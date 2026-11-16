// IKTests.cs — Animation IK 求解器深度验证
// 覆盖：FABRIK（可达/不可达/多关节收敛/根保持）、TwoBoneIK（解析余弦定理/极向量弯向），
// CCD（链末端逼近目标/骨长保持）、LookAt、SpringIK（阻尼衰减）。
// 全部纯 CPU 纯数学，确定性数值验证。

using System;
using System.Collections.Generic;
using System.Numerics;
using YamaEngine.Animation;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests
{
    public static class IKTests
    {
        private static void AssertNear(float got, float want, float tol, string msg)
            => AssertTrue(MathF.Abs(got - want) <= tol,
                $"{msg} (got {got:F5}, want {want:F5}±{tol:F5})");

        private static void AssertNearV(Vector3 got, Vector3 want, float tol, string msg)
            => AssertTrue((got - want).Length() <= tol,
                $"{msg} (got {got}, want {want})");

        // ── FABRIK：可达目标 → 末端到达，骨长保持 ──
        public static void TestFABRIKReachableTarget()
        {
            // 三关节链在 XZ 平面上，从原点向 +X，骨长各 1
            var init = new Vector3[]
            {
                new Vector3(0, 0, 0),
                new Vector3(1, 0, 0),
                new Vector3(2, 0, 0),
            };
            // 目标在 (1, 1, 0)——距离原点 √2 ≈ 1.414，总骨长 2，可达
            var result = FABRIKSolverReal.Solve(init, new Vector3(1, 1, 0), 20, 0.001f);
            AssertTrue(result.Converged, "FABRIK should converge for reachable target.");
            AssertNear(result.FinalError, 0, 0.001f, "FABRIK final error should be near 0.");
            // 根关节保持不动
            AssertNearV(result.Positions[0], new Vector3(0, 0, 0), 0.001f,
                "FABRIK root must stay at origin.");
            // 骨长保持
            float b0 = (result.Positions[1] - result.Positions[0]).Length();
            float b1 = (result.Positions[2] - result.Positions[1]).Length();
            AssertNear(b0, 1, 0.01f, "FABRIK bone0 length must be preserved.");
            AssertNear(b1, 1, 0.01f, "FABRIK bone1 length must be preserved.");
        }

        // ── FABRIK：不可达目标 → 伸展到直链，Converged=false ──
        public static void TestFABRIKUnreachableStretch()
        {
            var init = new Vector3[]
            {
                new Vector3(0, 0, 0),
                new Vector3(1, 0, 0),
                new Vector3(2, 0, 0),
            };
            // 目标在 (10, 0, 0)，总骨长 2 < 10，不可达
            var result = FABRIKSolverReal.Solve(init, new Vector3(10, 0, 0), 20, 0.001f);
            AssertTrue(!result.Converged, "FABRIK should report not converged for unreachable target.");
            // 末端沿 X 伸展至 ≈ 2
            AssertNear(result.Positions[2].X, 2, 0.01f,
                "FABRIK stretched end effector should be at totalLength along root-target direction.");
            // 骨长仍保持
            float b0 = (result.Positions[1] - result.Positions[0]).Length();
            float b1 = (result.Positions[2] - result.Positions[1]).Length();
            AssertNear(b0, 1, 0.01f, "FABRIK stretched bone0 length preserved.");
            AssertNear(b1, 1, 0.01f, "FABRIK stretched bone1 length preserved.");
        }

        // ── FABRIK：目标已在原末端位置 → 立即收敛 ──
        public static void TestFABRIKAlreadyAtTarget()
        {
            var init = new Vector3[]
            {
                new Vector3(0, 0, 0),
                new Vector3(1, 0, 0),
                new Vector3(2, 0, 0),
            };
            var result = FABRIKSolverReal.Solve(init, new Vector3(2, 0, 0), 10, 0.001f);
            AssertTrue(result.Converged, "FABRIK should converge immediately when target equals end effector.");
            AssertNear(result.Positions[2].X, 2, 0.001f, "End effector should stay at target.");
        }

        // ── TwoBoneIK：可达目标、极向量弯向 +Y → 肘关节偏向 +Y ──
        public static void TestTwoBoneIKReachablePoleY()
        {
            // 肩在原点，臂长各 1，初始末端在 (2, 0, 0)，目标 (1, 1, 0)，极向量 +Y
            var root = new Vector3(0, 0, 0);
            var mid = new Vector3(1, 0, 0);
            var end = new Vector3(2, 0, 0);
            var target = new Vector3(1, 1, 0);
            var pole = new Vector3(0, 1, 0);

            var result = TwoBoneIKSolverReal.Solve(root, mid, end, target, pole);
            // 根不动
            AssertNearV(result[0], root, 0.001f, "TwoBoneIK root unchanged.");
            // 末端到达目标
            AssertNearV(result[2], target, 0.01f, "TwoBoneIK end effector reaches target.");
            // 肘关节偏 +Y（极向量方向）
            AssertTrue(result[1].Y > 0.01f,
                $"TwoBoneIK elbow should bend toward +Y pole (mid.Y={result[1].Y:F4}).");
            // 骨长保持
            float b0 = (result[1] - result[0]).Length();
            float b1 = (result[2] - result[1]).Length();
            AssertNear(b0, 1, 0.01f, "TwoBoneIK bone0 preserved.");
            AssertNear(b1, 1, 0.01f, "TwoBoneIK bone1 preserved.");
        }

        // ── TwoBoneIK：目标太远 → 伸展为直线，末端钳制 ──
        public static void TestTwoBoneIKUnreachableClamps()
        {
            var root = new Vector3(0, 0, 0);
            var mid = new Vector3(1, 0, 0);
            var end = new Vector3(2, 0, 0);
            var target = new Vector3(10, 0, 0); // 距离 10，总骨长 2，不可达
            var pole = new Vector3(0, 1, 0);

            var result = TwoBoneIKSolverReal.Solve(root, mid, end, target, pole);
            // 末端应钳制到 lenA+lenB ≈ 2
            float endDist = (result[2] - result[0]).Length();
            AssertNear(endDist, 2, 0.01f,
                "TwoBoneIK unreachable target: end dist should clamp to lenA+lenB=2.");
        }

        // ── TwoBoneIK：目标在根关节处 → 肘折叠 ──
        public static void TestTwoBoneIKTargetAtRootFolds()
        {
            // 目标距离 root = 0，lenA=lenB=1，|lenA-lenB|=0 → clampedDist≈Eps，链几乎完全折叠
            var root = new Vector3(0, 0, 0);
            var mid = new Vector3(1, 0, 0);
            var end = new Vector3(2, 0, 0);
            var target = new Vector3(0, 0, 0);
            var pole = new Vector3(0, 1, 0);

            var result = TwoBoneIKSolverReal.Solve(root, mid, end, target, pole);
            // 末端应在根附近
            float endDist = (result[2] - result[0]).Length();
            AssertTrue(endDist < 0.2f,
                $"TwoBoneIK target at root: end should be near root (got dist {endDist:F4}).");
        }

        // ── CCD：三关节可达目标收敛 ──
        public static void TestCCDReachableConverges()
        {
            var init = new Vector3[]
            {
                new Vector3(0, 0, 0),
                new Vector3(1, 0, 0),
                new Vector3(2, 0, 0),
            };
            var result = CCDIKSolverReal.Solve(init, new Vector3(1, 1, 0), 30, 0.001f);
            AssertTrue(result.Converged, "CCD should converge for reachable target.");
            AssertNear(result.FinalError, 0, 0.01f, "CCD final error should be near 0.");
            // 根关节保持
            AssertNearV(result.Positions[0], new Vector3(0, 0, 0), 0.001f, "CCD root stays.");
            // 骨长保持
            float b0 = (result.Positions[1] - result.Positions[0]).Length();
            float b1 = (result.Positions[2] - result.Positions[1]).Length();
            AssertNear(b0, 1, 0.01f, "CCD bone0 length preserved.");
            AssertNear(b1, 1, 0.01f, "CCD bone1 length preserved.");
        }

        // ── CCD：不可达目标 → 直线伸展，Converged=false ──
        public static void TestCCDUnreachableStretch()
        {
            var init = new Vector3[]
            {
                new Vector3(0, 0, 0),
                new Vector3(1, 0, 0),
                new Vector3(2, 0, 0),
            };
            var result = CCDIKSolverReal.Solve(init, new Vector3(10, 0, 0), 30, 0.001f);
            AssertTrue(!result.Converged, "CCD should not converge for unreachable target.");
            // 末端应沿 X 伸展到 ≈2
            AssertNear(result.Positions[2].X, 2, 0.05f,
                "CCD stretched end should reach total length along X.");
        }

        // ── FABRIK：4 关节链收敛 + 所有关节骨长保持 ──
        public static void TestFABRIKFourJointPreservesAllLengths()
        {
            var init = new Vector3[]
            {
                new Vector3(0, 0, 0),
                new Vector3(1, 0, 0),
                new Vector3(2, 0, 0),
                new Vector3(3, 0, 0),
            };
            var result = FABRIKSolverReal.Solve(init, new Vector3(2, 2, 0), 30, 0.001f);
            AssertTrue(result.Converged, "FABRIK 4-joint should converge.");
            for (int i = 0; i < result.Positions.Length - 1; i++)
            {
                float len = (result.Positions[i + 1] - result.Positions[i]).Length();
                AssertNear(len, 1, 0.01f, $"FABRIK 4-joint bone{i} preserved.");
            }
            AssertNear(result.FinalError, 0, 0.001f, "FABRIK 4-joint final error near 0.");
        }

        // ── FABRIK/CCD：同一可达目标都收敛（求解器一致性交叉验证）──
        public static void TestFABRIKAndCCDBothConvergeSameTarget()
        {
            var init = new Vector3[]
            {
                new Vector3(0, 0, 0),
                new Vector3(1, 0, 0),
                new Vector3(2, 0, 0),
            };
            var target = new Vector3(1.5f, 1.2f, 0.3f);
            var fabrik = FABRIKSolverReal.Solve(init, target, 50, 0.0005f);
            // CCD 逐骨朝向目标，收敛较慢，放宽迭代与容差
            var ccd = CCDIKSolverReal.Solve(init, target, 200, 0.005f);
            AssertTrue(fabrik.Converged, "FABRIK should converge for reachable 3D target.");
            AssertTrue(ccd.Converged, $"CCD should converge for reachable 3D target (finalError={ccd.FinalError:F5}, iters={ccd.Iterations}).");
            // 两者末端都到达同一目标
            AssertNearV(fabrik.Positions[^1], target, 0.005f, "FABRIK end reaches target.");
            AssertNearV(ccd.Positions[^1], target, 0.02f, "CCD end reaches target.");
        }

        // ── SpringIK：高刚度使末端朝目标移动，根固定，骨长近似保持 ──
        public static void TestSpringIKHighStiffnessMovesTowardTarget()
        {
            var init = new Vector3[]
            {
                new Vector3(0, 0, 0),
                new Vector3(1, 0, 0),
                new Vector3(2, 0, 0),
            };
            var target = new Vector3(1.5f, 1.5f, 0);
            // 强刚度弱阻尼 + 多子步：末端被弹簧充分拉向目标
            var result = SpringIKSolver.Solve(init, target,
                stiffness: 800f, damping: 5f, substeps: 60, dt: 0.016f, maxConstraintIters: 8);
            // 根固定
            AssertNearV(result.Positions[0], new Vector3(0, 0, 0), 0.001f, "SpringIK root stays pinned.");
            // 弹簧力朝目标：末端 Y 从 0 增大（弹簧物理在给定步数下收敛慢，验证受力方向）
            AssertTrue(result.Positions[2].Y > 0.01f,
                $"SpringIK end should be pulled toward +Y target (end.Y={result.Positions[2].Y:F4}).");
            AssertTrue(result.Positions[2].Y < 1.6f,
                $"SpringIK end should not overshoot target Y (end.Y={result.Positions[2].Y:F4}).");
            // 骨长近似保持（弹簧+PBD 约束，允许较大容差）
            float b0 = (result.Positions[1] - result.Positions[0]).Length();
            float b1 = (result.Positions[2] - result.Positions[1]).Length();
            AssertNear(b0, 1, 0.15f, "SpringIK bone0 approx preserved.");
            AssertNear(b1, 1, 0.15f, "SpringIK bone1 approx preserved.");
        }

        // ── SpringIK：零刚度无弹簧力，链保持初始位置（根固定）──
        public static void TestSpringIKZeroStiffnessChainStays()
        {
            var init = new Vector3[]
            {
                new Vector3(0, 0, 0),
                new Vector3(1, 0, 0),
                new Vector3(2, 0, 0),
            };
            // 零刚度、零初速度 → 无力 → 链保持
            var result = SpringIKSolver.Solve(init, new Vector3(1, 1, 0),
                stiffness: 0f, damping: 20f, substeps: 5, dt: 0.016f, maxConstraintIters: 3);
            AssertNearV(result.Positions[2], new Vector3(2, 0, 0), 0.01f,
                "SpringIK zero stiffness: chain stays at initial pos.");
        }

        // ── SpringIK：Converged 依据末端误差 < 0.01 ──
        public static void TestSpringIKConvergenceFlag()
        {
            // 高刚度拉末端到目标 → 误差 < 0.01 → Converged
            var init = new Vector3[] { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(2, 0, 0) };
            var target = new Vector3(2, 0, 0); // 末端已在目标处，误差≈0
            var result = SpringIKSolver.Solve(init, target,
                stiffness: 500f, damping: 20f, substeps: 10, dt: 0.016f, maxConstraintIters: 5);
            AssertTrue(result.Converged,
                $"SpringIK should converge when end already at target (error={result.FinalError:F5}).");
        }
    }
}
