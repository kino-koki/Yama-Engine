// ════════════════════════════════════════════════════════════════════════
// SimulationCoordinator.cs — Round 7：把 NPC 心智与物理化学世界串成同一个确定性时钟
// 放在 NPC/Runtime/ 是因为 csproj 的 NPC\** 通配自动纳入；命名空间保持本层。
// 双向桥：
//   物理事件(burst/ignition/fracture/reaction) → NPC 感知(threat/chem/sound)
//   NPC 热源/伤害请求队列 → 物理热场（非有限值一律丢弃并计数）
// 固定步、事件排序稳定、快照包含两个子世界 + 桥接统计，可逐字回放。
// ════════════════════════════════════════════════════════════════════════
using System.Numerics;
using System.Text;
using YamaEngine.Physics.Reality;

namespace YamaEngine.NPC.Runtime;

public readonly struct NpcHeatRequest
{
    public readonly int Cell;
    public readonly double Joules;
    public readonly string ActorId;
    public NpcHeatRequest(int cell, double joules, string actorId)
    { Cell = cell; Joules = joules; ActorId = actorId; }
}

public sealed class SimulationBridgeStats
{
    public long PhysicsEventsSeen;
    public long PerceptsEmitted;
    public long HeatRequestsApplied;
    public long NonFiniteRejected;
    public long OutOfRangeRejected;
    public long EventsDeferred;      // 因单步限流推迟到后续步的事件数
    public int PeakEventBacklog;     // 历史最大积压
    public override string ToString()
        => $"physEvents={PhysicsEventsSeen} percepts={PerceptsEmitted} "
         + $"heat={HeatRequestsApplied} nonFinite={NonFiniteRejected} outOfRange={OutOfRangeRejected} "
         + $"deferred={EventsDeferred} peakBacklog={PeakEventBacklog}";
}

/// <summary>守恒回归门禁：把"可接受误差"变成可断言的常量，防止后续改动悄悄破坏守恒。</summary>
public sealed class ConservationGate
{
    public double EnergyTolerance = 1e-6;
    public double MassTolerance = 1e-9;
    public double ElementTolerance = 1e-9;
    public double MaxFluxRatio = 0.25;

    public bool Pass(ConservationReport r)
        => r.EnergyError <= EnergyTolerance
        && r.MassError <= MassTolerance
        && r.ElementError <= ElementTolerance
        && r.MaxFluxRatio <= MaxFluxRatio + 1e-9;

    public void Assert(ConservationReport r)
    {
        if (!Pass(r))
            throw new InvalidOperationException("守恒门禁未通过: " + r);
    }
}

public sealed class SimulationCoordinator
{
    public readonly NpcRuntimeWorld Npc;
    public readonly RealityWorld Phys;
    public readonly SimulationBridgeStats Stats = new();
    public readonly ConservationGate Gate = new();
    public readonly Queue<NpcHeatRequest> HeatRequests = new();

    public float FixedStep { get; set; } = 1.0f / 60.0f;
    public float PhysicsToNpcRadius { get; set; } = 20f;
    public int MaxActorsPerEvent { get; set; } = 8;
    public int MaxEventsPerStep { get; set; } = 256;
    public float ChemPerceptValence { get; set; } = -0.4f;
    public bool EmitThreatPercepts { get; set; } = true;
    public bool EnforceGateEachStep { get; set; }

    public double TimeSeconds { get; private set; }
    public long StepIndex { get; private set; }
    private int _lastEventCursor;

    public SimulationCoordinator(ulong npcSeed = 20261123UL, ulong physSeed = 20261124UL,
        int nx = 4, int ny = 2, int nz = 4, double cellSize = 0.5)
    {
        Npc = new NpcRuntimeWorld(npcSeed);
        Phys = new RealityWorld(nx, ny, nz, cellSize, physSeed);
    }

    public void PushHeat(int cell, double joules, string actorId = "")
        => HeatRequests.Enqueue(new NpcHeatRequest(cell, joules, actorId));

    /// <summary>固定步推进：先物理 → 再桥接 → 再 NPC，顺序永远一致。</summary>
    public void Step(float deltaSeconds)
    {
        int n = Math.Clamp((int)Math.Ceiling(Math.Max(0f, deltaSeconds) / Math.Max(1e-6f, FixedStep)), 1, 256);
        float sub = Math.Max(0f, deltaSeconds) / n;
        for (int i = 0; i < n; i++) StepOnce(sub);
    }

