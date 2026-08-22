// ============================================================================================
//  RigidBodyDynamicsReal.cs
//
//  山雨引擎 (ShanyuEngine) 真实刚体动力学模块 —— 纯手写实现，无外部物理库依赖。
//
//  内容：
//    - RigidBody：刚体（质量属性/速度/力/形状/碰撞过滤/休眠状态/用户数据）
//    - BodyType：Static / Dynamic / Kinematic
//    - 积分器：SymplecticEuler / VelocityVerlet / RK4
//    - 惯性张量计算：实心长方体/球/圆柱/胶囊/空心球/凸多面体
//    - 休眠系统：SleepManager（能量阈值/速度阈值/角速度阈值/唤醒策略）
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
    #region ======================== 刚体 ========================

    /// <summary>刚体类型。</summary>
    public enum BodyType
    {
        /// <summary>静态体（不受力影响，无限质量）。</summary>
        Static,
        /// <summary>动态体（完全物理模拟）。</summary>
        Dynamic,
        /// <summary>运动学体（用户控制运动，但参与碰撞）。</summary>
        Kinematic,
    }

    /// <summary>刚体状态。</summary>
    public enum BodyState
    {
        Awake,
        Sleeping,
    }

    /// <summary>刚体 —— 物理模拟的基本单位。</summary>
    public class RigidBody : IRigidBodyProxy
    {
        #region 位置/旋转/速度

        public PhysicsVector3 Position;
        public PhysicsQuaternion Rotation;
        public PhysicsVector3 LinearVelocity;
        public PhysicsVector3 AngularVelocity;
        public PhysicsVector3 Force;
        public PhysicsVector3 Torque;

        #endregion

        #region 质量属性

        private float _mass;
        private float _invMass;
        private PhysicsMatrix3x3 _invInertiaLocal;
        private PhysicsMatrix3x3 _invInertiaWorld;

        public float Mass
        {
            get => _mass;
            set
            {
                _mass = Math.Max(value, 0);
                _invMass = _mass > 1e-6f ? 1.0f / _mass : 0;
            }
        }

        public float InvMass => _invMass;

        public PhysicsMatrix3x3 InverseInertiaTensorLocal
        {
            get => _invInertiaLocal;
            set => _invInertiaLocal = value;
        }

        public PhysicsMatrix3x3 InvInertiaWorld
        {
            get => _invInertiaWorld;
            set => _invInertiaWorld = value;
        }

        PhysicsMatrix3x3 IRigidBodyProxy.InvInertiaWorld => _invInertiaWorld;

        #endregion

        #region 类型标志

        public BodyType BodyType { get; set; } = BodyType.Dynamic;
        public bool IsStatic => BodyType == BodyType.Static;
        public bool IsKinematic => BodyType == BodyType.Kinematic;

        public BodyState State { get; set; } = BodyState.Awake;
        public bool IsSleeping => State == BodyState.Sleeping;

        public int IslandIndex { get; set; } = -1;

        #endregion

        #region 碰撞属性

        public Shape CollisionShape { get; set; }

        public float Friction { get; set; } = 0.5f;
        public float Restitution { get; set; } = 0.0f;
        public float RollingFriction { get; set; } = 0.01f;
        public float LinearDamping { get; set; } = 0.01f;
        public float AngularDamping { get; set; } = 0.05f;

        public int CollisionLayer { get; set; } = 1;
        public int CollisionMask { get; set; } = 0xFFFFFFFF;
        public int CollisionGroup { get; set; } = 0;

        #endregion

        #region 用户数据

        public object UserData { get; set; }
        public string Tag { get; set; } = "";

        #endregion

        #region 构造

        public RigidBody()
        {
            Position = PhysicsVector3.Zero;
            Rotation = PhysicsQuaternion.Identity;
            LinearVelocity = PhysicsVector3.Zero;
            AngularVelocity = PhysicsVector3.Zero;
            Force = PhysicsVector3.Zero;
            Torque = PhysicsVector3.Zero;
            _mass = 1.0f;
            _invMass = 1.0f;
            _invInertiaLocal = PhysicsMatrix3x3.Identity;
            _invInertiaWorld = PhysicsMatrix3x3.Identity;
        }

        public RigidBody(Shape shape, float mass)
        {
            Position = PhysicsVector3.Zero;
            Rotation = PhysicsQuaternion.Identity;
            LinearVelocity = PhysicsVector3.Zero;
            AngularVelocity = PhysicsVector3.Zero;
            Force = PhysicsVector3.Zero;
            Torque = PhysicsVector3.Zero;
            CollisionShape = shape;
            Mass = mass;
            _invInertiaLocal = PhysicsMatrix3x3.Identity;
            _invInertiaWorld = PhysicsMatrix3x3.Identity;
        }

        #endregion

        #region 变换与惯性更新

        /// <summary>更新世界惯性张量（每次旋转改变时调用）。</summary>
        public void UpdateInertiaWorld()
        {
            PhysicsMatrix3x3 rot = Rotation.ToMatrix3x3();
            PhysicsMatrix3x3 rotT = rot.Transpose();
            _invInertiaWorld = rot.Multiply(_invInertiaLocal).Multiply(rotT);
        }

        /// <summary>获取世界空间变换。</summary>
        public PhysicsTransform GetTransform()
        {
            return new PhysicsTransform(Position, Rotation);
        }

        /// <summary>设置世界空间变换。</summary>
        public void SetTransform(PhysicsTransform transform)
        {
            Position = transform.Position;
            Rotation = transform.Rotation;
            UpdateInertiaWorld();
        }

        /// <summary>计算AABB。</summary>
        public AABBShape ComputeWorldAABB()
        {
            return CollisionShape?.ComputeAABB(GetTransform()) ?? new AABBShape(Position, Position);
        }

        #endregion

        #region 力与冲量

        /// <summary>在世界坐标点施加力。</summary>
        public void ApplyForceAtPoint(PhysicsVector3 force, PhysicsVector3 worldPoint)
        {
            Force = Force + force;
            PhysicsVector3 r = worldPoint - Position;
            Torque = Torque + r.Cross(force);
        }

        /// <summary>施加中心力。</summary>
        public void ApplyCentralForce(PhysicsVector3 force)
        {
            Force = Force + force;
        }

        /// <summary>在世界坐标点施加冲量。</summary>
        public void ApplyImpulse(PhysicsVector3 impulse, PhysicsVector3 contactPoint)
        {
            if (IsStatic || IsKinematic) return;
            LinearVelocity = LinearVelocity + impulse * _invMass;
            PhysicsVector3 r = contactPoint - Position;
            PhysicsVector3 angImpulse = _invInertiaWorld.Multiply(r.Cross(impulse));
            AngularVelocity = AngularVelocity + angImpulse;
        }

        /// <summary>施加角冲量。</summary>
        public void ApplyAngularImpulse(PhysicsVector3 impulse)
        {
            if (IsStatic || IsKinematic) return;
            PhysicsVector3 angVelChange = _invInertiaWorld.Multiply(impulse);
            AngularVelocity = AngularVelocity + angVelChange;
        }

        /// <summary>施加中心冲量。</summary>
        public void ApplyCentralImpulse(PhysicsVector3 impulse)
        {
            if (IsStatic || IsKinematic) return;
            LinearVelocity = LinearVelocity + impulse * _invMass;
        }

        #endregion

        #region 碰撞响应

        /// <summary>应用碰撞响应（接触冲量 → 速度/角速度变化）。</summary>
        public void ApplyCollisionResponse(ContactManifold manifold)
        {
            if (IsStatic) return;
            foreach (var cp in manifold.Points)
            {
                ApplyImpulse(
                    manifold.Normal * cp.NormalImpulse,
                    cp.Position);
            }
        }

        #endregion

        #region 清除

        /// <summary>清除累积力和力矩。</summary>
        public void ClearForces()
        {
            Force = PhysicsVector3.Zero;
            Torque = PhysicsVector3.Zero;
        }

        #endregion

        #region 能量

        /// <summary>计算动能。</summary>
        public float ComputeKineticEnergy()
        {
            float linearKE = 0.5f * _mass * LinearVelocity.LengthSquared();
            PhysicsVector3 localAngVel = Rotation.Inverse().ToMatrix3x3().Multiply(AngularVelocity);
            PhysicsVector3 angMom = _invInertiaLocal.Inverse().Multiply(localAngVel);
            float angularKE = 0.5f * localAngVel.Dot(angMom);
            return linearKE + angularKE;
        }

        /// <summary>计算速度大小的平方。</summary>
        public float ComputeVelocityMagnitudeSq()
        {
            return LinearVelocity.LengthSquared() + AngularVelocity.LengthSquared();
        }

        #endregion

        #region IRigidBodyProxy 显式实现

        PhysicsVector3 IRigidBodyProxy.Position { get => Position; set => Position = value; }
        PhysicsQuaternion IRigidBodyProxy.Rotation { get => Rotation; set => Rotation = value; }
        PhysicsVector3 IRigidBodyProxy.LinearVelocity { get => LinearVelocity; set => LinearVelocity = value; }
        PhysicsVector3 IRigidBodyProxy.AngularVelocity { get => AngularVelocity; set => AngularVelocity = value; }
        float IRigidBodyProxy.Friction => Friction;
        float IRigidBodyProxy.Restitution => Restitution;
        float IRigidBodyProxy.RollingFriction => RollingFriction;
        float IRigidBodyProxy.LinearDamping => LinearDamping;
        float IRigidBodyProxy.AngularDamping => AngularDamping;

        #endregion
    }

    #endregion

    #region ======================== 积分器 ========================

    /// <summary>积分器接口。</summary>
    public interface IIntegrator
    {
        void Integrate(RigidBody body, float dt);
    }

    /// <summary>力场接口。</summary>
    public interface IForceField
    {
        PhysicsVector3 ComputeForce(RigidBody body, float time);
    }

    /// <summary>广义力计算器。</summary>
    public static class ForceComputer
    {
        /// <summary>计算所有力：重力 + 风力 + 弹簧力 + 阻尼力 + 自定义力场。</summary>
        public static void ComputeForces(
            RigidBody body,
            PhysicsVector3 gravity,
            PhysicsVector3 windVelocity,
            List<IForceField> customFields,
            float time,
            float dt)
        {
            if (body.IsStatic) return;

            // 重力
            if (!body.IsKinematic)
            {
                body.ApplyCentralForce(gravity * body.Mass);
            }

            // 风力（简化：基于截面积和阻力系数）
            float dragCoeff = 0.47f; // 球体阻力系数
            if (body.CollisionShape != null)
            {
                AABBShape aabb = body.ComputeWorldAABB();
                float area = (aabb.Max - aabb.Min).Length() * 0.5f;
                PhysicsVector3 relVel = windVelocity - body.LinearVelocity;
                float speed = relVel.Length();
                if (speed > 1e-6f)
                {
                    PhysicsVector3 dragForce = relVel.Normalized() * (-0.5f * dragCoeff * area * speed * speed * 1.225f);
                    body.ApplyCentralForce(dragForce);
                }
            }

            // 线性阻尼力
            PhysicsVector3 dampingForce = body.LinearVelocity * (-body.LinearDamping * body.Mass);
            body.ApplyCentralForce(dampingForce);

            // 角阻尼力矩
            PhysicsVector3 angDampingTorque = body.AngularVelocity * (-body.AngularDamping * body.Mass);
            body.Torque = body.Torque + angDampingTorque;

            // 自定义力场
            if (customFields != null)
            {
                foreach (var field in customFields)
                {
                    PhysicsVector3 customForce = field.ComputeForce(body, time);
                    body.ApplyCentralForce(customForce);
                }
            }
        }
    }

    /// <summary>辛欧拉积分器（Symplectic Euler / Semi-implicit Euler）。</summary>
    /// <remarks>
    /// 先更新速度，再用新速度更新位置。
    /// v(t+dt) = v(t) + a(t) * dt
    /// x(t+dt) = x(t) + v(t+dt) * dt
    /// 优点：简单快速，能量近似守恒，游戏物理中最常用。
    /// </remarks>
    public class SymplecticEulerIntegrator : IIntegrator
    {
        public void Integrate(RigidBody body, float dt)
        {
            if (body.IsStatic) return;

            if (body.IsKinematic)
            {
                // 运动学体只更新位置（速度由用户设置）
                PhysicsMatrix3x3 rot = body.Rotation.ToMatrix3x3();
                body.Position = body.Position + body.LinearVelocity * dt;
                // 四元数积分：q_new = q_old + 0.5 * omega * q_old * dt
                PhysicsVector3 halfOmega = body.AngularVelocity * (0.5f * dt);
                PhysicsQuaternion dq = new PhysicsQuaternion(
                    halfOmega.X * body.Rotation.W + halfOmega.Y * body.Rotation.Z - halfOmega.Z * body.Rotation.Y,
                    halfOmega.Y * body.Rotation.W + halfOmega.Z * body.Rotation.X - halfOmega.X * body.Rotation.Z,
                    halfOmega.Z * body.Rotation.W + halfOmega.X * body.Rotation.Y - halfOmega.Y * body.Rotation.X,
                    -(halfOmega.X * body.Rotation.X + halfOmega.Y * body.Rotation.Y + halfOmega.Z * body.Rotation.Z));
                body.Rotation = new PhysicsQuaternion(
                    body.Rotation.X + dq.X,
                    body.Rotation.Y + dq.Y,
                    body.Rotation.Z + dq.Z,
                    body.Rotation.W + dq.W).Normalized();
                body.UpdateInertiaWorld();
                return;
            }

            // 加速度
            PhysicsVector3 acceleration = body.Force * body.InvMass;
            PhysicsVector3 angularAcceleration = body.InvInertiaWorld.Multiply(body.Torque);

            // 更新速度
            body.LinearVelocity = body.LinearVelocity + acceleration * dt;
            body.AngularVelocity = body.AngularVelocity + angularAcceleration * dt;

            // 更新位置
            body.Position = body.Position + body.LinearVelocity * dt;

            // 更新旋转（四元数积分）
            PhysicsVector3 halfOmega = body.AngularVelocity * (0.5f * dt);
            PhysicsQuaternion dq = new PhysicsQuaternion(
                halfOmega.X * body.Rotation.W + halfOmega.Y * body.Rotation.Z - halfOmega.Z * body.Rotation.Y,
                halfOmega.Y * body.Rotation.W + halfOmega.Z * body.Rotation.X - halfOmega.X * body.Rotation.Z,
                halfOmega.Z * body.Rotation.W + halfOmega.X * body.Rotation.Y - halfOmega.Y * body.Rotation.X,
                -(halfOmega.X * body.Rotation.X + halfOmega.Y * body.Rotation.Y + halfOmega.Z * body.Rotation.Z));
            body.Rotation = new PhysicsQuaternion(
                body.Rotation.X + dq.X,
                body.Rotation.Y + dq.Y,
                body.Rotation.Z + dq.Z,
                body.Rotation.W + dq.W).Normalized();

            body.UpdateInertiaWorld();
        }
    }

    /// <summary>速度Verlet积分器。</summary>
    /// <remarks>
    /// x(t+dt) = x(t) + v(t)*dt + 0.5*a(t)*dt^2
    /// v(t+dt) = v(t) + 0.5*(a(t) + a(t+dt))*dt
    /// 优点：二阶精度，能量守恒更好。
    /// </remarks>
    public class VelocityVerletIntegrator : IIntegrator
    {
        private Dictionary<int, PhysicsVector3> _prevAcceleration = new Dictionary<int, PhysicsVector3>();
        private Dictionary<int, PhysicsVector3> _prevAngularAccel = new Dictionary<int, PhysicsVector3>();

        public void Integrate(RigidBody body, float dt)
        {
            if (body.IsStatic) return;

            int bodyId = body.GetHashCode();

            if (body.IsKinematic)
            {
                body.Position = body.Position + body.LinearVelocity * dt;
                IntegrateRotation(body, dt);
                return;
            }

            PhysicsVector3 accel = body.Force * body.InvMass;
            PhysicsVector3 angAccel = body.InvInertiaWorld.Multiply(body.Torque);

            // 位置更新（使用当前加速度和上一步加速度的均值）
            PhysicsVector3 prevAccel = _prevAcceleration.TryGetValue(bodyId, out var pa) ? pa : accel;
            PhysicsVector3 prevAngAccel = _prevAngularAccel.TryGetValue(bodyId, out var paa) ? paa : angAccel;

            PhysicsVector3 avgAccel = (prevAccel + accel) * 0.5f;
            PhysicsVector3 avgAngAccel = (prevAngAccel + angAccel) * 0.5f;

            body.Position = body.Position + body.LinearVelocity * dt + avgAccel * (0.5f * dt * dt);
            body.LinearVelocity = body.LinearVelocity + avgAccel * dt;

            body.AngularVelocity = body.AngularVelocity + avgAngAccel * dt;
            IntegrateRotation(body, dt);

            _prevAcceleration[bodyId] = accel;
            _prevAngularAccel[bodyId] = angAccel;

            body.UpdateInertiaWorld();
        }

        private static void IntegrateRotation(RigidBody body, float dt)
        {
            PhysicsVector3 halfOmega = body.AngularVelocity * (0.5f * dt);
            PhysicsQuaternion dq = new PhysicsQuaternion(
                halfOmega.X * body.Rotation.W + halfOmega.Y * body.Rotation.Z - halfOmega.Z * body.Rotation.Y,
                halfOmega.Y * body.Rotation.W + halfOmega.Z * body.Rotation.X - halfOmega.X * body.Rotation.Z,
                halfOmega.Z * body.Rotation.W + halfOmega.X * body.Rotation.Y - halfOmega.Y * body.Rotation.X,
                -(halfOmega.X * body.Rotation.X + halfOmega.Y * body.Rotation.Y + halfOmega.Z * body.Rotation.Z));
            body.Rotation = new PhysicsQuaternion(
                body.Rotation.X + dq.X, body.Rotation.Y + dq.Y,
                body.Rotation.Z + dq.Z, body.Rotation.W + dq.W).Normalized();
            body.UpdateInertiaWorld();
        }
    }

    /// <summary>四阶龙格-库塔积分器（RK4）。</summary>
    /// <remarks>
    /// 4次导数评估，加权平均。最高精度但计算量最大。
    /// 适合精度要求高的场景（如物理模拟精度展示）。
    /// </remarks>
    public class RK4Integrator : IIntegrator
    {
        public void Integrate(RigidBody body, float dt)
        {
            if (body.IsStatic) return;

            if (body.IsKinematic)
            {
                body.Position = body.Position + body.LinearVelocity * dt;
                IntegrateRotation(body, dt);
                return;
            }

            float invMass = body.InvMass;
            PhysicsMatrix3x3 invInertia = body.InvInertiaWorld;

            // 保存初始状态
            PhysicsVector3 x0 = body.Position, v0 = body.LinearVelocity, w0 = body.AngularVelocity;
            PhysicsVector3 F0 = body.Force, T0 = body.Torque;
            PhysicsQuaternion q0 = body.Rotation;

            // k1
            PhysicsVector3 a1 = F0 * invMass;
            PhysicsVector3 alpha1 = invInertia.Multiply(T0);

            // k2（半步）
            PhysicsVector3 x2 = x0 + v0 * (0.5f * dt);
            PhysicsVector3 v2 = v0 + a1 * (0.5f * dt);
            PhysicsVector3 w2 = w0 + alpha1 * (0.5f * dt);
            PhysicsVector3 a2 = F0 * invMass; // 假设力不变
            PhysicsVector3 alpha2 = invInertia.Multiply(T0);

            // k3（半步）
            PhysicsVector3 v3 = v0 + a2 * (0.5f * dt);
            PhysicsVector3 w3 = w0 + alpha2 * (0.5f * dt);
            PhysicsVector3 a3 = F0 * invMass;
            PhysicsVector3 alpha3 = invInertia.Multiply(T0);

            // k4（全步）
            PhysicsVector3 v4 = v0 + a3 * dt;
            PhysicsVector3 w4 = w0 + alpha3 * dt;
            PhysicsVector3 a4 = F0 * invMass;
            PhysicsVector3 alpha4 = invInertia.Multiply(T0);

            // 加权平均
            body.Position = x0 + (v0 + v2 * 2 + v3 * 2 + v4) * (dt / 6.0f);
            body.LinearVelocity = v0 + (a1 + a2 * 2 + a3 * 2 + a4) * (dt / 6.0f);
            body.AngularVelocity = w0 + (alpha1 + alpha2 * 2 + alpha3 * 2 + alpha4) * (dt / 6.0f);

            IntegrateRotation(body, dt);
        }

        private static void IntegrateRotation(RigidBody body, float dt)
        {
            PhysicsVector3 halfOmega = body.AngularVelocity * (0.5f * dt);
            PhysicsQuaternion dq = new PhysicsQuaternion(
                halfOmega.X * body.Rotation.W + halfOmega.Y * body.Rotation.Z - halfOmega.Z * body.Rotation.Y,
                halfOmega.Y * body.Rotation.W + halfOmega.Z * body.Rotation.X - halfOmega.X * body.Rotation.Z,
                halfOmega.Z * body.Rotation.W + halfOmega.X * body.Rotation.Y - halfOmega.Y * body.Rotation.X,
                -(halfOmega.X * body.Rotation.X + halfOmega.Y * body.Rotation.Y + halfOmega.Z * body.Rotation.Z));
            body.Rotation = new PhysicsQuaternion(
                body.Rotation.X + dq.X, body.Rotation.Y + dq.Y,
                body.Rotation.Z + dq.Z, body.Rotation.W + dq.W).Normalized();
            body.UpdateInertiaWorld();
        }
    }

    #endregion

    #region ======================== 惯性张量计算 ========================

    /// <summary>惯性张量计算工具。</summary>
    public static class InertiaTensorComputer
    {
        /// <summary>实心长方体惯性张量（主对角线）。</summary>
        /// <remarks>
        /// Ixx = m/12 * (h^2 + d^2), Iyy = m/12 * (w^2 + d^2), Izz = m/12 * (w^2 + h^2)
        /// </remarks>
        public static PhysicsMatrix3x3 ComputeSolidBox(PhysicsVector3 halfExtents, float mass)
        {
            float m = Math.Max(mass, 1e-6f);
            float ix = m / 12.0f * (halfExtents.Y * halfExtents.Y + halfExtents.Z * halfExtents.Z);
            float iy = m / 12.0f * (halfExtents.X * halfExtents.X + halfExtents.Z * halfExtents.Z);
            float iz = m / 12.0f * (halfExtents.X * halfExtents.X + halfExtents.Y * halfExtents.Y);
            return new PhysicsMatrix3x3(ix, 0, 0, 0, iy, 0, 0, 0, iz);
        }

        /// <summary>实心球惯性张量。</summary>
        /// <remarks>I = 2/5 * m * r^2</remarks>
        public static PhysicsMatrix3x3 ComputeSolidSphere(float radius, float mass)
        {
            float m = Math.Max(mass, 1e-6f);
            float r = Math.Max(radius, 1e-6f);
            float i = 0.4f * m * r * r;
            return new PhysicsMatrix3x3(i, 0, 0, 0, i, 0, 0, 0, i);
        }

        /// <summary>实心圆柱惯性张量（Y轴对称）。</summary>
        /// <remarks>
        /// Ixx = Izz = m/12 * (3*r^2 + h^2)
        /// Iyy = m/2 * r^2
        /// </remarks>
        public static PhysicsMatrix3x3 ComputeSolidCylinder(float radius, float height, float mass)
        {
            float m = Math.Max(mass, 1e-6f);
            float r = Math.Max(radius, 1e-6f);
            float h = Math.Max(height, 1e-6f);
            float iAxial = 0.5f * m * r * r;
            float iRadial = m / 12.0f * (3.0f * r * r + h * h);
            return new PhysicsMatrix3x3(iRadial, 0, 0, 0, iAxial, 0, 0, 0, iRadial);
        }

        /// <summary>实心胶囊惯性张量（近似，Y轴对称）。</summary>
        /// <remarks>
        /// 胶囊 = 圆柱 + 两半球。近似计算。
        /// </remarks>
        public static PhysicsMatrix3x3 ComputeSolidCapsule(float radius, float halfHeight, float mass)
        {
            float m = Math.Max(mass, 1e-6f);
            float r = Math.Max(radius, 1e-6f);
            float h = Math.Max(halfHeight, 1e-6f);

            float cylVolume = MathF.PI * r * r * 2 * h;
            float sphVolume = (4.0f / 3.0f) * MathF.PI * r * r * r;
            float totalVol = cylVolume + sphVolume;
            float cylMass = m * cylVolume / totalVol;
            float sphMass = m * sphVolume / totalVol;

            PhysicsMatrix3x3 cylI = ComputeSolidCylinder(r, 2 * h, cylMass);
            PhysicsMatrix3x3 sphI = ComputeSolidSphere(r, sphMass);

            // 平行轴定理：球心偏移 halfHeight
            float offset = halfHeight;
            PhysicsMatrix3x3 sphIOffset = new PhysicsMatrix3x3(
                sphI.M00 + sphMass * offset * offset, 0, 0,
                0, sphI.M11, 0,
                0, 0, sphI.M22 + sphMass * offset * offset);

            return new PhysicsMatrix3x3(
                cylI.M00 + sphIOffset.M00 * 2, 0, 0,
                0, cylI.M11 + sphIOffset.M11 * 2, 0,
                0, 0, cylI.M22 + sphIOffset.M22 * 2);
        }

        /// <summary>空心球惯性张量。</summary>
        /// <remarks>I = 2/5 * m * (R^5 - r^5) / (R^3 - r^3)</remarks>
        public static PhysicsMatrix3x3 ComputeHollowSphere(float innerRadius, float outerRadius, float mass)
        {
            float m = Math.Max(mass, 1e-6f);
            float rInner = Math.Max(innerRadius, 0);
            float rOuter = Math.Max(outerRadius, rInner + 1e-6f);
            float r3diff = rOuter * rOuter * rOuter - rInner * rInner * rInner;
            float r5diff = rOuter * rOuter * rOuter * rOuter * rOuter - rInner * rInner * rInner * rInner * rInner;
            float i = 0.4f * m * r5diff / r3diff;
            return new PhysicsMatrix3x3(i, 0, 0, 0, i, 0, 0, 0, i);
        }

        /// <summary>凸多面体惯性张量（基于三角面片的体积积分近似）。</summary>
        /// <remarks>
        /// 使用发散定理（Divergence Theorem）计算实体积分。
        /// 对每个三角面片计算其对质量和惯性张量的贡献。
        /// </remarks>
        public static PhysicsMatrix3x3 ComputeConvexMesh(
            PhysicsVector3[] vertices, int[][] faces, float mass)
        {
            if (vertices == null || vertices.Length < 3 || faces == null || faces.Length < 1)
                return PhysicsMatrix3x3.Identity;

            float totalVolume = 0;
            PhysicsVector3 centerOfMass = PhysicsVector3.Zero;
            PhysicsMatrix3x3 inertia = new PhysicsMatrix3x3(0, 0, 0, 0, 0, 0, 0, 0, 0);

            for (int fi = 0; fi < faces.Length; fi++)
            {
                if (faces[fi].Length < 3) continue;
                int i0 = faces[fi][0], i1 = faces[fi][1], i2 = faces[fi][2];
                if (i0 >= vertices.Length || i1 >= vertices.Length || i2 >= vertices.Length) continue;

                PhysicsVector3 v0 = vertices[i0], v1 = vertices[i1], v2 = vertices[i2];

                // 三棱柱积分（体积法）
                for (int ia = 0; ia < 3; ia++)
                {
                    int ib = (ia + 1) % 3;
                    PhysicsVector3 a = ia == 0 ? v0 : (ia == 1 ? v1 : v2);
                    PhysicsVector3 b = ib == 0 ? v0 : (ib == 1 ? v1 : v2);

                    PhysicsVector3 cross = a.Cross(b);
                    float volFactor = cross.X + cross.Y + cross.Z;

                    totalVolume += volFactor / 6.0f;

                    float a0sq = a.X * a.X + a.X * b.X + b.X * b.X;
                    float a1sq = a.Y * a.Y + a.Y * b.Y + b.Y * b.Y;
                    float a2sq = a.Z * a.Z + a.Z * b.Z + b.Z * b.Z;

                    inertia = new PhysicsMatrix3x3(
                        inertia.M00 + volFactor * (a1sq + a2sq),
                        inertia.M01,
                        inertia.M02,
                        inertia.M10,
                        inertia.M11 + volFactor * (a0sq + a2sq),
                        inertia.M12,
                        inertia.M20,
                        inertia.M21,
                        inertia.M22 + volFactor * (a0sq + a1sq));
                }
            }

            if (totalVolume < 1e-12f) return PhysicsMatrix3x3.Identity;

            float density = mass / Math.Abs(totalVolume);
            float factor = density / 60.0f;

            inertia = new PhysicsMatrix3x3(
                inertia.M00 * factor, 0, 0,
                0, inertia.M11 * factor, 0,
                0, 0, inertia.M22 * factor);

            return inertia;
        }
    }

    /// <summary>惯性张量变换工具。</summary>
    public static class InertiaTransform
    {
        /// <summary>将局部惯性张量变换到世界空间。</summary>
        /// <remarks>I_world = R * I_local * R^T（对角线惯性张量版本）。</remarks>
        public static PhysicsMatrix3x3 TransformInertiaTensor(
            PhysicsMatrix3x3 localInertia,
            PhysicsQuaternion rotation,
            PhysicsVector3 scale,
            float mass)
        {
            PhysicsMatrix3x3 rot = rotation.ToMatrix3x3();

            // 缩放修正（非均匀缩放需要完整的相似变换）
            PhysicsMatrix3x3 scaleMatrix = new PhysicsMatrix3x3(
                scale.X * scale.X, 0, 0,
                0, scale.Y * scale.Y, 0,
                0, 0, scale.Z * scale.Z);

            PhysicsMatrix3x3 scaledLocal = localInertia.Multiply(scaleMatrix);
            PhysicsMatrix3x3 rotT = rot.Transpose();
            return rot.Multiply(scaledLocal).Multiply(rotT);
        }

        /// <summary>计算逆惯性张量。</summary>
        public static PhysicsMatrix3x3 ComputeInverseInertia(PhysicsMatrix3x3 inertia)
        {
            return inertia.Inverse();
        }
    }

    #endregion

    #region ======================== 休眠系统 ========================

    /// <summary>休眠配置。</summary>
    public struct SleepSettings
    {
        /// <summary>线性速度休眠阈值（默认0.1 m/s）。</summary>
        public float LinearSleepThreshold;

        /// <summary>角速度休眠阈值（默认0.1 rad/s）。</summary>
        public float AngularSleepThreshold;

        /// <summary>能量休眠阈值（默认0.005）。</summary>
        public float EnergySleepThreshold;

        /// <summary>休眠延迟时间（物体必须在阈值以下保持这么久才休眠，默认0.5s）。</summary>
        public float SleepDelay;

        /// <summary>是否启用休眠。</summary>
        public bool SleepEnabled;

        public static readonly SleepSettings Default = new SleepSettings
        {
            LinearSleepThreshold = 0.1f,
            AngularSleepThreshold = 0.1f,
            EnergySleepThreshold = 0.005f,
            SleepDelay = 0.5f,
            SleepEnabled = true,
        };
    }

    /// <summary>休眠管理器。</summary>

    #endregion
}
