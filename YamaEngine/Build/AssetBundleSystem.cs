using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace YamaEngine.Build
{
    // ════════════════════════════════════════════════════════════════════════
    // yama引擎资源包系统 (AssetBundleSystem)
    //
    // 类似 Unity AssetBundle，提供资产分组打包、压缩、依赖解析、
    // 版本管理、热更新差异比对与内存引用计数管理能力。
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Bundle 压缩算法。
    /// </summary>
    public enum BundleCompression
    {
        /// <summary>不压缩。</summary>
        None,
        /// <summary>LZ4 快速压缩（适合运行时加载）。</summary>
        LZ4,
        /// <summary>ZSTD 高压缩率（适合网络传输/存储）。</summary>
        ZSTD
    }

    /// <summary>
    /// 打包规则类型。决定资产如何分组到 Bundle。
    /// </summary>
    public enum BundleRuleType
    {
        /// <summary>按目录分组：同一目录下的资产打包到一个 Bundle。</summary>
        ByDirectory,
        /// <summary>按标签分组：具有相同标签的资产打包到一个 Bundle。</summary>
        ByTag,
        /// <summary>按类型分组：相同类型（纹理/网格/音频等）的资产打包到一个 Bundle。</summary>
        ByType,
        /// <summary>手动指定：用户显式指定资产到 Bundle 的映射。</summary>
        Manual,
        /// <summary>每个资产一个 Bundle（最细粒度）。</summary>
        PerAsset,
        /// <summary>全部合并为一个 Bundle（最粗粒度）。</summary>
        SingleBundle
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Bundle 构建条目与规则
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 资产到 Bundle 的映射条目。
    /// </summary>
    public sealed class BundleBuildEntry
    {
        /// <summary>资产源路径。</summary>
        public string AssetPath { get; set; } = string.Empty;

        /// <summary>目标 Bundle 名称（不含扩展名）。</summary>
        public string BundleName { get; set; } = string.Empty;

        /// <summary>资产在 Bundle 内的别名（可选，默认使用文件名）。</summary>
        public string? AssetAlias { get; set; }

        /// <summary>资产标签（用于按标签规则分组）。</summary>
        public List<string> Tags { get; set; } = new();

        /// <summary>资产类型标识。</summary>
        public string AssetType { get; set; } = "Unknown";

        /// <summary>是否为依赖资产（非主动引用）。</summary>
        public bool IsDependency { get; set; }
    }

    /// <summary>
    /// Bundle 打包规则。
    /// </summary>
    public sealed class BundleBuildRule
    {
        /// <summary>规则类型。</summary>
        public BundleRuleType RuleType { get; set; } = BundleRuleType.ByDirectory;

        /// <summary>规则筛选条件（如目录路径/标签名称/类型名称）。</summary>
        public string? Filter { get; set; }

        /// <summary>手动映射条目列表（Manual 规则时使用）。</summary>
        public List<BundleBuildEntry>? ManualEntries { get; set; }

        /// <summary>Bundle 大小上限 (字节)，超出则拆分。</summary>
        public long MaxBundleSizeBytes { get; set; } = 64 * 1024 * 1024; // 64MB

        /// <summary>是否启用依赖收集（自动将依赖资产打入同一 Bundle）。</summary>
        public bool CollectDependencies { get; set; } = true;

        /// <summary>压缩算法。</summary>
        public BundleCompression Compression { get; set; } = BundleCompression.LZ4;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Bundle 清单
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 单个 Bundle 的清单条目。
    /// </summary>
    public sealed class BundleManifestEntry
    {
        /// <summary>Bundle 名称。</summary>
        public string BundleName { get; set; } = string.Empty;

        /// <summary>Bundle 文件哈希 (SHA256)。</summary>
        public string Hash { get; set; } = string.Empty;

        /// <summary>Bundle 文件大小 (字节)。</summary>
        public long SizeBytes { get; set; }

        /// <summary>Bundle 压缩算法。</summary>
        public BundleCompression Compression { get; set; }

        /// <summary>包含的资产路径列表。</summary>
        public List<string> AssetPaths { get; set; } = new();

        /// <summary>依赖的 Bundle 名称列表。</summary>
        public List<string> Dependencies { get; set; } = new();

        /// <summary>Bundle CRC32 校验值。</summary>
        public uint Crc32 { get; set; }
    }

    /// <summary>
    /// 资产 Bundle 总清单。记录所有 Bundle 的元信息与依赖关系。
    /// </summary>
    public sealed class AssetBundleManifest
    {
        /// <summary>清单版本号。</summary>
        public int Version { get; set; } = 1;

        /// <summary>应用版本号。</summary>
        public string AppVersion { get; set; } = "1.0.0";

        /// <summary>生成时间 (UTC)。</summary>
        public DateTime GeneratedAtUtc { get; set; } = DateTime.UtcNow;

        /// <summary>Bundle 总数量。</summary>
        public int BundleCount => Bundles.Count;

        /// <summary>所有 Bundle 总大小 (字节)。</summary>
        public long TotalSizeBytes => Bundles.Sum(b => b.SizeBytes);

        /// <summary>Bundle 清单条目列表。</summary>
        public List<BundleManifestEntry> Bundles { get; set; } = new();

        /// <summary>全局 CRC32 表（用于完整性校验）。</summary>
        public Dictionary<string, uint> Crc32Table { get; set; } = new();

        /// <summary>序列化为 JSON。</summary>
        public string ToJson()
        {
            return JsonSerializer.Serialize(this, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
        }

        /// <summary>从 JSON 反序列化。</summary>
        public static AssetBundleManifest FromJson(string json)
        {
            return JsonSerializer.Deserialize<AssetBundleManifest>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? throw new ArgumentException("Invalid manifest JSON.");
        }

        /// <summary>保存到文件。</summary>
        public void Save(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, ToJson(), Encoding.UTF8);
        }

        /// <summary>从文件加载。</summary>
        public static AssetBundleManifest Load(string path)
        {
            var json = File.ReadAllText(path, Encoding.UTF8);
            return FromJson(json);
        }

        /// <summary>获取指定 Bundle 的依赖链（递归展开所有依赖）。</summary>
        public List<string> GetDependencyChain(string bundleName)
        {
            var result = new HashSet<string>();
            CollectDependenciesRecursive(bundleName, result);
            return result.ToList();
        }

        private void CollectDependenciesRecursive(string bundleName, HashSet<string> visited)
        {
            if (!visited.Add(bundleName)) return;
            var entry = Bundles.FirstOrDefault(b => b.BundleName == bundleName);
            if (entry == null) return;
            foreach (var dep in entry.Dependencies)
            {
                CollectDependenciesRecursive(dep, visited);
            }
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Bundle 加载状态
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Bundle 加载状态。
    /// </summary>
    public enum BundleLoadState
    {
        /// <summary>未加载。</summary>
        Unloaded,
        /// <summary>正在加载中。</summary>
        Loading,
        /// <summary>已加载到内存。</summary>
        Loaded,
        /// <summary>正在卸载中。</summary>
        Unloading,
        /// <summary>加载失败。</summary>
        LoadFailed
    }

    /// <summary>
    /// 已加载的 Bundle 实例。管理 Bundle 的内存生命周期与引用计数。
    /// </summary>
    public sealed class LoadedBundle
    {
        /// <summary>Bundle 名称。</summary>
        public string BundleName { get; set; } = string.Empty;

        /// <summary>Bundle 文件路径。</summary>
        public string FilePath { get; set; } = string.Empty;

        /// <summary>加载状态。</summary>
        public BundleLoadState State { get; set; } = BundleLoadState.Unloaded;

        /// <summary>引用计数。</summary>
        public int RefCount { get; set; }

        /// <summary>Bundle 数据（内存中的字节）。</summary>
        public byte[]? Data { get; set; }

        /// <summary>加载耗时 (毫秒)。</summary>
        public double LoadDurationMs { get; set; }

        /// <summary>已加载的资产数量。</summary>
        public int LoadedAssetCount { get; set; }

        /// <summary>加载错误信息。</summary>
        public string? Error { get; set; }

        /// <summary>最后访问时间。</summary>
        public DateTime LastAccessTime { get; set; } = DateTime.UtcNow;

        /// <summary>增加引用计数。</summary>
        public void AddRef()
        {
            RefCount++;
            LastAccessTime = DateTime.UtcNow;
        }

        /// <summary>减少引用计数。返回 true 表示引用计数归零可卸载。</summary>
        public bool Release()
        {
            RefCount--;
            if (RefCount <= 0)
            {
                RefCount = 0;
                State = BundleLoadState.Unloaded;
                Data = null;
                return true;
            }
            return false;
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Bundle 构建器
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Bundle 构建结果。
    /// </summary>
    public sealed class BundleBuildResult
    {
        /// <summary>构建是否成功。</summary>
        public bool Success { get; set; }

        /// <summary>输出目录。</summary>
        public string OutputDirectory { get; set; } = string.Empty;

        /// <summary>生成的清单。</summary>
        public AssetBundleManifest? Manifest { get; set; }

        /// <summary>总耗时 (毫秒)。</summary>
        public double TotalDurationMs { get; set; }

        /// <summary>构建的 Bundle 总数。</summary>
        public int BundleCount { get; set; }

        /// <summary>总输出大小 (字节)。</summary>
        public long TotalOutputSizeBytes { get; set; }

        /// <summary>警告列表。</summary>
        public List<string> Warnings { get; set; } = new();

        /// <summary>错误列表。</summary>
        public List<string> Errors { get; set; } = new();
    }

    /// <summary>
    /// 资产 Bundle 构建器。将资产按规则分组、打包、压缩并生成清单。
    /// </summary>
    public sealed class AssetBundleBuilder
    {
        private readonly List<BundleBuildRule> _rules = new();
        private readonly List<BundleBuildEntry> _manualEntries = new();

        /// <summary>添加打包规则。</summary>
        public void AddRule(BundleBuildRule rule)
        {
            _rules.Add(rule ?? throw new ArgumentNullException(nameof(rule)));
        }

        /// <summary>添加手动映射条目。</summary>
        public void AddManualEntry(BundleBuildEntry entry)
        {
            _manualEntries.Add(entry ?? throw new ArgumentNullException(nameof(entry)));
        }

        /// <summary>
        /// 执行 Bundle 构建。
        /// </summary>
        public BundleBuildResult Build(string assetRoot, string outputDir, string? version = null)
        {
            var sw = Stopwatch.StartNew();
            var result = new BundleBuildResult
            {
                OutputDirectory = outputDir
            };

            Directory.CreateDirectory(outputDir);

            try
            {
                // 1. 收集资产
                var allEntries = CollectAllAssets(assetRoot);

                // 2. 合并手动映射
                allEntries.AddRange(_manualEntries);

                // 3. 应用规则分组
                var bundles = ApplyRules(allEntries);

                // 4. 构建 Bundle 文件
                var manifest = new AssetBundleManifest
                {
                    AppVersion = version ?? "1.0.0"
                };

                foreach (var (bundleName, entries) in bundles)
                {
                    var rule = _rules.FirstOrDefault(r =>
                        r.RuleType == BundleRuleType.Manual && r.ManualEntries != null &&
                        r.ManualEntries.Any(e => e.BundleName == bundleName)) ?? _rules.FirstOrDefault();

                    var compression = rule?.Compression ?? BundleCompression.LZ4;
                    var bundlePath = Path.Combine(outputDir, bundleName + ".shbundle");
                    var buildResult = BuildSingleBundle(bundlePath, entries, compression);

                    if (buildResult.Success)
                    {
                        var hash = ComputeHash(bundlePath);
                        var size = new FileInfo(bundlePath).Length;

                        manifest.Bundles.Add(new BundleManifestEntry
                        {
                            BundleName = bundleName,
                            Hash = hash,
                            SizeBytes = size,
                            Compression = compression,
                            AssetPaths = entries.Select(e => e.AssetPath).ToList(),
                            Dependencies = new List<string>(),
                            Crc32 = ComputeCrc32(bundlePath)
                        });

                        result.BundleCount++;
                        result.TotalOutputSizeBytes += size;
                    }
                    else
                    {
                        result.Errors.Add($"Failed to build bundle '{bundleName}': {buildResult.Error}");
                    }
                }

                // 5. 解析依赖关系
                ResolveDependencies(manifest);

                // 6. 生成 CRC32 表
                foreach (var bundle in manifest.Bundles)
                {
                    manifest.Crc32Table[bundle.BundleName] = bundle.Crc32;
                }

                // 7. 保存清单
                var manifestPath = Path.Combine(outputDir, "bundle_manifest.json");
                manifest.Save(manifestPath);
                result.Manifest = manifest;

                result.Success = result.Errors.Count == 0;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Errors.Add($"Bundle build error: {ex.Message}");
            }
            finally
            {
                sw.Stop();
                result.TotalDurationMs = sw.Elapsed.TotalMilliseconds;
            }

            return result;
        }

        /// <summary>
        /// 收集指定目录下的所有资产。
        /// </summary>
        private List<BundleBuildEntry> CollectAllAssets(string assetRoot)
        {
            var entries = new List<BundleBuildEntry>();
            if (!Directory.Exists(assetRoot)) return entries;

            foreach (var file in Directory.EnumerateFiles(assetRoot, "*.*", SearchOption.AllDirectories))
            {
                var ext = Path.GetExtension(file).ToLowerInvariant();
                if (IsIgnoredExtension(ext)) continue;

                var relativePath = Path.GetRelativePath(assetRoot, file).Replace('\\', '/');
                var dir = Path.GetDirectoryName(relativePath)?.Replace('\\', '/') ?? "";

                entries.Add(new BundleBuildEntry
                {
                    AssetPath = relativePath,
                    BundleName = dir.Replace('/', '_'),
                    AssetType = ext.TrimStart('.').ToUpperInvariant()
                });
            }

            return entries;
        }

        private static readonly string[] IgnoredExtensions = { ".meta", ".shmeta", ".tmp", ".bak" };
        private static bool IsIgnoredExtension(string ext) => IgnoredExtensions.Contains(ext);

        /// <summary>
        /// 根据规则将资产分组到 Bundle。
        /// </summary>
        private Dictionary<string, List<BundleBuildEntry>> ApplyRules(List<BundleBuildEntry> entries)
        {
            var rule = _rules.FirstOrDefault() ?? new BundleBuildRule { RuleType = BundleRuleType.ByDirectory };
            var bundles = new Dictionary<string, List<BundleBuildEntry>>();

            foreach (var entry in entries)
            {
                string bundleName;
                switch (rule.RuleType)
                {
                    case BundleRuleType.ByDirectory:
                        var dir = Path.GetDirectoryName(entry.AssetPath)?.Replace('/', '_') ?? "root";
                        bundleName = string.IsNullOrWhiteSpace(dir) ? "root" : dir;
                        break;

                    case BundleRuleType.ByTag:
                        bundleName = entry.Tags.Count > 0 ? string.Join("_", entry.Tags) : "untagged";
                        break;

                    case BundleRuleType.ByType:
                        bundleName = entry.AssetType;
                        break;

                    case BundleRuleType.Manual:
                        bundleName = !string.IsNullOrWhiteSpace(entry.BundleName) ? entry.BundleName : "misc";
                        break;

                    case BundleRuleType.PerAsset:
                        bundleName = Path.GetFileNameWithoutExtension(entry.AssetPath);
                        break;

                    case BundleRuleType.SingleBundle:
                        bundleName = "all_assets";
                        break;

                    default:
                        bundleName = "misc";
                        break;
                }

                if (!bundles.ContainsKey(bundleName))
                    bundles[bundleName] = new List<BundleBuildEntry>();
                bundles[bundleName].Add(entry);
            }

            return bundles;
        }

        /// <summary>
        /// 构建单个 Bundle 文件。
        /// </summary>
        private (bool Success, string? Error) BuildSingleBundle(string path, List<BundleBuildEntry> entries, BundleCompression compression)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);

                // Bundle 文件格式:
                // [Header: magic(4) + version(4) + compression(4) + entryCount(4) + crc32(4)]
                // [Entry Table: offset(8) + size(8) + pathLen(4) + path(variable)]
                // [Data: raw/compressed asset data blocks]
                using var fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);

                // 占位 Header (20 bytes)
                var header = new byte[20];
                fs.Write(header, 0, 20);

                var entryTableStart = 20L;
                var entryTableSize = entries.Count * 20; // offset(8) + size(8) + pathLen(4)
                foreach (var e in entries)
                {
                    entryTableSize += 4 + Encoding.UTF8.GetByteCount(e.AssetPath);
                }

                // 占位 Entry Table
                fs.Seek(entryTableStart + entryTableSize, SeekOrigin.Begin);
                var dataStartOffset = entryTableStart + entryTableSize;

                var offsets = new List<(long Offset, long Size, string Path)>();
                foreach (var entry in entries)
                {
                    var assetSource = Path.Combine(Path.GetDirectoryName(path)!.Replace("Bundles", "Assets") ?? "", entry.AssetPath.Replace('/', Path.DirectorySeparatorChar));
                    var offset = fs.Position - dataStartOffset;
                    long size;

                    if (File.Exists(assetSource))
                    {
                        var data = File.ReadAllBytes(assetSource);
                        size = data.Length;

                        // 简化压缩处理：实际项目中使用 LZ4/ZSTD 库
                        fs.Write(data, 0, data.Length);
                    }
                    else
                    {
                        size = 0;
                    }

                    offsets.Add((offset, size, entry.AssetPath));
                }

                // 写入 Entry Table
                fs.Seek(entryTableStart, SeekOrigin.Begin);
                foreach (var (offset, size, assetPath) in offsets)
                {
                    var offsetBytes = BitConverter.GetBytes(offset);
                    var sizeBytes = BitConverter.GetBytes(size);
                    var pathBytes = Encoding.UTF8.GetBytes(assetPath);
                    var pathLenBytes = BitConverter.GetBytes(pathBytes.Length);

                    fs.Write(offsetBytes, 0, 8);
                    fs.Write(sizeBytes, 0, 8);
                    fs.Write(pathLenBytes, 0, 4);
                    fs.Write(pathBytes, 0, pathBytes.Length);
                }

                // 写入 Header
                fs.Seek(0, SeekOrigin.Begin);
                var magic = Encoding.UTF8.GetBytes("SHBN"); // Yama Bundle
                var version = BitConverter.GetBytes(1);
                var comp = BitConverter.GetBytes((int)compression);
                var entryCount = BitConverter.GetBytes(entries.Count);
                var crc = BitConverter.GetBytes(ComputeCrc32(path));

                fs.Write(magic, 0, 4);
                fs.Write(version, 0, 4);
                fs.Write(comp, 0, 4);
                fs.Write(entryCount, 0, 4);
                fs.Write(crc, 0, 4);

                return (true, null);
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        /// <summary>
        /// 解析 Bundle 之间的依赖关系。
        /// </summary>
        private void ResolveDependencies(AssetBundleManifest manifest)
        {
            // 构建资产到 Bundle 的映射
            var assetToBundle = new Dictionary<string, string>();
            foreach (var bundle in manifest.Bundles)
            {
                foreach (var asset in bundle.AssetPaths)
                {
                    assetToBundle[asset] = bundle.BundleName;
                }
            }

            // 简化依赖分析：基于共享资产目录推断依赖
            foreach (var bundle in manifest.Bundles)
            {
                var deps = new HashSet<string>();
                foreach (var asset in bundle.AssetPaths)
                {
                    // 同目录下的其他 Bundle 视为潜在依赖
                    var dir = Path.GetDirectoryName(asset);
                    if (dir == null) continue;
                    foreach (var otherBundle in manifest.Bundles)
                    {
                        if (otherBundle.BundleName == bundle.BundleName) continue;
                        if (otherBundle.AssetPaths.Any(a => Path.GetDirectoryName(a) == dir))
                        {
                            deps.Add(otherBundle.BundleName);
                        }
                    }
                }
                bundle.Dependencies = deps.ToList();
            }
        }

        private static string ComputeHash(string filePath)
        {
            using var sha = SHA256.Create();
            using var stream = File.OpenRead(filePath);
            var hashBytes = sha.ComputeHash(stream);
            return Convert.ToHexString(hashBytes).ToLowerInvariant();
        }

        private static uint ComputeCrc32(string filePath)
        {
            // 简化 CRC32 实现
            var bytes = File.ReadAllBytes(filePath);
            uint crc = 0xFFFFFFFF;
            foreach (var b in bytes)
            {
                crc ^= b;
                for (var i = 0; i < 8; i++)
                {
                    crc = (crc >> 1) ^ (crc & 1) * 0xEDB88320;
                }
            }
            return ~crc;
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Bundle 加载器
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Bundle 加载器。支持同步/异步加载、依赖解析与引用计数管理。
    /// </summary>
    public sealed class AssetBundleLoader
    {
        private readonly string _bundleRootPath;
        private readonly Dictionary<string, LoadedBundle> _loadedBundles = new();
        private readonly object _lock = new();
        private AssetBundleManifest? _manifest;

        /// <summary>当前加载的 Bundle 数量。</summary>
        public int LoadedCount
        {
            get { lock (_lock) { return _loadedBundles.Count; } }
        }

        /// <summary>已加载 Bundle 总内存大小 (字节)。</summary>
        public long TotalLoadedMemoryBytes
        {
            get
            {
                lock (_lock)
                {
                    return _loadedBundles.Values.Sum(b => b.Data?.Length ?? 0);
                }
            }
        }

        public AssetBundleLoader(string bundleRootPath)
        {
            _bundleRootPath = bundleRootPath;
        }

        /// <summary>加载清单。</summary>
        public void LoadManifest()
        {
            var manifestPath = Path.Combine(_bundleRootPath, "bundle_manifest.json");
            if (File.Exists(manifestPath))
            {
                _manifest = AssetBundleManifest.Load(manifestPath);
            }
        }

        /// <summary>
        /// 同步加载 Bundle（自动加载依赖）。
        /// </summary>
        public LoadedBundle? LoadBundle(string bundleName)
        {
            lock (_lock)
            {
                // 已加载则增加引用
                if (_loadedBundles.TryGetValue(bundleName, out var existing))
                {
                    existing.AddRef();
                    return existing;
                }
            }

            // 自动加载依赖
            if (_manifest != null)
            {
                var deps = _manifest.GetDependencyChain(bundleName);
                foreach (var dep in deps)
                {
                    LoadBundleInternal(dep);
                }
            }

            return LoadBundleInternal(bundleName);
        }

        private LoadedBundle? LoadBundleInternal(string bundleName)
        {
            lock (_lock)
            {
                if (_loadedBundles.TryGetValue(bundleName, out var existing))
                {
                    existing.AddRef();
                    return existing;
                }
            }

            var bundlePath = Path.Combine(_bundleRootPath, bundleName + ".shbundle");
            if (!File.Exists(bundlePath)) return null;

            var sw = Stopwatch.StartNew();
            var bundle = new LoadedBundle
            {
                BundleName = bundleName,
                FilePath = bundlePath,
                State = BundleLoadState.Loading
            };

            try
            {
                bundle.Data = File.ReadAllBytes(bundlePath);
                bundle.State = BundleLoadState.Loaded;
                bundle.LoadedAssetCount = 1; // 简化：实际应解析 Entry Table

                lock (_lock)
                {
                    _loadedBundles[bundleName] = bundle;
                }
            }
            catch (Exception ex)
            {
                bundle.State = BundleLoadState.LoadFailed;
                bundle.Error = ex.Message;
                return bundle;
            }
            finally
            {
                sw.Stop();
                bundle.LoadDurationMs = sw.Elapsed.TotalMilliseconds;
            }

            bundle.AddRef();
            return bundle;
        }

        /// <summary>
        /// 异步加载 Bundle。
        /// </summary>
        public async Task<LoadedBundle?> LoadBundleAsync(string bundleName)
        {
            return await Task.Run(() => LoadBundle(bundleName));
        }

        /// <summary>
        /// 从内存中的字节数据加载 Bundle（无需文件）。
        /// </summary>
        public LoadedBundle LoadBundleFromMemory(string bundleName, byte[] data)
        {
            var bundle = new LoadedBundle
            {
                BundleName = bundleName,
                State = BundleLoadState.Loaded,
                Data = data,
                LoadedAssetCount = 1
            };
            bundle.AddRef();

            lock (_lock)
            {
                _loadedBundles[bundleName] = bundle;
            }

            return bundle;
        }

        /// <summary>
        /// 释放 Bundle 引用。当引用计数归零时自动卸载。
        /// </summary>
        public void UnloadBundle(string bundleName, bool unloadAllLoadedAssets = false)
        {
            lock (_lock)
            {
                if (!_loadedBundles.TryGetValue(bundleName, out var bundle)) return;

                if (unloadAllLoadedAssets)
                {
                    bundle.RefCount = 0;
                }

                if (bundle.Release())
                {
                    _loadedBundles.Remove(bundleName);
                }
            }
        }

        /// <summary>
        /// 卸载所有 Bundle。
        /// </summary>
        public void UnloadAll()
        {
            lock (_lock)
            {
                foreach (var bundle in _loadedBundles.Values)
                {
                    bundle.Data = null;
                    bundle.State = BundleLoadState.Unloaded;
                    bundle.RefCount = 0;
                }
                _loadedBundles.Clear();
            }
        }

        /// <summary>
        /// 卸载引用计数为 0 的闲置 Bundle。
        /// </summary>
        public int UnloadUnused()
        {
            var unloaded = 0;
            lock (_lock)
            {
                var toRemove = _loadedBundles
                    .Where(kvp => kvp.Value.RefCount <= 0)
                    .Select(kvp => kvp.Key)
                    .ToList();

                foreach (var key in toRemove)
                {
                    _loadedBundles[key].Data = null;
                    _loadedBundles[key].State = BundleLoadState.Unloaded;
                    _loadedBundles.Remove(key);
                    unloaded++;
                }
            }
            return unloaded;
        }

        /// <summary>获取指定 Bundle 的加载状态。</summary>
        public BundleLoadState GetBundleState(string bundleName)
        {
            lock (_lock)
            {
                return _loadedBundles.TryGetValue(bundleName, out var b) ? b.State : BundleLoadState.Unloaded;
            }
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Bundle 版本管理
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Bundle 版本管理器。跟踪 Bundle 版本变更，支持热更新差异比对。
    /// </summary>
    public sealed class BundleVersionManager
    {
        private readonly string _versionRootPath;
        private readonly Dictionary<string, string> _versionHistory = new(); // bundleName -> currentVersion

        public BundleVersionManager(string versionRootPath)
        {
            _versionRootPath = versionRootPath;
            Directory.CreateDirectory(versionRootPath);
            LoadVersionHistory();
        }

        /// <summary>记录新版本的 Bundle。</summary>
        public void RecordVersion(string bundleName, string version, string hash, long size)
        {
            _versionHistory[bundleName] = version;

            var historyFile = Path.Combine(_versionRootPath, bundleName + ".version.json");
            var entry = new BundleVersionEntry
            {
                BundleName = bundleName,
                Version = version,
                Hash = hash,
                SizeBytes = size,
                Timestamp = DateTime.UtcNow
            };

            var entries = new List<BundleVersionEntry>();
            if (File.Exists(historyFile))
            {
                try
                {
                    var json = File.ReadAllText(historyFile, Encoding.UTF8);
                    entries = JsonSerializer.Deserialize<List<BundleVersionEntry>>(json) ?? entries;
                }
                catch { }
            }

            entries.Add(entry);

            // 保留最近 20 个版本记录
            if (entries.Count > 20)
                entries = entries.Skip(entries.Count - 20).ToList();

            var jsonOut = JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(historyFile, jsonOut, Encoding.UTF8);
        }

        /// <summary>对比两个版本的差异。</summary>
        public BundleVersionDiff CompareVersions(AssetBundleManifest oldManifest, AssetBundleManifest newManifest)
        {
            var diff = new BundleVersionDiff();

            var oldBundles = oldManifest.Bundles.ToDictionary(b => b.BundleName);
            var newBundles = newManifest.Bundles.ToDictionary(b => b.BundleName);

            // 新增 Bundle
            foreach (var name in newBundles.Keys.Except(oldBundles.Keys))
            {
                diff.AddedBundles.Add(name);
            }

            // 删除 Bundle
            foreach (var name in oldBundles.Keys.Except(newBundles.Keys))
            {
                diff.RemovedBundles.Add(name);
            }

            // 修改的 Bundle
            foreach (var name in oldBundles.Keys.Intersect(newBundles.Keys))
            {
                var oldEntry = oldBundles[name];
                var newEntry = newBundles[name];
                if (oldEntry.Hash != newEntry.Hash)
                {
                    diff.ModifiedBundles.Add(name);
                }
            }

            return diff;
        }

        private void LoadVersionHistory()
        {
            if (!Directory.Exists(_versionRootPath)) return;
            foreach (var file in Directory.EnumerateFiles(_versionRootPath, "*.version.json"))
            {
                try
                {
                    var json = File.ReadAllText(file, Encoding.UTF8);
                    var entries = JsonSerializer.Deserialize<List<BundleVersionEntry>>(json);
                    if (entries != null && entries.Count > 0)
                    {
                        var latest = entries[^1];
                        _versionHistory[latest.BundleName] = latest.Version;
                    }
                }
                catch { }
            }
        }

        private sealed class BundleVersionEntry
        {
            public string BundleName { get; set; } = string.Empty;
            public string Version { get; set; } = string.Empty;
            public string Hash { get; set; } = string.Empty;
            public long SizeBytes { get; set; }
            public DateTime Timestamp { get; set; }
        }
    }

    /// <summary>
    /// 两个版本之间的差异。
    /// </summary>
    public sealed class BundleVersionDiff
    {
        /// <summary>新增的 Bundle。</summary>
        public List<string> AddedBundles { get; set; } = new();

        /// <summary>删除的 Bundle。</summary>
        public List<string> RemovedBundles { get; set; } = new();

        /// <summary>内容变更的 Bundle。</summary>
        public List<string> ModifiedBundles { get; set; } = new();

        /// <summary>是否存在差异。</summary>
        public bool HasChanges => AddedBundles.Count > 0 || RemovedBundles.Count > 0 || ModifiedBundles.Count > 0;
    }
}
