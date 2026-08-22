// ==================================================================================================
//  PlatformAbstractionLayer.cs — 山雨引擎平台抽象层 (PAL)
//
//  命名空间: ShanyuEngine.Platform
//  依赖:     System.Runtime.InteropServices, 条件编译指令
//
//  功能:
//    1. IPlatformWindow 接口: Create / Destroy / GetSize / SetTitle / GetHandle / PumpEvents
//    2. IPlatformInput 接口: GetKeyboardState / GetMouseState / GetGamepadState / GetTouchState
//    3. IPlatformFileSystem 接口: GetAppDataPath / GetTempPath / GetEngineDir
//    4. IPlatformAudio 接口: GetAudioDeviceCount / GetDefaultOutputDevice
//    5. PlatformFactory: 根据运行时平台创建对应适配器
//    6. 条件编译: #if WINDOWS / #if LINUX / #if ANDROID / #if HARMONYOS
//    7. 统一 API，各平台通过实现 IPlatform 接口适配
// ==================================================================================================

using System;
using System.Collections.Generic;
using System.IO;

namespace ShanyuEngine.Platform
{
    // ════════════════════════════════════════════════════════════════════════════
    // 区域 1: 平台接口定义
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 平台窗口接口。所有平台必须实现此接口以提供统一的窗口操作 API。
    /// </summary>
    public interface IPlatformWindow : IDisposable
    {
        /// <summary>创建平台窗口。</summary>
        /// <param name="title">窗口标题。</param>
        /// <param name="width">窗口宽度。</param>
        /// <param name="height">窗口高度。</param>
        /// <param name="fullScreen">是否全屏。</param>
        void Create(string title, int width, int height, bool fullScreen = false);

        /// <summary>销毁平台窗口。</summary>
        void Destroy();

        /// <summary>获取窗口尺寸。</summary>
        (int Width, int Height) GetSize();

        /// <summary>获取可绘制区域尺寸（物理像素）。</summary>
        (int Width, int Height) GetDrawableSize();

        /// <summary>设置窗口标题。</summary>
        void SetTitle(string title);

        /// <summary>获取原生窗口句柄。</summary>
        IntPtr GetHandle();

        /// <summary>处理窗口事件。返回 false 表示窗口应关闭。</summary>
        bool PumpEvents();

        /// <summary>切换全屏模式。</summary>
        void ToggleFullScreen();

        /// <summary>设置窗口尺寸。</summary>
        void SetSize(int width, int height);

        /// <summary>窗口是否打开。</summary>
        bool IsOpen { get; }

        /// <summary>窗口是否拥有焦点。</summary>
        bool IsFocused { get; }

        /// <summary>DPI 缩放因子。</summary>
        float DpiScale { get; }

        /// <summary>创建 Vulkan surface。</summary>
        bool CreateVulkanSurface(IntPtr vulkanInstance, out ulong surface);

        /// <summary>窗口事件回调。</summary>
        event EventHandler<WindowResizeEventArgs>? OnResize;
        event EventHandler? OnClose;
        event EventHandler? OnFocusGained;
        event EventHandler? OnFocusLost;
    }

    /// <summary>
    /// 平台输入接口。统一键盘/鼠标/手柄/触摸的输入 API。
    /// </summary>
    public interface IPlatformInput : IDisposable
    {
        /// <summary>初始化输入子系统。</summary>
        void Initialize();

        /// <summary>每帧更新输入状态。</summary>
        void Update(float currentTime);

        // ──── 键盘 ────
        /// <summary>指定键是否按下。</summary>
        bool IsKeyDown(int scancode);
        /// <summary>指定键是否在当前帧刚按下。</summary>
        bool IsKeyPressed(int scancode);
        /// <summary>指定键是否在当前帧刚释放。</summary>
        bool IsKeyReleased(int scancode);

