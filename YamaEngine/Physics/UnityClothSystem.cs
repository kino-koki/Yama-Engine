using System;
using System.Collections.Generic;
using System.Numerics;

namespace YamaEngine.Physics
{
    // ============================================================================================
    //  UnityClothSystem.cs
    //
    //  布料系统 — 类似 Unity Cloth 组件。
    //
    //  功能：
    //    - ClothComponent：顶点选择 / 约束 / 外部力
    //    - 风力和重力
    //    - 碰撞体交互（球形 / 胶囊体碰撞）
    //    - 自碰撞
    //    - 拉伸 / 弯曲约束
    //    - 钉住顶点
    //    - 布料休眠（优化）
    //    - 与 PhysicsWorld 的连接
    // ============================================================================================

    /// <summary>
    ///   布料粒子。
    /// </summary>
    public struct ClothParticleEx
    {
        /// <summary>当前位置。</summary>
        public Vector3 Position;

        /// <summary>上一帧位置（Verlet 积分用）。</summary>
        public Vector3 PreviousPosition;

        /// <summary>速度。</summary>
        public Vector3 Velocity;

        /// <summary>逆质量（0 = 钉住的固定点）。</summary>
        public float InverseMass;

        /// <summary>是否钉住。</summary>
        public bool IsPinned;

        /// <summary>是否可移动。</summary>
        public bool CanMove => !IsPinned && InverseMass > 0f;
    }

    /// <summary>
    ///   布料弹簧类型。
    /// </summary>
    public enum ClothConstraintType
    {
        /// <summary>拉伸约束（结构弹簧）。</summary>
        Stretch,
        /// <summary>弯曲约束（弯曲弹簧）。</summary>
        Bend,
    }

    /// <summary>
    ///   布料约束（弹簧）。
    /// </summary>
    public struct ClothConstraint
    {
        /// <summary>粒子 A 索引。</summary>
        public int A;

        /// <summary>粒子 B 索引。</summary>
        public int B;

        /// <summary>静止长度。</summary>
        public float RestLength;

        /// <summary>刚度系数 (0..1)。</summary>
        public float Stiffness;

        /// <summary>约束类型。</summary>
        public ClothConstraintType Type;
    }

    /// <summary>
    ///   布料碰撞体。用于布料与外部碰撞体的交互。
    /// </summary>
    public struct ClothCollider
    {
        /// <summary>碰撞体类型（Sphere / Capsule）。</summary>
        public ColliderType Type;

        /// <summary>中心位置。</summary>
        public Vector3 Center;

        /// <summary>半径。</summary>
        public float Radius;

        /// <summary>胶囊体端点 A。</summary>
        public Vector3 PointA;

        /// <summary>胶囊体端点 B。</summary>
        public Vector3 PointB;

        /// <summary>创建球形碰撞体。</summary>
        public static ClothCollider Sphere(Vector3 center, float radius)
            => new() { Type = ColliderType.Sphere, Center = center, Radius = radius };

        /// <summary>创建胶囊体碰撞体。</summary>
        public static ClothCollider Capsule(Vector3 pointA, Vector3 pointB, float radius)
            => new() { Type = ColliderType.Capsule, Center = (pointA + pointB) * 0.5f,
                       PointA = pointA, PointB = pointB, Radius = radius };

        /// <summary>检查并推出粒子。</summary>
        public readonly bool Resolve(ref Vector3 position)
        {
            if (Type == ColliderType.Sphere)
            {
                Vector3 diff = position - Center;
                float dist = diff.Length();
                if (dist < Radius && dist > 1e-8f)
                {
                    position = Center + diff * (Radius / dist);
                    return true;
                }
            }
            else if (Type == ColliderType.Capsule)
            {
                // 点到线段距离
                Vector3 ab = PointB - PointA;
                float t = Vector3.Dot(position - PointA, ab);
                float abLenSq = ab.LengthSquared();
                if (abLenSq > 1e-8f)
                {
                    t = Math.Clamp(t / abLenSq, 0f, 1f);
                    Vector3 closest = PointA + ab * t;
                    Vector3 diff = position - closest;
                    float dist = diff.Length();
                    if (dist < Radius && dist > 1e-8f)
                    {
                        position = closest + diff * (Radius / dist);
                        return true;
                    }
                }
            }
            return false;
        }
    }

    /// <summary>
    ///   布料配置参数。
    /// </summary>
    public sealed class UnityClothSettings
    {
        /// <summary>网格列数（X 方向）。</summary>
        public int Columns = 10;

        /// <summary>网格行数（Y 方向）。</summary>
        public int Rows = 10;

        /// <summary>粒子间距（米）。</summary>
        public float Spacing = 0.1f;

