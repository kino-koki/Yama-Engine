// =============================================================================
//  Yama Engine - FlowVFXSimulationReal.cs
//  yama引擎 - 真实粒子模拟（公共入口）
//
//  本文件提供 FlowVFX 模拟系统的公共入口和帧预算管理器，
//  内部委托给 FlowVFXParticleSimulationReal 实现核心逻辑。
//
//    * FrameBudgetManager     - 帧预算管理（最大粒子数/最大发射数/时间预算）
//    * SpawnAccumulator       - 发射累积器（SpawnRate 分数累积/SpawnBurst/SpawnDistance）
//    * FlowVFXSimulationReal   - 模拟器公共入口（Simulate/Reset/统计）
//
//  发射逻辑:
//    SpawnRate: accumulatedFraction += rate * dt; spawnCount = floor(fraction)
//    SpawnBurst: 在指定时间点一次性发射 N 个粒子
//    SpawnDistance: 每移动指定距离发射一次
// =============================================================================
using System;
using System.Collections.Generic;
using System.Numerics;

namespace YamaEngine.Particles
{
    // ──────────────────────────────────────────────────────────────────────
    // 第一部分：帧预算管理器
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 帧预算管理器。控制每帧粒子系统允许的最大开销：
    ///   - 最大活跃粒子数
    ///   - 每帧最大发射数
    ///   - 时间预算（毫秒）
    /// 当超出预算时，延迟或跳过低优先级的操作。
    /// </summary>
    public sealed class FrameBudgetManager
    {
        /// <summary>最大活跃粒子数（超出时停止发射）。</summary>
        public int MaxActiveParticles = 10000;

        /// <summary>每帧最大发射数。</summary>
        public int MaxSpawnPerFrame = 500;

        /// <summary>时间预算（毫秒，0=不限）。</summary>
        public float TimeBudgetMs;

        /// <summary>当前帧已发射粒子数。</summary>
        public int SpawnedThisFrame;

        /// <summary>模拟开始时间（用于时间预算检查）。</summary>
        private long _frameStartTicks;

        /// <summary>是否已超出粒子数预算。</summary>
        public bool IsOverParticleBudget { get; private set; }

        /// <summary>是否已超出时间预算。</summary>
        public bool IsOverTimeBudget { get; private set; }

        /// <summary>开始帧预算计时。</summary>
        public void BeginFrame()
        {
            SpawnedThisFrame = 0;
            IsOverParticleBudget = false;
            IsOverTimeBudget = false;
            _frameStartTicks = DateTime.UtcNow.Ticks;
        }

        /// <summary>记录发射 N 个粒子，检查预算。</summary>
        /// <returns>实际允许发射的数量。</returns>
        public int TrySpawn(int requested, int currentActive)
        {
            IsOverParticleBudget = currentActive >= MaxActiveParticles;
            if (IsOverParticleBudget) return 0;

            int remaining = Math.Max(0, MaxSpawnPerFrame - SpawnedThisFrame);
            int allowed = Math.Min(requested, remaining);
            SpawnedThisFrame += allowed;
            return allowed;
        }

        /// <summary>检查时间预算。</summary>
        public void CheckTimeBudget()
        {
            if (TimeBudgetMs <= 0f) return;
            long elapsedMs = (DateTime.UtcNow.Ticks - _frameStartTicks) / 10000;
            IsOverTimeBudget = elapsedMs >= TimeBudgetMs;
        }

        /// <summary>是否还有剩余预算。</summary>
        public bool HasBudget => !IsOverParticleBudget && !IsOverTimeBudget;

