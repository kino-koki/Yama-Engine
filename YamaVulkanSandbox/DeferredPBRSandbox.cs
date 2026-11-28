// =============================================================================
// DeferredPBRSandbox.cs - 延迟渲染 + CSM + 后处理 端到端模板
//
// Pass 0: CSM shadow  (4 cascades, depth-only, push-constant driven)
// Pass 1: GBuffer MRT (5 RTV + depth)
// Pass 2: Lighting    (fullscreen, GGX + PCF shadow)
// Pass 3: PostProcess (ACES tonemap)
// Present: swapchain image
// =============================================================================

using System;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Core;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using Silk.NET.Windowing;
using Silk.NET.Vulkan.Extensions.EXT;
using Semaphore = Silk.NET.Vulkan.Semaphore;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace YamaVulkanSandbox;

internal sealed unsafe class DeferredPBRSandbox : IDisposable
{
    // ── Vulkan core ──
    private Vk? _vk;
    private Instance _instance;
    private PhysicalDevice _physicalDevice;
    private Device _device;
    private Queue _graphicsQueue;
    private uint _graphicsQueueFamily;
    private SurfaceKHR _surface;
    private SwapchainKHR _swapchain;
    private KhrSwapchain? _khrSwapchain;
    private KhrSurface? _khrSurface;
    private ExtDebugUtils? _extDebug;
    private DebugUtilsMessengerEXT _debugMessenger;

    private Image[] _swapchainImages = Array.Empty<Image>();
    private ImageView[] _swapchainImageViews = Array.Empty<ImageView>();
    private Format _swapchainFormat;
    private Extent2D _swapchainExtent;

    private const int MaxFramesInFlight = 2;
    private const int NumCascades = 4;
    private const uint ShadowMapSize = 1024;

    private int _currentFrame;
    private bool _disposed;
    private bool _initialized;

    private readonly IWindow _window;

    // ── Render passes ──
    private RenderPass _shadowRenderPass;
    private RenderPass _gbufferRenderPass;
    private RenderPass _lightingRenderPass;
    private RenderPass _bloomCompositeRP;   // HDR + bloom → LDR (full-res RGBA8)
    private RenderPass _bloomBrightRP;      // HDR → bloomBrightRT (half-res)
    private RenderPass _bloomBlurRP;        // bloomBlurH/bloomFinal (half-res)
    private RenderPass _fxaaRP;             // ldrRT → swapchain (full-res)

    // ── Pipeline layouts ──
    private PipelineLayout _shadowPipelineLayout;
    private PipelineLayout _gbufferPipelineLayout;
    private PipelineLayout _lightingPipelineLayout;
    private PipelineLayout _bloomCompositeLayout; // for _bloomCompositePipeline (uses _bloomCompositeSetLayout)
    private PipelineLayout _bloomBrightLayout;
    private PipelineLayout _bloomBlurLayout;
    private PipelineLayout _fxaaLayout;

    // ── Pipelines ──
    private Pipeline _shadowPipeline;
    private Pipeline _gbufferPipeline;
    private Pipeline _lightingPipeline;
    private Pipeline _bloomCompositePipeline;
    private Pipeline _bloomBrightPipeline;
    private Pipeline _bloomBlurPipeline;
    private Pipeline _fxaaPipeline;

    // ── Descriptor set layouts ──
    private DescriptorSetLayout _gbufferSetLayout;   // set0: b0 Camera UBO
    private DescriptorSetLayout[] _gbufferSetLayouts = new DescriptorSetLayout[1]; // set0 (+ set1 items if gpu-driven)
    private DescriptorSetLayout _itemsSetLayout;     // set1: b0 StorageBuffer (per-object data)
    private DescriptorSet _itemsSet;
    private DescriptorSetLayout _lightingSetLayout;  // set0: bindings 0..13
    private DescriptorSetLayout _bloomCompositeSetLayout; // set0: b0 exposure, t1 HDR, t3 bloom, s2 sampler
    private DescriptorSetLayout _bloomTexSetLayout;  // set0: t0 SampledImage, s1 Sampler(immutable linear)
    private DescriptorSetLayout _fxaaSetLayout;      // set0: b0 UBO, t1 SampledImage, s2 Sampler(immutable)

    private DescriptorPool _descriptorPool;
    private DescriptorSet _gbufferDescriptorSet;
    private DescriptorSet _lightingDescriptorSet;
    private DescriptorSet _bloomCompositeDescriptorSet;
    private DescriptorSet _bloomBrightDescSet;
    private DescriptorSet _bloomBlurHDescSet;
    private DescriptorSet _bloomBlurVDescSet;
    private DescriptorSet _fxaaDescSet;

    // ── Uniform buffers ──
    private Buffer _cameraUBO; private DeviceMemory _cameraUBOMem;    private byte* _cameraUBOPtr;
    private Buffer _lightUBO;  private DeviceMemory _lightUBOMem;     private byte* _lightUBOPtr;
    private Buffer _resUBO;    private DeviceMemory _resUBOMem;       private byte* _resUBOPtr;
    private Buffer _exposureUBO; private DeviceMemory _exposureUBOMem; private byte* _exposureUBOPtr;

    // ── GBuffer ──
    private Image[] _gbufferImages = new Image[5];
    private DeviceMemory[] _gbufferMems = new DeviceMemory[5];
    private ImageView[] _gbufferViews = new ImageView[5];
    private Image _gbufferDepth; private DeviceMemory _gbufferDepthMem; private ImageView _gbufferDepthView;
    private Framebuffer _gbufferFramebuffer;

    // ── HDR color (lighting output) ──
    private Image _hdrColor; private DeviceMemory _hdrColorMem; private ImageView _hdrColorView;

    // ── Bloom chain (half-res) ──
    private Image _bloomBright; private DeviceMemory _bloomBrightMem; private ImageView _bloomBrightView;
    private Image _bloomBlurH;  private DeviceMemory _bloomBlurHMem;  private ImageView _bloomBlurHView;
    private Image _bloomFinal;  private DeviceMemory _bloomFinalMem;  private ImageView _bloomFinalView;
    // LDR intermediate (full-res, FXAA source)
    private Image _ldrRT;       private DeviceMemory _ldrRTMem;       private ImageView _ldrRTView;

    // ── FXAA constant buffer ──
    private Buffer _fxaaUBO; private DeviceMemory _fxaaUBOMem; private byte* _fxaaUBOPtr;

    // ── Shadow cascades ──
    private Image[] _shadowImages = new Image[NumCascades];
    private DeviceMemory[] _shadowMems = new DeviceMemory[NumCascades];
    private ImageView[] _shadowViews = new ImageView[NumCascades];
    private Framebuffer[] _shadowFramebuffers = new Framebuffer[NumCascades];
    private Sampler _shadowSamplerCmp;
    private Sampler _linearSampler;
    private Sampler[] _lightingImmutableSamplers = new Sampler[2];
    private Sampler[] _bloomCompositeImmutableSamplers = new Sampler[1];
    private Sampler[] _fxaaImmutableSamplers = new Sampler[1];
    private PushConstantRange[] _shadowPCRanges = new PushConstantRange[1];
    private PushConstantRange[] _gbufferPCRanges = new PushConstantRange[1];

    // ── Lighting + bloom/fxaa framebuffers per swapchain image ──
    private Framebuffer[] _lightingFramebuffers = Array.Empty<Framebuffer>();
    private Framebuffer _bloomBrightFB;        // half-res
    private Framebuffer _bloomBlurHFB;         // half-res
    private Framebuffer _bloomFinalFB;         // half-res
    private Framebuffer _bloomCompositeFB;     // full-res ldrRT
    private Framebuffer[] _fxaaFramebuffers = Array.Empty<Framebuffer>(); // per swapchain image

    // ── Command ──
    private CommandPool _commandPool;
    private CommandBuffer[] _commandBuffers = Array.Empty<CommandBuffer>();

    // ── Mesh ──
    private Buffer _vertexBuffer; private DeviceMemory _vertexBufferMem;
    private Buffer _indexBuffer;  private DeviceMemory _indexBufferMem;
    private MeshDraw[] _meshDraws = Array.Empty<MeshDraw>();

    // ── Sync ──
    private Semaphore[] _imageAvailableSemaphores = Array.Empty<Semaphore>();
    private Semaphore[] _renderFinishedSemaphores = Array.Empty<Semaphore>();
    private Fence[] _inFlightFences = Array.Empty<Fence>();

    // ── Scene state ──
    private float _time;
    private Vector3 _cameraTarget = new(0f, 1f, 0f);
    private Vector3 _cameraPos = new(0f, 6f, 12f);
    private readonly Vector3 _lightDir = Vector3.Normalize(new Vector3(-0.4f, -1.0f, -0.3f));

    private struct MeshDraw
    {
        public uint IndexOffset;
        public uint IndexCount;
        public Matrix4x4 World;
        public Vector4 Color;
        public Vector4 PBR;
    }

    // ── GPU-driven culling ──
    private bool _gpuDriven = true;
    private Matrix4x4 _lastViewProj = Matrix4x4.Identity;
    private int _frameCounter;

    // 与 FrustumCull.comp.hlsl / DeferredPBR.vert|frag.hlsl 的 RenderItem 严格一致（112 字节）
    [StructLayout(LayoutKind.Sequential, Size = 112)]
    private struct RenderItem
    {
        public Matrix4x4 World;   // 0-63
        public Vector4 Color;     // 64-79  xyz=albedo, w=metallic
        public Vector4 PBR;       // 80-95  x=roughness, y=ao, z=emissiveStrength
        public uint IndexCount;   // 96
        public uint FirstIndex;   // 100
        public uint BaseVertex;   // 104
        public uint Pad;          // 108
    }

    // VkDrawIndexedIndirectCommand（字段顺序不可变，20 字节）
    [StructLayout(LayoutKind.Sequential, Size = 20)]
    private struct GpuIndirectCommand
    {
        public uint VertexCount;    // indexCount
        public uint InstanceCount;  // 1 = visible, 0 = culled
        public uint FirstIndex;     // firstIndex
        public int VertexOffset;    // baseVertex
        public uint FirstInstance;  // baseInstance = itemIndex + 1
    }

    // Push constant block for the cull compute shader: 6 vec4 planes + itemCount + pad3 = 112 bytes
    [StructLayout(LayoutKind.Sequential, Size = 112)]
    private struct CullPush
    {
        public Vector4 P0;
        public Vector4 P1;
        public Vector4 P2;
        public Vector4 P3;
        public Vector4 P4;
        public Vector4 P5;
        public uint ItemCount;
        // trailing 12 bytes are zero-initialized by Size=112
    }

    // ── GPU-driven resources ──
    private RenderItem[] _renderItems = Array.Empty<RenderItem>();
    private Buffer _renderItemsSSBO;    private DeviceMemory _renderItemsSSBOMem;     // b1: compute 读 / VS-FS 读（初始化时一次性上传）
    private Buffer _indirectCmdsBuffer; private DeviceMemory _indirectCmdsBufferMem;  // b2: compute 写 / CmdDrawIndexedIndirect 读
    private Buffer _visibleCountBuffer; private DeviceMemory _visibleCountBufferMem;  // b3: compute 写 / CPU 读日志
    private byte* _visibleCountPtr;
    private ShaderModule _cullComp;
    private PipelineLayout _cullLayout;
    private Pipeline _cullPipeline;
    private DescriptorSetLayout _cullSetLayout;
    private DescriptorSet _cullSet;

    public DeferredPBRSandbox(IWindow window) { _window = window; }

    // ════════════════════════════════════════════════════════════════
    // 公共入口
    // ════════════════════════════════════════════════════════════════
    public void Initialize()
    {
        CreateInstance();
        CreateSurface();
        PickPhysicalDevice();
        CreateLogicalDevice();
        QueryMemoryProps();
        CreateSwapchain();
        CreateImageViews();
        CreateSamplers();
        CreateCommandPool();
        CreateUniformBuffers();
        BuildScene();
        CreateShadowResources();
        CreateGBufferResources();
        CreateHDRResources();
        CreateLightingResources();
        CreatePostResources();
        CreatePostProcessImages();
        CreateDescriptorPoolAndSets();
        // Create items SSBO set layout before pipelines (needed by GBuffer pipeline layout)
        if (_gpuDriven) CreateItemsSetLayout();
        CreatePipelines();
        if (_gpuDriven) CreateGpuDrivenResources();
        CreateFramebuffers();
        CreateCommandBuffers();
        CreateSyncObjects();
        _initialized = true;
        Console.WriteLine("[DeferredPBRSandbox] Vulkan 延迟渲染管线初始化完成");
    }

    public void RenderFrame()
    {
        if (!_initialized) return;

        _vk!.WaitForFences(_device, 1, in _inFlightFences[_currentFrame], true, ulong.MaxValue);
        _vk.ResetFences(_device, 1, in _inFlightFences[_currentFrame]);

        // GPU 写入的可见计数（滞后 1-2 帧，仅用于日志验证剔除生效）
        if (_gpuDriven && _frameCounter % 60 == 0)
        {
            uint visible = *(uint*)_visibleCountPtr;
            Console.WriteLine($"[GPUDriven] 可见物体 {visible}/{_meshDraws.Length}");
        }

        uint imageIndex = 0;
        Result result = _khrSwapchain!.AcquireNextImage(
            _device, _swapchain, ulong.MaxValue,
            _imageAvailableSemaphores[_currentFrame], default, ref imageIndex);
        if (result != Result.Success && result != Result.SuboptimalKhr)
        {
            Console.WriteLine($"[WARN] AcquireNextImage 返回 {result}");
            return;
        }

        UpdateUniforms();
        BuildCommandBuffer((int)imageIndex);

        var waitSemaphores = new Semaphore[] { _imageAvailableSemaphores[_currentFrame] };
        var waitStages = new PipelineStageFlags[] { PipelineStageFlags.ColorAttachmentOutputBit };
        var cmdBuffer = _commandBuffers[imageIndex];
        var signalSemaphores = new Semaphore[] { _renderFinishedSemaphores[imageIndex] };

        fixed (Semaphore* pWaitSem = waitSemaphores)
        fixed (PipelineStageFlags* pWaitStage = waitStages)
        fixed (Semaphore* pSignalSem = signalSemaphores)
        {
            SubmitInfo submitInfo = new()
            {
                SType = StructureType.SubmitInfo,
                WaitSemaphoreCount = 1,
                PWaitSemaphores = pWaitSem,
                PWaitDstStageMask = pWaitStage,
                CommandBufferCount = 1,
                PCommandBuffers = &cmdBuffer,
                SignalSemaphoreCount = 1,
                PSignalSemaphores = pSignalSem
            };
            _vk.QueueSubmit(_graphicsQueue, 1, in submitInfo, _inFlightFences[_currentFrame]);
        }

        var swapchains = new SwapchainKHR[] { _swapchain };
        fixed (SwapchainKHR* pSwap = swapchains)
        fixed (Semaphore* pSignalSem2 = signalSemaphores)
        {
            PresentInfoKHR presentInfo = new()
            {
                SType = StructureType.PresentInfoKhr,
                WaitSemaphoreCount = 1,
                PWaitSemaphores = pSignalSem2,
                SwapchainCount = 1,
                PSwapchains = pSwap,
                PImageIndices = &imageIndex
            };
            var presentResult = _khrSwapchain.QueuePresent(_graphicsQueue, in presentInfo);
            if (presentResult != Result.Success && presentResult != Result.SuboptimalKhr)
                Console.WriteLine($"[WARN] QueuePresent 返回 {presentResult}");
        }

        _time += 1.0f / 60.0f;
        _frameCounter++;
        _currentFrame = (_currentFrame + 1) % MaxFramesInFlight;
    }

    // ════════════════════════════════════════════════════════════════
    // 基础 Vulkan 初始化（镜像 VulkanTriangleRenderer 模式）
    // ════════════════════════════════════════════════════════════════
    private void CreateInstance()
    {
        _vk = Vk.GetApi();

        uint layerCount = 0;
        _vk.EnumerateInstanceLayerProperties(&layerCount, null);
        var layers = new LayerProperties[layerCount];
        fixed (LayerProperties* layersPtr = layers)
            _vk.EnumerateInstanceLayerProperties(&layerCount, layersPtr);

        bool hasValidation = false;
        foreach (var l in layers)
        {
            string name = SilkMarshal.PtrToString((nint)l.LayerName, NativeStringEncoding.UTF8)!;
            if (name == "VK_LAYER_KHRONOS_validation") { hasValidation = true; break; }
        }
        if (hasValidation)
            Console.WriteLine("[DeferredPBRSandbox] 发现 VK_LAYER_KHRONOS_validation，启用验证层");

        ApplicationInfo appInfo = new()
        {
            SType = StructureType.ApplicationInfo,
            PApplicationName = (byte*)SilkMarshal.StringToPtr("Yama Deferred PBR Sandbox", NativeStringEncoding.UTF8),
            ApplicationVersion = Vk.MakeVersion(1, 0, 0),
            PEngineName = (byte*)SilkMarshal.StringToPtr("YamaEngine", NativeStringEncoding.UTF8),
            EngineVersion = Vk.MakeVersion(1, 0, 0),
            ApiVersion = Vk.MakeVersion(1, 4, 0)
        };

        var windowExtensions = _window.VkSurface!.GetRequiredExtensions(out uint extCount);
        int extraExt = hasValidation ? 1 : 0;
        Span<IntPtr> extensions = new IntPtr[extCount + extraExt];
        for (uint i = 0; i < extCount; i++)
            extensions[(int)i] = (nint)windowExtensions[(int)i];
        if (hasValidation)
            extensions[(int)extCount] = (nint)SilkMarshal.StringToPtr("VK_EXT_debug_utils", NativeStringEncoding.UTF8);

        byte* layerName = hasValidation
            ? (byte*)SilkMarshal.StringToPtr("VK_LAYER_KHRONOS_validation", NativeStringEncoding.UTF8)
            : null;

        InstanceCreateInfo createInfo = new()
        {
            SType = StructureType.InstanceCreateInfo,
            PApplicationInfo = &appInfo,
            EnabledExtensionCount = (uint)extensions.Length,
            PpEnabledExtensionNames = (byte**)Marshal.AllocHGlobal(extensions.Length * sizeof(nint)),
            EnabledLayerCount = hasValidation ? 1u : 0u,
            PpEnabledLayerNames = hasValidation ? &layerName : null
        };

        for (int i = 0; i < extensions.Length; i++)
            ((byte**)createInfo.PpEnabledExtensionNames)[i] = (byte*)extensions[i];

        fixed (Instance* instancePtr = &_instance)
        {
            if (_vk.CreateInstance(&createInfo, null, instancePtr) != Result.Success)
                throw new Exception("无法创建 Vulkan Instance");
        }

        if (hasValidation)
        {
            if (_vk.TryGetInstanceExtension(_instance, out ExtDebugUtils extDebug))
            {
                DebugUtilsMessengerCreateInfoEXT dbgInfo = new()
                {
                    SType = StructureType.DebugUtilsMessengerCreateInfoExt,
                    MessageSeverity = DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt
                                    | DebugUtilsMessageSeverityFlagsEXT.WarningBitExt,
                    MessageType = DebugUtilsMessageTypeFlagsEXT.GeneralBitExt
                                | DebugUtilsMessageTypeFlagsEXT.ValidationBitExt
                                | DebugUtilsMessageTypeFlagsEXT.PerformanceBitExt,
                    PfnUserCallback = (DebugUtilsMessengerCallbackFunctionEXT)((messageSeverity, _, pCallbackData, _) =>
                    {
                        string msg = SilkMarshal.PtrToString((nint)pCallbackData->PMessage, NativeStringEncoding.UTF8) ?? "";
                        bool isError = (messageSeverity & DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt) != 0;
                        Console.WriteLine(isError ? $"\n[VULKAN ERROR] {msg}\n" : $"\n[VULKAN WARN] {msg}\n");
                        return 0u;
                    })
                };
                _debugMessenger = default;
                fixed (DebugUtilsMessengerEXT* msgrPtr = &_debugMessenger)
                    extDebug.CreateDebugUtilsMessenger(_instance, &dbgInfo, null, msgrPtr);
                _extDebug = extDebug;
            }
        }

        SilkMarshal.Free((nint)appInfo.PApplicationName);
        SilkMarshal.Free((nint)appInfo.PEngineName);
        for (int i = 0; i < extensions.Length; i++)
            SilkMarshal.Free(extensions[i]);
        Marshal.FreeHGlobal((nint)createInfo.PpEnabledExtensionNames);
        if (hasValidation)
            SilkMarshal.Free((nint)layerName);
    }