        // ──── 鼠标 ────
        /// <summary>获取鼠标绝对坐标。</summary>
        (int X, int Y) GetMousePosition();
        /// <summary>获取鼠标相对移动量。</summary>
        (int DeltaX, int DeltaY) GetMouseDelta();
        /// <summary>设置相对鼠标模式。</summary>
        void SetRelativeMouseMode(bool enabled);

        // ──── 手柄 ────
        /// <summary>已连接手柄数量。</summary>
        int GamepadCount { get; }
        /// <summary>获取手柄轴值（归一化 -1~1）。</summary>
        float GetGamepadAxis(int gamepadIndex, int axis);
        /// <summary>获取手柄按钮状态。</summary>
        bool GetGamepadButton(int gamepadIndex, int button);
        /// <summary>手柄震动。</summary>
        bool GamepadRumble(int gamepadIndex, ushort lowFreq, ushort highFreq, uint durationMs);

        // ──── 触摸 ────
        /// <summary>当前活动触摸点数。</summary>
        int ActiveTouchCount { get; }

        // ──── 手势 ────
        /// <summary>手势识别事件。</summary>
        event EventHandler<TouchGestureEventArgs>? OnTouchGesture;
    }

    /// <summary>
    /// 平台文件系统接口。抽象各平台的文件路径和访问差异。
    /// </summary>
    public interface IPlatformFileSystem
    {
        /// <summary>获取应用数据目录（持久化用户数据）。</summary>
        string GetAppDataPath();

        /// <summary>获取临时文件目录。</summary>
        string GetTempPath();

        /// <summary>获取引擎安装目录。</summary>
        string GetEngineDir();

        /// <summary>获取引擎着色器目录。</summary>
        string GetShaderDir();

        /// <summary>获取引擎资源目录。</summary>
        string GetAssetsDir();

        /// <summary>获取用户配置目录。</summary>
        string GetConfigDir();

        /// <summary>获取日志目录。</summary>
        string GetLogDir();

        /// <summary>检查路径是否可访问。</summary>
        bool IsPathAccessible(string path, bool requireWrite = false);

        /// <summary>确保目录存在。</summary>
        void EnsureDirectoryExists(string path);

        /// <summary>获取当前进程可执行文件所在目录。</summary>
        string GetExecutableDir();
    }

    /// <summary>
    /// 平台音频接口。抽象各平台的音频设备枚举。
    /// </summary>
    public interface IPlatformAudio : IDisposable
    {
        /// <summary>获取音频输出设备数量。</summary>
        int GetAudioDeviceCount();

        /// <summary>获取默认输出设备名称。</summary>
        string GetDefaultOutputDevice();

        /// <summary>获取指定索引的输出设备名称。</summary>
        string GetAudioDeviceName(int index);

        /// <summary>初始化音频子系统。</summary>
        void Initialize();

        /// <summary>获取默认采样率。</summary>
        int GetDefaultSampleRate();

        /// <summary>获取默认缓冲区大小。</summary>
        int GetDefaultBufferSize();
    }

    /// <summary>
    /// 综合平台接口。聚合窗口、输入、文件系统、音频四大子系统。
    /// 各平台适配器实现此接口，由 PlatformFactory 创建。
    /// </summary>
    public interface IPlatform : IDisposable
    {
        /// <summary>平台类型。</summary>
        WindowPlatform PlatformType { get; }

        /// <summary>平台描述字符串。</summary>
        string PlatformDescription { get; }

        /// <summary>窗口子系统。</summary>
        IPlatformWindow Window { get; }

        /// <summary>输入子系统。</summary>
        IPlatformInput Input { get; }

        /// <summary>文件系统子系统。</summary>
        IPlatformFileSystem FileSystem { get; }

        /// <summary>音频子系统。</summary>
        IPlatformAudio Audio { get; }

