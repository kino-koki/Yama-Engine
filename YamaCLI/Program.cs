using YamaEngine.Assets;
using YamaEngine.Core;
using YamaEngine.Projects;

namespace YamaCLI;

/// <summary>
/// Yama Engine 命令行接口 —— 无需 GUI，直接驱动引擎库真实子系统：
/// 项目创建/加载、资产导入/列举、无头引擎冒烟测试。
/// </summary>
internal static class Program
{
    private const string EngineVersion = "Yama Engine 1.0";
    private const string CliVersion = "1.0.0";

    public static int Main(string[] args)
    {
        // 控制台输出用 UTF-8，避免中文乱码（Windows 默认 OEM 代码页）
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { /* 忽略 */ }
        try { Console.InputEncoding = System.Text.Encoding.UTF8; } catch { /* 忽略 */ }

        try
        {
            return Dispatch(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[yama] 错误: {ex.Message}");
            return 1;
        }
    }

    private static int Dispatch(string[] args)
    {
        if (args.Length == 0)
        {
            PrintHelp();
            return 0;
        }

        string cmd = args[0].ToLowerInvariant();
        return cmd switch
        {
            "help" or "--help" or "-h" => Help(),
            "version" or "--version" or "-v" => Version(),
            "new" => NewProject(args),
            "info" => Info(args),
            "list" => List(args),
            "import" => Import(args),
            "smoke" => Smoke(args),
            _ => Unknown(cmd)
        };
    }

    // ── 命令实现 ────────────────────────────────────────────────────

    private static int Help()
    {
        PrintHelp();
        return 0;
    }

    private static int Version()
    {
        Console.WriteLine($"{EngineVersion} (CLI v{CliVersion})");
        return 0;
    }

    private static int NewProject(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("用法: yama new <项目路径> <项目名> [模板路径]");
            return 1;
        }

        string path = Path.GetFullPath(args[1]);
        string name = args[2];
        string template = args.Length >= 4 ? args[3] : FindDefaultTemplate();

        if (!Directory.Exists(template))
        {
            Console.Error.WriteLine($"模板不存在: {template}");
            Console.Error.WriteLine("可用模板: " + string.Join(", ", ListTemplates()));
            return 1;
        }

        var project = ProjectSystem.CreateProject(path, name, template);
        Console.WriteLine($"已创建项目 '{project.Name}' @ {project.ProjectPath}");
        Console.WriteLine($"  启动场景: {project.StartupScene}");
        Console.WriteLine($"  目录数  : {project.Folders.Count}");
        return 0;
    }

    private static int Info(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("用法: yama info <项目路径>");
            return 1;
        }

