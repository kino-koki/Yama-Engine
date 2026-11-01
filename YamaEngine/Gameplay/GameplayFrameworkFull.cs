// ============================================================================
// yama引擎 - GameplayFrameworkFull.cs
//
// UE6 级别完整 Gameplay 框架实现
// 命名空间: YamaEngine.Gameplay.Full
//
// 本文件提供一套完整、独立、可编译的 Gameplay 框架，包含:
//   1. GActor           — 基础实体（层级管理、生命周期、组件管理）
//   2. GActorComponent  — 组件基类（注册/注销、Tick）
//   3. GPawn            — 可控制实体（Possess/Unpossess、移动/碰撞组件）
//   4. GPlayerController— 玩家控制器（Possess/Unpossess、Input绑定、RPC调用）
//   5. GGameState       — 游戏状态同步（ReplicatedState、Scores、MatchTime、Players）
//   6. GPlayerState     — 玩家状态（Score/Team/Deaths/Kills/Ping）
//   7. GGameMode        — 游戏规则管理（匹配状态、默认类引用、得分、流程控制）
//   8. DamageSystem     — 伤害系统（DamageType/DamageEvent/HealthComponent/Armor）
//
// 设计说明:
//   - 所有公开类型以 G 前缀避免与 GameplayFramework.cs 中的同名类型冲突
//   - 命名空间 YamaEngine.Gameplay.Full 完全隔离，不会产生重复定义
//   - 可通过桥接适配器与现有 Gameplay 框架对接
//   - 网络模型基于 Server-Client 架构，支持 Replicated 标记
// ============================================================================

using System;
using System.Collections.Generic;
using System.Numerics;

namespace YamaEngine.Gameplay.Full
{
    // ══════════════════════════════════════════════════════════════════════════
    // 第一部分: 枚举与基础数据结构
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>GActor 生命周期状态。</summary>
    public enum GLifecyclePhase
    {
        /// <summary>已构造，尚未初始化。</summary>
        Constructed,
        /// <summary>已初始化，尚未 BeginPlay。</summary>
        Initialized,
        /// <summary>BeginPlay 已调用。</summary>
        BeginPlayed,
        /// <summary>正在 Tick。</summary>
        Ticking,
        /// <summary>EndPlay 已调用。</summary>
        EndPlayed,
        /// <summary>已销毁。</summary>
        Destroyed
    }

    /// <summary>匹配状态 — UE6 完整状态机。</summary>
    public enum GMatchState
    {
        /// <summary>等待玩家进入。</summary>
        WaitingToStart,
        /// <summary> countdown 倒计时阶段。</summary>
        Countdown,
        /// <summary>比赛进行中。</summary>
        InProgress,
        /// <summary>比赛暂停。</summary>
        Paused,
        /// <summary>比赛已结束，等待结算。</summary>
        Ended,
        /// <summary>等待下一轮比赛。</summary>
        WaitingForNextRound,
        /// <summary>比赛完全终止。</summary>
        Aborted
    }

    /// <summary>移动模式。</summary>
    public enum GMovementMode
    {
        None,
        Walking,
        Running,
        Falling,
        Flying,
        Swimming,
        Crawling,
        Climbing
    }

    /// <summary>网络角色 — 用于判定 Replicated 行为。</summary>
    public enum GNetRole
    {
        /// <summary>无网络权限。</summary>
        None,
        /// <summary>权威端 (服务器)。</summary>
        Authority,
        /// <summary>自主代理 (本地玩家)。</summary>
        AutonomousProxy,
        /// <summary>模拟代理 (远程玩家)。</summary>
        SimulatedProxy
    }

    /// <summary>伤害类型枚举。</summary>
    public enum GDamageType
    {
        /// <summary>通用物理伤害。</summary>
        Physical,
        /// <summary>爆炸伤害（含半径衰减）。</summary>
        Explosive,
        /// <summary>火焰伤害。</summary>
        Fire,
        /// <summary>电击伤害。</summary>
        Electric,
        /// <summary>化学/毒素伤害。</summary>
        Chemical,
        /// <summary>辐射伤害。</summary>
        Radiation,
        /// <summary>精神/心理伤害。</summary>
        Psychic,
        /// <summary>真实伤害（无视护甲）。</summary>
        TrueDamage,
        /// <summary>坠落伤害。</summary>
        Fall,
        /// <summary>环境伤害（溺水、高温等）。</summary>
        Environmental,
        /// <summary>治疗效果（负伤害）。</summary>
        Heal
    }

    /// <summary>碰撞响应类型。</summary>
    public enum GCollisionResponse
    {
        Block,
        Overlap,
        Ignore
    }

    /// <summary>碰撞通道。</summary>
    public enum GCollisionChannel
    {
        WorldStatic,
        WorldDynamic,
        GPawnChannel,
        PhysicsBody,
        Vehicle,
        Visibility,
        Camera,
        Projectile,
        UI
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 第二部分: 事件与委托
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>MatchState 变化委托。</summary>
    public delegate void MatchStateChangedDelegate(GMatchState oldState, GMatchState newState);

    /// <summary>伤害事件委托。</summary>
    public delegate void DamageTakenDelegate(float damage, GDamageType type, GActor? instigator);

    /// <summary>死亡事件委托。</summary>
    public delegate void DeathDelegate(GActor? killer, GActor? victim);

    /// <summary>玩家加入/离开委托。</summary>
    public delegate void PlayerJoinedDelegate(GPlayerController player);
    public delegate void PlayerLeftDelegate(GPlayerController player);

    /// <summary>RPC 调用模式。</summary>
    public enum GRpcMode
    {
        /// <summary>服务器执行。</summary>
        Server,
        /// <summary>客户端执行（广播所有客户端）。</summary>
        Client,
        /// <summary>仅拥有该 Actor 的客户端执行。</summary>
        OwnerClient,
        /// <summary>多播（服务器+所有客户端）。</summary>
        Multicast
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 第三部分: GActorComponent — 组件基类
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// GActorComponent — 组件基类。
    /// 提供 Register/Unregister 生命周期、Tick 能力、Owner 引用与网络复制标记。
    /// </summary>
    public class GActorComponent
    {
        /// <summary>组件唯一标识。</summary>
        public Guid Id { get; } = Guid.NewGuid();

        /// <summary>组件显示名称。</summary>
        public string ComponentName { get; set; } = "GActorComponent";

        /// <summary>所属 Actor。</summary>
        public GActor? Owner { get; internal set; }

        /// <summary>是否允许 Tick。</summary>
        public bool ComponentTickEnabled { get; set; } = true;

        /// <summary>Tick 优先级（数值越小越先执行）。</summary>
        public int TickPriority { get; set; } = 0;

        /// <summary>是否已注册到 Owner。</summary>
        public bool IsRegistered { get; private set; }

        /// <summary>是否已 BeginPlay。</summary>
        public bool HasBegunPlay { get; private set; }

        /// <summary>是否需要网络复制。</summary>
        public bool IsReplicated { get; set; } = false;

        /// <summary>网络角色（从 Owner 继承）。</summary>
        public GNetRole NetRole => Owner?.NetRole ?? GNetRole.None;

        // ── 注册/注销 ────────────────────────────────────────────────

        /// <summary>注册此组件到 Owner。由 Owner.AddComponent 自动调用。</summary>
        public virtual void RegisterComponent()
        {
            if (IsRegistered) return;
            IsRegistered = true;
            OnRegister();
        }

        /// <summary>从 Owner 注销此组件。由 Owner.RemoveComponent 自动调用。</summary>
        public virtual void UnregisterComponent()
        {
            if (!IsRegistered) return;
            IsRegistered = false;
            OnUnregister();
        }

        /// <summary>销毁组件。</summary>
        public virtual void DestroyComponent()
        {
            if (HasBegunPlay)
                InvokeEndPlay();
            UnregisterComponent();
            Owner = null;
        }

        // ── 生命周期 (由 GActor 驱动) ────────────────────────────────

        internal void InvokeBeginPlay()
        {
            if (HasBegunPlay) return;
            HasBegunPlay = true;
            OnBeginPlay();
        }

        internal void InvokeTick(float deltaTime)
        {
            if (!ComponentTickEnabled || !HasBegunPlay) return;
            OnTick(deltaTime);
        }

        internal void InvokeEndPlay()
        {
            if (!HasBegunPlay) return;
            OnEndPlay();
            HasBegunPlay = false;
        }

        // ── 虚方法 (子类覆写) ────────────────────────────────────────

        protected virtual void OnRegister() { }
        protected virtual void OnUnregister() { }
        protected virtual void OnBeginPlay() { }
        protected virtual void OnTick(float deltaTime) { }
        protected virtual void OnEndPlay() { }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 第四部分: GMovementComponent — 移动组件
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// GMovementComponent — 移动组件。
    /// 封装移动逻辑：速度、加速度、最大速度、重力、移动模式切换。
    /// </summary>
    public class GMovementComponent : GActorComponent
    {
        /// <summary>当前移动模式。</summary>
        public GMovementMode MovementMode { get; set; } = GMovementMode.Walking;

        /// <summary>移动速度（单位/秒）。</summary>
        public float MoveSpeed { get; set; } = 600f;

        /// <summary>最大移动速度。</summary>
        public float MaxMoveSpeed { get; set; } = 1200f;

        /// <summary>加速度。</summary>
        public float Acceleration { get; set; } = 2048f;

        /// <summary>减速度。</summary>
        public float Deceleration { get; set; } = 2048f;

