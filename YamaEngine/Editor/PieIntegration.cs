// ==================================================================================================
//  PieIntegration.cs - yama引擎 PIE (Play In Editor) 集成层
// ==================================================================================================
//  将 EditorPlayModeController、ScriptEngine、PhysicsBridge、EditorWorld 串联起来，
//  实现真正的 F5 播放功能。
//
//  架构：
//    MainWindow (UI) 
//      -> EngineHost (桥接)
//        -> PieIntegration (PIE 核心)
//          -> EditorPlayModeController (状态机 + 生命周期)
//            -> ScriptEngine + ScriptRegistry (脚本执行)
//            -> PhysicsBridge (物理模拟)
//            -> EditorWorld (场景状态快照/恢复)
//          -> RuntimeGameLoop (运行时循环，用于导出构建)
// ==================================================================================================

using System;
using System.Collections.Generic;
using System.Numerics;
using YamaEngine.Assets;
using YamaEngine.ECS;
using YamaEngine.Editor;
using YamaEngine.Editor.LevelEditor;
using YamaEngine.Physics;
using YamaEngine.Runtime;
using YamaEngine.Runtime.Physics;
using YamaEngine.Scripting;
using YamaEngine.Core;
using YamaEngine.Rendering;
using LogLevel = YamaEngine.Config.LogLevel;

namespace YamaEngine.Editor
{
    // ================================================================================================
    //  PieIntegration - PIE 集成管理器
    // ================================================================================================

    /// <summary>
    /// PIE 集成管理器。连接编辑器 UI、脚本引擎、物理桥接、场景状态管理。
    /// 这是让 "F5 播放" 真正工作的核心枢纽。
    /// </summary>
    public sealed class PieIntegration : IDisposable
    {
        // 核心子系统

        private readonly EngineSystems _engineSystems;
        private readonly EntityComponentSystem _ecs;
        private readonly AssetDatabase _assetDatabase;
        private readonly ScriptEngine _scriptEngine;
        private readonly EditorPlayModeController _playModeController;
        private readonly PhysicsBridge _physicsBridge;
        private readonly EditorWorld _editorWorld;

        // 运行时循环（用于导出构建时的无头运行）

        private RuntimeGameLoop? _runtimeGameLoop;
        private YamaEngine.Runtime.RuntimeWorld? _runtimeWorld;

        // 状态

        private bool _disposed;
        private bool _initialized;

        // 事件

        /// <summary>PIE 状态变化事件。参数：旧状态, 新状态。</summary>
        public event Action<PlayModeState, PlayModeState>? OnPieStateChanged;

        /// <summary>PIE 日志事件。参数：消息, 级别。</summary>
        public event Action<string, LogLevel>? OnPieLog;

        /// <summary>热重载请求事件。参数：脚本状态快照。</summary>
        public event Action<ScriptStateSnapshot>? OnHotReloadRequested;

        /// <summary>热重载完成事件。</summary>
        public event Action? OnHotReloadCompleted;

        // 属性

        /// <summary>当前 PIE 状态。</summary>
        public PlayModeState State => _playModeController.State;

        /// <summary>是否在播放模式（包含暂停）。</summary>
        public bool IsInPlayMode => _playModeController.IsInPlayMode;

        /// <summary>是否正在播放（未暂停）。</summary>
        public bool IsPlaying => _playModeController.IsPlaying;

        /// <summary>是否暂停。</summary>
        public bool IsPaused => _playModeController.IsPaused;

        /// <summary>累计运行时间（秒）。</summary>
        public float PlayTimeSeconds => _playModeController.PlayTimeSeconds;

        /// <summary>当前帧号。</summary>
        public int PlayFrameIndex => _playModeController.PlayFrameIndex;

        /// <summary>脚本引擎实例（供外部访问编译/热重载）。</summary>
        public ScriptEngine ScriptEngine => _scriptEngine;

