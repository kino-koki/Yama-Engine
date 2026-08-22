// ============================================================================================
//  FluidSimulationSystem.cs
//
//  山雨引擎 (ShanyuEngine) 流体模拟系统 —— 基于 SPH (Smoothed Particle Hydrodynamics)。
//
//  SPH 把流体离散成一组粒子, 每个粒子携带质量、位置、速度。通过核函数 W(r, h) 估算
//  连续场的物理量 (密度、压力), 再用 Navier-Stokes 方程计算力, 最后积分。
//
//  核函数 (Kernel):
//    - Poly6 Kernel (密度计算):  W(r,h) = 315/(64πh^9) * (h²-r²)³,  r < h
//    - Spiky Gradient (压力力):  ∇W(r,h) = -45/(πh^6) * (h-r)² * (r̂),  r < h
//    - Viscosity Laplacian (粘性): ∇²W(r,h) = 45/(πh^6) * (h-r),  r < h
//    - Cubic Spline Kernel (备选): W(q) = α * f(q), q=r/h
//
//  算法流程:
//    1. 空间哈希建表 (邻居搜索加速)
//    2. 密度计算: ρ_i = Σ_j m_j * W(r_ij, h)
//    3. 压力计算: P_i = k * (ρ_i - ρ_0)  (Tait equation 或 线性状态方程)
//    4. 压力力: F_p = -Σ_j m_j * (P_i+P_j)/(2ρ_j) * ∇W(r_ij, h)
//    5. 粘性力: F_v = μ * Σ_j m_j * (v_j-v_i)/ρ_j * ∇²W(r_ij, h)
//    6. 表面张力: F_st = -σ * κ * n,  n = (1/ρ)∇ρ,  κ = -(1/|n|)∇·n
//    7. 积分: Semi-Implicit Euler
//    8. 边界处理: 反弹 / 吸收
//
//  流体类型: 水 (密度1000, 低粘性) / 油 (密度800, 中粘性) / 岩浆 (密度2800, 高粘性)
//
//  命名空间: ShanyuEngine.Physics
//  目标框架: net8.0 / C# 12+
//  外部依赖: 仅 System.Numerics
// ============================================================================================

using System;
using System.Collections.Generic;
using System.Numerics;

namespace ShanyuEngine.Physics
{
    /// <summary>流体类型。</summary>
    public enum SPHFluidType
    {
        Water,
        Oil,
        Magma,
        Custom
    }

    /// <summary>流体粒子。</summary>
    public struct SPHFluidParticle
    {
        public Vector3 Position;
        public Vector3 Velocity;
        public Vector3 Acceleration;
        public Vector3 ForceAccumulator;
        public float Density;
        public float Pressure;
        public float Mass;
        public float InvMass;
        public Vector3 OldPosition;
        public bool Boundary;
        public SPHFluidType SPHFluidType;
    }

    /// <summary>流体统计信息。</summary>
    public struct SPHFluidStats
    {
        public int ParticleCount;
        public float AverageDensity;
        public float MaxPressure;
        public float MinPressure;
        public float AverageVelocity;
        public float KineticEnergy;
    }

    /// <summary>
    /// 流体模拟系统主类 —— SPH求解。
    /// </summary>
    public sealed class FluidSimulationSystem
    {
        private readonly List<SPHFluidParticle> _particles = new(1024);
        private readonly Dictionary<long, List<int>> _hashGrid = new(512);
        private float _hashCellSize;

        // SPH参数
        private float _h;                  // 光滑长度 (smoothing length)
        private float _hSq;
        private float _restDensity;        // 静止密度 ρ_0
        private float _gasConstant;        // 气体常数 k (刚度)
        private float _viscosity;          // 粘性系数 μ
        private float _particleMass;       // 单个粒子质量
        private float _particleRadius;     // 粒子半径 (碰撞/渲染)
        private float _surfaceTension;     // 表面张力系数 σ

        // 核函数预计算系数
        private float _poly6Coeff;
        private float _spikyGradCoeff;
        private float _viscLaplCoeff;
        private float _cubicSplineCoeff;

        // 物理参数
        public Vector3 Gravity { get; set; } = new(0f, -9.81f, 0f);
        public float VelocityDamping { get; set; } = 0.01f;

