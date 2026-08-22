using System;
using System.Numerics;
using System.Runtime.InteropServices;

namespace ShanyuEngine.Physics.Real
{
    /// <summary>
    /// 刚体 - 遵循牛顿力学的刚体动力学实现
    /// </summary>
    public sealed class Rigidbody
    {
        // 基本属性
        public string Name { get; set; } = "Rigidbody";
        public RigidbodyType Type { get; set; } = RigidbodyType.Dynamic;
        
        // 位置和速度
        public Vector3 Position { get; set; }
        public Quaternion Orientation { get; set; } = Quaternion.Identity;
        public Vector3 Velocity { get; set; }
        public Vector3 AngularVelocity { get; set; }
        
        // 预测位置（用于求解器）
        public Vector3 PredictedPosition { get; set; }
        public Quaternion PredictedRotation { get; set; } = Quaternion.Identity;
        
        // 质量属性
        private float _mass = 1.0f;
        public float Mass
        {
            get => _mass;
            set
            {
                _mass = MathF.Max(0.001f, value); // 避免除以零
                UpdateInertia();
            }
        }
        
        public float InverseMass => Type == RigidbodyType.Static ? 0f : 1f / _mass;
        
        // 转动惯量张量
        private Matrix4x4 _inertiaTensor = Matrix4x4.Identity;
        public Matrix4x4 InverseInertiaTensor { get; private set; } = Matrix4x4.Identity;
        
        // 外部力和扭矩
        public Vector3 Force { get; set; }
        public Vector3 Torque { get; set; }
        public Vector3 ExternalForce { get; set; }
        public Vector3 ExternalTorque { get; set; }
        
        // 材质属性
        public PhysicsMaterial? Material { get; set; }
        public float LinearDamping { get; set; } = 0.01f;
        public float AngularDamping { get; set; } = 0.01f;
        
        // 睡眠优化
        public bool IsSleeping { get; set; }
        public float SleepTime { get; set; }
        
        // 碰撞检测标志
        public bool EnableCollision { get; set; } = true;
        public int CollisionLayer { get; set; } = 1;
        public int CollisionMask { get; set; } = -1; // 与所有层碰撞
        
        // 绑定的碰撞体
        public List<Collider> Colliders { get; } = new();
        
        // 变换矩阵（世界空间）
        public Matrix4x4 WorldTransform
        {
            get
            {
                return Matrix4x4.CreateFromQuaternion(Orientation) * 
                       Matrix4x4.CreateTranslation(Position);
            }
        }
        
        public bool IsStatic => Type == RigidbodyType.Static;
        public bool IsKinematic => Type == RigidbodyType.Kinematic;

        public Rigidbody(RigidbodyDescription desc)
        {
            if (desc == null)
                throw new ArgumentNullException(nameof(desc));
                
            Name = desc.Name ?? "Rigidbody";
            Type = desc.Type;
            Position = desc.Position;
            Orientation = desc.Orientation;
            Mass = desc.Mass;
            LinearDamping = desc.LinearDamping;
            AngularDamping = desc.AngularDamping;
            Material = desc.Material;
            EnableCollision = desc.EnableCollision;
            CollisionLayer = desc.CollisionLayer;
            CollisionMask = desc.CollisionMask;
            
            if (desc.InertiaTensor.HasValue)
            {
                _inertiaTensor = desc.InertiaTensor.Value;
                UpdateInverseInertia();
            }
        }

        /// <summary>
        /// 应用力到刚体中心
        /// </summary>
        public void ApplyForce(Vector3 force)
        {
            if (IsStatic || IsKinematic || IsSleeping)
                return;
                
            Force += force;
        }

        /// <summary>
        /// 应用力到刚体特定点（产生扭矩）
        /// </summary>
        public void ApplyForceAtPoint(Vector3 force, Vector3 worldPoint)
        {
            if (IsStatic || IsKinematic || IsSleeping)
                return;
                
            Force += force;
            Vector3 r = worldPoint - Position;
            Torque += Vector3.Cross(r, force);
        }

        /// <summary>
        /// 应用冲量（瞬间改变速度）
        /// </summary>
        public void ApplyImpulse(Vector3 impulse)
        {
            if (IsStatic || IsKinematic || IsSleeping)
                return;
                
            Velocity += impulse * InverseMass;
            WakeUp();
        }

        /// <summary>
        /// 在特定点应用冲量
        /// </summary>
        public void ApplyImpulseAtPoint(Vector3 impulse, Vector3 worldPoint)
        {
            if (IsStatic || IsKinematic || IsSleeping)
                return;
                
            Velocity += impulse * InverseMass;
            Vector3 r = worldPoint - Position;
            AngularVelocity += Vector3.Transform(Vector3.Cross(r, impulse), InverseInertiaTensor);
            WakeUp();
        }

        /// <summary>
        /// 应用扭矩
        /// </summary>
        public void ApplyTorque(Vector3 torque)
        {
            if (IsStatic || IsKinematic || IsSleeping)
                return;
                
            Torque += torque;
        }

        /// <summary>
        /// 唤醒刚体
        /// </summary>
        public void WakeUp()
        {
            IsSleeping = false;
            SleepTime = 0f;
        }

        /// <summary>
        /// 设置刚体静止
        /// </summary>
        public void SetStatic()
        {
            Type = RigidbodyType.Static;
            Velocity = Vector3.Zero;
            AngularVelocity = Vector3.Zero;
            Force = Vector3.Zero;
            Torque = Vector3.Zero;
        }

