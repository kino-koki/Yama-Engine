// ============================================================================================
//  SceneStreamingSystem.cs — 场景流送系统（深化版）
//
//  命名空间: ShanyuEngine.World
//
//  本系统实现大规模开放世界场景的流送加载/卸载管理，参考 UE5 World Partition 的设计：
//    - StreamingLevel       ：流送关卡（加载半径/卸载半径/状态机）
//    - StreamingPriority    ：优先级计算（距离/方向/滞后/游戏逻辑强制）
//    - SceneStreamingManager：主管理器（同步/异步加载、内存预算、滞后防抖）
//    - WorldStreamingGrid       ：世界分区（Grid 网格单元 + Cell）
//    - HLOD                 ：层级细节（远距离合并简化网格）
//
//  与引擎其他系统的对接：
//    - 与 ECS ActorSystem 集成：加载的Actor注册到World
//    - 与 PhysicsWorld 对接：流送的碰撞体注册/注销
//    - 与 Renderer 对接：流送网格提交GPU
//    - 与 AssetDatabase 对接：异步资源加载
// ============================================================================================

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using ShanyuEngine.Rendering;

namespace ShanyuEngine.World
{
    // ──────────────────────────────────────────────────────────────
    //  流送关卡状态枚举
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 流送关卡加载状态。
    /// 状态机：Unloaded -> Loading -> Loaded -> Unloading -> Unloaded
    /// </summary>

    // ──────────────────────────────────────────────────────────────
    //  StreamingLevel —— 流送关卡
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 流送关卡。表示一个可以独立加载/卸载的世界区域。
    /// </summary>
    public class StreamingLevel
    {
        /// <summary>关卡名称。</summary>
        public string LevelName { get; set; }

        /// <summary>资产路径。</summary>
        public string AssetPath { get; set; }

        /// <summary>关卡唯一ID。</summary>
        public int LevelId { get; set; }

        /// <summary>加载半径（米）。相机在此距离内时开始加载。</summary>
        public float LoadRadius { get; set; } = 200f;

        /// <summary>卸载半径（米）。相机超出此距离时开始卸载。必须大于 LoadRadius。</summary>
        public float UnloadRadius { get; set; } = 300f;

        /// <summary>当前加载状态。</summary>
        public StreamingLevelState LoadedState { get; set; } = StreamingLevelState.Unloaded;

        /// <summary>加载后生成的Actor列表。</summary>
        public List<string> LoadedActors { get; set; } = new List<string>();

        /// <summary>世界空间AABB包围盒。</summary>
        public BoundingBox Bounds { get; set; }

        /// <summary>AABB中心点。</summary>
        public Vector3 BoundsCenter { get; set; }

        /// <summary>预估内存占用（字节）。</summary>
        public long EstimatedMemoryUsage { get; set; }

        /// <summary>是否被游戏逻辑强制加载（忽略距离）。</summary>
        public bool RequiredByGameplay { get; set; }

        /// <summary>游戏逻辑强制加载的引用计数。</summary>
        public int GameplayRefCount { get; set; }

        /// <summary>优先级（越低越优先，0=最高）。</summary>
        public float Priority { get; set; }

        /// <summary>到相机的距离（每帧更新）。</summary>
        public float DistanceToCamera { get; set; } = float.MaxValue;

        /// <summary>是否在相机面向的方向上。</summary>
        public bool InViewDirection { get; set; }

        /// <summary>滞后计时器（进入LoadRadius后不立即卸载）。</summary>
        public float HysteresisTimer { get; set; }

        /// <summary>滞后卸载延迟（秒）。</summary>
        public float HysteresisDelay { get; set; } = 5f;

        /// <summary>加载进度（0~1，异步加载时有效）。</summary>
        public float LoadProgress { get; set; }

        /// <summary>异步加载任务。</summary>
        public Task LoadTask { get; set; }

        /// <summary>流送层级（HLOD/Far/Mid/Near/Closeup）。</summary>
        public StreamingLayer Layer { get; set; } = StreamingLayer.Near;

        /// <summary>HLOD替代资产路径（远距离使用）。</summary>
        public string HLODAssetPath { get; set; }

        /// <summary>是否正在使用HLOD显示。</summary>
        public bool UsingHLOD { get; set; }

