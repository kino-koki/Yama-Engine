// ════════════════════════════════════════════════════════════════════════
// NpcRuntimeScheduler.cs — 感知→记忆→动机→决策→执行→反馈 统一闭环
// 关键设计：LLM 异步结果经队列注入，Tick 本身永远同步、确定性、可快照回放。
// LOD 时间片 + 决策预算 + 命令日志 + 全局快照，支持大规模 NPC 而不阻塞主线程。
// ════════════════════════════════════════════════════════════════════════
using System.Numerics;

namespace YamaEngine.NPC.Runtime;

public sealed class NpcRuntimeWorld : INpcRtStateful
{
    private readonly Dictionary<string, NpcRtActor> _actors = new(StringComparer.Ordinal);
    private readonly List<NpcRtPercept> _percepts = new();
    private readonly List<NpcRtCommand> _commands = new();
    private readonly Dictionary<string, string> _llmResults = new(StringComparer.Ordinal);
    private readonly List<string> _events = new();
    private long _seq;

    public readonly NpcRtBudget Budget;
    public readonly NpcRuntimeBlackboard Board = new();
    public readonly NpcRtMemoryStore Memory = new();
    public readonly NpcRtSocialGraph Social = new();
    public readonly NpcRtMotivationArbiter Arbiter = new();
    public readonly NpcRuntimeGuard Guard;
    public readonly NpcRtRandom Random;
    public readonly NpcRtFairScheduler Fairness = new();
    public readonly NpcRtBeliefStore Beliefs = new();

    public INpcRtHost? Host { get; set; }
    public INpcRtExecutor? Executor { get; set; }
    public INpcRtLlmPort? Llm { get; set; }
    public Vector3 FocusPosition { get; set; }
    public float LodNearDistance { get; set; } = 40f;
    public float LodFarDistance { get; set; } = 120f;
    public double TimeSeconds { get; private set; }
    public long TickIndex { get; private set; }
    public float CurrentHour { get; set; } = 8f;

    public IReadOnlyDictionary<string, NpcRtActor> Actors => _actors;
    public IReadOnlyList<NpcRtCommand> Commands => _commands;
    public IReadOnlyList<string> Events => _events;
    public int PendingLlmResults => _llmResults.Count;

    public NpcRuntimeWorld(ulong seed = 20261123UL, NpcRtBudget? budget = null)
    {
        Budget = budget ?? new NpcRtBudget();
        Random = new NpcRtRandom(seed);
        Guard = new NpcRuntimeGuard(Budget);
    }

    public NpcRtActor Register(string id, string displayName)
    {
        var a = new NpcRtActor(id, displayName);
        _actors[id] = a;
        Social.SetSquad(id, "default");
        return a;
    }
    public bool Remove(string id) => _actors.Remove(id);
    public NpcRtActor? Find(string id) => _actors.TryGetValue(id, out var a) ? a : null;

    public void PushPercept(NpcRtPercept percept) => _percepts.Add(percept);

    /// <summary>由外部异步驱动：把 LLM 回复灌入队列，绝不破坏 Tick 的确定性。</summary>
    public async Task<int> PumpLlmAsync(CancellationToken ct = default)
    {
        if (Llm is null) return 0;
        int n = 0;
        foreach (var a in _actors.Values.OrderBy(x => x.Id, StringComparer.Ordinal))
        {
            if (!ShouldDecide(a) || !Guard.TryConsumeBudget(TimeSeconds)) continue;
            string memories = Memory.Summarize(a.Id, 4);
            var recent = RecentPercepts(a, 30f);
            string prompt = Guard.BuildPrompt(a, Host, Board, Social.SquadOf(a.Id), recent, memories);
            try
            {
                string reply = await Llm.CompleteAsync(prompt, ct).ConfigureAwait(false);
                _llmResults[a.Id] = reply ?? "";
                n++;
            }
            catch (OperationCanceledException) { break; }
            catch (Exception) { _events.Add("llm_error " + a.Id); }
        }
        return n;
    }

    private bool ShouldDecide(NpcRtActor a)
    {
        int period = a.Lod switch
        {
            NpcRtLod.Full => 1,
            NpcRtLod.Reduced => Math.Max(1, Budget.ReducedPeriodTicks),
            _ => Math.Max(1, Budget.DormantPeriodTicks)
        };
        return TickIndex - a.LastDecisionTick >= period;
    }

