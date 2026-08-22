using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace ShanyuEngine.Runtime
{
    // ============================================================
    //  1. IJob 接口
    // ============================================================

    /// <summary>
    /// 作业接口，所有可调度的任务必须实现此接口
    /// </summary>
    public interface IJob
    {
        /// <summary>
        /// 执行作业。可从工作线程调用，因此必须线程安全。
        /// </summary>
        void Execute();
    }

    /// <summary>
    /// 带返回值的作业接口
    /// </summary>
    public interface IJob<TResult>
    {
        TResult Execute();
    }

    // ============================================================
    //  2. JobHandle — 作业依赖句柄
    // ============================================================

    /// <summary>
    /// 作业依赖句柄，用于构建作业间的依赖关系和等待完成。
    /// </summary>
    public readonly struct JobHandle : IEquatable<JobHandle>
    {
        internal readonly int Id;
        internal readonly JobScheduler? Scheduler;
        internal readonly TaskCompletionSource<bool>? CompletionSource;

        internal JobHandle(int id, JobScheduler scheduler, TaskCompletionSource<bool>? tcs)
        {
            Id = id;
            Scheduler = scheduler;
            CompletionSource = tcs;
        }

        /// <summary>是否有效</summary>
        public bool IsValid => Scheduler != null && Id >= 0;

        /// <summary>是否已完成</summary>
        public bool IsCompleted => CompletionSource?.Task.IsCompleted ?? true;

        /// <summary>
        /// 同步等待此作业完成
        /// </summary>
        public void Complete()
        {
            CompletionSource?.Task.Wait();
        }

        /// <summary>
        /// 异步等待此作业完成
        /// </summary>
        public Task WaitAsync()
        {
            return CompletionSource?.Task ?? Task.CompletedTask;
        }

        /// <summary>
        /// 创建依赖于多个前置作业的组合句柄
        /// </summary>
        public static JobHandle Combine(params JobHandle[] handles)
        {
            if (handles == null || handles.Length == 0)
                return Invalid;

            if (handles.Length == 1)
                return handles[0];

            // 当所有前置完成时，组合句柄标记完成
            var combinedTcs = new TaskCompletionSource<bool>();
            int remaining = handles.Length;

            foreach (var handle in handles)
            {
                if (handle.IsCompleted)
                {
                    if (Interlocked.Decrement(ref remaining) == 0)
                        combinedTcs.SetResult(true);
                }
                else
                {
                    handle.CompletionSource?.Task.ContinueWith(_ =>
                    {
                        if (Interlocked.Decrement(ref remaining) == 0)
                            combinedTcs.SetResult(true);
                    }, TaskContinuationOptions.ExecuteSynchronously);
                }
            }

            return new JobHandle(-1, handles[0].Scheduler, combinedTcs);
        }

        public static readonly JobHandle Invalid = new JobHandle(-1, null, null);

        public bool Equals(JobHandle other) => Id == other.Id && ReferenceEquals(Scheduler, other.Scheduler);
        public override bool Equals(object? obj) => obj is JobHandle h && Equals(h);
        public override int GetHashCode() => Id;
        public static bool operator ==(JobHandle a, JobHandle b) => a.Equals(b);
        public static bool operator !=(JobHandle a, JobHandle b) => !a.Equals(b);
    }

    // ============================================================
    //  3. ParallelForJob — 并行 For 循环作业
    // ============================================================

    /// <summary>
    /// 并行 For 循环作业。将迭代范围分块后自动分配到工作线程。
    /// 典型用例：ECS System 并行处理、物理 Broadphase、LOD 计算。
    /// </summary>
    public sealed class ParallelForJob : IJob
    {
        private readonly int _startIndex;
        private readonly int _endIndex;
        private readonly int _batchSize;
        private readonly Action<int, int> _body;
        private readonly JobScheduler _scheduler;
        private JobHandle _handle;

        internal JobHandle Handle => _handle;

        /// <summary>
        /// 创建并行 For 作业
        /// </summary>
        /// <param name="scheduler">调度器</param>
        /// <param name="startIndex">起始索引（含）</param>
        /// <param name="endIndex">结束索引（不含）</param>
        /// <param name="body">处理回调 (startIndex, endIndex)</param>
        /// <param name="batchSize">每批大小（默认64）</param>
        public ParallelForJob(
            JobScheduler scheduler,
            int startIndex,
            int endIndex,
            Action<int, int> body,
            int batchSize = 64)
        {
            if (endIndex <= startIndex) throw new ArgumentException("endIndex must be > startIndex");
            if (batchSize <= 0) throw new ArgumentOutOfRangeException(nameof(batchSize));

            _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
            _startIndex = startIndex;
            _endIndex = endIndex;
            _batchSize = batchSize;
            _body = body ?? throw new ArgumentNullException(nameof(body));
        }

        /// <summary>
        /// 提交到调度器并返回依赖句柄
        /// </summary>
        public JobHandle Schedule(JobHandle? dependsOn = null)
        {
            _handle = _scheduler.ScheduleParallelFor(
                _startIndex, _endIndex, _batchSize, _body, dependsOn);
            return _handle;
        }

        public void Execute()
        {
            // ParallelForJob 本身不直接执行，由调度器拆分后执行
            _body(_startIndex, _endIndex);
        }
    }

    // ============================================================
    //  4. WorkStealingDeque — 工作窃取双端队列
    // ============================================================

    /// <summary>
    /// Chase-Lev 工作窃取双端队列。
    /// 所有者线程从尾部 Push/Pop（无竞争），窃取线程从头部 Steal（CAS）。
    /// </summary>
    internal sealed class WorkStealingDeque
    {
        private volatile IJob[] _tasks;
        private volatile int _top;       // 窃取端索引（头部）
        private volatile int _bottom;   // 所有者端索引（尾部）
        private readonly int _capacity;

        public int Count => Math.Max(0, _bottom - _top);

        public WorkStealingDeque(int capacity = 256)
        {
            _capacity = capacity;
            _tasks = new IJob[capacity];
            _top = 0;
            _bottom = 0;
        }

        /// <summary>
        /// 所有者线程：从尾部压入任务
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Push(IJob job)
        {
            int bottom = _bottom;
            int top = _top;

            if (bottom - top >= _capacity)
                Grow();

            _tasks[bottom % _capacity] = job;
            Volatile.Write(ref _bottom, bottom + 1);
        }

        /// <summary>
        /// 所有者线程：从尾部弹出任务（LIFO，缓存友好）
        /// </summary>
        public IJob? Pop()
        {
            int bottom = Volatile.Read(ref _bottom) - 1;
            Volatile.Write(ref _bottom, bottom);
            int top = Volatile.Read(ref _top);

            IJob? job = null;
            if (bottom < top)
            {
                Volatile.Write(ref _bottom, top);
                return null;
            }

            job = _tasks[bottom % _capacity];
            if (bottom > top)
            {
                _tasks[bottom % _capacity] = null!;
                return job;
            }

            // 单元素竞态
            int oldTop = Interlocked.CompareExchange(ref _top, top + 1, top);
            if (oldTop == top)
            {
                _tasks[bottom % _capacity] = null!;
                return job;
            }

            Volatile.Write(ref _bottom, top + 1);
            return null;
        }

        /// <summary>
        /// 窃取线程：从头部偷取任务（FIFO）
        /// </summary>
        public IJob? Steal()
        {
            int top = Volatile.Read(ref _top);
            int bottom = Volatile.Read(ref _bottom);

            if (top >= bottom) return null;

            var job = _tasks[top % _capacity];
            int oldTop = Interlocked.CompareExchange(ref _top, top + 1, top);
            if (oldTop == top)
            {
                _tasks[top % _capacity] = null!;
                return job;
            }

            return null;
        }

        private void Grow()
        {
            int newSize = _capacity * 2;
            var newTasks = new IJob[newSize];
            int bottom = _bottom;
            int top = _top;
            for (int i = top; i < bottom; i++)
                newTasks[i % newSize] = _tasks[i % _capacity];
            _tasks = newTasks;
        }
    }

    // ============================================================
    //  5. Fence / Signal 同步原语
    // ============================================================

    /// <summary>
    /// 轻量级 Fence 同步原语（非 OS 事件，基于自旋 + 退让）
    /// </summary>
    public class JobFence
    {
        private volatile int _counter;

        /// <summary>当前值</summary>
        public int Current => Volatile.Read(ref _counter);

        /// <summary>Signal：递增计数器</summary>
        public void Signal()
        {
            Interlocked.Increment(ref _counter);
        }

        /// <summary>Signal 多次</summary>
        public void Signal(int count)
        {
            Interlocked.Add(ref _counter, count);
        }

        /// <summary>
        /// 等待计数器达到目标值
        /// </summary>
        public void Wait(int target)
        {
            SpinWait spinner = default;
            while (Volatile.Read(ref _counter) < target)
            {
                if (spinner.NextSpinWillYield)
                    Thread.Yield();
                else
                    spinner.SpinOnce();
            }
        }

        /// <summary>重置</summary>
        public void Reset()
        {
            Interlocked.Exchange(ref _counter, 0);
        }
    }

    // ============================================================
    //  6. JobScheduler — 核心调度器
    // ============================================================

    /// <summary>
    /// 作业系统调度器。
    /// - 线程池：环境线程数-1 个 Worker
    /// - 工作窃取（WorkStealing 双端队列）
    /// - 依赖图执行（拓扑排序）
    /// - Fence/Signal 同步
    /// </summary>
    public sealed class JobScheduler : IDisposable
    {
        private readonly struct PendingJob
        {
            public IJob Job;
            public JobHandle Handle;
            public JobHandle[] Dependencies;
            public int RemainingDeps;
        }

        private readonly WorkStealingDeque[] _workerQueues;
        private readonly Thread[] _workers;
        private readonly ManualResetEventSlim _shutdownEvent;
        private readonly ConcurrentQueue<PendingJob> _pendingQueue;
        private readonly ConcurrentDictionary<int, PendingJob> _runningJobs;
        private readonly JobFence _fence;
        private volatile bool _isRunning;
        private int _nextJobId;
        private readonly int _workerCount;

        /// <summary>Worker 线程数</summary>
        public int WorkerCount => _workerCount;

        /// <summary>Fence 同步</summary>
        public JobFence Fence => _fence;

        /// <summary>是否运行中</summary>
        public bool IsRunning => _isRunning;

        /// <summary>
        /// 创建调度器。默认使用 Environment.ProcessorCount - 1 个工作线程。
        /// </summary>
        public JobScheduler(int? workerCount = null)
        {
            int coreCount = Environment.ProcessorCount;
            _workerCount = workerCount ?? Math.Max(1, coreCount - 1);
            _workerQueues = new WorkStealingDeque[_workerCount];
            _workers = new Thread[_workerCount];
            _shutdownEvent = new ManualResetEventSlim(false);
            _pendingQueue = new ConcurrentQueue<PendingJob>();
            _runningJobs = new ConcurrentDictionary<int, PendingJob>();
            _fence = new JobFence();
            _nextJobId = 0;
            _isRunning = false;

            for (int i = 0; i < _workerCount; i++)
                _workerQueues[i] = new WorkStealingDeque(512);
        }

        /// <summary>
        /// 启动调度器工作线程
        /// </summary>
        public void Start()
        {
            if (_isRunning) return;
            _isRunning = true;

            for (int i = 0; i < _workerCount; i++)
            {
                int workerIdx = i;
                _workers[i] = new Thread(WorkerLoop)
                {
                    Name = $"ShanyuJobWorker-{workerIdx}",
                    IsBackground = true,
                    Priority = ThreadPriority.AboveNormal
                };
                _workers[i].Start(workerIdx);
            }
        }

        /// <summary>
        /// 停止调度器
        /// </summary>
        public void Stop()
        {
            _isRunning = false;
            _shutdownEvent.Set();

            for (int i = 0; i < _workerCount; i++)
                _workers[i]?.Join(1000);

            _shutdownEvent.Reset();
        }

        /// <summary>
        /// 调度单个作业
        /// </summary>
        public JobHandle Schedule(IJob job, JobHandle? dependsOn = null)
        {
            if (job == null) throw new ArgumentNullException(nameof(job));

            var tcs = new TaskCompletionSource<bool>();
            int jobId = Interlocked.Increment(ref _nextJobId);
            var handle = new JobHandle(jobId, this, tcs);

            var deps = dependsOn.HasValue && dependsOn.Value.IsValid
                ? new[] { dependsOn.Value }
                : Array.Empty<JobHandle>();

            var pending = new PendingJob
            {
                Job = job,
                Handle = handle,
                Dependencies = deps,
                RemainingDeps = deps.Length
            };

            if (deps.Length == 0)
            {
                // 无依赖，直接入队
                EnqueueToWorker(pending);
            }
            else
            {
                // 注册依赖等待
                _runningJobs[jobId] = pending;
                foreach (var dep in deps)
                {
                    dep.CompletionSource?.Task.ContinueWith(_ =>
                    {
                        if (Interlocked.Decrement(ref pending.RemainingDeps) == 0)
                        {
                            _runningJobs.TryRemove(jobId, out _);
                            EnqueueToWorker(pending);
                        }
                    }, TaskContinuationOptions.ExecuteSynchronously);
                }
            }

            return handle;
        }

        /// <summary>
        /// 调度带返回值的作业
        /// </summary>
        public JobHandle Schedule<T>(IJob<T> job, out Task<T> resultTask, JobHandle? dependsOn = null)
        {
            var resultTcs = new TaskCompletionSource<T>();
            resultTask = resultTcs.Task;

            return Schedule(new LambdaJob(() =>
            {
                T result = job.Execute();
                resultTcs.SetResult(result);
            }), dependsOn);
        }

        /// <summary>
        /// 调度并行 For
        /// </summary>
        public JobHandle ScheduleParallelFor(
            int startIndex, int endIndex, int batchSize,
            Action<int, int> body,
            JobHandle? dependsOn = null)
        {
            if (endIndex <= startIndex) throw new ArgumentException("endIndex must > startIndex");
            if (batchSize <= 0) throw new ArgumentOutOfRangeException(nameof(batchSize));
            if (body == null) throw new ArgumentNullException(nameof(body));

            int totalItems = endIndex - startIndex;
            int batchCount = (totalItems + batchSize - 1) / batchSize;
            var handles = new JobHandle[batchCount];

            for (int i = 0; i < batchCount; i++)
            {
                int batchStart = startIndex + i * batchSize;
                int batchEnd = Math.Min(batchStart + batchSize, endIndex);
                // 捕获循环变量
                int s = batchStart, e = batchEnd;
                handles[i] = Schedule(new LambdaJob(() => body(s, e)), dependsOn);
            }

            return JobHandle.Combine(handles);
        }

        /// <summary>
        /// 刷新：等待所有已提交的作业完成
        /// </summary>
        public void Flush()
        {
            SpinWait spinner = default;
            while (!_pendingQueue.IsEmpty)
            {
                if (spinner.NextSpinWillYield)
                    Thread.Sleep(1);
                else
                    spinner.SpinOnce();
            }
        }

        /// <summary>
        /// 获取各 Worker 队列负载
        /// </summary>
        public int[] GetWorkerLoads()
        {
            var loads = new int[_workerCount];
            for (int i = 0; i < _workerCount; i++)
                loads[i] = _workerQueues[i].Count;
            return loads;
        }

        private void EnqueueToWorker(PendingJob pending)
        {
            // 选择队列最短的 Worker
            int minIdx = 0;
            int minCount = int.MaxValue;
            for (int i = 0; i < _workerCount; i++)
            {
                int count = _workerQueues[i].Count;
                if (count < minCount)
                {
                    minCount = count;
                    minIdx = i;
                }
            }
            _workerQueues[minIdx].Push(new ExecuteJobWrapper(pending.Job, pending.Handle));
        }

        /// <summary>
        /// Worker 线程主循环
        /// </summary>
        private void WorkerLoop(object state)
        {
            int myIdx = (int)state;
            var myQueue = _workerQueues[myIdx];
            SpinWait spinner = default;

            while (_isRunning)
            {
                // 1. 从自己的队列取任务
                IJob? job = myQueue.Pop();

                // 2. 窃取其他队列的任务
                if (job == null)
                {
                    for (int attempts = 0; attempts < _workerCount && job == null; attempts++)
                    {
                        int stealIdx = (myIdx + attempts + 1) % _workerCount;
                        job = _workerQueues[stealIdx].Steal();
                    }
                }

                // 3. 执行任务
                if (job != null)
                {
                    try
                    {
                        job.Execute();
                        if (job is ExecuteJobWrapper wrapper)
                            wrapper.Handle.CompletionSource?.TrySetResult(true);
                    }
                    catch (Exception ex)
                    {
                        if (job is ExecuteJobWrapper wrapper)
                            wrapper.Handle.CompletionSource?.TrySetException(ex);
                    }
                }
                else
                {
                    // 空闲时等待
                    if (_shutdownEvent.Wait(0))
                        break;

                    if (spinner.NextSpinWillYield)
                    {
                        Thread.Yield();
                    }
                    else
                    {
                        spinner.SpinOnce();
                    }
                }
            }
        }

        /// <summary>
        /// Lambda 包装为 IJob
        /// </summary>
        private sealed class LambdaJob : IJob
        {
            private readonly Action _action;
            public LambdaJob(Action action) { _action = action; }
            public void Execute() => _action();
        }

        /// <summary>
        /// 带 Handle 的执行包装
        /// </summary>
        private sealed class ExecuteJobWrapper : IJob
        {
            public IJob Inner { get; }
            public JobHandle Handle { get; }
            public ExecuteJobWrapper(IJob inner, JobHandle handle)
            {
                Inner = inner;
                Handle = handle;
            }
            public void Execute() => Inner.Execute();
        }

        public void Dispose()
        {
            Stop();
            _shutdownEvent.Dispose();
            GC.SuppressFinalize(this);
        }

        ~JobScheduler() => Dispose();
    }
}
