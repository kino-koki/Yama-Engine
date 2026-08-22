// ════════════════════════════════════════════════════════════════════════════
// 山雨引擎 - 实体查询缓存 (EntityQueryCache)
//
// 缓存查询结果，避免每帧重复匹配 archetype。
// 当 archetype 结构变化时自动失效。
//
// 主要功能:
//   * 查询结果缓存（带版本号）
//   * 自动失效（archetype 变化时）
//   * LRU 淘汰策略
//   * 缓存统计
//   * 线程安全
//
// FIXME: LRU 淘汰用的是简单链表，大量查询时可能成为瓶颈
// TODO: 加个预热功能，在启动时把常用查询缓存好
// ════════════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;

namespace ShanyuEngine.ECS
{
    /// <summary>
    /// 实体查询缓存：缓存查询结果，自动失效。
    /// </summary>
    public sealed class EntityQueryCache : IDisposable
    {
        // ──────────────────────────────────────────────────────────────────────
        // 字段
        // ──────────────────────────────────────────────────────────────────────

        private readonly Dictionary<QueryDesc, CacheEntry> _cache = new();
        private readonly LinkedList<QueryDesc> _lruList = new();
        private readonly object _lock = new();
        private ulong _currentVersion;
        private bool _disposed;

        /// <summary>最大缓存条目数</summary>
        public int MaxCacheSize { get; set; } = 256;

        /// <summary>当前缓存大小</summary>
        public int Count
        {
            get
            {
                lock (_lock)
                {
                    return _cache.Count;
                }
            }
        }

        /// <summary>当前版本号</summary>
        public ulong CurrentVersion => _currentVersion;

        // 统计
        public QueryCacheStats Stats { get; } = new QueryCacheStats();

        // ──────────────────────────────────────────────────────────────────────
        // 构造/析构
        // ──────────────────────────────────────────────────────────────────────

        public EntityQueryCache()
        {
        }

        public void Dispose()
        {
            lock (_lock)
            {
                _cache.Clear();
                _lruList.Clear();
                _disposed = true;
            }
        }

        // ──────────────────────────────────────────────────────────────────────
        // 缓存操作
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 尝试从缓存获取查询结果。
        /// </summary>
        /// <param name="desc">查询描述</param>
        /// <param name="result">查询结果（如果命中）</param>
        /// <returns>是否命中</returns>
        public bool TryGet(QueryDesc desc, out QueryResult result)
        {
            lock (_lock)
            {
                if (_cache.TryGetValue(desc, out var entry))
                {
                    if (entry.Version == _currentVersion)
                    {
                        // 命中
                        result = entry.Result;
                        entry.HitCount++;

                        // 更新 LRU
                        _lruList.Remove(entry.LruNode);
                        entry.LruNode = _lruList.AddLast(desc);

                        Stats.Hits++;
                        return true;
                    }
                    else
                    {
                        // 版本过期，移除
                        _lruList.Remove(entry.LruNode);
                        _cache.Remove(desc);
                        Stats.Expired++;
                    }
                }
                Stats.Misses++;
                result = null;
                return false;
            }
        }

        /// <summary>
        /// 把查询结果存入缓存。
        /// </summary>
        public void Put(QueryDesc desc, QueryResult result)
        {
            if (desc == null) throw new ArgumentNullException(nameof(desc));
            if (result == null) throw new ArgumentNullException(nameof(result));

            lock (_lock)
            {
                // 如果已存在，更新
                if (_cache.TryGetValue(desc, out var existing))
                {
                    existing.Result = result;
                    existing.Version = _currentVersion;
                    existing.HitCount = 0;

                    // 更新 LRU
                    _lruList.Remove(existing.LruNode);
                    existing.LruNode = _lruList.AddLast(desc);
                    return;
                }

                // 检查容量
                while (_cache.Count >= MaxCacheSize)
                {
                    EvictOldest();
                }

                // 添加新条目
                var lruNode = _lruList.AddLast(desc);
                var entry = new CacheEntry
                {
                    Result = result,
                    Version = _currentVersion,
                    HitCount = 0,
                    LruNode = lruNode,
                    CreatedFrame = Stats.TotalQueries,
                };
                _cache[desc] = entry;
                Stats.TotalCached++;
            }
        }

        /// <summary>
        /// 获取或添加查询结果。
        /// 如果缓存命中直接返回，否则执行 factory 创建结果并缓存。
        /// </summary>
        public QueryResult GetOrAdd(QueryDesc desc, Func<QueryDesc, QueryResult> factory)
        {
            if (desc == null) throw new ArgumentNullException(nameof(desc));
            if (factory == null) throw new ArgumentNullException(nameof(factory));

            lock (_lock)
            {
                if (TryGet(desc, out var result))
                    return result;

                // 未命中，创建
                Stats.TotalQueries++;
                var sw = Stopwatch.StartNew();
                result = factory(desc);
                sw.Stop();
                Stats.TotalMatchTimeMs += sw.Elapsed.TotalMilliseconds;

                Put(desc, result);
                return result;
            }
        }

