// ==================================================================================================
//  ScriptRuntime.cs — 山雨引擎 C# 脚本运行时
//
//  命名空间: ShanyuEngine.Scripting
//  依赖:     Microsoft.CodeAnalysis.CSharp (Roslyn), ECS, Physics, Input, Audio
//
//  功能:
//    1. ScriptCompiler        — 使用 Roslyn 编译 C# 脚本源码到内存 Assembly
//    2. ScriptInstance        — 脚本实例 (反射调用 OnStart/OnUpdate/OnDestroy, 绑定 ECS Entity)
//    3. ScriptRuntimeManager  — 脚本运行时管理器 (注册/注销, 每帧更新, 热重载)
//    4. ScriptAPI             — 脚本可用的静态 API (Input/Transform/Physics/Audio/Debug)
//
//  注意:
//    ScriptSource / ScriptFile / ScriptDiagnostic 等类型已存在于 ScriptEngine.cs,
//    本文件复用这些类型, 不重复定义。
// ==================================================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;
using ShanyuEngine.Audio;
using ShanyuEngine.ECS;
using ShanyuEngine.Input;
using ShanyuEngine.Physics;

using LogLevel = ShanyuEngine.Config.LogLevel;
namespace ShanyuEngine.Scripting
{
    // ════════════════════════════════════════════════════════════════════════════
    //  ScriptCompiler — Roslyn 脚本编译器
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 使用 Microsoft.CodeAnalysis.CSharp (Roslyn) 编译 C# 脚本源码到内存 Assembly。
    /// 不写磁盘, 所有编译产物仅在内存中。
    /// </summary>
    public sealed class ScriptCompiler
    {
        // ── 配置 ──────────────────────────────────────────────────────────────────

        /// <summary>是否生成调试信息 (PDB)。</summary>
        public bool EmitDebugInfo { get; set; } = true;

        /// <summary>优化级别。</summary>
        public OptimizationLevel OptimizationLevel { get; set; } = OptimizationLevel.Debug;

        /// <summary>是否允许 unsafe 代码。</summary>
        public bool AllowUnsafe { get; set; } = false;

        /// <summary>上次编译的诊断信息 (错误 + 警告)。</summary>
        public IReadOnlyList<ScriptDiagnostic> LastDiagnostics { get; private set; } = Array.Empty<ScriptDiagnostic>();

        /// <summary>上次编译是否成功。</summary>
        public bool LastCompileSucceeded { get; private set; }

        // ── 缓存的引用列表 ────────────────────────────────────────────────────────

        private List<MetadataReference>? _cachedReferences;

        // ─────────────────────────────────────────────────────────────────────────
        //  Compile — 编译脚本源码到 Assembly
        // ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 编译 <see cref="ScriptSource"/> 到内存 Assembly。
        /// 成功返回 Assembly, 失败返回 null (错误信息见 <see cref="LastDiagnostics"/>)。
        /// </summary>
        /// <param name="source">脚本源码集合。</param>
        /// <returns>编译后的 Assembly, 或 null (编译失败)。</returns>
        public Assembly? Compile(ScriptSource source)
        {
            if (source == null || source.Files == null || source.Files.Count == 0)
            {
                LastDiagnostics = new[] { new ScriptDiagnostic
                {
                    Severity = ScriptDiagnosticSeverity.Error,
                    Message = "没有可编译的源文件",
                    File = string.Empty,
                    Line = 0,
                    Column = 0,
                }};
                LastCompileSucceeded = false;
                return null;
            }

            var references = GetReferences();

            // 创建语法树
            var syntaxTrees = new List<SyntaxTree>();
            foreach (var file in source.Files)
            {
                var sourceText = SourceText.From(file.Content ?? string.Empty);
                var tree = CSharpSyntaxTree.ParseText(
                    sourceText,
                    path: file.Path ?? $"Script_{syntaxTrees.Count}.cs");
                syntaxTrees.Add(tree);
            }

            // 编译选项
            var preprocessorSymbols = new List<string> { "TRACE", "DEBUG" };
            if (source.PreprocessorSymbols != null)
                preprocessorSymbols.AddRange(source.PreprocessorSymbols);

            var parseOptions = new CSharpParseOptions(
                LanguageVersion.CSharp11,
                preprocessorSymbols: preprocessorSymbols);

            // 重新解析带预处理器符号
            syntaxTrees.Clear();
            foreach (var file in source.Files)
            {
                var sourceText = SourceText.From(file.Content ?? string.Empty);
                var tree = CSharpSyntaxTree.ParseText(
                    sourceText,
                    parseOptions,
                    path: file.Path ?? $"Script_{syntaxTrees.Count}.cs");
                syntaxTrees.Add(tree);
            }

            var compilationOptions = new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: OptimizationLevel,
                allowUnsafe: AllowUnsafe,
                nullableContextOptions: NullableContextOptions.Enable,
                deterministic: true);

            var compilation = CSharpCompilation.Create(
                source.AssemblyName ?? "ShanyuScripts",
                syntaxTrees,
                references,
                compilationOptions);

            // 发射到内存
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
                LastDiagnostics = new[] { new ScriptDiagnostic
                {
                    Severity = ScriptDiagnosticSeverity.Error,
                    Message = $"编译发射失败: {ex.Message}",
                    File = string.Empty,
                    Line = 0,
                    Column = 0,
                }};
                LastCompileSucceeded = false;
                return null;
            }

