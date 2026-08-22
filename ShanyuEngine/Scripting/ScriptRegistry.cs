using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using ShanyuEngine.Core;
using ShanyuEngine.ECS;

namespace ShanyuEngine.Scripting
{
    // ─────────────────────────────────────────────────────────────────────────────
    //  State snapshot types (for hot-reload)
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Snapshot of a single script instance's serializable state, used to preserve
    /// data across hot-reload cycles.
    /// </summary>
    public sealed class ScriptInstanceSnapshot
    {
        /// <summary>The entity the script was attached to.</summary>
        public int EntityId { get; init; }

        /// <summary>The full type name of the script.</summary>
        public string TypeName { get; init; } = string.Empty;

        /// <summary>Whether the script was enabled at snapshot time.</summary>
        public bool Enabled { get; init; }

        /// <summary>Captured field values keyed by field name.</summary>
        public Dictionary<string, object?> FieldValues { get; init; } = new();
    }

    /// <summary>
    /// Snapshot of all script instances, captured before an assembly hot-reload
    /// and restored after the new assembly is loaded.
    /// </summary>
    public sealed class ScriptStateSnapshot
    {
        /// <summary>All instance snapshots.</summary>
        public IReadOnlyList<ScriptInstanceSnapshot> Instances { get; init; } = Array.Empty<ScriptInstanceSnapshot>();

        /// <summary>Number of captured instances.</summary>
        public int Count => Instances.Count;
    }

    // ─────────────────────────────────────────────────────────────────────────────
    //  ScriptRegistry
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Registry of all discovered script types and live script instances.  Manages
    /// instance creation, lifecycle dispatch (Awake → Start → Update → … → OnDestroy),
    /// coroutine ticking, and state capture/restore for hot-reload.
    /// </summary>
    public sealed class ScriptRegistry : IDisposable
    {
        // ── Dependencies ───────────────────────────────────────────────────────────

        private readonly EntityComponentSystem _ecs;
        private readonly EngineSystems _engine;

        // ── Type registry ──────────────────────────────────────────────────────────

        private readonly Dictionary<string, Type> _typesByName = new(StringComparer.Ordinal);
        private readonly Dictionary<Type, string> _namesByType = new();

        // ── Instance storage ────────────────────────────────────────────────────────

        private readonly List<ScriptComponent> _allInstances = new();
        private readonly Dictionary<int, List<ScriptComponent>> _entityInstances = new();
        private readonly List<ScriptComponent> _pendingStart = new();
        private readonly HashSet<ScriptComponent> _toDestroy = new();

        // ── Update bookkeeping ──────────────────────────────────────────────────────

        private bool _isUpdating;
        private bool _disposed;

        // ── Properties ─────────────────────────────────────────────────────────────

        /// <summary>Number of registered script types.</summary>
        public int RegisteredTypeCount => _typesByName.Count;

        /// <summary>Number of live script instances.</summary>
        public int InstanceCount => _allInstances.Count;

        /// <summary>Number of entities that have at least one script.</summary>
        public int EntityWithScriptsCount => _entityInstances.Count;

        // ── Constructor ─────────────────────────────────────────────────────────────

        public ScriptRegistry(EntityComponentSystem ecs, EngineSystems engine)
        {
            _ecs = ecs ?? throw new ArgumentNullException(nameof(ecs));
            _engine = engine ?? throw new ArgumentNullException(nameof(engine));

            // Destroy all scripts when an entity is destroyed.
            _ecs.EntityDestroyed += OnEntityDestroyed;
        }

        // ── Type registration ──────────────────────────────────────────────────────

