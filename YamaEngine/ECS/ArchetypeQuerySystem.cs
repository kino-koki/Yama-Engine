// ════════════════════════════════════════════════════════════════════════════
// yama引擎 - 原型查询系统 (ArchetypeQuerySystem)
//
// 基于组件类型的原型查询。核心是 EntityQuery，描述"我要找有哪些组件的实体"。
// 查询会匹配所有满足条件的 archetype，返回它们的 chunk 列表。
//
// 设计参考 Unity ECS 的 EntityQuery，但简化了很多:
//   * 支持必须有的组件 (All)
//   * 支持任意一个就行的组件 (Any)
//   * 支持排除的组件 (None)
//   * 支持 chunk 级别的迭代 (不用遍历所有实体)
//   * 查询结果缓存
//
// FIXME: 还没支持 shared component 的过滤
// TODO: 加个变更追踪，只返回有变更的 chunk
// ════════════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

namespace YamaEngine.ECS
{
    /// <summary>
    /// 原型查询系统：管理实体查询，匹配 archetype，提供高效的 chunk 迭代。
    /// </summary>
    public sealed class ArchetypeQuerySystem : IDisposable
    {
        // ──────────────────────────────────────────────────────────────────────
        // 字段
        // ──────────────────────────────────────────────────────────────────────

        private readonly ArchetypeManager _archetypeManager;
        private readonly Dictionary<QueryDesc, QueryResult> _queryCache = new();
        private readonly List<QueryDesc> _allQueries = new();
        private ulong _currentVersion;
        private bool _disposed;

        /// <summary>当前版本号（每次 archetype 变化时递增）</summary>
        public ulong CurrentVersion => _currentVersion;

        /// <summary>已注册的查询数</summary>
        public int QueryCount => _allQueries.Count;

        /// <summary>缓存命中次数</summary>
        public int CacheHits { get; private set; }

        /// <summary>缓存未命中次数</summary>
        public int CacheMisses { get; private set; }

        // ──────────────────────────────────────────────────────────────────────
        // 构造
        // ──────────────────────────────────────────────────────────────────────

        public ArchetypeQuerySystem(ArchetypeManager archetypeManager)
        {
            _archetypeManager = archetypeManager ?? throw new ArgumentNullException(nameof(archetypeManager));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _queryCache.Clear();
            _allQueries.Clear();
            _disposed = true;
        }

        // ──────────────────────────────────────────────────────────────────────
        // 查询创建
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 创建一个新的查询描述。用 builder 模式。
        /// </summary>
        public QueryDescBuilder CreateQuery()
        {
            return new QueryDescBuilder(this);
        }

        /// <summary>
        /// 创建简单的 All 查询（必须包含所有指定组件）。
        /// </summary>
        public QueryDesc CreateQueryAll(params ComponentTypeReal[] required)
        {
            var builder = new QueryDescBuilder(this);
            foreach (var t in required)
                builder.All(t);
            return builder.Build();
        }

        /// <summary>
        /// 内部注册查询。
        /// </summary>
        internal QueryDesc RegisterQuery(QueryDesc desc)
        {
            _allQueries.Add(desc);
            return desc;
        }

        // ──────────────────────────────────────────────────────────────────────
        // 查询执行
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 执行查询，返回匹配的 archetype chunk 列表。
        /// 结果会被缓存，直到 archetype 结构发生变化。
        /// </summary>
        public QueryResult Execute(QueryDesc desc)
        {
            if (desc == null) throw new ArgumentNullException(nameof(desc));

            // 检查缓存
            if (_queryCache.TryGetValue(desc, out var cached))
            {
                if (cached.Version == _currentVersion)
                {
                    CacheHits++;
                    return cached;
                }
            }

            CacheMisses++;

            // 重新匹配
            var result = MatchArchetypes(desc);
            result.Version = _currentVersion;
            _queryCache[desc] = result;

            return result;
        }

        /// <summary>
        /// 执行查询并返回匹配的实体数量。
        /// </summary>
        public int GetEntityCount(QueryDesc desc)
        {
            var result = Execute(desc);
            return result.EntityCount;
        }

