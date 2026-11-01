using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace YamaEngine.Build
{
    // ════════════════════════════════════════════════════════════════════════
    // yama引擎平台抽象层 (PlatformAbstractionLayer)
    //
    // 提供运行时平台检测、平台特性查询、路径转换、环境变量访问、
    // 窗口管理接口等，作为运行时平台差异的统一抽象。
    // 替代编译期 #if 条件编译，实现运行时平台分支。
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 平台类型枚举（运行时检测）。
    /// </summary>
    public enum RuntimePlatform
    {
        Windows,
        Linux,
        MacOS,
        Android,
        iOS,
        HTML5,
        WebGL,
        Console,
        Unknown
    }

    /// <summary>
    /// 系统架构枚举。
    /// </summary>
    public enum SystemArchitecture
    {
        x86,
        x64,
        ARM64,
        ARM32,
        Universal,
        Unknown
    }

    /// <summary>
    /// 平台信息。描述当前运行环境的硬件与操作系统信息。
    /// </summary>
    public sealed class PlatformInfo
    {
        /// <summary>运行时平台。</summary>
        public RuntimePlatform Platform { get; set; } = RuntimePlatform.Unknown;

        /// <summary>系统架构。</summary>
        public SystemArchitecture Architecture { get; set; }

        /// <summary>操作系统名称。</summary>
        public string OSName { get; set; } = string.Empty;

        /// <summary>操作系统版本。</summary>
        public string OSVersion { get; set; } = string.Empty;

        /// <summary>总物理内存 (字节)。</summary>
        public long TotalPhysicalMemoryBytes { get; set; }

        /// <summary>可用物理内存 (字节)。</summary>
        public long AvailableMemoryBytes { get; set; }

        /// <summary>逻辑 CPU 核心数。</summary>
        public int ProcessorCount { get; set; }

        /// <summary>CPU 名称。</summary>
        public string CpuName { get; set; } = string.Empty;

        /// <summary>GPU 信息列表。</summary>
        public List<GpuInfo> Gpus { get; set; } = new();

        /// <summary>是否为 64 位进程。</summary>
        public bool Is64BitProcess { get; set; }

        /// <summary>是否为 64 位操作系统。</summary>
        public bool Is64BitOS { get; set; }

        /// <summary>进程当前工作目录。</summary>
        public string CurrentDirectory { get; set; } = string.Empty;

        /// <summary>用户主目录。</summary>
        public string UserHomeDirectory { get; set; } = string.Empty;
    }

    /// <summary>
    /// GPU 信息。
    /// </summary>
    public sealed class GpuInfo
    {
        /// <summary>GPU 名称。</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>GPU 驱动版本。</summary>
        public string DriverVersion { get; set; } = string.Empty;

        /// <summary>显存大小 (字节)。</summary>
        public long DedicatedMemoryBytes { get; set; }

        /// <summary>供应商 (NVIDIA/AMD/Intel/Apple/Qualcomm)。</summary>
        public string Vendor { get; set; } = string.Empty;

        /// <summary>GPU ID / 设备 ID。</summary>
        public string DeviceId { get; set; } = string.Empty;
    }

    /// <summary>
    /// 平台特性能力。描述当前平台支持的图形/计算特性。
    /// </summary>
    public sealed class PlatformCapabilities
    {
        /// <summary>是否支持 Compute Shader。</summary>
        public bool SupportsComputeShader { get; set; }

        /// <summary>是否支持 Mesh Shader。</summary>
        public bool SupportsMeshShader { get; set; }

        /// <summary>是否支持 Ray Tracing (硬件光线追踪)。</summary>
        public bool SupportsRayTracing { get; set; }

        /// <summary>是否支持 Variable Rate Shading (VRS)。</summary>
        public bool SupportsVariableRateShading { get; set; }

        /// <summary>是否支持 Sampler Feedback。</summary>
        public bool SupportsSamplerFeedback { get; set; }

        /// <summary>是否支持 VR。</summary>
        public bool SupportsVR { get; set; }

        /// <summary>是否支持立体渲染 (3D)。</summary>
        public bool SupportsStereoRendering { get; set; }

        /// <summary>是否支持 Tessellation Shader。</summary>
        public bool SupportsTessellation { get; set; }

        /// <summary>是否支持 Geometry Shader。</summary>
        public bool SupportsGeometryShader { get; set; }

        /// <summary>最大纹理尺寸。</summary>
        public int MaxTextureSize { get; set; } = 16384;

        /// <summary>最大各向异性过滤等级。</summary>
        public int MaxAnisotropy { get; set; } = 16;

        /// <summary>最大 3D 纹理尺寸。</summary>
        public int MaxTexture3DSize { get; set; } = 2048;

        /// <summary>最大纹理数组层数。</summary>
        public int MaxTextureArrayLayers { get; set; } = 256;

        /// <summary>最大 Compute Shader 线程组 (X, Y, Z)。</summary>
        public (int X, int Y, int Z) MaxComputeThreadGroupSize { get; set; } = (1024, 1024, 64);

        /// <summary>最大 Render Target 数。</summary>
        public int MaxRenderTargets { get; set; } = 8;

        /// <summary>显存预算 (字节)。</summary>
        public long DedicatedMemoryBudgetBytes { get; set; }

        /// <summary>是否为移动平台（低功耗设备）。</summary>
        public bool IsMobilePlatform { get; set; }

        /// <summary>支持的最大 Vulkan 版本 (主版本.次版本)。</summary>
        public string MaxVulkanVersion { get; set; } = "0.0";

        /// <summary>支持的最大 Metal 版本。</summary>
        public string MaxMetalVersion { get; set; } = "0.0";

        /// <summary>支持的最高 Direct3D 版本。</summary>
        public string MaxDirect3DVersion { get; set; } = "0.0";
    }

    /// <summary>
    /// 显示器信息。
    /// </summary>
    public sealed class DisplayInfo
    {
        /// <summary>显示器索引。</summary>
        public int Index { get; set; }

        /// <summary>显示器名称。</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>分辨率宽度 (像素)。</summary>
        public int Width { get; set; }

        /// <summary>分辨率高度 (像素)。</summary>
        public int Height { get; set; }

        /// <summary>刷新率 (Hz)。</summary>
        public int RefreshRate { get; set; }

        /// <summary>色深 (位/像素)。</summary>
        public int BitDepth { get; set; }

        /// <summary>DPI 缩放。</summary>
        public float DpiScale { get; set; } = 1.0f;

        /// <summary>是否为主显示器。</summary>
        public bool IsPrimary { get; set; }
    }

    /// <summary>
    /// 窗口模式。
    /// </summary>
    public enum WindowMode
    {
        /// <summary>窗口化。</summary>
        Windowed,
        /// <summary>无边框窗口。</summary>
        Borderless,
        /// <summary>独占全屏。</summary>
        Fullscreen,
        /// <summary>窗口全屏 (桌面分辨率全屏窗口)。</summary>
        WindowedFullscreen
    }

    /// <summary>
    /// 窗口描述。
    /// </summary>
    public sealed class WindowDescriptor
    {
        /// <summary>窗口标题。</summary>
        public string Title { get; set; } = "Yama Engine";

        /// <summary>窗口宽度。</summary>
        public int Width { get; set; } = 1280;

        /// <summary>窗口高度。</summary>
        public int Height { get; set; } = 720;

        /// <summary>窗口 X 位置 (-1 = 居中)。</summary>
        public int X { get; set; } = -1;

        /// <summary>窗口 Y 位置 (-1 = 居中)。</summary>
        public int Y { get; set; } = -1;

        /// <summary>窗口模式。</summary>
        public WindowMode Mode { get; set; } = WindowMode.Windowed;

        /// <summary>是否可调整大小。</summary>
        public bool Resizable { get; set; } = true;

        /// <summary>是否显示标题栏。</summary>
        public bool ShowTitleBar { get; set; } = true;

        /// <summary>是否支持多显示器全屏。</summary>
        public bool AllowMultiMonitorFullscreen { get; set; } = false;

        /// <summary>首选显示器索引。</summary>
        public int PreferredMonitor { get; set; } = 0;

        /// <summary>最低分辨率宽度。</summary>
        public int MinWidth { get; set; } = 320;

        /// <summary>最低分辨率高度。</summary>
        public int MinHeight { get; set; } = 240;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 平台抽象层核心
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// yama引擎平台抽象层。提供运行时平台检测、路径转换、环境变量、
    /// 窗口管理与特性查询的统一接口。
    /// </summary>
    public sealed class Platform
    {
        private static Platform? _instance;
        private readonly PlatformInfo _info;
        private readonly PlatformCapabilities _capabilities;
        private readonly List<DisplayInfo> _displays = new();

        /// <summary>平台信息。</summary>
        public PlatformInfo Info => _info;

        /// <summary>平台特性。</summary>
        public PlatformCapabilities Capabilities => _capabilities;

        /// <summary>显示器列表。</summary>
        public List<DisplayInfo> Displays => _displays;

        /// <summary>当前平台枚举值。</summary>
        public RuntimePlatform CurrentPlatform => _info.Platform;

        /// <summary>当前平台名称。</summary>
        public string PlatformName => _info.Platform.ToString();

        /// <summary>是否为 Windows。</summary>
        public bool IsWindows => _info.Platform == RuntimePlatform.Windows;

        /// <summary>是否为 Linux。</summary>
        public bool IsLinux => _info.Platform == RuntimePlatform.Linux;

        /// <summary>是否为 macOS。</summary>
        public bool IsMacOS => _info.Platform == RuntimePlatform.MacOS;

        /// <summary>是否为移动平台。</summary>
        public bool IsMobile => _info.Platform is RuntimePlatform.Android or RuntimePlatform.iOS;

        /// <summary>是否为桌面平台。</summary>
        public bool IsDesktop => _info.Platform is RuntimePlatform.Windows or RuntimePlatform.Linux or RuntimePlatform.MacOS;

        /// <summary>是否为 Web 平台。</summary>
        public bool IsWeb => _info.Platform is RuntimePlatform.HTML5 or RuntimePlatform.WebGL;

        /// <summary>单例访问。</summary>
        public static Platform Current => _instance ??= new Platform();

        private Platform()
        {
            _info = DetectPlatformInfo();
            _capabilities = DetectCapabilities(_info);
            _displays = DetectDisplays();
        }

        /// <summary>重新检测平台信息（例如显示器变化后）。</summary>
        public void Refresh()
        {
            var newDisplays = DetectDisplays();
            _displays.Clear();
            _displays.AddRange(newDisplays);
        }

        // ── 平台检测 ──

        private static PlatformInfo DetectPlatformInfo()
        {
            var info = new PlatformInfo
            {
                Architecture = RuntimeInformation.OSArchitecture switch
                {
                    Architecture.X64 => SystemArchitecture.x64,
                    Architecture.Arm64 => SystemArchitecture.ARM64,
                    Architecture.X86 => SystemArchitecture.x86,
                    Architecture.Arm => SystemArchitecture.ARM32,
                    _ => SystemArchitecture.Unknown
                },
                Is64BitProcess = Environment.Is64BitProcess,
                Is64BitOS = Environment.Is64BitOperatingSystem,
                ProcessorCount = Environment.ProcessorCount,
                CurrentDirectory = Environment.CurrentDirectory,
                OSName = RuntimeInformation.OSDescription,
                OSVersion = Environment.OSVersion.ToString(),
                Platform = DetectCurrentPlatform()
            };

            // 用户主目录
            info.UserHomeDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            // 总物理内存
            info.TotalPhysicalMemoryBytes = GetTotalPhysicalMemory();

            return info;
        }

        private static RuntimePlatform DetectCurrentPlatform()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return RuntimePlatform.Windows;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return RuntimePlatform.Linux;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return RuntimePlatform.MacOS;
            return RuntimePlatform.Unknown;
        }

        private static long GetTotalPhysicalMemory()
        {
            // 跨平台内存检测
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    // Windows: MEMORYSTATUSEX
                    return GetWindowsTotalMemory();
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    // Linux: 读取 /proc/meminfo
                    var memInfo = File.ReadAllText("/proc/meminfo");
                    var line = memInfo.Split('\n')
                        .FirstOrDefault(l => l.StartsWith("MemTotal:"));
                    if (line != null)
                    {
                        var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 2 && long.TryParse(parts[1], out var kb))
                            return kb * 1024;
                    }
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    // macOS: sysctl hw.memsize
                    // 简化返回默认值
                }
            }
            catch { }
            return 1024L * 1024 * 1024; // 默认 1GB
        }

        private static long GetWindowsTotalMemory()
        {
            try
            {
                // 使用 GC 内存信息作为近似
                return GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            }
            catch { return 1024L * 1024 * 1024; }
        }

        private static PlatformCapabilities DetectCapabilities(PlatformInfo info)
        {
            var caps = new PlatformCapabilities();

            switch (info.Platform)
            {
                case RuntimePlatform.Windows:
                    caps.SupportsComputeShader = true;
                    caps.SupportsMeshShader = true;
                    caps.SupportsRayTracing = true;
                    caps.SupportsVariableRateShading = true;
                    caps.SupportsSamplerFeedback = true;
                    caps.SupportsVR = true;
                    caps.SupportsStereoRendering = true;
                    caps.SupportsTessellation = true;
                    caps.SupportsGeometryShader = true;
                    caps.MaxDirect3DVersion = "12.1";
                    caps.MaxVulkanVersion = "1.3";
                    caps.IsMobilePlatform = false;
                    break;

                case RuntimePlatform.Linux:
                    caps.SupportsComputeShader = true;
                    caps.SupportsMeshShader = true;
                    caps.SupportsRayTracing = true;
                    caps.SupportsVR = true;
                    caps.SupportsStereoRendering = true;
                    caps.SupportsTessellation = true;
                    caps.SupportsGeometryShader = true;
                    caps.MaxVulkanVersion = "1.3";
                    caps.IsMobilePlatform = false;
                    break;

                case RuntimePlatform.MacOS:
                    caps.SupportsComputeShader = true;
                    caps.SupportsRayTracing = true;
                    caps.SupportsStereoRendering = true;
                    caps.SupportsTessellation = true;
                    caps.MaxMetalVersion = "3.1";
                    caps.IsMobilePlatform = false;
                    break;

                case RuntimePlatform.Android:
                    caps.SupportsComputeShader = true;
                    caps.SupportsTessellation = true;
                    caps.IsMobilePlatform = true;
                    caps.MaxTextureSize = 4096;
                    caps.MaxVulkanVersion = "1.1";
                    break;

                case RuntimePlatform.iOS:
                    caps.SupportsComputeShader = true;
                    caps.IsMobilePlatform = true;
                    caps.MaxTextureSize = 4096;
                    caps.MaxMetalVersion = "3.0";
                    break;

                case RuntimePlatform.HTML5:
                case RuntimePlatform.WebGL:
                    caps.SupportsComputeShader = false; // WebGPU 可选支持
                    caps.MaxTextureSize = 4096;
                    caps.IsMobilePlatform = true;
                    break;

                default:
                    caps.SupportsComputeShader = true;
                    break;
            }

            return caps;
        }

        private static List<DisplayInfo> DetectDisplays()
        {
            // 简化：返回一个默认主显示器
            return new List<DisplayInfo>
            {
                new DisplayInfo
                {
                    Index = 0,
                    Name = "Primary Display",
                    Width = 1920,
                    Height = 1080,
                    RefreshRate = 60,
                    BitDepth = 32,
                    DpiScale = 1.0f,
                    IsPrimary = true
                }
            };
        }

        // ── 路径转换 ──

        /// <summary>
        /// 将路径转换为当前平台的标准格式。
        /// </summary>
        public string ConvertPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;

            switch (_info.Platform)
            {
                case RuntimePlatform.Windows:
                    return path.Replace('/', '\\');

                case RuntimePlatform.Linux:
                case RuntimePlatform.MacOS:
                case RuntimePlatform.Android:
                case RuntimePlatform.iOS:
                    return path.Replace('\\', '/');

                case RuntimePlatform.HTML5:
                case RuntimePlatform.WebGL:
                    return path.Replace('\\', '/');

                default:
                    return path;
            }
        }

        /// <summary>
        /// 规范化路径（统一分隔符、去除冗余分隔符、解析 . 和 ..）。
        /// </summary>
        public string NormalizePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            var converted = ConvertPath(path);
            var separator = IsWindows ? '\\' : '/';
            return Path.GetFullPath(converted).TrimEnd(separator);
        }

        /// <summary>
        /// 检查路径是否区分大小写。
        /// </summary>
        public bool IsCaseSensitive => !IsWindows;

        /// <summary>
        /// 平台不敏感的路径比较。
        /// </summary>
        public bool PathEquals(string pathA, string pathB)
        {
            if (IsCaseSensitive)
                return NormalizePath(pathA) == NormalizePath(pathB);
            return string.Equals(NormalizePath(pathA), NormalizePath(pathB), StringComparison.OrdinalIgnoreCase);
        }

        // ── 环境变量 ──

        /// <summary>获取环境变量。</summary>
        public string? GetEnvironmentVariable(string name)
        {
            return Environment.GetEnvironmentVariable(name);
        }

        /// <summary>设置环境变量。</summary>
        public void SetEnvironmentVariable(string name, string? value)
        {
            Environment.SetEnvironmentVariable(name, value);
        }

        /// <summary>获取所有引擎相关的环境变量。</summary>
        public Dictionary<string, string> GetEngineEnvironmentVariables()
        {
            var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            var enginePrefixes = new[] { "SHANYU_", "SHANYUENGINE_" };
            foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
            {
                if (entry.Key is string key && entry.Value is string value)
                {
                    if (enginePrefixes.Any(p => key.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                    {
                        vars[key] = value;
                    }
                }
            }

            return vars;
        }

        // ── 平台特定 API（窗口管理占位） ──

        /// <summary>获取推荐窗口描述（基于当前显示器）。</summary>
        public WindowDescriptor GetRecommendedWindowDescriptor()
        {
            var primary = _displays.FirstOrDefault(d => d.IsPrimary) ?? _displays.FirstOrDefault();
            if (primary != null)
            {
                return new WindowDescriptor
                {
                    Width = (int)(primary.Width * 0.8f),
                    Height = (int)(primary.Height * 0.8f),
                    Mode = WindowMode.Windowed,
                    PreferredMonitor = primary.Index
                };
            }

            return new WindowDescriptor();
        }

        /// <summary>获取主显示器信息。</summary>
        public DisplayInfo? GetPrimaryDisplay()
        {
            return _displays.FirstOrDefault(d => d.IsPrimary) ?? _displays.FirstOrDefault();
        }

        /// <summary>获取所有可用分辨率。</summary>
        public List<(int Width, int Height, int RefreshRate)> GetAvailableResolutions()
        {
            // 简化：返回常见分辨率
            return new List<(int, int, int)>
            {
                (3840, 2160, 60), (2560, 1440, 60), (1920, 1080, 60),
                (1680, 1050, 60), (1440, 900, 60), (1366, 768, 60),
                (1280, 720, 60), (1024, 768, 60), (800, 600, 60)
            };
        }

        /// <summary>检查运行时条件，替代编译期 #if。</summary>
        public bool HasFeature(string feature)
        {
            return feature switch
            {
                "ComputeShader" => _capabilities.SupportsComputeShader,
                "MeshShader" => _capabilities.SupportsMeshShader,
                "RayTracing" => _capabilities.SupportsRayTracing,
                "VR" => _capabilities.SupportsVR,
                "Tessellation" => _capabilities.SupportsTessellation,
                "GeometryShader" => _capabilities.SupportsGeometryShader,
                "Stereo" => _capabilities.SupportsStereoRendering,
                "VRS" => _capabilities.SupportsVariableRateShading,
                _ => false
            };
        }





        // ── 兼容成员自动补齐（占位/默认） ──
        public dynamic Arm64;
        public dynamic X64;
    }
}
