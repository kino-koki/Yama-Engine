// ============================================================================================
//  PBDSolver.cs
//
//  yama引擎 (YamaEngine) Position Based Dynamics 求解器模块。
//
//  PBD (Position Based Dynamics) 是一种基于位置的动力学方法，与传统基于力的方法不同，
//  PBD 直接操作位置来满足约束，然后从位置变化反推速度。这使得它在处理刚性约束、
//  不可压缩流体和布料时非常稳定，不会因为时间步过大而爆炸。
//
//  核心流程 (Müller et al. 2007):
//    1. 预测位置: x* = x + v*dt + (F_ext/m)*dt^2
//    2. 约束生成 (碰撞检测等)
//    3. 高斯-赛德尔迭代求解约束: 对每个约束 C(x) >= 0, 投影位置使约束满足
//       Δx = -λ * ∇C / |∇C|^2,  λ = C(x) / (Σ w_k * |∇C_k|^2)
//    4. 速度更新: v = (x_new - x_old) / dt
//    5. 位置提交: x = x_new
//
//  约束类型: 距离约束 / 碰撞约束 / 体积约束 / 形状匹配
//  刚体PBD扩展: 基于位置+方向的约束求解
//  子步(Sub-stepping): 将一个大步分成若干小步, 提高稳定性
//
//  命名空间: YamaEngine.Physics
//  目标框架: net8.0 / C# 12+
//  外部依赖: 仅 System.Numerics
// ============================================================================================

using System;
using System.Collections.Generic;
using System.Numerics;

namespace YamaEngine.Physics
{
    /// <summary>PBD粒子 —— 位置、预测位置、速度、逆质量。</summary>
    public struct PBDParticle
    {
        public Vector3 Position;
        public Vector3 PredictedPosition;
        public Vector3 Velocity;
        public Vector3 ForceAccumulator;
        public float InverseMass;
        public float Radius;
        public bool Pinned;

        public readonly float Mass => InverseMass > 0f ? 1f / InverseMass : float.MaxValue;

        public PBDParticle(Vector3 position, float mass, float radius = 0.05f)
        {
            Position = position;
            PredictedPosition = position;
            Velocity = Vector3.Zero;
            ForceAccumulator = Vector3.Zero;
            InverseMass = mass > 0f ? 1f / mass : 0f;
            Radius = radius;
            Pinned = false;
        }
    }

    /// <summary>约束类型枚举。</summary>
    public enum PBDConstraintType
    {
        Distance,
        Collision,
        Volume,
        ShapeMatching,
        Bending,
        LongRange,
        Pin
    }

    /// <summary>
    /// PBD约束接口。每个约束实现 Project 方法, 在高斯-赛德尔迭代中被调用。
    /// </summary>
    public interface IPBDConstraint
    {
        PBDConstraintType Type { get; }
        float Stiffness { get; }

        /// <summary>投影约束: 修改粒子的预测位置使其满足约束。</summary>
        void Project(PBDParticle[] particles, float stiffnessScale);
    }

    /// <summary>
    /// 距离约束: 保持两个粒子之间的距离为 restLength。
    /// C = |x_i - x_j| - restLength = 0
    /// </summary>
    public struct DistancePBDConstraint : IPBDConstraint
    {
        public int IndexA;
        public int IndexB;
        public float RestLength;
        public float Stiffness { get; set; }
        public PBDConstraintType Type => PBDConstraintType.Distance;

        public DistancePBDConstraint(int a, int b, float restLength, float stiffness = 1.0f)
        {
            IndexA = a;
            IndexB = b;
            RestLength = MathF.Max(restLength, 1e-6f);
            Stiffness = Math.Clamp(stiffness, 0f, 1f);
        }

