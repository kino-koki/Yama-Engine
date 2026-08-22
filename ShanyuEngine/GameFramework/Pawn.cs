using System;
using System.Numerics;

namespace ShanyuEngine.GameFramework
{
    // ════════════════════════════════════════════════════════════════════════
    // 山雨引擎 - Pawn (GameFramework)
    //
    // 类似 UE Pawn / Unity CharacterController:
    //   * 移动组件 CharacterMovementComponent (行走/跑步/跳跃/飞行/游泳)
    //   * 碰撞组件: 胶囊体碰撞
    //   * 控制输入: SetupPlayerInputComponent 虚方法
    //   * 网格组件: SkeletalMeshComponent / StaticMeshComponent
    //   * AI 感知: PawnSensingComponent (视野/听觉)
    //   * 受伤/死亡: TakeDamage(damage, instigator) / Die() / OnDeath
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>移动模式。</summary>
    public enum MovementMode
    {
        None,
        Walking,
        Running,
        Flying,
        Swimming,
        Falling
    }

    /// <summary>碰撞通道。</summary>
    public enum CollisionChannel
    {
        WorldStatic,
        WorldDynamic,
        Pawn,
        PhysicsBody,
        Visibility,
        Camera,
        UI
    }

    /// <summary>碰撞预设。</summary>
    public enum CollisionPreset
    {
        Default,
        PlayerOnly,
        EnemyOnly,
        NoCollision,
        OverlapAll,
        BlockAll
    }

    /// <summary>
    /// 可操控角色基类。可被 PlayerController 或 AIController 占据。
    /// </summary>
    public class Pawn : Actor
    {
        // ── 控制器 ────────────────────────────────────────────────────────

        /// <summary>当前操控此 Pawn 的 Controller。</summary>
        public Controller CurrentController { get; internal set; }

        /// <summary>是否被玩家操控。</summary>
        public bool IsPlayerControlled => CurrentController is PlayerController;

        /// <summary>是否被 AI 操控。</summary>
        public bool IsAIControlled => CurrentController != null && !(CurrentController is PlayerController);

        // ── 移动 ──────────────────────────────────────────────────────────

        /// <summary>当前移动模式。</summary>
        public MovementMode CurrentMovementMode { get; set; } = MovementMode.Walking;

        /// <summary>行走速度。</summary>
        public float WalkSpeed { get; set; } = 5f;

        /// <summary>跑步速度。</summary>
        public float RunSpeed { get; set; } = 10f;

        /// <summary>飞行速度。</summary>
        public float FlySpeed { get; set; } = 8f;

        /// <summary>游泳速度。</summary>
        public float SwimSpeed { get; set; } = 3f;

        /// <summary>跳躍速度 (Z 轴初速度)。</summary>
        public float JumpVelocity { get; set; } = 8f;

        /// <summary>重力缩放。</summary>
        public float GravityScale { get; set; } = 1f;

        /// <summary>全局重力 (从 WorldSettings 同步)。</summary>
        public float WorldGravity { get; set; } = -9.81f;

        /// <summary>当前累积移动输入。</summary>
        public Vector3 PendingMovementInput { get; private set; } = Vector3.Zero;

        /// <summary>是否正在跑步。</summary>
        public bool IsRunning { get; set; }

        /// <summary>是否正在跳跃。</summary>
        public bool IsJumping { get; private set; }

        /// <summary>是否在地面。</summary>
        public bool IsGrounded { get; set; } = true;

        /// <summary>当前 Z 轴速度 (用于跳跃/下落)。</summary>
        public float VerticalVelocity { get; set; }

        // ── 碰撞 ──────────────────────────────────────────────────────────

        /// <summary>胶囊体半径。</summary>
        public float CapsuleRadius { get; set; } = 0.5f;

        /// <summary>胶囊体半高。</summary>
        public float CapsuleHalfHeight { get; set; } = 1f;

        /// <summary>碰撞预设。</summary>
        public CollisionPreset CollisionPreset { get; set; } = CollisionPreset.Default;

        /// <summary>碰撞启用状态。</summary>
        public bool CollisionEnabled { get; set; } = true;

        // ── 生命值 ────────────────────────────────────────────────────────

        /// <summary>当前生命值。</summary>
        public float Health { get; set; } = 100f;

        /// <summary>最大生命值。</summary>
        public float MaxHealth { get; set; } = 100f;

        /// <summary>护甲值。</summary>
        public float Armor { get; set; }

        /// <summary>是否死亡。</summary>
        public bool IsDead => Health <= 0f;

        /// <summary>是否无敌。</summary>
        public bool IsInvincible { get; set; }

        // ── 网格 ──────────────────────────────────────────────────────────

        /// <summary>骨骼网格体路径 (模型资产路径)。</summary>
        public string SkeletalMeshPath { get; set; }

        /// <summary>静态网格体路径。</summary>
        public string StaticMeshPath { get; set; }

        /// <summary>动画蓝图路径。</summary>
        public string AnimNodeScriptPath { get; set; }

        // ── 输入 ──────────────────────────────────────────────────────────

