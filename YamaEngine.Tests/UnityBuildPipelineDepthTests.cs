// UnityBuildPipelineDepthTests.cs — Unity5 Build Pipeline / AssetBundleManifest 深度验证
// 覆盖：BuildTarget/BuildType/ScriptBackend/CodeOptimization/CompressionAlgorithm/BuildStatus 枚举、
//       BuildConfiguration 默认值、AssetBundleManifest JSON 往返、依赖链、BundleManifestEntry。
using System;
using System.Linq;
using YamaEngine.Build;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests;

public static class UnityBuildPipelineDepthTests
{
    private static void AssertFalse(bool c, string msg) => AssertTrue(!c, msg);

    // ════════════════════════════════════════════════════════════════
    //  一、BuildTarget 枚举
    // ════════════════════════════════════════════════════════════════

    /// <summary>BuildTarget 覆盖主流平台：桌面/移动/主机/Web。</summary>
    public static void TestBuildTargetValues()
    {
        AssertEqual(0, (int)BuildTarget.Windows, "Windows=0");
        AssertEqual(1, (int)BuildTarget.Linux, "Linux=1");
        AssertEqual(2, (int)BuildTarget.Mac, "Mac=2");
        AssertEqual(3, (int)BuildTarget.Android, "Android=3");
        AssertEqual(4, (int)BuildTarget.iOS, "iOS=4");
        AssertEqual(5, (int)BuildTarget.PS5, "PS5=5");
        AssertEqual(6, (int)BuildTarget.Xbox, "Xbox=6");
        AssertEqual(7, (int)BuildTarget.Switch, "Switch=7");
        AssertEqual(8, (int)BuildTarget.WebGL, "WebGL=8");
        AssertEqual(9, (int)BuildTarget.Windows64, "Windows64=9");
        AssertEqual(10, (int)BuildTarget.Mobile, "Mobile=10");
    }

    /// <summary>BuildType 三态：Debug/Release/Shipping。</summary>
    public static void TestBuildTypeValues()
    {
        AssertEqual(0, (int)BuildType.Debug, "Debug=0");
        AssertEqual(1, (int)BuildType.Release, "Release=1");
        AssertEqual(2, (int)BuildType.Shipping, "Shipping=2");
    }

    /// <summary>ScriptBackend 两态：Mono/IL2CPP。</summary>
    public static void TestScriptBackendValues()
    {
        AssertEqual(0, (int)ScriptBackend.Mono, "Mono=0");
        AssertEqual(1, (int)ScriptBackend.IL2CPP, "IL2CPP=1");
    }

    /// <summary>CodeOptimization 四级：None/Speed/Size/Full。</summary>
    public static void TestCodeOptimizationValues()
    {
        AssertEqual(0, (int)CodeOptimization.None, "None=0");
        AssertEqual(1, (int)CodeOptimization.Speed, "Speed=1");
        AssertEqual(2, (int)CodeOptimization.Size, "Size=2");
        AssertEqual(3, (int)CodeOptimization.Full, "Full=3");
    }

    /// <summary>CompressionAlgorithm 四种：None/LZ4/LZMA/Zlib。</summary>
    public static void TestCompressionAlgorithmValues()
    {
        AssertEqual(0, (int)CompressionAlgorithm.None, "None=0");
        AssertEqual(1, (int)CompressionAlgorithm.LZ4, "LZ4=1");
        AssertEqual(2, (int)CompressionAlgorithm.LZMA, "LZMA=2");
        AssertEqual(3, (int)CompressionAlgorithm.Zlib, "Zlib=3");
    }

    /// <summary>BuildStatus 五态：Idle/Running/Success/Failed/Cancelled。</summary>
    public static void TestBuildStatusValues()
    {
        AssertEqual(0, (int)BuildStatus.Idle, "Idle=0");
        AssertEqual(1, (int)BuildStatus.Running, "Running=1");
        AssertEqual(2, (int)BuildStatus.Success, "Success=2");
        AssertEqual(3, (int)BuildStatus.Failed, "Failed=3");
        AssertEqual(4, (int)BuildStatus.Cancelled, "Cancelled=4");
    }

