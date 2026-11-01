// ============================================================================
//  YamaEngine.AI.MassCrowdSystem.cs
//  yama引擎 —— Mass Crowd 群体行为系统
//  对齐 Unreal Engine 5 的 Mass Entity + Mass Crowd + ZoneGraph Crowd 框架
// ----------------------------------------------------------------------------
//  本文件专注行为模拟层: 大规模人群的移动 / 避障 / 编队 / 区域聚集 / 生命周期。
//  与 Rendering.CrowdRenderingSystem 解耦, 通过 MassCrowdComponent 联动主 ECS。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Numerics;
using YamaEngine.ECS;
using YamaEngine.Physics;

namespace YamaEngine.AI
{
    // ==========================================================================
    //  一、Mass Entity 数据层（SOA）
    // ==========================================================================

    /// <summary>
    /// 群体代理运行时状态。对齐 UE5 MassAgent 的生命周期状态机：
    /// Active 正常模拟；Inactive 暂停（如远 LOD 冻结）；Dying 即将销毁（播放死亡后回收）。
    /// </summary>
    public enum MassAgentState : byte
    {
        /// <summary>未分配 / 已回收的空槽位。</summary>
        None = 0,
        /// <summary>活跃：参与全部行为与避障模拟。</summary>
        Active = 1,
        /// <summary>非活跃：冻结更新（远距离或被显式暂停），保留位姿。</summary>
        Inactive = 2,
        /// <summary>濒死：本帧或下帧将被 Despawn，可在此播放退场。</summary>
        Dying = 3
    }

    /// <summary>
    /// 群体行为类型。对齐 UE5 MassCrowd 的 StateTree 行为片段划分。
    /// </summary>
    public enum MassBehaviorType : byte
    {
        /// <summary>无行为（静止）。</summary>
        Idle = 0,
        /// <summary>巡逻：沿固定路径点循环往返。</summary>
        Patrol = 1,
        /// <summary>游荡：随机方向漫游。</summary>
        Wander = 2,
        /// <summary>逃跑：远离威胁点。</summary>
        Flee = 3,
        /// <summary>寻路：调用 NavigationSystem.FindPath 朝目标移动。</summary>
        Seek = 4,
        /// <summary>排队：前方有人则减速停止，保持间距。</summary>
        Queue = 5,
        /// <summary>编队：保持相对队长的队形偏移位置。</summary>
        Formation = 6
    }

    /// <summary>
    /// 细节层级。对齐 UE5 MassLOD 的近 / 中 / 远三级，影响每帧更新频率。
    /// </summary>
    public enum MassAgentLod : byte
    {
        /// <summary>近景：每帧全更新（行为 + 避障 + 动画）。</summary>
        Near = 0,
        /// <summary>中景：降频更新（每 N 帧一次），避障简化。</summary>
        Mid = 1,
        /// <summary>远景：冻结或仅位置积分，不执行避障。</summary>
        Far = 2
    }

    /// <summary>
    /// 编队形态。对齐 UE5 MassFormation 的常见队形枚举。
    /// </summary>
    public enum MassFormationType : byte
    {
        /// <summary>无编队。</summary>
        None = 0,
        /// <summary>圆形：成员环绕队长分布。</summary>
        Circle = 1,
        /// <summary>线形：成员在队长一侧排成横线。</summary>
        Line = 2,
        /// <summary>楔形：成员在队长身后呈三角推进。</summary>
        Wedge = 3,
        /// <summary>纵列：成员在队长身后排成纵队。</summary>
        Column = 4,
        /// <summary>方阵：成员排成密集网格。</summary>
        Grid = 5
    }

    /// <summary>
    /// Mass 代理视图（SOA 条目视图）。
    /// 本身是轻量 struct，持有所属 <see cref="MassAgentChunk"/> 引用与槽位索引，
    /// 通过属性读写底层 SOA 数组。对齐 UE5 中 FMassAgentHandle / FMassEntityView 的“句柄”语义：
    /// 数据连续存储于 Chunk 的并行数组中，代理句柄仅是 (Chunk, Slot) 二元组。
    /// 注意：因为持有的是 class 引用，即便按值传递该 struct，属性写操作仍会穿透到底层数组。
    /// </summary>
    public readonly struct MassAgent
    {
        /// <summary>所属代理块（不可为 null 时有效）。</summary>
        public readonly MassAgentChunk Chunk;
        /// <summary>块内槽位索引。</summary>
        public readonly int Slot;

        /// <summary>构造代理视图。</summary>
        public MassAgent(MassAgentChunk chunk, int slot)
        {
            Chunk = chunk;
            Slot = slot;
        }

        /// <summary>是否指向有效数据。</summary>
        public bool IsValid => Chunk != null && Slot >= 0 && Slot < MassAgentChunk.ChunkCapacity;

        // ── 热数据（每帧读写，SOA 连续） ──

        /// <summary>世界坐标位置。</summary>
        public Vector3 Position { get => Chunk.Positions[Slot]; set => Chunk.Positions[Slot] = value; }
        /// <summary>当前速度（米/秒）。</summary>
        public Vector3 Velocity { get => Chunk.Velocities[Slot]; set => Chunk.Velocities[Slot] = value; }
        /// <summary>朝向（归一化）。</summary>
        public Vector3 Forward { get => Chunk.Forwards[Slot]; set => Chunk.Forwards[Slot] = value; }
        /// <summary>本次行为计算出的期望速度（避障前的偏好速度）。</summary>
        public Vector3 DesiredVelocity { get => Chunk.DesiredVelocities[Slot]; set => Chunk.DesiredVelocities[Slot] = value; }
        /// <summary>行为目标位置（寻路 / 编队落脚点）。</summary>
        public Vector3 TargetPosition { get => Chunk.TargetPositions[Slot]; set => Chunk.TargetPositions[Slot] = value; }
        /// <summary>逃跑行为的威胁点。</summary>
        public Vector3 ThreatPosition { get => Chunk.ThreatPositions[Slot]; set => Chunk.ThreatPositions[Slot] = value; }
        /// <summary>代理半径（碰撞 / 避障用）。</summary>
        public float Radius { get => Chunk.Radii[Slot]; set => Chunk.Radii[Slot] = value; }
        /// <summary>最大速度。</summary>
        public float MaxSpeed { get => Chunk.MaxSpeeds[Slot]; set => Chunk.MaxSpeeds[Slot] = value; }

        // ── 冷 / 状态数据 ──

        /// <summary>运行时状态。</summary>
        public MassAgentState State { get => Chunk.States[Slot]; set => Chunk.States[Slot] = value; }
        /// <summary>行为类型。</summary>
        public MassBehaviorType Behavior { get => Chunk.BehaviorTypes[Slot]; set => Chunk.BehaviorTypes[Slot] = value; }
        /// <summary>细节层级。</summary>
        public MassAgentLod Lod { get => Chunk.Lods[Slot]; set => Chunk.Lods[Slot] = value; }
        /// <summary>所属群组 ID（0 = 无群组）。</summary>
        public int GroupId { get => Chunk.GroupIds[Slot]; set => Chunk.GroupIds[Slot] = value; }
        /// <summary>当前所在 ZoneGraph 车道 ID（0 = 未绑定）。</summary>
        public int CurrentLaneId { get => Chunk.CurrentLaneIds[Slot]; set => Chunk.CurrentLaneIds[Slot] = value; }
        /// <summary>沿路径的当前路径点索引（Seek / Patrol）。</summary>
        public int PathIndex { get => Chunk.PathIndices[Slot]; set => Chunk.PathIndices[Slot] = value; }
        /// <summary>游荡当前角度（弧度，XZ 平面）。</summary>
        public float WanderAngle { get => Chunk.WanderAngles[Slot]; set => Chunk.WanderAngles[Slot] = value; }
        /// <summary>LOD 降频累积计时器。</summary>
        public float UpdateTimer { get => Chunk.UpdateTimers[Slot]; set => Chunk.UpdateTimers[Slot] = value; }
        /// <summary>槽位是否活跃。</summary>
        public bool IsActive => Chunk.Active[Slot];
    }

    /// <summary>
    /// 代理块（SOA 分块）。对齐 UE5 FMassArchetypeData / FChunkFixedArray 的“按块连续存储”：
    /// 每块固定容量 256，所有同字段的数据位于同一数组，遍历时顺序访问，命中率极高。
    /// 热数据（位置 / 速度 / 朝向）与状态数据分列并行数组。
    /// </summary>
    public sealed class MassAgentChunk
    {
        /// <summary>每块容量（2 的幂，便于位运算与对齐）。</summary>
        public const int ChunkCapacity = 256;

        // ── SOA 并行数组 ──
        /// <summary>位置数组。</summary>
        public readonly Vector3[] Positions = new Vector3[ChunkCapacity];
        /// <summary>速度数组。</summary>
        public readonly Vector3[] Velocities = new Vector3[ChunkCapacity];
        /// <summary>朝向数组。</summary>
        public readonly Vector3[] Forwards = new Vector3[ChunkCapacity];
        /// <summary>期望速度数组（避障输入）。</summary>
        public readonly Vector3[] DesiredVelocities = new Vector3[ChunkCapacity];
        /// <summary>目标位置数组。</summary>
        public readonly Vector3[] TargetPositions = new Vector3[ChunkCapacity];
        /// <summary>威胁位置数组（Flee）。</summary>
        public readonly Vector3[] ThreatPositions = new Vector3[ChunkCapacity];