        /// <summary>物理桥接实例。</summary>
        public PhysicsBridge PhysicsBridge => _physicsBridge;

        /// <summary>编辑器世界实例。</summary>
        public EditorWorld EditorWorld => _editorWorld;

        // ================================================================================================
        //  构造
        // ================================================================================================

        /// <summary>
        /// 创建 PIE 集成管理器。
        /// </summary>
        /// <param name="engineSystems">引擎系统（单例，包含 Time/Physics/Input 等）。</param>
        /// <param name="ecs">ECS 实体组件系统。</param>
        /// <param name="assetDatabase">资产数据库。</param>
        public PieIntegration(EngineSystems engineSystems, EntityComponentSystem ecs, AssetDatabase assetDatabase)
        {
            _engineSystems = engineSystems ?? throw new ArgumentNullException(nameof(engineSystems));
            _ecs = ecs ?? throw new ArgumentNullException(nameof(ecs));
            _assetDatabase = assetDatabase ?? throw new ArgumentNullException(nameof(assetDatabase));

            // 1. 创建脚本引擎（绑定 ECS 和 EngineSystems）
            _scriptEngine = new ScriptEngine(_ecs, _engineSystems);

            // 2. 创建物理桥接（连接 ECS 和 PhysicsWorld）
            _physicsBridge = new PhysicsBridge(_ecs);

            // 3. 创建编辑器世界（场景状态管理）
            _editorWorld = new EditorWorld(_ecs, _assetDatabase, _engineSystems);

            // 4. 创建播放模式控制器（核心状态机）
            _playModeController = new EditorPlayModeController(
                _ecs,
                _scriptEngine.Registry,
                _physicsBridge);

            // 5. 订阅事件
            WireEvents();
        }

        // ================================================================================================
        //  初始化
        // ================================================================================================

        /// <summary>
        /// 初始化 PIE 集成。必须在所有子系统就绪后调用（通常在 EngineHost.Initialize 中）。
        /// </summary>
        public void Initialize()
        {
            if (_initialized) return;

            // 初始化物理世界
            _physicsBridge.Config.Gravity = new Vector3(0, -9.81f, 0);
            _physicsBridge.Config.FixedTimestep = 1f / 60f;
            _physicsBridge.Config.MaxSubSteps = 4;

            // 注册控制台命令
            RegisterConsoleCommands();

            _initialized = true;
            Log("PIE Integration initialized.", LogLevel.Info);
        }

        // ================================================================================================
        //  事件绑定
        // ================================================================================================

        private void WireEvents()
        {
            // PlayModeController 事件
            _playModeController.OnStateChanged += (oldState, newState) =>
            {
                OnPieStateChanged?.Invoke(oldState, newState);
                Log($"PIE State: {oldState} -> {newState}", LogLevel.Info);
            };

            _playModeController.OnLog += (msg, level) =>
            {
                OnPieLog?.Invoke(msg, level);
            };

            _playModeController.OnHotReloadRequested += (snapshot) =>
            {
                OnHotReloadRequested?.Invoke(snapshot);
            };

            _playModeController.OnHotReloadCompleted += () =>
            {
                OnHotReloadCompleted?.Invoke();
            };

            // ScriptEngine 事件
            _scriptEngine.OnCompileStarted += (source) =>
            {
                Log($"Compiling scripts: {source.AssemblyName} ({source.Files.Count} files)", LogLevel.Info);
            };

            _scriptEngine.OnCompileFinished += (result) =>
            {
                if (result.Success)
                {
                    Log($"Compilation succeeded: {result.ScriptTypeCount} types in {result.CompileTime.TotalMilliseconds:0}ms", LogLevel.Info);
                }
                else
                {
                    Log($"Compilation failed: {result.ErrorCount} errors, {result.WarningCount} warnings", LogLevel.Error);
                    foreach (var diag in result.GetErrors())
                    {
                        Log($"  {diag}", LogLevel.Error);
                    }
                }
            };

            _scriptEngine.OnHotReloadStarted += () =>
            {
                Log("Hot reload started...", LogLevel.Info);
            };

            _scriptEngine.OnHotReloadFinished += (result) =>
            {
                if (result.Success)
                {
                    var reloadMode = result.WasHotReload ? "hot-reload" : "cold-start";
                    Log($"Hot reload complete: {result.ScriptTypeCount} types, {reloadMode}", LogLevel.Info);
                }
                else
                {
                    Log("Hot reload failed.", LogLevel.Error);
                }
            };
        }

