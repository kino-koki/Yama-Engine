// =============================================================================
//  Yama Engine - FlowVFXParticleSimulationReal.cs
//  yama引擎 - 真实 FlowVFX 粒子模拟器
//
//  本文件实现数据导向(DOD)的粒子模拟内核：
//    * FlowVFXParticleDataReal  - SoA (Structure of Arrays) 粒子数据布局
//    * FlowVFXParticleSimulationReal - 模拟器（发射/更新/淘汰/事件/排序）
//
//  SoA 布局优势：缓存友好（同属性连续内存）、SIMD 友好、GPU 友好。
//  所有方法均为真实算法实现，无 stub/TODO。
// =============================================================================
using System;
using System.Collections.Generic;
using System.Numerics;

namespace YamaEngine.Particles
{
    // ──────────────────────────────────────────────────────────────────────
    // 第一部分：SoA 粒子数据布局
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 粒子数据（Structure of Arrays 布局）。每个属性独占一段连续内存，
    /// 利于缓存预取与向量化。容量固定，通过活跃/空闲索引表管理分配。
    /// </summary>
    public sealed class FlowVFXParticleDataReal
    {
        /// <summary>最大容量。</summary>
        public int Capacity { get; }

        // ── 属性数组（SoA）──────────────────────────────────────
        public Vector3[] Position;
        public Vector3[] Velocity;
        public Vector4[] Color;        // RGBA 线性
        public Vector2[] Size;         // 宽高
        public float[] Life;           // 剩余生命
        public float[] MaxLife;        // 初始生命
        public float[] Rotation;       // 旋转角（弧度）
        public float[] RotationRate;   // 角速度
        public float[] Mass;
        public float[] DistanceTraveled;
        public float[] RandomSeed;     // 每粒子随机种子（用于确定性曲线采样）
        public Vector4[] CustomData;   // 自定义数据（子发射器 ID 等）
        public bool[] Alive;

        /// <summary>当前活跃粒子索引列表（紧凑，无空洞）。</summary>
        public readonly List<int> ActiveIndices = new();

        /// <summary>空闲槽位栈（LIFO 分配）。</summary>
        private readonly Stack<int> _freeSlots = new();

        public FlowVFXParticleDataReal(int capacity)
        {
            Capacity = Math.Max(1, capacity);
            Position = new Vector3[Capacity];
            Velocity = new Vector3[Capacity];
            Color = new Vector4[Capacity];
            Size = new Vector2[Capacity];
            Life = new float[Capacity];
            MaxLife = new float[Capacity];
            Rotation = new float[Capacity];
            RotationRate = new float[Capacity];
            Mass = new float[Capacity];
            DistanceTraveled = new float[Capacity];
            RandomSeed = new float[Capacity];
            CustomData = new Vector4[Capacity];
            Alive = new bool[Capacity];

            // 全部槽位初始为空闲（从大到小压栈，分配时从小到大使用）
            for (int i = Capacity - 1; i >= 0; i--) _freeSlots.Push(i);
        }

        /// <summary>当前活跃粒子数。</summary>
        public int ActiveCount => ActiveIndices.Count;

        /// <summary>分配一个粒子槽位。返回 -1 表示池满。</summary>
        public int Allocate()
        {
            if (_freeSlots.Count == 0) return -1;
            int idx = _freeSlots.Pop();
            Alive[idx] = true;
            ActiveIndices.Add(idx);
            return idx;
        }

        /// <summary>
        /// 初始化一个新粒子的默认值（在 Allocate 后调用，模块随后覆盖）。
        /// </summary>
        public void InitDefaults(int idx)
        {
            Position[idx] = Vector3.Zero;
            Velocity[idx] = Vector3.Zero;
            Color[idx] = Vector4.One;
            Size[idx] = Vector2.One;
            Life[idx] = 1f;
            MaxLife[idx] = 1f;
            Rotation[idx] = 0f;
            RotationRate[idx] = 0f;
            Mass[idx] = 1f;
            DistanceTraveled[idx] = 0f;
            RandomSeed[idx] = 0f;
            CustomData[idx] = Vector4.Zero;
        }

