// ============================================================================================
//  RagdollSystem.cs
//
//  yama引擎 (YamaEngine) 布娃娃系统 —— 基于骨骼动画创建和管理物理布娃娃。
//
//  内容：
//    - RagdollBoneConfig：骨骼配置（名称/碰撞体形状/尺寸/关节类型/锚点偏移）
//    - RagdollConfig：布娃娃整体配置
//    - RagdollInstance：布娃娃实例（骨骼体 + 关节）
//    - RagdollCreator：从骨骼动画创建布娃娃
//    - RagdollController：动画/布娃娃混合控制
//    - GroundContactDetection：地面接触检测
//
//  命名空间：YamaEngine.Physics
//  目标框架：net8.0 / C# 8.0+
// ============================================================================================

using System;
using System.Collections.Generic;

namespace YamaEngine.Physics
{
    #region ======================== 骨骼碰撞体形状 ========================

    /// <summary>布娃娃骨骼碰撞体形状类型。</summary>
    public enum BoneColliderShape
    {
        /// <summary>胶囊体（适用于四肢长骨）。</summary>
        Capsule,
        /// <summary>长方体（适用于躯干/骨盆）。</summary>
        Box,
        /// <summary>球体（适用于头部/关节）。</summary>
        Sphere,
    }

    /// <summary>布娃娃骨骼对应的关节类型。</summary>

    #endregion

    #region ======================== 布娃娃配置 ========================

    /// <summary>
    ///   布娃娃骨骼配置：描述单个骨骼的物理属性和连接关系。
    /// </summary>
    public class RagdollBoneConfig
    {
        /// <summary>骨骼名称（对应动画骨骼名）。</summary>
        public string BoneName { get; set; } = "";

        /// <summary>父骨骼名称（空字符串表示根骨骼）。</summary>
        public string ParentBoneName { get; set; } = "";

        /// <summary>碰撞体形状。</summary>
        public BoneColliderShape ColliderShape { get; set; } = BoneColliderShape.Capsule;

        /// <summary>碰撞体半尺寸（Box）或半径（Sphere/Capsule）。</summary>
        public PhysicsVector3 ColliderHalfExtents { get; set; } = new PhysicsVector3(0.05f, 0.15f, 0.05f);

        /// <summary>胶囊体长度（仅Capsule有效）。</summary>
        public float CapsuleLength { get; set; } = 0.3f;

        /// <summary>碰撞体在骨骼空间中的位置偏移。</summary>
        public PhysicsVector3 ColliderOffset { get; set; } = PhysicsVector3.Zero;

        /// <summary>与父骨骼之间的关节类型。</summary>
        public RagdollJointType JointType { get; set; } = RagdollJointType.BallSocket;

        /// <summary>关节锚点在当前骨骼本地空间的偏移。</summary>
        public PhysicsVector3 AnchorOffsetSelf { get; set; } = new PhysicsVector3(0, 0.15f, 0);

        /// <summary>关节锚点在父骨骼本地空间的偏移。</summary>
        public PhysicsVector3 AnchorOffsetParent { get; set; } = new PhysicsVector3(0, -0.15f, 0);

        /// <summary>骨骼刚体质量（千克）。</summary>
        public float Mass { get; set; } = 5.0f;

        /// <summary>碰撞层。</summary>
        public int CollisionLayer { get; set; } = 1;

        /// <summary>碰撞掩码。</summary>
        public int CollisionMask { get; set; } = -1; // 0xFFFFFFFF（全位数）

        /// <summary>是否属于"组"（用于分部位激活）。</summary>
        public string Group { get; set; } = "fullbody";

        /// <summary>关节参数（可选，用于具体关节配置）。</summary>
        public Dictionary<string, float> JointParams { get; set; } = new Dictionary<string, float>();
    }

    /// <summary>
    ///   布娃娃整体配置。
    /// </summary>

    #endregion

    #region ======================== 布娃娃实例 ========================

    /// <summary>
    ///   布娃娃实例：运行时的物理骨骼体和关节集合。
    /// </summary>
    public class RagdollInstance
    {
        /// <summary>骨骼名称到物理刚体的映射。</summary>
        public Dictionary<string, RigidBody> BoneBodies { get; } = new Dictionary<string, RigidBody>();

        /// <summary>骨骼名称到关节的映射。</summary>
        public Dictionary<string, PhysicsJoint> Joints { get; } = new Dictionary<string, PhysicsJoint>();

