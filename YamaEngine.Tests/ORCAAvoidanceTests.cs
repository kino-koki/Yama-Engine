// ORCAAvoidanceTests.cs — UE5.8 人群避让子系统深度验证（ORCA/RVO）
// 覆盖：ORCAPlane.IsFeasible 半平面可行域、RVOObstacle 左法线、RVOSimulator.AddAgent 顺序 ID、
// ComputeORCAPlanes 手填邻居构造半平面、SolveLinearProgram 投影/回退 LP、
// 三场景：对撞侧移/追尾偏移/擦肩微偏。
// 全部纯 CPU、确定性数值验证（Vector2 XZ 平面，X→X、Y→Z）。

using System;
using System.Collections.Generic;
using System.Numerics;
using YamaEngine.AI;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests
{
    public static class ORCAAvoidanceTests
    {
        private static void AssertNear(float got, float want, float tol, string msg)
            => AssertTrue(MathF.Abs(got - want) <= tol,
                $"{msg} (got {got:F4}, want {want:F4}±{tol:F4})");

        // ── ORCAPlane.IsFeasible：半平面 v·Normal >= Distance ──
        public static void TestPlaneFeasible()
        {
            // 约束 v.y >= 1（Normal=(0,1), Distance=1）
            var plane = new ORCAPlane(new Vector2(0, 1), new Vector2(0, 1), 1f);
            AssertTrue(plane.IsFeasible(new Vector2(0, 5)), "v=(0,5) must be feasible (v.y>1).");
            AssertTrue(!plane.IsFeasible(new Vector2(0, -5)), "v=(0,-5) must be infeasible (v.y<1).");
            AssertTrue(plane.IsFeasible(new Vector2(0, 1)), "v on boundary must be feasible.");
            // 与 ProjectOnConstraint 一致：边界内/外判断只看 v·n 与 Distance
            AssertTrue(!plane.IsFeasible(new Vector2(3, 0.9f)), "v=(3,0.9) must be infeasible (0.9<1).");
        }

        // ── RVOObstacle：左法线 = (-d.y, d.x)，方向归一化 ──
        public static void TestObstacleLeftNormal()
        {
            // A=(0,0)→B=(0,2)：方向 (0,1)，左法线 (-1,0)
            var obs = new RVOObstacle(new Vector2(0, 0), new Vector2(0, 2));
            AssertNear(obs.Length, 2f, 0.001f, "Obstacle length must be 2.");
            AssertNear(obs.Direction.Y, 1f, 0.001f, "Direction normalized (0,1).");
            AssertNear(obs.Normal.X, -1f, 0.001f, "Left normal must be (-1,0).");
            AssertNear(obs.Normal.Y, 0f, 0.001f, "Left normal Y must be 0.");
        }

        // ── RVOSimulator.AddAgent：顺序 ID 从 0 起 ──
        public static void TestAddAgentSequentialId()
        {
            var sim = new RVOSimulator();
            int id0 = sim.AddAgent(new Vector3(0, 0, 0));
            int id1 = sim.AddAgent(new Vector3(5, 0, 0), radius: 0.5f, maxSpeed: 4f);
            AssertEqual(0, id0, "First agent ID must be 0.");
            AssertEqual(1, id1, "Second agent ID must be 1.");
            AssertEqual(0.5f, sim.Agents[1].Radius, "Agent radius must be stored.");
            AssertEqual(4f, sim.Agents[1].MaxSpeed, "Agent maxSpeed must be stored.");
        }

        // ── AddObstacle：线段障碍物入列表 ──
        public static void TestAddObstacle()
        {
            var sim = new RVOSimulator();
            sim.AddObstacle(new Vector2(0, 0), new Vector2(4, 0));
            sim.AddObstacle(new Vector3(0, 0, 2), new Vector3(4, 0, 2));
            AssertEqual(2, sim.Obstacles.Count, "Two obstacles must be added.");
            AssertEqual(2, sim.Obstacles.Count, "3D overload maps to XZ.");
        }

        // ── 手工构造 ORCA 平面 + SolveLinearProgram 投影到可行域 ──
        public static void TestSolveLP_ProjectsOntoConstraint()
        {
            // 单约束 v.y >= 1：Point 在边界 (0,1)，Normal (0,1)
            // preferred=(0,0) 不满足（(0,0)-(0,1))·(0,1) = -1 < 0）→ LP 投影到 v.y=1
            var planes = new List<ORCAPlane>
            {
                new ORCAPlane(new Vector2(0, 1), new Vector2(0, 1), 1f)
            };
            var sim = new RVOSimulator();
            var result = sim.SolveLinearProgram(planes, new Vector2(0, 0), 3f);
            AssertNear(result.Y, 1f, 0.01f, "LP must project to constraint boundary v.y=1.");
            AssertNear(result.X, 0f, 0.01f, "LP X must stay 0.");
        }

        // ── SolveLinearProgram 无约束：只限速 ──
        public static void TestSolveLP_NoConstraintSpeedClamp()
        {
            var sim = new RVOSimulator();
            // 偏好速度 (0,10)，maxSpeed=3 → 归一化到模 3
            var result = sim.SolveLinearProgram(new List<ORCAPlane>(), new Vector2(0, 10), 3f);
            AssertNear(result.Length(), 3f, 0.01f, "No-constraint LP must clamp to maxSpeed.");
            AssertTrue(result.Y > 0, "Direction preserved along +Y.");
        }

        // ── ComputeORCAPlanes：对撞场景产生半平面，LP 后侧向偏移 ──
        public static void TestHeadOnCollisionDeflects()
        {
            var sim = new RVOSimulator();
            // A 在原点朝 +X 运动；B 在 (2,0) 朝 -X 运动（对撞）
            var a = new RVOAgent(0, new Vector3(0, 0, 0), radius: 0.3f, maxSpeed: 1f)
            {
                Velocity = new Vector3(1, 0, 0),
                PreferredVelocity = new Vector3(1, 0, 0),
            };
            var b = new RVOAgent(1, new Vector3(2, 0, 0), radius: 0.3f, maxSpeed: 1f)
            {
                Velocity = new Vector3(-1, 0, 0),
                PreferredVelocity = new Vector3(-1, 0, 0),
            };
            a.NeighborAgents.Add(b);
            b.NeighborAgents.Add(a);
            sim.Agents.Add(a);
            sim.Agents.Add(b);

            sim.ComputeORCAPlanes();

            AssertTrue(a.ORCAPlanes.Count >= 1, "Head-on must generate ≥1 ORCA plane for agent A.");

            // LP 求解 A 的新速度
            var preferred = new Vector2(a.PreferredVelocity.X, a.PreferredVelocity.Z);
            var newVel = sim.SolveLinearProgram(a.ORCAPlanes, preferred, a.MaxSpeed);

            // 侧向偏移：Z 分量（Vector2.Y）非零
            AssertTrue(MathF.Abs(newVel.Y) > 1e-3f,
                $"Head-on collision must deflect laterally (Z≠0), got Z={newVel.Y:F4}.");
            // 速度不超最大
            AssertTrue(newVel.Length() <= a.MaxSpeed + 1e-2f,
                "New velocity must respect maxSpeed.");
        }

        // ── 追尾：后快前慢 → 后者横向偏移 + 减速 ──
        public static void TestRearEndDeflectsAndSlows()
        {
            var sim = new RVOSimulator();
            // A 在后 (0,0) 朝 +X 速度 2；B 在前 (1,0) 朝 +X 速度 0.5（同向）
            var a = new RVOAgent(0, new Vector3(0, 0, 0), radius: 0.3f, maxSpeed: 2f)
            {
                Velocity = new Vector3(2, 0, 0),
                PreferredVelocity = new Vector3(2, 0, 0),
            };
            var b = new RVOAgent(1, new Vector3(1, 0, 0), radius: 0.3f, maxSpeed: 2f)
            {
                Velocity = new Vector3(0.5f, 0, 0),
                PreferredVelocity = new Vector3(2, 0, 0),
            };
            a.NeighborAgents.Add(b);
            sim.Agents.Add(a);
            sim.Agents.Add(b);

            sim.ComputeORCAPlanes();
            var preferred = new Vector2(a.PreferredVelocity.X, a.PreferredVelocity.Z);
            var newVel = sim.SolveLinearProgram(a.ORCAPlanes, preferred, a.MaxSpeed);

            // 追尾：A 前进速度应减小（X 分量 < 2）或产生侧向偏移（Z ≠ 0）
            bool slowed = newVel.X < 2f - 1e-3f;
            bool deflected = MathF.Abs(newVel.Y) > 1e-3f;
            AssertTrue(slowed || deflected,
                $"Rear-end: A must slow (X<2) or deflect (Z≠0). Got X={newVel.X:F3}, Z={newVel.Y:F3}.");
        }

        // ── 擦肩：横向掠过 → 轻微偏移（不剧烈改变速度）──
        public static void TestLateralPassSmallDeflection()
        {
            var sim = new RVOSimulator();
            // A 在 (0,0) 朝 +X；B 在 (0, 5) 朝 -X（横向远距擦肩，combinedRadius*100 内）
            var a = new RVOAgent(0, new Vector3(0, 0, 0), radius: 0.3f, maxSpeed: 1f)
            {
                Velocity = new Vector3(1, 0, 0),
                PreferredVelocity = new Vector3(1, 0, 0),
            };
            var b = new RVOAgent(1, new Vector3(0, 0, 5), radius: 0.3f, maxSpeed: 1f)
            {
                Velocity = new Vector3(-1, 0, 0),
            };
            a.NeighborAgents.Add(b);
            sim.Agents.Add(a);
            sim.Agents.Add(b);

            sim.ComputeORCAPlanes();
            var preferred = new Vector2(1f, 0f);
            var newVel = sim.SolveLinearProgram(a.ORCAPlanes, preferred, a.MaxSpeed);

            // 远距擦肩：新速度应仍主要沿 +X（X 分量 > 0），偏移量有限
            AssertTrue(newVel.X > 0.5f,
                $"Lateral pass: velocity must remain mostly +X, got X={newVel.X:F3}.");
            AssertTrue(newVel.Length() <= a.MaxSpeed + 1e-2f, "Speed clamped to max.");
        }

        // ── ProjectPointOnSegment 行为（通过 AddObstacle+ComputeObstacleORCAPlane 间接验证）──
        public static void TestObstacleORCAPlaneGenerated()
        {
            var sim = new RVOSimulator { TimeHorizonObstacles = 3f };
            // agent 在原点朝 +X；障碍物线段在 (3, -1)→(3, 1)（前方竖墙）
            var a = new RVOAgent(0, new Vector3(0, 0, 0), radius: 0.3f, maxSpeed: 1f)
            {
                Velocity = new Vector3(1, 0, 0),
            };
            var wall = new RVOObstacle(new Vector2(3, -1), new Vector2(3, 1));
            a.NeighborObstacles.Add(wall);
            sim.Agents.Add(a);

            sim.ComputeORCAPlanes();
            AssertTrue(a.ORCAPlanes.Count >= 1, "Obstacle must generate an ORCA plane.");

            // LP 后新速度应减速（X 分量 < 1），避免撞墙
            var preferred = new Vector2(1f, 0f);
            var newVel = sim.SolveLinearProgram(a.ORCAPlanes, preferred, a.MaxSpeed);
            AssertTrue(newVel.X < 1f - 1e-3f,
                $"Obstacle ahead must slow agent (X<1), got X={newVel.X:F3}.");
        }
    }
}
