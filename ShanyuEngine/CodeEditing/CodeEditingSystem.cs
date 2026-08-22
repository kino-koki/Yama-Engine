using System.Text;
using System.Text.Json;

namespace ShanyuEngine.CodeEditing
{
    public enum CodeEditorBoundary
    {
        SelfDevelopedCore,
        ExternalToolCompatible
    }

    public enum ScriptTemplate
    {
        ActorComponent,
        Pawn,
        GameMode
    }

    public sealed class CodeWorkspaceOptions
    {
        public string ProjectRoot { get; set; } = string.Empty;
        public string ProjectName { get; set; } = "ShanyuGame";
        public string EngineAssemblyName { get; set; } = "ShanyuEngine";
        public bool GenerateVsCodeCompatibility { get; set; } = true;
    }

    public sealed class CodeWorkspaceResult
    {
        public string ProjectRoot { get; init; } = string.Empty;
        public string ScriptsDirectory { get; init; } = string.Empty;
        public string ScriptProjectFile { get; init; } = string.Empty;
        public string WorkspaceFile { get; init; } = string.Empty;
        public string TasksFile { get; init; } = string.Empty;
        public string LaunchFile { get; init; } = string.Empty;
        public CodeEditorBoundary EditorBoundary { get; init; }
    }

    public static class CodeWorkspaceGenerator
    {
        public static CodeWorkspaceResult Generate(CodeWorkspaceOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (string.IsNullOrWhiteSpace(options.ProjectRoot)) throw new ArgumentException("Project root is required.");

            var projectRoot = Path.GetFullPath(options.ProjectRoot);
            var scripts = Path.Combine(projectRoot, "Scripts");
            var vscode = Path.Combine(projectRoot, ".vscode");
            Directory.CreateDirectory(projectRoot);
            Directory.CreateDirectory(scripts);
            Directory.CreateDirectory(vscode);

            var projectName = SanitizeIdentifier(options.ProjectName);
            var csproj = Path.Combine(scripts, $"{projectName}.Scripts.csproj");
            File.WriteAllText(csproj, CreateScriptProject(projectName, options.EngineAssemblyName), Encoding.UTF8);

            var scriptApi = Path.Combine(scripts, "ShanyuScript.cs");
            if (!File.Exists(scriptApi)) File.WriteAllText(scriptApi, CreateBaseScriptApi(), Encoding.UTF8);

            var workspaceFile = Path.Combine(projectRoot, $"{projectName}.code-workspace");
            File.WriteAllText(workspaceFile, CreateWorkspaceJson(projectName), Encoding.UTF8);

            var tasksFile = Path.Combine(vscode, "tasks.json");
            var launchFile = Path.Combine(vscode, "launch.json");
            if (options.GenerateVsCodeCompatibility)
            {
                File.WriteAllText(tasksFile, CreateTasksJson(csproj), Encoding.UTF8);
                File.WriteAllText(launchFile, CreateLaunchJson(projectName), Encoding.UTF8);
            }

            return new CodeWorkspaceResult
            {
                ProjectRoot = projectRoot,
                ScriptsDirectory = scripts,
                ScriptProjectFile = csproj,
                WorkspaceFile = workspaceFile,
                TasksFile = tasksFile,
                LaunchFile = launchFile,
                EditorBoundary = CodeEditorBoundary.ExternalToolCompatible
            };
        }

        private static string CreateScriptProject(string projectName, string engineAssemblyName)
        {
            return $"""
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AssemblyName>{projectName}.Scripts</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <Reference Include="{engineAssemblyName}" />
  </ItemGroup>
</Project>
""";
        }

        private static string CreateBaseScriptApi()
        {
            return """
namespace ShanyuGame
{
    public abstract class ShanyuScript
    {
        public virtual void OnStart() { }
        public virtual void OnUpdate(float deltaTime) { }
        protected void Log(string message) { System.Console.WriteLine(message); }
    }
}
""";
        }

        private static string CreateWorkspaceJson(string projectName)
        {
            var workspace = new
            {
                folders = new[] { new { path = "." }, new { path = "Scripts" } },
                settings = new Dictionary<string, object>
                {
                    ["shanyu.project"] = projectName,
                    ["shanyu.editorBoundary"] = "ExternalToolCompatible",
                    ["omnisharp.enableRoslynAnalyzers"] = true
                }
            };
            return JsonSerializer.Serialize(workspace, new JsonSerializerOptions { WriteIndented = true });
        }

        private static string CreateTasksJson(string csproj)
        {
            var escaped = csproj.Replace("\\", "\\\\");
            return $$"""
{
  "version": "2.0.0",
  "tasks": [
    {
      "label": "Shanyu: Build Scripts",
      "type": "shell",
      "command": "dotnet build \"{{escaped}}\"",
      "group": "build",
      "problemMatcher": "$msCompile"
    }
  ]
}
""";
        }

