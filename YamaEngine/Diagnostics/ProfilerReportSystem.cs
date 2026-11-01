using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace YamaEngine.Diagnostics
{
    // ══════════════════════════════════════════════════════════════════
    // 分析报告系统（ProfilerReportSystem）
    // ══════════════════════════════════════════════════════════════════
    // 本文件实现完整的性能分析报告系统，包含：
    //   1. ProfilerReport：综合报告（CPU/GPU/内存/网络/音频汇总）
    //   2. 报告生成（JSON/HTML/CSV 格式）
    //   3. 报告比较（两份报告对比：帧率变化/内存变化/瓶颈变化）
    //   4. 自动瓶颈检测（CPU瓶颈/GPU瓶颈/内存瓶颈/IO瓶颈/建议）
    //   5. 性能预算（PerformanceBudget：目标帧率/目标内存/超标报警）
    //   6. 历史趋势（最近N次报告的趋势分析）
    //   7. 自动截图（性能低于阈值时自动截图留存）
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 性能瓶颈类型。
    /// </summary>
    public enum BottleneckType
    {
        /// <summary>无瓶颈</summary>
        None,
        /// <summary>CPU 瓶颈</summary>
        CPU,
        /// <summary>GPU 瓶颈</summary>
        GPU,
        /// <summary>内存瓶颈</summary>
        Memory,
        /// <summary>IO 瓶颈</summary>
        IO,
        /// <summary>网络瓶颈</summary>
        Network,
        /// <summary>音频瓶颈</summary>
        Audio
    }

    /// <summary>
    /// 瓶颈检测结果。
    /// </summary>
    public sealed class BottleneckDetection
    {
        public BottleneckType Type { get; set; } = BottleneckType.None;
        /// <summary>置信度（0~1）</summary>
        public double Confidence { get; set; }
        /// <summary>严重程度（0~1）</summary>
        public double Severity { get; set; }
        /// <summary>描述</summary>
        public string Description { get; set; } = string.Empty;
        /// <summary>优化建议</summary>
        public string Recommendation { get; set; } = string.Empty;
        /// <summary>相关指标值</summary>
        public Dictionary<string, double> Metrics { get; } = new();
    }

    /// <summary>
    /// 性能预算。设定目标帧率、目标内存等上限，
    /// 超标时触发报警。
    /// </summary>
    public sealed class PerformanceBudget
    {
        /// <summary>目标帧率</summary>
        public double TargetFPS { get; set; } = 60.0;
        /// <summary>目标帧时间（毫秒），由 TargetFPS 自动计算</summary>
        public double TargetFrameTimeMs => TargetFPS > 0 ? 1000.0 / TargetFPS : double.MaxValue;
        /// <summary>目标内存（字节）</summary>
        public long TargetMemoryBytes { get; set; } = 1024L * 1024 * 1024; // 1GB
        /// <summary>目标 GPU 时间（毫秒）</summary>
        public double TargetGpuTimeMs { get; set; } = 11.0;
        /// <summary>目标 DrawCall 数</summary>
        public int TargetDrawCalls { get; set; } = 3000;
        /// <summary>目标三角形数</summary>
        public ulong TargetTriangles { get; set; } = 5_000_000;
        /// <summary>FPS 警告阈值（低于此值报警）</summary>
        public double FPSWarningThreshold { get; set; } = 5.0;
        /// <summary>内存警告阈值（超出预算百分比报警）</summary>
        public double MemoryWarningThresholdPercent { get; set; } = 0.8;

        /// <summary>检查帧时间是否超标</summary>
        public bool IsFrameTimeOverBudget(double frameTimeMs) => frameTimeMs > TargetFrameTimeMs;

        /// <summary>检查内存是否超标</summary>
        public bool IsMemoryOverBudget(long memoryBytes) => memoryBytes > TargetMemoryBytes * MemoryWarningThresholdPercent;

        /// <summary>检查 GPU 时间是否超标</summary>
        public bool IsGpuTimeOverBudget(double gpuMs) => gpuMs > TargetGpuTimeMs;

        /// <summary>检查 DrawCall 是否超标</summary>
        public bool IsDrawCallsOverBudget(int drawCalls) => drawCalls > TargetDrawCalls;
    }

    /// <summary>
    /// 性能分析报告。汇总 CPU/GPU/内存/网络/音频的所有关键指标。
    /// </summary>

    /// <summary>
    /// 报告比较结果。
    /// </summary>
    public sealed class ReportComparison
    {
        public ProfilerReport ReportA { get; set; } = null!;
        public ProfilerReport ReportB { get; set; } = null!;

        /// <summary>帧率变化（正数 = 改善）</summary>
        public double FPSChange { get; set; }
        /// <summary>帧时间变化（正数 = 恶化）</summary>
        public double FrameTimeChangeMs { get; set; }
        /// <summary>内存变化（正数 = 增加）</summary>
        public long MemoryChangeBytes { get; set; }
        /// <summary>DrawCall 变化</summary>
        public int DrawCallChange { get; set; }
        /// <summary>GPU 时间变化</summary>
        public double GpuTimeChangeMs { get; set; }
        /// <summary>瓶颈变化描述</summary>
        public string BottleneckChanges { get; set; } = string.Empty;
        /// <summary>整体评估</summary>
        public string OverallAssessment { get; set; } = string.Empty;
    }

    /// <summary>
    /// 历史趋势数据点。
    /// </summary>
    public sealed class TrendDataPoint
    {
        public DateTime Timestamp { get; set; }
        public double AvgFPS { get; set; }
        public double AvgFrameTimeMs { get; set; }
        public long TotalMemoryMB { get; set; }
        public int DrawCalls { get; set; }
    }

    /// <summary>
    /// 分析报告系统。聚合各子分析器的数据，生成综合性能报告，
    /// 支持多格式导出、报告比较、自动瓶颈检测、性能预算与趋势分析。
    /// </summary>
    public sealed class ProfilerReportSystem
    {
        private readonly List<ProfilerReport> _reportHistory = new();
        private readonly List<TrendDataPoint> _trendHistory = new();
        private const int MaxHistory = 64;

        /// <summary>性能预算</summary>
        public PerformanceBudget Budget { get; } = new();

        /// <summary>事件：性能低于阈值时触发自动截图</summary>
        public event Action<ProfilerReport>? OnAutoScreenshotRequested;

        // ═══════════════════ 报告生成 ═══════════════════

        /// <summary>从各分析器数据生成综合报告</summary>
        public ProfilerReport GenerateReport(
            CPUProfiler? cpuProfiler = null,
            GPUProfiler? gpuProfiler = null,
            MemoryProfiler? memoryProfiler = null,
            NetworkProfiler? networkProfiler = null,
            AudioProfiler? audioProfiler = null)
        {
            var report = new ProfilerReport { Name = $"Report_{DateTime.UtcNow:yyyyMMdd_HHmmss}" };

            // CPU 数据
            if (cpuProfiler != null)
            {
                var fps = cpuProfiler.GetFPSStatistics();
                report.CurrentFPS = fps.CurrentFPS;
                report.AverageFPS = fps.AverageFPS;
                report.AvgFrameTimeMs = fps.AvgFrameTimeMs;
                report.MaxFrameTimeMs = fps.MaxFPS > 0 ? 1000.0 / fps.MinFPS : 0;
                report.MinFrameTimeMs = fps.MinFPS > 0 ? 1000.0 / fps.MaxFPS : 0;
                report.FPS_1Percent = fps.FPS_1Percent;
                report.FPS_0_1Percent = fps.FPS_0_1Percent;
                report.StdDevFrameTimeMs = fps.StdDevFrameTimeMs;
            }

            // GPU 数据
            if (gpuProfiler != null)
            {
                var overlay = gpuProfiler.GetOverlay();
                report.DrawCalls = overlay.DrawCalls;
                report.Triangles = overlay.Triangles;
                report.Vertices = overlay.Vertices;
                report.TotalGpuMs = overlay.TotalGpuMs;
                report.ShaderSwitches = overlay.ShaderSwitches;
                report.TextureMemoryMB = overlay.TextureMemoryMB;
                report.BufferMemoryMB = overlay.BufferMemoryMB;
                report.RenderPassCount = overlay.RenderPassCount;

                var bottleneck = gpuProfiler.DetectBottleneck();
                report.GPUBottleneckDescription = bottleneck.Description;
            }

            // 内存数据
            if (memoryProfiler != null)
            {
                var snapshot = memoryProfiler.CreateSnapshot();
                report.TotalMemoryBytes = snapshot.TotalBytes;
                report.ManagedMemoryBytes = snapshot.ManagedBytes;
                report.ProcessMemoryBytes = snapshot.ProcessBytes;
                report.ActiveAllocations = snapshot.ActiveAllocationCount;

                var leaks = memoryProfiler.DetectLeaks();
                report.LeakCount = leaks.Count;
                if (leaks.Count > 0)
                    report.MemoryLeakSummary = $"检测到 {leaks.Count} 处疑似泄漏";

                var catUsage = memoryProfiler.GetCategoryUsage();
                foreach (var kv in catUsage)
                    report.MemoryCategoryBreakdown[kv.Key.ToString()] = kv.Value;
            }

            // 网络数据
            if (networkProfiler != null)
            {
                var traffic = networkProfiler.GetTrafficStats();
                report.NetworkBytesSent = traffic.BytesSent;
                report.NetworkBytesReceived = traffic.BytesReceived;

                var latency = networkProfiler.GetLatencyStats();
                report.NetworkRTTMs = latency.CurrentRTTMs;
                report.NetworkPacketLoss = latency.PacketLossRate;
                report.NetworkQuality = networkProfiler.EvaluateQuality().ToString();
            }

            // 音频数据
            if (audioProfiler != null)
            {
                var src = audioProfiler.GetSourceStats();
                report.AudioActiveSources = src.ActiveSources;

                var cpu = audioProfiler.GetCPUStats();
                report.AudioTotalCpuMs = cpu.TotalMs;

                var mem = audioProfiler.GetMemoryStats();
                report.AudioMemoryBytes = mem.TotalBytes;

                report.AudioUnderrunCount = audioProfiler.GetBufferUnderrunCount();

                var bottleneck = audioProfiler.DetectBottleneck();
                report.AudioBottleneckDescription = bottleneck.Description;
            }

            // 自动瓶颈检测
            report.Bottlenecks = DetectBottlenecks(report);

            // 性能预算检查
            CheckBudget(report);

            // 自动截图检查
            CheckAutoScreenshot(report);

            // 保存到历史
            _reportHistory.Add(report);
            if (_reportHistory.Count > MaxHistory)
                _reportHistory.RemoveAt(0);

            return report;
        }

        // ═══════════════════ 瓶颈检测 ═══════════════════

        /// <summary>自动瓶颈检测</summary>
        public List<BottleneckDetection> DetectBottlenecks(ProfilerReport report)
        {
            var bottlenecks = new List<BottleneckDetection>();

            // CPU 瓶颈
            if (report.AvgFrameTimeMs > Budget.TargetFrameTimeMs && report.TotalGpuMs < report.AvgFrameTimeMs * 0.5)
            {
                bottlenecks.Add(new BottleneckDetection
                {
                    Type = BottleneckType.CPU,
                    Confidence = Math.Min(1.0, report.AvgFrameTimeMs / Budget.TargetFrameTimeMs - 0.5),
                    Severity = report.AvgFrameTimeMs > Budget.TargetFrameTimeMs * 2 ? 1.0 : 0.5,
                    Description = $"CPU 瓶颈: 平均帧时间 {report.AvgFrameTimeMs:F2}ms 超出目标 {Budget.TargetFrameTimeMs:F2}ms",
                    Recommendation = "优化游戏逻辑、减少物理迭代、降低 AI 计算复杂度"
                });
            }

            // GPU 瓶颈
            if (report.TotalGpuMs > Budget.TargetGpuTimeMs)
            {
                bottlenecks.Add(new BottleneckDetection
                {
                    Type = BottleneckType.GPU,
                    Confidence = Math.Min(1.0, report.TotalGpuMs / Budget.TargetGpuTimeMs - 0.3),
                    Severity = report.TotalGpuMs > Budget.TargetGpuTimeMs * 2 ? 1.0 : 0.5,
                    Description = $"GPU 瓶颈: GPU 耗时 {report.TotalGpuMs:F2}ms，{report.GPUBottleneckDescription}",
                    Recommendation = "降低分辨率、简化材质、启用 LOD、优化后处理"
                });
            }

            // 内存瓶颈
            if (report.TotalMemoryBytes > Budget.TargetMemoryBytes * Budget.MemoryWarningThresholdPercent)
            {
                bottlenecks.Add(new BottleneckDetection
                {
                    Type = BottleneckType.Memory,
                    Confidence = (double)report.TotalMemoryBytes / Budget.TargetMemoryBytes,
                    Severity = report.TotalMemoryBytes > Budget.TargetMemoryBytes ? 1.0 : 0.5,
                    Description = $"内存瓶颈: 使用 {FormatBytes(report.TotalMemoryBytes)} / 预算 {FormatBytes(Budget.TargetMemoryBytes)}",
                    Recommendation = "减少纹理分辨率、卸载未使用资源、启用流式加载"
                });
            }

            // 网络瓶颈
            if (report.NetworkPacketLoss > 0.05)
            {
                bottlenecks.Add(new BottleneckDetection
                {
                    Type = BottleneckType.Network,
                    Confidence = report.NetworkPacketLoss,
                    Severity = report.NetworkPacketLoss > 0.1 ? 1.0 : 0.5,
                    Description = $"网络瓶颈: 丢包率 {report.NetworkPacketLoss * 100:F1}%，RTT {report.NetworkRTTMs:F0}ms",
                    Recommendation = "启用网络压缩、减少同步频率、实现增量同步"
                });
            }

            // 音频瓶颈
            if (report.AudioUnderrunCount > 0)
            {
                bottlenecks.Add(new BottleneckDetection
                {
                    Type = BottleneckType.Audio,
                    Confidence = Math.Min(1.0, report.AudioUnderrunCount / 10.0),
                    Severity = report.AudioUnderrunCount > 10 ? 1.0 : 0.5,
                    Description = $"音频瓶颈: 缓冲区下溢 {report.AudioUnderrunCount} 次",
                    Recommendation = "增加流式缓冲区、预加载音频数据"
                });
            }

            return bottlenecks;
        }

        // ═══════════════════ 预算检查 ═══════════════════

        private void CheckBudget(ProfilerReport report)
        {
            report.BudgetChecks["FrameTime"] = Budget.IsFrameTimeOverBudget(report.AvgFrameTimeMs);
            report.BudgetChecks["Memory"] = Budget.IsMemoryOverBudget(report.TotalMemoryBytes);
            report.BudgetChecks["GpuTime"] = Budget.IsGpuTimeOverBudget(report.TotalGpuMs);
            report.BudgetChecks["DrawCalls"] = Budget.IsDrawCallsOverBudget(report.DrawCalls);

            if (report.BudgetChecks["FrameTime"])
                report.BudgetWarnings.Add($"帧时间超标: {report.AvgFrameTimeMs:F2}ms > {Budget.TargetFrameTimeMs:F2}ms");
            if (report.BudgetChecks["Memory"])
                report.BudgetWarnings.Add($"内存超标: {FormatBytes(report.TotalMemoryBytes)} > {FormatBytes(Budget.TargetMemoryBytes)}");
            if (report.BudgetChecks["GpuTime"])
                report.BudgetWarnings.Add($"GPU 时间超标: {report.TotalGpuMs:F2}ms > {Budget.TargetGpuTimeMs:F2}ms");
            if (report.BudgetChecks["DrawCalls"])
                report.BudgetWarnings.Add($"DrawCall 超标: {report.DrawCalls} > {Budget.TargetDrawCalls}");
        }

        private void CheckAutoScreenshot(ProfilerReport report)
        {
            if (report.AverageFPS > 0 && report.AverageFPS < Budget.TargetFPS - Budget.FPSWarningThreshold)
            {
                OnAutoScreenshotRequested?.Invoke(report);
            }
        }

        // ═══════════════════ 报告比较 ═══════════════════

        /// <summary>比较两份报告</summary>
        public ReportComparison CompareReports(ProfilerReport a, ProfilerReport b)
        {
            var comparison = new ReportComparison { ReportA = a, ReportB = b };

            comparison.FPSChange = b.AverageFPS - a.AverageFPS;
            comparison.FrameTimeChangeMs = b.AvgFrameTimeMs - a.AvgFrameTimeMs;
            comparison.MemoryChangeBytes = b.TotalMemoryBytes - a.TotalMemoryBytes;
            comparison.DrawCallChange = b.DrawCalls - a.DrawCalls;
            comparison.GpuTimeChangeMs = b.TotalGpuMs - a.TotalGpuMs;

            // 瓶颈变化
            var aBottlenecks = string.Join(", ", a.Bottlenecks.Select(bn => bn.Type.ToString()));
            var bBottlenecks = string.Join(", ", b.Bottlenecks.Select(bn => bn.Type.ToString()));
            comparison.BottleneckChanges = $"A: [{aBottlenecks}] -> B: [{bBottlenecks}]";

            // 整体评估
            int improvements = 0, regressions = 0;
            if (comparison.FPSChange > 0.5) improvements++;
            else if (comparison.FPSChange < -0.5) regressions++;
            if (comparison.FrameTimeChangeMs < -0.5) improvements++;
            else if (comparison.FrameTimeChangeMs > 0.5) regressions++;
            if (comparison.MemoryChangeBytes < -1024 * 1024) improvements++;
            else if (comparison.MemoryChangeBytes > 1024 * 1024) regressions++;

            comparison.OverallAssessment = improvements > regressions
                ? $"性能改善（{improvements}项改善 / {regressions}项恶化）"
                : regressions > improvements
                    ? $"性能恶化（{regressions}项恶化 / {improvements}项改善）"
                    : "性能基本持平";

            return comparison;
        }

        // ═══════════════════ 趋势分析 ═══════════════════

        /// <summary>记录趋势数据点</summary>
        public void RecordTrend(ProfilerReport report)
        {
            var point = new TrendDataPoint
            {
                Timestamp = report.GeneratedAt,
                AvgFPS = report.AverageFPS,
                AvgFrameTimeMs = report.AvgFrameTimeMs,
                TotalMemoryMB = report.TotalMemoryBytes / (1024 * 1024),
                DrawCalls = report.DrawCalls
            };
            _trendHistory.Add(point);
            if (_trendHistory.Count > MaxHistory)
                _trendHistory.RemoveAt(0);
        }

        /// <summary>获取趋势历史</summary>
        public IReadOnlyList<TrendDataPoint> GetTrendHistory() => _trendHistory;

        /// <summary>分析趋势（FPS 是否在下降，内存在上升等）</summary>
        public string AnalyzeTrends()
        {
            if (_trendHistory.Count < 3) return "数据不足，至少需要 3 个数据点";

            var sb = new StringBuilder();
            var recent = _trendHistory.TakeLast(5).ToList();
            if (recent.Count < 2) return "数据不足";

            // FPS 趋势
            double fpsFirst = recent[0].AvgFPS;
            double fpsLast = recent[recent.Count - 1].AvgFPS;
            if (fpsLast < fpsFirst * 0.9)
                sb.AppendLine($"FPS 下降趋势: {fpsFirst:F1} -> {fpsLast:F1} ({(fpsLast - fpsFirst) / fpsFirst * 100:F1}%)");
            else if (fpsLast > fpsFirst * 1.1)
                sb.AppendLine($"FPS 上升趋势: {fpsFirst:F1} -> {fpsLast:F1}");

            // 内存趋势
            long memFirst = recent[0].TotalMemoryMB;
            long memLast = recent[recent.Count - 1].TotalMemoryMB;
            if (memLast > memFirst * 1.1)
                sb.AppendLine($"内存上升趋势: {memFirst}MB -> {memLast}MB ({(memLast - memFirst) * 100.0 / memFirst:F1}%)");
            else if (memLast < memFirst * 0.9)
                sb.AppendLine($"内存下降趋势: {memFirst}MB -> {memLast}MB");

            if (sb.Length == 0) sb.AppendLine("各项指标稳定，无明显趋势");

            return sb.ToString();
        }

        // ═══════════════════ 格式导出 ═══════════════════

        /// <summary>导出为 JSON</summary>
        public string ExportJSON(ProfilerReport report)
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
            var data = new Dictionary<string, object>
            {
                ["reportId"] = report.ReportId.ToString(),
                ["name"] = report.Name,
                ["generatedAt"] = report.GeneratedAt.ToString("o"),
                ["durationMs"] = report.DurationMs,
                ["frameCount"] = report.FrameCount,
                ["cpu"] = new Dictionary<string, object>
                {
                    ["avgFrameTimeMs"] = report.AvgFrameTimeMs,
                    ["maxFrameTimeMs"] = report.MaxFrameTimeMs,
                    ["currentFPS"] = report.CurrentFPS,
                    ["averageFPS"] = report.AverageFPS,
                    ["fps_1Percent"] = report.FPS_1Percent,
                    ["fps_0_1Percent"] = report.FPS_0_1Percent,
                    ["stdDevFrameTimeMs"] = report.StdDevFrameTimeMs
                },
                ["gpu"] = new Dictionary<string, object>
                {
                    ["totalGpuMs"] = report.TotalGpuMs,
                    ["drawCalls"] = report.DrawCalls,
                    ["triangles"] = report.Triangles,
                    ["vertices"] = report.Vertices,
                    ["shaderSwitches"] = report.ShaderSwitches,
                    ["textureMemoryMB"] = report.TextureMemoryMB,
                    ["bufferMemoryMB"] = report.BufferMemoryMB,
                    ["renderPassCount"] = report.RenderPassCount,
                    ["bottleneck"] = report.GPUBottleneckDescription
                },
                ["memory"] = new Dictionary<string, object>
                {
                    ["totalBytes"] = report.TotalMemoryBytes,
                    ["managedBytes"] = report.ManagedMemoryBytes,
                    ["processBytes"] = report.ProcessMemoryBytes,
                    ["activeAllocations"] = report.ActiveAllocations,
                    ["leakCount"] = report.LeakCount,
                    ["categoryBreakdown"] = report.MemoryCategoryBreakdown
                },
                ["network"] = new Dictionary<string, object>
                {
                    ["bytesSent"] = report.NetworkBytesSent,
                    ["bytesReceived"] = report.NetworkBytesReceived,
                    ["rttMs"] = report.NetworkRTTMs,
                    ["packetLoss"] = report.NetworkPacketLoss,
                    ["quality"] = report.NetworkQuality
                },
                ["audio"] = new Dictionary<string, object>
                {
                    ["activeSources"] = report.AudioActiveSources,
                    ["totalCpuMs"] = report.AudioTotalCpuMs,
                    ["memoryBytes"] = report.AudioMemoryBytes,
                    ["underrunCount"] = report.AudioUnderrunCount
                },
                ["bottlenecks"] = report.Bottlenecks.Select(b => new Dictionary<string, object>
                {
                    ["type"] = b.Type.ToString(),
                    ["confidence"] = b.Confidence,
                    ["severity"] = b.Severity,
                    ["description"] = b.Description,
                    ["recommendation"] = b.Recommendation
                }).ToList(),
                ["budgetWarnings"] = report.BudgetWarnings
            };
            return JsonSerializer.Serialize(data, options);
        }

        /// <summary>导出为 HTML 报告</summary>
        public string ExportHTML(ProfilerReport report)
        {
            var sb = new StringBuilder();
            sb.AppendLine("<!DOCTYPE html><html><head><meta charset='utf-8'>");
            sb.AppendLine("<title>Yama Engine - Performance Report</title>");
            sb.AppendLine("<style>");
            sb.AppendLine("body{font-family:'Segoe UI',sans-serif;margin:20px;background:#1a1a2e;color:#eee}");
            sb.AppendLine(".section{background:#16213e;border-radius:8px;padding:16px;margin:12px 0}");
            sb.AppendLine("h2{color:#0f3460;border-bottom:2px solid #0f3460;padding-bottom:4px}");
            sb.AppendLine(".good{color:#4ecca3}.bad{color:#e94560}.warn{color:#f0a500}");
            sb.AppendLine("table{border-collapse:collapse;width:100%}td,th{padding:6px 12px;text-align:left;border-bottom:1px solid #333}");
            sb.AppendLine(".metric{font-size:1.2em;font-weight:bold}");
            sb.AppendLine("</style></head><body>");
            sb.AppendLine($"<h1>Yama Engine Performance Report</h1>");
            sb.AppendLine($"<p>Generated: {report.GeneratedAt:yyyy-MM-dd HH:mm:ss} | Duration: {report.DurationMs / 1000:F1}s | Frames: {report.FrameNumber}</p>");

            // CPU
            sb.AppendLine("<div class='section'><h2>CPU Performance</h2>");
            sb.AppendLine("<table>");
            sb.AppendLine($"<tr><td class='metric'>Average FPS</td><td class='{(report.AverageFPS >= 55 ? "good" : "bad")}'>{report.AverageFPS:F1}</td></tr>");
            sb.AppendLine($"<tr><td>Avg Frame Time</td><td>{report.AvgFrameTimeMs:F2} ms</td></tr>");
            sb.AppendLine($"<tr><td>1% Low FPS</td><td>{report.FPS_1Percent:F1}</td></tr>");
            sb.AppendLine($"<tr><td>0.1% Low FPS</td><td>{report.FPS_0_1Percent:F1}</td></tr>");
            sb.AppendLine($"<tr><td>Std Dev</td><td>{report.StdDevFrameTimeMs:F3} ms</td></tr>");
            sb.AppendLine("</table></div>");

            // GPU
            sb.AppendLine("<div class='section'><h2>GPU Performance</h2>");
            sb.AppendLine("<table>");
            sb.AppendLine($"<tr><td>GPU Time</td><td>{report.TotalGpuMs:F2} ms</td></tr>");
            sb.AppendLine($"<tr><td>Draw Calls</td><td>{report.DrawCalls:N0}</td></tr>");
            sb.AppendLine($"<tr><td>Triangles</td><td>{report.Triangles:N0}</td></tr>");
            sb.AppendLine($"<tr><td>Shader Switches</td><td>{report.ShaderSwitches}</td></tr>");
            sb.AppendLine($"<tr><td>Texture Memory</td><td>{report.TextureMemoryMB:F1} MB</td></tr>");
            sb.AppendLine($"<tr><td>GPU Bottleneck</td><td class='warn'>{report.GPUBottleneckDescription}</td></tr>");
            sb.AppendLine("</table></div>");

            // Memory
            sb.AppendLine("<div class='section'><h2>Memory</h2>");
            sb.AppendLine("<table>");
            sb.AppendLine($"<tr><td>Total Memory</td><td>{FormatBytes(report.TotalMemoryBytes)}</td></tr>");
            sb.AppendLine($"<tr><td>Managed Memory</td><td>{FormatBytes(report.ManagedMemoryBytes)}</td></tr>");
            sb.AppendLine($"<tr><td>Active Allocations</td><td>{report.ActiveAllocations}</td></tr>");
            if (report.LeakCount > 0)
                sb.AppendLine($"<tr><td>Leak Count</td><td class='bad'>{report.LeakCount}</td></tr>");
            sb.AppendLine("</table></div>");

            // Bottlenecks
            if (report.Bottlenecks.Count > 0)
            {
                sb.AppendLine("<div class='section'><h2>Bottlenecks Detected</h2>");
                foreach (var b in report.Bottlenecks)
                {
                    sb.AppendLine($"<p class='bad'><strong>{b.Type}</strong>: {b.Description}</p>");
                    sb.AppendLine($"<p>Recommendation: {b.Recommendation}</p>");
                }
                sb.AppendLine("</div>");
            }

            // Budget
            if (report.BudgetWarnings.Count > 0)
            {
                sb.AppendLine("<div class='section'><h2>Budget Warnings</h2>");
                foreach (var w in report.BudgetWarnings)
                    sb.AppendLine($"<p class='warn'>{w}</p>");
                sb.AppendLine("</div>");
            }

            sb.AppendLine("</body></html>");
            return sb.ToString();
        }

        /// <summary>导出为 CSV</summary>
        public string ExportCSV(ProfilerReport report)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Category,Metric,Value,Unit");
            sb.AppendLine($"CPU,AverageFPS,{report.AverageFPS.ToString(CultureInfo.InvariantCulture)},fps");
            sb.AppendLine($"CPU,AvgFrameTime,{report.AvgFrameTimeMs.ToString(CultureInfo.InvariantCulture)},ms");
            sb.AppendLine($"CPU,FPS_1Percent,{report.FPS_1Percent.ToString(CultureInfo.InvariantCulture)},fps");
            sb.AppendLine($"CPU,FPS_0_1Percent,{report.FPS_0_1Percent.ToString(CultureInfo.InvariantCulture)},fps");
            sb.AppendLine($"CPU,StdDevFrameTime,{report.StdDevFrameTimeMs.ToString(CultureInfo.InvariantCulture)},ms");
            sb.AppendLine($"GPU,TotalGpuTime,{report.TotalGpuMs.ToString(CultureInfo.InvariantCulture)},ms");
            sb.AppendLine($"GPU,DrawCalls,{report.DrawCalls},calls");
            sb.AppendLine($"GPU,Triangles,{report.Triangles},triangles");
            sb.AppendLine($"GPU,ShaderSwitches,{report.ShaderSwitches},switches");
            sb.AppendLine($"GPU,TextureMemoryMB,{report.TextureMemoryMB.ToString(CultureInfo.InvariantCulture)},MB");
            sb.AppendLine($"Memory,TotalBytes,{report.TotalMemoryBytes},bytes");
            sb.AppendLine($"Memory,ManagedBytes,{report.ManagedMemoryBytes},bytes");
            sb.AppendLine($"Memory,ActiveAllocations,{report.ActiveAllocations},allocations");
            sb.AppendLine($"Memory,LeakCount,{report.LeakCount},leaks");
            sb.AppendLine($"Network,BytesSent,{report.NetworkBytesSent},bytes");
            sb.AppendLine($"Network,BytesReceived,{report.NetworkBytesReceived},bytes");
            sb.AppendLine($"Network,RTTMs,{report.NetworkRTTMs.ToString(CultureInfo.InvariantCulture)},ms");
            sb.AppendLine($"Network,PacketLoss,{report.NetworkPacketLoss.ToString(CultureInfo.InvariantCulture)},rate");
            sb.AppendLine($"Audio,ActiveSources,{report.AudioActiveSources},sources");
            sb.AppendLine($"Audio,TotalCpuMs,{report.AudioTotalCpuMs.ToString(CultureInfo.InvariantCulture)},ms");
            sb.AppendLine($"Audio,UnderrunCount,{report.AudioUnderrunCount},underruns");
            return sb.ToString();
        }

        /// <summary>导出报告到文件</summary>
        public void ExportToFile(ProfilerReport report, string path, string format = "json")
        {
            string content = format.ToLowerInvariant() switch
            {
                "html" => ExportHTML(report),
                "csv" => ExportCSV(report),
                _ => ExportJSON(report)
            };
            File.WriteAllText(path, content, Encoding.UTF8);
        }

        /// <summary>获取报告历史</summary>
        public IReadOnlyList<ProfilerReport> GetReportHistory() => _reportHistory;

        // ── 工具 ──
        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
            return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
        }
    }
}