        /// <summary>
        /// 执行查询并返回所有匹配的实体 ID。
        /// 慎用，这会遍历所有 chunk 收集实体，比直接迭代 chunk 慢。
        /// </summary>
        public List<EntityReal> GetEntities(QueryDesc desc)
        {
            var result = Execute(desc);
            var entities = new List<EntityReal>(result.EntityCount);

            foreach (var chunk in result.Chunks)
            {
                for (int i = 0; i < chunk.EntityCount; i++)
                {
                    entities.Add(chunk.GetEntity(i));
                }
            }

            return entities;
        }

        /// <summary>
        /// 执行查询，返回第一个匹配的实体。没有则返回 default。
        /// </summary>
        public EntityReal GetSingletonEntity(QueryDesc desc)
        {
            var result = Execute(desc);
            if (result.EntityCount == 0)
                return default;
            foreach (var chunk in result.Chunks)
            {
                if (chunk.EntityCount > 0)
                    return chunk.GetEntity(0);
            }
            return default;
        }

        // ──────────────────────────────────────────────────────────────────────
        // Archetype 匹配
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 把查询描述和所有 archetype 匹配，返回匹配的 chunk 列表。
        /// </summary>
        private QueryResult MatchArchetypes(QueryDesc desc)
        {
            var chunks = new List<ArchetypeChunk>();
            int entityCount = 0;

            foreach (var archetype in _archetypeManager.GetAllArchetypes())
            {
                if (MatchesQuery(archetype, desc))
                {
                    foreach (var chunk in archetype.Chunks)
                    {
                        if (chunk.EntityCount > 0)
                        {
                            chunks.Add(chunk);
                            entityCount += chunk.EntityCount;
                        }
                    }
                }
            }

            return new QueryResult
            {
                Chunks = chunks,
                EntityCount = entityCount,
                ChunkCount = chunks.Count,
            };
        }

        /// <summary>
        /// 检查一个 archetype 是否匹配查询描述。
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool MatchesQuery(ArchetypeReal archetype, QueryDesc desc)
        {
            // All: 必须包含所有指定组件
            if (desc.All != null)
            {
                foreach (var t in desc.All)
                {
                    if (!archetype.HasComponent(t.TypeIndex))
                        return false;
                }
            }

            // None: 不能包含任何指定组件
            if (desc.None != null)
            {
                foreach (var t in desc.None)
                {
                    if (archetype.HasComponent(t.TypeIndex))
                        return false;
                }
            }

            // Any: 至少包含一个指定组件（如果列表非空）
            if (desc.Any != null && desc.Any.Length > 0)
            {
                bool found = false;
                foreach (var t in desc.Any)
                {
                    if (archetype.HasComponent(t.TypeIndex))
                    {
                        found = true;
                        break;
                    }
                }
                if (!found) return false;
            }

            return true;
        }

        // ──────────────────────────────────────────────────────────────────────
        // 版本管理
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 通知 archetype 结构发生变化。使所有缓存失效。
        /// </summary>
        public void OnArchetypeChanged()
        {
            _currentVersion++;
            // 不立即清空缓存，惰性失效
        }

        /// <summary>清除所有缓存。</summary>
        public void ClearCache()
        {
            _queryCache.Clear();
        }