        private static string CreateLaunchJson(string projectName)
        {
            return $$"""
{
  "version": "0.2.0",
  "configurations": [
    {
      "name": "Attach to Shanyu Editor",
      "type": "coreclr",
      "request": "attach",
      "processName": "ShanyuEditor"
    }
  ],
  "compounds": []
}
""";
        }

        internal static string SanitizeIdentifier(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "ShanyuGame";
            var chars = name.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_').ToArray();
            var result = new string(chars);
            return char.IsDigit(result[0]) ? "_" + result : result;
        }
    }

    public sealed class InEngineCodeEditor
    {
        public string ScriptsDirectory { get; }

        private InEngineCodeEditor(string scriptsDirectory)
        {
            ScriptsDirectory = scriptsDirectory;
            Directory.CreateDirectory(scriptsDirectory);
        }

        public static InEngineCodeEditor Open(string scriptsDirectory)
        {
            return new InEngineCodeEditor(scriptsDirectory);
        }

        public CodeDocument CreateScript(string className, ScriptTemplate template)
        {
            var safe = CodeWorkspaceGenerator.SanitizeIdentifier(className);
            var file = Path.Combine(ScriptsDirectory, $"{safe}.cs");
            var text = template switch
            {
                ScriptTemplate.Pawn => CreatePawnTemplate(safe),
                ScriptTemplate.GameMode => CreateGameModeTemplate(safe),
                _ => CreateActorComponentTemplate(safe)
            };
            File.WriteAllText(file, text, Encoding.UTF8);
            return new CodeDocument(file, text);
        }

        public CodeDocument OpenDocument(string filePath)
        {
            return new CodeDocument(filePath, File.ReadAllText(filePath));
        }

        private static string CreateActorComponentTemplate(string className)
        {
            return $$"""
using ShanyuGame;

public sealed class {{className}} : ShanyuScript
{
    public override void OnStart()
    {
    }

    public override void OnUpdate(float deltaTime)
    {
    }
}
""";
        }

        private static string CreatePawnTemplate(string className)
        {
            return $$"""
using ShanyuGame;

public sealed class {{className}} : ShanyuScript
{
    public override void OnUpdate(float deltaTime)
    {
        Log("Pawn tick");
    }
}
""";
        }

        private static string CreateGameModeTemplate(string className)
        {
            return $$"""
using ShanyuGame;

public sealed class {{className}} : ShanyuScript
{
    public override void OnStart()
    {
        Log("Game started");
    }
}
""";
        }
    }

    public enum CodeDiagnosticSeverity
    {
        Info,
        Warning,
        Error
    }

    public sealed record CodeDiagnostic(CodeDiagnosticSeverity Severity, string Message, int Line);

