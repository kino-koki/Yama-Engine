using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

namespace YamaEngine.ECS
{
    // ════════════════════════════════════════════════════════════════════════
    // yama引擎 - 真实实体管理器 (EntityManagerReal)
    //
    // 实体标识: EntityReal (ID + Version + 生成计数)
    // 实体管理:
    //   * CreateEntity: 空闲列表或递增ID分配，Version++
    //   * DestroyEntity: 释放回空闲列表，从Archetype中swap-remove
    //   * AddComponent<T>: 原型迁移（添加类型T → 新Archetype → 数据迁移）
    //   * RemoveComponent<T>: 原型迁移（去掉类型T → 新Archetype → 数据迁移）
    //   * SetComponentData<T> / GetComponentData<T>: chunk偏移读写
    //   * HasComponent<T>: 查Archetype类型集合
    //   * CreateQuery(QueryDescription): 实体查询
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 实体标识：唯一ID + 版本号（用于检测过期引用）。
    /// </summary>
    public readonly struct EntityReal : IEquatable<EntityReal>
    {
        /// <summary>实体ID（在实体池中的索引）。</summary>
        public readonly int Id;

        /// <summary>版本号（每次销毁后递增，用于检测悬空引用）。</summary>
        public readonly uint Version;

        /// <summary>创建计数器（全局递增，用于调试和排序）。</summary>
        public readonly long Generation;

        public EntityReal(int id, uint version, long generation = 0)
        {
            Id = id;
            Version = version;
            Generation = generation;
        }

        /// <summary>空实体。</summary>
        public static readonly EntityReal Null = new EntityReal(0, 0, 0);

        /// <summary>是否为空实体。</summary>
        public bool IsNull => Id == 0;

        /// <summary>从int隐式转换为Entity（用于向后兼容）。</summary>
        public static implicit operator EntityReal(int id) => new EntityReal(id, 1, 0);

        /// <summary>从Entity（旧版）转换为EntityReal。</summary>
        public static implicit operator EntityReal(Entity legacy) => new EntityReal(legacy.Index, (uint)legacy.Generation, 0);

        /// <summary>转换为旧版Entity。</summary>
        public static explicit operator Entity(EntityReal e) => new Entity(e.Id, (int)e.Version);

        public bool Equals(EntityReal other) => Id == other.Id && Version == other.Version;
        public override bool Equals(object obj) => obj is EntityReal e && Equals(e);
        public override int GetHashCode() => HashCode.Combine(Id, Version);
        public static bool operator ==(EntityReal a, EntityReal b) => a.Equals(b);
        public static bool operator !=(EntityReal a, EntityReal b) => !a.Equals(b);
        public override string ToString() => $"EntityReal({Id}:v{Version},gen={Generation})";
    }

    /// <summary>
    /// 实体内部信息：存储每个实体的元数据。
    /// </summary>
    internal struct EntityRecord
    {
        /// <summary>版本号。</summary>
        public uint Version;

        /// <summary>实体所在的原型。</summary>
        public ArchetypeReal Archetype;

        /// <summary>实体所在的Chunk。</summary>
        public ArchetypeChunk Chunk;

        /// <summary>实体在Chunk中的本地索引。</summary>
        public int IndexInChunk;

        /// <summary>实体是否有效。</summary>
        public bool IsValid;

        /// <summary>创建计数。</summary>
        public long Generation;
    }

    /// <summary>
    /// 真实实体管理器：管理实体的创建、销毁、组件增删和数据读写。
    /// 基于Archetype存储模型，支持原型迁移。
    /// </summary>
    public partial class EntityManagerReal
    {
        // 实体记录数组
        private EntityRecord[] _entities;

        // 已分配的最高实体ID（不含空闲列表回收的）
        private int _maxEntityId;

        // 当前活跃实体数量
        private int _aliveCount;

        // 空闲ID列表（被销毁实体的ID可复用）
        private readonly Queue<int> _freeList = new();

        // 全局创建计数器
        private long _globalGeneration;