        /// <summary>
        /// 以 swap-remove 策略释放一个粒子：将末尾索引搬到被删位置，
        /// O(1) 删除，代价是不保序（粒子系统通常不要求保序）。
        /// </summary>
        public void Release(int idx)
        {
            Alive[idx] = false;
            _freeSlots.Push(idx);
            // 从活跃列表移除：找到 idx 的位置，用末尾元素替换
            int listPos = ActiveIndices.IndexOf(idx);
            if (listPos >= 0)
            {
                int last = ActiveIndices.Count - 1;
                if (listPos != last)
                    ActiveIndices[listPos] = ActiveIndices[last];
                ActiveIndices.RemoveAt(last);
            }
        }

        /// <summary>重置所有数据（清空活跃列表，回收所有槽位）。</summary>
        public void Reset()
        {
            for (int i = 0; i < Capacity; i++) Alive[i] = false;
            ActiveIndices.Clear();
            _freeSlots.Clear();
            for (int i = Capacity - 1; i >= 0; i--) _freeSlots.Push(i);
        }

        /// <summary>获取第 i 个活跃粒子的归一化生命 [0,1]（1=刚出生, 0=即将死亡）。</summary>
        public float GetNormalizedLife(int idx)
        {
            float max = MaxLife[idx];
            return max > 0f ? Life[idx] / max : 0f;
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    // 第二部分：粒子排序
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>粒子排序模式。</summary>
    public enum RealParticleSortMode
    {
        None,
        /// <summary>按到相机距离降序（远→近，便于半透明从后向前渲染）。</summary>
        DistanceToCameraDescending,
        /// <summary>按剩余生命升序。</summary>
        LifeAscending,
        /// <summary>按材质/渲染器分组。</summary>
        ByMaterial
    }

    /// <summary>
    /// 粒子排序器。使用快速排序对活跃索引按指定模式排序。
    /// </summary>
    public static class ParticleSorter
    {
        /// <summary>对活跃索引列表原地排序。</summary>
        public static void Sort(FlowVFXParticleDataReal data, RealParticleSortMode mode, Vector3 cameraPos)
        {
            if (mode == RealParticleSortMode.None || data.ActiveCount <= 1) return;
            int[] arr = data.ActiveIndices.ToArray();
            switch (mode)
            {
                case RealParticleSortMode.DistanceToCameraDescending:
                    Array.Sort(arr, (a, b) =>
                    {
                        float da = Vector3.DistanceSquared(data.Position[a], cameraPos);
                        float db = Vector3.DistanceSquared(data.Position[b], cameraPos);
                        // 远→近 = 距离大→小 = 降序
                        return db.CompareTo(da);
                    });
                    break;
                case RealParticleSortMode.LifeAscending:
                    Array.Sort(arr, (a, b) => data.Life[a].CompareTo(data.Life[b]));
                    break;
                case RealParticleSortMode.ByMaterial:
                    Array.Sort(arr, (a, b) =>
                        data.CustomData[a].X.CompareTo(data.CustomData[b].X));
                    break;
            }
            data.ActiveIndices.Clear();
            data.ActiveIndices.AddRange(arr);
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    // 第三部分：粒子事件
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>粒子事件类型。</summary>
    public enum RealParticleEventType
    {
        Death,
        Collision,
        Spawn
    }

    /// <summary>粒子事件描述。</summary>
    public struct ParticleEventReal
    {
        public RealParticleEventType Type;
        public int ParticleIndex;
        public Vector3 Position;
        public Vector3 Velocity;
        public Vector3 CollisionNormal;
        public float LifeAtEvent;
    }

    /// <summary>事件收集队列（模拟器填充，外部消费）。</summary>

    // ──────────────────────────────────────────────────────────────────────
    // 第四部分：模拟器
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 真实粒子模拟器。完整模拟一步：
    ///   1. 发射新粒子（调用 Emitter）
    ///   2. 更新存活粒子（执行模块堆栈）
    ///   3. 淘汰死亡粒子（Life&lt;=0 → swap-remove）
    ///   4. 执行事件（死亡/碰撞/生成事件）
    /// </summary>
    public sealed class FlowVFXParticleSimulationReal
    {
        /// <summary>粒子数据（SoA）。</summary>
        public FlowVFXParticleDataReal Data { get; private set; }

        /// <summary>最大粒子容量（可在对象初始化器/运行期配置并重建 SoA 缓冲）。</summary>
        public int MaxParticles
        {
            get => Data.Capacity;
            set
            {
                if (value > 0 && (Data == null || value != Data.Capacity))
                    Data = new FlowVFXParticleDataReal(value);
            }
        }

        /// <summary>发射器。</summary>
        public FlowVFXEmitterReal? Emitter { get; set; }

        /// <summary>初始化模块堆栈（新生粒子按顺序执行）。</summary>
        public List<FlowVFXModuleReal> SpawnModules { get; } = new();

        /// <summary>更新模块堆栈（每帧对存活粒子执行）。</summary>
        public List<FlowVFXModuleReal> UpdateModules { get; } = new();

        /// <summary>渲染模块（输出绘制数据）。</summary>
        public List<FlowVFXModuleReal> RenderModules { get; } = new();

        /// <summary>事件队列。</summary>
        public ParticleEventQueue Events { get; } = new();

        /// <summary>排序模式（渲染前应用）。</summary>
        public RealParticleSortMode SortMode = RealParticleSortMode.DistanceToCameraDescending;

        /// <summary>渲染排序用的相机位置。</summary>
        public Vector3 CameraPosition = -Vector3.UnitZ * 10f;

        /// <summary>已发射粒子总数（统计）。</summary>
        public long TotalSpawned { get; private set; }

        /// <summary>累计模拟时间（秒）。</summary>
        public float SimulationTime { get; private set; }

        /// <summary>是否固定步长子步模拟。</summary>
        public int MaxSubSteps = 4;
        public float FixedDt = 1f / 60f;

        private readonly RealRng _spawnRng = new(0xABCDEF01);

        public FlowVFXParticleSimulationReal() : this(1024) { }

        public FlowVFXParticleSimulationReal(int maxParticles)
        {
            Data = new FlowVFXParticleDataReal(maxParticles);
        }

        /// <summary>重置模拟器。</summary>
        public void Reset()
        {
            Data.Reset();
            Events.Clear();
            TotalSpawned = 0;
            SimulationTime = 0f;
            Emitter?.Reset();
        }

        /// <summary>
        /// 完整模拟一步。内部对子步做循环以保证大 dt 下数值稳定。
        /// </summary>
        public void Simulate(float dt)
        {
            if (dt <= 0f) return;
            SimulationTime += dt;

            // 子步切分：当 dt 过大时拆分为固定步长，避免穿透与不稳定
            float remaining = dt;
            int steps = 0;
            while (remaining > 0f && steps < MaxSubSteps)
            {
                float stepDt = Math.Min(FixedDt, remaining);
                SimulateSingleStep(stepDt);
                remaining -= stepDt;
                steps++;
            }
            if (remaining > 0f)
            {
                // 超过子步上限，合并剩余
                SimulateSingleStep(remaining);
            }

            // 渲染前排序
            ParticleSorter.Sort(Data, SortMode, CameraPosition);
        }

        /// <summary>单步模拟：发射→更新→淘汰→事件。</summary>
        private void SimulateSingleStep(float dt)
        {
            // ── 1. 发射新粒子 ────────────────────────────────────────
            int spawnedThisFrame = 0;
            if (Emitter != null && Emitter.Tick(dt, out SpawnRequest req))
            {
                spawnedThisFrame = SpawnParticles(req.Count, req.LoopNormalized);
            }
            else if (Emitter != null)
            {
                // 发射器已结束，但需继续更新已存活粒子
            }

            // ── 2. 更新存活粒子（执行更新模块堆栈）──────────────────
            int activeCount = Data.ActiveCount;
            if (activeCount > 0)
            {
                // 位置积分（基础欧拉）：x += v*dt，并累加行驶距离
                IntegratePositions(dt);

                // 执行更新模块（力/阻力/碰撞/颜色/生命等）
                for (int m = 0; m < UpdateModules.Count; m++)
                {
                    UpdateModules[m].Execute(Data, 0, activeCount, dt);
                }
            }

            // ── 3. 淘汰死亡粒子（Life<=0）────────────────────────────
            CullDeadParticles();

            // ── 4. 触发生成事件（本帧新生粒子）──────────────────────
            if (spawnedThisFrame > 0)
            {
                int start = Data.ActiveCount - spawnedThisFrame;
                if (start < 0) start = 0;
                for (int i = start; i < Data.ActiveCount; i++)
                {
                    int idx = Data.ActiveIndices[i];
                    Events.Enqueue(new ParticleEventReal
                    {
                        Type = RealParticleEventType.Spawn,
                        ParticleIndex = idx,
                        Position = Data.Position[idx],
                        Velocity = Data.Velocity[idx],
                        LifeAtEvent = Data.Life[idx]
                    });
                }
            }

            // 执行渲染模块（生成绘制数据）
            for (int m = 0; m < RenderModules.Count; m++)
            {
                RenderModules[m].Execute(Data, 0, Data.ActiveCount, dt);
            }
        }

        /// <summary>
        /// 发射 N 个新粒子。为每个粒子分配槽位、初始化默认值、运行 spawn 模块堆栈。
        /// </summary>
        /// <returns>实际成功发射的数量。</returns>
        private int SpawnParticles(int count, float loopNormalized)
        {
            if (count <= 0 || Emitter == null) return 0;
            int spawned = 0;
            for (int i = 0; i < count; i++)
            {
                int idx = Data.Allocate();
                if (idx < 0) break; // 池满
                Data.InitDefaults(idx);
                Data.RandomSeed[idx] = Emitter.SampleRandom();

                // 采样发射位置与方向
                Vector3 pos = Emitter.SampleSpawnPosition();
                Vector3 dir = Emitter.SampleSpawnDirection(pos);
                Data.Position[idx] = pos;
                // 速度方向作为模块输入（InitializeVelocityModule 会乘以速率）
                Data.Velocity[idx] = dir;

                // 执行初始化模块堆栈
                for (int m = 0; m < SpawnModules.Count; m++)
                {
                    SpawnModules[m].Execute(Data, idx, 1, dt: 0f);
                }

                TotalSpawned++;
                spawned++;
            }
            return spawned;
        }

        /// <summary>位置积分：x += v*dt；记录行驶距离。</summary>
        private void IntegratePositions(float dt)
        {
            for (int i = 0; i < Data.ActiveCount; i++)
            {
                int idx = Data.ActiveIndices[i];
                Vector3 v = Data.Velocity[idx];
                Vector3 delta = v * dt;
                Data.Position[idx] += delta;
                Data.DistanceTraveled[idx] += delta.Length();
            }
        }

        /// <summary>淘汰死亡粒子（Life&lt;=0），使用 swap-remove，并触发死亡事件。</summary>
        private void CullDeadParticles()
        {
            // 从后向前遍历，便于 swap-remove
            for (int i = Data.ActiveCount - 1; i >= 0; i--)
            {
                int idx = Data.ActiveIndices[i];
                if (Data.Life[idx] <= 0f)
                {
                    // 触发死亡事件
                    Events.Enqueue(new ParticleEventReal
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
        }

        /// <summary>报告碰撞事件（供碰撞模块调用）。</summary>
        public void ReportCollision(int particleIndex, Vector3 normal)
        {
            if (particleIndex < 0 || particleIndex >= Data.Capacity || !Data.Alive[particleIndex]) return;
            Events.Enqueue(new ParticleEventReal
            {
                Type = RealParticleEventType.Collision,
                ParticleIndex = particleIndex,
                Position = Data.Position[particleIndex],
                Velocity = Data.Velocity[particleIndex],
                CollisionNormal = normal,
                LifeAtEvent = Data.Life[particleIndex]
            });
        }

        /// <summary>获取统计信息字符串。</summary>
        public string GetStats()
        {
            return $"Active={Data.ActiveCount}/{Data.Capacity} TotalSpawned={TotalSpawned} Time={SimulationTime:F2}s Events={Events.Count}";
        }
    }
}
