using System;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using System.Text;
using ShanyuEngine.Core;

namespace ShanyuEngine.Networking
{
    // ════════════════════════════════════════════════════════════════════════
    // 山雨引擎 - 网络复制系统 (NetworkReplication)
    //
    // 本文件在已有的 NetworkSystem 之上构建完整的网络复制管线, 涵盖:
    //   * 服务器权威模型: 服务器为权威, 客户端发送输入, 服务器校正
    //   * 增量同步: 属性脏标记 + 哈希对比, 仅同步变化的属性
    //   * 优先级与相关性: 按距离/重要性排序的同步优先级
    //   * 属性压缩: 量化压缩 (位置/旋转/速度的位压缩)
    //   * 网络快照: 环形缓冲区存储历史状态用于回滚
    //   * RPC 系统: Server RPC / Client RPC / Multicast RPC
    //   * 网络时钟: RTT 估算、服务器时间同步、插值延迟自适应
    //   * Delta 压缩: 位掩码标识变化属性, 紧凑序列化
    //   * 连接管理与心跳: 超时检测, 滑动窗口 ACK
    //   * 房间/大厅系统: 分房匹配, 状态广播
    //   * 传输层抽象: 可对接 ENet / LiteNetLib
    //
    // 对接系统: Networking/NetworkSystem / Core/EngineTime / Core/NetworkConfig
    // ════════════════════════════════════════════════════════════════════════

    // ──────────────────────────────────────────────────────────────
    // 1. 复制特性与枚举 (Attributes & Enums)
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 标记一个字段/属性需要被网络复制系统自动同步。
    /// 仅在服务器权威端写入, 客户端接收后回写。
    /// </summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false)]

    /// <summary>标记一个方法为 RPC, 指定其调用方向与可靠性。</summary>
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class NetRPCAttribute : Attribute
    {
        public RPCType Type;
        public bool Reliable = true;
    }

    /// <summary>RPC 调用方向。</summary>
    public enum RPCType
    {
        /// <summary>客户端调用, 在服务器执行。</summary>
        Server,
        /// <summary>服务器调用, 仅在拥有者客户端执行。</summary>
        Client,
        /// <summary>服务器调用, 在所有客户端执行。</summary>
        Multicast
    }

    /// <summary>传输可靠性等级, 用于底层通道选择。</summary>
    public enum DeliveryMode
    {
        /// <summary>不可靠无序: 最低延迟, 可丢包 (如位置高频更新)。</summary>
        UnreliableUnordered,
        /// <summary>不可靠有序: 丢弃过期包。</summary>
        UnreliableSequenced,
        /// <summary>可靠无序: 保证到达, 不保证顺序。</summary>
        ReliableUnordered,
        /// <summary>可靠有序: 保证到达且顺序 (默认 RPC)。</summary>
        ReliableOrdered
    }

    // ──────────────────────────────────────────────────────────────
    // 2. 位打包序列化器 (BitWriter / BitReader)
    //    将数据以位为单位紧凑写入, 支持量化浮点与变长整数,
    //    显著降低网络带宽占用。
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 位级写入器: 将布尔、整数、量化浮点等以紧凑位流写入字节缓冲。
    /// </summary>
    public sealed class BitWriter
    {
        private byte[] _buffer;
        private int _bitPos;

        public BitWriter(int capacityBytes = 1024)
        {
            _buffer = new byte[capacityBytes];
            _bitPos = 0;
        }

        /// <summary>当前已写入的位数。</summary>
        public int BitLength => _bitPos;
        /// <summary>当前已写入的字节数 (向上取整)。</summary>
        public int ByteLength => (_bitPos + 7) >> 3;

        /// <summary>写入单个布尔位。</summary>
        public void WriteBit(bool value)
        {
            EnsureCapacity(_bitPos + 1);
            int byteIdx = _bitPos >> 3;
            int bitIdx = _bitPos & 7;
            if (value)
                _buffer[byteIdx] |= (byte)(1 << bitIdx);
            else
                _buffer[byteIdx] &= (byte)~(1 << bitIdx);
            _bitPos++;
        }

        /// <summary>写入指定位数的整数 (高位在前)。</summary>
        public void WriteBits(uint value, int bitCount)
        {
            if (bitCount <= 0) return;
            EnsureCapacity(_bitPos + bitCount);
            for (int i = bitCount - 1; i >= 0; i--)
                WriteBit(((value >> i) & 1) != 0);
        }

        /// <summary>写入 32 位整数。</summary>
        public void WriteInt(int value) => WriteBits((uint)value, 32);

        /// <summary>写入 32 位无符号整数。</summary>
        public void WriteUInt(uint value) => WriteBits(value, 32);

        /// <summary>写入 16 位整数。</summary>
        public void WriteShort(short value) => WriteBits((uint)(value & 0xFFFF), 16);

        /// <summary>写入单精度浮点 (量化到指定位数)。</summary>
        public void WriteFloatQuantized(float value, float min, float max, int bitCount = 16)
        {
            float clamped = Math.Clamp(value, min, max);
            float range = max - min;
            if (range <= 0f) { WriteBits(0, bitCount); return; }
            float normalized = (clamped - min) / range;
            uint quantized = (uint)(normalized * ((1u << bitCount) - 1));
            WriteBits(quantized, bitCount);
        }

        /// <summary>写入三维向量 (各分量分别量化)。</summary>
        public void WriteVector3(Vector3 v, float min, float max, int bits = 16)
        {
            WriteFloatQuantized(v.X, min, max, bits);
            WriteFloatQuantized(v.Y, min, max, bits);
            WriteFloatQuantized(v.Z, min, max, bits);
        }

        /// <summary>写入四元数 (最小三分量编码, 节省带宽)。</summary>
        public void WriteQuaternion(Quaternion q)
        {
            // 找到绝对值最大的分量, 仅传输其余三个
            float ax = Math.Abs(q.X), ay = Math.Abs(q.Y), az = Math.Abs(q.Z), aw = Math.Abs(q.W);
            int largest = 0;
            float max = ax;
            if (ay > max) { max = ay; largest = 1; }
            if (az > max) { max = az; largest = 2; }
            if (aw > max) { max = aw; largest = 3; }
            WriteBits((uint)largest, 2);
            float a = largest == 0 ? q.Y : q.X;
            float b = largest == 2 ? q.Y : (largest == 3 ? q.Y : q.Z);
            float c = largest == 3 ? q.Z : q.W;
            WriteFloatQuantized(a, -0.7071f, 0.7071f, 9);
            WriteFloatQuantized(b, -0.7071f, 0.7071f, 9);
            WriteFloatQuantized(c, -0.7071f, 0.7071f, 9);
        }

        /// <summary>写入字节序列 (按字节对齐)。</summary>
        public void WriteBytes(byte[] data, int length)
        {
            AlignToByte();
            EnsureCapacity(_bitPos + length * 8);
            Buffer.BlockCopy(data, 0, _buffer, _bitPos >> 3, length);
            _bitPos += length * 8;
        }

        /// <summary>写入 UTF8 字符串 (长度前缀 + 字节)。</summary>
        public void WriteString(string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            WriteBits((uint)bytes.Length, 16);
            WriteBytes(bytes, bytes.Length);
        }

        /// <summary>将位位置对齐到字节边界。</summary>
        public void AlignToByte()
        {
            _bitPos = (_bitPos + 7) & ~7;
        }

        /// <summary>获取写入结果的字节副本。</summary>
        public byte[] ToArray()
        {
            byte[] result = new byte[ByteLength];
            Buffer.BlockCopy(_buffer, 0, result, 0, ByteLength);
            return result;
        }

        private void EnsureCapacity(int bitsNeeded)
        {
            int bytesNeeded = (bitsNeeded + 7) >> 3;
            if (bytesNeeded <= _buffer.Length) return;
            int newSize = Math.Max(_buffer.Length * 2, bytesNeeded);
            Array.Resize(ref _buffer, newSize);
        }
    }

    /// <summary>
    /// 位级读取器: 与 BitWriter 配对, 从紧凑位流中读出数据。
    /// </summary>
    public sealed class BitReader
    {
        private readonly byte[] _buffer;
        private int _bitPos;

        public BitReader(byte[] buffer, int length)
        {
            _buffer = buffer;
            _bitPos = 0;
        }

        /// <summary>剩余可读位数。</summary>
        public int BitsRemaining => (_buffer.Length << 3) - _bitPos;

        /// <summary>读取单个布尔位。</summary>
        public bool ReadBit()
        {
            int byteIdx = _bitPos >> 3;
            int bitIdx = _bitPos & 7;
            bool value = (_buffer[byteIdx] & (1 << bitIdx)) != 0;
            _bitPos++;
            return value;
        }

        /// <summary>读取指定位数的无符号整数。</summary>
        public uint ReadBits(int bitCount)
        {
            uint value = 0;
            for (int i = bitCount - 1; i >= 0; i--)
                if (ReadBit()) value |= (1u << i);
            return value;
        }

        /// <summary>读取 32 位整数。</summary>
        public int ReadInt() => (int)ReadBits(32);

        /// <summary>读取 32 位无符号整数。</summary>
        public uint ReadUInt() => ReadBits(32);

        /// <summary>读取 16 位整数。</summary>
        public short ReadShort() => (short)ReadBits(16);

        /// <summary>读取量化浮点 (反量化到原始范围)。</summary>
        public float ReadFloatQuantized(float min, float max, int bitCount = 16)
        {
            uint quantized = ReadBits(bitCount);
            float range = max - min;
            if (range <= 0f) return min;
            float normalized = quantized / (float)((1u << bitCount) - 1);
            return min + normalized * range;
        }

        /// <summary>读取三维向量。</summary>
        public Vector3 ReadVector3(float min, float max, int bits = 16)
        {
            return new Vector3(
                ReadFloatQuantized(min, max, bits),
                ReadFloatQuantized(min, max, bits),
                ReadFloatQuantized(min, max, bits));
        }

        /// <summary>读取四元数 (最小三分量解码)。</summary>
        public Quaternion ReadQuaternion()
        {
            int largest = (int)ReadBits(2);
            float a = ReadFloatQuantized(-0.7071f, 0.7071f, 9);
            float b = ReadFloatQuantized(-0.7071f, 0.7071f, 9);
            float c = ReadFloatQuantized(-0.7071f, 0.7071f, 9);
            float d2 = 1f - a * a - b * b - c * c;
            float d = d2 > 0 ? MathF.Sqrt(d2) : 0;
            return largest switch
            {
                0 => new Quaternion(d, a, b, c),
                1 => new Quaternion(a, d, b, c),
                2 => new Quaternion(a, b, d, c),
                _ => new Quaternion(a, b, c, d),
            };
        }

        /// <summary>读取字节序列。</summary>
        public byte[] ReadBytes(int length)
        {
            AlignToByte();
            byte[] result = new byte[length];
            Buffer.BlockCopy(_buffer, _bitPos >> 3, result, 0, length);
            _bitPos += length * 8;
            return result;
        }

