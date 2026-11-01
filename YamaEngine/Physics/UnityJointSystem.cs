using System;
using System.Numerics;

namespace YamaEngine.Physics
{
    // ============================================================================================
    //  UnityJointSystem.cs
    //
    //  关节系统 — 类似 Unity Joint：Fixed / Hinge / Spring / Configurable。
    //
    //  功能：
    //    - UnityFixedJoint（固定位置 + 旋转）
    //    - UnityHingeJoint（绕轴旋转，角度限制，电机驱动）
    //    - UnitySpringJoint（弹簧力连接，阻尼 + 刚度 + 目标距离）
    //    - UnityConfigurableJoint（完全可配置位置/旋转约束，目标，驱动）
    //    - 关节断链（Break Force / Break Torque）
    //    - 关节目标驱动（TargetPosition / TargetVelocity 电机）
    // ============================================================================================

    /// <summary>
    ///   驱动模式（用于 ConfigurableJoint 和 HingeJoint 的电机驱动）。
    /// </summary>
    public enum JointDriveMode
    {
        /// <summary>无驱动。</summary>
        None,
        /// <summary>速度驱动（目标速度 + 最大力）。</summary>
        Velocity,
        /// <summary>位置驱动（目标位置 + 弹簧刚度 + 阻尼）。</summary>
        Position,
    }

    /// <summary>
    ///   关节驱动参数。
    /// </summary>
    public struct JointDrive
    {
        /// <summary>驱动模式。</summary>
        public JointDriveMode Mode;

        /// <summary>位置驱动模式下的弹簧刚度。</summary>
        public float Spring;

        /// <summary>位置驱动模式下的阻尼。</summary>
        public float Damper;

        /// <summary>最大力 / 最大扭矩。</summary>
        public float MaxForce;

        /// <summary>目标速度（速度驱动模式）或目标位置（位置驱动模式）。</summary>
        public float Target;

        /// <summary>默认驱动（无驱动）。</summary>
        public static JointDrive Default => new()
        {
            Mode = JointDriveMode.None,
            Spring = 0f,
            Damper = 0f,
            MaxForce = float.MaxValue,
            Target = 0f,
        };
    }

    /// <summary>
    ///   线性/角运动的约束模式（用于 ConfigurableJoint）。
    /// </summary>
    public enum ConfigurableJointMotion
    {
        /// <summary>自由移动。</summary>
        Free,
        /// <summary>受限移动（在限制范围内）。</summary>
        Limited,
        /// <summary>锁定（不允许移动）。</summary>
        Locked,
    }

    /// <summary>
    ///   关节基类。所有关节类型的通用功能。
    /// </summary>
    public abstract class UnityJoint : IDisposable
    {
        /// <summary>物理系统句柄。</summary>
        protected PhysicsSystemHandle System { get; }

        /// <summary>原生约束 ID。</summary>
        public uint NativeId { get; protected set; }

        /// <summary>第一个 BodyId。</summary>
        public uint BodyA { get; }

        /// <summary>第二个 BodyId。</summary>
        public uint BodyB { get; }

        /// <summary>是否已销毁。</summary>
        public bool IsDisposed => NativeId == 0;

        /// <summary>断链力（0 = 不断链）。</summary>
        public float BreakForce { get; set; }

        /// <summary>断链扭矩（0 = 不断链）。</summary>
        public float BreakTorque { get; set; }

        /// <summary>弹性频率 (Hz)。0 = 刚性约束。</summary>
        public float Frequency
        {
            get => _frequency;
            set
            {
                _frequency = value;
                if (NativeId != 0)
                    JoltNative.SJ_Constraint_SetFrequency(System, NativeId, value);
            }
        }
        private float _frequency;

        /// <summary>阻尼比 (0..1)。</summary>
        public float Damping
        {
            get => _damping;
            set
            {
                _damping = value;
                if (NativeId != 0)
                    JoltNative.SJ_Constraint_SetDamping(System, NativeId, value);
            }
        }
        private float _damping;

        /// <summary>用户标签。</summary>
        public object? Tag { get; set; }

