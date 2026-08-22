using ShanyuEngine.Assets;
using ShanyuEngine.Build;

namespace ShanyuEngine.Diagnostics
{
    public static class EngineDiagnostics
    {
        public static ProjectDiagnosticsReport Run(ProjectDiagnosticsOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            var report = new ProjectDiagnosticsReport();

            CheckProjectRoot(options, report);
            CheckRegisteredAssets(options, report);
            CheckSceneReferences(options, report);
            CheckExportPackage(options, report);

            report.Success = report.ErrorCount == 0;
            return report;
        }

        private static void CheckProjectRoot(ProjectDiagnosticsOptions options, ProjectDiagnosticsReport report)
        {
            if (string.IsNullOrWhiteSpace(options.ProjectRoot) || !Directory.Exists(options.ProjectRoot))
            {
                report.AddError("PROJECT_ROOT_MISSING", options.ProjectRoot, "Project root folder does not exist.");
                return;
            }

            var manifestPath = Path.Combine(options.ProjectRoot, "shanyu.project.json");
            if (!File.Exists(manifestPath))
            {
                report.AddError("PROJECT_MANIFEST_MISSING", manifestPath, "Project manifest shanyu.project.json is missing.");
            }

            var assetsPath = Path.Combine(options.ProjectRoot, "Assets");
            if (!Directory.Exists(assetsPath))
            {
                report.AddError("PROJECT_ASSETS_FOLDER_MISSING", assetsPath, "Project Assets folder is missing.");
            }
        }

        private static void CheckRegisteredAssets(ProjectDiagnosticsOptions options, ProjectDiagnosticsReport report)
        {
            if (options.Assets == null) return;
            foreach (var asset in options.Assets.GetAllAssets())
            {
                if (string.IsNullOrWhiteSpace(asset.AssetPath)) continue;
                var path = Path.Combine(options.Assets.RootPath, asset.AssetPath.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(path))
                {
                    report.AddError("ASSET_FILE_MISSING", asset.AssetPath, $"Registered asset file is missing: {asset.AssetPath}");
                }

                var metaPath = path + ".shmeta";
                if (File.Exists(path) && !File.Exists(metaPath))
                {
                    report.AddWarning("ASSET_META_MISSING", asset.AssetPath, $"Asset meta file is missing: {asset.AssetPath}.shmeta");
                }
            }
        }

        private static void CheckSceneReferences(ProjectDiagnosticsOptions options, ProjectDiagnosticsReport report)
        {
            if (options.Assets == null) return;
            foreach (var obj in options.Assets.SceneObjects)
            {
                if (!string.IsNullOrWhiteSpace(obj.MeshAssetPath))
                {
                    var meshPath = Path.Combine(options.Assets.RootPath, obj.MeshAssetPath.Replace('/', Path.DirectorySeparatorChar));
                    if (!File.Exists(meshPath))
                    {
                        report.AddError("SCENE_MESH_REFERENCE_MISSING", obj.MeshAssetPath, $"Scene object '{obj.Name}' references a missing mesh.");
                    }
                }

                if (!string.IsNullOrWhiteSpace(obj.MaterialAssetPath))
                {
                    var materialPath = Path.Combine(options.Assets.RootPath, obj.MaterialAssetPath.Replace('/', Path.DirectorySeparatorChar));
                    if (!File.Exists(materialPath))
                    {
                        report.AddError("SCENE_MATERIAL_REFERENCE_MISSING", obj.MaterialAssetPath, $"Scene object '{obj.Name}' references a missing material.");
                    }
                }
            }
        }

        private static void CheckExportPackage(ProjectDiagnosticsOptions options, ProjectDiagnosticsReport report)
        {
            if (string.IsNullOrWhiteSpace(options.ExportDirectory)) return;
            if (!Directory.Exists(options.ExportDirectory))
            {
                report.AddError("EXPORT_DIRECTORY_MISSING", options.ExportDirectory, "Export package folder is missing.");
                return;
            }

            var manifest = Path.Combine(options.ExportDirectory, BuildPipeline.ManifestFileName);
            if (!File.Exists(manifest))
            {
                report.AddError("EXPORT_MANIFEST_MISSING", manifest, "Export manifest is missing.");
            }

            var scene = Path.Combine(options.ExportDirectory, "Content", "MainScene.shanyu");
            if (!File.Exists(scene))
            {
                report.AddError("EXPORT_SCENE_MISSING", scene, "Exported scene file is missing.");
            }

            var reportPath = Path.Combine(options.ExportDirectory, BuildPipeline.ReportFileName);
            if (!File.Exists(reportPath))
            {
                report.AddError("EXPORT_REPORT_MISSING", reportPath, "Build report is missing.");
            }
        }
    }

    public sealed class ProjectDiagnosticsOptions
    {
        public string ProjectRoot { get; set; } = string.Empty;
        public AssetDatabase? Assets { get; set; }
        public string ExportDirectory { get; set; } = string.Empty;
    }

    public sealed class ProjectDiagnosticsReport
    {
        public bool Success { get; set; }
        public List<ProjectDiagnosticIssue> Issues { get; set; } = new();
        public int ErrorCount => Issues.Count(i => i.Severity == ProjectDiagnosticSeverity.Error);
        public int WarningCount => Issues.Count(i => i.Severity == ProjectDiagnosticSeverity.Warning);

        public void AddError(string code, string path, string message)
        {
            Issues.Add(new ProjectDiagnosticIssue(code, ProjectDiagnosticSeverity.Error, path, message));
        }

        public void AddWarning(string code, string path, string message)
        {
            Issues.Add(new ProjectDiagnosticIssue(code, ProjectDiagnosticSeverity.Warning, path, message));
        }
    }

    public sealed record ProjectDiagnosticIssue(string Code, ProjectDiagnosticSeverity Severity, string Path, string Message);

    public enum ProjectDiagnosticSeverity
    {
        Info,
        Warning,
        Error
    }
}
