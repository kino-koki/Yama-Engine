using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace YamaEngine.PluginSystem
{
    // ════════════════════════════════════════════════════════════════════════
    // yama引擎 - 插件核心系统 (PluginCoreSystem)
    //
    // 类似 UE Plugin System + Unity Package Manager + BepInEx:
    //   * 插件接口与基类 (IPlugin / PluginBase)
    //   * 插件元数据与描述符 (.plugin.json 清单解析)
    //   * 插件生命周期管理 (OnLoad→OnActivate→OnUpdate→OnDeactivate→OnUnload)
    //   * 语义化版本与版本范围匹配
    //   * 依赖解析 (拓扑排序 / 循环依赖检测 / 缺失依赖报告)
    //   * 插件发现 (扫描 Plugins 目录 / 内置 / 用户 / 工作目录 / 命令行)
    //   * 插件加载上下文 (AssemblyLoadContext 隔离 / 可卸载)
    //   * 插件配置 (独立配置 / 持久化 / 热重载 / 验证)
    //   * 插件日志 (专属日志 / 级别 / 过滤 / 文件)
    //   * 插件权限 (文件 / 网络 / 引擎API / 编辑器 / 用户数据)
    //   * 插件间通信 (事件总线 / 消息传递 / 服务注册)
    // ════════════════════════════════════════════════════════════════════════

    #region 版本系统

    /// <summary>语义化版本 (Major.Minor.Patch-PreRelease+Build)。</summary>
    public sealed class SemanticVersion : IComparable<SemanticVersion>, IEquatable<SemanticVersion>
    {
        public int Major { get; }
        public int Minor { get; }
        public int Patch { get; }
        public string PreRelease { get; }
        public string Build { get; }

        public bool IsPreRelease => !string.IsNullOrEmpty(PreRelease);

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
            if (TryParse(version, out var v)) return v;
            throw new FormatException($"版本格式错误: '{version}'");
        }

        public static bool TryParse(string version, out SemanticVersion result)
        {
            result = null;
            if (string.IsNullOrWhiteSpace(version)) return false;

            string main = version;
            string pre = string.Empty;
            string build = string.Empty;

            int plus = main.IndexOf('+');
            if (plus >= 0) { build = main.Substring(plus + 1); main = main.Substring(0, plus); }

            int dash = main.IndexOf('-');
            if (dash >= 0) { pre = main.Substring(dash + 1); main = main.Substring(0, dash); }

            var parts = main.Split('.');
            if (parts.Length < 2) return false;
            if (!int.TryParse(parts[0], out int maj)) return false;
            int min = parts.Length > 1 && int.TryParse(parts[1], out int m) ? m : 0;
            int pat = parts.Length > 2 && int.TryParse(parts[2], out int p) ? p : 0;

            result = new SemanticVersion(maj, min, pat, pre, build);
            return true;
        }

        public int CompareTo(SemanticVersion other)
        {
            if (other is null) return 1;
            int c = Major.CompareTo(other.Major); if (c != 0) return c;
            c = Minor.CompareTo(other.Minor); if (c != 0) return c;
            c = Patch.CompareTo(other.Patch); if (c != 0) return c;

            if (string.IsNullOrEmpty(PreRelease) && !string.IsNullOrEmpty(other.PreRelease)) return 1;
            if (!string.IsNullOrEmpty(PreRelease) && string.IsNullOrEmpty(other.PreRelease)) return -1;
            return string.Compare(PreRelease, other.PreRelease, StringComparison.Ordinal);
        }

        public bool Equals(SemanticVersion other) =>
            other != null && Major == other.Major && Minor == other.Minor &&
            Patch == other.Patch && PreRelease == other.PreRelease;

        public override bool Equals(object obj) => Equals(obj as SemanticVersion);
        public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, PreRelease);
        public override string ToString() =>
            $"{Major}.{Minor}.{Patch}" +
            (string.IsNullOrEmpty(PreRelease) ? "" : $"-{PreRelease}") +
            (string.IsNullOrEmpty(Build) ? "" : $"+{Build}");

        public static bool operator ==(SemanticVersion a, SemanticVersion b) =>
            a is null ? b is null : a.Equals(b);
        public static bool operator !=(SemanticVersion a, SemanticVersion b) => !(a == b);
        public static bool operator <(SemanticVersion a, SemanticVersion b) =>
            a is null ? b is not null : a.CompareTo(b) < 0;
        public static bool operator >(SemanticVersion a, SemanticVersion b) =>
            a is not null && a.CompareTo(b) > 0;
        public static bool operator <=(SemanticVersion a, SemanticVersion b) =>
            a is null || a.CompareTo(b) <= 0;
        public static bool operator >=(SemanticVersion a, SemanticVersion b) =>
            a is null ? b is null : a.CompareTo(b) >= 0;
    }

    /// <summary>版本范围匹配 (支持 *, ^1.2, ~1.2.3, >=1.0 &lt;2.0 等)。</summary>
    public sealed class VersionRange
    {
        public SemanticVersion Min { get; }
        public SemanticVersion Max { get; }
        public bool MinInclusive { get; }
        public bool MaxInclusive { get; }
        public bool MatchAny { get; }

        private VersionRange(SemanticVersion min, SemanticVersion max, bool minInc, bool maxInc, bool any)
        {
            Min = min; Max = max; MinInclusive = minInc; MaxInclusive = maxInc; MatchAny = any;
        }

        public static VersionRange Any() => new VersionRange(null, null, true, true, true);

        public static VersionRange Parse(string range)
        {
            if (string.IsNullOrWhiteSpace(range) || range.Trim() == "*")
                return Any();

            range = range.Trim();

            // Caret: ^1.2.3 → >=1.2.3 <2.0.0
            if (range.StartsWith("^"))
            {
                var v = SemanticVersion.Parse(range.Substring(1));
                var caretMax = new SemanticVersion(v.Major + 1, 0, 0);
                return new VersionRange(v, caretMax, true, false, false);
            }

            // Tilde: ~1.2.3 → >=1.2.3 <1.3.0
            if (range.StartsWith("~"))
            {
                var v = SemanticVersion.Parse(range.Substring(1));
                var tildeMax = new SemanticVersion(v.Major, v.Minor + 1, 0);
                return new VersionRange(v, tildeMax, true, false, false);
            }

            // Exact
            if (SemanticVersion.TryParse(range, out var exact))
            {
                return new VersionRange(exact, exact, true, true, false);
            }

            // Range: >=1.0 <2.0
            string minStr = null, maxStr = null;
            bool minInc = false, maxInc = false;

            var tokens = range.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var tok in tokens)
            {
                if (tok.StartsWith(">=")) { minStr = tok.Substring(2); minInc = true; }
                else if (tok.StartsWith(">")) { minStr = tok.Substring(1); minInc = false; }
                else if (tok.StartsWith("<=")) { maxStr = tok.Substring(2); maxInc = true; }
                else if (tok.StartsWith("<")) { maxStr = tok.Substring(1); maxInc = false; }
                else if (tok.StartsWith("==")) { minStr = tok.Substring(2); minInc = true; maxStr = minStr; maxInc = true; }
            }

            SemanticVersion min = minStr != null ? SemanticVersion.Parse(minStr) : null;
            SemanticVersion max = maxStr != null ? SemanticVersion.Parse(maxStr) : null;
            return new VersionRange(min, max, minInc, maxInc, false);
        }

        public bool Matches(SemanticVersion version)
        {
            if (MatchAny) return true;
            if (Min != null)
            {
                int c = version.CompareTo(Min);
                if (MinInclusive ? c < 0 : c <= 0) return false;
            }
            if (Max != null)
            {
                int c = version.CompareTo(Max);
                if (MaxInclusive ? c > 0 : c >= 0) return false;
            }
            return true;
        }

        public override string ToString()
        {
            if (MatchAny) return "*";
            if (Min != null && Max != null && Min == Max) return $"=={Min}";
            string s = "";
            if (Min != null) s += (MinInclusive ? ">=" : ">") + Min;
            if (Max != null) s += (s.Length > 0 ? " " : "") + (MaxInclusive ? "<=" : "<") + Max;
            return s;
        }
    }

    #endregion

    #region 枚举

    /// <summary>插件状态。</summary>
    public enum PluginState
    {
        Unloaded,
        Loading,
        Loaded,
        Initialized,
        Activating,
        Active,
        Deactivating,
        Deactivating_Wait,
        Error,
        Disabled
    }

    /// <summary>插件加载阶段。</summary>
    public enum PluginLoadPhase
    {
        Startup,
        PreEngine,
        PostEngine,
        PreWorld,
        PostWorld,
        OnDemand
    }

    /// <summary>日志级别。</summary>
    public enum PluginLogLevel
    {
        Trace = 0,
        Debug = 1,
        Info = 2,
        Warning = 3,
        Error = 4,
        Fatal = 5,
        None = 6
    }

    #endregion

    #region 插件权限

    /// <summary>插件权限标志。</summary>
    [Flags]
    public enum PluginPermissionFlags : ulong
    {
        None = 0,
        FileSystemRead = 1 << 0,
        FileSystemWrite = 1 << 1,
        NetworkAccess = 1 << 2,
        EngineAPI = 1 << 3,
        EditorAccess = 1 << 4,
        UserData = 1 << 5,
        NativeInterop = 1 << 6,
        ProcessControl = 1 << 7,
        ReflectionEmit = 1 << 8,
        All = ~0UL
    }

    /// <summary>插件权限。</summary>
    public sealed class PluginPermission
    {
        public string PluginId { get; set; }
        public PluginPermissionFlags Granted { get; set; } = PluginPermissionFlags.None;
        public PluginPermissionFlags Requested { get; set; } = PluginPermissionFlags.None;
        public List<string> AllowedPaths { get; } = new List<string>();
        public List<string> DeniedPaths { get; } = new List<string>();
        public List<string> AllowedDomains { get; } = new List<string>();

        public bool HasPermission(PluginPermissionFlags flag) => (Granted & flag) == flag;

        public bool RequestPermission(PluginPermissionFlags flag)
        {
            if ((Requested & flag) == flag) return HasPermission(flag);
            Requested |= flag;
            // Auto-grant based on trust level (simplified)
            return Grant(flag);
        }

        public bool Grant(PluginPermissionFlags flag)
        {
            Granted |= flag;
            return true;
        }

        public void Revoke(PluginPermissionFlags flag) => Granted &= ~flag;

        public bool CanAccessPath(string path, bool write = false)
        {
            if (!HasPermission(write ? PluginPermissionFlags.FileSystemWrite : PluginPermissionFlags.FileSystemRead))
                return false;
            string norm = Path.GetFullPath(path).Replace('\\', '/');
            foreach (var denied in DeniedPaths)
                if (norm.StartsWith(Path.GetFullPath(denied).Replace('\\', '/')))
                    return false;
            if (AllowedPaths.Count == 0) return true;
            foreach (var allowed in AllowedPaths)
                if (norm.StartsWith(Path.GetFullPath(allowed).Replace('\\', '/')))
                    return true;
            return false;
        }

        public bool CanAccessNetwork(string domain = null)
        {
            if (!HasPermission(PluginPermissionFlags.NetworkAccess)) return false;
            if (string.IsNullOrEmpty(domain)) return true;
            return AllowedDomains.Count == 0 || AllowedDomains.Contains(domain);
        }
    }

    #endregion

    #region 插件依赖

    /// <summary>插件依赖描述。</summary>
    public sealed class PluginDependency
    {
        public string PluginName { get; set; } = string.Empty;
        public string VersionRangeStr { get; set; } = "*";
        public bool IsRequired { get; set; } = true;
        public string DisplayName { get; set; } = string.Empty;

        [JsonIgnore]
        public VersionRange VersionRange => VersionRange.Parse(VersionRangeStr);

        public bool MatchesVersion(SemanticVersion version) => VersionRange.Matches(version);

        public override string ToString() =>
            $"{PluginName} {VersionRangeStr}{(IsRequired ? "" : " (optional)")}";
    }

    #endregion

    #region 插件元数据

    /// <summary>插件元数据。</summary>
    public sealed class PluginInfo
    {
        public string Name { get; set; } = string.Empty;
        public string VersionStr { get; set; } = "0.0.1";
        public string Description { get; set; } = string.Empty;
        public string Author { get; set; } = string.Empty;
        public string Website { get; set; } = string.Empty;
        public string License { get; set; } = string.Empty;
        public List<PluginDependency> Dependencies { get; set; } = new List<PluginDependency>();
        public List<string> Conflicts { get; set; } = new List<string>();
        public List<string> Tags { get; set; } = new List<string>();
        public int LoadPriority { get; set; } = 100;
        public string EngineCompatVersion { get; set; } = "*";
        public PluginLoadPhase LoadPhase { get; set; } = PluginLoadPhase.PostEngine;
        public PluginPermissionFlags RequiredPermissions { get; set; } = PluginPermissionFlags.None;

        [JsonIgnore]
        public SemanticVersion Version => SemanticVersion.TryParse(VersionStr, out var v) ? v : new SemanticVersion(0, 0, 1);

        [JsonIgnore]
        public VersionRange EngineCompatRange => VersionRange.Parse(EngineCompatVersion);

        public bool IsCompatibleWithEngine(SemanticVersion engineVersion) =>
            EngineCompatRange.Matches(engineVersion);

        public override string ToString() => $"{Name} v{VersionStr} by {Author}";
    }

    #endregion

    #region 插件描述符

    /// <summary>插件描述符 (从 .plugin.json 解析)。</summary>
    public sealed class PluginDescriptor
    {
        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public PluginInfo Info { get; set; } = new PluginInfo();
        public string PluginPath { get; set; } = string.Empty;
        public string EntryDll { get; set; } = string.Empty;
        public string EntryClass { get; set; } = string.Empty;
        public string AssetsPath { get; set; } = string.Empty;
        public string ConfigPath { get; set; } = string.Empty;
        public Dictionary<string, object> DefaultConfig { get; set; } = new Dictionary<string, object>();
        public bool IsBuiltIn { get; set; } = false;
        public bool IsEnabled { get; set; } = true;
        public bool IsCollectible { get; set; } = true;
        public string PluginId { get; set; } = string.Empty;

        [JsonIgnore]
        public string FullDllPath => string.IsNullOrEmpty(EntryDll)
            ? string.Empty
            : Path.IsPathRooted(EntryDll) ? EntryDll : Path.Combine(PluginPath, EntryDll);

        [JsonIgnore]
        public string FullAssetsPath => string.IsNullOrEmpty(AssetsPath)
            ? string.Empty
            : Path.IsPathRooted(AssetsPath) ? AssetsPath : Path.Combine(PluginPath, AssetsPath);

        [JsonIgnore]
        public string FullConfigPath => string.IsNullOrEmpty(ConfigPath)
            ? Path.Combine(PluginPath, "config.json")
            : Path.IsPathRooted(ConfigPath) ? ConfigPath : Path.Combine(PluginPath, ConfigPath);

        /// <summary>从 .plugin.json 文件加载描述符。</summary>
        public static PluginDescriptor LoadFromFile(string path)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException($"插件清单文件不存在: {path}");

            string json = File.ReadAllText(path);
            var desc = JsonSerializer.Deserialize<PluginDescriptor>(json, JsonOpts) ?? new PluginDescriptor();
            desc.PluginPath = Path.GetDirectoryName(path);
            desc.PluginId = string.IsNullOrEmpty(desc.PluginId) ? desc.Info.Name : desc.PluginId;

            if (string.IsNullOrEmpty(desc.EntryDll))
                desc.EntryDll = desc.Info.Name + ".dll";

            return desc;
        }

        /// <summary>保存描述符到 .plugin.json。</summary>
        public void SaveToFile(string path = null)
        {
            path ??= Path.Combine(PluginPath, "plugin.json");
            string json = JsonSerializer.Serialize(this, JsonOpts);
            File.WriteAllText(path, json);
        }

        /// <summary>尝试加载描述符 (不抛异常)。</summary>
        public static bool TryLoadFromFile(string path, out PluginDescriptor descriptor)
        {
            descriptor = null;
            try
            {
                if (!File.Exists(path)) return false;
                descriptor = LoadFromFile(path);
                return true;
            }
            catch { return false; }
        }
    }

    #endregion

    #region 插件配置

    /// <summary>插件配置 (独立 / 持久化 / 热重载)。</summary>
    public sealed class PluginConfig
    {
        private readonly ConcurrentDictionary<string, object> _values = new ConcurrentDictionary<string, object>();
        private readonly Dictionary<string, object> _defaults = new Dictionary<string, object>();
        private string _filePath;
        private FileSystemWatcher _watcher;
        private readonly object _lock = new object();

        public string PluginId { get; }
        public bool AutoReload { get; set; } = true;
        public event Action<PluginConfig> OnReloaded;

        public PluginConfig(string pluginId, string filePath = null, Dictionary<string, object> defaults = null)
        {
            PluginId = pluginId;
            _filePath = filePath;
            if (defaults != null)
                foreach (var kv in defaults)
                    _defaults[kv.Key] = kv.Value;
        }

        public T Get<T>(string key, T defaultValue = default)
        {
            if (_values.TryGetValue(key, out var val))
            {
                if (val is T typed) return typed;
                try { return (T)Convert.ChangeType(val, typeof(T)); } catch { }
            }
            if (_defaults.TryGetValue(key, out var def))
            {
                if (def is T defTyped) return defTyped;
                try { return (T)Convert.ChangeType(def, typeof(T)); } catch { }
            }
            return defaultValue;
        }

        public void Set<T>(string key, T value)
        {
            _values[key] = value;
        }

        public bool Has(string key) => _values.ContainsKey(key) || _defaults.ContainsKey(key);

        public bool Remove(string key) => _values.TryRemove(key, out _);

        public IEnumerable<string> GetKeys() => _values.Keys.Union(_defaults.Keys).Distinct();

        public void Load()
        {
            lock (_lock)
            {
                if (string.IsNullOrEmpty(_filePath) || !File.Exists(_filePath)) return;
                try
                {
                    string json = File.ReadAllText(_filePath);
                    var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
                    if (dict != null)
                    {
                        _values.Clear();
                        foreach (var kv in dict)
                        {
                            _values[kv.Key] = kv.Value.ValueKind switch
                            {
                                JsonValueKind.String => kv.Value.GetString(),
                                JsonValueKind.Number => kv.Value.TryGetInt32(out int i) ? (object)i : kv.Value.GetDouble(),
                                JsonValueKind.True => true,
                                JsonValueKind.False => false,
                                _ => kv.Value.GetRawText()
                            };
                        }
                    }
                    OnReloaded?.Invoke(this);
                }
                catch { }
            }
        }

        public void Save()
        {
            lock (_lock)
            {
                if (string.IsNullOrEmpty(_filePath)) return;
                var dir = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                var dict = new Dictionary<string, object>();
                foreach (var key in GetKeys())
                {
                    if (_values.TryGetValue(key, out var val))
                        dict[key] = val;
                    else if (_defaults.TryGetValue(key, out var def))
                        dict[key] = def;
                }
                string json = JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_filePath, json);
            }
        }

        public void StartWatching()
        {
            if (!AutoReload || string.IsNullOrEmpty(_filePath) || !File.Exists(_filePath)) return;
            string dir = Path.GetDirectoryName(_filePath);
            string name = Path.GetFileName(_filePath);
            _watcher = new FileSystemWatcher(dir, name)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true
            };
            _watcher.Changed += (s, e) => { Thread.Sleep(100); Load(); };
        }

        public void StopWatching()
        {
            if (_watcher != null)
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Dispose();
                _watcher = null;
            }
        }

        public bool Validate<T>(string key, Func<T, bool> validator)
        {
            var val = Get<T>(key);
            return val != null && validator(val);
        }

        public void SetDefaults(Dictionary<string, object> defaults)
        {
            foreach (var kv in defaults)
                _defaults[kv.Key] = kv.Value;
        }
    }

    #endregion

    #region 插件日志

    /// <summary>插件专属日志。</summary>
    public sealed class PluginLogger
    {
        public string PluginId { get; }
        public PluginLogLevel Level { get; set; } = PluginLogLevel.Info;
        public string LogDirectory { get; set; }
        public bool LogToFile { get; set; } = true;
        public bool LogToConsole { get; set; } = true;

        private readonly object _lock = new object();
        private StreamWriter _fileWriter;
        private DateTime _fileDate;
        private readonly List<PluginLogLevel> _levelFilter = new List<PluginLogLevel>();

        public PluginLogger(string pluginId, string logDir = null)
        {
            PluginId = pluginId;
            LogDirectory = logDir ?? Path.Combine(AppContext.BaseDirectory, "Logs", "Plugins");
        }

        private string GetLogFilePath()
        {
            if (!Directory.Exists(LogDirectory))
                Directory.CreateDirectory(LogDirectory);
            return Path.Combine(LogDirectory, $"{PluginId}_{DateTime.Now:yyyyMMdd}.log");
        }

        private void EnsureWriter()
        {
            if (!LogToFile) return;
            DateTime today = DateTime.Today;
            if (_fileWriter == null || _fileDate != today)
            {
                _fileDate = today;
                _fileWriter?.Dispose();
                _fileWriter = new StreamWriter(GetLogFilePath(), true) { AutoFlush = true };
            }
        }

        public void Trace(string msg) => Write(PluginLogLevel.Trace, msg);
        public void Debug(string msg) => Write(PluginLogLevel.Debug, msg);
        public void Info(string msg) => Write(PluginLogLevel.Info, msg);
        public void Warning(string msg) => Write(PluginLogLevel.Warning, msg);
        public void Warning(string msg, Exception ex) => Write(PluginLogLevel.Warning, $"{msg}\n{ex}");
        public void Error(string msg) => Write(PluginLogLevel.Error, msg);
        public void Error(string msg, Exception ex) => Write(PluginLogLevel.Error, $"{msg}\n{ex}");
        public void Fatal(string msg) => Write(PluginLogLevel.Fatal, msg);
        public void Fatal(string msg, Exception ex) => Write(PluginLogLevel.Fatal, $"{msg}\n{ex}");

        public void Write(PluginLogLevel level, string message)
        {
            if (level < Level) return;
            if (_levelFilter.Count > 0 && !_levelFilter.Contains(level)) return;

            string line = $"[{DateTime.Now:HH:mm:ss.fff}] [{level}] [{PluginId}] {message}";

            lock (_lock)
            {
                if (LogToConsole)
                {
                    var prev = Console.ForegroundColor;
                    Console.ForegroundColor = level switch
                    {
                        PluginLogLevel.Trace => ConsoleColor.DarkGray,
                        PluginLogLevel.Debug => ConsoleColor.Gray,
                        PluginLogLevel.Info => ConsoleColor.White,
                        PluginLogLevel.Warning => ConsoleColor.Yellow,
                        PluginLogLevel.Error => ConsoleColor.Red,
                        PluginLogLevel.Fatal => ConsoleColor.DarkRed,
                        _ => ConsoleColor.White
                    };
                    Console.WriteLine(line);
                    Console.ForegroundColor = prev;
                }

                if (LogToFile)
                {
                    EnsureWriter();
                    _fileWriter?.WriteLine(line);
                }
            }
        }

        public void AddLevelFilter(PluginLogLevel level) { if (!_levelFilter.Contains(level)) _levelFilter.Add(level); }
        public void RemoveLevelFilter(PluginLogLevel level) => _levelFilter.Remove(level);
        public void ClearLevelFilter() => _levelFilter.Clear();

        public void Dispose()
        {
            lock (_lock) { _fileWriter?.Dispose(); _fileWriter = null; }
        }
    }

    #endregion

    #region 插件接口与基类

    /// <summary>插件接口。</summary>
    public interface IPlugin
    {
        PluginInfo Info { get; }
        PluginState State { get; }
        PluginDescriptor Descriptor { get; }

        void OnLoad(PluginContext context);
        void OnUnload();
        void OnUpdate(float deltaTime);

        // 生命周期扩展
        void OnActivate();
        void OnDeactivate();
    }

    /// <summary>插件运行时上下文。</summary>
    public sealed class PluginContext
    {
        public PluginDescriptor Descriptor { get; }
        public PluginConfig Config { get; internal set; }
        public PluginLogger Logger { get; internal set; }
        public PluginPermission Permission { get; internal set; }
        public PluginLoadContext LoadContext { get; internal set; }
        public PluginManager Manager { get; internal set; }

        // 服务定位器快捷方式
        public T GetService<T>() where T : class => Manager?.GetService<T>();
        public object GetService(Type serviceType) => Manager?.GetService(serviceType);

        // 事件总线快捷方式
        public void Subscribe<T>(Action<T> handler) where T : PluginEvent
            => Manager?.EventBus?.Subscribe<T>(handler, Descriptor.PluginId);
        public void Publish<T>(T evt) where T : PluginEvent
            => Manager?.EventBus?.Publish(evt);

        public PluginContext(PluginDescriptor descriptor)
        {
            Descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
        }
    }

    /// <summary>插件基类。</summary>
    public abstract class PluginBase : IPlugin
    {
        public abstract PluginInfo Info { get; }
        public PluginState State { get; internal set; } = PluginState.Unloaded;
        public PluginDescriptor Descriptor { get; internal set; }

        protected PluginContext Context { get; private set; }
        protected PluginConfig Config => Context?.Config;
        protected PluginLogger Logger => Context?.Logger;
        protected PluginPermission Permission => Context?.Permission;

        private float _accumulatedTime;
        private int _updateCount;

        public virtual void OnLoad(PluginContext context)
        {
            Context = context;
            State = PluginState.Loaded;
            Logger?.Info($"插件 {Info.Name} v{Info.VersionStr} 已加载。");
        }

        public virtual void OnUnload()
        {
            State = PluginState.Unloaded;
            Logger?.Info($"插件 {Info.Name} 已卸载。");
            Context = null;
        }

        public virtual void OnActivate()
        {
            State = PluginState.Active;
            Logger?.Debug($"插件 {Info.Name} 已激活。");
        }

        public virtual void OnDeactivate()
        {
            State = PluginState.Deactivating;
            Logger?.Debug($"插件 {Info.Name} 已停用。");
        }

        public virtual void OnUpdate(float deltaTime)
        {
            _accumulatedTime += deltaTime;
            _updateCount++;
        }

        // 辅助方法
        protected void LogInfo(string msg) => Logger?.Info(msg);
        protected void LogWarning(string msg) => Logger?.Warning(msg);
        protected void LogError(string msg) => Logger?.Error(msg);
        protected void LogError(string msg, Exception ex) => Logger?.Error(msg, ex);

        public TimeSpan GetAccumulatedTime() => TimeSpan.FromSeconds(_accumulatedTime);
        public int GetUpdateCount() => _updateCount;
    }

    #endregion

    #region 插件加载上下文

    /// <summary>插件加载上下文 (AssemblyLoadContext 隔离 / 可卸载)。</summary>
    public class PluginLoadContext : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver _resolver;
        private readonly string _pluginPath;
        private readonly List<string> _probingPaths = new List<string>();

        public string PluginPath => _pluginPath;
        public bool IsCollectible { get; }
        public List<Assembly> LoadedAssemblies { get; } = new List<Assembly>();

        public PluginLoadContext(string pluginPath, bool isCollectible = true)
            : base(name: Path.GetFileNameWithoutExtension(pluginPath), isCollectible)
        {
            _pluginPath = pluginPath;
            IsCollectible = isCollectible;
            _resolver = new AssemblyDependencyResolver(pluginPath);

            // Add plugin directory as probing path
            string pluginDir = Path.GetDirectoryName(pluginPath);
            if (!string.IsNullOrEmpty(pluginDir))
                _probingPaths.Add(pluginDir);
        }

        public void AddProbingPath(string path)
        {
            if (Directory.Exists(path) && !_probingPaths.Contains(path))
                _probingPaths.Add(path);
        }

        protected override Assembly Load(AssemblyName assemblyName)
        {
            // 1. Try resolver (deps.json)
            string assemblyPath = _resolver.ResolveAssemblyToPath(assemblyName);
            if (assemblyPath != null)
            {
                var asm = LoadFromAssemblyPath(assemblyPath);
                LoadedAssemblies.Add(asm);
                return asm;
            }

            // 2. Try probing paths
            foreach (var path in _probingPaths)
            {
                string candidate = Path.Combine(path, assemblyName.Name + ".dll");
                if (File.Exists(candidate))
                {
                    var asm = LoadFromAssemblyPath(candidate);
                    LoadedAssemblies.Add(asm);
                    return asm;
                }
            }

            // 3. Return null to fall back to default context (shared assemblies)
            return null;
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            string libraryPath = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            if (libraryPath != null)
                return LoadUnmanagedDllFromPath(libraryPath);
            return IntPtr.Zero;
        }

        public Assembly LoadPluginAssembly(string assemblyPath)
        {
            if (!Path.IsPathRooted(assemblyPath))
                assemblyPath = Path.Combine(_pluginPath, assemblyPath);
            var asm = LoadFromAssemblyPath(assemblyPath);
            if (!LoadedAssemblies.Contains(asm))
                LoadedAssemblies.Add(asm);
            return asm;
        }

        /// <summary>尝试卸载 (仅 collectible 上下文)。</summary>
        public bool TryUnload()
        {
            if (!IsCollectible) return false;
            try
            {
                Unload();
                return true;
            }
            catch { return false; }
        }
    }

    #endregion

    #region 已加载插件容器

    /// <summary>已加载的插件实例容器。</summary>
    internal sealed class LoadedPlugin
    {
        public PluginDescriptor Descriptor { get; set; }
        public IPlugin Instance { get; set; }
        public PluginContext Context { get; set; }
        public PluginLoadContext LoadContext { get; set; }
        public PluginState State { get => Instance?.State ?? PluginState.Unloaded; }
        public Assembly Assembly { get; set; }
        public DateTime LoadTime { get; set; }
        public List<string> LoadErrors { get; } = new List<string>();
        public WeakReference UnloadReference { get; set; }

        public override string ToString() => $"{Descriptor?.Info?.Name} [{State}]";
    }

    #endregion

    #region 插件管理器

    /// <summary>插件管理器 (单例)。</summary>
    public sealed class PluginManager
    {
        private static readonly Lazy<PluginManager> _instance =
            new Lazy<PluginManager>(() => new PluginManager());
        public static PluginManager Instance => _instance.Value;

        // 插件存储
        private readonly ConcurrentDictionary<string, LoadedPlugin> _plugins = new ConcurrentDictionary<string, LoadedPlugin>();
        private readonly ConcurrentDictionary<string, PluginDescriptor> _discovered = new ConcurrentDictionary<string, PluginDescriptor>();
        private readonly List<string> _searchPaths = new List<string>();
        private readonly object _loadLock = new object();

        // 引擎版本
        public SemanticVersion EngineVersion { get; set; } = new SemanticVersion(1, 0, 0);

        // 子系统引用 (延迟设置)
        public PluginEventBus EventBus { get; set; }
        public PluginServiceRegistry ServiceRegistry { get; set; }

        // 配置
        public string PluginsRootDirectory { get; set; } = Path.Combine(AppContext.BaseDirectory, "Plugins");
        public string UserPluginsDirectory { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "YamaEngine", "Plugins");
        public string BuiltInPluginsDirectory { get; set; } = Path.Combine(AppContext.BaseDirectory, "BuiltInPlugins");
        public bool AutoDiscoverOnStartup { get; set; } = true;
        public bool EnablePluginSandbox { get; set; } = true;

        // 日志
        private readonly PluginLogger _logger = new PluginLogger("PluginManager");

        // 事件
        public event Action<IPlugin> OnPluginLoaded;
        public event Action<IPlugin> OnPluginUnloaded;
        public event Action<IPlugin> OnPluginActivated;
        public event Action<IPlugin> OnPluginDeactivated;
        public event Action<string, Exception> OnPluginError;

        private PluginManager()
        {
            _searchPaths.Add(PluginsRootDirectory);
            _searchPaths.Add(UserPluginsDirectory);
            _searchPaths.Add(BuiltInPluginsDirectory);
        }

        #region 发现

        /// <summary>添加搜索路径。</summary>
        public void AddSearchPath(string path)
        {
            if (Directory.Exists(path) && !_searchPaths.Contains(path))
                _searchPaths.Add(path);
        }

        /// <summary>发现所有可用插件。</summary>
        public List<PluginDescriptor> DiscoverPlugins()
        {
            _discovered.Clear();
            var result = new List<PluginDescriptor>();

            foreach (var searchPath in _searchPaths)
            {
                if (!Directory.Exists(searchPath)) continue;
                DiscoverInDirectory(searchPath, result);
            }

            // 命令行指定的插件
            var cmdline = Environment.GetCommandLineArgs();
            for (int i = 0; i < cmdline.Length - 1; i++)
            {
                if (cmdline[i] == "--plugin" && Directory.Exists(cmdline[i + 1]))
                {
                    DiscoverInDirectory(cmdline[i + 1], result);
                }
            }

            _logger.Info($"发现 {result.Count} 个插件。");
            return result;
        }

        private void DiscoverInDirectory(string dir, List<PluginDescriptor> result)
        {
            try
            {
                // 搜索 plugin.json 或 .plugin.json
                var files = Directory.GetFiles(dir, "plugin.json", SearchOption.AllDirectories)
                    .Concat(Directory.GetFiles(dir, ".plugin.json", SearchOption.AllDirectories));

                foreach (var file in files)
                {
                    if (PluginDescriptor.TryLoadFromFile(file, out var desc))
                    {
                        if (string.IsNullOrEmpty(desc.PluginId))
                            desc.PluginId = desc.Info.Name;

                        _discovered[desc.PluginId] = desc;
                        result.Add(desc);
                        _logger.Debug($"发现插件: {desc.Info}");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"扫描目录 {dir} 失败", ex);
            }
        }

        /// <summary>获取所有已发现的插件描述符。</summary>
        public IReadOnlyDictionary<string, PluginDescriptor> GetDiscoveredPlugins() => _discovered;

        #endregion

        #region 依赖解析与排序

        /// <summary>解析依赖关系，返回拓扑排序后的加载顺序。</summary>
        public List<PluginDescriptor> ResolveDependencies(List<PluginDescriptor> descriptors)
        {
            var descMap = descriptors.ToDictionary(d => d.PluginId ?? d.Info.Name);
            var sorted = new List<PluginDescriptor>();
            var visited = new HashSet<string>();
            var visiting = new HashSet<string>();
            var errors = new List<string>();

            void Visit(PluginDescriptor desc, int depth = 0)
            {
                string id = desc.PluginId ?? desc.Info.Name;
                if (visited.Contains(id)) return;
                if (visiting.Contains(id))
                {
                    errors.Add($"检测到循环依赖: {id}");
                    return;
                }
                if (depth > 64)
                {
                    errors.Add($"依赖深度超过限制: {id}");
                    return;
                }

                visiting.Add(id);

                // 检查依赖
                foreach (var dep in desc.Info.Dependencies)
                {
                    string depId = dep.PluginName;
                    if (!descMap.ContainsKey(depId))
                    {
                        if (dep.IsRequired)
                            errors.Add($"缺失必需依赖: {id} -> {depId}");
                        continue;
                    }

                    // 版本检查
                    var depDesc = descMap[depId];
                    if (!dep.MatchesVersion(depDesc.Info.Version))
                    {
                        errors.Add($"依赖版本不匹配: {id} 需要 {dep}, 实际 {depDesc.Info.Version}");
                    }

                    Visit(depDesc, depth + 1);
                }

                visiting.Remove(id);
                visited.Add(id);
                sorted.Add(desc);
            }

            // 按 LoadPriority 排序后拓扑
            foreach (var desc in descriptors.OrderBy(d => d.Info.LoadPriority))
                Visit(desc);

            // 冲突检测
            foreach (var desc in descriptors)
            {
                foreach (var conflict in desc.Info.Conflicts)
                {
                    if (descMap.ContainsKey(conflict))
                        errors.Add($"插件冲突: {desc.PluginId} 与 {conflict} 不兼容");
                }
            }

            if (errors.Count > 0)
            {
                foreach (var err in errors)
                    _logger.Error(err);
            }

            return sorted;
        }

        /// <summary>检测冲突。</summary>
        public List<string> DetectConflicts(List<PluginDescriptor> descriptors)
        {
            var conflicts = new List<string>();
            var descMap = descriptors.ToDictionary(d => d.PluginId ?? d.Info.Name);

            foreach (var desc in descriptors)
            {
                foreach (var c in desc.Info.Conflicts)
                {
                    if (descMap.ContainsKey(c))
                        conflicts.Add($"{desc.PluginId} 与 {c} 冲突");
                }
            }
            return conflicts;
        }

        #endregion

        #region 加载与卸载

        /// <summary>加载所有已发现的插件 (按依赖顺序)。</summary>
        public void LoadAll(PluginLoadPhase phase = PluginLoadPhase.PostEngine)
        {
            if (AutoDiscoverOnStartup && _discovered.Count == 0)
                DiscoverPlugins();

            var phasePlugins = _discovered.Values
                .Where(d => d.Info.LoadPhase == phase && d.IsEnabled)
                .ToList();

            var sorted = ResolveDependencies(phasePlugins);
            foreach (var desc in sorted)
            {
                LoadPlugin(desc);
            }
        }

        /// <summary>加载单个插件。</summary>
        public bool LoadPlugin(PluginDescriptor descriptor)
        {
            lock (_loadLock)
            {
                string id = descriptor.PluginId ?? descriptor.Info.Name;

                if (_plugins.ContainsKey(id))
                {
                    _logger.Warning($"插件 {id} 已加载，跳过。");
                    return true;
                }

                var loaded = new LoadedPlugin { Descriptor = descriptor, LoadTime = DateTime.Now };

                try
                {
                    // 引擎兼容性检查
                    if (!descriptor.Info.IsCompatibleWithEngine(EngineVersion))
                    {
                        _logger.Error($"插件 {id} 不兼容引擎版本 {EngineVersion} (要求 {descriptor.Info.EngineCompatVersion})");
                        return false;
                    }

                    // 创建加载上下文
                    loaded.LoadContext = new PluginLoadContext(descriptor.FullDllPath, descriptor.IsCollectible);
                    if (Directory.Exists(descriptor.FullAssetsPath))
                        loaded.LoadContext.AddProbingPath(descriptor.FullAssetsPath);

                    // 加载程序集
                    loaded.Assembly = loaded.LoadContext.LoadPluginAssembly(descriptor.EntryDll);

                    // 查找入口类型
                    Type entryType = null;
                    if (!string.IsNullOrEmpty(descriptor.EntryClass))
                        entryType = loaded.Assembly.GetType(descriptor.EntryClass);

                    if (entryType == null)
                    {
                        // 查找实现 IPlugin 的类型
                        foreach (var type in loaded.Assembly.GetTypes())
                        {
                            if (typeof(IPlugin).IsAssignableFrom(type) && !type.IsAbstract && !type.IsInterface)
                            {
                                entryType = type;
                                break;
                            }
                        }
                    }

                    if (entryType == null)
                    {
                        _logger.Error($"插件 {id} 未找到入口类型。");
                        loaded.LoadErrors.Add("未找到入口类型");
                        return false;
                    }

                    // 创建实例
                    var instance = (IPlugin)Activator.CreateInstance(entryType);

                    // 创建上下文
                    var context = new PluginContext(descriptor)
                    {
                        Manager = this,
                        LoadContext = loaded.LoadContext,
                        Logger = new PluginLogger(id),
                        Config = new PluginConfig(id, descriptor.FullConfigPath, descriptor.DefaultConfig),
                        Permission = new PluginPermission
                        {
                            PluginId = id,
                            Granted = descriptor.Info.RequiredPermissions
                        }
                    };

                    // 加载配置
                    context.Config.Load();
                    context.Config.StartWatching();

                    // 设置引用
                    var basePlugin = instance as PluginBase;
                    if (basePlugin != null)
                    {
                        basePlugin.Descriptor = descriptor;
                    }

                    // 调用 OnLoad
                    instance.OnLoad(context);

                    loaded.Instance = instance;
                    loaded.Context = context;
                    if (descriptor.IsCollectible)
                        loaded.UnloadReference = new WeakReference(loaded.LoadContext);

                    _plugins[id] = loaded;

                    // 激活
                    instance.OnActivate();

                    _logger.Info($"插件 {id} v{descriptor.Info.VersionStr} 加载成功。");
                    OnPluginLoaded?.Invoke(instance);

                    return true;
                }
                catch (Exception ex)
                {
                    _logger.Error($"加载插件 {id} 失败", ex);
                    loaded.LoadErrors.Add(ex.Message);
                    OnPluginError?.Invoke(id, ex);
                    return false;
                }
            }
        }

        /// <summary>卸载插件。</summary>
        public bool UnloadPlugin(string pluginId)
        {
            lock (_loadLock)
            {
                if (!_plugins.TryRemove(pluginId, out var loaded))
                {
                    _logger.Warning($"插件 {pluginId} 未加载。");
                    return false;
                }

                try
                {
                    // 检查是否有其他插件依赖此插件
                    foreach (var kv in _plugins)
                    {
                        if (kv.Value.Descriptor.Info.Dependencies.Any(d => d.PluginName == pluginId))
                        {
                            _logger.Warning($"插件 {pluginId} 被插件 {kv.Key} 依赖，无法卸载。");
                            _plugins[pluginId] = loaded;
                            return false;
                        }
                    }

                    // 停用
                    loaded.Instance?.OnDeactivate();
                    OnPluginDeactivated?.Invoke(loaded.Instance);

                    // 卸载
                    loaded.Instance?.OnUnload();
                    OnPluginUnloaded?.Invoke(loaded.Instance);

                    // 停止配置监视
                    loaded.Context?.Config?.StopWatching();
                    loaded.Context?.Logger?.Dispose();

                    // 卸载 AssemblyLoadContext
                    if (loaded.LoadContext?.IsCollectible == true)
                    {
                        loaded.LoadContext.TryUnload();
                        loaded.UnloadReference = new WeakReference(loaded.LoadContext);
                    }

                    _logger.Info($"插件 {pluginId} 已卸载。");
                    return true;
                }
                catch (Exception ex)
                {
                    _logger.Error($"卸载插件 {pluginId} 失败", ex);
                    OnPluginError?.Invoke(pluginId, ex);
                    return false;
                }
            }
        }

        /// <summary>重新加载插件。</summary>
        public bool ReloadPlugin(string pluginId)
        {
            if (!_plugins.TryGetValue(pluginId, out var loaded))
            {
                _logger.Warning($"插件 {pluginId} 未加载，无法重载。");
                return false;
            }

            var desc = loaded.Descriptor;
            if (!UnloadPlugin(pluginId))
                return false;

            // 等待 GC 回收
            if (loaded.UnloadReference != null)
            {
                for (int i = 0; i < 10 && loaded.UnloadReference.IsAlive; i++)
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    Thread.Sleep(50);
                }
            }

            return LoadPlugin(desc);
        }

        #endregion

        #region 启用与禁用

        /// <summary>启用插件。</summary>
        public bool EnablePlugin(string pluginId)
        {
            if (_plugins.TryGetValue(pluginId, out var loaded))
            {
                if (loaded.State == PluginState.Active) return true;
                loaded.Instance?.OnActivate();
                OnPluginActivated?.Invoke(loaded.Instance);
                return true;
            }

            // 如果已发现但未加载
            if (_discovered.TryGetValue(pluginId, out var desc))
            {
                desc.IsEnabled = true;
                return LoadPlugin(desc);
            }

            return false;
        }

        /// <summary>禁用插件。</summary>
        public bool DisablePlugin(string pluginId)
        {
            if (_plugins.TryGetValue(pluginId, out var loaded))
            {
                loaded.Instance?.OnDeactivate();
                OnPluginDeactivated?.Invoke(loaded.Instance);
                return true;
            }
            return false;
        }

        #endregion

        #region 更新

        /// <summary>更新所有活跃插件。</summary>
        public void UpdateAll(float deltaTime)
        {
            foreach (var kv in _plugins)
            {
                try
                {
                    if (kv.Value.State == PluginState.Active)
                        kv.Value.Instance?.OnUpdate(deltaTime);
                }
                catch (Exception ex)
                {
                    _logger.Error($"插件 {kv.Key} 更新异常", ex);
                    OnPluginError?.Invoke(kv.Key, ex);
                }
            }
        }

        #endregion

        #region 查询

        /// <summary>获取已加载的插件。</summary>
        public IPlugin GetPlugin(string pluginId)
        {
            return _plugins.TryGetValue(pluginId, out var loaded) ? loaded.Instance : null;
        }

        /// <summary>获取所有已加载插件。</summary>
        public IEnumerable<IPlugin> GetLoadedPlugins() =>
            _plugins.Values.Select(l => l.Instance).Where(p => p != null);

        /// <summary>获取插件状态。</summary>
        public PluginState GetPluginState(string pluginId)
        {
            return _plugins.TryGetValue(pluginId, out var loaded) ? loaded.State : PluginState.Unloaded;
        }

        /// <summary>插件是否已加载。</summary>
        public bool IsLoaded(string pluginId) => _plugins.ContainsKey(pluginId);

        /// <summary>插件是否处于活跃状态。</summary>
        public bool IsActive(string pluginId) =>
            _plugins.TryGetValue(pluginId, out var loaded) && loaded.State == PluginState.Active;

        /// <summary>获取插件数量。</summary>
        public int LoadedCount => _plugins.Count;
        public int DiscoveredCount => _discovered.Count;

        #endregion

        #region 服务定位

        /// <summary>获取服务。</summary>
        public T GetService<T>() where T : class
        {
            return ServiceRegistry?.GetService<T>();
        }

        /// <summary>获取服务。</summary>
        public object GetService(Type serviceType)
        {
            return ServiceRegistry?.GetService(serviceType);
        }

        /// <summary>注册服务。</summary>
        public void RegisterService<TInterface, TImplementation>(TImplementation instance = null)
            where TImplementation : class, TInterface, new()
        {
            ServiceRegistry?.Register<TInterface, TImplementation>(instance);
        }

        #endregion

        #region 卸载所有

        /// <summary>卸载所有插件。</summary>
        public void UnloadAll()
        {
            // 逆序卸载
            var ids = _plugins.Keys.ToList();
            foreach (var id in Enumerable.Reverse(ids))
            {
                UnloadPlugin(id);
            }
        }

        #endregion
    }

    #endregion
}
