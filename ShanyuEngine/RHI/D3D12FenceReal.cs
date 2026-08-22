// ==================================================================================================
//  D3D12FenceReal.cs - 山雨游戏引擎 D3D12 同步机制真实实现
//
//  命名空间: ShanyuEngine.RHI
//  依赖: Vortice.Direct3D12
//
//  功能覆盖:
//    - D3D12Fence: GPU->CPU 同步栅栏
//      Signal(commandQueue) / Wait(commandQueue) / SetEventOnCompletion / GetCompletedValue
//    - D3D12TimelineCounter: CPU/GPU 双向计数器
//    - FrameSynchronizer: 三缓冲 / 帧编号 / WaitForPreviousFrame / SignalFrame / MoveToNextFrame
//    - CPU等待策略: SpinWait / Sleep / BusyWait / 自适应等待
//    - GPU等待: ExecuteIndirect等待 / UAV Barrier等待
// ==================================================================================================

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Vortice.Direct3D12;

namespace ShanyuEngine.RHI
{
    // ==============================================================================================
    //  D3D12Fence - GPU->CPU 同步栅栏
    // ==============================================================================================

    /// <summary>
    /// D3D12 栅栏。GPU->CPU 同步原语。
    /// 用于: 帧同步、资源释放延迟、异步操作完成检测。
    /// </summary>
    public sealed class D3D12Fence : IDisposable
    {
        private readonly ID3D12Fence _nativeFence;
        private ulong _lastSignaledValue;
        private string _name;
        private bool _disposed;

        /// <summary>原生 D3D12 Fence 指针</summary>
        public ID3D12Fence NativeFence => _nativeFence;

        /// <summary>当前已完成值 (GPU 已到达的值)</summary>
        public ulong CompletedValue => _nativeFence.CompletedValue;

        /// <summary>上一次 Signal 的值</summary>
        public ulong LastSignaledValue => _lastSignaledValue;

        public string Name
        {
            get => _name;
            set
            {
                _name = value;
                if (!string.IsNullOrEmpty(value))
                    _nativeFence.DeviceName = value;
            }
        }

        public D3D12Fence(ID3D12Device device, ulong initialValue = 0, string name = "")
        {
            _nativeFence = device.CreateFence(initialValue, FenceFlags.None);
            _lastSignaledValue = initialValue;
            _name = name;

            if (!string.IsNullOrEmpty(name))
                _nativeFence.DeviceName = name;
        }

        /// <summary>
        /// Signal: CPU 向命令队列发送信号，当 GPU 执行到此点时 Fence 值递增。
        /// </summary>
        public void Signal(ID3D12CommandQueue commandQueue, ulong value)
        {
            commandQueue.Signal(_nativeFence, value);
            _lastSignaledValue = Math.Max(_lastSignaledValue, value);
        }

        /// <summary>
        /// Signal: 使用递增的内部值。
        /// </summary>
        public ulong Signal(ID3D12CommandQueue commandQueue)
        {
            _lastSignaledValue++;
            commandQueue.Signal(_nativeFence, _lastSignaledValue);
            return _lastSignaledValue;
        }

        /// <summary>
        /// GPU 等待: 让 GPU 命令队列等待 Fence 达到指定值。
        /// 在此之前队列中的后续命令不会执行。
        /// </summary>
        public void Wait(ID3D12CommandQueue commandQueue, ulong value)
        {
            commandQueue.Wait(_nativeFence, value);
        }

        /// <summary>
        /// SetEventOnCompletion: 当 Fence 到达指定值时触发事件。
        /// 用于 CPU 等待 GPU 完成操作。
        /// </summary>
        public void SetEventOnCompletion(ulong value, IntPtr eventHandle)
        {
            _nativeFence.SetEventOnCompletion(value, eventHandle);
        }

        /// <summary>
        /// GetCompletedValue: 查询 GPU 已完成的 Fence 值。
        /// </summary>
        public ulong GetCompletedValue()
        {
            return _nativeFence.CompletedValue;
        }

        /// <summary>
        /// 检查指定值是否已完成。
        /// </summary>
        public bool IsCompleted(ulong value)
        {
            return _nativeFence.CompletedValue >= value;
        }

