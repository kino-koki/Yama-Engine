// ============================================================================================
//  CPUProfiler.cs — CPU Profiling 封装
//  本文件是 ProfilingSystem.cs 的前端封装。所有 CPU Profiling 功能移至
//  ProfilingSystem.cs 和 Diagnostics/MemoryTracker.cs。
// ============================================================================================

using System.Diagnostics;

namespace ShanyuEngine.Diagnostics
{
    public class CPUProfiler
    {
        private static readonly Stopwatch _stopwatch = new();

        /// <summary>开始 CPU 采样。</summary>
        public static void BeginSample(string name) { _stopwatch.Restart(); }

        /// <summary>结束 CPU 采样并返回耗时(ms)。</summary>
        public static double EndSample(string name)
        {
            _stopwatch.Stop();
            return _stopwatch.Elapsed.TotalMilliseconds;
        }
    }
}
