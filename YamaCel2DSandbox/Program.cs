// =============================================================================
//  Program.cs — YamaCel2DSandbox 入口
//
//  用法:
//    YamaCel2DSandbox --headless <out.png> [width] [height]
//      在真实 GPU 上离屏渲染一帧赛璐璐 2D 场景并保存 PNG（无需窗口）。
// =============================================================================

namespace YamaCel2DSandbox;

internal static class Program
{
    private static int Main(string[] args)
    {
        Console.WriteLine("[Cel2D] YamaCel2DSandbox —— 赛璐璐 2D 渲染验证");

        if (args.Length >= 2 && args[0] == "--headless")
        {
            int w = args.Length >= 3 ? int.Parse(args[2]) : 800;
            int h = args.Length >= 4 ? int.Parse(args[3]) : 600;
            return Cel2DSandboxApp.RunHeadless(args[1], w, h);
        }

        Console.WriteLine("用法:");
        Console.WriteLine("  YamaCel2DSandbox --headless <out.png> [width] [height]");
        return 1;
    }
}
