// =============================================================================
// RHIResourceTracker.cs - 山雨引擎 RHI 资源跟踪器
// =============================================================================
// 跟踪所有 RHI 资源的生命周期，做几件事：
//   1. 检测泄漏 (退出时 dump 所有未释放的资源)
//   2. 统计每种资源的数量和总内存
//   3. 资源命名 (debug 用)
//   4. 引用计数 (手动管理，避免重复销毁)
//   5. deferred destruction (GPU 完成后再销毁)
//
// FIXME: 引用计数还没接入到所有创建路径
// TODO: 加 GPU 内存占用估算
//
// 作者: 山雨引擎团队
// =============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace ShanyuEngine.RHI
{
    // ════════════════════════════════════════════════════════════════════════════
    // 区域 1: 公共类型
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>资源类型分类。</summary>
    public enum RHIResourceCategory
    {
        Buffer,
        Texture,
        Sampler,
        Shader,
        Pipeline,
        PipelineLayout,
        RenderPass,
        Framebuffer,
        DescriptorSet,
        DescriptorSetLayout,
        DescriptorPool,
        Fence,
        Semaphore,
        CommandList,
        CommandPool,
        QueryPool,
        Swapchain,
        Other,
    }

    /// <summary>资源状态。</summary>

    /// <summary>资源跟踪条目。</summary>
    public class RHIResourceEntry
    {
        public ulong Id;
        public RHIResource Resource;
        public RHIResourceCategory Category;
        public RHIResourceState State;
        public string Name;
        public ulong EstimatedSize;
        public DateTime CreatedAt;
        public long CreatedFrame;
        public long LastUsedFrame;
        public int RefCount;
        public string StackTrace;  // 创建时的调用栈 (调试用)
        public ulong DeferredDestroyFrame;  // 多少帧后销毁
    }

    /// <summary>资源跟踪统计。</summary>
    public struct ResourceTrackerStats
    {
        public int TotalResourcesCreated;
        public int TotalResourcesDestroyed;
        public int ActiveResources;
        public int PendingDestroy;
        public Dictionary<RHIResourceCategory, int> CountByCategory;
        public ulong EstimatedTotalMemory;
        public int LeakCount;

        public override string ToString()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Resources: active={ActiveResources}, pending={PendingDestroy}, " +
                          $"leaks={LeakCount}, total_created={TotalResourcesCreated}, " +
                          $"total_destroyed={TotalResourcesDestroyed}");
            sb.AppendLine($"Estimated memory: {EstimatedTotalMemory / 1024.0 / 1024.0:F2} MB");
            if (CountByCategory != null)
            {
                foreach (var kv in CountByCategory)
                {
                    if (kv.Value > 0)
                        sb.AppendLine($"  {kv.Key}: {kv.Value}");
                }
            }
            return sb.ToString();
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 2: 资源跟踪器主类
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 资源跟踪器主类。全局唯一，跟踪所有 RHI 资源。
    /// </summary>
    public sealed class RHIResourceTracker : IDisposable
    {
        private readonly Dictionary<ulong, RHIResourceEntry> _entries = new();
        private readonly object _lock = new();
        private ulong _nextId = 1;
        private long _currentFrame;
        private readonly bool _captureStackTraces;
        private readonly ResourceTrackerStats _stats;
        private bool _disposed;

        // deferred destruction 队列
        private readonly List<RHIResourceEntry> _pendingDestroy = new();
        private const ulong DefaultDeferredFrames = 3;  // 3 帧后销毁

        public ref ResourceTrackerStats Stats => ref _stats;

        public RHIResourceTracker(bool captureStackTraces = false)
        {
            _captureStackTraces = captureStackTraces;
            _stats = new ResourceTrackerStats
            {
                CountByCategory = new Dictionary<RHIResourceCategory, int>(),
            };
        }

        /// <summary>注册资源。</summary>
        public ulong Register(RHIResource resource, RHIResourceCategory category,
                              ulong estimatedSize = 0, string name = null)
        {
            if (resource == null) return 0;

            var entry = new RHIResourceEntry
            {
                Category = category,
                State = RHIResourceState.Active,
                Name = name ?? $"{category}_{_nextId}",
                EstimatedSize = estimatedSize,
                CreatedAt = DateTime.UtcNow,
                CreatedFrame = _currentFrame,
                LastUsedFrame = _currentFrame,
                RefCount = 1,
            };

            if (_captureStackTraces)
            {
                entry.StackTrace = new StackTrace(2, true).ToString();
            }

            lock (_lock)
            {
                entry.Id = _nextId++;
                entry.Resource = resource;
                _entries[entry.Id] = entry;
                _stats.TotalResourcesCreated++;
                _stats.ActiveResources++;
                _stats.EstimatedTotalMemory += estimatedSize;

                if (!_stats.CountByCategory.ContainsKey(category))
                    _stats.CountByCategory[category] = 0;
                _stats.CountByCategory[category]++;
            }

            return entry.Id;
        }

        /// <summary>注销资源。</summary>
        public void Unregister(ulong id)
        {
            if (id == 0) return;
            lock (_lock)
            {
                if (!_entries.TryGetValue(id, out var entry)) return;
                _entries.Remove(id);
                _stats.ActiveResources--;
                _stats.TotalResourcesDestroyed++;
                _stats.EstimatedTotalMemory -= entry.EstimatedSize;
                if (_stats.CountByCategory.TryGetValue(entry.Category, out var c))
                {
                    _stats.CountByCategory[entry.Category] = Math.Max(0, c - 1);
                }
            }
        }

        /// <summary>延迟销毁资源 (等 GPU 完成)。</summary>
        public void DestroyDeferred(RHIResource resource, ulong framesToWait = DefaultDeferredFrames)
        {
            if (resource == null) return;
            lock (_lock)
            {
                var entry = FindEntry(resource);
                if (entry == null) return;
                entry.State = RHIResourceState.PendingDestroy;
                entry.DeferredDestroyFrame = _currentFrame + (long)framesToWait;
                _pendingDestroy.Add(entry);
                _stats.PendingDestroy = _pendingDestroy.Count;
            }
        }

        /// <summary>增加引用计数。</summary>
        public void AddRef(ulong id)
        {
            lock (_lock)
            {
                if (_entries.TryGetValue(id, out var entry))
                    entry.RefCount++;
            }
        }

        /// <summary>减少引用计数。返回 true 表示应该销毁。</summary>
        public bool Release(ulong id)
        {
            lock (_lock)
            {
                if (!_entries.TryGetValue(id, out var entry)) return false;
                entry.RefCount--;
                return entry.RefCount <= 0;
            }
        }

        /// <summary>更新资源最后使用帧。</summary>
        public void Touch(ulong id)
        {
            lock (_lock)
            {
                if (_entries.TryGetValue(id, out var entry))
                    entry.LastUsedFrame = _currentFrame;
            }
        }

        /// <summary>设置资源名字。</summary>
        public void SetName(ulong id, string name)
        {
            lock (_lock)
            {
                if (_entries.TryGetValue(id, out var entry))
                    entry.Name = name;
            }
        }

        /// <summary>每帧调用，处理 deferred destruction。</summary>
        public void TickFrame(long frameIndex, Action<RHIResource> destroyCallback)
        {
            _currentFrame = frameIndex;

            List<RHIResourceEntry> toDestroy = null;
            lock (_lock)
            {
                for (int i = _pendingDestroy.Count - 1; i >= 0; i--)
                {
                    var e = _pendingDestroy[i];
                    if (_currentFrame >= e.DeferredDestroyFrame)
                    {
                        toDestroy ??= new List<RHIResourceEntry>();
                        toDestroy.Add(e);
                        _pendingDestroy.RemoveAt(i);
                    }
                }
                _stats.PendingDestroy = _pendingDestroy.Count;
            }

            if (toDestroy != null)
            {
                foreach (var e in toDestroy)
                {
                    destroyCallback?.Invoke(e.Resource);
                    e.State = RHIResourceState.Destroyed;
                    Unregister(e.Id);
                }
            }
        }

        /// <summary>查找资源条目。</summary>
        public RHIResourceEntry FindEntry(RHIResource resource)
        {
            lock (_lock)
            {
                foreach (var kv in _entries)
                {
                    if (ReferenceEquals(kv.Value.Resource, resource))
                        return kv.Value;
                }
                return null;
            }
        }

        /// <summary>查找资源条目 (按 id)。</summary>
        public RHIResourceEntry GetEntry(ulong id)
        {
            lock (_lock)
            {
                return _entries.TryGetValue(id, out var e) ? e : null;
            }
        }

        /// <summary>dump 所有未释放的资源。</summary>
        public void DumpLeaks(System.IO.TextWriter writer)
        {
            lock (_lock)
            {
                _stats.LeakCount = _entries.Count;
                writer.WriteLine($"=== Resource Leaks: {_entries.Count} ===");
                foreach (var kv in _entries)
                {
                    var e = kv.Value;
                    writer.WriteLine($"  [{e.Id}] {e.Category} '{e.Name}' " +
                                     $"size={e.EstimatedSize} created_frame={e.CreatedFrame} " +
                                     $"ref={e.RefCount}");
                    if (!string.IsNullOrEmpty(e.StackTrace))
                        writer.WriteLine($"    stack:\n{e.StackTrace}");
                }
            }
        }

        /// <summary>获取统计。</summary>
        public ResourceTrackerStats GetStats() => _stats;

        /// <summary>获取所有条目 (snapshot)。</summary>
        public List<RHIResourceEntry> GetAllEntries()
        {
            lock (_lock) return new List<RHIResourceEntry>(_entries.Values);
        }

        /// <summary>清空所有跟踪 (慎用)。</summary>
        public void Clear()
        {
            lock (_lock)
            {
                _entries.Clear();
                _pendingDestroy.Clear();
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            if (_entries.Count > 0)
            {
                Console.Error.WriteLine($"[ResourceTracker] Dispose 时还有 {_entries.Count} 个未释放资源");
            }
            _disposed = true;
            Clear();
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 3: 资源大小估算
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>资源大小估算器。</summary>
    public static class RHIResourceSizeEstimator
    {
        /// <summary>估算 buffer 大小。</summary>
        public static ulong EstimateBufferSize(RHIBufferDesc desc) => desc.Size;

        /// <summary>估算 texture 大小。</summary>
        public static ulong EstimateTextureSize(RHITextureDesc desc)
        {
            int bpp = RHIFormatUtils.BytesPerPixel(desc.Format);
            ulong total = 0;
            uint w = desc.Width, h = desc.Height, d = desc.Depth;
            for (uint m = 0; m < desc.MipLevels; m++)
            {
                total += (ulong)bpp * w * h * d * desc.ArraySize;
                if (w > 1) w /= 2;
                if (h > 1) h /= 2;
                if (d > 1 && desc.Type == RHITextureType.Texture3D) d /= 2;
            }
            return total;
        }

        /// <summary>估算 sampler 大小 (粗略)。</summary>
        public static ulong EstimateSamplerSize() => 64;

        /// <summary>估算 shader 大小。</summary>
        public static ulong EstimateShaderSize(RHIShaderDesc desc)
            => desc.Bytecode != null ? (ulong)desc.Bytecode.Length : 1024;

        /// <summary>估算 pipeline 大小 (粗略)。</summary>
        public static ulong EstimatePipelineSize() => 4096;  // pipeline 通常很贵
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 4: 资源命名辅助
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>资源命名 helper。</summary>
    public static class RHIResourceNaming
    {
        /// <summary>给资源生成默认名字。</summary>
        public static string GenerateDefaultName(RHIResourceCategory category, string suffix = null)
        {
            return suffix != null ? $"{category}_{suffix}" : category.ToString();
        }

        /// <summary>给一组资源批量命名。</summary>
        public static void NameBatch(RHIResourceTracker tracker, RHIResource[] resources,
                                     string prefix, RHIResourceCategory category)
        {
            for (int i = 0; i < resources.Length; i++)
            {
                var entry = tracker.FindEntry(resources[i]);
                if (entry != null)
                    tracker.SetName(entry.Id, $"{prefix}_{i}");
            }
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 5: 资源监控
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>资源监控器。后台定时 dump 资源状态。</summary>
    public sealed class RHIResourceMonitor : IDisposable
    {
        private readonly RHIResourceTracker _tracker;
        private System.Threading.Timer _timer;
        private readonly object _lock = new();
        private readonly List<ResourceTrackerStats> _history = new();
        private const int MaxHistory = 60;
        private bool _disposed;

        public event Action<ResourceTrackerStats> OnStatsUpdated;

        public RHIResourceMonitor(RHIResourceTracker tracker, TimeSpan? interval = null)
        {
            _tracker = tracker;
            _timer = new System.Threading.Timer(_ => Tick(), null,
                interval ?? TimeSpan.FromSeconds(1),
                interval ?? TimeSpan.FromSeconds(1));
        }

        private void Tick()
        {
            if (_disposed) return;
            var s = _tracker.GetStats();
            lock (_lock)
            {
                _history.Add(s);
                if (_history.Count > MaxHistory) _history.RemoveAt(0);
            }
            OnStatsUpdated?.Invoke(s);
        }

        public ResourceTrackerStats[] GetHistory()
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
    // 区域 6: 资源池
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 资源池。相同描述的资源复用，避免频繁创建销毁。
    /// </summary>
    public sealed class RHIResourcePool<TKey, TResource> : IDisposable where TResource : class
    {
        private readonly Dictionary<TKey, Stack<TResource>> _free = new();
        private readonly Dictionary<TKey, int> _created = new();
        private readonly Func<TKey, TResource> _factory;
        private readonly Action<TResource> _destroy;
        private readonly int _maxPerKey;
        private readonly object _lock = new();

        public RHIResourcePool(Func<TKey, TResource> factory, Action<TResource> destroy,
                               int maxPerKey = 16)
        {
            _factory = factory;
            _destroy = destroy;
            _maxPerKey = maxPerKey;
        }

        public TResource Acquire(TKey key)
        {
            lock (_lock)
            {
                if (_free.TryGetValue(key, out var stack) && stack.Count > 0)
                    return stack.Pop();
            }
            var r = _factory(key);
            lock (_lock)
            {
                if (!_created.ContainsKey(key)) _created[key] = 0;
                _created[key]++;
            }
            return r;
        }

        public void Release(TKey key, TResource resource)
        {
            lock (_lock)
            {
                if (!_free.TryGetValue(key, out var stack))
                {
                    stack = new Stack<TResource>();
                    _free[key] = stack;
                }
                if (stack.Count < _maxPerKey)
                    stack.Push(resource);
                else
                    _destroy?.Invoke(resource);
            }
        }

        public int FreeCount(TKey key)
        {
            lock (_lock)
            {
                return _free.TryGetValue(key, out var s) ? s.Count : 0;
            }
        }

        public int CreatedCount(TKey key)
        {
            lock (_lock) return _created.TryGetValue(key, out var c) ? c : 0;
        }

        public void Clear()
        {
            lock (_lock)
            {
                foreach (var stack in _free.Values)
                    foreach (var r in stack) _destroy?.Invoke(r);
                _free.Clear();
                _created.Clear();
            }
        }

        public void Dispose() => Clear();
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 7: 版本信息
    // ════════════════════════════════════════════════════════════════════════════

    public static class RHIResourceTrackerVersion
    {
        public const string Version = "1.0.0";
        public const string Description = "ShanyuEngine RHI Resource Tracker (leak detection + deferred destruction)";
    }
}

// 文件结束 - RHIResourceTracker.cs