        // 原型管理器
        private readonly ArchetypeManager _archetypeManager;

        // 实体名称映射
        private readonly Dictionary<int, string> _entityNames = new();

        // 线程安全锁
        private readonly object _lock = new();

        /// <summary>当前活跃实体总数。</summary>
        public int AliveEntityCount => _aliveCount;

        /// <summary>原型管理器引用。</summary>
        public ArchetypeManager ArchetypeManager => _archetypeManager;

        /// <summary>实体创建事件。</summary>
        public event Action<EntityReal>? EntityCreated;

        /// <summary>实体销毁事件。</summary>
        public event Action<EntityReal>? EntityDestroyed;

        /// <summary>实体组件变更事件。</summary>
        public event Action<EntityReal, int>? EntityComponentChanged;

        public EntityManagerReal(int initialCapacity = 4096)
        {
            _entities = new EntityRecord[initialCapacity];
            _maxEntityId = 0;
            _aliveCount = 0;
            _globalGeneration = 0;
            _archetypeManager = new ArchetypeManager();
        }

        /// <summary>
        /// 创建新实体。优先从空闲列表回收ID，否则递增分配。
        /// </summary>
        public EntityReal CreateEntity(string name = null)
        {
            lock (_lock)
            {
                int id;

                if (_freeList.Count > 0)
                {
                    // 从空闲列表回收ID
                    id = _freeList.Dequeue();
                }
                else
                {
                    // 递增分配新ID
                    _maxEntityId++;
                    id = _maxEntityId;

                    // 扩容检查
                    if (id >= _entities.Length)
                    {
                        int newSize = Math.Max(_entities.Length * 2, id + 1024);
                        Array.Resize(ref _entities, newSize);
                    }
                }

                ref var record = ref _entities[id];
                record.Version++;
                record.IsValid = true;
                record.Generation = ++_globalGeneration;
                record.Archetype = null;
                record.Chunk = null;
                record.IndexInChunk = -1;

                var entity = new EntityReal(id, record.Version, record.Generation);

                if (name != null)
                    _entityNames[id] = name;
                else
                    _entityNames[id] = $"Entity_{id}";

                _aliveCount++;
                EntityCreated?.Invoke(entity);
                return entity;
            }
        }

        /// <summary>
        /// 销毁实体：释放回空闲列表，从Archetype中swap-remove。
        /// </summary>
        public void DestroyEntity(EntityReal entity)
        {
            if (entity.IsNull) return;

            lock (_lock)
            {
                if (!IsEntityValidInternal(entity))
                    return;

                ref var record = ref _entities[entity.Id];

                // 从Archetype Chunk中移除
                if (record.Chunk != null && record.Archetype != null)
                {
                    RemoveEntityFromChunk(entity.Id, record.IndexInChunk, record.Chunk, record.Archetype);
                }

                // 标记无效并递增版本
                record.IsValid = false;
                record.Version++;
                record.Archetype = null;
                record.Chunk = null;
                record.IndexInChunk = -1;

                // 放回空闲列表
                _freeList.Enqueue(entity.Id);
                _entityNames.Remove(entity.Id);
                _aliveCount--;

                EntityDestroyed?.Invoke(entity);
            }
        }

        /// <summary>
        /// 检查实体是否有效（ID在范围内 + 版本匹配 + 标记为有效）。
        /// </summary>
        public bool IsValid(EntityReal entity)
        {
            if (entity.IsNull) return false;
            lock (_lock)
            {
                return IsEntityValidInternal(entity);
            }
        }

        private bool IsEntityValidInternal(EntityReal entity)
        {
            if (entity.Id < 0 || entity.Id >= _entities.Length)
                return false;
            ref var record = ref _entities[entity.Id];
            return record.IsValid && record.Version == entity.Version;
        }

        /// <summary>
        /// 获取实体名称。
        /// </summary>
        public string GetName(EntityReal entity)
        {
            if (entity.IsNull) return "NullEntity";
            lock (_lock)
            {
                return _entityNames.TryGetValue(entity.Id, out var name) ? name : "Entity";
            }
        }

