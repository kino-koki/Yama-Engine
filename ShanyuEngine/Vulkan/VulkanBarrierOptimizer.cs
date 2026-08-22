// =============================================================================
// VulkanBarrierOptimizer.cs - 山雨引擎 Vulkan 屏障优化器
// =============================================================================
// vkCmdPipelineBarrier 是个性能杀手，几个常见问题：
//   1. 屏障太多：每次写完贴图就插一个，应该批量
//   2. 屏障太重：srcStageMask=ALL_COMMANDS 把整个管线 stall 掉
//   3. 没必要的 queue family transfer
//   4. layout transition 重复
//
// 这套优化器干几件事：
//   1. 收集一帧内的所有 barrier 需求，合并到一次 vkCmdPipelineBarrier
//   2. 自动选择最窄的 src/dst stage mask
//   3. 检测冗余 barrier (同一个 image 已经是目标 layout)
//   4. 跟踪 resource 的当前状态 (layout/queue/access)
//
// FIXME: 还没处理 image 的 subresource range 细化
// TODO: 加 event 替代 barrier (细粒度同步)
//
// 作者: 山雨引擎团队
// =============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;

using Buffer = Silk.NET.Vulkan.Buffer;
using Semaphore = Vulkan.Semaphore;
using Vulkan = Silk.NET.Vulkan;
namespace ShanyuEngine.Vulkan
{
    // ════════════════════════════════════════════════════════════════════════════
    // 区域 1: 公共类型
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>资源当前状态。</summary>
    public struct ResourceState
    {
        public ImageLayout Layout;
        public AccessFlags LastAccess;
        public PipelineStageFlags LastStage;
        public uint QueueFamilyIndex;
        public bool IsInitialized;
    }

    /// <summary>屏障请求 (buffer)。</summary>
    public struct BufferBarrierRequest
    {
        public Buffer Buffer;
        public ulong Offset;
        public ulong Size;
        public AccessFlags SrcAccess;
        public AccessFlags DstAccess;
        public PipelineStageFlags SrcStage;
        public PipelineStageFlags DstStage;
        public uint SrcQueueFamily;
        public uint DstQueueFamily;
    }

    /// <summary>屏障请求 (image)。</summary>
    public struct ImageBarrierRequest
    {
        public Image Image;
        public ImageSubresourceRange Range;
        public ImageLayout OldLayout;
        public ImageLayout NewLayout;
        public AccessFlags SrcAccess;
        public AccessFlags DstAccess;
        public PipelineStageFlags SrcStage;
        public PipelineStageFlags DstStage;
        public uint SrcQueueFamily;
        public uint DstQueueFamily;
    }

    /// <summary>内存屏障请求。</summary>
    public struct MemoryBarrierRequest
    {
        public AccessFlags SrcAccess;
        public AccessFlags DstAccess;
        public PipelineStageFlags SrcStage;
        public PipelineStageFlags DstStage;
    }

    /// <summary>屏障优化器统计。</summary>
    public struct BarrierOptimizerStats
    {
        public int TotalRequests;
        public int MergedBarriers;
        public int SkippedRedundant;
        public int SubmittedBarriers;
        public long SavedBarriers;

        public float MergeRate
            => TotalRequests == 0 ? 0 : (float)MergedBarriers / TotalRequests;

        public override string ToString()
            => $"Barriers: requests={TotalRequests}, merged={MergedBarriers} ({MergeRate * 100:F0}%), " +
               $"skipped={SkippedRedundant}, submitted={SubmittedBarriers}";
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 2: 资源状态跟踪器
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 资源状态跟踪器。记录每个 image/buffer 当前的 layout/access/stage。
    /// 用于判断是否需要 barrier，避免冗余 barrier。
    /// </summary>
    public sealed class ResourceStateTracker
    {
        private readonly Dictionary<ulong, ResourceState> _imageStates = new();
        private readonly Dictionary<ulong, ResourceState> _bufferStates = new();
        private readonly object _lock = new();

