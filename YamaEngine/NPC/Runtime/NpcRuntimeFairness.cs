// ════════════════════════════════════════════════════════════════════════
// NpcRuntimeFairness.cs — Round 5：确定性公平调度
// 修掉真实缺陷：原来每帧按 actorId 排序、取前 N 个决策，当 actor 数 > 单帧预算时，
// id 靠后的 NPC 会永久饿死（永远轮不到决策）。
// 策略：老化优先（等待 tick 越久越优先）+ id 字典序兜底，完全无状态、可快照回放。
// ════════════════════════════════════════════════════════════════════════
namespace YamaEngine.NPC.Runtime;

public sealed class NpcRtStarvationReport
{
    public int Candidates;
    public int Selected;
    public long MaxWaitTicks;
    public long MinWaitTicks;
    public double MeanWaitTicks;
    public int ZeroWaitSelected;
    public override string ToString()
        => $"cand={Candidates} sel={Selected} wait[{MinWaitTicks},{MaxWaitTicks}] "
         + $"mean={MeanWaitTicks:0.0} fresh={ZeroWaitSelected}";
}

/// <summary>纯函数式公平选择器：不持有状态，排序只依赖 actor 的 LastDecisionTick 与当前 TickIndex。</summary>
public sealed class NpcRtFairScheduler
{
    public long Selections { get; private set; }

    public IReadOnlyList<NpcRtActor> Select(IEnumerable<NpcRtActor> candidates,
        long tickIndex, int budget)
    {
        var list = candidates as IReadOnlyList<NpcRtActor> ?? candidates.ToList();
        int take = Math.Clamp(budget, 0, list.Count);
        if (take == 0) return Array.Empty<NpcRtActor>();
        var ordered = list
            .Select(a => new { A = a, Wait = Math.Max(0L, tickIndex - a.LastDecisionTick) })
            .OrderByDescending(x => x.Wait)
            .ThenBy(x => x.A.Id, StringComparer.Ordinal)
            .Select(x => x.A)
            .Take(take)
            .ToList();
        Selections += ordered.Count;
        return ordered;
    }

    public NpcRtStarvationReport Diagnose(IEnumerable<NpcRtActor> candidates,
        long tickIndex, int budget)
    {
        var list = candidates.ToList();
        var waits = list.Select(a => Math.Max(0L, tickIndex - a.LastDecisionTick)).ToList();
        var picked = Select(list, tickIndex, budget);
        return new NpcRtStarvationReport
        {
            Candidates = list.Count, Selected = picked.Count,
            MaxWaitTicks = waits.Count == 0 ? 0 : waits.Max(),
            MinWaitTicks = waits.Count == 0 ? 0 : waits.Min(),
            MeanWaitTicks = waits.Count == 0 ? 0 : waits.Average(),
            ZeroWaitSelected = picked.Count(a => tickIndex - a.LastDecisionTick <= 0)
        };
    }

    /// <summary>回归判据：连续跑若干帧后，每个可决策 actor 都应至少被选中一次。</summary>
    public static bool VerifyNoStarvation(IReadOnlyList<NpcRtActor> actors,
        int ticks, int budgetPerTick, long startTick = 1)
    {
        if (actors.Count == 0) return true;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var sched = new NpcRtFairScheduler();
        for (long t = startTick; t < startTick + Math.Max(1, ticks); t++)
        {
            var picked = sched.Select(actors, t, budgetPerTick);
            foreach (var a in picked) { seen.Add(a.Id); a.LastDecisionTick = t; }
            if (seen.Count == actors.Count) return true;
        }
        return seen.Count == actors.Count;
    }
}
