// =============================================================================
//  Area2D.cs
//  yama引擎 - 2D 碰撞区域（对应 Godot 的 Area2D + CollisionShape2D）
//
//  对应 Godot 3 的 2D 物理区域语义：
//    * CollisionShape2D：碰撞形状子节点（CircleShape2D / RectangleShape2D / CapsuleShape2D）
//    * Area2D：检测重叠（不做物理响应），发射 area_entered / area_exited
//    * monitoring：本区域是否主动检测；monitorable：本区域是否可被检测
//    * 每帧 Update 前后对比 overlap 集合，产生 entered/exited 事件
//    * 形状用**全局位置**做重叠测试（忽略旋转，2D 轴对齐近似）
// =============================================================================

using System;
using System.Collections.Generic;
using System.Numerics;

namespace YamaEngine.UI
{
    /// <summary>碰撞形状基类（对应 Godot 的 Shape2D）。</summary>
    public abstract class Shape2D
    {
        /// <summary>形状类型。</summary>
        public abstract ShapeKind Kind { get; }

        /// <summary>形状在 Area2D 本地空间内的偏移。</summary>
        public Vector2 Offset { get; set; } = Vector2.Zero;

        /// <summary>求形状在给定 Area2D 全局位置下的世界中心。</summary>
        public Vector2 GetGlobalCenter(Vector2 areaGlobalPos)
            => areaGlobalPos + Offset;
    }

    /// <summary>形状类型枚举。</summary>
    public enum ShapeKind { Circle, Rectangle, Capsule }

    /// <summary>圆形碰撞形状（对应 Godot CircleShape2D）。</summary>
    public sealed class CircleShape2D : Shape2D
    {
        public override ShapeKind Kind => ShapeKind.Circle;

        /// <summary>半径。</summary>
        public float Radius { get; set; } = 10f;

        public CircleShape2D() { }
        public CircleShape2D(float radius) { Radius = radius; }
    }

    /// <summary>矩形碰撞形状（对应 Godot RectangleShape2D）。Extents 为半宽/半高。</summary>
    public sealed class RectangleShape2D : Shape2D
    {
        public override ShapeKind Kind => ShapeKind.Rectangle;

        /// <summary>半宽（对应 Godot RectangleShape2D.extents.x）。</summary>
        public float HalfWidth { get; set; } = 10f;

        /// <summary>半高。</summary>
        public float HalfHeight { get; set; } = 10f;

        public RectangleShape2D() { }
        public RectangleShape2D(float halfWidth, float halfHeight)
        {
            HalfWidth = halfWidth;
            HalfHeight = halfHeight;
        }
    }

    /// <summary>胶囊碰撞形状（对应 Godot CapsuleShape2D）。竖直方向为主轴。</summary>
    public sealed class CapsuleShape2D : Shape2D
    {
        public override ShapeKind Kind => ShapeKind.Capsule;

        /// <summary>半径。</summary>
        public float Radius { get; set; } = 10f;

        /// <summary>总高度（包含两个半圆端）。</summary>
        public float Height { get; set; } = 40f;

        public CapsuleShape2D() { }
        public CapsuleShape2D(float radius, float height)
        {
            Radius = radius;
            Height = height;
        }
    }

    /// <summary>区域事件类型。</summary>
    public enum AreaEventKind { AreaEntered, AreaExited }

    /// <summary>区域事件参数。</summary>
    public sealed class AreaEventArgs
    {
        /// <summary>触发事件的对方区域。</summary>
        public Area2D Other { get; set; }

        /// <summary>事件类型。</summary>
        public AreaEventKind Kind { get; set; }
    }

    /// <summary>
    /// 2D 碰撞区域（对应 Godot Area2D）。检测与其他 Area2D 的重叠，
    /// 每帧对比前后 overlap 集合产生 entered/exited 事件。
    /// </summary>
    public class Area2D : Node2D
    {
        /// <summary>碰撞形状（对应 Area2D 的 CollisionShape2D 子节点持有形状）。</summary>
        public Shape2D? Shape { get; set; }

        /// <summary>是否主动检测其他区域（对应 Godot monitoring）。</summary>
        public bool Monitoring { get; set; } = true;

