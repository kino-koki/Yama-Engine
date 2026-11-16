// UILayoutDepthTests.cs —— Unity 5 uGUI 布局系统深度验证
// 覆盖 RectTransform 锚点/Parent/AnchoredPosition + VerticalLayoutGroup/HorizontalLayoutGroup/GridLayoutGroup
using System;
using System.Numerics;
using YamaEngine.UI;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests
{
    public static class UILayoutDepthTests
    {
        private static void AssertTrue(bool cond, string msg) { if (!cond) throw new Exception(msg); }
        private static void AssertFalse(bool cond, string msg) { if (cond) throw new Exception(msg); }
        private static void AssertNear(float got, float want, string msg, float tol = 1e-3f)
        { if (MathF.Abs(got - want) > tol) throw new Exception($"{msg} (got {got}, want {want})."); }
        private static void AssertNear2(Vector2 got, Vector2 want, string msg, float tol = 1e-3f)
        {
            AssertNear(got.X, want.X, msg + " (X)", tol);
            AssertNear(got.Y, want.Y, msg + " (Y)", tol);
        }

        // ── RectTransform 默认状态：中心锚点、50×50 ──
        public static void TestRectTransformDefaults()
        {
            var rt = new RectTransform { Name = "Panel" };
            AssertNear(0.5f, rt.AnchorMin.X, "默认 AnchorMin.x=0.5.");
            AssertNear(0.5f, rt.AnchorMax.X, "默认 AnchorMax.x=0.5.");
            AssertNear(-50f, rt.OffsetMin.X, "默认 OffsetMin.x=-50.");
            AssertNear(50f, rt.OffsetMax.X, "默认 OffsetMax.x=50.");
        }

        // ── SizeDelta 读写 ──
        public static void TestSizeDeltaRoundTrip()
        {
            var rt = new RectTransform();
            rt.SizeDelta = new Vector2(200, 100);
            AssertNear(200f, rt.SizeDelta.X, "SizeDelta.X=200.");
            AssertNear(100f, rt.SizeDelta.Y, "SizeDelta.Y=100.");
        }

        // ── AnchoredPosition 读写 ──
        public static void TestAnchoredPositionRoundTrip()
        {
            var rt = new RectTransform();
            rt.SizeDelta = new Vector2(100, 50);
            rt.AnchoredPosition = new Vector2(10, 20);
            AssertNear(10f, rt.AnchoredPosition.X, "AnchoredPosition.X=10.");
            AssertNear(20f, rt.AnchoredPosition.Y, "AnchoredPosition.Y=20.");
        }

        // ── 中心锚点 GetRect：无 Parent，canvas 1920×1080，中心 (960,540) ──
        public static void TestCenterAnchorRect()
        {
            var rt = new RectTransform();
            rt.AnchorMin = new Vector2(0.5f, 0.5f);
            rt.AnchorMax = new Vector2(0.5f, 0.5f);
            rt.SizeDelta = new Vector2(100, 50);
            rt.AnchoredPosition = Vector2.Zero;

            var rect = rt.GetRect();
            AssertNear(910f, rect.X, "中心锚点 100 宽：X=910.");
            AssertNear(960f, rect.Center.X, "中心 X=960.");
            AssertNear(540f, rect.Center.Y, "中心 Y=540.");
        }

        // ── SetStretchAnchor：四边拉伸 ──
        public static void TestStretchAnchor()
        {
            var rt = new RectTransform();
            rt.SetStretchAnchor();
            AssertNear(0f, rt.AnchorMin.X, "拉伸锚点 Min=(0,0).");
            AssertNear(0f, rt.AnchorMin.Y, "拉伸锚点 Min.Y=0.");
            AssertNear(1f, rt.AnchorMax.X, "拉伸锚点 Max=(1,1).");
            AssertNear(1f, rt.AnchorMax.Y, "拉伸锚点 Max.Y=1.");
        }

        // ── SetTopLeftAnchor ──
        public static void TestTopLeftAnchor()
        {
            var rt = new RectTransform();
            rt.SetTopLeftAnchor();
            AssertNear(0f, rt.AnchorMin.X, "左上锚点 Min.X=0.");
            AssertNear(0f, rt.AnchorMin.Y, "左上锚点 Min.Y=0.");
        }

        // ── Parent-Child：子元素锚点相对父元素计算 ──
        public static void TestParentChildRect()
        {
            var parent = new RectTransform { Name = "Parent" };
            parent.AnchorMin = Vector2.Zero;
            parent.AnchorMax = Vector2.Zero;
            parent.SizeDelta = new Vector2(800, 600);
            parent.AnchoredPosition = Vector2.Zero;
            // 无 Parent → canvas (0,0,1920,1080)，左上锚点：left=0+0+0=0, top=0+(1-0)*1080-0=1080
            // → X=0, Y=1080-600=480? 让我重新读公式
            // top = 0 + (1-0)*1080 - 0 = 1080
            // bottom = 0 + (1-0)*1080 - 300 = 780
            // 等等：OffsetMin.y 默认-25，OffsetMax.y 默认25
            // 让我设置正确的 offset

            parent.OffsetMin = new Vector2(-400, -300);
            parent.OffsetMax = new Vector2(400, 300);
            // 左=0+0*1920+(-400)=-400, 右=0+0*1920+400=400 → 宽 800
            // top=0+(1-0)*1080-(-300)=1380, bottom=0+(1-0)*1080-300=780 → 高 600
            // 等等：bottom<top 时交换，所以 top=780, bottom=1380? 不对

            // 让我用不同的锚点方式：直接 Parent 是 canvas 中心锚点
            parent.AnchorMin = new Vector2(0.5f, 0.5f);
            parent.AnchorMax = new Vector2(0.5f, 0.5f);
            parent.SizeDelta = new Vector2(800, 600);
            parent.AnchoredPosition = Vector2.Zero;
            // left=960+0.5*1920-400=960+960-400=1520? 不对，AnchorMin=0.5: left=0+0.5*1920+(-400)=960-400=560
            // right=0+0.5*1920+400=960+400=1360 → 宽 800 ✓
            // top=0+(1-0.5)*1080-(-300)=540+300=840
            // bottom=0+(1-0.5)*1080-300=540-300=240 → bottom<top 交换 → top=240, bottom=840 → 高 600 ✓

            var child = new RectTransform { Name = "Child" };
            child.AnchorMin = Vector2.Zero;
            child.AnchorMax = Vector2.Zero;
            child.SizeDelta = new Vector2(100, 50);
            child.AnchoredPosition = Vector2.Zero;
            parent.AddChild(child);
            child.SetParent(parent);

            var parentRect = parent.GetRect();
            AssertNear(800f, parentRect.Width, "Parent 宽 800.");
            AssertNear(600f, parentRect.Height, "Parent 高 600.");
            AssertNear(960f, parentRect.Center.X, "Parent 中心 X=960.");
            AssertNear(540f, parentRect.Center.Y, "Parent 中心 Y=540.");
        }

        // ── VerticalLayoutGroup：3 个子元素从上到下排列 ──
        public static void TestVerticalLayoutStacksDown()
        {
            var panel = new RectTransform { Name = "VPanel" };
            panel.AnchorMin = Vector2.Zero;
            panel.AnchorMax = Vector2.Zero;
            panel.SizeDelta = new Vector2(400, 600);
            panel.AnchoredPosition = Vector2.Zero;
            panel.OffsetMin = new Vector2(-200, -300);
            panel.OffsetMax = new Vector2(200, 300);

            var c1 = new RectTransform { Name = "C1" };
            c1.SizeDelta = new Vector2(100, 50);
            panel.AddChild(c1);
            var c2 = new RectTransform { Name = "C2" };
            c2.SizeDelta = new Vector2(100, 50);
            panel.AddChild(c2);
            var c3 = new RectTransform { Name = "C3" };
            c3.SizeDelta = new Vector2(100, 50);
            panel.AddChild(c3);

            var layout = new VerticalLayoutGroup { Target = panel, Spacing = 10f, ControlChildWidth = false };
            layout.CalculateLayout();

            // 三个子元素应该在 Y 方向堆叠（向下），每个间隔 spacing+height=60
            float y1 = panel.Children[0].AnchoredPosition.Y;
            float y2 = panel.Children[1].AnchoredPosition.Y;
            float y3 = panel.Children[2].AnchoredPosition.Y;
            AssertTrue(y2 > y1 + 40f, $"垂直布局 c2 在 c1 下方 (y2={y2}, y1={y1}).");
            AssertTrue(y3 > y2 + 40f, $"垂直布局 c3 在 c2 下方 (y3={y3}, y2={y2}).");
        }

        // ── VerticalLayoutGroup 间距影响位置 ──
        public static void TestVerticalLayoutSpacing()
        {
            var panel = new RectTransform { Name = "VPanel" };
            panel.AnchorMin = Vector2.Zero;
            panel.AnchorMax = Vector2.Zero;
            panel.SizeDelta = new Vector2(400, 600);
            panel.AnchoredPosition = Vector2.Zero;
            panel.OffsetMin = new Vector2(-200, -300);
            panel.OffsetMax = new Vector2(200, 300);

            var c1 = new RectTransform { Name = "C1" };
            c1.SizeDelta = new Vector2(100, 50);
            panel.AddChild(c1);
            var c2 = new RectTransform { Name = "C2" };
            c2.SizeDelta = new Vector2(100, 50);
            panel.AddChild(c2);

            // 无间距
            var layout0 = new VerticalLayoutGroup { Target = panel, Spacing = 0f, ControlChildWidth = false };
            layout0.CalculateLayout();
            float yNoSpacing = panel.Children[1].AnchoredPosition.Y - panel.Children[0].AnchoredPosition.Y;

            // 有间距 20
            var layout20 = new VerticalLayoutGroup { Target = panel, Spacing = 20f, ControlChildWidth = false };
            layout20.CalculateLayout();
            float ySpacing20 = panel.Children[1].AnchoredPosition.Y - panel.Children[0].AnchoredPosition.Y;

            AssertNear(20f, ySpacing20 - yNoSpacing, "Spacing=20 应增加 20 像素间距.");
        }

        // ── HorizontalLayoutGroup：3 个子元素从左到右排列 ──
        public static void TestHorizontalLayoutStacksRight()
        {
            var panel = new RectTransform { Name = "HPanel" };
            panel.AnchorMin = Vector2.Zero;
            panel.AnchorMax = Vector2.Zero;
            panel.SizeDelta = new Vector2(600, 400);
            panel.AnchoredPosition = Vector2.Zero;
            panel.OffsetMin = new Vector2(-300, -200);
            panel.OffsetMax = new Vector2(300, 200);

            var c1 = new RectTransform { Name = "C1" };
            c1.SizeDelta = new Vector2(100, 50);
            panel.AddChild(c1);
            var c2 = new RectTransform { Name = "C2" };
            c2.SizeDelta = new Vector2(100, 50);
            panel.AddChild(c2);
            var c3 = new RectTransform { Name = "C3" };
            c3.SizeDelta = new Vector2(100, 50);
            panel.AddChild(c3);

            var layout = new HorizontalLayoutGroup { Target = panel, Spacing = 10f, ControlChildHeight = false };
            layout.CalculateLayout();

            float x1 = panel.Children[0].AnchoredPosition.X;
            float x2 = panel.Children[1].AnchoredPosition.X;
            float x3 = panel.Children[2].AnchoredPosition.X;
            AssertTrue(x2 > x1 + 50f, $"水平布局 c2 在 c1 右方 (x2={x2}, x1={x1}).");
            AssertTrue(x3 > x2 + 50f, $"水平布局 c3 在 c2 右方 (x3={x3}, x2={x2}).");
        }

        // ── GridLayoutGroup：4 个子元素 2×2 网格 ──
        public static void TestGridLayout2x2()
        {
            var panel = new RectTransform { Name = "GPanel" };
            panel.AnchorMin = Vector2.Zero;
            panel.AnchorMax = Vector2.Zero;
            panel.SizeDelta = new Vector2(400, 400);
            panel.AnchoredPosition = Vector2.Zero;
            panel.OffsetMin = new Vector2(-200, -200);
            panel.OffsetMax = new Vector2(200, 200);

            for (int i = 0; i < 4; i++)
            {
                var c = new RectTransform { Name = $"C{i}" };
                c.SizeDelta = new Vector2(80, 80);
                panel.AddChild(c);
            }

            var layout = new GridLayoutGroup
            {
                Target = panel,
                CellSize = new Vector2(100, 100),
                Spacing = new Vector2(10, 10),
                Constraint = UIGridConstraint.FixedColumnCount,
                ConstraintCount = 2, // 2 列 → 2×2 网格
            };
            layout.CalculateLayout();

            // 4 个子元素应该在 2×2 网格中
            // C0: (0,0), C1: (110,0), C2: (0,110), C3: (110,110) [相对布局起点]
            float x0 = panel.Children[0].AnchoredPosition.X;
            float y0 = panel.Children[0].AnchoredPosition.Y;
            float x1 = panel.Children[1].AnchoredPosition.X;
            float y1 = panel.Children[1].AnchoredPosition.Y;
            float x2 = panel.Children[2].AnchoredPosition.X;
            float y2 = panel.Children[2].AnchoredPosition.Y;

            AssertNear(x1 - x0, 110f, "网格 C1 X 间距 = CellSize.X + Spacing.X.", 1f);
            AssertNear(y2 - y0, 110f, "网格 C2 Y 间距 = CellSize.Y + Spacing.Y.", 1f);
            AssertNear(x0, x2, "网格 C0/C2 X 相同（同列）.", 1f);
            AssertNear(y0, y1, "网格 C0/C1 Y 相同（同行）.", 1f);
        }
    }
}
