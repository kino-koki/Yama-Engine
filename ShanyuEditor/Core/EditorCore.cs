using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Input;
using ShanyuEngine.Core;
using ShanyuEngine.Editor;

namespace ShanyuEditor
{
    // ════════════════════════════════════════════════════════════════════════
    //  EngineScene — 编辑器场景数据模型 (UI 绑定用)
    // ════════════════════════════════════════════════════════════════════════

    public sealed class EngineScene
    {
        public string Name { get; set; } = "Untitled Scene";
        public bool IsActive { get; set; }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  EngineHost — 编辑器引擎宿主 (连接 EditorEngineBridge)
    //
    //  重写说明:
    //    原 EngineHost 是空壳桩方法。现在所有方法都实际调用
    //    EditorEngineBridge, 连接到真正的引擎功能 (ECS / Physics /
    //    Assets / Selection / RHI)。
    //
    //  架构:
    //    App.xaml.cs → EngineHost → EditorEngineBridge → 引擎子系统
    // ════════════════════════════════════════════════════════════════════════

    public sealed class EngineHost
    {
        // ── 桥接层 ──────────────────────────────────────────────────────────────

        /// <summary>
        /// 编辑器-引擎桥接实例。所有引擎操作通过此实例执行。
        /// </summary>
        public EditorEngineBridge? EngineBridge { get; private set; }

        // ── 撤销 / 重做 ─────────────────────────────────────────────────────────

        private readonly Stack<string> _undoStack = new();
        private readonly Stack<string> _redoStack = new();
        private const int MaxUndoDepth = 50;

        // ── 状态 ────────────────────────────────────────────────────────────────

        /// <summary>是否已初始化。</summary>
        public bool IsInitialized { get; private set; }

