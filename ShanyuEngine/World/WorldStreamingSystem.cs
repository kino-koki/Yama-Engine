using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using ShanyuEngine.ECS;
using ShanyuEngine.RHI;
using ShanyuEngine.Rendering;

namespace ShanyuEngine.World
{
    // ══════════════════════════════════════════════════════════════════
    // 流式加载层级枚举 —— 定义运行时数据分层
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 流式数据层级。参考 UE5 World Partition 的数据分层设计，
    /// 根据距离视点的远近使用不同精度的数据，以平衡视觉质量与性能。
    /// </summary>
    public enum StreamingLayer
    {
        /// <summary>HLOD 层级 —— 超远距离使用合并后的简化网格</summary>
        HLOD = 0,
        /// <summary>远景层级 —— 远距离低精度模型</summary>
        Far = 1,
        /// <summary>中距层级 —— 中等距离常规精度模型</summary>
        Mid = 2,
        /// <summary>近距层级 —— 近距离高精度模型</summary>
        Near = 3,
        /// <summary>近景层级 —— 极近距离最高精度模型（如 MicroCluster 最高 LOD）</summary>
        Closeup = 4
    }

    /// <summary>
    /// 流式单元状态机。每个流式块在整个生命周期中处于以下状态之一：
    /// Unloaded → Loading → Loaded → Unloading → Unloaded
    /// </summary>
    public enum StreamingChunkState
    {
        /// <summary>已卸载 —— 数据不在内存中</summary>
        Unloaded,
        /// <summary>加载中 —— 后台线程正在从磁盘读取数据</summary>
        Loading,
        /// <summary>GPU 上传中 —— 数据已读入内存，正在上传至 GPU</summary>
        Uploading,
        /// <summary>已加载 —— 数据已在内存和 GPU 中，可正常使用</summary>
        Loaded,
        /// <summary>卸载中 —— 正在释放资源</summary>
        Unloading
    }

    // ══════════════════════════════════════════════════════════════════
    // 流式块（Streaming Chunk）—— 世界分区的基本数据单元
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 流式块。代表世界分区网格中一个单元格内、特定层级的全部数据。
    /// 一个 Grid 单元格可以包含多个层级的 Chunk（HLOD/Far/Mid/Near）。
    /// </summary>
    public class StreamingChunk
    {
        /// <summary>全局唯一标识符</summary>
        public Guid Id = Guid.NewGuid();

        /// <summary>所属网格坐标 X</summary>
        public int GridX;
        /// <summary>所属网格坐标 Y</summary>
        public int GridY;
        /// <summary>所属网格坐标 Z</summary>
        public int GridZ;

        /// <summary>数据层级</summary>
        public StreamingLayer Layer;

        /// <summary>当前状态</summary>
        public StreamingChunkState State = StreamingChunkState.Unloaded;

        /// <summary>加载进度 [0, 1]</summary>
        public float LoadProgress;

        /// <summary>该块在世界空间的包围盒</summary>
        public BoundingBox Bounds;

        /// <summary>该块中心位置</summary>
        public Vector3 Center => (Bounds.Min + Bounds.Max) * 0.5f;

        /// <summary>该块包含的资产 GUID 列表</summary>
        public readonly List<Guid> AssetIds = new();

        /// <summary>该块包含的实体 ID 列表（与 ECS 集成）</summary>
        public readonly List<int> EntityIds = new();

        /// <summary>最近访问时间（用于 LRU 淘汰）</summary>
        public DateTime LastAccessTime = DateTime.Now;

        /// <summary>优先级得分（距离越近、可见性越高则分值越高）</summary>
        public float PriorityScore;

        /// <summary>该块占用的内存大小（KB）</summary>
        public long MemoryKB;

        /// <summary>是否为持久块（不会被自动卸载）</summary>
        public bool IsPersistent;

        /// <summary>是否为始终加载块（编辑器中使用）</summary>
        public bool IsAlwaysLoaded;

        /// <summary>引用计数</summary>
        public int ReferenceCount;

        /// <summary>加载取消令牌</summary>
        public CancellationTokenSource? LoadCancellationToken;

        /// <summary>已上传到 GPU 的顶点缓冲（如果适用）</summary>
        public RHIBuffer? VertexBuffer;
        /// <summary>已上传到 GPU 的索引缓冲（如果适用）</summary>
        public RHIBuffer? IndexBuffer;

        /// <summary>该层级的切换距离阈值</summary>
        public float SwitchDistance;

        /// <summary>
        /// 计算到指定点的距离
        /// </summary>
        public float DistanceTo(Vector3 point) => Bounds.DistanceTo(point);
    }

    // ══════════════════════════════════════════════════════════════════
    // 流式体积（Streaming Volume）—— 基于触发器的流式加载
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 流式体积触发器。当玩家/摄像机进入或离开此体积时，
    /// 自动触发相关流式块的加载或卸载。
    /// 常用于关卡设计中的"预加载区域"，如走廊、隧道入口等。
    /// </summary>
    public class StreamingVolume
    {
        /// <summary>唯一标识符</summary>
        public Guid Id = Guid.NewGuid();

        /// <summary>体积名称</summary>
        public string Name = "";

        /// <summary>触发体积的包围盒</summary>
        public BoundingBox Bounds;

        /// <summary>进入此体积时应加载的块列表</summary>
        public readonly List<Guid> LoadOnEnter = new();

        /// <summary>离开此体积时应卸载的块列表</summary>
        public readonly List<Guid> UnloadOnExit = new();

        /// <summary>进入此体积时应卸载的块列表（用于卸载后方区域）</summary>
        public readonly List<Guid> UnloadOnEnter = new();

        /// <summary>离开此体积时应加载的块列表</summary>
        public readonly List<Guid> LoadOnExit = new();

        /// <summary>是否启用</summary>
        public bool IsEnabled = true;

        /// <summary>当前是否有视点在此体积内</summary>
        public bool ContainsViewpoint;

