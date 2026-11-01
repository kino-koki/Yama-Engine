using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace YamaEngine.Runtime
{
    // ============================================================
    //  1. 计算任务资源依赖类型
    // ============================================================

    /// <summary>
    /// 计算任务对资源的访问类型
    /// </summary>
    public enum ComputeResourceAccess
    {
        Read,       // 只读（可并行）
        Write,      // 只写（独占）
        ReadWrite   // 读写（独占）
    }

    /// <summary>
    /// 资源依赖描述
    /// </summary>
    public readonly struct ComputeResourceDependency : IEquatable<ComputeResourceDependency>
    {
        public readonly Guid ResourceId;
        public readonly ComputeResourceAccess Access;

        public ComputeResourceDependency(Guid resourceId, ComputeResourceAccess access)
        {
            ResourceId = resourceId;
            Access = access;
        }

        public bool IsRead => Access == ComputeResourceAccess.Read;
        public bool IsWrite => Access == ComputeResourceAccess.Write || Access == ComputeResourceAccess.ReadWrite;

        public bool Equals(ComputeResourceDependency other) =>
            ResourceId == other.ResourceId && Access == other.Access;
        public override bool Equals(object? obj) => obj is ComputeResourceDependency d && Equals(d);
        public override int GetHashCode() => HashCode.Combine(ResourceId, Access);
    }

    // ============================================================
    //  2. ComputeTask — 计算任务描述
    // ============================================================

    /// <summary>
    /// 异步计算任务描述，包含 Shader、Dispatch 参数和资源依赖。
    /// </summary>
    public sealed class ComputeTask
    {
        /// <summary>任务唯一标识</summary>
        public Guid Id { get; } = Guid.NewGuid();

        /// <summary>Compute Shader 标识</summary>
        public string ShaderName { get; }

        /// <summary>Dispatch X/Y/Z 线程组维度</summary>
        public int ThreadGroupsX { get; set; }
        public int ThreadGroupsY { get; set; }
        public int ThreadGroupsZ { get; set; }

        /// <summary>资源依赖列表</summary>
        public List<ComputeResourceDependency> ResourceDependencies { get; }

        /// <summary>任务优先级（数值越大越优先）</summary>
        public int Priority { get; set; }

        /// <summary>任务完成回调</summary>
        public Action<ComputeTask>? OnComplete { get; set; }

        /// <summary>用户数据</summary>
        public object? UserData { get; set; }

        /// <summary>任务状态</summary>
        public ComputeTaskState State { get; internal set; } = ComputeTaskState.Pending;

        /// <summary>执行耗时（毫秒）</summary>
        public double ExecutionTimeMs { get; internal set; }

        public ComputeTask(
            string shaderName,
            int threadGroupsX, int threadGroupsY = 1, int threadGroupsZ = 1,
            int priority = 0)
        {
            ShaderName = shaderName ?? throw new ArgumentNullException(nameof(shaderName));
            ThreadGroupsX = threadGroupsX;
            ThreadGroupsY = threadGroupsY;
            ThreadGroupsZ = threadGroupsZ;
            ResourceDependencies = new List<ComputeResourceDependency>();
            Priority = priority;
        }

        /// <summary>
        /// 添加资源依赖
        /// </summary>
        public ComputeTask WithResource(Guid resourceId, ComputeResourceAccess access)
        {
            ResourceDependencies.Add(new ComputeResourceDependency(resourceId, access));
            return this;
        }

        /// <summary>
        /// 总线程组数
        /// </summary>
        public int TotalThreadGroups => ThreadGroupsX * ThreadGroupsY * ThreadGroupsZ;
    }

    /// <summary>
    /// 计算任务状态
    /// </summary>
    public enum ComputeTaskState
    {
        Pending,
        Ready,
        Running,
        Completed,
        Failed,
        Cancelled
    }

    // ============================================================
    //  3. AsyncComputeQueue — 异步计算队列
    // ============================================================

    /// <summary>
    /// 与 Graphics 队列并行执行的 Compute 队列。
    /// 管理提交到 GPU Compute Engine 的任务执行。
    /// </summary>
    public sealed class AsyncComputeQueue : IDisposable
    {
        private readonly string _name;
        private readonly BlockingCollection<ComputeTask> _taskQueue;
        private readonly Thread _dispatchThread;
        private volatile bool _isRunning;
        private long _totalTasksDispatched;
        private long _totalTasksCompleted;

        /// <summary>队列名称</summary>
        public string Name => _name;

        /// <summary>是否运行中</summary>
        public bool IsRunning => _isRunning;

        /// <summary>队列中待执行任务数</summary>
        public int PendingCount => _taskQueue.Count;

        /// <summary>总已派发任务数</summary>
        public long TotalDispatched => Interlocked.Read(ref _totalTasksDispatched);

        /// <summary>总已完成任务数</summary>
        public long TotalCompleted => Interlocked.Read(ref _totalTasksCompleted);

        /// <summary>
        /// 创建异步计算队列
        /// </summary>
        /// <param name="name">队列名称（用于调试）</param>
        public AsyncComputeQueue(string name = "AsyncCompute")
        {
            _name = name;
            _taskQueue = new BlockingCollection<ComputeTask>(boundedCapacity: 1024);
            _dispatchThread = new Thread(DispatchLoop)
            {
                Name = $"Yama{_name}Queue",
                IsBackground = true,
                Priority = ThreadPriority.Normal
            };
        }

        /// <summary>
        /// 启动队列
        /// </summary>
        public void Start()
        {
            if (_isRunning) return;
            _isRunning = true;
            _dispatchThread.Start();
        }

        /// <summary>
        /// 提交计算任务
        /// </summary>
        public void Submit(ComputeTask task)
        {
            if (task == null) throw new ArgumentNullException(nameof(task));
            task.State = ComputeTaskState.Pending;
            _taskQueue.Add(task);
            Interlocked.Increment(ref _totalTasksDispatched);
        }

        /// <summary>
        /// 批量提交
        /// </summary>
        public void SubmitRange(IEnumerable<ComputeTask> tasks)
        {
            foreach (var task in tasks)
                Submit(task);
        }

        /// <summary>
        /// 等待队列清空
        /// </summary>
        public void Flush()
        {
            while (_taskQueue.Count > 0)
                Thread.Sleep(1);
        }

        /// <summary>
        /// 派发线程主循环
        /// </summary>
        private void DispatchLoop()
        {
            foreach (var task in _taskQueue.GetConsumingEnumerable())
            {
                if (!_isRunning) break;

                ExecuteTask(task);
            }
        }

        /// <summary>
        /// 执行单个计算任务（模拟 GPU Dispatch）
        /// </summary>
        private void ExecuteTask(ComputeTask task)
        {
            var sw = Stopwatch.StartNew();
            task.State = ComputeTaskState.Running;

            try
            {
                // 模拟 GPU Compute Dispatch
                // 实际实现中，这里会调用图形 API：
                //   commandList.SetComputeRootSignature(...)
                //   commandList.SetPipelineState(computePSO)
                //   commandList.Dispatch(task.ThreadGroupsX, task.ThreadGroupsY, task.ThreadGroupsZ)
                SimulateComputeDispatch(task);

                sw.Stop();
                task.ExecutionTimeMs = sw.Elapsed.TotalMilliseconds;
                task.State = ComputeTaskState.Completed;
                task.OnComplete?.Invoke(task);
                Interlocked.Increment(ref _totalTasksCompleted);
            }
            catch (Exception ex)
            {
                task.State = ComputeTaskState.Failed;
                Debug.WriteLine($"[AsyncComputeQueue] Task {task.Id} failed: {ex.Message}");
            }
        }

        /// <summary>
        /// 模拟 GPU 计算调度（基于线程组数量的虚拟延迟）
        /// </summary>
        private static void SimulateComputeDispatch(ComputeTask task)
        {
            // 模拟计算时间（实际由 GPU 执行，这里仅做占位模拟）
            int totalGroups = task.TotalThreadGroups;
            if (totalGroups > 0)
            {
                // 模拟每线程组约 0.001ms 的执行时间（非常轻量）
                int simulateMicroseconds = Math.Min(totalGroups, 100);
                if (simulateMicroseconds > 0)
                    Thread.SpinWait(simulateMicroseconds * 10);
            }
        }

        public void Dispose()
        {
            _isRunning = false;
            _taskQueue.CompleteAdding();
            _dispatchThread.Join(2000);
            _taskQueue.Dispose();
            GC.SuppressFinalize(this);
        }

        ~AsyncComputeQueue() => Dispose();
    }

    // ============================================================
    //  4. TaskGraphScheduler — 任务图调度器
    // ============================================================

    /// <summary>
    /// 任务图调度器。
    /// 基于资源依赖分析构建任务有向无环图（DAG），
    /// 无依赖任务并行执行，有依赖的任务按拓扑排序执行。
    /// </summary>
    public sealed class TaskGraphScheduler
    {
        // 邻接表：taskId -> 后继任务列表
        private readonly Dictionary<Guid, List<Guid>> _adjacency;
        // 入度计数
        private readonly Dictionary<Guid, int> _inDegree;
        // 所有任务
        private readonly Dictionary<Guid, ComputeTask> _tasks;
        // 资源 -> 当前占用任务 ID（用于冲突检测）
        private readonly Dictionary<Guid, Guid> _resourceOwners;

        private long _tasksScheduled;
        private long _tasksParallelized;

        /// <summary>已调度任务总数</summary>
        public long TasksScheduled => Interlocked.Read(ref _tasksScheduled);

        /// <summary>并行执行的任务组数</summary>
        public long TasksParallelized => Interlocked.Read(ref _tasksParallelized);

        public TaskGraphScheduler()
        {
            _adjacency = new Dictionary<Guid, List<Guid>>();
            _inDegree = new Dictionary<Guid, int>();
            _tasks = new Dictionary<Guid, ComputeTask>();
            _resourceOwners = new Dictionary<Guid, Guid>();
        }

        /// <summary>
        /// 添加任务到图中
        /// </summary>
        public void AddTask(ComputeTask task)
        {
            if (task == null) throw new ArgumentNullException(nameof(task));
            _tasks[task.Id] = task;
            _adjacency[task.Id] = new List<Guid>();
            _inDegree[task.Id] = 0;
        }

        /// <summary>
        /// 分析资源依赖并构建任务依赖图
        /// </summary>
        public void BuildDependencyGraph()
        {
            var allTaskIds = _tasks.Keys.ToList();

            foreach (var taskId in allTaskIds)
            {
                var task = _tasks[taskId];
                _inDegree[taskId] = 0;
                _adjacency[taskId].Clear();
            }

            // 为每对任务分析资源冲突
            for (int i = 0; i < allTaskIds.Count; i++)
            {
                for (int j = i + 1; j < allTaskIds.Count; j++)
                {
                    var taskA = _tasks[allTaskIds[i]];
                    var taskB = _tasks[allTaskIds[j]];

                    if (HasResourceConflict(taskA, taskB))
                    {
                        // 建立依赖：优先级高 -> 优先级低
                        Guid from, to;
                        if (taskA.Priority >= taskB.Priority)
                        {
                            from = taskA.Id;
                            to = taskB.Id;
                        }
                        else
                        {
                            from = taskB.Id;
                            to = taskA.Id;
                        }

                        // 避免重复边
                        if (!_adjacency[from].Contains(to))
                        {
                            _adjacency[from].Add(to);
                            _inDegree[to]++;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// 检测两个任务是否存在资源冲突
        /// </summary>
        private static bool HasResourceConflict(ComputeTask a, ComputeTask b)
        {
            foreach (var depA in a.ResourceDependencies)
            {
                foreach (var depB in b.ResourceDependencies)
                {
                    if (depA.ResourceId != depB.ResourceId) continue;

                    // 同一资源：
                    // Read + Read = 无冲突（可并行）
                    // Read + Write/ReadWrite = 冲突
                    // Write + Write/ReadWrite = 冲突
                    // ReadWrite + Write/ReadWrite = 冲突
                    if (depA.IsWrite || depB.IsWrite)
                        return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 拓扑排序（Kahn 算法），返回可并行执行的任务批次。
        /// 每个批次内的任务可以安全并行执行。
        /// </summary>
        public List<List<ComputeTask>> ComputeParallelBatches()
        {
            var batches = new List<List<ComputeTask>>();
            var inDegreeCopy = new Dictionary<Guid, int>(_inDegree);
            var readyQueue = new Queue<Guid>();

            // 初始入度为0的任务
            foreach (var kvp in inDegreeCopy)
            {
                if (kvp.Value == 0)
                    readyQueue.Enqueue(kvp.Key);
            }

            int totalTasks = _tasks.Count;
            int processed = 0;

            while (readyQueue.Count > 0)
            {
                // 当前批次：所有入度为 0 的任务（可并行）
                var batch = new List<ComputeTask>();
                int batchSize = readyQueue.Count;

                for (int i = 0; i < batchSize; i++)
                {
                    Guid taskId = readyQueue.Dequeue();
                    batch.Add(_tasks[taskId]);
                    processed++;

                    // 减少后继任务入度
                    foreach (var successor in _adjacency[taskId])
                    {
                        inDegreeCopy[successor]--;
                        if (inDegreeCopy[successor] == 0)
                            readyQueue.Enqueue(successor);
                    }
                }

                batches.Add(batch);
                Interlocked.Increment(ref _tasksParallelized);
            }

            if (processed != totalTasks)
                throw new InvalidOperationException(
                    "Task graph contains a cycle. Cannot compute parallel batches.");

            Interlocked.Add(ref _tasksScheduled, totalTasks);
            return batches;
        }

        /// <summary>
        /// 将批次提交到异步计算队列执行
        /// </summary>
        public void ExecuteBatches(AsyncComputeQueue queue)
        {
            if (queue == null) throw new ArgumentNullException(nameof(queue));

            var batches = ComputeParallelBatches();

            foreach (var batch in batches)
            {
                // 按优先级排序同一批次
                var sortedBatch = batch.OrderByDescending(t => t.Priority).ToList();

                // 同一批次内的任务可以并行，全部提交
                foreach (var task in sortedBatch)
                {
                    task.State = ComputeTaskState.Ready;
                    queue.Submit(task);
                }

                // 等待当前批次完成后再执行下一批次
                queue.Flush();
            }
        }

        /// <summary>
        /// 清空图
        /// </summary>
        public void Clear()
        {
            _tasks.Clear();
            _adjacency.Clear();
            _inDegree.Clear();
            _resourceOwners.Clear();
        }
    }

    // ============================================================
    //  5. AsyncComputeManager — 异步计算管线统一管理
    // ============================================================

    /// <summary>
    /// 异步计算管线管理器。
    /// 协调 AsyncComputeQueue 和 TaskGraphScheduler，
    /// 提供粒子模拟、LOD 计算、物理求解、纹理流送等异步计算用例。
    /// </summary>
    public sealed class AsyncComputeManager : IDisposable
    {
        private readonly AsyncComputeQueue _computeQueue;
        private readonly TaskGraphScheduler _graphScheduler;
        private readonly List<ComputeTask> _pendingTasks;
        private readonly object _pendingLock = new();
        private bool _disposed;
        private bool _isRunning;

        /// <summary>计算队列</summary>
        public AsyncComputeQueue ComputeQueue => _computeQueue;

        /// <summary>任务图调度器</summary>
        public TaskGraphScheduler GraphScheduler => _graphScheduler;

        /// <summary>是否运行中</summary>
        public bool IsRunning => _isRunning;

        /// <summary>待处理任务数</summary>
        public int PendingTaskCount
        {
            get
            {
                lock (_pendingLock) return _pendingTasks.Count;
            }
        }

        public AsyncComputeManager()
        {
            _computeQueue = new AsyncComputeQueue("AsyncCompute");
            _graphScheduler = new TaskGraphScheduler();
            _pendingTasks = new List<ComputeTask>();
        }

        /// <summary>
        /// 启动异步计算管线
        /// </summary>
        public void Start()
        {
            if (_isRunning) return;
            _isRunning = true;
            _computeQueue.Start();
        }

        /// <summary>
        /// 停止异步计算管线
        /// </summary>
        public void Stop()
        {
            _isRunning = false;
            _computeQueue.Dispose();
        }

        /// <summary>
        /// 提交单个计算任务（直接入队，跳过依赖分析）
        /// </summary>
        public void SubmitTask(ComputeTask task)
        {
            if (task == null) throw new ArgumentNullException(nameof(task));
            if (!_isRunning) return;

            _computeQueue.Submit(task);
        }

        /// <summary>
        /// 提交一组任务到待处理列表，等待 Flush 时统一做依赖分析
        /// </summary>
        public void EnqueueTasks(params ComputeTask[] tasks)
        {
            if (tasks == null) throw new ArgumentNullException(nameof(tasks));
            lock (_pendingLock)
            {
                foreach (var task in tasks)
                    _pendingTasks.Add(task);
            }
        }

        /// <summary>
        /// 刷新：对累积任务做依赖图分析，然后按批次并行提交到 Compute 队列
        /// </summary>
        public void Flush()
        {
            List<ComputeTask> snapshot;
            lock (_pendingLock)
            {
                snapshot = new List<ComputeTask>(_pendingTasks);
                _pendingTasks.Clear();
            }

            if (snapshot.Count == 0) return;

            // 构建依赖图
            _graphScheduler.Clear();
            foreach (var task in snapshot)
                _graphScheduler.AddTask(task);
            _graphScheduler.BuildDependencyGraph();

            // 按批次并行执行
            _graphScheduler.ExecuteBatches(_computeQueue);
        }

        // ---- 预设用例工厂方法 ----

        /// <summary>
        /// 创建粒子模拟 Compute 任务
        /// </summary>
        public static ComputeTask CreateParticleSimulationTask(
            Guid particleBufferId,
            Guid forceBufferId,
            int particleCount,
            int threadGroupSize = 256,
            int priority = 10)
        {
            int groups = (particleCount + threadGroupSize - 1) / threadGroupSize;
            return new ComputeTask("ParticleUpdateCS", groups, 1, 1, priority)
                .WithResource(particleBufferId, ComputeResourceAccess.ReadWrite)
                .WithResource(forceBufferId, ComputeResourceAccess.Read);
        }

        /// <summary>
        /// 创建 LOD 计算 Compute 任务
        /// </summary>
        public static ComputeTask CreateLodComputeTask(
            Guid meshBufferId,
            Guid lodOutputId,
            int meshCount,
            int threadGroupSize = 64,
            int priority = 5)
        {
            int groups = (meshCount + threadGroupSize - 1) / threadGroupSize;
            return new ComputeTask("LodComputeCS", groups, 1, 1, priority)
                .WithResource(meshBufferId, ComputeResourceAccess.Read)
                .WithResource(lodOutputId, ComputeResourceAccess.Write);
        }

        /// <summary>
        /// 创建物理求解 Compute 任务
        /// </summary>
        public static ComputeTask CreatePhysicsSolveTask(
            Guid rigidBodyBufferId,
            Guid constraintBufferId,
            int bodyCount,
            int threadGroupSize = 128,
            int priority = 8)
        {
            int groups = (bodyCount + threadGroupSize - 1) / threadGroupSize;
            return new ComputeTask("PhysicsSolveCS", groups, 1, 1, priority)
                .WithResource(rigidBodyBufferId, ComputeResourceAccess.ReadWrite)
                .WithResource(constraintBufferId, ComputeResourceAccess.ReadWrite);
        }

        /// <summary>
        /// 创建纹理流送 Compute 任务（Mip 生成）
        /// </summary>
        public static ComputeTask CreateTextureStreamingTask(
            Guid textureId,
            Guid stagingBufferId,
            int mipLevel,
            int width, int height,
            int threadGroupSize = 8,
            int priority = 3)
        {
            int groupsX = (width + threadGroupSize - 1) / threadGroupSize;
            int groupsY = (height + threadGroupSize - 1) / threadGroupSize;
            return new ComputeTask("TextureMipGenCS", groupsX, groupsY, 1, priority)
                .WithResource(textureId, ComputeResourceAccess.Write)
                .WithResource(stagingBufferId, ComputeResourceAccess.Read);
        }

        /// <summary>
        /// 获取统计信息
        /// </summary>
        public string GetStats()
        {
            return $"[AsyncCompute] Queue: {_computeQueue.TotalDispatched} dispatched, " +
                   $"{_computeQueue.TotalCompleted} completed, " +
                   $"Pending: {PendingTaskCount}, " +
                   $"Graph: {_graphScheduler.TasksScheduled} scheduled in {_graphScheduler.TasksParallelized} batches";
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                Stop();
                _disposed = true;
            }
            GC.SuppressFinalize(this);
        }

        ~AsyncComputeManager() => Dispose();
    }
}
