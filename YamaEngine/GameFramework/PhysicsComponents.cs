// ============================================================================================
//  PhysicsComponents.cs
//
//  yama引擎 - GameObjectReal 物理组件系统
//
//  内容：
//    - RigidbodyComponent：刚体组件（使 GameObject 受物理模拟控制）
//    - ColliderComponent：碰撞器基类 + 具体碰撞器实现（Box/Sphere/Capsule/Mesh）
//    - ForceMode：力施加模式枚举（Force/Acceleration/Impulse/VelocityChange）
//    - PhysicsMaterial：物理材质（摩擦/弹性）
//
//  与 PhysicsWorldReal 集成：使用内置手写物理引擎 API
//  命名空间：YamaEngine.GameFramework
// ============================================================================================

using System;
using System.Collections.Generic;
using System.Numerics;
using YamaEngine.Physics;

namespace YamaEngine.GameFramework
{
    /// <summary>
    /// 刚体组件 — 使 GameObject 受物理模拟控制。
    /// 使用 PhysicsWorldReal 的真实物理引擎。
    /// </summary>
    [AllowMultiple]
    public class RigidbodyComponent : ComponentReal
    {
        // 物理体引用
        private RigidBody? _physicsBody;
        private int _bodyIndex = -1;

        // 物理属性
        public float Mass { get; set; } = 1.0f;
        public bool IsKinematic { get; set; }
        public bool UseGravity { get; set; } = true;
        public float Drag { get; set; }
        public float AngularDrag { get; set; }
        public Vector3 Velocity
        {
            get
            {
                if (_physicsBody == null || _bodyIndex < 0) return Vector3.Zero;
                return _physicsBody.LinearVelocity;
            }
            set
            {
                if (_physicsBody == null || _bodyIndex < 0 || PhysicsWorld == null) return;
                _physicsBody.LinearVelocity = value;
            }
        }
        public Vector3 AngularVelocity
        {
            get
            {
                if (_physicsBody == null || _bodyIndex < 0) return Vector3.Zero;
                return _physicsBody.AngularVelocity;
            }
            set
            {
                if (_physicsBody == null || _bodyIndex < 0 || PhysicsWorld == null) return;
                _physicsBody.AngularVelocity = value;
            }
        }

        /// <summary>物理世界引用（从 Physics 静态类获取）。</summary>
        protected PhysicsWorldReal? PhysicsWorld => YamaEngine.Physics.PhysicsWorldReal.Instance;

        /// <summary>获取内部物理刚体索引。</summary>
        public int BodyIndex => _bodyIndex;

        /// <summary>获取内部物理刚体引用。</summary>
        public RigidBody? PhysicsBody => _physicsBody;

        // 碰撞器列表
        public List<ColliderComponent> Colliders { get; } = new();

        /// <summary>
        /// 当组件被创建时调用（仅一次）。
        /// 从 Colliders 构建物理形状。
        /// </summary>
        public override void Awake()
        {
            base.Awake();
            CollectColliders();
            BuildPhysicsShape();
        }

        /// <summary>
        /// 收集 GameObject 上的所有碰撞器。
        /// </summary>
        private void CollectColliders()
        {
            Colliders.Clear();
            var colliders = GameObject.GetComponents<ColliderComponent>();
            foreach (var collider in colliders)
            {
                if (!Colliders.Contains(collider))
                    Colliders.Add(collider);
            }
        }

        /// <summary>
        /// 添加一个碰撞器到刚体。
        /// </summary>
        public void AddCollider(ColliderComponent collider)
        {
            if (collider == null || Colliders.Contains(collider)) return;
            Colliders.Add(collider);
        }

        /// <summary>
        /// 移除一个碰撞器。
        /// </summary>
        public void RemoveCollider(ColliderComponent collider)
        {
            if (collider == null) return;
            Colliders.Remove(collider);
        }

