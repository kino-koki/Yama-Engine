using System;
using System.Collections.Generic;
using System.Reflection;

namespace YamaEngine.Scripting.Abi
{
    /// <summary>脚本实例的键值状态；热重载时按 key 迁移，不依赖实例引用（硬约束 3）。</summary>
    public sealed class PropertyBag
    {
        private readonly Dictionary<string, ScriptVariant> _values = new Dictionary<string, ScriptVariant>(StringComparer.Ordinal);
        private readonly object _gate = new object();

        public int Count { get { lock (_gate) return _values.Count; } }

        public void Set(string key, ScriptVariant value)
        {
            if (string.IsNullOrEmpty(key)) throw new ArgumentException("Property key must be non-empty.", nameof(key));
            lock (_gate) { _values[key] = value ?? ScriptVariant.Null; }
        }

        public ScriptVariant Get(string key, ScriptVariant fallback)
        {
            if (string.IsNullOrEmpty(key)) return fallback;
            lock (_gate) { return _values.TryGetValue(key, out var v) ? v : fallback; }
        }

        public bool TryGet(string key, out ScriptVariant value)
        {
            if (string.IsNullOrEmpty(key)) { value = ScriptVariant.Null; return false; }
            lock (_gate) { return _values.TryGetValue(key, out value); }
        }

        public bool Remove(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            lock (_gate) { return _values.Remove(key); }
        }

        public IReadOnlyList<string> Keys
        {
            get { lock (_gate) { return new List<string>(_values.Keys).AsReadOnly(); } }
        }

        public IDictionary<string, ScriptVariant> Snapshot()
        {
            lock (_gate) { return new Dictionary<string, ScriptVariant>(_values, StringComparer.Ordinal); }
        }

        public PropertyBag Clone()
        {
            var copy = new PropertyBag();
            foreach (var kv in Snapshot()) copy.Set(kv.Key, kv.Value);
            return copy;
        }

        /// <summary>把旧状态迁移到新实例：只补新实例里尚不存在的键，返回迁移条数。</summary>
        public static int Migrate(PropertyBag previous, PropertyBag current)
        {
            if (previous == null || current == null) return 0;
            int moved = 0;
            foreach (var kv in previous.Snapshot())
            {
                if (!current.TryGet(kv.Key, out _))
                {
                    current.Set(kv.Key, kv.Value);
                    moved++;
                }
            }
            return moved;
        }

        /// <summary>反射抓取带 [ScriptSerializeField] 的字段与可读属性。</summary>
        public static PropertyBag CaptureFrom(object instance)
        {
            if (instance == null) throw new ArgumentNullException(nameof(instance));
            var bag = new PropertyBag();
            var type = instance.GetType();
            const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            foreach (var field in type.GetFields(Flags))
            {
                var attr = field.GetCustomAttribute<ScriptSerializeFieldAttribute>();
                if (attr == null) continue;
                var key = string.IsNullOrEmpty(attr.Key) ? field.Name : attr.Key;
                bag.Set(key, ScriptVariant.FromObject(field.GetValue(instance)));
            }
            foreach (var prop in type.GetProperties(Flags))
            {
                if (!prop.CanRead) continue;
                var attr = prop.GetCustomAttribute<ScriptSerializeFieldAttribute>();
                if (attr == null) continue;
                var key = string.IsNullOrEmpty(attr.Key) ? prop.Name : attr.Key;
                try { bag.Set(key, ScriptVariant.FromObject(prop.GetValue(instance))); }
                catch (TargetInvocationException) { /* getter 抛错时跳过该键 */ }
            }
            return bag;
        }

        /// <summary>把状态写回带 [ScriptSerializeField] 的字段与可写属性。</summary>
        public int ApplyTo(object instance)
        {
            if (instance == null) throw new ArgumentNullException(nameof(instance));
            int applied = 0;
            var type = instance.GetType();
            const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var snapshot = Snapshot();
            foreach (var field in type.GetFields(Flags))
            {
                var attr = field.GetCustomAttribute<ScriptSerializeFieldAttribute>();
                if (attr == null) continue;
                var key = string.IsNullOrEmpty(attr.Key) ? field.Name : attr.Key;
                if (!snapshot.TryGetValue(key, out var v)) continue;
                try { field.SetValue(instance, Coerce(v, field.FieldType)); applied++; }
                catch (ArgumentException) { /* 类型不兼容时跳过 */ }
            }
            foreach (var prop in type.GetProperties(Flags))
            {
                if (!prop.CanWrite) continue;
                var attr = prop.GetCustomAttribute<ScriptSerializeFieldAttribute>();
                if (attr == null) continue;
                var key = string.IsNullOrEmpty(attr.Key) ? prop.Name : attr.Key;
                if (!snapshot.TryGetValue(key, out var v)) continue;
                try { prop.SetValue(instance, Coerce(v, prop.PropertyType)); applied++; }
                catch (ArgumentException) { }
                catch (TargetInvocationException) { }
            }
            return applied;
        }

        private static object? Coerce(ScriptVariant value, Type target)
        {
            if (target == typeof(ScriptVariant)) return value;
            if (target == typeof(int)) return value.AsInt32();
            if (target == typeof(long)) return (long)value.AsDouble();
            if (target == typeof(float)) return (float)value.AsDouble();
            if (target == typeof(double)) return value.AsDouble();
            if (target == typeof(bool)) return value.AsBool();
            if (target == typeof(string)) return value.AsString();
            return value.Value;
        }
    }
}
