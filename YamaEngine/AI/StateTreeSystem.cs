using System;
using System.Collections.Generic;
using System.Numerics;
using YamaEngine.ECS;
using YamaEngine.Physics;

namespace YamaEngine.AI
{
    // ══════════════════════════════════════════════════════════════════
    //  StateTreeSystem.cs — yama引擎 状态树（StateTree）AI 决策系统
    //
    //  本文件对齐 Unreal Engine 5 的 StateTree（状态树）系统。StateTree 是
    //  Epic 在 UE5 中引入的一种新型 AI 决策结构，它结合了：
    //    - 状态机（State Machine）的清晰状态划分与转移逻辑
    //    - 行为树（Behavior Tree）的任务/条件组合灵活性
    //  相比传统行为树，StateTree 具有以下优势：
    //    1. 扁平化的状态组织：状态间通过命名转移连接，无需深层嵌套树，
    //       降低了维护复杂度，调试更直观。
    //    2. 状态局部作用域：每个状态拥有独立的任务集合与进入条件，
    //       任务在状态进入时激活、退出时停用，生命周期清晰。
    //    3. 高效的条件评估：转移条件按优先级排序，首个满足即触发，
    //       避免行为树式的全树遍历。
    //    4. 显式的状态转移：转移目标明确（GotoState/NextState 等），
    //       比行为树的隐式回溯更易预测与控制。
    //
    //  本系统与引擎现有模块对接：
    //    - ECS（YamaEngine.ECS）：通过 StateTreeComponent 组件与
    //      StateTreeSystem 系统集成，复用 TransformComponent / TagComponent。
    //    - Blackboard（YamaEngine.AI.BehaviorTreeSystem）：直接复用已有的
    //      Blackboard 类作为状态树的上下文存储（条件参数、目标信息等），
    //      不重新定义，保持与行为树的数据互通。
    //    - NavigationSystem（YamaEngine.AI）：调用 FindPath 进行 NavMesh 寻路。
    //    - PhysicsWorld（YamaEngine.Physics）：调用 Raycast / OverlapSphere
    //      进行视线检测与范围感知查询。
    //
    //  本文件包含：
    //    1. 枚举：EStateTreeTransitionType / EStateTreeRunStatus
    //    2. 句柄：StateTreeHandle（绑定 AI 实体与状态树实例）
    //    3. 任务基类 StateTreeTask 及 7 个内置任务
    //    4. 条件基类 StateTreeCondition 及 7 个内置条件
    //    5. 状态节点 StateTreeState、转移规则 StateTreeTransition
    //    6. 状态树资产 StateTreeAsset（可序列化的状态集合）
    //    7. 运行时上下文 StateTreeContext（携带 ECS/导航/物理/黑板引用）
    //    8. 运行时实例 StateTreeInstance（每实体一个，驱动 Tick 与转移）
    //    9. ECS 集成：StateTreeComponent（struct : IComponent）
    //   10. ECS 系统：StateTreeSystem（: ISystem，统一驱动所有实例）
    //   11. 调试器：StateTreeDebugger（供编辑器绘制状态/任务信息）
    // ══════════════════════════════════════════════════════════════════

    // ──────────────────────────────────────────────────────────────
    // 枚举定义
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 状态树转移类型。对齐 UE5 的 EStateTreeTransitionType。
    /// 描述当转移条件满足时，状态树应执行何种状态切换动作。
    /// </summary>
    public enum EStateTreeTransitionType
    {
        /// <summary>
        /// 跳转到指定名称的状态（GotoState）。这是最常用的转移类型，
        /// 直接将当前状态切换到 TargetState 指定的目标状态。
        /// </summary>
        GotoState,

        /// <summary>
        /// 切换到下一个状态（NextState）。按状态在资产中的声明顺序，
        /// 切换到当前状态的下一个兄弟状态。常用于线性状态序列。
        /// </summary>
        NextState,

        /// <summary>
        /// 重启状态树（Restart）。从 RootState 重新开始执行，
        /// 相当于重置整个决策流程。常用于异常恢复或周期重置。
        /// </summary>
        Restart,

        /// <summary>
        /// 重新进入当前状态（ReenterState）。不切换到其他状态，
        /// 而是先 Exit 当前状态再重新 Enter，刷新状态局部数据。
        /// 常用于条件变化后需要重新初始化任务的场景。
        /// </summary>
        ReenterState,

        /// <summary>
        /// 保持当前状态（Stay）。条件满足时不发生转移，
        /// 仅用于条件表达中的占位/占位逻辑，实际不改变状态。
        /// </summary>
        Stay,

        /// <summary>
        /// 成功结束状态树（Succeeded）。整个状态树以成功状态终止，
        /// 不再继续 Tick。常用于任务完成的最终态。
        /// </summary>
        Succeeded,

        /// <summary>
        /// 失败结束状态树（Failed）。整个状态树以失败状态终止，
        /// 不再继续 Tick。常用于不可恢复的错误态。
        /// </summary>
        Failed,
    }

    /// <summary>
    /// 状态树运行状态。对齐 UE5 的 EStateTreeRunStatus。
    /// 描述状态树实例或单个任务当前的执行状态。
    /// </summary>
    public enum EStateTreeRunStatus
    {
        /// <summary>运行中：状态树/任务正在执行，尚未完成，需要后续帧继续推进。</summary>
        Running,

        /// <summary>成功：状态树/任务已成功完成。</summary>
        Succeeded,

        /// <summary>失败：状态树/任务执行失败或条件不满足。</summary>
        Failed,

        /// <summary>未初始化：状态树尚未启动或已被重置，等待首次 Start/Bind。</summary>
        Uninitialized,
    }

    // ══════════════════════════════════════════════════════════════════
    // StateTreeHandle —— 状态树实例句柄
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 状态树实例句柄。将一个 StateTreeInstance 运行时实例绑定到一个 AI 实体。
    /// 对齐 UE5 中 FStateTreeInstance 与 Actor 的绑定关系。
    /// 句柄封装了实体 ID 与实例引用，提供便捷的状态查询与控制接口，
    /// 供游戏逻辑代码（如 Lua/蓝图脚本）操控 AI 的状态树。
    /// </summary>
    public struct StateTreeHandle
    {
        /// <summary>绑定的 AI 实体 ID（ECS EntityId）</summary>
        public int EntityId;

        /// <summary>绑定的状态树运行时实例引用</summary>
        public StateTreeInstance? Instance;

        /// <summary>构造句柄，绑定实体与实例。</summary>
        public StateTreeHandle(int entityId, StateTreeInstance instance)
        {
            EntityId = entityId;
            Instance = instance;
        }

        /// <summary>句柄是否有效（实体存在且实例已绑定）。</summary>
        public readonly bool IsValid => Instance != null && EntityId > 0;

        /// <summary>获取当前状态名（调试/查询用）。</summary>
        public readonly string GetCurrentState()
            => Instance != null ? Instance.GetCurrentState() : string.Empty;

        /// <summary>获取运行状态。</summary>
        public readonly EStateTreeRunStatus GetStatus()
            => Instance != null ? Instance.GetStatus() : EStateTreeRunStatus.Uninitialized;

        /// <summary>启动状态树。</summary>
        public void Start() => Instance?.Start();

        /// <summary>停止状态树。</summary>
        public void Stop() => Instance?.Stop();

        /// <summary>重置状态树到初始状态。</summary>
        public void Reset() => Instance?.Reset();
    }

    // ══════════════════════════════════════════════════════════════════
    // StateTreeTask —— 任务基类
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 状态树任务基类。对齐 UE5 的 UStateTreeTask。
    /// 任务是状态树中执行实际行为的最小单元（如移动、等待、播放动画）。
    /// 任务的生命周期由所属状态管理：
    ///   - 当状态被进入（Enter）时，该状态下所有任务的 Enter 被调用以激活；
    ///   - 每帧 Tick 时，激活状态下的任务 Tick 被调用以推进行为；
    ///   - 当状态被退出（Exit）时，所有任务的 Exit 被调用以停用/清理。
    /// 任务通过返回 EStateTreeRunStatus 报告自身执行进度，
    /// 状态树据此聚合出状态整体状态，并可能触发转移评估。
    /// </summary>
    public abstract class StateTreeTask
    {
        /// <summary>任务名称（调试与序列化用）</summary>
        public string Name = "";

        /// <summary>任务是否已激活（状态进入后激活，退出后停用）</summary>
        public bool bIsActive;

        /// <summary>
        /// 任务进入/激活时调用。对齐 UE5 的 EnterState。
        /// 在此初始化任务运行时状态（如重置计时器、请求路径）。
        /// </summary>
        /// <param name="ctx">运行时上下文</param>
        public virtual void Enter(StateTreeContext ctx)
        {
            bIsActive = true;
        }

        /// <summary>
        /// 每帧推进任务。对齐 UE5 的 TickState。
        /// 在此推进行为逻辑（如沿路径移动、累计等待时间），并返回当前状态。
        /// </summary>
        /// <param name="ctx">运行时上下文</param>
        /// <returns>任务运行状态</returns>
        public abstract EStateTreeRunStatus Tick(StateTreeContext ctx);