        /// <summary>骨骼名称到骨骼世界变换的缓存。</summary>
        public Dictionary<string, PhysicsTransform> BoneTransforms { get; } = new Dictionary<string, PhysicsTransform>();

        /// <summary>布娃娃是否已激活。</summary>
        public bool IsActive { get; set; } = false;

        /// <summary>布娃娃配置引用。</summary>
        public RagdollConfig Config { get; }

        /// <summary>所属实体ID。</summary>
        public int EntityId { get; set; }

        /// <summary>关节求解器。</summary>
        public JacobianSolver Solver { get; } = new JacobianSolver();

        public RagdollInstance(RagdollConfig config, int entityId)
        {
            Config = config;
            EntityId = entityId;
        }

        /// <summary>获取指定骨骼的刚体。</summary>
        public bool TryGetBoneBody(string boneName, out RigidBody body)
        {
            return BoneBodies.TryGetValue(boneName, out body);
        }

        /// <summary>获取指定骨骼的关节。</summary>
        public bool TryGetJoint(string boneName, out PhysicsJoint joint)
        {
            return Joints.TryGetValue(boneName, out joint);
        }

        /// <summary>更新骨骼变换缓存。</summary>
        public void UpdateBoneTransforms()
        {
            BoneTransforms.Clear();
            foreach (var kvp in BoneBodies)
            {
                BoneTransforms[kvp.Key] = kvp.Value.GetTransform();
            }
        }

        /// <summary>对所有骨骼施加冲量。</summary>
        public void ApplyImpulseToAll(PhysicsVector3 impulse)
        {
            foreach (var kvp in BoneBodies)
            {
                if (kvp.Value != null && !kvp.Value.IsStatic)
                {
                    kvp.Value.LinearVelocity = kvp.Value.LinearVelocity + impulse * kvp.Value.InvMass;
                }
            }
        }

        /// <summary>在指定世界位置对所有骨骼施加爆炸力。</summary>
        public void ApplyExplosion(PhysicsVector3 center, float force, float radius)
        {
            foreach (var kvp in BoneBodies)
            {
                if (kvp.Value == null || kvp.Value.IsStatic) continue;

                PhysicsVector3 toBone = kvp.Value.Position - center;
                float distance = toBone.Length();
                if (distance > radius) continue;

                float falloff = 1.0f - distance / radius;
                PhysicsVector3 direction = distance > 1e-6f ? toBone.Normalized() : new PhysicsVector3(0, 1, 0);
                PhysicsVector3 explosionImpulse = direction * (force * falloff * kvp.Value.Mass);

                kvp.Value.LinearVelocity = kvp.Value.LinearVelocity + explosionImpulse * kvp.Value.InvMass;
                // 增加一些随机旋转
                PhysicsVector3 randomTorque = new PhysicsVector3(
                    (float)new Random().NextDouble() - 0.5f,
                    (float)new Random().NextDouble() - 0.5f,
                    (float)new Random().NextDouble() - 0.5f) * force * falloff * 0.1f;
                kvp.Value.AngularVelocity = kvp.Value.AngularVelocity + randomTorque;
            }
        }
    }

    #endregion

    #region ======================== 布娃娃创建器 ========================

    /// <summary>
    ///   从骨骼配置创建布娃娃实例。
    ///   为每根骨骼创建碰撞体，在相邻骨骼之间创建关节。
    /// </summary>
    public class RagdollCreator
    {
        /// <summary>创建布娃娃实例。</summary>
        /// <param name="config">布娃娃配置。</param>
        /// <param name="basePosition">布娃娃初始世界位置。</param>
        /// <param name="baseRotation">布娃娃初始世界旋转。</param>
        /// <param name="entityId">所属实体ID。</param>
        /// <returns>布娃娃实例。</returns>
        public static RagdollInstance Create(RagdollConfig config,
            PhysicsVector3 basePosition,
            PhysicsQuaternion baseRotation,
            int entityId)
        {
            // 如果没有自定义骨骼，使用默认人体
            if (config.Bones.Count == 0 && config.UseDefaultHumanoid)
                CreateDefaultHumanoidBones(config, config.DefaultHeight);

            var instance = new RagdollInstance(config, entityId);
            PhysicsMatrix3x3 baseRot = baseRotation.ToMatrix3x3();

            // 创建所有骨骼刚体
            foreach (var boneConfig in config.Bones)
            {
                RigidBody body = CreateBoneBody(boneConfig, config, basePosition, baseRot);
                instance.BoneBodies[boneConfig.BoneName] = body;
            }

            // 创建关节（连接相邻骨骼）
            foreach (var boneConfig in config.Bones)
            {
                if (string.IsNullOrEmpty(boneConfig.ParentBoneName))
                    continue;

                if (!instance.BoneBodies.TryGetValue(boneConfig.BoneName, out RigidBody bodySelf) ||
                    !instance.BoneBodies.TryGetValue(boneConfig.ParentBoneName, out RigidBody bodyParent))
                    continue;

                PhysicsJoint joint = CreateJointBetweenBones(boneConfig, bodyParent, bodySelf);
                if (joint != null)
                {
                    instance.Joints[boneConfig.BoneName] = joint;
                    instance.Solver.AddJoint(joint);
                }
            }

            return instance;
        }

