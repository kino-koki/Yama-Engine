// ==================================================================================================
//  EditorPlayModeController.cs — 山雨引擎编辑器 Play/Stop 控制器
// ==================================================================================================
//  命名空间: ShanyuEngine.Editor
//  依赖:     ShanyuEngine.Scripting, ShanyuEngine.ECS, ShanyuEngine.Runtime.Physics
//
//  功能:
//    1. PlayModeState 管理 (Editing → Playing → Paused)
//    2. 场景状态快照 / 恢复（进入 Play 时保存，Stop 时恢复）
//    3. 脚本生命周期驱动 (Awake → Start → Update → LateUpdate → FixedUpdate)
//    4. 物理步进集成 (PhysicsBridge.Step)
//    5. F5 热重载触发
//    6. 编辑器 UI 事件 (OnStateChanged / OnLog)
// ==================================================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ShanyuEngine.ECS;
using ShanyuEngine.Runtime.Physics;
using ShanyuEngine.Scripting;

using LogLevel = ShanyuEngine.Config.LogLevel;
namespace ShanyuEngine.Editor
{
    // ════════════════════════════════════════════════════════════════════════════
    //  PlayModeState
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>编辑器播放模式状态。</summary>
    public enum PlayModeState
    {
        /// <summary>编辑模式。用户可自由编辑场景和资产。</summary>
        Editing,

        /// <summary>播放模式。脚本在运行，物理在模拟。</summary>
        Playing,

        /// <summary>暂停模式。脚本和物理暂停，可从当前状态恢复。</summary>
        Paused,
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  EditorStateSnapshot — 场景状态快照
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 编辑器场景状态快照。在进入 Play 模式前捕获，Stop 时恢复。
    /// 包含 Transform、脚本字段值等可序列化状态。
    /// </summary>
    public sealed class EditorStateSnapshot
    {
        /// <summary>快照时间戳。</summary>
        public DateTime CaptureTime { get; init; } = DateTime.Now;

        /// <summary>实体 Transform 状态 [EntityId → (Position, Rotation, Scale)]。</summary>
        public Dictionary<int, TransformSnapshot> Transforms { get; init; } = new();

        /// <summary>脚本字段值 [EntityId → [ScriptTypeName → [FieldName → Value]]]。</summary>
        public Dictionary<int, Dictionary<string, Dictionary<string, object?>>> ScriptFields { get; init; } = new();

        /// <summary>已销毁的实体 ID 列表（Play 模式中新建但在 Stop 时要删除的）。</summary>
        public List<int> SpawnedEntities { get; init; } = new();
    }

