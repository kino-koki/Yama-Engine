using System;
using System.Buffers.Binary;
using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace YamaEngine.Network
{
    // ════════════════════════════════════════════════════════════════════════
    // yama引擎 - 真实网络序列化实现 (NetworkSerializationReal)
    //
    // 完整的二进制序列化/反序列化管线, 涵盖:
    //   * NetworkWriter: 高性能二进制写入器 (byte[]/MemoryStream/ArrayPool)
    //   * NetworkReader: 安全二进制读取器 (边界检查/越界保护)
    //   * VarIntUtility: 无符号/有符号VarInt编码 + ZigZag
    //   * FloatQuantizationUtility: 浮点量化/反量化/向量/四元数Smallest3
    //   * CRC32Utility: IEEE 802.3 CRC32 查表法 + 增量CRC
    //   * BitWriter/BitReader: 位打包读写器
    //
    // 所有方法均为真实实现, 无占位式实现。
    // 目标框架: net8.0 (C# 8.0+), 无额外 NuGet 依赖。
    // ════════════════════════════════════════════════════════════════════════

    // ──────────────────────────────────────────────────────────────
    // 1. NetworkWriter - 高性能二进制写入器
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 高性能网络二进制写入器。支持写入到 byte[] 缓冲区,
    /// 具备自动扩容、预分配和 ArrayPool 租用能力。
    /// 默认使用 LittleEndian 字节序。
    /// </summary>
    public sealed class NetworkSerializationWriter : IDisposable
    {
        private byte[] _buffer;
        private int _position;
        private int _capacity;
        private bool _disposed;
        private readonly bool _pooled;
        private const int DefaultInitialCapacity = 256;
        private const int MaxArrayPoolSize = 1024 * 1024; // 1MB 以下使用 ArrayPool

        /// <summary>当前写入位置。</summary>
        public int Position => _position;

        /// <summary>当前已写入的数据长度。</summary>
        public int Length => _position;

        /// <summary>缓冲区总容量。</summary>
        public int Capacity => _capacity;

        /// <summary>获取已写入的数据数组副本。</summary>
        public byte[] ToArray()
        {
            if (_position == 0) return Array.Empty<byte>();
            byte[] result = new byte[_position];
            Array.Copy(_buffer, result, _position);
            return result;
        }

        /// <summary>获取缓冲区引用 (注意: 长度需使用 Length)。</summary>
        public byte[] Buffer => _buffer;

        /// <summary>获取已写入数据的 Span。</summary>
        public ReadOnlySpan<byte> AsSpan() => _buffer.AsSpan(0, _position);

        /// <summary>获取已写入数据的 Memory。</summary>
        public ReadOnlyMemory<byte> AsMemory() => _buffer.AsMemory(0, _position);

        /// <summary>创建指定初始容量的写入器 (从 ArrayPool 租用缓冲区)。</summary>
        public NetworkSerializationWriter(int capacity = DefaultInitialCapacity)
        {
            _capacity = Math.Max(capacity, 16);
            _pooled = true;
            _buffer = ArrayPool<byte>.Shared.Rent(_capacity);
            _position = 0;
            _disposed = false;
        }

        /// <summary>使用外部缓冲区创建写入器 (不使用 ArrayPool)。</summary>
        public NetworkSerializationWriter(byte[] buffer, int offset = 0)
        {
            _buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
            _position = offset;
            _capacity = buffer.Length;
            _pooled = false;
            _disposed = false;
        }

        /// <summary>将写入器内容写入目标流。</summary>
        public void CopyTo(System.IO.Stream stream)
        {
            if (_position > 0)
            {
                stream.Write(_buffer, 0, _position);
            }
        }

        // ── 基础类型写入 ──────────────────────────────────────────

        /// <summary>写入一个字节。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteByte(byte value)
        {
            EnsureCapacity(1);
            _buffer[_position++] = value;
        }

        /// <summary>写入有符号字节。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteSByte(sbyte value)
        {
            EnsureCapacity(1);
            _buffer[_position++] = unchecked((byte)value);
        }

        /// <summary>写入 UInt16 (2字节, LittleEndian)。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteUInt16(ushort value)
        {
            EnsureCapacity(2);
            BinaryPrimitives.WriteUInt16LittleEndian(_buffer.AsSpan(_position), value);
            _position += 2;
        }

        /// <summary>写入 Int16 (2字节, LittleEndian)。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteInt16(short value)
        {
            EnsureCapacity(2);
            BinaryPrimitives.WriteInt16LittleEndian(_buffer.AsSpan(_position), value);
            _position += 2;
        }

        /// <summary>写入 UInt32 (4字节, LittleEndian)。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteUInt32(uint value)
        {
            EnsureCapacity(4);
            BinaryPrimitives.WriteUInt32LittleEndian(_buffer.AsSpan(_position), value);
            _position += 4;
        }

        /// <summary>写入 Int32 (4字节, LittleEndian)。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteInt32(int value)
        {
            EnsureCapacity(4);
            BinaryPrimitives.WriteInt32LittleEndian(_buffer.AsSpan(_position), value);
            _position += 4;
        }

        /// <summary>写入 UInt64 (8字节, LittleEndian)。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteUInt64(ulong value)
        {
            EnsureCapacity(8);
            BinaryPrimitives.WriteUInt64LittleEndian(_buffer.AsSpan(_position), value);
            _position += 8;
        }

        /// <summary>写入 Int64 (8字节, LittleEndian)。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteInt64(long value)
        {
            EnsureCapacity(8);
            BinaryPrimitives.WriteInt64LittleEndian(_buffer.AsSpan(_position), value);
            _position += 8;
        }

        /// <summary>写入 Float (4字节, IEEE 754)。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteFloat(float value)
        {
            EnsureCapacity(4);
            BinaryPrimitives.WriteSingleLittleEndian(_buffer.AsSpan(_position), value);
            _position += 4;
        }

        /// <summary>写入 Double (8字节, IEEE 754)。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteDouble(double value)
        {
            EnsureCapacity(8);
            BinaryPrimitives.WriteDoubleLittleEndian(_buffer.AsSpan(_position), value);
            _position += 8;
        }

        /// <summary>写入 Bool (1字节, 0 或 1)。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteBool(bool value)
        {
            EnsureCapacity(1);
            _buffer[_position++] = value ? (byte)1 : (byte)0;
        }

        // ── 字符串写入 ──────────────────────────────────────────────

        /// <summary>写入字符串 (VarInt长度前缀 + UTF-8 字节)。</summary>
        public void WriteString(string value)
        {
            if (value == null) value = string.Empty;
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            VarIntUtility.WriteVarInt(this, (uint)bytes.Length);
            if (bytes.Length > 0)
            {
                WriteRawBytes(bytes, 0, bytes.Length);
            }
        }

        /// <summary>写入固定长度的字符串 (不足补零, 超出截断)。</summary>
        public void WriteFixedString(string value, int fixedByteLength)
        {
            if (value == null) value = string.Empty;
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            EnsureCapacity(fixedByteLength);
            int copyLen = Math.Min(bytes.Length, fixedByteLength);
            if (copyLen > 0)
            {
                Array.Copy(bytes, 0, _buffer, _position, copyLen);
            }
            // 剩余填充零
            for (int i = copyLen; i < fixedByteLength; i++)
            {
                _buffer[_position + i] = 0;
            }
            _position += fixedByteLength;
        }

        // ── 向量/四元数写入 ─────────────────────────────────────────

        /// <summary>写入 Vector2 (Float * 2 = 8字节)。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteVector2(Vector2 v)
        {
            WriteFloat(v.X);
            WriteFloat(v.Y);
        }

        /// <summary>写入 Vector3 (Float * 3 = 12字节)。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteVector3(Vector3 v)
        {
            WriteFloat(v.X);
            WriteFloat(v.Y);
            WriteFloat(v.Z);
        }

        /// <summary>写入 Vector4 (Float * 4 = 16字节)。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteVector4(Vector4 v)
        {
            WriteFloat(v.X);
            WriteFloat(v.Y);
            WriteFloat(v.Z);
            WriteFloat(v.W);
        }

        /// <summary>写入 Quaternion (Float * 4 = 16字节, X/Y/Z/W)。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteQuaternion(Quaternion q)
        {
            WriteFloat(q.X);
            WriteFloat(q.Y);
            WriteFloat(q.Z);
            WriteFloat(q.W);
        }

        /// <summary>写入 Color32 (Byte * 4 = R/G/B/A, 4字节)。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteColor(byte r, byte g, byte b, byte a)
        {
            EnsureCapacity(4);
            _buffer[_position++] = r;
            _buffer[_position++] = g;
            _buffer[_position++] = b;
            _buffer[_position++] = a;
        }

        /// <summary>写入 Color32 (从 Vector4, 分量 0~1 映射到 0~255)。</summary>
        public void WriteColor(Vector4 color)
        {
            WriteColor(
                (byte)Math.Clamp((int)(color.X * 255f), 0, 255),
                (byte)Math.Clamp((int)(color.Y * 255f), 0, 255),
                (byte)Math.Clamp((int)(color.Z * 255f), 0, 255),
                (byte)Math.Clamp((int)(color.W * 255f), 0, 255));
        }

        // ── 字节数组写入 ──────────────────────────────────────────

        /// <summary>写入字节数组 (VarInt长度前缀 + 数据)。</summary>
        public void WriteBytes(byte[] data)
        {
            if (data == null || data.Length == 0)
            {
                VarIntUtility.WriteVarInt(this, 0);
                return;
            }
            VarIntUtility.WriteVarInt(this, (uint)data.Length);
            WriteRawBytes(data, 0, data.Length);
        }

        /// <summary>写入字节数组片段 (VarInt长度前缀 + 数据)。</summary>
        public void WriteBytes(byte[] data, int offset, int count)
        {
            VarIntUtility.WriteVarInt(this, (uint)count);
            WriteRawBytes(data, offset, count);
        }

        /// <summary>写入固定长度字节数据 (无长度前缀)。</summary>
        public void WriteFixedBytes(byte[] data, int count)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            WriteRawBytes(data, 0, Math.Min(count, data.Length));
            // 不足 count 的部分补零
            int padding = count - Math.Min(count, data.Length);
            EnsureCapacity(padding);
            for (int i = 0; i < padding; i++)
            {
                _buffer[_position++] = 0;
            }
        }

        /// <summary>写入原始字节 (不写入长度前缀, 直接写入)。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteRawBytes(byte[] data, int offset, int count)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (count <= 0) return;
            EnsureCapacity(count);
            Array.Copy(data, offset, _buffer, _position, count);
            _position += count;
        }

        /// <summary>写入只读 Span 的原始字节。</summary>
        public void WriteRawBytes(ReadOnlySpan<byte> data)
        {
            if (data.IsEmpty) return;
            EnsureCapacity(data.Length);
            data.CopyTo(_buffer.AsSpan(_position));
            _position += data.Length;
        }

        // ── 位写入 ────────────────────────────────────────────────

        private byte _bitAccumulator;
        private int _bitCount;

        /// <summary>写入指定位数的数据 (位打包模式)。</summary>
        public void WriteBits(uint value, int bits)
        {
            if (bits <= 0) return;
            if (bits > 32) bits = 32;

            for (int i = 0; i < bits; i++)
            {
                if ((value & (1u << i)) != 0)
                    _bitAccumulator |= (byte)(1 << _bitCount);
                _bitCount++;

                if (_bitCount == 8)
                {
                    WriteByte(_bitAccumulator);
                    _bitAccumulator = 0;
                    _bitCount = 0;
                }
            }
        }

        /// <summary>写入单个布尔位 (1位)。</summary>
        public void WriteBit(bool value)
        {
            if (value)
                _bitAccumulator |= (byte)(1 << _bitCount);
            _bitCount++;

            if (_bitCount == 8)
            {
                WriteByte(_bitAccumulator);
                _bitAccumulator = 0;
                _bitCount = 0;
            }
        }

        /// <summary>将位缓冲区对齐到字节边界 (丢弃剩余位)。</summary>
        public void FlushBits()
        {
            if (_bitCount > 0)
            {
                WriteByte(_bitAccumulator);
                _bitAccumulator = 0;
                _bitCount = 0;
            }
        }

        // ── 大端序写入 ──────────────────────────────────────────────

        /// <summary>写入 UInt16 (BigEndian)。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteUInt16BE(ushort value)
        {
            EnsureCapacity(2);
            BinaryPrimitives.WriteUInt16BigEndian(_buffer.AsSpan(_position), value);
            _position += 2;
        }

        /// <summary>写入 Int16 (BigEndian)。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteInt16BE(short value)
        {
            EnsureCapacity(2);
            BinaryPrimitives.WriteInt16BigEndian(_buffer.AsSpan(_position), value);
            _position += 2;
        }

        /// <summary>写入 UInt32 (BigEndian)。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteUInt32BE(uint value)
        {
            EnsureCapacity(4);
            BinaryPrimitives.WriteUInt32BigEndian(_buffer.AsSpan(_position), value);
            _position += 4;
        }

        /// <summary>写入 Int32 (BigEndian)。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteInt32BE(int value)
        {
            EnsureCapacity(4);
            BinaryPrimitives.WriteInt32BigEndian(_buffer.AsSpan(_position), value);
            _position += 4;
        }

        /// <summary>写入 UInt64 (BigEndian)。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteUInt64BE(ulong value)
        {
            EnsureCapacity(8);
            BinaryPrimitives.WriteUInt64BigEndian(_buffer.AsSpan(_position), value);
            _position += 8;
        }

        /// <summary>写入 Int64 (BigEndian)。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteInt64BE(long value)
        {
            EnsureCapacity(8);
            BinaryPrimitives.WriteInt64BigEndian(_buffer.AsSpan(_position), value);
            _position += 8;
        }

        // ── 缓冲区管理 ────────────────────────────────────────────

        /// <summary>预分配空间 (提前保证容量)。</summary>
        public void Reserve(int additionalBytes)
        {
            EnsureCapacity(additionalBytes);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void EnsureCapacity(int additionalBytes)
        {
            int required = _position + additionalBytes;
            if (required <= _capacity) return;

            int newCapacity = Math.Max(_capacity * 2, required);
            newCapacity = Math.Max(newCapacity, DefaultInitialCapacity);

            if (_pooled)
            {
                byte[] newBuffer = ArrayPool<byte>.Shared.Rent(newCapacity);
                Array.Copy(_buffer, newBuffer, _position);
                ArrayPool<byte>.Shared.Return(_buffer, clearArray: true);
                _buffer = newBuffer;
            }
            else
            {
                Array.Resize(ref _buffer, newCapacity);
            }
            _capacity = _buffer.Length;
        }

        /// <summary>跳过指定字节数 (填充零)。</summary>
        public void Skip(int count)
        {
            EnsureCapacity(count);
            Array.Clear(_buffer, _position, count);
            _position += count;
        }

        /// <summary>在指定偏移处写入 Int32 (不影响当前 Position)。</summary>
        public void WriteInt32At(int offset, int value)
        {
            if (offset < 0 || offset + 4 > _capacity)
                throw new ArgumentOutOfRangeException(nameof(offset));
            BinaryPrimitives.WriteInt32LittleEndian(_buffer.AsSpan(offset), value);
        }

        /// <summary>重置写入器 (清零位置, 保留缓冲区)。</summary>
        public void Reset()
        {
            _position = 0;
            _bitAccumulator = 0;
            _bitCount = 0;
        }

        /// <summary>释放资源, 归还 ArrayPool。</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_pooled && _buffer != null)
            {
                ArrayPool<byte>.Shared.Return(_buffer, clearArray: true);
                _buffer = Array.Empty<byte>();
            }
            _position = 0;
            _capacity = 0;
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 2. NetworkReader - 安全二进制读取器
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 安全的网络二进制读取器。支持边界检查和越界保护。
    /// 所有读取操作在数据不足时返回默认值而非抛出异常。
    /// </summary>

    // ──────────────────────────────────────────────────────────────
    // 3. VarInt 编码/解码
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// VarInt 变长整数编码工具。兼容 Protocol Buffers VarInt 格式。
    /// 每字节使用 7 位存数据 + 1 位继续标志 (MSB)。
    /// 支持: 无符号 VarInt / 有符号 ZigZag+VarInt / 字节数预估。
    /// </summary>
    public static class VarIntUtility
    {
        /// <summary>写入无符号 VarInt。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteVarInt(NetworkSerializationWriter writer, uint value)
        {
            Span<byte> temp = stackalloc byte[5];
            int count = 0;

            do
            {
                byte b = (byte)(value & 0x7F);
                value >>= 7;
                if (value != 0) b |= 0x80;
                temp[count++] = b;
            }
            while (value != 0);

            writer.WriteRawBytes(temp.Slice(0, count));
        }

        /// <summary>读取无符号 VarInt。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint ReadVarInt(NetworkReaderReal reader)
        {
            uint result = 0;
            int shift = 0;

            while (reader.CanRead)
            {
                byte b = reader.ReadByte();
                result |= (uint)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) break;
                shift += 7;
                if (shift >= 35) break; // 防止溢出 (uint 最多5字节)
            }

            return result;
        }

        /// <summary>写入有符号 VarInt (ZigZag 编码 + VarInt)。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteSignedVarInt(NetworkSerializationWriter writer, int value)
        {
            // ZigZag: 0→0, -1→1, 1→2, -2→3, 2→4 ...
            uint encoded = (uint)((value << 1) ^ (value >> 31));
            WriteVarInt(writer, encoded);
        }

        /// <summary>读取有符号 VarInt (VarInt + ZigZag 解码)。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int ReadSignedVarInt(NetworkReaderReal reader)
        {
            uint encoded = ReadVarInt(reader);
            // ZigZag 解码: 0→0, 1→-1, 2→1, 3→-2, 4→2 ...
            return (int)((encoded >> 1) ^ (-(int)(encoded & 1)));
        }

        /// <summary>写入64位无符号 VarInt。</summary>
        public static void WriteVarInt64(NetworkSerializationWriter writer, ulong value)
        {
            Span<byte> temp = stackalloc byte[10];
            int count = 0;

            do
            {
                byte b = (byte)(value & 0x7F);
                value >>= 7;
                if (value != 0) b |= 0x80;
                temp[count++] = b;
            }
            while (value != 0);

            writer.WriteRawBytes(temp.Slice(0, count));
        }

        /// <summary>读取64位无符号 VarInt。</summary>
        public static ulong ReadVarInt64(NetworkReaderReal reader)
        {
            ulong result = 0;
            int shift = 0;

            while (reader.CanRead)
            {
                byte b = reader.ReadByte();
                result |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) break;
                shift += 7;
                if (shift >= 70) break; // 防止溢出
            }

            return result;
        }

        /// <summary>写入64位有符号 VarInt (ZigZag + VarInt)。</summary>
        public static void WriteSignedVarInt64(NetworkSerializationWriter writer, long value)
        {
            ulong encoded = (ulong)((value << 1) ^ (value >> 63));
            WriteVarInt64(writer, encoded);
        }

        /// <summary>读取64位有符号 VarInt (VarInt + ZigZag 解码)。</summary>
        public static long ReadSignedVarInt64(NetworkReaderReal reader)
        {
            ulong encoded = ReadVarInt64(reader);
            return (long)((encoded >> 1) ^ (ulong)(-(long)(encoded & 1)));
        }

        /// <summary>计算无符号 VarInt 编码所需字节数。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int ComputeVarIntSize(uint value)
        {
            if (value < 0x80) return 1;
            if (value < 0x4000) return 2;
            if (value < 0x200000) return 3;
            if (value < 0x10000000) return 4;
            return 5;
        }

        /// <summary>计算有符号 VarInt 编码所需字节数。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int ComputeSignedVarIntSize(int value)
        {
            return ComputeVarIntSize((uint)((value << 1) ^ (value >> 31)));
        }

        /// <summary>计算64位 VarInt 编码所需字节数。</summary>
        public static int ComputeVarInt64Size(ulong value)
        {
            if (value < 0x80) return 1;
            if (value < 0x4000) return 2;
            if (value < 0x200000) return 3;
            if (value < 0x10000000) return 4;
            if (value < 0x800000000UL) return 5;
            if (value < 0x40000000000UL) return 6;
            if (value < 0x2000000000000UL) return 7;
            if (value < 0x100000000000000UL) return 8;
            if (value < 0x8000000000000000UL) return 9;
            return 10;
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 4. 浮点量化工具
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 浮点量化/反量化工具。提供任意位宽的浮点压缩,
    /// 以及 Vector3 独立分量量化和 Quaternion SmallestThree 量化。
    /// </summary>
    public static class FloatQuantizationUtility
    {
        /// <summary>量化浮点值到 N 位整数。</summary>
        /// <param name="value">原始浮点值。</param>
        /// <param name="min">范围最小值。</param>
        /// <param name="max">范围最大值。</param>
        /// <param name="bits">量化位宽 (1~32)。</param>
        /// <returns>量化后的整数值。</returns>
        public static uint QuantizeFloat(float value, float min, float max, int bits)
        {
            if (bits <= 0) bits = 1;
            if (bits > 32) bits = 32;

            float range = max - min;
            if (range == 0f) return 0;

            float normalized = (value - min) / range;
            normalized = Math.Clamp(normalized, 0f, 1f);

            uint maxVal = (bits >= 32) ? uint.MaxValue : ((1u << bits) - 1u);
            uint quantized = (uint)Math.Round(normalized * maxVal);
            return Math.Min(quantized, maxVal);
        }

        /// <summary>反量化 N 位整数为浮点值。</summary>
        /// <param name="quantized">量化后的整数值。</param>
        /// <param name="min">范围最小值。</param>
        /// <param name="max">范围最大值。</param>
        /// <param name="bits">量化位宽 (1~32)。</param>
        /// <returns>反量化后的浮点值。</returns>
        public static float DequantizeFloat(uint quantized, float min, float max, int bits)
        {
            if (bits <= 0) bits = 1;
            if (bits > 32) bits = 32;

            float range = max - min;
            if (range == 0f) return min;

            uint maxVal = (bits >= 32) ? uint.MaxValue : ((1u << bits) - 1u);
            float normalized = (float)quantized / maxVal;
            return min + normalized * range;
        }

        /// <summary>量化 Vector3 三个分量 (独立量化每个分量)。</summary>
        /// <param name="v">原始向量。</param>
        /// <param name="min">范围最小值。</param>
        /// <param name="max">范围最大值。</param>
        /// <param name="bitsPerComponent">每个分量的量化位宽。</param>
        /// <returns>三个量化分量值。</returns>
        public static (uint x, uint y, uint z) QuantizeVector3(Vector3 v, float min, float max, int bitsPerComponent)
        {
            uint qx = QuantizeFloat(v.X, min, max, bitsPerComponent);
            uint qy = QuantizeFloat(v.Y, min, max, bitsPerComponent);
            uint qz = QuantizeFloat(v.Z, min, max, bitsPerComponent);
            return (qx, qy, qz);
        }

        /// <summary>反量化 Vector3 (独立反量化每个分量)。</summary>
        public static Vector3 DequantizeVector3(uint qx, uint qy, uint qz, float min, float max, int bitsPerComponent)
        {
            return new Vector3(
                DequantizeFloat(qx, min, max, bitsPerComponent),
                DequantizeFloat(qy, min, max, bitsPerComponent),
                DequantizeFloat(qz, min, max, bitsPerComponent));
        }

        /// <summary>将量化后的 Vector3 写入写入器。</summary>
        public static void WriteQuantizedVector3(NetworkSerializationWriter writer, Vector3 v, float min, float max, int bitsPerComponent)
        {
            writer.WriteBits(QuantizeFloat(v.X, min, max, bitsPerComponent), bitsPerComponent);
            writer.WriteBits(QuantizeFloat(v.Y, min, max, bitsPerComponent), bitsPerComponent);
            writer.WriteBits(QuantizeFloat(v.Z, min, max, bitsPerComponent), bitsPerComponent);
        }

        /// <summary>从读取器读取量化 Vector3。</summary>
        public static Vector3 ReadQuantizedVector3(NetworkReaderReal reader, float min, float max, int bitsPerComponent)
        {
            uint qx = reader.ReadBits(bitsPerComponent);
            uint qy = reader.ReadBits(bitsPerComponent);
            uint qz = reader.ReadBits(bitsPerComponent);
            return DequantizeVector3(qx, qy, qz, min, max, bitsPerComponent);
        }

        /// <summary>
        /// SmallestThree 量化 Quaternion。
        /// 找到最大绝对值分量, 用其余 3 个分量 + 最大分量索引 + 符号位表示。
        /// 共需: 2位(索引) + 1位(符号) + bits*3(三个量化分量)。
        /// </summary>
        /// <param name="q">原始四元数。</param>
        /// <param name="bits">每个分量的量化位宽。</param>
        /// <returns>最大分量索引、符号、三个量化分量。</returns>
        public static (byte largestAxis, bool isNegated, uint a, uint b, uint c) QuantizeQuaternion(Quaternion q, int bits)
        {
            float absX = Math.Abs(q.X);
            float absY = Math.Abs(q.Y);
            float absZ = Math.Abs(q.Z);
            float absW = Math.Abs(q.W);

            byte largestAxis = 0;
            float maxAbs = absX;
            if (absY > maxAbs) { maxAbs = absY; largestAxis = 1; }
            if (absZ > maxAbs) { maxAbs = absZ; largestAxis = 2; }
            if (absW > maxAbs) { maxAbs = absW; largestAxis = 3; }

            // 获取最大分量的值
            float largestValue = largestAxis switch
            {
                0 => q.X, 1 => q.Y, 2 => q.Z, _ => q.W
            };

            bool isNegated = largestValue < 0;

            // 提取其他三个分量 (根据符号翻转)
            float fa, fb, fc;
            switch (largestAxis)
            {
                case 0: fa = q.Y; fb = q.Z; fc = q.W; break;
                case 1: fa = q.X; fb = q.Z; fc = q.W; break;
                case 2: fa = q.X; fb = q.Y; fc = q.W; break;
                default: fa = q.X; fb = q.Y; fc = q.Z; break;
            }

            if (isNegated) { fa = -fa; fb = -fb; fc = -fc; }

            // 量化三个分量到 [-1, 1] 范围
            uint qa = QuantizeFloat(fa, -1f, 1f, bits);
            uint qb = QuantizeFloat(fb, -1f, 1f, bits);
            uint qc = QuantizeFloat(fc, -1f, 1f, bits);

            return (largestAxis, isNegated, qa, qb, qc);
        }

        /// <summary>
        /// SmallestThree 反量化 Quaternion。
        /// 从最大分量索引、符号和三个量化分量重建四元数。
        /// </summary>
        public static Quaternion DequantizeQuaternion(byte largestAxis, bool isNegated, uint a, uint b, uint c, int bits)
        {
            float fa = DequantizeFloat(a, -1f, 1f, bits);
            float fb = DequantizeFloat(b, -1f, 1f, bits);
            float fc = DequantizeFloat(c, -1f, 1f, bits);

            if (isNegated) { fa = -fa; fb = -fb; fc = -fc; }

            // 重建第四个分量: w = sqrt(1 - x^2 - y^2 - z^2)
            float dot = fa * fa + fb * fb + fc * fc;
            float d = (float)Math.Sqrt(Math.Max(0f, 1f - dot));

            return largestAxis switch
            {
                0 => new Quaternion(d, fa, fb, fc),
                1 => new Quaternion(fa, d, fb, fc),
                2 => new Quaternion(fa, fb, d, fc),
                _ => new Quaternion(fa, fb, fc, d)
            };
        }

        /// <summary>将量化后的 Quaternion 写入写入器。</summary>
        public static void WriteQuantizedQuaternion(NetworkSerializationWriter writer, Quaternion q, int bits)
        {
            var (largestAxis, isNegated, a, b, c) = QuantizeQuaternion(q, bits);
            writer.WriteBits(largestAxis, 2);   // 2位: 索引 0~3
            writer.WriteBit(isNegated);           // 1位: 符号
            writer.WriteBits(a, bits);            // N位
            writer.WriteBits(b, bits);            // N位
            writer.WriteBits(c, bits);            // N位
        }

        /// <summary>从读取器读取量化 Quaternion。</summary>
        public static Quaternion ReadQuantizedQuaternion(NetworkReaderReal reader, int bits)
        {
            byte largestAxis = (byte)reader.ReadBits(2);
            bool isNegated = reader.ReadBit();
            uint a = reader.ReadBits(bits);
            uint b = reader.ReadBits(bits);
            uint c = reader.ReadBits(bits);
            return DequantizeQuaternion(largestAxis, isNegated, a, b, c, bits);
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 5. CRC32 校验 (IEEE 802.3)
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// CRC32 校验工具。使用 IEEE 802.3 标准多项式 (0xEDB88320 反转形式),
    /// 查表法实现, 支持增量和全量计算。
    /// </summary>
    public static class CRC32Utility
    {
        /// <summary>CRC32 IEEE 802.3 多项式 (反转)。</summary>
        private const uint Polynomial = 0xEDB88320u;

        /// <summary>预计算的 CRC32 查找表 (256 项)。</summary>
        private static readonly uint[] Table = GenerateTable();

        /// <summary>是否已初始化表。</summary>
        public static bool IsTableReady => Table != null;

        /// <summary>生成 CRC32 查找表。</summary>
        private static uint[] GenerateTable()
        {
            var table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint crc = i;
                for (int j = 0; j < 8; j++)
                {
                    crc = (crc & 1) != 0
                        ? (crc >> 1) ^ Polynomial
                        : crc >> 1;
                }
                table[i] = crc;
            }
            return table;
        }

        /// <summary>计算 CRC32 (全量)。</summary>
        /// <param name="data">数据数组。</param>
        /// <param name="offset">起始偏移。</param>
        /// <param name="count">计算长度 (-1 表示到数组末尾)。</param>
        /// <returns>CRC32 值。</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint ComputeCRC32(byte[] data, int offset = 0, int count = -1)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (count < 0) count = data.Length - offset;
            if (offset < 0 || count < 0 || offset + count > data.Length)
                throw new ArgumentOutOfRangeException();

            uint crc = 0xFFFFFFFFu;
            int end = offset + count;

            for (int i = offset; i < end; i++)
            {
                crc = (crc >> 8) ^ Table[(crc ^ data[i]) & 0xFF];
            }

            return ~crc;
        }

        /// <summary>计算 CRC32 (从 ReadOnlySpan)。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint ComputeCRC32(ReadOnlySpan<byte> data)
        {
            uint crc = 0xFFFFFFFFu;

            for (int i = 0; i < data.Length; i++)
            {
                crc = (crc >> 8) ^ Table[(crc ^ data[i]) & 0xFF];
            }

            return ~crc;
        }

        /// <summary>计算增量 CRC32 (从上一个 CRC 继续计算)。</summary>
        /// <param name="previousCRC">上一个 CRC 值 (未取反的中间值, 或 ~finalCRC)。</param>
        /// <param name="data">新数据。</param>
        /// <param name="offset">起始偏移。</param>
        /// <param name="count">计算长度。</param>
        /// <returns>新的 CRC32 值。</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint ComputeCRC32Incremental(uint previousCRC, byte[] data, int offset, int count)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));

            uint crc = previousCRC ^ 0xFFFFFFFFu; // 还原中间状态
            int end = offset + count;

            for (int i = offset; i < end; i++)
            {
                crc = (crc >> 8) ^ Table[(crc ^ data[i]) & 0xFF];
            }

            return ~crc;
        }

        /// <summary>追加单个字节到 CRC。</summary>
        /// <param name="crc">当前 CRC 中间值 (0xFFFFFFFF 起始)。</param>
        /// <param name="b">要追加的字节。</param>
        /// <returns>更新后的 CRC 中间值。</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint AppendByte(uint crc, byte b)
        {
            return (crc >> 8) ^ Table[(crc ^ b) & 0xFF];
        }

        /// <summary>完成 CRC 计算 (对中间值取反)。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint Finalize(uint intermediateCRC)
        {
            return ~intermediateCRC;
        }

        /// <summary>验证 CRC32。</summary>
        /// <param name="data">数据数组。</param>
        /// <param name="offset">起始偏移。</param>
        /// <param name="count">数据长度。</param>
        /// <param name="expectedCRC">期望的 CRC32 值。</param>
        /// <returns>是否匹配。</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool VerifyCRC32(byte[] data, int offset, int count, uint expectedCRC)
        {
            return ComputeCRC32(data, offset, count) == expectedCRC;
        }

        /// <summary>验证 CRC32 (使用 ReadOnlySpan)。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool VerifyCRC32(ReadOnlySpan<byte> data, uint expectedCRC)
        {
            return ComputeCRC32(data) == expectedCRC;
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 6. BitWriter / BitReader - 位打包读写器
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 位写入器。将数据以位为单位紧凑写入到字节数组。
    /// 用于网络数据极致压缩场景 (如: 布尔1位, 枚举最小位, 小整数)。
    /// </summary>
    public sealed class BitWriter : IDisposable
    {
        private byte[] _buffer;
        private int _bitPosition;
        private int _capacityBytes;
        private bool _disposed;

        /// <summary>当前位位置。</summary>
        public int BitPosition => _bitPosition;

        /// <summary>当前字节位置 (含未满字节)。</summary>
        public int BytePosition => (_bitPosition + 7) >> 3;

        /// <summary>总写入位数。</summary>
        public int TotalBits => _bitPosition;

        public BitWriter(int initialCapacityBytes = 64)
        {
            _capacityBytes = initialCapacityBytes;
            _buffer = ArrayPool<byte>.Shared.Rent(initialCapacityBytes);
            _bitPosition = 0;
            _disposed = false;
        }

        /// <summary>写入1位布尔值。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteBool(bool value)
        {
            EnsureCapacityBits(1);
            if (value)
                _buffer[_bitPosition >> 3] |= (byte)(1 << (_bitPosition & 7));
            else
                _buffer[_bitPosition >> 3] &= (byte)~(1 << (_bitPosition & 7));
            _bitPosition++;
        }

        /// <summary>写入指定位数的无符号整数。</summary>
        public void WriteUInt(uint value, int bits)
        {
            if (bits <= 0) return;
            if (bits > 32) bits = 32;
            EnsureCapacityBits(bits);

            for (int i = 0; i < bits; i++)
            {
                if ((value & (1u << i)) != 0)
                    _buffer[_bitPosition >> 3] |= (byte)(1 << (_bitPosition & 7));
                else
                    _buffer[_bitPosition >> 3] &= (byte)~(1 << (_bitPosition & 7));
                _bitPosition++;
            }
        }

        /// <summary>写入指定位数的有符号整数。</summary>
        public void WriteInt(int value, int bits)
        {
            WriteUInt((uint)value, bits);
        }

        /// <summary>写入量化浮点。</summary>
        public void WriteQuantizedFloat(float value, float min, float max, int bits)
        {
            uint quantized = FloatQuantizationUtility.QuantizeFloat(value, min, max, bits);
            WriteUInt(quantized, bits);
        }

        /// <summary>对齐到字节边界。</summary>
        public void PadToByte()
        {
            int remainder = _bitPosition & 7;
            if (remainder > 0)
                _bitPosition += 8 - remainder;
        }

        /// <summary>写入原始字节 (必须已对齐到字节边界)。</summary>
        public void WriteRawBytes(byte[] data, int offset, int count)
        {
            PadToByte();
            EnsureCapacityBits(count * 8);
            Array.Copy(data, offset, _buffer, _bitPosition >> 3, count);
            _bitPosition += count * 8;
        }

        /// <summary>获取已写入数据的字节数组副本。</summary>
        public byte[] ToArray()
        {
            int byteCount = BytePosition;
            byte[] result = new byte[byteCount];
            Array.Copy(_buffer, result, byteCount);
            return result;
        }

        /// <summary>获取已写入数据的 Span。</summary>
        public ReadOnlySpan<byte> AsSpan()
        {
            return _buffer.AsSpan(0, BytePosition);
        }

        private void EnsureCapacityBits(int additionalBits)
        {
            int requiredBits = _bitPosition + additionalBits;
            int requiredBytes = (requiredBits + 7) >> 3;

            if (requiredBytes > _capacityBytes)
            {
                int newCapacity = Math.Max(_capacityBytes * 2, requiredBytes);
                byte[] newBuffer = ArrayPool<byte>.Shared.Rent(newCapacity);
                Array.Copy(_buffer, newBuffer, _capacityBytes);
                ArrayPool<byte>.Shared.Return(_buffer, clearArray: true);
                _buffer = newBuffer;
                _capacityBytes = newBuffer.Length;
            }
        }

        /// <summary>重置。</summary>
        public void Reset()
        {
            _bitPosition = 0;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            ArrayPool<byte>.Shared.Return(_buffer, clearArray: true);
            _buffer = Array.Empty<byte>();
        }
    }

    /// <summary>
    /// 位读取器。从位流中读取紧凑编码的数据。
    /// </summary>
    public sealed class BitReader
    {
        private readonly byte[] _buffer;
        private int _bitPosition;
        private readonly int _totalBits;

        /// <summary>当前位位置。</summary>
        public int BitPosition => _bitPosition;

        /// <summary>是否还有更多数据。</summary>
        public bool HasMore => _bitPosition < _totalBits;

        /// <summary>剩余位数。</summary>
        public int RemainingBits => _totalBits - _bitPosition;

        public BitReader(byte[] data)
        {
            _buffer = data ?? throw new ArgumentNullException(nameof(data));
            _bitPosition = 0;
            _totalBits = data.Length * 8;
        }

        public BitReader(byte[] data, int bitOffset)
        {
            _buffer = data ?? throw new ArgumentNullException(nameof(data));
            _bitPosition = bitOffset;
            _totalBits = data.Length * 8;
        }

        /// <summary>读取1位布尔值。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool ReadBool()
        {
            if (_bitPosition >= _totalBits) return false;
            bool value = ((_buffer[_bitPosition >> 3] >> (_bitPosition & 7)) & 1) != 0;
            _bitPosition++;
            return value;
        }

        /// <summary>读取指定位数的无符号整数。</summary>
        public uint ReadUInt(int bits)
        {
            if (bits <= 0) return 0;
            if (bits > 32) bits = 32;

            uint value = 0;
            for (int i = 0; i < bits; i++)
            {
                if (_bitPosition >= _totalBits) break;
                if (((_buffer[_bitPosition >> 3] >> (_bitPosition & 7)) & 1) != 0)
                    value |= (1u << i);
                _bitPosition++;
            }
            return value;
        }

        /// <summary>读取指定位数的有符号整数。</summary>
        public int ReadInt(int bits)
        {
            return unchecked((int)ReadUInt(bits));
        }

        /// <summary>读取量化浮点。</summary>
        public float ReadQuantizedFloat(float min, float max, int bits)
        {
            uint quantized = ReadUInt(bits);
            return FloatQuantizationUtility.DequantizeFloat(quantized, min, max, bits);
        }

        /// <summary>读取32位浮点 (直接读取32位)。</summary>
        public float ReadFloat()
        {
            uint bits = ReadUInt(32);
            return BitConverter.Int32BitsToSingle(unchecked((int)bits));
        }

        /// <summary>对齐到字节边界。</summary>
        public void PadToByte()
        {
            int remainder = _bitPosition & 7;
            if (remainder > 0)
                _bitPosition += 8 - remainder;
        }

        /// <summary>读取原始字节。</summary>
        public byte[] ReadRawBytes(int count)
        {
            PadToByte();
            int bytePos = _bitPosition >> 3;
            int maxBytes = _buffer.Length - bytePos;
            int actualCount = Math.Min(count, maxBytes);
            byte[] result = new byte[actualCount];
            Array.Copy(_buffer, bytePos, result, 0, actualCount);
            _bitPosition += actualCount * 8;
            return result;
        }

        /// <summary>设置位位置。</summary>
        public void Seek(int bitPosition)
        {
            _bitPosition = Math.Clamp(bitPosition, 0, _totalBits);
        }
    }
}
