// ============================================================================
//  ShanyuEngine.AI.AIControllerSystem.cs
//  山雨引擎 -- AI 控制器系统 (AI Controller System)
//  对齐 UE AIController
// ----------------------------------------------------------------------------
//  完整的 AI 控制器系统，包含：
//    1. AIState                - AI状态 (Idle/Alert/Chase/Attack/Flee/Patrol/Search
//                                 Dead/Interact/HoldPosition/Follow)
//    2. AIStateTransition      - 状态转换 (条件驱动)
//    3. AIController            - AI控制器 (继承PlayerController)
//    4. AIPerceptionConfig集成 - 感知配置
//    5. BehaviorTree集成       - 当前行为树/行为树实例
//    6. NavAgent集成           - 导航代理移动
//    7. AIDecisionMaker        - 决策器 (优先级评分/最佳行动选择)
//    8. AIAction               - AI行动 (执行/中断/评估/成本/优先级)
//    9. 内置行动               - Patrol/Chase/Attack/Flee/Search/UseCover/Heal/Rally
//   10. AIMemory               - AI记忆 (已知目标/最后已知位置/威胁评估)
//   11. AICommunication        - AI通讯 (广播/请求/回应/团队协调)
// ============================================================================

using System;
using System.Collections.Generic;
using System.Numerics;

namespace ShanyuEngine.AI
{
    // ========================================================================
    //  一、AI 状态枚举
    // ========================================================================

    /// <summary>
    /// AI 内置状态。
    /// </summary>
    public enum AIStateType
    {
        Idle,
        Alert,
        Chase,
        Attack,
        Flee,
        Patrol,
        Search,
        Dead,
        Interact,
        HoldPosition,
        Follow,
        Custom
    }

    // ========================================================================
    //  二、AI 状态 (AIState)
    // ========================================================================

    /// <summary>
    /// AI 状态。描述一种行为状态的进入/更新/退出逻辑。
    /// </summary>
    public sealed class AIState
    {
        public AIStateType StateType { get; set; } = AIStateType.Idle;
        public string StateName { get; set; } = "Idle";
        public float MinDuration { get; set; } = 0f;
        public float MaxDuration { get; set; } = 0f; // 0=无限
        public float ElapsedTime { get; private set; }

        /// <summary>进入状态回调。</summary>
        public Action<AIController> OnEnter { get; set; }
        /// <summary>更新状态回调（返回false表示继续，true表示自动退出）。</summary>
        public Func<AIController, float, bool> OnUpdate { get; set; }
        /// <summary>退出状态回调。</summary>
        public Action<AIController> OnExit { get; set; }

        private float _duration = 0f;

        public void Enter(AIController controller)
        {
            ElapsedTime = 0f;
            _duration = MaxDuration > 0f ? MinDuration + (float)(new Random().NextDouble()) * (MaxDuration - MinDuration) : 0f;
            OnEnter?.Invoke(controller);
        }

        public void Update(AIController controller, float deltaTime)
        {
            ElapsedTime += deltaTime;
            OnUpdate?.Invoke(controller, deltaTime);
        }

        public void Exit(AIController controller)
        {
            OnExit?.Invoke(controller);
        }

        public bool IsDurationExpired() => MaxDuration > 0f && ElapsedTime >= _duration;
    }

    // ========================================================================
    //  三、状态转换 (AIStateTransition)
    // ========================================================================

    /// <summary>
    /// AI 状态转换。条件驱动的状态切换。
    /// </summary>
    public sealed class AIStateTransition
    {
        /// <summary>源状态。</summary>
        public AIStateType FromState { get; set; }
        /// <summary>目标状态。</summary>
        public AIStateType ToState { get; set; }
        /// <summary>转换条件。</summary>
        public Func<AIController, bool> Condition { get; set; }
        /// <summary>优先级（数值越小优先级越高）。</summary>
        public int Priority { get; set; } = 0;
        /// <summary>是否一次性（触发后自动删除）。</summary>
        public bool OneShot { get; set; } = false;
        /// <summary>是否已触发（用于一次性转换）。</summary>
        internal bool _triggered = false;

        /// <summary>转换回调。</summary>
        public Action<AIController> OnTransition { get; set; }

        public AIStateTransition() { }

        public AIStateTransition(AIStateType from, AIStateType to, Func<AIController, bool> condition,
            int priority = 0)
        {
            FromState = from;
            ToState = to;
            Condition = condition;
            Priority = priority;
        }

        public bool Evaluate(AIController controller)
        {
            if (OneShot && _triggered) return false;
            if (Condition != null && Condition(controller))
            {
                if (OneShot) _triggered = true;
                return true;
            }
            return false;
        }
    }

