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
            
            // 按刚体对分组
            int idA = contact.BodyA?.GetHashCode() ?? 0;
            int idB = contact.BodyB?.GetHashCode() ?? 0;
            var key = (Math.Min(idA, idB), Math.Max(idA, idB));
            
            if (!_contactPairs.ContainsKey(key))
            {
                _contactPairs[key] = new List<Contact>();
            }
            _contactPairs[key].Add(contact);
            
            // 计算物理属性
            CalculateContactProperties(ref contact);
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
            
            // 3. 分离速度（如果不分离，就不需要碰撞响应）
            if (normalVelocity > 0)
                return;
                
            // 4. 计算法向冲量
            float j = -(1 + contact.Restitution) * normalVelocity;
            j += contact.Bias / contact.EffectiveMassNormal;
            j *= contact.EffectiveMassNormal;
            
            // 5. 限制冲量（只允许推动物体，不拉回来）
            float oldImpulse = Vector3.Dot(contact.NormalImpulse, contact.Normal);
            j = MathF.Max(0, oldImpulse + j) - oldImpulse;
            
            // 6. 应用法向冲量
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
                
                float jt = -Vector3.Dot(relativeVelocity, tangent);
                jt *= contact.EffectiveMassTangent;
                
                // 库仑摩擦模型
                float maxJt = contact.Friction * Vector3.Dot(contact.NormalImpulse, contact.Normal);
                float oldTangentImpulse = Vector3.Dot(contact.TangentImpulse, tangent);
                jt = MathF.Min(maxJt, MathF.Max(-maxJt, oldTangentImpulse + jt)) - oldTangentImpulse;
                
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
            
            contact.EffectiveMassNormal = 1.0f / (invMassA + invMassB + rnA + rnB);
            
            // 切向有效质量（用于摩擦）
            Vector3 tangent1 = Vector3.Normalize(Vector3.Cross(contact.Normal, Vector3.UnitY));
            if (Math.Abs(Vector3.Dot(tangent1, contact.Normal)) > 0.99f)
                tangent1 = Vector3.Normalize(Vector3.Cross(contact.Normal, Vector3.UnitZ));
                
            Vector3 tangent2 = Vector3.Cross(contact.Normal, tangent1);
            
            float rtA1 = Vector3.Dot(Vector3.Cross(contact.PointA, tangent1), 
                                  Vector3.Transform(Vector3.Cross(contact.PointA, tangent1), invInertiaA));
            float rtB1 = Vector3.Dot(Vector3.Cross(contact.PointB, tangent1), 
                                  Vector3.Transform(Vector3.Cross(contact.PointB, tangent1), invInertiaB));
            float effMassTangent1 = 1.0f / (invMassA + invMassB + rtA1 + rtB1);
            
            contact.EffectiveMassTangent = effMassTangent1;
            
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