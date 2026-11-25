// ════════════════════════════════════════════════════════════════════════
// NpcRuntimeScale.cs — Round 2：规模化 / 冲突仲裁 / 有界命令日志
// 解决长时运行内存增长、多观察者（多玩家/LLM/反射）争用同一 actor 的仲裁问题。
// 全部纯计数、确定性，不测墙钟作断言；墙钟只作可选报告字段。
// ════════════════════════════════════════════════════════════════════════
using System.Diagnostics;

namespace YamaEngine.NPC.Runtime;

/// <summary>固定容量环形命令日志：长时运行内存恒定，可快照恢复。</summary>
public sealed class NpcRtRingLog : INpcRtStateful
{
    private readonly NpcRtCommand[] _buf;
    private int _head;
    private int _count;

    public NpcRtRingLog(int capacity = 1024)
    { _buf = new NpcRtCommand[Math.Max(1, capacity)]; }

    public int Capacity => _buf.Length;
    public int Count => _count;
    public long TotalAdded { get; private set; }
    public long Dropped { get; private set; }

    private int At(int logicalIndex)
        => (_head - _count + logicalIndex + _buf.Length * 2) % _buf.Length;

    public void Add(NpcRtCommand c)
    {
        _buf[_head] = c;
        _head = (_head + 1) % _buf.Length;
        if (_count < _buf.Length) _count++; else Dropped++;
        TotalAdded++;
    }

    /// <summary>最近 n 条（新→旧）。</summary>
    public IReadOnlyList<NpcRtCommand> Latest(int n = 16)
    {
        int take = Math.Clamp(n, 0, _count);
        var list = new List<NpcRtCommand>(take);
        for (int k = _count - take; k < _count; k++) list.Add(_buf[At(k)]);
        list.Reverse();
        return list;
    }

    public IReadOnlyList<NpcRtCommand> ByActor(string actorId)
    {
        var list = new List<NpcRtCommand>();
        for (int k = _count - 1; k >= 0; k--)
            if (_buf[At(k)].ActorId == actorId) list.Add(_buf[At(k)]);
        return list;
    }

    public void Capture(NpcRtStateBag bag, string p)
    {
        bag.Put(p + ".cap", _buf.Length); bag.Put(p + ".head", _head);
        bag.Put(p + ".count", _count); bag.Put(p + ".total", TotalAdded);
        bag.Put(p + ".dropped", Dropped);
        for (int k = 0; k < _count; k++)
        {
            var c = _buf[At(k)]; string b = p + ".c" + k + ".";
            bag.Put(b + "seq", c.Sequence); bag.Put(b + "actor", c.ActorId);
            bag.Put(b + "kind", c.Kind); bag.Put(b + "target", c.Target);
            bag.Put(b + "pos", c.TargetPosition); bag.Put(b + "pri", c.Priority);
            bag.Put(b + "reason", c.Reason); bag.Put(b + "src", c.Source);
        }
    }

    public void Restore(NpcRtStateBag bag, string p)
    {
        _head = ((bag.GetInt(p + ".head") % _buf.Length) + _buf.Length) % _buf.Length;
        _count = Math.Clamp(bag.GetInt(p + ".count"), 0, _buf.Length);
        TotalAdded = bag.GetLong(p + ".total"); Dropped = bag.GetLong(p + ".dropped");
        for (int k = 0; k < _count; k++)
        {
            string b = p + ".c" + k + ".";
            _buf[At(k)] = new NpcRtCommand
            {
                Sequence = bag.GetLong(b + "seq"), ActorId = bag.GetString(b + "actor"),
                Kind = NpcRtIntentNames.Normalize(bag.GetString(b + "kind")),
                Target = bag.GetString(b + "target"), TargetPosition = bag.GetVector3(b + "pos"),
                Priority = bag.GetFloat(b + "pri"), Reason = bag.GetString(b + "reason"),
                Source = bag.GetString(b + "src", "heuristic")
            };
        }
    }
}

/// <summary>多观察者命令仲裁：来源可信度 → 优先级 → 序号（早者稳定），防 LLM 覆盖反射行为。</summary>
public sealed class NpcRtConflictResolver
{
    public int ResolvedCount { get; private set; }

    public static int SourceRank(string source) => source switch
    {
        "reflex" => 4,
        "scripted" => 3,
        "heuristic" => 2,
        "llm" => 1,
        _ => 0
    };

    public NpcRtCommand Resolve(NpcRtCommand? incumbent, NpcRtCommand incoming)
    {
        ResolvedCount++;
        if (incumbent is null) return incoming;
        int a = SourceRank(incumbent.Source), b = SourceRank(incoming.Source);
        if (a != b) return b > a ? incoming : incumbent;
        if (MathF.Abs(incoming.Priority - incumbent.Priority) > 1e-6f)
            return incoming.Priority > incumbent.Priority ? incoming : incumbent;
        return incoming.Sequence < incumbent.Sequence ? incoming : incumbent;
    }

    /// <summary>每个 actor 只保留一个胜出命令，输出按 actorId 排序（确定性）。</summary>
    public IReadOnlyList<NpcRtCommand> MergeBatch(IEnumerable<NpcRtCommand> batch)
    {
        var winners = new Dictionary<string, NpcRtCommand>(StringComparer.Ordinal);
        foreach (var c in batch)
            winners[c.ActorId] = winners.TryGetValue(c.ActorId, out var w) ? Resolve(w, c) : c;
        return winners.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Value).ToList();
    }
}

public sealed class NpcRtScaleReport
{
    public int Actors;
    public int Ticks;
    public long Commands;
    public int MaxCommandsPerTick;
    public int BudgetViolations;
    public double WallMilliseconds;
    public override string ToString()
        => $"actors={Actors} ticks={Ticks} commands={Commands} max/tick={MaxCommandsPerTick} "
         + $"violations={BudgetViolations} wall={WallMilliseconds:0.0}ms";
}

/// <summary>纯计数规模探针：1000+ actor 也不依赖墙钟断言，回放必一致。</summary>
public static class NpcRtScaleProbe
{
    private static long LastSequence(NpcRuntimeWorld w)
        => w.Commands.Count == 0 ? 0L : w.Commands[w.Commands.Count - 1].Sequence;

    public static NpcRtScaleReport Run(NpcRuntimeWorld world, int ticks, float dt,
        bool measureWallClock = false)
    {
        var sw = measureWallClock ? Stopwatch.StartNew() : null;
        long prev = LastSequence(world);
        int maxPerTick = 0, violations = 0;
        int n = Math.Max(0, ticks);
        for (int i = 0; i < n; i++)
        {
            world.Tick(dt);
            long now = LastSequence(world);
            int delta = (int)Math.Clamp(now - prev, 0L, int.MaxValue);
            prev = now;
            maxPerTick = Math.Max(maxPerTick, delta);
            if (delta > world.Budget.MaxFullDecisionsPerTick) violations++;
        }
        sw?.Stop();
        return new NpcRtScaleReport
        {
            Actors = world.Actors.Count, Ticks = n, Commands = prev,
            MaxCommandsPerTick = maxPerTick, BudgetViolations = violations,
            WallMilliseconds = sw?.Elapsed.TotalMilliseconds ?? 0.0
        };
    }
}
