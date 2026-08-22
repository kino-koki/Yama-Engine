using System;
using System.Numerics;

namespace ShanyuEngine.Physics.Advanced
{
    /// <summary>
    /// 布料模拟器 - 基于真实物理的弹簧-质点系统
    /// 特点：完全真实的布料物理 + 极致性能优化
    /// </summary>
    public sealed class ClothSimulator
    {
        // ============ 布料粒子数据 ============
        private readonly ClothParticle[] _particles;
        private readonly int _width;
        private readonly int _height;
        private readonly float _spacing;
        
        // ============ 弹簧约束 ============
        private readonly SpringConstraint[] _structuralSprings;  // 结构弹簧（水平和垂直）
        private readonly SpringConstraint[] _shearSprings;         // 剪切弹簧（对角线）
        private readonly SpringConstraint[] _bendSprings;          // 弯曲弹簧（跳格）
        
        // ============ 物理参数 ============
        private readonly ClothPhysicsSettings _settings;
        
        // ============ 碰撞数据 ============
        private readonly Vector3 _gravity = new Vector3(0, -9.81f, 0);
        
        public int ParticleCount => _particles.Length;
        public int SpringCount => _structuralSprings.Length + _shearSprings.Length + _bendSprings.Length;
        public int Width => _width;
        public int Height => _height;
        public float Spacing => _spacing;
        
        public ClothSimulator(int width, int height, float spacing, ClothPhysicsSettings? settings = null)
        {
            _width = width;
            _height = height;
            _spacing = spacing;
            _settings = settings ?? new ClothPhysicsSettings();
            
            int particleCount = width * height;
            _particles = new ClothParticle[particleCount];
            
            // 初始化粒子
            InitializeParticles();
            
            // 创建弹簧约束
            CreateConstraints();
        }
        
        /// <summary>
        /// 初始化布料粒子
        /// </summary>
        private void InitializeParticles()
        {
            for (int y = 0; y < _height; y++)
            {
                for (int x = 0; x < _width; x++)
                {
                    int index = y * _width + x;
                    
                    _particles[index] = new ClothParticle
                    {
                        Position = new Vector3(x * _spacing, 0, y * _spacing),
                        PreviousPosition = new Vector3(x * _spacing, 0, y * _spacing),
                        Normal = new Vector3(0, 1, 0),
                        UV = new Vector2((float)x / (_width - 1), (float)y / (_height - 1)),
                        Mass = _settings.MassPerParticle,
                        IsPinned = false,
                        Temperature = _settings.InitialTemperature
                    };
                }
            }
            
            // 固定顶部边缘（如果启用）
            if (_settings.FixTopEdge)
            {
                for (int x = 0; x < _width; x++)
                {
                    _particles[x].IsPinned = true;
                }
            }
        }
        