        /// <summary>获取缓存统计信息。</summary>
        public string GetCacheStats()
        {
            float hitRate = (CacheHits + CacheMisses) > 0
                ? (float)CacheHits / (CacheHits + CacheMisses) * 100f
                : 0f;
            return $"查询缓存: 大小={_queryCache.Count}, 命中={CacheHits}, " +
                   $"未命中={CacheMisses}, 命中率={hitRate:F1}%";
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 查询描述
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 查询描述：定义一个实体查询的条件。
    /// </summary>
    public sealed class QueryDesc : IEquatable<QueryDesc>
    {
        /// <summary>必须全部包含的组件</summary>
        public ComponentTypeReal[] All;

        /// <summary>至少包含一个的组件</summary>
        public ComponentTypeReal[] Any;

        /// <summary>不能包含的组件</summary>
        public ComponentTypeReal[] None;

        private int _cachedHash;

        /// <summary>查询名称（调试用）</summary>
        public string Name { get; set; }

        public QueryDesc()
        {
            All = Array.Empty<ComponentTypeReal>();
            Any = Array.Empty<ComponentTypeReal>();
            None = Array.Empty<ComponentTypeReal>();
            Name = "UnnamedQuery";
        }

        /// <summary>是否匹配空查询（没有条件）</summary>
        public bool IsEmpty => (All == null || All.Length == 0) &&
                                (Any == null || Any.Length == 0) &&
                                (None == null || None.Length == 0);

        public bool Equals(QueryDesc other)
        {
            if (other == null) return false;
            return GetHashCode() == other.GetHashCode();
        }

        public override bool Equals(object obj) => obj is QueryDesc q && Equals(q);

        public override int GetHashCode()
        {
            if (_cachedHash == 0)
            {
                unchecked
                {
                    int hash = 17;
                    if (All != null)
                    {
                        hash = hash * 31 + All.Length;
                        foreach (var t in All)
                            hash = hash * 31 + t.TypeIndex;
                    }
                    if (Any != null)
                    {
                        hash = hash * 31 + Any.Length;
                        foreach (var t in Any)
                            hash = hash * 31 + t.TypeIndex;
                    }
                    if (None != null)
                    {
                        hash = hash * 31 + None.Length;
                        foreach (var t in None)
                            hash = hash * 31 + t.TypeIndex;
                    }
                    _cachedHash = hash;
                }
            }
            return _cachedHash;
        }

        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.Append($"Query '{Name}': ");
            if (All != null && All.Length > 0)
            {
                sb.Append("All=[");
                sb.Append(string.Join(",", Array.ConvertAll(All, t => t.Type?.Name ?? "?")));
                sb.Append("] ");
            }
            if (Any != null && Any.Length > 0)
            {
                sb.Append("Any=[");
                sb.Append(string.Join(",", Array.ConvertAll(Any, t => t.Type?.Name ?? "?")));
                sb.Append("] ");
            }
            if (None != null && None.Length > 0)
            {
                sb.Append("None=[");
                sb.Append(string.Join(",", Array.ConvertAll(None, t => t.Type?.Name ?? "?")));
                sb.Append("]");
            }
            if (IsEmpty) sb.Append("(empty - matches all)");
            return sb.ToString();
        }
    }

    /// <summary>
    /// 查询结果：包含匹配的 chunk 列表和统计信息。
    /// </summary>
    public sealed class QueryResult
    {
        /// <summary>匹配的 chunk 列表</summary>
        public List<ArchetypeChunk> Chunks;

        /// <summary>匹配的实体总数</summary>
        public int EntityCount;

        /// <summary>匹配的 chunk 数</summary>
        public int ChunkCount;

        /// <summary>结果版本（用于缓存判断）</summary>
        public ulong Version;

        /// <summary>是否为空</summary>
        public bool IsEmpty => EntityCount == 0;

        /// <summary>获取 chunk 迭代器</summary>
        public ChunkIterator GetChunkIterator() => new(Chunks);