        /// <summary>
        /// 使所有缓存失效。当 archetype 结构变化时调用。
        /// </summary>
        public void InvalidateAll()
        {
            lock (_lock)
            {
                _currentVersion++;
                // 不立即清除，让 TryGet 时惰性失效
                Stats.TotalInvalidations++;
            }
        }

        /// <summary>
        /// 清除所有缓存。
        /// </summary>
        public void Clear()
        {
            lock (_lock)
            {
                _cache.Clear();
                _lruList.Clear();
                Stats.TotalClears++;
            }
        }

        /// <summary>
        /// 驱逐最老的条目。
        /// </summary>
        private void EvictOldest()
        {
            if (_lruList.Count == 0) return;

            var oldest = _lruList.First.Value;
            _lruList.RemoveFirst();
            _cache.Remove(oldest);
            Stats.TotalEvicted++;
        }

        // ──────────────────────────────────────────────────────────────────────
        // 预热
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 预热缓存：提前执行查询并缓存结果。
        /// </summary>
        public void Warmup(IEnumerable<QueryDesc> queries, Func<QueryDesc, QueryResult> factory)
        {
            if (queries == null || factory == null) return;

            foreach (var desc in queries)
            {
                GetOrAdd(desc, factory);
            }

            Stats.TotalWarmups++;
        }

        // ──────────────────────────────────────────────────────────────────────
        // 诊断
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 获取缓存统计信息。
        /// </summary>
        public string GetStats()
        {
            lock (_lock)
            {
                float hitRate = (Stats.Hits + Stats.Misses) > 0
                    ? (float)Stats.Hits / (Stats.Hits + Stats.Misses) * 100f
                    : 0f;
                return $"EntityQueryCache: 大小={_cache.Count}/{MaxCacheSize}, " +
                       $"命中={Stats.Hits}, 未命中={Stats.Misses}, " +
                       $"命中率={hitRate:F1}%, 驱逐={Stats.TotalEvicted}, " +
                       $"过期={Stats.Expired}, 失效={Stats.TotalInvalidations}";
            }
        }

        /// <summary>
        /// 获取所有缓存条目的详细信息。
        /// </summary>
        public List<CacheEntryInfo> GetEntries()
        {
            var result = new List<CacheEntryInfo>();
            lock (_lock)
            {
                foreach (var kv in _cache)
                {
                    result.Add(new CacheEntryInfo
                    {
                        Name = kv.Key.Name,
                        Hash = kv.Key.GetHashCode(),
                        Version = kv.Value.Version,
                        HitCount = kv.Value.HitCount,
                        EntityCount = kv.Value.Result?.EntityCount ?? 0,
                        ChunkCount = kv.Value.Result?.ChunkCount ?? 0,
                        CreatedFrame = kv.Value.CreatedFrame,
                    });
                }
            }
            result.Sort((a, b) => b.HitCount.CompareTo(a.HitCount));
            return result;
        }

        // ──────────────────────────────────────────────────────────────────────
        // 内部类型
        // ──────────────────────────────────────────────────────────────────────

        private sealed class CacheEntry
        {
            public QueryResult Result;
            public ulong Version;
            public int HitCount;
            public LinkedListNode<QueryDesc> LruNode;
            public long CreatedFrame;
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 查询缓存统计
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 查询缓存统计信息。
    /// </summary>
    public class QueryCacheStats
    {
        /// <summary>命中次数</summary>
        public long Hits;

        /// <summary>未命中次数</summary>
        public long Misses;

        /// <summary>过期次数</summary>
        public long Expired;

        /// <summary>驱逐次数</summary>
        public long TotalEvicted;

        /// <summary>总缓存次数</summary>
        public long TotalCached;

        /// <summary>总查询次数</summary>
        public long TotalQueries;

        /// <summary>总匹配时间（毫秒）</summary>
        public double TotalMatchTimeMs;

        /// <summary>总失效次数</summary>
        public long TotalInvalidations;

        /// <summary>总清除次数</summary>
        public long TotalClears;

        /// <summary>总预热次数</summary>
        public long TotalWarmups;

        /// <summary>命中率</summary>
        public float HitRate => (Hits + Misses) > 0
            ? (float)Hits / (Hits + Misses) * 100f
            : 0f;

        /// <summary>平均匹配时间</summary>
        public double AvgMatchTimeMs => TotalQueries > 0
            ? TotalMatchTimeMs / TotalQueries
            : 0;

        public override string ToString()
        {
            return $"命中={Hits}, 未命中={Misses}, 命中率={HitRate:F1}%, " +
                   $"驱逐={TotalEvicted}, 过期={Expired}, " +
                   $"平均匹配={AvgMatchTimeMs:F3}ms";
        }
    }

