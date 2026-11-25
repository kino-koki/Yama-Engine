// ════════════════════════════════════════════════════════════════════════
// NpcRuntimeBeliefs.cs — Round 5：感知 → 信念
// 补的是「瞬时刺激」到「持续认知」之间的缺口：NPC 不该每帧重新"发现"同一个敌人。
// 多源刺激按置信度加权融合、时效衰减、最后已知位置、矛盾抑制，全部确定性。
// ════════════════════════════════════════════════════════════════════════
using System.Numerics;

namespace YamaEngine.NPC.Runtime;

/// <summary>一条持续信念（关于某个主体的当前认知）。</summary>
public sealed class NpcRtBelief
{
    public string Subject = "";
    public string Kind = "";             // threat / ally / resource / rumor / chem
    public Vector3 LastKnownPosition;
    public float Confidence;            // 0..1
    public float Valence;               // -1..1
    public int EvidenceCount;
    public double LastSeen;
    public double FirstSeen;
    public bool IsStale(double now, double ttl) => now - LastSeen > ttl;
    public override string ToString()
        => $"{Kind}:{Subject} conf={Confidence:0.00} val={Valence:0.00} n={EvidenceCount}";
}

/// <summary>
/// 信念库：同一主体的多次感知按「新证据置信度 vs 旧信念」做加权融合，
/// 而不是简单覆盖；矛盾效价互相削弱；过期信念自动降级而非瞬间删除。
/// </summary>
public sealed class NpcRtBeliefStore : INpcRtStateful
{
    private readonly Dictionary<string, Dictionary<string, NpcRtBelief>> _items =
        new(StringComparer.Ordinal);

    public double DefaultTtlSeconds { get; set; } = 12.0;
    public float MinConfidence { get; set; } = 0.03f;
    public float DecayPerSecond { get; set; } = 0.06f;
    public int CapacityPerActor { get; set; } = 32;
    public long TotalObservations { get; private set; }
    public long TotalEvictions { get; private set; }

    private Dictionary<string, NpcRtBelief> Bag(string actorId)
    {
        if (!_items.TryGetValue(actorId, out var d))
        { d = new Dictionary<string, NpcRtBelief>(StringComparer.Ordinal); _items[actorId] = d; }
        return d;
    }

    private static string Key(string subject, string kind) => kind + "|" + subject;

    /// <summary>吸收一条感知；返回融合后的信念。</summary>
    public NpcRtBelief Observe(string actorId, NpcRtPercept p, double now)
    {
        var bag = Bag(actorId);
        var key = Key(p.Subject, p.Kind);
        float evidence = Math.Clamp(p.Intensity, 0f, 1f);
        TotalObservations++;
        if (!bag.TryGetValue(key, out var b))
        {
            b = new NpcRtBelief { Subject = p.Subject, Kind = p.Kind, FirstSeen = now,
                LastSeen = now, LastKnownPosition = p.Position,
                Confidence = evidence, Valence = p.Valence, EvidenceCount = 1 };
            bag[key] = b;
            Enforce(bag);
            return b;
        }
        // 加权融合：新证据越强，位置和效价更新越多
        float w = evidence / (evidence + b.Confidence + 1e-5f);
        b.LastKnownPosition = Vector3.Lerp(b.LastKnownPosition, p.Position, w);
        // 矛盾效价（一个说危险一个说友好）互相削弱置信度
        bool contradictory = MathF.Sign(b.Valence) != MathF.Sign(p.Valence)
                             && MathF.Abs(b.Valence) > 0.2f && MathF.Abs(p.Valence) > 0.2f;
        b.Valence = Math.Clamp(b.Valence * (1f - w) + p.Valence * w, -1f, 1f);
        b.Confidence = Math.Clamp(
            MathF.Max(b.Confidence, evidence) + (contradictory ? -0.15f : 0.08f * evidence), 0f, 1f);
        b.EvidenceCount++;
        b.LastSeen = now;
        return b;
    }

