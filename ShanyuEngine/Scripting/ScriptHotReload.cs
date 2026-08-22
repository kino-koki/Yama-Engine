// ==================================================================================================
//  ScriptHotReload.cs — 山雨引擎 C# 脚本热重载系统
// ==================================================================================================
//  基于 AssemblyLoadContext (collectible) 实现运行时脚本热重载，无需重启编辑器。
//  
//  核心设计：
//  1. ScriptAssemblyLoader 已提供 Load/Unload 能力（collectible ALC）
//  2. ScriptHotReload 负责：文件监听 -> 增量编译 -> 卸载旧 ALC -> 加载新 ALC -> 状态迁移
//  3. 状态迁移：SerializeField 标记字段在重载前后保持数值（通过反射序列化/反序列化）
//  4. 事件通知：OnScriptReloaded / OnReloadFailed 供编辑器/运行时订阅
//  5. 防抖动：文件变更聚合（默认 300ms）避免频繁触发
//  6. 安全回滚：编译失败时保留旧程序集，不中断运行
//
//  依赖：ShanyuEngine.Scripting.ScriptAssemblyLoader, ScriptComponent, ScriptEngine
//  目标框架：net8.0
// ==================================================================================================

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;

namespace ShanyuEngine.Scripting
{
    // ================================================================================================
    // 热重载事件参数
    // ================================================================================================

    /// <summary>脚本重载完成事件参数。</summary>
    public sealed class ScriptReloadedEventArgs : EventArgs
    {
        /// <summary>重载的程序集名称。</summary>
        public string AssemblyName { get; init; } = string.Empty;

        /// <summary>新加载的程序集信息。</summary>
        public LoadedAssemblyInfo? NewAssemblyInfo { get; init; }

        /// <summary>旧程序集信息（已卸载）。</summary>
        public LoadedAssemblyInfo? OldAssemblyInfo { get; init; }

        /// <summary>受影响的脚本类型列表。</summary>
        public IReadOnlyList<Type> AffectedTypes { get; init; } = Array.Empty<Type>();

        /// <summary>实例迁移统计：成功迁移的实例数。</summary>
        public int MigratedInstanceCount { get; init; }

        /// <summary>实例迁移统计：失败的实例数。</summary>
        public int FailedInstanceCount { get; init; }

        /// <summary>耗时（毫秒）。</summary>
        public long ElapsedMs { get; init; }
    }

    /// <summary>脚本重载失败事件参数。</summary>
    public sealed class ScriptReloadFailedEventArgs : EventArgs
    {
        /// <summary>尝试重载的程序集名称。</summary>
        public string AssemblyName { get; init; } = string.Empty;

        /// <summary>编译诊断信息（错误/警告）。</summary>
        public IReadOnlyList<ScriptDiagnostic> Diagnostics { get; init; } = Array.Empty<ScriptDiagnostic>();

        /// <summary>异常信息（如有）。</summary>
        public Exception? Exception { get; init; }

        /// <summary>是否已回滚到旧版本。</summary>
        public bool RolledBack { get; init; }
    }

    /// <summary>脚本文件变更事件参数。</summary>
    public sealed class ScriptFileChangedEventArgs : EventArgs
    {
        /// <summary>变更的文件路径（相对于脚本项目根目录）。</summary>
        public string RelativePath { get; init; } = string.Empty;

        /// <summary>变更类型。</summary>
        public WatcherChangeTypes ChangeType { get; init; }

        /// <summary>所属程序集名称（推断）。</summary>
        public string? AssemblyName { get; init; }
    }

    // ================================================================================================
    // 实例状态快照（用于热重载时保持字段值）
    // ================================================================================================

    /// <summary>单个脚本组件实例的字段状态快照。</summary>
    public sealed class ScriptInstanceState
    {
        /// <summary>脚本类型全名（旧版本）。</summary>
        public string TypeFullName { get; set; } = string.Empty;

        /// <summary>实例在 ECS 中的 Entity ID（用于重新关联）。</summary>
        public Guid EntityId { get; set; }

        /// <summary>被 [SerializeField] 标记的字段值字典。</summary>
        public Dictionary<string, object?> SerializedFields { get; set; } = new();

        /// <summary>公共字段/属性值（可选，用于无 SerializeField 的情况）。</summary>
        public Dictionary<string, object?> PublicFields { get; set; } = new();
    }

