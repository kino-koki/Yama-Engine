using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace ShanyuEngine.PluginSystem
{
    // ════════════════════════════════════════════════════════════════════════
    // 山雨引擎 - 插件资产系统 (PluginAssetSystem)
    //
    // 插件资产管理与虚拟文件系统:
    //   * 资产挂载 (插件资产目录挂载到虚拟文件系统 / 路径映射)
    //   * 资产覆盖 (高优先级插件覆盖低优先级插件资产 / Mod 覆盖基础游戏资产)
    //   * 资产合并 (配置文件合并 / 数据表合并 / 本地化字符串合并)
    //   * 资产重定向 (请求路径A→实际路径B / 虚拟路径映射)
    //   * 资产版本 (插件资产版本 / 兼容性检查 / 升级迁移)
    //   * 资产搜索 (跨插件搜索 / 按类型搜索 / 按标签搜索 / 按名称搜索)
    //   * 资产依赖追踪 (资产→插件反向映射 / 删除插件时检查依赖)
    // ════════════════════════════════════════════════════════════════════════

    #region 资产类型与枚举

    /// <summary>资产类型。</summary>
    public enum PluginAssetType
    {
        Unknown,
        Texture,
        Mesh,
        Material,
        Shader,
        Audio,
        Animation,
        Script,
        Config,
        Localization,
        Prefab,
        Scene,
        Font,
        Data,
        Other
    }

    /// <summary>资产合并模式。</summary>
    public enum AssetMergeMode
    {
        /// <summary>高优先级覆盖低优先级</summary>
        Override,
        /// <summary>合并 (字典/列表/表)</summary>
        Merge,
        /// <summary>附加到列表末尾</summary>
        Append,
        /// <summary>前置到列表开头</summary>
        Prepend,
        /// <summary>深度合并 (递归合并字典)</summary>
        DeepMerge
    }

    #endregion

    #region 资产条目

    /// <summary>插件资产条目。</summary>
    public sealed class PluginAssetEntry
    {
        public string VirtualPath { get; set; } = string.Empty;
        public string PhysicalPath { get; set; } = string.Empty;
        public string PluginId { get; set; } = string.Empty;
        public PluginAssetType AssetType { get; set; } = PluginAssetType.Unknown;
        public int Priority { get; set; } = 100;
        public SemanticVersion Version { get; set; } = new SemanticVersion(1, 0, 0);
        public List<string> Tags { get; set; } = new List<string>();
        public long FileSize { get; set; }
        public DateTime LastModified { get; set; }
        public string Checksum { get; set; } = string.Empty;

        [JsonIgnore]
        public bool Exists => File.Exists(PhysicalPath);

        [JsonIgnore]
        public string Extension => Path.GetExtension(VirtualPath);

        public override string ToString() => $"[{PluginId}] {VirtualPath} (P:{Priority})";
    }

    #endregion

    #region 资产挂载点

    /// <summary>资产挂载点。</summary>
    public sealed class AssetMountPoint
    {
        public string PluginId { get; set; }
        public string PhysicalRoot { get; set; }
        public string VirtualRoot { get; set; }
        public int Priority { get; set; }
        public bool IsReadOnly { get; set; } = true;
        public List<PluginAssetEntry> Entries { get; } = new List<PluginAssetEntry>();

        public override string ToString() => $"[{PluginId}] {VirtualRoot} ← {PhysicalRoot} (P:{Priority})";
    }

    #endregion

    #region 资产重定向

    /// <summary>资产重定向规则。</summary>
    public sealed class AssetRedirect
    {
        public string FromPath { get; set; }
        public string ToPath { get; set; }
        public string PluginId { get; set; }
        public int Priority { get; set; }

        public override string ToString() => $"{FromPath} → {ToPath} [{PluginId}]";
    }

    #endregion

    #region 资产依赖追踪

    /// <summary>资产依赖记录。</summary>
    public sealed class AssetDependencyRecord
    {
        public string AssetPath { get; set; }
        public string DependentPluginId { get; set; }
        public string ProviderPluginId { get; set; }
        public DateTime AccessTime { get; set; }
        public int AccessCount { get; set; }

        public override string ToString() =>
            $"{AssetPath}: {DependentPluginId} → {ProviderPluginId} ({AccessCount} 次)";
    }

    #endregion

    #region 资产搜索结果

    /// <summary>资产搜索结果。</summary>
    public sealed class AssetSearchResult
    {
        public PluginAssetEntry Entry { get; set; }
        public int MatchScore { get; set; }
        public string MatchReason { get; set; }

        public override string ToString() => $"{Entry} (Score: {MatchScore})";
    }

    #endregion

    #region 插件资产提供者

    /// <summary>插件资产提供者 (管理插件资产的挂载/覆盖/合并/重定向/搜索)。</summary>
    public sealed class PluginAssetProvider
    {
        private static readonly Lazy<PluginAssetProvider> _instance =
            new Lazy<PluginAssetProvider>(() => new PluginAssetProvider());
        public static PluginAssetProvider Instance => _instance.Value;

        // 挂载点 (按优先级排序)
        private readonly List<AssetMountPoint> _mountPoints = new List<AssetMountPoint>();
        private readonly object _mountLock = new object();

        // 虚拟路径 → 资产条目映射 (高优先级在前)
        private readonly ConcurrentDictionary<string, List<PluginAssetEntry>> _pathMap = new ConcurrentDictionary<string, List<PluginAssetEntry>>();

        // 重定向规则
        private readonly List<AssetRedirect> _redirects = new List<AssetRedirect>();
        private readonly object _redirectLock = new object();

        // 资产依赖追踪
        private readonly ConcurrentDictionary<string, AssetDependencyRecord> _dependencyTracker = new ConcurrentDictionary<string, AssetDependencyRecord>();

        // 资产缓存
        private readonly ConcurrentDictionary<string, byte[]> _assetCache = new ConcurrentDictionary<string, byte[]>();
        private readonly ConcurrentDictionary<string, string> _textCache = new ConcurrentDictionary<string, string>();

        // 日志
        private readonly PluginLogger _logger = new PluginLogger("PluginAssetProvider");

        // 配置
        public bool EnableCache { get; set; } = true;
        public int MaxCacheSize { get; set; } = 256 * 1024 * 1024; // 256MB
        private long _currentCacheSize;

        // 事件
        public event Action<PluginAssetEntry> OnAssetMounted;
        public event Action<PluginAssetEntry> OnAssetUnmounted;
        public event Action<string, PluginAssetEntry> OnAssetAccessed;
        public event Action<string, string> OnAssetRedirected;

        private PluginAssetProvider() { }

        #region 挂载

        /// <summary>挂载插件资产目录。</summary>
        public void Mount(string pluginId, string physicalRoot, string virtualRoot, int priority = 100, bool readOnly = true)
        {
            if (!Directory.Exists(physicalRoot))
            {
                _logger.Warning($"挂载目录不存在: {physicalRoot}");
                return;
            }

            var mountPoint = new AssetMountPoint
            {
                PluginId = pluginId,
                PhysicalRoot = physicalRoot,
                VirtualRoot = NormalizePath(virtualRoot),
                Priority = priority,
                IsReadOnly = readOnly
            };

            // 扫描所有文件
            var files = Directory.GetFiles(physicalRoot, "*.*", SearchOption.AllDirectories);
            foreach (var file in files)
            {
                string relPath = Path.GetRelativePath(physicalRoot, file).Replace('\\', '/');
                string virtualPath = NormalizePath(virtualRoot.TrimEnd('/') + "/" + relPath);
                var fileInfo = new FileInfo(file);

                var entry = new PluginAssetEntry
                {
                    VirtualPath = virtualPath,
                    PhysicalPath = file,
                    PluginId = pluginId,
                    AssetType = DetectAssetType(file),
                    Priority = priority,
                    FileSize = fileInfo.Length,
                    LastModified = fileInfo.LastWriteTime
                };

                mountPoint.Entries.Add(entry);

                // 添加到路径映射
                _pathMap.AddOrUpdate(
                    virtualPath,
                    new List<PluginAssetEntry> { entry },
                    (key, list) => { list.Add(entry); return list; });

                OnAssetMounted?.Invoke(entry);
            }

            lock (_mountLock)
            {
                _mountPoints.Add(mountPoint);
                _mountPoints.Sort((a, b) => b.Priority.CompareTo(a.Priority)); // 高优先级在前
            }

            // 重建路径映射的优先级排序
            RebuildPathMap();

            _logger.Info($"挂载插件资产: [{pluginId}] {virtualRoot} ← {physicalRoot} ({mountPoint.Entries.Count} 文件, P:{priority})");
        }

        /// <summary>卸载插件资产。</summary>
        public void Unmount(string pluginId)
        {
            lock (_mountLock)
            {
                var toRemove = _mountPoints.Where(m => m.PluginId == pluginId).ToList();
                foreach (var mp in toRemove)
                {
                    _mountPoints.Remove(mp);
                    foreach (var entry in mp.Entries)
                    {
                        // 从路径映射中移除
                        if (_pathMap.TryGetValue(entry.VirtualPath, out var list))
                        {
                            list.RemoveAll(e => e.PluginId == pluginId);
                            if (list.Count == 0)
                                _pathMap.TryRemove(entry.VirtualPath, out _);
                        }

                        // 从缓存中移除
                        _assetCache.TryRemove(entry.VirtualPath, out _);
                        _textCache.TryRemove(entry.VirtualPath, out _);
                        _currentCacheSize -= entry.FileSize;

                        OnAssetUnmounted?.Invoke(entry);
                    }
                    _logger.Info($"卸载插件资产: [{pluginId}] {mp.VirtualRoot} ({mp.Entries.Count} 文件)");
                }
            }

            // 移除重定向规则
            lock (_redirectLock)
            {
                _redirects.RemoveAll(r => r.PluginId == pluginId);
            }

            RebuildPathMap();
        }

        private void RebuildPathMap()
        {
            foreach (var kv in _pathMap)
            {
                kv.Value.Sort((a, b) => b.Priority.CompareTo(a.Priority));
            }
        }

        #endregion

        #region 资产访问

        /// <summary>解析虚拟路径 (考虑重定向和优先级)。</summary>
        public PluginAssetEntry ResolveAsset(string virtualPath)
        {
            virtualPath = NormalizePath(virtualPath);

            // 检查重定向
            string redirectedPath = ResolveRedirect(virtualPath);
            if (redirectedPath != virtualPath)
            {
                OnAssetRedirected?.Invoke(virtualPath, redirectedPath);
                virtualPath = redirectedPath;
            }

            if (_pathMap.TryGetValue(virtualPath, out var entries) && entries.Count > 0)
            {
                var entry = entries[0]; // 最高优先级
                OnAssetAccessed?.Invoke(virtualPath, entry);
                TrackDependency(virtualPath, entry.PluginId);
                return entry;
            }

            return null;
        }

        /// <summary>读取资产字节。</summary>
        public byte[] LoadAssetBytes(string virtualPath)
        {
            var entry = ResolveAsset(virtualPath);
            if (entry == null || !entry.Exists) return null;

            if (EnableCache && _assetCache.TryGetValue(entry.VirtualPath, out var cached))
                return cached;

            byte[] data = File.ReadAllBytes(entry.PhysicalPath);

            if (EnableCache)
            {
                _assetCache[entry.VirtualPath] = data;
                _currentCacheSize += data.Length;
                TrimCacheIfNeeded();
            }

            return data;
        }

        /// <summary>读取资产文本。</summary>
        public string LoadAssetText(string virtualPath)
        {
            var entry = ResolveAsset(virtualPath);
            if (entry == null || !entry.Exists) return null;

            if (EnableCache && _textCache.TryGetValue(entry.VirtualPath, out var cached))
                return cached;

            string text = File.ReadAllText(entry.PhysicalPath);

            if (EnableCache)
            {
                _textCache[entry.VirtualPath] = text;
            }

            return text;
        }

        /// <summary>读取资产流。</summary>
        public Stream LoadAssetStream(string virtualPath)
        {
            var entry = ResolveAsset(virtualPath);
            if (entry == null || !entry.Exists) return null;
            return new FileStream(entry.PhysicalPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        }

        /// <summary>资产是否存在。</summary>
        public bool AssetExists(string virtualPath)
        {
            return ResolveAsset(virtualPath) != null;
        }

        /// <summary>获取资产所有版本 (不同插件提供的同一资产)。</summary>
        public List<PluginAssetEntry> GetAssetVersions(string virtualPath)
        {
            virtualPath = NormalizePath(virtualPath);
            return _pathMap.TryGetValue(virtualPath, out var entries) ? entries.ToList() : new List<PluginAssetEntry>();
        }

        #endregion

        #region 资产覆盖

        /// <summary>获取资产提供者 (按优先级)。</summary>
        public string GetAssetProvider(string virtualPath)
        {
            var entry = ResolveAsset(virtualPath);
            return entry?.PluginId;
        }

        /// <summary>检查资产是否被覆盖。</summary>
        public bool IsAssetOverridden(string virtualPath)
        {
            virtualPath = NormalizePath(virtualPath);
            return _pathMap.TryGetValue(virtualPath, out var entries) && entries.Count > 1;
        }

        /// <summary>获取覆盖链。</summary>
        public List<PluginAssetEntry> GetOverrideChain(string virtualPath)
        {
            return GetAssetVersions(virtualPath);
        }

        #endregion

        #region 资产合并

        /// <summary>合并资产 (配置/数据表/本地化)。</summary>
        public string MergeAssets(string virtualPathPattern, AssetMergeMode mode = AssetMergeMode.DeepMerge)
        {
            var entries = GetAllAssetsMatching(virtualPathPattern);
            if (entries.Count == 0) return null;
            if (entries.Count == 1) return LoadAssetText(entries[0].VirtualPath);

            _logger.Debug($"合并 {entries.Count} 个资产: {virtualPathPattern}");

            // 按优先级排序 (低优先级先加载，高优先级覆盖)
            var sorted = entries.OrderBy(e => e.Priority).ToList();

            string merged = null;
            foreach (var entry in sorted)
            {
                string content = LoadAssetText(entry.VirtualPath);
                if (merged == null)
                {
                    merged = content;
                }
                else
                {
                    merged = MergeContent(merged, content, mode);
                }
            }

            return merged;
        }

        /// <summary>合并本地化字符串。</summary>
        public Dictionary<string, string> MergeLocalization(string culture, string virtualPathPattern = "Localization/*")
        {
            var result = new Dictionary<string, string>();
            var entries = GetAllAssetsMatching(virtualPathPattern)
                .Where(e => e.AssetType == PluginAssetType.Localization || e.VirtualPath.EndsWith($".{culture}.json") || e.VirtualPath.EndsWith($"/{culture}.json"))
                .OrderBy(e => e.Priority)
                .ToList();

            foreach (var entry in entries)
            {
                string content = LoadAssetText(entry.VirtualPath);
                if (string.IsNullOrEmpty(content)) continue;
                try
                {
                    var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(content);
                    if (dict != null)
                        foreach (var kv in dict)
                            result[kv.Key] = kv.Value; // 高优先级覆盖
                }
                catch (Exception ex)
                {
                    _logger.Warning($"合并本地化失败: {entry.VirtualPath}: {ex.Message}");
                }
            }

            return result;
        }

        private string MergeContent(string existing, string incoming, AssetMergeMode mode)
        {
            switch (mode)
            {
                case AssetMergeMode.Override:
                    return incoming;

                case AssetMergeMode.Append:
                    return existing + "\n" + incoming;

                case AssetMergeMode.Prepend:
                    return incoming + "\n" + existing;

                case AssetMergeMode.Merge:
                case AssetMergeMode.DeepMerge:
                    // 尝试 JSON 深度合并
                    try
                    {
                        var existingDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(existing);
                        var incomingDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(incoming);
                        if (existingDict != null && incomingDict != null)
                        {
                            if (mode == AssetMergeMode.DeepMerge)
                                DeepMergeJson(existingDict, incomingDict);
                            else
                                foreach (var kv in incomingDict)
                                    existingDict[kv.Key] = kv.Value;
                            return JsonSerializer.Serialize(existingDict, new JsonSerializerOptions { WriteIndented = true });
                        }
                    }
                    catch { }
                    return incoming; // 非 JSON 直接覆盖

                default:
                    return incoming;
            }
        }

        private void DeepMergeJson(Dictionary<string, JsonElement> target, Dictionary<string, JsonElement> source)
        {
            foreach (var kv in source)
            {
                if (target.TryGetValue(kv.Key, out var existing) &&
                    existing.ValueKind == JsonValueKind.Object &&
                    kv.Value.ValueKind == JsonValueKind.Object)
                {
                    var existingDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(existing.GetRawText());
                    var incomingDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(kv.Value.GetRawText());
                    if (existingDict != null && incomingDict != null)
                    {
                        DeepMergeJson(existingDict, incomingDict);
                        target[kv.Key] = JsonDocument.Parse(JsonSerializer.Serialize(existingDict)).RootElement.Clone();
                        continue;
                    }
                }
                target[kv.Key] = kv.Value;
            }
        }

        #endregion

        #region 资产重定向

        /// <summary>添加重定向规则。</summary>
        public void AddRedirect(string fromPath, string toPath, string pluginId, int priority = 100)
        {
            lock (_redirectLock)
            {
                _redirects.Add(new AssetRedirect
                {
                    FromPath = NormalizePath(fromPath),
                    ToPath = NormalizePath(toPath),
                    PluginId = pluginId,
                    Priority = priority
                });
                _redirects.Sort((a, b) => b.Priority.CompareTo(a.Priority));
            }
            _logger.Debug($"添加重定向: {fromPath} → {toPath} [{pluginId}]");
        }

        /// <summary>移除重定向规则。</summary>
        public void RemoveRedirects(string pluginId)
        {
            lock (_redirectLock)
            {
                _redirects.RemoveAll(r => r.PluginId == pluginId);
            }
        }

        private string ResolveRedirect(string path)
        {
            lock (_redirectLock)
            {
                foreach (var redirect in _redirects)
                {
                    if (redirect.FromPath == path)
                        return redirect.ToPath;
                    // 通配符匹配
                    if (redirect.FromPath.EndsWith("/*"))
                    {
                        string prefix = redirect.FromPath.Substring(0, redirect.FromPath.Length - 1);
                        if (path.StartsWith(prefix))
                            return redirect.ToPath.TrimEnd('/') + path.Substring(prefix.Length - 1);
                    }
                }
            }
            return path;
        }

        #endregion

        #region 资产搜索

        /// <summary>跨插件搜索资产。</summary>
        public List<AssetSearchResult> SearchAssets(string query, PluginAssetType? typeFilter = null, List<string> tagFilter = null, int maxResults = 100)
        {
            var results = new List<AssetSearchResult>();
            query = query?.ToLowerInvariant() ?? "";

            foreach (var kv in _pathMap)
            {
                foreach (var entry in kv.Value)
                {
                    if (typeFilter.HasValue && entry.AssetType != typeFilter.Value) continue;
                    if (tagFilter != null && tagFilter.Count > 0 && !tagFilter.All(t => entry.Tags.Contains(t))) continue;

                    int score = 0;
                    string reason = "";

                    string fileName = Path.GetFileName(entry.VirtualPath).ToLowerInvariant();
                    string dirName = Path.GetDirectoryName(entry.VirtualPath)?.ToLowerInvariant() ?? "";

                    if (fileName == query) { score = 100; reason = "精确匹配文件名"; }
                    else if (fileName.Contains(query)) { score = 80; reason = "文件名包含"; }
                    else if (dirName.Contains(query)) { score = 60; reason = "目录名包含"; }
                    else if (entry.Tags.Any(t => t.ToLowerInvariant().Contains(query))) { score = 50; reason = "标签匹配"; }

                    if (score > 0)
                    {
                        results.Add(new AssetSearchResult { Entry = entry, MatchScore = score, MatchReason = reason });
                        if (results.Count >= maxResults) goto done;
                    }
                }
            }

            done:
            results.Sort((a, b) => b.MatchScore.CompareTo(a.MatchScore));
            return results;
        }

        /// <summary>按类型搜索。</summary>
        public List<PluginAssetEntry> GetAssetsByType(PluginAssetType type)
        {
            var results = new List<PluginAssetEntry>();
            foreach (var kv in _pathMap)
            {
                foreach (var entry in kv.Value)
                {
                    if (entry.AssetType == type)
                    {
                        results.Add(entry);
                        break; // 只取每个路径的最高优先级
                    }
                }
            }
            return results;
        }

        /// <summary>按标签搜索。</summary>
        public List<PluginAssetEntry> GetAssetsByTag(string tag)
        {
            var results = new List<PluginAssetEntry>();
            foreach (var kv in _pathMap)
            {
                foreach (var entry in kv.Value)
                {
                    if (entry.Tags.Contains(tag))
                    {
                        results.Add(entry);
                        break;
                    }
                }
            }
            return results;
        }

        /// <summary>按插件搜索。</summary>
        public List<PluginAssetEntry> GetAssetsByPlugin(string pluginId)
        {
            var results = new List<PluginAssetEntry>();
            lock (_mountLock)
            {
                foreach (var mp in _mountPoints)
                {
                    if (mp.PluginId == pluginId)
                        results.AddRange(mp.Entries);
                }
            }
            return results;
        }

        /// <summary>获取所有资产。</summary>
        public List<PluginAssetEntry> GetAllAssets()
        {
            var results = new List<PluginAssetEntry>();
            foreach (var kv in _pathMap)
            {
                if (kv.Value.Count > 0)
                    results.Add(kv.Value[0]); // 每个路径取最高优先级
            }
            return results;
        }

        private List<PluginAssetEntry> GetAllAssetsMatching(string pattern)
        {
            var results = new List<PluginAssetEntry>();
            string normalizedPattern = NormalizePath(pattern);

            foreach (var kv in _pathMap)
            {
                if (MatchPattern(kv.Key, normalizedPattern))
                {
                    results.AddRange(kv.Value);
                }
            }
            return results;
        }

        private bool MatchPattern(string path, string pattern)
        {
            if (pattern.EndsWith("/*"))
                return path.StartsWith(pattern.Substring(0, pattern.Length - 1));
            if (pattern == "*" || pattern == "")
                return true;
            return path == pattern;
        }

        #endregion

        #region 资产版本

        /// <summary>获取资产版本。</summary>
        public SemanticVersion GetAssetVersion(string virtualPath)
        {
            var entry = ResolveAsset(virtualPath);
            return entry?.Version;
        }

        /// <summary>检查资产版本兼容性。</summary>
        public bool CheckAssetCompatibility(string virtualPath, VersionRange requiredRange)
        {
            var version = GetAssetVersion(virtualPath);
            return version != null && requiredRange.Matches(version);
        }

        #endregion

        #region 资产依赖追踪

        /// <summary>记录资产访问。</summary>
        private void TrackDependency(string assetPath, string providerPluginId)
        {
            _dependencyTracker.AddOrUpdate(
                assetPath,
                new AssetDependencyRecord
                {
                    AssetPath = assetPath,
                    ProviderPluginId = providerPluginId,
                    AccessTime = DateTime.Now,
                    AccessCount = 1
                },
                (key, record) =>
                {
                    record.AccessTime = DateTime.Now;
                    record.AccessCount++;
                    return record;
                });
        }

        /// <summary>记录资产依赖 (插件A 依赖插件B的资产)。</summary>
        public void RegisterDependency(string assetPath, string dependentPluginId, string providerPluginId)
        {
            _dependencyTracker[assetPath] = new AssetDependencyRecord
            {
                AssetPath = assetPath,
                DependentPluginId = dependentPluginId,
                ProviderPluginId = providerPluginId,
                AccessTime = DateTime.Now,
                AccessCount = 1
            };
        }

        /// <summary>检查插件是否被其他插件依赖。</summary>
        public List<string> GetDependents(string pluginId)
        {
            return _dependencyTracker.Values
                .Where(r => r.ProviderPluginId == pluginId && !string.IsNullOrEmpty(r.DependentPluginId))
                .Select(r => r.DependentPluginId)
                .Distinct()
                .ToList();
        }

        /// <summary>获取插件提供的资产被依赖情况。</summary>
        public List<AssetDependencyRecord> GetDependencies(string pluginId)
        {
            return _dependencyTracker.Values
                .Where(r => r.ProviderPluginId == pluginId)
                .ToList();
        }

        /// <summary>检查是否可以安全删除插件 (无其他插件依赖其资产)。</summary>
        public bool CanSafelyRemovePlugin(string pluginId)
        {
            return !GetDependents(pluginId).Any();
        }

        #endregion

        #region 缓存管理

        private void TrimCacheIfNeeded()
        {
            while (_currentCacheSize > MaxCacheSize && _assetCache.Count > 0)
            {
                var oldest = _assetCache.OrderBy(kv => 0).FirstOrDefault();
                if (_assetCache.TryRemove(oldest.Key, out var data))
                    _currentCacheSize -= data.Length;
                else
                    break;
            }
        }

        /// <summary>清除缓存。</summary>
        public void ClearCache()
        {
            _assetCache.Clear();
            _textCache.Clear();
            _currentCacheSize = 0;
        }

        /// <summary>清除插件缓存。</summary>
        public void ClearCacheForPlugin(string pluginId)
        {
            var entries = GetAssetsByPlugin(pluginId);
            foreach (var entry in entries)
            {
                _assetCache.TryRemove(entry.VirtualPath, out _);
                _textCache.TryRemove(entry.VirtualPath, out _);
            }
        }

        #endregion

        #region 工具方法

        private static string NormalizePath(string path)
        {
            return path.Replace('\\', '/').TrimStart('/');
        }

        private static PluginAssetType DetectAssetType(string filePath)
        {
            string ext = Path.GetExtension(filePath).ToLowerInvariant();
            return ext switch
            {
                ".png" or ".jpg" or ".jpeg" or ".tga" or ".bmp" or ".hdr" or ".dds" => PluginAssetType.Texture,
                ".mesh" or ".fbx" or ".obj" or ".gltf" or ".glb" or ".usd" => PluginAssetType.Mesh,
                ".mat" or ".material" => PluginAssetType.Material,
                ".shader" or ".hlsl" or ".glsl" => PluginAssetType.Shader,
                ".wav" or ".mp3" or ".ogg" or ".flac" => PluginAssetType.Audio,
                ".anim" or ".clip" => PluginAssetType.Animation,
                ".cs" or ".lua" => PluginAssetType.Script,
                ".json" or ".xml" or ".ini" or ".cfg" => PluginAssetType.Config,
                ".lang" or ".loc" => PluginAssetType.Localization,
                ".prefab" => PluginAssetType.Prefab,
                ".scene" => PluginAssetType.Scene,
                ".ttf" or ".otf" => PluginAssetType.Font,
                ".csv" or ".dat" => PluginAssetType.Data,
                _ => PluginAssetType.Other
            };
        }

        /// <summary>获取统计信息。</summary>
        public string GetStats()
        {
            int totalAssets = _pathMap.Sum(kv => kv.Value.Count);
            int uniquePaths = _pathMap.Count;
            int overridden = _pathMap.Count(kv => kv.Value.Count > 1);
            long totalSize = _pathMap.Sum(kv => kv.Value.Sum(e => e.FileSize));

            return $"资产统计: {totalAssets} 总资产, {uniquePaths} 唯一路径, {overridden} 被覆盖, " +
                   $"缓存: {_currentCacheSize / 1024}KB / {MaxCacheSize / 1024}KB, " +
                   $"大小: {totalSize / 1024 / 1024}MB";
        }

        #endregion
    }

    #endregion
}