        /// <summary>为单个骨骼创建刚体。</summary>
        private static RigidBody CreateBoneBody(RagdollBone boneConfig, RagdollConfig config,
            PhysicsVector3 basePosition, PhysicsMatrix3x3 baseRot)
        {
            Shape shape = CreateBoneShape(boneConfig, config);

            var body = new RigidBody(shape, boneConfig.Mass * config.MassScale)
            {
                Friction = config.Friction,
                Restitution = config.Restitution,
                LinearDamping = config.LinearDamping,
                AngularDamping = config.AngularDamping,
                CollisionLayer = boneConfig.CollisionLayer,
                CollisionMask = boneConfig.CollisionMask,
                Tag = $"Ragdoll_{boneConfig.BoneName}",
            };

            // 初始位置由碰撞体偏移确定
            PhysicsVector3 localPos = boneConfig.ColliderOffset;
            body.Position = basePosition + baseRot.Multiply(localPos);
            body.Rotation = PhysicsQuaternion.Identity;
            body.UpdateInertiaWorld();

            return body;
        }

        /// <summary>创建骨骼碰撞体形状。</summary>
        private static Shape CreateBoneShape(RagdollBone boneConfig, RagdollConfig config)
        {
            float scale = config.ColliderScale;
            PhysicsVector3 ext = boneConfig.ColliderHalfExtents * scale;

            switch (boneConfig.ColliderShape)
            {
                case BoneColliderShape.Box:
                    return new BoxShape(ext);
                case BoneColliderShape.Sphere:
                    return new SphereShape(ext.X);
                case BoneColliderShape.Capsule:
                    // 使用胶囊体（简化为圆柱近似）
                    return new CapsuleShape(ext.X, boneConfig.CapsuleLength * scale);
                default:
                    return new BoxShape(ext);
            }
        }

        /// <summary>在相邻骨骼之间创建关节。</summary>
        private static PhysicsJoint CreateJointBetweenBones(
            RagdollBone boneConfig, RigidBody parentBody, RigidBody selfBody)
        {
            PhysicsVector3 anchorSelf = boneConfig.AnchorOffsetSelf;
            PhysicsVector3 anchorParent = boneConfig.AnchorOffsetParent;

            switch (boneConfig.JointType)
            {
                case RagdollJointType.Fixed:
                    return JointFactory.CreateFixed(parentBody, selfBody, anchorParent, anchorSelf);

                case RagdollJointType.ConeTwist:
                    {
                        float swingSpan = (float)(Math.PI / 4);
                        float twistSpan = (float)(Math.PI / 6);
                        if (boneConfig.JointParams.TryGetValue("SwingSpan", out float ss)) swingSpan = ss;
                        if (boneConfig.JointParams.TryGetValue("TwistSpan", out float ts)) twistSpan = ts;
                        return JointFactory.CreateConeTwist(parentBody, selfBody, anchorParent, anchorSelf, swingSpan, twistSpan);
                    }

                case RagdollJointType.Hinge:
                    {
                        float minAngle = (float)(-Math.PI / 2);
                        float maxAngle = (float)(Math.PI / 2);
                        PhysicsVector3 axisA = new PhysicsVector3(0, 0, 1);
                        PhysicsVector3 axisB = new PhysicsVector3(0, 0, 1);
                        if (boneConfig.JointParams.TryGetValue("MinAngle", out float ma)) minAngle = ma;
                        if (boneConfig.JointParams.TryGetValue("MaxAngle", out float mga)) maxAngle = mga;
                        return JointFactory.CreateHinge(parentBody, selfBody, anchorParent, anchorSelf, axisA, axisB, minAngle, maxAngle);
                    }

                case RagdollJointType.BallSocket:
                    return JointFactory.CreateBallSocket(parentBody, selfBody, anchorParent, anchorSelf);

                case RagdollJointType.SmallHinge:
                    {
                        float minAngle = (float)(-Math.PI / 6);
                        float maxAngle = (float)(Math.PI / 6);
                        PhysicsVector3 axisA = new PhysicsVector3(0, 0, 1);
                        PhysicsVector3 axisB = new PhysicsVector3(0, 0, 1);
                        if (boneConfig.JointParams.TryGetValue("MinAngle", out float ma)) minAngle = ma;
                        if (boneConfig.JointParams.TryGetValue("MaxAngle", out float mga)) maxAngle = mga;
                        return JointFactory.CreateHinge(parentBody, selfBody, anchorParent, anchorSelf, axisA, axisB, minAngle, maxAngle);
                    }

                default:
                    return JointFactory.CreateBallSocket(parentBody, selfBody, anchorParent, anchorSelf);
            }
        }