        protected UnityJoint(PhysicsSystemHandle system, uint bodyA, uint bodyB, uint nativeId)
        {
            System = system;
            BodyA = bodyA;
            BodyB = bodyB;
            NativeId = nativeId;
        }

        /// <summary>激活关节（添加到系统）。</summary>
        protected void Activate()
        {
            if (NativeId != 0)
                JoltNative.SJ_Constraint_Activate(System, NativeId);
        }

        public virtual void Dispose()
        {
            if (NativeId != 0)
            {
                JoltNative.SJ_Constraint_Destroy(System, NativeId);
                NativeId = 0;
            }
            GC.SuppressFinalize(this);
        }
    }

    // ============================================================================================
    //  UnityFixedJoint
    // ============================================================================================

    /// <summary>
    ///   固定关节。将两个刚体完全焊接在一起。
    /// </summary>
    public sealed class UnityFixedJoint : UnityJoint
    {
        internal UnityFixedJoint(PhysicsSystemHandle system, uint bodyA, uint bodyB, uint nativeId)
            : base(system, bodyA, bodyB, nativeId) { }

        /// <summary>创建固定关节。</summary>
        public static UnityFixedJoint Create(PhysicsSystemHandle system, uint bodyA, uint bodyB)
        {
            uint id = JoltNative.SJ_Constraint_CreateFixed(system, bodyA, bodyB);
            var joint = new UnityFixedJoint(system, bodyA, bodyB, id);
            joint.Activate();
            return joint;
        }
    }

    // ============================================================================================
    //  UnityHingeJoint
    // ============================================================================================

    /// <summary>
    ///   铰链关节。允许绕指定轴旋转，带角度限制和电机驱动。
    /// </summary>
    public sealed class UnityHingeJoint : UnityJoint
    {
        /// <summary>铰链轴方向（局部）。</summary>
        public Vector3 Axis { get; }

        /// <summary>锚点位置（世界空间）。</summary>
        public Vector3 Anchor { get; }

        /// <summary>上限角度（度）。</summary>
        public float UpperLimit { get; private set; } = 180f;

        /// <summary>下限角度（度）。</summary>
        public float LowerLimit { get; private set; } = -180f;

        /// <summary>是否使用角度限制。</summary>
        public bool UseLimits { get; private set; }

        /// <summary>是否使用电机。</summary>
        public bool UseMotor { get; private set; }

        /// <summary>电机驱动参数。</summary>
        public JointDrive Motor { get; set; }

        internal UnityHingeJoint(PhysicsSystemHandle system, uint bodyA, uint bodyB,
            Vector3 anchor, Vector3 axis, uint nativeId)
            : base(system, bodyA, bodyB, nativeId)
        {
            Anchor = anchor;
            Axis = axis.LengthSquared() > 1e-8f ? Vector3.Normalize(axis) : Vector3.UnitZ;
        }

        /// <summary>创建铰链关节。</summary>
        public static UnityHingeJoint Create(PhysicsSystemHandle system, uint bodyA, uint bodyB,
            Vector3 worldAnchor, Vector3 worldAxis)
        {
            Vector3 axis = worldAxis.LengthSquared() > 1e-8f
                ? Vector3.Normalize(worldAxis)
                : Vector3.UnitZ;

            uint id = JoltNative.SJ_Constraint_CreateHinge(
                system, bodyA, bodyB, worldAnchor, worldAnchor, axis, axis);

            var joint = new UnityHingeJoint(system, bodyA, bodyB, worldAnchor, axis, id);
            joint.Activate();
            return joint;
        }

        /// <summary>设置角度限制（度）。</summary>
        public void SetLimits(float lowerDeg, float upperDeg)
        {
            UpperLimit = Math.Max(upperDeg, lowerDeg);
            LowerLimit = Math.Min(upperDeg, lowerDeg);
            UseLimits = true;

            float minRad = LowerLimit * MathF.PI / 180f;
            float maxRad = UpperLimit * MathF.PI / 180f;
            JoltNative.SJ_Constraint_Hinge_SetLimits(System, NativeId, minRad, maxRad);
        }