        /// <summary>半径数组。</summary>
        public readonly float[] Radii = new float[ChunkCapacity];
        /// <summary>最大速度数组。</summary>
        public readonly float[] MaxSpeeds = new float[ChunkCapacity];
        /// <summary>游荡角度数组。</summary>
        public readonly float[] WanderAngles = new float[ChunkCapacity];
        /// <summary>LOD 计时器数组。</summary>
        public readonly float[] UpdateTimers = new float[ChunkCapacity];

        /// <summary>状态数组。</summary>
        public readonly MassAgentState[] States = new MassAgentState[ChunkCapacity];
        /// <summary>行为类型数组。</summary>
        public readonly MassBehaviorType[] BehaviorTypes = new MassBehaviorType[ChunkCapacity];
        /// <summary>LOD 数组。</summary>
        public readonly MassAgentLod[] Lods = new MassAgentLod[ChunkCapacity];
        /// <summary>群组 ID 数组。</summary>
        public readonly int[] GroupIds = new int[ChunkCapacity];
        /// <summary>车道 ID 数组。</summary>
        public readonly int[] CurrentLaneIds = new int[ChunkCapacity];
        /// <summary>路径点索引数组。</summary>
        public readonly int[] PathIndices = new int[ChunkCapacity];

        /// <summary>槽位活跃标记。</summary>
        public readonly bool[] Active = new bool[ChunkCapacity];

        /// <summary>块内活跃代理数。</summary>
        public int ActiveCount { get; private set; }

        /// <summary>本块所属的块索引（由管理器分配）。</summary>
        public int ChunkIndex { get; set; } = -1;

        /// <summary>分配一个空闲槽位。返回槽位索引，-1 表示块已满。</summary>
        public int Add()
        {
            for (int i = 0; i < ChunkCapacity; i++)
            {
                if (!Active[i])
                {
                    Active[i] = true;
                    ActiveCount++;
                    return i;
                }
            }
            return -1;
        }

        /// <summary>释放槽位（标记为不活跃，数据保留以便复用）。</summary>
        public void Remove(int slot)
        {
            if (slot < 0 || slot >= ChunkCapacity) return;
            if (Active[slot])
            {
                Active[slot] = false;
                ActiveCount--;
                States[slot] = MassAgentState.None;
            }
        }

        /// <summary>获取指定槽位的代理视图。</summary>
        public MassAgent Get(int slot) => new MassAgent(this, slot);

        /// <summary>是否还有空闲槽位。</summary>
        public bool HasFreeSlot => ActiveCount < ChunkCapacity;
    }

    /// <summary>
    /// 群体实体管理器。对齐 UE5 FMassEntityManager：
    ///   - 维护代理块列表与“代理ID -> (块索引, 槽位)”映射；
    ///   - Allocate / Free 管理生命周期；
    ///   - ForEachChunk 提供按块批量遍历（行为处理器在此回调中处理整块）。
    /// 代理 ID 从 1 起，0 视为无效（与主 ECS 实体 ID 约定一致）。
    /// 路径等“每代理稀有引用数据”以 AoS 字典附带存储，避免破坏 SOA 连续性。
    /// </summary>
    public sealed class MassEntityManager
    {
        private readonly List<MassAgentChunk> _chunks = new();
        private readonly Dictionary<int, (int chunkIdx, int slot)> _locations = new();

        // AoS 附带数据（冷数据 / 引用类型，避免污染 SOA）
        private readonly Dictionary<int, List<Vector3>> _paths = new();       // Seek 路径
        private readonly Dictionary<int, List<Vector3>> _patrolLoops = new(); // Patrol 循环路径
        // 反向索引：(块索引, 槽位) -> 代理 ID。保持 SOA 纯净（代理自身不存 ID）。
        private readonly Dictionary<long, int> _reverseLocations = new();
        private int _nextAgentId = 1;

        /// <summary>当前活跃代理数。</summary>
        public int ActiveCount { get; private set; }

        /// <summary>块总数。</summary>
        public int ChunkCount => _chunks.Count;

        /// <summary>所有块（只读视图，供行为处理器遍历）。</summary>
        public IReadOnlyList<MassAgentChunk> Chunks => _chunks;

        /// <summary>最大可容纳代理数（块数 * 容量）。</summary>
        public int Capacity => _chunks.Count * MassAgentChunk.ChunkCapacity;

        /// <summary>预分配若干空块以满足至少 count 个代理容量。</summary>
        public void Reserve(int count)
        {
            while (Capacity < count)
            {
                var chunk = new MassAgentChunk { ChunkIndex = _chunks.Count };
                _chunks.Add(chunk);
            }
        }

        /// <summary>
        /// 分配一个新代理并初始化默认值。返回代理 ID（>0）。
        /// 对齐 UE5 FMassEntityManager::BatchCreateEntities 的单条目版本。
        /// </summary>
        public int Allocate(Vector3 position, float radius = 0.4f, float maxSpeed = 1.4f)
        {
            // 寻找有空闲槽位的块
            MassAgentChunk? target = null;
            int targetIdx = -1;
            for (int i = 0; i < _chunks.Count; i++)
            {
                if (_chunks[i].HasFreeSlot)
                {
                    target = _chunks[i];
                    targetIdx = i;
                    break;
                }
            }
            if (target == null)
            {
                target = new MassAgentChunk { ChunkIndex = _chunks.Count };
                targetIdx = _chunks.Count;
                _chunks.Add(target);
            }

            int slot = target.Add();
            if (slot < 0) return 0; // 理论上不会发生

            int id = _nextAgentId++;
            _locations[id] = (targetIdx, slot);
            _reverseLocations[LocationKey(targetIdx, slot)] = id;

            // 初始化默认值
            target.Positions[slot] = position;
            target.Velocities[slot] = Vector3.Zero;
            target.Forwards[slot] = new Vector3(0, 0, -1);
            target.DesiredVelocities[slot] = Vector3.Zero;
            target.TargetPositions[slot] = position;
            target.ThreatPositions[slot] = position;
            target.Radii[slot] = radius;
            target.MaxSpeeds[slot] = maxSpeed;
            target.WanderAngles[slot] = RandomShared.NextSingle() * MathF.PI * 2f;
            target.UpdateTimers[slot] = 0f;
            target.States[slot] = MassAgentState.Active;
            target.BehaviorTypes[slot] = MassBehaviorType.Idle;
            target.Lods[slot] = MassAgentLod.Near;
            target.GroupIds[slot] = 0;
            target.CurrentLaneIds[slot] = 0;
            target.PathIndices[slot] = 0;

            ActiveCount++;
            return id;
        }

        /// <summary>批量分配 count 个代理于指定中心附近。返回代理 ID 列表。</summary>
        public List<int> Allocate(int count, Vector3 center, float spread = 4f)
        {
            var ids = new List<int>(count);
            for (int i = 0; i < count; i++)
            {
                float a = RandomShared.NextSingle() * MathF.PI * 2f;
                float r = RandomShared.NextSingle() * spread;
                var pos = center + new Vector3(MathF.Cos(a) * r, 0, MathF.Sin(a) * r);
                int id = Allocate(pos);
                if (id > 0) ids.Add(id);
            }
            return ids;
        }

        /// <summary>释放代理，回收槽位与附带数据。</summary>
        public void Free(int agentId)
        {
            if (!_locations.TryGetValue(agentId, out var loc)) return;
            var chunk = _chunks[loc.chunkIdx];
            chunk.Remove(loc.slot);
            _locations.Remove(agentId);
            _reverseLocations.Remove(LocationKey(loc.chunkIdx, loc.slot));
            _paths.Remove(agentId);
            _patrolLoops.Remove(agentId);
            if (ActiveCount > 0) ActiveCount--;
        }

        /// <summary>代理 ID 是否存在。</summary>
        public bool HasAgent(int agentId) => _locations.ContainsKey(agentId);

        /// <summary>获取代理视图。不存在返回无效视图。</summary>
        public MassAgent GetAgent(int agentId)
        {
            if (_locations.TryGetValue(agentId, out var loc))
                return _chunks[loc.chunkIdx].Get(loc.slot);
            return default;
        }

        /// <summary>
        /// 将源代理的 SOA 字段拷贝到目标代理（用于 swap-remove 等场景）。
        /// 不拷贝附带路径数据。
        /// </summary>
        public void SetAgent(int destId, in MassAgent src)
        {
            if (!_locations.TryGetValue(destId, out var loc)) return;
            var c = _chunks[loc.chunkIdx];
            int s = loc.slot;
            c.Positions[s] = src.Position;
            c.Velocities[s] = src.Velocity;
            c.Forwards[s] = src.Forward;
            c.DesiredVelocities[s] = src.DesiredVelocity;
            c.TargetPositions[s] = src.TargetPosition;
            c.ThreatPositions[s] = src.ThreatPosition;
            c.Radii[s] = src.Radius;
            c.MaxSpeeds[s] = src.MaxSpeed;
            c.WanderAngles[s] = src.WanderAngle;
            c.UpdateTimers[s] = src.UpdateTimer;
            c.States[s] = src.State;
            c.BehaviorTypes[s] = src.Behavior;
            c.Lods[s] = src.Lod;
            c.GroupIds[s] = src.GroupId;
            c.CurrentLaneIds[s] = src.CurrentLaneId;
            c.PathIndices[s] = src.PathIndex;
        }

        /// <summary>获取 / 设置 Seek 路径。</summary>
        public List<Vector3>? GetPath(int agentId) => _paths.TryGetValue(agentId, out var p) ? p : null;
        /// <summary>设置 Seek 路径并重置路径点索引。</summary>
        public void SetPath(int agentId, List<Vector3>? path)
        {
            if (path == null) { _paths.Remove(agentId); return; }
            _paths[agentId] = path;
            if (_locations.TryGetValue(agentId, out var loc))
                _chunks[loc.chunkIdx].PathIndices[loc.slot] = 0;
        }

