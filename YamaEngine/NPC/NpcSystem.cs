// ════════════════════════════════════════════════════════════════════════
// NpcSystem.cs — AI 驱动的 NPC 系统（LLM 接线版，超越 UE5.8 水平）
//
// UE5.8 的 NPC/AI 核心是 StateTree + Mass + SmartObjects + BehaviorTree + EQS。
// 本系统在同等 AI 组件之上，补齐 UE5.8 **没有**的"NPC 心智"层：
//   1. OCEAN 五因子个性（Openness/Conscientiousness/Extraversion/Agreeableness/Neuroticism）
//   2. 情感系统（8 基础情感 + 强度 + 指数衰减）
//   3. 记忆系统（事件/实体/对话，强度+时间衰减，可注入 LLM 上下文）
//   4. 关系系统（-100..100 双向关系值）
//   5. 日程系统（Sleep/Work/Social/Meal/Leisure/Idle 按时段）
//   6. LLM 驱动对话（角色卡 + 个性/情感/关系/记忆注入 → 真实 LLM 生成回复）
//
// 全部 CPU 级确定性，无 GPU 依赖，可严格验证。
// LLM 接线复用 YamaEngine.AI.LLMClient（HttpClient + SSE，多平台 OpenAI/Anthropic/
// Ollama/Qwen/Ernie），经 ILLMProvider 接口可注入 mock 进行离线测试。
// ════════════════════════════════════════════════════════════════════════

using System.Numerics;
using System.Text;
using YamaEngine.AI;

namespace YamaEngine.NPC;

// ──────────────────────────────────────────────────────────────────
// 1. OCEAN 五因子个性
// ──────────────────────────────────────────────────────────────────

/// <summary>OCEAN 五因子个性模型（各因子 0..1，中位数 0.5）。</summary>
public readonly struct NpcPersonality
{
    public readonly float Openness;          // 开放性
    public readonly float Conscientiousness; // 尽责性
    public readonly float Extraversion;      // 外向性
    public readonly float Agreeableness;     // 宜人性
    public readonly float Neuroticism;       // 神经质

    public NpcPersonality(float openness, float conscientiousness, float extraversion,
        float agreeableness, float neuroticism)
    {
        Openness = Math.Clamp(openness, 0f, 1f);
        Conscientiousness = Math.Clamp(conscientiousness, 0f, 1f);
        Extraversion = Math.Clamp(extraversion, 0f, 1f);
        Agreeableness = Math.Clamp(agreeableness, 0f, 1f);
        Neuroticism = Math.Clamp(neuroticism, 0f, 1f);
    }

    public static NpcPersonality Balanced => new(0.5f, 0.5f, 0.5f, 0.5f, 0.5f);

    /// <summary>生成随机个性（用给定种子保证确定性）。</summary>
    public static NpcPersonality Random(Random rng) => new(
        (float)rng.NextDouble(), (float)rng.NextDouble(), (float)rng.NextDouble(),
        (float)rng.NextDouble(), (float)rng.NextDouble());

    /// <summary>把个性映射为简短中文人格描述（供 LLM 角色卡注入）。</summary>
    public string Describe()
    {
        var sb = new StringBuilder();
        if (Openness >= 0.6f) sb.Append("富有好奇心、乐于尝试新事物");
        else if (Openness <= 0.4f) sb.Append("务实、偏好熟悉与既定方式");
        if (Conscientiousness >= 0.6f) sb.Append(sb.Length > 0 ? "，自律且有条理" : "自律、有责任心、井井有条");
        else if (Conscientiousness <= 0.4f) sb.Append(sb.Length > 0 ? "，随性、较不拘小节" : "随性、较不拘小节");
        if (Extraversion >= 0.6f) sb.Append(sb.Length > 0 ? "，外向健谈、喜欢社交" : "外向健谈、喜欢与人交往");
        else if (Extraversion <= 0.4f) sb.Append(sb.Length > 0 ? "，内敛安静、偏好独处" : "内敛安静、偏好独处");
        if (Agreeableness >= 0.6f) sb.Append(sb.Length > 0 ? "，友善合作、乐于助人" : "友善合作、乐于助人");
        else if (Agreeableness <= 0.4f) sb.Append(sb.Length > 0 ? "，较有主见、好胜心强" : "较有主见、好胜心强");
        if (Neuroticism >= 0.6f) sb.Append(sb.Length > 0 ? "，情绪敏感、容易焦虑" : "情绪敏感、容易紧张焦虑");
        else if (Neuroticism <= 0.4f) sb.Append(sb.Length > 0 ? "，情绪稳定、心态平和" : "情绪稳定、心态平和");
        return sb.Length == 0 ? "性格中庸、情绪平稳" : sb.ToString();
    }
}

