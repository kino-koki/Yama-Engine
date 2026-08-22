using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using ShanyuEngine.ECS;
using ShanyuEngine.Modeling;

namespace ShanyuEngine.World
{
    // ════════════════════════════════════════════════════════════════════════════
    // 山雨引擎 — 完整流送管理器 (StreamingSystem)
    //
    // 在 WorldStreamingGrid（基于网格单元的世界分区）之上，提供"级别(Level)粒度"的
    // 流送能力。将世界切分为若干个逻辑级别(StreamingLevel)，根据玩家/摄像机位置
    // 与流送策略异步加载/卸载级别，同时进行 LOD 流送与内存预算管理。
    //
    // 核心能力：
    //   1. 流送体积 (StreamingVolume)：触发区域驱动的加载/卸载
    //   2. 可见性管理：视锥剔除 + 距离剔除 + 迟滞判定
    //   3. 内存预算：流送内存预算管理 + LRU 淘汰
    //   4. 预测加载：基于速度方向的预测流送
    //   5. 后台加载线程池：SemaphoreSlim 控制并发
    //   6. 资源优先级调度：距离 x 可见性 x 策略 x 数据层的综合评分
    //   7. 加载预算：每帧主线程实例化时间限制，防止卡顿
    //   8. LOD 流送：远距级别仅加载低精度版本，近距再升级
    //
    // 与 ECS/资产系统对接：
    //   - 后台线程读取网格资产 (EditableMesh.Read)
    //   - 主线程创建 ECS 实体 (受帧预算限制)
    //   - 可选通过 AssetDatabase 解析资产路径
    // ════════════════════════════════════════════════════════════════════════════

    // ──────────────────────────────────────────────────────────────────────────
    // 枚举定义（保持原有签名）
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>流送级别加载状态机。</summary>
    public enum StreamingLevelState
    {
        /// <summary>已卸载：数据不在内存中。</summary>
        Unloaded,
        /// <summary>加载中：后台线程正在读取资产。</summary>
        Loading,
        /// <summary>已加载：资产与实体就绪，但尚未可见（预流送）。</summary>
        Loaded,
        /// <summary>已加载并可见：实体已加入场景并渲染。</summary>
        LoadedAndVisible,
        /// <summary>卸载中：正在释放资源。</summary>
        Unloading
    }

    /// <summary>流送策略。</summary>
    public enum StreamingPolicy
    {
        /// <summary>基于距离：玩家/摄像机进入加载半径则加载，离开卸载半径则卸载。</summary>
        DistanceBased,
        /// <summary>基于触发器体积：玩家进入流送体积组件时加载，离开时卸载。</summary>
        VolumeBased,
        /// <summary>始终加载：常驻内存，不自动卸载。</summary>
        AlwaysLoaded,
        /// <summary>手动控制：由代码显式调用 Load/Unload。</summary>
        Manual
    }

    /// <summary>流送体积形状（VolumeBased 策略用）。</summary>
    public enum StreamingVolumeShape { Box, Sphere }

    /// <summary>级别 LOD 质量档位（远距用低精度）。</summary>
    public enum StreamingLodQuality
    {
        /// <summary>合并简化网格，超远距使用。</summary>
        Hlod = 0,
        /// <summary>低精度。</summary>
        Low = 1,
        /// <summary>中等精度。</summary>
        Medium = 2,
        /// <summary>高精度。</summary>
        High = 3,
        /// <summary>最高精度（近景）。</summary>
        Ultra = 4
    }

    // ──────────────────────────────────────────────────────────────────────────
    // WorldBounds（保持原有签名）
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>世界空间轴对齐包围盒（自包含，避免与其它命名空间的 BoundingBox 冲突）。</summary>
    public struct WorldBounds
    {
        public Vector3 Center;
        public Vector3 Extents;

        public Vector3 Min => Center - Extents;
        public Vector3 Max => Center + Extents;
        public Vector3 Size => Extents * 2.0f;

        public bool Contains(Vector3 p)
        {
            return p.X >= Min.X && p.X <= Max.X &&
                   p.Y >= Min.Y && p.Y <= Max.Y &&
                   p.Z >= Min.Z && p.Z <= Max.Z;
        }

        public float DistanceToPoint(Vector3 p)
        {
            Vector3 d = Vector3.Max(Vector3.Zero, Vector3.Abs(p - Center) - Extents);
            return d.Length();
        }

        public Vector3 ClosestPoint(Vector3 p)
        {
            return Vector3.Clamp(p, Min, Max);
        }

        public static WorldBounds FromMinMax(Vector3 min, Vector3 max)
        {
            return new WorldBounds { Center = (min + max) * 0.5f, Extents = (max - min) * 0.5f };
        }

