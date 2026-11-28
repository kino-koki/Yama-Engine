using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using YamaEngine.Assets;
using YamaEngine.Animation;
using YamaEngine.NPC;
using YamaEngine.NPC.AiNpc;
using static YamaEngine.Tests.TestHelpers;
using Skeleton = YamaEngine.Animation.Skeleton;

namespace YamaEngine.Tests
{
    /// <summary>AI NPC 正式接入测试：Kimodo 动作协议/转换 + MOSS-Live 全双工 + 融合状态机。</summary>
    public static class AiNpcIntegrationTests
    {
        public static void TestConfigRoundtrip(string tempRoot)
        {
            var path = Path.Combine(tempRoot, "ai_npc.json");
            var config = AiNpcConfig.CreateDefault();
            config.Kimodo.Endpoint = "http://127.0.0.1:9999";
            config.MossLive.Endpoint = "ws://127.0.0.1:9998/v1/duplex";
            config.LipSync.JawBone = "Jaw";
            config.Save(path);

            var loaded = AiNpcConfig.Load(path);
            AssertEqual("http://127.0.0.1:9999", loaded.Kimodo.Endpoint, "Kimodo endpoint should roundtrip.");
            AssertEqual("ws://127.0.0.1:9998/v1/duplex", loaded.MossLive.Endpoint, "Moss endpoint should roundtrip.");
            AssertEqual("Jaw", loaded.LipSync.JawBone, "LipSync jaw bone should roundtrip.");
        }

        public static void TestKimodoConverterProducesPlayableClip()
        {
            var config = AiNpcConfig.CreateDefault().Kimodo;
            config.UseRootMotion = true;
            config.Fps = 30f;
            var converter = new KimodoMotionConverter(config);

            var motion = BuildSyntheticMotion();
            var converted = converter.Convert(motion, targetSkeleton: null, clipName: "wave");

            AssertEqual(3, converted.FrameCount, "Converter should keep frame count.");
            // 3 帧在 30fps 下的有效时长 = (3-1)/30 ≈ 0.0667s（末帧为最后一个关键帧）。
            AssertTrue(Math.Abs(converted.DurationSeconds - 2f / 30f) < 0.01f,
                $"Clip duration should be (frames-1)/fps, got {converted.DurationSeconds}");

            var pose = converted.Clip.Sample(converted.Skeleton, 1f / 30f);
            int hips = converted.Skeleton.FindBone("Hips");
            AssertTrue(hips >= 0, "Skeleton should contain Hips.");

            var offset = pose.GetTranslationOffset(hips);
            // 第 1 帧根位置 x=0.2，静息位置 x=0 → 位移 0.2。
            AssertTrue(Math.Abs(offset.X - 0.2f) < 1e-3f, $"Hips root offset should be 0.2, got {offset.X}");

            int hand = converted.Skeleton.FindBone("LeftHand");
            AssertTrue(hand >= 0, "Skeleton should contain LeftHand.");
            var handRotation = pose.GetRotation(hand);
            // 第 1 帧绕 Y 轴 90°：|y| ≈ 0.707。
            AssertTrue(Math.Abs(Math.Abs(handRotation.Y) - 0.7071f) < 0.01f,
                $"LeftHand should rotate 90° on Y, got {handRotation}");
        }

        public static void TestKimodoConverterRetargetsExistingSkeleton()
        {
            var config = AiNpcConfig.CreateDefault().Kimodo;
            config.UseRootMotion = false;
            var converter = new KimodoMotionConverter(config);

            var target = Skeleton.CreateBiped("Target");
            var converted = converter.Convert(BuildSyntheticMotion(), target, "retarget");

            AssertTrue(ReferenceEquals(target, converted.Skeleton), "Retarget should reuse the supplied skeleton.");
            AssertTrue(converted.BoneMapping.Count >= 2, "At least Hips/LeftHand should map.");
        }

