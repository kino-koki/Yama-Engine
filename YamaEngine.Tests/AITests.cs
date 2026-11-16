using System;
using System.Linq;
using System.Collections.Generic;
using System.Numerics;
using YamaEngine.AI;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests
{
    // ══════════════════════════════════════════════════════════════
    // AIDepth — UE5.8 级 GOAP 规划确定性深度验证
    // ══════════════════════════════════════════════════════════════

    public static class AITests
    {
        private static void AssertNearValue(float got, float want, float tol, string msg)
        {
            AssertTrue(MathF.Abs(got - want) <= tol,
                $"{msg} (got {got:F4}, want {want:F4}±{tol:F4})");
        }
        // GOAP 世界状态语义：DistanceTo 汉明距离、Satisfies 前置判断、ApplyEffects 叠加效果
        public static void TestGoapWorldStateSemantics()
        {
            var a = new WorldState();
            a.SetBool("hasWeapon", true);
            a.SetInt("ammo", 10);
            var b = new WorldState();
            b.SetBool("hasWeapon", true);
            b.SetInt("ammo", 3);
            var c = new WorldState();
            c.SetBool("hasWeapon", false);
            c.SetInt("ammo", 10);
            // 相同状态距离 0
            AssertTrue(a.DistanceTo(b) == 1, "States differing only in ammo should have distance 1.");
            AssertTrue(a.DistanceTo(c) == 1, "States differing only in hasWeapon should have distance 1.");
            AssertTrue(a.DistanceTo(a) == 0, "A state should have zero distance to itself.");
            // Satisfies：a 满足要求 hasWeapon=true 的前置
            var pre = new WorldState();
            pre.SetBool("hasWeapon", true);
            AssertTrue(a.Satisfies(pre), "a (hasWeapon=true) should satisfy precondition (hasWeapon=true).");
            AssertTrue(!c.Satisfies(pre), "c (hasWeapon=false) should NOT satisfy precondition (hasWeapon=true).");
            // ApplyEffects：叠加效果返回新状态
            var effects = new WorldState();
            effects.SetInt("ammo", 99);
            effects.SetBool("isArmed", true);
            var combined = a.ApplyEffects(effects);
            AssertTrue(combined.GetInt("ammo") == 99, "ApplyEffects should update ammo from effects.");
            AssertTrue(combined.GetBool("isArmed"), "ApplyEffects should add isArmed flag.");
            AssertTrue(combined.GetBool("hasWeapon"), "ApplyEffects should preserve original hasWeapon.");
            AssertTrue(a.GetInt("ammo") == 10, "ApplyEffects should not mutate the original state (a).");
        }

        // GOAP 规划器：按前置条件依赖链生成有序动作序列
        public static void TestGoapPlannerGeneratesOrderedPlan()
        {
            // 目标：hasWeapon=true 且 ammo>0。初始什么都没有。
            var start = new WorldState();
            start.SetBool("hasWeapon", false);
            start.SetInt("ammo", 0);
            var ctx = new GOAPContext(agentId: 1, start);

            var goal = new GOAPGoalV2("ArmSelf");
            goal.DesiredState.SetBool("hasWeapon", true);
            goal.DesiredState.SetInt("ammo", 1);

            // 动作1：拾取武器（前置无，效果 hasWeapon=true）
            var pickup = new GOAPActionV2("PickupWeapon");
            pickup.Effects.SetBool("hasWeapon", true);
            pickup.BaseCost = 2f;
            // 动作2：装填（前置 hasWeapon=true，效果 ammo=1）
            var load = new GOAPActionV2("ReloadAmmo");
            load.Preconditions.SetBool("hasWeapon", true);
            load.Effects.SetInt("ammo", 1);
            load.BaseCost = 1f;

            var planner = new GOAPPlannerV2();
            planner.RegisterAction(pickup);
            planner.RegisterAction(load);
            var plan = planner.Plan(goal, ctx);
            AssertTrue(plan != null, "Planner should find a plan to satisfy the goal.");
            if (plan == null) return;
            AssertTrue(plan.Count >= 1, "Plan should contain at least one action.");
            // 顺序正确：拾取武器必在装填之前（装填依赖 hasWeapon）
            int pickupIdx = -1, loadIdx = -1;
            for (int i = 0; i < plan.Count; i++)
            {
                if (plan[i].Name == "PickupWeapon") pickupIdx = i;
                if (plan[i].Name == "ReloadAmmo") loadIdx = i;
            }
            AssertTrue(pickupIdx >= 0, "Plan should include PickupWeapon action.");
            AssertTrue(loadIdx >= 0, "Plan should include ReloadAmmo action.");
            AssertTrue(pickupIdx < loadIdx, "PickupWeapon (prerequisite for ReloadAmmo) must come before it.");
        }

        // 转向行为 Seek：静止 agent 在 +Z 方向产生朝向目标的期望速度
        public static void TestSeekBehaviorDrivesTowardTarget()
        {
            var agent = new SteeringAgent(1) { Position = new Vector3(0, 0, 0), Velocity = Vector3.Zero, MaxSpeed = 10f };
            var seek = new SeekBehavior(new Vector3(0, 0, 5f));
            var force = seek.Calculate(agent, dt: 0.016f);
            // 静止 agent：force = desired(全速朝目标) - 0 = (0,0,MaxSpeed)
            AssertNearValue(force.X, 0f, 0.001f, "Seek should not apply lateral force.");
            AssertNearValue(force.Z, 10f, 0.001f, "Seek at rest should produce full-speed steering toward target.");
        }

        // Arrival：远距全速（同 Seek），近距减速，停止半径内反向力刹停
        public static void TestArrivalBehaviorSlowsNearTarget()
        {
            var far = new SteeringAgent(1) { Position = new Vector3(0, 0, 0), Velocity = Vector3.Zero, MaxSpeed = 10f };
            var arrival = new ArrivalBehavior(new Vector3(0, 0, 20f)) { SlowRadius = 3f, StopRadius = 0.2f };
            float farForce = arrival.Calculate(far, 0.016f).Z;
            AssertNearValue(farForce, 10f, 0.001f, "Arrival far from target should apply full speed.");

            // 距目标 1.5（<SlowRadius=3）：线性减速 speed=MaxSpeed*(dist/SlowRadius)
            var near = new SteeringAgent(2) { Position = new Vector3(0, 0, 18.5f), Velocity = Vector3.Zero, MaxSpeed = 10f };
            float nearForce = arrival.Calculate(near, 0.016f).Z;
            AssertNearValue(nearForce, 10f * (1.5f / 3f), 0.001f, "Arrival within slow radius should decelerate linearly.");

            // 停止半径内：反向刹停力（-Velocity）
            var stopped = new SteeringAgent(3) { Position = new Vector3(0, 0, 19.95f), Velocity = new Vector3(0, 0, 5f), MaxSpeed = 10f };
            var stopForce = arrival.Calculate(stopped, 0.016f);
            AssertNearValue(stopForce.Z, -5f, 0.001f, "Arrival inside stop radius should brake (negate velocity).");
        }

        // Flocking 分离行为：反平方斥力推开邻居，方向背离邻居
        public static void TestSeparationBehaviorPushesAwayFromNeighbors()
        {
            var agent = new SteeringAgent(1) { Position = new Vector3(0, 0, 0), Velocity = Vector3.Zero, MaxSpeed = 5f };
            var neighbor = new SteeringAgent(2) { Position = new Vector3(1f, 0, 0), Velocity = Vector3.Zero };
            var sep = new SeparationBehavior { Neighbors = { agent, neighbor }, NeighborRadius = 5f };
            var force = sep.Calculate(agent, 0.016f);
            // 邻居在 +X，分离力应指向 -X（背离）
            AssertTrue(force.X < 0f, "Separation should push agent away from neighbor (negative X).");
            // 无人时零力
            var lonely = new SteeringAgent(3) { Position = Vector3.Zero, Velocity = Vector3.Zero, MaxSpeed = 5f };
            var sepEmpty = new SeparationBehavior { Neighbors = { lonely }, NeighborRadius = 5f };
            AssertTrue(sepEmpty.Calculate(lonely, 0.016f) == Vector3.Zero,
                "Separation with no neighbors should produce zero force.");
        }

        // Flocking 对齐行为：取邻居平均速度方向
        public static void TestAlignmentBehaviorMatchesNeighborVelocity()
        {
            var agent = new SteeringAgent(1) { Position = new Vector3(0, 0, 0), Velocity = Vector3.Zero, MaxSpeed = 5f };
            var n1 = new SteeringAgent(2) { Position = new Vector3(1f, 0, 0), Velocity = new Vector3(0, 0, 3f) };
            var n2 = new SteeringAgent(3) { Position = new Vector3(-1f, 0, 0), Velocity = new Vector3(0, 0, 3f) };
            var align = new AlignmentBehavior { Neighbors = { agent, n1, n2 }, NeighborRadius = 5f };
            var force = align.Calculate(agent, 0.016f);
            // 邻居平均速度 (0,0,3)，归一化×MaxSpeed(5) → force=(0,0,5)-0
            AssertNearValue(force.Z, 5f, 0.001f, "Alignment should steer toward normalized average neighbor velocity.");
            AssertNearValue(force.X, 0f, 0.001f, "Alignment lateral force should cancel (symmetric neighbors).");
        }

        // Flocking 聚合行为：朝邻居质心靠拢
        public static void TestCohesionBehaviorSeeksGroupCenter()
        {
            var agent = new SteeringAgent(1) { Position = new Vector3(0, 0, 0), Velocity = Vector3.Zero, MaxSpeed = 5f };
            // 两个邻居在 +X 两侧，质心 (3,0,0)
            var n1 = new SteeringAgent(2) { Position = new Vector3(2f, 0, 0), Velocity = Vector3.Zero };
            var n2 = new SteeringAgent(3) { Position = new Vector3(4f, 0, 0), Velocity = Vector3.Zero };
            var coh = new CohesionBehavior { Neighbors = { agent, n1, n2 }, NeighborRadius = 10f };
            var force = coh.Calculate(agent, 0.016f);
            // 质心在 +X → 力应指向 +X
            AssertTrue(force.X > 0f, "Cohesion should steer agent toward group center (+X).");
            AssertNearValue(force.Z, 0f, 0.001f, "Cohesion should have no lateral (Z) force for collinear neighbors.");
        }

        // AI 路径优化：拐角检测（按转角阈值）+ 曲折度（实际长度/直线距离）
        public static void TestPathOptimizationCornersAndTortuosity()
        {
            // L 形路径：3 个点，中点是 90° 直角拐角
            var path = new List<Vector3>
            {
                new Vector3(0f, 0f, 0f),
                new Vector3(2f, 0f, 0f),   // 拐角点（直角）
                new Vector3(2f, 0f, 2f),
            };
            var sharpCorners = PathOptimizationUtils.FindSharpCorners(path, thresholdDeg: 45f);
            AssertTrue(sharpCorners.Count == 1 && sharpCorners[0] == 1,
                "L-shaped path should detect the single 90-degree corner at index 1.");

            // 直线路径：无拐角
            var straight = new List<Vector3>
            {
                new Vector3(0f, 0f, 0f),
                new Vector3(1f, 0f, 0f),
                new Vector3(2f, 0f, 0f),
            };
            AssertTrue(PathOptimizationUtils.FindSharpCorners(straight, 45f).Count == 0,
                "Straight path should have no sharp corners.");

            // 曲折度：直线 = 1.0
            AssertNearValue(PathOptimizationUtils.Tortuosity(straight), 1f, 0.001f,
                "Straight path tortuosity should equal 1.0.");
            // L 形：总长 2+2=4，直线 2√2≈2.83 → 曲折度≈1.41
            AssertNearValue(PathOptimizationUtils.Tortuosity(path), 4f / (2f * MathF.Sqrt(2f)), 0.001f,
                "L-shaped path tortuosity should be total length over straight-line distance.");
        }
    }
}
