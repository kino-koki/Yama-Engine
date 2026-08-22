// ==================================================================================================
//  Runtime.Scripting.ScriptEngine.cs — 山雨引擎运行时脚本引擎
//
//  命名空间: ShanyuEngine.Runtime.Scripting
//  依赖:     Microsoft.CodeAnalysis.CSharp (Roslyn), System.Runtime.Loader
//  引用:     ShanyuEngine.ECS
//  目标框架: net8.0
//
//  功能:
//    1. ScriptType              — 脚本类型枚举 (CSharp / Lua / VisualScript)
//    2. ScriptDiagnostic        — 编译诊断信息 (Id / Severity / Message / Location)
//    3. ScriptCompilationResult — 编译结果 (Success / Diagnostics / Assembly / Types)
//    4. ScriptEngine            — Roslyn 编译器驱动的脚本引擎
//       - CompileScripts(List<string>)           — 批量编译 .cs 文件
//       - CompileSingleFile(string)              — 单文件热重载编译
//       - CompileFromSource(string, string)       — 内存中源码编译
//       - CreateScriptInstance(Type)             — 创建脚本实例
//       - 全局脚本缓存 (Assembly → Types 映射)
//       - AssemblyLoadContext 隔离 / 卸载 (可回收)
//       - 热重载: 卸载旧程序集 → 编译新代码 → 更新缓存
// ==================================================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;

using ShanyuEngine.ECS;

namespace ShanyuEngine.Runtime.Scripting
{
    // ════════════════════════════════════════════════════════════════════════
    // 1. 枚举定义
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 脚本类型。当前运行时脚本引擎完整支持 CSharp (Roslyn 即时编译)，
    /// Lua 与 VisualScript 为预留类型，便于上层统一调度。
    /// </summary>
    public enum ScriptType
    {
        /// <summary>C# 脚本，由 Roslyn 即时编译为程序集。</summary>
        CSharp,
        /// <summary>Lua 脚本 (预留)。</summary>
        Lua,
        /// <summary>可视化脚本 / 蓝图 (预留)。</summary>
        VisualScript
    }

    /// <summary>编译诊断严重级别，与 Roslyn DiagnosticSeverity 对齐。</summary>
    public enum ScriptDiagnosticSeverity
    {
        /// <summary>隐藏信息。</summary>
        Hidden,
        /// <summary>提示信息。</summary>
        Info,
        /// <summary>警告。</summary>
        Warning,
        /// <summary>错误 (会导致编译失败)。</summary>
        Error
    }

    // ════════════════════════════════════════════════════════════════════════
    // 2. 编译诊断信息
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 编译诊断信息。将 Roslyn 的 Diagnostic 转换为引擎自有的、可序列化的轻量结构，
    /// 避免在引擎 API 表面直接暴露 Roslyn 内部类型。
    /// </summary>
    public sealed class ScriptDiagnostic
    {
        /// <summary>诊断标识符，例如 "CS0103"。</summary>
        public string Id { get; }

        /// <summary>严重级别。</summary>
        public ScriptDiagnosticSeverity Severity { get; }

        /// <summary>诊断消息文本。</summary>
        public string Message { get; }

        /// <summary>来源文件路径 (可能为空)。</summary>
        public string FilePath { get; }

        /// <summary>行号 (从 1 开始，0 表示未知)。</summary>
        public int Line { get; }

        /// <summary>列号 (从 1 开始，0 表示未知)。</summary>
        public int Column { get; }

        /// <summary>构造诊断信息。</summary>
        public ScriptDiagnostic(string id, ScriptDiagnosticSeverity severity, string message,
                                 string filePath, int line, int column)
        {
            Id = id ?? string.Empty;
            Severity = severity;
            Message = message ?? string.Empty;
            FilePath = filePath ?? string.Empty;
            Line = line;
            Column = column;
        }

        /// <summary>是否为错误级别。</summary>
        public bool IsError => Severity == ScriptDiagnosticSeverity.Error;

        /// <inheritdoc/>
        public override string ToString()
        {
            var loc = string.IsNullOrEmpty(FilePath)
                ? string.Empty
                : $"{FilePath}({Line},{Column}): ";
            return $"{loc}{Severity} {Id}: {Message}";
        }