    public void StepOnce(float dt)
    {
        StepIndex++;
        TimeSeconds += dt;

        // NPC → 物理：热请求先落地，非有限/越界一律拒绝并计数
        while (HeatRequests.Count > 0)
        {
            var q = HeatRequests.Dequeue();
            if (!double.IsFinite(q.Joules)) { Stats.NonFiniteRejected++; continue; }
            if (q.Cell < 0 || q.Cell >= Phys.Grid.Count) { Stats.OutOfRangeRejected++; continue; }
            Phys.Grid.AddHeat(q.Cell, q.Joules);
            Stats.HeatRequestsApplied++;
        }

        Phys.Step(dt);

        // 物理 → NPC：消费后即出队。
        // 不能用"记录 Events.Count 增量"的做法：物理层在超过 2048 条时会 RemoveRange 掉头部，
        // 游标一旦回退到 0 就会把历史事件重复转成感知。出队式消费天然免疫裁剪，且限流可积压。
        var events = Phys.Events;
        int take = Math.Min(events.Count, Math.Max(0, MaxEventsPerStep));
        for (int k = 0; k < take; k++) BridgeEvent(events[k]);
        int remaining = events.Count - take;
        events.RemoveRange(0, take);
        Stats.EventsDeferred += remaining;
        Stats.PeakEventBacklog = Math.Max(Stats.PeakEventBacklog, remaining);
        _lastEventCursor = 0; // 事件队列由本层逐步移出，游标恒为 0（字段保留仅为快照格式兼容）

        Npc.Tick(dt);

        if (EnforceGateEachStep) Gate.Assert(Phys.Conservation());
    }

    private void BridgeEvent(RealityEvent e)
    {
        Stats.PhysicsEventsSeen++;
        string kind; float valence;
        switch (e.Kind)
        {
            case "burst":
            case "fracture":
                if (!EmitThreatPercepts) return;
                kind = "threat"; valence = -0.8f; break;
            case "ignition":
            case "reaction":
                kind = "chem"; valence = ChemPerceptValence; break;
            case "extinction":
                kind = "sound"; valence = 0.0f; break;
            default: return;
        }
        float intensity = Math.Clamp(0.3f + 0.7f *
            (float)(Math.Log10(1.0 + Math.Abs(e.Value)) / 6.0), 0.3f, 1f);
        var pos = CellToWorld(e.Cell);
        var targets = Npc.Actors.Values
            .Select(a => new { A = a, D = Vector3.Distance(a.Position, pos) })
            .Where(x => x.D <= PhysicsToNpcRadius)
            .OrderBy(x => x.D)
            .ThenBy(x => x.A.Id, StringComparer.Ordinal)
            .Take(Math.Max(0, MaxActorsPerEvent));
        foreach (var t in targets)
        {
            Npc.PushPercept(new NpcRtPercept(kind, e.Kind + ":" + e.Detail,
                pos, intensity, valence, TimeSeconds));
            Stats.PerceptsEmitted++;
        }
    }

    public Vector3 CellToWorld(int cell)
    {
        if (cell < 0 || cell >= Phys.Grid.Count) return Vector3.Zero;
        var (x, y, z) = Phys.Grid.Coords(cell);
        float h = (float)Phys.Grid.CellSize;
        return new Vector3(x * h, y * h, z * h);
    }

    private const string Sep = "---YAMA_PHYSICS_SNAPSHOT---";

    public string Snapshot()
    {
        var bag = new NpcRtStateBag();
        bag.Put("v", 1); bag.Put("time", TimeSeconds); bag.Put("step", StepIndex);
        bag.Put("cursor", _lastEventCursor);
        bag.Put("npc", Convert.ToBase64String(Encoding.UTF8.GetBytes(Npc.Snapshot())));
        bag.Put("stats.seen", Stats.PhysicsEventsSeen);
        bag.Put("stats.percepts", Stats.PerceptsEmitted);
        bag.Put("stats.heat", Stats.HeatRequestsApplied);
        bag.Put("stats.nonfinite", Stats.NonFiniteRejected);
        bag.Put("stats.range", Stats.OutOfRangeRejected);
        bag.Put("stats.deferred", Stats.EventsDeferred);
        bag.Put("stats.peak", Stats.PeakEventBacklog);
        bag.Put("cfg.fixed", FixedStep); bag.Put("cfg.radius", PhysicsToNpcRadius);
        bag.Put("cfg.maxActors", MaxActorsPerEvent);
        bag.Put("cfg.maxEvents", MaxEventsPerStep);
        bag.Put("cfg.chemVal", ChemPerceptValence);
        bag.Put("cfg.threat", EmitThreatPercepts); bag.Put("cfg.gate", EnforceGateEachStep);
        bag.Put("heatQueue", HeatRequests.Count);
        int i = 0;
        foreach (var q in HeatRequests)
        { bag.Put("hq" + i + ".cell", q.Cell); bag.Put("hq" + i + ".j", q.Joules);
          bag.Put("hq" + i + ".a", q.ActorId); i++; }
        return bag.Dump() + "\n" + Sep + "\n" + Phys.Snapshot();
    }

