using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;

namespace YamaEngine.Physics.Real
{
    /// <summary>
    /// 真实物理引擎 - 基于牛顿力学，追求真实物理效果而非花哨特性
    /// 设计原则：可预测、稳定、高性能、真实物理行为
    /// </summary>
    public sealed class RealPhysicsEngine
    {
        private readonly List<Rigidbody> _rigidbodies = new();
        private readonly List<Collider> _colliders = new();
        private readonly List<Constraint> _constraints = new();
        private readonly ContactManager _contactManager;
        private readonly BroadPhase _broadPhase;
        private readonly NarrowPhase _narrowPhase;
        private readonly Solver _solver;
        private readonly Vector3 _gravity;
        private readonly PhysicsSettings _settings;
        private int _solverIterations;
        private bool _enableSleep;
        private float _sleepThreshold;
        
        public Vector3 Gravity => _gravity;
        public int BodyCount => _rigidbodies.Count;
        public int ColliderCount => _colliders.Count;
        public int ConstraintCount => _constraints.Count;

        // 诊断用：暴露当前步的接触列表（只读）。
        public System.Collections.Generic.IReadOnlyList<Contact> Contacts => _contactManager.Contacts;
        
        public RealPhysicsEngine(PhysicsSettings settings)
        {
            _settings = settings ?? new PhysicsSettings();
            _gravity = _settings.Gravity;
            _solverIterations = _settings.SolverIterations;
            _enableSleep = _settings.EnableSleep;
            _sleepThreshold = _settings.SleepThreshold;
            
            _contactManager = new ContactManager();
            _broadPhase = new BroadPhase();
            _narrowPhase = new NarrowPhase();
            _solver = new Solver(_settings);
        }

        /// <summary>
        /// 创建刚体
        /// </summary>
        public Rigidbody CreateRigidbody(RigidbodyDescription desc)
        {
            if (desc == null)
                throw new ArgumentNullException(nameof(desc));
                
            var body = new Rigidbody(desc);
            _rigidbodies.Add(body);
            return body;
        }

        /// <summary>
        /// 创建碰撞体
        /// </summary>
        public Collider CreateCollider(ColliderDescription desc, Rigidbody? body = null)
        {
            if (desc == null)
                throw new ArgumentNullException(nameof(desc));
                
            Collider collider = desc.Type switch
            {
                ColliderType.Box => new BoxCollider(desc),
                ColliderType.Sphere => new SphereCollider(desc),
                ColliderType.Capsule => new CapsuleCollider(desc),
                ColliderType.Mesh => new MeshCollider(desc),
                _ => throw new ArgumentException($"Unsupported collider type: {desc.Type}")
            };
            
            if (body != null)
            {
                collider.AttachToBody(body);
                // 碰撞体未显式指定材质时，继承所属刚体的材质。
                // 否则 ContactManager.CalculateContactProperties 读到 null，
                // 退回默认 mu=0.5，导致测试在 Rigidbody 上设置的静/动摩擦失效
                // （见 PhysicsRealismTests.TestStaticFrictionHoldsBelowThreshold）。
                collider.Material ??= body.Material;
            }
            
            _colliders.Add(collider);
            return collider;
        }

        /// <summary>
        /// 创建铰链约束
        /// </summary>
        public HingeConstraint CreateHingeConstraint(Rigidbody bodyA, Rigidbody bodyB, Vector3 anchorA, Vector3 anchorB, Vector3 axis)
        {
            var hinge = new HingeConstraint(bodyA, bodyB, anchorA, anchorB, axis);
            _constraints.Add(hinge);
            return hinge;
        }

        /// <summary>
        /// 创建弹簧约束
        /// </summary>
        public SpringConstraint CreateSpringConstraint(Rigidbody bodyA, Rigidbody bodyB, Vector3 anchorA, Vector3 anchorB, float stiffness, float damping)
        {
            var spring = new SpringConstraint(bodyA, bodyB, anchorA, anchorB, stiffness, damping);
            _constraints.Add(spring);
            return spring;
        }

        /// <summary>
        /// 物理模拟步骤 - 使用 Semi-Implicit Euler 积分器（稳定且高效）
        /// </summary>
        public void Step(float deltaTime)
        {
            if (deltaTime <= 0f)
                return;

            // 1. 应用外力
            ApplyForces();

            // 2. 预测位置和速度
            PredictPositions(deltaTime);

            // 3. 碰撞检测
            DetectCollisions();

            // 4. 求解约束（包括碰撞约束）
            SolveConstraints(deltaTime);

            // 5. 更新位置和速度
            UpdatePositions(deltaTime);

            // 6. 睡眠检测
            if (_enableSleep)
                UpdateSleepingBodies();

            // 7. 清理临时数据
            Cleanup();
        }