        /// <summary>从 Roslyn Diagnostic 构造。</summary>
        internal static ScriptDiagnostic FromRoslyn(Diagnostic d)
        {
            var severity = d.Severity switch
            {
                DiagnosticSeverity.Hidden => ScriptDiagnosticSeverity.Hidden,
                DiagnosticSeverity.Info => ScriptDiagnosticSeverity.Info,
                DiagnosticSeverity.Warning => ScriptDiagnosticSeverity.Warning,
                DiagnosticSeverity.Error => ScriptDiagnosticSeverity.Error,
                _ => ScriptDiagnosticSeverity.Info
            };

            var lm = d.Location.GetMappedLineSpan();
            var path = lm.Path ?? string.Empty;
            var line = lm.IsValidLineSpan ? lm.StartLinePosition.Line + 1 : 0;
            var col = lm.IsValidLineSpan ? lm.StartLinePosition.Character + 1 : 0;

            return new ScriptDiagnostic(d.Id, severity, d.GetMessage(), path, line, col);
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // 3. 编译结果
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 脚本编译结果。无论成功与否，都会返回诊断信息；成功时还会附带
    /// 加载好的程序集与从中导出的公开类型列表。
    /// </summary>
    public sealed class ScriptCompilationResult
    {
        /// <summary>编译是否成功 (无错误且程序集已加载)。</summary>
        public bool Success { get; }

        /// <summary>加载得到的程序集，失败时为 null。</summary>
        public Assembly? Assembly { get; }

        /// <summary>从程序集中导出的公开类型 (成功时填充)。</summary>
        public IReadOnlyList<Type> Types { get; }

        /// <summary>全部诊断信息 (含警告与错误)。</summary>
        public IReadOnlyList<ScriptDiagnostic> Diagnostics { get; }

        /// <summary>编译所用的程序集名称。</summary>
        public string AssemblyName { get; }

        /// <summary>错误级别诊断。</summary>
        public IEnumerable<ScriptDiagnostic> Errors => Diagnostics.Where(d => d.IsError);

        /// <summary>警告级别诊断。</summary>
        public IEnumerable<ScriptDiagnostic> Warnings => Diagnostics.Where(d => d.Severity == ScriptDiagnosticSeverity.Warning);

        /// <summary>是否存在错误。</summary>
        public bool HasErrors => Diagnostics.Any(d => d.IsError);

        private ScriptCompilationResult(bool success, Assembly? assembly, IReadOnlyList<Type> types,
                                         IReadOnlyList<ScriptDiagnostic> diagnostics, string assemblyName)
        {
            Success = success;
            Assembly = assembly;
            Types = types;
            Diagnostics = diagnostics;
            AssemblyName = assemblyName;
        }

        /// <summary>构造失败结果。</summary>
        internal static ScriptCompilationResult Failed(string assemblyName, IEnumerable<ScriptDiagnostic> diagnostics)
        {
            var list = diagnostics as IReadOnlyList<ScriptDiagnostic> ?? diagnostics.ToList();
            return new ScriptCompilationResult(false, null, Array.Empty<Type>(), list, assemblyName);
        }

        /// <summary>构造成功结果。</summary>
        internal static ScriptCompilationResult Succeeded(Assembly assembly, IReadOnlyList<Type> types,
                                                          IReadOnlyList<ScriptDiagnostic> diagnostics, string assemblyName)
        {
            return new ScriptCompilationResult(true, assembly, types, diagnostics, assemblyName);
        }

        /// <inheritdoc/>
        public override string ToString()
        {
            if (Success)
                return $"[OK] {AssemblyName}: {Types.Count} type(s), {Diagnostics.Count} diagnostic(s)";
            return $"[FAIL] {AssemblyName}: {Errors.Count()} error(s)";
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // 4. 脚本宿主接口 (可选实现)
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 脚本可选实现的接口。若编译出的类型实现该接口，<see cref="ScriptEngine.CreateScriptInstance"/>
    /// 会在构造实例后自动调用 <see cref="Initialize"/>，便于脚本执行初始化逻辑。
    /// </summary>
    public interface IScript
    {
        /// <summary>脚本被实例化后的初始化回调。</summary>
        void Initialize();
    }

    // ════════════════════════════════════════════════════════════════════════
    // 5. 可回收程序集加载上下文
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 可回收 (collectible) 的程序集加载上下文。每个编译产物都加载在独立的上下文中，
    /// 通过 <see cref="Unload"/> 即可卸载其全部程序集并回收内存，从而实现热重载与隔离。
    /// </summary>
    internal sealed class ScriptLoadContext : AssemblyLoadContext
    {
        private readonly AssemblyLoadContext _resolver;
        private readonly AssemblyDependencyResolver _dependencyResolver;

        public ScriptLoadContext(string name, AssemblyLoadContext resolver)
            : base(name, isCollectible: true)
        {
            _resolver = resolver;
            _dependencyResolver = new AssemblyDependencyResolver(typeof(ScriptEngine).Assembly.Location);
        }

        /// <summary>
        /// 解析程序集引用。优先使用依赖解析器处理 .deps.json 配置的程序集，
        /// 否则回退到宿主上下文已加载的同名程序集，确保引擎自身 (含 ShanyuEngine.ECS)
        /// 与系统库被正确复用，而非重复加载。
        /// </summary>
        protected override Assembly? Load(AssemblyName assemblyName)
        {
            // 1) 依赖解析器 (处理 .deps.json 中声明的依赖)
            var path = _dependencyResolver.ResolveAssemblyToPath(assemblyName);
            if (path != null)
                return null; // 交由默认上下文加载，保证框架程序集共享

            // 2) 复用宿主上下文已加载的程序集 (引擎程序集 / 系统库)
            var existing = _resolver.Assemblies.FirstOrDefault(a => a.GetName().Name == assemblyName.Name);
            if (existing != null)
                return existing;

            return null;
        }

        /// <summary>解析非托管 (native) 依赖。</summary>
        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            var path = _dependencyResolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            return path != null ? LoadUnmanagedDllFromPath(path) : IntPtr.Zero;
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // 6. 脚本引擎
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 山雨引擎运行时脚本引擎。基于 Roslyn (Microsoft.CodeAnalysis.CSharp) 即时编译 C# 源码，
    /// 通过可回收的 <see cref="AssemblyLoadContext"/> 实现程序集隔离与卸载，从而支持热重载：
    /// 卸载旧程序集 → 编译新代码 → 更新缓存。
    /// </summary>
    public sealed class ScriptEngine : IDisposable
    {
        // ──────────────────────────────────────────────────────────────
        // 6.1 内部数据结构
        // ──────────────────────────────────────────────────────────────

        /// <summary>单个编译产物在缓存中的条目。</summary>
        private sealed class ScriptAssemblyEntry
        {
            public string Key { get; }
            public string AssemblyName { get; }
            public ScriptLoadContext Context { get; }
            public Assembly Assembly { get; }
            public List<Type> Types { get; }

            public ScriptAssemblyEntry(string key, string assemblyName,
                                        ScriptLoadContext context, Assembly assembly, List<Type> types)
            {
                Key = key;
                AssemblyName = assemblyName;
                Context = context;
                Assembly = assembly;
                Types = types;
            }
        }

        // ──────────────────────────────────────────────────────────────
        // 6.2 字段
        // ──────────────────────────────────────────────────────────────

        private readonly object _gate = new();
        private readonly Dictionary<string, ScriptAssemblyEntry> _entries = new();
        private readonly Dictionary<Assembly, ScriptAssemblyEntry> _assemblyToEntry = new();
        private readonly List<MetadataReference> _references;
        private readonly CSharpCompilationOptions _options;
        private readonly Dictionary<string, string> _preprocessorSymbols;
        private bool _disposed;

        // ──────────────────────────────────────────────────────────────
        // 6.3 公共属性
        // ──────────────────────────────────────────────────────────────

        /// <summary>当前缓存的编译条目数量。</summary>
        public int CachedAssemblyCount
        {
            get
            {
                lock (_gate) return _entries.Count;
            }
        }

        /// <summary>缓存中所有已加载程序集导出的公开类型。</summary>
        public IReadOnlyList<Type> AllCachedTypes
        {
            get
            {
                lock (_gate)
                {
                    var list = new List<Type>();
                    foreach (var entry in _entries.Values)
                        list.AddRange(entry.Types);
                    return list;
                }
            }
        }

        /// <summary>引擎使用的预处理器符号 (如 "SHANYU", "TRACE")。</summary>
        public IReadOnlyDictionary<string, string> PreprocessorSymbols => _preprocessorSymbols;

        // ──────────────────────────────────────────────────────────────
        // 6.4 构造与引用构建
        // ──────────────────────────────────────────────────────────────

        /// <summary>
        /// 构造脚本引擎。会收集运行时可用的元数据引用 (含 ShanyuEngine.ECS 与引擎自身程序集)，
        /// 供后续编译使用。
        /// </summary>
        /// <param name="optimizationLevel">编译优化级别，默认为 Debug (便于热重载调试)。</param>
        /// <param name="allowUnsafe">是否允许 unsafe 代码。</param>
        public ScriptEngine(OptimizationLevel? optimizationLevel = null, bool allowUnsafe = true)
        {
            _preprocessorSymbols = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["SHANYU"] = "1",
                ["TRACE"] = "1"
            };

            _references = BuildReferences();
            _options = new CSharpCompilationOptions(
                outputKind: OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: optimizationLevel ?? OptimizationLevel.Debug,
                allowUnsafe: allowUnsafe,
                assemblyIdentityComparer: DesktopAssemblyIdentityComparer.Default,
                metadataImportOptions: MetadataImportOptions.All,
                warningLevel: 4);
        }

        /// <summary>
        /// 构建编译所需的元数据引用集合。
        /// 收集策略: 当前 AppDomain 中所有具有物理路径的程序集 + 引擎自身 + ShanyuEngine.ECS，
        /// 确保脚本可引用引擎 API 与 ECS 组件类型。
        /// </summary>
        private List<MetadataReference> BuildReferences()
        {
            var refs = new Dictionary<string, MetadataReference>(StringComparer.OrdinalIgnoreCase);

            // 1) 引擎自身程序集 (ScriptEngine 所在程序集)
            AddAssemblyReference(refs, typeof(ScriptEngine).Assembly);

            // 2) ShanyuEngine.ECS 程序集 (显式保证可被脚本引用)
            AddAssemblyReference(refs, typeof(ShanyuEngine.ECS.IComponent).Assembly);

            // 3) 核心运行时与所有已加载程序集
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                AddAssemblyReference(refs, asm);

            // 4) 兜底: .NET 运行时目录中的核心程序集
            var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location);
            if (!string.IsNullOrEmpty(runtimeDir))
            {
                foreach (var name in s_coreRuntimeAssemblies)
                {
                    var full = Path.Combine(runtimeDir, name);
                    if (File.Exists(full) && !refs.ContainsKey(name))
                        refs[name] = MetadataReference.CreateFromFile(full);
                }
            }

            return refs.Values.ToList();
        }

        private static void AddAssemblyReference(Dictionary<string, MetadataReference> refs, Assembly asm)
        {
            if (asm.IsDynamic)
                return;
            var path = asm.Location;
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return;
            var name = Path.GetFileName(path);
            if (!refs.ContainsKey(name))
                refs[name] = MetadataReference.CreateFromFile(path);
        }

        // .NET 运行时核心程序集，用于兜底引用
        private static readonly string[] s_coreRuntimeAssemblies =
        {
            "System.Runtime.dll",
            "System.Private.CoreLib.dll",
            "System.Collections.dll",
            "System.Console.dll",
            "System.Linq.dll",
            "System.Linq.Expressions.dll",
            "System.Runtime.Extensions.dll",
            "System.Runtime.InteropServices.dll",
            "System.Threading.dll",
            "System.Text.Json.dll",
            "System.Net.Http.dll",
            "System.Numerics.dll",
            "System.ObjectModel.dll",
            "System.Reflection.dll",
            "System.IO.FileSystem.dll"
        };

        /// <summary>追加一个额外的元数据引用 (例如插件程序集)。</summary>
        public void AddReference(string assemblyPath)
        {
            if (string.IsNullOrEmpty(assemblyPath) || !File.Exists(assemblyPath))
                throw new FileNotFoundException("找不到引用程序集文件。", assemblyPath);

            lock (_gate)
            {
                var name = Path.GetFileName(assemblyPath);
                _references.Add(MetadataReference.CreateFromFile(assemblyPath));
            }
        }

        /// <summary>追加一个由类型所在的程序集推导出的元数据引用。</summary>
        public void AddReference(Type typeFromAssembly)
        {
            if (typeFromAssembly == null) throw new ArgumentNullException(nameof(typeFromAssembly));
            AddAssemblyReference_BuildAgain(typeFromAssembly.Assembly);
        }

        private void AddAssemblyReference_BuildAgain(Assembly asm)
        {
            if (asm.IsDynamic) return;
            var path = asm.Location;
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

            lock (_gate)
            {
                var name = Path.GetFileName(path);
                _references.Add(MetadataReference.CreateFromFile(path));
            }
        }

        /// <summary>添加预处理器符号。</summary>
        public void AddPreprocessorSymbol(string symbol, string value = "1")
        {
            if (string.IsNullOrWhiteSpace(symbol)) return;
            _preprocessorSymbols[symbol] = value;
        }

        // ──────────────────────────────────────────────────────────────
        // 6.5 批量编译: CompileScripts
        // ──────────────────────────────────────────────────────────────

        /// <summary>
        /// 批量编译多个 .cs 文件为一个程序集。以文件路径集合作为缓存键，
        /// 相同文件集再次调用时会热重载 (卸载旧程序集后重新编译)。
        /// </summary>
        /// <param name="filePaths">.cs 文件路径列表。</param>
        /// <returns>编译结果。</returns>
        public ScriptCompilationResult CompileScripts(List<string> filePaths)
        {
            if (filePaths == null) throw new ArgumentNullException(nameof(filePaths));
            if (filePaths.Count == 0)
                throw new ArgumentException("文件路径列表不能为空。", nameof(filePaths));

            var (missing, trees, sources) = ParseFiles(filePaths);
            if (missing.Count > 0)
            {
                var diags = missing.Select(p => new ScriptDiagnostic(
                    "SHANYU001", ScriptDiagnosticSeverity.Error,
                    $"找不到源文件: {p}", p, 0, 0));
                return ScriptCompilationResult.Failed("BatchCompilation", diags);
            }

            var assemblyName = "ShanyuScript_" + StableHash(string.Join("|", filePaths));
            var cacheKey = "batch:" + assemblyName;

            var treesList = trees.ToList();
            var (entry, diags) = CompileAndLoadCore(cacheKey, assemblyName, treesList);
            return ResultFromEntry(entry, diags, sources);
        }

        // ──────────────────────────────────────────────────────────────
        // 6.6 单文件热重载: CompileSingleFile
        // ──────────────────────────────────────────────────────────────

        /// <summary>
        /// 编译单个 .cs 文件并以文件路径作为缓存键，实现热重载:
        /// 若该文件已被编译过，先卸载其旧程序集与加载上下文，再重新编译并更新缓存。
        /// </summary>
        /// <param name="filePath">单个 .cs 文件路径。</param>
        /// <returns>编译结果。</returns>
        public ScriptCompilationResult CompileSingleFile(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("文件路径不能为空。", nameof(filePath));

            if (!File.Exists(filePath))
            {
                var d = new ScriptDiagnostic("SHANYU001", ScriptDiagnosticSeverity.Error,
                                             $"找不到源文件: {filePath}", filePath, 0, 0);
                return ScriptCompilationResult.Failed(Path.GetFileNameWithoutExtension(filePath), new[] { d });
            }

            var fullPath = Path.GetFullPath(filePath);
            var assemblyName = "ShanyuScript_" + Path.GetFileNameWithoutExtension(fullPath) + "_" +
                               StableHash(fullPath);

            var (missing, trees, sources) = ParseFiles(new List<string> { fullPath });
            if (missing.Count > 0)
            {
                var diags = missing.Select(p => new ScriptDiagnostic(
                    "SHANYU001", ScriptDiagnosticSeverity.Error,
                    $"找不到源文件: {p}", p, 0, 0));
                return ScriptCompilationResult.Failed(assemblyName, diags);
            }

            // 热重载: 文件路径作为缓存键
            var (entry, diags) = CompileAndLoadCore(fullPath, assemblyName, trees.ToList());
            return ResultFromEntry(entry, diags, sources);
        }

        // ──────────────────────────────────────────────────────────────
        // 6.7 内存源码编译: CompileFromSource
        // ──────────────────────────────────────────────────────────────

        /// <summary>
        /// 从内存源码字符串编译。以 <paramref name="assemblyName"/> 作为缓存键，
        /// 同名再次调用会热重载。
        /// </summary>
        /// <param name="source">C# 源码文本。</param>
        /// <param name="assemblyName">程序集名称 (同时作为缓存键)。</param>
        /// <returns>编译结果。</returns>
        public ScriptCompilationResult CompileFromSource(string source, string assemblyName)
        {
            if (string.IsNullOrWhiteSpace(source))
                throw new ArgumentException("源码不能为空。", nameof(source));
            if (string.IsNullOrWhiteSpace(assemblyName))
                throw new ArgumentException("程序集名称不能为空。", nameof(assemblyName));

            var cacheKey = "source:" + assemblyName;
            var tree = CSharpSyntaxTree.ParseText(source,
                CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest)
                                          .WithPreprocessorSymbols(_preprocessorSymbols));

            var (entry, diags) = CompileAndLoadCore(cacheKey, assemblyName, new List<SyntaxTree> { tree });
            return ResultFromEntry(entry, diags, new List<string> { "<in-memory:" + assemblyName + ">" });
        }

        // ──────────────────────────────────────────────────────────────
        // 6.8 编译核心实现
        // ──────────────────────────────────────────────────────────────

        /// <summary>
        /// 编译核心流程: 热重载清理 → 创建隔离上下文 → Roslyn 编译 → emit → 加载 → 导出类型 → 入缓存。
        /// 返回编译条目与诊断信息 (含警告)。
        /// </summary>
        private (ScriptAssemblyEntry entry, IReadOnlyList<ScriptDiagnostic> diagnostics) CompileAndLoadCore(
            string cacheKey, string assemblyName, List<SyntaxTree> trees)
        {
            // 1) 热重载: 若同键条目已存在，卸载旧程序集与上下文
            UnloadByKeyInternal(cacheKey);

            // 2) 创建独立可回收加载上下文
            var resolver = AssemblyLoadContext.GetLoadContext(typeof(ScriptEngine).Assembly)
                            ?? AssemblyLoadContext.Default;
            var context = new ScriptLoadContext("ShanyuScriptCtx_" + assemblyName, resolver);

            // 3) Roslyn 编译 (程序集名由 Create 首参指定)
            var compilation = CSharpCompilation.Create(
                assemblyName,
                trees,
                _references,
                _options);

            // 4) Emit 到内存流
            using var peStream = new MemoryStream();
            var emitOptions = new EmitOptions(
                debugInformationFormat: _options.OptimizationLevel == OptimizationLevel.Release
                    ? DebugInformationFormat.None
                    : DebugInformationFormat.Embedded);

            EmitResult emitResult;
            lock (_gate)
            {
                emitResult = compilation.Emit(peStream, options: emitOptions);
            }

            // 5) 收集诊断
            var diagnostics = emitResult.Diagnostics
                .Where(d => d.Severity != DiagnosticSeverity.Hidden)
                .Select(ScriptDiagnostic.FromRoslyn)
                .ToList();

            if (!emitResult.Success)
            {
                context.Unload();
                throw new ScriptCompilationException(
                    $"编译失败: {assemblyName}", assemblyName, diagnostics);
            }

            // 6) 加载程序集到隔离上下文
            peStream.Position = 0;
            Assembly assembly;
            try
            {
                assembly = context.LoadFromStream(peStream);
            }
            catch (Exception ex)
            {
                context.Unload();
                throw new ScriptCompilationException(
                    $"程序集加载失败: {assemblyName} ({ex.Message})", assemblyName, diagnostics, ex);
            }

            // 7) 导出公开类型 (排除编译器生成的临时类型)
            var types = ExportTypes(assembly);

            // 8) 入缓存
            var entry = new ScriptAssemblyEntry(cacheKey, assemblyName, context, assembly, types);
            lock (_gate)
            {
                _entries[cacheKey] = entry;
                _assemblyToEntry[assembly] = entry;
            }

            return (entry, diagnostics);
        }

        /// <summary>从程序集中导出公开、非抽象、可实例化的类型。</summary>
        private static List<Type> ExportTypes(Assembly assembly)
        {
            var result = new List<Type>();
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types.Where(t => t != null).ToArray()!;
            }

            foreach (var t in types)
            {
                if (t == null) continue;
                if (!t.IsPublic && !t.IsNestedPublic) continue;
                if (t.IsAbstract) continue;
                if (t.IsInterface) continue;
                if (t.IsGenericTypeDefinition) continue;
                if (t.GetCustomAttributes(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), false).Length > 0)
                    continue;
                result.Add(t);
            }
            return result;
        }

