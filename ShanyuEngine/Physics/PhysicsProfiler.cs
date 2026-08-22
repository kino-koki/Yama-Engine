using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;

namespace ShanyuEngine.Physics
{
    // ============================================================================================
    //  PhysicsProfiler.cs
    //
    //  物理性能分析器。记录物理引擎各阶段的耗时、调用次数、内存使用等。
    //  用来定位性能瓶颈。
    //
    //  用法:
    //    using (profiler.BeginScope("Broadphase"))
    //    {
    //        // 做宽相位检测
    //    }
    //    profiler.EndFrame();
    //    var report = profiler.GenerateReport();
    //
    //  注意: 不要在正式发布版开profiler，会有额外开销。
    //  TODO: 加上多线程profiling(ThreadId)。
    // ============================================================================================

    /// <summary>性能分析节段类型。</summary>
    public enum ProfileSectionType
    {
        Total,
        Broadphase,
        Narrowphase,
        ContactSolver,
        ConstraintSolver,
        Integration,
        SleepManagement,
        IslandDecomposition,
        BodyUpdate,
        ShapeUpdate,
        CCD,
        ParticleUpdate,
        SoftBody,
        FluidSim,
        ClothSim,
        RopeSim,
        Destruction,
        Vehicle,
        Character,
        Ragdoll,
        Buoyancy,
        WindField,
        Magnetism,
        Serialization,
        DebugDraw,
        Custom,
    }

    /// <summary>性能统计条目。</summary>
    public struct ProfileEntry
    {
        public string Name;
        public ProfileSectionType Type;
        public double TotalTimeMs;
        public double MaxTimeMs;
        public double MinTimeMs = double.MaxValue;
        public long CallCount;
        public long FrameCount;
        public double LastTimeMs;
        public double AvgTimeMs;
        public DateTime FirstRecord;
        public DateTime LastRecord;

        public ProfileEntry(string name, ProfileSectionType type)
        {
            Name = name;
            Type = type;
            FirstRecord = DateTime.Now;
            LastRecord = FirstRecord;
        }

        public void Record(double timeMs)
        {
            TotalTimeMs += timeMs;
            if (timeMs > MaxTimeMs) MaxTimeMs = timeMs;
            if (timeMs < MinTimeMs) MinTimeMs = timeMs;
            CallCount++;
            LastTimeMs = timeMs;
            LastRecord = DateTime.Now;
            AvgTimeMs = CallCount > 0 ? TotalTimeMs / CallCount : 0;
        }

        public void Reset()
        {
            TotalTimeMs = 0;
            MaxTimeMs = 0;
            MinTimeMs = double.MaxValue;
            CallCount = 0;
            FrameCount = 0;
            LastTimeMs = 0;
            AvgTimeMs = 0;
            FirstRecord = DateTime.Now;
            LastRecord = FirstRecord;
        }
    }

    /// <summary>帧统计。</summary>
    public struct FrameStats
    {
        public int FrameIndex;
        public double TotalTimeMs;
        public double BroadphaseMs;
        public double NarrowphaseMs;
        public double SolverMs;
        public double IntegrationMs;
        public double OtherMs;
        public int BodyCount;
        public int ActiveBodyCount;
        public int ContactCount;
        public int ConstraintCount;
        public int IslandCount;
        public int PairCount;
        public long MemoryUsageBytes;
        public DateTime Timestamp;
    }

    /// <summary>计数器条目。</summary>
    public struct CounterEntry
    {
        public string Name;
        public long Value;
        public long MinValue = long.MaxValue;
        public long MaxValue;
        public long TotalValue;
        public long SampleCount;
        public double AvgValue => SampleCount > 0 ? (double)TotalValue / SampleCount : 0;

        public CounterEntry(string name) { Name = name; }

        public void Set(long value)
        {
            Value = value;
            if (value < MinValue) MinValue = value;
            if (value > MaxValue) MaxValue = value;
            TotalValue += value;
            SampleCount++;
        }

        public void Increment(long delta = 1)
        {
            Set(Value + delta);
        }

        public void Reset()
        {
            Value = 0;
            MinValue = long.MaxValue;
            MaxValue = 0;
            TotalValue = 0;
            SampleCount = 0;
        }
    }