        /// <summary>
        /// 设置实体名称。
        /// </summary>
        public void SetName(EntityReal entity, string name)
        {
            if (entity.IsNull) return;
            lock (_lock)
            {
                _entityNames[entity.Id] = name;
            }
        }

        // ═══════════════════════════════════════════════════════════════════
        // 组件管理
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// 为实体添加组件（指定默认值）。
        /// 流程：查找当前Archetype → 计算新Archetype(添加类型T) → 迁移数据 → 设置新组件值。
        /// </summary>
        public void AddComponent<T>(EntityReal entity, T component) where T : struct
        {
            lock (_lock)
            {
                if (!IsEntityValidInternal(entity))
                    throw new InvalidOperationException($"Entity {entity} is not valid.");

                var ct = ComponentTypeRealCache.Get<T>();
                ref var record = ref _entities[entity.Id];

                // 如果已有此组件，直接更新
                if (record.Archetype != null && record.Archetype.HasComponent(ct.TypeIndex))
                {
                    int slotIdx = record.Archetype.GetComponentTypeIndex(ct.TypeIndex);
                    record.Chunk.SetComponentData(record.IndexInChunk, slotIdx, component);
                    EntityComponentChanged?.Invoke(entity, ct.TypeIndex);
                    return;
                }

                // 计算目标Archetype
                var currentArchetype = record.Archetype;
                var currentChunk = record.Chunk;
                int currentIndex = record.IndexInChunk;

                var targetArchetype = ComputeTargetArchetypeAdd(currentArchetype, ct);

                if (targetArchetype == null)
                {
                    // 没有任何组件，创建新Archetype
                    targetArchetype = _archetypeManager.GetOrCreateArchetype(ct);
                }

                var targetChunk = targetArchetype.AcquireChunk();
                int targetIndex;

                if (currentArchetype != null)
                {
                    // 拷贝共有组件数据到新Chunk
                    targetIndex = targetChunk.CopyEntityFrom(currentChunk, currentIndex, entity);

                    // 从旧Archetype移除
                    RemoveEntityFromChunk(entity.Id, currentIndex, currentChunk, currentArchetype);
                }
                else
                {
                    // 实体还没有任何组件，直接添加
                    targetIndex = targetChunk.AddEntity(entity);
                }

                // 设置新组件数据
                int newSlotIdx = targetArchetype.GetComponentTypeIndex(ct.TypeIndex);
                targetChunk.SetComponentData(targetIndex, newSlotIdx, component);

                // 更新实体记录
                record.Archetype = targetArchetype;
                record.Chunk = targetChunk;
                record.IndexInChunk = targetIndex;
                targetArchetype.IncrementEntityCount();

                EntityComponentChanged?.Invoke(entity, ct.TypeIndex);
            }
        }

        /// <summary>
        /// 为实体添加组件（使用默认值）。
        /// </summary>
        public void AddComponent<T>(EntityReal entity) where T : struct
        {
            AddComponent(entity, default(T));
        }

