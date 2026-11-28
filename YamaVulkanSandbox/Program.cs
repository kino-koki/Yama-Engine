// =============================================================================
// YamaVulkanSandbox - 入口点
// 创建窗口，初始化 Vulkan，在真实 GPU 上渲染彩色三角形。
// =============================================================================

using Silk.NET.Core;
using Silk.NET.Maths;
using Silk.NET.Windowing;

namespace YamaVulkanSandbox;

internal static class Program
{
    private static VulkanTriangleRenderer? _renderer;
    private static DeferredPBRSandbox? _deferred;
    private static IWindow? _window;
    private static int _frameCount;
    private static double _elapsed;
    private static double _fpsTimer;
    private static bool _useDeferred;

    private static void Main(string[] args)
    {
        _useDeferred = args.Contains("--deferred", StringComparer.OrdinalIgnoreCase);

        WindowOptions options = WindowOptions.Default;
        options.Size = new Vector2D<int>(1280, 720);
        options.Title = _useDeferred
            ? "Yama Deferred PBR Sandbox - CSM + ACES"
            : "Yama Vulkan Sandbox - 真实 GPU 渲染测试";
        options.API = new GraphicsAPI(
            ContextAPI.Vulkan,
            ContextProfile.Core,
            ContextFlags.Default,
            new APIVersion(1, 4));
        options.VSync = false;
        options.ShouldSwapAutomatically = false;

        _window = Window.Create(options);
        _window.Load += OnLoad;
        _window.Render += OnRender;
        _window.Closing += OnClose;

        Console.WriteLine("[YamaVulkanSandbox] 启动窗口...");
        if (args.Contains("--frames", StringComparer.OrdinalIgnoreCase))
        {
            int idx = Array.IndexOf(args, "--frames");
            if (idx + 1 < args.Length && int.TryParse(args[idx + 1], out int n))
                _frameLimit = n;
        }
        _window.Run();
    }

    private static int _frameLimit = -1;

    private static void OnLoad()
    {
        if (_useDeferred)
        {
            Console.WriteLine("[YamaVulkanSandbox] 窗口加载完成，初始化 Deferred PBR Sandbox...");
            _deferred = new DeferredPBRSandbox(_window!);
            _deferred.Initialize();
            Console.WriteLine("[YamaVulkanSandbox] === 延迟渲染 PBR + CSM + ACES 已启动 ===");
        }
        else
        {
            Console.WriteLine("[YamaVulkanSandbox] 窗口加载完成，初始化 Vulkan...");
            _renderer = new VulkanTriangleRenderer(_window!);
            _renderer.Initialize();
            Console.WriteLine("[YamaVulkanSandbox] === 真实 GPU 渲染测试已启动 ===");
            Console.WriteLine("[YamaVulkanSandbox] 应看到一个彩色三角形（红/绿/蓝顶点）");
        }
    }

    private static void OnRender(double dt)
    {
        if (_useDeferred) _deferred?.RenderFrame();
        else _renderer?.RenderFrame();

        _totalFrames++;
        if (_frameLimit > 0 && _totalFrames >= _frameLimit)
        {
            Console.WriteLine($"[YamaVulkanSandbox] 达到帧数上限 {_frameLimit}，自动关闭");
            _window!.Close();
        }

        _frameCount++;
        _elapsed += dt;
        _fpsTimer += dt;
        if (_fpsTimer >= 2.0)
        {
            double fps = _frameCount / _fpsTimer;
            Console.WriteLine($"[YamaVulkanSandbox] 帧 {_frameCount} | FPS {fps:F1} | 累计 {_elapsed:F1}s");
            _fpsTimer = 0;
            _frameCount = 0;
        }

        if (_frameLimit > 0)
        {
            _totalFrames++;
            if (_totalFrames >= _frameLimit)
            {
                Console.WriteLine($"[YamaVulkanSandbox] 达到帧数上限 {_frameLimit}，自动关闭");
                _window!.Close();
            }
        }
    }

    private static int _totalFrames;

    private static void OnClose()
    {
        Console.WriteLine("[YamaVulkanSandbox] 窗口关闭，清理 Vulkan 资源...");
        _renderer?.Dispose();
        _deferred?.Dispose();
    }
}
