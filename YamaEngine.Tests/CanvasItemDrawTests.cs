// CanvasItemDrawTests.cs —— 追平 Godot 3 的 CanvasItem 自定义绘制命令（draw_* API）
// 覆盖：各 draw_* 录入、immediate-mode 每帧清空、本地空间命令、全局变换转到世界空间。

using System;
using System.Collections.Generic;
using System.Numerics;
using YamaEngine.UI;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests
{
    public static class CanvasItemDrawTests
    {
        private static readonly Vector4 Red = new(1, 0, 0, 1);
        private static readonly Vector4 White = Vector4.One;

        // ── draw_rect 录入：类型/位置/尺寸/填充 ──
        public static void TestDrawRectRecordsCommand()
        {
            var node = new Node2D("N");
            node.DrawRect(new Vector2(10, 20), new Vector2(30, 40), Red, filled: true, width: 2f);

            AssertEqual(1, node.DrawCommands.Count, "One rect command recorded.");
            var cmd = node.DrawCommands[0];
            AssertEqual(DrawPrimitive.Rect, cmd.Primitive, "Primitive is Rect.");
            AssertNear(10f, cmd.A.X, "Rect left.", 1e-4f);
            AssertNear(20f, cmd.A.Y, "Rect top.", 1e-4f);
            AssertNear(30f, cmd.Size.X, "Rect width.", 1e-4f);
            AssertNear(40f, cmd.Size.Y, "Rect height.", 1e-4f);
            AssertTrue(cmd.Filled, "Rect is filled.");
            AssertNear(2f, cmd.Width, "Rect outline width recorded.", 1e-4f);
            AssertNear(1f, cmd.Color.X, "Color R=1.", 1e-4f);
            AssertNear(0f, cmd.Color.Y, "Color G=0.", 1e-4f);
        }

        // ── draw_line 录入：起终点/颜色/宽度 ──
        public static void TestDrawLineRecordsCommand()
        {
            var node = new Node2D("N");
            node.DrawLine(new Vector2(0, 0), new Vector2(100, 50), White, width: 3f);

            AssertEqual(1, node.DrawCommands.Count, "One line command recorded.");
            var cmd = node.DrawCommands[0];
            AssertEqual(DrawPrimitive.Line, cmd.Primitive, "Primitive is Line.");
            AssertNear(0f, cmd.A.X, "Line start X.", 1e-4f);
            AssertNear(100f, cmd.B.X, "Line end X.", 1e-4f);
            AssertNear(50f, cmd.B.Y, "Line end Y.", 1e-4f);
            AssertNear(3f, cmd.Width, "Line width.", 1e-4f);
            AssertFalse(cmd.Filled, "Line is not filled.");
        }

        // ── draw_circle 录入：圆心/半径/段数 ──
        public static void TestDrawCircleRecordsCommand()
        {
            var node = new Node2D("N");
            node.DrawCircle(new Vector2(5, 5), 25f, Red, filled: false, segments: 32);

            AssertEqual(1, node.DrawCommands.Count, "One circle command recorded.");
            var cmd = node.DrawCommands[0];
            AssertEqual(DrawPrimitive.Circle, cmd.Primitive, "Primitive is Circle.");
            AssertNear(5f, cmd.A.X, "Circle center X.", 1e-4f);
            AssertNear(25f, cmd.Radius, "Circle radius.", 1e-4f);
            AssertEqual(32, cmd.CircleSegments, "Circle segments.");
            AssertFalse(cmd.Filled, "Circle outline only.");
        }

        // ── draw_colored_polygon 录入：顶点列表 ──
        public static void TestDrawPolygonRecordsVertices()
        {
            var node = new Node2D("N");
            var tri = new List<Vector2> { new(0, 0), new(10, 0), new(5, 10) };
            node.DrawColoredPolygon(tri, White);

            AssertEqual(1, node.DrawCommands.Count, "One polygon command recorded.");
            var cmd = node.DrawCommands[0];
            AssertEqual(DrawPrimitive.Polygon, cmd.Primitive, "Primitive is Polygon.");
            AssertEqual(3, cmd.Points.Count, "Polygon has 3 vertices.");
            AssertNear(10f, cmd.Points[1].X, "Second vertex X.", 1e-4f);
            AssertNear(10f, cmd.Points[2].Y, "Third vertex Y.", 1e-4f);
            AssertTrue(cmd.Filled, "Polygon filled.");
        }

        // ── draw_polyline 录入：不闭合折线 ──
        public static void TestDrawPolylineRecordsCommand()
        {
            var node = new Node2D("N");
            var pts = new List<Vector2> { new(0, 0), new(10, 0), new(10, 10) };
            node.DrawPolyline(pts, White, width: 2f);

            AssertEqual(1, node.DrawCommands.Count, "One polyline command.");
            var cmd = node.DrawCommands[0];
            AssertEqual(DrawPrimitive.Polyline, cmd.Primitive, "Primitive is Polyline.");
            AssertEqual(3, cmd.Points.Count, "Polyline vertices.");
            AssertFalse(cmd.Filled, "Polyline not filled.");
            AssertNear(2f, cmd.Width, "Polyline width.", 1e-4f);
        }

        // ── immediate mode：每帧清空，命令不跨帧累积 ──
        public static void TestClearDrawCommandsEmptiesBuffer()
        {
            var node = new Node2D("N");
            node.DrawRect(new Vector2(0, 0), new Vector2(1, 1), Red);
            node.DrawLine(new Vector2(0, 0), new Vector2(1, 1), White);
            AssertEqual(2, node.DrawCommands.Count, "Two commands before clear.");

            node.ClearDrawCommands();
            AssertEqual(0, node.DrawCommands.Count, "Buffer emptied after ClearDrawCommands (immediate mode).");
        }

        // ── 本地空间命令按全局变换转到世界空间（平移） ──
        public static void TestWorldDrawCommandsApplyTranslation()
        {
            var node = new Node2D("N") { Position = new Vector2(100, 50) };
            node.DrawRect(new Vector2(0, 0), new Vector2(10, 10), Red);

            var world = node.GetWorldDrawCommands();
            AssertEqual(1, world.Count, "One world command.");
            // 左上角本地 (0,0) → 世界 (100,50)
            AssertNear(100f, world[0].A.X, "Rect world left = local + node X.", 1e-4f);
            AssertNear(50f, world[0].A.Y, "Rect world top = local + node Y.", 1e-4f);
            // 本地命令未被修改
            AssertNear(0f, node.DrawCommands[0].A.X, "Local command unchanged.", 1e-4f);
        }

        // ── 世界变换含旋转：矩形角点按角度转 ──
        public static void TestWorldDrawCommandsApplyRotation()
        {
            // 旋转 90°（π/2）：本地 (10,0) → 世界 (0,10)
            var node = new Node2D("N") { Position = new Vector2(0, 0), Rotation = MathF.PI / 2f };
            node.DrawCircle(new Vector2(10, 0), 5f, Red);

            var world = node.GetWorldDrawCommands();
            AssertNear(0f, world[0].A.X, "Rotated point X ≈ 0.", 1e-3f);
            AssertNear(10f, world[0].A.Y, "Rotated point Y ≈ 10.", 1e-3f);
        }

        // ── 世界变换含缩放：圆心按 scale 缩放 ──
        public static void TestWorldDrawCommandsApplyScale()
        {
            var node = new Node2D("N") { Position = new Vector2(0, 0), Scale = new Vector2(2, 2) };
            node.DrawCircle(new Vector2(10, 10), 5f, Red);

            var world = node.GetWorldDrawCommands();
            AssertNear(20f, world[0].A.X, "Scaled point X = 10*2.", 1e-4f);
            AssertNear(20f, world[0].A.Y, "Scaled point Y = 10*2.", 1e-4f);
            // radius 是标量尺寸，不随矩阵变换（保持原值）
            AssertNear(5f, world[0].Radius, "Radius scalar unchanged by transform.", 1e-4f);
        }

        // ── 父节点变换影响子节点绘制命令的世界空间 ──
        public static void TestWorldDrawCommandsInheritParentTransform()
        {
            var parent = new Node2D("Parent") { Position = new Vector2(50, 0) };
            var child = new Node2D("Child") { Position = new Vector2(10, 0) };
            parent.AddChild(child);
            child.DrawRect(new Vector2(0, 0), new Vector2(5, 5), Red);

            // 子本地 (0,0) → 子全局 (60,0)（父 X50 + 子 X10）
            var world = child.GetWorldDrawCommands();
            AssertNear(60f, world[0].A.X, "Child command inherits parent translation.", 1e-4f);
            AssertNear(0f, world[0].A.Y, "Child command Y unchanged.", 1e-4f);
        }

        private static void AssertEqual<T>(T got, T want, string msg)
        {
            if (!Equals(got, want)) throw new Exception($"{msg} (got {got}, want {want}).");
        }

        private static void AssertNear(float got, float want, string msg, float tol)
        {
            if (MathF.Abs(got - want) > tol) throw new Exception($"{msg} (got {got}, want {want}±{tol}).");
        }

        private static void AssertTrue(bool cond, string msg)
        {
            if (!cond) throw new Exception(msg);
        }

        private static void AssertFalse(bool cond, string msg)
        {
            if (cond) throw new Exception(msg);
        }
    }
}