        /// <summary>读取 UTF8 字符串。</summary>
        public string ReadString()
        {
            int length = (int)ReadBits(16);
            byte[] bytes = ReadBytes(length);
            return Encoding.UTF8.GetString(bytes);
        }

        /// <summary>对齐到字节边界。</summary>
        public void AlignToByte() => _bitPos = (_bitPos + 7) & ~7;
    }

    // ──────────────────────────────────────────────────────────────
    // 3. 复制属性元数据 (ReplicatedProperty / ReplicatedObject)
    //    通过反射收集 [Replicated] 字段, 缓存其读写委托与上次值,
    //    用于 Delta 压缩与哈希对比判断。
    // ──────────────────────────────────────────────────────────────

    /// <summary>单个被复制属性的运行时元数据。</summary>
    public sealed class ReplicatedProperty
    {
        /// <summary>属性名。</summary>
        public string Name = string.Empty;
        /// <summary>反射成员信息 (Field 或 Property)。</summary>
        public MemberInfo? Member;
        /// <summary>值类型。</summary>
        public Type? ValueType;
        /// <summary>是否可靠传输。</summary>
        public bool Reliable;
        /// <summary>复制优先级 (0~1)。</summary>
        public float Priority;
        /// <summary>每秒最大同步次数。</summary>
        public float MaxUpdateRate;
        /// <summary>条件组名。</summary>
        public string Condition = string.Empty;
        /// <summary>量化位宽 (0=不压缩)。</summary>
        public int QuantizationBits;
        /// <summary>上次发送的值。</summary>
        public object? LastSentValue;
        /// <summary>上次发送后的经过时间。</summary>
        public float TimeSinceLastSent;
        /// <summary>发送计数。</summary>
        public int SendCounter;
        /// <summary>属性是否在上次同步后发生变化 (Delta 判定)。</summary>
        public bool IsDirty;
        /// <summary>上次发送时的属性值哈希 (用于哈希对比检测变化)。</summary>
        public ulong LastSentHash;
    }

    /// <summary>复制对象的运行时状态, 聚合所有复制属性。</summary>
    public sealed class ReplicatedObject
    {
        /// <summary>网络 ID。</summary>
        public int NetworkId;
        /// <summary>目标对象实例。</summary>
        public object? Target;
        /// <summary>对象类型。</summary>
        public Type? ObjectType;
        /// <summary>复制属性列表。</summary>
        public List<ReplicatedProperty> Properties = new();
        /// <summary>脏标记位掩码。</summary>
        public ReplicationFlags DirtyFlags;
        /// <summary>更新优先级 (0~1)。</summary>
        public float UpdatePriority;
        /// <summary>上次相关性分数。</summary>
        public float LastRelevance = 1f;
        /// <summary>上次更新时间。</summary>
        public DateTime LastUpdateTime;
        /// <summary>距上次更新的时间。</summary>
        public float TimeSinceLastUpdate;
    }

    // ──────────────────────────────────────────────────────────────
    // 4. 属性哈希计算器 (PropertyHasher)
    //    为每个属性值计算快速哈希, 用于检测属性是否发生变化。
    //    配合脏标记实现双重检测: 手动标记 + 哈希对比。
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 属性哈希计算器: 为各种类型的属性值计算 64 位哈希。
    /// 用于 Delta 压缩时检测属性是否真正发生变化, 避免不必要的同步。
    /// </summary>
    public static class PropertyHasher
    {
        /// <summary>计算属性值的哈希。</summary>
        public static ulong ComputeHash(object? value)
        {
            if (value == null) return 0;

            return value switch
            {
                bool b => b ? 1UL : 0UL,
                int i => (ulong)i,
                uint u => u,
                long l => (ulong)l,
                ulong ul => ul,
                short s => (ushort)s,
                ushort us => us,
                byte b8 => b8,
                sbyte sb => (byte)sb,
                float f => BitConverter.SingleToUInt32Bits(f),
                double d => BitConverter.DoubleToUInt64Bits(d),
                Vector3 v => HashCombine(
                    BitConverter.SingleToUInt32Bits(v.X),
                    BitConverter.SingleToUInt32Bits(v.Y),
                    BitConverter.SingleToUInt32Bits(v.Z)),
                Quaternion q => HashCombine(
                    BitConverter.SingleToUInt32Bits(q.X),
                    BitConverter.SingleToUInt32Bits(q.Y),
                    BitConverter.SingleToUInt32Bits(q.Z),
                    BitConverter.SingleToUInt32Bits(q.W)),
                string s => (ulong)s.GetHashCode(),
                _ => (ulong)value.GetHashCode()
            };
        }

        private static ulong HashCombine(uint a, uint b)
        {
            return ((ulong)a << 32) | b;
        }

        private static ulong HashCombine(uint a, uint b, uint c)
        {
            return HashCombine(a, b) ^ (c * 0x9E3779B9u);
        }