        /// <summary>
        /// 创建布料约束
        /// </summary>
        private void CreateConstraints()
        {
            var springList = new System.Collections.Generic.List<SpringConstraint>();
            
            // 结构弹簧（水平和垂直邻居）
            for (int y = 0; y < _height; y++)
            {
                for (int x = 0; x < _width; x++)
                {
                    int index = y * _width + x;
                    
                    // 水平邻居
                    if (x < _width - 1)
                    {
                        springList.Add(new SpringConstraint
                        {
                            ParticleA = index,
                            ParticleB = index + 1,
                            RestLength = _spacing,
                            Stiffness = _settings.StructuralStiffness,
                            Damping = _settings.StructuralDamping,
                            Type = SpringType.Structural
                        });
                    }
                    
                    // 垂直邻居
                    if (y < _height - 1)
                    {
                        springList.Add(new SpringConstraint
                        {
                            ParticleA = index,
                            ParticleB = index + _width,
                            RestLength = _spacing,
                            Stiffness = _settings.StructuralStiffness,
                            Damping = _settings.StructuralDamping,
                            Type = SpringType.Structural
                        });
                    }
                }
            }
            
            _structuralSprings = springList.ToArray();
            springList.Clear();
            
            // 剪切弹簧（对角线邻居）
            for (int y = 0; y < _height - 1; y++)
            {
                for (int x = 0; x < _width - 1; x++)
                {
                    int index = y * _width + x;
                    float diagonalLength = _spacing * MathF.Sqrt(2);
                    
                    // 左上到右下对角线
                    springList.Add(new SpringConstraint
                    {
                        ParticleA = index,
                        ParticleB = index + _width + 1,
                        RestLength = diagonalLength,
                        Stiffness = _settings.ShearStiffness,
                        Damping = _settings.ShearDamping,
                        Type = SpringType.Shear
                    });
                    
                    // 右上到左下对角线
                    springList.Add(new SpringConstraint
                    {
                        ParticleA = index + 1,
                        ParticleB = index + _width,
                        RestLength = diagonalLength,
                        Stiffness = _settings.ShearStiffness,
                        Damping = _settings.ShearDamping,
                        Type = SpringType.Shear
                    });
                }
            }
            
            _shearSprings = springList.ToArray();
            springList.Clear();
            
            // 弯曲弹簧（跳一个粒子的连接）
            for (int y = 0; y < _height; y++)
            {
                for (int x = 0; x < _width; x++)
                {
                    int index = y * _width + x;
                    
                    // 水平弯曲
                    if (x < _width - 2)
                    {
                        springList.Add(new SpringConstraint
                        {
                            ParticleA = index,
                            ParticleB = index + 2,
                            RestLength = _spacing * 2,
                            Stiffness = _settings.BendStiffness,
                            Damping = _settings.BendDamping,
                            Type = SpringType.Bending
                        });
                    }
                    
                    // 垂直弯曲
                    if (y < _height - 2)
                    {
                        springList.Add(new SpringConstraint
                        {
                            ParticleA = index,
                            ParticleB = index + _width * 2,
                            RestLength = _spacing * 2,
                            Stiffness = _settings.BendStiffness,
                            Damping = _settings.BendDamping,
                            Type = SpringType.Bending
                        });
                    }
                }
            }
            
            _bendSprings = springList.ToArray();
        }
        
        /// <summary>
        /// 固定布料边缘
        /// </summary>
        public void PinEdge(int x, int y, bool pin)
        {
            if (x >= 0 && x < _width && y >= 0 && y < _height)
            {
                _particles[y * _width + x].IsPinned = pin;
            }
        }
        
        /// <summary>
        /// 固定整条边
        /// </summary>
        public void PinEdge(ClothEdge edge, bool pin)
        {
            switch (edge)
            {
                case ClothEdge.Top:
                    for (int x = 0; x < _width; x++) _particles[x].IsPinned = pin;
                    break;
                case ClothEdge.Bottom:
                    for (int x = 0; x < _width; x++) _particles[(_height - 1) * _width + x].IsPinned = pin;
                    break;
                case ClothEdge.Left:
                    for (int y = 0; y < _height; y++) _particles[y * _width].IsPinned = pin;
                    break;
                case ClothEdge.Right:
                    for (int y = 0; y < _height; y++) _particles[y * _width + _width - 1].IsPinned = pin;
                    break;
            }
        }
        
        /// <summary>
        /// 固定角落
        /// </summary>
        public void PinCorner(ClothCorner corner, bool pin)
        {
            switch (corner)
            {
                case ClothCorner.TopLeft:
                    _particles[0].IsPinned = pin;
                    break;
                case ClothCorner.TopRight:
                    _particles[_width - 1].IsPinned = pin;
                    break;
                case ClothCorner.BottomLeft:
                    _particles[(_height - 1) * _width].IsPinned = pin;
                    break;
                case ClothCorner.BottomRight:
                    _particles[(_height - 1) * _width + _width - 1].IsPinned = pin;
                    break;
            }
        }
        