        // ================================================================================================
        //  公共 API：Play / Stop / Pause / Resume
        // ================================================================================================

        /// <summary>进入播放模式 (F5)。</summary>
        public void Play()
        {
            if (!IsInPlayMode)
            {
                // 进入前：同步编辑器场景到物理/脚本
                SyncEditorSceneToRuntime();
            }
            _playModeController.Play();
        }

        /// <summary>停止播放模式 (Shift+F5)。</summary>
        public void Stop()
        {
            _playModeController.Stop();
        }

        /// <summary>暂停播放。</summary>
        public void Pause()
        {
            _playModeController.Pause();
        }

        /// <summary>从暂停恢复。</summary>
        public void Resume()
        {
            _playModeController.Resume();
        }

        /// <summary>切换暂停/播放。</summary>
        public void TogglePause()
        {
            _playModeController.TogglePause();
        }

        /// <summary>单步推进一帧（仅暂停模式）。</summary>
        public void StepSingleFrame(float deltaTime = 1f / 60f)
        {
            _playModeController.StepSingleFrame(deltaTime);
        }

        /// <summary>请求热重载（编译脚本并替换，保留状态）。</summary>
        public void RequestHotReload()
        {
            _playModeController.RequestHotReload();
        }

        /// <summary>完成热重载（编译器调用此方法恢复状态）。</summary>
        public void CompleteHotReload(ScriptStateSnapshot? previousState = null)
        {
            _playModeController.CompleteHotReload(previousState);
        }

        // ================================================================================================
        //  每帧 Tick（由 MainWindow 调用）
        // ================================================================================================

        /// <summary>
        /// 编辑器主循环每帧调用。驱动 PIE 播放模式下的脚本和物理更新。
        /// </summary>
        /// <param name="deltaTime">帧间隔时间（秒）。</param>
        public void Tick(float deltaTime)
        {
            if (!_initialized) Initialize();

            // 委托给 PlayModeController（它会在 Playing 状态下驱动脚本和物理）
            _playModeController.Tick(deltaTime);

            // 同步物理桥接状态到 EngineSystems（用于渲染/调试）
            if (IsInPlayMode)
            {
                SyncPhysicsToEngineSystems();
            }
        }

        // ================================================================================================
        //  场景同步：编辑器 <-> 运行时
        // ================================================================================================

