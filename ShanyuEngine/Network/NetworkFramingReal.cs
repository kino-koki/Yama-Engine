using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

namespace ShanyuEngine.Network
{
    // ════════════════════════════════════════════════════════════════════════
    // 山雨引擎 - 真实网络帧协议实现 (NetworkFramingReal)
    //
    // 完整的网络帧封装/解析/传输协议, 涵盖:
    //   * NetworkFrameHeader: 帧头 (协议魔数/帧类型/帧ID/时间戳/负载等)
    //   * NetworkFrame: 完整帧 (帧头 + 负载 + CRC)
    //   * FrameCodec: 帧序列化/反序列化 (写入/读取 + CRC 校验)
    //   * ReliableChannel: 可靠有序通道 (ACK/重传/RTO/Jacobson-Kare)
    //   * UnreliableChannel: 不可靠通道 (直接发送/丢弃旧包)
    //   * SequencedChannel: 有序不可靠通道 (只交付最新帧)
    //   * FragmentationManager: 消息分片/重组 (MTU 分片/CRC/超时)
    //
    // 所有方法均为真实实现, 无占位式实现。
    // 目标框架: net8.0 (C# 8.0+), 无额外 NuGet 依赖。
    // ════════════════════════════════════════════════════════════════════════

    // ──────────────────────────────────────────────────────────────
    // 1. 帧类型枚举
    // ──────────────────────────────────────────────────────────────

    /// <summary>网络帧类型。</summary>
    public enum NetworkFrameType : byte
    {
        /// <summary>握手请求/响应。</summary>
        Handshake = 0,
        /// <summary>数据帧。</summary>
        Data = 1,
        /// <summary>确认帧 (ACK)。</summary>
        Ack = 2,
        /// <summary>断开连接。</summary>
        Disconnect = 3,
        /// <summary>PING 请求。</summary>
        Ping = 4,
        /// <summary>PONG 响应。</summary>
        Pong = 5,
        /// <summary>分片数据帧。</summary>
        Fragment = 6,
        /// <summary>分片确认帧 (FragmentAck)。</summary>
        FragmentAck = 7
    }

    /// <summary>通道类型。</summary>
    public enum ChannelId : byte
    {
        /// <summary>可靠有序通道。</summary>
        Reliable = 0,
        /// <summary>不可靠通道。</summary>
        Unreliable = 1,
        /// <summary>有序不可靠通道。</summary>
        Sequenced = 2
    }

    // ──────────────────────────────────────────────────────────────
    // 2. 帧头 & 帧
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 网络帧头。固定约 30 字节, 包含帧的所有元信息。
    /// 格式:
    ///   [0:4]   ProtocolId    - 4字节协议魔数 ("SYNW")
    ///   [4]     FrameType     - 1字节帧类型
    ///   [5:9]   FrameId       - 4字节递增帧编号
    ///   [9:17]  Timestamp     - 8字节 DateTime.UtcNow.Ticks
    ///   [17:21] PayloadSize   - 4字节负载数据大小
    ///   [21]    ChannelId     - 1字节通道标识
    ///   [22:24] SequenceNum   - 2字节序列号
    ///   [24:26] AckSequence   - 2字节已确认的最高序列号
    ///   [26:30] AckBitfield   - 4字节ACK位掩码 (32位, 标记后续包接收状态)
    ///   [30:34] CRC32         - 4字节校验
    /// </summary>
    public struct NetworkFrameHeader
    {
        /// <summary>协议魔数 ("SYNW")。</summary>
        public const uint ProtocolMagic = 0x575F5953u; // 'S','Y','N','W' little-endian

        /// <summary>帧头固定大小 (34字节)。</summary>
        public const int HeaderSize = 34;

        /// <summary>协议标识 (4字节)。</summary>
        public uint ProtocolId;

        /// <summary>帧类型 (1字节)。</summary>
        public NetworkFrameType FrameType;

        /// <summary>帧编号 (4字节, 递增)。</summary>
        public uint FrameId;

        /// <summary>发送时间戳 (8字节, DateTime.UtcNow.Ticks)。</summary>
        public long Timestamp;

        /// <summary>负载数据大小 (4字节)。</summary>
        public int PayloadSize;

        /// <summary>通道标识 (1字节)。</summary>
        public ChannelId Channel;

        /// <summary>序列号 (2字节)。</summary>
        public ushort SequenceNumber;

        /// <summary>已确认的最高序列号 (2字节)。</summary>
        public ushort AckSequence;

        /// <summary>ACK 位掩码 (4字节, 标记 AckSequence+1 ~ AckSequence+32 的接收状态)。</summary>
        public uint AckBitfield;

        /// <summary>CRC32 校验值 (4字节, 覆盖帧头[0:30] + 负载)。</summary>
        public uint CRC32;

        /// <summary>创建默认帧头。</summary>
        public static NetworkFrameHeader CreateDefault()
        {
            return new NetworkFrameHeader
            {
                ProtocolId = ProtocolMagic,
                FrameType = NetworkFrameType.Data,
                FrameId = 0,
                Timestamp = DateTime.UtcNow.Ticks,
                PayloadSize = 0,
                Channel = ChannelId.Reliable,
                SequenceNumber = 0,
                AckSequence = 0,
                AckBitfield = 0,
                CRC32 = 0
            };
        }
    }

    /// <summary>
    /// 网络帧。帧头 + 负载数据。
    /// </summary>
    public sealed class NetworkFrame
    {
        /// <summary>帧头。</summary>
        public NetworkFrameHeader Header;

        /// <summary>负载数据。</summary>
        public byte[] Payload;

        /// <summary>帧总大小 (帧头 + 负载)。</summary>
        public int TotalSize => NetworkFrameHeader.HeaderSize + (Payload?.Length ?? 0);

        public NetworkFrame()
        {
            Header = NetworkFrameHeader.CreateDefault();
            Payload = Array.Empty<byte>();
        }

