using System;
namespace YamaEngine.Reality
{
    /// <summary>单位四元数（w 为实部）。避免与引擎既有 Quaternion 类型冲突。</summary>
    public readonly struct RQuat
    {
        public readonly float X, Y, Z, W;
        public RQuat(float x, float y, float z, float w) { X = x; Y = y; Z = z; W = w; }
        public static RQuat Identity => new RQuat(0f, 0f, 0f, 1f);

        public static RQuat FromAxisAngle(RVec3 axis, float angle)
        {
            RVec3 a = axis.Normalized();
            float h = angle * 0.5f;
            float s = MathF.Sin(h);
            return new RQuat(a.X * s, a.Y * s, a.Z * s, MathF.Cos(h));
        }

        public RQuat Normalized()
        {
            float n = MathF.Sqrt(X * X + Y * Y + Z * Z + W * W);
            return n > 1e-9f ? new RQuat(X / n, Y / n, Z / n, W / n) : Identity;
        }

        public static RQuat operator *(RQuat a, RQuat b) => new RQuat(
            a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
            a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X,
            a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W,
            a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z);

        /// <summary>用四元数旋转向量：v + 2w(q×v) + 2 q×(q×v)。</summary>
        public RVec3 Rotate(RVec3 v)
        {
            float cx = Y * v.Z - Z * v.Y;
            float cy = Z * v.X - X * v.Z;
            float cz = X * v.Y - Y * v.X;
            float ccx = Y * cz - Z * cy;
            float ccy = Z * cx - X * cz;
            float ccz = X * cy - Y * cx;
            return new RVec3(
                v.X + 2f * (W * cx + ccx),
                v.Y + 2f * (W * cy + ccy),
                v.Z + 2f * (W * cz + ccz));
        }

        /// <summary>旋转的逆作用于向量（世界 → 局部）。</summary>
        public RVec3 RotateInverse(RVec3 v)
        {
            RQuat c = new RQuat(-X, -Y, -Z, W);
            return c.Rotate(v);
        }
    }

    /// <summary>
    /// 刚体：以「世界系角动量 L」为状态量而非角速度 ω。
    /// 无力矩时 L 精确守恒（逐位不变）；各向异性惯性张量下 ω 会随姿态进动，
    /// 这是自由刚体的真实行为，而「把 ω 当常量积分」是常见但错误的简化。
    /// 惯性张量在局部系取对角 (Ixx,Iyy,Izz)，世界系 I = R·I_local·Rᵀ。
    /// </summary>
    public sealed class RigidBody3D
    {
        public RVec3 Position;
        public RQuat Orientation = RQuat.Identity;
        public RVec3 LinearVelocity;
        public RVec3 AngularMomentum;      // 世界系
        public float Mass = 1f;
        public RVec3 InvInertiaLocal = new RVec3(1f, 1f, 1f);
        private RVec3 _force;
        private RVec3 _torque;             // 世界系力矩

        public float InvMass => Mass > 1e-9f ? 1f / Mass : 0f;

        /// <summary>局部系主惯性矩（对角）。盒子解析式。</summary>
        public static RVec3 BoxInertia(float mass, float sx, float sy, float sz)
        {
            float k = mass / 12f;
            return new RVec3(k * (sy * sy + sz * sz), k * (sx * sx + sz * sz), k * (sx * sx + sy * sy));
        }
        public static RVec3 SphereInertia(float mass, float radius)
        {
            float i = 0.4f * mass * radius * radius;
            return new RVec3(i, i, i);
        }
        public void SetInertia(RVec3 principal)
        {
            InvInertiaLocal = new RVec3(
                principal.X > 1e-12f ? 1f / principal.X : 0f,
                principal.Y > 1e-12f ? 1f / principal.Y : 0f,
                principal.Z > 1e-12f ? 1f / principal.Z : 0f);
        }

        public void ApplyForce(RVec3 force, RVec3 worldPoint)
        {
            _force = _force + force;
            RVec3 r = worldPoint - Position;
            _torque = _torque + new RVec3(
                r.Y * force.Z - r.Z * force.Y,
                r.Z * force.X - r.X * force.Z,
                r.X * force.Y - r.Y * force.X);
        }
        public void ApplyTorque(RVec3 torque) => _torque = _torque + torque;
        public void ApplyLinearImpulse(RVec3 impulse) => LinearVelocity = LinearVelocity + impulse * InvMass;
        public void ApplyAngularImpulse(RVec3 impulse) => AngularMomentum = AngularMomentum + impulse;

        /// <summary>世界系角速度：ω = R · (I_local⁻¹ · (Rᵀ · L))。</summary>
        public RVec3 AngularVelocityWorld()
        {
            RVec3 lb = Orientation.RotateInverse(AngularMomentum);
            RVec3 wb = new RVec3(lb.X * InvInertiaLocal.X, lb.Y * InvInertiaLocal.Y, lb.Z * InvInertiaLocal.Z);
            return Orientation.Rotate(wb);
        }

        public RVec3 WorldInvInertiaApply(RVec3 worldVector)
        {
            RVec3 b = Orientation.RotateInverse(worldVector);
            RVec3 t = new RVec3(b.X * InvInertiaLocal.X, b.Y * InvInertiaLocal.Y, b.Z * InvInertiaLocal.Z);
            return Orientation.Rotate(t);
        }

        /// <summary>半隐式积分：先更新守恒量 L / v，再由 L 求 ω 推进姿态。</summary>
        public void Integrate(float dt)
        {
            LinearVelocity = LinearVelocity + _force * (InvMass * dt);
            AngularMomentum = AngularMomentum + _torque * dt;
            Position = Position + LinearVelocity * dt;

            RVec3 w = AngularVelocityWorld();
            var wq = new RQuat(w.X, w.Y, w.Z, 0f);
            RQuat dq = wq * Orientation;
            Orientation = new RQuat(
                Orientation.X + 0.5f * dq.X * dt,
                Orientation.Y + 0.5f * dq.Y * dt,
                Orientation.Z + 0.5f * dq.Z * dt,
                Orientation.W + 0.5f * dq.W * dt).Normalized();

            _force = RVec3.Zero;
            _torque = RVec3.Zero;
        }

        public float KineticEnergy()
        {
            RVec3 w = AngularVelocityWorld();
            double linear = 0.5 * Mass * LinearVelocity.LengthSq();
            double angular = 0.5 * w.Dot(AngularMomentum);
            return (float)(linear + angular);
        }
    }
}
