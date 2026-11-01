// =============================================================================
// VulkanDevice.cs - yama引擎 Vulkan 渲染后端核心设备实现
// =============================================================================
//
// 本文件实现了完整的 Vulkan 渲染后端设备类 VulkanDevice，继承自 RHIDevice。
// 通过 Silk.NET.Vulkan 绑定层（内部使用 P/Invoke 调用 vulkan-1.dll）与
// Vulkan API 交互，实现跨平台的 GPU 渲染能力。
//
// 主要功能：
//   1. VkInstance 创建（含 Validation Layer 和 Debug Messenger）
//   2. 物理设备枚举与自动选择（离散 GPU 优先）
//   3. 队列族选择（图形 / 计算 / 传输 / 呈现）
//   4. VkDevice 逻辑设备创建（含所需扩展和特性）
//   5. VMA 风格的内存管理（块式子分配）
//   6. 描述符池与描述符集管理（Bindless）
//   7. 命令池管理（线程安全，每线程独立池）
//   8. Pipeline Cache（管线缓存加速创建）
//   9. Shader 编译（通过 shaderc P/Invoke 调用 shaderc_shared.dll）
//  10. 资源上传（Staging Buffer + Copy 命令）
//
// 作者: yama引擎团队
// =============================================================================

using Vulkan = Silk.NET.Vulkan;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;
using Semaphore = Silk.NET.Vulkan.Semaphore;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace YamaEngine.RHI.Vulkan
{
    // ════════════════════════════════════════════════════════════════════════════
    // 区域 1: Vulkan 原生函数 P/Invoke 声明
    // 直接通过 P/Invoke 调用 vulkan-1.dll 的核心函数。
    // Silk.NET 的 Vk.GetApi() 内部也使用相同的 P/Invoke 机制加载 vulkan-1.dll，
    // 此处声明用于在实例创建前查询可用的层和扩展。
    // ════════════════════════════════════════════════════════════════════════════

    internal static unsafe class VulkanNative
    {
        private const string VulkanLib = "vulkan-1.dll";

        /// <summary>获取实例级扩展属性列表</summary>
        [DllImport(VulkanLib, CallingConvention = CallingConvention.Winapi, EntryPoint = "vkEnumerateInstanceExtensionProperties")]
        public static extern Result EnumerateInstanceExtensionProperties(
            byte* pLayerName, uint* pPropertyCount, ExtensionProperties* pProperties);

        /// <summary>获取实例级验证层属性列表</summary>
        [DllImport(VulkanLib, CallingConvention = CallingConvention.Winapi, EntryPoint = "vkEnumerateInstanceLayerProperties")]
        public static extern Result EnumerateInstanceLayerProperties(
            uint* pPropertyCount, LayerProperties* pProperties);

        /// <summary>通过实例获取 Vulkan 函数指针</summary>
        [DllImport(VulkanLib, CallingConvention = CallingConvention.Winapi, EntryPoint = "vkGetInstanceProcAddr")]
        public static extern IntPtr GetInstanceProcAddr(Instance instance, byte* pName);

        /// <summary>获取设备级函数指针</summary>
        [DllImport(VulkanLib, CallingConvention = CallingConvention.Winapi, EntryPoint = "vkGetDeviceProcAddr")]
        public static extern IntPtr GetDeviceProcAddr(Device device, byte* pName);
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 2: Shader 编译器 (通过 shaderc P/Invoke)
    // 将 GLSL 源码编译为 SPIR-V 字节码，供 Vulkan 管线使用。
    // 需要将 shaderc_shared.dll 放在输出目录中。
    // ════════════════════════════════════════════════════════════════════════════

    internal sealed unsafe class VulkanShaderCompiler : IDisposable
    {
        // ── shaderc 原生函数声明 ──

        [DllImport("shaderc_shared.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr shaderc_compiler_initialize();

        [DllImport("shaderc_shared.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void shaderc_compiler_release(IntPtr compiler);

        [DllImport("shaderc_shared.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr shaderc_compile_options_initialize();

        [DllImport("shaderc_shared.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void shaderc_compile_options_release(IntPtr options);

        [DllImport("shaderc_shared.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void shaderc_compile_options_set_optimization_level(
            IntPtr options, int level);

        [DllImport("shaderc_shared.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void shaderc_compile_options_set_target_env(
            IntPtr options, int target, uint version);

        [DllImport("shaderc_shared.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void shaderc_compile_options_set_auto_bind_uniforms(
            IntPtr options, [MarshalAs(UnmanagedType.Bool)] bool auto_bind);

        // shaderc_compile_options_set_source_language: 设置输入着色器语言（GLSL/HLSL）。
        // Cel2D 等子系统使用 HLSL 源码，必须设为 HLSL，否则 shaderc 按 GLSL 解析会报错。
        [DllImport("shaderc_shared.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void shaderc_compile_options_set_source_language(
            IntPtr options, int language);

        [DllImport("shaderc_shared.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr shaderc_compile_into_spv(
            IntPtr compiler, byte* source_text, UIntPtr source_text_size,
            int shader_kind, byte* input_file_name, byte* entry_point_name,
            IntPtr additional_options);

        [DllImport("shaderc_shared.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int shaderc_result_get_compilation_status(IntPtr result);

        [DllImport("shaderc_shared.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern UIntPtr shaderc_result_get_length(IntPtr result);

        [DllImport("shaderc_shared.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern byte* shaderc_result_get_bytes(IntPtr result);

        [DllImport("shaderc_shared.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern byte* shaderc_result_get_error_message(IntPtr result);

        [DllImport("shaderc_shared.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void shaderc_result_release(IntPtr result);

        // ── shaderc 常量 ──

        // shaderc_shader_kind 枚举值
        private const int KindVertex = 0;
        private const int KindFragment = 1;
        private const int KindCompute = 2;
        private const int KindGeometry = 3;
        private const int KindTessControl = 4;
        private const int KindTessEval = 5;
        private const int KindMesh = 20;
        private const int KindTask = 21;

        // shaderc_optimization_level 枚举值
        private const int OptNone = 0;
        private const int OptSize = 1;
        private const int OptPerformance = 2;

        // shaderc_target_env 枚举值
        private const int TargetVulkan = 0;

        // shaderc_source_language 枚举值（GLSL=0, HLSL=1）
        private const int SourceGlsl = 0;
        private const int SourceHlsl = 1;

        // shaderc_compilation_status 枚举值
        private const int StatusSuccess = 0;
        private const int StatusCompilationError = 2;

        // ── 编译器状态 ──

        private IntPtr _compiler;
        private IntPtr _options;
        private bool _disposed;
        private bool _available;

        /// <summary>编译器是否可用（shaderc_shared.dll 是否成功加载）</summary>
        public bool IsAvailable => _available;

        public VulkanShaderCompiler()
        {
            try
            {
                _compiler = shaderc_compiler_initialize();
                _options = shaderc_compile_options_initialize();

                if (_compiler != IntPtr.Zero && _options != IntPtr.Zero)
                {
                    // 设置优化级别为性能优化
                    shaderc_compile_options_set_optimization_level(_options, OptPerformance);
                    // 目标环境为 Vulkan 1.3
                    shaderc_compile_options_set_target_env(_options, TargetVulkan,
                        (1u << 22) | (3u << 12));
                    // 自动绑定 uniform 变量
                    shaderc_compile_options_set_auto_bind_uniforms(_options, true);
                    // 输入语言设为 HLSL（Cel2D 等子系统的内联着色器使用 HLSL 语法）
                    shaderc_compile_options_set_source_language(_options, SourceHlsl);
                    _available = true;
                }
            }
            catch (DllNotFoundException)
            {
                // shaderc_shared.dll 未找到，编译器不可用
                _available = false;
            }
            catch (Exception)
            {
                _available = false;
            }
        }

        /// <summary>
        /// 将 GLSL 源码编译为 SPIR-V 字节码
        /// </summary>
        /// <param name="source">GLSL 源码字符串</param>
        /// <param name="entryPoint">入口函数名（通常为 "main"）</param>
        /// <param name="stage">Shader 阶段</param>
        /// <param name="fileName">用于错误提示的文件名</param>
        /// <returns>SPIR-V 字节码</returns>
        public byte[] Compile(string source, string entryPoint, RHIShaderStage stage, string fileName = "shader.glsl")
        {
            if (!_available)
                throw new InvalidOperationException(
                    "shaderc 编译器不可用。请确保 shaderc_shared.dll 在输出目录中。");

            int kind = StageToKind(stage);
            byte[] sourceBytes = Encoding.UTF8.GetBytes(source);
            byte[] fileBytes = VkHelper.StringToUtf8(fileName);
            byte[] entryBytes = VkHelper.StringToUtf8(entryPoint);

            IntPtr result = IntPtr.Zero;

            unsafe
            {
                fixed (byte* pSource = sourceBytes)
                fixed (byte* pFile = fileBytes)
                fixed (byte* pEntry = entryBytes)
                {
                    result = shaderc_compile_into_spv(
                        _compiler, pSource, (UIntPtr)sourceBytes.Length,
                        kind, pFile, pEntry, _options);
                }
            }

            if (result == IntPtr.Zero)
                throw new InvalidOperationException("shaderc 编译失败：结果为空");

            try
            {
                int status = shaderc_result_get_compilation_status(result);
                if (status != StatusSuccess)
                {
                    byte* errorMsg = shaderc_result_get_error_message(result);
                    string errorText = errorMsg != null ? VkHelper.PtrToString(errorMsg) : "未知错误";
                    throw new InvalidOperationException($"shaderc 编译错误: {errorText}");
                }

                // 获取编译结果
                UIntPtr length = shaderc_result_get_length(result);
                int spirvLength = (int)length.ToUInt64();
                byte[] spirv = new byte[spirvLength];

                unsafe
                {
                    byte* bytes = shaderc_result_get_bytes(result);
                    Marshal.Copy((IntPtr)bytes, spirv, 0, spirv.Length);
                }

                return spirv;
            }
            finally
            {
                shaderc_result_release(result);
            }
        }

        /// <summary>从文件读取 GLSL 源码并编译</summary>
        public byte[] CompileFromFile(string path, string entryPoint, RHIShaderStage stage)
        {
            string source = System.IO.File.ReadAllText(path);
            return Compile(source, entryPoint, stage, System.IO.Path.GetFileName(path));
        }

        /// <summary>将 RHI Shader 阶段映射到 shaderc shader kind</summary>
        private static int StageToKind(RHIShaderStage stage)
        {
            return stage switch
            {
                RHIShaderStage.Vertex => KindVertex,
                RHIShaderStage.Pixel => KindFragment,
                RHIShaderStage.Compute => KindCompute,
                RHIShaderStage.Geometry => KindGeometry,
                RHIShaderStage.Hull => KindTessControl,
                RHIShaderStage.Domain => KindTessEval,
                RHIShaderStage.Mesh => KindMesh,
                RHIShaderStage.Amplification => KindTask,
                _ => KindVertex
            };
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_options != IntPtr.Zero)
                shaderc_compile_options_release(_options);
            if (_compiler != IntPtr.Zero)
                shaderc_compiler_release(_compiler);

            _options = IntPtr.Zero;
            _compiler = IntPtr.Zero;
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 3: Debug Messenger 回调委托
    // 用于接收 Vulkan 验证层的调试消息
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Debug Messenger 回调委托类型
    /// 用于接收 Vulkan 验证层的调试消息
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal unsafe delegate uint DebugUtilsMessengerCallbackDelegate(
        DebugUtilsMessageSeverityFlagsEXT messageSeverity,
        DebugUtilsMessageTypeFlagsEXT messageTypes,
        DebugUtilsMessengerCallbackDataEXT* pCallbackData,
        void* pUserData);

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 4: VulkanDevice - Vulkan 渲染后端核心设备
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Vulkan 渲染后端核心设备类。
    /// 负责管理 VkInstance、VkPhysicalDevice、VkDevice 以及所有 RHI 资源。
    /// 通过 Silk.NET（内部 P/Invoke vulkan-1.dll）调用 Vulkan API。
    /// </summary>
    public sealed unsafe class VulkanDevice : RHIDevice
    {
        // ── Silk.NET Vulkan API 实例 ──
        // Vk.GetApi() 内部通过 P/Invoke 加载 vulkan-1.dll，并获取所有函数指针
        internal Vk Vk;

        // ── Vulkan 核心句柄 ──
        internal Instance VkInstance;
        internal PhysicalDevice VkPhysicalDevice;
        internal Device VkDevice;

        // ── Debug Messenger ──
        private ExtDebugUtils? _debugUtils;
        private DebugUtilsMessengerEXT _debugMessenger;
        // 保持委托引用防止 GC 回收（否则 native 回调会崩溃）
        private static readonly DebugUtilsMessengerCallbackDelegate _debugCallback = DebugCallback;

        // ── 物理设备属性 ──
        private PhysicalDeviceProperties _deviceProps;
        private PhysicalDeviceMemoryProperties _memProps;
        private PhysicalDeviceFeatures _deviceFeatures;

        // ── 队列族索引 ──
        internal uint GraphicsQueueFamily = uint.MaxValue;
        internal uint ComputeQueueFamily = uint.MaxValue;
        internal uint TransferQueueFamily = uint.MaxValue;
        internal uint PresentQueueFamily = uint.MaxValue;

        // ── 命令队列 ──
        private VulkanCommandQueue? _graphicsQueue;
        private VulkanCommandQueue? _computeQueue;
        private VulkanCommandQueue? _copyQueue;
        private readonly object _queueLock = new();

        // ── 管理器 ──
        internal VkMemoryAllocator? Allocator;
        internal VulkanDescriptorManager? DescriptorManager;
        private readonly Dictionary<RHIQueueType, VkCommandPoolManager> _poolManagers = new();

        // ── Pipeline Cache ──
        internal PipelineCache VkPipelineCache;

        // ── Shader 编译器 ──
        private VulkanShaderCompiler? _shaderCompiler;

        // ── 设备扩展支持标志 ──
        private bool _supportsMeshShaders;
        private bool _supportsRayTracing;
        private bool _supportsConditionalRendering;
        private bool _supportsBufferDeviceAddress;

        // ── 状态 ──
        private readonly bool _enableDebug;
        private bool _disposed;

        // ── 启用的实例扩展列表 ──
        private readonly List<string> _enabledInstanceExtensions = new();
        // ── 启用的验证层列表 ──
        private readonly List<string> _enabledLayers = new();
        // ── 启用的设备扩展列表 ──
        private readonly List<string> _enabledDeviceExtensions = new();
        // ── 物理设备实际支持的扩展集合（QueryDeviceExtensions 填充，用于过滤硬编码请求）──
        private readonly HashSet<string> _supportedDeviceExtensions = new();

        // ══════════════════════════════════════════════════════════════════════════
        // 构造函数
        // ══════════════════════════════════════════════════════════════════════════

        /// <summary>兼容重载：启用调试的设备构造（GPU 验证参数当前合并到调试层）。</summary>
        public VulkanDevice(bool enableDebug, bool enableGpuBasedValidation)
            : this(enableDebug || enableGpuBasedValidation)
        {
        }

        /// <summary>
        /// 创建 Vulkan 渲染设备
        /// </summary>
        /// <param name="enableDebug">是否启用 Validation Layer 和 Debug Messenger</param>
        public VulkanDevice(bool enableDebug)
        {
            _enableDebug = enableDebug;

            // 步骤 1: 加载 Vulkan API（通过 Silk.NET 内部 P/Invoke vulkan-1.dll）
            Vk = Vk.GetApi();

            // 步骤 2: 创建 VkInstance（含验证层和 Debug Messenger）
            CreateInstance();

            // 步骤 3: 设置 Debug Messenger（如果启用调试）
            if (_enableDebug)
                SetupDebugMessenger();

            // 步骤 4: 枚举物理设备并选择最佳设备
            SelectPhysicalDevice();

            // 步骤 5: 查找队列族（图形/计算/传输/呈现）
            FindQueueFamilies();

            // 步骤 6: 创建 VkDevice 逻辑设备
            CreateLogicalDevice();

            // 步骤 7: 获取命令队列
            RetrieveQueues();

            // 步骤 8: 初始化 VMA 风格内存分配器
            Allocator = new VkMemoryAllocator(Vk, VkDevice, VkPhysicalDevice);

            // 步骤 9: 初始化描述符管理器（Bindless 描述符）
            DescriptorManager = new VulkanDescriptorManager(this);

            // 步骤 10: 初始化命令池管理器（每种队列类型一个）
            InitializeCommandPoolManagers();

            // 步骤 11: 创建 Pipeline Cache（加速管线创建）
            CreatePipelineCache();

            // 步骤 12: 初始化 Shader 编译器
            _shaderCompiler = new VulkanShaderCompiler();
        }

        // ══════════════════════════════════════════════════════════════════════════
        // 实例创建
        // ══════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// 创建 Vulkan 实例 (VkInstance)
        /// 配置应用信息、验证层和实例扩展
        /// </summary>
        private void CreateInstance()
        {
            // 应用信息
            byte[] appName = VkHelper.StringToUtf8("YamaEngine");
            byte[] engineName = VkHelper.StringToUtf8("Yama");

            ApplicationInfo appInfo = new()
            {
                SType = StructureType.ApplicationInfo,
                PApplicationName = (byte*)Marshal.AllocHGlobal(appName.Length),
                ApplicationVersion = (1u << 22) | (0u << 12), // 1.0.0
                PEngineName = (byte*)Marshal.AllocHGlobal(engineName.Length),
                EngineVersion = (1u << 22) | (0u << 12), // 1.0.0
                ApiVersion = (1u << 22) | (3u << 12)      // Vulkan 1.3
            };

            try
            {
                Marshal.Copy(appName, 0, (IntPtr)appInfo.PApplicationName, appName.Length);
                Marshal.Copy(engineName, 0, (IntPtr)appInfo.PEngineName, engineName.Length);

                // ── 配置验证层 ──
                if (_enableDebug)
                {
                    string validationLayer = "VK_LAYER_KHRONOS_validation";
                    if (IsLayerSupported(validationLayer))
                    {
                        _enabledLayers.Add(validationLayer);
                    }
                }

                // ── 配置实例扩展 ──
                _enabledInstanceExtensions.Add("VK_EXT_debug_utils"); // Debug Messenger
                _enabledInstanceExtensions.Add("VK_KHR_surface");     // 跨平台表面

                // 平台特定表面扩展
                if (Environment.OSVersion.Platform == PlatformID.Win32NT)
                    _enabledInstanceExtensions.Add("VK_KHR_win32_surface"); // Win32 表面

                // 转换为 UTF8 字节数组
                byte[][] layerBytes = new byte[_enabledLayers.Count][];
                byte[][] extBytes = new byte[_enabledInstanceExtensions.Count][];
                GCHandle[] layerPins = new GCHandle[_enabledLayers.Count];
                GCHandle[] extPins = new GCHandle[_enabledInstanceExtensions.Count];

                for (int i = 0; i < _enabledLayers.Count; i++)
                {
                    layerBytes[i] = VkHelper.StringToUtf8(_enabledLayers[i]);
                    layerPins[i] = GCHandle.Alloc(layerBytes[i], GCHandleType.Pinned);
                }
                for (int i = 0; i < _enabledInstanceExtensions.Count; i++)
                {
                    extBytes[i] = VkHelper.StringToUtf8(_enabledInstanceExtensions[i]);
                    extPins[i] = GCHandle.Alloc(extBytes[i], GCHandleType.Pinned);
                }

                try
                {
                    // Debug Messenger 创建信息（链接到 InstanceCreateInfo.pNext，
                    // 使得实例创建和销毁期间也能收到验证层消息）
                    DebugUtilsMessengerCreateInfoEXT debugInfo = new()
                    {
                        SType = StructureType.DebugUtilsMessengerCreateInfoExt,
                        MessageSeverity = DebugUtilsMessageSeverityFlagsEXT.VerboseBitExt |
                                          DebugUtilsMessageSeverityFlagsEXT.WarningBitExt |
                                          DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt,
                        MessageType = DebugUtilsMessageTypeFlagsEXT.GeneralBitExt |
                                      DebugUtilsMessageTypeFlagsEXT.ValidationBitExt |
                                      DebugUtilsMessageTypeFlagsEXT.PerformanceBitExt,
                        PfnUserCallback = new PfnDebugUtilsMessengerCallbackEXT(
                            (delegate* unmanaged[Cdecl]<DebugUtilsMessageSeverityFlagsEXT, DebugUtilsMessageTypeFlagsEXT, DebugUtilsMessengerCallbackDataEXT*, void*, Silk.NET.Core.Bool32>)
                            Marshal.GetFunctionPointerForDelegate(_debugCallback))
                    };

                    // 构建层名称指针数组
                    byte** pLayers = null;
                    if (_enabledLayers.Count > 0)
                    {
                        pLayers = (byte**)Marshal.AllocHGlobal(IntPtr.Size * _enabledLayers.Count);
                        for (int i = 0; i < _enabledLayers.Count; i++)
                            pLayers[i] = (byte*)layerPins[i].AddrOfPinnedObject();
                    }

                    // 构建扩展名称指针数组
                    byte** pExts = (byte**)Marshal.AllocHGlobal(IntPtr.Size * _enabledInstanceExtensions.Count);
                    for (int i = 0; i < _enabledInstanceExtensions.Count; i++)
                        pExts[i] = (byte*)extPins[i].AddrOfPinnedObject();

                    try
                    {
                        InstanceCreateInfo createInfo = new()
                        {
                            SType = StructureType.InstanceCreateInfo,
                            PApplicationInfo = &appInfo,
                            EnabledLayerCount = (uint)_enabledLayers.Count,
                            PpEnabledLayerNames = (byte**)pLayers,
                            EnabledExtensionCount = (uint)_enabledInstanceExtensions.Count,
                            PpEnabledExtensionNames = (byte**)pExts,
                            PNext = _enableDebug ? &debugInfo : null
                        };

                        VkHelper.Check(
                            Vk.CreateInstance(&createInfo, null, out VkInstance),
                            "创建 VkInstance 失败");
                    }
                    finally
                    {
                        if (pLayers != null)
                            Marshal.FreeHGlobal((IntPtr)pLayers);
                        Marshal.FreeHGlobal((IntPtr)pExts);
                    }
                }
                finally
                {
                    for (int i = 0; i < layerPins.Length; i++) layerPins[i].Free();
                    for (int i = 0; i < extPins.Length; i++) extPins[i].Free();
                }
            }
            finally
            {
                Marshal.FreeHGlobal((IntPtr)appInfo.PApplicationName);
                Marshal.FreeHGlobal((IntPtr)appInfo.PEngineName);
            }
        }

        // ══════════════════════════════════════════════════════════════════════════
        // Debug Messenger
        // ══════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// 设置 Debug Messenger，用于接收 Vulkan 验证层的调试消息
        /// </summary>
        private void SetupDebugMessenger()
        {
            // 加载 VK_EXT_debug_utils 扩展
            if (!Vk.TryGetInstanceExtension(VkInstance, out _debugUtils))
            {
                // 扩展不可用，跳过 Debug Messenger 创建
                return;
            }

            DebugUtilsMessengerCreateInfoEXT createInfo = new()
            {
                SType = StructureType.DebugUtilsMessengerCreateInfoExt,
                MessageSeverity = DebugUtilsMessageSeverityFlagsEXT.VerboseBitExt |
                                  DebugUtilsMessageSeverityFlagsEXT.WarningBitExt |
                                  DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt,
                MessageType = DebugUtilsMessageTypeFlagsEXT.GeneralBitExt |
                              DebugUtilsMessageTypeFlagsEXT.ValidationBitExt |
                              DebugUtilsMessageTypeFlagsEXT.PerformanceBitExt,
                PfnUserCallback = new PfnDebugUtilsMessengerCallbackEXT(
                    (delegate* unmanaged[Cdecl]<DebugUtilsMessageSeverityFlagsEXT, DebugUtilsMessageTypeFlagsEXT, DebugUtilsMessengerCallbackDataEXT*, void*, Silk.NET.Core.Bool32>)
                    Marshal.GetFunctionPointerForDelegate(_debugCallback))
            };

            VkHelper.Check(
                _debugUtils.CreateDebugUtilsMessenger(VkInstance, &createInfo, null, out _debugMessenger),
                "创建 DebugUtilsMessenger 失败");
        }

        /// <summary>
        /// Debug Messenger 回调函数
        /// 接收 Vulkan 验证层的消息并输出到控制台
        /// </summary>
        private static uint DebugCallback(
            DebugUtilsMessageSeverityFlagsEXT messageSeverity,
            DebugUtilsMessageTypeFlagsEXT messageTypes,
            DebugUtilsMessengerCallbackDataEXT* pCallbackData,
            void* pUserData)
        {
            string message = VkHelper.PtrToString(pCallbackData->PMessage);

            // 根据严重级别选择日志级别
            if ((messageSeverity & DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt) != 0)
            {
                System.Diagnostics.Debug.WriteLine($"[Vulkan ERROR] {message}");
            }
            else if ((messageSeverity & DebugUtilsMessageSeverityFlagsEXT.WarningBitExt) != 0)
            {
                System.Diagnostics.Debug.WriteLine($"[Vulkan WARN]  {message}");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[Vulkan INFO]  {message}");
            }

            return 0; // VK_FALSE
        }

        // ══════════════════════════════════════════════════════════════════════════
        // 物理设备枚举与选择
        // ══════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// 枚举所有物理设备并选择最佳的 GPU
        /// 评分规则：离散 GPU > 集成 GPU > 其他
        /// </summary>
        private void SelectPhysicalDevice()
        {
            uint deviceCount = 0;
            Vk.EnumeratePhysicalDevices(VkInstance, &deviceCount, null);

            if (deviceCount == 0)
                throw new InvalidOperationException("未找到支持 Vulkan 的 GPU 设备");

            // 枚举所有物理设备
            PhysicalDevice[] devices = new PhysicalDevice[deviceCount];
            fixed (PhysicalDevice* pDevices = devices)
            {
                Vk.EnumeratePhysicalDevices(VkInstance, &deviceCount, pDevices);
            }

            // 评分选择最佳设备
            int bestScore = -1;
            for (int i = 0; i < deviceCount; i++)
            {
                Vk.GetPhysicalDeviceProperties(devices[i], out PhysicalDeviceProperties props);
                Vk.GetPhysicalDeviceFeatures(devices[i], out PhysicalDeviceFeatures features);

                int score = ScorePhysicalDevice(props, features);
                if (score > bestScore)
                {
                    bestScore = score;
                    VkPhysicalDevice = devices[i];
                    _deviceProps = props;
                    _deviceFeatures = features;
                }
            }

            if (VkPhysicalDevice.Handle == 0)
                throw new InvalidOperationException("未找到合适的 Vulkan GPU 设备");

            // 获取内存属性
            Vk.GetPhysicalDeviceMemoryProperties(VkPhysicalDevice, out _memProps);

            // 查询设备扩展支持情况
            QueryDeviceExtensions();
        }

        /// <summary>
        /// 对物理设备进行评分
        /// </summary>
        private static int ScorePhysicalDevice(PhysicalDeviceProperties props, PhysicalDeviceFeatures features)
        {
            int score = 0;

            // 离散 GPU 评分最高
            if (props.DeviceType == PhysicalDeviceType.DiscreteGpu)
                score += 1000;
            // 集成 GPU 次之
            else if (props.DeviceType == PhysicalDeviceType.IntegratedGpu)
                score += 500;

            // 最大 2D 纹理尺寸
            score += (int)(props.Limits.MaxImageDimension2D / 1024);

            // 支持几何着色器加分
            if (features.GeometryShader) score += 50;
            // 支持曲面细分加分
            if (features.TessellationShader) score += 50;

            return score;
        }

        /// <summary>
        /// 查询设备支持的扩展，设置支持标志
        /// </summary>
        private void QueryDeviceExtensions()
        {
            uint extCount = 0;
            Vk.EnumerateDeviceExtensionProperties(VkPhysicalDevice, (byte*)null, &extCount, null);

            ExtensionProperties[] exts = new ExtensionProperties[extCount];
            fixed (ExtensionProperties* pExts = exts)
            {
                Vk.EnumerateDeviceExtensionProperties(VkPhysicalDevice, (byte*)null, &extCount, pExts);

                // 检查关键扩展是否支持
                for (int i = 0; i < extCount; i++)
                {
                    string extName = VkHelper.PtrToString(pExts[i].ExtensionName);

                    // 记录物理设备实际支持的扩展名（CreateLogicalDevice 只启用这些，
                    // 避免请求已被核心化 / 驱动不再报告的扩展名导致 ErrorExtensionNotPresent）
                    if (!string.IsNullOrEmpty(extName))
                        _supportedDeviceExtensions.Add(extName);

                    if (extName == "VK_EXT_mesh_shader")
                        _supportsMeshShaders = true;
                    if (extName == "VK_KHR_ray_tracing_pipeline")
                        _supportsRayTracing = true;
                    if (extName == "VK_EXT_conditional_rendering")
                        _supportsConditionalRendering = true;
                    if (extName == "VK_KHR_buffer_device_address" || _deviceProps.ApiVersion >= ((1u << 22) | (2u << 12)))
                        _supportsBufferDeviceAddress = true;
                }
            }
        }

        // ══════════════════════════════════════════════════════════════════════════
        // 队列族查找
        // ══════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// 查找合适的队列族：图形、计算、传输、呈现
        /// 尽量使用独立的计算/传输队列以提高并行性
        /// </summary>
        private void FindQueueFamilies()
        {
            uint queueFamilyCount = 0;
            Vk.GetPhysicalDeviceQueueFamilyProperties(VkPhysicalDevice, &queueFamilyCount, null);

            QueueFamilyProperties[] queueFamilies = new QueueFamilyProperties[queueFamilyCount];
            fixed (QueueFamilyProperties* pFamilies = queueFamilies)
            {
                Vk.GetPhysicalDeviceQueueFamilyProperties(VkPhysicalDevice, &queueFamilyCount, pFamilies);
            }

            // 查找图形队列族（必须支持 GRAPHICS | COMPUTE）
            for (uint i = 0; i < queueFamilyCount; i++)
            {
                if ((queueFamilies[i].QueueFlags & QueueFlags.GraphicsBit) != 0)
                {
                    GraphicsQueueFamily = i;
                    break;
                }
            }

            if (GraphicsQueueFamily == uint.MaxValue)
                throw new InvalidOperationException("未找到支持图形操作的队列族");

            // 查找专用计算队列族（不含 GRAPHICS 位）
            for (uint i = 0; i < queueFamilyCount; i++)
            {
                if ((queueFamilies[i].QueueFlags & QueueFlags.ComputeBit) != 0 &&
                    (queueFamilies[i].QueueFlags & QueueFlags.GraphicsBit) == 0)
                {
                    ComputeQueueFamily = i;
                    break;
                }
            }
            // 如果没有专用计算队列，复用图形队列
            if (ComputeQueueFamily == uint.MaxValue)
                ComputeQueueFamily = GraphicsQueueFamily;

            // 查找专用传输队列族（不含 GRAPHICS 和 COMPUTE 位）
            for (uint i = 0; i < queueFamilyCount; i++)
            {
                if ((queueFamilies[i].QueueFlags & QueueFlags.TransferBit) != 0 &&
                    (queueFamilies[i].QueueFlags & QueueFlags.GraphicsBit) == 0 &&
                    (queueFamilies[i].QueueFlags & QueueFlags.ComputeBit) == 0)
                {
                    TransferQueueFamily = i;
                    break;
                }
            }
            // 如果没有专用传输队列，复用图形队列
            if (TransferQueueFamily == uint.MaxValue)
                TransferQueueFamily = GraphicsQueueFamily;

            // 呈现队列族：在 Windows 上检查 Win32 呈现支持
            PresentQueueFamily = GraphicsQueueFamily; // 通常图形队列支持呈现
        }

        // ══════════════════════════════════════════════════════════════════════════
        // 逻辑设备创建
        // ══════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// 创建 Vulkan 逻辑设备 (VkDevice)
        /// 配置所需扩展、队列和特性
        /// </summary>
        private void CreateLogicalDevice()
        {
            // 收集所需的设备扩展 —— 只启用物理设备实际支持的扩展名。
            // 说明: VK_KHR_dynamic_rendering / VK_KHR_timeline_semaphore / VK_KHR_buffer_device_address
            //       分别已在 Vulkan 1.3 / 1.2 / 1.2 被提升为核心，现代驱动可能不再报告这些扩展名，
            //       若硬编码请求会导致 vkCreateDevice 返回 ErrorExtensionNotPresent。
            //       核心版本功能由 PNext features 链（VkPhysicalDeviceVulkan12/13Features）启用，
            //       因此这里对已核心化的功能省略扩展名是安全的。
            void AddIfSupported(string ext)
            {
                if (_supportedDeviceExtensions.Contains(ext))
                    _enabledDeviceExtensions.Add(ext);
            }

            AddIfSupported("VK_KHR_swapchain");
            AddIfSupported("VK_KHR_dynamic_rendering");
            AddIfSupported("VK_KHR_timeline_semaphore");
            AddIfSupported("VK_KHR_buffer_device_address");
            if (_supportsConditionalRendering)
                AddIfSupported("VK_EXT_conditional_rendering");
            if (_supportsMeshShaders)
                AddIfSupported("VK_EXT_mesh_shader");

            // ── 队列创建信息 ──
            // 使用优先级区分不同队列族
            float graphicsPriority = 1.0f;
            float computePriority = 0.8f;
            float transferPriority = 0.6f;

            // 收集唯一队列族
            HashSet<uint> uniqueFamilies = new() { GraphicsQueueFamily, ComputeQueueFamily, TransferQueueFamily };
            var familyList = new List<uint>(uniqueFamilies);

            // 为每个队列族分配优先级（必须使用 pinned 数组，因为指针在循环外仍需有效）
            float[] queuePriorities = new float[familyList.Count];
            DeviceQueueCreateInfo[] queueCreateInfos = new DeviceQueueCreateInfo[familyList.Count];
            for (int i = 0; i < familyList.Count; i++)
            {
                queuePriorities[i] = familyList[i] == GraphicsQueueFamily ? graphicsPriority :
                                     familyList[i] == ComputeQueueFamily ? computePriority :
                                     transferPriority;
            }

            // ── 查询驱动实际支持的 base + vk1.2 + vk1.3 features ──
            // 说明: 硬编码请求驱动不支持的 feature 会导致 vkCreateDevice 返回
            //       ErrorFeatureNotPresent。这里用 vkGetPhysicalDeviceFeatures2 查询，
            //       只把驱动报告支持的 feature 置位后再启用。
            PhysicalDeviceVulkan13Features vk13Query = new()
            {
                SType = StructureType.PhysicalDeviceVulkan13Features
            };
            PhysicalDeviceVulkan12Features vk12Query = new()
            {
                SType = StructureType.PhysicalDeviceVulkan12Features,
                PNext = &vk13Query
            };
            PhysicalDeviceFeatures2 featuresQuery = new()
            {
                SType = StructureType.PhysicalDeviceFeatures2,
                PNext = &vk12Query
            };
            Vk.GetPhysicalDeviceFeatures2(VkPhysicalDevice, &featuresQuery);

            // ── 实际启用链：拷贝查询结果（仅含驱动支持的位），再按版本链入 ──
            PhysicalDeviceVulkan13Features vk13Features = vk13Query;
            vk13Features.PNext = null;
            PhysicalDeviceVulkan12Features vk12Features = vk12Query;
            vk12Features.PNext = null;
            PhysicalDeviceFeatures2 features2 = featuresQuery;
            features2.PNext = null;

            // 按设备实际 API 版本决定是否链入 1.2 / 1.3 features 链
            if (_deviceProps.ApiVersion >= Vk.MakeVersion(1, 3, 0))
            {
                vk12Features.PNext = &vk13Features;
                features2.PNext = &vk12Features;
            }
            else if (_deviceProps.ApiVersion >= Vk.MakeVersion(1, 2, 0))
            {
                vk12Features.PNext = null;
                features2.PNext = &vk12Features;
            }
            else
            {
                features2.PNext = null;
            }

            // ── 转换扩展名称为 UTF8 指针 ──
            byte[][] extBytes = new byte[_enabledDeviceExtensions.Count][];
            GCHandle[] extPins = new GCHandle[_enabledDeviceExtensions.Count];
            for (int i = 0; i < _enabledDeviceExtensions.Count; i++)
            {
                extBytes[i] = VkHelper.StringToUtf8(_enabledDeviceExtensions[i]);
                extPins[i] = GCHandle.Alloc(extBytes[i], GCHandleType.Pinned);
            }

            byte** pExts = (byte**)Marshal.AllocHGlobal(IntPtr.Size * _enabledDeviceExtensions.Count);
            for (int i = 0; i < _enabledDeviceExtensions.Count; i++)
                pExts[i] = (byte*)extPins[i].AddrOfPinnedObject();

            try
            {
                // 同时固定优先级数组和队列创建信息数组
                fixed (float* pPriorities = queuePriorities)
                fixed (DeviceQueueCreateInfo* pQueues = queueCreateInfos)
                {
                    // 在固定后填充队列创建信息（指针指向固定的优先级数组）
                    for (int i = 0; i < familyList.Count; i++)
                    {
                        pQueues[i] = new DeviceQueueCreateInfo
                        {
                            SType = StructureType.DeviceQueueCreateInfo,
                            QueueFamilyIndex = familyList[i],
                            QueueCount = 1,
                            PQueuePriorities = &pPriorities[i]
                        };
                    }

                    DeviceCreateInfo deviceInfo = new()
                    {
                        SType = StructureType.DeviceCreateInfo,
                        QueueCreateInfoCount = (uint)queueCreateInfos.Length,
                        PQueueCreateInfos = pQueues,
                        EnabledExtensionCount = (uint)_enabledDeviceExtensions.Count,
                        PpEnabledExtensionNames = (byte**)pExts,
                        PNext = &features2
                    };

                    VkHelper.Check(
                        Vk.CreateDevice(VkPhysicalDevice, &deviceInfo, null, out VkDevice),
                        "创建 VkDevice 失败");
                }
            }
            finally
            {
                Marshal.FreeHGlobal((IntPtr)pExts);
                for (int i = 0; i < extPins.Length; i++) extPins[i].Free();
            }
        }

        /// <summary>
        /// 从 VkDevice 获取命令队列句柄
        /// </summary>
        private void RetrieveQueues()
        {
            Vk.GetDeviceQueue(VkDevice, GraphicsQueueFamily, 0, out Queue graphicsQueue);
            _graphicsQueue = new VulkanCommandQueue(this, RHIQueueType.Graphics,
                GraphicsQueueFamily, graphicsQueue);

            if (ComputeQueueFamily != GraphicsQueueFamily)
            {
                Vk.GetDeviceQueue(VkDevice, ComputeQueueFamily, 0, out Queue computeQueue);
                _computeQueue = new VulkanCommandQueue(this, RHIQueueType.Compute,
                    ComputeQueueFamily, computeQueue);
            }
            else
            {
                _computeQueue = _graphicsQueue;
            }

            if (TransferQueueFamily != GraphicsQueueFamily && TransferQueueFamily != ComputeQueueFamily)
            {
                Vk.GetDeviceQueue(VkDevice, TransferQueueFamily, 0, out Queue transferQueue);
                _copyQueue = new VulkanCommandQueue(this, RHIQueueType.Copy,
                    TransferQueueFamily, transferQueue);
            }
            else
            {
                _copyQueue = _graphicsQueue;
            }
        }

        // ══════════════════════════════════════════════════════════════════════════
        // 命令池管理器初始化
        // ══════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// 为每种队列类型创建命令池管理器
        /// 命令池管理器为每个线程维护独立的命令池，实现线程安全
        /// </summary>
        private void InitializeCommandPoolManagers()
        {
            _poolManagers[RHIQueueType.Graphics] =
                new VkCommandPoolManager(this, GraphicsQueueFamily);
            _poolManagers[RHIQueueType.Compute] =
                new VkCommandPoolManager(this, ComputeQueueFamily);
            _poolManagers[RHIQueueType.Copy] =
                new VkCommandPoolManager(this, TransferQueueFamily);
        }

        /// <summary>
        /// 获取指定队列类型的命令池管理器
        /// </summary>
        internal VkCommandPoolManager GetCommandPoolManager(RHIQueueType type)
        {
            return _poolManagers[type];
        }

        // ══════════════════════════════════════════════════════════════════════════
        // Pipeline Cache
        // ══════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// 创建 Pipeline Cache，加速管线创建
        /// 可以将缓存数据保存到磁盘并在下次启动时加载
        /// </summary>
        private void CreatePipelineCache()
        {
            PipelineCacheCreateInfo cacheInfo = new()
            {
                SType = StructureType.PipelineCacheCreateInfo,
                InitialDataSize = 0,
                PInitialData = null,
                Flags = 0
            };

            VkHelper.Check(
                Vk.CreatePipelineCache(VkDevice, &cacheInfo, null, out VkPipelineCache),
                "创建 PipelineCache 失败");
        }

        /// <summary>
        /// 获取 Pipeline Cache 数据（可保存到磁盘用于下次启动加速）
        /// </summary>
        public byte[] GetPipelineCacheData()
        {
            nuint size = 0;
            Vk.GetPipelineCacheData(VkDevice, VkPipelineCache, &size, null);
            if (size == 0) return Array.Empty<byte>();

            byte[] data = new byte[(int)size];
            fixed (byte* pData = data)
            {
                Vk.GetPipelineCacheData(VkDevice, VkPipelineCache, &size, pData);
            }
            return data;
        }

        // ══════════════════════════════════════════════════════════════════════════
        // 辅助方法：检查层和扩展支持
        // ══════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// 检查指定的验证层是否可用
        /// 通过 P/Invoke 直接调用 vkEnumerateInstanceLayerProperties
        /// </summary>
        private bool IsLayerSupported(string layerName)
        {
            uint count = 0;
            VulkanNative.EnumerateInstanceLayerProperties(&count, null);
            if (count == 0) return false;

            LayerProperties[] layers = new LayerProperties[count];
            fixed (LayerProperties* pLayers = layers)
            {
                VulkanNative.EnumerateInstanceLayerProperties(&count, pLayers);

                for (int i = 0; i < count; i++)
                {
                    if (VkHelper.PtrToString(pLayers[i].LayerName) == layerName)
                        return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 检查指定的实例扩展是否可用
        /// 通过 P/Invoke 直接调用 vkEnumerateInstanceExtensionProperties
        /// </summary>
        private bool IsInstanceExtensionSupported(string extName)
        {
            uint count = 0;
            VulkanNative.EnumerateInstanceExtensionProperties(null, &count, null);
            if (count == 0) return false;

            ExtensionProperties[] exts = new ExtensionProperties[count];
            fixed (ExtensionProperties* pExts = exts)
            {
                VulkanNative.EnumerateInstanceExtensionProperties(null, &count, pExts);

                for (int i = 0; i < count; i++)
                {
                    if (VkHelper.PtrToString(pExts[i].ExtensionName) == extName)
                        return true;
                }
            }
            return false;
        }

        // ══════════════════════════════════════════════════════════════════════════
        // RHIDevice 属性实现
        // ══════════════════════════════════════════════════════════════════════════

        /// <summary>GPU 适配器名称</summary>
        public override unsafe string AdapterName
        {
            get
            {
                fixed (byte* pName = _deviceProps.DeviceName)
                    return VkHelper.PtrToString(pName);
            }
        }

        /// <summary>专用显存大小（字节）</summary>
        public override ulong DedicatedVideoMemory
        {
            get
            {
                ulong total = 0;
                for (int i = 0; i < (int)_memProps.MemoryHeapCount; i++)
                {
                    if ((_memProps.MemoryHeaps[i].Flags & MemoryHeapFlags.DeviceLocalBit) != 0)
                        total += _memProps.MemoryHeaps[i].Size;
                }
                return total;
            }
        }

        /// <summary>专用系统内存（Vulkan 中近似为 DEVICE_LOCAL 总量）</summary>
        public override ulong DedicatedSystemMemory => 0;

        /// <summary>共享系统内存（HOST_VISIBLE 总量）</summary>
        public override ulong SharedSystemMemory
        {
            get
            {
                ulong total = 0;
                for (int i = 0; i < (int)_memProps.MemoryHeapCount; i++)
                {
                    if ((_memProps.MemoryHeaps[i].Flags & MemoryHeapFlags.DeviceLocalBit) == 0)
                        total += _memProps.MemoryHeaps[i].Size;
                }
                return total;
            }
        }

        /// <summary>最大 Shader 模型版本</summary>
        public override RHIShaderModel MaxShaderModel =>
            _supportsMeshShaders ? RHIShaderModel.SM_6_6 : RHIShaderModel.SM_6_0;

        /// <summary>是否支持 Mesh Shader</summary>
        public override bool SupportsMeshShaders => _supportsMeshShaders;

        /// <summary>是否支持光线追踪</summary>
        public override bool SupportsRayTracing => _supportsRayTracing;

        /// <summary>是否支持可变速率着色 (VRS)</summary>
        public override bool SupportsVariableRateShading => false;

        /// <summary>是否支持采样器反馈</summary>
        public override bool SupportsSamplerFeedback => false;

        /// <summary>最大特性级别（Vulkan 中使用 API 版本字符串）</summary>
        public override string MaxFeatureLevel
        {
            get
            {
                uint major = _deviceProps.ApiVersion >> 22;
                uint minor = (_deviceProps.ApiVersion >> 12) & 0x3FF;
                return $"Vulkan {major}.{minor}";
            }
        }

        // ══════════════════════════════════════════════════════════════════════════
        // RHIDevice 方法实现: 命令队列
        // ══════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// 获取指定类型的命令队列
        /// </summary>
        public override RHICommandQueue GetCommandQueue(RHIQueueType type)
        {
            return type switch
            {
                RHIQueueType.Graphics => _graphicsQueue!,
                RHIQueueType.Compute => _computeQueue!,
                RHIQueueType.Copy => _copyQueue!,
                _ => _graphicsQueue!
            };
        }

        // ══════════════════════════════════════════════════════════════════════════
        // RHIDevice 方法实现: 资源创建
        // ══════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// 创建缓冲区
        /// </summary>
        public override RHIBuffer CreateBuffer(in RHIBufferDesc desc, string name = "")
        {
            var buffer = new VulkanBuffer(this, desc);
            buffer.Name = name;
            return buffer;
        }

        /// <summary>
        /// 创建纹理
        /// </summary>
        public override RHITexture CreateTexture(in RHITextureDesc desc, string name = "")
        {
            var texture = new VulkanTexture(this, desc);
            texture.Name = name;
            return texture;
        }

        /// <summary>
        /// 创建带初始数据的缓冲区（使用 Staging Buffer 上传）
        /// </summary>
        public override RHIBuffer CreateCommittedBuffer(in RHIBufferDesc desc, byte[] initialData, string name = "")
        {
            // 创建 GPU 本地缓冲区
            var localDesc = desc;
            localDesc.HeapType = RHIResourceHeapType.Default;
            var buffer = new VulkanBuffer(this, localDesc);
            buffer.Name = name;

            // 上传初始数据
            if (initialData != null && initialData.Length > 0)
            {
                UploadToBuffer(buffer, initialData, 0);
            }

            return buffer;
        }

        /// <summary>
        /// 创建上传缓冲区（CPU 可见，用于暂存数据）
        /// </summary>
        public override RHIBuffer CreateUploadBuffer(ulong sizeInBytes, string name = "")
        {
            var desc = new RHIBufferDesc
            {
                SizeInBytes = sizeInBytes,
                Format = RHIFormat.Unknown,
                HeapType = RHIResourceHeapType.Upload,
                Flags = RHIBufferFlags.None,
                Stride = 0
            };

            var buffer = new VulkanBuffer(this, desc);
            buffer.Name = name;
            return buffer;
        }

        // ══════════════════════════════════════════════════════════════════════════
        // RHIDevice 方法实现: 资源上传
        // ══════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// 上传数据到 GPU 缓冲区
        /// 使用 Staging Buffer + Copy 命令实现：
        /// 1. 创建 CPU 可见的 Staging Buffer
        /// 2. 将数据写入 Staging Buffer（通过内存映射）
        /// 3. 录制 Copy 命令并提交到图形队列
        /// 4. 等待 GPU 完成复制
        /// </summary>
        public override void UploadToBuffer<T>(RHIBuffer dest, ReadOnlySpan<T> data, ulong destOffset = 0)
        {
            ulong byteSize = (ulong)(data.Length * Marshal.SizeOf<T>());

            // 创建 Staging Buffer
            var stagingDesc = new RHIBufferDesc
            {
                SizeInBytes = byteSize,
                Format = RHIFormat.Unknown,
                HeapType = RHIResourceHeapType.Upload,
                Flags = RHIBufferFlags.None
            };
            var staging = new VulkanBuffer(this, stagingDesc);

            try
            {
                // 映射 Staging Buffer 并写入数据
                void* mapped = staging.GetMappedPointer();
                fixed (T* pData = data)
                {
                    System.Buffer.MemoryCopy(pData, mapped, byteSize, byteSize);
                }

                // 录制复制命令并执行
                var cmdList = new VulkanCommandList(this, RHIQueueType.Graphics, "UploadToBuffer");
                cmdList.Open();
                cmdList.CopyBufferRegion(dest, destOffset, staging, 0, byteSize);
                cmdList.Close();

                _graphicsQueue!.ExecuteCommandLists(new RHICommandList[] { cmdList });
                _graphicsQueue.Flush();

                cmdList.Dispose();
            }
            finally
            {
                staging.Dispose();
            }
        }

        /// <summary>
        /// 上传数据到 GPU 纹理
        /// 使用 Staging Buffer + CopyBufferToImage 命令实现
        /// </summary>
        public override void UploadToTexture(RHITexture dest, ReadOnlySpan<byte> data,
            int width, int height, int mipIndex = 0, int sliceIndex = 0)
        {
            var tex = (VulkanTexture)dest;

            // 计算数据大小（假设 4 字节每像素，实际应根据格式计算）
            int bpp = RHIFormatUtils.BytesPerPixel(tex.TextureDesc!.Value.Format);
            ulong rowPitch = (ulong)(width * bpp);
            ulong imageSize = rowPitch * (ulong)height;

            // 创建 Staging Buffer
            var stagingDesc = new RHIBufferDesc
            {
                SizeInBytes = imageSize,
                Format = RHIFormat.Unknown,
                HeapType = RHIResourceHeapType.Upload,
                Flags = RHIBufferFlags.None
            };
            var staging = new VulkanBuffer(this, stagingDesc);

            try
            {
                // 映射并写入数据
                void* mapped = staging.GetMappedPointer();
                fixed (byte* pData = data)
                {
                    System.Buffer.MemoryCopy(pData, mapped, imageSize, (ulong)data.Length);
                }

                // 录制命令
                var cmdList = new VulkanCommandList(this, RHIQueueType.Graphics, "UploadToTexture");
                cmdList.Open();

                // CopyTextureRegion 内部会自动转换纹理布局到 TransferDstOptimal
                // 复制 Buffer 到 Image
                ulong slicePitch = rowPitch * (ulong)height;
                cmdList.CopyTextureRegion(tex, 0, 0, 0, staging, 0,
                    (int)rowPitch, (int)slicePitch, width, height, 1);

                // 复制完成后转换纹理布局到 ShaderReadOnlyOptimal (PixelShaderResource)
                cmdList.ResourceBarrier(new RHIResourceBarrier(
                    tex, RHIResourceState.CopyDest, RHIResourceState.PixelShaderResource));

                cmdList.Close();
                _graphicsQueue!.ExecuteCommandLists(new RHICommandList[] { cmdList });
                _graphicsQueue.Flush();
                cmdList.Dispose();
            }
            finally
            {
                staging.Dispose();
            }
        }

        // ══════════════════════════════════════════════════════════════════════════
        // RHIDevice 方法实现: 描述符堆
        // ══════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// 创建描述符堆
        /// </summary>
        public override RHIDescriptorHeap CreateDescriptorHeap(RHIDescriptorHeapType type,
            int capacity, bool shaderVisible = true)
        {
            return new VulkanDescriptorHeap(this, type, capacity, shaderVisible);
        }

        // ══════════════════════════════════════════════════════════════════════════
        // RHIDevice 方法实现: 描述符创建
        // ══════════════════════════════════════════════════════════════════════════

        /// <summary>创建常量缓冲区视图 (CBV)</summary>
        public override RHIDescriptorHandle CreateCBV(RHIBuffer buffer, ulong offset, uint sizeInBytes)
        {
            var desc = new RHICBVDesc
            {
                BufferOffset = offset,
                SizeInBytes = sizeInBytes
            };
            return DescriptorManager!.CreateCBV((VulkanBuffer)buffer, desc);
        }

        /// <summary>创建着色器资源视图 (SRV) — 缓冲区</summary>
        public override RHIDescriptorHandle CreateSRV(RHIBuffer buffer, RHIFormat format,
            bool isStructured, uint firstElement, uint numElements, uint structureStride)
        {
            var desc = new RHISRVDesc
            {
                Format = format,
                Dimension = RHIResourceDimension.Buffer,
                FirstElement = firstElement,
                NumElements = numElements
            };
            return DescriptorManager!.CreateSRV(buffer, desc);
        }

        /// <summary>创建无序访问视图 (UAV) — 缓冲区</summary>
        public override RHIDescriptorHandle CreateUAV(RHIBuffer buffer, RHIFormat format,
            bool isStructured, uint firstElement, uint numElements, uint structureStride,
            RHIBuffer? counterBuffer = null, ulong counterOffset = 0)
        {
            var desc = new RHIUAVDesc
            {
                Format = format,
                Dimension = RHIResourceDimension.Buffer,
                FirstElement = firstElement,
                NumElements = numElements
            };
            return DescriptorManager!.CreateUAV(buffer, desc);
        }

        /// <summary>创建渲染目标视图 (RTV)</summary>
        public override RHIDescriptorHandle CreateRTV(RHITexture texture, uint mipSlice = 0, uint arraySlice = 0)
        {
            return DescriptorManager!.CreateRTV((VulkanTexture)texture, (int)mipSlice, (int)arraySlice);
        }

        /// <summary>创建深度模板视图 (DSV)</summary>
        public override RHIDescriptorHandle CreateDSV(RHITexture texture, uint mipSlice = 0, uint arraySlice = 0)
        {
            return DescriptorManager!.CreateDSV((VulkanTexture)texture, (int)mipSlice, (int)arraySlice);
        }

        // ── 纹理 SRV / UAV 便捷重载（覆盖 RHIDevice 虚方法）──

        /// <summary>创建 SRV — 纹理（便捷重载：指定 mip 范围）</summary>
        public override RHIDescriptorHandle CreateSRV(RHITexture texture, uint mostDetailedMip = 0, uint mipLevels = 0)
        {
            var desc = new RHISRVDesc
            {
                Format = RHIFormat.Unknown,
                Dimension = RHIResourceDimension.Texture2D,
                MostDetailedMip = mostDetailedMip,
                MipLevels = mipLevels
            };
            return DescriptorManager!.CreateSRV(texture, desc);
        }

        /// <summary>创建 UAV — 纹理（便捷重载：指定 mip 切片）</summary>
        public override RHIDescriptorHandle CreateUAV(RHITexture texture, uint mipSlice = 0)
        {
            var desc = new RHIUAVDesc
            {
                Format = RHIFormat.Unknown,
                Dimension = RHIResourceDimension.Texture2D,
                MipSlice = mipSlice
            };
            return DescriptorManager!.CreateUAV(texture, desc);
        }

        /// <summary>创建 SRV — 纹理（描述符结构重载）</summary>
        public override RHIDescriptorHandle CreateSRV(RHITexture texture, RHISRVDesc desc)
        {
            return DescriptorManager!.CreateSRV(texture, desc);
        }

        /// <summary>创建 UAV — 纹理（描述符结构重载）</summary>
        public override RHIDescriptorHandle CreateUAV(RHITexture texture, RHIUAVDesc desc)
        {
            return DescriptorManager!.CreateUAV(texture, desc);
        }

        /// <summary>
        /// 创建采样器
        /// 使用采样器缓存避免重复创建相同的采样器
        /// </summary>
        public override RHIDescriptorHandle CreateSampler(RHISamplerDesc desc)
        {
            return DescriptorManager!.SamplerCache.GetOrCreateSampler(desc);
        }

        // ══════════════════════════════════════════════════════════════════════════
        // RHIDevice 方法实现: Root Signature
        // ══════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// 创建 Root Signature (映射到 VkPipelineLayout + VkDescriptorSetLayout)
        /// </summary>
        public override RHIRootSignature CreateRootSignature(in RHIRootSignatureDesc desc, string name = "")
        {
            var rootSig = new VulkanRootSignature(this, desc);
            rootSig.Name = name;
            return rootSig;
        }

        // ══════════════════════════════════════════════════════════════════════════
        // RHIDevice 方法实现: Pipeline State (PSO)
        // ══════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// 创建图形管线状态对象 (Graphics PSO)
        /// 内部创建 VkPipeline，使用动态渲染 (VK_KHR_dynamic_rendering)
        /// </summary>
        public override RHIPipelineState CreateGraphicsPipelineState(in RHIGraphicsPipelineDesc desc, string name = "")
        {
            // 如果管线描述指定了根签名则直接使用，否则创建默认根签名
            VulkanRootSignature rootSig;
            if (desc.RootSignature is VulkanRootSignature existing)
            {
                rootSig = existing;
            }
            else
            {
                rootSig = new VulkanRootSignature(this, new RHIRootSignatureDesc
                {
                    Parameters = Array.Empty<RHIRootParameter>(),
                    StaticSamplers = desc.StaticSamplers ?? Array.Empty<RHISamplerDesc>(),
                    AllowInputAssembler = desc.InputLayout != null && desc.InputLayout.Length > 0
                });
            }

            var pso = new VulkanPipelineState(this, desc, rootSig);
            pso.Name = string.IsNullOrEmpty(name) ? desc.Name : name;
            return pso;
        }

        /// <summary>
        /// 创建计算管线状态对象 (Compute PSO)
        /// </summary>
        public override RHIPipelineState CreateComputePipelineState(in RHIComputePipelineDesc desc, string name = "")
        {
            var rootSig = desc.RootSignature is VulkanRootSignature existingRs
                ? existingRs
                : new VulkanRootSignature(this, new RHIRootSignatureDesc
                {
                    Parameters = Array.Empty<RHIRootParameter>(),
                    StaticSamplers = Array.Empty<RHISamplerDesc>()
                });

            var pso = new VulkanPipelineState(this, desc, rootSig);
            pso.Name = string.IsNullOrEmpty(name) ? desc.Name : name;
            return pso;
        }

        /// <summary>
        /// 创建 Mesh Shader 管线状态对象 (Mesh PSO)
        /// 需要 VK_EXT_mesh_shader 扩展支持
        /// </summary>
        public override RHIPipelineState CreateMeshPipelineState(in RHIMeshPipelineDesc desc, string name = "")
        {
            if (!_supportsMeshShaders)
                throw new NotSupportedException("当前 GPU 不支持 Mesh Shader (VK_EXT_mesh_shader)");

            var rootSig = desc.RootSignature is VulkanRootSignature existingRs
                ? existingRs
                : new VulkanRootSignature(this, new RHIRootSignatureDesc
                {
                    Parameters = Array.Empty<RHIRootParameter>(),
                    StaticSamplers = Array.Empty<RHISamplerDesc>()
                });

            var pso = new VulkanPipelineState(this, desc, rootSig);
            pso.Name = string.IsNullOrEmpty(name) ? desc.Name : name;
            return pso;
        }

        // ══════════════════════════════════════════════════════════════════════════
        // RHIDevice 方法实现: 命令列表
        // ══════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// 创建命令列表
        /// 从对应队列类型的命令池中分配命令缓冲区
        /// </summary>
        public override RHICommandList CreateCommandList(RHIQueueType type, string name = "")
        {
            return new VulkanCommandList(this, type, name);
        }

        // ══════════════════════════════════════════════════════════════════════════
        // RHIDevice 方法实现: Fence
        // ══════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// 创建 Fence（基于 Vulkan Timeline Semaphore 实现）
        /// </summary>
        public override RHIFence CreateFence(ulong initialValue, string name = "")
        {
            var fence = new VulkanFence(this, initialValue);
            fence.Name = name;
            return fence;
        }

        // ══════════════════════════════════════════════════════════════════════════
        // RHIDevice 方法实现: SwapChain
        // ══════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// 创建交换链 (VK_KHR_swapchain)
        /// </summary>
        public override RHISwapChain CreateSwapChain(IntPtr hwnd, int width, int height,
            RHIFormat format, int bufferCount, bool vsync, string name = "")
        {
            if (hwnd == IntPtr.Zero)
                throw new ArgumentException("未提供有效的窗口句柄 (hwnd)", nameof(hwnd));

            var desc = new RHISwapChainDesc
            {
                OutputWindow = hwnd,
                Width = width,
                Height = height,
                Format = format,
                BufferCount = bufferCount,
                VSync = vsync
            };
            var swapchain = new VulkanSwapChain(this, hwnd, desc);
            swapchain.Name = name;
            return swapchain;
        }

        // ══════════════════════════════════════════════════════════════════════════
        // RHIDevice 方法实现: Shader 编译
        // ══════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// 编译 GLSL Shader 为 SPIR-V 字节码
        /// 通过 shaderc (P/Invoke shaderc_shared.dll) 进行编译
        /// </summary>
        public override RHIShaderBytecode CompileShader(string hlslSource, string entryPoint,
            RHIShaderStage stage, RHIShaderModel shaderModel, string? sourceName = null, bool debug = false)
        {
            if (_shaderCompiler == null || !_shaderCompiler.IsAvailable)
                throw new InvalidOperationException(
                    "Shader 编译器不可用。请确保 shaderc_shared.dll 在输出目录中。");

            byte[] spirv = _shaderCompiler.Compile(hlslSource, entryPoint, stage);
            return new RHIShaderBytecode(spirv, entryPoint, stage, sourceName ?? "");
        }

        /// <summary>
        /// 从文件编译 GLSL Shader 为 SPIR-V 字节码
        /// </summary>
        public override RHIShaderBytecode CompileShaderFromFile(string filePath, string entryPoint,
            RHIShaderStage stage, RHIShaderModel shaderModel, bool debug = false)
        {
            if (_shaderCompiler == null || !_shaderCompiler.IsAvailable)
                throw new InvalidOperationException(
                    "Shader 编译器不可用。请确保 shaderc_shared.dll 在输出目录中。");

            byte[] spirv = _shaderCompiler.CompileFromFile(filePath, entryPoint, stage);
            return new RHIShaderBytecode(spirv, entryPoint, stage,
                System.IO.Path.GetFileNameWithoutExtension(filePath));
        }

        // ══════════════════════════════════════════════════════════════════════════
        // 等待设备空闲
        // ══════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// 等待 GPU 完成所有已提交的工作
        /// </summary>
        public void WaitIdle()
        {
            Vk.DeviceWaitIdle(VkDevice);
        }

        // ══════════════════════════════════════════════════════════════════════════
        // 释放资源
        // ══════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// 释放所有 Vulkan 资源
        /// 按照创建的逆序销毁：命令池 -> 描述符 -> 内存分配器 -> 管线缓存 -> 设备 -> 实例
        /// </summary>
        public override void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // 等待 GPU 完成所有工作
            Vk.DeviceWaitIdle(VkDevice);

            // 释放 Shader 编译器
            _shaderCompiler?.Dispose();

            // 释放命令池管理器
            foreach (var kvp in _poolManagers)
                kvp.Value.Dispose();
            _poolManagers.Clear();

            // 释放描述符管理器
            DescriptorManager?.Dispose();

            // 释放内存分配器
            Allocator?.Dispose();

            // 销毁 Pipeline Cache
            if (VkPipelineCache.Handle != 0)
                Vk.DestroyPipelineCache(VkDevice, VkPipelineCache, null);

            // 销毁命令队列（Vulkan 队列在设备销毁时自动释放，无需显式销毁）
            _graphicsQueue = null;
            _computeQueue = null;
            _copyQueue = null;

            // 销毁 Debug Messenger
            if (_debugUtils != null && _debugMessenger.Handle != 0)
                _debugUtils.DestroyDebugUtilsMessenger(VkInstance, _debugMessenger, null);

            // 销毁逻辑设备
            if (VkDevice.Handle != 0)
                Vk.DestroyDevice(VkDevice, null);

            // 销毁实例
            if (VkInstance.Handle != 0)
                Vk.DestroyInstance(VkInstance, null);

            // 释放 Silk.NET API
            Vk.Dispose();
        }
    }
}
