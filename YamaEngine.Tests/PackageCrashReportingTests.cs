// PackageCrashReportingTests.cs — 打包崩溃上报集成 (CrashReporterConfig → PackageBuilder → ConfigureFromFile → 报告落盘) 验证套件
// 覆盖:
//   * CrashReporterConfig JSON 往返 (ToJson/FromJson 保留字段)
//   * PackageBuilder 生成 CrashReporterConfig.json (启用)
//   * PackageBuilder 禁用崩溃上报 (不生成配置)
//   * ConfigureFromFile → ForceReport 落盘崩溃报告 (端到端)
//   * CrashReporter.Initialize → ForceReport 生成报告文件
// 全部 CPU 级确定性, 不依赖真实网络/GPU。
using System;
using System.IO;
using System.Linq;
using YamaEngine.Build;
using YamaEngine.Diagnostics;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests;

/// <summary>打包崩溃上报集成验证套件（对齐 UE4.7 打包 + 崩溃上报闭环）。</summary>
public sealed class PackageCrashReportingTests
{
    private static string TempDir(string prefix)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"{prefix}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void DeleteDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        catch { /* 清理尽力而为 */ }
    }

    /// <summary>CrashReporterConfig JSON 往返保留字段。</summary>
    public static void TestCrashReporterConfigRoundtrip()
    {
        var config = new CrashReporterConfig
        {
            Enabled = true,
            ReportsDirectory = "Saved/Crashes",
            EngineVersion = "2.5.0",
            RemoteEndpoint = "https://crash.example.com/ingest",
            ProjectName = "MyGame"
        };

        var json = config.ToJson();
        var roundtrip = CrashReporterConfig.FromJson(json);

        AssertTrue(roundtrip.Enabled, "往返应保留 Enabled。");
        AssertEqual("Saved/Crashes", roundtrip.ReportsDirectory, "往返应保留 ReportsDirectory。");
        AssertEqual("2.5.0", roundtrip.EngineVersion, "往返应保留 EngineVersion。");
        AssertEqual("https://crash.example.com/ingest", roundtrip.RemoteEndpoint, "往返应保留 RemoteEndpoint。");
        AssertEqual("MyGame", roundtrip.ProjectName, "往返应保留 ProjectName。");
    }

    /// <summary>PackageBuilder 启用崩溃上报 → 生成 CrashReporterConfig.json。</summary>
    public static void TestPackageGeneratesCrashReporterConfig()
    {
        var outDir = TempDir("pkg_crash");
        try
        {
            var builder = new PackageBuilder();
            var result = builder.Build(new PackageConfig
            {
                ProjectName = "CrashGame",
                Version = "3.1.4",
                OutputDirectory = outDir,
                EnableCrashReporting = true,
                CrashReportsDirectory = "Saved/Crashes",
                CrashReportEndpoint = "https://crash.example.com/ingest"
            });

            AssertTrue(result.Success, "打包应成功 (空目录仅告警)。");
            AssertTrue(result.CrashReportingEnabled, "崩溃上报应集成。");

            var configPath = result.CrashReportConfigPath;
            AssertTrue(File.Exists(configPath), $"崩溃上报配置文件应存在: {configPath}");

            var json = File.ReadAllText(configPath);
            var config = CrashReporterConfig.FromJson(json);
            AssertTrue(config.Enabled, "生成的配置应启用。");
            AssertEqual("Saved/Crashes", config.ReportsDirectory, "生成的配置应含崩溃目录。");
            AssertEqual("3.1.4", config.EngineVersion, "生成的配置应含引擎版本。");
            AssertEqual("https://crash.example.com/ingest", config.RemoteEndpoint, "生成的配置应含远端端点。");
            AssertEqual("CrashGame", config.ProjectName, "生成的配置应含项目名。");
        }
        finally { DeleteDir(outDir); }
    }

    /// <summary>PackageBuilder 禁用崩溃上报 → 不生成配置。</summary>
    public static void TestPackageDisablesCrashReporting()
    {
        var outDir = TempDir("pkg_nocrash");
        try
        {
            var builder = new PackageBuilder();
            var result = builder.Build(new PackageConfig
            {
                ProjectName = "NoCrashGame",
                OutputDirectory = outDir,
                EnableCrashReporting = false
            });

            AssertTrue(!result.CrashReportingEnabled, "崩溃上报应禁用。");
            AssertTrue(!File.Exists(Path.Combine(result.OutputDirectory, "CrashReporterConfig.json")),
                "禁用时不应生成崩溃上报配置文件。");
        }
        finally { DeleteDir(outDir); }
    }

    /// <summary>ConfigureFromFile → ForceReport 落盘崩溃报告 (端到端)。</summary>
    public static void TestConfigureFromFileInitializesAndReports()
    {
        var baseDir = TempDir("crash_e2e");
        var crashDir = Path.Combine(baseDir, "Crashes");
        try
        {
            // 生成配置文件 (模拟打包产物)
            var config = new CrashReporterConfig
            {
                Enabled = true,
                ReportsDirectory = crashDir,
                EngineVersion = "1.0.0"
            };
            var configPath = Path.Combine(baseDir, "CrashReporterConfig.json");
            File.WriteAllText(configPath, config.ToJson());

            // 运行时自配置
            CrashReporter.ConfigureFromFile(configPath);
            CrashReporter.Log("pre-crash log line");

            // 触发崩溃上报
            CrashReporter.ForceReport(new InvalidOperationException("test crash e2e"));

            AssertTrue(Directory.Exists(crashDir), "崩溃报告目录应存在。");
            var reports = Directory.GetFiles(crashDir, "crash_*.json");
            AssertTrue(reports.Length >= 1, "应生成至少 1 份崩溃报告。");
        }
        finally
        {
            CrashReporter.Shutdown();
            DeleteDir(baseDir);
        }
    }

    /// <summary>CrashReporter.Initialize → ForceReport 生成报告文件。</summary>
    public static void TestForceReportGeneratesReportFile()
    {
        var crashDir = TempDir("crash_force");
        try
        {
            bool generated = false;
            CrashReporter.Initialize(crashDir, "9.9.9");
            CrashReporter.Handler.OnCrashReportGenerated += _ => generated = true;

            CrashReporter.ForceReport(new NullReferenceException("forced crash"));

            AssertTrue(generated, "应触发 OnCrashReportGenerated。");
            AssertTrue(Directory.GetFiles(crashDir, "crash_*.json").Length >= 1, "应生成 JSON 报告。");
            AssertTrue(Directory.GetFiles(crashDir, "crash_*.txt").Length >= 1, "应生成文本报告。");
        }
        finally
        {
            CrashReporter.Shutdown();
            DeleteDir(crashDir);
        }
    }
}
