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
    public static class PrefabTests
    {
        public static void TestPrefabSaveAndInstantiateRoundtrip(string tempRoot)
        {
            var projectPath = Path.Combine(tempRoot, "PrefabProject");
            Directory.CreateDirectory(projectPath);
            var db = new AssetDatabase(projectPath);
            var prefabSystem = new PrefabSystem(projectPath, db);

            var source = db.CreateObject("Enemy", "Box", new Vector3(2, 1, 3));
            source.Scale = new Vector3(1.5f, 2f, 1.5f);
            source.BaseColor = new Vector3(1f, 0.2f, 0.1f);
            source.Metallic = 0.25f;
            source.Roughness = 0.4f;

            var prefab = prefabSystem.SavePrefab(source, "Assets/Prefabs/Enemy.shprefab");

            AssertTrue(File.Exists(Path.Combine(projectPath, "Assets", "Prefabs", "Enemy.shprefab")), "Prefab file should be written.");
            AssertTrue(File.Exists(Path.Combine(projectPath, "Assets", "Prefabs", "Enemy.shprefab.shmeta")), "Prefab meta should be written.");
            AssertTrue(!string.IsNullOrWhiteSpace(prefab.Guid), "Prefab should have a GUID.");

            var instance = prefabSystem.InstantiatePrefab(prefab.Guid, new Vector3(5, 0, 0));

            AssertEqual("Enemy", instance.Name, "Prefab instance should preserve name.");
            AssertEqual(new Vector3(5, 0, 0), instance.Position, "Prefab instantiate position should override root position.");
            AssertEqual(source.Scale, instance.Scale, "Prefab instance should preserve scale.");
            AssertEqual(source.BaseColor, instance.BaseColor, "Prefab instance should preserve material color.");
            AssertEqual(source.MeshAssetPath, instance.MeshAssetPath, "Prefab instance should preserve mesh reference.");
        }
    }
}
