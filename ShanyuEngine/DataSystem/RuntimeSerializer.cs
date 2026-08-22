using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace ShanyuEngine.DataSystem
{
    // ====================================================================
    // 运行时序列化器（RuntimeSerializer）
    // ====================================================================
    // 类似 UE FProperty / Unity ISerializationCallbackReceiver。
    // 支持二进制/JSON 格式、循环引用检测、版本迁移、差异序列化。
    // ====================================================================

    /// <summary>
    /// 字段类型枚举。定义序列化器支持的所有基础类型。
    /// </summary>
    public enum FieldType
    {
        Unknown,
        Int8, Int16, Int32, Int64,
        UInt8, UInt16, UInt32, UInt64,
        Float, Double,
        Bool,
        String,
        Vector2, Vector3, Vector4,
        Quaternion,
        Matrix4x4,
        Color,     // Vector4 别名（R,G,B,A）
        Enum,
        Array,
        Map,       // Dictionary
        Set,       // HashSet
        ObjectRef, // 对象引用（运行时对象ID）
        AssetRef,  // 资产引用（GUID）
        Struct,
        Class,     // 多态类引用
        Nullable,
        Guid,
        DateTime,
        Byte       // byte[]
    }

    /// <summary>
    /// [Serialized] 属性标记。标记需要被序列化的字段或属性。
    /// 未标记的字段在 ISerializable 对象中默认不序列化。
    /// </summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, Inherited = true)]
    public sealed class SerializedAttribute : Attribute
    {
        public string? FieldName { get; }
        public int Order { get; set; }

        public SerializedAttribute() { }
        public SerializedAttribute(string? fieldName) => FieldName = fieldName;
    }

    /// <summary>
    /// [NonSerialized] 排除标记。标记不应被序列化的字段或属性。
    /// </summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, Inherited = true)]
    public sealed class NonSerializedAttribute : Attribute { }

    /// <summary>
    /// [Version] 序列化版本标记。标记类/结构的序列化版本号。
    /// 用于版本迁移和向前/后向兼容。
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false)]
    public sealed class VersionAttribute : Attribute
    {
        public int VersionNumber { get; }
        public string? MigrationCallback { get; set; }

        public VersionAttribute(int version)
        {
            VersionNumber = version < 0 ? 0 : version;
        }
    }

    /// <summary>
    /// 可序列化接口。实现此接口的对象支持自定义序列化/反序列化逻辑。
    /// </summary>
    public interface ISerializable
    {
        /// <summary>序列化到 PropertySerializer</summary>
        void Serialize(PropertySerializer serializer);
        /// <summary>从 PropertySerializer 反序列化</summary>
        void Deserialize(PropertySerializer serializer);
    }

    // ====================================================================
    // 属性序列化器（PropertySerializer）
    // ====================================================================

    /// <summary>
    /// 属性序列化器。支持所有基础类型、集合类型和嵌套对象的序列化/反序列化。
    /// 提供统一的 Read/Write API，底层可切换二进制或 JSON 实现。
    /// </summary>
    public class PropertySerializer
    {
        private readonly Dictionary<string, object?> _properties = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _writtenProperties = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<int, object> _objectRefTable = new(); // 循环引用处理
        private int _nextObjectId = 1;

        /// <summary>当前序列化版本</summary>
        public int SerializationVersion { get; set; }

        /// <summary>所有已写入/读取的属性名</summary>
        public IEnumerable<string> PropertyNames => _properties.Keys;

        // ── 写入方法 ──

        public void Write(string name, byte value) => WriteValue(name, value);
        public void Write(string name, sbyte value) => WriteValue(name, value);
        public void Write(string name, short value) => WriteValue(name, value);
        public void Write(string name, ushort value) => WriteValue(name, value);
        public void Write(string name, int value) => WriteValue(name, value);
        public void Write(string name, uint value) => WriteValue(name, value);
        public void Write(string name, long value) => WriteValue(name, value);
        public void Write(string name, ulong value) => WriteValue(name, value);
        public void Write(string name, float value) => WriteValue(name, value);
        public void Write(string name, double value) => WriteValue(name, value);
        public void Write(string name, bool value) => WriteValue(name, value);
        public void Write(string name, string? value) => WriteValue(name, value);
        public void Write(string name, Vector2 value) => WriteValue(name, value);
        public void Write(string name, Vector3 value) => WriteValue(name, value);
        public void Write(string name, Vector4 value) => WriteValue(name, value);
        public void Write(string name, Quaternion value) => WriteValue(name, value);
        public void Write(string name, Matrix4x4 value) => WriteValue(name, value);
        public void Write(string name, Guid value) => WriteValue(name, value);
        public void Write(string name, DateTime value) => WriteValue(name, value);
        public void Write(string name, Enum value) => WriteValue(name, value);
        public void Write(string name, byte[]? value) => WriteValue(name, value);

        /// <summary>写入对象引用（使用对象ID替代直接引用，处理循环引用）</summary>
        public void WriteObjectRef<T>(string name, T? obj) where T : class
        {
            if (obj == null)
            {
                WriteValue(name, -1);
                return;
            }
            int id = GetOrCreateObjectId(obj);
            WriteValue(name, id);
        }

        /// <summary>写入嵌套对象</summary>
        public void WriteObject(string name, ISerializable? obj)
        {
            if (obj == null)
            {
                WriteValue(name, false);
                return;
            }
            WriteValue(name, true);
            var nested = new PropertySerializer { SerializationVersion = SerializationVersion };
            obj.Serialize(nested);
            WriteValue($"{name}.__data", nested);
        }

        /// <summary>写入集合</summary>
        public void WriteArray<T>(string name, IList<T>? list)
        {
            WriteValue(name, list?.Count ?? 0);
            if (list == null) return;
            for (int i = 0; i < list.Count; i++)
            {
                WriteValue($"{name}[{i}]", list[i]);
            }
        }

        /// <summary>写入字典</summary>
        public void WriteMap<TKey, TValue>(string name, IDictionary<TKey, TValue>? dict)
            where TKey : notnull
        {
            WriteValue(name, dict?.Count ?? 0);
            if (dict == null) return;
            int idx = 0;
            foreach (var kvp in dict)
            {
                WriteValue($"{name}_k{idx}", kvp.Key);
                WriteValue($"{name}_v{idx}", kvp.Value);
                idx++;
            }
        }

        // ── 读取方法 ──

        public byte ReadByte(string name, byte defaultValue = 0) => ReadValue(name, defaultValue);
        public sbyte ReadSByte(string name, sbyte defaultValue = 0) => ReadValue(name, defaultValue);
        public short ReadInt16(string name, short defaultValue = 0) => ReadValue(name, defaultValue);
        public ushort ReadUInt16(string name, ushort defaultValue = 0) => ReadValue(name, defaultValue);
        public int ReadInt32(string name, int defaultValue = 0) => ReadValue(name, defaultValue);
        public uint ReadUInt32(string name, uint defaultValue = 0) => ReadValue(name, defaultValue);
        public long ReadInt64(string name, long defaultValue = 0) => ReadValue(name, defaultValue);
        public ulong ReadUInt64(string name, ulong defaultValue = 0) => ReadValue(name, defaultValue);
        public float ReadSingle(string name, float defaultValue = 0) => ReadValue(name, defaultValue);
        public double ReadDouble(string name, double defaultValue = 0) => ReadValue(name, defaultValue);
        public bool ReadBool(string name, bool defaultValue = false) => ReadValue(name, defaultValue);
        public string ReadString(string name, string defaultValue = "") => ReadValue(name, defaultValue);
        public Vector2 ReadVector2(string name, Vector2 defaultValue = default) => ReadValue(name, defaultValue);
        public Vector3 ReadVector3(string name, Vector3 defaultValue = default) => ReadValue(name, defaultValue);
        public Vector4 ReadVector4(string name, Vector4 defaultValue = default) => ReadValue(name, defaultValue);
        public Quaternion ReadQuaternion(string name, Quaternion defaultValue = default) => ReadValue(name, defaultValue);
        public Matrix4x4 ReadMatrix4x4(string name, Matrix4x4 defaultValue = default) => ReadValue(name, defaultValue);
        public Guid ReadGuid(string name, Guid defaultValue = default) => ReadValue(name, defaultValue);
        public DateTime ReadDateTime(string name, DateTime defaultValue = default) => ReadValue(name, defaultValue);
        public byte[] ReadBytes(string name, byte[]? defaultValue = null) => ReadValue(name, defaultValue ?? Array.Empty<byte>());

        /// <summary>读取 Enum（按名称或整数值）</summary>
        public TEnum ReadEnum<TEnum>(string name, TEnum defaultValue = default) where TEnum : struct, Enum
        {
            if (!_properties.TryGetValue(name, out var raw) || raw == null)
                return defaultValue;
            if (raw is TEnum typed) return typed;
            if (raw is string str && Enum.TryParse<TEnum>(str, true, out var parsed)) return parsed;
            if (raw is int i) return (TEnum)(object)i;
            return defaultValue;
        }

        /// <summary>读取对象引用（从对象ID还原）</summary>
        public T? ReadObjectRef<T>(string name, Func<int, T?> resolver) where T : class
        {
            int id = ReadInt32(name, -1);
            if (id < 0) return null;
            return resolver(id);
        }

        /// <summary>读取嵌套对象</summary>
        public void ReadObject(string name, ISerializable obj)
        {
            bool hasData = ReadBool(name, false);
            if (!hasData) return;
            var nested = ReadValue<PropertySerializer>($"{name}.__data");
            if (nested != null)
                obj.Deserialize(nested);
        }

        /// <summary>读取集合</summary>
        public List<T> ReadArray<T>(string name)
        {
            int count = ReadInt32(name, 0);
            var list = new List<T>(count);
            for (int i = 0; i < count; i++)
            {
                var val = ReadValue<T>($"{name}[{i}]");
                list.Add(val);
            }
            return list;
        }

        /// <summary>读取字典</summary>
        public Dictionary<TKey, TValue> ReadMap<TKey, TValue>(string name)
            where TKey : notnull
        {
            int count = ReadInt32(name, 0);
            var dict = new Dictionary<TKey, TValue>(count);
            for (int i = 0; i < count; i++)
            {
                var key = ReadValue<TKey>($"{name}_k{i}");
                var value = ReadValue<TValue>($"{name}_v{i}");
                dict[key] = value;
            }
            return dict;
        }

        // ── 内部实现 ──

        private void WriteValue(string name, object? value)
        {
            _properties[name] = value;
            _writtenProperties.Add(name);
        }

        private T ReadValue<T>(string name, T defaultValue = default!)
        {
            if (!_properties.TryGetValue(name, out var raw) || raw == null)
                return defaultValue;

            if (raw is T typed) return typed;

            // 尝试类型转换
            var targetType = typeof(T);
            var rawType = raw.GetType();

            // 数值类型之间的转换
            if (targetType != rawType && IsNumericType(targetType) && IsNumericType(rawType))
            {
                try { return (T)Convert.ChangeType(raw, targetType); }
                catch { return defaultValue; }
            }

            // 字符串 -> 基础类型
            if (raw is string str)
            {
                try
                {
                    if (targetType == typeof(int)) return (T)(object)int.Parse(str);
                    if (targetType == typeof(float)) return (T)(object)float.Parse(str);
                    if (targetType == typeof(double)) return (T)(object)double.Parse(str);
                    if (targetType == typeof(bool)) return (T)(object)bool.Parse(str);
                    if (targetType == typeof(Guid)) return (T)(object)Guid.Parse(str);
                    if (targetType == typeof(Vector2)) return (T)(object)ParseVector2(str);
                    if (targetType == typeof(Vector3)) return (T)(object)ParseVector3(str);
                    if (targetType == typeof(Vector4)) return (T)(object)ParseVector4(str);
                    if (targetType == typeof(Quaternion)) return (T)(object)ParseQuaternion(str);
                }
                catch { return defaultValue; }
            }

            return defaultValue;
        }

        private static bool IsNumericType(Type t) =>
            t == typeof(byte) || t == typeof(sbyte) || t == typeof(short) || t == typeof(ushort) ||
            t == typeof(int) || t == typeof(uint) || t == typeof(long) || t == typeof(ulong) ||
            t == typeof(float) || t == typeof(double);

        private static Vector2 ParseVector2(string s)
        {
            var p = s.Split(',');
            return new Vector2(
                p.Length > 0 ? float.Parse(p[0]) : 0,
                p.Length > 1 ? float.Parse(p[1]) : 0);
        }

        private static Vector3 ParseVector3(string s)
        {
            var p = s.Split(',');
            return new Vector3(
                p.Length > 0 ? float.Parse(p[0]) : 0,
                p.Length > 1 ? float.Parse(p[1]) : 0,
                p.Length > 2 ? float.Parse(p[2]) : 0);
        }

        private static Vector4 ParseVector4(string s)
        {
            var p = s.Split(',');
            return new Vector4(
                p.Length > 0 ? float.Parse(p[0]) : 0,
                p.Length > 1 ? float.Parse(p[1]) : 0,
                p.Length > 2 ? float.Parse(p[2]) : 0,
                p.Length > 3 ? float.Parse(p[3]) : 0);
        }

        private static Quaternion ParseQuaternion(string s)
        {
            var p = s.Split(',');
            return new Quaternion(
                p.Length > 0 ? float.Parse(p[0]) : 0,
                p.Length > 1 ? float.Parse(p[1]) : 0,
                p.Length > 2 ? float.Parse(p[2]) : 0,
                p.Length > 3 ? float.Parse(p[3]) : 1);
        }

        // 循环引用：对象 -> ID
        private int GetOrCreateObjectId(object obj)
        {
            int hash = ShanyuRuntimeHelpers.GetObjectHashCode(obj);
            if (!_objectRefTable.TryGetValue(hash, out var entry))
            {
                entry = new { Id = _nextObjectId++, Obj = obj };
                _objectRefTable[hash] = entry;
            }
            return ((dynamic)entry).Id;
        }

        /// <summary>获取所有属性的原始字典（供底层序列化器使用）</summary>
        internal Dictionary<string, object?> GetProperties() => _properties;

        /// <summary>设置属性（供底层反序列化器使用）</summary>
        internal void SetProperties(Dictionary<string, object?> props) => _properties.Clear();
    }

    // ====================================================================
    // 二进制序列化器（BinarySerializer）
    // ====================================================================

    /// <summary>
    /// 二进制格式序列化器。紧凑、版本兼容、支持前向/后向兼容。
    /// 每个字段写入类型标记 + 字段名 + 值，未知字段可跳过。
    /// </summary>
    public static class BinarySerializer
    {
        private const byte MarkerInt8 = 1;
        private const byte MarkerInt16 = 2;
        private const byte MarkerInt32 = 3;
        private const byte MarkerInt64 = 4;
        private const byte MarkerUInt8 = 5;
        private const byte MarkerUInt16 = 6;
        private const byte MarkerUInt32 = 7;
        private const byte MarkerUInt64 = 8;
        private const byte MarkerFloat = 9;
        private const byte MarkerDouble = 10;
        private const byte MarkerBool = 11;
        private const byte MarkerString = 12;
        private const byte MarkerVector2 = 13;
        private const byte MarkerVector3 = 14;
        private const byte MarkerVector4 = 15;
        private const byte MarkerQuaternion = 16;
        private const byte MarkerMatrix4x4 = 17;
        private const byte MarkerGuid = 18;
        private const byte MarkerDateTime = 19;
        private const byte MarkerEnum = 20;
        private const byte MarkerArray = 21;
        private const byte MarkerMap = 22;
        private const byte MarkerBytes = 23;
        private const byte MarkerNull = 0xFF;
        private const int CurrentFormatVersion = 1;

        /// <summary>
        /// 将 ISerializable 对象序列化为二进制字节数组。
        /// </summary>
        public static byte[] Serialize(ISerializable obj)
        {
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms, Encoding.UTF8);

            // 写入格式头
            writer.Write("SHY_BIN");  // Magic
            writer.Write(CurrentFormatVersion);

            // 序列化类型名
            writer.Write(obj.GetType().AssemblyQualifiedName ?? obj.GetType().FullName ?? "");

            // 序列化属性
            var serializer = new PropertySerializer();
            obj.Serialize(serializer);
            WriteProperties(writer, serializer.GetProperties());

            return ms.ToArray();
        }

        /// <summary>
        /// 将 ISerializable 对象序列化为 PropertySerializer 中已有的属性。
        /// </summary>
        public static byte[] SerializeProperties(PropertySerializer serializer)
        {
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms, Encoding.UTF8);
            writer.Write("SHY_BIN");
            writer.Write(CurrentFormatVersion);
            WriteProperties(writer, serializer.GetProperties());
            return ms.ToArray();
        }

        /// <summary>
        /// 从二进制字节数组反序列化为 ISerializable 对象。
        /// </summary>
        public static T? Deserialize<T>(byte[] data) where T : ISerializable, new()
        {
            using var ms = new MemoryStream(data);
            using var reader = new BinaryReader(ms, Encoding.UTF8);

            // 读取格式头
            var magic = reader.ReadString();
            if (magic != "SHY_BIN")
                throw new FormatException("Invalid binary format: magic mismatch.");

            int version = reader.ReadInt32();
            string typeName = reader.ReadString();

            var props = ReadProperties(reader);

            var obj = new T();
            var serializer = new PropertySerializer
            {
                SerializationVersion = version
            };
            // 注入属性到 serializer
            foreach (var kvp in props)
                serializer.GetProperties()[kvp.Key] = kvp.Value;

            obj.Deserialize(serializer);
            return obj;
        }

        /// <summary>
        /// 从二进制字节数组反序列化为 PropertySerializer。
        /// </summary>
        public static PropertySerializer DeserializeProperties(byte[] data)
        {
            using var ms = new MemoryStream(data);
            using var reader = new BinaryReader(ms, Encoding.UTF8);

            var magic = reader.ReadString();
            if (magic != "SHY_BIN")
                throw new FormatException("Invalid binary format: magic mismatch.");

            int version = reader.ReadInt32();
            if (version > 1)
                reader.ReadString(); // 跳过类型名（兼容旧格式）

            var props = ReadProperties(reader);

            var serializer = new PropertySerializer
            {
                SerializationVersion = version
            };
            foreach (var kvp in props)
                serializer.GetProperties()[kvp.Key] = kvp.Value;

            return serializer;
        }

        private static void WriteProperties(BinaryWriter writer, Dictionary<string, object?> props)
        {
            writer.Write(props.Count);
            foreach (var kvp in props)
            {
                writer.Write(kvp.Key);
                WriteValue(writer, kvp.Value);
            }
        }

        private static void WriteValue(BinaryWriter writer, object? value)
        {
            if (value == null)
            {
                writer.Write(MarkerNull);
                return;
            }

            switch (value)
            {
                case byte b: writer.Write(MarkerInt8); writer.Write(b); break;
                case sbyte sb: writer.Write(MarkerInt8); writer.Write(sb); break;
                case short s: writer.Write(MarkerInt16); writer.Write(s); break;
                case ushort us: writer.Write(MarkerUInt16); writer.Write(us); break;
                case int i: writer.Write(MarkerInt32); writer.Write(i); break;
                case uint ui: writer.Write(MarkerUInt32); writer.Write(ui); break;
                case long l: writer.Write(MarkerInt64); writer.Write(l); break;
                case ulong ul: writer.Write(MarkerUInt64); writer.Write(ul); break;
                case float f: writer.Write(MarkerFloat); writer.Write(f); break;
                case double d: writer.Write(MarkerDouble); writer.Write(d); break;
                case bool b: writer.Write(MarkerBool); writer.Write(b); break;
                case string s: writer.Write(MarkerString); writer.Write(s); break;
                case Vector2 v2: writer.Write(MarkerVector2); writer.Write(v2.X); writer.Write(v2.Y); break;
                case Vector3 v3: writer.Write(MarkerVector3); writer.Write(v3.X); writer.Write(v3.Y); writer.Write(v3.Z); break;
                case Vector4 v4: writer.Write(MarkerVector4); writer.Write(v4.X); writer.Write(v4.Y); writer.Write(v4.Z); writer.Write(v4.W); break;
                case Quaternion q: writer.Write(MarkerQuaternion); writer.Write(q.X); writer.Write(q.Y); writer.Write(q.Z); writer.Write(q.W); break;
                case System.Numerics.Matrix4x4 m:
                    writer.Write(MarkerMatrix4x4);
                    for (int i = 0; i < 16; i++) writer.Write(m.GetRow(i / 4)[i % 4]);
                    break;
                case Guid g: writer.Write(MarkerGuid); writer.Write(g.ToByteArray()); break;
                case DateTime dt: writer.Write(MarkerDateTime); writer.Write(dt.ToBinary()); break;
                case Enum e:
                    writer.Write(MarkerEnum);
                    writer.Write(e.GetType().FullName ?? "");
                    writer.Write(Convert.ToInt32(e));
                    break;
                case byte[] arr:
                    writer.Write(MarkerBytes);
                    writer.Write(arr.Length);
                    writer.Write(arr);
                    break;
                case IList list:
                    writer.Write(MarkerArray);
                    writer.Write(list.Count);
                    foreach (var item in list) WriteValue(writer, item);
                    break;
                case IDictionary dict:
                    writer.Write(MarkerMap);
                    writer.Write(dict.Count);
                    foreach (DictionaryEntry entry in dict)
                    {
                        WriteValue(writer, entry.Key);
                        WriteValue(writer, entry.Value);
                    }
                    break;
                default:
                    // 尝试序列化为字符串
                    writer.Write(MarkerString);
                    writer.Write(value.ToString() ?? "");
                    break;
            }
        }

        private static Dictionary<string, object?> ReadProperties(BinaryReader reader)
        {
            int count = reader.ReadInt32();
            var props = new Dictionary<string, object?>(count);
            for (int i = 0; i < count; i++)
            {
                string name = reader.ReadString();
                props[name] = ReadValue(reader);
            }
            return props;
        }

        private static object? ReadValue(BinaryReader reader)
        {
            if (reader.BaseStream.Position >= reader.BaseStream.Length)
                return null;

            byte marker = reader.ReadByte();

            return marker switch
            {
                MarkerNull => null,
                MarkerInt8 => reader.ReadSByte(),
                MarkerInt16 => reader.ReadInt16(),
                MarkerInt32 => reader.ReadInt32(),
                MarkerInt64 => reader.ReadInt64(),
                MarkerUInt8 => reader.ReadByte(),
                MarkerUInt16 => reader.ReadUInt16(),
                MarkerUInt32 => reader.ReadUInt32(),
                MarkerUInt64 => reader.ReadUInt64(),
                MarkerFloat => reader.ReadSingle(),
                MarkerDouble => reader.ReadDouble(),
                MarkerBool => reader.ReadBoolean(),
                MarkerString => reader.ReadString(),
                MarkerVector2 => new Vector2(reader.ReadSingle(), reader.ReadSingle()),
                MarkerVector3 => new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
                MarkerVector4 => new Vector4(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
                MarkerQuaternion => new Quaternion(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
                MarkerMatrix4x4 => ReadMatrix4x4(reader),
                MarkerGuid => new Guid(reader.ReadBytes(16)),
                MarkerDateTime => DateTime.FromBinary(reader.ReadInt64()),
                MarkerEnum => reader.ReadInt32(), // 简化：直接返回整数
                MarkerBytes => reader.ReadBytes(reader.ReadInt32()),
                MarkerArray => ReadArray(reader),
                MarkerMap => ReadMap(reader),
                _ => null // 未知标记，跳过
            };
        }

        private static System.Numerics.Matrix4x4 ReadMatrix4x4(BinaryReader reader)
        {
            var m = System.Numerics.Matrix4x4.Identity;
            for (int i = 0; i < 16; i++)
            {
                float val = reader.ReadSingle();
                int row = i / 4, col = i % 4;
                m.SetRow(row, i % 4 == 0 ? new Vector4(val, m.M12, m.M13, m.M14)
                    : i % 4 == 1 ? new Vector4(m.M11, val, m.M13, m.M14)
                    : i % 4 == 2 ? new Vector4(m.M11, m.M12, val, m.M14)
                    : new Vector4(m.M11, m.M12, m.M13, val));
            }
            return m;
        }

        private static object ReadArray(BinaryReader reader)
        {
            int count = reader.ReadInt32();
            var list = new List<object?>(count);
            for (int i = 0; i < count; i++)
                list.Add(ReadValue(reader));
            return list;
        }

        private static object ReadMap(BinaryReader reader)
        {
            int count = reader.ReadInt32();
            var dict = new Dictionary<object, object?>(count);
            for (int i = 0; i < count; i++)
            {
                var key = ReadValue(reader);
                var value = ReadValue(reader);
                if (key != null) dict[key] = value;
            }
            return dict;
        }
    }

    // ====================================================================
    // JSON 序列化器（JsonSerializer）
    // ====================================================================

    /// <summary>
    /// JSON 格式序列化器。基于 System.Text.Json，提供可读性和版本兼容性。
    /// </summary>
    public static class JsonSerializer2
    {
        private static readonly JsonSerializerOptions Options = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        };

        /// <summary>将 ISerializable 对象序列化为 JSON 字符串</summary>
        public static string SerializeToJson(ISerializable obj)
        {
            var serializer = new PropertySerializer();
            obj.Serialize(serializer);
            return JsonSerializer.Serialize(serializer.GetProperties(), Options);
        }

        /// <summary>从 JSON 字符串反序列化为 ISerializable 对象</summary>
        public static T? DeserializeFromJson<T>(string json) where T : ISerializable, new()
        {
            var props = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json, Options);
            if (props == null) return default;

            var serializer = new PropertySerializer();
            foreach (var kvp in props)
                serializer.GetProperties()[kvp.Key] = ConvertJsonElement(kvp.Value);

            var obj = new T();
            obj.Deserialize(serializer);
            return obj;
        }

        /// <summary>将 PropertySerializer 的属性序列化为 JSON</summary>
        public static string SerializeProperties(PropertySerializer serializer)
        {
            return JsonSerializer.Serialize(serializer.GetProperties(), Options);
        }

        /// <summary>从 JSON 反序列化到 PropertySerializer</summary>
        public static PropertySerializer DeserializeProperties(string json)
        {
            var props = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json, Options);
            var serializer = new PropertySerializer();
            if (props == null) return serializer;
            foreach (var kvp in props)
                serializer.GetProperties()[kvp.Key] = ConvertJsonElement(kvp.Value);
            return serializer;
        }

        private static object? ConvertJsonElement(JsonElement element)
        {
            return element.ValueKind switch
            {
                JsonValueKind.Null => null,
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number => element.TryGetInt64(out var l) ? (object)l
                    : element.TryGetDouble(out var d) ? d : 0f,
                JsonValueKind.String => element.GetString(),
                JsonValueKind.Array =>
                    element.EnumerateArray().Select(ConvertJsonElement).ToList(),
                JsonValueKind.Object =>
                    element.EnumerateObject().ToDictionary(
                        p => p.Name,
                        p => ConvertJsonElement(p.Value)),
                _ => element.GetRawText()
            };
        }
    }

    // ====================================================================
    // 差异序列化器（DeltaSerializer）
    // ====================================================================

    /// <summary>
    /// 差异序列化器。只序列化变更的字段，用于网络同步和增量保存。
    /// 比较两个 PropertySerializer 实例，输出差异集合。
    /// </summary>
    public static class DeltaSerializer
    {
        /// <summary>
        /// 计算两个 PropertySerializer 实例之间的差异。
        /// 返回变更的字段集合。
        /// </summary>
        public static List<DeltaChange> ComputeDelta(PropertySerializer baseline, PropertySerializer current)
        {
            var changes = new List<DeltaChange>();
            var baselineProps = baseline.GetProperties();
            var currentProps = current.GetProperties();

            // 检查新增和修改的字段
            foreach (var kvp in currentProps)
            {
                if (!baselineProps.TryGetValue(kvp.Key, out var baseValue) || !ValuesEqual(baseValue, kvp.Value))
                {
                    changes.Add(new DeltaChange
                    {
                        FieldName = kvp.Key,
                        ChangeType = baselineProps.ContainsKey(kvp.Key) ? DeltaChangeType.Modified : DeltaChangeType.Added,
                        NewValue = kvp.Value,
                        OldValue = baselineProps.ContainsKey(kvp.Key) ? baseValue : null
                    });
                }
            }

            // 检查删除的字段
            foreach (var kvp in baselineProps)
            {
                if (!currentProps.ContainsKey(kvp.Key))
                {
                    changes.Add(new DeltaChange
                    {
                        FieldName = kvp.Key,
                        ChangeType = DeltaChangeType.Removed,
                        OldValue = kvp.Value,
                        NewValue = null
                    });
                }
            }

            return changes;
        }

        /// <summary>将差异应用到 PropertySerializer</summary>
        public static void ApplyDelta(PropertySerializer target, List<DeltaChange> changes)
        {
            foreach (var change in changes)
            {
                switch (change.ChangeType)
                {
                    case DeltaChangeType.Added:
                    case DeltaChangeType.Modified:
                        target.GetProperties()[change.FieldName] = change.NewValue;
                        break;
                    case DeltaChangeType.Removed:
                        target.GetProperties().Remove(change.FieldName);
                        break;
                }
            }
        }

        /// <summary>将差异序列化为 JSON</summary>
        public static string SerializeDelta(List<DeltaChange> changes)
        {
            return JsonSerializer.Serialize(changes.Select(c => new
            {
                f = c.FieldName,
                t = (int)c.ChangeType,
                n = c.NewValue?.ToString(),
                o = c.OldValue?.ToString()
            }));
        }

        private static bool ValuesEqual(object? a, object? b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null) return a == null && b == null;
            return a.Equals(b);
        }
    }

    /// <summary>差异变更类型</summary>
    public enum DeltaChangeType
    {
        Added,
        Modified,
        Removed
    }

    /// <summary>单个字段的差异变更</summary>
    public sealed class DeltaChange
    {
        public string FieldName { get; set; } = "";
        public DeltaChangeType ChangeType { get; set; }
        public object? OldValue { get; set; }
        public object? NewValue { get; set; }
    }

    // ====================================================================
    // 反射式自动序列化器（ReflectionSerializer）
    // ====================================================================

    /// <summary>
    /// 基于反射的自动序列化器。扫描标记了 [Serialized] 的字段/属性，
    /// 自动进行序列化/反序列化，无需手动实现 ISerializable。
    /// </summary>
    public static class ReflectionSerializer
    {
        /// <summary>自动序列化对象（基于 [Serialized] 标记）</summary>
        public static void AutoSerialize(object obj, PropertySerializer serializer)
        {
            if (obj == null) return;
            var type = obj.GetType();

            // 扫描字段
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (field.GetCustomAttribute<NonSerializedAttribute>() != null) continue;
                if (field.GetCustomAttribute<SerializedAttribute>() == null) continue;

                var attr = field.GetCustomAttribute<SerializedAttribute>();
                string name = !string.IsNullOrEmpty(attr?.FieldName) ? attr.FieldName : field.Name;
                var value = field.GetValue(obj);
                WriteAutoValue(serializer, name, value);
            }

            // 扫描属性
            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (prop.GetCustomAttribute<NonSerializedAttribute>() != null) continue;
                if (!prop.CanRead) continue;
                // 属性默认需要 [Serialized] 标记（除非 public 的自动属性）
                if (prop.GetCustomAttribute<SerializedAttribute>() == null &&
                    !IsAutoProperty(prop)) continue;

                var attr = prop.GetCustomAttribute<SerializedAttribute>();
                string name = !string.IsNullOrEmpty(attr?.FieldName) ? attr.FieldName : prop.Name;
                var value = prop.GetValue(obj);
                WriteAutoValue(serializer, name, value);
            }
        }

        /// <summary>自动反序列化对象（基于 [Serialized] 标记）</summary>
        public static void AutoDeserialize(object obj, PropertySerializer serializer)
        {
            if (obj == null) return;
            var type = obj.GetType();

            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (field.GetCustomAttribute<NonSerializedAttribute>() != null) continue;
                if (field.GetCustomAttribute<SerializedAttribute>() == null) continue;

                var attr = field.GetCustomAttribute<SerializedAttribute>();
                string name = !string.IsNullOrEmpty(attr?.FieldName) ? attr.FieldName : field.Name;
                var value = ReadAutoValue(serializer, name, field.FieldType);
                if (value != null || !field.FieldType.IsValueType)
                    field.SetValue(obj, value);
            }

            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (prop.GetCustomAttribute<NonSerializedAttribute>() != null) continue;
                if (!prop.CanWrite || !prop.CanRead) continue;
                if (prop.GetCustomAttribute<SerializedAttribute>() == null &&
                    !IsAutoProperty(prop)) continue;

                var attr = prop.GetCustomAttribute<SerializedAttribute>();
                string name = !string.IsNullOrEmpty(attr?.FieldName) ? attr.FieldName : prop.Name;
                var value = ReadAutoValue(serializer, name, prop.PropertyType);
                if (value != null || !prop.PropertyType.IsValueType)
                    prop.SetValue(obj, value);
            }
        }

        private static bool IsAutoProperty(System.Reflection.PropertyInfo prop)
        {
            return prop.GetGetMethod()?.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), false) == true;
        }

        private static void WriteAutoValue(PropertySerializer serializer, string name, object? value)
        {
            if (value == null) return;

            var type = value.GetType();
            if (type == typeof(byte)) serializer.Write(name, (byte)value);
            else if (type == typeof(sbyte)) serializer.Write(name, (sbyte)value);
            else if (type == typeof(short)) serializer.Write(name, (short)value);
            else if (type == typeof(ushort)) serializer.Write(name, (ushort)value);
            else if (type == typeof(int)) serializer.Write(name, (int)value);
            else if (type == typeof(uint)) serializer.Write(name, (uint)value);
            else if (type == typeof(long)) serializer.Write(name, (long)value);
            else if (type == typeof(ulong)) serializer.Write(name, (ulong)value);
            else if (type == typeof(float)) serializer.Write(name, (float)value);
            else if (type == typeof(double)) serializer.Write(name, (double)value);
            else if (type == typeof(bool)) serializer.Write(name, (bool)value);
            else if (type == typeof(string)) serializer.Write(name, (string)value);
            else if (type == typeof(Vector2)) serializer.Write(name, (Vector2)value);
            else if (type == typeof(Vector3)) serializer.Write(name, (Vector3)value);
            else if (type == typeof(Vector4)) serializer.Write(name, (Vector4)value);
            else if (type == typeof(Quaternion)) serializer.Write(name, (Quaternion)value);
            else if (type == typeof(Guid)) serializer.Write(name, (Guid)value);
            else if (type == typeof(DateTime)) serializer.Write(name, (DateTime)value);
            else if (type == typeof(byte[])) serializer.Write(name, (byte[])value);
            else if (value is Enum e) serializer.Write(name, e);
            else if (value is IList list) serializer.WriteArray(name, list.OfType<object>().ToList());
            else if (value is ISerializable serializable) serializer.WriteObject(name, serializable);
            else serializer.Write(name, value.ToString());
        }

        private static object? ReadAutoValue(PropertySerializer serializer, string name, Type type)
        {
            if (type == typeof(byte)) return serializer.ReadByte(name);
            if (type == typeof(sbyte)) return serializer.ReadSByte(name);
            if (type == typeof(short)) return serializer.ReadInt16(name);
            if (type == typeof(ushort)) return serializer.ReadUInt16(name);
            if (type == typeof(int)) return serializer.ReadInt32(name);
            if (type == typeof(uint)) return serializer.ReadUInt32(name);
            if (type == typeof(long)) return serializer.ReadInt64(name);
            if (type == typeof(ulong)) return serializer.ReadUInt64(name);
            if (type == typeof(float)) return serializer.ReadSingle(name);
            if (type == typeof(double)) return serializer.ReadDouble(name);
            if (type == typeof(bool)) return serializer.ReadBool(name);
            if (type == typeof(string)) return serializer.ReadString(name);
            if (type == typeof(Vector2)) return serializer.ReadVector2(name);
            if (type == typeof(Vector3)) return serializer.ReadVector3(name);
            if (type == typeof(Vector4)) return serializer.ReadVector4(name);
            if (type == typeof(Quaternion)) return serializer.ReadQuaternion(name);
            if (type == typeof(Guid)) return serializer.ReadGuid(name);
            if (type == typeof(DateTime)) return serializer.ReadDateTime(name);
            if (type == typeof(byte[])) return serializer.ReadBytes(name);
            if (type.IsEnum) return serializer.ReadEnum(name, Activator.CreateInstance(type) as Enum);
            return null;
        }
    }

    /// <summary>
    /// 运行时辅助工具类。委托到 System.Runtime.CompilerServices.RuntimeHelpers。
    /// </summary>
    internal static class ShanyuRuntimeHelpers
    {
        public static int GetObjectHashCode(object obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }
}