    private void UpdateLods()
    {
        foreach (var a in _actors.Values)
        {
            float d = Vector3.Distance(a.Position, FocusPosition);
            a.Lod = d <= LodNearDistance ? NpcRtLod.Full
                  : d <= LodFarDistance ? NpcRtLod.Reduced : NpcRtLod.Dormant;
        }
    }

    private IReadOnlyList<NpcRtPercept> RecentPercepts(NpcRtActor a, float radius)
        => _percepts.Where(p => Vector3.Distance(p.Position, a.Position) <= radius
                                && TimeSeconds - p.Time <= 2.0).ToList();

    /// <summary>固定步推进。同种子 + 同快照 + 同 dt 序列 ⇒ 完全一致的日志与状态。</summary>
    public void Tick(float deltaSeconds)
    {
        float dt = Math.Clamp(deltaSeconds, 0f, 0.25f);
        TickIndex++;
        TimeSeconds += dt;
        CurrentHour = (float)((CurrentHour + dt / 3600.0) % 24.0);
        Board.Advance(TimeSeconds);
        Board.Sweep();
        Memory.Consolidate(dt, TimeSeconds);
        UpdateLods();
        foreach (var a in _actors.Values) Arbiter.TickNeeds(a, dt);
        Beliefs.Tick(dt, TimeSeconds);

        // 感知对所有 actor 全量执行（不能被预算裁剪）；决策名额由公平调度分配，
        // 避免 actor 数 > 单帧预算时 id 靠后的 NPC 永久饿死。
        var all = _actors.Values.OrderBy(x => x.Id, StringComparer.Ordinal).ToList();
        var perceptCache = new Dictionary<string, IReadOnlyList<NpcRtPercept>>(StringComparer.Ordinal);
        foreach (var a in all)
        {
            var ps = RecentPercepts(a, 30f);
            perceptCache[a.Id] = ps;
            Sense(a, ps);
        }
        var eligible = all.Where(ShouldDecide).ToList();
        foreach (var a in Fairness.Select(eligible, TickIndex, Budget.MaxFullDecisionsPerTick))
        {
            a.LastDecisionTick = TickIndex;
            Deliberate(a, perceptCache[a.Id]);
        }

        // 执行器逐帧推进（移动/行为树 tick 由适配层负责）
        if (Executor is not null)
            foreach (var a in _actors.Values) Executor.Tick(a.Id, dt);

        if (TickIndex % 8 == 0) SocialStep();
        _percepts.RemoveAll(p => TimeSeconds - p.Time > 5.0);
        if (_commands.Count > 4096) _commands.RemoveRange(0, _commands.Count - 4096);
    }

    private void Sense(NpcRtActor a, IReadOnlyList<NpcRtPercept> percepts)
    {
        float threat = 0f, social = 0f, chem = 0f;
        foreach (var p in percepts)
        {
            if (p.Kind is "threat" or "damage") threat = MathF.Max(threat, p.Intensity);
            else if (p.Kind == "social") social = MathF.Max(social, p.Intensity);
            else if (p.Kind == "chem") chem = MathF.Max(chem, p.Intensity);
            a.Valence = Math.Clamp(a.Valence + p.Valence * p.Intensity * 0.05f, -1f, 1f);
            Beliefs.Observe(a.Id, p, TimeSeconds);
            if (p.Intensity >= 0.7f)
                Memory.Remember(a.Id, NpcRtMemoryKind.Episodic, p.Subject, p.Kind,
                    p.Kind + ":" + p.Subject, TimeSeconds, p.Intensity, p.Valence);
        }
        if (threat > 0f) Board.Set(NpcRtScope.Actor, a.Id, "threat", "1", 1.5, "sense");
        else Board.Remove(NpcRtScope.Actor, a.Id, "threat");
        if (social > 0f) Board.Set(NpcRtScope.Actor, a.Id, "interlocutor", "1", 2.0, "sense");
        if (chem > 0f) Board.Set(NpcRtScope.World, "", "chem_alert", "1", 3.0, "sense");
    }