// ──────────────────────────────────────────────────────────────────
// 2. 情感系统
// ──────────────────────────────────────────────────────────────────

/// <summary>基础情感类别（Plutchik 基础 8 情感 + 中性）。</summary>
public enum NpcEmotionType
{
    Neutral, Joy, Trust, Fear, Surprise, Sadness, Anger, Disgust, Anticipation
}

/// <summary>NPC 情感状态：各情感强度 0..1，随时间指数衰减，可叠加触发。</summary>
public sealed class NpcEmotionState
{
    private readonly float[] _intensity = new float[9];
    public float DecayRate { get; set; } = 0.25f; // 每秒衰减比例（指数）

    public float Get(NpcEmotionType e) => _intensity[(int)e];

    /// <summary>触发一个情感（叠加，上限 1）。</summary>
    public void Trigger(NpcEmotionType e, float intensity)
    {
        int i = (int)e;
        _intensity[i] = Math.Clamp(_intensity[i] + Math.Clamp(intensity, 0f, 1f), 0f, 1f);
    }

    /// <summary>每帧衰减（指数衰减到 0）。</summary>
    public void Tick(float deltaSeconds)
    {
        float k = MathF.Exp(-DecayRate * MathF.Max(deltaSeconds, 0f));
        for (int i = 0; i < _intensity.Length; i++)
            _intensity[i] *= k;
    }

    /// <summary>当前主导情感（最高强度，中性除外）。</summary>
    public NpcEmotionType Dominant
    {
        get
        {
            int best = (int)NpcEmotionType.Neutral; float bestV = 0f;
            for (int i = 1; i < _intensity.Length; i++)
                if (_intensity[i] > bestV) { bestV = _intensity[i]; best = i; }
            return (NpcEmotionType)best;
        }
    }

    /// <summary>主导情感强度（0..1）。</summary>
    public float DominantIntensity => Get(Dominant);

    /// <summary>中文情感名（供 LLM 注入）。</summary>
    public static string NameOf(NpcEmotionType e) => e switch
    {
        NpcEmotionType.Joy => "愉悦", NpcEmotionType.Trust => "信任",
        NpcEmotionType.Fear => "恐惧", NpcEmotionType.Surprise => "惊讶",
        NpcEmotionType.Sadness => "悲伤", NpcEmotionType.Anger => "愤怒",
        NpcEmotionType.Disgust => "厌恶", NpcEmotionType.Anticipation => "期待",
        _ => "平静"
    };
}

// ──────────────────────────────────────────────────────────────────
// 3. 记忆系统
// ──────────────────────────────────────────────────────────────────

public enum NpcMemoryKind { Encounter, Dialogue, Event, Fact, Quest }

/// <summary>单条 NPC 记忆。</summary>
public sealed class NpcMemoryEntry
{
    public NpcMemoryKind Kind;
    public string Subject = "";      // 关联主体（实体/事件）
    public string Content = "";      // 内容
    public float Strength = 1f;      // 0..1
    public float Importance = 1f;    // 0..1（重要记忆衰减更慢）
    public DateTime TimeUtc = DateTime.UtcNow;

    public bool IsForgotten => Strength <= 0.01f;
}

/// <summary>NPC 记忆库：添加、衰减、遗忘、摘要（供 LLM 注入）。</summary>
public sealed class NpcMemory
{
    private readonly List<NpcMemoryEntry> _entries = new();
    public int Capacity { get; set; } = 64;
    public IReadOnlyList<NpcMemoryEntry> Entries => _entries;