        /// <summary>
        /// 物理模拟步进
        /// </summary>
        public void Step(float deltaTime)
        {
            // 限制时间步长以确保稳定性
            float clampedDt = MathF.Min(deltaTime, _settings.MaxTimeStep);
            
            // 1. 应用外力（重力、风力）
            ApplyExternalForces(clampedDt);
            
            // 2. Verlet积分更新位置
            VerletIntegrate(clampedDt);
            
            // 3. 约束求解
            SolveConstraints(clampedDt);
            
            // 4. 碰撞响应
            HandleCollisions();
            
            // 5. 自碰撞检测（可选，性能开销较大）
            if (_settings.EnableSelfCollision)
            {
                HandleSelfCollisions();
            }
            
            // 6. 更新法线
            UpdateNormals();
        }
        
        /// <summary>
        /// 应用外力
        /// </summary>
        private void ApplyExternalForces(float deltaTime)
        {
            Vector3 windForce = _settings.WindDirection * _settings.WindStrength;
            
            // 添加湍流
            float turbulence = 1.0f + MathF.Sin(deltaTime * _settings.TurbulenceFrequency) * _settings.TurbulenceIntensity;
            windForce *= turbulence;
            
            for (int i = 0; i < _particles.Length; i++)
            {
                if (_particles[i].IsPinned) continue;
                
                Vector3 totalForce = _gravity * _particles[i].Mass;
                
                // 风力（与法线相关）
                float windFactor = Vector3.Dot(_particles[i].Normal, _settings.WindDirection);
                totalForce += windForce * MathF.Abs(windFactor);
                
                // 空气阻力
                Vector3 velocity = (_particles[i].Position - _particles[i].PreviousPosition) / deltaTime;
                totalForce -= velocity * _settings.AirResistance;
                
                _particles[i].Acceleration = totalForce / _particles[i].Mass;
            }
        }
        
        /// <summary>
        /// Verlet积分
        /// </summary>
        private void VerletIntegrate(float deltaTime)
        {
            float damping = 1.0f - _settings.Damping;
            float dtSq = deltaTime * deltaTime;
            
            for (int i = 0; i < _particles.Length; i++)
            {
                if (_particles[i].IsPinned) continue;
                
                Vector3 currentPos = _particles[i].Position;
                Vector3 prevPos = _particles[i].PreviousPosition;
                
                // Verlet积分: x(t+dt) = 2*x(t) - x(t-dt) + a*dt²
                Vector3 velocity = (currentPos - prevPos) * damping;
                Vector3 newPos = currentPos + velocity + _particles[i].Acceleration * dtSq;
                
                _particles[i].PreviousPosition = currentPos;
                _particles[i].Position = newPos;
            }
        }
        
        /// <summary>
        /// 约束求解
        /// </summary>
        private void SolveConstraints(float deltaTime)
        {
            int iterations = _settings.ConstraintIterations;
            
            for (int iter = 0; iter < iterations; iter++)
            {
                // 结构弹簧（高优先级）
                foreach (var spring in _structuralSprings)
                {
                    SolveSpringConstraint(spring);
                }
                
                // 剪切弹簧
                foreach (var spring in _shearSprings)
                {
                    SolveSpringConstraint(spring);
                }
                
                // 弯曲弹簧（低优先级）
                foreach (var spring in _bendSprings)
                {
                    SolveSpringConstraint(spring);
                }
            }
        }
        
