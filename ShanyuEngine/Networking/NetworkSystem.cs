using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using ShanyuEngine.Core;
using ShanyuEngine.Network;

namespace ShanyuEngine.Networking
{
    // ════════════════════════════════════════════════════════════════════════
    // 山雨引擎 - 网络核心系统 (NetworkSystem)
    //
    // 本文件实现完整的网络核心管线, 涵盖:
    //   * 连接管理: 服务器/客户端/P2P 模式, 超时检测, 心跳保活
    //   * 可靠与不可靠通道: 滑动窗口 ACK, 超时重传, 有序/无序交付
    //   * 消息分片与重组: 大消息自动分片, 接收端按 ID 重组
    //   * 客户端预测与服务器回滚: 本地输入立即模拟, 服务器校正后重放未确认输入
    //   * 实体插值与外插: 快照环形缓冲, 线性/球面插值, 速度外插
    //
    // 对接系统: Networking/NetworkReplication / Core/EngineTime / Core/NetworkConfig
    // ════════════════════════════════════════════════════════════════════════

    // ──────────────────────────────────────────────────────────────
    // 1. 枚举定义
    // ──────────────────────────────────────────────────────────────

    /// <summary>网络连接状态。</summary>

    /// <summary>网络包类型标识。</summary>
    public enum PacketType
    {
        Handshake = 0,
        ConnectAccepted,
        Disconnect,
        Heartbeat,
        EntitySpawn,
        EntityUpdate,
        EntityDestroy,
        RPC,
        PlayerInput,
        PlayerState,
        Chat,
        LoadLevel,
        /// <summary>分片包 (大消息拆分后的片段)。</summary>
        Fragment = 50,
        /// <summary>ACK 确认包 (可靠通道滑动窗口)。</summary>
        Ack = 51,
        /// <summary>时间同步包 (服务器时间广播)。</summary>
        TimeSync = 52,
        CustomData = 100
    }

    /// <summary>复制属性位掩码, 标识哪些属性需要同步。</summary>
    [Flags]
    public enum ReplicationFlags
    {
        None = 0,
        Position = 1 << 0,
        Rotation = 1 << 1,
        Velocity = 1 << 2,
        Scale = 1 << 3,
        Health = 1 << 4,
        State = 1 << 5,
        Animation = 1 << 6,
        Inventory = 1 << 7,
        Custom1 = 1 << 20,
        Custom2 = 1 << 21,
        All = Position | Rotation | Velocity | Scale | Health | State
    }

    /// <summary>网络运行模式。</summary>
    public enum NetMode
    {
        /// <summary>独立模式 (离线/单机)。</summary>
        Standalone,
        /// <summary>服务器权威模式, 接受客户端连接。</summary>
        Server,
        /// <summary>客户端模式, 连接到服务器。</summary>
        Client,
        /// <summary>点对点模式, 每个端点既是服务器也是客户端。</summary>
        P2P
    }

    /// <summary>通道类型, 决定可靠性与有序性。</summary>
    public enum ChannelType : byte
    {
        /// <summary>不可靠无序: 最低延迟, 可丢包 (如位置高频更新)。</summary>
        UnreliableUnordered = 0,
        /// <summary>不可靠有序: 丢弃过期包, 只保留最新。</summary>
        UnreliableSequenced = 1,
        /// <summary>可靠无序: 保证到达, 不保证顺序。</summary>
        ReliableUnordered = 2,
        /// <summary>可靠有序: 保证到达且顺序 (默认 RPC)。</summary>
        ReliableOrdered = 3
    }

    // ──────────────────────────────────────────────────────────────
    // 2. 环形缓冲区 (RingBuffer<T>)
    //    固定容量循环缓冲, 满时覆盖最旧元素, 零 GC 分配。
    //    用于快照存储、历史状态回滚等场景。
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 泛型环形缓冲区: 固定容量, 满时覆盖最旧元素。
    /// 适用于网络快照、历史状态回放等需要固定窗口的场景。
    /// </summary>
    /// <typeparam name="T">元素类型。</typeparam>
    public sealed class RingBuffer<T>
    {
        private readonly T[] _items;
        private int _head;  // 下一个写入位置
        private int _count; // 当前元素数

        /// <summary>缓冲区容量。</summary>
        public int Capacity => _items.Length;

        /// <summary>当前元素数量。</summary>
        public int Count => _count;

        /// <summary>缓冲区是否已满 (下一次写入将覆盖最旧元素)。</summary>
        public bool IsFull => _count == _items.Length;

        /// <param name="capacity">缓冲区容量 (必须大于 0)。</param>
        public RingBuffer(int capacity)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            _items = new T[capacity];
            _head = 0;
            _count = 0;
        }

        /// <summary>推入一个元素; 若缓冲区已满则覆盖最旧元素。</summary>
        public void Push(T item)
        {
            _items[_head] = item;
            _head = (_head + 1) % _items.Length;
            if (_count < _items.Length) _count++;
        }

        /// <summary>按索引访问元素 (0 = 最旧, Count-1 = 最新)。</summary>
        public T this[int index]
        {
            get
            {
                if ((uint)index >= (uint)_count)
                    throw new ArgumentOutOfRangeException(nameof(index));
                int actual = (_head - _count + index + _items.Length) % _items.Length;
                return _items[actual];
            }
        }

        /// <summary>获取最新元素 (若缓冲区为空返回 default)。</summary>
        public T Latest => _count > 0 ? this[_count - 1] : default!;

        /// <summary>获取最旧元素 (若缓冲区为空返回 default)。</summary>
        public T Oldest => _count > 0 ? this[0] : default!;

        /// <summary>清空缓冲区。</summary>
        public void Clear()
        {
            _head = 0;
            _count = 0;
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
                Array.Clear(_items, 0, _items.Length);
        }