        /// <summary>获取 / 设置 Patrol 循环路径。</summary>
        public List<Vector3>? GetPatrolLoop(int agentId) =>
            _patrolLoops.TryGetValue(agentId, out var p) ? p : null;
        /// <summary>设置 Patrol 循环路径。</summary>
        public void SetPatrolLoop(int agentId, List<Vector3> loop) => _patrolLoops[agentId] = loop;

        /// <summary>
        /// 按块批量遍历。对齐 UE5 MassProcessor 的“逐 Archetype 块处理”：
        /// 行为处理器在回调中对整块代理进行顺序处理。
        /// </summary>
        public void ForEachChunk(Action<MassAgentChunk> callback)
        {
            foreach (var chunk in _chunks)
            {
                if (chunk.ActiveCount > 0) callback(chunk);
            }
        }

        /// <summary>清空所有代理与块。</summary>
        public void Clear()
        {
            _chunks.Clear();
            _locations.Clear();
            _reverseLocations.Clear();
            _paths.Clear();
            _patrolLoops.Clear();
            ActiveCount = 0;
        }

        /// <summary>
        /// 按 (块, 槽) 反查代理 ID。用于行为 / 避障阶段需要代理 ID（查路径等附带数据）的场景。
        /// 返回 0 表示未找到。
        /// </summary>
        public int GetAgentIdByLocation(MassAgentChunk chunk, int slot)
        {
            if (chunk == null || chunk.ChunkIndex < 0) return 0;
            return _reverseLocations.TryGetValue(LocationKey(chunk.ChunkIndex, slot), out var id) ? id : 0;
        }

        /// <summary>(块索引, 槽位) 压缩为 long 键（块索引高 16 位，槽位低 16 位）。</summary>
        private static long LocationKey(int chunkIdx, int slot) => ((long)chunkIdx << 16) | (uint)slot;
    }

    // ==========================================================================
    //  二、群体行为（Behavior Processors）
    // ==========================================================================

    /// <summary>
    /// 行为处理上下文。承载行为处理器所需的各类引用，避免在每个代理上重复传参。
    /// 对齐 UE5 FMassEntityQueryExecutionContext 的“上下文注入”思路。
    /// </summary>
    public sealed class MassBehaviorContext
    {
        /// <summary>群体实体管理器。</summary>
        public MassEntityManager Manager = null!;
        /// <summary>空间哈希网格（邻居查询）。</summary>
        public SpatialHashGrid SpatialHash = null!;
        /// <summary>导航系统（寻路主源）。</summary>
        public NavigationSystem? Navigation;
        /// <summary>ZoneGraph 系统（可选，车道流动）。</summary>
        public ZoneGraphSystem? ZoneGraph;
        /// <summary>全局配置。</summary>
        public MassCrowdConfig Config = null!;
        /// <summary>LOD 焦点（玩家 / 摄像机位置）。</summary>
        public Vector3 FocalPoint;
        /// <summary>当前帧时间步长。</summary>
        public float Dt;
    }

    /// <summary>
    /// 行为基类。对齐 UE5 MassProcessor：每个处理器负责一种行为，
    /// 在 <see cref="ProcessChunk"/> 中遍历整块代理，仅处理 BehaviorType 匹配的条目。
    /// 处理器写入代理的 DesiredVelocity（避障前的偏好速度），由后续 RVO 阶段修正。
    /// </summary>
    public abstract class MassBehaviorBase
    {
        /// <summary>本处理器负责的行为类型。</summary>
        public abstract MassBehaviorType BehaviorType { get; }

        /// <summary>处理一个块的所有匹配代理。</summary>
        public abstract void ProcessChunk(MassBehaviorContext ctx, MassAgentChunk chunk, float dt);

        /// <summary>工具：将朝向转向目标方向（带最大转向角限制），保持 Y 分量归零。</summary>
        protected static Vector3 SteerTowards(Vector3 currentForward, Vector3 desiredDir, float maxTurnRate, float dt)
            => SteerTowardsStatic(currentForward, desiredDir, maxTurnRate, dt);

        /// <summary>
        /// 静态转向工具（供系统积分阶段与非行为子类复用）。将朝向转向目标方向
        /// （带最大转向角限制），保持 Y 分量归零。
        /// </summary>
        public static Vector3 SteerTowardsStatic(Vector3 currentForward, Vector3 desiredDir, float maxTurnRate, float dt)
        {
            float len = desiredDir.Length();
            if (len < 1e-5f) return currentForward;
            desiredDir /= len;
            // 仅在 XZ 平面转向
            desiredDir = new Vector3(desiredDir.X, 0, desiredDir.Z);
            float dl = desiredDir.Length();
            if (dl < 1e-5f) return currentForward;
            desiredDir /= dl;

            float dot = Math.Clamp(Vector3.Dot(currentForward, desiredDir), -1f, 1f);
            float angle = MathF.Acos(dot);
            float maxAngle = maxTurnRate * dt;
            if (angle <= maxAngle) return desiredDir;
            float t = maxAngle / angle;
            return Vector3.Normalize(Vector3.Lerp(currentForward, desiredDir, t));
        }
    }

    /// <summary>
    /// 寻路行为：调用 <see cref="NavigationSystem.FindPath"/> 获取路径点，沿路径移动。
    /// 对齐 UE5 MassMoveTo / MassNavigation 寻路片段。若导航不可用则直线趋向目标。
    /// </summary>
    public sealed class MassSeekBehavior : MassBehaviorBase
    {
        public override MassBehaviorType BehaviorType => MassBehaviorType.Seek;

        public override void ProcessChunk(MassBehaviorContext ctx, MassAgentChunk chunk, float dt)
        {
            for (int i = 0; i < MassAgentChunk.ChunkCapacity; i++)
            {
                if (!chunk.Active[i]) continue;
                if (chunk.BehaviorTypes[i] != BehaviorType) continue;
                if (chunk.States[i] != MassAgentState.Active) continue;

                var agent = chunk.Get(i);
                Vector3 pos = agent.Position;
                Vector3 target = agent.TargetPosition;
                float maxSpeed = agent.MaxSpeed;

                // 取 / 续算路径
                var path = ctx.Manager.GetPath(GetId(ctx, agent));
                float distToTarget = Vector3.Distance(pos, target);

                if (path == null || path.Count == 0 || distToTarget < 0.3f && path.Count > 0 && ReachedEnd(agent))
                {
                    // 重新规划
                    if (ctx.Navigation != null && ctx.Navigation.IsBuilt)
                    {
                        var newPath = ctx.Navigation.FindPath(pos, target);
                        ctx.Manager.SetPath(GetId(ctx, agent), newPath);
                        path = newPath;
                    }
                    else
                    {
                        // 无导航：直线
                        path = null;
                    }
                }

                Vector3 desiredDir;
                if (path != null && path.Count > 0)
                {
                    int idx = agent.PathIndex;
                    if (idx >= path.Count) idx = path.Count - 1;
                    Vector3 wp = path[idx];
                    if (Vector3.DistanceSquared(pos, wp) < 0.5f * 0.5f)
                    {
                        idx = Math.Min(idx + 1, path.Count - 1);
                        agent.PathIndex = idx;
                        wp = path[idx];
                    }
                    desiredDir = wp - pos;
                }
                else
                {
                    desiredDir = target - pos;
                }

                agent.Forward = SteerTowards(agent.Forward, desiredDir, ctx.Config.MaxTurnRate, dt);
                Vector3 fwd = agent.Forward;
                // 接近目标减速
                float speed = distToTarget < 1f ? maxSpeed * (distToTarget / 1f) : maxSpeed;
                agent.DesiredVelocity = fwd * Math.Max(0.1f, speed);
            }
        }

        private static bool ReachedEnd(MassAgent agent) => agent.PathIndex >= 255;
        private static int GetId(MassBehaviorContext ctx, MassAgent agent) =>
            ctx.Manager.GetAgentIdByLocation(agent.Chunk, agent.Slot);
    }

    /// <summary>
    /// 游荡行为：周期性改变方向，在区域内随机漫游。对齐 UE5 MassWander 片段。
    /// </summary>

    /// <summary>
    /// 逃跑行为：远离 ThreatPosition 方向移动，超出安全距离后降速。对齐 UE5 MassFlee 片段。
    /// </summary>
    public sealed class FleeBehavior : MassBehaviorBase
    {
        public override MassBehaviorType BehaviorType => MassBehaviorType.Flee;

        public override void ProcessChunk(MassBehaviorContext ctx, MassAgentChunk chunk, float dt)
        {
            float safeDist = ctx.Config.FleeSafeDistance;
            for (int i = 0; i < MassAgentChunk.ChunkCapacity; i++)
            {
                if (!chunk.Active[i]) continue;
                if (chunk.BehaviorTypes[i] != BehaviorType) continue;
                if (chunk.States[i] != MassAgentState.Active) continue;

                Vector3 pos = chunk.Positions[i];
                Vector3 threat = chunk.ThreatPositions[i];
                Vector3 away = pos - threat;
                away = new Vector3(away.X, 0, away.Z);
                float dist = away.Length();
                if (dist < 1e-4f)
                {
                    away = chunk.Forwards[i];
                    dist = 1f;
                }
                Vector3 dir = away / dist;

                chunk.Forwards[i] = SteerTowards(chunk.Forwards[i], dir, ctx.Config.MaxTurnRate, dt);
                // 越近威胁跑越快，远离后减速
                float factor = dist > safeDist ? 0.2f : (1f - dist / safeDist);
                factor = Math.Clamp(factor, 0.1f, 1f);
                chunk.DesiredVelocities[i] = chunk.Forwards[i] * (chunk.MaxSpeeds[i] * factor);
            }
        }
    }

