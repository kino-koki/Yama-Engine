using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;

namespace YamaEngine.Network
{
    // ════════════════════════════════════════════════════════════════════════
    // yama引擎 - 真实网络二进制写入器 (NetworkWriterReal)
    //
    // 高性能二进制写入器, 涵盖:
    //   * 基础类型: byte/sbyte/bool/int16~uint64/float/double/string/bytes
    //   * 数学类型: Vector2/3/4, Quaternion, Matrix4x4, Color
    //   * 位级写入: WriteBit/WriteBits/WriteBitsPacked(VarInt)/WriteBitsPackedSigned(ZigZag)
    //   * 量化写入: WriteQuantizedFloat/Vector3/CompressedQuaternion(Smallest3)/CompressedRotation(Euler)
    //   * 集合写入: WriteArray<T>/WriteDictionary<K,V>/WriteList<T>
    //   * 缓冲区管理: 自动扩容/ArrayPool/64KB初始/16MB最大
    //
    // 所有方法均为真实实现, 无占位式实现。
    // 目标框架: net8.0 (C# 8.0+), 无额外 NuGet 依赖。
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 高性能网络二进制写入器。使用 ArrayPool 内存池化, 支持位级打包、
    /// 量化压缩和集合序列化。默认 64KB 初始容量, 最大 16MB。
    /// </summary>
    public sealed class NetworkWriterReal : IDisposable
    {
        // ── 常量 ──────────────────────────────────────────────────

        /// <summary>默认初始容量: 64KB。</summary>
        private const int DefaultInitialCapacity = 65536;

        /// <summary>最大容量: 16MB。</summary>
        private const int MaxCapacity = 16 * 1024 * 1024;

        // ── 缓冲区状态 ────────────────────────────────────────────

        private byte[] _buffer;
        private int _position;
        private int _capacity;
        private bool _disposed;
        private readonly bool _pooled;

        // ── 位缓冲区状态 ──────────────────────────────────────────

        private byte _bitAccumulator;
        private int _bitCount;

        // ── 属性 ──────────────────────────────────────────────────

        /// <summary>当前写入位置 (字节)。</summary>
        public int Position => _position;

        /// <summary>已写入的数据长度 (字节)。</summary>
        public int Length => _position;

        /// <summary>缓冲区总容量。</summary>
        public int Capacity => _capacity;

        /// <summary>剩余可用容量 (字节)。</summary>
        public int GetRemainingBytes() => _capacity - _position;

        /// <summary>获取内部缓冲区引用 (注意: 有效长度需使用 Length)。</summary>
        public byte[] Buffer => _buffer;

        /// <summary>获取已写入数据的只读 Span。</summary>
        public ReadOnlySpan<byte> AsSpan() => _buffer.AsSpan(0, _position);

        /// <summary>获取已写入数据的只读 Memory。</summary>
        public ReadOnlyMemory<byte> AsMemory() => _buffer.AsMemory(0, _position);

        // ── 构造函数 ──────────────────────────────────────────────

        /// <summary>创建指定初始容量的写入器 (从 ArrayPool 租用缓冲区)。</summary>
        public NetworkWriterReal(int capacity = DefaultInitialCapacity)
        {
            _capacity = Math.Max(capacity, 64);
            _pooled = true;
            _buffer = ArrayPool<byte>.Shared.Rent(_capacity);
            _position = 0;
            _disposed = false;
        }

        /// <summary>使用外部缓冲区创建写入器 (不使用 ArrayPool)。</summary>
        public NetworkWriterReal(byte[] buffer, int offset = 0)
        {
            _buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
            _position = offset;
            _capacity = buffer.Length;
            _pooled = false;
            _disposed = false;
        }

        // ──────────────────────────────────────────────────────────
        // 1. 基础类型写入
        // ──────────────────────────────────────────────────────────

        /// <summary>写入 1 字节。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteByte(byte value)
        {
            EnsureCapacity(1);
            _buffer[_position++] = value;
        }

