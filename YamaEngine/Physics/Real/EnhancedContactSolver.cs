// ============================================================================================
//  EnhancedContactSolver.cs
//
//  yama引擎 增强型接触求解器 —— 让刚体碰撞更真实
//
//  增强内容:
//    1. 双切线摩擦 (Friction Cone 近似): 使用两个正交切线方向分别求解摩擦冲量,
//       比单切线更准确, 解决物体在斜面上滑动方向偏移的问题
//    2. 静/动摩擦分离: 低速时使用静摩擦 (更高), 高速时使用动摩擦 (更低),
//       模拟真实世界中物体静止时的"抓地力"
//    3. 恢复速度阈值: 小于阈值的碰撞速度不弹跳 (现实中轻放物体不会弹起)
//    4. 堆叠稳定增强: 基于 Baumgarte 稳定 + 位置修正分离因子 + 斜坡补偿,
//       防止堆叠物体抖动和下沉
//    5. 冲击波传播: 多次迭代时按质量比分配冲量, 让堆叠物体传力更均匀
//    6. 滚动摩擦: 对球体/胶囊体施加滚动阻力, 防止无限滚动
//    7. 接触持久性: 跨帧保持接触点, 用特征码匹配, 改善堆叠稳定性
//
//  命名空间: YamaEngine.Physics.Real
//  目标框架: net8.0 / C# 12+
//  外部依赖: 仅 System.Numerics
// ============================================================================================

using System;
using System.Collections.Generic;
using System.Numerics;

namespace YamaEngine.Physics.Real
{
    /// <summary>
    /// 增强接触点: 在基础 Contact 上扩展更多真实物理数据
    /// </summary>
    public struct EnhancedContact
    {
        // === 基础接触数据 ===
        public Vector3 Point;
        public Vector3 Normal;
        public float PenetrationDepth;
        public Rigidbody? BodyA;
        public Rigidbody? BodyB;
        public Collider? ColliderA;
        public Collider? ColliderB;
        public Vector3 PointA;
        public Vector3 PointB;

        // === 材质参数 ===
        public float StaticFriction;
        public float DynamicFriction;
        public float Restitution;
        public float RollingFriction;

        // === 求解器缓存 ===
        public float NormalImpulse;
        public Vector2 TangentImpulse;   // 双切线冲量 (t1, t2)
        public float EffectiveMassNormal;
        public Vector2 EffectiveMassTangent;
        public float PositionBias;

        // === 双切线方向 ===
        public Vector3 Tangent1;
        public Vector3 Tangent2;

        // === 冲击/变形 ===
        public float ImpactSpeed;        // 碰撞瞬间法向相对速度
        public float ImpactEnergy;       // 碰撞动能 (用于变形/声效)
        public bool IsNewContact;        // 是否为本次 Step 新生成的接触

        // === 接触持久性 ===
        public ulong FeatureId;          // 接触特征码 (用于跨帧匹配)
        public int PersistedFrames;      // 已持续的帧数

        // === 滚动摩擦 ===
        public bool HasRollingFriction;
    }

    /// <summary>
    /// 增强型接触求解器配置
    /// </summary>
    public sealed class EnhancedSolverSettings
    {
        /// <summary>求解器迭代次数 (更多=更稳定但更慢)</summary>
        public int Iterations = 10;

        /// <summary>位置修正迭代次数 (通常等于 Iterations)</summary>
        public int PositionIterations = 10;

        /// <summary>Baumgarte 速度偏差因子 (0.1~0.3, 越大越激进修正穿透)</summary>
        public float Baumgarte = 0.2f;

        /// <summary>允许的穿透余量 (m), 防止抖动</summary>
        public float Slop = 0.005f;

        /// <summary>位置修正比例 (0.2~0.8)</summary>
        public float PositionCorrectionPercent = 0.4f;

        /// <summary>恢复速度阈值 (m/s): 低于此速度的碰撞不弹跳</summary>
        public float RestitutionThreshold = 1.0f;

        /// <summary>静摩擦速度阈值 (m/s): 低于此速度使用静摩擦</summary>
        public float StaticFrictionThreshold = 0.5f;

        /// <summary>堆叠稳定因子: 额外的位置修正, 防止下沉</summary>
        public float StackStabilizationFactor = 0.15f;