        /// <summary>创建默认人体骨骼配置。</summary>
        private static void CreateDefaultHumanoidBones(RagdollConfig config, float height)
        {
            float scale = height / 1.75f;

            // 骨盆/臀部（根骨骼）
            config.Bones.Add(new RagdollBone
            {
                BoneName = "Hips",
                ParentBoneName = "",
                ColliderShape = BoneColliderShape.Box,
                ColliderHalfExtents = new PhysicsVector3(0.15f, 0.1f, 0.1f) * scale,
                ColliderOffset = new PhysicsVector3(0, 0.9f, 0) * scale,
                Mass = 10,
                JointType = RagdollJointType.Fixed,
                AnchorOffsetSelf = new PhysicsVector3(0, 0.1f, 0) * scale,
                AnchorOffsetParent = PhysicsVector3.Zero,
                Group = "lower_body",
            });

            // 脊柱
            config.Bones.Add(new RagdollBone
            {
                BoneName = "Spine",
                ParentBoneName = "Hips",
                ColliderShape = BoneColliderShape.Box,
                ColliderHalfExtents = new PhysicsVector3(0.12f, 0.15f, 0.08f) * scale,
                ColliderOffset = new PhysicsVector3(0, 1.15f, 0) * scale,
                Mass = 8,
                JointType = RagdollJointType.Fixed,
                AnchorOffsetSelf = new PhysicsVector3(0, 0.15f, 0) * scale,
                AnchorOffsetParent = new PhysicsVector3(0, 0.1f, 0) * scale,
                Group = "upper_body",
            });

            // 胸部
            config.Bones.Add(new RagdollBone
            {
                BoneName = "Chest",
                ParentBoneName = "Spine",
                ColliderShape = BoneColliderShape.Box,
                ColliderHalfExtents = new PhysicsVector3(0.16f, 0.15f, 0.1f) * scale,
                ColliderOffset = new PhysicsVector3(0, 1.45f, 0) * scale,
                Mass = 12,
                JointType = RagdollJointType.Fixed,
                AnchorOffsetSelf = new PhysicsVector3(0, 0.15f, 0) * scale,
                AnchorOffsetParent = new PhysicsVector3(0, 0.15f, 0) * scale,
                Group = "upper_body",
            });

            // 头部
            config.Bones.Add(new RagdollBone
            {
                BoneName = "Head",
                ParentBoneName = "Chest",
                ColliderShape = BoneColliderShape.Sphere,
                ColliderHalfExtents = new PhysicsVector3(0.1f, 0.1f, 0.12f) * scale,
                ColliderOffset = new PhysicsVector3(0, 1.75f, 0) * scale,
                Mass = 4.5f,
                JointType = RagdollJointType.ConeTwist,
                AnchorOffsetSelf = new PhysicsVector3(0, 0.1f, 0) * scale,
                AnchorOffsetParent = new PhysicsVector3(0, 0.15f, 0) * scale,
                Group = "head",
                JointParams = new Dictionary<string, float>
                {
                    { "SwingSpan", (float)(Math.PI / 6) },
                    { "TwistSpan", (float)(Math.PI / 8) },
                },
            });

            // 左上臂
            config.Bones.Add(new RagdollBone
            {
                BoneName = "LeftUpperArm",
                ParentBoneName = "Chest",
                ColliderShape = BoneColliderShape.Capsule,
                ColliderHalfExtents = new PhysicsVector3(0.04f, 0.04f, 0.04f) * scale,
                CapsuleLength = 0.3f * scale,
                ColliderOffset = new PhysicsVector3(-0.22f, 1.5f, 0) * scale,
                Mass = 3,
                JointType = RagdollJointType.ConeTwist,
                AnchorOffsetSelf = new PhysicsVector3(0, 0.15f, 0) * scale,
                AnchorOffsetParent = new PhysicsVector3(-0.18f, 0.12f, 0) * scale,
                Group = "left_arm",
                JointParams = new Dictionary<string, float>
                {
                    { "SwingSpan", (float)(Math.PI / 3) },
                    { "TwistSpan", (float)(Math.PI / 4) },
                },
            });

            // 左前臂
            config.Bones.Add(new RagdollBone
            {
                BoneName = "LeftLowerArm",
                ParentBoneName = "LeftUpperArm",
                ColliderShape = BoneColliderShape.Capsule,
                ColliderHalfExtents = new PhysicsVector3(0.03f, 0.03f, 0.03f) * scale,
                CapsuleLength = 0.25f * scale,
                ColliderOffset = new PhysicsVector3(-0.22f, 1.2f, 0) * scale,
                Mass = 2,
                JointType = RagdollJointType.Hinge,
                AnchorOffsetSelf = new PhysicsVector3(0, 0.12f, 0) * scale,
                AnchorOffsetParent = new PhysicsVector3(0, -0.15f, 0) * scale,
                Group = "left_arm",
                JointParams = new Dictionary<string, float>
                {
                    { "MinAngle", (float)(-Math.PI * 0.8) },
                    { "MaxAngle", 0 },
                },
            });

            // 右上臂
            config.Bones.Add(new RagdollBone
            {
                BoneName = "RightUpperArm",
                ParentBoneName = "Chest",
                ColliderShape = BoneColliderShape.Capsule,
                ColliderHalfExtents = new PhysicsVector3(0.04f, 0.04f, 0.04f) * scale,
                CapsuleLength = 0.3f * scale,
                ColliderOffset = new PhysicsVector3(0.22f, 1.5f, 0) * scale,
                Mass = 3,
                JointType = RagdollJointType.ConeTwist,
                AnchorOffsetSelf = new PhysicsVector3(0, 0.15f, 0) * scale,
                AnchorOffsetParent = new PhysicsVector3(0.18f, 0.12f, 0) * scale,
                Group = "right_arm",
                JointParams = new Dictionary<string, float>
                {
                    { "SwingSpan", (float)(Math.PI / 3) },
                    { "TwistSpan", (float)(Math.PI / 4) },
                },
            });

            // 右前臂
            config.Bones.Add(new RagdollBone
            {
                BoneName = "RightLowerArm",
                ParentBoneName = "RightUpperArm",
                ColliderShape = BoneColliderShape.Capsule,
                ColliderHalfExtents = new PhysicsVector3(0.03f, 0.03f, 0.03f) * scale,
                CapsuleLength = 0.25f * scale,
                ColliderOffset = new PhysicsVector3(0.22f, 1.2f, 0) * scale,
                Mass = 2,
                JointType = RagdollJointType.Hinge,
                AnchorOffsetSelf = new PhysicsVector3(0, 0.12f, 0) * scale,
                AnchorOffsetParent = new PhysicsVector3(0, -0.15f, 0) * scale,
                Group = "right_arm",
                JointParams = new Dictionary<string, float>
                {
                    { "MinAngle", (float)(-Math.PI * 0.8) },
                    { "MaxAngle", 0 },
                },
            });

            // 左大腿
            config.Bones.Add(new RagdollBone
            {
                BoneName = "LeftUpperLeg",
                ParentBoneName = "Hips",
                ColliderShape = BoneColliderShape.Capsule,
                ColliderHalfExtents = new PhysicsVector3(0.06f, 0.06f, 0.06f) * scale,
                CapsuleLength = 0.4f * scale,
                ColliderOffset = new PhysicsVector3(-0.1f, 0.65f, 0) * scale,
                Mass = 7,
                JointType = RagdollJointType.BallSocket,
                AnchorOffsetSelf = new PhysicsVector3(0, 0.2f, 0) * scale,
                AnchorOffsetParent = new PhysicsVector3(-0.1f, -0.1f, 0) * scale,
                Group = "left_leg",
            });

            // 左小腿
            config.Bones.Add(new RagdollBone
            {
                BoneName = "LeftLowerLeg",
                ParentBoneName = "LeftUpperLeg",
                ColliderShape = BoneColliderShape.Capsule,
                ColliderHalfExtents = new PhysicsVector3(0.04f, 0.04f, 0.04f) * scale,
                CapsuleLength = 0.4f * scale,
                ColliderOffset = new PhysicsVector3(-0.1f, 0.3f, 0) * scale,
                Mass = 4,
                JointType = RagdollJointType.Hinge,
                AnchorOffsetSelf = new PhysicsVector3(0, 0.2f, 0) * scale,
                AnchorOffsetParent = new PhysicsVector3(0, -0.2f, 0) * scale,
                Group = "left_leg",
                JointParams = new Dictionary<string, float>
                {
                    { "MinAngle", (float)(-Math.PI * 0.7) },
                    { "MaxAngle", 0 },
                },
            });

            // 右大腿
            config.Bones.Add(new RagdollBone
            {
                BoneName = "RightUpperLeg",
                ParentBoneName = "Hips",
                ColliderShape = BoneColliderShape.Capsule,
                ColliderHalfExtents = new PhysicsVector3(0.06f, 0.06f, 0.06f) * scale,
                CapsuleLength = 0.4f * scale,
                ColliderOffset = new PhysicsVector3(0.1f, 0.65f, 0) * scale,
                Mass = 7,
                JointType = RagdollJointType.BallSocket,
                AnchorOffsetSelf = new PhysicsVector3(0, 0.2f, 0) * scale,
                AnchorOffsetParent = new PhysicsVector3(0.1f, -0.1f, 0) * scale,
                Group = "right_leg",
            });

            // 右小腿
            config.Bones.Add(new RagdollBone
            {
                BoneName = "RightLowerLeg",
                ParentBoneName = "RightUpperLeg",
                ColliderShape = BoneColliderShape.Capsule,
                ColliderHalfExtents = new PhysicsVector3(0.04f, 0.04f, 0.04f) * scale,
                CapsuleLength = 0.4f * scale,
                ColliderOffset = new PhysicsVector3(0.1f, 0.3f, 0) * scale,
                Mass = 4,
                JointType = RagdollJointType.Hinge,
                AnchorOffsetSelf = new PhysicsVector3(0, 0.2f, 0) * scale,
                AnchorOffsetParent = new PhysicsVector3(0, -0.2f, 0) * scale,
                Group = "right_leg",
                JointParams = new Dictionary<string, float>
                {
                    { "MinAngle", (float)(-Math.PI * 0.7) },
                    { "MaxAngle", 0 },
                },
            });
        }
    }

