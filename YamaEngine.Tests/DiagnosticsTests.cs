using System;
using System.IO;
using System.Numerics;
using System.Linq;
using System.Collections.Generic;
using YamaEngine.Assets;
using YamaEngine.Build;
using YamaEngine.Diagnostics;
using YamaEngine.Projects;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests
{
    public static class DiagnosticsTests
    {
        public static void TestEngineDiagnosticsReportsHealthyProject(string tempRoot)
        {
            var projectPath = Path.Combine(tempRoot, "DiagnosticsHealthyProject");
            Directory.CreateDirectory(Path.Combine(projectPath, "Assets"));
            ProjectSystem.SaveProject(new YamaProject
            {
                Name = "DiagnosticsHealthyProject",
                ProjectPath = projectPath
            });

            var sourcePath = Path.Combine(tempRoot, "diagnostics_cube.obj");
            File.WriteAllText(sourcePath, "o Cube\nv 0 0 0\nv 1 0 0\nv 0 1 0\nf 1 2 3\n");

            var db = new AssetDatabase(projectPath);
            var imported = db.ImportAsset(sourcePath);
            db.CreateObject("DiagnosticsCube", imported!.AssetPath, new Vector3(0, 0, 0));

            var build = BuildPipeline.Build(new BuildOptions
            {
                ProjectName = "DiagnosticsHealthyProject",
                OutputDirectory = Path.Combine(tempRoot, "DiagnosticsHealthyExport"),
                SceneName = "Main",
                Assets = db
            });

            var report = EngineDiagnostics.Run(new ProjectDiagnosticsOptions
            {
                ProjectRoot = projectPath,
                Assets = db,
                ExportDirectory = build.OutputDirectory
            });

            AssertTrue(report.Success, "Healthy project diagnostics should succeed.");
            AssertEqual(0, report.ErrorCount, "Healthy project should have no diagnostic errors.");
            AssertEqual(0, report.Issues.Count, "Healthy project should have no diagnostic issues.");
        }

        public static void TestEngineDiagnosticsFindsMissingProjectFilesAndSceneReferences(string tempRoot)
        {
            var projectPath = Path.Combine(tempRoot, "DiagnosticsBrokenProject");
            Directory.CreateDirectory(projectPath);

            var db = new AssetDatabase(projectPath);
            db.RegisterAsset("Assets/Meshes/MissingRegisteredMesh.obj", Guid.NewGuid(), AssetType.Mesh, "MissingRegisteredMesh");
            db.CreateObject("GhostMesh", "Assets/Meshes/__missing_scene_mesh__.shmesh", new Vector3(1, 1, 1));

            var brokenExport = Path.Combine(tempRoot, "BrokenExportPackage");
            Directory.CreateDirectory(Path.Combine(brokenExport, "Content"));
            File.WriteAllText(Path.Combine(brokenExport, BuildPipeline.ManifestFileName), "{}");

            var report = EngineDiagnostics.Run(new ProjectDiagnosticsOptions
            {
                ProjectRoot = projectPath,
                Assets = db,
                ExportDirectory = brokenExport
            });

            AssertTrue(!report.Success, "Broken project diagnostics should fail.");
            AssertTrue(report.ErrorCount >= 5, "Broken project should report multiple diagnostic errors.");
            AssertTrue(report.Issues.Any(i => i.Code == "PROJECT_MANIFEST_MISSING"), "Diagnostics should report missing project manifest.");
            AssertTrue(report.Issues.Any(i => i.Code == "PROJECT_ASSETS_FOLDER_MISSING"), "Diagnostics should report missing Assets folder.");
            AssertTrue(report.Issues.Any(i => i.Code == "ASSET_FILE_MISSING" && i.Path.Contains("MissingRegisteredMesh")), "Diagnostics should report missing registered asset file.");
            AssertTrue(report.Issues.Any(i => i.Code == "SCENE_MESH_REFERENCE_MISSING" && i.Path.Contains("__missing_scene_mesh__")), "Diagnostics should report missing scene mesh reference.");
            AssertTrue(report.Issues.Any(i => i.Code == "EXPORT_SCENE_MISSING"), "Diagnostics should report missing exported scene.");
            AssertTrue(report.Issues.Any(i => i.Code == "EXPORT_REPORT_MISSING"), "Diagnostics should report missing build report.");
        }
    }
}
