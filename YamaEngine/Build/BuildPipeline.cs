// ==================================================================================================
//  BuildPipeline.cs — yama引擎游戏打包构建管线
// ==================================================================================================
//  命名空间: YamaEngine.Build
//
//  功能:
//    1. 收集场景和资产
//    2. 生成游戏配置文件 (game.config.json)
//    3. 复制引擎运行时 DLL 和依赖
//    4. 复制脚本 DLL
//    5. 生成启动器入口程序
//    6. 可选：压缩资产包
//    7. 可选：生成 ZIP 安装包
//
//  输出结构:
//    BuildName/
//      ├── Game.exe               (启动器)
//      ├── game.config.json       (游戏配置)
//      ├── YamaEngine.dll       (引擎运行时)
//      ├── scenes/
//      │     ├── level1.yama
//      │     └── level2.yama
//      ├── assets/
//      │     ├── meshes/
//      │     ├── textures/
//      │     └── materials/
//      └── scripts/
//            └── GameScripts.dll
// ==================================================================================================

using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.IO.Compression;

namespace YamaEngine.Build
{
    /// <summary>构建进度报告。</summary>
    public sealed class BuildProgress
    {
        public string Stage { get; set; } = "";
        public int CurrentStep { get; set; }
        public int TotalSteps { get; set; }
        public string Message { get; set; } = "";
        public bool IsError { get; set; }

        public float Percent => TotalSteps > 0 ? (float)CurrentStep / TotalSteps : 0f;
    }

    /// <summary>构建结果（旧版，BuildPipeline.cs 专用；与 BuildPipelineReal.BuildResult 区分）。</summary>
    public sealed class BuildResultLegacy
    {
        public bool Success { get; set; }
        public string OutputPath { get; set; } = "";
        public string? InstallerPath { get; set; }
        public List<string> Errors { get; set; } = new();
        public List<string> Warnings { get; set; } = new();
        public int SceneCount { get; set; }
        public int AssetCount { get; set; }
        public long TotalSizeBytes { get; set; }
        public TimeSpan Duration { get; set; }

        /// <summary>导出根目录（Demo 子文件夹），供调用方定位产物。</summary>
        public string OutputDirectory { get; set; } = "";

        /// <summary>构建报告文件路径（build_report.json）。</summary>
        public string ReportPath { get; set; } = "";
    }

    /// <summary>
    /// 演示/测试构建选项：描述一次简单推理构建的输入。
    /// </summary>
    public sealed class BuildOptions
    {
        /// <summary>项目名称。</summary>
        public string ProjectName { get; set; } = "";

        /// <summary>输出根目录（产物写入其下的 Demo 子文件夹）。</summary>
        public string OutputDirectory { get; set; } = "";

        /// <summary>场景名称（用于生成 Content/&lt;SceneName&gt;.yama）。</summary>
        public string SceneName { get; set; } = "Main";

        /// <summary>资产数据库，用于收集场景对象与资源引用。</summary>
        public YamaEngine.Assets.AssetDatabase? Assets { get; set; }
    }

    /// <summary>
    /// 游戏打包构建管线。将编辑器项目导出为独立可运行的游戏。
    /// </summary>
    public sealed class BuildPipeline
    {
        private readonly BuildConfiguration _config;
        private readonly List<string> _errors = new();
        private readonly List<string> _warnings = new();
        private int _currentStep;
        private int _totalSteps;

        /// <summary>进度报告事件。</summary>
        public event Action<BuildProgress>? OnProgress;

        // ── 演示/测试构建使用的常量 ───────────────────────────────────────────

        /// <summary>演示构建的子文件夹名。</summary>
        public const string DemoFolderName = "Demo";

        /// <summary>构建清单文件名。</summary>
        public const string ManifestFileName = "build_manifest.json";

        /// <summary>启动脚本文件名。</summary>
        public const string LaunchScriptFileName = "launch.bat";

        /// <summary>说明文件（README）文件名。</summary>
        public const string ReadmeFileName = "README.txt";

        /// <summary>构建报告文件名。</summary>
        public const string ReportFileName = "build_report.json";