        // ─────────────────────────────────────────────────────────────────────────
        //  Initialize — 初始化引擎桥接
        // ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 初始化编辑器引擎。创建 EditorEngineBridge 并尝试初始化 RHI 设备。
        /// </summary>
        public void Initialize()
        {
            if (IsInitialized) return;

            // 确定项目路径 (使用临时目录作为默认)
            string projectPath = Path.Combine(Path.GetTempPath(), "ShanyuEngine_Projects");
            try { Directory.CreateDirectory(projectPath); } catch { }

            // 创建桥接层
            EngineBridge = new EditorEngineBridge(projectPath);

            // 转发桥接层日志到编辑器
            EngineBridge.OnLogMessage += (msg) =>
            {
                Debug.WriteLine($"[Bridge] {msg}");
            };

            // 初始化 RHI (尝试 D3D12, 失败则无 GPU 模式)
            EngineBridge.Initialize(null);

            IsInitialized = true;
            Debug.WriteLine("[EngineHost] 初始化完成 — EditorEngineBridge 已就绪");
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  Shutdown — 关闭引擎
        // ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 关闭引擎, 释放所有资源。
        /// </summary>
        public void Shutdown()
        {
            EngineBridge?.Dispose();
            EngineBridge = null;
            IsInitialized = false;
            _undoStack.Clear();
            _redoStack.Clear();
            Debug.WriteLine("[EngineHost] 已关闭");
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  场景操作
        // ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 创建新的空场景。
        /// </summary>
        public void CreateScene()
        {
            if (EngineBridge == null) return;

            PushUndo();
            EngineBridge.CreateNewScene();
            Debug.WriteLine("[EngineHost] 新场景已创建");
        }

        /// <summary>
        /// 打开场景对话框。使用 EditorState.CurrentScenePath 加载场景。
        /// (实际文件选择对话框由 UI 层处理, 这里执行加载逻辑)
        /// </summary>
        public void OpenSceneDialog()
        {
            if (EngineBridge == null) return;

            // 尝试从 State 获取路径
            var state = App.State;
            string scenePath = state?.CurrentScenePath ?? string.Empty;

            if (!string.IsNullOrWhiteSpace(scenePath) && File.Exists(scenePath))
            {
                PushUndo();
                EngineBridge.LoadScene(scenePath);
                Debug.WriteLine($"[EngineHost] 场景已加载: {scenePath}");
            }
            else
            {
                // 尝试在项目目录中查找 .shanyu 文件
                string projectPath = state?.CurrentProjectPath ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(projectPath) && Directory.Exists(projectPath))
                {
                    var files = Directory.GetFiles(projectPath, "*.shanyu", SearchOption.AllDirectories);
                    if (files.Length > 0)
                    {
                        PushUndo();
                        EngineBridge.LoadScene(files[0]);
                        state.CurrentScenePath = files[0];
                        Debug.WriteLine($"[EngineHost] 场景已加载: {files[0]}");
                        return;
                    }
                }

                Debug.WriteLine("[EngineHost] OpenSceneDialog: 未找到场景文件");
            }
        }

        /// <summary>
        /// 保存当前场景到 EditorState.CurrentScenePath。
        /// </summary>
        public void SaveScene()
        {
            if (EngineBridge == null) return;

            var state = App.State;
            string scenePath = state?.CurrentScenePath ?? string.Empty;

            if (string.IsNullOrWhiteSpace(scenePath))
            {
                SaveSceneAs();
                return;
            }

            EngineBridge.SaveScene(scenePath);
            if (state != null) state.IsModified = false;
            Debug.WriteLine($"[EngineHost] 场景已保存: {scenePath}");
        }

        /// <summary>
        /// 另存为。将场景保存到新路径 (基于当前路径生成)。
        /// (实际文件保存对话框由 UI 层处理)
        /// </summary>
        public void SaveSceneAs()
        {
            if (EngineBridge == null) return;

            var state = App.State;
            string basePath = state?.CurrentScenePath ?? "Untitled.shanyu";
            string dir = Path.GetDirectoryName(basePath) ?? Path.GetTempPath();
            string fileName = Path.GetFileNameWithoutExtension(basePath);
            string newPath = Path.Combine(dir, $"{fileName}_Copy.shanyu");

            EngineBridge.SaveScene(newPath);
            if (state != null)
            {
                state.CurrentScenePath = newPath;
                state.IsModified = false;
            }
            Debug.WriteLine($"[EngineHost] 场景已另存为: {newPath}");
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  编辑操作
        // ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 撤销上一个操作。通过场景快照恢复。
        /// </summary>
        public void Undo()
        {
            if (_undoStack.Count == 0)
            {
                Debug.WriteLine("[EngineHost] Undo: 无可撤销操作");
                return;
            }

            // 保存当前状态到 redo 栈
            string current = CaptureSceneSnapshot();
            _redoStack.Push(current);

            // 恢复上一个状态
            string snapshot = _undoStack.Pop();
            RestoreSceneSnapshot(snapshot);
            Debug.WriteLine("[EngineHost] Undo 已执行");
        }

        /// <summary>
        /// 重做上一个被撤销的操作。
        /// </summary>
        public void Redo()
        {
            if (_redoStack.Count == 0)
            {
                Debug.WriteLine("[EngineHost] Redo: 无可重做操作");
                return;
            }

            // 保存当前状态到 undo 栈
            string current = CaptureSceneSnapshot();
            _undoStack.Push(current);

            // 恢复 redo 状态
            string snapshot = _redoStack.Pop();
            RestoreSceneSnapshot(snapshot);
            Debug.WriteLine("[EngineHost] Redo 已执行");
        }

        /// <summary>
        /// 删除当前选中的所有 Actor。
        /// </summary>
        public void DeleteSelected()
        {
            if (EngineBridge == null) return;

            var selected = EngineBridge.GetSelectedActors();
            if (selected.Count == 0)
            {
                Debug.WriteLine("[EngineHost] DeleteSelected: 无选中对象");
                return;
            }

            PushUndo();
            foreach (var id in selected)
            {
                EngineBridge.DeleteActor(id);
            }
            Debug.WriteLine($"[EngineHost] 已删除 {selected.Count} 个对象");
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  工具操作
        // ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 生成 LOD (Level of Detail)。当前为占位实现。
        /// </summary>
        public void GenerateLOD()
        {
            if (EngineBridge == null) return;

            var selected = EngineBridge.GetSelectedActors();
            Debug.WriteLine($"[EngineHost] GenerateLOD: 为 {selected.Count} 个选中对象生成 LOD (占位)");
            // TODO: 调用引擎的 LOD 生成系统
        }

        /// <summary>
        /// 烘焙光照贴图。当前为占位实现。
        /// </summary>
        public void BakeLightmaps()
        {
            if (EngineBridge == null) return;

            Debug.WriteLine("[EngineHost] BakeLightmaps: 光照贴图烘焙 (占位)");
            // TODO: 调用引擎的光照烘焙系统
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  撤销/重做 辅助方法
        // ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 将当前场景状态推入撤销栈。
        /// </summary>
        private void PushUndo()
        {
            string snapshot = CaptureSceneSnapshot();
            _undoStack.Push(snapshot);
            _redoStack.Clear(); // 新操作清空 redo 栈

            // 限制栈深度
            while (_undoStack.Count > MaxUndoDepth)
            {
                // 转为数组移除最底部元素
                var items = _undoStack.ToArray();
                _undoStack.Clear();
                for (int i = items.Length - 1; i >= 1; i--)
                    _undoStack.Push(items[i]);
            }
        }

        /// <summary>
        /// 捕获当前场景的快照 (JSON 序列化)。
        /// </summary>
        private string CaptureSceneSnapshot()
        {
            if (EngineBridge == null) return "{}";

            var actors = EngineBridge.Assets.SceneObjects;
            var data = new List<SceneSnapshotData>();
            foreach (var obj in actors)
            {
                data.Add(new SceneSnapshotData
                {
                    Id = obj.Id,
                    Name = obj.Name,
                    Position = new[] { obj.Position.X, obj.Position.Y, obj.Position.Z },
                    Rotation = new[] { obj.Rotation.X, obj.Rotation.Y, obj.Rotation.Z, obj.Rotation.W },
                    Scale = new[] { obj.Scale.X, obj.Scale.Y, obj.Scale.Z },
                    MeshAssetPath = obj.MeshAssetPath,
                    IsVisible = obj.IsVisible,
                });
            }
            return JsonSerializer.Serialize(data);
        }

        /// <summary>
        /// 从快照恢复场景状态。
        /// </summary>
        private void RestoreSceneSnapshot(string snapshot)
        {
            if (EngineBridge == null || string.IsNullOrWhiteSpace(snapshot)) return;

            try
            {
                var data = JsonSerializer.Deserialize<List<SceneSnapshotData>>(snapshot);
                if (data == null) return;

                EngineBridge.CreateNewScene();

                foreach (var item in data)
                {
                    var pos = item.Position != null && item.Position.Length >= 3
                        ? new Vector3(item.Position[0], item.Position[1], item.Position[2])
                        : Vector3.Zero;
                    var rot = item.Rotation != null && item.Rotation.Length >= 4
                        ? new Quaternion(item.Rotation[0], item.Rotation[1], item.Rotation[2], item.Rotation[3])
                        : Quaternion.Identity;
                    var scale = item.Scale != null && item.Scale.Length >= 3
                        ? new Vector3(item.Scale[0], item.Scale[1], item.Scale[2])
                        : Vector3.One;

                    var id = EngineBridge.CreateActor(item.Name ?? "GameObject", pos);
                    EngineBridge.SetActorTransform(id, pos, rot, scale);

                    var obj = EngineBridge.FindActor(id);
                    if (obj != null)
                    {
                        obj.MeshAssetPath = item.MeshAssetPath ?? string.Empty;
                        obj.IsVisible = item.IsVisible;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[EngineHost] 恢复快照失败: {ex.Message}");
            }
        }

        // ── 快照数据传输对象 ────────────────────────────────────────────────────

        private sealed class SceneSnapshotData
        {
            public Guid Id { get; set; }
            public string Name { get; set; } = "GameObject";
            public float[] Position { get; set; } = new float[3];
            public float[] Rotation { get; set; } = new float[4];
            public float[] Scale { get; set; } = new float[3];
            public string MeshAssetPath { get; set; } = string.Empty;
            public bool IsVisible { get; set; } = true;
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  CommandRegistry — 命令注册表
    // ════════════════════════════════════════════════════════════════════════

    public sealed class CommandRegistry
    {
        private readonly Dictionary<string, ICommand> _commands = new(StringComparer.OrdinalIgnoreCase);

        public void Register(string name, ICommand command)
        {
            _commands[name] = command;
        }

        public bool Execute(string name, object? parameter = null)
        {
            if (!_commands.TryGetValue(name, out var command) || !command.CanExecute(parameter)) return false;
            command.Execute(parameter);
            return true;
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  RelayCommand — ICommand 实现
    // ════════════════════════════════════════════════════════════════════════

    public sealed class RelayCommand : ICommand
    {
        private readonly Action<object?> _execute;
        private readonly Predicate<object?>? _canExecute;

        public RelayCommand(Action<object?> execute, Predicate<object?>? canExecute = null)
        {
            _execute = execute;
            _canExecute = canExecute;
        }

        public event EventHandler? CanExecuteChanged;
        public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;
        public void Execute(object? parameter) => _execute(parameter);
        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }

    // ════════════════════════════════════════════════════════════════════════
    //  EditorState — 编辑器状态 (INotifyPropertyChanged)
    // ════════════════════════════════════════════════════════════════════════

    public sealed class EditorState : INotifyPropertyChanged
    {
        private string _currentScenePath = string.Empty;
        private string _currentProjectPath = string.Empty;
        private string _currentProjectName = "Untitled";
        private bool _isModified;
        private ShanyuEngine.Core.GizmoMode _gizmoMode;

        public ObservableCollection<string> ConsoleMessages { get; } = new();

        public string CurrentScenePath
        {
            get => _currentScenePath;
            set { _currentScenePath = value; OnPropertyChanged(); }
        }

        public string CurrentProjectPath
        {
            get => _currentProjectPath;
            set { _currentProjectPath = value; OnPropertyChanged(); }
        }

        public string CurrentProjectName
        {
            get => _currentProjectName;
            set { _currentProjectName = value; OnPropertyChanged(); }
        }

        public bool IsModified
        {
            get => _isModified;
            set { _isModified = value; OnPropertyChanged(); }
        }

        public ShanyuEngine.Core.GizmoMode GizmoMode
        {
            get => _gizmoMode;
            set { _gizmoMode = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public void LogMessage(string message)
        {
            ConsoleMessages.Add($"[{DateTime.Now:HH:mm:ss}] {message}");
            if (ConsoleMessages.Count > 500) ConsoleMessages.RemoveAt(0);
        }

        private void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  BlenderInterop — Blender 互操作 (占位)
    // ════════════════════════════════════════════════════════════════════════

    public static class BlenderInterop
    {
        public static void Launch() { }
        public static Task ImportScene() => Task.CompletedTask;
        public static Task SyncWithEngine() => Task.CompletedTask;
    }

    // ════════════════════════════════════════════════════════════════════════
    //  PluginImporter — 插件导入器 (占位)
    // ════════════════════════════════════════════════════════════════════════

    public static class PluginImporter
    {
        public static void ImportFromUnity() { }
        public static void ImportFromUnreal() { }
    }
}
