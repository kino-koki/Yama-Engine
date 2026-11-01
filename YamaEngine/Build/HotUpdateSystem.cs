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
    // yama引擎热更新系统 (HotUpdateSystem)
    //
    // 提供完整的热更新能力：版本对比、差异补丁生成、多线程断点续传下载、
    // 补丁安装（备份→替换→验证→清理）、版本回滚与自动更新管理。
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>文件变更类型。</summary>
    public enum PatchFileChangeType { Added, Deleted, Modified, Renamed }

    /// <summary>热更新配置。</summary>
    public sealed class HotUpdateConfig
    {
        public string CurrentVersion { get; set; } = "1.0.0";
        public string ServerUrl { get; set; } = string.Empty;
        public string? VerifyKey { get; set; }
        public bool AutoDownload { get; set; } = false;
        public int DownloadTimeoutMs { get; set; } = 60000;
        public int MaxParallelDownloads { get; set; } = 4;
        public string PatchStorageDirectory { get; set; } = "Patches";
        public string BackupDirectory { get; set; } = "Backup";
        public string TempDownloadDirectory { get; set; } = "TempDownloads";
        public bool EnableResume { get; set; } = true;
        public int ChunkSize { get; set; } = 1024 * 1024;
    }

    /// <summary>单个文件的变更信息。</summary>
    public sealed class PatchFileEntry
    {
        public PatchFileChangeType ChangeType { get; set; }
        public string FilePath { get; set; } = string.Empty;
        public string NewHash { get; set; } = string.Empty;
        public string OldHash { get; set; } = string.Empty;
        public long SizeBytes { get; set; }
        public string DownloadUrl { get; set; } = string.Empty;
        public string? NewFilePath { get; set; }
    }

    /// <summary>版本差异补丁清单。</summary>
    public sealed class PatchManifest
    {
        public int FormatVersion { get; set; } = 1;
        public string FromVersion { get; set; } = string.Empty;
        public string ToVersion { get; set; } = string.Empty;
        public DateTime GeneratedAtUtc { get; set; } = DateTime.UtcNow;
        public long TotalPatchSizeBytes => Files.Sum(f => f.SizeBytes);
        public List<PatchFileEntry> Files { get; set; } = new();

        /// <summary>需要下载的文件数（排除删除）。</summary>
        public int DownloadFileCount => Files.Count(f => f.ChangeType != PatchFileChangeType.Deleted);

        public string ToJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        public static PatchManifest FromJson(string json) => JsonSerializer.Deserialize<PatchManifest>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        public void Save(string path) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, ToJson(), Encoding.UTF8); }
        public static PatchManifest Load(string path) => FromJson(File.ReadAllText(path, Encoding.UTF8));
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 补丁生成器
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 补丁生成器。对比两个版本的文件清单，生成差异补丁。
    /// </summary>
    public sealed class PatchGenerator
    {
        /// <summary>生成差异补丁。</summary>
        public PatchManifest Generate(string fromVersion, string toVersion, string oldDir, string newDir, string serverUrl)
        {
            var manifest = new PatchManifest { FromVersion = fromVersion, ToVersion = toVersion };

            var oldFiles = Directory.Exists(oldDir)
                ? Directory.EnumerateFiles(oldDir, "*.*", SearchOption.AllDirectories)
                    .ToDictionary(f => Path.GetRelativePath(oldDir, f).Replace('\\', '/'), f => ComputeHash(f))
                : new Dictionary<string, string>();

            var newFiles = Directory.Exists(newDir)
                ? Directory.EnumerateFiles(newDir, "*.*", SearchOption.AllDirectories)
                    .ToDictionary(f => Path.GetRelativePath(newDir, f).Replace('\\', '/'), f => ComputeHash(f))
                : new Dictionary<string, string>();

            // 新增 & 修改
            foreach (var (path, hash) in newFiles)
            {
                var fullPath = Path.Combine(newDir, path.Replace('/', Path.DirectorySeparatorChar));
                var entry = new PatchFileEntry
                {
                    FilePath = path,
                    NewHash = hash,
                    SizeBytes = new FileInfo(fullPath).Length,
                    DownloadUrl = $"{serverUrl.TrimEnd('/')}/{toVersion}/{path}"
                };

                if (oldFiles.TryGetValue(path, out var oldHash))
                {
                    if (oldHash != hash)
                    {
                        entry.ChangeType = PatchFileChangeType.Modified;
                        entry.OldHash = oldHash;
                        manifest.Files.Add(entry);
                    }
                    // 未变更则跳过
                }
                else
                {
                    entry.ChangeType = PatchFileChangeType.Added;
                    manifest.Files.Add(entry);
                }
            }

            // 删除
            foreach (var path in oldFiles.Keys.Except(newFiles.Keys))
            {
                manifest.Files.Add(new PatchFileEntry
                {
                    ChangeType = PatchFileChangeType.Deleted,
                    FilePath = path,
                    OldHash = oldFiles[path]
                });
            }

            return manifest;
        }

        private static string ComputeHash(string filePath)
        {
            using var sha = SHA256.Create();
            using var stream = File.OpenRead(filePath);
            return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 补丁下载器
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>单文件下载状态。</summary>
    public sealed class DownloadFileState
    {
        public string FilePath { get; set; } = string.Empty;
        public string DownloadUrl { get; set; } = string.Empty;
        public long TotalBytes { get; set; }
        public long DownloadedBytes { get; set; }
        public double Progress => TotalBytes > 0 ? (double)DownloadedBytes / TotalBytes : 0.0;
        public DownloadStatus Status { get; set; } = DownloadStatus.Pending;
        public string? Error { get; set; }
        public string TempFilePath { get; set; } = string.Empty;
    }

    public enum DownloadStatus { Pending, Downloading, Completed, Failed, Skipped }

    /// <summary>下载进度回调。</summary>
    public delegate void DownloadProgressDelegate(DownloadFileState state);

    /// <summary>下载结果。</summary>
    public sealed class PatchDownloadResult
    {
        public bool Success { get; set; }
        public int TotalFiles { get; set; }
        public int CompletedFiles { get; set; }
        public int FailedFiles { get; set; }
        public long TotalDownloadedBytes { get; set; }
        public double TotalDurationMs { get; set; }
        public List<DownloadFileState> FileStates { get; set; } = new();
        public List<string> Errors { get; set; } = new();
    }

    /// <summary>
    /// 补丁下载器。支持断点续传、多线程并行下载、进度追踪与完整性校验。
    /// </summary>
    public sealed class PatchDownloader
    {
        private readonly HotUpdateConfig _config;
        private DownloadProgressDelegate? _progressCallback;

        public PatchDownloader(HotUpdateConfig config) { _config = config; }
        public void SetProgressCallback(DownloadProgressDelegate callback) { _progressCallback = callback; }

        /// <summary>下载补丁中的所有文件。</summary>
        public async Task<PatchDownloadResult> DownloadAsync(PatchManifest manifest, CancellationToken ct = default)
        {
            var sw = Stopwatch.StartNew();
            var result = new PatchDownloadResult { TotalFiles = manifest.DownloadFileCount };
            var tempDir = _config.TempDownloadDirectory;
            Directory.CreateDirectory(tempDir);

            var toDownload = manifest.Files.Where(f => f.ChangeType != PatchFileChangeType.Deleted).ToList();
            var semaphore = new SemaphoreSlim(_config.MaxParallelDownloads);
            var states = new ConcurrentBag<DownloadFileState>();

            var tasks = toDownload.Select(async entry =>
            {
                await semaphore.WaitAsync(ct);
                try
                {
                    var state = await DownloadSingleFileAsync(entry, tempDir, ct);
                    states.Add(state);
                    _progressCallback?.Invoke(state);
                }
                finally { semaphore.Release(); }
            });

            await Task.WhenAll(tasks);
            result.FileStates = states.ToList();

            foreach (var s in result.FileStates)
            {
                if (s.Status == DownloadStatus.Completed) { result.CompletedFiles++; result.TotalDownloadedBytes += s.TotalBytes; }
                else if (s.Status == DownloadStatus.Failed) { result.FailedFiles++; result.Errors.Add($"{s.FilePath}: {s.Error}"); }
            }

            result.Success = result.FailedFiles == 0;
            sw.Stop();
            result.TotalDurationMs = sw.Elapsed.TotalMilliseconds;
            return result;
        }

        private async Task<DownloadFileState> DownloadSingleFileAsync(PatchFileEntry entry, string tempDir, CancellationToken ct)
        {
            var state = new DownloadFileState
            {
                FilePath = entry.FilePath,
                DownloadUrl = entry.DownloadUrl,
                TotalBytes = entry.SizeBytes,
                Status = DownloadStatus.Downloading,
                TempFilePath = Path.Combine(tempDir, entry.FilePath.Replace('/', Path.DirectorySeparatorChar))
            };

            Directory.CreateDirectory(Path.GetDirectoryName(state.TempFilePath)!);

            try
            {
                // 断点续传：检查已有临时文件大小
                long existingBytes = 0;
                if (_config.EnableResume && File.Exists(state.TempFilePath))
                {
                    existingBytes = new FileInfo(state.TempFilePath).Length;
                }

                // 使用 HttpClient 下载
                using var client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(_config.DownloadTimeoutMs) };
                var request = new HttpRequestMessage(HttpMethod.Get, state.DownloadUrl);

                if (_config.EnableResume && existingBytes > 0)
                {
                    request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(existingBytes, null);
                }

                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();

                var mode = existingBytes > 0 ? FileMode.Append : FileMode.Create;
                using var fs = new FileStream(state.TempFilePath, mode, FileAccess.Write, FileShare.None);
                using var responseStream = await response.Content.ReadAsStreamAsync(ct);
                var buffer = new byte[_config.ChunkSize];
                int bytesRead;

                while ((bytesRead = await responseStream.ReadAsync(buffer, ct)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    await fs.WriteAsync(buffer, 0, bytesRead, ct);
                    state.DownloadedBytes = existingBytes + fs.Position;
                    _progressCallback?.Invoke(state);
                }

                // 校验哈希
                var downloadedHash = ComputeHash(state.TempFilePath);
                if (!string.IsNullOrEmpty(entry.NewHash) && downloadedHash != entry.NewHash)
                {
                    state.Status = DownloadStatus.Failed;
                    state.Error = "Hash verification failed";
                    File.Delete(state.TempFilePath);
                    return state;
                }

                state.DownloadedBytes = new FileInfo(state.TempFilePath).Length;
                state.Status = DownloadStatus.Completed;
            }
            catch (OperationCanceledException)
            {
                state.Status = DownloadStatus.Failed;
                state.Error = "Download cancelled";
            }
            catch (Exception ex)
            {
                state.Status = DownloadStatus.Failed;
                state.Error = ex.Message;
            }

            return state;
        }

        private static string ComputeHash(string filePath)
        {
            using var sha = SHA256.Create();
            using var stream = File.OpenRead(filePath);
            return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 补丁安装器
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>安装结果。</summary>
    public sealed class PatchInstallResult
    {
        public bool Success { get; set; }
        public int InstalledFiles { get; set; }
        public int DeletedFiles { get; set; }
        public int FailedFiles { get; set; }
        public List<string> Errors { get; set; } = new();
    }

    /// <summary>
    /// 补丁安装器。执行流程：备份 → 替换 → 验证 → 清理。
    /// </summary>
    public sealed class PatchInstaller
    {
        private readonly HotUpdateConfig _config;

        public PatchInstaller(HotUpdateConfig config) { _config = config; }

        /// <summary>安装补丁。</summary>
        public PatchInstallResult Install(PatchManifest manifest, string installRoot, string downloadedDir)
        {
            var result = new PatchInstallResult();

            try
            {
                // 1. 创建备份
                Directory.CreateDirectory(_config.BackupDirectory);
                foreach (var entry in manifest.Files)
                {
                    if (entry.ChangeType == PatchFileChangeType.Added) continue;
                    var filePath = Path.Combine(installRoot, entry.FilePath.Replace('/', Path.DirectorySeparatorChar));
                    if (File.Exists(filePath))
                    {
                        var backupPath = Path.Combine(_config.BackupDirectory, entry.FilePath.Replace('/', Path.DirectorySeparatorChar));
                        Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
                        File.Copy(filePath, backupPath, true);
                    }
                }

                // 2. 应用变更
                foreach (var entry in manifest.Files)
                {
                    switch (entry.ChangeType)
                    {
                        case PatchFileChangeType.Added:
                        case PatchFileChangeType.Modified:
                            var srcPath = Path.Combine(downloadedDir, entry.FilePath.Replace('/', Path.DirectorySeparatorChar));
                            var destPath = Path.Combine(installRoot, entry.FilePath.Replace('/', Path.DirectorySeparatorChar));
                            if (!File.Exists(srcPath))
                            {
                                result.Errors.Add($"Downloaded file missing: {entry.FilePath}");
                                result.FailedFiles++;
                                continue;
                            }
                            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                            File.Copy(srcPath, destPath, true);
                            result.InstalledFiles++;
                            break;

                        case PatchFileChangeType.Deleted:
                            var delPath = Path.Combine(installRoot, entry.FilePath.Replace('/', Path.DirectorySeparatorChar));
                            if (File.Exists(delPath)) File.Delete(delPath);
                            result.DeletedFiles++;
                            break;

                        case PatchFileChangeType.Renamed:
                            if (entry.NewFilePath != null)
                            {
                                var oldPath = Path.Combine(installRoot, entry.FilePath.Replace('/', Path.DirectorySeparatorChar));
                                var newPath = Path.Combine(installRoot, entry.NewFilePath.Replace('/', Path.DirectorySeparatorChar));
                                Directory.CreateDirectory(Path.GetDirectoryName(newPath)!);
                                if (File.Exists(oldPath)) File.Move(oldPath, newPath);
                                result.InstalledFiles++;
                            }
                            break;
                    }
                }

                // 3. 验证
                foreach (var entry in manifest.Files)
                {
                    if (entry.ChangeType == PatchFileChangeType.Deleted) continue;
                    var verifyPath = Path.Combine(installRoot, entry.FilePath.Replace('/', Path.DirectorySeparatorChar));
                    if (File.Exists(verifyPath))
                    {
                        var hash = ComputeHash(verifyPath);
                        if (!string.IsNullOrEmpty(entry.NewHash) && hash != entry.NewHash)
                        {
                            result.Errors.Add($"Verification failed: {entry.FilePath}");
                            result.FailedFiles++;
                        }
                    }
                }

                result.Success = result.FailedFiles == 0;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Errors.Add($"Install error: {ex.Message}");
            }

            return result;
        }

        /// <summary>版本回滚：从备份还原。</summary>
        public bool Rollback(string installRoot)
        {
            if (!Directory.Exists(_config.BackupDirectory)) return false;
            try
            {
                foreach (var file in Directory.EnumerateFiles(_config.BackupDirectory, "*.*", SearchOption.AllDirectories))
                {
                    var relative = Path.GetRelativePath(_config.BackupDirectory, file).Replace('\\', '/');
                    var dest = Path.Combine(installRoot, relative.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    File.Copy(file, dest, true);
                }
                return true;
            }
            catch { return false; }
        }

        /// <summary>清理临时下载文件与备份。</summary>
        public void Cleanup()
        {
            try
            {
                if (Directory.Exists(_config.TempDownloadDirectory))
                    Directory.Delete(_config.TempDownloadDirectory, true);
                if (Directory.Exists(_config.BackupDirectory))
                    Directory.Delete(_config.BackupDirectory, true);
            }
            catch { }
        }

        private static string ComputeHash(string filePath)
        {
            using var sha = SHA256.Create();
            using var stream = File.OpenRead(filePath);
            return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 热更新管理器
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>更新检查结果。</summary>
    public sealed class UpdateCheckResult
    {
        public bool UpdateAvailable { get; set; }
        public string LatestVersion { get; set; } = string.Empty;
        public string CurrentVersion { get; set; } = string.Empty;
        public long PatchSizeBytes { get; set; }
        public string PatchDescription { get; set; } = string.Empty;
        public PatchManifest? Manifest { get; set; }
    }

    /// <summary>热更新状态。</summary>
    public enum HotUpdateState { Idle, Checking, Downloading, Installing, RollingBack, Completed, Failed }

    /// <summary>热更新状态变更回调。</summary>
    public delegate void HotUpdateStateDelegate(HotUpdateState state, string message);

    /// <summary>
    /// 热更新管理器。完整流程：检查更新 → 下载 → 安装 → 清理/重启。
    /// </summary>
    public sealed class HotUpdateManager
    {
        private readonly HotUpdateConfig _config;
        private HotUpdateState _state = HotUpdateState.Idle;
        private HotUpdateStateDelegate? _stateCallback;

        public HotUpdateState CurrentState => _state;

        public HotUpdateManager(HotUpdateConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
        }

        public void SetStateCallback(HotUpdateStateDelegate callback) { _stateCallback = callback; }

        private void SetState(HotUpdateState state, string msg = "")
        {
            _state = state;
            _stateCallback?.Invoke(state, msg);
        }

        /// <summary>检查服务器是否有可用更新。</summary>
        public async Task<UpdateCheckResult> CheckForUpdateAsync()
        {
            SetState(HotUpdateState.Checking, "Checking for updates...");
            var result = new UpdateCheckResult { CurrentVersion = _config.CurrentVersion };

            try
            {
                var manifestUrl = $"{_config.ServerUrl.TrimEnd('/')}/latest_manifest.json";
                using var client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(_config.DownloadTimeoutMs) };
                var json = await client.GetStringAsync(manifestUrl);

                // 解析服务器版本信息（简化）
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("version", out var versionEl))
                {
                    result.LatestVersion = versionEl.GetString() ?? "";
                }
                if (doc.RootElement.TryGetProperty("patchUrl", out var patchUrlEl))
                {
                    var patchUrl = patchUrlEl.GetString();
                    if (!string.IsNullOrEmpty(patchUrl))
                    {
                        var patchJson = await client.GetStringAsync(patchUrl);
                        result.Manifest = PatchManifest.FromJson(patchJson);
                        result.PatchSizeBytes = result.Manifest.TotalPatchSizeBytes;
                    }
                }

                result.UpdateAvailable = !string.IsNullOrEmpty(result.LatestVersion) &&
                                         result.LatestVersion != _config.CurrentVersion;
            }
            catch (Exception ex)
            {
                result.UpdateAvailable = false;
            }

            SetState(HotUpdateState.Idle);
            return result;
        }

        /// <summary>执行完整的热更新流程。</summary>
        public async Task<bool> ExecuteUpdateAsync(PatchManifest manifest, string installRoot, CancellationToken ct = default)
        {
            // 1. 下载
            SetState(HotUpdateState.Downloading, "Downloading patch...");
            var downloader = new PatchDownloader(_config);
            var downloadResult = await downloader.DownloadAsync(manifest, ct);
            if (!downloadResult.Success)
            {
                SetState(HotUpdateState.Failed, $"Download failed: {downloadResult.Errors.Count} files failed.");
                return false;
            }

            // 2. 安装
            SetState(HotUpdateState.Installing, "Installing patch...");
            var installer = new PatchInstaller(_config);
            var installResult = installer.Install(manifest, installRoot, _config.TempDownloadDirectory);
            if (!installResult.Success)
            {
                // 安装失败，自动回滚
                SetState(HotUpdateState.RollingBack, "Rolling back...");
                installer.Rollback(installRoot);
                SetState(HotUpdateState.Failed, "Install failed, rolled back.");
                return false;
            }

            // 3. 清理
            installer.Cleanup();

            // 4. 更新版本号
            _config.CurrentVersion = manifest.ToVersion;
            SetState(HotUpdateState.Completed, $"Updated to {manifest.ToVersion}");
            return true;
        }
    }
}