    /// <summary>
    ///   作用域计时器。用using语法自动计时段。
    /// </summary>
    public readonly struct ProfileScope : IDisposable
    {
        private readonly PhysicsProfiler _profiler;
        private readonly string _name;
        private readonly ProfileSectionType _type;
        private readonly long _startTime;
        private readonly int _threadId;

        public ProfileScope(PhysicsProfiler profiler, string name, ProfileSectionType type)
        {
            _profiler = profiler;
            _name = name;
            _type = type;
            _startTime = Stopwatch.GetTimestamp();
            _threadId = Environment.CurrentManagedThreadId;
        }

        public void Dispose()
        {
            if (_profiler == null) return;
            long elapsed = Stopwatch.GetTimestamp() - _startTime;
            double ms = (double)elapsed / Stopwatch.Frequency * 1000.0;
            _profiler.RecordSection(_name, _type, ms);
        }
    }

    /// <summary>
    ///   物理性能分析器。
    /// </summary>
    public sealed class PhysicsProfiler
    {
        private readonly Dictionary<string, ProfileEntry> _sections = new();
        private readonly Dictionary<string, CounterEntry> _counters = new();
        private readonly Queue<FrameStats> _frameHistory = new();
        private readonly int _maxFrameHistory;
        private readonly object _lock = new();

        private bool _enabled = true;
        private int _frameIndex;
        private long _frameStartTime;
        private FrameStats _currentFrame;
        private bool _frameStarted;

        // 当前作用域栈(支持嵌套)
        private readonly Stack<(string name, ProfileSectionType type, long start)> _scopeStack = new();

        // 内存跟踪
        private long _baselineMemory;
        private long _peakMemory;

        // 事件
        public event Action<FrameStats>? FrameCompleted;
        public event Action<string, ProfileSectionType, double>? SectionRecorded;
        public event Action<string, long>? CounterChanged;

        public bool Enabled
        {
            get => _enabled;
            set => _enabled = value;
        }
        public int FrameCount => _frameIndex;
        public int HistoryCount => _frameHistory.Count;
        public long CurrentMemoryBytes => GC.GetTotalMemory(false);
        public long PeakMemoryBytes => _peakMemory;
        public long BaselineMemoryBytes => _baselineMemory;

        public PhysicsProfiler(int maxFrameHistory = 300)
        {
            _maxFrameHistory = maxFrameHistory;
            _baselineMemory = GC.GetTotalMemory(false);
        }

        // ========================================================================================
        //  帧管理
        // ========================================================================================

        /// <summary>开始一帧。</summary>
        public void BeginFrame()
        {
            if (!_enabled) return;
            _frameStartTime = Stopwatch.GetTimestamp();
            _frameIndex++;
            _frameStarted = true;
            _currentFrame = new FrameStats
            {
                FrameIndex = _frameIndex,
                Timestamp = DateTime.Now,
            };
        }

        /// <summary>结束一帧。</summary>
        public void EndFrame()
        {
            if (!_enabled || !_frameStarted) return;
            _frameStarted = false;

            long elapsed = Stopwatch.GetTimestamp() - _frameStartTime;
            _currentFrame.TotalTimeMs = (double)elapsed / Stopwatch.Frequency * 1000.0;
            _currentFrame.OtherMs = _currentFrame.TotalTimeMs -
                _currentFrame.BroadphaseMs - _currentFrame.NarrowphaseMs -
                _currentFrame.SolverMs - _currentFrame.IntegrationMs;

            long mem = GC.GetTotalMemory(false);
            _currentFrame.MemoryUsageBytes = mem;
            if (mem > _peakMemory) _peakMemory = mem;

            lock (_lock)
            {
                _frameHistory.Enqueue(_currentFrame);
                while (_frameHistory.Count > _maxFrameHistory)
                    _frameHistory.Dequeue();
            }

            // 每个section的frame count+1
            foreach (var s in _sections.Values)
                s.FrameCount++;

            FrameCompleted?.Invoke(_currentFrame);
        }

        /// <summary>记录section耗时。</summary>
        public void RecordSection(string name, ProfileSectionType type, double timeMs)
        {
            if (!_enabled) return;

            lock (_lock)
            {
                if (!_sections.TryGetValue(name, out var entry))
                {
                    entry = new ProfileEntry(name, type);
                    _sections[name] = entry;
                }
                entry.Record(timeMs);
            }

            // 更新当前帧
            switch (type)
            {
                case ProfileSectionType.Broadphase:
                    _currentFrame.BroadphaseMs += timeMs;
                    break;
                case ProfileSectionType.Narrowphase:
                    _currentFrame.NarrowphaseMs += timeMs;
                    break;
                case ProfileSectionType.ContactSolver:
                case ProfileSectionType.ConstraintSolver:
                    _currentFrame.SolverMs += timeMs;
                    break;
                case ProfileSectionType.Integration:
                    _currentFrame.IntegrationMs += timeMs;
                    break;
            }

            SectionRecorded?.Invoke(name, type, timeMs);
        }

