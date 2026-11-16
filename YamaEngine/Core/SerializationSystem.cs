using System; using System.Collections.Generic; using System.IO; using System.IO.Compression;
using System.Runtime.CompilerServices; using System.Runtime.InteropServices;
using System.Text; using System.Text.Json; using System.Text.Json.Serialization;

namespace YamaEngine.Core;

public enum SerializedType : byte
{
    Null, Bool, Byte, SByte, Short, UShort, Int, UInt, Long, ULong, Float, Double,
    String, Guid, Vector2, Vector3, Vector4, Quaternion, Matrix4x4,
    Color, Color32, DateTime, TimeSpan, Decimal,
    Array, List, Dictionary, Object, Type, AssetHandle,
    ByteArray, IntArray, FloatArray, StringArray, ObjectArray
}

public struct SerializedField
{
    public string Name; public SerializedType Type;
    public object? Value; public int ArrayLength;
    public List<SerializedField>? Fields, Children;
}

public sealed class BinarySerializer
{
    private readonly byte[] _buffer = new byte[64 * 1024 * 1024];
    private int _offset;
    private readonly Dictionary<object, int> _objectCache = new(ReferenceEqualityComparer.Instance);
    private int _nextObjectId;
    private bool _compressStrings = true;

    public int Offset => _offset;
    public int Capacity => _buffer.Length;

    /// <summary>
    /// 校验写入容量。写越界抛出明确异常（而非晦涩的 IndexOutOfRangeException）。
    /// </summary>
    private void EnsureWrite(int count)
    {
        if (count < 0 || _offset > _buffer.Length - count)
            throw new InvalidOperationException(
                $"Serialization buffer overflow: need {count} byte(s) at offset {_offset}, capacity {_buffer.Length}.");
    }

    /// <summary>
    /// 校验读取边界。读越界抛出明确异常，用于检测损坏/截断的数据流。
    /// </summary>
    private void EnsureRead(int count)
    {
        if (count < 0 || _offset > _buffer.Length - count)
            throw new InvalidOperationException(
                $"Serialization buffer underflow: need {count} byte(s) at offset {_offset}, capacity {_buffer.Length}.");
    }

    /// <summary>返回自 <paramref name="offset"/> 起剩余可读字节数（用于长度前缀防越界校验）。</summary>
    private int RemainingFrom(int offset) => _buffer.Length - offset;

    public void Reset() { _offset = 0; _objectCache.Clear(); _nextObjectId = 0; }

    #region Writers

    public void WriteBool(bool v) { EnsureWrite(1); _buffer[_offset++] = (byte)(v ? 1 : 0); }
    public void WriteByte(byte v) { EnsureWrite(1); _buffer[_offset++] = v; }
    public void WriteSByte(sbyte v) { EnsureWrite(1); _buffer[_offset++] = (byte)v; }
    public void WriteShort(short v) { WriteUShort((ushort)v); }
    public void WriteUShort(ushort v)
    {
        EnsureWrite(2);
        _buffer[_offset++] = (byte)v; _buffer[_offset++] = (byte)(v >> 8);
    }
    public void WriteInt(int v) { WriteUInt((uint)v); }
    public void WriteUInt(uint v)
    {
        EnsureWrite(4);
        _buffer[_offset++] = (byte)v; _buffer[_offset++] = (byte)(v>>8); _buffer[_offset++] = (byte)(v>>16); _buffer[_offset++] = (byte)(v>>24);
    }
    public void WriteLong(long v) => WriteULong((ulong)v);
    public void WriteULong(ulong v)
    {
        EnsureWrite(8);
        for (int i = 0; i < 8; i++) { _buffer[_offset++] = (byte)(v >> (i*8)); }
    }
    public void WriteFloat(float v) { WriteUInt(BitConverter.SingleToUInt32Bits(v)); }
    public void WriteDouble(double v) { WriteLong(BitConverter.DoubleToInt64Bits(v)); }
    public void WriteDecimal(decimal v) { var bits = decimal.GetBits(v); for (int i = 0; i < 4; i++) WriteInt(bits[i]); }

    public void WriteString(string? v)
    {
        if (v == null) { WriteInt(-1); return; }
        byte[] bytes = Encoding.UTF8.GetBytes(v);
        EnsureWrite(bytes.Length + 4); // 4 = 长度前缀
        WriteInt(bytes.Length);
        Array.Copy(bytes, 0, _buffer, _offset, bytes.Length);
        _offset += bytes.Length;
    }