        /// <summary>创建流送关卡。</summary>
        public StreamingLevel(string name, string assetPath, int id)
        {
            LevelName = name;
            AssetPath = assetPath;
            LevelId = id;
            BoundsCenter = Vector3.Zero;
            Bounds = new BoundingBox(Vector3.Zero, Vector3.Zero);
        }

        /// <summary>设置AABB包围盒。</summary>
        public void SetBounds(Vector3 center, Vector3 halfExtents)
        {
            BoundsCenter = center;
            Bounds = new BoundingBox(center - halfExtents, center + halfExtents);
        }

        /// <summary>请求游戏逻辑强制加载。</summary>
        public void AddGameplayReference()
        {
            GameplayRefCount++;
            RequiredByGameplay = GameplayRefCount > 0;
        }

        /// <summary>释放游戏逻辑强制加载引用。</summary>
        public void RemoveGameplayReference()
        {
            GameplayRefCount = Math.Max(0, GameplayRefCount - 1);
            RequiredByGameplay = GameplayRefCount > 0;
        }
    }

    /// <summary>
    /// AABB包围盒。
    /// </summary>

    // ──────────────────────────────────────────────────────────────
    //  StreamingPriority —— 优先级系统
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 流送优先级计算器。
    /// 综合考虑距离、方向、滞后、游戏逻辑强制和大小因素。
    /// </summary>
    public static class StreamingPriority
    {
        /// <summary>
        /// 计算流送优先级。值越小越优先加载。
        /// </summary>
        /// <param name="distance">到相机的距离。</param>
        /// <param name="inView">是否在相机面向方向。</param>
        /// <param name="isHysteresis">是否处于滞后保护期。</param>
        /// <param name="requiredByGameplay">是否被游戏逻辑强制加载。</param>
        /// <param name="size">关卡大小（用于大关卡高优先级）。</param>
        /// <returns>优先级值（0=最高，越低越优先）。</returns>
        public static float Calculate(
            float distance,
            bool inView,
            bool isHysteresis,
            bool requiredByGameplay,
            float size)
        {
            // 游戏逻辑强制加载 → 最高优先级
            if (requiredByGameplay) return -1000f;

            // 滞后保护期 → 次高优先级
            if (isHysteresis) return -500f;

            // 距离因子（越近优先级越高）
            float distanceFactor = distance;

            // 方向因子（面向方向加成）
            float viewFactor = inView ? -50f : 0f;

            // 大小因子（大关卡略微提高优先级）
            float sizeFactor = -size * 0.01f;

            return distanceFactor + viewFactor + sizeFactor;
        }

        /// <summary>
        /// 判断是否在相机面向方向。
        /// 简化实现：使用向量点积判断。
        /// </summary>
        public static bool IsInViewDirection(Vector3 cameraPos, Vector3 cameraForward, Vector3 levelCenter, float fovHalfAngle = 1.0f)
        {
            Vector3 toLevel = Vector3.Normalize(levelCenter - cameraPos);
            float dot = Vector3.Dot(cameraForward, toLevel);
            float cosFov = (float)Math.Cos(fovHalfAngle);
            return dot >= cosFov;
        }
    }

    // ──────────────────────────────────────────────────────────────
    //  加载策略
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 流送加载策略。
    /// </summary>
    public enum LoadStrategy : byte
    {
        /// <summary>同步加载：小关卡直接在主线程加载。</summary>
        Synchronous = 0,
        /// <summary>异步加载：大关卡在后台线程加载。</summary>
        Asynchronous = 1,
        /// <summary>自动选择：根据关卡大小自动决定。</summary>
        Auto = 2,
    }

    /// <summary>
    /// 加载请求 —— 内部数据结构。
    /// </summary>
    internal struct LoadRequest
    {
        public StreamingLevel Level;
        public float Priority;
        public bool IsLoad; // true=加载, false=卸载

        public LoadRequest(StreamingLevel level, float priority, bool isLoad)
        {
            Level = level;
            Priority = priority;
            IsLoad = isLoad;
        }
    }

