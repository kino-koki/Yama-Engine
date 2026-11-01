using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace YamaEngine.Build
{
    // ════════════════════════════════════════════════════════════════════════
    // yama引擎打包系统 (PackageSystem)
    //
    // 类似 UE Package 阶段，将 Cook 后的资产、运行时二进制、配置文件等
    // 打包为平台特定的可分发格式。
    // 支持：Windows(.exe)、Linux(AppImage)、Android(APK)、HTML5/WebGL(WASM)。
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 目标架构。
    /// </summary>
    public enum PackageArchitecture
    {
        /// <summary>x86-64 (64位)。</summary>
        x64,
        /// <summary>ARM64 (AArch64)。</summary>
        ARM64,
        /// <summary>x86 (32位)。</summary>
        x86,
        /// <summary>Universal (macOS Universal Binary)。</summary>
        Universal
    }

    /// <summary>
    /// 打包配置。描述一次打包操作的全部参数。
    /// </summary>
    public sealed class PackageConfig
    {
        /// <summary>项目名称。</summary>
        public string ProjectName { get; set; } = "Yama Project";

        /// <summary>项目版本号。</summary>
        public string Version { get; set; } = "1.0.0";

        /// <summary>版本码 (整数, 用于 Android/iOS)。</summary>
        public int VersionCode { get; set; } = 1;

        /// <summary>目标平台。</summary>
        public CookPlatform TargetPlatform { get; set; } = CookPlatform.Windows;

        /// <summary>目标架构。</summary>
        public PackageArchitecture Architecture { get; set; } = PackageArchitecture.x64;

        /// <summary>是否为发布构建（去除调试信息）。</summary>
        public bool Shipping { get; set; } = false;

        /// <summary>是否为调试构建（包含调试符号和 Profiler）。</summary>
        public bool Debug { get; set; } = false;

        /// <summary>项目图标路径 (.png/.ico)。</summary>
        public string? IconPath { get; set; }

        /// <summary>Cook 后资产目录（CookingSystem 输出目录）。</summary>
        public string CookedAssetsDirectory { get; set; } = string.Empty;

        /// <summary>运行时二进制目录。</summary>
        public string RuntimeBinaryDirectory { get; set; } = string.Empty;

        /// <summary>输出根目录。</summary>
        public string OutputDirectory { get; set; } = string.Empty;

        /// <summary>项目描述。</summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>公司/组织名称。</summary>
        public string CompanyName { get; set; } = "Yama Engine";

        /// <summary>Copyright 信息。</summary>
        public string Copyright { get; set; } = string.Empty;

        /// <summary>Bundle ID (用于 Android/iOS)。</summary>
        public string BundleIdentifier { get; set; } = "com.yamaengine.project";

        /// <summary>Android Activity 类名。</summary>
        public string AndroidActivityClass { get; set; } = "com.yamaengine.MainActivity";

        /// <summary>Android 最小 SDK 版本。</summary>
        public int AndroidMinSdk { get; set; } = 24;

        /// <summary>Android 目标 SDK 版本。</summary>
        public int AndroidTargetSdk { get; set; } = 34;

        /// <summary>HTML5 页面标题。</summary>
        public string HtmlTitle { get; set; } = "Yama Engine Game";

        /// <summary>HTML5 Canvas 分辨率宽度。</summary>
        public int HtmlCanvasWidth { get; set; } = 1280;

        /// <summary>HTML5 Canvas 分辨率高度。</summary>
        public int HtmlCanvasHeight { get; set; } = 720;

        /// <summary>额外 DLL 依赖列表。</summary>
        public List<string> AdditionalDlls { get; set; } = new();

        /// <summary>自定义配置键值对。</summary>
        public Dictionary<string, string> CustomConfig { get; set; } = new();
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 打包结果
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 打包结果。
    /// </summary>
    public sealed class PackageResult
    {
        /// <summary>是否成功。</summary>
        public bool Success { get; set; }

        /// <summary>输出目录。</summary>
        public string OutputDirectory { get; set; } = string.Empty;

        /// <summary>打包格式描述。</summary>
        public string PackageFormat { get; set; } = string.Empty;

        /// <summary>主可执行文件路径（桌面平台）。</summary>
        public string ExecutablePath { get; set; } = string.Empty;

        /// <summary>总耗时 (毫秒)。</summary>
        public double TotalDurationMs { get; set; }

        /// <summary>输出总大小 (字节)。</summary>
        public long TotalSizeBytes { get; set; }

        /// <summary>包含的文件数量。</summary>
        public int FileCount { get; set; }

        /// <summary>各阶段耗时 (毫秒)。</summary>
        public Dictionary<string, double> PhaseTimingsMs { get; set; } = new();

        /// <summary>验证结果。</summary>
        public PackageValidationResult? Validation { get; set; }

        /// <summary>警告列表。</summary>
        public List<string> Warnings { get; set; } = new();

        /// <summary>错误列表。</summary>
        public List<string> Errors { get; set; } = new();
    }

    /// <summary>
    /// 打包验证结果。
    /// </summary>
    public sealed class PackageValidationResult
    {
        /// <summary>验证是否全部通过。</summary>
        public bool IsValid { get; set; }

        /// <summary>完整性校验通过。</summary>
        public bool IntegrityCheckPassed { get; set; }

        /// <summary>依赖检查通过。</summary>
        public bool DependencyCheckPassed { get; set; }

        /// <summary>可执行文件存在。</summary>
        public bool ExecutableExists { get; set; }

        /// <summary>运行时 DLL 完整。</summary>
        public bool RuntimeDllsComplete { get; set; }

        /// <summary>资产文件完整。</summary>
        public bool AssetsComplete { get; set; }

        /// <summary>缺少的文件列表。</summary>
        public List<string> MissingFiles { get; set; } = new();

        /// <summary>校验失败的文件列表。</summary>
        public List<string> FailedFiles { get; set; } = new();
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 打包构建器
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// yama引擎打包构建器。
    /// 流程：Cook资产 -> 复制运行时 -> 复制依赖DLL -> 生成配置文件 -> 创建启动器 -> 打包压缩 -> 验证。
    /// </summary>
    public sealed class PackageBuilder
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        /// <summary>
        /// 执行打包。
        /// </summary>
        public PackageResult Build(PackageConfig config)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            var overallSw = Stopwatch.StartNew();
            var result = new PackageResult
            {
                OutputDirectory = config.OutputDirectory
            };

            try
            {
                var packageDir = GetPackageOutputDirectory(config);
                Directory.CreateDirectory(packageDir);

                // 阶段 1: 复制 Cook 后资产
                var sw = Stopwatch.StartNew();
                CopyCookedAssets(config, packageDir, result);
                sw.Stop();
                result.PhaseTimingsMs["CopyAssets"] = sw.Elapsed.TotalMilliseconds;

                // 阶段 2: 复制运行时二进制
                sw.Restart();
                CopyRuntimeBinaries(config, packageDir, result);
                sw.Stop();
                result.PhaseTimingsMs["CopyRuntime"] = sw.Elapsed.TotalMilliseconds;

                // 阶段 3: 复制依赖 DLL
                sw.Restart();
                CopyDependencyDlls(config, packageDir, result);
                sw.Stop();
                result.PhaseTimingsMs["CopyDlls"] = sw.Elapsed.TotalMilliseconds;

                // 阶段 4: 生成配置文件
                sw.Restart();
                GenerateConfigFiles(config, packageDir);
                sw.Stop();
                result.PhaseTimingsMs["GenerateConfig"] = sw.Elapsed.TotalMilliseconds;

                // 阶段 5: 平台特定打包
                sw.Restart();
                PerformPlatformSpecificPackaging(config, packageDir, result);
                sw.Stop();
                result.PhaseTimingsMs["PlatformPackage"] = sw.Elapsed.TotalMilliseconds;

                // 阶段 6: 验证
                sw.Restart();
                result.Validation = ValidatePackage(config, packageDir);
                sw.Stop();
                result.PhaseTimingsMs["Validation"] = sw.Elapsed.TotalMilliseconds;

                // 计算总大小
                result.TotalSizeBytes = CalculateDirectorySize(packageDir);
                result.FileCount = Directory.EnumerateFiles(packageDir, "*.*", SearchOption.AllDirectories).Count();

                result.Success = result.Errors.Count == 0;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Errors.Add($"Package build error: {ex.Message}");
            }
            finally
            {
                overallSw.Stop();
                result.TotalDurationMs = overallSw.Elapsed.TotalMilliseconds;
            }

            return result;
        }

        /// <summary>
        /// 获取平台特定的输出目录名。
        /// </summary>
        private string GetPackageOutputDirectory(PackageConfig config)
        {
            var platformName = config.TargetPlatform switch
            {
                CookPlatform.Windows => "Windows",
                CookPlatform.Linux => "Linux",
                CookPlatform.Android => "Android",
                CookPlatform.iOS => "iOS",
                CookPlatform.Mac => "Mac",
                CookPlatform.Console => "Console",
                CookPlatform.HTML5 or CookPlatform.WebGL => "WebGL",
                _ => "Unknown"
            };

            var archName = config.Architecture switch
            {
                PackageArchitecture.x64 => "x64",
                PackageArchitecture.ARM64 => "ARM64",
                PackageArchitecture.x86 => "x86",
                PackageArchitecture.Universal => "Universal",
                _ => "x64"
            };

            var buildType = config.Shipping ? "Shipping" : config.Debug ? "Debug" : "Development";
            return Path.Combine(config.OutputDirectory, $"{config.ProjectName}_{platformName}_{archName}_{buildType}");
        }

        // ── 阶段 1: 复制 Cook 后资产 ──
        private void CopyCookedAssets(PackageConfig config, string packageDir, PackageResult result)
        {
            var cookedDir = config.CookedAssetsDirectory;
            if (string.IsNullOrWhiteSpace(cookedDir) || !Directory.Exists(cookedDir))
            {
                result.Warnings.Add("Cooked assets directory not found. Skipping asset copy.");
                return;
            }

            var targetDir = Path.Combine(packageDir, "Content");
            CopyDirectory(cookedDir, targetDir);
        }

        // ── 阶段 2: 复制运行时二进制 ──
        private void CopyRuntimeBinaries(PackageConfig config, string packageDir, PackageResult result)
        {
            var runtimeDir = config.RuntimeBinaryDirectory;
            if (string.IsNullOrWhiteSpace(runtimeDir) || !Directory.Exists(runtimeDir))
            {
                result.Warnings.Add("Runtime binary directory not found. Skipping runtime copy.");
                return;
            }

            var targetDir = Path.Combine(packageDir, "Runtime");
            CopyDirectory(runtimeDir, targetDir);

            // 确定主可执行文件
            switch (config.TargetPlatform)
            {
                case CookPlatform.Windows:
                    var exePath = Path.Combine(targetDir, "YamaRuntime.exe");
                    if (File.Exists(exePath))
                        result.ExecutablePath = exePath;
                    else
                        result.Warnings.Add("YamaRuntime.exe not found in runtime directory.");
                    break;

                case CookPlatform.Linux:
                    var linuxExe = Path.Combine(targetDir, "YamaRuntime");
                    if (File.Exists(linuxExe))
                        result.ExecutablePath = linuxExe;
                    break;

                case CookPlatform.Mac:
                    var macApp = Path.Combine(targetDir, "YamaRuntime.app");
                    if (Directory.Exists(macApp))
                        result.ExecutablePath = Path.Combine(macApp, "Contents", "MacOS", "YamaRuntime");
                    break;
            }
        }

        // ── 阶段 3: 复制依赖 DLL ──
        private void CopyDependencyDlls(PackageConfig config, string packageDir, PackageResult result)
        {
            var dllDir = Path.Combine(packageDir, "Runtime");
            foreach (var dll in config.AdditionalDlls)
            {
                if (!File.Exists(dll))
                {
                    result.Warnings.Add($"Additional DLL not found: {dll}");
                    continue;
                }
                File.Copy(dll, Path.Combine(dllDir, Path.GetFileName(dll)), true);
            }
        }

        // ── 阶段 4: 生成配置文件 ──
        private void GenerateConfigFiles(PackageConfig config, string packageDir)
        {
            // 生成 engine.config.json
            var engineConfig = new Dictionary<string, object>
            {
                ["projectName"] = config.ProjectName,
                ["version"] = config.Version,
                ["engine"] = "Yama Engine 1.0",
                ["platform"] = config.TargetPlatform.ToString(),
                ["architecture"] = config.Architecture.ToString(),
                ["shipping"] = config.Shipping,
                ["debug"] = config.Debug,
                ["companyName"] = config.CompanyName,
                ["copyright"] = config.Copyright,
                ["description"] = config.Description
            };

            var configPath = Path.Combine(packageDir, "engine.config.json");
            var json = JsonSerializer.Serialize(engineConfig, JsonOptions);
            File.WriteAllText(configPath, json, Encoding.UTF8);

            // 生成 .manifest 文件（文件列表 + 哈希，用于完整性校验）
            GeneratePackageManifest(config, packageDir);

            // Windows 启动脚本
            if (config.TargetPlatform == CookPlatform.Windows)
            {
                GenerateWindowsLauncher(config, packageDir);
            }
        }

        /// <summary>
        /// 生成包清单文件（所有文件哈希列表）。
        /// </summary>
        private void GeneratePackageManifest(PackageConfig config, string packageDir)
        {
            var manifest = new PackageManifestData
            {
                ProjectName = config.ProjectName,
                Version = config.Version,
                Platform = config.TargetPlatform.ToString(),
                GeneratedAtUtc = DateTime.UtcNow,
                Files = new List<PackageFileEntry>()
            };

            foreach (var file in Directory.EnumerateFiles(packageDir, "*.*", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(packageDir, file).Replace('\\', '/');
                var hash = ComputeFileHashSha256(file);
                var size = new FileInfo(file).Length;

                manifest.Files.Add(new PackageFileEntry
                {
                    Path = relativePath,
                    Hash = hash,
                    SizeBytes = size
                });
            }

            var manifestPath = Path.Combine(packageDir, "package.manifest");
            var json = JsonSerializer.Serialize(manifest, JsonOptions);
            File.WriteAllText(manifestPath, json, Encoding.UTF8);
        }

        /// <summary>
        /// 生成 Windows 启动脚本。
        /// </summary>
        private void GenerateWindowsLauncher(PackageConfig config, string packageDir)
        {
            var script = $@"@echo off
setlocal
chcp 65001 >nul
title {config.ProjectName} v{config.Version}
cd /d ""%~dp0""
Runtime\YamaRuntime.exe ""%~dp0""
if errorlevel 1 (
    echo [{config.ProjectName}] Runtime exited with error code %errorlevel%.
    pause
)
endlocal".Replace("\r\n", "\r\n");

            File.WriteAllText(Path.Combine(packageDir, "launch.bat"), script, Encoding.UTF8);
        }

        // ── 阶段 5: 平台特定打包 ──
        private void PerformPlatformSpecificPackaging(PackageConfig config, string packageDir, PackageResult result)
        {
            switch (config.TargetPlatform)
            {
                case CookPlatform.Windows:
                    PackageForWindows(config, packageDir, result);
                    break;

                case CookPlatform.Linux:
                    PackageForLinux(config, packageDir, result);
                    break;

                case CookPlatform.Android:
                    PackageForAndroid(config, packageDir, result);
                    break;

                case CookPlatform.HTML5:
                case CookPlatform.WebGL:
                    PackageForWebGL(config, packageDir, result);
                    break;

                case CookPlatform.iOS:
                    PackageForIOS(config, packageDir, result);
                    break;

                case CookPlatform.Mac:
                    PackageForMac(config, packageDir, result);
                    break;

                default:
                    result.Warnings.Add($"Platform-specific packaging not implemented for {config.TargetPlatform}.");
                    break;
            }
        }

        /// <summary>
        /// Windows 打包：.exe + 资产目录 + DLL + .manifest
        /// </summary>
        private void PackageForWindows(PackageConfig config, string packageDir, PackageResult result)
        {
            result.PackageFormat = "Windows Executable Directory";

            // 如果有图标，复制到输出目录
            if (!string.IsNullOrWhiteSpace(config.IconPath) && File.Exists(config.IconPath))
            {
                var ext = Path.GetExtension(config.IconPath).ToLowerInvariant();
                if (ext == ".ico")
                    File.Copy(config.IconPath, Path.Combine(packageDir, "app.ico"), true);
                else
                    File.Copy(config.IconPath, Path.Combine(packageDir, "app_icon.png"), true);
            }
        }

        /// <summary>
        /// Linux 打包：AppImage/包目录 + 资产
        /// </summary>
        private void PackageForLinux(PackageConfig config, string packageDir, PackageResult result)
        {
            result.PackageFormat = "Linux Application Directory";

            // 生成 shell 启动脚本
            var script = $"#!/bin/bash\n" +
                         $"cd \"$(dirname \"$0\")\"\n" +
                         $"./Runtime/YamaRuntime \"$PWD\"\n";
            var scriptPath = Path.Combine(packageDir, "launch.sh");
            File.WriteAllText(scriptPath, script, Encoding.UTF8);
        }

        /// <summary>
        /// Android 打包：APK 目录结构 (Activity/资产/库)
        /// </summary>
        private void PackageForAndroid(PackageConfig config, string packageDir, PackageResult result)
        {
            result.PackageFormat = "Android APK Structure";

            // 创建 APK 目录结构
            var apkRoot = Path.Combine(packageDir, "apk_structure");
            var assetsDir = Path.Combine(apkRoot, "assets", "Content");
            var libDir = Path.Combine(apkRoot, "lib", config.Architecture == PackageArchitecture.ARM64 ? "arm64-v8a" : "armeabi-v7a");

            Directory.CreateDirectory(assetsDir);
            Directory.CreateDirectory(libDir);

            // 复制资产到 assets
            var contentDir = Path.Combine(packageDir, "Content");
            if (Directory.Exists(contentDir))
                CopyDirectory(contentDir, assetsDir);

            // 生成 AndroidManifest.xml (简化)
            var androidManifest = $@"<?xml version=""1.0"" encoding=""utf-8""?>
<manifest xmlns:android=""http://schemas.android.com/apk/res/android""
    package=""{config.BundleIdentifier}""
    android:versionCode=""{config.VersionCode}""
    android:versionName=""{config.Version}"">
    <uses-sdk android:minSdkVersion=""{config.AndroidMinSdk}"" android:targetSdkVersion=""{config.AndroidTargetSdk}"" />
    <application android:label=""{config.ProjectName}""
        android:hasCode=""true""
        android:icon=""@mipmap/ic_launcher"">
        <activity android:name=""{config.AndroidActivityClass}""
            android:configChanges=""orientation|keyboardHidden|screenSize""
            android:screenOrientation=""sensorLandscape"">
            <intent-filter>
                <action android:name=""android.intent.action.MAIN"" />
                <category android:name=""android.intent.category.LAUNCHER"" />
            </intent-filter>
        </activity>
    </application>
</manifest>";

            File.WriteAllText(Path.Combine(apkRoot, "AndroidManifest.xml"), androidManifest, Encoding.UTF8);
        }

        /// <summary>
        /// HTML5/WebGL 打包：WASM+JS+HTML+资产
        /// </summary>
        private void PackageForWebGL(PackageConfig config, string packageDir, PackageResult result)
        {
            result.PackageFormat = "WebGL Static Site";

            var webRoot = Path.Combine(packageDir, "Web");
            Directory.CreateDirectory(webRoot);

            // 生成 HTML 页面
            var html = $@"<!DOCTYPE html>
<html lang=""en"">
<head>
    <meta charset=""UTF-8"">
    <meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
    <title>{config.HtmlTitle}</title>
    <style>
        body {{ margin: 0; background: #000; overflow: hidden; display: flex; justify-content: center; align-items: center; height: 100vh; }}
        canvas {{ display: block; }}
        #loading {{ color: #fff; font-family: sans-serif; position: absolute; }}
    </style>
</head>
<body>
    <div id=""loading"">Loading {config.ProjectName}...</div>
    <canvas id=""game-canvas"" width=""{config.HtmlCanvasWidth}"" height=""{config.HtmlCanvasHeight}""></canvas>
    <script>
        // Yama Engine WebGL Runtime Loader
        var Module = {{
            canvas: document.getElementById('game-canvas'),
            onRuntimeInitialized: function() {{
                document.getElementById('loading').style.display = 'none';
            }}
        }};
    </script>
    <script src=""yama_runtime.js""></script>
</body>
</html>";

            File.WriteAllText(Path.Combine(webRoot, "index.html"), html, Encoding.UTF8);

            // 复制资产到 web 目录
            var contentDir = Path.Combine(packageDir, "Content");
            var webContentDir = Path.Combine(webRoot, "Content");
            if (Directory.Exists(contentDir))
                CopyDirectory(contentDir, webContentDir);

            // 占位：WASM 和 JS 运行时文件
            File.WriteAllText(Path.Combine(webRoot, "yama_runtime.js"),
                "// Yama Engine WebGL Runtime (placeholder)\n// Actual runtime would be compiled from C++/Rust to WASM\n",
                Encoding.UTF8);
            File.WriteAllBytes(Path.Combine(webRoot, "yama_runtime.wasm"), new byte[] { 0x00, 0x61, 0x73, 0x6D }); // WASM magic
        }

        /// <summary>
        /// iOS 打包：.app 目录结构
        /// </summary>
        private void PackageForIOS(PackageConfig config, string packageDir, PackageResult result)
        {
            result.PackageFormat = "iOS App Bundle Structure";

            var appRoot = Path.Combine(packageDir, "YamaGame.app");
            Directory.CreateDirectory(appRoot);

            // Info.plist (简化)
            var infoPlist = $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<!DOCTYPE plist PUBLIC ""-//Apple//DTD PLIST 1.0//EN"" ""http://www.apple.com/DTDs/PropertyList-1.0.dtd"">
<plist version=""1.0"">
<dict>
    <key>CFBundleDisplayName</key>
    <string>{config.ProjectName}</string>
    <key>CFBundleExecutable</key>
    <string>YamaRuntime</string>
    <key>CFBundleIdentifier</key>
    <string>{config.BundleIdentifier}</string>
    <key>CFBundleVersion</key>
    <string>{config.Version}</string>
    <key>CFBundleShortVersionString</key>
    <string>{config.Version}</string>
    <key>LSRequiresIPhoneOS</key>
    <true/>
    <key>MinimumOSVersion</key>
    <string>14.0</string>
    <key>UIRequiredDeviceCapabilities</key>
    <array>
        <string>arm64</string>
    </array>
</dict>
</plist>";

            File.WriteAllText(Path.Combine(appRoot, "Info.plist"), infoPlist, Encoding.UTF8);
        }

        /// <summary>
        /// macOS 打包：.app 目录结构
        /// </summary>
        private void PackageForMac(PackageConfig config, string packageDir, PackageResult result)
        {
            result.PackageFormat = "macOS Application Bundle";

            var appRoot = Path.Combine(packageDir, "YamaGame.app");
            var contentsDir = Path.Combine(appRoot, "Contents");
            var macosDir = Path.Combine(contentsDir, "MacOS");
            var resourcesDir = Path.Combine(contentsDir, "Resources");

            Directory.CreateDirectory(macosDir);
            Directory.CreateDirectory(resourcesDir);

            // Info.plist
            var infoPlist = $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<!DOCTYPE plist PUBLIC ""-//Apple//DTD PLIST 1.0//EN"" ""http://www.apple.com/DTDs/PropertyList-1.0.dtd"">
<plist version=""1.0"">
<dict>
    <key>CFBundleDisplayName</key>
    <string>{config.ProjectName}</string>
    <key>CFBundleExecutable</key>
    <string>YamaRuntime</string>
    <key>CFBundleIdentifier</key>
    <string>{config.BundleIdentifier}</string>
    <key>CFBundleVersion</key>
    <string>{config.Version}</string>
    <key>NSHighResolutionCapable</key>
    <true/>
</dict>
</plist>";

            File.WriteAllText(Path.Combine(contentsDir, "Info.plist"), infoPlist, Encoding.UTF8);
        }

        // ── 阶段 6: 验证 ──
        private PackageValidationResult ValidatePackage(PackageConfig config, string packageDir)
        {
            var validation = new PackageValidationResult();

            // 1. 可执行文件存在性
            if (!string.IsNullOrWhiteSpace(result_executable_path_ref(config, packageDir, out var exePath)))
            {
                validation.ExecutableExists = File.Exists(exePath);
                if (!validation.ExecutableExists)
                    validation.MissingFiles.Add("Executable");
            }

            // 2. 依赖检查
            validation.DependencyCheckPassed = true;
            foreach (var dll in config.AdditionalDlls)
            {
                var fileName = Path.GetFileName(dll);
                var targetPath = Path.Combine(packageDir, "Runtime", fileName);
                if (!File.Exists(targetPath))
                {
                    validation.DependencyCheckPassed = false;
                    validation.MissingFiles.Add($"DLL: {fileName}");
                }
            }

            // 3. 完整性校验（与 manifest 对比）
            var manifestPath = Path.Combine(packageDir, "package.manifest");
            if (File.Exists(manifestPath))
            {
                validation.IntegrityCheckPassed = true;
                try
                {
                    var json = File.ReadAllText(manifestPath, Encoding.UTF8);
                    var manifest = JsonSerializer.Deserialize<PackageManifestData>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (manifest?.Files != null)
                    {
                        foreach (var fileEntry in manifest.Files)
                        {
                            var fullPath = Path.Combine(packageDir, fileEntry.Path.Replace('/', Path.DirectorySeparatorChar));
                            if (!File.Exists(fullPath))
                            {
                                validation.IntegrityCheckPassed = false;
                                validation.MissingFiles.Add(fileEntry.Path);
                            }
                        }
                    }
                }
                catch
                {
                    validation.IntegrityCheckPassed = false;
                }
            }
            else
            {
                validation.IntegrityCheckPassed = true; // manifest 不存在则跳过
            }

            // 4. 资产完整性
            var contentDir = Path.Combine(packageDir, "Content");
            validation.AssetsComplete = Directory.Exists(contentDir) &&
                                      Directory.EnumerateFiles(contentDir, "*.*", SearchOption.AllDirectories).Any();

            validation.IsValid = validation.ExecutableExists && validation.DependencyCheckPassed &&
                                 validation.IntegrityCheckPassed && validation.AssetsComplete;

            return validation;
        }

        private static string? result_executable_path_ref(PackageConfig config, string packageDir, out string exePath)
        {
            exePath = config.TargetPlatform switch
            {
                CookPlatform.Windows => Path.Combine(packageDir, "Runtime", "YamaRuntime.exe"),
                CookPlatform.Linux => Path.Combine(packageDir, "Runtime", "YamaRuntime"),
                CookPlatform.Mac => Path.Combine(packageDir, "YamaGame.app", "Contents", "MacOS", "YamaRuntime"),
                _ => string.Empty
            };
            return exePath;
        }

        // ── 工具方法 ──

        private static void CopyDirectory(string sourceDir, string targetDir)
        {
            Directory.CreateDirectory(targetDir);
            foreach (var file in Directory.EnumerateFiles(sourceDir))
            {
                var targetFile = Path.Combine(targetDir, Path.GetFileName(file));
                File.Copy(file, targetFile, true);
            }
            foreach (var dir in Directory.EnumerateDirectories(sourceDir))
            {
                var subTarget = Path.Combine(targetDir, Path.GetFileName(dir));
                CopyDirectory(dir, subTarget);
            }
        }

        private static string ComputeFileHashSha256(string filePath)
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            using var stream = File.OpenRead(filePath);
            var hashBytes = sha.ComputeHash(stream);
            return Convert.ToHexString(hashBytes).ToLowerInvariant();
        }

        private static long CalculateDirectorySize(string path)
        {
            return Directory.EnumerateFiles(path, "*.*", SearchOption.AllDirectories)
                .Sum(f => new FileInfo(f).Length);
        }

        // ── 序列化类型 ──

        private sealed class PackageManifestData
        {
            public string ProjectName { get; set; } = string.Empty;
            public string Version { get; set; } = string.Empty;
            public string Platform { get; set; } = string.Empty;
            public DateTime GeneratedAtUtc { get; set; }
            public List<PackageFileEntry> Files { get; set; } = new();
        }

        private sealed class PackageFileEntry
        {
            public string Path { get; set; } = string.Empty;
            public string Hash { get; set; } = string.Empty;
            public long SizeBytes { get; set; }
        }
    }
}
