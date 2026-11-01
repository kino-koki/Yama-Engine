using System.Text.Json;

namespace YamaEngine.Projects
{
    public sealed class YamaProject
    {
        public string Name { get; set; } = "Untitled";
        public string Engine { get; set; } = "Yama Engine";
        public int FormatVersion { get; set; } = 1;
        public string ProjectPath { get; set; } = string.Empty;
        public string StartupScene { get; set; } = "Content/Scenes/Main.yama";
        public List<string> Folders { get; set; } = new()
        {
            "Assets",
            "Content/Scenes",
            "Content/Assets",
            "Content/Materials",
            "Content/Scripts",
            "Builds",
            "Library",
            "ProjectSettings"
        };
    }

    public static class ProjectSystem
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        public static YamaProject CreateProject(string projectPath, string projectName, string templatePath)
        {
            if (string.IsNullOrWhiteSpace(projectPath)) throw new ArgumentException("Project path is required.", nameof(projectPath));
            if (string.IsNullOrWhiteSpace(projectName)) throw new ArgumentException("Project name is required.", nameof(projectName));
            if (!Directory.Exists(templatePath)) throw new DirectoryNotFoundException($"Template not found: {templatePath}");

            Directory.CreateDirectory(projectPath);
            CopyDirectory(templatePath, projectPath);

            var project = LoadProject(projectPath);
            project.Name = projectName;
            project.ProjectPath = projectPath;
            EnsureDefaultFolders(project);

            foreach (var folder in project.Folders)
            {
                Directory.CreateDirectory(Path.Combine(projectPath, folder.Replace('/', Path.DirectorySeparatorChar)));
            }

            SaveProject(project);
            return project;
        }

        private static void EnsureDefaultFolders(YamaProject project)
        {
            foreach (var folder in new[] { "Assets", "Builds", "Library", "ProjectSettings", "Content/Scenes", "Content/Assets", "Content/Materials", "Content/Scripts" })
            {
                if (!project.Folders.Any(f => string.Equals(f, folder, StringComparison.OrdinalIgnoreCase)))
                {
                    project.Folders.Add(folder);
                }
            }
        }

        public static YamaProject LoadProject(string projectPath)
        {
            var manifestPath = Path.Combine(projectPath, "yama.project.json");
            if (!File.Exists(manifestPath)) throw new FileNotFoundException("Project manifest not found.", manifestPath);

            var project = JsonSerializer.Deserialize<YamaProject>(File.ReadAllText(manifestPath), JsonOptions)
                          ?? throw new InvalidOperationException($"Invalid project manifest: {manifestPath}");
            project.ProjectPath = projectPath;
            return project;
        }

        public static void SaveProject(YamaProject project)
        {
            if (string.IsNullOrWhiteSpace(project.ProjectPath)) throw new InvalidOperationException("ProjectPath is required before saving.");
            Directory.CreateDirectory(project.ProjectPath);
            var manifestPath = Path.Combine(project.ProjectPath, "yama.project.json");
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(project, JsonOptions));
        }

        private static void CopyDirectory(string sourceDirectory, string targetDirectory)
        {
            Directory.CreateDirectory(targetDirectory);
            foreach (var directory in Directory.EnumerateDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(sourceDirectory, directory);
                Directory.CreateDirectory(Path.Combine(targetDirectory, relative));
            }

            foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(sourceDirectory, file);
                var target = Path.Combine(targetDirectory, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, true);
            }
        }
    }
}