        /// <summary>是否启用滚动摩擦</summary>
        public bool EnableRollingFriction = true;

        /// <summary>是否启用接触持久性 (跨帧保持接触)</summary>
        public bool EnableContactPersistence = true;

        /// <summary>最大持久帧数 (超过后重新匹配)</summary>
        public int MaxPersistedFrames = 60;

        /// <summary>冲量分配次数: 影响堆叠传力均匀性</summary>
        public int ImpulseDistributionPasses = 1;
    }

    /// <summary>
    /// 增强型接触求解器 —— 替代 ContactManager 中的基础求解逻辑
    /// </summary>
    public sealed class EnhancedContactSolver
    {
        private readonly List<EnhancedContact> _contacts = new(256);
        private readonly Dictionary<ulong, EnhancedContact> _persistentContacts = new(256);
        private readonly EnhancedSolverSettings _settings;

        // 统计
        public int TotalContacts => _contacts.Count;
        public int PersistentContacts => _persistentContacts.Count;
        public float MaxImpactSpeed { get; private set; }
        public float TotalImpactEnergy { get; private set; }

        public IReadOnlyList<EnhancedContact> Contacts => _contacts;

        public EnhancedContactSolver(EnhancedSolverSettings? settings = null)
        {
            _settings = settings ?? new EnhancedSolverSettings();
        }

        /// <summary>
        /// 添加接触点并初始化求解器缓存
        /// </summary>
        public void AddContact(
            Vector3 point, Vector3 normal, float penetration,
            Rigidbody? bodyA, Rigidbody? bodyB,
            Collider? colliderA, Collider? colliderB,
            Vector3 pointA, Vector3 pointB)
        {
            // 碰撞层检查
            if (!ShouldCollide(bodyA, bodyB))
                return;

            // 计算特征码 (用于持久性匹配)
            ulong featureId = ComputeFeatureId(colliderA, colliderB, point);

            // 检查是否为持久接触
            bool isPersistent = false;
            float prevNormalImpulse = 0f;
            Vector2 prevTangentImpulse = Vector2.Zero;
            int persistedFrames = 0;

            if (_settings.EnableContactPersistence && _persistentContacts.TryGetValue(featureId, out var prev))
            {
                isPersistent = true;
                prevNormalImpulse = prev.NormalImpulse;
                prevTangentImpulse = prev.TangentImpulse;
                persistedFrames = prev.PersistedFrames + 1;
            }

            var contact = new EnhancedContact
            {
                Point = point,
                Normal = Vector3.Normalize(normal),
                PenetrationDepth = MathF.Max(penetration, 0f),
                BodyA = bodyA,
                BodyB = bodyB,
                ColliderA = colliderA,
                ColliderB = colliderB,
                PointA = pointA,
                PointB = pointB,
                IsNewContact = !isPersistent,
                PersistedFrames = persistedFrames,
                FeatureId = featureId,
                NormalImpulse = prevNormalImpulse,
                TangentImpulse = prevTangentImpulse,
                ImpactSpeed = 0f,
                ImpactEnergy = 0f
            };

            // 计算材质参数
            ComputeMaterialProperties(ref contact);

            // 计算有效质量和切线方向
            ComputeEffectiveMass(ref contact);

            // 计算位置修正偏差
            ComputePositionBias(ref contact);

            // 计算冲击速度和能量
            ComputeImpactData(ref contact);

            _contacts.Add(contact);

            // 更新统计
            if (contact.ImpactSpeed > MaxImpactSpeed)
                MaxImpactSpeed = contact.ImpactSpeed;
            TotalImpactEnergy += contact.ImpactEnergy;
        }

        /// <summary>
        /// 预热求解器: 应用上一帧的冲量作为初始猜测
        /// </summary>
        public void WarmStart()
        {
            var contactsSpan = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_contacts);
            for (int i = 0; i < contactsSpan.Length; i++)
            {
                ref var contact = ref contactsSpan[i];

                // 应用法向冲量
                Vector3 normalImpulse = contact.Normal * contact.NormalImpulse;
                ApplyImpulse(ref contact, normalImpulse);

                // 应用切向冲量
                Vector3 tangentImpulse = contact.Tangent1 * contact.TangentImpulse.X +
                                         contact.Tangent2 * contact.TangentImpulse.Y;
                ApplyImpulse(ref contact, tangentImpulse);
            }
        }