    /// <summary>
    /// 游荡行为：随机改变朝向，漫无目的地移动。对齐 UE5 Mass 的呆滞/漫游片段。
    /// </summary>
    public sealed class MassWanderBehavior : MassBehaviorBase
    {
        public override MassBehaviorType BehaviorType => MassBehaviorType.Wander;

        public override void ProcessChunk(MassBehaviorContext ctx, MassAgentChunk chunk, float dt)
        {
            for (int i = 0; i < MassAgentChunk.ChunkCapacity; i++)
            {
                if (!chunk.Active[i]) continue;
                if (chunk.BehaviorTypes[i] != BehaviorType) continue;
                if (chunk.States[i] != MassAgentState.Active) continue;

                var agent = chunk.Get(i);
                float turnRate = Math.Max(ctx.Config.MaxTurnRate, 0.1f);
                // 随机偏转游荡角，产生漫游路径
                agent.WanderAngle += (RandomShared.NextSingle() - 0.5f) * turnRate * dt * 2f;
                float a = agent.WanderAngle;
                Vector3 dir = new Vector3(MathF.Cos(a), 0, MathF.Sin(a));
                agent.Forward = SteerTowards(agent.Forward, dir, turnRate, dt);
                agent.DesiredVelocity = agent.Forward * agent.MaxSpeed;
            }
        }
    }

    /// <summary>
    /// 巡逻行为：沿预设路径点循环往返。对齐 UE5 MassPatrol 片段。
    /// </summary>
    public sealed class PatrolBehavior : MassBehaviorBase
    {
        public override MassBehaviorType BehaviorType => MassBehaviorType.Patrol;

        public override void ProcessChunk(MassBehaviorContext ctx, MassAgentChunk chunk, float dt)
        {
            for (int i = 0; i < MassAgentChunk.ChunkCapacity; i++)
            {
                if (!chunk.Active[i]) continue;
                if (chunk.BehaviorTypes[i] != BehaviorType) continue;
                if (chunk.States[i] != MassAgentState.Active) continue;

                var agent = chunk.Get(i);
                int id = ctx.Manager.GetAgentIdByLocation(agent.Chunk, agent.Slot);
                var loop = ctx.Manager.GetPatrolLoop(id);
                if (loop == null || loop.Count == 0)
                {
                    agent.DesiredVelocity = Vector3.Zero;
                    continue;
                }

                int idx = agent.PathIndex % loop.Count;
                Vector3 wp = loop[idx];
                Vector3 pos = agent.Position;
                if (Vector3.DistanceSquared(pos, wp) < 1f)
                {
                    agent.PathIndex = (idx + 1) % loop.Count;
                    wp = loop[agent.PathIndex];
                }
                Vector3 dir = wp - pos;
                agent.Forward = SteerTowards(agent.Forward, dir, ctx.Config.MaxTurnRate, dt);
                agent.DesiredVelocity = agent.Forward * agent.MaxSpeed;
            }
        }
    }

    /// <summary>
    /// 排队行为：检测前方邻居，有人则减速停止，保持间距。对齐 UE5 MassCrowd 的排队 / 跟随片段。
    /// </summary>
    public sealed class QueueBehavior : MassBehaviorBase
    {
        public override MassBehaviorType BehaviorType => MassBehaviorType.Queue;

        public override void ProcessChunk(MassBehaviorContext ctx, MassAgentChunk chunk, float dt)
        {
            float stopDist = ctx.Config.QueueStopDistance;
            for (int i = 0; i < MassAgentChunk.ChunkCapacity; i++)
            {
                if (!chunk.Active[i]) continue;
                if (chunk.BehaviorTypes[i] != BehaviorType) continue;
                if (chunk.States[i] != MassAgentState.Active) continue;

                var agent = chunk.Get(i);
                Vector3 pos = agent.Position;
                Vector3 fwd = agent.Forward;
                Vector3 target = agent.TargetPosition;

                // 朝目标的期望方向
                Vector3 toTarget = target - pos;
                toTarget = new Vector3(toTarget.X, 0, toTarget.Z);
                float distToTarget = toTarget.Length();
                Vector3 dir = distToTarget > 1e-3f ? toTarget / distToTarget : fwd;
                agent.Forward = SteerTowards(fwd, dir, ctx.Config.MaxTurnRate, dt);

                // 查询前方邻居
                int id = ctx.Manager.GetAgentIdByLocation(agent.Chunk, agent.Slot);
                var neighbors = ctx.SpatialHash.QueryRadius(pos, stopDist * 2f);
                bool blocked = false;
                float minAhead = float.MaxValue;
                foreach (int nid in neighbors)
                {
                    if (nid == id) continue;
                    var nb = ctx.Manager.GetAgent(nid);
                    if (!nb.IsActive) continue;
                    Vector3 toNb = nb.Position - pos;
                    float ahead = Vector3.Dot(toNb, agent.Forward);
                    if (ahead > 0 && ahead < stopDist * 2f) // 前方
                    {
                        float lateral = MathF.Abs(Vector3.Dot(toNb, new Vector3(-agent.Forward.Z, 0, agent.Forward.X)));
                        if (lateral < agent.Radius + nb.Radius) // 同列
                        {
                            blocked = true;
                            if (ahead < minAhead) minAhead = ahead;
                        }
                    }
                }

                float speed = agent.MaxSpeed;
                if (blocked)
                {
                    // 前方过近则停止，否则按距离减速
                    float gap = minAhead - (agent.Radius * 2f);
                    speed = gap < 0.2f ? 0f : agent.MaxSpeed * Math.Clamp(gap / stopDist, 0f, 1f);
                }
                else if (distToTarget < 1f)
                {
                    speed = agent.MaxSpeed * (distToTarget / 1f);
                }
                agent.DesiredVelocity = agent.Forward * speed;
            }
        }
    }

    /// <summary>
    /// 编队行为：朝编队槽位目标移动（目标由 <see cref="MassFormationManager"/> 计算）。
    /// 对齐 UE5 MassFormation 的成员跟随片段。
    /// </summary>
    public sealed class FormationBehavior : MassBehaviorBase
    {
        public override MassBehaviorType BehaviorType => MassBehaviorType.Formation;

        public override void ProcessChunk(MassBehaviorContext ctx, MassAgentChunk chunk, float dt)
        {
            for (int i = 0; i < MassAgentChunk.ChunkCapacity; i++)
            {
                if (!chunk.Active[i]) continue;
                if (chunk.BehaviorTypes[i] != BehaviorType) continue;
                if (chunk.States[i] != MassAgentState.Active) continue;

                var agent = chunk.Get(i);
                Vector3 pos = agent.Position;
                Vector3 target = agent.TargetPosition;
                Vector3 dir = target - pos;
                float d = dir.Length();
                if (d < 0.2f)
                {
                    agent.DesiredVelocity = Vector3.Zero;
                    continue;
                }
                dir /= d;
                agent.Forward = SteerTowards(agent.Forward, dir, ctx.Config.MaxTurnRate, dt);
                float speed = d < 1.5f ? agent.MaxSpeed * (d / 1.5f) : agent.MaxSpeed;
                agent.DesiredVelocity = agent.Forward * speed;
            }
        }
    }

    // ==========================================================================
    //  三、避障（RVO / ORCA）与空间哈希
    // ==========================================================================

    /// <summary>
    /// 空间哈希网格。对齐 UE5 MassCrowd 的空间加速结构：
    /// 将代理按 XZ 坐标分桶，QueryRadius 仅检索邻近桶，O(1) 平均插入、O(k) 邻居查询。
    /// 每帧由系统整体重建，保证与代理位置同步。
    /// </summary>

    /// <summary>
    /// ORCA 半平面： permitted = { v ∈ R² : (v - Point) · Normal ≥ 0 }。
    /// 对齐 RVO2 的 Line（点 + 方向），此处以法向形式存储便于投影求解。
    /// </summary>
    public struct OrcaLine
    {
        /// <summary>半平面边界上的一点（速度空间，2D=XZ）。</summary>
        public Vector2 Point;
        /// <summary>指向许可区域的单位法向。</summary>
        public Vector2 Normal;
    }