        // 边界
        public Vector3 BoundaryMin { get; set; } = new(-5f, -5f, -5f);
        public Vector3 BoundaryMax { get; set; } = new(5f, 5f, 5f);
        public float BoundaryRestitution { get; set; } = 0.5f;
        public bool AbsorbBoundary { get; set; } = false;

        public int ParticleCount => _particles.Count;

        /// <summary>获取粒子列表 (只读引用)。</summary>
        public IReadOnlyList<SPHFluidParticle> Particles => _particles;

        /// <summary>
        /// 创建流体模拟系统。
        /// </summary>
        /// <param name="smoothingLength">光滑长度 h (粒子影响半径)</param>
        /// <param name="restDensity">静止密度 ρ_0 (水=1000 kg/m³)</param>
        /// <param name="gasConstant">气体常数 k (刚度, 越大越不可压缩)</param>
        /// <param name="viscosity">粘性系数 μ</param>
        /// <param name="particleMass">单个粒子质量</param>
        public FluidSimulationSystem(
            float smoothingLength = 0.5f,
            float restDensity = 1000f,
            float gasConstant = 2000f,
            float viscosity = 0.1f,
            float particleMass = 0.02f,
            float particleRadius = 0.1f,
            float surfaceTension = 0.0f)
        {
            _h = MathF.Max(smoothingLength, 0.01f);
            _hSq = _h * _h;
            _restDensity = restDensity;
            _gasConstant = gasConstant;
            _viscosity = viscosity;
            _particleMass = MathF.Max(particleMass, 1e-6f);
            _particleRadius = particleRadius;
            _surfaceTension = surfaceTension;
            _hashCellSize = _h;

            PrecomputeKernelCoefficients();
        }

        /// <summary>预计算核函数系数。</summary>
        private void PrecomputeKernelCoefficients()
        {
            float h = _h;
            float h9 = h * h * h * h * h * h * h * h * h;
            float h6 = h * h * h * h * h * h;
            float h3 = h * h * h;

            // Poly6: W(r,h) = 315/(64πh^9) * (h²-r²)³
            _poly6Coeff = 315f / (64f * MathF.PI * h9);

            // Spiky gradient: ∇W(r,h) = -45/(πh^6) * (h-r)² * r̂
            _spikyGradCoeff = -45f / (MathF.PI * h6);

            // Viscosity Laplacian: ∇²W(r,h) = 45/(πh^6) * (h-r)
            _viscLaplCoeff = 45f / (MathF.PI * h6);

            // Cubic Spline: W(q) = α * f(q), α = 8/(πh³), q = r/h
            _cubicSplineCoeff = 8f / (MathF.PI * h3);
        }

        /// <summary>设置流体类型参数预设。</summary>
        public void SetFluidType(SPHFluidType type)
        {
            switch (type)
            {
                case SPHFluidType.Water:
                    _restDensity = 1000f;
                    _gasConstant = 2000f;
                    _viscosity = 0.1f;
                    _surfaceTension = 0.0728f; // 水的表面张力 72.8 mN/m
                    break;
                case SPHFluidType.Oil:
                    _restDensity = 800f;
                    _gasConstant = 1500f;
                    _viscosity = 0.5f;
                    _surfaceTension = 0.032f;
                    break;
                case SPHFluidType.Magma:
                    _restDensity = 2800f;
                    _gasConstant = 5000f;
                    _viscosity = 10f;
                    _surfaceTension = 0.4f;
                    break;
            }
        }

        /// <summary>添加流体粒子。</summary>
        public int AddParticle(Vector3 position, Vector3 velocity, SPHFluidType SPHFluidType = SPHFluidType.Water)
        {
            var p = new SPHFluidParticle
            {
                Position = position,
                OldPosition = position,
                Velocity = velocity,
                Acceleration = Vector3.Zero,
                ForceAccumulator = Vector3.Zero,
                Density = _restDensity,
                Pressure = 0f,
                Mass = _particleMass,
                InvMass = 1f / _particleMass,
                Boundary = false,
                SPHFluidType = SPHFluidType
            };
            _particles.Add(p);
            return _particles.Count - 1;
        }

