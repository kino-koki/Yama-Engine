// ════════════════════════════════════════════════════════════════════════════
// 山雨引擎 - 稀疏集实体存储 (EntitySparseSet)
//
// 稀疏集(Sparse Set)是另一种 ECS 存储方案，和 Archetype 存储互补。
// 优点：添加/移除组件是 O(1)，不需要迁移 archetype。
// 缺点：遍历不如 Archetype 缓存友好。
//
// 适合场景：
//   * 频繁增删组件的实体
//   * 组件类型多但不常一起查询
//   * 管理/脚本系统实体
//
// 数据结构：
//   sparse: Entity.Id -> dense index (稀疏数组，大部分为空)
//   dense:  连续存储的实体和组件 (紧凑数组)
//   entities: dense 中对应的实体 ID
//
// 参考 EnTT (C++ ECS) 的 sparse set 实现。
//
// FIXME: 没做内存池化，频繁增删会 GC
// TODO: 加个版本号支持迭代时检测修改
// ════════════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace ShanyuEngine.ECS
{
    /// <summary>
    /// 稀疏集实体存储：用稀疏集存储实体和组件。
    /// </summary>
    public sealed class EntitySparseSet : IDisposable
    {
        // ──────────────────────────────────────────────────────────────────────
        // 字段
        // ──────────────────────────────────────────────────────────────────────

        // 稀疏数组：entity id -> dense index
        // 用分页来避免大数组分配
        private int[][] _sparsePages;
        private int _pageCount;
        private const int PageSize = 4096; // 每页 4096 个实体

        // 紧凑数组：实际存储
        private EntityReal[] _entities; // dense 中的实体
        private int _count;
        private int _capacity;

        // 版本号：每次实体被销毁后递增
        private int[] _versions;

        // 组件存储（按类型索引分）
        private readonly Dictionary<int, IComponentStorage> _componentStorages = new();

        // 空闲列表（回收的实体 id）
        private readonly Queue<int> _freeIds = new();

        // 统计
        public SparseSetStats Stats { get; } = new SparseSetStats();

        /// <summary>当前实体数</summary>
        public int Count => _count;

        /// <summary>容量</summary>
        public int Capacity => _capacity;

        /// <summary>已分配的实体 ID 数（包括已回收的）</summary>
        public int AllocatedIds => _pageCount * PageSize;

        /// <summary>空闲 ID 数</summary>
        public int FreeIdCount => _freeIds.Count;

        // ──────────────────────────────────────────────────────────────────────
        // 构造/析构
        // ──────────────────────────────────────────────────────────────────────

        public EntitySparseSet(int initialCapacity = 1024)
        {
            _capacity = initialCapacity;
            _entities = new EntityReal[initialCapacity];
            _versions = new int[initialCapacity];
            _pageCount = 0;
            _sparsePages = Array.Empty<int[]>();
            _count = 0;
        }

        public void Dispose()
        {
            _sparsePages = null;
            _entities = null;
            _versions = null;
            _componentStorages.Clear();
            _freeIds.Clear();
        }

        // ──────────────────────────────────────────────────────────────────────
        // 实体管理
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 创建一个新实体。
        /// </summary>
        public EntityReal CreateEntity()
        {
            int id;
            if (_freeIds.Count > 0)
            {
                // 回收已释放的 ID
                id = _freeIds.Dequeue();
                Stats.RecycledIds++;
            }
            else
            {
                // 分配新 ID
                id = Stats.TotalCreatedIds++;
                EnsureSparseCapacity(id + 1);
            }

            // 版本号（新创建为 1，回收的递增）
            int version = _versions[id];
            if (version == 0) version = 1;

            var entity = new EntityReal { Id = id, Version = version };

            // 添加到 dense
            int denseIndex = _count;
            EnsureDenseCapacity(denseIndex + 1);
            _entities[denseIndex] = entity;
            _versions[id] = version;
            SetSparse(id, denseIndex);
            _count++;

            Stats.TotalCreated++;
            Stats.CurrentAlive++;
            return entity;
        }

        /// <summary>
        /// 销毁一个实体。
        /// </summary>
        public bool DestroyEntity(EntityReal entity)
        {
            if (!Contains(entity))
                return false;

            int denseIndex = GetSparse(entity.Id);

            // 从所有组件存储中移除
            foreach (var storage in _componentStorages.Values)
            {
                storage.Remove(denseIndex);
            }

            // swap-remove：把最后一个移到被删的位置
            int lastIndex = _count - 1;
            if (denseIndex != lastIndex)
            {
                var lastEntity = _entities[lastIndex];
                _entities[denseIndex] = lastEntity;
                SetSparse(lastEntity.Id, denseIndex);

                // 迁移组件数据
                foreach (var storage in _componentStorages.Values)
                {
                    storage.SwapRemove(denseIndex, lastIndex);
                }
            }

            _count--;

            // 递增版本号使旧的 entity handle 失效
            _versions[entity.Id]++;
            _freeIds.Enqueue(entity.Id);

            Stats.TotalDestroyed++;
            Stats.CurrentAlive--;
            return true;
        }

        /// <summary>
        /// 检查实体是否存在。
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Contains(EntityReal entity)
        {
            if (entity.Id < 0 || entity.Id >= AllocatedIds)
                return false;
            if (_versions[entity.Id] != entity.Version)
                return false;
            int denseIndex = GetSparse(entity.Id);
            return denseIndex >= 0 && denseIndex < _count;
        }

        /// <summary>
        /// 获取所有存活的实体。
        /// </summary>
        public ReadOnlySpan<EntityReal> GetAllEntities()
        {
            return _entities.AsSpan(0, _count);
        }

        // ──────────────────────────────────────────────────────────────────────
        // 组件管理
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 添加组件到实体。
        /// </summary>
        public void AddComponent<T>(EntityReal entity, in T component) where T : struct
        {
            if (!Contains(entity))
                throw new ArgumentException($"实体 {entity} 不存在");

            int denseIndex = GetSparse(entity.Id);
            var storage = GetOrCreateStorage<T>();
            storage.Set(denseIndex, component);
            Stats.TotalComponentsAdded++;
        }

        /// <summary>
        /// 移除组件。
        /// </summary>
        public bool RemoveComponent<T>(EntityReal entity) where T : struct
        {
            if (!Contains(entity))
                return false;

            int typeIndex = ComponentTypeRealCache.Get<T>().TypeIndex;
            if (!_componentStorages.TryGetValue(typeIndex, out var storage))
                return false;

            int denseIndex = GetSparse(entity.Id);
            storage.Remove(denseIndex);
            Stats.TotalComponentsRemoved++;
            return true;
        }

        /// <summary>
        /// 获取组件数据。
        /// </summary>
        public ref T GetComponent<T>(EntityReal entity) where T : struct
        {
            if (!Contains(entity))
                throw new ArgumentException($"实体 {entity} 不存在");

            int typeIndex = ComponentTypeRealCache.Get<T>().TypeIndex;
            if (!_componentStorages.TryGetValue(typeIndex, out var storage))
                throw new InvalidOperationException($"实体没有组件 {typeof(T).Name}");

            int denseIndex = GetSparse(entity.Id);
            var typedStorage = (ComponentStorage<T>)storage;
            return ref typedStorage.Get(denseIndex);
        }

        /// <summary>
        /// 设置组件数据。
        /// </summary>
        public void SetComponent<T>(EntityReal entity, in T component) where T : struct
        {
            if (!Contains(entity))
                throw new ArgumentException($"实体 {entity} 不存在");

            int denseIndex = GetSparse(entity.Id);
            var storage = GetOrCreateStorage<T>();
            storage.Set(denseIndex, component);
            Stats.TotalComponentsSet++;
        }

        /// <summary>
        /// 检查是否有指定组件。
        /// </summary>
        public bool HasComponent<T>(EntityReal entity) where T : struct
        {
            if (!Contains(entity))
                return false;

            int typeIndex = ComponentTypeRealCache.Get<T>().TypeIndex;
            if (!_componentStorages.TryGetValue(typeIndex, out var storage))
                return false;

            int denseIndex = GetSparse(entity.Id);
            return storage.Has(denseIndex);
        }

        /// <summary>
        /// 获取所有有指定组件的实体。
        /// </summary>
        public List<EntityReal> GetEntitiesWith<T>() where T : struct
        {
            var result = new List<EntityReal>();
            int typeIndex = ComponentTypeRealCache.Get<T>().TypeIndex;
            if (!_componentStorages.TryGetValue(typeIndex, out var storage))
                return result;

            for (int i = 0; i < _count; i++)
            {
                if (storage.Has(i))
                    result.Add(_entities[i]);
            }
            return result;
        }

        /// <summary>
        /// 获取组件数组（只读）。
        /// </summary>
        public ReadOnlySpan<T> GetComponentData<T>() where T : struct
        {
            int typeIndex = ComponentTypeRealCache.Get<T>().TypeIndex;
            if (!_componentStorages.TryGetValue(typeIndex, out var storage))
                return ReadOnlySpan<T>.Empty;

            var typedStorage = (ComponentStorage<T>)storage;
            return typedStorage.AsReadOnlySpan(0, _count);
        }

        // ──────────────────────────────────────────────────────────────────────
        // 稀疏数组管理
        // ──────────────────────────────────────────────────────────────────────

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int GetSparse(int entityId)
        {
            int page = entityId / PageSize;
            int offset = entityId % PageSize;
            if (page >= _pageCount)
                return -1;
            return _sparsePages[page][offset];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void SetSparse(int entityId, int denseIndex)
        {
            int page = entityId / PageSize;
            int offset = entityId % PageSize;
            EnsureSparseCapacity(entityId + 1);
            _sparsePages[page][offset] = denseIndex;
        }

        private void EnsureSparseCapacity(int required)
        {
            int requiredPages = (required + PageSize - 1) / PageSize;
            if (requiredPages <= _pageCount)
                return;

            // 扩展页数组
            var newPages = new int[requiredPages][];
            if (_sparsePages != null)
                Array.Copy(_sparsePages, newPages, _pageCount);

            // 创建新页，初始化为 -1
            for (int i = _pageCount; i < requiredPages; i++)
            {
                newPages[i] = new int[PageSize];
                Array.Fill(newPages[i], -1);
            }

            _sparsePages = newPages;
            _pageCount = requiredPages;

            // 扩展版本号数组
            if (_versions.Length < requiredPages * PageSize)
            {
                var newVersions = new int[requiredPages * PageSize];
                Array.Copy(_versions, newVersions, _versions.Length);
                _versions = newVersions;
            }
        }

        private void EnsureDenseCapacity(int required)
        {
            if (required <= _capacity)
                return;

            int newCapacity = Math.Max(required, _capacity * 2);
            Array.Resize(ref _entities, newCapacity);
            _capacity = newCapacity;
        }

        // ──────────────────────────────────────────────────────────────────────
        // 组件存储管理
        // ──────────────────────────────────────────────────────────────────────

        private ComponentStorage<T> GetOrCreateStorage<T>() where T : struct
        {
            int typeIndex = ComponentTypeRealCache.Get<T>().TypeIndex;
            if (!_componentStorages.TryGetValue(typeIndex, out var storage))
            {
                storage = new ComponentStorage<T>(_capacity);
                _componentStorages[typeIndex] = storage;
            }
            return (ComponentStorage<T>)storage;
        }

        // ──────────────────────────────────────────────────────────────────────
        // 批量操作
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 遍历所有实体。
        /// </summary>
        public void ForEach(Action<EntityReal> action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            for (int i = 0; i < _count; i++)
            {
                action(_entities[i]);
            }
        }

        /// <summary>
        /// 遍历所有有指定组件的实体。
        /// </summary>
        public void ForEachWith<T>(Action<EntityReal, ref T> action) where T : struct
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            int typeIndex = ComponentTypeRealCache.Get<T>().TypeIndex;
            if (!_componentStorages.TryGetValue(typeIndex, out var storage))
                return;

            var typedStorage = (ComponentStorage<T>)storage;
            for (int i = 0; i < _count; i++)
            {
                if (typedStorage.Has(i))
                {
                    action(_entities[i], ref typedStorage.Get(i));
                }
            }
        }

        /// <summary>
        /// 清除所有实体和组件。
        /// </summary>
        public void Clear()
        {
            _count = 0;
            _freeIds.Clear();
            foreach (var storage in _componentStorages.Values)
                storage.Clear();

            // 重置 sparse pages
            for (int p = 0; p < _pageCount; p++)
                Array.Fill(_sparsePages[p], -1);

            Stats.CurrentAlive = 0;
        }

        // ──────────────────────────────────────────────────────────────────────
        // 诊断
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 生成诊断报告。
        /// </summary>
        public string GenerateReport()
        {
            var sb = new StringBuilder();
            sb.AppendLine("═══ EntitySparseSet 诊断报告 ═══");
            sb.AppendLine($"存活实体: {Stats.CurrentAlive}");
            sb.AppendLine($"容量: {_capacity}");
            sb.AppendLine($"已分配 ID: {AllocatedIds} (页数: {_pageCount})");
            sb.AppendLine($"空闲 ID: {FreeIdCount}");
            sb.AppendLine($"组件存储数: {_componentStorages.Count}");
            sb.AppendLine();
            sb.AppendLine("── 统计 ──");
            sb.AppendLine($"  总创建: {Stats.TotalCreated}");
            sb.AppendLine($"  总销毁: {Stats.TotalDestroyed}");
            sb.AppendLine($"  回收 ID: {Stats.RecycledIds}");
            sb.AppendLine($"  组件添加: {Stats.TotalComponentsAdded}");
            sb.AppendLine($"  组件移除: {Stats.TotalComponentsRemoved}");
            sb.AppendLine($"  组件设置: {Stats.TotalComponentsSet}");
            return sb.ToString();
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 组件存储接口
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 组件存储接口。
    /// </summary>
    internal interface IComponentStorage
    {
        void Remove(int index);
        void SwapRemove(int index, int lastIndex);
        void Clear();
        bool Has(int index);
    }

    /// <summary>
    /// 泛型组件存储。
    /// </summary>
    internal sealed class ComponentStorage<T> : IComponentStorage where T : struct
    {
        private T[] _data;
        private bool[] _has;

        public ComponentStorage(int capacity)
        {
            _data = new T[capacity];
            _has = new bool[capacity];
        }

        public ref T Get(int index)
        {
            return ref _data[index];
        }

        public void Set(int index, in T value)
        {
            EnsureCapacity(index + 1);
            _data[index] = value;
            _has[index] = true;
        }

        public bool Has(int index)
        {
            if (index < 0 || index >= _has.Length) return false;
            return _has[index];
        }

        public void Remove(int index)
        {
            if (index >= 0 && index < _has.Length)
            {
                _has[index] = false;
                _data[index] = default;
            }
        }

        public void SwapRemove(int index, int lastIndex)
        {
            if (index < 0 || index >= _has.Length) return;
            if (lastIndex < 0 || lastIndex >= _has.Length) return;

            if (_has[lastIndex])
            {
                _data[index] = _data[lastIndex];
                _has[index] = true;
                _has[lastIndex] = false;
                _data[lastIndex] = default;
            }
            else
            {
                _has[index] = false;
                _data[index] = default;
            }
        }

        public void Clear()
        {
            Array.Clear(_data, 0, _data.Length);
            Array.Clear(_has, 0, _has.Length);
        }

        public Span<T> AsSpan(int start, int length)
        {
            return _data.AsSpan(start, length);
        }

        public ReadOnlySpan<T> AsReadOnlySpan(int start, int length)
        {
            return _data.AsSpan(start, length);
        }

        private void EnsureCapacity(int required)
        {
            if (required <= _data.Length) return;
            int newCapacity = Math.Max(required, _data.Length * 2);
            Array.Resize(ref _data, newCapacity);
            Array.Resize(ref _has, newCapacity);
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 统计
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>稀疏集统计</summary>
    public class SparseSetStats
    {
        public int CurrentAlive;
        public long TotalCreated;
        public long TotalDestroyed;
        public long TotalCreatedIds;
        public long RecycledIds;
        public long TotalComponentsAdded;
        public long TotalComponentsRemoved;
        public long TotalComponentsSet;

        public override string ToString()
        {
            return $"存活={CurrentAlive}, 创建={TotalCreated}, 销毁={TotalDestroyed}, " +
                   $"回收ID={RecycledIds}, 组件加={TotalComponentsAdded}, " +
                   $"组件删={TotalComponentsRemoved}";
        }
    }
}