        /// <summary>进入时的回调</summary>
        public Action<StreamingVolume>? OnEnter;

        /// <summary>离开时的回调</summary>
        public Action<StreamingVolume>? OnExit;
    }

    // ══════════════════════════════════════════════════════════════════
    // 加载请求 —— 优先级队列的基本元素
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 流式加载请求。每个请求包含目标块、优先级和请求类型。
    /// </summary>
    public struct StreamingRequest : IComparable<StreamingRequest>
    {
        /// <summary>目标块 ID</summary>
        public Guid ChunkId;

        /// <summary>优先级得分（越高越优先）</summary>
        public float Priority;

        /// <summary>请求类型：加载或卸载</summary>
        public bool IsUnload;

        /// <summary>请求创建时间</summary>
        public DateTime RequestTime;

        public int CompareTo(StreamingRequest other)
        {
            // 优先级高的排前面；同优先级则先来先服务
            int cmp = other.Priority.CompareTo(Priority);
            if (cmp != 0) return cmp;
            return RequestTime.CompareTo(other.RequestTime);
        }
    }

    // ══════════════════════════════════════════════════════════════════
    // 流式加载配置
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 流式加载系统配置参数。控制网格大小、加载半径、并发数、内存预算等。
    /// </summary>
    public class StreamingConfig
    {
        /// <summary>网格单元大小（世界单位）</summary>
        public float CellSize = 512f;

        /// <summary>各层级的加载半径（以网格单元为单位）</summary>
        public readonly Dictionary<StreamingLayer, int> LayerLoadRadius = new()
        {
            [StreamingLayer.HLOD] = 8,
            [StreamingLayer.Far] = 6,
            [StreamingLayer.Mid] = 4,
            [StreamingLayer.Near] = 2,
            [StreamingLayer.Closeup] = 1
        };

        /// <summary>各层级的卸载半径（超出此距离后卸载）</summary>
        public readonly Dictionary<StreamingLayer, int> LayerUnloadRadius = new()
        {
            [StreamingLayer.HLOD] = 10,
            [StreamingLayer.Far] = 8,
            [StreamingLayer.Mid] = 6,
            [StreamingLayer.Near] = 4,
            [StreamingLayer.Closeup] = 2
        };

        /// <summary>最大并发加载数</summary>
        public int MaxConcurrentLoads = 4;

        /// <summary>最大并发 GPU 上传数</summary>
        public int MaxConcurrentUploads = 2;

        /// <summary>内存预算上限（KB），超出后启动 LRU 淘汰</summary>
        public long MemoryBudgetKB = 2 * 1024 * 1024; // 2 GB

        /// <summary>LRU 淘汰时释放的内存比例</summary>
        public float EvictionRatio = 0.15f;

        /// <summary>更新间隔（秒），避免每帧扫描</summary>
        public float UpdateInterval = 0.1f;

        /// <summary>是否启用视锥体剔除（不可见的块降低优先级）</summary>
        public bool EnableFrustumCulling = true;

        /// <summary>是否启用流式体积触发器</summary>
        public bool EnableStreamingVolumes = true;

        /// <summary>后台加载线程数</summary>
        public int WorkerThreadCount = 2;
    }

    // ══════════════════════════════════════════════════════════════════
    // 内存预算管理器 —— LRU 淘汰策略
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 内存预算管理器。跟踪所有已加载块的内存占用，
    /// 当超出预算时按照 LRU（最近最少使用）策略淘汰最不活跃的块。
    /// </summary>
    public class MemoryBudgetManager
    {
        private long _currentUsageKB;
        private long _budgetKB;
        private readonly float _evictionRatio;

        /// <summary>当前内存使用量（KB）</summary>
        public long CurrentUsageKB => _currentUsageKB;
        /// <summary>内存预算上限（KB）</summary>
        public long BudgetKB => _budgetKB;
        /// <summary>内存使用率 [0, 1]</summary>
        public float UsageRatio => _budgetKB > 0 ? (float)_currentUsageKB / _budgetKB : 0f;

        /// <summary>内存超出预算事件</summary>
        public event Action<float>? OnMemoryPressure;

        /// <param name="budgetKB">预算上限（KB）</param>
        /// <param name="evictionRatio">淘汰时释放的比例</param>
        public MemoryBudgetManager(long budgetKB, float evictionRatio)
        {
            _budgetKB = budgetKB;
            _evictionRatio = evictionRatio;
        }

        /// <summary>记录块加载后的内存增加</summary>
        public void OnChunkLoaded(StreamingChunk chunk)
        {
            _currentUsageKB += chunk.MemoryKB;
            if (UsageRatio > 0.85f)
                OnMemoryPressure?.Invoke(UsageRatio);
        }

        /// <summary>记录块卸载后的内存减少</summary>
        public void OnChunkUnloaded(StreamingChunk chunk)
        {
            _currentUsageKB = Math.Max(0, _currentUsageKB - chunk.MemoryKB);
        }

        /// <summary>设置新的预算上限</summary>
        public void SetBudget(long budgetKB) => _budgetKB = budgetKB;

        /// <summary>
        /// 检查是否超出预算，并返回需要淘汰的内存量（KB）。
        /// </summary>
        public long CheckEvictionNeeded()
        {
            if (_currentUsageKB <= _budgetKB) return 0;
            long targetEvict = (long)((_currentUsageKB - _budgetKB * 0.8f) + _budgetKB * _evictionRatio);
            return targetEvict;
        }