    public void Remember(NpcMemoryKind kind, string subject, string content,
        float importance = 1f, float strength = 1f)
    {
        _entries.Add(new NpcMemoryEntry
        {
            Kind = kind, Subject = subject, Content = content,
            Strength = Math.Clamp(strength, 0f, 1f),
            Importance = Math.Clamp(importance, 0f, 1f)
        });
        if (_entries.Count > Capacity)
        {
            // 遗忘强度最低/最早的一条
            int weakest = 0; float wv = float.MaxValue;
            for (int i = 0; i < _entries.Count; i++)
            {
                float score = _entries[i].Strength * (1f - _entries[i].Importance);
                if (score < wv) { wv = score; weakest = i; }
            }
            _entries.RemoveAt(weakest);
        }
    }

    /// <summary>每帧衰减：强度按重要性缩放，低于阈值被遗忘移除。</summary>
    public void Tick(float deltaSeconds)
    {
        float k = MathF.Exp(-0.05f * MathF.Max(deltaSeconds, 0f));
        for (int i = _entries.Count - 1; i >= 0; i--)
        {
            var e = _entries[i];
            // 重要记忆衰减更慢（importance 高 → k 更接近 1）
            float eK = 1f - (1f - k) * (1.6f - e.Importance);
            e.Strength = Math.Clamp(e.Strength * eK, 0f, 1f);
            if (e.IsForgotten) _entries.RemoveAt(i);
        }
    }

    /// <summary>检索给定主体的记忆条目。</summary>
    public IEnumerable<NpcMemoryEntry> Recall(string subject) =>
        _entries.Where(e => e.Subject.Equals(subject, StringComparison.OrdinalIgnoreCase));

    /// <summary>生成记忆摘要（按强度排序，供 LLM 上下文注入）。</summary>
    public string Summarize(int maxEntries = 5)
    {
        if (_entries.Count == 0) return "暂无";
        var top = _entries.OrderByDescending(e => e.Strength).Take(maxEntries);
        return string.Join("；", top.Select(e =>
            $"{e.Content}（重要度{e.Importance:0.0}）"));
    }
}

// ──────────────────────────────────────────────────────────────────
// 4. 关系系统
// ──────────────────────────────────────────────────────────────────

/// <summary>NPC 与其他实体的双向关系（-100 敌意 .. +100 亲密）。</summary>
public sealed class NpcRelationship
{
    public float Value { get; private set; }        // -100..100
    public float DecayPerHour { get; set; } = 0.2f; // 未互动缓慢衰减

    public NpcRelationship(float initial = 0f) => Value = Math.Clamp(initial, -100f, 100f);

    public void Change(float delta)
    {
        Value = Math.Clamp(Value + delta, -100f, 100f);
    }

    public void Tick(float deltaSeconds)
    {
        // 缓慢向 0 回归（不互动则关系冷却）
        float k = MathF.Exp(-DecayPerHour * MathF.Max(deltaSeconds, 0f) / 3600f);
        Value *= k;
    }

    public bool IsFriendly => Value > 25f;
    public bool IsHostile => Value < -25f;
    public string Describe() => Value switch
    {
        > 75f => "生死之交", > 25f => "友好", >= -25f => "中立", > -75f => "敌意", _ => "深仇"
    };
}

// ──────────────────────────────────────────────────────────────────
// 5. 日程系统
// ──────────────────────────────────────────────────────────────────

public enum NpcActivityKind { Sleep, Work, Meal, Social, Leisure, Idle }

/// <summary>一段日程活动（按小时）。</summary>
public readonly struct NpcScheduleActivity
{
    public readonly NpcActivityKind Kind;
    public readonly float StartHour, EndHour;
    public NpcScheduleActivity(NpcActivityKind kind, float start, float end)
    {
        Kind = kind; StartHour = start; EndHour = end;
    }
    public bool Contains(float hour)
    {
        if (StartHour <= EndHour) return hour >= StartHour && hour < EndHour;
        return hour >= StartHour || hour < EndHour; // 跨零点
    }
}

