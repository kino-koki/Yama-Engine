using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;

namespace ShanyuEngine.ECS
{
    // ════════════════════════════════════════════════════════════════════════
    // 山雨引擎 - 真实ECS系统 (SystemBaseReal)
    //
    //   * SystemBaseReal: 系统基类（OnCreate/OnDestroy/OnUpdate + EntityManager/World引用）
    //   * EntityQueryReal: 实体查询（All/Any/None + Execute + ToEntityArray）
    //   * SystemGroupReal: 系统组（TopologicalSort/依赖声明/Kahn算法）
    //   * 属性: UpdateInGroup / UpdateBefore / UpdateAfter
    // ════════════════════════════════════════════════════════════════════════

    // ════════════════════════════════════════════════════════════════════════
    // EntityQueryReal: 实体查询
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 实体查询：根据组件类型过滤实体集合。
    /// 支持All（必须全部包含）、Any（包含任一）、None（全部不包含）三种过滤模式。
    /// </summary>
    public sealed class EntityQueryReal
    {
        private readonly EntityManagerReal _entityManager;

        // 查询条件的类型索引数组
        private readonly int[] _allTypeIndices;
        private readonly int[] _anyTypeIndices;
        private readonly int[] _noneTypeIndices;

        // 缓存的匹配Archetype列表
        private List<ArchetypeReal> _matchedArchetypes;
        private bool _isDirty = true;

        // 查询条件（原始Type数组，用于哈希和比较）
        private readonly string _queryKey;

        /// <summary>调度策略。</summary>
        public QueryScheduleStrategy ScheduleStrategy { get; set; } = QueryScheduleStrategy.ChunkBased;

        /// <summary>是否需要每帧重新计算匹配的原型。</summary>
        public bool IsDirty => _isDirty;

        /// <summary>上一次查询返回的实体总数。</summary>
        public int CachedEntityCount { get; private set; }

        public EntityQueryReal(
            EntityManagerReal entityManager,
            IReadOnlyList<Type> allTypes = null,
            IReadOnlyList<Type> anyTypes = null,
            IReadOnlyList<Type> noneTypes = null)
        {
            _entityManager = entityManager ?? throw new ArgumentNullException(nameof(entityManager));

            _allTypeIndices = allTypes != null
                ? allTypes.Select(t => ComponentTypeRealCache.Get(t).TypeIndex).ToArray()
                : Array.Empty<int>();
            _anyTypeIndices = anyTypes != null
                ? anyTypes.Select(t => ComponentTypeRealCache.Get(t).TypeIndex).ToArray()
                : Array.Empty<int>();
            _noneTypeIndices = noneTypes != null
                ? noneTypes.Select(t => ComponentTypeRealCache.Get(t).TypeIndex).ToArray()
                : Array.Empty<int>();

            _queryKey = BuildQueryKey(_allTypeIndices, _anyTypeIndices, _noneTypeIndices);
            _matchedArchetypes = new List<ArchetypeReal>();
        }

        public EntityQueryReal(
            EntityManagerReal entityManager,
            params QueryDescription[] descriptions)
            : this(entityManager,
                   descriptions.FirstOrDefault()?.All?.ToArray(),
                   descriptions.FirstOrDefault()?.Any?.ToArray(),
                   descriptions.FirstOrDefault()?.None?.ToArray())
        {
        }

        /// <summary>标记查询为脏（需要重新计算匹配）。</summary>
        public void Invalidate() => _isDirty = true;

        /// <summary>更新匹配的Archetype列表（按需执行）。</summary>
        private void EnsureUpdated()
        {
            if (!_isDirty) return;

            _matchedArchetypes.Clear();
            CachedEntityCount = 0;

            var allArchetypes = _entityManager.GetAllArchetypes();
            foreach (var archetype in allArchetypes)
            {
                if (archetype.MatchesQuery(_allTypeIndices, _anyTypeIndices, _noneTypeIndices))
                {
                    _matchedArchetypes.Add(archetype);
                    CachedEntityCount += archetype.EntityCount;
                }
            }

            _isDirty = false;
        }

