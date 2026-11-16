// CanvasItemClipTests.cs —— 追平 Godot 3 的 CanvasItem 裁剪（clip_contents + VisibilityRect）

using System;
using System.Numerics;
using YamaEngine.UI;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests
{
    public static class CanvasItemClipTests
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

        private static void AssertFalse(bool cond, string msg)
        {
            if (cond) throw new Exception(msg);
        }

        private static void AssertRect(Rect2 got, float x, float y, float w, float h, string msg)
        {
            AssertNear(got.X, x, msg + " X.", 0.01f);
            AssertNear(got.Y, y, msg + " Y.", 0.01f);
            AssertNear(got.Width, w, msg + " W.", 0.01f);
            AssertNear(got.Height, h, msg + " H.", 0.01f);
        }

        // ── 默认 ClipMode 是 Disabled ──
        public static void TestDefaultClipModeDisabled()
        {
            var node = new Node2D("N");
            AssertEqual(ClipMode.Disabled, node.GetClipMode(), "Default clip mode.");
            AssertTrue(node.GetGlobalClipRect() == null, "No clip rect when Disabled.");
        }

        // ── SetClipMode 存取 ──
        public static void TestSetClipMode()
        {
            var node = new Node2D("N");
            node.SetClipMode(ClipMode.Only);
            AssertEqual(ClipMode.Only, node.GetClipMode(), "Clip mode Only.");
            node.SetClipMode(ClipMode.Disabled);
            AssertEqual(ClipMode.Disabled, node.GetClipMode(), "Back to Disabled.");
        }

        // ── ClipRect 局部存取 ──
        public static void TestClipRectLocal()
        {
            var node = new Node2D("N");
            node.SetClipRect(new ClipRect(10f, 20f, 100f, 50f));
            var r = node.GetClipRect();
            AssertTrue(r != null, "Clip rect set.");
            AssertNear(10f, r!.Position.X, "Clip X.");
            AssertNear(20f, r.Position.Y, "Clip Y.");
            AssertNear(100f, r.Size.X, "Clip W.");
            AssertNear(50f, r.Size.Y, "Clip H.");
            AssertNear(110f, r.End.X, "Clip End X.");
        }

        // ── 全局裁剪矩形：无变换时 = 局部矩形 ──
        public static void TestGlobalClipRectNoTransform()
        {
            var node = new Node2D("N");
            node.SetClipMode(ClipMode.Only);
            node.SetClipRect(new ClipRect(0f, 0f, 100f, 50f));

            var global = node.GetGlobalClipRect();
            AssertTrue(global != null, "Global clip rect exists.");
            AssertRect(global!.Value, 0f, 0f, 100f, 50f, "Global=local no transform.");
        }

        // ── 全局裁剪矩形：平移父节点，子裁剪矩形跟着走 ──
        public static void TestGlobalClipRectWithTranslation()
        {
            var parent = new Node2D("Parent") { Position = new Vector2(100f, 50f) };
            var child = new Node2D("Child");
            parent.AddChild(child);

            child.SetClipMode(ClipMode.AndDraw);
            child.SetClipRect(new ClipRect(0f, 0f, 80f, 40f));

            var global = child.GetGlobalClipRect();
            AssertTrue(global != null, "Global clip rect exists.");
            AssertRect(global!.Value, 100f, 50f, 80f, 40f, "Clip translated by parent.");
        }

        // ── 全局裁剪矩形：缩放变换 ──
        public static void TestGlobalClipRectWithScale()
        {
            var node = new Node2D("N") { Scale = new Vector2(2f, 2f) };
            node.SetClipMode(ClipMode.Only);
            node.SetClipRect(new ClipRect(0f, 0f, 50f, 25f));

            var global = node.GetGlobalClipRect();
            AssertRect(global!.Value, 0f, 0f, 100f, 50f, "Clip scaled by 2x.");
        }

        // ── 未显式设 ClipRect 时用可见矩形推断 ──
        public static void TestClipRectFallbackToVisibilityRect()
        {
            var node = new Node2D("N");
            node.SetClipMode(ClipMode.Only);
            node.SetVisibilityRect(new VisibilityRect(new Vector2(5f, 5f), new Vector2(30f, 20f)));

            var global = node.GetGlobalClipRect();
            AssertRect(global!.Value, 5f, 5f, 30f, 20f, "Clip fallback to visibility rect.");
        }

        // ── 未设 ClipRect 也未设 VisibilityRect 时用默认 (0,0,32,32) ──
        public static void TestClipRectDefaultFallback()
        {
            var node = new Node2D("N");
            node.SetClipMode(ClipMode.Only);
            var global = node.GetGlobalClipRect();
            AssertRect(global!.Value, 0f, 0f, 32f, 32f, "Default 32x32 clip rect.");
        }

        // ── IsClippedByAncestors：子节点在裁剪框内 → 不剔除 ──
        public static void TestNotClippedWhenInside()
        {
            var parent = new Node2D("Parent");
            var child = new Node2D("Child") { Position = new Vector2(10f, 10f) };
            parent.AddChild(child);

            parent.SetClipMode(ClipMode.Only);
            parent.SetClipRect(new ClipRect(0f, 0f, 100f, 100f));
            child.SetVisibilityRect(new VisibilityRect(new Vector2(0f, 0f), new Vector2(20f, 20f)));

            AssertFalse(child.IsClippedByAncestors(), "Child inside clip rect → not clipped.");
        }

        // ── IsClippedByAncestors：子节点完全在裁剪框外 → 剔除 ──
        public static void TestClippedWhenOutside()
        {
            var parent = new Node2D("Parent");
            var child = new Node2D("Child") { Position = new Vector2(200f, 200f) };
            parent.AddChild(child);

            parent.SetClipMode(ClipMode.Only);
            parent.SetClipRect(new ClipRect(0f, 0f, 100f, 100f));
            child.SetVisibilityRect(new VisibilityRect(new Vector2(0f, 0f), new Vector2(20f, 20f)));

            AssertTrue(child.IsClippedByAncestors(), "Child outside clip rect → clipped.");
        }

        // ── 多层裁剪：两层祖先都裁，取交集判定 ──
        public static void TestMultipleAncestorClipping()
        {
            var grand = new Node2D("Grand");
            var parent = new Node2D("Parent");
            var child = new Node2D("Child") { Position = new Vector2(10f, 10f) };
            grand.AddChild(parent);
            parent.AddChild(child);

            grand.SetClipMode(ClipMode.Only);
            grand.SetClipRect(new ClipRect(0f, 0f, 100f, 100f));
            parent.SetClipMode(ClipMode.Only);
            parent.SetClipRect(new ClipRect(5f, 5f, 50f, 50f)); // parent 相对 grand (5,5,50,50)
            child.SetVisibilityRect(new VisibilityRect(0f, 0f, 10f, 10f));

            // child global vis = (10,10,10,10)，grand clip=(0,0,100,100) 相交，parent clip global=(5,5,50,50) 相交
            AssertFalse(child.IsClippedByAncestors(), "Child inside both ancestor clip rects.");
        }

        // ── 多层裁剪：一个祖先裁掉就剔除 ──
        public static void TestOneAncestorClipsEnough()
        {
            var grand = new Node2D("Grand");
            var parent = new Node2D("Parent") { Position = new Vector2(500f, 500f) };
            var child = new Node2D("Child");
            grand.AddChild(parent);
            parent.AddChild(child);

            grand.SetClipMode(ClipMode.Only);
            grand.SetClipRect(new ClipRect(0f, 0f, 100f, 100f));
            child.SetVisibilityRect(new VisibilityRect(0f, 0f, 20f, 20f));

            // child global vis ≈ (500,500,20,20)，grand clip=(0,0,100,100) 不相交
            AssertTrue(child.IsClippedByAncestors(), "Grand clips child even though parent doesn't clip.");
        }

        // ── GetEffectiveVisibleRect：无裁剪 → 可见矩形原样 ──
        public static void TestEffectiveRectNoClip()
        {
            var node = new Node2D("N");
            node.SetVisibilityRect(new VisibilityRect(0f, 0f, 50f, 30f));
            var eff = node.GetEffectiveVisibleRect();
            AssertTrue(eff != null, "Effective rect exists.");
            AssertRect(eff!.Value, 0f, 0f, 50f, 30f, "No clip → vis rect unchanged.");
        }

        // ── GetEffectiveVisibleRect：父裁剪部分相交 → 取交集 ──
        public static void TestEffectiveRectPartialIntersect()
        {
            var parent = new Node2D("Parent");
            var child = new Node2D("Child") { Position = new Vector2(10f, 0f) };
            parent.AddChild(child);

            parent.SetClipMode(ClipMode.Only);
            parent.SetClipRect(new ClipRect(0f, 0f, 50f, 100f)); // global (0,0,50,100)
            child.SetVisibilityRect(new VisibilityRect(0f, 0f, 50f, 30f)); // global (10,0,50,30)

            var eff = child.GetEffectiveVisibleRect();
            AssertTrue(eff != null, "Partial intersect → non-null.");
            AssertRect(eff!.Value, 10f, 0f, 40f, 30f, "Intersect (10,0,50,30)∩(0,0,50,100) = (10,0,40,30).");
        }

        // ── GetEffectiveVisibleRect：完全不相交 → null ──
        public static void TestEffectiveRectNoIntersect()
        {
            var parent = new Node2D("Parent");
            var child = new Node2D("Child") { Position = new Vector2(200f, 0f) };
            parent.AddChild(child);

            parent.SetClipMode(ClipMode.Only);
            parent.SetClipRect(new ClipRect(0f, 0f, 100f, 100f));
            child.SetVisibilityRect(new VisibilityRect(0f, 0f, 20f, 20f));

            var eff = child.GetEffectiveVisibleRect();
            AssertTrue(eff == null, "No intersect → null effective rect.");
        }

        // ── RectsIntersect 单元测试 ──
        public static void TestRectsIntersectHelper()
        {
            var a = new Rect2(0f, 0f, 10f, 10f);
            var b = new Rect2(5f, 5f, 10f, 10f);
            var c = new Rect2(20f, 20f, 5f, 5f);
            AssertTrue(CanvasItemClipExtensions.RectsIntersect(a, b), "a intersects b.");
            AssertFalse(CanvasItemClipExtensions.RectsIntersect(a, c), "a does not intersect c.");
        }

        // ── ClearClipData 清理所有裁剪状态 ──
        public static void TestClearClipData()
        {
            var node = new Node2D("N");
            node.SetClipMode(ClipMode.Only);
            node.SetClipRect(new ClipRect(0f, 0f, 100f, 100f));
            node.SetVisibilityRect(new VisibilityRect(0f, 0f, 50f, 50f));
            node.ClearClipData();
            AssertEqual(ClipMode.Disabled, node.GetClipMode(), "Mode cleared.");
            AssertTrue(node.GetClipRect() == null, "Clip rect cleared.");
            AssertTrue(node.GetVisibilityRect() == null, "Visibility rect cleared.");
        }

        // ── ClipMode.AndDraw 与 Only 行为一致（裁剪判定不区分模式，只区分 Disabled/非 Disabled） ──
        public static void TestClipModeAndDrawStillClips()
        {
            var parent = new Node2D("Parent");
            var child = new Node2D("Child") { Position = new Vector2(200f, 0f) };
            parent.AddChild(child);
            parent.SetClipMode(ClipMode.AndDraw);
            parent.SetClipRect(new ClipRect(0f, 0f, 100f, 100f));
            child.SetVisibilityRect(new VisibilityRect(0f, 0f, 20f, 20f));
            AssertTrue(child.IsClippedByAncestors(), "AndDraw still clips children.");
        }

        // ── VisibilityRect 局部存取 + 默认值 ──
        public static void TestVisibilityRectLocalAccess()
        {
            var node = new Node2D("N");
            AssertTrue(node.GetVisibilityRect() == null, "No vis rect by default.");
            node.SetVisibilityRect(new VisibilityRect(new Vector2(5f, 10f), new Vector2(30f, 40f)));
            var vis = node.GetVisibilityRect();
            AssertNear(5f, vis!.Position.X, "Vis X.");
            AssertNear(10f, vis.Position.Y, "Vis Y.");
            AssertNear(30f, vis.Size.X, "Vis W.");
            AssertNear(40f, vis.Size.Y, "Vis H.");
        }
    }
}
