// ============================================================================================
//  ConstraintSystem.cs
//
//  yama引擎 (YamaEngine) 关节约束系统 —— 纯手写实现，基于 Sequential Impulse 求解器。
//
//  内容：
//    - JointType：关节类型枚举（Distance/Hinge/Slider/Spring/Fixed/ConeTwist/Spherical/Point/BallSocket）
//    - PhysicsJoint：关节基类（断裂力/碰撞启用/锚点）
//    - DistanceJoint：距离关节（弹簧阻尼）
//    - HingeJoint：铰链关节（单轴旋转 + 角度限制 + 马达）
//    - SliderJoint：滑动关节（单轴平移 + 位置限制 + 马达）
//    - SpringJoint：弹簧关节（Hooke定律）
//    - FixedJoint：固定关节（保持相对位姿）
//    - ConeTwistJoint：锥形扭转关节（布娃娃肩关节等）
//    - BallSocketJoint：球窝关节（3D万向旋转，仅约束位置）
//    - SphericalJoint：球关节（球窝别名）
//    - JacobianSolver：Jacobian构建 + Sequential Impulse + Warm Starting
//
//  命名空间：YamaEngine.Physics
//  目标框架：net8.0 / C# 8.0+
//  外部依赖：仅 System.Numerics
// ============================================================================================

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace YamaEngine.Physics
{
    #region ======================== 关节类型枚举 ========================

    /// <summary>关节类型。</summary>
    public enum JointType
    {
        /// <summary>距离关节：保持两点距离在范围内。</summary>
        Distance,
        /// <summary>铰链关节：单轴旋转。</summary>
        Hinge,
        /// <summary>滑动关节：单轴平移。</summary>
        Slider,
        /// <summary>弹簧关节：弹性连接。</summary>
        Spring,
        /// <summary>固定关节：焊接两个刚体。</summary>
        Fixed,
        /// <summary>锥形扭转关节：限制摆动和扭转范围（如肩膀）。</summary>
        ConeTwist,
        /// <summary>球关节：自由旋转，约束位置。</summary>
        Spherical,
        /// <summary>点关节（Point，等同于球窝）。</summary>
        Point,
        /// <summary>球窝关节：3D万向旋转。</summary>
        BallSocket,
    }

    #endregion

    #region ======================== 关节基类 ========================

    /// <summary>
    ///   物理关节基类。连接两个刚体并限制它们的相对运动。
    /// </summary>
    public abstract class PhysicsJoint
    {
        /// <summary>关节类型。</summary>
        public JointType Type { get; }

        /// <summary>刚体A引用（可为null，表示世界固定点）。</summary>
        public RigidBody BodyA { get; }

        /// <summary>刚体B引用（可为null，表示世界固定点）。</summary>
        public RigidBody BodyB { get; }

        /// <summary>Body A本地空间锚点。</summary>
        public PhysicsVector3 LocalAnchorA { get; set; }

        /// <summary>Body B本地空间锚点。</summary>
        public PhysicsVector3 LocalAnchorB { get; set; }

        /// <summary>断裂力阈值。超过此力时关节断开（0 = 不断裂）。</summary>
        public float BreakForce { get; set; }

        /// <summary>断裂力矩阈值。超过此力矩时关节断开（0 = 不断裂）。</summary>
        public float BreakTorque { get; set; }

        /// <summary>关节是否启用。</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>连接的刚体之间是否参与碰撞检测。</summary>
        public bool CollisionEnabled { get; set; } = false;

        /// <summary>上一帧累计约束力（用于断裂检测）。</summary>
        protected float _accumulatedForce;
        protected float _accumulatedTorque;

        /// <summary>关节是否已断裂。</summary>
        public bool IsBroken { get; protected set; }

        /// <summary>断裂时触发的事件。</summary>
        public event Action<PhysicsJoint> OnBreak;

        protected PhysicsJoint(JointType type, RigidBody bodyA, RigidBody bodyB,
            PhysicsVector3 localAnchorA, PhysicsVector3 localAnchorB)
        {
            Type = type;
            BodyA = bodyA;
            BodyB = bodyB;
            LocalAnchorA = localAnchorA;
            LocalAnchorB = localAnchorB;
            BreakForce = 0.0f;
            BreakTorque = 0.0f;
        }

        /// <summary>检查是否应断裂。</summary>
        public bool CheckBreak()
        {
            if (IsBroken || BreakForce <= 0 && BreakTorque <= 0) return false;

            bool shouldBreak = false;
            if (BreakForce > 0 && _accumulatedForce > BreakForce) shouldBreak = true;
            if (BreakTorque > 0 && _accumulatedTorque > BreakTorque) shouldBreak = true;

            if (shouldBreak)
            {
                IsBroken = true;
                OnBreak?.Invoke(this);
                return true;
            }
            return false;
        }

        /// <summary>重置累积约束力。</summary>
        public virtual void ResetAccumulators()
        {
            _accumulatedForce = 0;
            _accumulatedTorque = 0;
        }

        /// <summary>
        ///   计算世界空间锚点A。
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public PhysicsVector3 GetWorldAnchorA()
        {
            if (BodyA == null) return LocalAnchorA;
            PhysicsMatrix3x3 rotA = BodyA.Rotation.ToMatrix3x3();
            return BodyA.Position + rotA.Multiply(LocalAnchorA);
        }

        /// <summary>
        ///   计算世界空间锚点B。
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public PhysicsVector3 GetWorldAnchorB()
        {
            if (BodyB == null) return LocalAnchorB;
            PhysicsMatrix3x3 rotB = BodyB.Rotation.ToMatrix3x3();
            return BodyB.Position + rotB.Multiply(LocalAnchorB);
        }

        /// <summary>
        ///   计算相对位置（世界空间锚点A - 世界空间锚点B）。
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public PhysicsVector3 GetRelativePosition()
        {
            return GetWorldAnchorA() - GetWorldAnchorB();
        }
    }

    #endregion

    #region ======================== 距离关节 ========================

    /// <summary>
    ///   距离关节：保持两个锚点之间的距离在 [MinDistance, MaxDistance] 范围内。
    ///   求解：F = -stiffness * (distance - targetDistance) - damping * relativeVelocity
    /// </summary>
    public class DistanceJoint : PhysicsJoint
    {
        /// <summary>最小距离。</summary>
        public float MinDistance { get; set; }

        /// <summary>最大距离。</summary>
        public float MaxDistance { get; set; }

        /// <summary>弹簧刚度系数。</summary>
        public float Stiffness { get; set; } = 50.0f;

        /// <summary>弹簧阻尼系数。</summary>
        public float Damping { get; set; } = 5.0f;

        /// <summary>目标距离（初始距离）。</summary>
        private float _targetDistance;

        /// <summary>上一帧拉格朗日乘数（Warm Starting）。</summary>
        private float _warmStartLambda;

        public DistanceJoint(RigidBody bodyA, RigidBody bodyB,
            PhysicsVector3 localAnchorA, PhysicsVector3 localAnchorB,
            float minDistance = 0, float maxDistance = float.PositiveInfinity)
            : base(JointType.Distance, bodyA, bodyB, localAnchorA, localAnchorB)
        {
            MinDistance = minDistance;
            MaxDistance = maxDistance;
            _targetDistance = GetRelativePosition().Length();
        }

        /// <summary>求解距离约束。</summary>
        public void Solve(float dt)
        {
            if (!Enabled || IsBroken) return;

            PhysicsVector3 rA = GetWorldAnchorA() - BodyA.Position;
            PhysicsVector3 rB = GetWorldAnchorB() - BodyB.Position;
            PhysicsVector3 delta = GetRelativePosition();
            float distance = delta.Length();

            if (distance < 1e-6f) return;

            PhysicsVector3 n = delta / distance;

            // 相对速度沿法线方向
            PhysicsVector3 vA = BodyA.LinearVelocity + BodyA.AngularVelocity.Cross(rA);
            PhysicsVector3 vB = BodyB.LinearVelocity + BodyB.AngularVelocity.Cross(rB);
            float relVelN = (vA - vB).Dot(n);

            // 弹簧阻尼力：F = -stiffness * (distance - targetDistance) - damping * relativeVelocity
            float springForce = -Stiffness * (distance - _targetDistance) - Damping * relVelN;

            // 如果距离在范围内且力向外，不施加力
            if (distance <= MaxDistance && distance >= MinDistance && springForce < 0)
                return;

            // 钳制到范围内
            if (distance > MaxDistance)
                springForce = Math.Min(springForce, Stiffness * (distance - MaxDistance));
            else if (distance < MinDistance)
                springForce = Math.Max(springForce, Stiffness * (distance - MinDistance));

            // Jacobian：J = [n, rA x n, -n, -rB x n]
            float invMassA = BodyA != null ? BodyA.InvMass : 0;
            float invMassB = BodyB != null ? BodyB.InvMass : 0;
            PhysicsVector3 rAxN = rA.Cross(n);
            PhysicsVector3 rBxN = rB.Cross(n);

            PhysicsVector3 invInertiaA = BodyA != null
                ? BodyA.InvInertiaWorld.Multiply(rAxN)
                : PhysicsVector3.Zero;
            PhysicsVector3 invInertiaB = BodyB != null
                ? BodyB.InvInertiaWorld.Multiply(rBxN)
                : PhysicsVector3.Zero;

            float effectiveMass = invMassA + invMassB
                + rAxN.Dot(invInertiaA)
                + rBxN.Dot(invInertiaB);

            if (effectiveMass < 1e-12f) return;
            float invEffectiveMass = 1.0f / effectiveMass;

            // Warm Starting
            float lambda = springForce * dt * invEffectiveMass + _warmStartLambda * 0.8f;
            _warmStartLambda = lambda;

            // 施加冲量
            PhysicsVector3 impulse = n * lambda;
            if (BodyA != null && !BodyA.IsStatic)
            {
                BodyA.LinearVelocity = BodyA.LinearVelocity - impulse * invMassA;
                BodyA.AngularVelocity = BodyA.AngularVelocity - invInertiaA * invMassA;
            }
            if (BodyB != null && !BodyB.IsStatic)
            {
                BodyB.LinearVelocity = BodyB.LinearVelocity + impulse * invMassB;
                BodyB.AngularVelocity = BodyB.AngularVelocity + invInertiaB * invMassB;
            }

            _accumulatedForce = Math.Abs(lambda / dt);
        }

        public override void ResetAccumulators()
        {
            base.ResetAccumulators();
            _warmStartLambda = 0;
        }
    }

    #endregion

    #region ======================== 铰链关节 ========================

    /// <summary>
    ///   铰链关节：允许两个刚体围绕一个公共轴旋转。
    ///   投影速度到铰链轴，约束其他轴自由度。
    ///   角度限制使用 soft clamping。
    /// </summary>
    public class HingeJoint : PhysicsJoint
    {
        /// <summary>Body A本地空间旋转轴（归一化）。</summary>
        public PhysicsVector3 LocalAxisA { get; set; }

        /// <summary>Body B本地空间旋转轴（归一化）。</summary>
        public PhysicsVector3 LocalAxisB { get; set; }

        /// <summary>最小旋转角度（弧度）。</summary>
        public float MinAngle { get; set; } = float.NegativeInfinity;

        /// <summary>最大旋转角度（弧度）。</summary>
        public float MaxAngle { get; set; } = float.PositiveInfinity;

        /// <summary>马达目标角速度（弧度/秒）。0 = 马达关闭。</summary>
        public float MotorSpeed { get; set; }

        /// <summary>马达最大力矩。</summary>
        public float MotorMaxTorque { get; set; }

        /// <summary>角度限制的软度系数（0=硬限制, 1=软限制）。</summary>
        public float LimitSoftness { get; set; } = 0.9f;

        /// <summary>角度限制的偏置因子（Baumgarte）。</summary>
        public float LimitBiasFactor { get; set; } = 0.3f;

        /// <summary>角度限制的松弛系数。</summary>
        public float LimitRelaxation { get; set; } = 1.0f;

        // Warm Starting 缓存
        private float[] _warmStartLambdas = new float[6];

        public HingeJoint(RigidBody bodyA, RigidBody bodyB,
            PhysicsVector3 localAnchorA, PhysicsVector3 localAnchorB,
            PhysicsVector3 localAxisA, PhysicsVector3 localAxisB)
            : base(JointType.Hinge, bodyA, bodyB, localAnchorA, localAnchorB)
        {
            LocalAxisA = localAxisA.Normalized();
            LocalAxisB = localAxisB.Normalized();
            MotorSpeed = 0;
            MotorMaxTorque = 0;
        }

        /// <summary>求解铰链约束。</summary>
        public void Solve(float dt)
        {
            if (!Enabled || IsBroken) return;

            // ---- 步骤1：位置约束（保持锚点重合）----
            PhysicsVector3 rA = GetWorldAnchorA() - BodyA.Position;
            PhysicsVector3 rB = GetWorldAnchorB() - BodyB.Position;
            PhysicsVector3 delta = GetRelativePosition();

            float invMassA = BodyA?.InvMass ?? 0;
            float invMassB = BodyB?.InvMass ?? 0;

            // 线性位置误差
            PhysicsVector3 n = delta;
            float len = n.Length();
            if (len > 1e-6f) n = n / len; else n = PhysicsVector3.Zero;

            // 线性 Jacobian
            PhysicsVector3 rAxN = rA.Cross(n);
            PhysicsVector3 rBxN = rB.Cross(n);

            PhysicsVector3 angInvA = BodyA != null ? BodyA.InvInertiaWorld.Multiply(rAxN) : PhysicsVector3.Zero;
            PhysicsVector3 angInvB = BodyB != null ? BodyB.InvInertiaWorld.Multiply(rBxN) : PhysicsVector3.Zero;

            float linEffMass = invMassA + invMassB
                + rAxN.Dot(angInvA) + rBxN.Dot(angInvB);

            if (linEffMass > 1e-12f)
            {
                float lambda = -len / linEffMass + _warmStartLambdas[0] * 0.8f;
                _warmStartLambdas[0] = lambda;

                PhysicsVector3 impulse = n * lambda;
                if (BodyA != null && !BodyA.IsStatic)
                {
                    BodyA.LinearVelocity = BodyA.LinearVelocity - impulse * invMassA;
                    BodyA.AngularVelocity = BodyA.AngularVelocity - angInvA;
                }
                if (BodyB != null && !BodyB.IsStatic)
                {
                    BodyB.LinearVelocity = BodyB.LinearVelocity + impulse * invMassB;
                    BodyB.AngularVelocity = BodyB.AngularVelocity + angInvB;
                }
            }

            // ---- 步骤2：旋转约束（只允许绕铰链轴旋转）----
            PhysicsMatrix3x3 rotA = BodyA.Rotation.ToMatrix3x3();
            PhysicsMatrix3x3 rotB = BodyB.Rotation.ToMatrix3x3();
            PhysicsVector3 worldAxisA = rotA.Multiply(LocalAxisA).Normalized();
            PhysicsVector3 worldAxisB = rotB.Multiply(LocalAxisB).Normalized();

            // 两个轴的叉积 = 旋转误差轴
            PhysicsVector3 errorAxis = worldAxisA.Cross(worldAxisB);
            float errorMag = errorAxis.Length();

            // 约束另外两个旋转自由度（垂直于铰链轴的方向）
            for (int i = 0; i < 2; i++)
            {
                PhysicsVector3 perpAxis;
                if (errorMag > 1e-6f)
                {
                    perpAxis = errorAxis.Normalized();
                    if (i == 1)
                    {
                        // 第二个垂直轴
                        perpAxis = perpAxis.Cross(worldAxisA);
                        if (perpAxis.LengthSquared() < 1e-8f) continue;
                        perpAxis = perpAxis.Normalized();
                    }
                }
                else
                {
                    // 构建正交基
                    PhysicsVector3 tmp = Math.Abs(worldAxisA.X) < 0.9f
                        ? new PhysicsVector3(1, 0, 0) : new PhysicsVector3(0, 1, 0);
                    perpAxis = worldAxisA.Cross(tmp).Normalized();
                    if (i == 1) perpAxis = worldAxisA.Cross(perpAxis).Normalized();
                }

                // 旋转 Jacobian：J = [0, perpAxis, 0, -perpAxis]
                PhysicsVector3 angInvPerpA = BodyA != null ? BodyA.InvInertiaWorld.Multiply(perpAxis) : PhysicsVector3.Zero;
                PhysicsVector3 angInvPerpB = BodyB != null ? BodyB.InvInertiaWorld.Multiply(perpAxis) : PhysicsVector3.Zero;

                float angEffMass = perpAxis.Dot(angInvPerpA) + perpAxis.Dot(angInvPerpB);
                if (angEffMass < 1e-12f) continue;

                float error = i == 0 ? errorMag * LimitBiasFactor : 0;
                float lambda = -error / angEffMass + _warmStartLambdas[1 + i] * 0.8f;
                _warmStartLambdas[1 + i] = lambda;

                if (BodyA != null && !BodyA.IsStatic)
                    BodyA.AngularVelocity = BodyA.AngularVelocity - angInvPerpA * lambda;
                if (BodyB != null && !BodyB.IsStatic)
                    BodyB.AngularVelocity = BodyB.AngularVelocity + angInvPerpB * lambda;
            }

            // ---- 步骤3：角度限制 ----
            if (MinAngle > float.NegativeInfinity || MaxAngle < float.PositiveInfinity)
            {
                SolveAngleLimits(dt, worldAxisA);
            }

            // ---- 步骤4：马达驱动 ----
            if (MotorMaxTorque > 0 && Math.Abs(MotorSpeed) > 1e-6f)
            {
                SolveMotor(dt);
            }

            _accumulatedForce = Math.Abs(_warmStartLambdas[0] / dt);
        }

        /// <summary>求解铰链角度限制。</summary>
        private void SolveAngleLimits(float dt, PhysicsVector3 worldAxisA)
        {
            // 计算相对旋转角度
            PhysicsQuaternion qRel = BodyB.Rotation.Inverse() * BodyA.Rotation;
            // 将qRel投影到铰链轴
            PhysicsVector3 axisAngle = qRel.ToAxisAngle();
            float angle = axisAngle.Length();

            // 计算绕铰链轴的角度
            PhysicsVector3 relAxisLocal = BodyA.Rotation.Inverse().ToMatrix3x3()
                .Multiply(BodyB.Rotation.ToMatrix3x3().Multiply(LocalAxisB));
            float hingeAngle = (float)Math.Atan2(
                relAxisLocal.X * LocalAxisA.Z - relAxisLocal.Z * LocalAxisA.X,
                relAxisLocal.Dot(LocalAxisA));

            float error = 0;
            if (hingeAngle < MinAngle)
                error = (MinAngle - hingeAngle) * LimitBiasFactor * LimitSoftness;
            else if (hingeAngle > MaxAngle)
                error = (MaxAngle - hingeAngle) * LimitBiasFactor * LimitSoftness;

            if (Math.Abs(error) < 1e-6f) return;

            PhysicsVector3 angInvAxis = BodyA != null
                ? BodyA.InvInertiaWorld.Multiply(worldAxisA) : PhysicsVector3.Zero;
            PhysicsVector3 angInvAxisB = BodyB != null
                ? BodyB.InvInertiaWorld.Multiply(worldAxisA) : PhysicsVector3.Zero;

            float effMass = worldAxisA.Dot(angInvAxis) + worldAxisA.Dot(angInvAxisB);
            if (effMass < 1e-12f) return;

            float lambda = -error / (effMass * LimitRelaxation) + _warmStartLambdas[3] * 0.8f;
            _warmStartLambdas[3] = lambda;

            if (BodyA != null && !BodyA.IsStatic)
                BodyA.AngularVelocity = BodyA.AngularVelocity + angInvAxis * lambda;
            if (BodyB != null && !BodyB.IsStatic)
                BodyB.AngularVelocity = BodyB.AngularVelocity - angInvAxisB * lambda;
        }

        /// <summary>求解马达驱动。</summary>
        private void SolveMotor(float dt)
        {
            PhysicsMatrix3x3 rotA = BodyA.Rotation.ToMatrix3x3();
            PhysicsVector3 worldAxis = rotA.Multiply(LocalAxisA).Normalized();

            float velA = BodyA != null ? BodyA.AngularVelocity.Dot(worldAxis) : 0;
            float velB = BodyB != null ? BodyB.AngularVelocity.Dot(worldAxis) : 0;
            float relVel = velA - velB;

            float error = MotorSpeed - relVel;

            PhysicsVector3 angInvAxis = BodyA != null
                ? BodyA.InvInertiaWorld.Multiply(worldAxis) : PhysicsVector3.Zero;
            PhysicsVector3 angInvAxisB = BodyB != null
                ? BodyB.InvInertiaWorld.Multiply(worldAxis) : PhysicsVector3.Zero;

            float effMass = worldAxis.Dot(angInvAxis) + worldAxis.Dot(angInvAxisB);
            if (effMass < 1e-12f) return;

            float maxImpulse = MotorMaxTorque * dt;
            float lambda = -error / effMass;
            lambda = Math.Max(-maxImpulse, Math.Min(maxImpulse, lambda));
            lambda += _warmStartLambdas[4] * 0.8f;
            lambda = Math.Max(-maxImpulse, Math.Min(maxImpulse, lambda));
            _warmStartLambdas[4] = lambda;

            if (BodyA != null && !BodyA.IsStatic)
                BodyA.AngularVelocity = BodyA.AngularVelocity + angInvAxis * lambda;
            if (BodyB != null && !BodyB.IsStatic)
                BodyB.AngularVelocity = BodyB.AngularVelocity - angInvAxisB * lambda;

            _accumulatedTorque = Math.Abs(lambda / dt);
        }

        public override void ResetAccumulators()
        {
            base.ResetAccumulators();
            for (int i = 0; i < _warmStartLambdas.Length; i++)
                _warmStartLambdas[i] = 0;
        }
    }

    #endregion

    #region ======================== 滑动关节 ========================

    /// <summary>
    ///   滑动关节：允许两个刚体沿一个轴平移，限制其他方向的运动。
    /// </summary>
    public class SliderJoint : PhysicsJoint
    {
        /// <summary>Body A本地空间滑动轴（归一化）。</summary>
        public PhysicsVector3 LocalAxisA { get; set; }

        /// <summary>Body B本地空间滑动轴（归一化）。</summary>
        public PhysicsVector3 LocalAxisB { get; set; }

        /// <summary>最小滑动位置。</summary>
        public float MinPosition { get; set; } = float.NegativeInfinity;

        /// <summary>最大滑动位置。</summary>
        public float MaxPosition { get; set; } = float.PositiveInfinity;

        /// <summary>马达驱动力。</summary>
        public float MotorForce { get; set; }

        /// <summary>马达目标速度。</summary>
        public float MotorSpeed { get; set; }

        // Warm Starting
        private float[] _warmStartLambdas = new float[6];

        public SliderJoint(RigidBody bodyA, RigidBody bodyB,
            PhysicsVector3 localAnchorA, PhysicsVector3 localAnchorB,
            PhysicsVector3 localAxisA, PhysicsVector3 localAxisB)
            : base(JointType.Slider, bodyA, bodyB, localAnchorA, localAnchorB)
        {
            LocalAxisA = localAxisA.Normalized();
            LocalAxisB = localAxisB.Normalized();
        }

        /// <summary>求解滑动约束。</summary>
        public void Solve(float dt)
        {
            if (!Enabled || IsBroken) return;

            PhysicsVector3 rA = GetWorldAnchorA() - BodyA.Position;
            PhysicsVector3 rB = GetWorldAnchorB() - BodyB.Position;
            PhysicsVector3 delta = GetRelativePosition();

            PhysicsMatrix3x3 rotA = BodyA.Rotation.ToMatrix3x3();
            PhysicsMatrix3x3 rotB = BodyB.Rotation.ToMatrix3x3();
            PhysicsVector3 worldAxisA = rotA.Multiply(LocalAxisA).Normalized();

            // 滑动位移
            float slideOffset = delta.Dot(worldAxisA);

            float invMassA = BodyA?.InvMass ?? 0;
            float invMassB = BodyB?.InvMass ?? 0;

            // 约束垂直于滑动轴的两个平移方向
            for (int i = 0; i < 2; i++)
            {
                PhysicsVector3 perpAxis;
                if (i == 0)
                {
                    PhysicsVector3 tmp = Math.Abs(worldAxisA.X) < 0.9f
                        ? new PhysicsVector3(1, 0, 0) : new PhysicsVector3(0, 1, 0);
                    perpAxis = worldAxisA.Cross(tmp).Normalized();
                }
                else
                {
                    perpAxis = worldAxisA.Cross(
                        (Math.Abs(worldAxisA.X) < 0.9f
                            ? new PhysicsVector3(1, 0, 0) : new PhysicsVector3(0, 1, 0))).Normalized();
                    perpAxis = worldAxisA.Cross(perpAxis).Normalized();
                }

                float perpError = delta.Dot(perpAxis);

                PhysicsVector3 rAxP = rA.Cross(perpAxis);
                PhysicsVector3 rBxP = rB.Cross(perpAxis);
                PhysicsVector3 angInvA = BodyA != null ? BodyA.InvInertiaWorld.Multiply(rAxP) : PhysicsVector3.Zero;
                PhysicsVector3 angInvB = BodyB != null ? BodyB.InvInertiaWorld.Multiply(rBxP) : PhysicsVector3.Zero;

                float effMass = invMassA + invMassB + rAxP.Dot(angInvA) + rBxP.Dot(angInvB);
                if (effMass < 1e-12f) continue;

                float lambda = -perpError / effMass + _warmStartLambdas[i] * 0.8f;
                _warmStartLambdas[i] = lambda;

                PhysicsVector3 impulse = perpAxis * lambda;
                if (BodyA != null && !BodyA.IsStatic)
                {
                    BodyA.LinearVelocity = BodyA.LinearVelocity - impulse * invMassA;
                    BodyA.AngularVelocity = BodyA.AngularVelocity - angInvA;
                }
                if (BodyB != null && !BodyB.IsStatic)
                {
                    BodyB.LinearVelocity = BodyB.LinearVelocity + impulse * invMassB;
                    BodyB.AngularVelocity = BodyB.AngularVelocity + angInvB;
                }
            }

            // 约束所有3个旋转自由度
            for (int i = 0; i < 3; i++)
            {
                PhysicsVector3 rotAxis = i == 0 ? worldAxisA :
                    (i == 1 ? worldAxisA.Cross(new PhysicsVector3(0, 1, 0)).Normalized() :
                     worldAxisA.Cross(worldAxisA.Cross(new PhysicsVector3(0, 1, 0))).Normalized());

                PhysicsVector3 angInvA = BodyA != null ? BodyA.InvInertiaWorld.Multiply(rotAxis) : PhysicsVector3.Zero;
                PhysicsVector3 angInvB = BodyB != null ? BodyB.InvInertiaWorld.Multiply(rotAxis) : PhysicsVector3.Zero;
                float effMass = rotAxis.Dot(angInvA) + rotAxis.Dot(angInvB);
                if (effMass < 1e-12f) continue;

                float lambda = _warmStartLambdas[2 + i] * 0.8f;
                _warmStartLambdas[2 + i] = lambda;

                if (BodyA != null && !BodyA.IsStatic)
                    BodyA.AngularVelocity = BodyA.AngularVelocity - angInvA * lambda;
                if (BodyB != null && !BodyB.IsStatic)
                    BodyB.AngularVelocity = BodyB.AngularVelocity + angInvB * lambda;
            }

            // 位置限制
            if (MinPosition > float.NegativeInfinity || MaxPosition < float.PositiveInfinity)
            {
                float posError = 0;
                if (slideOffset < MinPosition)
                    posError = MinPosition - slideOffset;
                else if (slideOffset > MaxPosition)
                    posError = MaxPosition - slideOffset;

                if (Math.Abs(posError) > 1e-6f)
                {
                    PhysicsVector3 rAxN = rA.Cross(worldAxisA);
                    PhysicsVector3 rBxN = rB.Cross(worldAxisA);
                    PhysicsVector3 angInvA = BodyA != null ? BodyA.InvInertiaWorld.Multiply(rAxN) : PhysicsVector3.Zero;
                    PhysicsVector3 angInvB = BodyB != null ? BodyB.InvInertiaWorld.Multiply(rBxN) : PhysicsVector3.Zero;

                    float effMass = invMassA + invMassB + rAxN.Dot(angInvA) + rBxN.Dot(angInvB);
                    if (effMass > 1e-12f)
                    {
                        float lambda = posError / effMass;
                        PhysicsVector3 impulse = worldAxisA * lambda;
                        if (BodyA != null && !BodyA.IsStatic)
                            BodyA.LinearVelocity = BodyA.LinearVelocity + impulse * invMassA;
                        if (BodyB != null && !BodyB.IsStatic)
                            BodyB.LinearVelocity = BodyB.LinearVelocity - impulse * invMassB;
                    }
                }
            }

            // 马达驱动
            if (MotorForce > 0 && Math.Abs(MotorSpeed) > 1e-6f)
            {
                float velA = BodyA != null ? BodyA.LinearVelocity.Dot(worldAxisA) : 0;
                float velB = BodyB != null ? BodyB.LinearVelocity.Dot(worldAxisA) : 0;
                float relVel = velA - velB;
                float targetVel = MotorSpeed;

                float maxImpulse = MotorForce * dt;
                float lambda = (targetVel - relVel) / (invMassA + invMassB);
                lambda = Math.Max(-maxImpulse, Math.Min(maxImpulse, lambda));

                PhysicsVector3 impulse = worldAxisA * lambda;
                if (BodyA != null && !BodyA.IsStatic)
                    BodyA.LinearVelocity = BodyA.LinearVelocity + impulse * invMassA;
                if (BodyB != null && !BodyB.IsStatic)
                    BodyB.LinearVelocity = BodyB.LinearVelocity - impulse * invMassB;
            }

            _accumulatedForce = Math.Abs(slideOffset);
        }

        public override void ResetAccumulators()
        {
            base.ResetAccumulators();
            for (int i = 0; i < _warmStartLambdas.Length; i++)
                _warmStartLambdas[i] = 0;
        }
    }

    #endregion

    #region ======================== 弹簧关节 ========================

    /// <summary>
    ///   弹簧关节：基于Hooke定律的弹性连接。
    ///   F = -k * (x - x0) - c * v
    /// </summary>
    public class SpringJoint : PhysicsJoint
    {
        /// <summary>弹簧自然长度。</summary>
        public float RestLength { get; set; }

        /// <summary>弹簧刚度（k）。</summary>
        public float Stiffness { get; set; } = 100.0f;

        /// <summary>阻尼系数（c）。</summary>
        public float Damping { get; set; } = 10.0f;

        private float _warmStartLambda;

        public SpringJoint(RigidBody bodyA, RigidBody bodyB,
            PhysicsVector3 localAnchorA, PhysicsVector3 localAnchorB,
            float stiffness = 100.0f, float damping = 10.0f)
            : base(JointType.Spring, bodyA, bodyB, localAnchorA, localAnchorB)
        {
            Stiffness = stiffness;
            Damping = damping;
            RestLength = GetRelativePosition().Length();
        }

        /// <summary>求解弹簧约束（Hooke定律）。</summary>
        public void Solve(float dt)
        {
            if (!Enabled || IsBroken) return;

            PhysicsVector3 rA = GetWorldAnchorA() - BodyA.Position;
            PhysicsVector3 rB = GetWorldAnchorB() - BodyB.Position;
            PhysicsVector3 delta = GetRelativePosition();
            float distance = delta.Length();

            if (distance < 1e-6f) return;

            PhysicsVector3 n = delta / distance;

            // 相对速度
            PhysicsVector3 vA = BodyA.LinearVelocity + BodyA.AngularVelocity.Cross(rA);
            PhysicsVector3 vB = BodyB.LinearVelocity + BodyB.AngularVelocity.Cross(rB);
            float relVelN = (vA - vB).Dot(n);

            // Hooke定律: F = -k * (x - x0) - c * v
            float extension = distance - RestLength;
            float springForce = -Stiffness * extension - Damping * relVelN;

            // Jacobian
            float invMassA = BodyA?.InvMass ?? 0;
            float invMassB = BodyB?.InvMass ?? 0;
            PhysicsVector3 rAxN = rA.Cross(n);
            PhysicsVector3 rBxN = rB.Cross(n);
            PhysicsVector3 angInvA = BodyA != null ? BodyA.InvInertiaWorld.Multiply(rAxN) : PhysicsVector3.Zero;
            PhysicsVector3 angInvB = BodyB != null ? BodyB.InvInertiaWorld.Multiply(rBxN) : PhysicsVector3.Zero;

            float effMass = invMassA + invMassB + rAxN.Dot(angInvA) + rBxN.Dot(angInvB);
            if (effMass < 1e-12f) return;

            float lambda = springForce * dt / effMass + _warmStartLambda * 0.8f;
            _warmStartLambda = lambda;

            PhysicsVector3 impulse = n * lambda;
            if (BodyA != null && !BodyA.IsStatic)
            {
                BodyA.LinearVelocity = BodyA.LinearVelocity - impulse * invMassA;
                BodyA.AngularVelocity = BodyA.AngularVelocity - angInvA;
            }
            if (BodyB != null && !BodyB.IsStatic)
            {
                BodyB.LinearVelocity = BodyB.LinearVelocity + impulse * invMassB;
                BodyB.AngularVelocity = BodyB.AngularVelocity + angInvB;
            }

            _accumulatedForce = Math.Abs(springForce);
        }

        public override void ResetAccumulators()
        {
            base.ResetAccumulators();
            _warmStartLambda = 0;
        }
    }

    #endregion

    #region ======================== 固定关节 ========================

    /// <summary>
    ///   固定关节：焊接两个刚体，保持相对位置和旋转不变。
    ///   约束方程：x_A + R_A * localAnchorA = x_B + R_B * localAnchorB
    /// </summary>
    public class FixedJoint : PhysicsJoint
    {
        /// <summary>初始相对偏移（本地B帧中的A锚点位置）。</summary>
        private PhysicsVector3 _initialLocalOffsetB;

        /// <summary>初始相对旋转（B帧到A帧的旋转差）。</summary>
        private PhysicsQuaternion _initialRelativeRotation;

        // Warm Starting: 3个线性 + 3个旋转 = 6个lambda
        private float[] _warmStartLambdas = new float[6];

        public FixedJoint(RigidBody bodyA, RigidBody bodyB,
            PhysicsVector3 localAnchorA, PhysicsVector3 localAnchorB)
            : base(JointType.Fixed, bodyA, bodyB, localAnchorA, localAnchorB)
        {
            // 记录初始相对变换
            PhysicsMatrix3x3 rotBInv = BodyB.Rotation.Inverse().ToMatrix3x3();
            _initialLocalOffsetB = rotBInv.Multiply(
                GetWorldAnchorA() - BodyB.Position);
            _initialRelativeRotation = BodyB.Rotation.Inverse() * BodyA.Rotation;
        }

        /// <summary>求解固定约束。</summary>
        public void Solve(float dt)
        {
            if (!Enabled || IsBroken) return;

            float invMassA = BodyA?.InvMass ?? 0;
            float invMassB = BodyB?.InvMass ?? 0;

            // ---- 位置约束：3个线性约束 ----
            PhysicsVector3 rA = GetWorldAnchorA() - BodyA.Position;
            PhysicsVector3 rB_expected = BodyB.Rotation.ToMatrix3x3().Multiply(_initialLocalOffsetB);
            PhysicsVector3 rB = GetWorldAnchorB() - BodyB.Position;

            PhysicsVector3 posError = (BodyA.Position + rA) - (BodyB.Position + rB_expected);

            for (int axis = 0; axis < 3; axis++)
            {
                PhysicsVector3 n = axis == 0 ? new PhysicsVector3(1, 0, 0) :
                                  axis == 1 ? new PhysicsVector3(0, 1, 0) :
                                              new PhysicsVector3(0, 0, 1);
                float error = posError.X * n.X + posError.Y * n.Y + posError.Z * n.Z;

                PhysicsVector3 rAxN = rA.Cross(n);
                PhysicsVector3 rBxN = rB_expected.Cross(n);
                PhysicsVector3 angInvA = BodyA != null ? BodyA.InvInertiaWorld.Multiply(rAxN) : PhysicsVector3.Zero;
                PhysicsVector3 angInvB = BodyB != null ? BodyB.InvInertiaWorld.Multiply(rBxN) : PhysicsVector3.Zero;

                float effMass = invMassA + invMassB + rAxN.Dot(angInvA) + rBxN.Dot(angInvB);
                if (effMass < 1e-12f) continue;

                float lambda = -error / effMass + _warmStartLambdas[axis] * 0.8f;
                _warmStartLambdas[axis] = lambda;

                PhysicsVector3 impulse = n * lambda;
                if (BodyA != null && !BodyA.IsStatic)
                {
                    BodyA.LinearVelocity = BodyA.LinearVelocity - impulse * invMassA;
                    BodyA.AngularVelocity = BodyA.AngularVelocity - angInvA;
                }
                if (BodyB != null && !BodyB.IsStatic)
                {
                    BodyB.LinearVelocity = BodyB.LinearVelocity + impulse * invMassB;
                    BodyB.AngularVelocity = BodyB.AngularVelocity + angInvB;
                }
            }

            // ---- 旋转约束：3个旋转约束 ----
            PhysicsQuaternion qRel = _initialRelativeRotation.Inverse() * (BodyB.Rotation.Inverse() * BodyA.Rotation);
            PhysicsVector3 rotError = qRel.ToAxisAngle();
            float rotErrorMag = rotError.Length();

            for (int axis = 0; axis < 3; axis++)
            {
                PhysicsVector3 n = axis == 0 ? new PhysicsVector3(1, 0, 0) :
                                  axis == 1 ? new PhysicsVector3(0, 1, 0) :
                                              new PhysicsVector3(0, 0, 1);
                float error = rotErrorMag > 1e-6f
                    ? rotError.Normalized().Dot(n) * rotErrorMag
                    : 0;

                PhysicsVector3 angInvA = BodyA != null ? BodyA.InvInertiaWorld.Multiply(n) : PhysicsVector3.Zero;
                PhysicsVector3 angInvB = BodyB != null ? BodyB.InvInertiaWorld.Multiply(n) : PhysicsVector3.Zero;
                float effMass = n.Dot(angInvA) + n.Dot(angInvB);
                if (effMass < 1e-12f) continue;

                float lambda = -error / effMass + _warmStartLambdas[3 + axis] * 0.8f;
                _warmStartLambdas[3 + axis] = lambda;

                if (BodyA != null && !BodyA.IsStatic)
                    BodyA.AngularVelocity = BodyA.AngularVelocity + angInvA * lambda;
                if (BodyB != null && !BodyB.IsStatic)
                    BodyB.AngularVelocity = BodyB.AngularVelocity - angInvB * lambda;
            }

            _accumulatedForce = posError.Length() / Math.Max(dt, 1e-6f);
            _accumulatedTorque = rotErrorMag / Math.Max(dt, 1e-6f);
        }

        public override void ResetAccumulators()
        {
            base.ResetAccumulators();
            for (int i = 0; i < _warmStartLambdas.Length; i++)
                _warmStartLambdas[i] = 0;
        }
    }

    #endregion

    #region ======================== 锥形扭转关节 ========================

    /// <summary>
    ///   锥形扭转关节：限制摆动和扭转范围，用于布娃娃肩关节、髋关节等。
    /// </summary>
    public class ConeTwistJoint : PhysicsJoint
    {
        /// <summary>摆动范围（弧度，绕两个正交轴的最大旋转）。</summary>
        public float SwingSpan { get; set; } = (float)(Math.PI / 4);

        /// <summary>扭转范围（弧度，绕主轴的最大旋转）。</summary>
        public float TwistSpan { get; set; } = (float)(Math.PI / 4);

        /// <summary>软度系数（0=硬限制, 1=完全软）。</summary>
        public float Softness { get; set; } = 0.5f;

        /// <summary>偏置因子。</summary>
        public float BiasFactor { get; set; } = 0.3f;

        /// <summary>松弛系数。</summary>
        public float RelaxationFactor { get; set; } = 1.0f;

        /// <summary>Body A的扭转轴参考方向。</summary>
        public PhysicsVector3 TwistAxisA { get; set; }

        // Warm Starting
        private float[] _warmStartLambdas = new float[6];

        public ConeTwistJoint(RigidBody bodyA, RigidBody bodyB,
            PhysicsVector3 localAnchorA, PhysicsVector3 localAnchorB)
            : base(JointType.ConeTwist, bodyA, bodyB, localAnchorA, localAnchorB)
        {
            TwistSpan = (float)(Math.PI / 4);
            SwingSpan = (float)(Math.PI / 4);
        }

        /// <summary>求解锥形扭转约束。</summary>
        public void Solve(float dt)
        {
            if (!Enabled || IsBroken) return;

            // ---- 位置约束 ----
            PhysicsVector3 rA = GetWorldAnchorA() - BodyA.Position;
            PhysicsVector3 rB = GetWorldAnchorB() - BodyB.Position;
            PhysicsVector3 delta = GetRelativePosition();
            float distance = delta.Length();

            float invMassA = BodyA?.InvMass ?? 0;
            float invMassB = BodyB?.InvMass ?? 0;

            if (distance > 1e-6f)
            {
                PhysicsVector3 n = delta / distance;
                PhysicsVector3 rAxN = rA.Cross(n);
                PhysicsVector3 rBxN = rB.Cross(n);
                PhysicsVector3 angInvA = BodyA != null ? BodyA.InvInertiaWorld.Multiply(rAxN) : PhysicsVector3.Zero;
                PhysicsVector3 angInvB = BodyB != null ? BodyB.InvInertiaWorld.Multiply(rBxN) : PhysicsVector3.Zero;

                float effMass = invMassA + invMassB + rAxN.Dot(angInvA) + rBxN.Dot(angInvB);
                if (effMass > 1e-12f)
                {
                    float lambda = -distance / effMass + _warmStartLambdas[0] * 0.8f;
                    _warmStartLambdas[0] = lambda;

                    PhysicsVector3 impulse = n * lambda;
                    if (BodyA != null && !BodyA.IsStatic)
                    {
                        BodyA.LinearVelocity = BodyA.LinearVelocity - impulse * invMassA;
                        BodyA.AngularVelocity = BodyA.AngularVelocity - angInvA;
                    }
                    if (BodyB != null && !BodyB.IsStatic)
                    {
                        BodyB.LinearVelocity = BodyB.LinearVelocity + impulse * invMassB;
                        BodyB.AngularVelocity = BodyB.AngularVelocity + angInvB;
                    }
                }
            }

            // ---- 锥形约束（摆动限制）----
            PhysicsQuaternion qRel = BodyB.Rotation.Inverse() * BodyA.Rotation;
            PhysicsVector3 axisAngle = qRel.ToAxisAngle();
            float angle = axisAngle.Length();

            if (angle > 1e-6f)
            {
                PhysicsVector3 axis = axisAngle / angle;

                // 将旋转分解到锥形框架
                // swing angle: 偏离twist轴的角度
                // twist angle: 绕twist轴的旋转
                PhysicsMatrix3x3 rotB = BodyB.Rotation.ToMatrix3x3();
                PhysicsVector3 twistAxisWorld = rotB.Multiply(new PhysicsVector3(0, 1, 0)).Normalized();

                // 投影到swing平面
                PhysicsVector3 swingComponent = axis - twistAxisWorld * axis.Dot(twistAxisWorld);
                float swingAngle = swingComponent.Length();

                if (swingAngle > SwingSpan)
                {
                    PhysicsVector3 swingAxis = swingComponent.Normalized();
                    float swingError = (swingAngle - SwingSpan) * BiasFactor * Softness;

                    PhysicsVector3 angInvA = BodyA != null ? BodyA.InvInertiaWorld.Multiply(swingAxis) : PhysicsVector3.Zero;
                    PhysicsVector3 angInvB = BodyB != null ? BodyB.InvInertiaWorld.Multiply(swingAxis) : PhysicsVector3.Zero;
                    float effMass = swingAxis.Dot(angInvA) + swingAxis.Dot(angInvB);
                    if (effMass > 1e-12f)
                    {
                        float lambda = -swingError / (effMass * RelaxationFactor) + _warmStartLambdas[1] * 0.8f;
                        _warmStartLambdas[1] = lambda;

                        if (BodyA != null && !BodyA.IsStatic)
                            BodyA.AngularVelocity = BodyA.AngularVelocity + angInvA * lambda;
                        if (BodyB != null && !BodyB.IsStatic)
                            BodyB.AngularVelocity = BodyB.AngularVelocity - angInvB * lambda;
                    }
                }

                // ---- 扭转约束 ----
                float twistAngle = axis.Dot(twistAxisWorld) * angle;
                if (Math.Abs(twistAngle) > TwistSpan)
                {
                    float twistError = (twistAngle > 0 ? twistAngle - TwistSpan : twistAngle + TwistSpan)
                        * BiasFactor * Softness;

                    PhysicsVector3 angInvA = BodyA != null ? BodyA.InvInertiaWorld.Multiply(twistAxisWorld) : PhysicsVector3.Zero;
                    PhysicsVector3 angInvB = BodyB != null ? BodyB.InvInertiaWorld.Multiply(twistAxisWorld) : PhysicsVector3.Zero;
                    float effMass = twistAxisWorld.Dot(angInvA) + twistAxisWorld.Dot(angInvB);
                    if (effMass > 1e-12f)
                    {
                        float lambda = -twistError / (effMass * RelaxationFactor) + _warmStartLambdas[2] * 0.8f;
                        _warmStartLambdas[2] = lambda;

                        if (BodyA != null && !BodyA.IsStatic)
                            BodyA.AngularVelocity = BodyA.AngularVelocity + angInvA * lambda;
                        if (BodyB != null && !BodyB.IsStatic)
                            BodyB.AngularVelocity = BodyB.AngularVelocity - angInvB * lambda;
                    }
                }
            }

            _accumulatedTorque = angle / Math.Max(dt, 1e-6f);
        }

        public override void ResetAccumulators()
        {
            base.ResetAccumulators();
            for (int i = 0; i < _warmStartLambdas.Length; i++)
                _warmStartLambdas[i] = 0;
        }
    }

    #endregion

    #region ======================== 球窝关节 ========================

    /// <summary>
    ///   球窝关节（BallSocket / Point / Spherical）：允许自由旋转，仅约束位置。
    ///   两个锚点在世界空间中保持重合。
    /// </summary>
    public class BallSocketJoint : PhysicsJoint
    {
        private float _warmStartLambda;

        public BallSocketJoint(RigidBody bodyA, RigidBody bodyB,
            PhysicsVector3 localAnchorA, PhysicsVector3 localAnchorB)
            : base(JointType.BallSocket, bodyA, bodyB, localAnchorA, localAnchorB)
        {
        }

        /// <summary>求解球窝位置约束。</summary>
        public void Solve(float dt)
        {
            if (!Enabled || IsBroken) return;

            PhysicsVector3 rA = GetWorldAnchorA() - BodyA.Position;
            PhysicsVector3 rB = GetWorldAnchorB() - BodyB.Position;
            PhysicsVector3 delta = GetRelativePosition();
            float distance = delta.Length();

            if (distance < 1e-6f) return;

            PhysicsVector3 n = delta / distance;

            float invMassA = BodyA?.InvMass ?? 0;
            float invMassB = BodyB?.InvMass ?? 0;

            PhysicsVector3 rAxN = rA.Cross(n);
            PhysicsVector3 rBxN = rB.Cross(n);
            PhysicsVector3 angInvA = BodyA != null ? BodyA.InvInertiaWorld.Multiply(rAxN) : PhysicsVector3.Zero;
            PhysicsVector3 angInvB = BodyB != null ? BodyB.InvInertiaWorld.Multiply(rBxN) : PhysicsVector3.Zero;

            float effMass = invMassA + invMassB + rAxN.Dot(angInvA) + rBxN.Dot(angInvB);
            if (effMass < 1e-12f) return;

            float lambda = -distance / effMass + _warmStartLambda * 0.8f;
            _warmStartLambda = lambda;

            PhysicsVector3 impulse = n * lambda;
            if (BodyA != null && !BodyA.IsStatic)
            {
                BodyA.LinearVelocity = BodyA.LinearVelocity - impulse * invMassA;
                BodyA.AngularVelocity = BodyA.AngularVelocity - angInvA;
            }
            if (BodyB != null && !BodyB.IsStatic)
            {
                BodyB.LinearVelocity = BodyB.LinearVelocity + impulse * invMassB;
                BodyB.AngularVelocity = BodyB.AngularVelocity + angInvB;
            }

            _accumulatedForce = Math.Abs(lambda / dt);
        }

        public override void ResetAccumulators()
        {
            base.ResetAccumulators();
            _warmStartLambda = 0;
        }
    }

    /// <summary>球关节（SphericalJoint）：与BallSocketJoint相同。</summary>
    public class SphericalJoint : BallSocketJoint
    {
        public SphericalJoint(RigidBody bodyA, RigidBody bodyB,
            PhysicsVector3 localAnchorA, PhysicsVector3 localAnchorB)
            : base(bodyA, bodyB, localAnchorA, localAnchorB)
        {
        }
    }

    /// <summary>点关节（PointJoint）：与BallSocketJoint相同。</summary>
    public class PointJoint : BallSocketJoint
    {
        public PointJoint(RigidBody bodyA, RigidBody bodyB,
            PhysicsVector3 localAnchorA, PhysicsVector3 localAnchorB)
            : base(bodyA, bodyB, localAnchorA, localAnchorB)
        {
        }
    }

    #endregion

    #region ======================== Jacobian 求解器 ========================

    /// <summary>
    ///   Jacobian构建器与Sequential Impulse求解器。
    ///   支持Warm Starting（热启动），使用上一帧的拉格朗日乘数加速收敛。
    /// </summary>
    public class JacobianSolver
    {
        /// <summary>最大迭代次数（每关节）。</summary>
        public int MaxIterationsPerJoint { get; set; } = 10;

        /// <summary>全局最大求解迭代次数。</summary>
        public int MaxGlobalIterations { get; set; } = 8;

        /// <summary>Warm Starting缩放系数（0=不使用, 1=完全使用）。</summary>
        public float WarmStartFactor { get; set; } = 0.8f;

        /// <summary>Baumgarte位置校正因子。</summary>
        public float BaumgarteFactor { get; set; } = 0.2f;

        /// <summary>求解容差。</summary>
        public float SolverTolerance { get; set; } = 1e-4f;

        /// <summary>所有待求解的关节。</summary>
        private List<PhysicsJoint> _joints = new List<PhysicsJoint>();

        /// <summary>每帧求解结果（是否全部满足约束）。</summary>
        public bool LastSolveConverged { get; private set; } = true;

        /// <summary>上一帧求解迭代数。</summary>
        public int LastIterationCount { get; private set; }

        /// <summary>添加关节到求解器。</summary>
        public void AddJoint(PhysicsJoint joint)
        {
            if (joint == null) throw new ArgumentNullException(nameof(joint));
            _joints.Add(joint);
        }

        /// <summary>移除关节。</summary>
        public bool RemoveJoint(PhysicsJoint joint)
        {
            return _joints.Remove(joint);
        }

        /// <summary>获取所有关节（只读）。</summary>
        public IReadOnlyList<PhysicsJoint> Joints => _joints;

        /// <summary>清空所有关节。</summary>
        public void Clear()
        {
            _joints.Clear();
        }

        /// <summary>
        ///   构建Jacobian行。
        ///   Jacobian = [J1_linear, J1_angular, J2_linear, J2_angular]
        ///   对于位置约束：J = [n, rA x n, -n, -(rB x n)]
        /// </summary>
        public static void BuildJacobianRow(
            PhysicsVector3 worldAnchorA, PhysicsVector3 worldAnchorB,
            PhysicsVector3 bodyAPos, PhysicsVector3 bodyBPos,
            PhysicsVector3 normal,
            out PhysicsVector3 jLinA, out PhysicsVector3 jAngA,
            out PhysicsVector3 jLinB, out PhysicsVector3 jAngB,
            out float effectiveMass,
            float invMassA, float invMassB,
            PhysicsMatrix3x3 invInertiaA, PhysicsMatrix3x3 invInertiaB)
        {
            PhysicsVector3 rA = worldAnchorA - bodyAPos;
            PhysicsVector3 rB = worldAnchorB - bodyBPos;

            jLinA = normal;
            jAngA = rA.Cross(normal);
            jLinB = -normal;
            jAngB = -rB.Cross(normal);

            PhysicsVector3 tempA = invInertiaA.Multiply(jAngA);
            PhysicsVector3 tempB = invInertiaB.Multiply(jAngB);

            effectiveMass = invMassA + invMassB
                + jAngA.Dot(tempA)
                + jAngB.Dot(tempB);
        }

        /// <summary>
        ///   计算约束误差（位置 + 旋转）。
        /// </summary>
        public static float ComputeConstraintError(
            PhysicsVector3 worldAnchorA, PhysicsVector3 worldAnchorB,
            PhysicsVector3 normal)
        {
            PhysicsVector3 delta = worldAnchorA - worldAnchorB;
            return delta.Dot(normal);
        }

        /// <summary>
        ///   Sequential Impulse法求解所有关节约束。
        ///   支持Warm Starting：使用上一帧的拉格朗日乘数作为初始猜测。
        /// </summary>
        public void SolveAll(float dt)
        {
            if (_joints.Count == 0) return;

            // Warm Starting: 缩放上一帧的lambda
            // (已在每个Joint.Solve中通过_warmStartLambda * WarmStartFactor实现)

            bool converged = true;
            int totalIterations = 0;

            // 全局迭代
            for (int iter = 0; iter < MaxGlobalIterations; iter++)
            {
                converged = true;
                totalIterations++;

                float maxError = 0;

                for (int i = 0; i < _joints.Count; i++)
                {
                    PhysicsJoint joint = _joints[i];
                    if (!joint.Enabled || joint.IsBroken) continue;

                    // 先重置累积力用于断裂检测
                    joint.ResetAccumulators();

                    // 分发到具体关节求解
                    float error = SolveJointInternal(joint, dt);
                    maxError = Math.Max(maxError, Math.Abs(error));

                    // 检查断裂
                    joint.CheckBreak();
                }

                // 早停：如果所有约束误差都在容差内
                if (maxError < SolverTolerance) break;
            }

            LastSolveConverged = converged;
            LastIterationCount = totalIterations;
        }

        /// <summary>内部求解分发。</summary>
        private float SolveJointInternal(PhysicsJoint joint, float dt)
        {
            switch (joint)
            {
                case DistanceJoint dj:
                    dj.Solve(dt);
                    return dj.GetRelativePosition().Length();
                case HingeJoint hj:
                    hj.Solve(dt);
                    return hj.GetRelativePosition().Length();
                case SliderJoint sj:
                    sj.Solve(dt);
                    return sj.GetRelativePosition().Length();
                case SpringJoint spj:
                    spj.Solve(dt);
                    return spj.GetRelativePosition().Length() - spj.RestLength;
                case FixedJoint fj:
                    fj.Solve(dt);
                    return fj.GetRelativePosition().Length();
                case ConeTwistJoint ctj:
                    ctj.Solve(dt);
                    return ctj.GetRelativePosition().Length();
                case BallSocketJoint bsj:
                    bsj.Solve(dt);
                    return bsj.GetRelativePosition().Length();
                default:
                    return 0;
            }
        }

        /// <summary>
        ///   获取当前所有关节中已断裂的关节列表。
        /// </summary>
        public List<PhysicsJoint> GetBrokenJoints()
        {
            List<PhysicsJoint> broken = new List<PhysicsJoint>();
            for (int i = 0; i < _joints.Count; i++)
            {
                if (_joints[i].IsBroken)
                    broken.Add(_joints[i]);
            }
            return broken;
        }

        /// <summary>
        ///   移除所有已断裂的关节。
        /// </summary>
        public int RemoveBrokenJoints()
        {
            int count = 0;
            for (int i = _joints.Count - 1; i >= 0; i--)
            {
                if (_joints[i].IsBroken)
                {
                    _joints.RemoveAt(i);
                    count++;
                }
            }
            return count;
        }
    }

    #endregion

    #region ======================== 约束工厂 ========================

    /// <summary>
    ///   关节约束工厂：简化创建各种类型关节。
    /// </summary>
    public class JointFactory
    {
        /// <summary>创建距离关节。</summary>
        public static DistanceJoint CreateDistance(
            RigidBody bodyA, RigidBody bodyB,
            PhysicsVector3 anchorA, PhysicsVector3 anchorB,
            float minDist = 0, float maxDist = float.PositiveInfinity,
            float stiffness = 50, float damping = 5)
        {
            return new DistanceJoint(bodyA, bodyB, anchorA, anchorB, minDist, maxDist)
            {
                Stiffness = stiffness,
                Damping = damping,
            };
        }

        /// <summary>创建铰链关节。</summary>
        public static HingeJoint CreateHinge(
            RigidBody bodyA, RigidBody bodyB,
            PhysicsVector3 anchorA, PhysicsVector3 anchorB,
            PhysicsVector3 axisA, PhysicsVector3 axisB,
            float minAngle = float.NegativeInfinity,
            float maxAngle = float.PositiveInfinity,
            float motorSpeed = 0, float motorMaxTorque = 0)
        {
            return new HingeJoint(bodyA, bodyB, anchorA, anchorB, axisA, axisB)
            {
                MinAngle = minAngle,
                MaxAngle = maxAngle,
                MotorSpeed = motorSpeed,
                MotorMaxTorque = motorMaxTorque,
            };
        }

        /// <summary>创建滑动关节。</summary>
        public static SliderJoint CreateSlider(
            RigidBody bodyA, RigidBody bodyB,
            PhysicsVector3 anchorA, PhysicsVector3 anchorB,
            PhysicsVector3 axisA, PhysicsVector3 axisB,
            float minPos = float.NegativeInfinity,
            float maxPos = float.PositiveInfinity)
        {
            return new SliderJoint(bodyA, bodyB, anchorA, anchorB, axisA, axisB)
            {
                MinPosition = minPos,
                MaxPosition = maxPos,
            };
        }

        /// <summary>创建弹簧关节。</summary>
        public static SpringJoint CreateSpring(
            RigidBody bodyA, RigidBody bodyB,
            PhysicsVector3 anchorA, PhysicsVector3 anchorB,
            float stiffness = 100, float damping = 10)
        {
            return new SpringJoint(bodyA, bodyB, anchorA, anchorB, stiffness, damping);
        }

        /// <summary>创建固定关节。</summary>
        public static FixedJoint CreateFixed(
            RigidBody bodyA, RigidBody bodyB,
            PhysicsVector3 anchorA, PhysicsVector3 anchorB)
        {
            return new FixedJoint(bodyA, bodyB, anchorA, anchorB);
        }

        /// <summary>创建锥形扭转关节。</summary>
        public static ConeTwistJoint CreateConeTwist(
            RigidBody bodyA, RigidBody bodyB,
            PhysicsVector3 anchorA, PhysicsVector3 anchorB,
            float swingSpan = (float)(Math.PI / 4),
            float twistSpan = (float)(Math.PI / 4))
        {
            return new ConeTwistJoint(bodyA, bodyB, anchorA, anchorB)
            {
                SwingSpan = swingSpan,
                TwistSpan = twistSpan,
            };
        }

        /// <summary>创建球窝关节。</summary>
        public static BallSocketJoint CreateBallSocket(
            RigidBody bodyA, RigidBody bodyB,
            PhysicsVector3 anchorA, PhysicsVector3 anchorB)
        {
            return new BallSocketJoint(bodyA, bodyB, anchorA, anchorB);
        }
    }

    #endregion
}