        /// <summary>是否可被其他区域检测（对应 Godot monitorable）。</summary>
        public bool Monitorable { get; set; } = true;

        /// <summary>当前帧重叠的区域集合（由 Area2DWorld.Update 计算）。</summary>
        public IReadOnlyList<Area2D> OverlappingAreas => _overlapping;

        private readonly List<Area2D> _overlapping = new();
        private readonly List<Area2D> _previous = new();

        /// <summary>area_entered 事件（对应 Godot 的 area_entered 信号）。</summary>
        public event Action<AreaEventArgs>? AreaEntered;

        /// <summary>area_exited 事件（对应 Godot 的 area_exited 信号）。</summary>
        public event Action<AreaEventArgs>? AreaExited;

        public Area2D(string name = "Area2D") : base(name) { }

        /// <summary>本帧开始时保存上一帧快照并清空当前（由 World 调用）。</summary>
        internal void BeginFrame()
        {
            _previous.Clear();
            _previous.AddRange(_overlapping);
            _overlapping.Clear();
        }

        /// <summary>登记一个本帧重叠的对方（由 World 计算 overlap 后调用）。</summary>
        internal void AddOverlap(Area2D other)
        {
            if (!_overlapping.Contains(other))
                _overlapping.Add(other);
        }

        /// <summary>
        /// 帧末对比前后快照产生 entered/exited 事件（由 World 调用）。
        /// </summary>
        internal void EndFrameEmitEvents()
        {
            // entered：在当前但不在上一帧
            foreach (var cur in _overlapping)
            {
                if (!_previous.Contains(cur))
                    AreaEntered?.Invoke(new AreaEventArgs { Other = cur, Kind = AreaEventKind.AreaEntered });
            }
            // exited：在上一帧但不在当前
            foreach (var prev in _previous)
            {
                if (!_overlapping.Contains(prev))
                    AreaExited?.Invoke(new AreaEventArgs { Other = prev, Kind = AreaEventKind.AreaExited });
            }
        }

        /// <summary>求本区域形状的全局中心。</summary>
        public Vector2 GetShapeGlobalCenter()
        {
            if (Shape == null) return GlobalPosition;
            return Shape.GetGlobalCenter(GlobalPosition);
        }
    }

    /// <summary>
    /// Area2D 重叠世界（对应 Godot 2D 物理空间的 Area2D 部分）。
    /// 每帧注册所有活动 Area2D，计算两两重叠，并触发 entered/exited 事件。
    /// </summary>
    public sealed class Area2DWorld
    {
        private readonly List<Area2D> _areas = new();

        /// <summary>注册的区域数。</summary>
        public int AreaCount => _areas.Count;

        /// <summary>注册一个区域。</summary>
        public void AddArea(Area2D area)
        {
            if (!_areas.Contains(area))
                _areas.Add(area);
        }

        /// <summary>移除一个区域。</summary>
        public bool RemoveArea(Area2D area) => _areas.Remove(area);

        /// <summary>
        /// 每帧：重算所有区域重叠关系，并产生 entered/exited 事件。
        /// 只测试 monitorable 且 monitoring 的区域对，形状为空的区域跳过。
        /// </summary>
        public void Update()
        {
            // 帧开始：保存上帧快照并清空当前
            foreach (var area in _areas)
                area.BeginFrame();

            // 两两重叠测试（broad-phase：先 AABB，再 narrow-phase）
            for (int i = 0; i < _areas.Count; i++)
            {
                for (int j = i + 1; j < _areas.Count; j++)
                {
                    var a = _areas[i];
                    var b = _areas[j];

                    // monitoring/monitorable 对称检查：
                    // a 监测 b 需要 a.Monitoring && b.Monitorable
                    // b 监测 a 需要 b.Monitoring && a.Monitorable
                    bool aCanSeeB = a.Monitoring && b.Monitorable;
                    bool bCanSeeA = b.Monitoring && a.Monitorable;
                    if (!aCanSeeB && !bCanSeeA) continue;

                    if (a.Shape == null || b.Shape == null) continue;

                    if (ShapesOverlap(a, b))
                    {
                        if (aCanSeeB) a.AddOverlap(b);
                        if (bCanSeeA) b.AddOverlap(a);
                    }
                }
            }

            // 帧末：对比前后产生事件
            foreach (var area in _areas)
                area.EndFrameEmitEvents();
        }

