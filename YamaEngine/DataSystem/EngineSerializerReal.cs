using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text;

namespace YamaEngine.DataSystem
{
    // ════════════════════════════════════════════════════════════════════════
    // yama引擎 - 真实引擎序列化 (EngineSerializerReal)
    //
    //   * EngineSerializer: 反射+二进制序列化（基础类型/枚举/数组/List/Dictionary/嵌套/null标记）
    //   * JsonEngineSerializer: JSON序列化（反射→JSON字符串/类型处理/格式化）
    //   * BinaryFormatWriter / BinaryFormatReader: 二进制格式读写
    //   * 版本tolerant / 循环引用检测
    // ════════════════════════════════════════════════════════════════════════

    // ════════════════════════════════════════════════════════════════════════
    // 二进制格式读写器
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>二进制格式Writer。</summary>
    public sealed class BinaryFormatWriter : IDisposable
    {
        private readonly MemoryStream _stream;
        private readonly BinaryWriter _writer;

        /// <summary>魔数。</summary>
        public const uint MagicNumber = 0x53534E45; // "SSNE" = Yama Serial Number Engine

        public BinaryFormatWriter()
        {
            _stream = new MemoryStream();
            _writer = new BinaryWriter(_stream, Encoding.UTF8);
        }

        /// <summary>写入文件头（Magic + Version + TypeHash + FieldCount）。</summary>
        public void WriteHeader(int version, long typeHash, int fieldCount)
        {
            _writer.Write(MagicNumber);
            _writer.Write(version);
            _writer.Write(typeHash);
            _writer.Write(fieldCount);
        }

        public void WriteByte(byte value) => _writer.Write(value);
        public void WriteBool(bool value) => _writer.Write(value);
        public void WriteShort(short value) => _writer.Write(value);
        public void WriteUShort(ushort value) => _writer.Write(value);
        public void WriteInt(int value) => _writer.Write(value);
        public void WriteUInt(uint value) => _writer.Write(value);
        public void WriteLong(long value) => _writer.Write(value);
        public void WriteULong(ulong value) => _writer.Write(value);
        public void WriteFloat(float value) => _writer.Write(value);
        public void WriteDouble(double value) => _writer.Write(value);
        public void WriteString(string value)
        {
            if (value == null)
            {
                _writer.Write(-1);
                return;
            }
            var bytes = Encoding.UTF8.GetBytes(value);
            _writer.Write(bytes.Length);
            _writer.Write(bytes);
        }
        public void WriteBytes(byte[] value)
        {
            if (value == null)
            {
                _writer.Write(-1);
                return;
            }
            _writer.Write(value.Length);
            _writer.Write(value);
        }

        public void WriteVector2(Vector2 value) { WriteFloat(value.X); WriteFloat(value.Y); }
        public void WriteVector3(Vector3 value) { WriteFloat(value.X); WriteFloat(value.Y); WriteFloat(value.Z); }
        public void WriteVector4(Vector4 value) { WriteFloat(value.X); WriteFloat(value.Y); WriteFloat(value.Z); WriteFloat(value.W); }
        public void WriteQuaternion(Quaternion value)
        {
            WriteFloat(value.X); WriteFloat(value.Y); WriteFloat(value.Z); WriteFloat(value.W);
        }
        public void WriteMatrix4x4(Matrix4x4 value)
        {
            WriteFloat(value.M11); WriteFloat(value.M12); WriteFloat(value.M13); WriteFloat(value.M14);
            WriteFloat(value.M21); WriteFloat(value.M22); WriteFloat(value.M23); WriteFloat(value.M24);
            WriteFloat(value.M31); WriteFloat(value.M32); WriteFloat(value.M33); WriteFloat(value.M34);
            WriteFloat(value.M41); WriteFloat(value.M42); WriteFloat(value.M43); WriteFloat(value.M44);
        }
        public void WriteGuid(Guid value) => _writer.Write(value.ToByteArray());

        /// <summary>写入null标记（0x00=null, 0xFF=not null）。</summary>
        public void WriteNullMarker(bool isNull) => _writer.Write(isNull ? (byte)0x00 : (byte)0xFF);

        /// <summary>写入字段标签（名称+类型索引）。</summary>
        public void WriteFieldTag(string fieldName, int typeIndex)
        {
            WriteString(fieldName);
            WriteInt(typeIndex);
        }

        /// <summary>写入类型信息。</summary>
        public void WriteTypeInfo(string typeName, long typeHash)
        {
            WriteString(typeName);
            WriteLong(typeHash);
        }

        /// <summary>获取最终字节。</summary>
        public byte[] ToArray()
        {
            _writer.Flush();
            return _stream.ToArray();
        }

        public void Dispose()
        {
            _writer.Dispose();
            _stream.Dispose();
        }
    }

