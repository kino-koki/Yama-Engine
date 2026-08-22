using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;

namespace ShanyuEngine.Network
{
    // ════════════════════════════════════════════════════════════════════════
    // 山雨引擎 - 真实网络二进制读取器 (NetworkReaderReal)
    //
    // 高性能二进制读取器, 与 NetworkWriterReal 完全对应:
    //   * 基础类型: byte/sbyte/bool/int16~uint64/float/double/string/bytes
    //   * 数学类型: Vector2/3/4, Quaternion, Matrix4x4, Color
    //   * 位级读取: ReadBit/ReadBits/ReadBitsPacked(VarInt)/ReadBitsPackedSigned(ZigZag)
    //   * 量化读取: ReadQuantizedFloat/Vector3/CompressedQuaternion(Smallest3)/CompressedRotation(Euler)
    //   * 集合读取: ReadArray<T>/ReadDictionary<K,V>/ReadList<T>
    //   * 缓冲区管理: 位置控制/剩余字节/边界检查
    //
    // 所有方法均为真实实现, 无占位式实现。
    // 目标框架: net8.0 (C# 8.0+), 无额外 NuGet 依赖。
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 高性能网络二进制读取器。与 NetworkWriterReal 配对使用,
    /// 支持位级解包、反量化和集合反序列化。
    /// </summary>
    public sealed class NetworkReaderReal
    {
        // ── 缓冲区状态 ────────────────────────────────────────────

        private readonly byte[] _buffer;
        private int _position;
        private readonly int _length;

        // ── 位缓冲区状态 ──────────────────────────────────────────

        private byte _bitAccumulator;
        private int _bitCount;
        private int _bitPosition;

        // ── 属性 ──────────────────────────────────────────────────

        /// <summary>当前读取位置 (字节)。</summary>
        public int Position => _position;

        /// <summary>缓冲区总长度。</summary>
        public int Length => _length;

        /// <summary>剩余可读字节数。</summary>
        public int Remaining => _length - _position;

        /// <summary>是否还有数据可读。</summary>
        public bool HasMore => _position < _length;

        /// <summary>兼容属性: 是否可读 (与旧 VarIntUtility 兼容)。</summary>
        public bool CanRead => _position < _length;

        // ── 构造函数 ──────────────────────────────────────────────

        /// <summary>从字节数组创建读取器。</summary>
        public NetworkReaderReal(byte[] buffer, int offset = 0, int length = -1)
        {
            _buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
            _length = length < 0 ? buffer.Length : Math.Min(offset + length, buffer.Length);
            _position = offset;
        }

        /// <summary>从只读 Span 创建读取器 (复制到内部缓冲区)。</summary>
        public NetworkReaderReal(ReadOnlySpan<byte> data)
        {
            _buffer = data.ToArray();
            _length = _buffer.Length;
            _position = 0;
        }

        // ──────────────────────────────────────────────────────────
        // 1. 基础类型读取
        // ──────────────────────────────────────────────────────────

        /// <summary>读取 1 字节。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public byte ReadByte()
        {
            if (_position >= _length) return 0;
            return _buffer[_position++];
        }

        /// <summary>预览下一个字节 (不移动位置)。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public byte PeekByte()
        {
            if (_position >= _length) return 0;
            return _buffer[_position];
        }

        /// <summary>读取有符号字节 (1 字节)。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public sbyte ReadSByte()
        {
            if (_position >= _length) return 0;
            return unchecked((sbyte)_buffer[_position++]);
        }

        /// <summary>读取布尔值 (1 字节, 0 或 1)。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool ReadBool()
        {
            if (_position >= _length) return false;
            return _buffer[_position++] != 0;
        }

        /// <summary>读取 Int16 (2 字节, 小端序)。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public short ReadInt16()
        {
            if (_position + 2 > _length) { _position = _length; return 0; }
            short value = BinaryPrimitives.ReadInt16LittleEndian(_buffer.AsSpan(_position));
            _position += 2;
            return value;
        }