        // ========================================================================================
        //  作用域
        // ========================================================================================

        /// <summary>开始一个作用域(用using语法)。</summary>
        public ProfileScope BeginScope(string name, ProfileSectionType type = ProfileSectionType.Custom)
        {
            if (!_enabled) return default;
            return new ProfileScope(this, name, type);
        }

        /// <summary>开始计时(手动)。</summary>
        public void BeginManualScope(string name, ProfileSectionType type = ProfileSectionType.Custom)
        {
            if (!_enabled) return;
            _scopeStack.Push((name, type, Stopwatch.GetTimestamp()));
        }

        /// <summary>结束计时(手动)。</summary>
        public void EndManualScope()
        {
            if (!_enabled || _scopeStack.Count == 0) return;
            var (name, type, start) = _scopeStack.Pop();
            long elapsed = Stopwatch.GetTimestamp() - start;
            double ms = (double)elapsed / Stopwatch.Frequency * 1000.0;
            RecordSection(name, type, ms);
        }

        // ========================================================================================
        //  计数器
        // ========================================================================================

        /// <summary>设置计数器值。</summary>
        public void SetCounter(string name, long value)
        {
            if (!_enabled) return;
            lock (_lock)
            {
                if (!_counters.TryGetValue(name, out var c))
                {
                    c = new CounterEntry(name);
                    _counters[name] = c;
                }
                c.Set(value);
            }
            CounterChanged?.Invoke(name, value);
        }

        /// <summary>增加计数器。</summary>
        public void IncrementCounter(string name, long delta = 1)
        {
            if (!_enabled) return;
            lock (_lock)
            {
                if (!_counters.TryGetValue(name, out var c))
                {
                    c = new CounterEntry(name);
                    _counters[name] = c;
                }
                c.Increment(delta);
            }
        }

        /// <summary>获取计数器。</summary>
        public CounterEntry? GetCounter(string name)
        {
            return _counters.TryGetValue(name, out var c) ? c : null;
        }

        // ========================================================================================
        //  帧统计
        // ========================================================================================

        /// <summary>设置当前帧的body数。</summary>
        public void SetBodyCount(int count) => _currentFrame.BodyCount = count;
        public void SetActiveBodyCount(int count) => _currentFrame.ActiveBodyCount = count;
        public void SetContactCount(int count) => _currentFrame.ContactCount = count;
        public void SetConstraintCount(int count) => _currentFrame.ConstraintCount = count;
        public void SetIslandCount(int count) => _currentFrame.IslandCount = count;
        public void SetPairCount(int count) => _currentFrame.PairCount = count;

        // ========================================================================================
        //  报告
        // ========================================================================================