        /// <summary>
        /// 执行一次简单推理构建：导出场景、复制已导入资源、生成清单/启动器/README，
        /// 并写出 JSON 构建报告（含缺失引用检测）。供编辑器/测试使用。
        /// </summary>
        public static BuildResultLegacy Build(BuildOptions options)
        {
            var result = new BuildResultLegacy();
            if (options == null)
            {
                result.Success = false;
                result.Errors.Add("构建选项为 null。");
                return result;
            }

            try
            {
                string root = string.IsNullOrEmpty(options.OutputDirectory)
                    ? Path.Combine(Directory.GetCurrentDirectory(), "Builds")
                    : options.OutputDirectory;
                string exportRoot = Path.Combine(root, DemoFolderName);
                Directory.CreateDirectory(exportRoot);
                string contentDir = Path.Combine(exportRoot, "Content");
                Directory.CreateDirectory(contentDir);

                var warnings = new List<string>();
                var errors = new List<string>();
                var missing = new List<string>();

                var db = options.Assets;
                result.AssetCount = 0;

                // 1) 导出场景占位文件
                string sceneName = string.IsNullOrWhiteSpace(options.SceneName) ? "Main" : options.SceneName;
                File.WriteAllText(Path.Combine(contentDir, sceneName + "Scene.yama"), "// YamaEngine 导出场景\n");

                // 2) 复制已导入资源到 Content/<AssetPath>
                if (db != null)
                {
                    foreach (var asset in db.GetAllAssets())
                    {
                        if (asset == null || string.IsNullOrEmpty(asset.AssetPath)) continue;
                        string relative = asset.AssetPath.Replace('/', Path.DirectorySeparatorChar);
                        string dest = Path.Combine(contentDir, relative);
                        if (File.Exists(asset.SourceFile))
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                            File.Copy(asset.SourceFile, dest, overwrite: true);
                            result.AssetCount++;
                        }
                        else
                        {
                            warnings.Add($"资源源文件缺失: {asset.AssetPath}");
                            missing.Add(asset.AssetPath);
                        }
                    }

                    // 3) 检测场景对象引用的网格缺失
                    foreach (var obj in db.SceneObjects)
                    {
                        if (string.IsNullOrEmpty(obj.MeshAssetPath)) continue;
                        string rel = obj.MeshAssetPath.Replace('/', Path.DirectorySeparatorChar);
                        if (!File.Exists(Path.Combine(contentDir, rel)) && !File.Exists(obj.MeshAssetPath))
                        {
                            warnings.Add($"场景网格引用缺失: {obj.MeshAssetPath}");
                            missing.Add(obj.MeshAssetPath);
                        }
                    }
                    result.SceneCount = db.SceneObjects.Count;
                }

                // 4) 生成清单 / 启动器 / README
                File.WriteAllText(Path.Combine(exportRoot, ManifestFileName), "{}");
                File.WriteAllText(Path.Combine(exportRoot, LaunchScriptFileName),
                    "@echo off\r\nrem YamaEngine 启动器占位（演示构建）\r\nGameLauncher.exe\r\n");
                File.WriteAllText(Path.Combine(exportRoot, ReadmeFileName),
                    "yama引擎演示构建。\r\n");

                // 5) 写出构建报告
                string reportPath = Path.Combine(exportRoot, "build_report.json");
                var report = new Dictionary<string, object>
                {
                    ["projectName"] = options.ProjectName ?? "",
                    ["sceneName"] = sceneName,
                    ["scenePath"] = "Content/" + sceneName + "Scene.yama",
                    ["objectCount"] = db?.SceneObjects.Count ?? 0,
                    ["assetCount"] = result.AssetCount,
                    // BuildOptions 无运行时路径概念，故 runtime 从不内嵌（测试约定）。
                    ["runtimeIncluded"] = false,
                    ["engine"] = "Yama Engine",
                    ["warnings"] = warnings,
                    ["errors"] = errors,
                    ["missingReferences"] = missing,
                    ["exportedAtUtc"] = DateTime.UtcNow.ToString("O"),
                };
                File.WriteAllText(reportPath, JsonSerializer.Serialize(report,
                    new JsonSerializerOptions { WriteIndented = true }));

                result.Success = errors.Count == 0;
                result.OutputDirectory = exportRoot;
                result.ReportPath = reportPath;
                result.Errors.AddRange(errors);
                result.Warnings.AddRange(warnings);
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Errors.Add($"构建异常: {ex.Message}");
            }
            return result;
        }

