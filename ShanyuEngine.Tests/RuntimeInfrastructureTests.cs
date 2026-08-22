using System;
using System.Collections.Generic;
using ShanyuEngine.Build;
using ShanyuEngine.Diagnostics;
using ShanyuEngine.Testing;
using ShanyuEngine.UI;
using static ShanyuEngine.Tests.TestHelpers;

namespace ShanyuEngine.Tests
{
    public static class RuntimeInfrastructureTests
    {
        public static void TestAdvancedUILayoutSolvesFlexibleRowsAndColumns()
        {
            var layout = AdvancedUILayoutSolver.CreateGrid(800, 600, 3, 2)
                .WithPadding(20, 10, 20, 10)
                .WithGap(8, 12)
                .WithColumnWeight(0, 1)
                .WithColumnWeight(1, 2)
                .WithColumnWeight(2, 1)
                .WithRowWeight(0, 1)
                .WithRowWeight(1, 3);

            var cells = layout.Solve();

            AssertEqual(6, cells.Count, "Grid solver should return one rectangle per cell.");
            AssertTrue(cells[1].Width > cells[0].Width, "Weighted middle column should be wider.");
            AssertTrue(cells[3].Height > cells[0].Height, "Weighted second row should be taller.");
            AssertTrue(cells[0].X >= 20 && cells[0].Y >= 10, "Padding should move first cell inward.");
        }

        public static void TestStyleCascadeResolvesVariablesStatesAndSpecificity()
        {
            var sheet = new AdvancedStyleSheet()
                .SetVariable("primary", "#2F7BFF")
                .AddRule("button", new Dictionary<string, string> { ["color"] = "var(primary)", ["padding"] = "8" })
                .AddRule(".danger", new Dictionary<string, string> { ["color"] = "#E5484D" })
                .AddRule("button:hover", new Dictionary<string, string> { ["scale"] = "1.04" });

            var resolved = sheet.Resolve("button", new[] { "danger" }, UIInteractionState.Hovered);

            AssertEqual("#E5484D", resolved["color"], "Class rule should override element variable color.");
            AssertEqual("8", resolved["padding"], "Element rule should still contribute inherited declarations.");
            AssertEqual("1.04", resolved["scale"], "State selector should apply on hover.");
        }

        public static void TestEventRouterCapturesTargetsBubblesAndStopsPropagation()
        {
            var router = new AdvancedUIEventRouter();
            var calls = new List<string>();
            router.Register("root", null);
            router.Register("panel", "root");
            router.Register("button", "panel");
            router.On("root", UIEventPhase.Bubble, e => calls.Add("root"));
            router.On("panel", UIEventPhase.Capture, e => calls.Add("capture-panel"));
            router.On("button", UIEventPhase.Target, e => { calls.Add("target-button"); e.StopPropagation(); });

            var trace = router.Route(new UIEvent("click", "button"));

            AssertEqual(2, calls.Count, "StopPropagation should prevent bubble handlers after target.");
            AssertEqual("capture-panel", calls[0], "Capture should run from root toward target.");
            AssertEqual("target-button", calls[1], "Target handler should run before bubble.");
            AssertTrue(trace.Stopped, "Trace should mark stopped propagation.");
        }

        public static void TestAnimationTimelineSamplesEasingAndLoops()
        {
            var timeline = new AdvancedUIAnimationTimeline(loop: true)
                .AddFloatTrack("opacity", 0, 0)
                .AddFloatTrack("opacity", 1, 1, UIEasingKind.SmoothStep);

            var value = timeline.SampleFloat("opacity", 1.5);

            AssertTrue(value > 0.45f && value < 0.55f, "Looped smooth-step animation should sample near halfway at 1.5s.");
        }

