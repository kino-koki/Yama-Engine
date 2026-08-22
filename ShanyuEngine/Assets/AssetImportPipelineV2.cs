using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace ShanyuEngine.Assets
{
    // 资产管线最怕“看起来导入了，其实依赖没记住”。这里专门把依赖、hash、事务放在一起。
    public readonly record struct AssetGuid(Guid Value)
    {
        public static AssetGuid NewGuid() => new(Guid.NewGuid());
        public override string ToString() => Value.ToString("N");
    }

    public sealed record AssetImportInput(string SourcePath, string ProjectRelativePath, string ImporterId, Dictionary<string, string> Options);
    public sealed record AssetImportArtifact(AssetGuid Guid, string ArtifactPath, string ContentHash, IReadOnlyList<string> Dependencies, DateTime ImportedAt);

    public interface IAssetImporterV2
    {
        string Id { get; }
        IReadOnlyList<string> Extensions { get; }
        AssetImportArtifact Import(AssetImportInput input, AssetImportContext context);
    }

    public sealed class AssetImportContext
    {
        private readonly List<string> _dependencies = new();
        private readonly List<string> _warnings = new();
        public string ArtifactRoot { get; }
        public IReadOnlyList<string> Dependencies => _dependencies;
        public IReadOnlyList<string> Warnings => _warnings;

        public AssetImportContext(string artifactRoot)
        {
            ArtifactRoot = artifactRoot;
            Directory.CreateDirectory(artifactRoot);
        }

        public void DependsOn(string path)
        {
            if (!string.IsNullOrWhiteSpace(path) && !_dependencies.Contains(path, StringComparer.OrdinalIgnoreCase))
                _dependencies.Add(path);
        }

        public void Warn(string message)
        {
            if (!string.IsNullOrWhiteSpace(message)) _warnings.Add(message);
        }

        public string AllocateArtifactPath(AssetGuid guid, string extension)
        {
            var safeExt = extension.StartsWith('.') ? extension : "." + extension;
            return Path.Combine(ArtifactRoot, guid + safeExt);
        }
    }

    public sealed class TextAssetImporterV2 : IAssetImporterV2
    {
        public string Id => "text";
        public IReadOnlyList<string> Extensions { get; } = new[] { ".txt", ".json", ".yaml", ".xml", ".shader", ".hlsl" };

        public AssetImportArtifact Import(AssetImportInput input, AssetImportContext context)
        {
            var guid = AssetGuid.NewGuid();
            var text = File.Exists(input.SourcePath) ? File.ReadAllText(input.SourcePath) : "";
            var normalized = text.Replace("\r\n", "\n");
            var hash = AssetHash.Compute(normalized);
            var artifact = context.AllocateArtifactPath(guid, ".assettext");
            File.WriteAllText(artifact, normalized, Encoding.UTF8);
            context.DependsOn(input.SourcePath);
            return new AssetImportArtifact(guid, artifact, hash, context.Dependencies.ToArray(), DateTime.Now);
        }
    }

    public static class AssetHash
    {
        public static string Compute(string text)
        {
            using var sha = SHA256.Create();
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }

        public static string ComputeFile(string path)
        {
            if (!File.Exists(path)) return "missing";
            using var sha = SHA256.Create();
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
        }
    }

    public sealed class AssetDependencyGraphV2
    {
        private readonly Dictionary<string, HashSet<string>> _forward = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, HashSet<string>> _reverse = new(StringComparer.OrdinalIgnoreCase);

        public void SetDependencies(string asset, IEnumerable<string> dependencies)
        {
            if (_forward.TryGetValue(asset, out var old))
            {
                foreach (var dep in old)
                    if (_reverse.TryGetValue(dep, out var users))
                        users.Remove(asset);
            }
            var set = new HashSet<string>(dependencies.Where(d => !string.IsNullOrWhiteSpace(d)), StringComparer.OrdinalIgnoreCase);
            _forward[asset] = set;
            foreach (var dep in set)
            {
                if (!_reverse.TryGetValue(dep, out var users))
                    _reverse[dep] = users = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                users.Add(asset);
            }
        }

        public IReadOnlyList<string> GetDependencies(string asset) =>
            _forward.TryGetValue(asset, out var set) ? set.OrderBy(x => x).ToArray() : Array.Empty<string>();

        public IReadOnlyList<string> GetDependents(string asset) =>
            _reverse.TryGetValue(asset, out var set) ? set.OrderBy(x => x).ToArray() : Array.Empty<string>();

        public IReadOnlyList<string> CollectDirtyClosure(IEnumerable<string> changedFiles)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var queue = new Queue<string>(changedFiles);
            while (queue.Count > 0)
            {
                var item = queue.Dequeue();
                if (!result.Add(item)) continue;
                foreach (var user in GetDependents(item))
                    queue.Enqueue(user);
            }
            return result.OrderBy(x => x).ToArray();
        }
    }

    public sealed class IncrementalImportDatabase
    {
        private readonly Dictionary<string, string> _sourceHashes = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, AssetImportArtifact> _artifacts = new(StringComparer.OrdinalIgnoreCase);

        public bool NeedsImport(string sourcePath)
        {
            var hash = AssetHash.ComputeFile(sourcePath);
            return !_sourceHashes.TryGetValue(sourcePath, out var old) || old != hash;
        }

        public void Accept(string sourcePath, AssetImportArtifact artifact)
        {
            _sourceHashes[sourcePath] = AssetHash.ComputeFile(sourcePath);
            _artifacts[sourcePath] = artifact;
        }

        public bool TryGetArtifact(string sourcePath, out AssetImportArtifact artifact)
        {
            if (_artifacts.TryGetValue(sourcePath, out var found))
            {
                artifact = found;
                return true;
            }

            artifact = null!;
            return false;
        }
    }

    public sealed class AssetImportPipelineV2
    {
        private readonly List<IAssetImporterV2> _importers = new();
        public IncrementalImportDatabase Database { get; } = new();
        public AssetDependencyGraphV2 Dependencies { get; } = new();

        public void Register(IAssetImporterV2 importer)
        {
            if (_importers.Any(i => i.Id.Equals(importer.Id, StringComparison.OrdinalIgnoreCase))) return;
            _importers.Add(importer);
        }

        public IReadOnlyList<AssetImportArtifact> ImportChanged(IEnumerable<string> sourceFiles, string artifactRoot)
        {
            var imported = new List<AssetImportArtifact>();
            foreach (var source in sourceFiles)
            {
                if (!Database.NeedsImport(source)) continue;
                var importer = FindImporter(source);
                if (importer == null) continue;
                var ctx = new AssetImportContext(artifactRoot);
                var input = new AssetImportInput(source, Path.GetFileName(source), importer.Id, new Dictionary<string, string>());
                var artifact = importer.Import(input, ctx);
                Database.Accept(source, artifact);
                Dependencies.SetDependencies(source, artifact.Dependencies);
                imported.Add(artifact);
            }
            return imported;
        }

        private IAssetImporterV2? FindImporter(string source)
        {
            var ext = Path.GetExtension(source);
            return _importers.FirstOrDefault(i => i.Extensions.Contains(ext, StringComparer.OrdinalIgnoreCase));
        }

        public static AssetImportPipelineV2 CreateDefault()
        {
            var pipe = new AssetImportPipelineV2();
            pipe.Register(new TextAssetImporterV2());
            return pipe;
        }
    }

    public sealed record ContentCatalogEntry(string Address, AssetGuid Guid, string ArtifactPath, string Hash, long Size);

    public sealed class ContentCatalogBuilderV2
    {
        private readonly List<ContentCatalogEntry> _entries = new();
        public IReadOnlyList<ContentCatalogEntry> Entries => _entries;

        public void Add(string address, AssetImportArtifact artifact)
        {
            var size = File.Exists(artifact.ArtifactPath) ? new FileInfo(artifact.ArtifactPath).Length : 0;
            _entries.RemoveAll(e => e.Address.Equals(address, StringComparison.OrdinalIgnoreCase));
            _entries.Add(new ContentCatalogEntry(address, artifact.Guid, artifact.ArtifactPath, artifact.ContentHash, size));
        }

        public IReadOnlyList<string> Validate()
        {
            var errors = new List<string>();
            foreach (var group in _entries.GroupBy(e => e.Address, StringComparer.OrdinalIgnoreCase))
                if (group.Count() > 1) errors.Add($"地址重复：{group.Key}");
            foreach (var entry in _entries)
            {
                if (string.IsNullOrWhiteSpace(entry.Address)) errors.Add("存在空地址资源");
                if (!File.Exists(entry.ArtifactPath)) errors.Add($"Artifact 丢失：{entry.ArtifactPath}");
            }
            return errors;
        }

        public string ToJsonLike()
        {
            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine("  \"entries\": [");
            for (var i = 0; i < _entries.Count; i++)
            {
                var e = _entries[i];
                sb.Append("    { ");
                sb.Append($"\"address\": \"{Escape(e.Address)}\", ");
                sb.Append($"\"guid\": \"{e.Guid}\", ");
                sb.Append($"\"hash\": \"{e.Hash}\", ");
                sb.Append($"\"size\": {e.Size} ");
                sb.Append(i == _entries.Count - 1 ? "}" : "},");
                sb.AppendLine();
            }
            sb.AppendLine("  ]");
            sb.AppendLine("}");
            return sb.ToString();
        }

        private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}