        public void Project(PBDParticle[] particles, float stiffnessScale)
        {
            ref PBDParticle pa = ref particles[IndexA];
            ref PBDParticle pb = ref particles[IndexB];

            float wSum = pa.InverseMass + pb.InverseMass;
            if (wSum < 1e-10f) return;

            Vector3 delta = pa.PredictedPosition - pb.PredictedPosition;
            float dist = delta.Length();
            if (dist < 1e-8f) return;

            float C = dist - RestLength;
            // 有效刚度: 1 - (1-k)^(1/n), n=迭代次数的倒数由stiffnessScale传入
            float k = Stiffness * stiffnessScale;
            float lambda = -C / wSum * k;

            Vector3 dir = delta / dist;
            pa.PredictedPosition += dir * (lambda * pa.InverseMass);
            pb.PredictedPosition -= dir * (lambda * pb.InverseMass);
        }
    }

    /// <summary>
    /// 碰撞约束: 粒子之间或粒子与静态边界的非穿透约束。
    /// C = (r_i + r_j) - |x_i - x_j| >= 0
    /// </summary>
    public struct CollisionPBDConstraint : IPBDConstraint
    {
        public int IndexA;
        public int IndexB;
        public float SumRadius;
        public float Stiffness { get; set; }
        public PBDConstraintType Type => PBDConstraintType.Collision;

        public CollisionPBDConstraint(int a, int b, float sumRadius, float stiffness = 1.0f)
        {
            IndexA = a;
            IndexB = b;
            SumRadius = sumRadius;
            Stiffness = Math.Clamp(stiffness, 0f, 1f);
        }

        public void Project(PBDParticle[] particles, float stiffnessScale)
        {
            ref PBDParticle pa = ref particles[IndexA];
            ref PBDParticle pb = ref particles[IndexB];

            float wSum = pa.InverseMass + pb.InverseMass;
            if (wSum < 1e-10f) return;

            Vector3 delta = pa.PredictedPosition - pb.PredictedPosition;
            float distSq = delta.LengthSquared();
            if (distSq >= SumRadius * SumRadius) return;

            float dist = MathF.Sqrt(distSq);
            Vector3 dir;
            if (dist < 1e-8f)
            {
                dir = Vector3.UnitY;
                dist = 0f;
            }
            else
            {
                dir = delta / dist;
            }

            float C = SumRadius - dist; // 穿透量
            float k = Stiffness * stiffnessScale;
            float lambda = C / wSum * k;

            pa.PredictedPosition += dir * (lambda * pa.InverseMass);
            pb.PredictedPosition -= dir * (lambda * pb.InverseMass);
        }
    }

    /// <summary>
    /// 体积约束: 保持一组粒子围成的四面体体积不变 (用于不可压缩软体/流体)。
    /// C = V - V_rest,  V = (1/6) * |((x1-x0) x (x2-x0)) . (x3-x0)|
    /// </summary>
    public struct VolumePBDConstraint : IPBDConstraint
    {
        public int I0, I1, I2, I3;
        public float RestVolume;
        public float Stiffness { get; set; }
        public PBDConstraintType Type => PBDConstraintType.Volume;

        public VolumePBDConstraint(int i0, int i1, int i2, int i3, float restVolume, float stiffness = 1.0f)
        {
            I0 = i0; I1 = i1; I2 = i2; I3 = i3;
            RestVolume = restVolume;
            Stiffness = Math.Clamp(stiffness, 0f, 1f);
        }

