using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace YamaEngine.Physics
{
    // ============================================================================================
    //  ContactSolver.cs
    //
    //  接触求解器。窄相位生成的接触点会有穿透，求解器的任务是通过施加冲量，把穿透消除掉，
    //  同时处理反弹(弹性)和摩擦。
    //
    //  采用的核心方法是"序贯冲量法"(Sequential Impulse)，Box2D作者Erin Catto提出的。
    //  本质上是Gauss-Seidel迭代：每个约束轮流求解，多次迭代后收敛。
    //
    //  分两阶段：
    //    1. 速度求解(Velocity Solve) - 修改速度消除相对法向速度(分离)和切向速度(摩擦)
    //    2. 位置求解(Position Solve) - 直接修改位置消除残余穿透(Baumgarte或NGS)
    //
    //  注意：质量不能为0(实际用逆质量0表示无穷大质量，比如静态体)
    //  TODO: 接触流形持久化(warm starting)，目前每帧重建，会损失收敛速度
    // ============================================================================================

    /// <summary>
    /// 接触求解器。每个岛屿独立调用SolveIsland。
    /// </summary>
    public sealed class ContactSolver
    {
        private readonly int _velocityIterations;
        private readonly int _positionIterations;

        // 预计算的接触约束数据，每帧重建
        private ContactConstraint[] _constraints = new ContactConstraint[256];
        private int _constraintCount;

        /// <summary>单个接触点对应的约束数据(预计算)。</summary>
        private struct ContactConstraint
        {
            public int BodyA;
            public int BodyB;
            public Vector3 Normal;
            public Vector3 Tangent1;
            public Vector3 Tangent2;
            public Vector3 ContactPoint;

            // 从质心到接触点的向量(世界系)
            public Vector3 RA;
            public Vector3 RB;

            // 有效质量(normal/tangent1/tangent2)
            public float NormalMass;
            public float TangentMass1;
            public float TangentMass2;

            // 累积冲量(warm starting用)
            public float NormalImpulse;
            public float TangentImpulse1;
            public float TangentImpulse2;

            public float Restitution;
            public float Friction;
            public float Penetration;
            public float VelocityBias;
        }

        public ContactSolver(int velocityIterations = 8, int positionIterations = 3)
        {
            _velocityIterations = velocityIterations;
            _positionIterations = positionIterations;
        }

        /// <summary>
        /// 求解一个岛屿内的所有接触约束。
        /// </summary>
        public void SolveIsland(List<BodySlot> bodies, List<ContactPoint> contacts,
            PhysicsIsland island, float dt, float baumgarte, float slop)
        {
            if (island.ContactIndices.Count == 0) return;

            var bodySpan = bodies.AsSpan();
            var contactSpan = contacts.AsSpan();

            // 1) 预处理：把接触点转成ContactConstraint，算有效质量等
            PrepareConstraints(bodySpan, contactSpan, island, dt, baumgarte, slop);

            // 2) warm starting(暂未实现持久化流形，先空着)

            // 3) 速度求解
            for (int iter = 0; iter < _velocityIterations; iter++)
            {
                SolveVelocity(bodySpan);
            }

            // 4) 位置求解(NGS - Non-linear Gauss-Seidel)
            for (int iter = 0; iter < _positionIterations; iter++)
            {
                SolvePosition(bodySpan, baumgarte, slop);
            }

            // 5) 把累积冲量写回接触点(给warm start和摩擦记录用)
            WriteBackImpulses(contactSpan, island);
        }

        // =========================================================================
        //  预处理：构建ContactConstraint
        // =========================================================================

        private void PrepareConstraints(Span<BodySlot> bodies, Span<ContactPoint> contacts,
            PhysicsIsland island, float dt, float baumgarte, float slop)
        {
            _constraintCount = island.ContactIndices.Count;
            if (_constraintCount > _constraints.Length)
                _constraints = new ContactConstraint[_constraintCount * 2];

            for (int i = 0; i < _constraintCount; i++)
            {
                int contactIdx = island.ContactIndices[i];
                ref var cp = ref contacts[contactIdx];
                ref var c = ref _constraints[i];

                c.BodyA = cp.BodyAIndex;
                c.BodyB = cp.BodyBIndex;
                c.Normal = cp.Normal;
                c.Tangent1 = cp.Tangent1;
                c.Tangent2 = cp.Tangent2;
                c.ContactPoint = cp.Point;
                c.Penetration = cp.Penetration;
                c.Restitution = cp.Restitution;
                c.Friction = cp.Friction;

                ref var bodyA = ref bodies[c.BodyA];
                ref var bodyB = ref bodies[c.BodyB];

                // 质心到接触点向量
                c.RA = cp.Point - bodyA.Position;
                c.RB = cp.Point - bodyB.Position;

                // 算有效质量: K = 1/mA + 1/mB + (rA x n)^T * InvIA * (rA x n) + (rB x n)^T * InvIB * (rB x n)
                // 简化标量: kn = invMassA + invMassB + ((rA x n) · (InvIA * (rA x n))) + ((rB x n) · (InvIB * (rB x n)))
                float invMassA = bodyA.InvMass;
                float invMassB = bodyB.InvMass;

                Vector3 rAxBn = Vector3.Cross(c.RA, c.Normal);
                Vector3 rBxBn = Vector3.Cross(c.RB, c.Normal);
                Vector3 invIA_rAxN = TransformByInertia(rAxBn, bodyA.InvInertiaWorld);
                Vector3 invIB_rBxN = TransformByInertia(rBxBn, bodyB.InvInertiaWorld);

                float kn = invMassA + invMassB +
                           Vector3.Dot(rAxBn, invIA_rAxN) +
                           Vector3.Dot(rBxBn, invIB_rBxN);

                c.NormalMass = kn > 1e-8f ? 1f / kn : 0f;

                // 切向有效质量(两个切向)
                Vector3 rAxT1 = Vector3.Cross(c.RA, c.Tangent1);
                Vector3 rBxT1 = Vector3.Cross(c.RB, c.Tangent1);
                Vector3 invIA_rAxT1 = TransformByInertia(rAxT1, bodyA.InvInertiaWorld);
                Vector3 invIB_rBxT1 = TransformByInertia(rAxT1, bodyB.InvInertiaWorld);
                float kt1 = invMassA + invMassB +
                            Vector3.Dot(rAxT1, invIA_rAxT1) +
                            Vector3.Dot(rBxT1, invIB_rBxT1);
                c.TangentMass1 = kt1 > 1e-8f ? 1f / kt1 : 0f;

                Vector3 rAxT2 = Vector3.Cross(c.RA, c.Tangent2);
                Vector3 rBxT2 = Vector3.Cross(c.RB, c.Tangent2);
                Vector3 invIA_rAxT2 = TransformByInertia(rAxT2, bodyA.InvInertiaWorld);
                Vector3 invIB_rBxT2 = TransformByInertia(rBxT2, bodyB.InvInertiaWorld);
                float kt2 = invMassA + invMassB +
                            Vector3.Dot(rAxT2, invIA_rAxT2) +
                            Vector3.Dot(rBxT2, invIB_rBxT2);
                c.TangentMass2 = kt2 > 1e-8f ? 1f / kt2 : 0f;

                // 速度bias：法向相对速度。如果物体已经在分离，不要给负冲量
                Vector3 vA = bodyA.LinearVelocity + Vector3.Cross(bodyA.AngularVelocity, c.RA);
                Vector3 vB = bodyB.LinearVelocity + Vector3.Cross(bodyB.AngularVelocity, c.RB);
                Vector3 relativeVel = vB - vA;
                float velAlongNormal = Vector3.Dot(relativeVel, c.Normal);

                // 弹性：只在物体接近时反弹，已经分离的不反弹
                // restitution * (-velAlongNormal) 但只在velAlongNormal < -threshold时
                float restitutionThreshold = -1.0f; // m/s 阈值，慢于这个不反弹
                c.VelocityBias = 0f;
                if (velAlongNormal < restitutionThreshold)
                {
                    c.VelocityBias = -c.Restitution * velAlongNormal;
                }

                // 从上一帧恢复累积冲量(目前没持久化，每次都是0)
                c.NormalImpulse = 0f;
                c.TangentImpulse1 = 0f;
                c.TangentImpulse2 = 0f;
            }
        }

        // =========================================================================
        //  速度求解
        // =========================================================================

        private void SolveVelocity(Span<BodySlot> bodies)
        {
            for (int i = 0; i < _constraintCount; i++)
            {
                ref var c = ref _constraints[i];
                ref var bodyA = ref bodies[c.BodyA];
                ref var bodyB = ref bodies[c.BodyB];

                // ---- 法向冲量 ----
                Vector3 vA = bodyA.LinearVelocity + Vector3.Cross(bodyA.AngularVelocity, c.RA);
                Vector3 vB = bodyB.LinearVelocity + Vector3.Cross(bodyB.AngularVelocity, c.RB);
                Vector3 relativeVel = vB - vA;
                float velAlongNormal = Vector3.Dot(relativeVel, c.Normal);

                float lambda = -c.NormalMass * (velAlongNormal - c.VelocityBias);
                float oldImpulse = c.NormalImpulse;
                // 非负约束：接触不能拉(只有推)
                c.NormalImpulse = MathF.Max(oldImpulse + lambda, 0f);
                lambda = c.NormalImpulse - oldImpulse;

                Vector3 impulse = c.Normal * lambda;
                ApplyImpulse(ref bodyB, impulse, c.RB);
                ApplyImpulse(ref bodyA, -impulse, c.RA);

                // ---- 摩擦(切向冲量) ----
                // 重新算相对速度(法向冲量已改变)
                vA = bodyA.LinearVelocity + Vector3.Cross(bodyA.AngularVelocity, c.RA);
                vB = bodyB.LinearVelocity + Vector3.Cross(bodyB.AngularVelocity, c.RB);
                relativeVel = vB - vA;

                // 切向1
                float velAlongT1 = Vector3.Dot(relativeVel, c.Tangent1);
                float lambdaT1 = -c.TangentMass1 * velAlongT1;
                float maxFriction = c.Friction * c.NormalImpulse;
                float oldT1 = c.TangentImpulse1;
                c.TangentImpulse1 = Math.Clamp(oldT1 + lambdaT1, -maxFriction, maxFriction);
                lambdaT1 = c.TangentImpulse1 - oldT1;

                Vector3 impulseT1 = c.Tangent1 * lambdaT1;
                ApplyImpulse(ref bodyB, impulseT1, c.RB);
                ApplyImpulse(ref bodyA, -impulseT1, c.RA);

                // 切向2
                vA = bodyA.LinearVelocity + Vector3.Cross(bodyA.AngularVelocity, c.RA);
                vB = bodyB.LinearVelocity + Vector3.Cross(bodyB.AngularVelocity, c.RB);
                relativeVel = vB - vA;
                float velAlongT2 = Vector3.Dot(relativeVel, c.Tangent2);
                float lambdaT2 = -c.TangentMass2 * velAlongT2;
                float oldT2 = c.TangentImpulse2;
                c.TangentImpulse2 = Math.Clamp(oldT2 + lambdaT2, -maxFriction, maxFriction);
                lambdaT2 = c.TangentImpulse2 - oldT2;

                Vector3 impulseT2 = c.Tangent2 * lambdaT2;
                ApplyImpulse(ref bodyB, impulseT2, c.RB);
                ApplyImpulse(ref bodyA, -impulseT2, c.RA);
            }
        }

        // =========================================================================
        //  位置求解 (NGS)
        //  直接修改位置/朝向消除穿透，比Baumgarte更稳定但更慢
        // =========================================================================

        private void SolvePosition(Span<BodySlot> bodies, float baumgarte, float slop)
        {
            for (int i = 0; i < _constraintCount; i++)
            {
                ref var c = ref _constraints[i];
                ref var bodyA = ref bodies[c.BodyA];
                ref var bodyB = ref bodies[c.BodyB];

                // 重新算当前穿透(位置可能已被上次迭代改了)
                Vector3 rA = c.ContactPoint - bodyA.Position; // 注意：这里rA应该用接触点-新质心位置
                Vector3 rB = c.ContactPoint - bodyB.Position;

                // 算位置约束误差：当前两体在接触点处的位置差沿法向的投影
                Vector3 pA = bodyA.Position + rA;
                Vector3 pB = bodyB.Position + rB;
                Vector3 error = pB - pA;
                float penetration = Vector3.Dot(error, c.Normal);

                // slop：容忍小穿透，避免抖动
                // Baumgarte：只修正一部分
                float C = MathF.Max(-penetration + slop, 0f) * baumgarte;
                if (C <= 0f) continue;

                // 算位置有效质量(和速度的一样)
                Vector3 rAxN = Vector3.Cross(rA, c.Normal);
                Vector3 rBxN = Vector3.Cross(rB, c.Normal);
                float kn = bodyA.InvMass + bodyB.InvMass +
                           Vector3.Dot(rAxN, TransformByInertia(rAxN, bodyA.InvInertiaWorld)) +
                           Vector3.Dot(rBxN, TransformByInertia(rBxN, bodyB.InvInertiaWorld));
                if (kn < 1e-8f) continue;

                float lambda = -C / kn;
                Vector3 impulse = c.Normal * lambda;

                // 位置修正
                bodyA.Position -= impulse * bodyA.InvMass;
                bodyB.Position += impulse * bodyB.InvMass;

                // 朝向修正(用旋转近似)
                if (bodyA.MotionType != BodyMotionType.Static)
                {
                    Vector3 torqueImpulse = Vector3.Cross(rA, -impulse);
                    Vector3 deltaOmega = TransformByInertia(torqueImpulse, bodyA.InvInertiaWorld);
                    // 用很小的角度修正朝向
                    float angle = deltaOmega.Length();
                    if (angle > 1e-8f)
                    {
                        var axis = deltaOmega / angle;
                        // 限制角度，避免过大旋转
                        angle = MathF.Min(angle, 0.1f);
                        var dq = Quaternion.CreateFromAxisAngle(axis, angle);
                        bodyA.Orientation = Quaternion.Normalize(Quaternion.Multiply(dq, bodyA.Orientation));
                    }
                }
                if (bodyB.MotionType != BodyMotionType.Static)
                {
                    Vector3 torqueImpulse = Vector3.Cross(rB, impulse);
                    Vector3 deltaOmega = TransformByInertia(torqueImpulse, bodyB.InvInertiaWorld);
                    float angle = deltaOmega.Length();
                    if (angle > 1e-8f)
                    {
                        var axis = deltaOmega / angle;
                        angle = MathF.Min(angle, 0.1f);
                        var dq = Quaternion.CreateFromAxisAngle(axis, angle);
                        bodyB.Orientation = Quaternion.Normalize(Quaternion.Multiply(dq, bodyB.Orientation));
                    }
                }
            }
        }

        // =========================================================================
        //  把累积冲量写回接触点
        // =========================================================================

        private void WriteBackImpulses(Span<ContactPoint> contacts, PhysicsIsland island)
        {
            for (int i = 0; i < _constraintCount; i++)
            {
                int contactIdx = island.ContactIndices[i];
                ref var cp = ref contacts[contactIdx];
                ref var c = ref _constraints[i];
                cp.NormalImpulse = c.NormalImpulse;
                cp.TangentImpulse1 = c.TangentImpulse1;
                cp.TangentImpulse2 = c.TangentImpulse2;
            }
        }

        // =========================================================================
        //  辅助：施加冲量
        // =========================================================================

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ApplyImpulse(ref BodySlot body, Vector3 impulse, Vector3 r)
        {
            if (body.MotionType == BodyMotionType.Static) return;
            // 线速度
            body.LinearVelocity += impulse * body.InvMass;
            // 角速度: omega += I^-1 * (r x J)
            Vector3 torqueImpulse = Vector3.Cross(r, impulse);
            body.AngularVelocity += TransformByInertia(torqueImpulse, body.InvInertiaWorld);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector3 TransformByInertia(Vector3 v, Matrix4x4 m)
        {
            return new Vector3(
                m.M11 * v.X + m.M12 * v.Y + m.M13 * v.Z,
                m.M21 * v.X + m.M22 * v.Y + m.M23 * v.Z,
                m.M31 * v.X + m.M32 * v.Y + m.M33 * v.Z);
        }

        // =========================================================================
        //  调试用接口
        // =========================================================================

        /// <summary>获取上一次求解的约束数。</summary>
        public int GetLastConstraintCount() => _constraintCount;

        /// <summary>调试用：单独求解一对body的单个接触，返回法向冲量。</summary>
        public float DebugSolveSingle(ref BodySlot a, ref BodySlot b,
            Vector3 contactPoint, Vector3 normal, float penetration,
            float restitution, float friction, float dt, float baumgarte)
        {
            var c = new ContactConstraint
            {
                BodyA = 0,
                BodyB = 1,
                Normal = normal,
                ContactPoint = contactPoint,
                Penetration = penetration,
                Restitution = restitution,
                Friction = friction,
                RA = contactPoint - a.Position,
                RB = contactPoint - b.Position,
            };

            // 算切向
            Vector3 refVec = MathF.Abs(normal.X) < 0.9f ? new Vector3(1, 0, 0) : new Vector3(0, 1, 0);
            c.Tangent1 = Vector3.Normalize(Vector3.Cross(normal, refVec));
            c.Tangent2 = Vector3.Cross(normal, c.Tangent1);

            // 算有效质量
            Vector3 rAxN = Vector3.Cross(c.RA, normal);
            Vector3 rBxN = Vector3.Cross(c.RB, normal);
            float kn = a.InvMass + b.InvMass +
                       Vector3.Dot(rAxN, TransformByInertia(rAxN, a.InvInertiaWorld)) +
                       Vector3.Dot(rBxN, TransformByInertia(rBxN, b.InvInertiaWorld));
            c.NormalMass = kn > 1e-8f ? 1f / kn : 0f;

            Vector3 vA = a.LinearVelocity + Vector3.Cross(a.AngularVelocity, c.RA);
            Vector3 vB = b.LinearVelocity + Vector3.Cross(b.AngularVelocity, c.RB);
            float velAlongNormal = Vector3.Dot(vB - vA, normal);
            c.VelocityBias = velAlongNormal < -1f ? -restitution * velAlongNormal : 0f;

            c.NormalImpulse = 0f;
            _constraints[0] = c;
            _constraintCount = 1;

            // 跑几次迭代
            var bodies = new BodySlot[2] { a, b };
            for (int i = 0; i < _velocityIterations; i++)
            {
                SolveVelocity(bodies);
            }

            a = bodies[0];
            b = bodies[1];
            return c.NormalImpulse;
        }
    }
}