        /// <summary>记录/更新 image 状态。</summary>
        public void SetImageState(Image img, ImageLayout layout, AccessFlags access,
                                  PipelineStageFlags stage, uint queueFamily = 0)
        {
            lock (_lock)
            {
                _imageStates[img.Handle] = new ResourceState
                {
                    Layout = layout,
                    LastAccess = access,
                    LastStage = stage,
                    QueueFamilyIndex = queueFamily,
                    IsInitialized = true,
                };
            }
        }

        /// <summary>查询 image 当前状态。</summary>
        public bool TryGetImageState(Image img, out ResourceState state)
        {
            lock (_lock) return _imageStates.TryGetValue(img.Handle, out state);
        }

        /// <summary>记录/更新 buffer 状态。</summary>
        public void SetBufferState(Buffer buf, AccessFlags access, PipelineStageFlags stage,
                                   uint queueFamily = 0)
        {
            lock (_lock)
            {
                _bufferStates[buf.Handle] = new ResourceState
                {
                    LastAccess = access,
                    LastStage = stage,
                    QueueFamilyIndex = queueFamily,
                    IsInitialized = true,
                };
            }
        }

        /// <summary>查询 buffer 当前状态。</summary>
        public bool TryGetBufferState(Buffer buf, out ResourceState state)
        {
            lock (_lock) return _bufferStates.TryGetValue(buf.Handle, out state);
        }

        /// <summary>判断是否需要 image barrier。</summary>
        public bool NeedsImageBarrier(Image img, ImageLayout targetLayout,
                                      AccessFlags targetAccess, out ImageBarrierRequest req)
        {
            req = default;
            lock (_lock)
            {
                if (!_imageStates.TryGetValue(img.Handle, out var state))
                {
                    // 没记录过，需要从 Undefined 转换
                    req = new ImageBarrierRequest
                    {
                        Image = img,
                        OldLayout = ImageLayout.Undefined,
                        NewLayout = targetLayout,
                        SrcAccess = 0,
                        DstAccess = targetAccess,
                    };
                    return true;
                }

                if (state.Layout == targetLayout && (state.LastAccess & targetAccess) != 0)
                {
                    // 已经是目标状态，不需要 barrier
                    return false;
                }

                req = new ImageBarrierRequest
                {
                    Image = img,
                    OldLayout = state.Layout,
                    NewLayout = targetLayout,
                    SrcAccess = state.LastAccess,
                    DstAccess = targetAccess,
                };
                return true;
            }
        }

        /// <summary>判断是否需要 buffer barrier。</summary>
        public bool NeedsBufferBarrier(Buffer buf, AccessFlags targetAccess,
                                       out BufferBarrierRequest req)
        {
            req = default;
            lock (_lock)
            {
                if (!_bufferStates.TryGetValue(buf.Handle, out var state))
                    return false;  // 没记录就不插 barrier

                if ((state.LastAccess & targetAccess) != 0)
                    return false;

                req = new BufferBarrierRequest
                {
                    Buffer = buf,
                    SrcAccess = state.LastAccess,
                    DstAccess = targetAccess,
                };
                return true;
            }
        }

        /// <summary>清空所有状态。</summary>
        public void Clear()
        {
            lock (_lock)
            {
                _imageStates.Clear();
                _bufferStates.Clear();
            }
        }

        public int ImageStateCount { get { lock (_lock) return _imageStates.Count; } }
        public int BufferStateCount { get { lock (_lock) return _bufferStates.Count; } }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 3: 屏障批处理器
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 屏障批处理器。收集一帧内的所有 barrier，最后一次性 submit。
    /// </summary>
    internal sealed class BarrierBatcher
    {
        private readonly List<MemoryBarrier> _memoryBarriers = new();
        private readonly List<BufferMemoryBarrier> _bufferBarriers = new();
        private readonly List<ImageMemoryBarrier> _imageBarriers = new();
        private readonly List<(PipelineStageFlags, PipelineStageFlags)> _stagePairs = new();
        private readonly ResourceStateTracker _stateTracker;
        private readonly BarrierOptimizerStats _stats;

        internal BarrierBatcher(ResourceStateTracker tracker, ref BarrierOptimizerStats stats)
        {
            _stateTracker = tracker;
            _stats = stats;
        }

