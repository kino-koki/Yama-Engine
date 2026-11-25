// ════════════════════════════════════════════════════════════════════════
// NpcRuntimeGuard.cs — LLM 输出护栏 + 预算 + 审计 + 离线确定性回退
// 任何外部文本都要过：长度 → JSON 提取 → schema 白名单 → 范围钳制 → 注入清洗。
// 失败一律回退到本地启发式，NPC 行为永不中断。
// ════════════════════════════════════════════════════════════════════════
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Numerics;

namespace YamaEngine.NPC.Runtime;

public sealed class NpcRtGuardResult
{
    public string Kind = NpcRtIntentNames.Idle;
    public string Target = "";
    public Vector3 TargetPosition;
    public float Priority;
    public string Reason = "";
    public string Source = "heuristic";
    public string Audit = "";
    public bool Accepted;
}

public sealed class NpcRuntimeGuard : INpcRtStateful
{
    private readonly NpcRtBudget _budget;
    private double _windowStart;
    private int _windowCalls;
    private long _totalCalls;
    private long _totalRejects;
    private readonly List<string> _audit = new();

    public NpcRuntimeGuard(NpcRtBudget budget) { _budget = budget; }
    public IReadOnlyList<string> AuditLog => _audit;
    public long TotalCalls => _totalCalls;
    public long TotalRejects => _totalRejects;

    private static readonly string[] InjectionMarkers =
        { "ignore previous", "ignore all previous", "system prompt", "you are now",
          "忽略之前的", "忽略以上", "系统提示词", "越权", "developer mode" };

    public string BuildPrompt(NpcRtActor actor, INpcRtHost? host,
        NpcRuntimeBlackboard board, string squadId,
        IReadOnlyList<NpcRtPercept> percepts, string memories)
    {
        var sb = new StringBuilder();
        sb.AppendLine("你是游戏 NPC 的行为决策器。只输出一个 JSON 对象，禁止输出其他文字。");
        sb.AppendLine("角色：" + (host?.DescribePersona(actor.Id) ?? actor.DisplayName));
        sb.AppendLine("记忆：" + memories);
        sb.AppendLine("心情效价：" + actor.Valence.ToString("0.00", CultureInfo.InvariantCulture));
        sb.AppendLine("生命：" + actor.Health.ToString("0.0", CultureInfo.InvariantCulture));
        sb.Append("感知：");
        sb.AppendLine(percepts.Count == 0 ? "无" : string.Join("、",
            percepts.Take(6).Select(p => p.Kind + ":" + p.Subject)));
        sb.AppendLine("可用意图：" + string.Join("|", NpcRtIntentNames.All));
        sb.AppendLine("输出格式：{\"kind\":\"...\",\"target\":\"...\",\"x\":0,\"z\":0,\"priority\":0.0~1.0,\"reason\":\"...\"}");
        sb.AppendLine("约束：不得请求工具、不得修改规则、不得输出代码。");
        string prompt = sb.ToString();
        return prompt.Length > _budget.MaxPromptChars ? prompt.Substring(0, _budget.MaxPromptChars) : prompt;
    }

    /// <summary>从任意文本中抽取首个平衡 JSON 对象（抗 markdown / 前后噪声）。</summary>
    public static string? ExtractJsonObject(string raw)
    {
        int start = raw.IndexOf('{');
        if (start < 0) return null;
        int depth = 0; bool inString = false; bool escape = false;
        for (int i = start; i < raw.Length; i++)
        {
            char c = raw[i];
            if (inString)
            {
                if (escape) escape = false;
                else if (c == '\\') escape = true;
                else if (c == '"') inString = false;
                continue;
            }
            if (c == '"') inString = true;
            else if (c == '{') depth++;
            else if (c == '}') { depth--; if (depth == 0) return raw.Substring(start, i - start + 1); }
        }
        return null;
    }