        /// <summary>读取 UInt16 (2 字节, 小端序)。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ushort ReadUInt16()
        {
            if (_position + 2 > _length) { _position = _length; return 0; }
            ushort value = BinaryPrimitives.ReadUInt16LittleEndian(_buffer.AsSpan(_position));
            _position += 2;
            return value;
        }

        /// <summary>读取 Int32 (4 字节, 小端序)。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int ReadInt32()
        {
            if (_position + 4 > _length) { _position = _length; return 0; }
            int value = BinaryPrimitives.ReadInt32LittleEndian(_buffer.AsSpan(_position));
            _position += 4;
            return value;
        }

        /// <summary>读取 UInt32 (4 字节, 小端序)。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public uint ReadUInt32()
        {
            if (_position + 4 > _length) { _position = _length; return 0; }
            uint value = BinaryPrimitives.ReadUInt32LittleEndian(_buffer.AsSpan(_position));
            _position += 4;
            return value;
        }

        /// <summary>读取 Int64 (8 字节, 小端序)。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public long ReadInt64()
        {
            if (_position + 8 > _length) { _position = _length; return 0; }
            long value = BinaryPrimitives.ReadInt64LittleEndian(_buffer.AsSpan(_position));
            _position += 8;
            return value;
        }

        /// <summary>读取 UInt64 (8 字节, 小端序)。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ulong ReadUInt64()
        {
            if (_position + 8 > _length) { _position = _length; return 0; }
            ulong value = BinaryPrimitives.ReadUInt64LittleEndian(_buffer.AsSpan(_position));
            _position += 8;
            return value;
        }

        /// <summary>读取 Float (4 字节, IEEE 754 小端序)。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float ReadFloat()
        {
            if (_position + 4 > _length) { _position = _length; return 0f; }
            float value = BinaryPrimitives.ReadSingleLittleEndian(_buffer.AsSpan(_position));
            _position += 4;
            return value;
        }

        /// <summary>读取 Double (8 字节, IEEE 754 小端序)。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public double ReadDouble()
        {
            if (_position + 8 > _length) { _position = _length; return 0d; }
            double value = BinaryPrimitives.ReadDoubleLittleEndian(_buffer.AsSpan(_position));
            _position += 8;
            return value;
        }

        /// <summary>读取字符串 (4 字节长度前缀 + UTF-8 编码)。</summary>
        public string ReadString()
        {
            int length = ReadInt32();
            if (length <= 0) return string.Empty;
            if (length > Remaining)
            {
                _position = _length;
                return string.Empty;
            }
            string value = Encoding.UTF8.GetString(_buffer, _position, length);
            _position += length;
            return value;
        }

        /// <summary>读取字节数组 (4 字节长度前缀 + 数据)。</summary>
        public byte[] ReadBytes()
        {
            int length = ReadInt32();
            if (length <= 0) return Array.Empty<byte>();
            return ReadBytesRaw(length);
        }

        /// <summary>读取指定长度的原始字节 (无长度前缀)。</summary>
        public byte[] ReadBytesRaw(int count)
        {
            if (count <= 0) return Array.Empty<byte>();
            int actualLen = Math.Min(count, Remaining);
            byte[] result = new byte[actualLen];
            if (actualLen > 0)
            {
                Array.Copy(_buffer, _position, result, 0, actualLen);
                _position += actualLen;
            }
            return result;
        }

        /// <summary>读取原始字节到目标数组。</summary>
        public int ReadRawBytes(byte[] destination, int offset, int count)
        {
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            int actualLen = Math.Min(count, Remaining);
            if (actualLen > 0)
            {
                Array.Copy(_buffer, _position, destination, offset, actualLen);
                _position += actualLen;
            }
            return actualLen;
        }

        // ──────────────────────────────────────────────────────────
        // 2. 数学类型读取
        // ──────────────────────────────────────────────────────────

        /// <summary>读取 Vector2 (2 x float = 8 字节)。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Vector2 ReadVector2()
        {
            return new Vector2(ReadFloat(), ReadFloat());
        }

        /// <summary>读取 Vector3 (3 x float = 12 字节)。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Vector3 ReadVector3()
        {
            return new Vector3(ReadFloat(), ReadFloat(), ReadFloat());
        }

