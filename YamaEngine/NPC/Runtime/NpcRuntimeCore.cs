// ════════════════════════════════════════════════════════════════════════
// NpcRuntimeCore.cs — AI NPC 运行时基础层
// 纯 CPU、确定性、零外部依赖；不修改既有 AI/NPC/渲染/GPU 代码。
// 由 csproj 的 <Compile Include="NPC\**\*.cs" /> 自动纳入，无需改工程文件。
// ════════════════════════════════════════════════════════════════════════
using System.Globalization;
using System.Numerics;

namespace YamaEngine.NPC.Runtime;

public enum NpcRtPhase { Sense, Remember, Motivate, Deliberate, Act, Reflect, Dormant }
public enum NpcRtLod { Full, Reduced, Dormant }
public enum NpcRtNeed { Survival, Safety, Belonging, Esteem, Growth }
public enum NpcRtMemoryKind { Episodic, Semantic, Social, Rumor, Commitment }

/// <summary>行为意图白名单：所有外部（含 LLM）输入必须先过这里。</summary>
public static class NpcRtIntentNames
{
    public const string Idle = "Idle";
    public const string TalkTo = "TalkTo";
    public const string GoTo = "GoTo";
    public const string Flee = "Flee";
    public const string Work = "Work";
    public const string Socialize = "Socialize";
    public const string Quest = "Quest";
    public const string Wander = "Wander";
    public static readonly string[] All =
        { Idle, TalkTo, GoTo, Flee, Work, Socialize, Quest, Wander };
    public static bool IsKnown(string? kind) => kind != null && Array.IndexOf(All, kind) >= 0;
    public static string Normalize(string? kind) => IsKnown(kind) ? kind! : Idle;
}

/// <summary>xorshift64：同种子必得同序列，用于回放与一致性校验。</summary>
public sealed class NpcRtRandom
{
    private ulong _state;
    public NpcRtRandom(ulong seed) => _state = seed == 0UL ? 0x9E3779B97F4A7C15UL : seed;
    public ulong State => _state;
    public void SetState(ulong s) => _state = s;
    public ulong NextUInt64()
    {
        ulong x = _state;
        x ^= x << 13; x ^= x >> 7; x ^= x << 17;
        _state = x;
        return x;
    }
    public double NextDouble() => (NextUInt64() >> 11) * (1.0 / 9007199254740992.0);
    public float NextFloat() => (float)NextDouble();
    public int NextInt(int minInclusive, int maxExclusive)
        => minInclusive + (int)(NextDouble() * Math.Max(0, maxExclusive - minInclusive));
    public float Range(float a, float b) => a + (b - a) * NextFloat();
}

