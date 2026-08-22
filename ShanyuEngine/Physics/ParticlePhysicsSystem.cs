using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ShanyuEngine.Physics
{
    // ============================================================================================
    //  ParticlePhysicsSystem.cs
    //
    //  粒子物理系统。这里说的不是GPU粒子特效，是物理引擎层面的粒子。
    //  每个粒子有位置、速度、质量、寿命，受重力、风、碰撞影响。
    //
    //  典型用途:
    //    - 爆炸碎片
    //    - 弹片
    //    - 火花
    //    - 烟雾
    //    - 沙子/碎石
    //    - 雨雪
    //    - 子弹(简化)
    //
    //  性能要点:
    //    - 粒子数量可能很大(几千到几万)，必须用SoA(Structure of Arrays)
    //    - 碰撞检测要简化，用空间网格
    //    - 死掉的粒子要回收(对象池)
    //    - 远距离粒子用LOD简化
    //
    //  TODO: GPU加速还没做，目前纯CPU。粒子数上万会卡。
    // ============================================================================================

    /// <summary>粒子类型。</summary>
    public enum ParticleType
    {
        Spark,          // 火花
        Debris,         // 碎片
        Smoke,          // 烟雾
        Dust,           // 灰尘
        Water,          // 水滴
        Snow,           // 雪花
        Rain,           // 雨滴
        Ember,          // 余烬
        Bubble,         // 气泡
        Bullet,         // 子弹(简化)
        Custom,
    }

    /// <summary>粒子物理行为。</summary>
    public enum ParticleBehavior
    {
        /// <summary>标准: 受重力，受碰撞。</summary>
        Standard,

        /// <summary>轻飘: 受浮力影响大(烟雾、气泡)。</summary>
        Buoyant,

        /// <summary>弹跳: 高弹性(弹球)。</summary>
        Bouncy,

        /// <summary>粘性: 碰到就停(粘液)。</summary>
        Sticky,

        /// <summary>穿透: 不碰撞(烟雾穿透墙壁)。</summary>
        Passthrough,

        /// <summary>磁性: 受磁场影响。</summary>
        Magnetic,

        /// <summary>带电: 受电场影响。</summary>
        Charged,

        /// <summary>燃烧: 会点燃其他粒子。</summary>
        Burning,
    }

    /// <summary>
    ///   粒子系统配置。
    /// </summary>
    public struct ParticleSystemConfig
    {
        public int MaxParticles;
        public Vector3 Gravity;
        public float GlobalDamping;
        public float AirDensity;
        public bool EnableCollision;
        public bool EnableSelfCollision;
        public float CollisionRadius;
        public int SpatialGridCellSize;
        public bool EnableSleeping;
        public float SleepVelocity;
        public float SleepTime;
        public bool EnableLOD;
        public float LODDistance1;
        public float LODDistance2;
        public float MaxLifetime;
        public bool EnableWind;
        public float WindMultiplier;
        public bool EnableBouncing;
        public float DefaultRestitution;
        public float DefaultFriction;

        public static ParticleSystemConfig Default => new()
        {
            MaxParticles = 10000,
            Gravity = new Vector3(0, -9.81f, 0),
            GlobalDamping = 0.01f,
            AirDensity = 1.225f,
            EnableCollision = true,
            EnableSelfCollision = false,
            CollisionRadius = 0.05f,
            SpatialGridCellSize = 2,
            EnableSleeping = true,
            SleepVelocity = 0.1f,
            SleepTime = 2f,
            EnableLOD = true,
            LODDistance1 = 20f,
            LODDistance2 = 50f,
            MaxLifetime = 30f,
            EnableWind = true,
            WindMultiplier = 0.5f,
            EnableBouncing = true,
            DefaultRestitution = 0.3f,
            DefaultFriction = 0.5f,
        };
    }

    /// <summary>
    ///   粒子发射器配置。
    /// </summary>
    public struct ParticleEmitterConfig
    {
        public Vector3 Position;
        public Vector3 Direction = Vector3.UnitY;
        public float EmitRate = 10f;            // 每秒发射数
        public float EmitSpeed = 5f;
        public float SpeedVariance = 1f;
        public float ConeAngle = 0.3f;          // 发射锥角(弧度)
        public float Lifetime = 3f;
        public float LifetimeVariance = 0.5f;
        public float Mass = 0.001f;
        public float MassVariance = 0.0001f;
        public float StartSize = 0.05f;
        public float EndSize = 0.01f;
        public Vector3 StartColor = new(1f, 0.8f, 0.2f);
        public Vector3 EndColor = new(0.5f, 0.1f, 0f);
        public float Drag = 0.5f;
        public ParticleType Type = ParticleType.Spark;
        public ParticleBehavior Behavior = ParticleBehavior.Standard;
        public float Restitution = 0.3f;
        public float Friction = 0.5f;
        public int MaxEmit = -1;                // 最大发射数(-1无限)
        public bool IsLooping = true;
        public float BurstCount;                // 一次性发射数量(0=持续发射)
        public Vector3 InheritVelocity;          // 继承发射器速度
        public bool EmitOnStart = true;

        public ParticleEmitterConfig()
        {
            Position = Vector3.Zero;
        }
    }

    /// <summary>
    ///   粒子接触信息。
    /// </summary>
    public struct ParticleContact
    {
        public int ParticleIndex;
        public Vector3 Point;
        public Vector3 Normal;
        public float Penetration;
        public int OtherEntityId;
        public float Restitution;
        public float Friction;
    }

    /// <summary>
    ///   粒子碰撞世界查询。
    /// </summary>
    public interface IParticleWorldQuery
    {
        bool SweepSphere(Vector3 start, Vector3 end, float radius, out ParticleContact contact);
        int OverlapSphere(Vector3 center, float radius, ParticleContact[] results, int maxResults);
    }

    /// <summary>
    ///   粒子数据(SoA布局，cache友好)。
    /// </summary>
    public sealed class ParticleData
    {
        public int Count;
        public int Capacity;

        // SoA数据
        public Vector3[] Positions;
        public Vector3[] Velocities;
        public Vector3[] Forces;
        public float[] Masses;
        public float[] InverseMasses;
        public float[] Lifetimes;
        public float[] MaxLifetimes;
        public float[] Sizes;
        public Vector3[] Colors;
        public ParticleType[] Types;
        public ParticleBehavior[] Behaviors;
        public int[] CollisionGroups;
        public float[] Restitutions;
        public float[] Frictions;
        public bool[] IsActive;
        public bool[] IsSleeping;
        public float[] SleepTimers;
        public float[] Charges;
        public int[] LODLevels;

        // 空闲索引栈(对象池)
        private readonly Stack<int> _freeIndices = new();

        public ParticleData(int capacity)
        {
            Capacity = capacity;
            Positions = new Vector3[capacity];
            Velocities = new Vector3[capacity];
            Forces = new Vector3[capacity];
            Masses = new float[capacity];
            InverseMasses = new float[capacity];
            Lifetimes = new float[capacity];
            MaxLifetimes = new float[capacity];
            Sizes = new float[capacity];
            Colors = new Vector3[capacity];
            Types = new ParticleType[capacity];
            Behaviors = new ParticleBehavior[capacity];
            CollisionGroups = new int[capacity];
            Restitutions = new float[capacity];
            Frictions = new float[capacity];
            IsActive = new bool[capacity];
            IsSleeping = new bool[capacity];
            SleepTimers = new float[capacity];
            Charges = new float[capacity];
            LODLevels = new int[capacity];

            for (int i = capacity - 1; i >= 0; i--)
                _freeIndices.Push(i);
        }

        /// <summary>分配一个粒子索引。</summary>
        public int Allocate()
        {
            if (_freeIndices.Count == 0) return -1;
            int idx = _freeIndices.Pop();
            IsActive[idx] = true;
            IsSleeping[idx] = false;
            SleepTimers[idx] = 0;
            Count++;
            return idx;
        }

        /// <summary>释放一个粒子。</summary>
        public void Free(int idx)
        {
            if (idx < 0 || idx >= Capacity) return;
            if (!IsActive[idx]) return;
            IsActive[idx] = false;
            _freeIndices.Push(idx);
            Count--;
        }

        /// <summary>获取活跃粒子索引列表。</summary>
        public List<int> GetActiveIndices()
        {
            var result = new List<int>(Count);
            for (int i = 0; i < Capacity; i++)
            {
                if (IsActive[i])
                    result.Add(i);
            }
            return result;
        }

        public int ActiveCount => Count;
    }

    /// <summary>
    ///   粒子发射器。
    /// </summary>
    public sealed class ParticleEmitter
    {
        public int Id;
        public string Name = "";
        public ParticleEmitterConfig Config;
        public float EmitAccumulator;
        public int EmittedCount;
        public bool IsEnabled = true;
        public float ElapsedTime;
        public Vector3 CurrentPosition;
        public Vector3 CurrentVelocity;
        public Quaternion CurrentRotation = Quaternion.Identity;

        public bool IsFinished => !Config.IsLooping && EmittedCount >= Config.MaxEmit && Config.MaxEmit > 0;

        public void UpdatePosition(Vector3 pos, Vector3 vel, Quaternion rot)
        {
            CurrentPosition = pos;
            CurrentVelocity = vel;
            CurrentRotation = rot;
        }

        /// <summary>尝试发射粒子，返回发射的索引列表。</summary>
        public void Emit(float dt, ParticleData data, List<int> emittedIndices)
        {
            if (!IsEnabled || IsFinished) return;

            ElapsedTime += dt;

            // 一次性发射
            if (Config.BurstCount > 0 && EmittedCount == 0)
            {
                int count = (int)Config.BurstCount;
                for (int i = 0; i < count; i++)
                {
                    int idx = data.Allocate();
                    if (idx < 0) break;
                    InitParticle(idx, data);
                    emittedIndices.Add(idx);
                    EmittedCount++;
                }
                return;
            }

            // 持续发射
            EmitAccumulator += Config.EmitRate * dt;
            while (EmitAccumulator >= 1f)
            {
                EmitAccumulator -= 1f;
                int idx = data.Allocate();
                if (idx < 0) break;
                InitParticle(idx, data);
                emittedIndices.Add(idx);
                EmittedCount++;

                if (Config.MaxEmit > 0 && EmittedCount >= Config.MaxEmit)
                {
                    IsEnabled = false;
                    break;
                }
            }
        }

        private void InitParticle(int idx, ParticleData data)
        {
            // 位置
            data.Positions[idx] = CurrentPosition;

            // 速度: 在锥形范围内随机
            Vector3 dir = Config.Direction;
            if (Config.ConeAngle > 0)
            {
                dir = RandomInCone(Config.Direction, Config.ConeAngle);
            }
            dir = Vector3.Transform(dir, CurrentRotation);

            float speed = Config.EmitSpeed + (Random.Shared.NextSingle() - 0.5f) * 2f * Config.SpeedVariance;
            data.Velocities[idx] = dir * speed + Config.InheritVelocity;

            // 质量
            float mass = Config.Mass + (Random.Shared.NextSingle() - 0.5f) * Config.MassVariance;
            data.Masses[idx] = mass;
            data.InverseMasses[idx] = mass > 0 ? 1f / mass : 0;

            // 寿命
            float life = Config.Lifetime + (Random.Shared.NextSingle() - 0.5f) * Config.LifetimeVariance;
            data.Lifetimes[idx] = life;
            data.MaxLifetimes[idx] = life;

            // 视觉
            data.Sizes[idx] = Config.StartSize;
            data.Colors[idx] = Config.StartColor;
            data.Types[idx] = Config.Type;
            data.Behaviors[idx] = Config.Behavior;
            data.CollisionGroups[idx] = 1;
            data.Restitutions[idx] = Config.Restitution;
            data.Frictions[idx] = Config.Friction;
            data.Forces[idx] = Vector3.Zero;
            data.Charges[idx] = 0;
            data.LODLevels[idx] = 0;
        }

        private Vector3 RandomInCone(Vector3 axis, float maxAngle)
        {
            // 在锥形范围内均匀采样
            float cosMax = MathF.Cos(maxAngle);
            float cosTheta = 1f - Random.Shared.NextSingle() * (1f - cosMax);
            float sinTheta = MathF.Sqrt(1f - cosTheta * cosTheta);
            float phi = Random.Shared.NextSingle() * MathF.PI * 2f;

            Vector3 localDir = new(
                sinTheta * MathF.Cos(phi),
                cosTheta,
                sinTheta * MathF.Sin(phi));

            // 把局部方向(以Y为轴)转到axis
            if (axis == Vector3.UnitY) return localDir;
            Vector3 rotAxis = Vector3.Cross(Vector3.UnitY, axis);
            if (rotAxis.LengthSquared() < 1e-6f) return localDir;
            rotAxis = Vector3.Normalize(rotAxis);
            float angle = MathF.Acos(Vector3.Dot(Vector3.UnitY, Vector3.Normalize(axis)));
            Quaternion rot = Quaternion.CreateFromAxisAngle(rotAxis, angle);
            return Vector3.Transform(localDir, rot);
        }
    }

    /// <summary>
    ///   空间网格。用于粒子-粒子碰撞检测。
    /// </summary>
    public sealed class ParticleSpatialGrid
    {
        private readonly Dictionary<(int, int, int), List<int>> _cells = new();
        private float _cellSize;
        private float _invCellSize;

        public int CellCount => _cells.Count;
        public float CellSize
        {
            get => _cellSize;
            set
            {
                _cellSize = MathF.Max(0.01f, value);
                _invCellSize = 1f / _cellSize;
            }
        }

        public ParticleSpatialGrid(float cellSize)
        {
            CellSize = cellSize;
        }

        public void Clear()
        {
            _cells.Clear();
        }

        public void Insert(int particleIndex, Vector3 position)
        {
            var key = GetKey(position);
            if (!_cells.TryGetValue(key, out var list))
            {
                list = new List<int>(8);
                _cells[key] = list;
            }
            list.Add(particleIndex);
        }

        /// <summary>查询某点附近的粒子。</summary>
        public void Query(Vector3 position, float radius, List<int> results)
        {
            results.Clear();
            int range = (int)MathF.Ceiling(radius * _invCellSize);
            var centerKey = GetKey(position);

            for (int dx = -range; dx <= range; dx++)
            {
                for (int dy = -range; dy <= range; dy++)
                {
                    for (int dz = -range; dz <= range; dz++)
                    {
                        var key = (centerKey.Item1 + dx, centerKey.Item2 + dy, centerKey.Item3 + dz);
                        if (_cells.TryGetValue(key, out var list))
                        {
                            results.AddRange(list);
                        }
                    }
                }
            }
        }

        /// <summary>查询某点所在单元格及邻居的粒子。</summary>
        public void QueryNeighbors(Vector3 position, List<int> results)
        {
            results.Clear();
            var key = GetKey(position);
            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        var k = (key.Item1 + dx, key.Item2 + dy, key.Item3 + dz);
                        if (_cells.TryGetValue(k, out var list))
                            results.AddRange(list);
                    }
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private (int, int, int) GetKey(Vector3 p)
        {
            return (
                (int)MathF.Floor(p.X * _invCellSize),
                (int)MathF.Floor(p.Y * _invCellSize),
                (int)MathF.Floor(p.Z * _invCellSize));
        }
    }

    /// <summary>
    ///   粒子物理系统。
    /// </summary>
    public sealed class ParticlePhysicsSystem : IDisposable
    {
        private ParticleData _data;
        private ParticleSystemConfig _config;
        private readonly List<ParticleEmitter> _emitters = new();
        private readonly ParticleSpatialGrid _grid;
        private IParticleWorldQuery? _world;
        private readonly List<int> _tempIndices = new(256);
        private readonly List<ParticleContact> _contacts = new(256);
        private readonly List<int> _toRemove = new(256);
        private readonly List<int> _emittedThisFrame = new(256);
        private bool _disposed;
        private float _time;
        private Vector3 _windVelocity = Vector3.Zero;
        private int _frameCount;

        // 统计
        private float _lastStepTime;
        private int _collisionChecks;
        private int _collisionsFound;
        private int _maxActiveParticles;

        public ParticleData Data => _data;
        public ParticleSystemConfig Config
        {
            get => _config;
            set => _config = value;
        }
        public IReadOnlyList<ParticleEmitter> Emitters => _emitters;
        public int ActiveCount => _data.ActiveCount;
        public int MaxActiveParticles => _maxActiveParticles;
        public float Time => _time;
        public Vector3 WindVelocity
        {
            get => _windVelocity;
            set => _windVelocity = value;
        }
        public float LastStepTimeMs => _lastStepTime;
        public int CollisionChecks => _collisionChecks;
        public int CollisionsFound => _collisionsFound;

        public ParticlePhysicsSystem() : this(ParticleSystemConfig.Default) { }

        public ParticlePhysicsSystem(ParticleSystemConfig config)
        {
            _config = config;
            _data = new ParticleData(config.MaxParticles);
            _grid = new ParticleSpatialGrid(config.CollisionRadius * config.SpatialGridCellSize);
        }

        public void BindWorld(IParticleWorldQuery world)
        {
            _world = world;
        }

        // ========================================================================================
        //  发射器管理
        // ========================================================================================

        public ParticleEmitter AddEmitter(ParticleEmitterConfig config)
        {
            var emitter = new ParticleEmitter
            {
                Id = _emitters.Count + 1,
                Name = $"Emitter_{_emitters.Count + 1}",
                Config = config,
            };
            _emitters.Add(emitter);
            return emitter;
        }

        public bool RemoveEmitter(int emitterId)
        {
            int idx = _emitters.FindIndex(e => e.Id == emitterId);
            if (idx < 0) return false;
            _emitters.RemoveAt(idx);
            return true;
        }

        public ParticleEmitter? GetEmitter(int emitterId)
        {
            return _emitters.Find(e => e.Id == emitterId);
        }

        public void ClearEmitters()
        {
            _emitters.Clear();
        }

        // ========================================================================================
        //  手动添加粒子
        // ========================================================================================

        /// <summary>手动添加一个粒子。</summary>
        public int AddParticle(Vector3 position, Vector3 velocity, float mass, float lifetime,
            ParticleType type = ParticleType.Spark, ParticleBehavior behavior = ParticleBehavior.Standard)
        {
            int idx = _data.Allocate();
            if (idx < 0) return -1;

            _data.Positions[idx] = position;
            _data.Velocities[idx] = velocity;
            _data.Masses[idx] = mass;
            _data.InverseMasses[idx] = mass > 0 ? 1f / mass : 0;
            _data.Lifetimes[idx] = lifetime;
            _data.MaxLifetimes[idx] = lifetime;
            _data.Sizes[idx] = 0.05f;
            _data.Colors[idx] = new Vector3(1, 1, 1);
            _data.Types[idx] = type;
            _data.Behaviors[idx] = behavior;
            _data.CollisionGroups[idx] = 1;
            _data.Restitutions[idx] = _config.DefaultRestitution;
            _data.Frictions[idx] = _config.DefaultFriction;
            _data.Forces[idx] = Vector3.Zero;
            _data.Charges[idx] = 0;
            _data.LODLevels[idx] = 0;

            return idx;
        }

        /// <summary>批量发射(爆炸)。</summary>
        public void EmitBurst(Vector3 center, int count, float speed, float lifetime,
            ParticleType type = ParticleType.Debris)
        {
            for (int i = 0; i < count; i++)
            {
                Vector3 dir = RandomOnSphere();
                AddParticle(center, dir * speed * (0.5f + Random.Shared.NextSingle() * 0.5f),
                    0.01f, lifetime * (0.8f + Random.Shared.NextSingle() * 0.4f), type);
            }
        }

        /// <summary>移除粒子。</summary>
        public void RemoveParticle(int idx)
        {
            _data.Free(idx);
        }

        /// <summary>清除所有粒子。</summary>
        public void ClearParticles()
        {
            for (int i = 0; i < _data.Capacity; i++)
            {
                if (_data.IsActive[i])
                    _data.Free(i);
            }
        }

        // ========================================================================================
        //  主更新
        // ========================================================================================

        public void Update(float dt)
        {
            if (_disposed) return;
            if (dt <= 0) return;

            var sw = System.Diagnostics.Stopwatch.StartNew();

            _time += dt;
            _frameCount++;
            _collisionChecks = 0;
            _collisionsFound = 0;
            _emittedThisFrame.Clear();
            _toRemove.Clear();

            // 1. 发射粒子
            foreach (var emitter in _emitters)
            {
                emitter.Emit(dt, _data, _emittedThisFrame);
            }

            // 2. 更新寿命并标记死掉的
            UpdateLifetimes(dt);

            // 3. 应用外力(重力、风、阻力)
            ApplyForces(dt);

            // 4. 积分速度
            IntegrateVelocities(dt);

            // 5. 碰撞检测和处理
            if (_config.EnableCollision)
            {
                HandleCollisions(dt);
            }

            // 6. 积分位置
            IntegratePositions(dt);

            // 7. 更新视觉(颜色/大小渐变)
            UpdateVisuals();

            // 8. LOD更新
            if (_config.EnableLOD)
            {
                UpdateLOD();
            }

            // 9. 清理死粒子
            foreach (var idx in _toRemove)
            {
                _data.Free(idx);
            }

            // 统计
            if (_data.ActiveCount > _maxActiveParticles)
                _maxActiveParticles = _data.ActiveCount;

            sw.Stop();
            _lastStepTime = (float)sw.Elapsed.TotalMilliseconds;
        }

        // ========================================================================================
        //  寿命
        // ========================================================================================

        private void UpdateLifetimes(float dt)
        {
            for (int i = 0; i < _data.Capacity; i++)
            {
                if (!_data.IsActive[i]) continue;

                _data.Lifetimes[i] -= dt;
                if (_data.Lifetimes[i] <= 0)
                {
                    _toRemove.Add(i);
                    continue;
                }

                // 睡眠检测
                if (_config.EnableSleeping)
                {
                    if (_data.Velocities[i].LengthSquared() < _config.SleepVelocity * _config.SleepVelocity)
                    {
                        _data.SleepTimers[i] += dt;
                        if (_data.SleepTimers[i] > _config.SleepTime)
                            _data.IsSleeping[i] = true;
                    }
                    else
                    {
                        _data.SleepTimers[i] = 0;
                        _data.IsSleeping[i] = false;
                    }
                }
            }
        }

        // ========================================================================================
        //  外力
        // ========================================================================================

        private void ApplyForces(float dt)
        {
            for (int i = 0; i < _data.Capacity; i++)
            {
                if (!_data.IsActive[i] || _data.IsSleeping[i]) continue;

                var behavior = _data.Behaviors[i];
                var type = _data.Types[i];
                float mass = _data.Masses[i];

                // 重力(烟雾/气泡向上浮)
                Vector3 gravityForce = _config.Gravity;
                if (behavior == ParticleBehavior.Buoyant || type == ParticleType.Smoke ||
                    type == ParticleType.Bubble)
                {
                    gravityForce = -_config.Gravity * 0.3f; // 反向+减弱
                }
                _data.Forces[i] += gravityForce * mass;

                // 风
                if (_config.EnableWind && _windVelocity != Vector3.Zero)
                {
                    Vector3 relWind = _windVelocity * _config.WindMultiplier - _data.Velocities[i];
                    float drag = behavior == ParticleBehavior.Buoyant ? 2f : 0.5f;
                    _data.Forces[i] += relWind * drag * mass;
                }

                // 空气阻力(二次阻力)
                Vector3 vel = _data.Velocities[i];
                float speed = vel.Length();
                if (speed > 0.01f)
                {
                    float dragForce = 0.5f * _config.AirDensity * speed * speed * 0.1f * 0.5f;
                    _data.Forces[i] -= Vector3.Normalize(vel) * dragForce;
                }

                // 全局阻尼
                _data.Forces[i] -= vel * _config.GlobalDamping * mass;
            }
        }

        // ========================================================================================
        //  速度积分
        // ========================================================================================

        private void IntegrateVelocities(float dt)
        {
            for (int i = 0; i < _data.Capacity; i++)
            {
                if (!_data.IsActive[i] || _data.IsSleeping[i]) continue;

                _data.Velocities[i] += _data.Forces[i] * _data.InverseMasses[i] * dt;
                _data.Forces[i] = Vector3.Zero;
            }
        }

        // ========================================================================================
        //  位置积分
        // ========================================================================================

        private void IntegratePositions(float dt)
        {
            for (int i = 0; i < _data.Capacity; i++)
            {
                if (!_data.IsActive[i] || _data.IsSleeping[i]) continue;
                _data.Positions[i] += _data.Velocities[i] * dt;
            }
        }

        // ========================================================================================
        //  碰撞
        // ========================================================================================

        private void HandleCollisions(float dt)
        {
            _contacts.Clear();

            // 1. 和世界碰撞
            if (_world != null)
            {
                HandleWorldCollisions();
            }

            // 2. 自碰撞(可选)
            if (_config.EnableSelfCollision)
            {
                HandleSelfCollisions();
            }

            // 3. 解决接触
            foreach (var c in _contacts)
            {
                ResolveContact(c);
            }
        }

        private void HandleWorldCollisions()
        {
            for (int i = 0; i < _data.Capacity; i++)
            {
                if (!_data.IsActive[i] || _data.IsSleeping[i]) continue;

                Vector3 pos = _data.Positions[i];
                Vector3 vel = _data.Velocities[i];
                float radius = _data.Sizes[i] * 0.5f;

                // 预测下一位置
                Vector3 nextPos = pos + vel * (1f / 60f); // 简化: 假设1帧

                _collisionChecks++;
                if (_world!.SweepSphere(pos, nextPos, radius, out var contact))
                {
                    contact.ParticleIndex = i;
                    contact.Restitution = _data.Restitutions[i];
                    contact.Friction = _data.Frictions[i];
                    _contacts.Add(contact);
                    _collisionsFound++;
                }
            }
        }

        private void HandleSelfCollisions()
        {
            // 重建空间网格
            _grid.Clear();
            for (int i = 0; i < _data.Capacity; i++)
            {
                if (!_data.IsActive[i] || _data.IsSleeping[i]) continue;
                _grid.Insert(i, _data.Positions[i]);
            }

            float checkRadius = _config.CollisionRadius * 2f;

            for (int i = 0; i < _data.Capacity; i++)
            {
                if (!_data.IsActive[i] || _data.IsSleeping[i]) continue;

                _grid.Query(_data.Positions[i], checkRadius, _tempIndices);

                foreach (var j in _tempIndices)
                {
                    if (j <= i) continue;
                    if (!_data.IsActive[j]) continue;

                    Vector3 diff = _data.Positions[j] - _data.Positions[i];
                    float distSq = diff.LengthSquared();
                    float minDist = _data.Sizes[i] * 0.5f + _data.Sizes[j] * 0.5f;

                    _collisionChecks++;
                    if (distSq < minDist * minDist && distSq > 1e-8f)
                    {
                        float dist = MathF.Sqrt(distSq);
                        Vector3 normal = diff / dist;
                        float penetration = minDist - dist;

                        _contacts.Add(new ParticleContact
                        {
                            ParticleIndex = i,
                            Normal = normal,
                            Penetration = penetration * 0.5f,
                            Restitution = Math.Min(_data.Restitutions[i], _data.Restitutions[j]),
                            Friction = Math.Max(_data.Frictions[i], _data.Frictions[j]),
                            OtherEntityId = j,
                        });
                        _contacts.Add(new ParticleContact
                        {
                            ParticleIndex = j,
                            Normal = -normal,
                            Penetration = penetration * 0.5f,
                            Restitution = Math.Min(_data.Restitutions[i], _data.Restitutions[j]),
                            Friction = Math.Max(_data.Frictions[i], _data.Frictions[j]),
                            OtherEntityId = i,
                        });
                        _collisionsFound++;
                    }
                }
            }
        }

        private void ResolveContact(ParticleContact c)
        {
            int i = c.ParticleIndex;
            if (!_data.IsActive[i]) return;

            // 粘性粒子: 碰到就停
            if (_data.Behaviors[i] == ParticleBehavior.Sticky)
            {
                _data.Velocities[i] *= 0.1f;
                _data.Positions[i] += c.Normal * c.Penetration;
                return;
            }

            // 穿透粒子: 不处理
            if (_data.Behaviors[i] == ParticleBehavior.Passthrough)
                return;

            // 位置修正
            _data.Positions[i] += c.Normal * c.Penetration;

            // 速度反射
            Vector3 vel = _data.Velocities[i];
            float velAlongNormal = Vector3.Dot(vel, c.Normal);

            if (velAlongNormal < 0)
            {
                // 反射
                float j_imp = -(1f + c.Restitution) * velAlongNormal;
                _data.Velocities[i] += c.Normal * j_imp;

                // 摩擦: 切向速度衰减
                Vector3 tangent = vel - c.Normal * velAlongNormal;
                if (tangent.LengthSquared() > 1e-6f)
                {
                    _data.Velocities[i] -= tangent * c.Friction;
                }
            }

            // 唤醒
            _data.IsSleeping[i] = false;
            _data.SleepTimers[i] = 0;
        }

        // ========================================================================================
        //  视觉更新
        // ========================================================================================

        private void UpdateVisuals()
        {
            for (int i = 0; i < _data.Capacity; i++)
            {
                if (!_data.IsActive[i]) continue;

                float t = 1f - _data.Lifetimes[i] / _data.MaxLifetimes[i];
                t = Math.Clamp(t, 0f, 1f);

                // 大小渐变
                // 这里只更新数据，渲染由外部处理
            }
        }

        // ========================================================================================
        //  LOD
        // ========================================================================================

        private void UpdateLOD()
        {
            // 假设观察者在原点(实际要从相机获取)
            Vector3 observer = Vector3.Zero; // TODO: 从外部传入

            for (int i = 0; i < _data.Capacity; i++)
            {
                if (!_data.IsActive[i]) continue;

                float distSq = Vector3.DistanceSquared(_data.Positions[i], observer);
                if (distSq > _config.LODDistance2 * _config.LODDistance2)
                    _data.LODLevels[i] = 2; // 远: 简化
                else if (distSq > _config.LODDistance1 * _config.LODDistance1)
                    _data.LODLevels[i] = 1; // 中: 正常
                else
                    _data.LODLevels[i] = 0; // 近: 精细
            }
        }

        // ========================================================================================
        //  查询
        // ========================================================================================

        /// <summary>获取某半径内的所有粒子。</summary>
        public List<int> GetParticlesInRadius(Vector3 center, float radius)
        {
            var result = new List<int>(64);
            float radiusSq = radius * radius;
            for (int i = 0; i < _data.Capacity; i++)
            {
                if (!_data.IsActive[i]) continue;
                if (Vector3.DistanceSquared(_data.Positions[i], center) <= radiusSq)
                    result.Add(i);
            }
            return result;
        }

        /// <summary>对附近粒子施加冲量(爆炸)。</summary>
        public void ApplyExplosion(Vector3 center, float radius, float force, float damageRadius = 0f)
        {
            float radiusSq = radius * radius;
            for (int i = 0; i < _data.Capacity; i++)
            {
                if (!_data.IsActive[i]) continue;

                Vector3 diff = _data.Positions[i] - center;
                float distSq = diff.LengthSquared();
                if (distSq > radiusSq) continue;

                float dist = MathF.Sqrt(distSq);
                if (dist < 0.01f) continue;

                float falloff = 1f - dist / radius;
                Vector3 dir = diff / dist;
                _data.Velocities[i] += dir * force * falloff * _data.InverseMasses[i];
                _data.IsSleeping[i] = false;
            }
        }

        /// <summary>对所有粒子施加力。</summary>
        public void ApplyForceToAll(Vector3 force)
        {
            for (int i = 0; i < _data.Capacity; i++)
            {
                if (!_data.IsActive[i] || _data.IsSleeping[i]) continue;
                _data.Forces[i] += force * _data.Masses[i];
            }
        }

        /// <summary>对某个粒子施加冲量。</summary>
        public void ApplyImpulse(int particleIndex, Vector3 impulse)
        {
            if (particleIndex < 0 || particleIndex >= _data.Capacity) return;
            if (!_data.IsActive[particleIndex]) return;
            _data.Velocities[particleIndex] += impulse * _data.InverseMasses[particleIndex];
            _data.IsSleeping[particleIndex] = false;
        }

        /// <summary>获取粒子数据(用于渲染)。</summary>
        public void GetRenderData(List<(Vector3 pos, Vector3 color, float size, ParticleType type)> output)
        {
            output.Clear();
            for (int i = 0; i < _data.Capacity; i++)
            {
                if (!_data.IsActive[i]) continue;
                output.Add((_data.Positions[i], _data.Colors[i], _data.Sizes[i], _data.Types[i]));
            }
        }

        // ========================================================================================
        //  统计
        // ========================================================================================

        public void GetStats(out int activeCount, out int maxActive, out float stepTime,
            out int collisionChecks, out int collisionsFound)
        {
            activeCount = _data.ActiveCount;
            maxActive = _maxActiveParticles;
            stepTime = _lastStepTime;
            collisionChecks = _collisionChecks;
            collisionsFound = _collisionsFound;
        }

        private static Vector3 RandomOnSphere()
        {
            float u = Random.Shared.NextSingle();
            float v = Random.Shared.NextSingle();
            float theta = 2f * MathF.PI * u;
            float phi = MathF.Acos(2f * v - 1f);
            return new Vector3(
                MathF.Sin(phi) * MathF.Cos(theta),
                MathF.Sin(phi) * MathF.Sin(theta),
                MathF.Cos(phi));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _emitters.Clear();
            _tempIndices.Clear();
            _contacts.Clear();
            _toRemove.Clear();
            _emittedThisFrame.Clear();
        }
    }

    // ============================================================================================
    //  粒子特效预设
    // ============================================================================================

    /// <summary>粒子特效预设。</summary>
    public static class ParticleEffectPresets
    {
        /// <summary>爆炸特效。</summary>
        public static ParticleEmitterConfig Explosion => new()
        {
            EmitRate = 0,
            BurstCount = 100,
            EmitSpeed = 8f,
            SpeedVariance = 3f,
            ConeAngle = MathF.PI,
            Lifetime = 2f,
            LifetimeVariance = 0.5f,
            Mass = 0.01f,
            StartSize = 0.15f,
            EndSize = 0.02f,
            StartColor = new Vector3(1f, 0.9f, 0.3f),
            EndColor = new Vector3(0.3f, 0.05f, 0f),
            Type = ParticleType.Debris,
            Behavior = ParticleBehavior.Bouncy,
            Restitution = 0.5f,
            Friction = 0.3f,
            IsLooping = false,
            Drag = 0.8f,
        };

        /// <summary>火花特效。</summary>
        public static ParticleEmitterConfig Sparks => new()
        {
            EmitRate = 50,
            EmitSpeed = 5f,
            SpeedVariance = 2f,
            ConeAngle = 0.5f,
            Lifetime = 0.5f,
            LifetimeVariance = 0.2f,
            Mass = 0.001f,
            StartSize = 0.03f,
            EndSize = 0.005f,
            StartColor = new Vector3(1f, 0.8f, 0.2f),
            EndColor = new Vector3(0.5f, 0.1f, 0f),
            Type = ParticleType.Spark,
            Behavior = ParticleBehavior.Standard,
            Restitution = 0.6f,
            Friction = 0.2f,
            IsLooping = true,
            Drag = 0.3f,
        };

        /// <summary>烟雾特效。</summary>
        public static ParticleEmitterConfig Smoke => new()
        {
            EmitRate = 20,
            EmitSpeed = 1f,
            SpeedVariance = 0.5f,
            ConeAngle = 0.8f,
            Lifetime = 5f,
            LifetimeVariance = 1f,
            Mass = 0.0001f,
            StartSize = 0.1f,
            EndSize = 0.5f,
            StartColor = new Vector3(0.3f, 0.3f, 0.3f),
            EndColor = new Vector3(0.1f, 0.1f, 0.1f),
            Type = ParticleType.Smoke,
            Behavior = ParticleBehavior.Buoyant,
            Restitution = 0f,
            Friction = 0.9f,
            IsLooping = true,
            Drag = 1.5f,
        };

        /// <summary>火焰特效。</summary>
        public static ParticleEmitterConfig Fire => new()
        {
            EmitRate = 80,
            EmitSpeed = 2f,
            SpeedVariance = 0.5f,
            ConeAngle = 0.2f,
            Lifetime = 1f,
            LifetimeVariance = 0.3f,
            Mass = 0.0001f,
            StartSize = 0.08f,
            EndSize = 0.02f,
            StartColor = new Vector3(1f, 0.6f, 0.1f),
            EndColor = new Vector3(0.8f, 0.1f, 0f),
            Type = ParticleType.Ember,
            Behavior = ParticleBehavior.Buoyant,
            Restitution = 0f,
            Friction = 0.8f,
            IsLooping = true,
            Drag = 0.5f,
        };

        /// <summary>雨水。</summary>
        public static ParticleEmitterConfig Rain => new()
        {
            EmitRate = 200,
            EmitSpeed = 15f,
            SpeedVariance = 2f,
            ConeAngle = 0.05f,
            Direction = new Vector3(0, -1, 0),
            Lifetime = 3f,
            LifetimeVariance = 0.5f,
            Mass = 0.0005f,
            StartSize = 0.02f,
            EndSize = 0.02f,
            StartColor = new Vector3(0.5f, 0.6f, 0.8f),
            EndColor = new Vector3(0.3f, 0.4f, 0.6f),
            Type = ParticleType.Rain,
            Behavior = ParticleBehavior.Standard,
            Restitution = 0.1f,
            Friction = 0.1f,
            IsLooping = true,
            Drag = 0.1f,
        };

        /// <summary>雪花。</summary>
        public static ParticleEmitterConfig Snow => new()
        {
            EmitRate = 50,
            EmitSpeed = 1.5f,
            SpeedVariance = 0.3f,
            ConeAngle = 0.5f,
            Direction = new Vector3(0, -1, 0),
            Lifetime = 8f,
            LifetimeVariance = 2f,
            Mass = 0.00005f,
            StartSize = 0.05f,
            EndSize = 0.05f,
            StartColor = new Vector3(0.9f, 0.9f, 1f),
            EndColor = new Vector3(0.7f, 0.7f, 0.9f),
            Type = ParticleType.Snow,
            Behavior = ParticleBehavior.Buoyant,
            Restitution = 0.2f,
            Friction = 0.6f,
            IsLooping = true,
            Drag = 1.2f,
        };

        /// <summary>水花。</summary>
        public static ParticleEmitterConfig Splash => new()
        {
            EmitRate = 0,
            BurstCount = 30,
            EmitSpeed = 4f,
            SpeedVariance = 1.5f,
            ConeAngle = 1.2f,
            Direction = new Vector3(0, 1, 0),
            Lifetime = 1f,
            LifetimeVariance = 0.3f,
            Mass = 0.002f,
            StartSize = 0.06f,
            EndSize = 0.01f,
            StartColor = new Vector3(0.6f, 0.7f, 0.9f),
            EndColor = new Vector3(0.3f, 0.4f, 0.7f),
            Type = ParticleType.Water,
            Behavior = ParticleBehavior.Standard,
            Restitution = 0.3f,
            Friction = 0.4f,
            IsLooping = false,
            Drag = 0.6f,
        };

        /// <summary>灰尘。</summary>
        public static ParticleEmitterConfig Dust => new()
        {
            EmitRate = 10,
            EmitSpeed = 0.5f,
            SpeedVariance = 0.2f,
            ConeAngle = 1f,
            Lifetime = 4f,
            LifetimeVariance = 1f,
            Mass = 0.00005f,
            StartSize = 0.08f,
            EndSize = 0.15f,
            StartColor = new Vector3(0.7f, 0.6f, 0.4f),
            EndColor = new Vector3(0.4f, 0.3f, 0.2f),
            Type = ParticleType.Dust,
            Behavior = ParticleBehavior.Buoyant,
            Restitution = 0.1f,
            Friction = 0.9f,
            IsLooping = true,
            Drag = 2f,
        };
    }

    // ============================================================================================
    //  粒子群(批量管理)
    // ============================================================================================

    /// <summary>
    ///   粒子组。把多个粒子分组，方便批量操作。
    /// </summary>
    public sealed class ParticleGroup
    {
        public int Id;
        public string Name = "";
        public HashSet<int> ParticleIndices = new();
        public Vector3 GroupVelocity;
        public bool IsEnabled = true;
    }

    /// <summary>
    ///   粒子组管理器。
    /// </summary>
    public sealed class ParticleGroupManager
    {
        private readonly Dictionary<int, ParticleGroup> _groups = new();
        private readonly ParticlePhysicsSystem _system;
        private int _nextId = 1;

        public ParticleGroupManager(ParticlePhysicsSystem system)
        {
            _system = system;
        }

        public int CreateGroup(string name)
        {
            int id = _nextId++;
            _groups[id] = new ParticleGroup { Id = id, Name = name };
            return id;
        }

        public bool AddToGroup(int groupId, int particleIndex)
        {
            if (!_groups.TryGetValue(groupId, out var g)) return false;
            g.ParticleIndices.Add(particleIndex);
            return true;
        }

        public bool RemoveFromGroup(int groupId, int particleIndex)
        {
            if (!_groups.TryGetValue(groupId, out var g)) return false;
            return g.ParticleIndices.Remove(particleIndex);
        }

        public void ApplyForceToGroup(int groupId, Vector3 force)
        {
            if (!_groups.TryGetValue(groupId, out var g)) return;
            foreach (var idx in g.ParticleIndices)
            {
                _system.ApplyImpulse(idx, force * 0.016f); // 简化: 假设1帧
            }
        }

        public void SetGroupVelocity(int groupId, Vector3 velocity)
        {
            if (!_groups.TryGetValue(groupId, out var g)) return;
            foreach (var idx in g.ParticleIndices)
            {
                // 直接设速度需要内部接口，这里用冲量近似
            }
            g.GroupVelocity = velocity;
        }

        public bool RemoveGroup(int groupId)
        {
            return _groups.Remove(groupId);
        }
    }

    // ============================================================================================
    //  粒子间约束(简单的粒子-弹簧)
    // ============================================================================================

    /// <summary>粒子约束。</summary>
    public struct ParticleConstraint
    {
        public int ParticleA;
        public int ParticleB;
        public float RestLength;
        public float Stiffness;
        public float Damping;
        public bool IsEnabled;
    }

    /// <summary>粒子约束系统。</summary>
    public sealed class ParticleConstraintSystem
    {
        private readonly List<ParticleConstraint> _constraints = new();
        private readonly ParticleData _data;

        public int Count => _constraints.Count;

        public ParticleConstraintSystem(ParticleData data)
        {
            _data = data;
        }

        public void AddConstraint(int a, int b, float restLength, float stiffness = 1f, float damping = 0.1f)
        {
            _constraints.Add(new ParticleConstraint
            {
                ParticleA = a,
                ParticleB = b,
                RestLength = restLength,
                Stiffness = stiffness,
                Damping = damping,
                IsEnabled = true,
            });
        }

        public void Clear()
        {
            _constraints.Clear();
        }

        public void Solve(float dt)
        {
            foreach (var c in _constraints)
            {
                if (!c.IsEnabled) continue;
                if (c.ParticleA >= _data.Capacity || c.ParticleB >= _data.Capacity) continue;
                if (!_data.IsActive[c.ParticleA] || !_data.IsActive[c.ParticleB]) continue;

                Vector3 diff = _data.Positions[c.ParticleB] - _data.Positions[c.ParticleA];
                float dist = diff.Length();
                if (dist < 1e-6f) continue;

                Vector3 normal = diff / dist;
                float displacement = dist - c.RestLength;
                Vector3 force = normal * (displacement * c.Stiffness);

                // 阻尼
                Vector3 relVel = _data.Velocities[c.ParticleB] - _data.Velocities[c.ParticleA];
                force += normal * Vector3.Dot(relVel, normal) * c.Damping;

                _data.Velocities[c.ParticleA] += force * _data.InverseMasses[c.ParticleA] * dt;
                _data.Velocities[c.ParticleB] -= force * _data.InverseMasses[c.ParticleB] * dt;
            }
        }
    }
}