        public override string ToString()
        {
            return $"QueryResult: chunks={ChunkCount}, entities={EntityCount}, v{Version}";
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 查询描述构建器
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 查询描述构建器：用流式 API 创建 QueryDesc。
    /// </summary>
    public sealed class QueryDescBuilder
    {
        private readonly ArchetypeQuerySystem _system;
        private readonly List<ComponentTypeReal> _all = new();
        private readonly List<ComponentTypeReal> _any = new();
        private readonly List<ComponentTypeReal> _none = new();
        private string _name = "UnnamedQuery";

        internal QueryDescBuilder(ArchetypeQuerySystem system)
        {
            _system = system;
        }

        /// <summary>必须包含的组件</summary>
        public QueryDescBuilder All<T>() where T : struct
        {
            _all.Add(ComponentTypeRealCache.Get<T>());
            return this;
        }

        /// <summary>必须包含的组件</summary>
        public QueryDescBuilder All(ComponentTypeReal type)
        {
            _all.Add(type);
            return this;
        }

        /// <summary>至少包含一个的组件</summary>
        public QueryDescBuilder Any<T>() where T : struct
        {
            _any.Add(ComponentTypeRealCache.Get<T>());
            return this;
        }

        /// <summary>至少包含一个的组件</summary>
        public QueryDescBuilder Any(ComponentTypeReal type)
        {
            _any.Add(type);
            return this;
        }

        /// <summary>不能包含的组件</summary>
        public QueryDescBuilder None<T>() where T : struct
        {
            _none.Add(ComponentTypeRealCache.Get<T>());
            return this;
        }

        /// <summary>不能包含的组件</summary>
        public QueryDescBuilder None(ComponentTypeReal type)
        {
            _none.Add(type);
            return this;
        }

        /// <summary>设置查询名称</summary>
        public QueryDescBuilder WithName(string name)
        {
            _name = name;
            return this;
        }

        /// <summary>构建查询描述</summary>
        public QueryDesc Build()
        {
            var desc = new QueryDesc
            {
                All = _all.ToArray(),
                Any = _any.ToArray(),
                None = _none.ToArray(),
                Name = _name,
            };
            // 触发 hash 计算
            _ = desc.GetHashCode();
            return _system.RegisterQuery(desc);
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // Chunk 迭代器
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Chunk 迭代器：高效遍历查询结果中的所有 chunk。
    /// </summary>
    public struct ChunkIterator
    {
        private readonly List<ArchetypeChunk> _chunks;
        private int _currentIndex;

        public ChunkIterator(List<ArchetypeChunk> chunks)
        {
            _chunks = chunks;
            _currentIndex = -1;
        }

        /// <summary>当前 chunk</summary>
        public ArchetypeChunk Current => _currentIndex >= 0 && _currentIndex < _chunks.Count
            ? _chunks[_currentIndex]
            : null;

        /// <summary>是否有下一个</summary>
        public bool MoveNext()
        {
            _currentIndex++;
            return _currentIndex < _chunks.Count;
        }

        /// <summary>重置</summary>
        public void Reset()
        {
            _currentIndex = -1;
        }

        /// <summary>总数</summary>
        public int Count => _chunks?.Count ?? 0;

        /// <summary>按索引获取</summary>
        public ArchetypeChunk this[int index] => _chunks[index];
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 泛型查询辅助
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 泛型查询辅助：简化常见查询的写法。
    /// </summary>
    public static class QueryHelper
    {
        /// <summary>创建一个 All 查询</summary>
        public static QueryDesc QueryAll<T1>(ArchetypeQuerySystem system) where T1 : struct
        {
            return system.CreateQuery()
                .All<T1>()
                .WithName($"All<{typeof(T1).Name}>")
                .Build();
        }

        /// <summary>创建一个 All 查询</summary>
        public static QueryDesc QueryAll<T1, T2>(ArchetypeQuerySystem system)
            where T1 : struct
            where T2 : struct
        {
            return system.CreateQuery()
                .All<T1>()
                .All<T2>()
                .WithName($"All<{typeof(T1).Name},{typeof(T2).Name}>")
                .Build();
        }

        /// <summary>创建一个 All 查询</summary>
        public static QueryDesc QueryAll<T1, T2, T3>(ArchetypeQuerySystem system)
            where T1 : struct
            where T2 : struct
            where T3 : struct
        {
            return system.CreateQuery()
                .All<T1>()
                .All<T2>()
                .All<T3>()
                .WithName($"All<{typeof(T1).Name},{typeof(T2).Name},{typeof(T3).Name}>")
                .Build();
        }

        /// <summary>创建一个排除特定组件的查询</summary>
        public static QueryDesc QueryAllNone<T1, TExclude>(ArchetypeQuerySystem system)
            where T1 : struct
            where TExclude : struct
        {
            return system.CreateQuery()
                .All<T1>()
                .None<TExclude>()
                .WithName($"All<{typeof(T1).Name}>None<{typeof(TExclude).Name}>")
                .Build();
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 带变更过滤的查询
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 带变更过滤的查询：只返回有组件变更的 chunk。
    /// 参考 Unity ECS 的 EntityQueryDesc 的 ChangedFilter。
    /// </summary>
    public sealed class ChangeFilteredQuery
    {
        private readonly ArchetypeQuerySystem _system;
        private readonly QueryDesc _desc;
        private readonly HashSet<int> _trackedComponentTypes;
        private QueryResult _lastResult;
        private Dictionary<ArchetypeChunk, ulong> _chunkVersions = new();

        /// <summary>查询描述</summary>
        public QueryDesc Desc => _desc;

        /// <summary>跟踪变更的组件类型</summary>
        public IReadOnlyCollection<int> TrackedTypes => _trackedComponentTypes;

        public ChangeFilteredQuery(ArchetypeQuerySystem system, QueryDesc desc,
                                    params ComponentTypeReal[] trackedComponents)
        {
            _system = system ?? throw new ArgumentNullException(nameof(system));
            _desc = desc ?? throw new ArgumentNullException(nameof(desc));
            _trackedComponentTypes = new HashSet<int>();
            foreach (var ct in trackedComponents)
                _trackedComponentTypes.Add(ct.TypeIndex);
        }

        /// <summary>
        /// 执行查询，只返回有变更的 chunk。
        /// </summary>
        public QueryResult ExecuteChanged()
        {
            var fullResult = _system.Execute(_desc);
            var changedChunks = new List<ArchetypeChunk>();
            int entityCount = 0;

            var newVersions = new Dictionary<ArchetypeChunk, ulong>();

            foreach (var chunk in fullResult.Chunks)
            {
                // 获取 chunk 的变更版本
                ulong chunkVersion = GetChunkVersion(chunk);

                if (!_chunkVersions.TryGetValue(chunk, out var lastVersion) ||
                    chunkVersion != lastVersion)
                {
                    // 有变更
                    changedChunks.Add(chunk);
                    entityCount += chunk.EntityCount;
                }

                newVersions[chunk] = chunkVersion;
            }

            _chunkVersions = newVersions;

            return new QueryResult
            {
                Chunks = changedChunks,
                EntityCount = entityCount,
                ChunkCount = changedChunks.Count,
                Version = fullResult.Version,
            };
        }

        private ulong GetChunkVersion(ArchetypeChunk chunk)
        {
            // FIXME: 实际应该从 chunk 获取组件的变更版本
            // 这里用 chunk 的 EntityCount 作为简易版本
            return (ulong)chunk.EntityCount;
        }

        /// <summary>重置变更跟踪。</summary>
        public void Reset()
        {
            _chunkVersions.Clear();
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 批量查询执行器
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 批量查询执行器：一次性执行多个查询，减少重复匹配开销。
    /// </summary>
    public sealed class BatchQueryExecutor
    {
        private readonly ArchetypeQuerySystem _system;
        private readonly List<QueryDesc> _batchQueries = new();
        private QueryResult[] _batchResults;

        public BatchQueryExecutor(ArchetypeQuerySystem system)
        {
            _system = system ?? throw new ArgumentNullException(nameof(system));
        }

        /// <summary>添加查询到批量。</summary>
        public BatchQueryExecutor Add(QueryDesc desc)
        {
            _batchQueries.Add(desc);
            return this;
        }

        /// <summary>执行所有查询。</summary>
        public QueryResult[] Execute()
        {
            if (_batchResults == null || _batchResults.Length != _batchQueries.Count)
            {
                _batchResults = new QueryResult[_batchQueries.Count];
            }

            for (int i = 0; i < _batchQueries.Count; i++)
            {
                _batchResults[i] = _system.Execute(_batchQueries[i]);
            }

            return _batchResults;
        }

        /// <summary>获取指定查询的结果。</summary>
        public QueryResult GetResult(int index)
        {
            if (_batchResults == null || index < 0 || index >= _batchResults.Length)
                return null;
            return _batchResults[index];
        }

        /// <summary>清空批量。</summary>
        public void Clear()
        {
            _batchQueries.Clear();
            _batchResults = null;
        }
    }

    // ========================================================================
    // （以下各类型的真实定义见 ArchetypeECSReal.cs；此处不再注入重复占位）
}
