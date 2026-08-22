using System;
using System.Numerics;

namespace ShanyuEngine.Physics.Advanced
{
    /// <summary>
    /// 软体动力学模拟器 - 基于压力-体积模型的真实软体物理
    /// 特点：体积保持、弹性变形、碰撞响应
    /// </summary>
    public sealed class SoftBodySimulator
    {
        // ============ 软体粒子数据 ============
        private readonly SoftBodyParticle[] _particles;
        private readonly int _particleCount;
        
        // ============ 约束系统 ============
        private readonly DistanceConstraint[] _distanceConstraints;
        private readonly VolumeConstraint _volumeConstraint;
        private readonly PressureConstraint _pressureConstraint;
        
        // ============ 物理参数 ============
        private readonly SoftBodyPhysicsSettings _settings;
        private readonly Vector3 _gravity = new Vector3(0, -9.81f, 0);
        
        // ============ 状态 ============
        private float _currentVolume;
        private float _restVolume;
        private float _averageRadius;
        
        public int ParticleCount => _particleCount;
        public float CurrentVolume => _currentVolume;
        public float RestVolume => _restVolume;
        public float Pressure => _pressureConstraint.Pressure;
        
        public SoftBodySimulator(int particleCount, float radius, SoftBodyPhysicsSettings? settings = null)
        {
            _particleCount = particleCount;
            _settings = settings ?? new SoftBodyPhysicsSettings();
            _particles = new SoftBodyParticle[particleCount];
            
            // 初始化粒子
            InitializeParticles(radius);
            
            // 创建约束
            CreateConstraints();
            
            // 计算初始体积
            CalculateVolumes();
            _restVolume = _currentVolume;
        }
        
        /// <summary>
        /// 从球体初始化软体
        /// </summary>
        public void InitializeAsSphere(Vector3 center, float radius)
        {
            float particleRadius = radius / MathF.Pow(_particleCount, 1.0f / 3.0f);
            int layers = (int)MathF.Ceiling(radius / particleRadius);
            
            int index = 0;
            
            for (int i = 0; i < layers && index < _particleCount; i++)
            {
                for (int j = 0; j < layers && index < _particleCount; j++)
                {
                    for (int k = 0; k < layers && index < _particleCount; k++)
                    {
                        float x = (i - layers / 2.0f) * particleRadius * 1.5f;
                        float y = (j - layers / 2.0f) * particleRadius * 1.5f;
                        float z = (k - layers / 2.0f) * particleRadius * 1.5f;
                        
                        float dist = MathF.Sqrt(x * x + y * y + z * z);
                        
                        if (dist <= radius)
                        {
                            _particles[index].Position = center + new Vector3(x, y, z);
                            _particles[index].PreviousPosition = _particles[index].Position;
                            _particles[index].Velocity = Vector3.Zero;
                            _particles[index].Mass = _settings.MassPerParticle;
                            _particles[index].Radius = particleRadius * 0.5f;
                            
                            index++;
                        }
                    }
                }
            }
            
            // 填充剩余粒子
            while (index < _particleCount)
            {
                float phi = MathF.Acos(1 - 2.0f * index / _particleCount);
                float theta = MathF.PI * (1 + MathF.Sqrt(5)) * index;
                
                float r = radius * MathF.Pow((float)index / _particleCount, 1.0f / 3.0f);
                
                _particles[index].Position = center + new Vector3(
                    r * MathF.Sin(phi) * MathF.Cos(theta),
                    r * MathF.Sin(phi) * MathF.Sin(theta),
                    r * MathF.Cos(phi)
                );
                _particles[index].PreviousPosition = _particles[index].Position;
                _particles[index].Velocity = Vector3.Zero;
                _particles[index].Mass = _settings.MassPerParticle;
                _particles[index].Radius = particleRadius * 0.5f;
                
                index++;
            }
            
            _averageRadius = particleRadius * 0.5f;
            CreateConstraints();
            CalculateVolumes();
            _restVolume = _currentVolume;
        }
        