    #endregion

    #region ======================== 布娃娃控制器 ========================

    /// <summary>布娃娃状态。</summary>
    public enum RagdollState
    {
        /// <summary>完全动画控制。</summary>
        Animation,
        /// <summary>动画与布娃娃混合过渡中。</summary>
        Blend,
        /// <summary>完全布娃娃物理控制。</summary>
        FullRagdoll,
    }

    /// <summary>
    ///   布娃娃控制器：管理动画到布娃娃的混合过渡。
    /// </summary>
    public class RagdollController
    {
        /// <summary>布娃娃实例。</summary>
        public RagdollInstance Instance { get; }

        /// <summary>当前状态。</summary>
        public RagdollState State { get; private set; } = RagdollState.Animation;

        /// <summary>混合因子（0=全动画, 1=全布娃娃）。</summary>
        public float BlendFactor { get; set; }

        /// <summary>混合速度（每秒增加量）。</summary>
        public float BlendSpeed { get; set; } = 2.0f;

        /// <summary>骨骼混合权重（可选，默认全部相同权重）。</summary>
        public Dictionary<string, float> BoneBlendWeights { get; } = new Dictionary<string, float>();

        /// <summary>触地后速度衰减率。</summary>
        public float GroundDampingFactor { get; set; } = 0.95f;