    /// <summary>
    /// RVO 互惠速度障碍避障。对齐 UE5 MassAvoidance（基于 RVO2 / ORCA）。
    ///
    /// 实现思路（ORCA，Optimal Reciprocal Collision Avoidance）：
    ///   对当前代理 A 与每个邻居 B，构造一个“截断速度障碍”VO_{A|B}^τ（τ 为时间步幅），
    ///   其几何为：在相对速度空间中以“截断圆盘 + 锥面”构成。取 A 承担一半责任，
    ///   得到一条半平面（ORCA line）；最终在所有半平面与速度圆盘的交集中，
    ///   求最接近偏好速度的可行速度。
    ///
    /// 关键几何推导（相对速度空间，2D=XZ）：
    ///   relPos = A.pos - B.pos（B→A），relVel = A.vel - B.vel，R = rA + rB。
    ///   截断圆盘中心 = -relPos/τ、半径 = R/τ；锥轴 = -relPos 方向、半角 θ 满足 sinθ = R/|relPos|。
    ///   作 apex-shift：w = relVel + relPos/τ（圆盘移到原点）。
    ///   求边界上离 w 最近的点 wClosest，得到修正 u = wClosest - w；A 取半 →
    ///   半平面点 = vOptA + 0.5u，法向 = -normalize(u)（指向许可侧）。
    /// </summary>
    public static class RVOAvoidance
    {
        /// <summary>
        /// 计算代理 A 考虑所有邻居后的避障速度（2D=XZ）。
        /// </summary>
        /// <param name="posA">A 位置。</param>
        /// <param name="velA">A 当前速度。</param>
        /// <param name="radiusA">A 半径。</param>
        /// <param name="maxSpeed">A 最大速度。</param>
        /// <param name="neighbors">邻居信息列表（位置 / 速度 / 半径）。</param>
        /// <param name="timeHorizon">避障时间步幅 τ（秒）。</param>
        /// <param name="timeStep">碰撞情形使用的短时间步（秒）。</param>
        /// <returns>避障后的速度（2D）。</returns>
        public static Vector2 ComputeAvoidanceVelocity(
            Vector3 posA, Vector3 velA, float radiusA, float maxSpeed,
            List<NeighborInfo> neighbors, float timeHorizon, float timeStep)
        {
            Vector2 vOptA = To2D(velA);
            var lines = new List<OrcaLine>(neighbors.Count);

            for (int i = 0; i < neighbors.Count; i++)
            {
                var nb = neighbors[i];
                var line = ComputeOrcaLine(
                    To2D(posA), vOptA, radiusA,
                    To2D(nb.Position), To2D(nb.Velocity), nb.Radius,
                    timeHorizon, timeStep);
                if (line.HasValue) lines.Add(line.Value);
            }

            // 求解：在半平面交集 + 速度圆盘中，求最接近偏好速度的可行速度。
            // 采用迭代投影（linearProgram2 风格）：从偏好速度出发，依次把违反的半平面投影到边界，
            // 最后夹到速度圆盘。多轮迭代以缓解夹圆盘后再次违例。
            Vector2 newVel = vOptA;
            if (newVel.LengthSquared() > maxSpeed * maxSpeed)
                newVel = Vector2.Normalize(newVel) * maxSpeed;

            for (int pass = 0; pass < 2; pass++)
            {
                for (int i = 0; i < lines.Count; i++)
                {
                    var line = lines[i];
                    float violation = Vector2.Dot(newVel - line.Point, line.Normal);
                    if (violation < 0f)
                    {
                        // 投影到半平面边界：newVel -= Normal * violation（violation<0 故沿 +Normal 移动）
                        newVel -= line.Normal * violation;
                    }
                }
                // 夹到速度圆盘
                float lenSq = newVel.LengthSquared();
                if (lenSq > maxSpeed * maxSpeed)
                {
                    float len = MathF.Sqrt(lenSq);
                    newVel = newVel * (maxSpeed / len);
                }
            }
            return newVel;
        }

        /// <summary>
        /// 构造 A 相对 B 的单条 ORCA 半平面。返回 null 表示无需约束（不会碰撞）。
        /// </summary>
        private static OrcaLine? ComputeOrcaLine(
            Vector2 posA, Vector2 vOptA, float radiusA,
            Vector2 posB, Vector2 vOptB, float radiusB,
            float tau, float timeStep)
        {
            Vector2 relPos = posA - posB;        // B→A
            Vector2 relVel = vOptA - vOptB;      // A 相对 B
            float distSq = relVel.LengthSquared() > 0 ? relPos.LengthSquared() : relPos.LengthSquared();
            distSq = relPos.LengthSquared();
            float dist = MathF.Sqrt(distSq);
            float R = radiusA + radiusB;
            float Rsq = R * R;

            Vector2 u; // 修正向量（指向“应修正”的方向，许可侧 = -u 侧）

            if (distSq > Rsq)
            {
                // ── 未碰撞 ──
                float invTau = 1f / tau;
                Vector2 pHat = dist > 1e-5f ? relPos / dist : new Vector2(1, 0);
                // apex-shift：把截断圆盘移到原点
                Vector2 w = relVel + relPos * invTau;        // = vOptA - vOptB + relPos/τ
                float s = -Vector2.Dot(w, pHat);             // 沿 -pHat（朝 B）的分量
                Vector2 perp = w + pHat * s;                 // 垂直分量
                float perpLen = perp.Length();

                float legSq = distSq - Rsq;
                bool useCutoffCircle = s < 0f; // 朝远离 B 方向 → 最近点在截断圆盘上

                if (useCutoffCircle)
                {
                    // 截断圆盘（半径 R/τ，圆心在原点 w 空间）上最近点
                    float discR = R * invTau;
                    float wLen = w.Length();
                    if (wLen < 1e-5f)
                    {
                        // 相对速度近零且朝远离：几乎不构成约束，跳过
                        return null;
                    }
                    Vector2 wClosest = (w / wLen) * discR; // 不论 wLen 与 discR 关系，最近边界点投影到圆边
                    u = wClosest - w;
                }
                else
                {
                    // 锥面腿：半角 θ，tanθ = R / sqrt(distSq - Rsq)
                    if (legSq <= 0f)
                    {
                        // 相切退化：用圆盘
                        float discR = R * invTau;
                        float wLen = w.Length();
                        if (wLen < 1e-5f) return null;
                        Vector2 wClosest = (w / wLen) * discR;
                        u = wClosest - w;
                    }
                    else
                    {
                        float tanTheta = R / MathF.Sqrt(legSq);
                        float coneRadius = s * tanTheta; // 深度 s 处的锥半径
                        if (perpLen < 1e-5f)
                        {
                            // 正对锥轴内部：垂直方向任取，朝 +pHat 推出（远离 B）
                            u = pHat * (coneRadius); // 推到锥面
                            // 但需保证方向：把 w 推到锥面边界（任意垂直向）
                            u = -pHat * coneRadius; // 沿轴负（远离 B）方向修正以脱离锥
                        }
                        else
                        {
                            Vector2 perpDir = perp / perpLen;
                            Vector2 wClosest = -pHat * s + perpDir * coneRadius;
                            u = wClosest - w;
                        }
                    }
                }
            }
            else
            {
                // ── 已碰撞：沿 B→A 方向分离，使用短时间步 timeStep ──
                Vector2 pDir = dist > 1e-5f ? relPos / dist : new Vector2(1, 0);
                float invTs = 1f / Math.Max(0.01f, timeStep);
                // 需要的分离速度沿 pDir： (R - dist)/timeStep + 抵消当前相向分量
                float approach = -Vector2.Dot(relVel, pDir); // >0 表示相向
                float need = (R - dist) * invTs + Math.Max(0f, approach);
                u = pDir * need; // 朝远离 B 方向修正
            }

            float uLen = u.Length();
            if (uLen < 1e-6f) return null; // 无需修正

            Vector2 normal = u / uLen;
            // ORCA：A 承担一半 → 半平面点 = vOptA + 0.5u；许可侧法向 = -normal（远离 B）
            var line = new OrcaLine
            {
                Point = vOptA + u * 0.5f,
                Normal = -normal
            };
            return line;
        }

        /// <summary>Vector3(X,Z) -> Vector2(x=X, y=Z)。</summary>
        public static Vector2 To2D(Vector3 v) => new Vector2(v.X, v.Z);

        /// <summary>Vector2 -> Vector3(X, 0, Z)。</summary>
        public static Vector3 To3D(Vector2 v) => new Vector3(v.X, 0, v.Y);
    }

    /// <summary>邻居信息（位置 / 速度 / 半径）。</summary>
    public readonly struct NeighborInfo
    {
        /// <summary>邻居位置。</summary>
        public readonly Vector3 Position;
        /// <summary>邻居速度。</summary>
        public readonly Vector3 Velocity;
        /// <summary>邻居半径。</summary>
        public readonly float Radius;

        public NeighborInfo(Vector3 pos, Vector3 vel, float radius)
        {
            Position = pos;
            Velocity = vel;
            Radius = radius;
        }
    }

    // ==========================================================================
    //  四、群组与编队
    // ==========================================================================

    /// <summary>
    /// 群组：一组共享行为与编队的代理。对齐 UE5 MassCrowd Group 的概念。
    /// </summary>
    public sealed class MassGroup
    {
        /// <summary>群组 ID。</summary>
        public int GroupId;
        /// <summary>队长代理 ID（0 = 无队长）。</summary>
        public int LeaderAgentId;
        /// <summary>群组共同行为。</summary>
        public MassBehaviorType BehaviorType;
        /// <summary>编队形态。</summary>
        public MassFormationType Formation = MassFormationType.None;
        /// <summary>编队间距（米）。</summary>
        public float Spacing = 1.5f;
        /// <summary>成员代理 ID 列表。</summary>
        public List<int> Members = new();

        /// <summary>构造群组。</summary>
        public MassGroup(int groupId) { GroupId = groupId; }
    }

    /// <summary>
    /// 编队管理器。对齐 UE5 MassFormation：
    ///   - <see cref="ComputeFormationOffset"/> 计算成员相对队长的队形偏移；
    ///   - <see cref="UpdateGroups"/> 每帧根据队长位姿刷新成员目标位置。
    /// </summary>
    public sealed class MassFormationManager
    {
        private readonly MassEntityManager _manager;
        private readonly Dictionary<int, MassGroup> _groups = new();

        /// <summary>构造并绑定管理器。</summary>
        public MassFormationManager(MassEntityManager manager) { _manager = manager; }

        /// <summary>所有群组（只读）。</summary>
        public IReadOnlyCollection<MassGroup> Groups => _groups.Values;

        /// <summary>创建群组。</summary>
        public MassGroup CreateGroup(MassBehaviorType behavior, MassFormationType formation)
        {
            int id = _groups.Count + 1;
            var g = new MassGroup(id) { BehaviorType = behavior, Formation = formation };
            _groups[id] = g;
            return g;
        }

        /// <summary>获取群组。</summary>
        public MassGroup? GetGroup(int groupId) =>
            _groups.TryGetValue(groupId, out var g) ? g : null;