    /// <summary>程序集级别的状态快照，包含所有脚本实例状态。</summary>
    public sealed class AssemblyStateSnapshot
    {
        public string AssemblyName { get; set; } = string.Empty;
        public List<ScriptInstanceState> Instances { get; set; } = new();
        public DateTime CapturedAt { get; set; } = DateTime.UtcNow;
    }

    // ================================================================================================
    // 热重载配置
    // ================================================================================================

    /// <summary>热重载系统配置选项。</summary>
    public sealed class HotReloadOptions
    {
        /// <summary>脚本项目根目录（包含 .csproj 的目录）。</summary>
        public string ScriptProjectRoot { get; set; } = string.Empty;

        /// <summary>编译输出目录（相对于 ScriptProjectRoot，默认 bin/Debug/net8.0）。</summary>
        public string OutputPath { get; set; } = "bin/Debug/net8.0";

        /// <summary>文件变更防抖动延迟（毫秒），默认 300ms。</summary>
        public int DebounceMs { get; set; } = 300;

        /// <summary>最大并行编译数，默认 Environment.ProcessorCount。</summary>
        public int MaxParallelCompilations { get; set; } = Environment.ProcessorCount;

        /// <summary>是否启用增量编译（使用 Roslyn 增量编译器），默认 true。</summary>
        public bool EnableIncrementalCompilation { get; set; } = true;

        /// <summary>是否在编译失败时自动回滚，默认 true。</summary>
        public bool AutoRollbackOnFailure { get; set; } = true;

        /// <summary>文件监听包含模式，默认 *.cs。</summary>
        public string[] IncludePatterns { get; set; } = new[] { "*.cs" };

        /// <summary>文件监听排除模式。</summary>
        public string[] ExcludePatterns { get; set; } = new[] { "*.csproj", "*.sln", "*.meta", "*.tmp", "obj/*", "bin/*" };

        /// <summary>引擎程序集引用路径列表（用于编译时引用）。</summary>
        public List<string> EngineAssemblyReferences { get; set; } = new();

        /// <summary>额外的 NuGet 包引用路径。</summary>
        public List<string> AdditionalReferences { get; set; } = new();
    }

    // ================================================================================================
    // 编译结果模型
    // ================================================================================================

    /// <summary>单次编译的结果。</summary>
    public sealed class CompilationResult
    {
        public bool Success { get; set; }
        public string AssemblyName { get; set; } = string.Empty;
        public byte[]? AssemblyBytes { get; set; }
        public byte[]? PdbBytes { get; set; }
        public List<ScriptDiagnostic> Diagnostics { get; set; } = new();
        public TimeSpan CompilationTime { get; set; }
        public string? OutputPath { get; set; }
        public Exception? Exception { get; set; }
    }

    // ================================================================================================
    // ScriptHotReload - 主入口类
    // ================================================================================================

    /// <summary>
    /// C# 脚本热重载管理器。
    /// 负责监听脚本文件变更、增量编译、AssemblyLoadContext 卸载/加载、实例状态迁移。
    /// </summary>
    public sealed class ScriptHotReload : IDisposable
    {
        private readonly HotReloadOptions _options;
        private readonly ScriptAssemblyLoader _assemblyLoader;
        private readonly ScriptEngine _scriptEngine;
        private readonly FileSystemWatcher? _watcher;
        private readonly Timer _debounceTimer;
        private readonly ConcurrentQueue<FileChangeEvent> _pendingChanges = new();
        private readonly ConcurrentDictionary<string, AssemblyStateSnapshot> _stateSnapshots = new();
        private readonly SemaphoreSlim _compilationLock = new(1, 1);
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _processingTask;
        private bool _disposed;
        private bool _isCompiling;

        // 事件
        public event EventHandler<ScriptFileChangedEventArgs>? OnFileChanged;
        public event EventHandler<ScriptReloadedEventArgs>? OnScriptReloaded;
        public event EventHandler<ScriptReloadFailedEventArgs>? OnReloadFailed;
        public event EventHandler<string>? OnCompilationStarted;
        public event EventHandler<string>? OnCompilationCompleted;

        /// <summary>是否正在编译中。</summary>
        public bool IsCompiling => _isCompiling;

        /// <summary>当前加载的程序集信息列表。</summary>
        public IReadOnlyList<LoadedAssemblyInfo> LoadedAssemblies => _assemblyLoader.LoadedAssemblies;