        /// <summary>地面接触检测器。</summary>
        public GroundContactDetection GroundContact { get; }

        public RagdollController(RagdollInstance instance)
        {
            Instance = instance;
            GroundContact = new GroundContactDetection(instance);
        }

        /// <summary>
        ///   触发布娃娃过渡（从动画切换到布娃娃）。
        /// </summary>
        /// <param name="initialVelocity">过渡时继承的速度。</param>
        public void StartRagdoll(PhysicsVector3 initialVelocity)
        {
            State = RagdollState.Blend;
            BlendFactor = 0;

            // 将动画速度传递给布娃娃骨骼
            foreach (var kvp in Instance.BoneBodies)
            {
                if (kvp.Value != null && !kvp.Value.IsStatic)
                {
                    kvp.Value.LinearVelocity = initialVelocity;
                    kvp.Value.AngularVelocity = PhysicsVector3.Zero;
                }
            }

            Instance.IsActive = true;
        }

        /// <summary>
        ///   恢复到动画控制。
        /// </summary>
        public void StopRagdoll()
        {
            State = RagdollState.Animation;
            BlendFactor = 0;
            Instance.IsActive = false;
        }

        /// <summary>
        ///   分部位激活布娃娃。
        /// </summary>
        /// <param name="group">部位组名（如"left_arm"）。</param>
        /// <param name="initialVelocity">初始速度。</param>
        public void ActivateGroup(string group, PhysicsVector3 initialVelocity)
        {
            foreach (var kvp in Instance.BoneBodies)
            {
                string boneName = kvp.Key;
                string boneGroup = "fullbody";
                if (Instance.Config.BoneGroupMapping.TryGetValue(boneName, out string mappedGroup))
                    boneGroup = mappedGroup;

                // 检查对应骨骼配置的Group
                foreach (var boneConfig in Instance.Config.Bones)
                {
                    if (boneConfig.BoneName == boneName && boneConfig.Group == group)
                    {
                        if (kvp.Value != null && !kvp.Value.IsStatic)
                        {
                            kvp.Value.LinearVelocity = initialVelocity;
                        }
                        break;
                    }
                }
            }
        }