        /// <summary>重置。</summary>
        public void Reset()
        {
            SpawnedThisFrame = 0;
            IsOverParticleBudget = false;
            IsOverTimeBudget = false;
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    // 第二部分：发射累积器
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 发射累积器。管理三种发射模式：
    ///
    ///   1. SpawnRate（持续发射）：accumulatedFraction += rate * dt; count = floor(fraction)
    ///   2. SpawnBurst（脉冲发射）：在指定时间点一次性发射 N 个粒子
    ///   3. SpawnDistance（距离发射）：每移动指定距离发射一次
    /// </summary>
    public sealed class SpawnAccumulator
    {
        /// <summary>持续发射速率（粒子/秒）。</summary>
        public float SpawnRate = 10f;

        /// <summary>累积分数（ SpawnRate * dt 累加到此处）。</summary>
        private float _accumulatedFraction;

        /// <summary>脉冲发射列表。</summary>
        public List<SpawnBurstEntry> Bursts = new();

        /// <summary>距离发射参数。</summary>
        public float SpawnDistanceInterval = 1f; // 每移动 N 单位发射
        public float DistanceTraveled; // 已移动距离

        /// <summary>上次位置（用于距离计算）。</summary>
        public Vector3 LastPosition;

        /// <summary>是否启用持续发射。</summary>
        public bool EnableRate = true;

        /// <summary>是否启用脉冲发射。</summary>
        public bool EnableBursts = true;

        /// <summary>是否启用距离发射。</summary>
        public bool EnableDistanceSpawn;

        /// <summary>累计已发射粒子数。</summary>
        public long TotalSpawned { get; private set; }

        /// <summary>
        /// Tick 发射器，计算本帧应发射的粒子数。
        /// </summary>
        /// <param name="dt">帧间隔（秒）。</param>
        /// <param name="emitterPosition">发射器当前位置（距离发射用）。</param>
        /// <param name="simulationTime">当前模拟时间（脉冲发射用）。</param>
        /// <returns>本帧应发射的粒子数。</returns>
        public int Tick(float dt, Vector3 emitterPosition, float simulationTime)
        {
            int count = 0;

            // ── 1. SpawnRate（持续发射）────────────────────
            if (EnableRate && SpawnRate > 0f)
            {
                _accumulatedFraction += SpawnRate * dt;
                int rateCount = (int)_accumulatedFraction;
                if (rateCount > 0)
                {
                    _accumulatedFraction -= rateCount;
                    count += rateCount;
                }
            }

            // ── 2. SpawnBurst（脉冲发射）─────────────────────
            if (EnableBursts)
            {
                foreach (var burst in Bursts)
                {
                    if (burst.HasTriggered) continue;

                    // 检查是否到达触发时间
                    if (simulationTime >= burst.Time)
                    {
                        // 支持循环脉冲
                        if (burst.Loop && burst.LoopCount > 0 && burst.TriggeredCount >= burst.LoopCount)
                            continue;

                        count += burst.Count;
                        burst.HasTriggered = true;
                        burst.TriggeredCount++;

                        // 循环脉冲重置
                        if (burst.Loop && burst.LoopCount > 0)
                        {
                            burst.Time += burst.LoopInterval;
                            burst.HasTriggered = false;
                        }
                    }
                }
            }

            // ── 3. SpawnDistance（距离发射）──────────────────
            if (EnableDistanceSpawn && SpawnDistanceInterval > 0f)
            {
                float delta = Vector3.Distance(emitterPosition, LastPosition);
                DistanceTraveled += delta;
                int distCount = (int)(DistanceTraveled / SpawnDistanceInterval);
                if (distCount > 0)
                {
                    DistanceTraveled -= distCount * SpawnDistanceInterval;
                    count += distCount;
                }
                LastPosition = emitterPosition;
            }

            TotalSpawned += count;
            return count;
        }

        /// <summary>添加脉冲发射。</summary>
        public void AddBurst(float time, int count, bool loop = false, int loopCount = 0, float loopInterval = 1f)
        {
            Bursts.Add(new SpawnBurstEntry
            {
                Time = time,
                Count = count,
                Loop = loop,
                LoopCount = loopCount,
                LoopInterval = loopInterval
            });
        }

        /// <summary>重置。</summary>
        public void Reset()
        {
            _accumulatedFraction = 0f;
            DistanceTraveled = 0f;
            TotalSpawned = 0;
            foreach (var burst in Bursts)
            {
                burst.HasTriggered = false;
                burst.TriggeredCount = 0;
            }
        }
    }

    /// <summary>脉冲发射条目。</summary>
    public sealed class SpawnBurstEntry
    {
        /// <summary>触发时间（秒）。</summary>
        public float Time;

        /// <summary>发射数量。</summary>
        public int Count;

        /// <summary>是否循环。</summary>
        public bool Loop;

        /// <summary>循环次数（0=无限）。</summary>
        public int LoopCount;

        /// <summary>循环间隔。</summary>
        public float LoopInterval = 1f;

        /// <summary>是否已触发。</summary>
        public bool HasTriggered;

        /// <summary>已触发次数。</summary>
        public int TriggeredCount;
    }

    // ──────────────────────────────────────────────────────────────────────
    // 第三部分：模拟器公共入口
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 真实粒子模拟器（公共入口）。封装发射逻辑、帧预算管理、
    /// 粒子池管理、排序和统计。
    ///
    /// 模拟流程：
    ///   Simulate(dt):
    ///     1. FrameBudgetManager.BeginFrame()
    ///     2. SpawnAccumulator.Tick() → spawnCount
    ///     3. 粒子池分配 spawnCount 个槽位
    ///     4. 执行 SpawnModules（初始化新粒子）
    ///     5. 执行 UpdateModules（力/阻力/碰撞/颜色/生命）
    ///     6. 淘汰死亡粒子（Life &lt;= 0 → swap-remove）
    ///     7. 事件处理（死亡/碰撞事件）
    ///     8. 粒子排序（按距离/生命/材质）
    ///     9. 渲染模块（生成绘制数据）
    /// </summary>
    public sealed class FlowVFXSimulationReal
    {
        /// <summary>核心模拟数据。</summary>
        private readonly FlowVFXParticleSimulationReal _innerSimulation;

        /// <summary>帧预算管理器。</summary>
        public FrameBudgetManager Budget { get; } = new();

        /// <summary>发射累积器。</summary>
        public SpawnAccumulator Spawner { get; } = new();

        /// <summary>发射器位置（世界空间）。</summary>
        public Vector3 EmitterPosition = Vector3.Zero;

        /// <summary>发射器旋转（四元数）。</summary>
        public Quaternion EmitterRotation = Quaternion.Identity;

        /// <summary>粒子数据（只读访问）。</summary>
        public FlowVFXParticleDataReal Data => _innerSimulation.Data;

        /// <summary>事件队列。</summary>
        public ParticleEventQueue Events => _innerSimulation.Events;

        /// <summary>当前活跃粒子数。</summary>
        public int ActiveParticleCount => Data.ActiveCount;

        /// <summary>最大粒子容量。</summary>
        public int MaxParticles => Data.Capacity;

        /// <summary>累计模拟时间。</summary>
        public float SimulationTime => _innerSimulation.SimulationTime;

        /// <summary>已发射粒子总数。</summary>
        public long TotalSpawned => _innerSimulation.TotalSpawned;

        /// <summary>是否正在运行。</summary>
        public bool IsRunning = true;

        /// <summary>是否暂停（暂停时不更新物理）。</summary>
        public bool IsPaused;

        /// <summary>排序模式。</summary>
        public RealParticleSortMode SortMode
        {
            get => _innerSimulation.SortMode;
            set => _innerSimulation.SortMode = value;
        }

        /// <summary>渲染排序用相机位置。</summary>
        public Vector3 CameraPosition
        {
            get => _innerSimulation.CameraPosition;
            set => _innerSimulation.CameraPosition = value;
        }

        /// <summary>创建模拟器。</summary>
        public FlowVFXSimulationReal(int maxParticles)
        {
            _innerSimulation = new FlowVFXParticleSimulationReal(maxParticles);
        }

        /// <summary>重置模拟器。</summary>
        public void Reset()
        {
            _innerSimulation.Reset();
            Spawner.Reset();
            Budget.Reset();
        }

        /// <summary>
        /// 完整模拟一步。
        /// </summary>
        /// <param name="dt">帧间隔（秒）。</param>
        public void Simulate(float dt)
        {
            if (!IsRunning || IsPaused || dt <= 0f) return;

            Budget.BeginFrame();

            // 1. 发射累积
            int desiredCount = Spawner.Tick(dt, EmitterPosition, _innerSimulation.SimulationTime);

            // 2. 预算检查
            int allowedCount = Budget.TrySpawn(desiredCount, Data.ActiveCount);
            if (allowedCount > 0)
            {
                // 委托给内部模拟器发射
                // 通过直接操作粒子数据实现发射
                SpawnNewParticles(allowedCount);
            }

            // 3. 委托给内部模拟器更新
            // （内部模拟器有自己的 Simulate 流程，这里我们直接调用子步骤避免重复）
            SimulateUpdateOnly(dt);

            // 4. 时间预算检查
            Budget.CheckTimeBudget();
        }

        /// <summary>发射新粒子（直接操作粒子数据）。</summary>
        private void SpawnNewParticles(int count)
        {
            for (int i = 0; i < count; i++)
            {
                int idx = Data.Allocate();
                if (idx < 0) break; // 池满

                Data.InitDefaults(idx);

                // 设置随机种子
                Data.RandomSeed[idx] = PseudoRandom(idx + (int)Spawner.TotalSpawned);

                // 初始化位置（发射器位置 + 小随机偏移）
                float r1 = PseudoRandom(idx * 3 + 1);
                float r2 = PseudoRandom(idx * 3 + 2);
                Data.Position[idx] = EmitterPosition + new Vector3(
                    (r1 - 0.5f) * 0.1f,
                    (r2 - 0.5f) * 0.1f,
                    0f);

                // 初始化速度（发射方向 + 随机）
                Vector3 forward = Vector3.Transform(Vector3.UnitZ, EmitterRotation);
                float speed = 1f + PseudoRandom(idx * 5) * 2f;
                Data.Velocity[idx] = forward * speed;

                // 默认生命
                float life = 1f + PseudoRandom(idx * 7) * 2f;
                Data.Life[idx] = life;
                Data.MaxLife[idx] = life;

                // 默认颜色（白色）
                Data.Color[idx] = Vector4.One;

                // 默认大小
                Data.Size[idx] = new Vector2(0.2f, 0.2f);

                // 执行 spawn 模块（如果有）
                foreach (var module in _innerSimulation.SpawnModules)
                {
                    if (module.Enabled)
                        module.Execute(Data, idx, 1, 0f);
                }

                // 触发生成事件
                _innerSimulation.Events.Enqueue(new ParticleEventReal
                {
                    Type = RealParticleEventType.Spawn,
                    ParticleIndex = idx,
                    Position = Data.Position[idx],
                    Velocity = Data.Velocity[idx],
                    LifeAtEvent = Data.Life[idx]
                });
            }
        }

        /// <summary>仅执行更新（不发射新粒子）。</summary>
        private void SimulateUpdateOnly(float dt)
        {
            int activeCount = Data.ActiveCount;
            if (activeCount > 0)
            {
                // 位置积分：x += v * dt
                for (int i = 0; i < activeCount; i++)
                {
                    int idx = Data.ActiveIndices[i];
                    Vector3 v = Data.Velocity[idx];
                    Vector3 delta = v * dt;
                    Data.Position[idx] += delta;
                    Data.DistanceTraveled[idx] += delta.Length();
                }

                // 执行更新模块
                foreach (var module in _innerSimulation.UpdateModules)
                {
                    if (module.Enabled)
                        module.Execute(Data, 0, activeCount, dt);
                }
            }

            // 淘汰死亡粒子
            for (int i = Data.ActiveCount - 1; i >= 0; i--)
            {
                int idx = Data.ActiveIndices[i];
                if (Data.Life[idx] <= 0f)
                {
                    _innerSimulation.Events.Enqueue(new ParticleEventReal
                    {
                        Type = RealParticleEventType.Death,
                        ParticleIndex = idx,
                        Position = Data.Position[idx],
                        Velocity = Data.Velocity[idx],
                        LifeAtEvent = 0f
                    });
                    Data.Release(idx);
                }
            }

            // 执行事件模块
            foreach (var module in _innerSimulation.RenderModules)
            {
                if (module.Enabled && module.Stage == FlowVFXModuleStageReal.Event)
                    module.Execute(Data, 0, Data.ActiveCount, dt);
            }

            // 执行渲染模块
            foreach (var module in _innerSimulation.RenderModules)
            {
                if (module.Enabled && module.Stage == FlowVFXModuleStageReal.Render)
                    module.Execute(Data, 0, Data.ActiveCount, dt);
            }

            // 排序
            ParticleSorter.Sort(Data, SortMode, CameraPosition);
        }

        /// <summary>简单伪随机函数（基于索引的确定性随机）。</summary>
        private static float PseudoRandom(int seed)
        {
            // 简单的哈希函数：将整数映射到 [0,1)
            uint x = (uint)(seed * 1103515245u + 12345u);
            x = ((x >> 16) ^ x) * 0x45d9f3bu;
            x = ((x >> 16) ^ x) * 0x45d9f3bu;
            x = (x >> 16) ^ x;
            return (x & 0x7FFFFFFF) / (float)0x7FFFFFFF;
        }

        /// <summary>添加 spawn 模块。</summary>
        public void AddSpawnModule(FlowVFXModuleReal module) => _innerSimulation.SpawnModules.Add(module);

        /// <summary>添加 update 模块。</summary>
        public void AddUpdateModule(FlowVFXModuleReal module) => _innerSimulation.UpdateModules.Add(module);

        /// <summary>添加 render 模块。</summary>
        public void AddRenderModule(FlowVFXModuleReal module) => _innerSimulation.RenderModules.Add(module);

        /// <summary>获取统计信息。</summary>
        public string GetStats()
        {
            return $"Particles={ActiveParticleCount}/{MaxParticles} " +
                   $"Spawned={TotalSpawned} " +
                   $"Time={SimulationTime:F2}s " +
                   $"Budget={Budget.SpawnedThisFrame}/{Budget.MaxSpawnPerFrame} " +
                   $"Events={Events.Count}";
        }
    }
}