        // ──────────────────────────────────────────────────────────────
        // 6.9 结果构造与文件解析
        // ──────────────────────────────────────────────────────────────

        /// <summary>从缓存条目构造对外返回的编译结果 (含诊断信息)。</summary>
        private static ScriptCompilationResult ResultFromEntry(
            ScriptAssemblyEntry entry, IReadOnlyList<ScriptDiagnostic> diagnostics, List<string> sources)
        {
            // 注: CompileAndLoadCore 在失败时会抛异常，到达此处即代表成功。
            return ScriptCompilationResult.Succeeded(
                entry.Assembly, entry.Types, diagnostics, entry.AssemblyName);
        }

        /// <summary>解析多个文件为语法树，返回 (缺失文件, 语法树列表, 源文件列表)。</summary>
        private (List<string> missing, IEnumerable<SyntaxTree> trees, List<string> sources) ParseFiles(List<string> filePaths)
        {
            var missing = new List<string>();
            var trees = new List<SyntaxTree>();
            var sources = new List<string>();
            var parseOptions = CSharpParseOptions.Default
                .WithLanguageVersion(LanguageVersion.Latest)
                .WithPreprocessorSymbols(_preprocessorSymbols);

            foreach (var raw in filePaths)
            {
                if (string.IsNullOrWhiteSpace(raw))
                    continue;
                var path = Path.GetFullPath(raw);
                if (!File.Exists(path))
                {
                    missing.Add(path);
                    continue;
                }

                var source = File.ReadAllText(path);
                sources.Add(path);
                var tree = CSharpSyntaxTree.ParseText(source, parseOptions, path);
                trees.Add(tree);
            }

            return (missing, trees, sources);
        }