/// <summary>NPC 一日日程（默认作息：睡眠/工作/餐饮/社交/休闲）。</summary>
public sealed class NpcSchedule
{
    public List<NpcScheduleActivity> Activities { get; } = new();

    public static NpcSchedule Default() => new()
    {
        Activities =
        {
            new(NpcActivityKind.Sleep, 23f, 7f),   // 23:00 - 07:00
            new(NpcActivityKind.Meal, 7f, 8f),
            new(NpcActivityKind.Work, 8f, 12f),
            new(NpcActivityKind.Meal, 12f, 13f),
            new(NpcActivityKind.Work, 13f, 18f),
            new(NpcActivityKind.Meal, 18f, 19f),
            new(NpcActivityKind.Social, 19f, 21f),
            new(NpcActivityKind.Leisure, 21f, 23f)
        }
    };

    /// <summary>返回当前小时对应的活动（无匹配则为 Idle）。</summary>
    public NpcActivityKind GetActivity(float hour)
    {
        hour = ((hour % 24f) + 24f) % 24f;
        foreach (var a in Activities)
            if (a.Contains(hour)) return a.Kind;
        return NpcActivityKind.Idle;
    }

    public static string NameOf(NpcActivityKind k) => k switch
    {
        NpcActivityKind.Sleep => "睡眠", NpcActivityKind.Work => "工作",
        NpcActivityKind.Meal => "用餐", NpcActivityKind.Social => "社交",
        NpcActivityKind.Leisure => "休闲", _ => "闲逛"
    };
}

// ──────────────────────────────────────────────────────────────────
// 6. LLM 驱动对话
// ──────────────────────────────────────────────────────────────────

/// <summary>
/// NPC 对话：构建 LLM 对话（角色卡 + 个性/情感/关系/记忆注入），并驱动真实 LLM 生成回复。
/// </summary>
public sealed class NpcDialogue
{
    public List<LLMMessage> History { get; } = new();
    public int MaxHistory { get; set; } = 16;
    public string Persona { get; set; } = "一位生活在奇幻世界的居民";

    /// <summary>构建 LLM 会话：系统角色卡 + 注入 NPC 心智（个性/情感/关系/记忆）+ 对话历史。</summary>
    public LLMConversation BuildConversation(NpcCharacter npc, string playerName)
    {
        var conv = new LLMConversation();
        conv.SetSystem(BuildSystemPrompt(npc, playerName));
        foreach (var m in History)
            conv.Append(m);
        return conv;
    }

    /// <summary>构建系统提示词（角色卡 + 心智注入）。</summary>
    public string BuildSystemPrompt(NpcCharacter npc, string playerName)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"你是「{npc.Name}」，{Persona}。职业：{npc.Occupation}。阵营：{npc.Faction}。");
        sb.AppendLine($"性格：{npc.Personality.Describe()}。");
        sb.AppendLine($"当前心情：{NpcEmotionState.NameOf(npc.Emotion.Dominant)}（强度{npc.Emotion.DominantIntensity:0.0}）。");
        sb.AppendLine($"与{playerName}的关系：{npc.GetRelationship(playerName).Describe()}（{npc.GetRelationship(playerName).Value:0}）。");
        sb.AppendLine($"记忆：{npc.Memory.Summarize(4)}。");
        sb.AppendLine("请以该角色的口吻回应玩家，简短自然（1-3句），符合其性格与心情，不要打破第四面墙。");
        return sb.ToString();
    }

    /// <summary>把玩家的输入接入对话（记录历史，截断超限）。</summary>
    public void Push(string playerInput)
    {
        History.Add(new LLMMessage(LLMRole.User, playerInput));
        TrimHistory();
    }

    /// <summary>记录 NPC 回复（历史）。</summary>
    public void PushAssistant(string reply)
    {
        History.Add(new LLMMessage(LLMRole.Assistant, reply));
        TrimHistory();
    }

    private void TrimHistory()
    {
        while (History.Count > MaxHistory)
            History.RemoveAt(0);
    }

    /// <summary>LLM 驱动对话：注入玩家输入 → 调用 LLMClient → 记录历史并返回回复。</summary>
    public async Task<LLMResponse> RespondAsync(NpcCharacter npc, string playerName,
        string playerInput, LLMClient client, CancellationToken ct = default)
    {
        var conv = BuildConversation(npc, playerName);
        Push(playerInput);
        conv.Append(new LLMMessage(LLMRole.User, playerInput));
        var resp = await client.ChatAsync(conv, ct).ConfigureAwait(false);
        if (resp.Success && !string.IsNullOrWhiteSpace(resp.Content))
            PushAssistant(resp.Content);
        return resp;
    }
}

