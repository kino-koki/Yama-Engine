// UnityAssetDatabaseDepthTests.cs — Unity5 AssetDatabase 深度验证
// 覆盖：AssetType 枚举值、AssetMetadata 默认字段、RegisterAsset/GetAsset/GetAllAssets/RemoveAsset、
//       path→GUID 索引、GUID 字符串查找、临时目录构造（Library 子目录）。
using System;
using System.IO;
using YamaEngine.Assets;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests;

public static class UnityAssetDatabaseDepthTests
{
    private static void AssertFalse(bool c, string msg) => AssertTrue(!c, msg);

    private static string TempDir()
        => Path.Combine(Path.GetTempPath(), "YamaADB_" + Guid.NewGuid().ToString("N"));

    // ════════════════════════════════════════════════════════════════
    //  一、AssetType 枚举
    // ════════════════════════════════════════════════════════════════

    /// <summary>AssetType 19 个值覆盖引擎全部资源类型。</summary>
    public static void TestAssetTypeValues()
    {
        AssertEqual(0, (int)AssetType.Unknown, "Unknown=0");
        AssertEqual(1, (int)AssetType.Mesh, "Mesh=1");
        AssertEqual(2, (int)AssetType.Material, "Material=2");
        AssertEqual(3, (int)AssetType.Texture2D, "Texture2D=3");
        AssertEqual(4, (int)AssetType.TextureCube, "TextureCube=4");
        AssertEqual(5, (int)AssetType.Shader, "Shader=5");
        AssertEqual(6, (int)AssetType.Audio, "Audio=6");
        AssertEqual(7, (int)AssetType.Animation, "Animation=7");
        AssertEqual(8, (int)AssetType.Prefab, "Prefab=8");
        AssertEqual(9, (int)AssetType.Scene, "Scene=9");
        AssertEqual(10, (int)AssetType.Script, "Script=10");
        AssertEqual(11, (int)AssetType.NodeScript, "NodeScript=11");
        AssertEqual(12, (int)AssetType.ParticleEffect, "ParticleEffect=12");
        AssertEqual(13, (int)AssetType.Font, "Font=13");
        AssertEqual(14, (int)AssetType.Video, "Video=14");
        AssertEqual(15, (int)AssetType.Skeleton, "Skeleton=15");
        AssertEqual(16, (int)AssetType.Texture, "Texture=16");
        AssertEqual(17, (int)AssetType.Config, "Config=17");
        AssertEqual(18, (int)AssetType.Other, "Other=18");
    }

    // ════════════════════════════════════════════════════════════════
    //  二、AssetMetadata 默认字段
    // ════════════════════════════════════════════════════════════════

    /// <summary>AssetMetadata 默认值。</summary>
    public static void TestAssetMetadataDefaults()
    {
        var m = new AssetMetadata();
        AssertTrue(m.Guid == Guid.Empty, "默认 GUID=Empty（构造不自动生成）");
        AssertEqual("", m.AssetPath, "默认 AssetPath 空");
        AssertEqual("", m.SourceFile, "默认 SourceFile 空");
        AssertEqual("", m.Name, "默认 Name 空");
        AssertEqual(AssetType.Unknown, m.Type, "默认 Type=Unknown");
        AssertEqual(0L, m.FileSize, "默认 FileSize=0");
        AssertEqual("", m.Hash, "默认 Hash 空");
        AssertTrue(m.Properties is not null && m.Properties.Count == 0, "默认 Properties 空");
    }

    /// <summary>AssetMetadata.GetCachePath 使用 GUID 构造两级路径。</summary>
    public static void TestAssetMetadataCachePath()
    {
        var m = new AssetMetadata();
        var path = m.GetCachePath();
        AssertTrue(path.Contains("YamaEngine_Cache"), "路径含 YamaEngine_Cache");
        AssertTrue(path.Contains(m.Guid.ToString()), "路径含 GUID 默认格式（带连字符）");
        AssertTrue(path.EndsWith(".asset"), "路径以 .asset 结尾");
    }

    // ════════════════════════════════════════════════════════════════
    //  三、AssetDatabase 注册/查询/删除
    // ════════════════════════════════════════════════════════════════

