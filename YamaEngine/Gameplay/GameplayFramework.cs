using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using YamaEngine.Assets;
using YamaEngine.GameFramework;

namespace YamaEngine.Gameplay
{
    // ============================================================
    // yama Gameplay Framework —— 对齐 UE3 Actor/Component/Pawn/Controller/GameMode
    // 全部为自研运行时核心；外部脚本/编辑器只可与之绑定，不可替代它。
    // ============================================================

    public enum ActorLifecycleState
    {
        Constructed,
        BeginPlayed,
        Ticking,
        EndPlayed,
        Destroyed
    }

    /// <summary>
    /// 所有运行时游戏对象的基类。提供 Tick、生命周期与组件聚合。
    /// </summary>
    public class Actor
    {
        private readonly List<ActorComponent> _components = new();
        private readonly List<ActorComponent> _scratchTickList = new();

        public Guid Id { get; } = Guid.NewGuid();
        public string Name { get; set; } = "Actor";

        public Vector3 Position { get; set; } = Vector3.Zero;
        public Quaternion Rotation { get; set; } = Quaternion.Identity;
        public Vector3 Scale { get; set; } = Vector3.One;

        public bool TickEnabled { get; set; } = true;
        public ActorLifecycleState LifecycleState { get; private set; } = ActorLifecycleState.Constructed;
        public GameplayWorld? World { get; internal set; }
        public IReadOnlyList<ActorComponent> Components => _components;

        public Matrix4x4 GetWorldMatrix()
        {
            return Matrix4x4.CreateScale(Scale) *
                   Matrix4x4.CreateFromQuaternion(Rotation) *
                   Matrix4x4.CreateTranslation(Position);
        }

        public T AddComponent<T>() where T : ActorComponent, new()
        {
            var c = new T();
            AttachComponent(c);
            return c;
        }

        public T AttachComponent<T>(T component) where T : ActorComponent
        {
            if (component == null) throw new ArgumentNullException(nameof(component));
            if (component.Owner != null && component.Owner != this)
                throw new InvalidOperationException("Component already attached to another actor.");
            component.Owner = this;
            _components.Add(component);
            if (LifecycleState == ActorLifecycleState.BeginPlayed || LifecycleState == ActorLifecycleState.Ticking)
            {
                component.InvokeBeginPlay();
            }
            return component;
        }

        public T? GetComponent<T>() where T : ActorComponent
        {
            foreach (var c in _components)
                if (c is T t) return t;
            return null;
        }

        public IEnumerable<T> GetComponents<T>() where T : ActorComponent
        {
            foreach (var c in _components)
                if (c is T t) yield return t;
        }

        public bool RemoveComponent(ActorComponent component)
        {
            if (component == null) return false;
            if (_components.Remove(component))
            {
                component.InvokeEndPlay();
                component.Owner = null;
                return true;
            }
            return false;
        }

        internal void InvokeBeginPlay()
        {
            if (LifecycleState != ActorLifecycleState.Constructed) return;
            LifecycleState = ActorLifecycleState.BeginPlayed;
            OnBeginPlay();
            foreach (var c in _components.ToArray())
                c.InvokeBeginPlay();
            LifecycleState = ActorLifecycleState.Ticking;
        }

        internal void InvokeTick(float deltaTime)
        {
            if (LifecycleState != ActorLifecycleState.Ticking || !TickEnabled) return;
            OnTick(deltaTime);
            _scratchTickList.Clear();
            _scratchTickList.AddRange(_components);
            foreach (var c in _scratchTickList)
            {
                if (c.TickEnabled) c.InvokeTick(deltaTime);
            }
        }

        internal void InvokeEndPlay()
        {
            if (LifecycleState == ActorLifecycleState.EndPlayed ||
                LifecycleState == ActorLifecycleState.Destroyed ||
                LifecycleState == ActorLifecycleState.Constructed) return;
            foreach (var c in _components.ToArray())
                c.InvokeEndPlay();
            OnEndPlay();
            LifecycleState = ActorLifecycleState.EndPlayed;
        }

        protected virtual void OnBeginPlay() { }
        protected virtual void OnTick(float deltaTime) { }
        protected virtual void OnEndPlay() { }
    }

    /// <summary>
    /// Actor 的组件基类。区别于 ECS 里轻量 IComponent，ActorComponent 是有状态、可 Tick 的 OOP 组件。
    /// </summary>
    public abstract class ActorComponent
    {
        public Guid Id { get; } = Guid.NewGuid();
        public string Name { get; set; } = "ActorComponent";
        public bool TickEnabled { get; set; } = true;
        public Actor? Owner { get; internal set; }
        public bool HasBegunPlay { get; private set; }

        internal void InvokeBeginPlay()
        {
            if (HasBegunPlay) return;
            HasBegunPlay = true;
            OnBeginPlay();
        }

        internal void InvokeTick(float deltaTime) => OnTick(deltaTime);

