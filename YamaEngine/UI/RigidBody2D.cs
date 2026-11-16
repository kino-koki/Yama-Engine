// =============================================================================
//  RigidBody2D.cs
//  yama引擎 - 2D 刚体物理（对应 Godot 的 RigidBody2D）
//
//  对应 Godot 3 的 2D 刚体语义：
//    * RigidBody2D：受重力/力/冲量驱动的动态体（质量、速度、反弹、摩擦）
//    * gravity_scale / mass / weight
//    * linear_velocity 每步 += gravity * gravity_scale * dt
//    * apply_impulse(impulse) → velocity += impulse / mass
//    * bounce（弹性系数）：撞到静态体沿法线反射 v' = v - (1+e)(v·n)n
//    * friction：切向速度衰减
//    * sleep：速度低于阈值进入休眠，省 CPU
//    * 碰撞通过 CastOverlap（AABB/圆近似）检测静态体，法线由位置差推导
// =============================================================================

using System;
using System.Collections.Generic;
using System.Numerics;

namespace YamaEngine.UI
{
    /// <summary>
    /// 2D 刚体（对应 Godot RigidBody2D）。受重力驱动，可施加力/冲量，与静态体碰撞反弹。
    /// </summary>
    public sealed class RigidBody2D : PhysicsBody2D
    {
        /// <summary>质量（千克，对应 Godot mass）。</summary>
        public float Mass { get; set; } = 1f;

        /// <summary>重力缩放（对应 Godot gravity_scale，1=正常重力，0=无重力）。</summary>
        public float GravityScale { get; set; } = 1f;

        /// <summary>线速度（像素/秒，对应 Godot linear_velocity）。</summary>
        public Vector2 LinearVelocity { get; set; }

        /// <summary>弹性系数（0=完全不弹，1=完全弹性，对应 Godot bounce）。</summary>
        public float Bounce { get; set; } = 0f;

        /// <summary>摩擦系数（切向速度每步乘以 (1-friction) 衰减，对应 Godot friction）。</summary>
        public float Friction { get; set; } = 1f;

        /// <summary>是否休眠（速度低于阈值时为 true，对应 Godot sleeping）。</summary>
        public bool Sleeping { get; set; }

        /// <summary>是否允许休眠（对应 Godot can_sleep）。</summary>
        public bool CanSleep { get; set; } = true;

        /// <summary>进入休眠的线速度阈值（默认 20 px/s，对应 Godot linear_velocity sleep threshold）。</summary>
        public float LinearVelocitySleepThreshold { get; set; } = 20f;

        /// <summary>本步累积的碰撞法线（供外部读取，对应 Godot contact monitor）。</summary>
        public IReadOnlyList<Vector2> ContactNormals => _contactNormals;

        private readonly List<Vector2> _contactNormals = new();

        /// <summary>角速度（弧度/秒，逆时针为正，对应 Godot angular_velocity）。</summary>
        public float AngularVelocity { get; set; }

        /// <summary>转动惯量覆盖（>0 时使用此值；=0 时由质量+形状自动计算，对应 Godot inertia 覆盖）。</summary>
        public float InertiaOverride { get; set; }

        /// <summary>进入休眠的角速度阈值（对应 Godot angular_velocity sleep threshold）。</summary>
        public float AngularVelocitySleepThreshold { get; set; } = 1f;

        /// <summary>转动惯量 I：覆盖值优先，否则由质量+形状自动计算（2D 标量惯量）。</summary>
        public float Inertia => InertiaOverride > 0f
            ? InertiaOverride
            : ComputeInertia();

        /// <summary>由质量 + 形状计算 2D 转动惯量（对应 Godot 自动惯量）。</summary>
        private float ComputeInertia()
        {
            if (Shape == null || Mass <= 0f) return float.MaxValue;
            switch (Shape.Kind)
            {
                case ShapeKind.Circle:
                {
                    float r = ((CircleShape2D)Shape).Radius;
                    return 0.5f * Mass * r * r;
                }
                case ShapeKind.Rectangle:
                {
                    var r = (RectangleShape2D)Shape;
                    float w = r.HalfWidth * 2f, h = r.HalfHeight * 2f;
                    return Mass * (w * w + h * h) / 12f;
                }
                case ShapeKind.Capsule:
                {
                    var c = (CapsuleShape2D)Shape;
                    float w = c.Radius * 2f, h = c.Height;
                    return Mass * (w * w + h * h) / 12f;
                }
                default:
                    return float.MaxValue;
            }
        }

        public RigidBody2D(string name = "RigidBody2D") : base(name) { }