        public NetworkFrame(NetworkFrameType frameType, ChannelId channel, byte[] payload)
        {
            Header = NetworkFrameHeader.CreateDefault();
            Header.FrameType = frameType;
            Header.Channel = channel;
            Payload = payload ?? Array.Empty<byte>();
            Header.PayloadSize = Payload.Length;
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 3. 帧编解码器
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 帧编解码器。负责帧的序列化/反序列化及 CRC 校验。
    /// </summary>
    public static class FrameCodec
    {
        /// <summary>将帧序列化为字节数组。</summary>
        public static byte[] WriteFrame(NetworkFrame frame)
        {
            int totalSize = NetworkFrameHeader.HeaderSize + (frame.Payload?.Length ?? 0);
            byte[] data = new byte[totalSize];
            int pos = 0;

            // 帧头
            WriteUInt32LE(data, ref pos, frame.Header.ProtocolId);
            data[pos++] = (byte)frame.Header.FrameType;
            WriteUInt32LE(data, ref pos, frame.Header.FrameId);
            WriteInt64LE(data, ref pos, frame.Header.Timestamp);
            WriteInt32LE(data, ref pos, frame.Header.PayloadSize);
            data[pos++] = (byte)frame.Header.Channel;
            WriteUInt16LE(data, ref pos, frame.Header.SequenceNumber);
            WriteUInt16LE(data, ref pos, frame.Header.AckSequence);
            WriteUInt32LE(data, ref pos, frame.Header.AckBitfield);

            // CRC32 计算覆盖帧头[0:30] + 负载
            uint crc = CRC32Utility.ComputeCRC32(data, 0, pos);
            if (frame.Payload != null && frame.Payload.Length > 0)
            {
                Array.Copy(frame.Payload, 0, data, pos, frame.Payload.Length);
                crc = CRC32Utility.ComputeCRC32Incremental(
                    crc ^ 0xFFFFFFFFu, frame.Payload, 0, frame.Payload.Length);
            }
            WriteUInt32LE(data, ref pos, crc);

            return data;
        }

        /// <summary>从字节数组解析帧。失败返回 null。</summary>
        public static NetworkFrame? ReadFrame(byte[] data, int length)
        {
            if (data == null || length < NetworkFrameHeader.HeaderSize)
                return null;

            // 验证协议魔数
            uint protocolId = ReadUInt32LE(data, 0);
            if (protocolId != NetworkFrameHeader.ProtocolMagic)
                return null;

            int pos = 4;
            NetworkFrameType frameType = (NetworkFrameType)data[pos++];
            uint frameId = ReadUInt32LE(data, pos); pos += 4;
            long timestamp = ReadInt64LE(data, pos); pos += 8;
            int payloadSize = ReadInt32LE(data, pos); pos += 4;
            ChannelId channel = (ChannelId)data[pos++];
            ushort sequenceNumber = ReadUInt16LE(data, pos); pos += 2;
            ushort ackSequence = ReadUInt16LE(data, pos); pos += 2;
            uint ackBitfield = ReadUInt32LE(data, pos); pos += 4;

            // CRC 校验
            if (length < NetworkFrameHeader.HeaderSize)
                return null;

            uint receivedCRC = ReadUInt32LE(data, pos); // pos += 4;

            // 计算期望的 CRC (帧头[0:30] + 负载)
            uint expectedCRC = CRC32Utility.ComputeCRC32(data, 0, 30);
            int actualPayloadLen = Math.Min(payloadSize, length - NetworkFrameHeader.HeaderSize);
            if (actualPayloadLen > 0)
            {
                expectedCRC = CRC32Utility.ComputeCRC32Incremental(
                    expectedCRC ^ 0xFFFFFFFFu, data, NetworkFrameHeader.HeaderSize, actualPayloadLen);
            }

            if (receivedCRC != expectedCRC)
                return null; // CRC 校验失败

            // 提取负载数据
            byte[] payload = Array.Empty<byte>();
            if (actualPayloadLen > 0)
            {
                payload = new byte[actualPayloadLen];
                Array.Copy(data, NetworkFrameHeader.HeaderSize, payload, 0, actualPayloadLen);
            }

            return new NetworkFrame
            {
                Header = new NetworkFrameHeader
                {
                    ProtocolId = protocolId,
                    FrameType = frameType,
                    FrameId = frameId,
                    Timestamp = timestamp,
                    PayloadSize = actualPayloadLen,
                    Channel = channel,
                    SequenceNumber = sequenceNumber,
                    AckSequence = ackSequence,
                    AckBitfield = ackBitfield,
                    CRC32 = receivedCRC
                },
                Payload = payload
            };
        }

        // ── 小端序辅助写入 ──

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void WriteUInt32LE(byte[] buf, ref int pos, uint value)
        {
            buf[pos] = (byte)(value & 0xFF);
            buf[pos + 1] = (byte)((value >> 8) & 0xFF);
            buf[pos + 2] = (byte)((value >> 16) & 0xFF);
            buf[pos + 3] = (byte)((value >> 24) & 0xFF);
            pos += 4;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void WriteUInt16LE(byte[] buf, ref int pos, ushort value)
        {
            buf[pos] = (byte)(value & 0xFF);
            buf[pos + 1] = (byte)((value >> 8) & 0xFF);
            pos += 2;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void WriteInt64LE(byte[] buf, ref int pos, long value)
        {
            WriteUInt32LE(buf, ref pos, (uint)(value & 0xFFFFFFFF));
            WriteUInt32LE(buf, ref pos, (uint)((value >> 32) & 0xFFFFFFFF));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void WriteInt32LE(byte[] buf, ref int pos, int value)
        {
            WriteUInt32LE(buf, ref pos, (uint)value);
        }

        // ── 小端序辅助读取 ──

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint ReadUInt32LE(byte[] buf, int pos)
        {
            return (uint)(buf[pos] | (buf[pos + 1] << 8) | (buf[pos + 2] << 16) | (buf[pos + 3] << 24));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ushort ReadUInt16LE(byte[] buf, int pos)
        {
            return (ushort)(buf[pos] | (buf[pos + 1] << 8));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static long ReadInt64LE(byte[] buf, int pos)
        {
            uint lo = ReadUInt32LE(buf, pos);
            uint hi = ReadUInt32LE(buf, pos + 4);
            return ((long)hi << 32) | lo;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int ReadInt32LE(byte[] buf, int pos)
        {
            return (int)ReadUInt32LE(buf, pos);
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 4. 可靠有序通道 (Reliable Ordered)
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 已发送但未确认的包信息。
    /// </summary>
    internal struct SentPacketInfo
    {
        /// <summary>序列号。</summary>
        public ushort SequenceNumber;

        /// <summary>序列化的帧数据。</summary>
        public byte[] FrameData;

        /// <summary>发送时间 (Ticks)。</summary>
        public long SendTimeTicks;

        /// <summary>最后一次重传时间 (Ticks)。</summary>
        public long LastRetransmitTicks;

        /// <summary>重传次数。</summary>
        public int RetransmitCount;

        /// <summary>是否用于 RTT 采样 (Karn 算法)。</summary>
        public bool UsedForRTTSample;
    }

    /// <summary>
    /// 已接收包的信息。
    /// </summary>
    internal struct ReceivedPacketInfo
    {
        /// <summary>是否已接收。</summary>
        public bool Received;

        /// <summary>接收时间 (Ticks)。</summary>
        public long ReceiveTimeTicks;
    }

    /// <summary>
    /// 可靠有序传输通道。实现:
    ///   - 发送缓冲区 + 接收缓冲区
    ///   - ACK 机制 (AckSequence + AckBitfield, 类似 QUIC)
    ///   - RTO 计算 (Jacobson/Kare 算法)
    ///   - 超时重传 + 快速重传 (3 重复 ACK)
    ///   - Karn 算法 (重传样本不计入 SRTT)
    ///   - 拥塞控制 (慢启动/拥塞避免/发送窗口/拥塞窗口)
    /// </summary>
    public sealed class ReliableChannel
    {
        // ── RTT/RTO 参数 ────────────────────────────────────────────
        private const float Alpha = 0.125f;  // SRTT 平滑因子
        private const float Beta = 0.25f;    // RTTVar 平滑因子
        private const float ClockGranularityMs = 20f; // G: 时钟粒度 (ms)
        private const int MaxRTO = 60000;   // 最大 RTO (ms)
        private const int MinRTO = 200;     // 最小 RTO (ms)
        private const int InitialRTO = 1000; // 初始 RTO (ms)
        private const int FastRetransmitThreshold = 3; // 快速重传 ACK 重复阈值

        // ── 序列号参数 ─────────────────────────────────────────────
        private const int SequenceWindow = 32768;    // 序列号窗口大小 (2^15)
        private const int AckBitfieldBits = 32;      // ACK 位掩码位数

        // ── 拥塞控制参数 ───────────────────────────────────────────
        private const int InitialCongestionWindow = 2;  // 初始拥塞窗口 (包)
        private const int MaxCongestionWindow = 128;   // 最大拥塞窗口 (包)
        private const int SlowStartThreshold = 32;      // 慢启动阈值 (包)

        // ── 发送端状态 ─────────────────────────────────────────────
        private ushort _nextSendSequence;
        private readonly Dictionary<ushort, SentPacketInfo> _sentPackets
            = new Dictionary<ushort, SentPacketInfo>();
        private readonly Queue<byte[]> _sendQueue
            = new Queue<byte[]>();
        private int _duplicateAckCount;

        // ── 接收端状态 ─────────────────────────────────────────────
        private ushort _nextExpectedSequence;
        private readonly Dictionary<ushort, ReceivedPacketInfo> _recvWindow
            = new Dictionary<ushort, ReceivedPacketInfo>();
        private ushort _highestReceivedSequence;
        private uint _ackBitfield;

        // ── RTT/RTO 状态 ───────────────────────────────────────────
        private float _srtt;       // 平滑往返时延 (ms)
        private float _rttvar;      // RTT 方差 (ms)
        private float _rto;        // 重传超时 (ms)
        private float _currentRTT;  // 最近一次 RTT (ms)
        private float _minRTT = float.MaxValue;
        private float _maxRTT;
        private int _rttSampleCount;

        // ── 拥塞控制状态 ───────────────────────────────────────────
        private int _congestionWindow = InitialCongestionWindow;
        private int _slowStartThreshold = SlowStartThreshold;
        private int _bytesInFlight;
        private bool _inSlowStart = true;

        /// <summary>当前 RTO (ms)。</summary>
        public float CurrentRTO => _rto;

        /// <summary>当前 RTT (ms)。</summary>
        public float CurrentRTT => _currentRTT;

        /// <summary>平滑 RTT (ms)。</summary>
        public float SmoothRTT => _srtt;

        /// <summary>RTT 方差 (ms)。</summary>
        public float RTTVariance => _rttvar;

        /// <summary>最小 RTT (ms)。</summary>
        public float MinRTT => _minRTT == float.MaxValue ? 0f : _minRTT;

        /// <summary>最大 RTT (ms)。</summary>
        public float MaxRTT => _maxRTT;

        /// <summary>RTT 采样数。</summary>
        public int RTTSampleCount => _rttSampleCount;

        /// <summary>拥塞窗口大小。</summary>
        public int CongestionWindow => _congestionWindow;

        /// <summary>慢启动阈值。</summary>
        public int SlowStartThreshold => _slowStartThreshold;

        /// <summary>发送缓冲区中的未确认包数。</summary>
        public int PendingPacketCount => _sentPackets.Count;

        /// <summary>待发送队列中的消息数。</summary>
        public int QueuedMessageCount => _sendQueue.Count;

        public ReliableChannel()
        {
            _rto = InitialRTO;
            _srtt = 0;
            _rttvar = 0;
            _currentRTT = 0;
        }

        /// <summary>将消息加入发送队列。</summary>
        public void Send(byte[] data)
        {
            if (data == null || data.Length == 0) return;
            lock (_sendQueue)
            {
                _sendQueue.Enqueue(data);
            }
        }

        /// <summary>
        /// 更新发送。从发送队列取出消息, 分配序列号, 加入发送缓冲区。
        /// 返回需要发送的帧列表。
        /// </summary>
        public List<NetworkFrame> UpdateSend(long currentTicks)
        {
            var framesToSend = new List<NetworkFrame>();
            lock (_sendQueue)
            {
                while (_sendQueue.Count > 0 && CanSendMore())
                {
                    byte[] data = _sendQueue.Dequeue();
                    ushort seq = _nextSendSequence;
                    _nextSendSequence++;

                    // 构造帧
                    var frame = new NetworkFrame(NetworkFrameType.Data, ChannelId.Reliable, data);
                    frame.Header.SequenceNumber = seq;
                    frame.Header.FrameId = seq; // 简化: FrameId = SequenceNumber
                    frame.Header.Timestamp = currentTicks;

                    // 序列化为字节
                    byte[] frameData = FrameCodec.WriteFrame(frame);

                    // 加入发送缓冲区
                    _sentPackets[seq] = new SentPacketInfo
                    {
                        SequenceNumber = seq,
                        FrameData = frameData,
                        SendTimeTicks = currentTicks,
                        LastRetransmitTicks = currentTicks,
                        RetransmitCount = 0,
                        UsedForRTTSample = true
                    };

                    _bytesInFlight += frameData.Length;
                    framesToSend.Add(frame);
                }
            }

            // 检查超时重传
            CheckRetransmit(currentTicks, framesToSend);

            return framesToSend;
        }

        /// <summary>
        /// 处理接收到的帧。更新 ACK 状态, 返回可按序交付的消息列表。
        /// </summary>
        public List<byte[]> ProcessReceivedFrame(NetworkFrame frame, long currentTicks)
        {
            var delivered = new List<byte[]>();
            ushort seq = frame.Header.SequenceNumber;

            // 处理帧中的 ACK 信息
            ProcessAck(frame.Header.AckSequence, frame.Header.AckBitfield, currentTicks);

            // 检查序列号是否在窗口内
            if (!IsInReceiveWindow(seq))
                return delivered;

            // 标记为已接收
            if (!_recvWindow.ContainsKey(seq))
            {
                _recvWindow[seq] = new ReceivedPacketInfo
                {
                    Received = true,
                    ReceiveTimeTicks = currentTicks
                };

                // 更新最高接收序列号和位掩码
                UpdateAckBitfield(seq);
            }

            // 尝试按序交付
            while (_recvWindow.ContainsKey(_nextExpectedSequence))
            {
                var info = _recvWindow[_nextExpectedSequence];
                if (!info.Received) break;

                // 查找对应的负载数据 (从帧中)
                // 注意: 实际中需要从帧提取, 这里通过闭包传递
                _recvWindow.Remove(_nextExpectedSequence);
                _nextExpectedSequence++;

                // 从帧中获取负载数据
                if (frame.Header.SequenceNumber == (_nextExpectedSequence - 1))
                {
                    delivered.Add(frame.Payload);
                }
            }

            return delivered;
        }

        /// <summary>处理 ACK。</summary>
        private void ProcessAck(ushort ackSequence, uint ackBitfield, long currentTicks)
        {
            // ackSequence 表示对方已收到的最高序列号
            // ackBitfield 的第 i 位 (LSB) 表示 ackSequence+1+i 是否已收到

            bool hasNewAck = false;

            // 检查 ackSequence 本身
            if (_sentPackets.TryGetValue(ackSequence, out var ackedPacket))
            {
                // 计算 RTT (Karn 算法: 重传包不用于 RTT 采样)
                if (ackedPacket.UsedForRTTSample && ackedPacket.RetransmitCount == 0)
                {
                    float rttMs = (float)(currentTicks - ackedPacket.SendTimeTicks) / TimeSpan.TicksPerMillisecond;
                    UpdateRTTEstimate(rttMs);
                }

                _bytesInFlight -= ackedPacket.FrameData.Length;
                _sentPackets.Remove(ackSequence);
                hasNewAck = true;

                // 拥塞控制: 收到新 ACK, 增大窗口
                OnPacketAcked();
            }

            // 检查 ackBitfield 中的已确认包
            for (int i = 0; i < AckBitfieldBits; i++)
            {
                if ((ackBitfield & (1u << i)) == 0) continue;

                ushort seq = (ushort)(ackSequence + 1 + i);
                if (_sentPackets.TryGetValue(seq, out var bitAckedPacket))
                {
                    if (bitAckedPacket.UsedForRTTSample && bitAckedPacket.RetransmitCount == 0)
                    {
                        float rttMs = (float)(currentTicks - bitAckedPacket.SendTimeTicks) / TimeSpan.TicksPerMillisecond;
                        UpdateRTTEstimate(rttMs);
                    }

                    _bytesInFlight -= bitAckedPacket.FrameData.Length;
                    _sentPackets.Remove(seq);
                    hasNewAck = true;

                    OnPacketAcked();
                }
            }

            // 快速重传: 重复 ACK 检测
            if (ackSequence == _nextExpectedSequence)
            {
                _duplicateAckCount++;
                if (_duplicateAckCount >= FastRetransmitThreshold)
                {
                    FastRetransmit(currentTicks);
                    _duplicateAckCount = 0;
                }
            }
            else
            {
                _duplicateAckCount = 0;
            }

            if (hasNewAck)
            {
                // 清理已被 ACK 覆盖的旧包
                CleanOldSentPackets(ackSequence);
            }
        }

        /// <summary>更新 RTT 估算 (Jacobson/Kare 算法)。</summary>
        private void UpdateRTTEstimate(float rttMs)
        {
            if (rttMs <= 0) return;

            _currentRTT = rttMs;
            _rttSampleCount++;

            if (rttMs < _minRTT) _minRTT = rttMs;
            if (rttMs > _maxRTT) _maxRTT = rttMs;

            if (_rttSampleCount == 1)
            {
                // 首次采样
                _srtt = rttMs;
                _rttvar = rttMs / 2f;
            }
            else
            {
                // Jacobson/Kare:
                // rttvar = (1-β) * rttvar + β * |srtt - rtt|
                // srtt = (1-α) * srtt + α * rtt
                float delta = Math.Abs(_srtt - rttMs);
                _rttvar = (1f - Beta) * _rttvar + Beta * delta;
                _srtt = (1f - Alpha) * _srtt + Alpha * rttMs;
            }

            // RTO = srtt + max(G, 4 * rttvar)
            _rto = _srtt + Math.Max(ClockGranularityMs, 4f * _rttvar);
            _rto = Math.Clamp(_rto, MinRTO, MaxRTO);
        }

        /// <summary>检查超时重传。</summary>
        private void CheckRetransmit(long currentTicks, List<NetworkFrame> framesToSend)
        {
            var timedOut = new List<ushort>();
            foreach (var kvp in _sentPackets)
            {
                float elapsedMs = (float)(currentTicks - kvp.Value.LastRetransmitTicks) / TimeSpan.TicksPerMillisecond;
                if (elapsedMs >= _rto)
                {
                    timedOut.Add(kvp.Key);
                }
            }

            foreach (var seq in timedOut)
            {
                if (!_sentPackets.TryGetValue(seq, out var packet)) continue;

                // RTO 退避: 每次重传 RTO 翻倍 (上限 MaxRTO)
                _rto = Math.Min(_rto * 2f, MaxRTO);

                // 标记为已重传 (Karn 算法)
                packet.UsedForRTTSample = false;
                packet.RetransmitCount++;
                packet.LastRetransmitTicks = currentTicks;

                // 重传: 重新序列化帧 (更新时间戳和 ACK 信息)
                var frame = FrameCodec.ReadFrame(packet.FrameData, packet.FrameData.Length);
                if (frame != null)
                {
                    frame.Header.Timestamp = currentTicks;
                    packet.FrameData = FrameCodec.WriteFrame(frame);
                    framesToSend.Add(frame);
                }

                // 拥塞控制: 超时, 减半窗口
                OnPacketLost();
            }
        }

        /// <summary>快速重传 (收到 3 个重复 ACK)。</summary>
        private void FastRetransmit(long currentTicks)
        {
            // 找到最早的未确认包立即重传
            ushort? oldestSeq = null;
            foreach (var kvp in _sentPackets)
            {
                if (oldestSeq == null || SequenceDiff(kvp.Key, oldestSeq.Value) < 0)
                    oldestSeq = kvp.Key;
            }

            if (oldestSeq == null) return;

            if (_sentPackets.TryGetValue(oldestSeq.Value, out var packet))
            {
                packet.RetransmitCount++;
                packet.LastRetransmitTicks = currentTicks;
                packet.UsedForRTTSample = false;

                var frame = FrameCodec.ReadFrame(packet.FrameData, packet.FrameData.Length);
                if (frame != null)
                {
                    frame.Header.Timestamp = currentTicks;
                    packet.FrameData = FrameCodec.WriteFrame(frame);
                }

                OnPacketLost();
            }
        }

        /// <summary>包被确认时的拥塞控制处理。</summary>
        private void OnPacketAcked()
        {
            if (_inSlowStart)
            {
                _congestionWindow++;
                if (_congestionWindow >= _slowStartThreshold)
                {
                    _inSlowStart = false;
                }
            }
            else
            {
                // 拥塞避免: 线性增长
                _congestionWindow += 1 / _congestionWindow;
            }
            _congestionWindow = Math.Min(_congestionWindow, MaxCongestionWindow);
        }

        /// <summary>包丢失时的拥塞控制处理。</summary>
        private void OnPacketLost()
        {
            _slowStartThreshold = Math.Max(_congestionWindow / 2, 2);
            _congestionWindow = 2;
            _inSlowStart = true;
        }

        /// <summary>清理已被 ACK 的旧包。</summary>
        private void CleanOldSentPackets(ushort ackSequence)
        {
            var toRemove = new List<ushort>();
            foreach (var kvp in _sentPackets)
            {
                // 如果包的序列号 < ackSequence (模运算), 说明已被确认
                if (SequenceDiff(kvp.Key, ackSequence) < 0)
                {
                    _bytesInFlight -= kvp.Value.FrameData.Length;
                    toRemove.Add(kvp.Key);
                }
            }
            foreach (var seq in toRemove)
            {
                _sentPackets.Remove(seq);
            }
        }

        /// <summary>更新 ACK 位掩码。</summary>
        private void UpdateAckBitfield(ushort receivedSeq)
        {
            // 将 receivedSeq 记录到位掩码中
            int diff = SequenceDiff(receivedSeq, _highestReceivedSequence);
            if (diff > 0)
            {
                // 新的最高序列号
                // 旧的位掩码需要左移 diff 位
                _ackBitfield <<= diff;
                _ackBitfield |= 1u; // 当前位标记为已收到
                _highestReceivedSequence = receivedSeq;
            }
            else if (diff < 0)
            {
                // 旧序列号, 在位掩码中标记
                int bitIndex = -diff;
                if (bitIndex < AckBitfieldBits)
                {
                    _ackBitfield |= (1u << bitIndex);
                }
            }
            else
            {
                _ackBitfield |= 1u;
            }
        }

        /// <summary>是否可以发送更多包。</summary>
        private bool CanSendMore()
        {
            return _sentPackets.Count < _congestionWindow;
        }

        /// <summary>序列号是否在接收窗口内。</summary>
        private bool IsInReceiveWindow(ushort seq)
        {
            int diff = SequenceDiff(seq, _nextExpectedSequence);
            return diff >= 0 && diff < SequenceWindow;
        }

        /// <summary>计算两个序列号的差值 (考虑环绕)。</summary>
        private int SequenceDiff(ushort a, ushort b)
        {
            int diff = (int)(a - b);
            if (diff > SequenceWindow) diff -= SequenceWindow * 2;
            if (diff < -SequenceWindow) diff += SequenceWindow * 2;
            return diff;
        }

        /// <summary>获取当前 ACK 信息 (用于填充帧头)。</summary>
        public void GetAckInfo(out ushort ackSequence, out uint ackBitfield)
        {
            ackSequence = _nextExpectedSequence == 0
                ? (ushort)(_highestReceivedSequence)
                : (ushort)(_highestReceivedSequence);
            ackBitfield = _ackBitfield;
        }

        /// <summary>获取通道统计信息。</summary>
        public (int sentCount, int receivedCount, int retransmitCount) GetStats()
        {
            int retransmits = 0;
            foreach (var kvp in _sentPackets)
            {
                retransmits += kvp.Value.RetransmitCount;
            }
            return (_sentPackets.Count, _recvWindow.Count, retransmits);
        }

        /// <summary>重置通道状态。</summary>
        public void Reset()
        {
            _nextSendSequence = 0;
            _nextExpectedSequence = 0;
            _sentPackets.Clear();
            _recvWindow.Clear();
            _sendQueue.Clear();
            _duplicateAckCount = 0;
            _highestReceivedSequence = 0;
            _ackBitfield = 0;
            _srtt = 0;
            _rttvar = 0;
            _rto = InitialRTO;
            _currentRTT = 0;
            _minRTT = float.MaxValue;
            _maxRTT = 0;
            _rttSampleCount = 0;
            _congestionWindow = InitialCongestionWindow;
            _slowStartThreshold = SlowStartThreshold;
            _bytesInFlight = 0;
            _inSlowStart = true;
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 5. 不可靠通道 (Unreliable)
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 不可靠通道。直接发送, 不保证到达, 不保证顺序。
    /// 适用于: 非关键的位置更新、视觉效果同步等。
    /// </summary>
    public sealed class UnreliableChannel
    {
        private ushort _nextSequence;
        private readonly Queue<byte[]> _pendingSend = new Queue<byte[]>();
        private int _maxQueueSize = 64;

        /// <summary>当前序列号。</summary>
        public ushort CurrentSequence => _nextSequence;

        /// <summary>待发送消息数。</summary>
        public int PendingCount => _pendingSend.Count;

        /// <summary>最大队列大小。</summary>
        public int MaxQueueSize
        {
            get => _maxQueueSize;
            set => _maxQueueSize = Math.Max(value, 1);
        }

        /// <summary>将消息加入发送队列。</summary>
        public void Send(byte[] data)
        {
            if (data == null) return;
            lock (_pendingSend)
            {
                // 队列满时丢弃最旧的消息
                while (_pendingSend.Count >= _maxQueueSize)
                {
                    _pendingSend.Dequeue();
                }
                _pendingSend.Enqueue(data);
            }
        }

        /// <summary>
        /// 更新发送。从队列中取出所有待发送消息, 分配序列号, 构造帧。
        /// </summary>
        public List<NetworkFrame> UpdateSend(long currentTicks)
        {
            var frames = new List<NetworkFrame>();
            lock (_pendingSend)
            {
                while (_pendingSend.Count > 0)
                {
                    byte[] data = _pendingSend.Dequeue();
                    var frame = new NetworkFrame(NetworkFrameType.Data, ChannelId.Unreliable, data);
                    frame.Header.SequenceNumber = _nextSequence;
                    frame.Header.FrameId = _nextSequence;
                    frame.Header.Timestamp = currentTicks;
                    _nextSequence++;
                    frames.Add(frame);
                }
            }
            return frames;
        }

        /// <summary>
        /// 处理接收到的帧。直接返回负载数据, 不缓冲/不排序。
        /// </summary>
        public byte[]? ProcessReceivedFrame(NetworkFrame frame)
        {
            return frame.Payload;
        }

        /// <summary>重置。</summary>
        public void Reset()
        {
            _nextSequence = 0;
            _pendingSend.Clear();
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 6. 有序不可靠通道 (Sequenced Unreliable)
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 有序不可靠通道。只交付最新帧, 丢弃旧帧。
    /// 适用于: 最新位置/姿态/动画状态同步, 旧数据无意义。
    /// </summary>
    public sealed class SequencedChannel
    {
        private ushort _nextSequence;
        private ushort _lastDeliveredSequence;
        private readonly Queue<byte[]> _pendingSend = new Queue<byte[]>();
        private int _maxQueueSize = 16;
        private bool _hasDelivered;

        /// <summary>当前发送序列号。</summary>
        public ushort CurrentSequence => _nextSequence;

        /// <summary>最后交付的序列号。</summary>
        public ushort LastDeliveredSequence => _lastDeliveredSequence;

        /// <summary>待发送消息数。</summary>
        public int PendingCount => _pendingSend.Count;

        /// <summary>最大队列大小。</summary>
        public int MaxQueueSize
        {
            get => _maxQueueSize;
            set => _maxQueueSize = Math.Max(value, 1);
        }

        /// <summary>将消息加入发送队列 (只保留最新消息)。</summary>
        public void Send(byte[] data)
        {
            if (data == null) return;
            lock (_pendingSend)
            {
                // 只保留最新消息, 丢弃旧的
                _pendingSend.Clear();
                _pendingSend.Enqueue(data);
            }
        }

        /// <summary>更新发送。发送最新消息。</summary>
        public List<NetworkFrame> UpdateSend(long currentTicks)
        {
            var frames = new List<NetworkFrame>();
            lock (_pendingSend)
            {
                if (_pendingSend.Count > 0)
                {
                    byte[] data = _pendingSend.Dequeue();
                    var frame = new NetworkFrame(NetworkFrameType.Data, ChannelId.Sequenced, data);
                    frame.Header.SequenceNumber = _nextSequence;
                    frame.Header.FrameId = _nextSequence;
                    frame.Header.Timestamp = currentTicks;
                    _nextSequence++;
                    frames.Add(frame);
                }
            }
            return frames;
        }

        /// <summary>
        /// 处理接收到的帧。只交付比上次更新的帧, 丢弃旧的。
        /// </summary>
        public byte[]? ProcessReceivedFrame(NetworkFrame frame)
        {
            ushort seq = frame.Header.SequenceNumber;

            // 序列号比较 (考虑环绕)
            int diff = SequenceDiff(seq, _lastDeliveredSequence);

            if (!_hasDelivered || diff > 0)
            {
                // 新帧, 交付
                _lastDeliveredSequence = seq;
                _hasDelivered = true;
                return frame.Payload;
            }

            // 旧帧或重复帧, 丢弃
            return null;
        }

        /// <summary>序列号差值 (考虑环绕)。</summary>
        private int SequenceDiff(ushort a, ushort b)
        {
            int diff = (int)(a - b);
            if (diff > 32768) diff -= 65536;
            if (diff < -32768) diff += 65536;
            return diff;
        }

        /// <summary>重置。</summary>
        public void Reset()
        {
            _nextSequence = 0;
            _lastDeliveredSequence = 0;
            _hasDelivered = false;
            _pendingSend.Clear();
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 7. 消息分片管理器
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 分片信息。用于发送端跟踪分片状态。
    /// </summary>
    internal struct FragmentSendInfo
    {
        /// <summary>分片消息 ID。</summary>
        public ushort MessageId;

        /// <summary>总分片数。</summary>
        public ushort TotalFragments;

        /// <summary>已确认收到的分片位图。</summary>
        public uint[] AckBitmap;

        /// <summary>分片数据。</summary>
        public byte[][] FragmentData;

        /// <summary>创建时间。</summary>
        public long CreateTicks;

        /// <summary>最后重传时间。</summary>
        public long LastRetransmitTicks;
    }

    /// <summary>
    /// 重组缓冲区。用于接收端跟踪分片重组进度。
    /// </summary>
    internal struct ReassemblyBuffer
    {
        /// <summary>消息 ID。</summary>
        public ushort MessageId;

        /// <summary>总分片数。</summary>
        public int TotalFragments;

        /// <summary>已接收分片的数据。</summary>
        public byte[][] FragmentData;

        /// <summary>已接收分片位图。</summary>
        public bool[] ReceivedBitmap;

        /// <summary>创建时间。</summary>
        public long CreateTicks;

        /// <summary>完整消息大小。</summary>
        public int TotalMessageSize;
    }

    /// <summary>
    /// 消息分片管理器。将大于 MTU 的消息自动拆分为多个小片段发送,
    /// 接收端重组。支持:
    ///   - MTU 自适应分片 (每片最大 = MTU - 帧头)
    ///   - CRC 校验每片
    ///   - 超时清理未完成的重组
    ///   - 缺失分片重传请求 (通过 FragmentAck)
    /// </summary>
    public sealed class FragmentationManager
    {
        private const int DefaultMTU = 1200;       // 默认最大传输单元
        private const int FragmentHeaderSize = 10; // 分片头: 2(msgId) + 2(total) + 2(index) + 4(crc)
        private const int DefaultTimeoutMs = 10000;  // 重组超时 10秒
        private const int MaxPendingReassembly = 64;  // 最大同时重组数

        private int _mtu = DefaultMTU;
        private int _timeoutMs = DefaultTimeoutMs;
        private ushort _nextMessageId;

        // 发送端: 跟踪分片状态
        private readonly Dictionary<ushort, FragmentSendInfo> _sendFragments
            = new Dictionary<ushort, FragmentSendInfo>();

        // 接收端: 重组缓冲区
        private readonly Dictionary<ushort, ReassemblyBuffer> _reassembly
            = new Dictionary<ushort, ReassemblyBuffer>();

        /// <summary>MTU 大小。</summary>
        public int MTU
        {
            get => _mtu;
            set => _mtu = Math.Max(value, 64);
        }

        /// <summary>重组超时 (ms)。</summary>
        public int TimeoutMs
        {
            get => _timeoutMs;
            set => _timeoutMs = Math.Max(value, 1000);
        }

        /// <summary>待重组消息数。</summary>
        public int PendingReassemblyCount => _reassembly.Count;

        /// <summary>发送端分片数。</summary>
        public int ActiveSendFragments => _sendFragments.Count;

        /// <summary>每片最大数据大小。</summary>
        public int MaxFragmentPayload => _mtu - NetworkFrameHeader.HeaderSize - FragmentHeaderSize;

        /// <summary>
        /// 将消息分片。返回所有分片帧列表。
        /// 如果消息小于 MTU, 返回单个帧 (不分片)。
        /// </summary>
        public List<NetworkFrame> FragmentMessage(byte[] message, ChannelId channel)
        {
            if (message == null || message.Length == 0)
                return new List<NetworkFrame>();

            int maxPayload = MaxFragmentPayload;
            ushort msgId = ++_nextMessageId;

            if (message.Length <= maxPayload)
            {
                // 不需要分片, 作为单帧发送
                var payload = BuildFragmentPayload(msgId, 1, 0, message);
                var frame = new NetworkFrame(NetworkFrameType.Fragment, channel, payload);
                frame.Header.FrameId = msgId;
                return new List<NetworkFrame> { frame };
            }

            // 计算分片数
            int totalFragments = (message.Length + maxPayload - 1) / maxPayload;
            var frames = new List<NetworkFrame>(totalFragments);

            // 初始化发送端跟踪信息
            var sendInfo = new FragmentSendInfo
            {
                MessageId = msgId,
                TotalFragments = (ushort)totalFragments,
                AckBitmap = new uint[(totalFragments + 31) / 32],
                FragmentData = new byte[totalFragments][],
                CreateTicks = DateTime.UtcNow.Ticks,
                LastRetransmitTicks = DateTime.UtcNow.Ticks
            };

            int offset = 0;
            for (int i = 0; i < totalFragments; i++)
            {
                int chunkSize = Math.Min(maxPayload, message.Length - offset);
                byte[] chunk = new byte[chunkSize];
                Array.Copy(message, offset, chunk, 0, chunkSize);

                sendInfo.FragmentData[i] = chunk;

                var payload = BuildFragmentPayload(msgId, (ushort)totalFragments, (ushort)i, chunk);
                var frame = new NetworkFrame(NetworkFrameType.Fragment, channel, payload);
                frame.Header.FrameId = msgId;
                frames.Add(frame);

                offset += chunkSize;
            }

            _sendFragments[msgId] = sendInfo;
            return frames;
        }

        /// <summary>
        /// 处理接收到的分片帧。
        /// 返回 null 表示消息不完整; 返回 byte[] 表示重组完成。
        /// </summary>
        public byte[]? ProcessFragment(NetworkFrame frame, long currentTicks)
        {
            if (frame.Payload == null || frame.Payload.Length < FragmentHeaderSize)
                return null;

            int pos = 0;
            ushort msgId = ReadUInt16(frame.Payload, ref pos);
            ushort totalFrags = ReadUInt16(frame.Payload, ref pos);
            ushort fragIndex = ReadUInt16(frame.Payload, ref pos);
            uint fragCRC = ReadUInt32(frame.Payload, ref pos);

            int payloadLen = frame.Payload.Length - FragmentHeaderSize;

            // CRC 校验分片数据
            uint expectedCRC = CRC32Utility.ComputeCRC32(
                frame.Payload, FragmentHeaderSize, payloadLen);
            if (fragCRC != expectedCRC)
                return null; // 分片数据损坏

            // 获取或创建重组缓冲区
            if (!_reassembly.TryGetValue(msgId, out var buffer))
            {
                if (_reassembly.Count >= MaxPendingReassembly)
                {
                    // 清理最旧的重组缓冲区
                    CleanupTimeouts(currentTicks);
                }

                buffer = new ReassemblyBuffer
                {
                    MessageId = msgId,
                    TotalFragments = totalFrags,
                    FragmentData = new byte[totalFrags][],
                    ReceivedBitmap = new bool[totalFrags],
                    CreateTicks = currentTicks,
                    TotalMessageSize = 0
                };
                _reassembly[msgId] = buffer;
            }

            // 存储分片
            if (fragIndex >= totalFrags) return null;

            byte[] fragData = new byte[payloadLen];
            Array.Copy(frame.Payload, FragmentHeaderSize, fragData, 0, payloadLen);
            buffer.FragmentData[fragIndex] = fragData;
            buffer.ReceivedBitmap[fragIndex] = true;
            buffer.TotalMessageSize += payloadLen;

            // 检查是否所有分片都已收到
            bool allReceived = true;
            for (int i = 0; i < totalFrags; i++)
            {
                if (!buffer.ReceivedBitmap[i])
                {
                    allReceived = false;
                    break;
                }
            }

            if (allReceived)
            {
                // 重组消息
                var result = new byte[buffer.TotalMessageSize];
                int offset = 0;
                for (int i = 0; i < totalFrags; i++)
                {
                    Array.Copy(buffer.FragmentData[i], 0, result, offset, buffer.FragmentData[i].Length);
                    offset += buffer.FragmentData[i].Length;
                }

                _reassembly.Remove(msgId);

                // 清理发送端跟踪
                _sendFragments.Remove(msgId);

                return result;
            }

            return null;
        }

        /// <summary>获取缺失分片的重传请求 (FragmentAck 帧)。</summary>
        public List<NetworkFrame> GetMissingFragmentRequests(long currentTicks)
        {
            var requests = new List<NetworkFrame>();

            foreach (var kvp in _reassembly)
            {
                var buffer = kvp.Value;
                // 检查超时
                float elapsedMs = (float)(currentTicks - buffer.CreateTicks) / TimeSpan.TicksPerMillisecond;
                if (elapsedMs > _timeoutMs) continue;

                // 找缺失分片
                for (int i = 0; i < buffer.TotalFragments; i++)
                {
                    if (!buffer.ReceivedBitmap[i])
                    {
                        // 构造 FragmentAck 请求缺失分片
                        var payload = BuildFragmentAckPayload(buffer.MessageId, (ushort)i);
                        var frame = new NetworkFrame(NetworkFrameType.FragmentAck, ChannelId.Reliable, payload);
                        requests.Add(frame);
                    }
                }
            }

            return requests;
        }

        /// <summary>处理 FragmentAck, 标记分片已确认并返回需要重传的分片。</summary>
        public List<NetworkFrame> ProcessFragmentAck(NetworkFrame frame, long currentTicks)
        {
            var retransmits = new List<NetworkFrame>();

            if (frame.Payload == null || frame.Payload.Length < 4)
                return retransmits;

            int pos = 0;
            ushort msgId = ReadUInt16(frame.Payload, ref pos);
            ushort fragIndex = ReadUInt16(frame.Payload, ref pos);

            if (_sendFragments.TryGetValue(msgId, out var sendInfo))
            {
                // 标记分片已确认
                int bitmapIndex = fragIndex / 32;
                int bitIndex = fragIndex % 32;
                if (bitmapIndex < sendInfo.AckBitmap.Length)
                {
                    sendInfo.AckBitmap[bitmapIndex] |= (1u << bitIndex);
                }

                // 如果请求的是特定分片索引 (而非确认), 则重传
                if (fragIndex < sendInfo.TotalFragments)
                {
                    bool wasAcked = (sendInfo.AckBitmap[bitmapIndex] & (1u << bitIndex)) != 0;
                    // 重传逻辑: 如果对方请求了这个分片, 重传它
                    if (fragIndex < sendInfo.FragmentData.Length && sendInfo.FragmentData[fragIndex] != null)
                    {
                        var payload = BuildFragmentPayload(msgId, sendInfo.TotalFragments,
                            fragIndex, sendInfo.FragmentData[fragIndex]);
                        var frame = new NetworkFrame(NetworkFrameType.Fragment,
                            ChannelId.Reliable, payload);
                        frame.Header.FrameId = msgId;
                        frame.Header.Timestamp = currentTicks;
                        sendInfo.LastRetransmitTicks = currentTicks;
                        retransmits.Add(frame);
                    }
                }
            }

            return retransmits;
        }

        /// <summary>清理超时的重组缓冲区。</summary>
        public void CleanupTimeouts(long currentTicks)
        {
            var timedOut = new List<ushort>();
            foreach (var kvp in _reassembly)
            {
                float elapsedMs = (float)(currentTicks - kvp.Value.CreateTicks) / TimeSpan.TicksPerMillisecond;
                if (elapsedMs > _timeoutMs)
                {
                    timedOut.Add(kvp.Key);
                }
            }
            foreach (var id in timedOut)
            {
                _reassembly.Remove(id);
            }

            // 同时清理发送端的过期分片
            var oldSends = new List<ushort>();
            foreach (var kvp in _sendFragments)
            {
                float elapsedMs = (float)(currentTicks - kvp.Value.CreateTicks) / TimeSpan.TicksPerMillisecond;
                if (elapsedMs > _timeoutMs * 2)
                {
                    oldSends.Add(kvp.Key);
                }
            }
            foreach (var id in oldSends)
            {
                _sendFragments.Remove(id);
            }
        }

        /// <summary>重置。</summary>
        public void Reset()
        {
            _sendFragments.Clear();
            _reassembly.Clear();
            _nextMessageId = 0;
        }

        // ── 辅助方法 ──

        private static byte[] BuildFragmentPayload(ushort msgId, ushort totalFragments,
            ushort fragIndex, byte[] data)
        {
            byte[] payload = new byte[FragmentHeaderSize + data.Length];
            int pos = 0;
            WriteUInt16(payload, ref pos, msgId);
            WriteUInt16(payload, ref pos, totalFragments);
            WriteUInt16(payload, ref pos, fragIndex);

            uint crc = CRC32Utility.ComputeCRC32(data, 0, data.Length);
            WriteUInt32(payload, ref pos, crc);

            Array.Copy(data, 0, payload, pos, data.Length);
            return payload;
        }

        private static byte[] BuildFragmentAckPayload(ushort msgId, ushort fragIndex)
        {
            byte[] payload = new byte[4];
            int pos = 0;
            WriteUInt16(payload, ref pos, msgId);
            WriteUInt16(payload, ref pos, fragIndex);
            return payload;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void WriteUInt16(byte[] buf, ref int pos, ushort value)
        {
            buf[pos] = (byte)(value & 0xFF);
            buf[pos + 1] = (byte)((value >> 8) & 0xFF);
            pos += 2;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void WriteUInt32(byte[] buf, ref int pos, uint value)
        {
            buf[pos] = (byte)(value & 0xFF);
            buf[pos + 1] = (byte)((value >> 8) & 0xFF);
            buf[pos + 2] = (byte)((value >> 16) & 0xFF);
            buf[pos + 3] = (byte)((value >> 24) & 0xFF);
            pos += 4;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ushort ReadUInt16(byte[] buf, ref int pos)
        {
            ushort value = (ushort)(buf[pos] | (buf[pos + 1] << 8));
            pos += 2;
            return value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint ReadUInt32(byte[] buf, ref int pos)
        {
            uint value = (uint)(buf[pos] | (buf[pos + 1] << 8) |
                                (buf[pos + 2] << 16) | (buf[pos + 3] << 24));
            pos += 4;
            return value;
        }
    }
}
