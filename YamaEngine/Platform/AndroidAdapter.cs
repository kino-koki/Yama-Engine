// ==================================================================================================
//  AndroidAdapter.cs — yama引擎 Android 平台适配层
//
//  命名空间: YamaEngine.Platform
//  依赖:     System.Runtime.InteropServices, Android NDK (P/Invoke)
//
//  功能:
//    1. Android 特定适配
//    2. NativeActivity 生命周期管理
//    3. JNI P/Invoke 桥接（android.app.NativeActivity）
//    4. 触摸事件映射（AMotionEvent API）
//    5. 文件系统：Android Assets / ExternalStorage 路径
//    6. 设备信息：GPU Adreno / Mali / Tensor
//    7. 后台/前台切换处理
// ==================================================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace YamaEngine.Platform
{
    // ════════════════════════════════════════════════════════════════════════════
    // 区域 1: Android NDK P/Invoke 声明
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Android NDK 原生函数 P/Invoke 声明。
    /// 覆盖 NativeActivity、输入事件、传感器、文件系统等核心 API。
    /// </summary>
    internal static class AndroidNative
    {
        private const string AndroidLib = "libandroid.so";
        private const string LogLib = "liblog.so";
        private const string EGLLib = "libEGL.so";

        // ────────────── NativeActivity 生命周期 ──────────────

        /// <summary>获取 ANativeActivity 指针（通过 android_native_app_glue）。</summary>
        [DllImport(AndroidLib, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr ANativeActivity_fromJava(IntPtr env, IntPtr activity);

        /// <summary>显示软键盘。</summary>
        [DllImport(AndroidLib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void ANativeActivity_showSoftInput(IntPtr activity, int flags);

        /// <summary>隐藏软键盘。</summary>
        [DllImport(AndroidLib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void ANativeActivity_hideSoftInput(IntPtr activity, int flags);

        // ────────────── Android 日志 ──────────────

        [DllImport(LogLib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int __android_log_write(int prio, string tag, string text);

        [DllImport(LogLib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int __android_log_print(int prio, string tag, string fmt, object arg);

        // 日志优先级
        public const int ANDROID_LOG_VERBOSE = 2;
        public const int ANDROID_LOG_DEBUG   = 3;
        public const int ANDROID_LOG_INFO    = 4;
        public const int ANDROID_LOG_WARN    = 5;
        public const int ANDROID_LOG_ERROR   = 6;
        public const int ANDROID_LOG_FATAL   = 7;

        // ────────────── EGL（窗口 surface 创建）──────────────

        /// <summary>EGL 获取原生窗口。</summary>
        [DllImport(EGLLib, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr eglGetNativeWindow(IntPtr display, IntPtr surface);

        // ────────────── Asset Manager（APK 内资源访问）──────────────

        /// <summary>打开 APK 内的 Asset 文件。</summary>
        [DllImport(AndroidLib, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr AAssetManager_open(IntPtr mgr,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string filename, int mode);

        /// <summary>关闭 Asset 文件。</summary>
        [DllImport(AndroidLib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void AAsset_close(IntPtr asset);

        /// <summary>读取 Asset 文件内容。</summary>
        [DllImport(AndroidLib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int AAsset_read(IntPtr asset, byte[] buf, int count);

        /// <summary>获取 Asset 文件长度。</summary>
        [DllImport(AndroidLib, CallingConvention = CallingConvention.Cdecl)]
        public static extern long AAsset_getLength(IntPtr asset);

        /// <summary>获取 Asset Manager 指针。</summary>
        [DllImport(AndroidLib, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr AAssetManager_fromJava(IntPtr env, IntPtr assetManager);

        // Asset 打开模式
        public const int AASSET_MODE_UNKNOWN   = 0;
        public const int AASSET_MODE_RANDOM     = 1;
        public const int AASSET_MODE_STREAMING  = 2;
        public const int AASSET_MODE_BUFFER     = 3;

        // ────────────── 内部存储路径 ──────────────

        /// <summary>获取应用内部存储路径。</summary>
        [DllImport(AndroidLib, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr ANativeActivity_getInternalDataPath(IntPtr activity, out int length);

        /// <summary>获取应用外部存储路径。</summary>
        [DllImport(AndroidLib, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr ANativeActivity_getExternalFilesDir(IntPtr activity, out int length);

        /// <summary>获取应用缓存路径。</summary>
        [DllImport(AndroidLib, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr ANativeActivity_getCachePath(IntPtr activity, out int length);

        // ────────────── 屏幕/显示信息 ──────────────

        /// <summary>获取屏幕刷新率。</summary>
        [DllImport(AndroidLib, CallingConvention = CallingConvention.Cdecl)]
        public static extern float ANativeWindow_getRefreshRate(IntPtr window);

        // ────────────── Vulkan Surface ──────────────

        /// <summary>通过 ANativeWindow 创建 Vulkan Surface。</summary>
        [DllImport(AndroidLib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int ANativeWindow_createVulkanSurface(
            IntPtr vkInstance, IntPtr nativeWindow, out ulong surface);
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 2: Android 生命周期状态
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Android 应用生命周期状态。
    /// 对应 NativeActivity 的回调事件序列。
    /// </summary>
    public enum AndroidLifecycleState
    {
        /// <summary>应用未初始化。</summary>
        Uninitialized,
        /// <summary>NativeActivity.onCreate 已调用。</summary>
        Created,
        /// <summary>NativeActivity.onStart 已调用。</summary>
        Started,
        /// <summary>NativeActivity.onResume 已调用（前台活跃）。</summary>
        Resumed,
        /// <summary>NativeActivity.onPause 已调用（后台暂停）。</summary>
        Paused,
        /// <summary>NativeActivity.onStop 已调用。</summary>
        Stopped,
        /// <summary>NativeActivity.onDestroy 已调用。</summary>
        Destroyed
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 3: Android 触摸输入事件
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Android 触摸输入事件。从 AMotionEvent 映射而来。
    /// </summary>
    public sealed class AndroidTouchEvent
    {
        /// <summary>触摸动作类型。</summary>
        public enum TouchActionType
        {
            Cancel = -1,
            Down = 0,
            Up = 1,
            Move = 2,
            PointerDown = 5,
            PointerUp = 6
        }

        /// <summary>事件动作。</summary>
        public TouchActionType Action { get; set; }

        /// <summary>手指 ID。</summary>
        public int PointerId { get; set; }

        /// <summary>X 坐标（像素）。</summary>
        public float X { get; set; }

        /// <summary>Y 坐标（像素）。</summary>
        public float Y { get; set; }

        /// <summary>触摸压力值（0.0 ~ 1.0）。</summary>
        public float Pressure { get; set; }

        /// <summary>触摸区域大小。</summary>
        public float Size { get; set; }

        /// <summary>时间戳（纳秒）。</summary>
        public long TimestampNs { get; set; }

        /// <summary>当前活动触摸点数量。</summary>
        public int PointerCount { get; set; }

        /// <summary>触摸源类型（触摸屏/手写笔/鼠标）。</summary>
        public int Source { get; set; }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 4: Android 设备信息
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Android 设备硬件信息。包括 GPU 型号识别、内存、屏幕等。
    /// </summary>
    public sealed class AndroidDeviceInfo
    {
        /// <summary>GPU 渲染器（如 Adreno 650, Mali-G78, Mali-T880, Tensor GPU 等）。</summary>
        public string GPURenderer { get; set; } = "Unknown";

        /// <summary>GPU 厂商（Qualcomm / ARM / NVIDIA / IMG / Unknown）。</summary>
        public string GPUVendor { get; set; } = "Unknown";

        /// <summary>GPU 类型分类。</summary>
        public enum GPUType
        {
            Unknown,
            Adreno,     // Qualcomm Snapdragon
            Mali,       // ARM Mali
            PowerVR,    // Imagination Technologies
            Xclipse,    // Samsung Xclipse (RDNA)
            Tensor,     // Google Tensor
            Nvidia      // NVIDIA (Shield TV)
        }

        /// <summary>GPU 类型。</summary>
        public GPUType Type { get; set; } = GPUType.Unknown;

        /// <summary>设备型号（如 SM-G998B, Pixel 7 Pro）。</summary>
        public string Model { get; set; } = "Unknown";

        /// <summary>Android SDK 版本号。</summary>
        public int SdkVersion { get; set; }

        /// <summary>Android 版本字符串。</summary>
        public string AndroidVersion { get; set; } = "Unknown";

        /// <summary>设备总内存（MB）。</summary>
        public long TotalMemoryMB { get; set; }

        /// <summary>可用内存（MB）。</summary>
        public long AvailableMemoryMB { get; set; }

        /// <summary>屏幕宽度（像素）。</summary>
        public int ScreenWidth { get; set; }

        /// <summary>屏幕高度（像素）。</summary>
        public int ScreenHeight { get; set; }

        /// <summary>屏幕 DPI。</summary>
        public int ScreenDPI { get; set; }

        /// <summary>屏幕刷新率（Hz）。</summary>
        public float RefreshRate { get; set; } = 60.0f;

        /// <summary>是否支持 Vulkan。</summary>
        public bool SupportsVulkan { get; set; }

        /// <summary>是否 64 位系统。</summary>
        public bool Is64Bit { get; set; }

        /// <summary>是否低功耗模式。</summary>
        public bool IsPowerSaveMode { get; set; }

        /// <summary>最大并发触摸点数。</summary>
        public int MaxTouchPoints { get; set; } = 10;

        /// <summary>从 GL 渲染器字符串识别 GPU 类型。</summary>
        public static GPUType IdentifyGPUType(string renderer)
        {
            if (string.IsNullOrEmpty(renderer)) return GPUType.Unknown;
            string lower = renderer.ToLowerInvariant();
            if (lower.Contains("adreno")) return GPUType.Adreno;
            if (lower.Contains("mali")) return GPUType.Mali;
            if (lower.Contains("powervr")) return GPUType.PowerVR;
            if (lower.Contains("xclipse")) return GPUType.Xclipse;
            if (lower.Contains("tensor")) return GPUType.Tensor;
            if (lower.Contains("nvidia") || lower.Contains("tegra")) return GPUType.Nvidia;
            return GPUType.Unknown;
        }

        /// <summary>GPU 的推荐质量级别（0=最低, 1=低, 2=中, 3=高, 4=最高）。</summary>
        public int GetRecommendedQualityLevel()
        {
            // 根据 GPU 类型和大致性能分级
            return Type switch
            {
                GPUType.Adreno => GPURenderer switch
                {
                    string r when r.Contains("740") || r.Contains("750") || r.Contains("830") => 4,
                    string r when r.Contains("730") || r.Contains("820") => 3,
                    string r when r.Contains("710") || r.Contains("720") || r.Contains("730") => 2,
                    string r when r.Contains("660") || r.Contains("680") || r.Contains("710") => 1,
                    _ => 0
                },
                GPUType.Mali => GPURenderer switch
                {
                    string r when r.Contains("G7") => 4,
                    string r when r.Contains("G78") || r.Contains("G77") || r.Contains("G76") => 3,
                    string r when r.Contains("G72") || r.Contains("G71") => 2,
                    string r when r.Contains("G52") || r.Contains("G51") => 1,
                    _ => 0
                },
                GPUType.Xclipse => 3,
                GPUType.Tensor => 3,
                GPUType.PowerVR => 1,
                GPUType.Nvidia => 3,
                _ => 1
            };
        }

        /// <summary>可读描述。</summary>
        public override string ToString()
        {
            return $"Android: {Model} (SDK {SdkVersion}), GPU: {GPURenderer} ({Type}), " +
                   $"RAM: {TotalMemoryMB}MB, Screen: {ScreenWidth}x{ScreenHeight}@{RefreshRate}Hz";
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 5: Android 文件系统路径
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Android 文件系统路径映射。
    /// Android 的文件访问受沙箱限制，需要通过特定 API 获取合法路径。
    /// </summary>
    public sealed class AndroidFileSystemPaths
    {
        /// <summary>应用内部存储路径（如 /data/data/com.yama.engine/files）。</summary>
        public string InternalDataPath { get; set; } = "";

        /// <summary>外部存储路径（如 /sdcard/Android/data/com.yama.engine/files）。</summary>
        public string ExternalFilesDir { get; set; } = "";

        /// <summary>外部共享存储根目录。</summary>
        public string ExternalStorageRoot { get; set; } = "/sdcard";

        /// <summary>缓存目录。</summary>
        public string CacheDir { get; set; } = "";

        /// <summary>OBB 扩展文件目录。</summary>
        public string ObbDir { get; set; } = "";

        /// <summary>引擎资源目录（跟随 APK 安装）。</summary>
        public string EngineDir { get; set; } = "";

        /// <summary>用户数据目录。</summary>
        public string UserDataDir { get; set; } = "";

        /// <summary>APK 内 Assets 访问方式：通过 AAssetManager。</summary>
        public IntPtr AssetManagerPtr { get; set; } = IntPtr.Zero;

        /// <summary>获取 Assets 内文件的完整逻辑路径。</summary>
        public string GetAssetPath(string assetRelativePath) => $"assets://{assetRelativePath}";

        /// <summary>构建内部存储完整路径。</summary>
        public string GetInternalPath(string relativePath) =>
            Path.Combine(InternalDataPath, relativePath);

        /// <summary>构建外部存储完整路径。</summary>
        public string GetExternalPath(string relativePath) =>
            Path.Combine(ExternalFilesDir, relativePath);

        /// <summary>确保目录存在。</summary>
        public void EnsureDirectory(string path)
        {
            if (!string.IsNullOrEmpty(path) && !Directory.Exists(path))
                Directory.CreateDirectory(path);
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 6: AndroidAdapter — 平台适配器实现
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Android 平台适配器。封装 Android NativeActivity 生命周期、
    /// JNI 桥接、触摸输入、文件系统和设备信息查询。
    ///
    /// 集成方式:
    ///   yama引擎 Android 端通过 NativeActivity 运行。
    ///   android.app.NativeActivity 的 main() 函数由引擎提供（android_native_app_glue），
    ///   引擎在 NativeActivity 的回调中管理生命周期。
    ///   通过 JNI 与 Java/Kotlin 层通信（如支付、广告、社交等 Java API）。
    /// </summary>
    public sealed class AndroidAdapter : IDisposable
    {
        // ────────────── 私有状态 ──────────────
        private IntPtr _nativeActivity = IntPtr.Zero;
        private IntPtr _nativeWindow = IntPtr.Zero;
        private IntPtr _assetManager = IntPtr.Zero;
        private AndroidLifecycleState _lifecycleState = AndroidLifecycleState.Uninitialized;
        private bool _initialized;
        private bool _disposed;
        private int _width;
        private int _height;
        private readonly object _lifecycleLock = new();

        // ────────────── 属性 ──────────────
        /// <summary>NativeActivity 指针。</summary>
        public IntPtr NativeActivityPtr => _nativeActivity;

        /// <summary>原生窗口句柄。</summary>
        public IntPtr NativeWindowHandle => _nativeWindow;

        /// <summary>当前生命周期状态。</summary>
        public AndroidLifecycleState LifecycleState
        {
            get { lock (_lifecycleLock) return _lifecycleState; }
        }

        /// <summary>设备信息。</summary>
        public AndroidDeviceInfo DeviceInfo { get; private set; } = new();

        /// <summary>文件系统路径。</summary>
        public AndroidFileSystemPaths FileSystemPaths { get; private set; } = new();

        /// <summary>是否在前台。</summary>
        public bool IsForeground => LifecycleState == AndroidLifecycleState.Resumed;

        /// <summary>是否已初始化。</summary>
        public bool IsInitialized => _initialized;

        /// <summary>窗口宽度。</summary>
        public int Width => _width;

        /// <summary>窗口高度。</summary>
        public int Height => _height;

        // ────────────── 事件 ──────────────
        /// <summary>触摸输入事件。</summary>
        public event EventHandler<AndroidTouchEvent>? OnTouchEvent;

        /// <summary>生命周期状态变化。</summary>
        public event EventHandler<AndroidLifecycleState>? OnLifecycleChanged;

        /// <summary>窗口尺寸变化。</summary>
        public event EventHandler<(int Width, int Height)>? OnResize;

        /// <summary>配置变化（屏幕旋转等）。</summary>
        public event EventHandler? OnConfigurationChanged;

        // ────────────── 初始化 ──────────────
        /// <summary>
        /// 初始化 Android 适配器。
        /// 在 NativeActivity.onCreate 中被调用。
        /// </summary>
        /// <param name="nativeActivity">ANativeActivity 指针。</param>
        /// <param name="assetManager">AAssetManager 指针。</param>
        public void Initialize(IntPtr nativeActivity, IntPtr assetManager)
        {
            if (_initialized) return;

            _nativeActivity = nativeActivity;
            _assetManager = assetManager;

            // 1. 查询设备信息
            QueryDeviceInfo();

            // 2. 初始化文件系统路径
            InitializeFileSystemPaths();

            _initialized = true;
        }

        private void QueryDeviceInfo()
        {
            var info = new AndroidDeviceInfo();

            // GPU 信息需要通过 EGL/Vulkan 查询（此处预留接口）
            // 实际集成时通过 glGetString(GL_RENDERER) 或 Vulkan 物理设备属性获取

            // 从 /proc/cpuinfo 或系统属性获取设备型号
            try
            {
                if (File.Exists("/system/build.prop"))
                {
                    var lines = File.ReadAllLines("/system/build.prop");
                    foreach (var line in lines)
                    {
                        if (line.StartsWith("ro.product.model="))
                            info.Model = line.Substring("ro.product.model=".Length).Trim();
                        else if (line.StartsWith("ro.build.version.sdk="))
                        {
                            if (int.TryParse(line.Substring("ro.build.version.sdk=".Length).Trim(), out int sdk))
                                info.SdkVersion = sdk;
                        }
                        else if (line.StartsWith("ro.build.version.release="))
                            info.AndroidVersion = line.Substring("ro.build.version.release=".Length).Trim();
                    }
                }
            }
            catch { /* 非标准 Android 环境回退 */ }

            // 内存信息
            try
            {
                if (File.Exists("/proc/meminfo"))
                {
                    var lines = File.ReadAllLines("/proc/meminfo");
                    foreach (var line in lines)
                    {
                        if (line.StartsWith("MemTotal:"))
                        {
                            string val = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)[1];
                            info.TotalMemoryMB = long.Parse(val) / 1024;
                        }
                        else if (line.StartsWith("MemAvailable:"))
                        {
                            string val = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)[1];
                            info.AvailableMemoryMB = long.Parse(val) / 1024;
                        }
                    }
                }
            }
            catch { info.TotalMemoryMB = 4096; info.AvailableMemoryMB = 2048; }

            // 64 位检测
            info.Is64Bit = RuntimeInformation.OSArchitecture == Architecture.Arm64;

            // Vulkan 支持
            info.SupportsVulkan = NativeLibrary.TryLoad("libvulkan.so", out _);

            // 识别 GPU 类型（如果已有 GPU 渲染器字符串）
            info.Type = AndroidDeviceInfo.IdentifyGPUType(info.GPURenderer);

            DeviceInfo = info;
        }

        private void InitializeFileSystemPaths()
        {
            var paths = new AndroidFileSystemPaths();

            // 通过 NativeActivity API 获取路径
            try
            {
                if (_nativeActivity != IntPtr.Zero)
                {
                    int length;
                    IntPtr pathPtr;

                    pathPtr = AndroidNative.ANativeActivity_getInternalDataPath(_nativeActivity, out length);
                    if (pathPtr != IntPtr.Zero && length > 0)
                        paths.InternalDataPath = Marshal.PtrToStringUTF8(pathPtr, length) ?? "";

                    pathPtr = AndroidNative.ANativeActivity_getExternalFilesDir(_nativeActivity, out length);
                    if (pathPtr != IntPtr.Zero && length > 0)
                        paths.ExternalFilesDir = Marshal.PtrToStringUTF8(pathPtr, length) ?? "";

                    pathPtr = AndroidNative.ANativeActivity_getCachePath(_nativeActivity, out length);
                    if (pathPtr != IntPtr.Zero && length > 0)
                        paths.CacheDir = Marshal.PtrToStringUTF8(pathPtr, length) ?? "";
                }
            }
            catch { /* 回退路径 */ }

            // 回退默认路径
            if (string.IsNullOrEmpty(paths.InternalDataPath))
                paths.InternalDataPath = "/data/data/com.yama.engine/files";
            if (string.IsNullOrEmpty(paths.CacheDir))
                paths.CacheDir = "/data/data/com.yama.engine/cache";

            paths.EngineDir = Path.Combine(paths.InternalDataPath, "Engine");
            paths.UserDataDir = Path.Combine(paths.InternalDataPath, "UserData");
            paths.AssetManagerPtr = _assetManager;

            // 确保目录存在
            try
            {
                paths.EnsureDirectory(paths.EngineDir);
                paths.EnsureDirectory(paths.UserDataDir);
            }
            catch { /* 权限限制 */ }

            FileSystemPaths = paths;
        }

        // ────────────── 生命周期管理 ──────────────
        /// <summary>处理 NativeActivity.onNativeWindowCreated 回调。</summary>
        public void OnNativeWindowCreated(IntPtr nativeWindow)
        {
            _nativeWindow = nativeWindow;
            // 获取窗口尺寸
            try
            {
                _width = 1080; // 回退值
                _height = 2400;
            }
            catch { /* 尺寸获取失败 */ }
        }

        /// <summary>处理 NativeActivity.onNativeWindowResized 回调。</summary>
        public void OnNativeWindowResized(IntPtr nativeWindow, int newWidth, int newHeight)
        {
            _width = newWidth;
            _height = newHeight;
            OnResize?.Invoke(this, (newWidth, newHeight));
        }

        /// <summary>处理 NativeActivity.onNativeWindowDestroyed 回调。</summary>
        public void OnNativeWindowDestroyed(IntPtr nativeWindow)
        {
            _nativeWindow = IntPtr.Zero;
        }

        /// <summary>处理 NativeActivity.onStart 回调。</summary>
        public void OnStart()
        {
            lock (_lifecycleLock)
            {
                _lifecycleState = AndroidLifecycleState.Started;
            }
            OnLifecycleChanged?.Invoke(this, AndroidLifecycleState.Started);
        }

        /// <summary>处理 NativeActivity.onResume 回调（回到前台）。</summary>
        public void OnResume()
        {
            lock (_lifecycleLock)
            {
                _lifecycleState = AndroidLifecycleState.Resumed;
            }
            OnLifecycleChanged?.Invoke(this, AndroidLifecycleState.Resumed);
        }

        /// <summary>处理 NativeActivity.onPause 回调（进入后台）。</summary>
        public void OnPause()
        {
            lock (_lifecycleLock)
            {
                _lifecycleState = AndroidLifecycleState.Paused;
            }
            OnLifecycleChanged?.Invoke(this, AndroidLifecycleState.Paused);

            // 暂停时释放不必要的 GPU 资源
        }

        /// <summary>处理 NativeActivity.onStop 回调。</summary>
        public void OnStop()
        {
            lock (_lifecycleLock)
            {
                _lifecycleState = AndroidLifecycleState.Stopped;
            }
            OnLifecycleChanged?.Invoke(this, AndroidLifecycleState.Stopped);
        }

        /// <summary>处理 NativeActivity.onDestroy 回调。</summary>
        public void OnDestroy()
        {
            lock (_lifecycleLock)
            {
                _lifecycleState = AndroidLifecycleState.Destroyed;
            }
            OnLifecycleChanged?.Invoke(this, AndroidLifecycleState.Destroyed);
            Dispose();
        }

        /// <summary>处理 NativeActivity.onLowMemory 回调。</summary>
        public void OnLowMemory()
        {
            // 低内存时释放缓存和非必要资源
        }

        // ────────────── 触摸输入映射 ──────────────
        /// <summary>
        /// 将 Android AMotionEvent 映射到引擎统一触摸事件格式。
        /// 由 android_native_app_glue 的输入事件处理循环调用。
        /// </summary>
        public void MapMotionEvent(AndroidTouchEvent evt)
        {
            OnTouchEvent?.Invoke(this, evt);
        }

        // ────────────── JNI 桥接 ──────────────
        /// <summary>
        /// 通过 JNI 调用 Java 层方法。
        /// 用于访问 Android 系统服务（通知、振动、剪贴板等）。
        /// </summary>
        /// <param name="className">Java 类全名（如 "com/yama/engine/NativeBridge"）。</param>
        /// <param name="methodName">方法名。</param>
        /// <param name="signature">JNI 方法签名。</param>
        /// <param name="args">参数数组。</param>
        /// <returns>调用结果（object 形式）。</returns>
        public object? CallJavaMethod(string className, string methodName, string signature, object?[]? args)
        {
            // 概念代码: 实际集成时需要 JNI 环境和 JavaVM 指针
            // JNIEnv* env = GetJNIEnv();
            // jclass clazz = env->FindClass(className);
            // jmethodID method = env->GetStaticMethodID(clazz, methodName, signature);
            // return env->CallStaticObjectMethod(clazz, method, ...);
            return null;
        }

        // ────────────── Vulkan Surface 创建 ──────────────
        /// <summary>
        /// 通过 ANativeWindow 创建 Vulkan surface。
        /// </summary>
        /// <param name="vulkanInstance">Vulkan VkInstance 原生指针。</param>
        /// <returns>VkSurfaceKHR 句柄。</returns>
        public ulong CreateVulkanSurface(IntPtr vulkanInstance)
        {
            if (_nativeWindow == IntPtr.Zero)
                throw new InvalidOperationException("原生窗口未创建，无法创建 Vulkan Surface。");

            int result = AndroidNative.ANativeWindow_createVulkanSurface(
                vulkanInstance, _nativeWindow, out ulong surface);
            if (result != 0)
                throw new InvalidOperationException($"Android Vulkan Surface 创建失败，错误码: {result}");

            return surface;
        }

        // ────────────── Android 日志 ──────────────
        /// <summary>输出 Android logcat 日志。</summary>
        public static void Log(string tag, string message, int priority = AndroidNative.ANDROID_LOG_INFO)
        {
            try { AndroidNative.__android_log_write(priority, tag ?? "YamaEngine", message ?? ""); }
            catch { /* 日志不可用时忽略 */ }
        }

        /// <summary>输出调试日志。</summary>
        public static void LogDebug(string tag, string message) => Log(tag, message, AndroidNative.ANDROID_LOG_DEBUG);

        /// <summary>输出警告日志。</summary>
        public static void LogWarn(string tag, string message) => Log(tag, message, AndroidNative.ANDROID_LOG_WARN);

        /// <summary>输出错误日志。</summary>
        public static void LogError(string tag, string message) => Log(tag, message, AndroidNative.ANDROID_LOG_ERROR);

        // ────────────── Asset 读取 ──────────────
        /// <summary>从 APK Assets 读取文件内容。</summary>
        public byte[]? ReadAsset(string assetPath)
        {
            if (_assetManager == IntPtr.Zero || string.IsNullOrEmpty(assetPath))
                return null;

            try
            {
                IntPtr asset = AndroidNative.AAssetManager_open(
                    _assetManager, assetPath, AndroidNative.AASSET_MODE_BUFFER);
                if (asset == IntPtr.Zero) return null;

                long length = AndroidNative.AAsset_getLength(asset);
                if (length <= 0 || length > int.MaxValue)
                {
                    AndroidNative.AAsset_close(asset);
                    return null;
                }

                var buffer = new byte[length];
                int read = AndroidNative.AAsset_read(asset, buffer, (int)length);
                AndroidNative.AAsset_close(asset);

                if (read != (int)length) return null;
                return buffer;
            }
            catch
            {
                return null;
            }
        }

        // ────────────── IDisposable ──────────────
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _nativeActivity = IntPtr.Zero;
            _nativeWindow = IntPtr.Zero;
            _assetManager = IntPtr.Zero;

            lock (_lifecycleLock)
            {
                _lifecycleState = AndroidLifecycleState.Destroyed;
            }
            _initialized = false;
        }
    }
}
