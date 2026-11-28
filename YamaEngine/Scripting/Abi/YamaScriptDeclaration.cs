using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Text.Json;

namespace YamaEngine.Scripting.Abi
{
    /// <summary>
    /// YAMA 声明式脚本绑定：把 Content/Scripts/*.yama 的 JSON 声明还原成
    /// 已实现的 L2 绑定 ABI 对象（ScriptBinding / PropertyBag / NodeRef）。
    ///
    /// 诚实边界：这是**声明层**，不是 VM、不是 DSL 解释器、不执行脚本字节码。
    /// 属性只允许 SCRIPTING_ABI_SPEC.md 硬约束 1 的 variant 子集；
    /// 信号只声明连接点，实际 handler 由宿主接线；生命周期只对真正实现
    /// IScriptLifecycle 的实例注册，缺一即 fail-closed。
    /// 不改 SceneSerializer 的强类型组件路径，零回归。
    /// </summary>
    public sealed class YamaScriptDeclaration
    {
        public const string SchemaId = "yama.script-binding.v1";

        private static readonly HashSet<string> AllowedKinds = new HashSet<string>(
            new[] { "bool", "int32", "int64", "single", "double", "string",
                    "vector2", "vector3", "vector4" }, StringComparer.Ordinal);

        private static readonly HashSet<string> AllowedHooks = new HashSet<string>(
            new[] { "ready", "process", "physics", "destroy" }, StringComparer.Ordinal);

        public string Schema { get; private set; } = string.Empty;
        public string TypeName { get; private set; } = string.Empty;
        public IReadOnlyDictionary<string, ScriptVariant> Properties { get; private set; }
            = new Dictionary<string, ScriptVariant>(StringComparer.Ordinal);
        public IReadOnlyList<string> DeclaredSignals { get; private set; }
            = Array.Empty<string>();
        public IReadOnlyList<string> LifecycleHooks { get; private set; }
            = Array.Empty<string>();
        public string Note { get; private set; } = string.Empty;

        private YamaScriptDeclaration() { }

        /// <summary>严格解析；任何不合法都返回 false 并给出原因，绝不吞异常。</summary>
        public static bool TryParse(string json, out YamaScriptDeclaration? declaration,
                                    out string? error)
        {
            declaration = null;
            error = null;
            if (string.IsNullOrWhiteSpace(json))
            {
                error = "声明为空";
                return false;
            }
            try
            {
                using var doc = JsonDocument.Parse(json);
                var r = doc.RootElement;
                var d = new YamaScriptDeclaration();
                d.Schema = Str(r, "schema");
                if (d.Schema != SchemaId)
                {
                    error = "schema 不是 " + SchemaId;
                    return false;
                }
                d.TypeName = Str(r, "typeName");
                if (d.TypeName.Length == 0)
                {
                    error = "typeName 缺失";
                    return false;
                }
                d.Note = Str(r, "note");

                var props = new Dictionary<string, ScriptVariant>(StringComparer.Ordinal);
                if (!r.TryGetProperty("properties", out var pe)
                    || pe.ValueKind != JsonValueKind.Object)
                {
                    error = "properties 缺失或不是对象";
                    return false;
                }
                foreach (var kv in pe.EnumerateObject())
                {
                    if (kv.Value.ValueKind != JsonValueKind.Object)
                    {
                        error = "属性值不是对象: " + kv.Name;
                        return false;
                    }
                    string kind = Str(kv.Value, "kind");
                    if (!AllowedKinds.Contains(kind))
                    {
                        error = "属性 kind 不在白名单: " + kv.Name + " / " + kind;
                        return false;
                    }
                    if (!TryReadVariant(kind, kv.Value, out ScriptVariant value, out string? why))
                    {
                        error = "属性值非法: " + kv.Name + " / " + why;
                        return false;
                    }
                    props[kv.Name] = value;
                }
                d.Properties = props;

                var sigs = new List<string>();
                if (r.TryGetProperty("signals", out var se))
                {
                    if (se.ValueKind != JsonValueKind.Array)
                    {
                        error = "signals 不是数组";
                        return false;
                    }
                    foreach (var x in se.EnumerateArray())
                    {
                        if (x.ValueKind != JsonValueKind.String
                            || string.IsNullOrEmpty(x.GetString()))
                        {
                            error = "signals 含非法项";
                            return false;
                        }
                        sigs.Add(x.GetString()!);
                    }
                }
                d.DeclaredSignals = sigs.AsReadOnly();

                var hooks = new List<string>();
                if (r.TryGetProperty("lifecycle", out var le))
                {
                    if (le.ValueKind != JsonValueKind.Array)
                    {
                        error = "lifecycle 不是数组";
                        return false;
                    }
                    foreach (var x in le.EnumerateArray())
                    {
                        string h = x.ValueKind == JsonValueKind.String
                            ? (x.GetString() ?? string.Empty) : string.Empty;
                        if (!AllowedHooks.Contains(h))
                        {
                            error = "生命周期钩子不在白名单: " + h;
                            return false;
                        }
                        hooks.Add(h);
                    }
                }
                d.LifecycleHooks = hooks.AsReadOnly();
                declaration = d;
                return true;
            }
            catch (JsonException ex)
            {
                error = "JSON 非法: " + ex.Message;
                return false;
            }
        }