        /// <summary>初始化平台。</summary>
        /// <param name="windowWidth">窗口宽度。</param>
        /// <param name="windowHeight">窗口高度。</param>
        /// <param name="title">窗口标题。</param>
        /// <param name="fullScreen">是否全屏。</param>
        void Initialize(int windowWidth, int windowHeight, string title = "ShanyuEngine", bool fullScreen = false);

        /// <summary>主循环 Tick（含事件泵）。</summary>
        bool Tick();

        /// <summary>获取平台特定能力。</summary>
        IPlatformCapabilities Capabilities { get; }
    }

    /// <summary>
    /// 平台能力接口。描述各平台的硬件/软件能力上限。
    /// </summary>
    public interface IPlatformCapabilities
    {
        /// <summary>最大纹理尺寸。</summary>
        int MaxTextureSize { get; }
        /// <summary>最大纹理数组层数。</summary>
        int MaxTextureArrayLayers { get; }
        /// <summary>是否支持计算着色器。</summary>
        bool SupportsComputeShaders { get; }
        /// <summary>是否支持光线追踪。</summary>
        bool SupportsRayTracing { get; }
        /// <summary>是否支持网格着色器。</summary>
        bool SupportsMeshShaders { get; }
        /// <summary>是否支持可变速率着色。</summary>
        bool SupportsVariableRateShading { get; }
        /// <summary>最大各向异性级别。</summary>
        int MaxAnisotropy { get; }
        /// <summary>设备内存大小（字节）。</summary>
        ulong DeviceMemoryBytes { get; }
        /// <summary>推荐质量级别 (0~4)。</summary>
        int RecommendedQualityLevel { get; }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 2: 通用文件系统实现
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 基础文件系统实现。提供跨平台默认路径逻辑，
    /// 各平台可继承并覆盖特定路径。
    /// </summary>
    public abstract class BasePlatformFileSystem : IPlatformFileSystem
    {
        private string? _cachedAppData;
        private string? _cachedTemp;
        private string? _cachedEngineDir;

        /// <inheritdoc/>
        public virtual string GetAppDataPath()
        {
            _cachedAppData ??= BuildAppDataPath();
            return _cachedAppData;
        }

        /// <inheritdoc/>
        public virtual string GetTempPath()
        {
            _cachedTemp ??= Path.GetTempPath();
            return _cachedTemp;
        }

        /// <inheritdoc/>
        public virtual string GetEngineDir()
        {
            _cachedEngineDir ??= Path.Combine(GetExecutableDir(), "Engine");
            return _cachedEngineDir;
        }

        /// <inheritdoc/>
        public virtual string GetShaderDir()
        {
            return Path.Combine(GetEngineDir(), "Shaders");
        }

        /// <inheritdoc/>
        public virtual string GetAssetsDir()
        {
            return Path.Combine(GetEngineDir(), "Assets");
        }

        /// <inheritdoc/>
        public virtual string GetConfigDir()
        {
            return Path.Combine(GetAppDataPath(), "Config");
        }

        /// <inheritdoc/>
        public virtual string GetLogDir()
        {
            return Path.Combine(GetAppDataPath(), "Logs");
        }

        /// <inheritdoc/>
        public virtual bool IsPathAccessible(string path, bool requireWrite = false)
        {
            if (string.IsNullOrEmpty(path)) return false;
            try
            {
                if (requireWrite)
                {
                    string? dir = Path.GetDirectoryName(path);
                    if (string.IsNullOrEmpty(dir)) dir = path;
                    return Directory.Exists(dir) && !((File.GetAttributes(dir) & FileAttributes.ReadOnly) != 0);
                }
                return File.Exists(path) || Directory.Exists(path);
            }
            catch { return false; }
        }

        /// <inheritdoc/>
        public virtual void EnsureDirectoryExists(string path)
        {
            if (!string.IsNullOrEmpty(path) && !Directory.Exists(path))
                Directory.CreateDirectory(path);
        }

        /// <inheritdoc/>
        public virtual string GetExecutableDir()
        {
            return AppContext.BaseDirectory;
        }