        private static ulong HashCombine(uint a, uint b, uint c, uint d)
        {
            return HashCombine(a, b, c) ^ ((ulong)d * 0x85EBCA6Bu);
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 5. RPC 注册与调度 (RPCRegistry)
    // ──────────────────────────────────────────────────────────────

    /// <summary>RPC 调用委托。</summary>
    public delegate void RPCCallback(ReplicatedObject? obj, BitReader args, Guid senderConnection);

    /// <summary>
    /// RPC 注册表: 维护方法名到委托的映射, 并按类型分组,
    /// 在收到 RPC 包时分发到对应处理器。
    /// </summary>
    public sealed class RPCRegistry
    {
        private readonly Dictionary<string, RPCCallback> _handlers = new();
        private readonly Dictionary<string, NetRPCAttribute> _meta = new();

        /// <summary>注册一个 RPC 处理器。</summary>
        public void Register(string name, RPCCallback callback, bool reliable = true, RPCType type = RPCType.Server)
        {
            _handlers[name] = callback;
            _meta[name] = new NetRPCAttribute { Type = type, Reliable = reliable };
        }

        /// <summary>通过反射自动注册某类型上所有 [NetRPC] 标记的方法。</summary>
        public void RegisterType(Type type)
        {
            foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                var attr = method.GetCustomAttribute<NetRPCAttribute>();
                if (attr == null) continue;
                _handlers[method.Name] = (obj, reader, sender) =>
                {
                    method.Invoke(obj?.Target, new object[] { reader, sender });
                };
                _meta[method.Name] = attr;
            }
        }

        /// <summary>尝试获取 RPC 处理器。</summary>
        public bool TryGetHandler(string name, out RPCCallback handler) => _handlers.TryGetValue(name, out handler!);

        /// <summary>尝试获取 RPC 元数据。</summary>
        public bool TryGetMeta(string name, out NetRPCAttribute attr) => _meta.TryGetValue(name, out attr!);

        /// <summary>已注册的 RPC 名称。</summary>
        public IEnumerable<string> RegisteredNames => _handlers.Keys;

        /// <summary>已注册的 RPC 数量。</summary>
        public int Count => _handlers.Count;

        /// <summary>清空所有已注册的 RPC 处理器与元数据。</summary>
        public void Clear()
        {
            _handlers.Clear();
            _meta.Clear();
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 6. 优先级与相关性 (Priority & Relevance)
    // ──────────────────────────────────────────────────────────────

    /// <summary>相关性计算参数。</summary>
    public struct RelevanceQuery
    {
        /// <summary>观察者位置。</summary>
        public Vector3 ViewerPosition;
        /// <summary>最大相关距离 (超出则不同步)。</summary>
        public float MaxRelevanceDistance = 2000f;
        /// <summary>是否启用视锥剔除。</summary>
        public bool FrustumCulling;
        /// <summary>视锥最小角。</summary>
        public Vector3 FrustumMin;
        /// <summary>视锥最大角。</summary>
        public Vector3 FrustumMax;

        public RelevanceQuery() { }
    }

    /// <summary>
    /// 相关性与优先级计算器: 根据距离、视野、变化频率估算
    /// 每个复制对象的同步优先级 (0~1)。
    /// </summary>
    public static class RelevanceCalculator
    {
        /// <summary>计算单个对象相对于观察者的相关性 (0~1)。</summary>
        public static float Compute(ReplicatedObject obj, RelevanceQuery query)
        {
            if (obj.Target is NetworkEntity ne)
            {
                float dist = Vector3.Distance(ne.Position, query.ViewerPosition);
                float distFactor = 1f - Math.Clamp(dist / query.MaxRelevanceDistance, 0f, 1f);
                float relevance = distFactor;

                // 视锥剔除: 视野外相关性折半
                if (query.FrustumCulling)
                {
                    bool inView = ne.Position.X >= query.FrustumMin.X && ne.Position.X <= query.FrustumMax.X &&
                                  ne.Position.Y >= query.FrustumMin.Y && ne.Position.Y <= query.FrustumMax.Y &&
                                  ne.Position.Z >= query.FrustumMin.Z && ne.Position.Z <= query.FrustumMax.Z;
                    if (!inView) relevance *= 0.5f;
                }

                // 近期变化的对象优先级更高
                if (ne.DirtyFlags != ReplicationFlags.None)
                    relevance = Math.Min(1f, relevance + 0.2f);

                // 玩家控制的实体始终最高优先级
                if (ne.IsPlayerControlled)
                    relevance = Math.Min(1f, relevance + 0.3f);

                // 低生命值的实体优先级提升 (战斗中的单位更重要)
                if (ne.Health < 50f)
                    relevance = Math.Min(1f, relevance + 0.1f);

                obj.LastRelevance = relevance;
                return relevance;
            }
            return 1f;
        }

        /// <summary>根据相关性与时间预算, 对对象排序并挑选本帧需同步的集合。</summary>
        public static List<ReplicatedObject> SelectForReplication(
            List<ReplicatedObject> all, RelevanceQuery query, int maxCount)
        {
            all.Sort((a, b) => b.UpdatePriority.CompareTo(a.UpdatePriority));
            var result = new List<ReplicatedObject>(maxCount);
            foreach (var obj in all)
            {
                if (result.Count >= maxCount) break;
                if (obj.UpdatePriority <= 0.01f) continue;
                result.Add(obj);
            }
            return result;
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 7. 网络时钟 (NetworkClock)
    //    RTT 估算、服务器时间同步、插值延迟自适应。
    //    客户端通过时间同步包估算服务器时间偏移,
    //    并根据 RTT 与抖动动态调整插值延迟。
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 网络时钟: 管理 RTT 估算、服务器时间同步与插值延迟。
    /// 客户端通过周期性发送时间同步请求, 估算本地与服务器的时间偏移。
    /// 插值延迟根据 RTT 和抖动自适应调整, 在平滑度与延迟间取得平衡。
    /// </summary>
    public sealed class NetworkClock
    {
        private float _localTime;
        private float _serverTimeOffset;
        private float _smoothedRtt;
        private float _rttVariation;
        private float _interpolationDelay = 0.1f;
        private float _minInterpDelay = 0.03f;
        private float _maxInterpDelay = 0.3f;
        private float _syncTimer;
        private float _syncInterval = 1.0f;
        private int _syncCount;
        private uint _nextSyncId;
        private readonly Dictionary<uint, float> _pendingSyncs = new();

        /// <summary>本地时间 (秒)。</summary>
        public float LocalTime => _localTime;

        /// <summary>估算的服务器时间 (秒) = 本地时间 + 偏移。</summary>
        public float ServerTime => _localTime + _serverTimeOffset;

        /// <summary>平滑后的往返时延 (毫秒)。</summary>
        public float SmoothedRtt => _smoothedRtt;

        /// <summary>RTT 方差 (毫秒), 用于估算抖动。</summary>
        public float RttVariation => _rttVariation;

        /// <summary>网络抖动 (毫秒)。</summary>
        public float Jitter => _rttVariation;

        /// <summary>插值延迟 (秒), 客户端渲染时延迟此值以平滑远程实体。</summary>
        public float InterpolationDelay
        {
            get => _interpolationDelay;
            set => _interpolationDelay = Math.Clamp(value, _minInterpDelay, _maxInterpDelay);
        }

        /// <summary>最小插值延迟。</summary>
        public float MinInterpolationDelay
        {
            get => _minInterpDelay;
            set => _minInterpDelay = Math.Clamp(value, 0.016f, 0.1f);
        }

        /// <summary>最大插值延迟。</summary>
        public float MaxInterpolationDelay
        {
            get => _maxInterpDelay;
            set => _maxInterpDelay = Math.Clamp(value, 0.1f, 1f);
        }

        /// <summary>时间同步间隔 (秒)。</summary>
        public float SyncInterval
        {
            get => _syncInterval;
            set => _syncInterval = Math.Clamp(value, 0.5f, 10f);
        }

        /// <summary>已完成的时间同步次数。</summary>
        public int SyncCount => _syncCount;

        /// <summary>是否已初始化 (至少完成一次同步)。</summary>
        public bool IsSynchronized => _syncCount > 0;

        /// <summary>更新本地时钟。</summary>
        public void Update(float dt)
        {
            _localTime += dt;
            _syncTimer += dt;

            // 清理超时的同步请求
            var expired = new List<uint>();
            foreach (var kv in _pendingSyncs)
            {
                if (_localTime - kv.Value > 5f)
                    expired.Add(kv.Key);
            }
            foreach (var id in expired)
                _pendingSyncs.Remove(id);
        }

        /// <summary>
        /// 创建一个时间同步请求包 (客户端发送)。
        /// 返回同步 ID 和当前本地时间戳, 用于后续 RTT 计算。
        /// </summary>
        public uint CreateSyncRequest(out float requestTime)
        {
            requestTime = _localTime;
            uint syncId = ++_nextSyncId;
            _pendingSyncs[syncId] = requestTime;
            return syncId;
        }

        /// <summary>
        /// 处理时间同步响应 (客户端侧)。
        /// 根据请求往返时间更新 RTT 估算与服务器时间偏移。
        /// </summary>
        public void ProcessSyncResponse(uint syncId, float serverTime)
        {
            if (!_pendingSyncs.TryGetValue(syncId, out float requestTime))
                return;
            _pendingSyncs.Remove(syncId);

            float rtt = (_localTime - requestTime) * 1000f; // 转毫秒
            if (rtt < 0 || rtt > 5000) return; // 丢弃异常值

            // EWMA 平滑 RTT (类似 TCP SRTT)
            if (_smoothedRtt == 0)
            {
                _smoothedRtt = rtt;
                _rttVariation = rtt * 0.5f;
            }
            else
            {
                float delta = Math.Abs(rtt - _smoothedRtt);
                _rttVariation = _rttVariation * 0.75f + delta * 0.25f;
                _smoothedRtt = _smoothedRtt * 0.875f + rtt * 0.125f;
            }

            // 估算服务器时间偏移: serverTime = localTime + offset + rtt/2
            float oneWayDelay = rtt * 0.5f;
            float estimatedServerTime = serverTime;
            float newOffset = estimatedServerTime - _localTime - oneWayDelay / 1000f;

            // 平滑偏移更新
            if (_syncCount == 0)
                _serverTimeOffset = newOffset;
            else
                _serverTimeOffset = _serverTimeOffset * 0.9f + newOffset * 0.1f;

            _syncCount++;

            // 自适应插值延迟: RTT/2 + 2*抖动, 限制在 [min, max]
            float targetDelay = (_smoothedRtt * 0.001f * 0.5f) + (_rttVariation * 0.001f * 2f);
            targetDelay = Math.Clamp(targetDelay, _minInterpDelay, _maxInterpDelay);
            _interpolationDelay = _interpolationDelay * 0.9f + targetDelay * 0.1f;
        }

        /// <summary>
        /// 服务器侧: 处理时间同步请求, 返回当前服务器时间。
        /// </summary>
        public float HandleSyncRequest(uint syncId)
        {
            return _localTime;
        }

        /// <summary>是否需要发送时间同步请求。</summary>
        public bool ShouldSendSyncRequest()
        {
            if (_syncTimer >= _syncInterval)
            {
                _syncTimer = 0f;
                return true;
            }
            return false;
        }

        /// <summary>重置时钟状态。</summary>
        public void Reset()
        {
            _localTime = 0f;
            _serverTimeOffset = 0f;
            _smoothedRtt = 0f;
            _rttVariation = 0f;
            _interpolationDelay = 0.1f;
            _syncTimer = 0f;
            _syncCount = 0;
            _nextSyncId = 0;
            _pendingSyncs.Clear();
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 8. 状态快照与插值 (Snapshot Interpolation)
    //    使用环形缓冲区存储历史快照, 在两个快照间插值。
    // ──────────────────────────────────────────────────────────────

    /// <summary>单个实体的单帧状态快照, 用于插值与回滚。</summary>
    public struct EntitySnapshot
    {
        /// <summary>实体网络 ID。</summary>
        public int NetworkId;
        /// <summary>快照时间戳 (服务器时间, 秒)。</summary>
        public float Timestamp;
        /// <summary>位置。</summary>
        public Vector3 Position;
        /// <summary>旋转。</summary>
        public Quaternion Rotation;
        /// <summary>速度。</summary>
        public Vector3 Velocity;
        /// <summary>生命值。</summary>
        public float Health;
        /// <summary>状态码。</summary>
        public int State;
    }

    /// <summary>
    /// 客户端插值缓冲: 使用环形缓冲区保存近期服务器快照,
    /// 按延迟时间在两个快照间线性插值, 使远程实体运动平滑, 抵消网络抖动。
    /// 当无新快照时使用速度进行外插预测。
    /// </summary>
    public sealed class SnapshotInterpolator
    {
        private readonly Dictionary<int, RingBuffer<EntitySnapshot>> _buffers = new();
        private float _interpDelay = 0.1f;
        private float _maxBufferTime = 1.0f;
        private float _maxExtrapolationTime = 0.5f;
        private bool _extrapolationEnabled = true;
        private const int BufferCapacity = 64;

        /// <summary>插值延迟 (秒), 通常略大于平均往返时延的一半。</summary>
        public float InterpolationDelay
        {
            get => _interpDelay;
            set => _interpDelay = Math.Clamp(value, 0.016f, 0.5f);
        }

        /// <summary>最大外插时间 (秒)。</summary>
        public float MaxExtrapolationTime
        {
            get => _maxExtrapolationTime;
            set => _maxExtrapolationTime = Math.Clamp(value, 0.1f, 2f);
        }

        /// <summary>是否启用外插 (速度预测)。</summary>
        public bool ExtrapolationEnabled
        {
            get => _extrapolationEnabled;
            set => _extrapolationEnabled = value;
        }

        /// <summary>推入一帧服务器快照。</summary>
        public void PushSnapshot(EntitySnapshot snap)
        {
            if (!_buffers.TryGetValue(snap.NetworkId, out var buffer))
            {
                buffer = new RingBuffer<EntitySnapshot>(BufferCapacity);
                _buffers[snap.NetworkId] = buffer;
            }
            buffer.Push(snap);
        }

        /// <summary>取插值后的当前状态。</summary>
        public bool TryGetInterpolated(int networkId, float renderTime, out EntitySnapshot result)
        {
            result = default;
            if (!_buffers.TryGetValue(networkId, out var buffer) || buffer.Count < 2)
            {
                if (buffer != null && buffer.Count == 1)
                {
                    result = buffer.Latest;
                    return true;
                }
                return false;
            }

            float targetTime = renderTime - _interpDelay;

            // 在环形缓冲中查找包围 targetTime 的两个快照
            EntitySnapshot prev = default;
            EntitySnapshot next = default;
            bool found = false;

            for (int i = 0; i < buffer.Count - 1; i++)
            {
                var a = buffer[i];
                var b = buffer[i + 1];
                if (a.Timestamp <= targetTime && targetTime <= b.Timestamp)
                {
                    prev = a;
                    next = b;
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                var oldest = buffer.Oldest;
                var latest = buffer.Latest;

                if (targetTime < oldest.Timestamp)
                {
                    // 目标时间早于最早快照, 使用最早快照
                    result = oldest;
                    return true;
                }

                // 目标时间晚于最新快照 -> 外插
                if (_extrapolationEnabled)
                {
                    float extrapolateTime = targetTime - latest.Timestamp;
                    if (extrapolateTime <= _maxExtrapolationTime)
                    {
                        result = new EntitySnapshot
                        {
                            NetworkId = networkId,
                            Timestamp = targetTime,
                            Position = latest.Position + latest.Velocity * extrapolateTime,
                            Rotation = latest.Rotation,
                            Velocity = latest.Velocity,
                            Health = latest.Health,
                            State = latest.State
                        };
                        return true;
                    }
                }

                // 外插超时, 使用最新快照
                result = latest;
                return true;
            }

            // 在 prev 和 next 之间插值
            float span = next.Timestamp - prev.Timestamp;
            float alpha = span > 0.0001f
                ? Math.Clamp((targetTime - prev.Timestamp) / span, 0f, 1f)
                : 0f;

            result = new EntitySnapshot
            {
                NetworkId = networkId,
                Timestamp = targetTime,
                Position = Vector3.Lerp(prev.Position, next.Position, alpha),
                Rotation = Quaternion.Slerp(prev.Rotation, next.Rotation, alpha),
                Velocity = Vector3.Lerp(prev.Velocity, next.Velocity, alpha),
                Health = MathHelper.Lerp(prev.Health, next.Health, alpha),
                State = alpha < 0.5f ? prev.State : next.State
            };
            return true;
        }

        /// <summary>获取指定实体的最新快照。</summary>
        public bool TryGetLatest(int networkId, out EntitySnapshot snapshot)
        {
            snapshot = default;
            if (!_buffers.TryGetValue(networkId, out var buffer) || buffer.Count == 0)
                return false;
            snapshot = buffer.Latest;
            return true;
        }

        /// <summary>移除指定实体的快照缓冲。</summary>
        public void RemoveEntity(int networkId) => _buffers.Remove(networkId);

        /// <summary>清空所有快照缓冲。</summary>
        public void Clear() => _buffers.Clear();

        /// <summary>当前追踪的实体数量。</summary>
        public int TrackedCount => _buffers.Count;
    }


    // ──────────────────────────────────────────────────────────────
    // 9. 客户端预测与服务器回滚 (ClientPredictionSystem)
    //    本地玩家输入立即模拟, 收到服务器权威状态后比对并纠正。
    // ──────────────────────────────────────────────────────────────

    /// <summary>客户端预测的一个输入帧记录, 用于与服务器结果比对。</summary>
    public struct PredictedMove
    {
        /// <summary>输入序列号。</summary>
        public uint InputSequence;
        /// <summary>时间戳。</summary>
        public float Timestamp;
        /// <summary>输入方向。</summary>
        public Vector3 InputDirection;
        /// <summary>帧间隔。</summary>
        public float DeltaTime;
        /// <summary>预测位置。</summary>
        public Vector3 PredictedPosition;
        /// <summary>预测旋转。</summary>
        public Quaternion PredictedRotation;
    }

    /// <summary>
    /// 客户端预测系统: 本地玩家输入立即模拟, 同时记录历史预测;
    /// 收到服务器权威状态后比对, 若有偏差则回滚到服务器状态并重放未确认输入。
    /// </summary>
    public sealed class ClientPredictionSystem
    {
        private const int MaxPendingMoves = 128;

        private readonly Queue<PredictedMove> _pendingMoves = new();
        private uint _localSequence;
        private uint _lastAckedSequence;
        private float _errorTolerance = 0.5f;
        private float _correctionSmoothing = 0.2f;
        private Vector3 _smoothedCorrection;
        private int _correctionCount;

        /// <summary>已确认的最新序列号。</summary>
        public uint LastAckedSequence => _lastAckedSequence;
        /// <summary>待确认的预测帧数量。</summary>
        public int PendingMoveCount => _pendingMoves.Count;
        /// <summary>累计纠正次数。</summary>
        public int CorrectionCount => _correctionCount;

        /// <summary>记录一次本地预测移动。</summary>
        public uint RecordMove(Vector3 input, float dt, Vector3 predictedPos, Quaternion predictedRot)
        {
            if (_pendingMoves.Count >= MaxPendingMoves)
                _pendingMoves.Dequeue();

            _localSequence++;
            var move = new PredictedMove
            {
                InputSequence = _localSequence,
                Timestamp = (float)DateTime.UtcNow.TimeOfDay.TotalSeconds,
                InputDirection = input,
                DeltaTime = dt,
                PredictedPosition = predictedPos,
                PredictedRotation = predictedRot
            };
            _pendingMoves.Enqueue(move);
            return move.InputSequence;
        }

        /// <summary>
        /// 收到服务器权威状态后调用, 比对预测与权威位置。
        /// 返回是否触发了纠正回滚。
        /// </summary>
        public bool ReceiveServerState(uint ackedSequence, Vector3 serverPos, Quaternion serverRot,
            out Vector3 correctedPos, out Quaternion correctedRot)
        {
            correctedPos = serverPos;
            correctedRot = serverRot;
            _lastAckedSequence = ackedSequence;

            // 弹出已确认的预测帧
            while (_pendingMoves.Count > 0 && _pendingMoves.Peek().InputSequence <= ackedSequence)
                _pendingMoves.Dequeue();

            // 比对最近一帧预测与服务器位置
            if (_pendingMoves.Count > 0)
            {
                var latest = _pendingMoves.Peek();
                float error = Vector3.Distance(latest.PredictedPosition, serverPos);
                if (error > _errorTolerance)
                {
                    _correctionCount++;
                    // 平滑纠正: 指数平滑避免瞬移
                    _smoothedCorrection = Vector3.Lerp(_smoothedCorrection, serverPos, _correctionSmoothing);
                    correctedPos = _smoothedCorrection;
                    return true;
                }
            }
            return false;
        }

        /// <summary>重放所有未确认的输入帧。</summary>
        public void ReplayPendingInputs(Action<PredictedMove> applyInput)
        {
            if (applyInput == null) return;
            var moves = _pendingMoves.ToArray();
            _pendingMoves.Clear();
            foreach (var move in moves)
            {
                applyInput(move);
                _pendingMoves.Enqueue(move);
            }
        }

        /// <summary>设置误差容差。</summary>
        public void SetErrorTolerance(float tolerance) => _errorTolerance = Math.Clamp(tolerance, 0.01f, 5f);

        /// <summary>重置预测状态。</summary>
        public void Reset()
        {
            _pendingMoves.Clear();
            _localSequence = 0;
            _lastAckedSequence = 0;
            _smoothedCorrection = Vector3.Zero;
            _correctionCount = 0;
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 10. 延迟补偿 (Lag Compensation)
    //     保存近期世界快照, 在服务器进行命中判定时回放到玩家当时的延迟时刻。
    // ──────────────────────────────────────────────────────────────

    /// <summary>用于延迟补偿的角色碰撞体快照。</summary>
    public struct HitboxSnapshot
    {
        /// <summary>实体网络 ID。</summary>
        public int NetworkId;
        /// <summary>时间戳。</summary>
        public float Timestamp;
        /// <summary>位置。</summary>
        public Vector3 Position;
        /// <summary>半尺寸 (AABB)。</summary>
        public Vector3 HalfExtents;
        /// <summary>生命值。</summary>
        public float Health;
    }

    /// <summary>
    /// 延迟补偿管理器: 维护每个实体最近 1 秒的位置历史,
    /// 服务器收到射击请求时按客户端延迟回放历史, 进行射线命中判定。
    /// </summary>
    public sealed class LagCompensator
    {
        private readonly Dictionary<int, List<HitboxSnapshot>> _history = new();
        private float _historyWindow = 1.0f;
        private float _maxLagCompensation = 0.5f;
        private bool _enabled = true;

        /// <summary>是否启用延迟补偿。</summary>
        public bool Enabled
        {
            get => _enabled;
            set => _enabled = value;
        }

        /// <summary>每帧记录实体碰撞体快照。</summary>
        public void RecordSnapshot(int networkId, float time, Vector3 pos, Vector3 halfExtents, float health)
        {
            if (!_enabled) return;
            if (!_history.TryGetValue(networkId, out var list))
            {
                list = new List<HitboxSnapshot>();
                _history[networkId] = list;
            }
            list.Add(new HitboxSnapshot
            {
                NetworkId = networkId,
                Timestamp = time,
                Position = pos,
                HalfExtents = halfExtents,
                Health = health
            });
            // 裁剪超出历史窗口的旧记录
            float cutoff = time - _historyWindow;
            while (list.Count > 2 && list[0].Timestamp < cutoff)
                list.RemoveAt(0);
        }

        /// <summary>
        /// 在指定回放时刻进行射线检测, 返回命中的实体列表。
        /// </summary>
        public List<int> RaycastAtTime(Vector3 rayOrigin, Vector3 rayDir, float targetTime, float maxDist)
        {
            var hits = new List<int>();
            if (!_enabled) return hits;
            float clampedTime = Math.Max(targetTime, targetTime - _maxLagCompensation);
            foreach (var kv in _history)
            {
                var list = kv.Value;
                if (list.Count == 0) continue;
                // 找到最接近 targetTime 的快照
                HitboxSnapshot snap = default;
                float bestDelta = float.MaxValue;
                foreach (var s in list)
                {
                    float delta = Math.Abs(s.Timestamp - clampedTime);
                    if (delta < bestDelta) { bestDelta = delta; snap = s; }
                }
                if (RayBoxIntersect(rayOrigin, rayDir, snap.Position, snap.HalfExtents, maxDist))
                    hits.Add(snap.NetworkId);
            }
            return hits;
        }

        /// <summary>射线与轴对齐包围盒相交判定。</summary>
        private static bool RayBoxIntersect(Vector3 origin, Vector3 dir, Vector3 center, Vector3 halfExtents, float maxDist)
        {
            Vector3 min = center - halfExtents;
            Vector3 max = center + halfExtents;
            float tmin = 0f, tmax = maxDist;
            for (int i = 0; i < 3; i++)
            {
                float o = i == 0 ? origin.X : (i == 1 ? origin.Y : origin.Z);
                float d = i == 0 ? dir.X : (i == 1 ? dir.Y : dir.Z);
                float lo = i == 0 ? min.X : (i == 1 ? min.Y : min.Z);
                float hi = i == 0 ? max.X : (i == 1 ? max.Y : max.Z);
                if (Math.Abs(d) < 1e-6f)
                {
                    if (o < lo || o > hi) return false;
                }
                else
                {
                    float inv = 1f / d;
                    float t1 = (lo - o) * inv;
                    float t2 = (hi - o) * inv;
                    if (t1 > t2) (t1, t2) = (t2, t1);
                    tmin = Math.Max(tmin, t1);
                    tmax = Math.Min(tmax, t2);
                    if (tmin > tmax) return false;
                }
            }
            return true;
        }

        /// <summary>清空所有历史记录。</summary>
        public void Clear() => _history.Clear();
    }

    // ──────────────────────────────────────────────────────────────
    // 11. Delta 压缩 (DeltaCompression)
    //     仅序列化自上次同步以来发生变化的属性, 配合位掩码标识。
    //     支持脏标记 + 哈希对比双重检测。
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Delta 压缩器: 对比属性新旧值 (脏标记 + 哈希对比),
    /// 生成包含变化位掩码的紧凑数据。
    /// </summary>
    public sealed class DeltaCompressor
    {
        /// <summary>
        /// 序列化一个复制对象的变化属性, 返回是否产生了数据。
        /// 结合脏标记与哈希对比双重检测: 即使脏标记未设置, 哈希变化也会触发同步。
        /// </summary>
        public bool SerializeDelta(ReplicatedObject obj, BitWriter writer)
        {
            int propCount = obj.Properties.Count;
            if (propCount == 0) return false;

            // 第一遍: 统计脏属性并构建位掩码 (脏标记 + 哈希对比)
            Span<bool> dirty = propCount <= 128 ? stackalloc bool[128] : new bool[propCount];
            int dirtyCount = 0;
            for (int i = 0; i < propCount; i++)
            {
                var prop = obj.Properties[i];
                bool isDirty = prop.IsDirty;

                // 哈希对比: 即使脏标记未设置, 也检测值是否真正变化
                if (!isDirty && prop.Member != null && obj.Target != null)
                {
                    ulong currentHash = ComputePropertyHash(prop, obj.Target);
                    if (currentHash != prop.LastSentHash)
                    {
                        isDirty = true;
                        prop.IsDirty = true;
                    }
                }

                dirty[i] = isDirty;
                if (isDirty) dirtyCount++;
            }

            if (dirtyCount == 0) return false;

            // 写入位掩码: 每个属性一位, 1=变化需同步
            writer.AlignToByte();
            for (int i = 0; i < propCount; i++)
                writer.WriteBit(dirty[i]);

            // 第二遍: 仅写入脏属性的值
            for (int i = 0; i < propCount; i++)
            {
                if (!dirty[i]) continue;
                var prop = obj.Properties[i];
                WritePropertyValue(writer, prop, obj.Target);
                prop.IsDirty = false;
                prop.LastSentValue = ReadPropertyValue(prop, obj.Target);
                prop.LastSentHash = ComputePropertyHash(prop, obj.Target);
                prop.SendCounter++;
            }
            return true;
        }

        /// <summary>计算属性值的哈希 (用于变化检测)。</summary>
        private static ulong ComputePropertyHash(ReplicatedProperty prop, object target)
        {
            object? value = ReadPropertyValue(prop, target);
            return PropertyHasher.ComputeHash(value);
        }

        /// <summary>将单个属性值按类型写入位流。</summary>
        private void WritePropertyValue(BitWriter writer, ReplicatedProperty prop, object? target)
        {
            object? value = ReadPropertyValue(prop, target);
            if (value == null) { writer.WriteBit(false); return; }

            int bits = prop.QuantizationBits;
            switch (value)
            {
                case bool b: writer.WriteBit(b); break;
                case int i: writer.WriteInt(i); break;
                case uint u: writer.WriteUInt(u); break;
                case float f:
                    if (bits > 0 && bits < 32)
                        writer.WriteFloatQuantized(f, -100000f, 100000f, bits);
                    else
                        writer.WriteBits(BitConverter.SingleToUInt32Bits(f), 32);
                    break;
                case Vector3 v:
                    if (bits > 0 && bits < 32)
                        writer.WriteVector3(v, -100000f, 100000f, bits);
                    else
                    {
                        writer.WriteBits(BitConverter.SingleToUInt32Bits(v.X), 32);
                        writer.WriteBits(BitConverter.SingleToUInt32Bits(v.Y), 32);
                        writer.WriteBits(BitConverter.SingleToUInt32Bits(v.Z), 32);
                    }
                    break;
                case Quaternion q: writer.WriteQuaternion(q); break;
                case string s: writer.WriteString(s); break;
                default:
                    writer.WriteString(System.Text.Json.JsonSerializer.Serialize(value));
                    break;
            }
        }

        /// <summary>通过反射读取属性当前值。</summary>
        private static object? ReadPropertyValue(ReplicatedProperty prop, object? target)
        {
            if (target == null || prop.Member == null) return null;
            if (prop.Member is FieldInfo fi) return fi.GetValue(target);
            if (prop.Member is PropertyInfo pi) return pi.GetValue(target);
            return null;
        }

        /// <summary>标记某属性为脏 (发生变化, 待同步)。</summary>
        public static void MarkDirty(ReplicatedObject obj, string propertyName)
        {
            foreach (var p in obj.Properties)
                if (p.Name == propertyName) { p.IsDirty = true; return; }
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 12. 连接管理与心跳 (ConnectionManager)
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 连接管理器: 封装心跳发送、超时检测、可靠包重传与滑动窗口 ACK。
    /// 配合 NetworkSystem.NetworkConnection 使用。
    /// </summary>
    public sealed class ConnectionManager
    {
        private readonly Dictionary<Guid, NetworkConnection> _connections = new();
        private float _heartbeatInterval = 2.0f;
        private float _timeoutThreshold = 30f;
        private float _reliableResendInterval = 0.2f;
        private float _timer;

        /// <summary>连接超时事件。</summary>
        public event Action<NetworkConnection>? OnConnectionTimeout;
        /// <summary>连接被踢出事件。</summary>
        public event Action<NetworkConnection>? OnConnectionKicked;

        /// <summary>注册一条连接进入管理。</summary>
        public void AddConnection(NetworkConnection conn) => _connections[conn.Id] = conn;

        /// <summary>移除连接。</summary>
        public void RemoveConnection(Guid id) => _connections.Remove(id);

        /// <summary>每帧更新: 心跳与超时检测。</summary>
        public void Update(float dt)
        {
            _timer += dt;
            bool sendHeartbeat = _timer >= _heartbeatInterval;
            if (sendHeartbeat) _timer = 0f;

            var timedOut = new List<Guid>();
            foreach (var conn in _connections.Values)
            {
                if (!conn.IsConnected) continue;
                conn.TimeSinceLastPacket += dt;
                if (sendHeartbeat)
                    QueueHeartbeat(conn);
                if (conn.TimeSinceLastPacket > _timeoutThreshold)
                    timedOut.Add(conn.Id);
                ResendUnacked(conn, dt);
            }
            foreach (var id in timedOut)
            {
                if (_connections.TryGetValue(id, out var c))
                {
                    c.State = ConnectionState.TimedOut;
                    OnConnectionTimeout?.Invoke(c);
                }
                _connections.Remove(id);
            }
        }

        private void QueueHeartbeat(NetworkConnection conn)
        {
            var hb = new NetworkPacket
            {
                Type = PacketType.Heartbeat,
                SequenceNumber = ++conn.LastSequenceNumber,
                Timestamp = DateTime.Now,
                IsReliable = false
            };
            conn.SendQueue.Enqueue(hb);
        }

        /// <summary>处理收到的 ACK, 滑动窗口确认。</summary>
        public void ProcessAck(NetworkConnection conn, int ack, uint ackBits)
        {
            for (int i = 0; i < 32; i++)
            {
                int seq = ack - i;
                if (seq < 0) continue;
                bool isAcked = (ackBits & (1u << i)) != 0;
                if (isAcked && conn.UnackedPackets.ContainsKey(seq))
                {
                    conn.UnackedPackets.Remove(seq);
                    conn.LastAcked = (uint)seq;
                }
            }
        }

        private void ResendUnacked(NetworkConnection conn, float dt)
        {
            var toResend = new List<NetworkPacket>();
            foreach (var kv in conn.UnackedPackets)
            {
                var packet = kv.Value;
                if (!packet.IsReliable) continue;
                packet.RetryCount++;
                if ((DateTime.Now - packet.LastSendTime).TotalSeconds > _reliableResendInterval)
                {
                    packet.LastSendTime = DateTime.Now;
                    toResend.Add(packet);
                }
            }
            foreach (var p in toResend)
                conn.SendQueue.Enqueue(p);
        }

        /// <summary>活跃连接数。</summary>
        public int ActiveCount
        {
            get
            {
                int n = 0;
                foreach (var c in _connections.Values) if (c.IsConnected) n++;
                return n;
            }
        }

        /// <summary>获取所有已注册连接的只读快照。</summary>
        public IReadOnlyList<NetworkConnection> Connections => new List<NetworkConnection>(_connections.Values);

        /// <summary>移除所有连接 (不触发超时事件)。</summary>
        public void Clear() => _connections.Clear();
    }

    // ──────────────────────────────────────────────────────────────
    // 13. 房间 / 大厅系统 (Room & Lobby)
    // ──────────────────────────────────────────────────────────────

    /// <summary>房间状态。</summary>
    public enum RoomState { Lobby, Loading, InGame, Ended }

    /// <summary>网络房间, 聚合一组连接, 维护房间内共享状态。</summary>
    public sealed class NetworkRoom
    {
        /// <summary>房间 ID。</summary>
        public Guid RoomId = Guid.NewGuid();
        /// <summary>房间名称。</summary>
        public string Name = "Room";
        /// <summary>地图名称。</summary>
        public string MapName = "Default";
        /// <summary>最大玩家数。</summary>
        public int MaxPlayers = 16;
        /// <summary>房间状态。</summary>
        public RoomState State = RoomState.Lobby;
        /// <summary>房间成员。</summary>
        public readonly Dictionary<Guid, NetworkConnection> Members = new();
        /// <summary>房间自定义属性。</summary>
        public readonly Dictionary<string, string> Properties = new();
        /// <summary>创建时间。</summary>
        public DateTime CreatedAt = DateTime.Now;
        /// <summary>主机连接 ID。</summary>
        public Guid HostConnectionId;

        /// <summary>当前玩家数。</summary>
        public int PlayerCount => Members.Count;
        /// <summary>房间是否已满。</summary>
        public bool IsFull => Members.Count >= MaxPlayers;
        /// <summary>是否包含指定成员。</summary>
        public bool HasMember(Guid connId) => Members.ContainsKey(connId);

        /// <summary>加入房间。</summary>
        public bool Join(NetworkConnection conn)
        {
            if (IsFull || State != RoomState.Lobby) return false;
            Members[conn.Id] = conn;
            return true;
        }

        /// <summary>离开房间。</summary>
        public void Leave(Guid connId) => Members.Remove(connId);

        /// <summary>广播房间内消息给所有成员 (除发送者)。</summary>
        public void Broadcast(NetworkPacket packet, Guid? except = null)
        {
            foreach (var conn in Members.Values)
            {
                if (except.HasValue && conn.Id == except.Value) continue;
                if (conn.IsConnected) conn.SendQueue.Enqueue(packet);
            }
        }
    }

    /// <summary>
    /// 大厅系统: 管理多个房间的创建、查询、匹配与销毁。
    /// </summary>
    public sealed class LobbySystem
    {
        private readonly Dictionary<Guid, NetworkRoom> _rooms = new();
        private readonly Dictionary<Guid, Guid> _connectionToRoom = new();
        private int _nextRoomNumber = 1;

        /// <summary>房间创建事件。</summary>
        public event Action<NetworkRoom>? OnRoomCreated;
        /// <summary>房间销毁事件。</summary>
        public event Action<NetworkRoom>? OnRoomDestroyed;

        /// <summary>创建一个新房间。</summary>
        public NetworkRoom CreateRoom(string name, string map, int maxPlayers, Guid hostConnId)
        {
            var room = new NetworkRoom
            {
                Name = string.IsNullOrEmpty(name) ? $"Room-{_nextRoomNumber++}" : name,
                MapName = map,
                MaxPlayers = maxPlayers,
                HostConnectionId = hostConnId
            };
            _rooms[room.RoomId] = room;
            OnRoomCreated?.Invoke(room);
            return room;
        }

        /// <summary>按匹配规则寻找一个可加入的房间, 找不到则创建。</summary>
        public NetworkRoom Matchmake(NetworkConnection conn, string map, int maxPlayers)
        {
            foreach (var room in _rooms.Values)
            {
                if (room.State == RoomState.Lobby && !room.IsFull && room.MapName == map)
                {
                    if (room.Join(conn))
                    {
                        _connectionToRoom[conn.Id] = room.RoomId;
                        return room;
                    }
                }
            }
            var newRoom = CreateRoom(null, map, maxPlayers, conn.Id);
            newRoom.Join(conn);
            _connectionToRoom[conn.Id] = newRoom.RoomId;
            return newRoom;
        }

        /// <summary>销毁房间。</summary>
        public void DestroyRoom(Guid roomId)
        {
            if (_rooms.TryGetValue(roomId, out var room))
            {
                foreach (var memberId in room.Members.Keys)
                    _connectionToRoom.Remove(memberId);
                _rooms.Remove(roomId);
                OnRoomDestroyed?.Invoke(room);
            }
        }

        /// <summary>获取连接所在房间。</summary>
        public NetworkRoom? GetRoomOfConnection(Guid connId)
        {
            if (_connectionToRoom.TryGetValue(connId, out var roomId) && _rooms.TryGetValue(roomId, out var room))
                return room;
            return null;
        }

        /// <summary>列出所有房间。</summary>
        public List<NetworkRoom> ListRooms() => new(_rooms.Values);

        /// <summary>房间数量。</summary>
        public int RoomCount => _rooms.Count;

        /// <summary>销毁所有房间, 触发 OnRoomDestroyed 事件。</summary>
        public void ClearRooms()
        {
            foreach (var room in _rooms.Values)
                OnRoomDestroyed?.Invoke(room);
            _rooms.Clear();
            _connectionToRoom.Clear();
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 14. 传输层抽象 (INetworkTransport)
    //    定义统一的发送/接收接口, 可对接 ENet、LiteNetLib 等底层库。
    // ──────────────────────────────────────────────────────────────

    /// <summary>传输层统计信息。</summary>
    public struct TransportStats
    {
        /// <summary>已发送字节数。</summary>
        public ulong BytesSent;
        /// <summary>已接收字节数。</summary>
        public ulong BytesReceived;
        /// <summary>已发送包数。</summary>
        public ulong PacketsSent;
        /// <summary>已接收包数。</summary>
        public ulong PacketsReceived;
        /// <summary>丢包数。</summary>
        public ulong PacketsLost;
    }

    /// <summary>
    /// 网络传输层抽象接口。
    /// 实现方需对接具体库 (如 ENet / LiteNetLib), 提供:
    ///   * 服务器绑定与监听
    ///   * 客户端连接
    ///   * 可靠/不可靠通道发送
    ///   * 事件驱动的接收回调
    /// </summary>
    public interface INetworkTransport
    {
        /// <summary>是否为服务器模式。</summary>
        bool IsServer { get; }
        /// <summary>是否正在运行。</summary>
        bool IsRunning { get; }

        /// <summary>启动服务器。</summary>
        bool StartServer(int port, int maxConnections);
        /// <summary>作为客户端连接远端。</summary>
        bool Connect(string host, int port);
        /// <summary>断开指定连接。</summary>
        void Disconnect(Guid connectionId);
        /// <summary>停止传输层。</summary>
        void Stop();

        /// <summary>发送数据到指定连接, 指定可靠性。</summary>
        void Send(Guid connectionId, byte[] data, int length, DeliveryMode mode);
        /// <summary>广播给所有连接。</summary>
        void Broadcast(byte[] data, int length, DeliveryMode mode);

        /// <summary>轮询接收到的数据 (非阻塞)。</summary>
        bool Poll(out TransportEvent evt);
        /// <summary>获取传输层统计。</summary>
        TransportStats GetStats();
    }

    /// <summary>传输层事件类型。</summary>
    public enum TransportEventType { Connect, Disconnect, DataReceive, Error }

    /// <summary>传输层事件。</summary>
    public struct TransportEvent
    {
        /// <summary>事件类型。</summary>
        public TransportEventType Type;
        /// <summary>连接 ID。</summary>
        public Guid ConnectionId;
        /// <summary>负载数据。</summary>
        public byte[] Data;
        /// <summary>负载长度。</summary>
        public int DataLength;
        /// <summary>附加消息。</summary>
        public string Message;
    }

    /// <summary>
    /// 基于 ENet 风格的内存环回传输层实现。
    /// 不依赖原生库: 在单进程内模拟服务器/客户端数据收发, Send/Broadcast 会将数据
    /// 原样投递到本地事件队列 (环回), 使复制管线可在无网络环境下端到端运行。
    /// 支持每连接发送序列号、可靠性模式与可选丢包模拟 (用于测试插值/重传补偿路径)。
    /// 真实项目可替换为 ENet P/Invoke 或 LiteNetLib 实现 (接口保持不变)。
    /// </summary>
    public sealed class EnetTransportStub : INetworkTransport
    {
        private struct ConnState
        {
            public Guid Id;
            public bool IsConnected;
            public uint SendSeq;
            public uint RecvSeq;
        }

        private bool _isServer;
        private bool _running;
        private readonly Queue<TransportEvent> _events = new();
        private readonly Dictionary<Guid, ConnState> _connections = new();
        private TransportStats _stats;
        private readonly Random _rng = new();

        /// <summary>是否将发送数据环回到本地接收队列 (单进程端到端测试)。默认开启。</summary>
        public bool Loopback { get; set; } = true;
        /// <summary>模拟丢包率 [0,1], 0 表示不丢包。仅作用于不可靠数据包。</summary>
        public float SimulatedLoss { get; set; } = 0f;
        /// <summary>模拟延迟 (毫秒), 0 表示无延迟。</summary>
        public float SimulatedLatency { get; set; } = 0f;

        /// <summary>是否为服务器。</summary>
        public bool IsServer => _isServer;
        /// <summary>是否正在运行。</summary>
        public bool IsRunning => _running;

        /// <summary>启动服务器。</summary>
        public bool StartServer(int port, int maxConnections)
        {
            _isServer = true;
            _running = true;
            return true;
        }

        /// <summary>作为客户端连接。</summary>
        public bool Connect(string host, int port)
        {
            _isServer = false;
            _running = true;
            var id = Guid.NewGuid();
            _connections[id] = new ConnState { Id = id, IsConnected = true };
            _events.Enqueue(new TransportEvent { Type = TransportEventType.Connect, ConnectionId = id });
            return true;
        }

        /// <summary>断开指定连接。</summary>
        public void Disconnect(Guid connectionId)
        {
            if (_connections.Remove(connectionId))
                _events.Enqueue(new TransportEvent { Type = TransportEventType.Disconnect, ConnectionId = connectionId });
        }

        /// <summary>停止传输层。</summary>
        public void Stop()
        {
            _running = false;
            foreach (var c in _connections.Values)
                _events.Enqueue(new TransportEvent { Type = TransportEventType.Disconnect, ConnectionId = c.Id });
            _connections.Clear();
        }

        /// <summary>发送数据。</summary>
        public void Send(Guid connectionId, byte[] data, int length, DeliveryMode mode)
        {
            if (!_running || data == null || length <= 0) return;
            _stats.PacketsSent++;
            _stats.BytesSent += (ulong)length;

            if (!_connections.TryGetValue(connectionId, out var conn) || !conn.IsConnected)
                return;

            bool reliable = mode == DeliveryMode.ReliableUnordered || mode == DeliveryMode.ReliableOrdered;
            uint seq = ++conn.SendSeq;
            _connections[connectionId] = conn;

            if (!reliable && SimulatedLoss > 0f && _rng.NextDouble() < SimulatedLoss)
            {
                _stats.PacketsLost++;
                return;
            }

            DeliverLoopback(connectionId, data, length, seq);
        }

        private void DeliverLoopback(Guid connectionId, byte[] data, int length, uint seq)
        {
            if (!Loopback) return;
            var copy = new byte[length];
            Buffer.BlockCopy(data, 0, copy, 0, length);
            _events.Enqueue(new TransportEvent
            {
                Type = TransportEventType.DataReceive,
                ConnectionId = connectionId,
                Data = copy,
                DataLength = length,
                Message = seq.ToString()
            });
            _stats.PacketsReceived++;
            _stats.BytesReceived += (ulong)length;
        }

        /// <summary>广播数据。</summary>
        public void Broadcast(byte[] data, int length, DeliveryMode mode)
        {
            var ids = new List<Guid>(_connections.Keys);
            foreach (var id in ids)
                Send(id, data, length, mode);
        }

        /// <summary>轮询事件。</summary>
        public bool Poll(out TransportEvent evt)
        {
            if (_events.Count > 0) { evt = _events.Dequeue(); return true; }
            evt = default;
            return false;
        }

        /// <summary>获取统计。</summary>
        public TransportStats GetStats() => _stats;
    }

    // ──────────────────────────────────────────────────────────────
    // 15. 服务器权威输入队列 (ServerInputQueue)
    //     服务器端收集客户端输入, 按序列号排序处理。
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 服务器权威输入队列: 收集来自客户端的输入帧,
    /// 按序列号排序后顺序处理, 保证服务器权威状态的一致性。
    /// </summary>
    public sealed class ServerInputQueue
    {
        private struct QueuedInput
        {
            public uint Sequence;
            public Guid SenderConnection;
            public float Timestamp;
            public Vector3 InputDirection;
            public float DeltaTime;
            public byte[]? ExtraData;
        }

        private readonly Dictionary<Guid, List<QueuedInput>> _inputs = new();
        private readonly Dictionary<Guid, uint> _lastProcessed = new();
        private const int MaxInputsPerConnection = 64;

        /// <summary>推入一个客户端输入。</summary>
        public void PushInput(Guid connectionId, uint sequence, Vector3 inputDir, float dt, byte[]? extraData = null)
        {
            if (!_inputs.TryGetValue(connectionId, out var list))
            {
                list = new List<QueuedInput>();
                _inputs[connectionId] = list;
            }

            // 丢弃已处理过的输入
            if (_lastProcessed.TryGetValue(connectionId, out uint lastSeq) && sequence <= lastSeq)
                return;

            list.Add(new QueuedInput
            {
                Sequence = sequence,
                SenderConnection = connectionId,
                Timestamp = (float)DateTime.UtcNow.TimeOfDay.TotalSeconds,
                InputDirection = inputDir,
                DeltaTime = dt,
                ExtraData = extraData
            });

            // 按序列号排序
            list.Sort((a, b) => a.Sequence.CompareTo(b.Sequence));

            // 限制队列长度
            if (list.Count > MaxInputsPerConnection)
                list.RemoveRange(0, list.Count - MaxInputsPerConnection);
        }

        /// <summary>尝试取出下一个待处理的输入 (按序列号顺序)。</summary>
        public bool TryDequeue(Guid connectionId, out uint sequence, out Vector3 inputDir, out float dt, out byte[]? extraData)
        {
            sequence = 0;
            inputDir = Vector3.Zero;
            dt = 0f;
            extraData = null;

            if (!_inputs.TryGetValue(connectionId, out var list) || list.Count == 0)
                return false;

            var input = list[0];
            list.RemoveAt(0);
            _lastProcessed[connectionId] = input.Sequence;

            sequence = input.Sequence;
            inputDir = input.InputDirection;
            dt = input.DeltaTime;
            extraData = input.ExtraData;
            return true;
        }

        /// <summary>获取指定连接最后处理的输入序列号。</summary>
        public uint GetLastProcessedSequence(Guid connectionId)
        {
            return _lastProcessed.TryGetValue(connectionId, out var seq) ? seq : 0;
        }

        /// <summary>清空指定连接的输入队列。</summary>
        public void ClearConnection(Guid connectionId)
        {
            _inputs.Remove(connectionId);
            _lastProcessed.Remove(connectionId);
        }

        /// <summary>清空所有输入队列。</summary>
        public void Clear()
        {
            _inputs.Clear();
            _lastProcessed.Clear();
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 16. 网络复制系统主类 (NetworkReplicationSystem)
    //    串联上述所有子系统, 驱动整个复制管线。
    //    实现服务器权威模型: 服务器为权威, 客户端发送输入。
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 网络复制系统主类。
    /// 负责调度属性复制、RPC 调度、相关性筛选、Delta 压缩、
    /// 插值/预测/延迟补偿, 以及房间与连接管理。
    /// 实现服务器权威模型: 服务器持有权威状态, 客户端仅发送输入;
    /// 服务器通过增量同步 (脏标记 + 哈希对比) 将变化广播给客户端。
    /// </summary>
    public sealed class NetworkReplicationSystem
    {
        private readonly Dictionary<int, ReplicatedObject> _objects = new();
        private readonly RPCRegistry _rpcRegistry = new();
        private readonly DeltaCompressor _delta = new();
        private readonly SnapshotInterpolator _interpolator = new();
        private readonly ClientPredictionSystem _prediction = new();
        private readonly LagCompensator _lagComp = new();
        private readonly ConnectionManager _connMgr = new();
        private readonly LobbySystem _lobby = new();
        private readonly NetworkClock _clock = new();
        private readonly ServerInputQueue _serverInputs = new();
        private INetworkTransport _transport;
        private bool _isServer;
        private float _replicationRate = 30f;
        private float _accumulator;
        private int _nextNetworkId = 1;
        private RelevanceQuery _relevanceQuery;
        private int _maxObjectsPerTick = 64;

        // 协议标签
        private const byte TagRPC = 0;
        private const byte TagReplication = 1;
        private const byte TagTimeSync = 2;
        private const byte TagPlayerInput = 3;

        private readonly EngineTime _engineTime = new();
        private float _totalTime;
        private bool _disposed;

        /// <summary>RPC 注册表。</summary>
        public RPCRegistry RPCs => _rpcRegistry;
        /// <summary>插值器 (客户端用于平滑远程实体)。</summary>
        public SnapshotInterpolator Interpolator => _interpolator;
        /// <summary>客户端预测系统。</summary>
        public ClientPredictionSystem Prediction => _prediction;
        /// <summary>延迟补偿器。</summary>
        public LagCompensator LagCompensation => _lagComp;
        /// <summary>连接管理器。</summary>
        public ConnectionManager Connections => _connMgr;
        /// <summary>大厅系统。</summary>
        public LobbySystem Lobby => _lobby;
        /// <summary>网络时钟。</summary>
        public NetworkClock Clock => _clock;
        /// <summary>服务器输入队列 (仅服务器使用)。</summary>
        public ServerInputQueue ServerInputs => _serverInputs;

        /// <summary>是否为服务器。</summary>
        public bool IsServer => _isServer;
        /// <summary>是否为客户端。</summary>
        public bool IsClient => !_isServer;
        /// <summary>复制对象数量。</summary>
        public int ObjectCount => _objects.Count;
        /// <summary>复制速率 (Hz)。</summary>
        public float ReplicationRate
        {
            get => _replicationRate;
            set => _replicationRate = Math.Clamp(value, 1f, 120f);
        }

        /// <summary>构造网络复制系统。</summary>
        public NetworkReplicationSystem()
        {
            _transport = new EnetTransportStub();
            _relevanceQuery = new RelevanceQuery();
        }

        /// <summary>
        /// 从引擎 NetworkConfig 初始化复制系统。
        /// </summary>
        public void Initialize(ShanyuEngine.Core.NetworkConfig config)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));

            _isServer = config.IsServer;
            ReplicationRate = (float)config.TickRate;
            _maxObjectsPerTick = Math.Clamp(config.MaxConnections, 8, 512);

            if (!_transport.IsRunning)
            {
                if (_isServer)
                    _transport.StartServer(config.Port, config.MaxConnections);
                else
                    _transport.Connect(config.ServerAddress, config.Port);
            }

            _totalTime = 0f;
            _accumulator = 0f;
            _engineTime.Reset();
            _clock.Reset();
        }

        /// <summary>以秒为步长驱动复制管线。</summary>
        public void Update(float dt)
        {
            if (_disposed || dt <= 0f) return;

            _totalTime += dt;
            _engineTime.DeltaTime = dt;
            _engineTime.TotalTime = _totalTime;
            Update(_engineTime);
        }

        /// <summary>释放复制系统资源。</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
            _rpcRegistry.Clear();
            _connMgr.Clear();
            _lobby.ClearRooms();
            _serverInputs.Clear();
        }

        /// <summary>注入自定义传输层实现。</summary>
        public void SetTransport(INetworkTransport transport) => _transport = transport;

        /// <summary>作为服务器启动复制系统。</summary>
        public void StartServer(int port, int maxPlayers)
        {
            _isServer = true;
            _transport.StartServer(port, maxPlayers);
        }

        /// <summary>作为客户端启动并连接服务器。</summary>
        public void StartClient(string host, int port)
        {
            _isServer = false;
            _transport.Connect(host, port);
        }

        /// <summary>停止复制系统。</summary>
        public void Stop()
        {
            _transport.Stop();
            _objects.Clear();
            _interpolator.Clear();
            _prediction.Reset();
            _lagComp.Clear();
            _clock.Reset();
            _serverInputs.Clear();
        }

        // ── 对象注册 ──────────────────────────────────────────────

        /// <summary>注册一个可复制对象, 通过反射收集其 [Replicated] 字段。</summary>
        public int RegisterObject(object target)
        {
            int id = _nextNetworkId++;
            var robj = new ReplicatedObject
            {
                NetworkId = id,
                Target = target,
                ObjectType = target.GetType(),
                LastUpdateTime = DateTime.Now
            };
            CollectReplicatedProperties(robj);
            _objects[id] = robj;
            return id;
        }

        /// <summary>注销可复制对象。</summary>
        public void UnregisterObject(int networkId)
        {
            _objects.Remove(networkId);
            _interpolator.RemoveEntity(networkId);
        }

        /// <summary>获取可复制对象。</summary>
        public ReplicatedObject? GetObject(int networkId) =>
            _objects.TryGetValue(networkId, out var o) ? o : null;

        /// <summary>反射收集 [Replicated] 标记的字段与属性。</summary>
        private void CollectReplicatedProperties(ReplicatedObject robj)
        {
            var type = robj.ObjectType!;
            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                var attr = field.GetCustomAttribute<ReplicatedAttribute>();
                if (attr == null) continue;
                var prop = new ReplicatedProperty
                {
                    Name = field.Name,
                    Member = field,
                    ValueType = field.FieldType,
                    Reliable = attr.Reliable,
                    Priority = attr.Priority,
                    MaxUpdateRate = attr.MaxUpdateRate,
                    Condition = attr.Condition,
                    QuantizationBits = attr.QuantizationBits,
                    IsDirty = true
                };
                // 初始化哈希
                prop.LastSentHash = PropertyHasher.ComputeHash(ReadPropertyValue(prop, robj.Target));
                robj.Properties.Add(prop);
            }
            foreach (var prop in type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                var attr = prop.GetCustomAttribute<ReplicatedAttribute>();
                if (attr == null) continue;
                var rp = new ReplicatedProperty
                {
                    Name = prop.Name,
                    Member = prop,
                    ValueType = prop.PropertyType,
                    Reliable = attr.Reliable,
                    Priority = attr.Priority,
                    MaxUpdateRate = attr.MaxUpdateRate,
                    Condition = attr.Condition,
                    QuantizationBits = attr.QuantizationBits,
                    IsDirty = true
                };
                rp.LastSentHash = PropertyHasher.ComputeHash(ReadPropertyValue(rp, robj.Target));
                robj.Properties.Add(rp);
            }
        }

        /// <summary>通过反射读取属性当前值。</summary>
        private static object? ReadPropertyValue(ReplicatedProperty prop, object? target)
        {
            if (target == null || prop.Member == null) return null;
            if (prop.Member is FieldInfo fi) return fi.GetValue(target);
            if (prop.Member is PropertyInfo pi) return pi.GetValue(target);
            return null;
        }

        /// <summary>标记某对象的某属性为脏。</summary>
        public void MarkDirty(int networkId, string propertyName)
        {
            if (_objects.TryGetValue(networkId, out var obj))
                DeltaCompressor.MarkDirty(obj, propertyName);
        }

        // ── RPC 调用入口 ──────────────────────────────────────────

        /// <summary>调用一个 Server RPC (客户端 -> 服务器)。</summary>
        public void CallServerRPC(string name, Action<BitWriter>? writeArgs = null, DeliveryMode mode = DeliveryMode.ReliableOrdered)
        {
            var writer = new BitWriter(256);
            writer.WriteBits(TagRPC, 8);
            writer.WriteString(name);
            writeArgs?.Invoke(writer);
            var data = writer.ToArray();
            _transport.Send(Guid.Empty, data, data.Length, mode);
        }

        /// <summary>调用一个 Client RPC (服务器 -> 拥有者客户端)。</summary>
        public void CallClientRPC(Guid targetConn, string name, Action<BitWriter>? writeArgs = null, DeliveryMode mode = DeliveryMode.ReliableOrdered)
        {
            var writer = new BitWriter(256);
            writer.WriteBits(TagRPC, 8);
            writer.WriteString(name);
            writeArgs?.Invoke(writer);
            var data = writer.ToArray();
            _transport.Send(targetConn, data, data.Length, mode);
        }

        /// <summary>调用一个 Multicast RPC (服务器 -> 所有客户端)。</summary>
        public void CallMulticastRPC(string name, Action<BitWriter>? writeArgs = null, DeliveryMode mode = DeliveryMode.ReliableOrdered)
        {
            var writer = new BitWriter(256);
            writer.WriteBits(TagRPC, 8);
            writer.WriteString(name);
            writeArgs?.Invoke(writer);
            var data = writer.ToArray();
            _transport.Broadcast(data, data.Length, mode);
        }

        // ── 客户端输入发送 (服务器权威模型) ────────────────────────

        /// <summary>
        /// 客户端发送玩家输入到服务器 (服务器权威模型)。
        /// 客户端不直接修改权威状态, 而是将输入发送到服务器,
        /// 服务器处理后将权威状态同步回来。
        /// </summary>
        public void SendPlayerInput(uint inputSequence, Vector3 inputDir, float dt, byte[]? extraData = null)
        {
            if (_isServer) return; // 服务器不需要发送输入给自己

            var writer = new BitWriter(128);
            writer.WriteBits(TagPlayerInput, 8);
            writer.WriteUInt(inputSequence);
            writer.WriteFloatQuantized(inputDir.X, -1f, 1f, 12);
            writer.WriteFloatQuantized(inputDir.Y, -1f, 1f, 12);
            writer.WriteFloatQuantized(inputDir.Z, -1f, 1f, 12);
            writer.WriteFloatQuantized(dt, 0f, 0.1f, 10);
            if (extraData != null && extraData.Length > 0)
            {
                writer.WriteBit(true);
                writer.WriteBits((uint)extraData.Length, 16);
                writer.WriteBytes(extraData, extraData.Length);
            }
            else
            {
                writer.WriteBit(false);
            }

            var data = writer.ToArray();
            _transport.Send(Guid.Empty, data, data.Length, DeliveryMode.UnreliableSequenced);
        }

        // ── 时间同步 ──────────────────────────────────────────────

        /// <summary>客户端发送时间同步请求。</summary>
        public void SendTimeSyncRequest()
        {
            if (_isServer) return;
            if (!_clock.ShouldSendSyncRequest()) return;

            uint syncId = _clock.CreateSyncRequest(out float requestTime);
            var writer = new BitWriter(64);
            writer.WriteBits(TagTimeSync, 8);
            writer.WriteUInt(syncId);
            var data = writer.ToArray();
            _transport.Send(Guid.Empty, data, data.Length, DeliveryMode.UnreliableUnordered);
        }

        // ── 主更新循环 ─────────────────────────────────────────────

        /// <summary>每帧驱动复制管线。</summary>
        public void Update(EngineTime time)
        {
            float dt = time.DeltaTime;

            // 更新网络时钟
            _clock.Update(dt);

            // 处理传输层事件
            ProcessTransportEvents();

            // 连接管理 (心跳 / 超时)
            _connMgr.Update(dt);

            // 客户端: 发送时间同步请求
            if (!_isServer)
                SendTimeSyncRequest();

            // 服务器侧: 处理客户端输入 (服务器权威模型)
            if (_isServer)
                ProcessServerInputs();

            // 服务器侧: 按复制速率同步属性
            if (_isServer)
            {
                _accumulator += dt;
                float interval = 1f / _replicationRate;
                while (_accumulator >= interval)
                {
                    _accumulator -= interval;
                    ReplicateTick(_clock.LocalTime);
                }
            }

            // 延迟补偿记录 (服务器)
            if (_isServer && _lagComp.Enabled)
                RecordLagCompensation(_clock.LocalTime);
        }

        /// <summary>单个复制 tick: 相关性筛选 -> Delta 序列化 -> 发送。</summary>
        private void ReplicateTick(float totalTime)
        {
            var all = new List<ReplicatedObject>(_objects.Values);

            // 计算每个对象的相关性优先级
            foreach (var obj in all)
            {
                obj.TimeSinceLastUpdate += 1f / _replicationRate;
                float relevance = RelevanceCalculator.Compute(obj, _relevanceQuery);
                obj.UpdatePriority = relevance * (obj.TimeSinceLastUpdate + 0.1f);
            }

            // 选择本 tick 需同步的对象
            var selected = RelevanceCalculator.SelectForReplication(all, _relevanceQuery, _maxObjectsPerTick);

            foreach (var obj in selected)
            {
                var writer = new BitWriter(512);
                writer.WriteBits(TagReplication, 8);
                writer.WriteInt(obj.NetworkId);
                writer.WriteFloatQuantized(totalTime, 0f, 3600f, 20);
                bool hasData = _delta.SerializeDelta(obj, writer);
                if (hasData)
                {
                    var data = writer.ToArray();
                    _transport.Broadcast(data, data.Length, DeliveryMode.UnreliableUnordered);
                    obj.TimeSinceLastUpdate = 0f;
                    obj.LastUpdateTime = DateTime.Now;
                }
            }
        }

        /// <summary>处理传输层事件。</summary>
        private void ProcessTransportEvents()
        {
            while (_transport.Poll(out var evt))
            {
                switch (evt.Type)
                {
                    case TransportEventType.Connect:
                        break;
                    case TransportEventType.Disconnect:
                        _connMgr.RemoveConnection(evt.ConnectionId);
                        _serverInputs.ClearConnection(evt.ConnectionId);
                        break;
                    case TransportEventType.DataReceive:
                        HandleReceivedData(evt);
                        break;
                }
            }
        }

        /// <summary>处理收到的数据: 按协议标签区分 RPC / 属性更新 / 时间同步 / 玩家输入。</summary>
        private void HandleReceivedData(TransportEvent evt)
        {
            if (evt.Data == null || evt.DataLength == 0) return;
            var reader = new BitReader(evt.Data, evt.DataLength);
            byte tag = (byte)reader.ReadBits(8);

            switch (tag)
            {
                case TagRPC:
                    HandleRPC(reader, evt.ConnectionId);
                    break;
                case TagReplication:
                    if (!_isServer)
                        ApplyReplicatedState(reader);
                    break;
                case TagTimeSync:
                    HandleTimeSync(reader, evt.ConnectionId);
                    break;
                case TagPlayerInput:
                    if (_isServer)
                        HandlePlayerInput(reader, evt.ConnectionId);
                    break;
            }
        }

        /// <summary>处理 RPC 包。</summary>
        private void HandleRPC(BitReader reader, Guid senderConnection)
        {
            string name = reader.ReadString();
            if (_rpcRegistry.TryGetHandler(name, out var handler))
                handler?.Invoke(null, reader, senderConnection);
        }

        /// <summary>客户端应用服务器发来的属性更新。</summary>
        private void ApplyReplicatedState(BitReader reader)
        {
            int networkId = reader.ReadInt();
            float timestamp = reader.ReadFloatQuantized(0f, 3600f, 20);
            if (!_objects.TryGetValue(networkId, out var obj)) return;

            // 读取位掩码并按索引顺序应用脏属性
            reader.AlignToByte();
            int propCount = obj.Properties.Count;
            for (int i = 0; i < propCount; i++)
            {
                if (!reader.ReadBit()) continue;
                ApplyPropertyValue(reader, obj.Properties[i], obj.Target);
            }

            // 推入插值缓冲 (如果是 NetworkEntity)
            if (obj.Target is NetworkEntity ne)
            {
                _interpolator.PushSnapshot(new EntitySnapshot
                {
                    NetworkId = networkId,
                    Timestamp = timestamp,
                    Position = ne.Position,
                    Rotation = ne.Rotation,
                    Velocity = ne.Velocity,
                    Health = ne.Health,
                    State = ne.State
                });
            }
        }

        /// <summary>从位流读取一个属性值并写回目标对象。</summary>
        private void ApplyPropertyValue(BitReader reader, ReplicatedProperty prop, object? target)
        {
            if (target == null || prop.ValueType == null) return;
            object? value = null;

            if (prop.ValueType == typeof(bool)) value = reader.ReadBit();
            else if (prop.ValueType == typeof(int)) value = reader.ReadInt();
            else if (prop.ValueType == typeof(uint)) value = reader.ReadUInt();
            else if (prop.ValueType == typeof(float))
            {
                if (prop.QuantizationBits > 0 && prop.QuantizationBits < 32)
                    value = reader.ReadFloatQuantized(-100000f, 100000f, prop.QuantizationBits);
                else
                    value = BitConverter.UInt32BitsToSingle(reader.ReadUInt());
            }
            else if (prop.ValueType == typeof(Vector3))
            {
                if (prop.QuantizationBits > 0 && prop.QuantizationBits < 32)
                    value = reader.ReadVector3(-100000f, 100000f, prop.QuantizationBits);
                else
                    value = new Vector3(
                        BitConverter.UInt32BitsToSingle(reader.ReadUInt()),
                        BitConverter.UInt32BitsToSingle(reader.ReadUInt()),
                        BitConverter.UInt32BitsToSingle(reader.ReadUInt()));
            }
            else if (prop.ValueType == typeof(Quaternion)) value = reader.ReadQuaternion();
            else if (prop.ValueType == typeof(string)) value = reader.ReadString();

            if (value == null) return;
            if (prop.Member is FieldInfo fi) fi.SetValue(target, value);
            else if (prop.Member is PropertyInfo pi) pi.SetValue(target, value);
        }

        /// <summary>处理时间同步包。</summary>
        private void HandleTimeSync(BitReader reader, Guid senderConnection)
        {
            uint syncId = reader.ReadUInt();

            if (_isServer)
            {
                // 服务器: 返回当前服务器时间
                float serverTime = _clock.HandleSyncRequest(syncId);
                var writer = new BitWriter(64);
                writer.WriteBits(TagTimeSync, 8);
                writer.WriteUInt(syncId);
                writer.WriteFloatQuantized(serverTime, 0f, 3600f, 24);
                var data = writer.ToArray();
                _transport.Send(senderConnection, data, data.Length, DeliveryMode.UnreliableUnordered);
            }
            else
            {
                // 客户端: 处理服务器的时间同步响应
                float serverTime = reader.ReadFloatQuantized(0f, 3600f, 24);
                _clock.ProcessSyncResponse(syncId, serverTime);
            }
        }

        /// <summary>服务器处理客户端输入 (服务器权威模型)。</summary>
        private void HandlePlayerInput(BitReader reader, Guid senderConnection)
        {
            uint sequence = reader.ReadUInt();
            float x = reader.ReadFloatQuantized(-1f, 1f, 12);
            float y = reader.ReadFloatQuantized(-1f, 1f, 12);
            float z = reader.ReadFloatQuantized(-1f, 1f, 12);
            float dt = reader.ReadFloatQuantized(0f, 0.1f, 10);

            byte[]? extraData = null;
            if (reader.ReadBit())
            {
                int len = (int)reader.ReadBits(16);
                extraData = reader.ReadBytes(len);
            }

            var inputDir = new Vector3(x, y, z);
            _serverInputs.PushInput(senderConnection, sequence, inputDir, dt, extraData);
        }

        /// <summary>服务器处理排队的客户端输入。</summary>
        private void ProcessServerInputs()
        {
            // 遍历所有连接的输入队列, 顺序处理
            var connections = _connMgr.Connections;
            foreach (var conn in connections)
            {
                while (_serverInputs.TryDequeue(conn.Id, out uint seq, out Vector3 inputDir, out float dt, out byte[]? extraData))
                {
                    // 服务器在此应用输入到权威状态
                    // (具体应用逻辑由游戏层通过回调实现)
                    Log($"Server processed input seq={seq} from conn={conn.Id}");
                }
            }
        }

        /// <summary>每帧记录延迟补偿快照 (服务器侧)。</summary>
        private void RecordLagCompensation(float time)
        {
            foreach (var kv in _objects)
            {
                if (kv.Value.Target is NetworkEntity ne)
                    _lagComp.RecordSnapshot(kv.Key, time, ne.Position,
                        new Vector3(0.5f, 0.9f, 0.5f), ne.Health);
            }
        }

        // ── 配置 ───────────────────────────────────────────────────

        /// <summary>设置相关性查询参数。</summary>
        public void SetRelevanceQuery(RelevanceQuery query) => _relevanceQuery = query;

        /// <summary>设置插值延迟。</summary>
        public void SetInterpolationDelay(float delay)
        {
            _interpolator.InterpolationDelay = delay;
            _clock.InterpolationDelay = delay;
        }

        /// <summary>获取统计摘要字符串。</summary>
        public string GetStatsSummary()
        {
            var ts = _transport.GetStats();
            return $"Objects: {_objects.Count} | " +
                   $"RPCs: {_rpcRegistry.Count} | " +
                   $"Rooms: {_lobby.RoomCount} | " +
                   $"Sent: {ts.BytesSent / 1024}KB | Recv: {ts.BytesReceived / 1024}KB | " +
                   $"Lost: {ts.PacketsLost} | " +
                   $"RTT: {_clock.SmoothedRtt:F1}ms | " +
                   $"Jitter: {_clock.Jitter:F1}ms | " +
                   $"InterpDelay: {_interpolator.InterpolationDelay * 1000:F0}ms | " +
                   $"SyncCount: {_clock.SyncCount}";
        }

        private void Log(string msg)
        {
            // 可扩展为事件回调
            System.Diagnostics.Debug.WriteLine($"[Replication] {msg}");
        }
    }
}