    // ════════════════════════════════════════════════════════════════
    //  二、BuildConfiguration 默认值
    // ════════════════════════════════════════════════════════════════

    /// <summary>BuildConfiguration.Default 默认字段。</summary>
    public static void TestBuildConfigurationDefaults()
    {
        var cfg = BuildConfiguration.Default;
        AssertEqual("", cfg.ProjectPath, "默认 ProjectPath 空");
        AssertEqual("", cfg.OutputPath, "默认 OutputPath 空");
        AssertEqual("GameBuild", cfg.BuildName, "默认 BuildName=GameBuild");
        AssertEqual(BuildTarget.Windows64, cfg.Target, "默认 Target=Windows64");
        AssertEqual(0, cfg.StartSceneIndex, "默认 StartSceneIndex=0");
        AssertTrue(cfg.IncludeScripts, "默认 IncludeScripts=true");
        AssertFalse(cfg.IncludeSource, "默认 IncludeSource=false");
        AssertFalse(cfg.DevelopmentBuild, "默认 DevelopmentBuild=false");
        AssertTrue(cfg.CompressAssets, "默认 CompressAssets=true");
        AssertFalse(cfg.CreateInstaller, "默认 CreateInstaller=false");
        AssertEqual("1.0.0", cfg.Version, "默认 Version=1.0.0");
        AssertEqual("Yama Games", cfg.CompanyName, "默认 CompanyName=Yama Games");
        AssertEqual("Game", cfg.ProductName, "默认 ProductName=Game");
        AssertTrue(cfg.WindowSize == (1280, 720), "默认 WindowSize=1280x720");
        AssertFalse(cfg.Fullscreen, "默认 Fullscreen=false");
        AssertTrue(cfg.VSync, "默认 VSync=true");
        AssertEqual(1, cfg.BuildNumber, "默认 BuildNumber=1");
    }

    /// <summary>BuildConfiguration 可自定义字段。</summary>
    public static void TestBuildConfigurationCustom()
    {
        var cfg = new BuildConfiguration
        {
            BuildName = "MyGame",
            Target = BuildTarget.Android,
            Version = "2.5.1",
            CompanyName = "Acme",
            ProductName = "Prod",
            DevelopmentBuild = true,
            VSync = false,
            Fullscreen = true,
            BuildNumber = 42
        };
        AssertEqual("MyGame", cfg.BuildName, "BuildName 自定义");
        AssertEqual(BuildTarget.Android, cfg.Target, "Target=Android");
        AssertEqual("2.5.1", cfg.Version, "Version 自定义");
        AssertEqual("Acme", cfg.CompanyName, "CompanyName 自定义");
        AssertTrue(cfg.DevelopmentBuild, "DevelopmentBuild=true");
        AssertFalse(cfg.VSync, "VSync=false");
        AssertTrue(cfg.Fullscreen, "Fullscreen=true");
        AssertEqual(42, cfg.BuildNumber, "BuildNumber=42");
    }

    // ════════════════════════════════════════════════════════════════
    //  三、AssetBundleManifest JSON 往返
    // ════════════════════════════════════════════════════════════════

    /// <summary>AssetBundleManifest 默认字段：Version=1/AppVersion=1.0.0。</summary>
    public static void TestManifestDefaults()
    {
        var m = new AssetBundleManifest();
        AssertEqual(1, m.Version, "默认 Version=1");
        AssertEqual("1.0.0", m.AppVersion, "默认 AppVersion=1.0.0");
        AssertEqual(0, m.BundleCount, "默认 BundleCount=0");
        AssertEqual(0L, m.TotalSizeBytes, "默认 TotalSizeBytes=0");
        AssertTrue(m.Bundles is not null, "Bundles 列表非 null");
    }

