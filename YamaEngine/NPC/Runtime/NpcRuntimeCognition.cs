// ════════════════════════════════════════════════════════════════════════
// NpcRuntimeCognition.cs — 语义记忆 / 社会网络 / 需求动机仲裁
// 记忆：多线索加权检索 + 巩固 + 遗忘 + 传闻置信度衰减。
// 社会：关系图 + 平衡三角 + 情绪传染 + 谣言传播。
// 动机：需求层级 × 个性 × 情感 × 关系 × 感知，带迟滞防抖。
// ════════════════════════════════════════════════════════════════════════
namespace YamaEngine.NPC.Runtime;

public sealed class NpcRtMemoryRecord
{
    public string Id = Guid.NewGuid().ToString("N");
    public string OwnerId = "";
    public NpcRtMemoryKind Kind;
    public string Subject = "";
    public string Predicate = "";
    public string Content = "";
    public float Strength = 1f;
    public float Importance = 0.5f;
    public float Valence;
    public float Confidence = 1f;       // 观察=1，转述逐跳衰减
    public string Source = "observed";  // observed / told / inferred
    public double CreatedAt;
    public double LastRecalledAt;
    public int RecallCount;
    public int Hops;
    public bool IsForgotten => Strength <= 0.02f;
}

public sealed class NpcRtMemoryStore : INpcRtStateful
{
    private readonly List<NpcRtMemoryRecord> _records = new();
    private long _nextId;
    public int Capacity { get; set; } = 256;
    public IReadOnlyList<NpcRtMemoryRecord> Records => _records;

    public NpcRtMemoryRecord Remember(string owner, NpcRtMemoryKind kind, string subject,
        string predicate, string content, double now, float importance = 0.5f,
        float valence = 0f, float confidence = 1f, string source = "observed", int hops = 0)
    {
        var r = new NpcRtMemoryRecord
        {
            OwnerId = owner, Kind = kind, Subject = subject, Predicate = predicate,
            Content = content, Importance = Math.Clamp(importance, 0f, 1f),
            Valence = Math.Clamp(valence, -1f, 1f),
            Confidence = Math.Clamp(confidence, 0f, 1f),
            Source = source, Hops = hops, CreatedAt = now, LastRecalledAt = now,
            Id = owner + ":" + (_nextId++).ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
        _records.Add(r); EnforceCapacity();
        return r;
    }

    /// <summary>多线索检索：词面重合 + 强度 + 重要度 + 近因 + 情绪一致性 + 置信度。</summary>
    public IReadOnlyList<NpcRtMemoryRecord> Recall(string owner, string query,
        double now, float actorValence, int maxResults = 5)
    {
        var terms = Tokenize(query);
        return _records
            .Where(r => r.OwnerId == owner && !r.IsForgotten)
            .Select(r => new { R = r, S = Score(r, terms, now, actorValence) })
            .Where(x => x.S > 0.001)
            .OrderByDescending(x => x.S)
            .Take(Math.Max(1, maxResults))
            .Select(x => { x.R.RecallCount++; x.R.LastRecalledAt = now;
                           x.R.Strength = Math.Clamp(x.R.Strength + 0.02f, 0f, 1f); return x.R; })
            .ToList();
    }

    private static double Score(NpcRtMemoryRecord r, HashSet<string> terms, double now, float valence)
    {
        var words = Tokenize(r.Subject + " " + r.Predicate + " " + r.Content);
        double overlap = terms.Count == 0 ? 0.15 : (double)terms.Intersect(words).Count() / terms.Count;
        double ageHours = Math.Max(0.0, now - r.CreatedAt) / 3600.0;
        double recency = Math.Exp(-ageHours / 72.0);
        double mood = 1.0 + 0.25 * (1.0 - Math.Abs(r.Valence - valence));
        return (0.15 + overlap) * (0.25 + 0.75 * r.Strength)
             * (0.4 + 0.6 * r.Importance) * (0.3 + 0.7 * recency)
             * mood * (0.3 + 0.7 * r.Confidence);
    }

    private static HashSet<string> Tokenize(string s)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var buf = new System.Text.StringBuilder();
        foreach (char c in s ?? "")
        {
            if (char.IsLetterOrDigit(c)) buf.Append(char.ToLowerInvariant(c));
            else if (buf.Length > 0) { set.Add(buf.ToString()); buf.Clear(); }
        }
        if (buf.Length > 0) set.Add(buf.ToString());
        return set;
    }