        /// <summary>
        /// 从 Colliders 构建物理形状。
        /// 多个碰撞器 → 复合形状。
        /// </summary>
        private void BuildPhysicsShape()
        {
            if (_physicsBody == null)
                _physicsBody = new RigidBody();

            // 如果没有碰撞器，保持形状为空
            if (Colliders.Count == 0)
            {
                _physicsBody.CollisionShape = null;
                return;
            }

            // 单个碰撞器 → 直接使用该形状
            if (Colliders.Count == 1)
            {
                _physicsBody.CollisionShape = Colliders[0].CreateShape();
                return;
            }

            // 多个碰撞器 → 复合形状
            var composite = new CompositeShape();
            foreach (var collider in Colliders)
            {
                var shape = collider.CreateShape();
                if (shape == null) continue;

                // 获取局部变换（相对于刚体位置）
                var transform = new PhysicsTransform(
                    collider.Center,
                    PhysicsQuaternion.Identity
                );
                composite.AddChild(shape, transform);
            }

            _physicsBody.CollisionShape = composite;
        }

        /// <summary>
        /// 当组件启用时调用。
        /// 向物理世界注册。
        /// </summary>
        public override void OnEnable()
        {
            base.OnEnable();
            RegisterToPhysicsWorld();
        }

        /// <summary>
        /// 注册刚体到物理世界。
        /// </summary>
        private void RegisterToPhysicsWorld()
        {
            if (PhysicsWorld == null) return;
            if (_bodyIndex >= 0) return; // 已注册

            // 确保形状已构建
            if (_physicsBody == null || _physicsBody.CollisionShape == null)
            {
                BuildPhysicsShape();
            }

            if (_physicsBody == null) return;

            // 配置刚体属性
            _physicsBody.Mass = Mass;
            _physicsBody.BodyType = IsKinematic ? BodyType.Kinematic :
                                    Mass <= 0f ? BodyType.Static : BodyType.Dynamic;
            _physicsBody.LinearDamping = Drag;
            _physicsBody.AngularDamping = AngularDrag;

            // 从 Transform 获取初始位置/旋转
            TransformReal transform = GameObject.Transform;
            _physicsBody.Position = transform.Position;
            _physicsBody.Rotation = transform.WorldRotation;

            // 应用材质属性（来自第一个碰撞器）
            if (Colliders.Count > 0 && Colliders[0].Material != null)
            {
                _physicsBody.Friction = Colliders[0].Material.Friction;
                _physicsBody.Restitution = Colliders[0].Material.Restitution;
            }

            // 设置用户数据指向这个组件
            _physicsBody.UserData = this;

            // 计算惯性张量
            if (_physicsBody.CollisionShape != null && Mass > 0f)
            {
                // 根据形状计算惯性张量
                // 这里已经在 RigidBody 构造后通过 Mass 设置了 InvMass，只需计算惯性
                // 对于常见形状，我们使用预计算
                ComputeInertiaTensor();
            }

            // 添加到物理世界
            _bodyIndex = PhysicsWorld.AddBody(_physicsBody);
        }

        /// <summary>
        /// 根据形状计算惯性张量。
        /// </summary>
        private void ComputeInertiaTensor()
        {
            if (_physicsBody == null || _physicsBody.CollisionShape == null) return;

            var shape = _physicsBody.CollisionShape;
            float volume = shape.ComputeVolume();

            if (shape is SphereShape sphere)
            {
                float radius = sphere.Radius;
                _physicsBody.InverseInertiaTensorLocal = InertiaTensorComputer.ComputeSolidSphere(radius, Mass);
            }
            else if (shape is BoxShape box)
            {
                PhysicsVector3 halfExtents = box.HalfExtents;
                _physicsBody.InverseInertiaTensorLocal = InertiaTensorComputer.ComputeSolidBox(halfExtents, Mass);
            }
            else if (shape is CapsuleShape capsule)
            {
                float radius = capsule.Radius;
                float halfHeight = capsule.HalfHeight;
                _physicsBody.InverseInertiaTensorLocal = InertiaTensorComputer.ComputeSolidCapsule(radius, halfHeight, Mass);
            }
            // 其他形状保持默认单位矩阵
        }