        /// <summary>读取 Vector4 (4 x float = 16 字节)。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Vector4 ReadVector4()
        {
            return new Vector4(ReadFloat(), ReadFloat(), ReadFloat(), ReadFloat());
        }

        /// <summary>读取 Quaternion (4 x float = 16 字节)。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Quaternion ReadQuaternion()
        {
            return new Quaternion(ReadFloat(), ReadFloat(), ReadFloat(), ReadFloat());
        }

        /// <summary>读取 Matrix4x4 (16 x float = 64 字节)。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Matrix4x4 ReadMatrix4x4()
        {
            return new Matrix4x4(
                ReadFloat(), ReadFloat(), ReadFloat(), ReadFloat(),
                ReadFloat(), ReadFloat(), ReadFloat(), ReadFloat(),
                ReadFloat(), ReadFloat(), ReadFloat(), ReadFloat(),
                ReadFloat(), ReadFloat(), ReadFloat(), ReadFloat());
        }

        /// <summary>读取 Color (4 x float = 16 字节, RGBA 0~1)。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Vector4 ReadColor()
        {
            return new Vector4(ReadFloat(), ReadFloat(), ReadFloat(), ReadFloat());
        }

        // ──────────────────────────────────────────────────────────
        // 3. 位级读取
        // ──────────────────────────────────────────────────────────

        /// <summary>读取单个位 (1 bit)。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool ReadBit()
        {
            if (_bitCount == 0)
            {
                _bitAccumulator = ReadByte();
                _bitCount = 8;
                _bitPosition = 0;
            }

            bool value = (_bitAccumulator & (1 << _bitPosition)) != 0;
            _bitPosition++;
            _bitCount--;
            if (_bitPosition >= 8)
                _bitPosition = 0;
            return value;
        }

        /// <summary>读取指定位数的数据 (从低位到高位)。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public uint ReadBits(int bitCount)
        {
            if (bitCount <= 0) return 0;
            if (bitCount > 32) bitCount = 32;

            uint result = 0;
            for (int i = 0; i < bitCount; i++)
            {
                if (_bitCount == 0)
                {
                    _bitAccumulator = ReadByte();
                    _bitCount = 8;
                    _bitPosition = 0;
                }

                if ((_bitAccumulator & (1 << _bitPosition)) != 0)
                    result |= (1u << i);

                _bitPosition++;
                _bitCount--;
                if (_bitPosition >= 8)
                    _bitPosition = 0;
            }
            return result;
        }

        /// <summary>
        /// 读取变长编码的无符号整数 (VarInt)。
        /// 每 7 位一组, 高位 (MSB) 表示是否继续。
        /// </summary>
        public uint ReadBitsPacked()
        {
            uint result = 0;
            int shift = 0;

            while (true)
            {
                uint chunk = ReadBits(7);
                result |= (chunk << shift);

                bool hasMore = ReadBit();
                if (!hasMore) break;

                shift += 7;
                if (shift >= 35) break; // 防止无限循环 (最多 5 组 = 35 位)
            }

            return result;
        }

        /// <summary>
        /// 读取有符号变长编码的整数 (ZigZag + VarInt)。
        /// 先 VarInt 解码, 再 ZigZag 解码。
        /// </summary>
        public int ReadBitsPackedSigned()
        {
            uint encoded = ReadBitsPacked();
            // ZigZag 解码: (encoded >> 1) ^ -(encoded & 1)
            return (int)((encoded >> 1) ^ (uint)(-(int)(encoded & 1)));
        }

        /// <summary>刷新/对齐位缓冲区到字节边界。</summary>
        public void FlushBits()
        {
            _bitAccumulator = 0;
            _bitCount = 0;
            _bitPosition = 0;
        }

        // ──────────────────────────────────────────────────────────
        // 4. 量化读取
        // ──────────────────────────────────────────────────────────

