using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace YamaEngine.Plugins
{
    // ════════════════════════════════════════════════════════════════════════
    // yama引擎 - 插件系统 (PluginSystem)
    //
    // 类似 Unity Package Manager / Unreal Engine Plugin System:
    //   * 插件生命周期管理 (加载/卸载/启用/禁用)
    //   * 语义化版本与版本范围匹配
    //   * 依赖解析 (拓扑排序 / 循环依赖检测 / 缺失依赖报告)
    //   * 热加载 (运行时替换 DLL)
    //   * 插件冲突检测 / API 版本兼容
    //   * 服务定位器 (插件间通信, 避免硬引用)
    // ════════════════════════════════════════════════════════════════════════

    #region 版本系统

    /// <summary>语义化版本 (SemanticVersion): Major.Minor.Patch-PreRelease+Build</summary>
    public class SemanticVersion : IComparable<SemanticVersion>, IEquatable<SemanticVersion>
    {
        public int Major { get; }
        public int Minor { get; }
        public int Patch { get; }
        public string PreRelease { get; }
        public string Build { get; }

        public SemanticVersion(int major, int minor, int patch, string preRelease = null, string build = null)
        {
            if (major < 0 || minor < 0 || patch < 0)
                throw new ArgumentException("版本号组件不能为负数。");
            Major = major;
            Minor = minor;
            Patch = patch;
            PreRelease = preRelease ?? string.Empty;
            Build = build ?? string.Empty;
        }

        public static SemanticVersion Parse(string version)
        {
            if (string.IsNullOrWhiteSpace(version))
                throw new FormatException("版本字符串不能为空。");

            string main = version;
            string preRelease = string.Empty;
            string build = string.Empty;

            int plusIdx = main.IndexOf('+');
            if (plusIdx >= 0)
            {
                build = main.Substring(plusIdx + 1);
                main = main.Substring(0, plusIdx);
            }

            int dashIdx = main.IndexOf('-');
            if (dashIdx >= 0)
            {
                preRelease = main.Substring(dashIdx + 1);
                main = main.Substring(0, dashIdx);
            }

            var parts = main.Split('.');
            if (parts.Length < 3)
                throw new FormatException($"版本格式错误: '{version}', 期望 Major.Minor.Patch[-PreRelease][+Build]");

            if (!int.TryParse(parts[0], out int major))
                throw new FormatException($"无效的 Major 版本号: '{parts[0]}'");
            if (!int.TryParse(parts[1], out int minor))
                throw new FormatException($"无效的 Minor 版本号: '{parts[1]}'");
            if (!int.TryParse(parts[2], out int patch))
                throw new FormatException($"无效的 Patch 版本号: '{parts[2]}'");

            return new SemanticVersion(major, minor, patch, preRelease, build);
        }

        public static bool TryParse(string version, out SemanticVersion result)
        {
            try
            {
                result = Parse(version);
                return true;
            }
            catch
            {
                result = null;
                return false;
            }
        }

        public bool IsPreRelease => !string.IsNullOrEmpty(PreRelease);

        public bool Satisfies(SemanticVersion min, SemanticVersion max)
        {
            return CompareTo(min) >= 0 && CompareTo(max) <= 0;
        }

        public int CompareTo(SemanticVersion other)
        {
            if (other == null) return 1;

            int cmp = Major.CompareTo(other.Major);
            if (cmp != 0) return cmp;
            cmp = Minor.CompareTo(other.Minor);
            if (cmp != 0) return cmp;
            cmp = Patch.CompareTo(other.Patch);
            if (cmp != 0) return cmp;

            // 有预发布标识的版本低于同版本号正式版
            bool thisPre = !string.IsNullOrEmpty(PreRelease);
            bool otherPre = !string.IsNullOrEmpty(other.PreRelease);
            if (!thisPre && otherPre) return 1;
            if (thisPre && !otherPre) return -1;

            return string.CompareOrdinal(PreRelease, other.PreRelease);
        }

        public bool Equals(SemanticVersion other)
        {
            if (other == null) return false;
            return Major == other.Major && Minor == other.Minor && Patch == other.Patch
                && PreRelease == other.PreRelease && Build == other.Build;
        }

        public override bool Equals(object obj) => Equals(obj as SemanticVersion);
        public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, PreRelease, Build);
        public override string ToString()
        {
            var s = $"{Major}.{Minor}.{Patch}";
            if (!string.IsNullOrEmpty(PreRelease)) s += $"-{PreRelease}";
            if (!string.IsNullOrEmpty(Build)) s += $"+{Build}";
            return s;
        }

        public static bool operator <(SemanticVersion a, SemanticVersion b) => a.CompareTo(b) < 0;
        public static bool operator >(SemanticVersion a, SemanticVersion b) => a.CompareTo(b) > 0;
        public static bool operator <=(SemanticVersion a, SemanticVersion b) => a.CompareTo(b) <= 0;
        public static bool operator >=(SemanticVersion a, SemanticVersion b) => a.CompareTo(b) >= 0;
        public static bool operator ==(SemanticVersion a, SemanticVersion b) => a?.Equals(b) ?? b is null;
        public static bool operator !=(SemanticVersion a, SemanticVersion b) => !(a == b);
    }

    /// <summary>版本范围匹配 (支持 ^1.0.0, >=1.0.0, ~1.2.0, 1.0.0 - 2.0.0)</summary>
    public class VersionRange
    {
        public string RawExpression { get; }
        private readonly Func<SemanticVersion, bool> _matchFn;

        private VersionRange(string rawExpression, Func<SemanticVersion, bool> matchFn)
        {
            RawExpression = rawExpression;
            _matchFn = matchFn;
        }

        public static VersionRange Parse(string expression)
        {
            if (string.IsNullOrWhiteSpace(expression))
                throw new FormatException("版本范围表达式不能为空。");

            expression = expression.Trim();

            // ^1.2.3 — 兼容版本 (>=1.2.3 且 <2.0.0)
            if (expression.StartsWith("^"))
            {
                var ver = SemanticVersion.Parse(expression.Substring(1));
                var min = ver;
                var max = new SemanticVersion(ver.Major + 1, 0, 0);
                return new VersionRange(expression, v => v >= min && v < max);
            }

            // ~1.2.3 — 近似版本 (>=1.2.3 且 <1.3.0)
            if (expression.StartsWith("~"))
            {
                var ver = SemanticVersion.Parse(expression.Substring(1));
                var min = ver;
                var max = new SemanticVersion(ver.Major, ver.Minor + 1, 0);
                return new VersionRange(expression, v => v >= min && v < max);
            }

            // >= / <= / > / < / =
            if (expression.StartsWith(">="))
            {
                var ver = SemanticVersion.Parse(expression.Substring(2).Trim());
                return new VersionRange(expression, v => v >= ver);
            }
            if (expression.StartsWith("<="))
            {
                var ver = SemanticVersion.Parse(expression.Substring(2).Trim());
                return new VersionRange(expression, v => v <= ver);
            }
            if (expression.StartsWith(">"))
            {
                var ver = SemanticVersion.Parse(expression.Substring(1).Trim());
                return new VersionRange(expression, v => v > ver);
            }
            if (expression.StartsWith("<"))
            {
                var ver = SemanticVersion.Parse(expression.Substring(1).Trim());
                return new VersionRange(expression, v => v < ver);
            }
            if (expression.StartsWith("="))
            {
                var ver = SemanticVersion.Parse(expression.Substring(1).Trim());
                return new VersionRange(expression, v => v == ver);
            }

            // 1.0.0 - 2.0.0 — 范围
            if (expression.Contains(" - "))
            {
                var parts = expression.Split(new[] { " - " }, StringSplitOptions.None);
                var lo = SemanticVersion.Parse(parts[0].Trim());
                var hi = SemanticVersion.Parse(parts[1].Trim());
                return new VersionRange(expression, v => v >= lo && v <= hi);
            }

            // 1.2.3 — 精确版本
            var exact = SemanticVersion.Parse(expression);
            return new VersionRange(expression, v => v == exact);
        }

        public static bool TryParse(string expression, out VersionRange result)
        {
            try
            {
                result = Parse(expression);
                return true;
            }
            catch
            {
                result = null;
                return false;
            }
        }

        public bool Matches(SemanticVersion version) => _matchFn(version);

        public override string ToString() => RawExpression;
    }

    #endregion

    #region 插件基础类型

    /// <summary>插件生命周期状态枚举。</summary>
    public enum PluginState
    {
        Unloaded,
        Loading,
        Loaded,
        Active,
        Disabled,
        Unloading,
        Error
    }

    /// <summary>插件 API 版本标记属性。</summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Assembly, AllowMultiple = false, Inherited = false)]
    public class PluginAPIAttribute : Attribute
    {
        public string APIVersion { get; }
        public string MinEngineVersion { get; set; }
        public string MaxEngineVersion { get; set; }

        public PluginAPIAttribute(string apiVersion)
        {
            APIVersion = apiVersion ?? "1.0.0";
        }
    }

    /// <summary>插件依赖: 名称 / 版本范围 / 是否必须。</summary>
    public class PluginDependency
    {
        public string Name { get; set; } = string.Empty;
        public string VersionRange { get; set; } = "*";
        public bool IsRequired { get; set; } = true;

        public bool MatchesVersion(SemanticVersion version)
        {
            if (VersionRange == "*") return true;
            if (VersionRange.TryParse(out var range))
                return range.Matches(version);
            return SemanticVersion.TryParse(VersionRange, out var exact) && version == exact;
        }
    }

    /// <summary>插件清单文件 (JSON 格式)。</summary>
    public class PluginManifest
    {
        private static readonly JsonSerializerOptions _jsonOpts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        /// <summary>插件名称。</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>插件显示名称。</summary>
        public string DisplayName { get; set; } = string.Empty;

        /// <summary>版本。</summary>
        public string Version { get; set; } = "1.0.0";

        /// <summary>作者。</summary>
        public string Author { get; set; } = "Unknown";

        /// <summary>描述。</summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>API 版本。</summary>
        public string APIVersion { get; set; } = "1.0.0";

        /// <summary>最低引擎版本。</summary>
        public string MinEngineVersion { get; set; } = "0.1.0";

        /// <summary>入口类型全名。</summary>
        public string EntryPoint { get; set; } = string.Empty;

        /// <summary>插件程序集文件名。</summary>
        public string AssemblyPath { get; set; } = string.Empty;

        /// <summary>依赖列表。</summary>
        public List<PluginDependency> Dependencies { get; set; } = new List<PluginDependency>();

        /// <summary>模块列表。</summary>
        public List<PluginModuleManifest> Modules { get; set; } = new List<PluginModuleManifest>();

        /// <summary>是否在启动时自动启用。</summary>
        public bool AutoEnable { get; set; } = true;

        /// <summary>冲突的插件名称列表。</summary>
        public List<string> Conflicts { get; set; } = new List<string>();

        public SemanticVersion GetVersion() => SemanticVersion.Parse(Version);
        public SemanticVersion GetAPIVersion() => SemanticVersion.Parse(APIVersion);
        public SemanticVersion GetMinEngineVersion() => SemanticVersion.Parse(MinEngineVersion);

        public static PluginManifest FromJson(string json)
        {
            var manifest = JsonSerializer.Deserialize<PluginManifest>(json, _jsonOpts);
            if (manifest == null)
                throw new InvalidOperationException("插件清单反序列化失败。");
            return manifest;
        }

        public static PluginManifest FromFile(string filePath)
        {
            var json = File.ReadAllText(filePath);
            return FromJson(json);
        }

        public string ToJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>模块清单。</summary>
    public class PluginModuleManifest
    {
        public string Name { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public Dictionary<string, object> Settings { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>插件模块: 子系统注册 / 服务注册。</summary>
    public class PluginModule
    {
        public string Name { get; set; } = string.Empty;
        public Type ModuleType { get; set; }
        public Dictionary<string, object> Settings { get; set; } = new Dictionary<string, object>();

        private object _instance;

        /// <summary>初始化模块实例。</summary>
        public T CreateInstance<T>(Plugin owner) where T : class
        {
            if (ModuleType == null) return null;
            _instance = Activator.CreateInstance(ModuleType);
            return _instance as T;
        }

        /// <summary>获取模块实例。</summary>
        public T GetInstance<T>() where T : class => _instance as T;

        /// <summary>销毁模块实例。</summary>
        public void DestroyInstance()
        {
            (_instance as IDisposable)?.Dispose();
            _instance = null;
        }
    }

    /// <summary>冲突检测结果。</summary>
    public class PluginConflictInfo
    {
        public string PluginA { get; set; }
        public string PluginB { get; set; }
        public string Reason { get; set; }
        public string Suggestion { get; set; }

        public override string ToString() => $"[{PluginA}] <-> [{PluginB}]: {Reason}";
    }

    #endregion

    #region Plugin 基类

    /// <summary>
    /// 所有插件的基类。提供名称 / 版本 / 作者 / 描述 / 状态 / 依赖列表 / 模块列表 / 生命周期。
    /// </summary>
    public abstract class Plugin
    {
        /// <summary>插件名称。</summary>
        public string Name { get; protected set; } = string.Empty;

        /// <summary>显示名称。</summary>
        public string DisplayName { get; protected set; } = string.Empty;

        /// <summary>版本。</summary>
        public SemanticVersion Version { get; protected set; } = new SemanticVersion(1, 0, 0);

        /// <summary>作者。</summary>
        public string Author { get; protected set; } = "Unknown";

        /// <summary>描述。</summary>
        public string Description { get; protected set; } = string.Empty;

        /// <summary>API 版本。</summary>
        public SemanticVersion APIVersion { get; protected set; } = new SemanticVersion(1, 0, 0);

        /// <summary>当前状态。</summary>
        public PluginState State { get; internal set; } = PluginState.Unloaded;

        /// <summary>依赖列表。</summary>
        public List<PluginDependency> Dependencies { get; protected set; } = new List<PluginDependency>();

        /// <summary>模块列表。</summary>
        public List<PluginModule> Modules { get; protected set; } = new List<PluginModule>();

        /// <summary>清单。</summary>
        public PluginManifest Manifest { get; internal set; }

        /// <summary>加载上下文 (用于热卸载)。</summary>
        internal AssemblyLoadContext LoadContext { get; set; }

        /// <summary>插件程序集。</summary>
        internal Assembly PluginAssembly { get; set; }

        /// <summary>插件目录路径。</summary>
        public string PluginDirectory { get; internal set; } = string.Empty;

        // ── 生命周期回调 (子类覆写) ────────────────────────────────────

        /// <summary>插件加载时调用 (从 Unloaded → Loaded)。</summary>
        protected virtual void OnLoad() { }

        /// <summary>插件启用时调用 (从 Loaded → Active)。</summary>
        protected virtual void OnEnable() { }

        /// <summary>每帧更新。</summary>
        protected virtual void OnTick(float deltaTime) { }

        /// <summary>插件禁用时调用。</summary>
        protected virtual void OnDisable() { }

        /// <summary>插件卸载时调用 (→ Unloaded)。</summary>
        protected virtual void OnUnload() { }

        // ── 内部调用 ──────────────────────────────────────────────────

        internal void InternalLoad() { State = PluginState.Loading; OnLoad(); State = PluginState.Loaded; }
        internal void InternalEnable() { State = PluginState.Active; OnEnable(); }
        internal void InternalTick(float dt) { OnTick(dt); }
        internal void InternalDisable() { State = PluginState.Disabled; OnDisable(); }
        internal void InternalUnload() { State = PluginState.Unloading; OnUnload(); State = PluginState.Unloaded; }
    }

    #endregion

    #region 服务定位器

    /// <summary>
    /// 服务定位器: 插件间通信，避免硬引用。
    /// 通过注册/查询接口类型获取服务实例。
    /// </summary>
    public class PluginServiceLocator
    {
        private readonly Dictionary<Type, object> _services = new Dictionary<Type, object>();
        private readonly Dictionary<string, List<(Type Type, object Instance)>> _namedServices
            = new Dictionary<string, List<(Type, object)>>();
        private readonly object _lock = new object();

        /// <summary>注册服务。</summary>
        public void Register<T>(T service) where T : class
        {
            if (service == null) throw new ArgumentNullException(nameof(service));
            lock (_lock)
            {
                _services[typeof(T)] = service;
            }
        }

        /// <summary>注册命名服务 (同一接口多个实现)。</summary>
        public void Register<T>(string name, T service) where T : class
        {
            if (service == null) throw new ArgumentNullException(nameof(service));
            lock (_lock)
            {
                if (!_namedServices.TryGetValue(name, out var list))
                {
                    list = new List<(Type, object)>();
                    _namedServices[name] = list;
                }
                list.Add((typeof(T), service));
            }
        }

        /// <summary>获取服务。</summary>
        public T GetService<T>() where T : class
        {
            lock (_lock)
            {
                if (_services.TryGetValue(typeof(T), out var service))
                    return service as T;
            }
            return null;
        }

        /// <summary>尝试获取服务。</summary>
        public bool TryGetService<T>(out T service) where T : class
        {
            service = GetService<T>();
            return service != null;
        }

        /// <summary>获取命名服务列表。</summary>
        public List<T> GetNamedServices<T>(string name) where T : class
        {
            lock (_lock)
            {
                if (_namedServices.TryGetValue(name, out var list))
                    return list.Where(x => x.Type == typeof(T) || typeof(T).IsAssignableFrom(x.Type))
                               .Select(x => x.Instance as T)
                               .Where(x => x != null)
                               .ToList();
            }
            return new List<T>();
        }

        /// <summary>反注册服务。</summary>
        public void Unregister<T>() where T : class
        {
            lock (_lock)
            {
                _services.Remove(typeof(T));
            }
        }

        /// <summary>反注册命名服务。</summary>
        public void Unregister<T>(string name, T service) where T : class
        {
            lock (_lock)
            {
                if (_namedServices.TryGetValue(name, out var list))
                {
                    list.RemoveAll(x => x.Instance == service);
                    if (list.Count == 0) _namedServices.Remove(name);
                }
            }
        }

        /// <summary>是否已注册某服务。</summary>
        public bool IsRegistered<T>() where T : class
        {
            lock (_lock) { return _services.ContainsKey(typeof(T)); }
        }

        /// <summary>清除所有服务。</summary>
        public void Clear()
        {
            lock (_lock)
            {
                _services.Clear();
                _namedServices.Clear();
            }
        }
    }

    #endregion

    #region 依赖解析器

    /// <summary>
    /// 插件依赖解析器: 拓扑排序 / 循环依赖检测 / 缺失依赖报告。
    /// </summary>
    internal class PluginDependencyResolver
    {
        /// <summary>
        /// 解析加载顺序 (拓扑排序)。
        /// 返回 null 表示有循环依赖。
        /// </summary>
        public static List<string> ResolveLoadOrder(Dictionary<string, PluginManifest> manifests)
        {
            var graph = new Dictionary<string, List<string>>();
            var inDegree = new Dictionary<string, int>();

            foreach (var kvp in manifests)
            {
                var name = kvp.Key;
                if (!graph.ContainsKey(name)) graph[name] = new List<string>();
                if (!inDegree.ContainsKey(name)) inDegree[name] = 0;

                foreach (var dep in kvp.Value.Dependencies)
                {
                    if (!manifests.ContainsKey(dep.Name))
                        continue; // 缺失依赖会在检测阶段报告
                    graph[name].Add(dep.Name);
                    inDegree[name]++;
                }
            }

            var queue = new Queue<string>();
            foreach (var kvp in inDegree)
            {
                if (kvp.Value == 0) queue.Enqueue(kvp.Key);
            }

            var result = new List<string>();
            while (queue.Count > 0)
            {
                var node = queue.Dequeue();
                result.Add(node);
                foreach (var kvp in graph)
                {
                    if (kvp.Value.Remove(node))
                    {
                        inDegree[kvp.Key]--;
                        if (inDegree[kvp.Key] == 0) queue.Enqueue(kvp.Key);
                    }
                }
            }

            if (result.Count != manifests.Count) return null; // 有循环依赖
            return result;
        }

        /// <summary>检测循环依赖。</summary>
        public static List<string> DetectCycles(Dictionary<string, PluginManifest> manifests)
        {
            var visited = new HashSet<string>();
            var recursionStack = new HashSet<string>();
            var cycles = new List<string>();

            void DFS(string node, string path)
            {
                if (recursionStack.Contains(node))
                {
                    cycles.Add(path + " -> " + node + " (CYCLE)");
                    return;
                }
                if (visited.Contains(node)) return;

                visited.Add(node);
                recursionStack.Add(node);
                var currentPath = string.IsNullOrEmpty(path) ? node : path + " -> " + node;

                if (manifests.TryGetValue(node, out var manifest))
                {
                    foreach (var dep in manifest.Dependencies)
                    {
                        if (manifests.ContainsKey(dep.Name))
                            DFS(dep.Name, currentPath);
                    }
                }

                recursionStack.Remove(node);
            }

            foreach (var name in manifests.Keys)
            {
                DFS(name, string.Empty);
            }

            return cycles;
        }

        /// <summary>检测缺失依赖。</summary>
        public static List<(string Plugin, PluginDependency Dependency)> DetectMissingDependencies(
            Dictionary<string, PluginManifest> manifests)
        {
            var missing = new List<(string, PluginDependency)>();
            foreach (var kvp in manifests)
            {
                foreach (var dep in kvp.Value.Dependencies)
                {
                    if (dep.IsRequired && !manifests.ContainsKey(dep.Name))
                    {
                        missing.Add((kvp.Key, dep));
                    }
                }
            }
            return missing;
        }

        /// <summary>检测版本不兼容的依赖。</summary>
        public static List<(string Plugin, PluginDependency Dependency, SemanticVersion InstalledVersion)>
            DetectVersionMismatches(Dictionary<string, PluginManifest> manifests)
        {
            var mismatches = new List<(string, PluginDependency, SemanticVersion)>();
            foreach (var kvp in manifests)
            {
                foreach (var dep in kvp.Value.Dependencies)
                {
                    if (manifests.TryGetValue(dep.Name, out var depManifest))
                    {
                        var installedVersion = depManifest.GetVersion();
                        if (!dep.MatchesVersion(installedVersion))
                        {
                            mismatches.Add((kvp.Key, dep, installedVersion));
                        }
                    }
                }
            }
            return mismatches;
        }
    }

    #endregion

    #region PluginManager

    /// <summary>
    /// 插件管理器: 扫描 / 加载 / 卸载 / 启用 / 禁用 / 热加载 / 冲突检测。
    /// </summary>
    public class PluginManager
    {
        private readonly Dictionary<string, Plugin> _plugins = new Dictionary<string, Plugin>();
        private readonly Dictionary<string, PluginManifest> _manifests = new Dictionary<string, PluginManifest>();
        private readonly PluginServiceLocator _serviceLocator = new PluginServiceLocator();
        private readonly string _pluginDirectory;
        private readonly string _engineVersion;
        private readonly List<PluginConflictInfo> _conflicts = new List<PluginConflictInfo>();
        private readonly object _lock = new object();

        /// <summary>所有已注册插件。</summary>
        public IReadOnlyDictionary<string, Plugin> Plugins => _plugins;

        /// <summary>服务定位器。</summary>
        public PluginServiceLocator ServiceLocator => _serviceLocator;

        /// <summary>冲突列表。</summary>
        public IReadOnlyList<PluginConflictInfo> Conflicts => _conflicts;

        /// <summary>热重载事件: 加载前。</summary>
        public event Action<Plugin> OnBeforeReload;

        /// <summary>热重载事件: 加载后。</summary>
        public event Action<Plugin> OnAfterReload;

        /// <summary>热重载事件: 加载失败。</summary>
        public event Action<Plugin, Exception> OnReloadFailed;

        public PluginManager(string pluginDirectory, string engineVersion = "1.0.0")
        {
            _pluginDirectory = pluginDirectory;
            _engineVersion = engineVersion;
        }

        /// <summary>扫描插件目录。</summary>
        public void ScanPlugins()
        {
            lock (_lock)
            {
                if (!Directory.Exists(_pluginDirectory)) return;

                foreach (var dir in Directory.GetDirectories(_pluginDirectory))
                {
                    var manifestPath = Path.Combine(dir, "plugin.json");
                    if (!File.Exists(manifestPath)) continue;

                    try
                    {
                        var manifest = PluginManifest.FromFile(manifestPath);
                        _manifests[manifest.Name] = manifest;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[PluginManager] 解析插件清单失败: {manifestPath}, 错误: {ex.Message}");
                    }
                }
            }
        }

        /// <summary>检测冲突。</summary>
        public void DetectConflicts()
        {
            lock (_lock)
            {
                _conflicts.Clear();

                // 声明式冲突
                foreach (var kvp in _manifests)
                {
                    foreach (var conflictName in kvp.Value.Conflicts)
                    {
                        if (_manifests.ContainsKey(conflictName))
                        {
                            _conflicts.Add(new PluginConflictInfo
                            {
                                PluginA = kvp.Key,
                                PluginB = conflictName,
                                Reason = $"插件 {kvp.Key} 声明与 {conflictName} 冲突。",
                                Suggestion = $"请禁用其中一个插件。"
                            });
                        }
                    }
                }

                // API 版本不兼容
                var engineVer = SemanticVersion.Parse(_engineVersion);
                foreach (var kvp in _manifests)
                {
                    var minEngine = kvp.Value.GetMinEngineVersion();
                    if (engineVer < minEngine)
                    {
                        _conflicts.Add(new PluginConflictInfo
                        {
                            PluginA = kvp.Key,
                            PluginB = "Engine",
                            Reason = $"插件要求最低引擎版本 {minEngine}, 当前引擎版本 {engineVer}。",
                            Suggestion = $"请升级引擎或降低插件版本。"
                        });
                    }
                }
            }
        }

        /// <summary>加载所有插件 (按依赖顺序)。</summary>
        public bool LoadAll()
        {
            lock (_lock)
            {
                DetectConflicts();
                if (_conflicts.Count > 0)
                {
                    Console.WriteLine($"[PluginManager] 检测到 {_conflicts.Count} 个冲突，请先解决。");
                    foreach (var c in _conflicts) Console.WriteLine($"  - {c}");
                    return false;
                }

                var loadOrder = PluginDependencyResolver.ResolveLoadOrder(_manifests);
                if (loadOrder == null)
                {
                    var cycles = PluginDependencyResolver.DetectCycles(_manifests);
                    Console.WriteLine("[PluginManager] 检测到循环依赖:");
                    foreach (var c in cycles) Console.WriteLine($"  - {c}");
                    return false;
                }

                var missing = PluginDependencyResolver.DetectMissingDependencies(_manifests);
                foreach (var (plugin, dep) in missing)
                {
                    Console.WriteLine($"[PluginManager] 缺失依赖: [{plugin}] 需要 [{dep.Name}] ({dep.VersionRange})");
                }

                var mismatches = PluginDependencyResolver.DetectVersionMismatches(_manifests);
                foreach (var (plugin, dep, installed) in mismatches)
                {
                    Console.WriteLine($"[PluginManager] 版本不匹配: [{plugin}] 需要 [{dep.Name}] {dep.VersionRange}, 已安装 {installed}");
                }

                foreach (var name in loadOrder)
                {
                    var manifest = _manifests[name];
                    if (!manifest.AutoEnable) continue;
                    if (!LoadPlugin(name)) return false;
                }

                return true;
            }
        }

        /// <summary>加载单个插件。</summary>
        public bool LoadPlugin(string name)
        {
            lock (_lock)
            {
                if (_plugins.ContainsKey(name))
                {
                    Console.WriteLine($"[PluginManager] 插件已加载: {name}");
                    return true;
                }

                if (!_manifests.TryGetValue(name, out var manifest))
                {
                    Console.WriteLine($"[PluginManager] 未找到插件清单: {name}");
                    return false;
                }

                try
                {
                    var pluginDir = Path.Combine(_pluginDirectory, name);
                    var assemblyPath = Path.Combine(pluginDir, manifest.AssemblyPath);

                    if (!File.Exists(assemblyPath))
                    {
                        Console.WriteLine($"[PluginManager] 插件程序集不存在: {assemblyPath}");
                        return false;
                    }

                    var loadContext = new AssemblyLoadContext(name, isCollectible: true);
                    var assembly = loadContext.LoadFromAssemblyPath(assemblyPath);

                    Type entryType = null;
                    if (!string.IsNullOrEmpty(manifest.EntryPoint))
                    {
                        entryType = assembly.GetType(manifest.EntryPoint);
                    }
                    if (entryType == null)
                    {
                        var pluginAttr = assembly.GetCustomAttribute<PluginAPIAttribute>();
                        entryType = assembly.GetTypes()
                            .FirstOrDefault(t => typeof(Plugin).IsAssignableFrom(t) && !t.IsAbstract);
                    }

                    if (entryType == null)
                    {
                        Console.WriteLine($"[PluginManager] 未找到插件入口类型: {name}");
                        loadContext.Unload();
                        return false;
                    }

                    var plugin = (Plugin)Activator.CreateInstance(entryType);
                    plugin.Name = manifest.Name;
                    plugin.DisplayName = manifest.DisplayName;
                    plugin.Version = manifest.GetVersion();
                    plugin.Author = manifest.Author;
                    plugin.Description = manifest.Description;
                    plugin.APIVersion = manifest.GetAPIVersion();
                    plugin.Dependencies = manifest.Dependencies;
                    plugin.Manifest = manifest;
                    plugin.LoadContext = loadContext;
                    plugin.PluginAssembly = assembly;
                    plugin.PluginDirectory = pluginDir;

                    // 初始化模块
                    foreach (var modManifest in manifest.Modules)
                    {
                        var mod = new PluginModule
                        {
                            Name = modManifest.Name,
                            Settings = modManifest.Settings
                        };
                        if (!string.IsNullOrEmpty(modManifest.Type))
                        {
                            mod.ModuleType = assembly.GetType(modManifest.Type);
                        }
                        plugin.Modules.Add(mod);
                    }

                    plugin.InternalLoad();
                    if (manifest.AutoEnable) plugin.InternalEnable();

                    _plugins[name] = plugin;
                    return true;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[PluginManager] 加载插件失败: {name}, 错误: {ex.Message}");
                    return false;
                }
            }
        }

        /// <summary>卸载单个插件。</summary>
        public bool UnloadPlugin(string name)
        {
            lock (_lock)
            {
                if (!_plugins.TryGetValue(name, out var plugin)) return false;

                try
                {
                    plugin.InternalDisable();
                    plugin.InternalUnload();

                    // 销毁所有模块实例
                    foreach (var mod in plugin.Modules) mod.DestroyInstance();

                    // 卸载程序集
                    plugin.LoadContext?.Unload();
                    plugin.LoadContext = null;
                    plugin.PluginAssembly = null;

                    _plugins.Remove(name);
                    return true;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[PluginManager] 卸载插件失败: {name}, 错误: {ex.Message}");
                    return false;
                }
            }
        }

        /// <summary>启用插件。</summary>
        public bool EnablePlugin(string name)
        {
            lock (_lock)
            {
                if (!_plugins.TryGetValue(name, out var plugin)) return false;
                if (plugin.State == PluginState.Active) return true;
                if (plugin.State != PluginState.Loaded && plugin.State != PluginState.Disabled) return false;

                try
                {
                    plugin.InternalEnable();
                    return true;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[PluginManager] 启用插件失败: {name}, 错误: {ex.Message}");
                    return false;
                }
            }
        }

        /// <summary>禁用插件。</summary>
        public bool DisablePlugin(string name)
        {
            lock (_lock)
            {
                if (!_plugins.TryGetValue(name, out var plugin)) return false;
                if (plugin.State != PluginState.Active) return true;

                try
                {
                    plugin.InternalDisable();
                    return true;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[PluginManager] 禁用插件失败: {name}, 错误: {ex.Message}");
                    return false;
                }
            }
        }

        /// <summary>热加载: 运行时替换 DLL。</summary>
        public bool HotReloadPlugin(string name)
        {
            lock (_lock)
            {
                if (!_plugins.TryGetValue(name, out var oldPlugin)) return false;

                try
                {
                    OnBeforeReload?.Invoke(oldPlugin);

                    var manifest = oldPlugin.Manifest;
                    var pluginDir = oldPlugin.PluginDirectory;
                    var assemblyPath = Path.Combine(pluginDir, manifest.AssemblyPath);

                    // 卸载旧模块
                    foreach (var mod in oldPlugin.Modules) mod.DestroyInstance();

                    oldPlugin.InternalDisable();
                    oldPlugin.InternalUnload();

                    // 卸载旧程序集
                    oldPlugin.LoadContext?.Unload();

                    // 加载新程序集
                    var newContext = new AssemblyLoadContext(name, isCollectible: true);
                    var newAssembly = newContext.LoadFromAssemblyPath(assemblyPath);

                    Type entryType = null;
                    if (!string.IsNullOrEmpty(manifest.EntryPoint))
                        entryType = newAssembly.GetType(manifest.EntryPoint);
                    if (entryType == null)
                        entryType = newAssembly.GetTypes()
                            .FirstOrDefault(t => typeof(Plugin).IsAssignableFrom(t) && !t.IsAbstract);

                    if (entryType == null)
                    {
                        newContext.Unload();
                        oldPlugin.State = PluginState.Error;
                        OnReloadFailed?.Invoke(oldPlugin, new Exception("未找到入口类型"));
                        return false;
                    }

                    var newPlugin = (Plugin)Activator.CreateInstance(entryType);
                    newPlugin.Name = oldPlugin.Name;
                    newPlugin.DisplayName = oldPlugin.DisplayName;
                    newPlugin.Version = oldPlugin.Version;
                    newPlugin.Author = oldPlugin.Author;
                    newPlugin.Description = oldPlugin.Description;
                    newPlugin.APIVersion = oldPlugin.APIVersion;
                    newPlugin.Dependencies = oldPlugin.Dependencies;
                    newPlugin.Manifest = oldPlugin.Manifest;
                    newPlugin.LoadContext = newContext;
                    newPlugin.PluginAssembly = newAssembly;
                    newPlugin.PluginDirectory = oldPlugin.PluginDirectory;

                    // 重建模块
                    foreach (var modManifest in manifest.Modules)
                    {
                        var mod = new PluginModule
                        {
                            Name = modManifest.Name,
                            Settings = modManifest.Settings
                        };
                        if (!string.IsNullOrEmpty(modManifest.Type))
                            mod.ModuleType = newAssembly.GetType(modManifest.Type);
                        newPlugin.Modules.Add(mod);
                    }

                    newPlugin.InternalLoad();
                    newPlugin.InternalEnable();

                    _plugins[name] = newPlugin;
                    OnAfterReload?.Invoke(newPlugin);
                    return true;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[PluginManager] 热加载失败: {name}, 错误: {ex.Message}");
                    oldPlugin.State = PluginState.Error;
                    OnReloadFailed?.Invoke(oldPlugin, ex);
                    return false;
                }
            }
        }

        /// <summary>Tick 所有 Active 插件。</summary>
        public void TickAll(float deltaTime)
        {
            // 复制列表避免并发修改
            var snapshot = _plugins.Values.Where(p => p.State == PluginState.Active).ToList();
            foreach (var plugin in snapshot)
            {
                try
                {
                    plugin.InternalTick(deltaTime);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[PluginManager] Tick 插件异常: {plugin.Name}, 错误: {ex.Message}");
                }
            }
        }

        /// <summary>卸载所有插件。</summary>
        public void UnloadAll()
        {
            lock (_lock)
            {
                // 反序卸载
                var names = _plugins.Keys.ToList();
                for (int i = names.Count - 1; i >= 0; i--)
                {
                    UnloadPlugin(names[i]);
                }
                _plugins.Clear();
                _manifests.Clear();
                _conflicts.Clear();
                _serviceLocator.Clear();
            }
        }

        /// <summary>获取插件。</summary>
        public Plugin GetPlugin(string name)
        {
            lock (_lock)
            {
                _plugins.TryGetValue(name, out var plugin);
                return plugin;
            }
        }
    }

    #endregion
}
