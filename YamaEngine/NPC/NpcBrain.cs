// ════════════════════════════════════════════════════════════════════════
// NpcBrain.cs — LLM 引导行为驱动的 AI NPC
//
// 核心机制：NPC 的"行为意图"由 LLM 生成（基于个性/情感/记忆/关系/日程/
// 情境），经结构化 JSON 解析后，驱动行为执行。这是"LLM 引导行为"而非仅对话。
//
// 组成：
//   1. NpcIntentKind / NpcIntent      — LLM 输出的结构化行为意图
//   2. NpcBrain                       — 决策循环：LLM 决策 → 解析 → 应用 → 执行
//   3. NpcQuestKind / NpcQuest / NpcQuestSystem — 任务/目标系统
//   4. NpcSocialSimulation            — NPC 群体社交模拟（基于日程/邻近/关系）
//
// 行为执行可对接既有 AI 组件：
//   - BehaviorTree（BehaviorTreeManager.TickAll）
//   - StateTree（StateTreeTask 的 Enter/Tick/Exit）
//   - 简单模型（意图 → 移动/停留/远离）
//
// LLM 经 YamaEngine.AI.LLMClient（真实 HTTP+SSE，多平台，Endpoint 可配）。
// ════════════════════════════════════════════════════════════════════════

using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using YamaEngine.AI;

namespace YamaEngine.NPC;

// ──────────────────────────────────────────────────────────────────
// 1. LLM 引导的行为意图
// ──────────────────────────────────────────────────────────────────

/// <summary>NPC 行为意图类型（LLM 决策输出）。</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum NpcIntentKind
{
    Idle, TalkTo, GoTo, Flee, Work, Socialize, Quest, Wander
}

/// <summary>LLM 引导的结构化行为意图。</summary>
public sealed class NpcIntent
{
    [JsonPropertyName("kind")] public string Kind { get; set; } = "Idle";
    [JsonPropertyName("target")] public string Target { get; set; } = "";
    [JsonPropertyName("x")] public float X { get; set; }
    [JsonPropertyName("z")] public float Z { get; set; }
    [JsonPropertyName("priority")] public float Priority { get; set; } = 0.5f;
    [JsonPropertyName("reason")] public string Reason { get; set; } = "";

    [JsonIgnore] public Vector3 TargetPosition => new(X, 0f, Z);
    [JsonIgnore] public NpcIntentKind IntentKind => Enum.TryParse<NpcIntentKind>(Kind, true, out var k) ? k : NpcIntentKind.Idle;
    [JsonIgnore] public bool IsValid => Priority > 0f;

    public override string ToString() => $"{IntentKind}(target={Target}, p={Priority:0.0}, reason={Reason})";
}

// ──────────────────────────────────────────────────────────────────
// 2. NpcBrain — LLM 决策循环
// ──────────────────────────────────────────────────────────────────

/// <summary>
/// NPC 大脑：把 LLM 生成的意图解析并驱动行为执行。
/// 每个 NPC 一个，持有当前意图与执行状态。
/// </summary>
public sealed class NpcBrain
{
    private readonly NpcCharacter _npc;
    private readonly float _moveSpeed;
    private Vector3 _goal;

    public NpcIntent? CurrentIntent { get; private set; }
    public Vector3 Goal => _goal;
    public bool IsExecuting { get; private set; }
    public string LastDecisionReason { get; private set; } = "";
    /// <summary>LLM 决策失败时的本地回退（保证行为不中断）。</summary>
    public bool FallbackToHeuristic { get; set; } = true;

    public NpcBrain(NpcCharacter npc, float moveSpeed = 3f)
    {
        _npc = npc ?? throw new ArgumentNullException(nameof(npc));
        _moveSpeed = MathF.Max(moveSpeed, 0.01f);
    }

