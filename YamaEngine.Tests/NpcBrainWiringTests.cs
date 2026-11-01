// NpcBrainWiringTests.cs — 心智接入行为树/状态树 验证套件
// 验证 NpcBrain(LLM引导意图) 真正接入既有 BehaviorTree / StateTree 运行实例：
//   BTService_NpcBrain 同步意图到黑板、BTTask_NpcIntent 执行意图、
//   StateTreeTask_NpcIntent 执行意图。
// 全部 CPU 级确定性，NpcBrain 经 Context.UserData / 黑板动态键注入。
using System;
using System.Numerics;
using YamaEngine.AI;
using YamaEngine.NPC;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests;

public static class NpcBrainWiringTests
{
    // ── 行为树任务：执行 LLM 意图 ──────────────────────────────────

    public static void TestBTTaskNpcIntentExecutesGoToToCompletion()
    {
        var npc = new NpcCharacter { Name = "旅人", Position = new Vector3(0, 0, 0) };
        var brain = new NpcBrain(npc, moveSpeed: 3f);
        brain.ApplyIntent(new NpcIntent { Kind = "GoTo", X = 10f, Z = 0f, Priority = 0.8f, Reason = "去广场" });

        var task = new BTTask_NpcIntent();
        var tree = new BehaviorTree { AssetName = "NpcBrainTree", RootNode = task };
        var inst = tree.CreateInstance();
        inst.Context.UserData = brain;

        // 第一帧：Running 且 NPC 开始移动
        var r1 = inst.Update(0.5f);
        AssertTrue(r1 == BTNodeState.Running, "GoTo 未到达前应为 Running。");
        AssertTrue(npc.Position.X > 0f, "行为树应推进 NPC 向目标移动。");

        // 持续推进直到到达
        BTNodeState last = r1;
        for (int i = 0; i < 100; i++)
        {
            last = inst.Update(0.5f);
            if (last == BTNodeState.Success) break;
        }
        AssertTrue(last == BTNodeState.Success, "到达后行为树应返回 Success。");
        AssertTrue(Math.Abs(npc.Position.X - 10f) < 1e-3f, "NPC 应到达目标。");
        AssertTrue(!brain.IsExecuting, "完成后应停止执行。");
    }

    public static void TestBTTaskNpcIntentFailsWithoutBrain()
    {
        var task = new BTTask_NpcIntent();
        var tree = new BehaviorTree { AssetName = "EmptyTree", RootNode = task };
        var inst = tree.CreateInstance(); // 不注入 UserData/黑板

        var r = inst.Update(0.5f);
        AssertTrue(r == BTNodeState.Failed, "未接入 NpcBrain 时应返回 Failed。");
    }

    public static void TestBTTaskNpcIntentTalkToSucceedsImmediately()
    {
        var npc = new NpcCharacter { Name = "交谈者", Position = new Vector3(2, 0, 2) };
        var brain = new NpcBrain(npc);
        brain.ApplyIntent(new NpcIntent { Kind = "TalkTo", Priority = 0.6f, Reason = "交谈" });

        var task = new BTTask_NpcIntent();
        var tree = new BehaviorTree { AssetName = "TalkTree", RootNode = task };
        var inst = tree.CreateInstance();
        inst.Context.UserData = brain;

        var r = inst.Update(0.5f);
        AssertTrue(r == BTNodeState.Success, "TalkTo 应直接成功（对话由上层处理）。");
        AssertTrue(npc.Position.X == 2f && npc.Position.Z == 2f, "TalkTo 不应移动。");
    }

    public static void TestBTTaskNpcIntentFleeMovesAway()
    {
        var npc = new NpcCharacter { Name = "逃亡者", Position = new Vector3(1, 0, 0) };
        var brain = new NpcBrain(npc, moveSpeed: 3f);
        brain.ApplyIntent(new NpcIntent { Kind = "Flee", Priority = 0.9f, Reason = "遇袭" });

        var task = new BTTask_NpcIntent();
        var tree = new BehaviorTree { AssetName = "FleeTree", RootNode = task };
        var inst = tree.CreateInstance();
        inst.Context.UserData = brain;
        // 威胁在原点
        inst.Blackboard.SetDynamicValue(NpcBrainKeys.Threat, new Vector3(0, 0, 0));

        var r = inst.Update(0.5f);
        AssertTrue(npc.Position.X > 1f, "Flee 应远离威胁。");
        AssertTrue(r == BTNodeState.Running, "Flee 未脱离应保持 Running。");
    }

    // ── 行为树服务：同步心智到黑板 ──────────────────────────────────

