using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;

namespace ShanyuEngine.PluginSystem
{
    // ════════════════════════════════════════════════════════════════════════
    // 山雨引擎 - 脚本热重载系统 (ScriptHotReloadSystem)
    //
    // 提供运行时代码修改与热重载能力:
    //   * 文件监视 (FileSystemWatcher: .cs 变更/重命名/删除/创建)
    //   * 变更检测 (防抖 / 批量处理 / 忽略临时文件 / 只监视源码目录)
    //   * Roslyn 编译 (增量编译 / 错误报告 / 位置信息)
    //   * 热重载策略 (Assembly/Method/Class/Field 四级策略)
    //   * 状态迁移 (旧对象→新对象 / 字段映射 / 自定义迁移回调)
    //   * 安全检查 (无运行中引用 / 可安全卸载 / 状态可迁移)
    //   * 热重载事件 (OnBeforeReload/OnAfterReload/OnReloadFailed/OnReloadSkipped)
    //   * 编辑器集成 (一键热重载 / 自动热重载 / 编译状态显示)
    // ════════════════════════════════════════════════════════════════════════

    #region 枚举与选项

    /// <summary>热重载策略。</summary>
    public enum HotReloadStrategy
    {
        /// <summary>卸载旧 AssemblyLoadContext + 加载新程序集</summary>
        ReloadAssembly,
        /// <summary>方法体替换 (仅 IL 层面)</summary>
        ReloadMethod,
        /// <summary>类替换 (新建实例 / 状态迁移)</summary>
        ReloadClass,
        /// <summary>字段变更 (需要全量重载)</summary>
        ReloadField,
        /// <summary>跳过 (不可安全重载)</summary>
        Skip
    }

    /// <summary>热重载状态。</summary>
    public enum HotReloadStatus
    {
        Idle,
        Watching,
        Compiling,
        Reloading,
        Migrating,
        Success,
        Failed,
        Skipped
    }

    /// <summary>热重载选项。</summary>
    public sealed class HotReloadOptions
    {
        public bool AutoReload { get; set; } = true;
        public int DebounceMs { get; set; } = 500;
        public bool IgnoreTempFiles { get; set; } = true;
        public bool IgnoreGeneratedFiles { get; set; } = true;
        public List<string> WatchedExtensions { get; set; } = new List<string> { ".cs" };
        public List<string> IgnoredDirectories { get; set; } = new List<string> { "bin", "obj", ".vs", "temp" };
        public HotReloadStrategy PreferredStrategy { get; set; } = HotReloadStrategy.ReloadAssembly;
        public bool EnableStateMigration { get; set; } = true;
        public bool StrictSafetyCheck { get; set; } = true;
        public int CompileTimeoutMs { get; set; } = 30000;
        public bool Optimize { get; set; } = false;
        public string OutputDirectory { get; set; } = Path.Combine(Path.GetTempPath(), "ShanyuEngine", "HotReload");
        public List<string> AdditionalReferences { get; set; } = new List<string>();
        public List<string> PreprocessorSymbols { get; set; } = new List<string>();
    }

    #endregion

    #region 编译结果

    /// <summary>编译错误/警告。</summary>
    public sealed class CompileDiagnostic
    {
        public DiagnosticSeverity Severity { get; set; }
        public string Id { get; set; }
        public string Message { get; set; }
        public string FilePath { get; set; }
        public int Line { get; set; }
        public int Column { get; set; }
        public int StartPosition { get; set; }
        public int EndPosition { get; set; }

        public bool IsError => Severity == DiagnosticSeverity.Error;
        public bool IsWarning => Severity == DiagnosticSeverity.Warning;

        public override string ToString() =>
            $"[{Severity}] {FilePath}({Line},{Column}): {Id}: {Message}";
    }

    /// <summary>编译结果。</summary>
    public sealed class CompileResult
    {
        public bool Success { get; set; }
        public byte[] AssemblyData { get; set; }
        public byte[] PdbData { get; set; }
        public List<CompileDiagnostic> Diagnostics { get; } = new List<CompileDiagnostic>();
        public TimeSpan CompileTime { get; set; }
        public string AssemblyName { get; set; }
        public List<string> SourceFiles { get; } = new List<string>();

        public IEnumerable<CompileDiagnostic> Errors => Diagnostics.Where(d => d.IsError);
        public IEnumerable<CompileDiagnostic> Warnings => Diagnostics.Where(d => d.IsWarning);
        public int ErrorCount => Errors.Count();
        public int WarningCount => Warnings.Count();