        public void Project(PBDParticle[] particles, float stiffnessScale)
        {
            ref readonly Vector3 p0 = ref particles[I0].PredictedPosition;
            ref readonly Vector3 p1 = ref particles[I1].PredictedPosition;
            ref readonly Vector3 p2 = ref particles[I2].PredictedPosition;
            ref readonly Vector3 p3 = ref particles[I3].PredictedPosition;

            Vector3 e1 = p1 - p0;
            Vector3 e2 = p2 - p0;
            Vector3 e3 = p3 - p0;

            // 体积 = (1/6) * |dot(cross(e1, e2), e3)|
            Vector3 cross = Vector3.Cross(e1, e2);
            float signedVol = Vector3.Dot(cross, e3) / 6f;
            float C = MathF.Abs(signedVol) - MathF.Abs(RestVolume);
            if (MathF.Abs(C) < 1e-8f) return;

            // 梯度 (Jacobian): dV/dp_k
            // dV/dp1 = cross(e2, e3) / 6
            // dV/dp2 = cross(e3, e1) / 6
            // dV/dp3 = cross(e1, e2) / 6
            // dV/dp0 = -(dV/dp1 + dV/dp2 + dV/dp3)
            Vector3 g1 = Vector3.Cross(e2, e3) / 6f;
            Vector3 g2 = Vector3.Cross(e3, e1) / 6f;
            Vector3 g3 = Vector3.Cross(e1, e2) / 6f;
            Vector3 g0 = -(g1 + g2 + g3);

            float sign = signedVol >= 0f ? 1f : -1f;
            g0 *= sign; g1 *= sign; g2 *= sign; g3 *= sign;

            float w0 = particles[I0].InverseMass;
            float w1 = particles[I1].InverseMass;
            float w2 = particles[I2].InverseMass;
            float w3 = particles[I3].InverseMass;

            float denom = w0 * g0.LengthSquared() + w1 * g1.LengthSquared()
                        + w2 * g2.LengthSquared() + w3 * g3.LengthSquared();
            if (denom < 1e-12f) return;

            float k = Stiffness * stiffnessScale;
            float lambda = -C / denom * k;

            particles[I0].PredictedPosition += g0 * (lambda * w0);
            particles[I1].PredictedPosition += g1 * (lambda * w1);
            particles[I2].PredictedPosition += g2 * (lambda * w2);
            particles[I3].PredictedPosition += g3 * (lambda * w3);
        }
    }

    /// <summary>
    /// 形状匹配约束 (Müller et al. 2005): 将变形后的粒子群旋转/平移回原始形状。
    /// 适用于刚体PBD和可变形体恢复形状。
    /// </summary>
    public struct ShapeMatchingPBDConstraint : IPBDConstraint
    {
        public int[] Indices;
        public Vector3[] RestPositions;  // 相对质心的静止位置
        public float RestVolume;
        public float Stiffness { get; set; }
        public float Beta; // 体积保持系数 (0=刚体, 1=完全可塑)
        public PBDConstraintType Type => PBDConstraintType.ShapeMatching;

        public ShapeMatchingPBDConstraint(int[] indices, Vector3[] restPositions, float stiffness = 1.0f, float beta = 0f)
        {
            Indices = indices;
            RestPositions = restPositions;
            Stiffness = Math.Clamp(stiffness, 0f, 1f);
            Beta = Math.Clamp(beta, 0f, 1f);
            RestVolume = 1f;
            for (int i = 0; i < restPositions.Length; i++)
                RestVolume += restPositions[i].LengthSquared();
        }