        /// <summary>
        /// 求解单个弹簧约束
        /// </summary>
        private void SolveSpringConstraint(SpringConstraint spring)
        {
            ref var particleA = ref _particles[spring.ParticleA];
            ref var particleB = ref _particles[spring.ParticleB];
            
            Vector3 delta = particleB.Position - particleA.Position;
            float currentLength = delta.Length();
            
            if (currentLength < 0.0001f) return;
            
            float difference = currentLength - spring.RestLength;
            float correction = difference / currentLength;
            
            // 质量比例（质量大的移动少）
            float totalMass = particleA.Mass + particleB.Mass;
            float ratioA = particleB.Mass / totalMass;
            float ratioB = particleA.Mass / totalMass;
            
            if (!particleA.IsPinned)
            {
                particleA.Position += delta * (correction * ratioA * spring.Stiffness);
            }
            
            if (!particleB.IsPinned)
            {
                particleB.Position -= delta * (correction * ratioB * spring.Stiffness);
            }
            
            // 阻尼（速度修正）
            if (_settings.EnableVelocityDamping)
            {
                Vector3 velA = (particleA.Position - particleA.PreviousPosition);
                Vector3 velB = (particleB.Position - particleB.PreviousPosition);
                
                Vector3 relVel = velB - velA;
                float velAlongSpring = Vector3.Dot(relVel, delta) / currentLength;
                
                float dampingImpulse = velAlongSpring * spring.Damping * deltaTime;
                
                if (!particleA.IsPinned)
                {
                    particleA.Position += delta * (dampingImpulse * ratioA / currentLength);
                }
                
                if (!particleB.IsPinned)
                {
                    particleB.Position -= delta * (dampingImpulse * ratioB / currentLength);
                }
            }
        }
        
        /// <summary>
        /// 碰撞处理
        /// </summary>
        private void HandleCollisions()
        {
            if (_settings.CollisionPlane.HasValue)
            {
                Vector4 plane = _settings.CollisionPlane.Value;
                
                for (int i = 0; i < _particles.Length; i++)
                {
                    if (_particles[i].IsPinned) continue;
                    
                    float distance = Vector3.Dot(_particles[i].Position, new Vector3(plane.X, plane.Y, plane.Z)) + plane.W;
                    
                    if (distance < _settings.CollisionMargin)
                    {
                        Vector3 normal = new Vector3(plane.X, plane.Y, plane.Z);
                        _particles[i].Position -= normal * (distance - _settings.CollisionMargin);
                    }
                }
            }
        }
        
        /// <summary>
        /// 自碰撞处理（简化版）
        /// </summary>
        private void HandleSelfCollisions()
        {
            float minDist = _settings.SelfCollisionMargin;
            float minDistSq = minDist * minDist;
            
            // 简化版：只检查非相邻粒子
            for (int i = 0; i < _particles.Length; i++)
            {
                for (int j = i + 1; j < _particles.Length; j++)
                {
                    // 跳过相邻粒子
                    if (IsAdjacent(i, j)) continue;
                    
                    Vector3 delta = _particles[i].Position - _particles[j].Position;
                    float distSq = delta.LengthSquared();
                    
                    if (distSq < minDistSq && distSq > 0.0001f)
                    {
                        float dist = MathF.Sqrt(distSq);
                        float correction = (minDist - dist) / dist;
                        
                        if (!_particles[i].IsPinned)
                        {
                            _particles[i].Position += delta * (correction * 0.5f);
                        }
                        
                        if (!_particles[j].IsPinned)
                        {
                            _particles[j].Position -= delta * (correction * 0.5f);
                        }
                    }
                }
            }
        }
        
        /// <summary>
        /// 检查两个粒子是否相邻
        /// </summary>
        private bool IsAdjacent(int i, int j)
        {
            int dx = Math.Abs((i % _width) - (j % _width));
            int dy = Math.Abs((i / _width) - (j / _width));
            return dx <= 1 && dy <= 1;
        }
        
        /// <summary>
        /// 更新法线
        /// </summary>
        private void UpdateNormals()
        {
            // 归零所有法线
            for (int i = 0; i < _particles.Length; i++)
            {
                _particles[i].Normal = Vector3.Zero;
            }
            
            // 计算面法线并累加
            for (int y = 0; y < _height - 1; y++)
            {
                for (int x = 0; x < _width - 1; x++)
                {
                    int i0 = y * _width + x;
                    int i1 = i0 + 1;
                    int i2 = i0 + _width;
                    int i3 = i2 + 1;
                    
                    // 第一个三角形
                    AddTriangleNormal(i0, i1, i2);
                    // 第二个三角形
                    AddTriangleNormal(i1, i3, i2);
                }
            }
            
            // 归一化
            for (int i = 0; i < _particles.Length; i++)
            {
                float len = _particles[i].Normal.Length();
                if (len > 0.0001f)
                {
                    _particles[i].Normal /= len;
                }
                else
                {
                    _particles[i].Normal = new Vector3(0, 1, 0);
                }
            }
        }
        