        /// <summary>
        /// 从实体移除组件。
        /// 流程：查找当前Archetype → 计算新Archetype(去掉类型T) → 迁移数据。
        /// </summary>
        public bool RemoveComponent<T>(EntityReal entity) where T : struct
        {
            lock (_lock)
            {
                if (!IsEntityValidInternal(entity))
                    return false;

                var ct = ComponentTypeRealCache.Get<T>();
                ref var record = ref _entities[entity.Id];

                if (record.Archetype == null || !record.Archetype.HasComponent(ct.TypeIndex))
                    return false;

                var currentArchetype = record.Archetype;
                var currentChunk = record.Chunk;
                int currentIndex = record.IndexInChunk;

                // 计算目标Archetype（去掉类型T）
                var newTypes = new List<ComponentTypeReal>();
                foreach (var existingCt in currentArchetype.ComponentTypes)
                {
                    if (existingCt.TypeIndex != ct.TypeIndex)
                        newTypes.Add(existingCt);
                }

                ArchetypeReal targetArchetype = null;
                ArchetypeChunk targetChunk = null;
                int targetIndex = -1;

                if (newTypes.Count > 0)
                {
                    targetArchetype = _archetypeManager.GetOrCreateArchetype(newTypes);
                    targetChunk = targetArchetype.AcquireChunk();
                    targetIndex = targetChunk.CopyEntityFrom(currentChunk, currentIndex, entity);
                    targetArchetype.IncrementEntityCount();
                }

                // 从旧Archetype移除
                RemoveEntityFromChunk(entity.Id, currentIndex, currentChunk, currentArchetype);

                // 更新记录
                record.Archetype = targetArchetype;
                record.Chunk = targetChunk;
                record.IndexInChunk = targetIndex;

                EntityComponentChanged?.Invoke(entity, ct.TypeIndex);
                return true;
            }
        }

        /// <summary>
        /// 设置实体的组件数据（找到chunk+偏移→写入）。
        /// </summary>
        public void SetComponentData<T>(EntityReal entity, T data) where T : struct
        {
            lock (_lock)
            {
                if (!IsEntityValidInternal(entity))
                    throw new InvalidOperationException($"Entity {entity} is not valid.");

                var ct = ComponentTypeRealCache.Get<T>();
                ref var record = ref _entities[entity.Id];

                if (record.Archetype == null || !record.Archetype.HasComponent(ct.TypeIndex))
                    throw new InvalidOperationException($"Entity {entity} does not have component {typeof(T).Name}.");

                int slotIdx = record.Archetype.GetComponentTypeIndex(ct.TypeIndex);
                record.Chunk.SetComponentData(record.IndexInChunk, slotIdx, data);
            }
        }

        /// <summary>
        /// 读取实体的组件数据（找到chunk+偏移→读取）。
        /// </summary>
        public T GetComponentData<T>(EntityReal entity) where T : struct
        {
            lock (_lock)
            {
                if (!IsEntityValidInternal(entity))
                    throw new InvalidOperationException($"Entity {entity} is not valid.");

                var ct = ComponentTypeRealCache.Get<T>();
                ref var record = ref _entities[entity.Id];

                if (record.Archetype == null || !record.Archetype.HasComponent(ct.TypeIndex))
                    throw new InvalidOperationException($"Entity {entity} does not have component {typeof(T).Name}.");

                int slotIdx = record.Archetype.GetComponentTypeIndex(ct.TypeIndex);
                return record.Chunk.GetComponentData<T>(record.IndexInChunk, slotIdx);
            }
        }

        /// <summary>
        /// 尝试读取实体的组件数据。
        /// </summary>
        public bool TryGetComponentData<T>(EntityReal entity, out T data) where T : struct
        {
            lock (_lock)
            {
                data = default;
                if (!IsEntityValidInternal(entity))
                    return false;

                var ct = ComponentTypeRealCache.Get<T>();
                ref var record = ref _entities[entity.Id];

                if (record.Archetype == null || !record.Archetype.HasComponent(ct.TypeIndex))
                    return false;

                int slotIdx = record.Archetype.GetComponentTypeIndex(ct.TypeIndex);
                data = record.Chunk.GetComponentData<T>(record.IndexInChunk, slotIdx);
                return true;
            }
        }

        /// <summary>
        /// 检查实体是否拥有指定组件（查Archetype的类型集合）。
        /// </summary>
        public bool HasComponent<T>(EntityReal entity) where T : struct
        {
            if (entity.IsNull) return false;
            lock (_lock)
            {
                if (!IsEntityValidInternal(entity))
                    return false;

                var ct = ComponentTypeRealCache.Get<T>();
                ref var record = ref _entities[entity.Id];
                return record.Archetype != null && record.Archetype.HasComponent(ct.TypeIndex);
            }
        }