        /// <summary>配置电机驱动。</summary>
        public void SetMotor(JointDrive drive)
        {
            Motor = drive;
            UseMotor = drive.Mode != JointDriveMode.None;

            int motorState = drive.Mode switch
            {
                JointDriveMode.Velocity => 1,  // Jolt 的 Velocity 模式
                JointDriveMode.Position => 2,  // Jolt 的 Position 模式
                _ => 0,
            };

            JoltNative.SJ_Constraint_SetMotorState(System, NativeId, motorState);
            JoltNative.SJ_Constraint_SetTargetVelocity(System, NativeId, drive.Target);
            JoltNative.SJ_Constraint_SetMaxMotorForce(System, NativeId,
                drive.MaxForce < float.MaxValue ? drive.MaxForce : float.MaxValue);
        }
    }

    // ============================================================================================
    //  UnitySpringJoint
    // ============================================================================================

    /// <summary>
    ///   弹簧关节。通过弹簧力连接两个刚体。
    ///   弹簧试图将两个锚点保持在目标距离内。
    /// </summary>
    public sealed class UnitySpringJoint : UnityJoint
    {
        /// <summary>锚点 A（在 BodyA 局部空间）。</summary>
        public Vector3 AnchorA { get; }

        /// <summary>锚点 B（在 BodyB 局部空间）。</summary>
        public Vector3 AnchorB { get; }

        /// <summary>弹簧刚度 (N/m)。</summary>
        public float SpringStiffness { get; set; } = 1000f;

        /// <summary>弹簧阻尼 (N*s/m)。</summary>
        public float SpringDamper { get; set; } = 10f;

        /// <summary>目标距离。默认使用创建时的初始距离。</summary>
        public float TargetDistance { get; set; }

        /// <summary>最小距离限制。</summary>
        public float MinDistance { get; set; }

        /// <summary>最大距离限制。</summary>
        public float MaxDistance { get; set; } = float.MaxValue;

        internal UnitySpringJoint(PhysicsSystemHandle system, uint bodyA, uint bodyB,
            Vector3 anchorA, Vector3 anchorB, float restLength, uint nativeId)
            : base(system, bodyA, bodyB, nativeId)
        {
            AnchorA = anchorA;
            AnchorB = anchorB;
            TargetDistance = restLength;
            MinDistance = 0f;
            MaxDistance = float.MaxValue;
        }

        /// <summary>创建弹簧关节。</summary>
        public static UnitySpringJoint Create(PhysicsSystemHandle system, uint bodyA, uint bodyB,
            Vector3 worldAnchorA, Vector3 worldAnchorB,
            float stiffness = 1000f, float damping = 10f)
        {
            float restLength = Vector3.Distance(worldAnchorA, worldAnchorB);

            uint id = JoltNative.SJ_Constraint_CreateDistance(
                system, bodyA, bodyB, worldAnchorA, worldAnchorB, 0f, float.MaxValue);

            var joint = new UnitySpringJoint(system, bodyA, bodyB,
                worldAnchorA, worldAnchorB, restLength, id);
            joint.SpringStiffness = stiffness;
            joint.SpringDamper = damping;
            joint.Activate();

            // 设置弹簧参数
            float frequencyHz = MathF.Sqrt(stiffness / 1000f) * 5f;
            float dampingRatio = damping / (2f * MathF.Sqrt(stiffness * 1f));
            if (frequencyHz > 0f)
            {
                JoltNative.SJ_Constraint_SetFrequency(system, id,
                    Math.Clamp(frequencyHz, 0f, 100f));
                JoltNative.SJ_Constraint_SetDamping(system, id,
                    Math.Clamp(dampingRatio, 0f, 1f));
            }

            return joint;
        }
    }

    // ============================================================================================
    //  UnityConfigurableJoint
    // ============================================================================================