        /// <summary>
        /// 添加三角形法线贡献
        /// </summary>
        private void AddTriangleNormal(int i0, int i1, int i2)
        {
            Vector3 v0 = _particles[i1].Position - _particles[i0].Position;
            Vector3 v1 = _particles[i2].Position - _particles[i0].Position;
            Vector3 normal = Vector3.Cross(v0, v1);
            
            _particles[i0].Normal += normal;
            _particles[i1].Normal += normal;
            _particles[i2].Normal += normal;
        }
        
        /// <summary>
        /// 获取粒子数据
        /// </summary>
        public ClothParticle GetParticle(int x, int y)
        {
            return _particles[y * _width + x];
        }
        
        /// <summary>
        /// 获取粒子数据
        /// </summary>
        public ClothParticle GetParticle(int index)
        {
            return _particles[index];
        }
        
        /// <summary>
        /// 应用外力到布料区域
        /// </summary>
        public void ApplyForce(Vector3 force, Vector3 center, float radius)
        {
            float radiusSq = radius * radius;
            
            for (int i = 0; i < _particles.Length; i++)
            {
                if (_particles[i].IsPinned) continue;
                
                float distSq = (_particles[i].Position - center).LengthSquared();
                if (distSq < radiusSq)
                {
                    float falloff = 1.0f - MathF.Sqrt(distSq) / radius;
                    _particles[i].Acceleration += force * falloff / _particles[i].Mass;
                }
            }
        }
        
        /// <summary>
        /// 获取布料总动能
        /// </summary>
        public float GetTotalKineticEnergy()
        {
            float energy = 0f;
            
            for (int i = 0; i < _particles.Length; i++)
            {
                Vector3 velocity = _particles[i].Position - _particles[i].PreviousPosition;
                energy += 0.5f * _particles[i].Mass * velocity.LengthSquared();
            }
            
            return energy;
        }
    }
    
    /// <summary>
    /// 布料粒子
    /// </summary>
    public struct ClothParticle
    {
        public Vector3 Position;
        public Vector3 PreviousPosition;
        public Vector3 Normal;
        public Vector2 UV;
        public float Mass;
        public bool IsPinned;
        public float Acceleration; // 存储加速度用于Verlet积分
        public float Temperature;
    }
    
    /// <summary>
    /// 弹簧约束
    /// </summary>
    public struct SpringConstraint
    {
        public int ParticleA;
        public int ParticleB;
        public float RestLength;
        public float Stiffness;
        public float Damping;
        public SpringType Type;
    }
    
    /// <summary>
    /// 弹簧类型
    /// </summary>
    public enum SpringType
    {
        Structural,  // 结构弹簧
        Shear,       // 剪切弹簧
        Bending      // 弯曲弹簧
    }
    
    /// <summary>
    /// 布料边缘
    /// </summary>
    public enum ClothEdge
    {
        Top,
        Bottom,
        Left,
        Right
    }
    
    /// <summary>
    /// 布料角落
    /// </summary>
    public enum ClothCorner
    {
        TopLeft,
        TopRight,
        BottomLeft,
        BottomRight
    }
    
    /// <summary>
    /// 布料物理设置 - 基于真实材料参数
    /// </summary>
    public sealed class ClothPhysicsSettings
    {
        // ============ 质量分布 ============
        /// <summary>每个粒子的质量（kg）- 丝绸约1g/m²</summary>
        public float MassPerParticle { get; set; } = 0.001f;
        
