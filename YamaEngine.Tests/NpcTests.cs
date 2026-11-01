// NpcTests.cs — AI 驱动 NPC 系统（LLM 接线版）验证套件
// 全部 CPU 级确定性测试：个性/情感/记忆/关系/日程/对话 + LLM mock 端到端接线。
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using YamaEngine.AI;
using YamaEngine.NPC;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests;

/// <summary>离线 mock LLM provider：返回固定回复，不触网。</summary>
public sealed class MockLLMProvider : ILLMProvider
{
    public LLMPlatform Platform => LLMPlatform.OpenAI;
    public string Reply { get; set; } = "（NPC回复）你好，旅行者。";
    public LLMConversation? LastConversation { get; private set; }

    public Task<LLMResponse> ChatAsync(LLMConversation conv, LLMConfig cfg, CancellationToken ct = default)
    {
        LastConversation = conv;
        return Task.FromResult(new LLMResponse
        {
            Success = true, Content = Reply, FinishReason = "stop", Platform = LLMPlatform.OpenAI
        });
    }

    public Task<LLMResponse> CompleteAsync(string prompt, LLMConfig cfg, CancellationToken ct = default)
        => Task.FromResult(new LLMResponse { Success = true, Content = prompt, Platform = LLMPlatform.OpenAI });

    public Task<float[]> EmbedAsync(string text, LLMConfig cfg, CancellationToken ct = default)
        => Task.FromResult(Array.Empty<float>());

    public Task StreamChatAsync(LLMConversation conv, LLMConfig cfg, Action<string> onToken, CancellationToken ct = default)
    {
        onToken(Reply);
        return Task.CompletedTask;
    }
}

public static class NpcTests
{
    // ── 个性 ────────────────────────────────────────────────────────

    public static void TestPersonalityClampsAndDescribes()
    {
        var p = new NpcPersonality(1.5f, -0.2f, 0.7f, 0.5f, 0.3f);
        AssertTrue(p.Openness == 1f && p.Conscientiousness == 0f, "OCEAN 因子应钳制到 [0,1]。");

        var desc = p.Describe();
        AssertTrue(!string.IsNullOrWhiteSpace(desc), "个性描述不应为空。");
        AssertTrue(desc.Length > 5, "个性描述应包含实际内容。");
    }

    // ── 情感 ────────────────────────────────────────────────────────

    public static void TestEmotionTriggerDecayAndDominant()
    {
        var emo = new NpcEmotionState();
        emo.Trigger(NpcEmotionType.Joy, 0.8f);
        AssertTrue(emo.Get(NpcEmotionType.Joy) > 0.79f, "触发情感应设置强度。");
        AssertTrue(emo.Dominant == NpcEmotionType.Joy, "主导情感应为 Joy。");

        // 衰减 5 秒
        for (int i = 0; i < 50; i++) emo.Tick(0.1f);
        AssertTrue(emo.Get(NpcEmotionType.Joy) < 0.5f, "情感应随时间衰减。");
        AssertTrue(emo.DominantIntensity >= 0f, "主导强度应非负。");
    }

    public static void TestEmotionDominantTracksHighest()
    {
        var emo = new NpcEmotionState();
        emo.Trigger(NpcEmotionType.Fear, 0.2f);
        emo.Trigger(NpcEmotionType.Anger, 0.6f);
        AssertTrue(emo.Dominant == NpcEmotionType.Anger, "主导应为最高强度情感。");
    }

    // ── 记忆 ────────────────────────────────────────────────────────

    public static void TestMemoryRememberDecayForgetAndSummarize()
    {
        var mem = new NpcMemory();
        mem.Remember(NpcMemoryKind.Encounter, "商人", "在集市遇见了商人", importance: 1f);
        mem.Remember(NpcMemoryKind.Dialogue, "守卫", "守卫提醒注意宵禁", importance: 0.8f);
        AssertTrue(mem.Entries.Count == 2, "应记录两条记忆。");
        AssertTrue(mem.Summarize().Contains("商人") || mem.Summarize().Contains("守卫"),
            "记忆摘要应包含记忆内容。");

        // 长时间衰减，低重要度记忆被遗忘
        for (int i = 0; i < 6000; i++) mem.Tick(0.1f);
        AssertTrue(mem.Entries.Count < 2, "低重要度记忆应随时间被遗忘。");
    }

    public static void TestMemoryCapacityEvictsWeakest()
    {
        var mem = new NpcMemory { Capacity = 3 };
        mem.Remember(NpcMemoryKind.Fact, "A", "弱记忆A", importance: 0.1f, strength: 0.2f);
        mem.Remember(NpcMemoryKind.Fact, "B", "强记忆B", importance: 1f, strength: 1f);
        mem.Remember(NpcMemoryKind.Fact, "C", "中记忆C", importance: 0.5f, strength: 0.5f);
        mem.Remember(NpcMemoryKind.Fact, "D", "新记忆D", importance: 0.5f, strength: 0.5f);
        AssertTrue(mem.Entries.Count <= 3, "记忆库应淘汰最弱条目维持容量。");
    }

    // ── 关系 ────────────────────────────────────────────────────────

    public static void TestRelationshipBidirectionalChange()
    {
        var r = new NpcRelationship(0f);
        r.Change(30f);
        AssertTrue(r.IsFriendly, "关系 >25 应为友好。");
        r.Change(-70f);
        AssertTrue(r.IsHostile, "关系 <-25 应为敌意。");
        AssertTrue(r.Value == -40f, "关系值应累积变化。");
        AssertTrue(r.Value >= -100f, "关系值下限 -100。");
    }

