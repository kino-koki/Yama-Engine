// =============================================================================
// VulkanQueryPool.cs - 山雨引擎 Vulkan 查询池 (GPU 时间戳/Occlusion/PipelineStats)
// =============================================================================
// 这个模块管理 GPU 端的查询，三种用途：
//   1. Timestamp: 测 GPU 执行某个区段用了多少 ns
//   2. Occlusion: 测某个 draw call 画了多少像素
//   3. PipelineStatistics: 测 vertex count / draw count 等统计
//
// Vulkan 的查询 API 比较 weird，要点：
//   - vkCmdBeginQuery / vkCmdEndQuery 必须配对
//   - 结果不会立即可读，要 vkGetQueryPoolResults 等 GPU 完成
//   - 通常用"上一帧的结果"，因为本帧还没跑完
//   - TIMESTAMP 那种不需要 Begin/End，直接 vkCmdWriteTimestamp
//
// FIXME: 还没处理 query 不可用的情况 (VK_NOT_READY)
// TODO: 用 VK_KHR_performance_query 做更细粒度的性能计数
//
// 作者: 山雨引擎团队
// =============================================================================

using Vulkan = Silk.NET.Vulkan;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Silk.NET.Vulkan;
using Semaphore = Silk.NET.Vulkan.Semaphore;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace ShanyuEngine.Vulkan
{
    // ════════════════════════════════════════════════════════════════════════════
    // 区域 1: 公共类型
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>查询类型。</summary>
    public enum QueryTypeExt
    {
        Occlusion,
        PipelineStatistics,
        Timestamp,
        PerformanceQueryKhr,
        MeshShaderPerfHwInt,
    }

    /// <summary>管线统计位。</summary>
    [Flags]
    public enum PipelineStatisticFlagsExt : uint
    {
        None = 0,
        InputAssemblyVertices = 0x1,
        InputAssemblyPrimitives = 0x2,
        VertexShaderInvocations = 0x4,
        GeometryShaderInvocations = 0x8,
        GeometryShaderPrimitives = 0x10,
        ClippingInvocations = 0x20,
        ClippingPrimitives = 0x40,
        FragmentShaderInvocations = 0x80,
        TessellationControlShaderPatches = 0x100,
        TessellationEvaluationShaderInvocations = 0x200,
        ComputeShaderInvocations = 0x400,
    }

    /// <summary>查询结果标志。</summary>
    [Flags]
    public enum QueryResultFlagsExt : uint
    {
        Default = 0,
        /// <summary>64 位结果。</summary>
        SixtyFourBit = 0x1,
        /// <summary>等待结果可用。</summary>
        Wait = 0x2,
        /// <summary>带可用性位。</summary>
        WithAvailability = 0x4,
        /// <summary>部分结果 (不等)。</summary>
        Partial = 0x8,
    }

    /// <summary>单个 GPU 时间戳记录。</summary>
    public struct GpuTimestamp
    {
        /// <summary>查询索引。</summary>
        public uint QueryIndex;
        /// <summary>GPU 端时间戳 (单位 ns，需要 timestampPeriod 转换)。</summary>
        public ulong Ticks;
        /// <summary>实际时间 (ns)。</summary>
        public double Nanoseconds;
        /// <summary>标签。</summary>
        public string Label;
        /// <summary>所在的帧。</summary>
        public long FrameIndex;
    }

    /// <summary>两个时间戳之间的一段 GPU 时间。</summary>
    public struct GpuTimeRange
    {
        public GpuTimestamp Start;
        public GpuTimestamp End;
        public double DurationNs => End.Nanoseconds - Start.Nanoseconds;
        public double DurationUs => DurationNs / 1000.0;
        public double DurationMs => DurationUs / 1000.0;
        public string Label => Start.Label;

        public override string ToString()
            => $"{Label}: {DurationMs:F3}ms";
    }

    /// <summary>Occlusion 查询结果。</summary>
    public struct OcclusionResult
    {
        public uint QueryIndex;
        public ulong SampleCount;
        public bool Passed;
    }

    /// <summary>管线统计结果。</summary>
    public struct PipelineStatsResult
    {
        public uint QueryIndex;
        public ulong InputAssemblyVertices;
        public ulong InputAssemblyPrimitives;
        public ulong VertexShaderInvocations;
        public ulong GeometryShaderInvocations;
        public ulong GeometryShaderPrimitives;
        public ulong ClippingInvocations;
        public ulong ClippingPrimitives;
        public ulong FragmentShaderInvocations;
        public ulong TessellationControlShaderPatches;
        public ulong TessellationEvaluationShaderInvocations;
        public ulong ComputeShaderInvocations;

        public override string ToString()
            => $"verts={InputAssemblyVertices}, prims={InputAssemblyPrimitives}, " +
               $"vs_inv={VertexShaderInvocations}, fs_inv={FragmentShaderInvocations}, " +
               $"cs_inv={ComputeShaderInvocations}";
    }

    /// <summary>查询池统计。</summary>
    public struct QueryPoolStats
    {
        public int TimestampQueryCount;
        public int OcclusionQueryCount;
        public int PipelineStatsQueryCount;
        public int TotalAllocated;
        public int TotalFree;
        public int PendingResults;

        public override string ToString()
            => $"Queries: ts={TimestampQueryCount}, occ={OcclusionQueryCount}, " +
               $"stats={PipelineStatsQueryCount}, alloc={TotalAllocated}, free={TotalFree}, " +
               $"pending={PendingResults}";
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 2: 单个查询池包装
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 单个 VkQueryPool 包装。负责分配/释放 query index，回收复用。
    /// </summary>
    internal sealed unsafe class QueryPool : IDisposable
    {
        private readonly Vk _vk;
        private readonly Device _device;
        private readonly Silk.NET.Vulkan.QueryPool _handle;
        private readonly QueryTypeExt _type;
        private readonly uint _queryCount;
        private readonly Stack<uint> _freeIndices = new();
        private readonly HashSet<uint> _usedIndices = new();
        private readonly object _lock = new();

        public Silk.NET.Vulkan.QueryPool Handle => _handle;
        public QueryTypeExt Type => _type;
        public uint QueryCount => _queryCount;
        public int UsedCount { get; private set; }
        public int FreeCount => _freeIndices.Count;

        internal QueryPool(Vk vk, Device device, Silk.NET.Vulkan.QueryPool handle,
                          QueryTypeExt type, uint queryCount)
        {
            _vk = vk;
            _device = device;
            _handle = handle;
            _type = type;
            _queryCount = queryCount;
            for (uint i = 0; i < queryCount; i++) _freeIndices.Push(i);
        }

        /// <summary>分配一个 query index。</summary>
        public bool TryAllocate(out uint index)
        {
            lock (_lock)
            {
                if (_freeIndices.Count == 0) { index = 0; return false; }
                index = _freeIndices.Pop();
                _usedIndices.Add(index);
                UsedCount++;
                return true;
            }
        }

        /// <summary>批量分配。</summary>
        public bool TryAllocateMany(int count, uint[] outIndices)
        {
            lock (_lock)
            {
                if (_freeIndices.Count < count) return false;
                for (int i = 0; i < count; i++)
                {
                    outIndices[i] = _freeIndices.Pop();
                    _usedIndices.Add(outIndices[i]);
                }
                UsedCount += count;
                return true;
            }
        }

        /// <summary>释放 query index。</summary>
        public void Release(uint index)
        {
            lock (_lock)
            {
                if (_usedIndices.Remove(index))
                {
                    _freeIndices.Push(index);
                    UsedCount--;
                }
            }
        }

        /// <summary>获取结果 (uint64)。</summary>
        public Result GetResults(uint index, uint count, ulong[] outData,
                                 ulong stride, QueryResultFlagsExt flags)
        {
            fixed (ulong* pData = outData)
            {
                return _vk.GetQueryPoolResults(_device, _handle, index, count,
                    (nuint)(count * stride), pData, stride,
                    (QueryResultFlags)flags);
            }
        }

        /// <summary>重置查询 (Vulkan 里要 vkCmdResetQueryPool 才能用)。</summary>
        public void CmdReset(CommandBuffer cmd, uint firstQuery, uint queryCount)
        {
            _vk.CmdResetQueryPool(cmd, _handle, firstQuery, queryCount);
        }

        public void Dispose()
        {
            _vk.DestroyQueryPool(_device, _handle, null);
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 3: 时间戳查询管理
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 时间戳查询管理。专门负责 vkCmdWriteTimestamp。
    /// 关键思路：每帧分配 N 个 timestamp 查询槽，记录开始/结束，
    /// 下一帧获取上一帧的结果 (因为本帧还在跑)。
    /// </summary>
    public sealed unsafe class TimestampQueryManager : IDisposable
    {
        private readonly Vk _vk;
        private readonly Device _device;
        private readonly CommandBuffer _cmd;
        private readonly QueryPool _pool;
        private readonly float _timestampPeriod;  // ns/tick
        private readonly Dictionary<uint, GpuTimestamp> _pending = new();
        private readonly Dictionary<uint, GpuTimestamp> _completed = new();
        private readonly List<GpuTimeRange> _ranges = new();
        private readonly object _lock = new();
        private long _currentFrame;
        private uint _frameStartIndex;

        public int PendingCount
        {
            get { lock (_lock) return _pending.Count; }
        }

        public int CompletedCount
        {
            get { lock (_lock) return _completed.Count; }
        }

        public TimestampQueryManager(Vk vk, Device device, CommandBuffer cmd,
                                     uint maxQueriesPerFrame, float timestampPeriod)
        {
            _vk = vk;
            _device = device;
            _cmd = cmd;
            _timestampPeriod = timestampPeriod;

            // 创建 query pool
            var info = new QueryPoolCreateInfo
            {
                SType = StructureType.QueryPoolCreateInfo,
                QueryType = QueryType.Timestamp,
                QueryCount = maxQueriesPerFrame * 3,  // 3 帧重叠
            };

            Silk.NET.Vulkan.QueryPool handle;
            var r = vk.CreateQueryPool(device, info, null, &handle);
            if (r != Result.Success)
                throw new VulkanException(r, "CreateQueryPool timestamp 失败");
            _pool = new QueryPool(vk, device, handle, QueryTypeExt.Timestamp,
                                  maxQueriesPerFrame * 3);
        }

        /// <summary>写一个时间戳。返回 query index，等结果时用。</summary>
        public uint WriteTimestamp(PipelineStageFlags stage, string label)
        {
            if (!_pool.TryAllocate(out uint idx))
            {
                Console.Error.WriteLine("[QueryPool] timestamp 池满了");
                return uint.MaxValue;
            }

            _vk.CmdWriteTimestamp(_cmd, stage, _pool.Handle, idx);

            var ts = new GpuTimestamp
            {
                QueryIndex = idx,
                Label = label,
                FrameIndex = _currentFrame,
            };
            lock (_lock) _pending[idx] = ts;
            return idx;
        }

        /// <summary>开始一个时间范围。</summary>
        public GpuTimeRange BeginRange(string label, PipelineStageFlags stage = PipelineStageFlags.TopOfPipeBit)
        {
            var startIdx = WriteTimestamp(stage, label);
            return new GpuTimeRange
            {
                Start = new GpuTimestamp { QueryIndex = startIdx, Label = label, FrameIndex = _currentFrame },
            };
        }

        /// <summary>结束一个时间范围。</summary>
        public void EndRange(ref GpuTimeRange range, PipelineStageFlags stage = PipelineStageFlags.BottomOfPipeBit)
        {
            range.End = new GpuTimestamp
            {
                QueryIndex = WriteTimestamp(stage, range.Start.Label),
                Label = range.Start.Label,
                FrameIndex = _currentFrame,
            };
            lock (_lock) _ranges.Add(range);
        }

        /// <summary>获取上一帧的所有时间戳结果。</summary>
        public GpuTimestamp[] FetchPendingResults()
        {
            var results = new List<GpuTimestamp>();
            var toRemove = new List<uint>();

            lock (_lock)
            {
                foreach (var kv in _pending)
                {
                    uint idx = kv.Key;
                    var ts = kv.Value;
                    ulong[] data = new ulong[1];
                    var r = _pool.GetResults(idx, 1, data, sizeof(ulong),
                        QueryResultFlagsExt.SixtyFourBit | QueryResultFlagsExt.WithAvailability);
                    if (r == Result.Success && data[0] != 0)
                    {
                        ts.Ticks = data[0];
                        ts.Nanoseconds = data[0] * _timestampPeriod;
                        _completed[idx] = ts;
                        results.Add(ts);
                        toRemove.Add(idx);
                    }
                    else if (r == Result.NotReady)
                    {
                        // 还没好，下次再拿
                    }
                    else if (r == Result.DeviceLost)
                    {
                        Console.Error.WriteLine($"[QueryPool] DeviceLost 获取 timestamp {idx}");
                        toRemove.Add(idx);
                    }
                }
                foreach (var idx in toRemove) _pending.Remove(idx);
            }

            return results.ToArray();
        }

        /// <summary>计算所有完成的 range 的耗时。</summary>
        public GpuTimeRange[] ComputeRanges()
        {
            lock (_lock)
            {
                var completedRanges = new List<GpuTimeRange>();
                var keepRanges = new List<GpuTimeRange>();
                foreach (var range in _ranges)
                {
                    if (_completed.TryGetValue(range.Start.QueryIndex, out var startTs) &&
                        _completed.TryGetValue(range.End.QueryIndex, out var endTs))
                    {
                        var r = range;
                        r.Start = startTs;
                        r.End = endTs;
                        completedRanges.Add(r);
                    }
                    else
                    {
                        keepRanges.Add(range);
                    }
                }
                _ranges.Clear();
                _ranges.AddRange(keepRanges);
                return completedRanges.ToArray();
            }
        }

        /// <summary>每帧开始调用，重置本帧 query pool。</summary>
        public void OnFrameBegin(long frameIndex)
        {
            _currentFrame = frameIndex;
        }

        /// <summary>释放所有完成的 query index。</summary>
        public void ReleaseCompleted()
        {
            lock (_lock)
            {
                foreach (var idx in _completed.Keys) _pool.Release(idx);
                _completed.Clear();
            }
        }

        public void Dispose()
        {
            _pool.Dispose();
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 4: Occlusion 查询管理
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Occlusion 查询管理。begin/end 之间画了多少像素。
    /// 典型用法：bounding box 遮挡剔除。
    /// </summary>
    public sealed unsafe class OcclusionQueryManager : IDisposable
    {
        private readonly Vk _vk;
        private readonly Device _device;
        private readonly CommandBuffer _cmd;
        private readonly QueryPool _pool;
        private readonly Dictionary<uint, string> _labels = new();
        private readonly Dictionary<uint, OcclusionResult> _results = new();
        private readonly object _lock = new();
        private const uint DefaultQueryCount = 1024;

        public OcclusionQueryManager(Vk vk, Device device, CommandBuffer cmd,
                                     uint queryCount = DefaultQueryCount)
        {
            _vk = vk;
            _device = device;
            _cmd = cmd;
            var info = new QueryPoolCreateInfo
            {
                SType = StructureType.QueryPoolCreateInfo,
                QueryType = QueryType.Occlusion,
                QueryCount = queryCount,
            };
            Silk.NET.Vulkan.QueryPool handle;
            var r = vk.CreateQueryPool(device, info, null, &handle);
            if (r != Result.Success)
                throw new VulkanException(r, "CreateQueryPool occlusion 失败");
            _pool = new QueryPool(vk, device, handle, QueryTypeExt.Occlusion, queryCount);
        }

        /// <summary>开始 occlusion 查询。</summary>
        public uint BeginQuery(string label)
        {
            if (!_pool.TryAllocate(out uint idx))
                return uint.MaxValue;
            _vk.CmdBeginQuery(_cmd, _pool.Handle, idx, 0);
            lock (_lock) _labels[idx] = label;
            return idx;
        }

        /// <summary>结束 occlusion 查询。</summary>
        public void EndQuery(uint idx)
        {
            _vk.CmdEndQuery(_cmd, _pool.Handle, idx);
        }

        /// <summary>获取所有结果。</summary>
        public OcclusionResult[] FetchResults()
        {
            var results = new List<OcclusionResult>();
            var toRelease = new List<uint>();

            lock (_lock)
            {
                foreach (var kv in _labels)
                {
                    uint idx = kv.Key;
                    ulong[] data = new ulong[1];
                    var r = _pool.GetResults(idx, 1, data, sizeof(ulong),
                        QueryResultFlagsExt.SixtyFourBit | QueryResultFlagsExt.WithAvailability);
                    if (r == Result.Success)
                    {
                        results.Add(new OcclusionResult
                        {
                            QueryIndex = idx,
                            SampleCount = data[0],
                            Passed = data[0] > 0,
                        });
                        toRelease.Add(idx);
                    }
                }
                foreach (var idx in toRelease)
                {
                    _labels.Remove(idx);
                    _pool.Release(idx);
                }
            }

            return results.ToArray();
        }

        /// <summary>条件渲染 (基于已完成的 occlusion 查询)。</summary>
        public void CmdBeginConditionalRender(uint idx, bool invert = false)
        {
            var flags = ConditionalRenderingFlagsExt.None;
            if (invert) flags = ConditionalRenderingFlagsExt.InvertBit;
            var info = new ConditionalRenderingBeginInfoEXT
            {
                SType = StructureType.ConditionalRenderingBeginInfoExt,
                Buffer = default,  // 需要先把结果 copy 到 buffer
                Offset = idx * sizeof(ulong),
                Flags = flags,
            };
            // vkCmdBeginConditionalRenderingEXT(cmd, &info);
            // 需要扩展函数指针
        }

        public void Dispose() => _pool.Dispose();
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 5: 管线统计查询管理
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 管线统计查询管理。统计 GPU 执行了多少 vertex shader / fragment shader 等。
    /// 用于发现 overdraw / geometry shader 滥用。
    /// </summary>
    public sealed unsafe class PipelineStatsQueryManager : IDisposable
    {
        private readonly Vk _vk;
        private readonly Device _device;
        private readonly CommandBuffer _cmd;
        private readonly QueryPool _pool;
        private readonly PipelineStatisticFlagsExt _statFlags;
        private readonly int _resultCount;  // 每次查询返回几个 ulong
        private readonly Dictionary<uint, string> _labels = new();
        private readonly object _lock = new();

        public PipelineStatsQueryManager(Vk vk, Device device, CommandBuffer cmd,
                                         PipelineStatisticFlagsExt statFlags,
                                         uint queryCount = 256)
        {
            _vk = vk;
            _device = device;
            _cmd = cmd;
            _statFlags = statFlags;
            _resultCount = CountBits((uint)statFlags);

            var info = new QueryPoolCreateInfo
            {
                SType = StructureType.QueryPoolCreateInfo,
                QueryType = QueryType.PipelineStatistics,
                QueryCount = queryCount,
                PipelineStatistics = (QueryPipelineStatisticFlags)statFlags,
            };
            Silk.NET.Vulkan.QueryPool handle;
            var r = vk.CreateQueryPool(device, info, null, &handle);
            if (r != Result.Success)
                throw new VulkanException(r, "CreateQueryPool stats 失败");
            _pool = new QueryPool(vk, device, handle, QueryTypeExt.PipelineStatistics, queryCount);
        }

        /// <summary>开始统计查询。</summary>
        public uint BeginStats(string label)
        {
            if (!_pool.TryAllocate(out uint idx)) return uint.MaxValue;
            _vk.CmdBeginQuery(_cmd, _pool.Handle, idx, 0);
            lock (_lock) _labels[idx] = label;
            return idx;
        }

        /// <summary>结束统计查询。</summary>
        public void EndStats(uint idx)
        {
            _vk.CmdEndQuery(_cmd, _pool.Handle, idx);
        }

        /// <summary>获取结果。</summary>
        public PipelineStatsResult[] FetchResults()
        {
            var results = new List<PipelineStatsResult>();
            var toRelease = new List<uint>();

            lock (_lock)
            {
                foreach (var idx in _labels.Keys.ToList())
                {
                    var data = new ulong[_resultCount];
                    var r = _pool.GetResults(idx, 1, data, (ulong)(sizeof(ulong) * _resultCount),
                        QueryResultFlagsExt.SixtyFourBit | QueryResultFlagsExt.WithAvailability);
                    if (r == Result.Success)
                    {
                        var stat = DecodeStats(data);
                        stat.QueryIndex = idx;
                        results.Add(stat);
                        toRelease.Add(idx);
                    }
                }
                foreach (var idx in toRelease)
                {
                    _labels.Remove(idx);
                    _pool.Release(idx);
                }
            }
            return results.ToArray();
        }

        private PipelineStatsResult DecodeStats(ulong[] data)
        {
            var r = new PipelineStatsResult();
            int i = 0;
            if ((_statFlags & PipelineStatisticFlagsExt.InputAssemblyVertices) != 0)
                r.InputAssemblyVertices = data[i++];
            if ((_statFlags & PipelineStatisticFlagsExt.InputAssemblyPrimitives) != 0)
                r.InputAssemblyPrimitives = data[i++];
            if ((_statFlags & PipelineStatisticFlagsExt.VertexShaderInvocations) != 0)
                r.VertexShaderInvocations = data[i++];
            if ((_statFlags & PipelineStatisticFlagsExt.GeometryShaderInvocations) != 0)
                r.GeometryShaderInvocations = data[i++];
            if ((_statFlags & PipelineStatisticFlagsExt.GeometryShaderPrimitives) != 0)
                r.GeometryShaderPrimitives = data[i++];
            if ((_statFlags & PipelineStatisticFlagsExt.ClippingInvocations) != 0)
                r.ClippingInvocations = data[i++];
            if ((_statFlags & PipelineStatisticFlagsExt.ClippingPrimitives) != 0)
                r.ClippingPrimitives = data[i++];
            if ((_statFlags & PipelineStatisticFlagsExt.FragmentShaderInvocations) != 0)
                r.FragmentShaderInvocations = data[i++];
            if ((_statFlags & PipelineStatisticFlagsExt.TessellationControlShaderPatches) != 0)
                r.TessellationControlShaderPatches = data[i++];
            if ((_statFlags & PipelineStatisticFlagsExt.TessellationEvaluationShaderInvocations) != 0)
                r.TessellationEvaluationShaderInvocations = data[i++];
            if ((_statFlags & PipelineStatisticFlagsExt.ComputeShaderInvocations) != 0)
                r.ComputeShaderInvocations = data[i++];
            return r;
        }

        private static int CountBits(uint v)
        {
            int n = 0;
            while (v != 0) { n += (int)(v & 1); v >>= 1; }
            return Math.Max(1, n);
        }

        public void Dispose() => _pool.Dispose();
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 6: 查询池管理器主类
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 查询池管理器主入口。一个设备一个实例，统一管理所有 query。
    /// </summary>
    public sealed unsafe class VulkanQueryPoolManager : IDisposable
    {
        private readonly Vk _vk;
        private readonly Device _device;
        private readonly float _timestampPeriod;

        private TimestampQueryManager _timestampMgr;
        private OcclusionQueryManager _occlusionMgr;
        private PipelineStatsQueryManager _statsMgr;

        private readonly object _lock = new();
        private long _currentFrame;

        // 历史 (最近 N 帧的 timestamp)
        private readonly Dictionary<long, GpuTimestamp[]> _timestampHistory = new();
        private const int MaxHistoryFrames = 300;

        public float TimestampPeriod => _timestampPeriod;

        public VulkanQueryPoolManager(Vk vk, Device device, PhysicalDevice physicalDevice,
                                      uint maxTimestampPerFrame = 256,
                                      uint maxOcclusion = 1024,
                                      PipelineStatisticFlagsExt statFlags =
                                          PipelineStatisticFlagsExt.InputAssemblyVertices |
                                          PipelineStatisticFlagsExt.InputAssemblyPrimitives |
                                          PipelineStatisticFlagsExt.VertexShaderInvocations |
                                          PipelineStatisticFlagsExt.FragmentShaderInvocations |
                                          PipelineStatisticFlagsExt.ComputeShaderInvocations)
        {
            _vk = vk;
            _device = device;

            // 查询 timestampPeriod
            PhysicalDeviceProperties props;
            vk.GetPhysicalDeviceProperties(physicalDevice, &props);
            _timestampPeriod = props.Limits.TimestampPeriod;
        }

        /// <summary>每帧开始调用，绑定到某个 command buffer。</summary>
        public void BeginFrame(CommandBuffer cmd, long frameIndex)
        {
            _currentFrame = frameIndex;
            // 重新创建 manager 绑定到这个 cmd
            // 实际工程里每个 cmd buffer 一个 manager，或者把 cmd 作为参数传
            _timestampMgr = new TimestampQueryManager(_vk, _device, cmd, 256, _timestampPeriod);
            _occlusionMgr = new OcclusionQueryManager(_vk, _device, cmd);
        }

        public TimestampQueryManager Timestamp => _timestampMgr;
        public OcclusionQueryManager Occlusion => _occlusionMgr;
        public PipelineStatsQueryManager PipelineStats => _statsMgr;

        /// <summary>每帧结束调用，获取上一帧的结果。</summary>
        public void EndFrame()
        {
            if (_timestampMgr != null)
            {
                var results = _timestampMgr.FetchPendingResults();
                lock (_lock)
                {
                    _timestampHistory[_currentFrame] = results;
                    if (_timestampHistory.Count > MaxHistoryFrames)
                    {
                        var keysToRemove = new List<long>();
                        foreach (var k in _timestampHistory.Keys)
                            if (k < _currentFrame - MaxHistoryFrames) keysToRemove.Add(k);
                        foreach (var k in keysToRemove) _timestampHistory.Remove(k);
                    }
                }
            }
        }

        /// <summary>获取某帧的所有 timestamp。</summary>
        public GpuTimestamp[] GetFrameTimestamps(long frameIndex)
        {
            lock (_lock)
            {
                return _timestampHistory.TryGetValue(frameIndex, out var r) ? r : Array.Empty<GpuTimestamp>();
            }
        }

        /// <summary>获取最近 N 帧的 timestamp。</summary>
        public GpuTimestamp[][] GetRecentTimestamps(int count)
        {
            lock (_lock)
            {
                var arr = new List<GpuTimestamp[]>();
                for (long i = _currentFrame - count + 1; i <= _currentFrame; i++)
                {
                    if (_timestampHistory.TryGetValue(i, out var r)) arr.Add(r);
                }
                return arr.ToArray();
            }
        }

        /// <summary>获取统计。</summary>
        public QueryPoolStats GetStats()
        {
            var s = new QueryPoolStats
            {
                TimestampQueryCount = _timestampMgr?.PendingCount ?? 0,
                OcclusionQueryCount = _occlusionMgr?.UsedCount ?? 0,
                PipelineStatsQueryCount = _statsMgr?.UsedCount ?? 0,
            };
            s.TotalAllocated = s.TimestampQueryCount + s.OcclusionQueryCount + s.PipelineStatsQueryCount;
            return s;
        }

        public void Dispose()
        {
            _timestampMgr?.Dispose();
            _occlusionMgr?.Dispose();
            _statsMgr?.Dispose();
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 7: GPU 性能分析器 (基于 timestamp)
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// GPU 性能分析器。给上层用，自动管理 begin/end，统计每段耗时。
    /// </summary>
    public sealed class GpuProfiler
    {
        private readonly VulkanQueryPoolManager _poolMgr;
        private readonly Dictionary<string, GpuTimeRange> _activeRanges = new();
        private readonly Dictionary<string, List<double>> _history = new();
        private readonly object _lock = new();
        private const int MaxHistoryPerRange = 300;

        public GpuProfiler(VulkanQueryPoolManager poolMgr)
        {
            _poolMgr = poolMgr;
        }

        /// <summary>开始一段 GPU 计时。</summary>
        public void BeginRange(string name, PipelineStageFlags stage = PipelineStageFlags.TopOfPipeBit)
        {
            var range = _poolMgr.Timestamp.BeginRange(name, stage);
            lock (_lock) _activeRanges[name] = range;
        }

        /// <summary>结束一段 GPU 计时。</summary>
        public void EndRange(string name, PipelineStageFlags stage = PipelineStageFlags.BottomOfPipeBit)
        {
            lock (_lock)
            {
                if (!_activeRanges.TryGetValue(name, out var range)) return;
                var r = range;
                _poolMgr.Timestamp.EndRange(ref r, stage);
                _activeRanges.Remove(name);
            }
        }

        /// <summary>获取所有已完成的 range。</summary>
        public GpuTimeRange[] GetCompletedRanges()
        {
            return _poolMgr.Timestamp.ComputeRanges();
        }

        /// <summary>记录一帧的数据到历史。</summary>
        public void RecordFrame()
        {
            var ranges = GetCompletedRanges();
            lock (_lock)
            {
                foreach (var r in ranges)
                {
                    if (!_history.TryGetValue(r.Label, out var list))
                    {
                        list = new List<double>();
                        _history[r.Label] = list;
                    }
                    list.Add(r.DurationMs);
                    if (list.Count > MaxHistoryPerRange) list.RemoveAt(0);
                }
            }
        }

        /// <summary>获取某段历史的平均/最大/最小。</summary>
        public (double avg, double max, double min, int count) GetRangeStats(string name)
        {
            lock (_lock)
            {
                if (!_history.TryGetValue(name, out var list) || list.Count == 0)
                    return (0, 0, 0, 0);
                double sum = 0, max = double.MinValue, min = double.MaxValue;
                foreach (var v in list)
                {
                    sum += v;
                    if (v > max) max = v;
                    if (v < min) min = v;
                }
                return (sum / list.Count, max, min, list.Count);
            }
        }

        /// <summary>获取所有 range 名字。</summary>
        public string[] GetAllRangeNames()
        {
            lock (_lock)
            {
                var names = new string[_history.Count];
                _history.Keys.CopyTo(names, 0);
                return names;
            }
        }

        /// <summary>导出报告。</summary>
        public string ExportReport()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("=== GPU Profiler Report ===");
            foreach (var name in GetAllRangeNames())
            {
                var (avg, max, min, count) = GetRangeStats(name);
                sb.AppendLine($"  {name}: avg={avg:F3}ms, max={max:F3}ms, min={min:F3}ms, samples={count}");
            }
            return sb.ToString();
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 8: 帧时间统计
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>帧时间统计器。</summary>
    public sealed class FrameTimeTracker
    {
        private readonly VulkanQueryPoolManager _poolMgr;
        private readonly List<double> _frameTimes = new();
        private const int MaxSamples = 300;
        private double _frameStart;

        public FrameTimeTracker(VulkanQueryPoolManager poolMgr)
        {
            _poolMgr = poolMgr;
        }

        public void BeginFrame(string label = "Frame")
        {
            _poolMgr.Timestamp.WriteTimestamp(PipelineStageFlags.TopOfPipeBit, label);
        }

        public void EndFrame(string label = "Frame")
        {
            _poolMgr.Timestamp.WriteTimestamp(PipelineStageFlags.BottomOfPipeBit, label);
        }

        public void RecordFrame(double frameMs)
        {
            lock (_frameTimes)
            {
                _frameTimes.Add(frameMs);
                if (_frameTimes.Count > MaxSamples) _frameTimes.RemoveAt(0);
            }
        }

        public (double avg, double max, double min, double p99) GetStats()
        {
            lock (_frameTimes)
            {
                if (_frameTimes.Count == 0) return (0, 0, 0, 0);
                double sum = 0, max = double.MinValue, min = double.MaxValue;
                foreach (var v in _frameTimes)
                {
                    sum += v;
                    if (v > max) max = v;
                    if (v < min) min = v;
                }
                var sorted = new List<double>(_frameTimes);
                sorted.Sort();
                double p99 = sorted[(int)(sorted.Count * 0.99)];
                return (sum / _frameTimes.Count, max, min, p99);
            }
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 9: 查询池监控
    // ════════════════════════════════════════════════════════════════════════════

    public sealed class QueryPoolMonitor : IDisposable
    {
        private readonly VulkanQueryPoolManager _mgr;
        private System.Threading.Timer _timer;
        private readonly object _lock = new();
        private readonly List<QueryPoolStats> _history = new();
        private const int MaxHistory = 60;
        private bool _disposed;

        public event Action<QueryPoolStats> OnStatsUpdated;

        public QueryPoolMonitor(VulkanQueryPoolManager mgr, TimeSpan? interval = null)
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

        public QueryPoolStats[] GetHistory()
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
    // 区域 10: 查询池统计导出
    // ════════════════════════════════════════════════════════════════════════════

    public static class QueryPoolStatsExporter
    {
        public static string ToJson(in QueryPoolStats s)
        {
            return $@"{{
  ""timestamp_queries"": {s.TimestampQueryCount},
  ""occlusion_queries"": {s.OcclusionQueryCount},
  ""pipeline_stats_queries"": {s.PipelineStatsQueryCount},
  ""total_allocated"": {s.TotalAllocated},
  ""total_free"": {s.TotalFree},
  ""pending"": {s.PendingResults}
}}";
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 11: 查询池版本信息
    // ════════════════════════════════════════════════════════════════════════════

    public static class QueryPoolVersion
    {
        public const string Version = "1.0.0";
        public const string Description = "ShanyuEngine Vulkan Query Pool Manager (timestamp/occlusion/pipeline stats)";
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 12: 单元测试桩
    // ════════════════════════════════════════════════════════════════════════════

    public static class QueryPoolTestStub
    {
        public static double TestTimestampToMs(ulong ticks, float period)
            => ticks * period / 1e6;

        public static bool TestStatsFlags(PipelineStatisticFlagsExt flags, PipelineStatisticFlagsExt check)
            => (flags & check) != 0;

        public static int TestCountBits(uint v)
        {
            int n = 0;
            while (v != 0) { n += (int)(v & 1); v >>= 1; }
            return n;
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 13: 使用示例
    // ════════════════════════════════════════════════════════════════════════════

    /*
    // === 使用示例 ===
    //
    // var mgr = new VulkanQueryPoolManager(vk, device, physDevice);
    // mgr.BeginFrame(cmd, frame);
    // var profiler = new GpuProfiler(mgr);
    //
    // profiler.BeginRange("ShadowPass");
    // // ... draw shadow
    // profiler.EndRange("ShadowPass");
    //
    // profiler.BeginRange("GeometryBufferPass");
    // // ... draw geometrybuffer
    // profiler.EndRange("GeometryBufferPass");
    //
    // mgr.EndFrame();
    // profiler.RecordFrame();
    //
    // // 查看结果 (上一帧的)
    // var ranges = profiler.GetCompletedRanges();
    // foreach (var r in ranges)
    //     Console.WriteLine(r);
    */
}

// 文件结束 - VulkanQueryPool.cs