        /// <summary>
        /// Registers a single script type.  The type must be a concrete, non-abstract
        /// subclass of <see cref="ScriptComponent"/> with a parameterless constructor.
        /// </summary>
        /// <returns><c>true</c> if the type was registered; <c>false</c> if it was rejected.</returns>
        public bool RegisterType(Type type)
        {
            if (type == null) throw new ArgumentNullException(nameof(type));

            if (type.IsAbstract || type.IsInterface)
            {
                _engine.LogWarning($"Cannot register abstract or interface type '{type.FullName}'.");
                return false;
            }

            if (!typeof(ScriptComponent).IsAssignableFrom(type))
            {
                _engine.LogWarning($"Type '{type.FullName}' does not derive from ScriptComponent.");
                return false;
            }

            if (type.GetConstructor(Type.EmptyTypes) == null)
            {
                _engine.LogWarning($"Type '{type.FullName}' has no parameterless constructor.");
                return false;
            }

            var name = type.FullName ?? type.Name;
            _typesByName[name] = type;
            _namesByType[type] = name;
            return true;
        }

        /// <summary>
        /// Registers all script types from a loaded assembly info.
        /// </summary>
        public int RegisterAssembly(LoadedAssemblyInfo info)
        {
            if (info == null) throw new ArgumentNullException(nameof(info));
            int registered = 0;
            foreach (var type in info.ScriptTypes)
            {
                if (RegisterType(type))
                    registered++;
            }
            _engine.Log($"Registered {registered} script type(s) from '{info.Name}'.");
            return registered;
        }

        /// <summary>
        /// Unregisters a script type by name.  Existing instances are not affected
        /// (they continue to run until explicitly destroyed).
        /// </summary>
        public bool UnregisterType(string typeName)
        {
            if (string.IsNullOrEmpty(typeName)) return false;
            if (_typesByName.TryGetValue(typeName, out var type))
            {
                _typesByName.Remove(typeName);
                _namesByType.Remove(type);
                return true;
            }
            return false;
        }

        /// <summary>
        /// Clears all registered types.  Existing instances are not affected.
        /// </summary>
        public void ClearTypes()
        {
            _typesByName.Clear();
            _namesByType.Clear();
        }

        /// <summary>Checks whether a script type is registered.</summary>
        public bool IsTypeRegistered(string typeName)
        {
            return !string.IsNullOrEmpty(typeName) && _typesByName.ContainsKey(typeName);
        }

        /// <summary>Looks up a registered script type by name.</summary>
        public Type? GetRegisteredType(string typeName)
        {
            if (string.IsNullOrEmpty(typeName)) return null;
            return _typesByName.TryGetValue(typeName, out var type) ? type : null;
        }

        /// <summary>Returns all registered script types.</summary>
        public IReadOnlyList<Type> GetRegisteredTypes()
        {
            return _typesByName.Values.ToList();
        }

        /// <summary>Returns the registered names of all script types.</summary>
        public IReadOnlyList<string> GetRegisteredTypeNames()
        {
            return _typesByName.Keys.ToList();
        }

        // ── Instance creation ──────────────────────────────────────────────────────

        /// <summary>
        /// Creates a new script instance of the given type name and attaches it to
        /// the specified entity.  Calls <see cref="ScriptComponent.Awake"/> and
        /// <see cref="ScriptComponent.OnEnable"/> immediately.  <see cref="ScriptComponent.Start"/>
        /// is deferred to the next update cycle.
        /// </summary>
        /// <param name="entityId">The target entity. Must already exist.</param>
        /// <param name="typeName">Full or short name of a registered script type.</param>
        /// <returns>The created instance, or <c>null</c> if creation failed.</returns>
        public ScriptComponent? AddScript(int entityId, string typeName)
        {
            if (string.IsNullOrEmpty(typeName)) return null;
            if (!_ecs.HasEntity(entityId))
            {
                _engine.LogError($"Cannot add script '{typeName}': entity {entityId} does not exist.");
                return null;
            }

            var type = GetRegisteredType(typeName);
            if (type == null)
            {
                _engine.LogError($"Script type '{typeName}' is not registered.");
                return null;
            }

            // Check RequireComponent attributes
            if (!CheckRequiredComponents(type, entityId))
            {
                _engine.LogError(
                    $"Cannot attach '{typeName}' to entity {entityId}: required component(s) missing.");
                return null;
            }

            return CreateAndAttach(entityId, type);
        }

