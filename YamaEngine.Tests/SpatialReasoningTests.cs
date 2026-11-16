// SpatialReasoningTests.cs — UE5.8 战术空间推理子系统深度验证
// 覆盖：ThreatField（威胁源叠加 falloff、LOS 遮挡过滤、时间衰减、FindSafestPoint 最低威胁点）。
// 全部纯 CPU、确定性数值验证（Vector3 XZ 平面）。

using System;
using System.Collections.Generic;
using System.Numerics;
using YamaEngine.AI;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests
{
    public static class SpatialReasoningTests
    {
        private static void AssertNear(float got, float want, float tol, string msg)
            => AssertTrue(MathF.Abs(got - want) <= tol,
                $"{msg} (got {got:F4}, want {want:F4}±{tol:F4})");

        /// <summary>可控空间查询：可注入遮挡盒。</summary>
        private class FakeSpatial : ISpatialQuery
        {
            public List<(Vector3 min, Vector3 max)> Boxes = new();
            public bool IsBlocked(Vector3 from, Vector3 to)
            {
                foreach (var (min, max) in Boxes)
                {
                    if (SegmentIntersectsBox(from, to, min, max)) return true;
                }
                return false;
            }
            public bool IsWalkable(Vector3 pos) => true;
            public List<Vector3> QueryWalkablePoints(Vector3 center, float radius) => new();
            public float GetGroundHeight(Vector3 pos) => 0f;
            public Vector3 FindNearestWalkable(Vector3 pos, float maxDist = 10f) => pos;

            // 2D XZ slab 简化判定：沿 from→to 采样看是否落入盒（XZ 投影 + 高度忽略）
            private static bool SegmentIntersectsBox(Vector3 from, Vector3 to, Vector3 min, Vector3 max)
            {
                const int steps = 64;
                for (int i = 0; i <= steps; i++)
                {
                    float t = i / (float)steps;
                    var p = from + (to - from) * t;
                    if (p.X >= min.X && p.X <= max.X && p.Z >= min.Z && p.Z <= max.Z)
                        return true;
                }
                return false;
            }
        }

        private static ThreatField MakeField(FakeSpatial spatial)
            => new ThreatField(20, 20, 1f, new Vector3(0, 0, 0), spatial);

        // ── 威胁源叠加：中心高、随距离 falloff 递减、range 外为 0 ──
        public static void TestThreatFalloff()
        {
            var spatial = new FakeSpatial();
            var field = MakeField(spatial);
            // 威胁源在 (10,0,10)，range=5，无遮挡
            field.AddThreat(new Vector3(10, 0, 10), range: 5f, intensity: 1f);

            float center = field.GetThreat(new Vector3(10.5f, 0, 10.5f)); // 最近格中心
            AssertTrue(center > 0.5f, $"Center threat must be high, got {center:F3}.");

            float edge = field.GetThreat(new Vector3(14.5f, 0, 10.5f)); // ~4.5 距离，falloff 小
            AssertTrue(edge > 0f && edge < center,
                $"Edge threat must be between 0 and center, got {edge:F3} (center {center:F3}).");

            float outside = field.GetThreat(new Vector3(18.5f, 0, 10.5f)); // range 外
            AssertNear(outside, 0f, 1e-4f, "Outside-range threat must be 0.");
        }

        // ── LOS 遮挡：墙后格点威胁为 0 ──
        public static void TestThreatLosBlock()
        {
            var spatial = new FakeSpatial();
            var field = MakeField(spatial);
            // 威胁源在 (5,0,10)，range=6；墙在 X∈[8,9], Z∈[0,20]（竖墙）
            spatial.Boxes.Add((new Vector3(8, 0, 0), new Vector3(9, 3, 20)));
            field.AddThreat(new Vector3(5, 0, 10), range: 6f, intensity: 1f);

            // 墙前（威胁源侧）应有威胁
            float front = field.GetThreat(new Vector3(6.5f, 0, 10.5f));
            AssertTrue(front > 0f, $"Front-of-wall must have threat, got {front:F3}.");

            // 墙后（X>9 且被遮挡）应过滤为 0
            float behind = field.GetThreat(new Vector3(10.5f, 0, 10.5f));
            AssertNear(behind, 0f, 1e-4f,
                $"Behind-wall (LOS-blocked) must be 0, got {behind:F3}.");
        }

        // ── 时间衰减：Decay(rate) 每格乘 (1-rate) ──
        public static void TestThreatDecay()
        {
            var spatial = new FakeSpatial();
            var field = MakeField(spatial);
            field.AddThreat(new Vector3(10, 0, 10), range: 3f, intensity: 1f);
            float before = field.GetThreat(new Vector3(10.5f, 0, 10.5f));
            AssertTrue(before > 0f, "Threat must be nonzero before decay.");

            field.Decay(0.5f);
            float after1 = field.GetThreat(new Vector3(10.5f, 0, 10.5f));
            AssertNear(after1, before * 0.5f, 1e-3f, "One decay(0.5) must halve threat.");

            field.Decay(0.5f);
            float after2 = field.GetThreat(new Vector3(10.5f, 0, 10.5f));
            AssertNear(after2, before * 0.25f, 1e-3f, "Two decays must quarter threat.");
        }

        // ── FindSafestPoint：在半径内找最低威胁格 ──
        public static void TestFindSafestPoint()
        {
            var spatial = new FakeSpatial();
            var field = MakeField(spatial);
            // 威胁集中在 (15,0,15)
            field.AddThreat(new Vector3(15, 0, 15), range: 4f, intensity: 1f);

            // 在远离威胁的角落在半径 5 内找最安全点
            var safest = field.FindSafestPoint(new Vector3(2, 0, 2), radius: 6f);
            // 该区域远离威胁源，威胁应≈0
            float t = field.GetThreat(safest);
            AssertNear(t, 0f, 1e-4f, "Safest point must sit at zero (or minimal) threat.");

            // 反向：在威胁中心附近找最安全点，应被推离高威胁格
            var safest2 = field.FindSafestPoint(new Vector3(15, 0, 15), radius: 3f);
            float t2 = field.GetThreat(safest2);
            float centerT = field.GetThreat(new Vector3(15.5f, 0, 15.5f));
            AssertTrue(t2 <= centerT,
                $"Safest near threat must not exceed center threat, got {t2:F3} vs center {centerT:F3}.");
        }

        // ── Clear：清空全场 ──
        public static void TestThreatClear()
        {
            var spatial = new FakeSpatial();
            var field = MakeField(spatial);
            field.AddThreat(new Vector3(10, 0, 10), range: 5f, intensity: 2f);
            field.Clear();
            float t = field.GetThreat(new Vector3(10.5f, 0, 10.5f));
            AssertNear(t, 0f, 1e-5f, "After Clear all threat must be 0.");
        }
    }
}