        /// <summary>生成文本报告。</summary>
        public string GenerateReport(int lastNFrames = 60)
        {
            var sb = new StringBuilder();
            sb.AppendLine("==== Physics Profiler Report ====");
            sb.AppendLine($"Frame Count: {_frameIndex}");
            sb.AppendLine($"History: {Math.Min(lastNFrames, _frameHistory.Count)} frames");
            sb.AppendLine();

            // 帧时间统计
            var frames = _frameHistory.ToArray();
            int startIdx = Math.Max(0, frames.Length - lastNFrames);
            int count = frames.Length - startIdx;
            if (count > 0)
            {
                double totalMs = 0, maxMs = 0, minMs = double.MaxValue;
                double avgBroad = 0, avgNarrow = 0, avgSolver = 0, avgInteg = 0;
                int maxBodies = 0, maxContacts = 0;

                for (int i = startIdx; i < frames.Length; i++)
                {
                    var f = frames[i];
                    totalMs += f.TotalTimeMs;
                    if (f.TotalTimeMs > maxMs) maxMs = f.TotalTimeMs;
                    if (f.TotalTimeMs < minMs) minMs = f.TotalTimeMs;
                    avgBroad += f.BroadphaseMs;
                    avgNarrow += f.NarrowphaseMs;
                    avgSolver += f.SolverMs;
                    avgInteg += f.IntegrationMs;
                    if (f.BodyCount > maxBodies) maxBodies = f.BodyCount;
                    if (f.ContactCount > maxContacts) maxContacts = f.ContactCount;
                }

                sb.AppendLine("-- Frame Times --");
                sb.AppendLine($"  Average: {totalMs / count:F3} ms");
                sb.AppendLine($"  Max:     {maxMs:F3} ms");
                sb.AppendLine($"  Min:     {minMs:F3} ms");
                sb.AppendLine($"  FPS:     {1000.0 / (totalMs / count):F1}");
                sb.AppendLine();
                sb.AppendLine("-- Stage Breakdown (avg ms) --");
                sb.AppendLine($"  Broadphase:    {avgBroad / count:F3}");
                sb.AppendLine($"  Narrowphase:   {avgNarrow / count:F3}");
                sb.AppendLine($"  Solver:        {avgSolver / count:F3}");
                sb.AppendLine($"  Integration:   {avgInteg / count:F3}");
                sb.AppendLine($"  Other:         {(totalMs - avgBroad - avgNarrow - avgSolver - avgInteg) / count:F3}");
                sb.AppendLine();
                sb.AppendLine("-- Counts --");
                sb.AppendLine($"  Max Bodies:    {maxBodies}");
                sb.AppendLine($"  Max Contacts:  {maxContacts}");
            }

            // 各section统计
            sb.AppendLine();
            sb.AppendLine("-- Sections --");
            sb.AppendLine($"  {"Name",-30} {"Calls",-10} {"Total(ms)",-12} {"Avg(ms)",-10} {"Max(ms)",-10} {"Min(ms)",-10}");
            foreach (var kvp in _sections)
            {
                var s = kvp.Value;
                sb.AppendLine($"  {s.Name,-30} {s.CallCount,-10} {s.TotalTimeMs,-12:F3} {s.AvgTimeMs,-10:F3} {s.MaxTimeMs,-10:F3} {s.MinTimeMs,-10:F3}");
            }

            // 计数器
            if (_counters.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("-- Counters --");
                sb.AppendLine($"  {"Name",-30} {"Value",-10} {"Avg",-10} {"Min",-10} {"Max",-10}");
                foreach (var kvp in _counters)
                {
                    var c = kvp.Value;
                    sb.AppendLine($"  {c.Name,-30} {c.Value,-10} {c.AvgValue,-10:F1} {c.MinValue,-10} {c.MaxValue,-10}");
                }
            }

            // 内存
            sb.AppendLine();
            sb.AppendLine("-- Memory --");
            sb.AppendLine($"  Current:  {CurrentMemoryBytes / 1024.0 / 1024.0:F2} MB");
            sb.AppendLine($"  Peak:     {_peakMemory / 1024.0 / 1024.0:F2} MB");
            sb.AppendLine($"  Baseline: {_baselineMemory / 1024.0 / 1024.0:F2} MB");

            sb.AppendLine("=================================");
            return sb.ToString();
        }

        /// <summary>获取最近N帧的统计。</summary>
        public FrameStats[] GetRecentFrames(int count)
        {
            var frames = _frameHistory.ToArray();
            int start = Math.Max(0, frames.Length - count);
            var result = new FrameStats[frames.Length - start];
            Array.Copy(frames, start, result, 0, result.Length);
            return result;
        }

        /// <summary>获取所有section。</summary>
        public IReadOnlyDictionary<string, ProfileEntry> GetSections() => _sections;

        /// <summary>获取所有计数器。</summary>
        public IReadOnlyDictionary<string, CounterEntry> GetCounters() => _counters;

        // ========================================================================================
        //  重置/清理
        // ========================================================================================

        /// <summary>重置所有统计。</summary>
        public void Reset()
        {
            lock (_lock)
            {
                foreach (var s in _sections.Values)
                    s.Reset();
                foreach (var c in _counters.Values)
                    c.Reset();
                _frameHistory.Clear();
                _frameIndex = 0;
                _peakMemory = 0;
                _baselineMemory = GC.GetTotalMemory(false);
            }
        }

        /// <summary>清除所有section和counter。</summary>
        public void Clear()
        {
            lock (_lock)
            {
                _sections.Clear();
                _counters.Clear();
                _frameHistory.Clear();
                _frameIndex = 0;
            }
        }

        // ========================================================================================
        //  导出
        // ========================================================================================

