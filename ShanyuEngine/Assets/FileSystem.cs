using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ShanyuEngine.Assets
{
    // ====================================================================
    // VFileSystem — 虚拟文件系统
    // ====================================================================
    // 统一文件访问接口，支持项目目录、Pak 文件（zip-like 打包）、内存文件。
    // 通过挂载点（MountPoint）系统，将不同来源的文件统一到一个逻辑命名空间下。
    // ====================================================================

    /// <summary>
    /// 挂载点类型，决定文件来源。
    /// </summary>
    public enum MountType
    {
        /// <summary>本地磁盘目录</summary>
        Directory,
        /// <summary>Pak 打包文件（zip 格式）</summary>
        PakFile,
        /// <summary>内存文件系统</summary>
        Memory
    }

    /// <summary>
    /// 单个挂载点的描述信息。
    /// </summary>
    public class MountPoint
    {
        /// <summary>挂载的虚拟路径前缀，例如 "/engine/" 或 "/assets/"</summary>
        public string VirtualPath { get; }

        /// <summary>挂载点类型</summary>
        public MountType Type { get; }

        /// <summary>物理根路径（目录挂载时为目录路径，Pak 时为 .pak 文件路径）</summary>
        public string PhysicalPath { get; }

        /// <summary>挂载优先级（数字越大优先级越高，优先匹配）</summary>
        public int Priority { get; set; }

        /// <summary>此挂载点是否激活</summary>
        public bool IsEnabled { get; set; } = true;

        /// <summary>Pak 文件对应的 ZipArchive（Pak 类型专用）</summary>
        internal ZipArchive? PakArchive;

        /// <summary>内存文件存储（Memory 类型专用）</summary>
        internal ConcurrentDictionary<string, byte[]> MemoryFiles = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>内存文件元数据（Memory 类型专用）</summary>
        internal ConcurrentDictionary<string, DateTime> MemoryFileTimestamps = new(StringComparer.OrdinalIgnoreCase);

        public MountPoint(string virtualPath, string physicalPath, MountType type, int priority = 0)
        {
            VirtualPath = NormalizeVirtualPath(virtualPath);
            PhysicalPath = physicalPath;
            Type = type;
            Priority = priority;
        }

        private static string NormalizeVirtualPath(string path)
        {
            path = path.Replace('\\', '/');
            if (!path.StartsWith("/")) path = "/" + path;
            if (!path.EndsWith("/")) path += "/";
            return path;
        }
    }

    /// <summary>
    /// VFileSystem —— 引擎虚拟文件系统。
    /// 提供统一的 ReadAllBytes / ReadAllText / Exists / Enumerate / ReadAsync 接口。
    /// </summary>
    public sealed class VFileSystem : IDisposable
    {
        private readonly List<MountPoint> _mountPoints = new();
        private readonly ReaderWriterLockSlim _lock = new();

        /// <summary>挂载点数量</summary>
        public int MountCount
        {
            get
            {
                _lock.EnterReadLock();
                try { return _mountPoints.Count; }
                finally { _lock.ExitReadLock(); }
            }
        }

        // ──────────────────────────────────────────────────────────
        // 挂载 / 卸载
        // ──────────────────────────────────────────────────────────

        /// <summary>
        /// 挂载一个本地磁盘目录到虚拟路径。
        /// </summary>
        public MountPoint MountDirectory(string virtualPath, string physicalDirectory, int priority = 0)
        {
            var mount = new MountPoint(virtualPath, physicalDirectory, MountType.Directory, priority);
            Mount(mount);
            return mount;
        }

        /// <summary>
        /// 挂载一个 Pak 文件（zip 格式）到虚拟路径。
        /// </summary>
        public MountPoint MountPakFile(string virtualPath, string pakFilePath, int priority = 10)
        {
            var stream = new FileStream(pakFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
            var mount = new MountPoint(virtualPath, pakFilePath, MountType.PakFile, priority)
            {
                PakArchive = archive
            };
            Mount(mount);
            return mount;
        }

        /// <summary>
        /// 创建（或获取）一个内存文件挂载点。
        /// </summary>
        public MountPoint MountMemory(string virtualPath, int priority = 20)
        {
            var mount = new MountPoint(virtualPath, "<memory>", MountType.Memory, priority);
            Mount(mount);
            return mount;
        }

        /// <summary>
        /// 注册一个已创建的挂载点。
        /// </summary>
        public void Mount(MountPoint mount)
        {
            _lock.EnterWriteLock();
            try
            {
                _mountPoints.Add(mount);
                _mountPoints.Sort((a, b) => b.Priority.CompareTo(a.Priority));
            }
            finally { _lock.ExitWriteLock(); }
        }

        /// <summary>
        /// 卸载指定挂载点，并释放 Pak 文件句柄等资源。
        /// </summary>
        public bool Unmount(MountPoint mount)
        {
            _lock.EnterWriteLock();
            try
            {
                bool removed = _mountPoints.Remove(mount);
                if (removed)
                {
                    mount.PakArchive?.Dispose();
                    mount.PakArchive = null;
                    mount.IsEnabled = false;
                }
                return removed;
            }
            finally { _lock.ExitWriteLock(); }
        }

        // ──────────────────────────────────────────────────────────
        // 路径解析
        // ──────────────────────────────────────────────────────────

        /// <summary>
        /// 将虚拟路径规范化：统一分隔符、去掉冗余。
        /// </summary>
        public static string NormalizePath(string path)
        {
            path = path.Replace('\\', '/');
            // 去除开头的 ./ 
            while (path.StartsWith("./")) path = path.Substring(2);
            if (!path.StartsWith("/")) path = "/" + path;
            // 合并冗余的 //
            while (path.Contains("//")) path = path.Replace("//", "/");
            // 处理 ..
            var parts = path.Split('/');
            var stack = new List<string>();
            foreach (var part in parts)
            {
                if (string.IsNullOrEmpty(part) || part == ".") continue;
                if (part == "..")
                {
                    if (stack.Count > 1) stack.RemoveAt(stack.Count - 1);
                }
                else
                {
                    stack.Add(part);
                }
            }
            return string.Join("/", stack);
        }

        // ──────────────────────────────────────────────────────────
        // 文件读取
        // ──────────────────────────────────────────────────────────

        /// <summary>
        /// 判断文件是否存在。
        /// </summary>
        public bool Exists(string virtualPath)
        {
            virtualPath = NormalizePath(virtualPath);

            _lock.EnterReadLock();
            try
            {
                foreach (var mount in _mountPoints)
                {
                    if (!mount.IsEnabled) continue;
                    if (!virtualPath.StartsWith(mount.VirtualPath, StringComparison.OrdinalIgnoreCase))
                        continue;

                    string relativePath = virtualPath.Substring(mount.VirtualPath.Length).TrimStart('/');

                    switch (mount.Type)
                    {
                        case MountType.Directory:
                            var fullPath = Path.Combine(mount.PhysicalPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
                            if (File.Exists(fullPath)) return true;
                            break;

                        case MountType.PakFile:
                            if (mount.PakArchive != null)
                            {
                                var entry = mount.PakArchive.GetEntry(relativePath);
                                if (entry != null) return true;
                            }
                            break;

                        case MountType.Memory:
                            if (mount.MemoryFiles.ContainsKey(relativePath)) return true;
                            break;
                    }
                }
            }
            finally { _lock.ExitReadLock(); }

            return false;
        }

        /// <summary>
        /// 读取全部字节。
        /// </summary>
        public byte[] ReadAllBytes(string virtualPath)
        {
            virtualPath = NormalizePath(virtualPath);

            _lock.EnterReadLock();
            try
            {
                foreach (var mount in _mountPoints)
                {
                    if (!mount.IsEnabled) continue;
                    if (!virtualPath.StartsWith(mount.VirtualPath, StringComparison.OrdinalIgnoreCase))
                        continue;

                    string relativePath = virtualPath.Substring(mount.VirtualPath.Length).TrimStart('/');

                    switch (mount.Type)
                    {
                        case MountType.Directory:
                            var fullPath = Path.Combine(mount.PhysicalPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
                            if (File.Exists(fullPath)) return File.ReadAllBytes(fullPath);
                            break;

                        case MountType.PakFile:
                            if (mount.PakArchive != null)
                            {
                                var entry = mount.PakArchive.GetEntry(relativePath);
                                if (entry != null)
                                {
                                    using var stream = entry.Open();
                                    using var ms = new MemoryStream();
                                    stream.CopyTo(ms);
                                    return ms.ToArray();
                                }
                            }
                            break;

                        case MountType.Memory:
                            if (mount.MemoryFiles.TryGetValue(relativePath, out var data))
                                return data;
                            break;
                    }
                }
            }
            finally { _lock.ExitReadLock(); }

            throw new FileNotFoundException($"VFileSystem: 文件未找到: '{virtualPath}'");
        }

        /// <summary>
        /// 读取全部文本（UTF-8）。
        /// </summary>
        public string ReadAllText(string virtualPath)
        {
            return System.Text.Encoding.UTF8.GetString(ReadAllBytes(virtualPath));
        }

        /// <summary>
        /// 异步读取全部字节。
        /// </summary>
        public async Task<byte[]> ReadAsync(string virtualPath, CancellationToken cancellationToken = default)
        {
            virtualPath = NormalizePath(virtualPath);

            byte[]? result = null;

            _lock.EnterReadLock();
            MountPoint? matchedMount = null;
            string? relativePath = null;

            try
            {
                foreach (var mount in _mountPoints)
                {
                    if (!mount.IsEnabled) continue;
                    if (!virtualPath.StartsWith(mount.VirtualPath, StringComparison.OrdinalIgnoreCase))
                        continue;

                    relativePath = virtualPath.Substring(mount.VirtualPath.Length).TrimStart('/');

                    switch (mount.Type)
                    {
                        case MountType.Directory:
                            var fullPath = Path.Combine(mount.PhysicalPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
                            if (File.Exists(fullPath))
                            {
                                matchedMount = mount;
                                // 释放锁前先捕获路径
                                relativePath = fullPath;
                            }
                            break;

                        case MountType.PakFile:
                            if (mount.PakArchive != null)
                            {
                                var entry = mount.PakArchive.GetEntry(relativePath);
                                if (entry != null)
                                {
                                    matchedMount = mount;
                                    relativePath = relativePath; // 保持相对路径
                                }
                            }
                            break;

                        case MountType.Memory:
                            if (mount.MemoryFiles.TryGetValue(relativePath, out var data))
                            {
                                result = data;
                            }
                            break;
                    }

                    if (result != null || matchedMount != null) break;
                }
            }
            finally { _lock.ExitReadLock(); }

            if (result != null) return result;

            if (matchedMount != null && relativePath != null)
            {
                switch (matchedMount.Type)
                {
                    case MountType.Directory:
                        return await File.ReadAllBytesAsync(relativePath, cancellationToken).ConfigureAwait(false);

                    case MountType.PakFile:
                        if (matchedMount.PakArchive != null)
                        {
                            var entry = matchedMount.PakArchive.GetEntry(relativePath);
                            if (entry != null)
                            {
                                await using var stream = entry.Open();
                                using var ms = new MemoryStream();
                                await stream.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
                                return ms.ToArray();
                            }
                        }
                        break;
                }
            }

            throw new FileNotFoundException($"VFileSystem: 文件未找到: '{virtualPath}'");
        }

        /// <summary>
        /// 异步读取全部文本（UTF-8）。
        /// </summary>
        public async Task<string> ReadTextAsync(string virtualPath, CancellationToken cancellationToken = default)
        {
            var bytes = await ReadAsync(virtualPath, cancellationToken).ConfigureAwait(false);
            return System.Text.Encoding.UTF8.GetString(bytes);
        }

        // ──────────────────────────────────────────────────────────
        // 枚举与目录
        // ──────────────────────────────────────────────────────────

        /// <summary>
        /// 枚举虚拟路径下所有文件（递归）。
        /// 返回的是完整的虚拟路径列表。
        /// </summary>
        public List<string> EnumerateFiles(string virtualDirectory, string searchPattern = "*",
            bool recursive = true)
        {
            virtualDirectory = NormalizePath(virtualDirectory);
            var results = new List<string>();

            _lock.EnterReadLock();
            try
            {
                foreach (var mount in _mountPoints)
                {
                    if (!mount.IsEnabled) continue;
                    if (!virtualDirectory.StartsWith(mount.VirtualPath, StringComparison.OrdinalIgnoreCase))
                        continue;

                    string relativeDir = virtualDirectory.Substring(mount.VirtualPath.Length).TrimStart('/');

                    switch (mount.Type)
                    {
                        case MountType.Directory:
                            var fullDir = Path.Combine(mount.PhysicalPath, relativeDir.Replace('/', Path.DirectorySeparatorChar));
                            if (Directory.Exists(fullDir))
                            {
                                var option = recursive
                                    ? SearchOption.AllDirectories
                                    : SearchOption.TopDirectoryOnly;
                                var files = Directory.EnumerateFiles(fullDir, searchPattern, option);
                                foreach (var file in files)
                                {
                                    string rel = Path.GetRelativePath(mount.PhysicalPath, file)
                                        .Replace('\\', '/');
                                    results.Add(mount.VirtualPath + rel);
                                }
                            }
                            break;

                        case MountType.PakFile:
                            if (mount.PakArchive != null)
                            {
                                var prefix = string.IsNullOrEmpty(relativeDir) ? "" : relativeDir + "/";
                                foreach (var entry in mount.PakArchive.Entries)
                                {
                                    if (entry.FullName.EndsWith('/')) continue; // 跳过目录
                                    if (!entry.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                                    if (!recursive)
                                    {
                                        string subPath = entry.FullName.Substring(prefix.Length);
                                        if (subPath.Contains('/')) continue;
                                    }
                                    if (!MatchesPattern(entry.FullName, searchPattern)) continue;
                                    results.Add(mount.VirtualPath + entry.FullName);
                                }
                            }
                            break;

                        case MountType.Memory:
                            string memPrefix = string.IsNullOrEmpty(relativeDir) ? "" : relativeDir + "/";
                            foreach (var kvp in mount.MemoryFiles)
                            {
                                if (!kvp.Key.StartsWith(memPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                                if (!recursive)
                                {
                                    string subPath = kvp.Key.Substring(memPrefix.Length);
                                    if (subPath.Contains('/')) continue;
                                }
                                if (!MatchesPattern(kvp.Key, searchPattern)) continue;
                                results.Add(mount.VirtualPath + kvp.Key);
                            }
                            break;
                    }
                }
            }
            finally { _lock.ExitReadLock(); }

            return results;
        }

        /// <summary>
        /// 枚举虚拟路径下所有子目录。
        /// </summary>
        public List<string> EnumerateDirectories(string virtualDirectory)
        {
            virtualDirectory = NormalizePath(virtualDirectory);
            var dirSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            _lock.EnterReadLock();
            try
            {
                foreach (var mount in _mountPoints)
                {
                    if (!mount.IsEnabled) continue;
                    if (!virtualDirectory.StartsWith(mount.VirtualPath, StringComparison.OrdinalIgnoreCase))
                        continue;

                    string relativeDir = virtualDirectory.Substring(mount.VirtualPath.Length).TrimStart('/');

                    switch (mount.Type)
                    {
                        case MountType.Directory:
                            var fullDir = Path.Combine(mount.PhysicalPath, relativeDir.Replace('/', Path.DirectorySeparatorChar));
                            if (Directory.Exists(fullDir))
                            {
                                foreach (var dir in Directory.EnumerateDirectories(fullDir))
                                {
                                    string rel = Path.GetRelativePath(mount.PhysicalPath, dir).Replace('\\', '/');
                                    dirSet.Add(mount.VirtualPath + rel);
                                }
                            }
                            break;

                        case MountType.PakFile:
                        case MountType.Memory:
                            // 从文件路径中提取目录
                            var files = mount.Type == MountType.PakFile
                                ? mount.PakArchive?.Entries.Select(e => e.FullName).ToList()
                                : mount.MemoryFiles.Keys.ToList();
                            if (files != null)
                            {
                                string prefix = string.IsNullOrEmpty(relativeDir) ? "" : relativeDir + "/";
                                foreach (var f in files)
                                {
                                    if (!f.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                                    int slashIdx = f.IndexOf('/', prefix.Length);
                                    if (slashIdx < 0) continue;
                                    string subDir = f.Substring(0, slashIdx);
                                    dirSet.Add(mount.VirtualPath + subDir);
                                }
                            }
                            break;
                    }
                }
            }
            finally { _lock.ExitReadLock(); }

            return dirSet.ToList();
        }

        // ──────────────────────────────────────────────────────────
        // 内存文件写入
        // ──────────────────────────────────────────────────────────

        /// <summary>
        /// 向指定的内存挂载点写入文件。如果挂载点不是 Memory 类型则抛异常。
        /// </summary>
        public void WriteMemoryFile(MountPoint memoryMount, string relativePath, byte[] data)
        {
            if (memoryMount.Type != MountType.Memory)
                throw new ArgumentException("只能向 Memory 类型的挂载点写入文件。", nameof(memoryMount));
            if (!memoryMount.IsEnabled)
                throw new InvalidOperationException("挂载点已禁用。");

            relativePath = relativePath.Replace('\\', '/').TrimStart('/');
            memoryMount.MemoryFiles[relativePath] = data;
            memoryMount.MemoryFileTimestamps[relativePath] = DateTime.UtcNow;
        }

        /// <summary>
        /// 从内存挂载点删除文件。
        /// </summary>
        public bool DeleteMemoryFile(MountPoint memoryMount, string relativePath)
        {
            if (memoryMount.Type != MountType.Memory) return false;
            relativePath = relativePath.Replace('\\', '/').TrimStart('/');
            memoryMount.MemoryFiles.TryRemove(relativePath, out _);
            memoryMount.MemoryFileTimestamps.TryRemove(relativePath, out _);
            return true;
        }

        // ──────────────────────────────────────────────────────────
        // 引擎内容目录自动挂载
        // ──────────────────────────────────────────────────────────

        private static VFileSystem? _instance;
        private static readonly object _instanceLock = new();

        /// <summary>
        /// 获取或初始化全局 VFileSystem 单例。
        /// 自动挂载引擎内容目录。
        /// </summary>
        public static VFileSystem Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_instanceLock)
                    {
                        if (_instance == null)
                        {
                            _instance = new VFileSystem();
                            _instance.InitializeEngineMounts();
                        }
                    }
                }
                return _instance;
            }
        }

        /// <summary>
        /// 初始化引擎默认挂载点。
        /// 自动查找引擎 Content 目录、当前工作目录，进行挂载。
        /// </summary>
        private void InitializeEngineMounts()
        {
            // 1. 挂载当前工作目录作为项目根
            var projectDir = Environment.CurrentDirectory;
            if (Directory.Exists(projectDir))
            {
                MountDirectory("/", projectDir, priority: 0);
            }

            // 2. 挂载引擎内置 Content 目录
            var engineDir = AppContext.BaseDirectory;
            var contentDir = Path.Combine(engineDir, "Content");
            if (Directory.Exists(contentDir))
            {
                MountDirectory("/engine/content/", contentDir, priority: 10);
            }

            // 3. 挂载 Assets 子目录
            var assetsDir = Path.Combine(engineDir, "Assets");
            if (Directory.Exists(assetsDir))
            {
                MountDirectory("/assets/", assetsDir, priority: 5);
            }

            // 4. 创建内存文件系统挂载点
            MountMemory("/memory/", priority: 20);
        }

        // ──────────────────────────────────────────────────────────
        // 辅助
        // ──────────────────────────────────────────────────────────

        private static bool MatchesPattern(string fileName, string pattern)
        {
            // 简单的通配符匹配：* 和 ?
            if (pattern == "*" || pattern == "*.*") return true;

            int wi = 0, pi = 0;
            int starIdx = -1, matchIdx = 0;

            while (wi < fileName.Length)
            {
                if (pi < pattern.Length &&
                    (char.ToLowerInvariant(pattern[pi]) == char.ToLowerInvariant(fileName[wi]) ||
                     pattern[pi] == '?'))
                {
                    wi++;
                    pi++;
                }
                else if (pi < pattern.Length && pattern[pi] == '*')
                {
                    starIdx = pi;
                    matchIdx = wi;
                    pi++;
                }
                else if (starIdx >= 0)
                {
                    pi = starIdx + 1;
                    matchIdx++;
                    wi = matchIdx;
                }
                else
                {
                    return false;
                }
            }

            while (pi < pattern.Length && pattern[pi] == '*') pi++;

            return pi == pattern.Length;
        }

        // ──────────────────────────────────────────────────────────
        // IDisposable
        // ──────────────────────────────────────────────────────────

        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            foreach (var mount in _mountPoints)
            {
                mount.PakArchive?.Dispose();
                mount.PakArchive = null;
                mount.MemoryFiles.Clear();
                mount.MemoryFileTimestamps.Clear();
            }
            _mountPoints.Clear();
            _lock.Dispose();
        }
    }
}
