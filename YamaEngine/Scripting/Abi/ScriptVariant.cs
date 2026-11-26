using System;
using System.Collections.Generic;
using System.Globalization;

namespace YamaEngine.Scripting.Abi
{
    /// <summary>
    /// 脚本绑定 ABI 的值载体。所有跨边界数据都装箱为 <see cref="ScriptVariant"/>，
    /// 脚本层不得直接持有引擎内部类型或非托管内存（SCRIPTING_ABI_SPEC.md 硬约束 1、2）。
    /// </summary>
    public sealed class ScriptVariant
    {
        public static readonly ScriptVariant Null = new ScriptVariant(ScriptVariantKind.Null, null);

        public ScriptVariantKind Kind { get; }
        public object? Value { get; }

        private ScriptVariant(ScriptVariantKind kind, object? value)
        {
            Kind = kind;
            Value = value;
        }

        public static ScriptVariant From(bool value) => new ScriptVariant(ScriptVariantKind.Bool, value);
        public static ScriptVariant From(int value) => new ScriptVariant(ScriptVariantKind.Int32, value);
        public static ScriptVariant From(long value) => new ScriptVariant(ScriptVariantKind.Int64, value);
        public static ScriptVariant From(float value) => new ScriptVariant(ScriptVariantKind.Single, value);
        public static ScriptVariant From(double value) => new ScriptVariant(ScriptVariantKind.Double, value);
        public static ScriptVariant From(string value) => new ScriptVariant(ScriptVariantKind.String, value ?? string.Empty);
        public static ScriptVariant From(System.Numerics.Vector2 value) => new ScriptVariant(ScriptVariantKind.Vector2, value);
        public static ScriptVariant From(System.Numerics.Vector3 value) => new ScriptVariant(ScriptVariantKind.Vector3, value);
        public static ScriptVariant From(System.Numerics.Vector4 value) => new ScriptVariant(ScriptVariantKind.Vector4, value);
        public static ScriptVariant From(NodeRef value) => new ScriptVariant(ScriptVariantKind.NodeRef, value);
        public static ScriptVariant From(IReadOnlyList<ScriptVariant> value) => new ScriptVariant(ScriptVariantKind.Array, value ?? Array.Empty<ScriptVariant>());
        public static ScriptVariant From(IReadOnlyDictionary<string, ScriptVariant> value) => new ScriptVariant(ScriptVariantKind.Dictionary, value ?? new Dictionary<string, ScriptVariant>(StringComparer.Ordinal));

        /// <summary>把反射/脚本返回的普通 CLR 对象规整为 ABI 值；无法识别时保留为 Object。</summary>
        public static ScriptVariant FromObject(object? value)
        {
            if (value == null) return Null;
            if (value is ScriptVariant existing) return existing;
            if (value is bool b) return From(b);
            if (value is int i) return From(i);
            if (value is long l) return From(l);
            if (value is float f) return From(f);
            if (value is double d) return From(d);
            if (value is string s) return From(s);
            if (value is System.Numerics.Vector2 v2) return From(v2);
            if (value is System.Numerics.Vector3 v3) return From(v3);
            if (value is System.Numerics.Vector4 v4) return From(v4);
            if (value is NodeRef nr) return From(nr);
            if (value is IReadOnlyList<ScriptVariant> arr) return From(arr);
            if (value is IReadOnlyDictionary<string, ScriptVariant> map) return From(map);
            return new ScriptVariant(ScriptVariantKind.Object, value);
        }

        public bool TryGet<T>(out T value)
        {
            if (Value is T typed)
            {
                value = typed;
                return true;
            }
            value = default!;
            return false;
        }

        public bool AsBool(bool fallback = false) => Value is bool b ? b : fallback;

        public int AsInt32(int fallback = 0)
        {
            if (Value is int i) return i;
            if (Value is long l && l >= int.MinValue && l <= int.MaxValue) return (int)l;
            if (Value is float f) return (int)f;
            if (Value is double d) return (int)d;
            return fallback;
        }

        public double AsDouble(double fallback = 0.0)
        {
            if (Value is double d) return d;
            if (Value is float f) return f;
            if (Value is int i) return i;
            if (Value is long l) return l;
            return fallback;
        }

        public string AsString(string fallback = "") => Value as string ?? fallback;

        public override string ToString()
        {
            if (Kind == ScriptVariantKind.Null) return "null";
            return string.Format(CultureInfo.InvariantCulture, "{0}:{1}", Kind, Value);
        }
    }
}
