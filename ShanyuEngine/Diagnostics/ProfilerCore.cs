using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;

namespace ShanyuEngine.Diagnostics
{
    // ══════════════════════════════════════════════════════════════════
    // 分析器核心（ProfilerCore）
    // ══════════════════════════════════════════════════════════════════
    // 本文件实现山雨引擎性能分析工具系统的核心基础架构，包含：
    //   1. ProfilerCategory：性能采样类别枚举（CPU/GPU/Memory/...）
    //   2. ProfilerMarker：性能标记（名称/类别/颜色/自动采样配置）
    //   3. ProfilerSample：单次采样数据（标记/时间/子采样/线程/深度）
    //   4. ProfilerThread：线程级采样器（采样栈/嵌套/线程信息）
    //   5. ProfilerSession：分析会话（多线程聚合/开始/停止/重置）
    //   6. Profiler 静态便捷入口：BeginSample / EndSample / ConditionalSample
    //
    // 该核心为所有子系统（CPU/GPU/Memory/Network/Audio 分析器）
    // 提供统一的标记、采样与线程管理基础。
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 性能采样类别。覆盖引擎所有主要子系统，
    /// 用于标记分类、颜色编码、过滤与聚合统计。
    /// </summary>
    public enum ProfilerCategory
    {
        /// <summary>CPU 通用计算</summary>
        CPU = 0,
        /// <summary>GPU 渲染 / 计算</summary>
        GPU = 1,
        /// <summary>内存分配 / 释放</summary>
        Memory = 2,
        /// <summary>渲染管线（前向 / 延迟 / 后处理）</summary>
        Rendering = 3,
        /// <summary>物理模拟 / 碰撞检测</summary>
        Physics = 4,
        /// <summary>音频播放 / 混音</summary>
        Audio = 5,
        /// <summary>骨骼动画 / 混合树 / 状态机</summary>
        Animation = 6,
        /// <summary>网络通信 / 同步</summary>
        Network = 7,
        /// <summary>文件 / 资产 IO</summary>
        IO = 8,
        /// <summary>UI 布局 / 绘制</summary>
        UI = 9,
        /// <summary>用户输入处理</summary>
        Input = 10,
        /// <summary>脚本执行（Lua / 蓝图 / C# 热重载）</summary>
        Scripting = 11,
        /// <summary>流式加载（纹理流 / 场景流 / 资产流）</summary>
        Streaming = 12,
        /// <summary>粒子系统</summary>
        Particles = 13,
        /// <summary>场景管理</summary>
        Scene = 14,
        /// <summary>AI / 导航 / 行为树</summary>
        AI = 15,
        /// <summary>其他未分类</summary>
        Other = 255
    }

    /// <summary>
    /// 性能标记。代表一个可复用的分析采样点，
    /// 类似 UE 的 FProfileStatGroup 或 Unity 的 ProfilerMarker。
    /// 标记可在代码中预先定义并重复使用，也可动态创建。
    /// </summary>
    public sealed class ProfilerMarker
    {
        /// <summary>标记名称（如 "Render.Shadows.Cascade0"）</summary>
        public string Name { get; }
        /// <summary>标记类别</summary>
        public ProfilerCategory Category { get; }
        /// <summary>显示颜色（ARGB 格式，用于 UI 和时间线渲染）</summary>
        public uint ColorARGB { get; }
        /// <summary>是否在每帧自动采样（无需手动 Begin/End）</summary>
        public bool AutoSample { get; }
        /// <summary>标记描述（用于报告与工具提示）</summary>
        public string Description { get; set; } = string.Empty;
        /// <summary>标记 ID（内部使用，由 ProfilerSession 分配）</summary>
        internal int Id { get; }