        /// <summary>重力强度 (0 = 无重力)。</summary>
        public float GravityScale { get; set; } = 1f;

        /// <summary>跳跃速度。</summary>
        public float JumpVelocity { get; set; } = 420f;

        /// <summary>当前速度向量。</summary>
        public Vector3 Velocity { get; private set; }

        /// <summary>当前加速度向量。</summary>
        public Vector3 CurrentAcceleration { get; private set; }

        /// <summary>是否在地面。</summary>
        public bool IsGrounded { get; set; } = true;

        /// <summary>是否正在跳跃。</summary>
        public bool IsJumping { get; private set; }

        private Vector3 _pendingInput = Vector3.Zero;

        public GMovementComponent()
        {
            ComponentName = "GMovementComponent";
        }

        /// <summary>添加移动输入（世界方向）。</summary>
        public void AddMovementInput(Vector3 worldDirection, float scale = 1f)
        {
            if (worldDirection.LengthSquared() <= 0f || scale == 0f) return;
            var dir = Vector3.Normalize(worldDirection) * scale;
            _pendingInput += dir;
        }

        /// <summary>请求跳跃。</summary>
        public bool Jump()
        {
            if (!IsGrounded || IsJumping) return false;
            IsJumping = true;
            IsGrounded = false;
            Velocity = new Vector3(Velocity.X, JumpVelocity, Velocity.Z);
            return true;
        }