        /// <summary>添加一个 image barrier 请求。</summary>
        public void AddImageBarrier(in ImageBarrierRequest req)
        {
            _stats.TotalRequests++;

            // 检查是否冗余
            if (_stateTracker.TryGetImageState(req.Image, out var state) &&
                state.Layout == req.NewLayout &&
                (state.LastAccess & req.DstAccess) != 0)
            {
                _stats.SkippedRedundant++;
                return;
            }

            var barrier = new ImageMemoryBarrier
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = req.SrcAccess,
                DstAccessMask = req.DstAccess,
                OldLayout = req.OldLayout,
                NewLayout = req.NewLayout,
                SrcQueueFamilyIndex = req.SrcQueueFamily,
                DstQueueFamilyIndex = req.DstQueueFamily,
                Image = req.Image,
                SubresourceRange = req.Range,
            };
            _imageBarriers.Add(barrier);
            _stagePairs.Add((req.SrcStage, req.DstStage));

            // 更新状态
            _stateTracker.SetImageState(req.Image, req.NewLayout, req.DstAccess,
                                        req.DstStage, req.DstQueueFamily);
        }

        /// <summary>添加一个 buffer barrier 请求。</summary>
        public void AddBufferBarrier(in BufferBarrierRequest req)
        {
            _stats.TotalRequests++;

            var barrier = new BufferMemoryBarrier
            {
                SType = StructureType.BufferMemoryBarrier,
                SrcAccessMask = req.SrcAccess,
                DstAccessMask = req.DstAccess,
                SrcQueueFamilyIndex = req.SrcQueueFamily,
                DstQueueFamilyIndex = req.DstQueueFamily,
                Buffer = req.Buffer,
                Offset = req.Offset,
                Size = req.Size,
            };
            _bufferBarriers.Add(barrier);
            _stagePairs.Add((req.SrcStage, req.DstStage));

            _stateTracker.SetBufferState(req.Buffer, req.DstAccess, req.DstStage, req.DstQueueFamily);
        }

        /// <summary>添加一个 memory barrier 请求。</summary>
        public void AddMemoryBarrier(in MemoryBarrierRequest req)
        {
            _stats.TotalRequests++;

            var barrier = new MemoryBarrier
            {
                SType = StructureType.MemoryBarrier,
                SrcAccessMask = req.SrcAccess,
                DstAccessMask = req.DstAccess,
            };
            _memoryBarriers.Add(barrier);
            _stagePairs.Add((req.SrcStage, req.DstStage));
        }

        /// <summary>提交所有 barrier 到 command buffer。</summary>
        public void Submit(CommandBuffer cmd)
        {
            if (_memoryBarriers.Count == 0 && _bufferBarriers.Count == 0 && _imageBarriers.Count == 0)
                return;

            // 合并 stage mask
            PipelineStageFlags srcStage = 0, dstStage = 0;
            foreach (var (s, d) in _stagePairs)
            {
                srcStage |= s;
                dstStage |= d;
            }

            // 钳制到最窄的合法范围
            srcStage = NarrowStageMask(srcStage);
            dstStage = NarrowStageMask(dstStage);

            unsafe
            {
                var vk = Vk.GetApi();
                fixed (MemoryBarrier* pMem = _memoryBarriers.ToArray())
                fixed (BufferMemoryBarrier* pBuf = _bufferBarriers.ToArray())
                fixed (ImageMemoryBarrier* pImg = _imageBarriers.ToArray())
                {
                    vk.CmdPipelineBarrier(cmd, srcStage, dstStage, DependencyFlags.ByRegionBit,
                        (uint)_memoryBarriers.Count, pMem,
                        (uint)_bufferBarriers.Count, pBuf,
                        (uint)_imageBarriers.Count, pImg);
                }
            }

            _stats.MergedBarriers += _memoryBarriers.Count + _bufferBarriers.Count + _imageBarriers.Count;
            _stats.SubmittedBarriers++;
            Clear();
        }

        /// <summary>清空。</summary>
        public void Clear()
        {
            _memoryBarriers.Clear();
            _bufferBarriers.Clear();
            _imageBarriers.Clear();
            _stagePairs.Clear();
        }

        public int PendingCount => _memoryBarriers.Count + _bufferBarriers.Count + _imageBarriers.Count;

