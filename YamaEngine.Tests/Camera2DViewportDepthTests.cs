// Camera2DViewportDepthTests.cs —— Camera2D 世界↔屏幕投影（含旋转相机）/缩放/边界深度验证

using System;
using System.Numerics;
using YamaEngine.UI;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests
{
    public static class Camera2DViewportDepthTests
    {
        private static void AssertTrue(bool cond, string msg)
        {
            if (!cond) throw new Exception($"{msg}");
        }

        private static void AssertNear(float got, float want, string msg, float tol = 1e-3f)
        {
            if (MathF.Abs(got - want) > tol) throw new Exception($"{msg} (got {got}, want {want}).");
        }

        // ── 无旋转时 WorldToScreen：中心世界点映射到视口中心 ──
        public static void TestIdentityCenter()
        {
            var cam = new Camera2D("C") { ViewportSize = new Vector2(1000, 800) };
            cam.Position = new Vector2(0, 0);
            var s = cam.WorldToScreen(new Vector2(0, 0));
            AssertNear(500f, s.X, "World center→viewport center X.");
            AssertNear(400f, s.Y, "World center→viewport center Y.");
        }

        // ── 无旋转：世界点偏移映射（zoom=1）──
        public static void TestTranslateProjection()
        {
            var cam = new Camera2D("C") { ViewportSize = new Vector2(1000, 800) };
            cam.Position = new Vector2(100, 0);
            var s = cam.WorldToScreen(new Vector2(100, 0));
            AssertNear(500f, s.X, "Center world maps to center screen.");
        }

        // ── Zoom>1 放大：视野变小，点更靠外 ──
        public static void TestZoomIn()
        {
            var cam = new Camera2D("C") { ViewportSize = new Vector2(1000, 800), Zoom = 2f };
            var s = cam.WorldToScreen(new Vector2(100, 0));
            // (100-0)*2 + 500 = 700
            AssertNear(700f, s.X, "Zoom=2 doubles offset.");
        }

        // ── WorldToScreen/ScreenToWorld round-trip（无旋转）──
        public static void TestRoundTripNoRotation()
        {
            var cam = new Camera2D("C") { ViewportSize = new Vector2(1000, 800), Zoom = 1.5f };
            cam.Position = new Vector2(50, 30);
            var world = new Vector2(200, 150);
            var back = cam.ScreenToWorld(cam.WorldToScreen(world));
            AssertNear(200f, back.X, "Round-trip X.");
            AssertNear(150f, back.Y, "Round-trip Y.");
        }

        // ── 旋转相机：round-trip 仍成立 ──
        public static void TestRoundTripWithRotation()
        {
            var cam = new Camera2D("C")
            {
                ViewportSize = new Vector2(1000, 800),
                Zoom = 1.2f,
                Rotation = MathF.PI / 6f, // 30°
            };
            cam.Position = new Vector2(40, 20);
            var world = new Vector2(150, 80);
            var back = cam.ScreenToWorld(cam.WorldToScreen(world));
            AssertNear(150f, back.X, "Rotated round-trip X.", 1e-2f);
            AssertNear(80f, back.Y, "Rotated round-trip Y.", 1e-2f);
        }

        // ── 旋转 90°：正右方世界点投影到正上方屏幕（相机顺时针转，世界逆时针呈现）──
        public static void TestRotation90MapsRightToUp()
        {
            var cam = new Camera2D("C") { ViewportSize = new Vector2(1000, 800) };
            cam.Position = new Vector2(0, 0);
            cam.Rotation = MathF.PI / 2f; // 相机转 90°，世界右方在屏幕上变上方
            var s = cam.WorldToScreen(new Vector2(100, 0));
            // 逆旋转 -90°：(100,0)→(0,-100)；*1+center=(500, 400-100=300)
            AssertNear(500f, s.X, "X stays center.", 1e-2f);
            AssertNear(300f, s.Y, "Right world maps upward on screen.", 1e-2f);
        }

        // ── 旋转相机：相机正前方（本地 +X 对应的世界方向）映射到屏幕右侧 ──
        public static void TestRotationForwardToScreenRight()
        {
            var cam = new Camera2D("C") { ViewportSize = new Vector2(1000, 800) };
            cam.Position = new Vector2(0, 0);
            cam.Rotation = MathF.PI / 2f; // 本地 +X 指向世界 (0,+1)
            // 相机正前方（世界 (0,+100)）应映射到屏幕右侧
            var s = cam.WorldToScreen(new Vector2(0, 100));
            AssertNear(600f, s.X, "Local forward maps to screen right.", 1e-2f);
            AssertNear(400f, s.Y, "Screen Y stays center.", 1e-2f);
        }

        // ── GetVisibleBounds 无限制 ──
        public static void TestVisibleBoundsNoLimit()
        {
            var cam = new Camera2D("C") { ViewportSize = new Vector2(1000, 800), Zoom = 1f };
            cam.Position = new Vector2(0, 0);
            var (minX, minY, maxX, maxY) = cam.GetVisibleBounds();
            AssertNear(-500f, minX, "minX = -halfWidth.");
            AssertNear(500f, maxX, "maxX = +halfWidth.");
            AssertNear(-400f, minY, "minY = -halfHeight.");
            AssertNear(400f, maxY, "maxY = +halfHeight.");
        }

        // ── GetVisibleBounds Zoom 影响（zoom=2 视野减半）──
        public static void TestVisibleBoundsZoom()
        {
            var cam = new Camera2D("C") { ViewportSize = new Vector2(1000, 800), Zoom = 2f };
            var (minX, _, maxX, _) = cam.GetVisibleBounds();
            AssertNear(-250f, minX, "Zoom=2 halves half-width.");
            AssertNear(250f, maxX, "Zoom=2 halves half-width.");
        }

        // ── LimitEnabled 边界夹紧：相机靠近左边界时视野被夹住 ──
        public static void TestLimitClampsCamera()
        {
            var cam = new Camera2D("C")
            {
                ViewportSize = new Vector2(1000, 800),
                LimitEnabled = true,
                LimitLeft = -500, LimitRight = 500,
                LimitTop = -400, LimitBottom = 400,
            };
            cam.Position = new Vector2(-2000, 0); // 远超左边界
            var (minX, _, maxX, _) = cam.GetVisibleBounds();
            // 视野宽 1000 = [limitLeft, limitRight] 恰好，中心被夹到 -500+500=0
            AssertNear(-500f, minX, "Camera clamped to left limit.");
            AssertNear(500f, maxX, "maxX at right limit.");
        }

        // ── 旋转投影不破坏 HalfWidth/HalfHeight ──
        public static void TestHalfDimensionsUnaffectedByRotation()
        {
            var cam = new Camera2D("C") { ViewportSize = new Vector2(1000, 800), Zoom = 1f };
            cam.Rotation = MathF.PI / 4f;
            AssertNear(500f, cam.HalfWidth, "HalfWidth unaffected by rotation.");
            AssertNear(400f, cam.HalfHeight, "HalfHeight unaffected by rotation.");
        }

        // ── 投影线性性：两点中点的投影 = 投影的中点（无旋转）──
        public static void TestProjectionLinearity()
        {
            var cam = new Camera2D("C") { ViewportSize = new Vector2(1000, 800), Zoom = 1.3f };
            cam.Position = new Vector2(10, 20);
            var a = cam.WorldToScreen(new Vector2(0, 0));
            var b = cam.WorldToScreen(new Vector2(200, 100));
            var midWorld = new Vector2(100, 50);
            var midScreen = cam.WorldToScreen(midWorld);
            var avg = (a + b) * 0.5f;
            AssertNear(avg.X, midScreen.X, "Projection linear X.");
            AssertNear(avg.Y, midScreen.Y, "Projection linear Y.");
        }
    }
}