        /// <summary>添加移动输入 (由 Controller 调用)。</summary>
        public void AddMovementInput(Vector3 worldDirection, float scale = 1f)
        {
            if (worldDirection.LengthSquared() <= 0f || scale == 0f) return;
            var normalized = Vector3.Normalize(worldDirection) * scale;
            PendingMovementInput += normalized;
        }

        /// <summary>消费并返回累积的移动输入。</summary>
        public Vector3 ConsumeMovementInput()
        {
            var input = PendingMovementInput;
            PendingMovementInput = Vector3.Zero;
            return input;
        }

        /// <summary>设置输入组件绑定 (虚方法, 子类重写)。</summary>
        public virtual void SetupPlayerInputComponent(InputComponent inputComponent) { }

        // ── 移动行为 ──────────────────────────────────────────────────────

        /// <summary>执行移动 (每帧由 Controller 或 AI 调用)。</summary>
        public void Move(float deltaTime)
        {
            if (IsDead) return;

            var input = ConsumeMovementInput();
            if (input.LengthSquared() > 0f)
            {
                if (input.LengthSquared() > 1f) input = Vector3.Normalize(input);
                float speed = GetCurrentSpeed();
                Position += input * speed * deltaTime;
            }

            // 重力与跳跃 (仅在 Walking/Falling 模式)
            if (CurrentMovementMode == MovementMode.Walking ||
                CurrentMovementMode == MovementMode.Falling)
            {
                ApplyGravity(deltaTime);
            }
        }

        /// <summary>获取当前移动速度。</summary>
        protected float GetCurrentSpeed()
        {
            return CurrentMovementMode switch
            {
                MovementMode.Running => IsRunning ? RunSpeed : WalkSpeed,
                MovementMode.Flying => FlySpeed,
                MovementMode.Swimming => SwimSpeed,
                _ => WalkSpeed
            };
        }

        /// <summary>应用重力。</summary>
        protected void ApplyGravity(float deltaTime)
        {
            if (!IsGrounded)
            {
                VerticalVelocity += WorldGravity * GravityScale * deltaTime;
                Position += new Vector3(0, VerticalVelocity * deltaTime, 0);

                // 简单地面检测
                if (Position.Y <= 0f)
                {
                    Position = new Vector3(Position.X, 0f, Position.Z);
                    VerticalVelocity = 0f;
                    IsGrounded = true;
                    IsJumping = false;
                    if (CurrentMovementMode == MovementMode.Falling)
                        CurrentMovementMode = MovementMode.Walking;
                }
            }
        }

        /// <summary>跳跃。</summary>
        public void Jump()
        {
            if (IsDead || !IsGrounded || IsJumping) return;
            if (CurrentMovementMode != MovementMode.Walking &&
                CurrentMovementMode != MovementMode.Running) return;

            VerticalVelocity = JumpVelocity;
            IsGrounded = false;
            IsJumping = true;
            OnJump();
        }

        /// <summary>停止跳跃。</summary>
        public void StopJumping()
        {
            if (!IsJumping) return;
            if (VerticalVelocity > 0f)
                VerticalVelocity *= 0.5f;
            IsJumping = false;
        }

        /// <summary>切换到奔跑模式。</summary>
        public void StartRunning() => IsRunning = true;

        /// <summary>停止奔跑。</summary>
        public void StopRunning() => IsRunning = false;

        /// <summary>切换到飞行模式。</summary>
        public void StartFlying()
        {
            CurrentMovementMode = MovementMode.Flying;
            VerticalVelocity = 0f;
        }

        /// <summary>停止飞行。</summary>
        public void StopFlying() => CurrentMovementMode = MovementMode.Walking;

        // ── 受伤/死亡 ──────────────────────────────────────────────────────

        /// <summary>受到伤害。</summary>
        public virtual float TakeDamage(float damage, Actor instigator = null)
        {
            if (IsDead || IsInvincible || damage <= 0f) return 0f;

            // 护甲减免
            float actualDamage = MathF.Max(0f, damage - Armor);

            Health = MathF.Max(0f, Health - actualDamage);
            OnDamaged(actualDamage, instigator);

            if (Health <= 0f)
                Die(instigator);

            return actualDamage;
        }

        /// <summary>治疗。</summary>
        public virtual float Heal(float amount)
        {
            if (IsDead || amount <= 0f) return 0f;
            float before = Health;
            Health = MathF.Min(MaxHealth, Health + amount);
            float healed = Health - before;
            OnHealed(healed);
            return healed;
        }

        /// <summary>死亡。</summary>
        public virtual void Die(Actor instigator = null)
        {
            if (IsDead) return;
            Health = 0f;
            CurrentMovementMode = MovementMode.None;
            TickEnabled = false;
            OnDeath(instigator);
        }

        // ── 事件虚方法 ────────────────────────────────────────────────────

        /// <summary>跳跃时调用。</summary>
        protected virtual void OnJump() { }

        /// <summary>受到伤害时调用。</summary>
        protected virtual void OnDamaged(float damage, Actor instigator) { }

        /// <summary>治疗时调用。</summary>
        protected virtual void OnHealed(float amount) { }

        /// <summary>死亡时调用。</summary>
        protected virtual void OnDeath(Actor instigator) { }