        public BuildPipeline(BuildConfiguration config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
        }

        /// <summary>执行完整构建流程。</summary>
        public BuildResultLegacy Build()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = new BuildResultLegacy();
            _errors.Clear();
            _warnings.Clear();

            try
            {
                Report("初始化", "开始构建...");

                // 验证配置
                if (!ValidateConfig())
                {
                    result.Success = false;
                    result.Errors.AddRange(_errors);
                    return result;
                }

                string outputDir = PrepareOutputDirectory();
                result.OutputPath = outputDir;

                _totalSteps = 7;
                _currentStep = 0;

                // 1. 收集并复制场景
                var scenes = CollectScenes(outputDir);
                result.SceneCount = scenes.Count;

                // 2. 收集并复制资产
                var assets = CollectAssets(outputDir);
                result.AssetCount = assets.Count;

                // 3. 复制脚本 DLL
                CopyScripts(outputDir);

                // 4. 复制引擎 DLL
                CopyEngineBinaries(outputDir);

                // 5. 生成游戏配置
                GenerateGameConfig(outputDir);

                // 6. 生成启动器
                GenerateLauncher(outputDir);

                // 7. 可选：创建安装包
                if (_config.CreateInstaller)
                {
                    string zipPath = CreateInstaller(outputDir);
                    result.InstallerPath = zipPath;
                }

                // 计算总大小
                result.TotalSizeBytes = CalculateDirectorySize(outputDir);
                result.Success = _errors.Count == 0;
            }
            catch (Exception ex)
            {
                _errors.Add($"构建异常: {ex.Message}");
                result.Success = false;
            }

            sw.Stop();
            result.Duration = sw.Elapsed;
            result.Errors.AddRange(_errors);
            result.Warnings.AddRange(_warnings);

            Report("完成", result.Success ? "构建成功!" : "构建失败。");
            return result;
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  阶段实现
        // ─────────────────────────────────────────────────────────────────────────

        private bool ValidateConfig()
        {
            bool ok = true;
            if (string.IsNullOrEmpty(_config.ProjectPath) || !Directory.Exists(_config.ProjectPath))
            {
                _errors.Add($"项目路径无效: {_config.ProjectPath}");
                ok = false;
            }
            if (_config.Scenes.Count == 0)
            {
                _warnings.Add("未指定任何场景。");
            }
            return ok;
        }

        private string PrepareOutputDirectory()
        {
            string dir = string.IsNullOrEmpty(_config.OutputPath)
                ? Path.Combine(_config.ProjectPath, "Builds", _config.BuildName)
                : _config.OutputPath;

            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
            Directory.CreateDirectory(dir);

            return dir;
        }

        private List<string> CollectScenes(string outputDir)
        {
            Report("收集场景", "复制场景文件...");
            string scenesDir = Path.Combine(outputDir, "scenes");
            Directory.CreateDirectory(scenesDir);

            var copied = new List<string>();
            for (int i = 0; i < _config.Scenes.Count; i++)
            {
                string scenePath = _config.Scenes[i];
                if (!File.Exists(scenePath))
                {
                    _warnings.Add($"场景文件不存在: {scenePath}");
                    continue;
                }

                string destName = $"scene_{i:D3}{Path.GetExtension(scenePath)}";
                string destPath = Path.Combine(scenesDir, destName);
                File.Copy(scenePath, destPath, overwrite: true);
                copied.Add(destPath);
            }

            _currentStep++;
            return copied;
        }

