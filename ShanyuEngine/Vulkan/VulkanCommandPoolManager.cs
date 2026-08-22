// =============================================================================
// VulkanCommandPoolManager.cs - 山雨引擎 Vulkan 命令池管理 (每线程独立)
// =============================================================================
// Vulkan 的命令池不是线程安全的，多线程录制命令必须每个线程一个池。
// 这套管理器：
//   1. 用 ThreadLocal 给每个线程一个独立 command pool
//   2. 支持主/瞬态两种池 (transient 用于一次性 copy/staging)
//   3. 命令缓冲对象池 (避免每帧 alloc/free)
//   4. 每帧 reset 一次 transient pool
//
// FIXME: ThreadLocal 的 valueFactory 抛异常的话会很麻烦
// TODO: 加命令缓冲泄漏检测
//
// 作者: 山雨引擎团队
// =============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Silk.NET.Vulkan;

using Buffer = Silk.NET.Vulkan.Buffer;
using Semaphore = Vulkan.Semaphore;
using Vulkan = Silk.NET.Vulkan;
namespace ShanyuEngine.Vulkan
{
    // ════════════════════════════════════════════════════════════════════════════
    // 区域 1: 公共类型
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>命令池类型。</summary>
    public enum CommandPoolType
    {
        /// <summary>主图形/计算命令池，长期复用。</summary>
        Graphics,
        /// <summary>异步计算。</summary>
        Compute,
        /// <summary>传输专用 (copy)。</summary>
        Transfer,
        /// <summary>瞬态命令池 (一次性命令，每帧 reset)。</summary>
        Transient,
    }

    /// <summary>命令缓冲级别。</summary>
    public enum CommandBufferLevelExt
    {
        Primary,
        Secondary,
    }

    /// <summary>命令池统计。</summary>
    public struct CommandPoolStats
    {
        public int TotalPoolsCreated;
        public int ActivePools;
        public int TotalCommandBuffersAllocated;
        public int ActiveCommandBuffers;
        public int PooledCommandBuffers;
        public int ThreadCount;

        public override string ToString()
            => $"Pools: {ActivePools}/{TotalPoolsCreated}, " +
               $"Buffers: alloc={TotalCommandBuffersAllocated}, " +
               $"active={ActiveCommandBuffers}, pooled={PooledCommandBuffers}, " +
               $"threads={ThreadCount}";
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 2: 单个命令池包装
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 单个 VkCommandPool 包装。负责分配/释放/重置 command buffer。
    /// </summary>
    internal sealed unsafe class CommandPoolWrapper : IDisposable
    {
        private readonly Vk _vk;
        private readonly Device _device;
        private readonly Silk.NET.Vulkan.CommandPool _handle;
        private readonly CommandPoolType _type;
        private readonly uint _queueFamilyIndex;
        private readonly Stack<CommandBuffer> _freePrimaries = new();
        private readonly Stack<CommandBuffer> _freeSecondaries = new();
        private readonly HashSet<CommandBuffer> _inUse = new();
        private readonly object _lock = new();
        private int _totalAllocated;

        public Silk.NET.Vulkan.CommandPool Handle => _handle;
        public CommandPoolType Type => _type;
        public uint QueueFamilyIndex => _queueFamilyIndex;
        public int InUseCount { get; private set; }
        public int FreeCount => _freePrimaries.Count + _freeSecondaries.Count;

        internal CommandPoolWrapper(Vk vk, Device device, Silk.NET.Vulkan.CommandPool handle,
                                    CommandPoolType type, uint queueFamilyIndex)
        {
            _vk = vk;
            _device = device;
            _handle = handle;
            _type = type;
            _queueFamilyIndex = queueFamilyIndex;
        }

        /// <summary>分配一个 command buffer (优先从池拿)。</summary>
        public CommandBuffer Allocate(CommandBufferLevelExt level = CommandBufferLevelExt.Primary)
        {
            lock (_lock)
            {
                var stack = level == CommandBufferLevelExt.Primary ? _freePrimaries : _freeSecondaries;
                if (stack.Count > 0)
                {
                    var cb = stack.Pop();
                    _inUse.Add(cb);
                    InUseCount++;
                    return cb;
                }
            }

            var info = new CommandBufferAllocateInfo
            {
                SType = StructureType.CommandBufferAllocateInfo,
                CommandPool = _handle,
                Level = level == CommandBufferLevelExt.Primary ? CommandBufferLevel.Primary : CommandBufferLevel.Secondary,
                CommandBufferCount = 1,
            };
            CommandBuffer cb2;
            var r = _vk.AllocateCommandBuffers(_device, info, &cb2);
            if (r != Result.Success)
                throw new VulkanException(r, "AllocateCommandBuffers 失败");
            lock (_lock)
            {
                _totalAllocated++;
                _inUse.Add(cb2);
                InUseCount++;
            }
            return cb2;
        }