    public static void TestBTServiceSyncsIntentToBlackboard()
    {
        var npc = new NpcCharacter { Name = "村民", Position = new Vector3(0, 0, 0) };
        var brain = new NpcBrain(npc);
        brain.ApplyIntent(new NpcIntent
        {
            Kind = "Socialize", X = 5f, Z = -2f, Priority = 0.7f, Reason = "社交时段"
        });

        var task = new BTTask_NpcIntent();
        task.AddService(new BTService_NpcBrain());
        var tree = new BehaviorTree { AssetName = "SyncTree", RootNode = task };
        var inst = tree.CreateInstance();
        inst.Context.UserData = brain;

        inst.Update(0.5f); // dt 0.5 > TickInterval 0.2 → 触发服务

        AssertEqual("Socialize", inst.Blackboard.GetDynamicValue<string>(NpcBrainKeys.IntentKind),
            "黑板应同步意图类型。");
        AssertEqual(5f, inst.Blackboard.GetDynamicValue<float>(NpcBrainKeys.GoalX), "黑板应同步目标X。");
        AssertEqual(-2f, inst.Blackboard.GetDynamicValue<float>(NpcBrainKeys.GoalZ), "黑板应同步目标Z。");
        AssertEqual(0.7f, inst.Blackboard.GetDynamicValue<float>(NpcBrainKeys.Priority), "黑板应同步优先级。");
        AssertEqual("社交时段", inst.Blackboard.GetDynamicValue<string>(NpcBrainKeys.Reason), "黑板应同步理由。");
    }

    // ── 状态树任务：执行 LLM 意图 ──────────────────────────────────

    public static void TestStateTreeTaskNpcIntentExecutesGoTo()
    {
        var npc = new NpcCharacter { Name = "信使", Position = new Vector3(0, 0, 0) };
        var brain = new NpcBrain(npc, moveSpeed: 3f);
        brain.ApplyIntent(new NpcIntent { Kind = "GoTo", X = 6f, Z = 0f, Priority = 0.8f, Reason = "送信" });

        var bb = new BTBlackboardDefinition().CreateBlackboard();
        bb.SetDynamicValue(NpcBrainKeys.Brain, brain);

        var ctx = new StateTreeContext(1, bb);
        var task = new StateTreeTask_NpcIntent();

        ctx.DeltaTime = 0.5f; // 框架每帧由 StateTreeSystem 设置
        var r1 = task.Tick(ctx);
        AssertTrue(r1 == EStateTreeRunStatus.Running, "GoTo 未到达前应为 Running。");
        AssertTrue(npc.Position.X > 0f, "状态树应推进 NPC 移动。");

        EStateTreeRunStatus last = r1;
        for (int i = 0; i < 100; i++)
        {
            ctx.DeltaTime = 0.5f;
            last = task.Tick(ctx);
            if (last == EStateTreeRunStatus.Succeeded) break;
        }
        AssertTrue(last == EStateTreeRunStatus.Succeeded, "到达后状态树应返回 Succeeded。");
        AssertTrue(Math.Abs(npc.Position.X - 6f) < 1e-3f, "NPC 应到达目标。");
    }

    public static void TestStateTreeTaskNpcIntentFailsWithoutBrain()
    {
        var bb = new BTBlackboardDefinition().CreateBlackboard();
        var ctx = new StateTreeContext(1, bb); // 黑板无 NpcBrain 键
        var task = new StateTreeTask_NpcIntent();

        var r = task.Tick(ctx);
        AssertTrue(r == EStateTreeRunStatus.Failed, "未接入 NpcBrain 时应返回 Failed。");
    }

    // ── 全链路集成：LLM决策 → NpcBrain → 行为树 ────────────────────

    public static void TestFullChainLLMDecisionDrivesBehaviorTree()
    {
        var npc = new NpcCharacter { Name = "商队护卫", Position = new Vector3(0, 0, 0) };
        var brain = new NpcBrain(npc, moveSpeed: 3f);

        // LLM 决策（离线 mock 返回 GoTo 意图）
        var mock = new IntentLLMProvider
        {
            CompleteReply = "{\"kind\":\"GoTo\",\"x\":8,\"z\":4,\"priority\":0.9,\"reason\":\"护送商队\"}"
        };
        var client = new LLMClient(new LLMConfig(LLMPlatform.OpenAI, "", "test-model")
        { RateLimitPerMinute = 1000 });
        client.Register(mock);

        var intent = brain.DecideAsync("商队启程", client).GetAwaiter().GetResult();
        AssertTrue(intent != null && intent.IntentKind == NpcIntentKind.GoTo, "LLM 应决策出 GoTo 意图。");

        // 行为树执行该意图
        var task = new BTTask_NpcIntent();
        var tree = new BehaviorTree { AssetName = "LLMChainTree", RootNode = task };
        var inst = tree.CreateInstance();
        inst.Context.UserData = brain;

        inst.Update(0.5f);
        AssertTrue(npc.Position.X > 0f || npc.Position.Z > 0f, "行为树应驱动 NPC 移动。");
        AssertTrue(brain.CurrentIntent != null && brain.CurrentIntent.Reason == "护送商队",
            "LLM 意图应被应用到大脑。");
        AssertTrue(mock.LastPrompt != null && mock.LastPrompt.Contains("商队护卫"),
            "LLM 应收到心智注入提示。");
    }
}
