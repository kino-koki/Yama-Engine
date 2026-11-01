// ============================================================================================
//  MemoryTracker.cs
//  yama引擎 (YamaEngine) 内存追踪与分析系统
// ============================================================================================
//  监控引擎各模块的内存分配情况，提供泄漏检测和内存使用报告。
//  可以作为实时 Profiler Overlay 的数据源。
// ============================================================================================

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace YamaEngine.Diagnostics
{
    // ====== 内存分配记录 ======
    public readonly struct MemoryAllocRecord
    {
        public readonly string Tag;
        public readonly long Size;
        public readonly int Count;
        public readonly string? CallStack;

        public MemoryAllocRecord(string tag, long size, int count, string? callStack)
        {
            Tag = tag;
            Size = size;
            Count = count;
            CallStack = callStack;
        }
    }

    // ====== 内存统计 ======
    public struct MemoryStats
    {
        public long TotalAllocatedBytes;
        public long TotalFreedBytes;
        public long CurrentUsageBytes;
        public int AllocationCount;
        public int FreeCount;
        public int ActiveAllocations;

        public long PeakUsageBytes;
        public float AverageAllocationSize => AllocationCount > 0 ? (float)TotalAllocatedBytes / AllocationCount : 0;
    }

    // ====== 内存追踪器 ======
    /// <summary>
    /// 内存分配追踪器。可监控每个模块的内存使用情况。
    /// 在 Debug 构建中启用，Release 构建中条件编译移除。
    /// </summary>
    public static class MemoryTracker
    {
        private static readonly ConcurrentDictionary<string, MemoryStats> _moduleStats = new();
        private static long _totalAllocated;
        private static long _totalFreed;
        private static long _peakUsage;
        private static long _currentUsage;
        private static int _totalAllocCount;
        private static int _totalFreeCount;

        /// <summary>是否启用分配追踪。Debug 构建默认启用。</summary>
        public static bool IsEnabled { get; set; } =
#if DEBUG
            true;
#else
            false;
#endif

        /// <summary>是否记录调用栈（开销大，仅按需启用）。</summary>
        public static bool CaptureCallStack { get; set; } = false;

        /// <summary>全局内存统计。</summary>
        public static MemoryStats GlobalStats => new()
        {
            TotalAllocatedBytes = _totalAllocated,
            TotalFreedBytes = _totalFreed,
            CurrentUsageBytes = _currentUsage,
            AllocationCount = _totalAllocCount,
            FreeCount = _totalFreeCount,
            ActiveAllocations = _totalAllocCount - _totalFreeCount,
            PeakUsageBytes = Interlocked.Read(ref _peakUsage)
        };

        // ====== 模块级统计 ======
        public static MemoryStats GetModuleStats(string module)
        {
            return _moduleStats.TryGetValue(module, out var stats) ? stats : default;
        }

        public static IEnumerable<KeyValuePair<string, MemoryStats>> GetAllModuleStats() => _moduleStats;

        // ====== 记录分配 ======
        [Conditional("DEBUG")]
        public static void OnAllocate(string module, long size)
        {
            if (!IsEnabled) return;

            Interlocked.Add(ref _totalAllocated, size);
            Interlocked.Add(ref _currentUsage, size);
            Interlocked.Increment(ref _totalAllocCount);

            long current = Interlocked.Read(ref _currentUsage);
            long peak;
            do
            {
                peak = Interlocked.Read(ref _peakUsage);
                if (current <= peak) break;
            }
            while (Interlocked.CompareExchange(ref _peakUsage, current, peak) != peak);

            _moduleStats.AddOrUpdate(module,
                _ => new MemoryStats
                {
                    TotalAllocatedBytes = size,
                    CurrentUsageBytes = size,
                    AllocationCount = 1,
                    PeakUsageBytes = size
                },
                (_, old) =>
                {
                    old.TotalAllocatedBytes += size;
                    old.CurrentUsageBytes += size;
                    old.AllocationCount++;
                    if (old.CurrentUsageBytes > old.PeakUsageBytes)
                        old.PeakUsageBytes = old.CurrentUsageBytes;
                    return old;
                });
        }

        // ====== 记录释放 ======
        [Conditional("DEBUG")]
        public static void OnFree(string module, long size)
        {
            if (!IsEnabled) return;

            Interlocked.Add(ref _totalFreed, size);
            Interlocked.Add(ref _currentUsage, -size);
            Interlocked.Increment(ref _totalFreeCount);

            _moduleStats.AddOrUpdate(module,
                _ => new MemoryStats { TotalFreedBytes = size, FreeCount = 1 },
                (_, old) =>
                {
                    old.TotalFreedBytes += size;
                    old.CurrentUsageBytes -= size;
                    old.FreeCount++;
                    return old;
                });
        }

        // ====== 重置 ======
        public static void Reset()
        {
            _moduleStats.Clear();
            Interlocked.Exchange(ref _totalAllocated, 0);
            Interlocked.Exchange(ref _totalFreed, 0);
            Interlocked.Exchange(ref _peakUsage, 0);
            Interlocked.Exchange(ref _currentUsage, 0);
            Interlocked.Exchange(ref _totalAllocCount, 0);
            Interlocked.Exchange(ref _totalFreeCount, 0);
        }

        // ====== 生成报告 ======
        public static string GenerateReport()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("===== yama引擎内存报告 =====");
            sb.AppendLine($"当前使用: {_currentUsage / 1024.0 / 1024.0:F2} MB");
            sb.AppendLine($"峰值使用: {Interlocked.Read(ref _peakUsage) / 1024.0 / 1024.0:F2} MB");
            sb.AppendLine($"总分配: {_totalAllocated / 1024.0 / 1024.0:F2} MB");
            sb.AppendLine($"总释放: {_totalFreed / 1024.0 / 1024.0:F2} MB");
            sb.AppendLine($"分配次数: {_totalAllocCount}, 释放次数: {_totalFreeCount}");
            sb.AppendLine();

            if (_moduleStats.Count > 0)
            {
                sb.AppendLine("按模块:");
                foreach (var kvp in _moduleStats)
                {
                    var s = kvp.Value;
                    sb.AppendLine($"  {kvp.Key}: {s.CurrentUsageBytes / 1024.0:F1} KB ({s.AllocationCount} allocs, {s.FreeCount} frees)");
                }
            }

            return sb.ToString();
        }
    }

    // ====== 作用域性能分析器 ======
    /// <summary>
    /// 使用 using 语句的性能分析范围。自动记录耗时到 ProfilingSystem。
    /// 用法：
    /// <code>
    /// using var scope = ProfilingScope.Begin("RenderFrame");
    /// // ... 要测量的代码 ...
    /// </code>
    /// </summary>
    public readonly struct ProfilingScope : IDisposable
    {
        private readonly string _name;
        private readonly long _startTicks;

        private ProfilingScope(string name)
        {
            _name = name;
            _startTicks = Stopwatch.GetTimestamp();
        }

        public static ProfilingScope Begin(string name)
        {
            return new ProfilingScope(name);
        }

        public void Dispose()
        {
            long elapsedTicks = Stopwatch.GetTimestamp() - _startTicks;
            double ms = (double)elapsedTicks / Stopwatch.Frequency * 1000.0;

            // 写入 ProfilingSystem
            Diagnostics.Log.Debug(
                $"[PROFILE] {_name}: {ms:F2} ms",
                LogCategory.Core);

            // 短期注入 ProfilingSystem
            // 未来可以连接到 ProfilingSystem 的实时数据流
        }
    }

    // ====== 引擎诊断信息 ======
    /// <summary>收集引擎运行时诊断数据。</summary>
    public static class EngineHealthReport
    {
        /// <summary>生成完整的引擎健康报告。</summary>
        public static string GenerateReport()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("===== yama引擎健康报告 =====");
            sb.AppendLine($"时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"进程: {Process.GetCurrentProcess().ProcessName}");
            sb.AppendLine($"PID: {Environment.ProcessId}");
            sb.AppendLine($"64位: {Environment.Is64BitProcess}");
            sb.AppendLine($"托管内存: {GC.GetTotalMemory(false) / 1024.0 / 1024.0:F2} MB");
            sb.AppendLine($"GC 代数 0: {GC.CollectionCount(0)}, 1: {GC.CollectionCount(1)}, 2: {GC.CollectionCount(2)}");
            sb.AppendLine($"线程数: {Process.GetCurrentProcess().Threads.Count}");
            sb.AppendLine($"CPU 使用: {GetCpuUsage():F1}%");

            return sb.ToString();
        }

        private static double _lastCpuTime;
        private static DateTime _lastCpuCheck;

        private static double GetCpuUsage()
        {
            var process = Process.GetCurrentProcess();
            var now = DateTime.UtcNow;
            var cpuTime = process.TotalProcessorTime.TotalMilliseconds;

            if (_lastCpuCheck == default)
            {
                _lastCpuCheck = now;
                _lastCpuTime = cpuTime;
                return 0;
            }

            double elapsed = (now - _lastCpuCheck).TotalMilliseconds;
            double cpuUsed = cpuTime - _lastCpuTime;

            _lastCpuCheck = now;
            _lastCpuTime = cpuTime;

            return elapsed > 0 ? (cpuUsed / elapsed) * 100.0 / Environment.ProcessorCount : 0;
        }

        /// <summary>最大日志缓冲条目数。</summary>
        public static int MaxBufferedLogEntries { get; set; } = 500;
    }
}