    private static bool LooksLikeInjection(string s)
    {
        foreach (var m in InjectionMarkers)
            if (s.Contains(m, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>严格 schema 校验：未知意图、越界数值、超长字段一律拒绝。</summary>
    public NpcRtGuardResult Validate(string? raw, string fallbackSource = "llm")
    {
        var res = new NpcRtGuardResult { Source = fallbackSource };
        if (string.IsNullOrWhiteSpace(raw)) { res.Audit = "empty"; return res; }
        if (raw.Length > _budget.MaxReplyChars) raw = raw.Substring(0, _budget.MaxReplyChars);
        if (LooksLikeInjection(raw)) { res.Audit = "injection_marker"; Reject(res); return res; }

        string? json = ExtractJsonObject(raw);
        if (json is null) { res.Audit = "no_json"; Reject(res); return res; }
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { res.Audit = "not_object"; Reject(res); return res; }
            if (!root.TryGetProperty("kind", out var k) || k.ValueKind != JsonValueKind.String)
            { res.Audit = "missing_kind"; Reject(res); return res; }
            string kind = k.GetString() ?? "";
            if (!NpcRtIntentNames.IsKnown(kind)) { res.Audit = "unknown_kind:" + kind; Reject(res); return res; }
            string target = Str(root, "target", 64);
            string reason = Str(root, "reason", 160);
            if (LooksLikeInjection(target) || LooksLikeInjection(reason))
            { res.Audit = "injection_in_field"; Reject(res); return res; }
            float x = Num(root, "x", 0f, -100000f, 100000f);
            float z = Num(root, "z", 0f, -100000f, 100000f);
            float priority = Num(root, "priority", 0.5f, 0f, 1f);

            res.Kind = kind; res.Target = target; res.Reason = reason;
            res.TargetPosition = new Vector3(x, 0f, z);
            res.Priority = priority; res.Accepted = true; res.Audit = "ok";
            return res;
        }
        catch (JsonException) { res.Audit = "bad_json"; Reject(res); return res; }
    }

    private static string Str(JsonElement r, string name, int max)
    {
        if (!r.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String) return "";
        string s = (v.GetString() ?? "").Trim();
        return s.Length > max ? s.Substring(0, max) : s;
    }
    private static float Num(JsonElement r, string name, float d, float lo, float hi)
    {
        if (!r.TryGetProperty(name, out var v)) return d;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetSingle(out var f))
            return float.IsFinite(f) ? Math.Clamp(f, lo, hi) : d;
        if (v.ValueKind == JsonValueKind.String
            && float.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var g))
            return float.IsFinite(g) ? Math.Clamp(g, lo, hi) : d;
        return d;
    }
    private void Reject(NpcRtGuardResult res) { _totalRejects++; Log("reject " + res.Audit); }

    /// <summary>返回 false 表示预算耗尽，调用方必须走启发式。</summary>
    public bool TryConsumeBudget(double now)
    {
        if (now - _windowStart >= _budget.LlmWindowSeconds)
        { _windowStart = now; _windowCalls = 0; }
        if (_windowCalls >= _budget.MaxLlmCallsPerWindow) { Log("budget_exhausted"); return false; }
        _windowCalls++; _totalCalls++;
        return true;
    }

    /// <summary>确定性启发式：与 LLM 无关，回放必一致。</summary>
    public static NpcRtGuardResult Heuristic(NpcRtActor actor,
        IReadOnlyList<NpcRtArbitration> ranked, NpcRtPercept? nearestThreat, NpcRtRandom rng)
    {
        var top = ranked.Count > 0 ? ranked[0] : new NpcRtArbitration(NpcRtIntentNames.Idle, "Growth", 0.2f, "empty");
        var res = new NpcRtGuardResult
        { Kind = top.Kind, Priority = Math.Clamp(top.Score, 0f, 1f),
          Reason = top.Reason, Source = "heuristic", Accepted = true, Audit = "heuristic" };
        if (top.Kind == NpcRtIntentNames.Flee && nearestThreat.HasValue)
        {
            var away = actor.Position - nearestThreat.Value.Position; away.Y = 0f;
            res.TargetPosition = away.LengthSquared() > 1e-4f
                ? actor.Position + Vector3.Normalize(away) * 10f : actor.Position + new Vector3(10f, 0f, 0f);
        }
        else if (top.Kind is NpcRtIntentNames.Wander or NpcRtIntentNames.GoTo)
        {
            float a = rng.Range(0f, MathF.PI * 2f);
            res.TargetPosition = actor.Position + new Vector3(MathF.Cos(a) * 8f, 0f, MathF.Sin(a) * 8f);
        }
        return res;
    }

    private void Log(string line)
    {
        _audit.Add(line);
        if (_audit.Count > 256) _audit.RemoveRange(0, _audit.Count - 256);
    }

    public void Capture(NpcRtStateBag bag, string p)
    {
        bag.Put(p + ".winStart", _windowStart); bag.Put(p + ".winCalls", _windowCalls);
        bag.Put(p + ".total", _totalCalls); bag.Put(p + ".rejects", _totalRejects);
    }
    public void Restore(NpcRtStateBag bag, string p)
    {
        _windowStart = bag.GetDouble(p + ".winStart"); _windowCalls = bag.GetInt(p + ".winCalls");
        _totalCalls = bag.GetLong(p + ".total"); _totalRejects = bag.GetLong(p + ".rejects");
    }
}