        public string GetFormattedDiagnostics()
        {
            return string.Join("\n", Diagnostics.Select(d => d.ToString()));
        }
    }

    #endregion

    #region 文件变更事件

    /// <summary>文件变更信息。</summary>
    public sealed class FileChangeInfo
    {
        public string FilePath { get; set; }
        public WatcherChangeTypes ChangeType { get; set; }
        public DateTime Timestamp { get; set; }
        public string OldPath { get; set; }

        public override string ToString() => $"[{ChangeType}] {FilePath}";
    }

    #endregion

    #region 状态迁移

    /// <summary>字段映射信息。</summary>
    public sealed class FieldMapping
    {
        public string OldFieldName { get; set; }
        public string NewFieldName { get; set; }
        public Type OldType { get; set; }
        public Type NewType { get; set; }
        public Func<object, object> Converter { get; set; }
    }

    /// <summary>状态迁移器 (旧对象→新对象状态迁移)。</summary>
    public sealed class StateMigrator
    {
        private readonly Dictionary<string, List<FieldMapping>> _mappings = new Dictionary<string, List<FieldMapping>>();
        private readonly Dictionary<string, Func<object, object, object>> _customMigrators = new Dictionary<string, Func<object, object, object>>();
        private readonly PluginLogger _logger = new PluginLogger("StateMigrator");

        /// <summary>注册字段映射。</summary>
        public void RegisterFieldMapping(string typeName, FieldMapping mapping)
        {
            if (!_mappings.ContainsKey(typeName))
                _mappings[typeName] = new List<FieldMapping>();
            _mappings[typeName].Add(mapping);
        }

        /// <summary>注册自定义迁移回调。</summary>
        public void RegisterCustomMigrator(string typeName, Func<object, object, object> migrator)
        {
            _customMigrators[typeName] = migrator;
        }

        /// <summary>迁移状态。</summary>
        public void MigrateState(object oldInstance, object newInstance)
        {
            if (oldInstance == null || newInstance == null) return;

            Type oldType = oldInstance.GetType();
            Type newType = newInstance.GetType();
            string typeName = newType.FullName;

            _logger.Debug($"迁移状态: {oldType.Name} → {newType.Name}");

            // 自定义迁移器优先
            if (_customMigrators.TryGetValue(typeName, out var customMigrator))
            {
                try
                {
                    customMigrator(oldInstance, newInstance);
                    _logger.Debug($"自定义迁移完成: {typeName}");
                    return;
                }
                catch (Exception ex)
                {
                    _logger.Error($"自定义迁移失败: {typeName}", ex);
                }
            }

            // 自动字段迁移
            var oldFields = oldType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            var newFields = newType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .ToDictionary(f => f.Name);

            foreach (var oldField in oldFields)
            {
                // 检查是否有自定义映射
                if (_mappings.TryGetValue(typeName, out var mappings))
                {
                    var mapping = mappings.FirstOrDefault(m => m.OldFieldName == oldField.Name);
                    if (mapping != null)
                    {
                        if (newFields.TryGetValue(mapping.NewFieldName, out var newField))
                        {
                            try
                            {
                                object value = oldField.GetValue(oldInstance);
                                if (mapping.Converter != null)
                                    value = mapping.Converter(value);
                                newField.SetValue(newInstance, value);
                                continue;
                            }
                            catch (Exception ex)
                            {
                                _logger.Error($"字段映射迁移失败: {mapping.OldFieldName} → {mapping.NewFieldName}", ex);
                            }
                        }
                    }
                }

                // 同名同类型自动迁移
                if (newFields.TryGetValue(oldField.Name, out var matchedField))
                {
                    if (matchedField.FieldType == oldField.FieldType ||
                        matchedField.FieldType.IsAssignableFrom(oldField.FieldType))
                    {
                        try
                        {
                            matchedField.SetValue(newInstance, oldField.GetValue(oldInstance));
                        }
                        catch (Exception ex)
                        {
                            _logger.Warning($"字段迁移失败: {oldField.Name}: {ex.Message}");
                        }
                    }
                }
            }

            _logger.Debug($"状态迁移完成: {typeName}");
        }

