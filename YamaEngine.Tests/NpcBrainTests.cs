// NpcBrainTests.cs — LLM 引导行为驱动的 AI NPC 验证套件
// 覆盖：意图解析(含 markdown 剥除/非法回退)、LLM 决策→应用、启发式回退、
//      行为执行(GoTo 移动/Flee 远离/TalkTo 保持)、任务系统、群体社交。
// 全部 CPU 级确定性，LLM 用离线 mock（不触网）。
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using YamaEngine.AI;
using YamaEngine.NPC;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests;

/// <summary>离线 mock LLM provider（用于大脑决策测试）：CompleteAsync 返回可配置 JSON。</summary>
public sealed class IntentLLMProvider : ILLMProvider
{
    public LLMPlatform Platform => LLMPlatform.OpenAI;
    public string CompleteReply { get; set; } = "{\"kind\":\"Idle\",\"priority\":0.5,\"reason\":\"mock\"}";
    public string? LastPrompt { get; private set; }

    public Task<LLMResponse> ChatAsync(LLMConversation conv, LLMConfig cfg, CancellationToken ct = default)
        => Task.FromResult(new LLMResponse { Success = true, Content = "", Platform = LLMPlatform.OpenAI });

    public Task<LLMResponse> CompleteAsync(string prompt, LLMConfig cfg, CancellationToken ct = default)
    {
        LastPrompt = prompt;
        return Task.FromResult(new LLMResponse { Success = true, Content = CompleteReply, Platform = LLMPlatform.OpenAI });
    }

    public Task<float[]> EmbedAsync(string text, LLMConfig cfg, CancellationToken ct = default)
        => Task.FromResult(Array.Empty<float>());

    public Task StreamChatAsync(LLMConversation conv, LLMConfig cfg, Action<string> onToken, CancellationToken ct = default)
        => Task.CompletedTask;
}

public static class NpcBrainTests
{
    // ── 意图解析 ────────────────────────────────────────────────────

    public static void TestParseIntentPlainJson()
    {
        var intent = NpcBrain.ParseIntent(
            "{\"kind\":\"GoTo\",\"target\":\"集市\",\"x\":5,\"z\":-3,\"priority\":0.8,\"reason\":\"去采购\"}");
        AssertTrue(intent != null, "应能解析合法 JSON 意图。");
        AssertTrue(intent!.IntentKind == NpcIntentKind.GoTo, "意图类型应为 GoTo。");
        AssertTrue(intent.Target == "集市", "意图目标应保留。");
        AssertTrue(intent.TargetPosition.X == 5f && intent.TargetPosition.Z == -3f, "意图坐标应还原。");
        AssertTrue(Math.Abs(intent.Priority - 0.8f) < 1e-4f, "意图优先级应还原。");
        AssertTrue(intent.Reason == "去采购", "意图理由应还原。");
        AssertTrue(intent.IsValid, "优先级>0 的意图应有效。");
    }

    public static void TestParseIntentMarkdownCodeBlock()
    {
        var intent = NpcBrain.ParseIntent("```json\n{\"kind\":\"Flee\",\"priority\":0.9,\"reason\":\"危险\"}\n```");
        AssertTrue(intent != null, "应能剥除 markdown 代码块解析。");
        AssertTrue(intent!.IntentKind == NpcIntentKind.Flee, "剥除代码块后应为 Flee。");
    }

    public static void TestParseIntentInvalidReturnsNull()
    {
        AssertTrue(NpcBrain.ParseIntent(null) == null, "空输入应返回 null。");
        AssertTrue(NpcBrain.ParseIntent("   ") == null, "空白输入应返回 null。");
        AssertTrue(NpcBrain.ParseIntent("这不是 JSON") == null, "非 JSON 应返回 null。");
        AssertTrue(NpcBrain.ParseIntent("{\"kind\":\"Bogus\",\"priority\":0.5}") == null, "非法枚举应返回 null。");
    }

    // ── 决策提示词 ──────────────────────────────────────────────────

    public static void TestBuildIntentPromptInjectsMentality()
    {
        var npc = new NpcCharacter
        {
            Name = "商人玛尔",
            Occupation = "商人",
            Faction = "商人工会",
            Personality = new NpcPersonality(0.7f, 0.6f, 0.8f, 0.5f, 0.3f)
        };
        npc.Emotion.Trigger(NpcEmotionType.Joy, 0.6f);
        npc.GetRelationship("玩家").Change(40f);
        npc.Memory.Remember(NpcMemoryKind.Event, "集市", "最近集市行情好", importance: 0.8f);

        var brain = new NpcBrain(npc);
        var prompt = brain.BuildIntentPrompt("玩家路过摊位");

        AssertTrue(prompt.Contains("商人玛尔"), "提示应含 NPC 名字。");
        AssertTrue(prompt.Contains("商人工会"), "提示应含阵营。");
        AssertTrue(prompt.Contains("外向"), "提示应注入性格描述。");
        AssertTrue(prompt.Contains("愉悦"), "提示应注入主导情感。");
        AssertTrue(prompt.Contains("40"), "提示应注入关系值。");
        AssertTrue(prompt.Contains("行情好"), "提示应注入记忆。");
        AssertTrue(prompt.Contains("JSON"), "提示应要求输出 JSON。");
    }