        /// <summary>构建应用数据路径（由子类实现平台特定逻辑）。</summary>
        protected abstract string BuildAppDataPath();
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 3: 通用音频实现
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 基础音频实现。各平台可继承并覆盖特定行为。
    /// </summary>
    public abstract class BasePlatformAudio : IPlatformAudio
    {
        private bool _initialized;
        private bool _disposed;

        /// <inheritdoc/>
        public abstract int GetAudioDeviceCount();
        /// <inheritdoc/>
        public abstract string GetDefaultOutputDevice();
        /// <inheritdoc/>
        public abstract string GetAudioDeviceName(int index);

        /// <inheritdoc/>
        public virtual void Initialize()
        {
            _initialized = true;
        }

        /// <inheritdoc/>
        public virtual int GetDefaultSampleRate() => 48000;

        /// <inheritdoc/>
        public virtual int GetDefaultBufferSize() => 2048;

        /// <inheritdoc/>
        public bool IsInitialized => _initialized;

        /// <inheritdoc/>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _initialized = false;
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 4: SDL2 平台接口实现（跨平台默认方案）
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// SDL2 窗口适配器（实现 IPlatformWindow）。
    /// 包装 SDL2Window 类，提供统一接口。
    /// </summary>
    internal sealed class SDL2WindowAdapter : IPlatformWindow
    {
        private readonly SDL2Window _window;
        private bool _disposed;

        public SDL2WindowAdapter(SDL2Window window) => _window = window;

        public void Create(string title, int width, int height, bool fullScreen = false)
        {
            _window.Title = title;
            _window.Create(useVulkan: true, fullScreen: fullScreen, highDpi: true);
        }

        public void Destroy() => Dispose();

        public (int Width, int Height) GetSize() => (_window.Width, _window.Height);

        public (int Width, int Height) GetDrawableSize() => (_window.DrawableWidth, _window.DrawableHeight);

        public void SetTitle(string title) => _window.Title = title;

        public IntPtr GetHandle() => _window.Handle;

        public bool PumpEvents() => _window.PumpEvents();

        public void ToggleFullScreen() => _window.ToggleFullScreen();

        public void SetSize(int width, int height) => _window.SetSize(width, height);

        public bool IsOpen => _window.IsOpen;
        public bool IsFocused => _window.IsFocused;
        public float DpiScale => _window.DpiScale;

        public bool CreateVulkanSurface(IntPtr vulkanInstance, out ulong surface)
        {
            surface = 0;
            return _window.CreateVulkanSurface(vulkanInstance, out surface);
        }

        public event EventHandler<WindowResizeEventArgs>? OnResize
        {
            add => _window.OnResize += value;
            remove => _window.OnResize -= value;
        }
        public event EventHandler? OnClose
        {
            add => _window.OnClose += value;
            remove => _window.OnClose -= value;
        }
        public event EventHandler? OnFocusGained
        {
            add => _window.OnFocusGained += value;
            remove => _window.OnFocusGained -= value;
        }
        public event EventHandler? OnFocusLost
        {
            add => _window.OnFocusLost += value;
            remove => _window.OnFocusLost -= value;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _window.Dispose();
        }
    }

    /// <summary>
    /// SDL2 输入适配器（实现 IPlatformInput）。
    /// 包装 SDL2InputManager 类。
    /// </summary>
    internal sealed class SDL2InputAdapter : IPlatformInput
    {
        private readonly SDL2InputManager _input;

        public SDL2InputAdapter(SDL2InputManager input) => _input = input;

        public void Initialize() => _input.Initialize();
        public void Update(float currentTime) => _input.Update(currentTime);

        public bool IsKeyDown(int scancode) => _input.IsKeyDown(scancode);
        public bool IsKeyPressed(int scancode) => _input.IsKeyPressed(scancode);
        public bool IsKeyReleased(int scancode) => _input.IsKeyReleased(scancode);

