using System.Text.Json;
using ShanyuEngine.Testing;

namespace ShanyuEngine.Tests
{
    /// <summary>
    /// 构建测试：BuildPipeline 配置、平台目标、Cook
    /// </summary>
    [TestClass]
    public class BuildTests : EngineTestFixture
    {
        [TestMethod]
        public void BuildPipelineExportsPlayableDemoLayout()
        {
            var projectPath = Path.Combine(TempDirectory, "BuildProject");
            Directory.CreateDirectory(projectPath);
            var sourcePath = Path.Combine(TempDirectory, "build_cube.obj");
            File.WriteAllText(sourcePath, "o Cube\nv 0 0 0\nv 1 0 0\nv 0 1 0\nf 1 2 3\n");

            var db = new ShanyuEngine.Assets.AssetDatabase(projectPath);
            var imported = db.ImportAsset(sourcePath);
            var obj = db.CreateObject("BuildCube", imported!.AssetPath, new System.Numerics.Vector3(1, 2, 3));
            obj.BaseColor = new System.Numerics.Vector3(0.1f, 0.2f, 0.3f);

            var outputRoot = Path.Combine(TempDirectory, "Exports");
            var result = ShanyuEngine.Build.BuildPipeline.Build(new ShanyuEngine.Build.BuildOptions
            {
                ProjectName = "BuildProject",
                OutputDirectory = outputRoot,
                SceneName = "Main",
                Assets = db
            });

            var exportRoot = Path.Combine(outputRoot, ShanyuEngine.Build.BuildPipeline.DemoFolderName);
            Assert.IsTrue(result.Success);
            Assert.AreEqual(exportRoot, result.OutputDirectory);
            Assert.IsTrue(File.Exists(Path.Combine(exportRoot, ShanyuEngine.Build.BuildPipeline.ManifestFileName)));
            Assert.IsTrue(File.Exists(Path.Combine(exportRoot, "Content", "MainScene.shanyu")));
            Assert.IsTrue(File.Exists(Path.Combine(exportRoot, ShanyuEngine.Build.BuildPipeline.LaunchScriptFileName)));
            Assert.IsTrue(File.Exists(Path.Combine(exportRoot, ShanyuEngine.Build.BuildPipeline.ReadmeFileName)));
        }

        [TestMethod]
        public void BuildPipelineWritesBuildReport()
        {
            var projectPath = Path.Combine(TempDirectory, "ReportProject");
            Directory.CreateDirectory(projectPath);

            var sourcePath = Path.Combine(TempDirectory, "report_cube.obj");
            File.WriteAllText(sourcePath, "o Cube\nv 0 0 0\nv 1 0 0\nv 0 1 0\nf 1 2 3\n");

            var db = new ShanyuEngine.Assets.AssetDatabase(projectPath);
            var imported = db.ImportAsset(sourcePath);
            db.CreateObject("ReportCube", imported!.AssetPath, new System.Numerics.Vector3(0, 0, 0));
            db.CreateObject("GhostMesh", "Assets/Meshes/__does_not_exist__.shmesh", new System.Numerics.Vector3(1, 1, 1));

            var outputRoot = Path.Combine(TempDirectory, "ReportExports");
            var result = ShanyuEngine.Build.BuildPipeline.Build(new ShanyuEngine.Build.BuildOptions
            {
                ProjectName = "ReportProject",
                OutputDirectory = outputRoot,
                SceneName = "Main",
                Assets = db
            });

            var exportRoot = Path.Combine(outputRoot, ShanyuEngine.Build.BuildPipeline.DemoFolderName);
            Assert.IsTrue(result.Success);
            Assert.IsFalse(string.IsNullOrWhiteSpace(result.ReportPath));
            Assert.IsTrue(File.Exists(result.ReportPath));

            using var doc = JsonDocument.Parse(File.ReadAllText(result.ReportPath));
            var root = doc.RootElement;
            Assert.IsTrue(root.TryGetProperty("projectName", out _));
            Assert.IsTrue(root.TryGetProperty("warnings", out var warnings) && warnings.ValueKind == JsonValueKind.Array);
            Assert.IsTrue(warnings.GetArrayLength() >= 1);
            Assert.IsTrue(root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array);
            Assert.AreEqual(0, errors.GetArrayLength());
            Assert.IsTrue(root.TryGetProperty("missingReferences", out var missing) && missing.GetArrayLength() >= 1);
            Assert.IsTrue(root.TryGetProperty("exportedAtUtc", out _));
        }

        [TestMethod]
        public void BuildPipelineCookCopiesAssets()
        {
            var projectPath = Path.Combine(TempDirectory, "CookProject");
            Directory.CreateDirectory(projectPath);

            var sourcePath = Path.Combine(TempDirectory, "cook_cube.obj");
            File.WriteAllText(sourcePath, "o CookCube\nv 0 0 0\nv 1 0 0\nv 0 1 0\nf 1 2 3\n");

            var db = new ShanyuEngine.Assets.AssetDatabase(projectPath);
            var imported = db.ImportAsset(sourcePath);
            db.CreateObject("CookObj", imported!.AssetPath, System.Numerics.Vector3.Zero);

            var outputRoot = Path.Combine(TempDirectory, "CookExports");
            var result = ShanyuEngine.Build.BuildPipeline.Build(new ShanyuEngine.Build.BuildOptions
            {
                ProjectName = "CookProject",
                OutputDirectory = outputRoot,
                SceneName = "Main",
                Assets = db
            });

            Assert.IsTrue(result.Success);
            var contentDir = Path.Combine(result.OutputDirectory, "Content");
            Assert.IsTrue(Directory.Exists(contentDir));
            Assert.IsTrue(File.Exists(Path.Combine(contentDir, imported.AssetPath.Replace('/', Path.DirectorySeparatorChar))));
        }
    }
}
