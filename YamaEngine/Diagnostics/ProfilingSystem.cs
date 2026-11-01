using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace YamaEngine.Diagnostics
{
    // ======================================================================
    // yama完整性能分析系统 (YamaEngine.Diagnostics.ProfilingSystem)
    //
    // 整合子系统：
    //   1. ProfilerCore    — 核心计时器（高精度 Stopwatch、嵌套区域标记、线程安全）
    //   2. CPUProfiler     — CPU 性能分析（线程时间/函数调用栈/热点检测/帧图表）
    //   3. GPUProfiler     — GPU 性能分析（帧时间拆分/Pass 耗时/GPU Timestamp Query）
    //   4. MemoryProfiler  — 内存分析（分配追踪/GC 压力/内存池/泄漏检测）
    //   5. NetworkProfiler — 网络分析（带宽/包量/RTT/抖动/每连接统计）
    //   6. ProfilerOverlay  — 性能叠加显示（FPS/帧时间/Memory/GPU/Network 实时图表）
    //   7. ProfilerReport   — 报告生成（HTML/JSON 格式/历史对比/回归检测）
    //
    // 命名空间：YamaEngine.Diagnostics
    // ======================================================================

    #region ===================== 基础类型 =====================

    /// <summary>线程类型标记。</summary>
    public enum ProfilerThreadType
    {
        Main, Worker, Render, Gpu, Audio, Network, Other
    }

    /// <summary>内存分配类别。</summary>
    public enum MemoryCategory
    {
        Texture, Buffer, Mesh, Audio, Animation, Material, Scene, Scripting, Other
    }

    /// <summary>性能事件类型。</summary>
    public enum ProfilerEventType
    {
        Begin, End, Instant, Counter
    }

    /// <summary>GPU 计数器类型。</summary>
    public enum GPUCounter
    {
        DrawCall, TriangleCount, VertexCount, InstanceCount,
        ComputeDispatch, ShaderChange, PipelineChange,
        RenderTargetChange, TextureBind, BufferBind,
        TextureMemory, BufferMemory
    }

    /// <summary>GPU 瓶颈类型。</summary>
    public enum GPUBottleneckType
    {
        None, PixelBound, VertexBound, BandwidthBound, ComputeBound
    }

    #endregion

    #region ===================== ProfilerCore 核心计时器 =====================

    /// <summary>
    /// 单次性能采样统计（Welford 在线算法）。
    /// </summary>
    public sealed class ProfileSampleStats
    {
        private long _callCount;
        private double _minMs = double.PositiveInfinity;
        private double _maxMs;
        private double _sumMs;
        private double _sumSqMs;

        public string Name { get; }

        public ProfileSampleStats(string name) { Name = name; }

        public long CallCount => Interlocked.Read(ref _callCount);
        public double MinMs => _callCount == 0 ? 0 : _minMs;
        public double MaxMs => _maxMs;
        public double AvgMs => _callCount == 0 ? 0 : _sumMs / _callCount;
        public double TotalMs => _sumMs;

        public double StdDevMs
        {
            get
            {
                if (_callCount <= 1) return 0;
                double n = _callCount;
                double variance = (_sumSqMs - (_sumMs * _sumMs) / n) / (n - 1);
                return variance > 0 ? Math.Sqrt(variance) : 0;
            }
        }

        public void Record(double milliseconds)
        {
            Interlocked.Increment(ref _callCount);
            if (milliseconds < _minMs) _minMs = milliseconds;
            if (milliseconds > _maxMs) _maxMs = milliseconds;
            InterlockedAdd(ref _sumMs, milliseconds);
            InterlockedAdd(ref _sumSqMs, milliseconds * milliseconds);
        }

        public void Reset()
        {
            _callCount = 0;
            _minMs = double.PositiveInfinity;
            _maxMs = 0;
            _sumMs = 0;
            _sumSqMs = 0;
        }

        private static void InterlockedAdd(ref double location, double value)
        {
            double current = location;
            while (true)
            {
                double newValue = current + value;
                long currentBits = BitConverter.DoubleToInt64Bits(current);
                long newBits = BitConverter.DoubleToInt64Bits(newValue);
                long original = Interlocked.CompareExchange(ref UnsafeAsDoubleToLong(ref location), newBits, currentBits);
                if (original == currentBits) return;
                current = BitConverter.Int64BitsToDouble(original);
            }
        }

        private static ref long UnsafeAsDoubleToLong(ref double d) =>
            ref System.Runtime.CompilerServices.Unsafe.As<double, long>(ref d);
    }

    /// <summary>调用树节点。</summary>
    public sealed class CallTreeNode
    {
        public string Name { get; }
        public ProfilerThreadType ThreadType { get; }
        public ProfileSampleStats Stats { get; }
        public CallTreeNode? Parent { get; internal set; }
        public List<CallTreeNode> Children { get; } = new();

        private double _frameSelfMs;
        private double _frameTotalMs;

        public double FrameSelfMs => _frameSelfMs;
        public double FrameTotalMs => _frameTotalMs;

        public CallTreeNode(string name, ProfilerThreadType threadType)
        {
            Name = name;
            ThreadType = threadType;
            Stats = new ProfileSampleStats(name);
        }

        public CallTreeNode GetOrCreateChild(string name, ProfilerThreadType threadType)
        {
            for (int i = 0; i < Children.Count; i++)
                if (Children[i].Name == name && Children[i].ThreadType == threadType)
                    return Children[i];
            var child = new CallTreeNode(name, threadType) { Parent = this };
            Children.Add(child);
            return child;
        }

        internal void AddFrameSample(double totalMs)
        {
            _frameTotalMs += totalMs;
            Stats.Record(totalMs);
        }

        internal void FinalizeFrame()
        {
            double childSum = 0;
            for (int i = 0; i < Children.Count; i++)
            {
                Children[i].FinalizeFrame();
                childSum += Children[i]._frameTotalMs;
            }
            _frameSelfMs = Math.Max(0, _frameTotalMs - childSum);
        }

        internal void ResetFrame()
        {
            _frameSelfMs = 0;
            _frameTotalMs = 0;
            for (int i = 0; i < Children.Count; i++)
                Children[i].ResetFrame();
        }
    }

    /// <summary>线程安全的分层性能计时器。</summary>
    public sealed class ProfilingScopeTimer
    {
        [ThreadStatic] private static Stack<ScopeFrame>? _stack;
        [ThreadStatic] private static ProfilerThreadType _threadType = ProfilerThreadType.Other;

        private readonly Profiler _profiler;

        internal ProfilingScopeTimer(Profiler profiler) { _profiler = profiler; }

        public static void SetThreadType(ProfilerThreadType type) => _threadType = type;
        public static ProfilerThreadType GetThreadType() => _threadType;

        internal ScopeFrame BeginScope(string name)
        {
            var stack = _stack ??= new Stack<ScopeFrame>();
            var node = _profiler.GetOrCreateNode(name, _threadType,
                stack.Count > 0 ? stack.Peek().Node : null);
            var frame = new ScopeFrame(node, _profiler.GetTimestamp());
            stack.Push(frame);
            return frame;
        }

        internal void EndScope()
        {
            var stack = _stack;
            if (stack == null || stack.Count == 0) return;
            var frame = stack.Pop();
            double elapsed = _profiler.GetElapsedMs(frame.StartTimestamp);
            frame.Node.AddFrameSample(elapsed);
        }

        internal struct ScopeFrame
        {
            public readonly CallTreeNode Node;
            public readonly long StartTimestamp;
            public ScopeFrame(CallTreeNode node, long ts) { Node = node; StartTimestamp = ts; }
        }
    }

    /// <summary>
    /// 性能分析作用域（配合 using 语句）。
    /// ProfilingScope 已移至 MemoryTracker.cs，此处保留为新版本的类型别名。
    /// </summary>

    /// <summary>性能事件。</summary>
    public readonly struct ProfilerEvent
    {
        public string Name { get; }
        public string Category { get; }
        public ProfilerEventType Type { get; }
        public long TimestampUs { get; }
        public int ThreadId { get; }
        public double Value { get; }
        public uint ColorArgb { get; }

        public ProfilerEvent(string name, string category, ProfilerEventType type,
                              long timestampUs, int threadId, double value = 0, uint color = 0)
        {
            Name = name; Category = category; Type = type;
            TimestampUs = timestampUs; ThreadId = threadId; Value = value; ColorArgb = color;
        }
    }

    #endregion

    #region ===================== CPUProfiler (已移至新 Diagnostics/CPUProfiler.cs)

#endregion

    #region ===================== GPUProfiler (已移至新 Diagnostics/GPUProfiler.cs)

#endregion

    #region ===================== MemoryProfiler (已移至新 Diagnostics/MemoryProfiler.cs)

#endregion

    #region ===================== NetworkProfiler (已移至新 Diagnostics/NetworkProfiler.cs)

#endregion

    #region ===================== ProfilerOverlay 性能叠加显示 =====================

    /// <summary>叠加显示模式。</summary>
    public enum OverlayMode
    {
        Off, Minimal, Standard, Detailed, Full
    }

    /// <summary>性能叠加显示。</summary>
    public sealed class ProfilerOverlay
    {
        /// <summary>显示模式。</summary>
        public OverlayMode Mode { get; set; } = OverlayMode.Standard;

        /// <summary>叠加文本内容。</summary>
        public string OverlayText { get; private set; } = "";

        /// <summary>更新频率限制（秒）。</summary>
        public float UpdateInterval { get; set; } = 0.25f;

        private float _updateTimer;
        private bool _dirty = true;

        /// <summary>标记内容需要更新。</summary>
        public void MarkDirty() => _dirty = true;

        /// <summary>每帧更新叠加文本。</summary>
        public void Update(float deltaTime, Profiler profiler)
        {
            _updateTimer += deltaTime;
            if (_updateTimer < UpdateInterval && !_dirty) return;
            _updateTimer = 0;
            _dirty = false;

            if (Mode == OverlayMode.Off) { OverlayText = ""; return; }

            var sb = new StringBuilder(512);

            // FPS & Frame
            var cpu = profiler.CPU;
            var gpu = profiler.GPU;
            var mem = profiler.Memory;
            var net = profiler.Network;

            sb.AppendLine($"FPS: {cpu.CurrentFPS:F1} (Avg: {cpu.AverageFPS:F1})");
            sb.AppendLine($"Frame: {cpu.CurrentFrame.TotalFrameMs:F2}ms (CPU: {cpu.CurrentFrame.MainThreadMs:F1}ms / GPU: {gpu.CurrentFrameGpuMs:F1}ms)");

            if (Mode >= OverlayMode.Standard)
            {
                // Memory
                long totalMem = mem.TotalAllocatedBytes;
                sb.AppendLine($"Memory: {FormatBytes(totalMem)} | Process: {FormatBytes(Environment.WorkingSet)} | Allocs: {mem.AllocationCount}");

                // GPU
                sb.AppendLine($"GPU: Draw {gpu.Counters.GetValueOrDefault(GPUCounter.DrawCall)} | " +
                              $"Tri {gpu.Counters.GetValueOrDefault(GPUCounter.TriangleCount)} | " +
                              $"Bottleneck: {gpu.DetectedBottleneck}");
            }

            if (Mode >= OverlayMode.Detailed)
            {
                // GPU Pass breakdown
                sb.AppendLine("--- GPU Passes ---");
                foreach (var pass in gpu.RenderPasses.Take(10))
                    sb.AppendLine($"  {pass.Name}: {pass.DurationMs:F2}ms ({pass.DrawCalls} DC)");

                // Thread utilization
                var util = cpu.GetThreadUtilization();
                sb.AppendLine("--- Thread Utilization ---");
                foreach (var kvp in util)
                    sb.AppendLine($"  {kvp.Key}: {kvp.Value:F1}%");
            }

            if (Mode >= OverlayMode.Full)
            {
                // Network
                sb.AppendLine($"Network: {net.GetSummary()}");

                // Hot spots
                var hotspots = cpu.GetHotSpots(profiler.GetRootNode(), 5);
                sb.AppendLine("--- CPU Hotspots ---");
                foreach (var hs in hotspots)
                    sb.AppendLine($"  {hs.FunctionName}: {hs.SelfMs:F2}ms ({hs.CallCount} calls)");

                // Memory leaks
                var leaks = mem.DetectLeaks();
                if (leaks.Count > 0)
                {
                    sb.AppendLine($"--- Memory Leaks ({leaks.Count}) ---");
                    foreach (var leak in leaks.Take(5))
                        sb.AppendLine($"  [{leak.Tag}] {FormatBytes((long)leak.SizeBytes)} age={leak.Age}");
                }
            }

            OverlayText = sb.ToString();
        }

        private static string FormatBytes(long bytes) =>
            bytes < 1024 ? $"{bytes} B" :
            bytes < 1048576 ? $"{bytes / 1024.0:F1} KB" :
            bytes < 1073741824 ? $"{bytes / 1048576.0:F1} MB" :
            $"{bytes / 1073741824.0:F2} GB";
    }

    #endregion

    #region ===================== ProfilerReport 报告生成 =====================

    /// <summary>报告格式。</summary>
    public enum ReportFormat { HTML, JSON }

    /// <summary>报告内容。</summary>
    public sealed class ProfilerReportData
    {
        public string Title { get; set; } = "Performance Report";
        public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
        public double DurationSeconds { get; set; }

        // CPU
        public float AverageFPS { get; set; }
        public float WorstFrameMs { get; set; }
        public float BestFrameMs { get; set; }
        public float AverageFrameMs { get; set; }
        public List<CPUHotSpot> HotSpots { get; set; } = new();
        public Dictionary<string, double> ThreadUtilization { get; set; } = new();

        // GPU
        public double AverageGpuMs { get; set; }
        public double PeakGpuMs { get; set; }
        public GPUBottleneckType Bottleneck { get; set; }
        public List<RenderPassStats> RenderPasses { get; set; } = new();
        public Dictionary<GPUCounter, long> GPUCounters { get; set; } = new();

        // Memory
        public long TotalMemoryAllocated { get; set; }
        public long ProcessMemoryBytes { get; set; }
        public int AllocationCount { get; set; }
        public Dictionary<MemoryCategory, long> MemoryByCategory { get; set; } = new();
        public List<LeakSuspect> LeakSuspects { get; set; } = new();

        // Network
        public string NetworkSummary { get; set; } = "";
        public int ConnectionCount { get; set; }

        // 历史
        public float[] FrameTimeHistory { get; set; } = Array.Empty<float>();
        public float[] GpuTimeHistory { get; set; } = Array.Empty<float>();
    }

    /// <summary>回归检测结果。</summary>
    public sealed class RegressionCheckResult
    {
        public bool HasRegression { get; set; }
        public string MetricName { get; set; } = "";
        public double BaselineValue { get; set; }
        public double CurrentValue { get; set; }
        public double RegressionPercent { get; set; }
        public string Description { get; set; } = "";
    }

    /// <summary>报告生成器 + 综合报告数据模型。</summary>
    public sealed class ProfilerReport
    {
        // ── 元数据 ──
        public string Name { get; set; } = "";
        public Guid ReportId { get; set; } = Guid.NewGuid();
        public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
        public double DurationMs { get; set; }
        public int FrameCount { get; set; }
        public int FrameNumber { get; set; }

        // ── CPU ──
        public double CurrentFPS { get; set; }
        public double AverageFPS { get; set; }
        public double AvgFrameTimeMs { get; set; }
        public double MaxFrameTimeMs { get; set; }
        public double MinFrameTimeMs { get; set; }
        public double FPS_1Percent { get; set; }
        public double FPS_0_1Percent { get; set; }
        public double StdDevFrameTimeMs { get; set; }

        // ── GPU ──
        public int DrawCalls { get; set; }
        public long Triangles { get; set; }
        public long Vertices { get; set; }
        public double TotalGpuMs { get; set; }
        public int ShaderSwitches { get; set; }
        public double TextureMemoryMB { get; set; }
        public double BufferMemoryMB { get; set; }
        public int RenderPassCount { get; set; }
        public string GPUBottleneckDescription { get; set; } = "";

        // ── 内存 ──
        public long TotalMemoryBytes { get; set; }
        public long ManagedMemoryBytes { get; set; }
        public long ProcessMemoryBytes { get; set; }
        public int ActiveAllocations { get; set; }
        public int LeakCount { get; set; }
        public string MemoryLeakSummary { get; set; } = "";
        public Dictionary<string, long> MemoryCategoryBreakdown { get; set; } = new();

        // ── 网络 ──
        public long NetworkBytesSent { get; set; }
        public long NetworkBytesReceived { get; set; }
        public double NetworkRTTMs { get; set; }
        public double NetworkPacketLoss { get; set; }
        public string NetworkQuality { get; set; } = "";

        // ── 音频 ──
        public int AudioActiveSources { get; set; }
        public double AudioTotalCpuMs { get; set; }
        public long AudioMemoryBytes { get; set; }
        public int AudioUnderrunCount { get; set; }
        public string AudioBottleneckDescription { get; set; } = "";

        // ── 自动检测与预算 ──
        public List<BottleneckDetection> Bottlenecks { get; set; } = new();
        public Dictionary<string, bool> BudgetChecks { get; set; } = new();
        public List<string> BudgetWarnings { get; set; } = new();

        /// <summary>生成报告数据。</summary>
        public ProfilerReportData Generate(Profiler profiler)
        {
            var data = new ProfilerReportData
            {
                GeneratedAt = DateTime.UtcNow,
                AverageFPS = profiler.CPU.AverageFPS,
                WorstFrameMs = profiler.CPU.WorstFrameMs,
                BestFrameMs = profiler.CPU.BestFrameMs,
                AverageFrameMs = profiler.CPU.AverageFPS > 0 ? 1000f / profiler.CPU.AverageFPS : 0,
                HotSpots = profiler.CPU.GetHotSpots(profiler.GetRootNode()),
                ThreadUtilization = profiler.CPU.GetThreadUtilization(),
                AverageGpuMs = profiler.GPU.AverageGpuMs,
                PeakGpuMs = profiler.GPU.PeakGpuMs,
                Bottleneck = profiler.GPU.DetectedBottleneck,
                RenderPasses = new List<RenderPassStats>(profiler.GPU.RenderPasses),
                GPUCounters = new Dictionary<GPUCounter, long>(profiler.GPU.Counters),
                TotalMemoryAllocated = profiler.Memory.TotalAllocatedBytes,
                ProcessMemoryBytes = Environment.WorkingSet,
                AllocationCount = profiler.Memory.AllocationCount,
                MemoryByCategory = profiler.Memory.GetAllCategoryBytes(),
                LeakSuspects = profiler.Memory.DetectLeaks(),
                NetworkSummary = profiler.Network.GetSummary(),
                ConnectionCount = profiler.Network.ConnectionCount,
                FrameTimeHistory = profiler.CPU.GetFrameTimeHistory(),
                GpuTimeHistory = profiler.GPU.GetGpuTimeHistory()
            };
            return data;
        }

        /// <summary>导出为 JSON 格式。</summary>
        public string ExportJSON(ProfilerReportData data)
        {
            return JsonSerializer.Serialize(data, new JsonSerializerOptions
            { WriteIndented = true, IncludeFields = true });
        }

        /// <summary>导出为 HTML 格式。</summary>
        public string ExportHTML(ProfilerReportData data)
        {
            var sb = new StringBuilder(4096);
            sb.AppendLine("<!DOCTYPE html><html><head><meta charset='utf-8'>");
            sb.AppendLine("<title>YamaEngine Performance Report</title>");
            sb.AppendLine("<style>");
            sb.AppendLine("body{font-family:'Segoe UI',sans-serif;margin:20px;background:#1a1a2e;color:#e0e0e0}");
            sb.AppendLine("h1{color:#00d4ff}h2{color:#7b68ee;border-bottom:1px solid #333;padding-bottom:5px}");
            sb.AppendLine("table{border-collapse:collapse;width:100%;margin:10px 0}");
            sb.AppendLine("th,td{border:1px solid #444;padding:6px 10px;text-align:left}");
            sb.AppendLine("th{background:#2d2d4e;color:#00d4ff}");
            sb.AppendLine(".warn{color:#ff6b6b}.good{color:#51cf66}.metric{font-size:1.2em;color:#ffd43b}");
            sb.AppendLine("</style></head><body>");
            sb.AppendLine($"<h1>{data.Title}</h1>");
            sb.AppendLine($"<p>Generated: {data.GeneratedAt:yyyy-MM-dd HH:mm:ss}</p>");

            // CPU
            sb.AppendLine("<h2>CPU Performance</h2>");
            sb.AppendLine("<table><tr><th>Metric</th><th>Value</th></tr>");
            sb.AppendLine($"<tr><td>Average FPS</td><td class='metric'>{data.AverageFPS:F1}</td></tr>");
            sb.AppendLine($"<tr><td>Worst Frame</td><td class='{(data.WorstFrameMs > 33 ? "warn" : "good")}'>{data.WorstFrameMs:F2}ms</td></tr>");
            sb.AppendLine($"<tr><td>Best Frame</td><td class='good'>{data.BestFrameMs:F2}ms</td></tr>");
            sb.AppendLine($"<tr><td>Average Frame</td><td>{data.AverageFrameMs:F2}ms</td></tr>");
            sb.AppendLine("</table>");

            // Hotspots
            sb.AppendLine("<h3>CPU Hotspots</h3><table><tr><th>Function</th><th>Self (ms)</th><th>Calls</th><th>Avg</th></tr>");
            foreach (var hs in data.HotSpots.Take(10))
                sb.AppendLine($"<tr><td>{hs.FunctionName}</td><td>{hs.SelfMs:F2}</td><td>{hs.CallCount}</td><td>{hs.AvgMs:F3}</td></tr>");
            sb.AppendLine("</table>");

            // GPU
            sb.AppendLine("<h2>GPU Performance</h2>");
            sb.AppendLine("<table><tr><th>Metric</th><th>Value</th></tr>");
            sb.AppendLine($"<tr><td>Average GPU Time</td><td>{data.AverageGpuMs:F2}ms</td></tr>");
            sb.AppendLine($"<tr><td>Peak GPU Time</td><td class='warn'>{data.PeakGpuMs:F2}ms</td></tr>");
            sb.AppendLine($"<tr><td>Bottleneck</td><td>{data.Bottleneck}</td></tr>");
            sb.AppendLine($"<tr><td>Draw Calls</td><td>{data.GPUCounters.GetValueOrDefault(GPUCounter.DrawCall)}</td></tr>");
            sb.AppendLine($"<tr><td>Triangles</td><td>{data.GPUCounters.GetValueOrDefault(GPUCounter.TriangleCount):N0}</td></tr>");
            sb.AppendLine("</table>");

            // GPU Passes
            sb.AppendLine("<h3>Render Passes</h3><table><tr><th>Pass</th><th>Time (ms)</th><th>Draw Calls</th><th>Triangles</th></tr>");
            foreach (var pass in data.RenderPasses)
                sb.AppendLine($"<tr><td>{pass.Name}</td><td>{pass.DurationMs:F2}</td><td>{pass.DrawCalls}</td><td>{pass.Triangles:N0}</td></tr>");
            sb.AppendLine("</table>");

            // Memory
            sb.AppendLine("<h2>Memory</h2>");
            sb.AppendLine("<table><tr><th>Category</th><th>Bytes</th></tr>");
            sb.AppendLine($"<tr><td>Total Allocated</td><td class='metric'>{FormatBytes(data.TotalMemoryAllocated)}</td></tr>");
            sb.AppendLine($"<tr><td>Process Working Set</td><td>{FormatBytes(data.ProcessMemoryBytes)}</td></tr>");
            sb.AppendLine($"<tr><td>Active Allocations</td><td>{data.AllocationCount}</td></tr>");
            foreach (var kvp in data.MemoryByCategory)
                sb.AppendLine($"<tr><td>{kvp.Key}</td><td>{FormatBytes(kvp.Value)}</td></tr>");
            sb.AppendLine("</table>");

            // Leaks
            if (data.LeakSuspects.Count > 0)
            {
                sb.AppendLine($"<h3 class='warn'>Memory Leak Suspects ({data.LeakSuspects.Count})</h3>");
                sb.AppendLine("<table><tr><th>Tag</th><th>Size</th><th>Age</th></tr>");
                foreach (var leak in data.LeakSuspects.Take(20))
                    sb.AppendLine($"<tr><td>{leak.Tag}</td><td>{FormatBytes((long)leak.SizeBytes)}</td><td>{leak.Age}</td></tr>");
                sb.AppendLine("</table>");
            }

            // Network
            sb.AppendLine("<h2>Network</h2>");
            sb.AppendLine($"<p>{data.NetworkSummary}</p>");
            sb.AppendLine($"<p>Active Connections: {data.ConnectionCount}</p>");

            sb.AppendLine("</body></html>");
            return sb.ToString();
        }

        /// <summary>保存报告到文件。</summary>
        public void SaveToFile(ProfilerReportData data, string path, ReportFormat format)
        {
            string content = format == ReportFormat.HTML ? ExportHTML(data) : ExportJSON(data);
            string ext = format == ReportFormat.HTML ? ".html" : ".json";
            string fullPath = path.EndsWith(ext) ? path : path + ext;
            File.WriteAllText(fullPath, content);
        }

        /// <summary>回归检测：与基线对比，检测性能退化。</summary>
        public List<RegressionCheckResult> CheckRegression(
            ProfilerReportData baseline, ProfilerReportData current,
            float fpsRegressionThreshold = 0.1f, // FPS 下降 10%
            float frameTimeRegressionThreshold = 0.2f, // 帧时间增加 20%
            float memoryRegressionThreshold = 0.3f) // 内存增加 30%
        {
            var results = new List<RegressionCheckResult>();

            // FPS 回归
            if (baseline.AverageFPS > 0)
            {
                float fpsDiff = (baseline.AverageFPS - current.AverageFPS) / baseline.AverageFPS;
                if (fpsDiff > fpsRegressionThreshold)
                {
                    results.Add(new RegressionCheckResult
                    {
                        HasRegression = true, MetricName = "AverageFPS",
                        BaselineValue = baseline.AverageFPS, CurrentValue = current.AverageFPS,
                        RegressionPercent = fpsDiff * 100,
                        Description = $"FPS dropped from {baseline.AverageFPS:F1} to {current.AverageFPS:F1} ({fpsDiff * 100:F1}%)"
                    });
                }
            }

            // 帧时间回归
            if (baseline.AverageFrameMs > 0)
            {
                double frameDiff = (current.AverageFrameMs - baseline.AverageFrameMs) / baseline.AverageFrameMs;
                if (frameDiff > frameTimeRegressionThreshold)
                {
                    results.Add(new RegressionCheckResult
                    {
                        HasRegression = true, MetricName = "AverageFrameTime",
                        BaselineValue = baseline.AverageFrameMs, CurrentValue = current.AverageFrameMs,
                        RegressionPercent = frameDiff * 100,
                        Description = $"Frame time increased from {baseline.AverageFrameMs:F2}ms to {current.AverageFrameMs:F2}ms ({frameDiff * 100:F1}%)"
                    });
                }
            }

            // 内存回归
            if (baseline.TotalMemoryAllocated > 0)
            {
                double memDiff = (current.TotalMemoryAllocated - baseline.TotalMemoryAllocated) / (double)baseline.TotalMemoryAllocated;
                if (memDiff > memoryRegressionThreshold)
                {
                    results.Add(new RegressionCheckResult
                    {
                        HasRegression = true, MetricName = "TotalMemory",
                        BaselineValue = baseline.TotalMemoryAllocated, CurrentValue = current.TotalMemoryAllocated,
                        RegressionPercent = memDiff * 100,
                        Description = $"Memory increased by {memDiff * 100:F1}% ({FormatBytes(baseline.TotalMemoryAllocated)} -> {FormatBytes(current.TotalMemoryAllocated)})"
                    });
                }
            }

            return results;
        }

        private static string FormatBytes(long bytes) =>
            bytes < 1024 ? $"{bytes} B" :
            bytes < 1048576 ? $"{bytes / 1024.0:F1} KB" :
            bytes < 1073741824 ? $"{bytes / 1048576.0:F1} MB" :
            $"{bytes / 1073741824.0:F2} GB";
    }

    #endregion

    #region ===================== Profiler 全局入口 =====================

    /// <summary>性能分析颜色常量。</summary>
    public static class ProfilerColors
    {
        public const uint CpuScope = 0xFF4CAF50;
        public const uint GpuPass = 0xFF2196F3;
        public const uint Memory = 0xFFFF9800;
        public const uint Network = 0xFF9C27B0;
    }

    /// <summary>帧统计面板数据。</summary>
    public sealed class FrameStatsPanel
    {
        public ulong FrameNumber { get; set; }
        public float Fps { get; set; }
        public float FrameMs { get; set; }
        public float CpuMs { get; set; }
        public float GpuMs { get; set; }
        public int DrawCalls { get; set; }
        public long Triangles { get; set; }
        public long Vertices { get; set; }
        public int RenderPasses { get; set; }
        public long ManagedMemoryKb { get; set; }
        public long ProcessMemoryKb { get; set; }
        public float GpuMemoryUsedMb { get; set; }
        public float GpuMemoryTotalMb { get; set; }
    }

    /// <summary>
    /// yama引擎性能分析器全局入口。
    /// 整合 CPU / GPU / Memory / Network / Overlay / Report 全部子系统。
    /// </summary>
    public sealed class Profiler
    {
        // ── 子系统实例 ──

        /// <summary>CPU 性能分析器。</summary>
        public CPUProfiler CPU { get; } = new();

        /// <summary>GPU 性能分析器。</summary>
        public GPUProfiler GPU { get; } = new();

        /// <summary>内存分析器。</summary>
        public MemoryProfiler Memory { get; } = new();

        /// <summary>网络分析器。</summary>
        public NetworkProfiler Network { get; } = new();

        /// <summary>性能叠加显示。</summary>
        public ProfilerOverlay Overlay { get; } = new();

        /// <summary>报告生成器。</summary>
        public ProfilerReport Report { get; } = new();

        /// <summary>全局默认分析器实例。</summary>
        public static Profiler Instance { get; } = new();

        // ── 核心计时器 ──

        private readonly ProfilingScopeTimer _scopeTimer;
        private readonly Dictionary<string, CallTreeNode> _roots = new();
        private readonly object _treeLock = new();

        /// <summary>帧号。</summary>
        private ulong _frameNumber;

        /// <summary>是否启用 CPU 性能分析。</summary>
        public bool CpuProfilingEnabled { get; set; } = true;

        /// <summary>是否启用 GPU 性能分析。</summary>
        public bool GpuProfilingEnabled { get; set; } = true;

        /// <summary>是否启用叠加显示。</summary>
        public bool HudEnabled { get; set; } = true;

        /// <summary>性能警告回调。</summary>
        public event Action<MemoryCategory, long, long>? OnMemoryWarning;

        public Profiler() { _scopeTimer = new ProfilingScopeTimer(this); }

        // ── 静态便捷入口（供 ProfilerCore 的 ProfilerSampleScope / ProfilerSample 使用）──

        /// <summary>当前默认分析会话。</summary>
        public static ProfilerSession ActiveSession => _staticSession ??= CreateActiveSession();

        private static ProfilerSession? _staticSession;

        private static ProfilerSession CreateActiveSession()
        {
            var session = new ProfilerSession { Name = "Default" };
            session.Start();
            return session;
        }

        /// <summary>开始一次命名采样（静态便捷 API）。</summary>
        public static void BeginSample(string name, ProfilerCategory category = ProfilerCategory.CPU)
        {
            var session = ActiveSession;
            var thread = session.GetOrCreateThread(Environment.CurrentManagedThreadId, "Main");
            var marker = session.GetOrCreateMarker(name, category);
            thread.BeginSample(marker);
        }

        /// <summary>结束当前采样（静态便捷 API）。</summary>
        public static void EndSample()
        {
            var session = ActiveSession;
            var thread = session.GetOrCreateThread(Environment.CurrentManagedThreadId, "Main");
            thread.EndSample();
        }

        // ── 时间戳工具 ──

        /// <summary>获取高精度时间戳（Tick）。</summary>
        public long GetTimestamp() => Stopwatch.GetTimestamp();

        /// <summary>计算两个时间戳之间的毫秒差。</summary>
        public double GetElapsedMs(long startTimestamp) =>
            (Stopwatch.GetTimestamp() - startTimestamp) * 1000.0 / Stopwatch.Frequency;

        // ── CPU 作用域 API ──

        /// <summary>开始一个 CPU 采样作用域。</summary>
        public void BeginCpuScope(string name) => _scopeTimer.BeginScope(name);

        /// <summary>结束当前 CPU 采样作用域。</summary>
        public void EndCpuScope() => _scopeTimer.EndScope();

        /// <summary>便捷 API：创建一个 using 作用域（委托给新版 ProfilingScope）。</summary>
        public ProfilingScope Scope(string name) => ProfilingScope.Begin(name);

        /// <summary>获取或创建调用树根节点。</summary>
        internal CallTreeNode GetOrCreateNode(string name, ProfilerThreadType threadType, CallTreeNode? parent)
        {
            lock (_treeLock)
            {
                if (parent != null) return parent.GetOrCreateChild(name, threadType);
                if (!_roots.TryGetValue(name, out var node))
                {
                    node = new CallTreeNode(name, threadType);
                    _roots[name] = node;
                }
                return node;
            }
        }

        /// <summary>获取调用树根节点（默认取第一个）。</summary>
        public CallTreeNode? GetRootNode()
        {
            lock (_treeLock) return _roots.Values.FirstOrDefault();
        }

        // ── 内存跟踪 API ──

        /// <summary>跟踪内存分配。</summary>
        public int TrackAllocation(MemoryCategory category, long bytes, string name, bool isGpu = false)
        {
            return Memory.TrackAllocation(bytes, name, category);
        }

        /// <summary>取消跟踪内存分配。</summary>
        public void UntrackAllocation(int handle) => Memory.UntrackAllocation(handle);

        /// <summary>获取内存使用量。</summary>
        public long GetMemoryUsage(MemoryCategory category) => Memory.GetCategoryBytes(category);

        /// <summary>获取叠加文本。</summary>
        public string GetHudText() => Overlay.OverlayText;

        // ── 帧控制 API ──

        private readonly Queue<float> _frameTimeHistory = new();
        private const int FrameHistorySize = 300;

        /// <summary>帧开始。</summary>
        public void BeginFrame()
        {
            _frameNumber++;
            lock (_treeLock)
                foreach (var root in _roots.Values) root.ResetFrame();
            GPU.BeginFrame();
        }

        /// <summary>帧结束。</summary>
        public void EndFrame(float frameMs)
        {
            // 完成调用树
            lock (_treeLock)
                foreach (var root in _roots.Values) root.FinalizeFrame();

            // CPU 帧统计
            var cpuTiming = new CPUFrameTiming
            {
                FrameNumber = _frameNumber,
                MainThreadMs = frameMs,
                TotalFrameMs = frameMs
            };
            CPU.RecordFrame(cpuTiming);

            // 内存帧结束
            Memory.EndFrame();
            Memory.UpdateGCInfo();

            // 网络带宽更新
            Network.UpdateBandwidth();

            // 帧时间历史
            _frameTimeHistory.Enqueue(frameMs);
            while (_frameTimeHistory.Count > FrameHistorySize) _frameTimeHistory.Dequeue();
        }

        /// <summary>获取帧时间历史。</summary>
        public float[] GetFrameTimeHistory()
        {
            var arr = new float[_frameTimeHistory.Count];
            int i = 0;
            foreach (var v in _frameTimeHistory) arr[i++] = v;
            return arr;
        }

        /// <summary>当前帧号。</summary>
        public ulong FrameNumber => _frameNumber;

        // ── Chrome Tracing 导出 ──

        private readonly List<ProfilerEvent> _events = new();
        private readonly object _eventLock = new();

        /// <summary>发出即时事件。</summary>
        public void EmitInstant(string name, string category, uint color = 0)
        {
            lock (_eventLock)
                _events.Add(new ProfilerEvent(name, category, ProfilerEventType.Instant,
                    GetTimestampUs(), Environment.CurrentManagedThreadId, 0, color));
        }

        /// <summary>发出计数器事件。</summary>
        public void EmitCounter(string name, string category, double value, uint color = 0)
        {
            lock (_eventLock)
                _events.Add(new ProfilerEvent(name, category, ProfilerEventType.Counter,
                    GetTimestampUs(), Environment.CurrentManagedThreadId, value, color));
        }

        private long GetTimestampUs() => Stopwatch.GetTimestamp() * 1_000_000 / Stopwatch.Frequency;

        /// <summary>导出 Chrome Tracing JSON。</summary>
        public string ExportChromeTracingJson()
        {
            List<ProfilerEvent> snapshot;
            lock (_eventLock) snapshot = new List<ProfilerEvent>(_events);

            var sb = new StringBuilder(snapshot.Count * 128);
            sb.Append("{\"traceEvents\":[");
            for (int i = 0; i < snapshot.Count; i++)
            {
                var e = snapshot[i];
                if (i > 0) sb.Append(',');
                sb.Append('{');
                sb.Append("\"name\":\"").Append(Escape(e.Name)).Append("\",");
                sb.Append("\"cat\":\"").Append(Escape(e.Category)).Append("\",");
                sb.Append("\"pid\":1,\"tid\":").Append(e.ThreadId).Append(',');
                sb.Append("\"ts\":").Append(e.TimestampUs).Append(',');
                switch (e.Type)
                {
                    case ProfilerEventType.Begin:
                        sb.Append("\"ph\":\"B\""); break;
                    case ProfilerEventType.End:
                        sb.Append("\"ph\":\"E\""); break;
                    case ProfilerEventType.Instant:
                        sb.Append("\"ph\":\"i\",\"s\":\"g\""); break;
                    case ProfilerEventType.Counter:
                        sb.Append("\"ph\":\"C\",\"value\":").Append(e.Value.ToString("F3"));
                        break;
                }
                sb.Append('}');
            }
            sb.Append("]}");
            return sb.ToString();
        }

        private static string Escape(string s) =>
            s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");

        // ── 完整报告 API ──

        /// <summary>生成性能报告。</summary>
        public ProfilerReportData GenerateReport() => Report.Generate(this);

        /// <summary>保存 HTML 报告。</summary>
        public void SaveHTMLReport(string path) =>
            Report.SaveToFile(GenerateReport(), path, ReportFormat.HTML);

        /// <summary>保存 JSON 报告。</summary>
        public void SaveJSONReport(string path) =>
            Report.SaveToFile(GenerateReport(), path, ReportFormat.JSON);

        /// <summary>回归检测。</summary>
        public List<RegressionCheckResult> CheckRegression(ProfilerReportData baseline) =>
            Report.CheckRegression(baseline, GenerateReport());

        /// <summary>重置所有子系统。</summary>
        public void ResetAll()
        {
            CPU.Reset(); GPU.Reset(); Memory.Reset(); Network.Reset();
            _frameNumber = 0;
            _frameTimeHistory.Clear();
            lock (_treeLock) _roots.Clear();
            lock (_eventLock) _events.Clear();
        }
    }

    #endregion
}
