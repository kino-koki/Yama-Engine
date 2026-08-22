// ==================================================================================================
//  HarmonyOSAdapter.cs — 山雨引擎 HarmonyOS 平台适配层
//
//  命名空间: ShanyuEngine.Platform
//  依赖:     System.Runtime.InteropServices, HarmonyOS Native API (概念层)
//
//  功能:
//    1. HarmonyOS 平台特定适配
//    2. ArkTS/Native API 桥接概念
//    3. 窗口创建（通过 HarmonyOS Native Window API）
//    4. 触摸输入映射
//    5. 文件系统路径映射（HarmonyOS 沙箱路径）
//    6. 设备能力查询（GPU 型号 / 内存 / 屏幕分辨率）
// ==================================================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace ShanyuEngine.Platform
{
    // ════════════════════════════════════════════════════════════════════════════
    // 区域 1: HarmonyOS Native API P/Invoke 声明（概念层）
    //
    // 注意: HarmonyOS 的 Native API 体系与 Android NDK 不同，
    // 此处声明的是 HarmonyOS Native Window / Input / JNI 桥接的概念接口。
    // 实际集成时需要 HarmonyOS NDK（hdc_ndk）提供对应的 .so 库。
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// HarmonyOS 原生 API P/Invoke 声明。
    /// 声明 HarmonyOS Native Window、Input、System 信息的桥接接口。
    /// 实际使用需要 HarmonyOS NDK 提供对应的动态库。
    /// </summary>
    internal static class HarmonyOSNative
    {
        // HarmonyOS Native Window API
        private const string NativeWindowLib = "libace.z.so";

        /// <summary>
        /// OH_NativeWindow_Create — 创建 HarmonyOS 原生窗口。
        /// 对应 HarmonyOS ArkUI 的 NativeWindow 接口。
        /// </summary>
        [DllImport(NativeWindowLib, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr OH_NativeWindow_Create(int width, int height, int format);

        /// <summary>OH_NativeWindow_Destroy — 销毁原生窗口。</summary>
        [DllImport(NativeWindowLib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int OH_NativeWindow_Destroy(IntPtr window);

        /// <summary>OH_NativeWindow_SetSize — 设置窗口尺寸。</summary>
        [DllImport(NativeWindowLib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int OH_NativeWindow_SetSize(IntPtr window, int width, int height);

        /// <summary>OH_NativeWindow_GetSize — 获取窗口尺寸。</summary>
        [DllImport(NativeWindowLib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int OH_NativeWindow_GetSize(IntPtr window, out int width, out int height);

        /// <summary>OH_NativeWindow_SetSurfaceColorSpace — 设置色彩空间。</summary>
        [DllImport(NativeWindowLib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int OH_NativeWindow_SetSurfaceColorSpace(IntPtr window, int colorSpace);

        // HarmonyOS 输入事件 API
        private const string NativeInputLib = "libinput.z.so";

        /// <summary>
        /// OH_InputEvent_RegisterCallback — 注册输入事件回调。
        /// HarmonyOS 使用回调模式分发触摸/键盘/鼠标事件。
        /// </summary>
        [DllImport(NativeInputLib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int OH_InputEvent_RegisterCallback(
            IntPtr eventHub,
            IntPtr callback,
            IntPtr userData);

        /// <summary>OH_InputEvent_UnregisterCallback — 取消注册输入事件回调。</summary>
        [DllImport(NativeInputLib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int OH_InputEvent_UnregisterCallback(IntPtr eventHub, IntPtr callback);

        // HarmonyOS 设备信息 API
        private const string NativeDeviceLib = "libdeviceinfo.z.so";

        /// <summary>OH_DeviceInfo_GetModel — 获取设备型号。</summary>
        [DllImport(NativeDeviceLib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int OH_DeviceInfo_GetModel(byte[] buffer, int bufferSize);

        /// <summary>OH_DeviceInfo_GetGPURenderer — 获取 GPU 渲染器名称。</summary>
        [DllImport(NativeDeviceLib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int OH_DeviceInfo_GetGPURenderer(byte[] buffer, int bufferSize);

        /// <summary>OH_DeviceInfo_GetTotalMemoryMB — 获取设备总内存（MB）。</summary>
        [DllImport(NativeDeviceLib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int OH_DeviceInfo_GetTotalMemoryMB();

        /// <summary>OH_DeviceInfo_GetScreenResolution — 获取屏幕分辨率。</summary>
        [DllImport(NativeDeviceLib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int OH_DeviceInfo_GetScreenResolution(out int width, out int height);

        /// <summary>OH_DeviceInfo_GetScreenDPI — 获取屏幕 DPI。</summary>
        [DllImport(NativeDeviceLib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int OH_DeviceInfo_GetScreenDPI(out int dpiX, out int dpiY);

        // HarmonyOS 文件系统 API（沙箱路径）
        private const string NativeFileLib = "libfile.z.so";

        /// <summary>OH_File_GetFilesDir — 获取应用文件目录（沙箱）。</summary>
        [DllImport(NativeFileLib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int OH_File_GetFilesDir(byte[] buffer, int bufferSize);

        /// <summary>OH_File_GetCacheDir — 获取应用缓存目录。</summary>
        [DllImport(NativeFileLib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int OH_File_GetCacheDir(byte[] buffer, int bufferSize);

        /// <summary>OH_File_GetTempDir — 获取临时文件目录。</summary>
        [DllImport(NativeFileLib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int OH_File_GetTempDir(byte[] buffer, int bufferSize);

        // HarmonyOS Vulkan 扩展
        private const string NativeVulkanLib = "libvulkan.so";

        /// <summary>创建 Vulkan surface（通过 HarmonyOS Native Window）。</summary>
        [DllImport(NativeVulkanLib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int OH_Vulkan_CreateSurface(
            IntPtr vkInstance, IntPtr nativeWindow, out ulong surface);

        // 格式常量
        public const int WINDOW_FORMAT_RGBA_8888 = 1;
        public const int WINDOW_FORMAT_RGBX_8888 = 2;
        public const int COLOR_SPACE_SRGB = 0;
        public const int COLOR_SPACE_DISPLAY_P3 = 1;
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 2: HarmonyOS 触摸输入事件
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// HarmonyOS 触摸输入事件。从 HarmonyOS 原生输入事件映射而来。
    /// </summary>
    public sealed class HarmonyOSTouchEvent
    {
        /// <summary>触摸类型：按下/抬起/移动/取消。</summary>
        public enum TouchAction
        {
            Down,
            Up,
            Move,
            Cancel
        }

        /// <summary>事件动作类型。</summary>
        public TouchAction Action { get; set; }

        /// <summary>手指 ID（多指触摸标识）。</summary>
        public int FingerId { get; set; }

        /// <summary>触摸点 X 坐标（像素）。</summary>
        public float X { get; set; }

        /// <summary>触摸点 Y 坐标（像素）。</summary>
        public float Y { get; set; }

        /// <summary>触摸压力值（0.0 ~ 1.0）。</summary>
        public float Pressure { get; set; }

        /// <summary>触摸区域宽度。</summary>
        public float TouchMajor { get; set; }

        /// <summary>触摸区域高度。</summary>
        public float TouchMinor { get; set; }

        /// <summary>时间戳（纳秒）。</summary>
        public long TimestampNs { get; set; }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 3: HarmonyOS 设备能力信息
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// HarmonyOS 设备能力信息。描述当前设备的硬件规格。
    /// </summary>
    public sealed class HarmonyOSDeviceCapabilities
    {
        /// <summary>设备型号（如 Mate 60 Pro, P60 等）。</summary>
        public string Model { get; set; } = "Unknown";

        /// <summary>GPU 渲染器名称。</summary>
        public string GPURenderer { get; set; } = "Unknown";

        /// <summary>设备总内存（MB）。</summary>
        public int TotalMemoryMB { get; set; }

        /// <summary>屏幕宽度（像素）。</summary>
        public int ScreenWidth { get; set; }

        /// <summary>屏幕高度（像素）。</summary>
        public int ScreenHeight { get; set; }

        /// <summary>屏幕 DPI X。</summary>
        public int DpiX { get; set; }

        /// <summary>屏幕 DPI Y。</summary>
        public int DpiY { get; set; }

        /// <summary>屏幕刷新率（Hz）。</summary>
        public float RefreshRate { get; set; } = 60.0f;

        /// <summary>是否支持 Vulkan。</summary>
        public bool SupportsVulkan { get; set; }

        /// <summary>最大并发触摸点数。</summary>
        public int MaxTouchPoints { get; set; } = 10;

        /// <summary>构建可读描述。</summary>
        public override string ToString()
        {
            return $"HarmonyOS Device: {Model}, GPU: {GPURenderer}, " +
                   $"RAM: {TotalMemoryMB}MB, Screen: {ScreenWidth}x{ScreenHeight}@{DpiX}dpi";
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 4: HarmonyOS 沙箱文件系统路径
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// HarmonyOS 文件系统路径映射。HarmonyOS 使用应用沙箱文件系统，
    /// 需要通过 Native API 获取合法的路径。
    /// </summary>
    public sealed class HarmonyOSFileSystemPaths
    {
        /// <summary>应用文件目录（如 /data/app/xxx/files）。</summary>
        public string FilesDir { get; set; } = "";

        /// <summary>应用缓存目录（如 /data/app/xxx/cache）。</summary>
        public string CacheDir { get; set; } = "";

        /// <summary>临时文件目录。</summary>
        public string TempDir { get; set; } = "";

        /// <summary>引擎资源目录（引擎包内路径）。</summary>
        public string EngineDir { get; set; } = "";

        /// <summary>用户数据目录。</summary>
        public string UserDataDir { get; set; } = "";

        /// <summary>检查路径是否存在。</summary>
        public static bool PathExists(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            return Directory.Exists(path);
        }

        /// <summary>构建相对于 FilesDir 的完整路径。</summary>
        public string GetFilesPath(string relativePath)
        {
            return Path.Combine(FilesDir, relativePath);
        }

        /// <summary>构建相对于 CacheDir 的完整路径。</summary>
        public string GetCachePath(string relativePath)
        {
            return Path.Combine(CacheDir, relativePath);
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 5: HarmonyOSAdapter — 平台适配器实现
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// HarmonyOS 平台适配器。封装 HarmonyOS 特有的窗口管理、
    /// 输入处理、文件系统和设备信息查询。
    ///
    /// 架构说明:
    ///   HarmonyOS 应用通常由 ArkTS (UI框架) + Native C/C++ (性能层) 组成。
    ///   山雨引擎通过 HarmonyOS Native API (libace.z.so) 创建 Native Window，
    ///   在 Native 层直接运行引擎主循环和渲染。
    ///   ArkTS 层负责生命周期管理，通过 JNI/FFI 与引擎 Native 层通信。
    /// </summary>
    public sealed class HarmonyOSAdapter : IDisposable
    {
        // ────────────── 私有状态 ──────────────
        private IntPtr _nativeWindow = IntPtr.Zero;
        private IntPtr _inputEventHub = IntPtr.Zero;
        private IntPtr _nativeCallback = IntPtr.Zero;
        private bool _initialized;
        private bool _disposed;
        private int _width;
        private int _height;

        // ────────────── 属性 ──────────────
        /// <summary>原生窗口句柄。</summary>
        public IntPtr NativeWindowHandle => _nativeWindow;

        /// <summary>窗口宽度。</summary>
        public int Width => _width;

        /// <summary>窗口高度。</summary>
        public int Height => _height;

        /// <summary>设备能力信息。</summary>
        public HarmonyOSDeviceCapabilities DeviceCapabilities { get; private set; } = new();

        /// <summary>文件系统路径。</summary>
        public HarmonyOSFileSystemPaths FileSystemPaths { get; private set; } = new();

        /// <summary>是否已初始化。</summary>
        public bool IsInitialized => _initialized;

        // ────────────── 事件 ──────────────
        /// <summary>触摸输入事件回调。</summary>
        public event EventHandler<HarmonyOSTouchEvent>? OnTouchEvent;

        /// <summary>窗口尺寸变化回调。</summary>
        public event EventHandler<(int Width, int Height)>? OnResize;

        // ────────────── 初始化 ──────────────
        /// <summary>
        /// 初始化 HarmonyOS 适配器。
        /// 获取设备信息、初始化文件系统路径、创建原生窗口。
        /// </summary>
        /// <param name="requestedWidth">请求的窗口宽度。</param>
        /// <param name="requestedHeight">请求的窗口高度。</param>
        public void Initialize(int requestedWidth = 0, int requestedHeight = 0)
        {
            if (_initialized) return;

            // 1. 查询设备能力
            QueryDeviceCapabilities();

            // 2. 初始化文件系统路径
            InitializeFileSystemPaths();

            // 3. 确定窗口尺寸
            if (requestedWidth > 0 && requestedHeight > 0)
            {
                _width = requestedWidth;
                _height = requestedHeight;
            }
            else
            {
                _width = DeviceCapabilities.ScreenWidth;
                _height = DeviceCapabilities.ScreenHeight;
            }

            // 4. 创建原生窗口
            CreateNativeWindow();

            // 5. 注册输入事件回调
            RegisterInputCallbacks();

            _initialized = true;
        }

        private void QueryDeviceCapabilities()
        {
            var caps = new HarmonyOSDeviceCapabilities();

            // 获取设备型号
            try
            {
                byte[] buf = new byte[256];
                int ret = HarmonyOSNative.OH_DeviceInfo_GetModel(buf, buf.Length);
                if (ret >= 0 && ret < buf.Length)
                    caps.Model = System.Text.Encoding.UTF8.GetString(buf, 0, ret);
            }
            catch { caps.Model = "HarmonyOS Device"; }

            // 获取 GPU 渲染器
            try
            {
                byte[] buf = new byte[256];
                int ret = HarmonyOSNative.OH_DeviceInfo_GetGPURenderer(buf, buf.Length);
                if (ret >= 0 && ret < buf.Length)
                    caps.GPURenderer = System.Text.Encoding.UTF8.GetString(buf, 0, ret);
            }
            catch { caps.GPURenderer = "Unknown GPU"; }

            // 获取内存
            try { caps.TotalMemoryMB = HarmonyOSNative.OH_DeviceInfo_GetTotalMemoryMB(); }
            catch { caps.TotalMemoryMB = 4096; }

            // 获取屏幕信息
            try
            {
                HarmonyOSNative.OH_DeviceInfo_GetScreenResolution(out int w, out int h);
                caps.ScreenWidth = w;
                caps.ScreenHeight = h;
            }
            catch { caps.ScreenWidth = 1080; caps.ScreenHeight = 2400; }

            try
            {
                HarmonyOSNative.OH_DeviceInfo_GetScreenDPI(out int dpiX, out int dpiY);
                caps.DpiX = dpiX;
                caps.DpiY = dpiY;
            }
            catch { caps.DpiX = 480; caps.DpiY = 480; }

            // Vulkan 支持检测
            try
            {
                caps.SupportsVulkan = NativeLibrary.TryLoad("libvulkan.so", out _);
            }
            catch { caps.SupportsVulkan = false; }

            DeviceCapabilities = caps;
        }

        private void InitializeFileSystemPaths()
        {
            var paths = new HarmonyOSFileSystemPaths();

            try
            {
                byte[] buf = new byte[512];
                int ret = HarmonyOSNative.OH_File_GetFilesDir(buf, buf.Length);
                if (ret >= 0 && ret < buf.Length)
                    paths.FilesDir = System.Text.Encoding.UTF8.GetString(buf, 0, ret);
            }
            catch { paths.FilesDir = "/data/app/shanyu_engine/files"; }

            try
            {
                byte[] buf = new byte[512];
                int ret = HarmonyOSNative.OH_File_GetCacheDir(buf, buf.Length);
                if (ret >= 0 && ret < buf.Length)
                    paths.CacheDir = System.Text.Encoding.UTF8.GetString(buf, 0, ret);
            }
            catch { paths.CacheDir = "/data/app/shanyu_engine/cache"; }

            try
            {
                byte[] buf = new byte[512];
                int ret = HarmonyOSNative.OH_File_GetTempDir(buf, buf.Length);
                if (ret >= 0 && ret < buf.Length)
                    paths.TempDir = System.Text.Encoding.UTF8.GetString(buf, 0, ret);
            }
            catch { paths.TempDir = "/data/app/shanyu_engine/temp"; }

            // 引擎资源目录（跟随应用安装路径）
            paths.EngineDir = Path.Combine(paths.FilesDir, "Engine");
            paths.UserDataDir = Path.Combine(paths.FilesDir, "UserData");

            // 确保目录存在
            try
            {
                Directory.CreateDirectory(paths.EngineDir);
                Directory.CreateDirectory(paths.UserDataDir);
            }
            catch { /* 沙箱权限限制 */ }

            FileSystemPaths = paths;
        }

        private void CreateNativeWindow()
        {
            try
            {
                _nativeWindow = HarmonyOSNative.OH_NativeWindow_Create(
                    _width, _height,
                    HarmonyOSNative.WINDOW_FORMAT_RGBA_8888);

                if (_nativeWindow == IntPtr.Zero)
                    throw new InvalidOperationException("OH_NativeWindow_Create 返回空句柄");
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"HarmonyOS 原生窗口创建失败: {ex.Message}", ex);
            }
        }

        private void RegisterInputCallbacks()
        {
            // HarmonyOS 输入事件通过回调分发。
            // 实际集成时需要创建 native callback delegate 并 pin 住防止 GC。
            //
            // 概念代码:
            //   _nativeCallback = Marshal.GetFunctionPointerForDelegate(_inputCallback);
            //   HarmonyOSNative.OH_InputEvent_RegisterCallback(_inputEventHub, _nativeCallback, IntPtr.Zero);
            //
            // 回调中将原生输入事件映射为 HarmonyOSTouchEvent 并分发到 OnTouchEvent。
        }

        // ────────────── 触摸输入映射 ──────────────
        /// <summary>
        /// 将 HarmonyOS 原生触摸事件映射到引擎统一事件格式。
        /// 由 ArkTS/Native 回调触发。
        /// </summary>
        internal void MapNativeTouchEvent(
            int action, int fingerId, float x, float y,
            float pressure, float touchMajor, float touchMinor, long timestampNs)
        {
            var evt = new HarmonyOSTouchEvent
            {
                Action = action switch
                {
                    0 => HarmonyOSTouchEvent.TouchAction.Down,
                    1 => HarmonyOSTouchEvent.TouchAction.Up,
                    2 => HarmonyOSTouchEvent.TouchAction.Move,
                    3 => HarmonyOSTouchEvent.TouchAction.Cancel,
                    _ => HarmonyOSTouchEvent.TouchAction.Cancel
                },
                FingerId = fingerId,
                X = x,
                Y = y,
                Pressure = pressure,
                TouchMajor = touchMajor,
                TouchMinor = touchMinor,
                TimestampNs = timestampNs
            };

            OnTouchEvent?.Invoke(this, evt);
        }

        // ────────────── Vulkan Surface 创建 ──────────────
        /// <summary>
        /// 通过 HarmonyOS Native Window 创建 Vulkan surface。
        /// </summary>
        /// <param name="vulkanInstance">Vulkan VkInstance 原生指针。</param>
        /// <returns>VkSurfaceKHR 句柄。</returns>
        public ulong CreateVulkanSurface(IntPtr vulkanInstance)
        {
            if (_nativeWindow == IntPtr.Zero)
                throw new InvalidOperationException("原生窗口未创建，无法创建 Vulkan Surface。");

            int result = HarmonyOSNative.OH_Vulkan_CreateSurface(vulkanInstance, _nativeWindow, out ulong surface);
            if (result != 0)
                throw new InvalidOperationException($"HarmonyOS Vulkan Surface 创建失败，错误码: {result}");

            return surface;
        }

        // ────────────── 窗口操作 ──────────────
        /// <summary>设置窗口尺寸。</summary>
        public void SetWindowSize(int width, int height)
        {
            if (_nativeWindow == IntPtr.Zero) return;
            HarmonyOSNative.OH_NativeWindow_SetSize(_nativeWindow, width, height);
            _width = width;
            _height = height;
            OnResize?.Invoke(this, (width, height));
        }

        /// <summary>设置色彩空间（SRGB / Display P3）。</summary>
        public void SetColorSpace(int colorSpace)
        {
            if (_nativeWindow == IntPtr.Zero) return;
            HarmonyOSNative.OH_NativeWindow_SetSurfaceColorSpace(_nativeWindow, colorSpace);
        }

        // ────────────── ArkTS/Native 桥接概念 ──────────────
        /// <summary>
        /// ArkTS 桥接接口概念。
        /// HarmonyOS 应用的 ArkTS 层可以通过此接口与引擎 Native 层通信。
        ///
        /// 典型通信方式:
        ///   1. JNI: ArkTS -> C++ -> C# (通过 libshanyu_engine.so 导出函数)
        ///   2. FFI: ArkTS -> C# (HarmonyOS NDK 支持 FFI 调用)
        ///   3. 共享内存: ArkTS 和 Native 层通过共享内存交换数据
        ///
        /// 生命周期管理:
        ///   - ArkTS onCreate() -> Engine.Initialize()
        ///   - ArkTS onActive()  -> Engine.Resume()
        ///   - ArkTS onInactive() -> Engine.Pause()
        ///   - ArkTS onDestroy() -> Engine.Shutdown()
        /// </summary>

        /// <summary>引擎初始化（由 ArkTS onCreate 调用）。</summary>
        public void OnArkTSCreate(int width, int height)
        {
            Initialize(width, height);
        }

        /// <summary>引擎恢复到前台（由 ArkTS onActive 调用）。</summary>
        public void OnArkTSActive()
        {
            // 恢复渲染循环
        }

        /// <summary>引擎暂停到后台（由 ArkTS onInactive 调用）。</summary>
        public void OnArkTSInactive()
        {
            // 暂停渲染循环，释放 GPU 资源
        }

        /// <summary>引擎销毁（由 ArkTS onDestroy 调用）。</summary>
        public void OnArkTSDestroy()
        {
            Dispose();
        }

        // ────────────── IDisposable ──────────────
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                if (_nativeCallback != IntPtr.Zero)
                {
                    // HarmonyOSNative.OH_InputEvent_UnregisterCallback(_inputEventHub, _nativeCallback);
                    _nativeCallback = IntPtr.Zero;
                }

                if (_nativeWindow != IntPtr.Zero)
                {
                    HarmonyOSNative.OH_NativeWindow_Destroy(_nativeWindow);
                    _nativeWindow = IntPtr.Zero;
                }
            }
            catch { /* 忽略清理异常 */ }

            _initialized = false;
        }
    }
}
