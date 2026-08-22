using System;
using System.IO;
using System.Numerics;
using System.Linq;
using System.Collections.Generic;
using System.Text.Json;
using ShanyuEngine.Assets;
using ShanyuEngine.Build;
using static ShanyuEngine.Tests.TestHelpers;

namespace ShanyuEngine.Tests
{
    public static class BuildTests
    {
        public static void TestBuildPipelineExportsPlayableDemoLayout(string tempRoot)
        {
            var projectPath = Path.Combine(tempRoot, "BuildProject");
            Directory.CreateDirectory(projectPath);
            var sourcePath = Path.Combine(tempRoot, "build_cube.obj");
            File.WriteAllText(sourcePath, "o Cube\nv 0 0 0\nv 1 0 0\nv 0 1 0\nf 1 2 3\n");

            var db = new AssetDatabase(projectPath);
            var imported = db.ImportAsset(sourcePath);
            var obj = db.CreateObject("BuildCube", imported!.AssetPath, new Vector3(1, 2, 3));
            obj.BaseColor = new Vector3(0.1f, 0.2f, 0.3f);

            var outputRoot = Path.Combine(tempRoot, "Exports");
            var result = BuildPipeline.Build(new BuildOptions
            {
                ProjectName = "BuildProject",
                OutputDirectory = outputRoot,
                SceneName = "Main",
                Assets = db
            });

            var exportRoot = Path.Combine(outputRoot, BuildPipeline.DemoFolderName);
            AssertTrue(result.Success, "BuildPipeline should return success.");
            AssertEqual(exportRoot, result.OutputDirectory, "Build result should expose export folder.");
            AssertTrue(File.Exists(Path.Combine(exportRoot, BuildPipeline.ManifestFileName)), "Build manifest should be written.");
            AssertTrue(File.Exists(Path.Combine(exportRoot, "Content", "MainScene.shanyu")), "Scene should be exported into Content.");
            AssertTrue(File.Exists(Path.Combine(exportRoot, "Content", imported.AssetPath.Replace('/', Path.DirectorySeparatorChar))), "Imported asset should be copied into Content.");
            AssertTrue(File.Exists(Path.Combine(exportRoot, BuildPipeline.LaunchScriptFileName)), "Launch script should be written.");
            AssertTrue(File.Exists(Path.Combine(exportRoot, BuildPipeline.ReadmeFileName)), "Export README should be written.");
        }

        public static void TestBuildPipelineWritesBuildReport(string tempRoot)
        {
            var projectPath = Path.Combine(tempRoot, "ReportProject");
            Directory.CreateDirectory(projectPath);

            // 一个真实存在的资产
            var sourcePath = Path.Combine(tempRoot, "report_cube.obj");
            File.WriteAllText(sourcePath, "o Cube\nv 0 0 0\nv 1 0 0\nv 0 1 0\nf 1 2 3\n");

            var db = new AssetDatabase(projectPath);
            var imported = db.ImportAsset(sourcePath);
            var obj = db.CreateObject("ReportCube", imported!.AssetPath, new Vector3(0, 0, 0));

            // 一个会指向不存在资产的对象，制造 missing reference
            var ghost = db.CreateObject("GhostMesh", "Assets/Meshes/__does_not_exist__.shmesh", new Vector3(1, 1, 1));

            var outputRoot = Path.Combine(tempRoot, "ReportExports");
            var result = BuildPipeline.Build(new BuildOptions
            {
                ProjectName = "ReportProject",
                OutputDirectory = outputRoot,
                SceneName = "Main",
                Assets = db
            });

            var exportRoot = Path.Combine(outputRoot, BuildPipeline.DemoFolderName);
            AssertTrue(result.Success, "Build should still succeed even with missing references.");

            // BuildResult 必须暴露报告路径
            AssertTrue(!string.IsNullOrWhiteSpace(result.ReportPath), "BuildResult should expose ReportPath.");
            AssertEqual(Path.Combine(exportRoot, BuildPipeline.ReportFileName), result.ReportPath, "ReportPath should point under export root.");
            AssertTrue(File.Exists(result.ReportPath), "build-report.json should be written on disk.");

            // 报告内容字段
            using var doc = JsonDocument.Parse(File.ReadAllText(result.ReportPath));
            var root = doc.RootElement;

            AssertTrue(root.TryGetProperty("projectName", out var projectName), "Report should contain projectName.");
            AssertEqual("ReportProject", projectName.GetString(), "Report projectName should match input.");

            AssertTrue(root.TryGetProperty("sceneName", out var sceneName), "Report should contain sceneName.");
            AssertEqual("Main", sceneName.GetString(), "Report sceneName should match input.");

            AssertTrue(root.TryGetProperty("scenePath", out var scenePath), "Report should contain scenePath.");
            AssertEqual("Content/MainScene.shanyu", scenePath.GetString(), "Report scenePath should be relative to export root.");

            AssertTrue(root.TryGetProperty("objectCount", out var objectCount), "Report should contain objectCount.");
            AssertEqual(2, objectCount.GetInt32(), "Report objectCount should equal number of scene objects.");

            AssertTrue(root.TryGetProperty("assetCount", out var assetCount), "Report should contain assetCount.");
            AssertEqual(1, assetCount.GetInt32(), "Report assetCount should equal copied assets.");

            AssertTrue(root.TryGetProperty("runtimeIncluded", out var runtimeIncluded), "Report should contain runtimeIncluded.");
            AssertEqual(false, runtimeIncluded.GetBoolean(), "Runtime should not be included when no runtime path was supplied.");

            AssertTrue(root.TryGetProperty("warnings", out var warnings) && warnings.ValueKind == JsonValueKind.Array, "Report should contain warnings array.");
            AssertTrue(warnings.GetArrayLength() >= 1, "Report should record at least one warning (missing runtime).");

            AssertTrue(root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array, "Report should contain errors array.");
            AssertEqual(0, errors.GetArrayLength(), "Successful build should have empty errors.");

            AssertTrue(root.TryGetProperty("missingReferences", out var missing) && missing.ValueKind == JsonValueKind.Array, "Report should contain missingReferences array.");
            AssertTrue(missing.GetArrayLength() >= 1, "Report should record at least one missing reference (the ghost mesh).");
            AssertTrue(missing.EnumerateArray().Any(m => m.GetString()?.Contains("__does_not_exist__") == true), "Missing reference list should mention the ghost mesh path.");

            AssertTrue(root.TryGetProperty("exportedAtUtc", out var exportedAt), "Report should contain exportedAtUtc.");
            AssertTrue(DateTime.TryParse(exportedAt.GetString(), out _), "exportedAtUtc should be a valid timestamp.");

            AssertTrue(root.TryGetProperty("engine", out var engine), "Report should contain engine label.");
            AssertEqual("Shanyu Engine", engine.GetString(), "Engine label should be stable.");
        }
    }
}
