// Parallax2DTests.cs —— 追平 Godot 3 的 ParallaxBackground / ParallaxLayer 视差滚动语义
// 覆盖：MotionScale 相对移动、ScrollOffset 计算、MotionMirroring 平铺取模、
//       ScrollBaseOffset 叠加、Layer 添加/移除、Camera 为 null 安全、PredictScreenOffset 便捷断言。

using System;
using System.Collections.Generic;
using System.Numerics;
using YamaEngine.UI;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests
{
    public static class Parallax2DTests
    {
        // ── MotionScale=(0,0)：层固定不动（无限远） ──
        public static void TestParallaxLayerMotionScaleZeroStaysFixed()
        {
            var cam = new Camera2D("Cam") { Position = new Vector2(0, 0), Zoom = 1f, ViewportSize = new Vector2(1920, 1080) };
            var bg = new ParallaxBackground("BG") { Camera = cam };
            var layer = new ParallaxLayer("Far")
            {
                MotionScale = new Vector2(0, 0),
            };
            bg.AddLayer(layer);

            bg.UpdateScroll();   // 相机在原点
            var pos0 = layer.Position;

            cam.Position = new Vector2(1000, 500);   // 相机大移
            bg.UpdateScroll();

            AssertNear(pos0.X, layer.Position.X, 0.01f, "MotionScale=(0,0) must keep layer X fixed.");
            AssertNear(pos0.Y, layer.Position.Y, 0.01f, "MotionScale=(0,0) must keep layer Y fixed.");
        }

        // ── MotionScale=(1,1)：层与世界同步反向移动 ──
        public static void TestParallaxLayerMotionScaleOneFollowsWorld()
        {
            var cam = new Camera2D("Cam") { Position = new Vector2(0, 0), Zoom = 1f, ViewportSize = new Vector2(1920, 1080) };
            var bg = new ParallaxBackground("BG") { Camera = cam };
            var layer = new ParallaxLayer("World") { MotionScale = new Vector2(1, 1) };
            bg.AddLayer(layer);

            cam.Position = new Vector2(300, 200);
            bg.UpdateScroll();

            // ScrollOffset = -camera_center = (-300,-200)；screen = 0 + (-300,-200)*1
            AssertNear(-300f, layer.Position.X, 0.01f, "MotionScale=(1,1) must shift X by -camera.X.");
            AssertNear(-200f, layer.Position.Y, 0.01f, "MotionScale=(1,1) must shift Y by -camera.Y.");
        }

        // ── MotionScale=(0.5,0.5)：半速视差 ──
        public static void TestParallaxLayerHalfSpeed()
        {
            var cam = new Camera2D("Cam") { Position = new Vector2(400, 0), Zoom = 1f, ViewportSize = new Vector2(1920, 1080) };
            var bg = new ParallaxBackground("BG") { Camera = cam };
            var layer = new ParallaxLayer("Mid") { MotionScale = new Vector2(0.5f, 0.5f) };
            bg.AddLayer(layer);

            bg.UpdateScroll();
            // screen = (-400,0)*0.5 = (-200,0)
            AssertNear(-200f, layer.Position.X, 0.01f, "Half-speed X = -camera.X * 0.5 = -200.");
            AssertNear(0f, layer.Position.Y, 0.01f, "Half-speed Y = 0.");
        }

        // ── ScrollOffset = -camera_center ──
        public static void TestParallaxScrollOffsetEqualsNegativeCamera()
        {
            var cam = new Camera2D("Cam") { Position = new Vector2(123, 456), Zoom = 1f, ViewportSize = new Vector2(1920, 1080) };
            var bg = new ParallaxBackground("BG") { Camera = cam };

            bg.UpdateScroll();
            AssertNear(-123f, bg.ScrollOffset.X, 0.01f, "ScrollOffset.X = -camera.Center.X.");
            AssertNear(-456f, bg.ScrollOffset.Y, 0.01f, "ScrollOffset.Y = -camera.Center.Y.");
        }

        // ── MotionMirroring 平铺取模 ──
        public static void TestParallaxLayerMotionMirroringWraps()
        {
            var cam = new Camera2D("Cam") { Position = new Vector2(5000, 0), Zoom = 1f, ViewportSize = new Vector2(1920, 1080) };
            var bg = new ParallaxBackground("BG") { Camera = cam };
            var layer = new ParallaxLayer("Tile")
            {
                MotionScale = new Vector2(1, 1),
                MotionMirroring = new Vector2(256, 0),   // X 每 256px 平铺
            };
            bg.AddLayer(layer);

            bg.UpdateScroll();
            // raw = (-5000) % 256 = -5000 + 20*256 = 120（Python 风格取模）
            AssertNear(120f, layer.Position.X, 0.01f, "Mirroring.X=256 must wrap -5000 into [0,256) = 120.");
            AssertNear(0f, layer.Position.Y, 0.01f, "No Y mirroring: raw (-5000*0)=0.");
        }

        // ── ScrollBaseOffset 叠加 ──
        public static void TestParallaxScrollBaseOffsetAdds()
        {
            var cam = new Camera2D("Cam") { Position = new Vector2(0, 0), Zoom = 1f, ViewportSize = new Vector2(1920, 1080) };
            var bg = new ParallaxBackground("BG")
            {
                Camera = cam,
                ScrollBaseOffset = new Vector2(100, 50),
            };
            var layer = new ParallaxLayer("L") { MotionScale = new Vector2(0, 0) };
            bg.AddLayer(layer);

            bg.UpdateScroll();
            // screen = base(100,50) + scroll(-0)*ms = (100,50)
            AssertNear(100f, layer.Position.X, 0.01f, "BaseOffset.X=100 must add to layer.");
            AssertNear(50f, layer.Position.Y, 0.01f, "BaseOffset.Y=50 must add to layer.");
        }

        // ── Camera 为 null 时 UpdateScroll 安全 ──
        public static void TestParallaxNullCameraSafe()
        {
            var bg = new ParallaxBackground("NoCam");   // Camera = null
            var layer = new ParallaxLayer("L");
            bg.AddLayer(layer);

            bg.UpdateScroll();   // 必须不抛异常
            AssertNear(0f, layer.Position.X, 0.01f, "No camera: layer.X must stay at default 0.");
            AssertNear(0f, layer.Position.Y, 0.01f, "No camera: layer.Y must stay at default 0.");
        }

        // ── Layer 添加/移除 ──
        public static void TestParallaxLayerAddRemove()
        {
            var bg = new ParallaxBackground("BG");
            var l1 = new ParallaxLayer("L1");
            var l2 = new ParallaxLayer("L2");
            bg.AddLayer(l1);
            bg.AddLayer(l2);
            AssertEqual(2, bg.LayerCount, "After 2 AddLayer: count=2.");

            bg.RemoveLayer(l1);
            AssertEqual(1, bg.LayerCount, "After RemoveLayer: count=1.");
            AssertTrue(bg.Layers[0] == l2, "Remaining layer must be L2.");
        }

        // ── PredictScreenOffset 静态便捷方法 ──
        public static void TestParallaxPredictScreenOffset()
        {
            var predicted = ParallaxBackground.PredictScreenOffset(
                new Vector2(200, 100),
                new Vector2(0.5f, 0.5f),
                Vector2.Zero);
            AssertNear(-100f, predicted.X, 0.01f, "Predict: -200*0.5=-100.");
            AssertNear(-50f, predicted.Y, 0.01f, "Predict: -100*0.5=-50.");
        }

        // ── 多层视差：不同 MotionScale 各自移动 ──
        public static void TestParallaxMultipleLayersDifferentScales()
        {
            var cam = new Camera2D("Cam") { Position = new Vector2(1000, 0), Zoom = 1f, ViewportSize = new Vector2(1920, 1080) };
            var bg = new ParallaxBackground("BG") { Camera = cam };
            var sky = new ParallaxLayer("Sky") { MotionScale = new Vector2(0, 0) };
            var mid = new ParallaxLayer("Mid") { MotionScale = new Vector2(0.5f, 0.5f) };
            var near = new ParallaxLayer("Near") { MotionScale = new Vector2(1, 1) };
            bg.AddLayer(sky);
            bg.AddLayer(mid);
            bg.AddLayer(near);

            bg.UpdateScroll();
            AssertNear(0f, sky.Position.X, 0.01f, "Sky(ms=0) must stay at 0.");
            AssertNear(-500f, mid.Position.X, 0.01f, "Mid(ms=0.5) = -1000*0.5=-500.");
            AssertNear(-1000f, near.Position.X, 0.01f, "Near(ms=1) = -1000*1=-1000.");
        }

        // ── IgnoreCameraZoom 字段存在（行为暂不阻断） ──
        public static void TestParallaxIgnoreCameraZoomFlag()
        {
            var bg = new ParallaxBackground("BG");
            AssertTrue(!bg.IgnoreCameraZoom, "IgnoreCameraZoom default must be false.");
            bg.IgnoreCameraZoom = true;
            AssertTrue(bg.IgnoreCameraZoom, "IgnoreCameraZoom must be settable.");
        }

        private static void AssertNear(float got, float want, float tol, string msg)
        {
            if (MathF.Abs(got - want) > tol)
                throw new Exception($"{msg} (got {got:F4}, want {want:F4}).");
        }

    }
}
