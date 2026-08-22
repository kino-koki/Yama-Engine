using System;
using System.Collections.Generic;
using System.Numerics;
using ShanyuEngine.ECS;
using ShanyuEngine.Gameplay;

namespace ShanyuEngine.GameFramework
{
    // ════════════════════════════════════════════════════════════════════════
    // 山雨引擎 - Actor 基类 (GameFramework)
    //
    // 类似 UE Actor / Unity GameObject:
    //   * 生命周期: BeginPlay -> Tick(dt) -> EndPlay
    //   * 变换组件 RootComponent (SceneComponent 层级)
    //   * 标签系统 ActorTags
    //   * 父级关系 AttachToActor / DetachFromActor
    //   * 生成/销毁 SpawnActor<T> / DestroyActor
    //   * 网络复制标记 ReplicatedProperties / IsReplicated
    //   * 编辑器支持 OnEditorSelected / OnEditorTransformChanged
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>Actor 生命周期状态。</summary>
    public enum ActorLifecyclePhase
    {
        Constructed,
        Initialized,
        BeginPlayed,
        Ticking,
        EndPlayed,
        Destroyed
    }

    /// <summary>
    /// 所有运行时游戏对象的基类。提供 Tick、生命周期、组件聚合、变换层级与网络复制。
    /// </summary>
    public class Actor
    {
        // ── 标识 ──────────────────────────────────────────────────────────

        /// <summary>全局唯一标识。</summary>
        public Guid Id { get; } = Guid.NewGuid();

        /// <summary>显示名称。</summary>
        public string Name { get; set; } = "Actor";

        /// <summary>标签集合 (类似 Unity Tag / UE Tag)。</summary>
        public HashSet<string> ActorTags { get; set; } = new HashSet<string>();

        // ── 变换 ──────────────────────────────────────────────────────────

        /// <summary>世界位置。</summary>
        public Vector3 Position
        {
            get => _position;
            set { _position = value; OnTransformChanged(); }
        }

        /// <summary>世界旋转 (四元数)。</summary>
        public Quaternion Rotation
        {
            get => _rotation;
            set { _rotation = value; OnTransformChanged(); }
        }

        /// <summary>世界缩放。</summary>
        public Vector3 Scale
        {
            get => _scale;
            set { _scale = value; OnTransformChanged(); }
        }

        private Vector3 _position = Vector3.Zero;
        private Quaternion _rotation = Quaternion.Identity;
        private Vector3 _scale = Vector3.One;

        /// <summary>根组件 (SceneComponent 层级根)。</summary>
        public SceneComponent RootComponent { get; set; }

        // ── 生命周期 ──────────────────────────────────────────────────────

        /// <summary>当前生命周期阶段。</summary>
        public ActorLifecyclePhase LifecyclePhase { get; private set; } = ActorLifecyclePhase.Constructed;

        /// <summary>是否已开始运行 (BeginPlay 已调用)。</summary>
        public bool HasBegunPlay => LifecyclePhase >= ActorLifecyclePhase.BeginPlayed;

        /// <summary>是否启用 Tick。</summary>
        public bool TickEnabled { get; set; } = true;

        /// <summary>Tick 组 (用于控制更新顺序)。</summary>
        public int TickGroup { get; set; }

        /// <summary>所属世界引用。</summary>
        public GameplayWorld World { get; internal set; }

        // ── 父级关系 ──────────────────────────────────────────────────────

        /// <summary>父 Actor。</summary>
        public Actor AttachParent { get; private set; }

        /// <summary>子 Actor 列表。</summary>
        public readonly List<Actor> AttachedChildren = new List<Actor>();

        // ── 网络复制 ──────────────────────────────────────────────────────

        /// <summary>是否启用网络复制。</summary>
        public bool IsReplicated { get; set; }

        /// <summary>需要复制的属性字典 (属性名 -> 当前值字符串)。</summary>
        public Dictionary<string, string> ReplicatedProperties { get; set; } = new Dictionary<string, string>();

        // ── 组件集合 ──────────────────────────────────────────────────────

        private readonly List<ActorComponent> _components = new List<ActorComponent>();
        private readonly List<ActorComponent> _tickCache = new List<ActorComponent>();

        /// <summary>只读组件列表。</summary>
        public IReadOnlyList<ActorComponent> Components => _components;

        // ── 事件 ──────────────────────────────────────────────────────────

