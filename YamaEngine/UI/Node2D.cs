// =============================================================================
//  Yama Engine - Node2D.cs
//  yama引擎 - 2D 节点层级树（对应 Godot 的 CanvasItem + Node2D）
//
//  对应 Godot 2D 场景图的核心语义：
//    * CanvasItem 基类：可见性（Visible 沿树传播）、Z-index 排序、modulate 调制色。
//    * Node2D：本地 Position/Rotation/Scale + 全局变换（父链累积）。
//    * 父子层级：AddChild/SetParent，全局变换 = 父全局 × 本地。
//    * 按 Z-index 升序收集可见节点（渲染顺序）。
// =============================================================================
using System;
using System.Collections.Generic;
using System.Numerics;

namespace YamaEngine.UI
{
    /// <summary>
    /// 2D 节点基类（对应 Godot CanvasItem）：可见性传播 + Z-index + 调制色。
    /// </summary>
    public abstract class CanvasItem2D
    {
        private bool _visible = true;

        /// <summary>节点名。</summary>
        public string Name { get; set; } = "CanvasItem";

        /// <summary>自身可见性（最终可见 = 自身 && 所有祖先可见）。</summary>
        public bool Visible
        {
            get => _visible;
            set { _visible = value; }
        }

        /// <summary>沿树传播的最终可见性（对应 Godot is_visible_in_tree）。</summary>
        public bool IsVisibleInTree
        {
            get
            {
                if (!_visible) return false;
                var p = Parent;
                while (p != null)
                {
                    if (!p.Visible) return false;
                    p = p.Parent;
                }
                return true;
            }
        }

        /// <summary>Z-index（同层内升序绘制，值小在底层）。</summary>
        public int ZIndex { get; set; }

        /// <summary>调制色（乘法叠加到自身与子节点）。</summary>
        public Vector4 Modulate { get; set; } = Vector4.One;

        /// <summary>父节点。</summary>
        public CanvasItem2D? Parent { get; private set; }

        /// <summary>子节点列表。</summary>
        public List<CanvasItem2D> Children { get; } = new();

        /// <summary>子节点数。</summary>
        public int ChildCount => Children.Count;

        /// <summary>添加子节点（自动解除旧父）。</summary>
        public void AddChild(CanvasItem2D child)
        {
            if (child == this) return;
            child.Parent?.Children.Remove(child);
            child.Parent = this;
            Children.Add(child);
        }

        /// <summary>移除子节点。</summary>
        public bool RemoveChild(CanvasItem2D child)
        {
            if (child.Parent != this) return false;
            child.Parent = null;
            Children.Remove(child);
            return true;
        }

        /// <summary>设置父节点。</summary>
        public void SetParent(CanvasItem2D? newParent)
        {
            if (newParent == Parent) return;
            Parent?.Children.Remove(this);
            if (newParent != null)
            {
                newParent.Children.Add(this);
            }
            Parent = newParent;
        }

        /// <summary>沿树累积的调制色（对应 Godot get_modulate 累积）。</summary>
        public Vector4 GetEffectiveModulate()
        {
            var m = Modulate;
            var p = Parent;
            while (p != null)
            {
                m *= p.Modulate;
                p = p.Parent;
            }
            return m;
        }

        /// <summary>递归收集可见子树中按 Z-index 升序的节点（渲染顺序）。</summary>
        public void CollectVisibleSorted(List<CanvasItem2D> result)
        {
            if (!IsVisibleInTree) return;
            result.Add(this);
            foreach (var c in Children)
                c.CollectVisibleSorted(result);
        }

        /// <summary>
        /// 即时绘制命令缓冲（对应 Godot CanvasItem.draw_*）。命令在本地空间录入，
        /// 每帧由引擎 ClearDrawCommands 清空（immediate mode）。
        /// </summary>
        public List<DrawCommand> DrawCommands { get; } = new();

        /// <summary>清空绘制命令（每帧开始时调用，对应 Godot immediate-mode 重建）。</summary>
        public void ClearDrawCommands() => DrawCommands.Clear();

        /// <summary>返回按全局变换转换到世界空间的绘制命令副本（不影响本地命令）。</summary>
        public List<DrawCommand> GetWorldDrawCommands()
        {
            var world = new List<DrawCommand>();
            Matrix3x2 xform = Matrix3x2.Identity;
            if (this is Node2D n2d)
                xform = n2d.GlobalTransform;
            foreach (var cmd in DrawCommands)
                world.Add(CanvasItemDrawExtensions.TransformCommand(cmd, xform));
            return world;
        }
    }

    /// <summary>
    /// 2D 变换节点（对应 Godot Node2D）：本地变换 + 全局变换（父链累积）。
    /// </summary>
    public class Node2D : CanvasItem2D
    {
        private Vector2 _position = Vector2.Zero;
        private float _rotation;
        private Vector2 _scale = Vector2.One;

        /// <summary>本地位置。</summary>
        public Vector2 Position
        {
            get => _position;
            set { _position = value; }
        }

        /// <summary>本地旋转（弧度）。</summary>
        public float Rotation
        {
            get => _rotation;
            set { _rotation = value; }
        }

        /// <summary>本地缩放。</summary>
        public Vector2 Scale
        {
            get => _scale;
            set { _scale = value; }
        }

        /// <summary>构造。</summary>
        public Node2D(string name = "Node2D")
        {
            Name = name;
        }