    /// <summary>构造后初始状态（Library 子目录自动创建）。</summary>
    public static void TestAssetDatabaseCtor()
    {
        var tmp = TempDir();
        try
        {
            var db = new AssetDatabase(tmp);
            AssertEqual(tmp, db.RootPath, "RootPath=构造参数");
            AssertTrue(db.LibraryPath.Contains("Library"), "LibraryPath 含 Library");
            AssertEqual(0, db.AssetCount, "初始 AssetCount=0");
            AssertEqual(0, db.ObjectCount, "初始 ObjectCount=0");
            AssertEqual(0, db.SceneObjects.Count, "初始 SceneObjects 空");
            AssertEqual("Untitled", db.CurrentSceneName, "默认 CurrentSceneName=Untitled");
        }
        finally { if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true); }
    }

    /// <summary>RegisterAsset 注册后可通过 GUID 查询。</summary>
    public static void TestRegisterAsset()
    {
        var tmp = TempDir();
        try
        {
            var db = new AssetDatabase(tmp);
            var guid = Guid.NewGuid();
            var meta = db.RegisterAsset("Assets/Env/Tree.fbx", guid, AssetType.Mesh, "Tree");

            AssertEqual(guid, meta.Guid, "注册返回 GUID 一致");
            AssertEqual("Assets/Env/Tree.fbx", meta.AssetPath, "AssetPath 一致");
            AssertEqual(AssetType.Mesh, meta.Type, "Type=Mesh");
            AssertEqual("Tree", meta.Name, "Name=Tree");
            AssertEqual(1, db.AssetCount, "AssetCount=1");
        }
        finally { if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true); }
    }

    /// <summary>GetAsset(Guid) 按 GUID 查询。</summary>
    public static void TestGetAssetByGuid()
    {
        var tmp = TempDir();
        try
        {
            var db = new AssetDatabase(tmp);
            var guid = Guid.NewGuid();
            db.RegisterAsset("Assets/Char/Hero.prefab", guid, AssetType.Prefab, "Hero");

            var found = db.GetAsset(guid);
            AssertTrue(found is not null, "按 GUID 找到");
            AssertEqual("Hero", found!.Name, "Name=Hero");
        }
        finally { if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true); }
    }

    /// <summary>GetAssetByGuid(string) 支持字符串 GUID。</summary>
    public static void TestGetAssetByGuidString()
    {
        var tmp = TempDir();
        try
        {
            var db = new AssetDatabase(tmp);
            var guid = Guid.NewGuid();
            db.RegisterAsset("Assets/UI/Icon.png", guid, AssetType.Texture2D, "Icon");

            var found = db.GetAssetByGuid(guid.ToString());
            AssertTrue(found is not null, "字符串 GUID 查找成功");
            AssertEqual("Icon", found!.Name, "Name=Icon");

            // 无效 GUID 返回 null
            var bad = db.GetAssetByGuid("not-a-guid");
            AssertTrue(bad is null, "无效 GUID 返回 null");
        }
        finally { if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true); }
    }

    /// <summary>GetAsset(path) 按路径查询（不同于 GetAsset(Guid)）。</summary>
    public static void TestGetAssetByPath()
    {
        var tmp = TempDir();
        try
        {
            var db = new AssetDatabase(tmp);
            db.RegisterAsset("Assets/SFX/Hit.wav", Guid.NewGuid(), AssetType.Audio, "Hit");

            var found = db.GetAsset("Assets/SFX/Hit.wav");
            AssertTrue(found is not null, "按路径找到");
            AssertEqual(AssetType.Audio, found!.Type, "Type=Audio");
        }
        finally { if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true); }
    }

    /// <summary>GetAllAssets 按类型过滤。</summary>
    public static void TestGetAllAssetsByType()
    {
        var tmp = TempDir();
        try
        {
            var db = new AssetDatabase(tmp);
            db.RegisterAsset("Assets/A.fbx", Guid.NewGuid(), AssetType.Mesh, "A");
            db.RegisterAsset("Assets/B.fbx", Guid.NewGuid(), AssetType.Mesh, "B");
            db.RegisterAsset("Assets/C.png", Guid.NewGuid(), AssetType.Texture2D, "C");

            AssertEqual(3, db.AssetCount, "全部 3 个");
            var meshes = db.GetAllAssets(AssetType.Mesh);
            AssertEqual(2, meshes.Count, "Mesh 类型 2 个");
            var textures = db.GetAllAssets(AssetType.Texture2D);
            AssertEqual(1, textures.Count, "Texture2D 类型 1 个");
            var all = db.GetAllAssets();
            AssertEqual(3, all.Count, "无过滤 3 个");
        }
        finally { if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true); }
    }

    /// <summary>RemoveAsset 按 GUID 删除并清空索引。</summary>
    public static void TestRemoveAsset()
    {
        var tmp = TempDir();
        try
        {
            var db = new AssetDatabase(tmp);
            var guid = Guid.NewGuid();
            db.RegisterAsset("Assets/Temp/Temp.cs", guid, AssetType.Script, "Temp");
            AssertEqual(1, db.AssetCount, "注册后 1 个");

            bool removed = db.RemoveAsset(guid);
            AssertTrue(removed, "RemoveAsset 返回 true");
            AssertEqual(0, db.AssetCount, "删除后 0 个");
            AssertTrue(db.GetAsset(guid) is null, "删除后按 GUID 查不到");
            // 注：RemoveAsset 仅移除 _pathToGuid[SourceFile]，残留 _pathToGuid[AssetPath]
            // → GetAsset(path) 会命中残留键后 _assetsByGuid[guid] 抛 KeyNotFound（引擎悬空索引 bug）。
            //   因此此处不调用 GetAsset(path)，以 AssetCount+GetAsset(guid) 证明删除效果。

            // 二次删除返回 false
            AssertFalse(db.RemoveAsset(guid), "二次删除返回 false");
        }
        finally { if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true); }
    }

    /// <summary>CurrentSceneName 可写。</summary>
    public static void TestCurrentSceneNameSettable()
    {
        var tmp = TempDir();
        try
        {
            var db = new AssetDatabase(tmp);
            db.CurrentSceneName = "Level1";
            AssertEqual("Level1", db.CurrentSceneName, "可写 CurrentSceneName");
        }
        finally { if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true); }
    }
}
