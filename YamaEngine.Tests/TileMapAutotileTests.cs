// TileMapAutotileTests.cs —— 追平 Godot 3 的 TileMap 自动瓦片/地形过渡（peering bits 掩码 + 贪心匹配 + 批量填充）

using System;
using System.Collections.Generic;
using YamaEngine.UI;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests
{
    public static class TileMapAutotileTests
    {
        private static void AssertEqual<T>(T got, T want, string msg)
        {
            if (!Equals(got, want)) throw new Exception($"{msg} (got {got}, want {want}).");
        }

        private static void AssertTrue(bool cond, string msg)
        {
            if (!cond) throw new Exception($"{msg}");
        }

        private static void AssertFalse(bool cond, string msg)
        {
            if (cond) throw new Exception($"{msg}");
        }

        private static TileMap MakeMap()
        {
            var map = new TileMap();
            return map;
        }

        // ── TileNeighbor 位值唯一（8 位各占 1 位） ──
        public static void TestNeighborBitValues()
        {
            AssertEqual(1, (int)TileNeighbor.N, "N=1.");
            AssertEqual(2, (int)TileNeighbor.NE, "NE=2.");
            AssertEqual(4, (int)TileNeighbor.E, "E=4.");
            AssertEqual(128, (int)TileNeighbor.NW, "NW=1<<7=128.");
        }

        // ── TileNeighbor 可按位组合 ──
        public static void TestNeighborFlagsCombine()
        {
            var m = TileNeighbor.N | TileNeighbor.S | TileNeighbor.E;
            AssertTrue((m & TileNeighbor.N) != 0, "Has N.");
            AssertTrue((m & TileNeighbor.S) != 0, "Has S.");
            AssertTrue((m & TileNeighbor.E) != 0, "Has E.");
            AssertFalse((m & TileNeighbor.W) != 0, "No W.");
        }

        // ── PickTileForMask：空候选返回 -1 ──
        public static void TestPickNoCandidates()
        {
            var rules = new AutoTileRules();
            AssertEqual(-1, rules.PickTileForMask(TileNeighbor.N), "No candidates → -1.");
        }

        // ── PickTileForMask：单个候选模板全零（通配，任何掩码都匹配） ──
        public static void TestPickWildcard()
        {
            var rules = new AutoTileRules();
            rules.AddCandidate(10, TileNeighbor.None);
            AssertEqual(10, rules.PickTileForMask(TileNeighbor.None), "Wildcard matches empty mask.");
            AssertEqual(10, rules.PickTileForMask(TileNeighbor.N | TileNeighbor.S), "Wildcard matches any mask.");
        }

        // ── PickTileForMask：模板位被实际包含才匹配 ──
        public static void TestPickRequiresTemplateSubset()
        {
            var rules = new AutoTileRules();
            rules.AddCandidate(20, TileNeighbor.N);
            AssertEqual(20, rules.PickTileForMask(TileNeighbor.N), "Exact N matches.");
            AssertEqual(20, rules.PickTileForMask(TileNeighbor.N | TileNeighbor.S), "N ⊆ N|S matches.");
            AssertEqual(-1, rules.PickTileForMask(TileNeighbor.None), "Actual None lacks N → no match.");
        }

        // ── PickTileForMask：多个候选选最特异（模板位最多） ──
        public static void TestPickMostSpecific()
        {
            var rules = new AutoTileRules();
            rules.AddCandidate(1, TileNeighbor.None);              // 通配（0 位）
            rules.AddCandidate(2, TileNeighbor.N);                 // 1 位
            rules.AddCandidate(3, TileNeighbor.N | TileNeighbor.S); // 2 位
            // 实际 = N|S：候选 3 匹配（2 位，最特异）
            AssertEqual(3, rules.PickTileForMask(TileNeighbor.N | TileNeighbor.S),
                "Most specific (N|S) wins.");
        }

        // ── PickTileForMask：候选不满足时跳过，退到次特异 ──
        public static void TestPickFallsBackToLessSpecific()
        {
            var rules = new AutoTileRules();
            rules.AddCandidate(3, TileNeighbor.N | TileNeighbor.S); // 需 N|S
            rules.AddCandidate(2, TileNeighbor.N);                  // 需 N
            // 实际 = N：候选 3 不满足（缺 S），候选 2 满足
            AssertEqual(2, rules.PickTileForMask(TileNeighbor.N), "Fallback to less specific.");
        }

        // ── GetAutoTileMask：孤立格子无邻居 → 全零 ──
        public static void TestMaskIsolated()
        {
            var map = MakeMap();
            map.SetCell(0, new Vector2I(0, 0), 5);
            var mask = map.GetAutoTileMask(0, new Vector2I(0, 0),
                (l, c) => map.GetCell(l, c) == 5);
            AssertEqual(TileNeighbor.None, mask, "Isolated → None.");
        }

        // ── GetAutoTileMask：四周同类邻居 → 四正方向位 ──
        public static void TestMaskFourCardinal()
        {
            var map = MakeMap();
            map.SetCell(0, new Vector2I(0, 0), 5);
            map.SetCell(0, new Vector2I(1, 0), 5);   // E
            map.SetCell(0, new Vector2I(-1, 0), 5);  // W
            map.SetCell(0, new Vector2I(0, -1), 5);  // N
            map.SetCell(0, new Vector2I(0, 1), 5);   // S
            var mask = map.GetAutoTileMask(0, new Vector2I(0, 0),
                (l, c) => map.GetCell(l, c) == 5);
            AssertTrue((mask & TileNeighbor.N) != 0, "Has N.");
            AssertTrue((mask & TileNeighbor.E) != 0, "Has E.");
            AssertTrue((mask & TileNeighbor.S) != 0, "Has S.");
            AssertTrue((mask & TileNeighbor.W) != 0, "Has W.");
            AssertFalse((mask & TileNeighbor.NE) != 0, "No NE (diagonal empty).");
        }

        // ── GetAutoTileMask：对角线邻居置对角位 ──
        public static void TestMaskDiagonal()
        {
            var map = MakeMap();
            map.SetCell(0, new Vector2I(0, 0), 5);
            map.SetCell(0, new Vector2I(1, 1), 5);  // SE
            map.SetCell(0, new Vector2I(-1, -1), 5); // NW
            var mask = map.GetAutoTileMask(0, new Vector2I(0, 0),
                (l, c) => map.GetCell(l, c) == 5);
            AssertTrue((mask & TileNeighbor.SE) != 0, "Has SE.");
            AssertTrue((mask & TileNeighbor.NW) != 0, "Has NW.");
            AssertFalse((mask & TileNeighbor.N) != 0, "No N.");
        }

        // ── GetAutoTileMask：非同类格不算邻居 ──
        public static void TestMaskNonTerrainNeighbor()
        {
            var map = MakeMap();
            map.SetCell(0, new Vector2I(0, 0), 5);
            map.SetCell(0, new Vector2I(1, 0), 9);  // 不同 id，非同类
            var mask = map.GetAutoTileMask(0, new Vector2I(0, 0),
                (l, c) => map.GetCell(l, c) == 5);
            AssertFalse((mask & TileNeighbor.E) != 0, "Different id → not terrain → no E bit.");
        }

        // ── 全 8 邻居包围：掩码全 255 ──
        public static void TestMaskAllNeighbors()
        {
            var map = MakeMap();
            map.SetCell(0, new Vector2I(0, 0), 5);
            foreach (var (bit, dx, dy) in TileNeighborDir.All)
                map.SetCell(0, new Vector2I(dx, dy), 5);
            var mask = map.GetAutoTileMask(0, new Vector2I(0, 0),
                (l, c) => map.GetCell(l, c) == 5);
            AssertEqual(255, (int)mask, "All 8 neighbors → mask=255.");
        }

        // ── AutoFillLayer：无规则 → 填充 0 格 ──
        public static void TestFillNoRules()
        {
            var map = MakeMap();
            map.SetCell(0, new Vector2I(0, 0), 5);
            int filled = map.AutoFillLayer(0, (l, c) => map.GetCell(l, c) == 5);
            AssertEqual(0, filled, "No rules bound → 0 filled.");
        }

        // ── AutoFillLayer：单格孤立 → 通配候选填充 ──
        public static void TestFillSingleCell()
        {
            var map = MakeMap();
            var rules = new AutoTileRules();
            rules.AddCandidate(10, TileNeighbor.None);
            map.SetAutoTileRules(rules);

            map.SetCell(0, new Vector2I(2, 2), 5); // 占位标记地形
            int filled = map.AutoFillLayer(0, (l, c) => map.GetCell(l, c) == 5);
            AssertEqual(1, filled, "1 cell filled.");
            AssertEqual(10, map.GetCell(0, new Vector2I(2, 2)), "Filled with candidate tile 10.");
        }

        // ── AutoFillLayer：横向 3 格，中间格两侧同类 → 选中横向模板 ──
        public static void TestFillHorizontalStrip()
        {
            var map = MakeMap();
            var rules = new AutoTileRules();
            rules.AddCandidate(1, TileNeighbor.None);                    // 孤立
            rules.AddCandidate(2, TileNeighbor.E | TileNeighbor.W);     // 横向中段
            rules.AddCandidate(3, TileNeighbor.W);                      // 左端（仅右边有邻居）
            rules.AddCandidate(4, TileNeighbor.E);                      // 右端
            map.SetAutoTileRules(rules);

            map.SetCell(0, new Vector2I(0, 0), 5);
            map.SetCell(0, new Vector2I(1, 0), 5);
            map.SetCell(0, new Vector2I(2, 0), 5);
            map.AutoFillLayer(0, (l, c) => map.GetCell(l, c) == 5);

            // 中间 (1,0)：E+W 都有 → 选 2
            AssertEqual(2, map.GetCell(0, new Vector2I(1, 0)), "Middle picks E|W template.");
            // 左端 (0,0)：仅 E 有邻居 → 选 4
            AssertEqual(4, map.GetCell(0, new Vector2I(0, 0)), "Left end picks E template.");
            // 右端 (2,0)：仅 W 有邻居 → 选 3
            AssertEqual(3, map.GetCell(0, new Vector2I(2, 0)), "Right end picks W template.");
        }

        // ── AutoFillLayer：填充数与地形格数一致 ──
        public static void TestFillCountMatches()
        {
            var map = MakeMap();
            var rules = new AutoTileRules();
            rules.AddCandidate(10, TileNeighbor.None);
            map.SetAutoTileRules(rules);

            for (int x = 0; x < 4; x++)
                map.SetCell(0, new Vector2I(x, 0), 5);
            int filled = map.AutoFillLayer(0, (l, c) => map.GetCell(l, c) == 5);
            AssertEqual(4, filled, "4 cells filled.");
        }

        // ── PickAutoTile：查询不写入 ──
        public static void TestPickAutoTileNoWrite()
        {
            var map = MakeMap();
            var rules = new AutoTileRules();
            rules.AddCandidate(10, TileNeighbor.None);
            map.SetAutoTileRules(rules);
            map.SetCell(0, new Vector2I(0, 0), 5);

            int picked = map.PickAutoTile(0, new Vector2I(0, 0), (l, c) => map.GetCell(l, c) == 5);
            AssertEqual(10, picked, "Picked tile 10.");
            AssertEqual(5, map.GetCell(0, new Vector2I(0, 0)), "Original cell unchanged (no write).");
        }

        // ── SetAutoTileRules / GetAutoTileRules 存取 ──
        public static void TestRulesRoundTrip()
        {
            var map = MakeMap();
            AssertTrue(map.GetAutoTileRules() == null, "No rules initially.");

            var rules = new AutoTileRules();
            map.SetAutoTileRules(rules);
            AssertTrue(ReferenceEquals(rules, map.GetAutoTileRules()), "Round-trip returns same instance.");

            map.ClearAutoTileRules();
            AssertTrue(map.GetAutoTileRules() == null, "Cleared.");
        }

        // ── AutoTileRules CandidateCount ──
        public static void TestRulesCandidateCount()
        {
            var rules = new AutoTileRules();
            AssertEqual(0, rules.CandidateCount, "Empty.");
            rules.AddCandidate(1, TileNeighbor.None);
            rules.AddCandidate(2, TileNeighbor.N);
            AssertEqual(2, rules.CandidateCount, "Two candidates.");
        }

        // ── 2×2 地形块：每格都有 2 个正邻 + 1 个对角 ──
        public static void TestFillQuadBlock()
        {
            var map = MakeMap();
            var rules = new AutoTileRules();
            rules.AddCandidate(0, TileNeighbor.None);                                    // 0 位
            rules.AddCandidate(1, TileNeighbor.E);                                       // 1 位
            rules.AddCandidate(2, TileNeighbor.E | TileNeighbor.S);                      // 2 位
            rules.AddCandidate(3, TileNeighbor.E | TileNeighbor.S | TileNeighbor.SW);    // 3 位
            map.SetAutoTileRules(rules);

            map.SetCell(0, new Vector2I(0, 0), 5);
            map.SetCell(0, new Vector2I(1, 0), 5);
            map.SetCell(0, new Vector2I(0, 1), 5);
            map.SetCell(0, new Vector2I(1, 1), 5);
            map.AutoFillLayer(0, (l, c) => map.GetCell(l, c) == 5);

            // (0,0)：E(1,0)、S(0,1)、SE(1,1) 有邻居 → 掩码 E|S|SE=4+16+8=28
            // 候选 3 (E|S|SW) SW=32 不在掩码，候选 2 (E|S) ⊆ 28 匹配 → 选 2
            AssertEqual(2, map.GetCell(0, new Vector2I(0, 0)), "Top-left picks E|S.");
            // (1,1)：W(-1,0)、N(1,0)、NW(0,-1) 有邻居 → W|N|NW，无候选匹配 → 通配 0
            AssertEqual(0, map.GetCell(0, new Vector2I(1, 1)), "Bottom-right falls to wildcard.");
        }
    }
}