        var project = ProjectSystem.LoadProject(Path.GetFullPath(args[1]));
        Console.WriteLine($"项目    : {project.Name}");
        Console.WriteLine($"引擎    : {project.Engine}");
        Console.WriteLine($"格式版本: {project.FormatVersion}");
        Console.WriteLine($"路径    : {project.ProjectPath}");
        Console.WriteLine($"启动场景: {project.StartupScene}");
        Console.WriteLine("目录    : " + string.Join(", ", project.Folders));
        return 0;
    }

    private static int List(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("用法: yama list <项目路径>");
            return 1;
        }

        var project = ProjectSystem.LoadProject(Path.GetFullPath(args[1]));
        var db = new AssetDatabase(project.ProjectPath);
        Console.WriteLine($"项目 '{project.Name}' 资产清单:");
        Console.WriteLine($"  资产数: {db.AssetCount}");

        // 真实文件树（排除 Library/Builds 等生成目录）
        var excluded = new HashSet<string>(project.Folders
            .Where(f => f.Contains("Library") || f.Contains("Builds"))
            .Select(f => Path.GetFullPath(Path.Combine(project.ProjectPath, f))));

        var files = Directory.EnumerateFiles(project.ProjectPath, "*", SearchOption.AllDirectories)
            .Where(f => !excluded.Any(e => f.StartsWith(e, StringComparison.OrdinalIgnoreCase)));

        int count = 0;
        foreach (var file in files)
        {
            Console.WriteLine($"  {Path.GetRelativePath(project.ProjectPath, file)}");
            count++;
        }
        Console.WriteLine($"  文件总数: {count}");
        return 0;
    }

    private static int Import(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("用法: yama import <项目路径> <资产文件> [类型]");
            return 1;
        }

        var project = ProjectSystem.LoadProject(Path.GetFullPath(args[1]));
        string sourceFile = Path.GetFullPath(args[2]);
        if (!File.Exists(sourceFile))
        {
            Console.Error.WriteLine($"文件不存在: {sourceFile}");
            return 1;
        }

        var db = new AssetDatabase(project.ProjectPath);
        AssetType type = args.Length >= 4 ? ParseAssetType(args[3]) : AssetType.Unknown;
        var meta = db.ImportAsset(sourceFile, type);
        Console.WriteLine($"已导入: {meta.Name} [{meta.Type}]");
        Console.WriteLine($"  源文件 : {meta.SourceFile}");
        Console.WriteLine($"  缓存路径: {meta.GetCachePath()}");
        return 0;
    }

    private static int Smoke(string[] args)
    {
        int frames = 60;
        if (args.Length >= 2 && !int.TryParse(args[1], out frames))
        {
            Console.Error.WriteLine("帧数须为整数。用法: yama smoke [帧数]");
            return 1;
        }
        frames = Math.Clamp(frames, 1, 10000);

        Console.WriteLine($"无头引擎冒烟测试 — 引导 + {frames} 帧 Tick（无 GPU 依赖）...");

        try
        {
            var systems = new EngineSystems();
            systems.OnLogMessage += m => Console.WriteLine($"  [log] {m}");
            for (int i = 0; i < frames; i++)
                systems.Tick(0.016f);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"引擎无头引导/ Tick 异常: {ex.Message}");
            Console.Error.WriteLine(ex.StackTrace);
            return 1;
        }

        Console.WriteLine($"✔ 引擎无头引导成功，{frames} 帧已处理");
        return 0;
    }

    private static int Unknown(string cmd)
    {
        Console.Error.WriteLine($"未知命令: {cmd}");
        PrintHelp();
        return 1;
    }

    // ── 工具 ───────────────────────────────────────────────────────

    private static string FindDefaultTemplate()
    {
        string local = Path.Combine(Directory.GetCurrentDirectory(), "Templates", "Basic3D");
        return Directory.Exists(local) ? local : local;
    }

    private static IEnumerable<string> ListTemplates()
    {
        string dir = Path.Combine(Directory.GetCurrentDirectory(), "Templates");
        return Directory.Exists(dir)
            ? Directory.GetDirectories(dir).Select(d => Path.GetFileName(d)!)
            : Enumerable.Empty<string>();
    }

    private static AssetType ParseAssetType(string s) =>
        Enum.TryParse<AssetType>(s, true, out var t) ? t : AssetType.Unknown;

    private static void PrintHelp()
    {
        Console.WriteLine("Yama Engine 命令行 (yama CLI v" + CliVersion + ")");
        Console.WriteLine();
        Console.WriteLine("用法: yama <命令> [参数]");
        Console.WriteLine();
        Console.WriteLine("命令:");
        Console.WriteLine("  version                 显示引擎版本");
        Console.WriteLine("  new <路径> <名称> [模板]   创建项目（默认模板 Templates/Basic3D）");
        Console.WriteLine("  info <路径>              显示项目清单信息");
        Console.WriteLine("  list <路径>              列出项目资产与文件");
        Console.WriteLine("  import <路径> <文件> [类型] 导入资产（类型: mesh/material/texture/...）");
        Console.WriteLine("  smoke [帧数]             无头引擎冒烟测试（默认 60 帧）");
        Console.WriteLine("  help                    显示本帮助");
    }
}