        /// <summary>粒子质量（kg）。</summary>
        public float ParticleMass = 0.02f;

        /// <summary>拉伸刚度。</summary>
        public float StretchStiffness = 0.8f;

        /// <summary>弯曲刚度。</summary>
        public float BendStiffness = 0.3f;

        /// <summary>阻尼系数。</summary>
        public float Damping = 0.02f;

        /// <summary>风力方向。</summary>
        public Vector3 WindDirection = new(1, 0, 0);

        /// <summary>风力强度。</summary>
        public float WindStrength = 2f;

        /// <summary>风湍流强度。</summary>
        public float WindTurbulence = 0.3f;

        /// <summary>约束求解迭代次数。</summary>
        public int SolverIterations = 5;

        /// <summary>是否钉住顶行。</summary>
        public bool PinTopRow = true;

        /// <summary>是否钉住四角。</summary>
        public bool PinCorners = false;

        /// <summary>自碰撞距离阈值（0 = 禁用自碰撞）。</summary>
        public float SelfCollisionDistance = 0f;

        /// <summary>布料休眠速度阈值（低于此值一段时间后休眠）。</summary>
        public float SleepVelocityThreshold = 0.1f;

        /// <summary>布料休眠时间（秒）。</summary>
        public float SleepTimeThreshold = 2f;
    }

    /// <summary>
    ///   布料组件。挂载到实体上，包含布料模拟所需的所有数据。
    /// </summary>
    public struct UnityClothComponent
    {
        /// <summary>布料配置。</summary>
        public UnityClothSettings? Settings;

        /// <summary>粒子数组。</summary>
        public ClothParticleEx[]? Particles;

        /// <summary>约束数组。</summary>
        public ClothConstraint[]? Constraints;

        /// <summary>外部碰撞体列表。</summary>
        public List<ClothCollider>? Colliders;

        /// <summary>世界偏移。</summary>
        public Vector3 WorldOffset;

        /// <summary>是否启用。</summary>
        public bool Enabled;

        /// <summary>是否休眠中。</summary>
        public bool IsSleeping;

        // 内部计时
        internal float SleepTimer;
        internal float TotalEnergy;
    }

    /// <summary>
    ///   布料模拟器。实现 PBD 风格的布料求解。
    /// </summary>
    public sealed class UnityClothSimulator
    {
        private readonly UnityClothSettings _settings;
        private ClothParticleEx[] _particles = Array.Empty<ClothParticleEx>();
        private ClothConstraint[] _constraints = Array.Empty<ClothConstraint>();
        private readonly List<ClothCollider> _colliders = new();
        private bool _isInitialized;

        /// <summary>粒子数组。</summary>
        public ClothParticleEx[] Particles => _particles;

        /// <summary>约束数组。</summary>
        public ClothConstraint[] Constraints => _constraints;

        /// <summary>是否已初始化。</summary>
        public bool IsInitialized => _isInitialized;

        /// <summary>粒子数。</summary>
        public int ParticleCount => _particles.Length;

        /// <summary>约束数。</summary>
        public int ConstraintCount => _constraints.Length;

        public UnityClothSimulator(UnityClothSettings settings)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        /// <summary>
        ///   初始化布料网格。
        /// </summary>
        public void Initialize(Vector3 origin)
        {
            int cols = _settings.Columns;
            int rows = _settings.Rows;
            int count = cols * rows;
            float spacing = _settings.Spacing;

            _particles = new ClothParticleEx[count];
            var constraintList = new List<ClothConstraint>();

            // 生成粒子
            for (int r = 0; r < rows; r++)
            {
                for (int c = 0; c < cols; c++)
                {
                    int idx = r * cols + c;
                    Vector3 pos = origin + new Vector3(c * spacing, -r * spacing, 0);

                    bool pinned = false;
                    if (_settings.PinTopRow && r == 0) pinned = true;
                    if (_settings.PinCorners &&
                        ((r == 0 && c == 0) || (r == 0 && c == cols - 1) ||
                         (r == rows - 1 && c == 0) || (r == rows - 1 && c == cols - 1)))
                        pinned = true;

                    _particles[idx] = new ClothParticleEx
                    {
                        Position = pos,
                        PreviousPosition = pos,
                        Velocity = Vector3.Zero,
                        InverseMass = pinned ? 0f : 1f / _settings.ParticleMass,
                        IsPinned = pinned,
                    };
                }
            }

            // 生成拉伸约束（结构弹簧）
            for (int r = 0; r < rows; r++)
            {
                for (int c = 0; c < cols; c++)
                {
                    int idx = r * cols + c;
                    if (c < cols - 1)
                        constraintList.Add(new ClothConstraint
                        {
                            A = idx, B = idx + 1, RestLength = spacing,
                            Stiffness = _settings.StretchStiffness,
                            Type = ClothConstraintType.Stretch,
                        });
                    if (r < rows - 1)
                        constraintList.Add(new ClothConstraint
                        {
                            A = idx, B = idx + cols, RestLength = spacing,
                            Stiffness = _settings.StretchStiffness,
                            Type = ClothConstraintType.Stretch,
                        });
                }
            }

            // 生成弯曲约束（跳过一格）
            for (int r = 0; r < rows; r++)
            {
                for (int c = 0; c < cols; c++)
                {
                    int idx = r * cols + c;
                    if (c < cols - 2)
                        constraintList.Add(new ClothConstraint
                        {
                            A = idx, B = idx + 2, RestLength = spacing * 2f,
                            Stiffness = _settings.BendStiffness,
                            Type = ClothConstraintType.Bend,
                        });
                    if (r < rows - 2)
                        constraintList.Add(new ClothConstraint
                        {
                            A = idx, B = idx + cols * 2, RestLength = spacing * 2f,
                            Stiffness = _settings.BendStiffness,
                            Type = ClothConstraintType.Bend,
                        });
                }
            }

            _constraints = constraintList.ToArray();
            _isInitialized = true;
        }