        /// <summary>
        /// 将编辑器场景（AssetDatabase.SceneObjects）同步到运行时系统（ECS + Physics + Scripts）。
        /// 进入 Play 模式前调用。
        /// </summary>
        private void SyncEditorSceneToRuntime()
        {
            Log("Syncing editor scene to runtime...", LogLevel.Info);

            // 1. 清理旧的运行时状态
            _scriptEngine.Registry.DestroyAllInstances();
            _physicsBridge.ClearMappings();
            _ecs.Clear(); // 清除所有实体

            // 2. 从 AssetDatabase 创建 ECS 实体
            var entityIdMap = new Dictionary<Guid, int>(); // Asset Guid -> ECS EntityId

            foreach (var sceneObj in _assetDatabase.SceneObjects)
            {
                if (!sceneObj.IsVisible) continue;

                int entityId = _ecs.CreateEntity(sceneObj.Name);
                entityIdMap[sceneObj.Id] = entityId;

                // Transform
                var transform = new TransformComponent
                {
                    Position = sceneObj.Position,
                    Rotation = sceneObj.Rotation,
                    Scale = sceneObj.Scale
                };
                _ecs.AddComponent(entityId, transform);

                // Mesh + Material
                if (!string.IsNullOrEmpty(sceneObj.MeshAssetPath))
                {
                    _ecs.AddComponent(entityId, new MeshComponent
                    {
                        MeshId = PathGuid(sceneObj.MeshAssetPath),
                        MaterialId = PathGuid(sceneObj.MaterialAssetPath),
                        CastsShadow = sceneObj.CastShadow,
                        ReceivesShadow = sceneObj.ReceiveShadow,
                        IsVisible = sceneObj.IsVisible
                    });
                }

                // Light
                if (sceneObj.LightComponent != null)
                {
                    _ecs.AddComponent(entityId, sceneObj.LightComponent.Value);
                }

                // Camera
                if (sceneObj.CameraComponent != null)
                {
                    _ecs.AddComponent(entityId, sceneObj.CameraComponent.Value);
                }

                // 注册物理体（如果有 Rigidbody）
                if (sceneObj.RigidbodyComponent != null)
                {
                    var rb = sceneObj.RigidbodyComponent.Value;
                    var shape = CreateCollisionShape(rb.ColliderType, rb.ColliderSize);
                    if (shape != null)
                    {
                        var bodyType = rb.IsKinematic ? BodyType.Kinematic : BodyType.Dynamic;
                        _physicsBridge.CreateBody(entityId, shape, bodyType, rb.Mass);
                    }
                }
            }

            Log($"Synced {entityIdMap.Count} entities to runtime.", LogLevel.Info);
        }

        /// <summary>将路径字符串映射为确定性的 Guid (供 Mesh/Material 引用)。</summary>
        private static Guid PathGuid(string path)
        {
            int h = path?.GetHashCode() ?? 0;
            return new Guid((uint)h, (ushort)(h >> 16), (ushort)(h >> 1),
                0, 0, 0, 0, 0, 0, 0, 0);
        }

        private Shape? CreateCollisionShape(ColliderType type, Vector3 size)
        {
            return type switch
            {
                ColliderType.Box => new BoxShape((PhysicsVector3)(size * 0.5f)),
                ColliderType.Sphere => new SphereShape(size.X * 0.5f),
                ColliderType.Capsule => new CapsuleShape(size.Y * 0.5f, size.X * 0.5f),
                _ => null
            };
        }

        /// <summary>
        /// 将物理状态同步回 EngineSystems（用于渲染/调试显示）。
        /// </summary>
        private void SyncPhysicsToEngineSystems()
        {
            // EngineSystems.Physics 是 PhysicsScene，这里可以选择同步关键数据
            // 暂时不做深度同步，PhysicsBridge 独立运行
        }

        // ================================================================================================
        //  热重载支持
        // ================================================================================================

        /// <summary>
        /// 从磁盘重新编译脚本并热重载（编辑器菜单调用）。
        /// </summary>
        public CompileResult? HotReloadScriptsFromDisk()
        {
            return _scriptEngine.HotReloadFromDisk();
        }

        /// <summary>
        /// 编译指定源文件（用于外部 IDE 保存后触发）。
        /// </summary>
        public CompileResult CompileScriptFiles(IEnumerable<string> filePaths, string assemblyName = "YamaScripts")
        {
            return _scriptEngine.CompileFiles(filePaths, assemblyName);
        }

        // ================================================================================================
        //  导出构建：创建 RuntimeGameLoop
        // ================================================================================================

        /// <summary>
        /// 创建运行时游戏循环（用于导出构建或无头测试）。
        /// </summary>
        public RuntimeGameLoop CreateRuntimeGameLoop(IRenderDevice renderDevice, RuntimeLoopOptions? options = null)
        {
            // 导出当前场景到 RuntimeWorld
            _runtimeWorld = ExportToRuntimeWorld();

            _runtimeGameLoop = new RuntimeGameLoop(_runtimeWorld, renderDevice, options);
            return _runtimeGameLoop;
        }