        /// <summary>
        /// 创建热重载管理器。
        /// </summary>
        /// <param name="options">配置选项。</param>
        /// <param name="assemblyLoader">程序集加载器（共享实例）。</param>
        /// <param name="scriptEngine">脚本引擎（用于获取当前运行实例）。</param>
        public ScriptHotReload(HotReloadOptions options, ScriptAssemblyLoader assemblyLoader, ScriptEngine scriptEngine)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _assemblyLoader = assemblyLoader ?? throw new ArgumentNullException(nameof(assemblyLoader));
            _scriptEngine = scriptEngine ?? throw new ArgumentNullException(nameof(scriptEngine));

            if (string.IsNullOrWhiteSpace(_options.ScriptProjectRoot))
                throw new ArgumentException("ScriptProjectRoot is required.", nameof(options));

            if (!Directory.Exists(_options.ScriptProjectRoot))
                throw new DirectoryNotFoundException($"Script project root not found: {_options.ScriptProjectRoot}");

            // 初始化文件监听器
            _watcher = new FileSystemWatcher(_options.ScriptProjectRoot)
            {
                IncludeSubdirectories = true,
                EnableRaisingEvents = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName,
            };

            foreach (var pattern in _options.IncludePatterns)
                _watcher.Filters.Add(pattern);

            _watcher.Changed += OnFileSystemChanged;
            _watcher.Created += OnFileSystemChanged;
            _watcher.Deleted += OnFileSystemChanged;
            _watcher.Renamed += OnFileSystemRenamed;
            _watcher.Error += OnWatcherError;

            // 防抖动定时器
            _debounceTimer = new Timer(ProcessPendingChanges, null, Timeout.Infinite, Timeout.Infinite);

            // 后台处理任务
            _processingTask = Task.Run(ProcessQueueAsync, _cts.Token);
        }

        // ============================================================================================
        // 文件系统事件处理
        // ============================================================================================

        private void OnFileSystemChanged(object sender, FileSystemEventArgs e)
        {
            if (ShouldIgnoreFile(e.FullPath)) return;

            var relativePath = GetRelativePath(e.FullPath);
            var assemblyName = InferAssemblyName(relativePath);

            _pendingChanges.Enqueue(new FileChangeEvent
            {
                FullPath = e.FullPath,
                RelativePath = relativePath,
                ChangeType = e.ChangeType,
                AssemblyName = assemblyName,
                Timestamp = DateTime.UtcNow
            });

            // 重置防抖动定时器
            _debounceTimer.Change(_options.DebounceMs, Timeout.Infinite);
        }

        private void OnFileSystemRenamed(object sender, RenamedEventArgs e)
        {
            if (ShouldIgnoreFile(e.FullPath) && ShouldIgnoreFile(e.OldFullPath)) return;

            var relativePath = GetRelativePath(e.FullPath);
            var assemblyName = InferAssemblyName(relativePath);

            _pendingChanges.Enqueue(new FileChangeEvent
            {
                FullPath = e.FullPath,
                RelativePath = relativePath,
                ChangeType = WatcherChangeTypes.Renamed,
                AssemblyName = assemblyName,
                Timestamp = DateTime.UtcNow,
                OldFullPath = e.OldFullPath
            });

            _debounceTimer.Change(_options.DebounceMs, Timeout.Infinite);
        }

        private void OnWatcherError(object sender, ErrorEventArgs e)
        {
            System.Diagnostics.Debug.WriteLine($"[ScriptHotReload] FileSystemWatcher error: {e.GetException()}");
        }

        private bool ShouldIgnoreFile(string fullPath)
        {
            var fileName = Path.GetFileName(fullPath);
            foreach (var pattern in _options.ExcludePatterns)
            {
                if (MatchPattern(fileName, pattern) || MatchPattern(fullPath, pattern))
                    return true;
            }
            return false;
        }

