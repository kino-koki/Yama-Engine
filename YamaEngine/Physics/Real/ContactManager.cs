using System;
using System.Collections.Generic;
using System.Numerics;

namespace YamaEngine.Physics.Real
{
    /// <summary>
    /// 碰撞接触点
    /// </summary>
    public struct Contact
    {
        public Vector3 Point;           // 接触点（世界空间）
        public Vector3 Normal;          // 接触法线（从A指向B）
        public float PenetrationDepth;   // 穿透深度
        public Rigidbody? BodyA;        // 刚体A
        public Rigidbody? BodyB;        // 刚体B
        public Collider? ColliderA;     // 碰撞体A
        public Collider? ColliderB;     // 碰撞体B
        public Vector3 PointA;          // 刚体A上的接触点（局部空间）
        public Vector3 PointB;          // 刚体B上的接触点（局部空间）
        public float Friction;          // 摩擦系数
        public float Restitution;       // 恢复系数（弹性）
        
        // 求解器使用的缓存
        public Vector3 NormalImpulse;
        /// <summary>Baumgarte 穿透修正冲量（标量，沿法线方向）。Split-impulse：与 NormalImpulse
        /// 分开累积，不参与摩擦锥上限计算，避免虚高的 μ·N 导致动摩擦减速度超过 μg。</summary>
        public float BiasImpulse;
        public Vector3 TangentImpulse;
        public float EffectiveMassNormal;
        public float EffectiveMassTangent;
        public float Bias;
        public int WarmStartIteration;
    }

    /// <summary>
    /// 接触管理器 - 管理所有碰撞接触
    /// </summary>
    public sealed class ContactManager
    {
        private readonly List<Contact> _contacts = new();
        private readonly Dictionary<(int, int), List<Contact>> _contactPairs = new();

        // ── 诊断计数器（仅用于测试/调试，无热路径开销，单帧累计）──
        /// <summary>本帧（SolveContacts 期间）所有接触点摩擦冲量绝对值之和。Step 开始时清零。</summary>
        public float LastStepFrictionImpulse = 0f;
        /// <summary>本帧流形法向冲量之和（摩擦上限的实际取值）。Step 开始时清零。</summary>
        public float LastStepNormalImpulse = 0f;

        public IReadOnlyList<Contact> Contacts => _contacts;
        public int ContactCount => _contacts.Count;

        /// <summary>
        /// 添加接触点
        /// </summary>
        public void AddContact(Contact contact)
        {
            // 检查碰撞层
            if (!ShouldCollide(contact.BodyA, contact.BodyB))
                return;

            _contacts.Add(contact);
            int idx = _contacts.Count - 1;

            // 按刚体对分组
            int idA = contact.BodyA?.GetHashCode() ?? 0;
            int idB = contact.BodyB?.GetHashCode() ?? 0;
            var key = (Math.Min(idA, idB), Math.Max(idA, idB));

            if (!_contactPairs.ContainsKey(key))
            {
                _contactPairs[key] = new List<Contact>();
            }
            _contactPairs[key].Add(_contacts[idx]);

            // 计算物理属性 —— 必须作用于列表内存储的元素。
            // Contact 是 struct，旧的 `CalculateContactProperties(ref contact)`
            // 改的是方法参数（局部副本），EffectiveMassNormal 永远停留在默认 0，
            // 导致 SolveSingleContact 里 emn<=0 直接返回，碰撞响应完全失效
            // （盒子穿透地面持续下落，见 tools/NanRepro）。
            var stored = _contacts[idx];
            CalculateContactProperties(ref stored);
            _contacts[idx] = stored;
        }
        
        /// <summary>
        /// 预热求解器 - 使用上一帧的冲量
        /// </summary>
        public void WarmStart(int iterations)
        {
            var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_contacts);
            for (int ci = 0; ci < span.Length; ci++)
            {
                ref var contact = ref span[ci];
                if (contact.WarmStartIteration == iterations)
                    continue;
                    
                // 应用上一帧的法向冲量
                if (contact.EffectiveMassNormal > 0)
                {
                    float lambda = Vector3.Dot(contact.NormalImpulse, contact.Normal);
                    float impulse = lambda / contact.EffectiveMassNormal;
                    var P = contact.Normal * impulse;
                    
                    if (contact.BodyA != null && !contact.BodyA.IsStatic)
                    {
                        contact.BodyA.Velocity -= P * contact.BodyA.InverseMass;
                        contact.BodyA.AngularVelocity -= Vector3.Transform(
                            Vector3.Cross(contact.PointA, P), 
                            contact.BodyA.InverseInertiaTensor
                        );
                    }
                    
                    if (contact.BodyB != null && !contact.BodyB.IsStatic)
                    {
                        contact.BodyB.Velocity += P * contact.BodyB.InverseMass;
                        contact.BodyB.AngularVelocity += Vector3.Transform(
                            Vector3.Cross(contact.PointB, P), 
                            contact.BodyB.InverseInertiaTensor
                        );
                    }
                }
                
                contact.WarmStartIteration = iterations;
            }
        }
        