    /// <summary>巩固：反复回忆的条目加固，其余按重要度衰减；超容量淘汰最低价值。</summary>
    public void Consolidate(double deltaSeconds, double now)
    {
        float k = (float)Math.Exp(-0.02 * Math.Max(0.0, deltaSeconds));
        for (int i = _records.Count - 1; i >= 0; i--)
        {
            var r = _records[i];
            float decay = 1f - (1f - k) * (1.5f - r.Importance);
            r.Strength *= Math.Clamp(decay, 0f, 1f);
            if (r.RecallCount >= 3) r.Importance = Math.Clamp(r.Importance + 0.01f, 0f, 1f);
            if (r.IsForgotten) _records.RemoveAt(i);
        }
        EnforceCapacity();
    }

    private void EnforceCapacity()
    {
        while (_records.Count > Capacity)
        {
            int worst = 0; double wv = double.MaxValue;
            for (int i = 0; i < _records.Count; i++)
            {
                double v = _records[i].Strength * _records[i].Importance * _records[i].Confidence;
                if (v < wv) { wv = v; worst = i; }
            }
            _records.RemoveAt(worst);
        }
    }

    /// <summary>谣言传播：置信度与强度逐跳衰减，来源强制标为 told。</summary>
    public NpcRtMemoryRecord? Spread(string fromOwner, string toOwner, NpcRtMemoryRecord src,
        double now, int maxHops, float credulity = 0.5f)
    {
        if (src.Hops >= maxHops || src.Confidence <= 0.05f) return null;
        return Remember(toOwner, NpcRtMemoryKind.Rumor, src.Subject, src.Predicate,
            src.Content, now, src.Importance * 0.9f, src.Valence,
            src.Confidence * (0.55f + 0.35f * credulity), "told", src.Hops + 1);
    }

    public string Summarize(string owner, int max = 4)
    {
        var top = _records.Where(r => r.OwnerId == owner && !r.IsForgotten)
            .OrderByDescending(r => r.Strength * r.Importance * r.Confidence)
            .Take(Math.Max(1, max));
        var parts = top.Select(r => r.Content + (r.Source == "told" ? "（传闻）" : ""));
        return string.Join("；", parts);
    }