        /// <summary>
        /// 任务退出/停用时调用。对齐 UE5 的 ExitState。
        /// 在此清理运行时状态（如停止移动、释放资源）。
        /// </summary>
        /// <param name="ctx">运行时上下文</param>
        /// <param name="status">退出时的运行状态</param>
        public virtual void Exit(StateTreeContext ctx, EStateTreeRunStatus status)
        {
            bIsActive = false;
        }

        /// <summary>任务状态的可读描述（调试用）。</summary>
        public virtual string GetDebugInfo() => bIsActive ? "Active" : "Inactive";
    }

    // ──────────────────────────────────────────────────────────────
    // 内置任务实现
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 移动到目标任务（MoveToTask）。对齐 UE5 的 UStateTreeMoveToTask。
    /// 与 NavigationSystem 集成：
    ///   1. 从黑板读取目标位置（或目标实体的位置）；
    ///   2. 调用 NavigationSystem.FindPath 获取 NavMesh 路径；
    ///   3. 沿路径点逐段移动实体（写回 ECS TransformComponent）；
    ///   4. 抵达终点返回 Succeeded；寻路失败返回 Failed；移动中返回 Running。
    /// 当目标位置变化超过阈值时自动重新规划路径。
    /// </summary>
    public sealed class MoveToTask : StateTreeTask
    {
        /// <summary>目标位置的黑板键名</summary>
        public string TargetLocationKey = "TargetLocation";

        /// <summary>目标实体的黑板键名（若设置则优先使用其位置作为目标）</summary>
        public string TargetEntityKey = "TargetEntity";

        /// <summary>移动速度（米/秒）</summary>
        public float MoveSpeed = 4f;

        /// <summary>到达判定半径（米），距离小于此值视为已到达</summary>
        public float AcceptanceRadius = 0.5f;

        /// <summary>目标位置变化超过此距离时重新规划路径</summary>
        public float RepathThreshold = 1f;

        // 运行时路径数据
        private List<Vector3>? _path;
        private int _pathIndex;
        private Vector3 _lastTarget;
        private bool _hasLastTarget;

        public MoveToTask()
        {
            Name = "MoveTo";
        }

        public override void Enter(StateTreeContext ctx)
        {
            base.Enter(ctx);
            // 进入状态时清空旧路径，强制首帧重新规划
            _path = null;
            _pathIndex = 0;
            _hasLastTarget = false;
        }

        public override EStateTreeRunStatus Tick(StateTreeContext ctx)
        {
            // 依赖检查：必须有 ECS 与导航系统
            if (ctx.ECS == null || ctx.Navigation == null)
                return EStateTreeRunStatus.Failed;
            if (!ctx.ECS.HasEntity(ctx.EntityId))
                return EStateTreeRunStatus.Failed;

            // 获取实体变换
            var tf = ctx.ECS.GetComponent<TransformComponent>(ctx.EntityId);

            // 解析目标位置：优先目标实体，其次目标位置
            Vector3 target;
            int targetEntity = ctx.Blackboard.GetEntityId(TargetEntityKey);
            if (targetEntity != 0 && ctx.ECS.HasEntity(targetEntity))
            {
                var targetTf = ctx.ECS.GetComponent<TransformComponent>(targetEntity);
                target = targetTf.Position;
            }
            else
            {
                target = ctx.Blackboard.GetVector3(TargetLocationKey);
            }

            // 目标变化时重新规划路径
            bool needRepath = _path == null;
            if (_hasLastTarget && Vector3.Distance(_lastTarget, target) > RepathThreshold)
                needRepath = true;

            if (needRepath)
            {
                _path = ctx.Navigation.FindPath(tf.Position, target);
                _pathIndex = 0;
                _lastTarget = target;
                _hasLastTarget = true;
                if (_path == null || _path.Count == 0)
                {
                    // 寻路失败：无法到达目标
                    return EStateTreeRunStatus.Failed;
                }
            }

            // 沿路径点移动
            if (_path != null && _pathIndex < _path.Count)
            {
                Vector3 waypoint = _path[_pathIndex];
                Vector3 toWp = waypoint - tf.Position;
                toWp.Y = 0f; // 在水平面上移动
                float dist = toWp.Length();

                if (dist < AcceptanceRadius)
                {
                    _pathIndex++;
                }
                else
                {
                    Vector3 dir = Vector3.Normalize(toWp);
                    tf.Position += dir * MoveSpeed * ctx.DeltaTime;
                    // 朝向移动方向（ yaw = atan2(x, -z) ）
                    if (toWp != Vector3.Zero)
                    {
                        tf.Rotation = Quaternion.CreateFromYawPitchRoll(
                            (float)Math.Atan2(dir.X, -dir.Z), 0f, 0f);
                    }
                    ctx.ECS.SetComponent(ctx.EntityId, tf);
                }
            }

            // 是否抵达最终目标
            Vector3 toTarget = target - tf.Position;
            toTarget.Y = 0f;
            if (toTarget.Length() <= AcceptanceRadius || (_path != null && _pathIndex >= _path.Count))
            {
                return EStateTreeRunStatus.Succeeded;
            }

            return EStateTreeRunStatus.Running;
        }

        public override void Exit(StateTreeContext ctx, EStateTreeRunStatus status)
        {
            // 退出时清空路径，下次进入重新规划
            _path = null;
            _pathIndex = 0;
            _hasLastTarget = false;
            base.Exit(ctx, status);
        }
    }

    /// <summary>
    /// 等待任务（WaitTask）。对齐 UE5 的 UStateTreeWaitTask。
    /// 在状态中等待指定时长后返回 Succeeded。常用于：
    ///   - 攻击间隔冷却
    ///   - 巡逻点停留
    ///   - 行为节拍控制
    /// </summary>
    public sealed class WaitTask : StateTreeTask
    {
        /// <summary>等待时长（秒）</summary>
        public float WaitDuration = 1f;

        // 已等待时间
        private float _elapsed;

        public WaitTask()
        {
            Name = "Wait";
        }

        public override void Enter(StateTreeContext ctx)
        {
            base.Enter(ctx);
            _elapsed = 0f;
        }

        public override EStateTreeRunStatus Tick(StateTreeContext ctx)
        {
            _elapsed += ctx.DeltaTime;
            if (_elapsed >= WaitDuration)
                return EStateTreeRunStatus.Succeeded;
            return EStateTreeRunStatus.Running;
        }

        public override string GetDebugInfo()
            => $"{Name} ({_elapsed:F1}/{WaitDuration:F1}s)";
    }

    /// <summary>
    /// 转向目标方向任务（RotateToTask）。对齐 UE5 的转向任务。
    /// 使实体平滑转向指定方向（黑板中的目标位置或目标实体），
    /// 转向完成后返回 Succeeded。移动中可同步转向，也可单独用于"面朝目标"。
    /// </summary>
    public sealed class RotateToTask : StateTreeTask
    {
        /// <summary>目标位置黑板键</summary>
        public string TargetLocationKey = "TargetLocation";

        /// <summary>目标实体黑板键（优先）</summary>
        public string TargetEntityKey = "TargetEntity";

        /// <summary>角速度（弧度/秒）</summary>
        public float RotateSpeed = 3f;

        /// <summary>完成判定角度（度），小于此值视为已转向</summary>
        public float AcceptanceAngle = 5f;

        public RotateToTask()
        {
            Name = "RotateTo";
        }

        public override EStateTreeRunStatus Tick(StateTreeContext ctx)
        {
            if (ctx.ECS == null || !ctx.ECS.HasEntity(ctx.EntityId))
                return EStateTreeRunStatus.Failed;

            var tf = ctx.ECS.GetComponent<TransformComponent>(ctx.EntityId);

            // 解析目标位置
            Vector3 target;
            int targetEntity = ctx.Blackboard.GetEntityId(TargetEntityKey);
            if (targetEntity != 0 && ctx.ECS.HasEntity(targetEntity))
            {
                var targetTf = ctx.ECS.GetComponent<TransformComponent>(targetEntity);
                target = targetTf.Position;
            }
            else
            {
                target = ctx.Blackboard.GetVector3(TargetLocationKey);
            }

            // 计算期望朝向（水平面）
            Vector3 toTarget = target - tf.Position;
            toTarget.Y = 0f;
            if (toTarget.LengthSquared() < 1e-6f)
                return EStateTreeRunStatus.Succeeded;

            float desiredYaw = (float)Math.Atan2(toTarget.X, -toTarget.Z);

            // 当前朝向 yaw
            Vector3 forward = tf.Forward;
            float currentYaw = (float)Math.Atan2(forward.X, -forward.Z);

            // 角度差，归一化到 [-PI, PI]
            float yawDiff = desiredYaw - currentYaw;
            while (yawDiff > Math.PI) yawDiff -= 2f * (float)Math.PI;
            while (yawDiff < -Math.PI) yawDiff += 2f * (float)Math.PI;

            float acceptanceRad = AcceptanceAngle * (float)Math.PI / 180f;
            if (Math.Abs(yawDiff) <= acceptanceRad)
                return EStateTreeRunStatus.Succeeded;

            // 按角速度步进
            float step = Math.Sign(yawDiff) * Math.Min(RotateSpeed * ctx.DeltaTime, Math.Abs(yawDiff));
            float newYaw = currentYaw + step;
            tf.Rotation = Quaternion.CreateFromYawPitchRoll(newYaw, 0f, 0f);
            ctx.ECS.SetComponent(ctx.EntityId, tf);

            return EStateTreeRunStatus.Running;
        }
    }

