using Vulkan = Silk.NET.Vulkan;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;
using Semaphore = Silk.NET.Vulkan.Semaphore;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace YamaEngine.RHI.Vulkan
{
    // ════════════════════════════════════════════════════════════════
    // Win32 Interop
    // ════════════════════════════════════════════════════════════════

    internal static class Win32Interop
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetEvent(IntPtr hEvent);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool ResetEvent(IntPtr hEvent);
    }

    // ════════════════════════════════════════════════════════════════
    // Vulkan Fence (Timeline Semaphore-backed)
    // ════════════════════════════════════════════════════════════════

    public sealed unsafe class VulkanFence : RHIFence
    {
        internal readonly VulkanDevice Device;
        internal VkSemaphore Semaphore;
        private bool _disposed;

        public VulkanFence(VulkanDevice device, ulong initialValue)
        {
            Device = device;
            Semaphore = new VkSemaphore(device, initialValue);
        }

        public override ulong CompletedValue => Semaphore.GetValue();

        public override void Signal(ulong value)
        {
            Semaphore.Signal(value);
        }

        public override bool SetEventOnCompletion(ulong value, IntPtr waitEvent)
        {
            if (waitEvent == IntPtr.Zero)
                return false;

            Task.Run(() =>
            {
                while (Semaphore.GetValue() < value)
                {
                    try
                    {
                        Semaphore.Wait(value, 100_000_000); // 100ms
                    }
                    catch
                    {
                        Thread.Sleep(1);
                    }
                }
                Win32Interop.SetEvent(waitEvent);
            });

            return true;
        }

        public override void Wait(ulong value, uint timeoutMs = uint.MaxValue)
        {
            ulong timeoutNs = timeoutMs == uint.MaxValue ? ulong.MaxValue : (ulong)timeoutMs * 1_000_000;
            Semaphore.Wait(value, timeoutNs);
        }

        public override void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Semaphore?.Dispose();
            Semaphore = null;
        }
    }

    // ════════════════════════════════════════════════════════════════
    // Command Pool Manager (per-thread, per-frame)
    // ════════════════════════════════════════════════════════════════

    internal sealed unsafe class VkCommandPoolManager : IDisposable
    {
        private readonly VulkanDevice _device;
        private readonly uint _queueFamilyIndex;
        private readonly ConcurrentDictionary<int, CommandPool> _threadPools = new();
        private readonly ConcurrentDictionary<int, ConcurrentBag<CommandBuffer>> _freeBuffers = new();
        private bool _disposed;

        public VkCommandPoolManager(VulkanDevice device, uint queueFamilyIndex)
        {
            _device = device;
            _queueFamilyIndex = queueFamilyIndex;
        }

        public CommandBuffer GetOrCreateBuffer(int threadId)
        {
            var pool = _threadPools.GetOrAdd(threadId, id => CreatePool());
            var bag = _freeBuffers.GetOrAdd(threadId, _ => new ConcurrentBag<CommandBuffer>());

            if (bag.TryTake(out CommandBuffer cmd))
                return cmd;

            CommandBufferAllocateInfo allocInfo = new()
            {
                SType = StructureType.CommandBufferAllocateInfo,
                CommandPool = pool,
                Level = CommandBufferLevel.Primary,
                CommandBufferCount = 1
            };

            VkHelper.Check(_device.Vk.AllocateCommandBuffers(_device.VkDevice, &allocInfo, out CommandBuffer cmdBuffer),
                "Failed to allocate command buffer");
            return cmdBuffer;
        }

        /// <summary>
        /// 获取或创建指定线程的命令池
        /// 每个 CPU 线程拥有独立的 CommandPool，实现无锁的命令录制
        /// </summary>
        public CommandPool GetOrCreatePool(int threadId)
        {
            return _threadPools.GetOrAdd(threadId, id => CreatePool());
        }

        public void ReturnBuffer(int threadId, CommandBuffer cmd)
        {
            if (_freeBuffers.TryGetValue(threadId, out var bag))
                bag.Add(cmd);
        }

        public void ResetPools()
        {
            foreach (var kvp in _threadPools)
            {
                _device.Vk.ResetCommandPool(_device.VkDevice, kvp.Value, 0);
            }
        }

        private CommandPool CreatePool()
        {
            CommandPoolCreateInfo poolInfo = new()
            {
                SType = StructureType.CommandPoolCreateInfo,
                Flags = CommandPoolCreateFlags.ResetCommandBufferBit | CommandPoolCreateFlags.TransientBit,
                QueueFamilyIndex = _queueFamilyIndex
            };

            VkHelper.Check(_device.Vk.CreateCommandPool(_device.VkDevice, &poolInfo, null, out CommandPool pool),
                "Failed to create command pool");
            return pool;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            foreach (var kvp in _threadPools)
            {
                if (kvp.Value.Handle != 0)
                    _device.Vk.DestroyCommandPool(_device.VkDevice, kvp.Value, null);
            }
            _threadPools.Clear();
            _freeBuffers.Clear();
        }
    }

    // ════════════════════════════════════════════════════════════════
    // Vulkan Command Queue
    // ════════════════════════════════════════════════════════════════

    public sealed unsafe class VulkanCommandQueue : RHICommandQueue
    {
        internal readonly VulkanDevice Device;
        internal Queue VkQueue;
        internal uint QueueFamilyIndex;
        private bool _disposed;

        public VulkanCommandQueue(VulkanDevice device, RHIQueueType type, uint queueFamilyIndex, Queue queue)
        {
            Device = device;
            QueueType = type;
            QueueFamilyIndex = queueFamilyIndex;
            VkQueue = queue;
        }

        public override void ExecuteCommandLists(RHICommandList[] commandLists)
        {
            if (commandLists == null || commandLists.Length == 0)
                return;

            Span<IntPtr> cmdBufHandles = stackalloc IntPtr[commandLists.Length];
            Span<CommandBuffer> cmdBuffers = stackalloc CommandBuffer[commandLists.Length];

            for (int i = 0; i < commandLists.Length; i++)
            {
                var vkList = (VulkanCommandList)commandLists[i];
                cmdBuffers[i] = vkList.CmdBuffer;
            }

            fixed (CommandBuffer* pCmdBuffers = cmdBuffers)
            {
                SubmitInfo submitInfo = new()
                {
                    SType = StructureType.SubmitInfo,
                    CommandBufferCount = (uint)commandLists.Length,
                    PCommandBuffers = pCmdBuffers
                };

                VkHelper.Check(Device.Vk.QueueSubmit(VkQueue, 1, &submitInfo, default),
                    "Failed to submit command buffers to queue");
            }
        }

        internal void SubmitWithSignal(CommandBuffer cmdBuffer, VkSemaphore signalSemaphore, ulong signalValue)
        {
            CommandBuffer* pCmd = &cmdBuffer;
            fixed (Semaphore* pSignalSem = &signalSemaphore.Handle)
            {
                TimelineSemaphoreSubmitInfo timelineInfo = new()
                {
                    SType = StructureType.TimelineSemaphoreSubmitInfo,
                    SignalSemaphoreValueCount = 1,
                    PSignalSemaphoreValues = &signalValue
                };

                SubmitInfo submitInfo = new()
                {
                    SType = StructureType.SubmitInfo,
                    PNext = &timelineInfo,
                    CommandBufferCount = 1,
                    PCommandBuffers = pCmd,
                    SignalSemaphoreCount = 1,
                    PSignalSemaphores = pSignalSem
                };

                VkHelper.Check(Device.Vk.QueueSubmit(VkQueue, 1, &submitInfo, default),
                    "Failed to submit with signal");
            }
        }

        internal void SubmitWithWait(CommandBuffer cmdBuffer, VkSemaphore waitSemaphore, ulong waitValue,
            PipelineStageFlags waitStage)
        {
            CommandBuffer* pCmd = &cmdBuffer;
            fixed (Semaphore* pWaitSem = &waitSemaphore.Handle)
            {
                TimelineSemaphoreSubmitInfo timelineInfo = new()
                {
                    SType = StructureType.TimelineSemaphoreSubmitInfo,
                    WaitSemaphoreValueCount = 1,
                    PWaitSemaphoreValues = &waitValue
                };

                SubmitInfo submitInfo = new()
                {
                    SType = StructureType.SubmitInfo,
                    PNext = &timelineInfo,
                    WaitSemaphoreCount = 1,
                    PWaitSemaphores = pWaitSem,
                    PWaitDstStageMask = &waitStage,
                    CommandBufferCount = 1,
                    PCommandBuffers = pCmd
                };

                VkHelper.Check(Device.Vk.QueueSubmit(VkQueue, 1, &submitInfo, default),
                    "Failed to submit with wait");
            }
        }

        public override void Signal(RHIFence fence, ulong value)
        {
            var vkFence = (VulkanFence)fence;
            QueueSignal(vkFence.Semaphore, value, PipelineStageFlags.AllCommandsBit);
        }

        internal void QueueSignal(VkSemaphore semaphore, ulong value, PipelineStageFlags stage)
        {
            fixed (Semaphore* pSem = &semaphore.Handle)
            {
                TimelineSemaphoreSubmitInfo timelineInfo = new()
                {
                    SType = StructureType.TimelineSemaphoreSubmitInfo,
                    SignalSemaphoreValueCount = 1,
                    PSignalSemaphoreValues = &value
                };

                SubmitInfo submitInfo = new()
                {
                    SType = StructureType.SubmitInfo,
                    PNext = &timelineInfo,
                    SignalSemaphoreCount = 1,
                    PSignalSemaphores = pSem
                };

                VkHelper.Check(Device.Vk.QueueSubmit(VkQueue, 1, &submitInfo, default),
                    "Failed to signal semaphore on queue");
            }
        }

        internal void QueueWait(VkSemaphore semaphore, ulong value, PipelineStageFlags stage)
        {
            fixed (Semaphore* pSem = &semaphore.Handle)
            {
                TimelineSemaphoreSubmitInfo timelineInfo = new()
                {
                    SType = StructureType.TimelineSemaphoreSubmitInfo,
                    WaitSemaphoreValueCount = 1,
                    PWaitSemaphoreValues = &value
                };

                SubmitInfo submitInfo = new()
                {
                    SType = StructureType.SubmitInfo,
                    PNext = &timelineInfo,
                    WaitSemaphoreCount = 1,
                    PWaitSemaphores = pSem,
                    PWaitDstStageMask = &stage
                };

                VkHelper.Check(Device.Vk.QueueSubmit(VkQueue, 1, &submitInfo, default),
                    "Failed to wait on semaphore on queue");
            }
        }

        public override void Wait(RHIFence fence, ulong value)
        {
            var vkFence = (VulkanFence)fence;
            QueueWait(vkFence.Semaphore, value, PipelineStageFlags.AllCommandsBit);
        }

        public override void Flush()
        {
            VkHelper.Check(Device.Vk.QueueWaitIdle(VkQueue), "Queue wait idle failed");
        }

        public override void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            // Queue is destroyed with the device
            VkQueue = default;
        }
    }

    // ════════════════════════════════════════════════════════════════
    // Vulkan Command List
    // ════════════════════════════════════════════════════════════════

    public sealed unsafe class VulkanCommandList : RHICommandList
    {
        internal readonly VulkanDevice Device;
        internal CommandBuffer CmdBuffer;
        internal CommandPool Pool;

        private string _name = "";
        private bool _disposed;
        private bool _isRecording;
        private bool _isRendering;

        // Current state
        private VulkanPipelineState _currentPSO;
        private VulkanRootSignature _currentGraphicsRoot;
        private VulkanRootSignature _currentComputeRoot;
        private PipelineBindPoint _currentBindPoint = PipelineBindPoint.Graphics;

        // Push constants
        private byte[] _pushConstants = new byte[128];
        private bool _pushConstantsDirty;

        // Render targets (for clearing)
        private RHIDescriptorHandle[] _currentRTVs = Array.Empty<RHIDescriptorHandle>();
        private RHIDescriptorHandle? _currentDSV;

        // Viewport/Scissor
        private RHIViewport _viewport;
        private RHIScissorRect _scissorRect;
        private bool _viewportDirty = true;
        private bool _scissorDirty = true;

        // Primitive topology
        private PrimitiveTopology _topology = PrimitiveTopology.TriangleList;

        public VulkanCommandList(VulkanDevice device, RHIQueueType type, string name)
        {
            Device = device;
            QueueType = type;
            _name = name;

            var poolManager = device.GetCommandPoolManager(type);
            int threadId = Environment.CurrentManagedThreadId;
            Pool = poolManager.GetOrCreatePool(threadId);

            CommandBufferAllocateInfo allocInfo = new()
            {
                SType = StructureType.CommandBufferAllocateInfo,
                CommandPool = Pool,
                Level = CommandBufferLevel.Primary,
                CommandBufferCount = 1
            };

            VkHelper.Check(device.Vk.AllocateCommandBuffers(device.VkDevice, &allocInfo, out CmdBuffer),
                "Failed to allocate command buffer");
        }

        public override string Name { get => _name; set => _name = value; }
        public override RHIQueueType QueueType { get; protected set; }

        // ── Recording ──

        public override void Open()
        {
            if (_isRecording) return;

            CommandBufferBeginInfo beginInfo = new()
            {
                SType = StructureType.CommandBufferBeginInfo,
                Flags = CommandBufferUsageFlags.OneTimeSubmitBit
            };

            VkHelper.Check(Device.Vk.BeginCommandBuffer(CmdBuffer, &beginInfo),
                "Failed to begin command buffer");
            _isRecording = true;
        }

        public override void Close()
        {
            if (!_isRecording) return;

            if (_isRendering)
            {
                Device.Vk.CmdEndRendering(CmdBuffer);
                _isRendering = false;
            }

            VkHelper.Check(Device.Vk.EndCommandBuffer(CmdBuffer),
                "Failed to end command buffer");
            _isRecording = false;
        }

        public override void Reset(RHIPipelineState? initialState = null)
        {
            if (_isRecording)
            {
                if (_isRendering)
                {
                    Device.Vk.CmdEndRendering(CmdBuffer);
                    _isRendering = false;
                }
                Device.Vk.EndCommandBuffer(CmdBuffer);
                _isRecording = false;
            }

            VkHelper.Check(Device.Vk.ResetCommandBuffer(CmdBuffer, 0),
                "Failed to reset command buffer");

            _currentPSO = null;
            _currentGraphicsRoot = null;
            _currentComputeRoot = null;
            _pushConstants.AsSpan().Clear();
            _pushConstantsDirty = false;
            _currentRTVs = Array.Empty<RHIDescriptorHandle>();
            _currentDSV = null;
            _viewportDirty = true;
            _scissorDirty = true;

            Open();

            if (initialState != null)
                SetPipelineState(initialState);
        }

        // ── Resource Barriers ──

        /// <summary>抽象方法实现：基于资源状态转换的 ResourceBarrier</summary>
        public override void ResourceBarrier(RHIResource resource, RHIResourceState stateBefore,
            RHIResourceState stateAfter, uint subresource = 0xFFFFFFFF)
        {
            ResourceBarrier(new RHIResourceBarrier(resource, stateBefore, stateAfter, subresource));
        }

        /// <summary>UAV 屏障（全局内存屏障）</summary>
        public override void UAVBarrier(RHIResource? resource)
        {
            MemoryBarrier barrier = new()
            {
                SType = StructureType.MemoryBarrier,
                SrcAccessMask = AccessFlags.ShaderWriteBit,
                DstAccessMask = AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit
            };
            Device.Vk.CmdPipelineBarrier(CmdBuffer,
                PipelineStageFlags.AllCommandsBit, PipelineStageFlags.AllCommandsBit,
                DependencyFlags.ByRegionBit, 1, &barrier, 0, null, 0, null);
        }

        /// <summary>Vulkan 扩展：基于 RHIResourceBarrier 结构的屏障</summary>
        public new void ResourceBarrier(RHIResourceBarrier barrier)
        {
            ResourceBarriers(new[] { barrier });
        }

        /// <summary>Vulkan 扩展：批量屏障</summary>
        public new void ResourceBarriers(RHIResourceBarrier[] barriers)
        {
            if (barriers == null || barriers.Length == 0) return;

            var imageBarriers = new List<ImageMemoryBarrier>();
            var bufferBarriers = new List<BufferMemoryBarrier>();

            PipelineStageFlags srcStage = 0;
            PipelineStageFlags dstStage = 0;

            foreach (var barrier in barriers)
            {
                var (layout, access, stage) = VkFormatMapper.ResourceStateToVk(barrier.StateAfter);
                var (_, srcAccess, srcStageVal) = VkFormatMapper.ResourceStateToVk(barrier.StateBefore);

                srcStage |= srcStageVal;
                dstStage |= stage;

                if (barrier.Resource is VulkanTexture tex)
                {
                    var (_, dstAccess, _) = VkFormatMapper.ResourceStateToVk(barrier.StateAfter);

                    ImageMemoryBarrier imgBarrier = new()
                    {
                        SType = StructureType.ImageMemoryBarrier,
                        SrcAccessMask = srcAccess,
                        DstAccessMask = dstAccess,
                        OldLayout = tex.CurrentLayout,
                        NewLayout = layout,
                        SrcQueueFamilyIndex = QueueFamilyIgnored,
                        DstQueueFamilyIndex = QueueFamilyIgnored,
                        Image = tex.VkImage,
                        SubresourceRange = new ImageSubresourceRange
                        {
                            AspectMask = VkFormatMapper.GetAspectMask(
                                VkFormatMapper.ToVkFormat(tex.TextureDesc!.Value.Format)),
                            BaseMipLevel = 0,
                            LevelCount = (uint)tex.TextureDesc.Value.MipLevels,
                            BaseArrayLayer = 0,
                            LayerCount = (uint)(tex.TextureDesc.Value.Dimension == RHIResourceDimension.Texture3D
                                ? 1 : tex.TextureDesc.Value.DepthOrArraySize)
                        }
                    };

                    imageBarriers.Add(imgBarrier);
                    tex.CurrentLayout = layout;
                    tex.CurrentState = barrier.StateAfter;
                }
                else if (barrier.Resource is VulkanBuffer buf)
                {
                    var (_, dstAccess, _) = VkFormatMapper.ResourceStateToVk(barrier.StateAfter);

                    BufferMemoryBarrier bufBarrier = new()
                    {
                        SType = StructureType.BufferMemoryBarrier,
                        SrcAccessMask = srcAccess,
                        DstAccessMask = dstAccess,
                        SrcQueueFamilyIndex = QueueFamilyIgnored,
                        DstQueueFamilyIndex = QueueFamilyIgnored,
                        Buffer = buf.VkBuffer,
                        Offset = 0,
                        Size = buf.Allocation.Size
                    };

                    bufferBarriers.Add(bufBarrier);
                    buf.CurrentState = barrier.StateAfter;
                }
            }

            if (srcStage == 0) srcStage = PipelineStageFlags.TopOfPipeBit;
            if (dstStage == 0) dstStage = PipelineStageFlags.BottomOfPipeBit;

            ImageMemoryBarrier* pImgBarriers = null;
            BufferMemoryBarrier* pBufBarriers = null;

            if (imageBarriers.Count > 0)
            {
                var arr = imageBarriers.ToArray();
                fixed (ImageMemoryBarrier* p = arr)
                {
                    pImgBarriers = p;
                    if (bufferBarriers.Count == 0)
                    {
                        Device.Vk.CmdPipelineBarrier(CmdBuffer, srcStage, dstStage, 0,
                            0, null, 0, null, (uint)imageBarriers.Count, pImgBarriers);
                    }
                }
            }

            if (bufferBarriers.Count > 0)
            {
                var arr = bufferBarriers.ToArray();
                fixed (BufferMemoryBarrier* p = arr)
                {
                    pBufBarriers = p;
                    if (imageBarriers.Count > 0)
                    {
                        var imgArr = imageBarriers.ToArray();
                        fixed (ImageMemoryBarrier* pImg = imgArr)
                        {
                            Device.Vk.CmdPipelineBarrier(CmdBuffer, srcStage, dstStage, 0,
                                0, null, (uint)bufferBarriers.Count, pBufBarriers,
                                (uint)imageBarriers.Count, pImg);
                        }
                    }
                    else
                    {
                        Device.Vk.CmdPipelineBarrier(CmdBuffer, srcStage, dstStage, 0,
                            0, null, (uint)bufferBarriers.Count, pBufBarriers, 0, null);
                    }
                }
            }
        }

        // ── Copy Operations ──

        public override void CopyBufferRegion(RHIBuffer dst, ulong dstOffset, RHIBuffer src, ulong srcOffset, ulong numBytes)
        {
            var dstBuf = (VulkanBuffer)dst;
            var srcBuf = (VulkanBuffer)src;
            Device.Vk.CmdCopyBuffer(CmdBuffer, srcBuf.VkBuffer, dstBuf.VkBuffer, 1,
                new BufferCopy(srcOffset, dstOffset, numBytes));
        }

        /// <summary>抽象方法实现：CopyTextureRegion (uint 版本)</summary>
        public override void CopyTextureRegion(RHITexture dst, uint dstSubresource, uint dstX, uint dstY, uint dstZ,
            RHIBuffer src, ulong srcOffset, uint srcRowPitch, uint srcSlicePitch, uint srcWidth, uint srcHeight)
        {
            // 委托给 int 版本（Vulkan 扩展，含 depth 参数）
            CopyTextureRegion(dst, (int)dstX, (int)dstY, (int)dstZ, src, srcOffset,
                (int)srcRowPitch, (int)srcSlicePitch, (int)srcWidth, (int)srcHeight, 1);
        }

        public void CopyTextureRegion(RHITexture dst, int dstX, int dstY, int dstZ,
            RHIBuffer src, ulong srcOffset, int srcRowPitch, int srcSlicePitch, int width, int height, int depth)
        {
            var dstTex = (VulkanTexture)dst;
            var srcBuf = (VulkanBuffer)src;

            int bpp = RHIFormatUtils.BytesPerPixel(dstTex.TextureDesc!.Value.Format);
            uint bufferRowLength = bpp > 0 ? (uint)(srcRowPitch / bpp) : (uint)width;
            uint bufferImageHeight = srcRowPitch > 0 ? (uint)(srcSlicePitch / srcRowPitch) : (uint)height;

            uint baseArrayLayer = 0;
            int imageZ = 0;
            if (dstTex.TextureDesc.Value.Dimension == RHIResourceDimension.Texture3D)
                imageZ = dstZ;
            else
                baseArrayLayer = (uint)dstZ;

            BufferImageCopy copyRegion = new()
            {
                BufferOffset = srcOffset,
                BufferRowLength = bufferRowLength,
                BufferImageHeight = bufferImageHeight,
                ImageSubresource = new ImageSubresourceLayers
                {
                    AspectMask = VkFormatMapper.GetAspectMask(
                        VkFormatMapper.ToVkFormat(dstTex.TextureDesc.Value.Format)),
                    MipLevel = 0,
                    BaseArrayLayer = baseArrayLayer,
                    LayerCount = 1
                },
                ImageOffset = new Offset3D(dstX, dstY, imageZ),
                ImageExtent = new Extent3D((uint)width, (uint)height, (uint)depth)
            };

            // Transition to transfer dst
            if (dstTex.CurrentLayout != ImageLayout.TransferDstOptimal)
            {
                ImageMemoryBarrier barrier = new()
                {
                    SType = StructureType.ImageMemoryBarrier,
                    SrcAccessMask = AccessFlags.None,
                    DstAccessMask = AccessFlags.TransferWriteBit,
                    OldLayout = dstTex.CurrentLayout,
                    NewLayout = ImageLayout.TransferDstOptimal,
                    SrcQueueFamilyIndex = QueueFamilyIgnored,
                    DstQueueFamilyIndex = QueueFamilyIgnored,
                    Image = dstTex.VkImage,
                    SubresourceRange = new ImageSubresourceRange
                    {
                        AspectMask = VkFormatMapper.GetAspectMask(
                            VkFormatMapper.ToVkFormat(dstTex.TextureDesc.Value.Format)),
                        BaseMipLevel = 0,
                        LevelCount = (uint)dstTex.TextureDesc.Value.MipLevels,
                        BaseArrayLayer = 0,
                        LayerCount = (uint)(dstTex.TextureDesc.Value.Dimension == RHIResourceDimension.Texture3D
                            ? 1 : dstTex.TextureDesc.Value.DepthOrArraySize)
                    }
                };

                Device.Vk.CmdPipelineBarrier(CmdBuffer,
                    PipelineStageFlags.TopOfPipeBit, PipelineStageFlags.TransferBit, 0,
                    0, null, 0, null, 1, &barrier);
                dstTex.CurrentLayout = ImageLayout.TransferDstOptimal;
            }

            Device.Vk.CmdCopyBufferToImage(CmdBuffer, srcBuf.VkBuffer, dstTex.VkImage,
                ImageLayout.TransferDstOptimal, 1, &copyRegion);
        }

        public override void CopyResource(RHIResource dst, RHIResource src)
        {
            if (dst is VulkanBuffer dstBuf && src is VulkanBuffer srcBuf)
            {
                Device.Vk.CmdCopyBuffer(CmdBuffer, srcBuf.VkBuffer, dstBuf.VkBuffer, 1,
                    new BufferCopy(0, 0, dstBuf.Allocation.Size));
            }
            else if (dst is VulkanTexture dstTex && src is VulkanTexture srcTex)
            {
                // Transition to transfer layouts
                TransitionImageLayout(srcTex, srcTex.CurrentLayout, ImageLayout.TransferSrcOptimal);
                TransitionImageLayout(dstTex, dstTex.CurrentLayout, ImageLayout.TransferDstOptimal);

                Format fmt = VkFormatMapper.ToVkFormat(dstTex.TextureDesc!.Value.Format);
                ImageCopy copyRegion = new()
                {
                    SrcSubresource = new ImageSubresourceLayers
                    {
                        AspectMask = VkFormatMapper.GetAspectMask(fmt),
                        MipLevel = 0,
                        BaseArrayLayer = 0,
                        LayerCount = 1
                    },
                    SrcOffset = new Offset3D(0, 0, 0),
                    DstSubresource = new ImageSubresourceLayers
                    {
                        AspectMask = VkFormatMapper.GetAspectMask(fmt),
                        MipLevel = 0,
                        BaseArrayLayer = 0,
                        LayerCount = 1
                    },
                    DstOffset = new Offset3D(0, 0, 0),
                    Extent = new Extent3D((uint)dstTex.TextureDesc.Value.Width,
                        (uint)dstTex.TextureDesc.Value.Height, 1)
                };

                Device.Vk.CmdCopyImage(CmdBuffer, srcTex.VkImage, ImageLayout.TransferSrcOptimal,
                    dstTex.VkImage, ImageLayout.TransferDstOptimal, 1, &copyRegion);
            }
        }

        /// <summary>Vulkan 扩展：更新子资源数据</summary>
        public new void UpdateSubresources<T>(RHIResource dst, int subresource, T[] data) where T : struct
        {
            if (data == null || data.Length == 0) return;

            int elementSize = Marshal.SizeOf<T>();
            ulong totalBytes = (ulong)(data.Length * elementSize);

            if (dst is VulkanBuffer dstBuf)
            {
                void* mapped = dstBuf.GetMappedPointer();
                if (mapped != null)
                {
                    GCHandle handle = GCHandle.Alloc(data, GCHandleType.Pinned);
                    try
                    {
                        System.Buffer.MemoryCopy((void*)handle.AddrOfPinnedObject(),
                            mapped, (long)dstBuf.Allocation.Size, (long)totalBytes);
                    }
                    finally
                    {
                        handle.Free();
                    }
                }
                else
                {
                    // Use staging buffer
                    var staging = new VulkanBuffer(Device, new RHIBufferDesc
                    {
                        SizeInBytes = totalBytes,
                        HeapType = RHIResourceHeapType.Upload
                    });

                    void* stagingPtr = staging.GetMappedPointer();
                    GCHandle handle = GCHandle.Alloc(data, GCHandleType.Pinned);
                    try
                    {
                        System.Buffer.MemoryCopy((void*)handle.AddrOfPinnedObject(),
                            stagingPtr, (long)totalBytes, (long)totalBytes);
                    }
                    finally
                    {
                        handle.Free();
                    }

                    CopyBufferRegion(dstBuf, 0, staging, 0, totalBytes);
                }
            }
            else if (dst is VulkanTexture dstTex)
            {
                // Create staging buffer and copy
                var staging = new VulkanBuffer(Device, new RHIBufferDesc
                {
                    SizeInBytes = totalBytes,
                    HeapType = RHIResourceHeapType.Upload
                });

                void* stagingPtr = staging.GetMappedPointer();
                GCHandle handle = GCHandle.Alloc(data, GCHandleType.Pinned);
                try
                {
                    System.Buffer.MemoryCopy((void*)handle.AddrOfPinnedObject(),
                        stagingPtr, (long)totalBytes, (long)totalBytes);
                }
                finally
                {
                    handle.Free();
                }

                int width = dstTex.TextureDesc!.Value.Width;
                int height = dstTex.TextureDesc.Value.Height;
                int bpp = RHIFormatUtils.BytesPerPixel(dstTex.TextureDesc.Value.Format);
                int rowPitch = width * bpp;

                CopyTextureRegion(dstTex, 0, 0, 0, staging, 0, rowPitch, rowPitch * height,
                    width, height, 1);
            }
        }

        // ── Clear Operations ──

        /// <summary>抽象方法实现：ClearRenderTargetView (in Vector4)</summary>
        public override void ClearRenderTargetView(RHIDescriptorHandle rtvHandle, in Vector4 clearColor)
        {
            // 创建局部副本以避免重载解析匹配到 in 版本导致递归
            Vector4 color = clearColor;
            ClearRenderTargetView(rtvHandle, color);
        }

        public override void ClearRenderTargetView(RHIDescriptorHandle rtv, Vector4 color)
        {
            var tex = Device.DescriptorManager.GetRTVTexture(rtv);
            if (tex == null) return;

            if (_isRendering)
            {
                int attachmentIndex = -1;
                for (int i = 0; i < _currentRTVs.Length; i++)
                {
                    if (_currentRTVs[i] == rtv) { attachmentIndex = i; break; }
                }

                if (attachmentIndex >= 0)
                {
                    ClearAttachment clearAttachment = new()
                    {
                        AspectMask = ImageAspectFlags.ColorBit,
                        ColorAttachment = (uint)attachmentIndex,
                        ClearValue = new ClearValue
                        {
                            Color = new Silk.NET.Vulkan.ClearColorValue(color.X, color.Y, color.Z, color.W)
                        }
                    };

                    ClearRect clearRect = new()
                    {
                        Rect = new Rect2D(new Offset2D(0, 0),
                            new Extent2D((uint)_viewport.Width, (uint)_viewport.Height)),
                        BaseArrayLayer = 0,
                        LayerCount = 1
                    };

                    Device.Vk.CmdClearAttachments(CmdBuffer, 1, &clearAttachment, 1, &clearRect);
                    return;
                }
            }

            // Outside rendering: clear image directly
            Silk.NET.Vulkan.ClearColorValue clearColor = new(color.X, color.Y, color.Z, color.W);
            ImageSubresourceRange range = new()
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = 0,
                LevelCount = (uint)tex.TextureDesc!.Value.MipLevels,
                BaseArrayLayer = 0,
                LayerCount = (uint)(tex.TextureDesc.Value.Dimension == RHIResourceDimension.Texture3D
                    ? 1 : tex.TextureDesc.Value.DepthOrArraySize)
            };

            ImageLayout oldLayout = tex.CurrentLayout;

            // Transition to TransferDstOptimal
            TransitionImageLayout(tex, oldLayout, ImageLayout.TransferDstOptimal);

            Device.Vk.CmdClearColorImage(CmdBuffer, tex.VkImage, ImageLayout.TransferDstOptimal,
                &clearColor, 1, &range);

            // Transition back to old layout (or ColorAttachmentOptimal for RT)
            TransitionImageLayout(tex, ImageLayout.TransferDstOptimal,
                oldLayout == ImageLayout.Undefined ? ImageLayout.ColorAttachmentOptimal : oldLayout);
        }

        /// <summary>抽象方法实现：ClearDepthStencilView (bool clearDepth/clearStencil)</summary>
        public override void ClearDepthStencilView(RHIDescriptorHandle dsvHandle, bool clearDepth, bool clearStencil, float depth, byte stencil)
        {
            int flags = (clearDepth ? 1 : 0) | (clearStencil ? 2 : 0);
            ClearDepthStencilView(dsvHandle, depth, stencil, flags);
        }

        public override void ClearDepthStencilView(RHIDescriptorHandle dsv, float depth, byte stencil, int flags = 3)
        {
            var tex = Device.DescriptorManager.GetDSVTexture(dsv);
            if (tex == null) return;

            if (_isRendering)
            {
                ImageAspectFlags aspect = 0;
                if ((flags & 1) != 0) aspect |= ImageAspectFlags.DepthBit;
                if ((flags & 2) != 0) aspect |= ImageAspectFlags.StencilBit;

                ClearAttachment clearAttachment = new()
                {
                    AspectMask = aspect,
                    ClearValue = new ClearValue
                    {
                        DepthStencil = new ClearDepthStencilValue(depth, stencil)
                    }
                };

                ClearRect clearRect = new()
                {
                    Rect = new Rect2D(new Offset2D(0, 0),
                        new Extent2D((uint)_viewport.Width, (uint)_viewport.Height)),
                    BaseArrayLayer = 0,
                    LayerCount = 1
                };

                Device.Vk.CmdClearAttachments(CmdBuffer, 1, &clearAttachment, 1, &clearRect);
                return;
            }

            // Outside rendering
            ClearDepthStencilValue clearValue = new(depth, stencil);
            Format fmt = VkFormatMapper.ToVkFormat(tex.TextureDesc!.Value.Format);
            ImageSubresourceRange range = new()
            {
                AspectMask = VkFormatMapper.GetAspectMask(fmt),
                BaseMipLevel = 0,
                LevelCount = (uint)tex.TextureDesc.Value.MipLevels,
                BaseArrayLayer = 0,
                LayerCount = (uint)(tex.TextureDesc.Value.Dimension == RHIResourceDimension.Texture3D
                    ? 1 : tex.TextureDesc.Value.DepthOrArraySize)
            };

            ImageLayout oldLayout = tex.CurrentLayout;

            TransitionImageLayout(tex, oldLayout, ImageLayout.TransferDstOptimal);
            Device.Vk.CmdClearDepthStencilImage(CmdBuffer, tex.VkImage,
                ImageLayout.TransferDstOptimal, &clearValue, 1, &range);
            TransitionImageLayout(tex, ImageLayout.TransferDstOptimal,
                oldLayout == ImageLayout.Undefined ? ImageLayout.DepthStencilAttachmentOptimal : oldLayout);
        }

        // ── Render Pass / Dynamic Rendering ──

        public override void OMSetRenderTargets(RHIDescriptorHandle[] rtvs, RHIDescriptorHandle? dsv)
        {
            if (_isRendering)
            {
                Device.Vk.CmdEndRendering(CmdBuffer);
                _isRendering = false;
            }

            _currentRTVs = rtvs ?? Array.Empty<RHIDescriptorHandle>();
            _currentDSV = dsv;

            if (_currentRTVs.Length == 0 && !dsv.HasValue)
                return;

            // Build rendering info
            Span<RenderingAttachmentInfo> colorAttachments = stackalloc RenderingAttachmentInfo[
                _currentRTVs.Length > 0 ? _currentRTVs.Length : 1];
            RenderingAttachmentInfo depthAttachment = default;
            RenderingAttachmentInfo* pDepthAttachment = null;

            for (int i = 0; i < _currentRTVs.Length; i++)
            {
                var tex = Device.DescriptorManager.GetRTVTexture(_currentRTVs[i]);
                ImageView view = Device.DescriptorManager.GetRTV(_currentRTVs[i]);

                if (tex != null && tex.CurrentLayout != ImageLayout.ColorAttachmentOptimal)
                {
                    TransitionImageLayout(tex, tex.CurrentLayout, ImageLayout.ColorAttachmentOptimal);
                }

                colorAttachments[i] = new RenderingAttachmentInfo
                {
                    SType = StructureType.RenderingAttachmentInfo,
                    ImageView = view,
                    ImageLayout = ImageLayout.ColorAttachmentOptimal,
                    LoadOp = AttachmentLoadOp.Load,
                    StoreOp = AttachmentStoreOp.Store
                };
            }

            if (dsv.HasValue)
            {
                var tex = Device.DescriptorManager.GetDSVTexture(dsv.Value);
                ImageView view = Device.DescriptorManager.GetDSV(dsv.Value);

                if (tex != null && tex.CurrentLayout != ImageLayout.DepthStencilAttachmentOptimal)
                {
                    TransitionImageLayout(tex, tex.CurrentLayout, ImageLayout.DepthStencilAttachmentOptimal);
                }

                depthAttachment = new RenderingAttachmentInfo
                {
                    SType = StructureType.RenderingAttachmentInfo,
                    ImageView = view,
                    ImageLayout = ImageLayout.DepthStencilAttachmentOptimal,
                    LoadOp = AttachmentLoadOp.Load,
                    StoreOp = AttachmentStoreOp.Store
                };
                pDepthAttachment = &depthAttachment;
            }

            Rect2D renderArea = new(
                new Offset2D(0, 0),
                new Extent2D((uint)_viewport.Width, (uint)_viewport.Height));

            fixed (RenderingAttachmentInfo* pColorAttachments = colorAttachments)
            {
                RenderingInfo renderingInfo = new()
                {
                    SType = StructureType.RenderingInfo,
                    RenderArea = renderArea,
                    LayerCount = 1,
                    ColorAttachmentCount = (uint)_currentRTVs.Length,
                    PColorAttachments = _currentRTVs.Length > 0 ? pColorAttachments : null,
                    PDepthAttachment = pDepthAttachment,
                    PStencilAttachment = null
                };

                Device.Vk.CmdBeginRendering(CmdBuffer, &renderingInfo);
            }

            _isRendering = true;

            // Re-apply dynamic state
            if (_viewportDirty) ApplyViewport();
            if (_scissorDirty) ApplyScissor();
        }

        // ── Viewport / Scissor (抽象方法实现) ──

        public override void RSSetViewport(float x, float y, float width, float height, float minDepth, float maxDepth)
        {
            _viewport = new RHIViewport(x, y, width, height, minDepth, maxDepth);
            _viewportDirty = true;
            if (_isRendering) ApplyViewport();
        }

        public override void RSSetViewports(RHIViewport[] viewports)
        {
            if (viewports != null && viewports.Length > 0)
            {
                _viewport = viewports[0];
                _viewportDirty = true;
                if (_isRendering) ApplyViewport();
            }
        }

        public override void RSSetScissorRect(int left, int top, int right, int bottom)
        {
            _scissorRect = new RHIScissorRect(left, top, right, bottom);
            _scissorDirty = true;
            if (_isRendering) ApplyScissor();
        }

        public override void RSSetScissorRects(RHIRect[] rects)
        {
            if (rects != null && rects.Length > 0)
            {
                _scissorRect = new RHIScissorRect(rects[0].Left, rects[0].Top, rects[0].Right, rects[0].Bottom);
                _scissorDirty = true;
                if (_isRendering) ApplyScissor();
            }
        }

        // ── Viewport / Scissor 便捷重载（Vulkan 扩展，基类中为非虚方法）──

        public new void SetViewport(RHIViewport viewport)
        {
            _viewport = viewport;
            _viewportDirty = true;
            if (_isRendering) ApplyViewport();
        }

        public new void SetViewports(RHIViewport[] viewports)
        {
            if (viewports != null && viewports.Length > 0)
                SetViewport(viewports[0]);
        }

        public new void SetScissorRect(RHIScissorRect rect)
        {
            _scissorRect = rect;
            _scissorDirty = true;
            if (_isRendering) ApplyScissor();
        }

        public new void SetScissorRects(RHIScissorRect[] rects)
        {
            if (rects != null && rects.Length > 0)
                SetScissorRect(rects[0]);
        }

        private void ApplyViewport()
        {
            Viewport viewport = new()
            {
                X = _viewport.X,
                Y = _viewport.Y,
                Width = _viewport.Width,
                Height = _viewport.Height,
                MinDepth = _viewport.MinDepth,
                MaxDepth = _viewport.MaxDepth
            };
            Device.Vk.CmdSetViewport(CmdBuffer, 0, 1, &viewport);
            _viewportDirty = false;
        }

        private void ApplyScissor()
        {
            Rect2D scissor = new(
                new Offset2D(_scissorRect.Left, _scissorRect.Top),
                new Extent2D((uint)(_scissorRect.Right - _scissorRect.Left),
                    (uint)(_scissorRect.Bottom - _scissorRect.Top)));
            Device.Vk.CmdSetScissor(CmdBuffer, 0, 1, &scissor);
            _scissorDirty = false;
        }

        // ── Pipeline Binding ──

        public override void SetPipelineState(RHIPipelineState pso)
        {
            _currentPSO = (VulkanPipelineState)pso;
            Device.Vk.CmdBindPipeline(CmdBuffer, _currentPSO.BindPoint, _currentPSO.VkPipeline);
            _currentBindPoint = _currentPSO.BindPoint;

            if (_currentPSO.BindPoint == PipelineBindPoint.Graphics)
                _currentGraphicsRoot = null; // Will need to be set explicitly
            else
                _currentComputeRoot = null;
        }

        public override void SetGraphicsRootSignature(RHIRootSignature rootSig)
        {
            _currentGraphicsRoot = (VulkanRootSignature)rootSig;
            _currentBindPoint = PipelineBindPoint.Graphics;

            // Bind descriptor sets
            BindDescriptorSets(_currentGraphicsRoot, PipelineBindPoint.Graphics);
        }

        public override void SetComputeRootSignature(RHIRootSignature rootSig)
        {
            _currentComputeRoot = (VulkanRootSignature)rootSig;
            _currentBindPoint = PipelineBindPoint.Compute;

            BindDescriptorSets(_currentComputeRoot, PipelineBindPoint.Compute);
        }

        private void BindDescriptorSets(VulkanRootSignature rootSig, PipelineBindPoint bindPoint)
        {
            if (rootSig.SetLayouts == null || rootSig.SetLayouts.Length == 0) return;

            // Bind bindless descriptor set (set 0)
            DescriptorSet bindlessSet = Device.DescriptorManager.BindlessSet;
            DescriptorSet samplerSet = Device.DescriptorManager.SamplerSet;

            if (rootSig.SetLayouts.Length >= 1 && bindlessSet.Handle != 0)
            {
                DescriptorSet* pSet = &bindlessSet;
                {
                    Device.Vk.CmdBindDescriptorSets(CmdBuffer, bindPoint,
                        rootSig.VkPipelineLayout, 0, 1, pSet, 0, null);
                }
            }

            if (rootSig.SetLayouts.Length >= 2 && samplerSet.Handle != 0)
            {
                DescriptorSet* pSet = &samplerSet;
                {
                    Device.Vk.CmdBindDescriptorSets(CmdBuffer, bindPoint,
                        rootSig.VkPipelineLayout, 1, 1, pSet, 0, null);
                }
            }
        }

        // ── Root Descriptor Tables ──

        /// <summary>抽象方法实现：SetGraphicsRootDescriptorTable (uint)</summary>
        public override void SetGraphicsRootDescriptorTable(uint rootParameterIndex, RHIDescriptorHandle handle)
        {
            SetGraphicsRootDescriptorTable((int)rootParameterIndex, handle);
        }

        public override void SetGraphicsRootDescriptorTable(int rootParamIndex, RHIDescriptorHandle handle)
        {
            // In bindless mode, the descriptor index is passed as a push constant
            int index = handle.Index;
            SetRoot32BitConstants(rootParamIndex, 1, ref index, 0);
        }

        /// <summary>抽象方法实现：SetGraphicsRootConstantBufferView (uint, RHIBuffer)</summary>
        public override void SetGraphicsRootConstantBufferView(uint rootParameterIndex, RHIBuffer buffer)
        {
            SetGraphicsRootConstantBufferView((int)rootParameterIndex, buffer.GpuVirtualAddress);
        }

        /// <summary>虚拟方法 override：SetGraphicsRootConstantBufferView (uint, ulong gpuAddress)</summary>
        public override void SetGraphicsRootConstantBufferView(uint rootParameterIndex, ulong gpuAddress)
        {
            SetGraphicsRootConstantBufferView((int)rootParameterIndex, gpuAddress);
        }

        /// <summary>虚拟方法 override：SetGraphicsRootShaderResourceView (uint, RHIBuffer)</summary>
        public override void SetGraphicsRootShaderResourceView(uint rootParameterIndex, RHIBuffer buffer)
        {
            SetGraphicsRootShaderResourceView((int)rootParameterIndex, buffer.GpuVirtualAddress);
        }

        /// <summary>虚拟方法 override：SetGraphicsRootShaderResourceView (uint, ulong gpuAddress)</summary>
        public override void SetGraphicsRootShaderResourceView(uint rootParameterIndex, ulong gpuAddress)
        {
            SetGraphicsRootShaderResourceView((int)rootParameterIndex, gpuAddress);
        }

        /// <summary>抽象方法实现：SetGraphicsRootUnorderedAccessView (uint, RHIBuffer)</summary>
        public override void SetGraphicsRootUnorderedAccessView(uint rootParameterIndex, RHIBuffer buffer)
        {
            SetGraphicsRootUnorderedAccessView((int)rootParameterIndex, buffer.GpuVirtualAddress);
        }

        /// <summary>虚拟方法 override：SetGraphicsRootUnorderedAccessView (uint, ulong gpuAddress)</summary>
        public override void SetGraphicsRootUnorderedAccessView(uint rootParameterIndex, ulong gpuAddress)
        {
            SetGraphicsRootUnorderedAccessView((int)rootParameterIndex, gpuAddress);
        }

        // ── Vulkan 扩展：基于 GPU 地址的 root view 绑定（通过 push constants）──

        public new void SetGraphicsRootConstantBufferView(int rootParamIndex, ulong gpuVA)
        {
            SetRoot32BitConstants(rootParamIndex, 2, ref gpuVA, 0);
        }

        public new void SetGraphicsRootShaderResourceView(int rootParamIndex, ulong gpuVA)
        {
            SetRoot32BitConstants(rootParamIndex, 2, ref gpuVA, 0);
        }

        public new void SetGraphicsRootUnorderedAccessView(int rootParamIndex, ulong gpuVA)
        {
            SetRoot32BitConstants(rootParamIndex, 2, ref gpuVA, 0);
        }

        /// <summary>抽象方法实现：SetComputeRootDescriptorTable (uint)</summary>
        public override void SetComputeRootDescriptorTable(uint rootParameterIndex, RHIDescriptorHandle handle)
        {
            SetComputeRootDescriptorTable((int)rootParameterIndex, handle);
        }

        public override void SetComputeRootDescriptorTable(int rootParamIndex, RHIDescriptorHandle handle)
        {
            int index = handle.Index;
            SetRoot32BitConstants(rootParamIndex, 1, ref index, 0);
        }

        /// <summary>抽象方法实现：SetComputeRootConstantBufferView (uint, RHIBuffer)</summary>
        public override void SetComputeRootConstantBufferView(uint rootParameterIndex, RHIBuffer buffer)
        {
            SetComputeRootConstantBufferView((int)rootParameterIndex, buffer.GpuVirtualAddress);
        }

        /// <summary>虚拟方法 override：SetComputeRootConstantBufferView (uint, ulong)</summary>
        public override void SetComputeRootConstantBufferView(uint rootParameterIndex, ulong gpuAddress)
        {
            SetComputeRootConstantBufferView((int)rootParameterIndex, gpuAddress);
        }

        /// <summary>虚拟方法 override：SetComputeRootShaderResourceView (uint, RHIBuffer)</summary>
        public override void SetComputeRootShaderResourceView(uint rootParameterIndex, RHIBuffer buffer)
        {
            SetComputeRootShaderResourceView((int)rootParameterIndex, buffer.GpuVirtualAddress);
        }

        /// <summary>虚拟方法 override：SetComputeRootShaderResourceView (uint, ulong)</summary>
        public override void SetComputeRootShaderResourceView(uint rootParameterIndex, ulong gpuAddress)
        {
            SetComputeRootShaderResourceView((int)rootParameterIndex, gpuAddress);
        }

        /// <summary>虚拟方法 override：SetComputeRootUnorderedAccessView (uint, RHIBuffer)</summary>
        public override void SetComputeRootUnorderedAccessView(uint rootParameterIndex, RHIBuffer buffer)
        {
            SetComputeRootUnorderedAccessView((int)rootParameterIndex, buffer.GpuVirtualAddress);
        }

        /// <summary>虚拟方法 override：SetComputeRootUnorderedAccessView (uint, ulong)</summary>
        public override void SetComputeRootUnorderedAccessView(uint rootParameterIndex, ulong gpuAddress)
        {
            SetComputeRootUnorderedAccessView((int)rootParameterIndex, gpuAddress);
        }

        // ── Vulkan 扩展：基于 GPU 地址的 compute root view 绑定 ──

        public new void SetComputeRootConstantBufferView(int rootParamIndex, ulong gpuVA)
        {
            SetRoot32BitConstants(rootParamIndex, 2, ref gpuVA, 0);
        }

        public new void SetComputeRootShaderResourceView(int rootParamIndex, ulong gpuVA)
        {
            SetRoot32BitConstants(rootParamIndex, 2, ref gpuVA, 0);
        }

        public new void SetComputeRootUnorderedAccessView(int rootParamIndex, ulong gpuVA)
        {
            SetRoot32BitConstants(rootParamIndex, 2, ref gpuVA, 0);
        }

        /// <summary>抽象方法实现：SetGraphicsRoot32BitConstants (泛型, in T)</summary>
        public override unsafe void SetGraphicsRoot32BitConstants<T>(uint rootParameterIndex, uint num32BitValues, in T data, uint destOffsetIn32BitValues)
        {
            fixed (T* pData = &data)
            {
                SetGraphicsRoot32BitConstants((int)rootParameterIndex, (int)num32BitValues,
                    (IntPtr)pData, (int)destOffsetIn32BitValues);
            }
        }

        /// <summary>Vulkan 扩展：SetGraphicsRoot32BitConstants (IntPtr 版本)</summary>
        public new void SetGraphicsRoot32BitConstants(int rootParamIndex, int num32BitValues, IntPtr data, int destOffset)
        {
            var rootSig = _currentGraphicsRoot;
            if (rootSig == null) return;

            uint offset = GetPushConstantOffset(rootSig, rootParamIndex) + (uint)(destOffset * 4);
            uint size = (uint)(num32BitValues * 4);

            if (offset + size > (uint)_pushConstants.Length)
            {
                Array.Resize(ref _pushConstants, (int)(offset + size + 64));
            }

            Marshal.Copy(data, _pushConstants, (int)offset, (int)size);

            ShaderStageFlags stageFlags = _currentBindPoint == PipelineBindPoint.Compute
                ? ShaderStageFlags.ComputeBit
                : ShaderStageFlags.AllGraphics;

            fixed (byte* pData = _pushConstants)
            {
                Device.Vk.CmdPushConstants(CmdBuffer, rootSig.VkPipelineLayout,
                    stageFlags, offset, size, pData + offset);
            }
        }

        /// <summary>抽象方法实现：SetComputeRoot32BitConstants (泛型, in T)</summary>
        public override unsafe void SetComputeRoot32BitConstants<T>(uint rootParameterIndex, uint num32BitValues, in T data, uint destOffsetIn32BitValues)
        {
            fixed (T* pData = &data)
            {
                SetComputeRoot32BitConstants((int)rootParameterIndex, (int)num32BitValues,
                    (IntPtr)pData, (int)destOffsetIn32BitValues);
            }
        }

        /// <summary>Vulkan 扩展：SetComputeRoot32BitConstants (IntPtr 版本)</summary>
        public new void SetComputeRoot32BitConstants(int rootParamIndex, int num32BitValues, IntPtr data, int destOffset)
        {
            var rootSig = _currentComputeRoot;
            if (rootSig == null) return;

            uint offset = GetPushConstantOffset(rootSig, rootParamIndex) + (uint)(destOffset * 4);
            uint size = (uint)(num32BitValues * 4);

            if (offset + size > (uint)_pushConstants.Length)
            {
                Array.Resize(ref _pushConstants, (int)(offset + size + 64));
            }

            Marshal.Copy(data, _pushConstants, (int)offset, (int)size);

            fixed (byte* pData = _pushConstants)
            {
                Device.Vk.CmdPushConstants(CmdBuffer, rootSig.VkPipelineLayout,
                    ShaderStageFlags.ComputeBit, offset, size, pData + offset);
            }
        }

        private void SetRoot32BitConstants<T>(int rootParamIndex, int num32BitValues, ref T data, int destOffset) where T : struct
        {
            var rootSig = _currentBindPoint == PipelineBindPoint.Compute
                ? _currentComputeRoot : _currentGraphicsRoot;
            if (rootSig == null) return;

            uint offset = GetPushConstantOffset(rootSig, rootParamIndex) + (uint)(destOffset * 4);
            uint size = (uint)(num32BitValues * 4);

            if (offset + size > (uint)_pushConstants.Length)
            {
                Array.Resize(ref _pushConstants, (int)(offset + size + 64));
            }

            GCHandle handle = GCHandle.Alloc(data, GCHandleType.Pinned);
            try
            {
                Marshal.Copy(handle.AddrOfPinnedObject(), _pushConstants, (int)offset, (int)size);
            }
            finally
            {
                handle.Free();
            }

            ShaderStageFlags stageFlags = _currentBindPoint == PipelineBindPoint.Compute
                ? ShaderStageFlags.ComputeBit
                : ShaderStageFlags.AllGraphics;

            fixed (byte* pData = _pushConstants)
            {
                Device.Vk.CmdPushConstants(CmdBuffer, rootSig.VkPipelineLayout,
                    stageFlags, offset, size, pData + offset);
            }
        }

        private static uint GetPushConstantOffset(VulkanRootSignature rootSig, int rootParamIndex)
        {
            uint offset = 0;
            for (int i = 0; i < rootParamIndex && i < rootSig.Parameters.Length; i++)
            {
                if (rootSig.Parameters[i].ParameterType == RHIRootParameterType.Constants)
                    offset += (uint)(rootSig.Parameters[i].Constants.Value.Num32BitValues * 4);
                else
                    offset += 8; // CBV/SRV/UAV/DescriptorTable use 8 bytes
            }
            return offset;
        }

        // ── Input Assembler ──

        public override void IASetPrimitiveTopology(RHIPrimitiveTopology topology)
        {
            _topology = VkFormatMapper.ToVkTopology(topology);
        }

        /// <summary>抽象方法实现：IASetVertexBuffers (uint 版本)</summary>
        public override void IASetVertexBuffers(uint startSlot, RHIBuffer buffer, ulong offset, uint stride, uint sizeInBytes)
        {
            IASetVertexBuffers((int)startSlot, buffer, (ulong)stride, offset);
        }

        /// <summary>Vulkan 扩展：IASetVertexBuffers (ulong stride/offset)</summary>
        public new void IASetVertexBuffers(int startSlot, RHIBuffer buffer, ulong stride, ulong offset)
        {
            var vkBuf = (VulkanBuffer)buffer;
            Buffer vkBuffer = vkBuf.VkBuffer;
            Device.Vk.CmdBindVertexBuffers(CmdBuffer, (uint)startSlot, 1, &vkBuffer, &offset);
        }

        /// <summary>抽象方法实现：IASetIndexBuffer</summary>
        public override void IASetIndexBuffer(RHIBuffer buffer, ulong offset, uint sizeInBytes, RHIFormat format)
        {
            IASetIndexBuffer(buffer, format, offset);
        }

        public override void IASetIndexBuffer(RHIBuffer buffer, RHIFormat format, ulong offset)
        {
            var vkBuf = (VulkanBuffer)buffer;
            IndexType indexType = format switch
            {
                RHIFormat.R16_UInt => IndexType.Uint16,
                RHIFormat.R32_UInt => IndexType.Uint32,
                _ => IndexType.Uint32
            };
            Device.Vk.CmdBindIndexBuffer(CmdBuffer, vkBuf.VkBuffer, offset, indexType);
        }

        // ── Draw / Dispatch ──

        /// <summary>抽象方法实现：DrawInstanced (uint)</summary>
        public override void DrawInstanced(uint vertexCountPerInstance, uint instanceCount, uint startVertex, uint startInstance)
        {
            Device.Vk.CmdDraw(CmdBuffer, vertexCountPerInstance, instanceCount, startVertex, startInstance);
        }

        /// <summary>抽象方法实现：DrawIndexedInstanced (uint)</summary>
        public override void DrawIndexedInstanced(uint indexCountPerInstance, uint instanceCount, uint startIndex, int baseVertex, uint startInstance)
        {
            Device.Vk.CmdDrawIndexed(CmdBuffer, indexCountPerInstance, instanceCount,
                startIndex, baseVertex, startInstance);
        }

        /// <summary>抽象方法实现：DispatchMesh (uint)</summary>
        public override void DispatchMesh(uint groupCountX, uint groupCountY, uint groupCountZ)
        {
            if (Device.Vk.TryGetDeviceExtension(Device.VkInstance, Device.VkDevice, out ExtMeshShader meshShaderExt))
                meshShaderExt.CmdDrawMeshTask(CmdBuffer, groupCountX, groupCountY, groupCountZ);
        }

        /// <summary>抽象方法实现：Dispatch (uint)</summary>
        public override void Dispatch(uint groupCountX, uint groupCountY, uint groupCountZ)
        {
            Device.Vk.CmdDispatch(CmdBuffer, groupCountX, groupCountY, groupCountZ);
        }

        // ── Draw / Dispatch int 重载（虚拟方法 override）──

        public override void DrawInstanced(int vertexCount, int instanceCount, int startVertex, int startInstance)
        {
            Device.Vk.CmdDraw(CmdBuffer, (uint)vertexCount, (uint)instanceCount,
                (uint)startVertex, (uint)startInstance);
        }

        public override void DrawIndexedInstanced(int indexCount, int instanceCount, int startIndex, int baseVertex, int startInstance)
        {
            Device.Vk.CmdDrawIndexed(CmdBuffer, (uint)indexCount, (uint)instanceCount,
                (uint)startIndex, (int)baseVertex, (uint)startInstance);
        }

        public override void DrawIndexedInstancedIndirect(RHIBuffer argsBuffer, ulong argsOffset)
        {
            var vkBuf = (VulkanBuffer)argsBuffer;
            Device.Vk.CmdDrawIndexedIndirect(CmdBuffer, vkBuf.VkBuffer, argsOffset, 1, 0);
        }

        public override void DispatchMesh(int groupCountX, int groupCountY, int groupCountZ)
        {
            if (Device.Vk.TryGetDeviceExtension(Device.VkInstance, Device.VkDevice, out ExtMeshShader meshShaderExt))
                meshShaderExt.CmdDrawMeshTask(CmdBuffer, (uint)groupCountX, (uint)groupCountY, (uint)groupCountZ);
        }

        public override void Dispatch(int groupCountX, int groupCountY, int groupCountZ)
        {
            Device.Vk.CmdDispatch(CmdBuffer, (uint)groupCountX, (uint)groupCountY, (uint)groupCountZ);
        }

        public override void DispatchIndirect(RHIBuffer argsBuffer, ulong argsOffset)
        {
            var vkBuf = (VulkanBuffer)argsBuffer;
            Device.Vk.CmdDispatchIndirect(CmdBuffer, vkBuf.VkBuffer, argsOffset);
        }

        // ── Dynamic State ──

        /// <summary>抽象方法实现：OMSetStencilRef</summary>
        public override void OMSetStencilRef(uint refValue)
        {
            Device.Vk.CmdSetStencilReference(CmdBuffer, StencilFaceFlags.FrontAndBack, refValue);
        }

        /// <summary>抽象方法实现：OMSetBlendFactor (in Vector4)</summary>
        public override void OMSetBlendFactor(in Vector4 blendFactor)
        {
            float* factors = stackalloc float[4];
            factors[0] = blendFactor.X;
            factors[1] = blendFactor.Y;
            factors[2] = blendFactor.Z;
            factors[3] = blendFactor.W;
            Device.Vk.CmdSetBlendConstants(CmdBuffer, factors);
        }

        // ── Vulkan 扩展方法（基类中不存在，使用 new 隐藏）──

        /// <summary>Vulkan 扩展：设置模板参考值</summary>
        public new void SetStencilRef(uint refValue)
        {
            Device.Vk.CmdSetStencilReference(CmdBuffer, StencilFaceFlags.FrontAndBack, refValue);
        }

        /// <summary>Vulkan 扩展：设置混合因子</summary>
        public new void SetBlendFactor(Vector4 blendFactor)
        {
            float* factors = stackalloc float[4];
            factors[0] = blendFactor.X;
            factors[1] = blendFactor.Y;
            factors[2] = blendFactor.Z;
            factors[3] = blendFactor.W;
            Device.Vk.CmdSetBlendConstants(CmdBuffer, factors);
        }

        /// <summary>Vulkan 扩展：条件渲染（VK_EXT_conditional_rendering）</summary>
        public new void SetPredication(RHIBuffer buffer, ulong alignedBufferOffset, bool opEqual)
        {
            // VK_EXT_conditional_rendering
            var vkBuf = (VulkanBuffer)buffer;
            ConditionalRenderingBeginInfoEXT condInfo = new()
            {
                SType = StructureType.ConditionalRenderingBeginInfoExt,
                Buffer = vkBuf.VkBuffer,
                Offset = alignedBufferOffset,
                Flags = opEqual ? ConditionalRenderingFlagsEXT.InvertedBitExt : 0
            };

            if (Device.Vk.TryGetDeviceExtension(Device.VkInstance, Device.VkDevice, out ExtConditionalRendering condRenderingExt))
                condRenderingExt.CmdBeginConditionalRendering(CmdBuffer, &condInfo);
            // Note: caller must call CmdEndConditionalRenderingEXT after the predicated commands
            // For a complete implementation, we'd track the conditional rendering state
        }

        // ── Image Layout Transition Helper ──

        private void TransitionImageLayout(VulkanTexture tex, ImageLayout oldLayout, ImageLayout newLayout)
        {
            if (oldLayout == newLayout) return;

            (AccessFlags srcAccess, PipelineStageFlags srcStage) = oldLayout switch
            {
                ImageLayout.Undefined => (AccessFlags.None, PipelineStageFlags.TopOfPipeBit),
                ImageLayout.ColorAttachmentOptimal => (AccessFlags.ColorAttachmentWriteBit, PipelineStageFlags.ColorAttachmentOutputBit),
                ImageLayout.DepthStencilAttachmentOptimal => (AccessFlags.DepthStencilAttachmentWriteBit, PipelineStageFlags.EarlyFragmentTestsBit),
                ImageLayout.ShaderReadOnlyOptimal => (AccessFlags.ShaderReadBit, PipelineStageFlags.FragmentShaderBit),
                ImageLayout.TransferSrcOptimal => (AccessFlags.TransferReadBit, PipelineStageFlags.TransferBit),
                ImageLayout.TransferDstOptimal => (AccessFlags.TransferWriteBit, PipelineStageFlags.TransferBit),
                _ => (AccessFlags.None, PipelineStageFlags.TopOfPipeBit)
            };

            (AccessFlags dstAccess, PipelineStageFlags dstStage) = newLayout switch
            {
                ImageLayout.ColorAttachmentOptimal => (AccessFlags.ColorAttachmentWriteBit | AccessFlags.ColorAttachmentReadBit, PipelineStageFlags.ColorAttachmentOutputBit),
                ImageLayout.DepthStencilAttachmentOptimal => (AccessFlags.DepthStencilAttachmentReadBit | AccessFlags.DepthStencilAttachmentWriteBit, PipelineStageFlags.EarlyFragmentTestsBit),
                ImageLayout.ShaderReadOnlyOptimal => (AccessFlags.ShaderReadBit, PipelineStageFlags.FragmentShaderBit),
                ImageLayout.TransferSrcOptimal => (AccessFlags.TransferReadBit, PipelineStageFlags.TransferBit),
                ImageLayout.TransferDstOptimal => (AccessFlags.TransferWriteBit, PipelineStageFlags.TransferBit),
                ImageLayout.PresentSrcKhr => (AccessFlags.None, PipelineStageFlags.BottomOfPipeBit),
                _ => (AccessFlags.None, PipelineStageFlags.BottomOfPipeBit)
            };

            Format fmt = VkFormatMapper.ToVkFormat(tex.TextureDesc!.Value.Format);

            ImageMemoryBarrier barrier = new()
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = srcAccess,
                DstAccessMask = dstAccess,
                OldLayout = oldLayout,
                NewLayout = newLayout,
                SrcQueueFamilyIndex = QueueFamilyIgnored,
                DstQueueFamilyIndex = QueueFamilyIgnored,
                Image = tex.VkImage,
                SubresourceRange = new ImageSubresourceRange
                {
                    AspectMask = VkFormatMapper.GetAspectMask(fmt),
                    BaseMipLevel = 0,
                    LevelCount = (uint)tex.TextureDesc.Value.MipLevels,
                    BaseArrayLayer = 0,
                    LayerCount = (uint)(tex.TextureDesc.Value.Dimension == RHIResourceDimension.Texture3D
                        ? 1 : tex.TextureDesc.Value.DepthOrArraySize)
                }
            };

            Device.Vk.CmdPipelineBarrier(CmdBuffer, srcStage, dstStage, 0,
                0, null, 0, null, 1, &barrier);

            tex.CurrentLayout = newLayout;
        }

        // ── Disposal ──

        public override void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (Pool.Handle != 0)
            {
                CommandBuffer cmd = CmdBuffer;
                Device.Vk.FreeCommandBuffers(Device.VkDevice, Pool, 1, &cmd);
            }
        }

        private const uint QueueFamilyIgnored = ~0u;
    }
}
