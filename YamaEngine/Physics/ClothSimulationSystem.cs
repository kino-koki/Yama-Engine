// ============================================================================================
//  ClothSimulationSystem.cs
//
//  yama引擎 (YamaEngine) 布料模拟系统 —— 基于 PBD (Position Based Dynamics)。
//
//  PBD 布料比传统弹簧-质点方法更稳定, 不会因为时间步过大而爆炸。核心思路:
//    1. 预测位置: x* = x + v*dt + a*dt^2 (a = 重力 + 风力)
//    2. 约束投影 (高斯-赛德尔迭代):
//       - 距离约束: |x_i - x_j| = restLength (结构 + 剪切)
//       - 弯曲约束: 跨1个质点的距离约束 (防止弯折)
//       - 长距离约束: 跨多个质点的距离约束 (防止过度拉伸)
//    3. 自碰撞处理 (空间哈希)
//    4. 速度更新: v = (x_new - x_old) / dt
//    5. 撕裂检测: 约束拉伸超过阈值则断裂
//
//  风力交互: 通过 WindFieldSystem.ComputeWindAt 获取风速, 投影到面法线产生压力
//  固定点约束 (钉子约束): 将质点固定在指定位置
//
//  命名空间: YamaEngine.Physics
//  目标框架: net8.0 / C# 12+
//  外部依赖: 仅 System.Numerics
// ============================================================================================

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;

namespace YamaEngine.Physics
{
    /// <summary>布料质点。</summary>
    public struct ClothPBDParticle
    {
        public Vector3 Position;
        public Vector3 PreviousPosition;
        public Vector3 Velocity;
        public Vector3 AccumulatedForce;
        public float Mass;
        public float InvMass;
        public bool Pinned;
        public Vector3 PinnedPosition;
        public float Radius;
    }

    /// <summary>布料约束类型。</summary>
    public enum ClothPBDConstraintType
    {
        Structural,
        Shear,
        Bend,
        LongRange
    }

    /// <summary>距离约束 (可断裂)。</summary>
    public struct ClothPBDConstraint
    {
        public int ParticleA;
        public int ParticleB;
        public float RestLength;
        public float Stiffness;
        public ClothPBDConstraintType Type;
        public bool Torn;
        public float TearThreshold;

        public ClothPBDConstraint(int a, int b, float restLength, float stiffness, ClothPBDConstraintType type, float tearThreshold = float.MaxValue)
        {
            ParticleA = a;
            ParticleB = b;
            RestLength = MathF.Max(restLength, 1e-6f);
            Stiffness = Math.Clamp(stiffness, 0f, 1f);
            Type = type;
            Torn = false;
            TearThreshold = tearThreshold;
        }
    }

    /// <summary>
    /// 布料模拟系统主类 —— PBD求解。
    /// </summary>
    public sealed class ClothSimulationSystem
    {
        private ClothPBDParticle[] _particles = Array.Empty<ClothPBDParticle>();
        private readonly List<ClothPBDConstraint> _constraints = new(1024);
        private readonly List<(int, int, int)> _triangles = new(512);

        // 空间哈希 (自碰撞)
        private readonly Dictionary<long, List<int>> _hashGrid = new(256);
        private float _hashCellSize = 0.1f;

        // 物理参数
        public Vector3 Gravity { get; set; } = new(0f, -9.81f, 0f);
        public int SolverIterations { get; set; } = 8;
        public int SubSteps { get; set; } = 2;
        public float VelocityDamping { get; set; } = 0.98f;
        public float WindDragCoefficient { get; set; } = 0.5f;
        public float SelfCollisionRadius { get; set; } = 0.02f;
        public float GlobalStiffness { get; set; } = 1.0f;

        // 风场集成
        private WindFieldSystem? _windField;
        private float _windTime;

        // 边界 (简单AABB容器)
        public Vector3? BoundaryMin { get; set; }
        public Vector3? BoundaryMax { get; set; }
        public float BoundaryRestitution { get; set; } = 0.3f;

        public int ParticleCount => _particles.Length;
        public int ConstraintCount => _constraints.Count;
        public int ActiveConstraintCount
        {
            get
            {
                int count = 0;
                for (int i = 0; i < _constraints.Count; i++)
                    if (!_constraints[i].Torn) count++;
                return count;
            }
        }

        /// <summary>设置风场系统 (可选, 用于风力交互)。</summary>
        public void SetWindField(WindFieldSystem? windField) => _windField = windField;

