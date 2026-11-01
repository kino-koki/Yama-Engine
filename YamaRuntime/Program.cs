using YamaEngine;

namespace YamaRuntime;

/// <summary>
/// YamaRuntime 运行时入口点。
/// 创建 EngineApplication 并启动主循环。
/// 支持命令行参数: --width, --height, --null (强制无GPU模式), --title
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            var config = new EngineApplication.AppConfig
            {
                WindowTitle = "YamaEngine Runtime",
                WindowWidth = 1280,
                WindowHeight = 720,
                VSync = true,
                UseVulkan = true,
                EnableDebugLayer = true,
                EnableFixedTimeStep = true,
                FixedTimeStep = 1f / 60f,
                MaxFixedStepsPerFrame = 4
            };

            // 解析命令行参数
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i].ToLowerInvariant())
                {
                    case "--width" when i + 1 < args.Length && int.TryParse(args[++i], out int w):
                        config.WindowWidth = Math.Max(320, w);
                        break;
                    case "--height" when i + 1 < args.Length && int.TryParse(args[++i], out int h):
                        config.WindowHeight = Math.Max(240, h);
                        break;
                    case "--null":
                        config.UseVulkan = false;
                        break;
                    case "--title" when i + 1 < args.Length:
                        config.WindowTitle = args[++i];
                        break;
                    case "--novsync":
                        config.VSync = false;
                        break;
                    case "--debug":
                        config.EnableDebugLayer = true;
                        break;
                    case "--nofixedstep":
                        config.EnableFixedTimeStep = false;
                        break;
                }
            }

            using var app = new EngineApplication(config);
            return app.Run();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[YamaRuntime] 未捕获的异常: {ex.GetType().Name}");
            Console.Error.WriteLine($"  消息: {ex.Message}");
            Console.Error.WriteLine(ex.StackTrace);
            return 1;
        }
    }
}
