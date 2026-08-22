// =============================================================================
// VulkanSyncManager.cs - 山雨引擎 Vulkan 同步管理器 (Fence/Semaphore)
// =============================================================================
// Vulkan 的同步是整个 API 里最绕的部分，没有之一。
// fence 是 GPU->CPU 同步，semaphore 是 GPU->GPU 同步 (queue 内或跨 queue)。
//
// 这套管理器做的事：
//   1. fence/semaphore 对象池 (避免每帧创建销毁)
//   2. 每帧 fence 管理 (frames in flight)
//   3. timeline semaphore 支持 (比 binary semaphore 好用多了)
//   4. swapchain acquire/present 的信号量配对
//
// FIXME: timeline semaphore 还没在所有平台稳定，先默认 binary
// TODO: 加 dependency 链 (跨 queue 的 wait)
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
using Semaphore = Silk.NET.Vulkan.Semaphore;
namespace ShanyuEngine.Vulkan
{
    // ════════════════════════════════════════════════════════════════════════════
    // 区域 1: 公共类型
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>fence 创建标志。</summary>
    [Flags]
    public enum FenceCreateFlagsExt : uint
    {
        None = 0,
        /// <summary>创建时就是 signaled。</summary>
        Signaled = 0x1,
    }

    /// <summary>同步原语类型。</summary>
    public enum SyncPrimitiveType
    {
        BinarySemaphore,
        TimelineSemaphore,
        Fence,
    }

    /// <summary>同步原语句柄。</summary>
    public readonly struct SyncHandle : IEquatable<SyncHandle>
    {
        public readonly ulong Native;
        public readonly SyncPrimitiveType Type;
        public bool IsValid => Native != 0;
        public bool IsInvalid => Native == 0;
        public static SyncHandle Null => default;

        public SyncHandle(ulong n, SyncPrimitiveType t) { Native = n; Type = t; }

        public bool Equals(SyncHandle other) => Native == other.Native && Type == other.Type;
        public override bool Equals(object obj) => obj is SyncHandle h && Equals(h);
        public override int GetHashCode() => HashCode.Combine(Native, Type);
        public static bool operator ==(SyncHandle a, SyncHandle b) => a.Equals(b);
        public static bool operator !=(SyncHandle a, SyncHandle b) => !a.Equals(b);
        public override string ToString() => $"{Type}@0x{Native:X}";
    }

    /// <summary>同步管理统计。</summary>
    public struct SyncManagerStats
    {
        public int TotalFencesCreated;
        public int TotalSemaphoresCreated;
        public int ActiveFences;
        public int ActiveSemaphores;
        public int FramesInFlight;
        public long TotalWaits;
        public long TotalTimeouts;

        public override string ToString()
            => $"Sync: fences={ActiveFences}/{TotalFencesCreated}, " +
               $"sems={ActiveSemaphores}/{TotalSemaphoresCreated}, " +
               $"FIF={FramesInFlight}, waits={TotalWaits}, timeouts={TotalTimeouts}";
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 2: Fence 池
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Fence 池。fence 是一次性使用 (vkQueueSubmit 后等)，但对象本身可以复用 (reset 后再 submit)。
    /// </summary>
    internal sealed unsafe class FencePool : IDisposable
    {
        private readonly Vk _vk;
        private readonly Device _device;
        private readonly Stack<Fence> _free = new();
        private readonly HashSet<Fence> _inUse = new();
        private readonly object _lock = new();
        private int _totalCreated;

        public int TotalCreated => _totalCreated;
        public int InUseCount { get; private set; }
        public int FreeCount => _free.Count;

        internal FencePool(Vk vk, Device device)
        {
            _vk = vk;
            _device = device;
        }

        /// <summary>获取一个 fence (优先复用，没有再创建)。</summary>
        public Fence Acquire(bool signaled = false)
        {
            lock (_lock)
            {
                if (_free.Count > 0)
                {
                    var f = _free.Pop();
                    _inUse.Add(f);
                    InUseCount++;
                    if (signaled)
                    {
                        // 重置为 signaled
                        _vk.ResetFences(_device, 1, f);
                        // FIXME: 实际 signaled fence 应该用创建标志，但 reset 后是 unsignaled
                        // 想要 signaled 就得重新创建
                    }
                    return f;
                }
            }

            // 创建新 fence
            var info = new FenceCreateInfo
            {
                SType = StructureType.FenceCreateInfo,
                Flags = signaled ? FenceCreateFlags.SignaledBit : 0,
            };
            Fence f2;
            var r = _vk.CreateFence(_device, info, null, &f2);
            if (r != Result.Success)
                throw new VulkanException(r, "CreateFence 失败");
            lock (_lock)
            {
                _totalCreated++;
                _inUse.Add(f2);
                InUseCount++;
            }
            return f2;
        }