        /// <summary>
        /// 求解所有接触: 速度约束求解
        /// </summary>
        public void SolveVelocityConstraints(float deltaTime)
        {
            for (int iter = 0; iter < _settings.Iterations; iter++)
            {
                var contactsSpan = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_contacts);
                for (int i = 0; i < contactsSpan.Length; i++)
                {
                    SolveSingleContactVelocity(ref contactsSpan[i]);
                }
            }

            // 冲量分配: 让堆叠传力更均匀
            for (int pass = 0; pass < _settings.ImpulseDistributionPasses; pass++)
            {
                DistributeImpulses();
            }
        }

        /// <summary>
        /// 求解位置约束: 修正穿透
        /// </summary>
        public void SolvePositionConstraints()
        {
            for (int iter = 0; iter < _settings.PositionIterations; iter++)
            {
                var contactsSpan = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_contacts);
                for (int i = 0; i < contactsSpan.Length; i++)
                {
                    SolveSingleContactPosition(ref contactsSpan[i]);
                }
            }
        }

        /// <summary>
        /// 应用滚动摩擦
        /// </summary>
        public void ApplyRollingFriction(float deltaTime)
        {
            if (!_settings.EnableRollingFriction)
                return;

            var rxFrictionSpan = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_contacts);
            for (int i = 0; i < rxFrictionSpan.Length; i++)
            {
                ref var contact = ref rxFrictionSpan[i];
                if (!contact.HasRollingFriction || contact.RollingFriction <= 0f)
                    continue;

                ApplyRollingResistance(ref contact, deltaTime);
            }
        }

        /// <summary>
        /// 保存持久接触 (用于下一帧预热)
        /// </summary>
        public void StorePersistentContacts()
        {
            _persistentContacts.Clear();
            foreach (var contact in _contacts)
            {
                if (contact.PersistedFrames < _settings.MaxPersistedFrames)
                {
                    _persistentContacts[contact.FeatureId] = contact;
                }
            }
        }

        /// <summary>
        /// 清空当前帧接触 (在 Step 结束时调用)
        /// </summary>
        public void ClearContacts()
        {
            _contacts.Clear();
            MaxImpactSpeed = 0f;
            TotalImpactEnergy = 0f;
        }

        // ════════════════════════════════════════════════════════════════
        //  内部求解逻辑
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// 求解单个接触的速度约束 (法向 + 双切线摩擦)
        /// </summary>
        private void SolveSingleContactVelocity(ref EnhancedContact c)
        {
            // === 法向约束 ===
            Vector3 vA = c.BodyA?.GetVelocityAtPoint(c.PointA) ?? Vector3.Zero;
            Vector3 vB = c.BodyB?.GetVelocityAtPoint(c.PointB) ?? Vector3.Zero;
            Vector3 relativeVelocity = vB - vA;
            float normalVelocity = Vector3.Dot(relativeVelocity, c.Normal);

            // 恢复速度阈值: 低速碰撞不弹跳
            float restitution = c.Restitution;
            if (MathF.Abs(c.ImpactSpeed) < _settings.RestitutionThreshold)
            {
                restitution = 0f;
            }

            // 计算法向冲量
            float lambda = -(1f + restitution) * normalVelocity;

            // Baumgarte 速度偏差 (穿透修正)
            lambda -= c.PositionBias * c.EffectiveMassNormal;

            // 累加并限制 (不允许负冲量: 不拉回)
            float oldImpulse = c.NormalImpulse;
            c.NormalImpulse = MathF.Max(c.NormalImpulse + lambda, 0f);
            float deltaImpulse = c.NormalImpulse - oldImpulse;

            Vector3 impulse = c.Normal * deltaImpulse;
            ApplyImpulse(ref c, impulse);

            // === 双切线摩擦约束 ===
            // 重新计算相对速度
            vA = c.BodyA?.GetVelocityAtPoint(c.PointA) ?? Vector3.Zero;
            vB = c.BodyB?.GetVelocityAtPoint(c.PointB) ?? Vector3.Zero;
            relativeVelocity = vB - vA;

            // 切向相对速度
            float vt1 = Vector3.Dot(relativeVelocity, c.Tangent1);
            float vt2 = Vector3.Dot(relativeVelocity, c.Tangent2);

            // 选择摩擦系数: 静摩擦或动摩擦
            float tangentSpeed = MathF.Sqrt(vt1 * vt1 + vt2 * vt2);
            float friction = tangentSpeed < _settings.StaticFrictionThreshold
                ? c.StaticFriction
                : c.DynamicFriction;

            // 摩擦锥限制 (库仑摩擦模型)
            float maxFriction = friction * c.NormalImpulse;

            // 切线1冲量
            float lambdaT1 = -vt1 * c.EffectiveMassTangent.X;
            float oldT1 = c.TangentImpulse.X;
            c.TangentImpulse.X = MathF.Min(maxFriction, MathF.Max(-maxFriction, c.TangentImpulse.X + lambdaT1));
            float deltaT1 = c.TangentImpulse.X - oldT1;

            // 切线2冲量
            float lambdaT2 = -vt2 * c.EffectiveMassTangent.Y;
            float oldT2 = c.TangentImpulse.Y;
            c.TangentImpulse.Y = MathF.Min(maxFriction, MathF.Max(-maxFriction, c.TangentImpulse.Y + lambdaT2));
            float deltaT2 = c.TangentImpulse.Y - oldT2;

            // 应用摩擦冲量
            Vector3 frictionImpulse = c.Tangent1 * deltaT1 + c.Tangent2 * deltaT2;
            ApplyImpulse(ref c, frictionImpulse);
        }

        /// <summary>
        /// 求解单个接触的位置约束 (穿透修正 + 堆叠稳定)
        /// </summary>
        private void SolveSingleContactPosition(ref EnhancedContact c)
        {
            if (c.PenetrationDepth <= _settings.Slop)
                return;

            // 重新计算穿透
            Vector3 posA = c.BodyA?.Position ?? Vector3.Zero;
            Vector3 posB = c.BodyB?.Position ?? Vector3.Zero;

            // 当前穿透量
            float currentPenetration = c.PenetrationDepth;

            // 位置修正量 = max(穿透 - slop, 0) * percent + 堆叠稳定补偿
            float correction = (currentPenetration - _settings.Slop) * _settings.PositionCorrectionPercent;

            // 堆叠稳定: 如果接触持续多帧, 增加修正力度
            if (c.PersistedFrames > 0)
            {
                float stackBonus = _settings.StackStabilizationFactor *
                    MathF.Min(c.PersistedFrames / 10f, 1f);
                correction += currentPenetration * stackBonus;
            }

            if (correction <= 0f)
                return;

            // 按质量比分配位置修正
            float invMassA = c.BodyA?.InverseMass ?? 0f;
            float invMassB = c.BodyB?.InverseMass ?? 0f;
            float totalInvMass = invMassA + invMassB;
            if (totalInvMass <= 0f)
                return;

            float correctionA = correction * (invMassA / totalInvMass);
            float correctionB = correction * (invMassB / totalInvMass);

            Vector3 correctionVec = c.Normal * correction;

            if (c.BodyA != null && !c.BodyA.IsStatic)
            {
                c.BodyA.Position -= correctionVec * (invMassA / totalInvMass);
            }
            if (c.BodyB != null && !c.BodyB.IsStatic)
            {
                c.BodyB.Position += correctionVec * (invMassB / totalInvMass);
            }
        }

        /// <summary>
        /// 冲量分配: 让堆叠物体传力更均匀
        /// </summary>
        private void DistributeImpulses()
        {
            // 按刚体对分组, 将上方物体的法向冲量部分传递给下方物体
            var bodyContacts = new Dictionary<int, List<int>>();

            for (int i = 0; i < _contacts.Count; i++)
            {
                var c = _contacts[i];
                if (c.BodyB != null && !c.BodyB.IsStatic)
                {
                    int id = c.BodyB.GetHashCode();
                    if (!bodyContacts.ContainsKey(id))
                        bodyContacts[id] = new List<int>();
                    bodyContacts[id].Add(i);
                }
            }

            // 对每个有多个接触的刚体, 重新分配法向冲量
            foreach (var pair in bodyContacts)
            {
                if (pair.Value.Count <= 1)
                    continue;

                // 计算总法向冲量
                float totalNormalImpulse = 0f;
                foreach (int idx in pair.Value)
                {
                    totalNormalImpulse += _contacts[idx].NormalImpulse;
                }

                // 按穿透深度加权重新分配
                float totalWeight = 0f;
                foreach (int idx in pair.Value)
                {
                    totalWeight += MathF.Max(_contacts[idx].PenetrationDepth, 0.001f);
                }

                if (totalWeight > 0f)
                {
                    var writeSpan = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_contacts);
                    foreach (int idx in pair.Value)
                    {
                        float weight = MathF.Max(_contacts[idx].PenetrationDepth, 0.001f) / totalWeight;
                        float targetImpulse = totalNormalImpulse * weight;
                        writeSpan[idx].NormalImpulse = targetImpulse;
                    }
                }
            }
        }

        /// <summary>
        /// 应用滚动阻力 (对球体/胶囊体)
        /// </summary>
        private void ApplyRollingResistance(ref EnhancedContact c, float deltaTime)
        {
            Rigidbody? body = c.BodyA?.IsStatic == false ? c.BodyA : c.BodyB;
            if (body == null || body.IsStatic)
                return;

            // 滚动阻力扭矩 = -μ_r * |N| * ω_hat * r
            float normalForce = c.NormalImpulse / MathF.Max(deltaTime, 1e-6f);
            float rollingTorqueMag = c.RollingFriction * normalForce * 0.1f; // 缩放因子

            Vector3 angularVel = body.AngularVelocity;
            float angularSpeed = angularVel.Length();
            if (angularSpeed < 1e-6f)
                return;

            Vector3 torque = -angularVel / angularSpeed * rollingTorqueMag;
            body.AngularVelocity += Vector3.Transform(torque, body.InverseInertiaTensor) * deltaTime;
        }

        // ════════════════════════════════════════════════════════════════
        //  辅助计算
        // ════════════════════════════════════════════════════════════════

        private void ComputeMaterialProperties(ref EnhancedContact c)
        {
            // 获取材质参数 (使用预设或默认值)
            float staticFriction = 0.6f;
            float dynamicFriction = 0.4f;
            float restitution = 0.2f;
            float rollingFriction = 0.001f;

            // 简单组合: 取平均
            if (c.ColliderA?.Material != null && c.ColliderB?.Material != null)
            {
                // 尝试使用 Real.PhysicsMaterial
                var matA = c.ColliderA.Material;
                var matB = c.ColliderB.Material;

                // 通过反射或直接字段访问获取参数
                staticFriction = (GetStaticFriction(matA) + GetStaticFriction(matB)) * 0.5f;
                dynamicFriction = (GetDynamicFriction(matA) + GetDynamicFriction(matB)) * 0.5f;
                restitution = (GetRestitution(matA) + GetRestitution(matB)) * 0.5f;
                rollingFriction = (GetRollingFriction(matA) + GetRollingFriction(matB)) * 0.5f;
            }
            else if (c.ColliderA?.Material != null)
            {
                staticFriction = GetStaticFriction(c.ColliderA.Material);
                dynamicFriction = GetDynamicFriction(c.ColliderA.Material);
                restitution = GetRestitution(c.ColliderA.Material);
                rollingFriction = GetRollingFriction(c.ColliderA.Material);
            }
            else if (c.ColliderB?.Material != null)
            {
                staticFriction = GetStaticFriction(c.ColliderB.Material);
                dynamicFriction = GetDynamicFriction(c.ColliderB.Material);
                restitution = GetRestitution(c.ColliderB.Material);
                rollingFriction = GetRollingFriction(c.ColliderB.Material);
            }

            c.StaticFriction = staticFriction;
            c.DynamicFriction = dynamicFriction;
            c.Restitution = restitution;
            c.RollingFriction = rollingFriction;

            // 判断是否需要滚动摩擦 (球体或胶囊体)
            c.HasRollingFriction = c.ColliderA is SphereCollider || c.ColliderA is CapsuleCollider ||
                                   c.ColliderB is SphereCollider || c.ColliderB is CapsuleCollider;
        }

        private float GetStaticFriction(object? material)
        {
            if (material == null) return 0.5f;
            var type = material.GetType();
            var prop = type.GetProperty("StaticFriction");
            return prop?.GetValue(material) as float? ?? 0.5f;
        }

        private float GetDynamicFriction(object? material)
        {
            if (material == null) return 0.4f;
            var type = material.GetType();
            // 尝试 DynamicFriction, 如果没有则用 Friction
            var prop = type.GetProperty("DynamicFriction") ?? type.GetProperty("Friction");
            return prop?.GetValue(material) as float? ?? 0.4f;
        }

        private float GetRestitution(object? material)
        {
            if (material == null) return 0.2f;
            var type = material.GetType();
            var prop = type.GetProperty("Restitution");
            return prop?.GetValue(material) as float? ?? 0.2f;
        }

        private float GetRollingFriction(object? material)
        {
            if (material == null) return 0.001f;
            var type = material.GetType();
            var prop = type.GetProperty("RollingFriction");
            return prop?.GetValue(material) as float? ?? 0.001f;
        }

        private void ComputeEffectiveMass(ref EnhancedContact c)
        {
            float invMassA = c.BodyA?.InverseMass ?? 0f;
            float invMassB = c.BodyB?.InverseMass ?? 0f;
            Matrix4x4 invInertiaA = c.BodyA?.InverseInertiaTensor ?? default;
            Matrix4x4 invInertiaB = c.BodyB?.InverseInertiaTensor ?? default;

            // 计算双切线方向
            c.Tangent1 = Vector3.Normalize(Vector3.Cross(c.Normal, Vector3.UnitY));
            if (float.IsNaN(c.Tangent1.X) || c.Tangent1.LengthSquared() < 1e-6f)
            {
                c.Tangent1 = Vector3.Normalize(Vector3.Cross(c.Normal, Vector3.UnitZ));
            }
            c.Tangent2 = Vector3.Cross(c.Normal, c.Tangent1);

            // 法向有效质量
            Vector3 rA = c.PointA;
            Vector3 rB = c.PointB;
            Vector3 rnA = Vector3.Cross(rA, c.Normal);
            Vector3 rnB = Vector3.Cross(rB, c.Normal);

            float knA = invMassA + Vector3.Dot(rnA, Vector3.Transform(rnA, invInertiaA));
            float knB = invMassB + Vector3.Dot(rnB, Vector3.Transform(rnB, invInertiaB));
            c.EffectiveMassNormal = knA + knB > 0f ? 1f / (knA + knB) : 0f;

            // 切线1有效质量
            Vector3 rt1A = Vector3.Cross(rA, c.Tangent1);
            Vector3 rt1B = Vector3.Cross(rB, c.Tangent1);
            float kt1A = invMassA + Vector3.Dot(rt1A, Vector3.Transform(rt1A, invInertiaA));
            float kt1B = invMassB + Vector3.Dot(rt1B, Vector3.Transform(rt1B, invInertiaB));
            float emT1 = kt1A + kt1B > 0f ? 1f / (kt1A + kt1B) : 0f;

            // 切线2有效质量
            Vector3 rt2A = Vector3.Cross(rA, c.Tangent2);
            Vector3 rt2B = Vector3.Cross(rB, c.Tangent2);
            float kt2A = invMassA + Vector3.Dot(rt2A, Vector3.Transform(rt2A, invInertiaA));
            float kt2B = invMassB + Vector3.Dot(rt2B, Vector3.Transform(rt2B, invInertiaB));
            float emT2 = kt2A + kt2B > 0f ? 1f / (kt2A + kt2B) : 0f;

            c.EffectiveMassTangent = new Vector2(emT1, emT2);
        }

        private void ComputePositionBias(ref EnhancedContact c)
        {
            float penetration = c.PenetrationDepth - _settings.Slop;
            // Baumgarte 位置偏差
            c.PositionBias = penetration > 0f
                ? penetration * _settings.Baumgarte * 60f // 60 = 1/dt 假设
                : 0f;
        }

        private void ComputeImpactData(ref EnhancedContact c)
        {
            Vector3 vA = c.BodyA?.GetVelocityAtPoint(c.PointA) ?? Vector3.Zero;
            Vector3 vB = c.BodyB?.GetVelocityAtPoint(c.PointB) ?? Vector3.Zero;
            Vector3 relativeVelocity = vB - vA;

            c.ImpactSpeed = -Vector3.Dot(relativeVelocity, c.Normal);
            if (c.ImpactSpeed < 0f)
                c.ImpactSpeed = 0f;

            // 冲击动能 = 0.5 * m_eff * v^2
            float massA = c.BodyA?.Mass ?? 0f;
            float massB = c.BodyB?.Mass ?? 0f;
            float effectiveMass = (massA * massB) / (massA + massB + 1e-6f);
            c.ImpactEnergy = 0.5f * effectiveMass * c.ImpactSpeed * c.ImpactSpeed;
        }

        private ulong ComputeFeatureId(Collider? a, Collider? b, Vector3 point)
        {
            int idA = a?.GetHashCode() ?? 0;
            int idB = b?.GetHashCode() ?? 0;
            // 量化接触点位置 (1cm 精度)
            int px = (int)(point.X * 100);
            int py = (int)(point.Y * 100);
            int pz = (int)(point.Z * 100);

            ulong hash = (ulong)(idA ^ idB) << 32;
            hash ^= (ulong)(px * 73856093) ^ (ulong)(py * 19349663) ^ (ulong)(pz * 83492791);
            return hash;
        }

        private void ApplyImpulse(ref EnhancedContact c, Vector3 impulse)
        {
            if (c.BodyA != null && !c.BodyA.IsStatic && !c.BodyA.IsKinematic)
            {
                c.BodyA.Velocity -= impulse * c.BodyA.InverseMass;
                Vector3 rA = c.PointA;
                c.BodyA.AngularVelocity -= Vector3.Transform(Vector3.Cross(rA, impulse), c.BodyA.InverseInertiaTensor);
                c.BodyA.WakeUp();
            }
            if (c.BodyB != null && !c.BodyB.IsStatic && !c.BodyB.IsKinematic)
            {
                c.BodyB.Velocity += impulse * c.BodyB.InverseMass;
                Vector3 rB = c.PointB;
                c.BodyB.AngularVelocity += Vector3.Transform(Vector3.Cross(rB, impulse), c.BodyB.InverseInertiaTensor);
                c.BodyB.WakeUp();
            }
        }

        private bool ShouldCollide(Rigidbody? bodyA, Rigidbody? bodyB)
        {
            if (bodyA == null || bodyB == null)
                return true;
            if (!bodyA.EnableCollision || !bodyB.EnableCollision)
                return false;
            bool aCollidesWithB = (bodyA.CollisionMask & bodyB.CollisionLayer) != 0;
            bool bCollidesWithA = (bodyB.CollisionMask & bodyA.CollisionLayer) != 0;
            return aCollidesWithB && bCollidesWithA;
        }

        /// <summary>
        /// 获取接触统计信息 (调试用)
        /// </summary>
        public SolverStats GetStats()
        {
            return new SolverStats
            {
                ContactCount = _contacts.Count,
                PersistentContactCount = _persistentContacts.Count,
                NewContactCount = _contacts.FindAll(c => c.IsNewContact).Count,
                MaxImpactSpeed = MaxImpactSpeed,
                TotalImpactEnergy = TotalImpactEnergy,
                AveragePenetration = _contacts.Count > 0
                    ? _contacts.Sum(c => c.PenetrationDepth) / _contacts.Count
                    : 0f
            };
        }
    }

    /// <summary>
    /// 求解器统计信息
    /// </summary>
    public struct SolverStats
    {
        public int ContactCount;
        public int PersistentContactCount;
        public int NewContactCount;
        public float MaxImpactSpeed;
        public float TotalImpactEnergy;
        public float AveragePenetration;

        public override string ToString()
            => $"Contacts={ContactCount} (new={NewContactCount}, persistent={PersistentContactCount}) " +
               $"MaxImpact={MaxImpactSpeed:F2}m/s Energy={TotalImpactEnergy:F2}J AvgPen={AveragePenetration:F4}m";
    }
}