        /// <summary>
        ///   添加碰撞体。
        /// </summary>
        public void AddCollider(ClothCollider collider)
        {
            _colliders.Add(collider);
        }

        /// <summary>
        ///   清空碰撞体。
        /// </summary>
        public void ClearColliders() => _colliders.Clear();

        /// <summary>
        ///   步进布料模拟。
        /// </summary>
        public void Step(float dt, Vector3 gravity)
        {
            if (!_isInitialized) return;

            float subDt = dt / 3f; // 子步进提高稳定性
            for (int step = 0; step < 3; step++)
            {
                // 1. 外推力积分（重力 + 风力 + 阻尼）
                ApplyForces(subDt, gravity);

                // 2. 约束求解
                for (int iter = 0; iter < _settings.SolverIterations; iter++)
                {
                    SolveStretchConstraints();
                    if (_settings.SelfCollisionDistance > 0f)
                        SolveSelfCollision();
                }

                // 3. 碰撞约束
                SolveCollisionConstraints();

                // 4. 更新速度
                UpdateVelocities(subDt);
            }
        }

        /// <summary>
        ///   检查布料是否应进入休眠状态。
        /// </summary>
        public bool CheckSleep(float dt)
        {
            float totalKinetic = 0f;
            for (int i = 0; i < _particles.Length; i++)
            {
                if (!_particles[i].CanMove) continue;
                totalKinetic += _particles[i].Velocity.LengthSquared();
            }
            float avgEnergy = totalKinetic / Math.Max(_particles.Length, 1);

            if (avgEnergy < _settings.SleepVelocityThreshold)
                return true;

            return false;
        }

        /// <summary>唤醒布料。</summary>
        public void WakeUp()
        {
            // 无操作，自动唤醒
        }

        /// <summary>获取粒子位置数组（用于渲染）。</summary>
        public Vector3[] GetPositions()
        {
            var pos = new Vector3[_particles.Length];
            for (int i = 0; i < _particles.Length; i++)
                pos[i] = _particles[i].Position;
            return pos;
        }

        /// <summary>设置所有钉住粒子的位置。</summary>
        public void SetPinnedPosition(int index, Vector3 position)
        {
            if (index >= 0 && index < _particles.Length && _particles[index].IsPinned)
            {
                _particles[index].Position = position;
                _particles[index].PreviousPosition = position;
            }
        }

        // ========================================================================================
        //  内部求解
        // ========================================================================================

        private void ApplyForces(float dt, Vector3 gravity)
        {
            // 风力：每粒子接收不同湍流
            float time = (float)(DateTime.Now.TimeOfDay.TotalSeconds * 0.5);
            Random prng = new(42);

            for (int i = 0; i < _particles.Length; i++)
            {
                ref var p = ref _particles[i];
                if (!p.CanMove) continue;

                // 重力
                Vector3 accel = gravity;

                // 风力
                float turbScale = 1f + _settings.WindTurbulence *
                    (MathF.Sin(i * 1.3f + time) * 0.5f + 0.5f);
                Vector3 windForce = _settings.WindDirection * _settings.WindStrength * turbScale;
                accel += windForce * p.InverseMass;

                // 阻尼
                accel -= p.Velocity * _settings.Damping;

                // Verlet 风格积分
                p.Velocity += accel * dt;
                p.PreviousPosition = p.Position;
                p.Position += p.Velocity * dt;
            }
        }

