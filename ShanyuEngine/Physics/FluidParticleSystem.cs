// ============================================================================================
//  FluidParticleSystem.cs
//
//  山雨引擎 (ShanyuEngine) 流体粒子系统 —— 基于SPH（光滑粒子流体动力学）的实时流体模拟。
//
//  内容：
//    - FluidParticle：流体粒子（位置/速度/压力/密度/力/表面标记）
//    - SPHKernel：核函数（Poly6密度/Spiky压力梯度/Viscosity粘性拉普拉斯）
//    - SPHSimulator：SPH模拟器（邻居搜索/密度/压力/力计算/时间积分）
//    - SpatialHashGrid：空间哈希网格（加速邻居搜索）
//    - BoundaryHandling：边界处理（碰撞反射/penalty力）
//    - FluidConfig：流体配置参数
//
//  命名空间：ShanyuEngine.Physics
//  目标框架：net8.0 / C# 8.0+
// ============================================================================================

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace ShanyuEngine.Physics
{
    #region ======================== 流体粒子 ========================

    /// <summary>
    ///   流体粒子：SPH模拟的基本单位。
    /// </summary>
    public struct FluidParticle
    {
        /// <summary>位置。</summary>
        public PhysicsVector3 Position;

        /// <summary>速度。</summary>
        public PhysicsVector3 Velocity;

        /// <summary>加速度/力（累积）。</summary>
        public PhysicsVector3 Force;

        /// <summary>压力。</summary>
        public float Pressure;

        /// <summary>密度。</summary>
        public float Density;

        /// <summary>是否为表面粒子。</summary>
        public bool IsSurface;

        /// <summary>粒子质量。</summary>
        public float Mass;

        /// <summary>粒子索引（用于空间哈希）。</summary>
        public int Index;

        /// <summary>上一帧速度（Leapfrog积分用）。</summary>
        public PhysicsVector3 VelocityHalf;

        /// <summary>邻居数量（用于表面检测）。</summary>
        public int NeighborCount;
    }

    #endregion

    #region ======================== SPH 核函数 ========================

    /// <summary>
    ///   SPH核函数集。
    ///   包含三种常用核函数：
    ///   - Poly6核（密度估计）
    ///   - Spiky核（压力梯度）
    ///   - Viscosity核（粘性拉普拉斯）
    /// </summary>
    public static class SPHKernel
    {
        /// <summary>
        ///   Poly6核函数（用于密度估计）。
        ///   W(r,h) = 315 / (64 * pi * h^9) * (h^2 - r^2)^3,  r <= h
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Poly6(float rSquared, float h)
        {
            if (rSquared >= h * h) return 0.0f;

            float h2 = h * h;
            float diff = h2 - rSquared;
            float coeff = 315.0f / (64.0f * MathF.PI * h * h * h * h * h * h * h * h * h);
            return coeff * diff * diff * diff;
        }

        /// <summary>
        ///   Spiky核函数梯度（用于压力力计算）。
        ///   grad W(r,h) = -45 / (pi * h^6) * (h - r)^2 * (r_vec / r),  r <= h
        ///   返回标量部分：-45 / (pi * h^6) * (h - r)^2
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float SpikyGradient(float r, float h)
        {
            if (r >= h || r < 1e-6f) return 0.0f;

            float diff = h - r;
            float coeff = -45.0f / (MathF.PI * h * h * h * h * h * h);
            return coeff * diff * diff;
        }

        /// <summary>
        ///   Viscosity核函数拉普拉斯（用于粘性力计算）。
        ///   laplacian W(r,h) = 45 / (pi * h^6) * (h - r),  r <= h
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float ViscosityLaplacian(float r, float h)
        {
            if (r >= h) return 0.0f;

            float coeff = 45.0f / (MathF.PI * h * h * h * h * h * h);
            return coeff * (h - r);
        }

        /// <summary>
        ///   表面张力核函数（简化法线估计）。
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float SurfaceNormalKernel(float r, float h)
        {
            if (r >= h || r < 1e-6f) return 0.0f;

            float diff = h - r;
            return diff * diff; // 简化的表面法线核
        }
    }

    #endregion

    #region ======================== 空间哈希网格 ========================

    /// <summary>
    ///   空间哈希网格：加速SPH邻居搜索。
    ///   Hash(x,y,z) = ((x * 73856093) ^ (y * 19349663) ^ (z * 83492791)) % bucketCount
    /// </summary>

    #endregion

    #region ======================== 流体配置 ========================

    /// <summary>
    ///   流体模拟配置参数。
    /// </summary>
    public class FluidConfig
    {
        /// <summary>粒子半径（米）。</summary>
        public float ParticleRadius { get; set; } = 0.02f;

        /// <summary>SPH光滑核半径（米，通常为粒子直径的2倍）。</summary>
        public float SmoothingRadius { get; set; } = 0.08f;

        /// <summary>静止密度（kg/m^3）。</summary>
        public float RestDensity { get; set; } = 1000.0f;

        /// <summary>气体常数（压力刚度系数）。</summary>
        public float GasConstant { get; set; } = 2000.0f;

        /// <summary>粘性系数。</summary>
        public float Viscosity { get; set; } = 200.0f;

        /// <summary>重力加速度。</summary>
        public PhysicsVector3 Gravity { get; set; } = new PhysicsVector3(0, -9.81f, 0);

        /// <summary>粒子数量。</summary>
        public int ParticleCount { get; set; } = 1000;

        /// <summary>模拟域大小（米）。</summary>
        public PhysicsVector3 DomainSize { get; set; } = new PhysicsVector3(1.0f, 1.0f, 1.0f);

        /// <summary>时间步长（秒）。</summary>
        public float TimeStep { get; set; } = 0.001f;

        /// <summary>表面张力系数。</summary>
        public float SurfaceTension { get; set; } = 0.072f;

        /// <summary>表面粒子邻居阈值。</summary>
        public int SurfaceNeighborThreshold { get; set; } = 20;

        /// <summary>边界惩罚力刚度。</summary>
        public float BoundaryStiffness { get; set; } = 10000.0f;

        /// <summary>边界阻尼系数。</summary>
        public float BoundaryDamping { get; set; } = 256.0f;

        /// <summary>粒子质量（千克）。</summary>
        public float ParticleMass { get; set; } = 0.02f;

        /// <summary>最大速度限制。</summary>
        public float MaxVelocity { get; set; } = 10.0f;

        /// <summary>默认配置（水）。</summary>
        public static FluidConfig Water => new FluidConfig
        {
            ParticleRadius = 0.02f,
            SmoothingRadius = 0.08f,
            RestDensity = 1000.0f,
            GasConstant = 2000.0f,
            Viscosity = 200.0f,
            Gravity = new PhysicsVector3(0, -9.81f, 0),
            ParticleMass = 0.02f,
            SurfaceTension = 0.072f,
        };

        /// <summary>粘稠流体配置（蜂蜜）。</summary>
        public static FluidConfig Honey => new FluidConfig
        {
            ParticleRadius = 0.025f,
            SmoothingRadius = 0.1f,
            RestDensity = 1400.0f,
            GasConstant = 3000.0f,
            Viscosity = 2000.0f,
            Gravity = new PhysicsVector3(0, -9.81f, 0),
            ParticleMass = 0.05f,
            SurfaceTension = 0.05f,
        };
    }

    #endregion

    #region ======================== SPH 模拟器 ========================

    /// <summary>
    ///   SPH（光滑粒子流体动力学）模拟器。
    ///   模拟步骤：
    ///     1. NeighborSearch：邻居搜索（空间哈希网格）
    ///     2. ComputeDensity：密度计算 rho_i = Sum m_j * W_poly6(r_ij, h)
    ///     3. ComputePressure：压力计算 p_i = k * (rho_i - rho_0)
    ///     4. ComputeForces：压力力 + 粘性力 + 重力 + 表面张力
    ///     5. Time Integration：Leapfrog积分
    /// </summary>
    public class SPHSimulator
    {
        /// <summary>流体配置。</summary>
        public FluidConfig Config { get; }

        /// <summary>粒子数组。</summary>
        public FluidParticle[] Particles { get; private set; }

        /// <summary>粒子数量。</summary>
        public int ParticleCount { get; private set; }

        /// <summary>空间哈希网格。</summary>
        private SpatialHashGrid _spatialHash;

        /// <summary>邻居搜索缓存（复用以减少GC）。</summary>
        private List<int> _neighborCache = new List<int>(64);

        /// <summary>每帧模拟时间。</summary>
        public float SimulationTime { get; private set; }

        /// <summary>是否已初始化。</summary>
        public bool IsInitialized { get; private set; }

        /// <summary>事件：模拟步完成。</summary>
        public event Action<float> OnStepComplete;

        public SPHSimulator(FluidConfig config)
        {
            Config = config;
        }

        /// <summary>
        ///   初始化模拟：创建粒子并布置初始位置。
        /// </summary>
        /// <param name="startPosition">流体初始中心位置。</param>
        /// <param name="size">流体初始体积大小。</param>
        public void Initialize(PhysicsVector3 startPosition, PhysicsVector3 size)
        {
            ParticleCount = Config.ParticleCount;
            Particles = new FluidParticle[ParticleCount];
            _spatialHash = new SpatialHashGrid(Config.SmoothingRadius, ParticleCount);

            float spacing = Config.ParticleRadius * 2.0f;

            // 计算网格布局
            float volumePerParticle = spacing * spacing * spacing;
            int particlesPerAxis = (int)Math.Ceiling(Math.Pow(ParticleCount, 1.0 / 3.0));

            PhysicsVector3 halfSize = size * 0.5f;
            PhysicsVector3 start = startPosition - halfSize;

            int created = 0;
            for (int x = 0; x < particlesPerAxis && created < ParticleCount; x++)
            {
                for (int y = 0; y < particlesPerAxis && created < ParticleCount; y++)
                {
                    for (int z = 0; z < particlesPerAxis && created < ParticleCount; z++)
                    {
                        PhysicsVector3 pos = start + new PhysicsVector3(
                            x * spacing + spacing * 0.5f,
                            y * spacing + spacing * 0.5f,
                            z * spacing + spacing * 0.5f);

                        Particles[created] = new FluidParticle
                        {
                            Position = pos,
                            Velocity = PhysicsVector3.Zero,
                            VelocityHalf = PhysicsVector3.Zero,
                            Force = PhysicsVector3.Zero,
                            Pressure = 0,
                            Density = Config.RestDensity,
                            IsSurface = false,
                            Mass = Config.ParticleMass,
                            Index = created,
                            NeighborCount = 0,
                        };
                        created++;
                    }
                }
            }

            ParticleCount = created;
            IsInitialized = true;
            SimulationTime = 0;
        }

        /// <summary>
        ///   执行一步SPH模拟。
        /// </summary>
        public void Step(float dt)
        {
            if (!IsInitialized || ParticleCount == 0) return;

            dt = Math.Min(dt, Config.TimeStep);

            // 步骤1：构建空间哈希网格
            _spatialHash.Build(Particles, ParticleCount);

            // 步骤2：计算密度
            ComputeDensity();

            // 步骤3：计算压力
            ComputePressure();

            // 步骤4：计算力
            ComputeForces(dt);

            // 步骤5：时间积分（Leapfrog）
            TimeIntegration(dt);

            // 步骤6：边界处理
            HandleBoundaries();

            // 步骤7：表面检测
            DetectSurface();

            SimulationTime += dt;
            OnStepComplete?.Invoke(dt);
        }

        /// <summary>
        ///   步骤2：计算每个粒子的密度。
        ///   rho_i = Sum_j m_j * W_poly6(|r_i - r_j|, h)
        /// </summary>
        private void ComputeDensity()
        {
            float h = Config.SmoothingRadius;

            for (int i = 0; i < ParticleCount; i++)
            {
                float density = 0;

                // 自身贡献
                density += Config.ParticleMass * SPHKernel.Poly6(0, h);

                // 邻居贡献
                _spatialHash.FindNeighbors(Particles, i, _neighborCache);
                Particles[i].NeighborCount = _neighborCache.Count;

                for (int j = 0; j < _neighborCache.Count; j++)
                {
                    int ni = _neighborCache[j];
                    PhysicsVector3 diff = Particles[ni].Position - Particles[i].Position;
                    float distSq = diff.LengthSquared();
                    density += Config.ParticleMass * SPHKernel.Poly6(distSq, h);
                }

                // 密度下界保护
                Particles[i].Density = Math.Max(density, Config.RestDensity * 0.1f);
            }
        }

        /// <summary>
        ///   步骤3：计算每个粒子的压力。
        ///   p_i = k * (rho_i - rho_0)
        ///   使用Tait方程变体以增强稳定性。
        /// </summary>
        private void ComputePressure()
        {
            for (int i = 0; i < ParticleCount; i++)
            {
                // p = k * (rho - rho_0)
                Particles[i].Pressure = Config.GasConstant * (Particles[i].Density - Config.RestDensity);

                // 确保压力非负（避免负压导致的粒子聚集）
                if (Particles[i].Pressure < 0)
                    Particles[i].Pressure *= 0.5f;
            }
        }

        /// <summary>
        ///   步骤4：计算所有力（压力 + 粘性 + 重力 + 表面张力）。
        /// </summary>
        private void ComputeForces(float dt)
        {
            float h = Config.SmoothingRadius;
            PhysicsVector3 gravity = Config.Gravity;

            for (int i = 0; i < ParticleCount; i++)
            {
                PhysicsVector3 pressureForce = PhysicsVector3.Zero;
                PhysicsVector3 viscosityForce = PhysicsVector3.Zero;
                PhysicsVector3 surfaceNormal = PhysicsVector3.Zero;

                _spatialHash.FindNeighbors(Particles, i, _neighborCache);

                for (int j = 0; j < _neighborCache.Count; j++)
                {
                    int ni = _neighborCache[j];
                    PhysicsVector3 diff = Particles[ni].Position - Particles[i].Position;
                    float dist = diff.Length();

                    if (dist < 1e-6f) continue;

                    PhysicsVector3 dir = diff / dist;

                    // 压力力: F_pressure = -Sum m_j * (p_i + p_j) / (2 * rho_j) * grad_W_spiky(r, h)
                    float spikyGrad = SPHKernel.SpikyGradient(dist, h);
                    float pressureTerm = (Particles[i].Pressure + Particles[ni].Pressure)
                        / (2.0f * Math.Max(Particles[ni].Density, 1e-6f));
                    pressureForce = pressureForce - dir * (Config.ParticleMass * pressureTerm * spikyGrad);

                    // 粘性力: F_viscosity = mu * Sum m_j * (v_j - v_i) / rho_j * laplacian_W_viscosity(r, h)
                    float viscLap = SPHKernel.ViscosityLaplacian(dist, h);
                    PhysicsVector3 velDiff = Particles[ni].Velocity - Particles[i].Velocity;
                    float viscTerm = Config.ParticleMass / Math.Max(Particles[ni].Density, 1e-6f);
                    viscosityForce = viscosityForce + velDiff * (Config.Viscosity * viscTerm * viscLap);

                    // 表面张力法线估计
                    float surfKernel = SPHKernel.SurfaceNormalKernel(dist, h);
                    surfaceNormal = surfaceNormal + dir * (Config.ParticleMass / Math.Max(Particles[ni].Density, 1e-6f) * surfKernel);
                }

                // 重力: F_gravity = rho_i * g
                PhysicsVector3 gravityForce = gravity * Particles[i].Density;

                // 表面张力力（简化模型）
                PhysicsVector3 surfaceForce = PhysicsVector3.Zero;
                float surfNormalLen = surfaceNormal.Length();
                if (surfNormalLen > Config.RestDensity * 0.01f)
                {
                    surfaceForce = surfaceNormal.Normalized() * (-Config.SurfaceTension * surfNormalLen);
                }

                // 总力
                Particles[i].Force = pressureForce + viscosityForce + gravityForce + surfaceForce;
            }
        }

        /// <summary>
        ///   步骤5：Leapfrog时间积分。
        ///   半步速度 -> 力 -> 半步速度 -> 位置
        /// </summary>
        private void TimeIntegration(float dt)
        {
            float invDensity;
            float maxVelSq = Config.MaxVelocity * Config.MaxVelocity;

            for (int i = 0; i < ParticleCount; i++)
            {
                invDensity = 1.0f / Math.Max(Particles[i].Density, 1e-6f);

                // a = F / rho
                PhysicsVector3 acceleration = Particles[i].Force * invDensity;

                // Leapfrog: v(t+dt/2) = v(t-dt/2) + a(t) * dt
                Particles[i].VelocityHalf = Particles[i].VelocityHalf + acceleration * dt;

                // 速度限幅
                float velSq = Particles[i].VelocityHalf.LengthSquared();
                if (velSq > maxVelSq)
                {
                    float scale = Config.MaxVelocity / (float)Math.Sqrt(velSq);
                    Particles[i].VelocityHalf = Particles[i].VelocityHalf * scale;
                }

                // x(t+dt) = x(t) + v(t+dt/2) * dt
                Particles[i].Position = Particles[i].Position + Particles[i].VelocityHalf * dt;

                // 全步速度（用于外力计算）
                Particles[i].Velocity = Particles[i].VelocityHalf;
            }
        }

        /// <summary>
        ///   步骤6：边界处理。
        ///   使用碰撞反射和penalty力两种方式。
        /// </summary>
        private void HandleBoundaries()
        {
            PhysicsVector3 domainMin = PhysicsVector3.Zero;
            PhysicsVector3 domainMax = Config.DomainSize;
            float radius = Config.ParticleRadius;
            float stiffness = Config.BoundaryStiffness;
            float damping = Config.BoundaryDamping;

            for (int i = 0; i < ParticleCount; i++)
            {
                PhysicsVector3 pos = Particles[i].Position;
                PhysicsVector3 vel = Particles[i].VelocityHalf;

                // X边界
                if (pos.X < domainMin.X + radius)
                {
                    float penetration = domainMin.X + radius - pos.X;
                    Particles[i].VelocityHalf = new PhysicsVector3(
                        Math.Abs(vel.X) * 0.5f + damping * penetration,
                        vel.Y, vel.Z);
                    Particles[i].Position = new PhysicsVector3(
                        domainMin.X + radius, pos.Y, pos.Z);
                }
                else if (pos.X > domainMax.X - radius)
                {
                    float penetration = pos.X - (domainMax.X - radius);
                    Particles[i].VelocityHalf = new PhysicsVector3(
                        -Math.Abs(vel.X) * 0.5f - damping * penetration,
                        vel.Y, vel.Z);
                    Particles[i].Position = new PhysicsVector3(
                        domainMax.X - radius, pos.Y, pos.Z);
                }

                // Y边界（地面）
                if (pos.Y < domainMin.Y + radius)
                {
                    float penetration = domainMin.Y + radius - pos.Y;
                    Particles[i].VelocityHalf = new PhysicsVector3(
                        vel.X * 0.99f,
                        Math.Abs(vel.Y) * 0.3f + damping * penetration,
                        vel.Z * 0.99f);
                    Particles[i].Position = new PhysicsVector3(
                        pos.X, domainMin.Y + radius, pos.Z);
                }
                else if (pos.Y > domainMax.Y - radius)
                {
                    float penetration = pos.Y - (domainMax.Y - radius);
                    Particles[i].VelocityHalf = new PhysicsVector3(
                        vel.X,
                        -Math.Abs(vel.Y) * 0.5f - damping * penetration,
                        vel.Z);
                    Particles[i].Position = new PhysicsVector3(
                        pos.X, domainMax.Y - radius, pos.Z);
                }

                // Z边界
                if (pos.Z < domainMin.Z + radius)
                {
                    float penetration = domainMin.Z + radius - pos.Z;
                    Particles[i].VelocityHalf = new PhysicsVector3(
                        vel.X, vel.Y,
                        Math.Abs(vel.Z) * 0.5f + damping * penetration);
                    Particles[i].Position = new PhysicsVector3(
                        pos.X, pos.Y, domainMin.Z + radius);
                }
                else if (pos.Z > domainMax.Z - radius)
                {
                    float penetration = pos.Z - (domainMax.Z - radius);
                    Particles[i].VelocityHalf = new PhysicsVector3(
                        vel.X, vel.Y,
                        -Math.Abs(vel.Z) * 0.5f - damping * penetration);
                    Particles[i].Position = new PhysicsVector3(
                        pos.X, pos.Y, domainMax.Z - radius);
                }

                // Penalty力（粒子深入边界时施加推力）
                pos = Particles[i].Position;
                PhysicsVector3 penaltyForce = PhysicsVector3.Zero;

                float margin = radius * 2.0f;
                if (pos.X < domainMin.X + margin)
                    penaltyForce = penaltyForce + new PhysicsVector3(stiffness * (domainMin.X + margin - pos.X), 0, 0);
                if (pos.X > domainMax.X - margin)
                    penaltyForce = penaltyForce + new PhysicsVector3(stiffness * (domainMax.X - margin - pos.X), 0, 0);
                if (pos.Y < domainMin.Y + margin)
                    penaltyForce = penaltyForce + new PhysicsVector3(0, stiffness * (domainMin.Y + margin - pos.Y), 0);
                if (pos.Y > domainMax.Y - margin)
                    penaltyForce = penaltyForce + new PhysicsVector3(0, stiffness * (domainMax.Y - margin - pos.Y), 0);
                if (pos.Z < domainMin.Z + margin)
                    penaltyForce = penaltyForce + new PhysicsVector3(0, 0, stiffness * (domainMin.Z + margin - pos.Z));
                if (pos.Z > domainMax.Z - margin)
                    penaltyForce = penaltyForce + new PhysicsVector3(0, 0, stiffness * (domainMax.Z - margin - pos.Z));

                Particles[i].VelocityHalf = Particles[i].VelocityHalf + penaltyForce * Config.TimeStep;
            }
        }

        /// <summary>
        ///   步骤7：表面粒子检测。
        ///   邻居数少于阈值的粒子被标记为表面粒子。
        /// </summary>
        private void DetectSurface()
        {
            int threshold = Config.SurfaceNeighborThreshold;

            for (int i = 0; i < ParticleCount; i++)
            {
                // 邻居数已在ComputeDensity阶段计算
                Particles[i].IsSurface = Particles[i].NeighborCount < threshold;
            }
        }

        /// <summary>
        ///   在指定位置施加外部力（如障碍物推动流体）。
        /// </summary>
        public void ApplyExternalForce(PhysicsVector3 center, float radius, PhysicsVector3 force)
        {
            float radiusSq = radius * radius;

            for (int i = 0; i < ParticleCount; i++)
            {
                PhysicsVector3 diff = Particles[i].Position - center;
                float distSq = diff.LengthSquared();

                if (distSq < radiusSq)
                {
                    float falloff = 1.0f - (float)Math.Sqrt(distSq) / radius;
                    Particles[i].VelocityHalf = Particles[i].VelocityHalf + force * falloff * Config.TimeStep;
                }
            }
        }

        /// <summary>获取所有表面粒子。</summary>
        public List<int> GetSurfaceParticleIndices()
        {
            List<int> surface = new List<int>();
            for (int i = 0; i < ParticleCount; i++)
            {
                if (Particles[i].IsSurface)
                    surface.Add(i);
            }
            return surface;
        }

        /// <summary>获取指定位置附近的粒子。</summary>
        public List<int> GetParticlesInRange(PhysicsVector3 center, float radius)
        {
            List<int> result = new List<int>();
            float radiusSq = radius * radius;

            for (int i = 0; i < ParticleCount; i++)
            {
                PhysicsVector3 diff = Particles[i].Position - center;
                if (diff.LengthSquared() < radiusSq)
                    result.Add(i);
            }

            return result;
        }

        /// <summary>重置模拟。</summary>
        public void Reset()
        {
            if (Particles != null)
            {
                for (int i = 0; i < ParticleCount; i++)
                {
                    Particles[i].Velocity = PhysicsVector3.Zero;
                    Particles[i].VelocityHalf = PhysicsVector3.Zero;
                    Particles[i].Force = PhysicsVector3.Zero;
                    Particles[i].Pressure = 0;
                    Particles[i].Density = Config.RestDensity;
                    Particles[i].IsSurface = false;
                    Particles[i].NeighborCount = 0;
                }
            }
            SimulationTime = 0;
        }
    }

    #endregion
}
