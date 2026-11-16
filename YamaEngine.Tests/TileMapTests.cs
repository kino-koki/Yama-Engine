// TileMapTests.cs —— 追平 Godot 3 的 TileMap + TileSet（瓦片地图）
// 覆盖：set/get/erase_cell、层管理、map↔local↔world 坐标转换、clear_layer、used_cells。

using System;
using System.Numerics;
using YamaEngine.UI;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests
{
    public static class TileMapTests
    {
        private const float Tol = 1e-3f;

        private static void AssertEqual<T>(T got, T want, string msg)
        {
            if (!Equals(got, want)) throw new Exception($"{msg} (got {got}, want {want}).");
        }

        private static void AssertNear(float got, float want, string msg, float tol = Tol)
        {
            if (MathF.Abs(got - want) > tol) throw new Exception($"{msg} (got {got}, want {want}±{tol}).");
        }

        private static void AssertTrue(bool cond, string msg)
        {
            if (!cond) throw new Exception(msg);
        }

        // ── set_cell / get_cell 基本放置 ──
        public static void TestSetGetCell()
        {
            var map = new TileMap("M");
            var coords = new Vector2I(3, 2);

            AssertEqual(-1, map.GetCell(0, coords), "Empty cell returns -1.");
            map.SetCell(0, coords, 5);
            AssertEqual(5, map.GetCell(0, coords), "Cell now has tile 5.");
            AssertTrue(map.HasCell(0, coords), "HasCell true after set.");
        }

        // ── erase_cell 清除格子 ──
        public static void TestEraseCell()
        {
            var map = new TileMap("M");
            var coords = new Vector2I(1, 1);
            map.SetCell(0, coords, 7);
            AssertEqual(7, map.GetCell(0, coords), "Tile placed.");

            map.EraseCell(0, coords);
            AssertEqual(-1, map.GetCell(0, coords), "Cell erased -> -1.");
            AssertFalse(map.HasCell(0, coords), "HasCell false after erase.");
        }

        // ── set_cell with -1 等价于 erase ──
        public static void TestSetCellNegativeErases()
        {
            var map = new TileMap("M");
            var coords = new Vector2I(2, 3);
            map.SetCell(0, coords, 4);
            map.SetCell(0, coords, -1);
            AssertEqual(-1, map.GetCell(0, coords), "set_cell -1 erases cell.");
            AssertEqual(0, map.GetUsedCellsCount(0), "Used cells count back to 0.");
        }

        // ── 多层独立 ──
        public static void TestMultipleLayersIndependent()
        {
            var map = new TileMap("M");
            int bg = map.AddLayer();
            int fg = map.AddLayer();
            var coords = new Vector2I(0, 0);

            map.SetCell(bg, coords, 1);
            map.SetCell(fg, coords, 2);

            AssertEqual(1, map.GetCell(bg, coords), "BG layer has tile 1.");
            AssertEqual(2, map.GetCell(fg, coords), "FG layer has tile 2.");
            AssertEqual(3, map.LayerCount, "Default layer + 2 added = 3.");
        }

        // ── clear_layer 清空整层 ──
        public static void TestClearLayer()
        {
            var map = new TileMap("M");
            map.SetCell(0, new Vector2I(0, 0), 1);
            map.SetCell(0, new Vector2I(1, 0), 2);
            map.SetCell(0, new Vector2I(2, 0), 3);
            AssertEqual(3, map.GetUsedCellsCount(0), "Three cells before clear.");

            map.ClearLayer(0);
            AssertEqual(0, map.GetUsedCellsCount(0), "Layer cleared -> 0 used cells.");
            AssertEqual(-1, map.GetCell(0, new Vector2I(0, 0)), "Individual cell empty after clear.");
        }

        // ── get_used_cells 返回所有坐标 ──
        public static void TestGetUsedCells()
        {
            var map = new TileMap("M");
            map.SetCell(0, new Vector2I(0, 0), 1);
            map.SetCell(0, new Vector2I(2, 3), 1);
            var cells = map.GetUsedCells(0);

            AssertEqual(2, cells.Count, "Two used cells.");
            AssertTrue(cells.Contains(new Vector2I(0, 0)), "Contains (0,0).");
            AssertTrue(cells.Contains(new Vector2I(2, 3)), "Contains (2,3).");
        }

        // ── 越界层返回 -1 / 忽略写入 ──
        public static void TestInvalidLayerBounds()
        {
            var map = new TileMap("M");
            AssertEqual(-1, map.GetCell(5, new Vector2I(0, 0)), "Invalid layer get -> -1.");
            map.SetCell(5, new Vector2I(0, 0), 1); // 不崩溃
            AssertEqual(-1, map.GetCell(5, new Vector2I(0, 0)), "Invalid layer set ignored.");
            AssertEqual(0, map.GetUsedCellsCount(5), "Invalid layer used count 0.");
        }

        // ── map_to_local / local_to_map 坐标转换（cell_size=16 默认） ──
        public static void TestMapToLocal()
        {
            var map = new TileMap("M");
            var cell = new Vector2I(3, 2);
            var local = map.MapToLocal(cell);

            AssertNear(48f, local.X, "Cell (3,2) local X = 3*16=48.");
            AssertNear(32f, local.Y, "Cell (3,2) local Y = 2*16=32.");
        }

        // ── local_to_map 向下取整 ──
        public static void TestLocalToMapFloor()
        {
            var map = new TileMap("M");
            // (50, 35) → floor(50/16)=3, floor(35/16)=2
            var cell = map.LocalToMap(new Vector2(50f, 35f));
            AssertEqual(3, cell.X, "Local (50,35) -> cell X=3.");
            AssertEqual(2, cell.Y, "Local (50,35) -> cell Y=2.");

            // 负坐标也向下取整：(-1, -1) → floor(-1/16)=-1
            var negCell = map.LocalToMap(new Vector2(-1f, -1f));
            AssertEqual(-1, negCell.X, "Local (-1,-1) -> cell X=-1.");
            AssertEqual(-1, negCell.Y, "Local (-1,-1) -> cell Y=-1.");
        }

        // ── world_to_map 考虑 TileMap 全局位置 ──
        public static void TestWorldToMapWithTranslation()
        {
            var map = new TileMap("M") { Position = new Vector2(100f, 50f) };
            // 世界 (148, 82)：减去 TileMap 位置 (100,50) → 本地 (48,32) → cell (3,2)
            var cell = map.WorldToMap(new Vector2(148f, 82f));
            AssertEqual(3, cell.X, "World (148,82) with offset (100,50) -> cell X=3.");
            AssertEqual(2, cell.Y, "World (148,82) with offset (100,50) -> cell Y=2.");
        }

        // ── map_to_world 用全局变换（含 TileMap 位移） ──
        public static void TestMapToWorldWithTranslation()
        {
            var map = new TileMap("M") { Position = new Vector2(100f, 50f) };
            var world = map.MapToWorld(new Vector2I(0, 0));
            AssertNear(100f, world.X, "Cell (0,0) world X = tilemap X + 0.");
            AssertNear(50f, world.Y, "Cell (0,0) world Y = tilemap Y + 0.");
        }

        // ── 自定义 cell_size 影响 map_to_local ──
        public static void TestCustomCellSize()
        {
            var tileSet = new TileSet { CellSize = new Vector2(32f, 32f) };
            var map = new TileMap("M") { TileSet = tileSet };
            var local = map.MapToLocal(new Vector2I(2, 3));

            AssertNear(64f, local.X, "Cell (2,3) local X = 2*32=64.");
            AssertNear(96f, local.Y, "Cell (3,3) local Y = 3*32=96.");
        }

        // ── TileSet 添加/查询瓦片 ──
        public static void TestTileSetAddGet()
        {
            var set = new TileSet();
            var tile = new Tile(10) { TexturePath = "grass.png", Solid = true };
            set.AddTile(tile);

            AssertTrue(set.HasTile(10), "TileSet has tile 10.");
            AssertEqual(1, set.TileCount, "TileSet has 1 tile.");
            var got = set.GetTile(10);
            AssertTrue(got != null, "GetTile returns the tile.");
            AssertEqual("grass.png", got!.TexturePath, "Tile texture path.");
            AssertTrue(got.Solid, "Tile is solid.");
        }

        // ── TileSet 移除瓦片 ──
        public static void TestTileSetRemove()
        {
            var set = new TileSet();
            set.AddTile(new Tile(1));
            set.AddTile(new Tile(2));
            AssertEqual(2, set.TileCount, "Two tiles.");

            set.RemoveTile(1);
            AssertEqual(1, set.TileCount, "One tile after remove.");
            AssertEqual(null, set.GetTile(1), "Removed tile returns null.");
        }

        // ── Tile 自定义数据 ──
        public static void TestTileCustomData()
        {
            var tile = new Tile(5);
            tile.CustomData["damage"] = 25;
            tile.CustomData["walkable"] = false;

            AssertEqual(25, tile.CustomData["damage"], "Custom damage=25.");
            AssertEqual(false, tile.CustomData["walkable"], "Custom walkable=false.");
        }

        // ── Vector2I 相等性和哈希 ──
        public static void TestVector2IEquality()
        {
            var a = new Vector2I(3, 4);
            var b = new Vector2I(3, 4);
            var c = new Vector2I(3, 5);

            AssertTrue(a == b, "Equal Vector2I.");
            AssertTrue(a != c, "Different Vector2I.");
            AssertTrue(a.Equals(b), "Equals method.");
            AssertEqual(a.GetHashCode(), b.GetHashCode(), "Same hash for equal coords.");
        }

        private static void AssertFalse(bool cond, string msg)
        {
            if (cond) throw new Exception(msg);
        }
    }
}