        /// <summary>重量 = mass * |gravity|（对应 Godot weight）。gravity 默认向下。</summary>
        public float GetWeight(Vector2 gravity) => Mass * gravity.Length();

        /// <summary>
        /// 施加冲量（对应 Godot apply_impulse）。瞬时改变速度：Δv = impulse / mass。
        /// impulse 在质心作用（本简化 2D 模型无旋转，中心冲量=普通冲量）。
        /// </summary>
        public void ApplyImpulse(Vector2 impulse)
        {
            if (Mass <= 0f) return;
            LinearVelocity += impulse / Mass;
            WakeUp();
        }

        /// <summary>施加中心冲量（对应 Godot apply_central_impulse，无旋转=同 ApplyImpulse）。</summary>
        public void ApplyCentralImpulse(Vector2 impulse) => ApplyImpulse(impulse);

        /// <summary>
        /// 施加偏心冲量（对应 Godot apply_impulse(impulse, offset)）。
        /// 线速度 += impulse/mass；角冲量 = offset × impulse（2D 叉积 z 分量），立即生效：角速度 += 角冲量/I。
        /// </summary>
        public void ApplyImpulseAtOffset(Vector2 impulse, Vector2 offset)
        {
            ApplyImpulse(impulse);
            float angularImpulse = CrossZ(offset, impulse); // r × F
            ApplyAngularImpulse(angularImpulse);
        }

        /// <summary>施加持续扭矩（对应 Godot apply_torque，本步内有效；角加速度 = torque/I）。</summary>
        public void ApplyTorque(float torque, float dt)
        {
            float I = Inertia;
            if (I <= 0f || float.IsPositiveInfinity(I)) return;
            AngularVelocity += (torque / I) * dt;
            WakeUp();
        }

        /// <summary>施加角冲量（对应 Godot apply_torque_impulse；角速度 += angularImpulse/I）。</summary>
        public void ApplyAngularImpulse(float angularImpulse)
        {
            float I = Inertia;
            if (I <= 0f || float.IsPositiveInfinity(I)) return;
            AngularVelocity += angularImpulse / I;
            WakeUp();
        }

        /// <summary>旋转积分：angle += angularVelocity * dt（弧度，逆时针为正）。</summary>
        public void IntegrateRotation(float dt)
        {
            Rotation += AngularVelocity * dt;
        }

        /// <summary>2D 叉积 z 分量（标量）：a × b = a.x·b.y - a.y·b.x。</summary>
        private static float CrossZ(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;

        /// <summary>施加持续的力（对应 Godot apply_force，本步内有效，Step 前调用）。</summary>
        public void ApplyForce(Vector2 force, float dt)
        {
            if (Mass <= 0f) return;
            LinearVelocity += (force / Mass) * dt;
            WakeUp();
        }

        /// <summary>唤醒刚体（对应 Godot 忙碌时唤醒）。</summary>
        public void WakeUp() => Sleeping = false;

        /// <summary>重力积分：velocity += gravity * gravity_scale * dt。</summary>
        public void IntegrateGravity(Vector2 gravity, float dt)
        {
            LinearVelocity += gravity * (GravityScale * dt);
        }

        /// <summary>位移积分：position += velocity * dt。</summary>
        public void IntegratePosition(float dt)
        {
            Position += LinearVelocity * dt;
        }

        /// <summary>
        /// 碰撞响应：与静态体沿法线反弹 + 切向摩擦衰减。
        /// 法线 n 指向刚体（从静态体指向刚体）。v·n > 0 表示正在分离，跳过。
        /// </summary>
        public void ResolveBounce(Vector2 normal)
        {
            float vn = Vector2.Dot(LinearVelocity, normal);
            if (vn > 0f) return; // 正在分离，不处理

            // 分解速度：法向 + 切向
            Vector2 normalComp = normal * vn;
            Vector2 tangent = LinearVelocity - normalComp;

            // 法向反射（乘以弹性 e；e=0 完全非弹性，法向速度归零）
            Vector2 newNormal = -normalComp * Bounce;
            // 切向摩擦衰减
            Vector2 newTangent = tangent * (1f - Friction);

            LinearVelocity = newTangent + newNormal;
            _contactNormals.Add(normal);
        }

        /// <summary>休眠判定（线速度+角速度低于阈值且允许休眠）。返回是否刚进入休眠。</summary>
        public bool UpdateSleep()
        {
            if (!CanSleep)
            {
                Sleeping = false;
                return false;
            }
            bool slowLinear = LinearVelocity.Length() < LinearVelocitySleepThreshold;
            bool slowAngular = MathF.Abs(AngularVelocity) < AngularVelocitySleepThreshold;
            if (!Sleeping && slowLinear && slowAngular)
            {
                Sleeping = true;
                LinearVelocity = Vector2.Zero;
                AngularVelocity = 0f;
                return true;
            }
            return false;
        }

        internal void BeginStep() => _contactNormals.Clear();
    }

