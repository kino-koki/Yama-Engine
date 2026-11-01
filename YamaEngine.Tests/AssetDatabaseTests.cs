using System;
using System.IO;
using System.Numerics;
using System.Linq;
using System.Collections.Generic;
using YamaEngine.Assets;
using YamaEngine.Prefabs;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests
{
    public static class AssetDatabaseTests
    {
        public static void TestAssetDependencyGraphTracksSceneAndPrefabReferences(string tempRoot)
        {
            var projectPath = Path.Combine(tempRoot, "DependencyGraphProject");
            Directory.CreateDirectory(Path.Combine(projectPath, "Assets", "Meshes"));
            Directory.CreateDirectory(Path.Combine(projectPath, "Assets", "Materials"));

            var meshAssetPath = "Assets/Meshes/Cube.obj";
            var materialAssetPath = "Assets/Materials/Copper.shmat";
            File.WriteAllText(Path.Combine(projectPath, meshAssetPath.Replace('/', Path.DirectorySeparatorChar)), "o Cube\n");
            File.WriteAllText(Path.Combine(projectPath, materialAssetPath.Replace('/', Path.DirectorySeparatorChar)), "{}");

            var db = new AssetDatabase(projectPath);
            db.RegisterAsset(meshAssetPath, Guid.NewGuid(), AssetType.Mesh, "CubeMesh");
            db.RegisterAsset(materialAssetPath, Guid.NewGuid(), AssetType.Material, "CopperMaterial");

            var cube = db.CreateObject("DependencyCube", meshAssetPath, new Vector3(0, 0, 0));
            cube.MaterialAssetPath = materialAssetPath;

            var prefabSystem = new PrefabSystem(projectPath, db);
            var prefab = prefabSystem.SavePrefab(cube, "Assets/Prefabs/DependencyCube.shprefab");

            var graph = AssetDependencyGraph.Build(new AssetDependencyGraphOptions
            {
                ProjectRoot = projectPath,
                Assets = db,
                SceneName = "Main"
            });

            AssertTrue(graph.Nodes.Any(n => n.Id == "scene:Main" && n.Type == AssetDependencyNodeType.Scene), "Graph should contain scene node.");
            AssertTrue(graph.Nodes.Any(n => n.Id == meshAssetPath && n.Type == AssetDependencyNodeType.Mesh), "Graph should contain mesh node.");
            AssertTrue(graph.Nodes.Any(n => n.Id == materialAssetPath && n.Type == AssetDependencyNodeType.Material), "Graph should contain material node.");
            AssertTrue(graph.Nodes.Any(n => n.Id == "Assets/Prefabs/DependencyCube.shprefab" && n.Type == AssetDependencyNodeType.Prefab), "Graph should contain prefab node.");

            var sceneDependencies = graph.GetDirectDependencies("scene:Main").ToList();
            AssertTrue(sceneDependencies.Contains(meshAssetPath), "Scene should depend on mesh asset.");
            AssertTrue(sceneDependencies.Contains(materialAssetPath), "Scene should depend on material asset.");

            var prefabDependencies = graph.GetDirectDependencies("Assets/Prefabs/DependencyCube.shprefab").ToList();
            AssertTrue(prefabDependencies.Contains(meshAssetPath), "Prefab should depend on mesh asset.");
            AssertTrue(prefabDependencies.Contains(materialAssetPath), "Prefab should depend on material asset.");

            var materialDependents = graph.GetDirectDependents(materialAssetPath).ToList();
            AssertTrue(materialDependents.Contains("scene:Main"), "Material should list scene as dependent.");
            AssertTrue(materialDependents.Contains("Assets/Prefabs/DependencyCube.shprefab"), "Material should list prefab as dependent.");
            AssertEqual(0, graph.MissingReferences.Count, "Complete graph should have no missing references.");
        }

        public static void TestAssetDependencyGraphReportsMissingReferences(string tempRoot)
        {
            var projectPath = Path.Combine(tempRoot, "DependencyGraphBrokenProject");
            Directory.CreateDirectory(projectPath);

            var db = new AssetDatabase(projectPath);
            var ghost = db.CreateObject("GhostDependency", "Assets/Meshes/MissingMesh.obj", new Vector3(0, 0, 0));
            ghost.MaterialAssetPath = "Assets/Materials/MissingMaterial.shmat";

            var graph = AssetDependencyGraph.Build(new AssetDependencyGraphOptions
            {
                ProjectRoot = projectPath,
                Assets = db,
                SceneName = "BrokenScene"
            });

            AssertTrue(graph.MissingReferences.Any(r => r.SourceId == "scene:BrokenScene" && r.TargetId == "Assets/Meshes/MissingMesh.obj"), "Graph should report missing scene mesh reference.");
            AssertTrue(graph.MissingReferences.Any(r => r.SourceId == "scene:BrokenScene" && r.TargetId == "Assets/Materials/MissingMaterial.shmat"), "Graph should report missing scene material reference.");
            AssertTrue(graph.GetDirectDependencies("scene:BrokenScene").Contains("Assets/Meshes/MissingMesh.obj"), "Graph should still preserve missing mesh edge.");
            AssertTrue(graph.GetDirectDependencies("scene:BrokenScene").Contains("Assets/Materials/MissingMaterial.shmat"), "Graph should still preserve missing material edge.");
        }
    }
}