        /// <summary>
        /// 当组件禁用时调用。
        /// 从物理世界注销。
        /// </summary>
        public override void OnDisable()
        {
            base.OnDisable();
            UnregisterFromPhysicsWorld();
        }

        /// <summary>
        /// 当组件销毁时调用。
        /// </summary>
        public override void OnDestroy()
        {
            base.OnDestroy();
            UnregisterFromPhysicsWorld();
            _physicsBody = null;
            _bodyIndex = -1;
        }

        /// <summary>
        /// 从物理世界注销刚体。
        /// </summary>
        private void UnregisterFromPhysicsWorld()
        {
            if (PhysicsWorld == null || _bodyIndex < 0) return;

            PhysicsWorld.RemoveBody(_bodyIndex);
            _bodyIndex = -1;
        }

        /// <summary>
        /// 每帧从物理体同步变换到 Transform。
        /// 在物理步进完成后调用。
        /// </summary>
        public void SyncFromPhysics()
        {
            if (_bodyIndex < 0 || _physicsBody == null || PhysicsWorld == null) return;

            // 读取物理体的 Position/Rotation → 设置 Transform
            TransformReal transform = GameObject.Transform;
            Vector3 newPosition = _physicsBody.Position;
            Quaternion newRotation = _physicsBody.Rotation;

            // 只在变化时更新（利用脏标记机制）
            if (transform.Position != newPosition)
                transform.Position = newPosition;
            if (transform.WorldRotation != newRotation)
                transform.WorldRotation = newRotation;
        }

        /// <summary>
        /// 将 Transform 变化推送到物理体。
        /// 当用户手动移动变换后调用，仅对运动学体有用。
        /// </summary>
        public void SyncToPhysics()
        {
            if (_bodyIndex < 0 || _physicsBody == null || PhysicsWorld == null) return;

            TransformReal transform = GameObject.Transform;
            _physicsBody.Position = transform.Position;
            _physicsBody.Rotation = transform.WorldRotation;
            _physicsBody.UpdateInertiaWorld();
        }

        /// <summary>
        /// 施加力。
        /// </summary>
        /// <param name="force">力向量（世界空间）。</param>
        /// <param name="mode">力施加模式。</param>
        public void AddForce(Vector3 force, ForceMode mode = ForceMode.Force)
        {
            if (_physicsBody == null || _bodyIndex < 0 || PhysicsWorld == null) return;
            if (_physicsBody.IsStatic || _physicsBody.IsKinematic) return;

            switch (mode)
            {
                case ForceMode.Force:
                    // 连续力：F = ma → a = F/m → 最终力直接施加
                    _physicsBody.ApplyCentralForce(force);
                    break;

                case ForceMode.Acceleration:
                    // 加速度：忽略质量，直接转换为力 F = m * a
                    _physicsBody.ApplyCentralForce(force * _physicsBody.Mass);
                    break;

                case ForceMode.Impulse:
                    // 瞬时冲量：直接施加冲量
                    _physicsBody.ApplyCentralImpulse(force);
                    break;

                case ForceMode.VelocityChange:
                    // 速度变化：Δv = impulse / m → impulse = Δv * m
                    _physicsBody.ApplyCentralImpulse(force * _physicsBody.Mass);
                    break;
            }
        }

        /// <summary>
        /// 在指定世界点施加力。
        /// </summary>
        public void AddForceAtPosition(Vector3 force, Vector3 position, ForceMode mode = ForceMode.Force)
        {
            if (_physicsBody == null || _bodyIndex < 0 || PhysicsWorld == null) return;
            if (_physicsBody.IsStatic || _physicsBody.IsKinematic) return;

            // 先根据模式调整力
            Vector3 effectiveForce = force;
            if (mode == ForceMode.Acceleration)
                effectiveForce = force * _physicsBody.Mass;

            // 应用力和力矩
            if (mode == ForceMode.Force || mode == ForceMode.Acceleration)
            {
                _physicsBody.ApplyForceAtPoint(effectiveForce, position);
            }
            else if (mode == ForceMode.Impulse || mode == ForceMode.VelocityChange)
            {
                if (mode == ForceMode.VelocityChange)
                    effectiveForce = force * _physicsBody.Mass;
                _physicsBody.ApplyImpulse(effectiveForce, position);
            }
        }