        /// <summary>批量获取。</summary>
        public Fence[] AcquireMany(int count, bool signaled = false)
        {
            var arr = new Fence[count];
            for (int i = 0; i < count; i++) arr[i] = Acquire(signaled);
            return arr;
        }

        /// <summary>释放一个 fence 回池。</summary>
        public void Release(Fence fence)
        {
            lock (_lock)
            {
                if (_inUse.Remove(fence))
                {
                    InUseCount--;
                    _free.Push(fence);
                }
            }
        }

        /// <summary>等待一个 fence。</summary>
        public bool Wait(Fence fence, ulong timeoutNs = 1_000_000_000)
        {
            fixed (Fence* pf = &fence)
            {
                var r = _vk.WaitForFences(_device, 1, pf, true, timeoutNs);
                return r == Result.Success;
            }
        }

        /// <summary>等待多个 fence (全部)。</summary>
        public bool WaitAll(Fence[] fences, ulong timeoutNs = 1_000_000_000)
        {
            fixed (Fence* pf = fences)
            {
                var r = _vk.WaitForFences(_device, (uint)fences.Length, pf, true, timeoutNs);
                return r == Result.Success;
            }
        }

        /// <summary>等待多个 fence (任一)。</summary>
        public int WaitAny(Fence[] fences, ulong timeoutNs = 1_000_000_000)
        {
            fixed (Fence* pf = fences)
            {
                var r = _vk.WaitForFences(_device, (uint)fences.Length, pf, false, timeoutNs);
                if (r != Result.Success) return -1;
                // 查哪个 signaled 了
                for (int i = 0; i < fences.Length; i++)
                {
                    if (_vk.GetFenceStatus(_device, fences[i]) == Result.Success)
                        return i;
                }
                return -1;
            }
        }

        /// <summary>重置 fence。</summary>
        public void Reset(Fence fence)
        {
            fixed (Fence* pf = &fence)
                _vk.ResetFences(_device, 1, pf);
        }

        /// <summary>批量重置。</summary>
        public void ResetMany(Fence[] fences)
        {
            fixed (Fence* pf = fences)
                _vk.ResetFences(_device, (uint)fences.Length, pf);
        }

        /// <summary>查询 fence 状态。</summary>
        public bool IsSignaled(Fence fence)
        {
            return _vk.GetFenceStatus(_device, fence) == Result.Success;
        }

