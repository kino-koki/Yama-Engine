using System;
using System.Collections.Generic;
using System.Numerics;
using YamaEngine.Physics;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests
{
    // ══════════════════════════════════════════════════════════════
    // BroadphaseDepth — UE5.8 级碰撞宽相 / 空间查询 / 射线检测深度验证
    // 覆盖：AABB 结构几何 + Slab Raycast、DynamicAABBTree/SAP/Grid/BVH 四算法
    //       QueryAABB 一致性、FindPairs 层掩码过滤、RaycastAll 候选筛选
    // ══════════════════════════════════════════════════════════════
    public static class BroadphaseTests
    {
        private static void AssertNear(float got, float want, float tol, string msg)
            => AssertTrue(MathF.Abs(got - want) <= tol, $"{msg} (got {got:F5}, want {want:F5}±{tol:F5})");

        private static BodySlot MakeBody(int idx, Vector3 min, Vector3 max,
            BodyMotionType motion = BodyMotionType.Dynamic,
            int layer = 1, int mask = ~0)
            => new BodySlot
            {
                Active = true,
                MotionType = motion,
                AABBMin = min,
                AABBMax = max,
                CollisionLayer = layer,
                CollisionMask = mask
            };

        private static readonly BroadphaseAlgorithm[] AllAlgos = new[]
        {
            BroadphaseAlgorithm.DynamicAABBTree,
            BroadphaseAlgorithm.SweepAndPrune,
            BroadphaseAlgorithm.UniformGrid,
            BroadphaseAlgorithm.BVH
        };

        // ── AABB 几何：Union/Contains/Intersects 语义 ──
        public static void TestAABBGeometry()
        {
            var a = new AABB(new Vector3(0, 0, 0), new Vector3(2, 2, 2));
            var b = new AABB(new Vector3(1, 1, 1), new Vector3(3, 3, 3));
            var union = AABB.Union(a, b);
            AssertTrue(union.Min == new Vector3(0, 0, 0), "Union Min should take componentwise min.");
            AssertTrue(union.Max == new Vector3(3, 3, 3), "Union Max should take componentwise max.");
            AssertTrue(a.Intersects(b), "Overlapping boxes must intersect.");
            var separated = new AABB(new Vector3(10, 10, 10), new Vector3(11, 11, 11));
            AssertTrue(!a.Intersects(separated), "Separated boxes must not intersect.");
            AssertTrue(a.Contains(new AABB(new Vector3(0.5f, 0.5f, 0.5f), new Vector3(1.5f, 1.5f, 1.5f))),
                "Fully-inside box must be contained.");
            AssertTrue(!a.Contains(b), "Partially-overlapping box must not be contained.");
            // 相切不算碰撞（Intersects 用严格 < >）
            var touching = new AABB(new Vector3(2, 0, 0), new Vector3(3, 2, 2));
            AssertTrue(!a.Intersects(touching), "Edge-touching boxes must not intersect (strict comparison).");
        }

        // ── AABB.Slab Raycast：命中 tmin、平行射线、反向射线 ──
        public static void TestAABBSlabRaycast()
        {
            var box = new AABB(new Vector3(1, -1, -1), new Vector3(3, 1, 1));
            // 从原点朝 +X 射入，命中面 x=1，tmin=1
            AssertTrue(box.Raycast(new Vector3(0, 0, 0), new Vector3(1, 0, 0), out float t),
                "Ray toward box must hit.");
            AssertNear(t, 1f, 1e-5f, "Hit tmin should be 1 (distance to x=1 face).");
            // 反向射线（朝 -X 远离）不命中
            AssertTrue(!box.Raycast(new Vector3(0, 0, 0), new Vector3(-1, 0, 0), out _),
                "Ray away from box must miss.");
            // 原点在盒内部 → tmin=0（tmax>=0 通过）
            AssertTrue(box.Raycast(new Vector3(2, 0, 0), new Vector3(1, 0, 0), out float tInside),
                "Ray from inside box must hit.");
            AssertNear(tInside, 0f, 1e-6f, "Inside hit tmin should clamp to 0.");
            // 平行于 Y 且 origin.Y 在范围外 → 不命中
            AssertTrue(!box.Raycast(new Vector3(2, 5, 0), new Vector3(1, 0, 0), out _),
                "Parallel ray outside slab must miss.");
        }

        // ── 四算法 QueryAABB 一致性：同一场景各算法返回相同重叠集合 ──
        public static void TestQueryAABBConsistencyAcrossAlgorithms()
        {
            var bodies = new List<BodySlot>
            {
                MakeBody(0, new Vector3(0, 0, 0), new Vector3(2, 2, 2)),
                MakeBody(1, new Vector3(1, 1, 1), new Vector3(3, 3, 3)),   // 与 0 重叠
                MakeBody(2, new Vector3(10, 10, 10), new Vector3(12, 12, 12)), // 远离
            };
            HashSet<int> baseline = null;
            foreach (var algo in AllAlgos)
            {
                var bp = new BroadphaseSystem();
                bp.SetAlgorithm(algo);
                bp.SyncBounds(bodies);
                // 查询覆盖 body0+body1 区域
                var hits = bp.QueryAABB(new Vector3(0, 0, 0), new Vector3(3, 3, 3));
                var set = new HashSet<int>(hits);
                AssertTrue(set.Contains(0), $"{algo}: query must include body0.");
                AssertTrue(set.Contains(1), $"{algo}: query must include body1.");
                AssertTrue(!set.Contains(2), $"{algo}: query must exclude distant body2.");
                if (baseline == null) baseline = set;
                else AssertTrue(set.SetEquals(baseline), $"{algo}: result must match baseline.");
            }
        }

        // ── FindPairs 层掩码 + 静态-静态过滤 ──
        public static void TestFindPairsLayerMaskAndStaticFilter()
        {
            var bodies = new List<BodySlot>
            {
                // 0/1 重叠且层掩码互通 → 应配对
                MakeBody(0, new Vector3(0, 0, 0), new Vector3(1, 1, 1), layer: 1, mask: 1),
                MakeBody(1, new Vector3(0.5f, 0, 0), new Vector3(1.5f, 1, 1), layer: 1, mask: 1),
                // 2 与 0 重叠但 layer/mask 不通 → 排除
                MakeBody(2, new Vector3(0.2f, 0, 0), new Vector3(0.8f, 1, 1), layer: 2, mask: 2),
                // 3/4 重叠但都是 Static → 排除
                MakeBody(3, new Vector3(5, 0, 0), new Vector3(6, 1, 1), BodyMotionType.Static, 1, 1),
                MakeBody(4, new Vector3(5.5f, 0, 0), new Vector3(6.5f, 1, 1), BodyMotionType.Static, 1, 1),
            };
            var bp = new BroadphaseSystem();
            bp.SetAlgorithm(BroadphaseAlgorithm.DynamicAABBTree);
            bp.SyncBounds(bodies);
            var pairs = bp.FindPairs(bodies);
            bool hasPair01 = pairs.Exists(p => (p.BodyA == 0 && p.BodyB == 1) || (p.BodyA == 1 && p.BodyB == 0));
            bool hasAnyWith2 = pairs.Exists(p => p.BodyA == 2 || p.BodyB == 2);
            bool hasPair34 = pairs.Exists(p => (p.BodyA == 3 && p.BodyB == 4) || (p.BodyA == 4 && p.BodyB == 3));
            AssertTrue(hasPair01, "Overlapping bodies with compatible masks must be paired.");
            AssertTrue(!hasAnyWith2, "Pair with incompatible layer/mask must be filtered out.");
            AssertTrue(!hasPair34, "Static-Static pair must be filtered out.");
            // 约定 A < B
            foreach (var p in pairs)
                AssertTrue(p.BodyA < p.BodyB, "CandidatePair must normalize A < B.");
        }

        // ── RaycastAll：命中路径上所有候选、排除偏离射线 ──
        public static void TestRaycastAllCandidatesOnPath()
        {
            var bodies = new List<BodySlot>
            {
                MakeBody(0, new Vector3(2, -1, -1), new Vector3(3, 1, 1)),    // 在 +X 路径上
                MakeBody(1, new Vector3(5, -1, -1), new Vector3(6, 1, 1)),    // 更远，也在路径上
                MakeBody(2, new Vector3(2, 10, 10), new Vector3(3, 11, 11)),  // 偏离射线
            };
            var bp = new BroadphaseSystem();
            bp.SetAlgorithm(BroadphaseAlgorithm.DynamicAABBTree);
            bp.SyncBounds(bodies);
            var hits = bp.RaycastAll(new Vector3(0, 0, 0), new Vector3(1, 0, 0), 10f);
            var set = new HashSet<int>(hits);
            AssertTrue(set.Contains(0), "RaycastAll must return first box on path.");
            AssertTrue(set.Contains(1), "RaycastAll must return second box on path.");
            AssertTrue(!set.Contains(2), "RaycastAll must exclude box off the ray path.");
        }

        // ── 动态树增删：删除后查询不再返回 ──
        public static void TestDynamicTreeRemoveReflectsInQuery()
        {
            var bp = new BroadphaseSystem();
            bp.SetAlgorithm(BroadphaseAlgorithm.DynamicAABBTree);
            var bodies = new List<BodySlot>
            {
                MakeBody(0, new Vector3(0, 0, 0), new Vector3(1, 1, 1)),
            };
            bp.SyncBounds(bodies);
            AssertTrue(bp.QueryAABB(new Vector3(0, 0, 0), new Vector3(1, 1, 1)).Contains(0),
                "Inserted body must be queryable.");

            // 移动 body0 到远处再 Sync（DynamicAABBTree.Update 反映移动）
            var moved = bodies[0];
            moved.AABBMin = new Vector3(100, 100, 100);
            moved.AABBMax = new Vector3(101, 101, 101);
            bodies[0] = moved;
            bp.SyncBounds(bodies);
            var afterMove = bp.QueryAABB(new Vector3(0, 0, 0), new Vector3(1, 1, 1));
            AssertTrue(!afterMove.Contains(0), "Moved-away body must no longer overlap original region.");
        }
    }
}