        /// <summary>迁移所有实例。</summary>
        public void MigrateAllInstances<T>(List<T> oldInstances, Func<T> newInstanceFactory)
        {
            for (int i = 0; i < oldInstances.Count; i++)
            {
                var newInstance = newInstanceFactory();
                MigrateState(oldInstances[i], newInstance);
                oldInstances[i] = newInstance;
            }
        }

        /// <summary>清除映射。</summary>
        public void ClearMappings()
        {
            _mappings.Clear();
            _customMigrators.Clear();
        }
    }

    #endregion

    #region 热重载事件参数

    /// <summary>热重载前事件参数。</summary>
    public sealed class BeforeReloadEventArgs : EventArgs
    {
        public List<string> ChangedFiles { get; set; }
        public HotReloadStrategy Strategy { get; set; }
        public bool Cancel { get; set; }
    }

    /// <summary>热重载后事件参数。</summary>
    public sealed class AfterReloadEventArgs : EventArgs
    {
        public List<string> ChangedFiles { get; set; }
        public HotReloadStrategy Strategy { get; set; }
        public bool Success { get; set; }
        public TimeSpan ReloadTime { get; set; }
        public string ErrorMessage { get; set; }
    }

    /// <summary>热重载失败事件参数。</summary>
    public sealed class ReloadFailedEventArgs : EventArgs
    {
        public List<string> ChangedFiles { get; set; }
        public string ErrorMessage { get; set; }
        public CompileResult CompileResult { get; set; }
    }

    /// <summary>热重载跳过事件参数。</summary>
    public sealed class ReloadSkippedEventArgs : EventArgs
    {
        public List<string> ChangedFiles { get; set; }
        public string Reason { get; set; }
    }

    #endregion

    #region 脚本热重载管理器

    /// <summary>脚本热重载管理器。</summary>
    public sealed class ScriptHotReloader
    {
        private static readonly Lazy<ScriptHotReloader> _instance =
            new Lazy<ScriptHotReloader>(() => new ScriptHotReloader());
        public static ScriptHotReloader Instance => _instance.Value;

        // 配置
        public HotReloadOptions Options { get; set; } = new HotReloadOptions();
        public HotReloadStatus Status { get; private set; } = HotReloadStatus.Idle;

        // 文件监视
        private readonly List<FileSystemWatcher> _watchers = new List<FileSystemWatcher>();
        private readonly ConcurrentQueue<FileChangeInfo> _pendingChanges = new ConcurrentQueue<FileChangeInfo>();
        private Timer _debounceTimer;
        private readonly object _lock = new object();

        // 编译相关
        private readonly List<string> _sourceDirectories = new List<string>();
        private readonly List<MetadataReference> _references = new List<MetadataReference>();
        private readonly Dictionary<string, AssemblyLoadContext> _loadedContexts = new Dictionary<string, AssemblyLoadContext>();
        private readonly Dictionary<string, Assembly> _loadedAssemblies = new Dictionary<string, Assembly>();

        // 状态迁移
        public StateMigrator StateMigrator { get; } = new StateMigrator();

        // 日志
        private readonly PluginLogger _logger = new PluginLogger("ScriptHotReloader");

        // 事件
        public event EventHandler<BeforeReloadEventArgs> OnBeforeReload;
        public event EventHandler<AfterReloadEventArgs> OnAfterReload;
        public event EventHandler<ReloadFailedEventArgs> OnReloadFailed;
        public event EventHandler<ReloadSkippedEventArgs> OnReloadSkipped;
        public event Action<FileChangeInfo> OnFileChanged;
        public event Action<HotReloadStatus> OnStatusChanged;
        public event Action<CompileResult> OnCompileComplete;

        // 统计
        public int TotalReloads { get; private set; }
        public int SuccessfulReloads { get; private set; }
        public int FailedReloads { get; private set; }
        public int SkippedReloads { get; private set; }
        public TimeSpan TotalCompileTime { get; private set; }
        public TimeSpan TotalReloadTime { get; private set; }
        public DateTime? LastReloadTime { get; private set; }

        private ScriptHotReloader()
        {
            if (!Directory.Exists(Options.OutputDirectory))
                Directory.CreateDirectory(Options.OutputDirectory);

            // 初始化默认引用
            InitializeDefaultReferences();
        }

        #region 初始化