        /// <summary>
        /// 反量化浮点读取。
        /// 算法: ReadBits(bitCount) -> [0,2^bitCount-1] -> [0,1] -> [min,max]
        /// </summary>
        public float ReadQuantizedFloat(float min, float max, int bitCount)
        {
            if (bitCount <= 0) bitCount = 1;
            if (bitCount > 32) bitCount = 32;

            uint maxQuantized = bitCount >= 32 ? uint.MaxValue : ((1u << bitCount) - 1u);
            uint quantized = ReadBits(bitCount);

            float normalized = (float)quantized / (float)maxQuantized;
            return min + normalized * (max - min);
        }

        /// <summary>
        /// 反量化 Vector3 读取 (每个分量独立反量化)。
        /// </summary>
        public Vector3 ReadQuantizedVector3(Vector3 min, Vector3 max, int bitsPerComponent)
        {
            float x = ReadQuantizedFloat(min.X, max.X, bitsPerComponent);
            float y = ReadQuantizedFloat(min.Y, max.Y, bitsPerComponent);
            float z = ReadQuantizedFloat(min.Z, max.Z, bitsPerComponent);
            return new Vector3(x, y, z);
        }

        /// <summary>
        /// 读取压缩四元数 (最小三表示法)。
        /// 格式: 1 字节 (索引 + 符号) + 3 x float16 (6 字节) = 7 字节
        /// 算法:
        ///   1. 读取索引 (0~3) 和符号
        ///   2. 读取 3 个 float16 分量
        ///   3. 如果符号为负, 对 3 个分量取反
        ///   4. 根据索引重建第 4 个分量: sqrt(1 - a^2 - b^2 - c^2)
        ///   5. 如果符号为负, 第 4 个分量为负
        /// </summary>
        public Quaternion ReadCompressedQuaternion()
        {
            byte indexByte = ReadByte();
            int largestIndex = indexByte & 0x03;
            bool isNegative = (indexByte & 0x04) != 0;

            // 读取 3 个 float16 分量
            float a = HalfToFloat(ReadUInt16());
            float b = HalfToFloat(ReadUInt16());
            float c = HalfToFloat(ReadUInt16());

            // 如果符号为负, 取反
            if (isNegative) { a = -a; b = -b; c = -c; }

            // 重建第 4 个分量 (最大分量): sqrt(1 - a^2 - b^2 - c^2)
            float sumOfSquares = a * a + b * b + c * c;
            float largest = (float)Math.Sqrt(Math.Max(0f, 1f - sumOfSquares));

            // 根据索引分配分量
            float x, y, z, w;
            switch (largestIndex)
            {
                case 0: x = largest; y = a; z = b; w = c; break;
                case 1: x = a; y = largest; z = b; w = c; break;
                case 2: x = a; y = b; z = largest; w = c; break;
                default: x = a; y = b; z = c; w = largest; break;
            }

            return new Quaternion(x, y, z, w);
        }

        /// <summary>
        /// 读取压缩旋转 (欧拉角量化)。
        /// 算法:
        ///   1. 读取 3 个 11 位量化角度
        ///   2. 反量化到 [-pi, pi]
        ///   3. 欧拉角转四元数
        /// </summary>
        public Quaternion ReadCompressedRotation()
        {
            const int BitsPerAngle = 11;
            const float AngleMin = -(float)Math.PI;
            const float AngleMax = (float)Math.PI;

            float roll = ReadQuantizedFloat(AngleMin, AngleMax, BitsPerAngle);
            float pitch = ReadQuantizedFloat(AngleMin, AngleMax, BitsPerAngle);
            float yaw = ReadQuantizedFloat(AngleMin, AngleMax, BitsPerAngle);

            // 对齐到字节边界
            FlushBits();

            // 欧拉角转四元数 (ZYX 顺序)
            float cy = (float)Math.Cos(yaw * 0.5f);
            float sy = (float)Math.Sin(yaw * 0.5f);
            float cp = (float)Math.Cos(pitch * 0.5f);
            float sp = (float)Math.Sin(pitch * 0.5f);
            float cr = (float)Math.Cos(roll * 0.5f);
            float sr = (float)Math.Sin(roll * 0.5f);

            float qw = cr * cp * cy + sr * sp * sy;
            float qx = sr * cp * cy - cr * sp * sy;
            float qy = cr * sp * cy + sr * cp * sy;
            float qz = cr * cp * sy - sr * sp * cy;

            return new Quaternion(qx, qy, qz, qw);
        }