    /// <summary>
    /// 2D 刚体物理世界：持有 RigidBody2D 与 StaticBody2D，每步积分重力 + 碰撞反弹。
    /// </summary>
    public sealed class RigidBodyWorld
    {
        private readonly List<RigidBody2D> _bodies = new();
        private readonly List<PhysicsBody2D> _statics = new();

        /// <summary>重力加速度（默认 (0, 980) 屏幕向下，对应 Godot 2D 默认重力）。</summary>
        public Vector2 Gravity { get; set; } = new(0f, 980f);

        /// <summary>物理步长（秒/帧）。</summary>
        public float TimeStep { get; set; } = 1f / 60f;

        /// <summary>注册的刚体数。</summary>
        public int BodyCount => _bodies.Count;

        /// <summary>注册的静态体数。</summary>
        public int StaticCount => _statics.Count;

        public void AddBody(RigidBody2D body)
        {
            if (!_bodies.Contains(body)) _bodies.Add(body);
        }

        public void RemoveBody(RigidBody2D body) => _bodies.Remove(body);

        public void AddStatic(PhysicsBody2D body)
        {
            if (!_statics.Contains(body)) _statics.Add(body);
        }

        public void RemoveStatic(PhysicsBody2D body) => _statics.Remove(body);

        /// <summary>
        /// 推进一个物理步：重力积分 → 位移 → 碰撞检测与反弹 → 休眠判定。
        /// 休眠中的刚体跳过积分（对应 Godot sleeping body 不再计算）。
        /// </summary>
        public void Step(float dt)
        {
            foreach (var body in _bodies)
            {
                body.BeginStep();
                if (body.Sleeping) continue;

                // 1. 重力积分
                body.IntegrateGravity(Gravity, dt);

                // 2. 位移积分（先按未碰撞速度走一步）
                body.IntegratePosition(dt);
                body.IntegrateRotation(dt);

                // 3. 碰撞检测：与静态体测重叠，沿法线反弹
                if (body.Shape != null)
                {
                    foreach (var stat in _statics)
                    {
                        if (stat.Shape == null) continue;
                        if ((stat.CollisionLayer & body.CollisionMask) == 0) continue;

                        var ca = body.Shape.GetGlobalCenter(body.GlobalPosition);
                        var cb = stat.Shape.GetGlobalCenter(stat.GlobalPosition);
                        if (!RigidShapesOverlap(body.Shape, ca, stat.Shape, cb)) continue;

                        // 法线：从静态体指向刚体（简化：按中心连线方向；AABB 时按最小穿透轴）
                        var normal = ComputeNormal(body, ca, stat, cb);
                        body.ResolveBounce(normal);
                        // 推出穿透（位置修正）
                        body.Position += normal * ComputePenetration(body, ca, stat, cb);
                    }
                }

                // 4. 休眠判定
                body.UpdateSleep();
            }
        }

        private static bool RigidShapesOverlap(Shape2D sa, Vector2 ca, Shape2D sb, Vector2 cb)
        {
            return (sa.Kind, sb.Kind) switch
            {
                (ShapeKind.Circle, ShapeKind.Circle) => CircleCircle(ca, (CircleShape2D)sa, cb, (CircleShape2D)sb),
                (ShapeKind.Circle, ShapeKind.Rectangle) => CircleRect(ca, (CircleShape2D)sa, cb, (RectangleShape2D)sb),
                (ShapeKind.Rectangle, ShapeKind.Circle) => CircleRect(cb, (CircleShape2D)sb, ca, (RectangleShape2D)sa),
                (ShapeKind.Rectangle, ShapeKind.Rectangle) => RectRect(ca, (RectangleShape2D)sa, cb, (RectangleShape2D)sb),
                _ => false,
            };
        }

        private static bool CircleCircle(Vector2 a, CircleShape2D sa, Vector2 b, CircleShape2D sb)
            => Vector2.Distance(a, b) < sa.Radius + sb.Radius;

        private static bool RectRect(Vector2 a, RectangleShape2D sa, Vector2 b, RectangleShape2D sb)
            => MathF.Abs(a.X - b.X) < sa.HalfWidth + sb.HalfWidth
            && MathF.Abs(a.Y - b.Y) < sa.HalfHeight + sb.HalfHeight;