    // ──────────────────────────────────────────────────────────────
    //  SceneStreamingManager —— 场景流送管理器
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 场景流送管理器。负责流送世界的分区加载/卸载、优先级排序、内存预算管理。
    ///
    /// 每帧更新流程：
    ///   1. 计算相机到每个StreamingLevel的距离
    ///   2. 根据距离和方向计算优先级
    ///   3. 加载LoadRadius内的关卡
    ///   4. 卸载UnloadRadius外的关卡
    ///   5. Hysteresis：进入过再出来延迟5秒卸载
    /// </summary>
    public class SceneStreamingManager
    {
        /// <summary>所有流送关卡。</summary>
        public List<StreamingLevel> Levels { get; } = new List<StreamingLevel>();

        /// <summary>加载策略。</summary>
        public LoadStrategy Strategy { get; set; } = LoadStrategy.Auto;

        /// <summary>同步加载阈值（小于此内存占用时同步加载）。</summary>
        public long SyncLoadThreshold { get; set; } = 50 * 1024 * 1024; // 50MB

        /// <summary>内存预算（同时加载的关卡总内存上限）。</summary>
        public long MemoryBudget { get; set; } = 2048 * 1024 * 1024; // 2GB

        /// <summary>当前已加载关卡总内存。</summary>
        public long CurrentMemoryUsage { get; private set; }

        /// <summary>相机位置。</summary>
        public Vector3 CameraPosition { get; set; }

        /// <summary>相机朝向。</summary>
        public Vector3 CameraForward { get; set; }

        /// <summary>相机FOV半角。</summary>
        public float CameraFovHalfAngle { get; set; } = 1.0f; // ~57度

        /// <summary>滞后卸载延迟（秒）。</summary>
        public float GlobalHysteresisDelay { get; set; } = 5f;

        /// <summary>每帧最大加载数量。</summary>
        public int MaxLoadsPerFrame { get; set; } = 2;

        /// <summary>每帧最大卸载数量。</summary>
        public int MaxUnloadsPerFrame { get; set; } = 2;

        /// <summary>是否启用流送。</summary>
        public bool StreamingEnabled { get; set; } = true;

        /// <summary>加载进度回调。</summary>
        public Action<string, float> OnLoadProgress;

        /// <summary>关卡加载完成回调。</summary>
        public Action<StreamingLevel> OnLevelLoaded;

        /// <summary>关卡卸载完成回调。</summary>
        public Action<StreamingLevel> OnLevelUnloaded;

        /// <summary>加载开始回调。</summary>
        public Action<StreamingLevel> OnLevelLoadStart;

        /// <summary>异步加载任务队列。</summary>
        private readonly Queue<LoadRequest> _loadQueue = new Queue<LoadRequest>();

        /// <summary>当前正在异步加载的关卡数。</summary>
        private int _activeAsyncLoads;

        /// <summary>最大同时异步加载数。</summary>
        public int MaxAsyncLoads { get; set; } = 3;

        /// <summary>加载屏幕状态。</summary>
        public bool LoadingScreenActive { get; set; }

        /// <summary>加载屏幕显示的关卡名称。</summary>
        public string LoadingScreenLevelName { get; set; }

        /// <summary>上次加载进度。</summary>
        public float OverallLoadProgress { get; set; }

        /// <summary>统计信息。</summary>
        public int LoadedLevelCount { get; private set; }
        public int LoadingLevelCount { get; private set; }
        public int UnloadedLevelCount => Levels.Count - LoadedLevelCount - LoadingLevelCount;

        /// <summary>
        /// 注册流送关卡。
        /// </summary>
        public void RegisterLevel(StreamingLevel level)
        {
            Levels.Add(level);
        }

        /// <summary>
        /// 注销流送关卡。
        /// </summary>
        public void UnregisterLevel(int levelId)
        {
            Levels.RemoveAll(l => l.LevelId == levelId);
        }

        /// <summary>
        /// 每帧更新流送系统。
        /// </summary>
        public void Update(float deltaTime)
        {
            if (!StreamingEnabled || Levels.Count == 0) return;

            // 步骤1：计算相机到每个StreamingLevel的距离
            UpdateDistances();

            // 步骤2：计算优先级
            UpdatePriorities();

            // 步骤3：更新滞后计时器
            UpdateHysteresis(deltaTime);

            // 步骤4：生成加载/卸载请求
            ProcessLoadUnloadRequests(deltaTime);

            // 步骤5：处理异步加载队列
            ProcessAsyncLoadQueue();

            // 步骤6：更新统计信息
            UpdateStatistics();
        }