    public void Restore(string snapshot)
    {
        int cut = snapshot.LastIndexOf(Sep, StringComparison.Ordinal);
        string npcPart = cut >= 0 ? snapshot.Substring(0, cut) : snapshot;
        string physJson = cut >= 0 ? snapshot.Substring(cut + Sep.Length).TrimStart('\n', '\r') : "";
        var bag = NpcRtStateBag.Parse(npcPart);
        TimeSeconds = bag.GetDouble("time"); StepIndex = bag.GetLong("step");
        _lastEventCursor = bag.GetInt("cursor");
        string npcB64 = bag.GetString("npc");
        if (!string.IsNullOrEmpty(npcB64))
            Npc.Restore(Encoding.UTF8.GetString(Convert.FromBase64String(npcB64)));
        if (!string.IsNullOrWhiteSpace(physJson)) Phys.Restore(physJson);
        Stats.PhysicsEventsSeen = bag.GetLong("stats.seen");
        Stats.PerceptsEmitted = bag.GetLong("stats.percepts");
        Stats.HeatRequestsApplied = bag.GetLong("stats.heat");
        Stats.NonFiniteRejected = bag.GetLong("stats.nonfinite");
        Stats.OutOfRangeRejected = bag.GetLong("stats.range");
        Stats.EventsDeferred = bag.GetLong("stats.deferred");
        Stats.PeakEventBacklog = bag.GetInt("stats.peak");
        FixedStep = bag.GetFloat("cfg.fixed", 1f / 60f);
        PhysicsToNpcRadius = bag.GetFloat("cfg.radius", 20f);
        MaxActorsPerEvent = bag.GetInt("cfg.maxActors", 8);
        MaxEventsPerStep = bag.GetInt("cfg.maxEvents", 256);
        ChemPerceptValence = bag.GetFloat("cfg.chemVal", -0.4f);
        EmitThreatPercepts = bag.GetBool("cfg.threat", true);
        EnforceGateEachStep = bag.GetBool("cfg.gate", false);
        HeatRequests.Clear();
        int n = bag.GetInt("heatQueue");
        for (int i = 0; i < n; i++)
            HeatRequests.Enqueue(new NpcHeatRequest(bag.GetInt("hq" + i + ".cell"),
                bag.GetDouble("hq" + i + ".j"), bag.GetString("hq" + i + ".a")));
    }

    public static bool VerifyDeterminism(string snapshot, float dt, int steps)
        => VerifyDeterminism(snapshot, dt, steps, 4, 2, 4, 0.5);

    public static bool VerifyDeterminism(string snapshot, float dt, int steps,
        int nx, int ny, int nz, double cellSize)
    {
        string Run()
        {
            var c = new SimulationCoordinator(20261123UL, 20261124UL, nx, ny, nz, cellSize);
            c.Restore(snapshot);
            for (int i = 0; i < Math.Max(0, steps); i++) c.Step(dt);
            return c.Snapshot();
        }
        return string.Equals(Run(), Run(), StringComparison.Ordinal);
    }
}

/// <summary>确定性故障注入：用来验证护栏/守恒在异常输入下不崩，不含墙钟断言。</summary>
public static class SimulationChaos
{
    public sealed class ChaosReport
    {
        public int Steps;
        public int NonFiniteRejected;
        public int OutOfRangeRejected;
        public bool AllFinite = true;
        public bool GatePassed = true;
        public override string ToString()
            => $"steps={Steps} nonFinite={NonFiniteRejected} outOfRange={OutOfRangeRejected} "
             + $"finite={AllFinite} gate={GatePassed}";
    }

    public static ChaosReport Run(ulong seed, int steps)
    {
        var rng = new NpcRtRandom(seed);
        var c = new SimulationCoordinator();
        c.Phys.Grid.Emissivity = 0.0;
        c.EnforceGateEachStep = false;
        for (int i = 0; i < 6; i++)
        {
            var a = c.Npc.Register("n" + i, "NPC" + i);
            a.Position = new Vector3(i * 0.5f, 0f, i * 0.25f);
        }
        var rep = new ChaosReport { Steps = steps };
        for (int i = 0; i < Math.Max(0, steps); i++)
        {
            // 随机热源（含 NaN / Inf / 越界），验证桥接层全部拦下
            double j = rng.NextFloat() switch
            {
                < 0.10f => double.NaN,
                < 0.16f => double.PositiveInfinity,
                _ => (double)rng.Range(-2000f, 8000f)
            };
            int cell = rng.NextFloat() < 0.08f ? -7 : rng.NextInt(0, c.Phys.Grid.Count);
            c.PushHeat(cell, j, "chaos");
            c.Step(1f / 60f);
            var cons = c.Phys.Conservation();
            if (!c.Gate.Pass(cons)) rep.GatePassed = false;
            for (int k = 0; k < c.Phys.Grid.Count; k++)
                if (!double.IsFinite(c.Phys.Grid.T[k]) || c.Phys.Grid.T[k] < 0.0)
                { rep.AllFinite = false; break; }
            if (!rep.AllFinite) break;
        }
        rep.NonFiniteRejected = (int)c.Stats.NonFiniteRejected;
        rep.OutOfRangeRejected = (int)c.Stats.OutOfRangeRejected;
        return rep;
    }
}