    // ── 日程 ────────────────────────────────────────────────────────

    public static void TestScheduleActivityByHour()
    {
        var sched = NpcSchedule.Default();
        AssertTrue(sched.GetActivity(2f) == NpcActivityKind.Sleep, "凌晨2点应为睡眠。");
        AssertTrue(sched.GetActivity(10f) == NpcActivityKind.Work, "上午10点应为工作。");
        AssertTrue(sched.GetActivity(20f) == NpcActivityKind.Social, "晚8点应为社交。");
        AssertTrue(sched.GetActivity(13.5f) == NpcActivityKind.Work, "下午1点半应为工作。");
    }

    // ── NPC 实体 ────────────────────────────────────────────────────

    public static void TestNpcCharacterTickDecaysMentality()
    {
        var npc = new NpcCharacter { Name = "村民甲" };
        npc.Emotion.Trigger(NpcEmotionType.Joy, 0.9f);
        npc.Memory.Remember(NpcMemoryKind.Event, "庆典", "参加丰收庆典", importance: 0.5f);
        float joyBefore = npc.Emotion.Get(NpcEmotionType.Joy);
        int memBefore = npc.Memory.Entries.Count;

        for (int i = 0; i < 300; i++) npc.Tick(0.1f);

        AssertTrue(npc.Emotion.Get(NpcEmotionType.Joy) < joyBefore, "NPC Tick 应衰减情感。");
        AssertTrue(npc.Memory.Entries.Count <= memBefore, "NPC Tick 应衰减/遗忘记忆。");
    }

    public static void TestNpcInteractUpdatesRelationships()
    {
        var a = new NpcCharacter { Name = "NPC_A" };
        var b = new NpcCharacter { Name = "NPC_B" };
        a.Interact(b, delta: 20f);

        AssertTrue(a.GetRelationship("NPC_B").Value > 0f, "互动应提升对对方的关系。");
        AssertTrue(b.GetRelationship("NPC_A").Value > 0f, "互动应双向提升关系。");
        AssertTrue(a.Memory.Entries.Any(e => e.Subject == "NPC_B"), "互动应记录遭遇记忆。");
    }

    // ── LLM 驱动对话 ────────────────────────────────────────────────

    public static void TestNpcDialogueSystemPromptInjectsMentality()
    {
        var npc = new NpcCharacter
        {
            Name = "铁匠罗恩",
            Occupation = "铁匠",
            Faction = "铁炉堡",
            Personality = new NpcPersonality(0.7f, 0.6f, 0.8f, 0.5f, 0.3f)
        };
        npc.Emotion.Trigger(NpcEmotionType.Joy, 0.6f);
        npc.GetRelationship("玩家").Change(40f);
        npc.Memory.Remember(NpcMemoryKind.Dialogue, "玩家", "玩家上次询问了武器锻造", importance: 0.8f);

        var prompt = npc.Dialogue.BuildSystemPrompt(npc, "玩家");

        AssertTrue(prompt.Contains("铁匠罗恩"), "系统提示应含 NPC 名字。");
        AssertTrue(prompt.Contains("铁匠"), "系统提示应含职业。");
        AssertTrue(prompt.Contains("愉悦"), "系统提示应注入情感。");
        AssertTrue(prompt.Contains("40"), "系统提示应注入关系值。");
        AssertTrue(prompt.Contains("武器锻造"), "系统提示应注入记忆。");
    }

    public static void TestNpcDialogueLLMRoundtrip()
    {
        var mock = new MockLLMProvider { Reply = "（铁匠罗恩）想打把好剑？包在我身上。" };
        var client = new LLMClient(new LLMConfig(LLMPlatform.OpenAI, "", "test-model")
        {
            RateLimitPerMinute = 1000
        });
        client.Register(mock);

        var npc = new NpcCharacter { Name = "铁匠罗恩", Occupation = "铁匠" };
        var resp = npc.Dialogue.RespondAsync(npc, "玩家", "你能帮我锻造一把剑吗？", client)
            .GetAwaiter().GetResult();

        AssertTrue(resp.Success, "LLM 对话应成功。");
        AssertTrue(resp.Content.Contains("铁匠罗恩"), "LLM 回复应被记录。");
        AssertTrue(mock.LastConversation != null, "应把对话发给 LLM。");

        // 系统提示应存在且含注入
        var sys = mock.LastConversation!.Messages.FirstOrDefault(m => m.Role == LLMRole.System);
        AssertTrue(sys != null && sys.Content.Contains("铁匠罗恩"), "应含系统角色卡。");

        // 用户消息应被追加
        AssertTrue(mock.LastConversation.Messages.Any(m => m.Role == LLMRole.User),
            "玩家输入应作为 User 消息。");

        // 历史应记录回复
        AssertTrue(npc.Dialogue.History.Any(m => m.Role == LLMRole.Assistant),
            "对话历史应记录 NPC 回复。");
    }

    // ── NPC 世界 ────────────────────────────────────────────────────

    public static void TestNpcWorldTicksAndFinds()
    {
        var world = new NpcWorld();
        var npc = world.Register(new NpcCharacter { Name = "村长" });
        AssertTrue(world.Find("村长") == npc, "世界应按名字查找 NPC。");

        float t0 = world.TimeHours;
        world.Tick(3600f, timeScale: 1f); // 1 游戏小时
        AssertTrue(world.TimeHours > t0, "世界 Tick 应推进时间。");
        AssertTrue(world.Characters.Count == 1, "世界应管理注册的 NPC。");
    }
}