        /// <summary>批量分配。</summary>
        public CommandBuffer[] AllocateMany(int count, CommandBufferLevelExt level = CommandBufferLevelExt.Primary)
        {
            var arr = new CommandBuffer[count];
            for (int i = 0; i < count; i++) arr[i] = Allocate(level);
            return arr;
        }

        /// <summary>释放 command buffer 回池 (不真正 free)。</summary>
        public void Free(CommandBuffer cb, CommandBufferLevelExt level = CommandBufferLevelExt.Primary)
        {
            lock (_lock)
            {
                if (_inUse.Remove(cb))
                {
                    InUseCount--;
                    var stack = level == CommandBufferLevelExt.Primary ? _freePrimaries : _freeSecondaries;
                    stack.Push(cb);
                }
            }
        }

        /// <summary>开始录制。</summary>
        public void BeginRecording(CommandBuffer cb, CommandBufferUsageFlags flags = 0,
                                   CommandBufferInheritanceInfo? inheritance = null)
        {
            var bi = new CommandBufferBeginInfo
            {
                SType = StructureType.CommandBufferBeginInfo,
                Flags = flags,
            };
            if (inheritance.HasValue)
            {
                var inh = inheritance.Value;
                bi.PInheritanceInfo = (CommandBufferInheritanceInfo*)Unsafe.AsPointer(ref inh);
            }
            var r = _vk.BeginCommandBuffer(cb, bi);
            if (r != Result.Success)
                throw new VulkanException(r, "BeginCommandBuffer 失败");
        }

        /// <summary>结束录制。</summary>
        public void EndRecording(CommandBuffer cb)
        {
            var r = _vk.EndCommandBuffer(cb);
            if (r != Result.Success)
                throw new VulkanException(r, "EndCommandBuffer 失败");
        }

        /// <summary>重置单个 command buffer。</summary>
        public void ResetBuffer(CommandBuffer cb, bool releaseResources = false)
        {
            var flags = releaseResources ? CommandBufferResetFlags.ReleaseResourcesBit : 0;
            _vk.ResetCommandBuffer(cb, flags);
        }