        /// <summary>变换变化事件 (位置/旋转/缩放改变时触发)。</summary>
        public event Action<Actor> OnTransformChangedEvent;

        /// <summary>销毁前事件。</summary>
        public event Action<Actor> OnDestroying;

        // ── 变换 ──────────────────────────────────────────────────────────

        /// <summary>计算世界矩阵 (SRT)。</summary>
        public Matrix4x4 GetWorldMatrix()
        {
            return Matrix4x4.CreateScale(_scale) *
                   Matrix4x4.CreateFromQuaternion(_rotation) *
                   Matrix4x4.CreateTranslation(_position);
        }

        /// <summary>获取前向向量。</summary>
        public Vector3 GetForward() => Vector3.Transform(new Vector3(0, 0, -1), _rotation);

        /// <summary>获取右向向量。</summary>
        public Vector3 GetRight() => Vector3.Transform(new Vector3(1, 0, 0), _rotation);

        /// <summary>获取上向向量。</summary>
        public Vector3 GetUp() => Vector3.Transform(new Vector3(0, 1, 0), _rotation);

        /// <summary>获取世界位置 (如果有根组件则同步)。</summary>
        public Vector3 GetActorLocation() => RootComponent != null ? RootComponent.WorldPosition : _position;

        /// <summary>设置世界位置。</summary>
        public void SetActorLocation(Vector3 newLocation)
        {
            if (RootComponent != null)
                RootComponent.SetWorldPosition(newLocation);
            else
                Position = newLocation;
        }

        /// <summary>设置世界旋转。</summary>
        public void SetActorRotation(Quaternion newRotation)
        {
            if (RootComponent != null)
                RootComponent.SetWorldRotation(newRotation);
            else
                Rotation = newRotation;
        }

        /// <summary>设置世界缩放。</summary>
        public void SetActorScale(Vector3 newScale)
        {
            if (RootComponent != null)
                RootComponent.SetWorldScale(newScale);
            else
                Scale = newScale;
        }

        // ── 标签 ──────────────────────────────────────────────────────────

        /// <summary>判断是否拥有指定标签。</summary>
        public bool HasTag(string tag) => ActorTags.Contains(tag);

        /// <summary>添加标签。</summary>
        public void AddTag(string tag) => ActorTags.Add(tag);

        /// <summary>移除标签。</summary>
        public void RemoveTag(string tag) => ActorTags.Remove(tag);

        // ── 父级关系 ──────────────────────────────────────────────────────

        /// <summary>挂载到父 Actor (类似 UE AttachToActor)。</summary>
        public void AttachToActor(Actor parent, string socketName = null)
        {
            if (parent == null) throw new ArgumentNullException(nameof(parent));
            if (parent == this) throw new InvalidOperationException("Cannot attach to self.");

            // 从原父级分离
            DetachFromActor();

            AttachParent = parent;
            parent.AttachedChildren.Add(this);
            OnAttached(parent, socketName);
        }

        /// <summary>从父 Actor 分离。</summary>
        public void DetachFromActor()
        {
            if (AttachParent == null) return;
            var prev = AttachParent;
            AttachParent.AttachedChildren.Remove(this);
            AttachParent = null;
            OnDetached(prev);
        }

        /// <summary>挂载时回调。</summary>
        protected virtual void OnAttached(Actor parent, string socketName) { }

        /// <summary>分离时回调。</summary>
        protected virtual void OnDetached(Actor previousParent) { }

        // ── 组件管理 ──────────────────────────────────────────────────────

        /// <summary>添加组件 (类似 Unity AddComponent&lt;T&gt;)。</summary>
        public T AddComponent<T>() where T : ActorComponent, new()
        {
            var component = new T();
            return (T)AttachComponent(component);
        }

        /// <summary>附加已创建的组件。</summary>
        public ActorComponent AttachComponent(ActorComponent component)
        {
            if (component == null) throw new ArgumentNullException(nameof(component));
            if (component.Owner != null && component.Owner != this)
                throw new InvalidOperationException("Component already belongs to another Actor.");

            component.Owner = this;
            _components.Add(component);

            if (LifecyclePhase >= ActorLifecyclePhase.Initialized)
                component.InitializeComponent(this);

            if (LifecyclePhase >= ActorLifecyclePhase.BeginPlayed)
                component.BeginPlayComponent();

            return component;
        }

