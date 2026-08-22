using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;

namespace ShanyuEngine.Network
{
    // ════════════════════════════════════════════════════════════════════════
    // 山雨引擎 - 网络序列化系统 (NetworkSerializationSystem)
    //
    // 高效网络数据序列化/反序列化管线, 涵盖:
    //   * INetworkSerializable: 网络序列化接口 (Serialize/Deserialize)
    //   * BitPacker: 位级打包 (布尔1位/枚举最小位/整数可变长/浮点量化)
    //   * VarInt: 变长整数编码 (1~10字节, 小值高效)
    //   * 浮点量化: Vector3 16bit/Quaternion 最小旋转/Float 8/16/32bit
    //   * CRC32: 网络消息完整性校验哈希
    //   * 消息分片: 大消息自动分片/重组/超时重传
    //   * 网络字节序处理: 大端/小端
    //
    // 对接系统: NetworkCoreSystem / RPCSystem / PropertyReplicationSystem
    // ════════════════════════════════════════════════════════════════════════

    // ──────────────────────────────────────────────────────────────
    // 1. 网络序列化接口
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 网络序列化接口。实现此接口的类型可自定义网络序列化/反序列化逻辑。
    /// </summary>
    public interface INetworkSerializable
    {
        /// <summary>序列化到网络写入器。</summary>
        void Serialize(NetworkWriter writer);

        /// <summary>从网络读取器反序列化。</summary>
        void Deserialize(NetworkReader reader);
    }

    // ──────────────────────────────────────────────────────────────
    // 2. 位打包器
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 位级写入器。将数据以位为单位紧凑写入, 显著降低网络带宽占用。
    /// 支持: 布尔1位, 枚举最小位, 整数可变长度, 浮点量化。
    /// </summary>
    public sealed class BitPacker
    {
        private byte[] _buffer;
        private int _bitPosition;
        private int _capacityBits;

        /// <summary>当前位位置。</summary>
        public int BitPosition => _bitPosition;

        /// <summary>当前字节位置。</summary>
        public int BytePosition => _bitPosition >> 3;

        /// <summary>总位数。</summary>
        public int TotalBits => _bitPosition;

        public BitPacker(int capacityBytes = 256)
        {
            _buffer = new byte[capacityBytes];
            _capacityBits = capacityBytes * 8;
        }

        /// <summary>获取缓冲区 (包含已写入的数据)。</summary>
        public byte[] GetBuffer(int outByteCount)
        {
            byte[] result = new byte[outByteCount];
            Array.Copy(_buffer, result, outByteCount);
            return result;
        }

        /// <summary>获取完整缓冲区引用。</summary>
        public byte[] Buffer => _buffer;

        // ── 写入操作 ──────────────────────────────────────────────

        /// <summary>写入1位布尔值。</summary>
        public void WriteBool(bool value)
        {
            EnsureCapacity(1);
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
            EnsureCapacity(bits);

            for (int i = 0; i < bits; i++)
            {
                if ((value & (1u << i)) != 0)
                    _buffer[_bitPosition >> 3] |= (byte)(1 << (_bitPosition & 7));
                else
                    _buffer[_bitPosition >> 3] &= (byte)~(1 << (_bitPosition & 7));
                _bitPosition++;
            }
        }

        /// <summary>写入有符号整数。</summary>
        public void WriteInt(int value, int bits)
        {
            WriteUInt((uint)value, bits);
        }

        /// <summary>写入枚举 (使用最小位数)。</summary>
        public void WriteEnum<T>(T value) where T : Enum
        {
            string[] names = Enum.GetNames(typeof(T));
            int maxIndex = names.Length - 1;
            int bitsNeeded = maxIndex <= 0 ? 1 : (int)Math.Ceiling(Math.Log2(maxIndex + 1));
            int index = Convert.ToInt32(value);
            WriteUInt((uint)index, bitsNeeded);
        }

        /// <summary>写入量化浮点 (指定位宽)。</summary>
        public void WriteQuantizedFloat(float value, float min, float max, int bits)
        {
            if (bits <= 0 || bits > 32) { WriteFloat(value); return; }
            float normalized = (value - min) / (max - min);
            normalized = Math.Clamp(normalized, 0f, 1f);
            uint quantized = (uint)(normalized * ((1u << bits) - 1));
            WriteUInt(quantized, bits);
        }