        /// <summary>
        /// 获取组件类型索引。
        /// </summary>
        public int GetComponentTypeIndex<T>() where T : struct
        {
            return ComponentTypeRealCache.Get<T>().TypeIndex;
        }

        /// <summary>
        /// 获取实体拥有的所有组件类型。
        /// </summary>
        public IReadOnlyList<ComponentTypeReal> GetComponentTypes(EntityReal entity)
        {
            lock (_lock)
            {
                if (!IsEntityValidInternal(entity))
                    return Array.Empty<ComponentTypeReal>();

                ref var record = ref _entities[entity.Id];
                return record.Archetype?.ComponentTypes ?? Array.Empty<ComponentTypeReal>();
            }
        }

        // ═══════════════════════════════════════════════════════════════════
        // 实体查询
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// 创建实体查询。
        /// </summary>
        public EntityQueryReal CreateQuery(params QueryDescription[] description)
        {
            return new EntityQueryReal(this, description);
        }

        /// <summary>
        /// 创建实体查询（使用All/Any/None描述）。
        /// </summary>
        public EntityQueryReal CreateQuery(
            IReadOnlyList<Type> allTypes = null,
            IReadOnlyList<Type> anyTypes = null,
            IReadOnlyList<Type> noneTypes = null)
        {
            return new EntityQueryReal(this, allTypes, anyTypes, noneTypes);
        }

        // ═══════════════════════════════════════════════════════════════════
        // 内部方法
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// 计算添加组件后的目标Archetype。
        /// </summary>
        private ArchetypeReal ComputeTargetArchetypeAdd(ArchetypeReal current, ComponentTypeReal toAdd)
        {
            if (current == null)
                return null;

            return _archetypeManager.GetAddMigrationTarget(current, toAdd);
        }

        /// <summary>
        /// 从Chunk中移除实体（swap-remove），更新被移动实体的位置。
        /// </summary>
        private void RemoveEntityFromChunk(
            int entityId, int localIndex,
            ArchetypeChunk chunk, ArchetypeReal archetype)
        {
            int oldCount = chunk.EntityCount;
            EntityReal movedEntity = chunk.RemoveEntity(localIndex);
            archetype.DecrementEntityCount();

            // 如果swap了实体，更新被移动实体的记录
            if (movedEntity.Id != 0 && localIndex < oldCount - 1)
            {
                ref var movedRecord = ref _entities[movedEntity.Id];
                if (movedRecord.IsValid)
                {
                    movedRecord.IndexInChunk = localIndex;
                    movedRecord.Chunk = chunk;
                }
            }

            // 释放空Chunk
            archetype.ReleaseChunkIfEmpty(chunk);
        }

        /// <summary>获取所有已注册的Archetype。</summary>
        public IReadOnlyList<ArchetypeReal> GetAllArchetypes()
        {
            return _archetypeManager.GetAllArchetypes();
        }

        /// <summary>释放所有资源。</summary>
        public void Dispose()
        {
            lock (_lock)
            {
                _archetypeManager.Dispose();
                _entities = new EntityRecord[1024];
                _maxEntityId = 0;
                _aliveCount = 0;
                _globalGeneration = 0;
                _freeList.Clear();
                _entityNames.Clear();
                EntityCreated = null;
                EntityDestroyed = null;
                EntityComponentChanged = null;
            }
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // 查询描述（QueryDescription）
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 实体查询描述：定义All/Any/None组件过滤条件。
    /// </summary>
    public class QueryDescription
    {
        /// <summary>必须包含的组件类型。</summary>
        public List<Type> All { get; } = new();

        /// <summary>任一包含的组件类型。</summary>
        public List<Type> Any { get; } = new();

        /// <summary>排除的组件类型。</summary>
        public List<Type> None { get; } = new();

        public QueryDescription AllOf<T>() where T : struct { All.Add(typeof(T)); return this; }
        public QueryDescription AnyOf<T>() where T : struct { Any.Add(typeof(T)); return this; }
        public QueryDescription NoneOf<T>() where T : struct { None.Add(typeof(T)); return this; }
    }
}