        public (int X, int Y) GetMousePosition() => _input.GetMousePosition();
        public (int DeltaX, int DeltaY) GetMouseDelta() => _input.GetMouseDelta();
        public void SetRelativeMouseMode(bool enabled) { /* SDL2Native.SDL_SetRelativeMouseMode */ }

        public int GamepadCount => _input.GamepadCount;
        public float GetGamepadAxis(int gamepadIndex, int axis) => _input.GetGamepadAxis(gamepadIndex, axis);
        public bool GetGamepadButton(int gamepadIndex, int button) => _input.GetGamepadButton(gamepadIndex, button);
        public bool GamepadRumble(int gp, ushort lf, ushort hf, uint ms) => _input.GamepadRumble(gp, lf, hf, ms);

        public int ActiveTouchCount => _input.ActiveTouchCount;

        public event EventHandler<TouchGestureEventArgs>? OnTouchGesture
        {
            add { /* 委托到 _input */ }
            remove { /* 委托到 _input */ }
        }

        public void Dispose() => _input.Dispose();
    }

    /// <summary>
    /// 通用文件系统（实现 IPlatformFileSystem）。
    /// 使用标准 .NET API 提供跨平台文件路径。
    /// </summary>
    internal sealed class GenericPlatformFileSystem : BasePlatformFileSystem
    {
        private readonly WindowPlatform _platform;

        public GenericPlatformFileSystem(WindowPlatform platform) => _platform = platform;

        protected override string BuildAppDataPath()
        {
            // 标准 .NET 跨平台应用数据路径
            string baseDir = _platform switch
            {
                WindowPlatform.Windows => Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ShanyuEngine"),
                WindowPlatform.Linux => Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "shanyu-engine"),
                WindowPlatform.macOS => Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ShanyuEngine"),
                WindowPlatform.Android => "/data/data/com.shanyu.engine/files",
                WindowPlatform.HarmonyOS => "/data/app/shanyu_engine/files",
                _ => Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ShanyuEngine")
            };

            EnsureDirectoryExists(baseDir);
            return baseDir;
        }
    }

    /// <summary>
    /// 通用音频适配器（实现 IPlatformAudio）。
    /// </summary>
    internal sealed class GenericPlatformAudio : BasePlatformAudio
    {
        public override int GetAudioDeviceCount() => 1;
        public override string GetDefaultOutputDevice() => "Default";
        public override string GetAudioDeviceName(int index) => index == 0 ? "Default" : "";
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 5: 平台能力默认实现
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 默认平台能力。可在运行时根据实际 GPU 信息更新。
    /// </summary>
    public sealed class DefaultPlatformCapabilities : IPlatformCapabilities
    {
        public int MaxTextureSize { get; set; } = 16384;
        public int MaxTextureArrayLayers { get; set; } = 256;
        public bool SupportsComputeShaders { get; set; } = true;
        public bool SupportsRayTracing { get; set; } = false;
        public bool SupportsMeshShaders { get; set; } = false;
        public bool SupportsVariableRateShading { get; set; } = false;
        public int MaxAnisotropy { get; set; } = 16;
        public ulong DeviceMemoryBytes { get; set; } = 4UL * 1024 * 1024 * 1024; // 4GB
        public int RecommendedQualityLevel { get; set; } = 2;
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 6: SDL2 平台实现（综合 IPlatform）
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 基于 SDL2 的通用跨平台实现。
    /// 作为 Windows/Linux/macOS/Android 的默认平台方案。
    /// HarmonyOS 和特殊平台使用专用适配器。
    /// </summary>
    internal sealed class SDL2Platform : IPlatform
    {
        private SDL2Window? _sdl2Window;
        private SDL2WindowAdapter? _windowAdapter;
        private SDL2InputAdapter? _inputAdapter;
        private GenericPlatformFileSystem? _fileSystem;
        private GenericPlatformAudio? _audio;
        private bool _initialized;
        private bool _disposed;

