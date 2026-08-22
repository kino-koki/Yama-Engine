// ════════════════════════════════════════════════════════════════════════════
// 山雨引擎 - RHI 高效拷贝引擎 (RHICopyEngine)
//
// 负责各种资源拷贝操作：buffer-to-buffer, buffer-to-texture, texture-to-buffer,
// texture-to-texture, 以及批量拷贝。
//
// 关键设计:
//   * 使用专用的 copy queue (D3D12) 或 transfer queue (Vulkan) 实现异步拷贝
//   * 上传堆(Upload Heap)做中转，避免直接写 GPU 内存
//   * 支持批量拷贝，减少命令提交次数
//   * 内置环形缓冲区(Upload Ring Buffer)管理上传内存
//   * 支持纹理的 mip-by-mip 上传
//   * 自动屏障管理
//
// FIXME: 纹理上传的 footprint 计算在 D3D12 下很麻烦，先用简化版
// TODO: 加个 copy queue 的并行提交
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
    /// 高效拷贝引擎：管理资源上传、拷贝和批量传输。
    /// </summary>
    public sealed class RHICopyEngine : IDisposable
    {
        // ──────────────────────────────────────────────────────────────────────
        // 常量
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>默认上传堆大小 (256MB)</summary>
        public const ulong DefaultUploadBufferSize = 256 * 1024 * 1024;

        /// <summary>最大单次分配大小 (16MB)</summary>
        public const ulong MaxSingleAllocation = 16 * 1024 * 1024;

        /// <summary>对齐要求</summary>
        public const ulong BufferAlignment = 256;
        public const ulong TextureAlignment = 512;

        // ──────────────────────────────────────────────────────────────────────
        // 字段
        // ──────────────────────────────────────────────────────────────────────

        private readonly RHIDevice _device;
        private readonly RHIBarrierTracker _barrierTracker;
        private readonly UploadRingBuffer _ringBuffer;
        private readonly CopyCommandListPool _cmdListPool;
        private readonly List<CopyOperation> _pendingOps = new(64);
        private readonly CopyStats _stats;
        private readonly CopyStats _lastFrameStats;
        private ulong _currentFrame;
        private bool _disposed;

        /// <summary>是否使用专用的 copy queue</summary>
        public bool UseCopyQueue { get; set; } = true;

        /// <summary>是否自动插入屏障</summary>
        public bool AutoBarriers { get; set; } = true;

        /// <summary>是否批处理操作</summary>
        public bool BatchMode { get; set; } = true;

        /// <summary>统计信息</summary>
        public ref readonly CopyStats Stats => ref _stats;

        /// <summary>上一帧统计</summary>
        public ref readonly CopyStats LastFrameStats => ref _lastFrameStats;

        // ──────────────────────────────────────────────────────────────────────
        // 构造/析构
        // ──────────────────────────────────────────────────────────────────────

        public RHICopyEngine(RHIDevice device, RHIBarrierTracker barrierTracker = null,
                              ulong uploadBufferSize = DefaultUploadBufferSize)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));
            _barrierTracker = barrierTracker;
            _ringBuffer = new UploadRingBuffer(device, uploadBufferSize);
            _cmdListPool = new CopyCommandListPool(device);
            _stats = new CopyStats();
            _lastFrameStats = new CopyStats();
        }

        ~RHICopyEngine()
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
                _ringBuffer?.Dispose();
                _cmdListPool?.Dispose();
                _pendingOps.Clear();
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

            // 保存上一帧统计
            _lastFrameStats.Copy(_stats);
            _stats.Reset();

            // 更新环形缓冲区的帧
            _ringBuffer.BeginFrame(frameIndex);
        }

        /// <summary>帧结束，执行所有 pending 的拷贝操作。</summary>
        public void EndFrame()
        {
            if (_pendingOps.Count > 0)
            {
                FlushPendingOperations();
            }

            // 清理已完成的环形缓冲区分配
            _ringBuffer.EndFrame();
        }

        // ──────────────────────────────────────────────────────────────────────
        // Buffer 拷贝
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 把数据从 CPU 内存上传到 GPU buffer。
        /// </summary>
        /// <param name="dstBuffer">目标 GPU buffer</param>
        /// <param name="dstOffset">目标偏移</param>
        /// <param name="srcData">源数据</param>
        /// <param name="srcSize">数据大小</param>
        public void UploadToBuffer(RHIResource dstBuffer, ulong dstOffset,
                                    ReadOnlySpan<byte> srcData, int srcSize = -1)
        {
            if (srcSize < 0) srcSize = srcData.Length;
            if (srcSize == 0) return;

            _stats.TotalBytesUploaded += (ulong)srcSize;
            _stats.BufferUploadCount++;

            if (BatchMode)
            {
                // 加入 pending 队列
                var uploadAlloc = _ringBuffer.Allocate((ulong)srcSize, BufferAlignment);
                srcData.Slice(0, srcSize).CopyTo(uploadAlloc.Span);

                _pendingOps.Add(new CopyOperation
                {
                    Type = CopyOpType.BufferUpload,
                    DstResource = dstBuffer,
                    DstOffset = dstOffset,
                    UploadBuffer = uploadAlloc.Buffer,
                    UploadOffset = uploadAlloc.Offset,
                    Size = (ulong)srcSize,
                });
            }
            else
            {
                // 立即执行
                var uploadAlloc = _ringBuffer.Allocate((ulong)srcSize, BufferAlignment);
                srcData.Slice(0, srcSize).CopyTo(uploadAlloc.Span);

                var cmdList = _cmdListPool.Acquire();
                cmdList.Open();

                if (AutoBarriers && _barrierTracker != null)
                {
                    _barrierTracker.TransitionTo(dstBuffer, RHIResourceState.CopyDest);
                    _barrierTracker.Flush(cmdList);
                }

                cmdList.CopyBufferRegion(uploadAlloc.Buffer, uploadAlloc.Offset,
                                          dstBuffer, dstOffset, (ulong)srcSize);

                cmdList.Close();
                cmdList.Execute();

                _stats.TotalCopyCommands++;
            }
        }

        /// <summary>
        /// 上传结构化数据到 buffer（泛型版本）。
        /// </summary>
        public void UploadToBuffer<T>(RHIResource dstBuffer, ulong dstOffset,
                                       ReadOnlySpan<T> srcData) where T : struct
        {
            int byteSize = srcData.Length * Unsafe.SizeOf<T>();
            ReadOnlySpan<byte> byteSpan = MemoryMarshal.AsBytes(srcData);
            UploadToBuffer(dstBuffer, dstOffset, byteSpan, byteSize);
        }

        /// <summary>
        /// 上传单个结构体到 buffer。
        /// </summary>
        public void UploadToBuffer<T>(RHIResource dstBuffer, ulong dstOffset, in T data) where T : struct
        {
            int size = Unsafe.SizeOf<T>();
            // 用 stackalloc 太大不安全，用临时数组
            Span<byte> bytes = stackalloc byte[size];
            MemoryMarshal.Write(bytes, in data);
            UploadToBuffer(dstBuffer, dstOffset, bytes, size);
        }

        /// <summary>
        /// buffer 到 buffer 的拷贝。
        /// </summary>
        public void CopyBufferToBuffer(RHIResource srcBuffer, ulong srcOffset,
                                        RHIResource dstBuffer, ulong dstOffset,
                                        ulong size)
        {
            if (size == 0) return;

            _stats.BufferToBufferCount++;
            _stats.TotalBytesCopied += size;

            if (BatchMode)
            {
                _pendingOps.Add(new CopyOperation
                {
                    Type = CopyOpType.BufferToBuffer,
                    SrcResource = srcBuffer,
                    SrcOffset = srcOffset,
                    DstResource = dstBuffer,
                    DstOffset = dstOffset,
                    Size = size,
                });
            }
            else
            {
                var cmdList = _cmdListPool.Acquire();
                cmdList.Open();

                if (AutoBarriers && _barrierTracker != null)
                {
                    _barrierTracker.BeginBatch();
                    _barrierTracker.TransitionTo(srcBuffer, RHIResourceState.CopySource);
                    _barrierTracker.TransitionTo(dstBuffer, RHIResourceState.CopyDest);
                    _barrierTracker.EndBatch(cmdList);
                }

                cmdList.CopyBufferRegion(srcBuffer, srcOffset, dstBuffer, dstOffset, size);
                cmdList.Close();
                cmdList.Execute();
                _stats.TotalCopyCommands++;
            }
        }

        /// <summary>
        /// 整个 buffer 拷贝。
        /// </summary>
        public void CopyBuffer(RHIResource srcBuffer, RHIResource dstBuffer)
        {
            // 需要知道 buffer 大小，从 resource 获取
            ulong size = srcBuffer?.Size ?? 0;
            CopyBufferToBuffer(srcBuffer, 0, dstBuffer, 0, size);
        }

        // ──────────────────────────────────────────────────────────────────────
        // Texture 拷贝
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 把数据从 CPU 内存上传到 GPU 纹理。
        /// 需要计算纹理的 footprint (行间距、切片间距等)。
        /// </summary>
        public void UploadToTexture(RHIResource dstTexture, int dstMip, int dstArraySlice,
                                     ReadOnlySpan<byte> srcData,
                                     int width, int height, RHIFormat format,
                                     int rowPitch = 0)
        {
            if (srcData.IsEmpty) return;

            int bytesPerPixel = RHIFormatUtils.BytesPerPixel(format);
            if (rowPitch <= 0)
            {
                // 默认行间距对齐到 256 字节 (D3D12 要求)
                rowPitch = AlignUp(width * bytesPerPixel, 256);
            }

            int slicePitch = rowPitch * height;
            _stats.TotalBytesUploaded += (ulong)srcData.Length;
            _stats.TextureUploadCount++;

            if (BatchMode)
            {
                var uploadAlloc = _ringBuffer.Allocate((ulong)slicePitch, TextureAlignment);
                srcData.CopyTo(uploadAlloc.Span);

                _pendingOps.Add(new CopyOperation
                {
                    Type = CopyOpType.TextureUpload,
                    DstResource = dstTexture,
                    DstMip = dstMip,
                    DstArraySlice = dstArraySlice,
                    UploadBuffer = uploadAlloc.Buffer,
                    UploadOffset = uploadAlloc.Offset,
                    Width = width,
                    Height = height,
                    Format = format,
                    RowPitch = rowPitch,
                    SlicePitch = slicePitch,
                });
            }
            else
            {
                var uploadAlloc = _ringBuffer.Allocate((ulong)slicePitch, TextureAlignment);
                srcData.CopyTo(uploadAlloc.Span);

                var cmdList = _cmdListPool.Acquire();
                cmdList.Open();

                if (AutoBarriers && _barrierTracker != null)
                {
                    _barrierTracker.TransitionTo(dstTexture, RHIResourceState.CopyDest);
                    _barrierTracker.Flush(cmdList);
                }

                // 构建放置信息
                var placedFootprint = new TextureCopyFootprint
                {
                    Offset = uploadAlloc.Offset,
                    RowPitch = (ulong)rowPitch,
                    Width = width,
                    Height = height,
                    Depth = 1,
                    Format = format,
                };

                cmdList.CopyBufferToTexture(uploadAlloc.Buffer, placedFootprint,
                                              dstTexture, dstMip, dstArraySlice,
                                              width, height);

                cmdList.Close();
                cmdList.Execute();
                _stats.TotalCopyCommands++;
            }
        }

        /// <summary>
        /// 上传完整纹理（所有 mip level）。
        /// </summary>
        public void UploadTextureFull(RHIResource dstTexture,
                                       ReadOnlySpan<byte> srcData,
                                       int width, int height, int mipLevels,
                                       RHIFormat format)
        {
            int offset = 0;
            int mipWidth = width;
            int mipHeight = height;

            for (int mip = 0; mip < mipLevels; mip++)
            {
                int bytesPerPixel = RHIFormatUtils.BytesPerPixel(format);
                int rowPitch = AlignUp(mipWidth * bytesPerPixel, 256);
                int slicePitch = rowPitch * mipHeight;

                if (offset + slicePitch > srcData.Length)
                {
                    // 数据不够
                    Debug.WriteLine($"[RHICopyEngine] 纹理数据不足: mip={mip}, " +
                                   $"需要 {slicePitch} 字节但只剩 {srcData.Length - offset}");
                    break;
                }

                UploadToTexture(dstTexture, mip, 0, srcData.Slice(offset, slicePitch),
                                 mipWidth, mipHeight, format, rowPitch);

                offset += slicePitch;
                mipWidth = Math.Max(1, mipWidth / 2);
                mipHeight = Math.Max(1, mipHeight / 2);
            }
        }

        /// <summary>
        /// 纹理到纹理的拷贝。
        /// </summary>
        public void CopyTextureToTexture(RHIResource srcTexture, int srcMip, int srcArray,
                                          RHIResource dstTexture, int dstMip, int dstArray,
                                          int width, int height)
        {
            _stats.TextureToTextureCount++;
            _stats.TotalBytesCopied += (ulong)(width * height * 4); // 估算

            if (BatchMode)
            {
                _pendingOps.Add(new CopyOperation
                {
                    Type = CopyOpType.TextureToTexture,
                    SrcResource = srcTexture,
                    SrcMip = srcMip,
                    SrcArraySlice = srcArray,
                    DstResource = dstTexture,
                    DstMip = dstMip,
                    DstArraySlice = dstArray,
                    Width = width,
                    Height = height,
                });
            }
            else
            {
                var cmdList = _cmdListPool.Acquire();
                cmdList.Open();

                if (AutoBarriers && _barrierTracker != null)
                {
                    _barrierTracker.BeginBatch();
                    _barrierTracker.TransitionTo(srcTexture, RHIResourceState.CopySource);
                    _barrierTracker.TransitionTo(dstTexture, RHIResourceState.CopyDest);
                    _barrierTracker.EndBatch(cmdList);
                }

                cmdList.CopyTextureToTexture(srcTexture, srcMip, srcArray,
                                              dstTexture, dstMip, dstArray,
                                              width, height);
                cmdList.Close();
                cmdList.Execute();
                _stats.TotalCopyCommands++;
            }
        }

        /// <summary>
        /// 纹理到 buffer 的拷贝（回读用）。
        /// </summary>
        public void CopyTextureToBuffer(RHIResource srcTexture, int srcMip, int srcArray,
                                         RHIResource dstBuffer, ulong dstOffset,
                                         int width, int height, RHIFormat format)
        {
            int bytesPerPixel = RHIFormatUtils.BytesPerPixel(format);
            int rowPitch = AlignUp(width * bytesPerPixel, 256);
            int slicePitch = rowPitch * height;

            _stats.TextureToBufferCount++;
            _stats.TotalBytesCopied += (ulong)slicePitch;

            var cmdList = _cmdListPool.Acquire();
            cmdList.Open();

            if (AutoBarriers && _barrierTracker != null)
            {
                _barrierTracker.BeginBatch();
                _barrierTracker.TransitionTo(srcTexture, RHIResourceState.CopySource);
                _barrierTracker.TransitionTo(dstBuffer, RHIResourceState.CopyDest);
                _barrierTracker.EndBatch(cmdList);
            }

            var footprint = new TextureCopyFootprint
            {
                Offset = dstOffset,
                RowPitch = (ulong)rowPitch,
                Width = width,
                Height = height,
                Depth = 1,
                Format = format,
            };

            cmdList.CopyTextureToBuffer(srcTexture, srcMip, srcArray,
                                          dstBuffer, footprint, width, height);
            cmdList.Close();
            cmdList.Execute();
            _stats.TotalCopyCommands++;
        }

        // ──────────────────────────────────────────────────────────────────────
        // 批量操作
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 批量上传 buffer 数据。
        /// </summary>
        public void BatchUploadBuffers(params (RHIResource buffer, ulong offset, ReadOnlySpan<byte> data)[] uploads)
        {
            foreach (var (buffer, offset, data) in uploads)
            {
                UploadToBuffer(buffer, offset, data);
            }
        }

        /// <summary>
        /// 批量 buffer 拷贝。
        /// </summary>
        public void BatchCopyBuffers(params (RHIResource src, RHIResource dst, ulong size)[] copies)
        {
            foreach (var (src, dst, size) in copies)
            {
                CopyBufferToBuffer(src, 0, dst, 0, size);
            }
        }

        /// <summary>
        /// 刷新所有 pending 操作。
        /// </summary>
        public void Flush()
        {
            if (_pendingOps.Count == 0) return;
            FlushPendingOperations();
        }

        /// <summary>
        /// 执行所有 pending 的拷贝操作。
        /// </summary>
        private void FlushPendingOperations()
        {
            if (_pendingOps.Count == 0) return;

            var cmdList = _cmdListPool.Acquire();
            cmdList.Open();

            // 先收集所有需要的屏障
            if (AutoBarriers && _barrierTracker != null)
            {
                _barrierTracker.BeginBatch();
                foreach (var op in _pendingOps)
                {
                    switch (op.Type)
                    {
                        case CopyOpType.BufferUpload:
                        case CopyOpType.TextureUpload:
                            _barrierTracker.TransitionTo(op.DstResource, RHIResourceState.CopyDest);
                            break;
                        case CopyOpType.BufferToBuffer:
                            _barrierTracker.TransitionTo(op.SrcResource, RHIResourceState.CopySource);
                            _barrierTracker.TransitionTo(op.DstResource, RHIResourceState.CopyDest);
                            break;
                        case CopyOpType.TextureToTexture:
                            _barrierTracker.TransitionTo(op.SrcResource, RHIResourceState.CopySource);
                            _barrierTracker.TransitionTo(op.DstResource, RHIResourceState.CopyDest);
                            break;
                        case CopyOpType.TextureToBuffer:
                            _barrierTracker.TransitionTo(op.SrcResource, RHIResourceState.CopySource);
                            _barrierTracker.TransitionTo(op.DstResource, RHIResourceState.CopyDest);
                            break;
                    }
                }
                _barrierTracker.EndBatch(cmdList);
            }

            // 执行拷贝
            foreach (var op in _pendingOps)
            {
                switch (op.Type)
                {
                    case CopyOpType.BufferUpload:
                        cmdList.CopyBufferRegion(op.UploadBuffer, op.UploadOffset,
                                                  op.DstResource, op.DstOffset, op.Size);
                        _stats.TotalCopyCommands++;
                        break;

                    case CopyOpType.BufferToBuffer:
                        cmdList.CopyBufferRegion(op.SrcResource, op.SrcOffset,
                                                  op.DstResource, op.DstOffset, op.Size);
                        _stats.TotalCopyCommands++;
                        break;

                    case CopyOpType.TextureUpload:
                        var footprint = new TextureCopyFootprint
                        {
                            Offset = op.UploadOffset,
                            RowPitch = (ulong)op.RowPitch,
                            Width = op.Width,
                            Height = op.Height,
                            Depth = 1,
                            Format = op.Format,
                        };
                        cmdList.CopyBufferToTexture(op.UploadBuffer, footprint,
                                                     op.DstResource, op.DstMip, op.DstArraySlice,
                                                     op.Width, op.Height);
                        _stats.TotalCopyCommands++;
                        break;

                    case CopyOpType.TextureToTexture:
                        cmdList.CopyTextureToTexture(op.SrcResource, op.SrcMip, op.SrcArray,
                                                      op.DstResource, op.DstMip, op.DstArraySlice,
                                                      op.Width, op.Height);
                        _stats.TotalCopyCommands++;
                        break;

                    case CopyOpType.TextureToBuffer:
                        var readbackFootprint = new TextureCopyFootprint
                        {
                            Offset = op.UploadOffset,
                            RowPitch = (ulong)op.RowPitch,
                            Width = op.Width,
                            Height = op.Height,
                            Depth = 1,
                            Format = op.Format,
                        };
                        cmdList.CopyTextureToBuffer(op.SrcResource, op.SrcMip, op.SrcArray,
                                                       op.DstResource, readbackFootprint, op.Width, op.Height);
                        _stats.TotalCopyCommands++;
                        break;
                }
            }

            // 后续屏障：把资源从 CopyDest 转回常用状态
            if (AutoBarriers && _barrierTracker != null)
            {
                _barrierTracker.BeginBatch();
                foreach (var op in _pendingOps)
                {
                    if (op.Type == CopyOpType.BufferUpload || op.Type == CopyOpType.TextureUpload ||
                        op.Type == CopyOpType.BufferToBuffer || op.Type == CopyOpType.TextureToTexture)
                    {
                        _barrierTracker.TransitionTo(op.DstResource, RHIResourceState.Common);
                    }
                    if (op.Type == CopyOpType.BufferToBuffer || op.Type == CopyOpType.TextureToTexture ||
                        op.Type == CopyOpType.TextureToBuffer)
                    {
                        _barrierTracker.TransitionTo(op.SrcResource, RHIResourceState.Common);
                    }
                }
                _barrierTracker.EndBatch(cmdList);
            }

            cmdList.Close();
            cmdList.Execute();

            _stats.BatchCount++;
            _stats.PeakPendingOps = Math.Max(_stats.PeakPendingOps, _pendingOps.Count);
            _pendingOps.Clear();
        }

        // ──────────────────────────────────────────────────────────────────────
        // 辅助方法
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>对齐到指定边界</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong AlignUp(ulong value, ulong alignment)
        {
            return (value + alignment - 1) & ~(alignment - 1);
        }

        /// <summary>对齐到指定边界 (int 版本)</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int AlignUp(int value, int alignment)
        {
            return (value + alignment - 1) & ~(alignment - 1);
        }

        /// <summary>
        /// 计算纹理上传需要的缓冲区大小。
        /// </summary>
        public static ulong CalculateTextureUploadSize(int width, int height, RHIFormat format,
                                                        int alignment = 256)
        {
            int bytesPerPixel = RHIFormatUtils.BytesPerPixel(format);
            int rowPitch = AlignUp(width * bytesPerPixel, alignment);
            return (ulong)(rowPitch * height);
        }

        /// <summary>
        /// 生成诊断报告。
        /// </summary>
        public string GenerateReport()
        {
            var sb = new StringBuilder();
            sb.AppendLine("═══ RHICopyEngine 诊断报告 ═══");
            sb.AppendLine();
            sb.AppendLine("── 本帧统计 ──");
            sb.AppendLine($"  Buffer 上传: {_stats.BufferUploadCount}");
            sb.AppendLine($"  Texture 上传: {_stats.TextureUploadCount}");
            sb.AppendLine($"  Buffer->Buffer: {_stats.BufferToBufferCount}");
            sb.AppendLine($"  Texture->Texture: {_stats.TextureToTextureCount}");
            sb.AppendLine($"  Texture->Buffer: {_stats.TextureToBufferCount}");
            sb.AppendLine($"  总拷贝命令: {_stats.TotalCopyCommands}");
            sb.AppendLine($"  批处理次数: {_stats.BatchCount}");
            sb.AppendLine($"  峰值 pending: {_stats.PeakPendingOps}");
            sb.AppendLine($"  上传字节: {FormatBytes(_stats.TotalBytesUploaded)}");
            sb.AppendLine($"  拷贝字节: {FormatBytes(_stats.TotalBytesCopied)}");
            sb.AppendLine();
            sb.AppendLine("── 上一帧统计 ──");
            sb.AppendLine($"  Buffer 上传: {_lastFrameStats.BufferUploadCount}");
            sb.AppendLine($"  Texture 上传: {_lastFrameStats.TextureUploadCount}");
            sb.AppendLine($"  总拷贝命令: {_lastFrameStats.TotalCopyCommands}");
            sb.AppendLine($"  上传字节: {FormatBytes(_lastFrameStats.TotalBytesUploaded)}");
            sb.AppendLine();
            sb.AppendLine("── 上传环形缓冲区 ──");
            sb.AppendLine($"  {_ringBuffer.GetStatus()}");
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

        /// <summary>拷贝操作类型</summary>
        private enum CopyOpType
        {
            BufferUpload,
            TextureUpload,
            BufferToBuffer,
            TextureToTexture,
            TextureToBuffer,
        }

        /// <summary>拷贝操作</summary>
        private struct CopyOperation
        {
            public CopyOpType Type;
            public RHIResource SrcResource;
            public RHIResource DstResource;
            public RHIResource UploadBuffer;
            public ulong SrcOffset;
            public ulong DstOffset;
            public ulong UploadOffset;
            public ulong Size;
            public int SrcMip, SrcArraySlice;
            public int DstMip, DstArraySlice;
            public int Width, Height;
            public RHIFormat Format;
            public int RowPitch;
            public int SlicePitch;
        }

        /// <summary>纹理拷贝 footprint</summary>
        public struct TextureCopyFootprint
        {
            public ulong Offset;
            public ulong RowPitch;
            public int Width;
            public int Height;
            public int Depth;
            public RHIFormat Format;
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 上传环形缓冲区
    // 管理上传堆内存，实现环形分配以避免频繁创建/销毁
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 上传环形缓冲区：管理上传堆的内存分配。
    /// 使用环形缓冲区避免频繁分配释放上传 buffer。
    /// </summary>
    public sealed class UploadRingBuffer : IDisposable
    {
        private readonly RHIDevice _device;
        private readonly RHIResource _uploadBuffer;
        private readonly ulong _bufferSize;
        private readonly object _lock = new();

        // 环形缓冲区的头尾指针
        private ulong _head; // 下一次分配的位置
        private ulong _tail; // 最早未完成的分配位置

        // 每帧的分配记录，用于在帧结束时回收
        private readonly Dictionary<ulong, List<(ulong offset, ulong size)>> _frameAllocations = new();

        // 统计
        private ulong _totalAllocated;
        private ulong _totalFreed;
        private ulong _peakUsage;
        private ulong _currentUsage;
        private int _allocationCount;

        /// <summary>缓冲区大小</summary>
        public ulong BufferSize => _bufferSize;

        /// <summary>当前使用量</summary>
        public ulong CurrentUsage => _currentUsage;

        /// <summary>峰值使用量</summary>
        public ulong PeakUsage => _peakUsage;

        /// <summary>分配次数</summary>
        public int AllocationCount => _allocationCount;

        /// <summary>上传 buffer</summary>
        public RHIResource Buffer => _uploadBuffer;

        public UploadRingBuffer(RHIDevice device, ulong size)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));
            _bufferSize = size;
            _head = 0;
            _tail = 0;

            // 创建上传 buffer
            var desc = new RHIBufferDesc
            {
                Size = size,
                Usage = RHIResourceUsage.Upload,
                Name = "UploadRingBuffer",
            };
            _uploadBuffer = _device.CreateBuffer(desc);
        }

        /// <summary>
        /// 分配一块上传内存。
        /// </summary>
        public UploadAllocation Allocate(ulong size, ulong alignment = 256)
        {
            if (size > _bufferSize)
                throw new InvalidOperationException($"请求大小 {size} 超过缓冲区大小 {_bufferSize}");

            lock (_lock)
            {
                // 对齐 head
                ulong alignedHead = RHICopyEngineUtil.AlignUp(_head, alignment);
                ulong endPos = alignedHead + size;

                // 检查是否需要回绕
                if (endPos > _bufferSize)
                {
                    // 回绕到开头
                    _head = 0;
                    alignedHead = 0;
                    endPos = size;

                    if (_tail <= endPos)
                    {
                        // 没有空间，需要等待
                        // FIXME: 应该阻塞或扩容，现在先抛异常
                        throw new InvalidOperationException("上传缓冲区空间不足 (wrap-around)");
                    }
                }
                else if (alignedHead >= _tail && alignedHead < _tail && _tail > 0)
                {
                    // head 追上了 tail，空间不足
                    throw new InvalidOperationException("上传缓冲区空间不足");
                }

                var allocation = new UploadAllocation
                {
                    Buffer = _uploadBuffer,
                    Offset = alignedHead,
                    Size = size,
                    Frame = _currentFrame,
                };

                // 获取 CPU 可写指针
                allocation.CpuAddress = _device.MapBuffer(_uploadBuffer, alignedHead, size);

                _head = endPos;
                _currentUsage += size;
                _totalAllocated += size;
                _allocationCount++;

                if (_currentUsage > _peakUsage)
                    _peakUsage = _currentUsage;

                // 记录到当前帧的分配列表
                if (!_frameAllocations.ContainsKey(_currentFrame))
                    _frameAllocations[_currentFrame] = new List<(ulong, ulong)>();
                _frameAllocations[_currentFrame].Add((alignedHead, size));

                return allocation;
            }
        }

        private ulong _currentFrame;

        /// <summary>帧开始。</summary>
        public void BeginFrame(ulong frame)
        {
            _currentFrame = frame;
        }

        /// <summary>帧结束，回收旧帧的内存。</summary>
        public void EndFrame()
        {
            // 回收 2 帧之前的分配（假设 GPU 最多滞后 2 帧）
            ulong minFrame = _currentFrame > 2 ? _currentFrame - 2 : 0;

            lock (_lock)
            {
                var toRemove = new List<ulong>();
                foreach (var kv in _frameAllocations)
                {
                    if (kv.Key < minFrame)
                    {
                        foreach (var (offset, size) in kv.Value)
                        {
                            _totalFreed += size;
                            _currentUsage -= size;
                        }
                        toRemove.Add(kv.Key);
                    }
                }
                foreach (var f in toRemove)
                    _frameAllocations.Remove(f);

                // 更新 tail
                if (_frameAllocations.Count > 0)
                {
                    ulong minKey = ulong.MaxValue;
                    foreach (var k in _frameAllocations.Keys)
                        if (k < minKey) minKey = k;
                    if (_frameAllocations.TryGetValue(minKey, out var list) && list.Count > 0)
                    {
                        _tail = list[0].offset;
                    }
                }
                else
                {
                    _tail = _head;
                }
            }
        }

        /// <summary>获取状态字符串。</summary>
        public string GetStatus()
        {
            float usagePercent = (float)_currentUsage / _bufferSize * 100f;
            return $"大小={RHICopyEngineUtil.FormatBytesInternal(_bufferSize)}, " +
                   $"使用={RHICopyEngineUtil.FormatBytesInternal(_currentUsage)} ({usagePercent:F1}%), " +
                   $"峰值={RHICopyEngineUtil.FormatBytesInternal(_peakUsage)}, " +
                   $"分配次数={_allocationCount}";
        }

        public void Dispose()
        {
            _uploadBuffer?.Dispose();
            _frameAllocations.Clear();
        }
    }

    /// <summary>上传内存分配</summary>
    public struct UploadAllocation : IDisposable
    {
        public RHIResource Buffer;
        public ulong Offset;
        public ulong Size;
        public ulong Frame;
        public IntPtr CpuAddress;

        /// <summary>获取可写的 Span</summary>
        public unsafe Span<byte> Span
        {
            get
            {
                if (CpuAddress == IntPtr.Zero)
                    throw new InvalidOperationException("CPU 地址为空");
                return new Span<byte>((void*)CpuAddress, (int)Size);
            }
        }

        public void Dispose()
        {
            // Unmap 由 buffer 统一管理，这里不做
            CpuAddress = IntPtr.Zero;
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // Copy 命令列表池
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Copy 命令列表池：复用命令列表减少创建开销。
    /// </summary>
    public sealed class CopyCommandListPool : IDisposable
    {
        private readonly RHIDevice _device;
        private readonly Queue<CopyCommandList> _pool = new();
        private readonly object _lock = new();
        private int _totalCreated;
        private int _peakPoolSize;

        /// <summary>总创建数</summary>
        public int TotalCreated => _totalCreated;

        /// <summary>当前池大小</summary>
        public int PoolSize => _pool.Count;

        /// <summary>峰值池大小</summary>
        public int PeakPoolSize => _peakPoolSize;

        public CopyCommandListPool(RHIDevice device)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));
        }

        /// <summary>从池中获取一个命令列表。</summary>
        public CopyCommandList Acquire()
        {
            lock (_lock)
            {
                if (_pool.Count > 0)
                {
                    return _pool.Dequeue();
                }
            }
            _totalCreated++;
            return new CopyCommandList(_device);
        }

        /// <summary>归还命令列表到池中。</summary>
        public void Return(CopyCommandList cmdList)
        {
            if (cmdList == null) return;
            lock (_lock)
            {
                _pool.Enqueue(cmdList);
                if (_pool.Count > _peakPoolSize)
                    _peakPoolSize = _pool.Count;
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                while (_pool.Count > 0)
                {
                    var cmd = _pool.Dequeue();
                    cmd.Dispose();
                }
            }
        }
    }

    /// <summary>Copy 命令列表封装</summary>
    public sealed class CopyCommandList : IDisposable
    {
        private readonly RHIDevice _device;
        private IRHICommandList _inner;
        private bool _isOpen;
        private bool _disposed;

        public IRHICommandList Inner => _inner;
        public bool IsOpen => _isOpen;

        public CopyCommandList(RHIDevice device)
        {
            _device = device;
            _inner = _device.CreateCommandList(RHIQueueType.Copy);
        }

        public void Open()
        {
            if (_isOpen) throw new InvalidOperationException("命令列表已打开");
            _inner.Begin();
            _isOpen = true;
        }

        public void Close()
        {
            if (!_isOpen) throw new InvalidOperationException("命令列表未打开");
            _inner.End();
            _isOpen = false;
        }

        public void Execute()
        {
            _device.ExecuteCommandList(_inner, RHIQueueType.Copy);
        }

        public void CopyBufferRegion(RHIResource src, ulong srcOffset,
                                      RHIResource dst, ulong dstOffset, ulong size)
        {
            _inner.CopyBufferRegion(src, srcOffset, dst, dstOffset, size);
        }

        public void CopyBufferToTexture(RHIResource srcBuffer, RHICopyEngineUtil.TextureCopyFootprint footprint,
                                         RHIResource dstTexture, int dstMip, int dstArray,
                                         int width, int height)
        {
            _inner.CopyBufferToTexture(srcBuffer, footprint.Offset, footprint.RowPitch,
                                         dstTexture, dstMip, dstArray, width, height, footprint.Format);
        }

        public void CopyTextureToTexture(RHIResource src, int srcMip, int srcArray,
                                          RHIResource dst, int dstMip, int dstArray,
                                          int width, int height)
        {
            _inner.CopyTextureToTexture(src, srcMip, srcArray, dst, dstMip, dstArray, width, height);
        }

        public void CopyTextureToBuffer(RHIResource src, int srcMip, int srcArray,
                                         RHIResource dstBuffer, RHICopyEngineUtil.TextureCopyFootprint footprint,
                                         int width, int height)
        {
            _inner.CopyTextureToBuffer(src, srcMip, srcArray, dstBuffer, footprint.Offset,
                                          footprint.RowPitch, width, height, footprint.Format);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _inner?.Dispose();
            _disposed = true;
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 统计
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>拷贝统计</summary>
    public class CopyStats
    {
        public int BufferUploadCount;
        public int TextureUploadCount;
        public int BufferToBufferCount;
        public int TextureToTextureCount;
        public int TextureToBufferCount;
        public int TotalCopyCommands;
        public int BatchCount;
        public int PeakPendingOps;
        public ulong TotalBytesUploaded;
        public ulong TotalBytesCopied;

        public void Reset()
        {
            BufferUploadCount = 0;
            TextureUploadCount = 0;
            BufferToBufferCount = 0;
            TextureToTextureCount = 0;
            TextureToBufferCount = 0;
            TotalCopyCommands = 0;
            BatchCount = 0;
            PeakPendingOps = 0;
            TotalBytesUploaded = 0;
            TotalBytesCopied = 0;
        }

        public void Copy(CopyStats other)
        {
            BufferUploadCount = other.BufferUploadCount;
            TextureUploadCount = other.TextureUploadCount;
            BufferToBufferCount = other.BufferToBufferCount;
            TextureToTextureCount = other.TextureToTextureCount;
            TextureToBufferCount = other.TextureToBufferCount;
            TotalCopyCommands = other.TotalCopyCommands;
            BatchCount = other.BatchCount;
            PeakPendingOps = other.PeakPendingOps;
            TotalBytesUploaded = other.TotalBytesUploaded;
            TotalBytesCopied = other.TotalBytesCopied;
        }

        public override string ToString()
        {
            return $"Uploads: buf={BufferUploadCount} tex={TextureUploadCount}, " +
                   $"Copies: b2b={BufferToBufferCount} t2t={TextureToTextureCount} t2b={TextureToBufferCount}, " +
                   $"Cmds={TotalCopyCommands}, Batches={BatchCount}";
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 扩展方法
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// RHICopyEngine 的扩展方法。
    /// </summary>
    public static class CopyEngineExtensions
    {
        /// <summary>上传顶点缓冲。</summary>
        public static void UploadVertexBuffer<T>(this RHICopyEngine engine, RHIResource buffer,
                                                  ReadOnlySpan<T> vertices) where T : struct
        {
            engine.UploadToBuffer(buffer, 0, vertices);
        }

        /// <summary>上传索引缓冲。</summary>
        public static void UploadIndexBuffer(this RHICopyEngine engine, RHIResource buffer,
                                              ReadOnlySpan<uint> indices)
        {
            engine.UploadToBuffer(buffer, 0, indices);
        }

        /// <summary>上传常量缓冲数据。</summary>
        public static void UploadConstantBuffer<T>(this RHICopyEngine engine, RHIResource buffer,
                                                    in T data, ulong offset = 0) where T : struct
        {
            engine.UploadToBuffer(buffer, offset, in data);
        }
    }

    /// <summary>
    /// RHICopyEngine 内部的格式化辅助（被 UploadRingBuffer 使用）。
    /// </summary>
    public static class RHICopyEngineInternal
    {
        public static string FormatBytesInternal(ulong bytes) => RHICopyEngineUtil.FormatBytesStatic(bytes);
    }

    /// <summary>
    /// RHICopyEngine 的静态辅助方法。
    /// </summary>
    public static class RHICopyEngineUtil
    {
        public static string FormatBytesStatic(ulong bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
            return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
        }

        public static string FormatBytes(ulong bytes) => FormatBytesStatic(bytes);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong AlignUp(ulong value, ulong alignment)
        {
            return (value + alignment - 1) & ~(alignment - 1);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int AlignUp(int value, int alignment)
        {
            return (value + alignment - 1) & ~(alignment - 1);
        }

        public static ulong CalculateTextureUploadSize(int width, int height, RHIFormat format,
                                                        int alignment = 256)
        {
            int bytesPerPixel = RHIFormatUtils.BytesPerPixel(format);
            int rowPitch = AlignUp(width * bytesPerPixel, alignment);
            return (ulong)(rowPitch * height);
        }

        public struct TextureCopyFootprint
        {
            public ulong Offset;
            public ulong RowPitch;
            public int Width;
            public int Height;
            public int Depth;
            public RHIFormat Format;
        }
    }
}
