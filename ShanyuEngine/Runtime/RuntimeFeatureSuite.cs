using System.IO.Compression;
using System.Numerics;
using System.Text;
using System.Text.Json;
using ShanyuEngine.Assets;
using ShanyuEngine.ECS;
using ShanyuEngine.Rendering;

namespace ShanyuEngine.Runtime
{
    /// <summary>
    /// 运行时组件兼容层。它继承资产组件接口，因此现有 SceneObject.Components 可以直接承载运行时脚本组件。
    /// </summary>
    public interface IComponent : IAssetComponent
    {
    }

    public enum RuntimePhase
    {
        Initialize,
        Start,
        PreUpdate,
        FixedUpdate,
        Update,
        LateUpdate,
        Render,
        Stop
    }

    public readonly record struct RuntimeFrameContext(
        int FrameIndex,
        float DeltaTime,
        float UnscaledDeltaTime,
        float FixedDeltaTime,
        float TotalTime,
        int FixedStepCount,
        RuntimePhase Phase);

    public sealed class RuntimeClockOptions
    {
        public float TimeScale { get; set; } = 1f;
        public float FixedDeltaTime { get; set; } = 1f / 60f;
        public float MaxDeltaTime { get; set; } = 0.25f;
        public int MaxFixedStepsPerFrame { get; set; } = 6;
    }

    public sealed class RuntimeClock
    {
        private readonly RuntimeClockOptions _options;
        private float _fixedAccumulator;

        public RuntimeClock(RuntimeClockOptions? options = null)
        {
            _options = options ?? new RuntimeClockOptions();
        }

        public int FrameIndex { get; private set; }
        public float TotalTime { get; private set; }
        public float UnscaledTotalTime { get; private set; }
        public float FixedAccumulator => _fixedAccumulator;
        public RuntimeClockOptions Options => _options;

        public RuntimeFrameContext Step(float realDeltaTime)
        {
            var clamped = Math.Clamp(realDeltaTime, 0f, Math.Max(0.001f, _options.MaxDeltaTime));
            var scaled = clamped * Math.Max(0f, _options.TimeScale);
            UnscaledTotalTime += clamped;
            TotalTime += scaled;
            FrameIndex++;

            _fixedAccumulator += scaled;
            var fixedDelta = _options.FixedDeltaTime <= 0f ? 1f / 60f : _options.FixedDeltaTime;
            var steps = 0;
            while (_fixedAccumulator + 0.000001f >= fixedDelta && steps < _options.MaxFixedStepsPerFrame)
            {
                _fixedAccumulator -= fixedDelta;
                steps++;
            }

            return new RuntimeFrameContext(
                FrameIndex,
                scaled,
                clamped,
                fixedDelta,
                TotalTime,
                steps,
                RuntimePhase.Update);
        }
    }

    public interface IRuntimeLifecycleSystem : IDisposable
    {
        string Name { get; }
        int Order { get; }
        void Initialize(RuntimeLifecycleHost host);
        void Start(RuntimeLifecycleHost host);
        void Tick(RuntimeFrameContext frame, RuntimeLifecycleHost host);
        void LateTick(RuntimeFrameContext frame, RuntimeLifecycleHost host);
        void Stop(RuntimeLifecycleHost host);
    }

    public abstract class RuntimeLifecycleSystemBase : IRuntimeLifecycleSystem
    {
        protected RuntimeLifecycleSystemBase(string name, int order = 0)
        {
            Name = name;
            Order = order;
        }

        public string Name { get; }
        public int Order { get; }
        public bool IsInitialized { get; private set; }
        public bool IsStarted { get; private set; }

        public virtual void Initialize(RuntimeLifecycleHost host) => IsInitialized = true;
        public virtual void Start(RuntimeLifecycleHost host) => IsStarted = true;
        public virtual void Tick(RuntimeFrameContext frame, RuntimeLifecycleHost host) { }
        public virtual void LateTick(RuntimeFrameContext frame, RuntimeLifecycleHost host) { }
        public virtual void Stop(RuntimeLifecycleHost host) => IsStarted = false;
        public virtual void Dispose() { }
    }

