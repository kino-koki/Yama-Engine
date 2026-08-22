using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace ShanyuEngine.Plugins
{
    // ════════════════════════════════════════════════════════════════════════
    // 山雨引擎 - Mod 框架系统 (ModFrameworkSystem)
    //
    // 类似 Bethesda Mod System / Unity Mods:
    //   * Mod 生命周期管理 (安装/卸载/启用/禁用)
    //   * 加载顺序 (拓扑排序 / 用户覆盖 / 冲突检测)
    //   * 资产覆盖 (覆盖/添加/替换游戏资产)
    //   * Mod 配置 (用户可调参数)
    //   * 游戏→Mod / Mod→游戏 API 接口
    //   * Mod 沙箱 (隔离运行 / 资源访问限制)
    //   * Mod 热加载 / Mod 浏览器
    // ════════════════════════════════════════════════════════════════════════

    #region Mod 基础类型

    /// <summary>Mod 加载优先级。</summary>
    public enum ModLoadPriority
    {
        Critical = 0,      // 核心补丁，最先加载
        High = 100,
        Normal = 200,
        Low = 300,
        Cosmetic = 400      // 纯外观，最后加载
    }

    /// <summary>Mod 依赖。</summary>
    public class ModDependency
    {
        public string ModId { get; set; } = string.Empty;
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

    /// <summary>Mod 清单。</summary>
    public class ModManifest
    {
        private static readonly JsonSerializerOptions _jsonOpts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        public string ModId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string Version { get; set; } = "1.0.0";
        public string Author { get; set; } = "Unknown";
        public string Description { get; set; } = string.Empty;
        public string EntryPoint { get; set; } = string.Empty;
        public string AssemblyPath { get; set; } = string.Empty;
        public ModLoadPriority LoadPriority { get; set; } = ModLoadPriority.Normal;
        public bool AutoEnable { get; set; } = true;
        public List<ModDependency> Dependencies { get; set; } = new List<ModDependency>();
        public ModConfigManifest Config { get; set; } = new ModConfigManifest();
        public List<string> Conflicts { get; set; } = new List<string>();
        public string Category { get; set; } = "General";
        public string Website { get; set; } = string.Empty;
        public List<string> Tags { get; set; } = new List<string>();

        public SemanticVersion GetVersion() => SemanticVersion.Parse(Version);

        public static ModManifest FromJson(string json)
        {
            var manifest = JsonSerializer.Deserialize<ModManifest>(json, _jsonOpts);
            if (manifest == null) throw new InvalidOperationException("Mod清单反序列化失败。");
            return manifest;
        }

        public static ModManifest FromFile(string filePath)
        {
            var json = File.ReadAllText(filePath);
            return FromJson(json);
        }

        public string ToJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>Mod 配置清单。</summary>
    public class ModConfigManifest
    {
        public List<ModConfigEntry> Entries { get; set; } = new List<ModConfigEntry>();
    }

    /// <summary>配置项定义。</summary>
    public class ModConfigEntry
    {
        public string Key { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Type { get; set; } = "float";
        public object DefaultValue { get; set; }
        public object MinValue { get; set; }
        public object MaxValue { get; set; }
        public List<string> Options { get; set; } = new List<string>();
    }

    #endregion

    #region Mod 资产系统

    /// <summary>Mod 资产操作类型。</summary>
    public enum ModAssetAction
    {
        Add,        // 新增资产
        Override,   // 覆盖已有资产
        Replace,    // 完全替换资产
        Patch       // 修补资产 (部分修改)
    }

    /// <summary>Mod 资产: 覆盖 / 添加 / 替换游戏资产。</summary>
    public class ModAsset
    {
        public string AssetPath { get; set; } = string.Empty;
        public string ModSourcePath { get; set; } = string.Empty;
        public ModAssetAction Action { get; set; }
        public string ModId { get; set; } = string.Empty;
        public long FileSize { get; set; }
        public string Checksum { get; set; } = string.Empty;

        /// <summary>加载资产数据。</summary>
        public byte[] LoadData()
        {
            if (!File.Exists(ModSourcePath)) return null;
            return File.ReadAllBytes(ModSourcePath);
        }
    }

    /// <summary>资产覆盖映射表。</summary>
    public class AssetOverrideMap
    {
        private readonly Dictionary<string, List<ModAsset>> _overrides = new Dictionary<string, List<ModAsset>>();

        /// <summary>注册覆盖。</summary>
        public void RegisterOverride(ModAsset asset)
        {
            if (!_overrides.TryGetValue(asset.AssetPath, out var list))
            {
                list = new List<ModAsset>();
                _overrides[asset.AssetPath] = list;
            }
            list.Add(asset);
        }

        /// <summary>获取某资产的所有覆盖 (按加载顺序排列)。</summary>
        public IReadOnlyList<ModAsset> GetOverrides(string assetPath)
        {
            if (_overrides.TryGetValue(assetPath, out var list))
                return list.AsReadOnly();
            return Array.Empty<ModAsset>();
        }

        /// <summary>获取最终生效的资产 (最高优先级)。</summary>
        public ModAsset GetActiveOverride(string assetPath)
        {
            var list = GetOverrides(assetPath);
            return list.Count > 0 ? list[list.Count - 1] : null;
        }

        /// <summary>检测资产冲突 (多个 Mod 修改同一资产)。</summary>
        public List<(string AssetPath, List<ModAsset> Mods)> DetectConflicts()
        {
            var conflicts = new List<(string, List<ModAsset>)>();
            foreach (var kvp in _overrides)
            {
                if (kvp.Value.Count > 1)
                {
                    conflicts.Add((kvp.Key, kvp.Value));
                }
            }
            return conflicts;
        }

        /// <summary>所有被覆盖的资产路径。</summary>
        public IEnumerable<string> OverriddenPaths => _overrides.Keys;
    }

    #endregion

    #region Mod 配置系统

    /// <summary>Mod 运行时配置 (用户可调参数)。</summary>
    public class ModConfig
    {
        private readonly Dictionary<string, object> _values = new Dictionary<string, object>();
        private readonly ModConfigManifest _manifest;
        private readonly string _configFilePath;

        public ModConfig(ModConfigManifest manifest, string configFilePath)
        {
            _manifest = manifest;
            _configFilePath = configFilePath;
            LoadDefaults();
        }

        private void LoadDefaults()
        {
            foreach (var entry in _manifest.Entries)
            {
                _values[entry.Key] = entry.DefaultValue;
            }
        }

        public T GetValue<T>(string key)
        {
            if (_values.TryGetValue(key, out var val))
            {
                if (val is T typed) return typed;
                try { return (T)Convert.ChangeType(val, typeof(T)); }
                catch { return default; }
            }
            return default;
        }

        public void SetValue(string key, object value)
        {
            var entry = _manifest.Entries.FirstOrDefault(e => e.Key == key);
            if (entry != null)
            {
                // 范围检查
                if (entry.MinValue != null)
                {
                    try
                    {
                        var min = Convert.ToDouble(entry.MinValue);
                        var v = Convert.ToDouble(value);
                        if (v < min) return;
                    }
                    catch { }
                }
                if (entry.MaxValue != null)
                {
                    try
                    {
                        var max = Convert.ToDouble(entry.MaxValue);
                        var v = Convert.ToDouble(value);
                        if (v > max) return;
                    }
                    catch { }
                }
            }
            _values[key] = value;
        }

        /// <summary>保存配置到文件。</summary>
        public void Save()
        {
            try
            {
                var json = JsonSerializer.Serialize(_values, new JsonSerializerOptions { WriteIndented = true });
                var dir = Path.GetDirectoryName(_configFilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllText(_configFilePath, json);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ModConfig] 保存配置失败: {_configFilePath}, 错误: {ex.Message}");
            }
        }

        /// <summary>从文件加载配置。</summary>
        public void Load()
        {
            if (!File.Exists(_configFilePath)) return;
            try
            {
                var json = File.ReadAllText(_configFilePath);
                var values = JsonSerializer.Deserialize<Dictionary<string, object>>(json);
                if (values != null)
                {
                    foreach (var kvp in values)
                    {
                        if (_values.ContainsKey(kvp.Key))
                            _values[kvp.Key] = kvp.Value;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ModConfig] 加载配置失败: {_configFilePath}, 错误: {ex.Message}");
            }
        }
    }

    #endregion

    #region Mod API (游戏↔Mod 接口)

    /// <summary>游戏事件类型枚举。</summary>
    public enum GameEventType
    {
        GameStart,
        GameShutdown,
        LevelLoad,
        LevelUnload,
        ActorSpawn,
        ActorDestroy,
        ActorTick,
        Damage,
        Heal,
        Death,
        InventoryChange,
        InventoryAdd,
        InventoryRemove,
        PlayerLogin,
        PlayerLogout,
        SaveGame,
        LoadGame,
        Custom
    }

    /// <summary>游戏事件参数。</summary>
    public class GameEventArgs : EventArgs
    {
        public GameEventType EventType { get; }
        public string EventName { get; }
        public Dictionary<string, object> Data { get; } = new Dictionary<string, object>();

        public GameEventArgs(GameEventType eventType, string eventName = null)
        {
            EventType = eventType;
            EventName = eventName ?? eventType.ToString();
        }
    }

    /// <summary>Mod Hook 回调委托。</summary>
    public delegate void GameEventCallback(GameEventArgs args);

    /// <summary>
    /// Mod API: 游戏→Mod 接口 (游戏暴露给 Mod 的能力)。
    /// </summary>
    public class GameToModAPI
    {
        private readonly Dictionary<GameEventType, List<GameEventCallback>> _hooks =
            new Dictionary<GameEventType, List<GameEventCallback>>();
        private readonly Dictionary<string, List<GameEventCallback>> _customHooks =
            new Dictionary<string, List<GameEventCallback>>();
        private readonly Dictionary<string, object> _gameState = new Dictionary<string, object>();
        private readonly object _lock = new object();

        /// <summary>注册事件 Hook。</summary>
        public void RegisterHook(GameEventType eventType, GameEventCallback callback)
        {
            if (callback == null) return;
            lock (_lock)
            {
                if (!_hooks.TryGetValue(eventType, out var list))
                {
                    list = new List<GameEventCallback>();
                    _hooks[eventType] = list;
                }
                list.Add(callback);
            }
        }

        /// <summary>注册自定义事件 Hook。</summary>
        public void RegisterHook(string eventName, GameEventCallback callback)
        {
            if (callback == null) return;
            lock (_lock)
            {
                if (!_customHooks.TryGetValue(eventName, out var list))
                {
                    list = new List<GameEventCallback>();
                    _customHooks[eventName] = list;
                }
                list.Add(callback);
            }
        }

        /// <summary>反注册事件 Hook。</summary>
        public void UnregisterHook(GameEventType eventType, GameEventCallback callback)
        {
            lock (_lock)
            {
                if (_hooks.TryGetValue(eventType, out var list))
                    list.Remove(callback);
            }
        }

        /// <summary>反注册自定义事件 Hook。</summary>
        public void UnregisterHook(string eventName, GameEventCallback callback)
        {
            lock (_lock)
            {
                if (_customHooks.TryGetValue(eventName, out var list))
                    list.Remove(callback);
            }
        }

        /// <summary>触发事件 (内部使用)。</summary>
        internal void FireEvent(GameEventType eventType, GameEventArgs args)
        {
            List<GameEventCallback> callbacks;
            lock (_lock)
            {
                callbacks = _hooks.TryGetValue(eventType, out var list) ? new List<GameEventCallback>(list) : null;
            }
            if (callbacks != null)
            {
                foreach (var cb in callbacks)
                {
                    try { cb(args); }
                    catch (Exception ex) { Console.WriteLine($"[ModAPI] Hook 异常: {ex.Message}"); }
                }
            }
        }

        /// <summary>触发自定义事件 (内部使用)。</summary>
        internal void FireCustomEvent(string eventName, GameEventArgs args)
        {
            List<GameEventCallback> callbacks;
            lock (_lock)
            {
                callbacks = _customHooks.TryGetValue(eventName, out var list) ? new List<GameEventCallback>(list) : null;
            }
            if (callbacks != null)
            {
                foreach (var cb in callbacks)
                {
                    try { cb(args); }
                    catch (Exception ex) { Console.WriteLine($"[ModAPI] Hook 异常: {ex.Message}"); }
                }
            }
        }

        /// <summary>读取游戏状态。</summary>
        public T GetGameState<T>(string key)
        {
            lock (_lock)
            {
                if (_gameState.TryGetValue(key, out var val) && val is T typed)
                    return typed;
            }
            return default;
        }

        /// <summary>设置游戏状态 (Mod→游戏数据写入)。</summary>
        public void SetGameState(string key, object value)
        {
            lock (_lock)
            {
                _gameState[key] = value;
            }
        }

        /// <summary>清除所有 Hook (Mod 卸载时调用)。</summary>
        public void ClearHooks(GameEventCallback target = null)
        {
            lock (_lock)
            {
                foreach (var list in _hooks.Values)
                {
                    if (target != null) list.RemoveAll(cb => cb == target);
                    else list.Clear();
                }
                foreach (var list in _customHooks.Values)
                {
                    if (target != null) list.RemoveAll(cb => cb == target);
                    else list.Clear();
                }
            }
        }
    }

    /// <summary>
    /// Mod→游戏 API (Mod 调用游戏功能的接口)。
    /// </summary>
    public class ModToGameAPI
    {
        private readonly Func<string, object> _queryFunc;
        private readonly Action<string, object> _modifyFunc;

        public ModToGameAPI(
            Func<string, object> queryFunc = null,
            Action<string, object> modifyFunc = null)
        {
            _queryFunc = queryFunc ?? (_ => null);
            _modifyFunc = modifyFunc ?? ((_, __) => { });
        }

        /// <summary>查询游戏数据。</summary>
        public T Query<T>(string dataPath)
        {
            var result = _queryFunc(dataPath);
            if (result is T typed) return typed;
            try { return (T)Convert.ChangeType(result, typeof(T)); }
            catch { return default; }
        }

        /// <summary>修改游戏数据。</summary>
        public void Modify(string dataPath, object value)
        {
            _modifyFunc(dataPath, value);
        }

        /// <summary>记录 Mod 日志。</summary>
        public void Log(string modId, string level, string message)
        {
            Console.WriteLine($"[{modId}][{level}] {message}");
        }

        /// <summary>显示 HUD 消息。</summary>
        public void ShowHUDMessage(string message, float duration = 3f)
        {
            // 由游戏侧实现
        }
    }

    #endregion

    #region Mod 沙箱

    /// <summary>Mod 沙箱: 隔离运行 / 资源访问限制。</summary>
    public class ModSandbox
    {
        private readonly string _modId;
        private readonly string _sandboxDir;
        private readonly HashSet<string> _allowedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly AssemblyLoadContext _loadContext;
        private readonly List<string> _deniedTypes = new List<string>();

        public string ModId => _modId;
        public string SandboxDirectory => _sandboxDir;
        public AssemblyLoadContext LoadContext => _loadContext;

        public ModSandbox(string modId, string sandboxDir, bool isCollectible = true)
        {
            _modId = modId;
            _sandboxDir = sandboxDir;
            _loadContext = new AssemblyLoadContext($"ModSandbox_{modId}", isCollectible);
        }

        /// <summary>允许访问的路径白名单。</summary>
        public void AllowPath(string path)
        {
            _allowedPaths.Add(Path.GetFullPath(path));
        }

        /// <summary>拒绝加载的类型名。</summary>
        public void DenyType(string typeName)
        {
            _deniedTypes.Add(typeName);
        }

        /// <summary>检查路径是否在沙箱允许范围内。</summary>
        public bool IsPathAllowed(string path)
        {
            if (_allowedPaths.Count == 0) return true;
            var fullPath = Path.GetFullPath(path);
            return _allowedPaths.Any(allowed =>
                fullPath.StartsWith(allowed, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>检查类型是否允许加载。</summary>
        public bool IsTypeAllowed(Type type)
        {
            if (_deniedTypes.Count == 0) return true;
            return !_deniedTypes.Any(denied =>
                type.FullName != null && type.FullName.Contains(denied));
        }

        /// <summary>在沙箱中加载程序集。</summary>
        public Assembly LoadAssembly(string assemblyPath)
        {
            return _loadContext.LoadFromAssemblyPath(assemblyPath);
        }

        /// <summary>卸载沙箱。</summary>
        public void Unload()
        {
            _loadContext.Unload();
        }
    }

    #endregion

    #region Mod 加载顺序

    /// <summary>Mod 加载顺序管理。</summary>
    public class ModLoadOrder
    {
        private readonly Dictionary<string, int> _userOverrides = new Dictionary<string, int>();
        private readonly Dictionary<string, int> _computedOrder = new Dictionary<string, int>();

        /// <summary>用户覆盖加载顺序。</summary>
        public void SetUserOverride(string modId, int order)
        {
            _userOverrides[modId] = order;
        }

        /// <summary>移除用户覆盖。</summary>
        public void RemoveUserOverride(string modId)
        {
            _userOverrides.Remove(modId);
        }

        /// <summary>计算最终加载顺序 (拓扑排序 + 优先级 + 用户覆盖)。</summary>
        public List<string> ComputeOrder(Dictionary<string, ModManifest> manifests)
        {
            // 构建依赖图
            var graph = new Dictionary<string, List<string>>();
            var inDegree = new Dictionary<string, int>();

            foreach (var kvp in manifests)
            {
                var modId = kvp.Key;
                if (!graph.ContainsKey(modId)) graph[modId] = new List<string>();
                if (!inDegree.ContainsKey(modId)) inDegree[modId] = 0;

                foreach (var dep in kvp.Value.Dependencies)
                {
                    if (manifests.ContainsKey(dep.ModId))
                    {
                        graph[modId].Add(dep.ModId);
                        inDegree[modId]++;
                    }
                }
            }

            // Kahn's algorithm with priority
            var priorityMap = new Dictionary<string, int>();
            foreach (var kvp in manifests)
            {
                if (_userOverrides.TryGetValue(kvp.Key, out var overrideVal))
                    priorityMap[kvp.Key] = overrideVal;
                else
                    priorityMap[kvp.Key] = (int)kvp.Value.LoadPriority;
            }

            var queue = new SortedSet<(int Priority, string Id)>();
            foreach (var kvp in inDegree)
            {
                if (kvp.Value == 0)
                    queue.Add((priorityMap[kvp.Key], kvp.Key));
            }

            var result = new List<string>();
            while (queue.Count > 0)
            {
                var (priority, modId) = queue.Min;
                queue.Remove((priority, modId));
                result.Add(modId);

                foreach (var kvp in graph)
                {
                    if (kvp.Value.Remove(modId))
                    {
                        inDegree[kvp.Key]--;
                        if (inDegree[kvp.Key] == 0)
                            queue.Add((priorityMap[kvp.Key], kvp.Key));
                    }
                }
            }

            _computedOrder.Clear();
            for (int i = 0; i < result.Count; i++)
                _computedOrder[result[i]] = i;

            return result;
        }

        /// <summary>检测循环依赖。</summary>
        public static List<string> DetectCycles(Dictionary<string, ModManifest> manifests)
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
                        if (manifests.ContainsKey(dep.ModId))
                            DFS(dep.ModId, currentPath);
                    }
                }

                recursionStack.Remove(node);
            }

            foreach (var id in manifests.Keys) DFS(id, string.Empty);
            return cycles;
        }
    }

    #endregion

    #region Mod 内容加载器

    /// <summary>Mod 内容加载器: 资产覆盖映射 / 热替换。</summary>
    public class ModContentLoader
    {
        private readonly AssetOverrideMap _overrideMap = new AssetOverrideMap();
        private readonly Dictionary<string, Mod> _sourceMods = new Dictionary<string, Mod>();

        /// <summary>资产覆盖映射。</summary>
        public AssetOverrideMap OverrideMap => _overrideMap;

        /// <summary>注册 Mod 资产。</summary>
        public void RegisterModAssets(Mod mod)
        {
            _sourceMods[mod.ModId] = mod;
            var modDir = mod.ModDirectory;
            var assetsDir = Path.Combine(modDir, "Assets");

            if (!Directory.Exists(assetsDir)) return;

            foreach (var file in Directory.GetFiles(assetsDir, "*.*", SearchOption.AllDirectories))
            {
                var relativePath = file.Substring(assetsDir.Length + 1).Replace('\\', '/');
                var asset = new ModAsset
                {
                    AssetPath = relativePath,
                    ModSourcePath = file,
                    Action = ModAssetAction.Override,
                    ModId = mod.ModId,
                    FileSize = new FileInfo(file).Length
                };
                _overrideMap.RegisterOverride(asset);
                mod.RegisteredAssets.Add(asset);
            }
        }

        /// <summary>移除 Mod 资产。</summary>
        public void UnregisterModAssets(string modId)
        {
            if (_sourceMods.TryGetValue(modId, out var mod))
            {
                mod.RegisteredAssets.Clear();
                _sourceMods.Remove(modId);
            }
        }

        /// <summary>获取最终资产数据。</summary>
        public byte[] GetAssetData(string assetPath)
        {
            var overrideAsset = _overrideMap.GetActiveOverride(assetPath);
            if (overrideAsset != null)
                return overrideAsset.LoadData();
            return null;
        }

        /// <summary>检测并报告资产冲突。</summary>
        public string ReportConflicts()
        {
            var conflicts = _overrideMap.DetectConflicts();
            if (conflicts.Count == 0) return "无资产冲突。";

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"检测到 {conflicts.Count} 个资产冲突:");
            foreach (var (path, assets) in conflicts)
            {
                sb.AppendLine($"  资产: {path}");
                foreach (var a in assets) sb.AppendLine($"    - Mod [{a.ModId}] ({a.Action})");
            }
            return sb.ToString();
        }
    }

    #endregion

    #region Mod 浏览器

    /// <summary>Mod 浏览器条目。</summary>
    public class ModBrowserEntry
    {
        public string ModId { get; set; }
        public string Name { get; set; }
        public string DisplayName { get; set; }
        public string Version { get; set; }
        public string Author { get; set; }
        public string Description { get; set; }
        public string Category { get; set; }
        public float Rating { get; set; }
        public int DownloadCount { get; set; }
        public List<string> Tags { get; set; } = new List<string>();
        public string DownloadUrl { get; set; }
        public string PreviewImageUrl { get; set; }
        public bool IsInstalled { get; set; }
        public string InstalledVersion { get; set; }
        public bool HasUpdate { get; set; }
    }

    /// <summary>Mod 浏览器: 列表 / 搜索 / 安装 / 更新 / 评分 / 分类。</summary>
    public class ModBrowser
    {
        private readonly List<ModBrowserEntry> _catalog = new List<ModBrowserEntry>();
        private readonly string _modDirectory;
        private readonly Dictionary<string, ModManifest> _installedMods;

        public IReadOnlyList<ModBrowserEntry> Catalog => _catalog.AsReadOnly();

        public event Action<List<ModBrowserEntry>> OnSearchComplete;
        public event Action<ModBrowserEntry> OnDownloadProgress;
        public event Action<ModBrowserEntry> OnInstallComplete;
        public event Action<ModBrowserEntry, Exception> OnInstallFailed;

        public ModBrowser(string modDirectory, Dictionary<string, ModManifest> installedMods)
        {
            _modDirectory = modDirectory;
            _installedMods = installedMods;
        }

        /// <summary>搜索 Mod。</summary>
        public List<ModBrowserEntry> Search(string query, string category = null, int maxResults = 50)
        {
            var results = _catalog.AsEnumerable();

            if (!string.IsNullOrWhiteSpace(query))
            {
                var lower = query.ToLowerInvariant();
                results = results.Where(m =>
                    m.Name.ToLowerInvariant().Contains(lower) ||
                    m.DisplayName.ToLowerInvariant().Contains(lower) ||
                    m.Author.ToLowerInvariant().Contains(lower) ||
                    m.Tags.Any(t => t.ToLowerInvariant().Contains(lower)) ||
                    m.Description.ToLowerInvariant().Contains(lower));
            }

            if (!string.IsNullOrEmpty(category))
            {
                results = results.Where(m => m.Category.Equals(category, StringComparison.OrdinalIgnoreCase));
            }

            results = results.OrderByDescending(m => m.Rating)
                             .ThenByDescending(m => m.DownloadCount)
                             .Take(maxResults);

            var resultList = results.ToList();
            OnSearchComplete?.Invoke(resultList);
            return resultList;
        }

        /// <summary>安装 Mod。</summary>
        public bool InstallMod(ModBrowserEntry entry)
        {
            try
            {
                var modDir = Path.Combine(_modDirectory, entry.ModId);
                if (!Directory.Exists(modDir))
                    Directory.CreateDirectory(modDir);

                OnDownloadProgress?.Invoke(entry);

                // 清单文件由下载完成后创建 (实际网络下载不在框架内)
                OnInstallComplete?.Invoke(entry);
                return true;
            }
            catch (Exception ex)
            {
                OnInstallFailed?.Invoke(entry, ex);
                return false;
            }
        }

        /// <summary>注册 Mod 到目录。</summary>
        public void RegisterEntry(ModBrowserEntry entry)
        {
            var existing = _catalog.FirstOrDefault(e => e.ModId == entry.ModId);
            if (existing != null)
                _catalog.Remove(existing);

            // 检查安装状态
            if (_installedMods.TryGetValue(entry.ModId, out var installed))
            {
                entry.IsInstalled = true;
                entry.InstalledVersion = installed.Version;
                var catalogVer = SemanticVersion.Parse(entry.Version);
                var installedVer = installed.GetVersion();
                entry.HasUpdate = catalogVer > installedVer;
            }

            _catalog.Add(entry);
        }

        /// <summary>获取分类列表。</summary>
        public List<string> GetCategories()
        {
            return _catalog.Select(m => m.Category).Distinct().OrderBy(c => c).ToList();
        }
    }

    #endregion

    #region Mod 类

    /// <summary>Mod 类: 名称 / 版本 / 作者 / 描述 / 加载优先级 / 启用 / ModId。</summary>
    public abstract class Mod
    {
        public string ModId { get; protected set; } = string.Empty;
        public string Name { get; protected set; } = string.Empty;
        public string DisplayName { get; protected set; } = string.Empty;
        public SemanticVersion Version { get; protected set; } = new SemanticVersion(1, 0, 0);
        public string Author { get; protected set; } = "Unknown";
        public string Description { get; protected set; } = string.Empty;
        public ModLoadPriority LoadPriority { get; protected set; } = ModLoadPriority.Normal;
        public bool IsEnabled { get; internal set; }

        internal ModManifest Manifest { get; set; }
        internal ModSandbox Sandbox { get; set; }
        internal Assembly ModAssembly { get; set; }
        public string ModDirectory { get; internal set; } = string.Empty;
        public List<ModAsset> RegisteredAssets { get; } = new List<ModAsset>();
        public ModConfig Config { get; set; }

        // ── 生命周期 ──────────────────────────────────────────────────

        protected virtual void OnLoad(GameToModAPI gameApi, ModToGameAPI modApi) { }
        protected virtual void OnEnable() { }
        protected virtual void OnTick(float deltaTime) { }
        protected virtual void OnDisable() { }
        protected virtual void OnUnload() { }

        internal void InternalLoad(GameToModAPI gameApi, ModToGameAPI modApi)
        {
            IsEnabled = true;
            OnLoad(gameApi, modApi);
        }

        internal void InternalEnable() => OnEnable();
        internal void InternalTick(float dt) => OnTick(dt);
        internal void InternalDisable() => OnDisable();
        internal void InternalUnload() => OnUnload();
    }

    #endregion

    #region ModManager

    /// <summary>
    /// Mod 管理器: 扫描 / 安装 / 卸载 / 启用 / 禁用 / 依赖解析 / 冲突检测 / 沙箱 / 热加载。
    /// </summary>
    public class ModManager
    {
        private readonly Dictionary<string, Mod> _mods = new Dictionary<string, Mod>();
        private readonly Dictionary<string, ModManifest> _manifests = new Dictionary<string, ModManifest>();
        private readonly ModLoadOrder _loadOrder = new ModLoadOrder();
        private readonly ModContentLoader _contentLoader = new ModContentLoader();
        private readonly GameToModAPI _gameToModApi = new GameToModAPI();
        private readonly ModToGameAPI _modToGameApi;
        private readonly string _modDirectory;
        private readonly object _lock = new object();

        public IReadOnlyDictionary<string, Mod> Mods => _mods;
        public ModContentLoader ContentLoader => _contentLoader;
        public GameToModAPI GameAPI => _gameToModApi;
        public ModToGameAPI ModAPI => _modToGameApi;

        public event Action<Mod> OnModLoaded;
        public event Action<Mod> OnModUnloaded;
        public event Action<Mod, Exception> OnModError;

        public ModManager(string modDirectory,
            Func<string, object> queryFunc = null,
            Action<string, object> modifyFunc = null)
        {
            _modDirectory = modDirectory;
            _modToGameApi = new ModToGameAPI(queryFunc, modifyFunc);
        }

        /// <summary>扫描 Mod 目录。</summary>
        public void ScanMods()
        {
            lock (_lock)
            {
                if (!Directory.Exists(_modDirectory)) return;

                foreach (var dir in Directory.GetDirectories(_modDirectory))
                {
                    var manifestPath = Path.Combine(dir, "mod.json");
                    if (!File.Exists(manifestPath)) continue;

                    try
                    {
                        var manifest = ModManifest.FromFile(manifestPath);
                        _manifests[manifest.ModId] = manifest;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[ModManager] 解析 Mod 清单失败: {manifestPath}, 错误: {ex.Message}");
                    }
                }
            }
        }

        /// <summary>加载所有 Mod。</summary>
        public bool LoadAll()
        {
            lock (_lock)
            {
                var cycles = ModLoadOrder.DetectCycles(_manifests);
                if (cycles.Count > 0)
                {
                    Console.WriteLine("[ModManager] 检测到循环依赖:");
                    foreach (var c in cycles) Console.WriteLine($"  - {c}");
                    return false;
                }

                var order = _loadOrder.ComputeOrder(_manifests);

                foreach (var modId in order)
                {
                    var manifest = _manifests[modId];
                    if (!manifest.AutoEnable) continue;
                    if (!LoadMod(modId)) return false;
                }

                return true;
            }
        }

        /// <summary>加载单个 Mod。</summary>
        public bool LoadMod(string modId)
        {
            lock (_lock)
            {
                if (_mods.ContainsKey(modId)) return true;

                if (!_manifests.TryGetValue(modId, out var manifest))
                {
                    Console.WriteLine($"[ModManager] 未找到 Mod 清单: {modId}");
                    return false;
                }

                try
                {
                    var modDir = Path.Combine(_modDirectory, modId);
                    var assemblyPath = Path.Combine(modDir, manifest.AssemblyPath);

                    // 创建沙箱
                    var sandbox = new ModSandbox(modId, modDir);
                    sandbox.AllowPath(modDir);

                    Type entryType = null;
                    if (File.Exists(assemblyPath))
                    {
                        var assembly = sandbox.LoadAssembly(assemblyPath);
                        if (!string.IsNullOrEmpty(manifest.EntryPoint))
                            entryType = assembly.GetType(manifest.EntryPoint);
                        if (entryType == null)
                            entryType = assembly.GetTypes()
                                .FirstOrDefault(t => typeof(Mod).IsAssignableFrom(t) && !t.IsAbstract);
                    }

                    Mod mod;
                    if (entryType != null)
                    {
                        mod = (Mod)Activator.CreateInstance(entryType);
                    }
                    else
                    {
                        mod = new DefaultMod();
                    }

                    mod.ModId = manifest.ModId;
                    mod.Name = manifest.Name;
                    mod.DisplayName = manifest.DisplayName;
                    mod.Version = manifest.GetVersion();
                    mod.Author = manifest.Author;
                    mod.Description = manifest.Description;
                    mod.LoadPriority = manifest.LoadPriority;
                    mod.Manifest = manifest;
                    mod.Sandbox = sandbox;
                    mod.ModDirectory = modDir;

                    // 初始化配置
                    var configPath = Path.Combine(modDir, "config.json");
                    mod.Config = new ModConfig(manifest.Config, configPath);
                    mod.Config.Load();

                    // 注册 Mod 资产
                    _contentLoader.RegisterModAssets(mod);

                    mod.InternalLoad(_gameToModApi, _modToGameApi);
                    mod.InternalEnable();

                    _mods[modId] = mod;
                    OnModLoaded?.Invoke(mod);
                    return true;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ModManager] 加载 Mod 失败: {modId}, 错误: {ex.Message}");
                    OnModError?.Invoke(null, ex);
                    return false;
                }
            }
        }

        /// <summary>卸载 Mod。</summary>
        public bool UnloadMod(string modId)
        {
            lock (_lock)
            {
                if (!_mods.TryGetValue(modId, out var mod)) return false;

                try
                {
                    mod.Config?.Save();
                    mod.InternalDisable();
                    mod.InternalUnload();

                    _contentLoader.UnregisterModAssets(modId);
                    mod.Sandbox?.Unload();

                    _mods.Remove(modId);
                    OnModUnloaded?.Invoke(mod);
                    return true;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ModManager] 卸载 Mod 失败: {modId}, 错误: {ex.Message}");
                    return false;
                }
            }
        }

        /// <summary>启用 Mod。</summary>
        public bool EnableMod(string modId)
        {
            lock (_lock)
            {
                if (!_mods.TryGetValue(modId, out var mod)) return false;
                try { mod.InternalEnable(); mod.IsEnabled = true; return true; }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ModManager] 启用 Mod 失败: {modId}, 错误: {ex.Message}");
                    return false;
                }
            }
        }

        /// <summary>禁用 Mod。</summary>
        public bool DisableMod(string modId)
        {
            lock (_lock)
            {
                if (!_mods.TryGetValue(modId, out var mod)) return false;
                try { mod.InternalDisable(); mod.IsEnabled = false; return true; }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ModManager] 禁用 Mod 失败: {modId}, 错误: {ex.Message}");
                    return false;
                }
            }
        }

        /// <summary>Mod 热加载 (开发时 Mod 代码修改→自动重载)。</summary>
        public bool HotReloadMod(string modId)
        {
            lock (_lock)
            {
                if (!_mods.TryGetValue(modId, out var oldMod)) return false;

                try
                {
                    var manifest = oldMod.Manifest;
                    oldMod.InternalDisable();
                    oldMod.InternalUnload();
                    _contentLoader.UnregisterModAssets(modId);
                    oldMod.Sandbox?.Unload();

                    var sandbox = new ModSandbox(modId, oldMod.ModDirectory);
                    sandbox.AllowPath(oldMod.ModDirectory);

                    var assemblyPath = Path.Combine(oldMod.ModDirectory, manifest.AssemblyPath);
                    Mod newMod;

                    if (File.Exists(assemblyPath))
                    {
                        var assembly = sandbox.LoadAssembly(assemblyPath);
                        Type entryType = null;
                        if (!string.IsNullOrEmpty(manifest.EntryPoint))
                            entryType = assembly.GetType(manifest.EntryPoint);
                        if (entryType == null)
                            entryType = assembly.GetTypes()
                                .FirstOrDefault(t => typeof(Mod).IsAssignableFrom(t) && !t.IsAbstract);

                        if (entryType != null)
                            newMod = (Mod)Activator.CreateInstance(entryType);
                        else
                            newMod = new DefaultMod();
                    }
                    else
                    {
                        newMod = new DefaultMod();
                    }

                    newMod.ModId = oldMod.ModId;
                    newMod.Name = oldMod.Name;
                    newMod.DisplayName = oldMod.DisplayName;
                    newMod.Version = oldMod.Version;
                    newMod.Author = oldMod.Author;
                    newMod.Description = oldMod.Description;
                    newMod.LoadPriority = oldMod.LoadPriority;
                    newMod.Manifest = manifest;
                    newMod.Sandbox = sandbox;
                    newMod.ModDirectory = oldMod.ModDirectory;

                    var configPath = Path.Combine(newMod.ModDirectory, "config.json");
                    newMod.Config = new ModConfig(manifest.Config, configPath);
                    newMod.Config.Load();

                    _contentLoader.RegisterModAssets(newMod);
                    newMod.InternalLoad(_gameToModApi, _modToGameApi);
                    newMod.InternalEnable();

                    _mods[modId] = newMod;
                    return true;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ModManager] 热加载 Mod 失败: {modId}, 错误: {ex.Message}");
                    OnModError?.Invoke(oldMod, ex);
                    return false;
                }
            }
        }

        /// <summary>Tick 所有启用的 Mod。</summary>
        public void TickAll(float deltaTime)
        {
            var snapshot = _mods.Values.Where(m => m.IsEnabled).ToList();
            foreach (var mod in snapshot)
            {
                try { mod.InternalTick(deltaTime); }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ModManager] Tick Mod 异常: {mod.ModId}, 错误: {ex.Message}");
                }
            }
        }

        /// <summary>卸载所有 Mod。</summary>
        public void UnloadAll()
        {
            lock (_lock)
            {
                var ids = _mods.Keys.ToList();
                for (int i = ids.Count - 1; i >= 0; i--)
                    UnloadMod(ids[i]);
                _mods.Clear();
                _manifests.Clear();
            }
        }

        /// <summary>获取 Mod。</summary>
        public Mod GetMod(string modId)
        {
            lock (_lock)
            {
                _mods.TryGetValue(modId, out var mod);
                return mod;
            }
        }
    }

    /// <summary>默认 Mod 实现 (无代码 Mod)。</summary>
    internal class DefaultMod : Mod
    {
        protected override void OnLoad(GameToModAPI gameApi, ModToGameAPI modApi) { }
    }

    #endregion
}