        /// <summary>Unity5 深度：足部接触提取按帧推进，无数据时回退为全程触地。</summary>
        public static void TestFootContactExtractionByFrame()
        {
            var motion = BuildSyntheticMotion();
            motion.FootContacts = new[]
            {
                new[] { 1f, 1f, 1f, 1f },
                new[] { 0f, 0f, 1f, 1f },
                new[] { 0f, 0f, 0f, 0f }
            };
            var converted = BuildConverted(motion);

            var atStart = AiNpcMotionPostProcessor.ExtractFootContacts(converted, 0f, 30f);
            AssertTrue(atStart.Left > 0.99f && atStart.Right > 0.99f, "Frame 0 both feet grounded.");

            var airborne = AiNpcMotionPostProcessor.ExtractFootContacts(converted, 1f / 30f, 30f);
            AssertTrue(airborne.Left < 0.01f && airborne.Right > 0.99f, "Frame 1 left foot airborne, right grounded.");

            var none = AiNpcMotionPostProcessor.ExtractFootContacts(converted, 2f / 30f, 30f);
            AssertTrue(none.Left < 0.01f && none.Right < 0.01f, "Frame 2 both feet airborne.");

            // 无接触数据 → 回退为触地。
            var fallback = AiNpcMotionPostProcessor.ExtractFootContacts(null, 0f, 30f);
            AssertTrue(fallback.Left > 0.99f && fallback.Right > 0.99f, "Null motion should default to grounded.");
        }

        /// <summary>
        /// Unity5 深度：姿态后处理必须能安全跑完（足部 IK + 手势 + 注视），
        /// 不抛异常、不产生 NaN，并且注视目标超 FOV 时被衰减。
        /// </summary>
        public static void TestMotionPostProcessorPipelineIsStable()
        {
            var config = AiNpcConfig.CreateDefault().Rig;
            config.EnableFootIk = true;
            config.EnableLookAt = true;
            config.GestureLayerWeight = 1f;

            var skeleton = Skeleton.CreateBiped("Rig");
            var clip = new AnimationClip("idle", 1f, looping: true);
            clip.AddRotationKey("Hips", 0f, Quaternion.Identity);
            clip.AddRotationKey("Hips", 1f, Quaternion.Identity);
            clip.AddTranslationKey("Hips", 0f, Vector3.UnitY);
            clip.AddTranslationKey("Hips", 1f, Vector3.UnitY);

            var owner = new SceneObject { Position = new Vector3(0, 0, 0), Rotation = Quaternion.Identity };
            var runtime = new AnimationRuntime();
            runtime.PlayAnimation(owner, skeleton, clip, "rig-test");
            var instance = runtime.GetInstance(owner);
            AssertTrue(instance != null, "Instance should exist after PlayAnimation.");

            var post = new AiNpcMotionPostProcessor(config, instance!.Skeleton);

            // 正前方注视目标（应在 FOV 内，权重不衰减为 0）。
            for (int i = 0; i < 20; i++)
            {
                post.Process(instance, new Vector3(0, 1.5f, -1.5f), 1f, 1f, 1f, 1f / 60f);
                runtime.Update(1f / 60f);
            }
            AssertTrue(HasNoNaN(instance), "Pose should stay finite with front look-at.");

            // 背后方注视目标（超 FOV，应被衰减但不产生 NaN）。
            for (int i = 0; i < 10; i++)
            {
                post.Process(instance, new Vector3(0, 1.5f, 1.5f), 1f, 1f, 1f, 1f / 60f);
                runtime.Update(1f / 60f);
            }
            AssertTrue(HasNoNaN(instance), "Pose should stay finite with behind look-at.");

            runtime.StopAnimation(owner);
        }

        private static bool HasNoNaN(AnimationInstance instance)
        {
            var pose = instance.Animator.CurrentPose;
            if (pose == null) return true;
            for (int i = 0; i < instance.Skeleton.Bones.Count; i++)
            {
                var r = pose.GetRotation(i);
                var t = pose.GetTranslationOffset(i);
                if (!IsFinite(r) || !IsFinite(t)) return false;
            }
            return true;
        }

        private static bool IsFinite(Quaternion q) =>
            float.IsFinite(q.X) && float.IsFinite(q.Y) && float.IsFinite(q.Z) && float.IsFinite(q.W);

        private static bool IsFinite(Vector3 v) =>
            float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