    // ── LLM 决策 → 应用 ────────────────────────────────────────────

    public static void TestDecideAsyncAppliesIntentFromLLM()
    {
        var npc = new NpcCharacter { Name = "旅人" };
        var mock = new IntentLLMProvider
        {
            CompleteReply = "{\"kind\":\"GoTo\",\"x\":10,\"z\":0,\"priority\":0.9,\"reason\":\"去广场\"}"
        };
        var client = new LLMClient(new LLMConfig(LLMPlatform.OpenAI, "", "test-model")
        { RateLimitPerMinute = 1000 });
        client.Register(mock);

        var brain = new NpcBrain(npc);
        var intent = brain.DecideAsync("有人招手", client).GetAwaiter().GetResult();

        AssertTrue(intent != null, "LLM 决策应返回意图。");
        AssertTrue(intent!.IntentKind == NpcIntentKind.GoTo, "意图应为 GoTo。");
        AssertTrue(brain.CurrentIntent == intent, "当前意图应被设置。");
        AssertTrue(brain.Goal.X == 10f && brain.Goal.Z == 0f, "目标应被应用。");
        AssertTrue(brain.IsExecuting, "应处于执行状态。");
        AssertTrue(brain.LastDecisionReason == "去广场", "应记录决策理由。");
        AssertTrue(mock.LastPrompt != null && mock.LastPrompt.Contains("旅人"), "应把情境提示发给 LLM。");
    }

    public static void TestDecideAsyncFallsBackToHeuristicOnBadJson()
    {
        var npc = new NpcCharacter { Name = "村民" };
        var mock = new IntentLLMProvider { CompleteReply = "完全不是 JSON" };
        var client = new LLMClient(new LLMConfig(LLMPlatform.OpenAI, "", "test-model")
        { RateLimitPerMinute = 1000 });
        client.Register(mock);

        var brain = new NpcBrain(npc) { FallbackToHeuristic = true };
        var intent = brain.DecideAsync("任意", client).GetAwaiter().GetResult();

        AssertTrue(intent != null, "解析失败时应回退到启发式。");
        AssertTrue(brain.LastDecisionReason == "parse_failed", "应记录解析失败原因。");
        // 默认日程在 8:00 为工作时间 → 启发式应选择 Work
        AssertTrue(intent!.IntentKind == NpcIntentKind.Work, "8点启发式应为工作。");
        AssertTrue(brain.IsExecuting, "回退后仍应执行。");
    }

    public static void TestDecideAsyncNoFallbackReturnsNull()
    {
        var npc = new NpcCharacter { Name = "村民" };
        var mock = new IntentLLMProvider { CompleteReply = "garbage" };
        var client = new LLMClient(new LLMConfig(LLMPlatform.OpenAI, "", "test-model")
        { RateLimitPerMinute = 1000 });
        client.Register(mock);

        var brain = new NpcBrain(npc) { FallbackToHeuristic = false };
        var intent = brain.DecideAsync("任意", client).GetAwaiter().GetResult();

        AssertTrue(intent == null, "关闭回退时应返回 null。");
        AssertTrue(!brain.IsExecuting, "无回退时不应执行。");
    }

    // ── 启发式回退按日程 ────────────────────────────────────────────

    public static void TestApplyHeuristicRespectsCustomSchedule()
    {
        var npc = new NpcCharacter { Name = "演员" };
        // 自定义日程：8:00-20:00 全程社交
        npc.Schedule = new NpcSchedule();
        npc.Schedule.Activities.Add(new NpcScheduleActivity(NpcActivityKind.Social, 8f, 20f));

        var brain = new NpcBrain(npc);
        var intent = brain.ApplyHeuristic("任意");

        AssertTrue(intent.IntentKind == NpcIntentKind.Socialize, "自定义社交日程应生成 Socialize。");
        AssertTrue(brain.CurrentIntent == intent, "启发式意图应被应用。");
    }

    // ── 行为执行 ────────────────────────────────────────────────────

