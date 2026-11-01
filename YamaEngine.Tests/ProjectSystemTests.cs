using System;
using System.IO;
using System.Numerics;
using System.Linq;
using System.Collections.Generic;
using YamaEngine.Assets;
using YamaEngine.Projects;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests
{
    public static class ProjectSystemTests
    {
        public static void TestProjectSystemCreatesBasic3DProject(string tempRoot)
        {
            var templateRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Templates"));
            var projectPath = Path.Combine(tempRoot, "MyGame");

            var project = ProjectSystem.CreateProject(projectPath, "MyGame", Path.Combine(templateRoot, "Basic3D"));

            AssertEqual("MyGame", project.Name, "Project name should be stored.");
            AssertTrue(File.Exists(Path.Combine(projectPath, "yama.project.json")), "Project manifest should exist.");
            AssertTrue(File.Exists(Path.Combine(projectPath, "Content", "Scenes", "Main.yama")), "Template startup scene should be copied.");
            AssertTrue(Directory.Exists(Path.Combine(projectPath, "Assets")), "Unity-style Assets folder should exist.");

            var loaded = ProjectSystem.LoadProject(projectPath);
            AssertEqual("Content/Scenes/Main.yama", loaded.StartupScene, "Startup scene should roundtrip.");
        }

        public static void TestAssetImportCreatesStableMeta(string tempRoot)
        {
            var projectPath = Path.Combine(tempRoot, "AssetProject");
            Directory.CreateDirectory(projectPath);
            var sourcePath = Path.Combine(tempRoot, "cube.obj");
            File.WriteAllText(sourcePath, "o Cube\nv 0 0 0\nv 1 0 0\nv 0 1 0\nf 1 2 3\n");

            var db = new AssetDatabase(projectPath);
            var imported = db.ImportAsset(sourcePath);

            AssertTrue(imported != null, "Imported metadata should not be null.");
            AssertTrue(!string.IsNullOrWhiteSpace(imported!.Guid.ToString()), "Imported asset should have a GUID.");

            var copiedPath = Path.Combine(projectPath, imported.AssetPath.Replace('/', Path.DirectorySeparatorChar));
            var metaPath = copiedPath + ".shmeta";
            AssertTrue(File.Exists(copiedPath), "Imported asset file should be copied into project Assets.");
            AssertTrue(File.Exists(metaPath), "Imported asset should create .shmeta.");

            var meta = AssetMeta.Read(metaPath);
            AssertEqual(imported.Guid.ToString("N"), meta.Guid, "Meta GUID should match imported GUID.");
            AssertEqual(imported.AssetPath, meta.AssetPath, "Meta asset path should match imported asset path.");
            AssertEqual(AssetType.Mesh.ToString(), meta.Type, "OBJ should import as Mesh.");

            var resolved = db.GetAssetByGuid(meta.Guid);
            AssertTrue(resolved != null, "Asset should resolve by GUID.");
            AssertEqual(imported.AssetPath, resolved!.AssetPath, "GUID lookup should return the imported asset.");
        }
    }
}