    /// <summary>二进制格式Reader。</summary>
    public sealed class BinaryFormatReader : IDisposable
    {
        private readonly MemoryStream _stream;
        private readonly BinaryReader _reader;

        public BinaryFormatReader(byte[] data)
        {
            _stream = new MemoryStream(data);
            _reader = new BinaryReader(_stream, Encoding.UTF8);
        }

        /// <summary>读取并验证文件头。</summary>
        public bool ReadHeader(out int version, out long typeHash, out int fieldCount)
        {
            uint magic = _reader.ReadUInt32();
            version = _reader.ReadInt32();
            typeHash = _reader.ReadInt64();
            fieldCount = _reader.ReadInt32();
            return magic == BinaryFormatWriter.MagicNumber;
        }

        public byte ReadByte() => _reader.ReadByte();
        public bool ReadBool() => _reader.ReadBoolean();
        public short ReadShort() => _reader.ReadInt16();
        public ushort ReadUShort() => _reader.ReadUInt16();
        public int ReadInt() => _reader.ReadInt32();
        public uint ReadUInt() => _reader.ReadUInt32();
        public long ReadLong() => _reader.ReadInt64();
        public ulong ReadULong() => _reader.ReadUInt64();
        public float ReadFloat() => _reader.ReadSingle();
        public double ReadDouble() => _reader.ReadDouble();
        public string ReadString()
        {
            int len = _reader.ReadInt32();
            if (len < 0) return null;
            return Encoding.UTF8.GetString(_reader.ReadBytes(len));
        }
        public byte[] ReadBytes()
        {
            int len = _reader.ReadInt32();
            if (len < 0) return null;
            return _reader.ReadBytes(len);
        }

        public Vector2 ReadVector2() => new Vector2(ReadFloat(), ReadFloat());
        public Vector3 ReadVector3() => new Vector3(ReadFloat(), ReadFloat(), ReadFloat());
        public Vector4 ReadVector4() => new Vector4(ReadFloat(), ReadFloat(), ReadFloat(), ReadFloat());
        public Quaternion ReadQuaternion() => new Quaternion(ReadFloat(), ReadFloat(), ReadFloat(), ReadFloat());
        public Matrix4x4 ReadMatrix4x4()
        {
            return new Matrix4x4(
                ReadFloat(), ReadFloat(), ReadFloat(), ReadFloat(),
                ReadFloat(), ReadFloat(), ReadFloat(), ReadFloat(),
                ReadFloat(), ReadFloat(), ReadFloat(), ReadFloat(),
                ReadFloat(), ReadFloat(), ReadFloat(), ReadFloat());
        }
        public Guid ReadGuid() => new Guid(_reader.ReadBytes(16));

        /// <summary>读取null标记。</summary>
        public bool ReadNullMarker() => _reader.ReadByte() == 0x00;

        /// <summary>读取字段标签。</summary>
        public void ReadFieldTag(out string fieldName, out int typeIndex)
        {
            fieldName = ReadString();
            typeIndex = ReadInt();
        }

        /// <summary>读取类型信息。</summary>
        public void ReadTypeInfo(out string typeName, out long typeHash)
        {
            typeName = ReadString();
            typeHash = ReadLong();
        }

        /// <summary>剩余字节数。</summary>
        public int RemainingBytes => (int)(_stream.Length - _stream.Position);