        // ──────────────────────────────────────────────────────────────
        // 6.10 实例创建: CreateScriptInstance
        // ──────────────────────────────────────────────────────────────

        /// <summary>
        /// 创建指定类型的脚本实例。使用无参公共构造函数。
        /// 若类型实现 <see cref="IScript"/>，则在构造后自动调用 <see cref="IScript.Initialize"/>。
        /// </summary>
        /// <param name="scriptType">脚本类型 (应来自已编译程序集)。</param>
        /// <returns>脚本实例。</returns>
        public object CreateScriptInstance(Type scriptType)
        {
            if (scriptType == null) throw new ArgumentNullException(nameof(scriptType));
            if (scriptType.IsAbstract || scriptType.IsInterface)
                throw new InvalidOperationException($"无法实例化抽象类型或接口: {scriptType.FullName}");
            if (scriptType.IsGenericTypeDefinition)
                throw new InvalidOperationException($"无法实例化未绑定的泛型类型: {scriptType.FullName}");

            var instance = Activator.CreateInstance(scriptType)
                ?? throw new InvalidOperationException($"实例化失败: {scriptType.FullName}");

            if (instance is IScript script)
                script.Initialize();

            return instance;
        }

        /// <summary>
        /// 按类型全名在缓存中查找并创建实例。
        /// </summary>
        /// <param name="fullTypeName">类型全名 (含命名空间)。</param>
        /// <param name="instance">创建的实例。</param>
        /// <returns>是否找到并成功创建。</returns>
        public bool TryCreateScriptInstance(string fullTypeName, out object? instance)
        {
            instance = null;
            if (string.IsNullOrWhiteSpace(fullTypeName)) return false;

            Type? type = null;
            lock (_gate)
            {
                foreach (var entry in _entries.Values)
                {
                    type = entry.Types.FirstOrDefault(t => t.FullName == fullTypeName);
                    if (type != null) break;
                }
            }

            if (type == null) return false;

            try
            {
                instance = CreateScriptInstance(type);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>创建泛型实例 (返回指定接口/基类型的实例)。</summary>
        public T CreateScriptInstance<T>(Type scriptType) where T : class
        {
            return CreateScriptInstance(scriptType) as T
                ?? throw new InvalidCastException(
                    $"类型 {scriptType.FullName} 无法转换为 {typeof(T).FullName}");
        }

        // ──────────────────────────────────────────────────────────────
        // 6.11 缓存查询
        // ──────────────────────────────────────────────────────────────

        /// <summary>获取指定缓存键对应的程序集。</summary>
        public Assembly? GetCachedAssembly(string cacheKey)
        {
            lock (_gate)
            {
                return _entries.TryGetValue(cacheKey, out var entry) ? entry.Assembly : null;
            }
        }

        /// <summary>获取指定缓存键对应的导出类型。</summary>
        public IReadOnlyList<Type> GetCachedTypes(string cacheKey)
        {
            lock (_gate)
            {
                return _entries.TryGetValue(cacheKey, out var entry)
                    ? entry.Types
                    : Array.Empty<Type>();
            }
        }

        /// <summary>按文件路径获取其单文件编译的程序集 (若存在)。</summary>
        public Assembly? GetAssemblyForFile(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return null;
            var fullPath = Path.GetFullPath(filePath);
            lock (_gate)
            {
                return _entries.TryGetValue(fullPath, out var entry) ? entry.Assembly : null;
            }
        }

        /// <summary>在全部缓存类型中按全名查找类型。</summary>
        public Type? FindType(string fullTypeName)
        {
            if (string.IsNullOrWhiteSpace(fullTypeName)) return null;
            lock (_gate)
            {
                foreach (var entry in _entries.Values)
                {
                    var t = entry.Types.FirstOrDefault(x => x.FullName == fullTypeName);
                    if (t != null) return t;
                }
            }
            return null;
        }

        // ──────────────────────────────────────────────────────────────
        // 6.12 卸载与热重载
        // ──────────────────────────────────────────────────────────────

        /// <summary>
        /// 卸载指定缓存键对应的程序集及其加载上下文，并从缓存中移除。
        /// 卸载后相关类型/实例将不可再访问。
        /// </summary>
        /// <returns>是否成功卸载 (键存在且已卸载)。</returns>
        public bool Unload(string cacheKey)
        {
            lock (_gate) return UnloadByKeyInternal(cacheKey);
        }

        /// <summary>卸载指定文件路径的单文件编译产物。</summary>
        public bool UnloadFile(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return false;
            var fullPath = Path.GetFullPath(filePath);
            return Unload(fullPath);
        }

        /// <summary>卸载与指定程序集关联的编译产物。</summary>
        public bool UnloadAssembly(Assembly assembly)
        {
            if (assembly == null) return false;
            lock (_gate)
            {
                if (!_assemblyToEntry.TryGetValue(assembly, out var entry))
                    return false;
                return UnloadByKeyInternal(entry.Key);
            }
        }

        /// <summary>卸载全部缓存程序集并重置缓存。</summary>
        public void UnloadAll()
        {
            lock (_gate)
            {
                foreach (var key in _entries.Keys.ToList())
                    UnloadByKeyInternal(key);
            }
        }

        /// <summary>卸载指定键的内部实现 (调用方已持有锁)。</summary>
        private bool UnloadByKeyInternal(string cacheKey)
        {
            if (!_entries.TryGetValue(cacheKey, out var entry))
                return false;

            _entries.Remove(cacheKey);
            _assemblyToEntry.Remove(entry.Assembly);

            try
            {
                entry.Context.Unload();
            }
            catch (Exception)
            {
                // 卸载失败不致命，条目已从缓存移除
            }
            return true;
        }

        // ──────────────────────────────────────────────────────────────
        // 6.13 辅助方法
        // ──────────────────────────────────────────────────────────────

        /// <summary>稳定的字符串哈希 (FNV-1a 变体)，用于生成确定性程序集名/缓存键。</summary>
        private static string StableHash(string input)
        {
            const uint fnvPrime = 16777619u;
            const uint fnvOffset = 2166136261u;
            uint hash = fnvOffset;
            foreach (var b in System.Text.Encoding.UTF8.GetBytes(input))
            {
                hash ^= b;
                hash *= fnvPrime;
            }
            return hash.ToString("X8");
        }

        /// <summary>获取当前缓存键的快照。</summary>
        public IReadOnlyList<string> GetCacheKeys()
        {
            lock (_gate) return _entries.Keys.ToList();
        }

        // ──────────────────────────────────────────────────────────────
        // 6.14 资源释放
        // ──────────────────────────────────────────────────────────────

        /// <summary>释放脚本引擎，卸载全部程序集。</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            UnloadAll();
        }

        ~ScriptEngine()
        {
            Dispose();
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // 7. 编译异常
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>脚本编译过程中发生的异常，附带诊断信息。</summary>
    public sealed class ScriptCompilationException : Exception
    {
        /// <summary>相关程序集名称。</summary>
        public string AssemblyName { get; }

        /// <summary>编译诊断信息。</summary>
        public IReadOnlyList<ScriptDiagnostic> Diagnostics { get; }

        public ScriptCompilationException(string message, string assemblyName,
                                          IReadOnlyList<ScriptDiagnostic> diagnostics, Exception? inner = null)
            : base(message, inner)
        {
            AssemblyName = assemblyName;
            Diagnostics = diagnostics;
        }
    }
}
