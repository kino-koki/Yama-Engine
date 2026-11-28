// UnityBundleVersionDepthTests.cs — Unity5 Bundle 版本管理深度验证
// 覆盖：BundleVersionManager 构造、RecordVersion（含历史保留）、CompareVersions（Added/Removed/Modified）、
//       BundleVersionDiff.HasChanges 语义。
using System;
using System.IO;
using YamaEngine.Build;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests;

public static class UnityBundleVersionDepthTests
{
    private static void AssertFalse(bool c, string msg) => AssertTrue(!c, msg);

    /// <summary>构造 BundleVersionManager 会创建 versionRootPath 目录。</summary>
    public static void TestCtorCreatesDirectory()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "YamaBundleVer_" + Guid.NewGuid().ToString("N"));
        try
        {
            AssertFalse(Directory.Exists(tmp), "目录尚未创建");
            _ = new BundleVersionManager(tmp);
            AssertTrue(Directory.Exists(tmp), "构造后目录存在");
        }
        finally
        {
            if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
        }
    }

    /// <summary>RecordVersion 写入 .version.json 文件。</summary>
    public static void TestRecordVersionWritesFile()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "YamaBundleVer_" + Guid.NewGuid().ToString("N"));
        try
        {
            var mgr = new BundleVersionManager(tmp);
            mgr.RecordVersion("Env", "1.0.0", "hash_a", 1024);

            var file = Path.Combine(tmp, "Env.version.json");
            AssertTrue(File.Exists(file), "记录文件已生成");
            var json = File.ReadAllText(file);
            AssertTrue(json.Contains("Env"), "JSON 含 BundleName");
            AssertTrue(json.Contains("hash_a"), "JSON 含 Hash");
        }
        finally
        {
            if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
        }
    }

    /// <summary>RecordVersion 多次记录同一 bundle，历史保留（≤20 条）。</summary>
    public static void TestRecordVersionKeepsHistory()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "YamaBundleVer_" + Guid.NewGuid().ToString("N"));
        try
        {
            var mgr = new BundleVersionManager(tmp);
            for (int i = 0; i < 25; i++)
                mgr.RecordVersion("UI", $"1.0.{i}", $"hash_{i}", 100 + i);

            var file = Path.Combine(tmp, "UI.version.json");
            AssertTrue(File.Exists(file), "文件存在");
            var json = File.ReadAllText(file);
            // 只保留最近 20 条：最早的 5 条（1.0.0..1.0.4）被裁掉
            AssertFalse(json.Contains("hash_0"), "最旧 hash_0 已裁掉");
            AssertFalse(json.Contains("hash_4"), "hash_4 已裁掉");
            AssertTrue(json.Contains("hash_5"), "hash_5 保留");
            AssertTrue(json.Contains("hash_24"), "最新 hash_24 保留");
        }
        finally
        {
            if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
        }
    }

    /// <summary>RecordVersion 不同 bundle 写入不同文件。</summary>
    public static void TestRecordVersionMultipleBundles()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "YamaBundleVer_" + Guid.NewGuid().ToString("N"));
        try
        {
            var mgr = new BundleVersionManager(tmp);
            mgr.RecordVersion("Env", "1.0.0", "hash_env", 100);
            mgr.RecordVersion("Char", "2.0.0", "hash_char", 200);

            AssertTrue(File.Exists(Path.Combine(tmp, "Env.version.json")), "Env 文件存在");
            AssertTrue(File.Exists(Path.Combine(tmp, "Char.version.json")), "Char 文件存在");
        }
        finally
        {
            if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
        }
    }

    /// <summary>RecordVersion 加载历史：重启 Manager 后状态保持。</summary>
    public static void TestRecordVersionReload()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "YamaBundleVer_" + Guid.NewGuid().ToString("N"));
        try
        {
            var mgr1 = new BundleVersionManager(tmp);
            mgr1.RecordVersion("Env", "3.0.0", "hash_300", 999);

            // 重新构造（模拟重启）
            var mgr2 = new BundleVersionManager(tmp);
            // 旧版本文件仍在
            AssertTrue(File.Exists(Path.Combine(tmp, "Env.version.json")), "重启后文件仍在");
        }
        finally
        {
            if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
        }
    }

    // ════════════════════════════════════════════════════════════════
    //  CompareVersions：Added / Removed / Modified
    // ════════════════════════════════════════════════════════════════

    /// <summary>CompareVersions 空对空无差异。</summary>
    public static void TestCompareVersionsNoDiff()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "YamaBundleVer_" + Guid.NewGuid().ToString("N"));
        try
        {
            var mgr = new BundleVersionManager(tmp);
            var old = new AssetBundleManifest();
            var now = new AssetBundleManifest();

            var diff = mgr.CompareVersions(old, now);
            AssertFalse(diff.HasChanges, "空对空无变化");
            AssertEqual(0, diff.AddedBundles.Count, "无新增");
            AssertEqual(0, diff.RemovedBundles.Count, "无删除");
            AssertEqual(0, diff.ModifiedBundles.Count, "无修改");
        }
        finally { if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true); }
    }

    /// <summary>CompareVersions 新增 Bundle 出现在 Added。</summary>
    public static void TestCompareVersionsAdded()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "YamaBundleVer_" + Guid.NewGuid().ToString("N"));
        try
        {
            var mgr = new BundleVersionManager(tmp);
            var old = new AssetBundleManifest();
            var now = new AssetBundleManifest();
            old.Bundles.Add(new BundleManifestEntry { BundleName = "Env", Hash = "h1" });
            now.Bundles.Add(new BundleManifestEntry { BundleName = "Env", Hash = "h1" });
            now.Bundles.Add(new BundleManifestEntry { BundleName = "Char", Hash = "h2" });

            var diff = mgr.CompareVersions(old, now);
            AssertTrue(diff.HasChanges, "有变化");
            AssertEqual(1, diff.AddedBundles.Count, "新增 1 个");
            AssertEqual("Char", diff.AddedBundles[0], "新增 Char");
            AssertEqual(0, diff.RemovedBundles.Count, "无删除");
            AssertEqual(0, diff.ModifiedBundles.Count, "无修改");
        }
        finally { if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true); }
    }

    /// <summary>CompareVersions 删除 Bundle 出现在 Removed。</summary>
    public static void TestCompareVersionsRemoved()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "YamaBundleVer_" + Guid.NewGuid().ToString("N"));
        try
        {
            var mgr = new BundleVersionManager(tmp);
            var old = new AssetBundleManifest();
            var now = new AssetBundleManifest();
            old.Bundles.Add(new BundleManifestEntry { BundleName = "Env", Hash = "h1" });
            old.Bundles.Add(new BundleManifestEntry { BundleName = "Char", Hash = "h2" });
            now.Bundles.Add(new BundleManifestEntry { BundleName = "Env", Hash = "h1" });

            var diff = mgr.CompareVersions(old, now);
            AssertEqual(1, diff.RemovedBundles.Count, "删除 1 个");
            AssertEqual("Char", diff.RemovedBundles[0], "删除 Char");
        }
        finally { if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true); }
    }

    /// <summary>CompareVersions Hash 变化的 Bundle 出现在 Modified。</summary>
    public static void TestCompareVersionsModified()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "YamaBundleVer_" + Guid.NewGuid().ToString("N"));
        try
        {
            var mgr = new BundleVersionManager(tmp);
            var old = new AssetBundleManifest();
            var now = new AssetBundleManifest();
            old.Bundles.Add(new BundleManifestEntry { BundleName = "Env", Hash = "h_old" });
            now.Bundles.Add(new BundleManifestEntry { BundleName = "Env", Hash = "h_new" });

            var diff = mgr.CompareVersions(old, now);
            AssertEqual(1, diff.ModifiedBundles.Count, "修改 1 个");
            AssertEqual("Env", diff.ModifiedBundles[0], "修改 Env");
            AssertEqual(0, diff.AddedBundles.Count, "无新增");
            AssertEqual(0, diff.RemovedBundles.Count, "无删除");
        }
        finally { if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true); }
    }

    /// <summary>CompareVersions Hash 相同的同名 Bundle 不算 Modified。</summary>
    public static void TestCompareVersionsSameHashNoMod()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "YamaBundleVer_" + Guid.NewGuid().ToString("N"));
        try
        {
            var mgr = new BundleVersionManager(tmp);
            var old = new AssetBundleManifest();
            var now = new AssetBundleManifest();
            old.Bundles.Add(new BundleManifestEntry { BundleName = "Env", Hash = "same" });
            now.Bundles.Add(new BundleManifestEntry { BundleName = "Env", Hash = "same" });

            var diff = mgr.CompareVersions(old, now);
            AssertEqual(0, diff.ModifiedBundles.Count, "Hash 相同不算修改");
            AssertFalse(diff.HasChanges, "整体无变化");
        }
        finally { if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true); }
    }

    /// <summary>CompareVersions 综合：同时含 Added/Removed/Modified。</summary>
    public static void TestCompareVersionsMixed()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "YamaBundleVer_" + Guid.NewGuid().ToString("N"));
        try
        {
            var mgr = new BundleVersionManager(tmp);
            var old = new AssetBundleManifest();
            var now = new AssetBundleManifest();
            old.Bundles.Add(new BundleManifestEntry { BundleName = "A", Hash = "a1" });
            old.Bundles.Add(new BundleManifestEntry { BundleName = "B", Hash = "b1" });
            old.Bundles.Add(new BundleManifestEntry { BundleName = "C", Hash = "c1" });

            now.Bundles.Add(new BundleManifestEntry { BundleName = "B", Hash = "b1" }); // 未变
            now.Bundles.Add(new BundleManifestEntry { BundleName = "C", Hash = "c2" }); // 修改
            now.Bundles.Add(new BundleManifestEntry { BundleName = "D", Hash = "d1" }); // 新增
            // A 被删除

            var diff = mgr.CompareVersions(old, now);
            AssertEqual(1, diff.AddedBundles.Count, "新增 1");
            AssertEqual("D", diff.AddedBundles[0], "新增 D");
            AssertEqual(1, diff.RemovedBundles.Count, "删除 1");
            AssertEqual("A", diff.RemovedBundles[0], "删除 A");
            AssertEqual(1, diff.ModifiedBundles.Count, "修改 1");
            AssertEqual("C", diff.ModifiedBundles[0], "修改 C");
            AssertTrue(diff.HasChanges, "综合有变化");
        }
        finally { if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true); }
    }
}
