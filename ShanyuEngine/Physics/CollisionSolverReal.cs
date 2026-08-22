// ============================================================================================
//  CollisionSolverReal.cs
//
//  山雨引擎 (ShanyuEngine) 真实碰撞求解器模块 —— 纯手写实现，无外部物理库依赖。
//
//  内容：
//    - ContactPoint / ContactManifold：接触信息
//    - SequentialImpulseSolver：序贯冲量求解器（法线/切线/滚动摩擦/阻尼/restitution/warmstarting）
//    - PositionConstraintSolver：位置约束求解器（Baumgarte/位置投影/soft-hard/slop）
//    - 约束系统：Constraint基类 / PointConstraint / HingeConstraint / SliderConstraint
//      / ConeTwistConstraint / DistanceConstraint / Dof6SpringConstraint / GearConstraint
//      / PulleyConstraint / ContactConstraint
//
//  命名空间：ShanyuEngine.Physics
//  目标框架：net8.0 / C# 8.0+
//  外部依赖：仅 System.Numerics
// ============================================================================================

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace ShanyuEngine.Physics
{
    #region ======================== 接触信息 ========================

    /// <summary>单个接触点。</summary>
    public struct ContactPoint
    {
        /// <summary>接触点在世界空间的位置。</summary>
        public PhysicsVector3 Position;

        /// <summary>接触法线（从B指向A）。</summary>
        public PhysicsVector3 Normal;

        /// <summary>穿透深度。</summary>
        public float PenetrationDepth;

        /// <summary>接触点ID（用于持久化流形匹配）。</summary>
        public int ContactID;

        /// <summary>形状A索引。</summary>
        public int ShapeIndexA;

        /// <summary>形状B索引。</summary>
        public int ShapeIndexB;

        /// <summary>累积法线冲量（用于warmstarting）。</summary>
        public float NormalImpulse;

        /// <summary>累积切线冲量[0/1]（用于warmstarting）。</summary>
        public PhysicsVector2 TangentImpulse;
    }

    /// <summary>接触流形（两个形状之间的所有接触点集合）。</summary>
    public class ContactManifold
    {
        /// <summary>接触点列表。</summary>
        public List<ContactPoint> Points = new List<ContactPoint>();

        /// <summary>接触法线（从B指向A）。</summary>
        public PhysicsVector3 Normal;

        /// <summary>物体A的引用索引。</summary>
        public int BodyIndexA;

        /// <summary>物体B的引用索引。</summary>
        public int BodyIndexB;

        /// <summary>创建时间戳。</summary>
        public float Timestamp;

        /// <summary>归一化接触点（限制数量、合并相近点）。</summary>
        public void Normalize()
        {
            if (Points.Count <= 4) return;

            // 保留最大穿透深度的4个点
            Points.Sort((a, b) => b.PenetrationDepth.CompareTo(a.PenetrationDepth));
            int keepCount = Math.Min(4, Points.Count);
            Points.RemoveRange(keepCount, Points.Count - keepCount);
        }

        /// <summary>缩减流形（移除过旧的接触点）。</summary>
        public void PruneOldContacts(float maxAge)
        {
            Points.RemoveAll(p => (Timestamp - p.ContactID) > maxAge * 1000);
        }

        /// <summary>持久化：从旧流形匹配新接触点，保留冲量。</summary>
        public void PersistFrom(ContactManifold oldManifold, float positionTolerance = 0.01f)
        {
            if (oldManifold == null || oldManifold.Points.Count == 0) return;

            foreach (var newPoint in Points)
            {
                float bestDistSq = positionTolerance * positionTolerance;
                ContactPoint bestOld = default;
                bool found = false;

                foreach (var oldPoint in oldManifold.Points)
                {
                    float distSq = (newPoint.Position - oldPoint.Position).LengthSquared();
                    if (distSq < bestDistSq)
                    {
                        bestDistSq = distSq;
                        bestOld = oldPoint;
                        found = true;
                    }
                }

                if (found)
                {
                    newPoint.NormalImpulse = bestOld.NormalImpulse;
                    newPoint.TangentImpulse = bestOld.TangentImpulse;
                }
            }
        }

        /// <summary>是否为空。</summary>
        public bool IsEmpty => Points.Count == 0;
    }

    #endregion

    #region ======================== 序贯冲量求解器 ========================

    /// <summary>序贯冲量求解器配置。</summary>
    public struct SolverSettings
    {
        /// <summary>速度迭代次数（默认8）。</summary>
        public int VelocityIterations;

        /// <summary>位置迭代次数（默认4）。</summary>
        public int PositionIterations;

        /// <summary>Baumgarte系数（位置修正强度，默认0.2）。</summary>
        public float BaumgarteFactor;

        /// <summary>Softness系数（0=硬约束，>0=软约束，默认0）。</summary>
        public float Softness;

        /// <summary>穿透容忍度/Slop（避免微小穿透震荡，默认0.005）。</summary>
        public float Slop;

        /// <summary>速度阈值（低于此值的速度不计恢复系数，默认1.0）。</summary>
        public float RestitutionVelocityThreshold;

        /// <summary>Warmstarting缩放系数（默认0.8）。</summary>
        public float WarmstartFactor;

        public static readonly SolverSettings Default = new SolverSettings
        {
            VelocityIterations = 8,
            PositionIterations = 4,
            BaumgarteFactor = 0.2f,
            Softness = 0.0f,
            Slop = 0.005f,
            RestitutionVelocityThreshold = 1.0f,
            WarmstartFactor = 0.8f,
        };
    }

    /// <summary>刚体引用（由外部物理世界提供）。</summary>
    public interface IRigidBodyProxy
    {
        PhysicsVector3 Position { get; set; }
        PhysicsQuaternion Rotation { get; set; }
        PhysicsVector3 LinearVelocity { get; set; }
        PhysicsVector3 AngularVelocity { get; set; }
        float InvMass { get; }
        PhysicsMatrix3x3 InvInertiaWorld { get; }
        bool IsStatic { get; }
        bool IsKinematic { get; }
        float Friction { get; }
        float Restitution { get; }
        float RollingFriction { get; }
        float LinearDamping { get; }
        float AngularDamping { get; }
        void ApplyImpulse(PhysicsVector3 impulse, PhysicsVector3 contactPoint);
        void ApplyAngularImpulse(PhysicsVector3 impulse);
    }

    /// <summary>序贯冲量求解器。</summary>
    public class SequentialImpulseSolver
    {
        private SolverSettings _settings;

        public SequentialImpulseSolver(SolverSettings settings)
        {
            _settings = settings;
        }

        /// <summary>求解速度约束（所有接触流形）。</summary>
        public void SolveVelocityConstraints(
            List<ContactManifold> manifolds,
            IRigidBodyProxy[] bodies,
            float dt)
        {
            for (int iter = 0; iter < _settings.VelocityIterations; iter++)
            {
                foreach (var manifold in manifolds)
                {
                    if (manifold.IsEmpty) continue;
                    IRigidBodyProxy bodyA = bodies[manifold.BodyIndexA];
                    IRigidBodyProxy bodyB = bodies[manifold.BodyIndexB];

                    for (int ci = 0; ci < manifold.Points.Count; ci++)
                    {
                        SolveContactVelocityConstraint(manifold.Points, ci, bodyA, bodyB, manifold.Normal, dt);
                    }
                }
            }
        }

        /// <summary>求解单个接触点的速度约束。</summary>
        private void SolveContactVelocityConstraint(
            List<ContactPoint> points, int index,
            IRigidBodyProxy bodyA, IRigidBodyProxy bodyB,
            PhysicsVector3 normal, float dt)
        {
            ContactPoint cp = points[index];
            PhysicsVector3 rA = cp.Position - bodyA.Position;
            PhysicsVector3 rB = cp.Position - bodyB.Position;

            PhysicsVector3 velA = bodyA.LinearVelocity + bodyA.AngularVelocity.Cross(rA);
            PhysicsVector3 velB = bodyB.LinearVelocity + bodyB.AngularVelocity.Cross(rB);
            PhysicsVector3 relativeVel = velB - velA;

            float velAlongNormal = relativeVel.Dot(normal);

            // 法线冲量
            float normalImpulse = SolveNormalImpulse(
                velAlongNormal, cp.PenetrationDepth, dt,
                bodyA, bodyB, rA, rB, normal, cp.NormalImpulse);

            cp.NormalImpulse = normalImpulse;

            PhysicsVector3 impulse = normal * normalImpulse;

            // 切线冲量（库仑摩擦）
            PhysicsVector3 tangent = relativeVel - velAlongNormal * normal;
            float tangentLen = tangent.Length();
            PhysicsVector2 tangentImpulse = cp.TangentImpulse;

            if (tangentLen > 1e-6f)
            {
                tangent = tangent / tangentLen;
                float friction = (bodyA.Friction + bodyB.Friction) * 0.5f;

                PhysicsVector3 tangentImpulseVec = SolveFrictionImpulse(
                    tangent, friction, normalImpulse,
                    bodyA, bodyB, rA, rB, tangent, ref tangentImpulse);

                impulse = impulse + tangentImpulseVec;
            }

            // 滚动摩擦
            PhysicsVector3 rollImpulse = SolveRollingFriction(
                normalImpulse, bodyA.RollingFriction, bodyB.RollingFriction,
                bodyA, bodyB, rA, rB, normal);

            impulse = impulse + rollImpulse;

            // 应用冲量
            if (!bodyA.IsStatic && !bodyA.IsKinematic)
            {
                bodyA.LinearVelocity = bodyA.LinearVelocity - impulse * bodyA.InvMass;
                PhysicsVector3 angImpulse = rA.Cross(-impulse);
                bodyA.ApplyAngularImpulse(angImpulse);
            }
            if (!bodyB.IsStatic && !bodyB.IsKinematic)
            {
                bodyB.LinearVelocity = bodyB.LinearVelocity + impulse * bodyB.InvMass;
                PhysicsVector3 angImpulse = rB.Cross(impulse);
                bodyB.ApplyAngularImpulse(angImpulse);
            }

            // 写回修改后的接触点
            cp.TangentImpulse = tangentImpulse;
            points[index] = cp;
        }

        /// <summary>法线冲量求解（含恢复系数和warmstarting）。</summary>
        public float SolveNormalImpulse(
            float relativeVelAlongNormal,
            float penetrationDepth, float dt,
            IRigidBodyProxy bodyA, IRigidBodyProxy bodyB,
            PhysicsVector3 rA, PhysicsVector3 rB,
            PhysicsVector3 normal, float prevImpulse)
        {
            float invMassA = (bodyA.IsStatic || bodyA.IsKinematic) ? 0 : bodyA.InvMass;
            float invMassB = (bodyB.IsStatic || bodyB.IsKinematic) ? 0 : bodyB.InvMass;

            PhysicsVector3 rAxN = rA.Cross(normal);
            PhysicsVector3 rBxN = rB.Cross(normal);
            PhysicsVector3 angA = bodyA.InvInertiaWorld.Multiply(rAxN);
            PhysicsVector3 angB = bodyB.InvInertiaWorld.Multiply(rBxN);

            float rAxN_dot = rAxN.Dot(angA);
            float rBxN_dot = rBxN.Dot(angB);
            float invEffectiveMass = invMassA + invMassB + rAxN_dot + rBxN_dot;

            if (invEffectiveMass <= 1e-12f) return 0;

            float restitution = (bodyA.Restitution + bodyB.Restitution) * 0.5f;

            float velocityThreshold = _settings.RestitutionVelocityThreshold;
            bool applyRestitution = Math.Abs(relativeVelAlongNormal) > velocityThreshold;
            float e = applyRestitution ? restitution : 0;

            float bias = 0;
            float slop = _settings.Slop;
            if (penetrationDepth > slop)
            {
                bias = -_settings.BaumgarteFactor / dt * (penetrationDepth - slop);
            }

            float newImpulse = -(1.0f + e) * relativeVelAlongNormal + bias;
            newImpulse /= invEffectiveMass;

            float oldImpulse = prevImpulse;
            float warmstart = _settings.WarmstartFactor;
            newImpulse = oldImpulse * warmstart + newImpulse * (1.0f - warmstart);

            // 钳制：法线冲量只能为正（推力）
            newImpulse = Math.Max(0, newImpulse);

            return newImpulse;
        }

        /// <summary>切线摩擦冲量求解（库仑摩擦模型，两个切线方向）。</summary>
        public PhysicsVector3 SolveFrictionImpulse(
            PhysicsVector3 tangentDirection,
            float friction,
            float normalImpulse,
            IRigidBodyProxy bodyA, IRigidBodyProxy bodyB,
            PhysicsVector3 rA, PhysicsVector3 rB,
            PhysicsVector3 tangent, ref PhysicsVector2 prevTangentImpulse)
        {
            float invMassA = (bodyA.IsStatic || bodyA.IsKinematic) ? 0 : bodyA.InvMass;
            float invMassB = (bodyB.IsStatic || bodyB.IsKinematic) ? 0 : bodyB.InvMass;

            PhysicsVector3 rAxT = rA.Cross(tangent);
            PhysicsVector3 rBxT = rB.Cross(tangent);
            PhysicsVector3 angA = bodyA.InvInertiaWorld.Multiply(rAxT);
            PhysicsVector3 angB = bodyB.InvInertiaWorld.Multiply(rBxT);

            float invEffectiveMass = invMassA + invMassB + rAxT.Dot(angA) + rBxT.Dot(angB);

            if (invEffectiveMass <= 1e-12f) return PhysicsVector3.Zero;

            PhysicsVector3 relativeVel = (bodyB.LinearVelocity + bodyB.AngularVelocity.Cross(rB))
                                        - (bodyA.LinearVelocity + bodyA.AngularVelocity.Cross(rA));
            float tangentVel = relativeVel.Dot(tangent);

            float impulse = -tangentVel / invEffectiveMass;

            // Warmstart
            impulse = prevTangentImpulse.X * _settings.WarmstartFactor + impulse * (1f - _settings.WarmstartFactor);

            // 库仑摩擦锥钳制
            float maxFriction = friction * normalImpulse;
            impulse = Math.Clamp(impulse, -maxFriction, maxFriction);

            prevTangentImpulse.X = impulse;

            return tangent * impulse;
        }

        /// <summary>滚动摩擦冲量。</summary>
        public PhysicsVector3 SolveRollingFriction(
            float normalImpulse,
            float frictionA, float frictionB,
            IRigidBodyProxy bodyA, IRigidBodyProxy bodyB,
            PhysicsVector3 rA, PhysicsVector3 rB,
            PhysicsVector3 normal)
        {
            PhysicsVector3 angVelA = bodyA.AngularVelocity;
            PhysicsVector3 angVelB = bodyB.AngularVelocity;
            PhysicsVector3 relativeAngVel = angVelB - angVelA;

            float rollFriction = (frictionA + frictionB) * 0.5f * normalImpulse;
            float angSpeed = relativeAngVel.Length();
            if (angSpeed < 1e-6f) return PhysicsVector3.Zero;

            PhysicsVector3 rollImpulse = relativeAngVel * (-rollFriction / angSpeed);
            return rollImpulse;
        }

        /// <summary>阻尼力应用（线性+角速度阻尼）。</summary>
        public void SolveDamping(IRigidBodyProxy body, float dt)
        {
            if (body.IsStatic || body.IsKinematic) return;

            float linearFactor = (float)Math.Pow(1.0f - body.LinearDamping, dt);
            float angularFactor = (float)Math.Pow(1.0f - body.AngularDamping, dt);

            body.LinearVelocity = body.LinearVelocity * linearFactor;
            body.AngularVelocity = body.AngularVelocity * angularFactor;
        }
    }

    #endregion

    #region ======================== 位置约束求解器 ========================

    /// <summary>位置约束求解器。</summary>
    public class PositionConstraintSolver
    {
        private SolverSettings _settings;

        public PositionConstraintSolver(SolverSettings settings)
        {
            _settings = settings;
        }

        /// <summary>求解位置约束（Baumgarte稳定化 + 位置投影）。</summary>
        public void SolvePositionConstraints(
            List<ContactManifold> manifolds,
            IRigidBodyProxy[] bodies,
            float dt)
        {
            for (int iter = 0; iter < _settings.PositionIterations; iter++)
            {
                foreach (var manifold in manifolds)
                {
                    if (manifold.IsEmpty) continue;
                    IRigidBodyProxy bodyA = bodies[manifold.BodyIndexA];
                    IRigidBodyProxy bodyB = bodies[manifold.BodyIndexB];

                    foreach (var cp in manifold.Points)
                    {
                        SolveContactPositionConstraint(
                            cp, bodyA, bodyB, manifold.Normal, dt);
                    }
                }
            }
        }

        /// <summary>单个接触点的位置约束求解。</summary>
        private void SolveContactPositionConstraint(
            ContactPoint cp,
            IRigidBodyProxy bodyA, IRigidBodyProxy bodyB,
            PhysicsVector3 normal, float dt)
        {
            float invMassA = (bodyA.IsStatic || bodyA.IsKinematic) ? 0 : bodyA.InvMass;
            float invMassB = (bodyB.IsStatic || bodyB.IsKinematic) ? 0 : bodyB.InvMass;

            if (invMassA + invMassB <= 1e-12f) return;

            float slop = _settings.Slop;
            float correction = 0;
            if (cp.PenetrationDepth > slop)
            {
                float baumgarte = _settings.BaumgarteFactor;
                correction = Math.Max(cp.PenetrationDepth - slop, 0) * baumgarte / (invMassA + invMassB);

                PhysicsVector3 separation = normal * correction;

                if (!bodyA.IsStatic && !bodyA.IsKinematic)
                    bodyA.Position = bodyA.Position - separation * invMassA;
                if (!bodyB.IsStatic && !bodyB.IsKinematic)
                    bodyB.Position = bodyB.Position + separation * invMassB;
            }
        }

        /// <summary>Baumgarte稳定化：基于穿透深度的位置修正。</summary>
        public static float BaumgarteStabilization(
            float penetration, float slop, float baumgarte, float invMassSum, float dt)
        {
            if (penetration <= slop) return 0;
            return (penetration - slop) * baumgarte / (dt * invMassSum);
        }

        /// <summary>直接位置投影（PBD风格）。</summary>
        public static void ProjectPosition(
            IRigidBodyProxy bodyA, IRigidBodyProxy bodyB,
            PhysicsVector3 contactPoint, PhysicsVector3 normal,
            float targetDistance, float slop)
        {
            PhysicsVector3 diff = (bodyB.Position + normal * targetDistance) - bodyA.Position;
            float dist = diff.Dot(normal);
            if (dist <= slop) return;

            float invMassA = (bodyA.IsStatic || bodyA.IsKinematic) ? 0 : bodyA.InvMass;
            float invMassB = (bodyB.IsStatic || bodyB.IsKinematic) ? 0 : bodyB.InvMass;
            float totalInvMass = invMassA + invMassB;
            if (totalInvMass <= 1e-12f) return;

            float correction = (dist - slop) / totalInvMass;
            PhysicsVector3 correctionVec = normal * correction;

            if (!bodyA.IsStatic && !bodyA.IsKinematic)
                bodyA.Position = bodyA.Position + correctionVec * invMassA;
            if (!bodyB.IsStatic && !bodyB.IsKinematic)
                bodyB.Position = bodyB.Position - correctionVec * invMassB;
        }
    }

    #endregion

    #region ======================== 约束系统 ========================

    /// <summary>
    ///  序贯冲量求解器使用的约束基类。与 Jolt 后端的 <c>Constraint</c> 解耦：
    ///  本基类面向手写求解器，提供速度/位置求解的虚方法钩子与刚体索引。
    /// </summary>
    public abstract class SolverConstraint
    {
        /// <summary>物体A在 bodies 数组中的索引（-1 表示世界/静态）。</summary>
        public int BodyIndexA = -1;
        /// <summary>物体B在 bodies 数组中的索引（-1 表示世界/静态）。</summary>
        public int BodyIndexB = -1;

        /// <summary>在速度求解阶段前初始化缓存（质量矩阵、r 向量等）。默认空实现，子类按需重写。</summary>
        public virtual void InitVelocitySolve(IRigidBodyProxy[] bodies, float dt) { }

        /// <summary>单次速度迭代求解。</summary>
        public abstract void SolveVelocity(IRigidBodyProxy[] bodies, float dt);

        /// <summary>单次位置迭代求解（Baumgarte / 位置投影）。</summary>
        public abstract void SolvePosition(IRigidBodyProxy[] bodies, float dt);

        /// <summary>该约束建议的求解迭代次数（可被求解器覆盖）。</summary>
        public virtual int GetNumSolverIterations() => 10;
    }

    /// <summary>约束基类。</summary>

    /// <summary>点约束（球铰 / 球-套节 / 3DOF约束）。</summary>
    public class PointConstraint : SolverConstraint
    {
        public PhysicsVector3 LocalAnchorA;
        public PhysicsVector3 LocalAnchorB;
        public float BiasFactor = 0.2f;
        public float Softness = 0.0f;
        private PhysicsVector3 _rA, _rB;
        private PhysicsMatrix3x3 _massMatrix;

        public PointConstraint(int bodyA, int bodyB,
            PhysicsVector3 localAnchorA, PhysicsVector3 localAnchorB)
        {
            BodyIndexA = bodyA; BodyIndexB = bodyB;
            LocalAnchorA = localAnchorA; LocalAnchorB = localAnchorB;
        }

        public override void InitVelocitySolve(IRigidBodyProxy[] bodies, float dt)
        {
            var bodyA = bodies[BodyIndexA];
            var bodyB = BodyIndexB >= 0 ? bodies[BodyIndexB] : null;
            _rA = bodyA.Rotation.ToMatrix3x3().Multiply(LocalAnchorA);
            _rB = bodyB != null ? bodyB.Rotation.ToMatrix3x3().Multiply(LocalAnchorB) : PhysicsVector3.Zero;
        }

        public override void SolveVelocity(IRigidBodyProxy[] bodies, float dt)
        {
            var bodyA = bodies[BodyIndexA];
            var bodyB = BodyIndexB >= 0 ? bodies[BodyIndexB] : null;

            PhysicsVector3 cA = bodyA.Position + bodyA.Rotation.ToMatrix3x3().Multiply(LocalAnchorA);
            PhysicsVector3 cB = bodyB != null
                ? bodyB.Position + bodyB.Rotation.ToMatrix3x3().Multiply(LocalAnchorB)
                : PhysicsVector3.Zero;

            PhysicsVector3 diff = cB - cA;
            float invMassA = (bodyA.IsStatic || bodyA.IsKinematic) ? 0 : bodyA.InvMass;
            float invMassB = (bodyB != null && !bodyB.IsStatic && !bodyB.IsKinematic) ? bodyB.InvMass : 0;
            float totalInvMass = invMassA + invMassB;
            if (totalInvMass <= 1e-12f) return;

            PhysicsVector3 correction = diff * (BiasFactor / (dt * totalInvMass + Softness));

            if (!bodyA.IsStatic && !bodyA.IsKinematic)
                bodyA.LinearVelocity = bodyA.LinearVelocity + correction * invMassA;
            if (bodyB != null && !bodyB.IsStatic && !bodyB.IsKinematic)
                bodyB.LinearVelocity = bodyB.LinearVelocity - correction * invMassB;
        }

        public override void SolvePosition(IRigidBodyProxy[] bodies, float dt)
        {
            var bodyA = bodies[BodyIndexA];
            var bodyB = BodyIndexB >= 0 ? bodies[BodyIndexB] : null;

            PhysicsVector3 cA = bodyA.Position + bodyA.Rotation.ToMatrix3x3().Multiply(LocalAnchorA);
            PhysicsVector3 cB = bodyB != null
                ? bodyB.Position + bodyB.Rotation.ToMatrix3x3().Multiply(LocalAnchorB)
                : PhysicsVector3.Zero;

            PhysicsVector3 diff = cB - cA;
            float invMassA = (bodyA.IsStatic || bodyA.IsKinematic) ? 0 : bodyA.InvMass;
            float invMassB = (bodyB != null && !bodyB.IsStatic && !bodyB.IsKinematic) ? bodyB.InvMass : 0;
            float totalInvMass = invMassA + invMassB;
            if (totalInvMass <= 1e-12f) return;

            PhysicsVector3 correction = diff * (0.8f / totalInvMass);

            if (!bodyA.IsStatic && !bodyA.IsKinematic)
                bodyA.Position = bodyA.Position + correction * invMassA;
            if (bodyB != null && !bodyB.IsStatic && !bodyB.IsKinematic)
                bodyB.Position = bodyB.Position - correction * invMassB;
        }
    }

    /// <summary>铰链约束（1旋转轴，5DOF约束）。</summary>
    public class HingeConstraint : SolverConstraint
    {
        public PhysicsVector3 LocalAnchorA;
        public PhysicsVector3 LocalAnchorB;
        public PhysicsVector3 LocalAxisA;
        public PhysicsVector3 LocalAxisB;
        public float MinAngle;
        public float MaxAngle;
        public bool HasAngleLimits;
        public float MotorSpeed;
        public float MaxMotorTorque;
        public bool MotorEnabled;

        private PhysicsMatrix3x3 _matA, _matB;

        public HingeConstraint(int bodyA, int bodyB,
            PhysicsVector3 localAnchorA, PhysicsVector3 localAnchorB,
            PhysicsVector3 localAxisA, PhysicsVector3 localAxisB)
        {
            BodyIndexA = bodyA; BodyIndexB = bodyB;
            LocalAnchorA = localAnchorA; LocalAnchorB = localAnchorB;
            LocalAxisA = localAxisA.Normalized();
            LocalAxisB = localAxisB.Normalized();
            MinAngle = -MathF.PI; MaxAngle = MathF.PI;
            HasAngleLimits = false;
        }

        public override void InitVelocitySolve(IRigidBodyProxy[] bodies, float dt)
        {
            var bodyA = bodies[BodyIndexA];
            var bodyB = bodies[BodyIndexB];
            _matA = bodyA.Rotation.ToMatrix3x3();
            _matB = bodyB.Rotation.ToMatrix3x3();
        }

        public override void SolveVelocity(IRigidBodyProxy[] bodies, float dt)
        {
            var bodyA = bodies[BodyIndexA];
            var bodyB = bodies[BodyIndexB];

            // 点约束部分
            PhysicsVector3 rA = _matA.Multiply(LocalAnchorA);
            PhysicsVector3 rB = _matB.Multiply(LocalAnchorB);
            PhysicsVector3 cA = bodyA.Position + rA;
            PhysicsVector3 cB = bodyB.Position + rB;
            PhysicsVector3 diff = cB - cA;

            float invMassA = (bodyA.IsStatic || bodyA.IsKinematic) ? 0 : bodyA.InvMass;
            float invMassB = (bodyB.IsStatic || bodyB.IsKinematic) ? 0 : bodyB.InvMass;
            float totalInvMass = invMassA + invMassB;
            if (totalInvMass > 1e-12f)
            {
                PhysicsVector3 correction = diff * (0.2f / (dt * totalInvMass));
                if (!bodyA.IsStatic && !bodyA.IsKinematic)
                    bodyA.LinearVelocity = bodyA.LinearVelocity + correction * invMassA;
                if (!bodyB.IsStatic && !bodyB.IsKinematic)
                    bodyB.LinearVelocity = bodyB.LinearVelocity - correction * invMassB;
            }

            // 旋转约束（铰链轴）
            PhysicsVector3 worldAxisA = _matA.Multiply(LocalAxisA);
            PhysicsVector3 worldAxisB = _matB.Multiply(LocalAxisB);
            PhysicsVector3 cross = worldAxisA.Cross(worldAxisB);
            float crossLen = cross.Length();

            if (crossLen > 1e-6f)
            {
                PhysicsVector3 angCorrection = cross * (-2.0f / dt);
                if (!bodyA.IsStatic && !bodyA.IsKinematic)
                    bodyA.ApplyAngularImpulse(angCorrection * invMassA * 0.5f);
                if (!bodyB.IsStatic && !bodyB.IsKinematic)
                    bodyB.ApplyAngularImpulse(angCorrection * invMassB * 0.5f);
            }

            // 角度限制
            if (HasAngleLimits)
            {
                float angle = PhysicsVector3.Angle(worldAxisA, worldAxisB);
                if (angle < MinAngle || angle > MaxAngle)
                {
                    float correctionAngle = angle < MinAngle ? MinAngle - angle : MaxAngle - angle;
                    PhysicsVector3 correctionAxis = crossLen > 1e-6f ? cross / crossLen : worldAxisA.Perpendicular();
                    PhysicsVector3 angCorr = correctionAxis * (correctionAngle * 0.5f / dt);
                    if (!bodyA.IsStatic && !bodyA.IsKinematic)
                        bodyA.ApplyAngularImpulse(angCorr);
                    if (!bodyB.IsStatic && !bodyB.IsKinematic)
                        bodyB.ApplyAngularImpulse(-angCorr);
                }
            }

            // 马达
            if (MotorEnabled && Math.Abs(MaxMotorTorque) > 0)
            {
                PhysicsVector3 targetAngVel = worldAxisA * MotorSpeed;
                PhysicsVector3 angDiff = targetAngVel - bodyB.AngularVelocity;
                float torque = Math.Clamp(angDiff.Length(), -MaxMotorTorque, MaxMotorTorque);
                if (angDiff.Length() > 1e-6f)
                    bodyB.ApplyAngularImpulse(angDiff.Normalized() * torque * dt);
            }
        }

        public override void SolvePosition(IRigidBodyProxy[] bodies, float dt)
        {
            var bodyA = bodies[BodyIndexA];
            var bodyB = bodies[BodyIndexB];

            PhysicsVector3 rA = _matA.Multiply(LocalAnchorA);
            PhysicsVector3 rB = _matB.Multiply(LocalAnchorB);
            PhysicsVector3 cA = bodyA.Position + rA;
            PhysicsVector3 cB = bodyB.Position + rB;
            PhysicsVector3 diff = cB - cA;

            float invMassA = (bodyA.IsStatic || bodyA.IsKinematic) ? 0 : bodyA.InvMass;
            float invMassB = (bodyB.IsStatic || bodyB.IsKinematic) ? 0 : bodyB.InvMass;
            float totalInvMass = invMassA + invMassB;
            if (totalInvMass > 1e-12f)
            {
                PhysicsVector3 correction = diff * (0.8f / totalInvMass);
                if (!bodyA.IsStatic && !bodyA.IsKinematic)
                    bodyA.Position = bodyA.Position + correction * invMassA;
                if (!bodyB.IsStatic && !bodyB.IsKinematic)
                    bodyB.Position = bodyB.Position - correction * invMassB;
            }
        }

        public override int GetNumSolverIterations() => 8;
    }

    /// <summary>滑块约束（1平移轴，5DOF约束）。</summary>
    public class SliderConstraint : SolverConstraint
    {
        public PhysicsVector3 LocalAnchorA;
        public PhysicsVector3 LocalAnchorB;
        public PhysicsVector3 LocalAxis;
        public float MinDistance;
        public float MaxDistance;
        public bool HasLimits;
        public float MotorSpeed;
        public float MaxMotorForce;
        public bool MotorEnabled;

        private PhysicsVector3 _worldAxis;

        public SliderConstraint(int bodyA, int bodyB,
            PhysicsVector3 localAnchorA, PhysicsVector3 localAnchorB,
            PhysicsVector3 localAxis)
        {
            BodyIndexA = bodyA; BodyIndexB = bodyB;
            LocalAnchorA = localAnchorA; LocalAnchorB = localAnchorB;
            LocalAxis = localAxis.Normalized();
            MinDistance = 0; MaxDistance = float.MaxValue; HasLimits = false;
        }

        public override void InitVelocitySolve(IRigidBodyProxy[] bodies, float dt)
        {
            _worldAxis = bodies[BodyIndexA].Rotation.ToMatrix3x3().Multiply(LocalAxis).Normalized();
        }

        public override void SolveVelocity(IRigidBodyProxy[] bodies, float dt)
        {
            var bodyA = bodies[BodyIndexA];
            var bodyB = bodies[BodyIndexB];

            PhysicsVector3 rA = bodyA.Rotation.ToMatrix3x3().Multiply(LocalAnchorA);
            PhysicsVector3 rB = bodyB.Rotation.ToMatrix3x3().Multiply(LocalAnchorB);
            PhysicsVector3 cA = bodyA.Position + rA;
            PhysicsVector3 cB = bodyB.Position + rB;

            // 限制在滑块轴方向上的运动
            PhysicsVector3 diff = cB - cA;
            PhysicsVector3 projected = _worldAxis * diff.Dot(_worldAxis);
            PhysicsVector3 error = diff - projected;

            float invMassA = (bodyA.IsStatic || bodyA.IsKinematic) ? 0 : bodyA.InvMass;
            float invMassB = (bodyB.IsStatic || bodyB.IsKinematic) ? 0 : bodyB.InvMass;
            float totalInvMass = invMassA + invMassB;

            if (totalInvMass > 1e-12f)
            {
                PhysicsVector3 correction = error * (0.2f / (dt * totalInvMass));
                if (!bodyA.IsStatic && !bodyA.IsKinematic)
                    bodyA.LinearVelocity = bodyA.LinearVelocity + correction * invMassA;
                if (!bodyB.IsStatic && !bodyB.IsKinematic)
                    bodyB.LinearVelocity = bodyB.LinearVelocity - correction * invMassB;
            }

            // 距离限制
            if (HasLimits)
            {
                float slideDist = diff.Dot(_worldAxis);
                if (slideDist < MinDistance || slideDist > MaxDistance)
                {
                    float limit = slideDist < MinDistance ? MinDistance - slideDist : MaxDistance - slideDist;
                    PhysicsVector3 limitCorrection = _worldAxis * (limit * 0.2f / dt);
                    if (!bodyA.IsStatic && !bodyA.IsKinematic)
                        bodyA.LinearVelocity = bodyA.LinearVelocity + limitCorrection * invMassA;
                    if (!bodyB.IsStatic && !bodyB.IsKinematic)
                        bodyB.LinearVelocity = bodyB.LinearVelocity - limitCorrection * invMassB;
                }
            }

            // 马达
            if (MotorEnabled && Math.Abs(MaxMotorForce) > 0)
            {
                PhysicsVector3 velDiff = bodyB.LinearVelocity - bodyA.LinearVelocity;
                float velAlongAxis = velDiff.Dot(_worldAxis);
                float force = Math.Clamp((MotorSpeed - velAlongAxis) * 10f, -MaxMotorForce, MaxMotorForce);
                PhysicsVector3 motorImpulse = _worldAxis * force * dt;
                if (!bodyA.IsStatic && !bodyA.IsKinematic)
                    bodyA.LinearVelocity = bodyA.LinearVelocity - motorImpulse * invMassA;
                if (!bodyB.IsStatic && !bodyB.IsKinematic)
                    bodyB.LinearVelocity = bodyB.LinearVelocity + motorImpulse * invMassB;
            }
        }

        public override void SolvePosition(IRigidBodyProxy[] bodies, float dt)
        {
            var bodyA = bodies[BodyIndexA];
            var bodyB = bodies[BodyIndexB];

            PhysicsVector3 rA = bodyA.Rotation.ToMatrix3x3().Multiply(LocalAnchorA);
            PhysicsVector3 rB = bodyB.Rotation.ToMatrix3x3().Multiply(LocalAnchorB);
            PhysicsVector3 cA = bodyA.Position + rA;
            PhysicsVector3 cB = bodyB.Position + rB;
            PhysicsVector3 diff = cB - cA;
            PhysicsVector3 projected = _worldAxis * diff.Dot(_worldAxis);
            PhysicsVector3 error = diff - projected;

            float invMassA = (bodyA.IsStatic || bodyA.IsKinematic) ? 0 : bodyA.InvMass;
            float invMassB = (bodyB.IsStatic || bodyB.IsKinematic) ? 0 : bodyB.InvMass;
            float totalInvMass = invMassA + invMassB;
            if (totalInvMass > 1e-12f)
            {
                PhysicsVector3 correction = error * (0.8f / totalInvMass);
                if (!bodyA.IsStatic && !bodyA.IsKinematic)
                    bodyA.Position = bodyA.Position + correction * invMassA;
                if (!bodyB.IsStatic && !bodyB.IsKinematic)
                    bodyB.Position = bodyB.Position - correction * invMassB;
            }
        }
    }

    /// <summary>锥扭转约束（3DOF旋转约束，摆动锥角+扭转限制）。</summary>
    public class ConeTwistConstraint : SolverConstraint
    {
        public PhysicsVector3 LocalAnchorA;
        public PhysicsVector3 LocalAnchorB;
        public PhysicsVector3 LocalAxisA;
        public PhysicsVector3 LocalAxisB;
        public float SwingSpan;
        public float TwistSpan;
        public float Softness = 0.8f;
        public float BiasFactor = 0.3f;
        public float RelaxationFactor = 1.0f;

        public ConeTwistConstraint(int bodyA, int bodyB,
            PhysicsVector3 localAnchorA, PhysicsVector3 localAnchorB,
            PhysicsVector3 localAxisA, PhysicsVector3 localAxisB,
            float swingSpan, float twistSpan)
        {
            BodyIndexA = bodyA; BodyIndexB = bodyB;
            LocalAnchorA = localAnchorA; LocalAnchorB = localAnchorB;
            LocalAxisA = localAxisA.Normalized();
            LocalAxisB = localAxisB.Normalized();
            SwingSpan = swingSpan; TwistSpan = twistSpan;
        }

        public override void SolveVelocity(IRigidBodyProxy[] bodies, float dt)
        {
            var bodyA = bodies[BodyIndexA];
            var bodyB = bodies[BodyIndexB];

            PhysicsVector3 rA = bodyA.Rotation.ToMatrix3x3().Multiply(LocalAnchorA);
            PhysicsVector3 rB = bodyB.Rotation.ToMatrix3x3().Multiply(LocalAnchorB);

            // 点约束
            PhysicsVector3 cA = bodyA.Position + rA;
            PhysicsVector3 cB = bodyB.Position + rB;
            PhysicsVector3 diff = cB - cA;

            float invMassA = (bodyA.IsStatic || bodyA.IsKinematic) ? 0 : bodyA.InvMass;
            float invMassB = (bodyB.IsStatic || bodyB.IsKinematic) ? 0 : bodyB.InvMass;
            float totalInvMass = invMassA + invMassB;
            if (totalInvMass > 1e-12f)
            {
                PhysicsVector3 correction = diff * (BiasFactor / (dt * totalInvMass));
                if (!bodyA.IsStatic && !bodyA.IsKinematic)
                    bodyA.LinearVelocity = bodyA.LinearVelocity + correction * invMassA;
                if (!bodyB.IsStatic && !bodyB.IsKinematic)
                    bodyB.LinearVelocity = bodyB.LinearVelocity - correction * invMassB;
            }

            // 摆动锥约束
            PhysicsVector3 worldAxisA = bodyA.Rotation.ToMatrix3x3().Multiply(LocalAxisA);
            PhysicsVector3 worldAxisB = bodyB.Rotation.ToMatrix3x3().Multiply(LocalAxisB);
            float angle = PhysicsVector3.Angle(worldAxisA, worldAxisB);

            if (angle > SwingSpan && angle > 1e-6f)
            {
                PhysicsVector3 axis = PhysicsVector3.Axis(worldAxisA, worldAxisB);
                float correctionMag = (angle - SwingSpan) * Softness / dt;
                PhysicsVector3 angCorr = axis * correctionMag;
                if (!bodyA.IsStatic && !bodyA.IsKinematic)
                    bodyA.ApplyAngularImpulse(angCorr * 0.5f);
                if (!bodyB.IsStatic && !bodyB.IsKinematic)
                    bodyB.ApplyAngularImpulse(-angCorr * 0.5f);
            }

            // 扭转限制
            PhysicsVector3 angVelDiff = bodyB.AngularVelocity - bodyA.AngularVelocity;
            float twistVel = angVelDiff.Dot(worldAxisA);
            if (Math.Abs(twistVel) > TwistSpan / dt)
            {
                float correction = Math.Clamp(twistVel, -TwistSpan / dt, TwistSpan / dt) - twistVel;
                PhysicsVector3 twistCorr = worldAxisA * (correction * RelaxationFactor);
                if (!bodyA.IsStatic && !bodyA.IsKinematic)
                    bodyA.ApplyAngularImpulse(twistCorr);
                if (!bodyB.IsStatic && !bodyB.IsKinematic)
                    bodyB.ApplyAngularImpulse(-twistCorr);
            }
        }

        public override void SolvePosition(IRigidBodyProxy[] bodies, float dt)
        {
            var bodyA = bodies[BodyIndexA];
            var bodyB = bodies[BodyIndexB];

            PhysicsVector3 rA = bodyA.Rotation.ToMatrix3x3().Multiply(LocalAnchorA);
            PhysicsVector3 rB = bodyB.Rotation.ToMatrix3x3().Multiply(LocalAnchorB);
            PhysicsVector3 cA = bodyA.Position + rA;
            PhysicsVector3 cB = bodyB.Position + rB;
            PhysicsVector3 diff = cB - cA;

            float invMassA = (bodyA.IsStatic || bodyA.IsKinematic) ? 0 : bodyA.InvMass;
            float invMassB = (bodyB.IsStatic || bodyB.IsKinematic) ? 0 : bodyB.InvMass;
            float totalInvMass = invMassA + invMassB;
            if (totalInvMass > 1e-12f)
            {
                PhysicsVector3 correction = diff * (0.5f / totalInvMass);
                if (!bodyA.IsStatic && !bodyA.IsKinematic)
                    bodyA.Position = bodyA.Position + correction * invMassA;
                if (!bodyB.IsStatic && !bodyB.IsKinematic)
                    bodyB.Position = bodyB.Position - correction * invMassB;
            }
        }
    }

    /// <summary>距离约束。</summary>
    public class DistanceConstraint : SolverConstraint
    {
        public PhysicsVector3 LocalAnchorA;
        public PhysicsVector3 LocalAnchorB;
        public float TargetDistance;
        public float MinDistance = 0;
        public float MaxDistance = float.MaxValue;
        public float Stiffness = 1.0f;
        public float Damping = 0.0f;

        public DistanceConstraint(int bodyA, int bodyB,
            PhysicsVector3 localAnchorA, PhysicsVector3 localAnchorB,
            float targetDistance)
        {
            BodyIndexA = bodyA; BodyIndexB = bodyB;
            LocalAnchorA = localAnchorA; LocalAnchorB = localAnchorB;
            TargetDistance = Math.Max(targetDistance, 0);
        }

        public override void SolveVelocity(IRigidBodyProxy[] bodies, float dt)
        {
            var bodyA = bodies[BodyIndexA];
            var bodyB = bodies[BodyIndexB];

            PhysicsVector3 rA = bodyA.Rotation.ToMatrix3x3().Multiply(LocalAnchorA);
            PhysicsVector3 rB = bodyB.Rotation.ToMatrix3x3().Multiply(LocalAnchorB);
            PhysicsVector3 cA = bodyA.Position + rA;
            PhysicsVector3 cB = bodyB.Position + rB;

            PhysicsVector3 diff = cB - cA;
            float dist = diff.Length();
            if (dist < 1e-6f) return;

            PhysicsVector3 dir = diff / dist;
            float error = dist - TargetDistance;

            // 弹簧力
            float springForce = Stiffness * error - Damping * (bodyB.LinearVelocity - bodyA.LinearVelocity).Dot(dir);

            float invMassA = (bodyA.IsStatic || bodyA.IsKinematic) ? 0 : bodyA.InvMass;
            float invMassB = (bodyB.IsStatic || bodyB.IsKinematic) ? 0 : bodyB.InvMass;
            float totalInvMass = invMassA + invMassB;
            if (totalInvMass <= 1e-12f) return;

            PhysicsVector3 impulse = dir * (springForce / (totalInvMass + Stiffness));

            if (!bodyA.IsStatic && !bodyA.IsKinematic)
                bodyA.LinearVelocity = bodyA.LinearVelocity + impulse * invMassA;
            if (!bodyB.IsStatic && !bodyB.IsKinematic)
                bodyB.LinearVelocity = bodyB.LinearVelocity - impulse * invMassB;
        }

        public override void SolvePosition(IRigidBodyProxy[] bodies, float dt)
        {
            var bodyA = bodies[BodyIndexA];
            var bodyB = bodies[BodyIndexB];

            PhysicsVector3 rA = bodyA.Rotation.ToMatrix3x3().Multiply(LocalAnchorA);
            PhysicsVector3 rB = bodyB.Rotation.ToMatrix3x3().Multiply(LocalAnchorB);
            PhysicsVector3 cA = bodyA.Position + rA;
            PhysicsVector3 cB = bodyB.Position + rB;

            PhysicsVector3 diff = cB - cA;
            float dist = diff.Length();
            if (dist < 1e-6f) return;

            PhysicsVector3 dir = diff / dist;
            float clampedDist = Math.Clamp(dist, MinDistance, MaxDistance);
            float error = dist - clampedDist;

            float invMassA = (bodyA.IsStatic || bodyA.IsKinematic) ? 0 : bodyA.InvMass;
            float invMassB = (bodyB.IsStatic || bodyB.IsKinematic) ? 0 : bodyB.InvMass;
            float totalInvMass = invMassA + invMassB;
            if (totalInvMass <= 1e-12f) return;

            PhysicsVector3 correction = dir * (error * 0.5f / totalInvMass);
            if (!bodyA.IsStatic && !bodyA.IsKinematic)
                bodyA.Position = bodyA.Position + correction * invMassA;
            if (!bodyB.IsStatic && !bodyB.IsKinematic)
                bodyB.Position = bodyB.Position - correction * invMassB;
        }
    }

    /// <summary>6自由度弹簧约束（3线性弹簧 + 3角弹簧）。</summary>
    public class Dof6SpringConstraint : SolverConstraint
    {
        public PhysicsVector3 LocalAnchorA;
        public PhysicsVector3 LocalAnchorB;
        public PhysicsQuaternion LocalRotationA;
        public PhysicsQuaternion LocalRotationB;

        public float LinearStiffnessX = 1, LinearStiffnessY = 1, LinearStiffnessZ = 1;
        public float AngularStiffnessX = 1, AngularStiffnessY = 1, AngularStiffnessZ = 1;
        public float LinearDamping = 0;
        public float AngularDamping = 0;

        public float LinearLowerLimitX = float.MinValue, LinearLowerLimitY = float.MinValue, LinearLowerLimitZ = float.MinValue;
        public float LinearUpperLimitX = float.MaxValue, LinearUpperLimitY = float.MaxValue, LinearUpperLimitZ = float.MaxValue;
        public float AngularLowerLimitX = -MathF.PI, AngularLowerLimitY = -MathF.PI, AngularLowerLimitZ = -MathF.PI;
        public float AngularUpperLimitX = MathF.PI, AngularUpperLimitY = MathF.PI, AngularUpperLimitZ = MathF.PI;

        public Dof6SpringConstraint(int bodyA, int bodyB,
            PhysicsVector3 localAnchorA, PhysicsVector3 localAnchorB,
            PhysicsQuaternion localRotationA, PhysicsQuaternion localRotationB)
        {
            BodyIndexA = bodyA; BodyIndexB = bodyB;
            LocalAnchorA = localAnchorA; LocalAnchorB = localAnchorB;
            LocalRotationA = localRotationA; LocalRotationB = localRotationB;
        }

        public override void SolveVelocity(IRigidBodyProxy[] bodies, float dt)
        {
            var bodyA = bodies[BodyIndexA];
            var bodyB = bodies[BodyIndexB];

            PhysicsVector3 rA = bodyA.Rotation.ToMatrix3x3().Multiply(LocalAnchorA);
            PhysicsVector3 rB = bodyB.Rotation.ToMatrix3x3().Multiply(LocalAnchorB);
            PhysicsVector3 worldPosA = bodyA.Position + rA;
            PhysicsVector3 worldPosB = bodyB.Position + rB;
            PhysicsVector3 diff = worldPosB - worldPosA;

            float invMassA = (bodyA.IsStatic || bodyA.IsKinematic) ? 0 : bodyA.InvMass;
            float invMassB = (bodyB.IsStatic || bodyB.IsKinematic) ? 0 : bodyB.InvMass;
            float totalInvMass = invMassA + invMassB;

            // 线性弹簧
            PhysicsVector3 force = PhysicsVector3.Zero;
            if (totalInvMass > 1e-12f)
            {
                PhysicsVector3 velDiff = bodyB.LinearVelocity - bodyA.LinearVelocity;
                force = new PhysicsVector3(
                    (diff.X * LinearStiffnessX - velDiff.X * LinearDamping),
                    (diff.Y * LinearStiffnessY - velDiff.Y * LinearDamping),
                    (diff.Z * LinearStiffnessZ - velDiff.Z * LinearDamping));

                force.X = Math.Clamp(force.X, LinearLowerLimitX, LinearUpperLimitX);
                force.Y = Math.Clamp(force.Y, LinearLowerLimitY, LinearUpperLimitY);
                force.Z = Math.Clamp(force.Z, LinearLowerLimitZ, LinearUpperLimitZ);

                PhysicsVector3 impulse = force * dt / totalInvMass;
                if (!bodyA.IsStatic && !bodyA.IsKinematic)
                    bodyA.LinearVelocity = bodyA.LinearVelocity + impulse * invMassA;
                if (!bodyB.IsStatic && !bodyB.IsKinematic)
                    bodyB.LinearVelocity = bodyB.LinearVelocity - impulse * invMassB;
            }

            // 角弹簧
            PhysicsQuaternion targetRotB = bodyA.Rotation * (LocalRotationA.Inverse() * LocalRotationB);
            PhysicsQuaternion deltaRot = bodyB.Rotation.Inverse() * targetRotB;
            if (deltaRot.W < 0) deltaRot = new PhysicsQuaternion(-deltaRot.X, -deltaRot.Y, -deltaRot.Z, -deltaRot.W);

            float halfAngle = 2.0f * (float)Math.Atan2(
                (float)Math.Sqrt(deltaRot.X * deltaRot.X + deltaRot.Y * deltaRot.Y + deltaRot.Z * deltaRot.Z),
                deltaRot.W);
            if (Math.Abs(halfAngle) > 1e-6f)
            {
                PhysicsVector3 axis = new PhysicsVector3(deltaRot.X, deltaRot.Y, deltaRot.Z).Normalized();
                PhysicsVector3 angVelDiff = bodyB.AngularVelocity - bodyA.AngularVelocity;
                PhysicsVector3 angForce = axis * (halfAngle * 10.0f) - angVelDiff * AngularDamping;
                angForce = new PhysicsVector3(
                    Math.Clamp(angForce.X * AngularStiffnessX, AngularLowerLimitX, AngularUpperLimitX),
                    Math.Clamp(angForce.Y * AngularStiffnessY, AngularLowerLimitY, AngularUpperLimitY),
                    Math.Clamp(angForce.Z * AngularStiffnessZ, AngularLowerLimitZ, AngularUpperLimitZ));

                if (!bodyA.IsStatic && !bodyA.IsKinematic)
                    bodyA.ApplyAngularImpulse(angForce * dt * 0.5f);
                if (!bodyB.IsStatic && !bodyB.IsKinematic)
                    bodyB.ApplyAngularImpulse(-angForce * dt * 0.5f);
            }

            _accumulatedForce += force.Length();
            CheckBreak();
        }

        public override void SolvePosition(IRigidBodyProxy[] bodies, float dt)
        {
            var bodyA = bodies[BodyIndexA];
            var bodyB = bodies[BodyIndexB];

            PhysicsVector3 rA = bodyA.Rotation.ToMatrix3x3().Multiply(LocalAnchorA);
            PhysicsVector3 rB = bodyB.Rotation.ToMatrix3x3().Multiply(LocalAnchorB);
            PhysicsVector3 worldPosA = bodyA.Position + rA;
            PhysicsVector3 worldPosB = bodyB.Position + rB;
            PhysicsVector3 diff = worldPosB - worldPosA;

            float invMassA = (bodyA.IsStatic || bodyA.IsKinematic) ? 0 : bodyA.InvMass;
            float invMassB = (bodyB.IsStatic || bodyB.IsKinematic) ? 0 : bodyB.InvMass;
            float totalInvMass = invMassA + invMassB;
            if (totalInvMass <= 1e-12f) return;

            PhysicsVector3 correction = diff * (0.5f / totalInvMass);
            if (!bodyA.IsStatic && !bodyA.IsKinematic)
                bodyA.Position = bodyA.Position + correction * invMassA;
            if (!bodyB.IsStatic && !bodyB.IsKinematic)
                bodyB.Position = bodyB.Position - correction * invMassB;
        }

        public override int GetNumSolverIterations() => 8;
    }

    /// <summary>齿轮约束（两个旋转体耦合）。</summary>
    public class GearConstraint : SolverConstraint
    {
        public PhysicsVector3 LocalAxisA;
        public PhysicsVector3 LocalAxisB;
        public float Ratio;
        public float MaxTorque = float.MaxValue;

        public GearConstraint(int bodyA, int bodyB,
            PhysicsVector3 localAxisA, PhysicsVector3 localAxisB, float ratio)
        {
            BodyIndexA = bodyA; BodyIndexB = bodyB;
            LocalAxisA = localAxisA.Normalized();
            LocalAxisB = localAxisB.Normalized();
            Ratio = ratio;
        }

        public override void SolveVelocity(IRigidBodyProxy[] bodies, float dt)
        {
            var bodyA = bodies[BodyIndexA];
            var bodyB = bodies[BodyIndexB];

            PhysicsVector3 worldAxisA = bodyA.Rotation.ToMatrix3x3().Multiply(LocalAxisA);
            PhysicsVector3 worldAxisB = bodyB.Rotation.ToMatrix3x3().Multiply(LocalAxisB);

            float angVelA = bodyA.AngularVelocity.Dot(worldAxisA);
            float angVelB = bodyB.AngularVelocity.Dot(worldAxisB);

            float error = angVelA * Ratio + angVelB;
            float torque = error * 10.0f;
            torque = Math.Clamp(torque, -MaxTorque, MaxTorque);

            if (!bodyA.IsStatic && !bodyA.IsKinematic)
                bodyA.ApplyAngularImpulse(worldAxisA * (-torque * dt));
            if (!bodyB.IsStatic && !bodyB.IsKinematic)
                bodyB.ApplyAngularImpulse(worldAxisB * (torque * dt));
        }

        public override void SolvePosition(IRigidBodyProxy[] bodies, float dt)
        {
            // 齿轮约束通常不需要位置修正（仅速度耦合）
        }
    }

    /// <summary>滑轮约束。</summary>
    public class PulleyConstraint : SolverConstraint
    {
        public PhysicsVector3 WorldAnchorA;
        public PhysicsVector3 WorldAnchorB;
        public PhysicsVector3 LocalAnchorA;
        public PhysicsVector3 LocalAnchorB;
        public float Ratio = 1.0f;
        public float MaxForce = float.MaxValue;

        public PulleyConstraint(int bodyA, int bodyB,
            PhysicsVector3 worldAnchorA, PhysicsVector3 worldAnchorB,
            PhysicsVector3 localAnchorA, PhysicsVector3 localAnchorB,
            float ratio)
        {
            BodyIndexA = bodyA; BodyIndexB = bodyB;
            WorldAnchorA = worldAnchorA; WorldAnchorB = worldAnchorB;
            LocalAnchorA = localAnchorA; LocalAnchorB = localAnchorB;
            Ratio = Math.Max(ratio, 0.01f);
        }

        public override void SolveVelocity(IRigidBodyProxy[] bodies, float dt)
        {
            var bodyA = bodies[BodyIndexA];
            var bodyB = bodies[BodyIndexB];

            PhysicsVector3 rA = bodyA.Rotation.ToMatrix3x3().Multiply(LocalAnchorA);
            PhysicsVector3 rB = bodyB.Rotation.ToMatrix3x3().Multiply(LocalAnchorB);
            PhysicsVector3 cA = bodyA.Position + rA;
            PhysicsVector3 cB = bodyB.Position + rB;

            PhysicsVector3 dirA = WorldAnchorA - cA;
            PhysicsVector3 dirB = WorldAnchorB - cB;
            float lenA = dirA.Length(), lenB = dirB.Length();
            if (lenA < 1e-6f || lenB < 1e-6f) return;
            dirA = dirA / lenA; dirB = dirB / lenB;

            float velA = (bodyA.LinearVelocity + bodyA.AngularVelocity.Cross(rA)).Dot(dirA);
            float velB = (bodyB.LinearVelocity + bodyB.AngularVelocity.Cross(rB)).Dot(dirB);

            float error = velA * Ratio + velB;
            float force = Math.Clamp(error * 5.0f, -MaxForce, MaxForce);

            PhysicsVector3 impulseA = dirA * (force * dt * Ratio);
            PhysicsVector3 impulseB = dirB * (force * dt);

            if (!bodyA.IsStatic && !bodyA.IsKinematic)
            {
                bodyA.LinearVelocity = bodyA.LinearVelocity + impulseA * bodyA.InvMass;
                bodyA.ApplyAngularImpulse(rA.Cross(impulseA));
            }
            if (!bodyB.IsStatic && !bodyB.IsKinematic)
            {
                bodyB.LinearVelocity = bodyB.LinearVelocity - impulseB * bodyB.InvMass;
                bodyB.ApplyAngularImpulse(rB.Cross(-impulseB));
            }
        }

        public override void SolvePosition(IRigidBodyProxy[] bodies, float dt)
        {
            var bodyA = bodies[BodyIndexA];
            var bodyB = bodies[BodyIndexB];

            PhysicsVector3 rA = bodyA.Rotation.ToMatrix3x3().Multiply(LocalAnchorA);
            PhysicsVector3 rB = bodyB.Rotation.ToMatrix3x3().Multiply(LocalAnchorB);
            PhysicsVector3 cA = bodyA.Position + rA;
            PhysicsVector3 cB = bodyB.Position + rB;

            float lenA = (WorldAnchorA - cA).Length();
            float lenB = (WorldAnchorB - cB).Length();
            float totalLen = lenA * Ratio + lenB;

            PhysicsVector3 dirA = (WorldAnchorA - cA); float dLenA = dirA.Length();
            PhysicsVector3 dirB = (WorldAnchorB - cB); float dLenB = dirB.Length();
            if (dLenA < 1e-6f || dLenB < 1e-6f) return;
            dirA = dirA / dLenA; dirB = dirB / dLenB;

            float invMassA = (bodyA.IsStatic || bodyA.IsKinematic) ? 0 : bodyA.InvMass;
            float invMassB = (bodyB.IsStatic || bodyB.IsKinematic) ? 0 : bodyB.InvMass;

            PhysicsVector3 corrA = dirA * (totalLen * Ratio * 0.1f * invMassA);
            PhysicsVector3 corrB = dirB * (totalLen * 0.1f * invMassB);

            if (!bodyA.IsStatic && !bodyA.IsKinematic)
                bodyA.Position = bodyA.Position + corrA;
            if (!bodyB.IsStatic && !bodyB.IsKinematic)
                bodyB.Position = bodyB.Position + corrB;
        }
    }

    /// <summary>接触约束（法线 + 摩擦 + 滚动）。</summary>
    public class ContactConstraint : SolverConstraint
    {
        private ContactManifold _manifold;
        private SequentialImpulseSolver _solver;
        private PositionConstraintSolver _posSolver;

        public ContactConstraint(ContactManifold manifold, SolverSettings settings)
        {
            _manifold = manifold;
            _solver = new SequentialImpulseSolver(settings);
            _posSolver = new PositionConstraintSolver(settings);
        }

        public override void SolveVelocity(IRigidBodyProxy[] bodies, float dt)
        {
            _solver.SolveVelocityConstraints(
                new List<ContactManifold> { _manifold }, bodies, dt);
        }

        public override void SolvePosition(IRigidBodyProxy[] bodies, float dt)
        {
            _posSolver.SolvePositionConstraints(
                new List<ContactManifold> { _manifold }, bodies, dt);
        }

        public override int GetNumSolverIterations() => 8;
    }

    #endregion
}