        /// <summary>设置质点数组。</summary>
        public void SetParticles(ClothPBDParticle[] particles)
        {
            _particles = particles ?? throw new ArgumentNullException(nameof(particles));
        }

        public ClothPBDParticle[] Particles => _particles;

        /// <summary>添加约束。</summary>
        public void AddConstraint(int a, int b, float stiffness, ClothPBDConstraintType type, float tearThreshold = float.MaxValue)
        {
            if (a < 0 || b < 0 || a >= _particles.Length || b >= _particles.Length) return;
            float restLen = Vector3.Distance(_particles[a].Position, _particles[b].Position);
            _constraints.Add(new ClothPBDConstraint(a, b, restLen, stiffness, type, tearThreshold));
        }

        /// <summary>添加三角形 (用于法线计算和渲染)。</summary>
        public void AddTriangle(int a, int b, int c) => _triangles.Add((a, b, c));

        /// <summary>钉住质点 (固定点约束/钉子约束)。</summary>
        public void PinParticle(int index, Vector3? position = null)
        {
            if (index < 0 || index >= _particles.Length) return;
            _particles[index].Pinned = true;
            _particles[index].InvMass = 0f;
            _particles[index].PinnedPosition = position ?? _particles[index].Position;
        }

        /// <summary>解除钉住。</summary>
        public void UnpinParticle(int index, float mass)
        {
            if (index < 0 || index >= _particles.Length) return;
            _particles[index].Pinned = false;
            _particles[index].Mass = mass;
            _particles[index].InvMass = mass > 0f ? 1f / mass : 0f;
        }

        /// <summary>从网格创建布料。</summary>
        public static ClothSimulationSystem CreateFromGrid(
            Vector3 origin, int rows, int cols, float spacing,
            float massPerParticle, float structuralStiffness, float shearStiffness,
            float bendStiffness, float longRangeStiffness, int longRangeStride = 3,
            float tearThreshold = float.MaxValue)
        {
            int count = rows * cols;
            var particles = new ClothPBDParticle[count];
            for (int r = 0; r < rows; r++)
            {
                for (int c = 0; c < cols; c++)
                {
                    int idx = r * cols + c;
                    var pos = origin + new Vector3(c * spacing, -r * spacing, 0);
                    particles[idx] = new ClothPBDParticle
                    {
                        Position = pos,
                        PreviousPosition = pos,
                        Velocity = Vector3.Zero,
                        AccumulatedForce = Vector3.Zero,
                        Mass = massPerParticle,
                        InvMass = 1f / massPerParticle,
                        Pinned = false,
                        Radius = spacing * 0.3f
                    };
                }
            }

            var system = new ClothSimulationSystem();
            system.SetParticles(particles);

            // 结构约束 (右、下)
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                {
                    int idx = r * cols + c;
                    if (c < cols - 1) system.AddConstraint(idx, idx + 1, structuralStiffness, ClothPBDConstraintType.Structural, tearThreshold);
                    if (r < rows - 1) system.AddConstraint(idx, idx + cols, structuralStiffness, ClothPBDConstraintType.Structural, tearThreshold);
                }

            // 剪切约束 (对角)
            for (int r = 0; r < rows - 1; r++)
                for (int c = 0; c < cols - 1; c++)
                {
                    int idx = r * cols + c;
                    system.AddConstraint(idx, idx + cols + 1, shearStiffness, ClothPBDConstraintType.Shear, tearThreshold);
                    system.AddConstraint(idx + 1, idx + cols, shearStiffness, ClothPBDConstraintType.Shear, tearThreshold);
                }

            // 弯曲约束 (跨1)
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                {
                    int idx = r * cols + c;
                    if (c < cols - 2) system.AddConstraint(idx, idx + 2, bendStiffness, ClothPBDConstraintType.Bend, tearThreshold * 1.5f);
                    if (r < rows - 2) system.AddConstraint(idx, idx + 2 * cols, bendStiffness, ClothPBDConstraintType.Bend, tearThreshold * 1.5f);
                }

            // 长距离约束 (跨 longRangeStride)
            if (longRangeStiffness > 0f && longRangeStride > 1)
            {
                for (int r = 0; r < rows; r++)
                    for (int c = 0; c < cols; c++)
                    {
                        int idx = r * cols + c;
                        if (c < cols - longRangeStride)
                            system.AddConstraint(idx, idx + longRangeStride, longRangeStiffness, ClothPBDConstraintType.LongRange, tearThreshold);
                        if (r < rows - longRangeStride)
                            system.AddConstraint(idx, idx + longRangeStride * cols, longRangeStiffness, ClothPBDConstraintType.LongRange, tearThreshold);
                    }
            }

            // 三角形
            for (int r = 0; r < rows - 1; r++)
                for (int c = 0; c < cols - 1; c++)
                {
                    int idx = r * cols + c;
                    system.AddTriangle(idx, idx + 1, idx + cols);
                    system.AddTriangle(idx + 1, idx + cols + 1, idx + cols);
                }

            return system;
        }