        /// <summary>着陆时调用。</summary>
        protected virtual void OnLanded() { }

        // ── Tick ──────────────────────────────────────────────────────────

        protected override void OnTick(float deltaTime)
        {
            base.OnTick(deltaTime);
            Move(deltaTime);
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // 辅助组件类 (内联定义, 避免额外文件)
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>输入组件: 管理 Action/Axis 映射绑定。</summary>
    public class InputComponent : ActorComponent
    {
        /// <summary>Action 绑定字典 (ActionName -> 回调)。</summary>
        public readonly System.Collections.Generic.Dictionary<string, Action> ActionBindings
            = new System.Collections.Generic.Dictionary<string, Action>();

        /// <summary>Axis 绑定字典 (AxisName -> 回调)。</summary>
        public readonly System.Collections.Generic.Dictionary<string, Action<float>> AxisBindings
            = new System.Collections.Generic.Dictionary<string, Action<float>>();

        /// <summary>绑定 Action 按键事件。</summary>
        public void BindAction(string actionName, Action callback)
        {
            ActionBindings[actionName] = callback;
        }

        /// <summary>绑定 Axis 轴事件。</summary>
        public void BindAxis(string axisName, Action<float> callback)
        {
            AxisBindings[axisName] = callback;
        }

        /// <summary>触发 Action。</summary>
        public void TriggerAction(string actionName)
        {
            if (ActionBindings.TryGetValue(actionName, out var cb))
                cb?.Invoke();
        }

        /// <summary>触发 Axis。</summary>
        public void TriggerAxis(string axisName, float value)
        {
            if (AxisBindings.TryGetValue(axisName, out var cb))
                cb?.Invoke(value);
        }
    }

    /// <summary>
    /// 角色移动组件 (类似 UE CharacterMovementComponent)。
    /// 对 Pawn 的移动参数提供集中管理。
    /// </summary>
    public class CharacterMovementComponent : ActorComponent
    {
        /// <summary>最大行走速度。</summary>
        public float MaxWalkSpeed { get; set; } = 5f;

        /// <summary>最大奔跑速度。</summary>
        public float MaxRunSpeed { get; set; } = 10f;

        /// <summary>最大飞行速度。</summary>
        public float MaxFlySpeed { get; set; } = 8f;

        /// <summary>最大游泳速度。</summary>
        public float MaxSwimSpeed { get; set; } = 3f;

        /// <summary>跳躍 Z 速度。</summary>
        public float JumpZVelocity { get; set; } = 8f;

        /// <summary>重力缩放。</summary>
        public float GravityScale { get; set; } = 1f;

        /// <summary>加速度。</summary>
        public float Acceleration { get; set; } = 20f;

        /// <summary>减速度。</summary>
        public float Deceleration { get; set; } = 10f;

        /// <summary>地面摩擦力。</summary>
        public float GroundFriction { get; set; } = 4f;

        /// <summary>空气阻力。</summary>
        public float AirControl { get; set; } = 0.3f;

        /// <summary>是否启用碰撞。</summary>
        public bool EnablePhysicsCollision { get; set; } = true;

        /// <summary>获取所属 Pawn。</summary>
        public Pawn PawnOwner => Owner as Pawn;
    }

    /// <summary>
    /// AI 感知组件 (类似 UE PawnSensingComponent)。
    /// 提供视野与听觉范围检测。
    /// </summary>
    public class PawnSensingComponent : ActorComponent
    {
        /// <summary>视野角度 (度)。</summary>
        public float SightAngle { get; set; } = 90f;

        /// <summary>最大视距。</summary>
        public float SightRadius { get; set; } = 30f;

        /// <summary>最大听觉范围。</summary>
        public float HearingRadius { get; set; } = 20f;

        /// <summary>感知到目标事件 (目标 Actor)。</summary>
        public event Action<Actor> OnSeePawn;

        /// <summary>听到声音事件 (位置, 制造者)。</summary>
        public event Action<Vector3, Actor> OnHearNoise;

        /// <summary>检测目标是否在视野内。</summary>
        public bool CanSee(Actor target)
        {
            if (target == null || Owner == null) return false;
            Vector3 direction = target.Position - Owner.Position;
            float distance = direction.Length();
            if (distance > SightRadius) return false;

            float angle = MathF.Acos(Vector3.Dot(
                Vector3.Normalize(direction),
                Owner.GetForward())) * (180f / MathF.PI);

            return angle <= SightAngle * 0.5f;
        }

        /// <summary>检测目标是否在听觉范围内。</summary>
        public bool CanHear(Vector3 noiseLocation, out float distance)
        {
            if (Owner == null) { distance = float.MaxValue; return false; }
            distance = Vector3.Distance(Owner.Position, noiseLocation);
            return distance <= HearingRadius;
        }

        /// <summary>报告看到目标。</summary>
        public void ReportSeePawn(Actor target) => OnSeePawn?.Invoke(target);

        /// <summary>报告听到声音。</summary>
        public void ReportHearNoise(Vector3 location, Actor instigator)
            => OnHearNoise?.Invoke(location, instigator);
    }
}