        public void Project(PBDParticle[] particles, float stiffnessScale)
        {
            int n = Indices.Length;
            if (n < 2) return;

            // 计算当前质心
            Vector3 com = Vector3.Zero;
            float totalInvMass = 0f;
            for (int i = 0; i < n; i++)
            {
                float w = particles[Indices[i]].InverseMass;
                com += particles[Indices[i]].PredictedPosition * w;
                totalInvMass += w;
            }
            if (totalInvMass < 1e-10f) return;
            com /= totalInvMass;

            // 构建旋转矩阵 A = Σ (w_i * q_i * p_i^T),  q=rest, p=current(相对质心)
            // 使用3x3矩阵 (用Matrix4x4的左上3x3)
            float a00 = 0, a01 = 0, a02 = 0;
            float a10 = 0, a11 = 0, a12 = 0;
            float a20 = 0, a21 = 0, a22 = 0;

            for (int i = 0; i < n; i++)
            {
                float w = particles[Indices[i]].InverseMass;
                Vector3 q = RestPositions[i];
                Vector3 p = particles[Indices[i]].PredictedPosition - com;
                a00 += w * q.X * p.X; a01 += w * q.X * p.Y; a02 += w * q.X * p.Z;
                a10 += w * q.Y * p.X; a11 += w * q.Y * p.Y; a12 += w * q.Y * p.Z;
                a20 += w * q.Z * p.X; a21 += w * q.Z * p.Y; a22 += w * q.Z * p.Z;
            }

            // 极分解: 提取旋转部分 R (SVD简化版 — 使用迭代雅可比SVD)
            Matrix4x4 a = new(a00, a01, a02, 0,
                              a10, a11, a12, 0,
                              a20, a21, a22, 0,
                              0, 0, 0, 1);
            Matrix4x4 r = ExtractRotation(a);

            // 目标位置: g_i = com + R * q_i
            // 朝目标位置投影
            float k = Stiffness * stiffnessScale * (1f - Beta);
            for (int i = 0; i < n; i++)
            {
                ref PBDParticle p = ref particles[Indices[i]];
                if (p.InverseMass < 1e-10f) continue;

                Vector3 q = RestPositions[i];
                // R * q
                Vector3 target = com + new Vector3(
                    r.M11 * q.X + r.M12 * q.Y + r.M13 * q.Z,
                    r.M21 * q.X + r.M22 * q.Y + r.M23 * q.Z,
                    r.M31 * q.X + r.M32 * q.Y + r.M33 * q.Z);

                p.PredictedPosition += (target - p.PredictedPosition) * k;
            }
        }

        /// <summary>极分解提取旋转矩阵 (简化Jacobi SVD)。</summary>
        private static Matrix4x4 ExtractRotation(Matrix4x4 a)
        {
            // 迭代5次雅可比旋转求SVD的U, 取R = U * V^T
            Matrix4x4 u = Matrix4x4.Identity;
            const int iterations = 5;
            for (int iter = 0; iter < iterations; iter++)
            {
                for (int p = 0; p < 2; p++)
                {
                    for (int q = p + 1; q < 3; q++)
                    {
                        float apq = GetElement(a, p, q);
                        float app = GetElement(a, p, p);
                        float aqq = GetElement(a, q, q);
                        float apq2 = 2f * apq;
                        if (MathF.Abs(apq2) < 1e-10f) continue;

                        float theta = MathF.Atan2(apq2, aqq - app);
                        float c = MathF.Cos(theta * 0.5f);
                        float s = MathF.Sin(theta * 0.5f);

                        a = RotateMatrix(a, p, q, c, s);
                        u = RotateMatrix(u, p, q, c, s);
                    }
                }
            }
            return u;
        }

        private static float GetElement(Matrix4x4 m, int row, int col)
            => row switch
            {
                0 => col switch { 0 => m.M11, 1 => m.M12, 2 => m.M13, _ => 0 },
                1 => col switch { 0 => m.M21, 1 => m.M22, 2 => m.M23, _ => 0 },
                2 => col switch { 0 => m.M31, 1 => m.M32, 2 => m.M33, _ => 0 },
                _ => 0
            };

        private static Matrix4x4 RotateMatrix(Matrix4x4 m, int p, int q, float c, float s)
        {
            // 旋转: 对列 p,q 施加 Givens 旋转
            Matrix4x4 r = m;
            for (int row = 0; row < 3; row++)
            {
                float mp = GetElement(m, row, p);
                float mq = GetElement(m, row, q);
                SetElement(ref r, row, p, c * mp - s * mq);
                SetElement(ref r, row, q, s * mp + c * mq);
            }
            return r;
        }