        private static bool CircleRect(Vector2 c, CircleShape2D sc, Vector2 r, RectangleShape2D sr)
        {
            float nx = Math.Clamp(c.X, r.X - sr.HalfWidth, r.X + sr.HalfWidth);
            float ny = Math.Clamp(c.Y, r.Y - sr.HalfHeight, r.Y + sr.HalfHeight);
            float dx = c.X - nx, dy = c.Y - ny;
            return dx * dx + dy * dy < sc.Radius * sc.Radius;
        }

        /// <summary>计算碰撞法线（指向刚体，从静态体出发）。轴对齐近似。</summary>
        private static Vector2 ComputeNormal(RigidBody2D body, Vector2 ca, PhysicsBody2D stat, Vector2 cb)
        {
            var d = ca - cb; // 静态体 → 刚体
            if (body.Shape.Kind == ShapeKind.Circle && stat.Shape.Kind == ShapeKind.Rectangle)
            {
                // 圆撞矩形：先看圆心是否在矩形内（深穿透），否则按圆心相对矩形位置定主轴
                var rect = (RectangleShape2D)stat.Shape;
                var circle = (CircleShape2D)body.Shape;
                bool insideX = MathF.Abs(d.X) < rect.HalfWidth;
                bool insideY = MathF.Abs(d.Y) < rect.HalfHeight;
                if (insideX && insideY)
                {
                    // 圆心在矩形内：按到各边距离最小者推出
                    float px = rect.HalfWidth - MathF.Abs(d.X);
                    float py = rect.HalfHeight - MathF.Abs(d.Y);
                    if (px < py) return new Vector2(d.X >= 0 ? 1 : -1, 0);
                    return new Vector2(0, d.Y >= 0 ? 1 : -1);
                }
                float ox = MathF.Abs(d.X) - rect.HalfWidth;
                float oy = MathF.Abs(d.Y) - rect.HalfHeight;
                if (ox > oy) return new Vector2(d.X >= 0 ? 1 : -1, 0);
                return new Vector2(0, d.Y >= 0 ? 1 : -1);
            }
            if (body.Shape.Kind == ShapeKind.Rectangle && stat.Shape.Kind == ShapeKind.Rectangle)
            {
                var ra = (RectangleShape2D)body.Shape;
                var rb = (RectangleShape2D)stat.Shape;
                float ox = (ra.HalfWidth + rb.HalfWidth) - MathF.Abs(d.X);
                float oy = (ra.HalfHeight + rb.HalfHeight) - MathF.Abs(d.Y);
                if (ox < oy) return new Vector2(d.X >= 0 ? 1 : -1, 0);
                return new Vector2(0, d.Y >= 0 ? 1 : -1);
            }
            // 圆-圆 或兜底：中心连线方向
            float len = d.Length();
            return len > 1e-6f ? d / len : new Vector2(0, -1);
        }

        /// <summary>估算穿透深度（用于位置修正，轴对齐近似）。</summary>
        private static float ComputePenetration(RigidBody2D body, Vector2 ca, PhysicsBody2D stat, Vector2 cb)
        {
            var d = ca - cb;
            if (body.Shape.Kind == ShapeKind.Circle && stat.Shape.Kind == ShapeKind.Circle)
            {
                float dist = d.Length();
                float overlap = ((CircleShape2D)body.Shape).Radius + ((CircleShape2D)stat.Shape).Radius - dist;
                return MathF.Max(0f, overlap);
            }
            if (body.Shape.Kind == ShapeKind.Circle && stat.Shape.Kind == ShapeKind.Rectangle)
            {
                var rect = (RectangleShape2D)stat.Shape;
                float ox = rect.HalfWidth + ((CircleShape2D)body.Shape).Radius - MathF.Abs(d.X);
                float oy = rect.HalfHeight + ((CircleShape2D)body.Shape).Radius - MathF.Abs(d.Y);
                return MathF.Max(0f, MathF.Min(ox, oy));
            }
            if (body.Shape.Kind == ShapeKind.Rectangle && stat.Shape.Kind == ShapeKind.Rectangle)
            {
                var ra = (RectangleShape2D)body.Shape;
                var rb = (RectangleShape2D)stat.Shape;
                float ox = (ra.HalfWidth + rb.HalfWidth) - MathF.Abs(d.X);
                float oy = (ra.HalfHeight + rb.HalfHeight) - MathF.Abs(d.Y);
                return MathF.Max(0f, MathF.Min(ox, oy));
            }
            return 0f;
        }
    }
}
