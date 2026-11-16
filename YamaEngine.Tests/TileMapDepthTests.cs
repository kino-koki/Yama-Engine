// TileMapDepthTests.cs — Unity5 Tilemap + Autotile Terrain 深度验证
// 覆盖:
//   * TileSet: AddTile/GetTile/RemoveTile/HasTile/CellSize/TileCount
//   * TileMap: AddLayer/SetCell/GetCell/EraseCell/ClearLayer/GetUsedCells/HasCell
//   * 坐标转换: MapToLocal/LocalToMap/WorldToMap/MapToWorld
//   * TileNeighbor 位掩码 + AutoTileRules.PickTileForMask 贪心匹配
//   * TileMap.GetAutoTileMask + AutoFillLayer 两遍填充

using System;
using System.Collections.Generic;
using System.Numerics;
using YamaEngine.UI;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests
{
    public static class TileMapDepthTests
    {
        private const float Eps = 1e-4f;

        // ── 1. TileSet 基本 CRUD ──
        public static void TestTileSetCRUD()
        {
            var ts = new TileSet();
            AssertEqual(0, ts.TileCount, "新 TileSet 应空");
            AssertEqual(new Vector2(16, 16), ts.CellSize, "默认 CellSize 应 16x16");

            ts.AddTile(new Tile(0) { Solid = true });
            ts.AddTile(new Tile(1));
            ts.AddTile(new Tile(2));
            AssertEqual(3, ts.TileCount, "加 3 瓦片后应 3");

            // GetTile 命中
            var t1 = ts.GetTile(1);
            AssertTrue(t1 != null, "GetTile(1) 应命中");
            AssertEqual(1, t1!.Id, "命中瓦片 Id=1");

            // GetTile 未命中 → null
            AssertTrue(ts.GetTile(99) == null, "GetTile(99) 应 null");

            // HasTile
            AssertTrue(ts.HasTile(0), "HasTile(0) 应 true");
            AssertTrue(!ts.HasTile(99), "HasTile(99) 应 false");

            // AddTile 覆盖同 id
            ts.AddTile(new Tile(1) { Solid = true });
            AssertEqual(3, ts.TileCount, "覆盖同 id 不应增数量");
            AssertTrue(ts.GetTile(1)!.Solid, "覆盖后 Solid=true");

            // RemoveTile
            AssertTrue(ts.RemoveTile(0), "RemoveTile(0) 应 true");
            AssertTrue(!ts.RemoveTile(0), "重复 RemoveTile 应 false");
            AssertEqual(2, ts.TileCount, "移除后剩 2");
        }

        // ── 2. Tile 自定义数据 ──
        public static void TestTileCustomData()
        {
            var t = new Tile(5) { TexturePath = "terrain/grass.png", Solid = true };
            t.CustomData["speed"] = 0.7;
            t.CustomData["name"] = "Grass";

            AssertEqual("terrain/grass.png", t.TexturePath, "TexturePath");
            AssertTrue(t.Solid, "Solid=true");
            AssertEqual(2, t.CustomData.Count, "CustomData 应 2 项");
            AssertEqual("Grass", (string)t.CustomData["name"], "CustomData name");
            AssertEqual(0.7, (double)t.CustomData["speed"], "CustomData speed");
        }

        // ── 3. TileMap 层管理 ──
        public static void TestTileMapLayers()
        {
            var map = new TileMap("Ground");
            AssertEqual(1, map.LayerCount, "构造后默认 1 层");

            int layer1 = map.AddLayer();
            AssertEqual(1, layer1, "第二个 layer 索引=1");
            AssertEqual(2, map.LayerCount, "加层后 LayerCount=2");

            int layer2 = map.AddLayer();
            AssertEqual(2, layer2, "第三个 layer 索引=2");

            // 越界层访问安全
            AssertEqual(-1, map.GetCell(99, new Vector2I(0, 0)), "越界层 GetCell 应 -1");
            AssertEqual(-1, map.GetCell(-1, new Vector2I(0, 0)), "负层 GetCell 应 -1");
            AssertEqual(0, map.GetUsedCellsCount(99), "越界层 GetUsedCellsCount 应 0");
            AssertTrue(!map.HasCell(99, new Vector2I(0, 0)), "越界层 HasCell 应 false");
        }

        // ── 4. SetCell/GetCell/EraseCell ──
        public static void TestSetGetEraseCell()
        {
            var map = new TileMap();
            map.SetCell(0, new Vector2I(0, 0), 0);
            map.SetCell(0, new Vector2I(1, 0), 1);
            map.SetCell(0, new Vector2I(5, 3), 2);

            AssertEqual(0, map.GetCell(0, new Vector2I(0, 0)), "GetCell(0,0)=0");
            AssertEqual(1, map.GetCell(0, new Vector2I(1, 0)), "GetCell(1,0)=1");
            AssertEqual(2, map.GetCell(0, new Vector2I(5, 3)), "GetCell(5,3)=2");
            AssertEqual(-1, map.GetCell(0, new Vector2I(9, 9)), "空格 GetCell=-1");

            // 同格重写
            map.SetCell(0, new Vector2I(0, 0), 7);
            AssertEqual(7, map.GetCell(0, new Vector2I(0, 0)), "重写后应=7");

            // EraseCell
            map.EraseCell(0, new Vector2I(1, 0));
            AssertEqual(-1, map.GetCell(0, new Vector2I(1, 0)), "擦除后 GetCell=-1");
            AssertEqual(2, map.GetUsedCellsCount(0), "擦除后剩 2");

            // EraseCell 幂等
            map.EraseCell(0, new Vector2I(1, 0));
            AssertEqual(2, map.GetUsedCellsCount(0), "重复擦除不变");
        }

        // ── 5. ClearLayer/GetUsedCells ──
        public static void TestClearLayerAndGetUsedCells()
        {
            var map = new TileMap();
            map.AddLayer();
            for (int x = 0; x < 3; x++)
                map.SetCell(0, new Vector2I(x, 0), x);
            for (int y = 0; y < 3; y++)
                map.SetCell(1, new Vector2I(0, y), y + 10);

            AssertEqual(3, map.GetUsedCellsCount(0), "层0 3 格");
            AssertEqual(3, map.GetUsedCellsCount(1), "层1 3 格");

            var cells0 = map.GetUsedCells(0);
            AssertEqual(3, cells0.Count, "GetUsedCells(0) 返回 3 格");

            // ClearLayer(0) 只清层 0
            map.ClearLayer(0);
            AssertEqual(0, map.GetUsedCellsCount(0), "ClearLayer(0) 后层 0 空");
            AssertEqual(3, map.GetUsedCellsCount(1), "ClearLayer(0) 后层 1 不变");

            // 越界 ClearLayer 安全
            map.ClearLayer(99);
            AssertEqual(3, map.GetUsedCellsCount(1), "越界 ClearLayer 不影响其他层");
        }

        // ── 6. MapToLocal/LocalToMap ──
        public static void TestCoordinateConversion()
        {
            var ts = new TileSet { CellSize = new Vector2(32, 32) };
            var map = new TileMap { TileSet = ts };

            // (0,0) → (0,0)
            var l0 = map.MapToLocal(new Vector2I(0, 0));
            AssertTrue(MathF.Abs(l0.X) < Eps && MathF.Abs(l0.Y) < Eps,
                "MapToLocal(0,0)=(0,0)");

            // (3,2) → (96, 64)
            var l1 = map.MapToLocal(new Vector2I(3, 2));
            AssertTrue(MathF.Abs(l1.X - 96) < Eps && MathF.Abs(l1.Y - 64) < Eps,
                $"MapToLocal(3,2)=(96,64)，实际=({l1.X},{l1.Y})");

            // 反向：LocalToMap(96, 64) → (3, 2)
            var m1 = map.LocalToMap(new Vector2(96, 64));
            AssertEqual(3, m1.X, "LocalToMap(96,64).X=3");
            AssertEqual(2, m1.Y, "LocalToMap(96,64).Y=2");

            // 负坐标向下取整：LocalToMap(-1, -1) → (-1, -1)
            var m2 = map.LocalToMap(new Vector2(-1, -1));
            AssertEqual(-1, m2.X, "LocalToMap(-1,-1).X=-1（floor）");
            AssertEqual(-1, m2.Y, "LocalToMap(-1,-1).Y=-1");

            // 中心点：LocalToMap(48, 48) → (1, 1) 在 32x32 网格中
            var m3 = map.LocalToMap(new Vector2(48, 48));
            AssertEqual(1, m3.X, "LocalToMap(48,48).X=1");
            AssertEqual(1, m3.Y, "LocalToMap(48,48).Y=1");
        }

        // ── 7. WorldToMap/MapToWorld（TileMap 有 Position 时考虑全局变换）──
        public static void TestWorldCoordinateConversion()
        {
            var ts = new TileSet { CellSize = new Vector2(16, 16) };
            var map = new TileMap("MovingMap") { TileSet = ts };
            map.Position = new Vector2(100, 50); // TileMap 全局偏移

            // (0,0) 本地 → 世界 (100, 50)
            var w0 = map.MapToWorld(new Vector2I(0, 0));
            AssertTrue(MathF.Abs(w0.X - 100) < Eps && MathF.Abs(w0.Y - 50) < Eps,
                $"MapToWorld(0,0)=(100,50)，实际=({w0.X},{w0.Y})");

            // (2, 3) 本地 → 世界 (100 + 32, 50 + 48) = (132, 98)
            var w1 = map.MapToWorld(new Vector2I(2, 3));
            AssertTrue(MathF.Abs(w1.X - 132) < Eps && MathF.Abs(w1.Y - 98) < Eps,
                $"MapToWorld(2,3)=(132,98)，实际=({w1.X},{w1.Y})");

            // 世界 → 格子：WorldToMap(132, 98) → (2, 3)
            var m1 = map.WorldToMap(new Vector2(132, 98));
            AssertEqual(2, m1.X, "WorldToMap(132,98).X=2");
            AssertEqual(3, m1.Y, "WorldToMap(132,98).Y=3");

            // 世界 → 格子：WorldToMap(107, 55) → (0, 0)
            var m0 = map.WorldToMap(new Vector2(107, 55));
            AssertEqual(0, m0.X, "WorldToMap(107,55).X=0");
            AssertEqual(0, m0.Y, "WorldToMap(107,55).Y=0");
        }

        // ── 8. AutoTileRules.PickTileForMask 贪心匹配 ──
        public static void TestAutoTilePickGreedy()
        {
            var rules = new AutoTileRules();
            // 4 边完整块（声明所有方向邻居）
            rules.AddCandidate(1, TileNeighbor.N | TileNeighbor.E | TileNeighbor.S | TileNeighbor.W);
            // 左边（只有 E 邻居）
            rules.AddCandidate(2, TileNeighbor.E);
            // 右边（只有 W 邻居）
            rules.AddCandidate(3, TileNeighbor.W);
            // 孤立块（无邻居期望）
            rules.AddCandidate(4, TileNeighbor.None);

            // 全部 4 边邻居 → 选最特异的 1（4 bits）
            int full = rules.PickTileForMask(
                TileNeighbor.N | TileNeighbor.E | TileNeighbor.S | TileNeighbor.W |
                TileNeighbor.NE | TileNeighbor.SE | TileNeighbor.SW | TileNeighbor.NW);
            AssertEqual(1, full, "全邻居 → 最特异 id=1");

            // 只有 E 邻居 → 选 id=2（1 bit，模板 ⊆ 实际）
            int leftEdge = rules.PickTileForMask(TileNeighbor.E);
            AssertEqual(2, leftEdge, "仅 E 邻居 → id=2");

            // 只有 W 邻居 → 选 id=3
            int rightEdge = rules.PickTileForMask(TileNeighbor.W);
            AssertEqual(3, rightEdge, "仅 W 邻居 → id=3");

            // 无邻居 → 选孤立块 id=4（模板 None，任何 actual 都匹配）
            int isolated = rules.PickTileForMask(TileNeighbor.None);
            AssertEqual(4, isolated, "无邻居 → id=4");

            // N+E 邻居（模板 N|E 没注册，但 E 和 N 单独的也没有）
            // 实际掩码=N|E，候选模板：id=1(NESW) ⊄ N|E、id=2(E) ⊆ N|E、id=3(W) ⊄、id=4(None) ⊆ → 选 id=4 或 id=2 谁位数多？id=2
            int ne = rules.PickTileForMask(TileNeighbor.N | TileNeighbor.E);
            AssertEqual(2, ne, "N|E 邻居 → id=2（仅 E 匹配）");
        }

        // ── 9. PickTileForMask 无匹配 → -1 ──
        public static void TestAutoTilePickNoMatch()
        {
            var rules = new AutoTileRules();
            // 只有模板 N 邻居
            rules.AddCandidate(10, TileNeighbor.N);

            // 实际掩码只有 E（模板 N ⊄ E）
            int miss = rules.PickTileForMask(TileNeighbor.E);
            AssertEqual(-1, miss, "无匹配候选应返回 -1");

            // 实际掩码有 N → 命中
            int hit = rules.PickTileForMask(TileNeighbor.N | TileNeighbor.NW);
            AssertEqual(10, hit, "实际含 N 时应命中 id=10");
        }

        // ── 10. TileMap.GetAutoTileMask 8 邻居位掩码计算 ──
        public static void TestGetAutoTileMask()
        {
            var map = new TileMap();
            map.AddLayer();

            // 中心 (1,1)，填 E、W、N 三邻居为地形
            map.SetCell(0, new Vector2I(1, 1), 0);
            map.SetCell(0, new Vector2I(2, 1), 0); // E
            map.SetCell(0, new Vector2I(0, 1), 0); // W
            map.SetCell(0, new Vector2I(1, 0), 0); // N

            var mask = map.GetAutoTileMask(0, new Vector2I(1, 1),
                (layer, c) => layer >= 0 && map.HasCell(layer, c));

            AssertTrue((mask & TileNeighbor.E) != 0, "掩码应含 E");
            AssertTrue((mask & TileNeighbor.W) != 0, "掩码应含 W");
            AssertTrue((mask & TileNeighbor.N) != 0, "掩码应含 N");
            AssertTrue((mask & TileNeighbor.S) == 0, "掩码不含 S");
            AssertTrue((mask & TileNeighbor.NE) == 0, "掩码不含 NE");
            AssertTrue((mask & TileNeighbor.NW) == 0, "掩码不含 NW");

            // 越界邻居按"非地形"处理
            map.ClearLayer(0);
            map.SetCell(0, new Vector2I(0, 0), 0);
            var cornerMask = map.GetAutoTileMask(0, new Vector2I(0, 0),
                (layer, c) => layer >= 0 && map.HasCell(layer, c));
            AssertEqual(TileNeighbor.None, cornerMask, "左上角所有邻居都越界/空 → None");
        }

        // ── 11. AutoFillLayer 两遍填充（快照后统一写入）──
        public static void TestAutoFillLayer()
        {
            var map = new TileMap();
            map.AddLayer();

            // 3×3 全填（除中心）→ 地形区域
            var terrain = new List<Vector2I>();
            for (int x = 0; x < 3; x++)
                for (int y = 0; y < 3; y++)
                {
                    if (x == 1 && y == 1) continue;
                    map.SetCell(0, new Vector2I(x, y), -1); // 先置空
                    terrain.Add(new Vector2I(x, y));
                }

            // 标记哪些格子是地形（所有 8 个外围 + 中心也是）
            bool IsTerrain(int layer, Vector2I c)
                => c.X >= 0 && c.X < 3 && c.Y >= 0 && c.Y < 3;

            // 注册 4 个候选（基于 N/S/E/W）
            var rules = new AutoTileRules();
            rules.AddCandidate(100, TileNeighbor.N | TileNeighbor.E | TileNeighbor.S | TileNeighbor.W); // 内部
            rules.AddCandidate(101, TileNeighbor.E | TileNeighbor.S);  // 左上
            rules.AddCandidate(102, TileNeighbor.W | TileNeighbor.S);  // 右上
            rules.AddCandidate(103, TileNeighbor.E | TileNeighbor.N);  // 左下
            rules.AddCandidate(104, TileNeighbor.W | TileNeighbor.N);  // 右下
            rules.AddCandidate(105, TileNeighbor.None);                // 中心（无邻居）
            map.SetAutoTileRules(rules);

            // 用 region 限制 3×3
            int filled = map.AutoFillLayer(0, IsTerrain,
                new List<Vector2I> {
                    new(0, 0), new(1, 0), new(2, 0),
                    new(0, 1), new(1, 1), new(2, 1),
                    new(0, 2), new(1, 2), new(2, 2),
                });

            AssertEqual(9, filled, "9 格都应填充");

            // 中心 (1,1)：4 邻居都在 3×3 内，mask=N|E|S|W → 选 id=100
            AssertEqual(100, map.GetCell(0, new Vector2I(1, 1)), "中心 (1,1) 应选 id=100");

            // 左上 (0,0)：邻居只有 E(1,0) + S(0,1) 在 3×3 内 → mask=E|S → 选 id=101
            AssertEqual(101, map.GetCell(0, new Vector2I(0, 0)), "左上 (0,0) 应选 id=101");

            // 右下 (2,2)：邻居 W(1,2) + N(2,1) → mask=W|N → 选 id=104
            AssertEqual(104, map.GetCell(0, new Vector2I(2, 2)), "右下 (2,2) 应选 id=104");
        }

        // ── 12. AutoFillLayer 无规则 → 0 ──
        public static void TestAutoFillLayerNoRules()
        {
            var map = new TileMap();
            map.AddLayer();
            map.SetCell(0, new Vector2I(0, 0), 0);

            int filled = map.AutoFillLayer(0, (l, c) => true);
            AssertEqual(0, filled, "无 AutoTileRules 应返回 0");
        }

        // ── 13. TileNeighbor 位掩码组合 + TileNeighborDir.All 8 项 ──
        public static void TestTileNeighborBits()
        {
            // All 数组 8 项，位与顺序匹配
            AssertEqual(8, TileNeighborDir.All.Length, "TileNeighborDir.All 8 方向");
            AssertEqual(TileNeighbor.N, TileNeighborDir.All[0].Bit, "第 0 项 N");
            AssertEqual(TileNeighbor.NW, TileNeighborDir.All[7].Bit, "第 7 项 NW");

            // 偏移检查
            AssertEqual(0, TileNeighborDir.All[0].Dx, "N 偏移 X=0");
            AssertEqual(-1, TileNeighborDir.All[0].Dy, "N 偏移 Y=-1");
            AssertEqual(-1, TileNeighborDir.All[7].Dx, "NW 偏移 X=-1");
            AssertEqual(-1, TileNeighborDir.All[7].Dy, "NW 偏移 Y=-1");

            // 位组合 + | 
            var all = TileNeighbor.N | TileNeighbor.E | TileNeighbor.S | TileNeighbor.W |
                      TileNeighbor.NE | TileNeighbor.SE | TileNeighbor.SW | TileNeighbor.NW;
            AssertEqual(255, (int)all, "8 位全开 = 255");

            // 无效位
            AssertEqual(0, (int)TileNeighbor.None, "None=0");

            // 单位值
            AssertEqual(1, (int)TileNeighbor.N, "N=1");
            AssertEqual(128, (int)TileNeighbor.NW, "NW=128");
        }

        // ── 14. PickAutoTile 单点查询（不写入）──
        public static void TestPickAutoTileQuery()
        {
            var map = new TileMap();
            map.AddLayer();
            // 填一条横线（中心 (1,1)，左右邻居存在）
            map.SetCell(0, new Vector2I(0, 1), -1);
            map.SetCell(0, new Vector2I(1, 1), -1);
            map.SetCell(0, new Vector2I(2, 1), -1);

            var rules = new AutoTileRules();
            rules.AddCandidate(200, TileNeighbor.E | TileNeighbor.W); // 横线中段
            rules.AddCandidate(201, TileNeighbor.E);                  // 左端
            rules.AddCandidate(202, TileNeighbor.W);                  // 右端
            map.SetAutoTileRules(rules);

            bool IsTerrain(int layer, Vector2I c)
                => layer == 0 && (c.X >= 0 && c.X <= 2 && c.Y == 1);

            // 左端 (0,1)：E 邻居 → 选 201
            AssertEqual(201, map.PickAutoTile(0, new Vector2I(0, 1), IsTerrain),
                "左端应选 id=201");

            // 中段 (1,1)：E+W → 选 200
            AssertEqual(200, map.PickAutoTile(0, new Vector2I(1, 1), IsTerrain),
                "中段应选 id=200");

            // 右端 (2,1)：W 邻居 → 选 202
            AssertEqual(202, map.PickAutoTile(0, new Vector2I(2, 1), IsTerrain),
                "右端应选 id=202");
        }

        // ── 15. Vector2I 基础操作 ──
        public static void TestVector2I()
        {
            var a = new Vector2I(3, 4);
            var b = new Vector2I(1, 2);
            var sum = a + b;
            AssertEqual(4, sum.X, "a+b.X=4");
            AssertEqual(6, sum.Y, "a+b.Y=6");

            var diff = a - b;
            AssertEqual(2, diff.X, "a-b.X=2");
            AssertEqual(2, diff.Y, "a-b.Y=2");

            AssertTrue(a == new Vector2I(3, 4), "Vector2I == 相等");
            AssertTrue(a != b, "Vector2I != 不等");
            AssertTrue(a.Equals(new Vector2I(3, 4)), "Vector2I.Equals");

            // GetHashCode 区分
            AssertTrue(a.GetHashCode() != b.GetHashCode(), "不同格子应有不同 hash");
        }
    }
}
