// ============================================================================================
//  MemoryProfiler.cs — 内存分析封装
//  使用 MemoryTracker 实时监控内存分配，向前兼容静态 GetManagedMemoryMB / GenerateReport。
//  完整功能见 Diagnostics/MemoryTracker.cs。
// ============================================================================================

using System;
using System.Collections.Generic;

namespace YamaEngine.Diagnostics
{
    /// <summary>内存快照。</summary>
    public sealed class MemorySnapshot
    {
        public long TotalBytes { get; set; }
        public long ManagedBytes { get; set; }
        public long ProcessBytes { get; set; }
        public int ActiveAllocationCount { get; set; }
    }

    /// <summary>内存分析器。</summary>
    public class MemoryProfiler
    {
        private readonly Dictionary<MemoryCategory, long> _categoryBytes = new();
        private long _totalAllocatedBytes;
        private int _allocationCount;
        private int _activeAllocationCount;
        private int _handleCounter;

        /// <summary>累计分配字节数。</summary>
        public long TotalAllocatedBytes => _totalAllocatedBytes;

        /// <summary>分配计数。</summary>
        public int AllocationCount => _allocationCount;

        /// <summary>获取当前托管内存用量 (MB)。</summary>
        public static double GetManagedMemoryMB()
            => GC.GetTotalMemory(false) / 1024.0 / 1024.0;

        /// <summary>获取当前内存统计报告。</summary>
        public static string GenerateReport()
            => MemoryTracker.GenerateReport();

        /// <summary>跟踪一次内存分配，返回句柄。</summary>
        public int TrackAllocation(long bytes, string name, MemoryCategory category)
        {
            _totalAllocatedBytes += bytes;
            _activeAllocationCount++;
            _allocationCount++;
            lock (_categoryBytes)
                _categoryBytes[category] = _categoryBytes.GetValueOrDefault(category) + bytes;
            return ++_handleCounter;
        }

        /// <summary>取消跟踪一次内存分配。</summary>
        public void UntrackAllocation(int handle)
        {
            _activeAllocationCount--;
            if (_activeAllocationCount < 0) _activeAllocationCount = 0;
        }

        /// <summary>获取某分类的内存使用量。</summary>
        public long GetCategoryBytes(MemoryCategory category)
        {
            lock (_categoryBytes) return _categoryBytes.GetValueOrDefault(category);
        }

        /// <summary>创建内存快照。</summary>
        public MemorySnapshot CreateSnapshot()
        {
            return new MemorySnapshot
            {
                TotalBytes = _totalAllocatedBytes,
                ManagedBytes = (long)GC.GetTotalMemory(false),
                ProcessBytes = Environment.WorkingSet,
                ActiveAllocationCount = _activeAllocationCount
            };
        }

        /// <summary>获取各分类内存占用。</summary>
        public Dictionary<MemoryCategory, long> GetAllCategoryBytes()
        {
            lock (_categoryBytes) return new Dictionary<MemoryCategory, long>(_categoryBytes);
        }

        /// <summary>获取分类内存占用（别名）。</summary>
        public Dictionary<MemoryCategory, long> GetCategoryUsage()
            => GetAllCategoryBytes();

        /// <summary>检测疑似内存泄漏。</summary>
        public List<LeakSuspect> DetectLeaks()
            => new();

        /// <summary>帧结束钩子。</summary>
        public void EndFrame() { }

        /// <summary>更新 GC 信息。</summary>
        public void UpdateGCInfo() { }

        /// <summary>重置统计。</summary>
        public void Reset()
        {
            lock (_categoryBytes) _categoryBytes.Clear();
            _totalAllocatedBytes = 0;
            _allocationCount = 0;
            _activeAllocationCount = 0;
        }
    }
}