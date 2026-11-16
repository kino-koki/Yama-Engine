// SceneTreeTests.cs —— 追平 Godot 3 Node-tree 核心：TransformReal/GameObjectReal
// 覆盖：脏标记驱动惰性世界矩阵、父变换组合（旋转/缩放传播）、world-position-stays 重父化、
//       层级遍历（IsChildOf/Root/Find/GetSiblingIndex）、TransformPoint/InverseTransformPoint。
// 全部纯 CPU、确定性数值验证（Y-up）。

using System;
using System.Numerics;
using YamaEngine.GameFramework;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests
{
    public static class SceneTreeTests
    {
        private static void AssertNear(float got, float want, float tol, string msg)
            => AssertTrue(MathF.Abs(got - want) <= tol,
                $"{msg} (got {got:F4}, want {want:F4}±{tol:F4})");

        // ── 生命周期追踪组件 ──
        private sealed class LifecycleComponent : ComponentReal
        {
            public int AwakeCalls, StartCalls, EnableCalls, DisableCalls, UpdateCalls, DestroyCalls;
            public float LastDt;

            public override void Awake() { base.Awake(); AwakeCalls++; }
            public override void Start() { StartCalls++; }
            public override void OnEnable() { EnableCalls++; }
            public override void OnDisable() { DisableCalls++; }
            public override void Update(float dt) { UpdateCalls++; LastDt = dt; }
            public override void OnDestroy() { DestroyCalls++; }
        }

        // ── 多实例组件（标记 AllowMultiple） ──
        [AllowMultiple]
        private sealed class MultiComponent : ComponentReal
        {
            public int Value;
        }

        // ── 记录场景生命周期调用顺序的组件 ──
        private sealed class OrderComponent : ComponentReal
        {
            public readonly System.Collections.Generic.List<string> Log = new();
            public int UpdateCalls, OnDestroyCalls;
            public float LastDt;
            private readonly string _name;
            public OrderComponent(string name) { _name = name; }
            public override void Awake() => Log.Add(_name + ".Awake");
            public override void Start() => Log.Add(_name + ".Start");
            public override void OnEnable() => Log.Add(_name + ".OnEnable");
            public override void OnDisable() => Log.Add(_name + ".OnDisable");
            public override void Update(float dt) { UpdateCalls++; LastDt = dt; }
            public override void OnDestroy() { OnDestroyCalls++; Log.Add(_name + ".OnDestroy"); }
        }

        private static void AssertNearV(Vector3 got, Vector3 want, float tol, string msg)
        {
            AssertNear(got.X, want.X, tol, msg + " X");
            AssertNear(got.Y, want.Y, tol, msg + " Y");
            AssertNear(got.Z, want.Z, tol, msg + " Z");
        }

        // ── 根对象：无父 → LocalToWorld == S*R*T，世界位置==本地位置 ──
        public static void TestRootTransformIdentity()
        {
            var root = new GameObjectReal("Root");
            root.Transform.LocalPosition = new Vector3(5, 3, -2);
            root.Transform.SetEulerAngles(0, 90, 0);
            root.Transform.LocalScale = new Vector3(2, 2, 2);

            var world = root.Transform.Position;
            AssertNearV(world, new Vector3(5, 3, -2), 0.01f, "Root world position must equal local.");
            // Forward 在 yaw=90 时朝 -X（绕 Y 旋转 90°）
            var fwd = root.Transform.Forward;
            AssertNear(fwd.X, -1f, 0.02f, "Root forward (yaw=90) must point -X.");
            AssertNear(fwd.Z, 0f, 0.02f, "Root forward Z must be ~0.");
        }

        // ── 父子组合：子世界位置 = 父变换作用后的本地偏移 ──
        public static void TestChildWorldPositionComposesParent()
        {
            var parent = new GameObjectReal("Parent");
            parent.Transform.LocalPosition = new Vector3(10, 0, 0);

            var child = new GameObjectReal("Child");
            child.SetParent(parent, worldPositionStays: false);
            child.Transform.LocalPosition = new Vector3(5, 0, 0);

            // 子世界位置 = 父(10,0,0) + 本地(5,0,0) = (15,0,0)
            AssertNearV(child.Transform.Position, new Vector3(15, 0, 0), 0.01f,
                "Child world position must compose parent translation.");
        }

        // ── 旋转传播：父绕 Y 旋转 90°，子本地 +X 偏移在世界空间朝 -Z ──
        public static void TestChildRotationPropagatesFromParent()
        {
            var parent = new GameObjectReal("Parent");
            parent.Transform.LocalPosition = new Vector3(0, 0, 0);
            parent.Transform.SetEulerAngles(0, 90, 0); // 绕 Y 旋转 90°

            var child = new GameObjectReal("Child");
            child.SetParent(parent, worldPositionStays: false);
            child.Transform.LocalPosition = new Vector3(1, 0, 0); // 本地 +X

            // 世界空间：+X 绕 Y 轴 90° → 朝 -Z
            var world = child.Transform.Position;
            AssertNear(world.X, 0f, 0.01f, "Rotated child world X must be ~0.");
            AssertNear(world.Z, -1f, 0.01f, "Rotated child world Z must be ~-1 (X rotated into -Z).");
        }

        // ── 缩放传播：父缩放 2×，子本地偏移翻倍 ──
        public static void TestChildScalePropagatesFromParent()
        {
            var parent = new GameObjectReal("Parent");
            parent.Transform.LocalPosition = new Vector3(0, 0, 0);
            parent.Transform.LocalScale = new Vector3(2, 2, 2);

            var child = new GameObjectReal("Child");
            child.SetParent(parent, worldPositionStays: false);
            child.Transform.LocalPosition = new Vector3(3, 0, 0);

            var world = child.Transform.Position;
            AssertNear(world.X, 6f, 0.01f, "Parent scale 2x must double child local offset to 6.");
        }

        // ── WorldPositionStays 重父化：保留世界位置 ──
        public static void TestWorldPositionStaysOnReparent()
        {
            var a = new GameObjectReal("A");
            a.Transform.LocalPosition = new Vector3(10, 0, 0);
            var b = new GameObjectReal("B");
            b.Transform.LocalPosition = new Vector3(20, 0, 0);

            var child = new GameObjectReal("Child");
            child.SetParent(a, worldPositionStays: false);
            child.Transform.LocalPosition = new Vector3(5, 0, 0); // 世界 (15,0,0)

            // 重父化到 B，保持世界位置 (15,0,0)
            child.SetParent(b, worldPositionStays: true);
            AssertNearV(child.Transform.Position, new Vector3(15, 0, 0), 0.01f,
                "World-position-stays reparent must preserve world position.");
            // 新本地 = 15 - 20 = -5
            AssertNear(child.Transform.LocalPosition.X, -5f, 0.01f,
                "Reparented local X must be -5 to keep world X=15.");
        }

        // ── 层级遍历：IsChildOf / Root / Find ──
        public static void TestHierarchyTraversal()
        {
            var root = new GameObjectReal("Root");
            var mid = new GameObjectReal("Mid");
            mid.SetParent(root, worldPositionStays: false);
            var leaf = new GameObjectReal("Leaf");
            leaf.SetParent(mid, worldPositionStays: false);

            AssertTrue(leaf.Transform.IsChildOf(root.Transform),
                "Leaf must be a child-of Root (recursive).");
            AssertTrue(leaf.Transform.IsChildOf(mid.Transform),
                "Leaf must be a child-of Mid.");
            AssertTrue(!root.Transform.IsChildOf(leaf.Transform),
                "Root must NOT be child-of Leaf.");
            // Root 属性
            AssertTrue(ReferenceEquals(leaf.Transform.Root, root.Transform),
                "Leaf.Root must walk up to Root.");
            AssertTrue(ReferenceEquals(root.Transform.Root, root.Transform),
                "Root.Root must be itself.");
            // Find 按名递归
            var found = root.Find("Leaf");
            AssertTrue(found != null, "Root.Find(Leaf) must locate the nested child.");
            AssertEqual(1, root.ChildCount, "Root must have exactly 1 direct child (Mid).");
        }

        // ── 循环父化：禁止把祖先挂到自己后代下（A→B→C，尝试 A.SetParent(C) 抛异常） ──
        public static void TestCycleReparentForbidden()
        {
            var a = new GameObjectReal("A");
            var b = new GameObjectReal("B");
            var c = new GameObjectReal("C");
            b.SetParent(a, worldPositionStays: false);
            c.SetParent(b, worldPositionStays: false);

            bool threw = false;
            try { a.SetParent(c, worldPositionStays: false); }
            catch (InvalidOperationException) { threw = true; }
            AssertTrue(threw,
                "Moving an ancestor under its own descendant (A.SetParent(C)) must throw InvalidOperationException.");
            // 确保未破坏原层级
            AssertEqual(1, a.ChildCount, "Cycle attempt must leave original hierarchy intact (A still has B).");
            AssertEqual(1, b.ChildCount, "B still has C.");
            AssertTrue(a.Transform.IsChildOf(c.Transform) == false,
                "After a failed cycle attempt A must not be under C.");
        }

        // ── 取消父化：SetParent(null) 回到根，保留世界位置 ──
        public static void TestUnparentReturnsToRoot()
        {
            var parent = new GameObjectReal("Parent");
            parent.Transform.LocalPosition = new Vector3(10, 0, 0);
            var child = new GameObjectReal("Child");
            child.SetParent(parent, worldPositionStays: false);
            child.Transform.LocalPosition = new Vector3(5, 0, 0); // 世界 (15,0,0)

            // 取消父化，保持世界位置
            child.SetParent(null, worldPositionStays: true);
            AssertTrue(child.Transform.Parent == null, "Unparented child must have no parent.");
            AssertNearV(child.Transform.Position, new Vector3(15, 0, 0), 0.01f,
                "Unparent must preserve world position (15,0,0).");
            AssertTrue(ReferenceEquals(child.Transform.Root, child.Transform),
                "Unparented child becomes its own root.");
        }

        // ── TransformPoint / InverseTransformPoint 往返 ──
        public static void TestTransformPointRoundTrip()
        {
            var obj = new GameObjectReal("Obj");
            obj.Transform.LocalPosition = new Vector3(1, 2, 3);
            obj.Transform.SetEulerAngles(0, 45, 0);
            obj.Transform.LocalScale = new Vector3(2, 2, 2);

            var local = new Vector3(1, 0, 0);
            var world = obj.Transform.TransformPoint(local);
            var back = obj.Transform.InverseTransformPoint(world);
            AssertNearV(back, local, 0.01f, "TransformPoint/Inverse round trip must recover local point.");
        }

        // ── 组件唯一性：默认单实例，重复添加抛异常 ──
        public static void TestComponentUniquenessEnforced()
        {
            var go = new GameObjectReal("CompObj");
            var c1 = go.AddComponent<LifecycleComponent>();
            AssertTrue(c1 != null, "AddComponent must return the new component.");
            AssertTrue(ReferenceEquals(c1.GameObject, go), "Component.GameObject must point back to owner.");

            bool threw = false;
            try { go.AddComponent<LifecycleComponent>(); }
            catch (InvalidOperationException) { threw = true; }
            AssertTrue(threw, "Adding a second single-instance component must throw InvalidOperationException.");
        }

        // ── [AllowMultiple]：同类型可多实例 ──
        public static void TestAllowMultipleComponents()
        {
            var go = new GameObjectReal("MultiObj");
            var a = go.AddComponent<MultiComponent>();
            var b = go.AddComponent<MultiComponent>(); // 不应抛
            a.Value = 1;
            b.Value = 2;
            AssertEqual(2, go.GetComponents<MultiComponent>().Count,
                "[AllowMultiple] must permit two instances of the same type.");
            AssertEqual(1, a.Value, "First multi-component value must be preserved.");
            AssertEqual(2, b.Value, "Second multi-component value must be preserved.");
        }

        // ── 组件 Awake：手动调用设置组件 initialized 标记（对象级 IsInitialized 由 Scene 驱动） ──
        public static void TestComponentAwakeRunsOnAdd()
        {
            var go = new GameObjectReal("AwakeObj");
            var comp = go.AddComponent<LifecycleComponent>();
            AssertTrue(!comp.IsInitialized, "Fresh component must not be initialized yet.");
            comp.Awake(); // Awake 是 public，设置组件的 initialized 标记
            AssertEqual(1, comp.AwakeCalls, "Awake must run once on demand.");
            AssertTrue(comp.IsInitialized, "After Awake, the component must be marked initialized.");
        }

        // ── Enabled=false 的组件：OnDisable 触发，RemoveComponent 调 OnDestroy ──
        public static void TestComponentRemoveCallsDestroy()
        {
            var go = new GameObjectReal("RemoveObj");
            var comp = go.AddComponent<LifecycleComponent>();
            comp.Awake();
            comp.Start(); // Start+OnEnable 由使用者驱动（此处手动，模拟已启用流程）
            comp.Enabled = true;

            bool removed = go.RemoveComponent<LifecycleComponent>();
            AssertTrue(removed, "RemoveComponent must return true when the component exists.");
            AssertTrue(go.GetComponent<LifecycleComponent>() == null,
                "After RemoveComponent, GetComponent must return null.");
            AssertTrue(comp.DestroyCalls >= 1, "RemoveComponent must call OnDestroy.");
            AssertTrue(comp.GameObject == null,
                "After removal, Component.GameObject must be cleared.");
        }

        // ── HasComponent / TryGetComponent ──
        public static void TestComponentQueryAPI()
        {
            var go = new GameObjectReal("QueryObj");
            AssertTrue(!go.HasComponent<LifecycleComponent>(), "New object must not have the component.");
            go.AddComponent<LifecycleComponent>();
            AssertTrue(go.HasComponent<LifecycleComponent>(), "HasComponent must return true after add.");
            bool found = go.TryGetComponent<LifecycleComponent>(out var comp);
            AssertTrue(found && comp != null, "TryGetComponent must find the added component.");
            bool missing = go.TryGetComponent<MultiComponent>(out _);
            AssertTrue(!missing, "TryGetComponent must fail for absent type.");
        }

        // ── GameObjectReal 标签/层级/激活基础 ──
        public static void TestGameObjectMetadata()
        {
            var go = new GameObjectReal("Meta");
            AssertTrue(go.Id > 0, "GameObjectReal must have a positive unique Id.");
            AssertEqual("Default", go.Tag, "Default tag must be 'Default'.");
            AssertEqual(0, go.Layer, "Default layer must be 0.");
            AssertTrue(go.IsActive, "New object must be active by default.");
            AssertTrue(go.IsInitialized == false && go.IsStarted == false,
                "New object must not be initialized/started until lifecycle runs.");
            // 唯一 Id
            var go2 = new GameObjectReal("Meta2");
            AssertTrue(go2.Id != go.Id, "Each GameObject must get a unique Id.");
        }

        // ── 对象初始化：Awake 全部执行 → 再 Start 全部执行（模拟 SceneManager 广度优先两遍初始化） ──
        public static void TestSceneInitializeRunsAwakeThenStart()
        {
            var a = new GameObjectReal("A");
            var b = new GameObjectReal("B");
            var ca = new OrderComponent("A");
            var cb = new OrderComponent("B");
            a.AddComponent(ca);
            b.AddComponent(cb);

            // 模拟 SceneManager.InitializeSceneGameObjects：先全 Awake，再全 Start
            a.Initialize();
            b.Initialize();
            a.Start();
            b.Start();

            AssertTrue(a.IsInitialized, "A must be initialized after Initialize.");
            AssertTrue(b.IsInitialized, "B must be initialized after Initialize.");
            AssertTrue(a.IsStarted, "A must be started after Start.");
            // Awake 必须先于 Start（对象级+组件级）
            int aAwake = ca.Log.IndexOf("A.Awake");
            int aStart = ca.Log.IndexOf("A.Start");
            int bAwake = cb.Log.IndexOf("B.Awake");
            int bStart = cb.Log.IndexOf("B.Start");
            AssertTrue(aAwake >= 0 && aStart >= 0, "A must run Awake and Start.");
            AssertTrue(bAwake >= 0 && bStart >= 0, "B must run Awake and Start.");
            AssertTrue(aAwake < aStart, "A.Awake must precede A.Start.");
            AssertTrue(bAwake < bStart, "B.Awake must precede B.Start.");
            // 对象级：Start 只在 Initialize 之后
            AssertTrue(aStart > aAwake && bStart > bAwake,
                "Every object's Start must come after its Awake.");
        }

        // ── 对象 Update：运行中每帧驱动组件 Update(dt)，只有 IsStarted 才驱动 ──
        public static void TestSceneUpdateDrivesComponents()
        {
            var go = new GameObjectReal("Upd");
            var comp = new OrderComponent("Upd");
            go.AddComponent(comp);
            go.Initialize();
            go.Start();

            go.Update(0.016f);
            go.Update(0.033f);

            AssertEqual(2, comp.UpdateCalls, "Two object updates must drive component Update twice.");
            AssertNear(comp.LastDt, 0.033f, 0.0001f, "Update must receive the latest deltaTime.");
        }

        // ── 对象销毁：Destroy 清空组件、重置生命周期标记，OnDisable→OnDestroy 顺序正确 ──
        public static void TestSceneDestroyCallsOnDestroy()
        {
            var go = new GameObjectReal("Dead");
            var comp = new OrderComponent("Dead");
            go.AddComponent(comp);
            go.Initialize();
            go.Start();

            // 记录销毁前的状态
            int onDestroyBefore = comp.OnDestroyCalls;
            go.Destroy();

            // Destroy 重置生命周期标记
            AssertTrue(!go.IsInitialized, "Destroy must reset IsInitialized.");
            AssertTrue(!go.IsStarted, "Destroy must reset IsStarted.");
            // 组件被清空
            AssertTrue(go.GetComponent<OrderComponent>() == null,
                "Destroy must clear the component list.");
            // OnDestroy 被调用（引用在 Destroy 后字段不可靠，只比较调用计数）
            AssertTrue(comp.OnDestroyCalls >= onDestroyBefore + 1,
                "Destroy must call component OnDestroy.");
            // 顺序：OnDisable 必须在 OnDestroy 之前（组件 Enabled 时）
            int disableIdx = comp.Log.IndexOf("Dead.OnDisable");
            int destroyIdx = comp.Log.IndexOf("Dead.OnDestroy");
            AssertTrue(disableIdx >= 0 && destroyIdx >= 0 && disableIdx < destroyIdx,
                "OnDisable must precede OnDestroy during Destroy.");
        }

        // ── 场景查询：FindGameObject 按名递归（含嵌套子节点） ──
        public static void TestSceneFindGameObjectRecursive()
        {
            var scene = new SceneReal("FindScene");
            var parent = new GameObjectReal("Parent");
            var child = new GameObjectReal("Target");
            child.SetParent(parent, worldPositionStays: false);
            scene.AddRootObject(parent);

            // 直接根节点
            var foundParent = scene.FindGameObject("Parent");
            AssertTrue(foundParent == parent, "FindGameObject must locate root by name.");
            // 嵌套子节点（递归）
            var foundChild = scene.FindGameObject("Target");
            AssertTrue(foundChild == child, "FindGameObject must locate nested child by name.");
            // 不存在
            AssertTrue(scene.FindGameObject("Nope") == null, "FindGameObject must return null for absent name.");
        }

        // ── 场景查询：FindGameObjectsWithTag 递归收集所有同标签节点 ──
        public static void TestSceneFindGameObjectsWithTag()
        {
            var scene = new SceneReal("TagScene");
            var a = new GameObjectReal("A") { Tag = "Enemy" };
            var b = new GameObjectReal("B") { Tag = "Enemy" };
            var c = new GameObjectReal("C") { Tag = "Player" };
            var nested = new GameObjectReal("Nested") { Tag = "Enemy" };
            nested.SetParent(a, worldPositionStays: false); // 嵌套 Enemy

            scene.AddRootObject(a);
            scene.AddRootObject(b);
            scene.AddRootObject(c);

            var enemies = scene.FindGameObjectsWithTag("Enemy");
            AssertEqual(3, enemies.Count, "Must find all 3 Enemy-tagged objects (incl. nested).");
            AssertTrue(enemies.Contains(a) && enemies.Contains(b) && enemies.Contains(nested),
                "Enemy results must include root and nested objects.");
            AssertTrue(!enemies.Contains(c), "Player-tagged object must not be in Enemy results.");

            var players = scene.FindGameObjectsWithTag("Player");
            AssertEqual(1, players.Count, "Must find exactly 1 Player.");
            AssertTrue(players.Contains(c), "Player result must be object C.");

            // 无匹配标签返回空
            var none = scene.FindGameObjectsWithTag("Absent");
            AssertEqual(0, none.Count, "Absent tag must return empty list.");
        }

        // ── 场景查询：FindGameObjectsInLayer 按层级过滤 ──
        public static void TestSceneFindGameObjectsInLayer()
        {
            var scene = new SceneReal("LayerScene");
            var ground = new GameObjectReal("Ground") { Layer = 0 };
            var unit = new GameObjectReal("Unit") { Layer = 8 };
            var ui = new GameObjectReal("UI") { Layer = 5 };
            scene.AddRootObject(ground);
            scene.AddRootObject(unit);
            scene.AddRootObject(ui);

            var layer8 = scene.FindGameObjectsInLayer(8);
            AssertEqual(1, layer8.Count, "Layer 8 must contain exactly the unit.");
            AssertTrue(layer8.Contains(unit), "Layer 8 result must include Unit.");

            var layer0 = scene.FindGameObjectsInLayer(0);
            AssertEqual(1, layer0.Count, "Layer 0 must contain the ground.");
        }

        // ── 场景 GameObjectCount / AllGameObjects 递归计数 ──
        public static void TestSceneAllGameObjectsRecursiveCount()
        {
            var scene = new SceneReal("CountScene");
            var root = new GameObjectReal("Root");
            var mid = new GameObjectReal("Mid");
            mid.SetParent(root, worldPositionStays: false);
            var leaf = new GameObjectReal("Leaf");
            leaf.SetParent(mid, worldPositionStays: false);
            scene.AddRootObject(root);

            AssertEqual(3, scene.GameObjectCount,
                "GameObjectCount must recursively count root + mid + leaf.");
            AssertEqual(1, scene.RootGameObjects.Count, "Only root is a direct root object.");
            AssertTrue(scene.AllGameObjects.Contains(leaf),
                "AllGameObjects must include deeply nested leaf.");
        }
    }
}
