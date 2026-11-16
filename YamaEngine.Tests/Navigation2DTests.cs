// Navigation2DTests.cs —— 追平 Godot 3 的 Navigation2D 网格寻路（A* / set_point_disabled / get_simple_path）

using System;
using System.Collections.Generic;
using System.Numerics;
using YamaEngine.UI;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests
{
    public static class Navigation2DTests
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

        private static void AssertNear(float got, float want, string msg, float tol = 1e-3f)
        {
            if (MathF.Abs(got - want) > tol) throw new Exception($"{msg} (got {got}, want {want}±{tol}).");
        }

        // ── 构造：宽高正确，初始全通行 ──
        public static void TestConstruction()
        {
            var nav = new Navigation2D(4, 3);
            AssertEqual(4, nav.Width, "Width=4.");
            AssertEqual(3, nav.Height, "Height=3.");
            AssertEqual(12, nav.CellCount, "CellCount=12.");
            AssertFalse(nav.IsPointSolid(0, 0), "Default not solid.");
        }

        // ── IsInBounds ──
        public static void TestInBounds()
        {
            var nav = new Navigation2D(4, 4);
            AssertTrue(nav.IsInBounds(0, 0), "Origin in bounds.");
            AssertTrue(nav.IsInBounds(3, 3), "Max in bounds.");
            AssertFalse(nav.IsInBounds(4, 0), "X out.");
            AssertFalse(nav.IsInBounds(0, 4), "Y out.");
            AssertFalse(nav.IsInBounds(-1, 0), "Negative.");
        }

        // ── SetPointSolid / ClearPointSolid ──
        public static void TestSolidToggle()
        {
            var nav = new Navigation2D(5, 5);
            nav.SetPointSolid(2, 2);
            AssertTrue(nav.IsPointSolid(2, 2), "Solid after set.");
            nav.ClearPointSolid(2, 2);
            AssertFalse(nav.IsPointSolid(2, 2), "Clear after clear.");
        }

        // ── IsPointSolid 越界返回 false（不抛） ──
        public static void TestSolidOutOfBounds()
        {
            var nav = new Navigation2D(3, 3);
            AssertFalse(nav.IsPointSolid(-1, 0), "Negative not solid.");
            AssertFalse(nav.IsPointSolid(3, 3), "Out of range not solid.");
        }

        // ── 直线路径：无障碍横向 3 格 ──
        public static void TestStraightPath()
        {
            var nav = new Navigation2D(5, 1);
            var path = nav.IdPath(0, 0, 4, 0);
            AssertEqual(5, path.Count, "5 cells in line.");
            AssertEqual((0, 0), path[0], "Start.");
            AssertEqual((4, 0), path[4], "End.");
        }

        // ── 起点即终点：路径单格 ──
        public static void TestSameStartEnd()
        {
            var nav = new Navigation2D(5, 5);
            var path = nav.IdPath(2, 2, 2, 2);
            AssertEqual(1, path.Count, "Single cell.");
        }

        // ── 绕墙路径：墙分隔，绕行找到路 ──
        public static void TestPathAroundWall()
        {
            var nav = new Navigation2D(5, 5);
            // 竖墙 x=2, y=0..3（留 y=4 开口）
            nav.SetColumnSolid(2, 0, 3);
            var path = nav.IdPath(0, 0, 4, 0);
            AssertTrue(path.Count > 0, "Path exists.");
            // 验证起点终点
            AssertEqual((0, 0), path[0], "Start.");
            AssertEqual((4, 0), path[^1], "End.");
            // 验证不穿墙：所有点非障碍
            foreach (var (x, y) in path)
                AssertFalse(nav.IsPointSolid(x, y), $"Cell ({x},{y}) should not be solid.");
        }

        // ── 完全封死：无路返回空 ──
        public static void TestNoPath()
        {
            var nav = new Navigation2D(3, 1);
            nav.SetColumnSolid(1, 0, 0);
            var path = nav.IdPath(0, 0, 2, 0);
            AssertEqual(0, path.Count, "No path → empty.");
        }

        // ── 起点或终点是障碍：无路 ──
        public static void TestSolidStartOrEnd()
        {
            var nav = new Navigation2D(5, 5);
            nav.SetPointSolid(0, 0);
            AssertEqual(0, nav.IdPath(0, 0, 4, 4).Count, "Solid start → no path.");

            var nav2 = new Navigation2D(5, 5);
            nav2.SetPointSolid(4, 4);
            AssertEqual(0, nav2.IdPath(0, 0, 4, 4).Count, "Solid end → no path.");
        }

        // ── 起点或终点越界：无路 ──
        public static void TestOutOfBoundsPath()
        {
            var nav = new Navigation2D(3, 3);
            AssertEqual(0, nav.IdPath(-1, 0, 2, 2).Count, "Start out of bounds.");
            AssertEqual(0, nav.IdPath(0, 0, 3, 3).Count, "End out of bounds.");
        }

        // ── 对角防穿角：只能对角穿过的窄口被阻挡 ──
        public static void TestDiagonalCornerBlocked()
        {
            var nav = new Navigation2D(3, 3);
            // 设置 L 形障碍，使得 (0,0)→(1,1) 只能靠对角穿过（两侧正邻被堵）
            // (1,0) 和 (0,1) 都是障碍 → (0,0)→(1,1) 对角应被禁
            nav.SetPointSolid(1, 0);
            nav.SetPointSolid(0, 1);
            var path = nav.IdPath(0, 0, 1, 1);
            // 无直达对角，需绕行或无路（(1,1) 可从其他方向到，但绕行成本高）
            // 关键：直接对角一步不成立，路径要么绕行要么无路
            AssertTrue(path.Count != 2, "Direct diagonal blocked (not 2-cell path).");
        }

        // ── 对角通行：正常对角允许 ──
        public static void TestDiagonalAllowed()
        {
            var nav = new Navigation2D(3, 3);
            // 两侧正邻都通行 → 对角一步可达
            var path = nav.IdPath(0, 0, 1, 1);
            AssertEqual(2, path.Count, "Diagonal step allowed.");
        }

        // ── PathLength：跳数 = Count - 1 ──
        public static void TestPathLength()
        {
            var nav = new Navigation2D(5, 1);
            AssertEqual(4, nav.PathLength((0, 0), (4, 0)), "4 jumps for 5 cells.");
            AssertEqual(-1, nav.PathLength((0, 0), (9, 9)), "No path → -1.");
        }

        // ── GetSimplePath：世界坐标 + 格子中心换算 ──
        public static void TestSimplePathWorldCoords()
        {
            var nav = new Navigation2D(5, 1);
            var path = nav.GetSimplePath((0, 0), (2, 0), cellSize: 32f);
            AssertEqual(3, path.Count, "3 world points.");
            // 格子 (0,0) 中心 = (0.5×32, 0.5×32) = (16, 16)
            AssertNear(16f, path[0].X, "Center x=16.");
            AssertNear(16f, path[0].Y, "Center y=16.");
            // 格子 (2,0) 中心 = (2.5×32, 0.5×32) = (80, 16)
            AssertNear(80f, path[2].X, "Center x=80.");
        }

        // ── GetSimplePath：带 offset 偏移 ──
        public static void TestSimplePathOffset()
        {
            var nav = new Navigation2D(5, 1);
            var off = new Vector2(100f, 50f);
            var path = nav.GetSimplePath((0, 0), (0, 0), cellSize: 32f, offset: off);
            AssertNear(116f, path[0].X, "X=100+16=116.");
            AssertNear(66f, path[0].Y, "Y=50+16=66.");
        }

        // ── SetRowSolid / SetColumnSolid 批量 ──
        public static void TestBatchSolid()
        {
            var nav = new Navigation2D(5, 5);
            nav.SetRowSolid(2, 0, 4);
            for (int x = 0; x < 5; x++)
                AssertTrue(nav.IsPointSolid(x, 2), $"Row y=2 x={x} solid.");

            nav.SetColumnSolid(3, 0, 4);
            for (int y = 0; y < 5; y++)
                AssertTrue(nav.IsPointSolid(3, y), $"Col x=3 y={y} solid.");
        }

        // ── 复杂迷宫：路径存在且合法（不穿障碍） ──
        public static void TestMazePathValid()
        {
            var nav = new Navigation2D(7, 7);
            // 蛇形墙
            nav.SetRowSolid(1, 0, 5);
            nav.SetRowSolid(3, 1, 6);
            nav.SetRowSolid(5, 0, 5);
            var path = nav.IdPath(0, 0, 6, 6);
            AssertTrue(path.Count > 0, "Maze has path.");
            foreach (var (x, y) in path)
                AssertFalse(nav.IsPointSolid(x, y), $"Maze cell ({x},{y}) not solid.");
            AssertEqual((0, 0), path[0], "Maze start.");
            AssertEqual((6, 6), path[^1], "Maze end.");
        }

        // ── 构造非法参数抛异常 ──
        public static void TestInvalidConstruction()
        {
            bool threw = false;
            try { _ = new Navigation2D(0, 5); }
            catch (ArgumentOutOfRangeException) { threw = true; }
            AssertTrue(threw, "Zero width throws.");
        }
    }
}