        public void Dispose()
        {
            _reader.Dispose();
            _stream.Dispose();
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // EngineSerializer: 反射+二进制序列化
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 引擎序列化器：支持反射遍历字段+二进制序列化。
    /// 类型分派:
    ///   基础类型: int/float/bool/string/Vector3/Quaternion/Color/Matrix4x4
    ///   枚举→int, 数组→长度+逐元素, List→长度+逐元素
    ///   Dictionary→count+key+value, 嵌套对象→递归
    ///   null标记→0x00/非null→0xFF
    /// 特性: 版本tolerant, 循环引用检测
    /// </summary>
    public sealed class EngineSerializer
    {
        // 已序列化对象表（用于循环引用检测）
        private readonly Dictionary<object, int> _serializedObjects = new();
        private int _nextObjectId;

        // 序列化版本号
        public int Version { get; set; } = 1;

        /// <summary>类型信息缓存。</summary>
        private static readonly Dictionary<Type, Action<BinaryFormatWriter, object>> s_writeCache = new();
        private static readonly object s_cacheLock = new();

        /// <summary>
        /// 序列化对象为字节数组。
        /// </summary>
        public byte[] SerializeObject(object obj)
        {
            if (obj == null) return new byte[0];

            _serializedObjects.Clear();
            _nextObjectId = 0;

            using var writer = new BinaryFormatWriter();

            Type objType = obj.GetType();
            long typeHash = ComputeTypeHash(objType);

            // 收集可序列化字段
            var fields = GetSerializableFields(objType);
            var props = GetSerializableProperties(objType);
            int totalFields = fields.Length + props.Length;

            // 写入头
            writer.WriteHeader(Version, typeHash, totalFields);

            // 写入类型信息
            writer.WriteTypeInfo(objType.FullName ?? objType.Name, typeHash);

            // 写入每个字段
            foreach (var field in fields)
            {
                var value = field.GetValue(obj);
                writer.WriteFieldTag(field.Name, GetTypeCode(field.FieldType));
                WriteValue(writer, value, field.FieldType);
            }

            foreach (var prop in props)
            {
                var value = prop.GetValue(obj);
                writer.WriteFieldTag(prop.Name, GetTypeCode(prop.PropertyType));
                WriteValue(writer, value, prop.PropertyType);
            }

            return writer.ToArray();
        }

        /// <summary>
        /// 反序列化字节数组为对象。
        /// </summary>
        public T DeserializeObject<T>(byte[] data) where T : class, new()
        {
            if (data == null || data.Length == 0) return null;

            using var reader = new BinaryFormatReader(data);

            if (!reader.ReadHeader(out int version, out long typeHash, out int fieldCount))
                throw new InvalidDataException("Invalid binary format: magic number mismatch.");

            reader.ReadTypeInfo(out string typeName, out _);

            var obj = new T();
            var objType = typeof(T);
            var fieldMap = GetSerializableFields(objType).ToDictionary(f => f.Name);
            var propMap = GetSerializableProperties(objType).ToDictionary(p => p.Name);

            // 构建字段名到setter的映射
            var setters = new Dictionary<string, Action<object, object>>();
            foreach (var kvp in fieldMap)
                setters[kvp.Key] = (o, v) => kvp.Value.SetValue(o, v);
            foreach (var kvp in propMap)
            {
                if (kvp.Value.CanWrite)
                    setters[kvp.Key] = (o, v) => kvp.Value.SetValue(o, v);
            }

            for (int i = 0; i < fieldCount; i++)
            {
                reader.ReadFieldTag(out string fieldName, out int typeCode);

                if (setters.TryGetValue(fieldName, out var setter))
                {
                    var fieldInfo = fieldMap.TryGetValue(fieldName, out var fi) ? fi : null;
                    var propInfo = propMap.TryGetValue(fieldName, out var pi) ? pi : null;
                    Type fieldType = fieldInfo?.FieldType ?? propInfo?.PropertyType;

                    if (fieldType != null)
                    {
                        var value = ReadValue(reader, typeCode, fieldType);
                        setter(obj, value);
                    }
                    else
                    {
                        // 字段在当前版本不存在，跳过
                        SkipValue(reader, typeCode);
                    }
                }
                else
                {
                    // 版本tolerant: 跳过未知字段
                    SkipValue(reader, typeCode);
                }
            }

            return obj;
        }

        // ═══════════════════════════════════════════════════════════════════
        // 类型分派写入
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// 写入值（类型分派）。
        /// </summary>
        private void WriteValue(BinaryFormatWriter writer, object value, Type type)
        {
            if (value == null)
            {
                writer.WriteNullMarker(true);
                return;
            }

            writer.WriteNullMarker(false);

            // 循环引用检测
            if (!type.IsValueType)
            {
                if (_serializedObjects.TryGetValue(value, out int refId))
                {
                    writer.WriteInt(-1); // 引用标记
                    writer.WriteInt(refId);
                    return;
                }
                _serializedObjects[value] = _nextObjectId++;
            }

            // 基础类型分派
            if (type == typeof(int))
                writer.WriteInt((int)value);
            else if (type == typeof(uint))
                writer.WriteUInt((uint)value);
            else if (type == typeof(short))
                writer.WriteShort((short)value);
            else if (type == typeof(ushort))
                writer.WriteUShort((ushort)value);
            else if (type == typeof(long))
                writer.WriteLong((long)value);
            else if (type == typeof(ulong))
                writer.WriteULong((ulong)value);
            else if (type == typeof(byte))
                writer.WriteByte((byte)value);
            else if (type == typeof(float))
                writer.WriteFloat((float)value);
            else if (type == typeof(double))
                writer.WriteDouble((double)value);
            else if (type == typeof(bool))
                writer.WriteBool((bool)value);
            else if (type == typeof(string))
                writer.WriteString((string)value);
            else if (type == typeof(Vector2))
                writer.WriteVector2((Vector2)value);
            else if (type == typeof(Vector3))
                writer.WriteVector3((Vector3)value);
            else if (type == typeof(Vector4))
                writer.WriteVector4((Vector4)value);
            else if (type == typeof(Quaternion))
                writer.WriteQuaternion((Quaternion)value);
            else if (type == typeof(Matrix4x4))
                writer.WriteMatrix4x4((Matrix4x4)value);
            else if (type == typeof(Guid))
                writer.WriteGuid((Guid)value);
            else if (type.IsEnum)
            {
                // 枚举 → int
                writer.WriteInt(Convert.ToInt32(value));
            }
            else if (type.IsArray)
            {
                // 数组 → 长度+逐元素
                var arr = (Array)value;
                writer.WriteInt(arr.Length);
                Type elemType = type.GetElementType();
                for (int i = 0; i < arr.Length; i++)
                    WriteValue(writer, arr.GetValue(i), elemType);
            }
            else if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
            {
                // List → 长度+逐元素
                var list = (System.Collections.IList)value;
                writer.WriteInt(list.Count);
                Type elemType = type.GetGenericArguments()[0];
                for (int i = 0; i < list.Count; i++)
                    WriteValue(writer, list[i], elemType);
            }
            else if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>))
            {
                // Dictionary → count+key+value
                var dict = (System.Collections.IDictionary)value;
                writer.WriteInt(dict.Count);
                Type keyType = type.GetGenericArguments()[0];
                Type valueType = type.GetGenericArguments()[1];
                foreach (DictionaryEntry entry in dict)
                {
                    WriteValue(writer, entry.Key, keyType);
                    WriteValue(writer, entry.Value, valueType);
                }
            }
            else if (type.IsClass || (type.IsValueType && !type.IsPrimitive))
            {
                // 嵌套对象 → 递归序列化
                var nestedFields = GetSerializableFields(type);
                var nestedProps = GetSerializableProperties(type);
                writer.WriteInt(nestedFields.Length + nestedProps.Length);

                foreach (var field in nestedFields)
                {
                    writer.WriteString(field.Name);
                    WriteValue(writer, field.GetValue(value), field.FieldType);
                }
                foreach (var prop in nestedProps)
                {
                    writer.WriteString(prop.Name);
                    WriteValue(writer, prop.GetValue(value), prop.PropertyType);
                }
            }
            else
            {
                // 未知类型：尝试ToString
                writer.WriteString(value.ToString());
            }
        }