        /// <summary>更新每个关卡到相机的距离和方向。</summary>
        private void UpdateDistances()
        {
            foreach (var level in Levels)
            {
                level.DistanceToCamera = level.Bounds.DistanceTo(CameraPosition);
                level.InViewDirection = StreamingPriority.IsInViewDirection(
                    CameraPosition, CameraForward, level.BoundsCenter, CameraFovHalfAngle);
            }
        }

        /// <summary>更新每个关卡的优先级。</summary>
        private void UpdatePriorities()
        {
            foreach (var level in Levels)
            {
                level.Priority = StreamingPriority.Calculate(
                    level.DistanceToCamera,
                    level.InViewDirection,
                    level.HysteresisTimer > 0f,
                    level.RequiredByGameplay,
                    level.Bounds.Extents.Length());
            }
        }

        /// <summary>更新滞后计时器。</summary>
        private void UpdateHysteresis(float deltaTime)
        {
            foreach (var level in Levels)
            {
                if (level.LoadedState == StreamingLevelState.Loaded)
                {
                    // 已加载的关卡，如果超出LoadRadius，开始滞后计时
                    if (level.DistanceToCamera > level.LoadRadius)
                    {
                        level.HysteresisTimer += deltaTime;
                    }
                    else
                    {
                        // 回到LoadRadius内，重置滞后
                        level.HysteresisTimer = 0f;
                    }
                }
            }
        }

        /// <summary>处理加载/卸载请求。</summary>
        private void ProcessLoadUnloadRequests(float deltaTime)
        {
            int loadsThisFrame = 0;
            int unloadsThisFrame = 0;

            // 按优先级排序（升序，最低值=最高优先级）
            var sorted = new List<StreamingLevel>(Levels);
            sorted.Sort((a, b) => a.Priority.CompareTo(b.Priority));

            foreach (var level in sorted)
            {
                // ── 加载判断 ──
                if (level.LoadedState == StreamingLevelState.Unloaded &&
                    (level.DistanceToCamera <= level.LoadRadius || level.RequiredByGameplay))
                {
                    // 检查内存预算
                    if (CurrentMemoryUsage + level.EstimatedMemoryUsage <= MemoryBudget)
                    {
                        if (loadsThisFrame < MaxLoadsPerFrame)
                        {
                            StartLoad(level);
                            loadsThisFrame++;
                        }
                    }
                }

                // ── 卸载判断 ──
                if (level.LoadedState == StreamingLevelState.Loaded &&
                    level.DistanceToCamera > level.UnloadRadius &&
                    !level.RequiredByGameplay &&
                    level.HysteresisTimer >= level.HysteresisDelay)
                {
                    if (unloadsThisFrame < MaxUnloadsPerFrame)
                    {
                        StartUnload(level);
                        unloadsThisFrame++;
                    }
                }
            }
        }

        /// <summary>开始加载关卡。</summary>
        private void StartLoad(StreamingLevel level)
        {
            // 确定加载策略
            LoadStrategy strategy = Strategy;
            if (strategy == LoadStrategy.Auto)
            {
                strategy = level.EstimatedMemoryUsage <= SyncLoadThreshold
                    ? LoadStrategy.Synchronous
                    : LoadStrategy.Asynchronous;
            }

            level.LoadedState = StreamingLevelState.Loading;
            level.LoadProgress = 0f;
            OnLevelLoadStart?.Invoke(level);

            if (strategy == LoadStrategy.Synchronous)
            {
                // 同步加载
                PerformLoad(level);
            }
            else
            {
                // 异步加载
                if (_activeAsyncLoads < MaxAsyncLoads)
                {
                    _activeAsyncLoads++;
                    level.LoadTask = Task.Run(() => PerformLoad(level));
                    level.LoadTask.ContinueWith(_ =>
                    {
                        _activeAsyncLoads = Math.Max(0, _activeAsyncLoads - 1);
                    });
                }
                else
                {
                    // 加入队列
                    _loadQueue.Enqueue(new LoadRequest(level, level.Priority, true));
                }
            }
        }