    private void Deliberate(NpcRtActor a, IReadOnlyList<NpcRtPercept> percepts)
    {
        var ranked = Arbiter.Evaluate(a, Board, Social.SquadOf(a.Id), Host, percepts);
        NpcRtGuardResult decision;

        if (_llmResults.TryGetValue(a.Id, out var raw))
        {
            _llmResults.Remove(a.Id);
            decision = Guard.Validate(raw);
            if (!decision.Accepted)
            {
                _events.Add("guard_reject " + a.Id + " " + decision.Audit);
                decision = NpcRuntimeGuard.Heuristic(a, ranked, NearestThreat(a, percepts), Random);
            }
        }
        else decision = NpcRuntimeGuard.Heuristic(a, ranked, NearestThreat(a, percepts), Random);

        var cmd = new NpcRtCommand
        {
            Sequence = ++_seq, ActorId = a.Id, Kind = decision.Kind,
            Target = decision.Target, TargetPosition = decision.TargetPosition,
            Priority = decision.Priority, Reason = decision.Reason, Source = decision.Source
        };
        _commands.Add(cmd);
        a.CurrentIntent = cmd.Kind;
        bool applied = Executor?.Apply(cmd) ?? false;
        if (!applied) _events.Add("executor_missing " + a.Id);
        Reflect(a, cmd, percepts);
    }

    private static NpcRtPercept? NearestThreat(NpcRtActor a, IReadOnlyList<NpcRtPercept> ps)
    {
        NpcRtPercept? best = null; float bd = float.MaxValue;
        foreach (var p in ps)
        {
            if (p.Kind is not ("threat" or "damage")) continue;
            float d = Vector3.Distance(a.Position, p.Position);
            if (d < bd) { bd = d; best = p; }
        }
        return best;
    }

    private void Reflect(NpcRtActor a, NpcRtCommand cmd, IReadOnlyList<NpcRtPercept> percepts)
    {
        float valenceDelta = cmd.Kind switch
        {
            NpcRtIntentNames.Flee => -0.08f,
            NpcRtIntentNames.Socialize or NpcRtIntentNames.TalkTo => 0.06f,
            NpcRtIntentNames.Quest => 0.04f,
            _ => 0f
        };
        a.Valence = Math.Clamp(a.Valence + valenceDelta, -1f, 1f);
        if (cmd.Kind is NpcRtIntentNames.Socialize or NpcRtIntentNames.TalkTo)
            a.SetNeed(NpcRtNeed.Belonging, a.Need(NpcRtNeed.Belonging) + 0.12f);
        if (cmd.Kind == NpcRtIntentNames.Flee)
            a.SetNeed(NpcRtNeed.Safety, a.Need(NpcRtNeed.Safety) + 0.08f);
        Memory.Remember(a.Id, NpcRtMemoryKind.Commitment, cmd.Target, cmd.Kind,
            cmd.Kind + "(" + cmd.Reason + ")", TimeSeconds, cmd.Priority, valenceDelta);
        Host?.OnReflect(a.Id, cmd.Kind, cmd.Reason, valenceDelta);
        Board.Set(NpcRtScope.Actor, a.Id, "last_intent", cmd.Kind, 0, "reflect");
        Board.Set(NpcRtScope.Actor, a.Id, "last_priority",
            cmd.Priority.ToString("R", System.Globalization.CultureInfo.InvariantCulture), 0, "reflect");
    }

    private void SocialStep()
    {
        var ids = _actors.Keys.OrderBy(x => x, StringComparer.Ordinal).ToList();
        foreach (var a in _actors.Values)
        {
            if (Math.Abs(a.Valence) < 0.2f) continue;
            foreach (var kv in Social.Contagion(a.Id, a.Valence, ids))
                if (_actors.TryGetValue(kv.Key, out var other))
                    other.Valence = Math.Clamp(other.Valence + kv.Value, -1f, 1f);
        }
        // 谣言：把高置信度、高强度的传闻向信任度高的邻居扩散一跳
        foreach (var r in Memory.Records.Where(x => x.Kind == NpcRtMemoryKind.Rumor
                                                  && x.Strength > 0.6f && x.Hops < Budget.MaxRumorHops).Take(8).ToList())
        {
            foreach (var other in ids.Where(x => x != r.OwnerId))
            {
                float trust = Social.Edge(r.OwnerId, other).Trust;
                if (trust < 0.55f) continue;
                Memory.Spread(r.OwnerId, other, r, TimeSeconds, Budget.MaxRumorHops, trust);
            }
        }
        foreach (var kv in Social.Tension(ids))
            if (_actors.TryGetValue(kv.Key, out var a) && kv.Value > 0f)
                a.Arousal = Math.Clamp(a.Arousal + kv.Value * 0.01f, 0f, 1f);
    }

