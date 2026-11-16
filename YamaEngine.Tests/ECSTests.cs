using System;
using System.Collections.Generic;
using System.Numerics;
using YamaEngine.ECS;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests
{
    // ══════════════════════════════════════════════════════════════
    // ECSDepth — UE5.8 级 ECS 核心深度验证
    // 覆盖：EntitySparseSet 稀疏集 swap-remove 语义 + ID 回收/version 失效
    //       SystemScheduler Kahn 拓扑排序 + 依赖链确定性执行顺序
    // ══════════════════════════════════════════════════════════════
    public static class ECSTests
    {
        // 测试用组件（struct）
        public struct TestPosition { public Vector3 Value; }
        public struct TestVelocity { public Vector3 Value; }
        public struct TestTag { public int Id; }

        // ── 系统记录器：记录 OnUpdate 调用顺序到共享列表 ──
        private class OrderRecordingSystem : SystemBaseReal
        {
            private readonly List<string> _order;
            private readonly string _name;
            protected OrderRecordingSystem(string name, List<string> order)
            {
                _name = name;
                _order = order;
                Name = name;
            }
            public override void OnUpdate(float dt) => _order.Add(_name);
        }

        // ── EntitySparseSet swap-remove：销毁中间实体后 dense 无洞、末尾实体搬迁 ──
        public static void TestSparseSetSwapRemovePreservesDense()
        {
            using var ss = new EntitySparseSet();
            var a = ss.CreateEntity();
            var b = ss.CreateEntity();
            var c = ss.CreateEntity();
            ss.AddComponent(a, new TestPosition { Value = new Vector3(1, 0, 0) });
            ss.AddComponent(b, new TestPosition { Value = new Vector3(2, 0, 0) });
            ss.AddComponent(c, new TestPosition { Value = new Vector3(3, 0, 0) });
            AssertTrue(ss.Count == 3, "Should have 3 alive entities before destroy.");

            // 销毁中间实体 b → 末尾 c 搬到 b 的位置（swap-remove）
            AssertTrue(ss.DestroyEntity(b), "DestroyEntity should return true for alive entity.");
            AssertTrue(ss.Count == 2, "Count should drop to 2 after destroy.");
            // a, c 仍存活，b 已失效
            AssertTrue(ss.Contains(a), "a should remain alive.");
            AssertTrue(ss.Contains(c), "c should remain alive after being swapped.");
            AssertTrue(!ss.Contains(b), "b handle should be invalid after destroy.");
            // dense 区无洞：GetAllEntities 长度 == Count
            AssertTrue(ss.GetAllEntities().Length == 2, "Dense array should have no holes (length==Count).");
            // 搬迁后的 c 数据一致
            ref var cPos = ref ss.GetComponent<TestPosition>(c);
            AssertTrue(cPos.Value.X == 3f, "Swapped entity c should retain its component data.");
        }

        // ── EntitySparseSet ID 回收 + version 失效：旧 handle 不可用 ──
        public static void TestSparseSetVersionInvalidationAndRecycle()
        {
            using var ss = new EntitySparseSet();
            var e1 = ss.CreateEntity();
            int id1 = e1.Id;
            uint v1 = e1.Version;
            ss.AddComponent(e1, new TestTag { Id = 100 });

            // 销毁后 ID 入回收队列，version 递增
            ss.DestroyEntity(e1);
            AssertTrue(!ss.Contains(e1), "Old handle must be invalid after destroy.");

            // 新建实体应回收同一 ID，但 version 递增
            var e2 = ss.CreateEntity();
            AssertTrue(e2.Id == id1, "Recycled ID should be reused for new entity.");
            AssertTrue(e2.Version != v1, "New entity version must differ from destroyed handle's version.");
            AssertTrue(ss.Contains(e2), "New handle should be valid.");
            // 新实体的组件是干净的（不带旧数据）
            AssertTrue(!ss.HasComponent<TestTag>(e2),
                "Recycled entity should not inherit stale components from destroyed entity.");
        }

        // ── 用具体类型定义依赖（泛型 AddDependency 需要具体 SystemBaseReal 子类）──
        private class SysInput : OrderRecordingSystem
        {
            public SysInput(List<string> o) : base("Input", o) { }
        }
        private class SysPhysics : OrderRecordingSystem
        {
            public SysPhysics(List<string> o) : base("Physics", o) { }
        }
        private class SysRender : OrderRecordingSystem
        {
            public SysRender(List<string> o) : base("Render", o) { }
        }

        // 拓扑排序真实测试：Input → Physics → Render 依赖链
        public static void TestSchedulerRespectsDependencyChain()
        {
            var order = new List<string>();
            using var sched = new SystemScheduler { EnableParallelExecution = false };
            // 逆序注册，验证拓扑排序仍按依赖执行
            sched.AddSystem(new SysRender(order));
            sched.AddSystem(new SysPhysics(order));
            sched.AddSystem(new SysInput(order));
            // 依赖：Input 先于 Physics，Physics 先于 Render
            sched.AddDependency<SysInput, SysPhysics>();
            sched.AddDependency<SysPhysics, SysRender>();

            sched.Update(0.016f);
            AssertTrue(order.Count == 3, "All three systems should have ticked exactly once.");
            AssertTrue(order[0] == "Input", "Input must run first (dependency root).");
            AssertTrue(order[1] == "Physics", "Physics must run after Input.");
            AssertTrue(order[2] == "Render", "Render must run last (dependency sink).");
        }

        // ── SystemScheduler 无依赖系统按注册顺序执行（无约束时保持稳定顺序）──
        public static void TestSchedulerNoDependencyKeepsRegistrationOrder()
        {
            var order = new List<string>();
            using var sched = new SystemScheduler { EnableParallelExecution = false };
            sched.AddSystem(new SysInput(order));
            sched.AddSystem(new SysPhysics(order));
            sched.AddSystem(new SysRender(order));
            sched.Update(0.016f);
            AssertTrue(order.Count == 3, "All systems should tick.");
            AssertTrue(order[0] == "Input" && order[1] == "Physics" && order[2] == "Render",
                "Without dependencies, systems should execute in registration order.");
        }

        // ── SystemScheduler 禁用系统跳过执行 ──
        public static void TestSchedulerSkipsDisabledSystem()
        {
            var order = new List<string>();
            using var sched = new SystemScheduler { EnableParallelExecution = false };
            sched.AddSystem(new SysInput(order));
            sched.AddSystem(new SysPhysics(order));
            sched.SetSystemEnabled<SysPhysics>(false);
            sched.Update(0.016f);
            AssertTrue(order.Count == 1, "Only enabled systems should tick.");
            AssertTrue(order[0] == "Input", "Disabled Physics should be skipped, only Input runs.");
        }

        // ══════════════════════════════════════════════════════════════
        // EntityManagerReal — 主世界/实体管理器（90+ 文件引用的主路径）
        // ══════════════════════════════════════════════════════════════

        // ── 实体生命周期 + 组件增删改查 + 事件触发 ──
        public static void TestEntityManagerLifecycleComponentsEvents()
        {
            var em = new EntityManagerReal();
            int createdCount = 0, destroyedCount = 0;
            em.EntityCreated += _ => createdCount++;
            em.EntityDestroyed += _ => destroyedCount++;

            var e = em.CreateEntity("Player");
            AssertTrue(em.IsValid(e), "Newly created entity should be valid.");
            AssertTrue(createdCount == 1, "EntityCreated event should fire once on CreateEntity.");
            AssertTrue(em.GetName(e) == "Player", "GetName should return assigned name.");

            // 组件增删查
            em.AddComponent(e, new TestPosition { Value = new Vector3(1, 2, 3) });
            AssertTrue(em.HasComponent<TestPosition>(e), "HasComponent should be true after AddComponent.");
            var pos = em.GetComponentData<TestPosition>(e);
            AssertTrue(pos.Value == new Vector3(1, 2, 3), "GetComponentData should return stored value.");

            // 销毁 + 事件
            em.DestroyEntity(e);
            AssertTrue(!em.IsValid(e), "Destroyed entity should be invalid.");
            AssertTrue(destroyedCount == 1, "EntityDestroyed event should fire once on DestroyEntity.");
        }

        // ── 查询过滤 AllOf/NoneOf：按组件掩码筛选实体 ──
        public static void TestEntityManagerQueryAllOfNoneOf()
        {
            var em = new EntityManagerReal();
            var hero = em.CreateEntity("Hero");
            em.AddComponent(hero, new TestPosition { Value = Vector3.Zero });
            em.AddComponent(hero, new TestVelocity { Value = new Vector3(0, 0, 1) });

            var prop = em.CreateEntity("Prop");
            em.AddComponent(prop, new TestPosition { Value = Vector3.Zero });
            // Prop 只有 Position，无 Velocity

            // AllOf<Position> 应匹配两个
            var posQuery = em.CreateQuery(new QueryDescription().AllOf<TestPosition>());
            AssertTrue(posQuery.GetEntityCount() == 2, "AllOf<Position> should match both entities.");

            // AllOf<Position> + AllOf<Velocity> 应只匹配 hero
            var movingQuery = em.CreateQuery(
                new QueryDescription().AllOf<TestPosition>().AllOf<TestVelocity>());
            AssertTrue(movingQuery.GetEntityCount() == 1, "AllOf<Position>+AllOf<Velocity> should match only moving entity.");

            // AllOf<Position> + NoneOf<Velocity> 应只匹配 prop
            var staticQuery = em.CreateQuery(
                new QueryDescription().AllOf<TestPosition>().NoneOf<TestVelocity>());
            AssertTrue(staticQuery.GetEntityCount() == 1, "AllOf<Position>+NoneOf<Velocity> should match only static entity.");

            // 验证匹配的是 prop 而非 hero
            var matched = staticQuery.ToEntityList();
            AssertTrue(matched.Count == 1 && matched[0].Id == prop.Id,
                "Static query should return the prop entity.");
        }

        // ── RemoveComponent 动态改变查询结果（archetype 迁移语义）──
        public static void TestEntityManagerRemoveComponentMigratesArchetype()
        {
            var em = new EntityManagerReal();
            var e = em.CreateEntity("Mover");
            em.AddComponent(e, new TestPosition { Value = Vector3.Zero });
            em.AddComponent(e, new TestVelocity { Value = Vector3.UnitZ });

            var movingQuery = em.CreateQuery(
                new QueryDescription().AllOf<TestPosition>().AllOf<TestVelocity>());
            AssertTrue(movingQuery.GetEntityCount() == 1, "Entity with both components should match moving query.");

            // 移除 Velocity → 应迁出 moving archetype，不再匹配
            AssertTrue(em.RemoveComponent<TestVelocity>(e), "RemoveComponent should return true.");
            AssertTrue(!em.HasComponent<TestVelocity>(e), "Velocity should be gone after removal.");
            // 查询缓存语义：移除组件后计数是陈旧的（缓存直到 Invalidate）
            // 这是 ECS 查询缓存的真实行为——显式失效后才反映最新 archetype 状态
            AssertTrue(movingQuery.GetEntityCount() == 1,
                "Before Invalidate, query returns stale cached count (ECS cache-until-dirty semantics).");
            movingQuery.Invalidate();
            AssertTrue(movingQuery.GetEntityCount() == 0,
                "After Invalidate, moving query should reflect archetype removal (count 0).");
            // Position 仍保留
            AssertTrue(em.HasComponent<TestPosition>(e), "Position should remain after Velocity removal.");
        }
    }
}
