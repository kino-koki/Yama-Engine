using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace ShanyuEngine.Physics
{
    // ============================================================================================
    //  RagdollPhysicsSystem.cs
    //
    //  布娃娃物理系统。角色死亡时用，把一个骨骼结构转成多个刚体+约束，
    //  让身体自然地软掉。这玩意儿调参很麻烦，每根骨头的长度/质量/惯性都要对得上动画骨骼。
    //
    //  核心组成:
    //    1) RagdollBone - 一根布娃娃骨头，对应一个刚体+一个胶囊碰撞体
    //    2) RagdollJoint - 关节，连接两根骨头，限制相对运动
    //    3) RagdollSystem - 整个布娃娃系统，管理所有骨头和关节
    //
    //  典型人形布娃娃的骨头:
    //    pelvis(骨盆) -> spine(脊椎) -> chest(胸) -> head(头)
    //    chest -> upper_arm_L -> lower_arm_L -> hand_L
    //    chest -> upper_arm_R -> lower_arm_R -> hand_R
    //    pelvis -> upper_leg_L -> lower_leg_L -> foot_L
    //    pelvis -> upper_leg_R -> lower_leg_R -> foot_R
    //
    //  一共13根骨头，12个关节。
    //
    //  TODO: 当前实现没接实际的物理引擎，是个纯逻辑层。后续要桥接到Jolt/Bullet。
    //  注意：骨盆质量不能为0，不然整个布娃娃会鬼畜。
    // ============================================================================================

    /// <summary>
    ///   布娃娃骨头类型。用来标识一根骨头在身体上的位置。
    /// </summary>
    public enum RagdollBoneType
    {
        Pelvis = 0,         // 骨盆(根)
        Spine = 1,          // 下脊椎
        Chest = 2,          // 上脊椎/胸腔
        UpperChest = 3,     // 上胸(可选)
        Neck = 4,           // 脖子
        Head = 5,           // 头

        UpperArmLeft = 10,  // 左大臂
        LowerArmLeft = 11,  // 左小臂
        HandLeft = 12,      // 左手

        UpperArmRight = 20, // 右大臂
        LowerArmRight = 21, // 右小臂
        HandRight = 22,     // 右手

        UpperLegLeft = 30,  // 左大腿
        LowerLegLeft = 31,  // 左小腿
        FootLeft = 32,      // 左脚

        UpperLegRight = 40, // 右大腿
        LowerLegRight = 41, // 右小腿
        FootRight = 42,     // 右脚

        Tail1 = 50,         // 尾巴(动物用)
        Tail2 = 51,
        Tail3 = 52,

        Custom = 99,
    }

    /// <summary>
    ///   关节类型。不同关节有不同的自由度限制。
    /// </summary>
    public enum RagdollJointType
    {
        /// <summary>圆锥关节。可以在一个锥形范围内摆动，但不能扭转太多。最常用。</summary>
        Cone,

        /// <summary>铰链关节。只能绕一个轴旋转。膝盖肘部用。</summary>
        Hinge,

        /// <summary>扭转关节。只能扭转。前臂旋转那种。</summary>
        Twist,

        /// <summary>球关节。3轴自由。脖子/肩部用。</summary>
        Ball,

        /// <summary>固定关节。完全不能动。死了也不动的连接。</summary>
        Fixed,

        /// <summary>通用关节。两个垂直的铰链。比较少见。</summary>
        Universal,
    }

    /// <summary>
    ///   布娃娃骨头。对应一个刚体。
    /// </summary>
    public sealed class RagdollBone
    {
        // ---- 标识 ----
        public int Id;
        public RagdollBoneType BoneType;
        public string Name = "";

        // ---- 层级 ----
        public int ParentId = -1;
        public List<int> ChildIds = new(4);

        // ---- 物理状态 ----
        public Vector3 Position;
        public Quaternion Orientation = Quaternion.Identity;
        public Vector3 LinearVelocity;
        public Vector3 AngularVelocity;
        public Vector3 Force;
        public Vector3 Torque;

        // ---- 质量属性 ----
        public float Mass = 1f;
        public float InverseMass = 1f;
        public Matrix4x4 InertiaTensor = Matrix4x4.Identity;
        public Matrix4x4 InverseInertiaTensor = Matrix4x4.Identity;
        public Matrix4x4 LocalInertiaTensor = Matrix4x4.Identity;

        // ---- 形状(简化为胶囊) ----
        public Vector3 LocalShapeOffset = Vector3.Zero;
        public float CapsuleRadius = 0.1f;
        public float CapsuleLength = 0.5f;
        public Vector3 LocalShapeAxis = Vector3.UnitY;  // 胶囊沿哪个轴

        // ---- 动画骨骼偏移 ----
        public Vector3 LocalPosition;       // 相对父骨头的位置(骨骼空间)
        public Quaternion LocalRotation;    // 相对父骨头的旋转

        // ---- 阻尼 ----
        public float LinearDamping = 0.05f;
        public float AngularDamping = 0.15f;

        // ---- 睡眠 ----
        public bool IsSleeping;
        public float SleepTimer;

        // ---- 碰撞过滤 ----
        public bool EnableSelfCollision;
        public int CollisionGroup;
        public int CollisionMask = ~0;

        // ---- 约束锚点 ----
        public Vector3 LocalAnchorParent;   // 在父骨头坐标系下的关节锚点
        public Vector3 LocalAnchorSelf;     // 在自己坐标系下的关节锚点

        // ---- 目标动画(混合用) ----
        public Quaternion AnimTargetRotation = Quaternion.Identity;
        public float AnimBlendWeight;

        // 调试用
        public bool IsHighlighted;
        public int LastContactCount;

        public bool IsRoot => ParentId < 0;

        /// <summary>获取世界空间下的胶囊端点。</summary>
        public void GetCapsuleEndpoints(out Vector3 p0, out Vector3 p1)
        {
            Vector3 center = Position + Vector3.Transform(LocalShapeOffset, Orientation);
            Vector3 axis = Vector3.Transform(LocalShapeAxis, Orientation);
            Vector3 half = axis * (CapsuleLength * 0.5f);
            p0 = center - half;
            p1 = center + half;
        }

        /// <summary>获取胶囊的AABB。</summary>
        public void GetAABB(out Vector3 min, out Vector3 max)
        {
            GetCapsuleEndpoints(out var p0, out var p1);
            float r = CapsuleRadius;
            min = Vector3.Min(p0, p1) - new Vector3(r, r, r);
            max = Vector3.Max(p0, p1) + new Vector3(r, r, r);
        }

        /// <summary>设置质量并重算惯性。</summary>
        public void SetMass(float mass)
        {
            Mass = MathF.Max(0.001f, mass);
            InverseMass = 1f / Mass;
            ComputeInertia();
        }

        /// <summary>计算胶囊的惯性张量。</summary>
        public void ComputeInertia()
        {
            // 胶囊沿Y轴的惯性张量
            float r = CapsuleRadius;
            float l = CapsuleLength;
            float m = Mass;

            // 圆柱部分
            float cylIxx = m * (3f * r * r + l * l) / 12f;
            float cylIyy = 0.5f * m * r * r;
            float cylIzz = cylIxx;

            // 两个半球部分(简化为球)
            float sphMass = m * 0.3f;  // 估算半球质量占比
            float sphI = 0.4f * sphMass * r * r;

            float ixx = cylIxx + sphI;
            float iyy = cylIyy + sphI * 0.5f;
            float izz = cylIzz + sphI;

            // 如果形状轴不是Y，要旋转惯性张量
            LocalInertiaTensor = new Matrix4x4(
                ixx, 0, 0, 0,
                0, iyy, 0, 0,
                0, 0, izz, 0,
                0, 0, 0, 1
            );

            // 旋转到形状轴方向
            if (LocalShapeAxis != Vector3.UnitY)
            {
                Vector3 axis = Vector3.Normalize(LocalShapeAxis);
                Vector3 y = Vector3.UnitY;
                Vector3 rotAxis = Vector3.Cross(y, axis);
                float rotAngle = MathF.Acos(Vector3.Dot(y, axis));
                if (rotAxis.LengthSquared() > 1e-6f)
                {
                    rotAxis = Vector3.Normalize(rotAxis);
                    Quaternion rot = Quaternion.CreateFromAxisAngle(rotAxis, rotAngle);
                    Matrix4x4 rotMat = Matrix4x4.CreateFromQuaternion(rot);
                    LocalInertiaTensor = rotMat * LocalInertiaTensor * Matrix4x4.Transpose(rotMat);
                }
            }

            UpdateWorldInertia();
        }

        /// <summary>更新世界空间惯性张量。</summary>
        public void UpdateWorldInertia()
        {
            Matrix4x4 rot = Matrix4x4.CreateFromQuaternion(Orientation);
            InertiaTensor = rot * LocalInertiaTensor * Matrix4x4.Transpose(rot);
            Matrix4x4.Invert(InertiaTensor, out InverseInertiaTensor);
        }

        /// <summary>应用力到骨头质心。</summary>
        public void ApplyForce(Vector3 force)
        {
            if (IsSleeping) return;
            Force += force;
        }

        /// <summary>应用力到世界空间某点(产生扭矩)。</summary>
        public void ApplyForceAtPoint(Vector3 force, Vector3 worldPoint)
        {
            if (IsSleeping) return;
            Force += force;
            Vector3 r = worldPoint - Position;
            Torque += Vector3.Cross(r, force);
        }

        /// <summary>应用冲量。</summary>
        public void ApplyImpulse(Vector3 impulse)
        {
            if (IsSleeping) return;
            LinearVelocity += impulse * InverseMass;
            WakeUp();
        }

        /// <summary>应用冲量到世界空间某点。</summary>
        public void ApplyImpulseAtPoint(Vector3 impulse, Vector3 worldPoint)
        {
            if (IsSleeping) return;
            LinearVelocity += impulse * InverseMass;
            Vector3 r = worldPoint - Position;
            AngularVelocity += Vector3.Transform(Vector3.Cross(r, impulse), InverseInertiaTensor);
            WakeUp();
        }

        /// <summary>应用扭矩。</summary>
        public void ApplyTorque(Vector3 torque)
        {
            if (IsSleeping) return;
            Torque += torque;
        }

        /// <summary>唤醒。</summary>
        public void WakeUp()
        {
            IsSleeping = false;
            SleepTimer = 0;
        }

        /// <summary>获取某点的线速度。</summary>
        public Vector3 GetVelocityAtPoint(Vector3 worldPoint)
        {
            Vector3 r = worldPoint - Position;
            return LinearVelocity + Vector3.Cross(AngularVelocity, r);
        }

        /// <summary>积分位置和速度。</summary>
        public void Integrate(float dt, Vector3 gravity)
        {
            if (IsSleeping)
            {
                // 睡眠中也要保持位置
                return;
            }

            // 重力
            Force += gravity * Mass;

            // 线速度积分
            LinearVelocity += Force * InverseMass * dt;

            // 阻尼
            float linDamp = MathF.Pow(1f - LinearDamping, dt * 60f);
            LinearVelocity *= linDamp;

            // 角速度积分
            Vector3 angAccel = Vector3.Transform(Torque, InverseInertiaTensor);
            AngularVelocity += angAccel * dt;

            // 角阻尼
            float angDamp = MathF.Pow(1f - AngularDamping, dt * 60f);
            AngularVelocity *= angDamp;

            // 位置积分
            Position += LinearVelocity * dt;

            // 朝向积分
            Quaternion dq = new Quaternion(
                AngularVelocity.X * dt * 0.5f,
                AngularVelocity.Y * dt * 0.5f,
                AngularVelocity.Z * dt * 0.5f,
                0f);
            Quaternion newOri = Quaternion.Normalize(Orientation + dq * Orientation);
            Orientation = newOri;

            // 清理力
            Force = Vector3.Zero;
            Torque = Vector3.Zero;

            UpdateWorldInertia();

            // 睡眠检测
            if (LinearVelocity.LengthSquared() < 0.01f && AngularVelocity.LengthSquared() < 0.05f)
            {
                SleepTimer += dt;
                if (SleepTimer > 1f)
                {
                    IsSleeping = true;
                    LinearVelocity = Vector3.Zero;
                    AngularVelocity = Vector3.Zero;
                }
            }
            else
            {
                SleepTimer = 0;
            }
        }
    }

    /// <summary>
    ///   布娃娃关节。连接两根骨头，限制相对运动。
    ///   参考: Bullet的btConeTwistConstraint, PhysX的D6Joint。
    /// </summary>
    public sealed class RagdollJoint
    {
        public int Id;
        public int BoneAId;     // 父骨头
        public int BoneBId;     // 子骨头
        public RagdollJointType Type;

        // 锚点(各自骨头局部系)
        public Vector3 LocalAnchorA;
        public Vector3 LocalAnchorB;

        // 参考朝向(B相对A的初始朝向)
        public Quaternion ReferenceRotation = Quaternion.Identity;

        // 限制参数
        public float Swing1Limit = MathF.PI * 0.5f;   // 绕X轴摆动角度限制(弧度)
        public float Swing2Limit = MathF.PI * 0.5f;   // 绕Z轴摆动角度限制
        public float TwistLimit = MathF.PI * 0.3f;    // 绕Y轴扭转限制
        public float Swing1Restitution = 0.1f;        // 摆动到极限的反弹
        public float Swing2Restitution = 0.1f;
        public float TwistRestitution = 0.1f;

        // 弹簧(让关节有回弹力，模拟肌肉张力)
        public bool EnableSwingSpring;
        public float SwingStiffness = 0f;
        public float SwingDamping = 0f;
        public Quaternion SwingTargetRotation = Quaternion.Identity;

        public bool EnableTwistSpring;
        public float TwistStiffness = 0f;
        public float TwistDamping = 0f;
        public float TwistTargetAngle = 0f;

        // 软约束(允许少量超限，求解器会拉回来)
        public float Softness = 0.1f;
        public float BiasFactor = 0.2f;
        public float RelaxationFactor = 1f;

        // 驱动(用于把关节往目标角度推，做active ragdoll)
        public bool EnableDrive;
        public float DriveStiffness = 50f;
        public float DriveDamping = 5f;
        public Quaternion DriveTargetRotation = Quaternion.Identity;

        // 求解器内部状态(每次求解清零)
        internal Vector3 _accumulatedImpulse;
        internal Vector3 _axialImpulse;
        internal Vector3 _swingImpulse;
        internal bool _isLimitViolated;

        // 摩擦
        public float Friction = 0.1f;

        // 断裂(受力太大就断)
        public bool CanBreak;
        public float BreakForce = float.MaxValue;
        public float BreakTorque = float.MaxValue;
        public bool IsBroken;

        /// <summary>检查是否需要断开。</summary>
        public bool CheckBreak(Vector3 force, Vector3 torque)
        {
            if (!CanBreak || IsBroken) return false;
            if (force.LengthSquared() > BreakForce * BreakForce)
            {
                IsBroken = true;
                return true;
            }
            if (torque.LengthSquared() > BreakTorque * BreakTorque)
            {
                IsBroken = true;
                return true;
            }
            return false;
        }
    }

    /// <summary>
    ///   布娃娃配置。定义骨头和关节的结构。
    /// </summary>
    public sealed class RagdollConfig
    {
        public List<RagdollBone> Bones = new();
        public List<RagdollJoint> Joints = new();
        public Vector3 Gravity = new(0, -9.81f, 0);
        public float TotalMass = 75f;
        public float GroundFriction = 0.6f;
        public float GroundRestitution = 0.1f;
        public float LinearSleepThreshold = 0.1f;
        public float AngularSleepThreshold = 0.2f;
        public float SleepTimeThreshold = 1f;
        public int SolverIterations = 10;
        public bool EnableSelfCollision = false;
        public float SelfCollisionBias = 0.01f;

        /// <summary>创建标准人形布娃娃。</summary>
        public static RagdollConfig CreateHumanoid(float height = 1.8f, float totalMass = 75f)
        {
            var config = new RagdollConfig
            {
                TotalMass = totalMass,
                Gravity = new Vector3(0, -9.81f, 0),
            };

            // 质量分配(近似)
            // 头 8%, 脖子 2%, 胸 20%, 脊椎 12%, 骨盆 14%
            // 大臂 3% * 2, 小臂 2% * 2, 手 1% * 2
            // 大腿 10% * 2, 小腿 5% * 2, 脚 2% * 2
            float m = totalMass;
            var bones = new (RagdollBoneType type, string name, float mass, float r, float l)[]
            {
                (RagdollBoneType.Pelvis,        "Pelvis",        m*0.14f, 0.12f, 0.25f),
                (RagdollBoneType.Spine,         "Spine",         m*0.12f, 0.10f, 0.30f),
                (RagdollBoneType.Chest,         "Chest",         m*0.20f, 0.13f, 0.35f),
                (RagdollBoneType.Neck,          "Neck",          m*0.02f, 0.05f, 0.10f),
                (RagdollBoneType.Head,          "Head",          m*0.08f, 0.11f, 0.18f),
                (RagdollBoneType.UpperArmLeft,  "UpperArmL",     m*0.03f, 0.05f, 0.28f),
                (RagdollBoneType.LowerArmLeft,  "LowerArmL",     m*0.02f, 0.04f, 0.25f),
                (RagdollBoneType.HandLeft,      "HandL",         m*0.01f, 0.05f, 0.12f),
                (RagdollBoneType.UpperArmRight, "UpperArmR",     m*0.03f, 0.05f, 0.28f),
                (RagdollBoneType.LowerArmRight, "LowerArmR",     m*0.02f, 0.04f, 0.25f),
                (RagdollBoneType.HandRight,     "HandR",         m*0.01f, 0.05f, 0.12f),
                (RagdollBoneType.UpperLegLeft,  "UpperLegL",     m*0.10f, 0.07f, 0.42f),
                (RagdollBoneType.LowerLegLeft,  "LowerLegL",     m*0.05f, 0.06f, 0.40f),
                (RagdollBoneType.FootLeft,      "FootL",         m*0.02f, 0.06f, 0.20f),
                (RagdollBoneType.UpperLegRight, "UpperLegR",     m*0.10f, 0.07f, 0.42f),
                (RagdollBoneType.LowerLegRight, "LowerLegR",     m*0.05f, 0.06f, 0.40f),
                (RagdollBoneType.FootRight,     "FootR",         m*0.02f, 0.06f, 0.20f),
            };

            int id = 0;
            foreach (var b in bones)
            {
                var bone = new RagdollBone
                {
                    Id = id++,
                    BoneType = b.type,
                    Name = b.name,
                    Mass = b.mass,
                    InverseMass = 1f / b.mass,
                    CapsuleRadius = b.r,
                    CapsuleLength = b.l,
                    LocalShapeAxis = Vector3.UnitY,
                    LinearDamping = 0.05f,
                    AngularDamping = 0.2f,
                };
                bone.ComputeInertia();
                config.Bones.Add(bone);
            }

            // 关节定义: (childType, parentType, jointType, swing1, swing2, twist, anchorOffsetY)
            var joints = new (RagdollBoneType child, RagdollBoneType parent, RagdollJointType type,
                float s1, float s2, float tw, float anchorY)[]
            {
                (RagdollBoneType.Spine,         RagdollBoneType.Pelvis,        RagdollJointType.Cone,   0.3f, 0.3f, 0.3f, 0.10f),
                (RagdollBoneType.Chest,         RagdollBoneType.Spine,         RagdollJointType.Cone,   0.2f, 0.2f, 0.2f, 0.15f),
                (RagdollBoneType.Neck,          RagdollBoneType.Chest,         RagdollJointType.Cone,   0.4f, 0.4f, 0.3f, 0.15f),
                (RagdollBoneType.Head,          RagdollBoneType.Neck,          RagdollJointType.Ball,   0.5f, 0.5f, 0.5f, 0.05f),
                (RagdollBoneType.UpperArmLeft,  RagdollBoneType.Chest,         RagdollJointType.Ball,   1.4f, 1.2f, 0.5f, 0.15f),
                (RagdollBoneType.LowerArmLeft,  RagdollBoneType.UpperArmLeft,  RagdollJointType.Hinge,  0.0f, 0.0f, 0.0f, 0.14f),
                (RagdollBoneType.HandLeft,      RagdollBoneType.LowerArmLeft,  RagdollJointType.Cone,   0.5f, 0.5f, 0.5f, 0.12f),
                (RagdollBoneType.UpperArmRight, RagdollBoneType.Chest,         RagdollJointType.Ball,   1.4f, 1.2f, 0.5f, 0.15f),
                (RagdollBoneType.LowerArmRight, RagdollBoneType.UpperArmRight, RagdollJointType.Hinge,  0.0f, 0.0f, 0.0f, 0.14f),
                (RagdollBoneType.HandRight,     RagdollBoneType.LowerArmRight, RagdollJointType.Cone,   0.5f, 0.5f, 0.5f, 0.12f),
                (RagdollBoneType.UpperLegLeft,  RagdollBoneType.Pelvis,        RagdollJointType.Ball,   0.8f, 0.6f, 0.4f, 0.10f),
                (RagdollBoneType.LowerLegLeft,  RagdollBoneType.UpperLegLeft,  RagdollJointType.Hinge,  0.0f, 0.0f, 0.0f, 0.21f),
                (RagdollBoneType.FootLeft,      RagdollBoneType.LowerLegLeft,  RagdollJointType.Cone,   0.4f, 0.4f, 0.3f, 0.20f),
                (RagdollBoneType.UpperLegRight, RagdollBoneType.Pelvis,        RagdollJointType.Ball,   0.8f, 0.6f, 0.4f, 0.10f),
                (RagdollBoneType.LowerLegRight, RagdollBoneType.UpperLegRight, RagdollJointType.Hinge,  0.0f, 0.0f, 0.0f, 0.21f),
                (RagdollBoneType.FootRight,     RagdollBoneType.LowerLegRight, RagdollJointType.Cone,   0.4f, 0.4f, 0.3f, 0.20f),
            };

            int jid = 0;
            foreach (var j in joints)
            {
                int childIdx = config.Bones.FindIndex(b => b.BoneType == j.child);
                int parentIdx = config.Bones.FindIndex(b => b.BoneType == j.parent);
                if (childIdx < 0 || parentIdx < 0) continue;

                var bone = config.Bones[childIdx];
                bone.ParentId = parentIdx;
                config.Bones[parentIdx].ChildIds.Add(childIdx);

                var joint = new RagdollJoint
                {
                    Id = jid++,
                    BoneAId = parentIdx,
                    BoneBId = childIdx,
                    Type = j.type,
                    Swing1Limit = j.s1,
                    Swing2Limit = j.s2,
                    TwistLimit = j.tw,
                    LocalAnchorA = new Vector3(0, j.anchorY, 0),
                    LocalAnchorB = new Vector3(0, -j.anchorY, 0),
                };
                config.Joints.Add(joint);
            }

            return config;
        }

        /// <summary>创建四足动物布娃娃(狗/狼那种)。</summary>
        public static RagdollConfig CreateQuadruped(float length = 1.2f, float totalMass = 30f)
        {
            var config = new RagdollConfig { TotalMass = totalMass };
            float m = totalMass;

            var bones = new (RagdollBoneType type, string name, float mass, float r, float l)[]
            {
                (RagdollBoneType.Pelvis,        "Pelvis",        m*0.20f, 0.10f, 0.20f),
                (RagdollBoneType.Spine,         "Spine",         m*0.20f, 0.09f, 0.25f),
                (RagdollBoneType.Chest,         "Chest",         m*0.20f, 0.10f, 0.30f),
                (RagdollBoneType.Neck,          "Neck",          m*0.05f, 0.06f, 0.20f),
                (RagdollBoneType.Head,          "Head",          m*0.05f, 0.08f, 0.20f),
                (RagdollBoneType.UpperArmLeft,  "FrontLegUpperL", m*0.05f, 0.04f, 0.25f),
                (RagdollBoneType.LowerArmLeft,  "FrontLegLowerL", m*0.03f, 0.035f, 0.25f),
                (RagdollBoneType.UpperArmRight, "FrontLegUpperR", m*0.05f, 0.04f, 0.25f),
                (RagdollBoneType.LowerArmRight, "FrontLegLowerR", m*0.03f, 0.035f, 0.25f),
                (RagdollBoneType.UpperLegLeft,  "BackLegUpperL",  m*0.05f, 0.04f, 0.25f),
                (RagdollBoneType.LowerLegLeft,  "BackLegLowerL",  m*0.03f, 0.035f, 0.25f),
                (RagdollBoneType.UpperLegRight, "BackLegUpperR",  m*0.05f, 0.04f, 0.25f),
                (RagdollBoneType.LowerLegRight, "BackLegLowerR",  m*0.03f, 0.035f, 0.25f),
            };

            int id = 0;
            foreach (var b in bones)
            {
                var bone = new RagdollBone
                {
                    Id = id++,
                    BoneType = b.type,
                    Name = b.name,
                    Mass = b.mass,
                    InverseMass = 1f / b.mass,
                    CapsuleRadius = b.r,
                    CapsuleLength = b.l,
                    LinearDamping = 0.08f,
                    AngularDamping = 0.25f,
                };
                bone.ComputeInertia();
                config.Bones.Add(bone);
            }

            var joints = new (RagdollBoneType child, RagdollBoneType parent, RagdollJointType type,
                float s1, float s2, float tw, float anchorY)[]
            {
                (RagdollBoneType.Spine,         RagdollBoneType.Pelvis,        RagdollJointType.Cone,  0.2f, 0.3f, 0.2f, 0.10f),
                (RagdollBoneType.Chest,         RagdollBoneType.Spine,         RagdollJointType.Cone,  0.2f, 0.3f, 0.2f, 0.12f),
                (RagdollBoneType.Neck,          RagdollBoneType.Chest,         RagdollJointType.Cone,  0.4f, 0.5f, 0.3f, 0.15f),
                (RagdollBoneType.Head,          RagdollBoneType.Neck,          RagdollJointType.Ball,  0.5f, 0.5f, 0.5f, 0.10f),
                (RagdollBoneType.UpperArmLeft,  RagdollBoneType.Chest,         RagdollJointType.Ball,  0.8f, 0.8f, 0.4f, 0.12f),
                (RagdollBoneType.LowerArmLeft,  RagdollBoneType.UpperArmLeft,  RagdollJointType.Hinge, 0, 0, 0, 0.12f),
                (RagdollBoneType.UpperArmRight, RagdollBoneType.Chest,         RagdollJointType.Ball,  0.8f, 0.8f, 0.4f, 0.12f),
                (RagdollBoneType.LowerArmRight, RagdollBoneType.UpperArmRight, RagdollJointType.Hinge, 0, 0, 0, 0.12f),
                (RagdollBoneType.UpperLegLeft,  RagdollBoneType.Pelvis,        RagdollJointType.Ball,  0.8f, 0.8f, 0.4f, 0.10f),
                (RagdollBoneType.LowerLegLeft,  RagdollBoneType.UpperLegLeft,  RagdollJointType.Hinge, 0, 0, 0, 0.12f),
                (RagdollBoneType.UpperLegRight, RagdollBoneType.Pelvis,        RagdollJointType.Ball,  0.8f, 0.8f, 0.4f, 0.10f),
                (RagdollBoneType.LowerLegRight, RagdollBoneType.UpperLegRight, RagdollJointType.Hinge, 0, 0, 0, 0.12f),
            };

            int jid = 0;
            foreach (var j in joints)
            {
                int cIdx = config.Bones.FindIndex(b => b.BoneType == j.child);
                int pIdx = config.Bones.FindIndex(b => b.BoneType == j.parent);
                if (cIdx < 0 || pIdx < 0) continue;
                var bone = config.Bones[cIdx];
                bone.ParentId = pIdx;
                config.Bones[pIdx].ChildIds.Add(cIdx);

                var joint = new RagdollJoint
                {
                    Id = jid++,
                    BoneAId = pIdx,
                    BoneBId = cIdx,
                    Type = j.type,
                    Swing1Limit = j.s1,
                    Swing2Limit = j.s2,
                    TwistLimit = j.tw,
                    LocalAnchorA = new Vector3(0, j.anchorY, 0),
                    LocalAnchorB = new Vector3(0, -j.anchorY, 0),
                };
                config.Joints.Add(joint);
            }
            return config;
        }
    }

    /// <summary>
    ///   布娃娃碰撞接触信息。
    /// </summary>
    public struct RagdollContact
    {
        public int BoneId;
        public Vector3 Point;
        public Vector3 Normal;
        public float Penetration;
        public Vector3 OtherVelocity;
        public int OtherEntityId;
        public float Friction;
        public float Restitution;
    }

    /// <summary>
    ///   布娃娃世界查询接口。负责碰撞检测和外部刚体交互。
    /// </summary>
    public interface IRagdollWorldQuery
    {
        /// <summary>胶囊扫描。</summary>
        bool SweepCapsule(Vector3 start, Quaternion orientation,
            float radius, float length, Vector3 direction, float distance,
            out RagdollContact hit);

        /// <summary>重叠查询。</summary>
        int OverlapCapsule(Vector3 center, Quaternion orientation,
            float radius, float length, RagdollContact[] results, int maxResults);

        /// <summary>对其他刚体施加冲量。</summary>
        void ApplyImpulseToBody(int bodyId, Vector3 impulse, Vector3 worldPoint);
    }

    /// <summary>
    ///   布娃娃系统。管理一整个布娃娃的所有骨头和关节。
    /// </summary>
    public sealed class RagdollPhysicsSystem : IDisposable
    {
        private RagdollConfig _config;
        private IRagdollWorldQuery? _world;
        private readonly List<RagdollBone> _bones;
        private readonly List<RagdollJoint> _joints;
        private readonly List<RagdollContact> _contacts = new(64);
        private bool _disposed;
        private float _blendWeight = 1f;     // 0=纯动画, 1=纯物理
        private bool _isActive;
        private float _elapsedTime;
        private int _stepCount;

        // 性能统计
        private float _lastStepTime;
        private int _contactCount;
        private int _solverIterations;

        public IReadOnlyList<RagdollBone> Bones => _bones;
        public IReadOnlyList<RagdollJoint> Joints => _joints;
        public IReadOnlyList<RagdollContact> Contacts => _contacts;
        public RagdollConfig Config => _config;
        public bool IsActive => _isActive;
        public float ElapsedTime => _elapsedTime;
        public int StepCount => _stepCount;
        public int ContactCount => _contactCount;
        public float LastStepTimeMs => _lastStepTime;

        public float BlendWeight
        {
            get => _blendWeight;
            set => _blendWeight = Math.Clamp(value, 0f, 1f);
        }

        public RagdollBone? Root => _bones.Count > 0 ? _bones[0] : null;

        // ========================================================================================
        //  构造
        // ========================================================================================

        public RagdollPhysicsSystem(RagdollConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _bones = new List<RagdollBone>(config.Bones.Count);
            _joints = new List<RagdollJoint>(config.Joints.Count);

            // 深拷贝
            foreach (var b in config.Bones)
            {
                var copy = new RagdollBone
                {
                    Id = b.Id,
                    BoneType = b.BoneType,
                    Name = b.Name,
                    ParentId = b.ParentId,
                    Position = b.Position,
                    Orientation = b.Orientation,
                    Mass = b.Mass,
                    InverseMass = b.InverseMass,
                    CapsuleRadius = b.CapsuleRadius,
                    CapsuleLength = b.CapsuleLength,
                    LocalShapeAxis = b.LocalShapeAxis,
                    LocalShapeOffset = b.LocalShapeOffset,
                    LocalPosition = b.LocalPosition,
                    LocalRotation = b.LocalRotation,
                    LinearDamping = b.LinearDamping,
                    AngularDamping = b.AngularDamping,
                    LocalAnchorParent = b.LocalAnchorParent,
                    LocalAnchorSelf = b.LocalAnchorSelf,
                };
                copy.ComputeInertia();
                _bones.Add(copy);
            }

            foreach (var c in config.Bones)
            {
                var b = _bones[c.Id];
                b.ChildIds.Clear();
                foreach (var cid in c.ChildIds)
                    b.ChildIds.Add(cid);
            }

            foreach (var j in config.Joints)
            {
                var copy = new RagdollJoint
                {
                    Id = j.Id,
                    BoneAId = j.BoneAId,
                    BoneBId = j.BoneBId,
                    Type = j.Type,
                    LocalAnchorA = j.LocalAnchorA,
                    LocalAnchorB = j.LocalAnchorB,
                    ReferenceRotation = j.ReferenceRotation,
                    Swing1Limit = j.Swing1Limit,
                    Swing2Limit = j.Swing2Limit,
                    TwistLimit = j.TwistLimit,
                    Swing1Restitution = j.Swing1Restitution,
                    Swing2Restitution = j.Swing2Restitution,
                    TwistRestitution = j.TwistRestitution,
                    EnableSwingSpring = j.EnableSwingSpring,
                    SwingStiffness = j.SwingStiffness,
                    SwingDamping = j.SwingDamping,
                    EnableTwistSpring = j.EnableTwistSpring,
                    TwistStiffness = j.TwistStiffness,
                    TwistDamping = j.TwistDamping,
                    Softness = j.Softness,
                    BiasFactor = j.BiasFactor,
                    RelaxationFactor = j.RelaxationFactor,
                    EnableDrive = j.EnableDrive,
                    DriveStiffness = j.DriveStiffness,
                    DriveDamping = j.DriveDamping,
                    Friction = j.Friction,
                    CanBreak = j.CanBreak,
                    BreakForce = j.BreakForce,
                    BreakTorque = j.BreakTorque,
                };
                _joints.Add(copy);
            }
        }

        public void BindWorld(IRagdollWorldQuery world)
        {
            _world = world ?? throw new ArgumentNullException(nameof(world));
        }

        // ========================================================================================
        //  激活/初始化
        // ========================================================================================

        /// <summary>
        ///   从动画骨骼姿态初始化布娃娃。
        ///   传入每根骨头的世界变换。
        /// </summary>
        public void InitializeFromPose(Dictionary<RagdollBoneType, (Vector3 pos, Quaternion rot)> pose)
        {
            foreach (var bone in _bones)
            {
                if (pose.TryGetValue(bone.BoneType, out var t))
                {
                    bone.Position = t.pos;
                    bone.Orientation = t.rot;
                    bone.LinearVelocity = Vector3.Zero;
                    bone.AngularVelocity = Vector3.Zero;
                    bone.UpdateWorldInertia();
                    bone.WakeUp();
                }
            }

            // 计算关节参考朝向
            foreach (var joint in _joints)
            {
                var a = _bones[joint.BoneAId];
                var b = _bones[joint.BoneBId];
                // B相对A的朝向
                joint.ReferenceRotation = Quaternion.Inverse(a.Orientation) * b.Orientation;
            }

            _isActive = true;
            _elapsedTime = 0;
            _stepCount = 0;
        }

        /// <summary>
        ///   给整个布娃娃施加一个初始速度(比如被爆炸冲飞)。
        /// </summary>
        public void ApplyInitialVelocity(Vector3 linear, Vector3 angular)
        {
            foreach (var b in _bones)
            {
                b.LinearVelocity = linear;
                b.AngularVelocity = angular;
                b.WakeUp();
            }
        }

        /// <summary>
        ///   在某个骨头某点施加冲量(比如被子弹打中)。
        /// </summary>
        public void ApplyImpulseAtBone(RagdollBoneType boneType, Vector3 impulse, Vector3 worldPoint)
        {
            var bone = _bones.Find(b => b.BoneType == boneType);
            if (bone == null) return;
            bone.ApplyImpulseAtPoint(impulse, worldPoint);
            // 唤醒整条链
            WakeUpChain(bone);
        }

        /// <summary>唤醒整条骨头链(从该骨头往上和往下)。</summary>
        private void WakeUpChain(RagdollBone start)
        {
            start.WakeUp();
            // 往上
            int p = start.ParentId;
            while (p >= 0)
            {
                _bones[p].WakeUp();
                p = _bones[p].ParentId;
            }
            // 往下(递归)
            WakeUpChildren(start);
        }

        private void WakeUpChildren(RagdollBone bone)
        {
            foreach (var cid in bone.ChildIds)
            {
                _bones[cid].WakeUp();
                WakeUpChildren(_bones[cid]);
            }
        }

        /// <summary>暂停(冻结所有骨头)。</summary>
        public void Pause()
        {
            _isActive = false;
            foreach (var b in _bones)
            {
                b.LinearVelocity = Vector3.Zero;
                b.AngularVelocity = Vector3.Zero;
            }
        }

        /// <summary>恢复。</summary>
        public void Resume()
        {
            _isActive = true;
            foreach (var b in _bones)
                b.WakeUp();
        }

        // ========================================================================================
        //  主更新
        // ========================================================================================

        /// <summary>
        ///   每帧更新。
        /// </summary>
        public void Update(float dt)
        {
            if (_disposed) return;
            if (!_isActive) return;
            if (dt <= 0f) return;

            var sw = System.Diagnostics.Stopwatch.StartNew();

            _elapsedTime += dt;
            _stepCount++;
            _contacts.Clear();
            _contactCount = 0;
            _solverIterations = _config.SolverIterations;

            // 1. 应用外力(重力等)
            ApplyForces(dt);

            // 2. 积分速度
            IntegrateVelocities(dt);

            // 3. 碰撞检测
            DetectCollisions();

            // 4. 约束求解(多次迭代)
            for (int i = 0; i < _config.SolverIterations; i++)
            {
                SolveJoints(dt, i);
                SolveContacts(dt);
            }

            // 5. 积分位置
            IntegratePositions(dt);

            // 6. 更新派生数据
            UpdateDerivedData();

            // 7. 睡眠检测
            UpdateSleep(dt);

            sw.Stop();
            _lastStepTime = (float)sw.Elapsed.TotalMilliseconds;
        }

        // ========================================================================================
        //  力
        // ========================================================================================

        private void ApplyForces(float dt)
        {
            // 重力
            foreach (var b in _bones)
            {
                if (b.IsSleeping) continue;
                b.ApplyForce(_config.Gravity * b.Mass);
            }

            // 关节弹簧力(active ragdoll用)
            foreach (var j in _joints)
            {
                if (j.IsBroken) continue;
                if (j.EnableDrive)
                    ApplyJointDrive(j, dt);
                if (j.EnableSwingSpring)
                    ApplySwingSpring(j, dt);
                if (j.EnableTwistSpring)
                    ApplyTwistSpring(j, dt);
            }
        }

        private void ApplyJointDrive(RagdollJoint j, float dt)
        {
            var a = _bones[j.BoneAId];
            var b = _bones[j.BoneBId];
            if (a.IsSleeping && b.IsSleeping) return;

            // 目标:把b相对a的朝向驱动到DriveTargetRotation
            Quaternion curRel = Quaternion.Inverse(a.Orientation) * b.Orientation;
            Quaternion diff = Quaternion.Inverse(j.DriveTargetRotation) * curRel;
            // 转成axis-angle
            Vector3 axis;
            float angle;
            ToAxisAngle(diff, out axis, out angle);

            Vector3 torque = axis * (angle * j.DriveStiffness);
            // 阻尼
            Vector3 relAngVel = b.AngularVelocity - a.AngularVelocity;
            torque -= relAngVel * j.DriveDamping;

            a.ApplyTorque(-torque * 0.5f);
            b.ApplyTorque(torque * 0.5f);
        }

        private void ApplySwingSpring(RagdollJoint j, float dt)
        {
            var a = _bones[j.BoneAId];
            var b = _bones[j.BoneBId];
            Quaternion curRel = Quaternion.Inverse(a.Orientation) * b.Orientation;
            Quaternion diff = Quaternion.Inverse(j.SwingTargetRotation) * curRel;
            // 只取swing部分(去掉twist)
            Vector3 axis;
            float angle;
            ToAxisAngle(diff, out axis, out angle);
            // 把axis投影到垂直于参考Y轴的平面
            Vector3 refY = Vector3.Transform(Vector3.UnitY, j.ReferenceRotation);
            Vector3 swingAxis = axis - refY * Vector3.Dot(axis, refY);
            if (swingAxis.LengthSquared() < 1e-6f) return;
            swingAxis = Vector3.Normalize(swingAxis);

            Vector3 torque = swingAxis * (angle * j.SwingStiffness);
            Vector3 relAngVel = b.AngularVelocity - a.AngularVelocity;
            torque -= relAngVel * j.SwingDamping;

            a.ApplyTorque(-torque * 0.5f);
            b.ApplyTorque(torque * 0.5f);
        }

        private void ApplyTwistSpring(RagdollJoint j, float dt)
        {
            var a = _bones[j.BoneAId];
            var b = _bones[j.BoneBId];
            Quaternion curRel = Quaternion.Inverse(a.Orientation) * b.Orientation;
            // 算twist角度
            float twist = ComputeTwistAngle(curRel, j.ReferenceRotation);
            float diff = twist - j.TwistTargetAngle;
            Vector3 refY = Vector3.Transform(Vector3.UnitY, a.Orientation);
            Vector3 torque = refY * (diff * j.TwistStiffness);
            Vector3 relAngVel = b.AngularVelocity - a.AngularVelocity;
            torque -= relAngVel * j.TwistDamping;

            a.ApplyTorque(-torque * 0.5f);
            b.ApplyTorque(torque * 0.5f);
        }

        // ========================================================================================
        //  速度积分
        // ========================================================================================

        private void IntegrateVelocities(float dt)
        {
            foreach (var b in _bones)
            {
                if (b.IsSleeping) continue;
                b.Integrate(dt, Vector3.Zero); // 重力已经在ApplyForces里加了
            }
        }

        // ========================================================================================
        //  碰撞检测
        // ========================================================================================

        private void DetectCollisions()
        {
            if (_world == null) return;
            _contacts.Clear();

            // 1. 和外部世界碰撞
            foreach (var b in _bones)
            {
                if (b.IsSleeping) continue;
                DetectBoneWorldCollision(b);
            }

            // 2. 自碰撞(可选)
            if (_config.EnableSelfCollision)
            {
                DetectSelfCollisions();
            }

            _contactCount = _contacts.Count;
        }

        private void DetectBoneWorldCollision(RagdollBone bone)
        {
            bone.GetCapsuleEndpoints(out var p0, out var p1);
            Vector3 center = (p0 + p1) * 0.5f;
            Vector3 axis = p1 - p0;
            float length = axis.Length();
            if (length > 1e-6f) axis /= length;
            else axis = Vector3.UnitY;

            Quaternion capRot = QuaternionLookAt(axis);
            float halfLen = length * 0.5f;

            // 用胶囊扫描检测接触(向下小距离)
            // 简化: 直接overlap
            var results = new RagdollContact[8];
            int n = _world!.OverlapCapsule(center, capRot, bone.CapsuleRadius, length, results, results.Length);

            for (int i = 0; i < n; i++)
            {
                var c = results[i];
                c.BoneId = bone.Id;
                c.Friction = _config.GroundFriction;
                c.Restitution = _config.GroundRestitution;
                _contacts.Add(c);
                bone.LastContactCount++;
            }
        }

        private void DetectSelfCollisions()
        {
            // O(n^2) 遍历，但是布娃娃骨头数量不多(10-20)
            for (int i = 0; i < _bones.Count; i++)
            {
                for (int j = i + 1; j < _bones.Count; j++)
                {
                    var a = _bones[i];
                    var b = _bones[j];
                    if (a.IsSleeping && b.IsSleeping) continue;

                    // 跳过相邻骨头(已经有关节约束了)
                    if (a.ParentId == b.Id || b.ParentId == a.Id) continue;
                    // 跳过同一collisiongroup的
                    if (a.CollisionGroup != 0 && a.CollisionGroup == b.CollisionGroup) continue;

                    if (CapsuleCapsuleOverlap(a, b, out var contact))
                    {
                        contact.BoneId = a.Id;
                        contact.Friction = _config.GroundFriction * 0.5f;
                        contact.Restitution = 0f;
                        _contacts.Add(contact);
                    }
                }
            }
        }

        /// <summary>胶囊-胶囊重叠检测。</summary>
        private bool CapsuleCapsuleOverlap(RagdollBone a, RagdollBone b, out RagdollContact contact)
        {
            contact = default;
            a.GetCapsuleEndpoints(out var a0, out var a1);
            b.GetCapsuleEndpoints(out var b0, out var b1);

            // 算两条线段最近点
            Vector3 u = a1 - a0;
            Vector3 v = b1 - b0;
            Vector3 w = a0 - b0;
            float ua = Vector3.Dot(u, u);
            float ub = Vector3.Dot(u, v);
            float uc = Vector3.Dot(u, w);
            float vb = Vector3.Dot(v, v);
            float vc = Vector3.Dot(v, w);

            float denom = ua * vb - ub * ub;
            float s, t;
            if (MathF.Abs(denom) < 1e-8f)
            {
                s = 0; t = Math.Clamp(vc / vb, 0, 1);
            }
            else
            {
                s = Math.Clamp((ub * vc - vb * uc) / denom, 0, 1);
                t = (ub * s - vc) / vb;
                if (t < 0) { t = 0; s = Math.Clamp(-uc / ua, 0, 1); }
                else if (t > 1) { t = 1; s = Math.Clamp((ub - uc) / ua, 0, 1); }
            }

            Vector3 pa = a0 + u * s;
            Vector3 pb = b0 + v * t;
            Vector3 diff = pa - pb;
            float dist = diff.Length();
            float minDist = a.CapsuleRadius + b.CapsuleRadius + _config.SelfCollisionBias;

            if (dist >= minDist) return false;

            Vector3 normal = dist > 1e-6f ? diff / dist : Vector3.UnitY;
            contact = new RagdollContact
            {
                BoneId = a.Id,
                Point = (pa + pb) * 0.5f,
                Normal = normal,
                Penetration = minDist - dist,
                OtherEntityId = b.Id,
                OtherVelocity = b.GetVelocityAtPoint(pb),
                Friction = _config.GroundFriction * 0.3f,
                Restitution = 0f,
            };
            return true;
        }

        // ========================================================================================
        //  约束求解
        // ========================================================================================

        private void SolveJoints(float dt, int iter)
        {
            foreach (var j in _joints)
            {
                if (j.IsBroken) continue;
                SolveJoint(j, dt, iter);
            }
        }

        private void SolveJoint(RagdollJoint j, float dt, int iter)
        {
            var a = _bones[j.BoneAId];
            var b = _bones[j.BoneBId];
            if (a.IsSleeping && b.IsSleeping) return;

            // 1. 位置约束: 让两个锚点对齐
            SolveJointPosition(j, a, b, dt);

            // 2. 角度限制
            switch (j.Type)
            {
                case RagdollJointType.Cone:
                    SolveConeLimit(j, a, b, dt);
                    break;
                case RagdollJointType.Hinge:
                    SolveHingeLimit(j, a, b, dt);
                    break;
                case RagdollJointType.Twist:
                    SolveTwistLimit(j, a, b, dt);
                    break;
                case RagdollJointType.Ball:
                    // Ball没限制
                    break;
                case RagdollJointType.Fixed:
                    SolveFixedRotation(j, a, b, dt);
                    break;
                case RagdollJointType.Universal:
                    SolveUniversalLimit(j, a, b, dt);
                    break;
            }
        }

        private void SolveJointPosition(RagdollJoint j, RagdollBone a, RagdollBone b, float dt)
        {
            // 锚点世界位置
            Vector3 aw = a.Position + Vector3.Transform(j.LocalAnchorA, a.Orientation);
            Vector3 bw = b.Position + Vector3.Transform(j.LocalAnchorB, b.Orientation);
            Vector3 diff = bw - aw;

            if (diff.LengthSquared() < 1e-8f) return;

            Vector3 normal = Vector3.Normalize(diff);
            float penetration = diff.Length();

            // 相对速度
            Vector3 rA = aw - a.Position;
            Vector3 rB = bw - b.Position;
            Vector3 vA = a.LinearVelocity + Vector3.Cross(a.AngularVelocity, rA);
            Vector3 vB = b.LinearVelocity + Vector3.Cross(b.AngularVelocity, rB);
            float relVel = Vector3.Dot(vB - vA, normal);

            // 有效质量
            float invMassSum = a.InverseMass + b.InverseMass;
            Vector3 iA = Vector3.Cross(Vector3.Transform(Vector3.Cross(rA, normal), a.InverseInertiaTensor), rA);
            Vector3 iB = Vector3.Cross(Vector3.Transform(Vector3.Cross(rB, normal), b.InverseInertiaTensor), rB);
            float denom = invMassSum + Vector3.Dot(iA + iB, normal);
            if (denom < 1e-8f) return;

            // Baumgarte位置修正
            float bias = -penetration * 0.2f / MathF.Max(dt, 1f / 60f);
            float lambda = -(relVel + bias) / denom;

            Vector3 impulse = normal * lambda;
            a.ApplyImpulseAtPoint(-impulse, aw);
            b.ApplyImpulseAtPoint(impulse, bw);
        }

        private void SolveConeLimit(RagdollJoint j, RagdollBone a, RagdollBone b, float dt)
        {
            // B相对A的朝向
            Quaternion qRel = Quaternion.Inverse(a.Orientation) * b.Orientation;
            Quaternion qDiff = Quaternion.Inverse(j.ReferenceRotation) * qRel;

            // 把B的Y轴(在A坐标系下)和参考Y轴比较
            Vector3 bY = Vector3.Transform(Vector3.UnitY, qRel);
            Vector3 refY = Vector3.Transform(Vector3.UnitY, j.ReferenceRotation);

            // bY相对refY的角度
            float dot = Math.Clamp(Vector3.Dot(bY, refY), -1f, 1f);
            float angle = MathF.Acos(dot);

            // 用最小swing限制
            float limit = Math.Min(j.Swing1Limit, j.Swing2Limit);
            if (angle <= limit) return;

            // 超限了，算约束轴
            Vector3 axis = Vector3.Cross(refY, bY);
            if (axis.LengthSquared() < 1e-8f) return;
            axis = Vector3.Normalize(axis);

            // 转到世界空间
            Vector3 worldAxis = Vector3.Transform(axis, a.Orientation);

            // 相对角速度
            Vector3 relAngVel = b.AngularVelocity - a.AngularVelocity;
            float relVel = Vector3.Dot(relAngVel, worldAxis);

            // 有效惯性
            Vector3 iA = Vector3.Transform(worldAxis, a.InverseInertiaTensor);
            Vector3 iB = Vector3.Transform(worldAxis, b.InverseInertiaTensor);
            float denom = Vector3.Dot(iA, worldAxis) + Vector3.Dot(iB, worldAxis);
            if (denom < 1e-8f) return;

            float bias = (angle - limit) * 0.2f / MathF.Max(dt, 1f / 60f);
            float lambda = -(relVel + bias) / denom;
            lambda = Math.Min(0, lambda);  // 只能拉回来，不能推过去

            Vector3 torque = worldAxis * lambda;
            a.AngularVelocity -= Vector3.Transform(torque, a.InverseInertiaTensor) * 0.5f;
            b.AngularVelocity += Vector3.Transform(torque, b.InverseInertiaTensor) * 0.5f;
        }

        private void SolveHingeLimit(RagdollJoint j, RagdollBone a, RagdollBone b, float dt)
        {
            // Hinge: 只允许绕一个轴旋转，限制另外两个轴的旋转
            Quaternion qRel = Quaternion.Inverse(a.Orientation) * b.Orientation;
            Quaternion qDiff = Quaternion.Inverse(j.ReferenceRotation) * qRel;

            // 允许的旋转轴(假设是Y)
            Vector3 allowedAxis = Vector3.Transform(Vector3.UnitY, a.Orientation);

            // 把qDiff转成axis-angle
            Vector3 axis;
            float angle;
            ToAxisAngle(qDiff, out axis, out angle);

            // 投影到不允许的轴(X和Z)
            Vector3 worldAxis = Vector3.Transform(axis, a.Orientation);
            Vector3 proj = worldAxis - allowedAxis * Vector3.Dot(worldAxis, allowedAxis);
            if (proj.LengthSquared() < 1e-8f) return;
            proj = Vector3.Normalize(proj);

            Vector3 relAngVel = b.AngularVelocity - a.AngularVelocity;
            float relVel = Vector3.Dot(relAngVel, proj);

            Vector3 iA = Vector3.Transform(proj, a.InverseInertiaTensor);
            Vector3 iB = Vector3.Transform(proj, b.InverseInertiaTensor);
            float denom = Vector3.Dot(iA, proj) + Vector3.Dot(iB, proj);
            if (denom < 1e-8f) return;

            float bias = angle * 0.2f / MathF.Max(dt, 1f / 60f);
            float lambda = -(relVel + bias) / denom;

            Vector3 torque = proj * lambda;
            a.AngularVelocity -= Vector3.Transform(torque, a.InverseInertiaTensor);
            b.AngularVelocity += Vector3.Transform(torque, b.InverseInertiaTensor);
        }

        private void SolveTwistLimit(RagdollJoint j, RagdollBone a, RagdollBone b, float dt)
        {
            float twist = ComputeTwistAngle(
                Quaternion.Inverse(a.Orientation) * b.Orientation,
                j.ReferenceRotation);

            if (MathF.Abs(twist) <= j.TwistLimit) return;

            float excess = twist - Math.Sign(twist) * j.TwistLimit;
            Vector3 axis = Vector3.Transform(Vector3.UnitY, a.Orientation);

            Vector3 relAngVel = b.AngularVelocity - a.AngularVelocity;
            float relVel = Vector3.Dot(relAngVel, axis);

            Vector3 iA = Vector3.Transform(axis, a.InverseInertiaTensor);
            Vector3 iB = Vector3.Transform(axis, b.InverseInertiaTensor);
            float denom = Vector3.Dot(iA, axis) + Vector3.Dot(iB, axis);
            if (denom < 1e-8f) return;

            float bias = -excess * 0.2f / MathF.Max(dt, 1f / 60f);
            float lambda = -(relVel + bias) / denom;
            lambda = Math.Sign(twist) > 0 ? Math.Min(0, lambda) : Math.Max(0, lambda);

            Vector3 torque = axis * lambda;
            a.AngularVelocity -= Vector3.Transform(torque, a.InverseInertiaTensor);
            b.AngularVelocity += Vector3.Transform(torque, b.InverseInertiaTensor);
        }

        private void SolveFixedRotation(RagdollJoint j, RagdollBone a, RagdollBone b, float dt)
        {
            // 把b相对a的朝向驱动回参考朝向
            Quaternion qRel = Quaternion.Inverse(a.Orientation) * b.Orientation;
            Quaternion qDiff = Quaternion.Inverse(j.ReferenceRotation) * qRel;

            Vector3 axis;
            float angle;
            ToAxisAngle(qDiff, out axis, out angle);
            if (angle < 1e-4f) return;

            Vector3 worldAxis = Vector3.Transform(axis, a.Orientation);
            Vector3 relAngVel = b.AngularVelocity - a.AngularVelocity;
            float relVel = Vector3.Dot(relAngVel, worldAxis);

            Vector3 iA = Vector3.Transform(worldAxis, a.InverseInertiaTensor);
            Vector3 iB = Vector3.Transform(worldAxis, b.InverseInertiaTensor);
            float denom = Vector3.Dot(iA, worldAxis) + Vector3.Dot(iB, worldAxis);
            if (denom < 1e-8f) return;

            float bias = angle * 0.3f / MathF.Max(dt, 1f / 60f);
            float lambda = -(relVel + bias) / denom;

            Vector3 torque = worldAxis * lambda;
            a.AngularVelocity -= Vector3.Transform(torque, a.InverseInertiaTensor);
            b.AngularVelocity += Vector3.Transform(torque, b.InverseInertiaTensor);
        }

        private void SolveUniversalLimit(RagdollJoint j, RagdollBone a, RagdollBone b, float dt)
        {
            // 简化: 当成两个独立的Hinge
            SolveHingeLimit(j, a, b, dt);
        }

        // ========================================================================================
        //  接触求解
        // ========================================================================================

        private void SolveContacts(float dt)
        {
            foreach (var c in _contacts)
            {
                SolveContact(c, dt);
            }
        }

        private void SolveContact(RagdollContact c, float dt)
        {
            var bone = _bones[c.BoneId];
            if (bone.IsSleeping) return;

            Vector3 r = c.Point - bone.Position;
            Vector3 pointVel = bone.LinearVelocity + Vector3.Cross(bone.AngularVelocity, r);
            float relVel = Vector3.Dot(pointVel - c.OtherVelocity, c.Normal);

            // 只处理接近的接触
            if (relVel > 0.1f) return;  // 分离中

            // 有效质量
            Vector3 raxn = Vector3.Cross(r, c.Normal);
            Vector3 iA = Vector3.Transform(raxn, bone.InverseInertiaTensor);
            float denom = bone.InverseMass + Vector3.Dot(Vector3.Cross(iA, r), c.Normal);
            if (denom < 1e-8f) return;

            // 法向冲量
            float restitution = relVel < -0.5f ? c.Restitution : 0f;
            float bias = -c.Penetration * 0.2f / MathF.Max(dt, 1f / 60f);
            float lambda = -(relVel * (1f + restitution) + bias) / denom;
            lambda = Math.Max(0, lambda);

            Vector3 impulse = c.Normal * lambda;
            bone.ApplyImpulseAtPoint(impulse, c.Point);

            // 摩擦冲量
            Vector3 tangent = pointVel - c.Normal * relVel;
            if (tangent.LengthSquared() < 1e-6f) return;
            tangent = Vector3.Normalize(tangent);

            Vector3 raxt = Vector3.Cross(r, tangent);
            Vector3 iAt = Vector3.Transform(raxt, bone.InverseInertiaTensor);
            float denomT = bone.InverseMass + Vector3.Dot(Vector3.Cross(iAt, r), tangent);
            if (denomT < 1e-8f) return;

            float lambdaT = -Vector3.Dot(pointVel - c.OtherVelocity, tangent) / denomT;
            float maxFriction = lambda * c.Friction;
            lambdaT = Math.Clamp(lambdaT, -maxFriction, maxFriction);

            Vector3 frictionImpulse = tangent * lambdaT;
            bone.ApplyImpulseAtPoint(frictionImpulse, c.Point);
        }

        // ========================================================================================
        //  位置积分
        // ========================================================================================

        private void IntegratePositions(float dt)
        {
            foreach (var b in _bones)
            {
                if (b.IsSleeping) continue;

                b.Position += b.LinearVelocity * dt;

                Quaternion dq = new Quaternion(
                    b.AngularVelocity.X * dt * 0.5f,
                    b.AngularVelocity.Y * dt * 0.5f,
                    b.AngularVelocity.Z * dt * 0.5f,
                    0f);
                b.Orientation = Quaternion.Normalize(b.Orientation + dq * b.Orientation);
            }
        }

        private void UpdateDerivedData()
        {
            foreach (var b in _bones)
            {
                b.UpdateWorldInertia();
            }
        }

        private void UpdateSleep(float dt)
        {
            bool allSleeping = true;
            foreach (var b in _bones)
            {
                if (b.IsSleeping) continue;
                if (b.LinearVelocity.LengthSquared() < _config.LinearSleepThreshold * _config.LinearSleepThreshold &&
                    b.AngularVelocity.LengthSquared() < _config.AngularSleepThreshold * _config.AngularSleepThreshold)
                {
                    b.SleepTimer += dt;
                    if (b.SleepTimer > _config.SleepTimeThreshold)
                    {
                        b.IsSleeping = true;
                        b.LinearVelocity = Vector3.Zero;
                        b.AngularVelocity = Vector3.Zero;
                    }
                    else
                    {
                        allSleeping = false;
                    }
                }
                else
                {
                    b.SleepTimer = 0;
                    allSleeping = false;
                }
            }

            if (allSleeping)
            {
                _isActive = false;
            }
        }

        // ========================================================================================
        //  工具函数
        // ========================================================================================

        private static void ToAxisAngle(Quaternion q, out Vector3 axis, out float angle)
        {
            q = Quaternion.Normalize(q);
            float w = Math.Clamp(q.W, -1f, 1f);
            angle = 2f * MathF.Acos(Math.Abs(w));
            float s = MathF.Sqrt(Math.Max(0f, 1f - w * w));
            if (s < 1e-6f)
            {
                axis = Vector3.UnitX;
                angle = 0;
                return;
            }
            axis = new Vector3(q.X, q.Y, q.Z) / s;
            if (w < 0) angle = -angle;
        }

        private static float ComputeTwistAngle(Quaternion qRel, Quaternion reference)
        {
            Quaternion qDiff = Quaternion.Inverse(reference) * qRel;
            // twist = 绕Y轴的旋转
            Vector3 axis;
            float angle;
            ToAxisAngle(qDiff, out axis, out angle);
            // 投影到Y轴
            return angle * axis.Y;
        }

        private static Quaternion QuaternionLookAt(Vector3 direction)
        {
            if (direction.LengthSquared() < 1e-6f) return Quaternion.Identity;
            direction = Vector3.Normalize(direction);
            Vector3 forward = Vector3.UnitY;
            float dot = Vector3.Dot(forward, direction);
            if (dot > 0.9999f) return Quaternion.Identity;
            if (dot < -0.9999f)
                return Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI);
            Vector3 axis = Vector3.Cross(forward, direction);
            axis = Vector3.Normalize(axis);
            float angle = MathF.Acos(dot);
            return Quaternion.CreateFromAxisAngle(axis, angle);
        }

        // ========================================================================================
        //  公开API
        // ========================================================================================

        /// <summary>获取骨头。</summary>
        public RagdollBone? GetBone(RagdollBoneType type)
        {
            return _bones.Find(b => b.BoneType == type);
        }

        /// <summary>获取骨头。</summary>
        public RagdollBone? GetBone(int id)
        {
            return id >= 0 && id < _bones.Count ? _bones[id] : null;
        }

        /// <summary>获取关节。</summary>
        public RagdollJoint? GetJoint(int id)
        {
            return id >= 0 && id < _joints.Count ? _joints[id] : null;
        }

        /// <summary>获取两根骨头之间的关节。</summary>
        public RagdollJoint? GetJoint(int boneAId, int boneBId)
        {
            return _joints.Find(j =>
                (j.BoneAId == boneAId && j.BoneBId == boneBId) ||
                (j.BoneAId == boneBId && j.BoneBId == boneAId));
        }

        /// <summary>获取整个布娃娃的中心位置(骨盆位置)。</summary>
        public Vector3 GetCenter()
        {
            return Root?.Position ?? Vector3.Zero;
        }

        /// <summary>获取整个布娃娃的平均速度。</summary>
        public Vector3 GetAverageVelocity()
        {
            if (_bones.Count == 0) return Vector3.Zero;
            Vector3 sum = Vector3.Zero;
            float totalMass = 0;
            foreach (var b in _bones)
            {
                sum += b.LinearVelocity * b.Mass;
                totalMass += b.Mass;
            }
            return totalMass > 0 ? sum / totalMass : Vector3.Zero;
        }

        /// <summary>获取AABB。</summary>
        public void GetAABB(out Vector3 min, out Vector3 max)
        {
            min = new Vector3(float.MaxValue);
            max = new Vector3(float.MinValue);
            foreach (var b in _bones)
            {
                b.GetAABB(out var bmin, out var bmax);
                min = Vector3.Min(min, bmin);
                max = Vector3.Max(max, bmax);
            }
        }

        /// <summary>获取所有骨头的姿态(用于动画系统)。</summary>
        public Dictionary<RagdollBoneType, (Vector3 pos, Quaternion rot)> GetPose()
        {
            var result = new Dictionary<RagdollBoneType, (Vector3, Quaternion)>(_bones.Count);
            foreach (var b in _bones)
            {
                result[b.BoneType] = (b.Position, b.Orientation);
            }
            return result;
        }

        /// <summary>设置目标动画姿态(用于active ragdoll混合)。</summary>
        public void SetAnimTarget(Dictionary<RagdollBoneType, Quaternion> targetRotations, float weight)
        {
            foreach (var b in _bones)
            {
                if (targetRotations.TryGetValue(b.BoneType, out var rot))
                {
                    b.AnimTargetRotation = rot;
                    b.AnimBlendWeight = weight;
                }
            }

            // 设置关节驱动目标
            foreach (var j in _joints)
            {
                var a = _bones[j.BoneAId];
                var b = _bones[j.BoneBId];
                if (targetRotations.TryGetValue(a.BoneType, out var rotA) &&
                    targetRotations.TryGetValue(b.BoneType, out var rotB))
                {
                    j.DriveTargetRotation = Quaternion.Inverse(rotA) * rotB;
                    j.EnableDrive = weight > 0.01f;
                }
            }
        }

        /// <summary>断开某个关节(模拟断肢)。</summary>
        public bool BreakJoint(int jointId)
        {
            var j = GetJoint(jointId);
            if (j == null) return false;
            j.IsBroken = true;
            return true;
        }

        /// <summary>重新连接所有断开的关节。</summary>
        public void RepairAllJoints()
        {
            foreach (var j in _joints)
                j.IsBroken = false;
        }

        /// <summary>把布娃娃应用到某个外部刚体(把布娃娃作为一个整体推其他物体)。</summary>
        public void ApplyImpulseToExternal(int bodyId, Vector3 impulse, Vector3 worldPoint)
        {
            _world?.ApplyImpulseToBody(bodyId, impulse, worldPoint);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _bones.Clear();
            _joints.Clear();
            _contacts.Clear();
            _world = null;
        }
    }

    // ============================================================================================
    //  布娃娃管理器
    //  一个场景里可能有多个布娃娃(比如一群怪死后都是布娃娃)。
    // ============================================================================================

    /// <summary>
    ///   布娃娃管理器。
    /// </summary>
    public sealed class RagdollManager : IDisposable
    {
        private readonly Dictionary<int, RagdollPhysicsSystem> _ragdolls = new();
        private readonly List<RagdollPhysicsSystem> _activeList = new();
        private IRagdollWorldQuery? _world;
        private int _nextId = 1;
        private int _maxActiveRagdolls = 32;
        private float _cleanupTimer;
        private float _cleanupInterval = 5f;

        public int Count => _ragdolls.Count;
        public int ActiveCount => _activeList.Count;
        public int MaxActiveRagdolls
        {
            get => _maxActiveRagdolls;
            set => _maxActiveRagdolls = Math.Max(1, value);
        }

        public void BindWorld(IRagdollWorldQuery world)
        {
            _world = world;
            foreach (var r in _activeList)
                r.BindWorld(world);
        }

        /// <summary>创建一个布娃娃。</summary>
        public int Create(RagdollConfig config)
        {
            int id = _nextId++;
            var ragdoll = new RagdollPhysicsSystem(config);
            if (_world != null)
                ragdoll.BindWorld(_world);
            _ragdolls[id] = ragdoll;
            _activeList.Add(ragdoll);

            // 超过最大数量，移除最老的
            while (_activeList.Count > _maxActiveRagdolls)
            {
                var oldest = _activeList[0];
                _activeList.RemoveAt(0);
                // 从字典里也移除
                foreach (var kvp in _ragdolls)
                {
                    if (kvp.Value == oldest)
                    {
                        _ragdolls.Remove(kvp.Key);
                        break;
                    }
                }
                oldest.Dispose();
            }
            return id;
        }

        /// <summary>获取布娃娃。</summary>
        public RagdollPhysicsSystem? Get(int id)
        {
            return _ragdolls.TryGetValue(id, out var r) ? r : null;
        }

        /// <summary>移除布娃娃。</summary>
        public bool Remove(int id)
        {
            if (_ragdolls.TryGetValue(id, out var r))
            {
                _ragdolls.Remove(id);
                _activeList.Remove(r);
                r.Dispose();
                return true;
            }
            return false;
        }

        /// <summary>更新所有布娃娃。</summary>
        public void UpdateAll(float dt)
        {
            foreach (var r in _activeList)
            {
                if (r.IsActive)
                    r.Update(dt);
            }

            // 定期清理不活跃的
            _cleanupTimer += dt;
            if (_cleanupTimer > _cleanupInterval)
            {
                _cleanupTimer = 0;
                CleanupInactive();
            }
        }

        private void CleanupInactive()
        {
            for (int i = _activeList.Count - 1; i >= 0; i--)
            {
                var r = _activeList[i];
                if (!r.IsActive)
                {
                    // 可以选择移除或保留
                    // 这里保留，外部可以决定何时移除
                }
            }
        }

        /// <summary>清空所有。</summary>
        public void Clear()
        {
            foreach (var r in _activeList)
                r.Dispose();
            _ragdolls.Clear();
            _activeList.Clear();
        }

        public void Dispose()
        {
            Clear();
        }
    }

    // ============================================================================================
    //  调试可视化数据
    // ============================================================================================

    /// <summary>布娃娃调试绘制数据。</summary>
    public struct RagdollDebugDraw
    {
        public Vector3 CapsuleP0;
        public Vector3 CapsuleP1;
        public float Radius;
        public Vector4 Color;
        public bool IsSleeping;
        public int BoneId;
    }

    /// <summary>
    ///   调试绘制扩展。
    /// </summary>
    public static class RagdollDebugExtensions
    {
        /// <summary>收集所有骨头的调试绘制数据。</summary>
        public static void CollectDebugDraw(this RagdollPhysicsSystem ragdoll, List<RagdollDebugDraw> output)
        {
            output.Clear();
            foreach (var b in ragdoll.Bones)
            {
                b.GetCapsuleEndpoints(out var p0, out var p1);
                output.Add(new RagdollDebugDraw
                {
                    CapsuleP0 = p0,
                    CapsuleP1 = p1,
                    Radius = b.CapsuleRadius,
                    Color = b.IsSleeping ? new Vector4(0.3f, 0.3f, 0.5f, 1f) : new Vector4(0f, 1f, 0f, 1f),
                    IsSleeping = b.IsSleeping,
                    BoneId = b.Id,
                });
            }
        }

        /// <summary>收集所有关节的调试绘制数据(连接线)。</summary>
        public static void CollectJointDebugDraw(this RagdollPhysicsSystem ragdoll, List<(Vector3 a, Vector3 b)> output)
        {
            output.Clear();
            foreach (var j in ragdoll.Joints)
            {
                if (j.IsBroken) continue;
                var a = ragdoll.Bones[j.BoneAId];
                var b = ragdoll.Bones[j.BoneBId];
                Vector3 aw = a.Position + Vector3.Transform(j.LocalAnchorA, a.Orientation);
                Vector3 bw = b.Position + Vector3.Transform(j.LocalAnchorB, b.Orientation);
                output.Add((aw, bw));
            }
        }
    }

    // ============================================================================================
    //  力场/伤口系统
    // ============================================================================================

    /// <summary>伤口信息(被攻击的位置)。</summary>
    public struct RagdollWound
    {
        public RagdollBoneType BoneType;
        public Vector3 LocalPosition;   // 在骨头局部系下的位置
        public Vector3 ImpulseDirection;
        public float ImpulseMagnitude;
        public float Damage;
        public float Timestamp;
    }

    /// <summary>伤口管理。</summary>
    public static class RagdollWoundExtensions
    {
        /// <summary>对布娃娃施加伤害。</summary>
        public static void ApplyDamage(this RagdollPhysicsSystem ragdoll, RagdollWound wound)
        {
            var bone = ragdoll.GetBone(wound.BoneType);
            if (bone == null) return;

            Vector3 worldPoint = bone.Position + Vector3.Transform(wound.LocalPosition, bone.Orientation);
            Vector3 impulse = wound.ImpulseDirection * wound.ImpulseMagnitude;
            bone.ApplyImpulseAtPoint(impulse, worldPoint);

            // 唤醒相关骨头
            foreach (var b in ragdoll.Bones)
                b.WakeUp();
        }

        /// <summary>施加爆炸冲击。</summary>
        public static void ApplyExplosion(this RagdollPhysicsSystem ragdoll, Vector3 center, float radius, float force)
        {
            foreach (var b in ragdoll.Bones)
            {
                Vector3 toBone = b.Position - center;
                float dist = toBone.Length();
                if (dist > radius) continue;
                float falloff = 1f - dist / radius;
                Vector3 dir = dist > 0.01f ? toBone / dist : Vector3.UnitY;
                Vector3 impulse = dir * force * falloff;
                b.ApplyImpulse(impulse);
                // 一点随机角速度
                b.AngularVelocity += new Vector3(
                    (Random.Shared.NextSingle() - 0.5f) * 5f,
                    (Random.Shared.NextSingle() - 0.5f) * 5f,
                    (Random.Shared.NextSingle() - 0.5f) * 5f) * falloff;
                b.WakeUp();
            }
        }
    }

    // ============================================================================================
    //  序列化(简化)
    // ============================================================================================

    /// <summary>布娃娃序列化数据。</summary>
    [Serializable]
    public struct RagdollSnapshot
    {
        public Vector3[] Positions;
        public Quaternion[] Orientations;
        public Vector3[] LinearVelocities;
        public Vector3[] AngularVelocities;
        public bool[] JointBroken;
        public float ElapsedTime;
        public int StepCount;
    }

    /// <summary>序列化扩展。</summary>
    public static class RagdollSerializationExtensions
    {
        public static RagdollSnapshot SaveState(this RagdollPhysicsSystem ragdoll)
        {
            int n = ragdoll.Bones.Count;
            var snap = new RagdollSnapshot
            {
                Positions = new Vector3[n],
                Orientations = new Quaternion[n],
                LinearVelocities = new Vector3[n],
                AngularVelocities = new Vector3[n],
                JointBroken = new bool[ragdoll.Joints.Count],
                ElapsedTime = ragdoll.ElapsedTime,
                StepCount = ragdoll.StepCount,
            };
            for (int i = 0; i < n; i++)
            {
                var b = ragdoll.Bones[i];
                snap.Positions[i] = b.Position;
                snap.Orientations[i] = b.Orientation;
                snap.LinearVelocities[i] = b.LinearVelocity;
                snap.AngularVelocities[i] = b.AngularVelocity;
            }
            for (int i = 0; i < ragdoll.Joints.Count; i++)
            {
                snap.JointBroken[i] = ragdoll.Joints[i].IsBroken;
            }
            return snap;
        }

        public static void LoadState(this RagdollPhysicsSystem ragdoll, in RagdollSnapshot snap)
        {
            for (int i = 0; i < Math.Min(snap.Positions.Length, ragdoll.Bones.Count); i++)
            {
                var b = ragdoll.Bones[i];
                b.Position = snap.Positions[i];
                b.Orientation = snap.Orientations[i];
                b.LinearVelocity = snap.LinearVelocities[i];
                b.AngularVelocity = snap.AngularVelocities[i];
                b.UpdateWorldInertia();
                b.WakeUp();
            }
            for (int i = 0; i < Math.Min(snap.JointBroken.Length, ragdoll.Joints.Count); i++)
            {
                ragdoll.Joints[i].IsBroken = snap.JointBroken[i];
            }
        }
    }
}