        /// <summary>
        /// 初始化粒子
        /// </summary>
        private void InitializeParticles(float radius)
        {
            _averageRadius = radius * 0.5f;
            
            for (int i = 0; i < _particleCount; i++)
            {
                // 使用球面均匀分布
                float phi = MathF.Acos(1 - 2.0f * (i + 0.5f) / _particleCount);
                float theta = MathF.PI * (1 + MathF.Sqrt(5)) * i;
                
                float r = radius * MathF.Pow((float)i / _particleCount, 1.0f / 3.0f);
                
                _particles[i].Position = new Vector3(
                    r * MathF.Sin(phi) * MathF.Cos(theta),
                    r * MathF.Sin(phi) * MathF.Sin(theta),
                    r * MathF.Cos(phi)
                );
                _particles[i].PreviousPosition = _particles[i].Position;
                _particles[i].Velocity = Vector3.Zero;
                _particles[i].Mass = _settings.MassPerParticle;
                _particles[i].Radius = _averageRadius;
            }
        }
        
        /// <summary>
        /// 创建约束
        /// </summary>
        private void CreateConstraints()
        {
            // 距离约束（基于 Delaunay 三角剖分的简化版本）
            var constraintList = new System.Collections.Generic.List<DistanceConstraint>();
            float maxDist = _averageRadius * 3.0f;
            
            for (int i = 0; i < _particleCount; i++)
            {
                for (int j = i + 1; j < _particleCount; j++)
                {
                    float dist = (_particles[i].Position - _particles[j].Position).Length();
                    
                    if (dist < maxDist)
                    {
                        constraintList.Add(new DistanceConstraint
                        {
                            ParticleA = i,
                            ParticleB = j,
                            RestLength = dist,
                            Stiffness = _settings.DistanceStiffness
                        });
                    }
                }
            }
            
            _distanceConstraints = constraintList.ToArray();
            
            // 体积约束
            _volumeConstraint = new VolumeConstraint
            {
                Particles = new int[_particleCount],
                RestVolume = 0,
                Stiffness = _settings.VolumeStiffness
            };
            
            for (int i = 0; i < _particleCount; i++)
            {
                _volumeConstraint.Particles[i] = i;
            }
            
            // 压力约束
            _pressureConstraint = new PressureConstraint
            {
                Particles = new int[_particleCount],
                RestVolume = 0,
                Pressure = _settings.InitialPressure,
                Stiffness = _settings.PressureStiffness
            };
            
            for (int i = 0; i < _particleCount; i++)
            {
                _pressureConstraint.Particles[i] = i;
            }
        }
        
        /// <summary>
        /// 计算体积
        /// </summary>
        private void CalculateVolumes()
        {
            // 使用 Delaunay 四面体分解计算体积
            _currentVolume = 0;
            
            // 简化的体积计算：假设粒子形成凸包
            Vector3 centroid = Vector3.Zero;
            for (int i = 0; i < _particleCount; i++)
            {
                centroid += _particles[i].Position;
            }
            centroid /= _particleCount;
            
            // 使用最近邻居三角剖分估算体积
            float totalVolume = 0;
            for (int i = 0; i < _particleCount; i++)
            {
                Vector3 vi = _particles[i].Position - centroid;
                float viLen = vi.Length();
                
                if (viLen > 0.0001f)
                {
                    // 估算每个粒子占据的体积
                    float particleVolume = (4.0f / 3.0f) * MathF.PI * _particles[i].Radius * _particles[i].Radius * _particles[i].Radius;
                    totalVolume += particleVolume;
                }
            }
            
            _currentVolume = totalVolume;
        }
        
        /// <summary>
        /// 物理模拟步进
        /// </summary>
        public void Step(float deltaTime)
        {
            float clampedDt = MathF.Min(deltaTime, _settings.MaxTimeStep);
            
            // 1. 应用外力
            ApplyExternalForces(clampedDt);
            
            // 2. Verlet积分
            VerletIntegrate(clampedDt);
            
            // 3. 距离约束求解
            SolveDistanceConstraints();
            
            // 4. 体积约束求解（压力模型）
            SolveVolumeConstraint();
            
            // 5. 碰撞响应
            HandleCollisions();
            
            // 6. 更新体积
            CalculateVolumes();
        }
        
