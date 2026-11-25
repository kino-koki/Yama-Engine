// ════════════════════════════════════════════════════════════════════════
// NpcRuntimeNavigation.cs — Round 5：把「直线 GoTo」升级为路径点队列
// 不重写既有 NavMesh/寻路算法：通过 INpcRtPathSource 接缝接任意寻路器，
// 本文件只负责路径点消费、到达判定、卡住检测与重规划计数（纯 CPU 确定性）。
// ════════════════════════════════════════════════════════════════════════
using System.Numerics;

namespace YamaEngine.NPC.Runtime;

/// <summary>寻路接缝：由宿主把既有 NavMesh / A* / ZoneGraph 查询结果喂进来。</summary>
public interface INpcRtPathSource
{
    IReadOnlyList<Vector3> FindPath(Vector3 from, Vector3 to);
}

/// <summary>直线兜底：无寻路器时按等分插值给路径点。</summary>
public sealed class NpcRtStraightPathSource : INpcRtPathSource
{
    public int MaxPoints = 16;
    public IReadOnlyList<Vector3> FindPath(Vector3 from, Vector3 to)
    {
        var list = new List<Vector3>();
        int n = Math.Clamp(MaxPoints, 1, 256);
        for (int i = 1; i <= n; i++) list.Add(Vector3.Lerp(from, to, (float)i / n));
        return list;
    }
}

public sealed class NpcRtWaypointPlanner : INpcRtStateful
{
    private readonly Dictionary<string, Queue<Vector3>> _paths = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Vector3> _lastPos = new(StringComparer.Ordinal);
    private readonly Dictionary<string, float> _stuckSeconds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _replans = new(StringComparer.Ordinal);

    public float ArriveRadius { get; set; } = 0.05f;
    public float StuckSpeedThreshold { get; set; } = 0.01f;
    public float StuckSeconds { get; set; } = 1.5f;
    public int MaxReplans { get; set; } = 3;
    public long TotalReplans { get; private set; }
    public long TotalArrivals { get; private set; }
    public INpcRtPathSource PathSource { get; set; } = new NpcRtStraightPathSource();

    public int WaypointCount(string actorId)
        => _paths.TryGetValue(actorId, out var q) ? q.Count : 0;
    public int ReplanCount(string actorId)
        => _replans.TryGetValue(actorId, out var n) ? n : 0;
    public Vector3? CurrentWaypoint(string actorId)
        => _paths.TryGetValue(actorId, out var q) && q.Count > 0 ? q.Peek() : null;

    public void SetDestination(string actorId, Vector3 from, Vector3 to)
    {
        var pts = PathSource.FindPath(from, to) ?? Array.Empty<Vector3>();
        var q = new Queue<Vector3>(pts.Count + 1);
        foreach (var p in pts) q.Enqueue(p);
        if (q.Count == 0 || Vector3.Distance(q.Last(), to) > ArriveRadius) q.Enqueue(to);
        _paths[actorId] = q;
        _lastPos[actorId] = from;
        _stuckSeconds[actorId] = 0f;
        _replans.TryAdd(actorId, 0);
    }

    /// <summary>每帧推进：返回当前应前往的点；空表示已到达或放弃。</summary>
    public Vector3? Tick(string actorId, Vector3 current, float dt)
    {
        if (!_paths.TryGetValue(actorId, out var q) || q.Count == 0) return null;
        float moved = _lastPos.TryGetValue(actorId, out var prev)
            ? Vector3.Distance(prev, current) : 0f;
        _lastPos[actorId] = current;
        float stuck = _stuckSeconds.GetValueOrDefault(actorId);
        stuck = moved / Math.Max(1e-5f, dt) < StuckSpeedThreshold
            ? stuck + Math.Max(0f, dt) : 0f;
        _stuckSeconds[actorId] = stuck;

        if (stuck >= StuckSeconds)
        {
            int n = _replans.GetValueOrDefault(actorId);
            if (n < MaxReplans && q.Count > 0)
            {
                var goal = q.Last();
                var pts = PathSource.FindPath(current, goal) ?? Array.Empty<Vector3>();
                var replanned = new Queue<Vector3>(pts);
                replanned.Enqueue(goal);
                _paths[actorId] = replanned; q = replanned;
                _replans[actorId] = n + 1; TotalReplans++;
                _stuckSeconds[actorId] = 0f;
            }
            else { _paths[actorId] = new Queue<Vector3>(); return null; }
        }

        while (q.Count > 0 && Vector3.Distance(current, q.Peek()) <= ArriveRadius) q.Dequeue();
        if (q.Count == 0) { TotalArrivals++; return null; }
        return q.Peek();
    }

