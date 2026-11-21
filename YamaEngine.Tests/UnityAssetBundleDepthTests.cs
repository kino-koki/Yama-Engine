// UnityAssetBundleDepthTests.cs — Unity5 Asset Bundle 深度验证
// 覆盖：AssetRef 句柄/加载状态、AssetBundle 注册/查找/类型枚举/LoadOrCreate、
//       AssetPackage 跨包解析、PackageVersion 语义。
using System;
using System.Linq;
using YamaEngine.Packages;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests;

public static class UnityAssetBundleDepthTests
{
    private static void AssertFalse(bool c, string msg) => AssertTrue(!c, msg);

    // ══════════════════════════════════════════════════════════════════
    //  一、AssetType 枚举
    // ══════════════════════════════════════════════════════════════════

    /// <summary>AssetType 完整类别与 Unity 对齐。</summary>
    public static void TestAssetTypeValues()
    {
        AssertEqual(0, (int)AssetType.Texture, "Texture=0");
        AssertEqual(2, (int)AssetType.Mesh, "Mesh=2");
        AssertEqual(3, (int)AssetType.Material, "Material=3");
        AssertEqual(4, (int)AssetType.Shader, "Shader=4");
        AssertEqual(5, (int)AssetType.Audio, "Audio=5");
        AssertEqual(6, (int)AssetType.Scene, "Scene=6");
        AssertEqual(7, (int)AssetType.Prefab, "Prefab=7");
        AssertEqual(11, (int)AssetType.Other, "Other=11");
    }

    // ══════════════════════════════════════════════════════════════════
    //  二、AssetRef 句柄状态
    // ══════════════════════════════════════════════════════════════════

    /// <summary>AssetRef 默认未加载；赋 Handle 后 IsLoaded=true。</summary>
    public static void TestAssetRefLoadState()
    {
        var asset = new AssetRef { Name = "Hero", Type = AssetType.Prefab };
        AssertFalse(asset.IsLoaded, "默认未加载");
        AssertTrue(asset.Handle is null, "默认 Handle 为 null");

        asset.Handle = new object();
        AssertTrue(asset.IsLoaded, "赋 Handle 后已加载");
    }

    /// <summary>SourceBundle 自动归属。</summary>
    public static void TestAssetRefSourceBundle()
    {
        var bundle = new AssetBundle { Name = "Heroes" };
        var asset = new AssetRef { Name = "Knight", Type = AssetType.Prefab };
        AssertTrue(asset.SourceBundle is null, "初始无归属");

        bundle.Register(asset);
        AssertEqual("Heroes", asset.SourceBundle, "注册后归属 Heroes");
    }

    // ══════════════════════════════════════════════════════════════════
    //  三、AssetBundle 注册/查找
    // ══════════════════════════════════════════════════════════════════

    /// <summary>Register + Contains + Count。</summary>
    public static void TestBundleRegisterAndCount()
    {
        var bundle = new AssetBundle { Name = "Level1" };
        AssertEqual(0, bundle.Count, "初始空");

        bundle.Register(new AssetRef { Name = "Floor", Type = AssetType.Mesh });
        bundle.Register(new AssetRef { Name = "Wall", Type = AssetType.Mesh });
        bundle.Register(new AssetRef { Name = "Grass", Type = AssetType.Texture });

        AssertEqual(3, bundle.Count, "3 个资源");
        AssertTrue(bundle.Contains("Floor"), "含 Floor");
        AssertTrue(bundle.Contains("floor"), "按名查找大小写不敏感");
        AssertFalse(bundle.Contains("Enemy"), "不含 Enemy");
    }

    /// <summary>Get 按名查找资源。</summary>
    public static void TestBundleGetByName()
    {
        var bundle = new AssetBundle { Name = "Weapons" };
        var sword = new AssetRef { Name = "Sword", Type = AssetType.Prefab };
        bundle.Register(sword);

        var found = bundle.Get("Sword");
        AssertTrue(found is not null, "找到 Sword");
        AssertEqual(sword, found, "返回同一实例");

        AssertTrue(bundle.Get("Axe") is null, "未注册返回 null");
    }