        private void ApplyForces()
        {
            foreach (var body in _rigidbodies)
            {
                if (!body.IsStatic && !body.IsSleeping)
                {
                    // 应用重力
                    body.Force += _gravity * body.Mass;
                    
                    // 应用外部力
                    body.Force += body.ExternalForce;
                    body.Torque += body.ExternalTorque;
                }
            }
        }

        private void PredictPositions(float deltaTime)
        {
            foreach (var body in _rigidbodies)
            {
                if (!body.IsStatic && !body.IsSleeping)
                {
                    // Semi-Implicit Euler: 先更新速度，再预测位置
                    body.Velocity += (body.Force / body.Mass) * deltaTime;
                    body.AngularVelocity += Vector3.TransformNormal(body.Torque, body.InverseInertiaTensor) * deltaTime;

                    body.PredictedPosition = body.Position + body.Velocity * deltaTime;
                    body.PredictedRotation = QuaternionHelper.Integrate(body.Orientation, body.AngularVelocity, deltaTime);

                    // 力已积分进速度，立即清零。ApplyForce/ApplyForces 都是累加语义，
                    // 若留到 UpdatePositions 再清，则下一步 PredictPositions 会先把上一步
                    // 残留的 Force 再积分一次（重力 + 用户力双倍累加），导致施加的水平力
                    // 叠加超过 μmg，静摩擦失效（见 PhysicsRealismTests.TestStaticFrictionHoldsBelowThreshold）。
                    body.Force = Vector3.Zero;
                    body.Torque = Vector3.Zero;
                }
                else
                {
                    body.PredictedPosition = body.Position;
                    body.PredictedRotation = body.Orientation;
                }
            }
        }

        private void DetectCollisions()
        {
            // 宽相位检测
            var potentialPairs = _broadPhase.FindPotentialCollisions(_colliders);

            // 窄相位检测
            foreach (var pair in potentialPairs)
            {
                var contacts = _narrowPhase.GenerateContacts(pair.Item1, pair.Item2);
                if (contacts.Count > 0)
                {
                    foreach (var contact in contacts)
                    {
                        _contactManager.AddContact(contact);
                    }
                }
            }
        }

        private void SolveConstraints(float deltaTime)
        {
            // 预热碰撞约束
            _contactManager.WarmStart(_solverIterations);

            // 多次迭代求解约束
            for (int i = 0; i < _solverIterations; i++)
            {
                // 求解碰撞约束
                _contactManager.SolveContacts(deltaTime);

                // 求解用户约束
                foreach (var constraint in _constraints)
                {
                    constraint.Solve(deltaTime);
                }
            }

            // 注：Baumgarte bias 在 SolveSingleContact 内通过速度冲量直接做位置修正（原始方案）。
            // split impulse（迭代外独立位置修正）实验再次失败：位置修正与一帧滞后的碰撞检测
            // 冲突——修正把盒抬出接触（contacts=0），下帧法向无支撑被重力抛起，摩擦间歇失效。
            // 见 ContactManager.SolveContacts 阶段 2 注释与 tools/NanRepro 诊断。
        }

        private void UpdatePositions(float deltaTime)
        {
            foreach (var body in _rigidbodies)
            {
                if (!body.IsStatic && !body.IsSleeping)
                {
                    // 用求解修正后的速度重新积分位置。
                    // 旧实现直接写 PredictedPosition（在求解前由旧速度算出），
                    // 且本步 ApplyForces 施加的重力从未被清零，下一步又被累加，
                    // 导致：(1) 法向冲量永远落不到位置上 → 穿透持续下落；
                    //      (2) 重力每步被双倍累加 → 提前接触、更早爆 NaN。
                    body.Position += body.Velocity * deltaTime;
                    body.Orientation = QuaternionHelper.Integrate(body.Orientation, body.AngularVelocity, deltaTime);

                    // 清除累积的力（必须在本步积分完成后，避免下步重力双倍累加）
                    body.Force = Vector3.Zero;
                    body.Torque = Vector3.Zero;
                }
                else
                {
                    body.Position = body.PredictedPosition;
                    body.Orientation = body.PredictedRotation;
                }
            }
        }