        private List<string> CollectAssets(string outputDir)
        {
            Report("收集资产", "复制资产文件...");
            string assetsDir = Path.Combine(outputDir, "assets");
            Directory.CreateDirectory(assetsDir);

            var copied = new List<string>();
            var extensions = new[] { ".fbx", ".obj", ".gltf", ".glb", ".png", ".jpg", ".jpeg",
                ".dds", ".tga", ".mat", ".wav", ".ogg", ".mp3", ".prefab", ".skeleton", ".anim" };

            foreach (string folder in _config.AssetFolders)
            {
                if (!Directory.Exists(folder)) continue;

                foreach (string file in Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories))
                {
                    string ext = Path.GetExtension(file).ToLowerInvariant();
                    if (!extensions.Contains(ext)) continue;

                    string relative = Path.GetRelativePath(folder, file);
                    string destPath = Path.Combine(assetsDir, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                    File.Copy(file, destPath, overwrite: true);
                    copied.Add(destPath);
                }
            }

            _currentStep++;
            return copied;
        }

        private void CopyScripts(string outputDir)
        {
            if (!_config.IncludeScripts) return;

            Report("复制脚本", "复制脚本 DLL...");
            string scriptsDir = Path.Combine(outputDir, "scripts");
            Directory.CreateDirectory(scriptsDir);

            string projectScriptsDir = Path.Combine(_config.ProjectPath, "Scripts", "bin");
            if (Directory.Exists(projectScriptsDir))
            {
                foreach (string dll in Directory.GetFiles(projectScriptsDir, "*.dll"))
                {
                    string dest = Path.Combine(scriptsDir, Path.GetFileName(dll));
                    File.Copy(dll, dest, overwrite: true);
                }
            }

            _currentStep++;
        }

        private void CopyEngineBinaries(string outputDir)
        {
            Report("复制引擎", "复制引擎运行时...");

            // 复制引擎 DLL
            string engineDir = AppContext.BaseDirectory;
            string[] dlls = { "YamaEngine.dll", "ImGui.NET.dll", "SharpDX.dll", "SharpDX.D3D11.dll",
                "SharpDX.DXGI.dll", "SharpDX.Direct2D1.dll", "SharpDX.Direct3D11.dll",
                "SharpDX.Mathematics.dll", "SixLabors.ImageSharp.dll" };

            foreach (string dll in dlls)
            {
                string src = Path.Combine(engineDir, dll);
                if (File.Exists(src))
                {
                    string dest = Path.Combine(outputDir, dll);
                    File.Copy(src, dest, overwrite: true);
                }
            }

            _currentStep++;
        }

        private void GenerateGameConfig(string outputDir)
        {
            Report("生成配置", "生成 game.config.json...");

            var config = new Dictionary<string, object>
            {
                ["version"] = _config.Version,
                ["companyName"] = _config.CompanyName,
                ["productName"] = _config.ProductName,
                ["startSceneIndex"] = _config.StartSceneIndex,
                ["sceneCount"] = _config.Scenes.Count,
                ["windowWidth"] = _config.WindowSize.Width,
                ["windowHeight"] = _config.WindowSize.Height,
                ["fullscreen"] = _config.Fullscreen,
                ["vSync"] = _config.VSync,
                ["developmentBuild"] = _config.DevelopmentBuild,
            };

            string json = JsonSerializer.Serialize(config, new JsonSerializerOptions
            {
                WriteIndented = true,
            });

            File.WriteAllText(Path.Combine(outputDir, "game.config.json"), json);
            _currentStep++;
        }

        private void GenerateLauncher(string outputDir)
        {
            Report("生成启动器", "生成游戏入口...");

            // 生成 C# 启动器源码
            string launcherCode = GenerateLauncherCode();
            string launcherPath = Path.Combine(outputDir, "GameLauncher.cs");
            File.WriteAllText(launcherPath, launcherCode);

            _currentStep++;
        }