        /// <summary>
        /// 求解所有碰撞接触
        /// </summary>
        public void SolveContacts(float deltaTime)
        {
            var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_contacts);
            for (int ci = 0; ci < span.Length; ci++)
            {
                ref var contact = ref span[ci];
                SolveSingleContact(ref contact, deltaTime);
            }
        }
        
        /// <summary>
        /// 求解单个碰撞接触
        /// </summary>
        private void SolveSingleContact(ref Contact contact, float deltaTime)
        {
            // 1. 计算相对速度
            Vector3 vA = contact.BodyA?.GetVelocityAtPoint(contact.PointA) ?? Vector3.Zero;
            Vector3 vB = contact.BodyB?.GetVelocityAtPoint(contact.PointB) ?? Vector3.Zero;
            Vector3 relativeVelocity = vB - vA;
            
            // 2. 法向速度（沿法线方向的速度）
            float normalVelocity = Vector3.Dot(relativeVelocity, contact.Normal);

            // 3. 法向冲量求解（vn <= 0 时才施加，但摩擦始终要跑）
            // 顺序冲量求解器关键点：摩擦锥依赖**本帧累积的法向冲量** NormalImpulse。
            // iter1 修正 vn 后，iter2~N 常检测到 vn>0（分离），若法向不执行就返回，
            // 摩擦会读到 NormalImpulse≈0 → maxJt=0 → 静摩擦完全失效（见
            // PhysicsRealismTests.TestStaticFrictionHoldsBelowThreshold）。所以摩擦
            // 必须始终执行，并基于累积的 NormalImpulse 做钳制。
            float emn = contact.EffectiveMassNormal;
            if (emn > 1e-12f && normalVelocity <= 0)
            {
                // Split-impulse：碰撞响应与 Baumgarte 穿透修正分开累积。
                // - NormalImpulse 只含碰撞响应（用于摩擦锥 μ·N，物理意义正确）
                // - BiasImpulse 累积穿透修正，不参与摩擦上限，避免动摩擦减速度超过 μg
                float targetVelocity = -(1f + contact.Restitution) * normalVelocity;
                float j = emn * targetVelocity;
                float oldImpulse = Vector3.Dot(contact.NormalImpulse, contact.Normal);
                j = MathF.Max(0, oldImpulse + j) - oldImpulse;

                var impulse = contact.Normal * j;
                contact.NormalImpulse += impulse;

                if (contact.BodyA != null && !contact.BodyA.IsStatic)
                {
                    contact.BodyA.Velocity -= impulse * contact.BodyA.InverseMass;
                    contact.BodyA.AngularVelocity -= Vector3.Transform(
                        Vector3.Cross(contact.PointA, impulse),
                        contact.BodyA.InverseInertiaTensor
                    );
                }

                if (contact.BodyB != null && !contact.BodyB.IsStatic)
                {
                    contact.BodyB.Velocity += impulse * contact.BodyB.InverseMass;
                    contact.BodyB.AngularVelocity += Vector3.Transform(
                        Vector3.Cross(contact.PointB, impulse),
                        contact.BodyB.InverseInertiaTensor
                    );
                }

                // Baumgarte 穿透修正（分离冲量，不写入 NormalImpulse）
                if (contact.Bias > 0f)
                {
                    float jBias = emn * contact.Bias;
                    float oldBias = contact.BiasImpulse;
                    jBias = MathF.Max(0, oldBias + jBias) - oldBias;
                    contact.BiasImpulse += jBias;

                    var biasImpulse = contact.Normal * jBias;
                    if (contact.BodyA != null && !contact.BodyA.IsStatic)
                    {
                        contact.BodyA.Velocity -= biasImpulse * contact.BodyA.InverseMass;
                    }
                    if (contact.BodyB != null && !contact.BodyB.IsStatic)
                    {
                        contact.BodyB.Velocity += biasImpulse * contact.BodyB.InverseMass;
                    }
                }
            }

            // 注：Bias 仍施加速度，但不计入 NormalImpulse/摩擦锥，避免虚高 μ·N（split-impulse）。

            // 7. 重新计算相对速度（用于摩擦）
            vA = contact.BodyA?.GetVelocityAtPoint(contact.PointA) ?? Vector3.Zero;
            vB = contact.BodyB?.GetVelocityAtPoint(contact.PointB) ?? Vector3.Zero;
            relativeVelocity = vB - vA;

            // 8. 摩擦求解
            Vector3 tangent = relativeVelocity - contact.Normal * Vector3.Dot(relativeVelocity, contact.Normal);
            float tangentLength = tangent.Length();

            if (tangentLength > 1e-6f)
            {
                tangent /= tangentLength;

                // 摩擦有效质量：沿实际切向重算（含旋转项 r×t）。
                float emt = ComputeEffectiveMassTangent(contact, tangent);
                if (emt <= 1e-12f)
                    return;

                // 摩擦锥上限：流形（同刚体对）法向冲量的**总和 sum**。
                // 实测唯一让静摩擦速度收敛到 0 的取值（位移 0.1289m、velX=0）。
                // 逐点钳制给 5.07m、平均给 1.24m，都失败——盒静置时多点接触里部分点
                // emt 退化（emt=1.0）不施摩擦，有效支撑点少于流形点数。sum 把有效支撑的
                // 法向冲量分摊到全部接触点摩擦锥，补偿了多点接触下求解的摩擦不足。
                // 这是针对本引擎接触求解行为的工程取舍（Baumgarte bias 下 average 物理上
                // 更干净，但静摩擦不过，见 PhysicsRealismTests 注释）。
                float manifoldNormalImpulse = GetManifoldNormalImpulse(contact, useAverage: false);

                float maxJt = contact.Friction * manifoldNormalImpulse;
                float deltaJt = -Vector3.Dot(relativeVelocity, tangent) * emt;
                float oldTotal = Vector3.Dot(contact.TangentImpulse, tangent);
                float newTotal = MathF.Max(-maxJt, MathF.Min(maxJt, oldTotal + deltaJt));
                float jt = newTotal - oldTotal;

                // 诊断计数器：记录本帧摩擦与法向冲量，用于动摩擦测试定位（见 FrictionDiagnostics）。
                LastStepFrictionImpulse += MathF.Abs(jt);
                LastStepNormalImpulse += manifoldNormalImpulse;

                var frictionImpulse = tangent * jt;
                contact.TangentImpulse += frictionImpulse;

                if (contact.BodyA != null && !contact.BodyA.IsStatic)
                {
                    contact.BodyA.Velocity -= frictionImpulse * contact.BodyA.InverseMass;
                    contact.BodyA.AngularVelocity -= Vector3.Transform(
                        Vector3.Cross(contact.PointA, frictionImpulse),
                        contact.BodyA.InverseInertiaTensor
                    );
                }

                if (contact.BodyB != null && !contact.BodyB.IsStatic)
                {
                    contact.BodyB.Velocity += frictionImpulse * contact.BodyB.InverseMass;
                    contact.BodyB.AngularVelocity += Vector3.Transform(
                        Vector3.Cross(contact.PointB, frictionImpulse),
                        contact.BodyB.InverseInertiaTensor
                    );
                }
            }
        }

        /// <summary>
        /// 计算给定切向方向的有效质量（用于摩擦求解）
        /// </summary>
        private static float ComputeEffectiveMassTangent(in Contact contact, Vector3 tangent)
        {
            float invMassA = contact.BodyA?.InverseMass ?? 0;
            float invMassB = contact.BodyB?.InverseMass ?? 0;
            Matrix4x4 invInertiaA = contact.BodyA?.InverseInertiaTensor ?? default;
            Matrix4x4 invInertiaB = contact.BodyB?.InverseInertiaTensor ?? default;

            float rtA = Vector3.Dot(Vector3.Cross(contact.PointA, tangent),
                                    Vector3.Transform(Vector3.Cross(contact.PointA, tangent), invInertiaA));
            float rtB = Vector3.Dot(Vector3.Cross(contact.PointB, tangent),
                                    Vector3.Transform(Vector3.Cross(contact.PointB, tangent), invInertiaB));
            float denom = invMassA + invMassB + rtA + rtB;
            return denom > 1e-10f ? 1.0f / denom : 0f;
        }

        /// <summary>
        /// 流形级法向冲量：同一刚体对（contact pair）内所有接触点的法向冲量之和。
        /// useAverage=false 返回 sum（摩擦锥用，见 SolveSingleContact 注释）；
        /// useAverage=true 返回平均（诊断用）。
        /// 遍历主列表 _contacts（而非 _contactPairs，后者存的是 struct 值副本不会更新）。
        /// </summary>
        private float GetManifoldNormalImpulse(in Contact contact, bool useAverage)
        {
            int idA = contact.BodyA?.GetHashCode() ?? 0;
            int idB = contact.BodyB?.GetHashCode() ?? 0;
            var key = (Math.Min(idA, idB), Math.Max(idA, idB));

            float total = 0f;
            int count = 0;
            foreach (var c in _contacts)
            {
                int cidA = c.BodyA?.GetHashCode() ?? 0;
                int cidB = c.BodyB?.GetHashCode() ?? 0;
                var ckey = (Math.Min(cidA, cidB), Math.Max(cidA, cidB));
                if (ckey == key)
                {
                    total += Vector3.Dot(c.NormalImpulse, c.Normal);
                    count++;
                }
            }
            return useAverage && count > 0 ? total / count : total;
        }

        /// <summary>
        /// 计算接触点的物理属性
        /// </summary>
        private void CalculateContactProperties(ref Contact contact)
        {
            // 计算有效质量（用于冲量求解）
            float invMassA = contact.BodyA?.InverseMass ?? 0;
            float invMassB = contact.BodyB?.InverseMass ?? 0;
            Matrix4x4 invInertiaA = contact.BodyA?.InverseInertiaTensor ?? default;
            Matrix4x4 invInertiaB = contact.BodyB?.InverseInertiaTensor ?? default;
            
            // 法向有效质量
            float rnA = Vector3.Dot(Vector3.Cross(contact.PointA, contact.Normal), 
                                 Vector3.Transform(Vector3.Cross(contact.PointA, contact.Normal), invInertiaA));
            float rnB = Vector3.Dot(Vector3.Cross(contact.PointB, contact.Normal), 
                                 Vector3.Transform(Vector3.Cross(contact.PointB, contact.Normal), invInertiaB));
            
            float normalDenominator = invMassA + invMassB + rnA + rnB;
            contact.EffectiveMassNormal = normalDenominator > 1e-10f
                ? 1.0f / normalDenominator
                : 0f;
            
            // 切向有效质量（用于摩擦）
            // 稳健切向基：避免轴对齐法线（如盒静置水平地面，n=(0,1,0)）导致
            // Cross(n, UnitY)=0 → Normalize(0)=NaN → 摩擦冲量 NaN 传播。
            // 经典做法：选择与 n 不平行（|dot|<0.99）的辅助轴构造正交切向。
            Vector3 normal = contact.Normal;
            if (normal.LengthSquared() < 1e-10f)
            {
                // 退化法线：禁用摩擦有效质量，避免除零
                contact.EffectiveMassTangent = 0f;
                return;
            }
            Vector3 helper = MathF.Abs(normal.Y) < 0.99f ? Vector3.UnitY : Vector3.UnitX;
            Vector3 tangent1 = Vector3.Normalize(Vector3.Cross(normal, helper));

            float rtA1 = Vector3.Dot(Vector3.Cross(contact.PointA, tangent1), 
                                  Vector3.Transform(Vector3.Cross(contact.PointA, tangent1), invInertiaA));
            float rtB1 = Vector3.Dot(Vector3.Cross(contact.PointB, tangent1), 
                                  Vector3.Transform(Vector3.Cross(contact.PointB, tangent1), invInertiaB));
            float effMassTangentDenominator = invMassA + invMassB + rtA1 + rtB1;
            contact.EffectiveMassTangent = effMassTangentDenominator > 1e-10f
                ? 1.0f / effMassTangentDenominator
                : 0f;
            
            // 计算穿透偏差（位置修正）
            const float slop = 0.005f;  // 允许的小穿透
            const float percent = 0.8f; // 修正比例
            float penetration = contact.PenetrationDepth - slop;
            contact.Bias = penetration > 0 ? penetration * percent * (1.0f / 0.016f) : 0;
            
            // 计算摩擦系数和恢复系数
            if (contact.ColliderA?.Material != null && contact.ColliderB?.Material != null)
            {
                var (friction, restitution) = PhysicsMaterial.Combine(contact.ColliderA.Material, contact.ColliderB.Material);
                contact.Friction = friction;
                contact.Restitution = restitution;
            }
            else
            {
                contact.Friction = 0.5f;
                contact.Restitution = 0.2f;
            }
        }
        
        /// <summary>
        /// 检查两个刚体是否应该碰撞
        /// </summary>
        private bool ShouldCollide(Rigidbody? bodyA, Rigidbody? bodyB)
        {
            if (bodyA == null || bodyB == null)
                return true;
                
            if (!bodyA.EnableCollision || !bodyB.EnableCollision)
                return false;
                
            // 检查碰撞层
            bool aCollidesWithB = (bodyA.CollisionMask & bodyB.CollisionLayer) != 0;
            bool bCollidesWithA = (bodyB.CollisionMask & bodyA.CollisionLayer) != 0;
            
            return aCollidesWithB && bCollidesWithA;
        }
        
        /// <summary>
        /// 清除所有接触
        /// </summary>
        public void Clear()
        {
            _contacts.Clear();
            _contactPairs.Clear();
        }
    }
}