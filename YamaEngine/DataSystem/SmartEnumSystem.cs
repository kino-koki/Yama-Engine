using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace YamaEngine.DataSystem
{
    // ====================================================================
    // 智能枚举系统（SmartEnumSystem）
    // ====================================================================
    // 提供强类型枚举，支持显示名称、描述、标签、元数据。
    // 全局注册表、按名称/值查找、验证和序列化。
    // ====================================================================

    /// <summary>
    /// 智能枚举泛型基类。所有智能枚举必须继承此类。
    /// 用法：public class WeaponType : SmartEnum&lt;WeaponType&gt; { ... }
    /// </summary>
    /// <typeparam name="T">枚举子类自身类型</typeparam>
    public abstract class SmartEnum<T> where T : SmartEnum<T>
    {
        private static readonly Dictionary<int, T> _byValue = new();
        private static readonly Dictionary<string, T> _byName = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, T> _byDisplayName = new(StringComparer.OrdinalIgnoreCase);
        private static bool _initialized;

        /// <summary>枚举值（整数标识）</summary>
        public int Value { get; }

        /// <summary>显示名称（用于 UI 显示）</summary>
        public string DisplayName { get; }

        /// <summary>描述信息</summary>
        public string Description { get; }

        /// <summary>标签集合（用于分类和搜索）</summary>
        public List<string> Tags { get; }

        /// <summary>自定义元数据</summary>
        public Dictionary<string, object> Metadata { get; }

        /// <summary>内部名称（代码中使用的标识符名）</summary>
        public string Name { get; }

        protected SmartEnum(int value, string name) : this(value, name, name) { }

        protected SmartEnum(int value, string name, string displayName)
        {
            Value = value;
            Name = name ?? throw new ArgumentNullException(nameof(name));
            DisplayName = displayName ?? name;
            Description = "";
            Tags = new List<string>();
            Metadata = new Dictionary<string, object>();

            if (!_initialized)
                InitializeRegistry();
        }

        private static void InitializeRegistry()
        {
            _initialized = true;
            var type = typeof(T);
            var fields = type.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);

            foreach (var field in fields)
            {
                if (field.GetValue(null) is T instance)
                {
                    _byValue[instance.Value] = instance;
                    _byName[instance.Name] = instance;
                    _byDisplayName[instance.DisplayName] = instance;
                }
            }
        }

        /// <summary>确保注册表已初始化</summary>
        public static void EnsureInitialized()
        {
            if (!_initialized) InitializeRegistry();
        }

        // ── 查找 ──

        /// <summary>按值查找</summary>
        public static T? FromValue(int value)
        {
            EnsureInitialized();
            return _byValue.TryGetValue(value, out var result) ? result : null;
        }

        /// <summary>按名称查找</summary>
        public static T? FromName(string name)
        {
            EnsureInitialized();
            return _byName.TryGetValue(name, out var result) ? result : null;
        }

        /// <summary>按显示名称查找</summary>
        public static T? FromDisplayName(string displayName)
        {
            EnsureInitialized();
            return _byDisplayName.TryGetValue(displayName, out var result) ? result : null;
        }

        /// <summary>尝试按值查找</summary>
        public static bool TryFromValue(int value, out T? result)
        {
            EnsureInitialized();
            return _byValue.TryGetValue(value, out result);
        }

        /// <summary>尝试按名称查找</summary>
        public static bool TryFromName(string name, out T? result)
        {
            EnsureInitialized();
            return _byName.TryGetValue(name, out result);
        }

        /// <summary>尝试按显示名称查找</summary>
        public static bool TryFromDisplayName(string displayName, out T? result)
        {
            EnsureInitialized();
            return _byDisplayName.TryGetValue(displayName, out result);
        }

        /// <summary>解析（按名称或值字符串）</summary>
        public static T? Parse(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return null;
            if (int.TryParse(input, out int value))
                return FromValue(value);
            return FromName(input);
        }

        /// <summary>尝试解析</summary>
        public static bool TryParse(string input, out T? result)
        {
            result = null;
            if (string.IsNullOrWhiteSpace(input)) return false;
            if (int.TryParse(input, out int value))
                return TryFromValue(value, out result);
            return TryFromName(input, out result);
        }

        // ── 枚举列表 ──

        /// <summary>获取所有已注册的枚举值</summary>
        public static IReadOnlyList<T> GetAll()
        {
            EnsureInitialized();
            return _byValue.Values.ToList();
        }

        /// <summary>获取所有枚举值的值列表</summary>
        public static List<int> GetAllValues() => GetAll().Select(e => e.Value).ToList();

        /// <summary>获取所有枚举值的名称列表</summary>
        public static List<string> GetAllNames() => GetAll().Select(e => e.Name).ToList();

        /// <summary>获取所有枚举值的显示名称列表</summary>
        public static List<string> GetAllDisplayNames() => GetAll().Select(e => e.DisplayName).ToList();

        /// <summary>获取枚举值数量</summary>
        public static int Count => GetAll().Count;

        // ── 验证 ──

        /// <summary>判断指定值是否是有效的枚举值</summary>
        public static bool IsValid(int value)
        {
            EnsureInitialized();
            return _byValue.ContainsKey(value);
        }

        /// <summary>判断指定名称是否是有效的枚举值</summary>
        public static bool IsValidName(string name)
        {
            EnsureInitialized();
            return _byName.ContainsKey(name);
        }

        /// <summary>判断指定值是否在指定范围内</summary>
        public static bool InRange(int value, int minValue, int maxValue)
        {
            return value >= minValue && value <= maxValue && IsValid(value);
        }

        /// <summary>获取最小值</summary>
        public static int MinValue => GetAll().Min(e => e.Value);

        /// <summary>获取最大值</summary>
        public static int MaxValue => GetAll().Max(e => e.Value);

        // ── 序列化 ──

        /// <summary>序列化为名称字符串</summary>
        public string ToNameString() => Name;

        /// <summary>序列化为值字符串</summary>
        public string ToValueString() => Value.ToString();

        /// <summary>从名称字符串反序列化</summary>
        public static T? FromNameString(string nameStr) => FromName(nameStr);

        /// <summary>从值字符串反序列化</summary>
        public static T? FromValueString(string valueStr)
        {
            if (int.TryParse(valueStr, out int value))
                return FromValue(value);
            return null;
        }

        // ── 标签查找 ──

        /// <summary>按标签查找枚举值</summary>
        public static List<T> GetByTag(string tag)
        {
            return GetAll().Where(e => e.Tags.Contains(tag)).ToList();
        }

        /// <summary>按标签过滤</summary>
        public static List<T> FilterByTags(params string[] tags)
        {
            var tagSet = new HashSet<string>(tags);
            return GetAll().Where(e => e.Tags.Any(t => tagSet.Contains(t))).ToList();
        }

        // ── 重写 ──

        public override bool Equals(object? obj)
        {
            if (obj is T other) return Value == other.Value;
            return false;
        }

        public override int GetHashCode() => Value.GetHashCode();

        public override string ToString() => DisplayName;

        public static bool operator ==(SmartEnum<T>? a, SmartEnum<T>? b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a is null || b is null) return false;
            return a.Value == b.Value;
        }

        public static bool operator !=(SmartEnum<T>? a, SmartEnum<T>? b) => !(a == b);

        public static explicit operator int(SmartEnum<T> e) => e.Value;
    }

    // ====================================================================
    // 智能枚举扩展方法
    // ====================================================================

    /// <summary>
    /// SmartEnum 扩展方法。提供 DisplayName、Description、Tags、ToList 等便捷方法。
    /// </summary>
    public static class SmartEnumExtensions
    {
        /// <summary>获取显示名称</summary>
        public static string GetDisplayName<T>(this SmartEnum<T> e) where T : SmartEnum<T> =>
            e.DisplayName;

        /// <summary>获取描述</summary>
        public static string GetDescription<T>(this SmartEnum<T> e) where T : SmartEnum<T> =>
            e.Description;

        /// <summary>获取标签列表</summary>
        public static IReadOnlyList<string> GetTags<T>(this SmartEnum<T> e) where T : SmartEnum<T> =>
            e.Tags;

        /// <summary>判断是否有指定标签</summary>
        public static bool HasTag<T>(this SmartEnum<T> e, string tag) where T : SmartEnum<T> =>
            e.Tags.Contains(tag);

        /// <summary>获取所有枚举值列表</summary>
        public static List<T> ToList<T>() where T : SmartEnum<T> => SmartEnum<T>.GetAll().ToList();

        /// <summary>获取下拉列表项（用于 UI）</summary>
        public static List<(int value, string displayName)> ToDropdownItems<T>()
            where T : SmartEnum<T> =>
            SmartEnum<T>.GetAll().Select(e => (e.Value, e.DisplayName)).ToList();

        /// <summary>获取键值对字典</summary>
        public static Dictionary<int, string> ToDictionary<T>()
            where T : SmartEnum<T> =>
            SmartEnum<T>.GetAll().ToDictionary(e => e.Value, e => e.DisplayName);

        /// <summary>获取元数据值</summary>
        public static TMeta? GetMeta<T, TMeta>(this SmartEnum<T> e, string key)
            where T : SmartEnum<T> where TMeta : struct
        {
            if (e.Metadata.TryGetValue(key, out var value) && value is TMeta typed)
                return typed;
            return null;
        }

        /// <summary>获取元数据值（引用类型）</summary>
        public static TMeta? GetMetaRef<T, TMeta>(this SmartEnum<T> e, string key)
            where T : SmartEnum<T> where TMeta : class
        {
            if (e.Metadata.TryGetValue(key, out var value) && value is TMeta typed)
                return typed;
            return null;
        }
    }

    // ====================================================================
    // 全局智能枚举注册表
    // ====================================================================

    /// <summary>
    /// 全局智能枚举注册表。管理所有 SmartEnum 类型及其值。
    /// 提供跨类型的枚举查找和统计。
    /// </summary>
    public static class SmartEnumRegistry
    {
        private static readonly Dictionary<Type, object> _enumTypes = new();

        /// <summary>注册一个智能枚举类型</summary>
        public static void RegisterEnumType<T>() where T : SmartEnum<T>
        {
            var type = typeof(T);
            if (!_enumTypes.ContainsKey(type))
            {
                SmartEnum<T>.EnsureInitialized();
                _enumTypes[type] = type;
            }
        }

        /// <summary>获取指定类型的所有枚举值</summary>
        public static List<T> GetValues<T>() where T : SmartEnum<T> => SmartEnum<T>.GetAll().ToList();

        /// <summary>按类型名获取枚举值列表</summary>
        public static IReadOnlyList<object>? GetValues(string typeName)
        {
            var type = _enumTypes.Keys.FirstOrDefault(t => t.Name == typeName);
            if (type == null) return null;
            var getAllMethod = type.GetMethod("GetAll", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            return getAllMethod?.Invoke(null, null) as IReadOnlyList<object>;
        }

        /// <summary>获取所有已注册的枚举类型</summary>
        public static IReadOnlyList<Type> GetRegisteredTypes() => _enumTypes.Keys.ToList();

        /// <summary>按值字符串解析指定类型的枚举值</summary>
        public static object? Parse(Type enumType, string valueStr)
        {
            var parseMethod = enumType.GetMethod("Parse", new[] { typeof(string) });
            return parseMethod?.Invoke(null, new object[] { valueStr });
        }
    }
}
