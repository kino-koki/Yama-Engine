using YamaEngine.Assets;

namespace YamaEngine.Physics.Real
{
    /// <summary>
    /// 物理组件 - 连接 SceneObject 和物理引擎
    /// </summary>
    public sealed class PhysicsComponent : IAssetComponent
    {
        public string Name => "Physics";
        
        // 物理引擎引用
        private RealPhysicsEngine? _engine;
        
        // 刚体引用
        private Rigidbody? _rigidbody;
        
        // 碰撞体引用
        private readonly List<Collider> _colliders = new();
        
        // 配置
        public float Mass { get; set; } = 1.0f;
        public RigidbodyType Type { get; set; } = RigidbodyType.Dynamic;
        public bool EnableGravity { get; set; } = true;
        public PhysicsMaterial? Material { get; set; }
        public int CollisionLayer { get; set; } = 1;
        public int CollisionMask { get; set; } = -1;
        
        // 碰撞体配置
        public ColliderType MainColliderType { get; set; } = ColliderType.Box;
        public Vector3 ColliderSize { get; set; } = Vector3.One;
        public float ColliderRadius { get; set; } = 0.5f;
        public float ColliderHeight { get; set; } = 1.0f;
        public bool IsTrigger { get; set; } = false;
        
        // 是否已初始化
        public bool IsInitialized => _rigidbody != null;
        
        // 属性访问器
        public Rigidbody? Rigidbody => _rigidbody;
        public IReadOnlyList<Collider> Colliders => _colliders.AsReadOnly();
        public Vector3 Velocity => _rigidbody?.Velocity ?? Vector3.Zero;
        public Vector3 AngularVelocity => _rigidbody?.AngularVelocity ?? Vector3.Zero;
        public bool IsSleeping => _rigidbody?.IsSleeping ?? false;
        
        /// <summary>
        /// 初始化物理组件
        /// </summary>
        public void Initialize(RealPhysicsEngine engine)
        {
            if (_engine != null || _rigidbody != null)
                return; // 已初始化
                
            _engine = engine ?? throw new ArgumentNullException(nameof(engine));
            
            // 创建刚体
            var desc = new RigidbodyDescription
            {
                Name = "PhysicsBody",
                Type = Type,
                Mass = Mass,
                Material = Material,
                EnableCollision = true,
                CollisionLayer = CollisionLayer,
                CollisionMask = CollisionMask
            };
            
            _rigidbody = _engine.CreateRigidbody(desc);
            
            // 创建主碰撞体
            var colliderDesc = new ColliderDescription
            {
                Type = MainColliderType,
                Size = ColliderSize,
                Radius = ColliderRadius,
                Height = ColliderHeight,
                IsTrigger = IsTrigger,
                Material = Material
            };
            
            var collider = _engine.CreateCollider(colliderDesc, _rigidbody);
            _colliders.Add(collider);
            
            // 计算质量和惯量
            if (Type != RigidbodyType.Static && Type != RigidbodyType.Kinematic)
            {
                var massProps = collider.CalculateMassProperties(_engine.GetDefaultDensity());
                _rigidbody.Mass = massProps.Mass;
                
                // 注意：这里需要设置刚体的惯量张量，但 Rigidbody 类没有公开的 setter
                // 在实际实现中，需要修改 Rigidbody 类或通过构造函数设置
            }
        }
        
        /// <summary>
        /// 同步位置到 SceneObject
        /// </summary>
        public void SyncToSceneObject(SceneObject obj)
        {
            if (_rigidbody == null)
                return;
                
            obj.Position = _rigidbody.Position;
            obj.Orientation = _rigidbody.Orientation;
        }
        
        /// <summary>
        /// 从 SceneObject 同步位置到物理世界
        /// </summary>
        public void SyncFromSceneObject(SceneObject obj)
        {
            if (_rigidbody == null)
                return;
                
            if (Type == RigidbodyType.Kinematic)
            {
                _rigidbody.Position = obj.Position;
                _rigidbody.Orientation = obj.Orientation;
                _rigidbody.Velocity = Vector3.Zero;
                _rigidbody.AngularVelocity = Vector3.Zero;
            }
        }
        