    /// <summary>
    /// 设置黑板值任务（SetBlackboardTask）。对齐 UE5 的 SetBlackboard 任务。
    /// 在状态进入时（或每帧）向黑板写入指定值，用于串联决策链、
    /// 标记状态标志、传递参数给后续状态。
    /// 本任务在 Enter 时执行一次写入并立即返回 Succeeded。
    /// </summary>
    public sealed class SetBlackboardTask : StateTreeTask
    {
        /// <summary>黑板键名</summary>
        public string Key = "";

        /// <summary>要写入的值类型</summary>
        public BlackboardValueType ValueType = BlackboardValueType.Boolean;

        /// <summary>布尔值</summary>
        public bool BoolValue;

        /// <summary>整数值</summary>
        public int IntValue;

        /// <summary>浮点值</summary>
        public float FloatValue;

        /// <summary>字符串值</summary>
        public string StringValue = "";

        /// <summary>三维向量值</summary>
        public Vector3 Vector3Value;

        /// <summary>实体 ID 值</summary>
        public int EntityIdValue;

        public SetBlackboardTask()
        {
            Name = "SetBlackboard";
        }

        public override void Enter(StateTreeContext ctx)
        {
            base.Enter(ctx);
            // 根据值类型写入黑板
            switch (ValueType)
            {
                case BlackboardValueType.Boolean:
                    ctx.Blackboard.SetBool(Key, BoolValue);
                    break;
                case BlackboardValueType.Integer:
                    ctx.Blackboard.SetInt(Key, IntValue);
                    break;
                case BlackboardValueType.Float:
                    ctx.Blackboard.SetFloat(Key, FloatValue);
                    break;
                case BlackboardValueType.String:
                    ctx.Blackboard.SetString(Key, StringValue);
                    break;
                case BlackboardValueType.Vector3:
                    ctx.Blackboard.SetVector3(Key, Vector3Value);
                    break;
                case BlackboardValueType.EntityId:
                    ctx.Blackboard.SetEntityId(Key, EntityIdValue);
                    break;
            }
        }

        public override EStateTreeRunStatus Tick(StateTreeContext ctx)
        {
            // 写入在 Enter 已完成，Tick 直接成功
            return EStateTreeRunStatus.Succeeded;
        }
    }

    /// <summary>
    /// 黑板值类型枚举（用于 SetBlackboardTask 等需要区分类型的场景）。
    /// 与 BTBlackboardType 对应，但独立定义以避免依赖行为树内部类型。
    /// </summary>
    public enum BlackboardValueType
    {
        /// <summary>布尔</summary>
        Boolean,
        /// <summary>整数</summary>
        Integer,
        /// <summary>浮点</summary>
        Float,
        /// <summary>字符串</summary>
        String,
        /// <summary>三维向量</summary>
        Vector3,
        /// <summary>实体 ID</summary>
        EntityId,
    }

    /// <summary>
    /// 播放动画状态任务（PlayAnimationStateTask）。对齐 UE5 的动画播放任务。
    /// 通过黑板通知动画系统播放指定动画状态标记，并持续指定时长。
    /// 实际的动画触发由 AnimationSystem 监听黑板键实现（解耦设计）。
    /// 播放完成后返回 Succeeded；若 bLoop 为真则持续 Running。
    /// </summary>
    public sealed class PlayAnimationStateTask : StateTreeTask
    {
        /// <summary>动画状态标记名（对应 AnimationStateMachine 的状态名）</summary>
        public string AnimationState = "";

        /// <summary>播放时长（秒），非循环模式下到时返回 Succeeded</summary>
        public float Duration = 1f;

        /// <summary>是否循环播放</summary>
        public bool bLoop;

        // 已播放时间
        private float _elapsed;

        public PlayAnimationStateTask()
        {
            Name = "PlayAnimationState";
        }

        public override void Enter(StateTreeContext ctx)
        {
            base.Enter(ctx);
            _elapsed = 0f;
            // 通过黑板通知动画系统切换到指定状态
            ctx.Blackboard.SetString("CurrentAnimationState", AnimationState);
            ctx.Blackboard.SetBool("AnimationLooping", bLoop);
        }

        public override EStateTreeRunStatus Tick(StateTreeContext ctx)
        {
            if (bLoop)
            {
                // 循环模式：永远 Running，由状态转移来退出
                _elapsed += ctx.DeltaTime;
                return EStateTreeRunStatus.Running;
            }

            _elapsed += ctx.DeltaTime;
            if (_elapsed >= Duration)
                return EStateTreeRunStatus.Succeeded;
            return EStateTreeRunStatus.Running;
        }

        public override string GetDebugInfo()
            => $"{Name} [{AnimationState}] ({_elapsed:F1}/{Duration:F1}s)";
    }

    /// <summary>
    /// 看向目标任务（LookAtTask）。对齐 UE5 的 LookAt 任务。
    /// 使实体头部/视线朝向目标，但不改变身体朝向（与 RotateToTask 区分）。
    /// 此处通过黑板写入"注视目标"键，由动画系统/IK 系统读取并驱动头部骨骼。
    /// </summary>
    public sealed class LookAtTask : StateTreeTask
    {
        /// <summary>注视目标位置的黑板键</summary>
        public string TargetLocationKey = "TargetLocation";

        /// <summary>注视目标实体的黑板键（优先）</summary>
        public string TargetEntityKey = "TargetEntity";

        /// <summary>是否持续每帧更新注视目标（false 则仅在 Enter 时设置一次）</summary>
        public bool bTrackTarget = true;

        public LookAtTask()
        {
            Name = "LookAt";
        }

        public override void Enter(StateTreeContext ctx)
        {
            base.Enter(ctx);
            ctx.Blackboard.SetBool("IsLookingAt", true);
            UpdateLookAtTarget(ctx);
        }

        public override EStateTreeRunStatus Tick(StateTreeContext ctx)
        {
            if (bTrackTarget)
                UpdateLookAtTarget(ctx);
            // 看向目标是一个持续状态，除非转移条件触发否则一直 Running
            return EStateTreeRunStatus.Running;
        }

        public override void Exit(StateTreeContext ctx, EStateTreeRunStatus status)
        {
            // 退出时清除注视标志
            ctx.Blackboard.SetBool("IsLookingAt", false);
            base.Exit(ctx, status);
        }

        private void UpdateLookAtTarget(StateTreeContext ctx)
        {
            Vector3 target;
            int targetEntity = ctx.Blackboard.GetEntityId(TargetEntityKey);
            if (targetEntity != 0 && ctx.ECS != null && ctx.ECS.HasEntity(targetEntity))
            {
                var targetTf = ctx.ECS.GetComponent<TransformComponent>(targetEntity);
                target = targetTf.Position;
            }
            else
            {
                target = ctx.Blackboard.GetVector3(TargetLocationKey);
            }
            // 写入注视目标位置，供 IK/动画系统读取
            ctx.Blackboard.SetVector3("LookAtTarget", target);
        }
    }

    /// <summary>
    /// 发送消息任务（SendMessageTask）。对齐 UE5 的事件/消息派发任务。
    /// 向目标实体发送一个事件消息，用于实体间通信（如"发现敌人"、
    /// "请求支援"、"受到攻击"等）。本实现通过黑板消息队列派发：
    /// 将消息写入黑板的 "LastMessage" / "LastMessageTarget" 键，
    /// 同时标记 "MessagePending" 为 true。完整的引擎可扩展为事件总线派发。
    /// </summary>
    public sealed class SendMessageTask : StateTreeTask
    {
        /// <summary>消息名称（如 "Alert"、"RequestBackup"）</summary>
        public string MessageName = "";

        /// <summary>目标实体黑板键（0 表示广播给所有）</summary>
        public string TargetEntityKey = "TargetEntity";

        /// <summary>消息携带的浮点参数</summary>
        public float MessageParam;

        public SendMessageTask()
        {
            Name = "SendMessage";
        }

        public override void Enter(StateTreeContext ctx)
        {
            base.Enter(ctx);
            // 写入消息到黑板，供消息系统/其他实体轮询
            int target = ctx.Blackboard.GetEntityId(TargetEntityKey);
            ctx.Blackboard.SetString("LastMessage", MessageName);
            ctx.Blackboard.SetEntityId("LastMessageTarget", target);
            ctx.Blackboard.SetFloat("LastMessageParam", MessageParam);
            ctx.Blackboard.SetBool("MessagePending", true);
        }

        public override EStateTreeRunStatus Tick(StateTreeContext ctx)
        {
            // 消息在 Enter 时已发送，Tick 直接成功
            return EStateTreeRunStatus.Succeeded;
        }
    }