        /// <summary>导出为CSV。</summary>
        public string ExportCSV()
        {
            var sb = new StringBuilder();
            sb.AppendLine("Frame,TotalMs,BroadphaseMs,NarrowphaseMs,SolverMs,IntegrationMs,OtherMs," +
                          "Bodies,ActiveBodies,Contacts,Constraints,Islands,Pairs,MemoryMB");

            foreach (var f in _frameHistory)
            {
                sb.AppendLine($"{f.FrameIndex},{f.TotalTimeMs:F3},{f.BroadphaseMs:F3},{f.NarrowphaseMs:F3}," +
                              $"{f.SolverMs:F3},{f.IntegrationMs:F3},{f.OtherMs:F3}," +
                              $"{f.BodyCount},{f.ActiveBodyCount},{f.ContactCount},{f.ConstraintCount}," +
                              $"{f.IslandCount},{f.PairCount},{f.MemoryUsageBytes / 1048576.0:F2}");
            }
            return sb.ToString();
        }

        /// <summary>导出section统计为CSV。</summary>
        public string ExportSectionsCSV()
        {
            var sb = new StringBuilder();
            sb.AppendLine("Name,Type,Calls,TotalMs,AvgMs,MaxMs,MinMs");
            foreach (var s in _sections.Values)
            {
                sb.AppendLine($"{s.Name},{s.Type},{s.CallCount},{s.TotalTimeMs:F3},{s.AvgTimeMs:F3},{s.MaxTimeMs:F3},{s.MinTimeMs:F3}");
            }
            return sb.ToString();
        }
    }

    // ============================================================================================
    //  性能警告系统
    // ============================================================================================

    /// <summary>性能警告类型。</summary>
    public enum PerformanceWarningType
    {
        SlowFrame,
        SlowBroadphase,
        SlowNarrowphase,
        SlowSolver,
        SlowIntegration,
        TooManyBodies,
        TooManyContacts,
        TooManyPairs,
        HighMemoryUsage,
        SpikeDetected,
        Custom,
    }

    /// <summary>性能警告。</summary>
    public struct PerformanceWarning
    {
        public PerformanceWarningType Type;
        public string Message;
        public double Value;
        public double Threshold;
        public int FrameIndex;
        public DateTime Timestamp;
    }

    /// <summary>性能警告配置。</summary>
    public sealed class PerformanceWarningConfig
    {
        public double FrameTimeThreshold = 20.0;       // ms
        public double BroadphaseThreshold = 5.0;
        public double NarrowphaseThreshold = 8.0;
        public double SolverThreshold = 6.0;
        public double IntegrationThreshold = 3.0;
        public int MaxBodies = 20000;
        public int MaxContacts = 50000;
        public int MaxPairs = 100000;
        public double MemoryThresholdMB = 500.0;
        public double SpikeMultiplier = 3.0;           // 超过平均N倍算spike
        public bool EnableWarnings = true;
    }

    /// <summary>性能警告系统。</summary>
    public sealed class PerformanceWarningSystem
    {
        private readonly PhysicsProfiler _profiler;
        private PerformanceWarningConfig _config = new();
        private readonly Queue<PerformanceWarning> _warnings = new();
        private readonly int _maxWarnings = 100;
        private double _avgFrameTime;
        private int _frameSamples;

        public PerformanceWarningConfig Config
        {
            get => _config;
            set => _config = value;
        }

        public event Action<PerformanceWarning>? WarningRaised;

        public PerformanceWarningSystem(PhysicsProfiler profiler)
        {
            _profiler = profiler;
            _profiler.FrameCompleted += OnFrameCompleted;
        }