        private void UpdateSleepingBodies()
        {
            foreach (var body in _rigidbodies)
            {
                if (body.IsStatic)
                    continue;
                    
                float speed = body.Velocity.Length();
                float angularSpeed = body.AngularVelocity.Length();
                
                if (speed < _sleepThreshold && angularSpeed < _sleepThreshold * 0.5f)
                {
                    body.SleepTime += 0.016f; // 假设 60fps
                    if (body.SleepTime > 1.0f) // 睡眠 1 秒
                    {
                        body.IsSleeping = true;
                    }
                }
                else
                {
                    body.SleepTime = 0f;
                    body.IsSleeping = false;
                }
            }
        }

        private void Cleanup()
        {
            _contactManager.Clear();
        }

        /// <summary>
        /// 检测射线与物理世界的交点
        /// </summary>
        public RaycastHit? Raycast(Vector3 origin, Vector3 direction, float maxDistance = float.MaxValue)
        {
            RaycastHit? closestHit = null;
            float closestDistance = maxDistance;
            
            foreach (var collider in _colliders)
            {
                var hit = collider.Raycast(origin, direction, maxDistance);
                if (hit.HasValue && hit.Value.Distance < closestDistance)
                {
                    closestHit = hit;
                    closestDistance = hit.Value.Distance;
                }
            }
            
            return closestHit;
        }

        /// <summary>
        /// 清理物理引擎资源
        /// </summary>
        public void Dispose()
        {
            _rigidbodies.Clear();
            _colliders.Clear();
            _constraints.Clear();
            _contactManager.Clear();
        }
    }

    /// <summary>
    /// 物理设置 - 控制物理模拟行为
    /// </summary>
    public sealed class PhysicsSettings
    {
        public Vector3 Gravity { get; set; } = new Vector3(0, -9.81f, 0);
        public int SolverIterations { get; set; } = 8;
        public float ContactErp { get; set; } = 0.2f;
        public float ContactSlop { get; set; } = 0.01f;
        public bool EnableSleep { get; set; } = true;
        public float SleepThreshold { get; set; } = 0.05f;
        public float DefaultDensity { get; set; } = 1000f; // kg/m³
        public float DefaultFriction { get; set; } = 0.5f;
        public float DefaultRestitution { get; set; } = 0.2f;
    }

    /// <summary>
    /// 物理材质 - 定义真实的材质属性
    /// </summary>
    public sealed class PhysicsMaterial
    {
        public string Name { get; set; } = "Default";
        public float StaticFriction { get; set; } = 0.5f;
        public float DynamicFriction { get; set; } = 0.4f;
        public float Restitution { get; set; } = 0.2f;
        public float FrictionCombine { get; set; } = 0.5f; // 平均混合
        public float RestitutionCombine { get; set; } = 0.5f;
        
        /// <summary>
        /// 创建材质组合 - 根据两种材质计算最终的摩擦系数和恢复系数
        /// </summary>
        public static (float friction, float restitution) Combine(PhysicsMaterial materialA, PhysicsMaterial materialB)
        {
            // 使用几何平均计算摩擦系数（更符合物理现实）
            float friction = MathF.Sqrt(materialA.StaticFriction * materialB.StaticFriction);
            
            // 使用最小恢复系数（能量损失更真实）
            float restitution = MathF.Min(materialA.Restitution, materialB.Restitution);
            
            return (friction, restitution);
        }
        
        // 预定义材质
        public static PhysicsMaterial Ice => new() { 
            Name = "Ice", StaticFriction = 0.05f, DynamicFriction = 0.03f, Restitution = 0.1f 
        };
        
        public static PhysicsMaterial Wood => new() { 
            Name = "Wood", StaticFriction = 0.6f, DynamicFriction = 0.5f, Restitution = 0.3f 
        };
        
        public static PhysicsMaterial Metal => new() { 
            Name = "Metal", StaticFriction = 0.7f, DynamicFriction = 0.6f, Restitution = 0.5f 
        };
        
        public static PhysicsMaterial Rubber => new() { 
            Name = "Rubber", StaticFriction = 0.9f, DynamicFriction = 0.8f, Restitution = 0.8f 
        };
        
        public static PhysicsMaterial Concrete => new() { 
            Name = "Concrete", StaticFriction = 0.8f, DynamicFriction = 0.7f, Restitution = 0.1f 
        };
    }
}