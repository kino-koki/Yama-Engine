using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace ShanyuEngine.Testing
{
    public sealed class AutomationRunFilter
    {
        public string[] IncludeTags { get; init; } = Array.Empty<string>();
    }

    public sealed class AutomationTestResult
    {
        public string Name { get; init; } = "";
        public bool Passed { get; init; }
        public int Attempts { get; init; }
        public string Error { get; init; } = "";
    }

    public sealed class AutomationRunReport
    {
        public List<AutomationTestResult> Results { get; } = new();
    }

    public sealed class AdvancedAutomationRunner
    {
        private sealed class Case
        {
            public string Name = "";
            public HashSet<string> Tags = new(StringComparer.OrdinalIgnoreCase);
            public Action Body = () => { };
            public int Retries;
        }

        private readonly List<Case> _cases = new();

        public void Add(string name, IEnumerable<string> tags, Action body, int retries = 0)
        {
            _cases.Add(new Case { Name = name, Tags = new HashSet<string>(tags, StringComparer.OrdinalIgnoreCase), Body = body, Retries = retries });
        }

        public AutomationRunReport Run(AutomationRunFilter filter)
        {
            var report = new AutomationRunReport();
            var include = new HashSet<string>(filter.IncludeTags ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            foreach (var test in _cases.Where(c => include.Count == 0 || c.Tags.Overlaps(include)))
            {
                var attempts = 0;
                Exception? last = null;
                for (var i = 0; i <= test.Retries; i++)
                {
                    attempts++;
                    try
                    {
                        test.Body();
                        last = null;
                        break;
                    }
                    catch (Exception ex)
                    {
                        last = ex;
                    }
                }
                report.Results.Add(new AutomationTestResult { Name = test.Name, Passed = last == null, Attempts = attempts, Error = last?.Message ?? "" });
            }
            return report;
        }
    }

    public sealed class BenchmarkResult
    {
        public string Name { get; init; } = "";
        public int Iterations { get; init; }
        public double AverageNanoseconds { get; init; }
        public double RatioToBaseline { get; init; }
    }

    public sealed class BenchmarkReport
    {
        public List<BenchmarkResult> Results { get; } = new();
    }

    public sealed class AdvancedBenchmarkRunner
    {
        private sealed class Bench
        {
            public string Name = "";
            public int Warmup;
            public int Measured;
            public Action Body = () => { };
        }

        private readonly List<Bench> _benches = new();

        public void Add(string name, int warmupIterations, int measuredIterations, Action body)
        {
            _benches.Add(new Bench { Name = name, Warmup = warmupIterations, Measured = measuredIterations, Body = body });
        }

        public BenchmarkReport Run(IReadOnlyDictionary<string, double> baselines)
        {
            var report = new BenchmarkReport();
            foreach (var bench in _benches)
            {
                for (var i = 0; i < bench.Warmup; i++) bench.Body();
                var sw = Stopwatch.StartNew();
                for (var i = 0; i < bench.Measured; i++) bench.Body();
                sw.Stop();
                var averageNs = sw.Elapsed.TotalMilliseconds * 1_000_000.0 / Math.Max(1, bench.Measured);
                var baseline = baselines.TryGetValue(bench.Name, out var ns) ? ns : averageNs;
                report.Results.Add(new BenchmarkResult
                {
                    Name = bench.Name,
                    Iterations = bench.Measured,
                    AverageNanoseconds = averageNs,
                    RatioToBaseline = baseline <= 0 ? 1 : averageNs / baseline
                });
            }
            return report;
        }
    }
}
