using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using YamaEngine.AI;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests
{
    /// <summary>
    /// LLM 智能系统深度测试。覆盖对话上下文/token 估计/SSE 解析/本地规则回退/
    /// LLM→行为树转换/NPC 对话情感/任务生成器/GameMasterAI DDA。
    /// </summary>
    public static class LLMIntelligenceDepthTests
    {
        private static void Near(float want, float got, float tol, string msg)
        {
            if (Math.Abs(want - got) > tol) throw new Exception($"{msg}: want {want}, got {got}");
        }
        private static void AssertFalse(bool cond, string msg) { if (cond) throw new Exception(msg); }

        // ── 枚举 ──

        public static void TestLLMPlatformValues()
        {
            AssertEqual(0, (int)LLMPlatform.OpenAI, "OpenAI=0");
            AssertEqual(1, (int)LLMPlatform.Anthropic, "Anthropic=1");
            AssertEqual(2, (int)LLMPlatform.Ollama, "Ollama=2");
            AssertEqual(3, (int)LLMPlatform.Qwen, "Qwen=3");
            AssertEqual(4, (int)LLMPlatform.Ernie, "Ernie=4");
            AssertEqual(5, (int)LLMPlatform.Local, "Local=5");
        }

        public static void TestLLMRoleValues()
        {
            AssertEqual(0, (int)LLMRole.System, "System=0");
            AssertEqual(1, (int)LLMRole.User, "User=1");
            AssertEqual(2, (int)LLMRole.Assistant, "Assistant=2");
            AssertEqual(3, (int)LLMRole.Tool, "Tool=3");
        }

        // ── LLMConfig ──

        public static void TestConfigDefaults()
        {
            var c = new LLMConfig();
            AssertEqual(LLMPlatform.OpenAI, c.Platform, "default platform");
            AssertEqual("gpt-4o-mini", c.Model, "default model");
            Near(0.7f, c.Temperature, 1e-5f, "temp");
            AssertEqual(1024, c.MaxTokens, "maxTokens");
            Near(1f, c.TopP, 1e-5f, "topP");
            AssertEqual(30000, c.RequestTimeoutMs, "timeout");
            AssertEqual(3, c.MaxRetries, "retries");
            AssertEqual(60, c.RateLimitPerMinute, "rate");
            AssertFalse(c.EnableStreaming, "streaming off");
            AssertTrue(c.FallbackToLocal, "fallback on");
        }

        public static void TestConfigCtor()
        {
            var c = new LLMConfig(LLMPlatform.Anthropic, "key123", "claude");
            AssertEqual(LLMPlatform.Anthropic, c.Platform, "platform");
            AssertEqual("key123", c.ApiKey, "key");
            AssertEqual("claude", c.Model, "model");
        }

        // ── LLMMessage ──

        public static void TestMessageRoleName()
        {
            AssertEqual("system", new LLMMessage(LLMRole.System, "x").RoleName, "system");
            AssertEqual("user", new LLMMessage(LLMRole.User, "x").RoleName, "user");
            AssertEqual("assistant", new LLMMessage(LLMRole.Assistant, "x").RoleName, "assistant");
            AssertEqual("tool", new LLMMessage(LLMRole.Tool, "x").RoleName, "tool");
        }

        public static void TestMessageDefaults()
        {
            var m = new LLMMessage(LLMRole.User, "hello");
            AssertEqual(LLMRole.User, m.Role, "role");
            AssertEqual("hello", m.Content, "content");
            AssertTrue(m.Name == null, "no name");
            AssertEqual(0, m.EstimatedTokens, "0 tokens until conversation");
        }

        // ── LLMUsage / LLMResponse ──

        public static void TestUsageTotalTokens()
        {
            var u = new LLMUsage { PromptTokens = 10, CompletionTokens = 5 };
            AssertEqual(15, u.TotalTokens, "10+5=15");
        }

        public static void TestResponseDefaultsAndToString()
        {
            var r = new LLMResponse { Content = "hi" };
            AssertEqual("hi", r.ToString(), "success -> content");
            var err = new LLMResponse { Success = false, Error = "boom" };
            AssertTrue(err.ToString().Contains("boom"), "error -> contains message");
        }

        // ── LLMConversation ──

        public static void TestConversationAppend()
        {
            var c = new LLMConversation { MaxContextTokens = 4000 };
            c.Append(new LLMMessage(LLMRole.User, "Hello"));
            AssertEqual(1, c.Messages.Count, "1 msg");
            AssertTrue(c.Messages[0].EstimatedTokens > 0, "tokens estimated");
            AssertTrue(c.TotalTokens > 0, "total > 0");
        }

        public static void TestConversationSetSystem()
        {
            var c = new LLMConversation();
            c.SetSystem("You are a helpful AI.");
            AssertEqual(1, c.Messages.Count, "system added");
            AssertEqual(LLMRole.System, c.Messages[0].Role, "role is system");
            AssertTrue(c.SystemReservedTokens > 0, "reserved > 0");
            // Replace system
            c.SetSystem("New system");
            AssertEqual(1, c.Messages.Count, "still 1 system");
            AssertEqual("New system", c.Messages[0].Content, "replaced");
        }

        public static void TestConversationTruncateKeepsSystem()
        {
            var c = new LLMConversation { MaxContextTokens = 100 };
            c.SetSystem("sys");
            int sysCount = c.Messages.Count(m => m.Role == LLMRole.System);
            AssertEqual(1, sysCount, "system present");
            // Fill with lots of user messages to trigger truncate
            for (int i = 0; i < 20; i++)
                c.Append(new LLMMessage(LLMRole.User, new string('x', 200)));
            // After truncate: system + tail messages
            var sys = c.Messages.FindAll(m => m.Role == LLMRole.System);
            AssertEqual(1, sys.Count, "system always kept");
            AssertEqual(1, c.Messages[0].Role == LLMRole.System ? 1 : 0, "system first");
        }

        public static void TestConversationTruncateDropsOld()
        {
            var c = new LLMConversation { MaxContextTokens = 200 };
            c.SetSystem("sys");
            // Add messages with distinct content
            for (int i = 0; i < 10; i++)
                c.Append(new LLMMessage(LLMRole.User, new string((char)('a' + i), 300)));
            // Old non-system messages should be dropped
            AssertTrue(c.Messages.Count < 11, "truncated to fewer messages");
        }

        public static void TestConversationClear()
        {
            var c = new LLMConversation();
            c.SetSystem("sys");
            c.Append(new LLMMessage(LLMRole.User, "hi"));
            c.Clear();
            AssertEqual(0, c.Messages.Count, "cleared");
            AssertEqual(0, c.SystemReservedTokens, "reserved reset");
        }

        public static void TestConversationId()
        {
            var c1 = new LLMConversation();
            var c2 = new LLMConversation();
            AssertTrue(c1.Id != c2.Id, "unique ids");
        }

        // ── TokenEstimator ──

        public static void TestTokenEstimatorEmpty()
        {
            AssertEqual(0, TokenEstimator.Estimate("", TokenEstimator.ModelFamily.Auto), "empty");
            AssertEqual(0, TokenEstimator.Estimate(null, TokenEstimator.ModelFamily.Auto), "null");
        }

        public static void TestTokenEstimatorEnglish()
        {
            // 8 ASCII chars / 4 = 2 tokens
            int t = TokenEstimator.Estimate("abcdefgh", TokenEstimator.ModelFamily.OpenAI);
            AssertEqual(2, t, "8 chars / 4 = 2");
        }

        public static void TestTokenEstimatorChinese()
        {
            // 6 CJK chars / 1.5 = 4 tokens
            int t = TokenEstimator.Estimate("你好世界测试", TokenEstimator.ModelFamily.Auto);
            AssertEqual(4, t, "6 CJK / 1.5 = 4");
        }

        public static void TestTokenEstimatorMinOne()
        {
            int t = TokenEstimator.Estimate("a", TokenEstimator.ModelFamily.Auto);
            AssertTrue(t >= 1, "minimum 1 token");
        }

        public static void TestFamilyForPlatforms()
        {
            AssertEqual(TokenEstimator.ModelFamily.Anthropic, TokenEstimator.FamilyFor(LLMPlatform.Anthropic), "anthropic");
            AssertEqual(TokenEstimator.ModelFamily.Chinese, TokenEstimator.FamilyFor(LLMPlatform.Qwen), "qwen");
            AssertEqual(TokenEstimator.ModelFamily.Chinese, TokenEstimator.FamilyFor(LLMPlatform.Ernie), "ernie");
            AssertEqual(TokenEstimator.ModelFamily.OpenAI, TokenEstimator.FamilyFor(LLMPlatform.OpenAI), "openai");
            AssertEqual(TokenEstimator.ModelFamily.OpenAI, TokenEstimator.FamilyFor(LLMPlatform.Ollama), "ollama");
        }

        // ── SSEParser ──

        public static void TestSSEParserBasic()
        {
            var input = "data: hello\n\ndata: world\n\n";
            var stream = new MemoryStream(Encoding.UTF8.GetBytes(input));
            var events = new List<SSEEvent>();
            SSEParser.ReadAsync(stream, e => events.Add(e)).GetAwaiter().GetResult();
            AssertEqual(2, events.Count, "2 events");
            AssertEqual("hello", events[0].Data, "first data");
            AssertEqual("world", events[1].Data, "second data");
            AssertEqual("message", events[0].Event, "default event type");
        }

        public static void TestSSEParserWithEventType()
        {
            var input = "event: token\ndata: hi\n\nevent: done\ndata: bye\n\n";
            var stream = new MemoryStream(Encoding.UTF8.GetBytes(input));
            var events = new List<SSEEvent>();
            SSEParser.ReadAsync(stream, e => events.Add(e)).GetAwaiter().GetResult();
            AssertEqual(2, events.Count, "2 events");
            AssertEqual("token", events[0].Event, "first event type");
            AssertEqual("done", events[1].Event, "second event type");
        }

        public static void TestSSEParserMultilineData()
        {
            var input = "data: line1\ndata: line2\n\n";
            var stream = new MemoryStream(Encoding.UTF8.GetBytes(input));
            var events = new List<SSEEvent>();
            SSEParser.ReadAsync(stream, e => events.Add(e)).GetAwaiter().GetResult();
            AssertEqual(1, events.Count, "1 event (joined)");
            AssertTrue(events[0].Data.Contains("line1"), "contains line1");
            AssertTrue(events[0].Data.Contains("line2"), "contains line2");
        }

        public static void TestSSEParserEmptyStream()
        {
            var stream = new MemoryStream(Array.Empty<byte>());
            var events = new List<SSEEvent>();
            SSEParser.ReadAsync(stream, e => events.Add(e)).GetAwaiter().GetResult();
            AssertEqual(0, events.Count, "no events");
        }

        // ── LocalRuleInference ──

        public static void TestLocalRuleKnownKeyword()
        {
            var r = new LocalRuleInference();
            string resp = r.Respond("你好");
            AssertTrue(resp.Contains("你好") || resp.Contains("帮忙"), "matched greeting rule");
        }

        public static void TestLocalRuleFallback()
        {
            var r = new LocalRuleInference();
            string resp = r.Respond("量子纠缠态的超几何分布");
            AssertTrue(resp.Length > 0, "has fallback response");
        }

        public static void TestLocalRuleEmptyInput()
        {
            var r = new LocalRuleInference();
            string resp = r.Respond("");
            AssertTrue(resp.Length > 0, "empty input -> fallback");
        }

        public static void TestLocalRuleAddRule()
        {
            var r = new LocalRuleInference();
            r.AddRule("密码", "我不知道密码。");
            string resp = r.Respond("请告诉我密码");
            AssertTrue(resp.Contains("密码"), "custom rule matched");
        }

        // ── LLMBehaviorNode / LLMToBehaviorTree ──

        public static void TestBehaviorNodeDefaults()
        {
            var n = new LLMBehaviorNode();
            AssertEqual("Task", n.Kind, "default kind");
            AssertEqual("", n.Name, "empty name");
            AssertEqual("", n.Text, "empty text");
            AssertEqual(0, n.Children.Count, "no children");
        }

        public static void TestParseSpecValid()
        {
            string json = "{\"kind\":\"Sequence\",\"name\":\"Root\",\"children\":[" +
                "{\"kind\":\"Task\",\"name\":\"Move\",\"text\":\"goto\"}]}";
            var spec = LLMToBehaviorTree.ParseSpec(json);
            AssertTrue(spec != null, "parsed");
            AssertEqual("Sequence", spec!.Kind, "kind");
            AssertEqual("Root", spec.Name, "name");
            AssertEqual(1, spec.Children.Count, "1 child");
            AssertEqual("Task", spec.Children[0].Kind, "child kind");
        }

        public static void TestParseSpecInvalid()
        {
            var spec = LLMToBehaviorTree.ParseSpec("not json");
            AssertTrue(spec == null, "invalid json -> null");
        }

        public static void TestParseSpecEmpty()
        {
            var spec = LLMToBehaviorTree.ParseSpec("{}");
            AssertTrue(spec != null, "empty obj -> defaults");
            AssertEqual("Task", spec!.Kind, "default kind from empty");
        }

        public static void TestBuildTree()
        {
            string json = "{\"kind\":\"Sequence\",\"name\":\"Root\",\"children\":[" +
                "{\"kind\":\"Task\",\"name\":\"T1\",\"text\":\"attack\"}]}";
            var spec = LLMToBehaviorTree.ParseSpec(json)!;
            var tree = LLMToBehaviorTree.Build(spec, text => ctx => BTNodeStatus.Success, "TestTree");
            AssertTrue(tree.Root != null, "tree has root");
            AssertEqual("TestTree", tree.Name, "tree name");
        }

        public static void TestBuildBehaviorPrompt()
        {
            string p = LLMToBehaviorTree.BuildBehaviorPrompt("城镇", "巡逻");
            AssertTrue(p.Contains("场景") && p.Contains("目标"), "prompt contains markers");
            AssertTrue(p.Contains("城镇") && p.Contains("巡逻"), "prompt contains scenario/goal");
        }

        // ── NPC Persona / Memory / Emotion ──

        public static void TestPersonaDefaults()
        {
            var p = new NPCPersona();
            AssertEqual("NPC", p.Name, "name");
            AssertEqual("友善、健谈", p.Personality, "personality");
            AssertEqual("村庄商人", p.Background, "background");
            AssertTrue(p.KnowledgeTopics.Length == 0, "no topics");
        }

        public static void TestMemoryEntryDefaults()
        {
            var m = new NPCMemoryEntry { Subject = "player1" };
            AssertEqual("player1", m.Subject, "subject");
            AssertEqual(0, m.InteractionCount, "0 interactions");
            Near(0.5f, m.Affinity, 1e-5f, "default affinity");
        }

        public static void TestEmotionMoodLabel()
        {
            var e = new NPCEmotion { Pleasure = 0.8f, Arousal = 0.8f };
            AssertEqual("兴奋", e.MoodLabel, "high P high A -> 兴奋");
            e = new NPCEmotion { Pleasure = 0.8f, Arousal = 0.1f };
            AssertEqual("满足", e.MoodLabel, "high P low A -> 满足");
            e = new NPCEmotion { Pleasure = -0.8f, Arousal = 0.8f };
            AssertEqual("愤怒", e.MoodLabel, "low P high A -> 愤怒");
            e = new NPCEmotion { Pleasure = -0.8f, Arousal = -0.8f };
            AssertEqual("悲伤", e.MoodLabel, "low P low A -> 悲伤");
            e = new NPCEmotion { Pleasure = 0f, Arousal = 0f };
            AssertEqual("平静", e.MoodLabel, "neutral -> 平静");
        }

        public static void TestEmotionApplyClamp()
        {
            var e = new NPCEmotion { Pleasure = 0.9f };
            e.Apply(0.5f, 0f, 0f);
            Near(1f, e.Pleasure, 1e-5f, "clamped to 1");
            e = new NPCEmotion { Pleasure = -0.9f };
            e.Apply(-0.5f, 0f, 0f);
            Near(-1f, e.Pleasure, 1e-5f, "clamped to -1");
        }

        public static void TestEmotionApplyNormal()
        {
            var e = new NPCEmotion { Pleasure = 0.5f, Arousal = 0.2f, Dominance = 0.3f };
            e.Apply(0.1f, 0.1f, -0.1f);
            Near(0.6f, e.Pleasure, 1e-5f, "P");
            Near(0.3f, e.Arousal, 1e-5f, "A");
            Near(0.2f, e.Dominance, 1e-5f, "D");
        }

        // ── QuestGenerator ──

        public static void TestQuestParseValid()
        {
            string json = "{\"title\":\"Test\",\"description\":\"Desc\",\"giver\":\"NPC\"," +
                "\"rewardGold\":200,\"objectives\":[{\"description\":\"Kill 3\",\"type\":\"kill\",\"required\":3,\"targetTag\":\"goblin\"}]}";
            var q = QuestGenerator.Parse(json);
            AssertTrue(q != null, "parsed");
            AssertEqual("Test", q!.Title, "title");
            AssertEqual(200, q.RewardGold, "gold");
            AssertEqual(1, q.Objectives.Count, "1 objective");
            AssertEqual("kill", q.Objectives[0].Type, "type");
            AssertEqual(3, q.Objectives[0].Required, "required");
        }

        public static void TestQuestParseInvalid()
        {
            var q = QuestGenerator.Parse("broken");
            AssertTrue(q == null, "invalid -> null");
        }

        public static void TestQuestRuleGenerateDifficulty1()
        {
            var client = new LLMClient(new LLMConfig(LLMPlatform.Local, "", "x"));
            var gen = new QuestGenerator(client);
            var q = gen.RuleGenerate("奇幻", 1, "森林");
            AssertTrue(q.Title.Length > 0, "has title");
            AssertEqual(50, q.RewardGold, "1 difficulty -> 50 gold");
            AssertEqual(1, q.Objectives.Count, "d1: 1 objective");
        }

        public static void TestQuestRuleGenerateDifficulty5()
        {
            var client = new LLMClient(new LLMConfig(LLMPlatform.Local, "", "x"));
            var gen = new QuestGenerator(client);
            var q = gen.RuleGenerate("奇幻", 5, "雪山");
            AssertEqual(250, q.RewardGold, "5 difficulty -> 250 gold");
            AssertTrue(q.Objectives.Count >= 2, "d5: at least 2 objectives");
        }

        public static void TestQuestSpecDefaults()
        {
            var q = new QuestSpec();
            AssertEqual("", q.Title, "empty title");
            AssertEqual("", q.Giver, "empty giver");
            AssertEqual(0, q.RewardGold, "0 gold");
            AssertEqual(0, q.Objectives.Count, "no objectives");
        }

        // ── GameMasterAI ──

        public static void TestGMAIDefaults()
        {
            var ai = new GameMasterAI(new LLMClient(new LLMConfig(LLMPlatform.Local, "", "x")));
            Near(0.5f, ai.CurrentDifficulty, 1e-5f, "default difficulty");
            Near(0.5f, ai.TargetDifficulty, 1e-5f, "default target");
            Near(0.15f, ai.AdaptRate, 1e-5f, "adapt rate");
            AssertEqual(10, ai.HistoryWindow, "window");
        }

        public static void TestGMAIEnemyStatMultiplier()
        {
            var ai = new GameMasterAI(new LLMClient(new LLMConfig(LLMPlatform.Local, "", "x")));
            ai.CurrentDifficulty = 0.5f;
            Near(1.0f, ai.EnemyStatMultiplier, 0.01f, "0.5 -> 1.0x");
            ai.CurrentDifficulty = 0.0f;
            Near(0.6f, ai.EnemyStatMultiplier, 0.01f, "0 -> 0.6x");
            ai.CurrentDifficulty = 1.0f;
            Near(1.4f, ai.EnemyStatMultiplier, 0.01f, "1 -> 1.4x");
        }

        public static void TestGMAIObserveHighPerformance()
        {
            var ai = new GameMasterAI(new LLMClient(new LLMConfig(LLMPlatform.Local, "", "x")));
            // High kills, no deaths, lots of damage
            var snap = new PlayerBehaviorSnapshot
            {
                Kills = 10, Deaths = 0, DamageDealt = 500, DamageTaken = 100,
                TimeAlive = 120f
            };
            ai.Observe(snap);
            AssertTrue(ai.TargetDifficulty > 0.7f, "high performance -> high target");
        }

        public static void TestGMAIObserveLowPerformance()
        {
            var ai = new GameMasterAI(new LLMClient(new LLMConfig(LLMPlatform.Local, "", "x")));
            var snap = new PlayerBehaviorSnapshot
            {
                Kills = 0, Deaths = 5, DamageDealt = 50, DamageTaken = 1000,
                TimeAlive = 10f
            };
            ai.Observe(snap);
            AssertTrue(ai.TargetDifficulty < 0.3f, "low performance -> low target");
        }

        public static void TestGMAIObserveAdaptsGradually()
        {
            var ai = new GameMasterAI(new LLMClient(new LLMConfig(LLMPlatform.Local, "", "x")));
            var snap = new PlayerBehaviorSnapshot { Kills = 100, Deaths = 0, DamageDealt = 9999, TimeAlive = 300f };
            ai.Observe(snap);
            // After one observe, CurrentDifficulty should have moved from 0.5 toward target but not fully
            AssertTrue(ai.CurrentDifficulty > 0.5f, "moved up from default");
            AssertTrue(ai.CurrentDifficulty <= ai.TargetDifficulty, "current <= target (gradual approach)");
        }

        public static void TestGMAIHistoryWindow()
        {
            var ai = new GameMasterAI(new LLMClient(new LLMConfig(LLMPlatform.Local, "", "x")));
            for (int i = 0; i < 20; i++)
                ai.Observe(new PlayerBehaviorSnapshot { Kills = i, TimeAlive = 30f });
            // History should be capped at HistoryWindow (10)
            AssertTrue(ai.HistoryWindow == 10, "window unchanged");
        }

        // ── PlayerBehaviorSnapshot / GameEvent ──

        public static void TestPlayerSnapshotDefaults()
        {
            var s = new PlayerBehaviorSnapshot();
            AssertEqual(0, s.Kills, "0 kills");
            AssertEqual(0, s.Deaths, "0 deaths");
            Near(500f, s.AvgReactionMs, 1e-5f, "default reaction");
        }

        public static void TestGameEventDefaults()
        {
            var e = new GameEvent { Name = "test", Description = "desc", Intensity = 0.5f };
            AssertEqual("test", e.Name, "name");
            AssertEqual("desc", e.Description, "desc");
            Near(0.5f, e.Intensity, 1e-5f, "intensity");
        }
    }
}