        /// <summary>
        /// 预定义的常用颜色常量。
        /// </summary>
        public static class Colors
        {
            public const uint CPU = 0xFFFFCC00;        // 黄色
            public const uint GPU = 0xFF0088FF;         // 蓝色
            public const uint Memory = 0xFF888888;       // 灰色
            public const uint Rendering = 0xFF00FF88;   // 绿色
            public const uint Physics = 0xFF88FF00;      // 黄绿
            public const uint Audio = 0xFFFF44FF;       // 紫粉
            public const uint Animation = 0xFF44FFFF;    // 青色
            public const uint Network = 0xFF88FF88;      // 浅绿
            public const uint IO = 0xFFFF8800;          // 橙色
            public const uint UI = 0xFFFF4444;          // 红色
            public const uint Input = 0xFFAAAAFF;       // 淡蓝
            public const uint Scripting = 0xFFFFAA44;    // 金色
            public const uint Streaming = 0xFF88CCFF;    // 天蓝
            public const uint Particles = 0xFFFF6688;   // 粉红
            public const uint Scene = 0xFF44AA44;       // 深绿
            public const uint AI = 0xFFAA44AA;           // 紫色
            public const uint Other = 0xFFFFFFFF;         // 白色
        }

        /// <summary>根据类别获取默认颜色</summary>
        public static uint GetDefaultColor(ProfilerCategory category) => category switch
        {
            ProfilerCategory.CPU => Colors.CPU,
            ProfilerCategory.GPU => Colors.GPU,
            ProfilerCategory.Memory => Colors.Memory,
            ProfilerCategory.Rendering => Colors.Rendering,
            ProfilerCategory.Physics => Colors.Physics,
            ProfilerCategory.Audio => Colors.Audio,
            ProfilerCategory.Animation => Colors.Animation,
            ProfilerCategory.Network => Colors.Network,
            ProfilerCategory.IO => Colors.IO,
            ProfilerCategory.UI => Colors.UI,
            ProfilerCategory.Input => Colors.Input,
            ProfilerCategory.Scripting => Colors.Scripting,
            ProfilerCategory.Streaming => Colors.Streaming,
            ProfilerCategory.Particles => Colors.Particles,
            ProfilerCategory.Scene => Colors.Scene,
            ProfilerCategory.AI => Colors.AI,
            _ => Colors.Other
        };

        private static int _nextMarkerId;