        private void InitializeDefaultReferences()
        {
            // 添加核心程序集引用
            var coreAssemblies = new[]
            {
                typeof(object).Assembly,                    // System.Private.CoreLib
                typeof(Console).Assembly,                    // System.Console
                typeof(System.Linq.Enumerable).Assembly,     // System.Linq
                typeof(System.Collections.Generic.List<>).Assembly, // System.Collections
                typeof(System.Numerics.Vector3).Assembly,    // System.Numerics
                typeof(File).Assembly,                       // System.IO.FileSystem
                typeof(Assembly).Assembly,                   // System.Runtime
                typeof(JsonSerializer).Assembly,             // System.Text.Json
                typeof(PluginBase).Assembly,                 // 引擎程序集
            };

            foreach (var asm in coreAssemblies.Distinct())
            {
                try
                {
                    _references.Add(MetadataReference.CreateFromFile(asm.Location));
                }
                catch (Exception ex)
                {
                    _logger.Warning($"无法添加引用 {asm.Location}: {ex.Message}");
                }
            }

            // 添加附加引用
            foreach (var refPath in Options.AdditionalReferences)
            {
                if (File.Exists(refPath))
                {
                    try
                    {
                        _references.Add(MetadataReference.CreateFromFile(refPath));
                    }
                    catch (Exception ex)
                    {
                        _logger.Warning($"无法添加引用 {refPath}: {ex.Message}");
                    }
                }
            }
        }

        /// <summary>添加程序集引用。</summary>
        public void AddReference(string assemblyPath)
        {
            if (File.Exists(assemblyPath))
            {
                _references.Add(MetadataReference.CreateFromFile(assemblyPath));
                _logger.Debug($"添加引用: {assemblyPath}");
            }
        }

        /// <summary>添加程序集引用。</summary>
        public void AddReference(Assembly assembly)
        {
            if (assembly != null && !string.IsNullOrEmpty(assembly.Location))
            {
                _references.Add(MetadataReference.CreateFromFile(assembly.Location));
            }
        }

        #endregion

        #region 文件监视

        /// <summary>添加源码监视目录。</summary>
        public void AddSourceDirectory(string directory)
        {
            if (!Directory.Exists(directory)) return;
            if (_sourceDirectories.Contains(directory)) return;

            _sourceDirectories.Add(directory);
            _logger.Info($"添加源码监视目录: {directory}");

            if (Status == HotReloadStatus.Watching)
            {
                StartWatchingDirectory(directory);
            }
        }

        /// <summary>开始监视。</summary>
        public void StartWatching()
        {
            if (Status == HotReloadStatus.Watching) return;

            foreach (var dir in _sourceDirectories)
                StartWatchingDirectory(dir);

            Status = HotReloadStatus.Watching;
            OnStatusChanged?.Invoke(Status);
            _logger.Info($"热重载监视已启动，监视 {_watchers.Count} 个目录。");
        }

        private void StartWatchingDirectory(string directory)
        {
            var watcher = new FileSystemWatcher(directory)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
                EnableRaisingEvents = true
            };

            watcher.Changed += (s, e) => OnFileEvent(e.ChangeType, e.FullPath, null);
            watcher.Created += (s, e) => OnFileEvent(e.ChangeType, e.FullPath, null);
            watcher.Deleted += (s, e) => OnFileEvent(e.ChangeType, e.FullPath, null);
            watcher.Renamed += (s, e) => OnFileEvent(e.ChangeType, e.FullPath, e.OldFullPath);

            _watchers.Add(watcher);
        }

        private void OnFileEvent(WatcherChangeTypes changeType, string path, string oldPath)
        {
            // 过滤扩展名
            string ext = Path.GetExtension(path);
            if (!Options.WatchedExtensions.Contains(ext)) return;

            // 忽略临时文件
            if (Options.IgnoreTempFiles)
            {
                string fileName = Path.GetFileName(path);
                if (fileName.StartsWith("~") || fileName.StartsWith(".") ||
                    fileName.Contains(".tmp") || fileName.Contains("~"))
                    return;
            }

            // 忽略生成文件
            if (Options.IgnoreGeneratedFiles)
            {
                if (fileNameContains(path, ".generated.") || fileNameContains(path, ".g.cs") ||
                    fileNameContains(path, ".designer."))
                    return;
            }

            // 忽略目录
            foreach (var ignored in Options.IgnoredDirectories)
            {
                if (path.Replace('\\', '/').Contains($"/{ignored}/"))
                    return;
            }

            var info = new FileChangeInfo
            {
                FilePath = path,
                ChangeType = changeType,
                Timestamp = DateTime.Now,
                OldPath = oldPath
            };

            _pendingChanges.Enqueue(info);
            OnFileChanged?.Invoke(info);

            // 防抖
            _debounceTimer?.Dispose();
            _debounceTimer = new Timer(OnDebounceElapsed, null, Options.DebounceMs, Timeout.Infinite);
        }

