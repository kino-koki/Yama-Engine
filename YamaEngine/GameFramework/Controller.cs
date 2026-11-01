using System;
using System.Numerics;

namespace YamaEngine.GameFramework
{
    // ════════════════════════════════════════════════════════════════════════
    // yama引擎 - Controller / PlayerController (GameFramework)
    //
    // 类似 UE Controller / PlayerController:
    //   * Controller: 抽象基类, Pawn 的"灵魂"
    //   * PlayerController: 具体实现, 输入绑定 / 视角控制 / Camera 管理
    //   * AIController: AI 控制器
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Controller 抽象基类 —— 决定一个 Pawn 接受什么输入/AI 决策。
    /// 是"灵魂"，Pawn 是"躯体"。
    /// </summary>
    public abstract class Controller : Actor
    {
        // ── 拥有的 Pawn ────────────────────────────────────────────────────

        /// <summary>当前控制的 Pawn。</summary>
        public Pawn PossessedPawn { get; private set; }

        /// <summary>是否拥有 Pawn。</summary>
        public bool HasPawn => PossessedPawn != null;

        // ── 占据/释放 ──────────────────────────────────────────────────────

        /// <summary>占据一个 Pawn (类似 UE Possess)。</summary>
        public virtual void Possess(Pawn pawn)
        {
            if (pawn == null) throw new ArgumentNullException(nameof(pawn));
            UnPossess();

            PossessedPawn = pawn;
            pawn.CurrentController = this;
            OnPossess(pawn);
        }

        /// <summary>释放当前 Pawn (类似 UE UnPossess)。</summary>
        public virtual void UnPossess()
        {
            if (PossessedPawn == null) return;
            var prevPawn = PossessedPawn;
            PossessedPawn.CurrentController = null;
            PossessedPawn = null;
            OnUnPossess(prevPawn);
        }

        /// <summary>占据时回调。</summary>
        protected virtual void OnPossess(Pawn pawn) { }

        /// <summary>释放时回调。</summary>
        protected virtual void OnUnPossess(Pawn pawn) { }
    }

    /// <summary>
    /// 由本地/网络玩家操控的 PlayerController。
    /// 负责输入绑定、视角控制与 Camera 管理。
    /// </summary>
    public class PlayerController : Controller
    {
        // ── 输入 ──────────────────────────────────────────────────────────

        /// <summary>是否阻止输入处理。</summary>
        public bool BlockInput { get; set; }

        /// <summary>输入组件 (管理 Action/Axis 映射)。</summary>
        public InputComponent InputComponent { get; private set; }

        /// <summary>本地玩家标记。</summary>
        public bool IsLocalPlayer { get; set; } = true;

        /// <summary>网络玩家标记。</summary>
        public bool IsRemotePlayer => !IsLocalPlayer;

        /// <summary>玩家索引 (0 = 第一个玩家)。</summary>
        public int PlayerIndex { get; set; }

        /// <summary>玩家名称。</summary>
        public string PlayerName { get; set; } = "Player";

        // ── 玩家状态 ──────────────────────────────────────────────────────

        /// <summary>玩家状态数据引用 (分数/击杀/死亡等)。</summary>
        public PlayerStateData PlayerStateRef { get; set; } = new PlayerStateData();

        /// <summary>玩家所在队伍索引 (-1 表示无队伍)。</summary>
        public int Team { get; set; } = -1;

        /// <summary>玩家是否存活。</summary>
        public bool IsAlive { get; set; } = true;

        // ── 视角 ──────────────────────────────────────────────────────────

        /// <summary>当前视角目标。</summary>
        public Actor ViewTarget { get; set; }

        /// <summary>视角混合进度 (0~1)。</summary>
        public float ViewBlendTime { get; set; }

        /// <summary>视角混合持续时间。</summary>
        public float ViewBlendDuration { get; set; } = 0.5f;

        /// <summary>相机管理器。</summary>
        public CameraManager CameraManager { get; private set; }

        // ── 视线/旋转 ────────────────────────────────────────────────────

        /// <summary>控制旋转 (偏航, 用于视角旋转)。</summary>
        public float ControlYaw { get; set; }

        /// <summary>控制旋转 (俯仰, 用于视角旋转)。</summary>
        public float ControlPitch { get; set; }

        /// <summary>鼠标/摇杆灵敏度 (X=水平, Y=垂直)。</summary>
        public Vector2 LookSensitivity { get; set; } = new Vector2(1f, 1f);

        // ── 初始化 ────────────────────────────────────────────────────────

        protected override void OnInitialize()
        {
            base.OnInitialize();
            InputComponent = AddComponent<InputComponent>();
            CameraManager = AddComponent<CameraManager>();
            SetupInputComponent();
        }

        // ── 输入系统 ──────────────────────────────────────────────────────

        /// <summary>
        /// 设置输入绑定 (虚方法, 开发者重写以实现具体映射)。
        /// 默认绑定标准的 MoveX/MoveY/LookX/LookY/Action 映射。
        /// </summary>
        public virtual void SetupInputComponent()
        {
            if (InputComponent == null) return;

            // Action 映射 (按键事件)
            InputComponent.BindAction("Jump", OnJumpAction);
            InputComponent.BindAction("Fire", OnFireAction);
            InputComponent.BindAction("Interact", OnInteractAction);
            InputComponent.BindAction("Run", OnRunAction);

            // Axis 映射 (轴事件)
            InputComponent.BindAxis("MoveX", OnMoveXAxis);
            InputComponent.BindAxis("MoveY", OnMoveYAxis);
            InputComponent.BindAxis("LookX", OnLookXAxis);
            InputComponent.BindAxis("LookY", OnLookYAxis);
        }

