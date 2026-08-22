using System;
using System.Numerics;

namespace ShanyuEngine.Physics.Advanced
{
    /// <summary>
    /// 破坏系统 - 基于Voronoi断裂的真实破碎物理
    /// 特点：结构完整性分析、真实破碎模式、碎片动力学
    /// </summary>
    public sealed class DestructionSimulator
    {
        // ============ 待破坏物体 ============
        private readonly DestructibleBody _body;
        
        // ============ 碎片系统 ============
        private readonly System.Collections.Generic.List<Debris> _debris;
        private readonly System.Collections.Generic.List<Fragment> _fragments;
        
        // ============ 物理参数 ============
        private readonly DestructionPhysicsSettings _settings;
        private readonly Vector3 _gravity = new Vector3(0, -9.81f, 0);
        
        public int FragmentCount => _fragments.Count;
        public int DebrisCount => _debris.Count;
        
        public DestructionSimulator(DestructibleBody body, DestructionPhysicsSettings? settings = null)
        {
            _body = body;
            _settings = settings ?? new DestructionPhysicsSettings();
            _debris = new System.Collections.Generic.List<Debris>();
            _fragments = new System.Collections.Generic.List<Fragment>();
        }
        
        /// <summary>
        /// 物理模拟步进
        /// </summary>
        public void Step(float deltaTime)
        {
            float clampedDt = MathF.Min(deltaTime, _settings.MaxTimeStep);
            
            // 更新碎片
            for (int i = _fragments.Count - 1; i >= 0; i--)
            {
                _fragments[i].Update(clampedDt, _gravity);
                
                // 移除超出范围的碎片
                if (_fragments[i].Position.Y < _settings.DespawnHeight)
                {
                    _fragments.RemoveAt(i);
                }
            }
            
            // 更新碎片
            for (int i = _debris.Count - 1; i >= 0; i--)
            {
                _debris[i].Update(clampedDt, _gravity);
                
                // 地面碰撞
                if (_debris[i].Position.Y < 0)
                {
                    _debris[i].Position = new Vector3(_debris[i].Position.X, 0, _debris[i].Position.Z);
                    _debris[i].Velocity = new Vector3(
                        _debris[i].Velocity.X * _settings.GroundFriction,
                        -_debris[i].Velocity.Y * _settings.Restitution,
                        _debris[i].Velocity.Z * _settings.GroundFriction
                    );
                }
                
                // 移除超出范围的碎片
                if (_debris[i].Position.Y < _settings.DespawnHeight - 5)
                {
                    _debris.RemoveAt(i);
                }
            }
            
            // 碎片间碰撞检测
            if (_settings.EnableFragmentCollisions)
            {
                HandleFragmentCollisions();
            }
        }
        
        /// <summary>
        /// 应用冲击
        /// </summary>
        public void ApplyImpact(Vector3 point, Vector3 force)
        {
            // 计算结构应力
            float stress = CalculateStress(point, force);
            
            // 检查是否超过强度
            if (stress > _body.Material.Strength)
            {
                // 触发破碎
                TriggerDestruction(point, force, stress);
            }
        }
        
        /// <summary>
        /// 计算应力
        /// </summary>
        private float CalculateStress(Vector3 point, Vector3 force)
        {
            float distance = (point - _body.Center).Length();
            float areaEffect = MathF.Exp(-distance * distance / (_body.Radius * _body.Radius * 0.5f));
            
            // 应力 = 力 / 面积
            float forceMagnitude = force.Length();
            float stress = forceMagnitude * areaEffect / (_body.Material.CrossSectionArea);
            
            return stress;
        }
        
        /// <summary>
        /// 触发破坏
        /// </summary>
        private void TriggerDestruction(Vector3 point, Vector3 force, float stress)
        {
            // 根据应力大小决定破碎程度
            int fragments = CalculateFragmentCount(stress);
            
            // 生成碎片
            GenerateFragments(point, force, fragments);
            
            // 生成碎片
            GenerateDebris(point, force, stress);
            
            // 触发回调
            _settings.OnDestruction?.Invoke(_body, _fragments, _debris);
        }
        