        /// <summary>
        /// CPU 同步等待: 阻塞当前线程直到 Fence 达到指定值。
        /// 使用自适应等待策略 (Spin -> Sleep 混合)。
        /// </summary>
        public void WaitOnCpu(ulong value, uint timeoutMs = uint.MaxValue)
        {
            // 检查是否已完成
            if (_nativeFence.CompletedValue >= value)
                return;

            // 使用自适应等待
            if (timeoutMs == 0 || timeoutMs == uint.MaxValue)
            {
                // 无限等待: 使用 Event
                using var evt = new ManualResetEvent(false);
                _nativeFence.SetEventOnCompletion(value, evt.SafeWaitHandle.DangerousGetHandle());

                AdaptiveWait(evt);
            }
            else
            {
                // 有超时等待
                using var evt = new ManualResetEvent(false);
                _nativeFence.SetEventOnCompletion(value, evt.SafeWaitHandle.DangerousGetHandle());
                evt.WaitOne((int)timeoutMs);
            }
        }

        /// <summary>
        /// Signal + Wait: 在同一队列上 Signal 并立即等待完成。
        /// 等效于 Flush。
        /// </summary>
        public void SignalAndWait(ID3D12CommandQueue commandQueue)
        {
            ulong targetValue = Signal(commandQueue);
            WaitOnCpu(targetValue);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _nativeFence.Dispose();
        }
    }

    // ==============================================================================================
    //  CPU 等待策略
    // ==============================================================================================

    /// <summary>
    /// CPU 等待策略枚举。
    /// </summary>
    public enum CpuWaitStrategy
    {
        /// <summary>自旋等待 (高 CPU 占用，最低延迟)</summary>
        SpinWait,
        /// <summary>线程休眠 (低 CPU 占用，较高延迟)</summary>
        Sleep,
        /// <summary>忙等待 (持续轮询)</summary>
        BusyWait,
        /// <summary>自适应 (先 Spin 后 Sleep)</summary>
        Adaptive
    }

    /// <summary>
    /// 自适应 CPU 等待实现。
    /// 先使用自旋等待 (适合 GPU 快速完成场景)，
    /// 超过阈值后切换为 Sleep (降低 CPU 占用)。
    /// </summary>
    public static class CpuWaitHelper
    {
        /// <summary>自旋阈值 (微秒)，超过后切换为 Sleep</summary>
        private const int SpinThresholdMicroseconds = 500;

        /// <summary>Sleep 间隔 (毫秒)</summary>
        private const int SleepIntervalMs = 1;

        /// <summary>
        /// 自适应等待。先 Spin 等待，超时后 Sleep。
        /// </summary>
        public static void AdaptiveWait(WaitHandle waitEvent, int spinCount = 1000)
        {
            // 阶段 1: Spin 等待 (快速路径)
            var sw = Stopwatch.StartNew();

            for (int i = 0; i < spinCount; i++)
            {
                if (waitEvent.WaitOne(0)) return;

                // Thread.SpinWait: 让出当前时间片但保持运行
                Thread.SpinWait(10);
            }

            sw.Stop();

            // 阶段 2: Sleep 等待 (低 CPU 占用)
            while (!waitEvent.WaitOne(SleepIntervalMs))
            {
                // 继续等待
            }
        }

        /// <summary>纯自旋等待</summary>
        public static void SpinWait(WaitHandle waitEvent, int maxIterations = 10000)
        {
            for (int i = 0; i < maxIterations; i++)
            {
                if (waitEvent.WaitOne(0)) return;
                Thread.SpinWait(20);
            }
        }

        /// <summary>Sleep 等待</summary>
        public static void SleepWait(WaitHandle waitEvent, int intervalMs = 1)
        {
            while (!waitEvent.WaitOne(intervalMs))
            {
                // 继续
            }
        }

        /// <summary>
        /// 忙等待 (轮询 Fence 值)
        /// </summary>
        public static void BusyWait(ID3D12Fence fence, ulong targetValue)
        {
            while (fence.CompletedValue < targetValue)
            {
                // 空循环: 纯忙等
                Thread.SpinWait(50);
            }
        }
    }

    // ==============================================================================================
    //  D3D12TimelineCounter - CPU/GPU 双向计数器
    // ==============================================================================================

    /// <summary>
    /// Timeline Counter: CPU 和 GPU 双向递增的计数器。
    /// CPU 端递增用于标记工作提交，GPU 端到达对应值时表示工作完成。
    /// </summary>
    public sealed class D3D12TimelineCounter : IDisposable
    {
        private readonly ID3D12Fence _fence;
        private ulong _cpuCounter;
        private ulong _gpuCompletedValue;
        private bool _disposed;

        /// <summary>CPU 端当前计数</summary>
        public ulong CpuCounter => _cpuCounter;

        /// <summary>GPU 已完成的计数</summary>
        public ulong GpuCompletedValue
        {
            get
            {
                _gpuCompletedValue = _fence.CompletedValue;
                return _gpuCompletedValue;
            }
        }

