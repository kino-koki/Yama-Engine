// =====================================================================================
//  YamaEngine - AssetImportManager：资产导入管理器
//  命名空间：YamaEngine.Assets.Importers
// -------------------------------------------------------------------------------------
//  功能：
//    - 文件类型检测（通过扩展名 + Magic Bytes）
//    - 格式到导入器映射（注册表模式）
//    - 导入队列（多线程支持）
//    - 导入结果缓存（避免重复导入）
//    - 导入进度报告
//    - 错误日志（失败原因 / 部分成功）
// =====================================================================================

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace YamaEngine.Assets.Importers
{
    // =================================================================================
    //  导入结果
    // =================================================================================

    /// <summary>导入结果状态。</summary>
    public enum ImportResultStatus
    {
        Success,
        PartialSuccess,
        Failed,
        Cancelled,
        Cached
    }

    /// <summary>导入结果。</summary>
    public sealed class ImportResult
    {
        /// <summary>源文件路径。</summary>
        public string SourceFilePath = string.Empty;

        /// <summary>导入状态。</summary>
        public ImportResultStatus Status;

        /// <summary>导入产出的场景（成功时）。</summary>
        public InterchangeScene? Scene;

        /// <summary>错误 / 警告消息列表。</summary>
        public List<string> Messages = new();

        /// <summary>导入耗时（毫秒）。</summary>
        public long ElapsedMs;

        /// <summary>网格数量。</summary>
        public int MeshCount;

        /// <summary>材质数量。</summary>
        public int MaterialCount;

        /// <summary>顶点总数。</summary>
        public int TotalVertices;

        /// <summary>三角形总数。</summary>
        public int TotalTriangles;
    }

    // =================================================================================
    //  导入任务
    // =================================================================================

    /// <summary>导入任务状态。</summary>
    public enum ImportTaskState
    {
        Pending,
        Processing,
        Completed,
        Failed,
        Cancelled
    }

    /// <summary>导入任务。</summary>
    public sealed class ImportTask
    {
        /// <summary>任务唯一 ID。</summary>
        public Guid Id = Guid.NewGuid();

        /// <summary>源文件路径。</summary>
        public string FilePath = string.Empty;

        /// <summary>当前状态。</summary>
        public ImportTaskState State = ImportTaskState.Pending;

        /// <summary>进度 [0..1]。</summary>
        public float Progress;

        /// <summary>进度消息。</summary>
        public string Message = string.Empty;

        /// <summary>导入结果。</summary>
        public ImportResult? Result;

        /// <summary>取消令牌。</summary>
        internal CancellationTokenSource? Cts;

        /// <summary>进度回调。</summary>
        public event Action<ImportTask, float, string>? OnProgress;

        /// <summary>完成回调。</summary>
        public event Action<ImportTask>? OnCompleted;

        internal void ReportProgress(float progress, string message)
        {
            Progress = progress;
            Message = message;
            OnProgress?.Invoke(this, progress, message);
        }

        internal void NotifyCompleted()
        {
            State = Result?.Status == ImportResultStatus.Success || Result?.Status == ImportResultStatus.PartialSuccess
                ? ImportTaskState.Completed
                : Result?.Status == ImportResultStatus.Cancelled
                    ? ImportTaskState.Cancelled
                    : ImportTaskState.Failed;
            OnCompleted?.Invoke(this);
        }
    }

    // =================================================================================
    //  导入器注册表接口
    // =================================================================================

    /// <summary>
    /// 导入器接口。每个导入器负责解析一种特定格式的文件。
    /// </summary>
    public interface IAssetImporter
    {
        /// <summary>支持的格式标识列表（如 "fbx", "usd", "usda" 等）。</summary>
        IEnumerable<string> SupportedExtensions { get; }

        /// <summary>导入指定文件。</summary>
        InterchangeScene Import(string filePath);
    }

    // =================================================================================
    //  内置导入器注册
    // =================================================================================

    /// <summary>FBX 导入器适配器。</summary>
    public sealed class FbxImporterAdapter : IAssetImporter
    {
        public IEnumerable<string> SupportedExtensions => new[] { "fbx" };

        public InterchangeScene Import(string filePath)
        {
            var importer = new FbxImporter(filePath);
            return importer.Import();
        }
    }

    /// <summary>USD 导入器适配器。</summary>
    public sealed class UsdImporterAdapter : IAssetImporter
    {
        public IEnumerable<string> SupportedExtensions => new[] { "usd", "usda", "usdc" };

        public InterchangeScene Import(string filePath)
        {
            var importer = new UsdImporter(filePath);
            return importer.Import();
        }
    }

    // =================================================================================
    //  AssetImportManager —— 资产导入管理器
    // =================================================================================

    /// <summary>
    /// 资产导入管理器。负责：
    /// 1. 文件类型检测（扩展名 + Magic Bytes）
    /// 2. 格式到导入器的注册表映射
    /// 3. 导入队列（多线程）
    /// 4. 导入结果缓存
    /// 5. 进度报告
    /// 6. 错误日志
    /// </summary>
    public sealed class AssetImportManager : IDisposable
    {
        // 导入器注册表（扩展名 -> 导入器）
        private readonly Dictionary<string, IAssetImporter> _importers =
            new(StringComparer.OrdinalIgnoreCase);

        // Magic bytes -> 导入器标识
        private readonly Dictionary<string, string> _magicMap =
            new(StringComparer.Ordinal);

        // 导入结果缓存（源文件路径 -> 结果）
        private readonly ConcurrentDictionary<string, ImportResult> _cache =
            new(StringComparer.OrdinalIgnoreCase);

        // 任务队列
        private readonly ConcurrentQueue<ImportTask> _queue = new();

        // 后台处理线程
        private Thread? _workerThread;
        private CancellationTokenSource _workerCts = new();
        private bool _disposed;

        /// <summary>最大并行导入数。</summary>
        public int MaxConcurrentImports = 4;

        /// <summary>是否启用缓存（相同文件避免重复导入）。</summary>
        public bool EnableCache = true;

        /// <summary>是否自动检测文件类型（通过 Magic Bytes）。</summary>
        public bool EnableMagicDetection = true;

        // 事件
        /// <summary>任务进度事件。</summary>
        public event Action<ImportTask, float, string>? OnTaskProgress;

        /// <summary>任务完成事件。</summary>
        public event Action<ImportTask>? OnTaskCompleted;

        /// <summary>导入错误事件。</summary>
        public event Action<string, Exception>? OnImportError;

        /// <summary>日志事件。</summary>
        public event Action<string>? OnLog;

        /// <summary>构造导入管理器并注册默认导入器。</summary>
        public AssetImportManager()
        {
            RegisterDefaultImporters();
            RegisterMagicBytes();
        }

        // =================================================================================
        //  导入器注册
        // =================================================================================

        /// <summary>注册一个导入器。</summary>
        public void RegisterImporter(IAssetImporter importer)
        {
            if (importer == null) throw new ArgumentNullException(nameof(importer));

            foreach (var ext in importer.SupportedExtensions)
            {
                _importers[ext.TrimStart('.').ToLowerInvariant()] = importer;
                Log($"注册导入器: {importer.GetType().Name} -> .{ext}");
            }
        }

        /// <summary>注册 Magic Bytes 检测规则。</summary>
        public void RegisterMagic(string magic, string formatKey)
        {
            _magicMap[magic] = formatKey;
        }

        private void RegisterDefaultImporters()
        {
            RegisterImporter(new FbxImporterAdapter());
            RegisterImporter(new UsdImporterAdapter());
        }

        private void RegisterMagicBytes()
        {
            // FBX 二进制
            _magicMap["Kaydara FBX Binary"] = "fbx";
            // USD Crate
            _magicMap["PXR-USDC"] = "usdc";
        }

        // =================================================================================
        //  文件类型检测
        // =================================================================================

        /// <summary>
        /// 通过文件扩展名和 Magic Bytes 检测文件类型。
        /// 返回格式标识（小写，如 "fbx" / "usd" / "usda" / "usdc"）。
        /// 无法识别则返回 "unknown"。
        /// </summary>
        public string DetectFileType(string filePath)
        {
            // 1) 通过扩展名判断
            string ext = Path.GetExtension(filePath).TrimStart('.').ToLowerInvariant();

            if (_importers.ContainsKey(ext))
                return ext;

            // 2) 通过 Magic Bytes 判断
            if (EnableMagicDetection)
            {
                try
                {
                    using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    byte[] header = new byte[32];
                    int read = fs.Read(header, 0, header.Length);
                    string magicStr = System.Text.Encoding.ASCII.GetString(header, 0, read);

                    foreach (var kvp in _magicMap)
                    {
                        if (magicStr.StartsWith(kvp.Key, StringComparison.Ordinal))
                            return kvp.Value;
                    }
                }
                catch
                {
                    // 读文件头失败 — 回退到扩展名
                }
            }

            return ext;
        }

        /// <summary>检查文件类型是否受支持。</summary>
        public bool IsFormatSupported(string filePath)
        {
            string fmt = DetectFileType(filePath);
            return _importers.ContainsKey(fmt);
        }

        // =================================================================================
        //  单文件导入
        // =================================================================================

        /// <summary>
        /// 同步导入单个文件。
        /// </summary>
        public ImportResult ImportFile(string filePath)
        {
            if (string.IsNullOrEmpty(filePath))
                throw new ArgumentNullException(nameof(filePath));

            if (!File.Exists(filePath))
                return new ImportResult
                {
                    SourceFilePath = filePath,
                    Status = ImportResultStatus.Failed,
                    Messages = { $"文件不存在: {filePath}" }
                };

            // 缓存检查
            string fullPath = Path.GetFullPath(filePath);
            if (EnableCache && _cache.TryGetValue(fullPath, out var cached))
            {
                Log($"缓存命中: {fullPath}");
                return cached;
            }

            // 检测格式
            string format = DetectFileType(fullPath);
            if (!_importers.TryGetValue(format, out var importer))
            {
                return new ImportResult
                {
                    SourceFilePath = fullPath,
                    Status = ImportResultStatus.Failed,
                    Messages = { $"不支持的格式: .{format}" }
                };
            }

            Log($"开始导入: {fullPath} (格式: {format})");
            var sw = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                var scene = importer.Import(fullPath);
                sw.Stop();

                int totalVerts = 0, totalTris = 0;
                foreach (var mesh in scene.Meshes)
                {
                    totalVerts += mesh.Vertices.Count;
                    totalTris += mesh.TriangleCount;
                }

                var result = new ImportResult
                {
                    SourceFilePath = fullPath,
                    Status = ImportResultStatus.Success,
                    Scene = scene,
                    ElapsedMs = sw.ElapsedMilliseconds,
                    MeshCount = scene.Meshes.Count,
                    MaterialCount = scene.Materials.Count,
                    TotalVertices = totalVerts,
                    TotalTriangles = totalTris,
                    Messages = scene.ImportMessages
                };

                // 写入缓存
                if (EnableCache)
                    _cache[fullPath] = result;

                Log($"导入成功: {fullPath} ({totalVerts} 顶点, {totalTris} 三角形, {sw.ElapsedMilliseconds}ms)");
                return result;
            }
            catch (Exception ex)
            {
                sw.Stop();
                OnImportError?.Invoke(fullPath, ex);

                return new ImportResult
                {
                    SourceFilePath = fullPath,
                    Status = ImportResultStatus.Failed,
                    ElapsedMs = sw.ElapsedMilliseconds,
                    Messages = { $"导入失败: {ex.Message}" }
                };
            }
        }

        /// <summary>
        /// 异步导入单个文件。
        /// </summary>
        public Task<ImportResult> ImportFileAsync(string filePath)
        {
            return Task.Run(() => ImportFile(filePath));
        }

        // =================================================================================
        //  导入队列（多线程）
        // =================================================================================

        /// <summary>
        /// 创建导入任务并加入队列。
        /// </summary>
        public ImportTask EnqueueImport(string filePath)
        {
            var task = new ImportTask
            {
                FilePath = filePath,
                Cts = new CancellationTokenSource()
            };

            task.OnProgress += (t, p, m) => OnTaskProgress?.Invoke(t, p, m);
            task.OnCompleted += (t) => OnTaskCompleted?.Invoke(t);

            _queue.Enqueue(task);
            EnsureWorkerRunning();
            Log($"任务入队: {filePath}");
            return task;
        }

        /// <summary>
        /// 批量导入多个文件。
        /// </summary>
        public List<ImportTask> EnqueueBatch(IEnumerable<string> filePaths)
        {
            var tasks = new List<ImportTask>();
            foreach (var path in filePaths)
            {
                if (!string.IsNullOrEmpty(path))
                    tasks.Add(EnqueueImport(path));
            }
            return tasks;
        }

        /// <summary>
        /// 异步导入目录中所有支持的格式文件。
        /// </summary>
        public List<ImportTask> ImportDirectory(string directoryPath, bool recursive = true,
            IProgress<(int Completed, int Total)>? progress = null)
        {
            var tasks = new List<ImportTask>();
            if (!Directory.Exists(directoryPath)) return tasks;

            var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            var files = Directory.EnumerateFiles(directoryPath, "*.*", searchOption)
                .Where(f => IsFormatSupported(f))
                .ToList();

            foreach (var file in files)
                tasks.Add(EnqueueImport(file));

            Log($"批量导入: {files.Count} 个文件从 {directoryPath}");
            return tasks;
        }

        // =================================================================================
        //  后台工作线程
        // =================================================================================

        private void EnsureWorkerRunning()
        {
            if (_workerThread == null || !_workerThread.IsAlive)
            {
                _workerCts = new CancellationTokenSource();
                _workerThread = new Thread(ProcessQueue) { IsBackground = true, Name = "AssetImportWorker" };
                _workerThread.Start();
            }
        }

        private void ProcessQueue()
        {
            var token = _workerCts.Token;

            while (!token.IsCancellationRequested)
            {
                if (_queue.TryDequeue(out var task))
                {
                    ProcessTask(task, token);
                }
                else
                {
                    // 队列为空，退出线程
                    break;
                }
            }
        }

        private void ProcessTask(ImportTask task, CancellationToken token)
        {
            try
            {
                task.State = ImportTaskState.Processing;
                task.ReportProgress(0f, "开始导入...");

                token.ThrowIfCancellationRequested();

                // 实际导入
                var result = ImportFile(task.FilePath);

                task.Result = result;

                if (result.Status == ImportResultStatus.Success)
                {
                    task.ReportProgress(1f, "导入完成");
                }
                else
                {
                    task.ReportProgress(1f, $"导入失败: {string.Join("; ", result.Messages)}");
                }
            }
            catch (OperationCanceledException)
            {
                task.Result = new ImportResult
                {
                    SourceFilePath = task.FilePath,
                    Status = ImportResultStatus.Cancelled,
                    Messages = { "任务已取消" }
                };
            }
            catch (Exception ex)
            {
                task.Result = new ImportResult
                {
                    SourceFilePath = task.FilePath,
                    Status = ImportResultStatus.Failed,
                    Messages = { $"导入异常: {ex.Message}" }
                };
                OnImportError?.Invoke(task.FilePath, ex);
            }
            finally
            {
                task.NotifyCompleted();
            }
        }

        // =================================================================================
        //  缓存管理
        // =================================================================================

        /// <summary>清空导入结果缓存。</summary>
        public void ClearCache()
        {
            _cache.Clear();
            Log("缓存已清空");
        }

        /// <summary>从缓存中移除指定文件的结果。</summary>
        public void RemoveFromCache(string filePath)
        {
            string fullPath = Path.GetFullPath(filePath);
            _cache.TryRemove(fullPath, out _);
        }

        /// <summary>检查文件是否已缓存。</summary>
        public bool IsCached(string filePath)
        {
            string fullPath = Path.GetFullPath(filePath);
            return EnableCache && _cache.ContainsKey(fullPath);
        }

        // =================================================================================
        //  统计信息
        // =================================================================================

        /// <summary>获取管理器统计信息。</summary>
        public ImportManagerStats GetStats()
        {
            return new ImportManagerStats
            {
                RegisteredImporters = _importers.Count,
                CachedResults = _cache.Count,
                PendingTasks = _queue.Count
            };
        }

        // =================================================================================
        //  日志
        // =================================================================================

        private void Log(string message)
        {
            OnLog?.Invoke($"[AssetImportManager] {message}");
            System.Diagnostics.Debug.WriteLine($"[AssetImportManager] {message}");
        }

        // =================================================================================
        //  释放
        // =================================================================================

        public void Dispose()
        {
            if (!_disposed)
            {
                _workerCts.Cancel();
                _workerCts.Dispose();
                _cache.Clear();
                _disposed = true;
            }
        }
    }

    // =================================================================================
    //  统计信息
    // =================================================================================

    /// <summary>导入管理器统计信息。</summary>
    public sealed class ImportManagerStats
    {
        /// <summary>已注册导入器数量。</summary>
        public int RegisteredImporters;

        /// <summary>缓存结果数量。</summary>
        public int CachedResults;

        /// <summary>待处理任务数。</summary>
        public int PendingTasks;
    }
}