        /// <summary>执行布料模拟步。</summary>
        public void Step(float deltaTime)
        {
            if (deltaTime <= 0f || _particles.Length == 0) return;

            _windTime += deltaTime;
            int subSteps = Math.Max(1, SubSteps);
            float dt = deltaTime / subSteps;
            float dtSq = dt * dt;

            for (int s = 0; s < subSteps; s++)
            {
                // === PBD Step 1: 累积外力 + 预测位置 ===
                for (int i = 0; i < _particles.Length; i++)
                {
                    ref ClothPBDParticle p = ref _particles[i];
                    p.PreviousPosition = p.Position;

                    if (p.Pinned || p.InvMass <= 0f)
                    {
                        p.Position = p.PinnedPosition;
                        continue;
                    }

                    Vector3 force = Gravity * p.Mass;

                    // 风力
                    if (_windField != null)
                    {
                        Vector3 windVel = _windField.ComputeWindAt(p.Position, _windTime);
                        Vector3 normal = ComputeParticleNormal(i);
                        Vector3 relVel = windVel - p.Velocity;
                        // 风力 = drag * (v_wind - v) projected on normal * area
                        float pressure = Vector3.Dot(relVel, normal) * WindDragCoefficient;
                        force += normal * pressure * p.Mass * 2f;
                        // 全向阻力
                        force += relVel * (WindDragCoefficient * 0.2f * p.Mass);
                    }

                    Vector3 acc = force * p.InvMass;
                    p.Velocity += acc * dt;
                    p.Velocity *= VelocityDamping;
                    p.Position = p.Position + p.Velocity * dt;
                }

                // === PBD Step 2: 约束投影 (高斯-赛德尔迭代) ===
                for (int iter = 0; iter < SolverIterations; iter++)
                {
                    // 有效刚度: k_eff = 1 - (1-k)^(1/n)
                    float kEff = 1f - MathF.Pow(1f - GlobalStiffness / SolverIterations, 1f / SolverIterations);

                    for (int ci = 0; ci < _constraints.Count; ci++)
                    {
                        ref ClothPBDConstraint con = ref CollectionsMarshal.AsSpan(_constraints)[ci];
                        if (con.Torn) continue;

                        ref ClothPBDParticle pa = ref _particles[con.ParticleA];
                        ref ClothPBDParticle pb = ref _particles[con.ParticleB];

                        float wSum = pa.InvMass + pb.InvMass;
                        if (wSum < 1e-10f) continue;

                        Vector3 delta = pa.Position - pb.Position;
                        float dist = delta.Length();
                        if (dist < 1e-8f) continue;

                        // 撕裂检测
                        float stretch = dist / con.RestLength;
                        if (stretch > con.TearThreshold)
                        {
                            con.Torn = true;
                            continue;
                        }

                        float C = dist - con.RestLength;
                        float stiffness = con.Stiffness * kEff;
                        float lambda = -C / wSum * stiffness;

                        Vector3 dir = delta / dist;
                        pa.Position += dir * (lambda * pa.InvMass);
                        pb.Position -= dir * (lambda * pb.InvMass);
                    }

                    // 自碰撞
                    SolveSelfCollisions();

                    // 边界
                    SolveBoundary();
                }

                // === PBD Step 3: 速度更新 ===
                for (int i = 0; i < _particles.Length; i++)
                {
                    ref ClothPBDParticle p = ref _particles[i];
                    if (p.Pinned || p.InvMass <= 0f)
                    {
                        p.Velocity = Vector3.Zero;
                    }
                    else
                    {
                        p.Velocity = (p.Position - p.PreviousPosition) / dt;
                    }
                }
            }
        }

        /// <summary>计算质点的法线 (从相邻三角形面法线平均)。</summary>
        private Vector3 ComputeParticleNormal(int index)
        {
            Vector3 normal = Vector3.Zero;
            for (int i = 0; i < _triangles.Count; i++)
            {
                var (a, b, c) = _triangles[i];
                if (a != index && b != index && c != index) continue;

                Vector3 n = Vector3.Cross(
                    _particles[b].Position - _particles[a].Position,
                    _particles[c].Position - _particles[a].Position);

                normal += n;
            }

            if (normal.LengthSquared() < 1e-12f)
                return Vector3.UnitZ;
            return Vector3.Normalize(normal);
        }