        /// <summary>两个区域的形状是否重叠（用全局中心做测试，忽略旋转）。</summary>
        private static bool ShapesOverlap(Area2D a, Area2D b)
        {
            var sa = a.Shape!;
            var sb = b.Shape!;
            var ca = sa.GetGlobalCenter(a.GlobalPosition);
            var cb = sb.GetGlobalCenter(b.GlobalPosition);

            return (sa.Kind, sb.Kind) switch
            {
                (ShapeKind.Circle, ShapeKind.Circle) => CircleCircle(ca, (CircleShape2D)sa, cb, (CircleShape2D)sb),
                (ShapeKind.Rectangle, ShapeKind.Rectangle) => RectRect(ca, (RectangleShape2D)sa, cb, (RectangleShape2D)sb),
                (ShapeKind.Circle, ShapeKind.Rectangle) => CircleRect(ca, (CircleShape2D)sa, cb, (RectangleShape2D)sb),
                (ShapeKind.Rectangle, ShapeKind.Circle) => CircleRect(cb, (CircleShape2D)sb, ca, (RectangleShape2D)sa),
                (ShapeKind.Capsule, ShapeKind.Circle) => CircleCircle(ca, new CircleShape2D { Radius = ((CapsuleShape2D)sa).Radius, Offset = sa.Offset }, cb, (CircleShape2D)sb),
                (ShapeKind.Circle, ShapeKind.Capsule) => CircleCircle(ca, (CircleShape2D)sa, cb, new CircleShape2D { Radius = ((CapsuleShape2D)sb).Radius, Offset = sb.Offset }),
                (ShapeKind.Capsule, ShapeKind.Capsule) => CircleCircle(ca, new CircleShape2D { Radius = ((CapsuleShape2D)sa).Radius, Offset = sa.Offset }, cb, new CircleShape2D { Radius = ((CapsuleShape2D)sb).Radius, Offset = sb.Offset }),
                (ShapeKind.Capsule, ShapeKind.Rectangle) => CircleRect(ca, new CircleShape2D { Radius = ((CapsuleShape2D)sa).Radius, Offset = sa.Offset }, cb, (RectangleShape2D)sb),
                (ShapeKind.Rectangle, ShapeKind.Capsule) => CircleRect(cb, new CircleShape2D { Radius = ((CapsuleShape2D)sb).Radius, Offset = sb.Offset }, ca, (RectangleShape2D)sa),
                _ => false,
            };
        }

        /// <summary>圆-圆重叠：中心距 < r1+r2。</summary>
        private static bool CircleCircle(Vector2 c1, CircleShape2D s1, Vector2 c2, CircleShape2D s2)
        {
            float dx = c2.X - c1.X;
            float dy = c2.Y - c1.Y;
            float distSq = dx * dx + dy * dy;
            float rSum = s1.Radius + s2.Radius;
            return distSq < rSum * rSum;
        }

        /// <summary>矩形-矩形重叠（AABB）：|dx| < hw1+hw2 && |dy| < hh1+hh2。</summary>
        private static bool RectRect(Vector2 c1, RectangleShape2D s1, Vector2 c2, RectangleShape2D s2)
        {
            float dx = MathF.Abs(c2.X - c1.X);
            float dy = MathF.Abs(c2.Y - c1.Y);
            return dx < s1.HalfWidth + s2.HalfWidth && dy < s1.HalfHeight + s2.HalfHeight;
        }

        /// <summary>圆-矩形重叠：矩形上离圆心最近点到圆心的距 < r。</summary>
        private static bool CircleRect(Vector2 cc, CircleShape2D cs, Vector2 rc, RectangleShape2D rs)
        {
            // 矩形上最近点（clamp 圆心到矩形 AABB）
            float nearestX = Math.Clamp(cc.X, rc.X - rs.HalfWidth, rc.X + rs.HalfWidth);
            float nearestY = Math.Clamp(cc.Y, rc.Y - rs.HalfHeight, rc.Y + rs.HalfHeight);
            float dx = cc.X - nearestX;
            float dy = cc.Y - nearestY;
            float distSq = dx * dx + dy * dy;
            return distSq < cs.Radius * cs.Radius;
        }
    }
}