        /// <summary>
        /// 从候选块列表中选出应该被淘汰的块（按 LastAccessTime 排序，最旧的优先）。
        /// </summary>
        /// <param name="candidates">可淘汰的候选块列表</param>
        /// <param name="targetEvictKB">目标淘汰量（KB）</param>
        /// <returns>被选中的淘汰块列表</returns>
        public List<StreamingChunk> SelectEvictionTargets(List<StreamingChunk> candidates, long targetEvictKB)
        {
            // 按 LastAccessTime 升序排序（最久未使用的排最前）
            var sorted = candidates
                .Where(c => !c.IsPersistent && !c.IsAlwaysLoaded && c.ReferenceCount == 0)
                .OrderBy(c => c.LastAccessTime)
                .ToList();

            var result = new List<StreamingChunk>();
            long evicted = 0;
            foreach (var chunk in sorted)
            {
                if (evicted >= targetEvictKB) break;
                result.Add(chunk);
                evicted += chunk.MemoryKB;
            }
            return result;
        }
    }

    // ══════════════════════════════════════════════════════════════════
    // ECS 集成组件 —— 流式相关组件
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 流式代理组件。附加到实体上，标识该实体属于哪个流式块。
    /// 当块被卸载时，实体也会被隐藏或销毁。
    /// </summary>
    public struct StreamingProxyComponent : IComponent
    {
        /// <summary>所属流式块 ID</summary>
        public Guid ChunkId;
        /// <summary>块被卸载时是否销毁实体（否则仅隐藏）</summary>
        public bool DestroyOnUnload;
        /// <summary>实体当前是否因为块卸载而处于隐藏状态</summary>
        public bool IsHiddenByStreaming;
    }

    // ══════════════════════════════════════════════════════════════════
    // 世界流式加载系统 —— 主系统类
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 世界流式加载系统。
    /// 
    /// 核心设计理念（参考 UE5 World Partition）：
    /// 1. 将世界划分为均匀的 Grid 网格，每个单元格包含多层（HLOD/Near/Mid/Far）数据
    /// 2. 根据视点位置和距离，动态决定各层级的加载/卸载
    /// 3. 后台线程异步读取磁盘数据，主线程负责 GPU 上传
    /// 4. 基于距离和可见性的优先级队列，确保最重要的数据先加载
    /// 5. 内存预算管理 + LRU 淘汰，防止内存溢出
    /// 6. 流式体积触发器，支持关卡设计师的预加载区域
    /// 7. 与 ECS 系统集成，自动管理实体的可见性
    /// </summary>
    public class WorldStreamingSystem : ISystem
    {
        // ── 名称 ──
        public string Name => "WorldStreamingSystem";

        // ── 内部数据结构 ──

        /// <summary>所有已注册的流式块字典（按 ID 索引）</summary>
        private readonly Dictionary<Guid, StreamingChunk> _chunks = new();

        /// <summary>网格坐标到块列表的映射（一个网格坐标可能有多个层级的块）</summary>
        private readonly Dictionary<(int x, int y, int z), List<StreamingChunk>> _gridMap = new();

        /// <summary>当前已加载的块列表</summary>
        private readonly List<StreamingChunk> _loadedChunks = new();

        /// <summary>正在加载的块列表</summary>
        private readonly List<StreamingChunk> _loadingChunks = new();

        /// <summary>流式体积触发器列表</summary>
        private readonly List<StreamingVolume> _volumes = new();

        /// <summary>加载请求优先级队列</summary>
        private readonly ShanyuEngine.RHI.PriorityQueue<StreamingRequest, float> _loadQueue = new();

        /// <summary>卸载请求队列</summary>
        private readonly Queue<StreamingRequest> _unloadQueue = new();

        /// <summary>内存预算管理器</summary>
        private readonly MemoryBudgetManager _memoryManager;

        /// <summary>系统配置</summary>
        private readonly StreamingConfig _config;

        /// <summary>当前视点位置</summary>
        private Vector3 _viewpoint = Vector3.Zero;

        /// <summary>视锥体矩阵（用于可见性剔除）</summary>
        private Matrix4x4 _viewProjection = Matrix4x4.Identity;

        /// <summary>更新计时器</summary>
        private float _updateTimer;

        /// <summary>RHI 设备引用（用于 GPU 资源上传）</summary>
        private RHIDevice? _rhiDevice;

        /// <summary>ECS 引用</summary>
        private EntityComponentSystem? _ecs;

        /// <summary>后台加载任务的取消令牌</summary>
        private CancellationTokenSource _shutdownToken = new();

        // ── 统计信息 ──

        /// <summary>总块数</summary>
        public int TotalChunkCount => _chunks.Count;
        /// <summary>已加载块数</summary>
        public int LoadedChunkCount => _loadedChunks.Count;
        /// <summary>正在加载块数</summary>
        public int LoadingChunkCount => _loadingChunks.Count;
        /// <summary>流式体积数量</summary>
        public int VolumeCount => _volumes.Count;
        /// <summary>待处理加载请求数</summary>
        public int PendingLoadRequests => _loadQueue.Count;
        /// <summary>待处理卸载请求数</summary>
        public int PendingUnloadRequests => _unloadQueue.Count;
        /// <summary>当前内存使用（KB）</summary>
        public long MemoryUsageKB => _memoryManager.CurrentUsageKB;
        /// <summary>内存使用率</summary>
        public float MemoryUsageRatio => _memoryManager.UsageRatio;

        // ── 事件 ──

        /// <summary>块加载完成事件</summary>
        public event Action<StreamingChunk>? OnChunkLoaded;
        /// <summary>块卸载完成事件</summary>
        public event Action<StreamingChunk>? OnChunkUnloaded;
        /// <summary>内存压力事件</summary>
        public event Action<float>? OnMemoryPressure;
        /// <summary>日志事件</summary>
        public event Action<string>? OnLogMessage;

        // ══════════════════════════════════════════════════════════════
        // 构造函数
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// 创建世界流式加载系统。
        /// </summary>
        /// <param name="config">系统配置，为 null 时使用默认配置</param>
        public WorldStreamingSystem(StreamingConfig? config = null)
        {
            _config = config ?? new StreamingConfig();
            _memoryManager = new MemoryBudgetManager(_config.MemoryBudgetKB, _config.EvictionRatio);
            _memoryManager.OnMemoryPressure += ratio => OnMemoryPressure?.Invoke(ratio);
            Log("世界流式加载系统已初始化");
        }

