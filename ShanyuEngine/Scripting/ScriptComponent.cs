using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ShanyuEngine.Core;
using ShanyuEngine.ECS;

namespace ShanyuEngine.Scripting
{
    // ─────────────────────────────────────────────────────────────────────────────
    //  Attributes
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Marks a private or protected field for serialization by the engine inspector
    /// and for state preservation during hot-reload.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field, AllowMultiple = false, Inherited = true)]
    public sealed class SerializeFieldAttribute : Attribute
    {
        /// <summary>Optional tooltip shown in the inspector.</summary>
        public string? Tooltip { get; set; }

        /// <summary>Optional inclusive lower bound for numeric fields.</summary>
        public float RangeMin { get; set; } = float.NaN;

        /// <summary>Optional inclusive upper bound for numeric fields.</summary>
        public float RangeMax { get; set; } = float.NaN;

        public SerializeFieldAttribute() { }

        public SerializeFieldAttribute(string tooltip)
        {
            Tooltip = tooltip;
        }
    }

    /// <summary>
    /// Hides a public field from the inspector so it is not serialized or displayed.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field, AllowMultiple = false, Inherited = true)]
    public sealed class HideInInspectorAttribute : Attribute { }

    /// <summary>
    /// Requires the entity to have the specified component type(s) before this script
    /// can be attached.  Checked at registration time by the <see cref="ScriptRegistry"/>.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = true)]
    public sealed class RequireComponentAttribute : Attribute
    {
        public Type RequiredType { get; }