        /// <summary>将代理加入群组（设置 GroupId 与行为）。</summary>
        public void AddMember(MassGroup group, int agentId)
        {
            if (!group.Members.Contains(agentId)) group.Members.Add(agentId);
            var agent = _manager.GetAgent(agentId);
            if (agent.IsValid)
            {
                agent.GroupId = group.GroupId;
                agent.Behavior = group.BehaviorType;
            }
        }

        /// <summary>设置队长。</summary>
        public void SetLeader(MassGroup group, int agentId)
        {
            group.LeaderAgentId = agentId;
            if (group.Members.Count == 0 || !group.Members.Contains(agentId))
                group.Members.Insert(0, agentId);
            var agent = _manager.GetAgent(agentId);
            if (agent.IsValid) agent.GroupId = group.GroupId;
        }

        /// <summary>
        /// 计算编队中第 index 个成员（共 count 个，含队长 index=0）相对队长的偏移。
        /// 队长朝向为 +Forward（XZ），偏移在队长局部坐标系下给出后转换到世界。
        /// 这里返回局部偏移（未旋转），由 UpdateGroups 做旋转换算。
        /// </summary>
        public Vector3 ComputeFormationOffset(MassFormationType type, int index, int count, float spacing)
        {
            if (index <= 0 || count <= 1) return Vector3.Zero;
            switch (type)
            {
                case MassFormationType.Circle:
                {
                    // 队长居中，其余均匀环绕
                    int n = count - 1;
                    int i = index - 1;
                    float a = (i / (float)n) * MathF.PI * 2f;
                    float r = spacing * 1.5f;
                    return new Vector3(MathF.Cos(a) * r, 0, MathF.Sin(a) * r);
                }
                case MassFormationType.Line:
                {
                    // 横排，队长居中
                    int half = (count - 1) / 2;
                    int i = index - 1 - half;
                    return new Vector3(i * spacing, 0, 0);
                }
                case MassFormationType.Wedge:
                {
                    // 楔形：行号递增，每行两个，向两侧展开
                    int i = index - 1;
                    int row = (int)Math.Floor((MathF.Sqrt(1f + 8f * i) - 1f) * 0.5f); // 三角行号
                    int inRow = i - row * (row + 1) / 2;
                    float z = -(row + 1) * spacing;
                    float x = (inRow - row * 0.5f) * spacing;
                    return new Vector3(x, 0, z);
                }
                case MassFormationType.Column:
                {
                    // 纵队：依次排在队长身后
                    return new Vector3(0, 0, -index * spacing);
                }
                case MassFormationType.Grid:
                {
                    // 方阵：近正方形网格
                    int cols = (int)Math.Ceiling(MathF.Sqrt(count));
                    int i = index - 1;
                    int r = i / cols;
                    int c = i % cols;
                    float half = (cols - 1) * 0.5f;
                    return new Vector3((c - half) * spacing, 0, -(r + 1) * spacing);
                }
                default:
                    return Vector3.Zero;
            }
        }

        /// <summary>
        /// 每帧更新所有群组：根据队长位姿刷新成员的编队目标位置。
        /// 成员的 TargetPosition 被设为队长世界位置 + 旋转后的偏移。
        /// </summary>
        public void UpdateGroups(float dt)
        {
            foreach (var group in _groups.Values)
            {
                if (group.Formation == MassFormationType.None) continue;
                if (group.LeaderAgentId <= 0) continue;
                var leader = _manager.GetAgent(group.LeaderAgentId);
                if (!leader.IsValid) continue;

                Vector3 lpos = leader.Position;
                Vector3 lfwd = leader.Forward;
                lfwd = new Vector3(lfwd.X, 0, lfwd.Z);
                if (lfwd.LengthSquared() < 1e-5f) lfwd = new Vector3(0, 0, -1);
                lfwd = Vector3.Normalize(lfwd);
                Vector3 lright = new Vector3(lfwd.Z, 0, -lfwd.X); // 右向量（XZ）

                int count = group.Members.Count;
                for (int i = 0; i < count; i++)
                {
                    int aid = group.Members[i];
                    if (aid == group.LeaderAgentId) continue;
                    Vector3 localOff = ComputeFormationOffset(group.Formation, i, count, group.Spacing);
                    // 局部偏移 -> 世界：right * x + forward * z
                    Vector3 world = lpos + lright * localOff.X + lfwd * localOff.Z;
                    var agent = _manager.GetAgent(aid);
                    if (agent.IsValid) agent.TargetPosition = world;
                }
            }
        }
    }

    // ==========================================================================
    //  五、人群生成与生命周期
    // ==========================================================================

    /// <summary>
    /// 人群生成器。对齐 UE5 MassSpawner：
    ///   - <see cref="SpawnAt"/> 在指定中心生成一批代理；
    ///   - <see cref="SpawnOnLane"/> 沿 ZoneGraph 车道分布生成；
    ///   - <see cref="Despawn"/> 销毁单个代理；
    ///   - 密度控制：区域内达到上限则不再生成。
    /// </summary>
    public sealed class MassCrowdSpawner
    {
        private readonly MassEntityManager _manager;
        private readonly SpatialHashGrid _spatial;

        /// <summary>构造生成器。</summary>
        public MassCrowdSpawner(MassEntityManager manager, SpatialHashGrid spatial)
        {
            _manager = manager;
            _spatial = spatial;
        }

        /// <summary>
        /// 在 center 附近生成 count 个代理。返回新建代理 ID 列表。
        /// </summary>
        /// <param name="center">生成中心。</param>
        /// <param name="count">数量。</param>
        /// <param name="state">初始状态。</param>
        /// <param name="radius">生成散布半径。</param>
        public List<int> SpawnAt(Vector3 center, int count, MassAgentState state = MassAgentState.Active, float radius = 5f)
        {
            var ids = new List<int>(count);
            if (_manager.ActiveCount >= _manager.Capacity) return ids;
            for (int i = 0; i < count; i++)
            {
                // 密度控制：中心附近已达上限则跳过
                if (DensityExceeds(center, radius, 40)) break;

                float a = RandomShared.NextSingle() * MathF.PI * 2f;
                float r = RandomShared.NextSingle() * radius;
                Vector3 pos = center + new Vector3(MathF.Cos(a) * r, 0, MathF.Sin(a) * r);
                int id = _manager.Allocate(pos);
                if (id <= 0) break;
                var agent = _manager.GetAgent(id);
                agent.State = state;
                ids.Add(id);
            }
            return ids;
        }

        /// <summary>
        /// 沿 ZoneGraph 车道分布生成 count 个代理。若 ZoneGraph 不可用则回退到 SpawnAt。
        /// </summary>
        /// <param name="laneId">车道 ID。</param>
        /// <param name="count">数量。</param>
        public List<int> SpawnOnLane(int laneId, int count)
        {
            var ids = new List<int>(count);
            ZoneGraphData? graph = ZoneGraphSystem.Active?.Graph;
            ZoneLane? lane = graph?.GetLane(laneId);
            if (lane == null || lane.SplinePoints.Count < 2)
            {
                // 回退：在原点附近生成
                return SpawnAt(Vector3.Zero, count);
            }

            var pts = lane.SplinePoints;
            for (int i = 0; i < count; i++)
            {
                float t = RandomShared.NextSingle();
                Vector3 pos = SampleSpline(pts, t);
                // 在车道宽度内随机偏移
                Vector3 right = lane.RightVec;
                float off = (RandomShared.NextSingle() * 2f - 1f) * (lane.Width * 0.4f);
                pos += right * off;
                int id = _manager.Allocate(pos);
                if (id <= 0) break;
                var agent = _manager.GetAgent(id);
                agent.CurrentLaneId = laneId;
                ids.Add(id);
            }
            return ids;
        }

        /// <summary>销毁代理（先标记 Dying，由生命周期回收，或立即 Free）。</summary>
        public void Despawn(int agentId)
        {
            if (!_manager.HasAgent(agentId)) return;
            var agent = _manager.GetAgent(agentId);
            if (agent.IsValid) agent.State = MassAgentState.Dying;
        }

        /// <summary>区域内密度是否超限。</summary>
        public bool DensityExceeds(Vector3 center, float radius, int maxCount)
        {
            var cands = _spatial.QueryRadius(center, radius);
            int cnt = 0;
            foreach (int id in cands)
            {
                var a = _manager.GetAgent(id);
                if (a.IsActive && Vector3.Distance(a.Position, center) <= radius) cnt++;
            }
            return cnt >= maxCount;
        }

        /// <summary>沿样条线性采样（t∈[0,1]）。</summary>
        private static Vector3 SampleSpline(List<Vector3> pts, float t)
        {
            if (pts.Count == 1) return pts[0];
            float seg = t * (pts.Count - 1);
            int i = Math.Min((int)Math.Floor(seg), pts.Count - 2);
            float f = seg - i;
            return Vector3.Lerp(pts[i], pts[i + 1], f);
        }
    }

    /// <summary>
    /// 生命周期管理器。对齐 UE5 MassLOD + MassSpawner 生命周期：
    ///   - 按到焦点（玩家 / 摄像机）距离切换 LOD，远距离降频或冻结；
    ///   - 处理 Dying 代理的回收；
    ///   - 区域容量限制（超出上限的远景代理优先卸载）。
    /// </summary>
    public sealed class MassCrowdLifecycle
    {
        private readonly MassEntityManager _manager;
        private readonly MassCrowdSpawner _spawner;

        /// <summary>构造生命周期管理器。</summary>
        public MassCrowdLifecycle(MassEntityManager manager, MassCrowdSpawner spawner)
        {
            _manager = manager;
            _spawner = spawner;
        }

