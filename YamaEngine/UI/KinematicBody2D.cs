// =============================================================================
//  KinematicBody2D.cs
//  yama引擎 - 2D 运动学物理体（对应 Godot 的 KinematicBody2D + move_and_slide）
//
//  对应 Godot 3 的 2D 运动学物理语义：
//    * StaticBody2D：静态碰撞体（地形、墙），不移动
//    * KinematicBody2D：由代码驱动的碰撞体（玩家、NPC）
//    * move_and_slide(velocity, up_direction, slope_stop_min_velocity)
//      → 按速度移动，逐轴滑过碰撞面，返回剩余速度
//    * is_on_floor / is_on_wall / is_on_ceiling：根据最近碰撞法线判定
//    * get_slide_collision(i) / get_slide_count()：碰撞信息
//    * 轴分离移动：先 X 后 Y，撞到就归零该轴速度（滑动）
// =============================================================================

using System;
using System.Collections.Generic;
using System.Numerics;

namespace YamaEngine.UI
{
    /// <summary>碰撞法线方向分类。</summary>
    public enum CollisionNormal
    {
        None,
        Floor,   // 法线朝上（对应 Godot is_on_floor）
        Wall,    // 法线水平（对应 Godot is_on_wall）
        Ceiling, // 法线朝下（对应 Godot is_on_ceiling）
    }

    /// <summary>一次滑动碰撞信息（对应 Godot KinematicCollision2D）。</summary>
    public sealed class SlideCollision
    {
        /// <summary>碰撞到的对方（StaticBody2D 或 KinematicBody2D）。</summary>
        public PhysicsBody2D Collider { get; set; }

        /// <summary>碰撞法线（指向对方表面的单位向量）。</summary>
        public Vector2 Normal { get; set; }

        /// <summary>法线分类。</summary>
        public CollisionNormal Classification { get; set; }
    }

    /// <summary>物理体基类（StaticBody2D 和 KinematicBody2D 共同父类）。</summary>
    public abstract class PhysicsBody2D : Area2D
    {
        /// <summary>碰撞层掩码（本物体属于哪些层）。</summary>
        public uint CollisionLayer { get; set; } = 1;

        /// <summary>碰撞遮罩（本物体检测哪些层）。</summary>
        public uint CollisionMask { get; set; } = 1;

        protected PhysicsBody2D(string name) : base(name) { }
    }

    /// <summary>静态碰撞体（对应 Godot StaticBody2D）。地形、平台、墙。</summary>
    public sealed class StaticBody2D : PhysicsBody2D
    {
        public StaticBody2D(string name = "StaticBody2D") : base(name) { }
    }

    /// <summary>
    /// 运动学碰撞体（对应 Godot KinematicBody2D）。由代码驱动，逐轴滑动碰撞。
    /// </summary>
    public sealed class KinematicBody2D : PhysicsBody2D
    {
        /// <summary>上一帧速度（供外部读取）。</summary>
        public Vector2 Velocity { get; set; }

        /// <summary>最近一次 move_and_slide 后的碰撞列表。</summary>
        public IReadOnlyList<SlideCollision> SlideCollisions => _slideCollisions;

        /// <summary>上帧移动后的法线分类（供 is_on_floor 等查询）。</summary>
        public CollisionNormal LastFloorState { get; private set; } = CollisionNormal.None;

        private readonly List<SlideCollision> _slideCollisions = new();
        private Vector2 _floorNormal = new(0f, -1f); // 屏幕坐标 Y 向下，朝上为地板方向

        /// <summary>地板方向（默认 (0,-1) 屏幕坐标下地板朝上，即法线 (0,-1)）。</summary>
        public Vector2 FloorNormal
        {
            get => _floorNormal;
            set => _floorNormal = Vector2.Normalize(value);
        }

        public KinematicBody2D(string name = "KinematicBody2D") : base(name) { }

