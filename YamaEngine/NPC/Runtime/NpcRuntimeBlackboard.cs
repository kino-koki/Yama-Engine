// ════════════════════════════════════════════════════════════════════════
// NpcRuntimeBlackboard.cs — 分层黑板（世界 / 小队 / 个体）
// 作用域遮蔽、TTL、版本号、变更日志、观察者、快照回滚。
// 行为树、GOAP、StateTree 与 LLM 心智共用同一份事实源。
// ════════════════════════════════════════════════════════════════════════
using System.Globalization;
using System.Numerics;

namespace YamaEngine.NPC.Runtime;

public enum NpcRtScope { World, Squad, Actor }

public readonly struct NpcRtFact
{
    public readonly string Value;
    public readonly double Expiry;      // <=0 表示永不过期
    public readonly long Version;
    public readonly string Writer;
    public NpcRtFact(string value, double expiry, long version, string writer)
    { Value = value; Expiry = expiry; Version = version; Writer = writer; }
}

public sealed class NpcRuntimeBlackboard : INpcRtStateful
{
    private readonly Dictionary<string, NpcRtFact> _facts = new(StringComparer.Ordinal);
    private readonly List<string> _log = new();
    private long _version;
    private double _now;
    private readonly int _maxLog;

    public NpcRuntimeBlackboard(int maxLog = 512) { _maxLog = Math.Max(16, maxLog); }
    public event Action<string, string, string>? OnChanged;   // key, oldValue, newValue
    public IReadOnlyList<string> ChangeLog => _log;
    public long Version => _version;
    public int Count => _facts.Count;

    private static string Key(NpcRtScope scope, string owner, string key)
        => (int)scope + "|" + owner + "|" + key;

    public void Advance(double now) => _now = now;

    public void Set(NpcRtScope scope, string owner, string key, string value,
        double ttlSeconds = 0, string writer = "runtime")
    {
        var k = Key(scope, owner, key);
        string old = _facts.TryGetValue(k, out var f) && !IsExpired(f) ? f.Value : "";
        double expiry = ttlSeconds > 0 ? _now + ttlSeconds : 0;
        _facts[k] = new NpcRtFact(value, expiry, ++_version, writer);
        Record(k + " " + old + " -> " + value);
        OnChanged?.Invoke(k, old, value);
    }

    /// <summary>按 个体 → 小队 → 世界 的顺序解析，个体作用域优先遮蔽。</summary>
    public string? Get(string actorId, string squadId, string key)
    {
        foreach (var s in new[] { NpcRtScope.Actor, NpcRtScope.Squad, NpcRtScope.World })
        {
            string owner = s == NpcRtScope.Actor ? actorId : s == NpcRtScope.Squad ? squadId : "";
            if (_facts.TryGetValue(Key(s, owner, key), out var f) && !IsExpired(f)) return f.Value;
        }
        return null;
    }
    public string GetString(string actorId, string squadId, string key, string d = "")
        => Get(actorId, squadId, key) ?? d;
    public float GetFloat(string actorId, string squadId, string key, float d = 0)
        => float.TryParse(Get(actorId, squadId, key), NumberStyles.Float,
            CultureInfo.InvariantCulture, out var v) ? v : d;
    public bool GetBool(string actorId, string squadId, string key, bool d = false)
    {
        var s = Get(actorId, squadId, key);
        return s is null ? d : s is "1" or "true" or "yes";
    }
    public Vector3 GetVector3(string actorId, string squadId, string key, Vector3 d = default)
    {
        var parts = (Get(actorId, squadId, key) ?? "").Split(',');
        if (parts.Length == 3
            && float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
            && float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)
            && float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
            return new Vector3(x, y, z);
        return d;
    }
    public void SetVector3(NpcRtScope scope, string owner, string key, Vector3 v)
        => Set(scope, owner, key,
            v.X.ToString("R", CultureInfo.InvariantCulture) + "," +
            v.Y.ToString("R", CultureInfo.InvariantCulture) + "," +
            v.Z.ToString("R", CultureInfo.InvariantCulture));

    public bool Remove(NpcRtScope scope, string owner, string key)
    {
        bool ok = _facts.Remove(Key(scope, owner, key));
        if (ok) Record("remove " + Key(scope, owner, key));
        return ok;
    }

    /// <summary>清理过期事实，返回清理条数。</summary>
    public int Sweep()
    {
        var dead = _facts.Where(p => IsExpired(p.Value)).Select(p => p.Key).ToList();
        foreach (var k in dead) { _facts.Remove(k); Record("expire " + k); }
        return dead.Count;
    }

    private bool IsExpired(NpcRtFact f) => f.Expiry > 0 && _now >= f.Expiry;

    private void Record(string line)
    {
        _log.Add(_now.ToString("R", CultureInfo.InvariantCulture) + " " + line);
        if (_log.Count > _maxLog) _log.RemoveRange(0, _log.Count - _maxLog);
    }

    public void Capture(NpcRtStateBag bag, string prefix)
    {
        bag.Put(prefix + ".version", _version);
        bag.Put(prefix + ".now", _now);
        foreach (var p in _facts.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            string k = prefix + ".f." + p.Key;
            bag.Put(k + ".v", p.Value.Value);
            bag.Put(k + ".e", p.Value.Expiry);
            bag.Put(k + ".r", p.Value.Version);
            bag.Put(k + ".w", p.Value.Writer);
        }
    }
    public void Restore(NpcRtStateBag bag, string prefix)
    {
        _facts.Clear(); _log.Clear();
        _version = bag.GetLong(prefix + ".version");
        _now = bag.GetDouble(prefix + ".now");
        string head = prefix + ".f.";
        foreach (var kv in bag.Raw.Where(x => x.Key.StartsWith(head, StringComparison.Ordinal)
                                             && x.Key.EndsWith(".v", StringComparison.Ordinal)))
        {
            string k = kv.Key.Substring(head.Length, kv.Key.Length - head.Length - 2);
            string b = head + k;
            _facts[k] = new NpcRtFact(kv.Value, bag.GetDouble(b + ".e"),
                bag.GetLong(b + ".r"), bag.GetString(b + ".w"));
        }
    }
}
