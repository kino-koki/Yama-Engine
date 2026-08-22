// ════════════════════════════════════════════════════════════════════════════
// 山雨引擎 - 组件块迭代器 (ComponentChunkIterator)
//
// 提供对 archetype chunk 的高效迭代，支持泛型组件访问。
// 核心思想是把 "遍历所有实体的组件" 这个操作做到 cache-friendly。
//
// 主要类型:
//   * ComponentChunkIterator: 遍历 chunk，提供组件数组的直接访问
//   * ComponentDataAccessor<T>: 类型安全的组件访问器
//   * ArchetypeChunkArray: 批量获取 chunk 数据，避免逐个访问
//   * ParallelChunkIterator: 并行迭代（多线程）
//
// 参考 Unity ECS 的 IJobChunk 和 EntityQuery.ToComponentDataArray
//
// FIXME: 并行迭代还没做 work-stealing
// TODO: 加个 JobSystem 集成
// ════════════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace ShanyuEngine.ECS
{
    /// <summary>
    /// 组件块迭代器：高效遍历查询结果的 chunk，提供类型安全的组件访问。
    /// </summary>
    public readonly struct ComponentChunkIterator
    {
        private readonly List<ArchetypeChunk> _chunks;
        private readonly int _chunkCount;

        /// <summary>chunk 数量</summary>
        public int ChunkCount => _chunkCount;

        /// <summary>总实体数（需要遍历计算）</summary>
        public int EntityCount
        {
            get
            {
                int count = 0;
                foreach (var chunk in _chunks)
                    count += chunk.EntityCount;
                return count;
            }
        }

        public ComponentChunkIterator(List<ArchetypeChunk> chunks)
        {
            _chunks = chunks ?? new List<ArchetypeChunk>();
            _chunkCount = _chunks.Count;
        }

        // ──────────────────────────────────────────────────────────────────────
        // 基本迭代
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 遍历所有 chunk，对每个 chunk 调用回调。
        /// 这是最基本的迭代方式。
        /// </summary>
        public void ForEachChunk(Action<ArchetypeChunk> action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            for (int i = 0; i < _chunkCount; i++)
            {
                action(_chunks[i]);
            }
        }

        /// <summary>
        /// 遍历所有 chunk，带索引。
        /// </summary>
        public void ForEachChunk(Action<ArchetypeChunk, int> action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            for (int i = 0; i < _chunkCount; i++)
            {
                action(_chunks[i], i);
            }
        }

        /// <summary>
        /// 遍历所有实体。
        /// 注意：这比 ForEachChunk 慢，因为要逐个访问实体。
        /// </summary>
        public void ForEachEntity(Action<EntityReal> action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            for (int c = 0; c < _chunkCount; c++)
            {
                var chunk = _chunks[c];
                for (int e = 0; e < chunk.EntityCount; e++)
                {
                    action(chunk.GetEntity(e));
                }
            }
        }

        // ──────────────────────────────────────────────────────────────────────
        // 泛型组件迭代
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 遍历所有 chunk，提供单个组件类型的数组访问。
        /// </summary>
        public void ForEach<T1>(Action<ArchetypeChunk, Span<T1>> action) where T1 : struct
        {
            if (action == null) throw new ArgumentNullException(nameof(action));

            for (int c = 0; c < _chunkCount; c++)
            {
                var chunk = _chunks[c];
                var accessor = new ComponentDataAccessor<T1>(chunk);
                action(chunk, accessor.AsSpan());
            }
        }

        /// <summary>
        /// 遍历所有 chunk，提供两个组件类型的数组访问。
        /// </summary>
        public void ForEach<T1, T2>(Action<ArchetypeChunk, Span<T1>, Span<T2>> action)
            where T1 : struct
            where T2 : struct
        {
            if (action == null) throw new ArgumentNullException(nameof(action));

            for (int c = 0; c < _chunkCount; c++)
            {
                var chunk = _chunks[c];
                var accessor1 = new ComponentDataAccessor<T1>(chunk);
                var accessor2 = new ComponentDataAccessor<T2>(chunk);
                action(chunk, accessor1.AsSpan(), accessor2.AsSpan());
            }
        }

        /// <summary>
        /// 遍历所有 chunk，提供三个组件类型的数组访问。
        /// </summary>
        public void ForEach<T1, T2, T3>(Action<ArchetypeChunk, Span<T1>, Span<T2>, Span<T3>> action)
            where T1 : struct
            where T2 : struct
            where T3 : struct
        {
            if (action == null) throw new ArgumentNullException(nameof(action));

            for (int c = 0; c < _chunkCount; c++)
            {
                var chunk = _chunks[c];
                var accessor1 = new ComponentDataAccessor<T1>(chunk);
                var accessor2 = new ComponentDataAccessor<T2>(chunk);
                var accessor3 = new ComponentDataAccessor<T3>(chunk);
                action(chunk, accessor1.AsSpan(), accessor2.AsSpan(), accessor3.AsSpan());
            }
        }

        /// <summary>
        /// 遍历所有 chunk，提供四个组件类型的数组访问。
        /// </summary>
        public void ForEach<T1, T2, T3, T4>(
            Action<ArchetypeChunk, Span<T1>, Span<T2>, Span<T3>, Span<T4>> action)
            where T1 : struct
            where T2 : struct
            where T3 : struct
            where T4 : struct
        {
            if (action == null) throw new ArgumentNullException(nameof(action));

            for (int c = 0; c < _chunkCount; c++)
            {
                var chunk = _chunks[c];
                var a1 = new ComponentDataAccessor<T1>(chunk);
                var a2 = new ComponentDataAccessor<T2>(chunk);
                var a3 = new ComponentDataAccessor<T3>(chunk);
                var a4 = new ComponentDataAccessor<T4>(chunk);
                action(chunk, a1.AsSpan(), a2.AsSpan(), a3.AsSpan(), a4.AsSpan());
            }
        }

        // ──────────────────────────────────────────────────────────────────────
        // 带实体引用的迭代
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 遍历所有 chunk，同时提供实体数组和单个组件数组。
        /// </summary>
        public void ForEachWithEntity<T1>(
            Action<ArchetypeChunk, Span<EntityReal>, Span<T1>> action)
            where T1 : struct
        {
            if (action == null) throw new ArgumentNullException(nameof(action));

            for (int c = 0; c < _chunkCount; c++)
            {
                var chunk = _chunks[c];
                var entities = GetEntitySpan(chunk);
                var accessor = new ComponentDataAccessor<T1>(chunk);
                action(chunk, entities, accessor.AsSpan());
            }
        }

        /// <summary>
        /// 遍历所有 chunk，同时提供实体数组和两个组件数组。
        /// </summary>
        public void ForEachWithEntity<T1, T2>(
            Action<ArchetypeChunk, Span<EntityReal>, Span<T1>, Span<T2>> action)
            where T1 : struct
            where T2 : struct
        {
            if (action == null) throw new ArgumentNullException(nameof(action));

            for (int c = 0; c < _chunkCount; c++)
            {
                var chunk = _chunks[c];
                var entities = GetEntitySpan(chunk);
                var a1 = new ComponentDataAccessor<T1>(chunk);
                var a2 = new ComponentDataAccessor<T2>(chunk);
                action(chunk, entities, a1.AsSpan(), a2.AsSpan());
            }
        }

        /// <summary>
        /// 遍历所有 chunk，同时提供实体数组和三个组件数组。
        /// </summary>
        public void ForEachWithEntity<T1, T2, T3>(
            Action<ArchetypeChunk, Span<EntityReal>, Span<T1>, Span<T2>, Span<T3>> action)
            where T1 : struct
            where T2 : struct
            where T3 : struct
        {
            if (action == null) throw new ArgumentNullException(nameof(action));

            for (int c = 0; c < _chunkCount; c++)
            {
                var chunk = _chunks[c];
                var entities = GetEntitySpan(chunk);
                var a1 = new ComponentDataAccessor<T1>(chunk);
                var a2 = new ComponentDataAccessor<T2>(chunk);
                var a3 = new ComponentDataAccessor<T3>(chunk);
                action(chunk, entities, a1.AsSpan(), a2.AsSpan(), a3.AsSpan());
            }
        }

        // ──────────────────────────────────────────────────────────────────────
        // 数据收集
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 收集所有实体的指定组件到一个数组。
        /// 慎用，这会分配内存。优先用 ForEach。
        /// </summary>
        public T[] ToComponentDataArray<T>() where T : struct
        {
            int totalCount = EntityCount;
            var result = new T[totalCount];
            int offset = 0;

            for (int c = 0; c < _chunkCount; c++)
            {
                var chunk = _chunks[c];
                var accessor = new ComponentDataAccessor<T1Like<T>>(chunk);
                var span = accessor.AsSpan();
                span.CopyTo(result.AsSpan(offset, chunk.EntityCount));
                offset += chunk.EntityCount;
            }

            return result;
        }

        /// <summary>
        /// 收集所有实体 ID 到数组。
        /// </summary>
        public EntityReal[] ToEntityArray()
        {
            int totalCount = EntityCount;
            var result = new EntityReal[totalCount];
            int offset = 0;

            for (int c = 0; c < _chunkCount; c++)
            {
                var chunk = _chunks[c];
                var entities = GetEntitySpan(chunk);
                entities.CopyTo(result.AsSpan(offset, chunk.EntityCount));
                offset += chunk.EntityCount;
            }

            return result;
        }

        // ──────────────────────────────────────────────────────────────────────
        // 并行迭代
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 并行遍历所有 chunk。使用 Parallel.For。
        /// 注意：回调必须是线程安全的！
        /// </summary>
        public void ForEachParallel(Action<ArchetypeChunk> action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            if (_chunkCount == 0) return;

            // chunk 数量太少不值得并行
            if (_chunkCount < 2)
            {
                ForEachChunk(action);
                return;
            }

            Parallel.For(0, _chunkCount, i =>
            {
                action(_chunks[i]);
            });
        }

        /// <summary>
        /// 并行遍历所有 chunk，带组件访问。
        /// </summary>
        public void ForEachParallel<T1>(Action<ArchetypeChunk, Span<T1>> action) where T1 : struct
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            if (_chunkCount == 0) return;

            if (_chunkCount < 2)
            {
                ForEach(action);
                return;
            }

            Parallel.For(0, _chunkCount, i =>
            {
                var chunk = _chunks[i];
                var accessor = new ComponentDataAccessor<T1>(chunk);
                action(chunk, accessor.AsSpan());
            });
        }

        /// <summary>
        /// 并行遍历所有 chunk，带两个组件访问。
        /// </summary>
        public void ForEachParallel<T1, T2>(Action<ArchetypeChunk, Span<T1>, Span<T2>> action)
            where T1 : struct
            where T2 : struct
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            if (_chunkCount == 0) return;

            if (_chunkCount < 2)
            {
                ForEach(action);
                return;
            }

            Parallel.For(0, _chunkCount, i =>
            {
                var chunk = _chunks[i];
                var a1 = new ComponentDataAccessor<T1>(chunk);
                var a2 = new ComponentDataAccessor<T2>(chunk);
                action(chunk, a1.AsSpan(), a2.AsSpan());
            });
        }

        // ──────────────────────────────────────────────────────────────────────
        // 计算和聚合
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 对每个实体的组件值执行映射，收集结果。
        /// </summary>
        public TResult[] Select<TComponent, TResult>(Func<TComponent, TResult> selector)
            where TComponent : struct
        {
            int totalCount = EntityCount;
            var result = new TResult[totalCount];
            int offset = 0;

            for (int c = 0; c < _chunkCount; c++)
            {
                var chunk = _chunks[c];
                var accessor = new ComponentDataAccessor<T1Like<TComponent>>(chunk);
                var span = accessor.AsSpan();
                for (int i = 0; i < chunk.EntityCount; i++)
                {
                    result[offset + i] = selector(span[i]);
                }
                offset += chunk.EntityCount;
            }

            return result;
        }

        /// <summary>
        /// 统计满足条件的实体数。
        /// </summary>
        public int Count<TComponent>(Func<TComponent, bool> predicate) where TComponent : struct
        {
            int count = 0;
            for (int c = 0; c < _chunkCount; c++)
            {
                var chunk = _chunks[c];
                var accessor = new ComponentDataAccessor<T1Like<TComponent>>(chunk);
                var span = accessor.AsSpan();
                for (int i = 0; i < chunk.EntityCount; i++)
                {
                    if (predicate(span[i]))
                        count++;
                }
            }
            return count;
        }

        /// <summary>
        /// 检查是否有任意实体满足条件。
        /// </summary>
        public bool Any<TComponent>(Func<TComponent, bool> predicate) where TComponent : struct
        {
            for (int c = 0; c < _chunkCount; c++)
            {
                var chunk = _chunks[c];
                var accessor = new ComponentDataAccessor<T1Like<TComponent>>(chunk);
                var span = accessor.AsSpan();
                for (int i = 0; i < chunk.EntityCount; i++)
                {
                    if (predicate(span[i]))
                        return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 检查是否所有实体都满足条件。
        /// </summary>
        public bool All<TComponent>(Func<TComponent, bool> predicate) where TComponent : struct
        {
            for (int c = 0; c < _chunkCount; c++)
            {
                var chunk = _chunks[c];
                var accessor = new ComponentDataAccessor<T1Like<TComponent>>(chunk);
                var span = accessor.AsSpan();
                for (int i = 0; i < chunk.EntityCount; i++)
                {
                    if (!predicate(span[i]))
                        return false;
                }
            }
            return true;
        }

        // ──────────────────────────────────────────────────────────────────────
        // 辅助
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 获取 chunk 中的实体数组 Span。
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private Span<EntityReal> GetEntitySpan(ArchetypeChunk chunk)
        {
            // FIXME: 实际应该从 chunk 获取实体数组的 Span
            // 这里创建临时数组，性能不好
            var entities = new EntityReal[chunk.EntityCount];
            for (int i = 0; i < chunk.EntityCount; i++)
                entities[i] = chunk.GetEntity(i);
            return entities;
        }

        /// <summary>按索引获取 chunk</summary>
        public ArchetypeChunk this[int index] => _chunks[index];

        /// <summary>获取 chunk 列表的枚举器</summary>
        public List<ArchetypeChunk>.Enumerator GetEnumerator() => _chunks.GetEnumerator();
    }

    /// <summary>
    /// 辅助类型，用于在泛型方法中统一处理组件访问。
    /// </summary>
    internal struct T1Like<T> where T : struct
    {
        public T Value;
    }

    // ════════════════════════════════════════════════════════════════════════════
    // ArchetypeChunk 批量数组
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// ArchetypeChunk 批量数组：一次性获取多个 chunk 的数据。
    /// 减少 chunk 级别的重复查询开销。
    /// </summary>
    public sealed class ArchetypeChunkArray
    {
        private readonly ArchetypeChunk[] _chunks;
        private readonly int _entityCount;
        private readonly int[] _entityOffsets;

        /// <summary>chunk 数量</summary>
        public int ChunkCount => _chunks.Length;

        /// <summary>总实体数</summary>
        public int EntityCount => _entityCount;

        /// <summary>按索引获取 chunk</summary>
        public ArchetypeChunk this[int index] => _chunks[index];

        public ArchetypeChunkArray(ArchetypeChunk[] chunks)
        {
            _chunks = chunks ?? Array.Empty<ArchetypeChunk>();
            _entityOffsets = new int[_chunks.Length];

            int count = 0;
            for (int i = 0; i < _chunks.Length; i++)
            {
                _entityOffsets[i] = count;
                count += _chunks[i].EntityCount;
            }
            _entityCount = count;
        }

        /// <summary>获取指定 chunk 的实体起始偏移</summary>
        public int GetEntityOffset(int chunkIndex) => _entityOffsets[chunkIndex];

        /// <summary>
        /// 创建一个原生数组来收集所有 chunk 的组件数据。
        /// </summary>
        public NativeArray<T> CreateComponentDataArray<T>() where T : struct
        {
            var result = new NativeArray<T>(_entityCount);

            for (int c = 0; c < _chunks.Length; c++)
            {
                var chunk = _chunks[c];
                var accessor = new ComponentDataAccessor<T>(chunk);
                var srcSpan = accessor.AsSpan();
                var dstSpan = result.AsSpan(_entityOffsets[c], chunk.EntityCount);
                srcSpan.CopyTo(dstSpan);
            }

            return result;
        }

        /// <summary>
        /// 把组件数据写回 chunk。
        /// </summary>
        public void WriteBackComponentData<T>(NativeArray<T> data) where T : struct
        {
            for (int c = 0; c < _chunks.Length; c++)
            {
                var chunk = _chunks[c];
                var accessor = new ComponentDataAccessor<T>(chunk);
                var dstSpan = accessor.AsSpan();
                var srcSpan = data.AsSpan(_entityOffsets[c], chunk.EntityCount);
                srcSpan.CopyTo(dstSpan);
            }
        }

        /// <summary>获取所有实体</summary>
        public EntityReal[] GetEntities()
        {
            var result = new EntityReal[_entityCount];
            for (int c = 0; c < _chunks.Length; c++)
            {
                var chunk = _chunks[c];
                for (int i = 0; i < chunk.EntityCount; i++)
                {
                    result[_entityOffsets[c] + i] = chunk.GetEntity(i);
                }
            }
            return result;
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // NativeArray（简化版）
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 简化版 NativeArray：托管堆上的数组，但提供 Span 访问。
    /// 实际的 NativeArray 应该用非托管内存，这里简化。
    /// </summary>
    public sealed class NativeArray<T> : IDisposable where T : struct
    {
        private T[] _array;
        private bool _disposed;

        /// <summary>长度</summary>
        public int Length => _array?.Length ?? 0;

        /// <summary>是否已释放</summary>
        public bool IsDisposed => _disposed;

        public NativeArray(int length)
        {
            _array = new T[length];
        }

        /// <summary>获取 Span</summary>
        public Span<T> AsSpan() => _array.AsSpan();

        /// <summary>获取指定范围的 Span</summary>
        public Span<T> AsSpan(int start, int length) => _array.AsSpan(start, length);

        /// <summary>按索引访问</summary>
        public ref T this[int index] => ref _array[index];

        /// <summary>转换为普通数组</summary>
        public T[] ToArray()
        {
            var result = new T[_array.Length];
            Array.Copy(_array, result, _array.Length);
            return result;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _array = null;
            _disposed = true;
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 分块迭代器（带索引）
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 分块迭代器：不仅遍历 chunk，还提供 chunk 内实体的局部索引。
    /// 适合需要知道 "这是 chunk 内第几个实体" 的场景。
    /// </summary>
    public readonly struct IndexedChunkIterator
    {
        private readonly List<ArchetypeChunk> _chunks;

        public IndexedChunkIterator(List<ArchetypeChunk> chunks)
        {
            _chunks = chunks ?? new List<ArchetypeChunk>();
        }

        /// <summary>
        /// 遍历所有实体的索引。
        /// 回调参数: chunk, chunk内索引, 全局索引
        /// </summary>
        public void ForEachIndex(Action<ArchetypeChunk, int, int> action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            int globalIndex = 0;
            for (int c = 0; c < _chunks.Count; c++)
            {
                var chunk = _chunks[c];
                for (int i = 0; i < chunk.EntityCount; i++)
                {
                    action(chunk, i, globalIndex++);
                }
            }
        }

        /// <summary>
        /// 遍历所有实体的索引，带组件访问。
        /// </summary>
        public void ForEachIndex<T1>(Action<ArchetypeChunk, int, int, Span<T1>> action) where T1 : struct
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            int globalIndex = 0;
            for (int c = 0; c < _chunks.Count; c++)
            {
                var chunk = _chunks[c];
                var accessor = new ComponentDataAccessor<T1>(chunk);
                var span = accessor.AsSpan();
                for (int i = 0; i < chunk.EntityCount; i++)
                {
                    action(chunk, i, globalIndex++, span);
                }
            }
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // chunk 过滤器
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Chunk 过滤器：在遍历时跳过不满足条件的 chunk。
    /// </summary>
    public readonly struct ChunkFilter
    {
        private readonly ComponentChunkIterator _iterator;
        private readonly Func<ArchetypeChunk, bool> _filter;

        public ChunkFilter(ComponentChunkIterator iterator, Func<ArchetypeChunk, bool> filter)
        {
            _iterator = iterator;
            _filter = filter;
        }

        /// <summary>遍历满足条件的 chunk</summary>
        public void ForEach(Action<ArchetypeChunk> action)
        {
            for (int i = 0; i < _iterator.ChunkCount; i++)
            {
                var chunk = _iterator[i];
                if (_filter(chunk))
                    action(chunk);
            }
        }

        /// <summary>遍历满足条件的 chunk，带组件访问</summary>
        public void ForEach<T1>(Action<ArchetypeChunk, Span<T1>> action) where T1 : struct
        {
            for (int i = 0; i < _iterator.ChunkCount; i++)
            {
                var chunk = _iterator[i];
                if (_filter(chunk))
                {
                    var accessor = new ComponentDataAccessor<T1>(chunk);
                    action(chunk, accessor.AsSpan());
                }
            }
        }

        /// <summary>统计满足条件的 chunk 数</summary>
        public int Count()
        {
            int count = 0;
            for (int i = 0; i < _iterator.ChunkCount; i++)
            {
                if (_filter(_iterator[i]))
                    count++;
            }
            return count;
        }
    }

    /// <summary>
    /// ComponentChunkIterator 的扩展方法。
    /// </summary>
    public static class ChunkIteratorExtensions
    {
        /// <summary>创建一个过滤器</summary>
        public static ChunkFilter Where(this ComponentChunkIterator iterator,
                                         Func<ArchetypeChunk, bool> filter)
        {
            return new ChunkFilter(iterator, filter);
        }

        /// <summary>创建批量数组</summary>
        public static ArchetypeChunkArray ToChunkArray(this ComponentChunkIterator iterator)
        {
            var chunks = new ArchetypeChunk[iterator.ChunkCount];
            for (int i = 0; i < iterator.ChunkCount; i++)
                chunks[i] = iterator[i];
            return new ArchetypeChunkArray(chunks);
        }

        /// <summary>创建带索引的迭代器</summary>
        public static IndexedChunkIterator WithIndex(this ComponentChunkIterator iterator)
        {
            // 重新构建 chunks 列表
            var chunks = new List<ArchetypeChunk>(iterator.ChunkCount);
            for (int i = 0; i < iterator.ChunkCount; i++)
                chunks.Add(iterator[i]);
            return new IndexedChunkIterator(chunks);
        }
    }
}
