using System;
using System.Collections.Generic;
using System.Linq;

namespace YamaEngine.Build
{
    public sealed class BuildValidationSnapshot
    {
        public HashSet<string> Files { get; }
        public HashSet<string> Directories { get; }
        public Dictionary<string, string> Settings { get; }

        public BuildValidationSnapshot(IEnumerable<string> files, IEnumerable<string> directories, Dictionary<string, string> settings)
        {
            Files = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);
            Directories = new HashSet<string>(directories, StringComparer.OrdinalIgnoreCase);
            Settings = new Dictionary<string, string>(settings, StringComparer.OrdinalIgnoreCase);
        }
    }

    public sealed class BuildValidationIssue
    {
        public string Code { get; init; } = "";
        public string Message { get; init; } = "";
    }

    public sealed class BuildValidationReport
    {
        public List<BuildValidationIssue> Issues { get; } = new();
        public bool Success => Issues.Count == 0;
    }

    public sealed class AdvancedBuildValidationPipeline
    {
        private readonly List<Func<BuildValidationSnapshot, BuildValidationIssue?>> _rules = new();

        public AdvancedBuildValidationPipeline RequireFile(string path)
        {
            _rules.Add(s => s.Files.Contains(path) ? null : new BuildValidationIssue { Code = "FILE_MISSING", Message = path });
            return this;
        }

        public AdvancedBuildValidationPipeline RequireDirectory(string path)
        {
            _rules.Add(s => s.Directories.Contains(path) ? null : new BuildValidationIssue { Code = "DIRECTORY_MISSING", Message = path });
            return this;
        }

        public AdvancedBuildValidationPipeline RequireSetting(string key, string value)
        {
            _rules.Add(s => s.Settings.TryGetValue(key, out var actual) && string.Equals(actual, value, StringComparison.OrdinalIgnoreCase)
                ? null
                : new BuildValidationIssue { Code = "SETTING_MISMATCH", Message = key + "=" + value });
            return this;
        }

        public BuildValidationReport Validate(BuildValidationSnapshot snapshot)
        {
            var report = new BuildValidationReport();
            foreach (var rule in _rules)
            {
                var issue = rule(snapshot);
                if (issue != null) report.Issues.Add(issue);
            }
            return report;
        }
    }

    public sealed class CodeHealthIssue
    {
        public string Code { get; init; } = "";
        public string Message { get; init; } = "";
        public int Line { get; init; }
    }

    public sealed class CodeHealthReport
    {
        public List<CodeHealthIssue> Issues { get; } = new();
    }

    public static class AdvancedCodeHealthAnalyzer
    {
        public static CodeHealthReport AnalyzeText(string path, string source)
        {
            var report = new CodeHealthReport();
            var depth = 0;
            var maxDepth = 0;
            var line = 1;
            foreach (var ch in source)
            {
                if (ch == '\n') line++;
                if (ch == '{') maxDepth = Math.Max(maxDepth, ++depth);
                if (ch == '}') depth = Math.Max(0, depth - 1);
            }
            if (maxDepth > 2)
                report.Issues.Add(new CodeHealthIssue { Code = "NESTING_DEPTH", Line = line, Message = $"{path} nesting depth {maxDepth}" });
            if (source.Length > 80 && !source.Contains('\n'))
                report.Issues.Add(new CodeHealthIssue { Code = "LONG_LINE", Line = 1, Message = $"{path} contains a long single line" });
            if (source.Contains("if(true)", StringComparison.OrdinalIgnoreCase) || source.Contains("if (true)", StringComparison.OrdinalIgnoreCase))
                report.Issues.Add(new CodeHealthIssue { Code = "CONSTANT_BRANCH", Line = 1, Message = $"{path} contains constant branch" });
            return report;
        }
    }
}
