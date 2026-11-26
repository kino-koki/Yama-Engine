using System;
namespace YamaEngine.Reality
{
    /// <summary>一个接触点。Normal 从 A 指向 B；*Lambda 为 warm starting 累积冲量。</summary>
    public struct YrContact
    {
        public int BodyA;
        public int BodyB;
        public RVec3 Point;
        public RVec3 Normal;
        public float Depth;
        public float Restitution;
        public float FrictionStatic;
        public float FrictionDynamic;
        public float NormalLambda;
        public float TangentLambda1;
        public float TangentLambda2;
        public float InitialNormalVelocity;
        public float TargetNormalVelocity;
    }

    /// <summary>
    /// 序列冲量（Sequential Impulse）接触求解器：把前三轮分散的
    /// YrConvex(SAT 法向/深度) + ContactFriction(摩擦锥) + ContinuousCollision(TOI)
    /// 接成一条完整窄相链，并加上 warm starting 与恢复系数目标速度。
    ///
    /// 关键点：RigidBody3D 以世界系角动量 L 为状态量，所以角冲量就是
    /// `L += r × j`，无需再乘 I⁻¹ —— 结构上不会把内力矩算错。
    /// 位置修正（穿透恢复）与速度求解分离，避免 Baumgarte 注入虚假动能。
    /// </summary>
    public sealed class YrContactSolver
    {
        private RigidBody3D[] _bodies = Array.Empty<RigidBody3D>();
        private YrContact[] _contacts = Array.Empty<YrContact>();

        public void SetBodies(RigidBody3D[] bodies)
            => _bodies = bodies ?? Array.Empty<RigidBody3D>();
        public void SetContacts(YrContact[] contacts)
            => _contacts = contacts ?? Array.Empty<YrContact>();

        public YrContact[] Contacts => _contacts;
        public RigidBody3D[] Bodies => _bodies;

        private bool Valid(in YrContact c)
            => c.BodyA >= 0 && c.BodyB >= 0
            && c.BodyA < _bodies.Length && c.BodyB < _bodies.Length
            && _bodies[c.BodyA] != null && _bodies[c.BodyB] != null;

        public static (RVec3 T1, RVec3 T2) Basis(RVec3 normal)
        {
            RVec3 n = normal.Normalized();
            RVec3 seed = MathF.Abs(n.Y) < 0.9f
                ? new RVec3(0f, 1f, 0f) : new RVec3(1f, 0f, 0f);
            RVec3 t1 = seed.Cross(n).Normalized();
            RVec3 t2 = n.Cross(t1).Normalized();
            return (t1, t2);
        }

        public static (RVec3 T1, RVec3 T2) BasisFromAxis(RVec3 axis)
        {
            RVec3 a = axis.Normalized();
            RVec3 seed = MathF.Abs(a.Y) < 0.9f
                ? new RVec3(0f, 1f, 0f) : new RVec3(1f, 0f, 0f);
            RVec3 t1 = seed.Cross(a).Normalized();
            RVec3 t2 = a.Cross(t1).Normalized();
            return (t1, t2);
        }

        private RVec3 RelativeVelocityAt(in YrContact c)
        {
            RigidBody3D a = _bodies[c.BodyA], b = _bodies[c.BodyB];
            RVec3 ra = c.Point - a.Position, rb = c.Point - b.Position;
            RVec3 va = a.LinearVelocity + a.AngularVelocityWorld().Cross(ra);
            RVec3 vb = b.LinearVelocity + b.AngularVelocityWorld().Cross(rb);
            return vb - va;
        }

        /// <summary>沿 dir 的广义有效质量：invM_A + invM_B + 两体转动贡献。</summary>
        public float EffectiveMass(in YrContact c, RVec3 dir)
        {
            RigidBody3D a = _bodies[c.BodyA], b = _bodies[c.BodyB];
            RVec3 ra = c.Point - a.Position, rb = c.Point - b.Position;
            float angular = a.WorldInvInertiaApply(ra.Cross(dir)).Cross(ra).Dot(dir)
                          + b.WorldInvInertiaApply(rb.Cross(dir)).Cross(rb).Dot(dir);
            return a.InvMass + b.InvMass + angular;
        }

        /// <summary>jOnB 为作用于 B 的冲量；A 受 -jOnB。同一点作用力与反作用力，合力矩为零。</summary>
        public void ApplyImpulsePair(in YrContact c, RVec3 jOnB)
        {
            RigidBody3D a = _bodies[c.BodyA], b = _bodies[c.BodyB];
            a.ApplyLinearImpulse(-jOnB);
            b.ApplyLinearImpulse(jOnB);
            RVec3 ra = c.Point - a.Position, rb = c.Point - b.Position;
            a.ApplyAngularImpulse(-(ra.Cross(jOnB)));
            b.ApplyAngularImpulse(rb.Cross(jOnB));
        }