        private static bool TryReadVariant(string kind, JsonElement holder,
                                           out ScriptVariant value, out string? error)
        {
            value = ScriptVariant.Null;
            error = null;
            if (!holder.TryGetProperty("value", out var v))
            {
                error = "缺 value";
                return false;
            }
            switch (kind)
            {
                case "bool":
                    if (v.ValueKind != JsonValueKind.True && v.ValueKind != JsonValueKind.False)
                    { error = "bool 值类型不符"; return false; }
                    value = ScriptVariant.From(v.GetBoolean()); return true;
                case "int32":
                    if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out int i32))
                    { error = "int32 值类型不符"; return false; }
                    value = ScriptVariant.From(i32); return true;
                case "int64":
                    if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt64(out long i64))
                    { error = "int64 值类型不符"; return false; }
                    value = ScriptVariant.From(i64); return true;
                case "single":
                    if (v.ValueKind != JsonValueKind.Number || !v.TryGetSingle(out float f))
                    { error = "single 值类型不符"; return false; }
                    value = ScriptVariant.From(f); return true;
                case "double":
                    if (v.ValueKind != JsonValueKind.Number || !v.TryGetDouble(out double dbl))
                    { error = "double 值类型不符"; return false; }
                    value = ScriptVariant.From(dbl); return true;
                case "string":
                    if (v.ValueKind != JsonValueKind.String)
                    { error = "string 值类型不符"; return false; }
                    value = ScriptVariant.From(v.GetString() ?? string.Empty); return true;
                case "vector2":
                case "vector3":
                case "vector4":
                    if (v.ValueKind != JsonValueKind.Object)
                    { error = "向量值应为对象"; return false; }
                    double x = Num(v, "X"), y = Num(v, "Y");
                    if (double.IsNaN(x) || double.IsNaN(y))
                    { error = "向量缺 X/Y 数值分量"; return false; }
                    if (kind == "vector2")
                    { value = ScriptVariant.From(new Vector2((float)x, (float)y)); return true; }
                    double z = Num(v, "Z");
                    if (double.IsNaN(z))
                    { error = "向量缺 Z 数值分量"; return false; }
                    if (kind == "vector3")
                    { value = ScriptVariant.From(new Vector3((float)x, (float)y, (float)z)); return true; }
                    double w = Num(v, "W");
                    if (double.IsNaN(w))
                    { error = "向量缺 W 数值分量"; return false; }
                    value = ScriptVariant.From(new Vector4((float)x, (float)y, (float)z, (float)w));
                    return true;
                default:
                    error = "未知 kind";
                    return false;
            }
        }

        private static double Num(JsonElement e, string key)
            => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number
               && v.TryGetDouble(out double d) ? d : double.NaN;

        private static string Str(JsonElement e, string key)
            => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
               ? (v.GetString() ?? string.Empty) : string.Empty;

        /// <summary>
        /// 真正还原成 L2 绑定 ABI 对象：类型名未注册即拒绝（fail-closed），
        /// 节点由 NodeHandleTable 管句柄（脚本不持有原生指针），属性逐项灌入 PropertyBag。
        /// </summary>
        public ScriptBinding CreateBinding(NodeHandleTable handles, ScriptRegistry registry,
                                           object nodeTarget)
        {
            if (handles == null) throw new ArgumentNullException(nameof(handles));
            if (registry == null) throw new ArgumentNullException(nameof(registry));
            if (nodeTarget == null) throw new ArgumentNullException(nameof(nodeTarget));
            if (registry.Find(TypeName) == null)
                throw new InvalidOperationException("未注册的脚本类型，拒绝还原: " + TypeName);
            NodeRef node = handles.Register(nodeTarget);
            var binding = new ScriptBinding(TypeName, node, handles, registry);
            foreach (var kv in Properties) binding.Properties.Set(kv.Key, kv.Value);
            return binding;
        }

        /// <summary>
        /// 只把真正实现 IScriptLifecycle 的实例注册进调度器；否则返回 false，
        /// 不伪造生命周期、不谎报已挂载。
        /// </summary>
        public bool TryRegisterLifecycle(LifecycleDispatcher dispatcher, object instance)
        {
            if (dispatcher == null) throw new ArgumentNullException(nameof(dispatcher));
            if (instance is IScriptLifecycle lifecycle)
            {
                dispatcher.Register(lifecycle);
                return true;
            }
            return false;
        }

        /// <summary>声明的信号是连接点，不是已建立的 handler；不得谎报已连接。</summary>
        public bool HasDeclaredSignal(string name)
        {
            foreach (var signal in DeclaredSignals)
                if (string.Equals(signal, name, StringComparison.Ordinal)) return true;
            return false;
        }
    }
}