        /// <summary>
        /// 计算碎片数量
        /// </summary>
        private int CalculateFragmentCount(float stress)
        {
            float stressRatio = stress / _body.Material.Strength;
            
            if (stressRatio < 1.5f) return 3;
            if (stressRatio < 2.0f) return 5;
            if (stressRatio < 3.0f) return 8;
            
            return 12;
        }
        
        /// <summary>
        /// 生成碎片 - 基于Voronoi
        /// </summary>
        private void GenerateFragments(Vector3 impactPoint, Vector3 force, int count)
        {
            Vector3 velocity = force / _body.Mass;
            Vector3 forceDir = force.Length() > 0.0001f ? Vector3.Normalize(force) : new Vector3(0, 1, 0);
            
            // 在冲击点周围生成Voronoi种子点
            var seedPoints = GenerateVoronoiSeeds(impactPoint, count);
            
            // 生成每个碎片
            foreach (var seed in seedPoints)
            {
                // 计算碎片初始位置（沿冲击力方向偏移）
                Vector3 fragmentPos = seed + forceDir * 0.1f;
                
                // 计算碎片初始速度
                float distFromCenter = (seed - _body.Center).Length();
                float speedFactor = 1.0f / (1.0f + distFromCenter);
                Vector3 fragmentVel = velocity * speedFactor + new Vector3(
                    (_settings.Random.NextSingle() - 0.5f) * 2,
                    _settings.Random.NextSingle() * 2,
                    (_settings.Random.NextSingle() - 0.5f) * 2
                );
                
                // 碎片属性
                float mass = _body.Mass / count * (0.5f + _settings.Random.NextSingle() * 0.5f);
                float radius = _body.Radius * MathF.Pow(mass / _body.Mass, 1.0f / 3.0f);
                
                _fragments.Add(new Fragment
                {
                    Position = fragmentPos,
                    Velocity = fragmentVel,
                    AngularVelocity = new Vector3(
                        (_settings.Random.NextSingle() - 0.5f) * 10,
                        (_settings.Random.NextSingle() - 0.5f) * 10,
                        (_settings.Random.NextSingle() - 0.5f) * 10
                    ),
                    Mass = mass,
                    Radius = radius,
                    Material = _body.Material
                });
            }
        }
        
        /// <summary>
        /// 生成Voronoi种子点
        /// </summary>
        private Vector3[] GenerateVoronoiSeeds(Vector3 center, int count)
        {
            var seeds = new Vector3[count];
            
            // 使用泊松盘采样生成均匀分布的种子点
            float radius = _body.Radius * 1.5f;
            
            for (int i = 0; i < count; i++)
            {
                // 在球体内随机分布
                float u = _settings.Random.NextSingle();
                float v = _settings.Random.NextSingle();
                float theta = 2 * MathF.PI * u;
                float phi = MathF.Acos(2 * v - 1);
                float r = radius * MathF.Pow(_settings.Random.NextSingle(), 1.0f / 3.0f);
                
                seeds[i] = center + new Vector3(
                    r * MathF.Sin(phi) * MathF.Cos(theta),
                    r * MathF.Sin(phi) * MathF.Sin(theta),
                    r * MathF.Cos(phi)
                );
            }
            
            return seeds;
        }
        