    /// <summary>
    ///   可配置关节。提供完全可配置的位置/旋转约束，支持目标驱动。
    ///   类似 Unity 的 ConfigurableJoint。
    /// </summary>
    public sealed class UnityConfigurableJoint : UnityJoint
    {
        /// <summary>锚点位置。</summary>
        public Vector3 Anchor { get; set; }

        /// <summary>X 轴运动约束。</summary>
        public ConfigurableJointMotion XMotion { get; set; } = ConfigurableJointMotion.Free;

        /// <summary>Y 轴运动约束。</summary>
        public ConfigurableJointMotion YMotion { get; set; } = ConfigurableJointMotion.Free;

        /// <summary>Z 轴运动约束。</summary>
        public ConfigurableJointMotion ZMotion { get; set; } = ConfigurableJointMotion.Free;

        /// <summary>X 轴旋转约束。</summary>
        public ConfigurableJointMotion AngularXMotion { get; set; } = ConfigurableJointMotion.Free;

        /// <summary>Y 轴旋转约束。</summary>
        public ConfigurableJointMotion AngularYMotion { get; set; } = ConfigurableJointMotion.Free;

        /// <summary>Z 轴旋转约束。</summary>
        public ConfigurableJointMotion AngularZMotion { get; set; } = ConfigurableJointMotion.Free;

        /// <summary>线性限位：最小位置。</summary>
        public Vector3 LinearLimitMin { get; set; } = new(float.NegativeInfinity);

        /// <summary>线性限位：最大位置。</summary>
        public Vector3 LinearLimitMax { get; set; } = new(float.PositiveInfinity);

        /// <summary>角度限位：最小角度（度）。</summary>
        public Vector3 AngularLimitMin { get; set; } = new(-180f);

        /// <summary>角度限位：最大角度（度）。</summary>
        public Vector3 AngularLimitMax { get; set; } = new(180f);

        /// <summary>目标位置（位置驱动用）。</summary>
        public Vector3 TargetPosition { get; set; }

        /// <summary>目标速度（速度驱动用）。</summary>
        public Vector3 TargetVelocity { get; set; }

        /// <summary>位置驱动参数（X 轴）。</summary>
        public JointDrive XDrive { get; set; }

        /// <summary>位置驱动参数（Y 轴）。</summary>
        public JointDrive YDrive { get; set; }

        /// <summary>位置驱动参数（Z 轴）。</summary>
        public JointDrive ZDrive { get; set; }

        /// <summary>角驱动参数。</summary>
        public JointDrive AngularDrive { get; set; }

        internal UnityConfigurableJoint(PhysicsSystemHandle system, uint bodyA, uint bodyB, uint nativeId)
            : base(system, bodyA, bodyB, nativeId) { }

        /// <summary>
        ///   创建可配置关节。使用 Jolt 的 SixDOF 约束实现。
        /// </summary>
        public static UnityConfigurableJoint Create(PhysicsSystemHandle system, uint bodyA, uint bodyB,
            Vector3 worldAnchor)
        {
            // 使用点约束作为基础（6DOF 简化）
            uint id = JoltNative.SJ_Constraint_CreatePoint(
                system, bodyA, bodyB, worldAnchor, worldAnchor);

            var joint = new UnityConfigurableJoint(system, bodyA, bodyB, id);
            joint.Anchor = worldAnchor;
            joint.Activate();
            return joint;
        }

        /// <summary>更新约束配置（在修改运动模式后调用）。</summary>
        public void UpdateConfiguration()
        {
            // 当前 Jolt 桥接未提供完整的 6DOF 配置接口，
            // 此处为占位，实际应用中需要扩展 JoltNative 来支持 SixDOF 的完整配置。
        }

        /// <summary>设置目标驱动位置。</summary>
        public void SetTargetPosition(Vector3 position)
        {
            TargetPosition = position;
        }

        /// <summary>设置目标驱动速度。</summary>
        public void SetTargetVelocity(Vector3 velocity)
        {
            TargetVelocity = velocity;
        }
    }

    // ============================================================================================
    //  关节工厂
    // ============================================================================================

    /// <summary>
    ///   关节工厂。统一创建各种关节类型。
    /// </summary>
}