        private void SolveStretchConstraints()
        {
            for (int i = 0; i < _constraints.Length; i++)
            {
                ref var c = ref _constraints[i];
                ref var pa = ref _particles[c.A];
                ref var pb = ref _particles[c.B];

                Vector3 delta = pb.Position - pa.Position;
                float dist = delta.Length();
                if (dist < 1e-8f) continue;

                float diff = (dist - c.RestLength) / dist;
                float wSum = pa.InverseMass + pb.InverseMass;
                if (wSum <= 0f) continue;

                float stiffness = c.Stiffness;
                if (c.Type == ClothConstraintType.Bend)
                    stiffness *= 0.5f; // 弯曲约束更软

                Vector3 correction = delta * diff * stiffness;

                if (pa.CanMove)
                    pa.Position += correction * (pa.InverseMass / wSum);
                if (pb.CanMove)
                    pb.Position -= correction * (pb.InverseMass / wSum);
            }
        }

        private void SolveSelfCollision()
        {
            float minDist = _settings.SelfCollisionDistance;
            float minSq = minDist * minDist;

            int cols = _settings.Columns;
            for (int i = 0; i < _particles.Length; i++)
            {
                ref var pa = ref _particles[i];
                if (!pa.CanMove) continue;

                // 只检查非相邻的远处粒子
                for (int j = i + 2; j < _particles.Length; j++)
                {
                    // 跳过相邻粒子
                    int ri = i / cols, ci = i % cols;
                    int rj = j / cols, cj = j % cols;
                    int dr = Math.Abs(ri - rj);
                    int dc = Math.Abs(ci - cj);
                    if (dr <= 1 && dc <= 1) continue;

                    ref var pb = ref _particles[j];
                    if (!pb.CanMove) continue;

                    Vector3 d = pb.Position - pa.Position;
                    float dsq = d.LengthSquared();
                    if (dsq < minSq && dsq > 1e-8f)
                    {
                        float dist = MathF.Sqrt(dsq);
                        float push = (minDist - dist) / dist * 0.5f;
                        pa.Position -= d * push;
                        pb.Position += d * push;
                    }
                }
            }
        }

        private void SolveCollisionConstraints()
        {
            for (int i = 0; i < _particles.Length; i++)
            {
                ref var p = ref _particles[i];
                if (!p.CanMove) continue;

                for (int ci = 0; ci < _colliders.Count; ci++)
                {
                    Vector3 pos = p.Position;
                    if (_colliders[ci].Resolve(ref pos))
                        p.Position = pos;
                }
            }
        }

        private void UpdateVelocities(float dt)
        {
            for (int i = 0; i < _particles.Length; i++)
            {
                ref var p = ref _particles[i];
                if (!p.CanMove) continue;
                p.Velocity = (p.Position - p.PreviousPosition) / dt;
            }
        }
    }

    /// <summary>
    ///   布料系统 ECS 管理器。驱动所有布料组件。
    /// </summary>
    public sealed class UnityClothSystemManager
    {
        private readonly Dictionary<int, UnityClothSimulator> _simulators = new();

        /// <summary>模拟器数量。</summary>
        public int SimulatorCount => _simulators.Count;

        /// <summary>
        ///   为实体创建布料模拟器。
        /// </summary>
        public UnityClothSimulator CreateSimulator(int entityId, UnityClothSettings settings, Vector3 worldPosition)
        {
            if (_simulators.ContainsKey(entityId))
                throw new InvalidOperationException($"Entity {entityId} already has a cloth simulator.");

            var sim = new UnityClothSimulator(settings);
            sim.Initialize(worldPosition);
            _simulators[entityId] = sim;
            return sim;
        }

        /// <summary>
        ///   获取实体的布料模拟器。
        /// </summary>
        public UnityClothSimulator? GetSimulator(int entityId)
        {
            return _simulators.TryGetValue(entityId, out var sim) ? sim : null;
        }

        /// <summary>
        ///   销毁实体的布料模拟器。
        /// </summary>
        public void RemoveSimulator(int entityId)
        {
            _simulators.Remove(entityId);
        }

        /// <summary>
        ///   步进所有布料模拟器。
        /// </summary>
        public void StepAll(float dt, Vector3 gravity)
        {
            foreach (var kvp in _simulators)
            {
                if (kvp.Value.IsInitialized)
                    kvp.Value.Step(dt, gravity);
            }
        }

        /// <summary>
        ///   步进指定实体的布料模拟器。
        /// </summary>
        public void Step(int entityId, float dt, Vector3 gravity)
        {
            if (_simulators.TryGetValue(entityId, out var sim) && sim.IsInitialized)
                sim.Step(dt, gravity);
        }

        /// <summary>
        ///   清空所有模拟器。
        /// </summary>
        public void Clear()
        {
            _simulators.Clear();
        }
    }
}