        private void OnFrameCompleted(FrameStats frame)
        {
            if (!_config.EnableWarnings) return;

            // 更新平均帧时间
            _avgFrameTime = (_avgFrameTime * _frameSamples + frame.TotalTimeMs) / (_frameSamples + 1);
            _frameSamples++;
            if (_frameSamples > 60) _frameSamples = 60;

            // 检查各种阈值
            CheckThreshold(frame.TotalTimeMs, _config.FrameTimeThreshold,
                PerformanceWarningType.SlowFrame, "Frame time too high", frame.FrameIndex);

            CheckThreshold(frame.BroadphaseMs, _config.BroadphaseThreshold,
                PerformanceWarningType.SlowBroadphase, "Broadphase too slow", frame.FrameIndex);

            CheckThreshold(frame.NarrowphaseMs, _config.NarrowphaseThreshold,
                PerformanceWarningType.SlowNarrowphase, "Narrowphase too slow", frame.FrameIndex);

            CheckThreshold(frame.SolverMs, _config.SolverThreshold,
                PerformanceWarningType.SlowSolver, "Solver too slow", frame.FrameIndex);

            CheckThreshold(frame.IntegrationMs, _config.IntegrationThreshold,
                PerformanceWarningType.SlowIntegration, "Integration too slow", frame.FrameIndex);

            if (frame.BodyCount > _config.MaxBodies)
                RaiseWarning(PerformanceWarningType.TooManyBodies,
                    $"Body count {frame.BodyCount} exceeds max {_config.MaxBodies}",
                    frame.BodyCount, _config.MaxBodies, frame.FrameIndex);

            if (frame.ContactCount > _config.MaxContacts)
                RaiseWarning(PerformanceWarningType.TooManyContacts,
                    $"Contact count {frame.ContactCount} exceeds max {_config.MaxContacts}",
                    frame.ContactCount, _config.MaxContacts, frame.FrameIndex);

            if (frame.PairCount > _config.MaxPairs)
                RaiseWarning(PerformanceWarningType.TooManyPairs,
                    $"Pair count {frame.PairCount} exceeds max {_config.MaxPairs}",
                    frame.PairCount, _config.MaxPairs, frame.FrameIndex);

            double memMB = frame.MemoryUsageBytes / 1048576.0;
            if (memMB > _config.MemoryThresholdMB)
                RaiseWarning(PerformanceWarningType.HighMemoryUsage,
                    $"Memory usage {memMB:F1}MB exceeds threshold {_config.MemoryThresholdMB}MB",
                    memMB, _config.MemoryThresholdMB, frame.FrameIndex);

            // Spike检测
            if (_frameSamples > 10 && frame.TotalTimeMs > _avgFrameTime * _config.SpikeMultiplier)
            {
                RaiseWarning(PerformanceWarningType.SpikeDetected,
                    $"Frame spike: {frame.TotalTimeMs:F1}ms vs avg {_avgFrameTime:F1}ms",
                    frame.TotalTimeMs, _avgFrameTime * _config.SpikeMultiplier, frame.FrameIndex);
            }
        }

        private void CheckThreshold(double value, double threshold,
            PerformanceWarningType type, string message, int frameIdx)
        {
            if (value > threshold)
                RaiseWarning(type, $"{message}: {value:F2}ms > {threshold:F2}ms",
                    value, threshold, frameIdx);
        }

        private void RaiseWarning(PerformanceWarningType type, string message,
            double value, double threshold, int frameIdx)
        {
            var warning = new PerformanceWarning
            {
                Type = type,
                Message = message,
                Value = value,
                Threshold = threshold,
                FrameIndex = frameIdx,
                Timestamp = DateTime.Now,
            };
            _warnings.Enqueue(warning);
            while (_warnings.Count > _maxWarnings)
                _warnings.Dequeue();
            WarningRaised?.Invoke(warning);
        }

        public PerformanceWarning[] GetWarnings() => _warnings.ToArray();
        public int WarningCount => _warnings.Count;
        public void ClearWarnings() => _warnings.Clear();
    }

    // ============================================================================================
    //  性能分析扩展方法
    // ============================================================================================

    /// <summary>性能分析扩展。</summary>
    public static class PhysicsProfilerExtensions
    {
        /// <summary>用scope计时一段代码。</summary>
        public static double Time(this PhysicsProfiler profiler, string name, Action action)
        {
            var sw = Stopwatch.StartNew();
            action();
            sw.Stop();
            double ms = sw.Elapsed.TotalMilliseconds;
            profiler.RecordSection(name, ProfileSectionType.Custom, ms);
            return ms;
        }

        /// <summary>用scope计时并返回结果。</summary>
        public static (T result, double ms) Time<T>(this PhysicsProfiler profiler, string name, Func<T> func)
        {
            var sw = Stopwatch.StartNew();
            T result = func();
            sw.Stop();
            double ms = sw.Elapsed.TotalMilliseconds;
            profiler.RecordSection(name, ProfileSectionType.Custom, ms);
            return (result, ms);
        }

        /// <summary>开始一个标准section。</summary>
        public static ProfileScope BroadphaseScope(this PhysicsProfiler p) =>
            p.BeginScope("Broadphase", ProfileSectionType.Broadphase);