    // ══════════════════════════════════════════════════════════════════
    // StateTreeCondition —— 条件基类
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 状态树条件基类。对齐 UE5 的 UStateTreeConditionBase。
    /// 条件用于评估某个布尔谓词是否成立，是状态转移与状态进入的判定依据。
    /// 与行为树的装饰器不同，StateTree 的条件是"被动查询"的：
    /// 每帧由状态树实例主动调用 Evaluate，而非通过观察者主动通知。
    /// 这种拉取式（pull）评估更高效且无副作用。
    /// </summary>
    public abstract class StateTreeCondition
    {
        /// <summary>条件名称（调试用）</summary>
        public string Name = "";

        /// <summary>
        /// 评估条件是否满足。
        /// </summary>
        /// <param name="ctx">运行时上下文</param>
        /// <returns>true 表示条件成立</returns>
        public abstract bool Evaluate(StateTreeContext ctx);

        /// <summary>条件评估结果的可读描述（调试用）。</summary>
        public virtual string GetDebugInfo() => Name;
    }

    // ──────────────────────────────────────────────────────────────
    // 内置条件实现
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 黑板条件比较运算符。对齐 UE5 的 EStateTreeComparisonOp。
    /// </summary>
    public enum EStateTreeComparisonOp
    {
        /// <summary>相等</summary>
        Equal,
        /// <summary>不等于</summary>
        NotEqual,
        /// <summary>大于</summary>
        Greater,
        /// <summary>小于</summary>
        Less,
        /// <summary>大于等于</summary>
        GreaterEqual,
        /// <summary>小于等于</summary>
        LessEqual,
        /// <summary>在范围内 [Min, Max]</summary>
        InRange,
    }

    /// <summary>
    /// 黑板条件（BlackboardCondition）。对齐 UE5 的 UStateTreeBlackboardCondition。
    /// 对黑板中指定键的值进行比较，支持等于/不等于/大于/小于/范围等运算。
    /// 适用于浮点键与整数键的比较。是状态转移中最常用的条件类型。
    /// </summary>
    public sealed class BlackboardCondition : StateTreeCondition
    {
        /// <summary>要比较的黑板键名</summary>
        public string Key = "";

        /// <summary>比较运算符</summary>
        public EStateTreeComparisonOp Op = EStateTreeComparisonOp.Equal;

        /// <summary>比较基准值（用于 Equal/NotEqual/Greater/Less 等）</summary>
        public float CompareValue;

        /// <summary>范围最小值（用于 InRange）</summary>
        public float RangeMin;

        /// <summary>范围最大值（用于 InRange）</summary>
        public float RangeMax;

        public BlackboardCondition()
        {
            Name = "BlackboardCondition";
        }

        public override bool Evaluate(StateTreeContext ctx)
        {
            if (!ctx.Blackboard.HasKey(Key))
                return false;

            float val = ctx.Blackboard.GetFloat(Key);
            return Op switch
            {
                EStateTreeComparisonOp.Equal => Math.Abs(val - CompareValue) < 1e-5f,
                EStateTreeComparisonOp.NotEqual => Math.Abs(val - CompareValue) >= 1e-5f,
                EStateTreeComparisonOp.Greater => val > CompareValue,
                EStateTreeComparisonOp.Less => val < CompareValue,
                EStateTreeComparisonOp.GreaterEqual => val >= CompareValue,
                EStateTreeComparisonOp.LessEqual => val <= CompareValue,
                EStateTreeComparisonOp.InRange => val >= RangeMin && val <= RangeMax,
                _ => false,
            };
        }
    }

    /// <summary>
    /// 距离条件（DistanceCondition）。对齐 UE5 的距离比较条件。
    /// 评估实体到目标（黑板中的目标实体或目标位置）的距离是否满足比较关系。
    /// 常用于"接近敌人时攻击"、"远离时追击"等距离触发的转移。
    /// </summary>
    public sealed class DistanceCondition : StateTreeCondition
    {
        /// <summary>目标位置黑板键</summary>
        public string TargetLocationKey = "TargetLocation";

        /// <summary>目标实体黑板键（优先）</summary>
        public string TargetEntityKey = "TargetEntity";

        /// <summary>距离比较运算符</summary>
        public EStateTreeComparisonOp Op = EStateTreeComparisonOp.LessEqual;

        /// <summary>距离基准值（米）</summary>
        public float Distance = 5f;

        public DistanceCondition()
        {
            Name = "DistanceCondition";
        }

        public override bool Evaluate(StateTreeContext ctx)
        {
            if (ctx.ECS == null || !ctx.ECS.HasEntity(ctx.EntityId))
                return false;

            var tf = ctx.ECS.GetComponent<TransformComponent>(ctx.EntityId);
            Vector3 target;

            int targetEntity = ctx.Blackboard.GetEntityId(TargetEntityKey);
            if (targetEntity != 0 && ctx.ECS.HasEntity(targetEntity))
            {
                var targetTf = ctx.ECS.GetComponent<TransformComponent>(targetEntity);
                target = targetTf.Position;
            }
            else
            {
                target = ctx.Blackboard.GetVector3(TargetLocationKey);
            }

            float dist = Vector3.Distance(tf.Position, target);
            return Op switch
            {
                EStateTreeComparisonOp.Equal => Math.Abs(dist - Distance) < 0.1f,
                EStateTreeComparisonOp.NotEqual => Math.Abs(dist - Distance) >= 0.1f,
                EStateTreeComparisonOp.Greater => dist > Distance,
                EStateTreeComparisonOp.Less => dist < Distance,
                EStateTreeComparisonOp.GreaterEqual => dist >= Distance,
                EStateTreeComparisonOp.LessEqual => dist <= Distance,
                EStateTreeComparisonOp.InRange => dist >= Distance && dist <= RangeMax,
                _ => false,
            };
        }

        /// <summary>范围最大值（用于 InRange）</summary>
        public float RangeMax = 10f;
    }

    /// <summary>
    /// 是否有目标条件（HasTargetCondition）。
    /// 评估黑板中是否存在有效的目标实体（键已设置且实体存在）。
    /// 常用于"发现目标 -> 进入战斗"的转移门控。
    /// </summary>
    public sealed class HasTargetCondition : StateTreeCondition
    {
        /// <summary>目标实体黑板键</summary>
        public string TargetEntityKey = "TargetEntity";

        public HasTargetCondition()
        {
            Name = "HasTarget";
        }

        public override bool Evaluate(StateTreeContext ctx)
        {
            if (!ctx.Blackboard.HasKey(TargetEntityKey))
                return false;
            int target = ctx.Blackboard.GetEntityId(TargetEntityKey);
            if (target == 0)
                return false;
            // 验证目标实体在 ECS 中仍然存在（未被销毁）
            if (ctx.ECS != null && !ctx.ECS.HasEntity(target))
                return false;
            return true;
        }
    }

    /// <summary>
    /// 生命值阈值条件（HealthCondition）。
    /// 从黑板读取生命值（浮点键），评估是否满足阈值比较关系。
    /// 常用于"生命值低 -> 撤退"、"生命值满 -> 巡逻"等转移。
    /// </summary>
    public sealed class HealthCondition : StateTreeCondition
    {
        /// <summary>生命值黑板键名</summary>
        public string HealthKey = "Health";

        /// <summary>比较运算符</summary>
        public EStateTreeComparisonOp Op = EStateTreeComparisonOp.LessEqual;

        /// <summary>生命值阈值</summary>
        public float Threshold = 30f;

        public HealthCondition()
        {
            Name = "HealthCondition";
        }

        public override bool Evaluate(StateTreeContext ctx)
        {
            if (!ctx.Blackboard.HasKey(HealthKey))
                return false;
            float hp = ctx.Blackboard.GetFloat(HealthKey);
            return Op switch
            {
                EStateTreeComparisonOp.Equal => Math.Abs(hp - Threshold) < 1e-5f,
                EStateTreeComparisonOp.NotEqual => Math.Abs(hp - Threshold) >= 1e-5f,
                EStateTreeComparisonOp.Greater => hp > Threshold,
                EStateTreeComparisonOp.Less => hp < Threshold,
                EStateTreeComparisonOp.GreaterEqual => hp >= Threshold,
                EStateTreeComparisonOp.LessEqual => hp <= Threshold,
                _ => false,
            };
        }
    }

    /// <summary>
    /// 定时器条件（TimerCondition）。
    /// 评估从进入当前状态起是否已经过指定时长。利用 StateContext 的 StateData
    /// 中记录的状态进入时间进行计算。常用于"巡逻停留 N 秒后继续"等时间转移。
    /// </summary>
    public sealed class TimerCondition : StateTreeCondition
    {
        /// <summary>定时器时长（秒）</summary>
        public float Duration = 2f;

        public TimerCondition()
        {
            Name = "TimerCondition";
        }

        public override bool Evaluate(StateTreeContext ctx)
        {
            // StateData.ElapsedTime 记录当前状态已持续时间
            return ctx.StateData.ElapsedTime >= Duration;
        }
    }

    /// <summary>
    /// 实体标签条件（TagCondition）。
    /// 评估目标实体（或自身）是否拥有指定标签（TagComponent.Tag）。
    /// 常用于阵营判断、状态标记、交互资格检查。
    /// </summary>
    public sealed class TagCondition : StateTreeCondition
    {
        /// <summary>要匹配的标签字符串</summary>
        public string Tag = "";

