// ==================================================================================================
//  LinuxAdapter.cs — 山雨引擎 Linux 平台适配层
//
//  命名空间: ShanyuEngine.Platform
//  依赖:     System.Runtime.InteropServices, X11 / Wayland / evdev (P/Invoke)
//
//  功能:
//    1. X11 / Wayland 窗口系统支持
//    2. 输入设备热插拔（evdev）
//    3. 文件系统权限处理
//    4. Vulkan 实例创建（Linux 特定的 Layer 选择）
//    5. Linux 发行版检测
//    6. GPU 驱动信息查询 (Mesa / NVIDIA / AMDGPU-PRO)
// ==================================================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace ShanyuEngine.Platform
{
    // ════════════════════════════════════════════════════════════════════════════
    // 区域 1: Linux 原生 API P/Invoke 声明
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Linux 原生函数 P/Invoke 声明。
    /// 覆盖 X11, Wayland, evdev, 以及 Linux 系统信息 API。
    /// </summary>
    internal static class LinuxNative
    {
        // ────────────── X11 API ──────────────
        private const string X11Lib = "libX11.so.6";
        private const string X11ExtLib = "libX11-xcb.so.1";

        [DllImport(X11Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr XOpenDisplay([MarshalAs(UnmanagedType.LPUTF8Str)] string? displayName);

        [DllImport(X11Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int XCloseDisplay(IntPtr display);

        [DllImport(X11Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int XDisplayWidth(IntPtr display, int screen);

        [DllImport(X11Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int XDisplayHeight(IntPtr display, int screen);

        [DllImport(X11Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr XDefaultRootWindow(IntPtr display);

        [DllImport(X11Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int XFlush(IntPtr display);

        [DllImport(X11Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int XSync(IntPtr display, int discard);

        [DllImport(X11Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int XPending(IntPtr display);

        [DllImport(X11Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int XNextEvent(IntPtr display, out XEvent event_return);

        // X11 事件类型常量
        public const int ClientMessage = 33;
        public const int ResizeRequest = 23;
        public const int ConfigureNotify = 22;
        public const int KeyPress = 2;
        public const int KeyRelease = 3;
        public const int ButtonPress = 4;
        public const int ButtonRelease = 5;
        public const int MotionNotify = 6;
        public const int FocusIn = 9;
        public const int FocusOut = 10;
        public const int MapNotify = 19;
        public const int UnmapNotify = 18;
        public const int DestroyNotify = 17;
        public const int Expose = 12;
        public const int SelectionNotify = 31;

        // ────────────── XCB API (Wayland 兼容) ──────────────
        private const string XCBLib = "libxcb.so.1";

        [DllImport(XCBLib, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr xcb_connect(IntPtr displayName, out int screen);

        [DllImport(XCBLib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void xcb_disconnect(IntPtr connection);

        // ────────────── Wayland API ──────────────
        private const string WaylandLib = "libwayland-client.so.0";

        [DllImport(WaylandLib, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr wl_display_connect([MarshalAs(UnmanagedType.LPUTF8Str)] string? name);

        [DllImport(WaylandLib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void wl_display_disconnect(IntPtr display);

        [DllImport(WaylandLib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int wl_display_roundtrip(IntPtr display);

        [DllImport(WaylandLib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int wl_display_dispatch(IntPtr display);

        [DllImport(WaylandLib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int wl_display_dispatch_pending(IntPtr display);

        [DllImport(WaylandLib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int wl_display_get_fd(IntPtr display);

        [DllImport(WaylandLib, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr wl_display_get_registry(IntPtr display);

        // ────────────── evdev 输入设备 ──────────────
        private const string EvdevLib = "libevdev.so.2";

        [DllImport(EvdevLib, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr libevdev_new();

        [DllImport(EvdevLib, CallingConvention = CallingConvention.Cdecl)]
        public static extern void libevdev_free(IntPtr dev);

        [DllImport(EvdevLib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int libevdev_set_fd(IntPtr dev, int fd);

        [DllImport(EvdevLib, CallingConvention = CallingConvention.Cdecl)]
        public static extern string libevdev_get_name(IntPtr dev);

        // ────────────── Linux 系统 API ──────────────
        private const string LibC = "libc.so.6";

        [DllImport(LibC, CallingConvention = CallingConvention.Cdecl)]
        public static extern int uname(IntPtr buf);

        [DllImport(LibC, CallingConvention = CallingConvention.Cdecl)]
        public static extern int geteuid();

        [DllImport(LibC, CallingConvention = CallingConvention.Cdecl)]
        public static extern int getegid();

        [DllImport(LibC, CallingConvention = CallingConvention.Cdecl)]
        public static extern int chmod(string path, int mode);

        [DllImport(LibC, CallingConvention = CallingConvention.Cdecl)]
        public static extern int access(string path, int mode);

        [DllImport(LibC, CallingConvention = CallingConvention.Cdecl)]
        public static extern uint getuid();

        // access() 模式常量
        public const int F_OK = 0;
        public const int R_OK = 4;
        public const int W_OK = 2;
        public const int X_OK = 1;

        // 文件权限模式
        public const int S_IRWXU = 0x01C0;  // rwx for owner
        public const int S_IRUSR = 0x0100;
        public const int S_IWUSR = 0x0080;
        public const int S_IXUSR = 0x0040;
        public const int S_IRWXG = 0x0038;  // rwx for group
        public const int S_IRWXO = 0x0007;  // rwx for others
    }

    /// <summary>X11 事件结构体。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct XEvent
    {
        public int Type;
        private readonly ulong _serial;
        private readonly int _send_event;
        public IntPtr Display;
        public IntPtr Window;
        private readonly int _root;
        private readonly int _subwindow;
        private readonly int _time;
        private readonly int _x;
        private readonly int _y;
        private readonly int _x_root;
        private readonly int _y_root;
        private readonly int _state;
        private readonly int _keycode;
        private readonly int _same_screen;
        // 实际事件联合体更大，但此处只用到头部公共字段
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 2: Linux 窗口系统类型
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Linux 窗口系统类型。
    /// </summary>
    public enum LinuxWindowSystemType
    {
        Unknown,
        X11,
        Wayland,
        XWayland  // X11 应用运行在 Wayland XWayland 兼容层上
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 3: Linux 发行版信息
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Linux 发行版信息。从 /etc/os-release 解析。
    /// </summary>
    public sealed class LinuxDistributionInfo
    {
        /// <summary>发行版 ID（如 ubuntu, fedora, arch, debian）。</summary>
        public string Id { get; set; } = "unknown";

        /// <summary>发行版全名（如 Ubuntu 22.04.3 LTS）。</summary>
        public string PrettyName { get; set; } = "Unknown Linux";

        /// <summary>版本号。</summary>
        public string Version { get; set; } = "0.0";

        /// <summary>版本代号。</summary>
        public string VersionCodename { get; set; } = "";

        /// <summary>是否为已知发行版。</summary>
        public bool IsKnown => Id != "unknown";

        /// <summary>解析 /etc/os-release 文件。</summary>
        public static LinuxDistributionInfo Parse()
        {
            var info = new LinuxDistributionInfo();
            string osRelease = "/etc/os-release";

            if (!File.Exists(osRelease))
                osRelease = "/usr/lib/os-release";

            if (!File.Exists(osRelease))
                return info;

            try
            {
                foreach (var line in File.ReadAllLines(osRelease))
                {
                    if (line.StartsWith("#") || !line.Contains("="))
                        continue;

                    int idx = line.IndexOf('=');
                    string key = line.Substring(0, idx).Trim();
                    string value = line.Substring(idx + 1).Trim().Trim('"');

                    switch (key)
                    {
                        case "ID": info.Id = value.ToLowerInvariant(); break;
                        case "PRETTY_NAME": info.PrettyName = value; break;
                        case "VERSION_ID": info.Version = value; break;
                        case "VERSION_CODENAME": info.VersionCodename = value; break;
                    }
                }
            }
            catch { /* 解析失败使用默认值 */ }

            return info;
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 4: Linux GPU 驱动信息
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Linux GPU 驱动信息。识别 Mesa / NVIDIA / AMDGPU-PRO 等驱动。
    /// </summary>
    public sealed class LinuxGPUInfo
    {
        /// <summary>GPU 厂商。</summary>
        public string Vendor { get; set; } = "Unknown";

        /// <summary>GPU 渲染器。</summary>
        public string Renderer { get; set; } = "Unknown";

        /// <summary>驱动名称（如 i965, iris, radeonsi, nvidia-drm）。</summary>
        public string DriverName { get; set; } = "Unknown";

        /// <summary>Mesa 版本号（如 23.2.1）。</summary>
        public string MesaVersion { get; set; } = "";

        /// <summary>NVIDIA 驱动版本号。</summary>
        public string NvidiaVersion { get; set; } = "";

        /// <summary>Vulkan ICD 信息。</summary>
        public List<string> VulkanICDs { get; set; } = new();

        /// <summary>是否支持 Vulkan。</summary>
        public bool SupportsVulkan => VulkanICDs.Count > 0;

        /// <summary>从 /sys 或 /proc 解析 GPU 驱动信息。</summary>
        public static LinuxGPUInfo Detect()
        {
            var info = new LinuxGPUInfo();

            // 检测 NVIDIA
            if (File.Exists("/proc/driver/nvidia/version"))
            {
                try
                {
                    string nvidiaVer = File.ReadAllText("/proc/driver/nvidia/version");
                    info.Vendor = "NVIDIA";
                    info.DriverName = "nvidia-drm";
                    // 提取版本号: NVRM version  NVIDIA UNIX x86_64 Kernel Module  535.129.03
                    int idx = nvidiaVer.IndexOf("  ", StringComparison.Ordinal);
                    if (idx > 0)
                        info.NvidiaVersion = nvidiaVer.Substring(idx).Trim().Split(' ')[0];
                }
                catch { /* 忽略 */ }
            }

            // 检测 AMDGPU
            foreach (var drmDir in Directory.GetDirectories("/sys/class/drm"))
            {
                if (File.Exists(Path.Combine(drmDir, "device", "vendor")))
                {
                    try
                    {
                        string vendorFile = File.ReadAllText(Path.Combine(drmDir, "device", "vendor")).Trim();
                        if (vendorFile == "0x1002") // AMD
                        {
                            info.Vendor = "AMD";
                            info.DriverName = "amdgpu";
                            break;
                        }
                    }
                    catch { /* 忽略 */ }
                }
            }

            // 检测 Intel
            foreach (var drmDir in Directory.GetDirectories("/sys/class/drm"))
            {
                if (File.Exists(Path.Combine(drmDir, "device", "vendor")))
                {
                    try
                    {
                        string vendorFile = File.ReadAllText(Path.Combine(drmDir, "device", "vendor")).Trim();
                        if (vendorFile == "0x8086") // Intel
                        {
                            info.Vendor = "Intel";
                            info.DriverName = "i915";
                            break;
                        }
                    }
                    catch { /* 忽略 */ }
                }
            }

            // 检测 Vulkan ICD
            string vulkanIcdDir = "/etc/vulkan/icd.d";
            string vulkanIcdDir2 = "/usr/share/vulkan/icd.d";
            string vulkanIcdDir3 = "/usr/local/share/vulkan/icd.d";

            foreach (var dir in new[] { vulkanIcdDir, vulkanIcdDir2, vulkanIcdDir3 })
            {
                if (Directory.Exists(dir))
                {
                    foreach (var file in Directory.GetFiles(dir, "*.json"))
                        info.VulkanICDs.Add(Path.GetFileNameWithoutExtension(file));
                }
            }

            return info;
        }

        /// <summary>是否为 Mesa 开源驱动。</summary>
        public bool IsMesa => DriverName is "i965" or "iris" or "radeonsi" or "r600" or "zink" or "llvmpipe";

        /// <summary>是否为 NVIDIA 专有驱动。</summary>
        public bool IsNvidia => Vendor == "NVIDIA";

        /// <summary>是否为 AMDGPU。</summary>
        public bool IsAMDGPU => DriverName == "amdgpu";
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 5: Linux evdev 输入设备信息
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// evdev 输入设备信息。用于热插拔检测。
    /// </summary>
    public sealed class EvdevDeviceInfo
    {
        /// <summary>设备路径（如 /dev/input/event0）。</summary>
        public string DevicePath { get; set; } = "";

        /// <summary>设备名称。</summary>
        public string Name { get; set; } = "Unknown";

        /// <summary>设备类型：键盘/鼠标/手柄/触摸板/触摸屏。</summary>
        public enum InputDeviceType
        {
            Unknown,
            Keyboard,
            Mouse,
            Gamepad,
            Touchpad,
            Touchscreen,
            Joystick,
            Tablet
        }

        /// <summary>设备类型。</summary>
        public InputDeviceType Type { get; set; } = InputDeviceType.Unknown;

        /// <summary>供应商 ID。</summary>
        public string VendorId { get; set; } = "";

        /// <summary>产品 ID。</summary>
        public string ProductId { get; set; } = "";

        /// <summary>总线类型。</summary>
        public string BusType { get; set; } = "";

        /// <summary>是否为已连接的设备。</summary>
        public bool IsConnected { get; set; }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 6: LinuxAdapter — 平台适配器实现
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Linux 平台适配器。封装 X11/Wayland 窗口管理、
    /// evdev 输入设备热插拔、文件系统权限处理和 Vulkan 实例配置。
    ///
    /// 典型集成方式:
    ///   引擎 Linux 端通过 SDL2 作为窗口抽象层，此处提供底层 Native API 支持。
    ///   当引擎需要直接与 X11/Wayland 交互时（如嵌入到 GNOME/KDE Shell），
    ///   或需要访问特定 Linux 系统功能时使用此适配器。
    /// </summary>
    public sealed class LinuxAdapter : IDisposable
    {
        // ────────────── 私有状态 ──────────────
        private IntPtr _x11Display = IntPtr.Zero;
        private IntPtr _waylandDisplay = IntPtr.Zero;
        private LinuxWindowSystemType _windowSystemType = LinuxWindowSystemType.Unknown;
        private bool _initialized;
        private bool _disposed;

        // ────────────── 属性 ──────────────
        /// <summary>当前窗口系统类型。</summary>
        public LinuxWindowSystemType WindowSystemType => _windowSystemType;

        /// <summary>X11 Display 句柄。</summary>
        public IntPtr X11Display => _x11Display;

        /// <summary>Wayland Display 句柄。</summary>
        public IntPtr WaylandDisplay => _waylandDisplay;

        /// <summary>发行版信息。</summary>
        public LinuxDistributionInfo Distribution { get; private set; } = new();

        /// <summary>GPU 信息。</summary>
        public LinuxGPUInfo GPUInfo { get; private set; } = new();

        /// <summary>已检测到的输入设备列表。</summary>
        public List<EvdevDeviceInfo> InputDevices { get; } = new();

        /// <summary>是否已初始化。</summary>
        public bool IsInitialized => _initialized;

        // ────────────── 事件 ──────────────
        /// <summary>输入设备热插拔事件。</summary>
        public event EventHandler<EvdevDeviceInfo>? OnInputDeviceAdded;
        public event EventHandler<EvdevDeviceInfo>? OnInputDeviceRemoved;

        // ────────────── 初始化 ──────────────
        /// <summary>
        /// 初始化 Linux 适配器。
        /// 检测窗口系统、发行版、GPU 和输入设备。
        /// </summary>
        public void Initialize()
        {
            if (_initialized) return;

            // 1. 检测发行版
            Distribution = LinuxDistributionInfo.Parse();

            // 2. 检测窗口系统
            DetectWindowSystem();

            // 3. 检测 GPU
            GPUInfo = LinuxGPUInfo.Detect();

            // 4. 扫描输入设备
            ScanInputDevices();

            _initialized = true;
        }

        private void DetectWindowSystem()
        {
            // 优先检测 Wayland（通过环境变量 WAYLAND_DISPLAY）
            string? waylandDisplay = Environment.GetEnvironmentVariable("WAYLAND_DISPLAY");
            string? xdgSession = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE");

            if (!string.IsNullOrEmpty(waylandDisplay) || xdgSession == "wayland")
            {
                // 尝试连接 Wayland
                try
                {
                    _waylandDisplay = LinuxNative.wl_display_connect(null);
                    if (_waylandDisplay != IntPtr.Zero)
                    {
                        _windowSystemType = LinuxWindowSystemType.Wayland;
                        return;
                    }
                }
                catch { /* Wayland 连接失败 */ }
            }

            // 回退到 X11
            try
            {
                _x11Display = LinuxNative.XOpenDisplay(null);
                if (_x11Display != IntPtr.Zero)
                {
                    // 检查是否运行在 XWayland 上
                    string? xdgST = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE");
                    _windowSystemType = xdgST == "wayland"
                        ? LinuxWindowSystemType.XWayland
                        : LinuxWindowSystemType.X11;
                    return;
                }
            }
            catch { /* X11 连接失败 */ }

            _windowSystemType = LinuxWindowSystemType.Unknown;
        }

        private void ScanInputDevices()
        {
            InputDevices.Clear();

            string inputDir = "/dev/input";
            if (!Directory.Exists(inputDir)) return;

            // 扫描 event 设备
            foreach (var file in Directory.GetFiles(inputDir, "event*"))
            {
                try
                {
                    string deviceName = Path.GetFileName(file);

                    // 从 /sys/class/input/eventX/device/name 读取设备名
                    string sysPath = $"/sys/class/input/{deviceName}/device/name";
                    string name = "Unknown";
                    if (File.Exists(sysPath))
                        name = File.ReadAllText(sysPath).Trim();

                    var devInfo = new EvdevDeviceInfo
                    {
                        DevicePath = file,
                        Name = name,
                        IsConnected = true
                    };

                    // 简单的类型推断
                    string nameLower = name.ToLowerInvariant();
                    if (nameLower.Contains("keyboard") || nameLower.Contains("kbd"))
                        devInfo.Type = EvdevDeviceInfo.InputDeviceType.Keyboard;
                    else if (nameLower.Contains("mouse"))
                        devInfo.Type = EvdevDeviceInfo.InputDeviceType.Mouse;
                    else if (nameLower.Contains("gamepad") || nameLower.Contains("controller") || nameLower.Contains("xbox") || nameLower.Contains("playstation"))
                        devInfo.Type = EvdevDeviceInfo.InputDeviceType.Gamepad;
                    else if (nameLower.Contains("touchpad"))
                        devInfo.Type = EvdevDeviceInfo.InputDeviceType.Touchpad;
                    else if (nameLower.Contains("touchscreen") || nameLower.Contains("touch"))
                        devInfo.Type = EvdevDeviceInfo.InputDeviceType.Touchscreen;
                    else if (nameLower.Contains("joystick"))
                        devInfo.Type = EvdevDeviceInfo.InputDeviceType.Joystick;
                    else if (nameLower.Contains("wacom") || nameLower.Contains("tablet"))
                        devInfo.Type = EvdevDeviceInfo.InputDeviceType.Tablet;

                    // 尝试读取厂商和产品 ID
                    string idPath = $"/sys/class/input/{deviceName}/device/id/";
                    if (Directory.Exists(idPath))
                    {
                        string vendorFile = Path.Combine(idPath, "vendor");
                        string productFile = Path.Combine(idPath, "product");
                        string busFile = Path.Combine(idPath, "bus");
                        if (File.Exists(vendorFile))
                            devInfo.VendorId = File.ReadAllText(vendorFile).Trim();
                        if (File.Exists(productFile))
                            devInfo.ProductId = File.ReadAllText(productFile).Trim();
                        if (File.Exists(busFile))
                            devInfo.BusType = File.ReadAllText(busFile).Trim();
                    }

                    InputDevices.Add(devInfo);
                }
                catch { /* 设备访问失败 */ }
            }
        }

        // ────────────── 输入设备热插拔 ──────────────
        /// <summary>
        /// 监控 /dev/input 设备变化（热插拔）。
        /// 实际实现需要 inotify 或 udev 监听。
        /// </summary>
        public void StartHotplugMonitoring()
        {
            // 概念实现:
            // 1. 创建 inotify watch on /dev/input/
            // 2. 监听 IN_CREATE / IN_DELETE 事件
            // 3. 检测到新设备 → 扫描 → 触发 OnInputDeviceAdded
            // 4. 检测到移除 → 触发 OnInputDeviceRemoved
            //
            // 替代方案: 使用 libudev (libudev.so.1) 的 udev_monitor
        }

        /// <summary>停止热插拔监控。</summary>
        public void StopHotplugMonitoring()
        {
            // 清理 inotify watch / udev monitor
        }

        /// <summary>手动刷新输入设备列表。</summary>
        public void RefreshInputDevices()
        {
            var oldDevices = new HashSet<string>(InputDevices.ConvertAll(d => d.DevicePath));
            ScanInputDevices();

            foreach (var dev in InputDevices)
            {
                if (!oldDevices.Contains(dev.DevicePath))
                    OnInputDeviceAdded?.Invoke(this, dev);
            }

            var newDevices = new HashSet<string>(InputDevices.ConvertAll(d => d.DevicePath));
            foreach (var oldPath in oldDevices)
            {
                if (!newDevices.Contains(oldPath))
                {
                    var removed = new EvdevDeviceInfo { DevicePath = oldPath, IsConnected = false };
                    OnInputDeviceRemoved?.Invoke(this, removed);
                }
            }
        }

        // ────────────── 文件系统权限 ──────────────
        /// <summary>
        /// 检查文件/目录的访问权限。
        /// </summary>
        /// <param name="path">文件路径。</param>
        /// <param name="read">是否需要读权限。</param>
        /// <param name="write">是否需要写权限。</param>
        /// <param name="execute">是否需要执行权限。</param>
        /// <returns>是否满足所有请求的权限。</returns>
        public static bool CheckPermissions(string path, bool read = true, bool write = false, bool execute = false)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path) && !Directory.Exists(path))
                return false;

            int mode = 0;
            if (read) mode |= LinuxNative.R_OK;
            if (write) mode |= LinuxNative.W_OK;
            if (execute) mode |= LinuxNative.X_OK;

            return LinuxNative.access(path, mode) == 0;
        }

        /// <summary>
        /// 设置文件权限（Linux umask）。
        /// </summary>
        /// <param name="path">文件路径。</param>
        /// <param name="ownerRead">所有者可读。</param>
        /// <param name="ownerWrite">所有者可写。</param>
        /// <param name="ownerExecute">所有者可执行。</param>
        /// <param name="groupRead">组可读。</param>
        /// <param name="othersRead">其他可读。</param>
        public static bool SetPermissions(string path,
            bool ownerRead = true, bool ownerWrite = true, bool ownerExecute = false,
            bool groupRead = false, bool othersRead = false)
        {
            if (string.IsNullOrEmpty(path)) return false;

            int mode = 0;
            if (ownerRead) mode |= LinuxNative.S_IRUSR;
            if (ownerWrite) mode |= LinuxNative.S_IWUSR;
            if (ownerExecute) mode |= LinuxNative.S_IXUSR;
            if (groupRead) mode |= LinuxNative.S_IRGRP;
            if (othersRead) mode |= LinuxNative.S_IROTH;

            return LinuxNative.chmod(path, mode) == 0;
        }

        // ────────────── X11 操作 ──────────────
        /// <summary>获取 X11 默认屏幕分辨率。</summary>
        public (int Width, int Height) GetX11ScreenResolution()
        {
            if (_x11Display == IntPtr.Zero) return (1920, 1080);
            try
            {
                int screen = 0;
                int w = LinuxNative.XDisplayWidth(_x11Display, screen);
                int h = LinuxNative.XDisplayHeight(_x11Display, screen);
                return (w, h);
            }
            catch { return (1920, 1080); }
        }

        /// <summary>X11 消息泵。</summary>
        /// <returns>是否有待处理事件。</returns>
        public bool PumpX11Events()
        {
            if (_x11Display == IntPtr.Zero) return false;
            try
            {
                return LinuxNative.XPending(_x11Display) != 0;
            }
            catch { return false; }
        }

        // ────────────── Wayland 操作 ──────────────
        /// <summary>Wayland 消息泵（dispatch pending events）。</summary>
        public int PumpWaylandEvents()
        {
            if (_waylandDisplay == IntPtr.Zero) return 0;
            try
            {
                return LinuxNative.wl_display_dispatch_pending(_waylandDisplay);
            }
            catch { return 0; }
        }

        // ────────────── Vulkan 实例配置 ──────────────
        /// <summary>
        /// 获取 Linux 特定的 Vulkan 实例创建配置。
        /// 包括推荐启用的 Layer 和 Extension。
        /// </summary>
        public LinuxVulkanConfig GetVulkanConfig()
        {
            var config = new LinuxVulkanConfig();

            // 调试层（仅 Debug 模式）
            bool isDebug = false;
#if DEBUG
            isDebug = true;
#endif
            if (isDebug)
            {
                config.RequiredLayers.Add("VK_LAYER_KHRONOS_validation");
                config.OptionalLayers.Add("VK_LAYER_KHRONOS_synchronization");
                config.OptionalLayers.Add("VK_LAYER_KHRONOS_shader_object");
            }

            // Linux 特定扩展
            config.RequiredExtensions.Add("VK_KHR_surface");
            config.RequiredExtensions.Add("VK_KHR_get_surface_capabilities_2");

            if (_windowSystemType is LinuxWindowSystemType.X11 or LinuxWindowSystemType.XWayland)
                config.RequiredExtensions.Add("VK_KHR_xlib_surface");
            else if (_windowSystemType == LinuxWindowSystemType.Wayland)
                config.RequiredExtensions.Add("VK_KHR_wayland_surface");

            // 可选扩展
            config.OptionalExtensions.Add("VK_EXT_debug_utils");
            config.OptionalExtensions.Add("VK_KHR_swapchain");
            config.OptionalExtensions.Add("VK_EXT_memory_budget");
            config.OptionalExtensions.Add("VK_KHR_maintenance4");
            config.OptionalExtensions.Add("VK_EXT_shader_subgroup_vote");

            // NVIDIA 特定优化
            if (GPUInfo.IsNvidia)
            {
                config.OptionalExtensions.Add("VK_NV_device_diagnostic_checkpoints");
                config.OptionalExtensions.Add("VK_NV_fragment_shading_rate_enums");
            }

            // AMD 特定优化
            if (GPUInfo.IsAMDGPU)
            {
                config.OptionalExtensions.Add("VK_AMD_shader_trinary_minmax");
            }

            return config;
        }

        // ────────────── IDisposable ──────────────
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            StopHotplugMonitoring();

            if (_x11Display != IntPtr.Zero)
            {
                try { LinuxNative.XCloseDisplay(_x11Display); }
                catch { /* 忽略 */ }
                _x11Display = IntPtr.Zero;
            }

            if (_waylandDisplay != IntPtr.Zero)
            {
                try { LinuxNative.wl_display_disconnect(_waylandDisplay); }
                catch { /* 忽略 */ }
                _waylandDisplay = IntPtr.Zero;
            }

            _initialized = false;
        }
    }

    /// <summary>
    /// Linux Vulkan 实例配置。
    /// </summary>
    public sealed class LinuxVulkanConfig
    {
        /// <summary>必需的 Layer 列表。</summary>
        public List<string> RequiredLayers { get; } = new();

        /// <summary>可选的 Layer 列表（运行时检测是否可用）。</summary>
        public List<string> OptionalLayers { get; } = new();

        /// <summary>必需的扩展列表。</summary>
        public List<string> RequiredExtensions { get; } = new();

        /// <summary>可选的扩展列表。</summary>
        public List<string> OptionalExtensions { get; } = new();
    }
}