        /// <summary>批量添加粒子 (在区域内随机分布)。</summary>
        public void AddParticleCloud(Vector3 center, Vector3 size, int count, SPHFluidType type = SPHFluidType.Water)
        {
            var rng = new Random(42);
            for (int i = 0; i < count; i++)
            {
                var pos = center + new Vector3(
                    (float)(rng.NextDouble() - 0.5) * size.X,
                    (float)(rng.NextDouble() - 0.5) * size.Y,
                    (float)(rng.NextDouble() - 0.5) * size.Z);
                AddParticle(pos, Vector3.Zero, type);
            }
        }

        /// <summary>清空所有粒子。</summary>
        public void Clear() => _particles.Clear();

        /// <summary>
        /// 执行一次SPH流体模拟步。
        /// </summary>
        public void Step(float deltaTime)
        {
            if (deltaTime <= 0f || _particles.Count == 0) return;

            int n = _particles.Count;

            // 1. 建空间哈希 (邻居搜索)
            BuildSpatialHash();

            // 2. 计算密度和压力
            for (int i = 0; i < n; i++)
            {
                ComputeDensityAndPressure(i);
            }

            // 3. 计算力 (压力 + 粘性 + 表面张力 + 重力)
            for (int i = 0; i < n; i++)
            {
                ComputeForces(i);
            }

            // 4. 积分 (Semi-Implicit Euler)
            for (int i = 0; i < n; i++)
            {
                ref SPHFluidParticle p = ref _particles[i];
                if (p.Boundary) continue;

                p.Acceleration = p.ForceAccumulator * p.InvMass;
                p.Velocity += p.Acceleration * deltaTime;
                p.Velocity *= (1f - VelocityDamping);

                p.OldPosition = p.Position;
                p.Position += p.Velocity * deltaTime;

                // 边界处理
                HandleBoundary(ref p);
            }
        }

        /// <summary>构建空间哈希网格。</summary>
        private void BuildSpatialHash()
        {
            _hashGrid.Clear();
            for (int i = 0; i < _particles.Count; i++)
            {
                long hash = HashPosition(_particles[i].Position);
                if (!_hashGrid.TryGetValue(hash, out var list))
                {
                    list = new List<int>(8);
                    _hashGrid[hash] = list;
                }
                list.Add(i);
            }
        }

        /// <summary>获取粒子的邻居索引列表。</summary>
        private List<int> GetNeighbors(int particleIndex)
        {
            var neighbors = new List<int>(32);
            Vector3 pos = _particles[particleIndex].Position;
            int cx = (int)MathF.Floor(pos.X / _hashCellSize);
            int cy = (int)MathF.Floor(pos.Y / _hashCellSize);
            int cz = (int)MathF.Floor(pos.Z / _hashCellSize);

            for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
            for (int dz = -1; dz <= 1; dz++)
            {
                long hash = HashCell(cx + dx, cy + dy, cz + dz);
                if (_hashGrid.TryGetValue(hash, out var list))
                    neighbors.AddRange(list);
            }
            return neighbors;
        }

        /// <summary>计算粒子i的密度和压力。</summary>
        private void ComputeDensityAndPressure(int i)
        {
            float density = 0f;
            var neighbors = GetNeighbors(i);

            foreach (int j in neighbors)
            {
                Vector3 rij = _particles[i].Position - _particles[j].Position;
                float rSq = rij.LengthSquared();
                if (rSq < _hSq)
                {
                    // Poly6核: W = c * (h²-r²)³
                    float diff = _hSq - rSq;
                    density += _particleMass * _poly6Coeff * diff * diff * diff;
                }
            }

            // 确保密度不为零
            density = MathF.Max(density, 1f);

            // 状态方程: P = k * (ρ - ρ_0)
            float pressure = _gasConstant * (density - _restDensity);
            pressure = MathF.Max(pressure, 0f); // 不允许负压

            _particles[i] = new SPHFluidParticle
            {
                Position = _particles[i].Position,
                OldPosition = _particles[i].OldPosition,
                Velocity = _particles[i].Velocity,
                Acceleration = _particles[i].Acceleration,
                ForceAccumulator = _particles[i].ForceAccumulator,
                Density = density,
                Pressure = pressure,
                Mass = _particles[i].Mass,
                InvMass = _particles[i].InvMass,
                Boundary = _particles[i].Boundary,
                SPHFluidType = _particles[i].SPHFluidType
            };
        }