    public void Capture(NpcRtStateBag bag, string p)
    {
        var ordered = _records.OrderBy(x => x.Id, StringComparer.Ordinal).ToList();
        bag.Put(p + ".cap", Capacity); bag.Put(p + ".n", ordered.Count); bag.Put(p + ".seq", _nextId);
        for (int i = 0; i < ordered.Count; i++)
        {
            var r = ordered[i]; string k = p + ".r" + i + ".";
            bag.Put(k + "id", r.Id); bag.Put(k + "owner", r.OwnerId);
            bag.Put(k + "kind", (int)r.Kind); bag.Put(k + "subject", r.Subject);
            bag.Put(k + "pred", r.Predicate); bag.Put(k + "content", r.Content);
            bag.Put(k + "str", r.Strength); bag.Put(k + "imp", r.Importance);
            bag.Put(k + "val", r.Valence); bag.Put(k + "conf", r.Confidence);
            bag.Put(k + "src", r.Source); bag.Put(k + "hops", r.Hops);
            bag.Put(k + "created", r.CreatedAt); bag.Put(k + "recalled", r.LastRecalledAt);
            bag.Put(k + "count", r.RecallCount);
        }
    }
    public void Restore(NpcRtStateBag bag, string p)
    {
        _records.Clear(); Capacity = bag.GetInt(p + ".cap", Capacity);
        _nextId = bag.GetLong(p + ".seq");
        int n = bag.GetInt(p + ".n");
        for (int i = 0; i < n; i++)
        {
            string k = p + ".r" + i + ".";
            _records.Add(new NpcRtMemoryRecord
            {
                Id = bag.GetString(k + "id"), OwnerId = bag.GetString(k + "owner"),
                Kind = (NpcRtMemoryKind)bag.GetInt(k + "kind"),
                Subject = bag.GetString(k + "subject"), Predicate = bag.GetString(k + "pred"),
                Content = bag.GetString(k + "content"),
                Strength = bag.GetFloat(k + "str"), Importance = bag.GetFloat(k + "imp"),
                Valence = bag.GetFloat(k + "val"), Confidence = bag.GetFloat(k + "conf"),
                Source = bag.GetString(k + "src", "observed"), Hops = bag.GetInt(k + "hops"),
                CreatedAt = bag.GetDouble(k + "created"), LastRecalledAt = bag.GetDouble(k + "recalled"),
                RecallCount = bag.GetInt(k + "count")
            });
        }
    }
}

// ───────────────────────────── 社会网络 ─────────────────────────────

public sealed class NpcRtSocialEdge
{
    public float Affinity;      // -100..100
    public float Trust;         // 0..1
    public float Familiarity;   // 0..1
    public double LastInteraction;
}

public sealed class NpcRtSocialGraph : INpcRtStateful
{
    private readonly Dictionary<string, NpcRtSocialEdge> _edges = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _squad = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, NpcRtSocialEdge> Edges => _edges;

    private static string Key(string a, string b) => string.Compare(a, b, StringComparison.Ordinal) < 0
        ? a + "|" + b : b + "|" + a;
    public void SetSquad(string actorId, string squadId) => _squad[actorId] = squadId;
    public string SquadOf(string actorId) => _squad.TryGetValue(actorId, out var s) ? s : "default";

    public NpcRtSocialEdge Edge(string a, string b)
    {
        var k = Key(a, b);
        if (!_edges.TryGetValue(k, out var e)) { e = new NpcRtSocialEdge { Trust = 0.5f }; _edges[k] = e; }
        return e;
    }
    public void Interact(string a, string b, float affinityDelta, double now)
    {
        var e = Edge(a, b);
        e.Affinity = Math.Clamp(e.Affinity + affinityDelta, -100f, 100f);
        e.Familiarity = Math.Clamp(e.Familiarity + 0.05f, 0f, 1f);
        e.Trust = Math.Clamp(e.Trust + affinityDelta * 0.002f, 0f, 1f);
        e.LastInteraction = now;
    }

    /// <summary>平衡三角：敌友关系不一致产生张力，返回每个 actor 的张力值。</summary>
    public Dictionary<string, float> Tension(IEnumerable<string> actors)
    {
        var list = actors.ToList(); var result = list.ToDictionary(a => a, _ => 0f);
        for (int i = 0; i < list.Count; i++)
            for (int j = i + 1; j < list.Count; j++)
                for (int k = j + 1; k < list.Count; k++)
                {
                    float ab = Math.Sign(Edge(list[i], list[j]).Affinity);
                    float bc = Math.Sign(Edge(list[j], list[k]).Affinity);
                    float ac = Math.Sign(Edge(list[i], list[k]).Affinity);
                    if (ab * bc * ac < 0)
                    {
                        result[list[i]] += 1f; result[list[j]] += 1f; result[list[k]] += 1f;
                    }
                }
        return result;
    }