    /// <summary>构建 LLM 决策提示词（情境 + 心智注入 → 行为决策）。</summary>
    public string BuildIntentPrompt(string context)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"你是「{_npc.Name}」（{_npc.Occupation}，阵营{_npc.Faction}）的决策大脑。");
        sb.AppendLine($"性格：{_npc.Personality.Describe()}。");
        sb.AppendLine($"心情：{NpcEmotionState.NameOf(_npc.Emotion.Dominant)}（强度{_npc.Emotion.DominantIntensity:0.0}）。");
        sb.AppendLine($"当前日程：{NpcSchedule.NameOf(_npc.CurrentActivity(NpcWorld.DefaultHour))}。");
        sb.AppendLine($"关系：{_npc.GetRelationship("玩家").Value:0}（{_npc.GetRelationship("玩家").Describe()}）。");
        sb.AppendLine($"记忆：{_npc.Memory.Summarize(3)}。");
        sb.AppendLine($"情境：{context}");
        sb.AppendLine("请基于以上做出当下最合理的行为选择，只输出一个 JSON 对象，无其他文字：");
        sb.AppendLine("{\"kind\":\"TalkTo|GoTo|Flee|Work|Socialize|Quest|Wander|Idle\",\"target\":\"实体名或空\",\"x\":0,\"z\":0,\"priority\":0.0到1.0,\"reason\":\"简短理由\"}");
        return sb.ToString();
    }

    /// <summary>解析 LLM 输出为意图（容错：剥去代码块、截取首个 JSON）。</summary>
    public static NpcIntent? ParseIntent(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var text = raw.Trim();
        // 剥去 markdown 代码块 ```json ... ```
        int fence = text.IndexOf("```", StringComparison.Ordinal);
        if (fence >= 0)
        {
            int start = text.IndexOf('{', fence);
            int end = text.LastIndexOf('}');
            if (start >= 0 && end > start) text = text.Substring(start, end - start + 1);
        }
        try
        {
            var intent = JsonSerializer.Deserialize<NpcIntent>(text,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return intent != null && Enum.TryParse<NpcIntentKind>(intent.Kind, true, out _)
                ? intent : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>LLM 驱动决策：获取意图并应用。</summary>
    public async Task<NpcIntent?> DecideAsync(string context, LLMClient client, CancellationToken ct = default)
    {
        var prompt = BuildIntentPrompt(context);
        var resp = await client.CompleteAsync(prompt, ct).ConfigureAwait(false);
        if (!resp.Success || string.IsNullOrWhiteSpace(resp.Content))
        {
            LastDecisionReason = "llm_failed";
            return FallbackToHeuristic ? ApplyHeuristic(context) : null;
        }
        var intent = ParseIntent(resp.Content);
        if (intent == null)
        {
            LastDecisionReason = "parse_failed";
            return FallbackToHeuristic ? ApplyHeuristic(context) : null;
        }
        LastDecisionReason = intent.Reason;
        ApplyIntent(intent);
        return intent;
    }

    /// <summary>本地启发式回退：按日程/关系生成行为（保证 LLM 故障时 NPC 仍行动）。</summary>
    public NpcIntent ApplyHeuristic(string context)
    {
        var activity = _npc.CurrentActivity(NpcWorld.DefaultHour);
        var intent = activity switch
        {
            NpcActivityKind.Sleep => new NpcIntent { Kind = "Idle", Priority = 0.3f, Reason = "睡眠中" },
            NpcActivityKind.Work => new NpcIntent { Kind = "Work", Priority = 0.7f, Reason = "工作时间" },
            NpcActivityKind.Social => new NpcIntent { Kind = "Socialize", Priority = 0.6f, Reason = "社交时段" },
            NpcActivityKind.Meal => new NpcIntent { Kind = "GoTo", Priority = 0.5f, Reason = "用餐" },
            _ => new NpcIntent { Kind = "Wander", Priority = 0.4f, Reason = "闲逛" }
        };
        ApplyIntent(intent);
        return intent;
    }

    /// <summary>把意图应用到执行状态（设置目标/执行标志）。</summary>
    public void ApplyIntent(NpcIntent intent)
    {
        if (intent == null) return;
        CurrentIntent = intent;
        _goal = intent.TargetPosition;
        IsExecuting = true;
    }

    /// <summary>逐帧执行当前意图（简单行为模型：GoTo 移动 / Flee 远离 / TalkTo 保持）。</summary>
    public void Tick(float deltaSeconds, Vector3? threatPosition = null)
    {
        if (!IsExecuting || CurrentIntent == null) return;
        switch (CurrentIntent.IntentKind)
        {
            case NpcIntentKind.GoTo:
                MoveToward(_goal, deltaSeconds);
                break;
            case NpcIntentKind.Flee:
                if (threatPosition.HasValue)
                    MoveAway(threatPosition.Value, deltaSeconds);
                break;
            case NpcIntentKind.TalkTo:
                // 对话保持：无需移动
                break;
            default:
                break; // Idle/Work/Socialize/Quest/Wander：行为由上层（行为树/StateTree）执行
        }
    }

    /// <summary>停止当前行为。</summary>
    public void Stop() { IsExecuting = false; CurrentIntent = null; }

    private void MoveToward(Vector3 target, float dt)
    {
        var to = target - _npc.Position;
        to.Y = 0f;
        float dist = to.Length();
        if (dist <= 1e-3f) { IsExecuting = false; return; }
        if (dist <= _moveSpeed * dt) { _npc.Position = target; IsExecuting = false; }
        else _npc.Position += Vector3.Normalize(to) * _moveSpeed * dt;
    }

    private void MoveAway(Vector3 threat, float dt)
    {
        var away = _npc.Position - threat;
        away.Y = 0f;
        if (away.LengthSquared() < 1e-4f) away = new Vector3(1f, 0f, 0f);
        _npc.Position += Vector3.Normalize(away) * _moveSpeed * dt;
    }
}

// ──────────────────────────────────────────────────────────────────
// 3. 任务/目标系统
// ──────────────────────────────────────────────────────────────────

public enum NpcQuestKind { Kill, Collect, Reach, Deliver, Dialog }
public enum NpcQuestState { Available, Active, Completed, Failed }

/// <summary>单个 NPC 任务。</summary>
public sealed class NpcQuest
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public NpcQuestKind Kind { get; set; }
    public string Target { get; set; } = "";
    public int Required { get; set; } = 1;
    public int Progress { get; private set; }
    public string Reward { get; set; } = "";
    public NpcQuestState State { get; private set; } = NpcQuestState.Available;

    public bool IsComplete => State == NpcQuestState.Completed;
    public float ProgressRatio => Required > 0 ? Math.Clamp((float)Progress / Required, 0f, 1f) : 0f;

    /// <summary>激活任务（从 Available 进入 Active）。</summary>
    public void Activate()
    {
        if (State == NpcQuestState.Available) State = NpcQuestState.Active;
    }

    /// <summary>推进任务进度，达到要求则完成。</summary>
    public void Advance(int amount = 1)
    {
        if (State != NpcQuestState.Active) return;
        Progress = Math.Min(Progress + Math.Max(amount, 0), Required);
        if (Progress >= Required) State = NpcQuestState.Completed;
    }

    public void Fail() { if (State != NpcQuestState.Completed) State = NpcQuestState.Failed; }
}

/// <summary>任务系统：管理 NPC 的任务生命周期。</summary>
public sealed class NpcQuestSystem
{
    private readonly List<NpcQuest> _quests = new();
    public IReadOnlyList<NpcQuest> Quests => _quests;
    public event Action<NpcQuest>? OnQuestCompleted;

    public NpcQuest Add(NpcQuest quest)
    {
        _quests.Add(quest);
        return quest;
    }

    public NpcQuest? Find(string id) => _quests.FirstOrDefault(q => q.Id == id);

    public IEnumerable<NpcQuest> Active => _quests.Where(q => q.State == NpcQuestState.Active);

    /// <summary>推进指定类型/目标的任务进度，并触发完成事件。</summary>
    public void ReportProgress(NpcQuestKind kind, string target, int amount = 1)
    {
        foreach (var q in Active)
        {
            if (q.Kind != kind) continue;
            if (!string.IsNullOrEmpty(q.Target) && !q.Target.Equals(target, StringComparison.OrdinalIgnoreCase)) continue;
            q.Advance(amount);
            if (q.IsComplete) OnQuestCompleted?.Invoke(q);
        }
    }
}

// ──────────────────────────────────────────────────────────────────
// 4. NPC 群体社交模拟
// ──────────────────────────────────────────────────────────────────

/// <summary>一条社交事件（可驱动对话/关系变化）。</summary>
public readonly struct NpcSocialEvent
{
    public readonly NpcCharacter From;
    public readonly NpcCharacter To;
    public readonly NpcActivityKind Activity;
    public readonly float Hour;
    public NpcSocialEvent(NpcCharacter from, NpcCharacter to, NpcActivityKind activity, float hour)
    {
        From = from; To = to; Activity = activity; Hour = hour;
    }
}

/// <summary>
/// NPC 群体社交模拟：基于邻近 NPC 对、当前日程活动与关系值生成社交事件。
/// 复用既有群体/人群思想（MassCrowd），此处为社交层。
/// </summary>
public sealed class NpcSocialSimulation
{
    public float InteractionRadius { get; set; } = 8f;
    public float EventCooldownSeconds { get; set; } = 10f;
    private readonly Dictionary<string, float> _cooldowns = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<NpcSocialEvent> _pending = new();

    public IReadOnlyList<NpcSocialEvent> PendingEvents => _pending;

    /// <summary>扫描 NPC 群体生成社交事件（每帧调用）。</summary>
    public void Update(NpcWorld world, float deltaSeconds, float hour)
    {
        _pending.Clear();
        var chars = world.Characters;
        for (int i = 0; i < chars.Count; i++)
        {
            for (int j = i + 1; j < chars.Count; j++)
            {
                var a = chars[i]; var b = chars[j];
                var d = Vector3.Distance(a.Position, b.Position);
                if (d > InteractionRadius) continue;

                var activity = a.CurrentActivity(hour);
                // 只有社交/休闲时段且双方都在社交活动时触发社交事件
                if (activity != NpcActivityKind.Social && activity != NpcActivityKind.Leisure) continue;

                string key = Key(a, b);
                if (_cooldowns.TryGetValue(key, out float last) && hour * 3600f - last < EventCooldownSeconds) continue;
                _cooldowns[key] = hour * 3600f;

                _pending.Add(new NpcSocialEvent(a, b, activity, hour));
                // 双向轻微关系增进（社交提升关系）
                a.GetRelationship(b.Name).Change(0.5f);
                b.GetRelationship(a.Name).Change(0.5f);
            }
        }
    }

    /// <summary>取出本帧社交事件并清空。</summary>
    public IReadOnlyList<NpcSocialEvent> Drain()
    {
        var copy = _pending.ToArray();
        _pending.Clear();
        return copy;
    }

    private static string Key(NpcCharacter a, NpcCharacter b) =>
        string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase) < 0
            ? a.Name + "|" + b.Name : b.Name + "|" + a.Name;
}
