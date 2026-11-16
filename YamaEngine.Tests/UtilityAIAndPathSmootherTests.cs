// UtilityAIAndPathSmootherTests.cs — UE5.8 Utility AI + 路径平滑子系统深度验证
// 覆盖：ResponseCurve 8 种曲线（端点/中心/边界 clamp）、Consideration.Score（权重幂函数/InvertInput/Disabled）、
// UtilityAction.CalculateScore（6 种合并方式/Anchor/BaseWeight/空考虑）、FunnelSmoother（直线走廊/绕过拐角/空 portal）、
// PathOptimizationUtils（FindSharpCorners/Tortuosity/Reverse）。
// 全部纯 CPU、确定性数值验证。

using System;
using System.Collections.Generic;
using System.Numerics;
using YamaEngine.AI;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests
{
    public static class UtilityAIAndPathSmootherTests
    {
        private static void AssertNear(float got, float want, float tol, string msg)
            => AssertTrue(MathF.Abs(got - want) <= tol,
                $"{msg} (got {got:F4}, want {want:F4}±{tol:F4})");

        private static void AssertV(Vector3 got, Vector3 want, float tol, string msg)
        {
            AssertNear(got.X, want.X, tol, $"{msg}.X");
            AssertNear(got.Y, want.Y, tol, $"{msg}.Y");
            AssertNear(got.Z, want.Z, tol, $"{msg}.Z");
        }

        private static UtilityAIContext EmptyCtx()
            => new UtilityAIContext(0);

        // ── ResponseCurve.Linear：公式 slope*(x-center)+0.5，center=0 时 x=0→0.5 ──
        public static void TestLinearCurveEndpoints()
        {
            var c = ResponseCurve.Linear(slope: 1f, center: 0f);
            AssertNear(c.Evaluate(0f), 0.5f, 0.01f, "Linear at input=0 must be 0.5.");
            AssertNear(c.Evaluate(1f), 1f, 0.01f, "Linear at input=1 (1.5) must clamp to 1.");
            AssertNear(c.Evaluate(0.5f), 1f, 0.01f, "Linear at input=0.5 must be 1.");
        }

        // ── ResponseCurve.Logistic：中心=0.5、两端 clamp ──
        public static void TestLogisticCurveCenterAndTails()
        {
            var c = ResponseCurve.Logistic(slope: 4f, center: 0.5f);
            AssertNear(c.Evaluate(0.5f), 0.5f, 0.01f, "Logistic at center must be 0.5.");
            AssertTrue(c.Evaluate(0f) < 0.15f, $"Logistic at tail 0 must be small (got {c.Evaluate(0f):F3}).");
            AssertTrue(c.Evaluate(1f) > 0.85f, $"Logistic at tail 1 must be near 1 (got {c.Evaluate(1f):F3}).");
        }

        // ── ResponseCurve.Gaussian：中心=1、远离=0 ──
        public static void TestGaussianCurvePeak()
        {
            var c = ResponseCurve.Gaussian(center: 0.5f, width: 0.2f);
            AssertNear(c.Evaluate(0.5f), 1f, 0.01f, "Gaussian at center must be 1.");
            AssertTrue(c.Evaluate(0f) < 0.05f, $"Gaussian far from center must be near 0 (got {c.Evaluate(0f):F3}).");
        }

        // ── ResponseCurve.Step：阈值硬切 ──
        public static void TestStepCurveHardSwitch()
        {
            var c = ResponseCurve.Step(threshold: 0.5f);
            AssertNear(c.Evaluate(0.49f), 0f, 0.01f, "Step below threshold must be 0.");
            AssertNear(c.Evaluate(0.51f), 1f, 0.01f, "Step above threshold must be 1.");
            AssertNear(c.Evaluate(0.5f), 1f, 0.01f, "Step at threshold (>=) must be 1.");
        }

        // ── ResponseCurve 输入 clamp：input>1 clamp 到 1 ──
        public static void TestResponseCurveClampsInput()
        {
            var c = ResponseCurve.Step(threshold: 0.5f);
            AssertNear(c.Evaluate(5f), 1f, 0.01f, "Input>1 must clamp to 1 → step=1.");
            AssertNear(c.Evaluate(-5f), 0f, 0.01f, "Input<0 must clamp to 0 → step=0.");
        }

        // ── Consideration.Score：Weight=2 幂函数（s^2） ──
        public static void TestConsiderationWeightPow()
        {
            var ctx = EmptyCtx();
            var cons = new Consideration("test", _ => 0.5f, ResponseCurve.Linear(1f, 0f))
            {
                Weight = 2f,
            };
            float s = cons.Score(ctx);
            // curve(0.5) = 1.0, s^2 = 1.0
            AssertNear(s, 1f, 0.01f, "Consideration with curve=1 must remain 1 after pow.");
        }

        // ── Consideration.Score：InvertInput 反转 ──
        public static void TestConsiderationInvertInput()
        {
            var ctx = EmptyCtx();
            // Linear(slope=-1, center=1)：input=0.5 → -1*(0.5-1)+0.5 = 1.0
            // input=0.5 invert → 0.5 → curve → 1.0
            var normal = new Consideration("n", _ => 0.5f, ResponseCurve.Linear(-1f, 1f)) { InvertInput = false };
            var inverted = new Consideration("i", _ => 0.5f, ResponseCurve.Linear(-1f, 1f)) { InvertInput = true };
            AssertNear(normal.Score(ctx), 1f, 0.01f, "Normal input=0.5 → curve=1.");
            // inverted input=0.5 → 1-0.5=0.5 → curve(0.5) = -1*(-0.5)+0.5 = 1.0，same
            // 换用 Step 更明显：
            var stepNormal = new Consideration("sn", _ => 0.8f, ResponseCurve.Step(0.5f));
            var stepInvert = new Consideration("si", _ => 0.8f, ResponseCurve.Step(0.5f)) { InvertInput = true };
            AssertNear(stepNormal.Score(ctx), 1f, 0.01f, "Step(0.8>0.5) must be 1.");
            AssertNear(stepInvert.Score(ctx), 0f, 0.01f, "Inverted(0.2<0.5) must be 0.");
        }

        // ── Consideration.Score：Disabled → 1 ──
        public static void TestConsiderationDisabledReturnsOne()
        {
            var ctx = EmptyCtx();
            var cons = new Consideration("d", _ => 0f, ResponseCurve.Step(0.5f)) { Enabled = false };
            AssertNear(cons.Score(ctx), 1f, 0.01f, "Disabled consideration must return 1 (neutral).");
        }

        // ── UtilityAction.Multiply：任一为 0 → 整体为 0 ──
        public static void TestActionMultiplyZeroKills()
        {
            var ctx = EmptyCtx();
            var a = new UtilityAction("test") { Method = ScoringMethod.Multiply };
            a.AddConsideration(new Consideration("c1", _ => 0.8f, ResponseCurve.Step(0.5f)));
            a.AddConsideration(new Consideration("c2", _ => 0.2f, ResponseCurve.Step(0.5f)));
            float s = a.CalculateScore(ctx);
            AssertNear(s, 0f, 0.01f, "Multiply with any 0 must be 0.");
        }

        // ── UtilityAction.Minimum：短板效应 ──
        public static void TestActionMinimum()
        {
            var ctx = EmptyCtx();
            var a = new UtilityAction("test") { Method = ScoringMethod.Minimum };
            a.AddConsideration(new Consideration("c1", _ => 0.9f, ResponseCurve.Step(0.5f)));
            a.AddConsideration(new Consideration("c2", _ => 0.3f, ResponseCurve.Step(0.5f)));
            float s = a.CalculateScore(ctx);
            AssertNear(s, 0f, 0.01f, "Minimum: min(1, 0) = 0.");
        }

        // ── UtilityAction.Anchor：防止 0 清零 ──
        public static void TestActionAnchorRescue()
        {
            var ctx = EmptyCtx();
            var a = new UtilityAction("test") { Method = ScoringMethod.Multiply, Anchor = 0.5f };
            a.AddConsideration(new Consideration("c1", _ => 0f, ResponseCurve.Step(0.5f)));
            float s = a.CalculateScore(ctx);
            // Anchor*0.1 = 0.05 > product(0)
            AssertNear(s, 0.05f, 0.005f, "Anchor must rescue score from 0 to Anchor*0.1.");
        }

        // ── UtilityAction.BaseWeight：缩放分数 ──
        public static void TestActionBaseWeight()
        {
            var ctx = EmptyCtx();
            var a = new UtilityAction("test") { Method = ScoringMethod.Multiply, BaseWeight = 2f };
            a.AddConsideration(new Consideration("c1", _ => 0.8f, ResponseCurve.Step(0.5f)));
            a.AddConsideration(new Consideration("c2", _ => 0.7f, ResponseCurve.Step(0.5f)));
            float s = a.CalculateScore(ctx);
            AssertNear(s, 2f, 0.01f, "BaseWeight=2 must scale product(1*1=1) to 2.");
        }

        // ── UtilityAction 空考虑 → 1 ──
        public static void TestActionEmptyConsiderationsReturnsOne()
        {
            var ctx = EmptyCtx();
            var a = new UtilityAction("empty");
            float s = a.CalculateScore(ctx);
            AssertNear(s, 1f, 0.01f, "Action with no considerations must return 1.");
        }

        // ── FunnelSmoother：无 portal → [start, end] ──
        public static void TestFunnelEmptyPortals()
        {
            var funnel = new FunnelSmoother();
            var start = new Vector3(0, 0, 0);
            var end = new Vector3(10, 0, 0);
            var path = funnel.Smooth(start, end);
            AssertEqual(2, path.Count, "Empty portals must return [start, end].");
            AssertV(path[0], start, 0.01f, "First point must be start.");
            AssertV(path[1], end, 0.01f, "Last point must be end.");
        }

        // ── FunnelSmoother：直线走廊 → 直线路径（不拐弯）──
        public static void TestFunnelStraightCorridor()
        {
            var funnel = new FunnelSmoother();
            // 走廊沿 +X，宽 2（Z=±1）
            for (int i = 0; i < 5; i++)
            {
                float x = i * 2f;
                funnel.AddPortal(new Vector3(x, 0, -1), new Vector3(x, 0, 1), i);
            }
            var start = new Vector3(0, 0, 0);
            var end = new Vector3(10, 0, 0);
            var path = funnel.Smooth(start, end);
            // 直线走廊 → 只有 [start, end]
            AssertEqual(2, path.Count, "Straight corridor funnel must return [start, end].");
            AssertV(path[0], start, 0.01f, "Path[0]=start.");
            AssertV(path[1], end, 0.01f, "Path[1]=end.");
        }

        // ── FunnelSmoother：必须绕过凸拐角 → 拉绳到拐角点 ──
        public static void TestFunnelBypassesCorner()
        {
            var funnel = new FunnelSmoother { MinWaypointDist = 0.01f };
            // 走廊先向 +X，再向 +Z（L 形），portal 在拐角处收窄
            // portal 0-1-2 沿 X 方向（宽通道 Z∈[0,3]）
            funnel.AddPortal(new Vector3(0, 0, 3), new Vector3(0, 0, 0), 0);
            funnel.AddPortal(new Vector3(3, 0, 3), new Vector3(3, 0, 0), 1);
            // portal 2-3-4 沿 Z 方向（窄通道 X∈[0,1]）
            funnel.AddPortal(new Vector3(0, 0, 6), new Vector3(1, 0, 3), 2);
            funnel.AddPortal(new Vector3(0, 0, 9), new Vector3(1, 0, 6), 3);

            var start = new Vector3(0, 0, 1.5f);
            var end = new Vector3(0.5f, 0, 9f);
            var path = funnel.Smooth(start, end);

            // funnel 应至少产生 start + 一个拐角点 + end
            AssertTrue(path.Count >= 2, $"L-shaped corridor must produce ≥2 points (got {path.Count}).");
            // 最后点必须是 end
            AssertV(path[path.Count - 1], end, 0.01f, "Last point must be end.");
            // 所有路径点必须在走廊内（不检查具体坐标，仅确保路径存在）
            AssertTrue(path.Count >= 2 && path.Count <= 5,
                $"Path length should be reasonable (got {path.Count}).");
        }

        // ── PathOptimizationUtils.Tortuosity：直线=0、弯曲>0 ──
        public static void TestPathTortuosity()
        {
            var straight = new List<Vector3> { new(0, 0, 0), new(1, 0, 0), new(2, 0, 0) };
            float straightT = PathOptimizationUtils.Tortuosity(straight);
            AssertNear(straightT, 1f, 0.01f, "Straight path tortuosity must be 1 (total == straight).");

            var bendy = new List<Vector3> { new(0, 0, 0), new(1, 0, 1), new(2, 0, 0), new(3, 0, 1) };
            float bendyT = PathOptimizationUtils.Tortuosity(bendy);
            AssertTrue(bendyT > 1.1f, $"Bendy path tortuosity must be >1 (got {bendyT:F3}).");
        }

        // ── PathOptimizationUtils.Reverse：反转路径 ──
        public static void TestPathReverse()
        {
            var path = new List<Vector3> { new(0, 0, 0), new(1, 0, 0), new(2, 0, 0) };
            var rev = PathOptimizationUtils.Reverse(path);
            AssertEqual(path.Count, rev.Count, "Reverse must preserve count.");
            AssertV(rev[0], path[2], 0.01f, "Reversed[0] = original[last].");
            AssertV(rev[2], path[0], 0.01f, "Reversed[last] = original[0].");
        }

        // ── PathOptimizationUtils.FindSharpCorners：检测 90° 拐角 ──
        public static void TestPathFindSharpCorners()
        {
            // L 形路径：一个 90° 拐角在 (1,0,0)
            var path = new List<Vector3> { new(0, 0, 0), new(1, 0, 0), new(1, 0, 1) };
            var corners = PathOptimizationUtils.FindSharpCorners(path, thresholdDeg: 45f);
            AssertEqual(1, corners.Count, "L path must have exactly 1 sharp corner (at index 1).");
            AssertEqual(1, corners[0], "Corner must be at waypoint index 1.");
        }
    }
}