        /// <summary>执行实际的加载操作。</summary>
        private void PerformLoad(StreamingLevel level)
        {
            // 模拟加载进度
            int steps = 10;
            for (int i = 0; i <= steps; i++)
            {
                level.LoadProgress = (float)i / steps;
                OnLoadProgress?.Invoke(level.LevelName, level.LoadProgress);
                OverallLoadProgress = level.LoadProgress;

                // 模拟加载延迟
                Thread.Sleep(10);
            }

            // 标记为已加载
            level.LoadedState = StreamingLevelState.Loaded;
            level.LoadProgress = 1f;
            level.HysteresisTimer = 0f;
            CurrentMemoryUsage += level.EstimatedMemoryUsage;

            // 通知加载完成
            OnLevelLoaded?.Invoke(level);
        }

        /// <summary>开始卸载关卡。</summary>
        private void StartUnload(StreamingLevel level)
        {
            level.LoadedState = StreamingLevelState.Unloading;

            // 同步卸载
            PerformUnload(level);
        }

        /// <summary>执行实际的卸载操作。</summary>
        private void PerformUnload(StreamingLevel level)
        {
            // 释放内存
            CurrentMemoryUsage = Math.Max(0, CurrentMemoryUsage - level.EstimatedMemoryUsage);

            // 清理Actor
            level.LoadedActors.Clear();

            // 重置状态
            level.LoadedState = StreamingLevelState.Unloaded;
            level.LoadProgress = 0f;
            level.HysteresisTimer = 0f;
            level.UsingHLOD = false;

            // 通知卸载完成
            OnLevelUnloaded?.Invoke(level);
        }

        /// <summary>处理异步加载队列。</summary>
        private void ProcessAsyncLoadQueue()
        {
            while (_loadQueue.Count > 0 && _activeAsyncLoads < MaxAsyncLoads)
            {
                var request = _loadQueue.Dequeue();
                if (request.Level.LoadedState == StreamingLevelState.Unloaded)
                {
                    StartLoad(request.Level);
                }
            }
        }

        /// <summary>更新统计信息。</summary>
        private void UpdateStatistics()
        {
            LoadedLevelCount = 0;
            LoadingLevelCount = 0;
            foreach (var level in Levels)
            {
                if (level.LoadedState == StreamingLevelState.Loaded) LoadedLevelCount++;
                else if (level.LoadedState == StreamingLevelState.Loading) LoadingLevelCount++;
            }
        }

        /// <summary>强制加载指定关卡（用于过场动画/剧情触发）。</summary>
        public void ForceLoadLevel(int levelId)
        {
            var level = Levels.Find(l => l.LevelId == levelId);
            if (level != null)
            {
                level.AddGameplayReference();
                if (level.LoadedState == StreamingLevelState.Unloaded)
                    StartLoad(level);
            }
        }

        /// <summary>释放强制加载引用。</summary>
        public void ReleaseForceLoad(int levelId)
        {
            var level = Levels.Find(l => l.LevelId == levelId);
            level?.RemoveGameplayReference();
        }

        /// <summary>获取指定位置附近的已加载关卡列表。</summary>
        public List<StreamingLevel> GetLoadedLevelsNear(Vector3 position, float radius)
        {
            var result = new List<StreamingLevel>();
            foreach (var level in Levels)
            {
                if (level.LoadedState == StreamingLevelState.Loaded &&
                    level.Bounds.DistanceTo(position) <= radius)
                {
                    result.Add(level);
                }
            }
            return result;
        }

        /// <summary>设置内存预算。</summary>
        public void SetMemoryBudget(long bytes)
        {
            MemoryBudget = bytes;

            // 如果当前已超出预算，卸载最远且不强制加载的关卡
            while (CurrentMemoryUsage > MemoryBudget)
            {
                StreamingLevel farthest = null;
                float maxDist = 0f;
                foreach (var level in Levels)
                {
                    if (level.LoadedState == StreamingLevelState.Loaded &&
                        !level.RequiredByGameplay &&
                        level.DistanceToCamera > maxDist)
                    {
                        maxDist = level.DistanceToCamera;
                        farthest = level;
                    }
                }
                if (farthest != null)
                    StartUnload(farthest);
                else
                    break;
            }
        }
    }