    /// <summary>情绪传染：按信任 × 熟悉度扩散，返回本次收到的效价增量。</summary>
    public Dictionary<string, float> Contagion(string source, float sourceValence,
        IEnumerable<string> actors, float strength = 0.15f)
    {
        var result = new Dictionary<string, float>(StringComparer.Ordinal);
        foreach (var a in actors)
        {
            if (a == source) continue;
            var e = Edge(source, a);
            float delta = strength * sourceValence * (0.3f + 0.7f * e.Trust) * e.Familiarity;
            if (Math.Abs(delta) > 1e-5f) result[a] = delta;
        }
        return result;
    }

    public IReadOnlyList<string> Allies(string actor, IEnumerable<string> actors)
        => actors.Where(a => a != actor && Edge(actor, a).Affinity > 25f).ToList();
    public IReadOnlyList<string> Rivals(string actor, IEnumerable<string> actors)
        => actors.Where(a => a != actor && Edge(actor, a).Affinity < -25f).ToList();

    public void Capture(NpcRtStateBag bag, string p)
    {
        int i = 0;
        foreach (var kv in _edges.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            string k = p + ".e" + i++ + ".";
            bag.Put(k + "key", kv.Key); bag.Put(k + "aff", kv.Value.Affinity);
            bag.Put(k + "trust", kv.Value.Trust); bag.Put(k + "fam", kv.Value.Familiarity);
            bag.Put(k + "last", kv.Value.LastInteraction);
        }
        bag.Put(p + ".n", i);
        int j = 0;
        foreach (var kv in _squad) { bag.Put(p + ".s" + j + ".a", kv.Key); bag.Put(p + ".s" + j + ".s", kv.Value); j++; }
        bag.Put(p + ".sn", j);
    }
    public void Restore(NpcRtStateBag bag, string p)
    {
        _edges.Clear(); _squad.Clear();
        int n = bag.GetInt(p + ".n");
        for (int i = 0; i < n; i++)
        {
            string k = p + ".e" + i + ".";
            _edges[bag.GetString(k + "key")] = new NpcRtSocialEdge
            { Affinity = bag.GetFloat(k + "aff"), Trust = bag.GetFloat(k + "trust"),
              Familiarity = bag.GetFloat(k + "fam"), LastInteraction = bag.GetDouble(k + "last") };
        }
        int sn = bag.GetInt(p + ".sn");
        for (int i = 0; i < sn; i++) _squad[bag.GetString(p + ".s" + i + ".a")] = bag.GetString(p + ".s" + i + ".s");
    }
}

// ───────────────────────────── 动机仲裁 ─────────────────────────────

public sealed class NpcRtCandidate
{
    public string Kind = NpcRtIntentNames.Idle;
    public NpcRtNeed Need = NpcRtNeed.Growth;
    public float BaseUtility = 0.4f;
    public string RequiredFact = "";      // 黑板键，为空表示无前置
    public float MinHealth = 0f;
}

public sealed class NpcRtMotivationArbiter
{
    public float HysteresisBonus = 0.15f;   // 当前行为惯性，防抖
    public float SwitchMargin = 0.08f;      // 切换所需超出量
    public List<NpcRtCandidate> Candidates { get; } = DefaultCandidates();

    public static List<NpcRtCandidate> DefaultCandidates() => new()
    {
        new() { Kind = NpcRtIntentNames.Flee,      Need = NpcRtNeed.Survival,  BaseUtility = 1.00f, RequiredFact = "threat" },
        new() { Kind = NpcRtIntentNames.TalkTo,    Need = NpcRtNeed.Belonging, BaseUtility = 0.62f, RequiredFact = "interlocutor" },
        new() { Kind = NpcRtIntentNames.Socialize, Need = NpcRtNeed.Belonging, BaseUtility = 0.55f },
        new() { Kind = NpcRtIntentNames.Quest,     Need = NpcRtNeed.Esteem,    BaseUtility = 0.58f },
        new() { Kind = NpcRtIntentNames.Work,      Need = NpcRtNeed.Safety,    BaseUtility = 0.52f },
        new() { Kind = NpcRtIntentNames.GoTo,      Need = NpcRtNeed.Safety,    BaseUtility = 0.45f },
        new() { Kind = NpcRtIntentNames.Wander,    Need = NpcRtNeed.Growth,    BaseUtility = 0.35f },
        new() { Kind = NpcRtIntentNames.Idle,      Need = NpcRtNeed.Growth,    BaseUtility = 0.20f }
    };

