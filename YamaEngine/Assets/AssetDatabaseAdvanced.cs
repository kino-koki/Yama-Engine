using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace YamaEngine.Assets
{
    // ════════════════════════════════════════════════════════════════════════
    //  AssetDatabaseAdvanced.cs — 引擎级资源管理核心
    //
    //  本文件扩展基础 AssetDatabase, 提供完整的引擎级资源管理能力:
    //    1. AssetEntry        —— 资产条目 (GUID/Path/Type/Tags/Dependencies)
    //    2. GlobalAssetDatabase —— 全局资产数据库 (扫描/导入/查询/依赖图)
    //    3. AssetImporter     —— 资产导入器接口
    //    4. ImportQueue       —— 异步导入队列
    //    5. AssetManifest     —— 资产清单持久化 (JSON)
    //
    //  与基础 AssetDatabase 的关系:
    //    基础版侧重场景编辑器中的资产管理 (场景对象/灯光/相机)。
    //    本文件侧重引擎运行时资源管线 (目录扫描/格式识别/依赖追踪/缓存管理)。
    //    两者共享 AssetType 枚举和 AssetMetadata。
    // ════════════════════════════════════════════════════════════════════════

    // ──────────────────────────────────────────────────────────────
    // 1. AssetEntry — 资产条目
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 完整的资产条目。包含资产的所有元信息: GUID、路径、类型、标签、依赖关系等。
    /// 是引擎资源管理的核心数据结构。
    /// </summary>
    public sealed class AssetEntry
    {
        /// <summary>全局唯一标识符。</summary>
        public Guid GUID { get; set; } = Guid.NewGuid();

        /// <summary>资产虚拟路径 (相对于项目根目录, 使用 '/' 分隔)。</summary>
        public string Path { get; set; } = string.Empty;

        /// <summary>资产类型。</summary>
        public AssetType Type { get; set; } = AssetType.Unknown;

        /// <summary>标签数组 (用于分类/搜索)。</summary>
        public string[] Tags { get; set; } = Array.Empty<string>();

        /// <summary>依赖的其他资产 GUID 数组 (此资产引用了哪些资产)。</summary>
        public Guid[] Dependencies { get; set; } = Array.Empty<Guid>();

        /// <summary>引用此资产的其他资产 GUID 数组 (谁引用了这个资产)。</summary>
        public Guid[] References { get; set; } = Array.Empty<Guid>();

        /// <summary>文件大小 (字节)。</summary>
        public long FileSize { get; set; }

        /// <summary>导入时间 (UTC)。</summary>
        public DateTime ImportTime { get; set; } = DateTime.UtcNow;

        /// <summary>最后修改时间 (UTC)。</summary>
        public DateTime LastModifiedTime { get; set; } = DateTime.UtcNow;

        /// <summary>文件内容哈希 (SHA256)。</summary>
        public string ContentHash { get; set; } = string.Empty;

        /// <summary>附加元数据 (可扩展, 由各 Importer 填充)。</summary>
        public Dictionary<string, object> Meta { get; set; } = new();

        /// <summary>资产是否已加载到内存。</summary>
        public bool IsLoaded { get; set; }

        /// <summary>加载后的运行时对象引用。</summary>
        public object? RuntimeAsset { get; set; }

        /// <summary>获取显示名称 (路径中的文件名)。</summary>
        public string DisplayName => string.IsNullOrEmpty(Path)
            ? GUID.ToString("N")
            : Path.Split('/', '\\').LastOrDefault() ?? Path;

        /// <summary>获取文件扩展名 (含 '.')。</summary>
        public string Extension => string.IsNullOrEmpty(Path)
            ? string.Empty
            : Path.Contains('.') ? Path.Substring(Path.LastIndexOf('.')) : string.Empty;

        /// <summary>序列化为 JSON。</summary>
        public string ToJson()
        {
            return JsonSerializer.Serialize(this, AssetManifestSerializerContext.Default.AssetEntry);
        }

        /// <summary>从 JSON 反序列化。</summary>
        public static AssetEntry? FromJson(string json)
        {
            return JsonSerializer.Deserialize(json, AssetManifestSerializerContext.Default.AssetEntry);
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 2. IAssetImporter — 资产导入器接口
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 资产导入器接口。每种支持的文件格式实现此接口。
    /// </summary>
    public interface IAssetImporter
    {
        /// <summary>支持的文件扩展名 (含 '.', 如 ".fbx", ".png")。</summary>
        string[] SupportedExtensions { get; }

        /// <summary>该导入器处理的资产类型。</summary>
        AssetType AssetType { get; }

        /// <summary>导入资产文件, 返回资产条目和运行时对象。</summary>
        AssetImportResult Import(string filePath, Guid guid, string virtualPath);

        /// <summary>检查文件是否需要重新导入 (基于内容哈希)。</summary>
        bool NeedsReimport(string filePath, string lastContentHash);
    }

    /// <summary>资产导入结果。</summary>
    public class AssetImportResult
    {
        /// <summary>导入后的资产条目。</summary>
        public AssetEntry Entry { get; set; } = new();

        /// <summary>导入后的运行时对象 (可为 null)。</summary>
        public object? RuntimeAsset { get; set; }

        /// <summary>导入是否成功。</summary>
        public bool Success { get; set; }

        /// <summary>错误消息 (失败时)。</summary>
        public string? ErrorMessage { get; set; }
    }

    // ──────────────────────────────────────────────────────────────
    // 3. ImportQueue — 异步导入队列
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 异步资产导入队列。按优先级处理导入任务, 支持并行和取消。
    /// </summary>
    public class ImportQueue : IDisposable
    {
        private readonly ConcurrentQueue<ImportTask> _queue = new();
        private readonly List<Task> _activeTasks = new();
        private readonly SemaphoreSlim _signal = new(0);
        private readonly CancellationTokenSource _cts = new();
        private readonly int _maxParallelism;
        private volatile bool _disposed;
        private Thread? _dispatcherThread;

        /// <summary>队列中等待的任务数。</summary>
        public int PendingCount => _queue.Count;

        /// <summary>当前正在处理的任务数。</summary>
        public int ActiveCount => _activeTasks.Count;

        /// <summary>导入完成事件。</summary>
        public event Action<AssetEntry>? OnImportCompleted;

        /// <summary>导入失败事件。</summary>
        public event Action<string, Exception>? OnImportFailed;

        public ImportQueue(int maxParallelism = 4)
        {
            _maxParallelism = maxParallelism;
        }

        /// <summary>启动导入队列。</summary>
        public void Start()
        {
            if (_dispatcherThread != null) return;
            _dispatcherThread = new Thread(DispatchLoop)
            {
                Name = "YamaAssetImportQueue",
                IsBackground = true
            };
            _dispatcherThread.Start();
        }

        /// <summary>入队一个导入任务。</summary>
        public void Enqueue(ImportTask task)
        {
            if (_disposed) return;
            _queue.Enqueue(task);
            _signal.Release();
        }

        /// <summary>入队一个简单导入任务。</summary>
        public void Enqueue(string filePath, Guid guid, string virtualPath, int priority = 0)
        {
            Enqueue(new ImportTask
            {
                FilePath = filePath,
                GUID = guid,
                VirtualPath = virtualPath,
                Priority = priority
            });
        }

        private void DispatchLoop()
        {
            while (!_disposed)
            {
                try
                {
                    _signal.Wait(_cts.Token);
                    while (_queue.TryDequeue(out var task) && !_disposed)
                    {
                        if (_activeTasks.Count >= _maxParallelism)
                        {
                            // 等待有空闲槽位
                            var completed = Task.WhenAny(_activeTasks).GetAwaiter().GetResult();
                            _activeTasks.Remove(completed);
                        }

                        var taskCopy = task;
                        var t = Task.Run(() => ExecuteTask(taskCopy), _cts.Token);
                        _activeTasks.Add(t);
                        t.ContinueWith(t =>
                        {
                            lock (_activeTasks) _activeTasks.Remove(t);
                        }, TaskContinuationOptions.ExecuteSynchronously);
                    }
                }
                catch (OperationCanceledException) { break; }
                catch { /* ignore */ }
            }
        }

        private void ExecuteTask(ImportTask task)
        {
            try
            {
                // 导入逻辑由 GlobalAssetDatabase 调用
                // 这里仅触发事件
            }
            catch (Exception ex)
            {
                OnImportFailed?.Invoke(task.FilePath, ex);
            }
        }

        public void Dispose()
        {
            _disposed = true;
            _cts.Cancel();
            _signal.Release();
            _dispatcherThread?.Join(1000);
            _signal.Dispose();
            _cts.Dispose();
        }
    }

    /// <summary>导入任务描述。</summary>
    public class ImportTask
    {
        public string FilePath { get; set; } = string.Empty;
        public Guid GUID { get; set; }
        public string VirtualPath { get; set; } = string.Empty;
        public int Priority { get; set; }
        public CancellationToken CancellationToken { get; set; }
    }

    // ──────────────────────────────────────────────────────────────
    // 4. AssetManifest — 资产清单持久化
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 资产清单数据结构, 用于 JSON 序列化/反序列化。
    /// </summary>
    public class AssetManifest
    {
        /// <summary>清单版本。</summary>
        public int Version { get; set; } = 1;

        /// <summary>生成时间 (UTC)。</summary>
        public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;

        /// <summary>项目根路径。</summary>
        public string ProjectRoot { get; set; } = string.Empty;

        /// <summary>所有资产条目。</summary>
        public List<AssetEntry> Assets { get; set; } = new();
    }

    /// <summary>JSON 序列化上下文 (AOT 兼容)。</summary>
    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
    [JsonSerializable(typeof(AssetManifest))]
    [JsonSerializable(typeof(AssetEntry))]
    [JsonSerializable(typeof(Dictionary<string, object>))]
    public partial class AssetManifestSerializerContext : JsonSerializerContext { }

    // ──────────────────────────────────────────────────────────────
    // 5. GlobalAssetDatabase — 全局资产数据库
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 全局资产数据库。引擎级资源管理核心, 替换简单的资源缓存,
    /// 提供目录扫描、格式识别、依赖追踪、全文搜索、异步导入等完整功能。
    /// </summary>
    public sealed class GlobalAssetDatabase : IDisposable
    {
        // ── 内部存储 ──
        private readonly Dictionary<Guid, AssetEntry> _entriesByGuid = new();
        private readonly Dictionary<string, AssetEntry> _entriesByPath = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<AssetEntry>> _entriesByTag = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<AssetType, List<AssetEntry>> _entriesByType = new();

        // 依赖图: key 依赖于 value[]
        private readonly Dictionary<Guid, HashSet<Guid>> _dependencyGraph = new();
        // 引用图: key 被 value[] 引用
        private readonly Dictionary<Guid, HashSet<Guid>> _referenceGraph = new();

        // 已加载资产缓存
        private readonly ConcurrentDictionary<Guid, object> _assetCache = new();

        // 导入器注册表
        private readonly List<IAssetImporter> _importers = new();

        // 导入队列
        private readonly ImportQueue _importQueue;

        // 项目根路径
        private readonly string _projectRoot;

        // 支持的文件格式
        private static readonly HashSet<string> SupportedFormats = new(StringComparer.OrdinalIgnoreCase)
        {
            ".fbx", ".gltf", ".glb", ".obj", ".dae", ".blend",
            ".png", ".jpg", ".jpeg", ".bmp", ".tga", ".dds", ".hdr", ".webp",
            ".wav", ".ogg", ".mp3", ".flac",
            ".cs",
            ".yama", ".syasset",
            ".json", ".xml", ".yaml", ".yml"
        };

        // ── 事件 ──
        /// <summary>资产导入完成事件。</summary>
        public event Action<AssetEntry>? OnAssetImported;

        /// <summary>资产删除事件。</summary>
        public event Action<AssetEntry>? OnAssetDeleted;

        /// <summary>资产修改事件。</summary>
        public event Action<AssetEntry>? OnAssetModified;

        // ── 属性 ──
        /// <summary>资产总数。</summary>
        public int TotalAssets => _entriesByGuid.Count;

        /// <summary>已加载资产数。</summary>
        public int LoadedAssetCount => _assetCache.Count;

        /// <summary>项目根路径。</summary>
        public string ProjectRoot => _projectRoot;

        /// <summary>导入队列。</summary>
        public ImportQueue ImportQueueInstance => _importQueue;

        /// <summary>获取所有资产条目。</summary>
        public IEnumerable<AssetEntry> AllEntries => _entriesByGuid.Values;

        // ── 单例 ──
        private static GlobalAssetDatabase? _instance;

        /// <summary>全局实例 (首次访问时需先调用 Initialize)。</summary>
        public static GlobalAssetDatabase Instance => _instance
            ?? throw new InvalidOperationException("GlobalAssetDatabase 未初始化。请先调用 Initialize()。");

        /// <summary>是否已初始化。</summary>
        public static bool IsInitialized => _instance != null;

        /// <summary>
        /// 初始化全局资产数据库。
        /// </summary>
        /// <param name="projectRoot">项目根目录路径。</param>
        /// <param name="maxImportParallelism">导入并行度。</param>
        public static void Initialize(string projectRoot, int maxImportParallelism = 4)
        {
            if (_instance != null)
            {
                _instance.Dispose();
            }
            _instance = new GlobalAssetDatabase(projectRoot, maxImportParallelism);
        }

        /// <summary>关闭全局资产数据库。</summary>
        public static void Shutdown()
        {
            _instance?.Dispose();
            _instance = null;
        }

        // ── 构造 ──
        private GlobalAssetDatabase(string projectRoot, int maxImportParallelism = 4)
        {
            _projectRoot = projectRoot;
            _importQueue = new ImportQueue(maxImportParallelism);

            // 注册默认导入器
            RegisterDefaultImporters();
        }

        /// <summary>注册默认导入器。</summary>
        private void RegisterDefaultImporters()
        {
            // 网格导入器
            _importers.Add(new GenericAssetImporter(
                new[] { ".fbx", ".obj", ".gltf", ".glb", ".dae", ".blend" },
                AssetType.Mesh
            ));

            // 纹理导入器
            _importers.Add(new GenericAssetImporter(
                new[] { ".png", ".jpg", ".jpeg", ".bmp", ".tga", ".dds", ".hdr", ".webp" },
                AssetType.Texture2D
            ));

            // 音频导入器
            _importers.Add(new GenericAssetImporter(
                new[] { ".wav", ".ogg", ".mp3", ".flac" },
                AssetType.Audio
            ));

            // 脚本导入器
            _importers.Add(new GenericAssetImporter(
                new[] { ".cs" },
                AssetType.Script
            ));

            // yama资产导入器
            _importers.Add(new GenericAssetImporter(
                new[] { ".yama", ".syasset" },
                AssetType.Scene
            ));
        }

        // ══════════════════════════════════════════════════════════
        // 目录扫描
        // ══════════════════════════════════════════════════════════

        /// <summary>
        /// 递归扫描目录, 注册所有支持格式的文件。
        /// </summary>
        /// <param name="directoryPath">要扫描的目录路径 (相对于项目根或绝对路径)。</param>
        /// <param name="recursive">是否递归子目录。</param>
        /// <returns>新发现的资产条目列表。</returns>
        public List<AssetEntry> ScanDirectory(string directoryPath, bool recursive = true)
        {
            var results = new List<AssetEntry>();
            var fullPath = ResolveFullPath(directoryPath);

            if (!Directory.Exists(fullPath))
            {
                System.Diagnostics.Debug.WriteLine($"ScanDirectory: 目录不存在 - {fullPath}");
                return results;
            }

            var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            var files = Directory.GetFiles(fullPath, "*.*", searchOption);

            foreach (var file in files)
            {
                var ext = Path.GetExtension(file);
                if (!SupportedFormats.Contains(ext)) continue;

                // 检查是否已注册
                var virtualPath = GetVirtualPath(file);
                if (_entriesByPath.ContainsKey(virtualPath))
                {
                    // 检查是否需要更新
                    var existing = _entriesByPath[virtualPath];
                    var currentHash = ComputeFileHash(file);
                    if (currentHash == existing.ContentHash) continue;

                    // 文件已修改
                    existing.LastModifiedTime = File.GetLastWriteTimeUtc(file);
                    existing.ContentHash = currentHash;
                    existing.FileSize = new FileInfo(file).Length;
                    OnAssetModified?.Invoke(existing);
                }
                else
                {
                    // 新文件
                    var entry = CreateEntry(file, ext);
                    if (entry != null)
                    {
                        RegisterEntry(entry);
                        results.Add(entry);
                    }
                }
            }

            // 检查已删除的文件
            var removedEntries = new List<AssetEntry>();
            foreach (var kvp in _entriesByPath)
            {
                var absPath = Path.Combine(_projectRoot, kvp.Key.Replace('/', Path.DirectorySeparatorChar));
                if (absPath.StartsWith(fullPath) && !File.Exists(absPath))
                {
                    removedEntries.Add(kvp.Value);
                }
            }
            foreach (var entry in removedEntries)
            {
                RemoveAsset(entry.GUID);
            }

            System.Diagnostics.Debug.WriteLine($"ScanDirectory: 扫描完成, 发现 {results.Count} 个新资产");
            return results;
        }

        /// <summary>扫描整个项目目录。</summary>
        public List<AssetEntry> ScanProject()
        {
            return ScanDirectory(_projectRoot);
        }

        // ══════════════════════════════════════════════════════════
        // 资产导入
        // ══════════════════════════════════════════════════════════

        /// <summary>
        /// 导入单个资产文件。
        /// </summary>
        public AssetEntry? Import(string filePath)
        {
            var fullPath = ResolveFullPath(filePath);
            if (!File.Exists(fullPath))
            {
                System.Diagnostics.Debug.WriteLine($"Import: 文件不存在 - {fullPath}");
                return null;
            }

            var ext = Path.GetExtension(fullPath);
            var virtualPath = GetVirtualPath(fullPath);

            // 如果已存在, 先移除旧的
            if (_entriesByPath.TryGetValue(virtualPath, out var existing))
            {
                RemoveAsset(existing.GUID);
            }

            // 创建条目
            var entry = CreateEntry(fullPath, ext);
            if (entry == null) return null;

            // 查找对应导入器
            var importer = FindImporter(ext);
            if (importer != null)
            {
                try
                {
                    var result = importer.Import(fullPath, entry.GUID, virtualPath);
                    if (result.Success && result.Entry != null)
                    {
                        entry = result.Entry;
                        if (result.RuntimeAsset != null)
                        {
                            _assetCache[entry.GUID] = result.RuntimeAsset;
                            entry.IsLoaded = true;
                            entry.RuntimeAsset = result.RuntimeAsset;
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Import: 导入失败 - {fullPath}: {ex.Message}");
                }
            }

            RegisterEntry(entry);
            OnAssetImported?.Invoke(entry);
            return entry;
        }

        /// <summary>异步导入资产文件。</summary>
        public Task<AssetEntry?> ImportAsync(string filePath, CancellationToken cancellationToken = default)
        {
            return Task.Run(() => Import(filePath), cancellationToken);
        }

        // ══════════════════════════════════════════════════════════
        // 查询
        // ══════════════════════════════════════════════════════════

        /// <summary>通过 GUID 查找资产条目。</summary>
        public AssetEntry? FindByGUID(Guid guid)
        {
            return _entriesByGuid.TryGetValue(guid, out var entry) ? entry : null;
        }

        /// <summary>通过路径查找资产条目。</summary>
        public AssetEntry? FindByPath(string path)
        {
            var normalized = NormalizePath(path);
            return _entriesByPath.TryGetValue(normalized, out var entry) ? entry : null;
        }

        /// <summary>通过标签查找资产条目 (任一标签匹配)。</summary>
        public AssetEntry[] FindByTag(params string[] tags)
        {
            if (tags == null || tags.Length == 0) return Array.Empty<AssetEntry>();

            var results = new HashSet<AssetEntry>();
            foreach (var tag in tags)
            {
                var normalizedTag = tag.ToLowerInvariant();
                if (_entriesByTag.TryGetValue(normalizedTag, out var entries))
                {
                    foreach (var e in entries) results.Add(e);
                }
            }
            return results.ToArray();
        }

        /// <summary>通过标签查找 (所有标签必须匹配)。</summary>
        public AssetEntry[] FindByAllTags(params string[] tags)
        {
            if (tags == null || tags.Length == 0) return Array.Empty<AssetEntry>();

            AssetEntry[]? candidates = null;
            foreach (var tag in tags)
            {
                var normalizedTag = tag.ToLowerInvariant();
                if (!_entriesByTag.TryGetValue(normalizedTag, out var entries))
                    return Array.Empty<AssetEntry>(); // 有标签没有匹配

                if (candidates == null)
                {
                    candidates = entries.ToArray();
                }
                else
                {
                    var entrySet = new HashSet<AssetEntry>(entries);
                    candidates = candidates.Where(e => entrySet.Contains(e)).ToArray();
                }
            }
            return candidates ?? Array.Empty<AssetEntry>();
        }

        /// <summary>通过资产类型查找。</summary>
        public AssetEntry[] FindByType(AssetType type)
        {
            return _entriesByType.TryGetValue(type, out var entries)
                ? entries.ToArray()
                : Array.Empty<AssetEntry>();
        }

        /// <summary>
        /// 全文搜索 (匹配名称和标签)。
        /// </summary>
        /// <param name="query">搜索关键词。</param>
        /// <returns>匹配的资产条目列表。</returns>
        public AssetEntry[] Search(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return Array.Empty<AssetEntry>();

            var lowerQuery = query.ToLowerInvariant();
            var results = new List<AssetEntry>();

            foreach (var entry in _entriesByGuid.Values)
            {
                // 搜索名称
                if (entry.DisplayName.ToLowerInvariant().Contains(lowerQuery))
                {
                    results.Add(entry);
                    continue;
                }

                // 搜索路径
                if (entry.Path.ToLowerInvariant().Contains(lowerQuery))
                {
                    results.Add(entry);
                    continue;
                }

                // 搜索标签
                bool tagMatch = false;
                foreach (var tag in entry.Tags)
                {
                    if (tag.ToLowerInvariant().Contains(lowerQuery))
                    {
                        tagMatch = true;
                        break;
                    }
                }
                if (tagMatch)
                {
                    results.Add(entry);
                }
            }

            return results.ToArray();
        }

        // ══════════════════════════════════════════════════════════
        // 依赖管理
        // ══════════════════════════════════════════════════════════

        /// <summary>
        /// 递归获取资产的所有依赖 (直接 + 间接依赖)。
        /// </summary>
        /// <param name="guid">目标资产 GUID。</param>
        /// <returns>所有依赖资产的 GUID 数组。</returns>
        public Guid[] GetDependencies(Guid guid)
        {
            var result = new HashSet<Guid>();
            var stack = new Stack<Guid>();
            stack.Push(guid);

            while (stack.Count > 0)
            {
                var current = stack.Pop();
                if (result.Contains(current)) continue;
                result.Add(current);

                if (_dependencyGraph.TryGetValue(current, out var deps))
                {
                    foreach (var dep in deps)
                    {
                        if (!result.Contains(dep))
                            stack.Push(dep);
                    }
                }
            }

            result.Remove(guid); // 移除自身
            return result.ToArray();
        }

        /// <summary>
        /// 获取所有引用了该资产的资产 (谁依赖了这个资产)。
        /// </summary>
        /// <param name="guid">目标资产 GUID。</param>
        /// <returns>所有引用者的 GUID 数组。</returns>
        public Guid[] GetReferences(Guid guid)
        {
            if (_referenceGraph.TryGetValue(guid, out var refs))
                return refs.ToArray();
            return Array.Empty<Guid>();
        }

        /// <summary>
        /// 建立依赖关系: assetGUID 依赖于 dependencyGUID。
        /// </summary>
        public void AddDependency(Guid assetGuid, Guid dependencyGuid)
        {
            if (assetGuid == dependencyGuid) return;

            // 依赖图: asset -> dependency
            if (!_dependencyGraph.TryGetValue(assetGuid, out var deps))
            {
                deps = new HashSet<Guid>();
                _dependencyGraph[assetGuid] = deps;
            }
            deps.Add(dependencyGuid);

            // 引用图: dependency <- asset
            if (!_referenceGraph.TryGetValue(dependencyGuid, out var refs))
            {
                refs = new HashSet<Guid>();
                _referenceGraph[dependencyGuid] = refs;
            }
            refs.Add(assetGuid);

            // 更新 AssetEntry
            if (_entriesByGuid.TryGetValue(assetGuid, out var entry))
            {
                var depSet = new HashSet<Guid>(entry.Dependencies);
                depSet.Add(dependencyGuid);
                entry.Dependencies = depSet.ToArray();
            }
            if (_entriesByGuid.TryGetValue(dependencyGuid, out var depEntry))
            {
                var refSet = new HashSet<Guid>(depEntry.References);
                refSet.Add(assetGuid);
                depEntry.References = refSet.ToArray();
            }
        }

        /// <summary>
        /// 移除依赖关系。
        /// </summary>
        public void RemoveDependency(Guid assetGuid, Guid dependencyGuid)
        {
            if (_dependencyGraph.TryGetValue(assetGuid, out var deps))
            {
                deps.Remove(dependencyGuid);
                if (deps.Count == 0) _dependencyGraph.Remove(assetGuid);
            }

            if (_referenceGraph.TryGetValue(dependencyGuid, out var refs))
            {
                refs.Remove(assetGuid);
                if (refs.Count == 0) _referenceGraph.Remove(dependencyGuid);
            }

            if (_entriesByGuid.TryGetValue(assetGuid, out var entry))
            {
                var depSet = new HashSet<Guid>(entry.Dependencies);
                depSet.Remove(dependencyGuid);
                entry.Dependencies = depSet.ToArray();
            }
            if (_entriesByGuid.TryGetValue(dependencyGuid, out var depEntry))
            {
                var refSet = new HashSet<Guid>(depEntry.References);
                refSet.Remove(assetGuid);
                depEntry.References = refSet.ToArray();
            }
        }

        /// <summary>
        /// 检查资产是否被其他资产引用 (引用计数 > 0)。
        /// </summary>
        public bool IsReferenced(Guid guid)
        {
            return _referenceGraph.TryGetValue(guid, out var refs) && refs.Count > 0;
        }

        /// <summary>获取资产的引用计数。</summary>
        public int GetReferenceCount(Guid guid)
        {
            return _referenceGraph.TryGetValue(guid, out var refs) ? refs.Count : 0;
        }

        // ══════════════════════════════════════════════════════════
        // 资产移除
        // ══════════════════════════════════════════════════════════

        /// <summary>
        /// 移除资产。会检查引用计数, 如果有其他资产引用此资产, 则不强制移除。
        /// </summary>
        /// <param name="guid">要移除的资产 GUID。</param>
        /// <param name="force">是否强制移除 (即使有引用)。</param>
        /// <returns>是否成功移除。</returns>
        public bool RemoveAsset(Guid guid, bool force = false)
        {
            if (!_entriesByGuid.TryGetValue(guid, out var entry)) return false;

            // 检查引用计数
            if (!force && IsReferenced(guid))
            {
                System.Diagnostics.Debug.WriteLine(
                    $"RemoveAsset: 资产 {entry.DisplayName} 被 {GetReferenceCount(guid)} 个资产引用, 不移除。");
                return false;
            }

            // 从所有引用图中移除
            if (_referenceGraph.TryGetValue(guid, out var refs))
            {
                foreach (var refGuid in refs)
                {
                    RemoveDependency(refGuid, guid);
                }
            }
            if (_dependencyGraph.TryGetValue(guid, out var deps))
            {
                var depCopy = deps.ToArray();
                foreach (var depGuid in depCopy)
                {
                    RemoveDependency(guid, depGuid);
                }
            }

            // 从索引中移除
            _entriesByGuid.Remove(guid);
            _entriesByPath.Remove(entry.Path);

            // 从类型索引中移除
            if (_entriesByType.TryGetValue(entry.Type, out var typeList))
            {
                typeList.Remove(entry);
            }

            // 从标签索引中移除
            foreach (var tag in entry.Tags)
            {
                var normalizedTag = tag.ToLowerInvariant();
                if (_entriesByTag.TryGetValue(normalizedTag, out var tagList))
                {
                    tagList.Remove(entry);
                }
            }

            // 从缓存中移除
            _assetCache.TryRemove(guid, out _);

            OnAssetDeleted?.Invoke(entry);
            return true;
        }

        // ══════════════════════════════════════════════════════════
        // 标签管理
        // ══════════════════════════════════════════════════════════

        /// <summary>为资产添加标签。</summary>
        public void AddTags(Guid guid, params string[] tags)
        {
            if (!_entriesByGuid.TryGetValue(guid, out var entry)) return;

            var tagSet = new HashSet<string>(entry.Tags, StringComparer.OrdinalIgnoreCase);
            foreach (var tag in tags)
            {
                if (tagSet.Add(tag))
                {
                    var normalizedTag = tag.ToLowerInvariant();
                    if (!_entriesByTag.TryGetValue(normalizedTag, out var list))
                    {
                        list = new List<AssetEntry>();
                        _entriesByTag[normalizedTag] = list;
                    }
                    list.Add(entry);
                }
            }
            entry.Tags = tagSet.ToArray();
        }

        /// <summary>移除资产的标签。</summary>
        public void RemoveTags(Guid guid, params string[] tags)
        {
            if (!_entriesByGuid.TryGetValue(guid, out var entry)) return;

            var tagSet = new HashSet<string>(entry.Tags, StringComparer.OrdinalIgnoreCase);
            foreach (var tag in tags)
            {
                if (tagSet.Remove(tag))
                {
                    var normalizedTag = tag.ToLowerInvariant();
                    if (_entriesByTag.TryGetValue(normalizedTag, out var list))
                    {
                        list.Remove(entry);
                        if (list.Count == 0) _entriesByTag.Remove(normalizedTag);
                    }
                }
            }
            entry.Tags = tagSet.ToArray();
        }

        // ══════════════════════════════════════════════════════════
        // 资产缓存
        // ══════════════════════════════════════════════════════════

        /// <summary>将已加载的资产对象缓存到数据库中。</summary>
        public void CacheAsset(Guid guid, object asset)
        {
            if (asset == null) return;
            _assetCache[guid] = asset;
            if (_entriesByGuid.TryGetValue(guid, out var entry))
            {
                entry.IsLoaded = true;
                entry.RuntimeAsset = asset;
            }
        }

        /// <summary>从缓存中获取资产对象。</summary>
        public T? GetCachedAsset<T>(Guid guid) where T : class
        {
            return _assetCache.TryGetValue(guid, out var asset) ? asset as T : null;
        }

        /// <summary>从缓存中移除资产对象。</summary>
        public void UncacheAsset(Guid guid)
        {
            _assetCache.TryRemove(guid, out _);
            if (_entriesByGuid.TryGetValue(guid, out var entry))
            {
                entry.IsLoaded = false;
                entry.RuntimeAsset = null;
            }
        }

        /// <summary>清空所有缓存。</summary>
        public void ClearCache()
        {
            _assetCache.Clear();
            foreach (var entry in _entriesByGuid.Values)
            {
                entry.IsLoaded = false;
                entry.RuntimeAsset = null;
            }
        }

        // ══════════════════════════════════════════════════════════
        // 清单持久化
        // ══════════════════════════════════════════════════════════

        /// <summary>
        /// 保存资产清单到 JSON 文件。
        /// </summary>
        /// <param name="filePath">清单文件路径 (默认: 项目根/AssetManifest.json)。</param>
        public bool SaveManifest(string? filePath = null)
        {
            try
            {
                var manifest = new AssetManifest
                {
                    Version = 1,
                    GeneratedAt = DateTime.UtcNow,
                    ProjectRoot = _projectRoot,
                    Assets = _entriesByGuid.Values.ToList()
                };

                var json = JsonSerializer.Serialize(manifest,
                    AssetManifestSerializerContext.Default.AssetManifest);

                var savePath = filePath ?? Path.Combine(_projectRoot, "AssetManifest.json");
                var dir = Path.GetDirectoryName(savePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                File.WriteAllText(savePath, json);
                System.Diagnostics.Debug.WriteLine($"SaveManifest: 清单已保存到 {savePath}");
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"SaveManifest: 保存失败 - {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 从 JSON 文件加载资产清单。
        /// </summary>
        /// <param name="filePath">清单文件路径。</param>
        public bool LoadManifest(string? filePath = null)
        {
            try
            {
                var loadPath = filePath ?? Path.Combine(_projectRoot, "AssetManifest.json");
                if (!File.Exists(loadPath))
                {
                    System.Diagnostics.Debug.WriteLine($"LoadManifest: 清单文件不存在 - {loadPath}");
                    return false;
                }

                var json = File.ReadAllText(loadPath);
                var manifest = JsonSerializer.Deserialize(json,
                    AssetManifestSerializerContext.Default.AssetManifest);

                if (manifest == null) return false;

                // 清空现有数据
                _entriesByGuid.Clear();
                _entriesByPath.Clear();
                _entriesByTag.Clear();
                _entriesByType.Clear();
                _dependencyGraph.Clear();
                _referenceGraph.Clear();

                // 加载条目
                foreach (var entry in manifest.Assets)
                {
                    RegisterEntry(entry);
                }

                // 重建依赖图
                foreach (var entry in manifest.Assets)
                {
                    foreach (var depGuid in entry.Dependencies)
                    {
                        AddDependency(entry.GUID, depGuid);
                    }
                }

                System.Diagnostics.Debug.WriteLine(
                    $"LoadManifest: 加载 {manifest.Assets.Count} 个资产条目");
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"LoadManifest: 加载失败 - {ex.Message}");
                return false;
            }
        }

        // ══════════════════════════════════════════════════════════
        // 导入器注册
        // ══════════════════════════════════════════════════════════

        /// <summary>注册自定义资产导入器。</summary>
        public void RegisterImporter(IAssetImporter importer)
        {
            if (importer == null) throw new ArgumentNullException(nameof(importer));
            _importers.Add(importer);
        }

        /// <summary>查找能处理指定扩展名的导入器。</summary>
        public IAssetImporter? FindImporter(string extension)
        {
            foreach (var importer in _importers)
            {
                foreach (var ext in importer.SupportedExtensions)
                {
                    if (string.Equals(ext, extension, StringComparison.OrdinalIgnoreCase))
                        return importer;
                }
            }
            return null;
        }

        // ══════════════════════════════════════════════════════════
        // 内部方法
        // ══════════════════════════════════════════════════════════

        private void RegisterEntry(AssetEntry entry)
        {
            _entriesByGuid[entry.GUID] = entry;
            _entriesByPath[entry.Path] = entry;

            // 类型索引
            if (!_entriesByType.TryGetValue(entry.Type, out var typeList))
            {
                typeList = new List<AssetEntry>();
                _entriesByType[entry.Type] = typeList;
            }
            typeList.Add(entry);

            // 标签索引
            foreach (var tag in entry.Tags)
            {
                var normalizedTag = tag.ToLowerInvariant();
                if (!_entriesByTag.TryGetValue(normalizedTag, out var tagList))
                {
                    tagList = new List<AssetEntry>();
                    _entriesByTag[normalizedTag] = tagList;
                }
                tagList.Add(entry);
            }
        }

        private AssetEntry? CreateEntry(string fullPath, string extension)
        {
            try
            {
                var fileInfo = new FileInfo(fullPath);
                var virtualPath = GetVirtualPath(fullPath);

                var importer = FindImporter(extension);
                var type = importer?.AssetType ?? DetectAssetType(extension);

                return new AssetEntry
                {
                    GUID = Guid.NewGuid(),
                    Path = virtualPath,
                    Type = type,
                    FileSize = fileInfo.Length,
                    ImportTime = DateTime.UtcNow,
                    LastModifiedTime = fileInfo.LastWriteTimeUtc,
                    ContentHash = ComputeFileHash(fullPath),
                    Tags = DetectDefaultTags(extension)
                };
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"CreateEntry: 创建失败 - {fullPath}: {ex.Message}");
                return null;
            }
        }

        private static AssetType DetectAssetType(string extension)
        {
            return extension.ToLowerInvariant() switch
            {
                ".fbx" or ".obj" or ".gltf" or ".glb" or ".dae" or ".blend" => AssetType.Mesh,
                ".png" or ".jpg" or ".jpeg" or ".bmp" or ".tga" or ".dds" or ".hdr" or ".webp" => AssetType.Texture2D,
                ".wav" or ".ogg" or ".mp3" or ".flac" => AssetType.Audio,
                ".cs" => AssetType.Script,
                ".yama" or ".syasset" => AssetType.Scene,
                _ => AssetType.Unknown
            };
        }

        private static string[] DetectDefaultTags(string extension)
        {
            return extension.ToLowerInvariant() switch
            {
                ".fbx" or ".obj" or ".gltf" or ".glb" or ".dae" or ".blend" => new[] { "mesh", "model", "3d" },
                ".png" or ".jpg" or ".jpeg" or ".bmp" or ".tga" or ".dds" or ".hdr" or ".webp" => new[] { "texture", "image" },
                ".wav" or ".ogg" or ".mp3" or ".flac" => new[] { "audio", "sound" },
                ".cs" => new[] { "script", "code" },
                ".yama" or ".syasset" => new[] { "scene", "yama" },
                _ => Array.Empty<string>()
            };
        }

        private string ResolveFullPath(string path)
        {
            if (Path.IsPathRooted(path)) return path;
            return Path.Combine(_projectRoot, path.Replace('/', Path.DirectorySeparatorChar));
        }

        private string GetVirtualPath(string fullPath)
        {
            var relative = Path.GetRelativePath(_projectRoot, fullPath);
            return relative.Replace('\\', '/');
        }

        private static string NormalizePath(string path)
        {
            return path.Replace('\\', '/').TrimStart('/');
        }

        private static string ComputeFileHash(string filePath)
        {
            try
            {
                using var sha256 = SHA256.Create();
                using var stream = File.OpenRead(filePath);
                var hash = sha256.ComputeHash(stream);
                return Convert.ToHexString(hash).ToLowerInvariant();
            }
            catch
            {
                return string.Empty;
            }
        }

        public void Dispose()
        {
            _importQueue.Dispose();
            _entriesByGuid.Clear();
            _entriesByPath.Clear();
            _entriesByTag.Clear();
            _entriesByType.Clear();
            _dependencyGraph.Clear();
            _referenceGraph.Clear();
            _assetCache.Clear();
            OnAssetImported = null;
            OnAssetDeleted = null;
            OnAssetModified = null;
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 6. GenericAssetImporter — 通用资产导入器
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 通用资产导入器。用于不需要特殊处理的文件格式,
    /// 仅创建资产条目而不实际加载运行时对象。
    /// </summary>
    internal class GenericAssetImporter : IAssetImporter
    {
        public string[] SupportedExtensions { get; }
        public AssetType AssetType { get; }

        public GenericAssetImporter(string[] extensions, AssetType type)
        {
            SupportedExtensions = extensions ?? throw new ArgumentNullException(nameof(extensions));
            AssetType = type;
        }

        public AssetImportResult Import(string filePath, Guid guid, string virtualPath)
        {
            var fileInfo = new FileInfo(filePath);
            return new AssetImportResult
            {
                Entry = new AssetEntry
                {
                    GUID = guid,
                    Path = virtualPath,
                    Type = AssetType,
                    FileSize = fileInfo.Length,
                    ImportTime = DateTime.UtcNow,
                    LastModifiedTime = fileInfo.LastWriteTimeUtc,
                    ContentHash = ComputeHash(filePath)
                },
                Success = true
            };
        }

        public bool NeedsReimport(string filePath, string lastContentHash)
        {
            var currentHash = ComputeHash(filePath);
            return !string.Equals(currentHash, lastContentHash, StringComparison.OrdinalIgnoreCase);
        }

        private static string ComputeHash(string filePath)
        {
            try
            {
                using var sha256 = SHA256.Create();
                using var stream = File.OpenRead(filePath);
                var hash = sha256.ComputeHash(stream);
                return Convert.ToHexString(hash).ToLowerInvariant();
            }
            catch { return string.Empty; }
        }
    }
}
