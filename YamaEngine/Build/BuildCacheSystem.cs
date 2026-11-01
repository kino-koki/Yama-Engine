using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace YamaEngine.Build
{
    // ════════════════════════════════════════════════════════════════════════
    // yama引擎构建缓存系统 (BuildCacheSystem)
    //
    // 基于内容寻址的构建缓存，避免重复处理未变更的资产。
    // 支持缓存命中/未命中检测、LRU清理、按大小/时间清理、
    // 分布式缓存（可选）与缓存统计分析。
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 单个构建缓存条目。
    /// </summary>
    public sealed class BuildCacheEntry
    {
        /// <summary>缓存键（资产路径+配置签名哈希）。</summary>
        public string CacheKey { get; set; } = string.Empty;

        /// <summary>源文件内容哈希 (SHA256)。</summary>
        public string ContentHash { get; set; } = string.Empty;

        /// <summary>源文件路径。</summary>
        public string SourcePath { get; set; } = string.Empty;

        /// <summary>缓存输出路径。</summary>
        public string OutputPath { get; set; } = string.Empty;

        /// <summary>输出文件大小 (字节)。</summary>
        public long OutputSizeBytes { get; set; }

        /// <summary>缓存创建时间 (UTC)。</summary>
        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

        /// <summary>最后访问时间 (UTC)。</summary>
        public DateTime LastAccessTimeUtc { get; set; } = DateTime.UtcNow;

        /// <summary>构建平台。</summary>
        public string Platform { get; set; } = string.Empty;

        /// <summary>构建配置签名。</summary>
        public string ConfigSignature { get; set; } = string.Empty;
    }

    /// <summary>
    /// 缓存统计信息。
    /// </summary>
    public sealed class BuildCacheStatistics
    {
        /// <summary>缓存命中次数。</summary>
        public long HitCount { get; set; }

        /// <summary>缓存未命中次数。</summary>
        public long MissCount { get; set; }

        /// <summary>命中率 (0.0 ~ 1.0)。</summary>
        public double HitRate => (HitCount + MissCount) > 0 ? (double)HitCount / (HitCount + MissCount) : 0.0;

        /// <summary>缓存总条目数。</summary>
        public int TotalEntries { get; set; }

        /// <summary>缓存总大小 (字节)。</summary>
        public long TotalCacheSizeBytes { get; set; }

        /// <summary>预估节省的构建时间 (秒)。</summary>
        public double EstimatedTimeSavedSeconds { get; set; }

        /// <summary>平均每次命中节省时间 (秒)。</summary>
        public double AverageHitTimeSavedSeconds => HitCount > 0 ? EstimatedTimeSavedSeconds / HitCount : 0.0;
    }

    /// <summary>
    /// 缓存清理策略。
    /// </summary>
    public enum CacheCleanupPolicy
    {
        /// <summary>LRU：清理最久未访问的缓存。</summary>
        LRU,
        /// <summary>按大小：清理最大的缓存条目直到低于阈值。</summary>
        BySize,
        /// <summary>按时间：清理超过指定天数的缓存。</summary>
        ByAge,
        /// <summary>手动指定要清理的条目。</summary>
        Manual
    }

    /// <summary>
    /// 构建缓存系统。基于内容寻址，支持缓存命中检测与多种清理策略。
    /// </summary>
    public sealed class BuildCacheSystem : IDisposable
    {
        private readonly string _cacheRootDirectory;
        private readonly Dictionary<string, BuildCacheEntry> _entries = new();
        private readonly object _lock = new();
        private readonly string _indexFilePath;
        private bool _disposed;

        private long _hitCount;
        private long _missCount;
        private double _totalTimeSavedSeconds;

        public BuildCacheSystem(string? cacheDirectory = null)
        {
            _cacheRootDirectory = cacheDirectory ?? Path.Combine(Path.GetTempPath(), "YamaBuildCache");
            Directory.CreateDirectory(_cacheRootDirectory);
            _indexFilePath = Path.Combine(_cacheRootDirectory, "cache_index.json");
            LoadIndex();
        }

        /// <summary>获取缓存统计。</summary>
        public BuildCacheStatistics GetStatistics()
        {
            lock (_lock)
            {
                return new BuildCacheStatistics
                {
                    HitCount = _hitCount,
                    MissCount = _missCount,
                    TotalEntries = _entries.Count,
                    TotalCacheSizeBytes = _entries.Values.Sum(e => e.OutputSizeBytes),
                    EstimatedTimeSavedSeconds = _totalTimeSavedSeconds
                };
            }
        }

        /// <summary>
        /// 生成缓存键。
        /// </summary>
        public static string GenerateCacheKey(string assetPath, string platform, string configSignature)
        {
            var data = $"{assetPath}|{platform}|{configSignature}";
            using var sha = SHA256.Create();
            var hashBytes = sha.ComputeHash(Encoding.UTF8.GetBytes(data));
            return Convert.ToHexString(hashBytes).ToLowerInvariant().Substring(0, 32);
        }

        /// <summary>
        /// 生成配置签名。
        /// </summary>
        public static string GenerateConfigSignature(IDictionary<string, object> configParams)
        {
            var sorted = configParams.OrderBy(kv => kv.Key);
            var data = string.Join("&", sorted.Select(kv => $"{kv.Key}={kv.Value}"));
            using var sha = SHA256.Create();
            var hashBytes = sha.ComputeHash(Encoding.UTF8.GetBytes(data));
            return Convert.ToHexString(hashBytes).ToLowerInvariant().Substring(0, 16);
        }

        /// <summary>
        /// 查询缓存：检查指定资产是否有缓存命中。
        /// </summary>
        public BuildCacheEntry? Lookup(string cacheKey, string expectedContentHash)
        {
            lock (_lock)
            {
                if (_entries.TryGetValue(cacheKey, out var entry))
                {
                    // 验证内容哈希
                    if (entry.ContentHash == expectedContentHash && File.Exists(entry.OutputPath))
                    {
                        entry.LastAccessTimeUtc = DateTime.UtcNow;
                        Interlocked.Increment(ref _hitCount);
                        _totalTimeSavedSeconds += EstimateCookTime(entry.OutputPath);
                        return entry;
                    }
                }
                Interlocked.Increment(ref _missCount);
                return null;
            }
        }

        /// <summary>
        /// 写入缓存条目。
        /// </summary>
        public void Store(BuildCacheEntry entry)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));

            lock (_lock)
            {
                _entries[entry.CacheKey] = entry;
            }

            // 如果输出文件存在，将其复制到缓存目录
            if (File.Exists(entry.OutputPath))
            {
                var cachedFilePath = Path.Combine(_cacheRootDirectory, entry.CacheKey);
                Directory.CreateDirectory(Path.GetDirectoryName(cachedFilePath)!);
                File.Copy(entry.OutputPath, cachedFilePath, true);
                entry.OutputSizeBytes = new FileInfo(cachedFilePath).Length;
                entry.OutputPath = cachedFilePath;
            }

            SaveIndex();
        }

        /// <summary>
        /// 使指定缓存条目失效。
        /// </summary>
        public void Invalidate(string cacheKey)
        {
            lock (_lock)
            {
                if (_entries.TryGetValue(cacheKey, out var entry))
                {
                    if (File.Exists(entry.OutputPath))
                    {
                        try { File.Delete(entry.OutputPath); } catch { }
                    }
                    _entries.Remove(cacheKey);
                }
            }
            SaveIndex();
        }

        /// <summary>
        /// 清除所有缓存。
        /// </summary>
        public void ClearAll()
        {
            lock (_lock)
            {
                _entries.Clear();
                _hitCount = 0;
                _missCount = 0;
                _totalTimeSavedSeconds = 0;
            }

            try
            {
                foreach (var file in Directory.EnumerateFiles(_cacheRootDirectory, "*.*", SearchOption.AllDirectories))
                {
                    try { File.Delete(file); } catch { }
                }
            }
            catch { }

            SaveIndex();
        }

        /// <summary>
        /// 按策略清理缓存。
        /// </summary>
        public int Cleanup(CacheCleanupPolicy policy, long maxCacheSizeBytes = 0, int maxAgeDays = 30, int maxEntriesToRemove = 100)
        {
            lock (_lock)
            {
                List<string> toRemove;

                switch (policy)
                {
                    case CacheCleanupPolicy.LRU:
                        toRemove = _entries.Values
                            .OrderBy(e => e.LastAccessTimeUtc)
                            .Take(maxEntriesToRemove)
                            .Select(e => e.CacheKey)
                            .ToList();
                        break;

                    case CacheCleanupPolicy.BySize:
                        var currentSize = _entries.Values.Sum(e => e.OutputSizeBytes);
                        toRemove = new List<string>();
                        foreach (var entry in _entries.Values.OrderByDescending(e => e.OutputSizeBytes))
                        {
                            if (currentSize <= maxCacheSizeBytes) break;
                            toRemove.Add(entry.CacheKey);
                            currentSize -= entry.OutputSizeBytes;
                        }
                        break;

                    case CacheCleanupPolicy.ByAge:
                        var cutoff = DateTime.UtcNow.AddDays(-maxAgeDays);
                        toRemove = _entries.Values
                            .Where(e => e.CreatedAtUtc < cutoff)
                            .Select(e => e.CacheKey)
                            .ToList();
                        break;

                    case CacheCleanupPolicy.Manual:
                        return 0; // Manual requires explicit keys

                    default:
                        return 0;
                }

                foreach (var key in toRemove)
                {
                    if (_entries.TryGetValue(key, out var entry))
                    {
                        try { if (File.Exists(entry.OutputPath)) File.Delete(entry.OutputPath); } catch { }
                        _entries.Remove(key);
                    }
                }

                SaveIndex();
                return toRemove.Count;
            }
        }

        /// <summary>
        /// 导出缓存为可分发的存档（用于分布式缓存）。
        /// </summary>
        public string ExportCacheManifest()
        {
            var manifest = _entries.Values.Select(e => new
            {
                e.CacheKey,
                e.ContentHash,
                e.SourcePath,
                e.Platform,
                e.ConfigSignature,
                e.OutputSizeBytes,
                e.CreatedAtUtc
            }).ToList();

            return JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
        }

        /// <summary>
        /// 从远程清单预热缓存信息（不实际传输文件，仅注册元数据）。
        /// </summary>
        public void ImportCacheManifest(string jsonManifest)
        {
            try
            {
                var entries = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(jsonManifest);
                if (entries == null) return;

                foreach (var item in entries)
                {
                    var entry = new BuildCacheEntry
                    {
                        CacheKey = GetString(item, "cacheKey"),
                        ContentHash = GetString(item, "contentHash"),
                        SourcePath = GetString(item, "sourcePath"),
                        Platform = GetString(item, "platform"),
                        ConfigSignature = GetString(item, "configSignature"),
                        OutputSizeBytes = GetLong(item, "outputSizeBytes")
                    };

                    // 不覆盖本地已有条目
                    if (!_entries.ContainsKey(entry.CacheKey))
                    {
                        _entries[entry.CacheKey] = entry;
                    }
                }

                SaveIndex();
            }
            catch { }
        }

        // ── 内部方法 ──

        private void LoadIndex()
        {
            if (!File.Exists(_indexFilePath)) return;
            try
            {
                var json = File.ReadAllText(_indexFilePath, Encoding.UTF8);
                var entries = JsonSerializer.Deserialize<List<BuildCacheEntry>>(
                    json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (entries != null)
                {
                    foreach (var entry in entries)
                    {
                        _entries[entry.CacheKey] = entry;
                    }
                }
            }
            catch { }
        }

        private void SaveIndex()
        {
            lock (_lock)
            {
                try
                {
                    var json = JsonSerializer.Serialize(_entries.Values.ToList(),
                        new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(_indexFilePath, json, Encoding.UTF8);
                }
                catch { }
            }
        }

        private static double EstimateCookTime(string outputPath)
        {
            // 简化：根据输出文件大小估算 Cook 时间
            if (!File.Exists(outputPath)) return 0.5;
            var sizeBytes = new FileInfo(outputPath).Length;
            // 假设平均 Cook 速度 50MB/s
            return Math.Max(0.1, sizeBytes / (50.0 * 1024 * 1024));
        }

        private static string GetString(Dictionary<string, JsonElement> dict, string key)
        {
            return dict.TryGetValue(key, out var el) ? el.GetString() ?? "" : "";
        }

        private static long GetLong(Dictionary<string, JsonElement> dict, string key)
        {
            return dict.TryGetValue(key, out var el) ? el.GetInt64() : 0;
        }

        public void Dispose()
        {
            if (_disposed) return;
            SaveIndex();
            _disposed = true;
        }
    }
}