        /// <summary>写入普通浮点 (32位)。</summary>
        public void WriteFloat(float value)
        {
            WriteUInt(unchecked((uint)BitConverter.SingleToInt32Bits(value)), 32);
        }

        /// <summary>写入字节对齐填充 (跳到下一个字节边界)。</summary>
        public void WritePadToByte()
        {
            int remainder = _bitPosition & 7;
            if (remainder > 0)
                _bitPosition += 8 - remainder;
        }

        /// <summary>写入原始字节 (必须是字节对齐的)。</summary>
        public void WriteBytes(byte[] data, int offset, int count)
        {
            WritePadToByte();
            EnsureCapacity(count * 8);
            Array.Copy(data, offset, _buffer, _bitPosition >> 3, count);
            _bitPosition += count * 8;
        }

        private void EnsureCapacity(int additionalBits)
        {
            while (_bitPosition + additionalBits > _capacityBits)
            {
                int newSize = _buffer.Length * 2;
                Array.Resize(ref _buffer, newSize);
                _capacityBits = newSize * 8;
            }
        }

        /// <summary>重置。</summary>
        public void Reset() { _bitPosition = 0; }
    }

    /// <summary>
    /// 位级读取器。从位流中读取紧凑编码的数据。
    /// </summary>
    public sealed class BitUnpacker
    {
        private readonly byte[] _buffer;
        private int _bitPosition;
        private readonly int _capacityBits;

        /// <summary>当前位位置。</summary>
        public int BitPosition => _bitPosition;

        /// <summary>是否还有更多数据。</summary>
        public bool HasMore => _bitPosition < _capacityBits;

        public BitUnpacker(byte[] data)
        {
            _buffer = data;
            _capacityBits = data.Length * 8;
        }

        public BitUnpacker(byte[] data, int bitOffset)
        {
            _buffer = data;
            _bitPosition = bitOffset;
            _capacityBits = data.Length * 8;
        }

        // ── 读取操作 ──────────────────────────────────────────────

        /// <summary>读取1位布尔值。</summary>
        public bool ReadBool()
        {
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
                if (((_buffer[_bitPosition >> 3] >> (_bitPosition & 7)) & 1) != 0)
                    value |= (uint)(1 << i);
                _bitPosition++;
            }
            return value;
        }

        /// <summary>读取有符号整数。</summary>
        public int ReadInt(int bits)
        {
            return unchecked((int)ReadUInt(bits));
        }

        /// <summary>读取枚举。</summary>
        public T ReadEnum<T>(int maxValues) where T : Enum
        {
            int bitsNeeded = maxValues <= 1 ? 1 : (int)Math.Ceiling(Math.Log2(maxValues));
            uint index = ReadUInt(bitsNeeded);
            return (T)Enum.ToObject(typeof(T), index);
        }

        /// <summary>读取量化浮点。</summary>
        public float ReadQuantizedFloat(float min, float max, int bits)
        {
            if (bits <= 0) return ReadFloat();
            uint quantized = ReadUInt(bits);
            float normalized = (float)quantized / ((1u << bits) - 1);
            return min + normalized * (max - min);
        }

        /// <summary>读取32位浮点。</summary>
        public float ReadFloat()
        {
            uint bits = ReadUInt(32);
            return BitConverter.Int32BitsToSingle(unchecked((int)bits));
        }

        /// <summary>跳到下一个字节边界。</summary>
        public void PadToByte()
        {
            int remainder = _bitPosition & 7;
            if (remainder > 0)
                _bitPosition += 8 - remainder;
        }