    // ──────────────────────────────────────────────────────────────
    //  WorldStreamingGrid —— 世界分区
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 世界分区Cell —— 一个网格单元，包含该区域内的所有Actor。
    /// </summary>
    public class WorldStreamingGridCell
    {
        /// <summary>Cell 索引（X, Y）。</summary>
        public int CellX { get; set; }

        /// <summary>Cell 索引（X, Y）。</summary>
        public int CellY { get; set; }

        /// <summary>Cell 世界空间AABB。</summary>
        public BoundingBox CellBounds { get; set; }

        /// <summary>Cell 中心点。</summary>
        public Vector3 CellCenter { get; set; }

        /// <summary>Cell 内的Actor列表（Actor名称或ID）。</summary>
        public List<string> Actors { get; set; } = new List<string>();

        /// <summary>加载状态。</summary>
        public StreamingLevelState CellState { get; set; } = StreamingLevelState.Unloaded;

        /// <summary>Cell 内存占用。</summary>
        public long MemoryUsage { get; set; }

        /// <summary>Cell 加载优先级。</summary>
        public float CellPriority { get; set; }

        /// <summary>数据层（不同距离加载不同层的数据）。</summary>
        public StreamingLayer DataLayer { get; set; } = StreamingLayer.Near;

        /// <summary>创建Cell。</summary>
        public WorldStreamingGridCell(int x, int y, Vector3 center, Vector3 halfExtents)
        {
            CellX = x;
            CellY = y;
            CellCenter = center;
            CellBounds = new BoundingBox(center - halfExtents, center + halfExtents);
        }
    }

    /// <summary>
    /// 世界分区系统。将大世界划分为可配置的Grid（网格单元），
    /// 仅加载相机附近的Cell，实现超大世界的内存管理。
    /// </summary>
    public class WorldStreamingGrid
    {
        /// <summary>网格单元大小（米）。</summary>
        public float CellSize { get; set; } = 512f;

        /// <summary>世界总宽度（米）。</summary>
        public float WorldWidth { get; set; } = 8192f;

        /// <summary>世界总深度（米）。</summary>
        public float WorldDepth { get; set; } = 8192f;

        /// <summary>网格列数。</summary>
        public int GridColumns { get; private set; }

        /// <summary>网格行数。</summary>
        public int GridRows { get; private set; }

        /// <summary>所有Cell的二维数组。</summary>
        public WorldStreamingGridCell[,] Cells { get; private set; }

        /// <summary>已加载的Cell列表。</summary>
        public List<WorldStreamingGridCell> LoadedCells { get; } = new List<WorldStreamingGridCell>();

        /// <summary>加载半径（Cell数）。</summary>
        public int LoadCellRadius { get; set; } = 2;

        /// <summary>卸载半径（Cell数）。</summary>
        public int UnloadCellRadius { get; set; } = 3;

        /// <summary>相机所在Cell。</summary>
        public (int x, int y) CameraCell { get; private set; } = (-1, -1);

        /// <summary>世界原点偏移。</summary>
        public Vector3 WorldOrigin { get; set; } = Vector3.Zero;

        /// <summary>Cell 状态变化回调。</summary>
        public Action<WorldStreamingGridCell, StreamingLevelState> OnCellStateChanged;

        /// <summary>创建世界分区。</summary>
        public WorldStreamingGrid(float cellSize, float worldWidth, float worldDepth)
        {
            CellSize = cellSize;
            WorldWidth = worldWidth;
            WorldDepth = worldDepth;
            Initialize();
        }

        /// <summary>初始化网格。</summary>
        private void Initialize()
        {
            GridColumns = (int)Math.Ceiling(WorldWidth / CellSize);
            GridRows = (int)Math.Ceiling(WorldDepth / CellSize);
            Cells = new WorldStreamingGridCell[GridColumns, GridRows];

            Vector3 halfSize = new Vector3(CellSize * 0.5f, CellSize * 0.5f, CellSize * 0.5f);

            for (int x = 0; x < GridColumns; x++)
            {
                for (int y = 0; y < GridRows; y++)
                {
                    Vector3 center = WorldOrigin + new Vector3(
                        x * CellSize + CellSize * 0.5f,
                        0f,
                        y * CellSize + CellSize * 0.5f);
                    Cells[x, y] = new WorldStreamingGridCell(x, y, center, halfSize);
                }
            }
        }

