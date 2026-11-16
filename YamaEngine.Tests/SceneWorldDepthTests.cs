// SceneWorldDepthTests.cs — Unity5 SceneManager / WorldReal / SceneManagerReal 深度验证
// 覆盖:
//   * YamaEngine.Scenes.SceneManager: New/Load/Save/Switch/Pop/PushSubScene/Instantiate/AutoSave
//   * YamaEngine.ECS.WorldReal: Create/AddSystem/StepMode/Update/Query/Reset/Dispose
//   * YamaEngine.ECS.WorldSystemManager: CreateWorld/DestroyWorld/GetWorld/UpdateAll/TransferEntity
//   * YamaEngine.GameFramework.SceneManagerReal: LoadScene(Single/Additive)/SetActiveScene/UnloadScene

using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using YamaEngine.ECS;
using YamaEngine.GameFramework;
using YamaEngine.Scenes;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests
{
    // 测试用系统：记录更新次数和最近 dt
    public sealed class CountingSystem : SystemBaseReal
    {
        public int UpdateCount;
        public float LastDt;
        public bool Created, Destroyed;
        public override void OnCreate() { Created = true; }
        public override void OnDestroy() { Destroyed = true; }
        public override void OnUpdate(float dt) { UpdateCount++; LastDt = dt; }
    }

    public static class SceneWorldDepthTests
    {
        // =========================================================
        // 1. YamaEngine.Scenes.SceneManager
        // =========================================================

        /// <summary>NewScene 清空当前场景并重置 IsDirty/Path。</summary>
        public static void TestSceneManagerNewScene()
        {
            using var ecs = new EntityComponentSystem();
            using var sm = new SceneManager(ecs, autoSaveDir: Path.Combine(Path.GetTempPath(), "YamaTests_AutoSave_" + Guid.NewGuid().ToString("N")));

            sm.NewScene("Level1");
            AssertEqual("Level1", sm.CurrentScene.Name, "CurrentScene.Name 应为 Level1");
            AssertEqual(1, sm.CurrentScene.Version, "Version 应为 CurrentVersion");
            AssertTrue(sm.CurrentScenePath == null, "新场景 Path 应为 null");
            AssertTrue(!sm.IsDirty, "新场景 IsDirty 应 false");

            // 再 NewScene 一次：前一个场景被卸载（ECS 清空）
            ecs.CreateEntity("Orphan");
            sm.NewScene("Level2");
            AssertEqual("Level2", sm.CurrentScene.Name, "第二次 NewScene 应切换到 Level2");
            AssertEqual(0, new List<int>(ecs.AllEntities).Count, "NewScene 应清空 ECS");
        }

        /// <summary>LoadScene(SceneData) 重建实体 + SaveCurrentScene 落盘 + 重载 round-trip。</summary>
        public static void TestSceneManagerLoadSaveRoundtrip()
        {
            string dir = Path.Combine(Path.GetTempPath(), "YamaTests_Scene_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "roundtrip.yama");

            var data = new SceneData { Name = "Hero" };
            data.Entities.Add(new EntityData
            {
                GUID = Guid.NewGuid(),
                Name = "Hero",
                Transform = new TransformData
                {
                    Position = new float[] { 1, 2, 3 },
                    Scale = new float[] { 2, 2, 2 }
                },
                Components =
                {
                    new ComponentData
                    {
                        TypeName = nameof(TagComponent),
                        Properties = { ["Tag"] = "Player", ["Layer"] = 1 }
                    }
                }
            });

            using (var ecs = new EntityComponentSystem())
            using (var sm = new SceneManager(ecs))
            {
                sm.LoadScene(data);
                AssertEqual(1, ecs.EntityCount, "LoadScene 后应创建 1 个实体");
                var ids = new List<int>(ecs.AllEntities);
                AssertEqual("Hero", ecs.GetEntityName(ids[0]), "实体名应 Hero");
                var t = ecs.GetComponent<TransformComponent>(ids[0]);
                AssertTrue(MathF.Abs(t.Position.X - 1) < 1e-5 && MathF.Abs(t.Position.Y - 2) < 1e-5,
                    "Position 应从数据恢复");

                sm.SaveCurrentScene(path);
                AssertTrue(File.Exists(path), "SaveCurrentScene 应写出文件");
                AssertTrue(!sm.IsDirty, "保存后 IsDirty 应 false");
                AssertEqual(path, sm.CurrentScenePath, "CurrentScenePath 应记录保存路径");
            }

            using (var ecs2 = new EntityComponentSystem())
            using (var sm2 = new SceneManager(ecs2))
            {
                sm2.LoadScene(path);
                AssertEqual(1, ecs2.EntityCount, "从文件重载应创建 1 个实体");
                var ids2 = new List<int>(ecs2.AllEntities);
                AssertEqual("Hero", ecs2.GetEntityName(ids2[0]), "重载后实体名应 Hero");
                var tag = ecs2.GetComponent<TagComponent>(ids2[0]);
                AssertEqual("Player", tag.Tag, "Tag 应 round-trip");
            }
        }

        /// <summary>SwitchScene 压栈 + PopScene 恢复，ClearSceneStack 清空栈。</summary>
        public static void TestSceneManagerSwitchPopStack()
        {
            string dir = Path.Combine(Path.GetTempPath(), "YamaTests_Scene_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string pathA = Path.Combine(dir, "A.yama");
            string pathB = Path.Combine(dir, "B.yama");

            SceneJsonSerializer.SaveToFile(new SceneData
            {
                Name = "A",
                Entities = { new EntityData { GUID = Guid.NewGuid(), Name = "A_Entity" } }
            }, pathA);
            SceneJsonSerializer.SaveToFile(new SceneData
            {
                Name = "B",
                Entities = { new EntityData { GUID = Guid.NewGuid(), Name = "B_Entity" } }
            }, pathB);

            using var ecs = new EntityComponentSystem();
            using var sm = new SceneManager(ecs);
            sm.LoadScene(pathA);
            AssertEqual(1, ecs.EntityCount, "初始 A 应有 1 实体");

            // 切到 B：A 压栈，ECS 清空后载入 B
            sm.SwitchScene(pathB);
            AssertEqual(1, ecs.EntityCount, "切换到 B 后 ECS 应只剩 B 实体");
            var idsB = new List<int>(ecs.AllEntities);
            AssertEqual("B_Entity", ecs.GetEntityName(idsB[0]), "B 实体名应正确");
            AssertEqual(1, sm.SceneStackDepth, "SwitchScene 应压栈");

            // Pop 回 A
            var map = sm.PopScene();
            AssertTrue(map != null, "PopScene 应返回映射");
            AssertEqual(1, ecs.EntityCount, "Pop 回 A 后应重建 A 实体");
            var idsA = new List<int>(ecs.AllEntities);
            AssertEqual("A_Entity", ecs.GetEntityName(idsA[0]), "Pop 后 A 实体名应正确");
            AssertEqual(0, sm.SceneStackDepth, "Pop 后栈应清空");

            // 栈空再 Pop 返回 null
            AssertTrue(sm.PopScene() == null, "空栈 Pop 应 null");

            // ClearSceneStack：先压栈再清空
            sm.SwitchScene(pathB);
            AssertEqual(1, sm.SceneStackDepth, "再 Switch 应压栈");
            sm.ClearSceneStack();
            AssertEqual(0, sm.SceneStackDepth, "ClearSceneStack 应清空栈");
        }

        /// <summary>PushSubScene 不清空 ECS；InstantiateScene 追加实体。</summary>
        public static void TestSceneManagerPushSubAndInstantiate()
        {
            var baseData = new SceneData
            {
                Name = "Base",
                Entities = { new EntityData { GUID = Guid.NewGuid(), Name = "Base_Entity" } }
            };
            var overlayData = new SceneData
            {
                Name = "Overlay",
                Entities =
                {
                    new EntityData { GUID = Guid.NewGuid(), Name = "Overlay_A" },
                    new EntityData { GUID = Guid.NewGuid(), Name = "Overlay_B" }
                }
            };

            using var ecs = new EntityComponentSystem();
            using var sm = new SceneManager(ecs);
            sm.LoadScene(baseData);
            AssertEqual(1, ecs.EntityCount, "基础场景应有 1 实体");

            sm.PushSubScene(overlayData);
            AssertEqual(3, ecs.EntityCount, "PushSubScene 应保留 base + 追加 overlay = 3 实体");

            sm.InstantiateScene(overlayData);
            AssertEqual(5, ecs.EntityCount, "InstantiateScene 应再追加 2 实体 = 5");
            AssertTrue(sm.IsDirty, "InstantiateScene 应置 IsDirty=true");
        }

        /// <summary>AutoSaveNow 立即落盘；TrySaveCurrentScene 错误不抛。</summary>
        public static void TestSceneManagerAutoSaveAndTrySave()
        {
            string dir = Path.Combine(Path.GetTempPath(), "YamaTests_AutoSave_" + Guid.NewGuid().ToString("N"));
            using var ecs = new EntityComponentSystem();
            using var sm = new SceneManager(ecs, autoSaveDir: dir);
            sm.NewScene("AutoSave_Scene");
            ecs.CreateEntity("Persist");

            sm.AutoSaveNow();
            AssertTrue(Directory.Exists(dir), "AutoSaveNow 应创建目录");
            string[] files = Directory.GetFiles(dir, "AutoSave_Scene_*.yama");
            AssertTrue(files.Length >= 1, "AutoSaveNow 应生成一个自动保存文件");

            // TrySave 无路径 → false 不抛
            bool ok = sm.TrySaveCurrentScene(null, out string error);
            AssertTrue(!ok, "无保存路径 TrySave 应 false");
            AssertTrue(!string.IsNullOrEmpty(error), "TrySave 失败应返回错误信息");

            // 正常路径 → true
            string target = Path.Combine(dir, "manual.yama");
            bool ok2 = sm.TrySaveCurrentScene(target, out string error2);
            AssertTrue(ok2, "合法路径 TrySave 应 true");
            AssertTrue(error2 == null, "成功时 error 应 null");
            AssertTrue(File.Exists(target), "TrySave 应落盘");
        }

        // =========================================================
        // 2. WorldReal
        // =========================================================

        /// <summary>WorldReal.Create 默认 5 个系统组，按序为 Initialization/PreUpdate/Update/PostUpdate/Presentation。</summary>
        public static void TestWorldRealDefaults()
        {
            using var world = WorldReal.Create("TestWorld");
            AssertEqual("TestWorld", world.Name, "Name 应匹配");
            AssertEqual(5, world.SystemGroups.Count, "默认 5 个系统组");
            AssertEqual("Initialization", world.SystemGroupOrder[0], "第 1 组 Initialization");
            AssertEqual("Update", world.SystemGroupOrder[2], "第 3 组 Update");
            AssertEqual("Presentation", world.SystemGroupOrder[4], "第 5 组 Presentation");
            AssertEqual(0, world.EntityCount, "新 World 实体数 0");
            AssertTrue(!world.IsRunning, "未 Start 前 IsRunning=false");
            AssertEqual(1.0f, world.TimeScale, "TimeScale 默认 1");
            AssertEqual(0.1f, world.MaxDeltaTime, "MaxDeltaTime 默认 0.1");
            AssertEqual(FixedStepMode.Variable, world.StepMode, "StepMode 默认 Variable");
        }

        /// <summary>AddSystem 注册 + OnCreate 触发 + OnUpdate 计数。</summary>
        public static void TestWorldRealAddSystemAndUpdate()
        {
            using var world = WorldReal.Create("UpdateWorld");
            var sys = new CountingSystem();
            world.AddSystem(sys, "Update");

            world.Update(1f / 60f);
            AssertTrue(sys.Created, "首次 Update 应触发 OnCreate");
            AssertEqual(1, sys.UpdateCount, "Update 后应执行 1 次");
            AssertTrue(MathF.Abs(sys.LastDt - 1f / 60f) < 1e-6, "LastDt 应等于 dt");

            for (int i = 0; i < 4; i++) world.Update(1f / 60f);
            AssertEqual(5, sys.UpdateCount, "5 次 Update 后应执行 5 次");
            AssertEqual(5UL, world.FrameCount, "FrameCount 应累加到 5");
        }

        /// <summary>固定步长累积器：dt 小于 FixedDeltaTime 时不步进，累积到阈值才步进。</summary>
        public static void TestWorldRealFixedStep()
        {
            using var world = WorldReal.Create("FixedWorld");
            world.StepMode = FixedStepMode.Fixed;
            world.FixedDeltaTime = 1f / 30f; // ≈33.3ms
            var sys = new CountingSystem();
            world.AddSystem(sys, "Update");

            // 一次小 dt 不应触发系统
            world.Update(0.010f);
            AssertEqual(0, sys.UpdateCount, "小于 FixedDeltaTime 的 dt 不应步进");

            // 累积足够触发 1 次
            world.Update(0.030f);
            AssertEqual(1, sys.UpdateCount, "累积到阈值应步进 1 次");

            // dt 巨大但 MaxDeltaTime=0.1 限制 → 最多 3 步 (0.1 / (1/30) ≈ 3)
            world.Update(0.5f);
            AssertTrue(sys.UpdateCount >= 2 && sys.UpdateCount <= 4,
                $"大 dt 步进应在 2~4 次，实际 {sys.UpdateCount}");
        }

        /// <summary>暂停 TimeScale=0 / IsPaused 时 DeltaTime 与 FrameCount 行为。</summary>
        public static void TestWorldRealPauseAndTimeScale()
        {
            using var world = WorldReal.Create("PauseWorld");
            world.TimeScale = 0.5f;
            world.Update(0.1f);
            AssertTrue(MathF.Abs(world.DeltaTime - 0.05f) < 1e-6,
                $"TimeScale=0.5 时 DeltaTime 应 0.05，实际 {world.DeltaTime}");
            AssertTrue(MathF.Abs(world.ElapsedTime - 0.05f) < 1e-6, "ElapsedTime 应累加缩放 dt");

            world.IsPaused = true;
            world.Update(0.1f);
            AssertEqual(0f, world.DeltaTime, "暂停时 DeltaTime 应 0");
            AssertEqual(1UL, world.FrameCount, "暂停时 FrameCount 不应增加");

            world.IsPaused = false;
            world.ResetTime();
            AssertEqual(0f, world.ElapsedTime, "ResetTime 应清零 ElapsedTime");
            AssertEqual(0f, world.DeltaTime, "ResetTime 应清零 DeltaTime");
            AssertEqual(0UL, world.FrameCount, "ResetTime 应清零 FrameCount");
        }

        /// <summary>Start/Stop 触发事件 + 重复 Start 幂等。</summary>
        public static void TestWorldRealStartStop()
        {
            using var world = WorldReal.Create("Lifecycle");
            int started = 0, stopped = 0, updated = 0;
            world.WorldStarted += _ => started++;
            world.WorldStopped += _ => stopped++;
            world.WorldUpdated += (_, _) => updated++;

            world.Start();
            world.Start(); // 幂等
            AssertEqual(1, started, "Start 幂等应只触发一次");

            world.Update(0.016f);
            AssertEqual(1, updated, "Update 应触发 WorldUpdated 事件");

            world.Stop();
            world.Stop(); // 幂等
            AssertEqual(1, stopped, "Stop 幂等应只触发一次");
            AssertTrue(!world.IsRunning, "Stop 后 IsRunning=false");
        }

        /// <summary>GetOrCreateQuery 缓存命中：同参数返回同一实例。</summary>
        public static void TestWorldRealQueryCache()
        {
            using var world = WorldReal.Create("QueryWorld");
            var q1 = world.GetOrCreateQuery(allTypes: new[] { typeof(TransformComponent) });
            var q2 = world.GetOrCreateQuery(allTypes: new[] { typeof(TransformComponent) });
            AssertTrue(ReferenceEquals(q1, q2), "同参数查询应命中缓存");

            var q3 = world.GetOrCreateQuery(allTypes: new[] { typeof(TagComponent) });
            AssertTrue(!ReferenceEquals(q1, q3), "不同参数应返回不同查询");
        }

        // =========================================================
        // 3. WorldSystemManager
        // =========================================================

        /// <summary>构造时自动创建 DefaultWorld；CreateWorld 名字唯一化。</summary>
        public static void TestWorldManagerDefaults()
        {
            var mgr = new WorldSystemManager();
            AssertEqual(1, mgr.WorldCount, "构造时应只有 DefaultWorld");
            AssertTrue(mgr.DefaultWorld != null, "DefaultWorld 不应为 null");
            AssertTrue(mgr.ActiveWorld == mgr.DefaultWorld, "ActiveWorld 应初始为 DefaultWorld");

            var w1 = mgr.CreateWorld("Gameplay");
            var w2 = mgr.CreateWorld("Gameplay"); // 重名自动加后缀
            AssertEqual("Gameplay", w1.Name, "第一个 Gameplay 名称保留");
            AssertEqual("Gameplay_1", w2.Name, "重名应加后缀 _1");
            AssertEqual(3, mgr.WorldCount, "共 3 个 World");

            // Destroy 默认 World 失败
            AssertTrue(!mgr.DestroyWorld(mgr.DefaultWorld), "Destroy 默认 World 应 false");
            // 销毁非默认成功
            AssertTrue(mgr.DestroyWorld(w1), "销毁非默认 World 应 true");
            AssertEqual(2, mgr.WorldCount, "销毁后剩 2 个");
            // 重名查询返回 null
            AssertTrue(mgr.GetWorld("Gameplay") == null, "GetWorld 已销毁名应 null");
            AssertTrue(mgr.GetWorld("Gameplay_1") == w2, "GetWorld 剩余 World 命中");
        }

        /// <summary>UpdateAll 只更新 IsRunning=true 的 World。</summary>
        public static void TestWorldManagerUpdateAll()
        {
            var mgr = new WorldSystemManager();
            var sys = new CountingSystem();
            var extra = mgr.CreateWorld("Extra");
            extra.AddSystem(sys, "Update");

            // 未 Start：UpdateAll 不更新
            mgr.UpdateAll(0.016f);
            AssertEqual(0, sys.UpdateCount, "未 Start 的 World 不应更新");

            extra.Start();
            mgr.UpdateAll(0.016f);
            mgr.UpdateAll(0.016f);
            AssertEqual(2, sys.UpdateCount, "Start 后 UpdateAll 应驱动 2 次更新");
        }

        /// <summary>TransferEntity 跨 World 复制实体名与组件数据。</summary>
        public static void TestWorldManagerTransferEntity()
        {
            var mgr = new WorldSystemManager();
            var src = mgr.CreateWorld("Src");
            var dst = mgr.CreateWorld("Dst");

            var e = src.EntityManager.CreateEntity("Migrator");
            src.EntityManager.AddComponent(e, new TransformComponent
            {
                Position = new Vector3(5, 6, 7),
                Scale = Vector3.One
            });

            AssertEqual(1, src.EntityCount, "源 World 应 1 实体");
            AssertEqual(0, dst.EntityCount, "目标 World 应空");

            var migrated = mgr.TransferEntity(src, e, dst);
            AssertEqual(1, dst.EntityCount, "迁移后目标 World 应 1 实体");
            AssertEqual("Migrator", dst.EntityManager.GetName(migrated), "迁移后实体名应保留");
            AssertTrue(dst.EntityManager.HasComponent<TransformComponent>(migrated),
                "迁移后应带 TransformComponent");
            var t = dst.EntityManager.GetComponentData<TransformComponent>(migrated);
            AssertTrue(MathF.Abs(t.Position.X - 5) < 1e-5 && MathF.Abs(t.Position.Z - 7) < 1e-5,
                "迁移后 Position 应保留");
        }

        // =========================================================
        // 4. YamaEngine.GameFramework.SceneManagerReal
        // =========================================================

        /// <summary>LoadScene Single 卸载旧场景、加载新场景并自动激活。</summary>
        public static void TestSceneManagerRealSingle()
        {
            var mgr = new SceneManagerReal();
            var s1 = mgr.LoadScene("Level1", LoadSceneMode.Single);
            AssertTrue(s1.IsLoaded, "加载后 IsLoaded=true");
            AssertTrue(s1.IsActive, "Single 模式应自动激活");
            AssertEqual(s1, mgr.ActiveScene, "ActiveScene 应为 Level1");
            AssertEqual(1, mgr.SceneCount, "SceneCount=1");

            // Single 再加载：旧场景卸载
            var s2 = mgr.LoadScene("Level2", LoadSceneMode.Single);
            AssertEqual(1, mgr.SceneCount, "Single 加载后只保留最新场景");
            AssertTrue(mgr.GetSceneByName("Level1") == null, "旧场景应被卸载");
            AssertEqual(s2, mgr.ActiveScene, "ActiveScene 应切换到 Level2");
        }

        /// <summary>LoadScene Additive 叠加场景不卸载主场景；SetActiveScene 手动切换。</summary>
        public static void TestSceneManagerRealAdditiveAndSetActive()
        {
            var mgr = new SceneManagerReal();
            var main = mgr.LoadScene("Main", LoadSceneMode.Single);
            var add = mgr.LoadScene("Overlay", LoadSceneMode.Additive);

            AssertEqual(2, mgr.SceneCount, "Additive 后应 2 个场景");
            AssertEqual(main, mgr.ActiveScene, "Additive 不应切换 ActiveScene");
            AssertTrue(add.IsLoaded, "叠加场景应已加载");
            AssertTrue(!add.IsActive, "叠加场景未主动激活");

            mgr.SetActiveScene(add);
            AssertEqual(add, mgr.ActiveScene, "SetActiveScene 应切换活跃场景");
            AssertTrue(add.IsActive, "新 ActiveScene 应 IsActive=true");
            AssertTrue(!main.IsActive, "旧 ActiveScene 应退回 Loaded 状态");
            AssertTrue(main.IsLoaded, "旧场景仍 IsLoaded=true");

            // 已卸载场景不能设为 Active
            mgr.UnloadScene(add);
            AssertEqual(main, mgr.ActiveScene, "卸载 ActiveScene 后应回退到剩余场景");
            AssertTrue(mgr.GetSceneByName("Overlay") == null, "卸载后按名查不到");
        }

        /// <summary>UnloadAdditive 仅卸载最后加载的叠加场景，保留主场景。</summary>
        public static void TestSceneManagerRealUnloadAdditive()
        {
            var mgr = new SceneManagerReal();
            var main = mgr.LoadScene("Main", LoadSceneMode.Single);
            mgr.LoadScene("OverlayA", LoadSceneMode.Additive);
            mgr.LoadScene("OverlayB", LoadSceneMode.Additive);
            AssertEqual(3, mgr.SceneCount, "3 个场景已加载");

            var unloaded = mgr.LoadScene("Unused", LoadSceneMode.UnloadAdditive);
            AssertEqual("OverlayB", unloaded.Name, "UnloadAdditive 应卸载最后加载的叠加场景");
            AssertEqual(2, mgr.SceneCount, "卸载后剩 2 个");
            AssertEqual(main, mgr.ActiveScene, "主场景应保留为 ActiveScene");
        }

        /// <summary>事件顺序：OnSceneLoaded/OnActiveSceneChanged/OnSceneUnloaded 均触发。</summary>
        public static void TestSceneManagerRealEvents()
        {
            var mgr = new SceneManagerReal();
            int loaded = 0, unloaded = 0, activeChanged = 0;
            mgr.OnSceneLoaded += _ => loaded++;
            mgr.OnSceneUnloaded += _ => unloaded++;
            mgr.OnActiveSceneChanged += (_, _) => activeChanged++;

            var a = mgr.LoadScene("A");
            AssertEqual(1, loaded, "加载应触发 OnSceneLoaded");
            AssertEqual(1, activeChanged, "首次加载应触发一次 OnActiveSceneChanged");

            var b = mgr.LoadScene("B", LoadSceneMode.Additive);
            AssertEqual(2, loaded, "叠加加载再触发一次 OnSceneLoaded");
            AssertEqual(1, activeChanged, "Additive 不切换 Active 不应触发 OnActiveSceneChanged");

            mgr.SetActiveScene(b);
            AssertEqual(2, activeChanged, "SetActiveScene 应触发 OnActiveSceneChanged");

            mgr.UnloadScene(b);
            AssertEqual(1, unloaded, "卸载应触发 OnSceneUnloaded");
            AssertEqual(3, activeChanged, "卸载 ActiveScene 回退也触发一次 OnActiveSceneChanged");
        }

        /// <summary>SceneDataProvider 注入 JSON 数据 → 场景反序列化出 GameObject 层级。</summary>
        public static void TestSceneManagerRealSceneDataProvider()
        {
            var mgr = new SceneManagerReal
            {
                SceneDataProvider = path =>
                {
                    AssertTrue(path.Contains("HeroScene"), "SceneDataProvider 应收到场景路径");
                    return "{\"roots\":[{\"name\":\"Hero\",\"tag\":\"Player\",\"pos\":[1.0,2.0,3.0],\"children\":[" +
                           "{\"name\":\"Weapon\",\"tag\":\"Item\",\"pos\":[0.0,1.0,0.0],\"children\":[]}]}]}";
                }
            };

            var scene = mgr.LoadScene("HeroScene", LoadSceneMode.Single);
            AssertEqual(1, scene.RootGameObjects.Count, "反序列化应生成 1 个根对象");
            var hero = scene.FindGameObject("Hero");
            AssertTrue(hero != null, "按名应能查到 Hero");
            AssertEqual("Player", hero.Tag, "Tag 应从数据恢复");
            AssertTrue(MathF.Abs(hero.Transform.LocalPosition.X - 1) < 1e-4,
                "Hero.LocalPosition.X 应=1");

            // 子对象通过 children 字段挂载
            AssertTrue(hero.Children.Count >= 1, "Hero 应有 1 个子对象");
            var weapon = hero.Children[0];
            AssertEqual("Weapon", weapon.Name, "子对象名应 Weapon");
            AssertEqual("Item", weapon.Tag, "子对象 Tag 应 Item");
        }
    }
}