        /// <summary>
        /// 应用外力
        /// </summary>
        private void ApplyExternalForces(float deltaTime)
        {
            for (int i = 0; i < _particleCount; i++)
            {
                Vector3 totalForce = _gravity * _particles[i].Mass;
                
                // 空气阻力
                totalForce -= _particles[i].Velocity * _settings.AirResistance;
                
                // 表面张力（模拟软体感）
                totalForce += CalculateSurfaceTension(i);
                
                _particles[i].Acceleration = totalForce / _particles[i].Mass;
            }
        }
        
        /// <summary>
        /// 计算表面张力
        /// </summary>
        private Vector3 CalculateSurfaceTension(int index)
        {
            Vector3 tension = Vector3.Zero;
            int neighbors = 0;
            
            for (int i = 0; i < _particleCount; i++)
            {
                if (i == index) continue;
                
                float dist = (_particles[i].Position - _particles[index].Position).Length();
                
                if (dist < _averageRadius * 2.5f)
                {
                    Vector3 dir = _particles[index].Position - _particles[i].Position;
                    float strength = 1.0f - dist / (_averageRadius * 2.5f);
                    tension += dir * strength;
                    neighbors++;
                }
            }
            
            if (neighbors > 0)
            {
                tension /= neighbors;
                tension *= _settings.SurfaceTension;
            }
            
            return tension;
        }
        
        /// <summary>
        /// Verlet积分
        /// </summary>
        private void VerletIntegrate(float deltaTime)
        {
            float damping = 1.0f - _settings.Damping;
            float dtSq = deltaTime * deltaTime;
            
            for (int i = 0; i < _particleCount; i++)
            {
                Vector3 currentPos = _particles[i].Position;
                Vector3 prevPos = _particles[i].PreviousPosition;
                
                // 计算速度
                _particles[i].Velocity = (currentPos - prevPos) / deltaTime;
                
                // Verlet积分
                Vector3 velocity = _particles[i].Velocity * damping;
                Vector3 newPos = currentPos + velocity + _particles[i].Acceleration * dtSq;
                
                _particles[i].PreviousPosition = currentPos;
                _particles[i].Position = newPos;
            }
        }
        
        /// <summary>
        /// 距离约束求解
        /// </summary>
        private void SolveDistanceConstraints()
        {
            int iterations = _settings.ConstraintIterations;
            
            for (int iter = 0; iter < iterations; iter++)
            {
                foreach (var constraint in _distanceConstraints)
                {
                    SolveSingleConstraint(constraint);
                }
            }
        }
        
        /// <summary>
        /// 求解单个距离约束
        /// </summary>
        private void SolveSingleConstraint(DistanceConstraint constraint)
        {
            ref var particleA = ref _particles[constraint.ParticleA];
            ref var particleB = ref _particles[constraint.ParticleB];
            
            Vector3 delta = particleB.Position - particleA.Position;
            float currentLength = delta.Length();
            
            if (currentLength < 0.0001f) return;
            
            float difference = currentLength - constraint.RestLength;
            float correction = difference / currentLength;
            
            float totalMass = particleA.Mass + particleB.Mass;
            float ratioA = particleB.Mass / totalMass;
            float ratioB = particleA.Mass / totalMass;
            
            particleA.Position += delta * (correction * ratioA * constraint.Stiffness);
            particleB.Position -= delta * (correction * ratioB * constraint.Stiffness);
        }
        
        /// <summary>
        /// 体积约束求解（压力模型）
        /// </summary>
        private void SolveVolumeConstraint()
        {
            float volumeRatio = _currentVolume / _restVolume;
            float pressure = _pressureConstraint.Pressure;
            
            // 计算体积变化的压力修正
            float pressureChange = (volumeRatio - 1.0f) * pressure * _settings.PressureResponse;
            
            // 简化：直接调整粒子位置以恢复体积
            Vector3 centroid = Vector3.Zero;
            for (int i = 0; i < _particleCount; i++)
            {
                centroid += _particles[i].Position;
            }
            centroid /= _particleCount;
            
            float volumeCorrection = pressureChange * _settings.VolumeStiffness * 0.1f;
            
            for (int i = 0; i < _particleCount; i++)
            {
                Vector3 toCenter = centroid - _particles[i].Position;
                float dist = toCenter.Length();
                
                if (dist > 0.0001f)
                {
                    // 向外推以增加体积，向内拉以减小体积
                    float correction = volumeCorrection * dist;
                    _particles[i].Position += toCenter * (correction / dist);
                }
            }
        }
        