        public RequireComponentAttribute(Type type)
        {
            RequiredType = type ?? throw new ArgumentNullException(nameof(type));
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  Yield instructions for coroutines
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Suspends coroutine execution for the given number of (scaled) seconds.
    /// </summary>
    public sealed class WaitForSeconds
    {
        public float Seconds { get; }

        public WaitForSeconds(float seconds)
        {
            Seconds = Math.Max(0f, seconds);
        }
    }

    /// <summary>
    /// Suspends coroutine execution until the end of the current frame
    /// (after all LateUpdate calls and rendering).
    /// </summary>
    public sealed class WaitForEndOfFrame { }

    /// <summary>
    /// Suspends coroutine execution until the next FixedUpdate phase.
    /// </summary>
    public sealed class WaitForFixedUpdate { }

    /// <summary>
    /// Base class for custom yield instructions.  Override <see cref="KeepWaiting"/>
    /// to control when the coroutine should resume.
    /// </summary>
    public abstract class CustomYieldInstruction
    {
        /// <summary>
        /// Returns <c>true</c> while the coroutine should remain suspended,
        /// <c>false</c> when it should resume.
        /// </summary>
        public abstract bool KeepWaiting { get; }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  Coroutine
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Internal phase used to dispatch coroutine ticks to the correct update stage.
    /// </summary>
    internal enum CoroutinePhase
    {
        Update,
        FixedUpdate,
        EndOfFrame
    }

    /// <summary>
    /// Represents a running coroutine started via <see cref="ScriptComponent.StartCoroutine"/>.
    /// Can be passed to <see cref="ScriptComponent.StopCoroutine(Coroutine)"/> to stop it early.
    /// </summary>
    public sealed class Coroutine
    {
        private readonly Stack<IEnumerator> _stack;
        private readonly IEnumerator _root;

        private float _waitTimer;
        private bool _waitEndOfFrame;
        private bool _waitFixedUpdate;
        private bool _waitNextFrame;
        private CustomYieldInstruction? _customYield;

        /// <summary>Whether the coroutine has completed.</summary>
        public bool IsDone { get; private set; }

        /// <summary>Whether the coroutine is still running.</summary>
        public bool IsRunning => !IsDone;

        internal Coroutine(IEnumerator routine)
        {
            _root = routine ?? throw new ArgumentNullException(nameof(routine));
            _stack = new Stack<IEnumerator>();
            _stack.Push(routine);
            Advance();
        }

        /// <summary>
        /// Returns <c>true</c> if this coroutine was started from the given root enumerator.
        /// </summary>
        internal bool Matches(IEnumerator routine)
        {
            return ReferenceEquals(_root, routine);
        }

        /// <summary>Marks the coroutine as finished immediately.</summary>
        internal void Stop()
        {
            IsDone = true;
        }

        private void Advance()
        {
            while (_stack.Count > 0)
            {
                var top = _stack.Peek();
                if (top.MoveNext())
                {
                    var current = top.Current;
                    ProcessYield(current);
                    return;
                }
                _stack.Pop();
            }
            IsDone = true;
        }

        private void ProcessYield(object? current)
        {
            _waitTimer = 0f;
            _waitEndOfFrame = false;
            _waitFixedUpdate = false;
            _waitNextFrame = false;
            _customYield = null;

            switch (current)
            {
                case WaitForSeconds wfs:
                    _waitTimer = wfs.Seconds;
                    break;
                case WaitForEndOfFrame:
                    _waitEndOfFrame = true;
                    break;
                case WaitForFixedUpdate:
                    _waitFixedUpdate = true;
                    break;
                case CustomYieldInstruction cyi:
                    _customYield = cyi;
                    break;
                case Coroutine coro:
                    _customYield = new CoroutineYield(coro);
                    break;
                case IEnumerator nested:
                    _stack.Push(nested);
                    Advance();
                    return;
                default:
                    // null or unrecognised → wait one frame
                    _waitNextFrame = true;
                    break;
            }
        }

        internal void Tick(float dt, CoroutinePhase phase)
        {
            if (IsDone) return;

            // Handle time-based wait
            if (_waitTimer > 0f)
            {
                if (phase != CoroutinePhase.Update) return;
                _waitTimer -= dt;
                if (_waitTimer > 0f) return;
                _waitTimer = 0f;
            }

            if (_waitEndOfFrame && phase != CoroutinePhase.EndOfFrame) return;
            if (_waitFixedUpdate && phase != CoroutinePhase.FixedUpdate) return;
            if (_waitNextFrame && phase != CoroutinePhase.Update) return;

            if (_customYield != null)
            {
                if (phase != CoroutinePhase.Update) return;
                if (_customYield.KeepWaiting) return;
                _customYield = null;
            }

            Advance();
        }

        /// <summary>
        /// Custom yield instruction that waits for another <see cref="Coroutine"/> to finish.
        /// </summary>
        private sealed class CoroutineYield : CustomYieldInstruction
        {
            private readonly Coroutine _coroutine;
            public CoroutineYield(Coroutine coroutine) => _coroutine = coroutine;
            public override bool KeepWaiting => _coroutine.IsRunning;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  ScriptComponent — the MonoBehaviour equivalent
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Base class for all user-authored gameplay scripts.  Derive from this class,
    /// override the lifecycle methods you need, and attach instances to entities via
    /// <see cref="ScriptRegistry.AddScript"/>.
    /// </summary>
    public abstract class ScriptComponent
    {
        // ── Context (set by the registry before any lifecycle call) ──────────────

        /// <summary>The ECS entity identifier this script is attached to.</summary>
        public int EntityId { get; private set; }

        /// <summary>The entity-component system the entity lives in.</summary>
        public EntityComponentSystem ECS { get; private set; } = null!;

        /// <summary>The engine systems (time, input, physics, audio, …).</summary>
        public EngineSystems Engine { get; private set; } = null!;

        /// <summary>The script registry that owns this instance.</summary>
        internal ScriptRegistry Registry { get; set; } = null!;

        // ── Runtime state ─────────────────────────────────────────────────────────

        private bool _enabled = true;

        internal bool HasAwoken { get; set; }
        internal bool HasStarted { get; set; }
        internal int ErrorCount { get; set; }
        internal bool IsDestroyed { get; set; }

        internal const int MaxErrors = 10;
        internal bool HasError => ErrorCount >= MaxErrors;

        /// <summary>
        /// Full type name of this script (set by the registry at creation time).
        /// </summary>
        public string TypeName { get; internal set; } = string.Empty;

        // ── Enabled property with lifecycle callbacks ────────────────────────────

        /// <summary>
        /// Enables or disables this script instance.  Setting to <c>false</c> calls
        /// <see cref="OnDisable"/>; setting to <c>true</c> calls <see cref="OnEnable"/>.
        /// Changes are ignored until <see cref="Awake"/> has been called.
        /// </summary>
        public bool Enabled
        {
            get => _enabled;
            set
            {
                if (_enabled == value) return;
                _enabled = value;
                if (!HasAwoken) return;
                if (value) OnEnable();
                else OnDisable();
            }
        }

        // ── Transform convenience accessors ───────────────────────────────────────

        /// <summary>Returns a copy of the entity's transform component.</summary>
        public TransformComponent GetTransform()
        {
            return ECS.GetComponent<TransformComponent>(EntityId);
        }

        /// <summary>Writes a transform component back to the entity.</summary>
        public void SetTransform(TransformComponent value)
        {
            ECS.SetComponent(EntityId, value);
        }

        /// <summary>Position in world space.</summary>
        public Vector3 Position
        {
            get => ECS.GetComponent<TransformComponent>(EntityId).Position;
            set
            {
                var t = ECS.GetComponent<TransformComponent>(EntityId);
                t.Position = value;
                ECS.SetComponent(EntityId, t);
            }
        }

        /// <summary>Rotation as a quaternion.</summary>
        public Quaternion Rotation
        {
            get => ECS.GetComponent<TransformComponent>(EntityId).Rotation;
            set
            {
                var t = ECS.GetComponent<TransformComponent>(EntityId);
                t.Rotation = value;
                ECS.SetComponent(EntityId, t);
            }
        }

        /// <summary>Non-uniform scale.</summary>
        public Vector3 Scale
        {
            get => ECS.GetComponent<TransformComponent>(EntityId).Scale;
            set
            {
                var t = ECS.GetComponent<TransformComponent>(EntityId);
                t.Scale = value;
                ECS.SetComponent(EntityId, t);
            }
        }

        /// <summary>Local forward direction.</summary>
        public Vector3 Forward => ECS.GetComponent<TransformComponent>(EntityId).Forward;

        /// <summary>Local right direction.</summary>
        public Vector3 Right => ECS.GetComponent<TransformComponent>(EntityId).Right;

        /// <summary>Local up direction.</summary>
        public Vector3 Up => ECS.GetComponent<TransformComponent>(EntityId).Up;

        /// <summary>
        /// Convenience: applies a translation to the entity's position.
        /// </summary>
        public void Translate(Vector3 delta)
        {
            var t = ECS.GetComponent<TransformComponent>(EntityId);
            t.Position += delta;
            ECS.SetComponent(EntityId, t);
        }

        /// <summary>
        /// Convenience: applies a rotation (multiplicative) to the entity's rotation.
        /// </summary>
        public void Rotate(Quaternion deltaRotation)
        {
            var t = ECS.GetComponent<TransformComponent>(EntityId);
            t.Rotation = deltaRotation * t.Rotation;
            ECS.SetComponent(EntityId, t);
        }

        /// <summary>
        /// Convenience: applies a yaw-pitch-roll rotation in degrees.
        /// </summary>
        public void Rotate(float yawDegrees, float pitchDegrees, float rollDegrees)
        {
            Rotate(Quaternion.CreateFromYawPitchRoll(
                yawDegrees * (MathF.PI / 180f),
                pitchDegrees * (MathF.PI / 180f),
                rollDegrees * (MathF.PI / 180f)));
        }

        // ── ECS component accessors ───────────────────────────────────────────────

        /// <summary>Gets a struct component from the entity.</summary>
        public T GetComponent<T>() where T : struct, IComponent
        {
            return ECS.GetComponent<T>(EntityId);
        }

        /// <summary>Returns <c>true</c> if the entity has the given component.</summary>
        public bool HasComponent<T>() where T : struct, IComponent
        {
            return ECS.HasComponent<T>(EntityId);
        }

        /// <summary>Adds or replaces a struct component on the entity.</summary>
        public T AddComponent<T>(T component) where T : struct, IComponent
        {
            return ECS.AddComponent(EntityId, component);
        }

        /// <summary>Removes a struct component from the entity.</summary>
        public bool RemoveComponent<T>() where T : struct, IComponent
        {
            return ECS.RemoveComponent<T>(EntityId);
        }

        /// <summary>
        /// Non-generic component existence check using reflection.
        /// Used internally by <see cref="RequireComponentAttribute"/>.
        /// </summary>
        internal bool HasComponent(Type componentType)
        {
            var method = typeof(EntityComponentSystem)
                .GetMethods()
                .First(m => m.Name == nameof(EntityComponentSystem.HasComponent) && m.IsGenericMethod)
                .MakeGenericMethod(componentType);
            return (bool)method.Invoke(ECS, new object[] { EntityId })!;
        }

        // ── Inter-script accessors ─────────────────────────────────────────────────

        /// <summary>Gets the first script of type <typeparamref name="T"/> on this entity.</summary>
        public T? GetScript<T>() where T : ScriptComponent
        {
            return Registry.GetScript<T>(EntityId);
        }

        /// <summary>Gets a script by its full type name on this entity.</summary>
        public ScriptComponent? GetScript(string typeName)
        {
            return Registry.GetScript(EntityId, typeName);
        }

        /// <summary>Returns all scripts attached to this entity (including this one).</summary>
        public IReadOnlyList<ScriptComponent> GetScripts()
        {
            return Registry.GetScripts(EntityId);
        }

        /// <summary>Looks up a script on another entity.</summary>
        public T? GetScriptOnEntity<T>(int entityId) where T : ScriptComponent
        {
            return Registry.GetScript<T>(entityId);
        }

        // ── Entity helpers ─────────────────────────────────────────────────────────

        /// <summary>The human-readable name of the attached entity.</summary>
        public string EntityName => ECS.GetEntityName(EntityId);

        /// <summary>Whether the attached entity still exists.</summary>
        public bool EntityExists => ECS.HasEntity(EntityId);

        // ── Lifecycle methods (override these) ─────────────────────────────────────

        /// <summary>
        /// Called once when the script instance is first created, before
        /// <see cref="OnEnable"/> and <see cref="Start"/>.
        /// </summary>
        public virtual void Awake() { }

        /// <summary>Called when the script becomes enabled (or after Awake if enabled).</summary>
        public virtual void OnEnable() { }

        /// <summary>Called when the script becomes disabled.</summary>
        public virtual void OnDisable() { }

        /// <summary>
        /// Called once before the first <see cref="Update"/> call, after all
        /// <see cref="Awake"/> calls on the entity have completed.
        /// </summary>
        public virtual void Start() { }

        /// <summary>Called every frame while the script is enabled and started.</summary>
        public virtual void Update(float deltaTime) { }

        /// <summary>Called at a fixed interval for physics calculations.</summary>
        public virtual void FixedUpdate(float fixedDeltaTime) { }

        /// <summary>Called after all <see cref="Update"/> calls have finished.</summary>
        public virtual void LateUpdate(float deltaTime) { }

        /// <summary>Called when the script is destroyed or the entity is removed.</summary>
        public virtual void OnDestroy() { }

        // ── Coroutines ─────────────────────────────────────────────────────────────

        private readonly List<Coroutine> _coroutines = new();

        /// <summary>
        /// Starts a coroutine from the given enumerator and returns a handle
        /// that can be passed to <see cref="StopCoroutine(Coroutine)"/>.
        /// </summary>
        public Coroutine StartCoroutine(IEnumerator routine)
        {
            if (routine == null) throw new ArgumentNullException(nameof(routine));
            var coroutine = new Coroutine(routine);
            _coroutines.Add(coroutine);
            return coroutine;
        }

        /// <summary>Stops a previously started coroutine.</summary>
        public void StopCoroutine(Coroutine coroutine)
        {
            if (coroutine == null) return;
            coroutine.Stop();
            _coroutines.Remove(coroutine);
        }

        /// <summary>Stops the coroutine that was started with the given enumerator.</summary>
        public void StopCoroutine(IEnumerator routine)
        {
            if (routine == null) return;
            for (int i = _coroutines.Count - 1; i >= 0; i--)
            {
                if (_coroutines[i].Matches(routine))
                {
                    _coroutines[i].Stop();
                    _coroutines.RemoveAt(i);
                }
            }
        }

        /// <summary>Stops all coroutines started by this script.</summary>
        public void StopAllCoroutines()
        {
            foreach (var c in _coroutines)
                c.Stop();
            _coroutines.Clear();
        }

        /// <summary>Number of active coroutines on this script.</summary>
        public int CoroutineCount => _coroutines.Count;

        /// <summary>Ticks all active coroutines for this script.</summary>
        internal void TickCoroutines(float dt, CoroutinePhase phase)
        {
            if (_coroutines.Count == 0) return;
            for (int i = _coroutines.Count - 1; i >= 0; i--)
            {
                _coroutines[i].Tick(dt, phase);
                if (_coroutines[i].IsDone)
                    _coroutines.RemoveAt(i);
            }
        }

        // ── Destruction ────────────────────────────────────────────────────────────

        /// <summary>
        /// Requests destruction of this script instance.  The actual destruction
        /// happens at the end of the current update cycle if called during an update,
        /// or immediately otherwise.
        /// </summary>
        public void Destroy()
        {
            Registry?.Destroy(this);
        }

        // ── Logging ────────────────────────────────────────────────────────────────

        /// <summary>Logs an informational message prefixed with the script type name.</summary>
        public void Log(string message)
        {
            Engine.Log($"[{TypeName}] {message}");
        }

        /// <summary>Logs a warning message prefixed with the script type name.</summary>
        public void LogWarning(string message)
        {
            Engine.LogWarning($"[{TypeName}] {message}");
        }

        /// <summary>Logs an error message prefixed with the script type name.</summary>
        public void LogError(string message)
        {
            Engine.LogError($"[{TypeName}] {message}");
        }

        // ── Internal initialisation ───────────────────────────────────────────────

        /// <summary>
        /// Sets the runtime context.  Called by <see cref="ScriptRegistry"/> before
        /// any lifecycle method.
        /// </summary>
        internal void Initialize(int entityId, EntityComponentSystem ecs,
            EngineSystems engine, ScriptRegistry registry)
        {
            EntityId = entityId;
            ECS = ecs;
            Engine = engine;
            Registry = registry;
            _enabled = true;
            HasAwoken = false;
            HasStarted = false;
            ErrorCount = 0;
            IsDestroyed = false;
            _coroutines.Clear();
        }

        /// <summary>
        /// Returns the value of a field marked with <see cref="SerializeFieldAttribute"/>
        /// or any public instance field, or <c>null</c> if not found.
        /// </summary>
        internal object? GetFieldValue(string fieldName)
        {
            var field = GetType().GetField(fieldName,
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic);
            return field?.GetValue(this);
        }

        /// <summary>
        /// Sets the value of a field by name, attempting a best-effort conversion
        /// if the stored value type differs from the field type.
        /// </summary>
        internal void SetFieldValue(string fieldName, object? value)
        {
            var field = GetType().GetField(fieldName,
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic);
            if (field == null) return;

            if (value == null)
            {
                field.SetValue(this, value);
                return;
            }

            var fieldType = field.FieldType;
            var valueType = value.GetType();

            if (fieldType.IsAssignableFrom(valueType))
            {
                field.SetValue(this, value);
                return;
            }

            // Best-effort conversion for primitive types
            try
            {
                var converted = Convert.ChangeType(value, fieldType);
                field.SetValue(this, converted);
            }
            catch
            {
                // Type mismatch after hot-reload — leave default value
            }
        }
    }
}