        // ══════════════════════════════════════════════════════════════
        // 系统绑定 —— 与 RHI 和 ECS 对接
        // ══════════════════════════════════════════════════════════════

        /// <summary>绑定 RHI 设备，用于 GPU 资源上传</summary>
        public void BindRHI(RHIDevice device)
        {
            _rhiDevice = device;
            Log($"已绑定 RHI 设备: {device.AdapterName}");
        }

        /// <summary>绑定 ECS 系统</summary>
        public void BindECS(EntityComponentSystem ecs)
        {
            _ecs = ecs;
            Log("已绑定 ECS 系统");
        }

        // ══════════════════════════════════════════════════════════════
        // 块管理 —— 注册与查询
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// 注册一个流式块。通常在关卡构建或编辑器烘焙时调用。
        /// </summary>
        public StreamingChunk RegisterChunk(int gridX, int gridY, int gridZ, StreamingLayer layer, BoundingBox bounds)
        {
            var chunk = new StreamingChunk
            {
                GridX = gridX,
                GridY = gridY,
                GridZ = gridZ,
                Layer = layer,
                Bounds = bounds,
                State = StreamingChunkState.Unloaded,
                SwitchDistance = GetLayerSwitchDistance(layer)
            };
            _chunks[chunk.Id] = chunk;

            var key = (gridX, gridY, gridZ);
            if (!_gridMap.TryGetValue(key, out var list))
            {
                list = new List<StreamingChunk>();
                _gridMap[key] = list;
            }
            list.Add(chunk);

            return chunk;
        }

        /// <summary>
        /// 获取指定网格坐标和层级的块。
        /// </summary>
        public StreamingChunk? GetChunk(int gridX, int gridY, int gridZ, StreamingLayer layer)
        {
            if (!_gridMap.TryGetValue((gridX, gridY, gridZ), out var list)) return null;
            foreach (var c in list)
                if (c.Layer == layer) return c;
            return null;
        }

        /// <summary>
        /// 获取或创建指定网格坐标的块（所有层级都会获取或创建）。
        /// </summary>
        public List<StreamingChunk> GetChunksAt(int gridX, int gridY, int gridZ)
        {
            return _gridMap.TryGetValue((gridX, gridY, gridZ), out var list)
                ? list
                : new List<StreamingChunk>();
        }

        /// <summary>
        /// 根据世界坐标获取网格坐标。
        /// </summary>
        public (int x, int y, int z) WorldToGrid(Vector3 worldPos)
        {
            float cs = _config.CellSize;
            return (
                (int)MathF.Floor(worldPos.X / cs),
                (int)MathF.Floor(worldPos.Y / cs),
                (int)MathF.Floor(worldPos.Z / cs)
            );
        }

        /// <summary>
        /// 根据网格坐标计算世界空间中心点。
        /// </summary>
        public Vector3 GridToWorldCenter(int gridX, int gridY, int gridZ)
        {
            float cs = _config.CellSize;
            return new Vector3(
                gridX * cs + cs * 0.5f,
                gridY * cs + cs * 0.5f,
                gridZ * cs + cs * 0.5f
            );
        }

        // ══════════════════════════════════════════════════════════════
        // 流式体积管理
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// 注册一个流式体积触发器。
        /// </summary>
        public StreamingVolume RegisterVolume(string name, BoundingBox bounds)
        {
            var vol = new StreamingVolume
            {
                Name = name,
                Bounds = bounds
            };
            _volumes.Add(vol);
            Log($"注册流式体积: {name}");
            return vol;
        }

        /// <summary>
        /// 移除流式体积触发器。
        /// </summary>
        public bool RemoveVolume(Guid volumeId)
        {
            for (int i = 0; i < _volumes.Count; i++)
            {
                if (_volumes[i].Id == volumeId)
                {
                    _volumes.RemoveAt(i);
                    return true;
                }
            }
            return false;
        }

        // ══════════════════════════════════════════════════════════════
        // 视点与可见性
        // ══════════════════════════════════════════════════════════════

        /// <summary>设置当前视点位置</summary>
        public void SetViewpoint(Vector3 position) => _viewpoint = position;

        /// <summary>设置视锥体矩阵（用于可见性剔除）</summary>
        public void SetViewProjection(Matrix4x4 viewProj) => _viewProjection = viewProj;

        /// <summary>
        /// 检查块是否在视锥体内可见。
        /// 使用简化的 AABB-视锥体相交测试。
        /// </summary>
        public bool IsChunkVisible(StreamingChunk chunk)
        {
            if (!_config.EnableFrustumCulling) return true;

            // 将包围盒的 8 个角点变换到裁剪空间
            var b = chunk.Bounds;
            var corners = new Vector3[8]
            {
                new(b.Min.X, b.Min.Y, b.Min.Z),
                new(b.Max.X, b.Min.Y, b.Min.Z),
                new(b.Min.X, b.Max.Y, b.Min.Z),
                new(b.Max.X, b.Max.Y, b.Min.Z),
                new(b.Min.X, b.Min.Y, b.Max.Z),
                new(b.Max.X, b.Min.Y, b.Max.Z),
                new(b.Min.X, b.Max.Y, b.Max.Z),
                new(b.Max.X, b.Max.Y, b.Max.Z)
            };

            bool allLeft = true, allRight = true, allFront = true, allBack = true, allTop = true, allBottom = true;

            foreach (var corner in corners)
            {
                var clip = Vector4.Transform(new Vector4(corner, 1f), _viewProjection);
                if (clip.W <= 0) return true; // 在摄像机后面或太近，保守判定为可见

                float cx = clip.X / clip.W;
                float cy = clip.Y / clip.W;
                float cz = clip.Z / clip.W;

                if (cx > -1) allLeft = false;
                if (cx < 1) allRight = false;
                if (cy > -1) allBottom = false;
                if (cy < 1) allTop = false;
                if (cz > 0) allFront = false;
                if (cz < 1) allBack = false;
            }

            // 如果所有角点都在某一面之外，则不可见
            return !(allLeft || allRight || allTop || allBottom || allFront || allBack);
        }