        /// <summary>计算粒子i的所有力 (压力 + 粘性 + 表面张力 + 重力)。</summary>
        private void ComputeForces(int i)
        {
            Vector3 pressureForce = Vector3.Zero;
            Vector3 viscosityForce = Vector3.Zero;
            Vector3 surfaceForce = Vector3.Zero;
            Vector3 gravityForce = Vector3.Zero;

            Vector3 posI = _particles[i].Position;
            Vector3 velI = _particles[i].Velocity;
            float pressureI = _particles[i].Pressure;
            float densityI = _particles[i].Density;

            // 重力
            gravityForce = Gravity * _particleMass;

            var neighbors = GetNeighbors(i);
            foreach (int j in neighbors)
            {
                if (i == j) continue;

                Vector3 rij = posI - _particles[j].Position;
                float r = rij.Length();
                if (r < 1e-6f || r >= _h) continue;

                Vector3 rDir = rij / r;

                // === 压力力 ===
                // F_p = -Σ m_j * (P_i + P_j)/(2ρ_j) * ∇W_spiky
                float pressureJ = _particles[j].Pressure;
                float densityJ = _particles[j].Density;
                float hMinusR = _h - r;
                float spikyGrad = _spikyGradCoeff * hMinusR * hMinusR;
                float pressureTerm = -_particleMass * (pressureI + pressureJ) * 0.5f / densityJ;
                pressureForce += rDir * (pressureTerm * spikyGrad);

                // === 粘性力 ===
                // F_v = μ * Σ m_j * (v_j - v_i)/ρ_j * ∇²W_visc
                float viscLapl = _viscLaplCoeff * hMinusR;
                Vector3 velDiff = _particles[j].Velocity - velI;
                viscosityForce += velDiff * (_viscosity * _particleMass / densityJ * viscLapl);

                // === 表面张力 (简化: 使用颜色场梯度) ===
                if (_surfaceTension > 0f)
                {
                    float poly6 = _poly6Coeff * (_hSq - r * r) * (_hSq - r * r) * (_hSq - r * r);
                    // 颜色场梯度方向
                    surfaceForce -= rDir * (_surfaceTension * _particleMass / densityJ * poly6);
                }
            }

            Vector3 totalForce = pressureForce + viscosityForce + surfaceForce + gravityForce;

            // 更新力累加器
            var p = _particles[i];
            p.ForceAccumulator = totalForce;
            _particles[i] = p;
        }

        /// <summary>边界处理 (反弹/吸收)。</summary>
        private void HandleBoundary(ref SPHFluidParticle p)
        {
            float r = _particleRadius;
            Vector3 min = BoundaryMin;
            Vector3 max = BoundaryMax;

            if (AbsorbBoundary)
            {
                // 吸收: 超出边界则删除 (标记)
                if (p.Position.X < min.X || p.Position.X > max.X ||
                    p.Position.Y < min.Y || p.Position.Y > max.Y ||
                    p.Position.Z < min.Z || p.Position.Z > max.Z)
                {
                    p.Boundary = true;
                    p.Velocity = Vector3.Zero;
                }
                return;
            }

            // 反弹
            if (p.Position.X < min.X + r) { p.Position.X = min.X + r; p.Velocity.X = -p.Velocity.X * BoundaryRestitution; }
            if (p.Position.X > max.X - r) { p.Position.X = max.X - r; p.Velocity.X = -p.Velocity.X * BoundaryRestitution; }
            if (p.Position.Y < min.Y + r) { p.Position.Y = min.Y + r; p.Velocity.Y = -p.Velocity.Y * BoundaryRestitution; }
            if (p.Position.Y > max.Y - r) { p.Position.Y = max.Y - r; p.Velocity.Y = -p.Velocity.Y * BoundaryRestitution; }
            if (p.Position.Z < min.Z + r) { p.Position.Z = min.Z + r; p.Velocity.Z = -p.Velocity.Z * BoundaryRestitution; }
            if (p.Position.Z > max.Z - r) { p.Position.Z = max.Z - r; p.Velocity.Z = -p.Velocity.Z * BoundaryRestitution; }
        }