    /// <summary>单个实体的 Transform 快照。</summary>
    public readonly struct TransformSnapshot
    {
        public readonly Vector3 Position;
        public readonly Quaternion Rotation;
        public readonly Vector3 Scale;

        public TransformSnapshot(Vector3 position, Quaternion rotation, Vector3 scale)
        {
            Position = position;
            Rotation = rotation;
            Scale = scale;
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  EditorPlayModeController
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 编辑器播放模式控制器。管理 Play/Stop/Pause 状态切换，驱动脚本生命周期和物理模拟。
    /// 这是让编辑器 "F5 Play 按钮" 真正工作的核心类。
    /// </summary>
    public sealed class EditorPlayModeController : IDisposable
    {
        // ── 子系统引用 ────────────────────────────────────────────────────────────

        private readonly EntityComponentSystem _ecs;
        private readonly ScriptRegistry _scriptRegistry;
        private readonly PhysicsBridge? _physicsBridge;

        // ── 状态 ──────────────────────────────────────────────────────────────────

        private PlayModeState _state = PlayModeState.Editing;
        private EditorStateSnapshot? _editModeSnapshot;
        private float _fixedAccumulator;
        private readonly List<int> _playModeSpawnedEntities = new();

        // ── 日志 ──────────────────────────────────────────────────────────────────

        /// <summary>控制器日志事件 (消息, 级别)。</summary>
        public event Action<string, LogLevel>? OnLog;

        /// <summary>状态变化事件 (旧状态, 新状态)。</summary>
        public event Action<PlayModeState, PlayModeState>? OnStateChanged;

        /// <summary>热重载完成事件。</summary>
        public event Action? OnHotReloadCompleted;

        // ── 属性 ──────────────────────────────────────────────────────────────────

        /// <summary>当前播放模式状态。</summary>
        public PlayModeState State => _state;

        /// <summary>是否在播放模式（包含暂停）。</summary>
        public bool IsInPlayMode => _state == PlayModeState.Playing || _state == PlayModeState.Paused;

        /// <summary>是否正在播放（未暂停）。</summary>
        public bool IsPlaying => _state == PlayModeState.Playing;

        /// <summary>是否暂停。</summary>
        public bool IsPaused => _state == PlayModeState.Paused;

        /// <summary>累计运行时间（秒）。仅在 Playing 状态累加。</summary>
        public float PlayTimeSeconds { get; private set; }

        /// <summary>当前帧号（从 Play 开始计数）。</summary>
        public int PlayFrameIndex { get; private set; }

        /// <summary>固定时间步长（用于 FixedUpdate）。</summary>
        public float FixedDeltaTime { get; set; } = 1f / 60f;

        /// <summary>每帧最大固定步数。</summary>
        public int MaxFixedStepsPerFrame { get; set; } = 4;

        // ─────────────────────────────────────────────────────────────────────────
        //  构造
        // ─────────────────────────────────────────────────────────────────────────

        public EditorPlayModeController(
            EntityComponentSystem ecs,
            ScriptRegistry scriptRegistry,
            PhysicsBridge? physicsBridge = null)
        {
            _ecs = ecs ?? throw new ArgumentNullException(nameof(ecs));
            _scriptRegistry = scriptRegistry ?? throw new ArgumentNullException(nameof(scriptRegistry));
            _physicsBridge = physicsBridge;

            // 订阅脚本日志事件
            ScriptAPI.Debug.OnLog += OnScriptDebugLog;
        }

        public void Dispose()
        {
            ScriptAPI.Debug.OnLog -= OnScriptDebugLog;
            if (IsInPlayMode)
                Stop();
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  Play / Stop / Pause
        // ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 进入 Play 模式。
        /// 1. 保存当前编辑器场景状态快照
        /// 2. 启动所有脚本的 Awake → Start 生命周期
        /// 3. 切换到 Playing 状态
        /// </summary>
        public void Play()
        {
            if (_state == PlayModeState.Playing)
            {
                Log("已经在播放模式", LogLevel.Warning);
                return;
            }

            var previousState = _state;

            if (_state == PlayModeState.Editing)
            {
                // 首次进入 Play：保存编辑器状态快照
                _editModeSnapshot = CaptureStateSnapshot();
                _playModeSpawnedEntities.Clear();
                PlayTimeSeconds = 0f;
                PlayFrameIndex = 0;
                _fixedAccumulator = 0f;

                Log("保存编辑器状态快照", LogLevel.Info);
            }

            // 如果是从 Pause 恢复，不需要重新启动脚本
            if (previousState == PlayModeState.Editing)
            {
                // 启动脚本生命周期
                StartScripts();
            }

            _state = PlayModeState.Playing;
            Log("进入播放模式", LogLevel.Info);
            OnStateChanged?.Invoke(previousState, _state);
        }

        /// <summary>
        /// 停止 Play 模式并恢复编辑器状态。
        /// 1. 调用所有脚本的 OnDestroy
        /// 2. 销毁 Play 模式中创建的实体
        /// 3. 恢复编辑器状态快照
        /// 4. 切换到 Editing 状态
        /// </summary>
        public void Stop()
        {
            if (_state == PlayModeState.Editing)
                return;

            var previousState = _state;

            // 销毁所有脚本实例（触发 OnDestroy）
            _scriptRegistry.DestroyAllInstances();

            // 销毁 Play 模式中创建的实体
            foreach (var entityId in _playModeSpawnedEntities)
            {
                if (_ecs.HasEntity(entityId))
                    _ecs.DestroyEntity(entityId);
            }
            _playModeSpawnedEntities.Clear();

            // 恢复编辑器状态
            if (_editModeSnapshot != null)
            {
                RestoreStateSnapshot(_editModeSnapshot);
                _editModeSnapshot = null;
            }

            // 重置物理（如果有）
            _physicsBridge?.ClearMappings();

            _state = PlayModeState.Editing;
            PlayTimeSeconds = 0f;
            PlayFrameIndex = 0;
            _fixedAccumulator = 0f;

            Log("停止播放，恢复编辑器状态", LogLevel.Info);
            OnStateChanged?.Invoke(previousState, _state);
        }

        /// <summary>暂停播放。</summary>
        public void Pause()
        {
            if (_state != PlayModeState.Playing) return;

            var previousState = _state;
            _state = PlayModeState.Paused;
            Log("暂停", LogLevel.Info);
            OnStateChanged?.Invoke(previousState, _state);
        }

        /// <summary>从暂停恢复。</summary>
        public void Resume()
        {
            if (_state != PlayModeState.Paused) return;

            var previousState = _state;
            _state = PlayModeState.Playing;
            Log("恢复播放", LogLevel.Info);
            OnStateChanged?.Invoke(previousState, _state);
        }

        /// <summary>切换 Play/Pause 状态。</summary>
        public void TogglePause()
        {
            if (_state == PlayModeState.Playing) Pause();
            else if (_state == PlayModeState.Paused) Resume();
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  每帧 Tick（由编辑器主循环调用）
        // ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 编辑器主循环每帧调用。仅在 Playing 状态下驱动脚本和物理更新。
        /// </summary>
        /// <param name="deltaTime">帧间隔时间（秒）。</param>
        public void Tick(float deltaTime)
        {
            if (_state != PlayModeState.Playing) return;

            float safeDt = MathF.Max(0f, deltaTime);
            PlayTimeSeconds += safeDt;
            PlayFrameIndex++;

            // ── FixedUpdate (固定步长) ──
            RunFixedUpdates(safeDt);

            // ── Update (每帧) ──
            // ScriptRegistry.Update 内部会自动处理 pending Start + Update + 协程
            _scriptRegistry.Update(safeDt);

            // ── LateUpdate ──
            _scriptRegistry.LateUpdate(safeDt);

            // ── 物理步进 ──
            _physicsBridge?.Step(safeDt);
        }

        private void RunFixedUpdates(float deltaTime)
        {
            _fixedAccumulator += deltaTime;
            int steps = 0;
            float fixedDt = FixedDeltaTime;

            while (_fixedAccumulator >= fixedDt && steps < MaxFixedStepsPerFrame)
            {
                _scriptRegistry.FixedUpdate(fixedDt);
                _fixedAccumulator -= fixedDt;
                steps++;
            }

            // 防止时间螺旋
            if (_fixedAccumulator > fixedDt * MaxFixedStepsPerFrame)
                _fixedAccumulator = fixedDt * MaxFixedStepsPerFrame;
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  脚本生命周期
        // ─────────────────────────────────────────────────────────────────────────

        private void StartScripts()
        {
            // ScriptRegistry.AddScript 已自动调用 Awake + OnEnable
            // Start 会在第一次 Update 中自动触发（通过 _pendingStart 队列）
            // 这里只需要触发一次 Update(0) 来启动所有待处理的 Start
            _scriptRegistry.Update(0f);
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  热重载 (F5)
        // ─────────────────═════════════════════════════════════════════════════════

        /// <summary>
        /// 请求热重载。重新编译所有脚本并替换实例，同时保留字段状态。
        /// 可在 Play 模式或编辑模式下调用。
        /// </summary>
        public void RequestHotReload()
        {
            Log("热重载请求...", LogLevel.Info);

            // 捕获当前脚本状态
            var snapshot = _scriptRegistry.CaptureStateSnapshot();

            // 重新加载脚本 Assembly（假设外部已更新 DLL）
            // 这里触发事件让上层（编辑器）处理实际的编译和加载
            OnHotReloadRequested?.Invoke(snapshot);
        }

        /// <summary>热重载请求事件。参数为当前状态快照，订阅者负责编译并调用 CompleteHotReload。</summary>
        public event Action<ScriptStateSnapshot>? OnHotReloadRequested;

        /// <summary>
        /// 完成热重载。由编辑器在编译完成后调用，恢复脚本状态。
        /// </summary>
        public void CompleteHotReload(ScriptStateSnapshot? previousState = null)
        {
            if (previousState != null)
            {
                _scriptRegistry.RestoreStateSnapshot(previousState);
            }

            Log("热重载完成", LogLevel.Info);
            OnHotReloadCompleted?.Invoke();
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  场景快照 / 恢复
        // ─────────────────────────────────────────────────────────────────────────

        /// <summary>捕获当前场景状态快照。</summary>
        public EditorStateSnapshot CaptureStateSnapshot()
        {
            var transforms = new Dictionary<int, TransformSnapshot>();
            var scriptFields = new Dictionary<int, Dictionary<string, Dictionary<string, object?>>>();

            foreach (var entityId in _ecs.AllEntities)
            {
                // Transform
                if (_ecs.HasComponent<TransformComponent>(entityId))
                {
                    var t = _ecs.GetComponent<TransformComponent>(entityId);
                    transforms[entityId] = new TransformSnapshot(t.Position, t.Rotation, t.Scale);
                }

                // Script fields — 通过反射遍历所有公共实例字段
                var scripts = _scriptRegistry.GetScripts(entityId);
                if (scripts.Count > 0)
                {
                    var entityFields = new Dictionary<string, Dictionary<string, object?>>();
                    foreach (var script in scripts)
                    {
                        var fields = CaptureScriptFields(script);
                        if (fields.Count > 0)
                            entityFields[script.TypeName] = fields;
                    }
                    if (entityFields.Count > 0)
                        scriptFields[entityId] = entityFields;
                }
            }

            return new EditorStateSnapshot
            {
                Transforms = transforms,
                ScriptFields = scriptFields,
            };
        }

        private static Dictionary<string, object?> CaptureScriptFields(ScriptComponent script)
        {
            var result = new Dictionary<string, object?>();
            var fields = script.GetType().GetFields(
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.Instance);
            foreach (var field in fields)
            {
                if (field.IsInitOnly) continue;
                try { result[field.Name] = field.GetValue(script); }
                catch { }
            }
            return result;
        }

        /// <summary>从快照恢复场景状态。</summary>
        public void RestoreStateSnapshot(EditorStateSnapshot snapshot)
        {
            // 恢复 Transform
            foreach (var kv in snapshot.Transforms)
            {
                int entityId = kv.Key;
                if (!_ecs.HasEntity(entityId)) continue;
                if (!_ecs.HasComponent<TransformComponent>(entityId)) continue;

                var t = _ecs.GetComponent<TransformComponent>(entityId);
                t.Position = kv.Value.Position;
                t.Rotation = kv.Value.Rotation;
                t.Scale = kv.Value.Scale;
                _ecs.SetComponent(entityId, t);
            }

            // 恢复脚本字段
            foreach (var entityKv in snapshot.ScriptFields)
            {
                int entityId = entityKv.Key;
                if (!_ecs.HasEntity(entityId)) continue;

                foreach (var scriptKv in entityKv.Value)
                {
                    string typeName = scriptKv.Key;
                    var script = _scriptRegistry.GetScript(entityId, typeName);
                    if (script == null) continue;

                    foreach (var fieldKv in scriptKv.Value)
                    {
                        script.SetFieldValue(fieldKv.Key, fieldKv.Value);
                    }
                }
            }
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  PlayMode 实体管理
        // ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 在 Play 模式中创建实体。Stop 时会自动销毁这些实体。
        /// </summary>
        public int SpawnEntity(string name, Vector3 position, Quaternion? rotation = null, Vector3? scale = null)
        {
            int entityId = _ecs.CreateEntity(name);
            _ecs.AddComponent(entityId, new TransformComponent
            {
                Position = position,
                Rotation = rotation ?? Quaternion.Identity,
                Scale = scale ?? Vector3.One,
            });

            if (IsInPlayMode)
                _playModeSpawnedEntities.Add(entityId);

            return entityId;
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  日志
        // ─────────────────────────────────────────────────────────────────────────

        private void OnScriptDebugLog(string message, ScriptAPI.Debug.LogLevel level)
        {
            var logLevel = level switch
            {
                ScriptAPI.Debug.LogLevel.Warning => LogLevel.Warning,
                ScriptAPI.Debug.LogLevel.Error => LogLevel.Error,
                _ => LogLevel.Info,
            };
            OnLog?.Invoke(message, logLevel);
        }

        private void Log(string message, LogLevel level)
        {
            OnLog?.Invoke($"[PlayMode] {message}", level);
        }

        // ─────────────────────────────────────────────────────────────────────────
        //  单步调试（用于 Pause 状态逐帧推进）
        // ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// 单步推进一帧。仅在 Paused 状态下有效。
        /// </summary>
        /// <param name="deltaTime">步进时间（默认 1/60 秒）。</param>
        public void StepSingleFrame(float deltaTime = 1f / 60f)
        {
            if (_state != PlayModeState.Paused)
            {
                Log("单步调试仅在暂停模式下可用", LogLevel.Warning);
                return;
            }

            PlayTimeSeconds += deltaTime;
            PlayFrameIndex++;

            _scriptRegistry.FixedUpdate(FixedDeltaTime);
            _scriptRegistry.Update(deltaTime);
            _scriptRegistry.LateUpdate(deltaTime);
            _physicsBridge?.StepImmediate(deltaTime);

            Log($"单步推进 (frame={PlayFrameIndex}, dt={deltaTime:F4})", LogLevel.Info);
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  LogLevel
    // ════════════════════════════════════════════════════════════════════════════

}