        public ProfilerMarker(string name, ProfilerCategory category, uint? color = null, bool autoSample = false)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            Category = category;
            ColorARGB = color ?? GetDefaultColor(category);
            AutoSample = autoSample;
            Id = Interlocked.Increment(ref _nextMarkerId);
        }
    }

    /// <summary>
    /// 单次性能采样数据。记录一个分析标记在一次 Begin/End 期间的完整信息，
    /// 包含时间范围、所属线程、嵌套深度与子采样列表。
    /// </summary>
    public sealed class ProfilerSample
    {
        /// <summary>关联的性能标记</summary>
        public ProfilerMarker Marker { get; }
        /// <summary>开始时间戳（高精度 ticks）</summary>
        public long StartTimestamp { get; internal set; }
        /// <summary>结束时间戳（高精度 ticks）</summary>
        public long EndTimestamp { get; internal set; }
        /// <summary>持续时间（毫秒）</summary>
        public double DurationMs => (EndTimestamp - StartTimestamp) * _tickToMs;
        /// <summary>采样开始的绝对时间（自分析器启动起的毫秒）</summary>
        public double StartTimeMs => (StartTimestamp - _sessionStartTimestamp) * _tickToMs;
        /// <summary>所属线程 ID</summary>
        public int ThreadId { get; }
        /// <summary>所属线程名称</summary>
        public string ThreadName { get; }
        /// <summary>调用栈嵌套深度（0 = 根级）</summary>
        public int Depth { get; }
        /// <summary>子采样列表（嵌套在当前采样内的所有子标记采样）</summary>
        public List<ProfilerSample> Children { get; } = new();
        /// <summary>父采样（根级为 null）</summary>
        public ProfilerSample? Parent { get; internal set; }
        /// <summary>附加的元数据（用于自定义标记携带额外数据）</summary>
        public Dictionary<string, object>? Metadata { get; set; }

        private static readonly double _tickToMs = 1000.0 / Stopwatch.Frequency;
        private static long _sessionStartTimestamp;

        internal static void SetSessionStart(long timestamp) => _sessionStartTimestamp = timestamp;

        public ProfilerSample(ProfilerMarker marker, int threadId, string threadName, int depth)
        {
            Marker = marker;
            ThreadId = threadId;
            ThreadName = threadName;
            Depth = depth;
        }
    }

    /// <summary>
    /// 线程级采样器。管理单个线程上的采样栈，支持嵌套 Begin/End，
    /// 自动维护父子关系和深度信息。
    /// </summary>
    public sealed class ProfilerThread
    {
        /// <summary>线程 ID</summary>
        public int ThreadId { get; }
        /// <summary>线程名称</summary>
        public string ThreadName { get; }
        /// <summary>线程类型</summary>
        public ProfilerThreadType ThreadType { get; set; }
        /// <summary>当前采样栈深度</summary>
        public int CurrentDepth => _stack.Count;

        private readonly Stack<ProfilerSample> _stack = new();
        private readonly List<ProfilerSample> _rootSamples = new();

        /// <summary>当前线程所有根级采样（已完成的）</summary>
        public IReadOnlyList<ProfilerSample> RootSamples => _rootSamples;

        /// <summary>该线程在整个会话中的累计活跃时间（毫秒）</summary>
        public double TotalActiveTimeMs { get; private set; }

        /// <summary>该线程的采样次数</summary>
        public long SampleCount { get; private set; }

        public ProfilerThread(int threadId, string threadName, ProfilerThreadType threadType = ProfilerThreadType.Other)
        {
            ThreadId = threadId;
            ThreadName = threadName;
            ThreadType = threadType;
        }

        /// <summary>开始一个采样（压入栈）</summary>
        public ProfilerSample BeginSample(ProfilerMarker marker)
        {
            var sample = new ProfilerSample(marker, ThreadId, ThreadName, _stack.Count)
            {
                StartTimestamp = Stopwatch.GetTimestamp()
            };

            if (_stack.Count > 0)
            {
                var parent = _stack.Peek();
                parent.Children.Add(sample);
                sample.Parent = parent;
            }

            _stack.Push(sample);
            return sample;
        }

        /// <summary>结束当前采样（弹出栈）</summary>
        public ProfilerSample? EndSample()
        {
            if (_stack.Count == 0) return null;

            var sample = _stack.Pop();
            sample.EndTimestamp = Stopwatch.GetTimestamp();

            if (_stack.Count == 0)
            {
                _rootSamples.Add(sample);
            }

            TotalActiveTimeMs += sample.DurationMs;
            SampleCount++;
            return sample;
        }

        /// <summary>结束当前采样并返回持续时间</summary>
        public double EndSampleAndGetDuration()
        {
            var sample = EndSample();
            return sample?.DurationMs ?? 0;
        }

        /// <summary>获取当前栈顶的采样（未结束）</summary>
        public ProfilerSample? CurrentSample => _stack.Count > 0 ? _stack.Peek() : null;

        /// <summary>清除所有采样数据</summary>
        public void Clear()
        {
            _stack.Clear();
            _rootSamples.Clear();
            TotalActiveTimeMs = 0;
            SampleCount = 0;
        }
    }

    /// <summary>
    /// 分析会话。管理一次完整的性能分析运行，聚合所有线程的采样数据，
    /// 提供开始/停止/重置的生命周期控制。
    /// </summary>
    public sealed class ProfilerSession
    {
        /// <summary>会话 ID</summary>
        public Guid SessionId { get; } = Guid.NewGuid();
        /// <summary>会话名称</summary>
        public string Name { get; set; } = "Default";
        /// <summary>会话状态</summary>
        public ProfilerSessionState State { get; private set; } = ProfilerSessionState.Stopped;
        /// <summary>会话开始时间</summary>
        public DateTime StartTime { get; private set; }
        /// <summary>会话持续时间（毫秒）</summary>
        public double DurationMs => State == ProfilerSessionState.Recording
            ? (DateTime.UtcNow - StartTime).TotalMilliseconds
            : _stoppedDurationMs;
        private double _stoppedDurationMs;

        /// <summary>会话内所有线程采样器</summary>
        private readonly Dictionary<int, ProfilerThread> _threads = new();
        private readonly object _threadLock = new();

        /// <summary>标记注册表（名称 -> 标记）</summary>
        private readonly Dictionary<string, ProfilerMarker> _markers = new();
        private readonly object _markerLock = new();

        /// <summary>自动采样标记列表</summary>
        private readonly List<ProfilerMarker> _autoMarkers = new();

        /// <summary>采样帧计数</summary>
        public ulong FrameCount { get; private set; }
        /// <summary>捕获的最大帧时间（毫秒）</summary>
        public double MaxFrameTimeMs { get; private set; }
        /// <summary>捕获的最小帧时间（毫秒）</summary>
        public double MinFrameTimeMs { get; private set; } = double.MaxValue;

        /// <summary>帧时间历史</summary>
        private readonly List<double> _frameTimes = new();
        private const int MaxFrameHistory = 4096;

        /// <summary>会话开始时的全局时间戳</summary>
        private long _sessionStartTimestamp;

        /// <summary>事件：采样超出阈值时触发</summary>
        public event Action<ProfilerSample>? OnSampleThresholdExceeded;
        /// <summary>事件：会话状态变化时触发</summary>
        public event Action<ProfilerSession, ProfilerSessionState>? OnStateChanged;

        /// <summary>采样阈值（毫秒），超过此值的采样将触发事件</summary>
        public double SampleThresholdMs { get; set; } = 10.0;

        /// <summary>获取所有线程采样器（只读快照）</summary>
        public IReadOnlyDictionary<int, ProfilerThread> Threads
        {
            get
            {
                lock (_threadLock)
                {
                    return new Dictionary<int, ProfilerThread>(_threads);
                }
            }
        }

        /// <summary>获取所有已注册标记（只读快照）</summary>
        public IReadOnlyDictionary<string, ProfilerMarker> Markers
        {
            get
            {
                lock (_markerLock)
                {
                    return new Dictionary<string, ProfilerMarker>(_markers);
                }
            }
        }

        /// <summary>获取会话内所有采样（从所有线程聚合，深度优先遍历）</summary>
        public List<ProfilerSample> GetAllSamples()
        {
            var result = new List<ProfilerSample>();
            lock (_threadLock)
            {
                foreach (var thread in _threads.Values)
                {
                    CollectSamples(thread.RootSamples, result);
                }
            }
            return result;
        }

        private static void CollectSamples(List<ProfilerSample> samples, List<ProfilerSample> output)
        {
            for (int i = 0; i < samples.Count; i++)
            {
                output.Add(samples[i]);
                if (samples[i].Children.Count > 0)
                {
                    CollectSamples(samples[i].Children, output);
                }
            }
        }

        /// <summary>注册一个性能标记</summary>
        public ProfilerMarker RegisterMarker(ProfilerMarker marker)
        {
            lock (_markerLock)
            {
                if (_markers.TryGetValue(marker.Name, out var existing))
                    return existing;
                _markers[marker.Name] = marker;
                if (marker.AutoSample)
                    _autoMarkers.Add(marker);
                return marker;
            }
        }

        /// <summary>获取或创建标记</summary>
        public ProfilerMarker GetOrCreateMarker(string name, ProfilerCategory category, uint? color = null)
        {
            lock (_markerLock)
            {
                if (_markers.TryGetValue(name, out var existing))
                    return existing;
                var marker = new ProfilerMarker(name, category, color);
                _markers[name] = marker;
                return marker;
            }
        }

        /// <summary>获取当前线程的采样器（不存在则自动创建）</summary>
        public ProfilerThread GetOrCreateThread(int threadId, string threadName, ProfilerThreadType threadType = ProfilerThreadType.Other)
        {
            lock (_threadLock)
            {
                if (!_threads.TryGetValue(threadId, out var thread))
                {
                    thread = new ProfilerThread(threadId, threadName, threadType);
                    _threads[threadId] = thread;
                }
                thread.ThreadType = threadType;
                return thread;
            }
        }

        /// <summary>开始录制</summary>
        public void Start()
        {
            if (State == ProfilerSessionState.Recording) return;
            State = ProfilerSessionState.Recording;
            StartTime = DateTime.UtcNow;
            _sessionStartTimestamp = Stopwatch.GetTimestamp();
            ProfilerSample.SetSessionStart(_sessionStartTimestamp);
            OnStateChanged?.Invoke(this, State);
        }

        /// <summary>停止录制</summary>
        public void Stop()
        {
            if (State != ProfilerSessionState.Recording) return;
            State = ProfilerSessionState.Stopped;
            _stoppedDurationMs = (DateTime.UtcNow - StartTime).TotalMilliseconds;
            OnStateChanged?.Invoke(this, State);
        }

        /// <summary>重置所有采样数据（可保留标记注册）</summary>
        public void Reset()
        {
            State = ProfilerSessionState.Stopped;
            FrameCount = 0;
            MaxFrameTimeMs = 0;
            MinFrameTimeMs = double.MaxValue;
            _frameTimes.Clear();
            _stoppedDurationMs = 0;

            lock (_threadLock)
            {
                foreach (var thread in _threads.Values)
                    thread.Clear();
            }
        }

        /// <summary>记录一帧完成</summary>
        public void RecordFrame(double frameTimeMs)
        {
            FrameCount++;
            if (frameTimeMs > MaxFrameTimeMs) MaxFrameTimeMs = frameTimeMs;
            if (frameTimeMs < MinFrameTimeMs) MinFrameTimeMs = frameTimeMs;

            _frameTimes.Add(frameTimeMs);
            if (_frameTimes.Count > MaxFrameHistory)
                _frameTimes.RemoveAt(0);
        }

        /// <summary>获取帧时间历史</summary>
        public double[] GetFrameTimeHistory()
        {
            lock (_threadLock)
            {
                var arr = new double[_frameTimes.Count];
                for (int i = 0; i < _frameTimes.Count; i++)
                    arr[i] = _frameTimes[i];
                return arr;
            }
        }

        /// <summary>生成会话摘要</summary>
        public ProfilerSessionSummary GetSummary()
        {
            var summary = new ProfilerSessionSummary
            {
                SessionId = SessionId,
                Name = Name,
                DurationMs = DurationMs,
                FrameCount = FrameCount,
                MaxFrameTimeMs = MaxFrameTimeMs,
                MinFrameTimeMs = MinFrameTimeMs,
                AvgFrameTimeMs = FrameCount > 0 ? _frameTimes.Count > 0
                    ? _frameTimes[_frameTimes.Count - 1] : 0 : 0,
                ThreadCount = _threads.Count,
                MarkerCount = _markers.Count
            };

            if (_frameTimes.Count > 0)
            {
                double sum = 0;
                for (int i = 0; i < _frameTimes.Count; i++)
                    sum += _frameTimes[i];
                summary.AvgFrameTimeMs = sum / _frameTimes.Count;
            }

            return summary;
        }
    }

    /// <summary>
    /// 会话状态枚举。
    /// </summary>
    public enum ProfilerSessionState
    {
        /// <summary>未录制</summary>
        Stopped,
        /// <summary>正在录制</summary>
        Recording,
        /// <summary>已暂停（保留数据）</summary>
        Paused
    }

    /// <summary>
    /// 会话摘要数据。
    /// </summary>
    public sealed class ProfilerSessionSummary
    {
        public Guid SessionId { get; set; }
        public string Name { get; set; } = string.Empty;
        public double DurationMs { get; set; }
        public ulong FrameCount { get; set; }
        public double MaxFrameTimeMs { get; set; }
        public double MinFrameTimeMs { get; set; }
        public double AvgFrameTimeMs { get; set; }
        public int ThreadCount { get; set; }
        public int MarkerCount { get; set; }
    }

    // ══════════════════════════════════════════════════════════════════
    // 便捷静态入口（Profiler 便捷 API）
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 分析器便捷静态入口。提供 BeginSample / EndSample 等简洁 API，
    /// 底层操作默认会话（<see cref="DefaultSession"/>）。
    /// </summary>

    /// <summary>
    /// 采样作用域。配合 using 语句自动 Begin/End 采样。
    /// <code>using (Profiler.SampleScope("Update.GameLogic")) { ... }</code>
    /// </summary>
    public readonly struct ProfilerSampleScope : IDisposable
    {
        private readonly bool _active;

        public ProfilerSampleScope(string name, ProfilerCategory category)
        {
            _active = Profiler.ActiveSession.State == ProfilerSessionState.Recording;
            if (_active)
                Profiler.BeginSample(name, category);
        }

        public void Dispose()
        {
            if (_active)
                Profiler.EndSample();
        }
    }
}