        /// <summary>
        /// 每帧更新 LOD 与回收。返回需要“本帧模拟”的代理是否就绪。
        /// </summary>
        public void Update(float dt, Vector3 focalPoint, MassCrowdConfig config)
        {
            float nearSq = config.LodNearDistance * config.LodNearDistance;
            float midSq = config.LodMidDistance * config.LodMidDistance;

            foreach (var chunk in _manager.Chunks)
            {
                if (chunk.ActiveCount <= 0) continue;
                for (int i = 0; i < MassAgentChunk.ChunkCapacity; i++)
                {
                    if (!chunk.Active[i]) continue;

                    // 回收濒死代理
                    if (chunk.States[i] == MassAgentState.Dying)
                    {
                        int id = _manager.GetAgentIdByLocation(chunk, i);
                        if (id > 0) _manager.Free(id);
                        continue;
                    }

                    // LOD 距离判定
                    Vector3 pos = chunk.Positions[i];
                    float distSq = Vector3.DistanceSquared(pos, focalPoint);
                    MassAgentLod lod;
                    if (distSq <= nearSq) lod = MassAgentLod.Near;
                    else if (distSq <= midSq) lod = MassAgentLod.Mid;
                    else lod = MassAgentLod.Far;
                    chunk.Lods[i] = lod;

                    // 远 LOD 冻结：超出远距离且容量紧张时标记非活跃
                    if (lod == MassAgentLod.Far && _manager.ActiveCount > config.MaxAgents * 0.9f)
                    {
                        chunk.States[i] = MassAgentState.Inactive;
                    }
                    else if (chunk.States[i] == MassAgentState.Inactive && lod != MassAgentLod.Far)
                    {
                        chunk.States[i] = MassAgentState.Active;
                    }

                    // 降频计时器累加
                    chunk.UpdateTimers[i] += dt;
                }
            }
        }

        /// <summary>
        /// 判断代理本帧是否应执行行为 / 避障更新（依据 LOD 降频）。
        /// </summary>
        public bool ShouldUpdateThisFrame(MassAgent agent, MassCrowdConfig config)
        {
            if (agent.State != MassAgentState.Active) return false;
            float interval = agent.Lod switch
            {
                MassAgentLod.Near => config.LodNearUpdateInterval,
                MassAgentLod.Mid => config.LodMidUpdateInterval,
                _ => config.LodFarUpdateInterval
            };
            // interval<=1 表示每帧更新
            if (interval <= 1f) return true;
            return agent.UpdateTimer >= interval * config.FixedFrameDt;
        }

        /// <summary>重生一批代理到指定中心（用于人口维持）。</summary>
        public void RespawnAround(Vector3 center, int targetCount, float radius)
        {
            if (_manager.ActiveCount >= targetCount) return;
            int need = targetCount - _manager.ActiveCount;
            _spawner.SpawnAt(center, need, MassAgentState.Active, radius);
        }
    }

    // ==========================================================================
    //  六、系统集成（ECS 组件 / 系统 / 配置）
    // ==========================================================================

    /// <summary>
    /// ECS 组件：将主 ECS 实体与一个 MassAgent 关联。
    /// 引擎约束要求组件为 struct : IComponent。对齐 UE5 中 UMassAgentComponent 的桥接角色。
    /// </summary>
    public struct MassCrowdComponent : IComponent
    {
        /// <summary>关联的 Mass 代理 ID（0 = 未关联）。</summary>
        public int MassAgentId;

        /// <summary>是否已关联代理。</summary>
        public bool IsValid => MassAgentId > 0;
    }

    /// <summary>
    /// 全局配置。对齐 UE5 MassCrowd 的各项调参。
    /// </summary>
    public sealed class MassCrowdConfig
    {
        /// <summary>最大代理数（软上限）。</summary>
        public int MaxAgents = 20000;

        // ── 避障 ──
        /// <summary>是否启用 RVO 避障。</summary>
        public bool EnableAvoidance = true;
        /// <summary>避障时间步幅 τ（秒）。</summary>
        public float AvoidanceTimeHorizon = 1.5f;
        /// <summary>邻居查询半径。</summary>
        public float NeighborQueryRadius = 3f;
        /// <summary>每代理最大邻居数。</summary>
        public int MaxNeighbors = 16;
        /// <summary>空间哈希格子大小。</summary>
        public float SpatialCellSize = 4f;

        // ── LOD ──
        /// <summary>近景距离阈值。</summary>
        public float LodNearDistance = 40f;
        /// <summary>中景距离阈值。</summary>
        public float LodMidDistance = 120f;
        /// <summary>近景更新间隔（帧）。</summary>
        public float LodNearUpdateInterval = 1f;
        /// <summary>中景更新间隔（帧）。</summary>
        public float LodMidUpdateInterval = 4f;
        /// <summary>远景更新间隔（帧）。</summary>
        public float LodFarUpdateInterval = 16f;
        /// <summary>用于 LOD 计时的固定帧步长（秒）。</summary>
        public float FixedFrameDt = 1f / 30f;

        // ── 行为参数 ──
        /// <summary>最大转向速率（弧度/秒）。</summary>
        public float MaxTurnRate = 8f;
        /// <summary>游荡方向变化率。</summary>
        public float WanderRate = 2f;
        /// <summary>游荡速度系数。</summary>
        public float WanderSpeedFactor = 0.6f;
        /// <summary>逃跑安全距离。</summary>
        public float FleeSafeDistance = 20f;
        /// <summary>排队停止距离。</summary>
        public float QueueStopDistance = 1.2f;

        // ── 默认代理参数 ──
        /// <summary>默认代理半径。</summary>
        public float DefaultAgentRadius = 0.4f;
        /// <summary>默认最大速度。</summary>
        public float DefaultMaxSpeed = 1.4f;

        // ── 联动 ──
        /// <summary>是否将 MassAgent 位姿回写到主 ECS 实体的 TransformComponent。</summary>
        public bool SyncToECS = true;
    }

    /// <summary>
    /// Mass Crowd 系统。实现 ECS 的 <see cref="ISystem"/>，对齐 UE5 MassCrowd 系统主循环：
    ///   每帧流水线：空间哈希更新 → 按块执行行为 → RVO 避障 → 积分位置 → 编队更新 → LOD/生命周期 → ECS 回写。
    /// 与 <c>Rendering.CrowdRenderingSystem</c> 解耦：后者负责实例化渲染，本系统负责行为模拟，
    /// 通过 <see cref="MassCrowdComponent"/> 与主 ECS 实体桥接，渲染层可读取回写的 TransformComponent。
    /// </summary>
    public sealed class MassCrowdSystem : ISystem
    {
        /// <summary>系统名称。</summary>
        public string Name => "MassCrowdSystem";

        /// <summary>全局活跃实例（供游戏逻辑访问）。</summary>
        public static MassCrowdSystem? Active { get; private set; }

        /// <summary>群体实体管理器。</summary>
        public MassEntityManager EntityManager { get; }

        /// <summary>空间哈希网格。</summary>
        public SpatialHashGrid SpatialHash { get; }

        /// <summary>编队管理器。</summary>
        public MassFormationManager FormationManager { get; }

        /// <summary>人群生成器。</summary>
        public MassCrowdSpawner Spawner { get; }

        /// <summary>生命周期管理器。</summary>
        public MassCrowdLifecycle Lifecycle { get; }

        /// <summary>全局配置。</summary>
        public MassCrowdConfig Config { get; set; } = new();

        /// <summary>绑定的导航系统（寻路主源）。</summary>
        public NavigationSystem? Navigation { get; private set; }

        /// <summary>绑定的 ZoneGraph 系统（可选）。</summary>
        public ZoneGraphSystem? ZoneGraph { get; private set; }

        /// <summary>LOD 焦点（玩家 / 摄像机位置），由外部每帧设置。</summary>
        public Vector3 FocalPoint;

        // 行为处理器
        private readonly List<MassBehaviorBase> _behaviors = new();
        // 邻居查询复用缓冲
        private readonly List<NeighborInfo> _neighborBuf = new(64);

        /// <summary>构造系统并设为 Active。</summary>
        public MassCrowdSystem()
        {
            EntityManager = new MassEntityManager();
            SpatialHash = new SpatialHashGrid(Config.SpatialCellSize);
            FormationManager = new MassFormationManager(EntityManager);
            Spawner = new MassCrowdSpawner(EntityManager, SpatialHash);
            Lifecycle = new MassCrowdLifecycle(EntityManager, Spawner);

            // 注册内置行为处理器
            _behaviors.Add(new MassSeekBehavior());
            _behaviors.Add(new MassWanderBehavior());
            _behaviors.Add(new FleeBehavior());
            _behaviors.Add(new PatrolBehavior());
            _behaviors.Add(new QueueBehavior());
            _behaviors.Add(new FormationBehavior());

            Active = this;
        }

        /// <summary>绑定导航系统。</summary>
        public void BindNavigation(NavigationSystem nav) => Navigation = nav;

        /// <summary>绑定 ZoneGraph 系统。</summary>
        public void BindZoneGraph(ZoneGraphSystem zg) => ZoneGraph = zg;

        /// <summary>设置 LOD 焦点。</summary>
        public void SetFocalPoint(Vector3 p) => FocalPoint = p;

        /// <summary>为 ECS 实体关联一个新建的 Mass 代理，挂载 MassCrowdComponent。</summary>
        public int BindEntity(EntityComponentSystem ecs, int entityId, Vector3 pos)
        {
            if (!ecs.HasEntity(entityId)) return 0;
            int agentId = EntityManager.Allocate(pos, Config.DefaultAgentRadius, Config.DefaultMaxSpeed);
            if (agentId <= 0) return 0;
            ecs.AddComponent(entityId, new MassCrowdComponent { MassAgentId = agentId });
            return agentId;
        }