        /// <summary>写入有符号字节 (1 字节)。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteSByte(sbyte value)
        {
            EnsureCapacity(1);
            _buffer[_position++] = unchecked((byte)value);
        }

        /// <summary>写入布尔值 (1 字节, 0 或 1)。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteBool(bool value)
        {
            EnsureCapacity(1);
            _buffer[_position++] = value ? (byte)1 : (byte)0;
        }

        /// <summary>写入 Int16 (2 字节, 小端序)。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteInt16(short value)
        {
            EnsureCapacity(2);
            BinaryPrimitives.WriteInt16LittleEndian(_buffer.AsSpan(_position), value);
            _position += 2;
        }

        /// <summary>写入 UInt16 (2 字节, 小端序)。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteUInt16(ushort value)
        {
            EnsureCapacity(2);
            BinaryPrimitives.WriteUInt16LittleEndian(_buffer.AsSpan(_position), value);
            _position += 2;
        }

        /// <summary>写入 Int32 (4 字节, 小端序)。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteInt32(int value)
        {
            EnsureCapacity(4);
            BinaryPrimitives.WriteInt32LittleEndian(_buffer.AsSpan(_position), value);
            _position += 4;
        }

        /// <summary>写入 UInt32 (4 字节, 小端序)。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteUInt32(uint value)
        {
            EnsureCapacity(4);
            BinaryPrimitives.WriteUInt32LittleEndian(_buffer.AsSpan(_position), value);
            _position += 4;
        }

        /// <summary>写入 Int64 (8 字节, 小端序)。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteInt64(long value)
        {
            EnsureCapacity(8);
            BinaryPrimitives.WriteInt64LittleEndian(_buffer.AsSpan(_position), value);
            _position += 8;
        }

        /// <summary>写入 UInt64 (8 字节, 小端序)。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteUInt64(ulong value)
        {
            EnsureCapacity(8);
            BinaryPrimitives.WriteUInt64LittleEndian(_buffer.AsSpan(_position), value);
            _position += 8;
        }

        /// <summary>写入 Float (4 字节, IEEE 754 小端序)。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteFloat(float value)
        {
            EnsureCapacity(4);
            BinaryPrimitives.WriteSingleLittleEndian(_buffer.AsSpan(_position), value);
            _position += 4;
        }

        /// <summary>写入 Double (8 字节, IEEE 754 小端序)。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteDouble(double value)
        {
            EnsureCapacity(8);
            BinaryPrimitives.WriteDoubleLittleEndian(_buffer.AsSpan(_position), value);
            _position += 8;
        }

        /// <summary>写入字符串 (4 字节长度前缀 + UTF-8 编码)。</summary>
        public void WriteString(string value)
        {
            if (value == null) value = string.Empty;
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            WriteInt32(bytes.Length);
            if (bytes.Length > 0)
                WriteRawBytes(bytes, 0, bytes.Length);
        }

        /// <summary>写入字节数组 (4 字节长度前缀 + 数据)。</summary>
        public void WriteBytes(byte[] data)
        {
            if (data == null || data.Length == 0)
            {
                WriteInt32(0);
                return;
            }
            WriteInt32(data.Length);
            WriteRawBytes(data, 0, data.Length);
        }

        /// <summary>写入字节数组片段 (4 字节长度前缀 + 数据)。</summary>
        public void WriteBytes(byte[] data, int offset, int count)
        {
            WriteInt32(count);
            WriteRawBytes(data, offset, count);
        }

        /// <summary>写入原始字节 (无长度前缀)。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteBytesRaw(byte[] data)
        {
            if (data == null || data.Length == 0) return;
            WriteRawBytes(data, 0, data.Length);
        }

        /// <summary>写入只读 Span 的原始字节 (无长度前缀, 兼容 VarIntUtility)。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteRawBytes(ReadOnlySpan<byte> data)
        {
            if (data.IsEmpty) return;
            EnsureCapacity(data.Length);
            data.CopyTo(_buffer.AsSpan(_position));
            _position += data.Length;
        }