        /// <summary>重置整个 pool (释放所有 command buffer)。</summary>
        public void ResetPool(bool releaseResources = false)
        {
            var flags = releaseResources ? CommandPoolResetFlags.ReleaseResourcesBit : 0;
            var r = _vk.ResetCommandPool(_device, _handle, flags);
            if (r != Result.Success)
                throw new VulkanException(r, "ResetCommandPool 失败");
            lock (_lock)
            {
                // reset 后所有 command buffer 都失效了，要清掉池
                _freePrimaries.Clear();
                _freeSecondaries.Clear();
                _inUse.Clear();
                InUseCount = 0;
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                // 把池里的 command buffer 都 free 掉
                // 注意：reset pool 后 in use 的也失效了，不能再单独 free
                _vk.DestroyCommandPool(_device, _handle, null);
                _freePrimaries.Clear();
                _freeSecondaries.Clear();
                _inUse.Clear();
            }
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 3: 线程本地命令池组
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 一个线程的所有命令池 (graphics/compute/transfer/transient)。
    /// </summary>
    internal sealed class ThreadCommandPools : IDisposable
    {
        private readonly Dictionary<CommandPoolType, CommandPoolWrapper> _pools = new();
        private readonly Vk _vk;
        private readonly Device _device;
        private readonly uint _graphicsQueueFamily;
        private readonly uint _computeQueueFamily;
        private readonly uint _transferQueueFamily;
        private readonly object _lock = new();

        public int ThreadId { get; }

        internal ThreadCommandPools(Vk vk, Device device,
                                    uint graphicsQueue, uint computeQueue, uint transferQueue,
                                    int threadId)
        {
            _vk = vk;
            _device = device;
            _graphicsQueueFamily = graphicsQueue;
            _computeQueueFamily = computeQueue;
            _transferQueueFamily = transferQueue;
            ThreadId = threadId;
        }

        public CommandPoolWrapper GetPool(CommandPoolType type)
        {
            lock (_lock)
            {
                if (_pools.TryGetValue(type, out var p)) return p;
                var pool = CreatePool(type);
                _pools[type] = pool;
                return pool;
            }
        }

        private CommandPoolWrapper CreatePool(CommandPoolType type)
        {
            CommandPoolCreateFlags flags = 0;
            uint queueFamily;
            switch (type)
            {
                case CommandPoolType.Graphics:
                    flags = CommandPoolCreateFlags.ResetCommandBufferBit;
                    queueFamily = _graphicsQueueFamily;
                    break;
                case CommandPoolType.Compute:
                    flags = CommandPoolCreateFlags.ResetCommandBufferBit;
                    queueFamily = _computeQueueFamily > 0 ? _computeQueueFamily : _graphicsQueueFamily;
                    break;
                case CommandPoolType.Transfer:
                    flags = CommandPoolCreateFlags.TransientBit;
                    queueFamily = _transferQueueFamily > 0 ? _transferQueueFamily : _graphicsQueueFamily;
                    break;
                case CommandPoolType.Transient:
                    flags = CommandPoolCreateFlags.TransientBit;
                    queueFamily = _graphicsQueueFamily;
                    break;
                default:
                    queueFamily = _graphicsQueueFamily;
                    break;
            }

            var info = new CommandPoolCreateInfo
            {
                SType = StructureType.CommandPoolCreateInfo,
                Flags = flags,
                QueueFamilyIndex = queueFamily,
            };
            Silk.NET.Vulkan.CommandPool handle;
            var r = _vk.CreateCommandPool(_device, info, null, &handle);
            if (r != Result.Success)
                throw new VulkanException(r, $"CreateCommandPool 失败 type={type}");
            return new CommandPoolWrapper(_vk, _device, handle, type, queueFamily);
        }

        /// <summary>重置瞬态池 (每帧调用)。</summary>
        public void ResetTransient()
        {
            lock (_lock)
            {
                if (_pools.TryGetValue(CommandPoolType.Transient, out var p))
                    p.ResetPool();
                if (_pools.TryGetValue(CommandPoolType.Transfer, out var p2))
                    p2.ResetPool();
            }
        }

        /// <summary>从指定类型池分配 command buffer。</summary>
        public CommandBuffer Allocate(CommandPoolType type, CommandBufferLevelExt level = CommandBufferLevelExt.Primary)
        {
            return GetPool(type).Allocate(level);
        }

        /// <summary>释放。</summary>
        public void Free(CommandBuffer cb, CommandPoolType type, CommandBufferLevelExt level = CommandBufferLevelExt.Primary)
        {
            GetPool(type).Free(cb, level);
        }

        public void Dispose()
        {
            lock (_lock)
            {
                foreach (var p in _pools.Values) p.Dispose();
                _pools.Clear();
            }
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 4: 命令池管理器主类
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 命令池管理器主入口。每个线程一个 ThreadCommandPools。
    /// </summary>
    public sealed unsafe class VulkanCommandPoolManager : IDisposable
    {
        private readonly Vk _vk;
        private readonly Device _device;
        private readonly uint _graphicsQueueFamily;
        private readonly uint _computeQueueFamily;
        private readonly uint _transferQueueFamily;

        // 线程本地命令池
        private readonly ThreadLocal<ThreadCommandPools> _tlsPools;
        private readonly List<ThreadCommandPools> _allPools = new();
        private readonly object _allPoolsLock = new();
        private int _threadIdCounter;

        // 统计
        private int _totalPoolsCreated;
        private int _totalBuffersAllocated;
        private long _currentFrame;

        public Vk Vk => _vk;
        public Device Device => _device;

        public VulkanCommandPoolManager(Vk vk, Device device,
                                        uint graphicsQueueFamily,
                                        uint computeQueueFamily = 0,
                                        uint transferQueueFamily = 0)
        {
            _vk = vk;
            _device = device;
            _graphicsQueueFamily = graphicsQueueFamily;
            _computeQueueFamily = computeQueueFamily;
            _transferQueueFamily = transferQueueFamily;

            _tlsPools = new ThreadLocal<ThreadCommandPools>(() =>
            {
                int tid = Interlocked.Increment(ref _threadIdCounter);
                var pools = new ThreadCommandPools(vk, device,
                    graphicsQueueFamily, computeQueueFamily, transferQueueFamily, tid);
                lock (_allPoolsLock) _allPools.Add(pools);
                return pools;
            });
        }

        /// <summary>从当前线程的池分配 command buffer。</summary>
        public CommandBuffer Allocate(CommandPoolType type = CommandPoolType.Graphics,
                                      CommandBufferLevelExt level = CommandBufferLevelExt.Primary)
        {
            var cb = _tlsPools.Value.Allocate(type, level);
            Interlocked.Increment(ref _totalBuffersAllocated);
            return cb;
        }

        /// <summary>释放 command buffer 回当前线程的池。</summary>
        public void Free(CommandBuffer cb, CommandPoolType type = CommandPoolType.Graphics,
                         CommandBufferLevelExt level = CommandBufferLevelExt.Primary)
        {
            _tlsPools.Value.Free(cb, type, level);
        }

        /// <summary>开始录制。</summary>
        public void BeginRecording(CommandBuffer cb, CommandBufferUsageFlags flags = 0)
        {
            // 找到对应的 pool 来 begin - 简化，用 graphics pool
            _tlsPools.Value.GetPool(CommandPoolType.Graphics).BeginRecording(cb, flags);
        }

        /// <summary>结束录制。</summary>
        public void EndRecording(CommandBuffer cb)
        {
            _tlsPools.Value.GetPool(CommandPoolType.Graphics).EndRecording(cb);
        }

        /// <summary>重置单个 command buffer。</summary>
        public void ResetBuffer(CommandBuffer cb, bool releaseResources = false)
        {
            _tlsPools.Value.GetPool(CommandPoolType.Graphics).ResetBuffer(cb, releaseResources);
        }

        /// <summary>每帧开始：重置当前线程的 transient 池。</summary>
        public void OnFrameBegin(long frameIndex)
        {
            _currentFrame = frameIndex;
            _tlsPools.Value.ResetTransient();
        }

        /// <summary>重置所有线程的 transient 池 (主线程调用，确保 GPU 完成)。</summary>
        public void ResetAllTransient()
        {
            lock (_allPoolsLock)
            {
                foreach (var p in _allPools) p.ResetTransient();
            }
        }

        /// <summary>获取/创建一个"一次性命令缓冲"helper (submit 后自动 free)。</summary>
        public OneTimeCommand BeginOneTime(CommandPoolType type = CommandPoolType.Transfer)
        {
            var cb = Allocate(type);
            BeginRecording(cb, CommandBufferUsageFlags.OneTimeSubmitBit);
            return new OneTimeCommand(this, cb, type);
        }

        /// <summary>获取当前线程的 pool wrapper。</summary>
        public CommandPoolWrapper GetCurrentThreadPool(CommandPoolType type)
        {
            return _tlsPools.Value.GetPool(type);
        }

        /// <summary>获取统计。</summary>
        public CommandPoolStats GetStats()
        {
            var s = new CommandPoolStats
            {
                TotalPoolsCreated = _totalPoolsCreated,
                TotalCommandBuffersAllocated = _totalBuffersAllocated,
            };
            lock (_allPoolsLock)
            {
                s.ThreadCount = _allPools.Count;
                s.ActivePools = 0;
                s.ActiveCommandBuffers = 0;
                s.PooledCommandBuffers = 0;
                foreach (var p in _allPools)
                {
                    s.ActivePools++;
                    // 这里没暴露每个 pool 的统计，简化
                }
            }
            return s;
        }

        public void Dispose()
        {
            lock (_allPoolsLock)
            {
                foreach (var p in _allPools) p.Dispose();
                _allPools.Clear();
            }
            _tlsPools.Dispose();
        }
    }

    /// <summary>
    /// 一次性命令缓冲 helper。using 块结束自动 submit + free。
    /// 典型用法：上传数据、layout transition。
    /// </summary>
    public readonly struct OneTimeCommand : IDisposable
    {
        private readonly VulkanCommandPoolManager _mgr;
        private readonly CommandBuffer _cb;
        private readonly CommandPoolType _type;
        private bool _submitted;

        public CommandBuffer CommandBuffer => _cb;

        internal OneTimeCommand(VulkanCommandPoolManager mgr, CommandBuffer cb, CommandPoolType type)
        {
            _mgr = mgr;
            _cb = cb;
            _type = type;
            _submitted = false;
        }

        /// <summary>结束并提交到队列。</summary>
        public void EndAndSubmit(Queue queue)
        {
            if (_submitted) return;
            _mgr.EndRecording(_cb);
            var si = new SubmitInfo
            {
                SType = StructureType.SubmitInfo,
                CommandBufferCount = 1,
                PCommandBuffers = (CommandBuffer*)Unsafe.AsPointer(ref Unsafe.AsRef(in _cb)),
            };
            _mgr.Vk.QueueSubmit(queue, 1, si, default);
            _mgr.Vk.QueueWaitIdle(queue);
            _submitted = true;
            _mgr.Free(_cb, _type);
        }

        public void Dispose()
        {
            if (!_submitted)
            {
                // 没提交就 dispose，警告
                Console.Error.WriteLine("[CmdPool] OneTimeCommand 没 submit 就 dispose 了");
                _mgr.Free(_cb, _type);
            }
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 5: 命令缓冲录制辅助
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>命令缓冲录制辅助。提供常用的命令组合。</summary>
    public static unsafe class CommandRecordingHelper
    {
        /// <summary>pipeline barrier 包装。</summary>
        public static void PipelineBarrier(CommandBuffer cmd, PipelineStageFlags srcStage,
                                           PipelineStageFlags dstStage,
                                           DependencyFlags flags,
                                           MemoryBarrier[] memoryBarriers,
                                           BufferMemoryBarrier[] bufferBarriers,
                                           ImageMemoryBarrier[] imageBarriers)
        {
            fixed (MemoryBarrier* pMem = memoryBarriers)
            fixed (BufferMemoryBarrier* pBuf = bufferBarriers)
            fixed (ImageMemoryBarrier* pImg = imageBarriers)
            {
                Silk.NET.Vulkan.Vk.GetApi().CmdPipelineBarrier(cmd, srcStage, dstStage, flags,
                    (uint)(memoryBarriers?.Length ?? 0), pMem,
                    (uint)(bufferBarriers?.Length ?? 0), pBuf,
                    (uint)(imageBarriers?.Length ?? 0), pImg);
            }
        }

        /// <summary>简单的 image layout 转换。</summary>
        public static void TransitionImageLayout(CommandBuffer cmd, Image image,
                                                 ImageSubresourceRange range,
                                                 ImageLayout oldLayout, ImageLayout newLayout,
                                                 AccessFlags srcAccess, AccessFlags dstAccess,
                                                 PipelineStageFlags srcStage, PipelineStageFlags dstStage)
        {
            var barrier = new ImageMemoryBarrier
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = srcAccess,
                DstAccessMask = dstAccess,
                OldLayout = oldLayout,
                NewLayout = newLayout,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = image,
                SubresourceRange = range,
            };
            Silk.NET.Vulkan.Vk.GetApi().CmdPipelineBarrier(cmd, srcStage, dstStage, 0,
                0, null, 0, null, 1, &barrier);
        }

        /// <summary>buffer 拷贝。</summary>
        public static void CopyBuffer(CommandBuffer cmd, Buffer src, Buffer dst,
                                      ulong srcOffset, ulong dstOffset, ulong size)
        {
            var region = new BufferCopy
            {
                SrcOffset = srcOffset,
                DstOffset = dstOffset,
                Size = size,
            };
            Silk.NET.Vulkan.Vk.GetApi().CmdCopyBuffer(cmd, src, dst, 1, &region);
        }

        /// <summary>buffer 到 image 拷贝。</summary>
        public static void CopyBufferToImage(CommandBuffer cmd, Buffer src, Image dst,
                                             ImageLayout dstLayout, BufferImageCopy[] regions)
        {
            fixed (BufferImageCopy* p = regions)
            {
                Silk.NET.Vulkan.Vk.GetApi().CmdCopyBufferToImage(cmd, src, dst, dstLayout,
                    (uint)regions.Length, p);
            }
        }

        /// <summary>image 到 buffer 拷贝。</summary>
        public static void CopyImageToBuffer(CommandBuffer cmd, Image src, ImageLayout srcLayout,
                                             Buffer dst, BufferImageCopy[] regions)
        {
            fixed (BufferImageCopy* p = regions)
            {
                Silk.NET.Vulkan.Vk.GetApi().CmdCopyImageToBuffer(cmd, src, srcLayout, dst,
                    (uint)regions.Length, p);
            }
        }

        /// <summary>清空颜色附件。</summary>
        public static void ClearColorImage(CommandBuffer cmd, Image image, ImageLayout layout,
                                           ClearColorValue clearColor, ImageSubresourceRange range)
        {
            Silk.NET.Vulkan.Vk.GetApi().CmdClearColorImage(cmd, image, layout, clearColor, 1, &range);
        }

        /// <summary>清空深度模板附件。</summary>
        public static void ClearDepthStencilImage(CommandBuffer cmd, Image image, ImageLayout layout,
                                                  ClearDepthStencilValue clearValue, ImageSubresourceRange range)
        {
            Silk.NET.Vulkan.Vk.GetApi().CmdClearDepthStencilImage(cmd, image, layout, clearValue, 1, &range);
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 6: 命令缓冲泄漏检测
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>命令缓冲泄漏检测。</summary>
    public sealed class CommandBufferLeakDetector
    {
        private readonly Dictionary<CommandBuffer, LeakEntry> _active = new();
        private readonly object _lock = new();
        private bool _enabled = true;

        private struct LeakEntry
        {
            public CommandBuffer Buffer;
            public int ThreadId;
            public long Frame;
            public string Tag;
            public DateTime AcquiredAt;
        }

        public bool Enabled
        {
            get => _enabled;
            set => _enabled = value;
        }

        public void TrackAllocate(CommandBuffer cb, string tag = null)
        {
            if (!_enabled) return;
            lock (_lock)
            {
                _active[cb] = new LeakEntry
                {
                    Buffer = cb,
                    ThreadId = Thread.CurrentThread.ManagedThreadId,
                    Frame = 0,
                    Tag = tag,
                    AcquiredAt = DateTime.UtcNow,
                };
            }
        }

        public void TrackFree(CommandBuffer cb)
        {
            if (!_enabled) return;
            lock (_lock) _active.Remove(cb);
        }

        public int ActiveCount
        {
            get { lock (_lock) return _active.Count; }
        }

        public void DumpLeaks(System.IO.TextWriter writer)
        {
            lock (_lock)
            {
                writer.WriteLine($"=== Command buffer leaks: {_active.Count} ===");
                foreach (var kv in _active)
                {
                    writer.WriteLine($"  cb=0x{kv.Key.Handle:X} thread={kv.Value.ThreadId} " +
                                     $"tag={kv.Value.Tag} acquired={kv.Value.AcquiredAt:HH:mm:ss}");
                }
            }
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 7: 命令缓冲池监控
    // ════════════════════════════════════════════════════════════════════════════

    public sealed class CommandPoolMonitor : IDisposable
    {
        private readonly VulkanCommandPoolManager _mgr;
        private System.Threading.Timer _timer;
        private readonly object _lock = new();
        private readonly List<CommandPoolStats> _history = new();
        private const int MaxHistory = 60;
        private bool _disposed;

        public event Action<CommandPoolStats> OnStatsUpdated;

        public CommandPoolMonitor(VulkanCommandPoolManager mgr, TimeSpan? interval = null)
        {
            _mgr = mgr;
            _timer = new System.Threading.Timer(_ => Tick(), null,
                interval ?? TimeSpan.FromSeconds(1),
                interval ?? TimeSpan.FromSeconds(1));
        }

        private void Tick()
        {
            if (_disposed) return;
            var s = _mgr.GetStats();
            lock (_lock)
            {
                _history.Add(s);
                if (_history.Count > MaxHistory) _history.RemoveAt(0);
            }
            OnStatsUpdated?.Invoke(s);
        }

        public CommandPoolStats[] GetHistory()
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

    public static class CommandPoolVersion
    {
        public const string Version = "1.0.0";
        public const string Description = "ShanyuEngine Vulkan Command Pool Manager (thread-local, per-frame reset)";
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 9: 使用示例
    // ════════════════════════════════════════════════════════════════════════════

    /*
    // === 使用示例 ===
    //
    // var mgr = new VulkanCommandPoolManager(vk, device, graphicsQueueFamily, computeQueueFamily, transferQueueFamily);
    //
    // // 主线程每帧
    // mgr.OnFrameBegin(frame);
    // var cmd = mgr.Allocate(CommandPoolType.Graphics);
    // mgr.BeginRecording(cmd);
    // // ... vkCmdDraw ...
    // mgr.EndRecording(cmd);
    // // submit
    // mgr.Free(cmd);
    //
    // // 一次性命令 (上传数据)
    // using var oneTime = mgr.BeginOneTime(CommandPoolType.Transfer);
    // CommandRecordingHelper.CopyBuffer(oneTime.CommandBuffer, srcBuf, dstBuf, 0, 0, size);
    // oneTime.EndAndSubmit(transferQueue);
    */
}

// 文件结束 - VulkanCommandPoolManager.cs