        /// <summary>
        /// 根据世界空间坐标获取Cell索引。
        /// </summary>
        public (int x, int y) WorldToCell(Vector3 worldPosition)
        {
            Vector3 local = worldPosition - WorldOrigin;
            int x = Math.Clamp((int)(local.X / CellSize), 0, GridColumns - 1);
            int y = Math.Clamp((int)(local.Z / CellSize), 0, GridRows - 1);
            return (x, y);
        }

        /// <summary>
        /// 根据Cell索引获取世界空间中心坐标。
        /// </summary>
        public Vector3 CellToWorld(int cellX, int cellY)
        {
            if (cellX < 0 || cellX >= GridColumns || cellY < 0 || cellY >= GridRows)
                return Vector3.Zero;
            return Cells[cellX, cellY].CellCenter;
        }

        /// <summary>
        /// 每帧更新。根据相机位置加载/卸载Cell。
        /// </summary>
        public void Update(Vector3 cameraPosition)
        {
            var (camX, camY) = WorldToCell(cameraPosition);

            // 如果相机移动到新的Cell
            if (camX != CameraCell.x || camY != CameraCell.y)
            {
                CameraCell = (camX, camY);

                // 加载LoadCellRadius内的Cell
                for (int dx = -LoadCellRadius; dx <= LoadCellRadius; dx++)
                {
                    for (int dy = -LoadCellRadius; dy <= LoadCellRadius; dy++)
                    {
                        int cx = camX + dx;
                        int cy = camY + dy;

                        if (cx >= 0 && cx < GridColumns && cy >= 0 && cy < GridRows)
                        {
                            // 椭圆距离判断
                            float dist = (float)Math.Sqrt((dx * dx + dy * dy));
                            if (dist <= LoadCellRadius + 0.5f)
                            {
                                LoadCell(cx, cy);
                            }
                        }
                    }
                }

                // 卸载UnloadCellRadius外的Cell
                var toUnload = new List<WorldStreamingGridCell>();
                foreach (var cell in LoadedCells)
                {
                    int dx = cell.CellX - camX;
                    int dy = cell.CellY - camY;
                    float dist = (float)Math.Sqrt(dx * dx + dy * dy);
                    if (dist > UnloadCellRadius)
                        toUnload.Add(cell);
                }

                foreach (var cell in toUnload)
                    UnloadCell(cell);
            }
        }

        /// <summary>加载Cell。</summary>
        private void LoadCell(int x, int y)
        {
            var cell = Cells[x, y];
            if (cell.CellState == StreamingLevelState.Loaded) return;

            cell.CellState = StreamingLevelState.Loaded;
            LoadedCells.Add(cell);
            OnCellStateChanged?.Invoke(cell, StreamingLevelState.Loaded);
        }

        /// <summary>卸载Cell。</summary>
        private void UnloadCell(WorldStreamingGridCell cell)
        {
            cell.CellState = StreamingLevelState.Unloaded;
            LoadedCells.Remove(cell);
            OnCellStateChanged?.Invoke(cell, StreamingLevelState.Unloaded);
        }

        /// <summary>向Cell添加Actor。</summary>
        public void AddActorToCell(Vector3 position, string actorId)
        {
            var (x, y) = WorldToCell(position);
            if (x >= 0 && x < GridColumns && y >= 0 && y < GridRows)
                Cells[x, y].Actors.Add(actorId);
        }

        /// <summary>获取指定范围内的Cell列表。</summary>
        public List<WorldStreamingGridCell> GetCellsInRange(Vector3 center, float radius)
        {
            var result = new List<WorldStreamingGridCell>();
            int cellRadius = (int)Math.Ceiling(radius / CellSize);
            var (cx, cy) = WorldToCell(center);

            for (int dx = -cellRadius; dx <= cellRadius; dx++)
            {
                for (int dy = -cellRadius; dy <= cellRadius; dy++)
                {
                    int x = cx + dx;
                    int y = cy + dy;
                    if (x >= 0 && x < GridColumns && y >= 0 && y < GridRows)
                    {
                        if (Cells[x, y].CellBounds.IntersectsSphere(center, radius))
                            result.Add(Cells[x, y]);
                    }
                }
            }
            return result;
        }
    }