    /// <summary>确定性自检：从同一快照跑两次，比对状态指纹。</summary>
    public static bool VerifyDeterminism(string snapshot, float dt, int ticks)
    {
        string Run()
        {
            var w = new NpcRuntimeWorld();
            w.Restore(snapshot);
            for (int i = 0; i < ticks; i++) w.Tick(dt);
            return w.Snapshot();
        }
        return string.Equals(Run(), Run(), StringComparison.Ordinal);
    }

    public string Snapshot()
    {
        var bag = new NpcRtStateBag();
        bag.Put("v", 1); bag.Put("time", TimeSeconds); bag.Put("tick", TickIndex);
        bag.Put("hour", CurrentHour); bag.Put("rng", Random.State.ToString(
            System.Globalization.CultureInfo.InvariantCulture));
        bag.Put("focus", FocusPosition); bag.Put("seq", _seq);
        bag.Put("actors", _actors.Count);
        int i = 0;
        foreach (var a in _actors.Values.OrderBy(x => x.Id, StringComparer.Ordinal)) a.Capture(bag, "a" + i++);
        Board.Capture(bag, "board"); Memory.Capture(bag, "mem");
        Social.Capture(bag, "social"); Guard.Capture(bag, "guard");
        Beliefs.Capture(bag, "beliefs");
        bag.Put("percepts", _percepts.Count);
        for (int k = 0; k < _percepts.Count; k++)
        {
            var p = _percepts[k]; string b = "p" + k + ".";
            bag.Put(b + "kind", p.Kind); bag.Put(b + "subject", p.Subject);
            bag.Put(b + "pos", p.Position); bag.Put(b + "int", p.Intensity);
            bag.Put(b + "val", p.Valence); bag.Put(b + "time", p.Time);
        }
        bag.Put("llmResults", _llmResults.Count);
        int r = 0;
        foreach (var kv in _llmResults.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            bag.Put("lr" + r + ".k", kv.Key);
            bag.Put("lr" + r + ".v", Convert.ToBase64String(
                System.Text.Encoding.UTF8.GetBytes(kv.Value)));
            r++;
        }
        return bag.Dump();
    }

    public void Restore(string snapshot)
    {
        var bag = NpcRtStateBag.Parse(snapshot);
        TimeSeconds = bag.GetDouble("time"); TickIndex = bag.GetLong("tick");
        _seq = bag.GetLong("seq");
        CurrentHour = bag.GetFloat("hour", 8f);
        if (ulong.TryParse(bag.GetString("rng"), System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var s)) Random.SetState(s);
        FocusPosition = bag.GetVector3("focus");
        _actors.Clear(); _commands.Clear(); _percepts.Clear(); _llmResults.Clear(); _events.Clear();
        int n = bag.GetInt("actors");
        for (int i = 0; i < n; i++)
        {
            var a = new NpcRtActor("", "");
            a.Restore(bag, "a" + i);
            _actors[a.Id] = a;
        }
        Board.Restore(bag, "board"); Memory.Restore(bag, "mem");
        Social.Restore(bag, "social"); Guard.Restore(bag, "guard");
        Beliefs.Restore(bag, "beliefs");
        int pc = bag.GetInt("percepts");
        for (int k = 0; k < pc; k++)
        {
            string b = "p" + k + ".";
            _percepts.Add(new NpcRtPercept(bag.GetString(b + "kind"),
                bag.GetString(b + "subject"), bag.GetVector3(b + "pos"),
                bag.GetFloat(b + "int"), bag.GetFloat(b + "val"), bag.GetDouble(b + "time")));
        }
        int lc = bag.GetInt("llmResults");
        for (int k = 0; k < lc; k++)
        {
            string raw = bag.GetString("lr" + k + ".v");
            string value;
            try { value = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(raw)); }
            catch (FormatException) { value = raw; }   // 兼容早期未编码快照
            _llmResults[bag.GetString("lr" + k + ".k")] = value;
        }
    }

    // INpcRtStateful 显式实现：便于把 world 挂进更大的存档系统
    public void Capture(NpcRtStateBag bag, string prefix)
    {
        foreach (var line in Snapshot().Split('\n'))
        {
            int i = line.IndexOf('=');
            if (i > 0) bag.Put(prefix + "." + line.Substring(0, i), line.Substring(i + 1));
        }
    }
    public void Restore(NpcRtStateBag bag, string prefix)
    {
        var inner = new NpcRtStateBag();
        foreach (var kv in bag.Raw)
            if (kv.Key.StartsWith(prefix + ".", StringComparison.Ordinal))
                inner.Put(kv.Key.Substring(prefix.Length + 1), kv.Value);
        Restore(inner.Dump());
    }
}