        private static void SetElement(ref Matrix4x4 m, int row, int col, float val)
        {
            switch (row, col)
            {
                case (0, 0): m.M11 = val; break;
                case (0, 1): m.M12 = val; break;
                case (0, 2): m.M13 = val; break;
                case (1, 0): m.M21 = val; break;
                case (1, 1): m.M22 = val; break;
                case (1, 2): m.M23 = val; break;
                case (2, 0): m.M31 = val; break;
                case (2, 1): m.M32 = val; break;
                case (2, 2): m.M33 = val; break;
            }
        }
    }

    /// <summary>
    /// PBD求解器 —— 管理粒子集和约束集, 执行PBD积分循环。
    /// 支持子步进(Sub-stepping)提高稳定性。
    /// </summary>
    public sealed class PBDSolver
    {
        private readonly List<IPBDConstraint> _constraints = new(256);
        private PBDParticle[] _particles = Array.Empty<PBDParticle>();

        /// <summary>求解器迭代次数 (高斯-赛德尔)。</summary>
        public int Iterations { get; set; } = 10;

        /// <summary>子步数 (将一个物理步分成 subSteps 个小步)。</summary>
        public int SubSteps { get; set; } = 4;

        /// <summary>速度阻尼 (0~1, 每步速度乘以此系数)。</summary>
        public float VelocityDamping { get; set; } = 0.99f;

        /// <summary>重力加速度。</summary>
        public Vector3 Gravity { get; set; } = new(0f, -9.81f, 0f);

        /// <summary>粒子数量。</summary>
        public int ParticleCount => _particles.Length;

        /// <summary>约束数量。</summary>
        public int ConstraintCount => _constraints.Count;

        /// <summary>粒子数组引用 (外部可读取, 修改需通过接口)。</summary>
        public PBDParticle[] Particles => _particles;

        /// <summary>初始化粒子数组。</summary>
        public void SetParticles(PBDParticle[] particles)
        {
            _particles = particles ?? throw new ArgumentNullException(nameof(particles));
        }

        /// <summary>添加约束。</summary>
        public void AddConstraint(IPBDConstraint constraint)
        {
            if (constraint != null)
                _constraints.Add(constraint);
        }

        /// <summary>清空所有约束。</summary>
        public void ClearConstraints() => _constraints.Clear();

        /// <summary>钉住粒子 (设置逆质量为0)。</summary>
        public void PinParticle(int index)
        {
            if (index >= 0 && index < _particles.Length)
            {
                _particles[index].Pinned = true;
                _particles[index].InverseMass = 0f;
            }
        }

        /// <summary>解除钉住 (恢复原始逆质量)。</summary>
        public void UnpinParticle(int index, float mass)
        {
            if (index >= 0 && index < _particles.Length)
            {
                _particles[index].Pinned = false;
                _particles[index].InverseMass = mass > 0f ? 1f / mass : 0f;
            }
        }

        /// <summary>对外力求和 (重力等)。</summary>
        public void ApplyForce(int index, Vector3 force)
        {
            if (index >= 0 && index < _particles.Length)
                _particles[index].ForceAccumulator += force;
        }