        // ══════════════════════════════════════════════════════════════
        // 层级距离计算
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// 获取指定层级的切换距离阈值。
        /// 距离视点小于此阈值时使用该层级。
        /// </summary>
        private float GetLayerSwitchDistance(StreamingLayer layer)
        {
            return layer switch
            {
                StreamingLayer.HLOD => _config.CellSize * 6f,
                StreamingLayer.Far => _config.CellSize * 4f,
                StreamingLayer.Mid => _config.CellSize * 2f,
                StreamingLayer.Near => _config.CellSize * 1f,
                StreamingLayer.Closeup => _config.CellSize * 0.5f,
                _ => _config.CellSize * 2f
            };
        }

        /// <summary>
        /// 根据距离决定应该加载哪个层级。
        /// </summary>
        private StreamingLayer GetDesiredLayer(float distance)
        {
            if (distance < _config.CellSize * 0.5f) return StreamingLayer.Closeup;
            if (distance < _config.CellSize * 1f) return StreamingLayer.Near;
            if (distance < _config.CellSize * 2f) return StreamingLayer.Mid;
            if (distance < _config.CellSize * 4f) return StreamingLayer.Far;
            return StreamingLayer.HLOD;
        }

        // ══════════════════════════════════════════════════════════════
        // 优先级计算
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// 计算块的优先级得分。
        /// 距离越近、可见性越高、层级越精细，优先级越高。
        /// </summary>
        private float CalculatePriority(StreamingChunk chunk)
        {
            float dist = chunk.DistanceTo(_viewpoint);
            float distScore = 1f / (1f + dist * 0.001f);

            float visScore = IsChunkVisible(chunk) ? 1f : 0.2f;

            float layerScore = chunk.Layer switch
            {
                StreamingLayer.Closeup => 1.0f,
                StreamingLayer.Near => 0.8f,
                StreamingLayer.Mid => 0.6f,
                StreamingLayer.Far => 0.4f,
                StreamingLayer.HLOD => 0.2f,
                _ => 0.5f
            };

            return distScore * visScore * layerScore;
        }

        // ══════════════════════════════════════════════════════════════
        // 主更新逻辑
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// ECS 系统更新接口。每帧由 ECS 调用。
        /// </summary>
        public void Update(float dt, EntityComponentSystem ecs)
        {
            Update(dt);
        }

        /// <summary>
        /// 主更新方法。执行流式加载的全流程：
        /// 1. 检查流式体积触发器
        /// 2. 扫描视点周围需要加载的块
        /// 3. 处理加载请求队列
        /// 4. 处理卸载请求队列
        /// 5. 执行内存淘汰
        /// 6. 更新 ECS 实体可见性
        /// </summary>
        public void Update(float dt)
        {
            _updateTimer += dt;
            if (_updateTimer < _config.UpdateInterval) return;
            _updateTimer = 0f;

            // 步骤 1：检查流式体积触发器
            if (_config.EnableStreamingVolumes)
                UpdateStreamingVolumes();

            // 步骤 2：扫描需要加载/卸载的块
            ScanStreamingNeeds();

            // 步骤 3：处理加载队列（限制并发数）
            ProcessLoadQueue(dt);

            // 步骤 4：处理卸载队列
            ProcessUnloadQueue();

            // 步骤 5：内存预算检查与 LRU 淘汰
            ProcessMemoryEviction();

            // 步骤 6：更新已加载块的访问时间和优先级
            UpdateLoadedChunks();

            // 步骤 7：同步 ECS 实体可见性
            if (_ecs != null)
                SyncEntityVisibility();
        }

        // ══════════════════════════════════════════════════════════════
        // 流式体积触发器更新
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// 检查所有流式体积触发器，当视点进入或离开体积时触发加载/卸载。
        /// </summary>
        private void UpdateStreamingVolumes()
        {
            foreach (var vol in _volumes)
            {
                if (!vol.IsEnabled) continue;

                bool contains = vol.Bounds.Contains(_viewpoint);

                if (contains && !vol.ContainsViewpoint)
                {
                    // 视点刚进入体积
                    vol.ContainsViewpoint = true;
                    Log($"视点进入流式体积: {vol.Name}");
                    foreach (var chunkId in vol.LoadOnEnter)
                        EnqueueLoadRequest(chunkId, 10f); // 体积触发的加载给予高优先级
                    foreach (var chunkId in vol.UnloadOnEnter)
                        EnqueueUnloadRequest(chunkId);
                    vol.OnEnter?.Invoke(vol);
                }
                else if (!contains && vol.ContainsViewpoint)
                {
                    // 视点刚离开体积
                    vol.ContainsViewpoint = false;
                    Log($"视点离开流式体积: {vol.Name}");
                    foreach (var chunkId in vol.LoadOnExit)
                        EnqueueLoadRequest(chunkId, 5f);
                    foreach (var chunkId in vol.UnloadOnExit)
                        EnqueueUnloadRequest(chunkId);
                    vol.OnExit?.Invoke(vol);
                }
            }
        }

