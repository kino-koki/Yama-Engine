using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace ShanyuEngine.Runtime
{
    // ============================================================
    //  1. CommandBuffer — GPU 命令缓冲抽象
    // ============================================================

    /// <summary>
    /// GPU 命令缓冲抽象（对应 Vulkan CommandBuffer / D3D12 CommandList）
    /// </summary>
    public sealed class CommandBuffer : IDisposable
    {
        private readonly List<CommandEntry> _entries;
        private bool _isOpen;
        private bool _disposed;

        /// <summary>缓冲包含的命令数</summary>
        public int CommandCount => _entries.Count;

        /// <summary>所属 Worker 线程 ID（-1 表示主线程）</summary>
        public int OwnerThreadId { get; }

        /// <summary>是否已打开</summary>
        public bool IsOpen => _isOpen;

        public CommandBuffer(int ownerThreadId = -1)
        {
            _entries = new List<CommandEntry>();
            OwnerThreadId = ownerThreadId;
            _isOpen = false;
        }

        /// <summary>打开缓冲区准备录制</summary>
        public void Open()
        {
            _isOpen = true;
            _entries.Clear();
        }

        /// <summary>关闭缓冲区（结束录制）</summary>
        public void Close()
        {
            _isOpen = false;
        }

        /// <summary>添加绘制命令</summary>
        public void Draw(int vertexCount, int instanceCount = 1, int firstVertex = 0, int firstInstance = 0)
        {
            EnsureOpen();
            _entries.Add(new CommandEntry(CommandType.Draw, vertexCount, instanceCount, firstVertex, firstInstance));
        }

        /// <summary>添加索引绘制命令</summary>
        public void DrawIndexed(
            int indexCount, int instanceCount = 1,
            int firstIndex = 0, int vertexOffset = 0, int firstInstance = 0)
        {
            EnsureOpen();
            _entries.Add(new CommandEntry(CommandType.DrawIndexed, indexCount, instanceCount, firstIndex, vertexOffset));
        }

        /// <summary>添加 Compute Dispatch 命令</summary>
        public void Dispatch(int threadGroupsX, int threadGroupsY, int threadGroupsZ)
        {
            EnsureOpen();
            _entries.Add(new CommandEntry(CommandType.Dispatch, threadGroupsX, threadGroupsY, threadGroupsZ));
        }

        /// <summary>添加管线绑定命令</summary>
        public void BindPipeline(Guid pipelineId)
        {
            EnsureOpen();
            _entries.Add(CommandEntry.CreateBindPipeline(pipelineId));
        }

        /// <summary>添加资源屏障命令</summary>
        public void ResourceBarrier(Guid resource, string barrierType)
        {
            EnsureOpen();
            _entries.Add(CommandEntry.CreateBarrier(resource, barrierType));
        }

        /// <summary>添加 RenderPass 开始命令</summary>
        public void BeginRenderPass(string passName)
        {
            EnsureOpen();
            _entries.Add(CommandEntry.CreateBeginPass(passName));
        }

        /// <summary>添加 RenderPass 结束命令</summary>
        public void EndRenderPass()
        {
            EnsureOpen();
            _entries.Add(CommandEntry.CreateEndPass());
        }

        /// <summary>追加另一个命令缓冲的所有命令（Secondary Command Buffer / Bundle 概念）</summary>
        public void ExecuteBundle(CommandBuffer bundle)
        {
            if (bundle == null) throw new ArgumentNullException(nameof(bundle));
            EnsureOpen();
            _entries.AddRange(bundle._entries);
        }

        /// <summary>获取所有命令条目（只读）</summary>
        public ReadOnlySpan<CommandEntry> GetEntries()
        {
            return _entries.ToArray();
        }

        /// <summary>获取指定范围的命令</summary>
        public ReadOnlySpan<CommandEntry> GetRange(int start, int count)
        {
            if (start < 0 || count < 0 || start + count > _entries.Count)
                throw new ArgumentOutOfRangeException();
            var arr = new CommandEntry[count];
            _entries.CopyTo(start, arr, 0, count);
            return arr;
        }

        /// <summary>清空所有命令</summary>
        public void Clear()
        {
            _entries.Clear();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void EnsureOpen()
        {
            if (!_isOpen) throw new InvalidOperationException("CommandBuffer is not open for recording.");
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _entries.Clear();
                _disposed = true;
            }
            GC.SuppressFinalize(this);
        }
    }

    // ============================================================
    //  2. CommandEntry — 命令条目
    // ============================================================

    /// <summary>
    /// 命令条目类型
    /// </summary>
    public enum CommandType
    {
        Draw,
        DrawIndexed,
        Dispatch,
        BindPipeline,
        ResourceBarrier,
        BeginRenderPass,
        EndRenderPass,
        Bundle
    }

    /// <summary>
    /// 命令条目（轻量值类型）
    /// </summary>
    public readonly struct CommandEntry
    {
        public CommandType Type;
        public int Param0, Param1, Param2, Param3;
        public Guid ResourceGuid;
        public string? StringParam;

        public CommandEntry(CommandType type, int p0, int p1, int p2, int p3)
        {
            Type = type; Param0 = p0; Param1 = p1; Param2 = p2; Param3 = p3;
            ResourceGuid = Guid.Empty; StringParam = null;
        }

        public static CommandEntry CreateBindPipeline(Guid pipelineId)
            => new() { Type = CommandType.BindPipeline, ResourceGuid = pipelineId };

        public static CommandEntry CreateBarrier(Guid resource, string barrierType)
            => new() { Type = CommandType.ResourceBarrier, ResourceGuid = resource, StringParam = barrierType };

        public static CommandEntry CreateBeginPass(string passName)
            => new() { Type = CommandType.BeginRenderPass, StringParam = passName };

        public static CommandEntry CreateEndPass()
            => new() { Type = CommandType.EndRenderPass };
    }

    // ============================================================
    //  3. CommandBufferPool — 命令分配器池（每线程一个）
    // ============================================================

    /// <summary>
    /// 命令缓冲池，确保每个线程拥有独立的 CommandBuffer（避免锁竞争）。
    /// </summary>
    public sealed class CommandBufferPool : IDisposable
    {
        private readonly ConcurrentDictionary<int, CommandBuffer> _threadBuffers;
        private readonly int _preallocatedSize;
        private bool _disposed;

        /// <summary>池中缓冲数</summary>
        public int Count => _threadBuffers.Count;

        public CommandBufferPool(int preallocatedSize = 4096)
        {
            _threadBuffers = new ConcurrentDictionary<int, CommandBuffer>();
            _preallocatedSize = preallocatedSize;
        }

        /// <summary>
        /// 获取当前线程的 CommandBuffer（不存在则创建）
        /// </summary>
        public CommandBuffer GetThreadCommandBuffer()
        {
            int threadId = Environment.CurrentManagedThreadId;
            return _threadBuffers.GetOrAdd(threadId, _ =>
            {
                var buffer = new CommandBuffer(threadId);
                buffer.Open();
                return buffer;
            });
        }

        /// <summary>
        /// 重置所有线程的 CommandBuffer（在帧开始时调用）
        /// </summary>
        public void ResetAll()
        {
            foreach (var kvp in _threadBuffers)
            {
                kvp.Value.Close();
                kvp.Value.Clear();
                kvp.Value.Open();
            }
        }

        /// <summary>
        /// 收集所有线程录制的 CommandBuffer
        /// </summary>
        public CommandBuffer[] CollectAll()
        {
            var buffers = new List<CommandBuffer>();
            foreach (var kvp in _threadBuffers)
            {
                kvp.Value.Close();
                if (kvp.Value.CommandCount > 0)
                    buffers.Add(kvp.Value);
            }
            return buffers.ToArray();
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                foreach (var kvp in _threadBuffers)
                    kvp.Value.Dispose();
                _threadBuffers.Clear();
                _disposed = true;
            }
            GC.SuppressFinalize(this);
        }
    }

    // ============================================================
    //  4. PassRange — 每个Worker负责的DrawCall范围
    // ============================================================

    /// <summary>
    /// RenderPass 中 DrawCall 的子范围，分配给单个 Worker 线程录制。
    /// </summary>
    public readonly struct PassRange
    {
        /// <summary>起始命令索引</summary>
        public int StartIndex;

        /// <summary>命令数量</summary>
        public int Count;

        /// <summary>分配的 Worker 线程 ID</summary>
        public int WorkerThreadId;

        /// <summary>所属 RenderPass 名称</summary>
        public string PassName;

        public PassRange(int startIndex, int count, int workerThreadId, string passName)
        {
            StartIndex = startIndex;
            Count = count;
            WorkerThreadId = workerThreadId;
            PassName = passName;
        }

        /// <summary>结束索引（不含）</summary>
        public int EndIndex => StartIndex + Count;

        /// <summary>是否有效</summary>
        public bool IsValid => Count > 0;
    }

    // ============================================================
    //  5. RenderPassDescriptor — RenderPass 描述
    // ============================================================

    /// <summary>
    /// RenderPass 描述，包含所有要在该 Pass 中执行的命令
    /// </summary>
    public sealed class RenderPassDescriptor
    {
        /// <summary>Pass 名称</summary>
        public string Name { get; }

        /// <summary>所属 CommandBuffer</summary>
        public CommandBuffer SourceBuffer { get; }

        /// <summary>命令在 SourceBuffer 中的起始索引</summary>
        public int StartIndex { get; }

        /// <summary>命令数量</summary>
        public int CommandCount { get; }

        /// <summary>是否允许并行录制</summary>
        public bool AllowParallel { get; set; } = true;

        /// <summary>期望的分块大小（每 Worker 分配多少命令）</summary>
        public int ChunkSize { get; set; } = 32;

        public RenderPassDescriptor(string name, CommandBuffer source, int startIndex, int commandCount)
        {
            Name = name;
            SourceBuffer = source;
            StartIndex = startIndex;
            CommandCount = commandCount;
        }
    }

    // ============================================================
    //  6. MultiThreadedCommandRecord — 多线程命令录制核心
    // ============================================================

    /// <summary>
    /// 多线程命令录制器。
    /// 主线程生成 RenderPass 列表 -> Worker 线程并行录制 CommandList -> 主线程 Submit 所有 CommandList。
    /// 支持 Secondary Command Buffer（Vulkan）/ Bundle（D3D12）概念。
    /// </summary>
    public sealed class MultiThreadedCommandRecord : IDisposable
    {
        private readonly CommandBufferPool _bufferPool;
        private readonly int _workerCount;
        private readonly Thread[] _workers;
        private volatile bool _isRunning;
        private readonly object _dispatchLock = new();
        private readonly ManualResetEventSlim _workAvailable;
        private readonly ManualResetEventSlim _workComplete;
        private readonly List<WorkItem> _workItems;
        private readonly List<CommandBuffer> _completedBuffers;
        private volatile int _pendingWorkItems;

        /// <summary>Worker 线程数</summary>
        public int WorkerCount => _workerCount;

        /// <summary>命令缓冲池</summary>
        public CommandBufferPool BufferPool => _bufferPool;

        /// <summary>已完成的命令缓冲列表</summary>
        public ReadOnlySpan<CommandBuffer> CompletedBuffers => _completedBuffers.ToArray();

        /// <summary>总提交命令数</summary>
        public long TotalCommandsSubmitted { get; private set; }

        /// <summary>总录制时间（毫秒）</summary>
        public double TotalRecordTimeMs { get; private set; }

        private sealed class WorkItem
        {
            public CommandBuffer SourceBuffer;
            public int SourceStartIndex;
            public int SourceCount;
            public int WorkerThreadId;
            public string PassName;
            public CommandBuffer OutputBuffer;
        }

        /// <summary>
        /// 创建多线程命令录制器
        /// </summary>
        /// <param name="workerCount">工作线程数（默认核心数-1）</param>
        public MultiThreadedCommandRecord(int? workerCount = null)
        {
            _workerCount = workerCount ?? Math.Max(1, Environment.ProcessorCount - 1);
            _bufferPool = new CommandBufferPool();
            _workAvailable = new ManualResetEventSlim(false);
            _workComplete = new ManualResetEventSlim(false);
            _workItems = new List<WorkItem>();
            _completedBuffers = new List<CommandBuffer>();
            _workers = new Thread[_workerCount];

            for (int i = 0; i < _workerCount; i++)
            {
                int idx = i;
                _workers[i] = new Thread(WorkerLoop)
                {
                    Name = $"ShanyuCmdRecorder-{idx}",
                    IsBackground = true,
                    Priority = ThreadPriority.AboveNormal
                };
            }
        }

        /// <summary>
        /// 启动 Worker 线程
        /// </summary>
        public void Start()
        {
            if (_isRunning) return;
            _isRunning = true;
            for (int i = 0; i < _workerCount; i++)
                _workers[i].Start(i);
        }

        /// <summary>
        /// 停止 Worker 线程
        /// </summary>
        public void Stop()
        {
            _isRunning = false;
            _workAvailable.Set();
            for (int i = 0; i < _workerCount; i++)
                _workers[i]?.Join(1000);
        }

        /// <summary>
        /// 主线程入口：生成 RenderPass 列表 -> 分配 -> 并行录制 -> 收集
        /// </summary>
        /// <param name="passes">RenderPass 描述列表</param>
        /// <param name="sourceBuffer">包含所有命令的主命令缓冲</param>
        public void RecordFrame(IReadOnlyList<RenderPassDescriptor> passes, CommandBuffer sourceBuffer)
        {
            if (passes == null) throw new ArgumentNullException(nameof(passes));
            if (sourceBuffer == null) throw new ArgumentNullException(nameof(sourceBuffer));

            var frameTimer = Stopwatch.StartNew();

            // 1. 为每个 Pass 分配 PassRange
            var allRanges = new List<PassRange>();
            int currentWorker = 0;

            foreach (var pass in passes)
            {
                if (pass.CommandCount <= 0) continue;

                if (!pass.AllowParallel || pass.CommandCount <= pass.ChunkSize)
                {
                    // 小 Pass 不值得并行，直接给一个 Worker
                    allRanges.Add(new PassRange(
                        pass.StartIndex, pass.CommandCount,
                        currentWorker, pass.Name));
                    currentWorker = (currentWorker + 1) % _workerCount;
                }
                else
                {
                    // 大 Pass 拆分为多个 Worker 范围
                    int remaining = pass.CommandCount;
                    int offset = pass.StartIndex;

                    while (remaining > 0)
                    {
                        int chunk = Math.Min(pass.ChunkSize, remaining);
                        allRanges.Add(new PassRange(
                            offset, chunk, currentWorker, pass.Name));
                        currentWorker = (currentWorker + 1) % _workerCount;
                        offset += chunk;
                        remaining -= chunk;
                    }
                }
            }

            // 2. 分发工作项到各 Worker 线程
            DispatchWorkItems(sourceBuffer, allRanges);

            // 3. 等待所有 Worker 完成
            _workComplete.Wait();
            _workComplete.Reset();

            frameTimer.Stop();
            TotalRecordTimeMs = frameTimer.ElapsedTotalMilliseconds;

            // 4. 统计
            TotalCommandsSubmitted = 0;
            foreach (var buf in _completedBuffers)
                TotalCommandsSubmitted += buf.CommandCount;
        }

        /// <summary>
        /// 将 PassRange 分发为工作项
        /// </summary>
        private void DispatchWorkItems(CommandBuffer sourceBuffer, List<PassRange> ranges)
        {
            lock (_dispatchLock)
            {
                _workItems.Clear();
                _completedBuffers.Clear();

                int workerAssignIdx = 0;
                foreach (var range in ranges)
                {
                    var outputBuffer = _bufferPool.GetThreadCommandBuffer();
                    // 实际场景中每个 worker 有自己独立的 buffer
                    var workItem = new WorkItem
                    {
                        SourceBuffer = sourceBuffer,
                        SourceStartIndex = range.StartIndex,
                        SourceCount = range.Count,
                        WorkerThreadId = workerAssignIdx % _workerCount,
                        PassName = range.PassName,
                        OutputBuffer = outputBuffer
                    };
                    _workItems.Add(workItem);
                    workerAssignIdx++;
                }

                _pendingWorkItems = _workItems.Count;
                _workAvailable.Set();
            }
        }

        /// <summary>
        /// Worker 线程主循环
        /// </summary>
        private void WorkerLoop(object state)
        {
            while (_isRunning)
            {
                _workAvailable.Wait();
                _workAvailable.Reset();

                while (true)
                {
                    WorkItem? item = null;
                    lock (_dispatchLock)
                    {
                        foreach (var wi in _workItems)
                        {
                            if (wi.WorkerThreadId == (int)state && wi.OutputBuffer.CommandCount == 0)
                            {
                                // 简化：按索引分配
                                break;
                            }
                        }
                    }

                    // 简化的工作分配：每个 worker 取下一个未处理项
                    lock (_dispatchLock)
                    {
                        for (int i = 0; i < _workItems.Count; i++)
                        {
                            var wi = _workItems[i];
                            if (!wi.OutputBuffer.IsOpen)
                            {
                                wi.OutputBuffer = new CommandBuffer(wi.WorkerThreadId);
                                wi.OutputBuffer.Open();
                            }

                            // 检查是否已处理
                            if (wi.OutputBuffer.CommandCount > 0) continue;

                            item = wi;
                            _workItems[i] = item;
                            break;
                        }
                    }

                    if (item == null) break;

                    // 录制命令
                    RecordWorkItem(item);

                    lock (_dispatchLock)
                    {
                        _completedBuffers.Add(item.OutputBuffer);
                    }

                    if (Interlocked.Decrement(ref _pendingWorkItems) == 0)
                    {
                        _workComplete.Set();
                    }
                }

                if (!_isRunning) break;
            }
        }

        /// <summary>
        /// 录制单个工作项（从源缓冲复制命令到输出缓冲）
        /// </summary>
        private static void RecordWorkItem(WorkItem item)
        {
            var commands = item.SourceBuffer.GetRange(item.SourceStartIndex, item.SourceCount);
            foreach (var cmd in commands)
            {
                switch (cmd.Type)
                {
                    case CommandType.Draw:
                        item.OutputBuffer.Draw(cmd.Param0, cmd.Param1, cmd.Param2, cmd.Param3);
                        break;
                    case CommandType.DrawIndexed:
                        item.OutputBuffer.DrawIndexed(cmd.Param0, cmd.Param1, cmd.Param2, cmd.Param3);
                        break;
                    case CommandType.Dispatch:
                        item.OutputBuffer.Dispatch(cmd.Param0, cmd.Param1, cmd.Param2);
                        break;
                    case CommandType.BindPipeline:
                        item.OutputBuffer.BindPipeline(cmd.ResourceGuid);
                        break;
                    case CommandType.ResourceBarrier:
                        item.OutputBuffer.ResourceBarrier(cmd.ResourceGuid, cmd.StringParam ?? "UAV");
                        break;
                    case CommandType.BeginRenderPass:
                        item.OutputBuffer.BeginRenderPass(cmd.StringParam ?? item.PassName);
                        break;
                    case CommandType.EndRenderPass:
                        item.OutputBuffer.EndRenderPass();
                        break;
                }
            }
            item.OutputBuffer.Close();
        }

        /// <summary>
        /// 提交所有已录制的 CommandBuffer（主线程调用）
        /// </summary>
        public ReadOnlySpan<CommandBuffer> Submit()
        {
            _bufferPool.ResetAll();
            var result = _completedBuffers.ToArray();
            _completedBuffers.Clear();
            return result;
        }

        /// <summary>
        /// 获取统计信息
        /// </summary>
        public string GetStats()
        {
            return $"[MultiThreadedCmdRecord] Workers={_workerCount}, " +
                   $"Commands={TotalCommandsSubmitted}, " +
                   $"RecordTime={TotalRecordTimeMs:F2}ms";
        }

        public void Dispose()
        {
            Stop();
            _bufferPool.Dispose();
            _workAvailable.Dispose();
            _workComplete.Dispose();
            GC.SuppressFinalize(this);
        }

        ~MultiThreadedCommandRecord() => Dispose();
    }
}