        // ═══════════════════════════════════════════════════════════════════
        // 类型分派读取
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>读取值（类型分派）。</summary>
        private object ReadValue(BinaryFormatReader reader, int typeCode, Type type)
        {
            bool isNull = reader.ReadNullMarker();
            if (isNull) return null;

            // 引用标记
            if (typeCode == -1)
            {
                int refId = reader.ReadInt();
                return refId; // 简化：返回引用ID，实际应解析为对象
            }

            if (type == typeof(int))
                return reader.ReadInt();
            if (type == typeof(uint))
                return reader.ReadUInt();
            if (type == typeof(short))
                return reader.ReadShort();
            if (type == typeof(ushort))
                return reader.ReadUShort();
            if (type == typeof(long))
                return reader.ReadLong();
            if (type == typeof(ulong))
                return reader.ReadULong();
            if (type == typeof(byte))
                return reader.ReadByte();
            if (type == typeof(float))
                return reader.ReadFloat();
            if (type == typeof(double))
                return reader.ReadDouble();
            if (type == typeof(bool))
                return reader.ReadBool();
            if (type == typeof(string))
                return reader.ReadString();
            if (type == typeof(Vector2))
                return reader.ReadVector2();
            if (type == typeof(Vector3))
                return reader.ReadVector3();
            if (type == typeof(Vector4))
                return reader.ReadVector4();
            if (type == typeof(Quaternion))
                return reader.ReadQuaternion();
            if (type == typeof(Matrix4x4))
                return reader.ReadMatrix4x4();
            if (type == typeof(Guid))
                return reader.ReadGuid();

            if (type.IsEnum)
                return Enum.ToObject(type, reader.ReadInt());

            if (type.IsArray)
            {
                int len = reader.ReadInt();
                Type elemType = type.GetElementType();
                var arr = Array.CreateInstance(elemType, len);
                for (int i = 0; i < len; i++)
                    arr.SetValue(ReadValue(reader, GetTypeCode(elemType), elemType), i);
                return arr;
            }

            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
            {
                int count = reader.ReadInt();
                Type elemType = type.GetGenericArguments()[0];
                var list = (System.Collections.IList)Activator.CreateInstance(type);
                for (int i = 0; i < count; i++)
                    list.Add(ReadValue(reader, GetTypeCode(elemType), elemType));
                return list;
            }

            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>))
            {
                int count = reader.ReadInt();
                Type keyType = type.GetGenericArguments()[0];
                Type valueType = type.GetGenericArguments()[1];
                var dict = (System.Collections.IDictionary)Activator.CreateInstance(type);
                for (int i = 0; i < count; i++)
                {
                    var key = ReadValue(reader, GetTypeCode(keyType), keyType);
                    var val = ReadValue(reader, GetTypeCode(valueType), valueType);
                    dict[key] = val;
                }
                return dict;
            }