        /// <summary>自碰撞处理 (空间哈希)。</summary>
        private void SolveSelfCollisions()
        {
            _hashGrid.Clear();
            _hashCellSize = MathF.Max(SelfCollisionRadius * 2f, 0.01f);

            for (int i = 0; i < _particles.Length; i++)
            {
                long hash = HashPosition(_particles[i].Position);
                if (!_hashGrid.TryGetValue(hash, out var list))
                {
                    list = new List<int>(4);
                    _hashGrid[hash] = list;
                }
                list.Add(i);
            }

            float minDist = SelfCollisionRadius * 2f;
            float minDistSq = minDist * minDist;

            for (int i = 0; i < _particles.Length; i++)
            {
                ref ClothPBDParticle pi = ref _particles[i];
                int cx = (int)MathF.Floor(pi.Position.X / _hashCellSize);
                int cy = (int)MathF.Floor(pi.Position.Y / _hashCellSize);
                int cz = (int)MathF.Floor(pi.Position.Z / _hashCellSize);

                for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                for (int dz = -1; dz <= 1; dz++)
                {
                    long hash = HashCell(cx + dx, cy + dy, cz + dz);
                    if (!_hashGrid.TryGetValue(hash, out var list)) continue;

                    for (int li = 0; li < list.Count; li++)
                    {
                        int j = list[li];
                        if (j <= i) continue;

                        ref ClothPBDParticle pj = ref _particles[j];
                        Vector3 delta = pi.Position - pj.Position;
                        float distSq = delta.LengthSquared();
                        if (distSq >= minDistSq || distSq < 1e-12f) continue;

                        float dist = MathF.Sqrt(distSq);
                        Vector3 dir = delta / dist;
                        float penetration = minDist - dist;
                        float wSum = pi.InvMass + pj.InvMass;
                        if (wSum < 1e-10f) continue;

                        float corr = penetration / wSum * 0.5f;
                        pi.Position += dir * (corr * pi.InvMass);
                        pj.Position -= dir * (corr * pj.InvMass);
                    }
                }
            }
        }

        /// <summary>边界碰撞 (简单AABB容器)。</summary>
        private void SolveBoundary()
        {
            if (!BoundaryMin.HasValue || !BoundaryMax.HasValue) return;
            Vector3 min = BoundaryMin.Value;
            Vector3 max = BoundaryMax.Value;

            for (int i = 0; i < _particles.Length; i++)
            {
                ref ClothPBDParticle p = ref _particles[i];
                float r = p.Radius;

                if (p.Position.X < min.X + r) p.Position.X = min.X + r;
                if (p.Position.X > max.X - r) p.Position.X = max.X - r;
                if (p.Position.Y < min.Y + r) p.Position.Y = min.Y + r;
                if (p.Position.Y > max.Y - r) p.Position.Y = max.Y - r;
                if (p.Position.Z < min.Z + r) p.Position.Z = min.Z + r;
                if (p.Position.Z > max.Z - r) p.Position.Z = max.Z - r;
            }
        }

        private long HashPosition(Vector3 pos)
        {
            int cx = (int)MathF.Floor(pos.X / _hashCellSize);
            int cy = (int)MathF.Floor(pos.Y / _hashCellSize);
            int cz = (int)MathF.Floor(pos.Z / _hashCellSize);
            return HashCell(cx, cy, cz);
        }

        private static long HashCell(int cx, int cy, int cz)
            => (long)cx * 73856093L ^ (long)cy * 19349663L ^ (long)cz * 83492791L;

        /// <summary>获取所有质点位置。</summary>
        public Vector3[] GetPositions()
        {
            var positions = new Vector3[_particles.Length];
            for (int i = 0; i < _particles.Length; i++)
                positions[i] = _particles[i].Position;
            return positions;
        }

        /// <summary>获取断裂的约束数量。</summary>
        public int GetTornConstraintCount()
        {
            int count = 0;
            for (int i = 0; i < _constraints.Count; i++)
                if (_constraints[i].Torn) count++;
            return count;
        }

        /// <summary>对外部施加力到质点。</summary>
        public void ApplyForce(int index, Vector3 force)
        {
            if (index >= 0 && index < _particles.Length)
                _particles[index].AccumulatedForce += force;
        }
    }
}