    // ========================================================================
    //  四、AI 行动 (AIAction)
    // ========================================================================

    /// <summary>
    /// AI 行动基类。描述一种可执行的行为。
    /// </summary>
    public abstract class AIAction
    {
        public string ActionName { get; protected set; } = "UnnamedAction";
        public float Cost { get; set; } = 1f;
        public int Priority { get; set; } = 0;
        public bool IsAvailable { get; set; } = true;
        public bool IsExecuting { get; private set; }

        /// <summary>评估行动可用性。</summary>
        public virtual bool EvaluateAvailability(AIController controller) => IsAvailable;

        /// <summary>评估行动的动态成本。</summary>
        public virtual float EvaluateCost(AIController controller) => Cost;

        /// <summary>评估行动的优先级得分。</summary>
        public virtual float EvaluateScore(AIController controller)
        {
            return EvaluateAvailability(controller) ? Priority - EvaluateCost(controller) : float.MinValue;
        }

        /// <summary>开始执行。</summary>
        public virtual void Execute(AIController controller) { IsExecuting = true; }

        /// <summary>更新执行。</summary>
        public virtual void Update(AIController controller, float deltaTime) { }

        /// <summary>中断执行。</summary>
        public virtual void Abort(AIController controller) { IsExecuting = false; }

        /// <summary>是否完成。</summary>
        public virtual bool IsComplete(AIController controller) { return true; }
    }

    // ========================================================================
    //  五、内置 AI 行动
    // ========================================================================

    /// <summary>巡逻行动。</summary>
    public sealed class AIAction_Patrol : AIAction
    {
        public List<Vector3> PatrolPoints { get; set; } = new List<Vector3>();
        private int _currentPoint = 0;

        public AIAction_Patrol()
        {
            ActionName = "Patrol";
            Cost = 0.2f;
            Priority = 10;
        }

        public override void Execute(AIController controller)
        {
            base.Execute(controller);
            _currentPoint = 0;
            if (PatrolPoints.Count > 0 && controller.NavAgent != null)
                controller.NavAgent.MoveTo(PatrolPoints[0]);
        }

        public override void Update(AIController controller, float deltaTime)
        {
            if (controller.NavAgent == null || PatrolPoints.Count == 0) return;
            if (controller.NavAgent.HasArrived)
            {
                _currentPoint = (_currentPoint + 1) % PatrolPoints.Count;
                controller.NavAgent.MoveTo(PatrolPoints[_currentPoint]);
            }
            controller.NavAgent.Update(deltaTime);
        }

        public override bool IsComplete(AIController controller) => false; // 巡逻持续进行

        public override void Abort(AIController controller)
        {
            controller.NavAgent?.Stop();
            base.Abort(controller);
        }
    }

    /// <summary>追逐行动。</summary>
    public sealed class AIAction_Chase : AIAction
    {
        public float ChaseRadius { get; set; } = 500f;
        public float RecalcInterval { get; set; } = 1f;
        private float _recalcTimer = 0f;

        public AIAction_Chase()
        {
            ActionName = "Chase";
            Cost = 0.5f;
            Priority = 30;
        }

        public override bool EvaluateAvailability(AIController controller)
        {
            return controller.Memory?.GetClosestTarget() != null;
        }

        public override void Execute(AIController controller)
        {
            base.Execute(controller);
            var target = controller.Memory?.GetClosestTarget();
            if (target != null && controller.NavAgent != null)
                controller.NavAgent.MoveTo(target.Position);
        }

        public override void Update(AIController controller, float deltaTime)
        {
            _recalcTimer += deltaTime;
            if (_recalcTimer >= RecalcInterval)
            {
                _recalcTimer = 0f;
                var target = controller.Memory?.GetClosestTarget();
                if (target != null && controller.NavAgent != null)
                    controller.NavAgent.MoveTo(target.Position);
            }
            controller.NavAgent?.Update(deltaTime);
        }

        public override bool IsComplete(AIController controller)
        {
            return controller.Memory?.GetClosestTarget() == null;
        }

        public override void Abort(AIController controller)
        {
            controller.NavAgent?.Stop();
            base.Abort(controller);
        }
    }

    /// <summary>攻击行动。</summary>
    public sealed class AIAction_Attack : AIAction
    {
        public float AttackRange { get; set; } = 5f;
        public float AttackCooldown { get; set; } = 1f;
        private float _cooldownTimer = 0f;

        public AIAction_Attack()
        {
            ActionName = "Attack";
            Cost = 1f;
            Priority = 50;
        }