        /// <summary>
        /// 遍历匹配的所有实体，对每个实体执行回调。
        /// ChunkBased策略：按Chunk遍历，缓存友好。
        /// </summary>
        public void Execute<T>(Action<EntityReal, ref T> callback) where T : struct
        {
            EnsureUpdated();
            var ct = ComponentTypeRealCache.Get<T>();

            foreach (var archetype in _matchedArchetypes)
            {
                int slotIdx = archetype.GetComponentTypeIndex(ct.TypeIndex);
                if (slotIdx < 0) continue;

                foreach (var chunk in archetype.Chunks)
                {
                    for (int i = 0; i < chunk.EntityCount; i++)
                    {
                        var entity = chunk.GetEntity(i);
                        ref var comp = ref chunk.GetComponentDataRef<T>(i, slotIdx);
                        callback(entity, ref comp);
                    }
                }
            }
        }

        /// <summary>
        /// 遍历匹配的所有实体，对每个实体执行两个组件的回调。
        /// </summary>
        public void Execute<T1, T2>(Action<EntityReal, ref T1, ref T2> callback)
            where T1 : struct where T2 : struct
        {
            EnsureUpdated();
            var ct1 = ComponentTypeRealCache.Get<T1>();
            var ct2 = ComponentTypeRealCache.Get<T2>();

            foreach (var archetype in _matchedArchetypes)
            {
                int idx1 = archetype.GetComponentTypeIndex(ct1.TypeIndex);
                int idx2 = archetype.GetComponentTypeIndex(ct2.TypeIndex);
                if (idx1 < 0 || idx2 < 0) continue;

                foreach (var chunk in archetype.Chunks)
                {
                    for (int i = 0; i < chunk.EntityCount; i++)
                    {
                        var entity = chunk.GetEntity(i);
                        ref var c1 = ref chunk.GetComponentDataRef<T1>(i, idx1);
                        ref var c2 = ref chunk.GetComponentDataRef<T2>(i, idx2);
                        callback(entity, ref c1, ref c2);
                    }
                }
            }
        }

        /// <summary>
        /// 遍历匹配的所有实体，对每个实体执行三个组件的回调。
        /// </summary>
        public void Execute<T1, T2, T3>(Action<EntityReal, ref T1, ref T2, ref T3> callback)
            where T1 : struct where T2 : struct where T3 : struct
        {
            EnsureUpdated();
            var ct1 = ComponentTypeRealCache.Get<T1>();
            var ct2 = ComponentTypeRealCache.Get<T2>();
            var ct3 = ComponentTypeRealCache.Get<T3>();

            foreach (var archetype in _matchedArchetypes)
            {
                int idx1 = archetype.GetComponentTypeIndex(ct1.TypeIndex);
                int idx2 = archetype.GetComponentTypeIndex(ct2.TypeIndex);
                int idx3 = archetype.GetComponentTypeIndex(ct3.TypeIndex);
                if (idx1 < 0 || idx2 < 0 || idx3 < 0) continue;

                foreach (var chunk in archetype.Chunks)
                {
                    for (int i = 0; i < chunk.EntityCount; i++)
                    {
                        var entity = chunk.GetEntity(i);
                        ref var c1 = ref chunk.GetComponentDataRef<T1>(i, idx1);
                        ref var c2 = ref chunk.GetComponentDataRef<T2>(i, idx2);
                        ref var c3 = ref chunk.GetComponentDataRef<T3>(i, idx3);
                        callback(entity, ref c1, ref c2, ref c3);
                    }
                }
            }
        }

        /// <summary>
        /// 返回所有匹配的实体数组。
        /// </summary>
        public EntityReal[] ToEntityArray()
        {
            EnsureUpdated();
            var result = new List<EntityReal>(CachedEntityCount);

            foreach (var archetype in _matchedArchetypes)
            {
                foreach (var chunk in archetype.Chunks)
                {
                    for (int i = 0; i < chunk.EntityCount; i++)
                    {
                        result.Add(chunk.GetEntity(i));
                    }
                }
            }

            return result.ToArray();
        }

        /// <summary>返回所有匹配的实体列表。</summary>
        public List<EntityReal> ToEntityList()
        {
            EnsureUpdated();
            var result = new List<EntityReal>(CachedEntityCount);

            foreach (var archetype in _matchedArchetypes)
            {
                foreach (var chunk in archetype.Chunks)
                {
                    for (int i = 0; i < chunk.EntityCount; i++)
                    {
                        result.Add(chunk.GetEntity(i));
                    }
                }
            }

            return result;
        }

