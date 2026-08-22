using Microsoft.UI.Xaml;
using System;
using System.Diagnostics;
using ShanyuEngine.Core;
using ShanyuEngine.Config;
using ShanyuEngine.Projects;
using ShanyuEngine.ECS;
using ShanyuEngine.Assets;
using ShanyuEngine.Editor;

namespace ShanyuEditor
{
    public partial class App : Application
    {
        public static EngineHost Engine { get; private set; } = null!;
        public static EditorState State { get; private set; } = null!;
        public static CommandRegistry Commands { get; private set; } = null!;
        public static EngineSystems Systems { get; private set; } = null!;
        public static EngineScene CurrentScene { get; private set; } = null!;

        // PIE Integration
        public static PieIntegration? Pie { get; private set; }

        private Window? _mainWindow;
        public Window MainWindow => _mainWindow!;

        public App()
        {
            UnhandledException += App_UnhandledException;
        }

        private void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
        {
            Debug.WriteLine($"[ShanyuEditor] Unhandled: {e.Message}");
            e.Handled = true;
        }

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            Bootstrap();
            _mainWindow = new MainWindow();
            _mainWindow.Activate();
            _mainWindow.Closed += (s, e) => OnShutdown();
        }

        private void Bootstrap()
        {
            Engine = new EngineHost();
            State = new EditorState();
            Commands = new CommandRegistry();
            Systems = new EngineSystems();
            CurrentScene = new EngineScene { Name = "Untitled Scene", IsActive = true };

            try 
            { 
                Engine.Initialize(); 
            }
            catch (Exception ex) 
            { 
                Debug.WriteLine($"Engine init: {ex.Message}"); 
            }

            // Initialize PIE Integration
            Pie = new PieIntegration(
                Systems,
                Engine.EngineBridge?.ECS ?? new EntityComponentSystem(),
                Engine.EngineBridge?.Assets ?? Systems.Assets);
            Pie.Initialize();

            // Wire PIE events to Editor State
            Pie.OnPieStateChanged += (oldState, newState) =>
            {
                State.GizmoMode = newState == PlayModeState.Playing ? GizmoMode.Universal : GizmoMode.Translate;
            };

            Pie.OnPieLog += (msg, level) =>
            {
                State?.LogMessage($"[{level}] {msg}");
            };

            Systems.ConsoleVariables.OnLogMessage += (msg) =>
            {
                Debug.WriteLine($"[Console:{msg.Level}] {msg.Message}");
            };
            Systems.OnLogMessage += (msg) => Debug.WriteLine($"[Engine] {msg}");

            // Register standard editor commands
            RegisterEditorCommands();

            Systems.OnLogMessage += (msg) =>
            {
                State?.LogMessage(msg);
            };

            Debug.WriteLine("[ShanyuEditor] Bootstrap complete.");
            Debug.WriteLine($"[ShanyuEditor] {Systems.ConsoleVariables.Variables.Count} console variables registered.");
            Debug.WriteLine($"[ShanyuEditor] Input system initialized.");
            Debug.WriteLine($"[ShanyuEditor] Audio system: {Systems.Audio.GetBusStates().Count} buses.");
            Debug.WriteLine($"[ShanyuEditor] PIE Integration ready (F5 to play).");
        }

        private void RegisterEditorCommands()
        {
            // File commands
            Commands.Register("File.New", new RelayCommand(_ => Engine.CreateScene()));
            Commands.Register("File.Open", new RelayCommand(_ => Engine.OpenSceneDialog()));
            Commands.Register("File.Save", new RelayCommand(_ => Engine.SaveScene()));
            Commands.Register("File.SaveAs", new RelayCommand(_ => Engine.SaveSceneAs()));
            Commands.Register("File.Exit", new RelayCommand(_ => _mainWindow?.Close()));

            // Edit commands
            Commands.Register("Edit.Undo", new RelayCommand(_ => Engine.Undo()));
            Commands.Register("Edit.Redo", new RelayCommand(_ => Engine.Redo()));
            Commands.Register("Edit.Delete", new RelayCommand(_ => Engine.DeleteSelected()));

            // View commands
            Commands.Register("View.Transform.Translate", new RelayCommand(_ => State.GizmoMode = GizmoMode.Translate));
            Commands.Register("View.Transform.Rotate", new RelayCommand(_ => State.GizmoMode = GizmoMode.Rotate));
            Commands.Register("View.Transform.Scale", new RelayCommand(_ => State.GizmoMode = GizmoMode.Scale));

            // DCC commands
            Commands.Register("Blender.Launch", new RelayCommand(async _ => await System.Threading.Tasks.Task.Run(() => BlenderInterop.Launch())));
            Commands.Register("Blender.Import", new RelayCommand(async _ => await BlenderInterop.ImportScene()));
            Commands.Register("Blender.Sync", new RelayCommand(async _ => await BlenderInterop.SyncWithEngine()));

            // Tools commands
            Commands.Register("Tools.LOD.Generate", new RelayCommand(_ => Engine.GenerateLOD()));
            Commands.Register("Tools.Bake.Lightmaps", new RelayCommand(_ => Engine.BakeLightmaps()));
            Commands.Register("Tools.Import.Unity", new RelayCommand(_ => PluginImporter.ImportFromUnity()));
            Commands.Register("Tools.Import.Unreal", new RelayCommand(_ => PluginImporter.ImportFromUnreal()));

            // PIE commands (F5 Play, Ctrl+F5 Stop, etc.)
            Commands.Register("Engine.Play", new RelayCommand(_ => Pie?.Play()));
            Commands.Register("Engine.Pause", new RelayCommand(_ => Pie?.Pause()));
            Commands.Register("Engine.Stop", new RelayCommand(_ => Pie?.Stop()));
            Commands.Register("Engine.StepFrame", new RelayCommand(_ => Pie?.StepSingleFrame()));
            Commands.Register("Engine.HotReload", new RelayCommand(_ => Pie?.RequestHotReload()));
            Commands.Register("Engine.TogglePause", new RelayCommand(_ => Pie?.TogglePause()));
        }

        public static void UseProject(ShanyuProject project)
        {
            State.CurrentProjectPath = project.ProjectPath;
            State.CurrentProjectName = project.Name;
            State.CurrentScenePath = System.IO.Path.Combine(project.ProjectPath, project.StartupScene.Replace('/', System.IO.Path.DirectorySeparatorChar));
            State.IsModified = false;
            Systems.SetProject(project.ProjectPath, project.Name);
            Systems.OnLogMessage += (msg) => State?.LogMessage(msg);

            // Re-initialize PIE with new project
            if (Pie != null && Engine.EngineBridge != null)
            {
                Pie = new PieIntegration(
                    Systems,
                    Engine.EngineBridge.ECS,
                    Engine.EngineBridge.Assets);
                Pie.Initialize();
            }
        }

        private void OnShutdown()
        {
            try 
            { 
                Pie?.Dispose();
                Engine?.Shutdown(); 
            } 
            catch { }
        }
    }
}
