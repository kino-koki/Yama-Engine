using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace YamaEngine.Runtime
{
    // ============================================================
    //  1. 流送请求
    // ============================================================

    /// <summary>
    /// 资源流送请求
    /// </summary>
    public sealed class StreamRequest
    {
        /// <summary>请求唯一标识</summary>
        public Guid Id { get; } = Guid.NewGuid();

        /// <summary>资源标识</summary>
        public Guid ResourceId { get; }

        /// <summary>资源类型</summary>
        public StreamResourceType ResourceType { get; }

        /// <summary>优先级（数值越大越优先，0=最低）</summary>
        public int Priority { get; set; }

        /// <summary>请求大小（字节）</summary>
        public long SizeBytes { get; set; }

        /// <summary>加载完成回调</summary>
        public Action<Guid, object?>? OnLoaded { get; set; }

        /// <summary>请求状态</summary>
        public StreamRequestState State { get; internal set; } = StreamRequestState.Pending;

        /// <summary>加载耗时（毫秒）</summary>
        public double LoadTimeMs { get; internal set; }

        /// <summary>用户数据</summary>
        public object? UserData { get; set; }

        public StreamRequest(Guid resourceId, StreamResourceType type, int priority = 0)
        {
            ResourceId = resourceId;
            ResourceType = type;
            Priority = priority;
        }
    }

    /// <summary>
    /// 流送资源类型
    /// </summary>
    public enum StreamResourceType
    {
        Texture,
        Mesh,
        Audio,
        Animation,
        Shader,
        Other
    }

    /// <summary>
    /// 流送请求状态
    /// </summary>
    public enum StreamRequestState
    {
        Pending,
        Loading,
        Loaded,
        Failed,
        Cancelled
    }

    // ============================================================
    //  2. LRU 缓存
    // ============================================================

    /// <summary>
    /// 通用 LRU（最近最少使用）缓存
    /// </summary>
    public sealed class LruCache<TKey, TValue> where TKey : notnull
    {
        private readonly int _maxCapacity;
        private readonly Dictionary<TKey, LinkedListNode<LruEntry>> _cache;
        private readonly LinkedList<LruEntry> _lruList;
        private readonly object _lock = new();

        /// <summary>当前缓存数</summary>
        public int Count
        {
            get { lock (_lock) return _cache.Count; }
        }

        /// <summary>最大容量</summary>
        public int MaxCapacity => _maxCapacity;

        /// <summary>缓存命中数</summary>
        public long HitCount { get; private set; }

        /// <summary>缓存未命中数</summary>
        public long MissCount { get; private set; }

        private sealed class LruEntry
        {
            public TKey Key;
            public TValue Value;
            public long SizeBytes;
        }

        /// <summary>
        /// 创建 LRU 缓存
        /// </summary>
        /// <param name="maxCapacity">最大条目数</param>
        public LruCache(int maxCapacity)
        {
            if (maxCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(maxCapacity));
            _maxCapacity = maxCapacity;
            _cache = new Dictionary<TKey, LinkedListNode<LruEntry>>(maxCapacity);
            _lruList = new LinkedList<LruEntry>();
        }

        /// <summary>
        /// 尝试获取缓存值（命中则提升到 LRU 头部）
        /// </summary>
        public bool TryGet(TKey key, out TValue? value)
        {
            lock (_lock)
            {
                if (_cache.TryGetValue(key, out var node))
                {
                    _lruList.Remove(node);
                    _lruList.AddFirst(node);
                    value = node.Value.Value;
                    HitCount++;
                    return true;
                }
                value = default;
                MissCount++;
                return false;
            }
        }

        /// <summary>
        /// 添加或更新缓存条目
        /// </summary>
        public void AddOrUpdate(TKey key, TValue value, long sizeBytes = 0)
        {
            lock (_lock)
            {
                if (_cache.TryGetValue(key, out var existingNode))
                {
                    existingNode.Value.Value = value;
                    existingNode.Value.SizeBytes = sizeBytes;
                    _lruList.Remove(existingNode);
                    _lruList.AddFirst(existingNode);
                }
                else
                {
                    // 淘汰直到有空间
                    while (_cache.Count >= _maxCapacity)
                    {
                        var lru = _lruList.Last;
                        if (lru != null)
                        {
                            _cache.Remove(lru.Value.Key);
                            _lruList.RemoveLast();
                        }
                    }

                    var entry = new LruEntry { Key = key, Value = value, SizeBytes = sizeBytes };
                    var node = new LinkedListNode<LruEntry>(entry);
                    _cache[key] = node;
                    _lruList.AddFirst(node);
                }
            }
        }

        /// <summary>
        /// 移除指定条目
        /// </summary>
        public bool Remove(TKey key)
        {
            lock (_lock)
            {
                if (_cache.TryGetValue(key, out var node))
                {
                    _cache.Remove(key);
                    _lruList.Remove(node);
                    return true;
                }
                return false;
            }
        }

        /// <summary>
        /// 淘汰最久未使用的 N 个条目，返回被淘汰的条目
        /// </summary>
        public List<(TKey Key, TValue Value)> Evict(int count)
        {
            var evicted = new List<(TKey, TValue)>();
            lock (_lock)
            {
                for (int i = 0; i < count && _lruList.Last != null; i++)
                {
                    var lru = _lruList.Last.Value;
                    _cache.Remove(lru.Key);
                    _lruList.RemoveLast();
                    evicted.Add((lru.Key, lru.Value));
                }
            }
            return evicted;
        }

        /// <summary>
        /// 获取所有缓存条目（从新到旧）
        /// </summary>
        public List<(TKey Key, TValue Value, long SizeBytes)> GetAll()
        {
            var result = new List<(TKey, TValue, long)>(_cache.Count);
            lock (_lock)
            {
                foreach (var node in _lruList)
                {
                    result.Add((node.Key, node.Value, node.SizeBytes));
                }
            }
            return result;
        }

        /// <summary>命中率</summary>
        public float HitRate
        {
            get
            {
                long total = HitCount + MissCount;
                return total > 0 ? (float)HitCount / total : 0f;
            }
        }

        /// <summary>清空缓存</summary>
        public void Clear()
        {
            lock (_lock)
            {
                _cache.Clear();
                _lruList.Clear();
            }
        }
    }

    // ============================================================
    //  3. VirtualTexturingStreamer — 虚拟纹理流送
    // ============================================================

    /// <summary>
    /// 虚拟纹理流送器。
    /// 基于反馈分析的页面缓存系统，支持 Mip 链加载优先级。
    /// </summary>
    public sealed class VirtualTexturingStreamer
    {
        /// <summary>纹理页面的唯一标识</summary>
        public readonly struct PageKey : IEquatable<PageKey>
        {
            public readonly Guid TextureId;
            public readonly int MipLevel;
            public readonly int PageX;
            public readonly int PageY;

            public PageKey(Guid textureId, int mipLevel, int pageX, int pageY)
            {
                TextureId = textureId;
                MipLevel = mipLevel;
                PageX = pageX;
                PageY = pageY;
            }

            public bool Equals(PageKey other) =>
                TextureId == other.TextureId &&
                MipLevel == other.MipLevel &&
                PageX == other.PageX &&
                PageY == other.PageY;

            public override bool Equals(object? obj) => obj is PageKey k && Equals(k);
            public override int GetHashCode() => HashCode.Combine(TextureId, MipLevel, PageX, PageY);
        }

        /// <summary>页面数据</summary>
        public sealed class PageData
        {
            public byte[]? PixelData;
            public int Width;
            public int Height;
            public bool IsResident;
            public long LastAccessFrame;
            public int LoadPriority;
        }

        private const int MaxPages = 1024;
        private readonly LruCache<PageKey, PageData> _pageCache;
        private readonly HashSet<PageKey> _pageMisses;   // 当前帧的 Page Miss
        private readonly HashSet<PageKey> _requestedPages; // 已请求但未加载
        private readonly object _missLock = new();
        private long _frameCounter;
        private int _pageSize; // 默认 128x128

        /// <summary>页面尺寸（像素）</summary>
        public int PageSize => _pageSize;

        /// <summary>缓存页面数</summary>
        public int CachedPages => _pageCache.Count;

        /// <summary>当前帧 Page Miss 数</summary>
        public int PageMissCount
        {
            get { lock (_missLock) return _pageMisses.Count; }
        }

        public VirtualTexturingStreamer(int pageSize = 128)
        {
            _pageSize = pageSize;
            _pageCache = new LruCache<PageKey, PageData>(MaxPages);
            _pageMisses = new HashSet<PageKey>();
            _requestedPages = new HashSet<PageKey>();
        }

        /// <summary>
        /// 请求一个纹理页面
        /// </summary>
        public bool TryReservePage(Guid textureId, int mipLevel, int pageX, int pageY)
        {
            var key = new PageKey(textureId, mipLevel, pageX, pageY);

            if (_pageCache.TryGet(key, out var page) && page != null)
            {
                if (page.IsResident)
                {
                    page.LastAccessFrame = _frameCounter;
                    return true;
                }
            }

            // Page Miss
            lock (_missLock)
            {
                _pageMisses.Add(key);
            }
            return false;
        }

        /// <summary>
        /// 分析当前帧的 Page Miss，生成加载请求列表（按 Mip 链优先级排序）
        /// </summary>
        public List<StreamRequest> AnalyzeFeedbackAndGenerateRequests()
        {
            List<PageKey> misses;
            lock (_missLock)
            {
                misses = new List<PageKey>(_pageMisses);
                _pageMisses.Clear();
            }

            // 按 Mip 链优先级排序：低 Mip（大图）优先于高 Mip（细节）
            var sorted = misses
                .OrderBy(k => k.MipLevel)
                .ThenBy(k => k.PageX)
                .ThenBy(k => k.PageY)
                .ToList();

            var requests = new List<StreamRequest>();
            int basePriority = 100;

            foreach (var key in sorted)
            {
                if (_requestedPages.Contains(key)) continue;

                // Mip 越低优先级越高（距离 Camera 近的大页面更重要）
                int priority = basePriority - key.MipLevel * 10;

                var request = new StreamRequest(
                    key.TextureId, StreamResourceType.Texture, priority)
                {
                    UserData = new PageKey(key.TextureId, key.MipLevel, key.PageX, key.PageY)
                };

                request.OnLoaded = (resId, data) =>
                {
                    OnPageLoaded(key, data as PageData);
                };

                _requestedPages.Add(key);
                requests.Add(request);
            }

            return requests;
        }

        /// <summary>
        /// 页面加载完成回调
        /// </summary>
        private void OnPageLoaded(PageKey key, PageData? pageData)
        {
            if (pageData == null) return;

            pageData.IsResident = true;
            pageData.LastAccessFrame = _frameCounter;
            _pageCache.AddOrUpdate(key, pageData, _pageSize * _pageSize * 4); // 假设 RGBA
            _requestedPages.Remove(key);
        }

        /// <summary>
        /// 帧推进
        /// </summary>
        public void AdvanceFrame()
        {
            _frameCounter++;
        }

        /// <summary>
        /// 获取缓存统计
        /// </summary>
        public string GetStats()
        {
            return $"[VTStreamer] Cached={CachedPages}/{MaxPages}, " +
                   $"Misses={PageMissCount}, " +
                   $"HitRate={_pageCache.HitRate:P1}";
        }
    }

    // ============================================================
    //  4. MeshStreamer — 网格 LOD 流送
    // ============================================================

    /// <summary>
    /// 网格 LOD 流送器。基于相机距离的 LOD 切换和异步加载。
    /// </summary>
    public sealed class MeshStreamer
    {
        /// <summary>LOD 级别描述</summary>
        public sealed class LodLevel
        {
            public int Level;
            public float ScreenSize;       // 屏占比阈值
            public float SwitchDistance;    // 切换距离
            public int VertexCount;
            public int TriangleCount;
            public bool IsLoaded;
            public Guid LodMeshId;
        }

        /// <summary>网格流送条目</summary>
        public sealed class MeshStreamEntry
        {
            public Guid MeshId;
            public float LastKnownDistance;
            public int CurrentLod;
            public LodLevel[] LodLevels;
            public int MaxLod => LodLevels.Length - 1;
            public bool IsStreaming;
        }

        private readonly Dictionary<Guid, MeshStreamEntry> _entries;
        private readonly Dictionary<Guid, int> _pendingLoads; // meshId -> targetLod
        private readonly object _lock = new();
        private float _lodBias = 1.0f;

        /// <summary>LOD Bias：>1 推向更低细节，<1 推向更高细节</summary>
        public float LodBias { get => _lodBias; set => _lodBias = Math.Clamp(value, 0.1f, 4.0f); }

        /// <summary>注册网格数</summary>
        public int RegisteredMeshCount
        {
            get { lock (_lock) return _entries.Count; }
        }

        /// <summary>正在流送的网格数</summary>
        public int StreamingCount
        {
            get { lock (_lock) return _pendingLoads.Count; }
        }

        public MeshStreamer()
        {
            _entries = new Dictionary<Guid, MeshStreamEntry>();
            _pendingLoads = new Dictionary<Guid, int>();
        }

        /// <summary>
        /// 注册一个可流送的网格
        /// </summary>
        public void RegisterMesh(Guid meshId, float[] lodSwitchDistances, int[] vertexCounts, int[] triangleCounts)
        {
            if (lodSwitchDistances == null || vertexCounts == null || triangleCounts == null)
                throw new ArgumentNullException();
            if (lodSwitchDistances.Length != vertexCounts.Length ||
                vertexCounts.Length != triangleCounts.Length)
                throw new ArgumentException("LOD arrays must have same length.");

            var lods = new LodLevel[lodSwitchDistances.Length];
            for (int i = 0; i < lods.Length; i++)
            {
                lods[i] = new LodLevel
                {
                    Level = i,
                    ScreenSize = 1.0f / (i + 1),
                    SwitchDistance = lodSwitchDistances[i],
                    VertexCount = vertexCounts[i],
                    TriangleCount = triangleCounts[i],
                    IsLoaded = i == lods.Length - 1, // 最低LOD默认加载
                    LodMeshId = i == lods.Length - 1 ? meshId : Guid.Empty
                };
            }

            lock (_lock)
            {
                _entries[meshId] = new MeshStreamEntry
                {
                    MeshId = meshId,
                    LastKnownDistance = float.MaxValue,
                    CurrentLod = lods.Length - 1,
                    LodLevels = lods,
                    IsStreaming = false
                };
            }
        }

        /// <summary>
        /// 更新网格距离并计算 LOD 切换请求
        /// </summary>
        /// <returns>需要异步加载的 LOD 请求列表</returns>
        public List<StreamRequest> UpdateDistancesAndGenerateRequests(
            Dictionary<Guid, float> meshDistances)
        {
            var requests = new List<StreamRequest>();

            lock (_lock)
            {
                foreach (var kvp in meshDistances)
                {
                    if (!_entries.TryGetValue(kvp.Key, out var entry)) continue;

                    float effectiveDistance = kvp.Value / _lodBias;
                    entry.LastKnownDistance = effectiveDistance;

                    int targetLod = ComputeTargetLod(entry, effectiveDistance);

                    if (targetLod == entry.CurrentLod) continue;

                    // 需要切换 LOD
                    if (targetLod < entry.CurrentLod)
                    {
                        // 切换到更高质量 LOD
                        if (!entry.LodLevels[targetLod].IsLoaded && !_pendingLoads.ContainsKey(kvp.Key))
                        {
                            _pendingLoads[kvp.Key] = targetLod;
                            entry.IsStreaming = true;

                            var request = new StreamRequest(
                                entry.MeshId, StreamResourceType.Mesh,
                                priority: 50 - targetLod * 5)
                            {
                                SizeBytes = entry.LodLevels[targetLod].VertexCount * 32 +
                                           entry.LodLevels[targetLod].TriangleCount * 12
                            };
                            request.UserData = new { MeshId = entry.MeshId, TargetLod = targetLod };
                            request.OnLoaded = (meshId, data) =>
                            {
                                OnLodLoaded(entry.MeshId, targetLod);
                            };
                            requests.Add(request);
                        }
                    }
                    else
                    {
                        // 切换到更低质量 LOD（立即可用）
                        entry.CurrentLod = targetLod;
                    }
                }
            }

            return requests;
        }

        /// <summary>
        /// 根据距离计算目标 LOD 级别
        /// </summary>
        private static int ComputeTargetLod(MeshStreamEntry entry, float distance)
        {
            for (int i = 0; i < entry.LodLevels.Length; i++)
            {
                if (distance < entry.LodLevels[i].SwitchDistance)
                    return i;
            }
            return entry.MaxLod;
        }

        /// <summary>
        /// LOD 加载完成回调
        /// </summary>
        private void OnLodLoaded(Guid meshId, int targetLod)
        {
            lock (_lock)
            {
                if (_entries.TryGetValue(meshId, out var entry))
                {
                    entry.LodLevels[targetLod].IsLoaded = true;
                    entry.LodLevels[targetLod].LodMeshId = Guid.NewGuid(); // 模拟分配ID
                    entry.CurrentLod = targetLod;
                    entry.IsStreaming = false;
                }
                _pendingLoads.Remove(meshId);
            }
        }

        /// <summary>
        /// 获取所有网格的当前 LOD 状态
        /// </summary>
        public List<(Guid MeshId, int CurrentLod, float Distance, bool IsStreaming)> GetLodStatus()
        {
            var status = new List<(Guid, int, float, bool)>();
            lock (_lock)
            {
                foreach (var kvp in _entries)
                {
                    status.Add((
                        kvp.Value.MeshId,
                        kvp.Value.CurrentLod,
                        kvp.Value.LastKnownDistance,
                        kvp.Value.IsStreaming));
                }
            }
            return status;
        }
    }

    // ============================================================
    //  5. MemoryBudget — 内存预算管理
    // ============================================================

    /// <summary>
    /// 内存预算管理器。
    /// 按类别分配预算，超预算时自动驱逐低优先级资源。
    /// </summary>
    public sealed class MemoryBudget
    {
        /// <summary>内存类别预算条目</summary>
        public sealed class BudgetEntry
        {
            public string Category;
            public long BudgetBytes;
            public long UsedBytes;
            public float UsageRatio => BudgetBytes > 0 ? (float)UsedBytes / BudgetBytes : 0f;
        }

        private readonly Dictionary<string, BudgetEntry> _budgets;
        private readonly Dictionary<Guid, ResourceRecord> _resources;
        private readonly object _lock = new();
        private long _totalBudget;
        private long _totalUsed;

        /// <summary>总预算（字节）</summary>
        public long TotalBudget => _totalBudget;

        /// <summary>总已使用（字节）</summary>
        public long TotalUsed => Interlocked.Read(ref _totalUsed);

        /// <summary>总使用率</summary>
        public float TotalUsageRatio => _totalBudget > 0 ? (float)TotalUsed / _totalBudget : 0f;

        public MemoryBudget(long totalBudgetBytes = 512 * 1024 * 1024)
        {
            _totalBudget = totalBudgetBytes;
            _budgets = new Dictionary<string, BudgetEntry>();
            _resources = new Dictionary<Guid, ResourceRecord>();
        }

        /// <summary>
        /// 注册内存类别预算
        /// </summary>
        public void SetCategoryBudget(string category, long budgetBytes)
        {
            if (string.IsNullOrEmpty(category)) throw new ArgumentNullException(nameof(category));
            lock (_lock)
            {
                _budgets[category] = new BudgetEntry
                {
                    Category = category,
                    BudgetBytes = budgetBytes,
                    UsedBytes = 0
                };
            }
        }

        /// <summary>
        /// 注册默认类别预算
        /// </summary>
        public void SetDefaultBudgets(
            long textureBudget = 256 * 1024 * 1024,
            long meshBudget = 128 * 1024 * 1024,
            long audioBudget = 64 * 1024 * 1024,
            long otherBudget = 64 * 1024 * 1024)
        {
            SetCategoryBudget("Texture", textureBudget);
            SetCategoryBudget("Mesh", meshBudget);
            SetCategoryBudget("Audio", audioBudget);
            SetCategoryBudget("Other", otherBudget);
        }

        /// <summary>
        /// 记录资源分配
        /// </summary>
        public void RegisterResource(Guid resourceId, string category, long sizeBytes, int priority = 0)
        {
            lock (_lock)
            {
                _resources[resourceId] = new ResourceRecord
                {
                    ResourceId = resourceId,
                    Category = category,
                    SizeBytes = sizeBytes,
                    Priority = priority,
                    LastAccessFrame = 0
                };

                if (_budgets.TryGetValue(category, out var budget))
                    budget.UsedBytes += sizeBytes;

                Interlocked.Add(ref _totalUsed, sizeBytes);
            }
        }

        /// <summary>
        /// 更新资源访问时间
        /// </summary>
        public void TouchResource(Guid resourceId, long frame)
        {
            lock (_lock)
            {
                if (_resources.TryGetValue(resourceId, out var record))
                    record.LastAccessFrame = frame;
            }
        }

        /// <summary>
        /// 释放资源
        /// </summary>
        public void UnregisterResource(Guid resourceId)
        {
            lock (_lock)
            {
                if (_resources.TryGetValue(resourceId, out var record))
                {
                    if (_budgets.TryGetValue(record.Category, out var budget))
                        budget.UsedBytes -= record.SizeBytes;
                    Interlocked.Add(ref _totalUsed, -record.SizeBytes);
                    _resources.Remove(resourceId);
                }
            }
        }

        /// <summary>
        /// 检查并驱逐超预算的低优先级资源
        /// </summary>
        /// <returns>被驱逐的资源 ID 列表</returns>
        public List<Guid> EnforceBudget(long currentFrame)
        {
            var evicted = new List<Guid>();

            lock (_lock)
            {
                // 按类别检查预算
                foreach (var budget in _budgets.Values)
                {
                    while (budget.UsedBytes > budget.BudgetBytes)
                    {
                        // 找到该类别中优先级最低且最近最少使用的资源
                        ResourceRecord? victim = null;
                        foreach (var res in _resources.Values)
                        {
                            if (res.Category != budget.Category) continue;
                            if (victim == null ||
                                res.Priority < victim.Priority ||
                                (res.Priority == victim.Priority && res.LastAccessFrame < victim.LastAccessFrame))
                            {
                                victim = res;
                            }
                        }

                        if (victim == null) break;

                        budget.UsedBytes -= victim.SizeBytes;
                        Interlocked.Add(ref _totalUsed, -victim.SizeBytes);
                        _resources.Remove(victim.ResourceId);
                        evicted.Add(victim.ResourceId);
                    }
                }
            }

            return evicted;
        }

        /// <summary>
        /// 获取各分类预算使用情况
        /// </summary>
        public List<(string Category, long Budget, long Used, float Ratio)> GetBudgetStatus()
        {
            var status = new List<(string, long, long, float)>();
            lock (_lock)
            {
                foreach (var kvp in _budgets)
                {
                    status.Add((kvp.Value.Category, kvp.Value.BudgetBytes,
                        kvp.Value.UsedBytes, kvp.Value.UsageRatio));
                }
            }
            return status;
        }

        /// <summary>资源记录</summary>
        private sealed class ResourceRecord
        {
            public Guid ResourceId;
            public string Category = "";
            public long SizeBytes;
            public int Priority;
            public long LastAccessFrame;
        }
    }

    // ============================================================
    //  6. StreamingManager — 资源流送管理器
    // ============================================================

    /// <summary>
    /// 资源流送统一管理器。
    /// 协调虚拟纹理流送、网格 LOD 流送和内存预算管理。
    /// </summary>
    public sealed class StreamingManager : IDisposable
    {
        private readonly VirtualTexturingStreamer _vtStreamer;
        private readonly MeshStreamer _meshStreamer;
        private readonly MemoryBudget _memoryBudget;
        private readonly PriorityQueue<StreamRequest, int> _requestQueue; // 优先级队列
        private readonly Thread _streamingThread;
        private readonly ManualResetEventSlim _hasWork;
        private readonly object _queueLock = new();
        private volatile bool _isRunning;
        private long _frameCounter;
        private long _totalBytesStreamed;
        private long _totalRequestsProcessed;

        /// <summary>虚拟纹理流送器</summary>
        public VirtualTexturingStreamer VtStreamer => _vtStreamer;

        /// <summary>网格 LOD 流送器</summary>
        public MeshStreamer MeshStreamer => _meshStreamer;

        /// <summary>内存预算管理器</summary>
        public MemoryBudget MemoryBudget => _memoryBudget;

        /// <summary>是否运行中</summary>
        public bool IsRunning => _isRunning;

        /// <summary>队列中待处理请求数</summary>
        public int PendingRequestCount
        {
            get { lock (_queueLock) return _requestQueue.Count; }
        }

        /// <summary>已流送总字节</summary>
        public long TotalBytesStreamed => Interlocked.Read(ref _totalBytesStreamed);

        /// <summary>已处理请求总数</summary>
        public long TotalRequestsProcessed => Interlocked.Read(ref _totalRequestsProcessed);

        public StreamingManager(long memoryBudget = 512 * 1024 * 1024)
        {
            _vtStreamer = new VirtualTexturingStreamer();
            _meshStreamer = new MeshStreamer();
            _memoryBudget = new MemoryBudget(memoryBudget);
            _memoryBudget.SetDefaultBudgets();
            _requestQueue = new PriorityQueue<StreamRequest, int>();
            _hasWork = new ManualResetEventSlim(false);

            _streamingThread = new Thread(StreamingLoop)
            {
                Name = "YamaStreaming",
                IsBackground = true,
                Priority = ThreadPriority.BelowNormal
            };
        }

        /// <summary>
        /// 启动流送系统
        /// </summary>
        public void Start()
        {
            if (_isRunning) return;
            _isRunning = true;
            _streamingThread.Start();
        }

        /// <summary>
        /// 停止流送系统
        /// </summary>
        public void Stop()
        {
            _isRunning = false;
            _hasWork.Set();
            _streamingThread.Join(2000);
        }

        /// <summary>
        /// 提交流送请求
        /// </summary>
        public void SubmitRequest(StreamRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            lock (_queueLock)
            {
                _requestQueue.Enqueue(request, -request.Priority); // 负数使高优先级先出
            }
            _hasWork.Set();
        }

        /// <summary>
        /// 批量提交
        /// </summary>
        public void SubmitRequests(IEnumerable<StreamRequest> requests)
        {
            if (requests == null) throw new ArgumentNullException(nameof(requests));
            lock (_queueLock)
            {
                foreach (var req in requests)
                    _requestQueue.Enqueue(req, -req.Priority);
            }
            _hasWork.Set();
        }

        /// <summary>
        /// 帧更新（主线程调用）
        /// </summary>
        public void UpdateFrame(Dictionary<Guid, float>? meshDistances = null)
        {
            Interlocked.Increment(ref _frameCounter);

            // 分析虚拟纹理 Page Miss
            var vtRequests = _vtStreamer.AnalyzeFeedbackAndGenerateRequests();
            SubmitRequests(vtRequests);

            // 更新网格 LOD
            if (meshDistances != null && meshDistances.Count > 0)
            {
                var meshRequests = _meshStreamer.UpdateDistancesAndGenerateRequests(meshDistances);
                SubmitRequests(meshRequests);
            }

            // 内存预算检查
            var evicted = _memoryBudget.EnforceBudget(_frameCounter);
            if (evicted.Count > 0)
            {
                // 通知被驱逐的资源
                foreach (var resId in evicted)
                {
                    // 实际实现中通知资源管理器卸载
                }
            }

            _vtStreamer.AdvanceFrame();
        }

        /// <summary>
        /// 流送线程主循环
        /// </summary>
        private void StreamingLoop()
        {
            while (_isRunning)
            {
                _hasWork.Wait();
                _hasWork.Reset();

                while (true)
                {
                    StreamRequest? request = null;
                    lock (_queueLock)
                    {
                        if (_requestQueue.TryDequeue(out var req, out _))
                            request = req;
                    }

                    if (request == null || !_isRunning) break;

                    ProcessRequest(request);
                }
            }
        }

        /// <summary>
        /// 处理单个流送请求（模拟加载）
        /// </summary>
        private void ProcessRequest(StreamRequest request)
        {
            var sw = Stopwatch.StartNew();
            request.State = StreamRequestState.Loading;

            try
            {
                // 模拟资源加载（实际由 I/O 线程从磁盘/网络加载）
                SimulateResourceLoad(request);

                sw.Stop();
                request.LoadTimeMs = sw.Elapsed.TotalMilliseconds;
                request.State = StreamRequestState.Loaded;

                _memoryBudget.RegisterResource(
                    request.ResourceId,
                    request.ResourceType.ToString(),
                    request.SizeBytes,
                    request.Priority);

                Interlocked.Add(ref _totalBytesStreamed, request.SizeBytes);
                Interlocked.Increment(ref _totalRequestsProcessed);

                request.OnLoaded?.Invoke(request.ResourceId, null);
            }
            catch (Exception ex)
            {
                request.State = StreamRequestState.Failed;
                System.Diagnostics.Debug.WriteLine(
                    $"[StreamingManager] Failed to load {request.ResourceId}: {ex.Message}");
            }
        }

        /// <summary>
        /// 模拟资源加载
        /// </summary>
        private static void SimulateResourceLoad(StreamRequest request)
        {
            // 模拟 I/O 延迟（基于资源大小的虚拟延迟）
            int simulateMs = Math.Max(1, (int)(request.SizeBytes / (1024.0 * 1024.0 * 100)));
            if (simulateMs > 0)
                Thread.Sleep(Math.Min(simulateMs, 10));
        }

        /// <summary>
        /// 获取统计信息
        /// </summary>
        public string GetStats()
        {
            var budgetStatus = _memoryBudget.GetBudgetStatus();
            string budgetStr = string.Join(", ", budgetStatus.Select(b =>
                $"{b.Category}={b.Used / 1024}KB/{b.Budget / 1024}KB ({b.Ratio:P0})"));

            return $"[StreamingManager] Frame={_frameCounter}, " +
                   $"Pending={PendingRequestCount}, " +
                   $"Streamed={TotalBytesStreamed / 1024}KB, " +
                   $"Requests={TotalRequestsProcessed}, " +
                   $"VT: {_vtStreamer.GetStats()}, " +
                   $"MeshLOD: {_meshStreamer.RegisteredMeshCount} meshes, " +
                   $"Budget: [{budgetStr}]";
        }

        public void Dispose()
        {
            Stop();
            _hasWork.Dispose();
            GC.SuppressFinalize(this);
        }

        ~StreamingManager() => Dispose();
    }
}