        internal void InvokeEndPlay()
        {
            if (!HasBegunPlay) return;
            OnEndPlay();
            HasBegunPlay = false;
        }

        protected virtual void OnBeginPlay() { }
        protected virtual void OnTick(float deltaTime) { }
        protected virtual void OnEndPlay() { }
    }

    /// <summary>
    /// 一个把场景物件（SceneObject）和渲染数据接入 Gameplay 的桥组件。
    /// </summary>
    public sealed class SceneObjectComponent : ActorComponent
    {
        public SceneObject? Scene { get; set; }

        public void SyncFromOwner()
        {
            if (Owner == null || Scene == null) return;
            Scene.Position = Owner.Position;
            Scene.Rotation = Owner.Rotation;
            Scene.Scale = Owner.Scale;
        }

        protected override void OnTick(float deltaTime) => SyncFromOwner();
    }

    /// <summary>
    /// 一个可被 Controller 占据并接收输入的 Actor。
    /// </summary>
    public class Pawn : Actor
    {
        public Controller? CurrentController { get; internal set; }
        public Vector3 PendingMovementInput { get; private set; } = Vector3.Zero;
        public float MoveSpeed { get; set; } = 5f;

        public void AddMovementInput(Vector3 worldDirection, float scale = 1f)
        {
            if (worldDirection.LengthSquared() <= 0f || scale == 0f) return;
            var normalized = Vector3.Normalize(worldDirection) * scale;
            PendingMovementInput += normalized;
        }

        public Vector3 ConsumeMovementInput()
        {
            var v = PendingMovementInput;
            PendingMovementInput = Vector3.Zero;
            return v;
        }

        protected override void OnTick(float deltaTime)
        {
            var input = ConsumeMovementInput();
            if (input.LengthSquared() > 0f)
            {
                if (input.LengthSquared() > 1f) input = Vector3.Normalize(input);
                Position += input * MoveSpeed * deltaTime;
            }
        }
    }

    /// <summary>
    /// Controller —— 决定一个 Pawn 接受什么输入/AI 决策。是"灵魂"，Pawn 是"躯体"。
    /// </summary>
    public abstract class Controller : Actor
    {
        public Pawn? PossessedPawn { get; private set; }

        public void Possess(Pawn pawn)
        {
            if (pawn == null) throw new ArgumentNullException(nameof(pawn));
            UnPossess();
            PossessedPawn = pawn;
            pawn.CurrentController = this;
            OnPossess(pawn);
        }

        public void UnPossess()
        {
            if (PossessedPawn == null) return;
            var prev = PossessedPawn;
            PossessedPawn.CurrentController = null;
            PossessedPawn = null;
            OnUnPossess(prev);
        }

        protected virtual void OnPossess(Pawn pawn) { }
        protected virtual void OnUnPossess(Pawn pawn) { }
    }

    /// <summary>
    /// 由本地玩家操控的 Controller。负责将 InputCommand 翻译成 Pawn 的运动输入。
    /// 占据（Possess）一个 Pawn 后，收到的输入命令会被转换成该 Pawn 的运动输入。
    /// </summary>
    public class PlayerController : Controller
    {
        /// <summary>
        /// 处理一个玩家输入命令，并将其翻译为被占据 Pawn 的运动输入。
        /// </summary>
        public virtual void HandleInput(PlayerInputCommand command)
        {
            if (PossessedPawn == null) return;
            switch (command.Kind)
            {
                case PlayerInputKind.MoveRight:
                    PossessedPawn.AddMovementInput(new Vector3(1f, 0f, 0f), command.Scale);
                    break;
                case PlayerInputKind.MoveForward:
                    PossessedPawn.AddMovementInput(new Vector3(0f, 0f, 1f), command.Scale);
                    break;
                case PlayerInputKind.MoveUp:
                    PossessedPawn.AddMovementInput(new Vector3(0f, 1f, 0f), command.Scale);
                    break;
            }
        }
    }

    public enum PlayerInputKind
    {
        MoveForward,
        MoveRight,
        MoveUp,
        Fire,
        Jump,
        Custom
    }

    public readonly struct PlayerInputCommand
    {
        public PlayerInputKind Kind { get; }
        public float Scale { get; }
        public string CustomName { get; }

        public PlayerInputCommand(PlayerInputKind kind, float scale = 1f, string customName = "")
        {
            Kind = kind;
            Scale = scale;
            CustomName = customName ?? string.Empty;
        }
    }

    /// <summary>
    /// 决定关卡的玩法规则：默认 Pawn 类、Controller 类、得分、胜利条件等。
    /// </summary>
    public class GameMode
    {
        /// <summary>玩法规则名称。</summary>
        public string Name { get; set; } = "GameMode";

        /// <summary>默认 Pawn 工厂，用于在 Start 时为本地玩家生成默认 Pawn。</summary>
        public Func<Pawn>? DefaultPawnFactory { get; set; }

        /// <summary>当前玩家得分。</summary>
        public int PlayerScore { get; private set; }