        /// <summary>
        /// Creates a new script instance of the given type and attaches it to the
        /// specified entity.
        /// </summary>
        public T? AddScript<T>(int entityId) where T : ScriptComponent
        {
            return (T?)AddScript(entityId, typeof(T));
        }

        /// <summary>
        /// Creates a new script instance of the given <see cref="Type"/> and attaches it.
        /// </summary>
        public ScriptComponent? AddScript(int entityId, Type type)
        {
            if (type == null) throw new ArgumentNullException(nameof(type));
            if (!_ecs.HasEntity(entityId))
            {
                _engine.LogError($"Cannot add script '{type.Name}': entity {entityId} does not exist.");
                return null;
            }

            if (type.IsAbstract || !typeof(ScriptComponent).IsAssignableFrom(type))
            {
                _engine.LogError($"Type '{type.FullName}' is not a valid script type.");
                return null;
            }

            if (!CheckRequiredComponents(type, entityId))
            {
                _engine.LogError(
                    $"Cannot attach '{type.Name}' to entity {entityId}: required component(s) missing.");
                return null;
            }

            // Auto-register if not yet registered
            if (!_namesByType.ContainsKey(type))
                RegisterType(type);

            return CreateAndAttach(entityId, type);
        }

        /// <summary>
        /// Core instance creation logic: instantiates, initialises context, calls
        /// Awake + OnEnable, and registers the instance.
        /// </summary>
        private ScriptComponent? CreateAndAttach(int entityId, Type type)
        {
            ScriptComponent instance;
            try
            {
                instance = (ScriptComponent)Activator.CreateInstance(type)!;
            }
            catch (Exception ex)
            {
                _engine.LogError($"Failed to instantiate '{type.FullName}': {ex.Message}");
                return null;
            }

            // Set runtime context
            instance.Initialize(entityId, _ecs, _engine, this);
            instance.TypeName = type.FullName ?? type.Name;

            // Awake
            instance.HasAwoken = true;
            SafeCall(instance, instance.Awake, nameof(ScriptComponent.Awake));

            // OnEnable (if enabled)
            if (instance.Enabled)
                SafeCall(instance, instance.OnEnable, nameof(ScriptComponent.OnEnable));

            // Register
            _allInstances.Add(instance);
            if (!_entityInstances.TryGetValue(entityId, out var list))
            {
                list = new List<ScriptComponent>();
                _entityInstances[entityId] = list;
            }
            list.Add(instance);

            // Defer Start to next update cycle
            _pendingStart.Add(instance);

            return instance;
        }

        // ── Instance queries ───────────────────────────────────────────────────────

        /// <summary>Gets the first script of type <typeparamref name="T"/> on an entity.</summary>
        public T? GetScript<T>(int entityId) where T : ScriptComponent
        {
            if (_entityInstances.TryGetValue(entityId, out var list))
            {
                foreach (var inst in list)
                {
                    if (inst is T t) return t;
                }
            }
            return null;
        }

        /// <summary>Gets a script by its full type name on an entity.</summary>
        public ScriptComponent? GetScript(int entityId, string typeName)
        {
            if (string.IsNullOrEmpty(typeName)) return null;
            if (_entityInstances.TryGetValue(entityId, out var list))
            {
                foreach (var inst in list)
                {
                    if (inst.TypeName == typeName) return inst;
                }
            }
            return null;
        }

        /// <summary>Returns all scripts attached to an entity (copy of the list).</summary>
        public IReadOnlyList<ScriptComponent> GetScripts(int entityId)
        {
            if (_entityInstances.TryGetValue(entityId, out var list))
                return list.ToList();
            return Array.Empty<ScriptComponent>();
        }

        /// <summary>Returns all scripts of type <typeparamref name="T"/> across all entities.</summary>
        public List<T> GetScriptsOfType<T>() where T : ScriptComponent
        {
            var result = new List<T>();
            foreach (var inst in _allInstances)
            {
                if (inst is T t) result.Add(t);
            }
            return result;
        }

        /// <summary>Returns all entity IDs that have at least one script.</summary>
        public IReadOnlyList<int> GetEntitiesWithScripts()
        {
            return _entityInstances.Keys.ToList();
        }