    public static void TestTickGoToMovesNpcTowardsGoal()
    {
        var npc = new NpcCharacter { Name = "行商", Position = new System.Numerics.Vector3(0, 0, 0) };
        var brain = new NpcBrain(npc, moveSpeed: 3f);
        brain.ApplyIntent(new NpcIntent
        {
            Kind = "GoTo", X = 10f, Z = 0f, Priority = 0.8f, Reason = "出发"
        });

        float x0 = npc.Position.X;
        for (int i = 0; i < 10; i++) brain.Tick(0.1f); // 1 秒

        AssertTrue(npc.Position.X > x0, "GoTo 应向目标移动。");
        AssertTrue(npc.Position.X < 10f, "尚未到达目标。");

        // 继续直到到达
        for (int i = 0; i < 200; i++) brain.Tick(0.1f);
        AssertTrue(Math.Abs(npc.Position.X - 10f) < 1e-3f, "最终应到达目标。");
        AssertTrue(!brain.IsExecuting, "到达后应停止执行。");
    }

    public static void TestTickFleeMovesAwayFromThreat()
    {
        var npc = new NpcCharacter { Name = "逃亡者", Position = new System.Numerics.Vector3(1, 0, 0) };
        var brain = new NpcBrain(npc, moveSpeed: 3f);
        brain.ApplyIntent(new NpcIntent { Kind = "Flee", Priority = 0.9f, Reason = "遇袭" });

        var threat = new System.Numerics.Vector3(0, 0, 0);
        float dist0 = System.Numerics.Vector3.Distance(npc.Position, threat);
        for (int i = 0; i < 10; i++) brain.Tick(0.1f, threat);
        float dist1 = System.Numerics.Vector3.Distance(npc.Position, threat);

        AssertTrue(npc.Position.X > 1f, "Flee 应远离威胁。");
        AssertTrue(dist1 > dist0, "与威胁距离应增大。");
    }

    public static void TestTickTalkToKeepsPosition()
    {
        var npc = new NpcCharacter { Name = "交谈者", Position = new System.Numerics.Vector3(2, 0, 2) };
        var brain = new NpcBrain(npc);
        brain.ApplyIntent(new NpcIntent { Kind = "TalkTo", Priority = 0.6f, Reason = "交谈" });

        for (int i = 0; i < 10; i++) brain.Tick(0.1f);
        AssertTrue(npc.Position.X == 2f && npc.Position.Z == 2f, "TalkTo 应保持位置。");
    }

    public static void TestStopClearsExecution()
    {
        var npc = new NpcCharacter { Name = "测试" };
        var brain = new NpcBrain(npc);
        brain.ApplyIntent(new NpcIntent { Kind = "Wander", Priority = 0.4f });
        AssertTrue(brain.IsExecuting, "应用意图后应执行。");
        brain.Stop();
        AssertTrue(!brain.IsExecuting, "Stop 应清除执行状态。");
        AssertTrue(brain.CurrentIntent == null, "Stop 应清除当前意图。");
    }

    // ── 任务系统 ────────────────────────────────────────────────────

    public static void TestQuestActivateAdvanceComplete()
    {
        var quest = new NpcQuest
        {
            Title = "收集草药", Kind = NpcQuestKind.Collect, Target = "草药", Required = 3
        };
        AssertTrue(quest.State == NpcQuestState.Available, "初始应为 Available。");

        quest.Advance(1); // 未激活不推进
        AssertTrue(quest.Progress == 0, "未激活不应推进。");

        quest.Activate();
        AssertTrue(quest.State == NpcQuestState.Active, "激活后应为 Active。");

        quest.Advance(2);
        AssertTrue(quest.Progress == 2, "进度应推进到 2。");
        AssertTrue(!quest.IsComplete, "未达要求不应完成。");

        quest.Advance(1);
        AssertTrue(quest.IsComplete, "达到要求应完成。");
        AssertTrue(quest.State == NpcQuestState.Completed, "完成后状态应为 Completed。");
        AssertTrue(Math.Abs(quest.ProgressRatio - 1f) < 1e-4f, "完成时进度比应为 1。");
    }

    public static void TestQuestFail()
    {
        var quest = new NpcQuest { Title = "护送", Kind = NpcQuestKind.Deliver, Required = 1 };
        quest.Activate();
        quest.Fail();
        AssertTrue(quest.State == NpcQuestState.Failed, "Fail 应置为 Failed。");
        AssertTrue(!quest.IsComplete, "失败任务不应为完成。");
    }