        /// <summary>比赛是否已结束。</summary>
        public bool MatchHasEnded { get; private set; }

        /// <summary>比赛结束原因。</summary>
        public string EndReason { get; private set; } = string.Empty;

        /// <summary>增加玩家得分（比赛已结束时忽略）。</summary>
        public virtual void AddScore(int amount)
        {
            if (MatchHasEnded || amount <= 0) return;
            PlayerScore += amount;
        }

        /// <summary>结束比赛并记录结束原因。</summary>
        public virtual void EndMatch(string reason)
        {
            MatchHasEnded = true;
            EndReason = reason ?? string.Empty;
        }

        /// <summary>为所在世界生成本地玩家控制器。</summary>
        public virtual PlayerController SpawnPlayerController(GameplayWorld world)
        {
            return new PlayerController { Name = "LocalPlayer" };
        }

        /// <summary>为指定控制器生成并占据默认 Pawn。</summary>
        public virtual void SpawnDefaultPawnFor(PlayerController controller, GameplayWorld world)
        {
            if (controller == null) return;
            var pawn = DefaultPawnFactory?.Invoke()
                       ?? new Pawn { Name = "DefaultPawn", MoveSpeed = 5f };
            // 生成的默认 Pawn 必须注册进世界，否则不会被 GameplayWorld.Tick 遍历，
            // OnTick 永不执行，移动输入不生效（TestGameplayWorldSpawnsDefaultPlayer... 失败）。
            world.RegisterActor(pawn);
            controller.Possess(pawn);
        }
    }

    /// <summary>
    /// 玩法世界 —— 管理所有 Actor 的生命周期、Tick 顺序与 GameMode。
    /// 它不是 RuntimeWorld（后者是渲染快照容器），而是 OOP 玩法层。
    /// </summary>
    public sealed class GameplayWorld
    {
        private readonly List<Actor> _actors = new();
        private readonly List<Actor> _pendingAdd = new();
        private readonly List<Actor> _pendingRemove = new();
        private bool _started;

        public GameMode GameMode { get; }
        public PlayerController? LocalPlayerController { get; private set; }
        public Pawn? LocalPlayerPawn => LocalPlayerController?.PossessedPawn;
        public IReadOnlyList<Actor> Actors => _actors;
        public float TimeSeconds { get; private set; }

        public GameplayWorld(GameMode? gameMode = null)
        {
            GameMode = gameMode ?? new GameMode();
        }

        public T RegisterActor<T>(T actor) where T : Actor
        {
            if (actor == null) throw new ArgumentNullException(nameof(actor));
            if (actor.World != null && actor.World != this)
                throw new InvalidOperationException("Actor already registered in another world.");
            actor.World = this;
            if (_started)
                _pendingAdd.Add(actor);
            else
                _actors.Add(actor);
            return actor;
        }

        public bool DestroyActor(Actor actor)
        {
            if (actor == null) return false;
            if (!_actors.Contains(actor) && !_pendingAdd.Contains(actor)) return false;
            _pendingRemove.Add(actor);
            return true;
        }

        public void Start()
        {
            if (_started) return;
            _started = true;
            LocalPlayerController = GameMode.SpawnPlayerController(this);
            GameMode.SpawnDefaultPawnFor(LocalPlayerController, this);
            FlushPending();
            foreach (var a in _actors.ToArray())
                a.InvokeBeginPlay();
        }

        public void Tick(float deltaTime)
        {
            if (!_started) Start();
            FlushPending();
            TimeSeconds += MathF.Max(0f, deltaTime);
            foreach (var a in _actors.ToArray())
                a.InvokeTick(deltaTime);
            FlushPending();
        }

        public void Shutdown()
        {
            foreach (var a in _actors.ToArray())
            {
                a.InvokeEndPlay();
            }
            _actors.Clear();
            _pendingAdd.Clear();
            _pendingRemove.Clear();
            _started = false;
        }

        public T? FindActor<T>() where T : Actor
        {
            foreach (var a in _actors) if (a is T t) return t;
            return null;
        }

        public IEnumerable<T> FindActors<T>() where T : Actor
        {
            foreach (var a in _actors) if (a is T t) yield return t;
        }

        public void DispatchInput(PlayerInputCommand command)
        {
            LocalPlayerController?.HandleInput(command);
        }

        private void FlushPending()
        {
            if (_pendingAdd.Count > 0)
            {
                foreach (var a in _pendingAdd)
                {
                    _actors.Add(a);
                    if (_started) a.InvokeBeginPlay();
                }
                _pendingAdd.Clear();
            }
            if (_pendingRemove.Count > 0)
            {
                foreach (var a in _pendingRemove)
                {
                    if (_actors.Remove(a))
                    {
                        a.InvokeEndPlay();
                        a.World = null;
                    }
                    else
                    {
                        _pendingAdd.Remove(a);
                    }
                }
                _pendingRemove.Clear();
            }
        }
    }
}
