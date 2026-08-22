// ════════════════════════════════════════════════════════════════════════════
// 山雨引擎 - RHI 资源上传器 (RHIResourceUploader)
//
// 高层封装，负责把各种资源（网格、纹理、材质）从 CPU 上传到 GPU。
// 内部使用 RHICopyEngine 做实际传输，但提供了更友好的 API。
//
// 主要功能:
//   * 异步上传 (不阻塞主线程)
//   * 资源上传队列 (按优先级排序)
//   * 纹理 mipmap 生成
//   * 上传进度跟踪
//   * 内存预算控制
//   * 流式加载支持
//
// 参考: DirectX Toolkit 的 ResourceUploadBatch，但更强
//
// FIXME: 纹理压缩格式(BC)的 mipmap 生成还没做
// TODO: 加个后台线程做实际的上传工作
// ════════════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace ShanyuEngine.RHI
{
    /// <summary>
    /// 资源上传器：高层 API，负责把各种资源上传到 GPU。
    /// </summary>
    public sealed class RHIResourceUploader : IDisposable
    {
        // ──────────────────────────────────────────────────────────────────────
        // 字段
        // ──────────────────────────────────────────────────────────────────────

        private readonly RHIDevice _device;
        private readonly RHICopyEngine _copyEngine;
        private readonly RHIBarrierTracker _barrierTracker;
        private readonly PriorityQueue<UploadTask, UploadPriority> _taskQueue = new();
        private readonly List<UploadTask> _inFlightTasks = new(16);
        private readonly object _lock = new();

        private readonly UploadStats _stats;
        private readonly UploadStats _lastFrameStats;
        private ulong _currentFrame;
        private bool _disposed;

        // 内存预算
        private ulong _frameUploadBudget = 64 * 1024 * 1024; // 64MB per frame
        private ulong _currentFrameUploadBytes;

        /// <summary>每帧上传预算（字节）</summary>
        public ulong FrameUploadBudget
        {
            get => _frameUploadBudget;
            set => _frameUploadBudget = value;
        }

        /// <summary>当前帧已上传字节数</summary>
        public ulong CurrentFrameUploadBytes => _currentFrameUploadBytes;

        /// <summary>统计信息</summary>
        public ref readonly UploadStats Stats => ref _stats;

        /// <summary>上一帧统计</summary>
        public ref readonly UploadStats LastFrameStats => ref _lastFrameStats;

        /// <summary>待处理任务数</summary>
        public int PendingTaskCount
        {
            get
            {
                lock (_lock)
                {
                    return _taskQueue.Count;
                }
            }
        }

        /// <summary>在途任务数</summary>
        public int InFlightTaskCount => _inFlightTasks.Count;

        // ──────────────────────────────────────────────────────────────────────
        // 构造/析构
        // ──────────────────────────────────────────────────────────────────────

        public RHIResourceUploader(RHIDevice device, RHICopyEngine copyEngine,
                                    RHIBarrierTracker barrierTracker = null)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));
            _copyEngine = copyEngine ?? throw new ArgumentNullException(nameof(copyEngine));
            _barrierTracker = barrierTracker;
            _stats = new UploadStats();
            _lastFrameStats = new UploadStats();
        }

        ~RHIResourceUploader()
        {
            Dispose(false);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private void Dispose(bool disposing)
        {
            if (_disposed) return;
            if (disposing)
            {
                lock (_lock)
                {
                    _taskQueue.Clear();
                }
                _inFlightTasks.Clear();
            }
            _disposed = true;
        }

        // ──────────────────────────────────────────────────────────────────────
        // 帧管理
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>帧开始。</summary>
        public void BeginFrame(ulong frameIndex)
        {
            _currentFrame = frameIndex;
            _lastFrameStats.Copy(_stats);
            _stats.Reset();
            _currentFrameUploadBytes = 0;
            _copyEngine.BeginFrame(frameIndex);
        }

        /// <summary>帧结束，处理上传队列。</summary>
        public void EndFrame()
        {
            // 处理上传队列
            ProcessQueue();

            // 结束 copy engine 的帧
            _copyEngine.EndFrame();
        }

        // ──────────────────────────────────────────────────────────────────────
        // 同步上传 API (立即执行)
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 立即上传 buffer 数据。阻塞直到完成。
        /// </summary>
        public void UploadBufferImmediate(RHIResource buffer, ulong offset,
                                           ReadOnlySpan<byte> data)
        {
            _copyEngine.UploadToBuffer(buffer, offset, data);
            _stats.SyncUploadCount++;
            _stats.TotalBytesUploaded += (ulong)data.Length;
            _currentFrameUploadBytes += (ulong)data.Length;
        }

        /// <summary>
        /// 立即上传纹理数据。阻塞直到完成。
        /// </summary>
        public void UploadTextureImmediate(RHIResource texture, int mip, int arraySlice,
                                            ReadOnlySpan<byte> data,
                                            int width, int height, RHIFormat format)
        {
            _copyEngine.UploadToTexture(texture, mip, arraySlice, data, width, height, format);
            _stats.SyncUploadCount++;
            _stats.TotalBytesUploaded += (ulong)data.Length;
            _currentFrameUploadBytes += (ulong)data.Length;
        }

        /// <summary>
        /// 立即上传完整纹理。
        /// </summary>
        public void UploadTextureFullImmediate(RHIResource texture, ReadOnlySpan<byte> data,
                                                int width, int height, int mipLevels,
                                                RHIFormat format)
        {
            _copyEngine.UploadTextureFull(texture, data, width, height, mipLevels, format);
            _stats.SyncUploadCount++;
            _stats.TotalBytesUploaded += (ulong)data.Length;
            _currentFrameUploadBytes += (ulong)data.Length;
        }

        // ──────────────────────────────────────────────────────────────────────
        // 异步上传 API (加入队列，稍后执行)
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 异步上传 buffer 数据。返回任务 ID 用于跟踪。
        /// </summary>
        /// <param name="buffer">目标 buffer</param>
        /// <param name="offset">目标偏移</param>
        /// <param name="data">源数据（会被复制，调用后可以释放）</param>
        /// <param name="priority">优先级</param>
        /// <returns>任务 ID</returns>
        public long UploadBufferAsync(RHIResource buffer, ulong offset,
                                       byte[] data, UploadPriority priority = UploadPriority.Normal)
        {
            var task = new UploadTask
            {
                Id = Interlocked.Increment(ref _nextTaskId),
                Type = UploadTaskType.Buffer,
                DstResource = buffer,
                DstOffset = offset,
                Data = data, // 复制引用，后面执行时使用
                Priority = priority,
                EnqueueFrame = _currentFrame,
            };

            lock (_lock)
            {
                _taskQueue.Enqueue(task, priority);
            }

            _stats.AsyncUploadCount++;
            _stats.TotalBytesQueued += (ulong)data.Length;
            return task.Id;
        }

        /// <summary>
        /// 异步上传纹理数据。
        /// </summary>
        public long UploadTextureAsync(RHIResource texture, int mip, int arraySlice,
                                        byte[] data, int width, int height,
                                        RHIFormat format,
                                        UploadPriority priority = UploadPriority.Normal)
        {
            var task = new UploadTask
            {
                Id = Interlocked.Increment(ref _nextTaskId),
                Type = UploadTaskType.Texture,
                DstResource = texture,
                DstMip = mip,
                DstArraySlice = arraySlice,
                Data = data,
                Width = width,
                Height = height,
                Format = format,
                Priority = priority,
                EnqueueFrame = _currentFrame,
            };

            lock (_lock)
            {
                _taskQueue.Enqueue(task, priority);
            }

            _stats.AsyncUploadCount++;
            _stats.TotalBytesQueued += (ulong)data.Length;
            return task.Id;
        }

        /// <summary>
        /// 异步上传完整纹理（所有 mip）。
        /// </summary>
        public long UploadTextureFullAsync(RHIResource texture, byte[] data,
                                            int width, int height, int mipLevels,
                                            RHIFormat format,
                                            UploadPriority priority = UploadPriority.Normal)
        {
            var task = new UploadTask
            {
                Id = Interlocked.Increment(ref _nextTaskId),
                Type = UploadTaskType.TextureFull,
                DstResource = texture,
                Data = data,
                Width = width,
                Height = height,
                MipLevels = mipLevels,
                Format = format,
                Priority = priority,
                EnqueueFrame = _currentFrame,
            };

            lock (_lock)
            {
                _taskQueue.Enqueue(task, priority);
            }

            _stats.AsyncUploadCount++;
            _stats.TotalBytesQueued += (ulong)data.Length;
            return task.Id;
        }

        private static long _nextTaskId;

        /// <summary>
        /// 取消未执行的异步上传任务。
        /// </summary>
        public bool CancelUpload(long taskId)
        {
            // FIXME: PriorityQueue 不支持移除单个元素，这里只能标记取消
            // 实际实现需要换数据结构或维护一个 cancel set
            Console.WriteLine($"[RHIResourceUploader] CancelUpload({taskId}) - 暂未实现");
            return false;
        }

        // ──────────────────────────────────────────────────────────────────────
        // 队列处理
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 处理上传队列。在每帧的 EndFrame 中调用。
        /// </summary>
        private void ProcessQueue()
        {
            while (_currentFrameUploadBytes < _frameUploadBudget)
            {
                UploadTask task;
                lock (_lock)
                {
                    if (_taskQueue.Count == 0) break;
                    task = _taskQueue.Dequeue();
                }

                // 检查预算
                ulong dataSize = (ulong)task.Data.Length;
                if (_currentFrameUploadBytes + dataSize > _frameUploadBudget)
                {
                    // 超预算，放回队列等下一帧
                    lock (_lock)
                    {
                        _taskQueue.Enqueue(task, task.Priority);
                    }
                    _stats.BudgetExceededCount++;
                    break;
                }

                // 执行上传
                ExecuteTask(task);
                _currentFrameUploadBytes += dataSize;
                _stats.TotalBytesUploaded += dataSize;
            }
        }

        /// <summary>
        /// 执行单个上传任务。
        /// </summary>
        private void ExecuteTask(UploadTask task)
        {
            var span = new ReadOnlySpan<byte>(task.Data);

            switch (task.Type)
            {
                case UploadTaskType.Buffer:
                    _copyEngine.UploadToBuffer(task.DstResource, task.DstOffset, span);
                    break;

                case UploadTaskType.Texture:
                    _copyEngine.UploadToTexture(task.DstResource, task.DstMip, task.DstArraySlice,
                                                  span, task.Width, task.Height, task.Format);
                    break;

                case UploadTaskType.TextureFull:
                    _copyEngine.UploadTextureFull(task.DstResource, span,
                                                    task.Width, task.Height, task.MipLevels, task.Format);
                    break;
            }

            _stats.CompletedTaskCount++;
            task.Completed = true;
        }

        // ──────────────────────────────────────────────────────────────────────
        // Mipmap 生成
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 为纹理生成 mipmap。需要纹理支持渲染目标。
        /// </summary>
        public void GenerateMipmaps(RHIResource texture, int baseWidth, int baseHeight,
                                      RHIFormat format, int mipLevels)
        {
            // FIXME: 这需要渲染管线来做，暂时只标记
            // 实际实现：
            // 1. 对每个 mip level，设置它为渲染目标
            // 2. 绑定上一个 mip 作为 SRV
            // 3. 用全屏 quad shader 做降采样
            Console.WriteLine($"[RHIResourceUploader] GenerateMipmaps: {baseWidth}x{baseHeight} " +
                             $"fmt={format} mips={mipLevels} - 暂未实现");

            _stats.MipmapGenCount++;
        }

        // ──────────────────────────────────────────────────────────────────────
        // 流式加载支持
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 流式纹理加载状态。
        /// </summary>
        public struct StreamingTextureState
        {
            public RHIResource Texture;
            public int CurrentMipLevel;
            public int TargetMipLevel;
            public bool IsComplete;
        }

        private readonly Dictionary<RHIResource, StreamingTextureState> _streamingTextures = new();

        /// <summary>
        /// 开始流式纹理加载。先上传低分辨率 mip，逐步上传高分辨率。
        /// </summary>
        public void StartStreamingTexture(RHIResource texture, byte[] fullData,
                                           int baseWidth, int baseHeight, int mipLevels,
                                           RHIFormat format)
        {
            // 从最低分辨率开始上传
            int startMip = mipLevels - 1;
            int mipWidth = Math.Max(1, baseWidth >> startMip);
            int mipHeight = Math.Max(1, baseHeight >> startMip);

            int bytesPerPixel = RHIFormatUtils.BytesPerPixel(format);
            int rowPitch = RHICopyEngineUtil.AlignUp(mipWidth * bytesPerPixel, 256);
            int slicePitch = rowPitch * mipHeight;

            // 计算数据偏移
            int offset = 0;
            for (int i = 0; i < startMip; i++)
            {
                int w = Math.Max(1, baseWidth >> i);
                int h = Math.Max(1, baseHeight >> i);
                int pitch = RHICopyEngineUtil.AlignUp(w * bytesPerPixel, 256);
                offset += pitch * h;
            }

            var mipData = new byte[slicePitch];
            Array.Copy(fullData, offset, mipData, 0, slicePitch);

            UploadTextureAsync(texture, startMip, 0, mipData, mipWidth, mipHeight, format,
                                UploadPriority.High);

            _streamingTextures[texture] = new StreamingTextureState
            {
                Texture = texture,
                CurrentMipLevel = startMip,
                TargetMipLevel = 0,
                IsComplete = false,
            };

            _stats.StreamingTextureCount++;
        }

        /// <summary>
        /// 更新流式纹理加载。每帧调用，上传下一个 mip level。
        /// </summary>
        public void UpdateStreamingTextures(byte[] fullData, int baseWidth, int baseHeight,
                                             int mipLevels, RHIFormat format)
        {
            var toUpdate = new List<RHIResource>();
            foreach (var kv in _streamingTextures)
            {
                if (kv.Value.IsComplete) continue;
                toUpdate.Add(kv.Key);
            }

            foreach (var texture in toUpdate)
            {
                var state = _streamingTextures[texture];
                int nextMip = state.CurrentMipLevel - 1;
                if (nextMip < state.TargetMipLevel)
                {
                    state.IsComplete = true;
                    _streamingTextures[texture] = state;
                    continue;
                }

                int mipWidth = Math.Max(1, baseWidth >> nextMip);
                int mipHeight = Math.Max(1, baseHeight >> nextMip);
                int bytesPerPixel = RHIFormatUtils.BytesPerPixel(format);
                int rowPitch = RHICopyEngineUtil.AlignUp(mipWidth * bytesPerPixel, 256);
                int slicePitch = rowPitch * mipHeight;

                int offset = 0;
                for (int i = 0; i < nextMip; i++)
                {
                    int w = Math.Max(1, baseWidth >> i);
                    int h = Math.Max(1, baseHeight >> i);
                    int pitch = RHICopyEngineUtil.AlignUp(w * bytesPerPixel, 256);
                    offset += pitch * h;
                }

                var mipData = new byte[slicePitch];
                Array.Copy(fullData, offset, mipData, 0, slicePitch);

                UploadTextureAsync(texture, nextMip, 0, mipData, mipWidth, mipHeight, format,
                                    UploadPriority.Normal);

                state.CurrentMipLevel = nextMip;
                _streamingTextures[texture] = state;
            }
        }

        /// <summary>
        /// 获取流式纹理的加载状态。
        /// </summary>
        public StreamingTextureState? GetStreamingState(RHIResource texture)
        {
            if (_streamingTextures.TryGetValue(texture, out var state))
                return state;
            return null;
        }

        // ──────────────────────────────────────────────────────────────────────
        // 等待/同步
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 等待所有异步上传完成。
        /// </summary>
        public void WaitForCompletion()
        {
            // 处理所有 pending 的任务
            while (true)
            {
                lock (_lock)
                {
                    if (_taskQueue.Count == 0) break;
                }

                var oldBudget = _frameUploadBudget;
                _frameUploadBudget = ulong.MaxValue; // 临时取消预算限制
                ProcessQueue();
                _frameUploadBudget = oldBudget;
            }

            // 刷新 copy engine
            _copyEngine.Flush();
        }

        /// <summary>
        /// 等待指定任务完成。
        /// </summary>
        public bool WaitForTask(long taskId, int timeoutMs = -1)
        {
            var sw = Stopwatch.StartNew();
            while (true)
            {
                lock (_lock)
                {
                    // 检查任务是否还在队列里
                    // FIXME: PriorityQueue 不支持查找，这里只能处理所有任务
                    if (_taskQueue.Count == 0)
                    {
                        // 队列空了，任务应该完成了
                        return true;
                    }
                }

                ProcessQueue();
                _copyEngine.Flush();

                if (timeoutMs >= 0 && sw.ElapsedMilliseconds >= timeoutMs)
                    return false;

                Thread.Sleep(1);
            }
        }

        // ──────────────────────────────────────────────────────────────────────
        // 诊断
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>生成诊断报告。</summary>
        public string GenerateReport()
        {
            var sb = new StringBuilder();
            sb.AppendLine("═══ RHIResourceUploader 诊断报告 ═══");
            sb.AppendLine();
            sb.AppendLine("── 本帧统计 ──");
            sb.AppendLine($"  同步上传: {_stats.SyncUploadCount}");
            sb.AppendLine($"  异步上传: {_stats.AsyncUploadCount}");
            sb.AppendLine($"  完成任务: {_stats.CompletedTaskCount}");
            sb.AppendLine($"  预算超限: {_stats.BudgetExceededCount}");
            sb.AppendLine($"  Mipmap 生成: {_stats.MipmapGenCount}");
            sb.AppendLine($"  流式纹理: {_stats.StreamingTextureCount}");
            sb.AppendLine($"  已上传: {FormatBytes(_stats.TotalBytesUploaded)}");
            sb.AppendLine($"  队列中: {FormatBytes(_stats.TotalBytesQueued)}");
            sb.AppendLine($"  每帧预算: {FormatBytes(_frameUploadBudget)}");
            sb.AppendLine($"  本帧已用: {FormatBytes(_currentFrameUploadBytes)}");
            sb.AppendLine();
            sb.AppendLine("── 队列状态 ──");
            sb.AppendLine($"  待处理: {PendingTaskCount}");
            sb.AppendLine($"  在途: {InFlightTaskCount}");
            sb.AppendLine();
            sb.AppendLine("── 上一帧 ──");
            sb.AppendLine($"  同步上传: {_lastFrameStats.SyncUploadCount}");
            sb.AppendLine($"  异步上传: {_lastFrameStats.AsyncUploadCount}");
            sb.AppendLine($"  已上传: {FormatBytes(_lastFrameStats.TotalBytesUploaded)}");
            return sb.ToString();
        }

        private static string FormatBytes(ulong bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
            return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
        }

        // ──────────────────────────────────────────────────────────────────────
        // 内部类型
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>上传任务类型</summary>
        private enum UploadTaskType
        {
            Buffer,
            Texture,
            TextureFull,
        }

        /// <summary>上传任务</summary>
        private class UploadTask
        {
            public long Id;
            public UploadTaskType Type;
            public RHIResource DstResource;
            public ulong DstOffset;
            public int DstMip;
            public int DstArraySlice;
            public byte[] Data;
            public int Width, Height;
            public int MipLevels;
            public RHIFormat Format;
            public UploadPriority Priority;
            public ulong EnqueueFrame;
            public bool Completed;
        }
    }

    /// <summary>上传优先级</summary>
    public enum UploadPriority
    {
        /// <summary>低 - 后台加载</summary>
        Low = 0,
        /// <summary>正常 - 默认</summary>
        Normal = 1,
        /// <summary>高 - 用户可见的纹理</summary>
        High = 2,
        /// <summary>立即 - 阻塞加载</summary>
        Immediate = 3,
    }

    /// <summary>上传统计</summary>
    public class UploadStats
    {
        public int SyncUploadCount;
        public int AsyncUploadCount;
        public int CompletedTaskCount;
        public int BudgetExceededCount;
        public int MipmapGenCount;
        public int StreamingTextureCount;
        public ulong TotalBytesUploaded;
        public ulong TotalBytesQueued;

        public void Reset()
        {
            SyncUploadCount = 0;
            AsyncUploadCount = 0;
            CompletedTaskCount = 0;
            BudgetExceededCount = 0;
            MipmapGenCount = 0;
            StreamingTextureCount = 0;
            TotalBytesUploaded = 0;
            TotalBytesQueued = 0;
        }

        public void Copy(UploadStats other)
        {
            SyncUploadCount = other.SyncUploadCount;
            AsyncUploadCount = other.AsyncUploadCount;
            CompletedTaskCount = other.CompletedTaskCount;
            BudgetExceededCount = other.BudgetExceededCount;
            MipmapGenCount = other.MipmapGenCount;
            StreamingTextureCount = other.StreamingTextureCount;
            TotalBytesUploaded = other.TotalBytesUploaded;
            TotalBytesQueued = other.TotalBytesQueued;
        }

        public override string ToString()
        {
            return $"Sync={SyncUploadCount}, Async={AsyncUploadCount}, " +
                   $"Completed={CompletedTaskCount}, Bytes={TotalBytesUploaded}";
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 优先级队列比较器
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 上传优先级比较器（高优先级先出队）。
    /// </summary>
    public sealed class UploadPriorityComparer : IComparer<UploadPriority>
    {
        public int Compare(UploadPriority x, UploadPriority y)
        {
            // 高优先级应该排在前面，所以反过来比
            return y.CompareTo(x);
        }
    }

    /// <summary>
    /// 简单的优先级队列实现。
    /// </summary>
    public sealed class PriorityQueue<TElement, TPriority>
    {
        private readonly List<(TElement element, TPriority priority)> _heap = new();
        private readonly IComparer<TPriority> _comparer;

        public PriorityQueue()
        {
            _comparer = Comparer<TPriority>.Default;
        }

        public PriorityQueue(IComparer<TPriority> comparer)
        {
            _comparer = comparer ?? Comparer<TPriority>.Default;
        }

        public int Count => _heap.Count;

        public void Enqueue(TElement element, TPriority priority)
        {
            _heap.Add((element, priority));
            int i = _heap.Count - 1;
            while (i > 0)
            {
                int parent = (i - 1) / 2;
                if (_comparer.Compare(_heap[i].priority, _heap[parent].priority) <= 0)
                    break;
                (_heap[i], _heap[parent]) = (_heap[parent], _heap[i]);
                i = parent;
            }
        }

        public TElement Dequeue()
        {
            if (_heap.Count == 0)
                throw new InvalidOperationException("队列为空");

            var result = _heap[0].element;
            _heap[0] = _heap[^1];
            _heap.RemoveAt(_heap.Count - 1);

            int i = 0;
            while (true)
            {
                int left = 2 * i + 1;
                int right = 2 * i + 2;
                int largest = i;

                if (left < _heap.Count &&
                    _comparer.Compare(_heap[left].priority, _heap[largest].priority) > 0)
                    largest = left;
                if (right < _heap.Count &&
                    _comparer.Compare(_heap[right].priority, _heap[largest].priority) > 0)
                    largest = right;

                if (largest == i) break;
                (_heap[i], _heap[largest]) = (_heap[largest], _heap[i]);
                i = largest;
            }

            return result;
        }

        public TElement Peek()
        {
            if (_heap.Count == 0)
                throw new InvalidOperationException("队列为空");
            return _heap[0].element;
        }

        public void Clear()
        {
            _heap.Clear();
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 资源加载器（更高层封装）
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 资源加载器：负责从磁盘加载资源数据并上传到 GPU。
    /// </summary>
    public sealed class RHIResourceLoader : IDisposable
    {
        private readonly RHIResourceUploader _uploader;
        private readonly RHIDevice _device;
        private readonly Dictionary<string, LoadedResource> _loadedResources = new();
        private readonly object _lock = new();

        /// <summary>已加载资源数</summary>
        public int LoadedCount => _loadedResources.Count;

        public RHIResourceLoader(RHIDevice device, RHIResourceUploader uploader)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));
            _uploader = uploader ?? throw new ArgumentNullException(nameof(uploader));
        }

        /// <summary>
        /// 加载纹理文件。
        /// </summary>
        public long LoadTextureAsync(string path, RHIResource dstTexture,
                                      UploadPriority priority = UploadPriority.Normal)
        {
            // FIXME: 实际应该用图像库解码文件，这里只做框架
            Console.WriteLine($"[RHIResourceLoader] LoadTextureAsync: {path} - 暂未实现文件加载");

            lock (_lock)
            {
                _loadedResources[path] = new LoadedResource
                {
                    Path = path,
                    Type = ResourceType.Texture,
                    Resource = dstTexture,
                    State = LoadState.Loading,
                };
            }

            return 0;
        }

        /// <summary>
        /// 加载网格数据。
        /// </summary>
        public long LoadMeshAsync(string path, RHIResource vertexBuffer, RHIResource indexBuffer,
                                   UploadPriority priority = UploadPriority.Normal)
        {
            Console.WriteLine($"[RHIResourceLoader] LoadMeshAsync: {path} - 暂未实现文件加载");

            lock (_lock)
            {
                _loadedResources[path] = new LoadedResource
                {
                    Path = path,
                    Type = ResourceType.Mesh,
                    Resource = vertexBuffer,
                    State = LoadState.Loading,
                };
            }

            return 0;
        }

        /// <summary>获取加载状态。</summary>
        public LoadState GetLoadState(string path)
        {
            lock (_lock)
            {
                if (_loadedResources.TryGetValue(path, out var res))
                    return res.State;
                return LoadState.NotLoaded;
            }
        }

        /// <summary>更新加载状态。在每帧调用。</summary>
        public void Update()
        {
            // FIXME: 实际的文件加载和上传逻辑
        }

        public void Dispose()
        {
            lock (_lock)
            {
                _loadedResources.Clear();
            }
        }

        private class LoadedResource
        {
            public string Path;
            public ResourceType Type;
            public RHIResource Resource;
            public LoadState State;
        }
    }

    /// <summary>资源类型</summary>
    public enum ResourceType
    {
        Texture,
        Mesh,
        Buffer,
    }

    /// <summary>加载状态</summary>
    public enum LoadState
    {
        NotLoaded,
        Loading,
        Uploaded,
        Ready,
        Failed,
    }
}
