// SwarmIntelligenceTests.cs — UE5.8 蜂群智能子系统深度验证
// 覆盖：FlowField（Dijkstra 积分场：目标=0/障碍不可达/单调远离目标）、
// InfluenceMap（高斯影响叠加、指数衰减、双线性采样、清空）。
// 全部纯 CPU、确定性数值验证（Vector2 网格 + XZ 平面）。

using System;
using System.Collections.Generic;
using System.Numerics;
using YamaEngine.AI;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests
{
    public static class SwarmIntelligenceTests
    {
        private static void AssertNear(float got, float want, float tol, string msg)
            => AssertTrue(MathF.Abs(got - want) <= tol,
                $"{msg} (got {got:F4}, want {want:F4}±{tol:F4})");

        // ── FlowField：目标格积分=0，8邻域代价随距离单调增长，方向指向目标 ──
        public static void TestFlowFieldIntegration()
        {
            var field = new FlowField(10, 10, 1f, Vector2.Zero);
            field.Generate(5, 5);
            // 目标格积分值必须为 0
            AssertNear(field.IntegrationField[field.Index(5, 5)], 0f, 1e-4f,
                "Goal cell integration must be 0.");
            // 直线相邻格（4正方向）代价 = 1
            AssertNear(field.IntegrationField[field.Index(4, 5)], 1f, 1e-4f,
                "Cell left of goal must cost 1.");
            AssertNear(field.IntegrationField[field.Index(6, 5)], 1f, 1e-4f,
                "Cell right of goal must cost 1.");
            // 斜向邻居代价 ≈ √2
            AssertNear(field.IntegrationField[field.Index(4, 4)], MathF.Sqrt(2f), 1e-3f,
                "Diagonal neighbor must cost ~√2.");
        }

        // ── FlowField：障碍格不可达（积分保持 max），障碍后方向场为 0 ──
        public static void TestFlowFieldObstacleBlocks()
        {
            var field = new FlowField(9, 9, 1f, Vector2.Zero);
            // 用障碍墙隔断 (0,4)-(0,8) 竖墙
            for (int y = 0; y <= 8; y++) field.SetObstacle(4, y);
            field.Generate(0, 4);
            // 目标侧可达
            AssertTrue(field.IntegrationField[field.Index(0, 4)] < 1e5f,
                "Goal side must be reachable.");
            // 墙对面（x>4）不可达
            AssertTrue(field.IntegrationField[field.Index(8, 4)] >= 1e5f,
                "Beyond wall must be unreachable (integration=max).");
            // 不可达格方向场为 0
            AssertTrue(field.DirectionField[field.Index(8, 4)] == Vector2.Zero,
                "Unreachable cell direction must be zero.");
        }

        // ── FlowField：方向场指向目标（远离目标一侧方向朝目标） ──
        public static void TestFlowFieldDirectionTowardGoal()
        {
            var field = new FlowField(11, 11, 1f, Vector2.Zero);
            field.Generate(10, 10);
            // (0,0) 应朝 +X+Y（目标在 (10,10)）
            var dir = field.DirectionField[field.Index(0, 0)];
            AssertTrue(dir.X > 0f && dir.Y > 0f,
                $"Cell far from goal must point toward goal, got ({dir.X:F2},{dir.Y:F2}).");
            // 归一化
            AssertNear(dir.Length(), 1f, 0.01f, "Direction must be normalized.");
        }

        // ── InfluenceMap：高斯影响叠加，中心最高，随距离衰减 ──
        public static void TestInfluenceMapGaussian()
        {
            var map = new InfluenceMap(20, 20, 1f, Vector2.Zero);
            map.AddInfluence(new Vector3(10, 0, 10), value: 1f, radius: 4f);
            // 中心值最高
            float center = map.Values[map.Index(10, 10)];
            AssertTrue(center > 0.5f, $"Center influence must be high, got {center:F3}.");
            // 稍远处值低于中心
            float edge = map.Values[map.Index(12, 10)];
            AssertTrue(edge < center,
                $"Edge influence must be less than center, got {edge:F3} vs {center:F3}.");
            // 影响半径外（超过 radius）为 0
            float far = map.Values[map.Index(10, 16)];
            AssertNear(far, 0f, 1e-4f, "Influence beyond radius must be 0.");
        }

        // ── InfluenceMap：Decay(dt) 指数衰减；Clear 清空 ──
        public static void TestInfluenceMapDecayAndClear()
        {
            var map = new InfluenceMap(20, 20, 1f, Vector2.Zero);
            map.AddInfluence(new Vector3(10, 0, 10), 2f, 3f);
            float before = map.Values[map.Index(10, 10)];
            AssertTrue(before > 0f, "Influence must be nonzero before decay.");

            // DecayRate=0.5，Decay(1) → factor=exp(-0.5)≈0.6065
            map.Decay(1f);
            float after1 = map.Values[map.Index(10, 10)];
            AssertNear(after1, before * MathF.Exp(-0.5f), 1e-3f,
                "One second decay must multiply by exp(-DecayRate).");

            map.Clear();
            AssertNear(map.Values[map.Index(10, 10)], 0f, 1e-6f, "Clear must zero all values.");
        }

        // ── InfluenceMap.Sample：双线性采样，在格中心采样=该格值 ──
        public static void TestInfluenceMapSample()
        {
            var map = new InfluenceMap(20, 20, 1f, Vector2.Zero);
            map.AddInfluence(new Vector3(10, 0, 10), 1f, 3f);
            // 影响源在世界 (10,10)，落在格 (10,10) 的左下角；格中心在世界 (10.5,10.5)
            // Sample 在格中心应精确返回该格值（双线性 tx=ty=0.5 的相邻格...实际格中心=(i+0.5)→tx=0）
            float centerVal = map.Values[map.Index(10, 10)];
            float sampledCenter = map.Sample(new Vector3(10.5f, 0, 10.5f));
            AssertNear(sampledCenter, centerVal, 0.01f,
                "Sample at cell center must equal that cell's value.");
            // 单调性：靠影响中心比远处采样值高
            float far = map.Sample(new Vector3(18, 0, 18));
            AssertTrue(sampledCenter > far, "Sample near influence must exceed far sample.");
            // 影响半径外采样≈0
            AssertNear(far, 0f, 0.02f, "Sample far from influence must be ~0.");
        }
    }
}
