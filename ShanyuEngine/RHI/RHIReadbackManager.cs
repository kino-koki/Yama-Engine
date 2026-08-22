// ════════════════════════════════════════════════════════════════════════════
// 山雨引擎 - RHI 回读管理器 (RHIReadbackManager)
//
// 负责把 GPU 资源的数据回读到 CPU 内存。
// 典型用途：GPU 时间戳查询结果、GPU 可见性测试、截图、调试输出等。
//
// 关键设计:
//   * 使用 readback heap (D3D12) 或 host-visible memory (Vulkan)
//   * 双缓冲/三缓冲避免 CPU-GPU 同步等待
//   * 帧延迟回收：回读数据在 N 帧后才能安全读取
//   * 支持纹理和 buffer 的回读
//   * 异步回读：提交后立即返回，稍后查询结果
//
// 注意：回读操作会影响性能，不要在每帧大量使用！
//
// FIXME: 纹理回读的 footprint 计算需要更精确
// TODO: 加个回读结果的回调通知机制
// ════════════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace ShanyuEngine.RHI
{
    /// <summary>
    /// 回读管理器：管理 GPU 到 CPU 的数据回读。
    /// </summary>
    public sealed class RHIReadbackManager : IDisposable
    {
        // ──────────────────────────────────────────────────────────────────────
        // 常量
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>回读缓冲区帧数（三缓冲）</summary>
        public const int NumFrames = 3;

        /// <summary>默认回读缓冲区大小 (64MB)</summary>
        public const ulong DefaultReadbackBufferSize = 64 * 1024 * 1024;

        /// <summary>最大单次回读大小</summary>
        public const ulong MaxReadbackSize = 16 * 1024 * 1024;

        /// <summary>回读帧延迟（GPU 需要几帧才能完成）</summary>
        public const int ReadbackFrameDelay = 2;

        // ──────────────────────────────────────────────────────────────────────
        // 字段
        // ──────────────────────────────────────────────────────────────────────

        private readonly RHIDevice _device;
        private readonly RHIBarrierTracker _barrierTracker;
        private readonly RHICopyEngine _copyEngine;

        // 每帧的回读缓冲区
        private readonly ReadbackFrameBuffer[] _frameBuffers = new ReadbackFrameBuffer[NumFrames];
        private int _currentFrameIndex;

        // 待处理的回读请求
        private readonly List<ReadbackRequest> _pendingRequests = new(16);
        // 已完成但还没被读取的回读结果
        private readonly Dictionary<long, ReadbackResult> _completedResults = new();
        private readonly Queue<long> _resultEvictionQueue = new(); // FIFO 清理旧结果

        // 统计
        private readonly ReadbackStats _stats;
        private readonly ReadbackStats _lastFrameStats;
        private ulong _currentFrame;
        private long _nextRequestId;
        private bool _disposed;

        /// <summary>最大结果缓存数</summary>
        public int MaxResultCacheSize { get; set; } = 64;

        /// <summary>统计信息</summary>
        public ref readonly ReadbackStats Stats => ref _stats;

        /// <summary>上一帧统计</summary>
        public ref readonly ReadbackStats LastFrameStats => ref _lastFrameStats;

        /// <summary>待处理请求数</summary>
        public int PendingCount => _pendingRequests.Count;

        /// <summary>已完成结果数</summary>
        public int CompletedCount => _completedResults.Count;

        // ──────────────────────────────────────────────────────────────────────
        // 构造/析构
        // ──────────────────────────────────────────────────────────────────────

        public RHIReadbackManager(RHIDevice device, RHICopyEngine copyEngine,
                                    RHIBarrierTracker barrierTracker = null,
                                    ulong bufferSize = DefaultReadbackBufferSize)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));
            _copyEngine = copyEngine ?? throw new ArgumentNullException(nameof(copyEngine));
            _barrierTracker = barrierTracker;
            _stats = new ReadbackStats();
            _lastFrameStats = new ReadbackStats();

            // 创建每帧的回读缓冲区
            for (int i = 0; i < NumFrames; i++)
            {
                _frameBuffers[i] = new ReadbackFrameBuffer(device, bufferSize);
            }
        }

        ~RHIReadbackManager()
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
                for (int i = 0; i < NumFrames; i++)
                {
                    _frameBuffers[i]?.Dispose();
                }
                _pendingRequests.Clear();
                _completedResults.Clear();
                _resultEvictionQueue.Clear();
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
        }

        /// <summary>
        /// 帧结束。提交本帧的回读请求，检查之前帧的完成状态。
        /// </summary>
        public void EndFrame()
        {
            // 1. 提交本帧的回读请求
            SubmitPendingRequests();

            // 2. 检查之前帧的回读是否完成
            CheckCompletedRequests();

            // 3. 推进帧索引
            _currentFrameIndex = (_currentFrameIndex + 1) % NumFrames;
        }

        // ──────────────────────────────────────────────────────────────────────
        // Buffer 回读
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 请求回读 buffer 数据。返回请求 ID。
        /// 数据会在几帧后可用，通过 GetResult 获取。
        /// </summary>
        /// <param name="srcBuffer">要回读的 GPU buffer</param>
        /// <param name="offset">源偏移</param>
        /// <param name="size">回读大小</param>
        /// <returns>请求 ID</returns>
        public long RequestBufferReadback(RHIResource srcBuffer, ulong offset, ulong size)
        {
            if (srcBuffer == null) throw new ArgumentNullException(nameof(srcBuffer));
            if (size > MaxReadbackSize)
                throw new ArgumentException($"回读大小 {size} 超过限制 {MaxReadbackSize}");

            var request = new ReadbackRequest
            {
                Id = Interlocked.Increment(ref _nextRequestId),
                Type = ReadbackType.Buffer,
                SrcResource = srcBuffer,
                SrcOffset = offset,
                Size = size,
                RequestFrame = _currentFrame,
                TargetFrameIndex = (_currentFrameIndex + ReadbackFrameDelay) % NumFrames,
                CompletionFrame = _currentFrame + (ulong)ReadbackFrameDelay,
            };

            _pendingRequests.Add(request);
            _stats.BufferReadbackCount++;
            _stats.TotalBytesRequested += size;

            return request.Id;
        }

        /// <summary>
        /// 请求回读纹理数据。
        /// </summary>
        public long RequestTextureReadback(RHIResource srcTexture, int mip, int arraySlice,
                                            int width, int height, RHIFormat format)
        {
            if (srcTexture == null) throw new ArgumentNullException(nameof(srcTexture));

            int bytesPerPixel = RHIFormatUtils.BytesPerPixel(format);
            int rowPitch = RHICopyEngineUtil.AlignUp(width * bytesPerPixel, 256);
            int slicePitch = rowPitch * height;
            ulong size = (ulong)slicePitch;

            if (size > MaxReadbackSize)
                throw new ArgumentException($"回读大小 {size} 超过限制 {MaxReadbackSize}");

            var request = new ReadbackRequest
            {
                Id = Interlocked.Increment(ref _nextRequestId),
                Type = ReadbackType.Texture,
                SrcResource = srcTexture,
                SrcMip = mip,
                SrcArraySlice = arraySlice,
                Width = width,
                Height = height,
                Format = format,
                RowPitch = rowPitch,
                SlicePitch = slicePitch,
                Size = size,
                RequestFrame = _currentFrame,
                TargetFrameIndex = (_currentFrameIndex + ReadbackFrameDelay) % NumFrames,
                CompletionFrame = _currentFrame + (ulong)ReadbackFrameDelay,
            };

            _pendingRequests.Add(request);
            _stats.TextureReadbackCount++;
            _stats.TotalBytesRequested += size;

            return request.Id;
        }

        // ──────────────────────────────────────────────────────────────────────
        // 结果查询
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 检查回读请求是否已完成。
        /// </summary>
        public bool IsReady(long requestId)
        {
            return _completedResults.ContainsKey(requestId);
        }

        /// <summary>
        /// 获取回读结果。如果还没完成返回 null。
        /// </summary>
        public ReadbackResult GetResult(long requestId)
        {
            if (_completedResults.TryGetValue(requestId, out var result))
            {
                _stats.ResultRetrievedCount++;
                return result;
            }
            return null;
        }

        /// <summary>
        /// 获取回读结果并从缓存中移除。
        /// </summary>
        public ReadbackResult GetAndRemoveResult(long requestId)
        {
            if (_completedResults.TryGetValue(requestId, out var result))
            {
                _completedResults.Remove(requestId);
                // 从驱逐队列中移除 (O(n)，但队列不大)
                // FIXME: 用更高效的方式管理
                _stats.ResultRetrievedCount++;
                return result;
            }
            return null;
        }

        /// <summary>
        /// 获取回读数据为 byte 数组。如果未完成返回 null。
        /// </summary>
        public byte[] GetResultData(long requestId)
        {
            var result = GetResult(requestId);
            if (result == null) return null;

            byte[] data = new byte[result.Size];
            Marshal.Copy(result.CpuAddress, data, 0, (int)result.Size);
            return data;
        }

        /// <summary>
        /// 获取回读数据为泛型数组。
        /// </summary>
        public T[] GetResultData<T>(long requestId) where T : struct
        {
            var result = GetResult(requestId);
            if (result == null) return null;

            int elementSize = Unsafe.SizeOf<T>();
            int count = (int)(result.Size / (ulong)elementSize);
            T[] data = new T[count];

            unsafe
            {
                var srcSpan = new Span<byte>((void*)result.CpuAddress, (int)result.Size);
                var dstSpan = MemoryMarshal.Cast<T, byte>(data);
                srcSpan.Slice(0, dstSpan.Length).CopyTo(dstSpan);
            }

            return data;
        }

        /// <summary>
        /// 释放已读取的回读结果。
        /// </summary>
        public void ReleaseResult(long requestId)
        {
            _completedResults.Remove(requestId);
        }

        // ──────────────────────────────────────────────────────────────────────
        // 内部实现
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 提交待处理的回读请求。
        /// 把 GPU 资源拷贝到 readback buffer。
        /// </summary>
        private void SubmitPendingRequests()
        {
            if (_pendingRequests.Count == 0) return;

            var frameBuffer = _frameBuffers[_currentFrameIndex];

            foreach (var request in _pendingRequests)
            {
                // 在 readback buffer 中分配空间
                var alloc = frameBuffer.Allocate(request.Size);
                if (!alloc.IsValid)
                {
                    // 空间不足
                    Console.WriteLine($"[RHIReadbackManager] 回读缓冲区空间不足，跳过请求 {request.Id}");
                    _stats.FailedCount++;
                    continue;
                }

                // 执行拷贝
                switch (request.Type)
                {
                    case ReadbackType.Buffer:
                        _copyEngine.CopyBufferToBuffer(
                            request.SrcResource, request.SrcOffset,
                            alloc.Buffer, alloc.Offset, request.Size);
                        break;

                    case ReadbackType.Texture:
                        _copyEngine.CopyTextureToBuffer(
                            request.SrcResource, request.SrcMip, request.SrcArraySlice,
                            alloc.Buffer, alloc.Offset,
                            request.Width, request.Height, request.Format);
                        break;
                }

                // 记录分配信息，等几帧后读取
                request.AllocBuffer = alloc.Buffer;
                request.AllocOffset = alloc.Offset;

                _stats.SubmittedCount++;
                _stats.TotalBytesReadback += request.Size;
            }

            _pendingRequests.Clear();
        }

        /// <summary>
        /// 检查之前帧的回读是否完成。
        /// </summary>
        private void CheckCompletedRequests()
        {
            // 检查所有之前帧的缓冲区
            for (int i = 0; i < NumFrames; i++)
            {
                var frameBuffer = _frameBuffers[i];
                var completed = frameBuffer.GetCompletedAllocations(_currentFrame);

                foreach (var alloc in completed)
                {
                    var result = new ReadbackResult
                    {
                        RequestId = alloc.RequestId,
                        Type = alloc.Type,
                        Buffer = alloc.Buffer,
                        Offset = alloc.Offset,
                        Size = alloc.Size,
                        CpuAddress = _device.MapReadbackBuffer(alloc.Buffer, alloc.Offset, alloc.Size),
                        Width = alloc.Width,
                        Height = alloc.Height,
                        Format = alloc.Format,
                        RowPitch = alloc.RowPitch,
                        CompleteFrame = _currentFrame,
                    };

                    _completedResults[result.RequestId] = result;
                    _resultEvictionQueue.Enqueue(result.RequestId);

                    _stats.CompletedCount++;
                }

                frameBuffer.ClearCompleted();
            }

            // 清理过多的缓存结果
            while (_resultEvictionQueue.Count > MaxResultCacheSize)
            {
                var oldId = _resultEvictionQueue.Dequeue();
                _completedResults.Remove(oldId);
                _stats.EvictedCount++;
            }
        }

        // ──────────────────────────────────────────────────────────────────────
        // 截图功能
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 请求截图。返回请求 ID。
        /// </summary>
        public long RequestScreenshot(RHIResource srcTexture, int width, int height, RHIFormat format)
        {
            return RequestTextureReadback(srcTexture, 0, 0, width, height, format);
        }

        /// <summary>
        /// 保存截图到文件。
        /// </summary>
        public bool SaveScreenshot(long requestId, string filePath)
        {
            var result = GetResult(requestId);
            if (result == null) return false;

            // FIXME: 实际应该用图像库保存
            Console.WriteLine($"[RHIReadbackManager] SaveScreenshot: {filePath} " +
                             $"({result.Width}x{result.Height}) - 暂未实现文件保存");

            ReleaseResult(requestId);
            return true;
        }

        // ──────────────────────────────────────────────────────────────────────
        // GPU 查询结果回读
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 请求回读 GPU 时间戳查询结果。
        /// </summary>
        public long RequestTimestampReadback(RHIResource queryResultBuffer, ulong offset, int queryCount)
        {
            return RequestBufferReadback(queryResultBuffer, offset, (ulong)(queryCount * sizeof(ulong)));
        }

        /// <summary>
        /// 获取时间戳结果（纳秒）。
        /// </summary>
        public ulong[] GetTimestampResults(long requestId)
        {
            return GetResultData<ulong>(requestId);
        }

        /// <summary>
        /// 计算两个时间戳之间的时间差（毫秒）。
        /// </summary>
        public float GetTimestampDeltaMs(long requestId, int index0, int index1)
        {
            var results = GetTimestampResults(requestId);
            if (results == null || index0 >= results.Length || index1 >= results.Length)
                return 0f;

            // 时间戳单位取决于 GPU，假设是纳秒
            ulong delta = results[index1] - results[index0];
            return delta / 1_000_000f;
        }

        // ──────────────────────────────────────────────────────────────────────
        // 诊断
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>生成诊断报告。</summary>
        public string GenerateReport()
        {
            var sb = new StringBuilder();
            sb.AppendLine("═══ RHIReadbackManager 诊断报告 ═══");
            sb.AppendLine();
            sb.AppendLine("── 本帧统计 ──");
            sb.AppendLine($"  Buffer 回读: {_stats.BufferReadbackCount}");
            sb.AppendLine($"  Texture 回读: {_stats.TextureReadbackCount}");
            sb.AppendLine($"  已提交: {_stats.SubmittedCount}");
            sb.AppendLine($"  已完成: {_stats.CompletedCount}");
            sb.AppendLine($"  已获取: {_stats.ResultRetrievedCount}");
            sb.AppendLine($"  失败: {_stats.FailedCount}");
            sb.AppendLine($"  已驱逐: {_stats.EvictedCount}");
            sb.AppendLine($"  请求字节: {FormatBytes(_stats.TotalBytesRequested)}");
            sb.AppendLine($"  回读字节: {FormatBytes(_stats.TotalBytesReadback)}");
            sb.AppendLine();
            sb.AppendLine("── 队列状态 ──");
            sb.AppendLine($"  待处理: {PendingCount}");
            sb.AppendLine($"  已完成(缓存): {CompletedCount}");
            sb.AppendLine($"  最大缓存: {MaxResultCacheSize}");
            sb.AppendLine();
            sb.AppendLine("── 回读缓冲区 ──");
            for (int i = 0; i < NumFrames; i++)
            {
                sb.AppendLine($"  Frame[{i}]: {_frameBuffers[i].GetStatus()}");
            }
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

        /// <summary>回读类型</summary>
        private enum ReadbackType
        {
            Buffer,
            Texture,
        }

        /// <summary>回读请求</summary>
        private class ReadbackRequest
        {
            public long Id;
            public ReadbackType Type;
            public RHIResource SrcResource;
            public ulong SrcOffset;
            public int SrcMip, SrcArraySlice;
            public int Width, Height;
            public RHIFormat Format;
            public int RowPitch, SlicePitch;
            public ulong Size;
            public ulong RequestFrame;
            public int TargetFrameIndex;
            public ulong CompletionFrame;
            public RHIResource AllocBuffer;
            public ulong AllocOffset;
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 回读帧缓冲区
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 单帧的回读缓冲区。管理一个 readback heap buffer 的内存分配。
    /// </summary>
    public sealed class ReadbackFrameBuffer : IDisposable
    {
        private readonly RHIDevice _device;
        private readonly RHIResource _readbackBuffer;
        private readonly ulong _bufferSize;
        private readonly List<ReadbackAllocation> _allocations = new(16);
        private ulong _currentOffset;
        private ulong _frameAssigned;

        /// <summary>缓冲区大小</summary>
        public ulong BufferSize => _bufferSize;

        /// <summary>已用大小</summary>
        public ulong UsedSize => _currentOffset;

        /// <summary>分配数</summary>
        public int AllocationCount => _allocations.Count;

        public ReadbackFrameBuffer(RHIDevice device, ulong size)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));
            _bufferSize = size;
            _currentOffset = 0;

            var desc = new RHIBufferDesc
            {
                Size = size,
                Usage = RHIResourceUsage.Readback,
                Name = "ReadbackFrameBuffer",
            };
            _readbackBuffer = _device.CreateBuffer(desc);
        }

        /// <summary>
        /// 在缓冲区中分配空间。
        /// </summary>
        public ReadbackAllocation Allocate(ulong size, ulong alignment = 256)
        {
            ulong alignedOffset = RHICopyEngineUtil.AlignUp(_currentOffset, alignment);
            if (alignedOffset + size > _bufferSize)
            {
                return new ReadbackAllocation { IsValid = false };
            }

            var alloc = new ReadbackAllocation
            {
                IsValid = true,
                Buffer = _readbackBuffer,
                Offset = alignedOffset,
                Size = size,
            };

            _allocations.Add(new ReadbackAllocation
            {
                IsValid = true,
                Buffer = _readbackBuffer,
                Offset = alignedOffset,
                Size = size,
            });

            _currentOffset = alignedOffset + size;
            return alloc;
        }

        /// <summary>
        /// 获取已完成的分配（帧延迟已过）。
        /// </summary>
        public List<ReadbackAllocation> GetCompletedAllocations(ulong currentFrame)
        {
            // 简化版：假设所有分配都已完成
            // 实际应该检查 fence
            return _allocations;
        }

        /// <summary>清除已完成的分配记录。</summary>
        public void ClearCompleted()
        {
            _allocations.Clear();
            _currentOffset = 0;
        }

        /// <summary>获取状态字符串。</summary>
        public string GetStatus()
        {
            float usage = (float)_currentOffset / _bufferSize * 100f;
            return $"使用={_currentOutput()}/{_bufferSize} ({usage:F1}%), 分配数={_allocations.Count}";
        }

        private string _currentOutput()
        {
            if (_currentOffset < 1024) return $"{_currentOffset}B";
            if (_currentOffset < 1024 * 1024) return $"{_currentOffset / 1024.0:F0}KB";
            return $"{_currentOffset / (1024.0 * 1024):F1}MB";
        }

        public void Dispose()
        {
            _readbackBuffer?.Dispose();
            _allocations.Clear();
        }
    }

    /// <summary>回读分配</summary>
    public struct ReadbackAllocation
    {
        public bool IsValid;
        public RHIResource Buffer;
        public ulong Offset;
        public ulong Size;
        public long RequestId;
        public int Width, Height;
        public RHIFormat Format;
        public int RowPitch;
    }

    /// <summary>回读结果</summary>
    public sealed class ReadbackResult
    {
        public long RequestId;
        public RHIReadbackManager.ReadbackType Type;
        public RHIResource Buffer;
        public ulong Offset;
        public ulong Size;
        public IntPtr CpuAddress;
        public int Width, Height;
        public RHIFormat Format;
        public int RowPitch;
        public ulong CompleteFrame;

        /// <summary>获取数据的 Span</summary>
        public unsafe Span<byte> AsSpan()
        {
            if (CpuAddress == IntPtr.Zero)
                return Span<byte>.Empty;
            return new Span<byte>((void*)CpuAddress, (int)Size);
        }

        /// <summary>获取数据为 byte 数组</summary>
        public byte[] ToArray()
        {
            byte[] data = new byte[Size];
            Marshal.Copy(CpuAddress, data, 0, (int)Size);
            return data;
        }

        /// <summary>获取数据为泛型数组</summary>
        public T[] ToArray<T>() where T : struct
        {
            int elementSize = Unsafe.SizeOf<T>();
            int count = (int)(Size / (ulong)elementSize);
            T[] data = new T[count];

            unsafe
            {
                var srcSpan = new Span<byte>((void*)CpuAddress, (int)Size);
                var dstSpan = MemoryMarshal.Cast<T, byte>(data);
                srcSpan.Slice(0, dstSpan.Length).CopyTo(dstSpan);
            }

            return data;
        }

        public override string ToString()
        {
            return $"ReadbackResult#{RequestId}: size={Size}, " +
                   $"{(Width > 0 ? $"{Width}x{Height} " : "")}frame={CompleteFrame}";
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 统计
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>回读统计</summary>
    public class ReadbackStats
    {
        public int BufferReadbackCount;
        public int TextureReadbackCount;
        public int SubmittedCount;
        public int CompletedCount;
        public int ResultRetrievedCount;
        public int FailedCount;
        public int EvictedCount;
        public ulong TotalBytesRequested;
        public ulong TotalBytesReadback;

        public void Reset()
        {
            BufferReadbackCount = 0;
            TextureReadbackCount = 0;
            SubmittedCount = 0;
            CompletedCount = 0;
            ResultRetrievedCount = 0;
            FailedCount = 0;
            EvictedCount = 0;
            TotalBytesRequested = 0;
            TotalBytesReadback = 0;
        }

        public void Copy(ReadbackStats other)
        {
            BufferReadbackCount = other.BufferReadbackCount;
            TextureReadbackCount = other.TextureReadbackCount;
            SubmittedCount = other.SubmittedCount;
            CompletedCount = other.CompletedCount;
            ResultRetrievedCount = other.ResultRetrievedCount;
            FailedCount = other.FailedCount;
            EvictedCount = other.EvictedCount;
            TotalBytesRequested = other.TotalBytesRequested;
            TotalBytesReadback = other.TotalBytesReadback;
        }

        public override string ToString()
        {
            return $"Buffer={BufferReadbackCount}, Texture={TextureReadbackCount}, " +
                   $"Submitted={SubmittedCount}, Completed={CompletedCount}, " +
                   $"Retrieved={ResultRetrievedCount}, Failed={FailedCount}";
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 扩展方法
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// RHIReadbackManager 的扩展方法。
    /// </summary>
    public static class ReadbackManagerExtensions
    {
        /// <summary>同步回读 buffer 数据。阻塞直到完成。</summary>
        public static byte[] ReadbackBufferSync(this RHIReadbackManager manager,
                                                 RHIResource buffer, ulong offset, ulong size,
                                                 int timeoutMs = 1000)
        {
            long id = manager.RequestBufferReadback(buffer, offset, size);

            // 等待完成
            var sw = Stopwatch.StartNew();
            while (!manager.IsReady(id))
            {
                if (sw.ElapsedMilliseconds > timeoutMs)
                    return null;
                Thread.Sleep(1);
            }

            return manager.GetResultData(id);
        }

        /// <summary>同步回读纹理数据。阻塞直到完成。</summary>
        public static byte[] ReadbackTextureSync(this RHIReadbackManager manager,
                                                   RHIResource texture, int width, int height,
                                                   RHIFormat format, int timeoutMs = 1000)
        {
            long id = manager.RequestTextureReadback(texture, 0, 0, width, height, format);

            var sw = Stopwatch.StartNew();
            while (!manager.IsReady(id))
            {
                if (sw.ElapsedMilliseconds > timeoutMs)
                    return null;
                Thread.Sleep(1);
            }

            return manager.GetResultData(id);
        }
    }
}