        /// <summary>处理输入命令 (由游戏循环每帧调用)。</summary>
        public void ProcessInput(float deltaTime)
        {
            if (BlockInput || PossessedPawn == null) return;
        }

        // ── 默认 Action 处理 ─────────────────────────────────────────────

        protected virtual void OnJumpAction()
        {
            if (PossessedPawn is Pawn pawn && !BlockInput)
                pawn.Jump();
        }

        protected virtual void OnFireAction()
        {
            // 子类重写
        }

        protected virtual void OnInteractAction()
        {
            // 子类重写
        }

        protected virtual void OnRunAction()
        {
            if (PossessedPawn is Pawn pawn && !BlockInput)
                pawn.StartRunning();
        }

        // ── 默认 Axis 处理 ───────────────────────────────────────────────

        protected virtual void OnMoveXAxis(float value)
        {
            if (PossessedPawn != null && !BlockInput && MathF.Abs(value) > 0.1f)
                PossessedPawn.AddMovementInput(PossessedPawn.GetRight(), value);
        }

        protected virtual void OnMoveYAxis(float value)
        {
            if (PossessedPawn != null && !BlockInput && MathF.Abs(value) > 0.1f)
                PossessedPawn.AddMovementInput(PossessedPawn.GetForward(), value);
        }

        protected virtual void OnLookXAxis(float value)
        {
            if (!BlockInput && MathF.Abs(value) > 0.1f)
                ControlYaw += value * LookSensitivity.X;
        }

        protected virtual void OnLookYAxis(float value)
        {
            if (!BlockInput && MathF.Abs(value) > 0.1f)
            {
                ControlPitch += value * LookSensitivity.Y;
                ControlPitch = Math.Clamp(ControlPitch, -89f, 89f);
            }
        }

        // ── 视角管理 ──────────────────────────────────────────────────────

        /// <summary>获取当前视角目标。默认返回持有 Pawn, 否则返回自身。</summary>
        public Actor GetViewTarget()
        {
            if (ViewTarget != null && ViewBlendTime < ViewBlendDuration)
                return ViewTarget;
            return PossessedPawn ?? (Actor)this;
        }

        /// <summary>设置视角目标 (支持平滑混合)。</summary>
        public void SetViewTarget(Actor newTarget, float blendTime = 0.5f)
        {
            ViewTarget = newTarget;
            ViewBlendTime = 0f;
            ViewBlendDuration = MathF.Max(0f, blendTime);
        }

        // ── 占据/释放 ──────────────────────────────────────────────────────

        protected override void OnPossess(Pawn pawn)
        {
            // 占据后自动设置视角目标为 Pawn
            ViewTarget = pawn;

            // 调用 Pawn 的输入设置
            pawn.SetupPlayerInputComponent(InputComponent);
        }

        protected override void OnUnPossess(Pawn pawn)
        {
            // 释放后视角回到自身
            ViewTarget = this;
        }
    }

    /// <summary>
    /// AI 控制器 (类似 UE AIController)。
    /// 由行为树/状态树驱动 Pawn 的行为。
    /// </summary>
    public class AIController : Controller
    {
        /// <summary>行为树资产路径。</summary>
        public string BehaviorTreePath { get; set; }

        /// <summary>AI 感知组件。</summary>
        public PawnSensingComponent SensingComponent { get; private set; }

        /// <summary>当前目标 Actor。</summary>
        public Actor CurrentTarget { get; set; }

        /// <summary>巡逻点列表。</summary>
        public Vector3[] PatrolPoints { get; set; }

        /// <summary>当前巡逻索引。</summary>
        public int CurrentPatrolIndex { get; set; }

        protected override void OnInitialize()
        {
            base.OnInitialize();
            Name = "AIController";
            SensingComponent = AddComponent<PawnSensingComponent>();
        }

        /// <summary>设置 AI 移动到目标位置。</summary>
        public void MoveToLocation(Vector3 location, float acceptanceRadius = 1f)
        {
            if (PossessedPawn == null) return;
            Vector3 direction = location - PossessedPawn.Position;
            if (direction.LengthSquared() > acceptanceRadius * acceptanceRadius)
            {
                PossessedPawn.AddMovementInput(Vector3.Normalize(direction));
            }
        }

        /// <summary>设置 AI 追逐目标。</summary>
        public void MoveToActor(Actor target, float acceptanceRadius = 1f)
        {
            if (target != null)
                MoveToLocation(target.Position, acceptanceRadius);
        }

        /// <summary>巡逻到下一个巡逻点。</summary>
        public void PatrolNext()
        {
            if (PatrolPoints == null || PatrolPoints.Length == 0) return;
            CurrentPatrolIndex = (CurrentPatrolIndex + 1) % PatrolPoints.Length;
            MoveToLocation(PatrolPoints[CurrentPatrolIndex]);
        }
    }

    /// <summary>
    /// 相机管理器占位组件。由 Controller 持有并作为 ActorComponent 挂载，
    /// 具体相机机制由渲染层实现。
    /// </summary>
    public sealed class CameraManager : ActorComponent
    {
    }
}