    public sealed class RuntimeLifecycleHost : IDisposable
    {
        private readonly List<IRuntimeLifecycleSystem> _systems = new();
        private readonly Dictionary<Type, object> _services = new();
        private bool _initialized;
        private bool _started;

        public RuntimeLifecycleHost(RuntimeWorld world, RuntimeClock? clock = null)
        {
            World = world ?? throw new ArgumentNullException(nameof(world));
            Clock = clock ?? new RuntimeClock();
            AddService(World);
            AddService(Clock);
        }

        public RuntimeWorld World { get; }
        public RuntimeClock Clock { get; }
        public IReadOnlyList<IRuntimeLifecycleSystem> Systems => _systems;
        public event Action<RuntimePhase, string>? PhaseCompleted;

        public RuntimeLifecycleHost AddService<T>(T service) where T : class
        {
            _services[typeof(T)] = service ?? throw new ArgumentNullException(nameof(service));
            return this;
        }

        public T GetService<T>() where T : class
        {
            if (_services.TryGetValue(typeof(T), out var service)) return (T)service;
            throw new InvalidOperationException($"Runtime service not registered: {typeof(T).FullName}");
        }

        public RuntimeLifecycleHost AddSystem(IRuntimeLifecycleSystem system)
        {
            if (_started) throw new InvalidOperationException("Cannot add lifecycle systems after Start.");
            _systems.Add(system ?? throw new ArgumentNullException(nameof(system)));
            _systems.Sort((a, b) => a.Order != b.Order
                ? a.Order.CompareTo(b.Order)
                : string.Compare(a.Name, b.Name, StringComparison.Ordinal));
            return this;
        }

        public void Initialize()
        {
            if (_initialized) return;
            foreach (var system in _systems)
            {
                system.Initialize(this);
                PhaseCompleted?.Invoke(RuntimePhase.Initialize, system.Name);
            }
            _initialized = true;
        }

        public void Start()
        {
            Initialize();
            if (_started) return;
            foreach (var system in _systems)
            {
                system.Start(this);
                PhaseCompleted?.Invoke(RuntimePhase.Start, system.Name);
            }
            _started = true;
        }

        public RuntimeFrameContext Tick(float realDeltaTime)
        {
            Start();
            var frame = Clock.Step(realDeltaTime);
            foreach (var system in _systems)
            {
                system.Tick(frame, this);
                PhaseCompleted?.Invoke(RuntimePhase.Update, system.Name);
            }
            foreach (var system in _systems)
            {
                system.LateTick(frame, this);
                PhaseCompleted?.Invoke(RuntimePhase.LateUpdate, system.Name);
            }
            return frame;
        }

        public void Stop()
        {
            if (!_started) return;
            for (var i = _systems.Count - 1; i >= 0; i--)
            {
                _systems[i].Stop(this);
                PhaseCompleted?.Invoke(RuntimePhase.Stop, _systems[i].Name);
            }
            _started = false;
        }

        public void Dispose()
        {
            Stop();
            foreach (var system in _systems) system.Dispose();
            _systems.Clear();
            _services.Clear();
        }
    }

    public readonly record struct RuntimeEntityBinding(Guid ObjectId, int EntityId, string Name);

    public sealed class RuntimeGameObjectEntityBridge
    {
        private readonly EntityComponentSystem _ecs;
        private readonly Dictionary<Guid, RuntimeEntityBinding> _objectToEntity = new();
        private readonly Dictionary<int, SceneObject> _entityToObject = new();

        public RuntimeGameObjectEntityBridge(EntityComponentSystem ecs)
        {
            _ecs = ecs ?? throw new ArgumentNullException(nameof(ecs));
        }

        public IReadOnlyDictionary<Guid, RuntimeEntityBinding> Bindings => _objectToEntity;