        private YamaEngine.Runtime.RuntimeWorld ExportToRuntimeWorld()
        {
            var world = new YamaEngine.Runtime.RuntimeWorld
            {
                SceneName = _assetDatabase.CurrentSceneName ?? "ExportedScene"
            };

            var objects = new List<YamaEngine.Assets.SceneObject>();
            foreach (var sceneObj in _assetDatabase.SceneObjects)
            {
                if (!sceneObj.IsVisible) continue;
                objects.Add(sceneObj);
            }
            world.Objects = objects;

            return world;
        }

        // ================================================================================================
        //  控制台命令注册
        // ================================================================================================

        private void RegisterConsoleCommands()
        {
            var cvars = _engineSystems.ConsoleVariables;

            cvars.Register("pie.play", "0", "Enter play mode (1=play, 0=stop)");
            cvars.Register("pie.pause", "0", "Pause play mode");
            cvars.Register("pie.step", "0", "Step single frame (write 1 to trigger)");
            cvars.Register("pie.hotreload", "0", "Trigger hot reload");
            cvars.Register("pie.logstate", "0", "Log current PIE state");

            // 监听 cvar 变化
            cvars.OnVariableChanged += (name, value) =>
            {
                switch (name)
                {
                    case "pie.play":
                        if (value == "1") Play();
                        else if (value == "0") Stop();
                        break;
                    case "pie.pause":
                        if (value == "1") Pause();
                        else if (value == "0") Resume();
                        break;
                    case "pie.step":
                        if (value == "1") StepSingleFrame();
                        cvars.TrySet("pie.step", "0"); // 自动复位
                        break;
                    case "pie.hotreload":
                        if (value == "1")
                        {
                            RequestHotReload();
                            cvars.TrySet("pie.hotreload", "0");
                        }
                        break;
                    case "pie.logstate":
                        if (value == "1")
                        {
                            Log($"PIE State: {State}, Time: {PlayTimeSeconds:F2}s, Frame: {PlayFrameIndex}", LogLevel.Info);
                            cvars.TrySet("pie.logstate", "0");
                        }
                        break;
                }
            };
        }

        // ================================================================================================
        //  日志
        // ================================================================================================

        private void Log(string message, LogLevel level)
        {
            OnPieLog?.Invoke(message, level);
            _engineSystems.Log(message);
        }

        // ================================================================================================
        //  IDisposable
        // ================================================================================================

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (IsInPlayMode) Stop();

            _playModeController.Dispose();
            _physicsBridge.Dispose();
            _scriptEngine.Dispose();
            _editorWorld.Dispose();
            _runtimeGameLoop = null;
            _runtimeWorld = null;
        }
    }

    // ================================================================================================
    //  运行时对象模型（用于导出构建）
    // ================================================================================================

    public sealed class RuntimeWorld
    {
        public string SceneName { get; set; } = "Scene";
        public List<RuntimeObject> Objects { get; } = new();
    }

    public sealed class RuntimeObject
    {
        public string Name { get; set; } = "GameObject";
        public Vector3 Position { get; set; }
        public Quaternion Rotation { get; set; }
        public Vector3 Scale { get; set; } = Vector3.One;
        public string? MeshPath { get; set; }
        public string? MaterialPath { get; set; }
        public bool IsVisible { get; set; } = true;
        public bool CastShadow { get; set; }
        public bool ReceiveShadow { get; set; }
    }

    // ================================================================================================
    //  ScriptComponentRef - 场景对象上的脚本引用
    // ================================================================================================

    public sealed class ScriptComponentRef
    {
        public string ScriptTypeName { get; set; } = string.Empty;
        public Dictionary<string, object?> FieldValues { get; set; } = new();
    }
}
