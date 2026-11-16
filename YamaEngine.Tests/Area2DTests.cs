// Area2DTests.cs —— 追平 Godot 3 的 Area2D + CollisionShape2D（2D 重叠检测）
// 覆盖：圆/矩形/胶囊形状重叠、entered/exited 事件、monitoring/monitorable 对称、offset、多层。

using System;
using System.Collections.Generic;
using System.Numerics;
using YamaEngine.UI;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests
{
    public static class Area2DTests
    {
        private const float Tol = 1e-3f;

        // 记录事件的辅助回调收集器
        private sealed class EventLog
        {
            public List<string> Entered = new();
            public List<string> Exited = new();
            public void Wire(Area2D area)
            {
                area.AreaEntered += e => Entered.Add(e.Other.Name);
                area.AreaExited += e => Exited.Add(e.Other.Name);
            }
        }

        // ── 圆-圆重叠：相距小于半径和 → entered ──
        public static void TestCircleCircleOverlap()
        {
            var world = new Area2DWorld();
            var a = new Area2D("A") { Shape = new CircleShape2D(10f), Position = new Vector2(0, 0) };
            var b = new Area2D("B") { Shape = new CircleShape2D(10f), Position = new Vector2(15, 0) }; // 距 15 < 20
            world.AddArea(a);
            world.AddArea(b);

            var logA = new EventLog(); logA.Wire(a);
            var logB = new EventLog(); logB.Wire(b);

            world.Update();
            AssertEqual(1, a.OverlappingAreas.Count, "A overlaps B.");
            AssertEqual(1, b.OverlappingAreas.Count, "B overlaps A.");
            AssertEqual("B", logA.Entered[0], "A got area_entered for B.");
            AssertEqual("A", logB.Entered[0], "B got area_entered for A.");
        }

        // ── 圆-圆分离：相距大于半径和 → 无重叠 ──
        public static void TestCircleCircleSeparate()
        {
            var world = new Area2DWorld();
            var a = new Area2D("A") { Shape = new CircleShape2D(10f), Position = new Vector2(0, 0) };
            var b = new Area2D("B") { Shape = new CircleShape2D(10f), Position = new Vector2(30, 0) }; // 距 30 > 20
            world.AddArea(a);
            world.AddArea(b);

            world.Update();
            AssertEqual(0, a.OverlappingAreas.Count, "A does not overlap B.");
            AssertEqual(0, b.OverlappingAreas.Count, "B does not overlap A.");
        }

        // ── entered 然后 exited：区域移开后触发 exited ──
        public static void TestEnterThenExit()
        {
            var world = new Area2DWorld();
            var a = new Area2D("A") { Shape = new CircleShape2D(10f), Position = new Vector2(0, 0) };
            var b = new Area2D("B") { Shape = new CircleShape2D(10f), Position = new Vector2(15, 0) };
            world.AddArea(a);
            world.AddArea(b);
            var logA = new EventLog(); logA.Wire(a);

            world.Update(); // 第一帧重叠
            AssertEqual("B", logA.Entered[0], "First frame: entered B.");
            AssertEqual(0, logA.Exited.Count, "No exit yet.");

            b.Position = new Vector2(100, 0); // 移开
            world.Update(); // 第二帧分离
            AssertEqual(0, a.OverlappingAreas.Count, "No overlap after moving apart.");
            AssertEqual(1, logA.Exited.Count, "Exit event fired.");
            AssertEqual("B", logA.Exited[0], "Exited B.");
        }

        // ── 矩形-矩形重叠 ──
        public static void TestRectRectOverlap()
        {
            var world = new Area2DWorld();
            var a = new Area2D("A") { Shape = new RectangleShape2D(10f, 10f), Position = new Vector2(0, 0) };
            var b = new Area2D("B") { Shape = new RectangleShape2D(10f, 10f), Position = new Vector2(15, 0) }; // |dx|=15 < 20
            world.AddArea(a);
            world.AddArea(b);

            world.Update();
            AssertEqual(1, a.OverlappingAreas.Count, "Rects overlap on X.");
        }

        // ── 矩形-矩形分离（仅 Y 错开）──
        public static void TestRectRectSeparateOnY()
        {
            var world = new Area2DWorld();
            var a = new Area2D("A") { Shape = new RectangleShape2D(10f, 5f), Position = new Vector2(0, 0) };
            var b = new Area2D("B") { Shape = new RectangleShape2D(10f, 5f), Position = new Vector2(0, 20f) }; // |dy|=20 > 10
            world.AddArea(a);
            world.AddArea(b);

            world.Update();
            AssertEqual(0, a.OverlappingAreas.Count, "Rects separated on Y.");
        }

        // ── 圆-矩形重叠（最近点法） ──
        public static void TestCircleRectOverlap()
        {
            var world = new Area2DWorld();
            var circle = new Area2D("C") { Shape = new CircleShape2D(5f), Position = new Vector2(0, 0) };
            var rect = new Area2D("R") { Shape = new RectangleShape2D(10f, 10f), Position = new Vector2(12, 0) }; // 最近点 (10,0) 距圆心 12-10=2 < 5
            world.AddArea(circle);
            world.AddArea(rect);

            world.Update();
            AssertEqual(1, circle.OverlappingAreas.Count, "Circle overlaps rect.");
            AssertEqual(1, rect.OverlappingAreas.Count, "Rect overlaps circle (symmetric).");
        }

        // ── 胶囊-圆重叠（用胶囊半径近似圆） ──
        public static void TestCapsuleCircleOverlap()
        {
            var world = new Area2DWorld();
            var cap = new Area2D("Cap") { Shape = new CapsuleShape2D(10f, 40f), Position = new Vector2(0, 0) };
            var circ = new Area2D("C") { Shape = new CircleShape2D(5f), Position = new Vector2(12, 0) }; // 12 < 10+5=15
            world.AddArea(cap);
            world.AddArea(circ);

            world.Update();
            AssertEqual(1, cap.OverlappingAreas.Count, "Capsule overlaps circle.");
        }

        // ── 形状 Offset 影响（形状在 Area2D 本地空间偏移） ──
        public static void TestShapeOffset()
        {
            var world = new Area2DWorld();
            // 形状 A 偏移到 (100,0)，Area 位置在 (0,0)，形状 B 在 (105,0)
            var a = new Area2D("A") { Shape = new CircleShape2D(10f) { Offset = new Vector2(100, 0) }, Position = new Vector2(0, 0) };
            var b = new Area2D("B") { Shape = new CircleShape2D(10f), Position = new Vector2(105, 0) };
            world.AddArea(a);
            world.AddArea(b);

            world.Update();
            AssertEqual(1, a.OverlappingAreas.Count, "Offset shape overlaps B (A shape center at 100,0).");
        }

        // ── monitoring/monitorable 对称：A 监测需 A.Monitoring && B.Monitorable ──
        public static void TestMonitorableSymmetry()
        {
            var world = new Area2DWorld();
            // B 不可被监测：即使 A 在监测，也看不到 B
            var a = new Area2D("A") { Shape = new CircleShape2D(10f), Position = new Vector2(0, 0), Monitoring = true };
            var b = new Area2D("B") { Shape = new CircleShape2D(10f), Position = new Vector2(5, 0), Monitorable = false };
            world.AddArea(a);
            world.AddArea(b);

            world.Update();
            AssertEqual(0, a.OverlappingAreas.Count, "A cannot see unmonitorable B.");
            AssertEqual(1, b.OverlappingAreas.Count, "B still sees monitorable A (B monitoring, A monitorable).");
        }

        // ── monitoring=false 的区域不主动检测 ──
        public static void TestMonitoringDisabled()
        {
            var world = new Area2DWorld();
            var a = new Area2D("A") { Shape = new CircleShape2D(10f), Position = new Vector2(0, 0), Monitoring = false };
            var b = new Area2D("B") { Shape = new CircleShape2D(10f), Position = new Vector2(5, 0), Monitoring = true, Monitorable = true };
            world.AddArea(a);
            world.AddArea(b);

            world.Update();
            AssertEqual(0, a.OverlappingAreas.Count, "A (not monitoring) sees nothing.");
            AssertEqual(1, b.OverlappingAreas.Count, "B sees monitorable A.");
        }

        // ── 形状为空的区域跳过 ──
        public static void TestNoShapeSkipped()
        {
            var world = new Area2DWorld();
            var a = new Area2D("A") { Shape = new CircleShape2D(10f), Position = new Vector2(0, 0) };
            var b = new Area2D("B") { Shape = null, Position = new Vector2(1, 0) };
            world.AddArea(a);
            world.AddArea(b);

            world.Update();
            AssertEqual(0, a.OverlappingAreas.Count, "No shape on B -> no overlap.");
        }

        // ── 三区域链：A-B 重叠、A-C 重叠、B-C 不重叠 ──
        public static void TestMultipleAreas()
        {
            var world = new Area2DWorld();
            var a = new Area2D("A") { Shape = new CircleShape2D(10f), Position = new Vector2(0, 0) };
            var b = new Area2D("B") { Shape = new CircleShape2D(10f), Position = new Vector2(15, 0) };
            var c = new Area2D("C") { Shape = new CircleShape2D(10f), Position = new Vector2(100, 100) };
            world.AddArea(a);
            world.AddArea(b);
            world.AddArea(c);

            world.Update();
            AssertEqual(1, a.OverlappingAreas.Count, "A overlaps B only.");
            AssertEqual(1, b.OverlappingAreas.Count, "B overlaps A only.");
            AssertEqual(0, c.OverlappingAreas.Count, "C overlaps nothing.");
        }

        private static void AssertEqual<T>(T got, T want, string msg)
        {
            if (!Equals(got, want)) throw new Exception($"{msg} (got {got}, want {want}).");
        }
    }
}
