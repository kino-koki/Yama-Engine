using System.Text.Json;
using YamaEngine.Prefabs;

namespace YamaEngine.Assets
{
    public sealed class AssetDependencyGraph
    {
        private readonly Dictionary<string, AssetDependencyNode> _nodesById = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, HashSet<string>> _dependencies = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, HashSet<string>> _dependents = new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyList<AssetDependencyNode> Nodes => _nodesById.Values.ToList();
        public IReadOnlyList<AssetDependencyEdge> Edges => _dependencies
            .SelectMany(pair => pair.Value.Select(target => new AssetDependencyEdge(pair.Key, target)))
            .ToList();
        public List<AssetMissingReference> MissingReferences { get; } = new();

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        public static AssetDependencyGraph Build(AssetDependencyGraphOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (options.Assets == null) throw new ArgumentException("Asset database is required.", nameof(options.Assets));

            var graph = new AssetDependencyGraph();
            graph.BuildFromAssets(options);
            graph.BuildSceneDependencies(options);
            graph.BuildPrefabDependencies(options);
            return graph;
        }

        public IEnumerable<string> GetDirectDependencies(string nodeId)
        {
            return _dependencies.TryGetValue(NormalizeId(nodeId), out var targets)
                ? targets.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                : Enumerable.Empty<string>();
        }

        public IEnumerable<string> GetDirectDependents(string nodeId)
        {
            return _dependents.TryGetValue(NormalizeId(nodeId), out var sources)
                ? sources.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                : Enumerable.Empty<string>();
        }

        public IEnumerable<string> GetTransitiveDependencies(string nodeId)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var stack = new Stack<string>(GetDirectDependencies(nodeId));
            while (stack.Count > 0)
            {
                var current = stack.Pop();
                if (!result.Add(current)) continue;
                foreach (var next in GetDirectDependencies(current)) stack.Push(next);
            }
            return result.OrderBy(x => x, StringComparer.OrdinalIgnoreCase);
        }

        private void BuildFromAssets(AssetDependencyGraphOptions options)
        {
            foreach (var asset in options.Assets!.GetAllAssets())
            {
                AddNode(asset.AssetPath, ToNodeType(asset.Type), asset.Name);

                var metaPath = ResolveProjectPath(options.ProjectRoot, asset.AssetPath) + ".shmeta";
                if (!File.Exists(metaPath)) continue;

                var meta = AssetMeta.Read(metaPath);
                foreach (var dependency in meta.Dependencies)
                {
                    AddDependency(asset.AssetPath, dependency, "meta", options.ProjectRoot);
                }
            }
        }

        private void BuildSceneDependencies(AssetDependencyGraphOptions options)
        {
            var sceneId = "scene:" + (string.IsNullOrWhiteSpace(options.SceneName) ? "Main" : options.SceneName.Trim());
            AddNode(sceneId, AssetDependencyNodeType.Scene, options.SceneName);

            foreach (var obj in options.Assets!.SceneObjects)
            {
                AddProjectAssetReference(sceneId, obj.MeshAssetPath, "scene.mesh", options.ProjectRoot);
                AddProjectAssetReference(sceneId, obj.MaterialAssetPath, "scene.material", options.ProjectRoot);
            }
        }

        private void BuildPrefabDependencies(AssetDependencyGraphOptions options)
        {
            foreach (var asset in options.Assets!.GetAllAssets(AssetType.Prefab))
            {
                var prefabPath = ResolveProjectPath(options.ProjectRoot, asset.AssetPath);
                if (!File.Exists(prefabPath))
                {
                    MissingReferences.Add(new AssetMissingReference(asset.AssetPath, asset.AssetPath, "prefab.file"));
                    continue;
                }

                var prefab = JsonSerializer.Deserialize<PrefabAsset>(File.ReadAllText(prefabPath), JsonOptions);
                if (prefab?.Root == null) continue;

                AddProjectAssetReference(asset.AssetPath, prefab.Root.MeshAssetPath, "prefab.mesh", options.ProjectRoot);
                AddProjectAssetReference(asset.AssetPath, prefab.Root.MaterialAssetPath, "prefab.material", options.ProjectRoot);
            }
        }

        private void AddProjectAssetReference(string sourceId, string? targetId, string reason, string projectRoot)
        {
            if (!LooksLikeProjectAssetPath(targetId)) return;
            AddDependency(sourceId, targetId!, reason, projectRoot);
        }