        public RuntimeEntityBinding BindObject(SceneObject obj)
        {
            if (obj == null) throw new ArgumentNullException(nameof(obj));
            if (_objectToEntity.TryGetValue(obj.Id, out var existing) && _ecs.HasEntity(existing.EntityId))
                return existing;

            var entityId = _ecs.CreateEntity(obj.Name, obj.Position, obj.Rotation, obj.Scale);
            _ecs.AddComponent(entityId, new TagComponent
            {
                Tag = string.IsNullOrWhiteSpace(obj.Name) ? "GameObject" : obj.Name,
                Layer = obj.Layer,
                LayerMask = 0xFFFFFFFF,
                IsStatic = false
            });

            if (!string.IsNullOrWhiteSpace(obj.MeshAssetPath))
            {
                _ecs.AddComponent(entityId, new MeshComponent
                {
                    MeshId = StableGuid(obj.MeshAssetPath),
                    MaterialId = StableGuid(obj.MaterialAssetPath ?? string.Empty),
                    CastsShadow = obj.CastShadow,
                    ReceivesShadow = obj.ReceiveShadow,
                    IsVisible = obj.IsVisible
                });
            }

            var binding = new RuntimeEntityBinding(obj.Id, entityId, obj.Name);
            _objectToEntity[obj.Id] = binding;
            _entityToObject[entityId] = obj;
            return binding;
        }

        public void BindWorld(RuntimeWorld world)
        {
            foreach (var obj in world.Objects)
            {
                BindObject(obj);
            }
        }

        public bool TryGetEntity(Guid objectId, out int entityId)
        {
            if (_objectToEntity.TryGetValue(objectId, out var binding))
            {
                entityId = binding.EntityId;
                return true;
            }
            entityId = 0;
            return false;
        }

        public bool TryGetObject(int entityId, out SceneObject? obj) => _entityToObject.TryGetValue(entityId, out obj);

        public void SyncObjectsToEntities()
        {
            foreach (var pair in _entityToObject)
            {
                if (!_ecs.HasEntity(pair.Key)) continue;
                var obj = pair.Value;
                if (_ecs.HasComponent<TransformComponent>(pair.Key))
                {
                    var transform = _ecs.GetComponent<TransformComponent>(pair.Key);
                    transform.Position = obj.Position;
                    transform.Rotation = obj.Rotation;
                    transform.Scale = obj.Scale;
                    _ecs.SetComponent(pair.Key, transform);
                }
            }
        }

        public void SyncEntitiesToObjects()
        {
            foreach (var pair in _entityToObject)
            {
                if (!_ecs.HasEntity(pair.Key) || !_ecs.HasComponent<TransformComponent>(pair.Key)) continue;
                var transform = _ecs.GetComponent<TransformComponent>(pair.Key);
                pair.Value.Position = transform.Position;
                pair.Value.Rotation = transform.Rotation;
                pair.Value.Scale = transform.Scale;
                pair.Value.WorldMatrix = transform.Matrix;
            }
        }

        public bool Unbind(Guid objectId, bool destroyEntity = false)
        {
            if (!_objectToEntity.TryGetValue(objectId, out var binding)) return false;
            _objectToEntity.Remove(objectId);
            _entityToObject.Remove(binding.EntityId);
            if (destroyEntity && _ecs.HasEntity(binding.EntityId))
            {
                _ecs.DestroyEntity(binding.EntityId);
            }
            return true;
        }

