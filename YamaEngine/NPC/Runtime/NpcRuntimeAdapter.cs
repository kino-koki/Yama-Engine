// ════════════════════════════════════════════════════════════════════════
// NpcRuntimeAdapter.cs — 唯一与既有实现耦合的文件
// 若明日新版调整了 NpcCharacter / NpcBrain / LLMClient 的签名，只需改本文件。
// 其余运行时代码完全自包含，不依赖任何既有 AI/NPC 具体类型。
// ════════════════════════════════════════════════════════════════════════
using System.Numerics;
using YamaEngine.AI;
using YamaEngine.NPC;

namespace YamaEngine.NPC.Runtime;

/// <summary>把运行时的 actorId 映射到既有 NpcCharacter。</summary>
public sealed class NpcCharacterHost : INpcRtHost
{
    private readonly NpcWorld _world;
    private readonly Dictionary<string, NpcCharacter> _bind = new(StringComparer.Ordinal);
    public float Hour { get; set; } = 8f;

    public NpcCharacterHost(NpcWorld world) { _world = world; }
    public void Bind(string actorId, NpcCharacter npc) => _bind[actorId] = npc;

    private NpcCharacter? Resolve(string actorId)
        => _bind.TryGetValue(actorId, out var n) ? n : _world.Find(actorId);

    public string DescribePersona(string actorId)
    {
        var n = Resolve(actorId);
        if (n is null) return actorId;
        return $"{n.Name}（{n.Occupation}，阵营{n.Faction}），性格：{n.Personality.Describe()}，"
             + $"心情：{NpcEmotionState.NameOf(n.Emotion.Dominant)}，"
             + $"日程：{NpcSchedule.NameOf(n.CurrentActivity(Hour))}";
    }

    public float ReadValence(string actorId)
    {
        var n = Resolve(actorId);
        if (n is null) return 0f;
        var e = n.Emotion.Dominant;
        bool negative = e is NpcEmotionType.Fear or NpcEmotionType.Anger
            or NpcEmotionType.Sadness or NpcEmotionType.Disgust;
        return (negative ? -1f : 1f) * n.Emotion.DominantIntensity;
    }

    public float ReadRelationship(string actorId, string otherId)
        => Resolve(actorId)?.GetRelationship(otherId).Value ?? 0f;

    public string RecallSummary(string actorId, int maxEntries)
        => Resolve(actorId)?.Memory.Summarize(maxEntries) ?? "暂无";

    public void OnReflect(string actorId, string kind, string reason, float valenceDelta)
    {
        var n = Resolve(actorId);
        if (n is null) return;
        var mood = valenceDelta >= 0f ? NpcEmotionType.Joy : NpcEmotionType.Fear;
        n.Emotion.Trigger(mood, Math.Clamp(Math.Abs(valenceDelta) * 4f, 0f, 1f));
        n.Memory.Remember(NpcMemoryKind.Event, kind, reason,
            importance: Math.Clamp(Math.Abs(valenceDelta), 0.2f, 1f));
        if (Math.Abs(valenceDelta) > 0.02f)
            n.GetRelationship("玩家").Change(valenceDelta * 20f);
    }
}

/// <summary>把仲裁出的命令交给既有 NpcBrain 执行（GoTo / Flee / TalkTo 等）。</summary>
public sealed class NpcBrainExecutor : INpcRtExecutor
{
    private readonly Dictionary<string, NpcBrain> _brains = new(StringComparer.Ordinal);
    private readonly Dictionary<string, NpcCharacter> _chars = new(StringComparer.Ordinal);
    public Action<string, Vector3>? OnPositionChanged { get; set; }

    public void Bind(string actorId, NpcCharacter npc, float moveSpeed = 3f)
    {
        _chars[actorId] = npc;
        _brains[actorId] = new NpcBrain(npc, moveSpeed);
    }

    public bool Apply(NpcRtCommand command)
    {
        if (!_brains.TryGetValue(command.ActorId, out var brain)) return false;
        brain.ApplyIntent(new NpcIntent
        {
            Kind = NpcRtIntentNames.Normalize(command.Kind),
            Target = command.Target,
            X = command.TargetPosition.X,
            Z = command.TargetPosition.Z,
            Priority = command.Priority,
            Reason = command.Reason
        });
        return true;
    }

    public void Tick(string actorId, float deltaSeconds)
    {
        if (!_brains.TryGetValue(actorId, out var brain)) return;
        brain.Tick(deltaSeconds);
        if (_chars.TryGetValue(actorId, out var npc)) OnPositionChanged?.Invoke(actorId, npc.Position);
    }

    public void Stop(string actorId)
    {
        if (_brains.TryGetValue(actorId, out var brain)) brain.Stop();
    }
}

/// <summary>把既有 LLMClient 适配成运行时端口；不接网络时留空即走确定性启发式。</summary>
public sealed class NpcRuntimeLlmAdapter : INpcRtLlmPort
{
    private readonly LLMClient _client;
    public NpcRuntimeLlmAdapter(LLMClient client) { _client = client; }

    public async Task<string> CompleteAsync(string prompt, CancellationToken ct)
    {
        var r = await _client.CompleteAsync(prompt, ct).ConfigureAwait(false);
        return r.Success ? r.Content ?? "" : "";
    }
}

/// <summary>三行接入的装配工厂。</summary>
public static class NpcRuntimeBridge
{
    public static NpcRuntimeWorld Create(NpcWorld world, IEnumerable<NpcCharacter> characters,
        ulong seed = 20261123UL, LLMClient? llm = null)
    {
        var rt = new NpcRuntimeWorld(seed);
        var host = new NpcCharacterHost(world);
        var exec = new NpcBrainExecutor();
        foreach (var n in characters)
        {
            var a = rt.Register(n.Name, n.Name);
            a.Position = n.Position;
            host.Bind(n.Name, n);
            exec.Bind(n.Name, n);
        }
        exec.OnPositionChanged = (id, pos) =>
        {
            if (rt.Find(id) is { } a) a.Position = pos;
        };
        rt.Host = host;
        rt.Executor = exec;
        if (llm is not null) rt.Llm = new NpcRuntimeLlmAdapter(llm);
        return rt;
    }
}
