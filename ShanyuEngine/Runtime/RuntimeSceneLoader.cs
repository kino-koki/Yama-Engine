using System.Text.Json;
using ShanyuEngine.Assets;
using ShanyuEngine.Build;
using ShanyuEngine.Rendering;
using ShanyuEngine.Scenes;

namespace ShanyuEngine.Runtime
{
    public static class RuntimeSceneLoader
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        public static RuntimeLoadResult LoadPackage(string packageRoot)
        {
            var result = new RuntimeLoadResult
            {
                PackageRoot = packageRoot ?? string.Empty
            };

            if (string.IsNullOrWhiteSpace(packageRoot) || !Directory.Exists(packageRoot))
            {
                result.Errors.Add($"Package root does not exist: {packageRoot}");
                return result;
            }

            var manifestPath = Path.Combine(packageRoot, BuildPipeline.ManifestFileName);
            if (!File.Exists(manifestPath))
            {
                result.Errors.Add($"Missing {BuildPipeline.ManifestFileName}: {manifestPath}");
            }
            else
            {
                try
                {
                    result.Manifest = JsonSerializer.Deserialize<RuntimeDemoManifest>(
                        File.ReadAllText(manifestPath),
                        JsonOptions) ?? new RuntimeDemoManifest();
                }
                catch (Exception ex)
                {
                    result.Errors.Add($"Invalid {BuildPipeline.ManifestFileName}: {ex.Message}");
                }
            }

            var sceneRelative = string.IsNullOrWhiteSpace(result.Manifest.Scene)
                ? Path.Combine("Content", "MainScene.shanyu")
                : result.Manifest.Scene.Replace('/', Path.DirectorySeparatorChar);
            var scenePath = Path.Combine(packageRoot, sceneRelative);

            if (!File.Exists(scenePath))
            {
                result.Errors.Add($"Missing runtime scene MainScene.shanyu: {scenePath}");
            }

            if (result.Errors.Count > 0) return result;

            try
            {
                var document = SceneSerializer.Load(scenePath);
                var objects = SceneSerializer.Materialize(document);
                var renderScene = RenderSceneBuilder.FromObjects(objects, document.Name);

                result.World = new RuntimeWorld
                {
                    PackageRoot = packageRoot,
                    ScenePath = scenePath,
                    SceneName = document.Name,
                    Objects = objects,
                    RenderScene = renderScene
                };
            }
            catch (Exception ex)
            {
                result.Errors.Add($"Failed to load runtime scene: {ex.Message}");
            }

            return result;
        }
    }

    public sealed class RuntimeLoadResult
    {
        public string PackageRoot { get; set; } = string.Empty;
        public RuntimeDemoManifest Manifest { get; set; } = new();
        public RuntimeWorld? World { get; set; }
        public List<string> Errors { get; set; } = new();
        public bool Success => Errors.Count == 0 && World != null;
    }

    public sealed class RuntimeWorld
    {
        public string PackageRoot { get; set; } = string.Empty;
        public string ScenePath { get; set; } = string.Empty;
        public string SceneName { get; set; } = "Main";
        public IReadOnlyList<SceneObject> Objects { get; set; } = Array.Empty<SceneObject>();
        public RenderSceneSnapshot RenderScene { get; set; } = new();
    }

    public sealed class RuntimeDemoManifest
    {
        public string Name { get; set; } = "Shanyu Demo";
        public string Engine { get; set; } = "Shanyu Engine";
        public int FormatVersion { get; set; } = 1;
        public string Scene { get; set; } = "Content/MainScene.shanyu";
        public int ObjectCount { get; set; }
        public int AssetCount { get; set; }
        public DateTime ExportedAtUtc { get; set; }
    }
}