            // 收集诊断信息
            var diagnostics = new List<ScriptDiagnostic>();
            foreach (var diag in emitResult.Diagnostics)
            {
                var severity = diag.Severity switch
                {
                    DiagnosticSeverity.Error => ScriptDiagnosticSeverity.Error,
                    DiagnosticSeverity.Warning => ScriptDiagnosticSeverity.Warning,
                    _ => ScriptDiagnosticSeverity.Info,
                };

                if (severity == ScriptDiagnosticSeverity.Info) continue;

                var loc = diag.Location.GetMappedLineSpan();
                diagnostics.Add(new ScriptDiagnostic
                {
                    Severity = severity,
                    Message = diag.GetMessage(),
                    File = loc.Path ?? string.Empty,
                    Line = loc.StartLinePosition.Line + 1,
                    Column = loc.StartLinePosition.Character + 1,
                });
            }
            LastDiagnostics = diagnostics;

            if (!emitResult.Success)
            {
                LastCompileSucceeded = false;
                return null;
            }

            // 加载到内存 Assembly
            try
            {
                peStream.Position = 0;
                byte[] peBytes = peStream.ToArray();
                byte[]? pdbBytes = pdbStream?.ToArray();

                Assembly assembly;
                if (pdbBytes != null && pdbBytes.Length > 0)
                {
                    assembly = Assembly.Load(peBytes, pdbBytes);
                }
                else
                {
                    assembly = Assembly.Load(peBytes);
                }

                LastCompileSucceeded = true;
                return assembly;
            }
            catch (Exception ex)
            {
                LastDiagnostics = new[] { new ScriptDiagnostic
                {
                    Severity = ScriptDiagnosticSeverity.Error,
                    Message = $"Assembly 加载失败: {ex.Message}",
                    File = string.Empty,
                    Line = 0,
                    Column = 0,
                }};
                LastCompileSucceeded = false;
                return null;
            }
            finally
            {
                peStream.Dispose();
                pdbStream?.Dispose();
            }
        }

        /// <summary>
        /// 编译单个 C# 源码字符串到 Assembly。
        /// 便捷方法。
        /// </summary>
        public Assembly? CompileSource(string sourceCode, string assemblyName = "ShanyuScript", string filePath = "Script.cs")
        {
            return Compile(new ScriptSource
            {
                AssemblyName = assemblyName,
                Files = new[] { new ScriptFile { Path = filePath, Content = sourceCode } },
            });
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  引用构建
        // ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 构建编译引用列表。包含当前已加载的所有 Assembly + 框架运行时 DLL。
        /// 结果会被缓存。
        /// </summary>
        private List<MetadataReference> GetReferences()
        {
            if (_cachedReferences != null) return _cachedReferences;

            var references = new List<MetadataReference>();
            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 1. 引用所有当前已加载的 Assembly (包含 ShanyuEngine.dll)
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                string path;
                try { path = assembly.Location; }
                catch { continue; }

                if (string.IsNullOrEmpty(path) || !File.Exists(path)) continue;
                if (!seenPaths.Add(path)) continue;

                try { references.Add(MetadataReference.CreateFromFile(path)); }
                catch { /* 跳过不可读的 Assembly */ }
            }

            // 2. 确保框架运行时 DLL 被引用
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
                    "System.Threading.dll",
                    "System.Reflection.dll",
                    "System.IO.FileSystem.dll",
                    "System.Console.dll",
                    "System.Text.Json.dll",
                    "netstandard.dll",
                };

                foreach (var dll in frameworkDlls)
                {
                    var fullPath = Path.Combine(runtimeDir, dll);
                    if (File.Exists(fullPath) && seenPaths.Add(fullPath))
                    {
                        try { references.Add(MetadataReference.CreateFromFile(fullPath)); }
                        catch { }
                    }
                }
            }

            _cachedReferences = references;
            return references;
        }

        /// <summary>清除引用缓存 (当引擎 Assembly 更新后调用)。</summary>
        public void ClearReferenceCache()
        {
            _cachedReferences = null;
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  ScriptInstance — 脚本实例
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 脚本实例。从编译后的 Assembly 创建类型实例, 通过反射调用生命周期方法
    /// (OnStart/OnUpdate/OnDestroy), 并绑定到 ECS Entity。
    /// </summary>
    public sealed class ScriptInstance : IDisposable
    {
        // ── 属性 ──────────────────────────────────────────────────────────────────

        /// <summary>脚本实例的实际对象。</summary>
        public object Instance { get; private set; }

        /// <summary>脚本类型的 Type 信息。</summary>
        public Type ScriptType { get; }

        /// <summary>绑定的 ECS 实体 ID。</summary>
        public int EntityId { get; private set; }

        /// <summary>脚本的全名 (命名空间.类名)。</summary>
        public string FullName { get; }

        /// <summary>是否已调用 OnStart。</summary>
        public bool IsStarted { get; private set; }

        /// <summary>是否已销毁。</summary>
        public bool IsDestroyed { get; private set; }

        /// <summary>是否启用。</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>编译来源文件路径 (用于热重载)。</summary>
        public string SourcePath { get; set; } = string.Empty;

        // ── 反射缓存的生命周期方法 ────────────────────────────────────────────────

        private readonly MethodInfo? _onStartMethod;
        private readonly MethodInfo? _onUpdateMethod;
        private readonly MethodInfo? _onDestroyMethod;
        private readonly FieldInfo? _entityIdField;
        private readonly PropertyInfo? _entityIdProp;
        private readonly bool _isScriptComponent;

        // ─────────────────────────────────────────────────────────────────────────
        //  构造
        // ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 从编译后的 Assembly 创建脚本实例。
        /// </summary>
        /// <param name="scriptType">脚本类型 (从 Assembly 中获取)。</param>
        /// <param name="entityId">绑定的 ECS 实体 ID。</param>
        /// <param name="ecs">ECS 实例 (用于 ScriptComponent 绑定, 可为 null)。</param>
        public ScriptInstance(Type scriptType, int entityId, EntityComponentSystem? ecs = null)
        {
            ScriptType = scriptType ?? throw new ArgumentNullException(nameof(scriptType));
            EntityId = entityId;
            FullName = scriptType.FullName ?? scriptType.Name;
            Instance = Activator.CreateInstance(scriptType)!
                ?? throw new InvalidOperationException($"无法创建 {scriptType.Name} 的实例");

            _isScriptComponent = typeof(ScriptComponent).IsAssignableFrom(scriptType);

            // 查找生命周期方法 (支持 OnStart/Start, OnUpdate/Update, OnDestroy)
            _onStartMethod = FindLifecycleMethod(scriptType, "OnStart", "Start", "Awake");
            _onUpdateMethod = FindLifecycleMethod(scriptType, "OnUpdate", "Update");
            _onDestroyMethod = FindLifecycleMethod(scriptType, "OnDestroy");

            // 查找 EntityId 字段/属性 (用于注入实体 ID)
            _entityIdField = scriptType.GetField("EntityId", BindingFlags.Public | BindingFlags.Instance);
            _entityIdProp = scriptType.GetProperty("EntityId", BindingFlags.Public | BindingFlags.Instance);

            // 绑定 ECS Entity
            BindEntity(entityId, ecs);
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  生命周期
        // ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 调用脚本的 OnStart 方法 (如果存在且尚未调用)。
        /// </summary>
        public void OnStart()
        {
            if (IsStarted || IsDestroyed || !Enabled) return;
            IsStarted = true;

            if (_onStartMethod == null) return;

            try
            {
                InvokeLifecycleMethod(_onStartMethod);
            }
            catch (Exception ex)
            {
                ScriptAPI.Debug.LogError($"[{FullName}.OnStart] {ex.GetBaseException().Message}");
            }
        }

        /// <summary>
        /// 调用脚本的 OnUpdate 方法 (如果存在)。
        /// </summary>
        /// <param name="deltaTime">帧间隔时间 (秒)。</param>
        public void OnUpdate(float deltaTime)
        {
            if (!IsStarted || IsDestroyed || !Enabled) return;
            if (_onUpdateMethod == null) return;

            try
            {
                var parameters = _onUpdateMethod.GetParameters();
                if (parameters.Length == 0)
                    _onUpdateMethod.Invoke(Instance, null);
                else if (parameters.Length == 1 && parameters[0].ParameterType == typeof(float))
                    _onUpdateMethod.Invoke(Instance, new object[] { deltaTime });
            }
            catch (Exception ex)
            {
                ScriptAPI.Debug.LogError($"[{FullName}.OnUpdate] {ex.GetBaseException().Message}");
            }
        }

        /// <summary>
        /// 调用脚本的 OnDestroy 方法 (如果存在)。
        /// </summary>
        public void OnDestroy()
        {
            if (IsDestroyed) return;
            IsDestroyed = true;

            if (_onDestroyMethod == null) return;

            try
            {
                InvokeLifecycleMethod(_onDestroyMethod);
            }
            catch (Exception ex)
            {
                ScriptAPI.Debug.LogError($"[{FullName}.OnDestroy] {ex.GetBaseException().Message}");
            }
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  实体绑定
        // ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 将脚本实例绑定到 ECS 实体。注入 EntityId 到公共字段/属性。
        /// 如果脚本是 ScriptComponent 派生类, 通过反射设置内部 EntityId 和 ECS。
        /// </summary>
        public void BindEntity(int entityId, EntityComponentSystem? ecs)
        {
            EntityId = entityId;

            // 注入 EntityId 到公共字段
            if (_entityIdField != null && _entityIdField.FieldType == typeof(int))
            {
                _entityIdField.SetValue(Instance, entityId);
            }

            // 注入 EntityId 到公共属性 (有 setter)
            if (_entityIdProp != null && _entityIdProp.PropertyType == typeof(int) && _entityIdProp.CanWrite)
            {
                _entityIdProp.SetValue(Instance, entityId);
            }

            // 如果是 ScriptComponent, 通过反射设置内部 EntityId 和 ECS
            if (_isScriptComponent && ecs != null)
            {
                var flags = BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public;
                typeof(ScriptComponent)
                    .GetProperty("EntityId", flags)
                    ?.GetSetMethod(nonPublic: true)
                    ?.Invoke(Instance, new object[] { entityId });

                typeof(ScriptComponent)
                    .GetProperty("ECS", flags)
                    ?.GetSetMethod(nonPublic: true)
                    ?.Invoke(Instance, new object[] { ecs });
            }
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  热重载支持
        // ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 从旧实例迁移状态到新实例 (热重载时使用)。
        /// 复制所有公共可读写字段的值。
        /// </summary>
        internal void MigrateStateFrom(ScriptInstance oldInstance)
        {
            if (oldInstance == null) return;

            var oldObj = oldInstance.Instance;
            var newObj = Instance;

            foreach (var field in ScriptType.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                // 跳过只读字段
                if (field.IsInitOnly) continue;

                // 跳过 EntityId (已由 BindEntity 设置)
                if (field.Name == "EntityId") continue;

                try
                {
                    var oldValue = field.GetValue(oldObj);
                    field.SetValue(newObj, oldValue);
                }
                catch { /* 跳过无法复制的字段 */ }
            }
        }

        /// <summary>
        /// 获取脚本字段值 (用于 Inspector 显示)。
        /// </summary>
        public Dictionary<string, object?> GetFieldValues()
        {
            var result = new Dictionary<string, object?>();
            foreach (var field in ScriptType.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (field.IsInitOnly) continue;
                try { result[field.Name] = field.GetValue(Instance); }
                catch { }
            }
            return result;
        }

        /// <summary>
        /// 设置脚本字段值 (用于 Inspector 编辑)。
        /// </summary>
        public void SetFieldValue(string fieldName, object? value)
        {
            var field = ScriptType.GetField(fieldName, BindingFlags.Public | BindingFlags.Instance);
            if (field != null && !field.IsInitOnly)
            {
                try { field.SetValue(Instance, value); }
                catch { }
            }
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  辅助
        // ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 按优先级查找生命周期方法 (无参数或有 float 参数)。
        /// </summary>
        private static MethodInfo? FindLifecycleMethod(Type type, params string[] names)
        {
            foreach (var name in names)
            {
                // 优先查找无参数版本
                var method = type.GetMethod(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic,
                    null, Type.EmptyTypes, null);
                if (method != null && method.DeclaringType != typeof(object))
                    return method;

                // 查找有 float 参数版本 (Update(float))
                method = type.GetMethod(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic,
                    null, new[] { typeof(float) }, null);
                if (method != null && method.DeclaringType != typeof(object))
                    return method;
            }
            return null;
        }

        /// <summary>
        /// 调用生命周期方法 (自动处理参数)。
        /// </summary>
        private void InvokeLifecycleMethod(MethodInfo method)
        {
            var parameters = method.GetParameters();
            if (parameters.Length == 0)
                method.Invoke(Instance, null);
            else
                method.Invoke(Instance, new object[parameters.Length]);
        }

        /// <inheritdoc/>
        public override string ToString() => $"ScriptInstance({FullName}, Entity={EntityId})";

        /// <inheritdoc/>
        public void Dispose()
        {
            if (!IsDestroyed)
                OnDestroy();
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  ScriptRuntimeManager — 脚本运行时管理器
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 脚本运行时管理器。管理所有脚本实例的生命周期、每帧更新和热重载。
    /// </summary>
    public sealed class ScriptRuntimeManager : IDisposable
    {
        // ── 子系统引用 ────────────────────────────────────────────────────────────

        /// <summary>ECS 实体组件系统。</summary>
        public EntityComponentSystem ECS { get; }

        /// <summary>物理世界 (可为 null)。</summary>
        public PhysicsWorld? Physics { get; }

        /// <summary>输入系统。</summary>
        public InputSystem Input { get; }

        /// <summary>音频引擎 (可为 null)。</summary>
        public AdvancedAudioEngine? Audio { get; }

        /// <summary>脚本编译器。</summary>
        public ScriptCompiler Compiler { get; }

        // ── 脚本实例注册表 ────────────────────────────────────────────────────────

        /// <summary>所有活动的脚本实例。</summary>
        private readonly List<ScriptInstance> _instances = new();

        /// <summary>按类型全名索引的已编译 Assembly 缓存。</summary>
        private readonly Dictionary<string, Assembly> _assemblyCache = new();

        /// <summary>是否已被释放。</summary>
        public bool IsDisposed { get; private set; }

        /// <summary>是否暂停脚本更新。</summary>
        public bool IsPaused { get; set; }

        // ── 热重载 ────────────────────────────────────────────────────────────────

        private FileSystemWatcher? _fileWatcher;
        private readonly object _reloadLock = new();
        private Timer? _debounceTimer;
        private const int ReloadDebounceMs = 300;

        // ─────────────────────────────────────────────────────────────────────────
        //  构造
        // ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 创建脚本运行时管理器。
        /// </summary>
        /// <param name="ecs">ECS 实例。</param>
        /// <param name="physics">物理世界 (可为 null)。</param>
        /// <param name="input">输入系统。</param>
        /// <param name="audio">音频引擎 (可为 null)。</param>
        public ScriptRuntimeManager(
            EntityComponentSystem ecs,
            PhysicsWorld? physics = null,
            InputSystem? input = null,
            AdvancedAudioEngine? audio = null)
        {
            ECS = ecs ?? throw new ArgumentNullException(nameof(ecs));
            Physics = physics;
            Input = input ?? new InputSystem();
            Audio = audio;
            Compiler = new ScriptCompiler();

            // 初始化 ScriptAPI 的静态引用
            ScriptAPI.Initialize(Input, ECS, Physics, Audio);
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  注册 / 注销
        // ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 从编译后的 Assembly 创建并注册脚本实例。
        /// </summary>
        /// <param name="assembly">编译后的 Assembly。</param>
        /// <param name="typeName">脚本类型的全名 (命名空间.类名)。</param>
        /// <param name="entityId">绑定的 ECS 实体 ID。</param>
        /// <returns>创建的脚本实例, 失败返回 null。</returns>
        public ScriptInstance? RegisterScript(Assembly assembly, string typeName, int entityId)
        {
            Type? type = assembly.GetType(typeName);
            if (type == null)
            {
                // 尝试查找所有公共类型
                type = assembly.GetTypes().FirstOrDefault(t =>
                    t.Name == typeName || t.FullName == typeName);
            }

            if (type == null)
            {
                ScriptAPI.Debug.LogError($"RegisterScript: 未找到类型 {typeName}");
                return null;
            }

            var instance = new ScriptInstance(type, entityId, ECS);
            _instances.Add(instance);

            ScriptAPI.Debug.Log($"注册脚本: {instance.FullName} → Entity {entityId}");
            return instance;
        }

        /// <summary>
        /// 编译并注册脚本。
        /// </summary>
        /// <param name="source">脚本源码。</param>
        /// <param name="typeName">脚本类型的全名。</param>
        /// <param name="entityId">绑定的 ECS 实体 ID。</param>
        /// <returns>创建的脚本实例, 失败返回 null。</returns>
        public ScriptInstance? CompileAndRegister(ScriptSource source, string typeName, int entityId)
        {
            var assembly = Compiler.Compile(source);
            if (assembly == null)
            {
                ScriptAPI.Debug.LogError($"编译失败: {source.AssemblyName}");
                foreach (var diag in Compiler.LastDiagnostics)
                {
                    ScriptAPI.Debug.LogError($"  [{diag.Severity}] {diag.File}({diag.Line},{diag.Column}): {diag.Message}");
                }
                return null;
            }

            // 缓存 Assembly
            _assemblyCache[source.AssemblyName ?? typeName] = assembly;

            return RegisterScript(assembly, typeName, entityId);
        }

        /// <summary>
        /// 注销脚本实例。
        /// </summary>
        public void UnregisterScript(ScriptInstance instance)
        {
            if (instance == null) return;
            instance.OnDestroy();
            _instances.Remove(instance);
            ScriptAPI.Debug.Log($"注销脚本: {instance.FullName}");
        }

        /// <summary>
        /// 注销绑定到指定实体的所有脚本。
        /// </summary>
        public void UnregisterScriptsForEntity(int entityId)
        {
            for (int i = _instances.Count - 1; i >= 0; i--)
            {
                if (_instances[i].EntityId == entityId)
                {
                    _instances[i].OnDestroy();
                    _instances.RemoveAt(i);
                }
            }
        }

        /// <summary>
        /// 获取所有活动的脚本实例。
        /// </summary>
        public IReadOnlyList<ScriptInstance> GetInstances() => _instances;

        /// <summary>
        /// 获取绑定到指定实体的所有脚本实例。
        /// </summary>
        public List<ScriptInstance> GetScriptsForEntity(int entityId)
        {
            return _instances.Where(s => s.EntityId == entityId).ToList();
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  每帧更新
        // ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 启动所有已注册但尚未启动的脚本 (调用 OnStart)。
        /// 通常在场景加载完成后调用一次。
        /// </summary>
        public void StartAll()
        {
            foreach (var instance in _instances)
            {
                if (!instance.IsStarted)
                    instance.OnStart();
            }
        }

        /// <summary>
        /// 每帧更新所有脚本 (调用 OnUpdate)。
        /// </summary>
        /// <param name="deltaTime">帧间隔时间 (秒)。</param>
        public void Tick(float deltaTime)
        {
            if (IsDisposed || IsPaused) return;

            // 清理已销毁实体的脚本
            for (int i = _instances.Count - 1; i >= 0; i--)
            {
                var inst = _instances[i];
                if (!ECS.HasEntity(inst.EntityId))
                {
                    inst.OnDestroy();
                    _instances.RemoveAt(i);
                    continue;
                }

                // 启动未启动的脚本
                if (!inst.IsStarted)
                    inst.OnStart();

                // 更新
                inst.OnUpdate(deltaTime);
            }
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  热重载
        // ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 启用热重载。监视指定目录下的 .cs 文件变化, 自动重新编译并替换实例。
        /// </summary>
        /// <param name="watchPath">要监视的脚本目录路径。</param>
        /// <param name="filePattern">文件匹配模式 (默认 "*.cs")。</param>
        public void EnableHotReload(string watchPath, string filePattern = "*.cs")
        {
            if (!Directory.Exists(watchPath))
            {
                ScriptAPI.Debug.LogError($"热重载: 目录不存在 {watchPath}");
                return;
            }

            _fileWatcher?.Dispose();
            _fileWatcher = new FileSystemWatcher(watchPath, filePattern)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };

            _fileWatcher.Changed += OnScriptFileChanged;
            _fileWatcher.Created += OnScriptFileChanged;
            _fileWatcher.Renamed += OnScriptFileChanged;

            ScriptAPI.Debug.Log($"热重载已启用: 监视 {watchPath}");
        }

        /// <summary>
        /// 禁用热重载。
        /// </summary>
        public void DisableHotReload()
        {
            if (_fileWatcher != null)
            {
                _fileWatcher.EnableRaisingEvents = false;
                _fileWatcher.Changed -= OnScriptFileChanged;
                _fileWatcher.Created -= OnScriptFileChanged;
                _fileWatcher.Renamed -= OnScriptFileChanged;
                _fileWatcher.Dispose();
                _fileWatcher = null;
            }

            _debounceTimer?.Dispose();
            _debounceTimer = null;
        }

        /// <summary>
        /// 手动触发指定脚本的重新编译和替换。
        /// </summary>
        /// <param name="sourcePath">脚本源文件路径。</param>
        /// <param name="typeName">脚本类型全名。</param>
        /// <param name="sourceCode">新的源码 (为 null 时从文件读取)。</param>
        /// <returns>是否成功重新加载。</returns>
        public bool ReloadScript(string sourcePath, string typeName, string? sourceCode = null)
        {
            lock (_reloadLock)
            {
                if (sourceCode == null)
                {
                    if (!File.Exists(sourcePath))
                    {
                        ScriptAPI.Debug.LogError($"热重载: 文件不存在 {sourcePath}");
                        return false;
                    }
                    sourceCode = File.ReadAllText(sourcePath);
                }

                // 编译新版本
                var source = new ScriptSource
                {
                    AssemblyName = $"HotReload_{typeName}_{DateTime.Now.Ticks}",
                    Files = new[] { new ScriptFile { Path = sourcePath, Content = sourceCode } },
                };

                var newAssembly = Compiler.Compile(source);
                if (newAssembly == null)
                {
                    ScriptAPI.Debug.LogError($"热重载: 编译失败 {typeName}");
                    return false;
                }

                // 查找新类型
                Type? newType = newAssembly.GetType(typeName);
                if (newType == null)
                    newType = newAssembly.GetTypes().FirstOrDefault(t => t.Name == typeName || t.FullName == typeName);

                if (newType == null)
                {
                    ScriptAPI.Debug.LogError($"热重载: 未找到类型 {typeName}");
                    return false;
                }

                // 替换所有旧实例
                int replaced = 0;
                for (int i = 0; i < _instances.Count; i++)
                {
                    var oldInstance = _instances[i];
                    if (oldInstance.ScriptType.Name != newType.Name &&
                        oldInstance.ScriptType.FullName != typeName)
                        continue;

                    // 创建新实例
                    var newInstance = new ScriptInstance(newType, oldInstance.EntityId, ECS);
                    newInstance.SourcePath = sourcePath;

                    // 迁移状态
                    newInstance.MigrateStateFrom(oldInstance);

                    // 销毁旧实例
                    oldInstance.OnDestroy();

                    // 替换
                    _instances[i] = newInstance;
                    replaced++;
                }

                ScriptAPI.Debug.Log($"热重载完成: {typeName} ({replaced} 个实例已替换)");
                return replaced > 0;
            }
        }

        /// <summary>
        /// 文件变化回调 (防抖处理)。
        /// </summary>
        private void OnScriptFileChanged(object sender, FileSystemEventArgs e)
        {
            // 防抖: 延迟处理, 避免连续多次触发
            _debounceTimer?.Dispose();
            _debounceTimer = new Timer(_ =>
            {
                try
                {
                    var filePath = e.FullPath;
                    if (!File.Exists(filePath)) return;

                    // 查找与此文件关联的脚本实例
                    var affectedInstances = _instances
                        .Where(i => !string.IsNullOrEmpty(i.SourcePath) &&
                                    string.Equals(i.SourcePath, filePath, StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    if (affectedInstances.Count == 0) return;

                    // 获取类型名
                    var typeName = affectedInstances[0].FullName;
                    ReloadScript(filePath, typeName);
                }
                catch (Exception ex)
                {
                    ScriptAPI.Debug.LogError($"热重载异常: {ex.Message}");
                }
            }, null, ReloadDebounceMs, Timeout.Infinite);
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  IDisposable
        // ─────────────────────────────────────────────────────────────────────────

        /// <inheritdoc/>
        public void Dispose()
        {
            if (IsDisposed) return;
            IsDisposed = true;

            DisableHotReload();

            // 销毁所有脚本实例
            foreach (var instance in _instances)
                instance.OnDestroy();
            _instances.Clear();
            _assemblyCache.Clear();

            ScriptAPI.Debug.Log("ScriptRuntimeManager 已释放");
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  ScriptAPI — 脚本可用的静态 API
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 脚本可用的静态 API。提供 Input / Transform / Physics / Audio / Debug 的简化访问。
    /// 脚本通过 <c>ScriptAPI.Input.IsKeyDown("W")</c> 等方式调用。
    /// </summary>
    public static class ScriptAPI
    {
        // ── 内部子系统引用 (由 ScriptRuntimeManager.Initialize 设置) ──────────────

        private static InputSystem? _input;
        private static EntityComponentSystem? _ecs;
        private static PhysicsWorld? _physics;
        private static AdvancedAudioEngine? _audio;

        /// <summary>
        /// 初始化 ScriptAPI 的子系统引用。由 ScriptRuntimeManager 调用。
        /// </summary>
        internal static void Initialize(
            InputSystem input,
            EntityComponentSystem ecs,
            PhysicsWorld? physics,
            AdvancedAudioEngine? audio)
        {
            _input = input;
            _ecs = ecs;
            _physics = physics;
            _audio = audio;
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  Input — 输入 API
        // ─────────────────────────────────────────────────────────────────────────

        /// <summary>输入 API。</summary>
        public static class Input
        {
            /// <summary>
            /// 检查指定按键是否被按下。
            /// </summary>
            /// <param name="key">按键名称 (如 "W", "Space", "Escape") 或 <see cref="InputKey"/> 枚举值。</param>
            public static bool IsKeyDown(string key)
            {
                if (_input == null || string.IsNullOrEmpty(key)) return false;

                var inputKey = ParseKey(key);
                if (inputKey == InputKey.None) return false;
                return _input.IsKeyDown(inputKey);
            }

            /// <summary>
            /// 检查指定按键是否被按下。
            /// </summary>
            /// <param name="key"><see cref="InputKey"/> 枚举值。</param>
            public static bool IsKeyDown(InputKey key)
            {
                return _input?.IsKeyDown(key) ?? false;
            }

            /// <summary>
            /// 检查指定按键是否在当前帧被按下 (边缘触发)。
            /// </summary>
            public static bool IsKeyPressed(string key)
            {
                if (_input == null || string.IsNullOrEmpty(key)) return false;
                var inputKey = ParseKey(key);
                if (inputKey == InputKey.None) return false;
                return _input.IsKeyPressed(inputKey);
            }

            /// <summary>
            /// 检查指定按键是否在当前帧被释放 (边缘触发)。
            /// </summary>
            public static bool IsKeyReleased(string key)
            {
                if (_input == null || string.IsNullOrEmpty(key)) return false;
                var inputKey = ParseKey(key);
                if (inputKey == InputKey.None) return false;
                return _input.IsKeyReleased(inputKey);
            }

            /// <summary>
            /// 将字符串键名解析为 InputKey 枚举。
            /// </summary>
            private static InputKey ParseKey(string key)
            {
                if (Enum.TryParse<InputKey>(key, ignoreCase: true, out var result))
                    return result;

                // 特殊映射
                return key.ToUpperInvariant() switch
                {
                    "ESC" => InputKey.Escape,
                    "ENTER" or "RETURN" => InputKey.Enter,
                    "CTRL" or "CONTROL" => InputKey.Control,
                    "LSHIFT" or "RSHIFT" or "SHIFT" => InputKey.Shift,
                    "LMOUSE" or "MOUSE0" => InputKey.MouseLeft,
                    "RMOUSE" or "MOUSE1" => InputKey.MouseRight,
                    "MMOUSE" or "MOUSE2" => InputKey.MouseMiddle,
                    "UP" => InputKey.UpArrow,
                    "DOWN" => InputKey.DownArrow,
                    "LEFT" => InputKey.LeftArrow,
                    "RIGHT" => InputKey.RightArrow,
                    _ => InputKey.None,
                };
            }
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  Transform — 变换 API
        // ─────────────────────────────────────────────────────────────────────────

        /// <summary>变换 API。</summary>
        public static class Transform
        {
            /// <summary>获取实体的世界位置。</summary>
            public static Vector3 GetPosition(int entity)
            {
                if (_ecs == null || !_ecs.HasEntity(entity)) return Vector3.Zero;
                return _ecs.GetComponent<TransformComponent>(entity).Position;
            }

            /// <summary>设置实体的世界位置。</summary>
            public static void SetPosition(int entity, Vector3 position)
            {
                if (_ecs == null || !_ecs.HasEntity(entity)) return;
                var t = _ecs.GetComponent<TransformComponent>(entity);
                t.Position = position;
                _ecs.SetComponent(entity, t);
            }

            /// <summary>获取实体的旋转 (四元数)。</summary>
            public static Quaternion GetRotation(int entity)
            {
                if (_ecs == null || !_ecs.HasEntity(entity)) return Quaternion.Identity;
                return _ecs.GetComponent<TransformComponent>(entity).Rotation;
            }

            /// <summary>设置实体的旋转 (四元数)。</summary>
            public static void SetRotation(int entity, Quaternion rotation)
            {
                if (_ecs == null || !_ecs.HasEntity(entity)) return;
                var t = _ecs.GetComponent<TransformComponent>(entity);
                t.Rotation = rotation;
                _ecs.SetComponent(entity, t);
            }

            /// <summary>获取实体的缩放。</summary>
            public static Vector3 GetScale(int entity)
            {
                if (_ecs == null || !_ecs.HasEntity(entity)) return Vector3.One;
                return _ecs.GetComponent<TransformComponent>(entity).Scale;
            }

            /// <summary>设置实体的缩放。</summary>
            public static void SetScale(int entity, Vector3 scale)
            {
                if (_ecs == null || !_ecs.HasEntity(entity)) return;
                var t = _ecs.GetComponent<TransformComponent>(entity);
                t.Scale = scale;
                _ecs.SetComponent(entity, t);
            }

            /// <summary>平移实体。</summary>
            public static void Translate(int entity, Vector3 delta)
            {
                if (_ecs == null || !_ecs.HasEntity(entity)) return;
                var t = _ecs.GetComponent<TransformComponent>(entity);
                t.Position += delta;
                _ecs.SetComponent(entity, t);
            }

            /// <summary>获取实体的前向方向。</summary>
            public static Vector3 GetForward(int entity)
            {
                if (_ecs == null || !_ecs.HasEntity(entity)) return Vector3.UnitZ;
                return _ecs.GetComponent<TransformComponent>(entity).Forward;
            }

            /// <summary>获取实体的右向方向。</summary>
            public static Vector3 GetRight(int entity)
            {
                if (_ecs == null || !_ecs.HasEntity(entity)) return Vector3.UnitX;
                return _ecs.GetComponent<TransformComponent>(entity).Right;
            }

            /// <summary>获取实体的上向方向。</summary>
            public static Vector3 GetUp(int entity)
            {
                if (_ecs == null || !_ecs.HasEntity(entity)) return Vector3.UnitY;
                return _ecs.GetComponent<TransformComponent>(entity).Up;
            }
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  Physics — 物理 API
        // ─────────────────────────────────────────────────────────────────────────

        /// <summary>物理 API。</summary>
        public static class Physics
        {
            /// <summary>
            /// 射线检测。从 origin 沿 direction 方向发射射线, 最大距离 maxDistance。
            /// </summary>
            /// <returns>命中信息, HasHit 为 false 表示未命中。</returns>
            public static RaycastHit Raycast(Vector3 origin, Vector3 direction, float maxDistance = 1000f)
            {
                if (_physics == null)
                    return default;

                try
                {
                    return _physics.Raycast(origin, direction, maxDistance);
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[ScriptAPI.Physics.Raycast] {ex.Message}");
                    return default;
                }
            }

            /// <summary>
            /// 射线检测 (所有命中)。
            /// </summary>
            public static List<RaycastHit> RaycastAll(Vector3 origin, Vector3 direction, float maxDistance = 1000f)
            {
                if (_physics == null)
                    return new List<RaycastHit>();

                try
                {
                    return _physics.RaycastAll(origin, direction, maxDistance);
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[ScriptAPI.Physics.RaycastAll] {ex.Message}");
                    return new List<RaycastHit>();
                }
            }

            /// <summary>获取重力。</summary>
            public static Vector3 GetGravity()
            {
                if (_physics == null) return new Vector3(0, -9.81f, 0);
                return _physics.Gravity;
            }

            /// <summary>设置重力。</summary>
            public static void SetGravity(Vector3 gravity)
            {
                if (_physics == null) return;
                _physics.Gravity = gravity;
            }
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  Audio — 音频 API
        // ─────────────────────────────────────────────────────────────────────────

        /// <summary>音频 API。</summary>
        public static class Audio
        {
            /// <summary>
            /// 在指定位置播放 3D 音效。
            /// </summary>
            /// <param name="soundName">音效名称 (在音频引擎中注册的名称)。</param>
            /// <param name="position">播放位置 (世界坐标)。</param>
            public static void Play(string soundName, Vector3 position)
            {
                if (_audio == null || string.IsNullOrEmpty(soundName)) return;

                try
                {
                    var clip = _audio.GetClipByName(soundName);
                    if (clip == null)
                    {
                        Debug.LogWarning($"[ScriptAPI.Audio] 未找到音效: {soundName}");
                        return;
                    }
                    _audio.Play3D(clip, position);
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[ScriptAPI.Audio.Play] {ex.Message}");
                }
            }

            /// <summary>
            /// 播放 2D 音效 (非空间化)。
            /// </summary>
            public static void Play2D(string soundName, float volume = 1f)
            {
                if (_audio == null || string.IsNullOrEmpty(soundName)) return;

                try
                {
                    var clip = _audio.GetClipByName(soundName);
                    if (clip == null)
                    {
                        Debug.LogWarning($"[ScriptAPI.Audio] 未找到音效: {soundName}");
                        return;
                    }
                    _audio.Play2D(clip, volume);
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[ScriptAPI.Audio.Play2D] {ex.Message}");
                }
            }
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  Debug — 调试 API
        // ─────────────────────────────────────────────────────────────────────────

        /// <summary>调试 API。</summary>
        public static class Debug
        {
            /// <summary>日志消息事件 (编辑器可订阅)。</summary>
            public static event Action<string, LogLevel>? OnLog;

            /// <summary>记录信息日志。</summary>
            public static void Log(object? message)
            {
                string text = message?.ToString() ?? "null";
                System.Diagnostics.Debug.WriteLine($"[Script] {text}");
                OnLog?.Invoke(text, LogLevel.Info);
            }

            /// <summary>记录警告日志。</summary>
            public static void LogWarning(object? message)
            {
                string text = message?.ToString() ?? "null";
                System.Diagnostics.Debug.WriteLine($"[Script/Warning] {text}");
                OnLog?.Invoke(text, LogLevel.Warning);
            }

            /// <summary>记录错误日志。</summary>
            public static void LogError(object? message)
            {
                string text = message?.ToString() ?? "null";
                System.Diagnostics.Debug.WriteLine($"[Script/Error] {text}");
                OnLog?.Invoke(text, LogLevel.Error);
            }

            /// <summary>日志级别。</summary>
        }
    }
}