    /// <summary>同名 Register 覆盖（按 Name 唯一）。</summary>
    public static void TestBundleRegisterOverwrite()
    {
        var bundle = new AssetBundle { Name = "UI" };
        var v1 = new AssetRef { Name = "Icon", Type = AssetType.Sprite, Path = "icon_v1.png" };
        var v2 = new AssetRef { Name = "Icon", Type = AssetType.Sprite, Path = "icon_v2.png" };
        bundle.Register(v1);
        bundle.Register(v2);

        AssertEqual(1, bundle.Count, "同名覆盖，仍为 1");
        var found = bundle.Get("Icon");
        AssertEqual("icon_v2.png", found!.Path, "保留最新版本");
    }

    /// <summary>LoadAll 按类型枚举。</summary>
    public static void TestBundleLoadAllByType()
    {
        var bundle = new AssetBundle { Name = "Mixed" };
        bundle.Register(new AssetRef { Name = "M1", Type = AssetType.Mesh });
        bundle.Register(new AssetRef { Name = "M2", Type = AssetType.Mesh });
        bundle.Register(new AssetRef { Name = "T1", Type = AssetType.Texture });
        bundle.Register(new AssetRef { Name = "A1", Type = AssetType.Audio });

        var meshes = bundle.LoadAll(AssetType.Mesh).ToList();
        AssertEqual(2, meshes.Count, "2 个 Mesh");
        AssertTrue(meshes.All(a => a.Type == AssetType.Mesh), "全部为 Mesh");

        var textures = bundle.LoadAll(AssetType.Texture).ToList();
        AssertEqual(1, textures.Count, "1 个 Texture");

        var fonts = bundle.LoadAll(AssetType.Font).ToList();
        AssertEqual(0, fonts.Count, "无 Font");
    }

    /// <summary>Load<T> 仅在 Handle 类型匹配时返回。</summary>
    public static void TestBundleLoadTyped()
    {
        var bundle = new AssetBundle { Name = "Tex" };
        var texRef = new AssetRef { Name = "Hero", Type = AssetType.Texture };
        bundle.Register(texRef);

        // 未加载时返回 null
        AssertTrue(bundle.Load<string>("Hero") is null, "未加载返回 null");

        // 填入字符串句柄后返回
        texRef.Handle = "texture-data";
        var loaded = bundle.Load<string>("Hero");
        AssertEqual("texture-data", loaded, "Load<string> 返回句柄");

        // 类型不匹配返回 null (AssetRef 为引用类型且与 string 句柄不兼容)
        AssertTrue(bundle.Load<AssetRef>("Hero") is null, "类型不匹配返回 null");
    }

    /// <summary>LoadOrCreate：未加载时调用 loader 创建并缓存。</summary>
    public static void TestBundleLoadOrCreate()
    {
        var bundle = new AssetBundle { Name = "Mat" };
        var matRef = new AssetRef { Name = "Red", Type = AssetType.Material };
        bundle.Register(matRef);

        int loaderCalls = 0;
        var created = bundle.LoadOrCreate<object>("Red", _ =>
        {
            loaderCalls++;
            return new object();
        });
        AssertTrue(created is not null, "首次调用 loader 创建");
        AssertEqual(1, loaderCalls, "loader 调用 1 次");

        // 第二次调用应命中缓存
        var cached = bundle.LoadOrCreate<object>("Red", _ =>
        {
            loaderCalls++;
            return new object();
        });
        AssertEqual(created, cached, "第二次返回缓存实例");
        AssertEqual(1, loaderCalls, "loader 仍只调 1 次");
    }