        public SDL2Platform(WindowPlatform platformType)
        {
            PlatformType = platformType;
            PlatformDescription = $"SDL2 ({platformType})";
            Capabilities = new DefaultPlatformCapabilities();
        }

        public WindowPlatform PlatformType { get; }
        public string PlatformDescription { get; }

        public IPlatformWindow Window => _windowAdapter ?? throw new InvalidOperationException("平台未初始化。");
        public IPlatformInput Input => _inputAdapter ?? throw new InvalidOperationException("平台未初始化。");
        public IPlatformFileSystem FileSystem => _fileSystem ?? throw new InvalidOperationException("平台未初始化。");
        public IPlatformAudio Audio => _audio ?? throw new InvalidOperationException("平台未初始化。");
        public IPlatformCapabilities Capabilities { get; }

        public void Initialize(int windowWidth, int windowHeight, string title = "ShanyuEngine", bool fullScreen = false)
        {
            if (_initialized) return;

            // 创建 SDL2 窗口
            _sdl2Window = new SDL2Window(windowWidth, windowHeight, title);
            _sdl2Window.Create(useVulkan: true, fullScreen: fullScreen, highDpi: true);

            // 创建适配器
            _windowAdapter = new SDL2WindowAdapter(_sdl2Window);
            _inputAdapter = new SDL2InputAdapter(_sdl2Window.Input);
            _fileSystem = new GenericPlatformFileSystem(PlatformType);
            _audio = new GenericPlatformAudio();
            _audio.Initialize();

            _initialized = true;
        }