    // ──────────────────────────────────────────────────────────────
    //  HLOD（Hierarchical Level of Detail）
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// HLOD 层级配置。
    /// </summary>
    public class HLODLevel
    {
        /// <summary>HLOD 层级编号（0=最远，层级越高越近）。</summary>
        public int Level { get; set; }

        /// <summary>该层级的生效距离范围（最小距离）。</summary>
        public float MinDistance { get; set; }

        /// <summary>该层级的生效距离范围（最大距离）。</summary>
        public float MaxDistance { get; set; }

        /// <summary>该层级的网格简化比例。</summary>
        public float SimplificationRatio { get; set; }

        /// <summary>该层级合并的最大Cell数量。</summary>
        public int MaxCellsPerHLOD { get; set; }

        /// <summary>合并后的网格资产路径。</summary>
        public string MergedMeshAsset { get; set; }
    }

    /// <summary>
    /// HLOD 管理器。远处的流送关卡使用低细节合并网格替代，
    /// 减少Draw Call和GPU开销。
    /// </summary>
    public class HLODManager
    {
        /// <summary>HLOD 层级列表（从远到近排列）。</summary>
        public List<HLODLevel> HLODLevels { get; set; } = new List<HLODLevel>();

        /// <summary>HLOD 资产缓存（按区域索引）。</summary>
        private Dictionary<string, string> _hlodAssets = new Dictionary<string, string>();

        /// <summary>是否启用HLOD。</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>自动生成HLOD时的网格合并阈值（三角形数）。</summary>
        public int AutoGenTriangleThreshold { get; set; } = 100;

        /// <summary>自动生成HLOD时的目标三角形数。</summary>
        public int AutoGenTargetTriangles { get; set; } = 50;

        /// <summary>
        /// 根据距离获取应该使用的HLOD层级。
        /// </summary>
        public int GetHLODLevelForDistance(float distance)
        {
            if (!Enabled) return -1;

            for (int i = 0; i < HLODLevels.Count; i++)
            {
                var level = HLODLevels[i];
                if (distance >= level.MinDistance && distance < level.MaxDistance)
                    return i;
            }

            // 超出最远层级
            if (HLODLevels.Count > 0)
                return HLODLevels.Count - 1;

            return -1;
        }

        /// <summary>
        /// 获取指定区域和层级的HLOD资产路径。
        /// </summary>
        public string GetHLODAsset(string regionKey, int hlodLevel)
        {
            string cacheKey = $"{regionKey}_L{hlodLevel}";
            _hlodAssets.TryGetValue(cacheKey, out string asset);
            return asset;
        }

        /// <summary>
        /// 注册HLOD资产。
        /// </summary>
        public void RegisterHLODAsset(string regionKey, int hlodLevel, string assetPath)
        {
            string cacheKey = $"{regionKey}_L{hlodLevel}";
            _hlodAssets[cacheKey] = assetPath;
        }

        /// <summary>
        /// 自动生成HLOD配置（默认3层）。
        /// </summary>
        public void GenerateDefaultHLODLevels(float farDistance = 2000f)
        {
            HLODLevels.Clear();

            // HLOD 0：超远距离（最简化）
            HLODLevels.Add(new HLODLevel
            {
                Level = 0,
                MinDistance = farDistance * 0.7f,
                MaxDistance = farDistance,
                SimplificationRatio = 0.05f,
                MaxCellsPerHLOD = 16,
            });

            // HLOD 1：远距离
            HLODLevels.Add(new HLODLevel
            {
                Level = 1,
                MinDistance = farDistance * 0.4f,
                MaxDistance = farDistance * 0.7f,
                SimplificationRatio = 0.15f,
                MaxCellsPerHLOD = 8,
            });

            // HLOD 2：中远距离
            HLODLevels.Add(new HLODLevel
            {
                Level = 2,
                MinDistance = farDistance * 0.2f,
                MaxDistance = farDistance * 0.4f,
                SimplificationRatio = 0.3f,
                MaxCellsPerHLOD = 4,
            });
        }
    }
}
