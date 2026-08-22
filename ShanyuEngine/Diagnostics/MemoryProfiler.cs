// ============================================================================================
//  MemoryProfiler.cs — 内存分析封装
//  使用 MemoryTracker 实时监控内存分配。
//  完整功能见 Diagnostics/MemoryTracker.cs。
// ============================================================================================

using System;

namespace ShanyuEngine.Diagnostics
{
    public class MemoryProfiler
    {
        /// <summary>获取当前托管内存用量 (MB)。</summary>
        public static double GetManagedMemoryMB()
            => GC.GetTotalMemory(false) / 1024.0 / 1024.0;

        /// <summary>获取当前内存统计报告。</summary>
        public static string GenerateReport()
            => MemoryTracker.GenerateReport();
    }
}