    /// <summary>Clear 清空全部资源。</summary>
    public static void TestBundleClear()
    {
        var bundle = new AssetBundle { Name = "Temp" };
        bundle.Register(new AssetRef { Name = "A", Type = AssetType.Mesh });
        bundle.Register(new AssetRef { Name = "B", Type = AssetType.Mesh });
        AssertEqual(2, bundle.Count, "初始 2 个");

        bundle.Clear();
        AssertEqual(0, bundle.Count, "清空后 0");
        AssertFalse(bundle.Contains("A"), "A 不存在");
        AssertEqual(0, bundle.LoadAll(AssetType.Mesh).Count(), "枚举为空");
    }

    // ══════════════════════════════════════════════════════════════════
    //  四、AssetPackage 跨包解析
    // ══════════════════════════════════════════════════════════════════

    /// <summary>AddBundle + BundleCount + BundleNames。</summary>
    public static void TestPackageAddBundle()
    {
        var pkg = new AssetPackage { Id = "DLC1" };
        AssertEqual(0, pkg.BundleCount, "初始 0 包");

        var b1 = new AssetBundle { Name = "Env" };
        var b2 = new AssetBundle { Name = "Char" };
        pkg.AddBundle(b1);
        pkg.AddBundle(b2);

        AssertEqual(2, pkg.BundleCount, "2 个 Bundle");
        AssertTrue(pkg.BundleNames.Contains("Env"), "含 Env");
        AssertTrue(pkg.BundleNames.Contains("Char"), "含 Char");
    }

    /// <summary>Resolve 按 bundleName 精确解析。</summary>
    public static void TestPackageResolveByBundleName()
    {
        var pkg = new AssetPackage { Id = "Main" };
        var env = new AssetBundle { Name = "Env" };
        var hero = new AssetBundle { Name = "Hero" };
        env.Register(new AssetRef { Name = "Tree", Type = AssetType.Prefab });
        hero.Register(new AssetRef { Name = "Knight", Type = AssetType.Prefab });
        pkg.AddBundle(env);
        pkg.AddBundle(hero);

        var tree = pkg.Resolve("Tree", "Env");
        AssertTrue(tree is not null, "在 Env 包找到 Tree");

        var knight = pkg.Resolve("Knight", "Hero");
        AssertTrue(knight is not null, "在 Hero 包找到 Knight");

        // 错误的 bundleName 解析不到
        AssertTrue(pkg.Resolve("Tree", "Hero") is null, "Hero 包没有 Tree");
    }

    /// <summary>Resolve 全局首匹配（不指定 bundleName）。</summary>
    public static void TestPackageResolveGlobal()
    {
        var pkg = new AssetPackage { Id = "Main" };
        var b1 = new AssetBundle { Name = "B1" };
        var b2 = new AssetBundle { Name = "B2" };
        b1.Register(new AssetRef { Name = "Shared", Type = AssetType.Texture, Path = "b1_shared" });
        b2.Register(new AssetRef { Name = "Shared", Type = AssetType.Texture, Path = "b2_shared" });
        pkg.AddBundle(b1);
        pkg.AddBundle(b2);

        var found = pkg.Resolve("Shared");
        AssertTrue(found is not null, "全局解析找到 Shared");
        AssertTrue(found!.Path == "b1_shared" || found.Path == "b2_shared", "返回其中一版本");
    }

    /// <summary>ResolveAll 聚合所有 Bundle 的同类型资源。</summary>
    public static void TestPackageResolveAll()
    {
        var pkg = new AssetPackage { Id = "Main" };
        var b1 = new AssetBundle { Name = "B1" };
        var b2 = new AssetBundle { Name = "B2" };
        b1.Register(new AssetRef { Name = "M1", Type = AssetType.Mesh });
        b2.Register(new AssetRef { Name = "M2", Type = AssetType.Mesh });
        b2.Register(new AssetRef { Name = "M3", Type = AssetType.Mesh });
        pkg.AddBundle(b1);
        pkg.AddBundle(b2);

        var allMeshes = pkg.ResolveAll(AssetType.Mesh).ToList();
        AssertEqual(3, allMeshes.Count, "跨包共 3 个 Mesh");
    }
}
