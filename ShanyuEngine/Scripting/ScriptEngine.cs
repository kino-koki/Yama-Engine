using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;
using ShanyuEngine.Core;
using ShanyuEngine.ECS;

namespace ShanyuEngine.Scripting
{
    // ─────────────────────────────────────────────────────────────────────────────
    //  Diagnostics
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Severity levels for compilation diagnostics.
    /// </summary>
    public enum ScriptDiagnosticSeverity
    {
        Hidden,
        Info,
        Warning,
        Error
    }

    /// <summary>
    /// A single compilation diagnostic message (error, warning, or info).
    /// </summary>
    public sealed class ScriptDiagnostic
    {
        public ScriptDiagnosticSeverity Severity { get; init; }
        public string Message { get; init; } = string.Empty;
        public string? Id { get; init; }
        public string? FilePath { get; init; }
        public int Line { get; init; }
        public int Column { get; init; }

        public bool IsError => Severity == ScriptDiagnosticSeverity.Error;
        public bool IsWarning => Severity == ScriptDiagnosticSeverity.Warning;

        public override string ToString()
        {
            var sev = Severity switch
            {
                ScriptDiagnosticSeverity.Error => "error",
                ScriptDiagnosticSeverity.Warning => "warning",
                ScriptDiagnosticSeverity.Info => "info",
                _ => "hidden"
            };
            var loc = string.IsNullOrEmpty(FilePath)
                ? ""
                : $"{FilePath}({Line},{Column}): ";
            return $"{loc}{sev} {Id}: {Message}";
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  Script source model
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Represents a single C# source file to be compiled.
    /// </summary>
    public sealed class ScriptFile
    {
        /// <summary>Logical path (used for error reporting and source map).</summary>
        public string Path { get; init; } = string.Empty;

        /// <summary>The C# source text.</summary>
        public string Content { get; init; } = string.Empty;

        public ScriptFile() { }

        public ScriptFile(string path, string content)
        {
            Path = path ?? throw new ArgumentNullException(nameof(path));
            Content = content ?? throw new ArgumentNullException(nameof(content));
        }

        /// <summary>Creates a ScriptFile by reading a file from disk.</summary>
        public static ScriptFile FromFile(string filePath)
        {
            if (string.IsNullOrEmpty(filePath))
                throw new ArgumentException("File path is required.", nameof(filePath));
            if (!File.Exists(filePath))
                throw new FileNotFoundException($"Script file not found: {filePath}", filePath);

            return new ScriptFile
            {
                Path = filePath,
                Content = File.ReadAllText(filePath),
            };
        }
    }

    /// <summary>
    /// A collection of source files to be compiled into a single assembly.
    /// </summary>
    public sealed class ScriptSource
    {
        /// <summary>The output assembly name (without .dll extension).</summary>
        public string AssemblyName { get; init; } = "ShanyuScripts";

        /// <summary>The source files to compile.</summary>
        public IReadOnlyList<ScriptFile> Files { get; init; } = Array.Empty<ScriptFile>();

        /// <summary>
        /// Optional additional preprocessor symbols beyond the defaults
        /// (TRACE, DEBUG).
        /// </summary>
        public IReadOnlyList<string> PreprocessorSymbols { get; init; } = Array.Empty<string>();

        public ScriptSource() { }

        public ScriptSource(string assemblyName, IEnumerable<ScriptFile> files)
        {
            AssemblyName = assemblyName ?? throw new ArgumentNullException(nameof(assemblyName));
            Files = files?.ToList() ?? new List<ScriptFile>();
        }

        /// <summary>Creates a ScriptSource from a single source string.</summary>
        public static ScriptSource FromString(string source, string assemblyName = "ShanyuScripts",
            string filePath = "Script.cs")
        {
            return new ScriptSource
            {
                AssemblyName = assemblyName,
                Files = new[] { new ScriptFile { Path = filePath, Content = source } },
            };
        }

        /// <summary>Creates a ScriptSource by reading all .cs files from a directory.</summary>
        public static ScriptSource FromDirectory(string directory,
            string searchPattern = "*.cs", string assemblyName = "ShanyuScripts")
        {
            if (string.IsNullOrEmpty(directory))
                throw new ArgumentException("Directory is required.", nameof(directory));
            if (!Directory.Exists(directory))
                throw new DirectoryNotFoundException($"Script directory not found: {directory}");

            var files = Directory
                .GetFiles(directory, searchPattern, SearchOption.AllDirectories)
                .Select(ScriptFile.FromFile)
                .ToList();

            return new ScriptSource
            {
                AssemblyName = assemblyName,
                Files = files,
            };
        }

        /// <summary>Creates a ScriptSource from explicit file paths.</summary>
        public static ScriptSource FromFiles(IEnumerable<string> filePaths,
            string assemblyName = "ShanyuScripts")
        {
            if (filePaths == null)
                throw new ArgumentNullException(nameof(filePaths));

            var files = filePaths.Select(ScriptFile.FromFile).ToList();
            return new ScriptSource
            {
                AssemblyName = assemblyName,
                Files = files,
            };
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  Compile result
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Result of a compilation attempt, containing diagnostics and (on success)
    /// the loaded assembly info.
    /// </summary>
    public sealed class CompileResult
    {
        /// <summary>Whether compilation and loading succeeded.</summary>
        public bool Success { get; init; }

        /// <summary>The loaded assembly info (null on failure).</summary>
        public LoadedAssemblyInfo? Assembly { get; init; }

        /// <summary>All diagnostics (errors, warnings, infos) from the compilation.</summary>
        public IReadOnlyList<ScriptDiagnostic> Diagnostics { get; init; } = Array.Empty<ScriptDiagnostic>();

        /// <summary>Number of error diagnostics.</summary>
        public int ErrorCount { get; init; }

        /// <summary>Number of warning diagnostics.</summary>
        public int WarningCount { get; init; }

        /// <summary>The assembly name that was compiled.</summary>
        public string AssemblyName { get; init; } = string.Empty;

        /// <summary>Total compilation time.</summary>
        public TimeSpan CompileTime { get; init; }

        /// <summary>Whether this was a hot-reload (replaced a previous assembly).</summary>
        public bool WasHotReload { get; init; }

        /// <summary>Number of script types discovered in the assembly.</summary>
        public int ScriptTypeCount => Assembly?.ScriptTypeCount ?? 0;

        public IEnumerable<ScriptDiagnostic> GetErrors() =>
            Diagnostics.Where(d => d.Severity == ScriptDiagnosticSeverity.Error);

        public IEnumerable<ScriptDiagnostic> GetWarnings() =>
            Diagnostics.Where(d => d.Severity == ScriptDiagnosticSeverity.Warning);

        public string GetSummary()
        {
            var status = Success ? "Compiled" : "Failed";
            var reload = WasHotReload ? " (hot-reload)" : "";
            return $"{status}{reload}: {ErrorCount} error(s), {WarningCount} warning(s), " +
                   $"{ScriptTypeCount} type(s) in {CompileTime.TotalMilliseconds:0}ms";
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  ScriptEngine
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The main scripting engine.  Compiles C# source at runtime using Roslyn,
    /// loads the resulting assembly via <see cref="ScriptAssemblyLoader"/>, registers
    /// discovered types in the <see cref="ScriptRegistry"/>, and supports hot-reload
    /// with state preservation.
    /// </summary>
    public sealed class ScriptEngine : IDisposable
    {
        // ── Dependencies ───────────────────────────────────────────────────────────

        private readonly EntityComponentSystem _ecs;
        private readonly EngineSystems _engine;

        // ── Sub-systems ─────────────────────────────────────────────────────────────

        /// <summary>The assembly loader that manages collectible load contexts.</summary>
        public ScriptAssemblyLoader Loader { get; }

        /// <summary>The registry that manages script types and instances.</summary>
        public ScriptRegistry Registry { get; }

        // ── State ───────────────────────────────────────────────────────────────────

        private ScriptSource? _lastSource;
        private LoadedAssemblyInfo? _currentAssembly;
        private List<ScriptDiagnostic> _lastDiagnostics = new();
        private int _compileVersion;
        private List<MetadataReference>? _cachedReferences;
        private bool _disposed;

        // ── Configuration ──────────────────────────────────────────────────────────

        /// <summary>
        /// Whether to emit portable PDB (debug symbol) information.
        /// Disable for release builds to reduce memory and improve load time.
        /// </summary>
        public bool EmitDebugInfo { get; set; } = true;

        /// <summary>
        /// Optimization level for compiled scripts.
        /// <see cref="OptimizationLevel.Debug"/> for development,
        /// <see cref="OptimizationLevel.Release"/> for shipping.
        /// </summary>
        public OptimizationLevel OptimizationLevel { get; set; } = OptimizationLevel.Debug;

        /// <summary>
        /// C# language version used when parsing script source.
        /// </summary>
        public LanguageVersion LanguageVersion { get; set; } = LanguageVersion.CSharp12;

        /// <summary>
        /// Whether to allow unsafe code in compiled scripts.
        /// </summary>
        public bool AllowUnsafe { get; set; } = true;

        // ── Events ─────────────────────────────────────────────────────────────────

        /// <summary>Raised before a compilation begins.</summary>
        public event Action<ScriptSource>? OnCompileStarted;

        /// <summary>Raised after a compilation completes (success or failure).</summary>
        public event Action<CompileResult>? OnCompileFinished;

        /// <summary>Raised when a hot-reload cycle begins.</summary>
        public event Action? OnHotReloadStarted;

        /// <summary>Raised when a hot-reload cycle completes.</summary>
        public event Action<CompileResult>? OnHotReloadFinished;

        // ── Properties ─────────────────────────────────────────────────────────────

        /// <summary>The currently loaded script assembly, or null if none has been compiled.</summary>
        public LoadedAssemblyInfo? CurrentAssembly => _currentAssembly;

        /// <summary>Whether a script assembly is currently loaded.</summary>
        public bool IsCompiled => _currentAssembly != null;

        /// <summary>Diagnostics from the last compilation.</summary>
        public IReadOnlyList<ScriptDiagnostic> LastDiagnostics => _lastDiagnostics;

        /// <summary>Monotonically increasing compilation version counter.</summary>
        public int CompileVersion => _compileVersion;

        /// <summary>The last compiled source (for hot-reload).</summary>
        public ScriptSource? LastSource => _lastSource;

        // ── Constructor ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Creates a new ScriptEngine bound to the given ECS and engine systems.
        /// </summary>
        public ScriptEngine(EntityComponentSystem ecs, EngineSystems engine)
        {
            _ecs = ecs ?? throw new ArgumentNullException(nameof(ecs));
            _engine = engine ?? throw new ArgumentNullException(nameof(engine));
            Loader = new ScriptAssemblyLoader();
            Registry = new ScriptRegistry(ecs, engine);
            _engine.Log("ScriptEngine initialized.");
        }

        // ── Compilation API ────────────────────────────────────────────────────────

        /// <summary>
        /// Compiles the given script source, loads the resulting assembly, registers
        /// all discovered script types, and performs a hot-reload if a previous assembly
        /// was loaded (preserving serializable state).
        /// </summary>
        public CompileResult Compile(ScriptSource source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (source.Files.Count == 0)
            {
                return new CompileResult
                {
                    Success = false,
                    AssemblyName = source.AssemblyName,
                    Diagnostics = Array.Empty<ScriptDiagnostic>(),
                    ErrorCount = 1,
                    CompileTime = TimeSpan.Zero,
                };
            }

            OnCompileStarted?.Invoke(source);
            var sw = Stopwatch.StartNew();
            _compileVersion++;

            // 1. Compile to in-memory PE + PDB
            var (peStream, pdbStream, compileDiagnostics, errors, warnings) =
                CompileToAssemblyData(source);

            _lastDiagnostics = compileDiagnostics;

            if (errors > 0)
            {
                sw.Stop();
                var result = new CompileResult
                {
                    Success = false,
                    AssemblyName = source.AssemblyName,
                    Diagnostics = compileDiagnostics,
                    ErrorCount = errors,
                    WarningCount = warnings,
                    CompileTime = sw.Elapsed,
                };
                _engine.LogError(
                    $"Script compilation failed: {errors} error(s), {warnings} warning(s).");
                OnCompileFinished?.Invoke(result);
                return result;
            }

            // 2. Determine if this is a hot-reload
            bool isHotReload = _currentAssembly != null;
            ScriptStateSnapshot? stateSnapshot = null;

            if (isHotReload)
            {
                OnHotReloadStarted?.Invoke();
                stateSnapshot = Registry.CaptureState();
                Registry.DestroyAllInstances();
                Registry.ClearTypes();
                Loader.UnloadAll();
                Loader.CollectGarbage();
                _currentAssembly = null;
            }

            // 3. Load the new assembly
            LoadedAssemblyInfo info;
            var asmName = isHotReload
                ? $"{source.AssemblyName}_v{_compileVersion}"
                : source.AssemblyName;

            try
            {
                peStream.Position = 0;
                if (pdbStream != null) pdbStream.Position = 0;
                info = Loader.LoadFromStream(peStream, pdbStream, asmName);
            }
            catch (Exception ex)
            {
                sw.Stop();
                _engine.LogError($"Failed to load compiled assembly: {ex.Message}");

                // Attempt to restore previous state if this was a hot-reload
                if (isHotReload && stateSnapshot != null)
                {
                    _engine.LogWarning("Hot-reload failed. Previous assembly was already unloaded.");
                }

                var loadErrorDiag = new ScriptDiagnostic
                {
                    Severity = ScriptDiagnosticSeverity.Error,
                    Message = $"Assembly load failure: {ex.Message}",
                    Id = "SHANYU_LOAD",
                };
                var diags = new List<ScriptDiagnostic>(compileDiagnostics) { loadErrorDiag };

                var result = new CompileResult
                {
                    Success = false,
                    AssemblyName = source.AssemblyName,
                    Diagnostics = diags,
                    ErrorCount = errors + 1,
                    WarningCount = warnings,
                    CompileTime = sw.Elapsed,
                    WasHotReload = isHotReload,
                };
                OnCompileFinished?.Invoke(result);
                OnHotReloadFinished?.Invoke(result);
                return result;
            }

            // 4. Register types
            int typeCount = Registry.RegisterAssembly(info);
            _currentAssembly = info;
            _lastSource = source;

            // 5. Restore state (hot-reload)
            int restoredCount = 0;
            if (isHotReload && stateSnapshot != null)
            {
                restoredCount = Registry.RestoreState(stateSnapshot);
            }

            sw.Stop();

            var finalResult = new CompileResult
            {
                Success = true,
                Assembly = info,
                AssemblyName = source.AssemblyName,
                Diagnostics = compileDiagnostics,
                ErrorCount = 0,
                WarningCount = warnings,
                CompileTime = sw.Elapsed,
                WasHotReload = isHotReload,
            };

            if (isHotReload)
            {
                _engine.Log(
                    $"Hot-reload complete: {typeCount} type(s), {restoredCount} instance(s) restored " +
                    $"in {sw.Elapsed.TotalMilliseconds:0}ms.");
                OnHotReloadFinished?.Invoke(finalResult);
            }
            else
            {
                _engine.Log(
                    $"Script compilation complete: {typeCount} type(s) in {sw.Elapsed.TotalMilliseconds:0}ms.");
            }

            OnCompileFinished?.Invoke(finalResult);
            return finalResult;
        }

        /// <summary>
        /// Compiles a single C# source string.  Convenience wrapper around
        /// <see cref="Compile(ScriptSource)"/>.
        /// </summary>
        public CompileResult CompileSource(string source, string assemblyName = "ShanyuScripts",
            string filePath = "Script.cs")
        {
            return Compile(ScriptSource.FromString(source, assemblyName, filePath));
        }

        /// <summary>
        /// Compiles the given .cs files.  Convenience wrapper.
        /// </summary>
        public CompileResult CompileFiles(IEnumerable<string> filePaths,
            string assemblyName = "ShanyuScripts")
        {
            return Compile(ScriptSource.FromFiles(filePaths, assemblyName));
        }

        /// <summary>
        /// Compiles all .cs files in a directory (recursively).  Convenience wrapper.
        /// </summary>
        public CompileResult CompileDirectory(string directory,
            string searchPattern = "*.cs", string assemblyName = "ShanyuScripts")
        {
            return Compile(ScriptSource.FromDirectory(directory, searchPattern, assemblyName));
        }

        /// <summary>
        /// Recompiles the last source (stored from a prior <see cref="Compile"/> call),
        /// performing a hot-reload.  Useful after editing script files on disk if the
        /// source was originally loaded from files — but note that this recompiles the
        /// in-memory source text, it does NOT re-read files from disk.
        /// </summary>
        /// <returns>The compilation result, or null if there is no prior source.</returns>
        public CompileResult? HotReload()
        {
            if (_lastSource == null)
            {
                _engine.LogWarning("HotReload called but no prior compilation exists.");
                return null;
            }
            return Compile(_lastSource);
        }

        /// <summary>
        /// Re-reads script files from the last source's file paths and recompiles.
        /// This is the recommended way to hot-reload after editing files on disk.
        /// </summary>
        public CompileResult? HotReloadFromDisk()
        {
            if (_lastSource == null)
            {
                _engine.LogWarning("HotReloadFromDisk called but no prior compilation exists.");
                return null;
            }

            // Re-read files that have a valid path
            var files = new List<ScriptFile>();
            foreach (var file in _lastSource.Files)
            {
                if (!string.IsNullOrEmpty(file.Path) && File.Exists(file.Path))
                {
                    files.Add(ScriptFile.FromFile(file.Path));
                }
                else
                {
                    // Keep the in-memory content if the file doesn't exist on disk
                    files.Add(file);
                }
            }

            var source = new ScriptSource
            {
                AssemblyName = _lastSource.AssemblyName,
                Files = files,
                PreprocessorSymbols = _lastSource.PreprocessorSymbols,
            };

            return Compile(source);
        }

        // ── Engine tick integration ────────────────────────────────────────────────

        /// <summary>
        /// Call this from the engine's Update tick to dispatch Update and Start
        /// on all enabled script instances.
        /// </summary>
        public void Update(float deltaTime)
        {
            Registry.Update(deltaTime);
        }

        /// <summary>
        /// Call this from the engine's fixed-update tick to dispatch FixedUpdate
        /// on all enabled script instances.
        /// </summary>
        public void FixedUpdate(float fixedDeltaTime)
        {
            Registry.FixedUpdate(fixedDeltaTime);
        }

        /// <summary>
        /// Call this after the engine's Update tick to dispatch LateUpdate
        /// on all enabled script instances.
        /// </summary>
        public void LateUpdate(float deltaTime)
        {
            Registry.LateUpdate(deltaTime);
        }

        // ── Script type queries ────────────────────────────────────────────────────

        /// <summary>Returns all script types discovered in the current assembly.</summary>
        public IReadOnlyList<Type> GetScriptTypes()
        {
            return _currentAssembly?.ScriptTypes ?? Array.Empty<Type>();
        }

        /// <summary>Returns the names of all registered script types.</summary>
        public IReadOnlyList<string> GetScriptTypeNames()
        {
            return Registry.GetRegisteredTypeNames();
        }

        /// <summary>Finds a script type by name across loaded assemblies.</summary>
        public Type? FindScriptType(string typeName)
        {
            return Loader.FindScriptType(typeName);
        }

        // ── Reference management ───────────────────────────────────────────────────

        /// <summary>
        /// Invalidates the cached set of metadata references so that newly loaded
        /// assemblies are included in the next compilation.
        /// </summary>
        public void InvalidateReferences()
        {
            _cachedReferences = null;
        }

        // ── Internal compilation ───────────────────────────────────────────────────

        /// <summary>
        /// Compiles the source into in-memory PE and PDB streams using Roslyn.
        /// Returns the streams, diagnostics, and error/warning counts.
        /// </summary>
        private (MemoryStream pe, MemoryStream? pdb, List<ScriptDiagnostic> diags, int errors, int warnings)
            CompileToAssemblyData(ScriptSource source)
        {
            // Build syntax trees
            var parseOptions = new CSharpParseOptions(
                LanguageVersion,
                DocumentationMode.None,
                preprocessorSymbols: BuildPreprocessorSymbols(source));

            var syntaxTrees = new List<SyntaxTree>(source.Files.Count);
            foreach (var file in source.Files)
            {
                var sourceText = SourceText.From(file.Content);
                var tree = CSharpSyntaxTree.ParseText(sourceText, parseOptions, file.Path);
                syntaxTrees.Add(tree);
            }

            // Build references
            var references = GetCompilationReferences();

            // Build compilation options
            var compilationOptions = new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: OptimizationLevel,
                allowUnsafe: AllowUnsafe,
                nullableContextOptions: NullableContextOptions.Enable,
                deterministic: true
            ).WithSpecificDiagnosticOptions(BuildDiagnosticOptions());

            // Create the compilation
            var compilation = CSharpCompilation.Create(
                source.AssemblyName,
                syntaxTrees,
                references,
                compilationOptions
            );

            // Emit to memory
            var peStream = new MemoryStream();
            MemoryStream? pdbStream = null;
            EmitOptions? emitOptions = null;

            if (EmitDebugInfo)
            {
                pdbStream = new MemoryStream();
                emitOptions = new EmitOptions(
                    debugInformationFormat: DebugInformationFormat.PortablePdb);
            }

            EmitResult emitResult;
            try
            {
                emitResult = compilation.Emit(peStream, pdbStream, options: emitOptions);
            }
            catch (Exception ex)
            {
                // Catastrophic failure during emission
                var diag = new ScriptDiagnostic
                {
                    Severity = ScriptDiagnosticSeverity.Error,
                    Message = $"Emit failure: {ex.Message}",
                    Id = "SHANYU_EMIT",
                };
                return (peStream, pdbStream, new List<ScriptDiagnostic> { diag }, 1, 0);
            }

            // Collect diagnostics
            var diagnostics = emitResult.Diagnostics
                .Where(d => d.Severity >= DiagnosticSeverity.Hidden)
                .Select(ToScriptDiagnostic)
                .ToList();

            int errors = diagnostics.Count(d => d.Severity == ScriptDiagnosticSeverity.Error);
            int warnings = diagnostics.Count(d => d.Severity == ScriptDiagnosticSeverity.Warning);

            // If emit failed but there were no explicit error diagnostics, add a generic one
            if (!emitResult.Success && errors == 0)
            {
                diagnostics.Add(new ScriptDiagnostic
                {
                    Severity = ScriptDiagnosticSeverity.Error,
                    Message = "Compilation failed for unknown reasons.",
                    Id = "SHANYU_UNKNOWN",
                });
                errors = 1;
            }

            return (peStream, pdbStream, diagnostics, errors, warnings);
        }

        /// <summary>
        /// Builds the list of preprocessor symbols (TRACE, DEBUG, plus any custom ones).
        /// </summary>
        private IEnumerable<string> BuildPreprocessorSymbols(ScriptSource source)
        {
            var symbols = new List<string> { "TRACE", "DEBUG" };
            if (source.PreprocessorSymbols != null)
                symbols.AddRange(source.PreprocessorSymbols);
            return symbols.Distinct();
        }

        /// <summary>
        /// Builds the diagnostic option overrides (suppress noisy warnings).
        /// </summary>
        private static Dictionary<string, ReportDiagnostic> BuildDiagnosticOptions()
        {
            return new Dictionary<string, ReportDiagnostic>(StringComparer.Ordinal)
            {
                // Assembly version mismatch
                ["CS1701"] = ReportDiagnostic.Suppress,
                // Duplicate assembly reference
                ["CS1702"] = ReportDiagnostic.Suppress,
                // Type forwarded from multiple assemblies
                ["CS0436"] = ReportDiagnostic.Suppress,
            };
        }

        /// <summary>
        /// Collects all metadata references needed for compilation: the engine assembly,
        /// all loaded framework assemblies, and common runtime assemblies.
        /// </summary>
        private List<MetadataReference> GetCompilationReferences()
        {
            if (_cachedReferences != null) return _cachedReferences;

            var references = new List<MetadataReference>();
            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 1. Add all currently loaded assemblies (engine, framework, NuGet packages)
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                string path;
                try
                {
                    path = assembly.Location;
                }
                catch
                {
                    continue;
                }

                if (string.IsNullOrEmpty(path) || !File.Exists(path)) continue;
                if (!seenPaths.Add(path)) continue;

                try
                {
                    references.Add(MetadataReference.CreateFromFile(path));
                }
                catch
                {
                    // Some assemblies may not be readable as metadata; skip them.
                }
            }

            // 2. Ensure common framework assemblies from the runtime directory are included
            var runtimeDir = RuntimeEnvironment.GetRuntimeDirectory();
            if (!string.IsNullOrEmpty(runtimeDir) && Directory.Exists(runtimeDir))
            {
                var frameworkDlls = new[]
                {
                    "System.Runtime.dll",
                    "System.Collections.dll",
                    "System.Linq.dll",
                    "System.Numerics.dll",
                    "System.Numerics.Vectors.dll",
                    "System.Console.dll",
                    "System.Private.CoreLib.dll",
                    "System.Threading.dll",
                    "System.Reflection.dll",
                    "System.IO.FileSystem.dll",
                    "System.Text.RegularExpressions.dll",
                    "System.Runtime.InteropServices.dll",
                    "System.ObjectModel.dll",
                    "System.Text.Json.dll",
                    "netstandard.dll",
                };

                foreach (var name in frameworkDlls)
                {
                    var path = Path.Combine(runtimeDir, name);
                    if (File.Exists(path) && seenPaths.Add(path))
                    {
                        try
                        {
                            references.Add(MetadataReference.CreateFromFile(path));
                        }
                        catch
                        {
                            // Skip unreadable
                        }
                    }
                }
            }

            _cachedReferences = references;
            _engine.Log($"Compilation references: {references.Count} assembly reference(s).");
            return references;
        }

        /// <summary>
        /// Converts a Roslyn <see cref="Diagnostic"/> to a <see cref="ScriptDiagnostic"/>.
        /// </summary>
        private static ScriptDiagnostic ToScriptDiagnostic(Diagnostic diagnostic)
        {
            var severity = diagnostic.Severity switch
            {
                DiagnosticSeverity.Hidden => ScriptDiagnosticSeverity.Hidden,
                DiagnosticSeverity.Info => ScriptDiagnosticSeverity.Info,
                DiagnosticSeverity.Warning => ScriptDiagnosticSeverity.Warning,
                DiagnosticSeverity.Error => ScriptDiagnosticSeverity.Error,
                _ => ScriptDiagnosticSeverity.Info
            };

            var lineSpan = diagnostic.Location.GetMappedLineSpan();
            var filePath = lineSpan.Path;
            if (string.IsNullOrEmpty(filePath) && diagnostic.Location.SourceTree != null)
                filePath = diagnostic.Location.SourceTree.FilePath;

            return new ScriptDiagnostic
            {
                Severity = severity,
                Message = diagnostic.GetMessage(),
                Id = diagnostic.Id,
                FilePath = filePath,
                Line = lineSpan.IsValid ? lineSpan.StartLinePosition.Line + 1 : 0,
                Column = lineSpan.IsValid ? lineSpan.StartLinePosition.Character + 1 : 0,
            };
        }

        // ── Disposal ───────────────────────────────────────────────────────────────

        /// <summary>
        /// Destroys all script instances, unregisters types, unloads assemblies,
        /// and disposes sub-systems.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;

            Registry.Dispose();
            Loader.Dispose();
            _currentAssembly = null;
            _lastSource = null;
            _lastDiagnostics.Clear();
            _cachedReferences = null;

            _disposed = true;
            _engine.Log("ScriptEngine disposed.");
        }
    }
}