    public void WriteBytes(byte[] v)
    {
        ArgumentNullException.ThrowIfNull(v);
        EnsureWrite(v.Length + 4);
        WriteInt(v.Length);
        Array.Copy(v, 0, _buffer, _offset, v.Length); _offset += v.Length;
    }
    public void WriteSpan(ReadOnlySpan<byte> v)
    {
        EnsureWrite(v.Length + 4);
        WriteInt(v.Length);
        v.CopyTo(new Span<byte>(_buffer, _offset, v.Length)); _offset += v.Length;
    }

    public void WriteVector3(System.Numerics.Vector3 v) { WriteFloat(v.X); WriteFloat(v.Y); WriteFloat(v.Z); }
    public void WriteVector4(System.Numerics.Vector4 v) { WriteFloat(v.X); WriteFloat(v.Y); WriteFloat(v.Z); WriteFloat(v.W); }
    public void WriteQuaternion(System.Numerics.Quaternion v) { WriteFloat(v.X); WriteFloat(v.Y); WriteFloat(v.Z); WriteFloat(v.W); }
    public void WriteMatrix4x4(System.Numerics.Matrix4x4 m)
    {
        WriteFloat(m.M11); WriteFloat(m.M12); WriteFloat(m.M13); WriteFloat(m.M14);
        WriteFloat(m.M21); WriteFloat(m.M22); WriteFloat(m.M23); WriteFloat(m.M24);
        WriteFloat(m.M31); WriteFloat(m.M32); WriteFloat(m.M33); WriteFloat(m.M34);
        WriteFloat(m.M41); WriteFloat(m.M42); WriteFloat(m.M43); WriteFloat(m.M44);
    }

    public void WriteType(SerializedType t) { EnsureWrite(1); _buffer[_offset++] = (byte)t; }

    public void WriteObject(object? obj)
    {
        if (obj == null) { WriteType(SerializedType.Null); return; }
        int id = _objectCache.GetValueOrDefault(obj, -1);
        if (id >= 0) { WriteType(SerializedType.Guid); WriteInt(id); return; }
        _objectCache[obj] = _nextObjectId++;

        switch (obj)
        {
            case bool v: WriteType(SerializedType.Bool); WriteBool(v); break;
            case byte v: WriteType(SerializedType.Byte); WriteByte(v); break;
            case sbyte v: WriteType(SerializedType.SByte); WriteSByte(v); break;
            case short v: WriteType(SerializedType.Short); WriteShort(v); break;
            case ushort v: WriteType(SerializedType.UShort); WriteUShort(v); break;
            case int v: WriteType(SerializedType.Int); WriteInt(v); break;
            case uint v: WriteType(SerializedType.UInt); WriteUInt(v); break;
            case long v: WriteType(SerializedType.Long); WriteLong(v); break;
            case ulong v: WriteType(SerializedType.ULong); WriteULong(v); break;
            case float v: WriteType(SerializedType.Float); WriteFloat(v); break;
            case double v: WriteType(SerializedType.Double); WriteDouble(v); break;
            case string v: WriteType(SerializedType.String); WriteString(v); break;
            case decimal v: WriteType(SerializedType.Decimal); WriteDecimal(v); break;
            case System.Numerics.Vector3 v: WriteType(SerializedType.Vector3); WriteVector3(v); break;
            case System.Numerics.Vector4 v: WriteType(SerializedType.Vector4); WriteVector4(v); break;
            case System.Numerics.Quaternion v: WriteType(SerializedType.Quaternion); WriteQuaternion(v); break;
            case System.Numerics.Matrix4x4 v: WriteType(SerializedType.Matrix4x4); WriteMatrix4x4(v); break;
            default: WriteType(SerializedType.Null); break;
        }
    }

    #endregion

    #region Readers

    public bool ReadBool() { EnsureRead(1); return _buffer[_offset++] != 0; }
    public byte ReadByte() { EnsureRead(1); return _buffer[_offset++]; }
    public sbyte ReadSByte() { EnsureRead(1); return (sbyte)_buffer[_offset++]; }
    public ushort ReadUShort()
    {
        EnsureRead(2);
        ushort v = (ushort)(_buffer[_offset] | (_buffer[_offset+1] << 8)); _offset += 2; return v;
    }
    public short ReadShort() => (short)ReadUShort();
    public uint ReadUInt()
    {
        EnsureRead(4);
        uint v = _buffer[_offset] | (uint)(_buffer[_offset+1]<<8) | (uint)(_buffer[_offset+2]<<16) | (uint)(_buffer[_offset+3]<<24); _offset += 4; return v;
    }
    public int ReadInt() => (int)ReadUInt();
    public ulong ReadULong()
    {
        EnsureRead(8);
        ulong v = 0; for (int i = 0; i < 8; i++) v |= (ulong)_buffer[_offset++] << (i*8); return v;
    }
    public long ReadLong() => (long)ReadULong();
    public float ReadFloat() => BitConverter.ToSingle(BitConverter.GetBytes(ReadUInt()), 0);
    public double ReadDouble() => BitConverter.ToDouble(BitConverter.GetBytes(ReadLong()), 0);
    public decimal ReadDecimal() { var bits = new int[4]; for (int i = 0; i < 4; i++) bits[i] = ReadInt(); return new decimal(bits); }