        /// <summary>
        /// 施加力矩。
        /// </summary>
        public void AddTorque(Vector3 torque, ForceMode mode = ForceMode.Force)
        {
            if (_physicsBody == null || _bodyIndex < 0 || PhysicsWorld == null) return;
            if (_physicsBody.IsStatic || _physicsBody.IsKinematic) return;

            // Torque 只改变角速度，不影响线速度
            // 类似 AddForce 的处理
            Vector3 effectiveTorque = torque;

            switch (mode)
            {
                case ForceMode.Force:
                    _physicsBody.Torque += (PhysicsVector3)effectiveTorque;
                    break;

                case ForceMode.Acceleration:
                    // 角加速度：τ = I α → α 给定， τ = I α
                    // 这里简化处理：忽略惯性，直接施加
                    _physicsBody.Torque += (PhysicsVector3)(effectiveTorque * _physicsBody.Mass);
                    break;

                case ForceMode.Impulse:
                    _physicsBody.ApplyAngularImpulse(effectiveTorque);
                    break;

                case ForceMode.VelocityChange:
                    // 角速度变化：Δω = angImpulse * I^{-1}
                    // angImpulse = Δω * I
                    // 简化处理，直接乘以质量估计
                    _physicsBody.ApplyAngularImpulse(effectiveTorque * _physicsBody.Mass);
                    break;
            }
        }

        /// <summary>
        /// 唤醒刚体。
        /// </summary>
        public void WakeUp()
        {
            if (_bodyIndex < 0 || _physicsBody == null || PhysicsWorld == null) return;
            // 物理世界在下次步进时会自动唤醒有速度变化的刚体
            // 这里可以留空，或者如果有 API 的话调用
            _physicsBody.State = BodyState.Awake;
        }

        /// <summary>
        /// 强制刚体休眠。
        /// </summary>
        public void Sleep()
        {
            if (_bodyIndex < 0 || _physicsBody == null || PhysicsWorld == null) return;
            _physicsBody.LinearVelocity = PhysicsVector3.Zero;
            _physicsBody.AngularVelocity = PhysicsVector3.Zero;
            _physicsBody.ClearForces();
            _physicsBody.State = BodyState.Sleeping;
        }

        /// <summary>
        /// 刚体是否正在休眠。
        /// </summary>
        public bool IsSleeping()
        {
            if (_physicsBody == null) return false;
            return _physicsBody.IsSleeping;
        }

        /// <summary>
        /// 重新构建碰撞形状。
        /// 当碰撞器参数修改后调用，重新注册到物理世界。
        /// </summary>
        public void RebuildShape()
        {
            // 先注销再重建
            if (PhysicsWorld != null && _bodyIndex >= 0)
            {
                UnregisterFromPhysicsWorld();
            }
            BuildPhysicsShape();
            RegisterToPhysicsWorld();
        }
    }

    /// <summary>
    /// 力施加模式枚举。
    /// 四种模式对应 Unity 风格的力施加方式。
    /// </summary>
    public enum ForceMode
    {
        /// <summary>
        /// 连续力，考虑质量。
        /// F = ma → 施加给物体的力，每帧累积。
        /// </summary>
        Force,

        /// <summary>
        /// 连续加速度，不考虑质量。
        /// 直接施加加速度给物体，忽略质量。
        /// </summary>
        Acceleration,

        /// <summary>
        /// 瞬时冲量，考虑质量。
        /// 用于突然的冲击，如爆炸。
        /// </summary>
        Impulse,

        /// <summary>
        /// 瞬时速度变化，不考虑质量。
        /// 直接改变速度，忽略质量。
        /// </summary>
        VelocityChange
    }