        /// <summary>
        /// 应用力到刚体中心
        /// </summary>
        public void ApplyForce(Vector3 force)
        {
            _rigidbody?.ApplyForce(force);
        }
        
        /// <summary>
        /// 应用力到特定点
        /// </summary>
        public void ApplyForceAtPoint(Vector3 force, Vector3 worldPoint)
        {
            _rigidbody?.ApplyForceAtPoint(force, worldPoint);
        }
        
        /// <summary>
        /// 应用冲量
        /// </summary>
        public void ApplyImpulse(Vector3 impulse)
        {
            _rigidbody?.ApplyImpulse(impulse);
        }
        
        /// <summary>
        /// 在特定点应用冲量
        /// </summary>
        public void ApplyImpulseAtPoint(Vector3 impulse, Vector3 worldPoint)
        {
            _rigidbody?.ApplyImpulseAtPoint(impulse, worldPoint);
        }
        
        /// <summary>
        /// 设置速度
        /// </summary>
        public void SetVelocity(Vector3 velocity)
        {
            if (_rigidbody != null && !_rigidbody.IsStatic)
            {
                _rigidbody.Velocity = velocity;
                _rigidbody.WakeUp();
            }
        }
        
        /// <summary>
        /// 设置角速度
        /// </summary>
        public void SetAngularVelocity(Vector3 angularVelocity)
        {
            if (_rigidbody != null && !_rigidbody.IsStatic)
            {
                _rigidbody.AngularVelocity = angularVelocity;
                _rigidbody.WakeUp();
            }
        }
        
        /// <summary>
        /// 唤醒刚体
        /// </summary>
        public void WakeUp()
        {
            _rigidbody?.WakeUp();
        }
        
        /// <summary>
        /// 设置为静态
        /// </summary>
        public void SetStatic()
        {
            _rigidbody?.SetStatic();
            Type = RigidbodyType.Static;
        }
        
        /// <summary>
        /// 添加额外碰撞体
        /// </summary>
        public Collider AddCollider(ColliderType type, Vector3 center, Vector3 size, float radius = 0.5f, float height = 1.0f)
        {
            if (_engine == null || _rigidbody == null)
                throw new InvalidOperationException("Physics component not initialized");
                
            var desc = new ColliderDescription
            {
                Type = type,
                Center = center,
                Size = size,
                Radius = radius,
                Height = height,
                IsTrigger = IsTrigger,
                Material = Material
            };
            
            var collider = _engine.CreateCollider(desc, _rigidbody);
            _colliders.Add(collider);
            
            return collider;
        }
        
        /// <summary>
        /// 移除碰撞体
        /// </summary>
        public void RemoveCollider(Collider collider)
        {
            if (collider != null)
            {
                _colliders.Remove(collider);
                _rigidbody?.RemoveCollider(collider);
            }
        }
        
        /// <summary>
        /// 清理物理资源
        /// </summary>
        public void Dispose()
        {
            if (_rigidbody != null)
            {
                foreach (var collider in _colliders)
                {
                    _rigidbody.RemoveCollider(collider);
                }
                _colliders.Clear();
                
                // 注意：这里需要引擎提供删除刚体的方法
                // 在实际实现中，应该添加 RealPhysicsEngine.RemoveRigidbody()
                _rigidbody = null;
            }
            
            _engine = null;
        }
        
        /// <summary>
        /// 更新组件
        /// </summary>
        public void Update(float deltaTime, SceneObject owner)
        {
            // 物理更新在引擎主循环中进行，这里只同步位置
            SyncToSceneObject(owner);
        }
    }

    /// <summary>
    /// RealPhysicsEngine 扩展方法
    /// </summary>
    public static class RealPhysicsEngineExtensions
    {
        public static float GetDefaultDensity(this RealPhysicsEngine engine)
        {
            return 1000f; // kg/m³，水的密度
        }
    }
}