        private void AddDependency(string sourceId, string targetId, string reason, string projectRoot)
        {
            sourceId = NormalizeId(sourceId);
            targetId = NormalizeId(targetId);
            AddNode(sourceId, GuessNodeType(sourceId), sourceId);
            AddNode(targetId, GuessNodeType(targetId), targetId);

            if (!_dependencies.TryGetValue(sourceId, out var targets))
            {
                targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                _dependencies[sourceId] = targets;
            }
            targets.Add(targetId);

            if (!_dependents.TryGetValue(targetId, out var sources))
            {
                sources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                _dependents[targetId] = sources;
            }
            sources.Add(sourceId);

            if (LooksLikeProjectAssetPath(targetId) && !File.Exists(ResolveProjectPath(projectRoot, targetId)))
            {
                MissingReferences.Add(new AssetMissingReference(sourceId, targetId, reason));
            }
        }

        private void AddNode(string id, AssetDependencyNodeType type, string? name)
        {
            id = NormalizeId(id);
            if (_nodesById.TryGetValue(id, out var existing))
            {
                if (existing.Type == AssetDependencyNodeType.Unknown && type != AssetDependencyNodeType.Unknown)
                {
                    _nodesById[id] = existing with { Type = type };
                }
                return;
            }

            _nodesById[id] = new AssetDependencyNode(id, type, string.IsNullOrWhiteSpace(name) ? id : name);
        }

        private static AssetDependencyNodeType ToNodeType(AssetType type)
        {
            return type switch
            {
                AssetType.Mesh => AssetDependencyNodeType.Mesh,
                AssetType.Material => AssetDependencyNodeType.Material,
                AssetType.Texture2D or AssetType.TextureCube => AssetDependencyNodeType.Texture,
                AssetType.Prefab => AssetDependencyNodeType.Prefab,
                AssetType.Scene => AssetDependencyNodeType.Scene,
                AssetType.Shader => AssetDependencyNodeType.Shader,
                AssetType.Audio => AssetDependencyNodeType.Audio,
                _ => AssetDependencyNodeType.Unknown
            };
        }

        private static AssetDependencyNodeType GuessNodeType(string id)
        {
            if (id.StartsWith("scene:", StringComparison.OrdinalIgnoreCase)) return AssetDependencyNodeType.Scene;

            var ext = Path.GetExtension(id).ToLowerInvariant();
            return ext switch
            {
                ".obj" or ".fbx" or ".dae" or ".ply" or ".stl" or ".blend" or ".shmesh" => AssetDependencyNodeType.Mesh,
                ".shmat" => AssetDependencyNodeType.Material,
                ".png" or ".jpg" or ".jpeg" or ".bmp" or ".tga" or ".dds" => AssetDependencyNodeType.Texture,
                ".shprefab" => AssetDependencyNodeType.Prefab,
                ".yama" => AssetDependencyNodeType.Scene,
                ".shgraph" => AssetDependencyNodeType.Shader,
                ".wav" or ".mp3" or ".ogg" or ".flac" => AssetDependencyNodeType.Audio,
                _ => AssetDependencyNodeType.Unknown
            };
        }

        private static string ResolveProjectPath(string projectRoot, string assetPath)
        {
            return Path.Combine(projectRoot, NormalizeId(assetPath).Replace('/', Path.DirectorySeparatorChar));
        }

        private static bool LooksLikeProjectAssetPath(string? path)
        {
            return !string.IsNullOrWhiteSpace(path) &&
                   path.Replace('\\', '/').StartsWith("Assets/", StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeId(string id)
        {
            return id.Replace('\\', '/').TrimStart('/');
        }
    }

    public sealed class AssetDependencyGraphOptions
    {
        public string ProjectRoot { get; set; } = string.Empty;
        public AssetDatabase? Assets { get; set; }
        public string SceneName { get; set; } = "Main";
    }

    public sealed record AssetDependencyNode(string Id, AssetDependencyNodeType Type, string Name);

    public sealed record AssetDependencyEdge(string SourceId, string TargetId);

    public sealed record AssetMissingReference(string SourceId, string TargetId, string Reason);

    public enum AssetDependencyNodeType
    {
        Unknown,
        Scene,
        Prefab,
        Mesh,
        Material,
        Texture,
        Shader,
        Audio
    }
}