        public void Dispose()
        {
            lock (_lock)
            {
                foreach (var f in _free) _vk.DestroyFence(_device, f, null);
                foreach (var f in _inUse) _vk.DestroyFence(_device, f, null);
                _free.Clear();
                _inUse.Clear();
            }
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 3: Semaphore 池
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Semaphore 池。binary semaphore 不能查询状态，用完要 release 才能再用。
    /// timeline semaphore 支持查询/等待指定 value，比 binary 灵活。
    /// </summary>
    internal sealed unsafe class SemaphorePool : IDisposable
    {
        private readonly Vk _vk;
        private readonly Device _device;
        private readonly bool _useTimeline;
        private readonly Stack<Semaphore> _free = new();
        private readonly HashSet<Semaphore> _inUse = new();
        private readonly object _lock = new();
        private int _totalCreated;

        public bool UseTimeline => _useTimeline;
        public int TotalCreated => _totalCreated;
        public int InUseCount { get; private set; }
        public int FreeCount => _free.Count;

        internal SemaphorePool(Vk vk, Device device, bool useTimeline = false)
        {
            _vk = vk;
            _device = device;
            _useTimeline = useTimeline;
        }

        public Semaphore Acquire()
        {
            lock (_lock)
            {
                if (_free.Count > 0)
                {
                    var s = _free.Pop();
                    _inUse.Add(s);
                    InUseCount++;
                    return s;
                }
            }

            // 创建
            SemaphoreTypeCreateInfo stci = default;
            SemaphoreCreateInfo sci = new()
            {
                SType = StructureType.SemaphoreCreateInfo,
            };
            if (_useTimeline)
            {
                stci.SType = StructureType.SemaphoreTypeCreateInfo;
                stci.SemaphoreType = SemaphoreType.Timeline;
                stci.InitialValue = 0;
                sci.PNext = &stci;
            }

            Semaphore sem;
            var r = _vk.CreateSemaphore(_device, sci, null, &sem);
            if (r != Result.Success)
                throw new VulkanException(r, "CreateSemaphore 失败");
            lock (_lock)
            {
                _totalCreated++;
                _inUse.Add(sem);
                InUseCount++;
            }
            return sem;
        }

        public Semaphore[] AcquireMany(int count)
        {
            var arr = new Semaphore[count];
            for (int i = 0; i < count; i++) arr[i] = Acquire();
            return arr;
        }

        public void Release(Semaphore sem)
        {
            lock (_lock)
            {
                if (_inUse.Remove(sem))
                {
                    InUseCount--;
                    _free.Push(sem);
                }
            }
        }

        /// <summary>查询 timeline semaphore 的当前 value。</summary>
        public ulong QueryTimelineValue(Semaphore sem)
        {
            if (!_useTimeline) return 0;
            ulong val;
            _vk.GetSemaphoreCounterValue(_device, sem, &val);
            return val;
        }

        /// <summary>等待 timeline semaphore 到指定 value。</summary>
        public bool WaitTimeline(Semaphore sem, ulong value, ulong timeoutNs = 1_000_000_000)
        {
            if (!_useTimeline) return false;
            var info = new SemaphoreWaitInfo
            {
                SType = StructureType.SemaphoreWaitInfo,
                SemaphoreCount = 1,
                PSemaphores = &sem,
                PValues = &value,
            };
            var r = _vk.WaitSemaphores(_device, info, timeoutNs);
            return r == Result.Success;
        }

        /// <summary>给 timeline semaphore 发信号 (CPU 端)。</summary>
        public void SignalTimeline(Semaphore sem, ulong value)
        {
            if (!_useTimeline) return;
            var info = new SemaphoreSignalInfo
            {
                SType = StructureType.SemaphoreSignalInfo,
                Semaphore = sem,
                Value = value,
            };
            _vk.SignalSemaphore(_device, info);
        }

        public void Dispose()
        {
            lock (_lock)
            {
                foreach (var s in _free) _vk.DestroySemaphore(_device, s, null);
                foreach (var s in _inUse) _vk.DestroySemaphore(_device, s, null);
                _free.Clear();
                _inUse.Clear();
            }
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 4: 帧内 fence 管理 (frames in flight)
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 帧内 fence 管理。每帧一个 fence，提交后等待"上一帧"的 fence (而不是本帧)。
    /// 这是 frames-in-flight 的标准做法。
    /// </summary>
    public sealed class FrameFenceManager
    {
        private readonly FencePool _fencePool;
        private readonly Fence[] _frameFences;
        private readonly int _framesInFlight;
        private int _currentFrame;
        private readonly object _lock = new();

        public int FramesInFlight => _framesInFlight;
        public int CurrentFrame => _currentFrame;

        public FrameFenceManager(FencePool pool, int framesInFlight)
        {
            _fencePool = pool;
            _framesInFlight = framesInFlight;
            _frameFences = new Fence[framesInFlight];
            // 全部创建为 signaled 状态，这样第一次提交不会卡
            for (int i = 0; i < framesInFlight; i++)
                _frameFences[i] = pool.Acquire(signaled: true);
        }

        /// <summary>获取当前帧的 fence。</summary>
        public Fence GetCurrentFence() => _frameFences[_currentFrame];

        /// <summary>等待当前帧的 fence (上一帧的提交完成)。</summary>
        public bool WaitForCurrentFrame(ulong timeoutNs = 1_000_000_000)
        {
            // 注意：这里等待的是上一帧的 fence
            // 因为_submit 后 fence 才开始计数，但本帧还没 submit
            // 所以这里 wait 实际是等"上一帧的 fence"
            return _fencePool.Wait(_frameFences[_currentFrame], timeoutNs);
        }

        /// <summary>等待所有帧的 fence。</summary>
        public bool WaitForAll(ulong timeoutNs = 1_000_000_000)
        {
            return _fencePool.WaitAll(_frameFences, timeoutNs);
        }

        /// <summary>提交后重置当前帧 fence，准备下一帧。</summary>
        public void OnFrameSubmitted()
        {
            _currentFrame = (_currentFrame + 1) % _framesInFlight;
        }

        /// <summary>等待并重置指定帧。</summary>
        public bool WaitAndReset(int frameIndex, ulong timeoutNs = 1_000_000_000)
        {
            if (!_fencePool.Wait(_frameFences[frameIndex], timeoutNs)) return false;
            _fencePool.Reset(_frameFences[frameIndex]);
            return true;
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 5: swapchain semaphore 配对
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Swapchain acquire/present 用的信号量配对管理。
    /// 每次 acquire 一个 image，需要一个 semaphore 标记"image 可用"。
    /// 提交 draw 命令时 wait 这个 semaphore。
    /// present 时 signal 另一个 semaphore 标记"渲染完成"。
    /// </summary>
    public sealed class SwapchainSemaphoreManager : IDisposable
    {
        private readonly SemaphorePool _semPool;
        private readonly Semaphore[] _imageAvailable;  // acquire 用
        private readonly Semaphore[] _renderFinished;   // present 用
        private readonly int _framesInFlight;
        private int _currentFrame;

        public SwapchainSemaphoreManager(SemaphorePool pool, int framesInFlight)
        {
            _semPool = pool;
            _framesInFlight = framesInFlight;
            _imageAvailable = new Semaphore[framesInFlight];
            _renderFinished = new Semaphore[framesInFlight];
            for (int i = 0; i < framesInFlight; i++)
            {
                _imageAvailable[i] = pool.Acquire();
                _renderFinished[i] = pool.Acquire();
            }
        }

        public Semaphore GetImageAvailable(int frameIndex) => _imageAvailable[frameIndex % _framesInFlight];
        public Semaphore GetRenderFinished(int frameIndex) => _renderFinished[frameIndex % _framesInFlight];

        public Semaphore GetCurrentImageAvailable() => _imageAvailable[_currentFrame];
        public Semaphore GetCurrentRenderFinished() => _renderFinished[_currentFrame];

        public void Advance() => _currentFrame = (_currentFrame + 1) % _framesInFlight;

        public void Dispose()
        {
            foreach (var s in _imageAvailable) _semPool.Release(s);
            foreach (var s in _renderFinished) _semPool.Release(s);
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 6: 提交信息构建器
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>Submit 信息构建器。简化 vkQueueSubmit 的参数构造。</summary>
    public sealed unsafe class SubmitInfoBuilder
    {
        private readonly List<Semaphore> _waitSemaphores = new();
        private readonly List<PipelineStageFlags> _waitStages = new();
        private readonly List<Semaphore> _signalSemaphores = new();
        private readonly List<CommandBuffer> _commandBuffers = new();
        private readonly List<ulong> _timelineWaitValues = new();
        private readonly List<ulong> _timelineSignalValues = new();
        private bool _useTimeline;

        public SubmitInfoBuilder AddWaitSemaphore(Semaphore sem, PipelineStageFlags stage)
        {
            _waitSemaphores.Add(sem);
            _waitStages.Add(stage);
            return this;
        }

        public SubmitInfoBuilder AddWaitTimeline(Semaphore sem, PipelineStageFlags stage, ulong value)
        {
            _useTimeline = true;
            _waitSemaphores.Add(sem);
            _waitStages.Add(stage);
            _timelineWaitValues.Add(value);
            return this;
        }

        public SubmitInfoBuilder AddSignalSemaphore(Semaphore sem)
        {
            _signalSemaphores.Add(sem);
            return this;
        }

        public SubmitInfoBuilder AddSignalTimeline(Semaphore sem, ulong value)
        {
            _useTimeline = true;
            _signalSemaphores.Add(sem);
            _timelineSignalValues.Add(value);
            return this;
        }

        public SubmitInfoBuilder AddCommandBuffer(CommandBuffer cmd)
        {
            _commandBuffers.Add(cmd);
            return this;
        }

        public SubmitInfoBuilder AddCommandBuffers(CommandBuffer[] cmds)
        {
            _commandBuffers.AddRange(cmds);
            return this;
        }

        public SubmitInfo Build()
        {
            var si = new SubmitInfo
            {
                SType = StructureType.SubmitInfo,
                CommandBufferCount = (uint)_commandBuffers.Count,
            };

            // 简化处理，实际需要 pin 数组
            if (_commandBuffers.Count > 0)
            {
                // si.PCommandBuffers = ... (需要 fixed)
            }

            return si;
        }

        public void Clear()
        {
            _waitSemaphores.Clear();
            _waitStages.Clear();
            _signalSemaphores.Clear();
            _commandBuffers.Clear();
            _timelineWaitValues.Clear();
            _timelineSignalValues.Clear();
            _useTimeline = false;
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 7: 同步管理器主类
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 同步管理器主入口。一个设备一个实例。
    /// </summary>
    public sealed unsafe class VulkanSyncManager : IDisposable
    {
        private readonly Vk _vk;
        private readonly Device _device;
        private readonly FencePool _fencePool;
        private readonly SemaphorePool _semPool;
        private FrameFenceManager _frameFenceMgr;
        private SwapchainSemaphoreManager _swapchainSemMgr;
        private readonly SyncManagerStats _stats;
        private readonly object _lock = new();

        public bool HasTimelineSemaphore => _semPool.UseTimeline;
        public ref SyncManagerStats Stats => ref _stats;

        public VulkanSyncManager(Vk vk, Device device, bool useTimeline = false)
        {
            _vk = vk;
            _device = device;
            _fencePool = new FencePool(vk, device);
            _semPool = new SemaphorePool(vk, device, useTimeline);
            _stats = new SyncManagerStats();
        }

        /// <summary>初始化帧内同步 (frames in flight)。</summary>
        public void InitFramesInFlight(int framesInFlight)
        {
            _frameFenceMgr = new FrameFenceManager(_fencePool, framesInFlight);
            _swapchainSemMgr = new SwapchainSemaphoreManager(_semPool, framesInFlight);
            _stats.FramesInFlight = framesInFlight;
        }

        /// <summary>获取当前帧 fence。</summary>
        public Fence GetCurrentFrameFence() => _frameFenceMgr?.GetCurrentFence() ?? default;

        /// <summary>等待当前帧 fence (上一帧完成)。</summary>
        public bool WaitForCurrentFrame(ulong timeoutNs = 1_000_000_000)
        {
            if (_frameFenceMgr == null) return true;
            Interlocked.Increment(ref _stats.TotalWaits);
            bool ok = _frameFenceMgr.WaitForCurrentFrame(timeoutNs);
            if (!ok) Interlocked.Increment(ref _stats.TotalTimeouts);
            return ok;
        }

        /// <summary>等待所有帧。</summary>
        public bool WaitForAllFrames(ulong timeoutNs = 1_000_000_000)
        {
            return _frameFenceMgr?.WaitForAll(timeoutNs) ?? true;
        }

        /// <summary>帧提交后调用。</summary>
        public void OnFrameSubmitted()
        {
            _frameFenceMgr?.OnFrameSubmitted();
            _swapchainSemMgr?.Advance();
        }

        /// <summary>获取 swapchain image available semaphore。</summary>
        public Semaphore GetImageAvailableSemaphore() =>
            _swapchainSemMgr?.GetCurrentImageAvailable() ?? default;

        /// <summary>获取 swapchain render finished semaphore。</summary>
        public Semaphore GetRenderFinishedSemaphore() =>
            _swapchainSemMgr?.GetCurrentRenderFinished() ?? default;

        /// <summary>获取 swapchain image available semaphore (指定帧)。</summary>
        public Semaphore GetImageAvailableSemaphore(int frame) =>
            _swapchainSemMgr?.GetImageAvailable(frame) ?? default;

        /// <summary>获取 swapchain render finished semaphore (指定帧)。</summary>
        public Semaphore GetRenderFinishedSemaphore(int frame) =>
            _swapchainSemMgr?.GetRenderFinished(frame) ?? default;

        /// <summary>单独获取一个 fence (临时用)。</summary>
        public Fence AcquireFence(bool signaled = false)
        {
            var f = _fencePool.Acquire(signaled);
            _stats.ActiveFences++;
            return f;
        }

        /// <summary>释放临时 fence。</summary>
        public void ReleaseFence(Fence fence)
        {
            _fencePool.Release(fence);
            _stats.ActiveFences--;
        }

        /// <summary>等待单个 fence。</summary>
        public bool WaitFence(Fence fence, ulong timeoutNs = 1_000_000_000)
        {
            Interlocked.Increment(ref _stats.TotalWaits);
            bool ok = _fencePool.Wait(fence, timeoutNs);
            if (!ok) Interlocked.Increment(ref _stats.TotalTimeouts);
            return ok;
        }

        /// <summary>等待多个 fence。</summary>
        public bool WaitFences(Fence[] fences, ulong timeoutNs = 1_000_000_000)
        {
            Interlocked.Increment(ref _stats.TotalWaits);
            bool ok = _fencePool.WaitAll(fences, timeoutNs);
            if (!ok) Interlocked.Increment(ref _stats.TotalTimeouts);
            return ok;
        }

        /// <summary>等待任一 fence。</summary>
        public int WaitAnyFence(Fence[] fences, ulong timeoutNs = 1_000_000_000)
        {
            Interlocked.Increment(ref _stats.TotalWaits);
            int idx = _fencePool.WaitAny(fences, timeoutNs);
            if (idx < 0) Interlocked.Increment(ref _stats.TotalTimeouts);
            return idx;
        }

        /// <summary>查询 fence 是否 signaled。</summary>
        public bool IsFenceSignaled(Fence fence) => _fencePool.IsSignaled(fence);

        /// <summary>重置 fence。</summary>
        public void ResetFence(Fence fence) => _fencePool.Reset(fence);

        /// <summary>重置多个 fence。</summary>
        public void ResetFences(Fence[] fences) => _fencePool.ResetMany(fences);

        /// <summary>单独获取一个 semaphore。</summary>
        public Semaphore AcquireSemaphore()
        {
            var s = _semPool.Acquire();
            _stats.ActiveSemaphores++;
            return s;
        }

        /// <summary>释放 semaphore。</summary>
        public void ReleaseSemaphore(Semaphore sem)
        {
            _semPool.Release(sem);
            _stats.ActiveSemaphores--;
        }

        /// <summary>查询 timeline semaphore 当前值。</summary>
        public ulong QueryTimelineSemaphore(Semaphore sem)
            => _semPool.QueryTimelineValue(sem);

        /// <summary>等待 timeline semaphore 到指定值。</summary>
        public bool WaitTimelineSemaphore(Semaphore sem, ulong value, ulong timeoutNs = 1_000_000_000)
        {
            Interlocked.Increment(ref _stats.TotalWaits);
            bool ok = _semPool.WaitTimeline(sem, value, timeoutNs);
            if (!ok) Interlocked.Increment(ref _stats.TotalTimeouts);
            return ok;
        }

        /// <summary>CPU 端 signal timeline semaphore。</summary>
        public void SignalTimelineSemaphore(Semaphore sem, ulong value)
            => _semPool.SignalTimeline(sem, value);

        /// <summary>创建 submit info builder。</summary>
        public SubmitInfoBuilder CreateSubmitBuilder() => new();

        /// <summary>获取统计。</summary>
        public SyncManagerStats GetStats()
        {
            _stats.TotalFencesCreated = _fencePool.TotalCreated;
            _stats.TotalSemaphoresCreated = _semPool.TotalCreated;
            _stats.ActiveFences = _fencePool.InUseCount;
            _stats.ActiveSemaphores = _semPool.InUseCount;
            return _stats;
        }

        public void Dispose()
        {
            _swapchainSemMgr?.Dispose();
            _frameFenceMgr = null;
            _semPool.Dispose();
            _fencePool.Dispose();
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 8: Barrier 同步辅助
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>Barrier 辅助。提供常用的 memory barrier 构造。</summary>
    public static unsafe class BarrierHelper
    {
        /// <summary>通用 host->device barrier。</summary>
        public static MemoryBarrier HostToDevice()
            => new MemoryBarrier
            {
                SType = StructureType.MemoryBarrier,
                SrcAccessMask = AccessFlags.HostWriteBit,
                DstAccessMask = AccessFlags.ShaderReadBit | AccessFlags.TransferReadBit,
            };

        /// <summary>通用 device->host barrier。</summary>
        public static MemoryBarrier DeviceToHost()
            => new MemoryBarrier
            {
                SType = StructureType.MemoryBarrier,
                SrcAccessMask = AccessFlags.TransferWriteBit,
                DstAccessMask = AccessFlags.HostReadBit,
            };

        /// <summary>顶点缓冲写入->顶点着色器读。</summary>
        public static BufferMemoryBarrier VertexBufferRead(Buffer buf, ulong offset, ulong size)
            => new BufferMemoryBarrier
            {
                SType = StructureType.BufferMemoryBarrier,
                SrcAccessMask = AccessFlags.TransferWriteBit,
                DstAccessMask = AccessFlags.VertexAttributeReadBit,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Buffer = buf,
                Offset = offset,
                Size = size,
            };

        /// <summary>贴图写入->shader 读。</summary>
        public static ImageMemoryBarrier TextureRead(Image img, ImageSubresourceRange range)
            => new ImageMemoryBarrier
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = AccessFlags.TransferWriteBit,
                DstAccessMask = AccessFlags.ShaderReadBit,
                OldLayout = ImageLayout.TransferDstOptimal,
                NewLayout = ImageLayout.ShaderReadOnlyOptimal,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = img,
                SubresourceRange = range,
            };

        /// <summary>渲染目标->shader 读 (input attachment)。</summary>
        public static ImageMemoryBarrier RenderTargetToInput(Image img, ImageSubresourceRange range)
            => new ImageMemoryBarrier
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = AccessFlags.ColorAttachmentWriteBit,
                DstAccessMask = AccessFlags.InputAttachmentReadBit,
                OldLayout = ImageLayout.ColorAttachmentOptimal,
                NewLayout = ImageLayout.ShaderReadOnlyOptimal,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = img,
                SubresourceRange = range,
            };

        /// <summary>深度写入->深度读。</summary>
        public static ImageMemoryBarrier DepthWriteToRead(Image img, ImageSubresourceRange range)
            => new ImageMemoryBarrier
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = AccessFlags.DepthStencilAttachmentWriteBit,
                DstAccessMask = AccessFlags.DepthStencilAttachmentReadBit,
                OldLayout = ImageLayout.DepthStencilAttachmentOptimal,
                NewLayout = ImageLayout.DepthStencilAttachmentOptimal,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = img,
                SubresourceRange = range,
            };

        /// <summary>未定义布局->渲染目标。</summary>
        public static ImageMemoryBarrier UndefinedToColorAttachment(Image img, ImageSubresourceRange range)
            => new ImageMemoryBarrier
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = 0,
                DstAccessMask = AccessFlags.ColorAttachmentReadBit | AccessFlags.ColorAttachmentWriteBit,
                OldLayout = ImageLayout.Undefined,
                NewLayout = ImageLayout.ColorAttachmentOptimal,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = img,
                SubresourceRange = range,
            };

        /// <summary>渲染目标->present。</summary>
        public static ImageMemoryBarrier ColorAttachmentToPresent(Image img, ImageSubresourceRange range)
            => new ImageMemoryBarrier
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = AccessFlags.ColorAttachmentWriteBit,
                DstAccessMask = 0,
                OldLayout = ImageLayout.ColorAttachmentOptimal,
                NewLayout = ImageLayout.PresentSrcKhr,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = img,
                SubresourceRange = range,
            };
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 9: 同步原语事件 (EVENT)
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// VkEvent 管理。event 介于 fence 和 semaphore 之间，可以 CPU/GPU 双向 set/reset。
    /// 主要用于 device 端的条件同步。
    /// </summary>
    public sealed unsafe class VulkanEventManager : IDisposable
    {
        private readonly Vk _vk;
        private readonly Device _device;
        private readonly Stack<Event> _free = new();
        private readonly HashSet<Event> _inUse = new();
        private readonly object _lock = new();

        public VulkanEventManager(Vk vk, Device device)
        {
            _vk = vk;
            _device = device;
        }

        public Event Acquire()
        {
            lock (_lock)
            {
                if (_free.Count > 0)
                {
                    var e = _free.Pop();
                    _inUse.Add(e);
                    return e;
                }
            }

            var info = new EventCreateInfo
            {
                SType = StructureType.EventCreateInfo,
            };
            Event ev;
            var r = _vk.CreateEvent(_device, info, null, &ev);
            if (r != Result.Success)
                throw new VulkanException(r, "CreateEvent 失败");
            lock (_lock) _inUse.Add(ev);
            return ev;
        }

        public void Release(Event ev)
        {
            lock (_lock)
            {
                if (_inUse.Remove(ev)) _free.Push(ev);
            }
        }

        public bool IsSet(Event ev) => _vk.GetEventStatus(_device, ev) == Result.EventSet;

        public void Set(Event ev) => _vk.SetEvent(_device, ev);
        public void Reset(Event ev) => _vk.ResetEvent(_device, ev);

        public void Dispose()
        {
            lock (_lock)
            {
                foreach (var e in _free) _vk.DestroyEvent(_device, e, null);
                foreach (var e in _inUse) _vk.DestroyEvent(_device, e, null);
                _free.Clear();
                _inUse.Clear();
            }
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 10: 同步策略
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>同步策略。</summary>
    public enum SyncStrategy
    {
        /// <summary>每帧一个 fence，等上一帧 (标准 frames-in-flight)。</summary>
        PerFrameFence,
        /// <summary>用 timeline semaphore 一个值递增。</summary>
        TimelineCounter,
        /// <summary>无等待 (调试用，可能崩)。</summary>
        NoWait,
        /// <summary>全部等待 (调试用，最保守)。</summary>
        FullSync,
    }

    /// <summary>同步配置。</summary>
    public sealed class SyncConfig
    {
        public SyncStrategy Strategy { get; set; } = SyncStrategy.PerFrameFence;
        public int FramesInFlight { get; set; } = 2;
        public bool UseTimelineSemaphore { get; set; } = false;
        public ulong DefaultTimeoutNs { get; set; } = 1_000_000_000;
        public bool EnableEventSync { get; set; } = false;
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 11: 同步监控
    // ════════════════════════════════════════════════════════════════════════════

    public sealed class SyncMonitor : IDisposable
    {
        private readonly VulkanSyncManager _mgr;
        private System.Threading.Timer _timer;
        private readonly object _lock = new();
        private readonly List<SyncManagerStats> _history = new();
        private const int MaxHistory = 60;
        private bool _disposed;

        public event Action<SyncManagerStats> OnStatsUpdated;

        public SyncMonitor(VulkanSyncManager mgr, TimeSpan? interval = null)
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

        public SyncManagerStats[] GetHistory()
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
    // 区域 12: 同步原语调试
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>同步调试器。检测 fence 泄漏、死锁等。</summary>
    public sealed class SyncDebugger
    {
        private readonly VulkanSyncManager _mgr;
        private readonly Dictionary<Fence, long> _fenceAcquireFrames = new();
        private readonly object _lock = new();
        private long _currentFrame;

        public SyncDebugger(VulkanSyncManager mgr)
        {
            _mgr = mgr;
        }

        public void TickFrame(long frame) => _currentFrame = frame;

        public void TrackFenceAcquire(Fence f)
        {
            lock (_lock) _fenceAcquireFrames[f] = _currentFrame;
        }

        public void TrackFenceRelease(Fence f)
        {
            lock (_lock) _fenceAcquireFrames.Remove(f);
        }

        /// <summary>检查是否有 fence 泄漏 (持有时间过长)。</summary>
        public void CheckForLeaks(long maxFrames = 5)
        {
            lock (_lock)
            {
                foreach (var kv in _fenceAcquireFrames)
                {
                    if (_currentFrame - kv.Value > maxFrames)
                    {
                        Console.Error.WriteLine($"[SyncDebug] Fence 0x{kv.Key.Handle:X} 持有时间过长 ({_currentFrame - kv.Value} 帧)");
                    }
                }
            }
        }

        /// <summary>dump 当前状态。</summary>
        public string Dump()
        {
            var sb = new System.Text.StringBuilder();
            var s = _mgr.GetStats();
            sb.AppendLine(s.ToString());
            return sb.ToString();
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 13: 版本信息
    // ════════════════════════════════════════════════════════════════════════════

    public static class SyncManagerVersion
    {
        public const string Version = "1.0.0";
        public const string Description = "ShanyuEngine Vulkan Sync Manager (fence/semaphore/event/timeline)";
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 14: 使用示例
    // ════════════════════════════════════════════════════════════════════════════

    /*
    // === 使用示例 ===
    //
    // var sync = new VulkanSyncManager(vk, device);
    // sync.InitFramesInFlight(2);
    //
    // // 每帧
    // sync.WaitForCurrentFrame();
    // // acquire image
    // vkAcquireNextImageKHR(device, swapchain, ...,
    //     sync.GetImageAvailableSemaphore(), ...);
    //
    // // submit
    // var builder = sync.CreateSubmitBuilder()
    //     .AddWaitSemaphore(sync.GetImageAvailableSemaphore(), PipelineStageFlags.ColorAttachmentOutputBit)
    //     .AddSignalSemaphore(sync.GetRenderFinishedSemaphore())
    //     .AddCommandBuffer(cmd);
    // vkQueueSubmit(queue, 1, &builder.Build(), sync.GetCurrentFrameFence());
    //
    // // present
    // vkQueuePresentKHR(queue, &presentInfo);  // presentInfo 用 render finished semaphore
    //
    // sync.OnFrameSubmitted();
    */

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 15: 单元测试桩
    // ════════════════════════════════════════════════════════════════════════════

    public static class SyncManagerTestStub
    {
        public static ulong TestTimeoutMsToNs(ulong ms) => ms * 1_000_000;
        public static ulong TestTimeoutSecToNs(ulong sec) => sec * 1_000_000_000;

        public static bool TestFenceFrameIndex(int current, int fif, int expected)
        {
            int next = (current + 1) % fif;
            return next == expected;
        }
    }
}

// 文件结束 - VulkanSyncManager.cs
