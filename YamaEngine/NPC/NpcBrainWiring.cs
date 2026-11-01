// ════════════════════════════════════════════════════════════════════
// NpcBrainWiring.cs — 心智接入既有行为树(BehaviorTree) / 状态树(StateTree)
//
// 目标：把 NpcBrain 的 LLM 引导意图作为行为树/状态树的**决策源**，实现真正的
// "心智接入行为树/StateTree"。链路：
//   LLM 决策 → NpcBrain(心智: 个性/情感/记忆/关系/日程) → 意图(JSON)
//           → BTService_NpcBrain 同步意图到黑板 (条件分支可读)
//           → BTTask_NpcIntent / StateTreeTask_NpcIntent 执行意图
//
// 行为树：BTService_NpcBrain(服务，把意图写入黑板) + BTTask_NpcIntent(任务，执行意图)。
// 状态树：StateTreeTask_NpcIntent(任务，执行意图，复用黑板)。
//
// 全部 CPU 级确定性，NpcBrain 经 UserData 或黑板动态键 "NpcBrain" 注入。
// ════════════════════════════════════════════════════════════════════

using System.Numerics;
using YamaEngine.AI;

namespace YamaEngine.NPC;

/// <summary>黑板键名约定（NpcBrain 与行为树/状态树共享数据协议）。</summary>
public static class NpcBrainKeys
{
    public const string Brain = "NpcBrain";           // 黑板动态键：NpcBrain 引用
    public const string IntentKind = "NpcIntentKind"; // 意图类型字符串 (GoTo/Flee/...)
    public const string GoalX = "NpcGoalX";
    public const string GoalZ = "NpcGoalZ";
    public const string Priority = "NpcPriority";
    public const string Reason = "NpcDecisionReason";
    public const string Threat = "NpcThreat";         // Flee 威胁位置 (Vector3)
}

/// <summary>从上下文解析 NpcBrain（UserData 优先，其次黑板动态键）。</summary>
public static class NpcBrainResolver
{
    public static NpcBrain? FromContext(object? userData, Blackboard? blackboard)
        => userData as NpcBrain ?? blackboard?.GetDynamicValue<NpcBrain>(NpcBrainKeys.Brain);
}

/// <summary>
/// 行为树服务：把 NpcBrain 当前意图同步到黑板，供 BT 条件分支判断。
/// 这是"LLM 心智 → 行为树黑板"的桥接层。
/// </summary>
public sealed class BTService_NpcBrain : BTService
{
    public BTService_NpcBrain() { ServiceName = "NpcBrainSync"; TickInterval = 0.2f; }

    protected override void PerformService(BTNodeContext ctx)
    {
        var brain = NpcBrainResolver.FromContext(ctx.UserData, ctx.Blackboard);
        if (brain == null || ctx.Blackboard == null) return;
        var intent = brain.CurrentIntent;
        ctx.Blackboard.SetDynamicValue(NpcBrainKeys.IntentKind, intent?.IntentKind.ToString() ?? "");
        ctx.Blackboard.SetDynamicValue(NpcBrainKeys.GoalX, intent?.X ?? 0f);
        ctx.Blackboard.SetDynamicValue(NpcBrainKeys.GoalZ, intent?.Z ?? 0f);
        ctx.Blackboard.SetDynamicValue(NpcBrainKeys.Priority, intent?.Priority ?? 0f);
        ctx.Blackboard.SetDynamicValue(NpcBrainKeys.Reason, intent?.Reason ?? "");
    }
}

/// <summary>
/// 行为树任务：执行 NpcBrain 当前 LLM 意图。
/// GoTo/Flee 逐帧推进（Running→Success），TalkTo 直接成功（对话由上层处理），
/// Idle/Work/Socialize/Quest/Wander 视为成功（由 StateTree/上层处理）。
/// 无意图或未接入 NpcBrain 时返回 Failed。
/// </summary>
public sealed class BTTask_NpcIntent : BTTaskV1
{
    public BTTask_NpcIntent() { NodeName = "NpcIntent"; }

    protected override BTNodeState ExecuteTask(BTNodeContext ctx)
    {
        var brain = NpcBrainResolver.FromContext(ctx.UserData, ctx.Blackboard);
        if (brain == null || brain.CurrentIntent == null) return BTNodeState.Failed;

        switch (brain.CurrentIntent.IntentKind)
        {
            case NpcIntentKind.GoTo:
            case NpcIntentKind.Flee:
                var threat = ctx.Blackboard?.GetDynamicValue<Vector3>(NpcBrainKeys.Threat) ?? Vector3.Zero;
                brain.Tick(ctx.DeltaTime, threat);
                return brain.IsExecuting ? BTNodeState.Running : BTNodeState.Success;
            default:
                // TalkTo/Idle/Work/Socialize/Quest/Wander：行为由上层/状态树处理
                return BTNodeState.Success;
        }
    }
}

/// <summary>
/// 状态树任务：执行 NpcBrain 当前 LLM 意图（对齐 UE5 StateTreeTask TickState）。
/// NpcBrain 从黑板动态键 "NpcBrain" 注入（StateTreeContext 无 UserData）。
/// </summary>
public sealed class StateTreeTask_NpcIntent : StateTreeTask
{
    public StateTreeTask_NpcIntent() { Name = "NpcIntent"; }

    public override EStateTreeRunStatus Tick(StateTreeContext ctx)
    {
        var brain = NpcBrainResolver.FromContext(null, ctx.Blackboard);
        if (brain == null || brain.CurrentIntent == null) return EStateTreeRunStatus.Failed;

        switch (brain.CurrentIntent.IntentKind)
        {
            case NpcIntentKind.GoTo:
            case NpcIntentKind.Flee:
                var threat = ctx.Blackboard?.GetDynamicValue<Vector3>(NpcBrainKeys.Threat) ?? Vector3.Zero;
                brain.Tick(ctx.DeltaTime, threat);
                return brain.IsExecuting ? EStateTreeRunStatus.Running : EStateTreeRunStatus.Succeeded;
            default:
                return EStateTreeRunStatus.Succeeded;
        }
    }
}