/// <summary>纯文本状态袋：零序列化依赖、顺序稳定、可直接 diff。</summary>
public sealed class NpcRtStateBag
{
    private readonly Dictionary<string, string> _d = new(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, string> Raw => _d;
    public void Put(string k, string v) => _d[k] = v ?? "";
    public void Put(string k, int v) => _d[k] = v.ToString(CultureInfo.InvariantCulture);
    public void Put(string k, long v) => _d[k] = v.ToString(CultureInfo.InvariantCulture);
    public void Put(string k, double v) => _d[k] = v.ToString("R", CultureInfo.InvariantCulture);
    public void Put(string k, float v) => _d[k] = ((double)v).ToString("R", CultureInfo.InvariantCulture);
    public void Put(string k, bool v) => _d[k] = v ? "1" : "0";
    private static string F(float x) => x.ToString("R", CultureInfo.InvariantCulture);
    public void Put(string k, Vector3 v) => _d[k] = F(v.X) + "," + F(v.Y) + "," + F(v.Z);

    public string? Get(string k) => _d.TryGetValue(k, out var v) ? v : null;
    public string GetString(string k, string d = "") => Get(k) ?? d;
    public int GetInt(string k, int d = 0)
        => int.TryParse(Get(k), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : d;
    public long GetLong(string k, long d = 0)
        => long.TryParse(Get(k), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : d;
    public double GetDouble(string k, double d = 0)
        => double.TryParse(Get(k), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : d;
    public float GetFloat(string k, float d = 0)
        => float.TryParse(Get(k), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : d;
    public bool GetBool(string k, bool d = false) => Get(k) is "1" or "true" || (Get(k) == null && d);
    public Vector3 GetVector3(string k, Vector3 d = default)
    {
        var parts = (Get(k) ?? "").Split(',');
        if (parts.Length == 3
            && float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
            && float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)
            && float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
            return new Vector3(x, y, z);
        return d;
    }
    public string Dump() => string.Join("\n",
        _d.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "=" + p.Value));
    public static NpcRtStateBag Parse(string? text)
    {
        var bag = new NpcRtStateBag();
        if (string.IsNullOrEmpty(text)) return bag;
        foreach (var line in text.Split('\n'))
        {
            int i = line.IndexOf('=');
            if (i > 0) bag.Put(line.Substring(0, i), line.Substring(i + 1).TrimEnd('\r'));
        }
        return bag;
    }
}

public interface INpcRtStateful
{
    void Capture(NpcRtStateBag bag, string prefix);
    void Restore(NpcRtStateBag bag, string prefix);
}

/// <summary>运行时 actor：与既有 NpcCharacter 解耦，由适配层双向同步。</summary>
public sealed class NpcRtActor : INpcRtStateful
{
    public string Id;
    public string DisplayName;
    public Vector3 Position;
    public Vector3 Velocity;
    public float Health = 100f;
    public float Valence;                 // 情绪效价 -1..1
    public float Arousal;                 // 唤醒度 0..1
    public NpcRtLod Lod = NpcRtLod.Full;
    public long LastDecisionTick;
    public string CurrentIntent = NpcRtIntentNames.Idle;
    public readonly Dictionary<string, float> Needs = new(StringComparer.OrdinalIgnoreCase);
    public readonly Dictionary<string, float> Traits = new(StringComparer.OrdinalIgnoreCase);

    private static readonly string[] NeedKeys = { "Survival", "Safety", "Belonging", "Esteem", "Growth" };
    private static readonly string[] TraitKeys =
        { "Openness", "Conscientiousness", "Extraversion", "Agreeableness", "Neuroticism" };

    public NpcRtActor(string id, string displayName)
    {
        Id = id; DisplayName = displayName;
        foreach (var k in NeedKeys) Needs[k] = 0.5f;
        foreach (var k in TraitKeys) Traits[k] = 0.5f;
    }
    public float Need(NpcRtNeed n) => Needs[NeedKeys[(int)n]];
    public void SetNeed(NpcRtNeed n, float v) => Needs[NeedKeys[(int)n]] = Math.Clamp(v, 0f, 1f);
    public float Trait(string k) => Traits.TryGetValue(k, out var v) ? v : 0.5f;
    public void SetTrait(string k, float v) => Traits[k] = Math.Clamp(v, 0f, 1f);

    public void Capture(NpcRtStateBag bag, string p)
    {
        bag.Put(p + ".id", Id); bag.Put(p + ".name", DisplayName);
        bag.Put(p + ".pos", Position); bag.Put(p + ".vel", Velocity);
        bag.Put(p + ".hp", Health); bag.Put(p + ".valence", Valence);
        bag.Put(p + ".arousal", Arousal); bag.Put(p + ".lod", (int)Lod);
        bag.Put(p + ".lastTick", LastDecisionTick); bag.Put(p + ".intent", CurrentIntent);
        foreach (var kv in Needs) bag.Put(p + ".need." + kv.Key, kv.Value);
        foreach (var kv in Traits) bag.Put(p + ".trait." + kv.Key, kv.Value);
    }
    public void Restore(NpcRtStateBag bag, string p)
    {
        Id = bag.GetString(p + ".id", Id); DisplayName = bag.GetString(p + ".name", DisplayName);
        Position = bag.GetVector3(p + ".pos"); Velocity = bag.GetVector3(p + ".vel");
        Health = bag.GetFloat(p + ".hp", Health); Valence = bag.GetFloat(p + ".valence");
        Arousal = bag.GetFloat(p + ".arousal"); Lod = (NpcRtLod)bag.GetInt(p + ".lod", 0);
        LastDecisionTick = bag.GetLong(p + ".lastTick");
        CurrentIntent = NpcRtIntentNames.Normalize(bag.GetString(p + ".intent"));
        foreach (var k in NeedKeys) Needs[k] = bag.GetFloat(p + ".need." + k, 0.5f);
        foreach (var k in TraitKeys) Traits[k] = bag.GetFloat(p + ".trait." + k, 0.5f);
    }
}

public readonly struct NpcRtPercept
{
    public readonly string Kind;       // sight / sound / damage / social / chem / threat
    public readonly string Subject;
    public readonly Vector3 Position;
    public readonly float Intensity;   // 0..1
    public readonly float Valence;     // -1..1
    public readonly double Time;
    public NpcRtPercept(string kind, string subject, Vector3 position,
        float intensity, float valence, double time)
    { Kind = kind; Subject = subject; Position = position;
      Intensity = Math.Clamp(intensity, 0f, 1f); Valence = Math.Clamp(valence, -1f, 1f); Time = time; }
}

public sealed class NpcRtCommand
{
    public long Sequence;
    public string ActorId = "";
    public string Kind = NpcRtIntentNames.Idle;
    public string Target = "";
    public Vector3 TargetPosition;
    public float Priority;
    public string Reason = "";
    public string Source = "heuristic";   // llm | heuristic | reflex | scripted
    public override string ToString()
        => $"#{Sequence} {ActorId} {Kind}({Target}) p={Priority:0.00} src={Source}";
}

public readonly struct NpcRtArbitration
{
    public readonly string Kind;
    public readonly string Need;
    public readonly float Score;
    public readonly string Reason;
    public NpcRtArbitration(string kind, string need, float score, string reason)
    { Kind = kind; Need = need; Score = score; Reason = reason; }
}

public sealed class NpcRtBudget
{
    public int MaxFullDecisionsPerTick = 8;
    public int ReducedPeriodTicks = 4;
    public int DormantPeriodTicks = 16;
    public int MaxLlmCallsPerWindow = 30;
    public double LlmWindowSeconds = 60.0;
    public int MaxPromptChars = 4000;
    public int MaxReplyChars = 2000;
    public int MaxRumorHops = 3;
}

/// <summary>宿主适配：把运行时状态读写映射到既有 NPC 实体。</summary>
public interface INpcRtHost
{
    string DescribePersona(string actorId);
    float ReadValence(string actorId);
    float ReadRelationship(string actorId, string otherId);
    string RecallSummary(string actorId, int maxEntries);
    void OnReflect(string actorId, string kind, string reason, float valenceDelta);
}

/// <summary>执行器适配：把仲裁结果交给行为树 / StateTree / NpcBrain。</summary>
public interface INpcRtExecutor
{
    bool Apply(NpcRtCommand command);
    void Tick(string actorId, float deltaSeconds);
    void Stop(string actorId);
}

/// <summary>LLM 端口：由适配层注入，运行时自身不触网。</summary>
public interface INpcRtLlmPort
{
    Task<string> CompleteAsync(string prompt, CancellationToken ct);
}