    /// <summary>显式断言一条信念（如从记忆召回、传闻）。</summary>
    public NpcRtBelief Assert(string actorId, string subject, string kind,
        Vector3 position, float confidence, float valence, double now)
        => Observe(actorId, new NpcRtPercept(kind, subject, position, confidence, valence, now), now);

    /// <summary>逐帧衰减；陈旧信念降级，低于阈值移除。</summary>
    public void Tick(double deltaSeconds, double now)
    {
        float k = MathF.Exp(-DecayPerSecond * (float)Math.Max(0.0, deltaSeconds));
        foreach (var bag in _items.Values)
        {
            var dead = new List<string>();
            foreach (var kv in bag)
            {
                var b = kv.Value;
                if (b.IsStale(now, DefaultTtlSeconds)) b.Confidence *= k * 0.5f;
                else b.Confidence *= k;
                if (b.Confidence < MinConfidence) dead.Add(kv.Key);
            }
            foreach (var k2 in dead) { bag.Remove(k2); TotalEvictions++; }
        }
    }

    /// <summary>按置信度排序的当前信念；可按 kind 过滤。</summary>
    public IReadOnlyList<NpcRtBelief> Query(string actorId, string? kind = null)
    {
        if (!_items.TryGetValue(actorId, out var bag)) return Array.Empty<NpcRtBelief>();
        return bag.Values.Where(b => kind is null || b.Kind == kind)
            .OrderByDescending(b => b.Confidence)
            .ThenBy(b => b.Subject, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>最强威胁信念（供逃跑/战术决策）。</summary>
    public NpcRtBelief? TopThreat(string actorId)
        => Query(actorId).FirstOrDefault(b => b.Kind is "threat" or "damage" or "chem");

    private void Enforce(Dictionary<string, NpcRtBelief> bag)
    {
        while (bag.Count > CapacityPerActor)
        {
            string weakest = bag.OrderBy(kv => kv.Value.Confidence)
                .ThenBy(kv => kv.Key, StringComparer.Ordinal).First().Key;
            bag.Remove(weakest); TotalEvictions++;
        }
    }

    public void Capture(NpcRtStateBag bag, string p)
    {
        bag.Put(p + ".obs", TotalObservations); bag.Put(p + ".evict", TotalEvictions);
        int i = 0;
        foreach (var actor in _items.OrderBy(x => x.Key, StringComparer.Ordinal))
            foreach (var kv in actor.Value.OrderBy(x => x.Key, StringComparer.Ordinal))
            {
                string k = p + ".b" + i++ + ".";
                var b = kv.Value;
                bag.Put(k + "actor", actor.Key); bag.Put(k + "subject", b.Subject);
                bag.Put(k + "kind", b.Kind); bag.Put(k + "pos", b.LastKnownPosition);
                bag.Put(k + "conf", b.Confidence); bag.Put(k + "val", b.Valence);
                bag.Put(k + "n", b.EvidenceCount);
                bag.Put(k + "last", b.LastSeen); bag.Put(k + "first", b.FirstSeen);
            }
        bag.Put(p + ".n", i);
    }

    public void Restore(NpcRtStateBag bag, string p)
    {
        _items.Clear();
        TotalObservations = bag.GetLong(p + ".obs"); TotalEvictions = bag.GetLong(p + ".evict");
        int n = bag.GetInt(p + ".n");
        for (int i = 0; i < n; i++)
        {
            string k = p + ".b" + i + ".";
            var b = new NpcRtBelief
            {
                Subject = bag.GetString(k + "subject"), Kind = bag.GetString(k + "kind"),
                LastKnownPosition = bag.GetVector3(k + "pos"),
                Confidence = bag.GetFloat(k + "conf"), Valence = bag.GetFloat(k + "val"),
                EvidenceCount = bag.GetInt(k + "n"), LastSeen = bag.GetDouble(k + "last"),
                FirstSeen = bag.GetDouble(k + "first")
            };
            Bag(bag.GetString(k + "actor"))[Key(b.Subject, b.Kind)] = b;
        }
    }
}
