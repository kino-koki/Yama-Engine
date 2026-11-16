// Node2DTests.cs —— 追平 Godot 3 Node2D/CanvasItem 语义
// 覆盖：父子层级、全局变换累积、全局/本地互转、可见性传播、Z-index 渲染顺序、modulate 累积。

using System;
using System.Collections.Generic;
using System.Numerics;
using YamaEngine.UI;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests
{
    public static class Node2DTests
    {
        // ── 父子层级：AddChild 建立 Parent/Children ──
        public static void TestNode2DAddChildEstablishesHierarchy()
        {
            var parent = new Node2D("Parent");
            var child = new Node2D("Child");
            parent.AddChild(child);

            AssertTrue(child.Parent == parent, "Child.Parent must be set after AddChild.");
            AssertEqual(1, parent.ChildCount, "Parent must have 1 child.");
            AssertTrue(parent.Children.Contains(child), "Parent.Children must contain child.");
        }

        // ── SetParent 自动解除旧父 ──
        public static void TestNode2DSetParentReparents()
        {
            var a = new Node2D("A");
            var b = new Node2D("B");
            var c = new Node2D("C");

            a.AddChild(c);
            c.SetParent(b);  // 从 A 移到 B

            AssertTrue(c.Parent == b, "After SetParent(B), C.Parent must be B.");
            AssertEqual(0, a.ChildCount, "A must lose child.");
            AssertEqual(1, b.ChildCount, "B must gain child.");
        }

        // ── 全局变换：父链累积（父平移 + 子平移） ──
        public static void TestNode2DGlobalTransformAccumulatesParent()
        {
            var parent = new Node2D("Parent") { Position = new Vector2(100, 0) };
            var child = new Node2D("Child") { Position = new Vector2(10, 0) };
            parent.AddChild(child);

            var gp = child.GlobalPosition;
            AssertNear(110f, gp.X, 0.01f, "Child global X = parent(100) + child(10) = 110.");
            AssertNear(  0f, gp.Y, 0.01f, "Child global Y must be 0.");
        }

        // ── 全局变换：父旋转影响子世界位置 ──
        public static void TestNode2DParentRotationAffectsChild()
        {
            var parent = new Node2D("Parent") { Rotation = MathF.PI / 2f };  // 90°
            var child = new Node2D("Child") { Position = new Vector2(10, 0) };
            parent.AddChild(child);

            // 子本地 (10,0) 绕父旋转 90° → (0, 10)
            var gp = child.GlobalPosition;
            AssertNear(0f,  gp.X, 0.01f, "Parent rot90 must map child local (10,0).X to ≈0.");
            AssertNear(10f, gp.Y, 0.01f, "Parent rot90 must map child local (10,0).Y to ≈10.");
        }

        // ── 全局位置 setter：反解本地位置 ──
        public static void TestNode2DSetGlobalPositionInverts()
        {
            var parent = new Node2D("Parent") { Position = new Vector2(50, 50) };
            var child = new Node2D("Child");
            parent.AddChild(child);

            child.GlobalPosition = new Vector2(120, 80);
            AssertNear(70f, child.Position.X, 0.01f, "Setting global(120) under parent(50) must set local.X=70.");
            AssertNear(30f, child.Position.Y, 0.01f, "Setting global(80) under parent(50) must set local.Y=30.");

            // 往返校验
            AssertNear(120f, child.GlobalPosition.X, 0.01f, "GlobalPosition.X round-trip.");
            AssertNear( 80f, child.GlobalPosition.Y, 0.01f, "GlobalPosition.Y round-trip.");
        }

        // ── ToGlobal/ToLocal 互转 ──
        public static void TestNode2DToGlobalToLocalRoundTrip()
        {
            var node = new Node2D("N") { Position = new Vector2(5, 7), Rotation = 0.5f, Scale = new Vector2(2, 2) };

            var local = new Vector2(3, 1);
            var global = node.ToGlobal(local);
            var back = node.ToLocal(global);

            AssertNear(local.X, back.X, 0.01f, "ToLocal(ToGlobal(p)) must round-trip X.");
            AssertNear(local.Y, back.Y, 0.01f, "ToLocal(ToGlobal(p)) must round-trip Y.");
        }

        // ── 可见性传播：父隐藏则子 IsVisibleInTree=false ──
        public static void TestNode2DVisibilityPropagates()
        {
            var parent = new Node2D("Parent");
            var child = new Node2D("Child");
            parent.AddChild(child);

            AssertTrue(child.IsVisibleInTree, "Child must be visible when all ancestors visible.");

            parent.Visible = false;
            AssertTrue(!child.IsVisibleInTree, "Child must be hidden when parent hidden.");
            AssertTrue(child.Visible, "Child's own Visible flag must stay true (Godot semantics).");
        }

        // ── 自身隐藏：Visible=false 则自身不可见 ──
        public static void TestNode2DSelfHiddenNotVisibleInTree()
        {
            var node = new Node2D("N");
            node.Visible = false;
            AssertTrue(!node.IsVisibleInTree, "Self-hidden node must not be visible in tree.");
        }

        // ── Z-index 渲染顺序：升序、Z 相同保持遍历序 ──
        public static void TestNode2DZIndexRenderOrder()
        {
            var root = new Node2D("Root");
            var bg   = new Node2D("BG")   { ZIndex = -1 };
            var mid1 = new Node2D("Mid1") { ZIndex = 0 };
            var mid2 = new Node2D("Mid2") { ZIndex = 0 };
            var top  = new Node2D("Top")  { ZIndex = 5 };
            root.AddChild(bg);
            root.AddChild(top);
            root.AddChild(mid2);
            root.AddChild(mid1);

            var tree = new SceneTree2D();
            tree.AddRoot(root);
            var order = tree.GetRenderOrder();

            AssertEqual(5, order.Count, "All visible nodes must be collected (incl. root).");
            // 稳定排序：先按 Z 升序，Z 相同保持收集（树先序）顺序。
            // Root 是 Z=0 的第一个（先序根先于子），BG Z=-1 整体提前。
            AssertEqual("BG",   order[0].Name, "Z=-1 renders first.");
            AssertEqual("Root", order[1].Name, "Root (Z=0) precedes its Z=0 children in pre-order.");
            AssertEqual("Mid2", order[2].Name, "Z=0 ties keep tree order (Mid2 added before Mid1).");
            AssertEqual("Mid1", order[3].Name, "Z=0 ties keep tree order.");
            AssertEqual("Top",  order[4].Name, "Z=5 renders last.");
        }

        // ── 隐藏节点从渲染顺序中排除（含其子树） ──
        public static void TestNode2DHiddenSubtreeExcludedFromRender()
        {
            var root = new Node2D("Root");
            var hidden = new Node2D("HiddenBranch") { Visible = false };
            var hiddenChild = new Node2D("HiddenChild");
            hidden.AddChild(hiddenChild);
            var visible = new Node2D("Visible");
            root.AddChild(hidden);
            root.AddChild(visible);

            var tree = new SceneTree2D();
            tree.AddRoot(root);
            var order = tree.GetRenderOrder();

            AssertEqual(2, order.Count, "Hidden branch + its child must be excluded.");
            AssertEqual("Root",    order[0].Name, "Root must appear.");
            AssertEqual("Visible", order[1].Name, "Visible branch must appear.");
        }

        // ── modulate 累积：父*子逐级相乘 ──
        public static void TestNode2DModulateAccumulates()
        {
            var root = new Node2D("Root") { Modulate = new Vector4(1, 0.5f, 1, 1) };
            var child = new Node2D("Child") { Modulate = new Vector4(1, 1, 0.5f, 1) };
            root.AddChild(child);

            var eff = child.GetEffectiveModulate();
            AssertNear(1f,   eff.X, 0.001f, "Root.X=1 * Child.X=1 = 1.");
            AssertNear(0.5f, eff.Y, 0.001f, "Root.Y=0.5 * Child.Y=1 = 0.5.");
            AssertNear(0.5f, eff.Z, 0.001f, "Root.Z=1 * Child.Z=0.5 = 0.5.");
            AssertNear(1f,   eff.W, 0.001f, "Alpha must stay 1.");
        }

        private static void AssertNear(float got, float want, float tol, string msg)
        {
            if (MathF.Abs(got - want) > tol)
                throw new Exception($"{msg} (got {got:F4}, want {want:F4}).");
        }
    }
}