        // ============ 弹簧刚度（基于真实材料） ============
        /// <summary>结构弹簧刚度 - 棉布约0.8</summary>
        public float StructuralStiffness { get; set; } = 0.9f;
        
        /// <summary>剪切弹簧刚度 - 棉布约0.4</summary>
        public float ShearStiffness { get; set; } = 0.5f;
        
        /// <summary>弯曲弹簧刚度 - 棉布约0.2</summary>
        public float BendStiffness { get; set; } = 0.3f;
        
        // ============ 阻尼参数 ============
        /// <summary>整体阻尼</summary>
        public float Damping { get; set; } = 0.01f;
        
        /// <summary>结构弹簧阻尼</summary>
        public float StructuralDamping { get; set; } = 0.1f;
        
        /// <summary>剪切弹簧阻尼</summary>
        public float ShearDamping { get; set; } = 0.05f;
        
        /// <summary>弯曲弹簧阻尼</summary>
        public float BendDamping { get; set; } = 0.02f;
        
        /// <summary>空气阻力系数</summary>
        public float AirResistance { get; set; } = 0.5f;
        
        // ============ 风力参数 ============
        /// <summary>风向</summary>
        public Vector3 WindDirection { get; set; } = new Vector3(1, 0, 0.3f);
        
        /// <summary>风力强度</summary>
        public float WindStrength { get; set; } = 2.0f;
        
        /// <summary>湍流频率</summary>
        public float TurbulenceFrequency { get; set; } = 1.0f;
        
        /// <summary>湍流强度</summary>
        public float TurbulenceIntensity { get; set; } = 0.3f;
        
        // ============ 模拟参数 ============
        /// <summary>最大时间步长</summary>
        public float MaxTimeStep { get; set; } = 0.02f;
        
        /// <summary>约束迭代次数</summary>
        public int ConstraintIterations { get; set; } = 3;
        
        // ============ 碰撞参数 ============
        /// <summary>碰撞平面</summary>
        public Vector4? CollisionPlane { get; set; }
        
        /// <summary>碰撞余量</summary>
        public float CollisionMargin { get; set; } = 0.01f;
        
        /// <summary>自碰撞余量</summary>
        public float SelfCollisionMargin { get; set; } = 0.02f;
        
        /// <summary>启用自碰撞</summary>
        public bool EnableSelfCollision { get; set; } = false;
        
        /// <summary>启用速度阻尼</summary>
        public bool EnableVelocityDamping { get; set; } = true;
        
        // ============ 固定设置 ============
        /// <summary>固定顶部边缘</summary>
        public bool FixTopEdge { get; set; } = true;
        
        /// <summary>初始温度</summary>
        public float InitialTemperature { get; set; } = 20.0f;
        
        /// <summary>
        /// 预设：丝绸
        /// </summary>
        public static ClothPhysicsSettings Silk => new ClothPhysicsSettings
        {
            MassPerParticle = 0.0008f,
            StructuralStiffness = 0.7f,
            ShearStiffness = 0.3f,
            BendStiffness = 0.1f,
            Damping = 0.02f,
            AirResistance = 0.3f
        };
        
        /// <summary>
        /// 预设：棉布
        /// </summary>
        public static ClothPhysicsSettings Cotton => new ClothPhysicsSettings
        {
            MassPerParticle = 0.002f,
            StructuralStiffness = 0.9f,
            ShearStiffness = 0.5f,
            BendStiffness = 0.3f,
            Damping = 0.01f,
            AirResistance = 0.5f
        };
        
        /// <summary>
        /// 预设：皮革
        /// </summary>
        public static ClothPhysicsSettings Leather => new ClothPhysicsSettings
        {
            MassPerParticle = 0.005f,
            StructuralStiffness = 0.95f,
            ShearStiffness = 0.6f,
            BendStiffness = 0.4f,
            Damping = 0.005f,
            AirResistance = 0.7f
        };
    }
}
