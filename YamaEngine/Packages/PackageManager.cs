using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace YamaEngine.Packages
{
    // ======================== 枚举 ========================

    public enum PackageContentType
    {
        Plugin,
        Asset,
        Script,
        Shader,
        Map,
        Template,
        Tool,
        Documentation
    }

    public enum PackageSourceType
    {
        Local,
        Remote,
        Git
    }

    // ======================== 版本解析 ========================

    /// <summary>
    /// SemVer 解析与范围匹配。支持 ^, ~, >=, <=, >, <, = 运算符。
    /// </summary>
    public class PackageVersion : IComparable<PackageVersion>
    {
        public int Major { get; set; }
        public int Minor { get; set; }
        public int Patch { get; set; }
        public string Prerelease { get; set; }
        public string Build { get; set; }

        public PackageVersion(int major, int minor, int patch, string prerelease = null, string build = null)
        {
            Major = major; Minor = minor; Patch = patch;
            Prerelease = prerelease; Build = build;
        }

        public static PackageVersion Parse(string version)
        {
            if (string.IsNullOrWhiteSpace(version))
                return new PackageVersion(0, 0, 0);
            var s = version.Trim();
            if (s.StartsWith("v")) s = s.Substring(1);
            var prerelease = "";
            var build = "";
            int plus = s.IndexOf('+');
            if (plus >= 0) { build = s.Substring(plus + 1); s = s.Substring(0, plus); }
            int dash = s.IndexOf('-');
            if (dash >= 0) { prerelease = s.Substring(dash + 1); s = s.Substring(0, dash); }
            var parts = s.Split('.');
            int major = parts.Length > 0 && int.TryParse(parts[0], out var m1) ? m1 : 0;
            int minor = parts.Length > 1 && int.TryParse(parts[1], out var m2) ? m2 : 0;
            int patch = parts.Length > 2 && int.TryParse(parts[2], out var m3) ? m3 : 0;
            return new PackageVersion(major, minor, patch,
                string.IsNullOrEmpty(prerelease) ? null : prerelease,
                string.IsNullOrEmpty(build) ? null : build);
        }

        public bool Satisfies(string range)
        {
            if (string.IsNullOrWhiteSpace(range) || range == "*") return true;
            var trimmed = range.Trim();
            if (trimmed.StartsWith("^"))
            {
                var min = Parse(trimmed.Substring(1));
                var max = new PackageVersion(min.Major + 1, 0, 0);
                return this >= min && this < max && string.IsNullOrEmpty(Prerelease);
            }
            if (trimmed.StartsWith("~"))
            {
                var min = Parse(trimmed.Substring(1));
                var max = new PackageVersion(min.Major, min.Minor + 1, 0);
                return this >= min && this < max;
            }
            if (trimmed.StartsWith(">="))
            {
                var target = Parse(trimmed.Substring(2));
                return this >= target;
            }
            if (trimmed.StartsWith("<="))
            {
                var target = Parse(trimmed.Substring(2));
                return this <= target;
            }
            if (trimmed.StartsWith(">"))
            {
                var target = Parse(trimmed.Substring(1));
                return this > target;
            }
            if (trimmed.StartsWith("<"))
            {
                var target = Parse(trimmed.Substring(1));
                return this < target;
            }
            return this == Parse(trimmed);
        }

        public int CompareTo(PackageVersion other)
        {
            if (other == null) return 1;
            int cmp = Major.CompareTo(other.Major);
            if (cmp != 0) return cmp;
            cmp = Minor.CompareTo(other.Minor);
            if (cmp != 0) return cmp;
            cmp = Patch.CompareTo(other.Patch);
            if (cmp != 0) return cmp;
            if (string.IsNullOrEmpty(Prerelease) && !string.IsNullOrEmpty(other.Prerelease)) return 1;
            if (!string.IsNullOrEmpty(Prerelease) && string.IsNullOrEmpty(other.Prerelease)) return -1;
            return string.Compare(Prerelease, other.Prerelease, StringComparison.Ordinal);
        }

        public override bool Equals(object obj) => obj is PackageVersion v && Major == v.Major && Minor == v.Minor && Patch == v.Patch && Prerelease == v.Prerelease;
        public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, Prerelease);
        public static bool operator ==(PackageVersion a, PackageVersion b) => a?.Equals(b) ?? b is null;
        public static bool operator !=(PackageVersion a, PackageVersion b) => !(a == b);
        public static bool operator <(PackageVersion a, PackageVersion b) => a.CompareTo(b) < 0;
        public static bool operator >(PackageVersion a, PackageVersion b) => a.CompareTo(b) > 0;
        public static bool operator <=(PackageVersion a, PackageVersion b) => a.CompareTo(b) <= 0;
        public static bool operator >=(PackageVersion a, PackageVersion b) => a.CompareTo(b) >= 0;
        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.Append($"{Major}.{Minor}.{Patch}");
            if (!string.IsNullOrEmpty(Prerelease)) sb.Append($"-{Prerelease}");
            if (!string.IsNullOrEmpty(Build)) sb.Append($"+{Build}");
            return sb.ToString();
        }
    }

    // ======================== 包内容 ========================

    public class PackageContent
    {
        public PackageContentType Type { get; set; }
        public string Path { get; set; }
        public string Checksum { get; set; }
        public ulong Size { get; set; }
    }

    public class DependencyInfo
    {
        public string Name { get; set; }
        public string VersionRange { get; set; }
        public bool Optional { get; set; }
        public string Source { get; set; }
    }

    // ======================== 清单 ========================

    public class PackageManifest
    {
        public string Name { get; set; }
        public string Version { get; set; }
        public string Description { get; set; }
        public string Author { get; set; }
        public string License { get; set; }
        public List<string> Tags { get; set; } = new List<string>();
        public List<DependencyInfo> Dependencies { get; set; } = new List<DependencyInfo>();
        public List<PackageContent> Contents { get; set; } = new List<PackageContent>();
        public Dictionary<string, string> Metadata { get; set; } = new Dictionary<string, string>();

        [JsonIgnore]
        public PackageVersion ParsedVersion => PackageVersion.Parse(Version);

        public static PackageManifest FromJson(string json) => JsonSerializer.Deserialize<PackageManifest>(json);
        public string ToJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
    }

    // ======================== 包源 ========================

    public abstract class PackageSource
    {
        public string Name { get; set; }
        public PackageSourceType SourceType { get; protected set; }
        public abstract Task<Stream> DownloadAsync(string packageName, string version);
        public abstract Task<PackageManifest> GetManifestAsync(string packageName, string version);
        public abstract Task<List<string>> ListVersionsAsync(string packageName);
        public abstract Task<List<string>> SearchAsync(string query);
    }

    public class LocalSource : PackageSource
    {
        public string RootDirectory { get; set; }

        public LocalSource(string rootDirectory)
        {
            RootDirectory = rootDirectory;
            SourceType = PackageSourceType.Local;
        }

        public override Task<Stream> DownloadAsync(string packageName, string version)
        {
            var path = GetPackagePath(packageName, version);
            if (!File.Exists(path))
                throw new FileNotFoundException($"Package not found: {path}");
            return Task.FromResult<Stream>(File.OpenRead(path));
        }

        public override Task<PackageManifest> GetManifestAsync(string packageName, string version)
        {
            var dir = Path.Combine(RootDirectory, packageName, version);
            var manifestPath = Path.Combine(dir, "manifest.json");
            if (!File.Exists(manifestPath))
                return Task.FromResult<PackageManifest>(null);
            var json = File.ReadAllText(manifestPath);
            return Task.FromResult(PackageManifest.FromJson(json));
        }

        public override Task<List<string>> ListVersionsAsync(string packageName)
        {
            var dir = Path.Combine(RootDirectory, packageName);
            if (!Directory.Exists(dir))
                return Task.FromResult(new List<string>());
            var versions = Directory.GetDirectories(dir).Select(Path.GetFileName).ToList();
            return Task.FromResult(versions);
        }

        public override Task<List<string>> SearchAsync(string query)
        {
            if (!Directory.Exists(RootDirectory))
                return Task.FromResult(new List<string>());
            var results = Directory.GetDirectories(RootDirectory)
                .Where(d => Path.GetFileName(d).Contains(query, StringComparison.OrdinalIgnoreCase))
                .Select(Path.GetFileName)
                .ToList();
            return Task.FromResult(results);
        }

        private string GetPackagePath(string packageName, string version)
        {
            return Path.Combine(RootDirectory, packageName, version, $"{packageName}-{version}.zip");
        }
    }

    public class RemoteSource : PackageSource
    {
        public string BaseUrl { get; set; }
        private readonly HttpClient _client;

        public RemoteSource(string baseUrl)
        {
            BaseUrl = baseUrl.TrimEnd('/');
            SourceType = PackageSourceType.Remote;
            _client = new HttpClient();
        }

        public override async Task<Stream> DownloadAsync(string packageName, string version)
        {
            var url = $"{BaseUrl}/packages/{packageName}/{version}/download";
            var response = await _client.GetAsync(url);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStreamAsync();
        }

        public override async Task<PackageManifest> GetManifestAsync(string packageName, string version)
        {
            var url = $"{BaseUrl}/packages/{packageName}/{version}/manifest.json";
            var json = await _client.GetStringAsync(url);
            return PackageManifest.FromJson(json);
        }

        public override async Task<List<string>> ListVersionsAsync(string packageName)
        {
            var url = $"{BaseUrl}/packages/{packageName}/versions";
            var json = await _client.GetStringAsync(url);
            return JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
        }

        public override async Task<List<string>> SearchAsync(string query)
        {
            var url = $"{BaseUrl}/search?q={Uri.EscapeDataString(query)}";
            var json = await _client.GetStringAsync(url);
            return JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
        }
    }

    public class GitSource : PackageSource
    {
        public string RepositoryUrl { get; set; }
        public string Branch { get; set; } = "main";

        public GitSource(string repositoryUrl)
        {
            RepositoryUrl = repositoryUrl;
            SourceType = PackageSourceType.Git;
        }

        public override Task<Stream> DownloadAsync(string packageName, string version)
        {
            // In real implementation: clone / archive download
            return Task.FromResult<Stream>(null);
        }

        public override Task<PackageManifest> GetManifestAsync(string packageName, string version)
        {
            return Task.FromResult<PackageManifest>(null);
        }

        public override Task<List<string>> ListVersionsAsync(string packageName)
        {
            // List git tags
            return Task.FromResult(new List<string>());
        }

        public override Task<List<string>> SearchAsync(string query)
        {
            return Task.FromResult(new List<string>());
        }
    }

    // ======================== 锁定文件 ========================

    public class PackageLockEntry
    {
        public string Name { get; set; }
        public string Version { get; set; }
        public string Resolved { get; set; }
        public string Integrity { get; set; }
        public List<string> Dependencies { get; set; } = new List<string>();
    }

    public class PackageLock
    {
        public int LockfileVersion { get; set; } = 1;
        public Dictionary<string, PackageLockEntry> Packages { get; set; } = new Dictionary<string, PackageLockEntry>();

        public static PackageLock Load(string path)
        {
            if (!File.Exists(path))
                return new PackageLock();
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<PackageLock>(json) ?? new PackageLock();
        }

        public void Save(string path)
        {
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }

        public void AddOrUpdate(PackageLockEntry entry)
        {
            Packages[entry.Name] = entry;
        }
    }

    // ======================== 包管理器 ========================

    public class PackageManager
    {
        private readonly string _packagesDirectory;
        private readonly string _lockFilePath;
        private readonly List<PackageSource> _sources = new List<PackageSource>();
        private readonly PackageLock _lock;
        private readonly Dictionary<string, InstalledPackage> _installed = new Dictionary<string, InstalledPackage>();

        public PackageManager(string packagesDirectory, string lockFilePath = null)
        {
            _packagesDirectory = packagesDirectory;
            _lockFilePath = lockFilePath ?? Path.Combine(packagesDirectory, "package-lock.json");
            _lock = PackageLock.Load(_lockFilePath);
            Directory.CreateDirectory(_packagesDirectory);
            ScanInstalled();
        }

        public void AddSource(PackageSource source) => _sources.Add(source);
        public IReadOnlyList<PackageSource> Sources => _sources;

        public async Task<bool> InstallAsync(string packageName, string versionRange)
        {
            var manifest = await ResolveManifestAsync(packageName, versionRange);
            if (manifest == null)
            {
                Console.WriteLine($"Package {packageName}@{versionRange} not found.");
                return false;
            }

            var targetDir = Path.Combine(_packagesDirectory, packageName, manifest.Version);
            if (Directory.Exists(targetDir))
            {
                Console.WriteLine($"Package {packageName}@{manifest.Version} already installed.");
            }
            else
            {
                Directory.CreateDirectory(targetDir);
                var stream = await DownloadAsync(packageName, manifest.Version);
                if (stream != null)
                {
                    using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
                    archive.ExtractToDirectory(targetDir);
                }
                else
                {
                    // Local / dev package without archive: just write manifest
                    File.WriteAllText(Path.Combine(targetDir, "manifest.json"), manifest.ToJson());
                }
                Console.WriteLine($"Installed {packageName}@{manifest.Version} to {targetDir}");
            }

            _installed[packageName] = new InstalledPackage
            {
                Manifest = manifest,
                InstallPath = targetDir
            };

            _lock.AddOrUpdate(new PackageLockEntry
            {
                Name = packageName,
                Version = manifest.Version,
                Resolved = targetDir,
                Integrity = ComputeDirectoryChecksum(targetDir)
            });

            // Recursive dependency installation
            if (manifest.Dependencies != null)
            {
                foreach (var dep in manifest.Dependencies.Where(d => !d.Optional))
                {
                    if (!_installed.ContainsKey(dep.Name))
                    {
                        var success = await InstallAsync(dep.Name, dep.VersionRange);
                        if (!success && !dep.Optional)
                        {
                            Console.WriteLine($"Failed to install required dependency {dep.Name}@{dep.VersionRange}");
                            return false;
                        }
                    }
                }
            }

            _lock.Save(_lockFilePath);
            return true;
        }

        public bool Uninstall(string packageName)
        {
            if (!_installed.TryGetValue(packageName, out var installed))
            {
                Console.WriteLine($"Package {packageName} is not installed.");
                return false;
            }

            var dependents = _installed.Values.Where(p =>
                p.Manifest.Dependencies?.Any(d => d.Name == packageName && !d.Optional) ?? false).ToList();
            if (dependents.Count > 0)
            {
                Console.WriteLine($"Cannot uninstall {packageName}. Required by: {string.Join(", ", dependents.Select(d => d.Manifest.Name))}");
                return false;
            }

            try
            {
                Directory.Delete(installed.InstallPath, true);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to delete {installed.InstallPath}: {ex.Message}");
                return false;
            }

            _installed.Remove(packageName);
            _lock.Packages.Remove(packageName);
            _lock.Save(_lockFilePath);
            Console.WriteLine($"Uninstalled {packageName}.");
            return true;
        }

        public async Task<bool> UpdateAsync(string packageName)
        {
            if (!_installed.TryGetValue(packageName, out var installed))
            {
                Console.WriteLine($"Package {packageName} is not installed.");
                return false;
            }

            var latest = await GetLatestVersionAsync(packageName);
            if (latest == null || latest == installed.Manifest.Version)
            {
                Console.WriteLine($"Package {packageName} is already up to date.");
                return true;
            }

            Uninstall(packageName);
            return await InstallAsync(packageName, $"={latest}");
        }

        public IReadOnlyDictionary<string, InstalledPackage> ListInstalled() => _installed;

        public async Task<List<string>> SearchAsync(string query)
        {
            var results = new HashSet<string>();
            foreach (var source in _sources)
            {
                try
                {
                    var r = await source.SearchAsync(query);
                    foreach (var item in r) results.Add(item);
                }
                catch { /* ignore source failures */ }
            }
            return results.ToList();
        }

        public List<PackageManifest> ResolveDependencies(PackageManifest root)
        {
            var all = new Dictionary<string, PackageManifest>();
            var visited = new HashSet<string>();
            var stack = new Stack<(PackageManifest pkg, int depth)>();
            stack.Push((root, 0));

            while (stack.Count > 0)
            {
                var (current, depth) = stack.Pop();
                if (visited.Contains(current.Name)) continue;
                visited.Add(current.Name);
                all[current.Name] = current;

                if (current.Dependencies != null)
                {
                    foreach (var dep in current.Dependencies.Where(d => !d.Optional))
                    {
                        if (all.ContainsKey(dep.Name)) continue;
                        var resolved = _installed.Values.FirstOrDefault(p => p.Manifest.Name == dep.Name)?.Manifest;
                        if (resolved != null)
                            stack.Push((resolved, depth + 1));
                    }
                }
            }

            var graph = all.Values.ToList();
            return TopologicalSort(graph);
        }

        private List<PackageManifest> TopologicalSort(List<PackageManifest> packages)
        {
            var inDegree = packages.ToDictionary(p => p.Name, _ => 0);
            var adj = packages.ToDictionary(p => p.Name, _ => new List<string>());

            foreach (var pkg in packages)
            {
                if (pkg.Dependencies == null) continue;
                foreach (var dep in pkg.Dependencies.Where(d => !d.Optional && inDegree.ContainsKey(d.Name)))
                {
                    adj[pkg.Name].Add(dep.Name);
                    inDegree[dep.Name]++;
                }
            }

            var queue = new Queue<string>(inDegree.Where(kv => kv.Value == 0).Select(kv => kv.Key));
            var result = new List<PackageManifest>();

            while (queue.Count > 0)
            {
                var name = queue.Dequeue();
                var pkg = packages.FirstOrDefault(p => p.Name == name);
                if (pkg != null) result.Add(pkg);

                foreach (var neighbor in adj[name])
                {
                    inDegree[neighbor]--;
                    if (inDegree[neighbor] == 0)
                        queue.Enqueue(neighbor);
                }
            }

            return result;
        }

        private void ScanInstalled()
        {
            if (!Directory.Exists(_packagesDirectory)) return;
            foreach (var pkgDir in Directory.GetDirectories(_packagesDirectory))
            {
                var pkgName = Path.GetFileName(pkgDir);
                foreach (var verDir in Directory.GetDirectories(pkgDir))
                {
                    var manifestPath = Path.Combine(verDir, "manifest.json");
                    if (File.Exists(manifestPath))
                    {
                        var json = File.ReadAllText(manifestPath);
                        var manifest = PackageManifest.FromJson(json);
                        _installed[manifest.Name] = new InstalledPackage
                        {
                            Manifest = manifest,
                            InstallPath = verDir
                        };
                    }
                }
            }
        }

        private async Task<PackageManifest> ResolveManifestAsync(string packageName, string versionRange)
        {
            foreach (var source in _sources)
            {
                try
                {
                    var versions = await source.ListVersionsAsync(packageName);
                    var matching = versions.Select(PackageVersion.Parse)
                        .Where(v => v.Satisfies(versionRange))
                        .OrderByDescending(v => v)
                        .FirstOrDefault();
                    if (matching != null)
                        return await source.GetManifestAsync(packageName, matching.ToString());
                }
                catch { /* continue */ }
            }
            return null;
        }

        private async Task<string> GetLatestVersionAsync(string packageName)
        {
            foreach (var source in _sources)
            {
                try
                {
                    var versions = await source.ListVersionsAsync(packageName);
                    var latest = versions.Select(PackageVersion.Parse)
                        .Where(v => string.IsNullOrEmpty(v.Prerelease))
                        .OrderByDescending(v => v)
                        .FirstOrDefault();
                    if (latest != null) return latest.ToString();
                }
                catch { /* continue */ }
            }
            return null;
        }

        private async Task<Stream> DownloadAsync(string packageName, string version)
        {
            foreach (var source in _sources)
            {
                try
                {
                    return await source.DownloadAsync(packageName, version);
                }
                catch { /* continue */ }
            }
            return null;
        }

        private static string ComputeDirectoryChecksum(string path)
        {
            using var sha = SHA256.Create();
            var files = Directory.GetFiles(path, "*", SearchOption.AllDirectories).OrderBy(f => f);
            foreach (var file in files)
            {
                var relative = file.Substring(path.Length);
                var bytes = Encoding.UTF8.GetBytes(relative);
                sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
                var content = File.ReadAllBytes(file);
                sha.TransformBlock(content, 0, content.Length, null, 0);
            }
            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant();
        }
    }

    public class InstalledPackage
    {
        public PackageManifest Manifest { get; set; }
        public string InstallPath { get; set; }
    }
}
