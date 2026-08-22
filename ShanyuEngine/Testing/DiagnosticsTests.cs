using ShanyuEngine.Testing;

namespace ShanyuEngine.Tests
{
    /// <summary>
    /// 诊断测试：EngineDiagnostics 健康检查、缺失引用检测
    /// </summary>
    [TestClass]
    public class DiagnosticsTests : EngineTestFixture
    {
        [TestMethod]
        public void EngineDiagnosticsReportsHealthyProject()
        {
            var projectPath = Path.Combine(TempDirectory, "DiagnosticsHealthyProject");
            Directory.CreateDirectory(Path.Combine(projectPath, "Assets"));
            ShanyuEngine.Projects.ProjectSystem.SaveProject(new ShanyuEngine.Projects.ShanyuProject
            {
                Name = "DiagnosticsHealthyProject",
                ProjectPath = projectPath
            });

            var sourcePath = Path.Combine(TempDirectory, "diagnostics_cube.obj");
            File.WriteAllText(sourcePath, "o Cube\nv 0 0 0\nv 1 0 0\nv 0 1 0\nf 1 2 3\n");

            var db = new ShanyuEngine.Assets.AssetDatabase(projectPath);
            var imported = db.ImportAsset(sourcePath);
            db.CreateObject("DiagnosticsCube", imported!.AssetPath, new System.Numerics.Vector3(0, 0, 0));

            var build = ShanyuEngine.Build.BuildPipeline.Build(new ShanyuEngine.Build.BuildOptions
            {
                ProjectName = "DiagnosticsHealthyProject",
                OutputDirectory = Path.Combine(TempDirectory, "DiagnosticsHealthyExport"),
                SceneName = "Main",
                Assets = db
            });

            var report = ShanyuEngine.Diagnostics.EngineDiagnostics.Run(new ShanyuEngine.Diagnostics.ProjectDiagnosticsOptions
            {
                ProjectRoot = projectPath,
                Assets = db,
                ExportDirectory = build.OutputDirectory
            });

            Assert.IsTrue(report.Success);
            Assert.AreEqual(0, report.ErrorCount);
            Assert.AreEqual(0, report.Issues.Count);
        }

        [TestMethod]
        public void EngineDiagnosticsFindsMissingProjectFilesAndSceneReferences()
        {
            var projectPath = Path.Combine(TempDirectory, "DiagnosticsBrokenProject");
            Directory.CreateDirectory(projectPath);

            var db = new ShanyuEngine.Assets.AssetDatabase(projectPath);
            db.RegisterAsset("Assets/Meshes/MissingRegisteredMesh.obj", Guid.NewGuid(), ShanyuEngine.Assets.AssetType.Mesh, "MissingRegisteredMesh");
            db.CreateObject("GhostMesh", "Assets/Meshes/__missing_scene_mesh__.shmesh", new System.Numerics.Vector3(1, 1, 1));

            var brokenExport = Path.Combine(TempDirectory, "BrokenExportPackage");
            Directory.CreateDirectory(Path.Combine(brokenExport, "Content"));
            File.WriteAllText(Path.Combine(brokenExport, ShanyuEngine.Build.BuildPipeline.ManifestFileName), "{}");

            var report = ShanyuEngine.Diagnostics.EngineDiagnostics.Run(new ShanyuEngine.Diagnostics.ProjectDiagnosticsOptions
            {
                ProjectRoot = projectPath,
                Assets = db,
                ExportDirectory = brokenExport
            });

            Assert.IsFalse(report.Success);
            Assert.IsTrue(report.ErrorCount >= 5);
            Assert.IsTrue(report.Issues.Any(i => i.Code == "PROJECT_MANIFEST_MISSING"));
            Assert.IsTrue(report.Issues.Any(i => i.Code == "PROJECT_ASSETS_FOLDER_MISSING"));
            Assert.IsTrue(report.Issues.Any(i => i.Code == "ASSET_FILE_MISSING" && i.Path.Contains("MissingRegisteredMesh")));
            Assert.IsTrue(report.Issues.Any(i => i.Code == "SCENE_MESH_REFERENCE_MISSING" && i.Path.Contains("__missing_scene_mesh__")));
        }
    }
}
