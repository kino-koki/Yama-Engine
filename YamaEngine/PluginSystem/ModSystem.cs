using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace YamaEngine.PluginSystem
{
    // ════════════════════════════════════════════════════════════════════════
    // yama引擎 - Mod 系统 (ModSystem)
    //
    // 玩家创建的内容管理系统 (类似 Bethesda Mod System / Steam Workshop):
    //   * Mod 元数据与生命周期管理
    //   * Mod 发现 (Mod 目录扫描 / .mod 清单 / Steam Workshop / 压缩包)
    //   * Mod 安装 (从压缩包安装 / 验证 / 依赖检查 / 冲突检测)
    //   * Mod 内容加载 (资产覆盖 / 脚本加载 / 配置合并 / 本地化合并)
    //   * Mod 加载顺序 (依赖排序 / 用户排序 / 自动排序 / 冲突解决)
    //   * Mod 兼容性 (版本范围 / 引擎版本 / 游戏版本 / API版本)
    //   * Mod 打包 (导出 .modpak / 签名 / 加密 / 分块)
    //   * Mod 发布 (上传创意工坊 / 本地分享 / 版本管理)
    //   * Mod 冲突解决 (资产冲突 / 脚本冲突 / 配置冲突 / 优先级)
    // ════════════════════════════════════════════════════════════════════════

    #region Mod 枚举

    /// <summary>Mod 状态。</summary>
    public enum ModState
    {
        Inactive,
        Loading,
        Active,
        Error,
        Disabled,
        Unloading
    }

    /// <summary>Mod 来源类型。</summary>
    public enum ModSource
    {
        Local,
        SteamWorkshop,
        GOG,
        CompressedPackage,
        ManualInstall
    }

    /// <summary>Mod 内容类型。</summary>
    [Flags]
    public enum ModContentType : uint
    {
        None = 0,
        Assets = 1 << 0,
        Scripts = 1 << 1,
        Config = 1 << 2,
        Localization = 1 << 3,
        Textures = 1 << 4,
        Meshes = 1 << 5,
        Audio = 1 << 6,
        Animations = 1 << 7,
        Shaders = 1 << 8,
        UI = 1 << 9,
        Maps = 1 << 10,
        All = ~0u
    }

    /// <summary>Mod 冲突类型。</summary>
    public enum ModConflictType
    {
        AssetConflict,
        ScriptConflict,
        ConfigConflict,
        DependencyConflict,
        VersionConflict
    }

    #endregion

    #region Mod 依赖

    /// <summary>Mod 依赖描述。</summary>
    public sealed class ModDependency
    {
        public string ModId { get; set; } = string.Empty;
        public string VersionRangeStr { get; set; } = "*";
        public bool IsRequired { get; set; } = true;
        public string DisplayName { get; set; } = string.Empty;

        [JsonIgnore]
        public VersionRange VersionRange => VersionRange.Parse(VersionRangeStr);

        public bool MatchesVersion(SemanticVersion version) => VersionRange.Matches(version);

        public override string ToString() =>
            $"{ModId} {VersionRangeStr}{(IsRequired ? "" : " (optional)")}";
    }

    #endregion

    #region Mod 元数据

    /// <summary>Mod 元数据。</summary>
    public sealed class ModInfo
    {
        public string ModId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string VersionStr { get; set; } = "1.0.0";
        public string Author { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string IconPath { get; set; } = string.Empty;
        public string Homepage { get; set; } = string.Empty;
        public List<ModDependency> Dependencies { get; set; } = new List<ModDependency>();
        public List<string> Incompatibilities { get; set; } = new List<string>();
        public string EngineCompatVersion { get; set; } = "*";
        public string GameCompatVersion { get; set; } = "*";
        public string ApiCompatVersion { get; set; } = "*";
        public List<string> Tags { get; set; } = new List<string>();
        public string Category { get; set; } = string.Empty;
        public ModContentType ContentTypes { get; set; } = ModContentType.All;
        public long WorkshopItemId { get; set; } = 0;
        public string License { get; set; } = string.Empty;
        public DateTime CreatedDate { get; set; } = DateTime.Now;
        public DateTime UpdatedDate { get; set; } = DateTime.Now;

        [JsonIgnore]
        public SemanticVersion Version => SemanticVersion.TryParse(VersionStr, out var v) ? v : new SemanticVersion(1, 0, 0);

        [JsonIgnore]
        public VersionRange EngineCompatRange => VersionRange.Parse(EngineCompatVersion);

        [JsonIgnore]
        public VersionRange GameCompatRange => VersionRange.Parse(GameCompatVersion);

        [JsonIgnore]
        public VersionRange ApiCompatRange => VersionRange.Parse(ApiCompatVersion);

        public bool IsCompatibleWithEngine(SemanticVersion engineVersion) =>
            EngineCompatRange.Matches(engineVersion);

        public bool IsCompatibleWithGame(SemanticVersion gameVersion) =>
            GameCompatRange.Matches(gameVersion);

        public bool IsCompatibleWithApi(SemanticVersion apiVersion) =>
            ApiCompatRange.Matches(apiVersion);

        public override string ToString() => $"{Name} v{VersionStr} by {Author}";
    }

    #endregion

    #region Mod 清单

    /// <summary>Mod 清单文件 (.mod JSON)。</summary>
    public sealed class ModManifest
    {
        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public ModInfo Info { get; set; } = new ModInfo();
        public string ModPath { get; set; } = string.Empty;
        public string AssetsDirectory { get; set; } = "Assets";
        public string ScriptsDirectory { get; set; } = "Scripts";
        public string ConfigFile { get; set; } = "config.json";
        public string LocalizationDirectory { get; set; } = "Localization";
        public ModSource Source { get; set; } = ModSource.Local;
        public int LoadOrder { get; set; } = 100;
        public int Priority { get; set; } = 100;
        public bool IsEnabled { get; set; } = true;
        public Dictionary<string, object> DefaultConfig { get; set; } = new Dictionary<string, object>();
        public List<string> AssetOverrides { get; set; } = new List<string>();
        public List<string> ScriptFiles { get; set; } = new List<string>();
        public string Signature { get; set; } = string.Empty;
        public string Checksum { get; set; } = string.Empty;

        [JsonIgnore]
        public string FullAssetsPath => Path.Combine(ModPath, AssetsDirectory);

        [JsonIgnore]
        public string FullScriptsPath => Path.Combine(ModPath, ScriptsDirectory);

        [JsonIgnore]
        public string FullConfigPath => Path.Combine(ModPath, ConfigFile);

        [JsonIgnore]
        public string FullLocalizationPath => Path.Combine(ModPath, LocalizationDirectory);

        /// <summary>从 .mod 文件加载清单。</summary>
        public static ModManifest LoadFromFile(string path)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException($"Mod 清单文件不存在: {path}");

            string json = File.ReadAllText(path);
            var manifest = JsonSerializer.Deserialize<ModManifest>(json, JsonOpts) ?? new ModManifest();
            manifest.ModPath = Path.GetDirectoryName(path);

            if (string.IsNullOrEmpty(manifest.Info.ModId))
                manifest.Info.ModId = manifest.Info.Name;

            return manifest;
        }

        /// <summary>保存清单到文件。</summary>
        public void SaveToFile(string path = null)
        {
            path ??= Path.Combine(ModPath, "mod.json");
            string json = JsonSerializer.Serialize(this, JsonOpts);
            File.WriteAllText(path, json);
        }

        public static bool TryLoadFromFile(string path, out ModManifest manifest)
        {
            manifest = null;
            try
            {
                if (!File.Exists(path)) return false;
                manifest = LoadFromFile(path);
                return true;
            }
            catch { return false; }
        }

        /// <summary>计算校验和。</summary>
        public string ComputeChecksum()
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            var files = Directory.GetFiles(ModPath, "*.*", SearchOption.AllDirectories)
                .Where(f => !Path.GetFileName(f).StartsWith("."))
                .OrderBy(f => f)
                .ToList();

            using var combined = new MemoryStream();
            foreach (var file in files)
            {
                byte[] nameBytes = System.Text.Encoding.UTF8.GetBytes(Path.GetFileName(file));
                combined.Write(nameBytes, 0, nameBytes.Length);
                byte[] content = File.ReadAllBytes(file);
                combined.Write(content, 0, content.Length);
            }
            combined.Position = 0;
            byte[] hash = sha.ComputeHash(combined);
            return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        }

        /// <summary>验证完整性。</summary>
        public bool VerifyChecksum()
        {
            if (string.IsNullOrEmpty(Checksum)) return true;
            return ComputeChecksum() == Checksum;
        }
    }

    #endregion

    #region Mod 冲突

    /// <summary>Mod 冲突描述。</summary>
    public sealed class ModConflict
    {
        public ModConflictType ConflictType { get; set; }
        public string ModId1 { get; set; }
        public string ModId2 { get; set; }
        public string ResourcePath { get; set; }
        public string Description { get; set; }
        public string Resolution { get; set; }

        public override string ToString() =>
            $"[{ConflictType}] {ModId1} vs {ModId2}: {ResourcePath} - {Description}";
    }

    #endregion

    #region Mod 内容包

    /// <summary>Mod 内容包。</summary>
    public sealed class ModContentPackage
    {
        public string ModId { get; set; }
        public List<string> AssetFiles { get; } = new List<string>();
        public List<string> ScriptFiles { get; } = new List<string>();
        public Dictionary<string, object> ConfigValues { get; } = new Dictionary<string, object>();
        public Dictionary<string, Dictionary<string, string>> LocalizationStrings { get; } = new Dictionary<string, Dictionary<string, string>>();
        public List<string> AssetOverrides { get; } = new List<string>();

        public void AddAssetFile(string path) => AssetFiles.Add(path);
        public void AddScriptFile(string path) => ScriptFiles.Add(path);

        public void MergeConfig(Dictionary<string, object> config)
        {
            foreach (var kv in config)
                ConfigValues[kv.Key] = kv.Value;
        }

        public void MergeLocalization(string culture, Dictionary<string, string> strings)
        {
            if (!LocalizationStrings.ContainsKey(culture))
                LocalizationStrings[culture] = new Dictionary<string, string>();
            foreach (var kv in strings)
                LocalizationStrings[culture][kv.Key] = kv.Value;
        }

        public void Clear()
        {
            AssetFiles.Clear();
            ScriptFiles.Clear();
            ConfigValues.Clear();
            LocalizationStrings.Clear();
            AssetOverrides.Clear();
        }
    }

    #endregion

    #region Mod 类

    /// <summary>Mod 实例。</summary>
    public sealed class Mod
    {
        public ModManifest Manifest { get; set; }
        public ModInfo Info => Manifest?.Info;
        public ModState State { get; internal set; } = ModState.Inactive;
        public ModContentPackage Content { get; } = new ModContentPackage();
        public PluginConfig Config { get; internal set; }
        public PluginLogger Logger { get; internal set; }
        public string ModId => Info?.ModId ?? string.Empty;
        public DateTime InstallDate { get; set; } = DateTime.Now;
        public DateTime LastLoadTime { get; internal set; }
        public List<string> LoadErrors { get; } = new List<string>();
        public List<ModConflict> Conflicts { get; } = new List<ModConflict>();
        public bool IsEnabled { get => Manifest?.IsEnabled ?? false; set { if (Manifest != null) Manifest.IsEnabled = value; } }

        // 统计
        public int AssetCount => Content.AssetFiles.Count;
        public int ScriptCount => Content.ScriptFiles.Count;
        public long TotalSizeBytes { get; internal set; }

        public Mod() { }

        public Mod(ModManifest manifest)
        {
            Manifest = manifest;
            Content.ModId = ModId;
        }

        /// <summary>加载 Mod 内容。</summary>
        public void LoadContent()
        {
            if (Manifest == null) return;
            State = ModState.Loading;
            LastLoadTime = DateTime.Now;

            try
            {
                Content.Clear();

                // 加载资产文件列表
                if (Directory.Exists(Manifest.FullAssetsPath))
                {
                    var assetFiles = Directory.GetFiles(Manifest.FullAssetsPath, "*.*", SearchOption.AllDirectories);
                    foreach (var file in assetFiles)
                    {
                        string relPath = Path.GetRelativePath(Manifest.FullAssetsPath, file);
                        Content.AddAssetFile(relPath);
                    }
                }

                // 加载脚本文件列表
                if (Directory.Exists(Manifest.FullScriptsPath))
                {
                    var scriptFiles = Directory.GetFiles(Manifest.FullScriptsPath, "*.cs", SearchOption.AllDirectories);
                    foreach (var file in scriptFiles)
                    {
                        string relPath = Path.GetRelativePath(Manifest.FullScriptsPath, file);
                        Content.AddScriptFile(relPath);
                    }
                }
                else
                {
                    foreach (var scriptFile in Manifest.ScriptFiles)
                        Content.AddScriptFile(scriptFile);
                }

                // 加载配置
                if (File.Exists(Manifest.FullConfigPath))
                {
                    Config = new PluginConfig(ModId, Manifest.FullConfigPath, Manifest.DefaultConfig);
                    Config.Load();
                    Config.StartWatching();
                    Content.MergeConfig(Config.GetKeys().ToDictionary(k => k, k => Config.Get<object>(k)));
                }

                // 加载本地化
                if (Directory.Exists(Manifest.FullLocalizationPath))
                {
                    var locFiles = Directory.GetFiles(Manifest.FullLocalizationPath, "*.json", SearchOption.AllDirectories);
                    foreach (var locFile in locFiles)
                    {
                        string culture = Path.GetFileNameWithoutExtension(locFile);
                        string json = File.ReadAllText(locFile);
                        var strings = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                        if (strings != null)
                            Content.MergeLocalization(culture, strings);
                    }
                }

                // 资产覆盖列表
                Content.AssetOverrides.AddRange(Manifest.AssetOverrides);

                // 计算总大小
                TotalSizeBytes = 0;
                if (Directory.Exists(Manifest.ModPath))
                {
                    TotalSizeBytes = Directory.GetFiles(Manifest.ModPath, "*.*", SearchOption.AllDirectories)
                        .Sum(f => new FileInfo(f).Length);
                }

                State = ModState.Active;
                Logger?.Info($"Mod {ModId} 内容加载完成: {AssetCount} 资产, {ScriptCount} 脚本。");
            }
            catch (Exception ex)
            {
                State = ModState.Error;
                LoadErrors.Add(ex.Message);
                Logger?.Error($"Mod {ModId} 内容加载失败", ex);
            }
        }

        /// <summary>卸载 Mod 内容。</summary>
        public void UnloadContent()
        {
            State = ModState.Unloading;
            Config?.StopWatching();
            Content.Clear();
            State = ModState.Inactive;
            Logger?.Info($"Mod {ModId} 内容已卸载。");
        }

        public override string ToString() => $"{Info} [{State}]";
    }

    #endregion

    #region Mod 管理器

    /// <summary>Mod 管理器 (单例)。</summary>
    public sealed class ModManager
    {
        private static readonly Lazy<ModManager> _instance =
            new Lazy<ModManager>(() => new ModManager());
        public static ModManager Instance => _instance.Value;

        // Mod 存储
        private readonly ConcurrentDictionary<string, Mod> _mods = new ConcurrentDictionary<string, Mod>();
        private readonly ConcurrentDictionary<string, ModManifest> _discovered = new ConcurrentDictionary<string, ModManifest>();
        private readonly List<string> _searchPaths = new List<string>();
        private readonly List<string> _loadOrder = new List<string>();
        private readonly object _lock = new object();

        // 配置
        public string ModsRootDirectory { get; set; } = Path.Combine(AppContext.BaseDirectory, "Mods");
        public string DownloadDirectory { get; set; } = Path.Combine(AppContext.BaseDirectory, "Downloads", "Mods");
        public string TempDirectory { get; set; } = Path.Combine(Path.GetTempPath(), "YamaEngine", "Mods");
        public SemanticVersion EngineVersion { get; set; } = new SemanticVersion(1, 0, 0);
        public SemanticVersion GameVersion { get; set; } = new SemanticVersion(1, 0, 0);
        public SemanticVersion ApiVersion { get; set; } = new SemanticVersion(1, 0, 0);
        public bool AutoSortLoadOrder { get; set; } = true;

        // 日志
        private readonly PluginLogger _logger = new PluginLogger("ModManager");

        // 事件
        public event Action<Mod> OnModInstalled;
        public event Action<Mod> OnModUninstalled;
        public event Action<Mod> OnModEnabled;
        public event Action<Mod> OnModDisabled;
        public event Action<Mod, ModConflict> OnModConflict;

        private ModManager()
        {
            _searchPaths.Add(ModsRootDirectory);
            if (!Directory.Exists(DownloadDirectory))
                Directory.CreateDirectory(DownloadDirectory);
            if (!Directory.Exists(TempDirectory))
                Directory.CreateDirectory(TempDirectory);
        }

        #region 发现

        /// <summary>添加搜索路径。</summary>
        public void AddSearchPath(string path)
        {
            if (Directory.Exists(path) && !_searchPaths.Contains(path))
                _searchPaths.Add(path);
        }

        /// <summary>发现所有可用 Mod。</summary>
        public List<ModManifest> DiscoverMods()
        {
            _discovered.Clear();
            var result = new List<ModManifest>();

            foreach (var searchPath in _searchPaths)
            {
                if (!Directory.Exists(searchPath)) continue;
                DiscoverInDirectory(searchPath, result);
            }

            _logger.Info($"发现 {result.Count} 个 Mod。");
            return result;
        }

        private void DiscoverInDirectory(string dir, List<ModManifest> result)
        {
            try
            {
                var files = Directory.GetFiles(dir, "mod.json", SearchOption.AllDirectories)
                    .Concat(Directory.GetFiles(dir, ".mod", SearchOption.AllDirectories));

                foreach (var file in files)
                {
                    if (ModManifest.TryLoadFromFile(file, out var manifest))
                    {
                        _discovered[manifest.Info.ModId] = manifest;
                        result.Add(manifest);
                        _logger.Debug($"发现 Mod: {manifest.Info}");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"扫描目录 {dir} 失败", ex);
            }
        }

        /// <summary>获取所有已发现的 Mod。</summary>
        public IReadOnlyDictionary<string, ModManifest> GetDiscoveredMods() => _discovered;

        #endregion

        #region 安装与卸载

        /// <summary>从压缩包安装 Mod。</summary>
        public bool InstallFromArchive(string archivePath, bool overwrite = false)
        {
            if (!File.Exists(archivePath))
            {
                _logger.Error($"压缩包不存在: {archivePath}");
                return false;
            }

            string tempExtractDir = Path.Combine(TempDirectory, Guid.NewGuid().ToString("N"));
            try
            {
                _logger.Info($"解压 Mod 压缩包: {archivePath}");
                ZipFile.ExtractToDirectory(archivePath, tempExtractDir, overwrite);

                // 查找 mod.json
                var manifestFiles = Directory.GetFiles(tempExtractDir, "mod.json", SearchOption.AllDirectories);
                if (manifestFiles.Length == 0)
                {
                    _logger.Error("压缩包中未找到 mod.json。");
                    return false;
                }

                string manifestFile = manifestFiles[0];
                string modDir = Path.GetDirectoryName(manifestFile);
                var manifest = ModManifest.LoadFromFile(manifestFile);
                manifest.Source = ModSource.CompressedPackage;

                // 验证依赖
                var missingDeps = CheckDependencies(manifest.Info);
                if (missingDeps.Count > 0)
                {
                    _logger.Warning($"缺少依赖: {string.Join(", ", missingDeps)}");
                    // 仍然允许安装，但标记
                }

                // 检测冲突
                var conflicts = DetectConflicts(manifest);
                foreach (var conflict in conflicts)
                {
                    _logger.Warning($"冲突检测: {conflict}");
                }

                // 移动到 Mods 目录
                string targetDir = Path.Combine(ModsRootDirectory, manifest.Info.ModId);
                if (Directory.Exists(targetDir))
                {
                    if (!overwrite)
                    {
                        _logger.Error($"Mod 已存在: {manifest.Info.ModId}");
                        return false;
                    }
                    Directory.Delete(targetDir, true);
                }

                CopyDirectory(tempExtractDir, targetDir);
                manifest.ModPath = targetDir;
                manifest.SaveToFile();

                _discovered[manifest.Info.ModId] = manifest;
                _logger.Info($"Mod {manifest.Info} 安装成功。");
                return true;
            }
            catch (Exception ex)
            {
                _logger.Error($"安装 Mod 失败", ex);
                return false;
            }
            finally
            {
                if (Directory.Exists(tempExtractDir))
                    try { Directory.Delete(tempExtractDir, true); } catch { }
            }
        }

        /// <summary>从目录安装 Mod。</summary>
        public bool InstallFromDirectory(string sourceDir, bool overwrite = false)
        {
            string manifestFile = Path.Combine(sourceDir, "mod.json");
            if (!File.Exists(manifestFile))
            {
                _logger.Error($"目录中未找到 mod.json: {sourceDir}");
                return false;
            }

            try
            {
                var manifest = ModManifest.LoadFromFile(manifestFile);
                manifest.Source = ModSource.ManualInstall;

                string targetDir = Path.Combine(ModsRootDirectory, manifest.Info.ModId);
                if (Directory.Exists(targetDir))
                {
                    if (!overwrite) return false;
                    Directory.Delete(targetDir, true);
                }

                CopyDirectory(sourceDir, targetDir);
                manifest.ModPath = targetDir;
                manifest.SaveToFile();

                _discovered[manifest.Info.ModId] = manifest;
                _logger.Info($"Mod {manifest.Info} 从目录安装成功。");
                return true;
            }
            catch (Exception ex)
            {
                _logger.Error($"从目录安装 Mod 失败", ex);
                return false;
            }
        }

        /// <summary>卸载 Mod (删除文件)。</summary>
        public bool UninstallMod(string modId)
        {
            if (!_discovered.TryRemove(modId, out var manifest))
            {
                _logger.Warning($"Mod {modId} 未发现。");
                return false;
            }

            // 先卸载
            if (_mods.TryRemove(modId, out var mod))
            {
                mod.UnloadContent();
                OnModUninstalled?.Invoke(mod);
            }

            // 删除目录
            try
            {
                if (Directory.Exists(manifest.ModPath))
                    Directory.Delete(manifest.ModPath, true);
                _logger.Info($"Mod {modId} 已卸载并删除。");
                return true;
            }
            catch (Exception ex)
            {
                _logger.Error($"删除 Mod {modId} 文件失败", ex);
                return false;
            }
        }

        #endregion

        #region 启用与禁用

        /// <summary>启用 Mod。</summary>
        public bool EnableMod(string modId)
        {
            if (!_discovered.TryGetValue(modId, out var manifest))
            {
                _logger.Warning($"Mod {modId} 未发现。");
                return false;
            }

            // 兼容性检查
            if (!manifest.Info.IsCompatibleWithEngine(EngineVersion))
            {
                _logger.Error($"Mod {modId} 不兼容引擎版本 {EngineVersion}");
                return false;
            }

            if (!manifest.Info.IsCompatibleWithGame(GameVersion))
            {
                _logger.Error($"Mod {modId} 不兼容游戏版本 {GameVersion}");
                return false;
            }

            // 依赖检查
            var missingDeps = CheckDependencies(manifest.Info);
            if (missingDeps.Count > 0)
            {
                _logger.Error($"Mod {modId} 缺少依赖: {string.Join(", ", missingDeps)}");
                return false;
            }

            manifest.IsEnabled = true;
            manifest.SaveToFile();

            // 加载 Mod
            var mod = new Mod(manifest)
            {
                Logger = new PluginLogger($"Mod_{modId}"),
                Config = new PluginConfig(modId, manifest.FullConfigPath, manifest.DefaultConfig)
            };
            mod.LoadContent();

            _mods[modId] = mod;
            OnModEnabled?.Invoke(mod);
            _logger.Info($"Mod {modId} 已启用。");
            return true;
        }

        /// <summary>禁用 Mod。</summary>
        public bool DisableMod(string modId)
        {
            if (_mods.TryRemove(modId, out var mod))
            {
                mod.UnloadContent();
                OnModDisabled?.Invoke(mod);
                _logger.Info($"Mod {modId} 已禁用。");
                return true;
            }

            if (_discovered.TryGetValue(modId, out var manifest))
            {
                manifest.IsEnabled = false;
                manifest.SaveToFile();
                return true;
            }

            return false;
        }

        #endregion

        #region 加载顺序

        /// <summary>计算加载顺序 (拓扑排序 + 优先级)。</summary>
        public List<string> ComputeLoadOrder()
        {
            var enabledMods = _discovered.Values
                .Where(m => m.IsEnabled)
                .ToList();

            var sorted = new List<string>();
            var visited = new HashSet<string>();
            var visiting = new HashSet<string>();

            void Visit(ModManifest manifest, int depth = 0)
            {
                string id = manifest.Info.ModId;
                if (visited.Contains(id)) return;
                if (visiting.Contains(id)) return; // 循环依赖，跳过
                if (depth > 64) return;

                visiting.Add(id);

                foreach (var dep in manifest.Info.Dependencies)
                {
                    if (_discovered.TryGetValue(dep.ModId, out var depManifest))
                        Visit(depManifest, depth + 1);
                }

                visiting.Remove(id);
                visited.Add(id);
                sorted.Add(id);
            }

            // 先按优先级排序
            foreach (var manifest in enabledMods.OrderBy(m => m.Priority))
                Visit(manifest);

            if (AutoSortLoadOrder)
            {
                _loadOrder.Clear();
                _loadOrder.AddRange(sorted);
            }

            return sorted;
        }

        /// <summary>获取当前加载顺序。</summary>
        public IReadOnlyList<string> GetLoadOrder() => _loadOrder.AsReadOnly();

        /// <summary>手动设置加载顺序。</summary>
        public void SetLoadOrder(List<string> order)
        {
            _loadOrder.Clear();
            _loadOrder.AddRange(order);
            AutoSortLoadOrder = false;
        }

        /// <summary>移动 Mod 在加载顺序中的位置。</summary>
        public void MoveModInLoadOrder(string modId, int newIndex)
        {
            int currentIdx = _loadOrder.IndexOf(modId);
            if (currentIdx < 0 || newIndex < 0 || newIndex >= _loadOrder.Count) return;
            _loadOrder.RemoveAt(currentIdx);
            _loadOrder.Insert(newIndex, modId);
        }

        #endregion

        #region 冲突检测

        /// <summary>检测 Mod 冲突。</summary>
        public List<ModConflict> DetectConflicts(ModManifest manifest)
        {
            var conflicts = new List<ModConflict>();

            // 不兼容检查
            foreach (var incompat in manifest.Info.Incompatibilities)
            {
                if (_discovered.ContainsKey(incompat) || _mods.ContainsKey(incompat))
                {
                    conflicts.Add(new ModConflict
                    {
                        ConflictType = ModConflictType.DependencyConflict,
                        ModId1 = manifest.Info.ModId,
                        ModId2 = incompat,
                        Description = $"Mod 与 {incompat} 不兼容"
                    });
                }
            }

            // 资产冲突检查
            if (Directory.Exists(manifest.FullAssetsPath))
            {
                var assetFiles = Directory.GetFiles(manifest.FullAssetsPath, "*.*", SearchOption.AllDirectories);
                foreach (var assetFile in assetFiles)
                {
                    string relPath = Path.GetRelativePath(manifest.FullAssetsPath, assetFile);
                    foreach (var kv in _mods)
                    {
                        if (kv.Key == manifest.Info.ModId) continue;
                        string otherAsset = Path.Combine(kv.Value.Manifest.FullAssetsPath, relPath);
                        if (File.Exists(otherAsset))
                        {
                            conflicts.Add(new ModConflict
                            {
                                ConflictType = ModConflictType.AssetConflict,
                                ModId1 = manifest.Info.ModId,
                                ModId2 = kv.Key,
                                ResourcePath = relPath,
                                Description = $"资产冲突: {relPath}"
                            });
                        }
                    }
                }
            }

            // 脚本冲突检查
            if (Directory.Exists(manifest.FullScriptsPath))
            {
                var scriptFiles = Directory.GetFiles(manifest.FullScriptsPath, "*.cs", SearchOption.AllDirectories);
                foreach (var scriptFile in scriptFiles)
                {
                    string relPath = Path.GetRelativePath(manifest.FullScriptsPath, scriptFile);
                    foreach (var kv in _mods)
                    {
                        if (kv.Key == manifest.Info.ModId) continue;
                        string otherScript = Path.Combine(kv.Value.Manifest.FullScriptsPath, relPath);
                        if (File.Exists(otherScript))
                        {
                            conflicts.Add(new ModConflict
                            {
                                ConflictType = ModConflictType.ScriptConflict,
                                ModId1 = manifest.Info.ModId,
                                ModId2 = kv.Key,
                                ResourcePath = relPath,
                                Description = $"脚本冲突: {relPath}"
                            });
                        }
                    }
                }
            }

            foreach (var conflict in conflicts)
                OnModConflict?.Invoke(null, conflict);

            return conflicts;
        }

        /// <summary>解决冲突 (按优先级)。</summary>
        public void ResolveConflicts()
        {
            foreach (var kv in _mods)
            {
                var conflicts = kv.Value.Conflicts;
                foreach (var conflict in conflicts)
                {
                    if (conflict.ConflictType == ModConflictType.AssetConflict)
                    {
                        // 按加载顺序决定优先级
                        int idx1 = _loadOrder.IndexOf(conflict.ModId1);
                        int idx2 = _loadOrder.IndexOf(conflict.ModId2);
                        conflict.Resolution = idx1 < idx2 ? conflict.ModId1 : conflict.ModId2;
                    }
                }
            }
        }

        #endregion

        #region 依赖检查

        /// <summary>检查缺失的必需依赖。</summary>
        public List<string> CheckDependencies(ModInfo info)
        {
            var missing = new List<string>();
            foreach (var dep in info.Dependencies)
            {
                if (!dep.IsRequired) continue;

                bool found = _discovered.ContainsKey(dep.ModId) || _mods.ContainsKey(dep.ModId);
                if (!found)
                {
                    missing.Add(dep.ModId);
                    continue;
                }

                // 版本检查
                if (_discovered.TryGetValue(dep.ModId, out var manifest))
                {
                    if (!dep.MatchesVersion(manifest.Info.Version))
                        missing.Add($"{dep.ModId} (版本不匹配: 需要 {dep.VersionRangeStr}, 实际 {manifest.Info.VersionStr})");
                }
            }
            return missing;
        }

        #endregion

        #region 兼容性矩阵

        /// <summary>检查兼容性。</summary>
        public ModCompatibilityReport CheckCompatibility(ModInfo info)
        {
            var report = new ModCompatibilityReport { ModId = info.ModId };

            report.EngineCompatible = info.IsCompatibleWithEngine(EngineVersion);
            report.GameCompatible = info.IsCompatibleWithGame(GameVersion);
            report.ApiCompatible = info.IsCompatibleWithApi(ApiVersion);
            report.MissingDependencies = CheckDependencies(info);

            // 检查不兼容 Mod
            foreach (var incompat in info.Incompatibilities)
            {
                if (_discovered.ContainsKey(incompat) || _mods.ContainsKey(incompat))
                    report.IncompatibleMods.Add(incompat);
            }

            report.IsCompatible = report.EngineCompatible && report.GameCompatible &&
                                   report.ApiCompatible && report.MissingDependencies.Count == 0 &&
                                   report.IncompatibleMods.Count == 0;

            return report;
        }

        #endregion

        #region 打包

        /// <summary>打包 Mod 为 .modpak 压缩包。</summary>
        public bool PackMod(string modId, string outputPath, bool sign = false, bool encrypt = false)
        {
            if (!_discovered.TryGetValue(modId, out var manifest))
            {
                _logger.Error($"Mod {modId} 未发现，无法打包。");
                return false;
            }

            try
            {
                outputPath ??= Path.Combine(DownloadDirectory, $"{modId}_{manifest.Info.VersionStr}.modpak");

                // 计算校验和
                manifest.Checksum = manifest.ComputeChecksum();
                manifest.SaveToFile();

                // 创建压缩包
                if (File.Exists(outputPath))
                    File.Delete(outputPath);

                ZipFile.CreateFromDirectory(manifest.ModPath, outputPath);

                // 签名 (简化实现)
                if (sign)
                {
                    manifest.Signature = ComputeSignature(manifest.Checksum);
                    // Re-create with signature
                    manifest.SaveToFile();
                    ZipFile.CreateFromDirectory(manifest.ModPath, outputPath);
                }

                _logger.Info($"Mod {modId} 已打包到 {outputPath}");
                return true;
            }
            catch (Exception ex)
            {
                _logger.Error($"打包 Mod {modId} 失败", ex);
                return false;
            }
        }

        private string ComputeSignature(string data)
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            byte[] hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(data + DateTime.UtcNow.Ticks));
            return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        }

        #endregion

        #region 更新

        /// <summary>检查 Mod 更新。</summary>
        public async Task<bool> CheckForUpdates(string modId)
        {
            // 占位：实际实现需要连接到 Workshop 或市场
            await Task.Delay(0);
            return false;
        }

        /// <summary>更新 Mod。</summary>
        public bool UpdateMod(string modId)
        {
            // 占位：实际实现需要下载新版本并替换
            _logger.Info($"Mod {modId} 更新功能尚未实现。");
            return false;
        }

        #endregion

        #region 查询

        /// <summary>获取 Mod。</summary>
        public Mod GetMod(string modId) =>
            _mods.TryGetValue(modId, out var mod) ? mod : null;

        /// <summary>获取所有活跃 Mod。</summary>
        public IEnumerable<Mod> GetActiveMods() =>
            _mods.Values.Where(m => m.State == ModState.Active);

        /// <summary>获取所有已发现的 Mod。</summary>
        public IEnumerable<ModManifest> GetAllDiscoveredMods() => _discovered.Values;

        /// <summary>Mod 是否已安装。</summary>
        public bool IsInstalled(string modId) => _discovered.ContainsKey(modId);

        /// <summary>Mod 是否活跃。</summary>
        public bool IsActive(string modId) =>
            _mods.TryGetValue(modId, out var mod) && mod.State == ModState.Active;

        /// <summary>获取 Mod 数量。</summary>
        public int ActiveCount => _mods.Count(m => m.Value.State == ModState.Active);
        public int InstalledCount => _discovered.Count;

        #endregion

        #region 工具方法

        private static void CopyDirectory(string source, string target)
        {
            Directory.CreateDirectory(target);
            foreach (var file in Directory.GetFiles(source, "*.*", SearchOption.TopDirectoryOnly))
            {
                File.Copy(file, Path.Combine(target, Path.GetFileName(file)), true);
            }
            foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.TopDirectoryOnly))
            {
                CopyDirectory(dir, Path.Combine(target, Path.GetFileName(dir)));
            }
        }

        #endregion
    }

    #endregion

    #region 兼容性报告

    /// <summary>Mod 兼容性报告。</summary>
    public sealed class ModCompatibilityReport
    {
        public string ModId { get; set; }
        public bool IsCompatible { get; set; }
        public bool EngineCompatible { get; set; }
        public bool GameCompatible { get; set; }
        public bool ApiCompatible { get; set; }
        public List<string> MissingDependencies { get; set; } = new List<string>();
        public List<string> IncompatibleMods { get; set; } = new List<string>();

        public override string ToString() =>
            $"{ModId}: {(IsCompatible ? "兼容" : "不兼容")} " +
            $"[Engine:{(EngineCompatible ? "Y" : "N")} Game:{(GameCompatible ? "Y" : "N")} API:{(ApiCompatible ? "Y" : "N")}] " +
            $"Missing:{MissingDependencies.Count} Incompat:{IncompatibleMods.Count}";
    }

    #endregion
}