        private bool MatchPattern(string input, string pattern)
        {
            if (pattern.Contains("*"))
            {
                var regex = "^" + pattern.Replace(".", "\.").Replace("*", ".*") + "$";
                return System.Text.RegularExpressions.Regex.IsMatch(input, regex, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            }
            return string.Equals(input, pattern, StringComparison.OrdinalIgnoreCase);
        }

        private string GetRelativePath(string fullPath)
        {
            var root = Path.GetFullPath(_options.ScriptProjectRoot);
            var full = Path.GetFullPath(fullPath);
            if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                return full.Substring(root.Length + 1).Replace('\', '/');
            return fullPath;
        }

        private string? InferAssemblyName(string relativePath)
        {
            // 尝试从路径推断程序集名：Scripts/AssemblyName/... -> AssemblyName
            var parts = relativePath.Split('/');
            if (parts.Length >= 2 && parts[0].Equals("Scripts", StringComparison.OrdinalIgnoreCase))
                return parts[1];
            return null;
        }

        // ============================================================================================
        // 防抖动处理
        // ============================================================================================

        private void ProcessPendingChanges(object? state)
        {
            var changes = new List<FileChangeEvent>();
            while (_pendingChanges.TryDequeue(out var evt))
                changes.Add(evt);

            if (changes.Count == 0) return;

            // 按程序集分组
            var byAssembly = changes.GroupBy(c => c.AssemblyName ?? "Unknown")
                .ToDictionary(g => g.Key, g => g.ToList());

            foreach (var kvp in byAssembly)
            {
                _ = Task.Run(() => TriggerRecompileAsync(kvp.Key, kvp.Value), _cts.Token);
            }
        }

        // ============================================================================================
        // 编译与重载流程
        // ============================================================================================

        private async Task TriggerRecompileAsync(string assemblyName, List<FileChangeEvent> changes)
        {
            await _compilationLock.WaitAsync(_cts.Token);
            try
            {
                if (_isCompiling) return;
                _isCompiling = true;

                OnCompilationStarted?.Invoke(this, assemblyName);

                var stopwatch = Stopwatch.StartNew();

                // 1. 捕获当前实例状态（用于迁移）
                var oldSnapshot = CaptureAssemblyState(assemblyName);

                // 2. 编译
                var result = await CompileAssemblyAsync(assemblyName);

                // 3. 处理编译结果
                if (result.Success)
                {
                    await ApplyReloadAsync(assemblyName, result, oldSnapshot);
                }
                else
                {
                    HandleCompilationFailure(assemblyName, result, oldSnapshot);
                }

                stopwatch.Stop();
                OnCompilationCompleted?.Invoke(this, $"{assemblyName} ({stopwatch.ElapsedMs}ms)");
            }
            catch (Exception ex)
            {
                HandleException(assemblyName, ex);
            }
            finally
            {
                _isCompiling = false;
                _compilationLock.Release();
            }
        }

        private AssemblyStateSnapshot CaptureAssemblyState(string assemblyName)
        {
            var snapshot = new AssemblyStateSnapshot { AssemblyName = assemblyName };

            // 从 ScriptEngine 获取当前运行的脚本实例
            var instances = _scriptEngine.GetRunningScriptInstances(assemblyName);
            foreach (var instance in instances)
            {
                var state = new ScriptInstanceState
                {
                    TypeFullName = instance.GetType().FullName ?? string.Empty,
                    EntityId = instance.EntityId,
                    SerializedFields = new(),
                    PublicFields = new()
                };

                // 序列化 [SerializeField] 字段
                var type = instance.GetType();
                foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (Attribute.IsDefined(field, typeof(SerializeFieldAttribute)))
                    {
                        state.SerializedFields[field.Name] = field.GetValue(instance);
                    }
                    else if (field.IsPublic)
                    {
                        state.PublicFields[field.Name] = field.GetValue(instance);
                    }
                }

                // 序列化公共属性
                foreach (var prop in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
                {
                    if (prop.CanRead && prop.CanWrite)
                    {
                        state.PublicFields[prop.Name] = prop.GetValue(instance);
                    }
                }

                snapshot.Instances.Add(state);
            }

            _stateSnapshots[assemblyName] = snapshot;
            return snapshot;
        }

        private async Task<CompilationResult> CompileAssemblyAsync(string assemblyName)
        {
            var result = new CompilationResult { AssemblyName = assemblyName };
            var sw = Stopwatch.StartNew();

            try
            {
                // 查找 .csproj 文件
                var csprojPath = FindCsprojForAssembly(assemblyName);
                if (string.IsNullOrEmpty(csprojPath) || !File.Exists(csprojPath))
                {
                    result.Success = false;
                    result.Diagnostics.Add(new ScriptDiagnostic
                    {
                        Severity = ScriptDiagnosticSeverity.Error,
                        Message = $"Could not find .csproj for assembly: {assemblyName}",
                        Id = "HR001"
                    });
                    return result;
                }

                // 使用 dotnet build 编译（最可靠的方式，支持增量编译）
                var outputDir = Path.Combine(_options.ScriptProjectRoot, _options.OutputPath);
                var dllPath = Path.Combine(outputDir, $"{assemblyName}.dll");
                var pdbPath = Path.Combine(outputDir, $"{assemblyName}.pdb");

                var buildArgs = $"build "{csprojPath}" -c Debug -o "{outputDir}" --no-restore";
                var processStartInfo = new ProcessStartInfo
                {
                    FileName = "dotnet",
                    Arguments = buildArgs,
                    WorkingDirectory = _options.ScriptProjectRoot,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                using var process = Process.Start(processStartInfo);
                if (process == null)
                {
                    result.Success = false;
                    result.Diagnostics.Add(new ScriptDiagnostic
                    {
                        Severity = ScriptDiagnosticSeverity.Error,
                        Message = "Failed to start dotnet build process",
                        Id = "HR002"
                    });
                    return result;
                }

                var stdout = await process.StandardOutput.ReadToEndAsync();
                var stderr = await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync(_cts.Token);

                sw.Stop();
                result.CompilationTime = sw.Elapsed;

                if (process.ExitCode != 0 || !File.Exists(dllPath))
                {
                    result.Success = false;
                    ParseBuildOutput(stderr, stdout, result.Diagnostics);
                    return result;
                }

                // 读取编译产物
                result.AssemblyBytes = File.ReadAllBytes(dllPath);
                if (File.Exists(pdbPath))
                    result.PdbBytes = File.ReadAllBytes(pdbPath);
                result.OutputPath = dllPath;
                result.Success = true;
            }
            catch (Exception ex)
            {
                sw.Stop();
                result.CompilationTime = sw.Elapsed;
                result.Success = false;
                result.Exception = ex;
                result.Diagnostics.Add(new ScriptDiagnostic
                {
                    Severity = ScriptDiagnosticSeverity.Error,
                    Message = ex.Message,
                    Id = "HR003"
                });
            }

            return result;
        }

        private void ParseBuildOutput(string stderr, string stdout, List<ScriptDiagnostic> diagnostics)
        {
            var output = stderr + "
" + stdout;
            var lines = output.Split(new[] { '
', '' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var line in lines)
            {
                // 解析 MSBuild/Roslyn 错误格式: file.cs(line,col): error CSxxxx: message
                var match = System.Text.RegularExpressions.Regex.Match(line,
                    @"(?<file>[^\s:]+)\((?<line>\d+),(?<col>\d+)\):\s*(?<type>error|warning)\s+(?<code>CS\d+):\s*(?<msg>.+)");

                if (match.Success)
                {
                    diagnostics.Add(new ScriptDiagnostic
                    {
                        Severity = match.Groups["type"].Value.Equals("error", StringComparison.OrdinalIgnoreCase)
                            ? ScriptDiagnosticSeverity.Error
                            : ScriptDiagnosticSeverity.Warning,
                        Message = match.Groups["msg"].Value.Trim(),
                        Id = match.Groups["code"].Value,
                        FilePath = match.Groups["file"].Value,
                        Line = int.Parse(match.Groups["line"].Value),
                        Column = int.Parse(match.Groups["col"].Value)
                    });
                }
            }
        }

        private string? FindCsprojForAssembly(string assemblyName)
        {
            // 查找 Scripts/AssemblyName/AssemblyName.csproj
            var scriptsDir = Path.Combine(_options.ScriptProjectRoot, "Scripts");
            if (Directory.Exists(scriptsDir))
            {
                var asmDir = Path.Combine(scriptsDir, assemblyName);
                if (Directory.Exists(asmDir))
                {
                    var csprojFiles = Directory.GetFiles(asmDir, "*.csproj");
                    if (csprojFiles.Length > 0)
                        return csprojFiles[0];
                }
            }

            // 备选：在整个项目中查找
            var allCsproj = Directory.GetFiles(_options.ScriptProjectRoot, "*.csproj", SearchOption.AllDirectories);
            foreach (var csproj in allCsproj)
            {
                var content = File.ReadAllText(csproj);
                if (content.Contains($"<AssemblyName>{assemblyName}</AssemblyName>", StringComparison.OrdinalIgnoreCase) ||
                    content.Contains($"<RootNamespace>{assemblyName}</RootNamespace>", StringComparison.OrdinalIgnoreCase))
                {
                    return csproj;
                }
            }

            return null;
        }

        private async Task ApplyReloadAsync(string assemblyName, CompilationResult result, AssemblyStateSnapshot? oldSnapshot)
        {
            var oldInfo = _assemblyLoader.LoadedAssemblies.FirstOrDefault(a => a.Name == assemblyName);
            var affectedTypes = oldInfo?.ScriptTypes ?? Array.Empty<Type>();

            // 1. 卸载旧程序集
            if (oldInfo != null)
            {
                _assemblyLoader.Unload(oldInfo);
                _assemblyLoader.CollectGarbage();
            }

            // 2. 加载新程序集
            LoadedAssemblyInfo newInfo;
            using (var peStream = new MemoryStream(result.AssemblyBytes!))
            using (var pdbStream = result.PdbBytes != null ? new MemoryStream(result.PdbBytes) : null)
            {
                newInfo = _assemblyLoader.LoadFromStream(peStream, pdbStream, assemblyName);
            }

            // 3. 迁移实例状态
            int migrated = 0, failed = 0;
            if (oldSnapshot != null && oldSnapshot.Instances.Count > 0)
            {
                foreach (var instanceState in oldSnapshot.Instances)
                {
                    try
                    {
                        await MigrateInstanceStateAsync(instanceState, newInfo);
                        migrated++;
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[ScriptHotReload] Failed to migrate instance {instanceState.EntityId}: {ex.Message}");
                        failed++;
                    }
                }
            }

            // 4. 通知完成
            var args = new ScriptReloadedEventArgs
            {
                AssemblyName = assemblyName,
                NewAssemblyInfo = newInfo,
                OldAssemblyInfo = oldInfo,
                AffectedTypes = newInfo.ScriptTypes,
                MigratedInstanceCount = migrated,
                FailedInstanceCount = failed,
                ElapsedMs = result.CompilationTime.TotalMilliseconds
            };

            OnScriptReloaded?.Invoke(this, args);

            // 清理旧快照
            _stateSnapshots.TryRemove(assemblyName, out _);
        }

        private async Task MigrateInstanceStateAsync(ScriptInstanceState oldState, LoadedAssemblyInfo newInfo)
        {
            // 在新程序集中查找对应类型
            var newType = newInfo.ScriptTypes.FirstOrDefault(t => t.FullName == oldState.TypeFullName);
            if (newType == null)
            {
                // 尝试按类型名匹配（忽略命名空间变更）
                newType = newInfo.ScriptTypes.FirstOrDefault(t => t.Name == oldState.TypeFullName.Split('.').Last());
            }

            if (newType == null)
                throw new InvalidOperationException($"Type not found in new assembly: {oldState.TypeFullName}");

            // 从 ScriptEngine 获取运行时实例（通过 EntityId）
            var instance = _scriptEngine.GetScriptInstanceByEntityId(oldState.EntityId);
            if (instance == null)
                throw new InvalidOperationException($"Running instance not found for EntityId: {oldState.EntityId}");

            // 如果类型变了，需要重新创建实例（这里简化：假设类型兼容）
            if (instance.GetType() != newType)
            {
                // 完整迁移需要：销毁旧实例、创建新实例、挂载到同一 Entity
                // 这里仅演示字段值恢复
                instance = _scriptEngine.RecreateScriptInstance(oldState.EntityId, newType);
            }

            // 恢复 [SerializeField] 字段
            foreach (var kvp in oldState.SerializedFields)
            {
                var field = instance.GetType().GetField(kvp.Key, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (field != null && !field.IsInitOnly)
                {
                    try { field.SetValue(instance, kvp.Value); }
                    catch { /* 忽略单个字段失败 */ }
                }
            }

            // 恢复公共字段/属性
            foreach (var kvp in oldState.PublicFields)
            {
                var field = instance.GetType().GetField(kvp.Key, BindingFlags.Instance | BindingFlags.Public);
                if (field != null && !field.IsInitOnly)
                {
                    try { field.SetValue(instance, kvp.Value); }
                    catch { }
                }
                else
                {
                    var prop = instance.GetType().GetProperty(kvp.Key, BindingFlags.Instance | BindingFlags.Public);
                    if (prop != null && prop.CanWrite)
                    {
                        try { prop.SetValue(instance, kvp.Value); }
                        catch { }
                    }
                }
            }

            // 触发 OnHotReload 回调（如果脚本实现了）
            var hotReloadMethod = instance.GetType().GetMethod("OnHotReload", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (hotReloadMethod != null)
            {
                try { hotReloadMethod.Invoke(instance, null); }
                catch { }
            }
        }

        private void HandleCompilationFailure(string assemblyName, CompilationResult result, AssemblyStateSnapshot? oldSnapshot)
        {
            var rolledBack = false;

            if (_options.AutoRollbackOnFailure && oldSnapshot != null)
            {
                // 尝试恢复旧程序集（如果之前已卸载）
                var oldInfo = _assemblyLoader.LoadedAssemblies.FirstOrDefault(a => a.Name == assemblyName);
                if (oldInfo == null)
                {
                    // 旧程序集已卸载，无法自动回滚
                    // 实际项目中可保留旧 DLL 备份以支持回滚
                }
                else
                {
                    rolledBack = true;
                }
            }

            var args = new ScriptReloadFailedEventArgs
            {
                AssemblyName = assemblyName,
                Diagnostics = result.Diagnostics,
                Exception = result.Exception,
                RolledBack = rolledBack
            };

            OnReloadFailed?.Invoke(this, args);
        }

        private void HandleException(string assemblyName, Exception ex)
        {
            var args = new ScriptReloadFailedEventArgs
            {
                AssemblyName = assemblyName,
                Diagnostics = new[] { new ScriptDiagnostic
                {
                    Severity = ScriptDiagnosticSeverity.Error,
                    Message = ex.Message,
                    Id = "HR999"
                }},
                Exception = ex,
                RolledBack = false
            };

            OnReloadFailed?.Invoke(this, args);
        }

        // ============================================================================================
        // 后台队列处理
        // ============================================================================================

        private async Task ProcessQueueAsync()
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                await Task.Delay(100, _cts.Token);
                // 主要工作在防抖动定时器和 Task.Run 中完成
            }
        }

        // ============================================================================================
        // 公共 API
        // ============================================================================================

        /// <summary>
        /// 手动触发指定程序集的重载。
        /// </summary>
        public async Task<bool> TriggerReloadAsync(string assemblyName)
        {
            var changes = new List<FileChangeEvent>
            {
                new FileChangeEvent
                {
                    AssemblyName = assemblyName,
                    ChangeType = WatcherChangeTypes.Changed,
                    Timestamp = DateTime.UtcNow
                }
            };

            await TriggerRecompileAsync(assemblyName, changes);
            return true;
        }

        /// <summary>
        /// 重载所有已加载的程序集。
        /// </summary>
        public async Task ReloadAllAsync()
        {
            var assemblies = _assemblyLoader.LoadedAssemblies.Select(a => a.Name).ToList();
            foreach (var name in assemblies)
            {
                await TriggerReloadAsync(name);
            }
        }

        /// <summary>
        /// 获取指定程序集的状态快照（只读）。
        /// </summary>
        public AssemblyStateSnapshot? GetStateSnapshot(string assemblyName)
        {
            _stateSnapshots.TryGetValue(assemblyName, out var snapshot);
            return snapshot;
        }

        /// <summary>
        /// 启用/禁用文件监听。
        /// </summary>
        public void SetWatcherEnabled(bool enabled)
        {
            if (_watcher != null)
                _watcher.EnableRaisingEvents = enabled;
        }

        // ============================================================================================
        // IDisposable
        // ============================================================================================

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _cts.Cancel();
            _debounceTimer.Dispose();
            _watcher?.Dispose();
            _compilationLock.Dispose();
            _cts.Dispose();

            try { _processingTask.Wait(TimeSpan.FromSeconds(5)); } catch { }
        }

        // ============================================================================================
        // 内部类型
        // ============================================================================================

        private sealed class FileChangeEvent
        {
            public string FullPath { get; set; } = string.Empty;
            public string RelativePath { get; set; } = string.Empty;
            public WatcherChangeTypes ChangeType { get; set; }
            public string? AssemblyName { get; set; }
            public DateTime Timestamp { get; set; }
            public string? OldFullPath { get; set; }
        }
    }
}