        /// <summary>
        ///   施加外力（爆炸/击飞）。
        /// </summary>
        public void ApplyImpulse(string boneName, PhysicsVector3 impulse)
        {
            if (Instance.TryGetBoneBody(boneName, out RigidBody body) && body != null)
            {
                body.LinearVelocity = body.LinearVelocity + impulse * body.InvMass;
            }
        }

        /// <summary>
        ///   施加爆炸力（从中心向外扩散）。
        /// </summary>
        public void ApplyExplosion(PhysicsVector3 center, float force, float radius)
        {
            if (State != RagdollState.FullRagdoll && State != RagdollState.Blend)
                StartRagdoll(PhysicsVector3.Zero);

            Instance.ApplyExplosion(center, force, radius);
        }

        /// <summary>
        ///   获取布娃娃骨骼的世界变换。
        /// </summary>
        public bool GetBoneTransform(string boneName, out PhysicsTransform transform)
        {
            transform = default;
            if (!Instance.BoneBodies.TryGetValue(boneName, out RigidBody body) || body == null)
                return false;

            transform = body.GetTransform();
            return true;
        }

        /// <summary>
        ///   设置布娃娃骨骼位置（动画驱动，仅在Blend状态下有效）。
        /// </summary>
        public void SetBoneTransform(string boneName, PhysicsTransform transform)
        {
            if (State == RagdollState.FullRagdoll) return;
            if (!Instance.BoneBodies.TryGetValue(boneName, out RigidBody body) || body == null) return;

            float weight = 1.0f;
            if (BoneBlendWeights.TryGetValue(boneName, out float w))
                weight = w;

            if (State == RagdollState.Blend && BlendFactor < 1.0f)
            {
                // 按权重混合动画位置和物理位置
                float animWeight = (1.0f - BlendFactor) * weight;
                PhysicsVector3 animPos = transform.Position;
                PhysicsVector3 physPos = body.Position;
                body.Position = physPos + (animPos - physPos) * animWeight;

                // 混合旋转
                PhysicsQuaternion animRot = transform.Rotation;
                PhysicsQuaternion physRot = body.Rotation;
                body.Rotation = PhysicsQuaternion.Slerp(physRot, animRot, animWeight);
                body.UpdateInertiaWorld();
            }
        }

        /// <summary>
        ///   每帧更新混合因子和地面接触。
        /// </summary>
        public void Update(float dt)
        {
            if (State == RagdollState.Blend)
            {
                BlendFactor += BlendSpeed * dt;
                if (BlendFactor >= 1.0f)
                {
                    BlendFactor = 1.0f;
                    State = RagdollState.FullRagdoll;
                }
            }

            // 地面接触检测和阻尼
            if (State == RagdollState.Blend || State == RagdollState.FullRagdoll)
            {
                GroundContact.Update(dt);
            }
        }

