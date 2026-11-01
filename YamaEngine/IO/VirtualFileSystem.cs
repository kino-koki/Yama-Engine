// ============================================================================================
//  VirtualFileSystem.cs
//  yama引擎 (YamaEngine) 虚拟文件系统与异步加载器
// ============================================================================================
//  提供统一的文件访问抽象：
//    - 虚拟路径挂载（类似 UE .pak / Unity AssetBundle 的挂载点）
//    - 异步文件加载（Task/RingBuffer 回调双模式）
//    - 文件监视器（热重载触发）
//    - 压缩归档读取（.pak/.zip 透明解压）
// ============================================================================================

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace YamaEngine.IO
{
    // ====== 文件系统事件 ======
    public enum FileEventType { Created, Modified, Deleted, Renamed }

    public readonly struct FileEvent
    {
        public FileEventType Type { get; }
        public string VirtualPath { get; }
        public string PhysicalPath { get; }

        public FileEvent(FileEventType type, string virtualPath, string physicalPath)
        {
            Type = type; VirtualPath = virtualPath; PhysicalPath = physicalPath;
        }
    }

    // ====== 挂载点 ======
    public sealed class MountPoint
    {
        public string VirtualRoot { get; }
        public string PhysicalRoot { get; }
        public bool IsReadOnly { get; }
        public int Priority { get; } // 高优先级优先查找
        public string? ArchivePath { get; }

        internal ZipArchive? Archive { get; set; }

        public MountPoint(string virtualRoot, string physicalRoot, bool readOnly = false, int priority = 0, string? archivePath = null)
        {
            VirtualRoot = NormalizePath(virtualRoot);
            PhysicalRoot = NormalizePath(physicalRoot);
            IsReadOnly = readOnly;
            Priority = priority;
            ArchivePath = archivePath;
        }

        internal static string NormalizePath(string path)
        {
            return path.Replace('\\', '/').TrimEnd('/');
        }
    }

    // ====== 异步加载结果 ======
    public readonly struct FileLoadResult
    {
        public readonly byte[] Data;
        public readonly string VirtualPath;
        public readonly bool Success;
        public readonly string? Error;

        public FileLoadResult(byte[] data, string path, bool success, string? error = null)
        {
            Data = data; VirtualPath = path; Success = success; Error = error;
        }

        public string AsString() => Data != null ? Encoding.UTF8.GetString(Data) : string.Empty;
        public int Length => Data?.Length ?? 0;
    }

    // ====== 文件加载优先级 ======
    public enum LoadPriority
    {
        Low = 0,
        Normal = 1,
        High = 2,
        Critical = 3
    }

    // ====== 虚拟文件系统 ======
    /// <summary>
    /// yama引擎虚拟文件系统。支持：
    /// - 多挂载点叠层（类似 Linux mount）
    /// - 异步/同步文件读写
    /// - .pak/.zip 归档文件的透明访问
    /// - 文件变更监视（热重载）
    /// - 内存缓存
    /// </summary>
    public sealed class VirtualFileSystem : IDisposable
    {
        // ====== 单例 ======
        public static VirtualFileSystem Instance { get; private set; } = new VirtualFileSystem();

        // ====== 挂载点列表（按优先级排序） ======
        private readonly List<MountPoint> _mounts = new();

        // ====== 缓存 ======
        private readonly ConcurrentDictionary<string, FileLoadResult> _readCache = new();
        private readonly ConcurrentDictionary<string, byte[]> _writeCache = new();
        private bool _cacheEnabled = true;
        public int CacheSize => _readCache.Count;

        // ====== 文件监视 ======
        private FileSystemWatcher? _watcher;
        private readonly ConcurrentQueue<FileEvent> _fileEvents = new();
        public event Action<FileEvent>? OnFileChanged;

        // ====== 异步请求队列 ======
        private readonly ConcurrentQueue<LoadRequest> _asyncQueue = new();
        private int _pendingLoads;
        private readonly CancellationTokenSource _cts = new();

        // ====== 统计 ======
        public int TotalMounts => _mounts.Count;
        public int PendingLoads => _pendingLoads;
        public int TotalFileEvents => _fileEvents.Count;

        // ====== 构造 ======
        private VirtualFileSystem()
        {
            // 自动挂载基础路径
            Mount("engine:/", AppDomain.CurrentDomain.BaseDirectory, readOnly: true, priority: 0);
            Mount("project:/", Directory.GetCurrentDirectory(), readOnly: false, priority: 10);
        }

        // ====== 挂载管理 ======
        /// <summary>挂载一个物理目录到虚拟路径。</summary>
        public void Mount(string virtualRoot, string physicalPath, bool readOnly = false, int priority = 50, string? archivePath = null)
        {
            physicalPath = Path.GetFullPath(physicalPath);
            if (!Directory.Exists(physicalPath) && archivePath == null)
            {
                Diagnostics.Log.Warning($"挂载目标不存在: {physicalPath}");
                return;
            }

            var mount = new MountPoint(virtualRoot, physicalPath, readOnly, priority, archivePath);

            // 如果是归档文件，打开 ZipArchive
            if (archivePath != null && File.Exists(archivePath))
            {
                try
                {
                    mount.Archive = ZipFile.OpenRead(archivePath);
                }
                catch (Exception ex)
                {
                    Diagnostics.Log.Error($"无法打开归档 {archivePath}: {ex.Message}");
                }
            }

            lock (_mounts)
            {
                _mounts.Add(mount);
                _mounts.Sort((a, b) => b.Priority.CompareTo(a.Priority));
            }

            Diagnostics.Log.Info($"挂载: {virtualRoot} → {physicalPath}{(readOnly ? " (只读)" : "")}");
        }

        /// <summary>卸载虚拟路径。</summary>
        public void Unmount(string virtualRoot)
        {
            virtualRoot = MountPoint.NormalizePath(virtualRoot);
            lock (_mounts)
            {
                _mounts.RemoveAll(m => m.VirtualRoot == virtualRoot);
            }
        }

        // ====== 文件查找 ======
        /// <summary>将虚拟路径解析为物理路径。</summary>
        public string? ResolvePhysicalPath(string virtualPath)
        {
            virtualPath = MountPoint.NormalizePath(virtualPath);

            lock (_mounts)
            {
                foreach (var mount in _mounts)
                {
                    if (virtualPath.StartsWith(mount.VirtualRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        string relative = virtualPath.Substring(mount.VirtualRoot.Length).TrimStart('/');
                        string physical = MountPoint.NormalizePath(Path.Combine(mount.PhysicalRoot, relative));

                        if (mount.Archive != null)
                        {
                            // 归档中查找
                            var entry = mount.Archive.GetEntry(relative);
                            if (entry != null) return $"archive://{mount.ArchivePath}/{relative}";
                            continue;
                        }

                        if (File.Exists(physical))
                            return physical;
                    }
                }
            }
            return null;
        }

        // ====== 同步读取 ======
        /// <summary>同步读取文件到字节数组。</summary>
        public FileLoadResult ReadFile(string virtualPath)
        {
            virtualPath = MountPoint.NormalizePath(virtualPath);

            // 检查写缓存
            if (_writeCache.TryGetValue(virtualPath, out var cachedWrite))
                return new FileLoadResult(cachedWrite, virtualPath, true);

            // 检查读缓存
            if (_cacheEnabled && _readCache.TryGetValue(virtualPath, out var cached))
                return cached;

            lock (_mounts)
            {
                foreach (var mount in _mounts)
                {
                    if (!virtualPath.StartsWith(mount.VirtualRoot, StringComparison.OrdinalIgnoreCase))
                        continue;

                    string relative = virtualPath.Substring(mount.VirtualRoot.Length).TrimStart('/');
                    string physical = Path.Combine(mount.PhysicalRoot, relative);

                    if (mount.Archive != null)
                    {
                        var entry = mount.Archive.GetEntry(relative);
                        if (entry != null)
                        {
                            using var stream = entry.Open();
                            using var ms = new MemoryStream();
                            stream.CopyTo(ms);
                            var result = new FileLoadResult(ms.ToArray(), virtualPath, true);
                            if (_cacheEnabled) _readCache[virtualPath] = result;
                            return result;
                        }
                        continue;
                    }

                    if (File.Exists(physical))
                    {
                        try
                        {
                            byte[] data = File.ReadAllBytes(physical);
                            var result = new FileLoadResult(data, virtualPath, true);
                            if (_cacheEnabled) _readCache[virtualPath] = result;
                            return result;
                        }
                        catch (Exception ex)
                        {
                            return new FileLoadResult(Array.Empty<byte>(), virtualPath, false, ex.Message);
                        }
                    }
                }
            }

            return new FileLoadResult(Array.Empty<byte>(), virtualPath, false, "文件未找到");
        }

        /// <summary>同步读取文本文件。</summary>
        public string ReadText(string virtualPath)
        {
            var result = ReadFile(virtualPath);
            return result.Success ? result.AsString() : string.Empty;
        }

        // ====== 异步读取 ======
        /// <summary>异步读取文件。返回 Task。</summary>
        public Task<FileLoadResult> ReadFileAsync(string virtualPath, LoadPriority priority = LoadPriority.Normal)
        {
            return Task.Run(() =>
            {
                Interlocked.Increment(ref _pendingLoads);
                try
                {
                    // 模拟异步延迟（实际项目中用 IO Completion Ports）
                    if (priority == LoadPriority.Low)
                        Thread.SpinWait(100);

                    return ReadFile(virtualPath);
                }
                finally
                {
                    Interlocked.Decrement(ref _pendingLoads);
                }
            });
        }

        /// <summary>异步读取文本。</summary>
        public async Task<string> ReadTextAsync(string virtualPath)
        {
            var result = await ReadFileAsync(virtualPath);
            return result.Success ? result.AsString() : string.Empty;
        }

        // ====== 写入 ======
        /// <summary>写入文件到首个非只读挂载点。</summary>
        public bool WriteFile(string virtualPath, byte[] data)
        {
            virtualPath = MountPoint.NormalizePath(virtualPath);

            lock (_mounts)
            {
                foreach (var mount in _mounts)
                {
                    if (mount.IsReadOnly) continue;
                    if (!virtualPath.StartsWith(mount.VirtualRoot, StringComparison.OrdinalIgnoreCase))
                        continue;

                    string relative = virtualPath.Substring(mount.VirtualRoot.Length).TrimStart('/');
                    string physical = Path.Combine(mount.PhysicalRoot, relative);

                    try
                    {
                        string? dir = Path.GetDirectoryName(physical);
                        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                        File.WriteAllBytes(physical, data);
                        _writeCache[virtualPath] = data;
                        _readCache.TryRemove(virtualPath, out _);
                        return true;
                    }
                    catch (Exception ex)
                    {
                        Diagnostics.Log.Error($"写入文件失败 {physical}: {ex.Message}");
                        return false;
                    }
                }
            }

            Diagnostics.Log.Error($"没有可写的挂载点: {virtualPath}");
            return false;
        }

        /// <summary>写入文本文件。</summary>
        public bool WriteText(string virtualPath, string text)
            => WriteFile(virtualPath, Encoding.UTF8.GetBytes(text));

        // ====== 文件存在检查 ======
        public bool FileExists(string virtualPath)
        {
            virtualPath = MountPoint.NormalizePath(virtualPath);
            if (_writeCache.ContainsKey(virtualPath)) return true;
            return ResolvePhysicalPath(virtualPath) != null;
        }

        // ====== 目录操作 ======
        public string[] GetFiles(string virtualPath, string pattern = "*", bool recursive = false)
        {
            virtualPath = MountPoint.NormalizePath(virtualPath);
            var results = new List<string>();

            lock (_mounts)
            {
                foreach (var mount in _mounts)
                {
                    if (!virtualPath.StartsWith(mount.VirtualRoot, StringComparison.OrdinalIgnoreCase))
                        continue;

                    string relative = virtualPath.Substring(mount.VirtualRoot.Length).TrimStart('/');
                    string physical = Path.Combine(mount.PhysicalRoot, relative);

                    if (Directory.Exists(physical))
                    {
                        var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                        foreach (var file in Directory.GetFiles(physical, pattern, searchOption))
                        {
                            string virtualFile = mount.VirtualRoot + "/" +
                                Path.GetRelativePath(mount.PhysicalRoot, file).Replace('\\', '/');
                            results.Add(virtualFile);
                        }
                    }
                }
            }

            return results.ToArray();
        }

        // ====== 文件监视 ======
        /// <summary>启动文件监视器。</summary>
        public void StartWatching(string? physicalPath = null)
        {
            StopWatching();

            if (physicalPath == null)
            {
                lock (_mounts)
                {
                    foreach (var m in _mounts)
                    {
                        if (Directory.Exists(m.PhysicalRoot))
                        {
                            physicalPath = m.PhysicalRoot;
                            break;
                        }
                    }
                }
            }

            if (physicalPath == null) return;

            _watcher = new FileSystemWatcher(physicalPath)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName,
                IncludeSubdirectories = true,
                EnableRaisingEvents = true
            };

            _watcher.Created += (s, e) => EnqueueEvent(FileEventType.Created, e.FullPath);
            _watcher.Changed += (s, e) => EnqueueEvent(FileEventType.Modified, e.FullPath);
            _watcher.Deleted += (s, e) => EnqueueEvent(FileEventType.Deleted, e.FullPath);
            _watcher.Renamed += (s, e) => EnqueueEvent(FileEventType.Renamed, e.FullPath);

            Diagnostics.Log.Info($"文件监视已启动: {physicalPath}");
        }

        public void StopWatching()
        {
            _watcher?.Dispose();
            _watcher = null;
        }

        private void EnqueueEvent(FileEventType type, string physicalPath)
        {
            string virtualPath = PhysicalToVirtual(physicalPath) ?? physicalPath;
            var evt = new FileEvent(type, virtualPath, physicalPath);
            _fileEvents.Enqueue(evt);
            OnFileChanged?.Invoke(evt);
        }

        private string? PhysicalToVirtual(string physicalPath)
        {
            physicalPath = MountPoint.NormalizePath(physicalPath);
            lock (_mounts)
            {
                foreach (var mount in _mounts)
                {
                    string normalizedPhysical = MountPoint.NormalizePath(mount.PhysicalRoot);
                    if (physicalPath.StartsWith(normalizedPhysical, StringComparison.OrdinalIgnoreCase))
                    {
                        string relative = physicalPath.Substring(normalizedPhysical.Length).TrimStart('/');
                        return $"{mount.VirtualRoot}/{relative}";
                    }
                }
            }
            return null;
        }

        /// <summary>消费文件事件队列。</summary>
        public bool TryDequeueFileEvent(out FileEvent evt) => _fileEvents.TryDequeue(out evt);

        // ====== 缓存控制 ======
        public void ClearCache()
        {
            _readCache.Clear();
            _writeCache.Clear();
        }

        public void SetCacheEnabled(bool enabled) => _cacheEnabled = enabled;

        // ====== 内部请求类型 ======
        internal struct LoadRequest
        {
            public string Path;
            public LoadPriority Priority;
            public TaskCompletionSource<FileLoadResult> Completion;
        }

        // ====== 清理 ======
        public void Dispose()
        {
            StopWatching();
            _cts.Cancel();
            lock (_mounts)
            {
                foreach (var m in _mounts) m.Archive?.Dispose();
                _mounts.Clear();
            }
            ClearCache();
            if (Instance == this) Instance = null!;
        }
    }

    // ====== 扩展方法 ======
    public static class FileExtensions
    {
        /// <summary>快速读取 JSON 并反序列化。</summary>
        public static T? ReadJson<T>(this VirtualFileSystem vfs, string path) where T : class
        {
            var text = vfs.ReadText(path);
            return string.IsNullOrEmpty(text) ? null :
                System.Text.Json.JsonSerializer.Deserialize<T>(text);
        }

        /// <summary>快速写入 JSON。</summary>
        public static bool WriteJson<T>(this VirtualFileSystem vfs, string path, T obj) where T : class
        {
            var json = System.Text.Json.JsonSerializer.Serialize(obj,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            return vfs.WriteText(path, json);
        }

        /// <summary>读取文件作为 Stream。</summary>
        public static MemoryStream? ReadStream(this VirtualFileSystem vfs, string path)
        {
            var result = vfs.ReadFile(path);
            return result.Success ? new MemoryStream(result.Data) : null;
        }
    }
}