        /// <summary>
        /// 计算世界空间中的速度
        /// </summary>
        public Vector3 GetVelocityAtPoint(Vector3 worldPoint)
        {
            if (IsStatic)
                return Vector3.Zero;
                
            Vector3 r = worldPoint - Position;
            return Velocity + Vector3.Cross(AngularVelocity, r);
        }

        /// <summary>
        /// 添加碰撞体
        /// </summary>
        public void AddCollider(Collider collider)
        {
            if (collider != null && !Colliders.Contains(collider))
            {
                Colliders.Add(collider);
                collider.AttachToBody(this);
            }
        }

        /// <summary>
        /// 移除碰撞体
        /// </summary>
        public void RemoveCollider(Collider collider)
        {
            if (collider != null)
            {
                Colliders.Remove(collider);
                collider.DetachFromBody();
            }
        }

        private void UpdateInertia()
        {
            // 如果没有提供自定义惯量，使用默认值
            if (_inertiaTensor == Matrix4x4.Identity && !IsStatic)
            {
                // 默认：球体的惯量张量
                float i = (2f / 5f) * _mass * MathF.Pow(1f, 2f); // 假设半径为1
                _inertiaTensor = new Matrix4x4(
                    i, 0, 0, 0,
                    0, i, 0, 0,
                    0, 0, i, 0,
                    0, 0, 0, 1
                );
            }
            UpdateInverseInertia();
        }

        private void UpdateInverseInertia()
        {
            if (IsStatic)
            {
                InverseInertiaTensor = Matrix4x4.Zero;
            }
            else
            {
                Matrix4x4.Invert(_inertiaTensor, out var inverse);
                InverseInertiaTensor = inverse;
            }
        }
    }

    /// <summary>
    /// 刚体类型
    /// </summary>
    public enum RigidbodyType
    {
        /// <summary>动态刚体 - 受力影响</summary>
        Dynamic,
        /// <summary>静态刚体 - 不受力影响，但可以与其他物体碰撞</summary>
        Static,
        /// <summary>运动学刚体 - 可以移动，但不受力影响</summary>
        Kinematic
    }

    /// <summary>
    /// 刚体描述
    /// </summary>
    public sealed class RigidbodyDescription
    {
        public string? Name { get; set; }
        public RigidbodyType Type { get; set; } = RigidbodyType.Dynamic;
        public Vector3 Position { get; set; }
        public Quaternion Orientation { get; set; } = Quaternion.Identity;
        public float Mass { get; set; } = 1.0f;
        public Matrix4x4? InertiaTensor { get; set; }
        public float LinearDamping { get; set; } = 0.01f;
        public float AngularDamping { get; set; } = 0.01f;
        public PhysicsMaterial? Material { get; set; }
        public bool EnableCollision { get; set; } = true;
        public int CollisionLayer { get; set; } = 1;
        public int CollisionMask { get; set; } = -1;
    }

    /// <summary>
    /// 四元数辅助函数
    /// </summary>
    public static class QuaternionHelper
    {
        /// <summary>
        /// 使用角速度积分四元数
        /// </summary>
        public static Quaternion Integrate(Quaternion q, Vector3 angularVelocity, float deltaTime)
        {
            // 创建旋转四元数：q = [0, wx, wy, wz] * dt/2
            float halfDt = deltaTime * 0.5f;
            Quaternion rotationDelta = new Quaternion(
                angularVelocity.X * halfDt,
                angularVelocity.Y * halfDt,
                angularVelocity.Z * halfDt,
                0f
            );
            
            // 四元数乘法：q_new = q_old + rotationDelta * q_old
            Quaternion result = rotationDelta * q;
            result.W += q.W;
            result.X += q.X;
            result.Y += q.Y;
            result.Z += q.Z;
            
            // 归一化
            return Normalize(result);
        }

        public static Quaternion Normalize(Quaternion q)
        {
            float length = MathF.Sqrt(q.W * q.W + q.X * q.X + q.Y * q.Y + q.Z * q.Z);
            if (length > 1e-6f)
            {
                float invLength = 1.0f / length;
                return new Quaternion(
                    q.X * invLength,
                    q.Y * invLength,
                    q.Z * invLength,
                    q.W * invLength
                );
            }
            return Quaternion.Identity;
        }

        /// <summary>
        /// 从四元数创建旋转矩阵
        /// </summary>
        public static Matrix4x4 ToMatrix(Quaternion q)
        {
            q = Normalize(q);
            
            float xx = q.X * q.X;
            float yy = q.Y * q.Y;
            float zz = q.Z * q.Z;
            float xy = q.X * q.Y;
            float xz = q.X * q.Z;
            float yz = q.Y * q.Z;
            float wx = q.W * q.X;
            float wy = q.W * q.Y;
            float wz = q.W * q.Z;

            return new Matrix4x4(
                1f - 2f * (yy + zz), 2f * (xy - wz), 2f * (xz + wy), 0f,
                2f * (xy + wz), 1f - 2f * (xx + zz), 2f * (yz - wx), 0f,
                2f * (xz - wy), 2f * (yz + wx), 1f - 2f * (xx + yy), 0f,
                0f, 0f, 0f, 1f
            );
        }
    }
}