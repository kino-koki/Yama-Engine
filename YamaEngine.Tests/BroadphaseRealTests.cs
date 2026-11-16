using System;
using System.Collections.Generic;
using YamaEngine.Physics;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests
{
    // ══════════════════════════════════════════════════════════════
    // BroadphaseRealDepth — 第二套独立宽相实现深度验证
    // 覆盖：DynamicAABBTree（SAH 插入/查询/移除/更新 fat-AABB）、
    //       SpatialHashGrid（稀疏哈希多格插入/查询去重/同格配对）、
    //       SweepAndPrune（单轴排序扫掠+速度外推扩展）。
    //       全部纯 CPU 确定性。
    // ══════════════════════════════════════════════════════════════
    public static class BroadphaseRealTests
    {
        private static AABBShape Box(float minX, float minY, float minZ, float maxX, float maxY, float maxZ)
            => new AABBShape(new PhysicsVector3(minX, minY, minZ), new PhysicsVector3(maxX, maxY, maxZ));

        private static HashSet<int> QueryToSet(DynamicAABBTree tree, AABBShape aabb)
        {
            var set = new HashSet<int>();
            tree.Query(aabb, (id, ud) => set.Add(id));
            return set;
        }

        // ── DynamicAABBTree：插入后查询、移除反映、远移更新 ──
        public static void TestDynamicAABBTreeInsertQueryRemove()
        {
            var tree = new DynamicAABBTree();
            int a = tree.Insert(Box(0, 0, 0, 2, 2, 2), "A");
            int b = tree.Insert(Box(1, 1, 1, 3, 3, 3), "B");   // 与 A 重叠
            int c = tree.Insert(Box(10, 10, 10, 12, 12, 12), "C"); // 远离

            var hits = QueryToSet(tree, Box(0, 0, 0, 3, 3, 3));
            AssertTrue(hits.Contains(a), "Query must include leaf A.");
            AssertTrue(hits.Contains(b), "Query must include leaf B.");
            AssertTrue(!hits.Contains(c), "Query must exclude distant leaf C.");

            // 移除 B → 查询不再返回
            tree.Remove(b);
            var afterRemove = QueryToSet(tree, Box(0, 0, 0, 3, 3, 3));
            AssertTrue(!afterRemove.Contains(b), "Removed leaf must not be returned by query.");
            AssertTrue(afterRemove.Contains(a), "Remaining leaf A must still be returned.");

            // 更新 A 到远处 → 原区域不再返回 A
            tree.Update(a, Box(50, 50, 50, 52, 52, 52));
            var afterMove = QueryToSet(tree, Box(0, 0, 0, 3, 3, 3));
            AssertTrue(!afterMove.Contains(a), "Moved-away leaf must no longer overlap original region.");
        }

        // ── DynamicAABBTree OverlapPairs：同区域叶子配对、远区域不配对 ──
        public static void TestDynamicAABBTreeOverlapPairs()
        {
            var tree = new DynamicAABBTree();
            int a = tree.Insert(Box(0, 0, 0, 1, 1, 1), "A");
            int b = tree.Insert(Box(0.5f, 0, 0, 1.5f, 1, 1), "B"); // 与 A 重叠
            int c = tree.Insert(Box(5, 5, 5, 6, 6, 6), "C");        // 独立区域
            int d = tree.Insert(Box(5.5f, 5, 5, 6.5f, 6, 6), "D");  // 与 C 重叠

            var pairs = new List<(int, int)>();
            tree.OverlapPairs((x, y) => pairs.Add((x, y)));
            var set = new HashSet<(int, int)>(pairs);

            // fat-AABB margin 0.1 → 重叠对通过 SAH 树遍历产出
            bool hasAB = set.Contains((Math.Min(a, b), Math.Max(a, b)));
            bool hasCD = pairs.Exists(p => (p.Item1 == Math.Min(c, d) && p.Item2 == Math.Max(c, d)));
            bool hasAC = pairs.Exists(p => (p.Item1 == Math.Min(a, c) && p.Item2 == Math.Max(a, c)));
            AssertTrue(hasAB, "Overlapping leaves A/B must be paired.");
            AssertTrue(hasCD, "Overlapping leaves C/D must be paired.");
            AssertTrue(!hasAC, "Leaves from disjoint regions A/C must not pair.");
        }

        // ── SpatialHashGrid：多格插入、查询去重、同格配对 ──
        public static void TestSpatialHashGridQueryAndPairs()
        {
            var grid = new SpatialHashGrid(cellSize: 1f);
            // 物体 0：大盒跨多格
            grid.Insert(0, Box(0, 0, 0, 2, 2, 2));
            // 物体 1：在物体内的小盒（应与 0 同格）
            grid.Insert(1, Box(0.5f, 0.5f, 0.5f, 1.5f, 1.5f, 1.5f));
            // 物体 2：远离
            grid.Insert(2, Box(100, 100, 100, 101, 101, 101));

            var hits = new HashSet<int>();
            grid.Query(Box(0, 0, 0, 2, 2, 2), id => hits.Add(id));
            AssertTrue(hits.Contains(0), "Query must include object 0.");
            AssertTrue(hits.Contains(1), "Query must include object 1.");
            AssertTrue(!hits.Contains(2), "Query must exclude distant object 2.");

            // 同格配对：0/1 应配对，2 不应出现
            var pairs = grid.QueryOverlapPairs();
            bool has01 = pairs.Contains((0, 1));
            bool hasWith2 = pairs.Exists(p => p.Item1 == 2 || p.Item2 == 2);
            AssertTrue(has01, "Co-located objects 0/1 must be paired.");
            AssertTrue(!hasWith2, "Distant object 2 must not be paired.");

            // Remove 后查询不再返回
            grid.Remove(0);
            var afterRemove = new HashSet<int>();
            grid.Query(Box(0, 0, 0, 2, 2, 2), id => afterRemove.Add(id));
            AssertTrue(!afterRemove.Contains(0), "Removed object must not be returned.");
        }

        // ── SpatialHashGrid：相邻但不同格的物体不配对 ──
        public static void TestSpatialHashGridSeparatesDisjointCells()
        {
            var grid = new SpatialHashGrid(cellSize: 1f);
            grid.Insert(0, Box(0, 0, 0, 0.5f, 0.5f, 0.5f));      // 格 (0,0,0)
            grid.Insert(1, Box(2, 0, 0, 2.5f, 0.5f, 0.5f));      // 格 (2,0,0)，中间隔 1 格

            var pairs = grid.QueryOverlapPairs();
            AssertTrue(!pairs.Contains((0, 1)), "Objects in disjoint cells must not be paired.");
        }

        // ── SweepAndPrune：重叠对检出、速度外推扩展捕捉将重叠 ──
        public static void TestSweepAndPruneDetectsOverlapAndPredictive()
        {
            var sap = new SweepAndPrune(maxBodies: 16);

            // 静态重叠：0/1 X 轴重叠（Y/Z 也重叠），2 三轴全远离
            var aabbs = new AABBShape[]
            {
                Box(0, 0, 0, 1, 1, 1),
                Box(0.5f, 0, 0, 1.5f, 1, 1),
                Box(50, 50, 50, 51, 51, 51),
            };
            var vels = new PhysicsVector3[3]; // 全 0 速度
            var pairs = sap.UpdateAndQuery(aabbs, vels, dt: 0.016f);
            AssertTrue(pairs.Contains((0, 1)), "SAP must detect overlapping pair (0,1).");
            AssertTrue(!pairs.Exists(p => p.Item1 == 2 || p.Item2 == 2), "SAP must exclude distant body 2.");

            // 速度外推：两盒当前分离但即将靠近 → 大 dt 下应被捕捉
            var aabbs2 = new AABBShape[]
            {
                Box(0, 0, 0, 1, 1, 1),
                Box(1.5f, 0, 0, 2.5f, 1, 1), // 当前 gap=0.5
            };
            var vels2 = new PhysicsVector3[]
            {
                new PhysicsVector3(0, 0, 0),
                new PhysicsVector3(-30, 0, 0), // 朝 -X 快速靠近
            };
            var predictive = sap.UpdateAndQuery(aabbs2, vels2, dt: 0.016f);
            // projMax 扩展 |v*dt*2| = 30*0.016*2 ≈ 0.96 > gap 0.5 → 应配对
            AssertTrue(predictive.Contains((0, 1)),
                "SAP velocity extrapolation should capture soon-to-overlap pair.");
        }
    }
}