    private void CreateSurface()
    {
        if (!_vk!.TryGetInstanceExtension(_instance, out _khrSurface))
            throw new Exception("VK_KHR_surface 扩展不可用");
        _surface = _window.VkSurface!.Create<AllocationCallbacks>(_instance.ToHandle(), null).ToSurface();
    }

    private void PickPhysicalDevice()
    {
        uint deviceCount = 0;
        _vk!.EnumeratePhysicalDevices(_instance, &deviceCount, null);
        if (deviceCount == 0) throw new Exception("未找到支持 Vulkan 的 GPU");

        var devices = new PhysicalDevice[deviceCount];
        fixed (PhysicalDevice* devicesPtr = devices)
            _vk.EnumeratePhysicalDevices(_instance, &deviceCount, devicesPtr);

        foreach (var device in devices)
        {
            uint queueFamilyCount = 0;
            _vk.GetPhysicalDeviceQueueFamilyProperties(device, &queueFamilyCount, null);
            var queueFamilies = new QueueFamilyProperties[queueFamilyCount];
            fixed (QueueFamilyProperties* qfPtr = queueFamilies)
                _vk.GetPhysicalDeviceQueueFamilyProperties(device, &queueFamilyCount, qfPtr);

            for (uint i = 0; i < queueFamilyCount; i++)
            {
                if ((queueFamilies[i].QueueFlags & QueueFlags.GraphicsBit) != 0)
                {
                    _khrSurface!.GetPhysicalDeviceSurfaceSupport(device, i, _surface, out Bool32 presentSupport);
                    if (presentSupport)
                    {
                        _physicalDevice = device;
                        _graphicsQueueFamily = i;
                        PhysicalDeviceProperties props;
                        _vk.GetPhysicalDeviceProperties(device, &props);
                        string deviceName = SilkMarshal.PtrToString((nint)props.DeviceName, NativeStringEncoding.UTF8)!;
                        Console.WriteLine($"[DeferredPBRSandbox] 选中 GPU: {deviceName}");
                        return;
                    }
                }
            }
        }
        throw new Exception("未找到支持图形+呈现队列的 GPU");
    }

    private void CreateLogicalDevice()
    {
        float queuePriority = 1.0f;
        DeviceQueueCreateInfo queueCreateInfo = new()
        {
            SType = StructureType.DeviceQueueCreateInfo,
            QueueFamilyIndex = _graphicsQueueFamily,
            QueueCount = 1,
            PQueuePriorities = &queuePriority
        };

        byte* swapchainExtName = (byte*)SilkMarshal.StringToPtr(KhrSwapchain.ExtensionName, NativeStringEncoding.UTF8);
        byte** extNames = stackalloc byte*[1];
        extNames[0] = swapchainExtName;

        // Features: multiDrawIndirect（core，GPU-driven CmdDrawIndexedIndirect 多命令绘制）
        // + drawIndirectCount（Vulkan 1.2）。先查询设备支持再启用，避免启用不支持的 feature。
        PhysicalDeviceVulkan12Features vk12Features = new()
        {
            SType = StructureType.PhysicalDeviceVulkan12Features
        };
        PhysicalDeviceFeatures2 feat2 = new()
        {
            SType = StructureType.PhysicalDeviceFeatures2,
            PNext = &vk12Features
        };
        _vk!.GetPhysicalDeviceFeatures2(_physicalDevice, &feat2);
        if (!feat2.Features.MultiDrawIndirect || !vk12Features.DrawIndirectCount)
            throw new Exception("设备不支持 multiDrawIndirect / drawIndirectCount");
        feat2.Features.MultiDrawIndirect = true;
        vk12Features.DrawIndirectCount = true;

        DeviceCreateInfo deviceInfo = new()
        {
            SType = StructureType.DeviceCreateInfo,
            PNext = &feat2,
            QueueCreateInfoCount = 1,
            PQueueCreateInfos = &queueCreateInfo,
            EnabledExtensionCount = 1,
            PpEnabledExtensionNames = extNames
        };

        fixed (Device* devicePtr = &_device)
        {
            if (_vk!.CreateDevice(_physicalDevice, &deviceInfo, null, devicePtr) != Result.Success)
                throw new Exception("无法创建 Vulkan 逻辑设备");
        }
        _vk.GetDeviceQueue(_device, _graphicsQueueFamily, 0, out _graphicsQueue);
        SilkMarshal.Free((nint)swapchainExtName);
    }

    private void CreateSwapchain()
    {
        if (!_vk!.TryGetDeviceExtension(_instance, _device, out _khrSwapchain))
            throw new Exception("VK_KHR_swapchain 扩展不可用");

        _khrSurface!.GetPhysicalDeviceSurfaceCapabilities(_physicalDevice, _surface, out SurfaceCapabilitiesKHR capabilities);

        uint formatCount = 0;
        _khrSurface.GetPhysicalDeviceSurfaceFormats(_physicalDevice, _surface, &formatCount, null);
        var formats = new SurfaceFormatKHR[formatCount];
        fixed (SurfaceFormatKHR* formatsPtr = formats)
            _khrSurface.GetPhysicalDeviceSurfaceFormats(_physicalDevice, _surface, &formatCount, formatsPtr);

        _swapchainFormat = formats[0].Format;
        foreach (var f in formats)
        {
            if (f.Format == Format.B8G8R8A8Srgb || f.Format == Format.R8G8B8A8Srgb)
            {
                _swapchainFormat = f.Format;
                break;
            }
        }

        _swapchainExtent = capabilities.CurrentExtent;
        if (_swapchainExtent.Width == 0 || _swapchainExtent.Height == 0)
            _swapchainExtent = new Extent2D((uint)_window.Size.X, (uint)_window.Size.Y);

        uint imageCount = capabilities.MinImageCount + 1;
        if (capabilities.MaxImageCount > 0 && imageCount > capabilities.MaxImageCount)
            imageCount = capabilities.MaxImageCount;

        SwapchainCreateInfoKHR createInfo = new()
        {
            SType = StructureType.SwapchainCreateInfoKhr,
            Surface = _surface,
            MinImageCount = imageCount,
            ImageFormat = _swapchainFormat,
            ImageColorSpace = formats[0].ColorSpace,
            ImageExtent = _swapchainExtent,
            ImageArrayLayers = 1,
            ImageUsage = ImageUsageFlags.ColorAttachmentBit,
            ImageSharingMode = SharingMode.Exclusive,
            PreTransform = capabilities.CurrentTransform,
            CompositeAlpha = CompositeAlphaFlagsKHR.OpaqueBitKhr,
            PresentMode = PresentModeKHR.FifoKhr,
            Clipped = true
        };

        fixed (SwapchainKHR* swapchainPtr = &_swapchain)
        {
            if (_khrSwapchain!.CreateSwapchain(_device, &createInfo, null, swapchainPtr) != Result.Success)
                throw new Exception("无法创建 Swapchain");
        }

        _khrSwapchain.GetSwapchainImages(_device, _swapchain, &imageCount, null);
        _swapchainImages = new Image[imageCount];
        fixed (Image* imagesPtr = _swapchainImages)
            _khrSwapchain.GetSwapchainImages(_device, _swapchain, &imageCount, imagesPtr);
    }