        /// <summary>
        /// 记录恢复系数目标速度并施加 warm start 冲量。
        /// 目标速度只由初始法向速度决定（e 只作用一次），后续迭代目标为 0，
        /// 避免逐迭代重复注入弹性能量。
        /// </summary>
        public void Prepare()
        {
            for (int k = 0; k < _contacts.Length; k++)
            {
                ref var c = ref _contacts[k];
                if (!Valid(c)) continue;
                RVec3 n = c.Normal.Normalized();
                var basis = Basis(n);
                c.InitialNormalVelocity = RelativeVelocityAt(c).Dot(n);
                c.TargetNormalVelocity = c.InitialNormalVelocity < -1.0f
                    ? -c.Restitution * c.InitialNormalVelocity : 0f;
                RVec3 warm = n * c.NormalLambda
                           + basis.T1 * c.TangentLambda1
                           + basis.T2 * c.TangentLambda2;
                ApplyImpulsePair(c, warm);
            }
        }

        public void Solve(int iterations)
        {
            for (int it = 0; it < iterations; it++)
            {
                for (int k = 0; k < _contacts.Length; k++)
                {
                    ref var c = ref _contacts[k];
                    if (!Valid(c)) continue;
                    SolveNormal(ref c);
                    SolveFriction(ref c);
                }
            }
        }

        private void SolveNormal(ref YrContact c)
        {
            RVec3 n = c.Normal.Normalized();
            float vn = RelativeVelocityAt(c).Dot(n);
            float eff = EffectiveMass(c, n);
            if (eff <= 1e-12f) return;
            float delta = (c.TargetNormalVelocity - vn) / eff;
            float old = c.NormalLambda;
            float next = MathF.Max(0f, old + delta);
            c.NormalLambda = next;
            ApplyImpulsePair(c, n * (next - old));
        }

        private void SolveFriction(ref YrContact c)
        {
            RVec3 n = c.Normal.Normalized();
            var basis = Basis(n);
            RVec3 vrel = RelativeVelocityAt(c);
            float v1 = vrel.Dot(basis.T1), v2 = vrel.Dot(basis.T2);
            float slip = MathF.Sqrt(v1 * v1 + v2 * v2);
            float mu = slip > 1e-4f ? c.FrictionDynamic : c.FrictionStatic;
            float e1 = EffectiveMass(c, basis.T1), e2 = EffectiveMass(c, basis.T2);
            float d1 = e1 > 1e-12f ? -v1 / e1 : 0f;
            float d2 = e2 > 1e-12f ? -v2 / e2 : 0f;
            float l1 = c.TangentLambda1 + d1;
            float l2 = c.TangentLambda2 + d2;
            // 二维库仑圆锥投影（不是盒式近似）：切向合冲量模长 <= mu * |lambda_n|
            float limit = MathF.Max(0f, mu * c.NormalLambda);
            float len = MathF.Sqrt(l1 * l1 + l2 * l2);
            if (len > limit && len > 1e-12f)
            {
                float s = limit / len;
                l1 *= s; l2 *= s;
            }
            RVec3 applied = basis.T1 * (l1 - c.TangentLambda1)
                          + basis.T2 * (l2 - c.TangentLambda2);
            c.TangentLambda1 = l1;
            c.TangentLambda2 = l2;
            ApplyImpulsePair(c, applied);
        }

        /// <summary>
        /// 纯位置投影消除穿透：不改线速度/角动量，因此不注入动能。
        /// 与速度级冲量求解分离，是「恢复系数不被污染」的关键。
        /// </summary>
        public void CorrectPositions(float beta, float slop)
        {
            for (int k = 0; k < _contacts.Length; k++)
            {
                ref var c = ref _contacts[k];
                if (!Valid(c)) continue;
                RigidBody3D a = _bodies[c.BodyA], b = _bodies[c.BodyB];
                float w = a.InvMass + b.InvMass;
                if (w <= 0f) continue;
                float pen = MathF.Max(0f, c.Depth - slop);
                if (pen <= 0f) continue;
                RVec3 n = c.Normal.Normalized();
                float corr = beta * pen / w;
                a.Position = a.Position - n * (corr * a.InvMass);
                b.Position = b.Position + n * (corr * b.InvMass);
            }
        }

        /// <summary>
        /// 总角动量（关于世界原点）：自旋 L + 轨道 r×(mv)。
        /// 单独看 L_A+L_B 并不守恒——这是写守恒断言时最常见的错误。
        /// </summary>
        public static RVec3 TotalAngularMomentum(RigidBody3D[] bodies)
        {
            RVec3 total = RVec3.Zero;
            for (int i = 0; i < bodies.Length; i++)
            {
                RigidBody3D b = bodies[i];
                if (b == null) continue;
                float mass = b.Mass > 1e-9f ? b.Mass : 0f;
                total = total + b.AngularMomentum
                      + b.Position.Cross(b.LinearVelocity * mass);
            }
            return total;
        }

        public static RVec3 TotalLinearMomentum(RigidBody3D[] bodies)
        {
            RVec3 total = RVec3.Zero;
            for (int i = 0; i < bodies.Length; i++)
            {
                RigidBody3D b = bodies[i];
                if (b == null) continue;
                float mass = b.Mass > 1e-9f ? b.Mass : 0f;
                total = total + b.LinearVelocity * mass;
            }
            return total;
        }
    }
}