        /// <summary>
        /// 生成碎片
        /// </summary>
        private void GenerateDebris(Vector3 impactPoint, Vector3 force, float stress)
        {
            int debrisCount = (int)(stress * _settings.DebrisPerStress);
            
            for (int i = 0; i < debrisCount; i++)
            {
                Vector3 debrisPos = impactPoint + new Vector3(
                    (_settings.Random.NextSingle() - 0.5f) * _body.Radius,
                    (_settings.Random.NextSingle() - 0.5f) * _body.Radius,
                    (_settings.Random.NextSingle() - 0.5f) * _body.Radius
                );
                
                Vector3 debrisVel = force / _body.Mass * 2 + new Vector3(
                    (_settings.Random.NextSingle() - 0.5f) * 5,
                    _settings.Random.NextSingle() * 8,
                    (_settings.Random.NextSingle() - 0.5f) * 5
                );
                
                _debris.Add(new Debris
                {
                    Position = debrisPos,
                    Velocity = debrisVel,
                    Size = _settings.MinDebrisSize + _settings.Random.NextSingle() * (_settings.MaxDebrisSize - _settings.MinDebrisSize),
                    Rotation = new Vector3(
                        _settings.Random.NextSingle() * MathF.PI * 2,
                        _settings.Random.NextSingle() * MathF.PI * 2,
                        _settings.Random.NextSingle() * MathF.PI * 2
                    ),
                    AngularVelocity = new Vector3(
                        (_settings.Random.NextSingle() - 0.5f) * 20,
                        (_settings.Random.NextSingle() - 0.5f) * 20,
                        (_settings.Random.NextSingle() - 0.5f) * 20
                    )
                });
            }
        }
        
        /// <summary>
        /// 碎片间碰撞检测
        /// </summary>
        private void HandleFragmentCollisions()
        {
            for (int i = 0; i < _fragments.Count; i++)
            {
                for (int j = i + 1; j < _fragments.Count; j++)
                {
                    var a = _fragments[i];
                    var b = _fragments[j];
                    
                    Vector3 delta = b.Position - a.Position;
                    float dist = delta.Length();
                    float minDist = a.Radius + b.Radius;
                    
                    if (dist < minDist && dist > 0.0001f)
                    {
                        // 分离碎片
                        float overlap = minDist - dist;
                        Vector3 normal = delta / dist;
                        
                        float totalMass = a.Mass + b.Mass;
                        a.Position -= normal * (overlap * b.Mass / totalMass);
                        b.Position += normal * (overlap * a.Mass / totalMass);
                        
                        // 弹性碰撞
                        Vector3 relVel = b.Velocity - a.Velocity;
                        float velAlongNormal = Vector3.Dot(relVel, normal);
                        
                        if (velAlongNormal < 0)
                        {
                            float restitution = (a.Material.Restitution + b.Material.Restitution) * 0.5f;
                            float impulse = -(1 + restitution) * velAlongNormal / totalMass;
                            
                            a.Velocity -= normal * (impulse * b.Mass);
                            b.Velocity += normal * (impulse * a.Mass);
                        }
                        
                        _fragments[i] = a;
                        _fragments[j] = b;
                    }
                }
            }
        }
        
        /// <summary>
        /// 获取碎片
        /// </summary>
        public Fragment GetFragment(int index)
        {
            return _fragments[index];
        }
        
        /// <summary>
        /// 获取碎片
        /// </summary>
        public Debris GetDebris(int index)
        {
            return _debris[index];
        }
    }
    
    /// <summary>
    /// 可破坏物体
    /// </summary>
    public struct DestructibleBody
    {
        public Vector3 Center;
        public float Radius;
        public float Mass;
        public DestructionMaterial Material;
    }
    
    /// <summary>
    /// 碎片
    /// </summary>
    public struct Fragment
    {
        public Vector3 Position;
        public Vector3 Velocity;
        public Vector3 AngularVelocity;
        public float Mass;
        public float Radius;
        public DestructionMaterial Material;
        
        public void Update(float deltaTime, Vector3 gravity)
        {
            // 应用重力
            Velocity += gravity * deltaTime;
            
            // 空气阻力
            Velocity *= 1.0f - 0.01f * deltaTime;
            AngularVelocity *= 1.0f - 0.02f * deltaTime;
            
            // 更新位置
            Position += Velocity * deltaTime;
        }
    }
    
