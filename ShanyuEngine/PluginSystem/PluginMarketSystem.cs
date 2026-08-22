using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace ShanyuEngine.PluginSystem
{
    // ════════════════════════════════════════════════════════════════════════
    // 山雨引擎 - 插件市场系统 (PluginMarketSystem)
    //
    // 插件市场客户端 (类似 Unity Asset Store / Steam Workshop):
    //   * 市场源 (官方市场 / Steam创意工坊 / 自定义源 / 本地源)
    //   * 插件搜索 (关键字 / 标签 / 分类 / 排序 / 分页 / 筛选)
    //   * 插件详情 (名称 / 描述 / 截图 / 视频 / 评论 / 评分 / 下载量 / 版本历史)
    //   * 插件下载 (断点续传 / 校验 / 解压 / 安装 / 依赖自动安装)
    //   * 插件更新 (版本检查 / 自动更新 / 手动更新 / 更新通知 / 回滚)
    //   * 插件评分/评论 (评分 / 评论 / 举报 / 回复 / 审核)
    //   * 插件上传 (打包 / 上传 / 审核 / 发布 / 版本管理)
    //   * 市场缓存 (搜索缓存 / 详情缓存 / 图片缓存)
    // ════════════════════════════════════════════════════════════════════════

    #region 市场枚举

    /// <summary>市场源类型。</summary>
    public enum MarketSourceType
    {
        Official,
        SteamWorkshop,
        GOG,
        Custom,
        Local
    }

    /// <summary>搜索排序方式。</summary>
    public enum MarketSortMode
    {
        Relevance,
        Newest,
        Oldest,
        MostDownloaded,
        HighestRated,
        Name,
        Author,
        LastUpdated
    }

    /// <summary>下载状态。</summary>
    public enum DownloadState
    {
        Pending,
        Downloading,
        Paused,
        Completed,
        Failed,
        Cancelled,
        Installing,
        Installed
    }

    /// <summary>审核状态。</summary>
    public enum ReviewStatus
    {
        Pending,
        Approved,
        Rejected,
        ChangesRequested,
        UnderReview
    }

    #endregion

    #region 市场源

    /// <summary>市场源。</summary>
    public sealed class MarketSource
    {
        public string SourceId { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = string.Empty;
        public MarketSourceType Type { get; set; } = MarketSourceType.Official;
        public string BaseUrl { get; set; } = string.Empty;
        public string ApiKey { get; set; } = string.Empty;
        public bool IsEnabled { get; set; } = true;
        public bool RequiresAuthentication { get; set; } = false;
        public int Priority { get; set; } = 100;

        // 本地源路径
        public string LocalPath { get; set; } = string.Empty;

        public override string ToString() => $"{Name} ({Type}) [{(IsEnabled ? "Enabled" : "Disabled")}]";
    }

    #endregion

    #region 市场插件信息

    /// <summary>市场中的插件信息。</summary>
    public sealed class MarketPluginInfo
    {
        public string PluginId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Author { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string VersionStr { get; set; } = "1.0.0";
        public string Category { get; set; } = string.Empty;
        public List<string> Tags { get; set; } = new List<string>();
        public List<string> Screenshots { get; set; } = new List<string>();
        public List<string> Videos { get; set; } = new List<string>();
        public string IconUrl { get; set; } = string.Empty;
        public string DownloadUrl { get; set; } = string.Empty;
        public long DownloadSize { get; set; }
        public long DownloadCount { get; set; }
        public double Rating { get; set; }
        public int RatingCount { get; set; }
        public DateTime CreatedDate { get; set; }
        public DateTime UpdatedDate { get; set; }
        public string License { get; set; } = string.Empty;
        public string Homepage { get; set; } = string.Empty;
        public string SourceId { get; set; } = string.Empty;
        public List<string> Dependencies { get; set; } = new List<string>();
        public List<PluginVersionHistory> VersionHistory { get; set; } = new List<PluginVersionHistory>();
        public string Changelog { get; set; } = string.Empty;
        public ReviewStatus ReviewStatus { get; set; } = ReviewStatus.Approved;
        public string EngineCompatVersion { get; set; } = "*";

        [JsonIgnore]
        public SemanticVersion Version => SemanticVersion.TryParse(VersionStr, out var v) ? v : new SemanticVersion(1, 0, 0);

        public override string ToString() => $"{Name} v{VersionStr} by {Author} (★{Rating:F1})";
    }

    /// <summary>版本历史记录。</summary>
    public sealed class PluginVersionHistory
    {
        public string Version { get; set; }
        public DateTime ReleaseDate { get; set; }
        public string Changelog { get; set; }
        public long DownloadCount { get; set; }
        public string DownloadUrl { get; set; }

        public override string ToString() => $"v{Version} ({ReleaseDate:yyyy-MM-dd})";
    }

    #endregion

    #region 搜索请求与结果

    /// <summary>搜索请求。</summary>
    public sealed class MarketSearchRequest
    {
        public string Query { get; set; } = string.Empty;
        public List<string> Tags { get; set; } = new List<string>();
        public string Category { get; set; }
        public string Author { get; set; }
        public MarketSortMode SortMode { get; set; } = MarketSortMode.Relevance;
        public int Page { get; set; } = 1;
        public int PageSize { get; set; } = 20;
        public double MinRating { get; set; } = 0;
        public string EngineCompatVersion { get; set; }
        public List<string> SourceIds { get; set; } = new List<string>();

        public override string ToString() =>
            $"Query='{Query}' Cat={Category} Sort={SortMode} Page={Page}/{PageSize}";
    }

    /// <summary>搜索结果。</summary>
    public sealed class MarketSearchResult
    {
        public List<MarketPluginInfo> Plugins { get; set; } = new List<MarketPluginInfo>();
        public int TotalCount { get; set; }
        public int Page { get; set; }
        public int PageSize { get; set; }
        public int TotalPages => PageSize > 0 ? (int)Math.Ceiling((double)TotalCount / PageSize) : 0;
        public TimeSpan SearchTime { get; set; }
        public string CacheKey { get; set; }

        public bool HasMore => Page < TotalPages;

        public override string ToString() =>
            $"{Plugins.Count} / {TotalCount} 结果 (Page {Page}/{TotalPages}, {SearchTime.TotalMilliseconds:F0}ms)";
    }

    #endregion

    #region 下载任务

    /// <summary>下载任务。</summary>
    public sealed class DownloadTask
    {
        public string TaskId { get; set; } = Guid.NewGuid().ToString("N");
        public string PluginId { get; set; }
        public string PluginName { get; set; }
        public string DownloadUrl { get; set; }
        public string LocalPath { get; set; }
        public long TotalBytes { get; set; }
        public long DownloadedBytes { get; set; }
        public DownloadState State { get; set; } = DownloadState.Pending;
        public DateTime StartTime { get; set; }
        public DateTime? CompleteTime { get; set; }
        public string ErrorMessage { get; set; }
        public string Checksum { get; set; }
        public string SourceId { get; set; }

        [JsonIgnore]
        public double Progress => TotalBytes > 0 ? (double)DownloadedBytes / TotalBytes * 100 : 0;

        [JsonIgnore]
        public double SpeedBytesPerSec { get; set; }

        [JsonIgnore]
        public TimeSpan? EstimatedTimeRemaining =>
            SpeedBytesPerSec > 0 && DownloadedBytes < TotalBytes
                ? TimeSpan.FromSeconds((TotalBytes - DownloadedBytes) / SpeedBytesPerSec)
                : (TimeSpan?)null;

        public override string ToString() =>
            $"{PluginName}: {Progress:F1}% ({DownloadedBytes / 1024 / 1024:F1}MB / {TotalBytes / 1024 / 1024:F1}MB) [{State}]";
    }

    #endregion

    #region 评论与评分

    /// <summary>插件评论。</summary>
    public sealed class PluginReview
    {
        public string ReviewId { get; set; } = Guid.NewGuid().ToString("N");
        public string PluginId { get; set; }
        public string Author { get; set; }
        public string AuthorId { get; set; }
        public double Rating { get; set; }
        public string Title { get; set; }
        public string Content { get; set; }
        public DateTime CreatedDate { get; set; } = DateTime.Now;
        public DateTime? EditedDate { get; set; }
        public int HelpfulCount { get; set; }
        public int ReportCount { get; set; }
        public ReviewStatus Status { get; set; } = ReviewStatus.Approved;
        public List<PluginReview> Replies { get; set; } = new List<PluginReview>();
        public string ParentReviewId { get; set; }

        public override string ToString() => $"★{Rating:F1} {Title} by {Author}";
    }

    #endregion

    #region 市场缓存

    /// <summary>市场缓存项。</summary>
    internal sealed class MarketCacheEntry<T>
    {
        public T Data { get; set; }
        public DateTime CachedTime { get; set; } = DateTime.Now;
        public TimeSpan Expiry { get; set; } = TimeSpan.FromMinutes(30);

        public bool IsExpired => DateTime.Now - CachedTime > Expiry;
    }

    #endregion

    #region 插件市场

    /// <summary>插件市场客户端。</summary>
    public sealed class PluginMarket
    {
        private static readonly Lazy<PluginMarket> _instance =
            new Lazy<PluginMarket>(() => new PluginMarket());
        public static PluginMarket Instance => _instance.Value;

        // 市场源
        private readonly List<MarketSource> _sources = new List<MarketSource>();
        private readonly object _sourceLock = new object();

        // 下载任务
        private readonly ConcurrentDictionary<string, DownloadTask> _downloads = new ConcurrentDictionary<string, DownloadTask>();

        // 评论缓存
        private readonly ConcurrentDictionary<string, List<PluginReview>> _reviews = new ConcurrentDictionary<string, List<PluginReview>>();

        // 缓存
        private readonly ConcurrentDictionary<string, MarketCacheEntry<MarketSearchResult>> _searchCache = new ConcurrentDictionary<string, MarketCacheEntry<MarketSearchResult>>();
        private readonly ConcurrentDictionary<string, MarketCacheEntry<MarketPluginInfo>> _detailCache = new ConcurrentDictionary<string, MarketCacheEntry<MarketPluginInfo>>();
        private readonly ConcurrentDictionary<string, byte[]> _imageCache = new ConcurrentDictionary<string, byte[]>();

        // HTTP 客户端
        private readonly HttpClient _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        // 日志
        private readonly PluginLogger _logger = new PluginLogger("PluginMarket");

        // 配置
        public string DownloadDirectory { get; set; } = Path.Combine(AppContext.BaseDirectory, "Downloads", "Market");
        public string CacheDirectory { get; set; } = Path.Combine(Path.GetTempPath(), "ShanyuEngine", "MarketCache");
        public bool EnableCache { get; set; } = true;
        public TimeSpan CacheExpiry { get; set; } = TimeSpan.FromMinutes(30);
        public bool AutoInstallDependencies { get; set; } = true;
        public bool AutoUpdate { get; set; } = false;
        public int MaxConcurrentDownloads { get; set; } = 3;

        // 事件
        public event Action<DownloadTask> OnDownloadStarted;
        public event Action<DownloadTask> OnDownloadProgress;
        public event Action<DownloadTask> OnDownloadCompleted;
        public event Action<DownloadTask, string> OnDownloadFailed;
        public event Action<MarketPluginInfo> OnUpdateAvailable;
        public event Action<MarketSearchResult> OnSearchCompleted;
        public event Action<PluginReview> OnReviewSubmitted;

        // 统计
        public int TotalDownloads { get; private set; }
        public int CompletedDownloads { get; private set; }
        public int FailedDownloads { get; private set; }

        private PluginMarket()
        {
            if (!Directory.Exists(DownloadDirectory))
                Directory.CreateDirectory(DownloadDirectory);
            if (!Directory.Exists(CacheDirectory))
                Directory.CreateDirectory(CacheDirectory);

            // 添加默认本地源
            AddSource(new MarketSource
            {
                Name = "本地市场",
                Type = MarketSourceType.Local,
                LocalPath = Path.Combine(AppContext.BaseDirectory, "LocalMarket"),
                IsEnabled = true,
                Priority = 0
            });
        }

        #region 市场源管理

        /// <summary>添加市场源。</summary>
        public void AddSource(MarketSource source)
        {
            if (source == null) return;
            lock (_sourceLock)
            {
                if (!_sources.Any(s => s.SourceId == source.SourceId))
                {
                    _sources.Add(source);
                    _sources.Sort((a, b) => a.Priority.CompareTo(b.Priority));
                    _logger.Info($"添加市场源: {source}");
                }
            }
        }

        /// <summary>移除市场源。</summary>
        public bool RemoveSource(string sourceId)
        {
            lock (_sourceLock)
            {
                var source = _sources.FirstOrDefault(s => s.SourceId == sourceId);
                if (source != null)
                {
                    _sources.Remove(source);
                    _logger.Info($"移除市场源: {source}");
                    return true;
                }
            }
            return false;
        }

        /// <summary>获取所有市场源。</summary>
        public IReadOnlyList<MarketSource> GetSources()
        {
            lock (_sourceLock) return _sources.ToList().AsReadOnly();
        }

        /// <summary>获取启用的市场源。</summary>
        public List<MarketSource> GetEnabledSources()
        {
            lock (_sourceLock) return _sources.Where(s => s.IsEnabled).ToList();
        }

        #endregion

        #region 搜索

        /// <summary>搜索插件。</summary>
        public async Task<MarketSearchResult> SearchAsync(MarketSearchRequest request)
        {
            if (request == null) request = new MarketSearchRequest();

            // 检查缓存
            string cacheKey = GenerateCacheKey(request);
            if (EnableCache && _searchCache.TryGetValue(cacheKey, out var cached) && !cached.IsExpired)
            {
                _logger.Debug($"搜索缓存命中: {cacheKey}");
                cached.Data.CacheKey = cacheKey;
                OnSearchCompleted?.Invoke(cached.Data);
                return cached.Data;
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = new MarketSearchResult
            {
                Page = request.Page,
                PageSize = request.PageSize
            };

            var sources = GetEnabledSources();
            if (request.SourceIds != null && request.SourceIds.Count > 0)
                sources = sources.Where(s => request.SourceIds.Contains(s.SourceId)).ToList();

            // 从各源搜索
            foreach (var source in sources)
            {
                try
                {
                    var sourceResults = await SearchFromSource(source, request);
                    result.Plugins.AddRange(sourceResults);
                }
                catch (Exception ex)
                {
                    _logger.Warning($"从源 {source.Name} 搜索失败: {ex.Message}");
                }
            }

            // 排序
            result.Plugins = SortPlugins(result.Plugins, request.SortMode);

            // 分页
            result.TotalCount = result.Plugins.Count;
            result.Plugins = result.Plugins
                .Skip((request.Page - 1) * request.PageSize)
                .Take(request.PageSize)
                .ToList();

            sw.Stop();
            result.SearchTime = sw.Elapsed;
            result.CacheKey = cacheKey;

            // 缓存
            if (EnableCache)
            {
                _searchCache[cacheKey] = new MarketCacheEntry<MarketSearchResult>
                {
                    Data = result,
                    Expiry = CacheExpiry
                };
            }

            _logger.Info($"搜索完成: {result}");
            OnSearchCompleted?.Invoke(result);
            return result;
        }

        private async Task<List<MarketPluginInfo>> SearchFromSource(MarketSource source, MarketSearchRequest request)
        {
            // 本地源
            if (source.Type == MarketSourceType.Local)
            {
                return SearchLocalSource(source, request);
            }

            // 远程源 (占位实现)
            await Task.Delay(0);

            // 实际实现需要调用远程 API
            return new List<MarketPluginInfo>();
        }

        private List<MarketPluginInfo> SearchLocalSource(MarketSource source, MarketSearchRequest request)
        {
            var results = new List<MarketPluginInfo>();
            if (!Directory.Exists(source.LocalPath)) return results;

            var manifestFiles = Directory.GetFiles(source.LocalPath, "plugin.json", SearchOption.AllDirectories);
            foreach (var file in manifestFiles)
            {
                try
                {
                    if (PluginDescriptor.TryLoadFromFile(file, out var desc))
                    {
                        var info = new MarketPluginInfo
                        {
                            PluginId = desc.PluginId,
                            Name = desc.Info.Name,
                            Author = desc.Info.Author,
                            Description = desc.Info.Description,
                            VersionStr = desc.Info.VersionStr,
                            Tags = desc.Info.Tags,
                            Category = desc.Info.Tags.FirstOrDefault() ?? "General",
                            License = desc.Info.License,
                            Homepage = desc.Info.Website,
                            SourceId = source.SourceId,
                            CreatedDate = DateTime.Now,
                            UpdatedDate = DateTime.Now,
                            DownloadUrl = file,
                            DownloadSize = new FileInfo(file).Length,
                            EngineCompatVersion = desc.Info.EngineCompatVersion
                        };

                        // 过滤
                        if (!string.IsNullOrEmpty(request.Query) &&
                            !info.Name.Contains(request.Query, StringComparison.OrdinalIgnoreCase) &&
                            !info.Description.Contains(request.Query, StringComparison.OrdinalIgnoreCase))
                            continue;

                        if (!string.IsNullOrEmpty(request.Category) &&
                            !info.Category.Equals(request.Category, StringComparison.OrdinalIgnoreCase))
                            continue;

                        if (!string.IsNullOrEmpty(request.Author) &&
                            !info.Author.Contains(request.Author, StringComparison.OrdinalIgnoreCase))
                            continue;

                        if (request.Tags.Count > 0 &&
                            !request.Tags.All(t => info.Tags.Contains(t)))
                            continue;

                        if (info.Rating < request.MinRating)
                            continue;

                        results.Add(info);
                    }
                }
                catch { }
            }

            return results;
        }

        private List<MarketPluginInfo> SortPlugins(List<MarketPluginInfo> plugins, MarketSortMode mode)
        {
            return mode switch
            {
                MarketSortMode.Newest => plugins.OrderByDescending(p => p.CreatedDate).ToList(),
                MarketSortMode.Oldest => plugins.OrderBy(p => p.CreatedDate).ToList(),
                MarketSortMode.MostDownloaded => plugins.OrderByDescending(p => p.DownloadCount).ToList(),
                MarketSortMode.HighestRated => plugins.OrderByDescending(p => p.Rating).ToList(),
                MarketSortMode.Name => plugins.OrderBy(p => p.Name).ToList(),
                MarketSortMode.Author => plugins.OrderBy(p => p.Author).ToList(),
                MarketSortMode.LastUpdated => plugins.OrderByDescending(p => p.UpdatedDate).ToList(),
                _ => plugins.ToList()
            };
        }

        #endregion

        #region 插件详情

        /// <summary>获取插件详情。</summary>
        public async Task<MarketPluginInfo> GetPluginDetailsAsync(string pluginId)
        {
            // 检查缓存
            if (EnableCache && _detailCache.TryGetValue(pluginId, out var cached) && !cached.IsExpired)
            {
                _logger.Debug($"详情缓存命中: {pluginId}");
                return cached.Data;
            }

            // 从各源查找
            foreach (var source in GetEnabledSources())
            {
                try
                {
                    var info = await GetDetailsFromSource(source, pluginId);
                    if (info != null)
                    {
                        if (EnableCache)
                        {
                            _detailCache[pluginId] = new MarketCacheEntry<MarketPluginInfo>
                            {
                                Data = info,
                                Expiry = CacheExpiry
                            };
                        }
                        return info;
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warning($"从源 {source.Name} 获取详情失败: {ex.Message}");
                }
            }

            return null;
        }

        private async Task<MarketPluginInfo> GetDetailsFromSource(MarketSource source, string pluginId)
        {
            if (source.Type == MarketSourceType.Local)
            {
                // 搜索本地源
                var searchResult = SearchLocalSource(source, new MarketSearchRequest());
                return searchResult.FirstOrDefault(p => p.PluginId == pluginId);
            }

            await Task.Delay(0);
            return null;
        }

        #endregion

        #region 下载

        /// <summary>下载插件。</summary>
        public async Task<DownloadTask> DownloadAsync(string pluginId, string downloadUrl = null, long expectedSize = 0, string checksum = null)
        {
            var info = await GetPluginDetailsAsync(pluginId);
            if (info == null && string.IsNullOrEmpty(downloadUrl))
            {
                _logger.Error($"无法找到插件 {pluginId}");
                return null;
            }

            downloadUrl ??= info?.DownloadUrl;
            string fileName = $"{pluginId}_{info?.VersionStr ?? "latest"}.zip";
            string localPath = Path.Combine(DownloadDirectory, fileName);

            var task = new DownloadTask
            {
                PluginId = pluginId,
                PluginName = info?.Name ?? pluginId,
                DownloadUrl = downloadUrl,
                LocalPath = localPath,
                TotalBytes = expectedSize > 0 ? expectedSize : info?.DownloadSize ?? 0,
                Checksum = checksum,
                StartTime = DateTime.Now,
                State = DownloadState.Pending,
                SourceId = info?.SourceId
            };

            _downloads[task.TaskId] = task;
            TotalDownloads++;
            OnDownloadStarted?.Invoke(task);

            // 如果是本地文件
            if (File.Exists(downloadUrl))
            {
                task.State = DownloadState.Downloading;
                try
                {
                    File.Copy(downloadUrl, localPath, true);
                    var fileInfo = new FileInfo(localPath);
                    task.DownloadedBytes = fileInfo.Length;
                    task.TotalBytes = fileInfo.Length;
                    task.State = DownloadState.Completed;
                    task.CompleteTime = DateTime.Now;
                    CompletedDownloads++;
                    OnDownloadCompleted?.Invoke(task);

                    // 自动安装
                    await InstallDownloadedPlugin(task);
                }
                catch (Exception ex)
                {
                    task.State = DownloadState.Failed;
                    task.ErrorMessage = ex.Message;
                    FailedDownloads++;
                    OnDownloadFailed?.Invoke(task, ex.Message);
                }
            }
            else
            {
                // HTTP 下载 (简化实现)
                try
                {
                    task.State = DownloadState.Downloading;
                    using var response = await _httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead);
                    task.TotalBytes = response.Content.Headers.ContentLength ?? task.TotalBytes;

                    using var fileStream = new FileStream(localPath, FileMode.Create, FileAccess.Write);
                    using var stream = await response.Content.ReadAsStreamAsync();

                    byte[] buffer = new byte[81920];
                    int bytesRead;
                    var sw = System.Diagnostics.Stopwatch.StartNew();

                    while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                    {
                        await fileStream.WriteAsync(buffer, 0, bytesRead);
                        task.DownloadedBytes += bytesRead;

                        if (sw.Elapsed.TotalSeconds > 0)
                            task.SpeedBytesPerSec = task.DownloadedBytes / sw.Elapsed.TotalSeconds;

                        OnDownloadProgress?.Invoke(task);
                    }

                    task.State = DownloadState.Completed;
                    task.CompleteTime = DateTime.Now;
                    CompletedDownloads++;
                    OnDownloadCompleted?.Invoke(task);

                    // 自动安装
                    await InstallDownloadedPlugin(task);
                }
                catch (Exception ex)
                {
                    task.State = DownloadState.Failed;
                    task.ErrorMessage = ex.Message;
                    FailedDownloads++;
                    OnDownloadFailed?.Invoke(task, ex.Message);
                    _logger.Error($"下载失败: {pluginId}", ex);
                }
            }

            return task;
        }

        /// <summary>安装已下载的插件。</summary>
        private async Task InstallDownloadedPlugin(DownloadTask task)
        {
            if (task.State != DownloadState.Completed) return;
            if (!File.Exists(task.LocalPath)) return;

            task.State = DownloadState.Installing;

            try
            {
                // 校验
                if (!string.IsNullOrEmpty(task.Checksum))
                {
                    string actualChecksum = ComputeFileChecksum(task.LocalPath);
                    if (actualChecksum != task.Checksum)
                    {
                        _logger.Error($"校验和不匹配: {task.PluginId}");
                        task.State = DownloadState.Failed;
                        task.ErrorMessage = "校验和不匹配";
                        OnDownloadFailed?.Invoke(task, "校验和不匹配");
                        return;
                    }
                }

                // 安装 (委托给 PluginManager)
                bool installed = PluginManager.Instance != null
                    ? await Task.Run(() => InstallViaPluginManager(task))
                    : ModManager.Instance.InstallFromArchive(task.LocalPath);

                if (installed)
                {
                    task.State = DownloadState.Installed;
                    _logger.Info($"插件 {task.PluginId} 安装成功。");
                }
                else
                {
                    task.State = DownloadState.Failed;
                    task.ErrorMessage = "安装失败";
                }
            }
            catch (Exception ex)
            {
                task.State = DownloadState.Failed;
                task.ErrorMessage = ex.Message;
                _logger.Error($"安装失败: {task.PluginId}", ex);
            }
        }

        private bool InstallViaPluginManager(DownloadTask task)
        {
            // 尝试通过 ModManager 安装 (Mod 和 Plugin 共用安装逻辑)
            return ModManager.Instance.InstallFromArchive(task.LocalPath, true);
        }

        private string ComputeFileChecksum(string filePath)
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            using var stream = File.OpenRead(filePath);
            byte[] hash = sha.ComputeHash(stream);
            return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        }

        /// <summary>取消下载。</summary>
        public bool CancelDownload(string taskId)
        {
            if (_downloads.TryGetValue(taskId, out var task))
            {
                if (task.State == DownloadState.Downloading || task.State == DownloadState.Pending)
                {
                    task.State = DownloadState.Cancelled;
                    return true;
                }
            }
            return false;
        }

        /// <summary>获取所有下载任务。</summary>
        public IEnumerable<DownloadTask> GetAllDownloads() => _downloads.Values;

        /// <summary>获取活跃下载。</summary>
        public IEnumerable<DownloadTask> GetActiveDownloads() =>
            _downloads.Values.Where(t => t.State == DownloadState.Downloading || t.State == DownloadState.Pending);

        #endregion

        #region 更新

        /// <summary>检查更新。</summary>
        public async Task<List<MarketPluginInfo>> CheckUpdatesAsync()
        {
            var updates = new List<MarketPluginInfo>();

            // 检查已安装插件的更新
            foreach (var plugin in PluginManager.Instance.GetLoadedPlugins())
            {
                var marketInfo = await GetPluginDetailsAsync(plugin.Descriptor?.PluginId);
                if (marketInfo != null && marketInfo.Version > plugin.Info.Version)
                {
                    updates.Add(marketInfo);
                    OnUpdateAvailable?.Invoke(marketInfo);
                    _logger.Info($"发现更新: {plugin.Info.Name} {plugin.Info.Version} → {marketInfo.Version}");
                }
            }

            // 检查 Mod 的更新
            foreach (var mod in ModManager.Instance.GetActiveMods())
            {
                var marketInfo = await GetPluginDetailsAsync(mod.ModId);
                if (marketInfo != null && marketInfo.Version > mod.Info.Version)
                {
                    updates.Add(marketInfo);
                    OnUpdateAvailable?.Invoke(marketInfo);
                }
            }

            return updates;
        }

        /// <summary>更新插件。</summary>
        public async Task<bool> UpdatePluginAsync(string pluginId)
        {
            var info = await GetPluginDetailsAsync(pluginId);
            if (info == null)
            {
                _logger.Error($"无法找到插件 {pluginId} 的更新信息。");
                return false;
            }

            _logger.Info($"开始更新 {pluginId} → v{info.VersionStr}");
            var task = await DownloadAsync(pluginId, info.DownloadUrl, info.DownloadSize);
            return task?.State == DownloadState.Installed;
        }

        /// <summary>回滚到上一版本。</summary>
        public bool RollbackPlugin(string pluginId)
        {
            // 占位：实际实现需要保存历史版本
            _logger.Info($"回滚插件 {pluginId} (功能待实现)");
            return false;
        }

        #endregion

        #region 评论与评分

        /// <summary>获取插件评论。</summary>
        public List<PluginReview> GetReviews(string pluginId)
        {
            if (_reviews.TryGetValue(pluginId, out var list))
                return list.Where(r => r.Status == ReviewStatus.Approved).ToList();
            return new List<PluginReview>();
        }

        /// <summary>提交评论。</summary>
        public bool SubmitReview(PluginReview review)
        {
            if (review == null || string.IsNullOrEmpty(review.PluginId)) return false;

            var list = _reviews.GetOrAdd(review.PluginId, _ => new List<PluginReview>());
            list.Add(review);
            OnReviewSubmitted?.Invoke(review);
            _logger.Info($"评论提交: {review}");
            return true;
        }

        /// <summary>举报评论。</summary>
        public bool ReportReview(string reviewId, string reason)
        {
            foreach (var kv in _reviews)
            {
                var review = kv.Value.FirstOrDefault(r => r.ReviewId == reviewId);
                if (review != null)
                {
                    review.ReportCount++;
                    if (review.ReportCount >= 3)
                        review.Status = ReviewStatus.UnderReview;
                    _logger.Info($"评论举报: {reviewId} - {reason}");
                    return true;
                }
            }
            return false;
        }

        /// <summary>回复评论。</summary>
        public bool ReplyToReview(string parentReviewId, PluginReview reply)
        {
            foreach (var kv in _reviews)
            {
                var parent = kv.Value.FirstOrDefault(r => r.ReviewId == parentReviewId);
                if (parent != null)
                {
                    reply.ParentReviewId = parentReviewId;
                    parent.Replies.Add(reply);
                    return true;
                }
            }
            return false;
        }

        /// <summary>审核评论。</summary>
        public bool ModerateReview(string reviewId, ReviewStatus status)
        {
            foreach (var kv in _reviews)
            {
                var review = kv.Value.FirstOrDefault(r => r.ReviewId == reviewId);
                if (review != null)
                {
                    review.Status = status;
                    _logger.Info($"评论审核: {reviewId} → {status}");
                    return true;
                }
            }
            return false;
        }

        /// <summary>计算插件评分。</summary>
        public double CalculateRating(string pluginId)
        {
            if (!_reviews.TryGetValue(pluginId, out var list)) return 0;
            var approved = list.Where(r => r.Status == ReviewStatus.Approved).ToList();
            if (approved.Count == 0) return 0;
            return approved.Average(r => r.Rating);
        }

        #endregion

        #region 上传

        /// <summary>上传插件到市场。</summary>
        public async Task<bool> UploadPluginAsync(string pluginId, string archivePath, string changelog = null, ReviewStatus requestedStatus = ReviewStatus.Pending)
        {
            if (!File.Exists(archivePath))
            {
                _logger.Error($"文件不存在: {archivePath}");
                return false;
            }

            var info = await GetPluginDetailsAsync(pluginId);
            if (info == null)
            {
                _logger.Error($"无法找到插件 {pluginId} 的市场信息。");
                return false;
            }

            try
            {
                // 本地源：复制到本地市场目录
                var localSource = GetEnabledSources().FirstOrDefault(s => s.Type == MarketSourceType.Local);
                if (localSource != null)
                {
                    string targetDir = Path.Combine(localSource.LocalPath, pluginId);
                    Directory.CreateDirectory(targetDir);
                    string targetFile = Path.Combine(targetDir, Path.GetFileName(archivePath));
                    File.Copy(archivePath, targetFile, true);

                    // 更新版本历史
                    var versionEntry = new PluginVersionHistory
                    {
                        Version = info.VersionStr,
                        ReleaseDate = DateTime.Now,
                        Changelog = changelog,
                        DownloadUrl = targetFile
                    };
                    info.VersionHistory.Add(versionEntry);
                    info.UpdatedDate = DateTime.Now;
                    info.ReviewStatus = requestedStatus;

                    _logger.Info($"插件 {pluginId} 已上传到本地市场。");
                    return true;
                }

                // 远程源 (占位)
                await Task.Delay(0);
                _logger.Info($"远程上传功能待实现。");
                return false;
            }
            catch (Exception ex)
            {
                _logger.Error($"上传失败: {pluginId}", ex);
                return false;
            }
        }

        #endregion

        #region 缓存管理

        /// <summary>清除搜索缓存。</summary>
        public void ClearSearchCache()
        {
            _searchCache.Clear();
            _logger.Debug("搜索缓存已清除。");
        }

        /// <summary>清除详情缓存。</summary>
        public void ClearDetailCache()
        {
            _detailCache.Clear();
            _logger.Debug("详情缓存已清除。");
        }

        /// <summary>清除所有缓存。</summary>
        public void ClearAllCache()
        {
            _searchCache.Clear();
            _detailCache.Clear();
            _imageCache.Clear();
            _logger.Debug("所有市场缓存已清除。");
        }

        /// <summary>获取图片 (带缓存)。</summary>
        public async Task<byte[]> GetImageAsync(string imageUrl)
        {
            if (string.IsNullOrEmpty(imageUrl)) return null;

            if (_imageCache.TryGetValue(imageUrl, out var cached))
                return cached;

            if (File.Exists(imageUrl))
            {
                var data = File.ReadAllBytes(imageUrl);
                _imageCache[imageUrl] = data;
                return data;
            }

            try
            {
                var data = await _httpClient.GetByteArrayAsync(imageUrl);
                _imageCache[imageUrl] = data;
                return data;
            }
            catch
            {
                return null;
            }
        }

        #endregion

        #region 工具方法

        private string GenerateCacheKey(MarketSearchRequest request)
        {
            return $"{request.Query}_{string.Join(",", request.Tags)}_{request.Category}_{request.Author}_" +
                   $"{request.SortMode}_{request.Page}_{request.PageSize}_{request.MinRating}_" +
                   $"{string.Join(",", request.SourceIds ?? new List<string>())}";
        }

        /// <summary>获取统计信息。</summary>
        public string GetStats()
        {
            return $"市场统计: 源 {_sources.Count} 个, 下载 {TotalDownloads} (完成 {CompletedDownloads}, 失败 {FailedDownloads}), " +
                   $"缓存 搜索={_searchCache.Count} 详情={_detailCache.Count} 图片={_imageCache.Count}";
        }

        #endregion
    }

    #endregion
}