        /// <summary>获取首个指定类型的组件。</summary>
        public T GetComponent<T>() where T : ActorComponent
        {
            foreach (var c in _components)
                if (c is T typed) return typed;
            return null;
        }

        /// <summary>获取所有指定类型的组件。</summary>
        public T[] GetComponents<T>() where T : ActorComponent
        {
            var results = new List<T>();
            foreach (var c in _components)
                if (c is T typed) results.Add(typed);
            return results.ToArray();
        }

        /// <summary>移除组件。</summary>
        public bool RemoveComponent(ActorComponent component)
        {
            if (component == null || component.Owner != this) return false;
            if (_components.Remove(component))
            {
                if (LifecyclePhase >= ActorLifecyclePhase.EndPlayed)
                    component.EndPlayComponent();
                component.UninitializeComponent();
                component.Owner = null;
                return true;
            }
            return false;
        }

        /// <summary>按类型移除组件。</summary>
        public bool RemoveComponent<T>() where T : ActorComponent
        {
            var comp = GetComponent<T>();
            if (comp != null) return RemoveComponent(comp);
            return false;
        }

        // ── 生命周期 ──────────────────────────────────────────────────────

        /// <summary>初始化 (由 GameplayWorld 在注册时调用)。</summary>
        internal void InitializeActor()
        {
            if (LifecyclePhase != ActorLifecyclePhase.Constructed) return;
            LifecyclePhase = ActorLifecyclePhase.Initialized;

            // 初始化所有组件
            foreach (var c in _components)
                c.InitializeComponent(this);

            OnInitialize();
        }

        /// <summary>调用 BeginPlay (由 GameplayWorld 调用)。</summary>
        internal void BeginPlayActor()
        {
            if (LifecyclePhase != ActorLifecyclePhase.Initialized &&
                LifecyclePhase != ActorLifecyclePhase.Constructed) return;

            LifecyclePhase = ActorLifecyclePhase.BeginPlayed;

            foreach (var c in _components.ToArray())
                c.BeginPlayComponent();

            OnBeginPlay();
            LifecyclePhase = ActorLifecyclePhase.Ticking;
        }

        /// <summary>每帧 Tick (由 GameplayWorld 调用)。</summary>
        internal void TickActor(float deltaTime)
        {
            if (LifecyclePhase != ActorLifecyclePhase.Ticking || !TickEnabled) return;

            OnTick(deltaTime);

            _tickCache.Clear();
            _tickCache.AddRange(_components);
            foreach (var c in _tickCache)
            {
                if (c.IsActive && c.TickEnabled)
                    c.TickComponent(deltaTime);
            }
        }

        /// <summary>调用 EndPlay (由 GameplayWorld 调用)。</summary>
        internal void EndPlayActor()
        {
            if (LifecyclePhase == ActorLifecyclePhase.EndPlayed ||
                LifecyclePhase == ActorLifecyclePhase.Destroyed) return;

            foreach (var c in _components.ToArray())
                c.EndPlayComponent();

            OnEndPlay();
            LifecyclePhase = ActorLifecyclePhase.EndPlayed;
        }

        /// <summary>销毁 Actor。</summary>
        internal void DestroyActorInternal()
        {
            OnDestroying?.Invoke(this);
            EndPlayActor();

            foreach (var c in _components.ToArray())
                c.UninitializeComponent();

            LifecyclePhase = ActorLifecyclePhase.Destroyed;
            World = null;
        }

        // ── 虚方法 ────────────────────────────────────────────────────────

        /// <summary>初始化时调用 (组件初始化之后)。</summary>
        protected virtual void OnInitialize() { }

        /// <summary>游戏开始时调用。</summary>
        protected virtual void OnBeginPlay() { }

        /// <summary>每帧调用。</summary>
        protected virtual void OnTick(float deltaTime) { }

        /// <summary>游戏结束时调用。</summary>
        protected virtual void OnEndPlay() { }

        /// <summary>变换变化时调用。</summary>
        protected virtual void OnTransformChanged()
        {
            OnTransformChangedEvent?.Invoke(this);
        }

        // ── 编辑器支持 ────────────────────────────────────────────────────

        /// <summary>编辑器选中时调用。</summary>
        public virtual void OnEditorSelected() { }

        /// <summary>编辑器变换变化时调用。</summary>
        public virtual void OnEditorTransformChanged() { }

        // ── 工具 ──────────────────────────────────────────────────────────

        public override string ToString() => $"{Name} ({GetType().Name})";
    }
}