        /// <summary>本地变换矩阵（TR 顺序：先旋转后平移，缩放内嵌）。</summary>
        public Matrix3x2 LocalTransform
        {
            get
            {
                var m = Matrix3x2.CreateScale(_scale);
                m = Matrix3x2.Multiply(m, Matrix3x2.CreateRotation(_rotation));
                m = Matrix3x2.Multiply(m, Matrix3x2.CreateTranslation(_position));
                return m;
            }
        }

        /// <summary>全局变换矩阵（父链累积，对应 Godot get_global_transform）。</summary>
        public Matrix3x2 GlobalTransform
        {
            get
            {
                var local = LocalTransform;
                var parent2d = Parent as Node2D;
                if (parent2d == null) return local;
                // 子全局 = 子本地 × 父全局
                return Matrix3x2.Multiply(local, parent2d.GlobalTransform);
            }
        }

        /// <summary>全局位置（对应 Godot global_position）。</summary>
        public Vector2 GlobalPosition
        {
            get
            {
                var p = Vector2.Transform(Vector2.Zero, GlobalTransform);
                return p;
            }
            set
            {
                // 反解本地位置：从父全局变换的逆变换世界点
                var parent2d = Parent as Node2D;
                if (parent2d == null)
                {
                    _position = value;
                }
                else
                {
                    Matrix3x2.Invert(parent2d.GlobalTransform, out var inv);
                    _position = Vector2.Transform(value, inv);
                }
            }
        }

        /// <summary>用全局变换把本地点转为世界点。</summary>
        public Vector2 ToGlobal(Vector2 localPoint)
            => Vector2.Transform(localPoint, GlobalTransform);

        /// <summary>用全局变换的逆把世界点转为本地点。</summary>
        public Vector2 ToLocal(Vector2 globalPoint)
        {
            Matrix3x2.Invert(GlobalTransform, out var inv);
            return Vector2.Transform(globalPoint, inv);
        }

        /// <summary>平移（本地坐标累加）。</summary>
        public void Translate(Vector2 delta) => _position += delta;

        /// <summary>每帧更新（子类可 override 并调 base.Update）。SceneTree2D.Update 递归调用。</summary>
        public virtual void Update(float dt) { }
    }

    /// <summary>
    /// 2D 节点树根（对应 SceneTree 的 2D 部分）：持有根节点、收集渲染顺序。
    /// </summary>
    public sealed class SceneTree2D
    {
        /// <summary>树名。</summary>
        public string Name { get; set; } = "SceneTree2D";

        /// <summary>
        /// YSort 开关：开启后同 Z-index 内按全局 Y 升序排序（俯视角深度排序）。
        /// </summary>
        public bool YSortEnabled { get; set; }

        /// <summary>根节点列表（可多个顶层节点）。</summary>
        public List<Node2D> Roots { get; } = new();

        /// <summary>添加根节点。</summary>
        public void AddRoot(Node2D node) => Roots.Add(node);

        /// <summary>
        /// 收集渲染顺序：所有可见节点（IsVisibleInTree）按 Z-index 升序返回，
        /// Z-index 相同保持树遍历顺序（稳定）。
        /// </summary>
        public List<CanvasItem2D> GetRenderOrder()
        {
            var collected = new List<CanvasItem2D>();
            foreach (var root in Roots)
                root.CollectVisibleSorted(collected);

            // 按 Z-index 稳定排序（List.Sort 非稳定，用索引做稳定键）
            var indexed = new List<(CanvasItem2D item, int idx)>();
            for (int i = 0; i < collected.Count; i++)
                indexed.Add((collected[i], i));

            // YSort 关闭：Z → 原始索引（树先序稳定）
            // YSort 开启：Z → 全局 Y 升序 → 原始索引（俯视角深度排序，
            //   Y 越大越靠下 → 越后绘制 → 在前景遮挡 Y 小的）
            var ysort = YSortEnabled;
            var yCache = new Dictionary<CanvasItem2D, float>();
            if (ysort)
            {
                foreach (var (item, _) in indexed)
                    yCache[item] = GetGlobalY(item);
            }

            indexed.Sort((a, b) =>
            {
                int z = a.item.ZIndex.CompareTo(b.item.ZIndex);
                if (z != 0) return z;
                if (ysort)
                {
                    int y = yCache[a.item].CompareTo(yCache[b.item]);
                    if (y != 0) return y;
                }
                return a.idx.CompareTo(b.idx);
            });

            var order = new List<CanvasItem2D>();
            foreach (var (item, _) in indexed)
                order.Add(item);
            return order;
        }

        /// <summary>求节点在树中的全局 Y 坐标（用于 YSort）。</summary>
        private static float GetGlobalY(CanvasItem2D item)
        {
            if (item is Node2D n2d)
                return n2d.GlobalPosition.Y;
            return 0f;
        }

        /// <summary>
        /// 每帧更新：先序遍历所有根，递归调用每个 Node2D 的 Update(dt)。
        /// </summary>
        public void Update(float dt)
        {
            foreach (var root in Roots)
                UpdateRecursive(root, dt);
        }

        private static void UpdateRecursive(Node2D node, float dt)
        {
            node.Update(dt);
            foreach (var child in node.Children)
            {
                if (child is Node2D n2d)
                    UpdateRecursive(n2d, dt);
            }
        }
    }
}