        private static Guid StableGuid(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return Guid.Empty;
            var bytes = System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes(text));
            return new Guid(bytes);
        }
    }

    public sealed class RuntimeBridgeSystem : RuntimeLifecycleSystemBase
    {
        private readonly RuntimeGameObjectEntityBridge _bridge;

        public RuntimeBridgeSystem(RuntimeGameObjectEntityBridge bridge, int order = -100) : base("GameObject/Entity Bridge", order)
        {
            _bridge = bridge;
        }

        public override void Initialize(RuntimeLifecycleHost host)
        {
            base.Initialize(host);
            _bridge.BindWorld(host.World);
        }

        public override void Tick(RuntimeFrameContext frame, RuntimeLifecycleHost host) => _bridge.SyncObjectsToEntities();
        public override void LateTick(RuntimeFrameContext frame, RuntimeLifecycleHost host) => _bridge.SyncEntitiesToObjects();
    }

    public sealed class RuntimeComponentDescriptor
    {
        public string TypeName { get; init; } = string.Empty;
        public string DisplayName { get; init; } = string.Empty;
        public string Category { get; init; } = "Runtime";
        public Func<IComponent> Factory { get; init; } = () => new RuntimeTagComponent();
        public Dictionary<string, object> Defaults { get; init; } = new();

        public IComponent Create()
        {
            var component = Factory();
            if (component is IRuntimeConfigurableComponent configurable)
            {
                configurable.Configure(Defaults);
            }
            return component;
        }
    }

    public interface IRuntimeConfigurableComponent
    {
        void Configure(IReadOnlyDictionary<string, object> values);
    }

    public sealed class RuntimeComponentRegistry
    {
        private readonly Dictionary<string, RuntimeComponentDescriptor> _descriptors = new(StringComparer.OrdinalIgnoreCase);

        public IEnumerable<RuntimeComponentDescriptor> Descriptors => _descriptors.Values.OrderBy(d => d.Category).ThenBy(d => d.DisplayName);

        public RuntimeComponentRegistry Register(RuntimeComponentDescriptor descriptor)
        {
            if (descriptor == null) throw new ArgumentNullException(nameof(descriptor));
            if (string.IsNullOrWhiteSpace(descriptor.TypeName)) throw new ArgumentException("Component descriptor requires TypeName.");
            _descriptors[descriptor.TypeName] = descriptor;
            return this;
        }

        public bool TryCreate(string typeName, out IComponent? component)
        {
            if (_descriptors.TryGetValue(typeName, out var descriptor))
            {
                component = descriptor.Create();
                return true;
            }
            component = null;
            return false;
        }

        public static RuntimeComponentRegistry CreateDefault()
        {
            return new RuntimeComponentRegistry()
                .Register(new RuntimeComponentDescriptor
                {
                    TypeName = "Runtime.Tag",
                    DisplayName = "Runtime Tag",
                    Category = "Identity",
                    Factory = () => new RuntimeTagComponent()
                })
                .Register(new RuntimeComponentDescriptor
                {
                    TypeName = "Runtime.Rotator",
                    DisplayName = "Rotator",
                    Category = "Movement",
                    Factory = () => new RuntimeRotatorComponent(),
                    Defaults = new Dictionary<string, object> { ["YawDegreesPerSecond"] = 45f }
                })
                .Register(new RuntimeComponentDescriptor
                {
                    TypeName = "Runtime.Bob",
                    DisplayName = "Bobbing Motion",
                    Category = "Movement",
                    Factory = () => new RuntimeBobbingComponent(),
                    Defaults = new Dictionary<string, object> { ["Amplitude"] = 0.5f, ["Frequency"] = 1f }
                })
                .Register(new RuntimeComponentDescriptor
                {
                    TypeName = "Runtime.Lifetime",
                    DisplayName = "Lifetime",
                    Category = "Gameplay",
                    Factory = () => new RuntimeLifetimeComponent(),
                    Defaults = new Dictionary<string, object> { ["Seconds"] = 5f }
                });
        }
    }

    public sealed class RuntimeTagComponent : IComponent, IRuntimeConfigurableComponent
    {
        public string Name => "Runtime Tag";
        public string Tag { get; set; } = "Untagged";
        public void Update(float deltaTime, SceneObject owner) { }
        public void Configure(IReadOnlyDictionary<string, object> values)
        {
            if (values.TryGetValue("Tag", out var tag)) Tag = Convert.ToString(tag) ?? Tag;
        }
    }

    public sealed class RuntimeRotatorComponent : IComponent, IRuntimeConfigurableComponent
    {
        public string Name => "Rotator";
        public Vector3 Axis { get; set; } = Vector3.UnitY;
        public float DegreesPerSecond { get; set; } = 45f;

        public void Update(float deltaTime, SceneObject owner)
        {
            if (Axis.LengthSquared() <= 0.0001f) return;
            var radians = DegreesPerSecond * MathF.PI / 180f * deltaTime;
            owner.Rotation = Quaternion.Normalize(Quaternion.CreateFromAxisAngle(Vector3.Normalize(Axis), radians) * owner.Rotation);
        }

        public void Configure(IReadOnlyDictionary<string, object> values)
        {
            if (values.TryGetValue("YawDegreesPerSecond", out var yaw)) DegreesPerSecond = Convert.ToSingle(yaw);
        }
    }

    public sealed class RuntimeBobbingComponent : IComponent, IRuntimeConfigurableComponent
    {
        private float _time;
        private Vector3? _origin;

        public string Name => "Bobbing Motion";
        public float Amplitude { get; set; } = 0.5f;
        public float Frequency { get; set; } = 1f;

        public void Update(float deltaTime, SceneObject owner)
        {
            _origin ??= owner.Position;
            _time += Math.Max(0f, deltaTime);
            owner.Position = _origin.Value + Vector3.UnitY * (MathF.Sin(_time * Frequency * MathF.Tau) * Amplitude);
        }

        public void Configure(IReadOnlyDictionary<string, object> values)
        {
            if (values.TryGetValue("Amplitude", out var amplitude)) Amplitude = Convert.ToSingle(amplitude);
            if (values.TryGetValue("Frequency", out var frequency)) Frequency = Convert.ToSingle(frequency);
        }
    }

    public sealed class RuntimeLifetimeComponent : IComponent, IRuntimeConfigurableComponent
    {
        public string Name => "Lifetime";
        public float Seconds { get; set; } = 5f;
        public float Age { get; private set; }
        public bool Expired => Age >= Seconds;

        public void Update(float deltaTime, SceneObject owner)
        {
            Age += Math.Max(0f, deltaTime);
            if (Expired) owner.IsVisible = false;
        }

        public void Configure(IReadOnlyDictionary<string, object> values)
        {
            if (values.TryGetValue("Seconds", out var seconds)) Seconds = Convert.ToSingle(seconds);
        }
    }

    public abstract class RuntimeWaitInstruction
    {
        public abstract bool Tick(RuntimeFrameContext frame);
    }

    public static class RuntimeWait
    {
        public static RuntimeWaitInstruction ForSeconds(float seconds) => new SecondsWait(seconds);
        public static RuntimeWaitInstruction ForFrames(int frames) => new FramesWait(frames);
        public static RuntimeWaitInstruction Until(Func<bool> predicate) => new PredicateWait(predicate);

        private sealed class SecondsWait : RuntimeWaitInstruction
        {
            private readonly float _target;
            private float _elapsed;
            public SecondsWait(float seconds) => _target = Math.Max(0f, seconds);
            public override bool Tick(RuntimeFrameContext frame)
            {
                _elapsed += Math.Max(0f, frame.DeltaTime);
                return _elapsed + 0.000001f >= _target;
            }
        }

        private sealed class FramesWait : RuntimeWaitInstruction
        {
            private readonly int _target;
            private int _elapsed;
            public FramesWait(int frames) => _target = Math.Max(0, frames);
            public override bool Tick(RuntimeFrameContext frame)
            {
                _elapsed++;
                return _elapsed >= _target;
            }
        }

        private sealed class PredicateWait : RuntimeWaitInstruction
        {
            private readonly Func<bool> _predicate;
            public PredicateWait(Func<bool> predicate) => _predicate = predicate ?? throw new ArgumentNullException(nameof(predicate));
            public override bool Tick(RuntimeFrameContext frame) => _predicate();
        }
    }

    public sealed class RuntimeCoroutine
    {
        internal RuntimeCoroutine(string name, IEnumerator<object?> enumerator)
        {
            Name = name;
            Enumerator = enumerator;
        }

        internal IEnumerator<object?> Enumerator { get; }
        internal RuntimeWaitInstruction? Wait { get; set; }
        public string Name { get; }
        public bool IsRunning { get; internal set; } = true;
        public bool IsCompleted { get; internal set; }
        public Exception? Error { get; internal set; }
    }

    public sealed class RuntimeCoroutineScheduler
    {
        private readonly RuntimeClock _clock;
        private readonly List<RuntimeCoroutine> _coroutines = new();

        public RuntimeCoroutineScheduler(RuntimeClock clock)
        {
            _clock = clock;
        }

        public IReadOnlyList<RuntimeCoroutine> Coroutines => _coroutines;

        public RuntimeCoroutine Start(IEnumerable<object?> routine, string name = "Coroutine")
        {
            var coroutine = new RuntimeCoroutine(name, routine.GetEnumerator());
            _coroutines.Add(coroutine);
            return coroutine;
        }

        public void Tick(RuntimeFrameContext? frame = null)
        {
            var context = frame ?? new RuntimeFrameContext(
                _clock.FrameIndex,
                0f,
                0f,
                _clock.Options.FixedDeltaTime,
                _clock.TotalTime,
                0,
                RuntimePhase.Update);

            foreach (var coroutine in _coroutines.ToArray())
            {
                if (!coroutine.IsRunning || coroutine.IsCompleted) continue;
                Advance(coroutine, context);
            }
            _coroutines.RemoveAll(c => c.IsCompleted);
        }

        private static void Advance(RuntimeCoroutine coroutine, RuntimeFrameContext frame)
        {
            try
            {
                var guard = 0;
                while (coroutine.IsRunning && !coroutine.IsCompleted && guard++ < 16)
                {
                    if (coroutine.Wait != null && !coroutine.Wait.Tick(frame)) return;
                    coroutine.Wait = null;

                    if (!coroutine.Enumerator.MoveNext())
                    {
                        coroutine.IsCompleted = true;
                        coroutine.IsRunning = false;
                        return;
                    }

                    coroutine.Wait = coroutine.Enumerator.Current as RuntimeWaitInstruction;
                    if (coroutine.Wait == null) return;
                }
            }
            catch (Exception ex)
            {
                coroutine.Error = ex;
                coroutine.IsCompleted = true;
                coroutine.IsRunning = false;
            }
        }
    }

    public sealed class RuntimeStateContext
    {
        public RuntimeStateContext(float deltaTime, Dictionary<string, object> blackboard)
        {
            DeltaTime = deltaTime;
            Blackboard = blackboard;
        }

        public float DeltaTime { get; }
        public Dictionary<string, object> Blackboard { get; }
    }

    public sealed class RuntimeStateDefinition<TState> where TState : notnull
    {
        private readonly List<RuntimeTransition<TState>> _transitions = new();
        private Action<RuntimeStateContext>? _enter;
        private Action<RuntimeStateContext>? _exit;
        private Action<RuntimeStateContext>? _tick;

        public RuntimeStateDefinition(TState state)
        {
            State = state;
        }

        public TState State { get; }
        public IReadOnlyList<RuntimeTransition<TState>> Transitions => _transitions;
        public RuntimeStateDefinition<TState> OnEnter(Action<RuntimeStateContext> action) { _enter += action; return this; }
        public RuntimeStateDefinition<TState> OnExit(Action<RuntimeStateContext> action) { _exit += action; return this; }
        public RuntimeStateDefinition<TState> OnTick(Action<RuntimeStateContext> action) { _tick += action; return this; }
        public RuntimeStateDefinition<TState> Permit(string trigger, TState target) => PermitIf(trigger, target, _ => true);
        public RuntimeStateDefinition<TState> PermitIf(string trigger, TState target, Func<RuntimeStateContext, bool> guard)
        {
            _transitions.Add(new RuntimeTransition<TState>(trigger, target, guard));
            return this;
        }

        internal void Enter(RuntimeStateContext context) => _enter?.Invoke(context);
        internal void Exit(RuntimeStateContext context) => _exit?.Invoke(context);
        internal void Tick(RuntimeStateContext context) => _tick?.Invoke(context);
    }

    public readonly record struct RuntimeTransition<TState>(string Trigger, TState Target, Func<RuntimeStateContext, bool> Guard)
        where TState : notnull;

    public sealed class RuntimeStateMachine<TState> where TState : notnull
    {
        private readonly Dictionary<TState, RuntimeStateDefinition<TState>> _states = new();

        public RuntimeStateMachine(TState initialState)
        {
            CurrentState = initialState;
        }

        public TState CurrentState { get; private set; }
        public Dictionary<string, object> Blackboard { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool IsStarted { get; private set; }
        public event Action<TState, TState, string>? Transitioned;

        public RuntimeStateDefinition<TState> Configure(TState state)
        {
            if (!_states.TryGetValue(state, out var definition))
            {
                definition = new RuntimeStateDefinition<TState>(state);
                _states[state] = definition;
            }
            return definition;
        }

        public void Start()
        {
            if (IsStarted) return;
            IsStarted = true;
            Get(CurrentState).Enter(new RuntimeStateContext(0f, Blackboard));
        }

        public void Tick(float deltaTime)
        {
            Start();
            Get(CurrentState).Tick(new RuntimeStateContext(deltaTime, Blackboard));
        }

        public bool Fire(string trigger)
        {
            Start();
            var context = new RuntimeStateContext(0f, Blackboard);
            var state = Get(CurrentState);
            foreach (var transition in state.Transitions)
            {
                if (!string.Equals(transition.Trigger, trigger, StringComparison.OrdinalIgnoreCase)) continue;
                if (!transition.Guard(context)) continue;

                var previous = CurrentState;
                state.Exit(context);
                CurrentState = transition.Target;
                Get(CurrentState).Enter(context);
                Transitioned?.Invoke(previous, CurrentState, trigger);
                return true;
            }
            return false;
        }

        private RuntimeStateDefinition<TState> Get(TState state) => _states.TryGetValue(state, out var definition)
            ? definition
            : Configure(state);
    }

    public sealed class RuntimeBehaviorGraph
    {
        private readonly Dictionary<string, RuntimeBehaviorNode> _nodes = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<RuntimeBehaviorEdge> _edges = new();

        public RuntimeBehaviorGraph AddNode(RuntimeBehaviorNode node)
        {
            _nodes[node.Id] = node;
            return this;
        }

        public RuntimeBehaviorGraph Connect(string from, string to, Func<RuntimeBehaviorContext, bool>? condition = null)
        {
            _edges.Add(new RuntimeBehaviorEdge(from, to, condition ?? (_ => true)));
            return this;
        }

        public RuntimeBehaviorResult Tick(string startNode, RuntimeBehaviorContext context)
        {
            var current = startNode;
            var visited = new List<string>();
            for (var guard = 0; guard < 64; guard++)
            {
                if (!_nodes.TryGetValue(current, out var node))
                    return new RuntimeBehaviorResult(false, current, visited, $"Missing node: {current}");
                visited.Add(current);
                var status = node.Execute(context);
                if (status != RuntimeBehaviorStatus.Success)
                    return new RuntimeBehaviorResult(status == RuntimeBehaviorStatus.Running, current, visited, status.ToString());

                var next = _edges.FirstOrDefault(e => e.From == current && e.Condition(context));
                if (next.To == null) return new RuntimeBehaviorResult(true, current, visited, "Completed");
                current = next.To;
            }
            return new RuntimeBehaviorResult(false, current, visited, "Behavior graph cycle guard tripped.");
        }
    }

    public sealed class RuntimeBehaviorContext
    {
        public RuntimeBehaviorContext(SceneObject owner, float deltaTime)
        {
            Owner = owner;
            DeltaTime = deltaTime;
        }

        public SceneObject Owner { get; }
        public float DeltaTime { get; }
        public Dictionary<string, object> Blackboard { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public enum RuntimeBehaviorStatus { Success, Failure, Running }
    public readonly record struct RuntimeBehaviorNode(string Id, Func<RuntimeBehaviorContext, RuntimeBehaviorStatus> Execute);
    public readonly record struct RuntimeBehaviorEdge(string From, string To, Func<RuntimeBehaviorContext, bool> Condition);
    public readonly record struct RuntimeBehaviorResult(bool Success, string LastNode, IReadOnlyList<string> VisitedNodes, string Message);

    public sealed record RuntimeSaveSlot(string Id, string DisplayName);

    public sealed class RuntimeSaveObject
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public Vector3 Position { get; set; }
        public Quaternion Rotation { get; set; }
        public Vector3 Scale { get; set; } = Vector3.One;
        public string MeshAssetPath { get; set; } = string.Empty;
        public string MaterialAssetPath { get; set; } = string.Empty;
        public bool IsVisible { get; set; } = true;
        public List<string> ComponentNames { get; set; } = new();
    }

    public sealed class RuntimeSaveSnapshot
    {
        public int Version { get; set; } = 1;
        public RuntimeSaveSlot Slot { get; set; } = new("default", "Default");
        public string SceneName { get; set; } = "Main";
        public DateTime SavedAtUtc { get; set; } = DateTime.UtcNow;
        public Dictionary<string, string> GameVariables { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public List<RuntimeSaveObject> Objects { get; set; } = new();

        public static RuntimeSaveSnapshot FromWorld(RuntimeWorld world, RuntimeSaveSlot slot)
        {
            var snapshot = new RuntimeSaveSnapshot
            {
                Slot = slot,
                SceneName = world.SceneName,
                Objects = world.Objects.Select(o => new RuntimeSaveObject
                {
                    Id = o.Id,
                    Name = o.Name,
                    Position = o.Position,
                    Rotation = o.Rotation,
                    Scale = o.Scale,
                    MeshAssetPath = o.MeshAssetPath ?? string.Empty,
                    MaterialAssetPath = o.MaterialAssetPath ?? string.Empty,
                    IsVisible = o.IsVisible,
                    ComponentNames = o.Components.Select(c => c.Name).ToList()
                }).ToList()
            };
            return snapshot;
        }

        public RuntimeWorld RestoreWorld()
        {
            var objects = Objects.Select(o => new SceneObject
            {
                Id = o.Id,
                Name = o.Name,
                Position = o.Position,
                Rotation = o.Rotation,
                Scale = o.Scale,
                MeshAssetPath = o.MeshAssetPath,
                MaterialAssetPath = o.MaterialAssetPath,
                IsVisible = o.IsVisible
            }).ToList();

            return new RuntimeWorld
            {
                SceneName = SceneName,
                Objects = objects,
                RenderScene = RenderSceneBuilder.FromObjects(objects, SceneName)
            };
        }
    }

    public sealed class RuntimeSaveGameStore
    {
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("SYRTSAVE");
        private readonly JsonSerializerOptions _json = new() { WriteIndented = false };

        public byte[] Write(RuntimeSaveSnapshot snapshot)
        {
            using var payload = new MemoryStream();
            using (var gzip = new GZipStream(payload, CompressionLevel.SmallestSize, leaveOpen: true))
            {
                JsonSerializer.Serialize(gzip, snapshot, _json);
            }

            using var output = new MemoryStream();
            using var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);
            writer.Write(Magic);
            writer.Write(snapshot.Version);
            writer.Write(DateTime.UtcNow.Ticks);
            writer.Write(payload.Length);
            writer.Write(payload.ToArray());
            writer.Flush();
            return output.ToArray();
        }

        public RuntimeSaveSnapshot Read(byte[] bytes)
        {
            using var input = new MemoryStream(bytes);
            using var reader = new BinaryReader(input, Encoding.UTF8, leaveOpen: true);
            var magic = reader.ReadBytes(Magic.Length);
            if (!magic.SequenceEqual(Magic)) throw new InvalidDataException("Not a Shanyu runtime save payload.");
            _ = reader.ReadInt32();
            _ = reader.ReadInt64();
            var length = reader.ReadInt64();
            if (length < 0 || length > bytes.Length) throw new InvalidDataException("Runtime save payload length is invalid.");
            var compressed = reader.ReadBytes((int)length);

            using var payload = new MemoryStream(compressed);
            using var gzip = new GZipStream(payload, CompressionMode.Decompress);
            return JsonSerializer.Deserialize<RuntimeSaveSnapshot>(gzip, _json)
                ?? throw new InvalidDataException("Runtime save snapshot could not be decoded.");
        }
    }
}