    /// <summary>AssetBundleManifest 添加 Bundle 后 BundleCount/TotalSizeBytes 计算。</summary>
    public static void TestManifestComputed()
    {
        var m = new AssetBundleManifest();
        m.Bundles.Add(new BundleManifestEntry
        {
            BundleName = "Env",
            SizeBytes = 1024,
            Hash = "abc"
        });
        m.Bundles.Add(new BundleManifestEntry
        {
            BundleName = "Char",
            SizeBytes = 2048,
            Hash = "def"
        });
        AssertEqual(2, m.BundleCount, "BundleCount=2");
        AssertEqual(3072L, m.TotalSizeBytes, "TotalSizeBytes=1024+2048=3072");
    }

    /// <summary>AssetBundleManifest ToJson/FromJson 往返保持字段。</summary>
    public static void TestManifestJsonRoundTrip()
    {
        var m = new AssetBundleManifest { Version = 2, AppVersion = "2.3.4" };
        m.Bundles.Add(new BundleManifestEntry
        {
            BundleName = "Env",
            Hash = "hash123",
            SizeBytes = 4096,
            Compression = BundleCompression.LZ4,
            AssetPaths = { "Assets/Env/Tree.prefab", "Assets/Env/Rock.prefab" },
            Dependencies = { "Common" }
        });

        var json = m.ToJson();
        AssertTrue(json.Contains("Env"), "JSON 含 Env");
        AssertTrue(json.Contains("hash123"), "JSON 含 hash");

        var loaded = AssetBundleManifest.FromJson(json);
        AssertEqual(2, loaded.Version, "往返 Version=2");
        AssertEqual("2.3.4", loaded.AppVersion, "往返 AppVersion");
        AssertEqual(1, loaded.BundleCount, "往返 BundleCount=1");
        var env = loaded.Bundles[0];
        AssertEqual("Env", env.BundleName, "往返 BundleName");
        AssertEqual("hash123", env.Hash, "往返 Hash");
        AssertEqual(4096L, env.SizeBytes, "往返 SizeBytes");
        AssertEqual(BundleCompression.LZ4, env.Compression, "往返 Compression=LZ4");
        AssertEqual(2, env.AssetPaths.Count, "往返 2 个 AssetPaths");
        AssertEqual(1, env.Dependencies.Count, "往返 1 个依赖");
    }