    public static class CodeDiagnostics
    {
        public static IReadOnlyList<CodeDiagnostic> Analyze(CodeDocument document)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            var diagnostics = new List<CodeDiagnostic>();
            var balance = 0;
            var line = 1;
            foreach (var c in document.Text)
            {
                if (c == '\n') line++;
                if (c == '{') balance++;
                if (c == '}') balance--;
                if (balance < 0)
                {
                    diagnostics.Add(new CodeDiagnostic(CodeDiagnosticSeverity.Error, "Unexpected closing brace.", line));
                    balance = 0;
                }
            }
            if (balance > 0) diagnostics.Add(new CodeDiagnostic(CodeDiagnosticSeverity.Error, "Missing closing brace.", line));
            if (!document.Text.Contains("class ", StringComparison.Ordinal))
            {
                diagnostics.Add(new CodeDiagnostic(CodeDiagnosticSeverity.Error, "Script must declare a class.", 1));
            }
            return diagnostics;
        }
    }

    public enum CodeEditorKind
    {
        VSCode,
        CodeOSS,
        VSCodium,
        InEngine
    }

    public sealed class CodeCorePolicy
    {
        public bool RequiresSelfDevelopedRuntimeCore { get; init; } = true;
        public string PolicyText { get; init; } = "Runtime, gameplay framework and compile orchestration stay self-developed; external editors are optional tools.";
    }

    public sealed class ExternalCodeEditorDescriptor
    {
        public CodeEditorKind Kind { get; init; }
        public string DisplayName { get; init; } = string.Empty;
        public IReadOnlyList<string> ExecutableCandidates { get; init; } = Array.Empty<string>();
        public CodeEditorBoundary Boundary { get; init; } = CodeEditorBoundary.ExternalToolCompatible;
    }

    public sealed class ExternalCodeEditorOpenPlan
    {
        public CodeEditorKind Kind { get; init; }
        public IReadOnlyList<string> ExecutableCandidates { get; init; } = Array.Empty<string>();
        public string Arguments { get; init; } = string.Empty;
        public CodeEditorBoundary Boundary { get; init; }
        public CodeCorePolicy CorePolicy { get; init; } = new();
    }

    public sealed class ExternalCodeEditorCatalog
    {
        public IReadOnlyList<ExternalCodeEditorDescriptor> Editors { get; init; } = Array.Empty<ExternalCodeEditorDescriptor>();

        public static ExternalCodeEditorCatalog CreateDefault()
        {
            return new ExternalCodeEditorCatalog
            {
                Editors = new[]
                {
                    new ExternalCodeEditorDescriptor
                    {
                        Kind = CodeEditorKind.VSCode,
                        DisplayName = "Visual Studio Code compatible",
                        ExecutableCandidates = new[] { "code.cmd", "code.exe", "code" }
                    },
                    new ExternalCodeEditorDescriptor
                    {
                        Kind = CodeEditorKind.CodeOSS,
                        DisplayName = "Code-OSS compatible",
                        ExecutableCandidates = new[] { "code-oss.cmd", "code-oss.exe", "code-oss", "code" }
                    },
                    new ExternalCodeEditorDescriptor
                    {
                        Kind = CodeEditorKind.VSCodium,
                        DisplayName = "VSCodium compatible",
                        ExecutableCandidates = new[] { "codium.cmd", "VSCodium.exe", "codium" }
                    },
                    new ExternalCodeEditorDescriptor
                    {
                        Kind = CodeEditorKind.InEngine,
                        DisplayName = "Shanyu in-engine code editor",
                        ExecutableCandidates = Array.Empty<string>(),
                        Boundary = CodeEditorBoundary.SelfDevelopedCore
                    }
                }
            };
        }

        public ExternalCodeEditorOpenPlan CreateOpenPlan(CodeEditorKind kind, string workspaceFile)
        {
            var editor = Editors.FirstOrDefault(e => e.Kind == kind)
                         ?? throw new ArgumentException($"Code editor '{kind}' is not registered.");
            return new ExternalCodeEditorOpenPlan
            {
                Kind = kind,
                ExecutableCandidates = editor.ExecutableCandidates,
                Arguments = $"\"{workspaceFile}\"",
                Boundary = editor.Boundary,
                CorePolicy = new CodeCorePolicy()
            };
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 编译反馈 (Compile Feedback)
    // ──────────────────────────────────────────────────────────────

    /// <summary>编译结果。</summary>
    public sealed class CompileResult
    {
        public bool Success { get; init; }
        public List<CompileMessage> Messages { get; init; } = new();
        public int ErrorCount { get; init; }
        public int WarningCount { get; init; }
        public TimeSpan CompileTime { get; init; }
    }

    /// <summary>编译消息 (错误/警告)。</summary>
    public sealed class CompileMessage
    {
        public CodeDiagnosticSeverity Severity { get; init; }
        public string Code { get; init; } = string.Empty;
        public string Message { get; init; } = string.Empty;
        public string FilePath { get; init; } = string.Empty;
        public int Line { get; init; }
        public int Column { get; init; }
    }

    /// <summary>编译反馈分析器: 对代码文档执行轻量级静态分析, 模拟编译反馈。</summary>
    public static class CompileFeedbackAnalyzer
    {
        /// <summary>分析代码文档并生成编译反馈结果。</summary>
        public static CompileResult Analyze(CodeDocument document)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var messages = new List<CompileMessage>();
            int errors = 0, warnings = 0;

            var lines = document.Text.Split('\n');
            bool inMultiLineComment = false;
            bool inString = false;
            int braceBalance = 0;
            int parenBalance = 0;
            int bracketBalance = 0;

            for (int lineIdx = 0; lineIdx < lines.Length; lineIdx++)
            {
                var line = lines[lineIdx];
                int lineNum = lineIdx + 1;

                // 跳过多行注释中的行
                if (inMultiLineComment)
                {
                    var endIdx = line.IndexOf("*/", StringComparison.Ordinal);
                    if (endIdx >= 0) { inMultiLineComment = false; line = line[(endIdx + 2)..]; }
                    else continue;
                }

                // 逐字符扫描
                for (int i = 0; i < line.Length; i++)
                {
                    char c = line[i];
                    char next = i + 1 < line.Length ? line[i + 1] : '\0';

                    if (inString)
                    {
                        if (c == '\\') { i++; continue; }
                        if (c == '"') inString = false;
                        continue;
                    }

                    // 注释
                    if (c == '/' && next == '/') break; // 行注释
                    if (c == '/' && next == '*') { inMultiLineComment = true; break; }

                    // 字符串
                    if (c == '"') { inString = true; continue; }

                    // 括号匹配
                    if (c == '{') braceBalance++;
                    if (c == '}') { braceBalance--; if (braceBalance < 0) { messages.Add(Error("CS1513", "Unexpected }", lineNum, i + 1)); errors++; } }
                    if (c == '(') parenBalance++;
                    if (c == ')') { parenBalance--; if (parenBalance < 0) { messages.Add(Error("CS1026", "Unexpected )", lineNum, i + 1)); errors++; } }
                    if (c == '[') bracketBalance++;
                    if (c == ']') { bracketBalance--; if (bracketBalance < 0) { messages.Add(Error("CS0443", "Unexpected ]", lineNum, i + 1)); errors++; } }
                }

                // 检查行尾是否缺少分号 (简单的启发式)
                var trimmed = StripCommentsAndStrings(line).TrimEnd();
                if (trimmed.Length > 0 && ShouldEndWithSemicolon(trimmed))
                {
                    char last = trimmed[^1];
                    if (last != ';' && last != '{' && last != '}' && last != ',')
                    {
                        messages.Add(Warning("CS1002", "Possible missing semicolon ';'", lineNum, trimmed.Length));
                        warnings++;
                    }
                }
            }

            // 检查未闭合的括号
            if (braceBalance > 0) { messages.Add(Error("CS1513", "Expected } (unclosed brace)", lines.Length, 1)); errors++; }
            if (braceBalance < 0) { messages.Add(Error("CS1513", "Unexpected } (extra closing brace)", lines.Length, 1)); errors++; }
            if (parenBalance > 0) { messages.Add(Error("CS1026", "Expected ) (unclosed parenthesis)", lines.Length, 1)); errors++; }
            if (bracketBalance > 0) { messages.Add(Error("CS0443", "Expected ] (unclosed bracket)", lines.Length, 1)); errors++; }

            // 检查未闭合的字符串
            if (inString) { messages.Add(Error("CS1002", "Unterminated string literal", lines.Length, 1)); errors++; }
            if (inMultiLineComment) { messages.Add(Error("CS1035", "Unterminated comment", lines.Length, 1)); errors++; }

            // 检查是否有 class 声明
            if (!document.Text.Contains("class ", StringComparison.Ordinal) &&
                !document.Text.Contains("interface ", StringComparison.Ordinal) &&
                !document.Text.Contains("struct ", StringComparison.Ordinal) &&
                !document.Text.Contains("enum ", StringComparison.Ordinal) &&
                !document.Text.Contains("namespace ", StringComparison.Ordinal))
            {
                messages.Add(Error("CS0116", "A namespace cannot directly contain members such as fields or methods. Declare a class, struct, or interface.", 1, 1));
                errors++;
            }

            sw.Stop();
            return new CompileResult
            {
                Success = errors == 0,
                Messages = messages,
                ErrorCount = errors,
                WarningCount = warnings,
                CompileTime = sw.Elapsed
            };
        }

        private static bool ShouldEndWithSemicolon(string trimmed)
        {
            // 不需要分号的行
            if (trimmed.StartsWith("//") || trimmed.StartsWith("/*") || trimmed.StartsWith("#") ||
                trimmed.StartsWith("using ") && !trimmed.Contains("=") ||
                trimmed.StartsWith("namespace ") ||
                trimmed.EndsWith("{") || trimmed.EndsWith("}") ||
                trimmed.EndsWith("(") || trimmed.EndsWith(",") ||
                trimmed.EndsWith(":") || trimmed.EndsWith("=>"))
                return false;
            // 需要分号的常见模式
            return trimmed.Contains(" = ") || trimmed.Contains("return ") ||
                   trimmed.Contains("var ") || trimmed.Contains("int ") || trimmed.Contains("float ") ||
                   trimmed.Contains("string ") || trimmed.Contains("bool ") || trimmed.Contains("double ") ||
                   trimmed.Contains("Log(") || trimmed.Contains("throw ") || trimmed.Contains("break") ||
                   trimmed.Contains("continue") || trimmed.Contains("new ");
        }

        private static string StripCommentsAndStrings(string line)
        {
            var sb = new StringBuilder();
            bool inStr = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c == '/' && i + 1 < line.Length && line[i + 1] == '/') break;
                if (c == '"' && (i == 0 || line[i - 1] != '\\')) inStr = !inStr;
                if (!inStr) sb.Append(c);
            }
            return sb.ToString();
        }

        private static CompileMessage Error(string code, string msg, int line, int col) =>
            new() { Severity = CodeDiagnosticSeverity.Error, Code = code, Message = msg, Line = line, Column = col };

        private static CompileMessage Warning(string code, string msg, int line, int col) =>
            new() { Severity = CodeDiagnosticSeverity.Warning, Code = code, Message = msg, Line = line, Column = col };
    }
}