    private void CreateImageViews()
    {
        _swapchainImageViews = new ImageView[_swapchainImages.Length];
        for (int i = 0; i < _swapchainImages.Length; i++)
        {
            ImageViewCreateInfo createInfo = new()
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = _swapchainImages[i],
                ViewType = ImageViewType.Type2D,
                Format = _swapchainFormat,
                Components = new ComponentMapping(
                    ComponentSwizzle.Identity, ComponentSwizzle.Identity,
                    ComponentSwizzle.Identity, ComponentSwizzle.Identity),
                SubresourceRange = new ImageSubresourceRange(
                    ImageAspectFlags.ColorBit, 0, 1, 0, 1)
            };
            fixed (ImageView* viewPtr = &_swapchainImageViews[i])
            {
                if (_vk!.CreateImageView(_device, &createInfo, null, viewPtr) != Result.Success)
                    throw new Exception($"无法创建 Swapchain ImageView {i}");
            }
        }
    }

    private void CreateCommandPool()
    {
        CommandPoolCreateInfo poolInfo = new()
        {
            SType = StructureType.CommandPoolCreateInfo,
            Flags = CommandPoolCreateFlags.ResetCommandBufferBit,
            QueueFamilyIndex = _graphicsQueueFamily
        };
        fixed (CommandPool* poolPtr = &_commandPool)
        {
            if (_vk!.CreateCommandPool(_device, &poolInfo, null, poolPtr) != Result.Success)
                throw new Exception("无法创建 CommandPool");
        }
    }

    private void CreateCommandBuffers()
    {
        _commandBuffers = new CommandBuffer[_swapchainImages.Length];
        CommandBufferAllocateInfo allocInfo = new()
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = _commandPool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = (uint)_commandBuffers.Length
        };
        fixed (CommandBuffer* cmdPtr = _commandBuffers)
        {
            if (_vk!.AllocateCommandBuffers(_device, &allocInfo, cmdPtr) != Result.Success)
                throw new Exception("无法分配 CommandBuffer");
        }
    }

    private void CreateSyncObjects()
    {
        _imageAvailableSemaphores = new Semaphore[MaxFramesInFlight];
        _renderFinishedSemaphores = new Semaphore[_swapchainImages.Length];
        _inFlightFences = new Fence[MaxFramesInFlight];

        SemaphoreCreateInfo semInfo = new() { SType = StructureType.SemaphoreCreateInfo };
        FenceCreateInfo fenceInfo = new() { SType = StructureType.FenceCreateInfo, Flags = FenceCreateFlags.SignaledBit };

        for (int i = 0; i < MaxFramesInFlight; i++)
        {
            fixed (Semaphore* imgSemPtr = &_imageAvailableSemaphores[i])
            fixed (Fence* fencePtr = &_inFlightFences[i])
            {
                if (_vk!.CreateSemaphore(_device, &semInfo, null, imgSemPtr) != Result.Success ||
                    _vk.CreateFence(_device, &fenceInfo, null, fencePtr) != Result.Success)
                    throw new Exception($"无法创建同步对象 {i}");
            }
        }
        for (int i = 0; i < _renderFinishedSemaphores.Length; i++)
        {
            fixed (Semaphore* renderSemPtr = &_renderFinishedSemaphores[i])
            {
                if (_vk!.CreateSemaphore(_device, &semInfo, null, renderSemPtr) != Result.Success)
                    throw new Exception($"无法创建 render-finished 信号量 {i}");
            }
        }
    }

    private static uint FindMemoryType(PhysicalDeviceMemoryProperties memProps, uint typeFilter, MemoryPropertyFlags properties)
    {
        for (int i = 0; i < memProps.MemoryTypeCount; i++)
        {
            if ((typeFilter & (1u << i)) != 0 &&
                (memProps.MemoryTypes[i].PropertyFlags & properties) == properties)
                return (uint)i;
        }
        throw new Exception("未找到合适的内存类型");
    }

    private PhysicalDeviceMemoryProperties _memProps;
    private void QueryMemoryProps() => _vk!.GetPhysicalDeviceMemoryProperties(_physicalDevice, out _memProps);

    private static uint[] ToUIntArray(byte[] bytes)
    {
        uint[] words = new uint[bytes.Length / 4];
        System.Buffer.BlockCopy(bytes, 0, words, 0, words.Length * 4);
        return words;
    }

    private ShaderModule CreateShaderModule(uint[] code)
    {
        ShaderModule module;
        fixed (uint* codePtr = code)
        {
            ShaderModuleCreateInfo createInfo = new()
            {
                SType = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)(code.Length * sizeof(uint)),
                PCode = codePtr
            };
            var result = _vk!.CreateShaderModule(_device, &createInfo, null, &module);
            if (result != Result.Success) throw new Exception($"无法创建着色器模块: {result}");
        }
        return module;
    }

    // ════════════════════════════════════════════════════════════════
    // Buffer / Image / Memory helpers
    // ════════════════════════════════════════════════════════════════
    private void CreateBuffer(ulong size, BufferUsageFlags usage, MemoryPropertyFlags memProps,
        out Buffer buffer, out DeviceMemory memory)
    {
        BufferCreateInfo bufInfo = new()
        {
            SType = StructureType.BufferCreateInfo,
            Size = size,
            Usage = usage,
            SharingMode = SharingMode.Exclusive
        };
        fixed (Buffer* bufPtr = &buffer)
        {
            if (_vk!.CreateBuffer(_device, &bufInfo, null, bufPtr) != Result.Success)
                throw new Exception("无法创建 Buffer");
        }
        MemoryRequirements req;
        _vk.GetBufferMemoryRequirements(_device, buffer, &req);
        uint memType = FindMemoryType(_memProps, req.MemoryTypeBits, memProps);
        MemoryAllocateInfo alloc = new()
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = req.Size,
            MemoryTypeIndex = memType
        };
        fixed (DeviceMemory* memPtr = &memory)
        {
            if (_vk.AllocateMemory(_device, &alloc, null, memPtr) != Result.Success)
                throw new Exception("无法分配 Buffer 内存");
        }
        _vk.BindBufferMemory(_device, buffer, memory, 0);
    }

    private void CreateImage(uint width, uint height, Format format, ImageUsageFlags usage,
        ImageAspectFlags aspect, ImageTiling tiling, out Image image, out DeviceMemory memory, out ImageView view)
    {
        ImageCreateInfo imgInfo = new()
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Extent = new Extent3D(width, height, 1),
            MipLevels = 1,
            ArrayLayers = 1,
            Format = format,
            Tiling = tiling,
            InitialLayout = ImageLayout.Undefined,
            Usage = usage,
            SharingMode = SharingMode.Exclusive,
            Samples = SampleCountFlags.Count1Bit
        };
        fixed (Image* imgPtr = &image)
        {
            if (_vk!.CreateImage(_device, &imgInfo, null, imgPtr) != Result.Success)
                throw new Exception("无法创建 Image");
        }
        MemoryRequirements req;
        _vk.GetImageMemoryRequirements(_device, image, &req);
        MemoryPropertyFlags memProps = tiling == ImageTiling.Linear
            ? MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit
            : MemoryPropertyFlags.DeviceLocalBit;
        uint memType = FindMemoryType(_memProps, req.MemoryTypeBits, memProps);
        MemoryAllocateInfo alloc = new()
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = req.Size,
            MemoryTypeIndex = memType
        };
        fixed (DeviceMemory* memPtr = &memory)
        {
            if (_vk.AllocateMemory(_device, &alloc, null, memPtr) != Result.Success)
                throw new Exception("无法分配 Image 内存");
        }
        _vk.BindImageMemory(_device, image, memory, 0);

        ImageViewCreateInfo viewInfo = new()
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = image,
            ViewType = ImageViewType.Type2D,
            Format = format,
            Components = new ComponentMapping(
                ComponentSwizzle.Identity, ComponentSwizzle.Identity,
                ComponentSwizzle.Identity, ComponentSwizzle.Identity),
            SubresourceRange = new ImageSubresourceRange(aspect, 0, 1, 0, 1)
        };
        fixed (ImageView* vPtr = &view)
        {
            if (_vk.CreateImageView(_device, &viewInfo, null, vPtr) != Result.Success)
                throw new Exception("无法创建 ImageView");
        }
    }

    private void CreateSamplers()
    {
        SamplerCreateInfo cmpInfo = new()
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = Filter.Linear,
            MinFilter = Filter.Linear,
            MipmapMode = SamplerMipmapMode.Nearest,
            AddressModeU = SamplerAddressMode.ClampToEdge,
            AddressModeV = SamplerAddressMode.ClampToEdge,
            AddressModeW = SamplerAddressMode.ClampToEdge,
            CompareOp = CompareOp.Less,
            MinLod = 0, MaxLod = 0,
            BorderColor = BorderColor.FloatOpaqueWhite
        };
        fixed (Sampler* sPtr = &_shadowSamplerCmp)
        {
            if (_vk!.CreateSampler(_device, &cmpInfo, null, sPtr) != Result.Success)
                throw new Exception("无法创建 shadow comparison sampler");
        }

        SamplerCreateInfo linInfo = new()
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = Filter.Linear,
            MinFilter = Filter.Linear,
            MipmapMode = SamplerMipmapMode.Linear,
            AddressModeU = SamplerAddressMode.ClampToEdge,
            AddressModeV = SamplerAddressMode.ClampToEdge,
            AddressModeW = SamplerAddressMode.ClampToEdge,
            MinLod = 0, MaxLod = 1,
            BorderColor = BorderColor.FloatOpaqueBlack
        };
        fixed (Sampler* sPtr = &_linearSampler)
        {
            if (_vk!.CreateSampler(_device, &linInfo, null, sPtr) != Result.Success)
                throw new Exception("无法创建 linear sampler");
        }
    }

    // ════════════════════════════════════════════════════════════════
    // Uniform buffers + scene mesh
    // ════════════════════════════════════════════════════════════════
    private void CreateUniformBuffers()
    {
        ulong camSize = 3UL * 64 + 16;          // 208
        ulong lightSize = 16UL * 3 + 16 + 4 * 64; // 320
        ulong resSize = 16;
        ulong exposureSize = 16;
        ulong fxaaSize = 16;

        var props = MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit;
        CreateBuffer(camSize, BufferUsageFlags.UniformBufferBit, props, out _cameraUBO, out _cameraUBOMem);
        void* camPtr; _vk!.MapMemory(_device, _cameraUBOMem, 0, camSize, 0, &camPtr); _cameraUBOPtr = (byte*)camPtr;
        CreateBuffer(lightSize, BufferUsageFlags.UniformBufferBit, props, out _lightUBO, out _lightUBOMem);
        void* lightPtr; _vk.MapMemory(_device, _lightUBOMem, 0, lightSize, 0, &lightPtr); _lightUBOPtr = (byte*)lightPtr;
        CreateBuffer(resSize, BufferUsageFlags.UniformBufferBit, props, out _resUBO, out _resUBOMem);
        void* resPtr; _vk.MapMemory(_device, _resUBOMem, 0, resSize, 0, &resPtr); _resUBOPtr = (byte*)resPtr;
        CreateBuffer(exposureSize, BufferUsageFlags.UniformBufferBit, props, out _exposureUBO, out _exposureUBOMem);
        void* expPtr; _vk.MapMemory(_device, _exposureUBOMem, 0, exposureSize, 0, &expPtr); _exposureUBOPtr = (byte*)expPtr;
        CreateBuffer(fxaaSize, BufferUsageFlags.UniformBufferBit, props, out _fxaaUBO, out _fxaaUBOMem);
        void* fxaaPtr; _vk.MapMemory(_device, _fxaaUBOMem, 0, fxaaSize, 0, &fxaaPtr); _fxaaUBOPtr = (byte*)fxaaPtr;
    }

    private static void WriteUBO(byte* dst, ReadOnlySpan<byte> data, ulong size)
    {
        fixed (byte* src = data)
            System.Buffer.MemoryCopy(src, dst, size, (ulong)data.Length);
    }

    private void BuildScene()
    {
        // Cube: 24 verts (per-face normals), 36 indices. Format: pos(3)+normal(3)+uv(2)+tangent(3)=11 floats
        var verts = new List<float>();
        var indices = new List<uint>();

        void AddFace(Vector3[] p, Vector3 n, Vector3 t)
        {
            uint baseIdx = (uint)(verts.Count / 11);
            // UVs: 0,0 1,0 1,1 0,1
            Vector2[] uvs = new Vector2[] { new(0, 0), new(1, 0), new(1, 1), new(0, 1) };
            for (int i = 0; i < 4; i++)
            {
                verts.Add(p[i].X); verts.Add(p[i].Y); verts.Add(p[i].Z);
                verts.Add(n.X); verts.Add(n.Y); verts.Add(n.Z);
                verts.Add(uvs[i].X); verts.Add(uvs[i].Y);
                verts.Add(t.X); verts.Add(t.Y); verts.Add(t.Z);
            }
            indices.Add(baseIdx + 0); indices.Add(baseIdx + 1); indices.Add(baseIdx + 2);
            indices.Add(baseIdx + 0); indices.Add(baseIdx + 2); indices.Add(baseIdx + 3);
        }

        float h = 0.5f;
        // Front (+Z): CCW when viewed from +Z
        AddFace(new[] { new Vector3(-h, -h, h), new Vector3(h, -h, h), new Vector3(h, h, h), new Vector3(-h, h, h) },
                new Vector3(0, 0, 1), new Vector3(1, 0, 0));
        // Back (-Z): CCW from -Z
        AddFace(new[] { new Vector3(h, -h, -h), new Vector3(-h, -h, -h), new Vector3(-h, h, -h), new Vector3(h, h, -h) },
                new Vector3(0, 0, -1), new Vector3(-1, 0, 0));
        // Right (+X)
        AddFace(new[] { new Vector3(h, -h, h), new Vector3(h, -h, -h), new Vector3(h, h, -h), new Vector3(h, h, h) },
                new Vector3(1, 0, 0), new Vector3(0, 0, -1));
        // Left (-X)
        AddFace(new[] { new Vector3(-h, -h, -h), new Vector3(-h, -h, h), new Vector3(-h, h, h), new Vector3(-h, h, -h) },
                new Vector3(-1, 0, 0), new Vector3(0, 0, 1));
        // Top (+Y)
        AddFace(new[] { new Vector3(-h, h, h), new Vector3(h, h, h), new Vector3(h, h, -h), new Vector3(-h, h, -h) },
                new Vector3(0, 1, 0), new Vector3(1, 0, 0));
        // Bottom (-Y)
        AddFace(new[] { new Vector3(-h, -h, -h), new Vector3(h, -h, -h), new Vector3(h, -h, h), new Vector3(-h, -h, h) },
                new Vector3(0, -1, 0), new Vector3(1, 0, 0));

        uint cubeIndexCount = (uint)indices.Count;

        // Ground plane at y=-0.5, facing +Y, CCW from above
        uint planeBase = (uint)(verts.Count / 11);
        Vector3[] pp = new[]
        {
            new Vector3(-20, -0.5f, 20),
            new Vector3(20, -0.5f, 20),
            new Vector3(20, -0.5f, -20),
            new Vector3(-20, -0.5f, -20),
        };
        for (int i = 0; i < 4; i++)
        {
            verts.Add(pp[i].X); verts.Add(pp[i].Y); verts.Add(pp[i].Z);
            verts.Add(0); verts.Add(1); verts.Add(0); // normal +Y
            verts.Add(0); verts.Add(0);               // uv
            verts.Add(1); verts.Add(0); verts.Add(0); // tangent +X
        }
        indices.Add(planeBase + 0); indices.Add(planeBase + 1); indices.Add(planeBase + 2);
        indices.Add(planeBase + 0); indices.Add(planeBase + 2); indices.Add(planeBase + 3);

        uint planeIndexCount = 6;
        uint planeIndexOffset = cubeIndexCount;

        float[] vertArray = verts.ToArray();
        uint[] idxArray = indices.ToArray();

        // Upload vertex buffer
        ulong vertBytes = (ulong)(vertArray.Length * sizeof(float));
        CreateBuffer(vertBytes, BufferUsageFlags.VertexBufferBit | BufferUsageFlags.TransferDstBit,
            MemoryPropertyFlags.DeviceLocalBit, out _vertexBuffer, out _vertexBufferMem);
        UploadBuffer(_vertexBuffer, _vertexBufferMem, vertBytes, MemoryMarshal.AsBytes(vertArray.AsSpan()));

        ulong idxBytes = (ulong)(idxArray.Length * sizeof(uint));
        CreateBuffer(idxBytes, BufferUsageFlags.IndexBufferBit | BufferUsageFlags.TransferDstBit,
            MemoryPropertyFlags.DeviceLocalBit, out _indexBuffer, out _indexBufferMem);
        UploadBuffer(_indexBuffer, _indexBufferMem, idxBytes, MemoryMarshal.AsBytes(idxArray.AsSpan()));

        // Mesh draws
        var draws = new List<MeshDraw>();
        // Ground — enlarged to cover the 10x10 cube grid
        Matrix4x4 groundWorld = Matrix4x4.Identity;
        draws.Add(new MeshDraw
        {
            IndexOffset = planeIndexOffset, IndexCount = planeIndexCount,
            World = groundWorld,
            Color = new Vector4(0.55f, 0.55f, 0.58f, 0.0f),
            PBR = new Vector4(0.85f, 1.0f, 0.0f, 0.0f)
        });
        // Cubes — 100 in a 10x10 grid so frustum culling is observable
        const int gridN = 10;
        const float gridSpacing = 3.0f;
        float gridMin = -((gridN - 1) * gridSpacing) * 0.5f;
        int cubeIndex = 0;
        for (int gx = 0; gx < gridN; gx++)
        {
            for (int gz = 0; gz < gridN; gz++)
            {
                float x = gridMin + gx * gridSpacing;
                float z = gridMin + gz * gridSpacing;
                // Hue-ish albedo variation based on linear index
                float t = cubeIndex / (float)(gridN * gridN - 1);
                Vector4 color = new(
                    0.2f + 0.7f * (1.0f - t),
                    0.2f + 0.7f * MathF.Abs(MathF.Sin(cubeIndex * 0.7f)),
                    0.2f + 0.7f * t,
                    0.0f);
                float roughness = 0.3f + ((cubeIndex % gridN) / (float)(gridN - 1)) * 0.5f;
                float metallic = (cubeIndex % 2);
                draws.Add(new MeshDraw
                {
                    IndexOffset = 0, IndexCount = cubeIndexCount,
                    World = Matrix4x4.CreateTranslation(new Vector3(x, 0.5f, z)),
                    Color = color,
                    PBR = new Vector4(roughness, 1.0f, 0.0f, 0.0f)
                });
                cubeIndex++;
            }
        }
        // 远处立方体：让环绕相机真正触发视锥剔除（远离原点，常被剔除/常被看见随轨道变化）
        Vector3[] farPos = { new(-45f, 1f, 0f), new(45f, 1f, 0f), new(0f, 1f, -45f) };
        Vector4[] farColor =
        {
            new(0.9f, 0.25f, 0.2f, 0f),
            new(0.2f, 0.9f, 0.3f, 0f),
            new(0.25f, 0.35f, 0.95f, 0f),
        };
        for (int i = 0; i < farPos.Length; i++)
        {
            draws.Add(new MeshDraw
            {
                IndexOffset = 0, IndexCount = cubeIndexCount,
                World = Matrix4x4.CreateTranslation(farPos[i]),
                Color = farColor[i],
                PBR = new Vector4(0.4f, 1.0f, 0.0f, 0.0f)
            });
        }
        _meshDraws = draws.ToArray();

        // Build GPU-driven render item array (112-byte layout matching FrustumCull.comp.hlsl / DeferredPBR.vert.hlsl)
        _renderItems = new RenderItem[_meshDraws.Length];
        for (int i = 0; i < _meshDraws.Length; i++)
        {
            var d = _meshDraws[i];
            _renderItems[i] = new RenderItem
            {
                World = d.World,
                Color = d.Color,
                PBR = d.PBR,
                IndexCount = d.IndexCount,
                FirstIndex = d.IndexOffset,
                BaseVertex = 0,
                Pad = 0
            };
        }
        System.Diagnostics.Debug.Assert(Marshal.SizeOf<RenderItem>() == 112,
            $"RenderItem size mismatch: {Marshal.SizeOf<RenderItem>()}");
        System.Diagnostics.Debug.Assert(Marshal.SizeOf<GpuIndirectCommand>() == 20,
            $"GpuIndirectCommand size mismatch: {Marshal.SizeOf<GpuIndirectCommand>()}");
        System.Diagnostics.Debug.Assert(Marshal.SizeOf<CullPush>() == 112,
            $"CullPush size mismatch: {Marshal.SizeOf<CullPush>()}");
    }

    private void UploadBuffer(Buffer buffer, DeviceMemory mem, ulong size, ReadOnlySpan<byte> data)
    {
        CreateBuffer(size, BufferUsageFlags.TransferSrcBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
            out Buffer staging, out DeviceMemory stagingMem);
        void* ptr;
        _vk!.MapMemory(_device, stagingMem, 0, size, 0, &ptr);
        fixed (byte* src = data)
            System.Buffer.MemoryCopy(src, ptr, size, size);
        _vk.UnmapMemory(_device, stagingMem);

        CommandBuffer cmd = BeginOneTimeCmd();
        BufferCopy region = new() { Size = size, SrcOffset = 0, DstOffset = 0 };
        _vk.CmdCopyBuffer(cmd, staging, buffer, 1, in region);
        EndOneTimeCmd(cmd);

        _vk.DestroyBuffer(_device, staging, null);
        _vk.FreeMemory(_device, stagingMem, null);
    }

    private CommandBuffer BeginOneTimeCmd()
    {
        CommandBufferAllocateInfo allocInfo = new()
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = _commandPool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = 1
        };
        CommandBuffer cmd;
        _vk!.AllocateCommandBuffers(_device, &allocInfo, &cmd);
        CommandBufferBeginInfo beginInfo = new()
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit
        };
        _vk.BeginCommandBuffer(cmd, &beginInfo);
        return cmd;
    }

    private void EndOneTimeCmd(CommandBuffer cmd)
    {
        _vk!.EndCommandBuffer(cmd);
        SubmitInfo submit = new()
        {
            SType = StructureType.SubmitInfo,
            CommandBufferCount = 1,
            PCommandBuffers = &cmd
        };
        _vk.QueueSubmit(_graphicsQueue, 1, in submit, default);
        _vk.QueueWaitIdle(_graphicsQueue);
        _vk.FreeCommandBuffers(_device, _commandPool, 1, in cmd);
    }

    // ════════════════════════════════════════════════════════════════
    // Render pass / image / framebuffer creation
    // ════════════════════════════════════════════════════════════════
    private void CreateShadowResources()
    {
        // Render pass: depth only
        AttachmentDescription depthAtt = new()
        {
            Format = Format.D32Sfloat,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.Store,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.Undefined,
            FinalLayout = ImageLayout.ShaderReadOnlyOptimal
        };
        AttachmentReference depthRef = new()
        {
            Attachment = 0,
            Layout = ImageLayout.DepthStencilAttachmentOptimal
        };
        SubpassDescription subpass = new()
        {
            PipelineBindPoint = PipelineBindPoint.Graphics,
            PDepthStencilAttachment = &depthRef
        };
        // shadow depth -> lighting sample: finalLayout auto-transitions to ShaderReadOnlyOptimal
        RenderPassCreateInfo rpInfo = new()
        {
            SType = StructureType.RenderPassCreateInfo,
            AttachmentCount = 1,
            PAttachments = &depthAtt,
            SubpassCount = 1,
            PSubpasses = &subpass,
            DependencyCount = 2
        };
        SubpassDependency[] deps = new[]
        {
            new SubpassDependency
            {
                SrcSubpass = Vk.SubpassExternal, DstSubpass = 0,
                SrcStageMask = PipelineStageFlags.LateFragmentTestsBit,
                SrcAccessMask = AccessFlags.DepthStencilAttachmentWriteBit,
                DstStageMask = PipelineStageFlags.EarlyFragmentTestsBit,
                DstAccessMask = AccessFlags.DepthStencilAttachmentReadBit | AccessFlags.DepthStencilAttachmentWriteBit
            },
            new SubpassDependency
            {
                SrcSubpass = 0, DstSubpass = Vk.SubpassExternal,
                SrcStageMask = PipelineStageFlags.LateFragmentTestsBit,
                SrcAccessMask = AccessFlags.DepthStencilAttachmentWriteBit,
                DstStageMask = PipelineStageFlags.FragmentShaderBit,
                DstAccessMask = AccessFlags.ShaderReadBit
            }
        };
        fixed (SubpassDependency* depPtr = deps)
        {
            rpInfo.PDependencies = depPtr;
            fixed (RenderPass* rpPtr = &_shadowRenderPass)
            {
                if (_vk!.CreateRenderPass(_device, &rpInfo, null, rpPtr) != Result.Success)
                    throw new Exception("无法创建 shadow RenderPass");
            }
        }

        // Shadow images
        for (int c = 0; c < NumCascades; c++)
        {
            CreateImage(ShadowMapSize, ShadowMapSize, Format.D32Sfloat,
                ImageUsageFlags.DepthStencilAttachmentBit | ImageUsageFlags.SampledBit,
                ImageAspectFlags.DepthBit, ImageTiling.Optimal,
                out _shadowImages[c], out _shadowMems[c], out _shadowViews[c]);

            ImageView[] shadowViewArr = new ImageView[] { _shadowViews[c] };
            fixed (ImageView* attPtr = shadowViewArr)
            fixed (Framebuffer* fbPtr = &_shadowFramebuffers[c])
            {
                FramebufferCreateInfo fbInfo = new()
                {
                    SType = StructureType.FramebufferCreateInfo,
                    RenderPass = _shadowRenderPass,
                    AttachmentCount = 1,
                    PAttachments = attPtr,
                    Width = ShadowMapSize, Height = ShadowMapSize, Layers = 1
                };
                if (_vk.CreateFramebuffer(_device, &fbInfo, null, fbPtr) != Result.Success)
                    throw new Exception($"无法创建 shadow framebuffer {c}");
            }
        }
    }

    private void CreateGBufferResources()
    {
        uint w = _swapchainExtent.Width, h = _swapchainExtent.Height;
        Format[] colorFormats = new[]
        {
            Format.R16G16B16A16Sfloat, // 0 worldPos
            Format.R8G8B8A8Unorm,      // 1 albedo
            Format.R16G16B16A16Sfloat, // 2 normal
            Format.R8G8B8A8Unorm,      // 3 pbr
            Format.R16G16B16A16Sfloat, // 4 emissive
        };

        // Render pass: 5 color + 1 depth
        AttachmentDescription[] atts = new AttachmentDescription[6];
        for (int i = 0; i < 5; i++)
        {
            atts[i] = new AttachmentDescription
            {
                Format = colorFormats[i],
                Samples = SampleCountFlags.Count1Bit,
                LoadOp = AttachmentLoadOp.Clear,
                StoreOp = AttachmentStoreOp.Store,
                StencilLoadOp = AttachmentLoadOp.DontCare,
                StencilStoreOp = AttachmentStoreOp.DontCare,
                InitialLayout = ImageLayout.Undefined,
                FinalLayout = ImageLayout.ShaderReadOnlyOptimal
            };
        }
        atts[5] = new AttachmentDescription
        {
            Format = Format.D32Sfloat,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.DontCare,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.Undefined,
            FinalLayout = ImageLayout.ShaderReadOnlyOptimal
        };

        AttachmentReference[] colorRefs = new AttachmentReference[5];
        for (int i = 0; i < 5; i++)
            colorRefs[i] = new AttachmentReference
            {
                Attachment = (uint)i,
                Layout = ImageLayout.ColorAttachmentOptimal
            };
        AttachmentReference depthRef = new()
        {
            Attachment = 5,
            Layout = ImageLayout.DepthStencilAttachmentOptimal
        };

        fixed (AttachmentDescription* attsPtr = atts)
        fixed (AttachmentReference* colorRefsPtr = colorRefs)
        {
            SubpassDescription subpass = new()
            {
                PipelineBindPoint = PipelineBindPoint.Graphics,
                ColorAttachmentCount = 5,
                PColorAttachments = colorRefsPtr,
                PDepthStencilAttachment = &depthRef
            };
            SubpassDependency dep = new()
            {
                SrcSubpass = Vk.SubpassExternal, DstSubpass = 0,
                SrcStageMask = PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.LateFragmentTestsBit,
                SrcAccessMask = 0,
                DstStageMask = PipelineStageFlags.FragmentShaderBit,
                DstAccessMask = AccessFlags.ShaderReadBit
            };
            RenderPassCreateInfo rpInfo = new()
            {
                SType = StructureType.RenderPassCreateInfo,
                AttachmentCount = 6,
                PAttachments = attsPtr,
                SubpassCount = 1,
                PSubpasses = &subpass,
                DependencyCount = 1,
                PDependencies = &dep
            };
            fixed (RenderPass* rpPtr = &_gbufferRenderPass)
            {
                if (_vk!.CreateRenderPass(_device, &rpInfo, null, rpPtr) != Result.Success)
                    throw new Exception("无法创建 GBuffer RenderPass");
            }
        }

        // GBuffer images
        for (int i = 0; i < 5; i++)
        {
            CreateImage(w, h, colorFormats[i],
                ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.SampledBit,
                ImageAspectFlags.ColorBit, ImageTiling.Optimal,
                out _gbufferImages[i], out _gbufferMems[i], out _gbufferViews[i]);
        }
        CreateImage(w, h, Format.D32Sfloat,
            ImageUsageFlags.DepthStencilAttachmentBit | ImageUsageFlags.SampledBit,
            ImageAspectFlags.DepthBit, ImageTiling.Optimal,
            out _gbufferDepth, out _gbufferDepthMem, out _gbufferDepthView);

        // GBuffer framebuffer
        ImageView[] fbAtts = new ImageView[6];
        for (int i = 0; i < 5; i++) fbAtts[i] = _gbufferViews[i];
        fbAtts[5] = _gbufferDepthView;
        fixed (ImageView* fbAttsPtr = fbAtts)
        {
            FramebufferCreateInfo fbInfo = new()
            {
                SType = StructureType.FramebufferCreateInfo,
                RenderPass = _gbufferRenderPass,
                AttachmentCount = 6,
                PAttachments = fbAttsPtr,
                Width = w, Height = h, Layers = 1
            };
            fixed (Framebuffer* fbPtr = &_gbufferFramebuffer)
            {
                if (_vk.CreateFramebuffer(_device, &fbInfo, null, fbPtr) != Result.Success)
                    throw new Exception("无法创建 GBuffer framebuffer");
            }
        }
    }

    private void CreateHDRResources()
    {
        uint w = _swapchainExtent.Width, h = _swapchainExtent.Height;
        CreateImage(w, h, Format.R16G16B16A16Sfloat,
            ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.SampledBit,
            ImageAspectFlags.ColorBit, ImageTiling.Optimal,
            out _hdrColor, out _hdrColorMem, out _hdrColorView);
    }

    private void CreateLightingResources()
    {
        uint w = _swapchainExtent.Width, h = _swapchainExtent.Height;

        AttachmentDescription colorAtt = new()
        {
            Format = Format.R16G16B16A16Sfloat,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.Store,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.Undefined,
            FinalLayout = ImageLayout.ShaderReadOnlyOptimal
        };
        AttachmentReference colorRef = new() { Attachment = 0, Layout = ImageLayout.ColorAttachmentOptimal };
        SubpassDescription subpass = new()
        {
            PipelineBindPoint = PipelineBindPoint.Graphics,
            ColorAttachmentCount = 1,
            PColorAttachments = &colorRef
        };
        SubpassDependency dep = new()
        {
            SrcSubpass = Vk.SubpassExternal, DstSubpass = 0,
            SrcStageMask = PipelineStageFlags.ColorAttachmentOutputBit,
            SrcAccessMask = 0,
            DstStageMask = PipelineStageFlags.FragmentShaderBit,
            DstAccessMask = AccessFlags.ShaderReadBit
        };
        RenderPassCreateInfo rpInfo = new()
        {
            SType = StructureType.RenderPassCreateInfo,
            AttachmentCount = 1,
            PAttachments = &colorAtt,
            SubpassCount = 1,
            PSubpasses = &subpass,
            DependencyCount = 1,
            PDependencies = &dep
        };
        fixed (RenderPass* rpPtr = &_lightingRenderPass)
        {
            if (_vk!.CreateRenderPass(_device, &rpInfo, null, rpPtr) != Result.Success)
                throw new Exception("无法创建 Lighting RenderPass");
        }
    }

    private void CreateColorRenderPass(Format format, ImageLayout finalLayout, out RenderPass rp)
    {
        AttachmentDescription colorAtt = new()
        {
            Format = format,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.Store,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.Undefined,
            FinalLayout = finalLayout
        };
        AttachmentReference colorRef = new() { Attachment = 0, Layout = ImageLayout.ColorAttachmentOptimal };
        SubpassDescription subpass = new()
        {
            PipelineBindPoint = PipelineBindPoint.Graphics,
            ColorAttachmentCount = 1,
            PColorAttachments = &colorRef
        };
        SubpassDependency dep = new()
        {
            SrcSubpass = Vk.SubpassExternal, DstSubpass = 0,
            SrcStageMask = PipelineStageFlags.ColorAttachmentOutputBit,
            SrcAccessMask = 0,
            DstStageMask = PipelineStageFlags.FragmentShaderBit,
            DstAccessMask = AccessFlags.ShaderReadBit
        };
        RenderPassCreateInfo rpInfo = new()
        {
            SType = StructureType.RenderPassCreateInfo,
            AttachmentCount = 1,
            PAttachments = &colorAtt,
            SubpassCount = 1,
            PSubpasses = &subpass,
            DependencyCount = 1,
            PDependencies = &dep
        };
        fixed (RenderPass* rpPtr = &rp)
        {
            if (_vk!.CreateRenderPass(_device, &rpInfo, null, rpPtr) != Result.Success)
                throw new Exception("无法创建 color RenderPass");
        }
    }

    private void CreatePostResources()
    {
        // Bloom composite pass: HDR + bloom → ldrRT (RGBA8, shader-read)
        CreateColorRenderPass(Format.R8G8B8A8Unorm, ImageLayout.ShaderReadOnlyOptimal, out _bloomCompositeRP);
        // Bloom bright pass: HDR → bloomBrightRT (half-res RGBA16F, shader-read)
        CreateColorRenderPass(Format.R16G16B16A16Sfloat, ImageLayout.ShaderReadOnlyOptimal, out _bloomBrightRP);
        // Bloom blur pass: (bloomBlurH or bloomFinal, half-res RGBA16F, shader-read)
        CreateColorRenderPass(Format.R16G16B16A16Sfloat, ImageLayout.ShaderReadOnlyOptimal, out _bloomBlurRP);
        // FXAA pass: ldrRT → swapchain
        CreateColorRenderPass(_swapchainFormat, ImageLayout.PresentSrcKhr, out _fxaaRP);
    }

    private void CreatePostProcessImages()
    {
        uint w = _swapchainExtent.Width, h = _swapchainExtent.Height;
        uint halfW = w / 2, halfH = h / 2;
        var usage = ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.SampledBit;

        CreateImage(halfW, halfH, Format.R16G16B16A16Sfloat, usage,
            ImageAspectFlags.ColorBit, ImageTiling.Optimal,
            out _bloomBright, out _bloomBrightMem, out _bloomBrightView);
        CreateImage(halfW, halfH, Format.R16G16B16A16Sfloat, usage,
            ImageAspectFlags.ColorBit, ImageTiling.Optimal,
            out _bloomBlurH, out _bloomBlurHMem, out _bloomBlurHView);
        CreateImage(halfW, halfH, Format.R16G16B16A16Sfloat, usage,
            ImageAspectFlags.ColorBit, ImageTiling.Optimal,
            out _bloomFinal, out _bloomFinalMem, out _bloomFinalView);

        CreateImage(w, h, Format.R8G8B8A8Unorm, usage,
            ImageAspectFlags.ColorBit, ImageTiling.Optimal,
            out _ldrRT, out _ldrRTMem, out _ldrRTView);
    }

    // ════════════════════════════════════════════════════════════════
    // GPU-driven culling: SSBOs + compute pipeline + descriptors
    // ════════════════════════════════════════════════════════════════
    private void CreateGpuDrivenResources()
    {
        ulong itemCount = (ulong)_renderItems.Length;
        ulong itemBytes = itemCount * 112;
        ulong cmdBytes = itemCount * (ulong)sizeof(GpuIndirectCommand);

        // items SSBO: STORAGE (compute/VS/FS read) + TRANSFER_DST (one-time staging upload)
        CreateBuffer(itemBytes,
            BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit,
            MemoryPropertyFlags.DeviceLocalBit,
            out _renderItemsSSBO, out _renderItemsSSBOMem);
        UploadBuffer(_renderItemsSSBO, _renderItemsSSBOMem, itemBytes,
            MemoryMarshal.AsBytes(_renderItems.AsSpan()));

        // indirect commands: STORAGE (compute writes) + INDIRECT (CmdDrawIndexedIndirect reads)
        CreateBuffer(cmdBytes,
            BufferUsageFlags.StorageBufferBit | BufferUsageFlags.IndirectBufferBit,
            MemoryPropertyFlags.DeviceLocalBit,
            out _indirectCmdsBuffer, out _indirectCmdsBufferMem);

        // visible count: STORAGE + TRANSFER_DST, HOST_VISIBLE|COHERENT, mapped persistently (4 bytes)
        CreateBuffer(4,
            BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
            out _visibleCountBuffer, out _visibleCountBufferMem);
        void* vcPtr;
        _vk!.MapMemory(_device, _visibleCountBufferMem, 0, 4, 0, &vcPtr);
        _visibleCountPtr = (byte*)vcPtr;
        *(uint*)_visibleCountPtr = 0;

        // Allocate the items SSBO set (set1 of GBuffer pipeline, bind0 -> _renderItemsSSBO)
        {
            DescriptorSet[] itemsSets = new DescriptorSet[1];
            DescriptorSetLayout[] itemsLays = new DescriptorSetLayout[] { _itemsSetLayout };
            fixed (DescriptorSetLayout* laysPtr = itemsLays)
            fixed (DescriptorSet* setsPtr = itemsSets)
            {
                DescriptorSetAllocateInfo alloc = new()
                {
                    SType = StructureType.DescriptorSetAllocateInfo,
                    DescriptorPool = _descriptorPool,
                    DescriptorSetCount = 1,
                    PSetLayouts = laysPtr
                };
                _vk!.AllocateDescriptorSets(_device, &alloc, setsPtr);
                _itemsSet = itemsSets[0];
            }
            DescriptorBufferInfo bi = new()
            {
                Buffer = _renderItemsSSBO, Offset = 0, Range = itemBytes
            };
            WriteDescriptorSet w = new()
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = _itemsSet, DstBinding = 0, DstArrayElement = 0,
                DescriptorCount = 1, DescriptorType = DescriptorType.StorageBuffer,
                PBufferInfo = &bi
            };
            _vk.UpdateDescriptorSets(_device, 1, in w, 0, null);
        }

        // Descriptor set layout for the cull compute shader: 3 SSBOs (items, outCommands, visibleCount)
        // FrustumCull.comp.hlsl: t0→binding1 items, u0→binding2 outCommands, u1→binding3 visibleCount
        // (视锥平面走 push constants，不占 binding)
        DescriptorSetLayoutBinding[] binds = new[]
        {
            new DescriptorSetLayoutBinding
            {
                Binding = 1, DescriptorType = DescriptorType.StorageBuffer,
                DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit,
                PImmutableSamplers = null
            },
            new DescriptorSetLayoutBinding
            {
                Binding = 2, DescriptorType = DescriptorType.StorageBuffer,
                DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit,
                PImmutableSamplers = null
            },
            new DescriptorSetLayoutBinding
            {
                Binding = 3, DescriptorType = DescriptorType.StorageBuffer,
                DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit,
                PImmutableSamplers = null
            },
        };
        fixed (DescriptorSetLayoutBinding* bPtr = binds)
        {
            DescriptorSetLayoutCreateInfo layInfo = new()
            {
                SType = StructureType.DescriptorSetLayoutCreateInfo,
                BindingCount = (uint)binds.Length,
                PBindings = bPtr
            };
            fixed (DescriptorSetLayout* layPtr = &_cullSetLayout)
            {
                if (_vk!.CreateDescriptorSetLayout(_device, &layInfo, null, layPtr) != Result.Success)
                    throw new Exception("无法创建 cull set layout");
            }
        }

        // Pipeline layout: set0 + push constants (compute, offset 0, size 112)
        PushConstantRange pcRange = new()
        {
            StageFlags = ShaderStageFlags.ComputeBit,
            Offset = 0, Size = 112
        };
        fixed (DescriptorSetLayout* setPtr = &_cullSetLayout)
        {
            PipelineLayoutCreateInfo layInfo = new()
            {
                SType = StructureType.PipelineLayoutCreateInfo,
                SetLayoutCount = 1,
                PSetLayouts = setPtr,
                PushConstantRangeCount = 1,
                PPushConstantRanges = &pcRange
            };
            fixed (PipelineLayout* layPtr = &_cullLayout)
            {
                if (_vk.CreatePipelineLayout(_device, &layInfo, null, layPtr) != Result.Success)
                    throw new Exception("无法创建 cull pipeline layout");
            }
        }

        // Compile compute shader
        string baseDir = AppContext.BaseDirectory;
        string shaderDir = Path.Combine(baseDir, "Shaders");
        var dxc = new YamaEngine.Shaders.DxcCompiler(
            shaderDir, cacheDir: Path.Combine(baseDir, "ShaderCache"), useDebug: false);
        string src = File.ReadAllText(Path.Combine(shaderDir, "FrustumCull.comp.hlsl"));
        var bc = dxc.Compile(src, "CSMain",
            YamaEngine.RHI.RHIShaderStage.Compute, YamaEngine.RHI.RHIShaderModel.SM_6_0);
        _cullComp = CreateShaderModule(ToUIntArray(bc.Bytecode));

        // Compute pipeline
        fixed (byte* namePtr = System.Text.Encoding.UTF8.GetBytes("CSMain"))
        {
            PipelineShaderStageCreateInfo stage = new()
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.ComputeBit,
                Module = _cullComp,
                PName = namePtr
            };
            ComputePipelineCreateInfo ci = new()
            {
                SType = StructureType.ComputePipelineCreateInfo,
                Stage = stage,
                Layout = _cullLayout
            };
            fixed (Pipeline* pipePtr = &_cullPipeline)
            {
                if (_vk.CreateComputePipelines(_device, default, 1, &ci, null, pipePtr) != Result.Success)
                    throw new Exception("无法创建 cull compute pipeline");
            }
        }
        _vk.DestroyShaderModule(_device, _cullComp, null);
        _cullComp = default;

        // Allocate cull descriptor set and bind the three SSBOs
        DescriptorSet[] sets = new DescriptorSet[1];
        DescriptorSetLayout[] lays = new DescriptorSetLayout[] { _cullSetLayout };
        fixed (DescriptorSetLayout* laysPtr = lays)
        fixed (DescriptorSet* setsPtr = sets)
        {
            DescriptorSetAllocateInfo alloc = new()
            {
                SType = StructureType.DescriptorSetAllocateInfo,
                DescriptorPool = _descriptorPool,
                DescriptorSetCount = 1,
                PSetLayouts = laysPtr
            };
            _vk.AllocateDescriptorSets(_device, &alloc, setsPtr);
            _cullSet = sets[0];
        }
        {
            var bufInfos = stackalloc DescriptorBufferInfo[3];
            bufInfos[0] = new DescriptorBufferInfo { Buffer = _renderItemsSSBO, Offset = 0, Range = itemBytes };
            bufInfos[1] = new DescriptorBufferInfo { Buffer = _indirectCmdsBuffer, Offset = 0, Range = cmdBytes };
            bufInfos[2] = new DescriptorBufferInfo { Buffer = _visibleCountBuffer, Offset = 0, Range = 4 };

            WriteDescriptorSet* writes = stackalloc WriteDescriptorSet[3];
            for (uint i = 0; i < 3; i++)
            {
                writes[i] = new WriteDescriptorSet
                {
                    SType = StructureType.WriteDescriptorSet,
                    DstSet = _cullSet,
                    DstBinding = i + 1, // FrustumCull.comp.hlsl uses bindings 1/2/3
                    DstArrayElement = 0,
                    DescriptorCount = 1,
                    DescriptorType = DescriptorType.StorageBuffer,
                    PBufferInfo = &bufInfos[i]
                };
            }
            _vk.UpdateDescriptorSets(_device, 3, writes, 0, null);
        }
    }

    // Create the items SSBO set layout used by GBuffer VS as set1. Called early so the
    // GBuffer pipeline layout can include it when _gpuDriven is enabled.
    private void CreateItemsSetLayout()
    {
        DescriptorSetLayoutBinding[] bindArr = new[]
        {
            new DescriptorSetLayoutBinding
            {
                Binding = 0,
                DescriptorType = DescriptorType.StorageBuffer,
                DescriptorCount = 1,
                StageFlags = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit,
                PImmutableSamplers = null
            }
        };
        fixed (DescriptorSetLayoutBinding* bPtr = bindArr)
        {
            DescriptorSetLayoutCreateInfo layInfo = new()
            {
                SType = StructureType.DescriptorSetLayoutCreateInfo,
                BindingCount = 1,
                PBindings = bPtr
            };
            fixed (DescriptorSetLayout* layPtr = &_itemsSetLayout)
            {
                if (_vk!.CreateDescriptorSetLayout(_device, &layInfo, null, layPtr) != Result.Success)
                    throw new Exception("无法创建 items set layout");
            }
        }
        _gbufferSetLayouts = new[] { _gbufferSetLayout, _itemsSetLayout };
    }

    // ════════════════════════════════════════════════════════════════
    // Gribb-Hartmann frustum plane extraction from a row-major view-proj matrix.
    // Plane ordering matches CullPush.P0..P5 (left, right, bottom, top, near, far).
    // Each plane is normalized so (n.xyz, n.w) satisfies dot(n.xyz, p) + n.w >= 0 inside.
    // ════════════════════════════════════════════════════════════════
    private static void ExtractFrustumPlanes(Matrix4x4 m,
        out Vector4 p0, out Vector4 p1, out Vector4 p2,
        out Vector4 p3, out Vector4 p4, out Vector4 p5)
    {
        Vector4 r1 = new(m.M11, m.M21, m.M31, m.M41);
        Vector4 r2 = new(m.M12, m.M22, m.M32, m.M42);
        Vector4 r3 = new(m.M13, m.M23, m.M33, m.M43);
        Vector4 r4 = new(m.M14, m.M24, m.M34, m.M44);
        p0 = NormalizePlane(r4 + r1);
        p1 = NormalizePlane(r4 - r1);
        p2 = NormalizePlane(r4 + r2);
        p3 = NormalizePlane(r4 - r2);
        p4 = NormalizePlane(r4 + r3);
        p5 = NormalizePlane(r4 - r3);
    }

    private static Vector4 NormalizePlane(Vector4 p)
    {
        float len = MathF.Sqrt(p.X * p.X + p.Y * p.Y + p.Z * p.Z);
        if (len < 1e-8f) return p;
        return p / len;
    }

    private void CreateFramebuffers()
    {
        uint w = _swapchainExtent.Width, h = _swapchainExtent.Height;
        uint halfW = w / 2, halfH = h / 2;

        // Lighting framebuffers (per swapchain image)
        _lightingFramebuffers = new Framebuffer[_swapchainImages.Length];
        ImageView[] hdrAtt = new ImageView[] { _hdrColorView };
        for (int i = 0; i < _swapchainImages.Length; i++)
        {
            fixed (ImageView* hdrAttPtr = hdrAtt)
            fixed (Framebuffer* fbPtr = &_lightingFramebuffers[i])
            {
                FramebufferCreateInfo fbInfo = new()
                {
                    SType = StructureType.FramebufferCreateInfo,
                    RenderPass = _lightingRenderPass,
                    AttachmentCount = 1,
                    PAttachments = hdrAttPtr,
                    Width = w, Height = h, Layers = 1
                };
                if (_vk!.CreateFramebuffer(_device, &fbInfo, null, fbPtr) != Result.Success)
                    throw new Exception($"无法创建 Lighting framebuffer {i}");
            }
        }

        // Bloom half-res framebuffers
        {
            ImageView[] att = new ImageView[] { _bloomBrightView };
            fixed (ImageView* attPtr = att) fixed (Framebuffer* fbPtr = &_bloomBrightFB)
            {
                FramebufferCreateInfo fbInfo = new()
                {
                    SType = StructureType.FramebufferCreateInfo,
                    RenderPass = _bloomBrightRP,
                    AttachmentCount = 1,
                    PAttachments = attPtr,
                    Width = halfW, Height = halfH, Layers = 1
                };
                if (_vk!.CreateFramebuffer(_device, &fbInfo, null, fbPtr) != Result.Success)
                    throw new Exception("无法创建 bloomBright framebuffer");
            }
        }
        {
            ImageView[] att = new ImageView[] { _bloomBlurHView };
            fixed (ImageView* attPtr = att) fixed (Framebuffer* fbPtr = &_bloomBlurHFB)
            {
                FramebufferCreateInfo fbInfo = new()
                {
                    SType = StructureType.FramebufferCreateInfo,
                    RenderPass = _bloomBlurRP,
                    AttachmentCount = 1,
                    PAttachments = attPtr,
                    Width = halfW, Height = halfH, Layers = 1
                };
                if (_vk!.CreateFramebuffer(_device, &fbInfo, null, fbPtr) != Result.Success)
                    throw new Exception("无法创建 bloomBlurH framebuffer");
            }
        }
        {
            ImageView[] att = new ImageView[] { _bloomFinalView };
            fixed (ImageView* attPtr = att) fixed (Framebuffer* fbPtr = &_bloomFinalFB)
            {
                FramebufferCreateInfo fbInfo = new()
                {
                    SType = StructureType.FramebufferCreateInfo,
                    RenderPass = _bloomBlurRP,
                    AttachmentCount = 1,
                    PAttachments = attPtr,
                    Width = halfW, Height = halfH, Layers = 1
                };
                if (_vk!.CreateFramebuffer(_device, &fbInfo, null, fbPtr) != Result.Success)
                    throw new Exception("无法创建 bloomFinal framebuffer");
            }
        }

        // Bloom composite framebuffer (ldrRT, full-res)
        {
            ImageView[] att = new ImageView[] { _ldrRTView };
            fixed (ImageView* attPtr = att) fixed (Framebuffer* fbPtr = &_bloomCompositeFB)
            {
                FramebufferCreateInfo fbInfo = new()
                {
                    SType = StructureType.FramebufferCreateInfo,
                    RenderPass = _bloomCompositeRP,
                    AttachmentCount = 1,
                    PAttachments = attPtr,
                    Width = w, Height = h, Layers = 1
                };
                if (_vk!.CreateFramebuffer(_device, &fbInfo, null, fbPtr) != Result.Success)
                    throw new Exception("无法创建 bloomComposite framebuffer");
            }
        }

        // FXAA framebuffers (per swapchain image, full-res)
        _fxaaFramebuffers = new Framebuffer[_swapchainImages.Length];
        for (int i = 0; i < _swapchainImages.Length; i++)
        {
            ImageView[] swAtt = new ImageView[] { _swapchainImageViews[i] };
            fixed (ImageView* swAttPtr = swAtt)
            fixed (Framebuffer* fbPtr = &_fxaaFramebuffers[i])
            {
                FramebufferCreateInfo fbInfo = new()
                {
                    SType = StructureType.FramebufferCreateInfo,
                    RenderPass = _fxaaRP,
                    AttachmentCount = 1,
                    PAttachments = swAttPtr,
                    Width = w, Height = h, Layers = 1
                };
                if (_vk!.CreateFramebuffer(_device, &fbInfo, null, fbPtr) != Result.Success)
                    throw new Exception($"无法创建 FXAA framebuffer {i}");
            }
        }
    }

    // ════════════════════════════════════════════════════════════════
    // Descriptor sets
    // ════════════════════════════════════════════════════════════════
    private void CreateDescriptorPoolAndSets()
    {
        // GBuffer set layout: b0 Camera UBO
        DescriptorSetLayoutBinding[] gbufBinds = new[]
        {
            new DescriptorSetLayoutBinding
            {
                Binding = 0,
                DescriptorType = DescriptorType.UniformBuffer,
                DescriptorCount = 1,
                StageFlags = ShaderStageFlags.VertexBit,
                PImmutableSamplers = null
            }
        };
        fixed (DescriptorSetLayoutBinding* bPtr = gbufBinds)
        {
            DescriptorSetLayoutCreateInfo layInfo = new()
            {
                SType = StructureType.DescriptorSetLayoutCreateInfo,
                BindingCount = 1,
                PBindings = bPtr
            };
            fixed (DescriptorSetLayout* layPtr = &_gbufferSetLayout)
            {
                if (_vk!.CreateDescriptorSetLayout(_device, &layInfo, null, layPtr) != Result.Success)
                    throw new Exception("无法创建 GBuffer set layout");
            }
        }

        // Lighting set layout: bindings 0..13
        // 0-2 UBOs; 3-7 gbuffer SampledImage; 8-11 shadow SampledImage; 12 cmp sampler; 13 linear sampler
        DescriptorSetLayoutBinding[] lightingBinds = new DescriptorSetLayoutBinding[14];
        for (int i = 0; i < 3; i++)
        {
            lightingBinds[i] = new DescriptorSetLayoutBinding
            {
                Binding = (uint)i,
                DescriptorType = DescriptorType.UniformBuffer,
                DescriptorCount = 1,
                StageFlags = ShaderStageFlags.FragmentBit,
                PImmutableSamplers = null
            };
        }
        for (int i = 3; i < 12; i++)
        {
            lightingBinds[i] = new DescriptorSetLayoutBinding
            {
                Binding = (uint)i,
                DescriptorType = DescriptorType.SampledImage,
                DescriptorCount = 1,
                StageFlags = ShaderStageFlags.FragmentBit,
                PImmutableSamplers = null
            };
        }
        // 12 cmp, 13 linear - use immutable samplers
        _lightingImmutableSamplers[0] = _shadowSamplerCmp;
        _lightingImmutableSamplers[1] = _linearSampler;
        fixed (Sampler* cmpPtr = _lightingImmutableSamplers.AsSpan(0, 1))
        fixed (Sampler* linPtr = _lightingImmutableSamplers.AsSpan(1, 1))
        {
            lightingBinds[12] = new DescriptorSetLayoutBinding
            {
                Binding = 12,
                DescriptorType = DescriptorType.Sampler,
                DescriptorCount = 1,
                StageFlags = ShaderStageFlags.FragmentBit,
                PImmutableSamplers = cmpPtr
            };
            lightingBinds[13] = new DescriptorSetLayoutBinding
            {
                Binding = 13,
                DescriptorType = DescriptorType.Sampler,
                DescriptorCount = 1,
                StageFlags = ShaderStageFlags.FragmentBit,
                PImmutableSamplers = linPtr
            };
            fixed (DescriptorSetLayoutBinding* bPtr = lightingBinds)
            {
                DescriptorSetLayoutCreateInfo layInfo = new()
                {
                    SType = StructureType.DescriptorSetLayoutCreateInfo,
                    BindingCount = 14,
                    PBindings = bPtr
                };
                fixed (DescriptorSetLayout* layPtr = &_lightingSetLayout)
                {
                    if (_vk!.CreateDescriptorSetLayout(_device, &layInfo, null, layPtr) != Result.Success)
                        throw new Exception("无法创建 Lighting set layout");
                }
            }
        }

        // Post set layout: b0 exposure, t1 HDR, s2 point
        // Bloom composite set layout: b0 exposure, t1 HDR, s2 sampler, t3 bloom
        DescriptorSetLayoutBinding[] bloomCompBinds = new DescriptorSetLayoutBinding[4];
        bloomCompBinds[0] = new DescriptorSetLayoutBinding
        {
            Binding = 0,
            DescriptorType = DescriptorType.UniformBuffer,
            DescriptorCount = 1,
            StageFlags = ShaderStageFlags.FragmentBit,
            PImmutableSamplers = null
        };
        bloomCompBinds[1] = new DescriptorSetLayoutBinding
        {
            Binding = 1,
            DescriptorType = DescriptorType.SampledImage,
            DescriptorCount = 1,
            StageFlags = ShaderStageFlags.FragmentBit,
            PImmutableSamplers = null
        };
        _bloomCompositeImmutableSamplers[0] = _linearSampler;
        fixed (Sampler* linPtr2 = _bloomCompositeImmutableSamplers)
        {
            bloomCompBinds[2] = new DescriptorSetLayoutBinding
            {
                Binding = 2,
                DescriptorType = DescriptorType.Sampler,
                DescriptorCount = 1,
                StageFlags = ShaderStageFlags.FragmentBit,
                PImmutableSamplers = linPtr2
            };
            bloomCompBinds[3] = new DescriptorSetLayoutBinding
            {
                Binding = 3,
                DescriptorType = DescriptorType.SampledImage,
                DescriptorCount = 1,
                StageFlags = ShaderStageFlags.FragmentBit,
                PImmutableSamplers = null
            };
            fixed (DescriptorSetLayoutBinding* bPtr = bloomCompBinds)
            {
                DescriptorSetLayoutCreateInfo layInfo = new()
                {
                    SType = StructureType.DescriptorSetLayoutCreateInfo,
                    BindingCount = 4,
                    PBindings = bPtr
                };
                fixed (DescriptorSetLayout* layPtr = &_bloomCompositeSetLayout)
                {
                    if (_vk!.CreateDescriptorSetLayout(_device, &layInfo, null, layPtr) != Result.Success)
                        throw new Exception("无法创建 bloomComposite set layout");
                }
            }
        }

        // Bloom tex set layout (bright + blur): b0 SampledImage, b1 Sampler(immutable linear)
        DescriptorSetLayoutBinding[] bloomTexBinds = new DescriptorSetLayoutBinding[2];
        bloomTexBinds[0] = new DescriptorSetLayoutBinding
        {
            Binding = 0,
            DescriptorType = DescriptorType.SampledImage,
            DescriptorCount = 1,
            StageFlags = ShaderStageFlags.FragmentBit,
            PImmutableSamplers = null
        };
        fixed (Sampler* linPtr3 = _bloomCompositeImmutableSamplers)
        {
            bloomTexBinds[1] = new DescriptorSetLayoutBinding
            {
                Binding = 1,
                DescriptorType = DescriptorType.Sampler,
                DescriptorCount = 1,
                StageFlags = ShaderStageFlags.FragmentBit,
                PImmutableSamplers = linPtr3
            };
            fixed (DescriptorSetLayoutBinding* bPtr = bloomTexBinds)
            {
                DescriptorSetLayoutCreateInfo layInfo = new()
                {
                    SType = StructureType.DescriptorSetLayoutCreateInfo,
                    BindingCount = 2,
                    PBindings = bPtr
                };
                fixed (DescriptorSetLayout* layPtr = &_bloomTexSetLayout)
                {
                    if (_vk!.CreateDescriptorSetLayout(_device, &layInfo, null, layPtr) != Result.Success)
                        throw new Exception("无法创建 bloomTex set layout");
                }
            }
        }

        // FXAA set layout: b0 UBO, b1 SampledImage, b2 Sampler(immutable linear)
        DescriptorSetLayoutBinding[] fxaaBinds = new DescriptorSetLayoutBinding[3];
        fxaaBinds[0] = new DescriptorSetLayoutBinding
        {
            Binding = 0,
            DescriptorType = DescriptorType.UniformBuffer,
            DescriptorCount = 1,
            StageFlags = ShaderStageFlags.FragmentBit,
            PImmutableSamplers = null
        };
        fxaaBinds[1] = new DescriptorSetLayoutBinding
        {
            Binding = 1,
            DescriptorType = DescriptorType.SampledImage,
            DescriptorCount = 1,
            StageFlags = ShaderStageFlags.FragmentBit,
            PImmutableSamplers = null
        };
        _fxaaImmutableSamplers[0] = _linearSampler;
        fixed (Sampler* linPtr4 = _fxaaImmutableSamplers)
        {
            fxaaBinds[2] = new DescriptorSetLayoutBinding
            {
                Binding = 2,
                DescriptorType = DescriptorType.Sampler,
                DescriptorCount = 1,
                StageFlags = ShaderStageFlags.FragmentBit,
                PImmutableSamplers = linPtr4
            };
            fixed (DescriptorSetLayoutBinding* bPtr = fxaaBinds)
            {
                DescriptorSetLayoutCreateInfo layInfo = new()
                {
                    SType = StructureType.DescriptorSetLayoutCreateInfo,
                    BindingCount = 3,
                    PBindings = bPtr
                };
                fixed (DescriptorSetLayout* layPtr = &_fxaaSetLayout)
                {
                    if (_vk!.CreateDescriptorSetLayout(_device, &layInfo, null, layPtr) != Result.Success)
                        throw new Exception("无法创建 FXAA set layout");
                }
            }
        }

        // Pool
        DescriptorPoolSize[] sizes = new[]
        {
            new DescriptorPoolSize { Type = DescriptorType.UniformBuffer, DescriptorCount = 10 },
            new DescriptorPoolSize { Type = DescriptorType.SampledImage, DescriptorCount = 30 },
            new DescriptorPoolSize { Type = DescriptorType.Sampler, DescriptorCount = 10 },
            new DescriptorPoolSize { Type = DescriptorType.StorageBuffer, DescriptorCount = 8 },
        };
        fixed (DescriptorPoolSize* sizesPtr = sizes)
        {
            DescriptorPoolCreateInfo poolInfo = new()
            {
                SType = StructureType.DescriptorPoolCreateInfo,
                PoolSizeCount = (uint)sizes.Length,
                PPoolSizes = sizesPtr,
                MaxSets = 28
            };
            fixed (DescriptorPool* poolPtr = &_descriptorPool)
            {
                if (_vk!.CreateDescriptorPool(_device, &poolInfo, null, poolPtr) != Result.Success)
                    throw new Exception("无法创建 descriptor pool");
            }
        }

        AllocateAndUpdateSets();
    }

    private void AllocateAndUpdateSets()
    {
        // GBuffer descriptor set
        DescriptorSet[] gbufSets = new DescriptorSet[1];
        DescriptorSetLayout[] gbufLayouts = new DescriptorSetLayout[] { _gbufferSetLayout };
        fixed (DescriptorSetLayout* gbufLayoutsPtr = gbufLayouts)
        fixed (DescriptorSet* gbufSetsPtr = gbufSets)
        {
            DescriptorSetAllocateInfo gbufAlloc = new()
            {
                SType = StructureType.DescriptorSetAllocateInfo,
                DescriptorPool = _descriptorPool,
                DescriptorSetCount = 1,
                PSetLayouts = gbufLayoutsPtr
            };
            _vk!.AllocateDescriptorSets(_device, &gbufAlloc, gbufSetsPtr);
            _gbufferDescriptorSet = gbufSets[0];
        }
        {
            DescriptorBufferInfo camInfo = new()
            {
                Buffer = _cameraUBO,
                Offset = 0,
                Range = 3UL * 64 + 16
            };
            WriteDescriptorSet write = new()
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = _gbufferDescriptorSet,
                DstBinding = 0,
                DstArrayElement = 0,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.UniformBuffer,
                PBufferInfo = &camInfo
            };
            _vk.UpdateDescriptorSets(_device, 1, in write, 0, null);
        }

        // Lighting descriptor set
        DescriptorSet[] lightSets = new DescriptorSet[1];
        DescriptorSetLayout[] lightLayouts = new DescriptorSetLayout[] { _lightingSetLayout };
        fixed (DescriptorSetLayout* lightLayoutsPtr = lightLayouts)
        fixed (DescriptorSet* lightSetsPtr = lightSets)
        {
            DescriptorSetAllocateInfo lightAlloc = new()
            {
                SType = StructureType.DescriptorSetAllocateInfo,
                DescriptorPool = _descriptorPool,
                DescriptorSetCount = 1,
                PSetLayouts = lightLayoutsPtr
            };
            _vk.AllocateDescriptorSets(_device, &lightAlloc, lightSetsPtr);
            _lightingDescriptorSet = lightSets[0];
        }
        {
            var bufferInfos = new DescriptorBufferInfo[3];
            bufferInfos[0] = new DescriptorBufferInfo { Buffer = _cameraUBO, Offset = 0, Range = 3UL * 64 + 16 };
            bufferInfos[1] = new DescriptorBufferInfo { Buffer = _lightUBO, Offset = 0, Range = 16UL * 3 + 16 + 4 * 64 };
            bufferInfos[2] = new DescriptorBufferInfo { Buffer = _resUBO, Offset = 0, Range = 16 };

            var imageInfos = new DescriptorImageInfo[10]; // 5 gbuffer + 4 shadow + 1 HDR (unused in lighting, but HDR isn't a light input)
            // bindings 3..7 = gbuffer (worldPos, albedo, normal, pbr, emissive)
            // bindings 8..11 = shadow depth
            for (int i = 0; i < 5; i++)
            {
                imageInfos[i] = new DescriptorImageInfo
                {
                    Sampler = default,
                    ImageView = _gbufferViews[i],
                    ImageLayout = ImageLayout.ShaderReadOnlyOptimal
                };
            }
            for (int i = 0; i < NumCascades; i++)
            {
                imageInfos[5 + i] = new DescriptorImageInfo
                {
                    Sampler = default,
                    ImageView = _shadowViews[i],
                    ImageLayout = ImageLayout.ShaderReadOnlyOptimal
                };
            }

            WriteDescriptorSet[] writes = new WriteDescriptorSet[12];
            fixed (DescriptorBufferInfo* bufPtr = bufferInfos)
            fixed (DescriptorImageInfo* imgPtr = imageInfos)
            {
                for (int i = 0; i < 3; i++)
                {
                    writes[i] = new WriteDescriptorSet
                    {
                        SType = StructureType.WriteDescriptorSet,
                        DstSet = _lightingDescriptorSet,
                        DstBinding = (uint)i,
                        DstArrayElement = 0,
                        DescriptorCount = 1,
                        DescriptorType = DescriptorType.UniformBuffer,
                        PBufferInfo = &bufPtr[i]
                    };
                }
                for (int i = 3; i < 12; i++)
                {
                    int idx = i - 3; // imageInfos index
                    writes[i] = new WriteDescriptorSet
                    {
                        SType = StructureType.WriteDescriptorSet,
                        DstSet = _lightingDescriptorSet,
                        DstBinding = (uint)i,
                        DstArrayElement = 0,
                        DescriptorCount = 1,
                        DescriptorType = DescriptorType.SampledImage,
                        PImageInfo = &imgPtr[idx]
                    };
                }
                fixed (WriteDescriptorSet* writesPtr = writes)
                    _vk.UpdateDescriptorSets(_device, (uint)writes.Length, writesPtr, 0, null);
            }
        }

        // Bloom composite descriptor set (HDR + bloom → LDR)
        {
            DescriptorSet[] sets = new DescriptorSet[1];
            DescriptorSetLayout[] layouts = new DescriptorSetLayout[] { _bloomCompositeSetLayout };
            fixed (DescriptorSetLayout* layoutsPtr = layouts)
            fixed (DescriptorSet* setsPtr = sets)
            {
                DescriptorSetAllocateInfo alloc = new()
                {
                    SType = StructureType.DescriptorSetAllocateInfo,
                    DescriptorPool = _descriptorPool,
                    DescriptorSetCount = 1,
                    PSetLayouts = layoutsPtr
                };
                _vk!.AllocateDescriptorSets(_device, &alloc, setsPtr);
                _bloomCompositeDescriptorSet = sets[0];
            }
            DescriptorBufferInfo expInfo = new() { Buffer = _exposureUBO, Offset = 0, Range = 16 };
            DescriptorImageInfo hdrInfo = new()
            {
                Sampler = default,
                ImageView = _hdrColorView,
                ImageLayout = ImageLayout.ShaderReadOnlyOptimal
            };
            DescriptorImageInfo bloomInfo = new()
            {
                Sampler = default,
                ImageView = _bloomFinalView,
                ImageLayout = ImageLayout.ShaderReadOnlyOptimal
            };
            WriteDescriptorSet[] writes = new WriteDescriptorSet[3];
            writes[0] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = _bloomCompositeDescriptorSet,
                DstBinding = 0,
                DstArrayElement = 0,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.UniformBuffer,
                PBufferInfo = &expInfo
            };
            writes[1] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = _bloomCompositeDescriptorSet,
                DstBinding = 1,
                DstArrayElement = 0,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.SampledImage,
                PImageInfo = &hdrInfo
            };
            writes[2] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = _bloomCompositeDescriptorSet,
                DstBinding = 3,
                DstArrayElement = 0,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.SampledImage,
                PImageInfo = &bloomInfo
            };
            fixed (WriteDescriptorSet* writesPtr = writes)
                _vk.UpdateDescriptorSets(_device, 3, writesPtr, 0, null);
        }

        // Bloom bright descriptor set (HDR → bloomBrightRT)
        {
            DescriptorSet[] sets = new DescriptorSet[1];
            DescriptorSetLayout[] layouts = new DescriptorSetLayout[] { _bloomTexSetLayout };
            fixed (DescriptorSetLayout* layoutsPtr = layouts)
            fixed (DescriptorSet* setsPtr = sets)
            {
                DescriptorSetAllocateInfo alloc = new()
                {
                    SType = StructureType.DescriptorSetAllocateInfo,
                    DescriptorPool = _descriptorPool,
                    DescriptorSetCount = 1,
                    PSetLayouts = layoutsPtr
                };
                _vk!.AllocateDescriptorSets(_device, &alloc, setsPtr);
                _bloomBrightDescSet = sets[0];
            }
            DescriptorImageInfo hdrInfo = new()
            {
                Sampler = default,
                ImageView = _hdrColorView,
                ImageLayout = ImageLayout.ShaderReadOnlyOptimal
            };
            WriteDescriptorSet write = new()
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = _bloomBrightDescSet,
                DstBinding = 0,
                DstArrayElement = 0,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.SampledImage,
                PImageInfo = &hdrInfo
            };
            _vk.UpdateDescriptorSets(_device, 1, in write, 0, null);
        }

        // Bloom blur H descriptor set (bloomBrightRT → bloomBlurHRT)
        {
            DescriptorSet[] sets = new DescriptorSet[1];
            DescriptorSetLayout[] layouts = new DescriptorSetLayout[] { _bloomTexSetLayout };
            fixed (DescriptorSetLayout* layoutsPtr = layouts)
            fixed (DescriptorSet* setsPtr = sets)
            {
                DescriptorSetAllocateInfo alloc = new()
                {
                    SType = StructureType.DescriptorSetAllocateInfo,
                    DescriptorPool = _descriptorPool,
                    DescriptorSetCount = 1,
                    PSetLayouts = layoutsPtr
                };
                _vk!.AllocateDescriptorSets(_device, &alloc, setsPtr);
                _bloomBlurHDescSet = sets[0];
            }
            DescriptorImageInfo srcInfo = new()
            {
                Sampler = default,
                ImageView = _bloomBrightView,
                ImageLayout = ImageLayout.ShaderReadOnlyOptimal
            };
            WriteDescriptorSet write = new()
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = _bloomBlurHDescSet,
                DstBinding = 0,
                DstArrayElement = 0,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.SampledImage,
                PImageInfo = &srcInfo
            };
            _vk.UpdateDescriptorSets(_device, 1, in write, 0, null);
        }

        // Bloom blur V descriptor set (bloomBlurHRT → bloomFinalRT)
        {
            DescriptorSet[] sets = new DescriptorSet[1];
            DescriptorSetLayout[] layouts = new DescriptorSetLayout[] { _bloomTexSetLayout };
            fixed (DescriptorSetLayout* layoutsPtr = layouts)
            fixed (DescriptorSet* setsPtr = sets)
            {
                DescriptorSetAllocateInfo alloc = new()
                {
                    SType = StructureType.DescriptorSetAllocateInfo,
                    DescriptorPool = _descriptorPool,
                    DescriptorSetCount = 1,
                    PSetLayouts = layoutsPtr
                };
                _vk!.AllocateDescriptorSets(_device, &alloc, setsPtr);
                _bloomBlurVDescSet = sets[0];
            }
            DescriptorImageInfo srcInfo = new()
            {
                Sampler = default,
                ImageView = _bloomBlurHView,
                ImageLayout = ImageLayout.ShaderReadOnlyOptimal
            };
            WriteDescriptorSet write = new()
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = _bloomBlurVDescSet,
                DstBinding = 0,
                DstArrayElement = 0,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.SampledImage,
                PImageInfo = &srcInfo
            };
            _vk.UpdateDescriptorSets(_device, 1, in write, 0, null);
        }

        // FXAA descriptor set (ldrRT → swapchain)
        {
            DescriptorSet[] sets = new DescriptorSet[1];
            DescriptorSetLayout[] layouts = new DescriptorSetLayout[] { _fxaaSetLayout };
            fixed (DescriptorSetLayout* layoutsPtr = layouts)
            fixed (DescriptorSet* setsPtr = sets)
            {
                DescriptorSetAllocateInfo alloc = new()
                {
                    SType = StructureType.DescriptorSetAllocateInfo,
                    DescriptorPool = _descriptorPool,
                    DescriptorSetCount = 1,
                    PSetLayouts = layoutsPtr
                };
                _vk!.AllocateDescriptorSets(_device, &alloc, setsPtr);
                _fxaaDescSet = sets[0];
            }
            DescriptorBufferInfo fxaaInfo = new() { Buffer = _fxaaUBO, Offset = 0, Range = 16 };
            DescriptorImageInfo ldrInfo = new()
            {
                Sampler = default,
                ImageView = _ldrRTView,
                ImageLayout = ImageLayout.ShaderReadOnlyOptimal
            };
            WriteDescriptorSet[] writes = new WriteDescriptorSet[2];
            writes[0] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = _fxaaDescSet,
                DstBinding = 0,
                DstArrayElement = 0,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.UniformBuffer,
                PBufferInfo = &fxaaInfo
            };
            writes[1] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = _fxaaDescSet,
                DstBinding = 1,
                DstArrayElement = 0,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.SampledImage,
                PImageInfo = &ldrInfo
            };
            fixed (WriteDescriptorSet* writesPtr = writes)
                _vk.UpdateDescriptorSets(_device, 2, writesPtr, 0, null);
        }
    }

    // ════════════════════════════════════════════════════════════════
    // Pipelines
    // ════════════════════════════════════════════════════════════════
    private void CreatePipelines()
    {
        string baseDir = AppContext.BaseDirectory;
        string shaderDir = Path.Combine(baseDir, "Shaders");
        var dxc = new YamaEngine.Shaders.DxcCompiler(
            shaderDir, cacheDir: Path.Combine(baseDir, "ShaderCache"), useDebug: false);
        if (!dxc.IsAvailable)
            throw new Exception("DxcCompiler 不可用");

        string LoadSrc(string name) => File.ReadAllText(Path.Combine(shaderDir, name));
        ShaderModule CompileVS(string file, string entry)
            => CreateShaderModule(ToUIntArray(dxc.Compile(LoadSrc(file), entry,
                YamaEngine.RHI.RHIShaderStage.Vertex, YamaEngine.RHI.RHIShaderModel.SM_6_0).Bytecode));
        ShaderModule CompilePS(string file, string entry)
            => CreateShaderModule(ToUIntArray(dxc.Compile(LoadSrc(file), entry,
                YamaEngine.RHI.RHIShaderStage.Pixel, YamaEngine.RHI.RHIShaderModel.SM_6_0).Bytecode));

        // ── Shadow pipeline ──
        {
            var vs = CompileVS("CSMShadow.vert.hlsl", "VSMain");
            var fs = CompilePS("CSMShadow.frag.hlsl", "PSMain");
            PipelineShaderStageCreateInfo vStage = new()
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.VertexBit, Module = vs,
                PName = (byte*)SilkMarshal.StringToPtr("VSMain", NativeStringEncoding.UTF8)
            };
            PipelineShaderStageCreateInfo fStage = new()
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.FragmentBit, Module = fs,
                PName = (byte*)SilkMarshal.StringToPtr("PSMain", NativeStringEncoding.UTF8)
            };
            var stages = stackalloc PipelineShaderStageCreateInfo[2];
            stages[0] = vStage; stages[1] = fStage;

            VertexInputBindingDescription vbind = new()
            {
                Binding = 0, Stride = 44, InputRate = VertexInputRate.Vertex
            };
            VertexInputAttributeDescription[] attrs = new VertexInputAttributeDescription[1];
            attrs[0] = new VertexInputAttributeDescription
            {
                Binding = 0, Location = 0, Format = Format.R32G32B32Sfloat, Offset = 0
            };
            PipelineVertexInputStateCreateInfo vin = new()
            {
                SType = StructureType.PipelineVertexInputStateCreateInfo,
                VertexBindingDescriptionCount = 1,
                PVertexBindingDescriptions = &vbind,
                VertexAttributeDescriptionCount = 1
            };
            fixed (VertexInputAttributeDescription* attrPtr = attrs)
            {
                vin.PVertexAttributeDescriptions = attrPtr;
                PipelineInputAssemblyStateCreateInfo ia = new()
                {
                    SType = StructureType.PipelineInputAssemblyStateCreateInfo,
                    Topology = PrimitiveTopology.TriangleList
                };
                Viewport vp = new(0, 0, ShadowMapSize, ShadowMapSize, 0, 1);
                Rect2D sc = new(new Offset2D(0, 0), new Extent2D(ShadowMapSize, ShadowMapSize));
                PipelineViewportStateCreateInfo vps = new()
                {
                    SType = StructureType.PipelineViewportStateCreateInfo,
                    ViewportCount = 1, PViewports = &vp,
                    ScissorCount = 1, PScissors = &sc
                };
                PipelineRasterizationStateCreateInfo rs = new()
                {
                    SType = StructureType.PipelineRasterizationStateCreateInfo,
                    PolygonMode = PolygonMode.Fill,
                    CullMode = CullModeFlags.FrontBit,
                    FrontFace = FrontFace.CounterClockwise,
                    DepthBiasEnable = true,
                    DepthBiasConstantFactor = 2.0f,
                    DepthBiasSlopeFactor = 4.0f,
                    LineWidth = 1.0f
                };
                PipelineMultisampleStateCreateInfo ms = new()
                {
                    SType = StructureType.PipelineMultisampleStateCreateInfo,
                    RasterizationSamples = SampleCountFlags.Count1Bit
                };
                PipelineDepthStencilStateCreateInfo ds = new()
                {
                    SType = StructureType.PipelineDepthStencilStateCreateInfo,
                    DepthTestEnable = true,
                    DepthWriteEnable = true,
                    DepthCompareOp = CompareOp.Less,
                    MinDepthBounds = 0, MaxDepthBounds = 1
                };
                PipelineColorBlendStateCreateInfo cb = new()
                {
                    SType = StructureType.PipelineColorBlendStateCreateInfo,
                    AttachmentCount = 0,
                    PAttachments = null
                };

                _shadowPCRanges[0] = new PushConstantRange
                {
                    StageFlags = ShaderStageFlags.VertexBit,
                    Offset = 0, Size = 128
                };
                fixed (PushConstantRange* pcPtr = _shadowPCRanges)
                {
                    PipelineLayoutCreateInfo layInfo = new()
                    {
                        SType = StructureType.PipelineLayoutCreateInfo,
                        PushConstantRangeCount = 1,
                        PPushConstantRanges = pcPtr
                    };
                    fixed (PipelineLayout* layPtr = &_shadowPipelineLayout)
                    {
                        if (_vk!.CreatePipelineLayout(_device, &layInfo, null, layPtr) != Result.Success)
                            throw new Exception("无法创建 shadow pipeline layout");
                    }
                    GraphicsPipelineCreateInfo pipeInfo = new()
                    {
                        SType = StructureType.GraphicsPipelineCreateInfo,
                        StageCount = 2, PStages = stages,
                        PVertexInputState = &vin,
                        PInputAssemblyState = &ia,
                        PViewportState = &vps,
                        PRasterizationState = &rs,
                        PMultisampleState = &ms,
                        PDepthStencilState = &ds,
                        PColorBlendState = &cb,
                        Layout = _shadowPipelineLayout,
                        RenderPass = _shadowRenderPass,
                        Subpass = 0
                    };
                    fixed (Pipeline* pipePtr = &_shadowPipeline)
                    {
                        var r = _vk.CreateGraphicsPipelines(_device, default, 1, &pipeInfo, null, pipePtr);
                        if (r != Result.Success) throw new Exception($"无法创建 shadow pipeline: {r}");
                    }
                }
            }
            _vk!.DestroyShaderModule(_device, vs, null);
            _vk.DestroyShaderModule(_device, fs, null);
            SilkMarshal.Free((nint)vStage.PName);
            SilkMarshal.Free((nint)fStage.PName);
        }

        // ── GBuffer pipeline ──
        {
            var vs = CompileVS("DeferredPBR.vert.hlsl", "VSMain");
            var fs = CompilePS("DeferredPBR.frag.hlsl", "PSMain");
            PipelineShaderStageCreateInfo vStage = new()
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.VertexBit, Module = vs,
                PName = (byte*)SilkMarshal.StringToPtr("VSMain", NativeStringEncoding.UTF8)
            };
            PipelineShaderStageCreateInfo fStage = new()
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.FragmentBit, Module = fs,
                PName = (byte*)SilkMarshal.StringToPtr("PSMain", NativeStringEncoding.UTF8)
            };
            var stages = stackalloc PipelineShaderStageCreateInfo[2];
            stages[0] = vStage; stages[1] = fStage;

            VertexInputBindingDescription vbind = new()
            {
                Binding = 0, Stride = 44, InputRate = VertexInputRate.Vertex
            };
            VertexInputAttributeDescription[] attrs = new VertexInputAttributeDescription[3];
            attrs[0] = new() { Binding = 0, Location = 0, Format = Format.R32G32B32Sfloat, Offset = 0 };
            attrs[1] = new() { Binding = 0, Location = 1, Format = Format.R32G32B32Sfloat, Offset = 12 };
            attrs[2] = new() { Binding = 0, Location = 2, Format = Format.R32G32Sfloat, Offset = 24 };
            PipelineVertexInputStateCreateInfo vin = new()
            {
                SType = StructureType.PipelineVertexInputStateCreateInfo,
                VertexBindingDescriptionCount = 1,
                PVertexBindingDescriptions = &vbind,
                VertexAttributeDescriptionCount = 3
            };
            fixed (VertexInputAttributeDescription* attrPtr = attrs)
            {
                vin.PVertexAttributeDescriptions = attrPtr;
                PipelineInputAssemblyStateCreateInfo ia = new()
                {
                    SType = StructureType.PipelineInputAssemblyStateCreateInfo,
                    Topology = PrimitiveTopology.TriangleList
                };
                uint w = _swapchainExtent.Width, h = _swapchainExtent.Height;
                Viewport vp = new(0, 0, w, h, 0, 1);
                Rect2D sc = new(new Offset2D(0, 0), new Extent2D(w, h));
                PipelineViewportStateCreateInfo vps = new()
                {
                    SType = StructureType.PipelineViewportStateCreateInfo,
                    ViewportCount = 1, PViewports = &vp,
                    ScissorCount = 1, PScissors = &sc
                };
                PipelineRasterizationStateCreateInfo rs = new()
                {
                    SType = StructureType.PipelineRasterizationStateCreateInfo,
                    PolygonMode = PolygonMode.Fill,
                    CullMode = CullModeFlags.None, // 首次 bring-up 用 None 避免不可见 bug
                    FrontFace = FrontFace.CounterClockwise,
                    LineWidth = 1.0f
                };
                PipelineMultisampleStateCreateInfo ms = new()
                {
                    SType = StructureType.PipelineMultisampleStateCreateInfo,
                    RasterizationSamples = SampleCountFlags.Count1Bit
                };
                PipelineDepthStencilStateCreateInfo ds = new()
                {
                    SType = StructureType.PipelineDepthStencilStateCreateInfo,
                    DepthTestEnable = true,
                    DepthWriteEnable = true,
                    DepthCompareOp = CompareOp.Less,
                    MinDepthBounds = 0, MaxDepthBounds = 1
                };
                PipelineColorBlendAttachmentState[] blendAtts = new PipelineColorBlendAttachmentState[5];
                for (int i = 0; i < 5; i++)
                {
                    blendAtts[i] = new PipelineColorBlendAttachmentState
                    {
                        BlendEnable = false,
                        ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit |
                                         ColorComponentFlags.BBit | ColorComponentFlags.ABit
                    };
                }
                PipelineColorBlendStateCreateInfo cb = new()
                {
                    SType = StructureType.PipelineColorBlendStateCreateInfo,
                    AttachmentCount = 5
                };
                fixed (PipelineColorBlendAttachmentState* blendPtr = blendAtts)
                {
                    cb.PAttachments = blendPtr;
                    _gbufferPCRanges[0] = new PushConstantRange
                    {
                        StageFlags = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit,
                        Offset = 0, Size = 96
                    };
                    fixed (PushConstantRange* pcPtr = _gbufferPCRanges)
                    fixed (DescriptorSetLayout* gbufSetLayoutPtr = _gbufferSetLayouts)
                    {
                        PipelineLayoutCreateInfo layInfo = new()
                        {
                            SType = StructureType.PipelineLayoutCreateInfo,
                            SetLayoutCount = (uint)_gbufferSetLayouts.Length,
                            PSetLayouts = gbufSetLayoutPtr,
                            PushConstantRangeCount = 1,
                            PPushConstantRanges = pcPtr
                        };
                        fixed (PipelineLayout* layPtr = &_gbufferPipelineLayout)
                        {
                            if (_vk!.CreatePipelineLayout(_device, &layInfo, null, layPtr) != Result.Success)
                                throw new Exception("无法创建 gbuffer pipeline layout");
                        }
                        GraphicsPipelineCreateInfo pipeInfo = new()
                        {
                            SType = StructureType.GraphicsPipelineCreateInfo,
                            StageCount = 2, PStages = stages,
                            PVertexInputState = &vin,
                            PInputAssemblyState = &ia,
                            PViewportState = &vps,
                            PRasterizationState = &rs,
                            PMultisampleState = &ms,
                            PDepthStencilState = &ds,
                            PColorBlendState = &cb,
                            Layout = _gbufferPipelineLayout,
                            RenderPass = _gbufferRenderPass,
                            Subpass = 0
                        };
                        fixed (Pipeline* pipePtr = &_gbufferPipeline)
                        {
                            var r = _vk.CreateGraphicsPipelines(_device, default, 1, &pipeInfo, null, pipePtr);
                            if (r != Result.Success) throw new Exception($"无法创建 gbuffer pipeline: {r}");
                        }
                    }
                }
            }
            _vk!.DestroyShaderModule(_device, vs, null);
            _vk.DestroyShaderModule(_device, fs, null);
            SilkMarshal.Free((nint)vStage.PName);
            SilkMarshal.Free((nint)fStage.PName);
        }

        // ── Lighting pipeline ──
        CreateFullscreenPipeline(
            "DeferredLighting.frag.hlsl", "PSMain",
            _lightingSetLayout, ref _lightingPipelineLayout, ref _lightingPipeline,
            _lightingRenderPass, depthEnable: false, pcSize: 0, dynamicViewport: false);

        // ── Bloom composite pipeline (HDR + bloom → ldrRT, full-res, no push constants) ──
        CreateFullscreenPipeline(
            "PostProcess.frag.hlsl", "PSMain",
            _bloomCompositeSetLayout, ref _bloomCompositeLayout, ref _bloomCompositePipeline,
            _bloomCompositeRP, depthEnable: false, pcSize: 0, dynamicViewport: false);

        // ── Bloom bright pipeline (HDR → bloomBrightRT, half-res, push constants) ──
        CreateFullscreenPipeline(
            "BloomBright.frag.hlsl", "PSMain",
            _bloomTexSetLayout, ref _bloomBrightLayout, ref _bloomBrightPipeline,
            _bloomBrightRP, depthEnable: false, pcSize: 16, dynamicViewport: true);

        // ── Bloom blur pipeline (bloomBlurH/bloomFinal, half-res, push constants) ──
        CreateFullscreenPipeline(
            "BloomBlur.frag.hlsl", "PSMain",
            _bloomTexSetLayout, ref _bloomBlurLayout, ref _bloomBlurPipeline,
            _bloomBlurRP, depthEnable: false, pcSize: 16, dynamicViewport: true);

        // ── FXAA pipeline (ldrRT → swapchain, full-res, no push constants) ──
        CreateFullscreenPipeline(
            "FXAA.frag.hlsl", "PSMain",
            _fxaaSetLayout, ref _fxaaLayout, ref _fxaaPipeline,
            _fxaaRP, depthEnable: false, pcSize: 0, dynamicViewport: false);
    }

    private void CreateFullscreenPipeline(string fragFile, string fragEntry,
        DescriptorSetLayout setLayout, ref PipelineLayout layout, ref Pipeline pipeline,
        RenderPass renderPass, bool depthEnable, uint pcSize, bool dynamicViewport)
    {
        string baseDir = AppContext.BaseDirectory;
        string shaderDir = Path.Combine(baseDir, "Shaders");
        var dxc = new YamaEngine.Shaders.DxcCompiler(
            shaderDir, cacheDir: Path.Combine(baseDir, "ShaderCache"), useDebug: false);

        var vs = CreateShaderModule(ToUIntArray(dxc.Compile(
            File.ReadAllText(Path.Combine(shaderDir, "Fullscreen.vert.hlsl")), "VSMain",
            YamaEngine.RHI.RHIShaderStage.Vertex, YamaEngine.RHI.RHIShaderModel.SM_6_0).Bytecode));
        var fs = CreateShaderModule(ToUIntArray(dxc.Compile(
            File.ReadAllText(Path.Combine(shaderDir, fragFile)), fragEntry,
            YamaEngine.RHI.RHIShaderStage.Pixel, YamaEngine.RHI.RHIShaderModel.SM_6_0).Bytecode));

        PipelineShaderStageCreateInfo vStage = new()
        {
            SType = StructureType.PipelineShaderStageCreateInfo,
            Stage = ShaderStageFlags.VertexBit, Module = vs,
            PName = (byte*)SilkMarshal.StringToPtr("VSMain", NativeStringEncoding.UTF8)
        };
        PipelineShaderStageCreateInfo fStage = new()
        {
            SType = StructureType.PipelineShaderStageCreateInfo,
            Stage = ShaderStageFlags.FragmentBit, Module = fs,
            PName = (byte*)SilkMarshal.StringToPtr(fragEntry, NativeStringEncoding.UTF8)
        };
        var stages = stackalloc PipelineShaderStageCreateInfo[2];
        stages[0] = vStage; stages[1] = fStage;

        PipelineVertexInputStateCreateInfo vin = new()
        {
            SType = StructureType.PipelineVertexInputStateCreateInfo
        };
        PipelineInputAssemblyStateCreateInfo ia = new()
        {
            SType = StructureType.PipelineInputAssemblyStateCreateInfo,
            Topology = PrimitiveTopology.TriangleList
        };
        uint w = _swapchainExtent.Width, h = _swapchainExtent.Height;
        Viewport vp = new(0, 0, w, h, 0, 1);
        Rect2D sc = new(new Offset2D(0, 0), new Extent2D(w, h));
        PipelineViewportStateCreateInfo vps = new()
        {
            SType = StructureType.PipelineViewportStateCreateInfo,
            ViewportCount = 1, PViewports = &vp,
            ScissorCount = 1, PScissors = &sc
        };
        PipelineRasterizationStateCreateInfo rs = new()
        {
            SType = StructureType.PipelineRasterizationStateCreateInfo,
            PolygonMode = PolygonMode.Fill,
            CullMode = CullModeFlags.None,
            LineWidth = 1.0f
        };
        PipelineMultisampleStateCreateInfo ms = new()
        {
            SType = StructureType.PipelineMultisampleStateCreateInfo,
            RasterizationSamples = SampleCountFlags.Count1Bit
        };
        PipelineDepthStencilStateCreateInfo ds = new()
        {
            SType = StructureType.PipelineDepthStencilStateCreateInfo,
            DepthTestEnable = false,
            DepthWriteEnable = false
        };
        PipelineColorBlendAttachmentState blendAtt = new()
        {
            BlendEnable = false,
            ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit |
                             ColorComponentFlags.BBit | ColorComponentFlags.ABit
        };
        PipelineColorBlendStateCreateInfo cb = new()
        {
            SType = StructureType.PipelineColorBlendStateCreateInfo,
            AttachmentCount = 1,
            PAttachments = &blendAtt
        };

        PushConstantRange pcRange = new()
        {
            StageFlags = ShaderStageFlags.FragmentBit,
            Offset = 0, Size = pcSize
        };
        DynamicState* dynStates = stackalloc DynamicState[]
        {
            DynamicState.Viewport, DynamicState.Scissor
        };
        PipelineDynamicStateCreateInfo dynamicState = new()
        {
            SType = StructureType.PipelineDynamicStateCreateInfo,
            DynamicStateCount = 2,
            PDynamicStates = dynStates
        };

        PipelineLayoutCreateInfo layInfo = new()
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 1,
            PSetLayouts = &setLayout,
            PushConstantRangeCount = pcSize > 0 ? 1u : 0u,
            PPushConstantRanges = pcSize > 0 ? &pcRange : null
        };
        fixed (PipelineLayout* layPtr = &layout)
        {
            if (_vk!.CreatePipelineLayout(_device, &layInfo, null, layPtr) != Result.Success)
                throw new Exception("无法创建 fullscreen pipeline layout");

            GraphicsPipelineCreateInfo pipeInfo = new()
            {
                SType = StructureType.GraphicsPipelineCreateInfo,
                StageCount = 2, PStages = stages,
                PVertexInputState = &vin,
                PInputAssemblyState = &ia,
                PViewportState = &vps,
                PRasterizationState = &rs,
                PMultisampleState = &ms,
                PDepthStencilState = &ds,
                PColorBlendState = &cb,
                Layout = layout,
                RenderPass = renderPass,
                Subpass = 0
            };
            if (dynamicViewport)
                pipeInfo.PDynamicState = &dynamicState;
            fixed (Pipeline* pipePtr = &pipeline)
            {
                var r = _vk.CreateGraphicsPipelines(_device, default, 1, &pipeInfo, null, pipePtr);
                if (r != Result.Success) throw new Exception($"无法创建 fullscreen pipeline: {r}");
            }
        }
        _vk!.DestroyShaderModule(_device, vs, null);
        _vk.DestroyShaderModule(_device, fs, null);
        SilkMarshal.Free((nint)vStage.PName);
        SilkMarshal.Free((nint)fStage.PName);
    }

    // ════════════════════════════════════════════════════════════════
    // Per-frame uniform update
    // ════════════════════════════════════════════════════════════════
    private void UpdateUniforms()
    {
        float radius = 20.0f;
        float angle = _time * 0.1f;
        _cameraPos = new Vector3(
            _cameraTarget.X + MathF.Sin(angle) * radius,
            _cameraTarget.Y + 8.0f,
            _cameraTarget.Z + MathF.Cos(angle) * radius);

        Matrix4x4 view = Matrix4x4.CreateLookAt(_cameraPos, _cameraTarget, Vector3.UnitY);
        float aspect = (float)_swapchainExtent.Width / (float)_swapchainExtent.Height;
        Matrix4x4 proj = Matrix4x4.CreatePerspectiveFieldOfView(
            MathF.PI / 3.0f, aspect, 0.1f, 200.0f);
        Matrix4x4 viewProj = view * proj;
        _lastViewProj = viewProj;

        Matrix4x4[] camMats = new[] { view, proj, viewProj };
        Span<byte> camBytes = stackalloc byte[3 * 64 + 16];
        for (int i = 0; i < 3; i++)
        {
            ReadOnlySpan<byte> mBytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref camMats[i], 1));
            mBytes.CopyTo(camBytes.Slice(i * 64, 64));
        }
        Vector4 camPosV = new(_cameraPos, 0);
        MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref camPosV, 1)).CopyTo(camBytes.Slice(192, 16));
        fixed (byte* p = camBytes)
            WriteUBO(_cameraUBOPtr, camBytes, 3UL * 64 + 16);

        Vector3 lightDirUp = Vector3.Normalize(-_lightDir);
        float[] cascadeFars = new[] { 10.0f, 25.0f, 50.0f, 100.0f };
        Matrix4x4[] lightVPs = new Matrix4x4[NumCascades];
        for (int c = 0; c < NumCascades; c++)
        {
            float far = cascadeFars[c];
            Vector3 lightEye = _cameraTarget + lightDirUp * far;
            var lookAt = Matrix4x4.CreateLookAt(lightEye, _cameraTarget, Vector3.UnitY);
            var ortho = Matrix4x4.CreateOrthographic(far * 1.5f, far * 1.5f, 0.1f, far * 2.0f);
            lightVPs[c] = lookAt * ortho;
        }

        Vector4 lightDirV = new(_lightDir, 1.0f);
        Vector4 lightColorV = new(1.0f, 0.95f, 0.9f, 1.0f);
        Vector4 cascadeSplitsV = new(cascadeFars[0], cascadeFars[1], cascadeFars[2], cascadeFars[3]);
        float texelSize = 1.0f / (float)ShadowMapSize;
        Vector4 shadowParamsV = new(texelSize, 0.002f, 0, 0);

        Span<byte> lightBytes = stackalloc byte[16 * 4 + 4 * 64];
        MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref lightDirV, 1)).CopyTo(lightBytes.Slice(0, 16));
        MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref lightColorV, 1)).CopyTo(lightBytes.Slice(16, 16));
        MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref cascadeSplitsV, 1)).CopyTo(lightBytes.Slice(32, 16));
        MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref shadowParamsV, 1)).CopyTo(lightBytes.Slice(48, 16));
        for (int c = 0; c < NumCascades; c++)
        {
            ReadOnlySpan<byte> mBytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref lightVPs[c], 1));
            mBytes.CopyTo(lightBytes.Slice(64 + c * 64, 64));
        }
        fixed (byte* p = lightBytes)
            WriteUBO(_lightUBOPtr, lightBytes, 16UL * 4 + 4 * 64);

        Vector4 resV = new(_swapchainExtent.Width, _swapchainExtent.Height,
            1.0f / _swapchainExtent.Width, 1.0f / _swapchainExtent.Height);
        Span<byte> resBytes = stackalloc byte[16];
        MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref resV, 1)).CopyTo(resBytes);
        fixed (byte* p = resBytes)
            WriteUBO(_resUBOPtr, resBytes, 16);

        Vector4 expV = new(1.0f, 2.2f, 0.6f, 0);
        Span<byte> expBytes = stackalloc byte[16];
        MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref expV, 1)).CopyTo(expBytes);
        fixed (byte* p = expBytes)
            WriteUBO(_exposureUBOPtr, expBytes, 16);
    }

    // ════════════════════════════════════════════════════════════════
    // Command buffer build (all 5 passes per swapchain image)
    // ════════════════════════════════════════════════════════════════
    private void BuildCommandBuffer(int imageIndex)
    {
        var cmd = _commandBuffers[imageIndex];
        _vk!.ResetCommandBuffer(cmd, 0);
        CommandBufferBeginInfo beginInfo = new()
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = 0
        };
        _vk.BeginCommandBuffer(cmd, &beginInfo);

        uint w = _swapchainExtent.Width, h = _swapchainExtent.Height;

        Matrix4x4[] lightVPs = GetCurrentLightVPs();

        // Pass 0: Shadow cascades
        for (int c = 0; c < NumCascades; c++)
        {
            ClearValue clearDepth = new()
            {
                DepthStencil = new ClearDepthStencilValue(1.0f, 0)
            };
            RenderPassBeginInfo rpBegin = new()
            {
                SType = StructureType.RenderPassBeginInfo,
                RenderPass = _shadowRenderPass,
                Framebuffer = _shadowFramebuffers[c],
                RenderArea = new Rect2D(new Offset2D(0, 0), new Extent2D(ShadowMapSize, ShadowMapSize)),
                ClearValueCount = 1,
                PClearValues = &clearDepth
            };
            _vk.CmdBeginRenderPass(cmd, &rpBegin, SubpassContents.Inline);
            _vk.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _shadowPipeline);

            for (int m = 0; m < _meshDraws.Length; m++)
            {
                var mesh = _meshDraws[m];
                fixed (byte* lvpPtr = MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref lightVPs[c], 1)))
                    _vk.CmdPushConstants(cmd, _shadowPipelineLayout, ShaderStageFlags.VertexBit, 0, 64, lvpPtr);
                fixed (byte* worldPtr = MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref mesh.World, 1)))
                    _vk.CmdPushConstants(cmd, _shadowPipelineLayout, ShaderStageFlags.VertexBit, 64, 64, worldPtr);
                ulong vboOffsets = 0;
                fixed (Buffer* vboPtr = &_vertexBuffer)
                    _vk.CmdBindVertexBuffers(cmd, 0, 1, vboPtr, &vboOffsets);
                _vk.CmdBindIndexBuffer(cmd, _indexBuffer, mesh.IndexOffset * sizeof(uint), IndexType.Uint32);
                _vk.CmdDrawIndexed(cmd, mesh.IndexCount, 1, 0, 0, 0);
            }
            _vk.CmdEndRenderPass(cmd);
        }

        // GPU-driven frustum cull: clear count, dispatch, barrier, then indirect draw in GBuffer
        if (_gpuDriven)
        {
            // C1: clear visible count (compute shader will InterlockedAdd it)
            _vk.CmdFillBuffer(cmd, _visibleCountBuffer, 0, 4, 0);
            // Barrier: TransferWrite (fill + items upload) -> shader read/write
            // (items SSBO 的上传在更早的提交里，用 AllCommands 覆盖；VS/FS 也会读 items SSBO)
            BufferMemoryBarrier[] preBarrs = new BufferMemoryBarrier[2];
            preBarrs[0] = new BufferMemoryBarrier
            {
                SType = StructureType.BufferMemoryBarrier,
                SrcAccessMask = AccessFlags.TransferWriteBit,
                DstAccessMask = AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit,
                SrcQueueFamilyIndex = uint.MaxValue, DstQueueFamilyIndex = uint.MaxValue,
                Buffer = _visibleCountBuffer, Offset = 0, Size = Vk.WholeSize
            };
            preBarrs[1] = new BufferMemoryBarrier
            {
                SType = StructureType.BufferMemoryBarrier,
                SrcAccessMask = AccessFlags.MemoryWriteBit,
                DstAccessMask = AccessFlags.ShaderReadBit,
                SrcQueueFamilyIndex = uint.MaxValue, DstQueueFamilyIndex = uint.MaxValue,
                Buffer = _renderItemsSSBO, Offset = 0, Size = Vk.WholeSize
            };
            fixed (BufferMemoryBarrier* p = preBarrs)
                _vk.CmdPipelineBarrier(cmd,
                    PipelineStageFlags.TransferBit | PipelineStageFlags.AllCommandsBit,
                    PipelineStageFlags.ComputeShaderBit | PipelineStageFlags.VertexShaderBit
                        | PipelineStageFlags.FragmentShaderBit, 0,
                    0, null, 2, p, 0, null);

            // C2: dispatch cull（视锥平面 + itemCount 经 push constants 传入）
            _vk.CmdBindPipeline(cmd, PipelineBindPoint.Compute, _cullPipeline);
            _vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, _cullLayout, 0, 1, in _cullSet, 0, null);
            CullPush push = default;
            ExtractFrustumPlanes(_lastViewProj, out push.P0, out push.P1, out push.P2,
                out push.P3, out push.P4, out push.P5);
            push.ItemCount = (uint)_renderItems.Length;
            byte* pushBytes = stackalloc byte[112];
            MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref push, 1)).CopyTo(new Span<byte>(pushBytes, 112));
            _vk.CmdPushConstants(cmd, _cullLayout, ShaderStageFlags.ComputeBit, 0, 112, pushBytes);
            uint groups = ((uint)_renderItems.Length + 63) / 64;
            _vk.CmdDispatch(cmd, groups, 1, 1);

            // C3: barrier compute write -> draw indirect read (indirect commands buffer)
            // C4: items SSBO 仅初始化时上传，无需逐帧 compute->VS 屏障（上方屏障已覆盖）
            BufferMemoryBarrier postBarr = new BufferMemoryBarrier
            {
                SType = StructureType.BufferMemoryBarrier,
                SrcAccessMask = AccessFlags.ShaderWriteBit,
                DstAccessMask = AccessFlags.IndirectCommandReadBit,
                SrcQueueFamilyIndex = uint.MaxValue, DstQueueFamilyIndex = uint.MaxValue,
                Buffer = _indirectCmdsBuffer, Offset = 0, Size = Vk.WholeSize
            };
            BufferMemoryBarrier* pPost = &postBarr; // 局部变量无需 fixed
            _vk.CmdPipelineBarrier(cmd,
                PipelineStageFlags.ComputeShaderBit,
                PipelineStageFlags.DrawIndirectBit, 0,
                0, null, 1, pPost, 0, null);
        }

        // Pass 1: GBuffer
        {
            ClearValue[] clears = new ClearValue[6];
            for (int i = 0; i < 5; i++)
                clears[i] = new ClearValue { Color = new ClearColorValue(0f, 0f, 0f, 1f) };
            clears[5] = new ClearValue { DepthStencil = new ClearDepthStencilValue(1.0f, 0) };
            fixed (ClearValue* clearsPtr = clears)
            {
                RenderPassBeginInfo rpBegin = new()
                {
                    SType = StructureType.RenderPassBeginInfo,
                    RenderPass = _gbufferRenderPass,
                    Framebuffer = _gbufferFramebuffer,
                    RenderArea = new Rect2D(new Offset2D(0, 0), new Extent2D(w, h)),
                    ClearValueCount = 6,
                    PClearValues = clearsPtr
                };
                _vk.CmdBeginRenderPass(cmd, &rpBegin, SubpassContents.Inline);
            }
            _vk.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _gbufferPipeline);
            if (_gpuDriven)
            {
                DescriptorSet[] two = new[] { _gbufferDescriptorSet, _itemsSet };
                fixed (DescriptorSet* p = two)
                    _vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _gbufferPipelineLayout, 0, 2,
                        p, 0, null);
            }
            else
            {
                _vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _gbufferPipelineLayout, 0, 1,
                    in _gbufferDescriptorSet, 0, null);
            }

            byte* pcBuf = stackalloc byte[96];
            if (_gpuDriven)
            {
                // Bind vertex/index buffers once, then a single indirect multi-draw.
                // 被剔除的命令 instanceCount=0，GPU 自动跳过；firstInstance=itemIndex+1 供 VS 读 SSBO
                ulong vboOffsets = 0;
                fixed (Buffer* vboPtr = &_vertexBuffer)
                    _vk.CmdBindVertexBuffers(cmd, 0, 1, vboPtr, &vboOffsets);
                _vk.CmdBindIndexBuffer(cmd, _indexBuffer, 0, IndexType.Uint32);
                _vk.CmdDrawIndexedIndirect(cmd, _indirectCmdsBuffer, 0,
                    (uint)_meshDraws.Length, (uint)sizeof(GpuIndirectCommand));
            }
            else
            {
                for (int m = 0; m < _meshDraws.Length; m++)
                {
                    var mesh = _meshDraws[m];
                    var pcSpan = new Span<byte>(pcBuf, 96);
                    MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref mesh.World, 1)).CopyTo(pcSpan.Slice(0, 64));
                    MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref mesh.Color, 1)).CopyTo(pcSpan.Slice(64, 16));
                    MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref mesh.PBR, 1)).CopyTo(pcSpan.Slice(80, 16));
                    _vk.CmdPushConstants(cmd, _gbufferPipelineLayout, ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, 0, 96, pcBuf);
                    ulong vboOffsets = 0;
                    fixed (Buffer* vboPtr = &_vertexBuffer)
                        _vk.CmdBindVertexBuffers(cmd, 0, 1, vboPtr, &vboOffsets);
                    _vk.CmdBindIndexBuffer(cmd, _indexBuffer, mesh.IndexOffset * sizeof(uint), IndexType.Uint32);
                    _vk.CmdDrawIndexed(cmd, mesh.IndexCount, 1, 0, 0, 0);
                }
            }
            _vk.CmdEndRenderPass(cmd);
        }

        // Pass 2: Lighting
        {
            ClearValue clear = new() { Color = new ClearColorValue(0f, 0f, 0f, 1f) };
            RenderPassBeginInfo rpBegin = new()
            {
                SType = StructureType.RenderPassBeginInfo,
                RenderPass = _lightingRenderPass,
                Framebuffer = _lightingFramebuffers[imageIndex],
                RenderArea = new Rect2D(new Offset2D(0, 0), new Extent2D(w, h)),
                ClearValueCount = 1,
                PClearValues = &clear
            };
            _vk.CmdBeginRenderPass(cmd, &rpBegin, SubpassContents.Inline);
            _vk.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _lightingPipeline);
            _vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _lightingPipelineLayout, 0, 1,
                in _lightingDescriptorSet, 0, null);
            _vk.CmdDraw(cmd, 3, 1, 0, 0);
            _vk.CmdEndRenderPass(cmd);
        }

        uint halfW = w / 2, halfH = h / 2;

        // Pass 3: BloomBright (HDR → bloomBrightRT, half-res)
        {
            ClearValue clear = new() { Color = new ClearColorValue(0f, 0f, 0f, 1f) };
            RenderPassBeginInfo rpBegin = new()
            {
                SType = StructureType.RenderPassBeginInfo,
                RenderPass = _bloomBrightRP,
                Framebuffer = _bloomBrightFB,
                RenderArea = new Rect2D(new Offset2D(0, 0), new Extent2D(halfW, halfH)),
                ClearValueCount = 1,
                PClearValues = &clear
            };
            _vk.CmdBeginRenderPass(cmd, &rpBegin, SubpassContents.Inline);
            _vk.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _bloomBrightPipeline);
            _vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _bloomBrightLayout, 0, 1,
                in _bloomBrightDescSet, 0, null);
            Viewport vp = new(0, 0, halfW, halfH, 0, 1);
            Rect2D sc = new(new Offset2D(0, 0), new Extent2D(halfW, halfH));
            _vk.CmdSetViewport(cmd, 0, 1, &vp);
            _vk.CmdSetScissor(cmd, 0, 1, &sc);
            Vector4 pc = new(0.8f, 0.7f, 0, 0);
            fixed (byte* pcPtr = MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref pc, 1)))
                _vk.CmdPushConstants(cmd, _bloomBrightLayout, ShaderStageFlags.FragmentBit, 0, 16, pcPtr);
            _vk.CmdDraw(cmd, 3, 1, 0, 0);
            _vk.CmdEndRenderPass(cmd);
        }

        // Pass 4: BloomBlurH (bloomBrightRT → bloomBlurHRT, half-res)
        {
            ClearValue clear = new() { Color = new ClearColorValue(0f, 0f, 0f, 1f) };
            RenderPassBeginInfo rpBegin = new()
            {
                SType = StructureType.RenderPassBeginInfo,
                RenderPass = _bloomBlurRP,
                Framebuffer = _bloomBlurHFB,
                RenderArea = new Rect2D(new Offset2D(0, 0), new Extent2D(halfW, halfH)),
                ClearValueCount = 1,
                PClearValues = &clear
            };
            _vk.CmdBeginRenderPass(cmd, &rpBegin, SubpassContents.Inline);
            _vk.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _bloomBlurPipeline);
            _vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _bloomBlurLayout, 0, 1,
                in _bloomBlurHDescSet, 0, null);
            Viewport vp = new(0, 0, halfW, halfH, 0, 1);
            Rect2D sc = new(new Offset2D(0, 0), new Extent2D(halfW, halfH));
            _vk.CmdSetViewport(cmd, 0, 1, &vp);
            _vk.CmdSetScissor(cmd, 0, 1, &sc);
            Vector4 pc = new(0, 0, 1.0f / halfW, 0);
            fixed (byte* pcPtr = MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref pc, 1)))
                _vk.CmdPushConstants(cmd, _bloomBlurLayout, ShaderStageFlags.FragmentBit, 0, 16, pcPtr);
            _vk.CmdDraw(cmd, 3, 1, 0, 0);
            _vk.CmdEndRenderPass(cmd);
        }

        // Pass 5: BloomBlurV (bloomBlurHRT → bloomFinalRT, half-res)
        {
            ClearValue clear = new() { Color = new ClearColorValue(0f, 0f, 0f, 1f) };
            RenderPassBeginInfo rpBegin = new()
            {
                SType = StructureType.RenderPassBeginInfo,
                RenderPass = _bloomBlurRP,
                Framebuffer = _bloomFinalFB,
                RenderArea = new Rect2D(new Offset2D(0, 0), new Extent2D(halfW, halfH)),
                ClearValueCount = 1,
                PClearValues = &clear
            };
            _vk.CmdBeginRenderPass(cmd, &rpBegin, SubpassContents.Inline);
            _vk.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _bloomBlurPipeline);
            _vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _bloomBlurLayout, 0, 1,
                in _bloomBlurVDescSet, 0, null);
            Viewport vp = new(0, 0, halfW, halfH, 0, 1);
            Rect2D sc = new(new Offset2D(0, 0), new Extent2D(halfW, halfH));
            _vk.CmdSetViewport(cmd, 0, 1, &vp);
            _vk.CmdSetScissor(cmd, 0, 1, &sc);
            Vector4 pc = new(0, 0, 0, 1.0f / halfH);
            fixed (byte* pcPtr = MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref pc, 1)))
                _vk.CmdPushConstants(cmd, _bloomBlurLayout, ShaderStageFlags.FragmentBit, 0, 16, pcPtr);
            _vk.CmdDraw(cmd, 3, 1, 0, 0);
            _vk.CmdEndRenderPass(cmd);
        }

        // Pass 6: PostProcess composite (HDR + bloom → ldrRT, full-res)
        {
            ClearValue clear = new() { Color = new ClearColorValue(0f, 0f, 0f, 1f) };
            RenderPassBeginInfo rpBegin = new()
            {
                SType = StructureType.RenderPassBeginInfo,
                RenderPass = _bloomCompositeRP,
                Framebuffer = _bloomCompositeFB,
                RenderArea = new Rect2D(new Offset2D(0, 0), new Extent2D(w, h)),
                ClearValueCount = 1,
                PClearValues = &clear
            };
            _vk.CmdBeginRenderPass(cmd, &rpBegin, SubpassContents.Inline);
            _vk.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _bloomCompositePipeline);
            _vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _bloomCompositeLayout, 0, 1,
                in _bloomCompositeDescriptorSet, 0, null);
            _vk.CmdDraw(cmd, 3, 1, 0, 0);
            _vk.CmdEndRenderPass(cmd);
        }

        // Pass 7: FXAA (ldrRT → swapchain, full-res)
        {
            ClearValue clear = new() { Color = new ClearColorValue(0f, 0f, 0f, 1f) };
            RenderPassBeginInfo rpBegin = new()
            {
                SType = StructureType.RenderPassBeginInfo,
                RenderPass = _fxaaRP,
                Framebuffer = _fxaaFramebuffers[imageIndex],
                RenderArea = new Rect2D(new Offset2D(0, 0), new Extent2D(w, h)),
                ClearValueCount = 1,
                PClearValues = &clear
            };
            _vk.CmdBeginRenderPass(cmd, &rpBegin, SubpassContents.Inline);
            _vk.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _fxaaPipeline);
            _vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _fxaaLayout, 0, 1,
                in _fxaaDescSet, 0, null);
            _vk.CmdDraw(cmd, 3, 1, 0, 0);
            _vk.CmdEndRenderPass(cmd);
        }

        _vk.EndCommandBuffer(cmd);
    }

    private Matrix4x4[] GetCurrentLightVPs()
    {
        Vector3 lightDirUp = Vector3.Normalize(-_lightDir);
        float[] cascadeFars = new[] { 10.0f, 25.0f, 50.0f, 100.0f };
        Matrix4x4[] vps = new Matrix4x4[NumCascades];
        for (int c = 0; c < NumCascades; c++)
        {
            float far = cascadeFars[c];
            Vector3 lightEye = _cameraTarget + lightDirUp * far;
            var lookAt = Matrix4x4.CreateLookAt(lightEye, _cameraTarget, Vector3.UnitY);
            var ortho = Matrix4x4.CreateOrthographic(far * 1.5f, far * 1.5f, 0.1f, far * 2.0f);
            vps[c] = lookAt * ortho;
        }
        return vps;
    }

    // ════════════════════════════════════════════════════════════════
    // Dispose
    // ════════════════════════════════════════════════════════════════
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (!_initialized) return;

        _vk!.DeviceWaitIdle(_device);

        _vk.DestroyPipeline(_device, _shadowPipeline, null);
        _vk.DestroyPipeline(_device, _gbufferPipeline, null);
        _vk.DestroyPipeline(_device, _lightingPipeline, null);
        _vk.DestroyPipeline(_device, _bloomCompositePipeline, null);
        _vk.DestroyPipeline(_device, _bloomBrightPipeline, null);
        _vk.DestroyPipeline(_device, _bloomBlurPipeline, null);
        _vk.DestroyPipeline(_device, _fxaaPipeline, null);
        _vk.DestroyPipelineLayout(_device, _shadowPipelineLayout, null);
        _vk.DestroyPipelineLayout(_device, _gbufferPipelineLayout, null);
        _vk.DestroyPipelineLayout(_device, _lightingPipelineLayout, null);
        _vk.DestroyPipelineLayout(_device, _bloomCompositeLayout, null);
        _vk.DestroyPipelineLayout(_device, _bloomBrightLayout, null);
        _vk.DestroyPipelineLayout(_device, _bloomBlurLayout, null);
        _vk.DestroyPipelineLayout(_device, _fxaaLayout, null);
        _vk.DestroyDescriptorSetLayout(_device, _gbufferSetLayout, null);
        _vk.DestroyDescriptorSetLayout(_device, _lightingSetLayout, null);
        _vk.DestroyDescriptorSetLayout(_device, _bloomCompositeSetLayout, null);
        _vk.DestroyDescriptorSetLayout(_device, _bloomTexSetLayout, null);
        _vk.DestroyDescriptorSetLayout(_device, _fxaaSetLayout, null);
        if (_gpuDriven)
        {
            _vk.DestroyPipeline(_device, _cullPipeline, null);
            _vk.DestroyPipelineLayout(_device, _cullLayout, null);
            _vk.DestroyDescriptorSetLayout(_device, _cullSetLayout, null);
            _vk.DestroyDescriptorSetLayout(_device, _itemsSetLayout, null);
            if (_cullComp.Handle != 0) _vk.DestroyShaderModule(_device, _cullComp, null);
            _vk.DestroyBuffer(_device, _renderItemsSSBO, null);
            _vk.FreeMemory(_device, _renderItemsSSBOMem, null);
            _vk.DestroyBuffer(_device, _indirectCmdsBuffer, null);
            _vk.FreeMemory(_device, _indirectCmdsBufferMem, null);
            _vk.UnmapMemory(_device, _visibleCountBufferMem);
            _vk.DestroyBuffer(_device, _visibleCountBuffer, null);
            _vk.FreeMemory(_device, _visibleCountBufferMem, null);
        }
        _vk.DestroyDescriptorPool(_device, _descriptorPool, null);

        foreach (var fb in _shadowFramebuffers) _vk.DestroyFramebuffer(_device, fb, null);
        _vk.DestroyFramebuffer(_device, _gbufferFramebuffer, null);
        foreach (var fb in _lightingFramebuffers) _vk.DestroyFramebuffer(_device, fb, null);
        foreach (var fb in _fxaaFramebuffers) _vk.DestroyFramebuffer(_device, fb, null);
        _vk.DestroyFramebuffer(_device, _bloomBrightFB, null);
        _vk.DestroyFramebuffer(_device, _bloomBlurHFB, null);
        _vk.DestroyFramebuffer(_device, _bloomFinalFB, null);
        _vk.DestroyFramebuffer(_device, _bloomCompositeFB, null);

        _vk.DestroyRenderPass(_device, _shadowRenderPass, null);
        _vk.DestroyRenderPass(_device, _gbufferRenderPass, null);
        _vk.DestroyRenderPass(_device, _lightingRenderPass, null);
        _vk.DestroyRenderPass(_device, _bloomCompositeRP, null);
        _vk.DestroyRenderPass(_device, _bloomBrightRP, null);
        _vk.DestroyRenderPass(_device, _bloomBlurRP, null);
        _vk.DestroyRenderPass(_device, _fxaaRP, null);

        _vk.DestroySampler(_device, _shadowSamplerCmp, null);
        _vk.DestroySampler(_device, _linearSampler, null);

        for (int i = 0; i < 5; i++)
        {
            _vk.DestroyImageView(_device, _gbufferViews[i], null);
            _vk.DestroyImage(_device, _gbufferImages[i], null);
            _vk.FreeMemory(_device, _gbufferMems[i], null);
        }
        _vk.DestroyImageView(_device, _gbufferDepthView, null);
        _vk.DestroyImage(_device, _gbufferDepth, null);
        _vk.FreeMemory(_device, _gbufferDepthMem, null);

        _vk.DestroyImageView(_device, _hdrColorView, null);
        _vk.DestroyImage(_device, _hdrColor, null);
        _vk.FreeMemory(_device, _hdrColorMem, null);

        _vk.DestroyImageView(_device, _bloomBrightView, null);
        _vk.DestroyImage(_device, _bloomBright, null);
        _vk.FreeMemory(_device, _bloomBrightMem, null);
        _vk.DestroyImageView(_device, _bloomBlurHView, null);
        _vk.DestroyImage(_device, _bloomBlurH, null);
        _vk.FreeMemory(_device, _bloomBlurHMem, null);
        _vk.DestroyImageView(_device, _bloomFinalView, null);
        _vk.DestroyImage(_device, _bloomFinal, null);
        _vk.FreeMemory(_device, _bloomFinalMem, null);
        _vk.DestroyImageView(_device, _ldrRTView, null);
        _vk.DestroyImage(_device, _ldrRT, null);
        _vk.FreeMemory(_device, _ldrRTMem, null);

        for (int c = 0; c < NumCascades; c++)
        {
            _vk.DestroyImageView(_device, _shadowViews[c], null);
            _vk.DestroyImage(_device, _shadowImages[c], null);
            _vk.FreeMemory(_device, _shadowMems[c], null);
        }

        _vk.DestroyBuffer(_device, _vertexBuffer, null);
        _vk.FreeMemory(_device, _vertexBufferMem, null);
        _vk.DestroyBuffer(_device, _indexBuffer, null);
        _vk.FreeMemory(_device, _indexBufferMem, null);

        _vk.UnmapMemory(_device, _cameraUBOMem);
        _vk.UnmapMemory(_device, _lightUBOMem);
        _vk.UnmapMemory(_device, _resUBOMem);
        _vk.UnmapMemory(_device, _exposureUBOMem);
        _vk.DestroyBuffer(_device, _cameraUBO, null);
        _vk.FreeMemory(_device, _cameraUBOMem, null);
        _vk.DestroyBuffer(_device, _lightUBO, null);
        _vk.FreeMemory(_device, _lightUBOMem, null);
        _vk.DestroyBuffer(_device, _resUBO, null);
        _vk.FreeMemory(_device, _resUBOMem, null);
        _vk.DestroyBuffer(_device, _exposureUBO, null);
        _vk.FreeMemory(_device, _exposureUBOMem, null);
        _vk.UnmapMemory(_device, _fxaaUBOMem);
        _vk.DestroyBuffer(_device, _fxaaUBO, null);
        _vk.FreeMemory(_device, _fxaaUBOMem, null);

        _vk.FreeCommandBuffers(_device, _commandPool, (uint)_commandBuffers.Length, _commandBuffers);
        _vk.DestroyCommandPool(_device, _commandPool, null);

        foreach (var v in _swapchainImageViews) _vk.DestroyImageView(_device, v, null);
        _khrSwapchain!.DestroySwapchain(_device, _swapchain, null);

        foreach (var s in _imageAvailableSemaphores) _vk.DestroySemaphore(_device, s, null);
        foreach (var s in _renderFinishedSemaphores) _vk.DestroySemaphore(_device, s, null);
        foreach (var f in _inFlightFences) _vk.DestroyFence(_device, f, null);

        _khrSurface!.DestroySurface(_instance, _surface, null);
        _extDebug?.DestroyDebugUtilsMessenger(_instance, _debugMessenger, null);
        _vk.DestroyDevice(_device, null);
        _vk.DestroyInstance(_instance, null);

        Console.WriteLine("[DeferredPBRSandbox] 资源清理完成");
    }
}