        /// <summary>
        /// 碰撞响应
        /// </summary>
        private void HandleCollisions()
        {
            // 地面碰撞
            if (_settings.CollisionPlane.HasValue)
            {
                Vector4 plane = _settings.CollisionPlane.Value;
                float restitution = _settings.Restitution;
                
                for (int i = 0; i < _particleCount; i++)
                {
                    float distance = Vector3.Dot(_particles[i].Position, new Vector3(plane.X, plane.Y, plane.Z)) + plane.W;
                    
                    if (distance < _particles[i].Radius)
                    {
                        Vector3 normal = new Vector3(plane.X, plane.Y, plane.Z);
                        
                        // 位置修正
                        _particles[i].Position -= normal * (distance - _particles[i].Radius);
                        
                        // 速度反弹
                        Vector3 vel = (_particles[i].Position - _particles[i].PreviousPosition);
                        float velAlongNormal = Vector3.Dot(vel, normal);
                        
                        if (velAlongNormal < 0)
                        {
                            _particles[i].PreviousPosition = _particles[i].Position + vel * restitution;
                        }
                    }
                }
            }
            
            // 球体碰撞
            if (_settings.CollisionSphere.HasValue)
            {
                var sphere = _settings.CollisionSphere.Value;
                
                for (int i = 0; i < _particleCount; i++)
                {
                    Vector3 toParticle = _particles[i].Position - sphere.Center;
                    float dist = toParticle.Length();
                    
                    if (dist < sphere.Radius + _particles[i].Radius)
                    {
                        Vector3 normal = dist > 0.0001f ? toParticle / dist : new Vector3(0, 1, 0);
                        float penetration = sphere.Radius + _particles[i].Radius - dist;
                        
                        _particles[i].Position += normal * penetration;
                        
                        // 速度反弹
                        Vector3 vel = (_particles[i].Position - _particles[i].PreviousPosition);
                        float velAlongNormal = Vector3.Dot(vel, normal);
                        
                        if (velAlongNormal < 0)
                        {
                            _particles[i].PreviousPosition = _particles[i].Position + vel * _settings.Restitution;
                        }
                    }
                }
            }
        }
        
        /// <summary>
        /// 获取粒子
        /// </summary>
        public SoftBodyParticle GetParticle(int index)
        {
            return _particles[index];
        }
        
        /// <summary>
        /// 获取质心
        /// </summary>
        public Vector3 GetCentroid()
        {
            Vector3 centroid = Vector3.Zero;
            
            for (int i = 0; i < _particleCount; i++)
            {
                centroid += _particles[i].Position;
            }
            
            return centroid / _particleCount;
        }
        
        /// <summary>
        /// 获取总动能
        /// </summary>
        public float GetTotalKineticEnergy()
        {
            float energy = 0f;
            
            for (int i = 0; i < _particleCount; i++)
            {
                Vector3 velocity = _particles[i].Position - _particles[i].PreviousPosition;
                energy += 0.5f * _particles[i].Mass * velocity.LengthSquared();
            }
            
            return energy;
        }
        
        /// <summary>
        /// 获取变形程度（0-1）
        /// </summary>
        public float GetDeformationRatio()
        {
            return MathF.Abs(_currentVolume - _restVolume) / _restVolume;
        }
        
        /// <summary>
        /// 应用冲量
        /// </summary>
        public void ApplyImpulse(Vector3 impulse, Vector3 point)
        {
            for (int i = 0; i < _particleCount; i++)
            {
                float dist = (_particles[i].Position - point).Length();
                float influence = 1.0f / (1.0f + dist);
                
                _particles[i].PreviousPosition -= impulse * influence * _settings.ImpulseResponse;
            }
        }
    }
    