    /// <summary>
    /// 碎片
    /// </summary>
    public struct Debris
    {
        public Vector3 Position;
        public Vector3 Velocity;
        public float Size;
        public Vector3 Rotation;
        public Vector3 AngularVelocity;
        
        public void Update(float deltaTime, Vector3 gravity)
        {
            // 应用重力
            Velocity += gravity * deltaTime;
            
            // 更新位置
            Position += Velocity * deltaTime;
            
            // 更新旋转
            Rotation += AngularVelocity * deltaTime;
        }
    }
    
    /// <summary>
    /// 破坏材料
    /// </summary>
    public struct DestructionMaterial
    {
        /// <summary>抗拉强度（Pa）</summary>
        public float Strength;
        
        /// <summary>密度（kg/m³）</summary>
        public float Density;
        
        /// <summary>弹性模量（Pa）</summary>
        public float ElasticModulus;
        
        /// <summary>横截面积（m²）</summary>
        public float CrossSectionArea;
        
        /// <summary>弹性系数</summary>
        public float Restitution;
        
        /// <summary>
        /// 预设：木材
        /// </summary>
        public static DestructionMaterial Wood => new DestructionMaterial
        {
            Strength = 4e7f,        // 40 MPa
            Density = 600f,         // kg/m³
            ElasticModulus = 1e10f, // 10 GPa
            CrossSectionArea = 0.01f,
            Restitution = 0.3f
        };
        
        /// <summary>
        /// 预设：混凝土
        /// </summary>
        public static DestructionMaterial Concrete => new DestructionMaterial
        {
            Strength = 3e6f,         // 3 MPa
            Density = 2400f,         // kg/m³
            ElasticModulus = 3e9f,   // 3 GPa
            CrossSectionArea = 0.01f,
            Restitution = 0.1f
        };
        
        /// <summary>
        /// 预设：玻璃
        /// </summary>
        public static DestructionMaterial Glass => new DestructionMaterial
        {
            Strength = 3e7f,         // 30 MPa
            Density = 2500f,         // kg/m³
            ElasticModulus = 7e10f,  // 70 GPa
            CrossSectionArea = 0.01f,
            Restitution = 0.2f
        };
        
        /// <summary>
        /// 预设：钢
        /// </summary>
        public static DestructionMaterial Steel => new DestructionMaterial
        {
            Strength = 2.5e8f,       // 250 MPa
            Density = 7850f,         // kg/m³
            ElasticModulus = 2e11f,  // 200 GPa
            CrossSectionArea = 0.01f,
            Restitution = 0.5f
        };
    }
    
    /// <summary>
    /// 破坏物理设置
    /// </summary>
    public sealed class DestructionPhysicsSettings
    {
        /// <summary>最大时间步长</summary>
        public float MaxTimeStep { get; set; } = 0.02f;
        
        /// <summary>碎片恢复系数</summary>
        public float Restitution { get; set; } = 0.3f;
        
        /// <summary>地面摩擦</summary>
        public float GroundFriction { get; set; } = 0.8f;
        
        /// <summary>每个应力单位的碎片数</summary>
        public float DebrisPerStress { get; set; } = 0.1f;
        
        /// <summary>最小碎片大小</summary>
        public float MinDebrisSize { get; set; } = 0.01f;
        
        /// <summary>最大碎片大小</summary>
        public float MaxDebrisSize { get; set; } = 0.1f;
        
        /// <summary>碎片销毁高度</summary>
        public float DespawnHeight { get; set; } = -50f;
        
        /// <summary>启用碎片碰撞</summary>
        public bool EnableFragmentCollisions { get; set; } = true;
        
        /// <summary>随机数生成器</summary>
        public Random Random { get; set; } = new Random();
        
        /// <summary>破坏回调</summary>
        public Action<DestructibleBody, System.Collections.Generic.List<Fragment>, System.Collections.Generic.List<Debris>>? OnDestruction { get; set; }
    }
}