        /// <summary>GPU 落后于 CPU 的计数</summary>
        public ulong PendingCount => _cpuCounter - _gpuCompletedValue;

        public D3D12TimelineCounter(ID3D12Device device, string name = "")
        {
            _fence = device.CreateFence(0, FenceFlags.None);
            _cpuCounter = 0;
            _gpuCompletedValue = 0;

            if (!string.IsNullOrEmpty(name))
                _fence.DeviceName = name;
        }

        /// <summary>递增 CPU 计数并 Signal GPU</summary>
        public ulong AdvanceAndSignal(ID3D12CommandQueue commandQueue)
        {
            _cpuCounter++;
            commandQueue.Signal(_fence, _cpuCounter);
            return _cpuCounter;
        }

        /// <summary>等待 GPU 完成</summary>
        public void WaitUntilCompleted(ulong targetValue, uint timeoutMs = uint.MaxValue)
        {
            if (_fence.CompletedValue >= targetValue) return;

            using var evt = new ManualResetEvent(false);
            _fence.SetEventOnCompletion(targetValue, evt.SafeWaitHandle.DangerousGetHandle());

            if (timeoutMs == uint.MaxValue)
                evt.WaitOne();
            else
                evt.WaitOne((int)timeoutMs);
        }

        /// <summary>等待 GPU 完成到当前 CPU 计数</summary>
        public void WaitUntilComplete(uint timeoutMs = uint.MaxValue)
        {
            WaitUntilCompleted(_cpuCounter, timeoutMs);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _fence.Dispose();
        }
    }

    // ==============================================================================================
    //  FrameSynchronizer - 帧同步器
    // ==============================================================================================

    /// <summary>
    /// 帧同步器。管理多缓冲渲染的帧同步。
    /// 支持二缓冲/三缓冲，处理帧编号、WaitForPreviousFrame、SignalFrame、MoveToNextFrame。
    /// </summary>
    public sealed class FrameSynchronizer : IDisposable
    {
        private readonly ID3D12CommandQueue _commandQueue;
        private readonly ID3D12Device _device;
        private readonly D3D12Fence _frameFence;
        private readonly ulong[] _frameFenceValues;
        private readonly EventWaitHandle _frameEvent;

        private int _currentFrameIndex;
        private int _bufferCount;
        private ulong _currentFenceValue;
        private bool _disposed;

        /// <summary>当前帧索引 (0 ~ bufferCount-1)</summary>
        public int CurrentFrameIndex => _currentFrameIndex;

        /// <summary>缓冲数量</summary>
        public int BufferCount => _bufferCount;

        /// <summary>当前 Fence 值</summary>
        public ulong CurrentFenceValue => _currentFenceValue;

        public FrameSynchronizer(ID3D12Device device, ID3D12CommandQueue commandQueue,
            int bufferCount = 2, string name = "")
        {
            _device = device;
            _commandQueue = commandQueue;
            _bufferCount = Math.Clamp(bufferCount, 2, 16);
            _currentFrameIndex = 0;
            _currentFenceValue = 0;

            _frameFence = new D3D12Fence(device, 0, name + "_FrameFence");
            _frameFenceValues = new ulong[_bufferCount];
            for (int i = 0; i < _bufferCount; i++)
                _frameFenceValues[i] = 0;

            _frameEvent = new EventWaitHandle(false, EventResetMode.AutoReset);
        }

        /// <summary>
        /// 等待前一帧完成 (在开始新帧之前调用)。
        /// 确保当前帧索引的缓冲区不再被 GPU 使用。
        /// </summary>
        public void WaitForPreviousFrame()
        {
            // 等待当前帧对应的 Fence 值被 GPU 完成
            ulong targetValue = _frameFenceValues[_currentFrameIndex];

            if (targetValue > 0 && _frameFence.NativeFence.CompletedValue < targetValue)
            {
                // CPU 等待 GPU
                _frameFence.NativeFence.SetEventOnCompletion(
                    targetValue,
                    _frameEvent.SafeWaitHandle.DangerousGetHandle());

                // 使用自适应等待
                CpuWaitHelper.AdaptiveWait(_frameEvent, 500);
            }
        }

        /// <summary>
        /// Signal 当前帧完成。
        /// 在每帧结束时 (Present 之后) 调用。
        /// </summary>
        public ulong SignalFrame()
        {
            _currentFenceValue++;
            _commandQueue.Signal(_frameFence.NativeFence, _currentFenceValue);
            _frameFenceValues[_currentFrameIndex] = _currentFenceValue;
            return _currentFenceValue;
        }

        /// <summary>
        /// 推进到下一帧。
        /// 在 SignalFrame 之后调用。
        /// </summary>
        public void MoveToNextFrame()
        {
            _currentFrameIndex = (_currentFrameIndex + 1) % _bufferCount;
        }