        protected override void OnTick(float deltaTime)
        {
            if (Owner == null) return;

            // 计算加速度
            if (_pendingInput.LengthSquared() > 0f)
            {
                var desiredDir = Vector3.Normalize(_pendingInput);
                CurrentAcceleration = desiredDir * Acceleration;
                _pendingInput = Vector3.Zero;
            }
            else
            {
                CurrentAcceleration = -Velocity * Deceleration;
            }

            // 更新速度
            Velocity += CurrentAcceleration * deltaTime;

            // 限速
            float horizSpeed = new Vector3(Velocity.X, 0, Velocity.Z).Length();
            if (horizSpeed > MaxMoveSpeed)
            {
                float ratio = MaxMoveSpeed / horizSpeed;
                Velocity = new Vector3(Velocity.X * ratio, Velocity.Y, Velocity.Z * ratio);
            }

            // 重力
            if (!IsGrounded && GravityScale > 0f)
            {
                Velocity += new Vector3(0, -981f * GravityScale * deltaTime, 0);
            }

            // 应用位移
            Owner.Position += Velocity * deltaTime;

            // 地面检测 (简化: Y < 起始高度)
            if (Velocity.Y <= 0f && Owner.Position.Y <= 0f)
            {
                Owner.Position = new Vector3(Owner.Position.X, 0f, Owner.Position.Z);
                Velocity = new Vector3(Velocity.X, 0f, Velocity.Z);
                IsGrounded = true;
                IsJumping = false;
            }
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 第五部分: GCollisionComponent — 碰撞组件
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// GCollisionComponent — 碰撞组件。
    /// 支持球体/胶囊体/AABB 碰撞体、碰撞通道、碰撞响应设置。
    /// </summary>
    public class GCollisionComponent : GActorComponent
    {
        /// <summary>碰撞体类型。</summary>
        public enum GCollisionShape
        {
            Sphere,
            Capsule,
            Box
        }

        /// <summary>碰撞体形状。</summary>
        public GCollisionShape Shape { get; set; } = GCollisionShape.Capsule;

        /// <summary>球体半径（Sphere 用）。</summary>
        public float SphereRadius { get; set; } = 34f;

        /// <summary>胶囊体半径。</summary>
        public float CapsuleRadius { get; set; } = 34f;

        /// <summary>胶囊体半高。</summary>
        public float CapsuleHalfHeight { get; set; } = 90f;

        /// <summary>盒子半尺寸。</summary>
        public Vector3 BoxExtent { get; set; } = new Vector3(34f, 90f, 34f);

        /// <summary>碰撞通道。</summary>
        public GCollisionChannel CollisionChannel { get; set; } = GCollisionChannel.GPawnChannel;

        /// <summary>对 Pawn 的响应。</summary>
        public GCollisionResponse PawnResponse { get; set; } = GCollisionResponse.Block;

        /// <summary>对 WorldStatic 的响应。</summary>
        public GCollisionResponse WorldStaticResponse { get; set; } = GCollisionResponse.Block;

        /// <summary>对 WorldDynamic 的响应。</summary>
        public GCollisionResponse WorldDynamicResponse { get; set; } = GCollisionResponse.Block;

        /// <summary>碰撞发生事件。</summary>
        public event Action<GCollisionComponent, GCollisionComponent, Vector3, Vector3>? OnHit;

        /// <summary>重叠开始事件。</summary>
        public event Action<GCollisionComponent, GCollisionComponent>? OnOverlapBegin;

        /// <summary>重叠结束事件。</summary>
        public event Action<GCollisionComponent, GCollisionComponent>? OnOverlapEnd;

        public GCollisionComponent()
        {
            ComponentName = "GCollisionComponent";
        }

        /// <summary>获取碰撞体世界位置（使用 Owner 变换）。</summary>
        public Vector3 GetWorldCenter()
        {
            var pos = Owner?.Position ?? Vector3.Zero;
            return new Vector3(pos.X, pos.Y + CapsuleHalfHeight, pos.Z);
        }

        /// <summary>简化 AABB 碰撞检测。</summary>
        public bool Intersects(GCollisionComponent other)
        {
            var centerA = GetWorldCenter();
            var centerB = other.GetWorldCenter();
            float dist = Vector3.Distance(centerA, centerB);
            float thresholdA = Shape == GCollisionShape.Sphere ? SphereRadius
                              : Shape == GCollisionShape.Capsule ? CapsuleRadius
                              : BoxExtent.X;
            float thresholdB = other.Shape == GCollisionShape.Sphere ? other.SphereRadius
                              : other.Shape == GCollisionShape.Capsule ? other.CapsuleRadius
                              : other.BoxExtent.X;
            return dist < (thresholdA + thresholdB);
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 第六部分: GHealthComponent — 生命值组件
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// GHealthComponent — 生命值组件。
    /// 管理生命值、护甲、死亡判定，对接 DamageSystem。
    /// </summary>
    public class GHealthComponent : GActorComponent
    {
        /// <summary>最大生命值。</summary>
        public float MaxHealth { get; set; } = 100f;

        /// <summary>当前生命值。</summary>
        public float CurrentHealth { get; private set; }

        /// <summary>最大护甲值。</summary>
        public float MaxArmor { get; set; } = 50f;

        /// <summary>当前护甲值。</summary>
        public float CurrentArmor { get; private set; }

        /// <summary>护甲吸收比例 (0~1)。</summary>
        public float ArmorAbsorptionRatio { get; set; } = 0.6f;

        /// <summary>是否已死亡。</summary>
        public bool IsDead { get; private set; }

        /// <summary>是否无敌。</summary>
        public bool IsInvulnerable { get; set; }

        /// <summary>受伤事件。</summary>
        public event DamageTakenDelegate? OnDamageTaken;

        /// <summary>死亡事件。</summary>
        public event DeathDelegate? OnDeath;

        /// <summary>生命值变化事件。</summary>
        public event Action<float, float>? OnHealthChanged;

        public GHealthComponent()
        {
            ComponentName = "GHealthComponent";
        }

        /// <summary>初始化生命值（BeginPlay 时调用）。</summary>
        protected override void OnBeginPlay()
        {
            CurrentHealth = MaxHealth;
            CurrentArmor = MaxArmor;
        }

        /// <summary>应用伤害。返回实际造成的伤害量。</summary>
        public float ApplyDamage(float rawDamage, GDamageType type, GActor? instigator)
        {
            if (IsDead || IsInvulnerable) return 0f;

            float actualDamage = rawDamage;

            // 真实伤害无视护甲
            if (type != GDamageType.TrueDamage && CurrentArmor > 0f)
            {
                float armorAbsorbed = Math.Min(rawDamage * ArmorAbsorptionRatio, CurrentArmor);
                CurrentArmor -= armorAbsorbed;
                actualDamage = rawDamage - armorAbsorbed;
            }

            // 治疗效果
            if (type == GDamageType.Heal)
            {
                float healAmount = Math.Min(rawDamage, MaxHealth - CurrentHealth);
                CurrentHealth += healAmount;
                OnHealthChanged?.Invoke(CurrentHealth - healAmount, CurrentHealth);
                OnDamageTaken?.Invoke(-healAmount, type, instigator);
                return -healAmount; // 负值表示治疗
            }

            actualDamage = Math.Max(0f, actualDamage);
            float prevHealth = CurrentHealth;
            CurrentHealth = Math.Max(0f, CurrentHealth - actualDamage);

            OnHealthChanged?.Invoke(prevHealth, CurrentHealth);
            OnDamageTaken?.Invoke(actualDamage, type, instigator);

            if (CurrentHealth <= 0f && !IsDead)
            {
                IsDead = true;
                OnDeath?.Invoke(instigator, Owner);
            }

            return actualDamage;
        }

        /// <summary>直接设置生命值（用于初始化或复活）。</summary>
        public void SetHealth(float health, float armor = -1f)
        {
            CurrentHealth = Math.Clamp(health, 0f, MaxHealth);
            if (armor >= 0f)
                CurrentArmor = Math.Clamp(armor, 0f, MaxArmor);
            IsDead = CurrentHealth <= 0f;
        }

        /// <summary>复活（完全恢复）。</summary>
        public void Respawn()
        {
            IsDead = false;
            CurrentHealth = MaxHealth;
            CurrentArmor = MaxArmor;
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 第七部分: GActor — 基础实体
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// GActor — 所有运行时游戏对象的基类。
    /// 提供层级管理（Attach/Detach）、生命周期（BeginPlay/EndPlay/Tick）、
    /// 组件管理、标签系统与网络复制支持。
    /// </summary>
    public class GActor
    {
        private readonly List<GActorComponent> _components = new();
        private readonly List<GActorComponent> _tickScratchList = new();
        private readonly List<GActor> _children = new();
        private GActor? _parent;

        // ── 标识 ──────────────────────────────────────────────────────

        /// <summary>全局唯一标识。</summary>
        public Guid Id { get; } = Guid.NewGuid();

        /// <summary>显示名称。</summary>
        public string ActorName { get; set; } = "GActor";

        /// <summary>标签集合。</summary>
        public HashSet<string> Tags { get; } = new();

        /// <summary>是否拥有指定标签。</summary>
        public bool HasTag(string tag) => Tags.Contains(tag);

        /// <summary>添加标签。</summary>
        public void AddTag(string tag) => Tags.Add(tag);

        /// <summary>移除标签。</summary>
        public bool RemoveTag(string tag) => Tags.Remove(tag);

        // ── 变换 ──────────────────────────────────────────────────────

        private Vector3 _position = Vector3.Zero;
        private Quaternion _rotation = Quaternion.Identity;
        private Vector3 _scale = Vector3.One;

        /// <summary>世界位置。</summary>
        public Vector3 Position
        {
            get => _position;
            set { _position = value; OnTransformChanged(); }
        }

        /// <summary>世界旋转。</summary>
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

        /// <summary>获取世界矩阵。</summary>
        public Matrix4x4 GetWorldMatrix()
        {
            return Matrix4x4.CreateScale(_scale) *
                   Matrix4x4.CreateFromQuaternion(_rotation) *
                   Matrix4x4.CreateTranslation(_position);
        }

        // ── 生命周期 ────────────────────────────────────────────────

        /// <summary>当前生命周期阶段。</summary>
        public GLifecyclePhase LifecyclePhase { get; private set; } = GLifecyclePhase.Constructed;

        /// <summary>是否允许 Tick。</summary>
        public bool TickEnabled { get; set; } = true;

        /// <summary>是否已销毁。</summary>
        public bool IsDestroyed { get; private set; }

        /// <summary>所属 World。</summary>
        public GGameWorld? World { get; internal set; }

        // ── 层级 ──────────────────────────────────────────────────────

        /// <summary>父 Actor。</summary>
        public GActor? Parent => _parent;

        /// <summary>子 Actor 列表。</summary>
        public IReadOnlyList<GActor> Children => _children;

        // ── 组件 ──────────────────────────────────────────────────────

        /// <summary>所有组件。</summary>
        public IReadOnlyList<GActorComponent> Components => _components;

        /// <summary>组件数量。</summary>
        public int ComponentCount => _components.Count;

        // ── 网络 ──────────────────────────────────────────────────────

        /// <summary>网络角色。</summary>
        public GNetRole NetRole { get; set; } = GNetRole.Authority;

        /// <summary>是否需要网络复制。</summary>
        public bool IsReplicated { get; set; } = false;

        /// <summary>远程角色 (由网络系统设置)。</summary>
        public GNetRole RemoteRole { get; set; } = GNetRole.None;

        // ── 初始化 ────────────────────────────────────────────────────

        public GActor()
        {
        }

        /// <summary>构造后初始化（设置属性后再调用）。</summary>
        public virtual void Initialize()
        {
            if (LifecyclePhase != GLifecyclePhase.Constructed) return;
            LifecyclePhase = GLifecyclePhase.Initialized;
            OnInit();
        }

        // ── 组件管理 ──────────────────────────────────────────────────

        /// <summary>添加并注册一个组件。</summary>
        public T AddComponent<T>() where T : GActorComponent, new()
        {
            var comp = new T();
            return AttachComponent(comp);
        }

        /// <summary>附加已有组件并注册。</summary>
        public T AttachComponent<T>(T component) where T : GActorComponent
        {
            if (component == null) throw new ArgumentNullException(nameof(component));
            if (component.Owner != null && component.Owner != this)
                throw new InvalidOperationException($"Component '{component.ComponentName}' is already attached to another actor.");
            component.Owner = this;
            _components.Add(component);
            component.RegisterComponent();

            // 如果 Actor 已经 BeginPlay，立即 BeginPlay 组件
            if (LifecyclePhase == GLifecyclePhase.BeginPlayed || LifecyclePhase == GLifecyclePhase.Ticking)
            {
                component.InvokeBeginPlay();
            }
            return component;
        }

        /// <summary>移除并注销组件。</summary>
        public bool RemoveComponent(GActorComponent component)
        {
            if (component == null) return false;
            if (_components.Remove(component))
            {
                component.InvokeEndPlay();
                component.UnregisterComponent();
                component.Owner = null;
                return true;
            }
            return false;
        }

        /// <summary>按类型查找组件。</summary>
        public T? GetComponent<T>() where T : GActorComponent
        {
            foreach (var c in _components)
                if (c is T t) return t;
            return null;
        }

        /// <summary>按类型查找所有组件。</summary>
        public IEnumerable<T> GetComponents<T>() where T : GActorComponent
        {
            foreach (var c in _components)
                if (c is T t) yield return t;
        }

        /// <summary>是否拥有指定类型的组件。</summary>
        public bool HasComponent<T>() where T : GActorComponent => GetComponent<T>() != null;

        // ── 层级管理 ──────────────────────────────────────────────────

        /// <summary>附加到目标 Actor。</summary>
        public void AttachToActor(GActor? parentActor, string? socketName = null)
        {
            if (_parent == parentActor) return;

            // 从旧父级脱离
            if (_parent != null)
                _parent._children.Remove(this);

            _parent = parentActor;

            if (_parent != null)
            {
                _parent._children.Add(this);
                // 将位置设为父级空间的相对位置
                Position = _parent.Position;
                Rotation = _parent.Rotation;
            }
        }

        /// <summary>从父 Actor 脱离。</summary>
        public void DetachFromActor()
        {
            AttachToActor(null);
        }

        /// <summary>销毁此 Actor。</summary>
        public void Destroy()
        {
            if (IsDestroyed) return;
            IsDestroyed = true;

            // 销毁所有子 Actor
            foreach (var child in _children.ToArray())
                child.Destroy();

            // EndPlay 所有组件
            foreach (var comp in _components.ToArray())
            {
                comp.InvokeEndPlay();
                comp.UnregisterComponent();
                comp.Owner = null;
            }
            _components.Clear();

            // 从父级脱离
            DetachFromActor();

            // 从 World 移除
            World?.UnregisterActor(this);

            LifecyclePhase = GLifecyclePhase.Destroyed;
        }

        // ── 内部生命周期 (由 GGameWorld 驱动) ─────────────────────────

        internal void InvokeBeginPlay()
        {
            if (LifecyclePhase != GLifecyclePhase.Initialized) return;
            LifecyclePhase = GLifecyclePhase.BeginPlayed;
            OnBeginPlay();
            foreach (var c in _components.ToArray())
                c.InvokeBeginPlay();
            LifecyclePhase = GLifecyclePhase.Ticking;
        }

        internal void InvokeTick(float deltaTime)
        {
            if (LifecyclePhase != GLifecyclePhase.Ticking || !TickEnabled) return;
            OnTick(deltaTime);
            _tickScratchList.Clear();
            _tickScratchList.AddRange(_components);
            foreach (var c in _tickScratchList)
            {
                if (c.ComponentTickEnabled) c.InvokeTick(deltaTime);
            }
        }

        internal void InvokeEndPlay()
        {
            if (LifecyclePhase == GLifecyclePhase.EndPlayed ||
                LifecyclePhase == GLifecyclePhase.Destroyed ||
                LifecyclePhase == GLifecyclePhase.Constructed) return;

            foreach (var c in _components.ToArray())
                c.InvokeEndPlay();
            OnEndPlay();
            LifecyclePhase = GLifecyclePhase.EndPlayed;
        }

        // ── 虚方法 (子类覆写) ────────────────────────────────────────

        /// <summary>初始化回调。</summary>
        protected virtual void OnInit() { }

        /// <summary>BeginPlay 回调 — 游戏开始时调用一次。</summary>
        protected virtual void OnBeginPlay() { }

        /// <summary>每帧 Tick。</summary>
        protected virtual void OnTick(float deltaTime) { }

        /// <summary>EndPlay 回调 — 游戏结束或销毁时调用。</summary>
        protected virtual void OnEndPlay() { }

        /// <summary>变换变化回调。</summary>
        protected virtual void OnTransformChanged() { }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 第八部分: GPawn — 可控制实体
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// GPawn — 可被 GPlayerController 或 AIController 占据的实体。
    /// 内置移动组件和碰撞组件，支持 Possess/Unpossess。
    /// </summary>
    public class GPawn : GActor
    {
        // ── 控制器 ──────────────────────────────────────────────────

        /// <summary>当前控制此 Pawn 的 Controller。</summary>
        public GController? CurrentController { get; internal set; }

        /// <summary>上一次控制此 Pawn 的 Controller。</summary>
        public GController? PreviousController { get; private set; }

        // ── 内置组件 ──────────────────────────────────────────────────

        private GMovementComponent? _movementComponent;
        private GCollisionComponent? _collisionComponent;
        private GHealthComponent? _healthComponent;

        /// <summary>移动组件（首次访问时自动创建）。</summary>
        public GMovementComponent MovementComponent
        {
            get
            {
                if (_movementComponent == null)
                    _movementComponent = AddComponent<GMovementComponent>();
                return _movementComponent;
            }
            set => _movementComponent = value;
        }

        /// <summary>碰撞组件（首次访问时自动创建）。</summary>
        public GCollisionComponent CollisionComponent
        {
            get
            {
                if (_collisionComponent == null)
                    _collisionComponent = AddComponent<GCollisionComponent>();
                return _collisionComponent;
            }
            set => _collisionComponent = value;
        }

        /// <summary>生命组件（首次访问时自动创建）。</summary>
        public GHealthComponent HealthComponent
        {
            get
            {
                if (_healthComponent == null)
                    _healthComponent = AddComponent<GHealthComponent>();
                return _healthComponent;
            }
            set => _healthComponent = value;
        }

        /// <summary>是否可被控制。</summary>
        public bool CanBePossessed { get; set; } = true;

        // ── 输入 ──────────────────────────────────────────────────────

        /// <summary>等待处理的移动输入。</summary>
        public Vector3 PendingMovementInput { get; private set; } = Vector3.Zero;

        /// <summary>添加移动输入。</summary>
        public void AddMovementInput(Vector3 worldDirection, float scale = 1f)
        {
            if (worldDirection.LengthSquared() <= 0f || scale == 0f) return;
            var dir = Vector3.Normalize(worldDirection) * scale;
            PendingMovementInput += dir;
            _movementComponent?.AddMovementInput(worldDirection, scale);
        }

        /// <summary>清空并返回待处理输入。</summary>
        public Vector3 ConsumeMovementInput()
        {
            var v = PendingMovementInput;
            PendingMovementInput = Vector3.Zero;
            return v;
        }

        /// <summary>请求跳跃。</summary>
        public virtual bool Jump()
        {
            return _movementComponent?.Jump() ?? false;
        }

        // ── 生命周期 ────────────────────────────────────────────────

        public GPawn()
        {
            ActorName = "GPawn";
        }

        /// <summary>被 Controller 占据时调用。</summary>
        public virtual void PossessedBy(GController controller)
        {
            PreviousController = CurrentController;
            CurrentController = controller;
            OnPossessed(controller);
        }

        /// <summary>被 Controller 释放时调用。</summary>
        public virtual void Unpossessed()
        {
            var prev = CurrentController;
            CurrentController = null;
            OnUnpossessed(prev);
        }

        // ── 虚方法 ──────────────────────────────────────────────────

        protected virtual void OnPossessed(GController controller) { }
        protected virtual void OnUnpossessed(GController? controller) { }

        protected override void OnTick(float deltaTime)
        {
            // 基础移动逻辑（如果 MovementComponent 不存在）
            if (_movementComponent == null)
            {
                var input = ConsumeMovementInput();
                if (input.LengthSquared() > 0f)
                {
                    if (input.LengthSquared() > 1f)
                        input = Vector3.Normalize(input);
                    Position += input * 5f * deltaTime;
                }
            }
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 第九部分: GController — 控制器基类
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// GController — 控制器基类。
    /// 决定 Pawn 接受什么输入或 AI 决策。是"灵魂"，Pawn 是"躯体"。
    /// </summary>
    public abstract class GController : GActor
    {
        /// <summary>当前占据的 Pawn。</summary>
        public GPawn? PossessedPawn { get; private set; }

        /// <summary>关联的 PlayerState。</summary>
        public GPlayerState? PlayerState { get; set; }

        /// <summary>占据目标 Pawn。</summary>
        public virtual void Possess(GPawn pawn)
        {
            if (pawn == null) throw new ArgumentNullException(nameof(pawn));
            if (!pawn.CanBePossessed)
                throw new InvalidOperationException($"Pawn '{pawn.ActorName}' cannot be possessed.");

            // 先释放旧 Pawn
            UnPossess();

            // 如果 Pawn 已被其他 Controller 占据
            pawn.CurrentController?.UnPossess();

            PossessedPawn = pawn;
            pawn.PossessedBy(this);
            OnPossess(pawn);
        }

        /// <summary>释放当前占据的 Pawn。</summary>
        public virtual void UnPossess()
        {
            if (PossessedPawn == null) return;
            var prev = PossessedPawn;
            PossessedPawn.Unpossessed();
            PossessedPawn = null;
            OnUnPossess(prev);
        }

        /// <summary>Pawn 死亡时调用。</summary>
        public virtual void OnPawnDeath(GPawn pawn)
        {
            if (PossessedPawn == pawn)
                UnPossess();
        }

        // ── 虚方法 ──────────────────────────────────────────────────

        protected virtual void OnPossess(GPawn pawn) { }
        protected virtual void OnUnPossess(GPawn pawn) { }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 第十部分: GPlayerController — 玩家控制器
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 输入绑定描述。
    /// </summary>
    public readonly struct GInputBinding
    {
        public string ActionName { get; }
        public Action<float> Action { get; }
        public bool IsAxis { get; }

        public GInputBinding(string actionName, Action<float> action, bool isAxis = false)
        {
            ActionName = actionName ?? string.Empty;
            Action = action ?? throw new ArgumentNullException(nameof(action));
            IsAxis = isAxis;
        }
    }

    /// <summary>
    /// RPC 调用描述。
    /// </summary>
    public readonly struct GRpcCall
    {
        public string FunctionName { get; }
        public GRpcMode Mode { get; }
        public bool Reliable { get; }

        public GRpcCall(string functionName, GRpcMode mode, bool reliable = true)
        {
            FunctionName = functionName ?? string.Empty;
            Mode = mode;
            Reliable = reliable;
        }
    }

    /// <summary>
    /// GPlayerController — 玩家控制器。
    /// 负责输入处理、RPC 调用、相机管理、PlayerState 同步。
    /// </summary>
    public class GPlayerController : GController
    {
        // ── 输入 ──────────────────────────────────────────────────────

        private readonly Dictionary<string, GInputBinding> _inputBindings = new();

        /// <summary>是否屏蔽输入。</summary>
        public bool InputBlocked { get; set; }

        /// <summary>所有输入绑定。</summary>
        public IReadOnlyDictionary<string, GInputBinding> InputBindings => _inputBindings;

        // ── RPC ────────────────────────────────────────────────────────

        private readonly Dictionary<string, List<Action<object?>>> _rpcHandlers = new();

        // ── 网络 ──────────────────────────────────────────────────────

        /// <summary>网络连接 ID (0 = 本地)。</summary>
        public int NetConnectionId { get; set; } = 0;

        /// <summary>是否为本地玩家。</summary>
        public bool IsLocalPlayer { get; set; } = true;

        /// <summary>网络延迟 (ms)。</summary>
        public int Ping { get; set; }

        // ── 相机 ──────────────────────────────────────────────────────

        /// <summary>相机 FOV。</summary>
        public float CameraFOV { get; set; } = 90f;

        /// <summary>相机位置偏移。</summary>
        public Vector3 CameraOffset { get; set; } = new Vector3(0, 120, -300);

        public GPlayerController()
        {
            ActorName = "GPlayerController";
        }

        // ── 输入绑定 ────────────────────────────────────────────────

        /// <summary>绑定一个动作输入（按下/释放）。</summary>
        public void BindAction(string actionName, Action<float> action)
        {
            if (string.IsNullOrEmpty(actionName)) throw new ArgumentNullException(nameof(actionName));
            _inputBindings[actionName] = new GInputBinding(actionName, action, false);
        }

        /// <summary>绑定一个轴输入（持续值）。</summary>
        public void BindAxis(string axisName, Action<float> action)
        {
            if (string.IsNullOrEmpty(axisName)) throw new ArgumentNullException(nameof(axisName));
            _inputBindings[axisName] = new GInputBinding(axisName, action, true);
        }

        /// <summary>解除绑定。</summary>
        public void UnbindInput(string actionName)
        {
            _inputBindings.Remove(actionName);
        }

        /// <summary>处理输入事件。</summary>
        public void ProcessInputEvent(string actionName, float value)
        {
            if (InputBlocked) return;
            if (_inputBindings.TryGetValue(actionName, out var binding))
            {
                binding.Action.Invoke(value);
            }
        }

        /// <summary>处理移动输入（转发到 Pawn）。</summary>
        public void HandleMovementInput(Vector3 direction, float scale = 1f)
        {
            if (InputBlocked || PossessedPawn == null) return;
            PossessedPawn.AddMovementInput(direction, scale);
        }

        /// <summary>处理动作输入（如跳跃、开火）。</summary>
        public void HandleActionInput(string actionName)
        {
            if (InputBlocked) return;
            ProcessInputEvent(actionName, 1f);
        }

        // ── RPC ──────────────────────────────────────────────────────

        /// <summary>注册 RPC 处理器。</summary>
        public void RegisterRpcHandler(string functionName, Action<object?> handler)
        {
            if (!_rpcHandlers.ContainsKey(functionName))
                _rpcHandlers[functionName] = new List<Action<object?>>();
            _rpcHandlers[functionName].Add(handler);
        }

        /// <summary>调用 RPC。</summary>
        public void CallRpc(GRpcCall rpc, params object?[] args)
        {
            // 在实际引擎中，这里会通过 NetworkSystem 发送到远程
            // 当前为框架占位: 如果是 Server RPC 且本地是 Authority，直接执行
            if (rpc.Mode == GRpcMode.Server && NetRole == GNetRole.Authority)
            {
                ExecuteRpcLocal(rpc.FunctionName, args);
            }
            // Multicast: 本地 + 广播
            else if (rpc.Mode == GRpcMode.Multicast)
            {
                ExecuteRpcLocal(rpc.FunctionName, args);
            }
            // Client/OwnerClient: 由网络系统在远程端执行
        }

        private void ExecuteRpcLocal(string functionName, object?[] args)
        {
            if (_rpcHandlers.TryGetValue(functionName, out var handlers))
            {
                foreach (var handler in handlers)
                {
                    try
                    {
                        if (args.Length == 0)
                            handler(null);
                        else
                            handler(args[0]);
                    }
                    catch (Exception ex)
                    {
                        // RPC 执行异常不影响游戏流程
                        System.Diagnostics.Debug.WriteLine(
                            $"[GPlayerController] RPC '{functionName}' error: {ex.Message}");
                    }
                }
            }
        }

        // ── Possess 扩展 ─────────────────────────────────────────────

        protected override void OnPossess(GPawn pawn)
        {
            base.OnPossess(pawn);
            // 设置 PlayerState
            if (PlayerState != null)
                PlayerState.Pawn = pawn;
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 第十一部分: GPlayerState — 玩家状态
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// GPlayerState — 玩家状态。
    /// 记录玩家的分数、队伍、击杀/死亡、Ping 等数据。
    /// 由 GGameState 持有，需要网络复制到所有客户端。
    /// </summary>
    public class GPlayerState : GActor
    {
        // ── 基本信息 ──────────────────────────────────────────────────

        /// <summary>玩家显示名称。</summary>
        public string PlayerName { get; set; } = "Player";

        /// <summary>玩家 ID。</summary>
        public int PlayerId { get; set; }

        /// <summary>队伍编号 (-1 = 无队伍)。</summary>
        public int TeamId { get; set; } = -1;

        // ── 分数 ──────────────────────────────────────────────────────

        /// <summary>当前分数。</summary>
        public int Score { get; private set; }

        /// <summary>击杀数。</summary>
        public int Kills { get; private set; }

        /// <summary>死亡数。</summary>
        public int Deaths { get; private set; }

        /// <summary>助攻数。</summary>
        public int Assists { get; private set; }

        /// <summary>KDA 比率。</summary>
        public float KDA => Deaths == 0 ? (float)Kills : (float)(Kills + Assists * 0.5f) / Deaths;

        // ── 网络信息 ──────────────────────────────────────────────────

        /// <summary>网络延迟 (ms)。</summary>
        public int Ping { get; set; }

        /// <summary>是否已准备。</summary>
        public bool IsReady { get; set; }

        // ── 关联 ──────────────────────────────────────────────────────

        /// <summary>关联的 Pawn。</summary>
        public GPawn? Pawn { get; set; }

        /// <summary>关联的 PlayerController。</summary>
        public GPlayerController? PlayerController { get; set; }

        // ── 事件 ──────────────────────────────────────────────────────

        /// <summary>分数变化事件。</summary>
        public event Action<int, int>? OnScoreChanged;

        /// <summary>击杀事件。</summary>
        public event Action? OnKill;

        /// <summary>死亡事件。</summary>
        public event Action? OnDeath;

        // ── 操作 ──────────────────────────────────────────────────────

        public GPlayerState()
        {
            ActorName = "GPlayerState";
            IsReplicated = true;
        }

        /// <summary>增加分数。</summary>
        public void AddScore(int amount)
        {
            int prev = Score;
            Score += amount;
            OnScoreChanged?.Invoke(prev, Score);
        }

        /// <summary>设置分数。</summary>
        public void SetScore(int newScore)
        {
            int prev = Score;
            Score = newScore;
            OnScoreChanged?.Invoke(prev, Score);
        }

        /// <summary>记录击杀。</summary>
        public void RecordKill()
        {
            Kills++;
            OnKill?.Invoke();
        }

        /// <summary>记录死亡。</summary>
        public void RecordDeath()
        {
            Deaths++;
            OnDeath?.Invoke();
        }

        /// <summary>记录助攻。</summary>
        public void RecordAssist()
        {
            Assists++;
        }

        /// <summary>重置统计数据。</summary>
        public void ResetStats()
        {
            Score = 0;
            Kills = 0;
            Deaths = 0;
            Assists = 0;
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 第十二部分: GGameState — 游戏状态同步
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// GGameState — 游戏全局状态。
    /// 同步 MatchState、分数、比赛时间、玩家列表到所有客户端。
    /// 每个 GGameWorld 持有唯一实例。
    /// </summary>
    public class GGameState : GActor
    {
        // ── 匹配状态 ────────────────────────────────────────────────

        private GMatchState _matchState = GMatchState.WaitingToStart;

        /// <summary>当前匹配状态。</summary>
        public GMatchState MatchState
        {
            get => _matchState;
            private set
            {
                if (_matchState == value) return;
                var old = _matchState;
                _matchState = value;
                OnMatchStateChanged?.Invoke(old, value);
            }
        }

        /// <summary>匹配状态变化事件。</summary>
        public event MatchStateChangedDelegate? OnMatchStateChanged;

        // ── 比赛时间 ────────────────────────────────────────────────

        /// <summary>比赛已经过时间（秒）。</summary>
        public float MatchTime { get; private set; }

        /// <summary>比赛最大时间（秒，0 = 无限）。</summary>
        public float MaxMatchTime { get; set; } = 0f;

        /// <summary>剩余时间（秒，仅在 MaxMatchTime > 0 时有效）。</summary>
        public float RemainingTime => MaxMatchTime > 0f
            ? Math.Max(0f, MaxMatchTime - MatchTime)
            : 0f;

        // ── 分数 ──────────────────────────────────────────────────────

        /// <summary>团队分数 (TeamId -> Score)。</summary>
        public Dictionary<int, int> TeamScores { get; } = new();

        /// <summary>全局分数。</summary>
        public int TotalScore { get; private set; }

        // ── 玩家列表 ──────────────────────────────────────────────────

        /// <summary>所有 PlayerState。</summary>
        public readonly List<GPlayerState> Players = new();

        /// <summary>玩家数量。</summary>
        public int PlayerCount => Players.Count;

        /// <summary>最大玩家数。</summary>
        public int MaxPlayers { get; set; } = 16;

        // ── 同步标记 ──────────────────────────────────────────────────

        /// <summary>是否需要复制。</summary>
        public bool ReplicatedState { get; set; } = true;

        // ── 事件 ──────────────────────────────────────────────────────

        /// <summary>玩家加入事件。</summary>
        public event PlayerJoinedDelegate? OnPlayerJoined;

        /// <summary>玩家离开事件。</summary>
        public event PlayerLeftDelegate? OnPlayerLeft;

        /// <summary>比赛结束事件。</summary>
        public event Action<string>? OnMatchEnded;

        // ── 初始化 ────────────────────────────────────────────────────

        public GGameState()
        {
            ActorName = "GGameState";
            IsReplicated = true;
        }

        // ── 状态转换 ──────────────────────────────────────────────────

        /// <summary>进入 Ready 阶段 (WaitingToStart)。</summary>
        public void SetReady()
        {
            if (MatchState == GMatchState.WaitingToStart) return;
            MatchState = GMatchState.WaitingToStart;
        }

        /// <summary>进入 Active 阶段 (InProgress)。</summary>
        public void SetInProgress()
        {
            MatchState = GMatchState.InProgress;
        }

        /// <summary>暂停比赛。</summary>
        public void SetPaused()
        {
            if (MatchState != GMatchState.InProgress) return;
            MatchState = GMatchState.Paused;
        }

        /// <summary>恢复比赛。</summary>
        public void SetResumed()
        {
            if (MatchState != GMatchState.Paused) return;
            MatchState = GMatchState.InProgress;
        }

        /// <summary>结束比赛。</summary>
        public void SetEnded(string reason = "")
        {
            MatchState = GMatchState.Ended;
            OnMatchEnded?.Invoke(reason);
        }

        /// <summary>中止比赛。</summary>
        public void SetAborted()
        {
            MatchState = GMatchState.Aborted;
        }

        // ── 玩家管理 ──────────────────────────────────────────────────

        /// <summary>添加玩家。</summary>
        public void AddPlayer(GPlayerState playerState)
        {
            if (playerState == null) throw new ArgumentNullException(nameof(playerState));
            if (Players.Contains(playerState)) return;
            Players.Add(playerState);
            OnPlayerJoined?.Invoke(playerState.PlayerController!);
        }

        /// <summary>移除玩家。</summary>
        public void RemovePlayer(GPlayerState playerState)
        {
            if (playerState == null) return;
            if (Players.Remove(playerState))
            {
                OnPlayerLeft?.Invoke(playerState.PlayerController!);
            }
        }

        /// <summary>按 ID 查找玩家。</summary>
        public GPlayerState? FindPlayerById(int playerId)
        {
            foreach (var p in Players)
                if (p.PlayerId == playerId) return p;
            return null;
        }

        // ── 分数管理 ──────────────────────────────────────────────────

        /// <summary>设置团队分数。</summary>
        public void SetTeamScore(int teamId, int score)
        {
            TeamScores[teamId] = score;
        }

        /// <summary>增加团队分数。</summary>
        public void AddTeamScore(int teamId, int amount)
        {
            if (!TeamScores.ContainsKey(teamId))
                TeamScores[teamId] = 0;
            TeamScores[teamId] += amount;
        }

        /// <summary>增加全局分数。</summary>
        public void AddTotalScore(int amount)
        {
            TotalScore += amount;
        }

        /// <summary>重置比赛时间（供游戏框架内部在重新开始比赛时调用）。</summary>
        internal void ResetMatchTime() => MatchTime = 0f;

        /// <summary>重置全局分数（供游戏框架内部在重新开始比赛时调用）。</summary>
        internal void ResetTotalScore() => TotalScore = 0;

        // ── Tick ──────────────────────────────────────────────────────

        protected override void OnTick(float deltaTime)
        {
            // 更新比赛时间
            if (MatchState == GMatchState.InProgress)
            {
                MatchTime += deltaTime;

                // 超时检测
                if (MaxMatchTime > 0f && MatchTime >= MaxMatchTime)
                {
                    SetEnded("TimeLimit");
                }
            }
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 第十三部分: DamageSystem — 伤害系统
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// GDamageEvent — 伤害事件数据。
    /// </summary>
    public class GDamageEvent
    {
        /// <summary>原始伤害量。</summary>
        public float BaseDamage { get; set; }

        /// <summary>实际造成的伤害量。</summary>
        public float ActualDamage { get; set; }

        /// <summary>伤害类型。</summary>
        public GDamageType DamageType { get; set; } = GDamageType.Physical;

        /// <summary>造成伤害的 Actor。</summary>
        public GActor? Instigator { get; set; }

        /// <summary>造成伤害的 Controller。</summary>
        public GController? InstigatorController { get; set; }

        /// <summary>命中点（世界坐标）。</summary>
        public Vector3 HitLocation { get; set; }

        /// <summary>命中法线。</summary>
        public Vector3 HitNormal { get; set; }

        /// <summary>伤害方向。</summary>
        public Vector3 DamageDirection { get; set; }

        /// <summary>是否暴击。</summary>
        public bool IsCritical { get; set; }

        /// <summary>暴击倍率。</summary>
        public float CriticalMultiplier { get; set; } = 2f;

        /// <summary>时间戳。</summary>
        public float Timestamp { get; set; }

        public GDamageEvent()
        {
            Timestamp = 0f;
        }

        /// <summary>创建一个标准伤害事件。</summary>
        public static GDamageEvent Create(
            float damage,
            GDamageType type,
            GActor? instigator,
            Vector3 hitLocation = default,
            Vector3 hitNormal = default)
        {
            return new GDamageEvent
            {
                BaseDamage = damage,
                DamageType = type,
                Instigator = instigator,
                HitLocation = hitLocation,
                HitNormal = hitNormal,
                DamageDirection = hitNormal != default ? -hitNormal : Vector3.Zero
            };
        }
    }

    /// <summary>
    /// GDamageModifier — 伤害修改器（用于护甲、buff/debuff 等）。
    /// </summary>
    public class GDamageModifier
    {
        /// <summary>修改器名称。</summary>
        public string ModifierName { get; set; } = string.Empty;

        /// <summary>伤害乘数 (1.0 = 不变, 0.5 = 减半, 2.0 = 加倍)。</summary>
        public float DamageMultiplier { get; set; } = 1f;

        /// <summary>固定减伤值。</summary>
        public float FlatReduction { get; set; } = 0f;

        /// <summary>优先级（数值越小越先执行）。</summary>
        public int Priority { get; set; } = 0;

        /// <summary>生效的伤害类型 (null = 所有类型)。</summary>
        public GDamageType? ApplicableDamageType { get; set; }

        /// <summary>是否启用。</summary>
        public bool IsActive { get; set; } = true;

        /// <summary>应用修改器到伤害值。</summary>
        public float Apply(float damage, GDamageType type)
        {
            if (!IsActive) return damage;
            if (ApplicableDamageType.HasValue && ApplicableDamageType.Value != type) return damage;

            float modified = (damage - FlatReduction) * DamageMultiplier;
            return Math.Max(0f, modified);
        }
    }

    /// <summary>
    /// GArmorComponent — 护甲组件。
    /// 提供物理护甲和特殊抗性，支持不同伤害类型的不同抗性。
    /// </summary>
    public class GArmorComponent : GActorComponent
    {
        /// <summary>基础护甲值（减少固定伤害）。</summary>
        public float BaseArmor { get; set; } = 50f;

        /// <summary>当前护甲值。</summary>
        public float CurrentArmor { get; private set; }

        /// <summary>护甲耐久度（护甲损坏后不再生效）。</summary>
        public float ArmorDurability { get; set; } = 100f;

        /// <summary>当前耐久度。</summary>
        public float CurrentDurability { get; private set; }

        /// <summary>伤害类型抗性 (DamageType -> 抗性比例 0~1)。</summary>
        public Dictionary<GDamageType, float> TypeResistances { get; } = new();

        /// <summary>护甲破损事件。</summary>
        public event Action? OnArmorBroken;

        /// <summary>护甲修复事件。</summary>
        public event Action? OnArmorRepaired;

        public GArmorComponent()
        {
            ComponentName = "GArmorComponent";

            // 默认抗性
            TypeResistances[GDamageType.Physical] = 0.3f;
            TypeResistances[GDamageType.Explosive] = 0.1f;
            TypeResistances[GDamageType.Fire] = 0.2f;
            TypeResistances[GDamageType.Electric] = 0.15f;
            TypeResistances[GDamageType.TrueDamage] = 0f;
        }

        protected override void OnBeginPlay()
        {
            CurrentArmor = BaseArmor;
            CurrentDurability = ArmorDurability;
        }

        /// <summary>计算护甲减免后的伤害。</summary>
        public float CalculateReducedDamage(float damage, GDamageType type)
        {
            if (CurrentDurability <= 0f) return damage; // 护甲破损，不减免

            // 获取类型抗性
            float resistance = 0f;
            TypeResistances.TryGetValue(type, out resistance);

            // 计算减免
            float reduction = damage * resistance + BaseArmor * (1f - damage / (damage + BaseArmor));
            float reducedDamage = Math.Max(0f, damage - reduction);

            // 消耗耐久
            float durabilityCost = damage * 0.5f;
            CurrentDurability = Math.Max(0f, CurrentDurability - durabilityCost);

            if (CurrentDurability <= 0f)
                OnArmorBroken?.Invoke();

            return reducedDamage;
        }

        /// <summary>修复护甲。</summary>
        public void RepairArmor(float amount)
        {
            bool wasBroken = CurrentDurability <= 0f;
            CurrentDurability = Math.Min(ArmorDurability, CurrentDurability + amount);
            if (wasBroken && CurrentDurability > 0f)
                OnArmorRepaired?.Invoke();
        }

        /// <summary>重置护甲。</summary>
        public void ResetArmor()
        {
            CurrentArmor = BaseArmor;
            CurrentDurability = ArmorDurability;
        }
    }

    /// <summary>
    /// DamageSystem — 静态伤害处理工具类。
    /// 提供伤害计算、应用、传播的核心逻辑。
    /// </summary>
    public static class DamageSystem
    {
        /// <summary>应用伤害到目标 Actor。</summary>
        /// <returns>实际造成的伤害量。</returns>
        public static float ApplyDamage(GActor target, GDamageEvent damageEvent)
        {
            if (target == null || damageEvent == null) return 0f;

            // 尝试获取 HealthComponent
            var healthComp = target.GetComponent<GHealthComponent>();
            if (healthComp == null) return 0f;

            // 尝试获取 ArmorComponent (可选)
            var armorComp = target.GetComponent<GArmorComponent>();
            float modifiedDamage = damageEvent.BaseDamage;

            // 应用护甲减免（非真实伤害）
            if (armorComp != null && damageEvent.DamageType != GDamageType.TrueDamage)
            {
                modifiedDamage = armorComp.CalculateReducedDamage(modifiedDamage, damageEvent.DamageType);
            }

            // 应用伤害修改器
            // DamageModifier 不是组件，此处跳过（修改器由外部系统管理）

            // 暴击处理
            if (damageEvent.IsCritical)
            {
                modifiedDamage *= damageEvent.CriticalMultiplier;
            }

            // 应用到 HealthComponent
            damageEvent.ActualDamage = healthComp.ApplyDamage(
                modifiedDamage,
                damageEvent.DamageType,
                damageEvent.Instigator);

            return damageEvent.ActualDamage;
        }

        /// <summary>创建并应用一个标准伤害事件。</summary>
        public static float ApplyDamage(
            GActor target,
            float damage,
            GDamageType type,
            GActor? instigator = null)
        {
            var evt = GDamageEvent.Create(damage, type, instigator);
            return ApplyDamage(target, evt);
        }

        /// <summary>治疗目标。</summary>
        public static float Heal(GActor target, float amount, GActor? instigator = null)
        {
            var evt = GDamageEvent.Create(amount, GDamageType.Heal, instigator);
            return ApplyDamage(target, evt);
        }

        /// <summary>创建一个爆炸伤害事件，应用到范围内所有目标。</summary>
        /// <returns>受影响的 Actor 列表。</returns>
        public static List<(GActor target, float damage)> ApplyRadialDamage(
            Vector3 origin,
            float baseDamage,
            float radius,
            GDamageType type,
            IEnumerable<GActor> targets,
            GActor? instigator = null,
            float falloffExponent = 1f)
        {
            var results = new List<(GActor, float)>();

            foreach (var target in targets)
            {
                if (target == null) continue;

                float distance = Vector3.Distance(origin, target.Position);
                if (distance > radius) continue;

                // 线性衰减（UE 标准）
                float falloff = 1f - MathF.Pow(distance / radius, falloffExponent);
                float damage = baseDamage * Math.Max(0f, falloff);

                var evt = GDamageEvent.Create(damage, type, instigator, target.Position);
                evt.HitNormal = Vector3.Normalize(target.Position - origin);
                float actual = ApplyDamage(target, evt);

                if (actual != 0f)
                    results.Add((target, actual));
            }

            return results;
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 第十四部分: GGameMode — 游戏规则管理
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>玩家登录结果。</summary>
    public enum GLoginResult
    {
        Accepted,
        Rejected,
        ServerFull,
        WrongPassword,
        Banned
    }

    /// <summary>
    /// GGameMode — 游戏规则管理核心。
    /// 定义默认 Pawn/PlayerController/GameState/PlayerState 类型引用，
    /// 管理匹配状态转换（Ready/Active/Ended）、玩家登录流程、得分与胜利条件。
    /// </summary>
    public class GGameMode : GActor
    {
        // ── 默认类引用 ────────────────────────────────────────────────

        /// <summary>默认 Pawn 类型工厂。</summary>
        public Func<GPawn> DefaultPawnFactory { get; set; } =
            () => new GPawn { ActorName = "DefaultPawn" };

        /// <summary>默认 PlayerController 类型工厂。</summary>
        public Func<GPlayerController> PlayerControllerFactory { get; set; } =
            () => new GPlayerController { ActorName = "PlayerController" };

        /// <summary>默认 GameState 类型工厂。</summary>
        public Func<GGameState> GameStateFactory { get; set; } =
            () => new GGameState { ActorName = "GameState" };

        /// <summary>默认 PlayerState 类型工厂。</summary>
        public Func<GPlayerState> PlayerStateFactory { get; set; } =
            () => new GPlayerState { ActorName = "PlayerState" };

        // ── 实例引用 ──────────────────────────────────────────────────

        /// <summary>当前 GameState 实例。</summary>
        public GGameState? GameState { get; private set; }

        /// <summary>所有已注册的 PlayerController。</summary>
        public readonly List<GPlayerController> PlayerControllers = new();

        /// <summary>所有已注册的 PlayerState。</summary>
        public readonly List<GPlayerState> PlayerStates = new();

        // ── 比赛设置 ──────────────────────────────────────────────────

        /// <summary>最大玩家数。</summary>
        public int MaxPlayers { get; set; } = 16;

        /// <summary>最小开始人数。</summary>
        public int MinPlayersToStart { get; set; } = 1;

        /// <summary>比赛开始前倒计时（秒）。</summary>
        public float CountdownTime { get; set; } = 5f;

        /// <summary>比赛最大时间（秒，0 = 无限）。</summary>
        public float MatchTimeLimit { get; set; } = 0f;

        /// <summary>是否允许观察者。</summary>
        public bool AllowSpectators { get; set; } = true;

        // ── 分数与规则 ────────────────────────────────────────────────

        /// <summary>胜利分数（达到此分数自动结束比赛）。</summary>
        public int ScoreToWin { get; set; } = 0;

        /// <summary>当前全局分数。</summary>
        public int Score { get; private set; }

        /// <summary>比赛结束原因。</summary>
        public string EndReason { get; private set; } = string.Empty;

        /// <summary>是否比赛已结束。</summary>
        public bool HasMatchEnded { get; private set; }

        // ── 事件 ──────────────────────────────────────────────────────

        /// <summary>玩家登录事件。</summary>
        public event Action<string, GLoginResult>? OnPlayerLogin;

        /// <summary>玩家加入事件。</summary>
        public event Action<GPlayerController>? OnPlayerJoined;

        /// <summary>玩家离开事件。</summary>
        public event Action<GPlayerController>? OnPlayerLeft;

        /// <summary>比赛开始事件。</summary>
        public event Action? OnMatchStarted;

        /// <summary>比赛结束事件。</summary>
        public event Action<string>? OnMatchEnded;

        /// <summary>分数变化事件。</summary>
        public event Action<int, int>? OnScoreChanged;

        // ── 初始化 ────────────────────────────────────────────────────

        public GGameMode()
        {
            ActorName = "GGameMode";
        }

        /// <summary>初始化 GameMode（创建 GameState 等实例）。</summary>
        public override void Initialize()
        {
            base.Initialize();
            GameState = GameStateFactory();
        }

        // ── 玩家登录流程 ──────────────────────────────────────────────

        /// <summary>预登录检查。</summary>
        public virtual GLoginResult PreLogin(string playerName, int playerId)
        {
            // 服务器已满
            if (PlayerControllers.Count >= MaxPlayers)
                return GLoginResult.ServerFull;

            // 比赛已结束
            if (HasMatchEnded)
                return GLoginResult.Rejected;

            return GLoginResult.Accepted;
        }

        /// <summary>正式登录。</summary>
        public virtual GPlayerController? Login(string playerName, int playerId)
        {
            var result = PreLogin(playerName, playerId);
            OnPlayerLogin?.Invoke(playerName, result);

            if (result != GLoginResult.Accepted) return null;

            // 创建 PlayerController
            var pc = PlayerControllerFactory();
            pc.ActorName = $"PC_{playerName}_{playerId}";
            pc.PlayerState = CreatePlayerState(playerName, playerId);
            pc.PlayerState.PlayerController = pc;
            return pc;
        }

        /// <summary>创建 PlayerState。</summary>
        protected virtual GPlayerState CreatePlayerState(string playerName, int playerId)
        {
            var ps = PlayerStateFactory();
            ps.PlayerName = playerName;
            ps.PlayerId = playerId;
            PlayerStates.Add(ps);
            return ps;
        }

        /// <summary>玩家加入后处理。</summary>
        public virtual void PostLogin(GPlayerController playerController)
        {
            if (playerController == null) return;

            PlayerControllers.Add(playerController);
            GameState?.AddPlayer(playerController.PlayerState!);
            OnPlayerJoined?.Invoke(playerController);

            // 检查是否满足开始条件
            if (!HasMatchEnded && GameState?.MatchState == GMatchState.WaitingToStart)
            {
                if (GameState.PlayerCount >= MinPlayersToStart)
                {
                    StartCountdown();
                }
            }
        }

        /// <summary>玩家离开处理。</summary>
        public virtual void Logout(GPlayerController playerController)
        {
            if (playerController == null) return;

            // 释放 Pawn
            playerController.UnPossess();

            PlayerControllers.Remove(playerController);

            if (playerController.PlayerState != null)
            {
                GameState?.RemovePlayer(playerController.PlayerState);
                PlayerStates.Remove(playerController.PlayerState);
            }

            OnPlayerLeft?.Invoke(playerController);
        }

        // ── 比赛流程 ──────────────────────────────────────────────────

        /// <summary>开始倒计时。</summary>
        protected virtual void StartCountdown()
        {
            GameState?.SetReady();
        }

        /// <summary>开始比赛。</summary>
        public virtual void StartMatch()
        {
            GameState?.SetInProgress();
            if (MatchTimeLimit > 0f)
                GameState!.MaxMatchTime = MatchTimeLimit;

            HasMatchEnded = false;
            EndReason = string.Empty;

            // 为所有已登录玩家生成 Pawn
            foreach (var pc in PlayerControllers)
            {
                SpawnDefaultPawnFor(pc);
            }

            OnMatchStarted?.Invoke();
        }

        /// <summary>结束比赛。</summary>
        public virtual void EndMatch(string reason = "")
        {
            if (HasMatchEnded) return;

            HasMatchEnded = true;
            EndReason = reason ?? string.Empty;
            GameState?.SetEnded(reason);
            OnMatchEnded?.Invoke(reason);
        }

        /// <summary>重启比赛。</summary>
        public virtual void RestartMatch()
        {
            // 重置所有状态
            Score = 0;
            HasMatchEnded = false;
            EndReason = string.Empty;

            if (GameState != null)
            {
                GameState.ResetMatchTime();
                GameState.ResetTotalScore();
                GameState.TeamScores.Clear();
            }

            foreach (var ps in PlayerStates)
                ps.ResetStats();

            StartMatch();
        }

        // ── Pawn 生成 ─────────────────────────────────────────────────

        /// <summary>为 PlayerController 生成默认 Pawn。</summary>
        public virtual GPawn SpawnDefaultPawnFor(GPlayerController playerController)
        {
            var pawn = DefaultPawnFactory();
            pawn.ActorName = $"Pawn_{playerController.ActorName}";

            // 注册到 World
            World?.RegisterActor(pawn);

            // 占据
            playerController.Possess(pawn);

            // 设置 PlayerState 关联
            if (playerController.PlayerState != null)
                playerController.PlayerState.Pawn = pawn;

            return pawn;
        }

        // ── 得分 ──────────────────────────────────────────────────────

        /// <summary>增加分数。</summary>
        public virtual void AddScore(int amount, GPlayerController? scorer = null)
        {
            int prev = Score;
            Score += amount;
            GameState?.AddTotalScore(amount);
            OnScoreChanged?.Invoke(prev, Score);

            // 检查胜利条件
            if (ScoreToWin > 0 && Score >= ScoreToWin)
            {
                EndMatch("ScoreLimitReached");
            }

            // 更新 PlayerState 分数
            if (scorer?.PlayerState != null)
                scorer.PlayerState.AddScore(amount);
        }

        /// <summary>记录击杀。</summary>
        public virtual void RecordKill(GPlayerController killer, GPlayerController victim)
        {
            killer.PlayerState?.RecordKill();
            victim.PlayerState?.RecordDeath();
            AddScore(100, killer); // 默认击杀 +100 分
        }

        // ── 胜负判定 ──────────────────────────────────────────────────

        /// <summary>检查胜利条件（子类覆写）。</summary>
        public virtual int? CheckWinCondition()
        {
            if (ScoreToWin > 0 && Score >= ScoreToWin)
                return -1; // 全局胜利

            // 检查团队胜利
            if (GameState?.TeamScores != null)
            {
                foreach (var kvp in GameState.TeamScores)
                {
                    if (kvp.Value >= ScoreToWin)
                        return kvp.Key;
                }
            }

            return null; // 无胜利者
        }

        // ── Tick ──────────────────────────────────────────────────────

        protected override void OnTick(float deltaTime)
        {
            // 检查胜利条件
            var winner = CheckWinCondition();
            if (winner.HasValue && !HasMatchEnded)
            {
                EndMatch($"Team {winner.Value} Wins");
            }

            // 倒计时自动开始比赛
            if (GameState != null &&
                GameState.MatchState == GMatchState.WaitingToStart &&
                GameState.PlayerCount >= MinPlayersToStart)
            {
                // 延迟开始 (由子类实现具体倒计时逻辑)
            }
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 第十五部分: GGameWorld — 游戏世界
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// GGameWorld — 游戏世界容器。
    /// 管理所有 GActor 的生命周期、Tick 顺序、GameMode 集成。
    /// 是完整 Gameplay Framework 的运行时核心。
    /// </summary>
    public sealed class GGameWorld
    {
        private readonly List<GActor> _actors = new();
        private readonly List<GActor> _pendingAdd = new();
        private readonly List<GActor> _pendingRemove = new();
        private bool _started;

        /// <summary>GameMode 实例。</summary>
        public GGameMode GameMode { get; }

        /// <summary>本地玩家控制器。</summary>
        public GPlayerController? LocalPlayerController { get; private set; }

        /// <summary>本地玩家 Pawn。</summary>
        public GPawn? LocalPlayerPawn => LocalPlayerController?.PossessedPawn;

        /// <summary>所有 Actor。</summary>
        public IReadOnlyList<GActor> Actors => _actors;

        /// <summary>Actor 数量。</summary>
        public int ActorCount => _actors.Count;

        /// <summary>世界运行时间（秒）。</summary>
        public float TimeSeconds { get; private set; }

        /// <summary>世界是否已启动。</summary>
        public bool IsStarted => _started;

        /// <summary>GameState 快捷访问。</summary>
        public GGameState? GameState => GameMode.GameState;

        // ── 构造 ──────────────────────────────────────────────────────

        public GGameWorld(GGameMode? gameMode = null)
        {
            GameMode = gameMode ?? new GGameMode();
            GameMode.Initialize();
        }

        // ── Actor 管理 ────────────────────────────────────────────────

        /// <summary>注册 Actor 到世界。</summary>
        public T RegisterActor<T>(T actor) where T : GActor
        {
            if (actor == null) throw new ArgumentNullException(nameof(actor));
            if (actor.World != null && actor.World != this)
                throw new InvalidOperationException($"Actor '{actor.ActorName}' already in another world.");

            actor.World = this;

            if (_started)
                _pendingAdd.Add(actor);
            else
                _actors.Add(actor);

            return actor;
        }

        /// <summary>从世界移除并销毁 Actor。</summary>
        public bool UnregisterActor(GActor actor)
        {
            if (actor == null) return false;
            if (!_actors.Contains(actor) && !_pendingAdd.Contains(actor)) return false;
            _pendingRemove.Add(actor);
            return true;
        }

        /// <summary>按类型查找 Actor。</summary>
        public T? FindActor<T>() where T : GActor
        {
            foreach (var a in _actors)
                if (a is T t) return t;
            return null;
        }

        /// <summary>按名称查找 Actor。</summary>
        public GActor? FindActorByName(string name)
        {
            foreach (var a in _actors)
                if (a.ActorName == name) return a;
            return null;
        }

        /// <summary>按标签查找 Actor。</summary>
        public IEnumerable<GActor> FindActorsByTag(string tag)
        {
            foreach (var a in _actors)
                if (a.HasTag(tag)) yield return a;
        }

        /// <summary>按类型查找所有 Actor。</summary>
        public IEnumerable<T> FindActors<T>() where T : GActor
        {
            foreach (var a in _actors)
                if (a is T t) yield return t;
        }

        /// <summary>在世界中生成 Actor。</summary>
        public T SpawnActor<T>(Vector3 position, T? template = null) where T : GActor, new()
        {
            var actor = template ?? new T();
            actor.Position = position;
            actor.Initialize();
            RegisterActor(actor);

            if (_started)
            {
                _pendingAdd.Remove(actor);
                _actors.Add(actor);
                actor.InvokeBeginPlay();
            }

            return actor;
        }

        /// <summary>销毁 Actor。</summary>
        public void DestroyActor(GActor actor)
        {
            if (actor == null) return;
            actor.Destroy();
        }

        // ── 玩家登录 ──────────────────────────────────────────────────

        /// <summary>登录新玩家。</summary>
        public GPlayerController? LoginPlayer(string playerName, int playerId = 0)
        {
            var pc = GameMode.Login(playerName, playerId);
            if (pc == null) return null;

            RegisterActor(pc);
            GameMode.PostLogin(pc);

            // 设置为本地玩家（第一个登录的）
            if (LocalPlayerController == null)
                LocalPlayerController = pc;

            return pc;
        }

        // ── 输入分发 ──────────────────────────────────────────────────

        /// <summary>分发移动输入。</summary>
        public void DispatchMovementInput(Vector3 direction, float scale = 1f)
        {
            LocalPlayerController?.HandleMovementInput(direction, scale);
        }

        /// <summary>分发动作输入。</summary>
        public void DispatchActionInput(string actionName, float value = 1f)
        {
            LocalPlayerController?.ProcessInputEvent(actionName, value);
        }

        // ── 生命周期 ──────────────────────────────────────────────────

        /// <summary>启动世界。</summary>
        public void Start()
        {
            if (_started) return;
            _started = true;

            // 初始化 GameMode
            if (GameMode.LifecyclePhase == GLifecyclePhase.Constructed)
                GameMode.Initialize();

            RegisterActor(GameMode);

            if (GameState != null)
                RegisterActor(GameState);

            FlushPending();

            // BeginPlay 所有 Actor
            foreach (var a in _actors.ToArray())
            {
                if (a.LifecyclePhase == GLifecyclePhase.Constructed)
                    a.Initialize();
                a.InvokeBeginPlay();
            }

            // 自动登录本地玩家（如果没有手动登录）
            if (LocalPlayerController == null)
            {
                LoginPlayer("LocalPlayer", 0);
            }
        }

        /// <summary>每帧更新。</summary>
        public void Tick(float deltaTime)
        {
            if (!_started) Start();

            float dt = Math.Max(0f, deltaTime);
            TimeSeconds += dt;
            FlushPending();

            // Tick 所有 Actor
            foreach (var a in _actors.ToArray())
                a.InvokeTick(dt);

            FlushPending();
        }

        /// <summary>关闭世界。</summary>
        public void Shutdown()
        {
            foreach (var a in _actors.ToArray())
                a.InvokeEndPlay();

            _actors.Clear();
            _pendingAdd.Clear();
            _pendingRemove.Clear();
            _started = false;
            LocalPlayerController = null;
        }

        // ── 内部 ──────────────────────────────────────────────────────

        private void FlushPending()
        {
            if (_pendingAdd.Count > 0)
            {
                foreach (var a in _pendingAdd)
                {
                    _actors.Add(a);
                    if (_started)
                    {
                        if (a.LifecyclePhase == GLifecyclePhase.Constructed)
                            a.Initialize();
                        a.InvokeBeginPlay();
                    }
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

    // ══════════════════════════════════════════════════════════════════════════
    // 第十六部分: 桥接适配器 — 连接现有 Gameplay 与 Full 框架
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// GameplayBridge — 桥接适配器。
    /// 可将 GGameWorld 中的 Full 框架实体桥接到现有 Gameplay.Actor/World。
    /// </summary>
    public static class GameplayBridge
    {
        /// <summary>
        /// 创建一个将 GPawn 的位置/旋转同步到目标 Action 的桥接组件。
        /// </summary>
        public class GPawnBridgeComponent : GActorComponent
        {
            /// <summary>外部同步回调。</summary>
            public Action<Vector3, Quaternion>? OnTransformSync;

            public GPawnBridgeComponent()
            {
                ComponentName = "GPawnBridgeComponent";
            }

            protected override void OnTick(float deltaTime)
            {
                if (Owner != null)
                    OnTransformSync?.Invoke(Owner.Position, Owner.Rotation);
            }
        }

        /// <summary>将 GPawn 的变换同步到外部系统。</summary>
        public static GPawnBridgeComponent CreatePawnBridge(
            GPawn pawn,
            Action<Vector3, Quaternion> syncCallback)
        {
            var bridge = new GPawnBridgeComponent();
            bridge.OnTransformSync = syncCallback;
            pawn.AttachComponent(bridge);
            return bridge;
        }

        /// <summary>从现有 GameplayWorld 创建桥接的 GGameWorld。</summary>
        public static GGameWorld CreateBridgedWorld()
        {
            return new GGameWorld();
        }
    }
}