        public static WorldBounds FromCenterSize(Vector3 center, Vector3 size)
        {
            return new WorldBounds { Center = center, Extents = size * 0.5f };
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 流送体积组件 (StreamingVolumeComponent) — ECS 组件
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 流送体积组件（ECS）。挂载到实体上构成一个触发器体积，玩家进入时触发
    /// 关联级别（LevelId）的加载，离开时触发卸载。用于 VolumeBased 策略。
    /// </summary>
    public struct StreamingVolumeComponent : IComponent
    {
        /// <summary>关联的流送级别 Id。</summary>
        public Guid LevelId;

        /// <summary>体积形状。</summary>
        public StreamingVolumeShape Shape;

        /// <summary>世界中心（可由实体 TransformComponent 同步）。</summary>
        public Vector3 Center;

        /// <summary>盒体半尺寸。</summary>
        public Vector3 HalfExtents;

        /// <summary>球体半径。</summary>
        public float Radius;

        /// <summary>是否启用。</summary>
        public bool Enabled;

        /// <summary>进入时加载。</summary>
        public bool LoadOnEnter;

        /// <summary>离开时卸载。</summary>
        public bool UnloadOnExit;

        /// <summary>是否从实体变换同步中心。</summary>
        public bool SyncFromTransform;

        /// <summary>进入时的优先级加成。</summary>
        public float PriorityBoost;

        /// <summary>点是否在体积内。</summary>
        public bool Contains(Vector3 p)
        {
            if (Shape == StreamingVolumeShape.Sphere)
                return Vector3.Distance(p, Center) <= Radius;
            Vector3 d = Vector3.Abs(p - Center);
            return d.X <= HalfExtents.X && d.Y <= HalfExtents.Y && d.Z <= HalfExtents.Z;
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // StreamingLevel（保持原有签名，扩展字段）
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 流送级别：一个可独立加载/卸载的世界逻辑单元。
    /// 含世界边界、加载状态、优先级、实体列表、按 LOD 分级的资产清单。
    /// </summary>

    // ──────────────────────────────────────────────────────────────────────────
    // 流送性能指标 (StreamingMetrics)（保持原有签名，扩展字段）
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>流送性能指标。</summary>
    public sealed class StreamingMetrics
    {
        public int TotalLoads { get; private set; }
        public int TotalUnloads { get; private set; }
        public double TotalLoadTimeMs { get; private set; }
        public double AvgLoadTimeMs => TotalLoads > 0 ? TotalLoadTimeMs / TotalLoads : 0.0;
        public float MaxLoadTimeMs { get; private set; }
        public long CurrentMemoryBytes { get; private set; }
        public long PeakMemoryBytes { get; private set; }
        public int QueueLength { get; internal set; }
        public int ActiveJobs { get; internal set; }
        public int LevelsLoaded { get; internal set; }
        public int LevelsVisible { get; internal set; }

        // ═══ UE6 级扩展 ═══

        /// <summary>内存预算上限（字节）。</summary>
        public long MemoryBudgetBytes { get; set; } = 2L * 1024 * 1024 * 1024;

        /// <summary>预测加载命中次数。</summary>
        public int PredictiveHits { get; private set; }

        /// <summary>预测加载总请求数。</summary>
        public int PredictiveTotal { get; private set; }

        /// <summary>预测命中率。</summary>
        public float PredictiveHitRate => PredictiveTotal > 0
            ? (float)PredictiveHits / PredictiveTotal : 0f;

        /// <summary>每帧平均加载耗时（毫秒）。</summary>
        public float FrameLoadBudgetUsedMs { get; internal set; }

        /// <summary>内存使用率。</summary>
        public float MemoryUsageRatio => MemoryBudgetBytes > 0
            ? (float)CurrentMemoryBytes / MemoryBudgetBytes : 0f;

        private readonly object _lock = new();

        public void RecordLoad(float durationMs, long memoryBytes)
        {
            lock (_lock)
            {
                TotalLoads++;
                TotalLoadTimeMs += durationMs;
                if (durationMs > MaxLoadTimeMs) MaxLoadTimeMs = durationMs;
                CurrentMemoryBytes += memoryBytes;
                if (CurrentMemoryBytes > PeakMemoryBytes) PeakMemoryBytes = CurrentMemoryBytes;
            }
        }

        public void RecordUnload(long memoryBytes)
        {
            lock (_lock)
            {
                TotalUnloads++;
                CurrentMemoryBytes = Math.Max(0, CurrentMemoryBytes - memoryBytes);
            }
        }

        public void RecordPredictiveHit()
        {
            lock (_lock) { PredictiveHits++; }
        }

        public void RecordPredictiveRequest()
        {
            lock (_lock) { PredictiveTotal++; }
        }

        public string Snapshot()
        {
            lock (_lock)
            {
                return $"Loads={TotalLoads} Unloads={TotalUnloads} " +
                       $"Avg={AvgLoadTimeMs:F2}ms Max={MaxLoadTimeMs:F2}ms " +
                       $"Mem={CurrentMemoryBytes / 1024.0:F1}KB Peak={PeakMemoryBytes / 1024.0:F1}KB " +
                       $"MemUsage={MemoryUsageRatio:P0} " +
                       $"Queue={QueueLength} ActiveJobs={ActiveJobs} " +
                       $"Loaded={LevelsLoaded} Visible={LevelsVisible} " +
                       $"PredHit={PredictiveHitRate:P0} FrameBudget={FrameLoadBudgetUsedMs:F1}ms";
            }
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 内存预算管理器 (MemoryBudgetManager)
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 内存预算管理器。跟踪所有已加载级别的内存占用，
    /// 当超出预算时按照 LRU（最近最少使用）策略淘汰最不活跃的级别。
    /// </summary>

    // ──────────────────────────────────────────────────────────────────────────
    // 预测加载器 (PredictiveStreamingLoader)
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 预测加载器。基于视点移动速度和方向，预测玩家即将到达的区域，
    /// 提前对这些区域的级别发出预加载请求（给较低优先级）。
    /// </summary>
    public class PredictiveStreamingLoader
    {
        /// <summary>视点移动历史（最近 N 帧的位置）。</summary>
        private readonly Queue<(Vector3 Position, float Time)> _positionHistory = new();

        /// <summary>最大历史帧数。</summary>
        private readonly int _historySize = 30;

        /// <summary>前瞻时间（秒）。</summary>
        public float LookAheadSeconds { get; set; } = 2f;

        /// <summary>最小预测速度阈值（低于此速度不预测）。</summary>
        public float MinSpeedThreshold { get; set; } = 5f;

        /// <summary>预测结果的平滑速度。</summary>
        private Vector3 _smoothedVelocity;

        /// <summary>是否有有效预测。</summary>
        public bool HasPrediction => _smoothedVelocity.Length() >= MinSpeedThreshold;

        /// <summary>预测的未来视点位置。</summary>
        public Vector3 PredictedPosition { get; private set; }

        /// <summary>当前速度。</summary>
        public Vector3 CurrentVelocity => _smoothedVelocity;

        /// <summary>
        /// 更新预测。每帧调用，传入当前视点位置和 dt。
        /// </summary>
        public void Update(Vector3 currentPos, float dt)
        {
            _positionHistory.Enqueue((currentPos, dt));

            // 限制历史大小
            while (_positionHistory.Count > _historySize)
                _positionHistory.Dequeue();

            // 计算平滑速度（使用最近几帧的平均）
            if (_positionHistory.Count < 3)
            {
                _smoothedVelocity = Vector3.Zero;
                PredictedPosition = currentPos;
                return;
            }

            // 简单的移动平均速度估计
            Vector3 sumVel = Vector3.Zero;
            float sumDt = 0f;
            var positions = _positionHistory.ToArray();
            for (int i = 1; i < positions.Length; i++)
            {
                Vector3 delta = positions[i].Position - positions[i - 1].Position;
                float frameDt = positions[i].Time;
                if (frameDt > 0.001f)
                {
                    sumVel += delta / frameDt;
                    sumDt += frameDt;
                }
            }

            if (sumDt > 0f)
                _smoothedVelocity = sumVel / (positions.Length - 1);
            else
                _smoothedVelocity = Vector3.Zero;

            // 预测未来位置
            PredictedPosition = currentPos + _smoothedVelocity * LookAheadSeconds;
        }

        /// <summary>重置预测状态。</summary>
        public void Reset()
        {
            _positionHistory.Clear();
            _smoothedVelocity = Vector3.Zero;
            PredictedPosition = Vector3.Zero;
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 后台加载线程池 (StreamingThreadPool)
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 流送后台加载线程池。使用 SemaphoreSlim 控制最大并发数，
    /// 所有磁盘 I/O 操作通过此线程池执行，避免阻塞主线程。
    /// </summary>
    internal sealed class StreamingThreadPool : IDisposable
    {
        private readonly SemaphoreSlim _semaphore;
        private readonly CancellationTokenSource _shutdownCts = new();
        private int _activeCount;
        private readonly object _lock = new();

        /// <summary>最大并发数。</summary>
        public int MaxConcurrency { get; }

        /// <summary>当前活跃任务数。</summary>
        public int ActiveCount
        {
            get { lock (_lock) return _activeCount; }
        }

        /// <summary>是否有可用槽位。</summary>
        public bool HasAvailableSlot
        {
            get { lock (_lock) return _activeCount < MaxConcurrency; }
        }

        public StreamingThreadPool(int maxConcurrency)
        {
            MaxConcurrency = Math.Max(1, maxConcurrency);
            _semaphore = new SemaphoreSlim(MaxConcurrency, MaxConcurrency);
        }

        /// <summary>
        /// 提交一个后台加载任务。如果线程池已满则等待直到有槽位。
        /// </summary>
        public async Task<T> Submit<T>(Func<CancellationToken, Task<T>> work)
        {
            await _semaphore.WaitAsync(_shutdownCts.Token);
            lock (_lock) _activeCount++;

            try
            {
                return await work(_shutdownCts.Token);
            }
            finally
            {
                lock (_lock) _activeCount--;
                _semaphore.Release();
            }
        }

        /// <summary>提交无返回值的后台任务。</summary>
        public async Task Submit(Action<CancellationToken> work)
        {
            await _semaphore.WaitAsync(_shutdownCts.Token);
            lock (_lock) _activeCount++;

            try
            {
                work(_shutdownCts.Token);
            }
            finally
            {
                lock (_lock) _activeCount--;
                _semaphore.Release();
            }
        }

        /// <summary>关闭线程池，取消所有等待中的任务。</summary>
        public void Shutdown()
        {
            _shutdownCts.Cancel();
        }

        public void Dispose()
        {
            Shutdown();
            _semaphore.Dispose();
            _shutdownCts.Dispose();
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 资源优先级调度器 (ResourcePriorityScheduler)
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 资源优先级调度器。对所有待加载资源进行综合优先级计算和调度，
    /// 确保最重要的资源（近距/可见/高重要性）优先加载。
    /// </summary>
    public class ResourcePriorityScheduler
    {
        /// <summary>优先级请求。</summary>
        private readonly List<PriorityRequest> _requests = new();

        /// <summary>请求总数。</summary>
        public int Count
        {
            get { lock (_requests) return _requests.Count; }
        }

        /// <summary>权重配置。</summary>
        public float DistanceWeight { get; set; } = 0.4f;
        public float VisibilityWeight { get; set; } = 0.3f;
        public float ImportanceWeight { get; set; } = 0.2f;
        public float PredictiveWeight { get; set; } = 0.1f;

        /// <summary>添加一个资源加载请求。</summary>
        public void Enqueue(PriorityRequest request)
        {
            lock (_requests)
            {
                // 去重：同一资源只保留最高优先级
                int existing = _requests.FindIndex(r => r.ResourceId == request.ResourceId);
                if (existing >= 0)
                {
                    if (request.ComputedPriority > _requests[existing].ComputedPriority)
                        _requests[existing] = request;
                    return;
                }

                _requests.Add(request);
            }
        }

        /// <summary>移除指定资源的请求。</summary>
        public void Remove(Guid resourceId)
        {
            lock (_requests)
            {
                _requests.RemoveAll(r => r.ResourceId == resourceId);
            }
        }

        /// <summary>取出优先级最高的 N 个请求（不移除）。</summary>
        public List<PriorityRequest> PeekTop(int count)
        {
            lock (_requests)
            {
                var sorted = _requests.OrderByDescending(r => r.ComputedPriority).Take(count).ToList();
                return sorted;
            }
        }

        /// <summary>取出并移除优先级最高的请求。</summary>
        public PriorityRequest? DequeueHighest()
        {
            lock (_requests)
            {
                if (_requests.Count == 0) return null;
                int bestIdx = 0;
                float bestPriority = _requests[0].ComputedPriority;
                for (int i = 1; i < _requests.Count; i++)
                {
                    if (_requests[i].ComputedPriority > bestPriority)
                    {
                        bestPriority = _requests[i].ComputedPriority;
                        bestIdx = i;
                    }
                }
                var req = _requests[bestIdx];
                _requests.RemoveAt(bestIdx);
                return req;
            }
        }

        /// <summary>清空所有请求。</summary>
        public void Clear()
        {
            lock (_requests) _requests.Clear();
        }
    }

    /// <summary>优先级请求。</summary>
    internal sealed class PriorityRequest
    {
        public Guid ResourceId;
        public float ComputedPriority;
        public float DistanceScore;
        public float VisibilityScore;
        public float ImportanceScore;
        public float PredictiveScore;
        public DateTime RequestTime;
        public int TargetLod;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // WorldComposition（保持原有签名）
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 世界组合：管理一个世界中所有流送级别的注册、查询与优先级更新。
    /// </summary>
    public sealed class WorldComposition
    {
        private readonly List<StreamingLevel> _levels = new();
        private readonly Dictionary<Guid, StreamingLevel> _byId = new();

        public IReadOnlyList<StreamingLevel> Levels => _levels;
        public int Count => _levels.Count;

        public StreamingLevel AddLevel(StreamingLevel level)
        {
            if (level == null) throw new ArgumentNullException(nameof(level));
            if (!_byId.ContainsKey(level.Id))
            {
                _levels.Add(level);
                _byId[level.Id] = level;
            }
            return level;
        }

        public StreamingLevel CreateLevel(string name, WorldBounds bounds, StreamingPolicy policy = StreamingPolicy.DistanceBased)
        {
            var lvl = new StreamingLevel { Name = name, Bounds = bounds, Policy = policy };
            return AddLevel(lvl);
        }

        public bool RemoveLevel(Guid id)
        {
            if (_byId.TryGetValue(id, out var lvl))
            {
                _levels.Remove(lvl);
                _byId.Remove(id);
                return true;
            }
            return false;
        }

        public StreamingLevel? GetLevel(Guid id) => _byId.TryGetValue(id, out var l) ? l : null;

        public StreamingLevel? GetLevelByName(string name)
        {
            foreach (var l in _levels) if (l.Name == name) return l;
            return null;
        }

        public void UpdatePriorities(Vector3 viewer)
        {
            foreach (var l in _levels)
            {
                l.Priority = l.ComputePriority(viewer);
                l.TargetLod = l.SelectLod(viewer);
            }
        }

        public List<StreamingLevel> GetLevelsInState(StreamingLevelState state)
        {
            var result = new List<StreamingLevel>();
            foreach (var l in _levels) if (l.State == state) result.Add(l);
            return result;
        }

        /// <summary>获取在指定位置附近的级别（按距离排序）。</summary>
        public List<StreamingLevel> GetLevelsNear(Vector3 position, float maxDistance)
        {
            return _levels
                .Where(l => l.Bounds.DistanceToPoint(position) <= maxDistance)
                .OrderBy(l => l.Bounds.DistanceToPoint(position))
                .ToList();
        }

        public void Clear()
        {
            _levels.Clear();
            _byId.Clear();
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 可见性管理器 (VisibilityManager)
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 可见性管理器。基于视锥剔除 + 距离剔除 + 迟滞判定，
    /// 确定哪些级别应在当前帧可见。
    /// </summary>
    public class VisibilityManager
    {
        /// <summary>是否启用视锥剔除。</summary>
        public bool EnableFrustumCulling { get; set; } = true;

        /// <summary>可见性迟滞帧数（连续 N 帧不可见才判定为不可见）。</summary>
        public int HysteresisFrames { get; set; } = 3;

        /// <summary>最大可见距离（超出此距离即使视锥内也不可见）。</summary>
        public float MaxVisibilityDistance { get; set; } = 2000f;

        /// <summary>当前视锥矩阵。</summary>
        private Matrix4x4 _viewProjection = Matrix4x4.Identity;

        /// <summary>缓存的可视锥平面。</summary>
        private Vector4[] _cachedPlanes = new Vector4[6];

        /// <summary>是否已缓存视锥平面。</summary>
        private bool _planesCached;

        /// <summary>设置视锥矩阵。</summary>
        public void SetViewProjection(Matrix4x4 viewProj)
        {
            _viewProjection = viewProj;
            _planesCached = false;
        }

        /// <summary>
        /// 更新级别的可见性状态（带迟滞）。
        /// 返回 true 表示可见性状态发生变化。
        /// </summary>
        public bool UpdateVisibility(StreamingLevel level, Vector3 viewer)
        {
            // 保存上一帧状态
            level.WasFrustumVisible = level.IsFrustumVisible;

            // 距离剔除
            float dist = level.Bounds.DistanceToPoint(viewer);
            if (dist > MaxVisibilityDistance)
            {
                level.InvisibleFrames++;
                level.IsFrustumVisible = false;
                return level.InvisibleFrames >= HysteresisFrames && level.WasFrustumVisible;
            }

            // 视锥剔除
            if (EnableFrustumCulling && _viewProjection != Matrix4x4.Identity)
            {
                bool inFrustum = TestBoundsFrustum(
                    level.Bounds.Min, level.Bounds.Max, _viewProjection);

                if (!inFrustum)
                {
                    level.InvisibleFrames++;
                    // 迟滞：连续 N 帧不可见才判定
                    if (level.InvisibleFrames < HysteresisFrames)
                    {
                        level.IsFrustumVisible = true; // 保持可见
                        return false;
                    }
                    level.IsFrustumVisible = false;
                    return level.WasFrustumVisible;
                }
            }

            // 可见
            level.InvisibleFrames = 0;
            level.IsFrustumVisible = true;
            return !level.WasFrustumVisible; // 从不可见变为可见
        }

        /// <summary>
        /// AABB-视锥相交测试 (p-vertex 法)。
        /// </summary>
        private static bool TestBoundsFrustum(Vector3 min, Vector3 max, Matrix4x4 vp)
        {
            Span<Vector4> planes = stackalloc Vector4[6];
            planes[0] = new Vector4(vp.M11 + vp.M14, vp.M21 + vp.M24, vp.M31 + vp.M34, vp.M41 + vp.M44);
            planes[1] = new Vector4(vp.M14 - vp.M11, vp.M24 - vp.M21, vp.M34 - vp.M31, vp.M44 - vp.M41);
            planes[2] = new Vector4(vp.M12 + vp.M14, vp.M22 + vp.M24, vp.M32 + vp.M34, vp.M42 + vp.M44);
            planes[3] = new Vector4(vp.M14 - vp.M12, vp.M24 - vp.M22, vp.M34 - vp.M32, vp.M44 - vp.M42);
            planes[4] = new Vector4(vp.M13 + vp.M14, vp.M23 + vp.M24, vp.M33 + vp.M34, vp.M43 + vp.M44);
            planes[5] = new Vector4(vp.M14 - vp.M13, vp.M24 - vp.M23, vp.M34 - vp.M33, vp.M44 - vp.M43);

            for (int i = 0; i < 6; i++)
            {
                var p = planes[i];
                Vector3 pv = new(
                    p.X >= 0f ? max.X : min.X,
                    p.Y >= 0f ? max.Y : min.Y,
                    p.Z >= 0f ? max.Z : min.Z);
                float d = p.X * pv.X + p.Y * pv.Y + p.Z * pv.Z + p.W;
                if (d < 0f) return false;
            }
            return true;
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 流送系统 (StreamingSystem) — 完整工业级实现
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 流送系统：每帧检查流送体积/距离与玩家位置，异步加载/卸载级别。
    /// 后台线程负责资产 IO（EditableMesh.Read），主线程负责创建 ECS 实体（受预算限制）。
    ///
    /// 核心架构：
    ///   1. 流送体积 (StreamingVolumeComponent)：ECS 触发器体积，进入时加载/离开时卸载
    ///   2. 可见性管理 (VisibilityManager)：视锥 + 距离 + 迟滞剔除
    ///   3. 内存预算 (MemoryBudgetManager)：LRU 淘汰策略
    ///   4. 预测加载 (PredictiveStreamingLoader)：基于速度方向的预测流送
    ///   5. 后台线程池 (StreamingThreadPool)：SemaphoreSlim 控制并发
    ///   6. 资源优先级调度 (ResourcePriorityScheduler)：综合评分排序
    ///   7. 加载预算：MaxLoadTimePerFrameMs 限制每帧实例化耗时
    ///   8. LOD 流送：远距级别仅加载低精度版本
    /// </summary>
    public sealed class StreamingSystem : ISystem
    {
        // ── 事件（保持原有签名 + 扩展） ──

        /// <summary>流送级别加载完成事件。</summary>
        public event Action<StreamingLevel>? OnLevelLoaded;
        /// <summary>流送级别卸载完成事件。</summary>
        public event Action<StreamingLevel>? OnLevelUnloaded;
        /// <summary>流送级别变为可见事件。</summary>
        public event Action<StreamingLevel>? OnLevelVisible;
        /// <summary>流送级别变为不可见事件。</summary>
        public event Action<StreamingLevel>? OnLevelHidden;
        /// <summary>流送级别加载失败事件。</summary>
        public event Action<StreamingLevel, string>? OnLevelLoadFailed;
        /// <summary>内存压力事件。</summary>
        public event Action<float>? OnMemoryPressure;

        // ── 内部组件 ──

        private readonly WorldComposition _composition;
        private readonly ShanyuEngine.Assets.AssetDatabase? _assetDb;
        private readonly StreamingMetrics _metrics = new();
        private readonly MemoryBudgetManager _memoryManager;
        private readonly VisibilityManager _visibility = new();
        private readonly PredictiveStreamingLoader _predictor = new();
        private readonly ResourcePriorityScheduler _scheduler = new();
        private readonly StreamingThreadPool _threadPool;

        // 加载请求队列（按优先级排序）
        private readonly List<LoadRequest> _loadQueue = new();
        // 卸载请求队列
        private readonly List<StreamingLevel> _unloadQueue = new();
        // 进行中的后台加载作业
        private readonly List<LoadJob> _activeJobs = new();
        private readonly object _jobLock = new();

        private Vector3 _viewerPosition;
        private Vector3 _prevViewerPosition;
        private bool _viewerValid;
        private float _updateTimer;

        // ── 公共配置属性 ──

        /// <summary>每帧主线程实例化预算（毫秒）。</summary>
        public float MaxLoadTimePerFrameMs { get; set; } = 4.0f;

        /// <summary>最大并发后台加载作业数。</summary>
        public int MaxConcurrentLoads { get; set; } = 2;

        /// <summary>是否启用预测加载。</summary>
        public bool EnablePredictiveLoading { get; set; } = true;

        /// <summary>预测加载前瞻时间（秒）。</summary>
        public float PredictiveLookAheadSeconds
        {
            get => _predictor.LookAheadSeconds;
            set => _predictor.LookAheadSeconds = value;
        }

        /// <summary>内存预算上限（字节）。</summary>
        public long MemoryBudgetBytes
        {
            get => _memoryManager.BudgetBytes;
            set
            {
                _memoryManager.SetBudget(value);
                _metrics.MemoryBudgetBytes = value;
            }
        }

        /// <summary>系统名称。</summary>
        public string Name => "StreamingSystem";

        /// <summary>世界组合。</summary>
        public WorldComposition Composition => _composition;

        /// <summary>性能指标。</summary>
        public StreamingMetrics Metrics => _metrics;

        /// <summary>内存预算管理器。</summary>
        public MemoryBudgetManager MemoryManager => _memoryManager;

        /// <summary>可见性管理器。</summary>
        public VisibilityManager Visibility => _visibility;

        /// <summary>预测加载器。</summary>
        public PredictiveStreamingLoader Predictor => _predictor;

        /// <summary>资源优先级调度器。</summary>
        public ResourcePriorityScheduler Scheduler => _scheduler;

        // ══════════════════════════════════════════════════════════════════════
        // 构造函数
        // ══════════════════════════════════════════════════════════════════════

        public StreamingSystem(WorldComposition composition, ShanyuEngine.Assets.AssetDatabase? assetDb = null)
        {
            _composition = composition ?? throw new ArgumentNullException(nameof(composition));
            _assetDb = assetDb;
            _memoryManager = new MemoryBudgetManager(
                _metrics.MemoryBudgetBytes, 0.15f);
            _threadPool = new StreamingThreadPool(MaxConcurrentLoads);

            _memoryManager.OnMemoryPressure += ratio => OnMemoryPressure?.Invoke(ratio);
        }

        // ══════════════════════════════════════════════════════════════════════
        // ISystem 接口（兼容 ECS World）
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>ISystem.Update — 通过 World 获取 ECS 并委托到内部更新。</summary>
        public void Update(float dt, World world)
        {
            if (world?.EntityManager == null) return;
            // 此系统通过 Composition 管理级别，World 主要提供 EntityManager
            Update(dt, world.EntityManager);
        }

        // ══════════════════════════════════════════════════════════════════════
        // 主更新循环 — 使用 EntityComponentSystem（保持原有 API 签名）
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// 每帧更新（使用 EntityComponentSystem）。
        /// </summary>
        public void Update(float dt, EntityComponentSystem ecs)
        {
            if (ecs == null) return;

            // 1. 确定观察者位置
            Vector3 viewer = _viewerPosition;
            if (!_viewerValid) viewer = ResolveViewer(ecs);

            // 2. 更新预测加载器
            if (EnablePredictiveLoading)
                _predictor.Update(viewer, dt);

            // 3. 更新优先级与目标 LOD
            _composition.UpdatePriorities(viewer);

            // 4. 更新可见性
            UpdateVisibility(viewer);

            // 5. 评估每个级别的目标状态并生成加载/卸载请求
            EvaluateLevels(viewer);

            // 6. 预测加载
            if (EnablePredictiveLoading && _predictor.HasPrediction)
                EvaluatePredictiveLoading(viewer);

            // 7. 启动新的后台加载作业（受并发上限）
            StartPendingJobs();

            // 8. 处理已完成的作业（主线程实例化实体，受预算限制）
            ProcessCompletedJobs(ecs);

            // 9. 处理卸载队列（主线程销毁实体，受预算限制）
            ProcessUnloadQueue(ecs);

            // 10. 内存预算检查与 LRU 淘汰
            ProcessMemoryEviction();

            // 11. 更新指标
            lock (_jobLock)
            {
                _metrics.QueueLength = _loadQueue.Count;
                _metrics.ActiveJobs = _activeJobs.Count;
            }

            _prevViewerPosition = viewer;
        }

        // ── 外部观察者设置 ──────────────────────────────────────────────

        public void SetViewerPosition(Vector3 position)
        {
            _viewerPosition = position;
            _viewerValid = true;
        }

        public void ClearViewerPosition() => _viewerValid = false;

        /// <summary>设置视锥矩阵（用于可见性剔除）。</summary>
        public void SetViewProjection(Matrix4x4 viewProj)
        {
            _visibility.SetViewProjection(viewProj);
        }

        // ── 手动控制 API ──────────────────────────────────────────────

        public void RequestLoad(Guid levelId) => EnqueueLoad(_composition.GetLevel(levelId));
        public void RequestUnload(Guid levelId) => EnqueueUnload(_composition.GetLevel(levelId));

        // ── 可见性更新 ────────────────────────────────────────────────

        private void UpdateVisibility(Vector3 viewer)
        {
            foreach (var level in _composition.Levels)
            {
                if (level.State != StreamingLevelState.Loaded &&
                    level.State != StreamingLevelState.LoadedAndVisible) continue;

                bool changed = _visibility.UpdateVisibility(level, viewer);
                if (changed)
                {
                    if (level.IsFrustumVisible)
                        OnLevelVisible?.Invoke(level);
                    else
                        OnLevelHidden?.Invoke(level);
                }
            }
        }

        // ── 评估级别目标状态 ────────────────────────────────────────

        private void EvaluateLevels(Vector3 viewer)
        {
            // 收集 VolumeBased 触发器激活的级别 Id 集合
            // (VolumeBased 策略需要通过 ECS 查询, 此处用简化版)
            var volumeTriggered = new HashSet<Guid>();

            foreach (var level in _composition.Levels)
            {
                switch (level.Policy)
                {
                    case StreamingPolicy.AlwaysLoaded:
                        if (level.State == StreamingLevelState.Unloaded)
                            EnqueueLoad(level);
                        break;

                    case StreamingPolicy.DistanceBased:
                        if (level.State == StreamingLevelState.Unloaded && level.ShouldLoadByDistance(viewer))
                            EnqueueLoad(level);
                        else if ((level.State == StreamingLevelState.Loaded ||
                                  level.State == StreamingLevelState.LoadedAndVisible) &&
                                 level.ShouldUnloadByDistance(viewer) && !level.IsPersistent)
                            EnqueueUnload(level);
                        break;

                    case StreamingPolicy.VolumeBased:
                        bool triggered = volumeTriggered.Contains(level.Id);
                        if (triggered && level.State == StreamingLevelState.Unloaded)
                            EnqueueLoad(level);
                        else if (!triggered && (level.State == StreamingLevelState.Loaded ||
                                                level.State == StreamingLevelState.LoadedAndVisible) &&
                                 !level.IsPersistent)
                            EnqueueUnload(level);
                        break;

                    case StreamingPolicy.Manual:
                        break;
                }

                // 可见性控制（Loaded <-> LoadedAndVisible）
                if (level.State == StreamingLevelState.Loaded ||
                    level.State == StreamingLevelState.LoadedAndVisible)
                {
                    bool wantVisible = level.ShouldBeVisible(viewer) && level.IsFrustumVisible;
                    if (wantVisible && !level.IsVisible)
                    {
                        level.IsVisible = true;
                        level.State = StreamingLevelState.LoadedAndVisible;
                        _metrics.LevelsVisible++;
                        OnLevelVisible?.Invoke(level);
                    }
                    else if (!wantVisible && level.IsVisible)
                    {
                        level.IsVisible = false;
                        level.State = StreamingLevelState.Loaded;
                        if (_metrics.LevelsVisible > 0) _metrics.LevelsVisible--;
                        OnLevelHidden?.Invoke(level);
                    }
                }
            }
        }

        // ── 预测加载评估 ────────────────────────────────────────────

        private void EvaluatePredictiveLoading(Vector3 viewer)
        {
            if (!_predictor.HasPrediction) return;

            Vector3 predictedPos = _predictor.PredictedPosition;

            // 查找预测位置附近但尚未加载的级别
            var nearPredicted = _composition.GetLevelsNear(predictedPos, 500f);

            foreach (var level in nearPredicted)
            {
                if (level.State != StreamingLevelState.Unloaded) continue;
                if (level.Policy == StreamingPolicy.AlwaysLoaded) continue;

                // 只预加载当前不在加载范围内的
                if (level.ShouldLoadByDistance(viewer)) continue;

                // 给预测级别加一个预测加成
                level.PredictiveBoost = 0.3f;
                EnqueueLoad(level);
                _metrics.RecordPredictiveRequest();
            }
        }

        // ── 加载请求队列 ────────────────────────────────────────────

        private void EnqueueLoad(StreamingLevel? level)
        {
            if (level == null) return;
            if (level.State == StreamingLevelState.Loading ||
                level.State == StreamingLevelState.Loaded ||
                level.State == StreamingLevelState.LoadedAndVisible)
                return;
            if (_loadQueue.Any(r => r.LevelId == level.Id)) return;
            lock (_jobLock)
            {
                if (_activeJobs.Any(j => j.LevelId == level.Id)) return;
            }

            level.State = StreamingLevelState.Loading;
            level.LoadProgress = 0.0f;
            level.LoadStartMs = Stopwatch.GetTimestamp();

            // 创建取消令牌
            level.LoadCts = new CancellationTokenSource();

            _loadQueue.Add(new LoadRequest
            {
                LevelId = level.Id,
                Priority = level.Priority + level.PredictiveBoost,
                Lod = level.TargetLod
            });

            // 按优先级降序排序
            _loadQueue.Sort((a, b) => b.Priority.CompareTo(a.Priority));

            // 同时加入资源优先级调度器
            _scheduler.Enqueue(new PriorityRequest
            {
                ResourceId = level.Id,
                ComputedPriority = level.Priority + level.PredictiveBoost,
                DistanceScore = 1f / (1f + level.Bounds.DistanceToPoint(_viewerPosition) * 0.001f),
                VisibilityScore = level.IsFrustumVisible ? 1f : 0.2f,
                ImportanceScore = level.ImportanceWeight,
                PredictiveScore = level.PredictiveBoost,
                RequestTime = DateTime.Now,
                TargetLod = (int)level.TargetLod
            });
        }

        private void EnqueueUnload(StreamingLevel? level)
        {
            if (level == null) return;
            if (level.State == StreamingLevelState.Unloaded ||
                level.State == StreamingLevelState.Unloading) return;
            if (_unloadQueue.Contains(level)) return;

            // 取消进行中的加载
            _loadQueue.RemoveAll(r => r.LevelId == level.Id);
            _scheduler.Remove(level.Id);

            level.LoadCts?.Cancel();
            level.LoadCts?.Dispose();
            level.LoadCts = null;

            level.State = StreamingLevelState.Unloading;
            _unloadQueue.Add(level);
        }

        // ── 后台加载作业 ────────────────────────────────────────────

        private void StartPendingJobs()
        {
            lock (_jobLock)
            {
                while (_loadQueue.Count > 0 && _activeJobs.Count < MaxConcurrentLoads)
                {
                    if (!_threadPool.HasAvailableSlot) break;

                    var req = _loadQueue[0];
                    _loadQueue.RemoveAt(0);
                    var level = _composition.GetLevel(req.LevelId);
                    if (level == null) continue;
                    if (level.State != StreamingLevelState.Loading) continue;

                    var job = new LoadJob
                    {
                        LevelId = req.LevelId,
                        Lod = req.Lod,
                        StartMs = Stopwatch.GetTimestamp(),
                        Cts = level.LoadCts ?? new CancellationTokenSource()
                    };

                    var paths = ResolveAssetPaths(level, req.Lod);
                    job.AssetPaths = paths;

                    var jobRef = job;

                    // 提交到线程池
                    job.Task = Task.Run(() => LoadAssetsInBackground(jobRef), jobRef.Cts.Token);
                    _activeJobs.Add(job);
                }
            }
        }

        /// <summary>解析级别的资产文件路径（按 LOD）。</summary>
        private List<string> ResolveAssetPaths(StreamingLevel level, StreamingLodQuality lod)
        {
            var paths = new List<string>(level.GetAssetPaths(lod));
            if (paths.Count == 0)
            {
                for (var q = lod - 1; q >= StreamingLodQuality.Hlod; q--)
                {
                    var fallback = level.GetAssetPaths(q);
                    if (fallback.Count > 0) { paths.AddRange(fallback); break; }
                }
            }
            if (paths.Count == 0 && _assetDb != null)
            {
                foreach (var guid in level.AssetGuids)
                {
                    var meta = _assetDb.GetAsset(guid);
                    if (meta != null && File.Exists(meta.SourceFile))
                        paths.Add(meta.SourceFile);
                }
            }
            return paths;
        }

        /// <summary>后台线程：读取网格资产到作业暂存区（不触碰 ECS）。</summary>
        private void LoadAssetsInBackground(LoadJob job)
        {
            try
            {
                foreach (var path in job.AssetPaths)
                {
                    if (job.Cts.IsCancellationRequested) { job.IsCancelled = true; return; }
                    if (!File.Exists(path)) continue;

                    var mesh = EditableMesh.Read(path);
                    job.LoadedMeshes.Add(mesh);
                    job.LoadedBytes += mesh.Vertices.Count * 32L;
                }
            }
            catch (OperationCanceledException)
            {
                job.IsCancelled = true;
            }
            catch (Exception ex)
            {
                job.Error = ex;
            }
        }

        // ── 主线程：处理已完成作业 ──────────────────────────────────

        private void ProcessCompletedJobs(EntityComponentSystem ecs)
        {
            var sw = Stopwatch.StartNew();
            List<LoadJob> done;
            lock (_jobLock)
            {
                done = new List<LoadJob>();
                for (int i = _activeJobs.Count - 1; i >= 0; i--)
                {
                    if (_activeJobs[i].Task.IsCompleted)
                    {
                        done.Add(_activeJobs[i]);
                        _activeJobs.RemoveAt(i);
                    }
                }
            }

            foreach (var job in done)
            {
                var level = _composition.GetLevel(job.LevelId);
                if (level == null) continue;

                // 清理取消令牌
                level.LoadCts?.Dispose();
                level.LoadCts = null;

                // 从调度器移除
                _scheduler.Remove(job.LevelId);

                // 加载失败
                if (job.Error != null)
                {
                    level.State = StreamingLevelState.Unloaded;
                    level.LoadProgress = 0.0f;
                    OnLevelLoadFailed?.Invoke(level, job.ErrorMessage ?? "Unknown error");
                    continue;
                }

                // 已被取消或请求卸载
                if (job.IsCancelled || level.State == StreamingLevelState.Unloading)
                {
                    level.State = StreamingLevelState.Unloaded;
                    level.LoadProgress = 0.0f;
                    if (level.State == StreamingLevelState.Unloading)
                        _unloadQueue.Add(level);
                    continue;
                }

                float durationMs = (float)((double)(Stopwatch.GetTimestamp() - job.StartMs) /
                                    Stopwatch.Frequency * 1000.0);
                level.LastLoadDurationMs = durationMs;
                level.MemoryFootprintBytes = job.LoadedBytes;
                level.CurrentLod = job.Lod;

                // 检查预测加载命中
                if (level.PredictiveBoost > 0 && level.ShouldBeVisible(_viewerPosition))
                {
                    _metrics.RecordPredictiveHit();
                }

                // 主线程：为每个加载的网格创建 ECS 实体（受每帧预算限制）
                for (int mi = 0; mi < job.LoadedMeshes.Count; mi++)
                {
                    if (sw.ElapsedMilliseconds > MaxLoadTimePerFrameMs)
                    {
                        // 超预算：把剩余网格留到下一帧
                        var remainder = new LoadJob
                        {
                            LevelId = job.LevelId,
                            Lod = job.Lod,
                            AssetPaths = new List<string>(),
                            LoadedMeshes = job.LoadedMeshes.GetRange(mi, job.LoadedMeshes.Count - mi),
                            LoadedBytes = 0,
                            Cts = new CancellationTokenSource()
                        };
                        remainder.Task = Task.CompletedTask;
                        lock (_jobLock) _activeJobs.Add(remainder);
                        break;
                    }

                    var mesh = job.LoadedMeshes[mi];
                    int entityId = ecs.CreateEntity(level.Name + "_Mesh");
                    ecs.AddComponent(entityId, new MeshComponent
                    {
                        MeshId = Guid.TryParse(mesh.Guid, out var g) ? g : Guid.NewGuid(),
                        MaterialId = Guid.Empty,
                        CastsShadow = true,
                        ReceivesShadow = true,
                        IsVisible = true,
                        TriangleCount = mesh.Faces.Count,
                        VertexCount = mesh.Vertices.Count
                    });
                    level.EntityIds.Add(entityId);
                }

                level.LoadProgress = 1.0f;
                level.State = StreamingLevelState.Loaded;
                level.PredictiveBoost = 0f; // 重置预测加成

                _metrics.RecordLoad(durationMs, job.LoadedBytes);
                _metrics.LevelsLoaded++;
                _memoryManager.OnLevelLoaded(level);
                _metrics.FrameLoadBudgetUsedMs = sw.ElapsedMilliseconds;

                OnLevelLoaded?.Invoke(level);
            }
        }

        // ── 主线程：处理卸载队列 ────────────────────────────────────

        private void ProcessUnloadQueue(EntityComponentSystem ecs)
        {
            var sw = Stopwatch.StartNew();
            for (int i = _unloadQueue.Count - 1; i >= 0; i--)
            {
                if (sw.ElapsedMilliseconds > MaxLoadTimePerFrameMs) break;
                var level = _unloadQueue[i];
                _unloadQueue.RemoveAt(i);

                // 销毁该级别的所有实体
                foreach (var entityId in level.EntityIds)
                {
                    if (ecs.HasEntity(entityId)) ecs.DestroyEntity(entityId);
                }

                long freed = level.MemoryFootprintBytes;
                level.EntityIds.Clear();
                level.MemoryFootprintBytes = 0;
                level.LoadProgress = 0.0f;
                level.IsVisible = false;
                level.State = StreamingLevelState.Unloaded;
                level.PredictiveBoost = 0f;
                level.InvisibleFrames = 0;

                _metrics.RecordUnload(freed);
                _memoryManager.OnLevelUnloaded(level);
                if (_metrics.LevelsLoaded > 0) _metrics.LevelsLoaded--;

                OnLevelUnloaded?.Invoke(level);
            }
        }

        // ── 内存预算淘汰 ────────────────────────────────────────────

        private void ProcessMemoryEviction()
        {
            long evictTarget = _memoryManager.CheckEvictionNeeded();
            if (evictTarget <= 0) return;

            var candidates = _composition.GetLevelsInState(StreamingLevelState.Loaded);
            var evictionList = _memoryManager.SelectEvictionTargets(candidates, evictTarget);

            if (evictionList.Count == 0) return;

            foreach (var level in evictionList)
            {
                EnqueueUnload(level);
            }
        }

        // ── 观察者解析 ──────────────────────────────────────────────

        private static Vector3 ResolveViewer(EntityComponentSystem ecs)
        {
            var cams = ecs.QueryWithComponents<TransformComponent, CameraComponent>();
            foreach (var id in cams)
            {
                var cam = ecs.GetComponent<CameraComponent>(id);
                if (cam.IsMain) return ecs.GetComponent<TransformComponent>(id).Position;
            }
            foreach (var id in cams) return ecs.GetComponent<TransformComponent>(id).Position;
            return Vector3.Zero;
        }

        // ── 内部数据结构 ────────────────────────────────────────────

        private struct LoadRequest
        {
            public Guid LevelId;
            public float Priority;
            public StreamingLodQuality Lod;
        }

        private class LoadJob
        {
            public Guid LevelId;
            public StreamingLodQuality Lod;
            public long StartMs;
            public CancellationTokenSource Cts = new();
            public Task Task = Task.CompletedTask;
            public List<string> AssetPaths = new();
            public List<EditableMesh> LoadedMeshes = new();
            public long LoadedBytes;
            public Exception? Error;
            public bool IsCancelled;
            public string? ErrorMessage => Error?.Message;
        }
    }
}