        // ══════════════════════════════════════════════════════════════
        // 流式需求扫描
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// 扫描视点周围，确定哪些块需要加载、哪些需要卸载。
        /// 根据各层级的加载/卸载半径决定。
        /// </summary>
        private void ScanStreamingNeeds()
        {
            var (vx, vy, vz) = WorldToGrid(_viewpoint);

            // 收集需要加载的块
            var loadCandidates = new List<StreamingChunk>();
            var maxRadius = _config.LayerLoadRadius.Values.Max();

            for (int x = vx - maxRadius; x <= vx + maxRadius; x++)
            {
                for (int y = vy - maxRadius; y <= vy + maxRadius; y++)
                {
                    for (int z = vz - maxRadius; z <= vz + maxRadius; z++)
                    {
                        int dist = Math.Max(Math.Max(Math.Abs(x - vx), Math.Abs(y - vy)), Math.Abs(z - vz));
                        if (dist > maxRadius) continue;

                        if (!_gridMap.TryGetValue((x, y, z), out var chunks)) continue;

                        // 根据距离决定需要哪个层级
                        float worldDist = Vector3.Distance(GridToWorldCenter(x, y, z), _viewpoint);
                        var desiredLayer = GetDesiredLayer(worldDist);

                        foreach (var chunk in chunks)
                        {
                            // 只加载需要的层级（及更低精度的层级作为后备）
                            if (chunk.Layer <= desiredLayer)
                            {
                                if (chunk.State == StreamingChunkState.Unloaded)
                                    loadCandidates.Add(chunk);
                            }
                            else
                            {
                                // 不需要的更精细层级 —— 排队卸载
                                if (chunk.State == StreamingChunkState.Loaded)
                                    EnqueueUnloadRequest(chunk.Id);
                            }
                        }
                    }
                }
            }

            // 计算优先级并加入加载队列
            foreach (var chunk in loadCandidates)
            {
                float priority = CalculatePriority(chunk);
                EnqueueLoadRequest(chunk.Id, priority);
            }

            // 检查超出卸载半径的已加载块
            var toUnload = new List<StreamingChunk>();
            foreach (var chunk in _loadedChunks)
            {
                if (chunk.IsPersistent || chunk.IsAlwaysLoaded) continue;

                int dx = Math.Abs(chunk.GridX - vx);
                int dy = Math.Abs(chunk.GridY - vy);
                int dz = Math.Abs(chunk.GridZ - vz);
                int gridDist = Math.Max(Math.Max(dx, dy), dz);

                if (!_config.LayerUnloadRadius.TryGetValue(chunk.Layer, out int unloadRadius))
                    unloadRadius = 6;

                if (gridDist > unloadRadius)
                    toUnload.Add(chunk);
            }

            foreach (var chunk in toUnload)
                EnqueueUnloadRequest(chunk.Id);
        }

        // ══════════════════════════════════════════════════════════════
        // 加载队列处理
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// 将加载请求加入优先级队列。
        /// </summary>
        private void EnqueueLoadRequest(Guid chunkId, float priority)
        {
            var request = new StreamingRequest
            {
                ChunkId = chunkId,
                Priority = priority,
                IsUnload = false,
                RequestTime = DateTime.Now
            };
            _loadQueue.Enqueue(request, -priority); // PriorityQueue 是最小堆，取负值使高优先级先出
        }

        /// <summary>
        /// 将卸载请求加入队列。
        /// </summary>
        private void EnqueueUnloadRequest(Guid chunkId)
        {
            _unloadQueue.Enqueue(new StreamingRequest
            {
                ChunkId = chunkId,
                Priority = 0f,
                IsUnload = true,
                RequestTime = DateTime.Now
            });
        }

        /// <summary>
        /// 处理加载队列。每次最多处理 MaxConcurrentLoads 个请求。
        /// 加载过程分为两阶段：
        /// 1. 后台线程异步读取磁盘数据（Loading 状态）
        /// 2. 主线程上传数据到 GPU（Uploading 状态）
        /// </summary>
        private void ProcessLoadQueue(float dt)
        {
            int activeLoads = _loadingChunks.Count(c =>
                c.State == StreamingChunkState.Loading || c.State == StreamingChunkState.Uploading);

            int availableSlots = _config.MaxConcurrentLoads - activeLoads;
            if (availableSlots <= 0) return;

            // 从优先级队列中取出请求
            int processed = 0;
            while (_loadQueue.Count > 0 && processed < availableSlots)
            {
                if (!_loadQueue.TryDequeue(out var request, out _)) break;

                if (!_chunks.TryGetValue(request.ChunkId, out var chunk)) continue;
                if (chunk.State != StreamingChunkState.Unloaded) continue;

                // 启动异步加载
                StartAsyncLoad(chunk);
                processed++;
            }

            // 更新正在加载的块的进度
            for (int i = _loadingChunks.Count - 1; i >= 0; i--)
            {
                var chunk = _loadingChunks[i];

                if (chunk.State == StreamingChunkState.Loading)
                {
                    // 等待后台加载完成（进度由后台任务更新）
                    chunk.LoadProgress = Math.Clamp(chunk.LoadProgress + dt * 2f, 0f, 0.9f);
                }
                else if (chunk.State == StreamingChunkState.Uploading)
                {
                    // GPU 上传阶段
                    chunk.LoadProgress = Math.Clamp(chunk.LoadProgress + dt * 5f, 0f, 1f);
                    if (chunk.LoadProgress >= 1f)
                    {
                        CompleteChunkLoad(chunk);
                        _loadingChunks.RemoveAt(i);
                    }
                }
            }
        }

        /// <summary>
        /// 启动异步加载任务（后台线程读取磁盘数据）。
        /// </summary>
        private void StartAsyncLoad(StreamingChunk chunk)
        {
            chunk.State = StreamingChunkState.Loading;
            chunk.LoadProgress = 0f;
            chunk.LoadCancellationToken = new CancellationTokenSource();
            _loadingChunks.Add(chunk);

            var token = chunk.LoadCancellationToken.Token;

            // 在后台线程执行磁盘 I/O
            Task.Run(() =>
            {
                try
                {
                    // 模拟异步读取磁盘数据
                    // 实际实现中这里会调用 AssetDatabase.LoadAssetsAsync(chunk.AssetIds)
                    Thread.Sleep(10); // 模拟 I/O 延迟

                    if (token.IsCancellationRequested) return;

                    // 数据读取完成后，切换到 GPU 上传状态
                    chunk.State = StreamingChunkState.Uploading;
                    chunk.LoadProgress = 0.9f;

                    // 如果有 RHI 设备，执行 GPU 上传
                    if (_rhiDevice != null)
                    {
                        UploadToGPU(chunk);
                    }
                }
                catch (Exception ex)
                {
                    Log($"块加载失败 [{chunk.GridX},{chunk.GridY},{chunk.GridZ}] {chunk.Layer}: {ex.Message}");
                    chunk.State = StreamingChunkState.Unloaded;
                    chunk.LoadProgress = 0f;
                }
            }, token);

            Log($"开始加载块 [{chunk.GridX},{chunk.GridY},{chunk.GridZ}] 层级={chunk.Layer}");
        }