// ──────────────────────────────────────────────────────────────────
// 7. NPC 实体
// ──────────────────────────────────────────────────────────────────

/// <summary>AI 驱动的 NPC 角色：个性/情感/记忆/关系/日程/对话的统一载体。</summary>
public sealed class NpcCharacter
{
    public string Name { get; set; } = "NPC";
    public string Occupation { get; set; } = "普通居民";
    public string Faction { get; set; } = "中立";
    public Vector3 Position { get; set; }
    public float Health { get; set; } = 100f;

    public NpcPersonality Personality { get; set; } = NpcPersonality.Balanced;
    public NpcEmotionState Emotion { get; } = new();
    public NpcMemory Memory { get; } = new();
    public NpcSchedule Schedule { get; set; } = NpcSchedule.Default();
    public NpcDialogue Dialogue { get; } = new();

    private readonly Dictionary<string, NpcRelationship> _relationships = new(StringComparer.OrdinalIgnoreCase);

    public NpcRelationship GetRelationship(string otherName)
    {
        if (!_relationships.TryGetValue(otherName, out var r))
        {
            r = new NpcRelationship();
            _relationships[otherName] = r;
        }
        return r;
    }

    /// <summary>当前日程活动（按游戏内小时）。</summary>
    public NpcActivityKind CurrentActivity(float hour) => Schedule.GetActivity(hour);

    /// <summary>与其他 NPC 社交互动：双向关系变化 + 触发情感 + 记录记忆。</summary>
    public void Interact(NpcCharacter other, float delta = 5f, NpcEmotionType mood = NpcEmotionType.Joy)
    {
        GetRelationship(other.Name).Change(delta);
        other.GetRelationship(Name).Change(delta * 0.8f);
        Emotion.Trigger(mood, 0.3f);
        Memory.Remember(NpcMemoryKind.Encounter, other.Name,
            $"遇到了{other.Name}", importance: 0.6f);
    }

    /// <summary>NPC 每帧更新：情感衰减 + 记忆衰减 + 关系冷却。</summary>
    public void Tick(float deltaSeconds)
    {
        Emotion.Tick(deltaSeconds);
        Memory.Tick(deltaSeconds);
        foreach (var r in _relationships.Values)
            r.Tick(deltaSeconds);
    }
}

// ──────────────────────────────────────────────────────────────────
// 8. NPC 世界
// ──────────────────────────────────────────────────────────────────

/// <summary>NPC 世界：管理所有 NPC 的注册与逐帧 Tick。</summary>
public sealed class NpcWorld
{
    private readonly List<NpcCharacter> _characters = new();
    public IReadOnlyList<NpcCharacter> Characters => _characters;
    public float TimeHours { get; private set; } = 8f; // 游戏内小时

    public NpcCharacter Register(NpcCharacter npc)
    {
        if (npc == null) throw new ArgumentNullException(nameof(npc));
        _characters.Add(npc);
        return npc;
    }

    public bool Remove(NpcCharacter npc) => _characters.Remove(npc);

    public NpcCharacter? Find(string name) =>
        _characters.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>推进世界时间与全部 NPC 的 Tick。</summary>
    public void Tick(float deltaSeconds, float timeScale = 1f)
    {
        TimeHours = (TimeHours + deltaSeconds * timeScale / 3600f) % 24f;
        foreach (var c in _characters)
            c.Tick(deltaSeconds);
    }
}