        /// <summary>检查目标实体的黑板键（空则检查自身实体）</summary>
        public string TargetEntityKey = "TargetEntity";

        /// <summary>是否检查自身实体（true 时忽略 TargetEntityKey）</summary>
        public bool bCheckSelf;

        public TagCondition()
        {
            Name = "TagCondition";
        }

        public override bool Evaluate(StateTreeContext ctx)
        {
            if (ctx.ECS == null)
                return false;

            int entityId;
            if (bCheckSelf)
            {
                entityId = ctx.EntityId;
            }
            else
            {
                entityId = ctx.Blackboard.GetEntityId(TargetEntityKey);
                if (entityId == 0)
                    return false;
            }

            if (!ctx.ECS.HasEntity(entityId))
                return false;
            if (!ctx.ECS.HasComponent<TagComponent>(entityId))
                return false;

            var tag = ctx.ECS.GetComponent<TagComponent>(entityId);
            return tag.Tag == Tag;
        }
    }

    /// <summary>
    /// 视线可见条件（LineOfSightCondition）。对齐 UE5 的视线检测条件。
    /// 通过 PhysicsWorld.Raycast 检测从实体到目标之间是否有视线遮挡。
    /// 若射线未命中任何障碍物（或命中的就是目标实体本身），则视为可见。
    /// 常用于"看到敌人 -> 追击/攻击"的感知触发。
    /// </summary>
    public sealed class LineOfSightCondition : StateTreeCondition
    {
        /// <summary>目标实体黑板键</summary>
        public string TargetEntityKey = "TargetEntity";

        /// <summary>目标位置黑板键（当无目标实体时使用）</summary>
        public string TargetLocationKey = "TargetLocation";

        /// <summary>视线检测最大距离（米）</summary>
        public float MaxDistance = 50f;

        /// <summary>视线起始高度偏移（如眼睛高度，相对实体位置）</summary>
        public float EyeHeight = 1.7f;

        public LineOfSightCondition()
        {
            Name = "LineOfSight";
        }

        public override bool Evaluate(StateTreeContext ctx)
        {
            if (ctx.ECS == null || ctx.PhysicsWorld == null)
                return false;
            if (!ctx.ECS.HasEntity(ctx.EntityId))
                return false;

            var tf = ctx.ECS.GetComponent<TransformComponent>(ctx.EntityId);
            Vector3 eyePos = tf.Position + new Vector3(0f, EyeHeight, 0f);

            // 解析目标位置
            Vector3 target;
            int targetEntity = ctx.Blackboard.GetEntityId(TargetEntityKey);
            if (targetEntity != 0 && ctx.ECS.HasEntity(targetEntity))
            {
                var targetTf = ctx.ECS.GetComponent<TransformComponent>(targetEntity);
                target = targetTf.Position + new Vector3(0f, EyeHeight, 0f);
            }
            else
            {
                target = ctx.Blackboard.GetVector3(TargetLocationKey);
            }

            Vector3 toTarget = target - eyePos;
            float dist = toTarget.Length();
            if (dist < 0.01f)
                return true;
            if (dist > MaxDistance)
                return false;

            Vector3 dir = toTarget / dist;

            // 射线检测：使用 Default 层查询所有可碰撞物体
            bool hasHit = ctx.PhysicsWorld.Raycast(eyePos, dir, dist, out var hit,
                PhysicsLayers.All);

            if (!hasHit)
            {
                // 无命中：视线无遮挡
                return true;
            }

            // 命中的是目标实体本身则视为可见
            if (targetEntity != 0 && hit.BodyId != 0
                && ctx.PhysicsWorld.GetEntityId(hit.BodyId) == targetEntity)
                return true;

            // 命中其他物体：视线被遮挡
            return false;
        }
    }

    // ══════════════════════════════════════════════════════════════════
    // StateTreeStateData —— 状态局部运行时数据
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 状态局部运行时数据。对齐 UE5 中状态实例的运行时数据。
    /// 每个状态在运行期间维护一份此类数据，记录进入时间、已持续时长、
    /// Tick 次数等。当状态切换时，旧状态数据被丢弃，新状态数据重新初始化。
    /// </summary>
    public sealed class StateTreeStateData
    {
        /// <summary>进入此状态时的全局时间（秒）</summary>
        public float EnterTime;

        /// <summary>在此状态中已持续的时长（秒）</summary>
        public float ElapsedTime;

        /// <summary>在此状态中已 Tick 的次数</summary>
        public int TickCount;

        /// <summary>自定义数据字典（供任务/条件存取状态局部变量）</summary>
        public readonly Dictionary<string, object> CustomData = new();

        /// <summary>重置状态数据（进入状态时调用）。</summary>
        /// <param name="worldTime">当前全局时间</param>
        public void Reset(float worldTime)
        {
            EnterTime = worldTime;
            ElapsedTime = 0f;
            TickCount = 0;
            CustomData.Clear();
        }

        /// <summary>每帧推进（Tick 时调用）。</summary>
        /// <param name="dt">增量时间</param>
        public void Advance(float dt)
        {
            ElapsedTime += dt;
            TickCount++;
        }

        /// <summary>获取/设置自定义数据。</summary>
        public object this[string key]
        {
            get => CustomData.TryGetValue(key, out var v) ? v : null;
            set => CustomData[key] = value;
        }
    }

    // ══════════════════════════════════════════════════════════════════
    // StateTreeTransition —— 转移规则
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 状态转移规则。对齐 UE5 的 FStateTreeTransition。
    /// 描述从当前状态到目标状态的转移条件与方式。
    /// 每帧（Tick 当前状态任务后）按 Priority 升序评估所有转移规则，
    /// 首个所有条件均满足（AND 逻辑）且 bCanTransition 为真的转移将被触发。
    /// </summary>
    public sealed class StateTreeTransition
    {
        /// <summary>目标状态名（GotoState 时使用）</summary>
        public string TargetState = "";

        /// <summary>触发条件列表，全部满足（AND 逻辑）才触发转移</summary>
        public readonly List<StateTreeCondition> Conditions = new();

        /// <summary>
        /// 优先级，数值越小越先评估。同优先级按列表顺序。
        /// 对齐 UE5 转移的优先级排序机制。
        /// </summary>
        public int Priority;

        /// <summary>转移类型（GotoState / NextState / Restart 等）</summary>
        public EStateTreeTransitionType TransitionType = EStateTreeTransitionType.GotoState;

        /// <summary>
        /// 运行时门控标志。设为 false 可临时禁用此转移，
        /// 而无需从转移列表中移除。对齐 UE5 的 bCanTransition。
        /// </summary>
        public bool bCanTransition = true;

        /// <summary>评估此转移的所有条件是否满足。</summary>
        public bool Evaluate(StateTreeContext ctx)
        {
            if (!bCanTransition)
                return false;
            // AND 逻辑：所有条件均须满足；无条件列表则视为无条件转移（始终满足）
            for (int i = 0; i < Conditions.Count; i++)
            {
                if (!Conditions[i].Evaluate(ctx))
                    return false;
            }
            return true;
        }
    }

    // ══════════════════════════════════════════════════════════════════
    // StateTreeState —— 状态节点
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 状态树状态节点。对齐 UE5 的 FStateTreeState。
    /// 一个状态代表 AI 的一个行为阶段（如"巡逻"、"追击"、"攻击"）。
    /// 每个状态包含：
    ///   - Tasks：该状态激活时执行的任务列表（并行执行，状态进入时激活）
    ///   - Transitions：从此状态出发的转移规则列表（按优先级评估）
    ///   - EnterConditions：进入此状态需满足的前置条件（用于 GotoState 时的门控）
    ///   - bAlwaysTick：是否即使非当前激活状态也每帧 Tick（用于后台持续任务）
    /// </summary>
    public sealed class StateTreeState
    {
        /// <summary>状态名称（在资产内唯一，转移通过此名称引用）</summary>
        public string Name = "";

        /// <summary>该状态下激活的任务列表。状态进入时调用各任务 Enter，退出时 Exit。</summary>
        public readonly List<StateTreeTask> Tasks = new();

        /// <summary>从此状态出发的转移规则列表（按 Priority 升序评估）。</summary>
        public readonly List<StateTreeTransition> Transitions = new();

        /// <summary>进入此状态需满足的前置条件列表（AND 逻辑）。</summary>
        public readonly List<StateTreeCondition> EnterConditions = new();

        /// <summary>
        /// 是否始终 Tick 即使非当前激活状态。
        /// 对齐 UE5 的 bAlwaysTick：用于需要在后台持续运行的任务
        /// （如持续感知、持续动画）。默认 false。
        /// </summary>
        public bool bAlwaysTick;

        /// <summary>评估进入条件是否全部满足。</summary>
        public bool EvaluateEnterConditions(StateTreeContext ctx)
        {
            for (int i = 0; i < EnterConditions.Count; i++)
            {
                if (!EnterConditions[i].Evaluate(ctx))
                    return false;
            }
            return true;
        }

        /// <summary>按优先级排序转移规则（Priority 升序）。</summary>
        public void SortTransitions()
        {
            Transitions.Sort((a, b) => a.Priority.CompareTo(b.Priority));
        }
    }