        /// <summary>读取原始字节。</summary>
        public byte[] ReadBytes(int count)
        {
            PadToByte();
            byte[] result = new byte[count];
            int bytePos = _bitPosition >> 3;
            if (bytePos + count <= _buffer.Length)
                Array.Copy(_buffer, bytePos, result, 0, count);
            _bitPosition += count * 8;
            return result;
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 3. 变长整数编码 (VarInt)
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 变长整数编码器/解码器。小值使用更少的字节, 节省带宽。
    /// 编码规则 (类似 Protocol Buffers VarInt):
    ///   0~127: 1 字节
    ///   128~16383: 2 字节
    ///   以此类推, 最多 10 字节 (64位)。
    /// </summary>
    public static class VarInt
    {
        /// <summary>编码无符号整数到写入器。</summary>
        public static void Write(NetworkWriter writer, uint value)
        {
            do
            {
                byte b = (byte)(value & 0x7F);
                value >>= 7;
                if (value != 0) b |= 0x80;
                writer.WriteByte(b);
            }
            while (value != 0);
        }

        /// <summary>从读取器解码无符号整数。</summary>
        public static uint ReadUInt(NetworkReader reader)
        {
            uint result = 0;
            int shift = 0;
            while (true)
            {
                byte b = reader.ReadByte();
                result |= (uint)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) break;
                shift += 7;
                if (shift >= 35) break; // 防止溢出
            }
            return result;
        }

        /// <summary>编码有符号整数 (ZigZag 编码)。</summary>
        public static void WriteSigned(NetworkWriter writer, int value)
        {
            Write(writer, (uint)((value << 1) ^ (value >> 31)));
        }

        /// <summary>解码有符号整数 (ZigZag 解码)。</summary>
        public static int ReadInt(NetworkReader reader)
        {
            uint encoded = ReadUInt(reader);
            return (int)((encoded >> 1) ^ (-(int)(encoded & 1)));
        }

        /// <summary>编码64位变长整数。</summary>
        public static void WriteLong(NetworkWriter writer, ulong value)
        {
            do
            {
                byte b = (byte)(value & 0x7F);
                value >>= 7;
                if (value != 0) b |= 0x80;
                writer.WriteByte(b);
            }
            while (value != 0);
        }

        /// <summary>解码64位变长整数。</summary>
        public static ulong ReadULong(NetworkReader reader)
        {
            ulong result = 0;
            int shift = 0;
            while (true)
            {
                byte b = reader.ReadByte();
                result |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) break;
                shift += 7;
                if (shift >= 70) break;
            }
            return result;
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 4. 浮点量化工具
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 浮点量化工具集。提供各种精度级别的浮点压缩。
    /// </summary>
    public static class FloatQuantizer
    {
        /// <summary>量化 float 到 8 位 (低精度, 适合不重要的值)。</summary>
        public static byte Quantize8(float value, float min, float max)
        {
            float normalized = (value - min) / (max - min);
            return (byte)Math.Clamp((int)(normalized * 255), 0, 255);
        }

        /// <summary>反量化 8 位到 float。</summary>
        public static float Dequantize8(byte quantized, float min, float max)
        {
            return min + (quantized / 255f) * (max - min);
        }

        /// <summary>量化 float 到 16 位 (中等精度, 适合位置/旋转)。</summary>
        public static ushort Quantize16(float value, float min, float max)
        {
            float normalized = (value - min) / (max - min);
            return (ushort)Math.Clamp((int)(normalized * 65535), 0, 65535);
        }

        /// <summary>反量化 16 位到 float。</summary>
        public static float Dequantize16(ushort quantized, float min, float max)
        {
            return min + (quantized / 65535f) * (max - min);
        }

        /// <summary>量化 Vector3 到 16 位/分量 (假设范围 -500~500)。</summary>
        public static void QuantizeVector3_16(Vector3 v, float range,
            out ushort x, out ushort y, out ushort z)
        {
            x = Quantize16(v.X, -range, range);
            y = Quantize16(v.Y, -range, range);
            z = Quantize16(v.Z, -range, range);
        }

        /// <summary>反量化 Vector3 16位。</summary>
        public static Vector3 DequantizeVector3_16(ushort x, ushort y, ushort z, float range)
        {
            return new Vector3(
                Dequantize16(x, -range, range),
                Dequantize16(y, -range, range),
                Dequantize16(z, -range, range));
        }

        /// <summary>
        /// 量化 Quaternion 到最小旋转表示 (Smallest Three)。
        /// 只发送 3 个分量 + 1 个符号位, 共 49 位 (vs 128 位)。
        /// </summary>
        public static void QuantizeQuaternion_Smallest3(Quaternion q,
            out byte largestAxis, out float a, out float b, out float c, out bool isNegative)
        {
            float absX = Math.Abs(q.X);
            float absY = Math.Abs(q.Y);
            float absZ = Math.Abs(q.Z);
            float absW = Math.Abs(q.W);

            // 找最大分量
            float maxAbs = absX;
            largestAxis = 0;
            if (absY > maxAbs) { maxAbs = absY; largestAxis = 1; }
            if (absZ > maxAbs) { maxAbs = absZ; largestAxis = 2; }
            if (absW > maxAbs) { maxAbs = absW; largestAxis = 3; }

            // 确保最大分量为正
            isNegative = GetAxisValue(q, largestAxis) < 0;

            // 提取其他三个分量
            switch (largestAxis)
            {
                case 0: a = q.Y; b = q.Z; c = q.W; break;
                case 1: a = q.X; b = q.Z; c = q.W; break;
                case 2: a = q.X; b = q.Y; c = q.W; break;
                default: a = q.X; b = q.Y; c = q.Z; break;
            }

            if (isNegative) { a = -a; b = -b; c = -c; }
        }

        /// <summary>从最小旋转表示重建 Quaternion。</summary>
        public static Quaternion DequantizeQuaternion_Smallest3(
            byte largestAxis, float a, float b, float c, bool isNegative)
        {
            if (isNegative) { a = -a; b = -b; c = -c; }

            float d = (float)Math.Sqrt(Math.Max(0, 1.0f - a * a - b * b - c * c));

            return largestAxis switch
            {
                0 => new Quaternion(d, a, b, c),
                1 => new Quaternion(a, d, b, c),
                2 => new Quaternion(a, b, d, c),
                _ => new Quaternion(a, b, c, d)
            };
        }

        private static float GetAxisValue(Quaternion q, int axis) => axis switch
        {
            0 => q.X, 1 => q.Y, 2 => q.Z, _ => q.W
        };
    }

    // ──────────────────────────────────────────────────────────────
    // 5. CRC32 校验
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// CRC32 校验。用于网络消息完整性验证。
    /// </summary>
    public static class NetworkCRC32
    {
        // CRC32 查找表 (IEEE 802.3 多项式: 0xEDB88320)
        private static readonly uint[] _table = GenerateTable();

        private static uint[] GenerateTable()
        {
            var table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint crc = i;
                for (int j = 0; j < 8; j++)
                {
                    crc = (crc & 1) != 0
                        ? (crc >> 1) ^ 0xEDB88320u
                        : crc >> 1;
                }
                table[i] = crc;
            }
            return table;
        }

        /// <summary>计算字节序列的 CRC32 值。</summary>
        public static uint Compute(byte[] data, int offset = 0, int count = -1)
        {
            if (count < 0) count = data.Length - offset;
            uint crc = 0xFFFFFFFF;
            for (int i = offset; i < offset + count; i++)
            {
                crc = (crc >> 8) ^ _table[(crc ^ data[i]) & 0xFF];
            }
            return ~crc;
        }

        /// <summary>将 CRC32 写入写入器 (4字节, 小端)。</summary>
        public static void WriteToWriter(NetworkWriter writer, byte[] data, int offset, int count)
        {
            uint crc = Compute(data, offset, count);
            writer.WriteUInt(crc);
        }

        /// <summary>从读取器读取 CRC32 并验证。</summary>
        public static bool Verify(NetworkReader reader, byte[] data, int offset, int count)
        {
            uint expected = reader.ReadUInt();
            uint actual = Compute(data, offset, count);
            return expected == actual;
        }

        /// <summary>计算追加 CRC32。</summary>
        public static uint Append(uint currentCRC, byte value)
        {
            return (currentCRC >> 8) ^ _table[(currentCRC ^ value) & 0xFF];
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 6. 消息分片系统
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 消息分片器。将大消息自动拆分为多个小片段发送,
    /// 接收端按消息 ID 重组。支持超时重传。
    /// </summary>
    public sealed class MessageFragmenter
    {
        /// <summary>最大分片大小 (字节)。</summary>
        public int MaxFragmentSize { get; set; } = 1200;

        /// <summary>分片超时 (毫秒)。</summary>
        public int FragmentTimeoutMs { get; set; } = 5000;

        /// <summary>最大同时重组的消息数。</summary>
        public int MaxPendingMessages { get; set; } = 32;

        // ── 分片头格式 (8字节):
        //   byte 0: 消息类型 (0=DATA, 1=ACK)
        //   byte 1: 标志 (bit0=最后一片)
        //   ushort 2-3: 消息 ID
        //   ushort 4-5: 分片索引
        //   ushort 6-7: 总分片数

        private const byte FragmentTypeData = 0;
        private const byte FragmentTypeAck = 1;
        private const byte FlagLastFragment = 0x01;

        private ushort _nextMessageId;
        private readonly Dictionary<ushort, PendingReassembly> _pendingReassembly
            = new Dictionary<ushort, PendingReassembly>();

        /// <summary>
        /// 将消息分片。
        /// </summary>
        public List<byte[]> Fragment(byte[] message, int length)
        {
            var fragments = new List<byte[]>();
            if (length <= MaxFragmentSize - 8)
            {
                // 不需要分片
                var packet = new byte[length + 8];
                packet[0] = FragmentTypeData;
                packet[1] = FlagLastFragment; // 最后一片
                WriteUShort(packet, 2, ++_nextMessageId);
                WriteUShort(packet, 4, 0); // 索引 0
                WriteUShort(packet, 6, 1); // 总共 1 片
                Array.Copy(message, 0, packet, 8, length);
                fragments.Add(packet);
                return fragments;
            }

            int totalFragments = (int)Math.Ceiling((double)length / (MaxFragmentSize - 8));
            ushort msgId = ++_nextMessageId;
            int offset = 0;

            for (int i = 0; i < totalFragments; i++)
            {
                int chunkSize = Math.Min(MaxFragmentSize - 8, length - offset);
                var packet = new byte[chunkSize + 8];

                packet[0] = FragmentTypeData;
                packet[1] = (i == totalFragments - 1) ? FlagLastFragment : (byte)0;
                WriteUShort(packet, 2, msgId);
                WriteUShort(packet, 4, (ushort)i);
                WriteUShort(packet, 6, (ushort)totalFragments);
                Array.Copy(message, offset, packet, 8, chunkSize);

                fragments.Add(packet);
                offset += chunkSize;
            }

            return fragments;
        }

        /// <summary>
        /// 处理接收到的分片。返回 null 表示消息不完整,
        /// 返回完整消息表示重组完成。
        /// </summary>
        public byte[]? ProcessFragment(byte[] packet, int length)
        {
            if (length < 8) return null;

            byte fragType = packet[0];
            byte flags = packet[1];
            ushort msgId = ReadUShort(packet, 2);
            ushort fragIndex = ReadUShort(packet, 4);
            ushort totalFrags = ReadUShort(packet, 6);

            int payloadLen = length - 8;

            if (!_pendingReassembly.TryGetValue(msgId, out var pending))
            {
                if (_pendingReassembly.Count >= MaxPendingMessages)
                {
                    // 清理最旧的消息
                    var oldest = _pendingReassembly.Keys.First();
                    _pendingReassembly.Remove(oldest);
                }

                pending = new PendingReassembly
                {
                    MessageId = msgId,
                    TotalFragments = totalFrags,
                    ReceivedFragments = new byte[totalFragments][],
                    ReceivedBitmap = new bool[totalFragments],
                    CreateTime = DateTime.UtcNow
                };
                _pendingReassembly[msgId] = pending;
            }

            // 存储分片数据
            var fragData = new byte[payloadLen];
            Array.Copy(packet, 8, fragData, 0, payloadLen);
            pending.ReceivedFragments[fragIndex] = fragData;
            pending.ReceivedBitmap[fragIndex] = true;

            // 检查是否所有分片都已收到
            bool allReceived = true;
            for (int i = 0; i < totalFragments; i++)
            {
                if (!pending.ReceivedBitmap[i]) { allReceived = false; break; }
            }

            if (allReceived)
            {
                // 重组消息
                int totalSize = 0;
                for (int i = 0; i < totalFragments; i++)
                    totalSize += pending.ReceivedFragments[i].Length;

                var result = new byte[totalSize];
                int offset = 0;
                for (int i = 0; i < totalFragments; i++)
                {
                    int fragLen = pending.ReceivedFragments[i].Length;
                    Array.Copy(pending.ReceivedFragments[i], 0, result, offset, fragLen);
                    offset += fragLen;
                }

                _pendingReassembly.Remove(msgId);
                return result;
            }

            return null;
        }

        /// <summary>清理超时的分片消息。</summary>
        public void CleanupTimeouts()
        {
            var now = DateTime.UtcNow;
            var timedOut = _pendingReassembly
                .Where(kvp => (now - kvp.Value.CreateTime).TotalMilliseconds > FragmentTimeoutMs)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var id in timedOut)
                _pendingReassembly.Remove(id);
        }

        /// <summary>获取待重组消息数。</summary>
        public int PendingCount => _pendingReassembly.Count;

        private class PendingReassembly
        {
            public ushort MessageId;
            public int TotalFragments;
            public byte[][] ReceivedFragments = null!;
            public bool[] ReceivedBitmap = null!;
            public DateTime CreateTime;
        }

        private static void WriteUShort(byte[] buf, int offset, ushort value)
        {
            buf[offset] = (byte)(value & 0xFF);
            buf[offset + 1] = (byte)((value >> 8) & 0xFF);
        }

        private static ushort ReadUShort(byte[] buf, int offset)
        {
            return (ushort)(buf[offset] | (buf[offset + 1] << 8));
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 7. 网络字节序处理
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 网络字节序工具。网络传输使用大端序 (Big-Endian),
    /// 本机可能使用小端序 (Little-Endian)。
    /// </summary>
    public static class NetworkByteOrder
    {
        /// <summary>当前系统是否为小端序。</summary>
        public static readonly bool IsLittleEndian = BitConverter.IsLittleEndian;

        /// <summary>将主机字节序转换为网络字节序 (大端)。</summary>
        public static short HostToNetwork(short value)
        {
            if (IsLittleEndian) return (short)SwapBytes((ushort)value);
            return value;
        }

        /// <summary>将网络字节序转换为主机字节序。</summary>
        public static short NetworkToHost(short value)
        {
            if (IsLittleEndian) return (short)SwapBytes((ushort)value);
            return value;
        }

        /// <summary>将主机字节序转换为网络字节序 (大端)。</summary>
        public static ushort HostToNetwork(ushort value)
        {
            if (IsLittleEndian) return SwapBytes(value);
            return value;
        }

        /// <summary>将网络字节序转换为主机字节序。</summary>
        public static ushort NetworkToHost(ushort value)
        {
            if (IsLittleEndian) return SwapBytes(value);
            return value;
        }

        /// <summary>32位主机序转网络序。</summary>
        public static int HostToNetwork(int value)
        {
            if (IsLittleEndian) return (int)SwapBytes((uint)value);
            return value;
        }

        /// <summary>32位网络序转主机序。</summary>
        public static int NetworkToHost(int value)
        {
            if (IsLittleEndian) return (int)SwapBytes((uint)value);
            return value;
        }

        /// <summary>64位主机序转网络序。</summary>
        public static long HostToNetwork(long value)
        {
            if (IsLittleEndian) return (long)SwapBytes((ulong)value);
            return value;
        }

        /// <summary>64位网络序转主机序。</summary>
        public static long NetworkToHost(long value)
        {
            if (IsLittleEndian) return (long)SwapBytes((ulong)value);
            return value;
        }

        /// <summary>交换字节序。</summary>
        private static ushort SwapBytes(ushort value)
        {
            return (ushort)((value & 0xFF) << 8 | (value >> 8) & 0xFF);
        }

        /// <summary>交换字节序。</summary>
        private static uint SwapBytes(uint value)
        {
            return ((value & 0x000000FF) << 24) |
                   ((value & 0x0000FF00) << 8) |
                   ((value & 0x00FF0000) >> 8) |
                   ((value & 0xFF000000) >> 24);
        }
    }
}
