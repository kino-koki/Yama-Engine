using System;
using System.Numerics;

namespace ShanyuEngine.GameFramework
{
    // ════════════════════════════════════════════════════════════════════════
    // 山雨引擎 - ActorComponent 基类 (GameFramework)
    //
    // 类似 UE UActorComponent / Unity MonoBehaviour:
    //   * 拥有者 Owner / GetOwner<T>
    //   * 生命周期: Initialize -> BeginPlay -> TickComponent(dt) -> Uninitialize
    //   * 启用/禁用 IsActive / SetActive
    //   * 世界坐标辅助 GetWorldLocation / GetWorldRotation / GetWorldScale
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Actor 组件的基类。有状态、可 Tick 的 OOP 组件，
    /// 区别于 ECS 中的轻量 IComponent 结构体。
    /// </summary>
    public abstract class ActorComponent
    {
        // ── 标识 ──────────────────────────────────────────────────────────

        /// <summary>全局唯一标识。</summary>
        public Guid Id { get; } = Guid.NewGuid();

        /// <summary>组件显示名称。</summary>
        public string Name { get; set; } = "ActorComponent";

        // ── 拥有者 ──────────────────────────────────────────────────────

        /// <summary>所属 Actor。</summary>
        public Actor Owner { get; internal set; }

        /// <summary>获取拥有者并转为指定类型。</summary>
        public T GetOwner<T>() where T : Actor => Owner as T;

        // ── 启用/禁用 ──────────────────────────────────────────────────────

        /// <summary>是否已激活。</summary>
        public bool IsActive { get; private set; } = true;

        /// <summary>设置启用/禁用状态。</summary>
        public void SetActive(bool active)
        {
            if (IsActive == active) return;
            IsActive = active;
            if (active)
                OnEnabled();
            else
                OnDisabled();
        }

        // ── Tick ──────────────────────────────────────────────────────────

        /// <summary>是否启用 Tick。</summary>
        public bool TickEnabled { get; set; } = true;

        // ── 内部生命周期标记 ──────────────────────────────────────────────

        /// <summary>是否已初始化。</summary>
        public bool HasBeenInitialized { get; private set; }

        /// <summary>是否已调用 BeginPlay。</summary>
        public bool HasBegunPlay { get; private set; }

        // ── 生命周期 ──────────────────────────────────────────────────────

        /// <summary>内部: 初始化组件。</summary>
        internal void InitializeComponent(Actor owner)
        {
            if (HasBeenInitialized) return;
            Owner = owner;
            HasBeenInitialized = true;
            OnInitialize();
        }

        /// <summary>内部: 调用 BeginPlay。</summary>
        internal void BeginPlayComponent()
        {
            if (HasBegunPlay) return;
            HasBegunPlay = true;
            OnBeginPlay();
        }

        /// <summary>内部: 每帧 Tick。</summary>
        internal void TickComponent(float deltaTime)
        {
            if (!IsActive || !TickEnabled) return;
            OnTick(deltaTime);
        }

        /// <summary>内部: 调用 EndPlay。</summary>
        internal void EndPlayComponent()
        {
            if (!HasBegunPlay) return;
            OnEndPlay();
            HasBegunPlay = false;
        }

        /// <summary>内部: 反初始化。</summary>
        internal void UninitializeComponent()
        {
            if (!HasBeenInitialized) return;
            OnUninitialize();
            HasBeenInitialized = false;
        }

        // ── 虚方法 ────────────────────────────────────────────────────────

        /// <summary>初始化时调用。</summary>
        protected virtual void OnInitialize() { }

        /// <summary>游戏开始时调用。</summary>
        protected virtual void OnBeginPlay() { }

        /// <summary>每帧调用。</summary>
        protected virtual void OnTick(float deltaTime) { }

        /// <summary>游戏结束/组件移除时调用。</summary>
        protected virtual void OnEndPlay() { }

        /// <summary>反初始化时调用。</summary>
        protected virtual void OnUninitialize() { }

        /// <summary>启用时调用。</summary>
        protected virtual void OnEnabled() { }

        /// <summary>禁用时调用。</summary>
        protected virtual void OnDisabled() { }

        // ── 世界坐标辅助 ──────────────────────────────────────────────────

        /// <summary>获取世界位置 (从拥有者 Actor)。</summary>
        public Vector3 GetWorldLocation() => Owner?.GetActorLocation() ?? Vector3.Zero;

        /// <summary>获取世界旋转 (从拥有者 Actor)。</summary>
        public Quaternion GetWorldRotation() => Owner?.Rotation ?? Quaternion.Identity;

        /// <summary>获取世界缩放 (从拥有者 Actor)。</summary>
        public Vector3 GetWorldScale() => Owner?.Scale ?? Vector3.One;

        // ── 工具 ──────────────────────────────────────────────────────────

        public override string ToString() => $"{Name} ({GetType().Name})";
    }
}