    // ══════════════════════════════════════════════════════════════════
    // StateTreeAsset —— 状态树资产
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 状态树资产。对齐 UE5 的 UStateTree。
    /// 资产是状态树的可序列化定义，包含所有状态、转移、初始状态等。
    /// 由编辑器构建，运行时由 StateTreeInstance 实例化为运行时实例。
    /// 多个 AI 实体可共享同一资产，各自维护独立的运行时状态。
    /// </summary>
    [Serializable]
    public sealed class StateTreeAsset
    {
        /// <summary>资产名称</summary>
        public string Name = "";

        /// <summary>所有状态，按状态名索引。对齐 UE5 的 States 数组。</summary>
        public readonly Dictionary<string, StateTreeState> States = new();

        /// <summary>初始状态名（状态树启动时进入的第一个状态）</summary>
        public string RootState = "";

        /// <summary>资产是否暂停（编辑器/调试用，运行时由实例控制）</summary>
        public bool bPaused;

        /// <summary>状态名列表（保持声明顺序，用于 NextState 转移）</summary>
        public readonly List<string> StateOrder = new();

        /// <summary>获取指定名称的状态。不存在则返回 null。</summary>
        public StateTreeState? GetState(string name)
            => States.TryGetValue(name, out var s) ? s : null;

        /// <summary>添加一个状态到资产中（同时记录声明顺序）。</summary>
        public void AddState(StateTreeState state)
        {
            States[state.Name] = state;
            if (!StateOrder.Contains(state.Name))
                StateOrder.Add(state.Name);
            // 添加后对转移排序以保证优先级正确
            state.SortTransitions();
        }

        /// <summary>获取指定状态在声明顺序中的下一个状态名（用于 NextState 转移）。</summary>
        public string? GetNextStateName(string currentName)
        {
            int idx = StateOrder.IndexOf(currentName);
            if (idx < 0 || idx + 1 >= StateOrder.Count)
                return null;
            return StateOrder[idx + 1];
        }
    }

    // ══════════════════════════════════════════════════════════════════
    // StateTreeContext —— 运行时上下文
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 状态树运行时上下文。对齐 UE5 的 FStateTreeExecutionContext。
    /// 在每帧 Tick 时构建并传递给任务与条件，携带当前 AI 实体的所有外部依赖，
    /// 使任务/条件无需依赖全局单例即可访问引擎服务（ECS、导航、物理、黑板）。
    /// 同时携带当前状态名与状态局部数据，供任务/条件读取。
    /// </summary>
    public sealed class StateTreeContext
    {
        /// <summary>当前 AI 控制的实体 ID（ECS EntityId）</summary>
        public int EntityId;

        /// <summary>本次 Tick 的增量时间（秒）</summary>
        public float DeltaTime;

        /// <summary>全局累计时间（秒）</summary>
        public float WorldTime;

        /// <summary>ECS 引用（用于读写组件）</summary>
        public EntityComponentSystem? ECS;

        /// <summary>导航系统引用（用于寻路）</summary>
        public NavigationSystem? Navigation;

        /// <summary>物理世界引用（用于射线/范围查询）</summary>
        public PhysicsWorld? PhysicsWorld;

        /// <summary>
        /// 黑板引用。复用 BehaviorTreeSystem 中已有的 Blackboard 类，
        /// 作为状态树的条件参数与目标信息存储，与行为树共享数据协议。
        /// </summary>
        public Blackboard Blackboard;

        /// <summary>当前状态名</summary>
        public string CurrentState = "";

        /// <summary>当前状态的局部运行时数据（进入时间、累计时长等）</summary>
        public StateTreeStateData StateData;

        /// <summary>所属状态树实例引用（供任务回调实例方法）</summary>
        public StateTreeInstance? Instance;

        /// <summary>构造上下文。</summary>
        public StateTreeContext(int entityId, Blackboard blackboard)
        {
            EntityId = entityId;
            Blackboard = blackboard;
            StateData = new StateTreeStateData();
        }
    }

    // ══════════════════════════════════════════════════════════════════
    // StateTreeInstance —— 运行时实例
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 状态树运行时实例。对齐 UE5 的 FStateTreeInstance。
    /// 每个 AI 实体持有独立实例，维护：
    ///   - 绑定的状态树资产
    ///   - 私有黑板（复用 Blackboard 类）
    ///   - 当前激活状态
    ///   - 状态局部数据（StateData）
    ///   - 整体运行状态
    /// 每帧由 StateTreeSystem 调用 Tick，推进当前状态任务并评估转移。
    /// </summary>
    public sealed class StateTreeInstance
    {
        /// <summary>绑定的状态树资产</summary>
        public StateTreeAsset? Asset;

        /// <summary>私有黑板（与行为树共享 Blackboard 类）</summary>
        public Blackboard Blackboard;

        /// <summary>资产名称（调试用）</summary>
        public string AssetName = "";

        /// <summary>是否已启动</summary>
        public bool bStarted;

        /// <summary>是否暂停（暂停时不 Tick）</summary>
        public bool bPaused;

        /// <summary>整体运行状态</summary>
        public EStateTreeRunStatus Status = EStateTreeRunStatus.Uninitialized;

        /// <summary>当前激活状态名（空表示未进入任何状态）</summary>
        public string CurrentStateName = "";

        // 当前激活状态引用
        private StateTreeState? _currentState;

        // 状态局部数据
        private readonly StateTreeStateData _stateData = new();

        // 上一帧的全局时间（用于检测时间回溯/重置）
        private float _lastWorldTime;

        // 缓存的上下文依赖：由 StateTreeSystem 每帧注入，供 Enter/Exit 回调
        // （即使发生在 Start/Stop 等非 Tick 路径中）也能访问 ECS/导航/物理。
        private int _entityId;
        private EntityComponentSystem? _ecs;
        private NavigationSystem? _navigation;
        private PhysicsWorld? _physicsWorld;

        /// <summary>构造实例，绑定资产并创建黑板。</summary>
        public StateTreeInstance(StateTreeAsset? asset, Blackboard blackboard)
        {
            Asset = asset;
            Blackboard = blackboard;
            AssetName = asset?.Name ?? "";
        }

        /// <summary>
        /// 绑定状态树资产。对齐 UE5 的 Init。
        /// 若资产为空或无状态则绑定失败。
        /// </summary>
        /// <param name="asset">要绑定的状态树资产</param>
        /// <returns>是否绑定成功</returns>
        public bool Bind(StateTreeAsset asset)
        {
            Asset = asset;
            AssetName = asset.Name;
            if (asset.States.Count == 0)
                return false;
            Status = EStateTreeRunStatus.Uninitialized;
            return true;
        }

        /// <summary>获取当前状态名。</summary>
        public string GetCurrentState() => CurrentStateName;

        /// <summary>获取运行状态。</summary>
        public EStateTreeRunStatus GetStatus() => Status;

        /// <summary>
        /// 启动状态树。进入 RootState（初始状态）。
        /// 对齐 UE5 的 Start。
        /// </summary>
        public void Start()
        {
            if (Asset == null || Asset.States.Count == 0)
            {
                Status = EStateTreeRunStatus.Failed;
                return;
            }
            bStarted = true;
            bPaused = false;
            Status = EStateTreeRunStatus.Running;
            // 进入初始状态
            EnterState(Asset.RootState);
        }

        /// <summary>停止状态树。退出当前状态，标记为未初始化。</summary>
        public void Stop()
        {
            if (_currentState != null)
            {
                ExitCurrentState(EStateTreeRunStatus.Failed);
            }
            bStarted = false;
            Status = EStateTreeRunStatus.Uninitialized;
        }

        /// <summary>重置状态树到初始状态。等效于 Stop + Start。</summary>
        public void Reset()
        {
            Stop();
            Start();
        }

        /// <summary>
        /// 每帧推进状态树。对齐 UE5 的 Tick。
        /// 流程：
        ///   1. 检查启动/暂停状态；
        ///   2. 推进当前状态局部数据（累计时长）；
        ///   3. Tick 当前状态的所有任务，聚合状态运行状态；
        ///   4. 按优先级评估转移规则，首个满足的转移触发状态切换；
        ///   5. 若无转移触发，根据任务聚合状态决定是否结束（Succeeded/Failed）。
        /// </summary>
        /// <param name="dt">增量时间</param>
        /// <param name="ctx">运行时上下文</param>
        public void Tick(float dt, StateTreeContext ctx)
        {
            // 未启动或已结束则不 Tick
            if (!bStarted || Status != EStateTreeRunStatus.Running)
                return;
            if (bPaused)
                return;
            if (Asset == null || _currentState == null)
                return;

            // 同步上下文状态信息
            ctx.Instance = this;
            ctx.CurrentState = CurrentStateName;
            ctx.DeltaTime = dt;
            ctx.StateData = _stateData;

            // 推进状态局部数据
            _stateData.Advance(dt);

            // ---- 第一步：Tick 当前状态的所有任务 ----
            EStateTreeRunStatus taskStatus = TickStateTasks(ctx, _currentState);

            // ---- 第二步：按优先级评估转移规则 ----
            StateTreeTransition? triggeredTransition = EvaluateTransitions(ctx, _currentState);

            if (triggeredTransition != null)
            {
                // 有转移被触发：执行状态切换
                ProcessTransition(ctx, triggeredTransition, taskStatus);
                return;
            }

            // ---- 第三步：无转移触发，根据任务聚合状态决定状态树状态 ----
            // 若所有任务均成功，状态视为 Succeeded；若有失败，视为 Failed；
            // 否则 Running。Succeeded/Failed 不会自动结束状态树（需转移触发），
            // 但会更新实例状态供外部查询。
            if (taskStatus == EStateTreeRunStatus.Failed)
            {
                // 任务失败：可在此触发默认失败逻辑，此处保持 Running 等待转移
                // （UE5 中任务失败通常通过 Global Transitions 处理）
            }
            // 默认保持 Running
            Status = EStateTreeRunStatus.Running;
        }