        /// <summary>
        /// 将块数据上传到 GPU（创建顶点/索引缓冲）。
        /// </summary>
        private void UploadToGPU(StreamingChunk chunk)
        {
            if (_rhiDevice == null) return;

            // 实际实现中，这里会根据块内的资产数据创建 GPU 资源
            // 示例：创建顶点缓冲和索引缓冲
            // var vbDesc = new RHIBufferDesc { SizeInBytes = ..., Flags = RHIBufferFlags.VertexBuffer, HeapType = RHIResourceHeapType.Default };
            // chunk.VertexBuffer = _rhiDevice.CreateBuffer(vbDesc, $"ChunkVB_{chunk.Id}");

            // 模拟上传延迟
            Thread.Sleep(5);
        }

        /// <summary>
        /// 完成块加载。将块加入已加载列表，更新内存统计。
        /// </summary>
        private void CompleteChunkLoad(StreamingChunk chunk)
        {
            chunk.State = StreamingChunkState.Loaded;
            chunk.LoadProgress = 1f;
            chunk.LastAccessTime = DateTime.Now;
            _loadedChunks.Add(chunk);
            _memoryManager.OnChunkLoaded(chunk);
            OnChunkLoaded?.Invoke(chunk);

            // 如果绑定了 ECS，为块内实体添加流式代理组件
            if (_ecs != null)
            {
                foreach (var entityId in chunk.EntityIds)
                {
                    if (_ecs.HasEntity(entityId))
                    {
                        _ecs.AddComponent(entityId, new StreamingProxyComponent
                        {
                            ChunkId = chunk.Id,
                            DestroyOnUnload = false,
                            IsHiddenByStreaming = false
                        });
                    }
                }
            }

            Log($"块加载完成 [{chunk.GridX},{chunk.GridY},{chunk.GridZ}] 层级={chunk.Layer} 内存={chunk.MemoryKB}KB");
        }

        // ══════════════════════════════════════════════════════════════
        // 卸载队列处理
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// 处理卸载队列。逐个卸载块并释放资源。
        /// </summary>
        private void ProcessUnloadQueue()
        {
            while (_unloadQueue.Count > 0)
            {
                var request = _unloadQueue.Dequeue();
                if (!_chunks.TryGetValue(request.ChunkId, out var chunk)) continue;
                if (chunk.State != StreamingChunkState.Loaded) continue;
                if (chunk.IsPersistent || chunk.IsAlwaysLoaded) continue;
                if (chunk.ReferenceCount > 0) continue;

                UnloadChunk(chunk);
            }
        }

        /// <summary>
        /// 卸载指定块。释放 GPU 资源和内存。
        /// </summary>
        private void UnloadChunk(StreamingChunk chunk)
        {
            chunk.State = StreamingChunkState.Unloading;

            // 取消正在进行的加载
            chunk.LoadCancellationToken?.Cancel();
            chunk.LoadCancellationToken?.Dispose();
            chunk.LoadCancellationToken = null;

            // 释放 GPU 资源
            chunk.VertexBuffer?.Dispose();
            chunk.IndexBuffer?.Dispose();
            chunk.VertexBuffer = null;
            chunk.IndexBuffer = null;

            // 更新 ECS 实体
            if (_ecs != null)
            {
                foreach (var entityId in chunk.EntityIds)
                {
                    if (_ecs.HasEntity(entityId) && _ecs.HasComponent<StreamingProxyComponent>(entityId))
                    {
                        var proxy = _ecs.GetComponent<StreamingProxyComponent>(entityId);
                        if (proxy.DestroyOnUnload)
                        {
                            _ecs.DestroyEntity(entityId);
                        }
                        else
                        {
                            proxy.IsHiddenByStreaming = true;
                            _ecs.SetComponent(entityId, proxy);
                        }
                    }
                }
            }

            // 更新状态
            chunk.State = StreamingChunkState.Unloaded;
            chunk.LoadProgress = 0f;
            _loadedChunks.Remove(chunk);
            _loadingChunks.Remove(chunk);
            _memoryManager.OnChunkUnloaded(chunk);
            OnChunkUnloaded?.Invoke(chunk);

            Log($"块已卸载 [{chunk.GridX},{chunk.GridY},{chunk.GridZ}] 层级={chunk.Layer}");
        }

        // ══════════════════════════════════════════════════════════════
        // 内存淘汰
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// 检查内存预算，必要时通过 LRU 策略淘汰最不活跃的块。
        /// </summary>
        private void ProcessMemoryEviction()
        {
            long evictTarget = _memoryManager.CheckEvictionNeeded();
            if (evictTarget <= 0) return;

            var candidates = new List<StreamingChunk>(_loadedChunks);
            var evictionList = _memoryManager.SelectEvictionTargets(candidates, evictTarget);

            if (evictionList.Count == 0)
            {
                Log($"内存压力 {MemoryUsageRatio:P1}，但没有可淘汰的块");
                return;
            }

            Log($"内存淘汰：释放 {evictionList.Count} 个块，目标 {evictTarget}KB");
            foreach (var chunk in evictionList)
            {
                UnloadChunk(chunk);
            }
        }

