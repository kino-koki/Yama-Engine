using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace YamaEngine.Build
{
    // ════════════════════════════════════════════════════════════════════════
    // yama引擎构建性能分析系统 (BuildProfilerSystem)
    //
    // 追踪构建过程中的每个任务耗时、内存占用、CPU 与 IO 等待，
    // 识别构建瓶颈，生成趋势对比报告 (JSON/HTML)。
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 单个构建任务的性能采样。
    /// </summary>
    public sealed class BuildTaskProfile
    {
        /// <summary>任务名称。</summary>
        public string TaskName { get; set; } = string.Empty;

        /// <summary>任务类型 (Cook/Package/Deploy/Compile)。</summary>
        public string TaskType { get; set; } = "Unknown";

        /// <summary>资产路径。</summary>
        public string AssetPath { get; set; } = string.Empty;

        /// <summary>线程 ID。</summary>
        public int ThreadId { get; set; }

        /// <summary>开始时间 (UTC)。</summary>
        public DateTime StartTimeUtc { get; set; }

        /// <summary>结束时间 (UTC)。</summary>
        public DateTime EndTimeUtc { get; set; }

        /// <summary>总耗时 (毫秒)。</summary>
        public double DurationMs => (EndTimeUtc - StartTimeUtc).TotalMilliseconds;

        /// <summary>CPU 时间 (毫秒，用户态+内核态)。</summary>
        public double CpuTimeMs { get; set; }

        /// <summary>IO 等待时间 (毫秒)。</summary>
        public double IoWaitMs { get; set; }

        /// <summary>峰值内存增量 (字节)。</summary>
        public long PeakMemoryDeltaBytes { get; set; }

        /// <summary>输入大小 (字节)。</summary>
        public long InputSizeBytes { get; set; }

        /// <summary>输出大小 (字节)。</summary>
        public long OutputSizeBytes { get; set; }

        /// <summary>是否成功。</summary>
        public bool Success { get; set; }

        /// <summary>错误信息。</summary>
        public string? Error { get; set; }

        /// <summary>自定义标签。</summary>
        public Dictionary<string, string> Tags { get; set; } = new();
    }

    /// <summary>
    /// 完整构建性能 Profile 数据。
    /// </summary>
    public sealed class BuildProfileData
    {
        /// <summary>Profile ID。</summary>
        public string ProfileId { get; set; } = Guid.NewGuid().ToString("N").Substring(0, 8);

        /// <summary>项目名称。</summary>
        public string ProjectName { get; set; } = string.Empty;

        /// <summary>目标平台。</summary>
        public string Platform { get; set; } = string.Empty;

        /// <summary>构建配置。</summary>
        public string BuildConfig { get; set; } = string.Empty;

        /// <summary>Profile 开始时间 (UTC)。</summary>
        public DateTime StartTimeUtc { get; set; }

        /// <summary>Profile 结束时间 (UTC)。</summary>
        public DateTime EndTimeUtc { get; set; }

        /// <summary>总耗时 (毫秒)。</summary>
        public double TotalDurationMs => (EndTimeUtc - StartTimeUtc).TotalMilliseconds;

        /// <summary>各阶段耗时 (毫秒)。</summary>
        public Dictionary<string, double> PhaseTimingsMs { get; set; } = new();

        /// <summary>所有任务 Profile 列表。</summary>
        public List<BuildTaskProfile> Tasks { get; set; } = new();

        /// <summary>总任务数。</summary>
        public int TotalTaskCount => Tasks.Count;

        /// <summary>成功任务数。</summary>
        public int SuccessCount => Tasks.Count(t => t.Success);

        /// <summary>失败任务数。</summary>
        public int FailedCount => Tasks.Count(t => !t.Success);

        /// <summary>峰值进程内存 (字节)。</summary>
        public long PeakProcessMemoryBytes { get; set; }

        /// <summary>峰值 GC 内存 (字节)。</summary>
        public long PeakGcMemoryBytes { get; set; }

        /// <summary>CPU 核心数。</summary>
        public int CpuCoreCount { get; set; }

        /// <summary>CPU 利用率 (0.0 ~ 1.0，估算)。</summary>
        public double CpuUtilization => TotalDurationMs > 0
            ? Math.Min(1.0, Tasks.Sum(t => t.CpuTimeMs) / (TotalDurationMs * CpuCoreCount))
            : 0.0;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 瓶颈分析
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 构建瓶颈报告。
    /// </summary>
    public sealed class BuildBottleneckReport
    {
        /// <summary>最慢的 N 个任务（按耗时降序）。</summary>
        public List<BuildTaskProfile> SlowestTasks { get; set; } = new();

        /// <summary>最慢的阶段（按阶段名分组求和）。</summary>
        public List<(string Phase, double TotalMs)> SlowestPhases { get; set; } = new();

        /// <summary>IO 等待最严重的任务。</summary>
        public List<BuildTaskProfile> IoHeavyTasks { get; set; } = new();

        /// <summary>内存峰值最高的任务。</summary>
        public List<BuildTaskProfile> MemoryHeavyTasks { get; set; } = new();

        /// <summary>CPU 利用率最低的任务（可能是 IO 瓶颈）。</summary>
        public List<BuildTaskProfile> CpuIdleTasks { get; set; } = new();

        /// <summary>瓶颈建议。</summary>
        public List<string> Recommendations { get; set; } = new();
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 构建时间历史
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 单次构建的时间记录。
    /// </summary>
    public sealed class BuildTimeRecord
    {
        public DateTime TimestampUtc { get; set; }
        public string ProjectName { get; set; } = string.Empty;
        public string Platform { get; set; } = string.Empty;
        public string BuildConfig { get; set; } = string.Empty;
        public double TotalDurationMs { get; set; }
        public int AssetCount { get; set; }
        public bool Success { get; set; }
        public Dictionary<string, double> PhaseTimingsMs { get; set; } = new();
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 构建分析器
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// yama引擎构建性能分析器。追踪构建任务、识别瓶颈、记录历史趋势、生成报告。
    /// </summary>
    public sealed class BuildProfiler
    {
        private readonly List<BuildTimeRecord> _history = new();
        private readonly string _historyDirectory;
        private BuildProfileData? _currentProfile;
        private readonly List<Stopwatch> _phaseStopwatches = new();
        private long _peakMemoryBytes;
        private readonly object _lock = new();

        public BuildProfiler(string? historyDirectory = null)
        {
            _historyDirectory = historyDirectory ?? Path.Combine(Path.GetTempPath(), "YamaBuildProfiles");
            Directory.CreateDirectory(_historyDirectory);
            LoadHistory();
        }

        /// <summary>开始一次构建 Profile。</summary>
        public BuildProfileData BeginProfile(string projectName, string platform, string buildConfig)
        {
            _currentProfile = new BuildProfileData
            {
                ProjectName = projectName,
                Platform = platform,
                BuildConfig = buildConfig,
                StartTimeUtc = DateTime.UtcNow,
                CpuCoreCount = Environment.ProcessorCount
            };
            _peakMemoryBytes = GC.GetTotalMemory(true);
            return _currentProfile;
        }

        /// <summary>结束当前构建 Profile。</summary>
        public BuildProfileData EndProfile()
        {
            if (_currentProfile == null) throw new InvalidOperationException("No active profile.");
            _currentProfile.EndTimeUtc = DateTime.UtcNow;
            _currentProfile.PeakProcessMemoryBytes = GC.GetTotalMemory(false);
            _currentProfile.PeakGcMemoryBytes = _peakMemoryBytes;

            // 保存到历史
            lock (_lock)
            {
                _history.Add(new BuildTimeRecord
                {
                    TimestampUtc = _currentProfile.StartTimeUtc,
                    ProjectName = _currentProfile.ProjectName,
                    Platform = _currentProfile.Platform,
                    BuildConfig = _currentProfile.BuildConfig,
                    TotalDurationMs = _currentProfile.TotalDurationMs,
                    AssetCount = _currentProfile.TotalTaskCount,
                    Success = _currentProfile.FailedCount == 0,
                    PhaseTimingsMs = _currentProfile.PhaseTimingsMs
                });

                if (_history.Count > 100)
                    _history.RemoveRange(0, _history.Count - 100);
            }

            SaveHistory();
            var result = _currentProfile;
            _currentProfile = null;
            return result;
        }

        /// <summary>记录单个构建任务。</summary>
        public void RecordTask(BuildTaskProfile task)
        {
            _currentProfile?.Tasks.Add(task);
            UpdatePeakMemory();
        }

        /// <summary>开始计时构建阶段。</summary>
        public Stopwatch BeginPhase(string phaseName)
        {
            var sw = Stopwatch.StartNew();
            return sw;
        }

        /// <summary>结束计时构建阶段并记录。</summary>
        public void EndPhase(string phaseName, Stopwatch sw)
        {
            sw.Stop();
            double existing = 0;
            if (_currentProfile != null)
                _currentProfile.PhaseTimingsMs.TryGetValue(phaseName, out existing);
            _currentProfile?.PhaseTimingsMs.Remove(phaseName);
            _currentProfile?.PhaseTimingsMs.Add(phaseName, existing + sw.Elapsed.TotalMilliseconds);
        }

        /// <summary>分析瓶颈。</summary>
        public BuildBottleneckReport AnalyzeBottlenecks(BuildProfileData profile)
        {
            var report = new BuildBottleneckReport();

            // 最慢的 10 个任务
            report.SlowestTasks = profile.Tasks
                .OrderByDescending(t => t.DurationMs)
                .Take(10)
                .ToList();

            // 最慢的阶段
            report.SlowestPhases = profile.PhaseTimingsMs
                .OrderByDescending(kv => kv.Value)
                .Select(kv => (kv.Key, kv.Value))
                .ToList();

            // IO 重任务
            report.IoHeavyTasks = profile.Tasks
                .Where(t => t.IoWaitMs > 0)
                .OrderByDescending(t => t.IoWaitMs / Math.Max(1, t.DurationMs))
                .Take(10)
                .ToList();

            // 内存重任务
            report.MemoryHeavyTasks = profile.Tasks
                .OrderByDescending(t => t.PeakMemoryDeltaBytes)
                .Take(10)
                .ToList();

            // CPU 空闲任务（CPU 利用率低）
            report.CpuIdleTasks = profile.Tasks
                .Where(t => t.DurationMs > 100)
                .OrderBy(t => t.CpuTimeMs / Math.Max(1, t.DurationMs))
                .Take(10)
                .ToList();

            // 生成建议
            GenerateRecommendations(profile, report);

            return report;
        }

        private void GenerateRecommendations(BuildProfileData profile, BuildBottleneckReport report)
        {
            // IO 瓶颈检测
            var totalIoWait = profile.Tasks.Sum(t => t.IoWaitMs);
            if (totalIoWait > profile.TotalDurationMs * 0.3)
            {
                report.Recommendations.Add("IO wait 占比较高 (>30%)。建议使用 SSD 或增加内存缓存。");
            }

            // CPU 利用率低
            if (profile.CpuUtilization < 0.5 && profile.CpuCoreCount > 1)
            {
                report.Recommendations.Add($"CPU 利用率较低 ({profile.CpuUtilization:P0})。建议增加并行 Worker 数量。");
            }

            // 内存峰值
            if (profile.PeakProcessMemoryBytes > 2 * 1024 * 1024 * 1024L) // >2GB
            {
                report.Recommendations.Add($"内存峰值较高 ({FormatSize(profile.PeakProcessMemoryBytes)})。建议分批处理大型资产。");
            }

            // 最慢阶段建议
            if (report.SlowestPhases.Count > 0)
            {
                var slowest = report.SlowestPhases[0];
                report.Recommendations.Add($"最慢阶段: {slowest.Phase} ({slowest.TotalMs:F0}ms, {slowest.TotalMs / profile.TotalDurationMs * 100:F1}%)");
            }

            if (report.SlowestTasks.Count > 0)
            {
                var top = report.SlowestTasks[0];
                report.Recommendations.Add($"最慢资产: {top.AssetPath ?? top.TaskName} ({top.DurationMs:F0}ms)");
            }
        }

        /// <summary>获取构建时间趋势（最近 N 次）。</summary>
        public List<BuildTimeRecord> GetTimeTrend(int lastN = 20)
        {
            lock (_lock)
            {
                return _history.Skip(Math.Max(0, _history.Count - lastN)).ToList();
            }
        }

        /// <summary>对比两次构建的各阶段耗时。</summary>
        public Dictionary<string, (double OldMs, double NewMs, double DeltaMs, double DeltaPercent)> CompareBuilds(
            BuildTimeRecord oldBuild, BuildTimeRecord newBuild)
        {
            var result = new Dictionary<string, (double, double, double, double)>();

            var allPhases = oldBuild.PhaseTimingsMs.Keys.Union(newBuild.PhaseTimingsMs.Keys);
            foreach (var phase in allPhases)
            {
                var oldMs = oldBuild.PhaseTimingsMs.GetValueOrDefault(phase, 0);
                var newMs = newBuild.PhaseTimingsMs.GetValueOrDefault(phase, 0);
                var delta = newMs - oldMs;
                var percent = oldMs > 0 ? (delta / oldMs) * 100.0 : (newMs > 0 ? 100.0 : 0.0);
                result[phase] = (oldMs, newMs, delta, percent);
            }

            return result;
        }

        /// <summary>生成 JSON 报告。</summary>
        public string GenerateJsonReport(BuildProfileData profile, BuildBottleneckReport? bottleneck = null)
        {
            var report = new
            {
                ProfileId = profile.ProfileId,
                ProjectName = profile.ProjectName,
                Platform = profile.Platform,
                BuildConfig = profile.BuildConfig,
                TotalDurationMs = profile.TotalDurationMs,
                StartTimeUtc = profile.StartTimeUtc,
                EndTimeUtc = profile.EndTimeUtc,
                TotalTasks = profile.TotalTaskCount,
                SuccessCount = profile.SuccessCount,
                FailedCount = profile.FailedCount,
                PeakMemoryBytes = profile.PeakProcessMemoryBytes,
                CpuUtilization = profile.CpuUtilization,
                PhaseTimingsMs = profile.PhaseTimingsMs,
                SlowestTasks = profile.Tasks.OrderByDescending(t => t.DurationMs).Take(20)
                    .Select(t => new { t.TaskName, t.AssetPath, t.DurationMs, t.IoWaitMs, t.PeakMemoryDeltaBytes, t.Success }),
                Bottleneck = bottleneck != null ? new
                {
                    bottleneck.Recommendations,
                    SlowestPhases = bottleneck.SlowestPhases.Select(p => new { Phase = p.Phase, TotalMs = p.TotalMs })
                } : null
            };

            return JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        }

        /// <summary>生成 HTML 报告。</summary>
        public string GenerateHtmlReport(BuildProfileData profile, BuildBottleneckReport? bottleneck = null)
        {
            var sb = new StringBuilder();
            sb.AppendLine("<!DOCTYPE html><html><head><meta charset='UTF-8'>");
            sb.AppendLine("<style>body{font-family:sans-serif;margin:20px}table{border-collapse:collapse;width:100%}th,td{border:1px solid #ddd;padding:8px;text-align:left}th{background:#f5f5f5}.slow{color:red}.ok{color:green}</style>");
            sb.AppendLine("</head><body>");
            sb.AppendLine($"<h1>Build Report: {profile.ProjectName}</h1>");
            sb.AppendLine($"<p>Platform: <b>{profile.Platform}</b> | Config: <b>{profile.BuildConfig}</b> | Duration: <b>{profile.TotalDurationMs / 1000:F1}s</b></p>");
            sb.AppendLine($"<p>Tasks: {profile.TotalTaskCount} ({profile.SuccessCount} ok, {profile.FailedCount} failed) | Peak Memory: {FormatSize(profile.PeakProcessMemoryBytes)} | CPU Util: {profile.CpuUtilization:P0}</p>");

            // 阶段耗时表
            sb.AppendLine("<h2>Phase Timings</h2><table><tr><th>Phase</th><th>Duration (ms)</th><th>%</th></tr>");
            foreach (var (phase, ms) in profile.PhaseTimingsMs.OrderByDescending(kv => kv.Value))
            {
                var percent = profile.TotalDurationMs > 0 ? ms / profile.TotalDurationMs * 100 : 0;
                sb.AppendLine($"<tr><td>{phase}</td><td>{ms:F0}</td><td>{percent:F1}%</td></tr>");
            }
            sb.AppendLine("</table>");

            // 最慢任务
            var slowest = profile.Tasks.OrderByDescending(t => t.DurationMs).Take(20);
            sb.AppendLine("<h2>Slowest Tasks</h2><table><tr><th>Task</th><th>Asset</th><th>Duration (ms)</th><th>IO Wait (ms)</th><th>Status</th></tr>");
            foreach (var t in slowest)
            {
                var cls = t.DurationMs > profile.TotalDurationMs * 0.1 ? "slow" : "ok";
                sb.AppendLine($"<tr class='{cls}'><td>{t.TaskName}</td><td>{t.AssetPath}</td><td>{t.DurationMs:F0}</td><td>{t.IoWaitMs:F0}</td><td>{(t.Success ? "OK" : "FAIL")}</td></tr>");
            }
            sb.AppendLine("</table>");

            // 瓶颈建议
            if (bottleneck != null && bottleneck.Recommendations.Count > 0)
            {
                sb.AppendLine("<h2>Recommendations</h2><ul>");
                foreach (var rec in bottleneck.Recommendations)
                    sb.AppendLine($"<li>{rec}</li>");
                sb.AppendLine("</ul>");
            }

            sb.AppendLine("</body></html>");
            return sb.ToString();
        }

        /// <summary>保存报告到文件。</summary>
        public void SaveReport(BuildProfileData profile, string? directory = null)
        {
            var dir = directory ?? _historyDirectory;
            Directory.CreateDirectory(dir);

            var timestamp = profile.StartTimeUtc.ToString("yyyyMMdd_HHmmss");
            var bottleneck = AnalyzeBottlenecks(profile);

            var jsonReport = GenerateJsonReport(profile, bottleneck);
            File.WriteAllText(Path.Combine(dir, $"build_report_{timestamp}.json"), jsonReport, Encoding.UTF8);

            var htmlReport = GenerateHtmlReport(profile, bottleneck);
            File.WriteAllText(Path.Combine(dir, $"build_report_{timestamp}.html"), htmlReport, Encoding.UTF8);
        }

        // ── 内部方法 ──

        private void UpdatePeakMemory()
        {
            var current = GC.GetTotalMemory(true);
            if (current > _peakMemoryBytes)
                _peakMemoryBytes = current;
        }

        private void LoadHistory()
        {
            if (!Directory.Exists(_historyDirectory)) return;
            foreach (var file in Directory.EnumerateFiles(_historyDirectory, "build_history_*.json"))
            {
                try
                {
                    var json = File.ReadAllText(file, Encoding.UTF8);
                    var records = JsonSerializer.Deserialize<List<BuildTimeRecord>>(json);
                    if (records != null) _history.AddRange(records);
                }
                catch { }
            }
            _history.Sort((a, b) => a.TimestampUtc.CompareTo(b.TimestampUtc));
        }

        private void SaveHistory()
        {
            lock (_lock)
            {
                var path = Path.Combine(_historyDirectory, $"build_history_{DateTime.UtcNow:yyyyMMdd}.json");
                var json = JsonSerializer.Serialize(_history, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(path, json, Encoding.UTF8);
            }
        }

        private static string FormatSize(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
            return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
        }
    }
}