        /// <summary>
        /// Tick 状态的所有任务，聚合返回状态运行状态。
        /// 聚合规则：任一任务 Failed -> 整体 Failed；
        ///           所有任务 Succeeded -> 整体 Succeeded；
        ///           否则 Running。
        /// </summary>
        private EStateTreeRunStatus TickStateTasks(StateTreeContext ctx, StateTreeState state)
        {
            bool anyRunning = false;
            bool allSucceeded = state.Tasks.Count > 0;

            for (int i = 0; i < state.Tasks.Count; i++)
            {
                var task = state.Tasks[i];
                if (!task.bIsActive)
                    continue;

                EStateTreeRunStatus s = task.Tick(ctx);
                if (s == EStateTreeRunStatus.Failed)
                    return EStateTreeRunStatus.Failed;
                if (s == EStateTreeRunStatus.Running)
                {
                    anyRunning = true;
                    allSucceeded = false;
                }
                else if (s == EStateTreeRunStatus.Succeeded)
                {
                    // 该任务成功，继续检查其他任务
                }
            }

            if (allSucceeded && !anyRunning)
                return EStateTreeRunStatus.Succeeded;
            return EStateTreeRunStatus.Running;
        }

        /// <summary>
        /// 按优先级评估状态的转移规则，返回首个满足条件的转移。
        /// 转移列表已在 AddState 时按 Priority 排序。
        /// </summary>
        private StateTreeTransition? EvaluateTransitions(StateTreeContext ctx, StateTreeState state)
        {
            for (int i = 0; i < state.Transitions.Count; i++)
            {
                var trans = state.Transitions[i];
                if (trans.Evaluate(ctx))
                    return trans;
            }
            return null;
        }

        /// <summary>
        /// 处理被触发的转移：根据 TransitionType 执行相应的状态切换。
        /// </summary>
        private void ProcessTransition(StateTreeContext ctx, StateTreeTransition trans,
            EStateTreeRunStatus taskStatus)
        {
            switch (trans.TransitionType)
            {
                case EStateTreeTransitionType.GotoState:
                    // 跳转到指定目标状态
                    if (!string.IsNullOrEmpty(trans.TargetState))
                    {
                        SwitchToState(ctx, trans.TargetState);
                    }
                    break;

                case EStateTreeTransitionType.NextState:
                    // 切换到声明顺序的下一个状态
                    if (Asset != null)
                    {
                        string? next = Asset.GetNextStateName(CurrentStateName);
                        if (next != null)
                            SwitchToState(ctx, next);
                        else if (!string.IsNullOrEmpty(Asset.RootState))
                            SwitchToState(ctx, Asset.RootState);
                    }
                    break;

                case EStateTreeTransitionType.Restart:
                    // 重启状态树：退出当前状态，从 RootState 重新开始
                    ExitCurrentState(taskStatus);
                    if (Asset != null && !string.IsNullOrEmpty(Asset.RootState))
                        EnterState(Asset.RootState);
                    break;

                case EStateTreeTransitionType.ReenterState:
                    // 重入当前状态：先 Exit 再 Enter，刷新局部数据
                    ExitCurrentState(taskStatus);
                    EnterState(CurrentStateName);
                    break;

                case EStateTreeTransitionType.Stay:
                    // 保持当前状态，不做任何操作
                    break;

                case EStateTreeTransitionType.Succeeded:
                    // 成功结束状态树
                    ExitCurrentState(EStateTreeRunStatus.Succeeded);
                    Status = EStateTreeRunStatus.Succeeded;
                    bStarted = false;
                    break;

                case EStateTreeTransitionType.Failed:
                    // 失败结束状态树
                    ExitCurrentState(EStateTreeRunStatus.Failed);
                    Status = EStateTreeRunStatus.Failed;
                    bStarted = false;
                    break;
            }
        }

        /// <summary>
        /// 切换到目标状态：先退出当前状态，再进入目标状态。
        /// 若目标状态的进入条件不满足，则不切换（保持当前状态）。
        /// </summary>
        private void SwitchToState(StateTreeContext ctx, string targetStateName)
        {
            if (Asset == null)
                return;
            var targetState = Asset.GetState(targetStateName);
            if (targetState == null)
                return;

            // 检查目标状态的进入条件
            if (!targetState.EvaluateEnterConditions(ctx))
                return;

            // 退出当前状态
            ExitCurrentState(EStateTreeRunStatus.Running);

            // 进入目标状态
            EnterState(targetStateName);
        }

        /// <summary>进入指定名称的状态。激活该状态的所有任务。</summary>
        private void EnterState(string stateName)
        {
            if (Asset == null)
                return;
            var state = Asset.GetState(stateName);
            if (state == null)
            {
                // 状态不存在：状态树失败
                Status = EStateTreeRunStatus.Failed;
                return;
            }

            _currentState = state;
            CurrentStateName = stateName;

            // 重置状态局部数据
            _stateData.Reset(_lastWorldTime);

            // 激活该状态的所有任务（调用 Enter）
            for (int i = 0; i < state.Tasks.Count; i++)
            {
                state.Tasks[i].Enter(BuildContextForCallbacks());
            }
        }

        /// <summary>退出当前状态。停用该状态的所有任务（调用 Exit）。</summary>
        private void ExitCurrentState(EStateTreeRunStatus exitStatus)
        {
            if (_currentState == null)
                return;

            // 停用该状态的所有任务
            var ctx = BuildContextForCallbacks();
            for (int i = 0; i < _currentState.Tasks.Count; i++)
            {
                _currentState.Tasks[i].Exit(ctx, exitStatus);
            }

            _currentState = null;
            CurrentStateName = "";
        }

        /// <summary>
        /// 构建一个用于生命周期回调（Enter/Exit）的上下文。
        /// 使用实例缓存的依赖（EntityId / ECS / 导航 / 物理），
        /// 确保即使回调发生在 Start/Stop 等非 Tick 路径中，
        /// 任务也能访问引擎服务。回调中 DeltaTime 为 0，WorldTime 为上次记录值。
        /// </summary>
        private StateTreeContext BuildContextForCallbacks()
        {
            return new StateTreeContext(_entityId, Blackboard)
            {
                Instance = this,
                CurrentState = CurrentStateName,
                StateData = _stateData,
                WorldTime = _lastWorldTime,
                DeltaTime = 0f,
                ECS = _ecs,
                Navigation = _navigation,
                PhysicsWorld = _physicsWorld,
            };
        }

        /// <summary>
        /// 设置全局时间（由 StateTreeSystem 每帧更新，用于状态进入时间记录）。
        /// </summary>
        internal void SetWorldTime(float worldTime)
        {
            _lastWorldTime = worldTime;
        }

        /// <summary>
        /// 注入上下文依赖（由 StateTreeSystem 在 Start/Tick 前调用）。
        /// 缓存实体 ID 与引擎系统引用，供 Enter/Exit 回调构建上下文使用。
        /// </summary>
        internal void SetContextDependencies(int entityId, EntityComponentSystem? ecs,
            NavigationSystem? navigation, PhysicsWorld? physicsWorld)
        {
            _entityId = entityId;
            _ecs = ecs;
            _navigation = navigation;
            _physicsWorld = physicsWorld;
        }
    }

    // ══════════════════════════════════════════════════════════════════
    // StateTreeComponent —— ECS 组件
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 状态树 ECS 组件。对齐 UE5 中 Actor 上的 StateTree 组件。
    /// 挂载到需要 StateTree 决策的 AI 实体上，引用状态树资产与运行时实例。
    /// 由 StateTreeSystem 在首次更新时懒初始化运行时实例。
    /// 必须为 struct 以满足 ECS 的 IComponent 约束（值类型组件）。
    /// </summary>
    public struct StateTreeComponent : IComponent
    {
        /// <summary>关联的状态树资产</summary>
        public StateTreeAsset? Asset;

        /// <summary>运行时实例（由 StateTreeSystem 创建，不需手动赋值）</summary>
        public StateTreeInstance? Instance;

        /// <summary>是否启用此 AI 的状态树决策</summary>
        public bool bEnabled;

        /// <summary>Tick 频率（Hz），0 表示每帧更新</summary>
        public float TickRate;

        /// <summary>累计时间（用于控制 Tick 频率）</summary>
        public float TickAccumulator;

        /// <summary>绑定的黑板（若为空则使用实例自带黑板）</summary>
        public Blackboard? ExternalBlackboard;
    }