    public static void TestQuestSystemReportProgressTriggersEvent()
    {
        var sys = new NpcQuestSystem();
        var quest = sys.Add(new NpcQuest
        {
            Id = "q1", Title = "清剿狼群", Kind = NpcQuestKind.Kill, Target = "狼", Required = 2
        });
        quest.Activate();

        NpcQuest? completed = null;
        sys.OnQuestCompleted += q => completed = q;

        sys.ReportProgress(NpcQuestKind.Kill, "狼", 1); // 进度 1
        AssertTrue(completed == null, "未达要求不应触发完成。");
        AssertTrue(quest.Progress == 1, "进度应为 1。");

        sys.ReportProgress(NpcQuestKind.Kill, "狼", 1); // 进度 2 → 完成
        AssertTrue(completed == quest, "达到要求应触发完成事件。");
        AssertTrue(quest.IsComplete, "任务应完成。");
        AssertTrue(sys.Active.Count() == 0, "无活跃任务。");
    }

    public static void TestQuestSystemIgnoresMismatchedTarget()
    {
        var sys = new NpcQuestSystem();
        var quest = sys.Add(new NpcQuest
        {
            Id = "q2", Title = "采集蘑菇", Kind = NpcQuestKind.Collect, Target = "蘑菇", Required = 2
        });
        quest.Activate();

        sys.ReportProgress(NpcQuestKind.Collect, "蘑菇", 1);
        sys.ReportProgress(NpcQuestKind.Collect, "蘑菇", 1);
        AssertTrue(quest.IsComplete, "匹配目标应完成。");

        // 新任务 + 错误目标
        var quest2 = sys.Add(new NpcQuest
        {
            Id = "q3", Title = "采集浆果", Kind = NpcQuestKind.Collect, Target = "浆果", Required = 2
        });
        quest2.Activate();
        sys.ReportProgress(NpcQuestKind.Collect, "蘑菇", 2);
        AssertTrue(quest2.Progress == 0, "目标不匹配不应推进。");
    }

    // ── 群体社交 ────────────────────────────────────────────────────

    public static void TestSocialSimulationGeneratesEventForNearbySocialPair()
    {
        var world = new NpcWorld();
        var a = world.Register(new NpcCharacter { Name = "甲", Position = new System.Numerics.Vector3(0, 0, 0) });
        var b = world.Register(new NpcCharacter { Name = "乙", Position = new System.Numerics.Vector3(3, 0, 0) });
        // 19:00 为社交时段
        var sim = new NpcSocialSimulation();

        sim.Update(world, 0.016f, 19f);

        AssertTrue(sim.PendingEvents.Count == 1, "邻近社交对应生成 1 条事件。");
        var ev = sim.PendingEvents[0];
        AssertTrue(ev.Activity == NpcActivityKind.Social, "事件应为社交活动。");
        AssertTrue(a.GetRelationship("乙").Value > 0f, "社交应提升甲对乙的关系。");
        AssertTrue(b.GetRelationship("甲").Value > 0f, "社交应双向提升关系。");

        var drained = sim.Drain();
        AssertTrue(drained.Count == 1, "Drain 应取出事件。");
        AssertTrue(sim.PendingEvents.Count == 0, "Drain 后应清空。");
    }

    public static void TestSocialSimulationRespectsRadiusAndActivity()
    {
        var world = new NpcWorld();
        var a = world.Register(new NpcCharacter { Name = "甲", Position = new System.Numerics.Vector3(0, 0, 0) });
        var b = world.Register(new NpcCharacter { Name = "乙", Position = new System.Numerics.Vector3(20, 0, 0) });
        var sim = new NpcSocialSimulation();

        sim.Update(world, 0.016f, 19f); // 距离 20 > 半径 8
        AssertTrue(sim.PendingEvents.Count == 0, "超出交互半径不应生成事件。");

        // 拉近但处于工作时间 (10:00)
        b.Position = new System.Numerics.Vector3(3, 0, 0);
        sim.Update(world, 0.016f, 10f);
        AssertTrue(sim.PendingEvents.Count == 0, "非社交时段不应生成事件。");

        // 休闲时段 (22:00)
        sim.Update(world, 0.016f, 22f);
        AssertTrue(sim.PendingEvents.Count == 1, "休闲时段邻近应生成事件。");
    }

    public static void TestSocialSimulationCooldownSuppressesRepeat()
    {
        var world = new NpcWorld();
        var a = world.Register(new NpcCharacter { Name = "甲", Position = new System.Numerics.Vector3(0, 0, 0) });
        var b = world.Register(new NpcCharacter { Name = "乙", Position = new System.Numerics.Vector3(3, 0, 0) });
        var sim = new NpcSocialSimulation { EventCooldownSeconds = 10f };

        sim.Update(world, 0.016f, 19f);
        AssertTrue(sim.PendingEvents.Count == 1, "首次应生成事件。");

        sim.Update(world, 0.016f, 19f); // 同一小时再次扫描
        AssertTrue(sim.PendingEvents.Count == 0, "冷却期内不应重复生成事件。");
    }
}