        public static void TestKimodoHttpClientRoundtrip()
        {
            var handler = new StubHttpHandler(JsonSerializer.Serialize(BuildSyntheticMotion(), KimodoTestJson.Options));
            using var http = new HttpClient(handler);
            var config = AiNpcConfig.CreateDefault().Kimodo;
            config.Endpoint = "http://kimodo.test";
            using var client = new KimodoMotionClient(config, http);

            var response = client.GenerateAsync("NPC wave", 2f).GetAwaiter().GetResult();

            AssertEqual(3, response.FrameCount, "HTTP client should parse frames.");
            AssertTrue(handler.LastRequestUri!.ToString().EndsWith("/v1/motion/generate"),
                $"Unexpected request uri: {handler.LastRequestUri}");
            AssertTrue(handler.LastRequestBody.Contains("NPC wave"), "Request body should carry the prompt.");
            AssertTrue(handler.LastRequestBody.Contains("\"constraints\""), "Request body should carry constraints array.");
        }

        public static void TestMossLiveDuplexTransport()
        {
            var transport = new InMemoryMossTransport();
            var config = AiNpcConfig.CreateDefault().MossLive;
            config.Endpoint = "ws://moss.test/v1/duplex";
            var client = new MossLiveClient(config, transport);

            var received = new List<MossServerEvent>();
            client.EventReceived += evt => received.Add(evt);

            client.StartAsync(new MossSessionInfo { NpcId = "npc-1", Name = "阿岚" }).GetAwaiter().GetResult();
            AssertTrue(client.IsConnected, "Moss client should connect through the in-memory transport.");
            AssertTrue(transport.Sent.Any(s => s.Contains("\"type\":\"session_start\"")), "session_start must be sent.");

            transport.Push("{\"type\":\"ready\"}");
            transport.Push("{\"type\":\"text_done\",\"text\":\"你好，旅人。\"}");
            transport.Push("{\"type\":\"viseme\",\"viseme\":\"AA\",\"weight\":0.8,\"duration_ms\":50}");

            WaitUntil(() => received.Count >= 3, 2000);
            AssertEqual(3, received.Count, "Three MOSS events should arrive.");
            AssertEqual("text_done", received[1].Type, "Second event should be text_done.");
            AssertEqual("你好，旅人。", received[1].Text, "Text payload should decode.");
            AssertTrue(client.TryDequeueEvent(out var first) && first.Type == "ready", "Event queue should expose ready.");

            client.SendAudioAsync(new byte[] { 1, 2, 3 }).GetAwaiter().GetResult();
            AssertTrue(transport.Sent.Any(s => s.Contains("AQID")), "PCM16 should be base64 encoded.");
            AssertTrue(transport.Sent.Any(s => s.Contains("\"type\":\"audio_in\"")), "audio_in must be sent.");

            client.StopAsync().GetAwaiter().GetResult();
        }