        private string GenerateLauncherCode()
        {
            return $$"""
// =============================================================================
// GameLauncher.cs — yama引擎游戏启动器（自动生成）
// =============================================================================
// 构建配置:
//   产品: {{_config.ProductName}}
//   版本: {{_config.Version}}
//   日期: {{_config.BuildDate:yyyy-MM-dd HH:mm}}
// =============================================================================

using System;
using System.IO;
using System.Text.Json;
using System.Numerics;
using YamaEngine;
using YamaEngine.ECS;
using YamaEngine.Rendering;
using YamaEngine.Scenes;
using YamaEngine.Scripting;
using YamaEngine.Audio;
using YamaEngine.Runtime.Physics;
using YamaEngine.Animation;

class GameLauncher
{
    static void Main(string[] args)
    {
        Console.WriteLine($"Starting {{_config.ProductName}} v{{_config.Version}}...");

        // 加载游戏配置
        string configPath = Path.Combine(AppContext.BaseDirectory, "game.config.json");
        var config = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(configPath));

        int width = config.GetProperty("windowWidth").GetInt32();
        int height = config.GetProperty("windowHeight").GetInt32();
        bool fullscreen = config.GetProperty("fullscreen").GetBoolean();
        bool vSync = config.GetProperty("vSync").GetBoolean();
        int startSceneIdx = config.GetProperty("startSceneIndex").GetInt32();

        // 初始化子系统
        var ecs = new EntityComponentSystem();
        var scriptRegistry = new ScriptRegistry(ecs);
        var audioEngine = new AdvancedAudioEngine();
        var physicsBridge = new PhysicsBridge(ecs);
        var animBridge = new EcsAnimationBridge(ecs);

        // 初始化渲染器
        var renderer = new SceneRenderer(width, height);
        renderer.Initialize(fullscreen);
        renderer.SetVSync(vSync);

        // 加载起始场景
        string scenesDir = Path.Combine(AppContext.BaseDirectory, "scenes");
        var sceneFiles = Directory.GetFiles(scenesDir, "*.yama").OrderBy(f => f).ToArray();
        if (startSceneIdx < sceneFiles.Length)
        {
            var sceneData = SceneJsonSerializer.LoadFromFile(sceneFiles[startSceneIdx]);
            SceneJsonSerializer.RestoreToECS(sceneData, ecs, scriptRegistry);
            Console.WriteLine($"Loaded scene: {sceneFiles[startSceneIdx]}");
        }

        // 加载脚本 DLL
        string scriptsDir = Path.Combine(AppContext.BaseDirectory, "scripts");
        if (Directory.Exists(scriptsDir))
        {
            foreach (var dll in Directory.GetFiles(scriptsDir, "*.dll"))
            {
                scriptRegistry.LoadAssembly(dll);
            }
        }

        // 启动脚本
        scriptRegistry.Update(0f);

        // 游戏主循环
        var clock = new System.Diagnostics.Stopwatch();
        clock.Start();
        float lastTime = 0f;

        while (renderer.IsRunning)
        {
            float now = (float)clock.Elapsed.TotalSeconds;
            float dt = now - lastTime;
            lastTime = now;

            // 物理
            physicsBridge.Step(dt);

            // 动画
            animBridge.Update(dt);

            // 脚本
            scriptRegistry.Update(dt);
            scriptRegistry.FixedUpdate(1f / 60f);
            scriptRegistry.LateUpdate(dt);

            // 渲染
            renderer.RenderScene(ecs);
            renderer.Present();
        }

        // 清理
        audioEngine.Dispose();
        renderer.Dispose();
        physicsBridge.Dispose();
        scriptRegistry.DestroyAllInstances();
        ecs.Dispose();
    }
}
""";
        }

        private string CreateInstaller(string outputDir)
        {
            Report("创建安装包", "打包为 ZIP...");
            string zipPath = outputDir + ".zip";
            if (File.Exists(zipPath)) File.Delete(zipPath);
            ZipFile.CreateFromDirectory(outputDir, zipPath, CompressionLevel.Optimal, includeBaseDirectory: true);
            _currentStep++;
            return zipPath;
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  辅助
        // ─────────────────────────────────────────────────────────────────────────

        private void Report(string stage, string message, bool isError = false)
        {
            OnProgress?.Invoke(new BuildProgress
            {
                Stage = stage,
                CurrentStep = _currentStep,
                TotalSteps = _totalSteps,
                Message = message,
                IsError = isError,
            });
        }

        private static long CalculateDirectorySize(string path)
        {
            long size = 0;
            foreach (var file in Directory.EnumerateFiles(path, "*.*", SearchOption.AllDirectories))
            {
                try { size += new FileInfo(file).Length; }
                catch { }
            }
            return size;
        }
    }
}
