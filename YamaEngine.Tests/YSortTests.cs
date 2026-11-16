// YSortTests.cs —— 追平 Godot 3 的 YSort 语义（同 Z-index 内按全局 Y 升序深度排序）
// 覆盖：YSort 开启/关闭差异、全局 Y 升序、父子 Y 累积、Z-index 优先于 Y、稳定性回退到索引。

using System;
using System.Collections.Generic;
using System.Numerics;
using YamaEngine.UI;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests
{
    public static class YSortTests
    {
        // ── YSort 开启：同 Z 内 Y 升序（Y 小先画，Y 大后画遮挡） ──
        public static void TestYSortSortsByGlobalY()
        {
            var tree = new SceneTree2D { YSortEnabled = true };
            var root = new Node2D("Root");
            var far  = new Node2D("Far")  { Position = new Vector2(0, 10) };
            var near = new Node2D("Near") { Position = new Vector2(0, 100) };
            var mid  = new Node2D("Mid")  { Position = new Vector2(0, 50) };
            root.AddChild(near);
            root.AddChild(far);
            root.AddChild(mid);
            tree.AddRoot(root);

            var order = tree.GetRenderOrder();
            AssertEqual("Root", order[0].Name, "Root(Y=0) smallest Y, drawn first.");
            AssertEqual("Far",  order[1].Name, "Far(Y=10) next.");
            AssertEqual("Mid",  order[2].Name, "Mid(Y=50) next.");
            AssertEqual("Near", order[3].Name, "Near(Y=100) largest Y, drawn last (front).");
        }

        // ── YSort 关闭：同 Z 保持树先序（不受 Y 影响） ──
        public static void TestYSortDisabledKeepsTreeOrder()
        {
            var tree = new SceneTree2D { YSortEnabled = false };
            var root = new Node2D("Root");
            var a = new Node2D("A") { Position = new Vector2(0, 100) };  // Y 大但先加
            var b = new Node2D("B") { Position = new Vector2(0, 10) };   // Y 小但后加
            root.AddChild(a);
            root.AddChild(b);
            tree.AddRoot(root);

            var order = tree.GetRenderOrder();
            AssertEqual("Root", order[0].Name, "Root first (pre-order).");
            AssertEqual("A", order[1].Name, "YSort off: A added first, keeps tree order despite larger Y.");
            AssertEqual("B", order[2].Name, "YSort off: B added second.");
        }

        // ── 全局 Y：父平移影响子 YSort 顺序 ──
        public static void TestYSortUsesGlobalYFromParent()
        {
            var tree = new SceneTree2D { YSortEnabled = true };
            var root = new Node2D("Root");
            // 两个子树，父 Y 决定子树整体前后
            var highParent = new Node2D("HighParent") { Position = new Vector2(0, 0) };
            var lowParent  = new Node2D("LowParent")  { Position = new Vector2(0, 500) };
            var highChild = new Node2D("HighChild") { Position = new Vector2(0, 10) };  // 全局 Y=10
            var lowChild  = new Node2D("LowChild")  { Position = new Vector2(0, 10) };  // 全局 Y=510
            highParent.AddChild(highChild);
            lowParent.AddChild(lowChild);
            root.AddChild(lowParent);
            root.AddChild(highParent);
            tree.AddRoot(root);

            var order = tree.GetRenderOrder();
            // 按全局 Y 升序：Root(0,idx0), HighParent(0,idx3), HighChild(10), LowParent(500), LowChild(510)
            AssertEqual(5, order.Count, "All 5 visible nodes collected.");
            AssertEqual("Root", order[0].Name, "Root globalY=0, idx0 wins tie over HighParent.");
            AssertEqual("HighParent", order[1].Name, "HighParent globalY=0 (added later, loses tie).");
            AssertEqual("HighChild", order[2].Name, "HighChild globalY=10.");
            AssertEqual("LowParent", order[3].Name, "LowParent globalY=500.");
            AssertEqual("LowChild", order[4].Name, "LowChild globalY=510 last.");
        }

        // ── Z-index 优先于 YSort：不同 Z 时 Y 不参与 ──
        public static void TestYSortRespectsZIndexFirst()
        {
            var tree = new SceneTree2D { YSortEnabled = true };
            var root = new Node2D("Root");
            var lowZHighY = new Node2D("LowZHighY") { ZIndex = 0, Position = new Vector2(0, 1000) };
            var highZLowY = new Node2D("HighZLowY") { ZIndex = 5, Position = new Vector2(0, 0) };
            root.AddChild(highZLowY);
            root.AddChild(lowZHighY);
            tree.AddRoot(root);

            var order = tree.GetRenderOrder();
            AssertEqual("Root", order[0].Name, "Root first.");
            AssertEqual("LowZHighY", order[1].Name, "Z=0 drawn before Z=5 regardless of Y.");
            AssertEqual("HighZLowY", order[2].Name, "Z=5 drawn last.");
        }

        // ── YSort 稳定性：相同 Y 回退到树先序 ──
        public static void TestYSortStableForEqualY()
        {
            var tree = new SceneTree2D { YSortEnabled = true };
            var root = new Node2D("Root");
            var first  = new Node2D("First")  { Position = new Vector2(0, 50) };
            var second = new Node2D("Second") { Position = new Vector2(0, 50) };
            root.AddChild(first);
            root.AddChild(second);
            tree.AddRoot(root);

            var order = tree.GetRenderOrder();
            AssertEqual("Root", order[0].Name, "Root first.");
            AssertEqual("First", order[1].Name, "Equal Y: first added keeps earlier index.");
            AssertEqual("Second", order[2].Name, "Equal Y: second added keeps later index.");
        }

        private static void AssertEqual<T>(T got, T want, string msg)
        {
            if (!Equals(got, want)) throw new Exception($"{msg} (got {got}, want {want}).");
        }
    }
}