        public override bool EvaluateAvailability(AIController controller)
        {
            var target = controller.Memory?.GetClosestTarget();
            if (target == null) return false;
            float dist = Vector3.Distance(controller.Position, target.Position);
            return dist <= AttackRange;
        }

        public override void Execute(AIController controller)
        {
            base.Execute(controller);
            _cooldownTimer = 0f;
        }

        public override void Update(AIController controller, float deltaTime)
        {
            _cooldownTimer += deltaTime;
            if (_cooldownTimer >= AttackCooldown)
            {
                _cooldownTimer = 0f;
                var target = controller.Memory?.GetClosestTarget();
                if (target != null)
                {
                    // 触发攻击
                    controller.PerformAttack(target);
                }
            }
        }

        public override bool IsComplete(AIController controller)
        {
            var target = controller.Memory?.GetClosestTarget();
            if (target == null) return true;
            return Vector3.Distance(controller.Position, target.Position) > AttackRange * 1.5f;
        }
    }

    /// <summary>逃跑行动。</summary>
    public sealed class AIAction_Flee : AIAction
    {
        public float FleeDistance { get; set; } = 50f;
        public float LowHealthThreshold { get; set; } = 0.3f;

        public AIAction_Flee()
        {
            ActionName = "Flee";
            Cost = 0.8f;
            Priority = 40;
        }

        public override bool EvaluateAvailability(AIController controller)
        {
            return controller.HealthPercent <= LowHealthThreshold;
        }

        public override void Execute(AIController controller)
        {
            base.Execute(controller);
            var threat = controller.Memory?.GetClosestTarget();
            if (threat != null && controller.NavAgent != null)
            {
                var dir = controller.Position - threat.Position;
                if (dir.LengthSquared() > 0.001f)
                {
                    dir = Vector3.Normalize(dir);
                    controller.NavAgent.MoveTo(controller.Position + dir * FleeDistance);
                }
            }
        }

        public override void Update(AIController controller, float deltaTime)
        {
            controller.NavAgent?.Update(deltaTime);
        }

        public override bool IsComplete(AIController controller)
        {
            return controller.NavAgent?.HasArrived ?? true;
        }

        public override void Abort(AIController controller)
        {
            controller.NavAgent?.Stop();
            base.Abort(controller);
        }
    }

    /// <summary>搜索行动。</summary>
    public sealed class AIAction_Search : AIAction
    {
        public float SearchDuration { get; set; } = 10f;
        public float SearchRadius { get; set; } = 30f;
        private float _elapsed = 0f;

        public AIAction_Search()
        {
            ActionName = "Search";
            Cost = 0.4f;
            Priority = 20;
        }

        public override void Execute(AIController controller)
        {
            base.Execute(controller);
            _elapsed = 0f;
            var lastPos = controller.Memory?.GetLastKnownPosition();
            if (lastPos.HasValue && controller.NavAgent != null)
                controller.NavAgent.MoveTo(lastPos.Value);
        }

        public override void Update(AIController controller, float deltaTime)
        {
            _elapsed += deltaTime;
            controller.NavAgent?.Update(deltaTime);
        }

        public override bool IsComplete(AIController controller) => _elapsed >= SearchDuration;

        public override void Abort(AIController controller)
        {
            controller.NavAgent?.Stop();
            base.Abort(controller);
        }
    }

    /// <summary>使用掩体行动。</summary>
    public sealed class AIAction_UseCover : AIAction
    {
        public Vector3 CoverPosition { get; set; }

        public AIAction_UseCover()
        {
            ActionName = "UseCover";
            Cost = 0.3f;
            Priority = 35;
        }

        public override void Execute(AIController controller)
        {
            base.Execute(controller);
            if (controller.NavAgent != null)
                controller.NavAgent.MoveTo(CoverPosition);
        }

        public override void Update(AIController controller, float deltaTime)
        {
            controller.NavAgent?.Update(deltaTime);
        }

        public override bool IsComplete(AIController controller)
        {
            return controller.NavAgent?.HasArrived ?? true;
        }

        public override void Abort(AIController controller)
        {
            controller.NavAgent?.Stop();
            base.Abort(controller);
        }
    }

    /// <summary>治疗行动。</summary>
    public sealed class AIAction_Heal : AIAction
    {
        public float HealAmount { get; set; } = 20f;
        public float HealInterval { get; set; } = 2f;
        private float _healTimer = 0f;

        public AIAction_Heal()
        {
            ActionName = "Heal";
            Cost = 0.6f;
            Priority = 25;
        }

        public override bool EvaluateAvailability(AIController controller)
        {
            return controller.HealthPercent < 1f;
        }

