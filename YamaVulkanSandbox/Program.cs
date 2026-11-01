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
    private static IWindow? _window;
    private static int _frameCount;
    private static double _elapsed;
    private static double _fpsTimer;

    private static void Main()
    {
        WindowOptions options = WindowOptions.Default;
        options.Size = new Vector2D<int>(800, 600);
        options.Title = "Yama Vulkan Sandbox - 真实 GPU 渲染测试";
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
        _window.Run();
    }

    private static void OnLoad()
    {
        Console.WriteLine("[YamaVulkanSandbox] 窗口加载完成，初始化 Vulkan...");
        _renderer = new VulkanTriangleRenderer(_window!);
        _renderer.Initialize();
        Console.WriteLine("[YamaVulkanSandbox] === 真实 GPU 渲染测试已启动 ===");
        Console.WriteLine("[YamaVulkanSandbox] 应看到一个彩色三角形（红/绿/蓝顶点）");
    }

    private static void OnRender(double dt)
    {
        _renderer?.RenderFrame();

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
    }

    private static void OnClose()
    {
        Console.WriteLine("[YamaVulkanSandbox] 窗口关闭，清理 Vulkan 资源...");
        _renderer?.Dispose();
    }
}