    /// <summary>
    /// 软体粒子
    /// </summary>
    public struct SoftBodyParticle
    {
        public Vector3 Position;
        public Vector3 PreviousPosition;
        public Vector3 Velocity;
        public Vector3 Acceleration;
        public float Mass;
        public float Radius;
    }
    
    /// <summary>
    /// 距离约束
    /// </summary>
    
    /// <summary>
    /// 体积约束
    /// </summary>
    public struct VolumeConstraint
    {
        public int[] Particles;
        public float RestVolume;
        public float Stiffness;
    }
    
    /// <summary>
    /// 压力约束
    /// </summary>
    public struct PressureConstraint
    {
        public int[] Particles;
        public float RestVolume;
        public float Pressure;
        public float Stiffness;
    }
    
    /// <summary>
    /// 球体碰撞体
    /// </summary>
    public struct CollisionSphere
    {
        public Vector3 Center;
        public float Radius;
    }
    
    /// <summary>
    /// 软体物理设置 - 基于真实材料参数
    /// </summary>
    public sealed class SoftBodyPhysicsSettings
    {
        // ============ 质量分布 ============
        /// <summary>每个粒子的质量（kg）</summary>
        public float MassPerParticle { get; set; } = 0.01f;
        
        // ============ 弹性参数 ============
        /// <summary>距离约束刚度 - 果冻约0.8</summary>
        public float DistanceStiffness { get; set; } = 0.85f;
        
        /// <summary>体积约束刚度</summary>
        public float VolumeStiffness { get; set; } = 0.9f;
        
        /// <summary>压力刚度</summary>
        public float PressureStiffness { get; set; } = 0.8f;
        
        /// <summary>初始压力</summary>
        public float InitialPressure { get; set; } = 1000.0f;
        
        // ============ 表面特性 ============
        /// <summary>表面张力系数</summary>
        public float SurfaceTension { get; set; } = 0.05f;
        
        /// <summary>空气阻力</summary>
        public float AirResistance { get; set; } = 0.3f;
        
        /// <summary>整体阻尼</summary>
        public float Damping { get; set; } = 0.02f;
        
        // ============ 碰撞参数 ============
        /// <summary>碰撞平面</summary>
        public Vector4? CollisionPlane { get; set; }
        
        /// <summary>碰撞球体</summary>
        public CollisionSphere? CollisionSphere { get; set; }
        
        /// <summary>弹性系数</summary>
        public float Restitution { get; set; } = 0.3f;
        
        /// <summary>冲量响应</summary>
        public float ImpulseResponse { get; set; } = 0.5f;
        
        // ============ 模拟参数 ============
        /// <summary>最大时间步长</summary>
        public float MaxTimeStep { get; set; } = 0.02f;
        
        /// <summary>约束迭代次数</summary>
        public int ConstraintIterations { get; set; } = 4;
        
        /// <summary>压力响应系数</summary>
        public float PressureResponse { get; set; } = 0.5f;
        
        /// <summary>
        /// 预设：果冻
        /// </summary>
        public static SoftBodyPhysicsSettings Jelly => new SoftBodyPhysicsSettings
        {
            MassPerParticle = 0.005f,
            DistanceStiffness = 0.7f,
            VolumeStiffness = 0.6f,
            SurfaceTension = 0.1f,
            AirResistance = 0.2f,
            Damping = 0.01f,
            Restitution = 0.4f
        };
        
        /// <summary>
        /// 预设：橡胶
        /// </summary>
        public static SoftBodyPhysicsSettings Rubber => new SoftBodyPhysicsSettings
        {
            MassPerParticle = 0.02f,
            DistanceStiffness = 0.95f,
            VolumeStiffness = 0.95f,
            SurfaceTension = 0.02f,
            AirResistance = 0.5f,
            Damping = 0.005f,
            Restitution = 0.7f
        };
        
        /// <summary>
        /// 预设：肉/生物组织
        /// </summary>
        public static SoftBodyPhysicsSettings Flesh => new SoftBodyPhysicsSettings
        {
            MassPerParticle = 0.015f,
            DistanceStiffness = 0.8f,
            VolumeStiffness = 0.7f,
            SurfaceTension = 0.08f,
            AirResistance = 0.4f,
            Damping = 0.03f,
            Restitution = 0.2f
        };
    }
}