        public override void Execute(AIController controller)
        {
            base.Execute(controller);
            _healTimer = 0f;
        }

        public override void Update(AIController controller, float deltaTime)
        {
            _healTimer += deltaTime;
            if (_healTimer >= HealInterval)
            {
                _healTimer = 0f;
                controller.HealthPercent = Math.Min(1f, controller.HealthPercent + HealAmount * 0.01f);
            }
        }

        public override bool IsComplete(AIController controller)
        {
            return controller.HealthPercent >= 1f;
        }
    }

    /// <summary>集结行动。</summary>
    public sealed class AIAction_Rally : AIAction
    {
        public Vector3 RallyPoint { get; set; }

        public AIAction_Rally()
        {
            ActionName = "Rally";
            Cost = 0.3f;
            Priority = 15;
        }

        public override void Execute(AIController controller)
        {
            base.Execute(controller);
            if (controller.NavAgent != null)
                controller.NavAgent.MoveTo(RallyPoint);
        }

        public override void Update(AIController controller, float deltaTime)
        {
            controller.NavAgent?.Update(deltaTime);
        }

        public override bool IsComplete(AIController controller)
        {
            return controller.NavAgent?.HasArrived ?? true;
        }

        public override void Abort(AIController controller)
        {
            controller.NavAgent?.Stop();
            base.Abort(controller);
        }
    }

    // ========================================================================
    //  六、AI 决策器 (AIDecisionMaker)
    // ========================================================================

    /// <summary>
    /// AI 决策器。优先级评分/最佳行动选择。
    /// </summary>
    public sealed class AIDecisionMaker
    {
        private readonly List<AIAction> _availableActions = new List<AIAction>();
        private AIAction _currentAction;

        /// <summary>当前执行的行动。</summary>
        public AIAction CurrentAction => _currentAction;

        /// <summary>行动变更回调。</summary>
        public Action<AIAction, AIAction> OnActionChanged { get; set; }

        /// <summary>注册可用行动。</summary>
        public void RegisterAction(AIAction action)
        {
            if (action != null && !_availableActions.Contains(action))
                _availableActions.Add(action);
        }

        /// <summary>注销行动。</summary>
        public void UnregisterAction(AIAction action)
        {
            if (action != null)
            {
                _availableActions.Remove(action);
                if (_currentAction == action)
                {
                    _currentAction = null;
                }
            }
        }

        /// <summary>评估并选择最佳行动。</summary>
        public AIAction DecideBestAction(AIController controller)
        {
            AIAction bestAction = null;
            float bestScore = float.MinValue;

            foreach (var action in _availableActions)
            {
                if (!action.EvaluateAvailability(controller)) continue;
                float score = action.EvaluateScore(controller);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestAction = action;
                }
            }

            return bestAction;
        }

        /// <summary>执行最佳行动。</summary>
        public void ExecuteBestAction(AIController controller)
        {
            var bestAction = DecideBestAction(controller);
            if (bestAction != _currentAction)
            {
                var oldAction = _currentAction;
                _currentAction?.Abort(controller);
                _currentAction = bestAction;
                _currentAction?.Execute(controller);
                OnActionChanged?.Invoke(oldAction, _currentAction);
            }
        }

        /// <summary>更新当前行动。</summary>
        public void Update(AIController controller, float deltaTime)
        {
            if (_currentAction == null) return;

            _currentAction.Update(controller, deltaTime);

            if (_currentAction.IsComplete(controller) || !_currentAction.EvaluateAvailability(controller))
            {
                _currentAction.Abort(controller);
                _currentAction = null;
                ExecuteBestAction(controller);
            }
        }

        /// <summary>强制切换到指定行动。</summary>
        public void ForceAction(AIAction action, AIController controller)
        {
            if (action == null) return;
            _currentAction?.Abort(controller);
            _currentAction = action;
            _currentAction.Execute(controller);
        }