        /// <summary>钳制 stage mask 到最窄范围。</summary>
        private static PipelineStageFlags NarrowStageMask(PipelineStageFlags mask)
        {
            // 如果有 ALL_COMMANDS，移除其他 (ALL_COMMANDS 已经覆盖)
            if ((mask & PipelineStageFlags.AllCommandsBit) != 0)
                return PipelineStageFlags.AllCommandsBit;
            if ((mask & PipelineStageFlags.AllGraphicsBit) != 0)
            {
                // ALL_GRAPHICS 只覆盖图形阶段，但加上 compute/transfer 可能更窄
                var graphics = PipelineStageFlags.AllGraphicsBit;
                if ((mask & PipelineStageFlags.ComputeShaderBit) != 0) graphics |= PipelineStageFlags.ComputeShaderBit;
                if ((mask & (PipelineStageFlags.TransferBit | PipelineStageFlags.CopyBit)) != 0)
                    graphics |= PipelineStageFlags.TransferBit;
                if ((mask & PipelineStageFlags.HostBit) != 0) graphics |= PipelineStageFlags.HostBit;
                return graphics;
            }
            return mask;
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 4: 屏障优化器主类
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 屏障优化器主类。
    /// </summary>
    public sealed unsafe class VulkanBarrierOptimizer : IDisposable
    {
        private readonly ResourceStateTracker _stateTracker;
        private readonly BarrierBatcher _batcher;
        private readonly BarrierOptimizerStats _stats;
        private readonly object _lock = new();

        public ResourceStateTracker StateTracker => _stateTracker;
        public ref BarrierOptimizerStats Stats => ref _stats;

        public VulkanBarrierOptimizer()
        {
            _stateTracker = new ResourceStateTracker();
            _stats = new BarrierOptimizerStats();
            _batcher = new BarrierBatcher(_stateTracker, ref _stats);
        }

        /// <summary>请求 image layout 转换。</summary>
        public void RequestImageLayoutTransition(Image img, ImageSubresourceRange range,
                                                 ImageLayout newLayout, AccessFlags newAccess,
                                                 PipelineStageFlags newStage)
        {
            ImageBarrierRequest req;
            if (_stateTracker.TryGetImageState(img, out var state))
            {
                req = new ImageBarrierRequest
                {
                    Image = img,
                    Range = range,
                    OldLayout = state.Layout,
                    NewLayout = newLayout,
                    SrcAccess = state.LastAccess,
                    DstAccess = newAccess,
                    SrcStage = state.LastStage,
                    DstStage = newStage,
                    SrcQueueFamily = Vk.QueueFamilyIgnored,
                    DstQueueFamily = Vk.QueueFamilyIgnored,
                };
            }
            else
            {
                req = new ImageBarrierRequest
                {
                    Image = img,
                    Range = range,
                    OldLayout = ImageLayout.Undefined,
                    NewLayout = newLayout,
                    SrcAccess = 0,
                    DstAccess = newAccess,
                    SrcStage = PipelineStageFlags.TopOfPipeBit,
                    DstStage = newStage,
                    SrcQueueFamily = Vk.QueueFamilyIgnored,
                    DstQueueFamily = Vk.QueueFamilyIgnored,
                };
            }

            lock (_lock) _batcher.AddImageBarrier(req);
        }

        /// <summary>请求 buffer access 转换。</summary>
        public void RequestBufferAccessTransition(Buffer buf, ulong offset, ulong size,
                                                  AccessFlags newAccess, PipelineStageFlags newStage)
        {
            BufferBarrierRequest req;
            if (_stateTracker.TryGetBufferState(buf, out var state))
            {
                req = new BufferBarrierRequest
                {
                    Buffer = buf,
                    Offset = offset,
                    Size = size,
                    SrcAccess = state.LastAccess,
                    DstAccess = newAccess,
                    SrcStage = state.LastStage,
                    DstStage = newStage,
                    SrcQueueFamily = Vk.QueueFamilyIgnored,
                    DstQueueFamily = Vk.QueueFamilyIgnored,
                };
            }
            else
            {
                req = new BufferBarrierRequest
                {
                    Buffer = buf,
                    Offset = offset,
                    Size = size,
                    SrcAccess = 0,
                    DstAccess = newAccess,
                    SrcStage = PipelineStageFlags.TopOfPipeBit,
                    DstStage = newStage,
                    SrcQueueFamily = Vk.QueueFamilyIgnored,
                    DstQueueFamily = Vk.QueueFamilyIgnored,
                };
            }

            lock (_lock) _batcher.AddBufferBarrier(req);
        }

        /// <summary>请求 memory barrier。</summary>
        public void RequestMemoryBarrier(AccessFlags srcAccess, AccessFlags dstAccess,
                                         PipelineStageFlags srcStage, PipelineStageFlags dstStage)
        {
            var req = new MemoryBarrierRequest
            {
                SrcAccess = srcAccess,
                DstAccess = dstAccess,
                SrcStage = srcStage,
                DstStage = dstStage,
            };
            lock (_lock) _batcher.AddMemoryBarrier(req);
        }

        /// <summary>请求 queue family ownership 转移。</summary>
        public void RequestQueueOwnershipTransfer(Image img, ImageSubresourceRange range,
                                                  uint srcQueue, uint dstQueue,
                                                  AccessFlags srcAccess, AccessFlags dstAccess,
                                                  PipelineStageFlags srcStage, PipelineStageFlags dstStage)
        {
            var req = new ImageBarrierRequest
            {
                Image = img,
                Range = range,
                OldLayout = _stateTracker.TryGetImageState(img, out var s) ? s.Layout : ImageLayout.General,
                NewLayout = _stateTracker.TryGetImageState(img, out s) ? s.Layout : ImageLayout.General,
                SrcAccess = srcAccess,
                DstAccess = dstAccess,
                SrcStage = srcStage,
                DstStage = dstStage,
                SrcQueueFamily = srcQueue,
                DstQueueFamily = dstQueue,
            };
            lock (_lock) _batcher.AddImageBarrier(req);
        }

        /// <summary>提交所有 pending 的 barrier。</summary>
        public void Flush(CommandBuffer cmd)
        {
            lock (_lock) _batcher.Submit(cmd);
        }

        /// <summary>每帧开始时调用，清空状态。</summary>
        public void OnFrameBegin()
        {
            // 不清空状态，因为跨帧需要保持 layout 跟踪
        }

        /// <summary>获取统计。</summary>
        public BarrierOptimizerStats GetStats() => _stats;

        /// <summary>清空所有资源状态 (慎用)。</summary>
        public void ResetAllStates()
        {
            _stateTracker.Clear();
        }

        public void Dispose()
        {
            _stateTracker.Clear();
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 5: 屏障辅助
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>常用的 stage/access 组合。</summary>
    public static class BarrierPatterns
    {
        // ── Image layout transitions ──

        /// <summary>undefined -> color attachment optimal。</summary>
        public static (PipelineStageFlags src, PipelineStageFlags dst, AccessFlags srcAccess, AccessFlags dstAccess)
            UndefinedToColorAttachment()
            => (PipelineStageFlags.TopOfPipeBit,
                PipelineStageFlags.ColorAttachmentOutputBit,
                0,
                AccessFlags.ColorAttachmentReadBit | AccessFlags.ColorAttachmentWriteBit);

        /// <summary>color attachment -> present source。</summary>
        public static (PipelineStageFlags src, PipelineStageFlags dst, AccessFlags srcAccess, AccessFlags dstAccess)
            ColorAttachmentToPresent()
            => (PipelineStageFlags.ColorAttachmentOutputBit,
                PipelineStageFlags.BottomOfPipeBit,
                AccessFlags.ColorAttachmentWriteBit,
                0);

        /// <summary>undefined -> depth attachment optimal。</summary>
        public static (PipelineStageFlags src, PipelineStageFlags dst, AccessFlags srcAccess, AccessFlags dstAccess)
            UndefinedToDepthAttachment()
            => (PipelineStageFlags.TopOfPipeBit,
                PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit,
                0,
                AccessFlags.DepthStencilAttachmentReadBit | AccessFlags.DepthStencilAttachmentWriteBit);

        /// <summary>transfer dst -> shader read only。</summary>
        public static (PipelineStageFlags src, PipelineStageFlags dst, AccessFlags srcAccess, AccessFlags dstAccess)
            TransferDstToShaderRead()
            => (PipelineStageFlags.TransferBit,
                PipelineStageFlags.FragmentShaderBit,
                AccessFlags.TransferWriteBit,
                AccessFlags.ShaderReadBit);

        /// <summary>shader read -> transfer dst。</summary>
        public static (PipelineStageFlags src, PipelineStageFlags dst, AccessFlags srcAccess, AccessFlags dstAccess)
            ShaderReadToTransferDst()
            => (PipelineStageFlags.FragmentShaderBit,
                PipelineStageFlags.TransferBit,
                AccessFlags.ShaderReadBit,
                AccessFlags.TransferWriteBit);

        /// <summary>color attachment -> shader read。</summary>
        public static (PipelineStageFlags src, PipelineStageFlags dst, AccessFlags srcAccess, AccessFlags dstAccess)
            ColorAttachmentToShaderRead()
            => (PipelineStageFlags.ColorAttachmentOutputBit,
                PipelineStageFlags.FragmentShaderBit,
                AccessFlags.ColorAttachmentWriteBit,
                AccessFlags.ShaderReadBit);

        /// <summary>shader read -> color attachment。</summary>
        public static (PipelineStageFlags src, PipelineStageFlags dst, AccessFlags srcAccess, AccessFlags dstAccess)
            ShaderReadToColorAttachment()
            => (PipelineStageFlags.FragmentShaderBit,
                PipelineStageFlags.ColorAttachmentOutputBit,
                AccessFlags.ShaderReadBit,
                AccessFlags.ColorAttachmentReadBit | AccessFlags.ColorAttachmentWriteBit);

        // ── Buffer access transitions ──

        /// <summary>host write -> vertex shader read。</summary>
        public static (PipelineStageFlags src, PipelineStageFlags dst, AccessFlags srcAccess, AccessFlags dstAccess)
            HostWriteToVertexRead()
            => (PipelineStageFlags.HostBit,
                PipelineStageFlags.VertexInputBit,
                AccessFlags.HostWriteBit,
                AccessFlags.VertexAttributeReadBit);

        /// <summary>transfer write -> vertex shader read。</summary>
        public static (PipelineStageFlags src, PipelineStageFlags dst, AccessFlags srcAccess, AccessFlags dstAccess)
            TransferWriteToVertexRead()
            => (PipelineStageFlags.TransferBit,
                PipelineStageFlags.VertexInputBit,
                AccessFlags.TransferWriteBit,
                AccessFlags.VertexAttributeReadBit);

        /// <summary>transfer write -> shader read (uniform)。</summary>
        public static (PipelineStageFlags src, PipelineStageFlags dst, AccessFlags srcAccess, AccessFlags dstAccess)
            TransferWriteToUniformRead()
            => (PipelineStageFlags.TransferBit,
                PipelineStageFlags.VertexShaderBit | PipelineStageFlags.FragmentShaderBit,
                AccessFlags.TransferWriteBit,
                AccessFlags.UniformReadBit);

        /// <summary>shader write -> shader read (storage buffer)。</summary>
        public static (PipelineStageFlags src, PipelineStageFlags dst, AccessFlags srcAccess, AccessFlags dstAccess)
            ShaderWriteToShaderRead()
            => (PipelineStageFlags.ComputeShaderBit,
                PipelineStageFlags.VertexShaderBit | PipelineStageFlags.FragmentShaderBit,
                AccessFlags.ShaderWriteBit,
                AccessFlags.ShaderReadBit);

        /// <summary>shader write -> host read。</summary>
        public static (PipelineStageFlags src, PipelineStageFlags dst, AccessFlags srcAccess, AccessFlags dstAccess)
            ShaderWriteToHostRead()
            => (PipelineStageFlags.ComputeShaderBit,
                PipelineStageFlags.HostBit,
                AccessFlags.ShaderWriteBit,
                AccessFlags.HostReadBit);
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 6: 全局屏障 (一次清空所有 cache)
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>全局屏障 helper。最简单的内存屏障，清空所有 cache。</summary>
    public static class GlobalBarriers
    {
        /// <summary>全屏障 (所有 cache flush+invalidate)。慢但保险。</summary>
        public static MemoryBarrierRequest Full()
            => new MemoryBarrierRequest
            {
                SrcAccess = AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit,
                DstAccess = AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit,
                SrcStage = PipelineStageFlags.AllCommandsBit,
                DstStage = PipelineStageFlags.AllCommandsBit,
            };

        /// <summary>host->device。</summary>
        public static MemoryBarrierRequest HostToDevice()
            => new MemoryBarrierRequest
            {
                SrcAccess = AccessFlags.HostWriteBit,
                DstAccess = AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit,
                SrcStage = PipelineStageFlags.HostBit,
                DstStage = PipelineStageFlags.AllCommandsBit,
            };

        /// <summary>device->host。</summary>
        public static MemoryBarrierRequest DeviceToHost()
            => new MemoryBarrierRequest
            {
                SrcAccess = AccessFlags.MemoryWriteBit,
                DstAccess = AccessFlags.HostReadBit,
                SrcStage = PipelineStageFlags.AllCommandsBit,
                DstStage = PipelineStageFlags.HostBit,
            };
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 7: 屏障优化器监控
    // ════════════════════════════════════════════════════════════════════════════

    public sealed class BarrierOptimizerMonitor : IDisposable
    {
        private readonly VulkanBarrierOptimizer _opt;
        private System.Threading.Timer _timer;
        private readonly object _lock = new();
        private readonly List<BarrierOptimizerStats> _history = new();
        private const int MaxHistory = 60;
        private bool _disposed;

        public event Action<BarrierOptimizerStats> OnStatsUpdated;

        public BarrierOptimizerMonitor(VulkanBarrierOptimizer opt, TimeSpan? interval = null)
        {
            _opt = opt;
            _timer = new System.Threading.Timer(_ => Tick(), null,
                interval ?? TimeSpan.FromSeconds(1),
                interval ?? TimeSpan.FromSeconds(1));
        }

        private void Tick()
        {
            if (_disposed) return;
            var s = _opt.GetStats();
            lock (_lock)
            {
                _history.Add(s);
                if (_history.Count > MaxHistory) _history.RemoveAt(0);
            }
            OnStatsUpdated?.Invoke(s);
        }

        public BarrierOptimizerStats[] GetHistory()
        {
            lock (_lock) return _history.ToArray();
        }

        public void Dispose()
        {
            _disposed = true;
            _timer?.Dispose();
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 8: 版本信息
    // ════════════════════════════════════════════════════════════════════════════

    public static class BarrierOptimizerVersion
    {
        public const string Version = "1.0.0";
        public const string Description = "ShanyuEngine Vulkan Barrier Optimizer (state tracking + batch merge)";
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 9: 使用示例
    // ════════════════════════════════════════════════════════════════════════════

    /*
    // === 使用示例 ===
    //
    // var opt = new VulkanBarrierOptimizer();
    //
    // // 把 color attachment 转成 shader read
    // var (srcStage, dstStage, srcAccess, dstAccess) = BarrierPatterns.ColorAttachmentToShaderRead();
    // opt.RequestImageLayoutTransition(colorImage, range, ImageLayout.ShaderReadOnlyOptimal,
    //                                  dstAccess, dstStage);
    //
    // // 把另一个 image 从 undefined 转 color attachment
    // var (s2, d2, sa, da) = BarrierPatterns.UndefinedToColorAttachment();
    // opt.RequestImageLayoutTransition(renderTarget, range, ImageLayout.ColorAttachmentOptimal,
    //                                  da, d2);
    //
    // // 一次性提交
    // opt.Flush(cmd);
    //
    // // 统计
    // Console.WriteLine(opt.GetStats());
    */

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 10: 单元测试桩
    // ════════════════════════════════════════════════════════════════════════════

    public static class BarrierOptimizerTestStub
    {
        public static bool TestNeedsTransition(ImageLayout current, ImageLayout target)
            => current != target;

        public static PipelineStageFlags TestNarrowMask(PipelineStageFlags mask)
        {
            if ((mask & PipelineStageFlags.AllCommandsBit) != 0)
                return PipelineStageFlags.AllCommandsBit;
            return mask;
        }

        public static int TestCountBarriers(int mem, int buf, int img)
            => mem + buf + img;
    }
}

// 文件结束 - VulkanBarrierOptimizer.cs