        public static void TestProfilerAggregatesSamplesAndExportsPercentiles()
        {
            var profiler = new AdvancedFrameProfiler();
            profiler.Record("UI.Layout", 1.0);
            profiler.Record("UI.Layout", 2.0);
            profiler.Record("UI.Layout", 8.0);

            var summary = profiler.Summarize("UI.Layout");

            AssertEqual(3, summary.Count, "Profiler should count samples.");
            AssertTrue(summary.AverageMs > 3.6 && summary.AverageMs < 3.7, "Profiler should compute average duration.");
            AssertTrue(summary.P95Ms >= 8.0, "Profiler should expose high percentile cost.");
        }

        public static void TestStructuredLoggerRedactsAndRoutesEntries()
        {
            var logger = new AdvancedStructuredLogger();
            var memory = new InMemoryAdvancedLogSink();
            logger.AddSink(memory);
            logger.AddRedaction("token", "token=<redacted>");

            logger.Info("auth", "login token=secret user=alice");

            AssertEqual(1, memory.Entries.Count, "Logger should write to registered sink.");
            AssertTrue(memory.Entries[0].Message.Contains("token=<redacted>"), "Logger should redact sensitive values.");
        }

        public static void TestCrashReporterBuildsDeterministicBundle()
        {
            var reporter = new AdvancedCrashReportBuilder()
                .WithEngineVersion("1.0-test")
                .WithRecentLog("Frame 42: UI layout")
                .WithCustomData("scene", "Main");

            var report = reporter.Capture(new InvalidOperationException("boom"));

            AssertTrue(report.ReportId.Length >= 16, "Crash report should include stable id.");
            AssertEqual("InvalidOperationException", report.ExceptionType, "Crash report should capture exception type.");
            AssertTrue(report.RecentLogs.Contains("Frame 42: UI layout"), "Crash report should include recent logs.");
        }

        public static void TestAutomationRunnerFiltersRetriesAndReports()
        {
            var runner = new AdvancedAutomationRunner();
            var attempts = 0;
            runner.Add("smoke/ui/button", tags: new[] { "smoke", "ui" }, () =>
            {
                attempts++;
                if (attempts == 1) throw new InvalidOperationException("first fail");
            }, retries: 1);

            var report = runner.Run(new AutomationRunFilter(includeTags: new[] { "smoke" }));

            AssertEqual(1, report.Results.Count, "Automation runner should select tagged tests.");
            AssertTrue(report.Results[0].Passed, "Retry should allow transient failure to pass.");
            AssertEqual(2, attempts, "Runner should retry once after first failure.");
        }

        public static void TestBenchmarkRunnerWarmsUpMeasuresAndComparesBaseline()
        {
            var benchmark = new AdvancedBenchmarkRunner();
            benchmark.Add("math/add", warmupIterations: 2, measuredIterations: 5, () => Math.Sqrt(144));

            var report = benchmark.Run(new Dictionary<string, double> { ["math/add"] = 1000 });

            AssertEqual(1, report.Results.Count, "Benchmark runner should execute registered benchmark.");
            AssertTrue(report.Results[0].Iterations == 5, "Benchmark runner should report measured iteration count.");
            AssertTrue(report.Results[0].RatioToBaseline < 1.0, "Tiny benchmark should be faster than loose baseline.");
        }

        public static void TestBuildValidationAndCodeHealthDetectActionableIssues()
        {
            var validation = new AdvancedBuildValidationPipeline()
                .RequireFile("Project.shanyu")
                .RequireDirectory("Assets")
                .RequireSetting("TargetPlatform", "Windows");

            var buildReport = validation.Validate(new BuildValidationSnapshot(
                files: new[] { "Project.shanyu" },
                directories: Array.Empty<string>(),
                settings: new Dictionary<string, string> { ["TargetPlatform"] = "Windows" }));

            var health = AdvancedCodeHealthAnalyzer.AnalyzeText("Example.cs", "public class Big { void M(){ if(true){ if(false){ } } } }");

            AssertTrue(!buildReport.Success, "Build validation should fail when required directories are missing.");
            AssertTrue(health.Issues.Count > 0, "Code health analyzer should report nested branch/format issues.");
        }
    }
}