        /// <summary>Returns all live script instances (copy).</summary>
        public IReadOnlyList<ScriptComponent> GetAllInstances()
        {
            return _allInstances.ToList();
        }

        /// <summary>Checks whether an entity has at least one script of the given type name.</summary>
        public bool HasScript(int entityId, string typeName)
        {
            return GetScript(entityId, typeName) != null;
        }

        /// <summary>Checks whether an entity has at least one script of type <typeparamref name="T"/>.</summary>
        public bool HasScript<T>(int entityId) where T : ScriptComponent
        {
            return GetScript<T>(entityId) != null;
        }

        // ── Instance removal ───────────────────────────────────────────────────────

        /// <summary>
        /// Removes and destroys a script from an entity by type name.
        /// If called during an update cycle, destruction is deferred to the end of the cycle.
        /// </summary>
        /// <returns><c>true</c> if a matching script was found and scheduled for destruction.</returns>
        public bool RemoveScript(int entityId, string typeName)
        {
            if (string.IsNullOrEmpty(typeName)) return false;
            if (!_entityInstances.TryGetValue(entityId, out var list)) return false;

            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].TypeName == typeName)
                {
                    ScheduleDestroy(list[i]);
                    return true;
                }
            }
            return false;
        }

        /// <summary>Removes the first script of type <typeparamref name="T"/> from an entity.</summary>
        public bool RemoveScript<T>(int entityId) where T : ScriptComponent
        {
            if (!_entityInstances.TryGetValue(entityId, out var list)) return false;
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] is T)
                {
                    ScheduleDestroy(list[i]);
                    return true;
                }
            }
            return false;
        }

        /// <summary>Removes all scripts from an entity.</summary>
        public int RemoveAllScripts(int entityId)
        {
            if (!_entityInstances.TryGetValue(entityId, out var list)) return 0;
            int count = list.Count;
            foreach (var inst in list)
                ScheduleDestroy(inst);
            return count;
        }

        /// <summary>
        /// Schedules a specific instance for destruction.  If called during an update
        /// cycle, the destruction is deferred; otherwise it happens immediately.
        /// </summary>
        public void Destroy(ScriptComponent instance)
        {
            if (instance == null || instance.IsDestroyed) return;
            ScheduleDestroy(instance);
        }

        private void ScheduleDestroy(ScriptComponent instance)
        {
            if (instance.IsDestroyed) return;
            if (_isUpdating)
            {
                _toDestroy.Add(instance);
            }
            else
            {
                DestroyInstance(instance);
            }
        }

        // ── Lifecycle dispatch ─────────────────────────────────────────────────────

        /// <summary>
        /// Called every frame by the <see cref="ScriptEngine"/>.  Dispatches Start
        /// (for newly created instances), Update, and coroutine ticks.
        /// </summary>
        public void Update(float deltaTime)
        {
            _isUpdating = true;

            // 1. Process pending Start calls
            if (_pendingStart.Count > 0)
            {
                var toStart = _pendingStart.ToList();
                _pendingStart.Clear();
                foreach (var inst in toStart)
                {
                    if (inst.IsDestroyed) continue;
                    if (inst.Enabled && !inst.HasStarted)
                    {
                        inst.HasStarted = true;
                        SafeCall(inst, () => inst.Start(), nameof(ScriptComponent.Start));
                    }
                }
            }

            // 2. Update + coroutine tick
            foreach (var inst in _allInstances)
            {
                if (inst.IsDestroyed) continue;
                if (!inst.Enabled || inst.HasError) continue;

                SafeCall(inst, () => inst.Update(deltaTime), nameof(ScriptComponent.Update));
                inst.TickCoroutines(deltaTime, CoroutinePhase.Update);
            }

            _isUpdating = false;

            // 3. Process deferred destructions
            ProcessPendingDestroy();
        }

        /// <summary>Called at a fixed interval for physics-related script logic.</summary>
        public void FixedUpdate(float fixedDeltaTime)
        {
            _isUpdating = true;

            foreach (var inst in _allInstances)
            {
                if (inst.IsDestroyed) continue;
                if (!inst.Enabled || inst.HasError) continue;

                SafeCall(inst, () => inst.FixedUpdate(fixedDeltaTime), nameof(ScriptComponent.FixedUpdate));
                inst.TickCoroutines(fixedDeltaTime, CoroutinePhase.FixedUpdate);
            }

            _isUpdating = false;
            ProcessPendingDestroy();
        }

        /// <summary>Called after all Update calls have finished.</summary>
        public void LateUpdate(float deltaTime)
        {
            _isUpdating = true;

            foreach (var inst in _allInstances)
            {
                if (inst.IsDestroyed) continue;
                if (!inst.Enabled || inst.HasError) continue;

                SafeCall(inst, () => inst.LateUpdate(deltaTime), nameof(ScriptComponent.LateUpdate));
                inst.TickCoroutines(deltaTime, CoroutinePhase.EndOfFrame);
            }

            _isUpdating = false;
            ProcessPendingDestroy();
        }

        // ── Destruction helpers ─────────────────────────────────────────────────────

        private void ProcessPendingDestroy()
        {
            if (_toDestroy.Count == 0) return;
            foreach (var inst in _toDestroy)
            {
                if (!inst.IsDestroyed)
                    DestroyInstance(inst);
            }
            _toDestroy.Clear();
        }

        private void DestroyInstance(ScriptComponent instance)
        {
            if (instance.IsDestroyed) return;
            instance.IsDestroyed = true;

            // OnDisable
            if (instance.Enabled)
                SafeCall(instance, instance.OnDisable, nameof(ScriptComponent.OnDisable));

            // Stop all coroutines
            instance.StopAllCoroutines();

            // OnDestroy
            SafeCall(instance, instance.OnDestroy, nameof(ScriptComponent.OnDestroy));

            // Remove from storage
            _allInstances.Remove(instance);
            if (_entityInstances.TryGetValue(instance.EntityId, out var list))
            {
                list.Remove(instance);
                if (list.Count == 0)
                    _entityInstances.Remove(instance.EntityId);
            }
        }

        /// <summary>
        /// Destroys all script instances, calling OnDisable and OnDestroy on each.
        /// Types remain registered.
        /// </summary>
        public void DestroyAllInstances()
        {
            // Iterate in reverse (typically newest first)
            for (int i = _allInstances.Count - 1; i >= 0; i--)
            {
                DestroyInstance(_allInstances[i]);
            }
            _allInstances.Clear();
            _entityInstances.Clear();
            _pendingStart.Clear();
            _toDestroy.Clear();
        }

        // ── State capture / restore (hot-reload) ───────────────────────────────────

        /// <summary>
        /// Captures the serializable state of all live script instances for
        /// hot-reload preservation.
        /// </summary>
        public ScriptStateSnapshot CaptureState()
        {
            var snapshots = new List<ScriptInstanceSnapshot>();

            foreach (var inst in _allInstances)
            {
                if (inst.IsDestroyed) continue;

                var fieldValues = new Dictionary<string, object?>();
                var type = inst.GetType();

                // Capture public fields
                var publicFields = type.GetFields(BindingFlags.Instance | BindingFlags.Public);
                foreach (var field in publicFields)
                {
                    if (field.GetCustomAttribute<HideInInspectorAttribute>() != null) continue;
                    fieldValues[field.Name] = field.GetValue(inst);
                }

                // Capture [SerializeField] private/protected fields
                var nonPublicFields = type.GetFields(
                    BindingFlags.Instance | BindingFlags.NonPublic);
                foreach (var field in nonPublicFields)
                {
                    if (field.GetCustomAttribute<SerializeFieldAttribute>() != null)
                        fieldValues[field.Name] = field.GetValue(inst);
                }

                snapshots.Add(new ScriptInstanceSnapshot
                {
                    EntityId = inst.EntityId,
                    TypeName = inst.TypeName,
                    Enabled = inst.Enabled,
                    FieldValues = fieldValues,
                });
            }

            return new ScriptStateSnapshot { Instances = snapshots };
        }

        /// <summary>
        /// Restores previously captured state by recreating script instances with
        /// the same type/entity mapping and applying saved field values.
        /// Types must already be registered in the registry.
        /// </summary>
        /// <param name="snapshot">The state captured by <see cref="CaptureState"/>.</param>
        /// <returns>Number of instances successfully restored.</returns>
        public int RestoreState(ScriptStateSnapshot snapshot)
        {
            if (snapshot == null) return 0;

            int restored = 0;
            foreach (var snap in snapshot.Instances)
            {
                if (!_ecs.HasEntity(snap.EntityId))
                {
                    _engine.LogWarning(
                        $"Cannot restore '{snap.TypeName}': entity {snap.EntityId} no longer exists.");
                    continue;
                }

                var type = GetRegisteredType(snap.TypeName);
                if (type == null)
                {
                    _engine.LogWarning(
                        $"Cannot restore '{snap.TypeName}': type not registered after reload.");
                    continue;
                }

                var instance = CreateAndAttach(snap.EntityId, type);
                if (instance == null)
                {
                    _engine.LogError($"Failed to recreate instance '{snap.TypeName}' on entity {snap.EntityId}.");
                    continue;
                }

                // Restore enabled state
                if (!snap.Enabled)
                    instance.Enabled = false;

                // Restore field values
                foreach (var kvp in snap.FieldValues)
                {
                    instance.SetFieldValue(kvp.Key, kvp.Value);
                }

                restored++;
            }

            _engine.Log($"Restored {restored}/{snapshot.Count} script instance(s).");
            return restored;
        }

        // ── RequireComponent checking ──────────────────────────────────────────────

        /// <summary>
        /// Checks whether the entity satisfies all <see cref="RequireComponentAttribute"/>
        /// constraints on the given type.
        /// </summary>
        private bool CheckRequiredComponents(Type type, int entityId)
        {
            var attrs = type.GetCustomAttributes<RequireComponentAttribute>(inherit: true);
            foreach (var attr in attrs)
            {
                if (!HasComponentNonGeneric(entityId, attr.RequiredType))
                    return false;
            }
            return true;
        }

        /// <summary>
        /// Non-generic HasComponent check using reflection on the ECS.
        /// </summary>
        private bool HasComponentNonGeneric(int entityId, Type componentType)
        {
            // Try the generic method via reflection
            var method = typeof(EntityComponentSystem)
                .GetMethods()
                .FirstOrDefault(m => m.Name == nameof(EntityComponentSystem.HasComponent)
                                     && m.IsGenericMethod);

            if (method != null)
            {
                var generic = method.MakeGenericMethod(componentType);
                return (bool)generic.Invoke(_ecs, new object[] { entityId })!;
            }

            // Fallback: check if the component type exists at all in the ECS
            return false;
        }

        // ── Entity-destroyed handler ───────────────────────────────────────────────

        private void OnEntityDestroyed(int entityId)
        {
            RemoveAllScripts(entityId);
        }

        // ── Safe invocation ────────────────────────────────────────────────────────

        /// <summary>
        /// Invokes a lifecycle action inside a try/catch.  Exceptions are logged and
        /// the instance's error counter is incremented.  After <see cref="ScriptComponent.MaxErrors"/>
        /// errors, the instance is automatically disabled.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void SafeCall(ScriptComponent instance, Action action, string methodName)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                instance.ErrorCount++;
                _engine.LogError(
                    $"[{instance.TypeName}.{methodName}] {ex.GetType().Name}: {ex.Message}");

                if (instance.HasError)
                {
                    instance.Enabled = false;
                    _engine.LogError(
                        $"[{instance.TypeName}] Disabled after {ScriptComponent.MaxErrors} errors.");
                }
            }
        }

        // ── Disposal ───────────────────────────────────────────────────────────────

        /// <summary>Destroys all instances and unregisters from ECS events.</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _ecs.EntityDestroyed -= OnEntityDestroyed;
            DestroyAllInstances();
            ClearTypes();
            _disposed = true;
        }
    }
}
