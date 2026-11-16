// CanvasLayerTests.cs —— 追平 Godot 3 CanvasLayer 语义
// 覆盖：层排序、整层可见性、Offset/Rotation/Scale 变换、follow_viewport、渲染顺序。

using System;
using System.Collections.Generic;
using System.Numerics;
using YamaEngine.UI;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests
{
    public static class CanvasLayerTests
    {
        // ── Layer 排序：值小先渲染（底层） ──
        public static void TestCanvasLayerSortsByLayerAscending()
        {
            var stack = new CanvasLayerStack();
            var bg  = new CanvasLayerReal("Background", layer: -1);
            var hud = new CanvasLayerReal("HUD",       layer:  1);
            var main= new CanvasLayerReal("Main",      layer:  0);

            // 乱序注册
            stack.AddLayer(hud);
            stack.AddLayer(bg);
            stack.AddLayer(main);
            stack.SortLayers();

            AssertEqual("Background", stack.Sorted[0].Name, "Layer -1 must render first (bottom).");
            AssertEqual("Main",       stack.Sorted[1].Name, "Layer  0 must be middle.");
            AssertEqual("HUD",        stack.Sorted[2].Name, "Layer  1 must render last (top).");
            AssertEqual(3, stack.Count, "Stack must hold 3 layers.");
        }

        // ── 整层可见性：Visible=false 的层不出现在渲染顺序 ──
        public static void TestCanvasLayerHiddenLayerSkippedInRenderOrder()
        {
            var stack = new CanvasLayerStack();
            var bg  = new CanvasLayerReal("Background", layer: 0);
            var hud = new CanvasLayerReal("HUD",        layer: 1);
            stack.AddLayer(bg);
            stack.AddLayer(hud);

            hud.Visible = false;
            var order = stack.GetRenderOrder();

            AssertEqual(1, order.Count, "Hidden layer must be excluded from render order.");
            AssertEqual("Background", order[0], "Only visible layer must appear.");
        }

        // ── GetVisibleLayers 只返回 Visible=true ──
        public static void TestCanvasLayerGetVisibleLayers()
        {
            var stack = new CanvasLayerStack();
            var a = new CanvasLayerReal("A", layer: 0);
            var b = new CanvasLayerReal("B", layer: 1);
            var c = new CanvasLayerReal("C", layer: 2);
            stack.AddLayer(a);
            stack.AddLayer(b);
            stack.AddLayer(c);

            b.Visible = false;
            var visible = stack.GetVisibleLayers();

            AssertEqual(2, visible.Count, "2 of 3 layers must be visible.");
            AssertEqual("A", visible[0].Name, "Layer A must be visible.");
            AssertEqual("C", visible[1].Name, "Layer C must be visible.");
        }

        // ── Offset 变换：LocalToWorld 应用平移 ──
        public static void TestCanvasLayerOffsetTranslation()
        {
            var layer = new CanvasLayerReal("OffsetLayer");
            layer.Offset = new Vector2(100, 50);

            var world = layer.LocalToWorld(Vector2.Zero);
            AssertNear(100f, world.X, 0.01f, "Offset.X=100 must shift local (0,0) to world (100,50).");
            AssertNear( 50f, world.Y, 0.01f, "Offset.Y=50 must shift local (0,0) to world (100,50).");
        }

        // ── Scale 变换：放大 2 倍 ──
        public static void TestCanvasLayerScaleTransform()
        {
            var layer = new CanvasLayerReal("ScaleLayer");
            layer.Scale = new Vector2(2f, 3f);
            layer.Offset = Vector2.Zero;

            var world = layer.LocalToWorld(new Vector2(10, 10));
            AssertNear(20f, world.X, 0.01f, "Scale.X=2 with local (10,10) must produce world.X=20.");
            AssertNear(30f, world.Y, 0.01f, "Scale.Y=3 with local (10,10) must produce world.Y=30.");
        }

        // ── Rotation 90° 变换：局部 (1,0) → 世界 (0,1) ──
        public static void TestCanvasLayerRotation90()
        {
            var layer = new CanvasLayerReal("RotLayer");
            layer.Offset = Vector2.Zero;
            layer.Scale = Vector2.One;
            layer.Rotation = MathF.PI / 2f;  // 90° 绕 Z 轴

            // 局部 (1, 0) 绕 Z 轴转 90° → (cos90, sin90) = (0, 1)
            var world = layer.LocalToWorld(new Vector2(1, 0));
            AssertNear(0f, world.X, 0.01f, "Rotation 90° must map local (1,0).X to ≈0.");
            AssertNear(1f, world.Y, 0.01f, "Rotation 90° must map local (1,0).Y to ≈1.");
        }

        // ── follow_viewport：视口相机平移叠加到层变换 ──
        public static void TestCanvasLayerFollowViewport()
        {
            var vp = new ViewportReal("Main", new Vector2(1920, 1080))
            {
                CameraPosition = new Vector2(500, 300),
                CameraZoom = 1f,
            };

            // 不跟随的层：无视口变换，只应用 Offset
            var staticLayer = new CanvasLayerReal("Static") { Offset = new Vector2(10, 20) };
            var staticWorld = staticLayer.LocalToWorld(Vector2.Zero);
            AssertNear(10f, staticWorld.X, 0.01f, "Non-follow layer: Offset only.");
            AssertNear(20f, staticWorld.Y, 0.01f, "Non-follow layer: Offset only.");

            // 跟随视口的层：叠加相机平移
            var parallaxLayer = new CanvasLayerReal("Parallax")
            {
                Offset = Vector2.Zero,
                FollowViewportEnabled = true,
            };
            // LocalToWorld(vp) 将视口变换叠加：translate(-CameraPos) → local (0,0) 变为 (-500, -300)
            var parallaxWorld = parallaxLayer.LocalToWorld(Vector2.Zero, vp);
            AssertNear(-500f, parallaxWorld.X, 0.01f,
                "FollowViewport layer must include camera position translation.");
            AssertNear(-300f, parallaxWorld.Y, 0.01f,
                "FollowViewport layer must include camera position translation.");
        }

        // ── follow_viewport + zoom：放大视口叠加 ──
        public static void TestCanvasLayerFollowViewportZoom()
        {
            var vp = new ViewportReal("Zoom", new Vector2(1920, 1080))
            {
                CameraPosition = new Vector2(100, 0),
                CameraZoom = 2f,
            };

            var layer = new CanvasLayerReal("ZoomLayer")
            {
                Offset = Vector2.Zero,
                FollowViewportEnabled = true,
            };
            // LocalToWorld(vp)：先 translate(-100,0)，再 scale(2)。
            // local (0,0) → translate → (-100,0) → scale → (-200, 0)
            var world = layer.LocalToWorld(Vector2.Zero, vp);
            AssertNear(-200f, world.X, 0.01f, "Zoom=2 must scale translated (-100,0) to (-200,0).");
        }

        // ── 子节点管理：AddChild/RemoveChild/ChildCount ──
        public static void TestCanvasLayerAddRemoveChild()
        {
            var layer = new CanvasLayerReal("HUD");
            var child1 = new RectTransform { Name = "Panel1" };
            var child2 = new RectTransform { Name = "Panel2" };

            AssertEqual(0, layer.ChildCount, "Empty layer has 0 children.");
            layer.AddChild(child1);
            AssertEqual(1, layer.ChildCount, "After AddChild: 1 child.");
            layer.AddChild(child2);
            AssertEqual(2, layer.ChildCount, "After 2 AddChild: 2 children.");

            layer.RemoveChild(child1);
            AssertEqual(1, layer.ChildCount, "After RemoveChild: 1 child.");
        }

        // ── 按值/名查找层 ──
        public static void TestCanvasLayerStackLookup()
        {
            var stack = new CanvasLayerStack();
            stack.AddLayer(new CanvasLayerReal("Main", 0));
            stack.AddLayer(new CanvasLayerReal("HUD",  1));
            stack.AddLayer(new CanvasLayerReal("Debug",2));

            var byVal = stack.GetLayerByValue(1);
            AssertTrue(byVal != null && byVal.Name == "HUD", "GetLayerByValue(1) must return HUD.");

            var byName = stack.GetLayerByName("Debug");
            AssertTrue(byName != null && byName.Layer == 2, "GetLayerByName(Debug) must return layer=2.");

            AssertTrue(stack.GetLayerByValue(99) == null, "Missing layer must return null.");
        }

        // ── RemoveLayer ──
        public static void TestCanvasLayerStackRemove()
        {
            var stack = new CanvasLayerStack();
            var layer = new CanvasLayerReal("Temp", 0);
            stack.AddLayer(layer);
            AssertEqual(1, stack.Count, "1 layer before Remove.");

            AssertTrue(stack.RemoveLayer(layer), "RemoveLayer must return true for present layer.");
            AssertEqual(0, stack.Count, "0 layers after Remove.");
            AssertTrue(!stack.RemoveLayer(layer), "RemoveLayer on absent must return false.");
        }

        private static void AssertNear(float got, float want, float tol, string msg)
        {
            if (MathF.Abs(got - want) > tol)
                throw new Exception($"{msg} (got {got:F4}, want {want:F4}).");
        }
    }
}