    /// <summary>AssetBundleManifest Save/Load 文件往返。</summary>
    public static void TestManifestSaveLoad()
    {
        var tmp = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "YamaTests_" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var m = new AssetBundleManifest { Version = 3, AppVersion = "9.9.9" };
            m.Bundles.Add(new BundleManifestEntry { BundleName = "UI", SizeBytes = 512 });

            m.Save(tmp);
            AssertTrue(System.IO.File.Exists(tmp), "文件已保存");

            var loaded = AssetBundleManifest.Load(tmp);
            AssertEqual(3, loaded.Version, "Load Version=3");
            AssertEqual("9.9.9", loaded.AppVersion, "Load AppVersion");
            AssertEqual(1, loaded.BundleCount, "Load BundleCount=1");
            AssertEqual("UI", loaded.Bundles[0].BundleName, "Load BundleName=UI");
        }
        finally
        {
            if (System.IO.File.Exists(tmp)) System.IO.File.Delete(tmp);
        }
    }

    /// <summary>GetDependencyChain 递归依赖（A→B→C）。</summary>
    public static void TestManifestDependencyChain()
    {
        var m = new AssetBundleManifest();
        m.Bundles.Add(new BundleManifestEntry { BundleName = "A", Dependencies = { "B" } });
        m.Bundles.Add(new BundleManifestEntry { BundleName = "B", Dependencies = { "C" } });
        m.Bundles.Add(new BundleManifestEntry { BundleName = "C" });

        var chain = m.GetDependencyChain("A");
        AssertEqual(3, chain.Count, "A 的依赖链 3 个（A,B,C）");
        AssertTrue(chain.Contains("A"), "链含 A");
        AssertTrue(chain.Contains("B"), "链含 B");
        AssertTrue(chain.Contains("C"), "链含 C");
    }

    /// <summary>GetDependencyChain 未知 bundle 返回 [bundleName]（自身）。</summary>
    public static void TestManifestDependencyChainUnknown()
    {
        var m = new AssetBundleManifest();
        var chain = m.GetDependencyChain("Ghost");
        // 实现先 visited.Add(bundleName)，未知入口时返回 [bundleName] 自身
        AssertEqual(1, chain.Count, "未知 bundle 依赖链含自身");
        AssertEqual("Ghost", chain[0], "链含 Ghost");
    }

    /// <summary>BundleBuildEntry 默认字段。</summary>
    public static void TestBundleBuildEntryDefaults()
    {
        var e = new BundleBuildEntry();
        AssertEqual("", e.AssetPath, "默认 AssetPath 空");
        AssertEqual("", e.BundleName, "默认 BundleName 空");
        AssertTrue(e.AssetAlias is null, "默认 AssetAlias null");
        AssertTrue(e.Tags is not null && e.Tags.Count == 0, "默认 Tags 空");
        AssertEqual("Unknown", e.AssetType, "默认 AssetType=Unknown");
        AssertFalse(e.IsDependency, "默认非依赖");
    }

    /// <summary>BundleCompression 枚举三态。</summary>
    public static void TestBundleCompressionValues()
    {
        AssertEqual(0, (int)BundleCompression.None, "None=0");
        AssertEqual(1, (int)BundleCompression.LZ4, "LZ4=1");
        AssertEqual(2, (int)BundleCompression.ZSTD, "ZSTD=2");
    }

    /// <summary>BundleRuleType 枚举覆盖 6 种打包规则。</summary>
    public static void TestBundleRuleTypeValues()
    {
        AssertEqual(0, (int)BundleRuleType.ByDirectory, "ByDirectory=0");
        AssertEqual(1, (int)BundleRuleType.ByTag, "ByTag=1");
        AssertEqual(2, (int)BundleRuleType.ByType, "ByType=2");
        AssertEqual(3, (int)BundleRuleType.Manual, "Manual=3");
        AssertEqual(4, (int)BundleRuleType.PerAsset, "PerAsset=4");
        AssertEqual(5, (int)BundleRuleType.SingleBundle, "SingleBundle=5");
    }

    /// <summary>BundleLoadState 五态。</summary>
    public static void TestBundleLoadStateValues()
    {
        AssertEqual(0, (int)BundleLoadState.Unloaded, "Unloaded=0");
        AssertEqual(1, (int)BundleLoadState.Loading, "Loading=1");
        AssertEqual(2, (int)BundleLoadState.Loaded, "Loaded=2");
        AssertEqual(3, (int)BundleLoadState.Unloading, "Unloading=3");
        AssertEqual(4, (int)BundleLoadState.LoadFailed, "LoadFailed=4");
    }

    /// <summary>LoadedBundle AddRef/Release 引用计数（归零时返回 true）。</summary>
    public static void TestLoadedBundleRefCount()
    {
        var b = new LoadedBundle { BundleName = "Test", FilePath = "/tmp/test" };
        AssertEqual(0, b.RefCount, "初始 RefCount=0");

        b.AddRef();
        b.AddRef();
        AssertEqual(2, b.RefCount, "加 2 次后 RefCount=2");

        bool zero1 = b.Release();
        AssertFalse(zero1, "第一次 Release 未归零");
        AssertEqual(1, b.RefCount, "Release 后 RefCount=1");

        bool zero2 = b.Release();
        AssertTrue(zero2, "第二次 Release 归零返回 true");
        AssertEqual(0, b.RefCount, "归零后 RefCount=0");
    }
}
