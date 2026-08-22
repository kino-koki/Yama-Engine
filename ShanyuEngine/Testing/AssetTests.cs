using ShanyuEngine.Testing;

namespace ShanyuEngine.Tests
{
    /// <summary>
    /// 资产管线测试：AssetDatabase GUID、.shmeta、导入、序列化
    /// </summary>
    [TestClass]
    public class AssetTests : EngineTestFixture
    {
        [TestMethod]
        public void SceneJsonRoundtrip()
        {
            var scene = new MockEngine.Scene { Name = "TestScene" };
            scene.Entities.Add(new MockEngine.Entity(1));
            scene.Entities.Add(new MockEngine.Entity(2));

            var json = System.Text.Json.JsonSerializer.Serialize(new { scene.Name, EntityCount = scene.Entities.Count });
            Assert.IsFalse(string.IsNullOrEmpty(json));
            Assert.IsTrue(json.Contains("TestScene"));
        }

        [TestMethod]
        public void SceneEntitiesCount()
        {
            var scene = new MockEngine.Scene { Name = "Level1" };
            for (int i = 0; i < 100; i++)
                scene.Entities.Add(new MockEngine.Entity(i));
            Assert.AreEqual(100, scene.Entities.Count);
        }

        [TestMethod]
        public void ComponentSerialization()
        {
            var comp = new MockEngine.ComponentA { Value = 99 };
            var json = System.Text.Json.JsonSerializer.Serialize(comp);
            Assert.IsTrue(json.Contains("99"));
        }

        [TestMethod]
        public void AssetImportCreatesStableMeta()
        {
            var projectPath = Path.Combine(TempDirectory, "AssetProject");
            Directory.CreateDirectory(projectPath);
            var sourcePath = Path.Combine(TempDirectory, "cube.obj");
            File.WriteAllText(sourcePath, "o Cube\nv 0 0 0\nv 1 0 0\nv 0 1 0\nf 1 2 3\n");

            var db = new ShanyuEngine.Assets.AssetDatabase(projectPath);
            var imported = db.ImportAsset(sourcePath);

            Assert.IsNotNull(imported);
            Assert.IsFalse(string.IsNullOrWhiteSpace(imported!.Guid.ToString()));

            var copiedPath = Path.Combine(projectPath, imported.AssetPath.Replace('/', Path.DirectorySeparatorChar));
            var metaPath = copiedPath + ".shmeta";
            Assert.IsTrue(File.Exists(copiedPath));
            Assert.IsTrue(File.Exists(metaPath));

            var meta = ShanyuEngine.Assets.AssetMeta.Read(metaPath);
            Assert.AreEqual(imported.Guid.ToString("N"), meta.Guid);
            Assert.AreEqual(imported.AssetPath, meta.AssetPath);
            Assert.AreEqual(ShanyuEngine.Assets.AssetType.Mesh.ToString(), meta.Type);

            var resolved = db.GetAssetByGuid(meta.Guid);
            Assert.IsNotNull(resolved);
            Assert.AreEqual(imported.AssetPath, resolved!.AssetPath);
        }

        [TestMethod]
        public void AssetDependencyGraphTracksSceneAndPrefabReferences()
        {
            var projectPath = Path.Combine(TempDirectory, "DependencyGraphProject");
            Directory.CreateDirectory(Path.Combine(projectPath, "Assets", "Meshes"));
            Directory.CreateDirectory(Path.Combine(projectPath, "Assets", "Materials"));

            var meshAssetPath = "Assets/Meshes/Cube.obj";
            var materialAssetPath = "Assets/Materials/Copper.shmat";
            File.WriteAllText(Path.Combine(projectPath, meshAssetPath.Replace('/', Path.DirectorySeparatorChar)), "o Cube\n");
            File.WriteAllText(Path.Combine(projectPath, materialAssetPath.Replace('/', Path.DirectorySeparatorChar)), "{}");

            var db = new ShanyuEngine.Assets.AssetDatabase(projectPath);
            db.RegisterAsset(meshAssetPath, Guid.NewGuid(), ShanyuEngine.Assets.AssetType.Mesh, "CubeMesh");
            db.RegisterAsset(materialAssetPath, Guid.NewGuid(), ShanyuEngine.Assets.AssetType.Material, "CopperMaterial");

            var cube = db.CreateObject("DependencyCube", meshAssetPath, new System.Numerics.Vector3(0, 0, 0));
            cube.MaterialAssetPath = materialAssetPath;

            var prefabSystem = new ShanyuEngine.Prefabs.PrefabSystem(projectPath, db);
            var prefab = prefabSystem.SavePrefab(cube, "Assets/Prefabs/DependencyCube.shprefab");

            var graph = ShanyuEngine.Diagnostics.AssetDependencyGraph.Build(new ShanyuEngine.Diagnostics.AssetDependencyGraphOptions
            {
                ProjectRoot = projectPath,
                Assets = db,
                SceneName = "Main"
            });

            Assert.IsTrue(graph.Nodes.Any(n => n.Id == "scene:Main" && n.Type == ShanyuEngine.Diagnostics.AssetDependencyNodeType.Scene));
            Assert.IsTrue(graph.Nodes.Any(n => n.Id == meshAssetPath && n.Type == ShanyuEngine.Diagnostics.AssetDependencyNodeType.Mesh));
            Assert.IsTrue(graph.Nodes.Any(n => n.Id == materialAssetPath && n.Type == ShanyuEngine.Diagnostics.AssetDependencyNodeType.Material));
            Assert.IsTrue(graph.Nodes.Any(n => n.Id == "Assets/Prefabs/DependencyCube.shprefab" && n.Type == ShanyuEngine.Diagnostics.AssetDependencyNodeType.Prefab));

            var sceneDependencies = graph.GetDirectDependencies("scene:Main").ToList();
            Assert.IsTrue(sceneDependencies.Contains(meshAssetPath));
            Assert.IsTrue(sceneDependencies.Contains(materialAssetPath));

            var materialDependents = graph.GetDirectDependents(materialAssetPath).ToList();
            Assert.IsTrue(materialDependents.Contains("scene:Main"));
            Assert.AreEqual(0, graph.MissingReferences.Count);
        }

        [TestMethod]
        public void AssetDependencyGraphReportsMissingReferences()
        {
            var projectPath = Path.Combine(TempDirectory, "DependencyGraphBrokenProject");
            Directory.CreateDirectory(projectPath);

            var db = new ShanyuEngine.Assets.AssetDatabase(projectPath);
            var ghost = db.CreateObject("GhostDependency", "Assets/Meshes/MissingMesh.obj", new System.Numerics.Vector3(0, 0, 0));
            ghost.MaterialAssetPath = "Assets/Materials/MissingMaterial.shmat";

            var graph = ShanyuEngine.Diagnostics.AssetDependencyGraph.Build(new ShanyuEngine.Diagnostics.AssetDependencyGraphOptions
            {
                ProjectRoot = projectPath,
                Assets = db,
                SceneName = "BrokenScene"
            });

            Assert.IsTrue(graph.MissingReferences.Any(r => r.SourceId == "scene:BrokenScene" && r.TargetId == "Assets/Meshes/MissingMesh.obj"));
            Assert.IsTrue(graph.MissingReferences.Any(r => r.SourceId == "scene:BrokenScene" && r.TargetId == "Assets/Materials/MissingMaterial.shmat"));
            Assert.IsTrue(graph.GetDirectDependencies("scene:BrokenScene").Contains("Assets/Meshes/MissingMesh.obj"));
        }
    }
}