        /// <summary>写入原始字节 (无长度前缀)。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteRawBytes(byte[] data, int offset, int count)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (count <= 0) return;
            EnsureCapacity(count);
            Array.Copy(data, offset, _buffer, _position, count);
            _position += count;
        }

        // ──────────────────────────────────────────────────────────
        // 2. 数学类型写入
        // ──────────────────────────────────────────────────────────

        /// <summary>写入 Vector2 (2 x float = 8 字节)。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteVector2(Vector2 v)
        {
            WriteFloat(v.X);
            WriteFloat(v.Y);
        }

        /// <summary>写入 Vector3 (3 x float = 12 字节)。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteVector3(Vector3 v)
        {
            WriteFloat(v.X);
            WriteFloat(v.Y);
            WriteFloat(v.Z);
        }

        /// <summary>写入 Vector4 (4 x float = 16 字节)。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteVector4(Vector4 v)
        {
            WriteFloat(v.X);
            WriteFloat(v.Y);
            WriteFloat(v.Z);
            WriteFloat(v.W);
        }

        /// <summary>写入 Quaternion (4 x float = 16 字节)。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteQuaternion(Quaternion q)
        {
            WriteFloat(q.X);
            WriteFloat(q.Y);
            WriteFloat(q.Z);
            WriteFloat(q.W);
        }

        /// <summary>写入 Matrix4x4 (16 x float = 64 字节)。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteMatrix4x4(Matrix4x4 m)
        {
            WriteFloat(m.M11); WriteFloat(m.M12); WriteFloat(m.M13); WriteFloat(m.M14);
            WriteFloat(m.M21); WriteFloat(m.M22); WriteFloat(m.M23); WriteFloat(m.M24);
            WriteFloat(m.M31); WriteFloat(m.M32); WriteFloat(m.M33); WriteFloat(m.M34);
            WriteFloat(m.M41); WriteFloat(m.M42); WriteFloat(m.M43); WriteFloat(m.M44);
        }

        /// <summary>写入 Color (4 x float = 16 字节, RGBA 0~1)。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteColor(Vector4 color)
        {
            WriteFloat(color.X);
            WriteFloat(color.Y);
            WriteFloat(color.Z);
            WriteFloat(color.W);
        }

        // ──────────────────────────────────────────────────────────
        // 3. 位级写入
        // ──────────────────────────────────────────────────────────

        /// <summary>写入单个位 (1 bit)。满 8 位刷新到字节缓冲区。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
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