        public static void TestAiNpcActionFusionAndBargeIn()
        {
            var config = AiNpcConfig.CreateDefault();
            config.MossLive.Enabled = true;
            config.MossLive.Endpoint = "ws://moss.test/v1/duplex";

            var transport = new InMemoryMossTransport();
            var moss = new MossLiveClient(config.MossLive, transport);
            var motionSource = new FakeKimodoSource(BuildSyntheticMotion());
            var agent = new AiNpcAgent(config, new NpcCharacter { Name = "测试NPC" }, motionSource, moss);

            KimodoConvertedMotion? played = null;
            agent.MotionPlayed += m => played = m;

            agent.StartAsync().GetAwaiter().GetResult();
            transport.Push(JsonSerializer.Serialize(new MossServerEvent
            {
                Type = "action",
                Name = "wave",
                Prompt = "抬手挥别",
                DurationSeconds = 2f,
                WaitForCompletion = true
            }, KimodoTestJson.Options));

            WaitUntil(() => played != null, 3000, () => agent.Update(0.01f));
            AssertTrue(played != null, "Action event should produce a Kimodo motion.");
            AssertEqual(AiNpcState.Acting, agent.State, "Agent should enter Acting.");

            // 推进到动作结束，action_result 必须回传 MOSS。
            for (int i = 0; i < 30 && agent.State == AiNpcState.Acting; i++)
            {
                agent.Update(0.02f);
                Thread.Sleep(2);
            }
            WaitUntil(() => transport.Sent.Any(s => s.Contains("\"type\":\"action_result\"")), 2000);
            AssertTrue(transport.Sent.Any(s => s.Contains("\"status\":\"completed\"")),
                "Completed action must be reported back to MOSS-Live.");

            // 再触发一次动作，随后打断。
            transport.Push("{\"type\":\"action\",\"name\":\"wave\",\"prompt\":\"挥手\",\"duration_seconds\":2}");
            for (int i = 0; i < 30 && agent.State != AiNpcState.Acting; i++)
            {
                agent.Update(0.01f);
                Thread.Sleep(2);
            }
            transport.Push("{\"type\":\"interrupt\"}");
            WaitUntil(() => agent.State == AiNpcState.Listening, 1000, () => agent.Update(0.01f));

            AssertEqual(AiNpcState.Listening, agent.State, "Barge-in should drop back to Listening.");
            AssertEqual("", agent.CurrentActionName, "Barge-in should stop the current action.");

            agent.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        /// <summary>语音/动作 Agent 绑定 NpcBrain 后：人格注入完整心智、情绪回写、对话记忆回写。</summary>
        public static void TestAiNpcAgentSharesMindModelWithNpcBrain()
        {
            var config = AiNpcConfig.CreateDefault();
            config.MossLive.Enabled = false; // 不连 MOSS，纯本地验证事件处理路径

            var npc = new NpcCharacter
            {
                Name = "艾莉",
                Occupation = "吟游诗人",
                Faction = "吟游者公会",
                Personality = new NpcPersonality(0.8f, 0.4f, 0.9f, 0.6f, 0.2f)
            };
            npc.GetRelationship("玩家").Change(35f);
            npc.Memory.Remember(NpcMemoryKind.Event, "酒馆", "昨晚酒馆有人唱跑调", importance: 0.7f);

            var brain = new NpcBrain(npc);
            var agent = new AiNpcAgent(config, npc, brain: brain);

            // 1) 人格提示应注入完整心智（个性/情感/关系/记忆），而非仅名字/职业/阵营。
            var persona = agent.GetType()
                .GetMethod("BuildPersona", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .Invoke(agent, null) as string;
            AssertTrue(persona != null, "BuildPersona 应返回字符串。");
            AssertTrue(persona!.Contains("艾莉"), "人格应含名字。");
            AssertTrue(persona.Contains("吟游诗人"), "人格应含职业。");
            AssertTrue(persona.Contains("外向"), "人格应注入 OCEAN 个性描述。");
            AssertTrue(persona.Contains("35"), "人格应注入与玩家的关系值。");
            AssertTrue(persona.Contains("跑调"), "人格应注入记忆摘要。");

            // 2) MOSS 情绪事件应回写心智情感（英文标签 → Plutchik 枚举）。
            float joyBefore = npc.Emotion.Get(NpcEmotionType.Joy);
            var handleEmotion = agent.GetType()
                .GetMethod("HandleMossEvent", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            handleEmotion.Invoke(agent, new object[] { new MossServerEvent { Type = "emotion", Label = "joy" } });
            AssertTrue(npc.Emotion.Get(NpcEmotionType.Joy) > joyBefore, "joy 情绪事件应提升心智 Joy 强度。");
            AssertTrue(agent.Emotion == "joy", "Agent 顶层情绪标签应更新。");

            // 未知标签应安全忽略，不写心智。
            float before = npc.Emotion.Get(NpcEmotionType.Fear);
            handleEmotion.Invoke(agent, new object[] { new MossServerEvent { Type = "emotion", Label = "unknown_xyz" } });
            AssertTrue(npc.Emotion.Get(NpcEmotionType.Fear) == before, "未知情绪标签不应改动心智。");

            // 3) 对话片段应写入心智记忆（玩家/NPC 双向）。
            int memBefore = npc.Memory.Entries.Count;
            handleEmotion.Invoke(agent, new object[] { new MossServerEvent { Type = "text_in", Text = "你好，艾莉。" } });
            handleEmotion.Invoke(agent, new object[] { new MossServerEvent { Type = "text_done", Text = "嗨，旅人。" } });
            AssertTrue(npc.Memory.Entries.Count >= memBefore + 2, "text_in/text_done 应各写入一条记忆。");
            AssertTrue(npc.Memory.Recall("玩家").Any(), "玩家对话应记入玩家主体记忆。");
            AssertTrue(npc.Memory.Recall("NPC").Any(), "NPC 回复应记入 NPC 主体记忆。");

            agent.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        // ───────────────────────── helpers ─────────────────────────

        private static KimodoMotionResponse BuildSyntheticMotion()
        {
            var motion = new KimodoMotionResponse
            {
                Id = "synthetic",
                Fps = 30f,
                DurationSeconds = 0.1f,
                Skeleton = new KimodoSkeletonDef
                {
                    Name = "test",
                    Joints = new List<KimodoJointDef>
                    {
                        new() { Name = "Hips", ParentIndex = -1, RestPosition = new[] { 0f, 1f, 0f }, RestRotation = new[] { 0f, 0f, 0f, 1f } },
                        new() { Name = "LeftHand", ParentIndex = 0, RestPosition = new[] { 0f, 0.5f, 0.2f }, RestRotation = new[] { 0f, 0f, 0f, 1f } }
                    }
                },
                RootPositions = new[]
                {
                    new[] { 0f, 1f, 0f },
                    new[] { 0.2f, 1f, 0f },
                    new[] { 0.4f, 1f, 0f }
                },
                Rotations = new[]
                {
                    new[] { new[] { 0f, 0f, 0f, 1f }, new[] { 0f, 0f, 0f, 1f } },
                    new[] { new[] { 0f, 0f, 0f, 1f }, new[] { 0f, 0.7071f, 0f, 0.7071f } },
                    new[] { new[] { 0f, 0f, 0f, 1f }, new[] { 0f, 0.7071f, 0f, 0.7071f } }
                }
            };
            return motion;
        }

        private static void WaitUntil(Func<bool> condition, int timeoutMs, Action? tick = null)
        {
            var deadline = Environment.TickCount64 + timeoutMs;
            while (Environment.TickCount64 < deadline)
            {
                if (condition()) return;
                tick?.Invoke();
                Thread.Sleep(5);
            }
        }

        private static KimodoConvertedMotion BuildConverted(KimodoMotionResponse motion)
        {
            var config = AiNpcConfig.CreateDefault().Kimodo;
            config.UseRootMotion = false;
            var converter = new KimodoMotionConverter(config);
            return converter.Convert(motion, Skeleton.CreateBiped("Contact"), "contacts");
        }

        private sealed class FakeKimodoSource : IKimodoMotionSource
        {
            private readonly KimodoMotionResponse _motion;
            public FakeKimodoSource(KimodoMotionResponse motion) => _motion = motion;

            public Task<KimodoHealthResponse> HealthAsync(CancellationToken ct = default)
                => Task.FromResult(new KimodoHealthResponse { Status = "ok" });

            public Task<KimodoMotionResponse> GenerateAsync(KimodoGenerateRequest request, CancellationToken ct = default)
                => Task.FromResult(_motion);
        }

        private sealed class StubHttpHandler : HttpMessageHandler
        {
            private readonly string _response;
            public Uri? LastRequestUri { get; private set; }
            public string LastRequestBody { get; private set; } = "";

            public StubHttpHandler(string response) => _response = response;

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                LastRequestUri = request.RequestUri;
                LastRequestBody = request.Content == null ? "" : await request.Content.ReadAsStringAsync(ct);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(_response, Encoding.UTF8, "application/json")
                };
            }
        }

        private sealed class InMemoryMossTransport : IMossLiveTransport
        {
            private readonly Channel<string> _inbound = Channel.CreateUnbounded<string>();
            public readonly List<string> Sent = new();
            public bool IsConnected { get; private set; }

            public void Push(string json) => _inbound.Writer.TryWrite(json);

            public Task ConnectAsync(Uri uri, CancellationToken ct)
            {
                IsConnected = true;
                return Task.CompletedTask;
            }

            public Task SendAsync(string text, CancellationToken ct)
            {
                lock (Sent) Sent.Add(text);
                return Task.CompletedTask;
            }

            public async Task<string?> ReceiveAsync(CancellationToken ct)
            {
                try { return await _inbound.Reader.ReadAsync(ct); }
                catch (ChannelClosedException) { return null; }
            }

            public Task CloseAsync(CancellationToken ct)
            {
                IsConnected = false;
                _inbound.Writer.TryComplete();
                return Task.CompletedTask;
            }

            public ValueTask DisposeAsync()
            {
                _inbound.Writer.TryComplete();
                return ValueTask.CompletedTask;
            }
        }
    }

    internal static class KimodoTestJson
    {
        public static readonly JsonSerializerOptions Options = new()
        {
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };
    }
}