        // ══════════════════════════════════════════════════════════════
        // 已加载块更新
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// 更新已加载块的访问时间和优先级得分。
        /// </summary>
        private void UpdateLoadedChunks()
        {
            foreach (var chunk in _loadedChunks)
            {
                // 如果块在视点附近，刷新访问时间
                float dist = chunk.DistanceTo(_viewpoint);
                if (dist < _config.CellSize * 2f)
                    chunk.LastAccessTime = DateTime.Now;

                chunk.PriorityScore = CalculatePriority(chunk);
            }
        }

        // ══════════════════════════════════════════════════════════════
        // ECS 实体可见性同步
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// 同步 ECS 实体可见性。
        /// 当块被卸载时，相关实体被隐藏；块加载后恢复可见。
        /// </summary>
        private void SyncEntityVisibility()
        {
            if (_ecs == null) return;

            // 获取所有拥有流式代理组件的实体
            var entities = _ecs.QueryWithComponent<StreamingProxyComponent>();
            foreach (var entityId in entities)
            {
                var proxy = _ecs.GetComponent<StreamingProxyComponent>(entityId);

                if (_chunks.TryGetValue(proxy.ChunkId, out var chunk))
                {
                    bool shouldBeVisible = chunk.State == StreamingChunkState.Loaded;

                    if (proxy.IsHiddenByStreaming && shouldBeVisible)
                    {
                        // 块重新加载后恢复实体可见性
                        proxy.IsHiddenByStreaming = false;
                        _ecs.SetComponent(entityId, proxy);
                    }
                    else if (!proxy.IsHiddenByStreaming && !shouldBeVisible)
                    {
                        // 块被卸载后隐藏实体
                        proxy.IsHiddenByStreaming = true;
                        _ecs.SetComponent(entityId, proxy);
                    }
                }
            }
        }

        // ══════════════════════════════════════════════════════════════
        // 公共 API —— 手动控制
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// 强制加载指定块（给予最高优先级）。
        /// </summary>
        public void ForceLoadChunk(Guid chunkId)
        {
            if (_chunks.TryGetValue(chunkId, out var chunk) && chunk.State == StreamingChunkState.Unloaded)
            {
                EnqueueLoadRequest(chunkId, float.MaxValue);
                Log($"强制加载块 {chunkId}");
            }
        }

        /// <summary>
        /// 强制卸载指定块。
        /// </summary>
        public void ForceUnloadChunk(Guid chunkId)
        {
            EnqueueUnloadRequest(chunkId);
        }

        /// <summary>
        /// 将块标记为持久（不会被自动卸载）。
        /// </summary>
        public void MarkChunkPersistent(Guid chunkId, bool persistent = true)
        {
            if (_chunks.TryGetValue(chunkId, out var chunk))
                chunk.IsPersistent = persistent;
        }

        /// <summary>
        /// 增加块的引用计数（防止被卸载）。
        /// </summary>
        public void AddChunkReference(Guid chunkId)
        {
            if (_chunks.TryGetValue(chunkId, out var chunk))
                chunk.ReferenceCount++;
        }

        /// <summary>
        /// 减少块的引用计数。
        /// </summary>
        public void ReleaseChunkReference(Guid chunkId)
        {
            if (_chunks.TryGetValue(chunkId, out var chunk) && chunk.ReferenceCount > 0)
                chunk.ReferenceCount--;
        }

        /// <summary>
        /// 获取所有已加载块。
        /// </summary>
        public List<StreamingChunk> GetLoadedChunks() => new(_loadedChunks);

        /// <summary>
        /// 获取所有正在加载的块。
        /// </summary>
        public List<StreamingChunk> GetLoadingChunks() => new(_loadingChunks);

        /// <summary>
        /// 获取所有流式体积。
        /// </summary>
        public List<StreamingVolume> GetVolumes() => new(_volumes);

        /// <summary>
        /// 获取指定层级的加载半径。
        /// </summary>
        public int GetLoadRadius(StreamingLayer layer) =>
            _config.LayerLoadRadius.GetValueOrDefault(layer, 4);

        /// <summary>
        /// 设置内存预算上限。
        /// </summary>
        public void SetMemoryBudget(long budgetKB) => _memoryManager.SetBudget(budgetKB);

        // ══════════════════════════════════════════════════════════════
        // 关闭与清理
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// 关闭流式加载系统，释放所有资源。
        /// </summary>
        public void Shutdown()
        {
            _shutdownToken.Cancel();

            // 卸载所有已加载块
            foreach (var chunk in _loadedChunks.ToList())
                UnloadChunk(chunk);

            // 取消所有加载中的块
            foreach (var chunk in _loadingChunks)
            {
                chunk.LoadCancellationToken?.Cancel();
                chunk.State = StreamingChunkState.Unloaded;
            }

            _loadedChunks.Clear();
            _loadingChunks.Clear();
            _loadQueue.Clear();
            _unloadQueue.Clear();
            _chunks.Clear();
            _gridMap.Clear();
            _volumes.Clear();

            _shutdownToken.Dispose();
            Log("世界流式加载系统已关闭");
        }

        /// <summary>
        /// 获取系统统计信息的字符串表示。
        /// </summary>
        public string GetStatsString()
        {
            return $"[WorldStreaming] " +
                   $"总块数={TotalChunkCount} " +
                   $"已加载={LoadedChunkCount} " +
                   $"加载中={LoadingChunkCount} " +
                   $"待加载={PendingLoadRequests} " +
                   $"待卸载={PendingUnloadRequests} " +
                   $"内存={MemoryUsageKB / 1024}MB/{_config.MemoryBudgetKB / 1024}MB ({MemoryUsageRatio:P0}) " +
                   $"体积数={VolumeCount}";
        }

        /// <summary>日志辅助方法</summary>
        private void Log(string msg) => OnLogMessage?.Invoke($"[WorldStreaming] {msg}");
    }
}