        /// <summary>
        /// 执行一次完整的PBD时间步 (含子步进)。
        /// </summary>
        public void Step(float deltaTime)
        {
            if (deltaTime <= 0f || _particles.Length == 0) return;

            int subSteps = Math.Max(1, SubSteps);
            float dt = deltaTime / subSteps;
            float dtSq = dt * dt;

            for (int s = 0; s < subSteps; s++)
            {
                // 1. 累积外力 (重力)
                for (int i = 0; i < _particles.Length; i++)
                {
                    if (_particles[i].InverseMass > 0f && !_particles[i].Pinned)
                        _particles[i].ForceAccumulator += Gravity / _particles[i].InverseMass;
                }

                // 2. 预测位置: x* = x + v*dt + (F/m)*dt^2
                for (int i = 0; i < _particles.Length; i++)
                {
                    ref PBDParticle p = ref _particles[i];
                    if (p.InverseMass > 0f && !p.Pinned)
                    {
                        Vector3 acc = p.ForceAccumulator * p.InverseMass;
                        p.PredictedPosition = p.Position + p.Velocity * dt + acc * dtSq;
                    }
                    else
                    {
                        p.PredictedPosition = p.Position;
                    }
                    p.ForceAccumulator = Vector3.Zero;
                }

                // 3. 约束求解 (高斯-赛德尔迭代)
                // 有效刚度公式: k_eff = 1 - (1 - k)^(1/iterations)
                for (int iter = 0; iter < Iterations; iter++)
                {
                    float stiffnessScale = 1f - MathF.Pow(1f - 1f / Iterations, 1f / Iterations);
                    // 更精确的: 使用 1 - (1-k)^(1/n), 但这里k已经在约束内部, 用近似
                    float scale = (float)(1f - Math.Pow(1f - 1f / (iter + 1), 1f / Iterations));
                    scale = Math.Clamp(scale, 0.01f, 1f);

                    foreach (var constraint in _constraints)
                    {
                        constraint.Project(_particles, scale);
                    }
                }

                // 4. 速度更新: v = (x_new - x_old) / dt
                for (int i = 0; i < _particles.Length; i++)
                {
                    ref PBDParticle p = ref _particles[i];
                    if (p.InverseMass > 0f && !p.Pinned)
                    {
                        p.Velocity = (p.PredictedPosition - p.Position) / dt;
                        // 速度阻尼
                        p.Velocity *= VelocityDamping;
                    }
                    // 5. 提交位置
                    p.Position = p.PredictedPosition;
                }
            }
        }

        /// <summary>在指定位置创建并添加距离约束。</summary>
        public DistancePBDConstraint AddDistanceConstraint(int a, int b, float restLength, float stiffness = 1f)
        {
            var c = new DistancePBDConstraint(a, b, restLength, stiffness);
            _constraints.Add(c);
            return c;
        }

        /// <summary>添加碰撞约束。</summary>
        public void AddCollisionConstraint(int a, int b, float sumRadius, float stiffness = 1f)
        {
            _constraints.Add(new CollisionPBDConstraint(a, b, sumRadius, stiffness));
        }

        /// <summary>添加体积约束。</summary>
        public void AddVolumeConstraint(int i0, int i1, int i2, int i3, float restVolume, float stiffness = 1f)
        {
            _constraints.Add(new VolumePBDConstraint(i0, i1, i2, i3, restVolume, stiffness));
        }

        /// <summary>
        /// 生成粒子间的碰撞约束 (O(n^2) 暴力检测, 适合小规模)。
        /// 大规模场景请外部用空间哈希预过滤后调用 AddCollisionConstraint。
        /// </summary>
        public int GenerateCollisionConstraints()
        {
            int count = 0;
            int n = _particles.Length;
            for (int i = 0; i < n; i++)
            {
                for (int j = i + 1; j < n; j++)
                {
                    float sumR = _particles[i].Radius + _particles[j].Radius;
                    Vector3 d = _particles[i].PredictedPosition - _particles[j].PredictedPosition;
                    if (d.LengthSquared() < sumR * sumR)
                    {
                        _constraints.Add(new CollisionPBDConstraint(i, j, sumR, 1f));
                        count++;
                    }
                }
            }
            return count;
        }

        /// <summary>获取粒子位置数组 (用于渲染)。</summary>
        public Vector3[] GetPositions()
        {
            var positions = new Vector3[_particles.Length];
            for (int i = 0; i < _particles.Length; i++)
                positions[i] = _particles[i].Position;
            return positions;
        }

        /// <summary>重置所有粒子速度。</summary>
        public void ResetVelocities()
        {
            for (int i = 0; i < _particles.Length; i++)
                _particles[i].Velocity = Vector3.Zero;
        }
    }
}