    public void Cancel(string actorId)
    {
        _paths.Remove(actorId); _stuckSeconds.Remove(actorId);
    }

    public void Capture(NpcRtStateBag bag, string p)
    {
        bag.Put(p + ".replans", TotalReplans); bag.Put(p + ".arrivals", TotalArrivals);
        int i = 0;
        foreach (var kv in _paths.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            string k = p + ".p" + i++ + ".";
            bag.Put(k + "actor", kv.Key); bag.Put(k + "n", kv.Value.Count);
            int j = 0;
            foreach (var w in kv.Value) bag.Put(k + "w" + j++, w);
            bag.Put(k + "last", _lastPos.GetValueOrDefault(kv.Key));
            bag.Put(k + "stuck", _stuckSeconds.GetValueOrDefault(kv.Key));
            bag.Put(k + "rp", _replans.GetValueOrDefault(kv.Key));
        }
        bag.Put(p + ".n", i);
    }

    public void Restore(NpcRtStateBag bag, string p)
    {
        _paths.Clear(); _lastPos.Clear(); _stuckSeconds.Clear(); _replans.Clear();
        TotalReplans = bag.GetLong(p + ".replans"); TotalArrivals = bag.GetLong(p + ".arrivals");
        int n = bag.GetInt(p + ".n");
        for (int i = 0; i < n; i++)
        {
            string k = p + ".p" + i + ".";
            string actor = bag.GetString(k + "actor");
            int cnt = bag.GetInt(k + "n");
            var q = new Queue<Vector3>();
            for (int j = 0; j < cnt; j++) q.Enqueue(bag.GetVector3(k + "w" + j));
            _paths[actor] = q;
            _lastPos[actor] = bag.GetVector3(k + "last");
            _stuckSeconds[actor] = bag.GetFloat(k + "stuck");
            _replans[actor] = bag.GetInt(k + "rp");
        }
    }
}

/// <summary>包装任意执行器：GoTo/Flee 命令自动转成路径点队列再交给底层执行。</summary>
public sealed class NpcRtWaypointExecutor : INpcRtExecutor
{
    private readonly INpcRtExecutor _inner;
    public NpcRtWaypointPlanner Planner { get; } = new();
    private readonly Dictionary<string, Vector3> _pos = new(StringComparer.Ordinal);

    public NpcRtWaypointExecutor(INpcRtExecutor inner) { _inner = inner; }

    public void SyncPosition(string actorId, Vector3 position) => _pos[actorId] = position;

    public bool Apply(NpcRtCommand command)
    {
        if (command.Kind is NpcRtIntentNames.GoTo or NpcRtIntentNames.Flee
            or NpcRtIntentNames.Wander)
        {
            var from = _pos.GetValueOrDefault(command.ActorId);
            Planner.SetDestination(command.ActorId, from, command.TargetPosition);
        }
        else Planner.Cancel(command.ActorId);
        return _inner.Apply(command);
    }

    public void Tick(string actorId, float deltaSeconds)
    {
        var cur = _pos.GetValueOrDefault(actorId);
        var wp = Planner.Tick(actorId, cur, deltaSeconds);
        if (wp.HasValue)
            _inner.Apply(new NpcRtCommand { ActorId = actorId, Kind = NpcRtIntentNames.GoTo,
                TargetPosition = wp.Value, Source = "scripted", Priority = 0.6f });
        _inner.Tick(actorId, deltaSeconds);
    }

    public void Stop(string actorId)
    {
        Planner.Cancel(actorId);
        _inner.Stop(actorId);
    }
}
