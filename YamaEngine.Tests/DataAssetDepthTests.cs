// DataAssetDepthTests.cs — Unity5 ScriptableObject/DataAsset 深度验证
// 覆盖:
//   * DataAsset 基类: DisplayName/Categories attribute 读取 + fallback、Load/Unload 事件
//   * DataAsset<T>.DeepCopy: JSON round-trip 独立副本
//   * DataAssetReference<T>: IsValid / Resolve 懒加载 / ResetCache
//   * DataAssetRegistry: Register/Unregister/GetById/GetByName/GetAllOfType/GetByTag/GetByCategory/重复ID抛异常
//   * DataAssetManager: RegisterAsset/GetAsset/AddReference/ReleaseReference(归零自动Unload)/SaveToFile/LoadFromFile/ReloadAsset

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using YamaEngine.DataSystem;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests
{
    // ── 测试用资产类型 ──────────────────────────────────────────────────────
    [DataAssetDisplayName("Sword Data")]
    [DataAssetCategory("Weapons/Melee")]
    [DataAssetCategory("Items")]
    public class TestSwordData : DataAsset<TestSwordData>
    {
        public string WeaponName { get; set; } = "IronSword";
        public float Damage { get; set; } = 42f;
        public List<string> Enchantments { get; set; } = new();
    }

    public class PlainData : DataAsset<PlainData>
    {
        public int Level { get; set; } = 5;
        public string Description { get; set; } = "plain";
    }

    public class BaseCreatureData : DataAsset { }
    public class DragonData : BaseCreatureData
    {
        public int Wings { get; set; } = 2;
    }

    public static class DataAssetDepthTests
    {
        // =========================================================
        // 1. DataAsset 基类 — DisplayName / Categories
        // =========================================================

        /// <summary>GetDisplayName 读 [DataAssetDisplayName]，无属性则回退类名。</summary>
        public static void TestDisplayNameAttributeAndFallback()
        {
            var sword = new TestSwordData();
            AssertEqual("Sword Data", sword.GetDisplayName(), "TestSwordData 应读取 attribute");

            var plain = new PlainData();
            AssertEqual("PlainData", plain.GetDisplayName(), "PlainData 无 attribute 应回退类名");
        }

        /// <summary>GetCategories 读多个 [DataAssetCategory]，无属性则回退 Category 字段。</summary>
        public static void TestCategoriesMultiAndFallback()
        {
            var sword = new TestSwordData();
            var cats = sword.GetCategories();
            AssertEqual(2, cats.Length, "TestSwordData 应有 2 个分类");
            AssertTrue(cats.Contains("Weapons/Melee"), "应包含 Weapons/Melee");
            AssertTrue(cats.Contains("Items"), "应包含 Items");

            var plain = new PlainData();
            AssertEqual("Misc", plain.Category, "默认 Category 应为 Misc");
            var plainCats = plain.GetCategories();
            AssertEqual(1, plainCats.Length, "无 attribute 应回退到 Category 字段");
            AssertEqual("Misc", plainCats[0], "回退值应为 Misc");
        }

        /// <summary>AssetId 默认非空 Guid；AssetName 默认值。</summary>
        public static void TestDefaults()
        {
            var asset = new TestSwordData();
            AssertTrue(asset.AssetId != Guid.Empty, "AssetId 默认应为非空 Guid");
            AssertEqual("UnnamedDataAsset", asset.AssetName, "AssetName 默认应为 UnnamedDataAsset");
            AssertTrue(!asset.IsLoaded, "IsLoaded 默认 false");
            AssertTrue(asset.Tags != null && asset.Tags.Count == 0, "Tags 默认空集合");
        }

        // =========================================================
        // 2. Load / Unload 事件
        // =========================================================

        /// <summary>Load 切换 IsLoaded + 触发 OnLoaded；Unload 反之。</summary>
        public static void TestLoadUnloadEvents()
        {
            var asset = new TestSwordData();
            bool loadedFired = false, unloadedFired = false;
            asset.OnLoaded += a => loadedFired = true;
            asset.OnUnloaded += a => unloadedFired = true;

            AssertTrue(!asset.IsLoaded, "初始未加载");
            asset.Load();
            AssertTrue(asset.IsLoaded, "Load 后 IsLoaded=true");
            AssertTrue(loadedFired, "OnLoaded 应触发");
            AssertTrue(!unloadedFired, "Unload 前不应触发 OnUnloaded");

            asset.Unload();
            AssertTrue(!asset.IsLoaded, "Unload 后 IsLoaded=false");
            AssertTrue(unloadedFired, "OnUnloaded 应触发");
        }

        // =========================================================
        // 3. DeepCopy 独立副本
        // =========================================================

        /// <summary>DeepCopy 创建独立副本，修改原不影响副本。</summary>
        public static void TestDeepCopyIndependence()
        {
            var orig = new TestSwordData
            {
                AssetName = "Excalibur",
                WeaponName = "HolyBlade",
                Damage = 99f
            };
            orig.Enchantments.Add("Fire");
            orig.Enchantments.Add("Holy");

            var copy = orig.DeepCopy();
            AssertTrue(copy != orig, "副本应为不同实例");
            AssertEqual(orig.AssetId, copy.AssetId, "DeepCopy 保留同一 AssetId");
            AssertEqual("HolyBlade", copy.WeaponName, "字段值应一致");
            AssertEqual(99f, copy.Damage, "Damage 应一致");
            AssertEqual(2, copy.Enchantments.Count, "List 字段应复制");

            // 修改原不影响副本
            orig.WeaponName = "Rusted";
            orig.Damage = 1f;
            orig.Enchantments.Clear();
            AssertEqual("HolyBlade", copy.WeaponName, "修改原后副本 WeaponName 不变");
            AssertEqual(99f, copy.Damage, "修改原后副本 Damage 不变");
            AssertEqual(2, copy.Enchantments.Count, "修改原后副本 Enchantments 不变");
        }

        // =========================================================
        // 4. DataAssetReference<T>
        // =========================================================

        /// <summary>TargetAssetId=Guid.Empty → IsValid=false；有效 ID → true。</summary>
        public static void TestReferenceIsValid()
        {
            var empty = new DataAssetReference<TestSwordData>();
            AssertTrue(!empty.IsValid, "空 ID 应 IsValid=false");
            AssertTrue(!(bool)empty, "隐式转 bool 应 false");

            var valid = new DataAssetReference<TestSwordData>
            {
                TargetAssetId = Guid.NewGuid()
            };
            AssertTrue(valid.IsValid, "有效 ID 应 IsValid=true");
            AssertTrue((bool)valid, "隐式转 bool 应 true");
        }

        /// <summary>Resolve 懒加载：未注册返回 null；注册后返回实例；ResetCache 后可重新 Resolve。</summary>
        public static void TestReferenceResolveLifecycle()
        {
            DataAssetManager.Initialize();
            var mgr = DataAssetManager.Instance!;

            var sword = new TestSwordData { AssetName = "RefTarget" };
            var reference = new DataAssetReference<TestSwordData>
            {
                TargetAssetId = sword.AssetId,
                EditorHintPath = "Assets/Data/Sword.asset"
            };

            // 未注册：Resolve 返回 null
            var resolved = reference.Resolve();
            AssertTrue(resolved == null, "未注册时 Resolve 应返回 null");

            // 注册后：Resolve 返回实例
            mgr.RegisterAsset(sword);
            var resolved2 = reference.Resolve();
            AssertTrue(resolved2 != null, "注册后 Resolve 应返回实例");
            AssertEqual(sword.AssetId, resolved2.AssetId, "Resolve 应返回注册的资产");

            // 缓存：第二次 Resolve 仍返回同一实例
            var resolved3 = reference.Resolve();
            AssertTrue(ReferenceEquals(resolved2, resolved3), "二次 Resolve 应返回缓存实例");

            // ResetCache + Unregister 后 Resolve 再返回 null
            reference.ResetCache();
            mgr.UnregisterAsset(sword);
            var resolved4 = reference.Resolve();
            AssertTrue(resolved4 == null, "ResetCache+Unregister 后 Resolve 应返回 null");

            mgr.Shutdown();
        }

        // =========================================================
        // 5. DataAssetRegistry
        // =========================================================

        /// <summary>重复 ID Register 抛 InvalidOperationException。</summary>
        public static void TestRegistryDuplicateIdThrows()
        {
            var registry = new DataAssetRegistry();
            var id = Guid.NewGuid();
            var a = new TestSwordData { AssetId = id, AssetName = "A" };
            var b = new TestSwordData { AssetId = id, AssetName = "B" };
            registry.Register(a);

            bool threw = false;
            try { registry.Register(b); }
            catch (InvalidOperationException) { threw = true; }
            AssertTrue(threw, "重复 ID 注册应抛 InvalidOperationException");
        }

        /// <summary>GetById/GetByName/GetAllOfType/GetByTag/GetByCategory 查找。</summary>
        public static void TestRegistryLookup()
        {
            var registry = new DataAssetRegistry();
            var sword = new TestSwordData { AssetName = "Excalibur" };
            sword.Tags.Add("legendary");
            sword.Tags.Add("weapon");
            var sword2 = new TestSwordData { AssetName = "Knife" };
            sword2.Tags.Add("weapon");
            var plain = new PlainData { AssetName = "Config" };

            registry.Register(sword);
            registry.Register(sword2);
            registry.Register(plain);

            // GetById
            var found = registry.GetById(sword.AssetId);
            AssertTrue(found != null, "GetById 应找到资产");
            AssertEqual("Excalibur", found.AssetName, "GetById 应返回正确资产");
            AssertTrue(registry.GetById(Guid.NewGuid()) == null, "GetById 未命中返回 null");

            // GetById<T>
            var typed = registry.GetById<TestSwordData>(sword.AssetId);
            AssertTrue(typed != null, "GetById<T> 应返回类型化资产");
            AssertTrue(registry.GetById<PlainData>(sword.AssetId) == null, "GetById<T> 类型不匹配返回 null");

            // GetByName（大小写不敏感）
            AssertTrue(registry.GetByName("excalibur") != null, "GetByName 大小写不敏感");
            AssertTrue(registry.GetByName("nonexist") == null, "GetByName 未命中返回 null");

            // GetAllOfType
            var allSwords = registry.GetAllOfType<TestSwordData>();
            AssertEqual(2, allSwords.Count, "应找到 2 个 TestSwordData");
            var allPlain = registry.GetAllOfType<PlainData>();
            AssertEqual(1, allPlain.Count, "应找到 1 个 PlainData");

            // GetByTag
            var legendary = registry.GetByTag("legendary");
            AssertEqual(1, legendary.Count, "legendary 标签应只命中 1 个");
            var weapons = registry.GetByTag("weapon");
            AssertEqual(2, weapons.Count, "weapon 标签应命中 2 个");

            // GetByCategory
            var inWeapons = registry.GetByCategory("Weapons/Melee");
            AssertEqual(2, inWeapons.Count, "Weapons/Melee 分类应有 2 个 sword");

            // Count
            AssertEqual(3, registry.Count, "注册表应有 3 个资产");
        }

        /// <summary>Unregister 后各索引清除，Count 递减。</summary>
        public static void TestRegistryUnregister()
        {
            var registry = new DataAssetRegistry();
            var sword = new TestSwordData { AssetName = "Temp" };
            sword.Tags.Add("temp");
            registry.Register(sword);
            AssertEqual(1, registry.Count, "注册后 Count=1");

            bool removed = registry.Unregister(sword);
            AssertTrue(removed, "Unregister 应返回 true");
            AssertEqual(0, registry.Count, "Unregister 后 Count=0");
            AssertTrue(registry.GetById(sword.AssetId) == null, "Unregister 后 GetById 返回 null");
            AssertTrue(registry.GetByName("Temp") == null, "Unregister 后 GetByName 返回 null");
            AssertEqual(0, registry.GetByTag("temp").Count, "Unregister 后 GetByTag 返回空");

            bool removedAgain = registry.Unregister(sword);
            AssertTrue(!removedAgain, "重复 Unregister 应返回 false");
        }

        // =========================================================
        // 6. DataAssetManager — 引用计数 & 自动卸载
        // =========================================================

        /// <summary>LoadAsset AddReference; AddReference/ReleaseReference 归零自动 Unload。</summary>
        public static void TestManagerRefCounts()
        {
            DataAssetManager.Initialize();
            var mgr = DataAssetManager.Instance!;

            var sword = new TestSwordData { AssetName = "RefCountSword" };
            mgr.RegisterAsset(sword);
            AssertTrue(!sword.IsLoaded, "注册后未自动加载");

            // LoadAsset: !IsLoaded → Load + AddReference
            mgr.LoadAsset(sword);
            AssertTrue(sword.IsLoaded, "LoadAsset 后 IsLoaded=true");
            AssertEqual(1, mgr.GetReferenceCount(sword.AssetId), "LoadAsset 后引用计数=1");

            // AddReference
            mgr.AddReference(sword.AssetId);
            AssertEqual(2, mgr.GetReferenceCount(sword.AssetId), "AddReference 后引用计数=2");

            // Release → 计数减但不卸载
            mgr.ReleaseReference(sword.AssetId);
            AssertEqual(1, mgr.GetReferenceCount(sword.AssetId), "Release 一次后计数=1");
            AssertTrue(sword.IsLoaded, "计数>0 时不应卸载");

            // Release 归零 → 自动 Unload
            mgr.ReleaseReference(sword.AssetId);
            AssertEqual(0, mgr.GetReferenceCount(sword.AssetId), "归零后计数=0");
            AssertTrue(!sword.IsLoaded, "归零后应自动 Unload");

            // Release 超出：不抛异常
            mgr.ReleaseReference(sword.AssetId); // count=0 already removed
            AssertEqual(0, mgr.GetReferenceCount(sword.AssetId), "超出释放不抛异常，计数保持 0");

            mgr.Shutdown();
        }

        /// <summary>RegisterAsset 后 GetAsset<T>(id) 和 GetAsset<T>(name) 命中。</summary>
        public static void TestManagerGetAsset()
        {
            DataAssetManager.Initialize();
            var mgr = DataAssetManager.Instance!;

            var sword = new TestSwordData { AssetName = "NamedSword" };
            mgr.RegisterAsset(sword);

            var byId = mgr.GetAsset<TestSwordData>(sword.AssetId);
            AssertTrue(byId != null, "GetAsset<T>(id) 应命中");
            AssertEqual("NamedSword", byId.AssetName, "GetAsset(id) 返回正确资产");

            var byName = mgr.GetAsset<TestSwordData>("namedsword");
            AssertTrue(byName != null, "GetAsset<T>(name) 大小写不敏感应命中");

            var wrongType = mgr.GetAsset<PlainData>(sword.AssetId);
            AssertTrue(wrongType == null, "GetAsset<T> 类型不匹配返回 null");

            AssertEqual(1, mgr.AssetCount, "AssetCount 应为 1");

            mgr.Shutdown();
        }

        /// <summary>OnAssetLoaded 事件通过 RegisterAsset 的 OnLoaded 转发。</summary>
        public static void TestManagerAssetLoadedEvent()
        {
            DataAssetManager.Initialize();
            var mgr = DataAssetManager.Instance!;

            DataAsset? eventReceived = null;
            mgr.OnAssetLoaded += a => eventReceived = a;

            var sword = new TestSwordData { AssetName = "EventSword" };
            mgr.RegisterAsset(sword);
            AssertTrue(eventReceived == null, "注册但未 Load，事件不应触发");

            sword.Load(); // 触发 OnLoaded → manager 转发 OnAssetLoaded
            AssertTrue(eventReceived != null, "Load 后 OnAssetLoaded 应触发");
            AssertEqual(sword.AssetId, eventReceived.AssetId, "事件应携带正确资产");

            mgr.Shutdown();
        }

        // =========================================================
        // 7. JSON Save/Load/Reload round-trip
        // =========================================================

        /// <summary>SaveToFile → LoadFromFile 字段保持一致。</summary>
        public static void TestJsonSaveLoadRoundTrip()
        {
            DataAssetManager.Initialize();
            var mgr = DataAssetManager.Instance!;

            string tmpDir = Path.Combine(Path.GetTempPath(), "yama_dataasset_test");
            Directory.CreateDirectory(tmpDir);
            string filePath = Path.Combine(tmpDir, "sword.json");
            try
            {
                var orig = new TestSwordData
                {
                    AssetName = "SavedSword",
                    WeaponName = "FlameBlade",
                    Damage = 77.5f
                };
                orig.Tags.Add("fire");
                orig.Tags.Add("rare");
                orig.Enchantments.Add("Burn");

                bool saved = mgr.SaveToFile(orig, filePath);
                AssertTrue(saved, "SaveToFile 应返回 true");
                AssertTrue(File.Exists(filePath), "文件应存在");

                // 加载到新 Manager（清空旧的）
                mgr.Shutdown();
                DataAssetManager.Initialize();
                var mgr2 = DataAssetManager.Instance!;

                var loaded = mgr2.LoadFromFile<TestSwordData>(filePath);
                AssertTrue(loaded != null, "LoadFromFile 应返回资产");
                AssertEqual("FlameBlade", loaded.WeaponName, "WeaponName 应保持");
                AssertEqual(77.5f, loaded.Damage, "Damage 应保持");
                AssertEqual(2, loaded.Tags.Count, "Tags 应保持");
                AssertTrue(loaded.Tags.Contains("fire"), "Tags 应含 fire");
                AssertEqual(1, loaded.Enchantments.Count, "Enchantments 应保持");
                AssertEqual("Burn", loaded.Enchantments[0], "Enchantments[0] 应为 Burn");
                AssertEqual(filePath, loaded.AssetPath, "AssetPath 应更新");
                AssertTrue(loaded.IsLoaded, "LoadFromFile 应自动 Load");
            }
            finally
            {
                if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, recursive: true);
                mgr.Shutdown();
            }
        }

        /// <summary>LoadFromFile 文件不存在返回 null。</summary>
        public static void TestLoadFromFileMissing()
        {
            DataAssetManager.Initialize();
            var mgr = DataAssetManager.Instance!;
            var result = mgr.LoadFromFile<TestSwordData>("__nonexistent__.json");
            AssertTrue(result == null, "文件不存在应返回 null");
            mgr.Shutdown();
        }

        /// <summary>ReloadAsset 从磁盘重读，保留 ID，事件触发。</summary>
        public static void TestReloadAsset()
        {
            DataAssetManager.Initialize();
            var mgr = DataAssetManager.Instance!;

            string tmpDir = Path.Combine(Path.GetTempPath(), "yama_dataasset_test_reload");
            Directory.CreateDirectory(tmpDir);
            string filePath = Path.Combine(tmpDir, "plain.json");
            try
            {
                var orig = new PlainData { AssetName = "Reloadable", Level = 10, Description = "v1" };
                mgr.SaveToFile(orig, filePath);
                mgr.RegisterAsset(orig);
                var savedId = orig.AssetId;
                mgr.LoadAsset(orig);

                // 修改磁盘文件（模拟热重载）
                var modified = new PlainData { AssetName = "Reloadable", Level = 20, Description = "v2" };
                modified.AssetId = savedId;
                string json = System.Text.Json.JsonSerializer.Serialize(modified);
                File.WriteAllText(filePath, json);

                // Reload
                bool reloaded = false;
                mgr.OnAssetReloaded += a => reloaded = true;
                bool ok = mgr.ReloadAsset(savedId);
                AssertTrue(ok, "ReloadAsset 应返回 true");
                AssertTrue(reloaded, "OnAssetReloaded 事件应触发");

                var after = mgr.GetAsset<PlainData>(savedId);
                AssertTrue(after != null, "Reload 后资产应存在");
                AssertEqual(20, after.Level, "Reload 后 Level 应为 20");
                AssertEqual("v2", after.Description, "Reload 后 Description 应为 v2");
                AssertEqual(savedId, after.AssetId, "Reload 保留原 ID");
            }
            finally
            {
                if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, recursive: true);
                mgr.Shutdown();
            }
        }

        /// <summary>Shutdown 后 Instance 置 null，所有资产 Unload。</summary>
        public static void TestShutdownClears()
        {
            DataAssetManager.Initialize();
            var mgr = DataAssetManager.Instance!;
            var asset = new PlainData { AssetName = "Shutdown" };
            mgr.RegisterAsset(asset);
            mgr.LoadAsset(asset);
            AssertTrue(asset.IsLoaded, "加载后 IsLoaded=true");

            mgr.Shutdown();
            AssertTrue(!asset.IsLoaded, "Shutdown 后资产应 Unload");
            AssertTrue(DataAssetManager.Instance == null, "Shutdown 后 Instance 应置 null");
        }
    }
}