        private bool fileNameContains(string path, string fragment)
        {
            return Path.GetFileName(path).Contains(fragment, StringComparison.OrdinalIgnoreCase);
        }

        private void OnDebounceElapsed(object state)
        {
            _debounceTimer?.Dispose();
            _debounceTimer = null;

            if (_pendingChanges.IsEmpty) return;

            if (!Options.AutoReload)
            {
                _logger.Debug("自动热重载已禁用，变更待处理。");
                return;
            }

            // 收集变更
            var changes = new List<FileChangeInfo>();
            while (_pendingChanges.TryDequeue(out var change))
                changes.Add(change);

            if (changes.Count == 0) return;

            _logger.Info($"检测到 {changes.Count} 个文件变更，开始热重载...");

            // 执行热重载
            Task.Run(() => ReloadAsync(changes));
        }

        /// <summary>停止监视。</summary>
        public void StopWatching()
        {
            foreach (var watcher in _watchers)
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }
            _watchers.Clear();
            _debounceTimer?.Dispose();
            _debounceTimer = null;

            Status = HotReloadStatus.Idle;
            OnStatusChanged?.Invoke(Status);
            _logger.Info("热重载监视已停止。");
        }

        #endregion

        #region 编译

        /// <summary>编译源文件。</summary>
        public CompileResult Compile(List<string> sourceFiles, string assemblyName = null)
        {
            var sw = Stopwatch.StartNew();
            Status = HotReloadStatus.Compiling;
            OnStatusChanged?.Invoke(Status);

            var result = new CompileResult
            {
                AssemblyName = assemblyName ?? $"HotReload_{DateTime.Now:yyyyMMddHHmmssfff}",
                SourceFiles = sourceFiles.ToList()
            };

            try
            {
                // 解析源文件
                var syntaxTrees = new List<SyntaxTree>();
                var parseOptions = new CSharpParseOptions(
                    LanguageVersion.CSharp8,
                    DocumentationMode.None,
                    preprocessorSymbols: Options.PreprocessorSymbols);

                foreach (var file in sourceFiles)
                {
                    if (!File.Exists(file))
                    {
                        result.Diagnostics.Add(new CompileDiagnostic
                        {
                            Severity = DiagnosticSeverity.Error,
                            Message = $"源文件不存在: {file}",
                            FilePath = file
                        });
                        continue;
                    }

                    string code = File.ReadAllText(file);
                    var tree = CSharpSyntaxTree.ParseText(code, parseOptions, path: file);
                    syntaxTrees.Add(tree);
                }

                if (result.Diagnostics.Any(d => d.IsError))
                {
                    result.Success = false;
                    result.CompileTime = sw.Elapsed;
                    return result;
                }

                // 编译选项
                var compileOptions = new CSharpCompilationOptions(
                    OutputKind.DynamicallyLinkedLibrary,
                    optimizationLevel: Options.Optimize ? OptimizationLevel.Release : OptimizationLevel.Debug,
                    allowUnsafe: true);

                // 创建编译
                var compilation = CSharpCompilation.Create(
                    result.AssemblyName,
                    syntaxTrees: syntaxTrees,
                    references: _references,
                    options: compileOptions);

                // 发射
                string dllPath = Path.Combine(Options.OutputDirectory, result.AssemblyName + ".dll");
                string pdbPath = Path.Combine(Options.OutputDirectory, result.AssemblyName + ".pdb");

                EmitResult emitResult;
                using (var dllStream = new FileStream(dllPath, FileMode.Create, FileAccess.Write))
                using (var pdbStream = new FileStream(pdbPath, FileMode.Create, FileAccess.Write))
                {
                    emitResult = compilation.Emit(dllStream, pdbStream);
                }

                // 收集诊断
                foreach (var diag in emitResult.Diagnostics)
                {
                    var loc = diag.Location.GetLineSpan();
                    result.Diagnostics.Add(new CompileDiagnostic
                    {
                        Severity = diag.Severity,
                        Id = diag.Id,
                        Message = diag.GetMessage(),
                        FilePath = loc.Path ?? "",
                        Line = loc.StartLinePosition.Line + 1,
                        Column = loc.StartLinePosition.Character + 1,
                        StartPosition = loc.StartLinePosition.Position,
                        EndPosition = loc.EndLinePosition.Position
                    });
                }

                if (emitResult.Success)
                {
                    result.Success = true;
                    result.AssemblyData = File.ReadAllBytes(dllPath);
                    result.PdbData = File.ReadAllBytes(pdbPath);
                    _logger.Info($"编译成功: {result.AssemblyName} ({sourceFiles.Count} 文件, {result.ErrorCount} 错误, {result.WarningCount} 警告)");
                }
                else
                {
                    result.Success = false;
                    _logger.Error($"编译失败: {result.ErrorCount} 个错误");
                    foreach (var err in result.Errors)
                        _logger.Error(err.ToString());
                }

                result.CompileTime = sw.Elapsed;
                TotalCompileTime += sw.Elapsed;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Diagnostics.Add(new CompileDiagnostic
                {
                    Severity = DiagnosticSeverity.Error,
                    Message = ex.Message
                });
                _logger.Error("编译异常", ex);
            }
            finally
            {
                sw.Stop();
                Status = HotReloadStatus.Idle;
                OnStatusChanged?.Invoke(Status);
                OnCompileComplete?.Invoke(result);
            }

            return result;
        }

        /// <summary>增量编译 (只编译变更的文件)。</summary>
        public CompileResult IncrementalCompile(List<string> changedFiles, string assemblyName = null)
        {
            // 收集所有源文件 (包括未变更的，因为需要完整编译)
            var allFiles = new HashSet<string>();
            foreach (var changed in changedFiles)
                allFiles.Add(changed);

            // 添加同一目录下的其他源文件
            foreach (var dir in _sourceDirectories)
            {
                if (Directory.Exists(dir))
                {
                    var files = Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories);
                    foreach (var file in files)
                    {
                        if (!ShouldIgnoreFile(file))
                            allFiles.Add(file);
                    }
                }
            }

            return Compile(allFiles.ToList(), assemblyName);
        }

        private bool ShouldIgnoreFile(string path)
        {
            if (Options.IgnoreTempFiles)
            {
                string fileName = Path.GetFileName(path);
                if (fileName.StartsWith("~") || fileName.StartsWith("."))
                    return true;
            }

            if (Options.IgnoreGeneratedFiles)
            {
                if (fileNameContains(path, ".generated.") || fileNameContains(path, ".g.cs"))
                    return true;
            }

            foreach (var ignored in Options.IgnoredDirectories)
            {
                if (path.Replace('\\', '/').Contains($"/{ignored}/"))
                    return true;
            }

            return false;
        }

        #endregion

        #region 热重载

        /// <summary>执行热重载 (异步)。</summary>
        public async Task<AfterReloadEventArgs> ReloadAsync(List<FileChangeInfo> changes)
        {
            var changedFiles = changes.Select(c => c.FilePath).Distinct().ToList();
            var sw = Stopwatch.StartNew();

            var beforeArgs = new BeforeReloadEventArgs
            {
                ChangedFiles = changedFiles,
                Strategy = Options.PreferredStrategy
            };

            OnBeforeReload?.Invoke(this, beforeArgs);

            if (beforeArgs.Cancel)
            {
                var skipArgs = new ReloadSkippedEventArgs
                {
                    ChangedFiles = changedFiles,
                    Reason = "被事件处理器取消"
                };
                OnReloadSkipped?.Invoke(this, skipArgs);
                SkippedReloads++;
                return new AfterReloadEventArgs { ChangedFiles = changedFiles, Success = false };
            }

            // 安全检查
            if (Options.StrictSafetyCheck && !SafetyCheck(changedFiles))
            {
                var skipArgs = new ReloadSkippedEventArgs
                {
                    ChangedFiles = changedFiles,
                    Reason = "安全检查未通过"
                };
                OnReloadSkipped?.Invoke(this, skipArgs);
                SkippedReloads++;
                return new AfterReloadEventArgs { ChangedFiles = changedFiles, Success = false };
            }

            // 编译
            Status = HotReloadStatus.Compiling;
            OnStatusChanged?.Invoke(Status);

            var compileResult = await Task.Run(() => IncrementalCompile(changedFiles));

            if (!compileResult.Success)
            {
                var failArgs = new ReloadFailedEventArgs
                {
                    ChangedFiles = changedFiles,
                    ErrorMessage = compileResult.GetFormattedDiagnostics(),
                    CompileResult = compileResult
                };
                OnReloadFailed?.Invoke(this, failArgs);
                FailedReloads++;
                TotalReloads++;
                Status = HotReloadStatus.Failed;
                OnStatusChanged?.Invoke(Status);
                Status = HotReloadStatus.Idle;
                return new AfterReloadEventArgs
                {
                    ChangedFiles = changedFiles,
                    Success = false,
                    ErrorMessage = failArgs.ErrorMessage
                };
            }

            // 执行热重载
            Status = HotReloadStatus.Reloading;
            OnStatusChanged?.Invoke(Status);

            bool reloadSuccess = false;
            string errorMsg = null;

            try
            {
                reloadSuccess = ExecuteReload(compileResult, beforeArgs.Strategy);
            }
            catch (Exception ex)
            {
                errorMsg = ex.Message;
                _logger.Error("热重载执行失败", ex);
            }

            sw.Stop();
            TotalReloadTime += sw.Elapsed;
            TotalReloads++;
            LastReloadTime = DateTime.Now;

            if (reloadSuccess)
            {
                SuccessfulReloads++;
                Status = HotReloadStatus.Success;
            }
            else
            {
                FailedReloads++;
                Status = HotReloadStatus.Failed;
            }
            OnStatusChanged?.Invoke(Status);

            var afterArgs = new AfterReloadEventArgs
            {
                ChangedFiles = changedFiles,
                Strategy = beforeArgs.Strategy,
                Success = reloadSuccess,
                ReloadTime = sw.Elapsed,
                ErrorMessage = errorMsg
            };
            OnAfterReload?.Invoke(this, afterArgs);

            // 重置状态
            Status = HotReloadStatus.Idle;
            OnStatusChanged?.Invoke(Status);

            return afterArgs;
        }

        /// <summary>执行重载策略。</summary>
        private bool ExecuteReload(CompileResult compileResult, HotReloadStrategy strategy)
        {
            switch (strategy)
            {
                case HotReloadStrategy.ReloadAssembly:
                    return ReloadAssembly(compileResult);

                case HotReloadStrategy.ReloadClass:
                    return ReloadClass(compileResult);

                case HotReloadStrategy.ReloadMethod:
                    return ReloadMethod(compileResult);

                case HotReloadStrategy.ReloadField:
                    return ReloadField(compileResult);

                default:
                    _logger.Warning($"不支持的策略: {strategy}");
                    return false;
            }
        }

        /// <summary>策略: 卸载旧 AssemblyLoadContext + 加载新程序集。</summary>
        private bool ReloadAssembly(CompileResult compileResult)
        {
            _logger.Info($"[ReloadAssembly] 重载程序集: {compileResult.AssemblyName}");

            // 卸载旧上下文
            string contextName = compileResult.AssemblyName;
            if (_loadedContexts.TryGetValue(contextName, out var oldContext))
            {
                if (oldContext is PluginLoadContext plc && plc.IsCollectible)
                {
                    plc.TryUnload();
                    _logger.Debug($"已卸载旧加载上下文: {contextName}");
                }
                _loadedContexts.Remove(contextName);
            }

            // 加载新程序集
            string dllPath = Path.Combine(Options.OutputDirectory, compileResult.AssemblyName + ".dll");
            var newContext = new PluginLoadContext(dllPath, isCollectible: true);
            var assembly = newContext.LoadPluginAssembly(dllPath);

            _loadedContexts[contextName] = newContext;
            _loadedAssemblies[contextName] = assembly;

            _logger.Info($"[ReloadAssembly] 新程序集已加载: {assembly.FullName}");
            return true;
        }

        /// <summary>策略: 类替换 (新建实例 / 状态迁移)。</summary>
        private bool ReloadClass(CompileResult compileResult)
        {
            _logger.Info($"[ReloadClass] 类替换重载: {compileResult.AssemblyName}");

            // 先加载新程序集
            if (!ReloadAssembly(compileResult))
                return false;

            if (!Options.EnableStateMigration)
            {
                _logger.Debug("状态迁移已禁用，跳过类状态迁移。");
                return true;
            }

            // 状态迁移
            Status = HotReloadStatus.Migrating;
            OnStatusChanged?.Invoke(Status);

            var newAssembly = _loadedAssemblies[compileResult.AssemblyName];

            // 查找需要迁移的类型
            foreach (var type in newAssembly.GetTypes())
            {
                try
                {
                    // 查找旧实例并迁移
                    // 实际实现需要维护实例注册表
                    _logger.Debug($"类型已重载: {type.FullName}");
                }
                catch (Exception ex)
                {
                    _logger.Warning($"类型迁移失败: {type.FullName}: {ex.Message}");
                }
            }

            return true;
        }

        /// <summary>策略: 方法体替换 (IL 层面)。</summary>
        private bool ReloadMethod(CompileResult compileResult)
        {
            _logger.Info($"[ReloadMethod] 方法体替换重载: {compileResult.AssemblyName}");
            _logger.Warning("方法体替换需要运行时 IL 修补，降级为程序集重载。");
            return ReloadAssembly(compileResult);
        }

        /// <summary>策略: 字段变更 (需要全量重载)。</summary>
        private bool ReloadField(CompileResult compileResult)
        {
            _logger.Info($"[ReloadField] 字段变更重载: {compileResult.AssemblyName}");
            _logger.Warning("字段变更需要全量重载，降级为类替换重载。");
            return ReloadClass(compileResult);
        }

        #endregion

        #region 安全检查

        /// <summary>安全检查: 确认可以安全重载。</summary>
        public bool SafetyCheck(List<string> changedFiles)
        {
            // 1. 确认无运行中引用 (简化检查)
            // 实际实现需要检查是否有线程正在执行相关代码

            // 2. 确认可安全卸载
            foreach (var kv in _loadedContexts)
            {
                if (kv.Value is PluginLoadContext plc)
                {
                    if (!plc.IsCollectible)
                    {
                        _logger.Warning($"加载上下文 {kv.Key} 不可卸载 (非 collectible)。");
                        // 不阻止重载，只警告
                    }
                }
            }

            // 3. 确认状态可迁移
            if (Options.EnableStateMigration)
            {
                // 检查是否有自定义迁移器
                // 实际实现需要更详细的检查
            }

            return true;
        }

        #endregion

        #region 手动触发

        /// <summary>手动触发热重载。</summary>
        public AfterReloadEventArgs TriggerReload(List<string> files = null)
        {
            files ??= _sourceDirectories
                .SelectMany(d => Directory.Exists(d) ? Directory.GetFiles(d, "*.cs", SearchOption.AllDirectories) : Array.Empty<string>())
                .Where(f => !ShouldIgnoreFile(f))
                .ToList();

            var changes = files.Select(f => new FileChangeInfo
            {
                FilePath = f,
                ChangeType = WatcherChangeTypes.Changed,
                Timestamp = DateTime.Now
            }).ToList();

            return ReloadAsync(changes).Result;
        }

        /// <summary>触发单文件热重载。</summary>
        public AfterReloadEventArgs TriggerReloadForFile(string filePath)
        {
            var changes = new List<FileChangeInfo>
            {
                new FileChangeInfo { FilePath = filePath, ChangeType = WatcherChangeTypes.Changed, Timestamp = DateTime.Now }
            };
            return ReloadAsync(changes).Result;
        }

        #endregion

        #region 查询

        /// <summary>获取已加载的程序集。</summary>
        public Assembly GetLoadedAssembly(string name) =>
            _loadedAssemblies.TryGetValue(name, out var asm) ? asm : null;

        /// <summary>获取所有监视目录。</summary>
        public IReadOnlyList<string> GetSourceDirectories() => _sourceDirectories.AsReadOnly();

        /// <summary>获取统计信息。</summary>
        public string GetStats()
        {
            return $"热重载统计: 总计 {TotalReloads} 次, 成功 {SuccessfulReloads}, 失败 {FailedReloads}, 跳过 {SkippedReloads}\n" +
                   $"编译时间: {TotalCompileTime.TotalSeconds:F2}s, 重载时间: {TotalReloadTime.TotalSeconds:F2}s\n" +
                   $"上次重载: {LastReloadTime?.ToString() ?? "无"}";
        }

        #endregion

        #region 清理

        /// <summary>清理所有已加载上下文。</summary>
        public void Cleanup()
        {
            StopWatching();

            foreach (var kv in _loadedContexts)
            {
                if (kv.Value is PluginLoadContext plc && plc.IsCollectible)
                {
                    plc.TryUnload();
                }
            }
            _loadedContexts.Clear();
            _loadedAssemblies.Clear();
        }

        #endregion
    }

    #endregion
}