        /// <summary>
        ///   求解布娃娃关节约束。
        /// </summary>
        public void SolveJoints(float dt)
        {
            if (!Instance.IsActive) return;
            Instance.Solver.SolveAll(dt);
        }
    }

    #endregion

    #region ======================== 地面接触检测 ========================

    /// <summary>
    ///   地面接触检测：检测布娃娃哪些部位接触地面，触地后逐渐降低速度。
    /// </summary>
    public class GroundContactDetection
    {
        /// <summary>布娃娃实例。</summary>
        private readonly RagdollInstance _instance;

        /// <summary>地面高度（世界Y坐标）。</summary>
        public float GroundHeight { get; set; } = 0.0f;

        /// <summary>接触阈值（距离地面多远算接触）。</summary>
        public float ContactThreshold { get; set; } = 0.05f;

        /// <summary>触地摩擦系数。</summary>
        public float GroundFriction { get; set; } = 0.6f;

        /// <summary>触地阻尼系数（每帧速度衰减）。</summary>
        public float GroundDamping { get; set; } = 0.92f;

        /// <summary>当前帧接触地面的骨骼列表。</summary>
        public List<string> ContactBones { get; } = new List<string>();

        /// <summary>所有骨骼的触地信息。</summary>
        public Dictionary<string, float> BoneContactDistances { get; } = new Dictionary<string, float>();

        public GroundContactDetection(RagdollInstance instance)
        {
            _instance = instance;
        }

        /// <summary>
        ///   更新地面接触检测。
        /// </summary>
        public void Update(float dt)
        {
            ContactBones.Clear();
            BoneContactDistances.Clear();

            foreach (var kvp in _instance.BoneBodies)
            {
                string boneName = kvp.Key;
                RigidBody body = kvp.Value;
                if (body == null || body.IsStatic) continue;

                // 计算骨骼最低点到地面的距离
                float boneBottom = GetBoneLowestPoint(body);
                float distanceToGround = boneBottom - GroundHeight;

                BoneContactDistances[boneName] = distanceToGround;

                if (distanceToGround < ContactThreshold)
                {
                    ContactBones.Add(boneName);

                    // 施加触地阻尼
                    PhysicsVector3 vel = body.LinearVelocity;
                    float speed = vel.Length();

                    if (speed > 0.01f)
                    {
                        // 法向分量衰减（弹跳阻尼）
                        float normalVel = vel.Y;
                        if (normalVel < 0)
                        {
                            body.LinearVelocity = new PhysicsVector3(
                                vel.X * GroundDamping,
                                -normalVel * 0.3f,  // 反弹衰减
                                vel.Z * GroundDamping);
                        }

                        // 水平摩擦
                        PhysicsVector3 horizontalVel = new PhysicsVector3(vel.X, 0, vel.Z);
                        float horizontalSpeed = horizontalVel.Length();
                        if (horizontalSpeed > 0.01f)
                        {
                            float frictionImpulse = GroundFriction * dt;
                            float reduction = Math.Min(frictionImpulse, horizontalSpeed);
                            PhysicsVector3 frictionDir = -horizontalVel.Normalized();
                            body.LinearVelocity = body.LinearVelocity + frictionDir * reduction;
                        }

                        // 角速度阻尼
                        body.AngularVelocity = body.AngularVelocity * GroundDamping;
                    }
                }
            }
        }

        /// <summary>获取骨骼最低点的世界Y坐标。</summary>
        private float GetBoneLowestPoint(RigidBody body)
        {
            if (body.CollisionShape == null)
                return body.Position.Y;

            AABBShape aabb = body.ComputeWorldAABB();
            return aabb.Min.Y;
        }

        /// <summary>
        ///   检测某个骨骼是否接触地面。
        /// </summary>
        public bool IsBoneInContact(string boneName)
        {
            return ContactBones.Contains(boneName);
        }

        /// <summary>
        ///   获取所有接触地面的骨骼数量。
        /// </summary>
        public int ContactCount => ContactBones.Count;

        /// <summary>
        ///   检测布娃娃整体是否躺地（大部分骨骼触地）。
        /// </summary>
        public bool IsLyingOnGround => ContactBones.Count >= 3;
    }

    #endregion
}