    /// <summary>
    /// 缓存条目信息（调试用）。
    /// </summary>
    public struct CacheEntryInfo
    {
        public string Name;
        public int Hash;
        public ulong Version;
        public int HitCount;
        public int EntityCount;
        public int ChunkCount;
        public long CreatedFrame;

        public override string ToString()
        {
            return $"{Name}: hits={HitCount}, entities={EntityCount}, " +
                   $"chunks={ChunkCount}, v{Version}";
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 分层查询缓存
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 分层查询缓存：支持按系统分组的缓存管理。
    /// 每个系统有自己的缓存空间，避免相互干扰。
    /// </summary>
    public sealed class LayeredQueryCache : IDisposable
    {
        private readonly Dictionary<string, EntityQueryCache> _layerCaches = new();
        private readonly object _lock = new();
        private readonly int _maxSizePerLayer;

        /// <summary>层数</summary>
        public int LayerCount
        {
            get
            {
                lock (_lock)
                {
                    return _layerCaches.Count;
                }
            }
        }

        /// <summary>每层最大缓存数</summary>
        public int MaxSizePerLayer => _maxSizePerLayer;

        public LayeredQueryCache(int maxSizePerLayer = 128)
        {
            _maxSizePerLayer = maxSizePerLayer;
        }

        /// <summary>获取或创建指定层的缓存。</summary>
        public EntityQueryCache GetLayer(string layerName)
        {
            lock (_lock)
            {
                if (!_layerCaches.TryGetValue(layerName, out var cache))
                {
                    cache = new EntityQueryCache { MaxCacheSize = _maxSizePerLayer };
                    _layerCaches[layerName] = cache;
                }
                return cache;
            }
        }

        /// <summary>使所有层的缓存失效。</summary>
        public void InvalidateAll()
        {
            lock (_lock)
            {
                foreach (var cache in _layerCaches.Values)
                    cache.InvalidateAll();
            }
        }

        /// <summary>清除所有层的缓存。</summary>
        public void Clear()
        {
            lock (_lock)
            {
                foreach (var cache in _layerCaches.Values)
                    cache.Clear();
            }
        }

        /// <summary>获取所有层的统计信息。</summary>
        public string GetStats()
        {
            var sb = new StringBuilder();
            sb.AppendLine("═══ 分层查询缓存统计 ═══");
            lock (_lock)
            {
                foreach (var kv in _layerCaches)
                {
                    sb.AppendLine($"  [{kv.Key}] {kv.Value.GetStats()}");
                }
            }
            return sb.ToString();
        }

        public void Dispose()
        {
            lock (_lock)
            {
                foreach (var cache in _layerCaches.Values)
                    cache.Dispose();
                _layerCaches.Clear();
            }
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 查询缓存管理器
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 查询缓存管理器：统一管理全局查询缓存，与 ArchetypeManager 集成。
    /// </summary>
    public sealed class QueryCacheManager : IDisposable
    {
        private readonly EntityQueryCache _globalCache;
        private readonly LayeredQueryCache _layeredCache;
        private readonly ArchetypeManager _archetypeManager;
        private ulong _lastArchetypeVersion;
        private bool _disposed;

        /// <summary>全局缓存</summary>
        public EntityQueryCache GlobalCache => _globalCache;

        /// <summary>分层缓存</summary>
        public LayeredQueryCache LayeredCache => _layeredCache;

        public QueryCacheManager(ArchetypeManager archetypeManager)
        {
            _archetypeManager = archetypeManager ?? throw new ArgumentNullException(nameof(archetypeManager));
            _globalCache = new EntityQueryCache { MaxCacheSize = 512 };
            _layeredCache = new LayeredQueryCache(128);
        }

        /// <summary>
        /// 每帧调用，检查 archetype 是否变化并更新缓存。
        /// </summary>
        public void Update()
        {
            // FIXME: 实际应该从 ArchetypeManager 获取版本号
            // var currentVersion = _archetypeManager.Version;
            // if (currentVersion != _lastArchetypeVersion)
            // {
            //     _globalCache.InvalidateAll();
            //     _layeredCache.InvalidateAll();
            //     _lastArchetypeVersion = currentVersion;
            // }
        }

        /// <summary>
        /// 执行查询（带缓存）。
        /// </summary>
        public QueryResult ExecuteQuery(QueryDesc desc,
                                         Func<QueryDesc, QueryResult> matcher)
        {
            return _globalCache.GetOrAdd(desc, matcher);
        }

        /// <summary>
        /// 执行查询（带分层缓存）。
        /// </summary>
        public QueryResult ExecuteQuery(string layer, QueryDesc desc,
                                         Func<QueryDesc, QueryResult> matcher)
        {
            var cache = _layeredCache.GetLayer(layer);
            return cache.GetOrAdd(desc, matcher);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _globalCache.Dispose();
            _layeredCache.Dispose();
            _disposed = true;
        }
    }
}