    /// <summary>
    /// 读取一个长度前缀并校验其不越过当前可读边界。
    /// 防止损坏/恶意流通过伪造超大长度导致 OOM。
    /// </summary>
    private int ReadLengthPrefix()
    {
        int len = ReadInt();
        if (len < 0) return -1; // null 标记
        if (len > RemainingFrom(_offset))
            throw new InvalidOperationException(
                $"Serialization data corrupt: declared length {len} exceeds remaining {RemainingFrom(_offset)} bytes.");
        return len;
    }

    public string ReadString()
    {
        int len = ReadLengthPrefix();
        if (len < 0) return null!;
        EnsureRead(len);
        string v = Encoding.UTF8.GetString(_buffer, _offset, len);
        _offset += len; return v;
    }

    public byte[] ReadBytes()
    {
        int len = ReadLengthPrefix();
        if (len < 0) return Array.Empty<byte>();
        EnsureRead(len);
        var v = new byte[len];
        Array.Copy(_buffer, _offset, v, 0, len); _offset += len; return v;
    }

    public object? ReadObject()
    {
        EnsureRead(1);
        var type = (SerializedType)_buffer[_offset++];
        return type switch
        {
            SerializedType.Null => null,
            SerializedType.Bool => ReadBool(),
            SerializedType.Byte => ReadByte(),
            SerializedType.SByte => ReadSByte(),
            SerializedType.Short => ReadShort(),
            SerializedType.UShort => ReadUShort(),
            SerializedType.Int => ReadInt(),
            SerializedType.UInt => ReadUInt(),
            SerializedType.Long => ReadLong(),
            SerializedType.ULong => ReadULong(),
            SerializedType.Float => ReadFloat(),
            SerializedType.Double => ReadDouble(),
            SerializedType.String => ReadString(),
            SerializedType.Decimal => ReadDecimal(),
            SerializedType.Vector3 => new System.Numerics.Vector3(ReadFloat(), ReadFloat(), ReadFloat()),
            SerializedType.Vector4 => new System.Numerics.Vector4(ReadFloat(), ReadFloat(), ReadFloat(), ReadFloat()),
            SerializedType.Quaternion => new System.Numerics.Quaternion(ReadFloat(), ReadFloat(), ReadFloat(), ReadFloat()),
            _ => throw new InvalidOperationException(
                $"Serialization data corrupt: unknown type tag {(byte)type} at offset {_offset - 1}."),
        };
    }

    #endregion

    #region Array Serialization

    public void WriteIntArray(int[] arr)
    {
        ArgumentNullException.ThrowIfNull(arr);
        WriteInt(arr.Length);
        for (int i = 0; i < arr.Length; i++) WriteInt(arr[i]);
    }

    public int[] ReadIntArray()
    {
        int len = ReadLengthPrefix();
        if (len < 0) return Array.Empty<int>();
        EnsureRead(len * 4);
        var arr = new int[len]; for (int i = 0; i < len; i++) arr[i] = ReadInt(); return arr;
    }

    public void WriteFloatArray(float[] arr)
    {
        ArgumentNullException.ThrowIfNull(arr);
        WriteInt(arr.Length);
        for (int i = 0; i < arr.Length; i++) WriteFloat(arr[i]);
    }
    public float[] ReadFloatArray()
    {
        int len = ReadLengthPrefix();
        if (len < 0) return Array.Empty<float>();
        EnsureRead(len * 4);
        var arr = new float[len]; for (int i = 0; i < len; i++) arr[i] = ReadFloat(); return arr;
    }

    public void WriteStringArray(string[] arr)
    {
        ArgumentNullException.ThrowIfNull(arr);
        WriteInt(arr.Length);
        foreach (var s in arr) WriteString(s);
    }
    public string[] ReadStringArray()
    {
        int len = ReadLengthPrefix();
        if (len < 0) return Array.Empty<string>();
        var arr = new string[len]; for (int i = 0; i < len; i++) arr[i] = ReadString(); return arr;
    }

    #endregion

    #region Compression