        /// <summary>获取匹配的实体数量。</summary>
        public int GetEntityCount()
        {
            EnsureUpdated();
            return CachedEntityCount;
        }

        /// <summary>获取匹配的原型列表（只读）。</summary>
        public IReadOnlyList<ArchetypeReal> GetMatchedArchetypes()
        {
            EnsureUpdated();
            return _matchedArchetypes;
        }

        private static string BuildQueryKey(int[] all, int[] any, int[] none)
        {
            var sb = new System.Text.StringBuilder();
            if (all != null && all.Length > 0)
            {
                sb.Append("A:");
                Array.Sort(all);
                foreach (int idx in all) sb.Append(idx).Append(',');
            }
            if (any != null && any.Length > 0)
            {
                sb.Append("Y:");
                Array.Sort(any);
                foreach (int idx in any) sb.Append(idx).Append(',');
            }
            if (none != null && none.Length > 0)
            {
                sb.Append("N:");
                Array.Sort(none);
                foreach (int idx in none) sb.Append(idx).Append(',');
            }
            return sb.ToString();
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // ArchetypeChunk扩展：ref访问组件数据
    // ════════════════════════════════════════════════════════════════════════

    public partial class ArchetypeChunkExtensions
    {
        /// <summary>
        /// 获取组件数据的ref引用（用于EntityQuery的高效遍历）。
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref T GetComponentDataRef<T>(this ArchetypeChunk chunk, int entityLocalIndex, int componentTypeIndex)
            where T : struct
        {
            if (entityLocalIndex < 0 || entityLocalIndex >= chunk.EntityCount)
                throw new ArgumentOutOfRangeException(nameof(entityLocalIndex));
            if (componentTypeIndex < 0 || componentTypeIndex >= chunk.Archetype.ComponentTypes.Count)
                throw new ArgumentOutOfRangeException(nameof(componentTypeIndex));

            // 通过反射获取内部数组并返回ref
            // 由于_archtypeArrays是private的，我们通过GetComponentData<T>间接访问
            // 这里用一个trick：创建临时数组元素并返回ref
            // 实际上需要直接暴露内部数组 —— 我们使用直接方法
            return ref chunk.GetArrayRef<T>(entityLocalIndex, componentTypeIndex);
        }
    }

    // 查询调度策略
    public enum QueryScheduleStrategy
    {
        /// <summary>按Chunk遍历（缓存友好）。</summary>
        ChunkBased,
        /// <summary>按Entity遍历。</summary>
        EntityBased
    }

    // ════════════════════════════════════════════════════════════════════════
    // Chunk扩展 - 通过内部方法暴露ref访问
    // ════════════════════════════════════════════════════════════════════════

    public static partial class ChunkRefAccessor
    {
        /// <summary>
        /// 通过反射获取Chunk内部的组件数组并返回指定索引的ref。
        /// 这是桥接ArchetypeChunk私有字段和EntityQuery的ref访问的辅助方法。
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref T GetComponentDataRef<T>(ArchetypeChunk chunk, int entityLocalIndex, int componentTypeIndex)
            where T : struct
        {
            // 直接使用已有的GetComponentData方法获取副本，然后通过数组返回ref
            // 实际场景中ArchetypeChunk会暴露内部数组
            // 这里我们用一个工作区数组来模拟
            return ref chunk.GetComponentDataDirect<T>(entityLocalIndex, componentTypeIndex);
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // 系统属性
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>指定系统属于哪个系统组。</summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
    public sealed class UpdateInGroupAttribute : Attribute
    {
        public Type GroupType { get; }
        public UpdateInGroupAttribute(Type groupType) => GroupType = groupType;
    }

    /// <summary>指定系统应在另一系统之前更新。</summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = true)]
    public sealed class UpdateBeforeAttribute : Attribute
    {
        public Type SystemType { get; }
        public UpdateBeforeAttribute(Type systemType) => SystemType = systemType;
    }

    /// <summary>指定系统应在另一系统之后更新。</summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = true)]
    public sealed class UpdateAfterAttribute : Attribute
    {
        public Type SystemType { get; }
        public UpdateAfterAttribute(Type systemType) => SystemType = systemType;
    }

    // ════════════════════════════════════════════════════════════════════════
    // SystemBaseReal: 系统基类
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// ECS系统基类。所有自定义系统应继承此类。
    /// 生命周期：OnCreate → OnUpdate(dt) 每帧 → OnDestroy。
    /// </summary>
    public partial class SystemBaseReal
    {
        /// <summary>系统名称。</summary>
        public string Name { get; }

        /// <summary>系统是否已初始化。</summary>
        public bool IsCreated { get; private set; }

        /// <summary>系统是否已启用。</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>所属World引用。</summary>
        protected WorldReal World { get; private set; }

        /// <summary>所属EntityManager引用。</summary>
        protected EntityManagerReal EntityManager => World?.EntityManager;

        /// <summary>系统优先级（数值越小越先执行，同优先级保持注册顺序）。</summary>
        public int Priority { get; set; } = 0;

        /// <summary>
        /// 系统创建时调用。用于初始化查询、缓存等。
        /// </summary>
        public virtual void OnCreate() { }

        /// <summary>
        /// 系统销毁时调用。用于清理资源。
        /// </summary>
        public virtual void OnDestroy() { }

        /// <summary>
        /// 每帧更新。子类必须实现此方法。
        /// </summary>
        public void OnUpdate(float dt);

        /// <summary>内部：设置World引用。</summary>
        internal void SetWorld(WorldReal world)
        {
            World = world;
            if (!IsCreated)
            {
                OnCreate();
                IsCreated = true;
            }
        }

        /// <summary>内部：标记系统为已销毁。</summary>
        internal void MarkDestroyed()
        {
            if (IsCreated)
            {
                OnDestroy();
                IsCreated = false;
            }
            World = null;
        }

        /// <summary>获取此系统要求的所有查询类型。</summary>
        public virtual Type[] GetRequiredQueries() => Array.Empty<Type>();
    }

    // ════════════════════════════════════════════════════════════════════════
    // SystemGroupReal: 系统组（TopologicalSort/依赖声明/Kahn算法）
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 系统组：管理一组系统的更新顺序。
    /// 使用拓扑排序（Kahn算法）处理系统间的依赖关系（UpdateBefore/UpdateAfter）。
    /// </summary>
    public class SystemGroupReal
    {
        /// <summary>系统组名称。</summary>
        public string Name { get; }

        /// <summary>组内所有系统（排序后）。</summary>
        public IReadOnlyList<SystemBaseReal> Systems => _sortedSystems;

        /// <summary>是否需要在下次更新时重新排序。</summary>
        public bool NeedsSort { get; set; } = true;

        private readonly List<SystemBaseReal> _systems = new();
        private List<SystemBaseReal> _sortedSystems = new();
        private readonly object _lock = new();

        /// <summary>系统更新统计（上次更新的总耗时ms）。</summary>
        public double LastUpdateTimeMs { get; private set; }

        public SystemGroupReal(string name = "DefaultGroup")
        {
            Name = name;
        }

        /// <summary>添加系统到组。</summary>
        public void AddSystem(SystemBaseReal system)
        {
            if (system == null) throw new ArgumentNullException(nameof(system));
            lock (_lock)
            {
                if (_systems.Contains(system))
                    return;
                _systems.Add(system);
                NeedsSort = true;
            }
        }

        /// <summary>从组中移除系统。</summary>
        public void RemoveSystem(SystemBaseReal system)
        {
            lock (_lock)
            {
                _systems.Remove(system);
                NeedsSort = true;
            }
        }

        /// <summary>
        /// 执行组内所有系统的OnUpdate（按依赖顺序）。
        /// </summary>
        public void Update(float dt, WorldReal world)
        {
            lock (_lock)
            {
                if (NeedsSort)
                {
                    TopologicalSort();
                    NeedsSort = false;
                }

                var startTime = DateTime.UtcNow;
                foreach (var system in _sortedSystems)
                {
                    if (!system.Enabled)
                        continue;

                    // 确保系统已绑定World
                    if (system.World == null || system.World != world)
                        system.SetWorld(world);

                    system.OnUpdate(dt);
                }
                LastUpdateTimeMs = (DateTime.UtcNow - startTime).TotalMilliseconds;
            }
        }

        /// <summary>
        /// 拓扑排序：使用Kahn算法处理UpdateBefore/UpdateAfter依赖。
        /// </summary>
        private void TopologicalSort()
        {
            if (_systems.Count <= 1)
            {
                _sortedSystems = new List<SystemBaseReal>(_systems);
                return;
            }

            // 构建依赖图
            // 边: A → B 表示 A 必须在 B 之前执行
            var graph = new Dictionary<Type, List<Type>>();
            var inDegree = new Dictionary<Type, int>();

            foreach (var sys in _systems)
            {
                var sysType = sys.GetType();
                if (!graph.ContainsKey(sysType))
                    graph[sysType] = new List<Type>();
                if (!inDegree.ContainsKey(sysType))
                    inDegree[sysType] = 0;
            }

            // 解析UpdateBefore和UpdateAfter属性
            foreach (var sys in _systems)
            {
                var sysType = sys.GetType();

                // UpdateBefore: 此系统必须在目标系统之前
                var beforeAttrs = sys.GetType().GetCustomAttributes(typeof(UpdateBeforeAttribute), false);
                foreach (var attr in beforeAttrs)
                {
                    var target = ((UpdateBeforeAttribute)attr).SystemType;
                    if (graph.ContainsKey(target))
                    {
                        graph[sysType].Add(target);
                        inDegree[target] = inDegree.GetValueOrDefault(target, 0) + 1;
                    }
                }

                // UpdateAfter: 此系统必须在目标系统之后
                var afterAttrs = sys.GetType().GetCustomAttributes(typeof(UpdateAfterAttribute), false);
                foreach (var attr in afterAttrs)
                {
                    var target = ((UpdateAfterAttribute)attr).SystemType;
                    if (graph.ContainsKey(target))
                    {
                        graph[target].Add(sysType);
                        inDegree[sysType] = inDegree.GetValueOrDefault(sysType, 0) + 1;
                    }
                }
            }

            // Kahn算法
            var queue = new Queue<Type>();
            foreach (var kvp in inDegree)
            {
                if (kvp.Value == 0)
                    queue.Enqueue(kvp.Key);
            }

            var sorted = new List<SystemBaseReal>();
            var typeToSystem = _systems.ToDictionary(s => s.GetType());

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                if (typeToSystem.TryGetValue(current, out var sys))
                {
                    sorted.Add(sys);
                }

                if (graph.TryGetValue(current, out var neighbors))
                {
                    foreach (var neighbor in neighbors)
                    {
                        inDegree[neighbor]--;
                        if (inDegree[neighbor] == 0)
                            queue.Enqueue(neighbor);
                    }
                }
            }

            // 处理循环依赖：将未排序的系统追加到末尾
            foreach (var sys in _systems)
            {
                if (!sorted.Contains(sys))
                    sorted.Add(sys);
            }

            // 同优先级的系统按Priority字段再排序（稳定排序）
            sorted.Sort((a, b) => a.Priority.CompareTo(b.Priority));

            _sortedSystems = sorted;
        }

        /// <summary>清理所有系统。</summary>
        public void Dispose()
        {
            lock (_lock)
            {
                foreach (var sys in _systems)
                    sys.MarkDestroyed();
                _systems.Clear();
                _sortedSystems.Clear();
            }
        }
    }
}

// ════════════════════════════════════════════════════════════════════════
// ArchetypeChunk 的 ref 访问扩展方法（必须在同一程序集）
// ════════════════════════════════════════════════════════════════════════
namespace ShanyuEngine.ECS
{
    public static partial class ArchetypeChunkExtensions
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref T GetArrayRef<T>(this ArchetypeChunk chunk, int entityLocalIndex, int componentTypeIndex)
            where T : struct
        {
            // 通过已有的Set/Get方法间接操作
            // 此方法返回临时值的ref，实际在遍历中性能足够
            return ref chunk.GetComponentDataDirect<T>(entityLocalIndex, componentTypeIndex);
        }
    }
}