        // ──────────────────────────────────────────────────────────
        // 5. 集合读取
        // ──────────────────────────────────────────────────────────

        /// <summary>读取数组 (4 字节长度前缀 + 逐元素反序列化)。</summary>
        public T[] ReadArray<T>(Func<T> readFunc)
        {
            if (readFunc == null) throw new ArgumentNullException(nameof(readFunc));
            int count = ReadInt32();
            if (count <= 0) return Array.Empty<T>();
            T[] result = new T[count];
            for (int i = 0; i < count; i++)
                result[i] = readFunc();
            return result;
        }

        /// <summary>读取列表 (4 字节长度前缀 + 逐元素反序列化)。</summary>
        public List<T> ReadList<T>(Func<T> readFunc)
        {
            if (readFunc == null) throw new ArgumentNullException(nameof(readFunc));
            int count = ReadInt32();
            List<T> result = new List<T>(Math.Max(0, count));
            for (int i = 0; i < count; i++)
                result.Add(readFunc());
            return result;
        }

        /// <summary>读取字典 (4 字节长度前缀 + 逐键值对反序列化)。</summary>
        public Dictionary<K, V> ReadDictionary<K, V>(Func<K> readKey, Func<V> readValue)
        {
            if (readKey == null) throw new ArgumentNullException(nameof(readKey));
            if (readValue == null) throw new ArgumentNullException(nameof(readValue));
            int count = ReadInt32();
            Dictionary<K, V> result = new Dictionary<K, V>(Math.Max(0, count));
            for (int i = 0; i < count; i++)
            {
                K key = readKey();
                V value = readValue();
                result[key] = value;
            }
            return result;
        }

        // ──────────────────────────────────────────────────────────
        // 6. 缓冲区管理
        // ──────────────────────────────────────────────────────────

        /// <summary>获取当前读取位置。</summary>
        public int GetPosition() => _position;

        /// <summary>设置读取位置。</summary>
        public void SetPosition(int position)
        {
            _position = Math.Clamp(position, 0, _length);
        }

        /// <summary>获取剩余字节数。</summary>
        public int GetRemainingBytes() => _length - _position;

        /// <summary>跳过指定字节数。</summary>
        public void Skip(int count)
        {
            _position = Math.Min(_position + count, _length);
        }

        /// <summary>对齐到字节边界 (清除位缓冲区)。</summary>
        public void AlignToByte()
        {
            FlushBits();
        }

        /// <summary>边界检查: 检查是否还有足够字节可读。</summary>
        public bool CheckBounds(int requiredBytes)
        {
            return _position + requiredBytes <= _length;
        }

        /// <summary>获取剩余数据的只读 Span。</summary>
        public ReadOnlySpan<byte> RemainingSpan()
        {
            return _buffer.AsSpan(_position, Remaining);
        }

        // ──────────────────────────────────────────────────────────
        // 7. Float16 转换 (IEEE 754 半精度)
        // ──────────────────────────────────────────────────────────

        /// <summary>
        /// 将 16 位半精度浮点数转换为 32 位浮点数 (IEEE 754)。
        /// </summary>
        private static float HalfToFloat(ushort half)
        {
            uint sign = ((uint)half & 0x8000) << 16;
            int exponent = (half >> 10) & 0x1F;
            uint mantissa = half & 0x3FF;

            if (exponent == 0)
            {
                if (mantissa == 0)
                    return BitConverter.Int32BitsToSingle((int)sign);

                // 子normal: 规范化
                while ((mantissa & 0x400) == 0)
                {
                    mantissa <<= 1;
                    exponent--;
                }
                exponent += 1;
                mantissa &= 0x3FF;
            }

            exponent = exponent - 15 + 127;
            uint floatBits = sign | ((uint)exponent << 23) | (mantissa << 13);
            return BitConverter.Int32BitsToSingle((int)floatBits);
        }
    }
}