    public byte[] Compress()
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Fastest))
            gz.Write(_buffer, 0, _offset);
        return ms.ToArray();
    }

    public void Decompress(byte[] compressed)
    {
        ArgumentNullException.ThrowIfNull(compressed);
        using var ms = new MemoryStream(compressed);
        using var gz = new GZipStream(ms, CompressionMode.Decompress);
        _offset = 0;
        int read;
        // 读入时保持 _offset=0：读位置从缓冲区开头开始，写入用 _buffer.Length - readPos 滑动
        int readPos = 0;
        try
        {
            while ((read = gz.Read(_buffer, readPos, _buffer.Length - readPos)) > 0)
            {
                readPos += read;
                if (readPos == _buffer.Length)
                {
                    // 缓冲区已满，检查是否还有更多数据（说明解压结果超出容量）
                    if (gz.ReadByte() != -1)
                        throw new InvalidOperationException(
                            $"Serialization decompress overflow: result exceeds {_buffer.Length} bytes.");
                    break;
                }
            }
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidOperationException("Serialization data corrupt: invalid GZip stream.", ex);
        }
        _offset = 0;
    }

    #endregion

    #region File I/O

    public void SaveToFile(string path)
    {
        if (string.IsNullOrEmpty(path)) throw new ArgumentException("Path is required.", nameof(path));
        using var fs = new FileStream(path, FileMode.Create);
        var compressed = Compress();
        fs.Write(compressed, 0, compressed.Length);
    }

    public void LoadFromFile(string path)
    {
        if (string.IsNullOrEmpty(path)) throw new ArgumentException("Path is required.", nameof(path));
        var compressed = File.ReadAllBytes(path);
        Decompress(compressed);
    }

    #endregion
}

public sealed class JsonSerializerWrapper
{
    private readonly JsonSerializerOptions _options = new()
    {
        WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        IncludeFields = true, Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string Serialize<T>(T obj) => JsonSerializer.Serialize(obj, _options);
    public T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, _options);

    public byte[] SerializeBinary<T>(T obj)
    {
        string json = Serialize(obj);
        return Encoding.UTF8.GetBytes(json);
    }

    public T? DeserializeBinary<T>(byte[] data)
    {
        string json = Encoding.UTF8.GetString(data);
        return Deserialize<T>(json);
    }
}

public sealed class CSVSerializer
{
    public string Serialize<T>(IEnumerable<T> objects) where T : class
    {
        var sb = new StringBuilder();
        var type = typeof(T);
        var props = type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        sb.AppendLine(string.Join(",", Array.ConvertAll(props, p => p.Name)));
        foreach (var obj in objects)
        {
            var values = new List<string>();
            foreach (var p in props)
            {
                var val = p.GetValue(obj)?.ToString() ?? "";
                if (val.Contains(',') || val.Contains('"')) val = $"\"{val.Replace("\"", "\"\"")}\"";
                values.Add(val);
            }
            sb.AppendLine(string.Join(",", values));
        }
        return sb.ToString();
    }
}

public sealed class VersionedSerializer
{
    public struct SchemaVersion { public int Major, Minor, Patch; public DateTime Date; }

    private SchemaVersion _currentVersion = new() { Major = 1, Minor = 0, Patch = 0, Date = DateTime.UtcNow };
    private readonly Dictionary<int, Func<BinarySerializer, object?>> _migrations = new();
    private readonly Dictionary<int, Type> _typeRegistry = new();
    private int _nextTypeId = 1;

    public void RegisterType<T>(int typeId) { _typeRegistry[typeId] = typeof(T); }
    public void RegisterMigration(int fromVersion, Func<BinarySerializer, object?> migration) => _migrations[fromVersion] = migration;

    public void WriteHeader(BinarySerializer writer)
    {
        writer.WriteInt(_currentVersion.Major);
        writer.WriteInt(_currentVersion.Minor);
        writer.WriteInt(_currentVersion.Patch);
        writer.WriteString(_currentVersion.Date.ToString("O"));
    }

    public (int major, int minor, int patch) ReadHeader(BinarySerializer reader)
    {
        int maj = reader.ReadInt(), min = reader.ReadInt(), pat = reader.ReadInt();
        reader.ReadString();
        return (maj, min, pat);
    }

    public byte[] SaveWithHeader(Action<BinarySerializer> writeData)
    {
        var writer = new BinarySerializer();
        WriteHeader(writer);
        writeData(writer);
        return writer.Compress();
    }

    public object? LoadWithVersionCheck(byte[] data, int targetVersion)
    {
        var reader = new BinarySerializer();
        reader.Decompress(data);
        var (maj, _, _) = ReadHeader(reader);
        if (maj < targetVersion && _migrations.TryGetValue(maj, out var migration))
            return migration(reader);
        return reader; // Return raw reader for manual deserialization
    }
}

internal sealed class ReferenceEqualityComparer : IEqualityComparer<object>
{
    public static readonly ReferenceEqualityComparer Instance = new();
    public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);
    public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
}