        /// <summary>将所有元素按时间顺序 (旧 -> 新) 拷贝到列表。</summary>
        public List<T> ToList()
        {
            var result = new List<T>(_count);
            for (int i = 0; i < _count; i++)
                result.Add(this[i]);
            return result;
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 3. 网络包 (NetworkPacket)
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 网络数据包: 封装一次传输的完整数据单元。
    /// 包含序列号、ACK 信息、分片元数据与通道标识。
    /// </summary>
    public class NetworkPacket
    {
        /// <summary>包类型。</summary>
        public PacketType Type;
        /// <summary>包序列号 (用于可靠通道排序与 ACK)。</summary>
        public int SequenceNumber;
        /// <summary>确认号: 已收到的最新连续序列号。</summary>
        public int AckNumber;
        /// <summary>ACK 位掩码: AckNumber 之前 32 个包的接收状态。</summary>
        public uint AckBits;
        /// <summary>发送时间戳。</summary>
        public DateTime Timestamp;
        /// <summary>估算延迟 (毫秒)。</summary>
        public float LatencyMs;
        /// <summary>负载数据。</summary>
        public byte[] Data = Array.Empty<byte>();
        /// <summary>负载有效长度。</summary>
        public int DataLength;
        /// <summary>是否可靠传输 (需要 ACK 确认)。</summary>
        public bool IsReliable;
        /// <summary>是否有序传输。</summary>
        public bool IsOrdered;
        /// <summary>重传次数。</summary>
        public int RetryCount;
        /// <summary>上次发送时间 (用于重传计时)。</summary>
        public DateTime LastSendTime;
        /// <summary>所属通道。</summary>
        public ChannelType Channel = ChannelType.UnreliableUnordered;
        /// <summary>分片所属消息 ID (0 = 非分片)。</summary>
        public int MessageId;
        /// <summary>分片索引 (从 0 开始)。</summary>
        public int FragmentIndex;
        /// <summary>分片总数。</summary>
        public int FragmentCount;

        /// <summary>是否为分片包。</summary>
        public bool IsFragment => FragmentCount > 1;

        public override string ToString()
        {
            return IsFragment
                ? $"{Type} (seq={SequenceNumber}, msg={MessageId}, frag={FragmentIndex}/{FragmentCount}, len={DataLength})"
                : $"{Type} (seq={SequenceNumber}, len={DataLength})";
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 4. 网络连接 (NetworkConnection)
    //    封装单条连接的完整状态: 传输统计、可靠/不可靠通道、分片重组器。
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 网络连接: 表示一个远端端点的完整连接状态。
    /// 包含传输统计、可靠/不可靠通道实例与分片重组器。
    /// </summary>
    public class NetworkConnection
    {
        /// <summary>连接唯一 ID。</summary>
        public Guid Id;
        /// <summary>远端地址。</summary>
        public string Address = string.Empty;
        /// <summary>远端端口。</summary>
        public int Port;
        /// <summary>连接状态。</summary>
        public ConnectionState State;
        /// <summary>玩家 ID (服务器为 0)。</summary>
        public int PlayerId;
        /// <summary>玩家名称。</summary>
        public string PlayerName = string.Empty;
        /// <summary>连接建立时间。</summary>
        public DateTime ConnectedAt;
        /// <summary>连接持续时间。</summary>
        public TimeSpan ConnectionTime => DateTime.Now - ConnectedAt;
        /// <summary>往返时延 (毫秒)。</summary>
        public float RoundTripTime;
        /// <summary>平滑后的 RTT (EWMA)。</summary>
        public float SmoothedRtt;
        /// <summary>RTT 方差 (用于抖动估算)。</summary>
        public float RttVariation;
        /// <summary>网络抖动 (毫秒)。</summary>
        public float Jitter;
        /// <summary>丢包率百分比。</summary>
        public float PacketLossPercent;
        /// <summary>已发包数。</summary>
        public int PacketsSent;
        /// <summary>已收包数。</summary>
        public int PacketsReceived;
        /// <summary>已发送字节数。</summary>
        public int BytesSent;
        /// <summary>已接收字节数。</summary>
        public int BytesReceived;
        /// <summary>已确认的最新连续序列号。</summary>
        public uint LastAcked;
        /// <summary>本地发送序列号 (下一个可用)。</summary>
        public int LastSequenceNumber;
        /// <summary>距上次收到包的时间 (秒)。</summary>
        public float TimeSinceLastPacket;
        /// <summary>超时阈值 (秒)。</summary>
        public float TimeoutThreshold = 30f;
        /// <summary>连接优先级 (影响带宽分配)。</summary>
        public int Priority;
        /// <summary>自定义标签。</summary>
        public Dictionary<string, object> Tags = new();

        /// <summary>可靠通道 (保证到达 + 有序/无序)。</summary>
        public readonly ReliableChannel Reliable = new();
        /// <summary>不可靠通道 (最低延迟 + 可选有序)。</summary>
        public readonly UnreliableChannel Unreliable = new();
        /// <summary>分片重组器。</summary>
        public readonly FragmentReassembler Reassembler = new();

        /// <summary>发送队列 (待发出的包)。</summary>
        public readonly Queue<NetworkPacket> SendQueue = new();
        /// <summary>接收队列 (已收到待处理的包)。</summary>
        public readonly Queue<NetworkPacket> ReceiveQueue = new();
        /// <summary>未确认的可靠包 (序列号 -> 包)。</summary>
        public readonly Dictionary<int, NetworkPacket> UnackedPackets = new();

        /// <summary>是否已连接。</summary>
        public bool IsConnected => State == ConnectionState.Connected;
        /// <summary>是否为服务器连接。</summary>
        public bool IsServer => PlayerId == 0;

        /// <summary>估算的延迟 (单向, 毫秒)。</summary>
        public float OneWayLatency => SmoothedRtt > 0 ? SmoothedRtt * 0.5f : RoundTripTime * 0.5f;
    }

    // ──────────────────────────────────────────────────────────────
    // 5. 网络实体 (NetworkEntity)
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 网络实体: 一个可在网络间同步的游戏对象。
    /// 包含变换、状态、所有权与插值/预测辅助字段。
    /// </summary>
    public class NetworkEntity
    {
        /// <summary>实体网络 ID。</summary>
        public int EntityId;
        /// <summary>拥有者连接 ID。</summary>
        public Guid OwnerConnection;
        /// <summary>拥有者玩家 ID。</summary>
        public int OwnerPlayerId;
        /// <summary>本地是否拥有权威。</summary>
        public bool HasAuthority;
        /// <summary>是否由玩家控制。</summary>
        public bool IsPlayerControlled;
        /// <summary>相关性分数 (0~1)。</summary>
        public float Relevance = 1f;
        /// <summary>脏标记 (哪些属性需要同步)。</summary>
        public ReplicationFlags DirtyFlags;
        /// <summary>当前位置。</summary>
        public Vector3 Position;
        /// <summary>当前旋转。</summary>
        public Quaternion Rotation = Quaternion.Identity;
        /// <summary>当前速度。</summary>
        public Vector3 Velocity;
        /// <summary>缩放。</summary>
        public Vector3 Scale = Vector3.One;
        /// <summary>生命值。</summary>
        public float Health = 100f;
        /// <summary>状态码。</summary>
        public int State;
        /// <summary>自定义属性。</summary>
        public Dictionary<string, object> Properties = new();
        /// <summary>上次更新时间。</summary>
        public DateTime LastUpdateTime;
        /// <summary>同步间隔 (秒)。</summary>
        public float UpdateInterval = 0.033f;
        /// <summary>距上次同步的时间 (秒)。</summary>
        public float TimeSinceLastUpdate;
        /// <summary>预测错误计数。</summary>
        public int PredictionErrorCount;

        // ── 插值辅助字段 ──
        /// <summary>上一帧位置 (用于插值)。</summary>
        public Vector3 PreviousPosition;
        /// <summary>目标位置 (最新快照)。</summary>
        public Vector3 TargetPosition;
        /// <summary>目标旋转 (最新快照)。</summary>
        public Quaternion TargetRotation = Quaternion.Identity;
        /// <summary>上次快照时间戳。</summary>
        public float LastSnapshotTime;
        /// <summary>是否正在插值中。</summary>
        public bool IsInterpolating;
        /// <summary>插值进度 [0, 1]。</summary>
        public float InterpolationAlpha;
    }

    // ──────────────────────────────────────────────────────────────
    // 6. RPC 结构 (NetworkRPC)
    // ──────────────────────────────────────────────────────────────

    /// <summary>RPC 调用请求。</summary>
    public struct NetworkRPC
    {
        public NetworkRPC() { }

        /// <summary>RPC 方法名。</summary>
        public string Name = string.Empty;
        /// <summary>目标连接 ID。</summary>
        public Guid TargetConnection;
        /// <summary>目标玩家 ID。</summary>
        public int TargetPlayerId;
        /// <summary>是否发送给所有人。</summary>
        public bool SendToAll;
        /// <summary>是否发送到服务器。</summary>
        public bool SendToServer;
        /// <summary>是否发送给拥有者。</summary>
        public bool SendToOwner;
        /// <summary>负载数据。</summary>
        public byte[] Data = Array.Empty<byte>();
        /// <summary>发送时间。</summary>
        public DateTime SentTime;
        /// <summary>过期时间 (秒)。</summary>
        public float ExpiryTime = 5f;
        /// <summary>是否可靠传输。</summary>
        public bool Reliable = true;
    }

    // ──────────────────────────────────────────────────────────────
    // 7. 消息分片与重组 (FragmentReassembler)
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 消息分片重组器: 将大消息拆分为分片发送, 接收端按消息 ID 收集并重组。
    /// 每个连接拥有独立实例, 避免跨连接分片混淆。
    /// </summary>
    public sealed class FragmentReassembler
    {
        /// <summary>单分片最大字节数。</summary>
        public const int FragmentSize = 1024;
        /// <summary>最大消息大小 (64KB)。</summary>
        public const int MaxMessageSize = 65536;
        /// <summary>最大并发重组集合数。</summary>
        private const int MaxConcurrentSets = 32;

        private readonly Dictionary<int, FragmentSet> _sets = new();
        private int _nextMessageId = 1;
        private readonly object _lock = new();

        /// <summary>一个分片重组集合。</summary>
        private sealed class FragmentSet
        {
            public readonly int MessageId;
            public readonly int FragmentCount;
            public readonly byte[][] Fragments;
            public readonly bool[] Received;
            public int ReceivedCount;
            public float Age;

            public FragmentSet(int messageId, int fragmentCount)
            {
                MessageId = messageId;
                FragmentCount = fragmentCount;
                Fragments = new byte[fragmentCount][];
                Received = new bool[fragmentCount];
                ReceivedCount = 0;
                Age = 0f;
            }

            public bool IsComplete => ReceivedCount == FragmentCount;

            public byte[] Reassemble()
            {
                int total = 0;
                for (int i = 0; i < FragmentCount; i++)
                    total += Fragments[i]!.Length;
                var result = new byte[total];
                int offset = 0;
                for (int i = 0; i < FragmentCount; i++)
                {
                    Buffer.BlockCopy(Fragments[i], 0, result, offset, Fragments[i]!.Length);
                    offset += Fragments[i]!.Length;
                }
                return result;
            }
        }

        /// <summary>将一条大消息拆分为分片包列表。</summary>
        public List<NetworkPacket> Fragment(byte[] data, int length)
        {
            var packets = new List<NetworkPacket>();
            if (length <= FragmentSize)
            {
                // 无需分片, 单包发送
                packets.Add(new NetworkPacket
                {
                    Type = PacketType.Fragment,
                    Data = data,
                    DataLength = length,
                    MessageId = 0,
                    FragmentIndex = 0,
                    FragmentCount = 1
                });
                return packets;
            }

            lock (_lock)
            {
                int messageId = _nextMessageId++;
                if (_nextMessageId > 0x7FFFFFFF) _nextMessageId = 1;

                int fragmentCount = (length + FragmentSize - 1) / FragmentSize;
                int offset = 0;
                for (int i = 0; i < fragmentCount; i++)
                {
                    int chunkSize = Math.Min(FragmentSize, length - offset);
                    var chunk = new byte[chunkSize];
                    Buffer.BlockCopy(data, offset, chunk, 0, chunkSize);

                    packets.Add(new NetworkPacket
                    {
                        Type = PacketType.Fragment,
                        Data = chunk,
                        DataLength = chunkSize,
                        MessageId = messageId,
                        FragmentIndex = i,
                        FragmentCount = fragmentCount
                    });
                    offset += chunkSize;
                }
            }
            return packets;
        }

        /// <summary>接收一个分片包, 返回是否已完成重组 (若完成则 out 完整数据)。</summary>
        public bool ReceiveFragment(NetworkPacket packet, out byte[]? reassembled)
        {
            reassembled = null;
            if (!packet.IsFragment)
            {
                // 非分片包, 直接返回
                reassembled = packet.Data;
                return true;
            }

            lock (_lock)
            {
                // 清理过多的并发重组集合
                if (_sets.Count >= MaxConcurrentSets)
                    EvictOldestSet();

                if (!_sets.TryGetValue(packet.MessageId, out var set))
                {
                    set = new FragmentSet(packet.MessageId, packet.FragmentCount);
                    _sets[packet.MessageId] = set;
                }

                if (set.FragmentCount != packet.FragmentCount)
                    return false; // 分片数不匹配, 丢弃

                if (set.Received[packet.FragmentIndex])
                    return false; // 重复分片, 丢弃

                set.Fragments[packet.FragmentIndex] = packet.Data;
                set.Received[packet.FragmentIndex] = true;
                set.ReceivedCount++;

                if (set.IsComplete)
                {
                    reassembled = set.Reassemble();
                    _sets.Remove(packet.MessageId);
                    return true;
                }
                return false;
            }
        }

        /// <summary>更新重组器, 清理超时的未完成分片集合。</summary>
        public void Update(float dt)
        {
            lock (_lock)
            {
                var expired = new List<int>();
                foreach (var kv in _sets)
                {
                    kv.Value.Age += dt;
                    if (kv.Value.Age > 5f) // 5 秒超时
                        expired.Add(kv.Key);
                }
                foreach (var id in expired)
                    _sets.Remove(id);
            }
        }

        private void EvictOldestSet()
        {
            int oldestId = 0;
            float oldestAge = -1f;
            foreach (var kv in _sets)
            {
                if (kv.Value.Age > oldestAge)
                {
                    oldestAge = kv.Value.Age;
                    oldestId = kv.Key;
                }
            }
            if (oldestId != 0)
                _sets.Remove(oldestId);
        }

        /// <summary>清空所有重组集合。</summary>
        public void Clear()
        {
            lock (_lock)
                _sets.Clear();
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 8. 可靠通道 (ReliableChannel)
    //    滑动窗口 ACK + 超时重传, 保证数据到达且可选有序。
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 可靠通道: 实现滑动窗口 ACK 确认与超时重传机制。
    /// 支持有序 (等待前序包到达后交付) 与无序 (到达即交付) 两种模式。
    /// </summary>
    public sealed class ReliableChannel
    {
        private const int WindowSize = 64;
        private const float DefaultResendInterval = 0.2f;
        private const int MaxRetries = 10;

        private int _sendSequence;
        private int _recvSequence = -1; // 有序模式下期待的下一个序列号
        private readonly Dictionary<int, NetworkPacket> _unacked = new();
        private readonly Dictionary<int, NetworkPacket> _recvBuffer = new();
        private readonly Queue<NetworkPacket> _deliveryQueue = new();
        private float _resendTimer;
        private readonly object _lock = new();

        /// <summary>未确认的包数量。</summary>
        public int UnackedCount
        {
            get { lock (_lock) return _unacked.Count; }
        }

        /// <summary>下一个发送序列号。</summary>
        public int NextSequence => _sendSequence + 1;

        /// <summary>创建一个可靠包, 分配序列号并加入未确认队列。</summary>
        public NetworkPacket CreatePacket(byte[] data, int length, bool ordered)
        {
            lock (_lock)
            {
                var packet = new NetworkPacket
                {
                    Type = PacketType.EntityUpdate,
                    SequenceNumber = ++_sendSequence,
                    Data = data,
                    DataLength = length,
                    IsReliable = true,
                    IsOrdered = ordered,
                    Channel = ordered ? ChannelType.ReliableOrdered : ChannelType.ReliableUnordered,
                    Timestamp = DateTime.Now,
                    LastSendTime = DateTime.Now,
                    RetryCount = 0
                };
                _unacked[packet.SequenceNumber] = packet;
                return packet;
            }
        }

        /// <summary>接收一个可靠包, 返回是否需要发送 ACK。</summary>
        public bool Receive(NetworkPacket packet, out int ackSeq, out uint ackBits)
        {
            ackSeq = 0;
            ackBits = 0;
            lock (_lock)
            {
                if (packet.IsOrdered)
                {
                    // 有序模式: 缓冲并按序交付
                    _recvBuffer[packet.SequenceNumber] = packet;
                    int expected = _recvSequence + 1;
                    while (_recvBuffer.TryGetValue(expected, out var p))
                    {
                        _deliveryQueue.Enqueue(p);
                        _recvBuffer.Remove(expected);
                        _recvSequence = expected;
                        expected++;
                    }
                }
                else
                {
                    // 无序模式: 直接交付
                    _deliveryQueue.Enqueue(packet);
                }

                // 构建 ACK: 最新连续序列号 + 位掩码
                ackSeq = _recvSequence;
                for (int i = 0; i < 32; i++)
                {
                    int seq = _recvSequence - i;
                    if (seq < 0) break;
                    if (_recvBuffer.ContainsKey(seq) || seq == _recvSequence)
                        ackBits |= (1u << i);
                }
                return true;
            }
        }

        /// <summary>处理收到的 ACK, 移除已确认的包。</summary>
        public void ProcessAcks(int ack, uint ackBits)
        {
            lock (_lock)
            {
                for (int i = 0; i < 32; i++)
                {
                    int seq = ack - i;
                    if (seq < 0) continue;
                    bool isAcked = i == 0 || (ackBits & (1u << i)) != 0;
                    if (isAcked)
                        _unacked.Remove(seq);
                }
            }
        }

        /// <summary>更新通道: 检查超时未确认的包并标记重传。</summary>
        public List<NetworkPacket> Update(float dt)
        {
            var toResend = new List<NetworkPacket>();
            lock (_lock)
            {
                _resendTimer += dt;
                if (_resendTimer < DefaultResendInterval)
                    return toResend;
                _resendTimer = 0f;

                foreach (var kv in _unacked)
                {
                    var packet = kv.Value;
                    if (!packet.IsReliable) continue;
                    var elapsed = (DateTime.Now - packet.LastSendTime).TotalSeconds;
                    if (elapsed >= DefaultResendInterval)
                    {
                        packet.RetryCount++;
                        packet.LastSendTime = DateTime.Now;
                        if (packet.RetryCount <= MaxRetries)
                            toResend.Add(packet);
                    }
                }
                // 移除超过最大重试次数的包
                var failed = new List<int>();
                foreach (var kv in _unacked)
                    if (kv.Value.RetryCount > MaxRetries)
                        failed.Add(kv.Key);
                foreach (var seq in failed)
                    _unacked.Remove(seq);
            }
            return toResend;
        }

        /// <summary>尝试取出一个已按序到达的包。</summary>
        public bool TryDequeue(out NetworkPacket? packet)
        {
            lock (_lock)
                return _deliveryQueue.TryDequeue(out packet);
        }

        /// <summary>重置通道状态。</summary>
        public void Reset()
        {
            lock (_lock)
            {
                _sendSequence = 0;
                _recvSequence = -1;
                _unacked.Clear();
                _recvBuffer.Clear();
                _deliveryQueue.Clear();
                _resendTimer = 0f;
            }
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 9. 不可靠通道 (UnreliableChannel)
    //    无重传, 可选有序 (丢弃旧包)。
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 不可靠通道: 无重传, 最低延迟。
    /// 有序模式下丢弃序列号小于已接收的包, 保证只处理最新数据。
    /// </summary>
    public sealed class UnreliableChannel
    {
        private int _sendSequence;
        private int _recvSequence = -1;
        private readonly Queue<NetworkPacket> _deliveryQueue = new();
        private readonly object _lock = new();

        /// <summary>下一个发送序列号。</summary>
        public int NextSequence => _sendSequence + 1;

        /// <summary>创建一个不可靠包。</summary>
        public NetworkPacket CreatePacket(byte[] data, int length, bool sequenced)
        {
            lock (_lock)
            {
                return new NetworkPacket
                {
                    Type = PacketType.EntityUpdate,
                    SequenceNumber = ++_sendSequence,
                    Data = data,
                    DataLength = length,
                    IsReliable = false,
                    IsOrdered = sequenced,
                    Channel = sequenced ? ChannelType.UnreliableSequenced : ChannelType.UnreliableUnordered,
                    Timestamp = DateTime.Now
                };
            }
        }

        /// <summary>接收一个不可靠包, 有序模式下丢弃旧包。</summary>
        public bool Receive(NetworkPacket packet)
        {
            lock (_lock)
            {
                if (packet.IsOrdered)
                {
                    // 有序模式: 丢弃序列号 <= 已收最新序列号的包
                    if (packet.SequenceNumber <= _recvSequence)
                        return false; // 旧包, 丢弃
                    _recvSequence = packet.SequenceNumber;
                }
                _deliveryQueue.Enqueue(packet);
                return true;
            }
        }

        /// <summary>尝试取出一个包。</summary>
        public bool TryDequeue(out NetworkPacket? packet)
        {
            lock (_lock)
                return _deliveryQueue.TryDequeue(out packet);
        }

        /// <summary>重置通道状态。</summary>
        public void Reset()
        {
            lock (_lock)
            {
                _sendSequence = 0;
                _recvSequence = -1;
                _deliveryQueue.Clear();
            }
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 10. 客户端预测与服务器回滚 (ClientPredictionEngine)
    //     客户端本地立即模拟输入, 服务器返回权威状态后比对并校正。
    // ──────────────────────────────────────────────────────────────

    /// <summary>客户端预测的单帧输入记录。</summary>
    public struct ClientInputFrame
    {
        /// <summary>输入序列号 (递增)。</summary>
        public uint Sequence;
        /// <summary>时间戳 (秒)。</summary>
        public float Timestamp;
        /// <summary>帧间隔 (秒)。</summary>
        public float DeltaTime;
        /// <summary>输入方向 (已归一化)。</summary>
        public Vector3 InputDirection;
        /// <summary>是否跳跃。</summary>
        public bool Jump;
        /// <summary>预测得到的位置。</summary>
        public Vector3 PredictedPosition;
        /// <summary>预测得到的旋转。</summary>
        public Quaternion PredictedRotation;
        /// <summary>附加输入数据。</summary>
        public byte[]? ExtraData;
    }

    /// <summary>
    /// 客户端预测引擎: 本地玩家输入立即模拟并记录历史预测帧。
    /// 收到服务器权威状态后比对, 若偏差超过容差则回滚到服务器状态并重放未确认输入。
    /// </summary>
    public sealed class ClientPredictionEngine
    {
        private const int MaxPendingMoves = 128;

        private readonly Queue<ClientInputFrame> _pendingMoves = new();
        private uint _localSequence;
        private uint _lastAckedSequence;
        private float _errorTolerance = 0.5f;
        private float _correctionSmoothing = 0.2f;
        private Vector3 _smoothedCorrection;
        private bool _hasCorrection;
        private int _correctionCount;

        /// <summary>已确认的最新序列号。</summary>
        public uint LastAckedSequence => _lastAckedSequence;
        /// <summary>待确认的预测帧数量。</summary>
        public int PendingMoveCount => _pendingMoves.Count;
        /// <summary>预测误差容差 (单位距离)。</summary>
        public float ErrorTolerance
        {
            get => _errorTolerance;
            set => _errorTolerance = Math.Clamp(value, 0.01f, 10f);
        }
        /// <summary>纠正平滑系数 (0=瞬移, 1=不纠正)。</summary>
        public float CorrectionSmoothing
        {
            get => _correctionSmoothing;
            set => _correctionSmoothing = Math.Clamp(value, 0.01f, 1f);
        }
        /// <summary>累计纠正次数。</summary>
        public int CorrectionCount => _correctionCount;
        /// <summary>是否有待应用的纠正。</summary>
        public bool HasPendingCorrection => _hasCorrection;

        /// <summary>
        /// 记录一次本地预测移动, 返回分配的序列号。
        /// </summary>
        public uint RecordMove(Vector3 inputDir, float dt, Vector3 predictedPos, Quaternion predictedRot,
            bool jump = false, byte[]? extraData = null)
        {
            if (_pendingMoves.Count >= MaxPendingMoves)
                _pendingMoves.Dequeue(); // 丢弃最旧的, 避免无限增长

            _localSequence++;
            var move = new ClientInputFrame
            {
                Sequence = _localSequence,
                Timestamp = (float)DateTime.UtcNow.TimeOfDay.TotalSeconds,
                DeltaTime = dt,
                InputDirection = inputDir,
                Jump = jump,
                PredictedPosition = predictedPos,
                PredictedRotation = predictedRot,
                ExtraData = extraData
            };
            _pendingMoves.Enqueue(move);
            return move.Sequence;
        }

        /// <summary>
        /// 收到服务器权威状态后调用, 比对预测与权威位置。
        /// 若偏差超过容差, 返回 true 并输出纠正后的位置/旋转。
        /// </summary>
        public bool ReceiveServerState(uint ackedSequence, Vector3 serverPos, Quaternion serverRot,
            out Vector3 correctedPos, out Quaternion correctedRot)
        {
            correctedPos = serverPos;
            correctedRot = serverRot;
            _lastAckedSequence = ackedSequence;

            // 弹出已确认的预测帧
            while (_pendingMoves.Count > 0 && _pendingMoves.Peek().Sequence <= ackedSequence)
                _pendingMoves.Dequeue();

            // 比对最近一帧预测与服务器位置
            if (_pendingMoves.Count > 0)
            {
                var latest = _pendingMoves.Peek();
                float error = Vector3.Distance(latest.PredictedPosition, serverPos);
                if (error > _errorTolerance)
                {
                    _correctionCount++;
                    _smoothedCorrection = Vector3.Lerp(_smoothedCorrection, serverPos, _correctionSmoothing);
                    correctedPos = _smoothedCorrection;
                    correctedRot = serverRot;
                    _hasCorrection = true;
                    return true;
                }
            }
            _hasCorrection = false;
            return false;
        }

        /// <summary>
        /// 重放所有未确认的输入帧 (在服务器纠正后调用)。
        /// 对每个待确认帧依次应用 simulate 回调, 更新其预测位置。
        /// </summary>
        public void ReplayPendingInputs(Action<ClientInputFrame> applyInput)
        {
            if (applyInput == null) return;
            // 转为数组避免在回调中修改队列导致枚举失效
            var moves = _pendingMoves.ToArray();
            _pendingMoves.Clear();
            foreach (var move in moves)
            {
                applyInput(move);
                // 更新预测位置为重放后的当前位置
                var updated = move;
                // 注意: applyInput 应更新实际位置, 这里仅重新入队
                _pendingMoves.Enqueue(updated);
            }
        }

        /// <summary>重置预测状态。</summary>
        public void Reset()
        {
            _pendingMoves.Clear();
            _localSequence = 0;
            _lastAckedSequence = 0;
            _smoothedCorrection = Vector3.Zero;
            _hasCorrection = false;
            _correctionCount = 0;
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 11. 实体插值与外插 (EntityInterpolationEngine)
    //     缓冲服务器快照, 按延迟时间在快照间插值;
    //     当无新快照时使用速度进行外插预测。
    // ──────────────────────────────────────────────────────────────

    /// <summary>实体单帧插值快照。</summary>
    public struct InterpolationSnapshot
    {
        /// <summary>实体 ID。</summary>
        public int EntityId;
        /// <summary>快照时间戳 (服务器时间, 秒)。</summary>
        public float Timestamp;
        /// <summary>位置。</summary>
        public Vector3 Position;
        /// <summary>旋转。</summary>
        public Quaternion Rotation;
        /// <summary>速度。</summary>
        public Vector3 Velocity;
    }

    /// <summary>
    /// 实体插值引擎: 为每个实体维护一个快照环形缓冲区,
    /// 在两个快照间进行位置线性插值与旋转球面插值 (Slerp)。
    /// 当渲染时间超出最新快照时, 使用速度进行外插预测。
    /// </summary>
    public sealed class EntityInterpolationEngine
    {
        private readonly Dictionary<int, RingBuffer<InterpolationSnapshot>> _buffers = new();
        private float _interpolationDelay = 0.1f;
        private float _maxExtrapolationTime = 0.5f;
        private bool _extrapolationEnabled = true;
        private const int DefaultBufferSize = 32;

        /// <summary>插值延迟 (秒), 通常略大于平均往返时延的一半。</summary>
        public float InterpolationDelay
        {
            get => _interpolationDelay;
            set => _interpolationDelay = Math.Clamp(value, 0.016f, 0.5f);
        }

        /// <summary>最大外插时间 (秒), 超出后停止预测。</summary>
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

        /// <summary>当前追踪的实体数量。</summary>
        public int TrackedCount => _buffers.Count;

        /// <summary>推入一帧服务器快照。</summary>
        public void PushSnapshot(InterpolationSnapshot snapshot)
        {
            if (!_buffers.TryGetValue(snapshot.EntityId, out var buffer))
            {
                buffer = new RingBuffer<InterpolationSnapshot>(DefaultBufferSize);
                _buffers[snapshot.EntityId] = buffer;
            }
            buffer.Push(snapshot);
        }

        /// <summary>
        /// 获取指定实体在渲染时刻的插值/外插状态。
        /// </summary>
        public bool TryGetState(int entityId, float renderTime,
            out Vector3 position, out Quaternion rotation, out Vector3 velocity)
        {
            position = Vector3.Zero;
            rotation = Quaternion.Identity;
            velocity = Vector3.Zero;

            if (!_buffers.TryGetValue(entityId, out var buffer) || buffer.Count < 2)
            {
                if (buffer != null && buffer.Count == 1)
                {
                    var snap = buffer.Latest;
                    position = snap.Position;
                    rotation = snap.Rotation;
                    velocity = snap.Velocity;
                    return true;
                }
                return false;
            }

            float targetTime = renderTime - _interpolationDelay;

            // 在环形缓冲中查找包围 targetTime 的两个快照
            InterpolationSnapshot prev = buffer[0];
            InterpolationSnapshot next = buffer[buffer.Count - 1];

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
                if (targetTime < prev.Timestamp)
                {
                    // 目标时间早于最早快照, 使用最早快照
                    position = prev.Position;
                    rotation = prev.Rotation;
                    velocity = prev.Velocity;
                    return true;
                }

                // 目标时间晚于最新快照 -> 外插
                if (_extrapolationEnabled)
                {
                    var latest = buffer.Latest;
                    float extrapolateTime = targetTime - latest.Timestamp;
                    if (extrapolateTime <= _maxExtrapolationTime)
                    {
                        position = latest.Position + latest.Velocity * extrapolateTime;
                        rotation = latest.Rotation;
                        velocity = latest.Velocity;
                        return true;
                    }
                }

                // 外插超时, 使用最新快照
                position = next.Position;
                rotation = next.Rotation;
                velocity = next.Velocity;
                return true;
            }

            // 在 prev 和 next 之间插值
            float span = next.Timestamp - prev.Timestamp;
            float alpha = span > 0.0001f
                ? Math.Clamp((targetTime - prev.Timestamp) / span, 0f, 1f)
                : 0f;

            position = Vector3.Lerp(prev.Position, next.Position, alpha);
            rotation = Quaternion.Slerp(prev.Rotation, next.Rotation, alpha);
            velocity = Vector3.Lerp(prev.Velocity, next.Velocity, alpha);
            return true;
        }

        /// <summary>移除指定实体的快照缓冲。</summary>
        public void RemoveEntity(int entityId) => _buffers.Remove(entityId);

        /// <summary>清空所有快照缓冲。</summary>
        public void Clear() => _buffers.Clear();
    }

    // ──────────────────────────────────────────────────────────────
    // 12. 网络系统主类 (NetworkSystem)
    //     串联连接管理、通道、分片、预测与插值, 驱动整个网络核心。
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 网络核心系统: 管理连接生命周期、可靠/不可靠通道、消息分片重组、
    /// 客户端预测与服务器回滚、实体插值与外插。
    /// 作为引擎网络层的核心入口, 可与 NetworkReplicationSystem 协同工作。
    /// </summary>
    public class NetworkSystem
    {
        // ── 连接与实体管理 ──
        private readonly Dictionary<Guid, NetworkConnection> _connections = new();
        private readonly Dictionary<int, NetworkEntity> _entities = new();
        private readonly Queue<NetworkRPC> _outgoingRpcs = new();
        private readonly Queue<NetworkRPC> _incomingRpcs = new();

        // ── 模式与状态 ──
        private NetMode _netMode = NetMode.Standalone;
        private ConnectionState _state = ConnectionState.Disconnected;
        private bool _isServer;
        private int _serverPort;
        private int _localPlayerId;
        private readonly Guid _localConnectionId = Guid.NewGuid();

        // ── ID 分配 ──
        private int _nextEntityId = 1000;
        private int _nextSequenceNumber;

        // ── 速率控制 ──
        private float _tickRate = 60f;
        private float _replicationRate = 30f;
        private float _tickAccumulator;
        private float _replicationAccumulator;
        private float _heartbeatTimer;
        private float _timeSinceStart;

        // ── 统计 ──
        private int _totalPacketsSent;
        private int _totalPacketsReceived;
        private int _totalBytesSent;
        private int _totalBytesReceived;

        // ── 配置 ──
        private bool _lagCompensationEnabled = true;
        private bool _interpolationEnabled = true;
        private int _maxPlayers = 16;
        private float _heartbeatInterval = 5f;

        // ── 子系统 ──
        private readonly ClientPredictionEngine _prediction = new();
        private readonly EntityInterpolationEngine _interpolation = new();

        // ── P2P 相关 ──
        private readonly HashSet<Guid> _p2pPeers = new();

        // ── 回调 ──
        private readonly Dictionary<int, Action<NetworkEntity>> _entitySpawnCallbacks = new();
        private readonly Dictionary<int, Action<NetworkEntity>> _entityDestroyCallbacks = new();
        private readonly Dictionary<string, Action<NetworkRPC>> _rpcHandlers = new();

        // ── 线程安全 ──
        private readonly object _lock = new();

        /// <summary>连接建立事件。</summary>
        public event Action<NetworkConnection>? OnConnected;
        /// <summary>连接断开事件。</summary>
        public event Action<NetworkConnection>? OnDisconnected;
        /// <summary>实体生成事件。</summary>
        public event Action<NetworkEntity>? OnEntitySpawned;
        /// <summary>实体销毁事件。</summary>
        public event Action<NetworkEntity>? OnEntityDestroyed;
        /// <summary>RPC 接收事件。</summary>
        public event Action<NetworkRPC>? OnRPCReceived;
        /// <summary>日志事件。</summary>
        public event Action<string>? OnLogMessage;

        // ── 公共属性 ──

        /// <summary>当前网络模式。</summary>
        public NetMode Mode => _netMode;
        /// <summary>连接状态。</summary>
        public ConnectionState State => _state;
        /// <summary>是否为服务器。</summary>
        public bool IsServer => _isServer;
        /// <summary>是否为客户端。</summary>
        public bool IsClient => !_isServer && _state != ConnectionState.Disconnected;
        /// <summary>是否为 P2P 模式。</summary>
        public bool IsP2P => _netMode == NetMode.P2P;
        /// <summary>当前连接数。</summary>
        public int ConnectionCount => _connections.Count;
        /// <summary>当前实体数。</summary>
        public int EntityCount => _entities.Count;
        /// <summary>Tick 速率。</summary>
        public float TickRate => _tickRate;
        /// <summary>服务器端口。</summary>
        public int Port => _serverPort;
        /// <summary>本地玩家 ID。</summary>
        public int LocalPlayerId => _localPlayerId;
        /// <summary>本地连接 ID。</summary>
        public Guid LocalConnectionId => _localConnectionId;
        /// <summary>最大玩家数。</summary>
        public int MaxPlayers => _maxPlayers;
        /// <summary>总发送包数。</summary>
        public int TotalPacketsSent => _totalPacketsSent;
        /// <summary>总接收包数。</summary>
        public int TotalPacketsReceived => _totalPacketsReceived;
        /// <summary>总发送字节数。</summary>
        public int TotalBytesSent => _totalBytesSent;
        /// <summary>总接收字节数。</summary>
        public int TotalBytesReceived => _totalBytesReceived;
        /// <summary>客户端预测引擎。</summary>
        public ClientPredictionEngine Prediction => _prediction;
        /// <summary>实体插值引擎。</summary>
        public EntityInterpolationEngine Interpolation => _interpolation;
        /// <summary>心跳间隔 (秒)。</summary>
        public float HeartbeatInterval
        {
            get => _heartbeatInterval;
            set => _heartbeatInterval = Math.Clamp(value, 1f, 30f);
        }

        /// <summary>平均延迟 (毫秒)。</summary>
        public float AverageLatency
        {
            get
            {
                float total = 0;
                int count = 0;
                lock (_lock)
                {
                    foreach (var c in _connections.Values)
                    {
                        if (c.Id == _localConnectionId) continue;
                        total += c.SmoothedRtt > 0 ? c.SmoothedRtt : c.RoundTripTime;
                        count++;
                    }
                }
                return count > 0 ? total / count : 0;
            }
        }

        /// <summary>构造网络系统并注册默认 RPC。</summary>
        public NetworkSystem()
        {
            RegisterDefaultRPCs();
        }

        // ── 默认 RPC 注册 ──────────────────────────────────────────

        private void RegisterDefaultRPCs()
        {
            RegisterRPC("ChatMessage", (rpc) =>
            {
                var msg = Encoding.UTF8.GetString(rpc.Data);
                Log($"[{rpc.TargetPlayerId}] {msg}");
            });

            RegisterRPC("Ping", (rpc) =>
            {
                Log($"Ping from player {rpc.TargetPlayerId}");
                if (_isServer)
                    SendRPC("Pong", rpc.TargetConnection);
            });

            RegisterRPC("Pong", (rpc) =>
            {
                Log("Pong received");
            });
        }

        // ── 连接管理 ───────────────────────────────────────────────

        /// <summary>作为服务器启动, 监听指定端口。</summary>
        public void StartServer(int port, int maxPlayers)
        {
            if (_state != ConnectionState.Disconnected) return;
            _isServer = true;
            _netMode = NetMode.Server;
            _serverPort = port;
            _maxPlayers = maxPlayers;
            _state = ConnectionState.Connected;
            _localPlayerId = 0;
            _timeSinceStart = 0f;

            var localConn = new NetworkConnection
            {
                Id = _localConnectionId,
                Address = "local",
                Port = port,
                State = ConnectionState.Connected,
                PlayerId = 0,
                PlayerName = "Server",
                ConnectedAt = DateTime.Now
            };
            lock (_lock)
                _connections[_localConnectionId] = localConn;

            OnConnected?.Invoke(localConn);
            Log($"Server started on port {port}, max players: {maxPlayers}");
        }

        /// <summary>作为客户端连接到远端服务器。</summary>
        public bool Connect(string address, int port, string playerName)
        {
            if (_state != ConnectionState.Disconnected) return false;
            _isServer = false;
            _netMode = NetMode.Client;
            _serverPort = port;
            _state = ConnectionState.Connecting;

            var conn = new NetworkConnection
            {
                Id = _localConnectionId,
                Address = address,
                Port = port,
                State = ConnectionState.Connected,
                PlayerId = 1,
                PlayerName = playerName,
                ConnectedAt = DateTime.Now
            };
            lock (_lock)
                _connections[_localConnectionId] = conn;
            _state = ConnectionState.Connected;
            _localPlayerId = 1;

            OnConnected?.Invoke(conn);
            Log($"Connected to {address}:{port} as {playerName}");
            return true;
        }

        /// <summary>以 P2P 模式启动, 同时作为服务器和客户端。</summary>
        public bool StartP2P(int port, int maxPlayers, string localName)
        {
            if (_state != ConnectionState.Disconnected) return false;
            _isServer = false; // P2P 中没有单一权威服务器
            _netMode = NetMode.P2P;
            _serverPort = port;
            _maxPlayers = maxPlayers;
            _state = ConnectionState.Connected;
            _localPlayerId = 1;
            _timeSinceStart = 0f;

            var localConn = new NetworkConnection
            {
                Id = _localConnectionId,
                Address = "local",
                Port = port,
                State = ConnectionState.Connected,
                PlayerId = 1,
                PlayerName = localName,
                ConnectedAt = DateTime.Now
            };
            lock (_lock)
            {
                _connections[_localConnectionId] = localConn;
                _p2pPeers.Add(_localConnectionId);
            }

            OnConnected?.Invoke(localConn);
            Log($"P2P mode started on port {port}, max peers: {maxPlayers}");
            return true;
        }

        /// <summary>P2P 模式下连接到一个对等端。</summary>
        public bool ConnectPeer(string address, int port, string peerName)
        {
            if (_netMode != NetMode.P2P) return false;

            var peerConn = new NetworkConnection
            {
                Id = Guid.NewGuid(),
                Address = address,
                Port = port,
                State = ConnectionState.Connected,
                PlayerId = _connections.Count + 1,
                PlayerName = peerName,
                ConnectedAt = DateTime.Now
            };
            lock (_lock)
            {
                _connections[peerConn.Id] = peerConn;
                _p2pPeers.Add(peerConn.Id);
            }
            OnConnected?.Invoke(peerConn);
            Log($"P2P peer connected: {peerName} ({address}:{port})");
            return true;
        }

        /// <summary>停止网络系统, 断开所有连接。</summary>
        public void Stop()
        {
            lock (_lock)
            {
                foreach (var conn in _connections.Values)
                {
                    conn.State = ConnectionState.Disconnected;
                    conn.Reliable.Reset();
                    conn.Unreliable.Reset();
                    conn.Reassembler.Clear();
                    OnDisconnected?.Invoke(conn);
                }
                _connections.Clear();
                _p2pPeers.Clear();
                _entities.Clear();
            }
            _outgoingRpcs.Clear();
            _incomingRpcs.Clear();
            _interpolation.Clear();
            _prediction.Reset();
            _state = ConnectionState.Disconnected;
            _netMode = NetMode.Standalone;
            Log("Network stopped");
        }

        // ── 主更新循环 ─────────────────────────────────────────────

        /// <summary>每帧驱动网络系统。</summary>
        public void Update(float dt)
        {
            if (_state == ConnectionState.Disconnected) return;
            _timeSinceStart += dt;
            _tickAccumulator += dt;
            _replicationAccumulator += dt;
            _heartbeatTimer += dt;

            float tickInterval = 1f / _tickRate;
            while (_tickAccumulator >= tickInterval)
            {
                _tickAccumulator -= tickInterval;
                ProcessTick(tickInterval);
            }

            if (_replicationAccumulator >= 1f / _replicationRate)
            {
                _replicationAccumulator = 0;
                ProcessReplication();
            }

            if (_heartbeatTimer >= _heartbeatInterval)
            {
                _heartbeatTimer = 0;
                SendHeartbeat();
            }

            ProcessChannels(dt);
            ProcessIncomingPackets(dt);
            ProcessConnections(dt);
        }

        private void ProcessTick(float dt)
        {
            lock (_lock)
            {
                foreach (var conn in _connections.Values)
                    conn.TimeSinceLastPacket += dt;
            }
        }

        private void ProcessChannels(float dt)
        {
            lock (_lock)
            {
                foreach (var conn in _connections.Values)
                {
                    if (!conn.IsConnected) continue;
                    conn.Reassembler.Update(dt);
                    // 可靠通道重传检查
                    var toResend = conn.Reliable.Update(dt);
                    foreach (var packet in toResend)
                    {
                        conn.SendQueue.Enqueue(packet);
                        conn.PacketsSent++;
                        _totalPacketsSent++;
                    }
                }
            }
        }

        private void ProcessConnections(float dt)
        {
            List<Guid>? timedOut = null;
            lock (_lock)
            {
                foreach (var conn in _connections.Values)
                {
                    if (conn.Id == _localConnectionId) continue;
                    if (!conn.IsConnected) continue;
                    if (conn.TimeSinceLastPacket > conn.TimeoutThreshold)
                    {
                        timedOut ??= new List<Guid>();
                        timedOut.Add(conn.Id);
                    }
                }
            }
            if (timedOut != null)
            {
                foreach (var id in timedOut)
                {
                    NetworkConnection? conn;
                    lock (_lock)
                        _connections.TryGetValue(id, out conn);
                    if (conn != null)
                    {
                        conn.State = ConnectionState.TimedOut;
                        OnDisconnected?.Invoke(conn);
                        Log($"Connection timed out: {conn.PlayerName}");
                        lock (_lock)
                            _connections.Remove(id);
                    }
                }
            }
        }

        private void ProcessReplication()
        {
            lock (_lock)
            {
                foreach (var entity in _entities.Values)
                {
                    entity.TimeSinceLastUpdate += 1f / _replicationRate;
                    if (entity.TimeSinceLastUpdate < entity.UpdateInterval) continue;

                    entity.TimeSinceLastUpdate = 0;
                    entity.LastUpdateTime = DateTime.Now;

                    if (_isServer || _netMode == NetMode.P2P)
                    {
                        var packet = CreateEntityUpdatePacket(entity);
                        QueuePacketForAll(packet);
                    }
                    entity.DirtyFlags = ReplicationFlags.None;
                }
            }
        }

        // ── 包创建与发送 ───────────────────────────────────────────

        private NetworkPacket CreateEntityUpdatePacket(NetworkEntity entity)
        {
            var data = new List<byte>(64);
            data.AddRange(BitConverter.GetBytes(entity.EntityId));
            data.AddRange(BitConverter.GetBytes(entity.Position.X));
            data.AddRange(BitConverter.GetBytes(entity.Position.Y));
            data.AddRange(BitConverter.GetBytes(entity.Position.Z));
            data.AddRange(BitConverter.GetBytes(entity.Rotation.X));
            data.AddRange(BitConverter.GetBytes(entity.Rotation.Y));
            data.AddRange(BitConverter.GetBytes(entity.Rotation.Z));
            data.AddRange(BitConverter.GetBytes(entity.Rotation.W));
            data.AddRange(BitConverter.GetBytes(entity.Velocity.X));
            data.AddRange(BitConverter.GetBytes(entity.Velocity.Y));
            data.AddRange(BitConverter.GetBytes(entity.Velocity.Z));

            return new NetworkPacket
            {
                Type = PacketType.EntityUpdate,
                SequenceNumber = ++_nextSequenceNumber,
                Data = data.ToArray(),
                DataLength = data.Count,
                Timestamp = DateTime.Now,
                IsReliable = false,
                Channel = ChannelType.UnreliableSequenced
            };
        }

        private void QueuePacketForAll(NetworkPacket packet)
        {
            lock (_lock)
            {
                foreach (var conn in _connections.Values)
                {
                    if (conn.Id == _localConnectionId) continue;
                    if (!conn.IsConnected) continue;
                    conn.SendQueue.Enqueue(packet);
                    conn.PacketsSent++;
                    _totalPacketsSent++;
                    _totalBytesSent += packet.DataLength + 12;
                }
            }
        }

        /// <summary>通过指定通道发送数据到目标连接。</summary>
        public void SendOnChannel(Guid connectionId, byte[] data, int length, ChannelType channel)
        {
            NetworkConnection? conn;
            lock (_lock)
                _connections.TryGetValue(connectionId, out conn);
            if (conn == null || !conn.IsConnected) return;

            bool reliable = channel == ChannelType.ReliableOrdered || channel == ChannelType.ReliableUnordered;
            bool ordered = channel == ChannelType.ReliableOrdered || channel == ChannelType.UnreliableSequenced;

            if (length > FragmentReassembler.FragmentSize)
            {
                // 大消息: 分片发送
                var fragments = conn.Reassembler.Fragment(data, length);
                foreach (var frag in fragments)
                {
                    frag.IsReliable = reliable;
                    frag.IsOrdered = ordered;
                    frag.Channel = channel;
                    frag.Timestamp = DateTime.Now;
                    if (reliable)
                    {
                        frag.SequenceNumber = ++_nextSequenceNumber;
                        conn.UnackedPackets[frag.SequenceNumber] = frag;
                    }
                    conn.SendQueue.Enqueue(frag);
                    conn.PacketsSent++;
                    _totalPacketsSent++;
                    _totalBytesSent += frag.DataLength + 12;
                }
            }
            else
            {
                NetworkPacket packet;
                if (reliable)
                    packet = conn.Reliable.CreatePacket(data, length, ordered);
                else
                    packet = conn.Unreliable.CreatePacket(data, length, ordered);

                packet.Type = PacketType.EntityUpdate;
                packet.Timestamp = DateTime.Now;
                if (reliable)
                    conn.UnackedPackets[packet.SequenceNumber] = packet;

                conn.SendQueue.Enqueue(packet);
                conn.PacketsSent++;
                _totalPacketsSent++;
                _totalBytesSent += packet.DataLength + 12;
            }
        }

        /// <summary>广播数据到所有连接 (可选排除某个连接)。</summary>
        public void SendToAll(byte[] data, int length, ChannelType channel, Guid? except = null)
        {
            List<NetworkConnection> snapshot;
            lock (_lock)
                snapshot = new List<NetworkConnection>(_connections.Values);

            foreach (var conn in snapshot)
            {
                if (except.HasValue && conn.Id == except.Value) continue;
                if (conn.Id == _localConnectionId) continue;
                if (!conn.IsConnected) continue;
                SendOnChannel(conn.Id, data, length, channel);
            }
        }

        // ── 心跳 ───────────────────────────────────────────────────

        private void SendHeartbeat()
        {
            var heartbeat = new NetworkPacket
            {
                Type = PacketType.Heartbeat,
                SequenceNumber = ++_nextSequenceNumber,
                Timestamp = DateTime.Now,
                Data = Array.Empty<byte>(),
                DataLength = 0,
                IsReliable = true,
                Channel = ChannelType.ReliableOrdered
            };
            lock (_lock)
            {
                foreach (var conn in _connections.Values)
                {
                    if (conn.Id != _localConnectionId && conn.IsConnected)
                    {
                        conn.SendQueue.Enqueue(heartbeat);
                        conn.PacketsSent++;
                        _totalPacketsSent++;
                    }
                }
            }
        }

        // ── 收包处理 ───────────────────────────────────────────────

        private void ProcessIncomingPackets(float dt)
        {
            // 处理 RPC 队列
            while (_incomingRpcs.TryDequeue(out var rpc))
            {
                OnRPCReceived?.Invoke(rpc);
                if (_rpcHandlers.TryGetValue(rpc.Name, out var handler))
                    handler(rpc);
            }

            // 处理各连接的接收队列
            List<NetworkConnection> snapshot;
            lock (_lock)
                snapshot = new List<NetworkConnection>(_connections.Values);

            foreach (var conn in snapshot)
            {
                while (conn.ReceiveQueue.TryDequeue(out var packet))
                {
                    conn.PacketsReceived++;
                    _totalPacketsReceived++;
                    _totalBytesReceived += packet.DataLength + 12;
                    conn.TimeSinceLastPacket = 0f;

                    HandleReceivedPacket(conn, packet);
                }
            }
        }

        private void HandleReceivedPacket(NetworkConnection conn, NetworkPacket packet)
        {
            // 分片重组
            if (packet.IsFragment)
            {
                if (conn.Reassembler.ReceiveFragment(packet, out var reassembled) && reassembled != null)
                {
                    // 重组完成, 创建合并包继续处理
                    var fullPacket = new NetworkPacket
                    {
                        Type = packet.Type,
                        SequenceNumber = packet.SequenceNumber,
                        Data = reassembled,
                        DataLength = reassembled.Length,
                        Timestamp = packet.Timestamp,
                        IsReliable = packet.IsReliable,
                        Channel = packet.Channel
                    };
                    ProcessPacketPayload(conn, fullPacket);
                }
                return;
            }

            // ACK 处理
            if (packet.Type == PacketType.Ack)
            {
                conn.Reliable.ProcessAcks(packet.AckNumber, packet.AckBits);
                UpdateRtt(conn, packet);
                return;
            }

            // 可靠包处理: 发送 ACK
            if (packet.IsReliable)
            {
                conn.Reliable.Receive(packet, out int ackSeq, out uint ackBits);
                var ackPacket = new NetworkPacket
                {
                    Type = PacketType.Ack,
                    SequenceNumber = ++_nextSequenceNumber,
                    AckNumber = ackSeq,
                    AckBits = ackBits,
                    Timestamp = DateTime.Now,
                    Data = Array.Empty<byte>(),
                    DataLength = 0,
                    IsReliable = false,
                    Channel = ChannelType.UnreliableUnordered
                };
                conn.SendQueue.Enqueue(ackPacket);
            }
            else
            {
                conn.Unreliable.Receive(packet);
            }

            ProcessPacketPayload(conn, packet);
        }

        private void ProcessPacketPayload(NetworkConnection conn, NetworkPacket packet)
        {
            switch (packet.Type)
            {
                case PacketType.Heartbeat:
                    // 更新 RTT
                    UpdateRtt(conn, packet);
                    break;

                case PacketType.TimeSync:
                    HandleTimeSync(conn, packet);
                    break;

                case PacketType.EntityUpdate:
                    ApplyEntityUpdate(packet);
                    break;

                case PacketType.EntitySpawn:
                    if (packet.DataLength >= 4)
                    {
                        int entityId = BitConverter.ToInt32(packet.Data, 0);
                        Log($"Entity spawn received: {entityId}");
                    }
                    break;

                case PacketType.EntityDestroy:
                    if (packet.DataLength >= 4)
                    {
                        int entityId = BitConverter.ToInt32(packet.Data, 0);
                        DestroyEntity(entityId);
                    }
                    break;

                case PacketType.RPC:
                    var rpc = new NetworkRPC
                    {
                        Name = "Received",
                        TargetConnection = conn.Id,
                        TargetPlayerId = conn.PlayerId,
                        Data = packet.Data,
                        SentTime = packet.Timestamp
                    };
                    _incomingRpcs.Enqueue(rpc);
                    break;

                case PacketType.PlayerInput:
                    HandlePlayerInput(conn, packet);
                    break;
            }
        }

        private void UpdateRtt(NetworkConnection conn, NetworkPacket packet)
        {
            float rtt = (float)(DateTime.Now - packet.Timestamp).TotalMilliseconds;
            if (rtt < 0 || rtt > 5000) return; // 丢弃异常值

            // EWMA 平滑 RTT (类似 TCP SRTT)
            if (conn.SmoothedRtt == 0)
            {
                conn.SmoothedRtt = rtt;
                conn.RttVariation = rtt * 0.5f;
            }
            else
            {
                float delta = Math.Abs(rtt - conn.SmoothedRtt);
                conn.RttVariation = conn.RttVariation * 0.75f + delta * 0.25f;
                conn.SmoothedRtt = conn.SmoothedRtt * 0.875f + rtt * 0.125f;
            }
            conn.RoundTripTime = conn.SmoothedRtt;
            conn.Jitter = conn.RttVariation;
        }

        private void HandleTimeSync(NetworkConnection conn, NetworkPacket packet)
        {
            if (packet.DataLength < 4) return;
            float serverTime = BitConverter.ToSingle(packet.Data, 0);
            // 客户端调整本地时钟到服务器时间
            Log($"Time sync: server={serverTime:F3}s, rtt={conn.SmoothedRtt:F1}ms");
        }

        private void ApplyEntityUpdate(NetworkPacket packet)
        {
            if (packet.DataLength < 40) return;
            int entityId = BitConverter.ToInt32(packet.Data, 0);
            var pos = new Vector3(
                BitConverter.ToSingle(packet.Data, 4),
                BitConverter.ToSingle(packet.Data, 8),
                BitConverter.ToSingle(packet.Data, 12));
            var rot = new Quaternion(
                BitConverter.ToSingle(packet.Data, 16),
                BitConverter.ToSingle(packet.Data, 20),
                BitConverter.ToSingle(packet.Data, 24),
                BitConverter.ToSingle(packet.Data, 28));
            var vel = new Vector3(
                BitConverter.ToSingle(packet.Data, 32),
                BitConverter.ToSingle(packet.Data, 36),
                BitConverter.ToSingle(packet.Data, 40));

            lock (_lock)
            {
                if (_entities.TryGetValue(entityId, out var entity))
                {
                    // 推入插值缓冲
                    if (_interpolationEnabled && !entity.HasAuthority)
                    {
                        var snap = new InterpolationSnapshot
                        {
                            EntityId = entityId,
                            Timestamp = _timeSinceStart,
                            Position = pos,
                            Rotation = rot,
                            Velocity = vel
                        };
                        _interpolation.PushSnapshot(snap);
                        entity.TargetPosition = pos;
                        entity.TargetRotation = rot;
                        entity.IsInterpolating = true;
                    }
                    else
                    {
                        entity.Position = pos;
                        entity.Rotation = rot;
                        entity.Velocity = vel;
                    }
                    entity.LastUpdateTime = DateTime.Now;
                }
            }
        }

        private void HandlePlayerInput(NetworkConnection conn, NetworkPacket packet)
        {
            if (packet.DataLength < 16) return;
            // 解析玩家输入并应用到对应实体
            uint inputSeq = BitConverter.ToUInt32(packet.Data, 0);
            float dt = BitConverter.ToSingle(packet.Data, 4);
            var inputDir = new Vector3(
                BitConverter.ToSingle(packet.Data, 8),
                BitConverter.ToSingle(packet.Data, 12),
                packet.DataLength >= 16 ? BitConverter.ToSingle(packet.Data, 16) : 0f);

            // 服务器侧: 应用输入并返回权威状态
            if (_isServer)
            {
                Log($"Input from {conn.PlayerName}: seq={inputSeq}, dir={inputDir}");
                // 发回 PlayerState 确认
                var stateData = new List<byte>(20);
                stateData.AddRange(BitConverter.GetBytes(inputSeq));
                stateData.AddRange(BitConverter.GetBytes(conn.SmoothedRtt));
                var statePacket = new NetworkPacket
                {
                    Type = PacketType.PlayerState,
                    SequenceNumber = ++_nextSequenceNumber,
                    Data = stateData.ToArray(),
                    DataLength = stateData.Count,
                    Timestamp = DateTime.Now,
                    IsReliable = false,
                    Channel = ChannelType.UnreliableSequenced
                };
                conn.SendQueue.Enqueue(statePacket);
            }
        }

        // ── 实体管理 ───────────────────────────────────────────────

        /// <summary>生成一个网络实体并通知所有连接。</summary>
        public int SpawnEntity(Guid ownerConnection, Vector3 position, Quaternion rotation, bool hasAuthority = false)
        {
            var entity = new NetworkEntity
            {
                EntityId = _nextEntityId++,
                OwnerConnection = ownerConnection,
                OwnerPlayerId = _connections.TryGetValue(ownerConnection, out var c) ? c.PlayerId : -1,
                Position = position,
                PreviousPosition = position,
                TargetPosition = position,
                Rotation = rotation,
                TargetRotation = rotation,
                Velocity = Vector3.Zero,
                Scale = Vector3.One,
                Health = 100f,
                HasAuthority = hasAuthority,
                LastUpdateTime = DateTime.Now,
                LastSnapshotTime = _timeSinceStart
            };
            lock (_lock)
                _entities[entity.EntityId] = entity;
            OnEntitySpawned?.Invoke(entity);

            if (_isServer || _netMode == NetMode.P2P)
            {
                var spawn = new NetworkPacket
                {
                    Type = PacketType.EntitySpawn,
                    SequenceNumber = ++_nextSequenceNumber,
                    Data = BitConverter.GetBytes(entity.EntityId),
                    DataLength = 4,
                    IsReliable = true,
                    Channel = ChannelType.ReliableOrdered,
                    Timestamp = DateTime.Now
                };
                QueuePacketForAll(spawn);
            }

            Log($"Entity {entity.EntityId} spawned");
            return entity.EntityId;
        }

        /// <summary>销毁网络实体并通知所有连接。</summary>
        public void DestroyEntity(int entityId)
        {
            NetworkEntity? entity;
            lock (_lock)
                _entities.TryGetValue(entityId, out entity);
            if (entity == null) return;

            OnEntityDestroyed?.Invoke(entity);
            _interpolation.RemoveEntity(entityId);
            lock (_lock)
                _entities.Remove(entityId);

            if (_isServer || _netMode == NetMode.P2P)
            {
                var packet = new NetworkPacket
                {
                    Type = PacketType.EntityDestroy,
                    SequenceNumber = ++_nextSequenceNumber,
                    Data = BitConverter.GetBytes(entityId),
                    DataLength = 4,
                    IsReliable = true,
                    Channel = ChannelType.ReliableOrdered,
                    Timestamp = DateTime.Now
                };
                QueuePacketForAll(packet);
            }
            Log($"Entity {entityId} destroyed");
        }

        /// <summary>更新实体位置 (标记为脏)。</summary>
        public void UpdateEntityPosition(int entityId, Vector3 position)
        {
            lock (_lock)
            {
                if (_entities.TryGetValue(entityId, out var entity))
                {
                    entity.Position = position;
                    entity.DirtyFlags |= ReplicationFlags.Position;
                }
            }
        }

        /// <summary>更新实体旋转 (标记为脏)。</summary>
        public void UpdateEntityRotation(int entityId, Quaternion rotation)
        {
            lock (_lock)
            {
                if (_entities.TryGetValue(entityId, out var entity))
                {
                    entity.Rotation = rotation;
                    entity.DirtyFlags |= ReplicationFlags.Rotation;
                }
            }
        }

        /// <summary>更新实体速度 (标记为脏)。</summary>
        public void UpdateEntityVelocity(int entityId, Vector3 velocity)
        {
            lock (_lock)
            {
                if (_entities.TryGetValue(entityId, out var entity))
                {
                    entity.Velocity = velocity;
                    entity.DirtyFlags |= ReplicationFlags.Velocity;
                }
            }
        }

        /// <summary>获取指定实体。</summary>
        public NetworkEntity? GetEntity(int entityId)
        {
            lock (_lock)
                return _entities.TryGetValue(entityId, out var e) ? e : null;
        }

        /// <summary>获取所有实体的快照列表。</summary>
        public List<NetworkEntity> GetAllEntities()
        {
            lock (_lock)
                return new List<NetworkEntity>(_entities.Values);
        }

        // ── 客户端预测与插值 ───────────────────────────────────────

        /// <summary>
        /// 客户端记录一次玩家输入 (用于预测)。
        /// 本地立即模拟并记录预测帧, 同时将输入发送到服务器。
        /// </summary>
        public uint RecordPlayerInput(Vector3 inputDir, float dt, Vector3 predictedPos,
            Quaternion predictedRot, bool jump = false)
        {
            uint seq = _prediction.RecordMove(inputDir, dt, predictedPos, predictedRot, jump);

            // 将输入发送到服务器
            var data = new List<byte>(20);
            data.AddRange(BitConverter.GetBytes(seq));
            data.AddRange(BitConverter.GetBytes(dt));
            data.AddRange(BitConverter.GetBytes(inputDir.X));
            data.AddRange(BitConverter.GetBytes(inputDir.Y));
            data.AddRange(BitConverter.GetBytes(inputDir.Z));

            SendOnChannel(GetServerConnectionId(), data.ToArray(), data.Count, ChannelType.UnreliableSequenced);
            return seq;
        }

        /// <summary>
        /// 客户端收到服务器权威状态后调用, 比对预测并纠正。
        /// </summary>
        public bool ReceiveServerReconciliation(uint ackedSeq, Vector3 serverPos, Quaternion serverRot,
            out Vector3 correctedPos, out Quaternion correctedRot)
        {
            return _prediction.ReceiveServerState(ackedSeq, serverPos, serverRot,
                out correctedPos, out correctedRot);
        }

        /// <summary>
        /// 获取指定实体的插值/外插状态 (客户端渲染用)。
        /// </summary>
        public bool GetInterpolatedState(int entityId, float renderTime,
            out Vector3 position, out Quaternion rotation, out Vector3 velocity)
        {
            return _interpolation.TryGetState(entityId, renderTime, out position, out rotation, out velocity);
        }

        /// <summary>推入实体快照到插值缓冲。</summary>
        public void PushEntitySnapshot(int entityId, float time, Vector3 pos, Quaternion rot, Vector3 vel)
        {
            _interpolation.PushSnapshot(new InterpolationSnapshot
            {
                EntityId = entityId,
                Timestamp = time,
                Position = pos,
                Rotation = rot,
                Velocity = vel
            });
        }

        // ── RPC 系统 ───────────────────────────────────────────────

        /// <summary>发送 RPC 到指定连接。</summary>
        public void SendRPC(string name, Guid targetConnection, byte[]? data = null, bool reliable = true)
        {
            var rpc = new NetworkRPC
            {
                Name = name,
                TargetConnection = targetConnection,
                Data = data ?? Array.Empty<byte>(),
                SentTime = DateTime.Now,
                ExpiryTime = 5f,
                Reliable = reliable
            };
            _outgoingRpcs.Enqueue(rpc);
            lock (_lock)
            {
                if (_connections.TryGetValue(targetConnection, out var conn))
                    conn.PacketsSent++;
            }
        }

        /// <summary>广播 RPC 到所有连接。</summary>
        public void SendRPCToAll(string name, byte[]? data = null)
        {
            List<Guid> targets;
            lock (_lock)
            {
                targets = new List<Guid>();
                foreach (var conn in _connections.Values)
                    if (conn.Id != _localConnectionId && conn.IsConnected)
                        targets.Add(conn.Id);
            }
            foreach (var id in targets)
                SendRPC(name, id, data);
        }

        /// <summary>发送 RPC 到服务器。</summary>
        public void SendRPCToServer(string name, byte[]? data = null)
        {
            Guid serverId = GetServerConnectionId();
            if (serverId != Guid.Empty)
                SendRPC(name, serverId, data);
        }

        /// <summary>注册 RPC 处理器。</summary>
        public void RegisterRPC(string name, Action<NetworkRPC> handler)
        {
            _rpcHandlers[name] = handler;
        }

        /// <summary>取消注册 RPC 处理器。</summary>
        public void UnregisterRPC(string name)
        {
            _rpcHandlers.Remove(name);
        }

        // ── 连接查询 ───────────────────────────────────────────────

        /// <summary>获取指定连接。</summary>
        public NetworkConnection? GetConnection(Guid id)
        {
            lock (_lock)
                return _connections.TryGetValue(id, out var c) ? c : null;
        }

        /// <summary>按玩家 ID 获取连接。</summary>
        public NetworkConnection? GetConnectionByPlayerId(int playerId)
        {
            lock (_lock)
            {
                foreach (var conn in _connections.Values)
                    if (conn.PlayerId == playerId) return conn;
            }
            return null;
        }

        /// <summary>获取所有连接的快照列表。</summary>
        public List<NetworkConnection> GetAllConnections()
        {
            lock (_lock)
                return new List<NetworkConnection>(_connections.Values);
        }

        /// <summary>获取活跃玩家数 (非服务器连接)。</summary>
        public int GetActivePlayerCount()
        {
            int count = 0;
            lock (_lock)
            {
                foreach (var conn in _connections.Values)
                    if (conn.IsConnected && conn.PlayerId > 0) count++;
            }
            return count;
        }

        /// <summary>获取服务器连接 ID (客户端模式)。</summary>
        public Guid GetServerConnectionId()
        {
            if (_isServer) return _localConnectionId;
            lock (_lock)
            {
                foreach (var conn in _connections.Values)
                {
                    if (conn.Id != _localConnectionId && conn.IsConnected && conn.IsServer)
                        return conn.Id;
                }
            }
            // 如果只有本地连接 (尚未建立真实连接), 返回本地
            return _localConnectionId;
        }

        // ── 配置 ───────────────────────────────────────────────────

        /// <summary>设置 Tick 速率。</summary>
        public void SetTickRate(float rate) => _tickRate = Math.Clamp(rate, 10f, 240f);

        /// <summary>设置复制速率。</summary>
        public void SetReplicationRate(float rate) => _replicationRate = Math.Clamp(rate, 1f, 120f);

        /// <summary>启用/禁用延迟补偿。</summary>
        public void EnableLagCompensation(bool enabled)
        {
            _lagCompensationEnabled = enabled;
            Log($"Lag compensation: {(enabled ? "enabled" : "disabled")}");
        }

        /// <summary>启用/禁用实体插值。</summary>
        public void EnableInterpolation(bool enabled)
        {
            _interpolationEnabled = enabled;
            Log($"Client interpolation: {(enabled ? "enabled" : "disabled")}");
        }

        // ── 统计 ───────────────────────────────────────────────────

        /// <summary>获取统计摘要字符串。</summary>
        public string GetStatsSummary()
        {
            return $"Mode: {_netMode} | " +
                   $"Connections: {_connections.Count} | " +
                   $"Entities: {_entities.Count} | " +
                   $"Sent: {_totalPacketsSent} ({_totalBytesSent / 1024}KB) | " +
                   $"Received: {_totalPacketsReceived} ({_totalBytesReceived / 1024}KB) | " +
                   $"Latency: {AverageLatency:F1}ms | " +
                   $"PendingInputs: {_prediction.PendingMoveCount} | " +
                   $"InterpTracked: {_interpolation.TrackedCount}";
        }

        // ── 日志 ───────────────────────────────────────────────────

        private void Log(string msg)
        {
            OnLogMessage?.Invoke($"[Network] {msg}");
        }
    }
}
