using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace ShanyuEngine.Diagnostics
{
    public readonly struct AdvancedProfileSummary
    {
        public readonly int Count;
        public readonly double AverageMs;
        public readonly double MinMs;
        public readonly double MaxMs;
        public readonly double P50Ms;
        public readonly double P95Ms;

        public AdvancedProfileSummary(int count, double averageMs, double minMs, double maxMs, double p50Ms, double p95Ms)
        {
            Count = count;
            AverageMs = averageMs;
            MinMs = minMs;
            MaxMs = maxMs;
            P50Ms = p50Ms;
            P95Ms = p95Ms;
        }
    }

    public sealed class AdvancedFrameProfiler
    {
        private readonly Dictionary<string, List<double>> _samples = new(StringComparer.OrdinalIgnoreCase);

        public void Record(string marker, double milliseconds)
        {
            if (!_samples.TryGetValue(marker, out var values))
                _samples[marker] = values = new List<double>();
            values.Add(Math.Max(0, milliseconds));
        }

        public AdvancedProfileSummary Summarize(string marker)
        {
            if (!_samples.TryGetValue(marker, out var values) || values.Count == 0)
                return new AdvancedProfileSummary(0, 0, 0, 0, 0, 0);
            var ordered = values.OrderBy(v => v).ToArray();
            return new AdvancedProfileSummary(
                ordered.Length,
                ordered.Average(),
                ordered[0],
                ordered[^1],
                Percentile(ordered, 0.50),
                Percentile(ordered, 0.95));
        }

        private static double Percentile(double[] ordered, double percentile)
        {
            if (ordered.Length == 1) return ordered[0];
            var rank = Math.Ceiling(percentile * ordered.Length) - 1;
            var index = Math.Clamp((int)rank, 0, ordered.Length - 1);
            return ordered[index];
        }
    }

    public readonly struct AdvancedLogEntry
    {
        public readonly DateTime TimestampUtc;
        public readonly string Level;
        public readonly string Category;
        public readonly string Message;

        public AdvancedLogEntry(DateTime timestampUtc, string level, string category, string message)
        {
            TimestampUtc = timestampUtc;
            Level = level;
            Category = category;
            Message = message;
        }
    }

    public interface IAdvancedLogSink
    {
        void Write(AdvancedLogEntry entry);
    }

    public sealed class InMemoryAdvancedLogSink : IAdvancedLogSink
    {
        public List<AdvancedLogEntry> Entries { get; } = new();
        public void Write(AdvancedLogEntry entry) => Entries.Add(entry);
    }

    public sealed class AdvancedStructuredLogger
    {
        private readonly List<IAdvancedLogSink> _sinks = new();
        private readonly Dictionary<string, string> _redactions = new(StringComparer.OrdinalIgnoreCase);

        public void AddSink(IAdvancedLogSink sink) => _sinks.Add(sink);
        public void AddRedaction(string tokenPrefix, string replacement) => _redactions[tokenPrefix] = replacement;
        public void Info(string category, string message) => Write("INFO", category, message);

        public void Write(string level, string category, string message)
        {
            foreach (var pair in _redactions)
            {
                var idx = message.IndexOf(pair.Key + "=", StringComparison.OrdinalIgnoreCase);
                if (idx < 0) continue;
                var end = message.IndexOf(' ', idx);
                if (end < 0) end = message.Length;
                message = message[..idx] + pair.Value + message[end..];
            }
            var entry = new AdvancedLogEntry(DateTime.UtcNow, level, category, message);
            foreach (var sink in _sinks) sink.Write(entry);
        }
    }

    public sealed class AdvancedCrashReport
    {
        public string ReportId { get; set; } = "";
        public string EngineVersion { get; set; } = "";
        public string ExceptionType { get; set; } = "";
        public string Message { get; set; } = "";
        public List<string> RecentLogs { get; set; } = new();
        public Dictionary<string, string> CustomData { get; set; } = new();
    }

    public sealed class AdvancedCrashReportBuilder
    {
        private string _engineVersion = "unknown";
        private readonly List<string> _recentLogs = new();
        private readonly Dictionary<string, string> _custom = new(StringComparer.OrdinalIgnoreCase);

        public AdvancedCrashReportBuilder WithEngineVersion(string version)
        {
            _engineVersion = version;
            return this;
        }

        public AdvancedCrashReportBuilder WithRecentLog(string log)
        {
            _recentLogs.Add(log);
            return this;
        }

        public AdvancedCrashReportBuilder WithCustomData(string key, string value)
        {
            _custom[key] = value;
            return this;
        }

        public AdvancedCrashReport Capture(Exception exception)
        {
            var material = _engineVersion + "|" + exception.GetType().FullName + "|" + exception.Message + "|" + string.Join("|", _recentLogs);
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)))[..24];
            return new AdvancedCrashReport
            {
                ReportId = hash,
                EngineVersion = _engineVersion,
                ExceptionType = exception.GetType().Name,
                Message = exception.Message,
                RecentLogs = new List<string>(_recentLogs),
                CustomData = new Dictionary<string, string>(_custom, StringComparer.OrdinalIgnoreCase)
            };
        }
    }
}