        public bool Tick()
        {
            if (!_initialized || _sdl2Window == null) return false;
            return _sdl2Window.PumpEvents();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _inputAdapter?.Dispose();
            _windowAdapter?.Dispose();
            _audio?.Dispose();
            _initialized = false;
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 7: PlatformFactory — 平台工厂
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 平台工厂。根据运行时检测的平台类型创建对应的 IPlatform 实例。
    /// 支持条件编译和运行时检测两种模式。
    /// </summary>
    public static class PlatformFactory
    {
        private static IPlatform? _currentPlatform;

        /// <summary>当前平台实例。</summary>
        public static IPlatform Current => _currentPlatform ?? throw new InvalidOperationException(
            "平台未创建。请先调用 PlatformFactory.CreatePlatform()。");

        /// <summary>是否已创建平台实例。</summary>
        public static bool IsCreated => _currentPlatform != null;

        /// <summary>
        /// 创建平台实例。
        /// 自动检测运行时平台并创建对应的 IPlatform 实现。
        /// </summary>
        /// <param name="windowWidth">窗口宽度。</param>
        /// <param name="windowHeight">窗口高度。</param>
        /// <param name="title">窗口标题。</param>
        /// <param name="fullScreen">是否全屏。</param>
        /// <returns>创建的平台实例。</returns>
        public static IPlatform CreatePlatform(
            int windowWidth = 1280, int windowHeight = 720,
            string title = "ShanyuEngine", bool fullScreen = false)
        {
            var platform = DetectAndCreate();
            platform.Initialize(windowWidth, windowHeight, title, fullScreen);
            _currentPlatform = platform;
            return platform;
        }

        /// <summary>
        /// 强制指定平台类型创建（用于测试或交叉编译场景）。
        /// </summary>
        public static IPlatform CreatePlatform(
            WindowPlatform forcedPlatform,
            int windowWidth = 1280, int windowHeight = 720,
            string title = "ShanyuEngine", bool fullScreen = false)
        {
            var platform = CreateForPlatform(forcedPlatform);
            platform.Initialize(windowWidth, windowHeight, title, fullScreen);
            _currentPlatform = platform;
            return platform;
        }

        private static IPlatform DetectAndCreate()
        {
            var detected = PlatformDetect.CurrentPlatform;
            return CreateForPlatform(detected);
        }

        private static IPlatform CreateForPlatform(WindowPlatform platform)
        {
            // ── 条件编译路径 ──
            // 编译期确定平台时，编译器会只保留对应分支，
            // 减少运行时开销和代码体积。

#if WINDOWS
            return CreateWindowsPlatform();
#elif LINUX
            return CreateLinuxPlatform();
#elif ANDROID
            return CreateAndroidPlatform();
#elif HARMONYOS
            return CreateHarmonyOSPlatform();
#elif MACOS
            return CreateMacOSPlatform();
#else
            // ── 运行时检测路径 ──
            return platform switch
            {
                WindowPlatform.Windows => CreateWindowsPlatform(),
                WindowPlatform.Linux => CreateLinuxPlatform(),
                WindowPlatform.Android => CreateAndroidPlatform(),
                WindowPlatform.HarmonyOS => CreateHarmonyOSPlatform(),
                WindowPlatform.macOS => CreateMacOSPlatform(),
                _ => CreateFallbackPlatform(platform)
            };
#endif
        }

        private static IPlatform CreateWindowsPlatform()
        {
            // Windows 默认使用 SDL2 跨平台方案
            // 也可使用 Win32Window 专属方案
            return new SDL2Platform(WindowPlatform.Windows);
        }

        private static IPlatform CreateLinuxPlatform()
        {
            return new SDL2Platform(WindowPlatform.Linux);
        }

        private static IPlatform CreateAndroidPlatform()
        {
            return new SDL2Platform(WindowPlatform.Android);
        }

        private static IPlatform CreateHarmonyOSPlatform()
        {
            // HarmonyOS 使用专用适配器
            return new HarmonyOSPlatformAdapter();
        }

        private static IPlatform CreateMacOSPlatform()
        {
            return new SDL2Platform(WindowPlatform.macOS);
        }

        private static IPlatform CreateFallbackPlatform(WindowPlatform platform)
        {
            return new SDL2Platform(platform);
        }

        /// <summary>销毁当前平台实例。</summary>
        public static void Destroy()
        {
            _currentPlatform?.Dispose();
            _currentPlatform = null;
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 8: HarmonyOS 专用 IPlatform 实现
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// HarmonyOS 平台的 IPlatform 实现。使用 HarmonyOSAdapter。
    /// </summary>
    internal sealed class HarmonyOSPlatformAdapter : IPlatform
    {
        private HarmonyOSAdapter? _adapter;
        private HarmonyOSWindowAdapter? _windowAdapter;
        private HarmonyOSInputAdapter? _inputAdapter;
        private HarmonyOSFileSystemAdapter? _fileSystem;
        private GenericPlatformAudio? _audio;
        private bool _initialized;
        private bool _disposed;

        public WindowPlatform PlatformType => WindowPlatform.HarmonyOS;
        public string PlatformDescription => "HarmonyOS (Native API)";
        public IPlatformCapabilities Capabilities { get; } = new DefaultPlatformCapabilities();

        public IPlatformWindow Window => _windowAdapter ?? throw new InvalidOperationException("平台未初始化。");
        public IPlatformInput Input => _inputAdapter ?? throw new InvalidOperationException("平台未初始化。");
        public IPlatformFileSystem FileSystem => _fileSystem ?? throw new InvalidOperationException("平台未初始化。");
        public IPlatformAudio Audio => _audio ?? throw new InvalidOperationException("平台未初始化。");

        public void Initialize(int windowWidth, int windowHeight, string title = "ShanyuEngine", bool fullScreen = false)
        {
            if (_initialized) return;

            _adapter = new HarmonyOSAdapter();
            _adapter.Initialize(windowWidth, windowHeight);

            _windowAdapter = new HarmonyOSWindowAdapter(_adapter);
            _inputAdapter = new HarmonyOSInputAdapter(_adapter);
            _fileSystem = new HarmonyOSFileSystemAdapter(_adapter);
            _audio = new GenericPlatformAudio();
            _audio.Initialize();

            _initialized = true;
        }

        public bool Tick()
        {
            // HarmonyOS 主循环由 ArkTS 事件循环驱动
            return _initialized;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _adapter?.Dispose();
            _inputAdapter?.Dispose();
            _windowAdapter?.Dispose();
            _audio?.Dispose();
            _initialized = false;
        }
    }

    // ────────────── HarmonyOS 子适配器 ──────────────
    internal sealed class HarmonyOSWindowAdapter : IPlatformWindow
    {
        private readonly HarmonyOSAdapter _adapter;
        private bool _open = true;

        public HarmonyOSWindowAdapter(HarmonyOSAdapter adapter) => _adapter = adapter;

        public void Create(string title, int width, int height, bool fullScreen = false)
        {
            _adapter.Initialize(width, height);
            _open = true;
        }
        public void Destroy() { _adapter.Dispose(); _open = false; }
        public (int Width, int Height) GetSize() => (_adapter.Width, _adapter.Height);
        public (int Width, int Height) GetDrawableSize() => (_adapter.Width, _adapter.Height);
        public void SetTitle(string title) { /* HarmonyOS 窗口标题由 ArkTS 层管理 */ }
        public IntPtr GetHandle() => _adapter.NativeWindowHandle;
        public bool PumpEvents() => _open;
        public void ToggleFullScreen() { /* HarmonyOS 全屏切换 */ }
        public void SetSize(int w, int h) => _adapter.SetWindowSize(w, h);
        public bool IsOpen => _open;
        public bool IsFocused => true;
        public float DpiScale => 1.0f;
        public bool CreateVulkanSurface(IntPtr vkInstance, out ulong surface)
        {
            surface = _adapter.CreateVulkanSurface(vkInstance);
            return true;
        }

        public event EventHandler<WindowResizeEventArgs>? OnResize;
        public event EventHandler? OnClose;
        public event EventHandler? OnFocusGained;
        public event EventHandler? OnFocusLost;

        public void Dispose() { _adapter.Dispose(); _open = false; }
    }

    internal sealed class HarmonyOSInputAdapter : IPlatformInput
    {
        private readonly HarmonyOSAdapter _adapter;

        public HarmonyOSInputAdapter(HarmonyOSAdapter adapter) => _adapter = adapter;

        public void Initialize() { }
        public void Update(float currentTime) { }

        public bool IsKeyDown(int scancode) => false;
        public bool IsKeyPressed(int scancode) => false;
        public bool IsKeyReleased(int scancode) => false;

        public (int X, int Y) GetMousePosition() => (0, 0);
        public (int DeltaX, int DeltaY) GetMouseDelta() => (0, 0);
        public void SetRelativeMouseMode(bool enabled) { }

        public int GamepadCount => 0;
        public float GetGamepadAxis(int gp, int axis) => 0;
        public bool GetGamepadButton(int gp, int btn) => false;
        public bool GamepadRumble(int gp, ushort lf, ushort hf, uint ms) => false;

        public int ActiveTouchCount => 0;
        public event EventHandler<TouchGestureEventArgs>? OnTouchGesture;

        public void Dispose() { }
    }

    internal sealed class HarmonyOSFileSystemAdapter : BasePlatformFileSystem
    {
        private readonly HarmonyOSAdapter _adapter;

        public HarmonyOSFileSystemAdapter(HarmonyOSAdapter adapter) => _adapter = adapter;

        protected override string BuildAppDataPath()
        {
            return _adapter.FileSystemPaths.UserDataDir;
        }

        public override string GetEngineDir()
        {
            return _adapter.FileSystemPaths.EngineDir;
        }

        public override string GetTempPath()
        {
            return _adapter.FileSystemPaths.TempDir;
        }
    }
}