        /// <summary>写入指定位数的数据 (从低位到高位写入)。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteBits(uint value, int bitCount)
        {
            if (bitCount <= 0) return;
            if (bitCount > 32) bitCount = 32;

            for (int i = 0; i < bitCount; i++)
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

        /// <summary>
        /// 写入变长编码的无符号整数 (VarInt)。
        /// 每 7 位一组, 高位 (MSB) 表示是否继续。
        /// 通过位缓冲区写入, 非字节对齐。
        /// </summary>
        public void WriteBitsPacked(uint value)
        {
            do
            {
                uint chunk = value & 0x7Fu;
                value >>= 7;
                bool hasMore = value != 0;

                // 写入 7 位数据
                WriteBits(chunk, 7);
                // 写入 1 位继续标志
                WriteBit(hasMore);
            }
            while (value != 0);
        }

        /// <summary>
        /// 写入有符号变长编码的整数 (ZigZag + VarInt)。
        /// ZigZag: 0->0, -1->1, 1->2, -2->3, 2->4 ...
        /// 然后 VarInt 编码。
        /// </summary>
        public void WriteBitsPackedSigned(int value)
        {
            // ZigZag 编码: 将有符号映射到无符号, 小绝对值 -> 小无符号值
            uint encoded = (uint)((value << 1) ^ (value >> 31));
            WriteBitsPacked(encoded);
        }

        /// <summary>刷新位缓冲区, 补 0 到整字节边界。</summary>
        public void FlushBits()
        {
            if (_bitCount > 0)
            {
                WriteByte(_bitAccumulator);
                _bitAccumulator = 0;
                _bitCount = 0;
            }
        }

        // ──────────────────────────────────────────────────────────
        // 4. 量化写入
        // ──────────────────────────────────────────────────────────

        /// <summary>
        /// 量化浮点写入。
        /// 算法: value -> [0,1] 归一化 -> [0, 2^bitCount-1] 量化 -> WriteBits
        /// </summary>
        /// <param name="value">要量化的值。</param>
        /// <param name="min">范围最小值。</param>
        /// <param name="max">范围最大值。</param>
        /// <param name="bitCount">量化位宽 (1~32)。</param>
        public void WriteQuantizedFloat(float value, float min, float max, int bitCount)
        {
            if (bitCount <= 0) bitCount = 1;
            if (bitCount > 32) bitCount = 32;

            float range = max - min;
            if (range == 0f)
            {
                WriteBits(0, bitCount);
                return;
            }

            // 归一化到 [0, 1]
            float normalized = (value - min) / range;
            normalized = Math.Clamp(normalized, 0f, 1f);

            // 量化到 [0, 2^bitCount - 1]
            uint maxQuantized = bitCount >= 32 ? uint.MaxValue : ((1u << bitCount) - 1u);
            uint quantized = (uint)Math.Round(normalized * maxQuantized);
            quantized = Math.Min(quantized, maxQuantized);

            WriteBits(quantized, bitCount);
        }

        /// <summary>
        /// 量化 Vector3 写入 (每个分量独立量化)。
        /// </summary>
        public void WriteQuantizedVector3(Vector3 value, Vector3 min, Vector3 max, int bitsPerComponent)
        {
            WriteQuantizedFloat(value.X, min.X, max.X, bitsPerComponent);
            WriteQuantizedFloat(value.Y, min.Y, max.Y, bitsPerComponent);
            WriteQuantizedFloat(value.Z, min.Z, max.Z, bitsPerComponent);
        }

        /// <summary>
        /// 压缩四元数写入 (最小三表示法 Smallest Three)。
        /// 算法:
        ///   1. 找绝对值最大的分量, 记录索引 (0=X, 1=Y, 2=Z, 3=W)
        ///   2. 丢弃最大分量, 用其他 3 个分量 + 索引重建
        ///   3. 如果最大分量为负, 对其他 3 个分量取反 (保证符号一致)
        ///   4. 其他 3 个分量用 float16 量化
        /// 格式: 1 字节 (索引 + 符号) + 3 x float16 (6 字节) = 7 字节
        /// </summary>
        public void WriteCompressedQuaternion(Quaternion q)
        {
            // 找绝对值最大的分量
            float absX = Math.Abs(q.X);
            float absY = Math.Abs(q.Y);
            float absZ = Math.Abs(q.Z);
            float absW = Math.Abs(q.W);

            byte largestIndex = 0;
            float maxAbs = absX;
            if (absY > maxAbs) { maxAbs = absY; largestIndex = 1; }
            if (absZ > maxAbs) { maxAbs = absZ; largestIndex = 2; }
            if (absW > maxAbs) { maxAbs = absW; largestIndex = 3; }

            // 获取最大分量的值和符号
            float largestValue = largestIndex switch
            {
                0 => q.X, 1 => q.Y, 2 => q.Z, _ => q.W
            };
            bool isNegative = largestValue < 0;

            // 提取其他三个分量 (如果最大分量为负, 取反)
            float a, b, c;
            switch (largestIndex)
            {
                case 0: a = q.Y; b = q.Z; c = q.W; break;
                case 1: a = q.X; b = q.Z; c = q.W; break;
                case 2: a = q.X; b = q.Y; c = q.W; break;
                default: a = q.X; b = q.Y; c = q.Z; break;
            }
            if (isNegative) { a = -a; b = -b; c = -c; }

            // 写入 1 字节: 低 2 位 = 索引, 位 2 = 符号
            byte indexByte = (byte)(largestIndex | (isNegative ? 4 : 0));
            WriteByte(indexByte);

            // 写入 3 个 float16 (每个 2 字节, 小端序)
            WriteUInt16(FloatToHalf(a));
            WriteUInt16(FloatToHalf(b));
            WriteUInt16(FloatToHalf(c));
        }

        /// <summary>
        /// 压缩旋转写入 (欧拉角量化)。
        /// 算法:
        ///   1. 四元数 -> 欧拉角 (pitch, yaw, roll), 范围 [-pi, pi]
        ///   2. 每个角度量化到 11 位 (0~2047)
        ///   3. 总计 3 x 11 = 33 位 -> 5 字节 (补 0 对齐)
        /// </summary>
        public void WriteCompressedRotation(Quaternion q)
        {
            // 四元数转欧拉角
            // Roll (X 轴)
            float sinr_cosp = 2f * (q.W * q.X + q.Y * q.Z);
            float cosr_cosp = 1f - 2f * (q.X * q.X + q.Y * q.Y);
            float roll = (float)Math.Atan2(sinr_cosp, cosr_cosp);

            // Pitch (Y 轴)
            float sinp = 2f * (q.W * q.Y - q.Z * q.X);
            float pitch;
            if (Math.Abs(sinp) >= 1f)
                pitch = (float)Math.CopySign(Math.PI / 2f, sinp);
            else
                pitch = (float)Math.Asin(sinp);

            // Yaw (Z 轴)
            float siny_cosp = 2f * (q.W * q.Z + q.X * q.Y);
            float cosy_cosp = 1f - 2f * (q.Y * q.Y + q.Z * q.Z);
            float yaw = (float)Math.Atan2(siny_cosp, cosy_cosp);

            // 量化到 11 位 (范围 [-pi, pi])
            const int BitsPerAngle = 11;
            const float AngleMin = -(float)Math.PI;
            const float AngleMax = (float)Math.PI;

            WriteQuantizedFloat(roll, AngleMin, AngleMax, BitsPerAngle);
            WriteQuantizedFloat(pitch, AngleMin, AngleMax, BitsPerAngle);
            WriteQuantizedFloat(yaw, AngleMin, AngleMax, BitsPerAngle);

            // 对齐到字节边界
            FlushBits();
        }

        // ──────────────────────────────────────────────────────────
        // 5. 集合写入
        // ──────────────────────────────────────────────────────────

        /// <summary>写入数组 (4 字节长度前缀 + 逐元素序列化)。</summary>
        public void WriteArray<T>(T[] array, Action<T> writeFunc)
        {
            if (writeFunc == null) throw new ArgumentNullException(nameof(writeFunc));
            int count = array?.Length ?? 0;
            WriteInt32(count);
            if (array != null)
            {
                for (int i = 0; i < array.Length; i++)
                    writeFunc(array[i]);
            }
        }

        /// <summary>写入列表 (4 字节长度前缀 + 逐元素序列化)。</summary>
        public void WriteList<T>(List<T> list, Action<T> writeFunc)
        {
            if (writeFunc == null) throw new ArgumentNullException(nameof(writeFunc));
            int count = list?.Count ?? 0;
            WriteInt32(count);
            if (list != null)
            {
                for (int i = 0; i < list.Count; i++)
                    writeFunc(list[i]);
            }
        }

        /// <summary>写入字典 (4 字节长度前缀 + 逐键值对序列化)。</summary>
        public void WriteDictionary<K, V>(Dictionary<K, V> dict, Action<K> writeKey, Action<V> writeValue)
        {
            if (writeKey == null) throw new ArgumentNullException(nameof(writeKey));
            if (writeValue == null) throw new ArgumentNullException(nameof(writeValue));
            int count = dict?.Count ?? 0;
            WriteInt32(count);
            if (dict != null)
            {
                foreach (var kvp in dict)
                {
                    writeKey(kvp.Key);
                    writeValue(kvp.Value);
                }
            }
        }

        // ──────────────────────────────────────────────────────────
        // 6. 缓冲区管理
        // ──────────────────────────────────────────────────────────

        /// <summary>获取已写入数据的副本。</summary>
        public byte[] ToArray()
        {
            if (_position == 0) return Array.Empty<byte>();
            byte[] result = new byte[_position];
            Array.Copy(_buffer, result, _position);
            return result;
        }

        /// <summary>重置写入位置 (保留缓冲区)。</summary>
        public void Reset()
        {
            _position = 0;
            _bitAccumulator = 0;
            _bitCount = 0;
        }

        /// <summary>清空缓冲区并重置位置。</summary>
        public void Clear()
        {
            Array.Clear(_buffer, 0, _position);
            _position = 0;
            _bitAccumulator = 0;
            _bitCount = 0;
        }

        /// <summary>获取当前写入位置。</summary>
        public int GetPosition() => _position;

        /// <summary>设置写入位置 (用于回写/覆盖)。</summary>
        public void SetPosition(int position)
        {
            _position = Math.Clamp(position, 0, _capacity);
        }

        /// <summary>在指定偏移处写入 Int32 (不影响当前 Position, 用于回填长度)。</summary>
        public void WriteInt32At(int offset, int value)
        {
            if (offset < 0 || offset + 4 > _capacity)
                throw new ArgumentOutOfRangeException(nameof(offset));
            BinaryPrimitives.WriteInt32LittleEndian(_buffer.AsSpan(offset), value);
        }

        /// <summary>确保缓冲区有足够的剩余容量。</summary>
        public void EnsureCapacity(int additionalBytes)
        {
            int required = _position + additionalBytes;
            if (required <= _capacity) return;

            int newCapacity = Math.Max(_capacity * 2, required);
            if (newCapacity > MaxCapacity)
            {
                if (required > MaxCapacity)
                    throw new InvalidOperationException(
                        $"Buffer capacity {required} exceeds maximum {MaxCapacity}");
                newCapacity = MaxCapacity;
            }

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

        /// <summary>预分配空间。</summary>
        public void Reserve(int additionalBytes) => EnsureCapacity(additionalBytes);

        /// <summary>将写入器内容写入目标流。</summary>
        public void CopyTo(System.IO.Stream stream)
        {
            if (_position > 0)
                stream.Write(_buffer, 0, _position);
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

        // ──────────────────────────────────────────────────────────
        // 7. Float16 转换 (IEEE 754 半精度)
        // ──────────────────────────────────────────────────────────

        /// <summary>
        /// 将 32 位浮点数转换为 16 位半精度浮点数 (IEEE 754 half)。
        /// </summary>
        private static ushort FloatToHalf(float value)
        {
            uint bits = (uint)BitConverter.SingleToInt32Bits(value);
            uint sign = (bits >> 16) & 0x8000;
            int exponent = (int)((bits >> 23) & 0xFF) - 127 + 15;
            uint mantissa = bits & 0x7FFFFF;

            if (exponent <= 0)
            {
                // 子normal 或零
                if (exponent < -10)
                    return (ushort)sign;

                mantissa |= 0x800000;
                int shift = 14 - exponent;
                uint rounded = mantissa >> shift;

                // 四舍五入到最近偶数
                uint remainder = mantissa & ((1U << shift) - 1);
                uint halfWay = 1U << (shift - 1);
                if (remainder > halfWay || (remainder == halfWay && (rounded & 1) != 0))
                    rounded++;

                return (ushort)(sign | rounded);
            }
            else if (exponent >= 31)
            {
                // Inf 或 NaN
                return (ushort)(sign | 0x7C00 | (mantissa != 0 ? 0x200 : 0));
            }
            else
            {
                // Normal
                return (ushort)(sign | (exponent << 10) | (mantissa >> 13));
            }
        }
    }
}