            if (type.IsClass || (type.IsValueType && !type.IsPrimitive))
            {
                // 嵌套对象：创建实例并递归填充
                var nested = Activator.CreateInstance(type);
                int fieldCount = reader.ReadInt();
                var nestedFieldMap = GetSerializableFields(type).ToDictionary(f => f.Name);
                var nestedPropMap = GetSerializableProperties(type).Where(p => p.CanWrite).ToDictionary(p => p.Name);

                for (int i = 0; i < fieldCount; i++)
                {
                    string name = reader.ReadString();
                    if (nestedFieldMap.TryGetValue(name, out var fi))
                    {
                        var val = ReadValue(reader, GetTypeCode(fi.FieldType), fi.FieldType);
                        fi.SetValue(nested, val);
                    }
                    else if (nestedPropMap.TryGetValue(name, out var pi))
                    {
                        var val = ReadValue(reader, GetTypeCode(pi.PropertyType), pi.PropertyType);
                        pi.SetValue(nested, val);
                    }
                    else
                    {
                        // 跳过未知字段（版本tolerant）
                        SkipValue(reader, GetTypeCode(type));
                    }
                }
                return nested;
            }

            return reader.ReadString(); // fallback
        }

        /// <summary>跳过指定类型的值（版本tolerant支持）。</summary>
        private void SkipValue(BinaryFormatReader reader, int typeCode)
        {
            bool isNull = reader.ReadNullMarker();
            if (isNull) return;

            switch (typeCode)
            {
                case 0: reader.ReadByte(); break;       // int/byte
                case 1: reader.ReadInt(); break;
                case 2: reader.ReadFloat(); break;
                case 3: reader.ReadBool(); break;
                case 4: reader.ReadString(); break;
                case 5: // Vector3
                    reader.ReadFloat(); reader.ReadFloat(); reader.ReadFloat(); break;
                case 6: // Quaternion
                    reader.ReadFloat(); reader.ReadFloat(); reader.ReadFloat(); reader.ReadFloat(); break;
                case 7: // Matrix4x4
                    for (int i = 0; i < 16; i++) reader.ReadFloat(); break;
                case 8: // Array
                    int len = reader.ReadInt();
                    for (int i = 0; i < len; i++) SkipValue(reader, 0); break;
                default:
                    reader.ReadString(); break;
            }
        }

        // ═══════════════════════════════════════════════════════════════════
        // 辅助方法
        // ═══════════════════════════════════════════════════════════════════

        private static int GetTypeCode(Type type)
        {
            if (type == typeof(int) || type == typeof(uint) || type == typeof(short) || type == typeof(ushort) ||
                type == typeof(long) || type == typeof(ulong) || type == typeof(byte) || type == typeof(sbyte) ||
                type == typeof(char))
                return 0;
            if (type == typeof(float) || type == typeof(double))
                return 2;
            if (type == typeof(bool))
                return 3;
            if (type == typeof(string))
                return 4;
            if (type == typeof(Vector3))
                return 5;
            if (type == typeof(Quaternion))
                return 6;
            if (type == typeof(Matrix4x4))
                return 7;
            if (type == typeof(Vector2))
                return 8;
            if (type == typeof(Vector4))
                return 9;
            if (type.IsEnum)
                return 0; // enum as int
            if (type.IsArray)
                return 8;
            if (type.IsGenericType)
                return 10;
            if (type.IsClass || (type.IsValueType && !type.IsPrimitive))
                return 11; // nested object
            return 0; // fallback
        }

        private static FieldInfo[] GetSerializableFields(Type type)
        {
            return type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                       .Where(f => !f.IsStatic && !f.IsInitOnly &&
                           (f.GetCustomAttribute<NonSerializedAttribute>() == null))
                       .ToArray();
        }

        private static PropertyInfo[] GetSerializableProperties(Type type)
        {
            return type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                       .Where(p => p.CanRead &&
                           p.GetCustomAttribute<NonSerializedAttribute>() == null &&
                           p.GetIndexParameters().Length == 0)
                       .ToArray();
        }

        private static long ComputeTypeHash(Type type)
        {
            unchecked
            {
                long hash = 17;
                foreach (char c in type.FullName ?? type.Name)
                    hash = hash * 31 + c;
                return hash;
            }
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // JsonEngineSerializer: JSON序列化器
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// JSON引擎序列化器：反射→JSON字符串。
    /// 类型处理: 日期→ISO8601, Vector3→[x,y,z], 枚举→名称
    /// </summary>
    public sealed class JsonEngineSerializer
    {
        private readonly Dictionary<object, int> _serializedObjects = new();
        private int _nextRefId;
        private bool _formatted;

        /// <summary>是否使用格式化输出（缩进）。</summary>
        public bool Formatted { get; set; } = true;

        /// <summary>缩进大小。</summary>
        public int IndentSize { get; set; } = 2;

        /// <summary>
        /// 将对象序列化为JSON字符串。
        /// </summary>
        public string SerializeToJson(object obj)
        {
            if (obj == null) return "null";
            _serializedObjects.Clear();
            _nextRefId = 0;
            _formatted = Formatted;

            var sb = new StringBuilder();
            WriteValue(sb, obj, 0);
            return sb.ToString();
        }

        /// <summary>
        /// 从JSON字符串反序列化为对象。
        /// </summary>
        public T DeserializeFromJson<T>(string json) where T : class, new()
        {
            if (string.IsNullOrEmpty(json) || json == "null")
                return null;

            var obj = new T();
            var objType = typeof(T);

            // 简化的JSON解析
            int pos = 0;
            var parsed = ParseJsonValue(json, ref pos);

            if (parsed is Dictionary<string, object> dict)
            {
                FillObjectFromDict(obj, dict, objType);
            }

            return obj;
        }

        // ═══════════════════════════════════════════════════════════════════
        // JSON写入
        // ═══════════════════════════════════════════════════════════════════

        private void WriteValue(StringBuilder sb, object value, int indent)
        {
            if (value == null)
            {
                sb.Append("null");
                return;
            }

            Type type = value.GetType();

            // 循环引用
            if (!type.IsValueType && _serializedObjects.TryGetValue(value, out int refId))
            {
                sb.Append($"{{\"$ref\":{refId}}}");
                return;
            }
            if (!type.IsValueType)
                _serializedObjects[value] = _nextRefId++;

            string pad = _formatted ? new string(' ', indent * IndentSize) : "";
            string childPad = _formatted ? new string(' ', (indent + 1) * IndentSize) : "";

            if (type == typeof(int) || type == typeof(uint) || type == typeof(short) || type == typeof(ushort) ||
                type == typeof(long) || type == typeof(ulong) || type == typeof(byte) || type == typeof(sbyte))
            {
                sb.Append(value.ToString());
            }
            else if (type == typeof(float))
            {
                sb.Append(((float)value).ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            }
            else if (type == typeof(double))
            {
                sb.Append(((double)value).ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            }
            else if (type == typeof(bool))
            {
                sb.Append((bool)value ? "true" : "false");
            }
            else if (type == typeof(string))
            {
                sb.Append(EscapeJsonString((string)value));
            }
            else if (type == typeof(DateTime))
            {
                sb.Append('"');
                sb.Append(((DateTime)value).ToString("o"));
                sb.Append('"');
            }
            else if (type == typeof(Vector2))
            {
                var v = (Vector2)value;
                sb.Append($"[{v.X.ToString("R", System.Globalization.CultureInfo.InvariantCulture)},{v.Y.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}]");
            }
            else if (type == typeof(Vector3))
            {
                var v = (Vector3)value;
                sb.Append($"[{v.X.ToString("R", System.Globalization.CultureInfo.InvariantCulture)},{v.Y.ToString("R", System.Globalization.CultureInfo.InvariantCulture)},{v.Z.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}]");
            }
            else if (type == typeof(Vector4))
            {
                var v = (Vector4)value;
                sb.Append($"[{v.X.ToString("R", System.Globalization.CultureInfo.InvariantCulture)},{v.Y.ToString("R", System.Globalization.CultureInfo.InvariantCulture)},{v.Z.ToString("R", System.Globalization.CultureInfo.InvariantCulture)},{v.W.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}]");
            }
            else if (type == typeof(Quaternion))
            {
                var q = (Quaternion)value;
                sb.Append($"[{q.X.ToString("R", System.Globalization.CultureInfo.InvariantCulture)},{q.Y.ToString("R", System.Globalization.CultureInfo.InvariantCulture)},{q.Z.ToString("R", System.Globalization.CultureInfo.InvariantCulture)},{q.W.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}]");
            }
            else if (type == typeof(Guid))
            {
                sb.Append('"').Append(value.ToString()).Append('"');
            }
            else if (type.IsEnum)
            {
                sb.Append('"').Append(value.ToString()).Append('"');
            }
            else if (type.IsArray)
            {
                var arr = (Array)value;
                sb.Append('[');
                for (int i = 0; i < arr.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    if (_formatted && arr.Length > 1) sb.Append(childPad);
                    WriteValue(sb, arr.GetValue(i), indent + 1);
                }
                if (_formatted && arr.Length > 0) sb.Append(pad);
                sb.Append(']');
            }
            else if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
            {
                var list = (System.Collections.IList)value;
                sb.Append('[');
                for (int i = 0; i < list.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    if (_formatted && list.Count > 1) sb.Append(childPad);
                    WriteValue(sb, list[i], indent + 1);
                }
                if (_formatted && list.Count > 0) sb.Append(pad);
                sb.Append(']');
            }
            else if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>))
            {
                var dict = (System.Collections.IDictionary)value;
                sb.Append('{');
                bool first = true;
                foreach (DictionaryEntry entry in dict)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    if (_formatted) sb.Append(childPad);
                    sb.Append(EscapeJsonString(entry.Key.ToString()));
                    sb.Append(':');
                    WriteValue(sb, entry.Value, indent + 1);
                }
                if (_formatted && dict.Count > 0) sb.Append(pad);
                sb.Append('}');
            }
            else if (type.IsClass || (type.IsValueType && !type.IsPrimitive))
            {
                // 嵌套对象
                var fields = GetSerializableFieldsJson(type);
                var props = GetSerializablePropertiesJson(type);
                sb.Append('{');
                sb.Append(_formatted ? $"\"$type\":\"{type.FullName}\"," : $"\"$type\":\"{type.FullName}\",");
                if (_formatted) sb.Append(childPad);

                bool first = true;
                foreach (var field in fields)
                {
                    if (!first) { sb.Append(','); if (_formatted) sb.Append(childPad); }
                    first = false;
                    sb.Append('"').Append(field.Name).Append('"').Append(':');
                    WriteValue(sb, field.GetValue(value), indent + 1);
                }
                foreach (var prop in props)
                {
                    if (!first) { sb.Append(','); if (_formatted) sb.Append(childPad); }
                    first = false;
                    sb.Append('"').Append(prop.Name).Append('"').Append(':');
                    WriteValue(sb, prop.GetValue(value), indent + 1);
                }
                if (_formatted) sb.Append(pad);
                sb.Append('}');
            }
            else
            {
                sb.Append('"').Append(value.ToString()).Append('"');
            }
        }

        // ═══════════════════════════════════════════════════════════════════
        // JSON读取（简化解析）
        // ═══════════════════════════════════════════════════════════════════

        private object ParseJsonValue(string json, ref int pos)
        {
            pos = SkipWhitespace(json, pos);
            if (pos >= json.Length) return null;

            char c = json[pos];

            if (c == '"') return ParseJsonString(json, ref pos);
            if (c == '{') return ParseJsonObject(json, ref pos);
            if (c == '[') return ParseJsonArray(json, ref pos);
            if (c == 't' || c == 'f') return ParseJsonBool(json, ref pos);
            if (c == 'n') return ParseJsonNull(json, ref pos);
            if (c == '-' || char.IsDigit(c)) return ParseJsonNumber(json, ref pos);

            pos++;
            return null;
        }

        private Dictionary<string, object> ParseJsonObject(string json, ref int pos)
        {
            var dict = new Dictionary<string, object>();
            pos++; // skip '{'
            pos = SkipWhitespace(json, pos);

            while (pos < json.Length && json[pos] != '}')
            {
                pos = SkipWhitespace(json, pos);
                if (json[pos] == ',') { pos++; continue; }

                string key = ParseJsonString(json, ref pos) as string;
                pos = SkipWhitespace(json, pos);
                if (pos < json.Length && json[pos] == ':') pos++;
                pos = SkipWhitespace(json, pos);

                object value = ParseJsonValue(json, ref pos);
                if (key != null)
                    dict[key] = value;

                pos = SkipWhitespace(json, pos);
            }

            if (pos < json.Length && json[pos] == '}') pos++;
            return dict;
        }

        private List<object> ParseJsonArray(string json, ref int pos)
        {
            var list = new List<object>();
            pos++; // skip '['
            pos = SkipWhitespace(json, pos);

            while (pos < json.Length && json[pos] != ']')
            {
                pos = SkipWhitespace(json, pos);
                if (json[pos] == ',') { pos++; continue; }

                list.Add(ParseJsonValue(json, ref pos));
                pos = SkipWhitespace(json, pos);
            }

            if (pos < json.Length && json[pos] == ']') pos++;
            return list;
        }

        private string ParseJsonString(string json, ref int pos)
        {
            if (json[pos] != '"') return null;
            pos++;
            int start = pos;
            while (pos < json.Length)
            {
                if (json[pos] == '\\' && pos + 1 < json.Length) { pos += 2; continue; }
                if (json[pos] == '"') { pos++; return json.Substring(start, pos - start - 1); }
                pos++;
            }
            return json.Substring(start);
        }

        private object ParseJsonNumber(string json, ref int pos)
        {
            int start = pos;
            if (json[pos] == '-') pos++;
            while (pos < json.Length && (char.IsDigit(json[pos]) || json[pos] == '.' || json[pos] == 'e' || json[pos] == 'E' || json[pos] == '+' || json[pos] == '-'))
                pos++;

            string numStr = json.Substring(start, pos - start);
            if (numStr.Contains(".") || numStr.Contains("e") || numStr.Contains("E"))
            {
                if (double.TryParse(numStr, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double d))
                    return d;
            }
            else
            {
                if (int.TryParse(numStr, out int i))
                    return i;
                if (long.TryParse(numStr, out long l))
                    return l;
            }
            return 0;
        }

        private bool ParseJsonBool(string json, ref int pos)
        {
            if (json.Substring(pos).StartsWith("true", StringComparison.Ordinal))
            {
                pos += 4;
                return true;
            }
            if (json.Substring(pos).StartsWith("false", StringComparison.Ordinal))
            {
                pos += 5;
                return false;
            }
            pos++;
            return false;
        }

        private object ParseJsonNull(string json, ref int pos)
        {
            if (json.Substring(pos).StartsWith("null", StringComparison.Ordinal))
            {
                pos += 4;
                return null;
            }
            pos++;
            return null;
        }

        private void FillObjectFromDict(object obj, Dictionary<string, object> dict, Type type)
        {
            var fieldMap = GetSerializableFieldsJson(type).ToDictionary(f => f.Name);
            var propMap = GetSerializablePropertiesJson(type).Where(p => p.CanWrite).ToDictionary(p => p.Name);

            foreach (var kvp in dict)
            {
                if (kvp.Key == "$type") continue;

                if (fieldMap.TryGetValue(kvp.Key, out var fi))
                {
                    var converted = ConvertValue(kvp.Value, fi.FieldType);
                    fi.SetValue(obj, converted);
                }
                else if (propMap.TryGetValue(kvp.Key, out var pi))
                {
                    var converted = ConvertValue(kvp.Value, pi.PropertyType);
                    pi.SetValue(obj, converted);
                }
            }
        }

        private object ConvertValue(object value, Type targetType)
        {
            if (value == null) return null;
            if (value.GetType() == targetType) return value;

            if (targetType == typeof(int) && value is double d)
                return (int)d;
            if (targetType == typeof(float) && value is double d2)
                return (float)d2;
            if (targetType == typeof(long) && value is double d3)
                return (long)d3;

            if (targetType == typeof(Vector3) && value is List<object> vecList)
            {
                float x = Convert.ToSingle(vecList.Count > 0 ? vecList[0] : 0);
                float y = Convert.ToSingle(vecList.Count > 1 ? vecList[1] : 0);
                float z = Convert.ToSingle(vecList.Count > 2 ? vecList[2] : 0);
                return new Vector3(x, y, z);
            }

            if (targetType == typeof(Vector4) && value is List<object> vec4List)
            {
                return new Vector4(
                    Convert.ToSingle(vec4List.Count > 0 ? vec4List[0] : 0),
                    Convert.ToSingle(vec4List.Count > 1 ? vec4List[1] : 0),
                    Convert.ToSingle(vec4List.Count > 2 ? vec4List[2] : 0),
                    Convert.ToSingle(vec4List.Count > 3 ? vec4List[3] : 0));
            }

            if (targetType == typeof(Quaternion) && value is List<object> quatList)
            {
                return new Quaternion(
                    Convert.ToSingle(quatList.Count > 0 ? quatList[0] : 0),
                    Convert.ToSingle(quatList.Count > 1 ? quatList[1] : 0),
                    Convert.ToSingle(quatList.Count > 2 ? quatList[2] : 0),
                    Convert.ToSingle(quatList.Count > 3 ? quatList[3] : 1));
            }

            if (targetType.IsEnum && value is string enumStr)
                return Enum.Parse(targetType, enumStr);

            if (targetType == typeof(string))
                return value.ToString();

            return System.Convert.ChangeType(value, targetType);
        }

        // ═══════════════════════════════════════════════════════════════════
        // 辅助方法
        // ═══════════════════════════════════════════════════════════════════

        private static string EscapeJsonString(string s)
        {
            var sb = new StringBuilder();
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 32)
                            sb.Append($"\\u{(int)c:X4}");
                        else
                            sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        private static int SkipWhitespace(string s, int pos)
        {
            while (pos < s.Length && char.IsWhiteSpace(s[pos]))
                pos++;
            return pos;
        }

        private static FieldInfo[] GetSerializableFieldsJson(Type type)
        {
            return type.GetFields(BindingFlags.Public | BindingFlags.Instance)
                       .Where(f => f.GetCustomAttribute<NonSerializedAttribute>() == null)
                       .ToArray();
        }

        private static PropertyInfo[] GetSerializablePropertiesJson(Type type)
        {
            return type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                       .Where(p => p.CanRead && p.GetIndexParameters().Length == 0 &&
                           p.GetCustomAttribute<NonSerializedAttribute>() == null)
                       .ToArray();
        }
    }
}