        public static ProfileScope NarrowphaseScope(this PhysicsProfiler p) =>
            p.BeginScope("Narrowphase", ProfileSectionType.Narrowphase);

        public static ProfileScope SolverScope(this PhysicsProfiler p) =>
            p.BeginScope("Solver", ProfileSectionType.ContactSolver);

        public static ProfileScope IntegrationScope(this PhysicsProfiler p) =>
            p.BeginScope("Integration", ProfileSectionType.Integration);

        public static ProfileScope SleepScope(this PhysicsProfiler p) =>
            p.BeginScope("Sleep", ProfileSectionType.SleepManagement);

        public static ProfileScope IslandScope(this PhysicsProfiler p) =>
            p.BeginScope("IslandDecomposition", ProfileSectionType.IslandDecomposition);

        public static ProfileScope CCDScope(this PhysicsProfiler p) =>
            p.BeginScope("CCD", ProfileSectionType.CCD);
    }

    // ============================================================================================
    //  全局Profiler
    // ============================================================================================

    /// <summary>
    ///   全局Profiler实例。简单使用时直接用这个。
    /// </summary>
    public static class GlobalProfiler
    {
        private static PhysicsProfiler? _instance;
        private static readonly object _lock = new();

        public static PhysicsProfiler Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        _instance ??= new PhysicsProfiler();
                    }
                }
                return _instance;
            }
        }

        public static ProfileScope Scope(string name, ProfileSectionType type = ProfileSectionType.Custom)
        {
            return Instance.BeginScope(name, type);
        }

        public static void BeginFrame() => Instance.BeginFrame();
        public static void EndFrame() => Instance.EndFrame();
        public static void SetCounter(string name, long value) => Instance.SetCounter(name, value);
        public static void IncrementCounter(string name, long delta = 1) => Instance.IncrementCounter(name, delta);
        public static string Report() => Instance.GenerateReport();
    }

    // ============================================================================================
    //  热点分析
    // ============================================================================================

    /// <summary>热点分析。找出最耗时的section。</summary>
    public sealed class HotspotAnalyzer
    {
        private readonly PhysicsProfiler _profiler;
        private readonly List<HotspotInfo> _hotspots = new();

        public HotspotAnalyzer(PhysicsProfiler profiler)
        {
            _profiler = profiler;
        }

        /// <summary>分析热点。</summary>
        public List<HotspotInfo> Analyze(int topN = 10)
        {
            _hotspots.Clear();
            double totalTime = 0;
            foreach (var s in _profiler.GetSections().Values)
                totalTime += s.TotalTimeMs;

            if (totalTime <= 0) return _hotspots;

            foreach (var s in _profiler.GetSections().Values)
            {
                _hotspots.Add(new HotspotInfo
                {
                    Name = s.Name,
                    Type = s.Type,
                    TotalMs = s.TotalTimeMs,
                    AvgMs = s.AvgTimeMs,
                    MaxMs = s.MaxTimeMs,
                    CallCount = s.CallCount,
                    Percentage = s.TotalTimeMs / totalTime * 100.0,
                });
            }

            _hotspots.Sort((a, b) => b.TotalMs.CompareTo(a.TotalMs));
            if (_hotspots.Count > topN)
                _hotspots.RemoveRange(topN, _hotspots.Count - topN);
            return _hotspots;
        }

        public string GenerateReport()
        {
            var sb = new StringBuilder();
            sb.AppendLine("==== Hotspot Analysis ====");
            sb.AppendLine($"  {"Rank",-5} {"Name",-30} {"Total(ms)",-12} {"%",-8} {"Avg(ms)",-10} {"Calls",-10}");
            for (int i = 0; i < _hotspots.Count; i++)
            {
                var h = _hotspots[i];
                sb.AppendLine($"  {i + 1,-5} {h.Name,-30} {h.TotalMs,-12:F3} {h.Percentage,-8:F1} {h.AvgMs,-10:F3} {h.CallCount,-10}");
            }
            return sb.ToString();
        }
    }

    /// <summary>热点信息。</summary>
    public struct HotspotInfo
    {
        public string Name;
        public ProfileSectionType Type;
        public double TotalMs;
        public double AvgMs;
        public double MaxMs;
        public long CallCount;
        public double Percentage;
    }

    // ============================================================================================
    //  性能目标
    // ============================================================================================

    /// <summary>性能目标。</summary>
    public struct PerformanceTarget
    {
        public double TargetFrameTime = 16.67;     // 60FPS
        public double MaxFrameTime = 33.33;        // 最低30FPS
        public double TargetBroadphase = 2.0;
        public double TargetNarrowphase = 4.0;
        public double TargetSolver = 3.0;
        public double TargetIntegration = 1.0;
        public int TargetBodyCount = 5000;
        public int TargetContactCount = 10000;

        public PerformanceTarget() { }

        public static PerformanceTarget VR => new()
        {
            TargetFrameTime = 7.05,   // 90FPS
            MaxFrameTime = 11.11,
            TargetBroadphase = 1.0,
            TargetNarrowphase = 2.0,
            TargetSolver = 1.5,
            TargetIntegration = 0.5,
        };

        public static PerformanceTarget Mobile => new()
        {
            TargetFrameTime = 16.67,
            MaxFrameTime = 33.33,
            TargetBroadphase = 3.0,
            TargetNarrowphase = 5.0,
            TargetSolver = 4.0,
            TargetIntegration = 2.0,
            TargetBodyCount = 2000,
            TargetContactCount = 5000,
        };
    }

    /// <summary>性能目标检查器。</summary>
    public sealed class PerformanceTargetChecker
    {
        private readonly PhysicsProfiler _profiler;
        private PerformanceTarget _target;
        private double _complianceRate;
        private int _totalFrames;
        private int _compliantFrames;

        public PerformanceTarget Target
        {
            get => _target;
            set => _target = value;
        }
        public double ComplianceRate => _complianceRate;

        public PerformanceTargetChecker(PhysicsProfiler profiler, PerformanceTarget target)
        {
            _profiler = profiler;
            _target = target;
            _profiler.FrameCompleted += OnFrameCompleted;
        }

        private void OnFrameCompleted(FrameStats frame)
        {
            _totalFrames++;
            bool compliant = frame.TotalTimeMs <= _target.TargetFrameTime &&
                            frame.BroadphaseMs <= _target.TargetBroadphase &&
                            frame.NarrowphaseMs <= _target.TargetNarrowphase &&
                            frame.SolverMs <= _target.TargetSolver &&
                            frame.IntegrationMs <= _target.TargetIntegration;
            if (compliant) _compliantFrames++;
            _complianceRate = (double)_compliantFrames / _totalFrames * 100.0;
        }

        public string GenerateReport()
        {
            var sb = new StringBuilder();
            sb.AppendLine("==== Performance Target Check ====");
            sb.AppendLine($"Compliance Rate: {_complianceRate:F1}% ({_compliantFrames}/{_totalFrames})");
            var recent = _profiler.GetRecentFrames(60);
            if (recent.Length > 0)
            {
                double avgTotal = 0, avgBroad = 0, avgNarrow = 0, avgSolver = 0, avgInteg = 0;
                foreach (var f in recent)
                {
                    avgTotal += f.TotalTimeMs;
                    avgBroad += f.BroadphaseMs;
                    avgNarrow += f.NarrowphaseMs;
                    avgSolver += f.SolverMs;
                    avgInteg += f.IntegrationMs;
                }
                int n = recent.Length;
                sb.AppendLine($"  Total:        {avgTotal / n:F2}ms / {_target.TargetFrameTime:F2}ms {(avgTotal / n <= _target.TargetFrameTime ? "OK" : "FAIL")}");
                sb.AppendLine($"  Broadphase:   {avgBroad / n:F2}ms / {_target.TargetBroadphase:F2}ms {(avgBroad / n <= _target.TargetBroadphase ? "OK" : "FAIL")}");
                sb.AppendLine($"  Narrowphase:  {avgNarrow / n:F2}ms / {_target.TargetNarrowphase:F2}ms {(avgNarrow / n <= _target.TargetNarrowphase ? "OK" : "FAIL")}");
                sb.AppendLine($"  Solver:       {avgSolver / n:F2}ms / {_target.TargetSolver:F2}ms {(avgSolver / n <= _target.TargetSolver ? "OK" : "FAIL")}");
                sb.AppendLine($"  Integration:  {avgInteg / n:F2}ms / {_target.TargetIntegration:F2}ms {(avgInteg / n <= _target.TargetIntegration ? "OK" : "FAIL")}");
            }
            return sb.ToString();
        }
    }
}