        // === 核函数 ===

        /// <summary>Poly6核函数 W(r,h) = 315/(64πh^9) * (h²-r²)³。</summary>
        public float Poly6Kernel(float rSquared)
        {
            if (rSquared >= _hSq) return 0f;
            float diff = _hSq - rSquared;
            return _poly6Coeff * diff * diff * diff;
        }

        /// <summary>Spiky核梯度 ∇W(r,h) = -45/(πh^6) * (h-r)² * r̂。</summary>
        public float SpikyKernelGradient(float r)
        {
            if (r >= _h || r < 1e-6f) return 0f;
            float hMinusR = _h - r;
            return _spikyGradCoeff * hMinusR * hMinusR;
        }

        /// <summary>粘性核拉普拉斯 ∇²W(r,h) = 45/(πh^6) * (h-r)。</summary>
        public float ViscosityKernelLaplacian(float r)
        {
            if (r >= _h || r < 1e-6f) return 0f;
            return _viscLaplCoeff * (_h - r);
        }

        /// <summary>Cubic Spline核函数 W(q) = α * f(q), q = r/h。</summary>
        public float CubicSplineKernel(float r)
        {
            if (r >= _h) return 0f;
            float q = r / _h;
            float q2 = q * q;
            float q3 = q2 * q;
            float result;
            if (q < 0.5f)
                result = 1f - 6f * q2 + 6f * q3;
            else
                result = 2f * (1f - q) * (1f - q) * (1f - q);
            return _cubicSplineCoeff * result;
        }

        // === 空间哈希 ===

        private long HashPosition(Vector3 pos)
        {
            int cx = (int)MathF.Floor(pos.X / _hashCellSize);
            int cy = (int)MathF.Floor(pos.Y / _hashCellSize);
            int cz = (int)MathF.Floor(pos.Z / _hashCellSize);
            return HashCell(cx, cy, cz);
        }

        private static long HashCell(int cx, int cy, int cz)
            => (long)cx * 73856093L ^ (long)cy * 19349663L ^ (long)cz * 83492791L;

        // === 统计与工具 ===

        /// <summary>获取统计信息。</summary>
        public SPHFluidStats GetStats()
        {
            var stats = new SPHFluidStats();
            stats.ParticleCount = _particles.Count;

            if (_particles.Count == 0) return stats;

            float totalDensity = 0f;
            float maxP = float.MinValue;
            float minP = float.MaxValue;
            float totalVel = 0f;
            float totalKE = 0f;

            foreach (var p in _particles)
            {
                totalDensity += p.Density;
                maxP = MathF.Max(maxP, p.Pressure);
                minP = MathF.Min(minP, p.Pressure);
                float speed = p.Velocity.Length();
                totalVel += speed;
                totalKE += 0.5f * p.Mass * speed * speed;
            }

            stats.AverageDensity = totalDensity / _particles.Count;
            stats.MaxPressure = maxP;
            stats.MinPressure = minP;
            stats.AverageVelocity = totalVel / _particles.Count;
            stats.KineticEnergy = totalKE;
            return stats;
        }

        /// <summary>获取所有粒子位置 (用于渲染)。</summary>
        public Vector3[] GetPositions()
        {
            var positions = new Vector3[_particles.Count];
            for (int i = 0; i < _particles.Count; i++)
                positions[i] = _particles[i].Position;
            return positions;
        }

        /// <summary>获取所有粒子速度。</summary>
        public Vector3[] GetVelocities()
        {
            var velocities = new Vector3[_particles.Count];
            for (int i = 0; i < _particles.Count; i++)
                velocities[i] = _particles[i].Velocity;
            return velocities;
        }

        /// <summary>获取所有粒子密度。</summary>
        public float[] GetDensities()
        {
            var densities = new float[_particles.Count];
            for (int i = 0; i < _particles.Count; i++)
                densities[i] = _particles[i].Density;
            return densities;
        }

        /// <summary>移除被标记为边界的粒子 (吸收模式)。</summary>
        public void RemoveAbsorbedParticles()
        {
            _particles.RemoveAll(p => p.Boundary);
        }
    }
}