        /// <summary>
        /// 移动并滑动（对应 Godot move_and_slide）。
        /// 逐轴分离移动：先 X 后 Y，撞到则该轴速度归零（滑动）。
        /// 返回剩余速度（被碰撞削减后的）。
        /// </summary>
        /// <param name="velocity">本帧期望速度（像素/秒）。</param>
        /// <param name="upDirection">判定地板的上方向（默认 (0,-1)）。</param>
        /// <param name="world">碰撞世界（包含静态体列表）。</param>
        /// <returns>碰撞削减后的剩余速度。</returns>
        public Vector2 MoveAndSlide(
            Vector2 velocity,
            Vector2 upDirection,
            PhysicsWorld world)
        {
            Velocity = velocity;
            _slideCollisions.Clear();
            LastFloorState = CollisionNormal.None;

            Vector2 pos = Position;
            var remaining = velocity;

            // 逐轴移动：先 X 后 Y
            // X 轴
            if (remaining.X != 0f)
            {
                float dx = remaining.X * world.TimeStep;
                Vector2 newXPos = new(pos.X + dx, pos.Y);
                var hit = world.CastOverlap(this, newXPos, Shape, CollisionMask);
                if (hit != null)
                {
                    // 碰到：X 速度归零（停止滑动 X）
                    RecordCollision(hit, CollisionNormal.Wall, upDirection);
                    remaining = new Vector2(0f, remaining.Y);
                }
                else
                {
                    pos = newXPos;
                }
            }

            // Y 轴
            if (remaining.Y != 0f)
            {
                float dy = remaining.Y * world.TimeStep;
                Vector2 newYPos = new(pos.X, pos.Y + dy);
                var hit = world.CastOverlap(this, newYPos, Shape, CollisionMask);
                if (hit != null)
                {
                    // Y 方向：向下撞=地板，向上撞=天花板
                    var cls = remaining.Y > 0f ? CollisionNormal.Floor : CollisionNormal.Ceiling;
                    RecordCollision(hit, cls, upDirection);
                    remaining = new Vector2(remaining.X, 0f);
                }
                else
                {
                    pos = newYPos;
                }
            }

            Position = pos;
            return remaining;
        }

        /// <summary>简化的 move_and_slide（地板方向默认 (0,-1)，屏幕坐标 Y 向下）。</summary>
        public Vector2 MoveAndSlide(Vector2 velocity, PhysicsWorld world)
            => MoveAndSlide(velocity, new Vector2(0f, -1f), world);

        private void RecordCollision(PhysicsBody2D collider, CollisionNormal cls, Vector2 upDir)
        {
            var normal = cls switch
            {
                CollisionNormal.Floor => new Vector2(0f, -1f),
                CollisionNormal.Ceiling => new Vector2(0f, 1f),
                CollisionNormal.Wall => new Vector2(Velocity.X > 0f ? -1f : 1f, 0f),
                _ => Vector2.Zero,
            };
            _slideCollisions.Add(new SlideCollision
            {
                Collider = collider,
                Normal = normal,
                Classification = cls,
            });
            if (cls == CollisionNormal.Floor)
                LastFloorState = CollisionNormal.Floor;
        }

        /// <summary>是否在地板上（对应 Godot is_on_floor）。</summary>
        public bool IsOnFloor => LastFloorState == CollisionNormal.Floor;

        /// <summary>是否在墙上（对应 Godot is_on_wall）。</summary>
        public bool IsOnWall
        {
            get
            {
                foreach (var c in _slideCollisions)
                    if (c.Classification == CollisionNormal.Wall) return true;
                return false;
            }
        }

        /// <summary>是否在天花板上（对应 Godot is_on_ceiling）。</summary>
        public bool IsOnCeiling
        {
            get
            {
                foreach (var c in _slideCollisions)
                    if (c.Classification == CollisionNormal.Ceiling) return true;
                return false;
            }
        }

        /// <summary>滑动碰撞数（对应 Godot get_slide_count）。</summary>
        public int GetSlideCount => _slideCollisions.Count;

        /// <summary>第 i 个滑动碰撞（对应 Godot get_slide_collision）。</summary>
        public SlideCollision? GetSlideCollision(int i)
            => i >= 0 && i < _slideCollisions.Count ? _slideCollisions[i] : null;
    }

    /// <summary>
    /// 2D 物理世界：持有所有 PhysicsBody2D，提供 AABB 重叠投射。
    /// </summary>
    public sealed class PhysicsWorld
    {
        private readonly List<PhysicsBody2D> _bodies = new();

        /// <summary>物理步长（秒/帧，默认 1/60）。</summary>
        public float TimeStep { get; set; } = 1f / 60f;

        /// <summary>注册的物理体数。</summary>
        public int BodyCount => _bodies.Count;

        /// <summary>注册物理体。</summary>
        public void AddBody(PhysicsBody2D body)
        {
            if (!_bodies.Contains(body))
                _bodies.Add(body);
        }

        /// <summary>移除物理体。</summary>
        public bool RemoveBody(PhysicsBody2D body) => _bodies.Remove(body);