    /// <summary>
    /// 碰撞器组件基类。
    /// 所有具体碰撞器从此继承。
    /// </summary>
    [AllowMultiple]
    public abstract class ColliderComponent : ComponentReal
    {
        /// <summary>
        /// 碰撞器中心（相对于 Transform 原点）。
        /// </summary>
        public Vector3 Center { get; set; }

        /// <summary>
        /// 是否为触发器（传感器）。
        /// 触发器只检测重叠，不产生物理响应。
        /// </summary>
        public bool IsTrigger { get; set; }

        /// <summary>
        /// 物理材质。
        /// </summary>
        public YamaEngine.Physics.PhysicsMaterial? Material { get; set; }

        /// <summary>
        /// 创建物理形状对象。
        /// 由子类实现，返回具体形状。
        /// </summary>
        public abstract Shape CreateShape();
    }

    /// <summary>
    /// 盒碰撞器。
    /// </summary>
    public class BoxColliderComponent : ColliderComponent
    {
        /// <summary>
        /// 盒尺寸（完整大小，不是半尺寸）。
        /// 中心为 Center。
        /// </summary>
        public Vector3 Size { get; set; } = Vector3.One;

        /// <summary>
        /// 创建盒形状。
        /// </summary>
        public override Shape CreateShape()
        {
            // Size 是完整尺寸，需要转换为半尺寸
            Vector3 halfExtents = Size * 0.5f + Center * 0f; // Center 偏移在复合形状中处理
            return new BoxShape(new PhysicsVector3(
                halfExtents.X, halfExtents.Y, halfExtents.Z));
        }
    }

    /// <summary>
    /// 球碰撞器。
    /// </summary>
    public class SphereColliderComponent : ColliderComponent
    {
        /// <summary>
        /// 球半径。
        /// </summary>
        public float Radius { get; set; } = 0.5f;

        /// <summary>
        /// 创建球形状。
        /// </summary>
        public override Shape CreateShape()
        {
            return new SphereShape(Radius);
        }
    }

    /// <summary>
    /// 胶囊碰撞器。
    /// 胶囊沿 Y 轴方向。
    /// </summary>
    public class CapsuleColliderComponent : ColliderComponent
    {
        /// <summary>
        /// 胶囊半径。
        /// </summary>
        public float Radius { get; set; } = 0.5f;

        /// <summary>
        /// 胶囊总高度（包括两端半球）。
        /// </summary>
        public float Height { get; set; } = 2.0f;

        /// <summary>
        /// 创建胶囊形状。
        /// </summary>
        public override Shape CreateShape()
        {
            // 总高度 → 圆柱半高度
            float halfHeight = (Height * 0.5f) - Radius;
            if (halfHeight < 0) halfHeight = 0;
            return new CapsuleShape(Radius, halfHeight);
        }
    }

    /// <summary>
    /// 网格碰撞器（静态网格）。
    /// 通常用于静态环境几何体。
    /// </summary>
    public class MeshColliderComponent : ColliderComponent
    {
        /// <summary>
        /// 网格资源路径。
        /// 运行时加载网格顶点和面片。
        /// </summary>
        public string? MeshAssetPath { get; set; }

        /// <summary>
        /// 顶点数组（如果已加载）。
        /// </summary>
        public PhysicsVector3[]? Vertices { get; set; }

        /// <summary>
        /// 三角形面片索引数组。
        /// </summary>
        public int[][]? Faces { get; set; }

        /// <summary>
        /// 是否为凸多边形。
        /// 物理引擎只支持凸多边形网格碰撞。
        /// </summary>
        public bool Convex { get; set; } = true;

        /// <summary>
        /// 创建凸网格形状。
        /// </summary>
        public override Shape CreateShape()
        {
            if (Vertices == null || Faces == null || Vertices.Length < 3)
                return null;

            if (!Convex)
            {
                // 非凸网格不支持，返回空
                return null;
            }

            return new ConvexMeshShape(Vertices, Faces);
        }
    }
}