    // ══════════════════════════════════════════════════════════════════
    // StateTreeSystem —— ECS 系统
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 状态树系统（StateTreeSystem）。对齐 UE5 的 StateTree 每帧驱动。
    /// 实现 ECS 的 ISystem 接口，每帧查询所有 StateTreeComponent 实体，
    /// 构建 StateTreeContext 并 Tick 各实例。
    /// 持有导航系统与物理世界的引用，注入到上下文供任务/条件使用。
    /// </summary>
    public sealed class StateTreeSystem : ISystem
    {
        /// <summary>系统名称</summary>
        public string Name => "StateTreeSystem";

        /// <summary>导航系统引用</summary>
        public NavigationSystem? Navigation;

        /// <summary>物理世界引用</summary>
        public PhysicsWorld? PhysicsWorld;

        /// <summary>全局累计时间（秒）</summary>
        public float WorldTime;

        /// <summary>活跃状态树实例数（统计用）</summary>
        public int ActiveInstanceCount { get; private set; }

        /// <summary>调试器（供编辑器查询状态信息）</summary>
        public StateTreeDebugger Debugger { get; } = new();

        /// <summary>
        /// 每帧更新：驱动所有 AI 实体的状态树。
        /// 对齐 UE5 中 StateTree Component 的 Tick 逻辑。
        /// </summary>
        /// <param name="dt">增量时间</param>
        /// <param name="ecs">ECS 实例</param>
        public void Update(float dt, EntityComponentSystem ecs)
        {
            WorldTime += dt;
            ActiveInstanceCount = 0;
            Debugger.ClearFrame();

            // 查询所有拥有 StateTreeComponent 的实体
            var entities = ecs.QueryWithComponent<StateTreeComponent>();
            foreach (int eid in entities)
            {
                var comp = ecs.GetComponent<StateTreeComponent>(eid);
                if (!comp.bEnabled || comp.Asset == null)
                {
                    ecs.SetComponent(eid, comp);
                    continue;
                }

                // 懒初始化运行时实例
                if (comp.Instance == null)
                {
                    Blackboard bb = comp.ExternalBlackboard ?? new Blackboard();
                    comp.Instance = new StateTreeInstance(comp.Asset, bb);
                    comp.Instance.Bind(comp.Asset);
                    // 在 Start 前注入上下文依赖，确保初始状态的 Enter 回调
                    // 能访问 ECS / 导航 / 物理（如 MoveToTask.Enter 无需 ECS，但
                    // 自定义任务可能需要）
                    comp.Instance.SetContextDependencies(eid, ecs, Navigation, PhysicsWorld);
                    comp.Instance.SetWorldTime(WorldTime);
                    comp.Instance.Start();
                }

                var instance = comp.Instance;

                // 每帧同步全局时间与上下文依赖（实体可能被销毁重建，引用需刷新）
                instance.SetWorldTime(WorldTime);
                instance.SetContextDependencies(eid, ecs, Navigation, PhysicsWorld);

                // 控制更新频率
                float tickInterval = comp.TickRate > 0 ? 1f / comp.TickRate : 0f;
                comp.TickAccumulator += dt;
                if (tickInterval > 0 && comp.TickAccumulator < tickInterval)
                {
                    ecs.SetComponent(eid, comp);
                    continue;
                }
                comp.TickAccumulator = 0f;

                // 选择黑板：优先外部黑板，否则使用实例黑板
                Blackboard blackboard = comp.ExternalBlackboard ?? instance.Blackboard;

                // 构建运行时上下文
                var ctx = new StateTreeContext(eid, blackboard)
                {
                    DeltaTime = dt,
                    WorldTime = WorldTime,
                    ECS = ecs,
                    Navigation = Navigation,
                    PhysicsWorld = PhysicsWorld,
                    CurrentState = instance.GetCurrentState(),
                    Instance = instance,
                };

                // 推进状态树
                instance.Tick(dt, ctx);

                // 记录调试信息
                Debugger.RecordInstance(eid, instance);

                ActiveInstanceCount++;
                ecs.SetComponent(eid, comp);
            }
        }

        /// <summary>绑定导航系统。</summary>
        public void BindNavigation(NavigationSystem nav) => Navigation = nav;

        /// <summary>绑定物理世界。</summary>
        public void BindPhysics(PhysicsWorld world) => PhysicsWorld = world;

        /// <summary>
        /// 为指定实体创建并挂载状态树。
        /// </summary>
        /// <param name="ecs">ECS 实例</param>
        /// <param name="entityId">实体 ID</param>
        /// <param name="asset">状态树资产</param>
        /// <param name="tickRate">Tick 频率（Hz），0 表示每帧</param>
        /// <param name="blackboard">可选的外部黑板（共享时使用）</param>
        public void AssignStateTree(EntityComponentSystem ecs, int entityId,
            StateTreeAsset asset, float tickRate = 0f, Blackboard? blackboard = null)
        {
            if (!ecs.HasEntity(entityId))
                return;

            // 确保实体拥有 StateTreeComponent
            if (!ecs.HasComponent<StateTreeComponent>(entityId))
                ecs.AddComponent(entityId, new StateTreeComponent());

            var comp = ecs.GetComponent<StateTreeComponent>(entityId);
            comp.Asset = asset;
            comp.bEnabled = true;
            comp.TickRate = tickRate;
            comp.ExternalBlackboard = blackboard;
            comp.Instance = null; // 交由 Update 懒初始化
            ecs.SetComponent(entityId, comp);
        }
    }

    // ══════════════════════════════════════════════════════════════════
    // StateTreeDebugger —— 调试器
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 状态树调试器。对齐 UE5 的 StateTree 调试视图。
    /// 每帧收集所有活跃状态树实例的当前状态与任务状态，
    /// 供编辑器绘制状态树可视化（当前状态高亮、任务进度条等）。
    /// </summary>
    public sealed class StateTreeDebugger
    {
        /// <summary>本帧调试快照列表（每实体一条）</summary>
        private readonly List<StateTreeDebugSnapshot> _snapshots = new();

        /// <summary>获取本帧所有调试快照（只读）</summary>
        public IReadOnlyList<StateTreeDebugSnapshot> Snapshots => _snapshots;

        /// <summary>清除本帧数据（每帧开始时调用）。</summary>
        public void ClearFrame()
        {
            _snapshots.Clear();
        }

        /// <summary>记录一个实例的调试快照。</summary>
        public void RecordInstance(int entityId, StateTreeInstance instance)
        {
            var snapshot = new StateTreeDebugSnapshot
            {
                EntityId = entityId,
                AssetName = instance.AssetName,
                CurrentState = instance.GetCurrentState(),
                Status = instance.GetStatus(),
                bStarted = instance.bStarted,
                bPaused = instance.bPaused,
            };

            // 收集当前状态的任务调试信息
            if (instance.Asset != null && !string.IsNullOrEmpty(snapshot.CurrentState))
            {
                var state = instance.Asset.GetState(snapshot.CurrentState);
                if (state != null)
                {
                    snapshot.StateName = state.Name;
                    snapshot.TaskInfos = new List<string>(state.Tasks.Count);
                    for (int i = 0; i < state.Tasks.Count; i++)
                    {
                        snapshot.TaskInfos.Add(state.Tasks[i].GetDebugInfo());
                    }
                }
            }

            _snapshots.Add(snapshot);
        }

        /// <summary>获取指定实体的调试快照（不存在返回 null）。</summary>
        public StateTreeDebugSnapshot? GetSnapshot(int entityId)
        {
            for (int i = 0; i < _snapshots.Count; i++)
            {
                if (_snapshots[i].EntityId == entityId)
                    return _snapshots[i];
            }
            return null;
        }

        /// <summary>生成所有实例的调试文本摘要（供日志输出）。</summary>
        public string GetDebugSummary()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"=== StateTree Debug ({_snapshots.Count} instances) ===");
            for (int i = 0; i < _snapshots.Count; i++)
            {
                var s = _snapshots[i];
                sb.AppendLine($"  Entity#{s.EntityId} [{s.AssetName}] " +
                              $"State={s.CurrentState} Status={s.Status} " +
                              $"Started={s.bStarted} Paused={s.bPaused}");
                if (s.TaskInfos != null)
                {
                    for (int j = 0; j < s.TaskInfos.Count; j++)
                        sb.AppendLine($"    Task[{j}]: {s.TaskInfos[j]}");
                }
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// 状态树调试快照。记录某一帧中某个状态树实例的运行时状态摘要。
    /// </summary>
    public sealed class StateTreeDebugSnapshot
    {
        /// <summary>实体 ID</summary>
        public int EntityId;

        /// <summary>资产名称</summary>
        public string AssetName = "";

        /// <summary>当前状态名</summary>
        public string CurrentState = "";

        /// <summary>状态显示名</summary>
        public string StateName = "";

        /// <summary>运行状态</summary>
        public EStateTreeRunStatus Status;

        /// <summary>是否已启动</summary>
        public bool bStarted;

        /// <summary>是否暂停</summary>
        public bool bPaused;

        /// <summary>当前状态各任务的调试信息</summary>
        public List<string>? TaskInfos;
    }
}
