using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace YamaEngine.ECS
{
    // ════════════════════════════════════════════════════════════════════════
    // yama引擎 - 真实Archetype ECS核心 (ArchetypeECSReal)
    //
    // 类似 Unity DOTS/ECS 风格的 Archetype 存储:
    //   * ComponentType / ComponentType<T>: 组件类型描述（TypeIndex/Size/StableTypeHash）
    //   * Archetype: 原型（组件类型集合 → Chunk列表）
    //   * ArchetypeChunk: 固定大小内存块（16KB，AoS布局）
    //   * ArchetypeManager: 原型管理器（创建/查找/缓存/迁移图）
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 组件类型描述。包含类型索引、大小和稳定哈希。
    /// </summary>
    public readonly struct ComponentTypeReal : IEquatable<ComponentTypeReal>
    {
        public readonly int TypeIndex;
        public readonly int Size;
        public readonly Type Type;
        public readonly bool IsZeroSized;
        public readonly long StableTypeHash;

        internal ComponentTypeReal(int typeIndex, int size, Type type, bool isZeroSized, long stableTypeHash)
        {
            TypeIndex = typeIndex;
            Size = size;
            Type = type;
            IsZeroSized = isZeroSized;
            StableTypeHash = stableTypeHash;
        }

        public bool Equals(ComponentTypeReal other) => TypeIndex == other.TypeIndex;
        public override bool Equals(object obj) => obj is ComponentTypeReal ct && Equals(ct);
        public override int GetHashCode() => TypeIndex;
        public static bool operator ==(ComponentTypeReal a, ComponentTypeReal b) => a.Equals(b);
        public static bool operator !=(ComponentTypeReal a, ComponentTypeReal b) => !a.Equals(b);
        public override string ToString() => $"CT[{Type?.Name ?? "null"}](Idx={TypeIndex},Size={Size})";
    }

    /// <summary>
    /// 组件类型泛型辅助，缓存hash和大小。
    /// </summary>
    public static class ComponentTypeRealCache
    {
        private static readonly Dictionary<Type, ComponentTypeReal> s_typeMap = new();
        private static int s_nextTypeIndex = 1;
        private static readonly object s_lock = new();

        /// <summary>获取或注册组件类型的元信息。</summary>
        public static ComponentTypeReal Get<T>() where T : struct
        {
            Type type = typeof(T);
            lock (s_lock)
            {
                if (s_typeMap.TryGetValue(type, out var ct))
                    return ct;

                int size = Unsafe.SizeOf<T>();
                bool isZeroSized = size == 0;
                long stableHash = ComputeStableTypeHash(type);
                var newCt = new ComponentTypeReal(s_nextTypeIndex++, size, type, isZeroSized, stableHash);
                s_typeMap[type] = newCt;
                return newCt;
            }
        }

        /// <summary>通过 Type 获取组件类型元信息。</summary>
        public static ComponentTypeReal Get(Type type)
        {
            lock (s_lock)
            {
                if (s_typeMap.TryGetValue(type, out var ct))
                    return ct;

                int size;
                bool isZeroSized = false;
                if (type.IsValueType && !type.IsPrimitive)
                {
                    size = type.IsLayoutSequential || type.IsExplicitLayout
                        ? Marshal.SizeOf(type)
                        : Unsafe.SizeOf<object>(); // fallback
                    isZeroSized = size == 1; // struct with no fields = 1 byte
                }
                else if (type.IsPrimitive)
                {
                    size = Marshal.SizeOf(type);
                }
                else
                {
                    size = IntPtr.Size; // reference type = pointer size
                }

                long stableHash = ComputeStableTypeHash(type);
                var newCt = new ComponentTypeReal(s_nextTypeIndex++, size, type, isZeroSized, stableHash);
                s_typeMap[type] = newCt;
                return newCt;
            }
        }

        /// <summary>计算类型的稳定哈希（基于程序集+命名空间+名称）。</summary>
        private static long ComputeStableTypeHash(Type type)
        {
            unchecked
            {
                long hash = 17;
                if (type.Assembly != null)
                {
                    foreach (char c in type.Assembly.FullName)
                        hash = hash * 31 + c;
                }
                foreach (char c in type.FullName ?? type.Name)
                    hash = hash * 31 + c;
                return hash;
            }
        }

        public static IReadOnlyDictionary<Type, ComponentTypeReal> AllTypes => s_typeMap;
        public static void Reset() { lock (s_lock) { s_typeMap.Clear(); s_nextTypeIndex = 1; } }
    }

    /// <summary>
    /// 类型签名，用于Archetype查找的键。
    /// </summary>
    public class TypeSet : IEquatable<TypeSet>
    {
        public readonly int[] SortedTypeIndices;
        private int _cachedHashCode;

        public TypeSet(IReadOnlyList<ComponentTypeReal> types)
        {
            SortedTypeIndices = new int[types.Count];
            for (int i = 0; i < types.Count; i++)
                SortedTypeIndices[i] = types[i].TypeIndex;
            Array.Sort(SortedTypeIndices);
            _cachedHashCode = ComputeHash();
        }

        private int ComputeHash()
        {
            int hash = 0;
            foreach (int idx in SortedTypeIndices)
                hash = hash * 31 + idx;
            return hash;
        }

        public bool Equals(TypeSet other)
        {
            if (other == null || SortedTypeIndices.Length != other.SortedTypeIndices.Length)
                return false;
            for (int i = 0; i < SortedTypeIndices.Length; i++)
            {
                if (SortedTypeIndices[i] != other.SortedTypeIndices[i])
                    return false;
            }
            return true;
        }

        public override bool Equals(object obj) => obj is TypeSet ts && Equals(ts);
        public override int GetHashCode() => _cachedHashCode;
    }

    // ════════════════════════════════════════════════════════════════════════
    // ArchetypeChunk: 固定大小内存块 (16KB)
    //
    // 数据布局 (AoS - Array of Structures):
    //   [Entity0_CompA][Entity0_CompB]...[Entity1_CompA][Entity1_CompB]...
    //
    // 内存布局:
    //   EntityArray (Entity*sizeof) | CompA Array (N * sizeA) | CompB Array (N * sizeB) | ...
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 原型块：固定16KB的内存块，存储同一Archetype的实体数据。
    /// 采用 SoA（Structure of Arrays）布局以优化缓存命中率。
    /// </summary>
    public partial class ArchetypeChunk : IDisposable
    {
        /// <summary>块大小：16KB。</summary>
        public const int CHUNK_SIZE = 16 * 1024;

        /// <summary>当前块中存储的实体数量。</summary>
        public int EntityCount { get; internal set; }

        /// <summary>块所属的原型。</summary>
        public readonly ArchetypeReal Archetype;

        /// <summary>块能容纳的最大实体数量。</summary>
        public readonly int EntityCapacity;

        // 实体数组（每个实体的Entity标识）
        private readonly EntityReal[] _entities;

        // 各组件的独立数组（SoA布局），使用 object 存储泛型数组
        private readonly Array[] _componentArrays;

        // 各组件在 SoA 布局中的字节偏移（用于指针计算）
        private readonly int[] _componentOffsets;

        // 原始内存缓冲区（byte*），仅用于unsafe路径
        private IntPtr _buffer;
        private int _bufferSize;
        private bool _disposed;

        public ArchetypeChunk(ArchetypeReal archetype)
        {
            Archetype = archetype;

            // 计算每个实体占用的总字节数（SoA布局下每组件一行）
            int stride = 0;
            var offsets = new int[archetype.ComponentTypes.Count];
            for (int i = 0; i < archetype.ComponentTypes.Count; i++)
            {
                offsets[i] = stride;
                int compSize = archetype.ComponentTypes[i].IsZeroSized ? 0 : archetype.ComponentTypes[i].Size;
                stride += compSize;
            }
            _componentOffsets = offsets;

            // 计算能容纳多少实体（基于总缓冲区大小）
            // 实体ID数组也需要空间
            int entityArraySize = Unsafe.SizeOf<EntityReal>(); // 不用，下面用托管数组
            // 简化：EntityCapacity = CHUNK_SIZE / (stride + sizeof(EntityReal))，至少1
            if (stride == 0)
                stride = 1; // 零大小组件的退化处理

            EntityCapacity = Math.Max(1, CHUNK_SIZE / (stride + sizeof(int))); // sizeof EntityReal ≈ 8
            _entities = new EntityReal[EntityCapacity];
            _componentArrays = new Array[archetype.ComponentTypes.Count];

            // 为每个组件类型创建托管数组
            for (int i = 0; i < archetype.ComponentTypes.Count; i++)
            {
                var ct = archetype.ComponentTypes[i];
                if (ct.IsZeroSized)
                {
                    _componentArrays[i] = Array.CreateInstance(typeof(byte), 0);
                }
                else
                {
                    _componentArrays[i] = Array.CreateInstance(ct.Type, EntityCapacity);
                }
            }

            // 分配非托管缓冲区（用于 unsafe 读写路径）
            _bufferSize = stride * EntityCapacity;
            if (_bufferSize > 0)
            {
                _buffer = Marshal.AllocHGlobal(_bufferSize);
                // 清零
                unsafe { Unsafe.InitBlockUnaligned((void*)_buffer, 0, (uint)_bufferSize); }
            }
        }

        /// <summary>块是否已满。</summary>
        public bool IsFull => EntityCount >= EntityCapacity;

        /// <summary>块是否为空。</summary>
        public bool IsEmpty => EntityCount == 0;

        /// <summary>获取指定组件类型在chunk中的字节偏移。</summary>
        public int GetComponentOffset(int componentTypeIndex)
        {
            if (componentTypeIndex < 0 || componentTypeIndex >= _componentOffsets.Length)
                return -1;
            return _componentOffsets[componentTypeIndex];
        }

        /// <summary>获取实体在chunk中的全局索引。</summary>
        public int GetLocalIndex(EntityReal entity)
        {
            for (int i = 0; i < EntityCount; i++)
            {
                if (_entities[i].Id == entity.Id && _entities[i].Version == entity.Version)
                    return i;
            }
            return -1;
        }

        /// <summary>
        /// 获取组件数据（托管数组路径）。通过类型索引获取。
        /// </summary>
        public T GetComponentData<T>(int entityLocalIndex, int componentTypeIndex) where T : struct
        {
            if (entityLocalIndex < 0 || entityLocalIndex >= EntityCount)
                throw new ArgumentOutOfRangeException(nameof(entityLocalIndex));
            if (componentTypeIndex < 0 || componentTypeIndex >= _componentArrays.Length)
                throw new ArgumentOutOfRangeException(nameof(componentTypeIndex));
            return ((T[])_componentArrays[componentTypeIndex])[entityLocalIndex];
        }

        /// <summary>
        /// 设置组件数据（托管数组路径）。
        /// </summary>
        public void SetComponentData<T>(int entityLocalIndex, int componentTypeIndex, in T value) where T : struct
        {
            if (entityLocalIndex < 0 || entityLocalIndex >= EntityCount)
                throw new ArgumentOutOfRangeException(nameof(entityLocalIndex));
            if (componentTypeIndex < 0 || componentTypeIndex >= _componentArrays.Length)
                throw new ArgumentOutOfRangeException(nameof(componentTypeIndex));
            ((T[])_componentArrays[componentTypeIndex])[entityLocalIndex] = value;
        }

        /// <summary>
        /// 获取组件数据的原始指针（unsafe路径）。
        /// </summary>
        public unsafe T* GetComponentDataPtr<T>(int entityLocalIndex, int componentTypeIndex) where T : struct
        {
            if (entityLocalIndex < 0 || entityLocalIndex >= EntityCount)
                throw new ArgumentOutOfRangeException(nameof(entityLocalIndex));
            int offset = _componentOffsets[componentTypeIndex];
            int byteOffset = offset + entityLocalIndex * Unsafe.SizeOf<T>();
            return (T*)((byte*)_buffer + byteOffset);
        }

        /// <summary>获取指定索引处的Entity。</summary>
        public EntityReal GetEntity(int localIndex)
        {
            if (localIndex < 0 || localIndex >= EntityCount)
                throw new ArgumentOutOfRangeException(nameof(localIndex));
            return _entities[localIndex];
        }

        /// <summary>
        /// 向块中添加实体，返回本地索引。
        /// </summary>
        internal int AddEntity(EntityReal entity)
        {
            if (IsFull)
                throw new InvalidOperationException("ArchetypeChunk is full, cannot add more entities.");
            int localIndex = EntityCount;
            _entities[localIndex] = entity;
            EntityCount++;
            return localIndex;
        }

        /// <summary>
        /// 从块中移除实体（swap-remove 策略）。
        /// 将最后一个实体移动到被移除位置以保持紧凑。
        /// </summary>
        internal EntityReal RemoveEntity(int localIndex)
        {
            if (localIndex < 0 || localIndex >= EntityCount)
                throw new ArgumentOutOfRangeException(nameof(localIndex));

            EntityReal movedEntity = default;
            int lastIndex = EntityCount - 1;

            if (localIndex != lastIndex)
            {
                // 交换：将最后一个实体移到被移除的位置
                movedEntity = _entities[lastIndex];
                _entities[localIndex] = movedEntity;

                // 迁移各组件数据
                for (int i = 0; i < _componentArrays.Length; i++)
                {
                    if (Archetype.ComponentTypes[i].IsZeroSized)
                        continue;
                    var arr = _componentArrays[i];
                    object lastValue = arr.GetValue(lastIndex);
                    arr.SetValue(lastValue, localIndex);
                }
            }

            // 清除最后一个位置
            _entities[lastIndex] = default;
            EntityCount--;
            return movedEntity;
        }

        /// <summary>
        /// 从另一个chunk拷贝实体的所有组件数据到此chunk。
        /// </summary>
        internal int CopyEntityFrom(ArchetypeChunk sourceChunk, int sourceLocalIndex, EntityReal newEntity)
        {
            int destLocalIndex = AddEntity(newEntity);

            for (int i = 0; i < Archetype.ComponentTypes.Count; i++)
            {
                int srcCompIdx = sourceChunk.Archetype.GetComponentTypeIndex(Archetype.ComponentTypes[i].TypeIndex);
                if (srcCompIdx < 0)
                    continue; // 新组件不拷贝

                if (Archetype.ComponentTypes[i].IsZeroSized)
                    continue;

                var srcArr = sourceChunk._componentArrays[srcCompIdx];
                var dstArr = _componentArrays[i];
                dstArr.SetValue(srcArr.GetValue(sourceLocalIndex), destLocalIndex);
            }

            return destLocalIndex;
        }

        /// <summary>
        /// 获取chunk中从startIndex开始到末尾的所有实体。
        /// </summary>
        public void GetEntitiesInRange(int startIndex, List<EntityReal> result)
        {
            for (int i = startIndex; i < EntityCount; i++)
                result.Add(_entities[i]);
        }

        /// <summary>
        /// 获取组件数据的ref引用（通过内部数组桥接）。
        /// </summary>
        public ref T GetComponentDataDirect<T>(int entityLocalIndex, int componentTypeIndex) where T : struct
        {
            if (entityLocalIndex < 0 || entityLocalIndex >= EntityCount)
                throw new ArgumentOutOfRangeException(nameof(entityLocalIndex));
            if (componentTypeIndex < 0 || componentTypeIndex >= _componentArrays.Length)
                throw new ArgumentOutOfRangeException(nameof(componentTypeIndex));
            // 通过Array.GetValue/SetValue桥接 — 返回拷贝的ref
            // 注意：此方法返回的是拷贝的ref，在callback中使用时语义正确
            // 真实的高性能实现会使用 unsafe fixed 数组指针
            T[] arr = (T[])_componentArrays[componentTypeIndex];
            return ref arr[entityLocalIndex];
        }

        public void Dispose()
        {
            if (_disposed) return;
            if (_buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_buffer);
                _buffer = IntPtr.Zero;
            }
            _disposed = true;
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // ArchetypeReal: 原型（组件类型集合 → Chunk列表）
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 原型：拥有相同组件类型集合的实体共享同一个Archetype。
    /// 组件类型按TypeIndex排序，确保查找一致性。
    /// </summary>
    public partial class ArchetypeReal
    {
        /// <summary>排序后的组件类型数组。</summary>
        public readonly IReadOnlyList<ComponentTypeReal> ComponentTypes;

        /// <summary>原型签名哈希。</summary>
        public readonly int ArchetypeHash;

        /// <summary>类型集合（用于字典键）。</summary>
        public readonly TypeSet TypeSet;

        /// <summary>原型中的Chunk列表。</summary>
        public IReadOnlyList<ArchetypeChunk> Chunks => _chunks;

        /// <summary>原型中的总实体数量。</summary>
        public int EntityCount { get; private set; }

        private readonly List<ArchetypeChunk> _chunks = new();
        private readonly Dictionary<int, int> _typeIndexToSlotIndex = new();

        public ArchetypeReal(IReadOnlyList<ComponentTypeReal> componentTypes)
        {
            // 按TypeIndex排序
            var sorted = componentTypes.OrderBy(ct => ct.TypeIndex).ToList();
            ComponentTypes = sorted;
            TypeSet = new TypeSet(sorted);

            // 计算哈希
            int hash = 17;
            foreach (var ct in sorted)
                hash = hash * 31 + ct.TypeIndex;
            ArchetypeHash = hash;

            // 建立TypeIndex → slot索引的映射
            for (int i = 0; i < sorted.Count; i++)
                _typeIndexToSlotIndex[sorted[i].TypeIndex] = i;
        }

        /// <summary>根据TypeIndex获取组件在Archetype中的slot索引。</summary>
        public int GetComponentTypeIndex(int typeIndex)
        {
            return _typeIndexToSlotIndex.TryGetValue(typeIndex, out var idx) ? idx : -1;
        }

        /// <summary>是否包含指定组件类型。</summary>
        public bool HasComponent(int typeIndex)
        {
            return _typeIndexToSlotIndex.ContainsKey(typeIndex);
        }

        /// <summary>是否包含所有指定组件类型。</summary>
        public bool HasAllComponents(int[] typeIndices)
        {
            foreach (var idx in typeIndices)
            {
                if (!_typeIndexToSlotIndex.ContainsKey(idx))
                    return false;
            }
            return true;
        }

        /// <summary>是否包含任一指定组件类型。</summary>
        public bool HasAnyComponent(int[] typeIndices)
        {
            if (typeIndices == null || typeIndices.Length == 0)
                return true;
            foreach (var idx in typeIndices)
            {
                if (_typeIndexToSlotIndex.ContainsKey(idx))
                    return true;
            }
            return false;
        }

        /// <summary>是否不包含任何指定组件类型。</summary>
        public bool HasNoneComponents(int[] typeIndices)
        {
            if (typeIndices == null || typeIndices.Length == 0)
                return true;
            foreach (var idx in typeIndices)
            {
                if (_typeIndexToSlotIndex.ContainsKey(idx))
                    return false;
            }
            return true;
        }

        /// <summary>
        /// 判断此原型是否匹配查询条件。
        /// </summary>
        public bool MatchesQuery(int[] allTypes, int[] anyTypes, int[] noneTypes)
        {
            // None: 不能有任何排除的类型
            if (!HasNoneComponents(noneTypes))
                return false;

            // All: 必须包含所有必需的类型
            if (!HasAllComponents(allTypes))
                return false;

            // Any: 必须包含至少一个
            if (!HasAnyComponent(anyTypes))
                return false;

            return true;
        }

        /// <summary>
        /// 获取或创建一个非满的Chunk用于存放新实体。
        /// </summary>
        internal ArchetypeChunk AcquireChunk()
        {
            for (int i = 0; i < _chunks.Count; i++)
            {
                if (!_chunks[i].IsFull)
                    return _chunks[i];
            }

            var chunk = new ArchetypeChunk(this);
            _chunks.Add(chunk);
            return chunk;
        }

        /// <summary>增加实体计数。</summary>
        internal void IncrementEntityCount() => EntityCount++;

        /// <summary>减少实体计数。</summary>
        internal void DecrementEntityCount()
        {
            if (EntityCount > 0)
                EntityCount--;
        }

        /// <summary>如果Chunk为空则释放。</summary>
        internal void ReleaseChunkIfEmpty(ArchetypeChunk chunk)
        {
            if (chunk.IsEmpty)
            {
                chunk.Dispose();
                _chunks.Remove(chunk);
            }
        }

        /// <summary>根据实体在Archetype中的全局索引计算chunk索引和chunk内本地索引。</summary>
        public void GetChunkAndLocalIndex(int globalEntityIndex, out ArchetypeChunk chunk, out int localIndex)
        {
            int cumulative = 0;
            for (int i = 0; i < _chunks.Count; i++)
            {
                var c = _chunks[i];
                if (cumulative + c.EntityCount > globalEntityIndex)
                {
                    chunk = c;
                    localIndex = globalEntityIndex - cumulative;
                    return;
                }
                cumulative += c.EntityCount;
            }
            chunk = null;
            localIndex = -1;
        }

        /// <summary>获取实体在Archetype中的chunk索引。</summary>
        public int GetChunkIndex(int globalEntityIndex)
        {
            int cumulative = 0;
            for (int i = 0; i < _chunks.Count; i++)
            {
                cumulative += _chunks[i].EntityCount;
                if (cumulative > globalEntityIndex)
                    return i;
            }
            return -1;
        }

        /// <summary>获取每个chunk能容纳的实体数量。</summary>
        public int EntitiesPerChunk => _chunks.Count > 0 ? _chunks[0].EntityCapacity : 0;
    }

    // ════════════════════════════════════════════════════════════════════════
    // ArchetypeManager: 原型管理器（创建/查找/缓存/迁移图）
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 原型管理器：管理所有Archetype的创建、查找、缓存和迁移路径。
    /// </summary>
    public partial class ArchetypeManager
    {
        // TypeSet → Archetype 缓存
        private readonly Dictionary<TypeSet, ArchetypeReal> _archetypeCache = new();

        // 迁移图：添加组件时的迁移路径 (源TypeSet, 添加的TypeIndex) → 目标Archetype
        private readonly Dictionary<(TypeSet, int), ArchetypeReal> _addMigrationMap = new();

        // 迁移图：删除组件时的迁移路径 (源TypeSet, 删除的TypeIndex) → 目标Archetype
        private readonly Dictionary<(TypeSet, int), ArchetypeReal> _removeMigrationMap = new();

        private readonly object _lock = new();

        /// <summary>当前已创建的原型数量。</summary>
        public int ArchetypeCount => _archetypeCache.Count;

        /// <summary>
        /// 根据组件类型集合获取或创建Archetype。
        /// </summary>
        public ArchetypeReal GetOrCreateArchetype(IReadOnlyList<ComponentTypeReal> componentTypes)
        {
            var typeSet = new TypeSet(componentTypes);

            lock (_lock)
            {
                if (_archetypeCache.TryGetValue(typeSet, out var archetype))
                    return archetype;

                archetype = new ArchetypeReal(componentTypes);
                _archetypeCache[typeSet] = archetype;
                return archetype;
            }
        }

        /// <summary>
        /// 根据组件类型数组获取或创建Archetype。
        /// </summary>
        public ArchetypeReal GetOrCreateArchetype(params ComponentTypeReal[] componentTypes)
        {
            return GetOrCreateArchetype((IReadOnlyList<ComponentTypeReal>)componentTypes);
        }

        /// <summary>
        /// 添加组件时的原型迁移：返回添加指定组件类型后的新Archetype。
        /// </summary>
        public ArchetypeReal GetAddMigrationTarget(ArchetypeReal source, ComponentTypeReal componentToAdd)
        {
            lock (_lock)
            {
                var key = (source.TypeSet, componentToAdd.TypeIndex);
                if (_addMigrationMap.TryGetValue(key, out var target))
                    return target;

                // 构建新的组件类型集合
                var newTypes = new List<ComponentTypeReal>(source.ComponentTypes);
                newTypes.Add(componentToAdd);

                target = GetOrCreateArchetype(newTypes);
                _addMigrationMap[key] = target;
                return target;
            }
        }

        /// <summary>
        /// 删除组件时的原型迁移：返回删除指定组件类型后的新Archetype。
        /// </summary>
        public ArchetypeReal GetRemoveMigrationTarget(ArchetypeReal source, ComponentTypeReal componentToRemove)
        {
            lock (_lock)
            {
                var key = (source.TypeSet, componentToRemove.TypeIndex);
                if (_removeMigrationMap.TryGetValue(key, out var target))
                    return target;

                // 构建新的组件类型集合（移除目标类型）
                var newTypes = new List<ComponentTypeReal>();
                foreach (var ct in source.ComponentTypes)
                {
                    if (ct.TypeIndex != componentToRemove.TypeIndex)
                        newTypes.Add(ct);
                }

                target = newTypes.Count > 0
                    ? GetOrCreateArchetype(newTypes)
                    : null;

                _removeMigrationMap[key] = target;
                return target;
            }
        }

        /// <summary>
        /// 执行原型迁移：将实体从源Archetype迁移到目标Archetype。
        /// 拷贝共有组件数据，新组件用默认值填充。
        /// </summary>
        public static int MigrateEntity(
            ArchetypeChunk sourceChunk, int sourceLocalIndex,
            ArchetypeReal targetArchetype, EntityReal entity)
        {
            var targetChunk = targetArchetype.AcquireChunk();
            int targetLocalIndex = targetChunk.CopyEntityFrom(sourceChunk, sourceLocalIndex, entity);
            targetArchetype.IncrementEntityCount();
            return targetLocalIndex;
        }

        /// <summary>
        /// 查询所有匹配查询条件的Archetype。
        /// </summary>
        public List<ArchetypeReal> FindMatchingArchetypes(int[] allTypes, int[] anyTypes, int[] noneTypes)
        {
            var result = new List<ArchetypeReal>();
            lock (_lock)
            {
                foreach (var archetype in _archetypeCache.Values)
                {
                    if (archetype.MatchesQuery(allTypes, anyTypes, noneTypes))
                        result.Add(archetype);
                }
            }
            return result;
        }

        /// <summary>获取所有已注册的Archetype。</summary>
        public IReadOnlyList<ArchetypeReal> GetAllArchetypes()
        {
            lock (_lock)
            {
                return _archetypeCache.Values.ToList();
            }
        }

        /// <summary>获取两个原型之间的共有组件类型索引列表。</summary>
        public static List<int> GetSharedComponentIndices(ArchetypeReal a, ArchetypeReal b)
        {
            var shared = new List<int>();
            foreach (var ct in a.ComponentTypes)
            {
                if (b.HasComponent(ct.TypeIndex))
                    shared.Add(ct.TypeIndex);
            }
            return shared;
        }

        /// <summary>清理所有原型和迁移缓存。</summary>
        public void Dispose()
        {
            lock (_lock)
            {
                foreach (var archetype in _archetypeCache.Values)
                {
                    foreach (var chunk in archetype.Chunks)
                    {
                        chunk.Dispose();
                    }
                }
                _archetypeCache.Clear();
                _addMigrationMap.Clear();
                _removeMigrationMap.Clear();
            }
        }
    }
}