        /// <summary>获取所有可用行动。</summary>
        public IReadOnlyList<AIAction> GetAvailableActions() => _availableActions;
    }

    // ========================================================================
    //  七、AI 记忆 (AIMemory)
    // ========================================================================

    /// <summary>
    /// AI 记忆系统。已知目标/最后已知位置/威胁评估/时间衰减遗忘。
    /// </summary>
    public sealed class AIMemory
    {
        private readonly Dictionary<ShanyuEngine.GameFramework.Actor, MemoryEntry> _memories =
            new Dictionary<ShanyuEngine.GameFramework.Actor, MemoryEntry>();

        public float DefaultForgetTime { get; set; } = 30f;
        public float ThreatUpdateInterval { get; set; } = 1f;
        private float _currentTime = 0f;

        private sealed class MemoryEntry
        {
            public ShanyuEngine.GameFramework.Actor Actor;
            public Vector3 LastKnownPosition;
            public float LastSeenTime;
            public float ThreatLevel;
            public bool IsValid;
        }

        /// <summary>记忆更新回调。</summary>
        public Action<ShanyuEngine.GameFramework.Actor, float> OnThreatChanged { get; set; }
        public Action<ShanyuEngine.GameFramework.Actor> OnTargetForgotten { get; set; }

        /// <summary>更新目标信息。</summary>
        public void UpdateTarget(ShanyuEngine.GameFramework.Actor target, Vector3 position, float threatLevel = 0.5f)
        {
            if (target == null) return;

            if (_memories.TryGetValue(target, out var entry))
            {
                entry.LastKnownPosition = position;
                entry.LastSeenTime = _currentTime;
                entry.ThreatLevel = threatLevel;
                entry.IsValid = true;
            }
            else
            {
                _memories[target] = new MemoryEntry
                {
                    Actor = target,
                    LastKnownPosition = position,
                    LastSeenTime = _currentTime,
                    ThreatLevel = threatLevel,
                    IsValid = true
                };
            }
            OnThreatChanged?.Invoke(target, threatLevel);
        }

        /// <summary>获取目标的最后已知位置。</summary>
        public Vector3? GetLastKnownPosition(ShanyuEngine.GameFramework.Actor target)
        {
            if (target != null && _memories.TryGetValue(target, out var entry) && entry.IsValid)
                return entry.LastKnownPosition;
            return null;
        }

        /// <summary>获取所有已知目标。</summary>
        public List<ShanyuEngine.GameFramework.Actor> GetKnownTargets()
        {
            var targets = new List<ShanyuEngine.GameFramework.Actor>();
            foreach (var kv in _memories)
                if (kv.Value.IsValid) targets.Add(kv.Key);
            return targets;
        }

        /// <summary>获取最近的目标。</summary>
        public ShanyuEngine.GameFramework.Actor GetClosestTarget(Vector3 fromPosition)
        {
            ShanyuEngine.GameFramework.Actor closest = null;
            float minDist = float.MaxValue;
            foreach (var kv in _memories)
            {
                if (!kv.Value.IsValid) continue;
                float dist = Vector3.Distance(fromPosition, kv.Value.LastKnownPosition);
                if (dist < minDist)
                {
                    minDist = dist;
                    closest = kv.Key;
                }
            }
            return closest;
        }

        /// <summary>获取最高威胁目标。</summary>
        public ShanyuEngine.GameFramework.Actor GetHighestThreatTarget()
        {
            ShanyuEngine.GameFramework.Actor threat = null;
            float maxThreat = float.MinValue;
            foreach (var kv in _memories)
            {
                if (!kv.Value.IsValid) continue;
                if (kv.Value.ThreatLevel > maxThreat)
                {
                    maxThreat = kv.Value.ThreatLevel;
                    threat = kv.Key;
                }
            }
            return threat;
        }

        /// <summary>每帧更新（时间衰减遗忘）。</summary>
        public void Update(float deltaTime, Vector3 ownerPosition)
        {
            _currentTime += deltaTime;

            var toForget = new List<ShanyuEngine.GameFramework.Actor>();
            foreach (var kv in _memories)
            {
                if (!kv.Value.IsValid) continue;
                float age = _currentTime - kv.Value.LastSeenTime;
                if (age > DefaultForgetTime)
                {
                    kv.Value.IsValid = false;
                    toForget.Add(kv.Key);
                }
            }

            foreach (var target in toForget)
            {
                _memories.Remove(target);
                OnTargetForgotten?.Invoke(target);
            }
        }

        /// <summary>清除所有记忆。</summary>
        public void ClearAll()
        {
            _memories.Clear();
        }
    }

    // ========================================================================
    //  八、AI 通讯 (AICommunication)
    // ========================================================================

    /// <summary>
    /// AI 通讯系统。广播/请求/回应/团队协调。
    /// </summary>
    public sealed class AICommunication
    {
        public enum MessageType
        {
            None,
            EnemySpotted,
            UnderAttack,
            NeedBackup,
            TargetDown,
            RallyPoint,
            FlankLeft,
            FlankRight,
            TakeCover,
            Regroup,
            Custom
        }

        public sealed class AIMessage
        {
            public MessageType Type { get; set; }
            public ShanyuEngine.GameFramework.Actor Sender { get; set; }
            public ShanyuEngine.GameFramework.Actor Target { get; set; }
            public Vector3 Position { get; set; }
            public float Timestamp { get; set; }
            public Dictionary<string, object> Data { get; } = new Dictionary<string, object>();
        }

        private readonly Dictionary<AITeamId, List<AIController>> _teamMembers =
            new Dictionary<AITeamId, List<AIController>>();

        private readonly List<(AIMessage Message, AITeamId Team)> _messageQueue =
            new List<(AIMessage, AITeamId)>();

        /// <summary>消息处理回调。</summary>
        public Action<AIController, AIMessage> OnMessageReceived { get; set; }

        public float MessageLifespan { get; set; } = 5f;

        /// <summary>注册团队成员。</summary>
        public void RegisterMember(AITeamId teamId, AIController controller)
        {
            if (!_teamMembers.ContainsKey(teamId))
                _teamMembers[teamId] = new List<AIController>();
            if (!_teamMembers[teamId].Contains(controller))
                _teamMembers[teamId].Add(controller);
        }

        /// <summary>注销团队成员。</summary>
        public void UnregisterMember(AITeamId teamId, AIController controller)
        {
            if (_teamMembers.TryGetValue(teamId, out var members))
                members.Remove(controller);
        }

        /// <summary>广播消息到全队。</summary>
        public void Broadcast(AITeamId teamId, AIMessage message)
        {
            _messageQueue.Add((message, teamId));
        }

        /// <summary>发送请求（等待回应）。</summary>
        public void SendRequest(AITeamId teamId, AIMessage message)
        {
            _messageQueue.Add((message, teamId));
        }

        /// <summary>处理消息队列。</summary>
        public void ProcessMessages(float currentTime)
        {
            for (int i = _messageQueue.Count - 1; i >= 0; i--)
            {
                var (msg, teamId) = _messageQueue[i];

                // 检查消息是否过期
                if (currentTime - msg.Timestamp > MessageLifespan)
                {
                    _messageQueue.RemoveAt(i);
                    continue;
                }

                // 分发给团队成员
                if (_teamMembers.TryGetValue(teamId, out var members))
                {
                    foreach (var member in members)
                    {
                        if (member != msg.Sender)
                            OnMessageReceived?.Invoke(member, msg);
                    }
                }

                _messageQueue.RemoveAt(i);
            }
        }

        /// <summary>协同攻击：通知队友攻击同一目标。</summary>
        public void CoordinateAttack(AITeamId teamId, ShanyuEngine.GameFramework.Actor sender,
            ShanyuEngine.GameFramework.Actor target, Vector3 position)
        {
            Broadcast(teamId, new AIMessage
            {
                Type = MessageType.EnemySpotted,
                Sender = sender,
                Target = target,
                Position = position,
                Timestamp = System.Diagnostics.Stopwatch.GetTimestamp() / 10000000f
            });
        }

        /// <summary>包抄：通知队友从侧翼进攻。</summary>
        public void Flank(AITeamId teamId, ShanyuEngine.GameFramework.Actor sender,
            ShanyuEngine.GameFramework.Actor target, bool left)
        {
            Broadcast(teamId, new AIMessage
            {
                Type = left ? MessageType.FlankLeft : MessageType.FlankRight,
                Sender = sender,
                Target = target,
                Timestamp = System.Diagnostics.Stopwatch.GetTimestamp() / 10000000f
            });
        }
    }

    // ========================================================================
    //  九、AI 控制器 (AIController)
    // ========================================================================

    /// <summary>
    /// AI 控制器。继承 PlayerController，管理 AI 决策、状态、感知、行为树和导航。
    /// </summary>
    public class AIController : ShanyuEngine.GameFramework.PlayerController
    {
        // ── AI 状态 ──────────────────────────────────────────────

        private readonly Dictionary<AIStateType, AIState> _states = new Dictionary<AIStateType, AIState>();
        private readonly List<AIStateTransition> _transitions = new List<AIStateTransition>();
        private AIState _currentState;
        public AIStateType CurrentStateType => _currentState?.StateType ?? AIStateType.Idle;

        /// <summary>状态变更回调。</summary>
        public Action<AIStateType, AIStateType> OnStateChanged { get; set; }

        // ── 行为树集成 ────────────────────────────────────────────

        public BehaviorTree CurrentBehaviorTree { get; set; }
        public BehaviorTreeInstance BehaviorTreeInstance { get; set; }

        // ── 感知系统 ────────────────────────────────────────────

        public AIPerceptionComponent Perception { get; private set; } = new AIPerceptionComponent();

        // ── 导航代理 ────────────────────────────────────────────

        public NavAgent NavAgent { get; set; }

        // ── 决策器 ──────────────────────────────────────────────

        public AIDecisionMaker DecisionMaker { get; } = new AIDecisionMaker();

        // ── 记忆 ────────────────────────────────────────────────

        public AIMemory Memory { get; } = new AIMemory();

        // ── 通讯 ────────────────────────────────────────────────

        public AICommunication Communication { get; set; } = new AICommunication();
        public AITeamId TeamId { get; set; } = AITeamId.None;

        // ── 属性 ────────────────────────────────────────────────

        public float HealthPercent { get; set; } = 1f;

        /// <summary>获取位置。</summary>
        public new Vector3 Position => PossessedPawn?.Position ?? Vector3.Zero;

        // ── 生命周期 ────────────────────────────────────────────

        /// <summary>占据 Pawn 时初始化 AI 系统。</summary>
        public override void Possess(ShanyuEngine.GameFramework.Pawn pawn)
        {
            base.Possess(pawn);
            InitializeAI();
        }

        private void InitializeAI()
        {
            // 初始化感知
            Perception.Initialize(PossessedPawn);

            // 注册默认状态
            RegisterDefaultStates();
            RegisterDefaultTransitions();

            // 注册默认行动
            RegisterDefaultActions();

            // 初始化通讯
            if (TeamId != AITeamId.None)
                Communication.RegisterMember(TeamId, this);

            // 启动行为树
            StartBehaviorTree();
        }

        private void RegisterDefaultStates()
        {
            _states[AIStateType.Idle] = new AIState { StateType = AIStateType.Idle, StateName = "Idle" };
            _states[AIStateType.Patrol] = new AIState { StateType = AIStateType.Patrol, StateName = "Patrol" };
            _states[AIStateType.Alert] = new AIState { StateType = AIStateType.Alert, StateName = "Alert", MaxDuration = 5f };
            _states[AIStateType.Chase] = new AIState { StateType = AIStateType.Chase, StateName = "Chase" };
            _states[AIStateType.Attack] = new AIState { StateType = AIStateType.Attack, StateName = "Attack" };
            _states[AIStateType.Flee] = new AIState { StateType = AIStateType.Flee, StateName = "Flee" };
            _states[AIStateType.Search] = new AIState { StateType = AIStateType.Search, StateName = "Search", MaxDuration = 15f };
            _states[AIStateType.Dead] = new AIState { StateType = AIStateType.Dead, StateName = "Dead" };
            _states[AIStateType.HoldPosition] = new AIState { StateType = AIStateType.HoldPosition, StateName = "HoldPosition" };
            _states[AIStateType.Follow] = new AIState { StateType = AIStateType.Follow, StateName = "Follow" };

            _currentState = _states[AIStateType.Idle];
            _currentState.Enter(this);
        }

        private void RegisterDefaultTransitions()
        {
            // 发现敌人 -> Alert
            AddTransition(new AIStateTransition(AIStateType.Idle, AIStateType.Alert,
                ctrl => ctrl.Perception.GetKnownTargets().Count > 0, 1));
            AddTransition(new AIStateTransition(AIStateType.Patrol, AIStateType.Alert,
                ctrl => ctrl.Perception.GetKnownTargets().Count > 0, 1));

            // Alert -> Chase
            AddTransition(new AIStateTransition(AIStateType.Alert, AIStateType.Chase,
                ctrl => ctrl.Perception.GetKnownTargets().Count > 0, 2));

            // 敌人近 -> Attack
            AddTransition(new AIStateTransition(AIStateType.Chase, AIStateType.Attack,
                ctrl => {
                    var target = ctrl.Memory.GetClosestTarget(ctrl.Position);
                    return target != null && Vector3.Distance(ctrl.Position, target.Position) < 5f;
                }, 3));

            // 低血量 -> Flee
            AddTransition(new AIStateTransition(AIStateType.Chase, AIStateType.Flee,
                ctrl => ctrl.HealthPercent < 0.3f, 5));
            AddTransition(new AIStateTransition(AIStateType.Attack, AIStateType.Flee,
                ctrl => ctrl.HealthPercent < 0.3f, 5));

            // 死亡
            AddTransition(new AIStateTransition(AIStateType.Idle, AIStateType.Dead,
                ctrl => ctrl.HealthPercent <= 0f, 100));
            AddTransition(new AIStateTransition(AIStateType.Attack, AIStateType.Dead,
                ctrl => ctrl.HealthPercent <= 0f, 100));
        }

        private void RegisterDefaultActions()
        {
            DecisionMaker.RegisterAction(new AIAction_Patrol());
            DecisionMaker.RegisterAction(new AIAction_Chase());
            DecisionMaker.RegisterAction(new AIAction_Attack());
            DecisionMaker.RegisterAction(new AIAction_Flee());
            DecisionMaker.RegisterAction(new AIAction_Search());
            DecisionMaker.RegisterAction(new AIAction_UseCover());
            DecisionMaker.RegisterAction(new AIAction_Heal());
            DecisionMaker.RegisterAction(new AIAction_Rally());
        }

        // ── 每帧更新 ────────────────────────────────────────────

        /// <summary>AI 控制器每帧 Tick。</summary>
        public void TickAI(float deltaTime)
        {
            // 更新感知
            Perception.Update(deltaTime);

            // 更新感知记忆
            var knownTargets = Perception.GetKnownTargets();
            foreach (var target in knownTargets)
            {
                if (target.Key != null)
                {
                    var stimulus = target.Value;
                    Memory.UpdateTarget(target.Key, stimulus.Position, stimulus.Intensity);
                }
            }
            Memory.Update(deltaTime, Position);

            // 处理通讯
            Communication.ProcessMessages(System.Diagnostics.Stopwatch.GetTimestamp() / 10000000f);

            // 状态机更新
            UpdateStateMachine(deltaTime);

            // 决策器更新
            DecisionMaker.Update(this, deltaTime);

            // 行为树更新
            BehaviorTreeInstance?.Update(deltaTime);
        }

        private void UpdateStateMachine(float deltaTime)
        {
            // 检查状态转换
            var bestTransition = FindBestTransition();
            if (bestTransition != null)
            {
                var prevState = _currentState?.StateType ?? AIStateType.Idle;
                _currentState?.Exit(this);
                bestTransition.OnTransition?.Invoke(this);
                _currentState = _states.GetValueOrDefault(bestTransition.ToState);
                _currentState?.Enter(this);
                OnStateChanged?.Invoke(prevState, _currentState?.StateType ?? AIStateType.Idle);
                return;
            }

            // 检查状态持续时间
            if (_currentState != null)
            {
                _currentState.Update(this, deltaTime);
                if (_currentState.IsDurationExpired())
                {
                    var prevState = _currentState.StateType;
                    _currentState.Exit(this);
                    _currentState = _states[AIStateType.Idle];
                    _currentState.Enter(this);
                    OnStateChanged?.Invoke(prevState, AIStateType.Idle);
                }
            }
        }

        private AIStateTransition FindBestTransition()
        {
            AIStateTransition best = null;
            int bestPriority = int.MaxValue;

            foreach (var transition in _transitions)
            {
                if (transition.FromState != _currentState?.StateType) continue;
                if (transition.Evaluate(this))
                {
                    if (transition.Priority < bestPriority)
                    {
                        bestPriority = transition.Priority;
                        best = transition;
                    }
                }
            }
            return best;
        }

        // ── 状态管理 ────────────────────────────────────────────

        public void AddState(AIState state)
        {
            if (state != null)
                _states[state.StateType] = state;
        }

        public void AddTransition(AIStateTransition transition)
        {
            if (transition != null)
                _transitions.Add(transition);
        }

        public void ForceState(AIStateType stateType)
        {
            if (_states.TryGetValue(stateType, out var state))
            {
                var prev = _currentState?.StateType ?? AIStateType.Idle;
                _currentState?.Exit(this);
                _currentState = state;
                _currentState.Enter(this);
                OnStateChanged?.Invoke(prev, stateType);
            }
        }

        // ── 行为树管理 ────────────────────────────────────────────

        public void StartBehaviorTree()
        {
            if (CurrentBehaviorTree != null)
            {
                BehaviorTreeInstance = CurrentBehaviorTree.CreateInstance();
                BehaviorTreeManager.Instance.ActivateInstance(BehaviorTreeInstance);
            }
        }

        public void StopBehaviorTree()
        {
            if (BehaviorTreeInstance != null)
            {
                BehaviorTreeManager.Instance.DeactivateInstance(BehaviorTreeInstance);
                BehaviorTreeInstance.Abort();
                BehaviorTreeInstance = null;
            }
        }

        public void RestartBehaviorTree()
        {
            StopBehaviorTree();
            StartBehaviorTree();
        }

        // ── 行动执行 ────────────────────────────────────────────

        public void PerformAttack(ShanyuEngine.GameFramework.Actor target)
        {
            if (target == null || PossessedPawn == null) return;
            // 攻击逻辑由具体游戏实现
        }

        public void MoveTo(Vector3 target)
        {
            NavAgent?.MoveTo(target);
        }

        public void StopMovement()
        {
            NavAgent?.Stop();
        }
    }
}