    /// <summary>需求缺口 × 个性 × 情感 × 关系 × 感知 → 排序后的候选。</summary>
    public IReadOnlyList<NpcRtArbitration> Evaluate(NpcRtActor actor,
        NpcRuntimeBlackboard board, string squadId, INpcRtHost? host,
        IReadOnlyList<NpcRtPercept> percepts)
    {
        float threat = percepts.Where(p => p.Kind is "threat" or "damage").Sum(p => p.Intensity);
        float socialCue = percepts.Where(p => p.Kind == "social").Sum(p => p.Intensity);
        float chemCue = percepts.Where(p => p.Kind == "chem").Sum(p => p.Intensity);
        float relationship = host?.ReadRelationship(actor.Id, "玩家") ?? 0f;
        float persona = host is null ? 0f : Math.Clamp(host.ReadValence(actor.Id), -1f, 1f);
        float neuro = actor.Trait("Neuroticism");
        float extra = actor.Trait("Extraversion");
        float agree = actor.Trait("Agreeableness");
        float hp = Math.Clamp(actor.Health / 100f, 0f, 1f);

        var result = new List<NpcRtArbitration>();
        foreach (var c in Candidates)
        {
            if (!string.IsNullOrEmpty(c.RequiredFact))
            {
                string? fact = board.Get(actor.Id, squadId, c.RequiredFact);
                if (c.RequiredFact == "threat" && threat <= 0f && fact is null) continue;
                if (c.RequiredFact == "interlocutor" && fact is null && socialCue <= 0f) continue;
                if (fact is null && c.RequiredFact != "threat" && c.RequiredFact != "interlocutor") continue;
            }
            float needGap = 1f - actor.Need(c.Need);
            float score = c.BaseUtility * (0.35f + 0.65f * needGap);
            score *= c.Need switch
            {
                NpcRtNeed.Survival => 0.5f + 1.2f * (1f - hp) + 0.8f * threat + 0.4f * neuro,
                NpcRtNeed.Safety => 0.8f + 0.4f * neuro + 0.3f * chemCue,
                NpcRtNeed.Belonging => 0.6f + 0.8f * extra + 0.4f * agree + 0.3f * socialCue
                                       + 0.002f * relationship,
                NpcRtNeed.Esteem => 0.7f + 0.5f * (1f - agree),
                _ => 0.6f + 0.7f * actor.Trait("Openness")
            };
            score += 0.08f * persona;
            score += c.Kind == actor.CurrentIntent ? HysteresisBonus : -SwitchMargin;
            score = Math.Clamp(score, 0f, 2f);
            result.Add(new NpcRtArbitration(c.Kind, c.Need.ToString(), score,
                $"need={c.Need} gap={needGap:0.00} hp={hp:0.00}"));
        }
        return result.OrderByDescending(r => r.Score).ToList();
    }

    /// <summary>需求随时间回升（未满足则越来越强），并返回饥饿式衰减量。</summary>
    public void TickNeeds(NpcRtActor actor, float deltaSeconds)
    {
        float dt = Math.Max(0f, deltaSeconds);
        for (int i = 0; i < 5; i++)
        {
            var n = (NpcRtNeed)i;
            float rate = n switch { NpcRtNeed.Survival => 0.010f, NpcRtNeed.Safety => 0.006f,
                NpcRtNeed.Belonging => 0.004f, NpcRtNeed.Esteem => 0.003f, _ => 0.002f };
            actor.SetNeed(n, actor.Need(n) * (1f - rate * dt));
        }
    }
}