        /// <summary>
        /// 系统每帧更新。流水线见类注释。
        /// </summary>
        public void Update(float dt, EntityComponentSystem ecs)
        {
            if (EntityManager.ActiveCount == 0) return;

            var ctx = new MassBehaviorContext
            {
                Manager = EntityManager,
                SpatialHash = SpatialHash,
                Navigation = Navigation,
                ZoneGraph = ZoneGraph,
                Config = Config,
                FocalPoint = FocalPoint,
                Dt = dt
            };

            // 1) 空间哈希重建
            SpatialHash.Rebuild(EntityManager);

            // 2) 按块执行行为（写 DesiredVelocity）
            foreach (var behavior in _behaviors)
            {
                EntityManager.ForEachChunk(chunk => behavior.ProcessChunk(ctx, chunk, dt));
            }

            // 3) RVO 避障 + 4) 位置积分
            ProcessAvoidanceAndIntegrate(ctx, dt);

            // 5) 编队更新（刷新成员目标位置，下一帧行为据此趋向）
            FormationManager.UpdateGroups(dt);

            // 6) LOD / 生命周期 / 回收
            Lifecycle.Update(dt, FocalPoint, Config);

            // 7) ECS 回写（联动渲染层）
            if (Config.SyncToECS) SyncToECS(ecs);
        }

        /// <summary>避障 + 位置积分：遍历活跃代理，计算避障速度并积分。</summary>
        private void ProcessAvoidanceAndIntegrate(MassBehaviorContext ctx, float dt)
        {
            bool enableAvoidance = Config.EnableAvoidance;
            float tau = Config.AvoidanceTimeHorizon;
            float neighborR = Config.NeighborQueryRadius;
            int maxNeighbors = Config.MaxNeighbors;

            foreach (var chunk in EntityManager.Chunks)
            {
                if (chunk.ActiveCount <= 0) continue;
                for (int i = 0; i < MassAgentChunk.ChunkCapacity; i++)
                {
                    if (!chunk.Active[i]) continue;
                    if (chunk.States[i] != MassAgentState.Active) continue;

                    // LOD 降频：非近景代理按间隔跳过行为/避障（但位置仍积分）
                    var agent = chunk.Get(i);
                    bool fullUpdate = Lifecycle.ShouldUpdateThisFrame(agent, Config);
                    if (fullUpdate) agent.UpdateTimer = 0f;

                    Vector3 desired = chunk.DesiredVelocities[i];
                    Vector3 vel;

                    if (enableAvoidance && fullUpdate && agent.Lod != MassAgentLod.Far)
                    {
                        // 收集邻居
                        _neighborBuf.Clear();
                        var ids = SpatialHash.QueryRadius(chunk.Positions[i], neighborR);
                        int added = 0;
                        foreach (int nid in ids)
                        {
                            if (nid <= 0) continue;
                            var nb = EntityManager.GetAgent(nid);
                            if (!nb.IsValid || !nb.IsActive) continue;
                            if (nid == EntityManager.GetAgentIdByLocation(chunk, i)) continue;
                            float d2 = Vector3.DistanceSquared(nb.Position, chunk.Positions[i]);
                            if (d2 > neighborR * neighborR) continue;
                            _neighborBuf.Add(new NeighborInfo(nb.Position, nb.Velocity, nb.Radius));
                            if (++added >= maxNeighbors) break;
                        }

                        Vector2 av = RVOAvoidance.ComputeAvoidanceVelocity(
                            chunk.Positions[i], desired, chunk.Radii[i], chunk.MaxSpeeds[i],
                            _neighborBuf, tau, dt);
                        vel = RVOAvoidance.To3D(av);
                    }
                    else
                    {
                        // 不避障：直接用期望速度并夹到最大速度
                        vel = desired;
                        float maxS = chunk.MaxSpeeds[i];
                        if (vel.LengthSquared() > maxS * maxS)
                            vel = Vector3.Normalize(vel) * maxS;
                    }

                    // 平滑速度（简单低通），避免抖动
                    chunk.Velocities[i] = Vector3.Lerp(chunk.Velocities[i], vel, 0.5f);

                    // 位置积分
                    chunk.Positions[i] += chunk.Velocities[i] * dt;

                    // 更新朝向
                    Vector3 v = chunk.Velocities[i];
                    if (v.LengthSquared() > 1e-4f)
                    {
                        Vector3 f = Vector3.Normalize(new Vector3(v.X, 0, v.Z));
                        chunk.Forwards[i] = MassBehaviorBase.SteerTowardsStatic(chunk.Forwards[i], f, Config.MaxTurnRate, dt);
                    }
                }
            }
        }

        /// <summary>将 MassAgent 位姿回写到主 ECS 实体的 TransformComponent（联动渲染层）。</summary>
        private void SyncToECS(EntityComponentSystem ecs)
        {
            var entities = ecs.QueryWithComponent<MassCrowdComponent>();
            foreach (int eid in entities)
            {
                var comp = ecs.GetComponent<MassCrowdComponent>(eid);
                if (comp.MassAgentId <= 0) continue;
                var agent = EntityManager.GetAgent(comp.MassAgentId);
                if (!agent.IsValid) continue;

                if (ecs.HasComponent<TransformComponent>(eid))
                {
                    var tc = ecs.GetComponent<TransformComponent>(eid);
                    tc.Position = agent.Position;
                    // 由朝向计算 yaw 旋转（Y 轴）
                    Vector3 f = agent.Forward;
                    if (f.LengthSquared() > 1e-5f)
                    {
                        f = Vector3.Normalize(new Vector3(f.X, 0, f.Z));
                        float yaw = MathF.Atan2(-f.X, -f.Z); // 与 TransformComponent.Forward=(0,0,-1) 约定一致
                        tc.Rotation = Quaternion.CreateFromYawPitchRoll(yaw, 0, 0);
                    }
                    ecs.SetComponent(eid, tc);
                }
            }
        }
    }

    // ==========================================================================
    //  辅助工具
    // ==========================================================================

    /// <summary>
    /// MassEntityManager 的扩展方法：通过 (块, 槽) 反查代理 ID。
    /// 维护一份 (chunkIdx,slot)->id 的反向索引，避免每代理持有自身 ID（保持 SOA 纯净）。
    /// </summary>
    public static class MassEntityManagerExtensions
    {
        // 反向索引：key = chunkIndex * 256 + slot
        private static readonly Dictionary<long, int> _reverse = new();

        /// <summary>登记代理 ID 与位置的映射（由 Allocate 内部调用本扩展维护）。</summary>
        public static void RegisterLocation(this MassEntityManager mgr, int chunkIdx, int slot, int agentId)
        {
            _reverse[Key(chunkIdx, slot)] = agentId;
        }

        /// <summary>按 (块, 槽) 反查代理 ID。</summary>
        public static int GetAgentIdByLocation(this MassEntityManager mgr, MassAgentChunk chunk, int slot)
        {
            if (chunk == null || chunk.ChunkIndex < 0) return 0;
            return _reverse.TryGetValue(Key(chunk.ChunkIndex, slot), out var id) ? id : 0;
        }

        /// <summary>清除位置映射（Free 时调用）。</summary>
        public static void UnregisterLocation(this MassEntityManager mgr, int chunkIdx, int slot)
        {
            _reverse.Remove(Key(chunkIdx, slot));
        }

        private static long Key(int chunkIdx, int slot) => ((long)chunkIdx << 16) | (uint)slot;

        /// <summary>重建空间哈希网格：将所有活跃代理按半径包围盒插入。</summary>
        public static void Rebuild(this SpatialHashGrid grid, MassEntityManager manager)
        {
            if (grid == null || manager == null) return;
            grid.Clear();
            foreach (var chunk in manager.Chunks)
            {
                for (int i = 0; i < MassAgentChunk.ChunkCapacity; i++)
                {
                    if (!chunk.Active[i]) continue;
                    int id = manager.GetAgentIdByLocation(chunk, i);
                    if (id <= 0) continue;
                    float r = chunk.Radii[i];
                    var c = new PhysicsVector3(chunk.Positions[i]);
                    var ext = new PhysicsVector3(r, r, r);
                    grid.Insert(id, new AABBShape(c - ext, c + ext));
                }
            }
        }

        /// <summary>以中心点 + 半径查询网格内的候选代理（调用方需按精确距离再过滤）。</summary>
        public static List<int> QueryRadius(this SpatialHashGrid grid, Vector3 center, float radius)
        {
            var result = new List<int>();
            if (grid == null) return result;
            var c = new PhysicsVector3(center);
            var ext = new PhysicsVector3(radius, radius, radius);
            grid.Query(new AABBShape(c - ext, c + ext), id => result.Add(id));
            return result;
        }
    }

    /// <summary>
    /// 行为基类的静态转向工具门面（供系统积分阶段复用）。
    /// 转发至 <see cref="MassBehaviorBase.SteerTowardsStatic"/>。
    /// </summary>
    public static class MassBehaviorBaseStatic
    {
        /// <summary>将朝向转向目标方向（带最大转向角限制），保持 Y 分量归零。</summary>
        public static Vector3 SteerTowards(Vector3 currentForward, Vector3 desiredDir, float maxTurnRate, float dt)
            => MassBehaviorBase.SteerTowardsStatic(currentForward, desiredDir, maxTurnRate, dt);
    }

    /// <summary>
    /// 共享线程安全随机数源（简化版，单线程使用）。
    /// </summary>
    internal static class RandomShared
    {
        private static readonly Random _rng = new(1337);
        /// <summary>下一个 [0,1) 单精度浮点。</summary>
        public static float NextSingle() => (float)_rng.NextDouble();
    }
}