        /// <summary>
        /// 在给定位置投射形状，找到第一个与之重叠的**其他**物理体（按引用跳过 self）。
        /// 仅匹配 CollisionMask 命中的层。返回 null 表示无碰撞。
        /// </summary>
        public PhysicsBody2D? CastOverlap(PhysicsBody2D self, Vector2 testPosition, Shape2D? shape, uint mask)
        {
            if (shape == null) return null;
            foreach (var body in _bodies)
            {
                if (ReferenceEquals(body, self)) continue; // 按引用跳过自己
                if (body.Shape == null) continue;
                // 层掩码过滤：body.CollisionLayer & mask
                if ((body.CollisionLayer & mask) == 0) continue;

                var ca = shape.GetGlobalCenter(testPosition);
                var cb = body.Shape.GetGlobalCenter(body.GlobalPosition);
                if (ShapesOverlap(shape, ca, body.Shape, cb))
                    return body;
            }
            return null;
        }

        /// <summary>形状-形状重叠测试（复用 Area2D 的算法思路）。</summary>
        private static bool ShapesOverlap(Shape2D sa, Vector2 ca, Shape2D sb, Vector2 cb)
        {
            return (sa.Kind, sb.Kind) switch
            {
                (ShapeKind.Circle, ShapeKind.Circle) => CircleCircle(ca, (CircleShape2D)sa, cb, (CircleShape2D)sb),
                (ShapeKind.Rectangle, ShapeKind.Rectangle) => RectRect(ca, (RectangleShape2D)sa, cb, (RectangleShape2D)sb),
                (ShapeKind.Circle, ShapeKind.Rectangle) => CircleRect(ca, (CircleShape2D)sa, cb, (RectangleShape2D)sb),
                (ShapeKind.Rectangle, ShapeKind.Circle) => CircleRect(cb, (CircleShape2D)sb, ca, (RectangleShape2D)sa),
                (ShapeKind.Capsule, ShapeKind.Rectangle) => CircleRect(ca, new CircleShape2D { Radius = ((CapsuleShape2D)sa).Radius, Offset = sa.Offset }, cb, (RectangleShape2D)sb),
                (ShapeKind.Rectangle, ShapeKind.Capsule) => CircleRect(cb, new CircleShape2D { Radius = ((CapsuleShape2D)sb).Radius, Offset = sb.Offset }, ca, (RectangleShape2D)sa),
                (ShapeKind.Capsule, ShapeKind.Circle) => CircleCircle(ca, new CircleShape2D { Radius = ((CapsuleShape2D)sa).Radius, Offset = sa.Offset }, cb, (CircleShape2D)sb),
                (ShapeKind.Circle, ShapeKind.Capsule) => CircleCircle(ca, (CircleShape2D)sa, cb, new CircleShape2D { Radius = ((CapsuleShape2D)sb).Radius, Offset = sb.Offset }),
                (ShapeKind.Capsule, ShapeKind.Capsule) => CircleCircle(ca, new CircleShape2D { Radius = ((CapsuleShape2D)sa).Radius, Offset = sa.Offset }, cb, new CircleShape2D { Radius = ((CapsuleShape2D)sb).Radius, Offset = sb.Offset }),
                _ => false,
            };
        }

        private static bool CircleCircle(Vector2 c1, CircleShape2D s1, Vector2 c2, CircleShape2D s2)
        {
            float dx = c2.X - c1.X, dy = c2.Y - c1.Y;
            float rSum = s1.Radius + s2.Radius;
            return dx * dx + dy * dy < rSum * rSum;
        }

        private static bool RectRect(Vector2 c1, RectangleShape2D s1, Vector2 c2, RectangleShape2D s2)
        {
            float dx = MathF.Abs(c2.X - c1.X), dy = MathF.Abs(c2.Y - c1.Y);
            return dx < s1.HalfWidth + s2.HalfWidth && dy < s1.HalfHeight + s2.HalfHeight;
        }

        private static bool CircleRect(Vector2 cc, CircleShape2D cs, Vector2 rc, RectangleShape2D rs)
        {
            float nx = Math.Clamp(cc.X, rc.X - rs.HalfWidth, rc.X + rs.HalfWidth);
            float ny = Math.Clamp(cc.Y, rc.Y - rs.HalfHeight, rc.Y + rs.HalfHeight);
            float dx = cc.X - nx, dy = cc.Y - ny;
            return dx * dx + dy * dy < cs.Radius * cs.Radius;
        }
    }
}