        /// <summary>
        /// 完整的帧同步流程: WaitForPreviousFrame -> (执行帧) -> SignalFrame -> MoveToNextFrame
        /// 返回当前帧索引。
        /// </summary>
        public int BeginFrame()
        {
            WaitForPreviousFrame();
            return _currentFrameIndex;
        }

        /// <summary>
        /// 结束当前帧: Signal 并推进。
        /// </summary>
        public void EndFrame()
        {
            SignalFrame();
            MoveToNextFrame();
        }

        /// <summary>
        /// CPU 等待所有挂起的帧完成。
        /// 用于资源释放前的最终同步。
        /// </summary>
        public void Flush()
        {
            _currentFenceValue++;
            _commandQueue.Signal(_frameFence.NativeFence, _currentFenceValue);
            _frameFence.WaitOnCpu(_currentFenceValue);
        }

        /// <summary>获取指定帧索引是否已完成</summary>
        public bool IsFrameCompleted(int frameIndex)
        {
            if (frameIndex < 0 || frameIndex >= _bufferCount) return false;
            ulong targetValue = _frameFenceValues[frameIndex];
            return targetValue == 0 || _frameFence.NativeFence.CompletedValue >= targetValue;
        }

        /// <summary>获取统计信息</summary>
        public FrameSyncStats GetStats()
        {
            int pendingFrames = 0;
            for (int i = 0; i < _bufferCount; i++)
            {
                if (!IsFrameCompleted(i))
                    pendingFrames++;
            }

            return new FrameSyncStats
            {
                CurrentFrameIndex = _currentFrameIndex,
                BufferCount = _bufferCount,
                PendingFrames = pendingFrames,
                CompletedFenceValue = _frameFence.NativeFence.CompletedValue,
                CurrentFenceValue = _currentFenceValue
            };
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _frameFence?.Dispose();
            _frameEvent?.Dispose();
        }
    }

    /// <summary>帧同步统计</summary>
    public struct FrameSyncStats
    {
        public int CurrentFrameIndex;
        public int BufferCount;
        public int PendingFrames;
        public ulong CompletedFenceValue;
        public ulong CurrentFenceValue;
    }

    // ==============================================================================================
    //  GPU 等待辅助
    // ==============================================================================================

    /// <summary>
    /// GPU 等待辅助工具。提供 ExecuteIndirect 等待和 UAV Barrier 等待的构建。
    /// </summary>
    public static class GpuWaitHelper
    {
        /// <summary>
        /// 创建 ExecuteIndirect 的 CountBuffer 等待条件。
        /// 通过 Predication 实现条件执行。
        /// </summary>
        public static void SetupIndirectPredication(
            D3D12GraphicsCommandList commandList,
            ID3D12Resource predicateBuffer,
            ulong predicateOffset,
            bool executeIfZero = false)
        {
            commandList.SetPredication(predicateBuffer, predicateOffset, executeIfZero);
        }

        /// <summary>
        /// 插入 UAV Barrier 等待。
        /// 确保 UAV 写入对后续 UAV 读取可见。
        /// </summary>
        public static void InsertUAVBarrier(
            D3D12GraphicsCommandList commandList,
            ID3D12Resource? resource = null)
        {
            commandList.ResourceBarrierUAV(resource);
        }

        /// <summary>
        /// 插入 UAV 等待屏障 (批量: 多个资源)。
        /// </summary>
        public static void InsertUAVBarriers(
            D3D12GraphicsCommandList commandList,
            ID3D12Resource[] resources)
        {
            foreach (var resource in resources)
            {
                commandList.ResourceBarrierUAV(resource);
            }
        }

        /// <summary>
        /// 使用 Split Barrier 优化 UAV 等待:
        /// BeginOnly 在写入之后，EndOnly 在读取之前。
        /// </summary>
        public static void InsertSplitUAVBarrier(
            D3D12GraphicsCommandList commandList,
            ID3D12Resource resource,
            SplitBarrierPhase phase)
        {
            if (phase == SplitBarrierPhase.Begin)
            {
                // Begin: 在写入操作之后发出
                // Split barrier 不直接用于 UAV，但可以用于 Transition barrier
                // UAV barrier 本身不需要 split
                commandList.ResourceBarrierUAV(resource);
            }
            else
            {
                // End: 在读取操作之前发出
                commandList.ResourceBarrierUAV(resource);
            }
        }
    }

    /// <summary>Split Barrier 阶段</summary>
    public enum SplitBarrierPhase
    {
        Begin,
        End
    }
}
