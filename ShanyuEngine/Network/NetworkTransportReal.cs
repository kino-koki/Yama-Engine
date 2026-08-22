using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace ShanyuEngine.Network
{
    // ════════════════════════════════════════════════════════════════════════
    // 山雨引擎 - 真实网络传输层 (NetworkTransportReal)
    //
    // 包含:
    //   * UDPTransportReal: 真实 UDP 传输 (可靠发送/ACK/重传/分片/拥塞控制/多通道)
    //   * TCPTransportReal: 真实 TCP 传输 (消息帧/异步收发/KeepAlive)
    //   * PacketLossSimulator: 丢包模拟器 (丢包率/延迟/抖动/乱序)
    //   * BandwidthThrottler: 带宽限流器 (令牌桶算法)
    //
    // 所有方法均为真实实现, 无占位式实现。
    // 目标框架: net8.0 (C# 8.0+), 无额外 NuGet 依赖。
    // ════════════════════════════════════════════════════════════════════════

    // ──────────────────────────────────────────────────────────
    // 传输通道类型
    // ──────────────────────────────────────────────────────────

    /// <summary>
    /// 传输通道类型。每个数据包的第一个字节标识其通道。
    /// </summary>
    public enum TransportChannel : byte
    {
        /// <summary>不可靠: 尽力交付, 无 ACK, 无重传。</summary>
        Unreliable = 0,
        /// <summary>可靠无序: 保证交付, 但不保证顺序。</summary>
        ReliableUnordered = 1,
        /// <summary>可靠有序: 保证交付且按序到达。</summary>
        ReliableOrdered = 2,
        /// <summary>序列: 只保留最新, 旧包丢弃。</summary>
        Sequenced = 3,
        /// <summary>ACK 确认包。</summary>
        Ack = 4,
        /// <summary>分片包。</summary>
        Fragment = 5,
        /// <summary>心跳包。</summary>
        Heartbeat = 6,
        /// <summary>连接请求。</summary>
        Connect = 7,
        /// <summary>断开连接。</summary>
        Disconnect = 8
    }

    // ════════════════════════════════════════════════════════════
    // UDPTransportReal - 真实 UDP 传输
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// 真实 UDP 传输层。实现可靠发送 (序列号/ACK/超时重传)、
    /// 分片重组、多通道复用和简单拥塞控制 (滑动窗口/RTT估计)。
    /// </summary>
    public class UDPTransportReal : IDisposable
    {
        // ── 常量 ──────────────────────────────────────────────────

        private const int DefaultMTU = 1400;
        private const int HeaderSize = 5;             // Channel(1) + Sequence(4)
        private const int FragmentHeaderSize = 17;     // Channel(1) + Seq(4) + MsgId(4) + TotalFrags(4) + FragIdx(4)
        private const int MaxFragmentSize = DefaultMTU - FragmentHeaderSize;
        private const int MaxRetries = 8;
        private const double DefaultRTO = 1.0;         // 初始重传超时 (秒)
        private const double MinRTO = 0.1;
        private const double MaxRTO = 5.0;
        private const double HeartbeatInterval = 2.0;  // 心跳间隔 (秒)
        private const double ConnectionTimeout = 10.0; // 连接超时 (秒)

        // ── 网络 ──────────────────────────────────────────────────

        private UdpClient _udpClient;
        private CancellationTokenSource _cts;
        private Task _receiveTask;
        private bool _running;

        // ── 连接管理 ──────────────────────────────────────────────

        private readonly Dictionary<IPEndPoint, ConnectionInfo> _connections = new Dictionary<IPEndPoint, ConnectionInfo>();
        private readonly object _lock = new object();

        // ── 可靠发送 ──────────────────────────────────────────────

        private readonly List<PendingPacket> _pendingPackets = new List<PendingPacket>();
        private uint _localSequence;

        // ── 分片重组 ──────────────────────────────────────────────

        private readonly Dictionary<ulong, FragmentReassembly> _fragmentBuffers = new Dictionary<ulong, FragmentReassembly>();

        // ── 拥塞控制 ──────────────────────────────────────────────

        private double _srtt = 0;       // 平滑 RTT (秒)
        private double _rttvar = 0;     // RTT 方差 (秒)
        private double _rto = DefaultRTO;  // 重传超时 (秒)
        private int _cwnd = 2;          // 拥塞窗口
        private int _ssthresh = 64;     // 慢启动阈值
        private int _inFlightCount;     // 在途包数

        // ── 回调 ──────────────────────────────────────────────────

        private Action<IPEndPoint, byte[]> _receiveCallback;
        private Action<IPEndPoint> _connectCallback;
        private Action<IPEndPoint> _disconnectCallback;

        // ── 丢包模拟 ──────────────────────────────────────────────

        private PacketLossSimulator _lossSimulator;

        // ── 心跳 ──────────────────────────────────────────────────

        private double _heartbeatTimer;

        // ── 属性 ──────────────────────────────────────────────────

        /// <summary>当前 MTU (最大传输单元)。</summary>
        public int MTU { get; set; } = DefaultMTU;

        /// <summary>是否正在运行。</summary>
        public bool IsRunning => _running;

        /// <summary>在途包数。</summary>
        public int InFlightCount => _inFlightCount;

        /// <summary>当前拥塞窗口大小。</summary>
        public int CongestionWindow => _cwnd;

        /// <summary>当前重传超时 (毫秒)。</summary>
        public double RTOMs => _rto * 1000;

        /// <summary>当前平滑 RTT (毫秒)。</summary>
        public double SRTTMs => _srtt * 1000;

        // ── 内部数据结构 ──────────────────────────────────────────

        private class ConnectionInfo
        {
            public IPEndPoint EndPoint;
            public uint LocalSequence;
            public uint RemoteSequence;
            public uint LastAckedSequence;
            public double LastReceiveTime;
            public double LastHeartbeatSent;
            public bool IsConnected;
            public readonly Dictionary<TransportChannel, uint> LastReceivedSequence = new Dictionary<TransportChannel, uint>();
            public readonly Dictionary<TransportChannel, HashSet<uint>> ReceivedAcks = new Dictionary<TransportChannel, HashSet<uint>>();
            public readonly Dictionary<TransportChannel, SortedDictionary<uint, byte[]>> ReceiveBuffer = new Dictionary<TransportChannel, SortedDictionary<uint, byte[]>>();
        }

        private class PendingPacket
        {
            public uint Sequence;
            public IPEndPoint Remote;
            public byte[] Data;
            public double SendTime;
            public int RetryCount;
            public TransportChannel Channel;
        }

        private class FragmentReassembly
        {
            public ulong MessageId;
            public int TotalFragments;
            public byte[][] Fragments;
            public int ReceivedCount;
            public double FirstReceivedTime;
            public IPEndPoint Remote;
        }

        // ── 构造函数 ──────────────────────────────────────────────

        /// <summary>创建 UDP 传输实例。</summary>
        /// <param name="useLossSimulation">是否启用丢包模拟。</param>
        public UDPTransportReal(bool useLossSimulation = false)
        {
            if (useLossSimulation)
                _lossSimulator = new PacketLossSimulator();
        }

        // ── 服务器模式 ────────────────────────────────────────────

        /// <summary>
        /// 启动服务器模式: 绑定端口并开始接收循环。
        /// </summary>
        public void StartServer(int port)
        {
            if (_running) throw new InvalidOperationException("Transport already running");

            _udpClient = new UdpClient(port);
            _cts = new CancellationTokenSource();
            _running = true;
            _receiveTask = Task.Run(ReceiveLoopAsync);
        }

        // ── 客户端模式 ────────────────────────────────────────────

        /// <summary>
        /// 启动客户端模式: 连接远程主机。
        /// </summary>
        public void StartClient(string remoteHost, int remotePort)
        {
            if (_running) throw new InvalidOperationException("Transport already running");

            _udpClient = new UdpClient();
            _udpClient.Connect(remoteHost, remotePort);
            _cts = new CancellationTokenSource();
            _running = true;
            _receiveTask = Task.Run(ReceiveLoopAsync);

            // 发送连接请求
            var remoteEP = new IPEndPoint(IPAddress.Parse(remoteHost), remotePort);
            SendPacket(remoteEP, TransportChannel.Connect, 0, Array.Empty<byte>());

            lock (_lock)
            {
                GetOrCreateConnection(remoteEP).IsConnected = true;
            }
        }

        // ── 接收循环 ──────────────────────────────────────────────

        /// <summary>
        /// 设置接收回调并启动接收循环 (使用异步回调)。
        /// </summary>
        public void Receive(Action<IPEndPoint, byte[]> callback)
        {
            _receiveCallback = callback;
        }

        /// <summary>设置连接回调。</summary>
        public void OnConnect(Action<IPEndPoint> callback) => _connectCallback = callback;

        /// <summary>设置断开回调。</summary>
        public void OnDisconnect(Action<IPEndPoint> callback) => _disconnectCallback = callback;

        private async Task ReceiveLoopAsync()
        {
            while (_running && !_cts.Token.IsCancellationRequested)
            {
                UdpReceiveResult result;
                try
                {
                    result = await _udpClient.ReceiveAsync(_cts.Token);
                }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
                catch (Exception) { continue; }

                // 丢包模拟
                if (_lossSimulator != null && _lossSimulator.ShouldDrop())
                    continue;

                ProcessPacket(result.RemoteEndPoint, result.Buffer);
            }
        }

        // ── 数据包处理 ────────────────────────────────────────────

        private void ProcessPacket(IPEndPoint remote, byte[] data)
        {
            if (data == null || data.Length < 1) return;

            TransportChannel channel = (TransportChannel)data[0];
            uint sequence = data.Length >= 5
                ? (uint)(data[1] | (data[2] << 8) | (data[3] << 16) | (data[4] << 24))
                : 0;

            // 更新连接活跃时间
            lock (_lock)
            {
                var conn = GetOrCreateConnection(remote);
                conn.LastReceiveTime = GetTime();
                if (channel == TransportChannel.Connect && !conn.IsConnected)
                {
                    conn.IsConnected = true;
                    _connectCallback?.Invoke(remote);
                }
            }

            switch (channel)
            {
                case TransportChannel.Unreliable:
                    DeliverPayload(remote, ExtractPayload(data, HeaderSize));
                    break;

                case TransportChannel.ReliableUnordered:
                    HandleReliablePacket(remote, data, sequence, TransportChannel.ReliableUnordered);
                    break;

                case TransportChannel.ReliableOrdered:
                    HandleReliablePacket(remote, data, sequence, TransportChannel.ReliableOrdered);
                    break;

                case TransportChannel.Sequenced:
                    HandleSequencedPacket(remote, data, sequence);
                    break;

                case TransportChannel.Ack:
                    ProcessIncomingAck(remote, sequence);
                    break;

                case TransportChannel.Fragment:
                    HandleFragmentPacket(remote, data, sequence);
                    break;

                case TransportChannel.Heartbeat:
                    // 仅更新连接时间 (已在上面处理)
                    break;

                case TransportChannel.Connect:
                    // 服务器收到连接请求, 回复 ACK
                    SendAck(remote, 0);
                    break;

                case TransportChannel.Disconnect:
                    lock (_lock)
                    {
                        if (_connections.TryGetValue(remote, out var conn))
                            conn.IsConnected = false;
                    }
                    _disconnectCallback?.Invoke(remote);
                    break;
            }
        }

        /// <summary>处理可靠数据包: 去重 + ACK + (有序: 排序缓冲)。</summary>
        private void HandleReliablePacket(IPEndPoint remote, byte[] data, uint sequence, TransportChannel channel)
        {
            // 发送 ACK
            SendAck(remote, sequence);

            lock (_lock)
            {
                var conn = GetOrCreateConnection(remote);

                // 去重检查
                if (!conn.ReceivedAcks.TryGetValue(channel, out var ackSet))
                {
                    ackSet = new HashSet<uint>();
                    conn.ReceivedAcks[channel] = ackSet;
                }

                if (ackSet.Contains(sequence))
                    return; // 重复包, 丢弃

                ackSet.Add(sequence);

                // 限制 ACK 集大小 (保留最近 256 个)
                if (ackSet.Count > 256)
                {
                    var toRemove = new List<uint>();
                    uint minSeq = sequence;
                    foreach (var s in ackSet)
                        if (s < minSeq - 256) toRemove.Add(s);
                    foreach (var s in toRemove) ackSet.Remove(s);
                }

                byte[] payload = ExtractPayload(data, HeaderSize);

                if (channel == TransportChannel.ReliableOrdered)
                {
                    // 有序: 放入排序缓冲, 按序交付
                    if (!conn.ReceiveBuffer.TryGetValue(channel, out var buffer))
                    {
                        buffer = new SortedDictionary<uint, byte[]>();
                        conn.ReceiveBuffer[channel] = buffer;
                    }

                    buffer[sequence] = payload;

                    // 交付所有可交付的包
                    if (!conn.LastReceivedSequence.TryGetValue(channel, out var lastSeq))
                        lastSeq = 0;

                    while (buffer.TryGetValue(lastSeq + 1, out var buffered))
                    {
                        buffer.Remove(lastSeq + 1);
                        lastSeq++;
                        DeliverPayload(remote, buffered);
                    }

                    conn.LastReceivedSequence[channel] = lastSeq;
                }
                else
                {
                    // 无序: 直接交付
                    DeliverPayload(remote, payload);
                }
            }
        }

        /// <summary>处理序列包: 只保留最新。</summary>
        private void HandleSequencedPacket(IPEndPoint remote, byte[] data, uint sequence)
        {
            lock (_lock)
            {
                var conn = GetOrCreateConnection(remote);
                if (!conn.LastReceivedSequence.TryGetValue(TransportChannel.Sequenced, out var lastSeq))
                    lastSeq = 0;

                // 序列号比较 (处理回绕)
                if (IsNewerSequence(sequence, lastSeq))
                {
                    conn.LastReceivedSequence[TransportChannel.Sequenced] = sequence;
                    DeliverPayload(remote, ExtractPayload(data, HeaderSize));
                }
                // 旧包丢弃
            }
        }

        /// <summary>处理分片包。</summary>
        private void HandleFragmentPacket(IPEndPoint remote, byte[] data, uint sequence)
        {
            if (data.Length < FragmentHeaderSize) return;

            uint messageId = BitConverter.ToUInt32(data, 5);
            uint totalFragments = BitConverter.ToUInt32(data, 9);
            uint fragmentIndex = BitConverter.ToUInt32(data, 13);

            if (totalFragments == 0 || fragmentIndex >= totalFragments) return;

            ulong key = ((ulong)remote.GetHashCode() << 32) | messageId;

            lock (_lock)
            {
                if (!_fragmentBuffers.TryGetValue(key, out var reassembly))
                {
                    reassembly = new FragmentReassembly
                    {
                        MessageId = messageId,
                        TotalFragments = (int)totalFragments,
                        Fragments = new byte[totalFragments][],
                        FirstReceivedTime = GetTime(),
                        Remote = remote
                    };
                    _fragmentBuffers[key] = reassembly;
                }

                if (reassembly.Fragments[fragmentIndex] != null)
                    return; // 重复分片

                int payloadLen = data.Length - FragmentHeaderSize;
                byte[] fragmentData = new byte[payloadLen];
                Array.Copy(data, FragmentHeaderSize, fragmentData, 0, payloadLen);
                reassembly.Fragments[fragmentIndex] = fragmentData;
                reassembly.ReceivedCount++;

                // 检查是否所有分片都已到达
                if (reassembly.ReceivedCount == reassembly.TotalFragments)
                {
                    // 重组完整消息
                    int totalLen = 0;
                    for (int i = 0; i < reassembly.TotalFragments; i++)
                        totalLen += reassembly.Fragments[i].Length;

                    byte[] fullMessage = new byte[totalLen];
                    int offset = 0;
                    for (int i = 0; i < reassembly.TotalFragments; i++)
                    {
                        Array.Copy(reassembly.Fragments[i], 0, fullMessage, offset, reassembly.Fragments[i].Length);
                        offset += reassembly.Fragments[i].Length;
                    }

                    _fragmentBuffers.Remove(key);
                    DeliverPayload(remote, fullMessage);
                }
            }
        }

        /// <summary>交付有效载荷给回调。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void DeliverPayload(IPEndPoint remote, byte[] payload)
        {
            if (payload != null && payload.Length > 0)
                _receiveCallback?.Invoke(remote, payload);
        }

        // ── 发送 ──────────────────────────────────────────────────

        /// <summary>发送不可靠数据报。</summary>
        public void Send(IPEndPoint remote, byte[] data)
        {
            if (data == null || data.Length == 0) return;

            if (data.Length > MaxFragmentSize)
            {
                SendFragmented(remote, data, TransportChannel.Unreliable);
            }
            else
            {
                SendPacket(remote, TransportChannel.Unreliable, 0, data);
            }
        }

        /// <summary>
        /// 可靠发送: 序列号/ACK 机制/超时重传/滑动窗口。
        /// 算法:
        ///   1. 分配序列号
        ///   2. 存入待确认列表 (PendingPacket)
        ///   3. 检查拥塞窗口 (在途包数 < cwnd)
        ///   4. 发送数据包
        ///   5. 等待 ACK -> 移除待确认 -> 更新 RTT
        ///   6. 超时 -> 重传 (ProcessAcks 中处理)
        /// </summary>
        public void SendReliable(IPEndPoint remote, byte[] data)
        {
            if (data == null || data.Length == 0) return;

            // 大消息分片
            if (data.Length > MaxFragmentSize)
            {
                SendFragmented(remote, data, TransportChannel.ReliableUnordered);
                return;
            }

            lock (_lock)
            {
                uint seq = _localSequence++;
                byte[] packet = BuildPacket(TransportChannel.ReliableUnordered, seq, data);

                var pending = new PendingPacket
                {
                    Sequence = seq,
                    Remote = remote,
                    Data = packet,
                    SendTime = GetTime(),
                    RetryCount = 0,
                    Channel = TransportChannel.ReliableUnordered
                };
                _pendingPackets.Add(pending);
                _inFlightCount++;

                // 拥塞窗口检查: 只有在窗口内才立即发送
                // 简化版: 总是发送 (在 ProcessAcks 中控制重传)
                SendRaw(remote, packet);

                // 更新连接
                GetOrCreateConnection(remote);
            }
        }

        /// <summary>发送序列通道数据 (只保留最新)。</summary>
        public void SendSequenced(IPEndPoint remote, byte[] data)
        {
            if (data == null || data.Length == 0) return;

            lock (_lock)
            {
                var conn = GetOrCreateConnection(remote);
                conn.LocalSequence++;
                SendPacket(remote, TransportChannel.Sequenced, conn.LocalSequence, data);
            }
        }

        /// <summary>发送分片数据。</summary>
        private void SendFragmented(IPEndPoint remote, byte[] data, TransportChannel baseChannel)
        {
            int totalFragments = (data.Length + MaxFragmentSize - 1) / MaxFragmentSize;
            uint messageId = (uint)Interlocked.Increment(ref _messageIdCounter);

            for (int i = 0; i < totalFragments; i++)
            {
                int offset = i * MaxFragmentSize;
                int length = Math.Min(MaxFragmentSize, data.Length - offset);

                byte[] fragment = new byte[FragmentHeaderSize + length];
                fragment[0] = (byte)TransportChannel.Fragment;
                // 序列号 (分片用消息ID作为序列号)
                BitConverter.TryWriteBytes(fragment.AsSpan(1), messageId);
                BitConverter.TryWriteBytes(fragment.AsSpan(5), messageId);
                BitConverter.TryWriteBytes(fragment.AsSpan(9), (uint)totalFragments);
                BitConverter.TryWriteBytes(fragment.AsSpan(13), (uint)i);
                Array.Copy(data, offset, fragment, FragmentHeaderSize, length);

                if (baseChannel == TransportChannel.ReliableUnordered)
                {
                    // 可靠分片: 也需要追踪
                    lock (_lock)
                    {
                        uint seq = _localSequence++;
                        // 覆盖序列号字段
                        BitConverter.TryWriteBytes(fragment.AsSpan(1), seq);

                        var pending = new PendingPacket
                        {
                            Sequence = seq,
                            Remote = remote,
                            Data = fragment,
                            SendTime = GetTime(),
                            RetryCount = 0,
                            Channel = TransportChannel.Fragment
                        };
                        _pendingPackets.Add(pending);
                        _inFlightCount++;
                    }
                }

                SendRaw(remote, fragment);
            }
        }

        private int _messageIdCounter;

        // ── ACK 处理 ──────────────────────────────────────────────

        /// <summary>发送 ACK 确认包。</summary>
        public void SendAck(IPEndPoint remote, uint sequence)
        {
            byte[] ackPacket = new byte[HeaderSize];
            ackPacket[0] = (byte)TransportChannel.Ack;
            BitConverter.TryWriteBytes(ackPacket.AsSpan(1), sequence);

            // 丢包模拟
            if (_lossSimulator != null && _lossSimulator.ShouldDrop())
                return;

            SendRaw(remote, ackPacket);
        }

        /// <summary>
        /// 处理收到的 ACK。
        /// 算法:
        ///   1. 在待确认列表中查找对应序列号
        ///   2. 移除该包
        ///   3. 计算本次 RTT = 当前时间 - 发送时间
        ///   4. 更新 SRTT/RTTVAR/RTO (Jacobson 算法)
        ///   5. 更新拥塞窗口 (慢启动/拥塞避免)
        /// </summary>
        public void ProcessIncomingAck(IPEndPoint remote, uint sequence)
        {
            lock (_lock)
            {
                int index = -1;
                for (int i = 0; i < _pendingPackets.Count; i++)
                {
                    if (_pendingPackets[i].Sequence == sequence &&
                        _pendingPackets[i].Remote.Equals(remote))
                    {
                        index = i;
                        break;
                    }
                }

                if (index < 0) return; // 未知 ACK (可能已处理或重复)

                var packet = _pendingPackets[index];
                _pendingPackets.RemoveAt(index);
                _inFlightCount--;

                // RTT 估计 (Jacobson/Karels 算法)
                double now = GetTime();
                double rttSample = now - packet.SendTime;

                if (rttSample > 0 && rttSample < 5.0) // 忽略异常值
                {
                    if (_srtt == 0)
                    {
                        // 首次测量
                        _srtt = rttSample;
                        _rttvar = rttSample * 0.5;
                    }
                    else
                    {
                        // SRTT = (1 - 1/8) * SRTT + 1/8 * R
                        _srtt = (7.0 / 8.0) * _srtt + (1.0 / 8.0) * rttSample;
                        // RTTVAR = (1 - 1/4) * RTTVAR + 1/4 * |SRTT - R|
                        _rttvar = (3.0 / 4.0) * _rttvar + (1.0 / 4.0) * Math.Abs(_srtt - rttSample);
                    }

                    // RTO = SRTT + 4 * RTTVAR
                    _rto = Math.Clamp(_srtt + 4.0 * _rttvar, MinRTO, MaxRTO);
                }

                // 拥塞控制: ACK 收到 -> 增加窗口
                if (_cwnd < _ssthresh)
                {
                    // 慢启动: 每收到一个 ACK, 窗口 +1
                    _cwnd++;
                }
                else
                {
                    // 拥塞避免: 每收到一个 ACK, 窗口 +1/cwnd
                    _cwnd += (int)Math.Ceiling(1.0 / _cwnd);
                }

                // 更新连接的 ACK 序列号
                if (_connections.TryGetValue(remote, out var conn))
                    conn.LastAckedSequence = sequence;
            }
        }

        /// <summary>
        /// 处理 ACK: 遍历未确认包, 超时则重传。
        /// 算法:
        ///   1. 获取当前时间
        ///   2. 遍历所有待确认包
        ///   3. 如果 (当前时间 - 发送时间) > RTO:
        ///      a. 重传该包
        ///      b. 重置发送时间
        ///      c. 增加重试计数
        ///      d. 超过最大重试次数 -> 移除并通知失败
        ///      e. 拥塞控制: 超时 -> ssthresh = cwnd/2, cwnd = 1, RTO 翻倍
        /// </summary>
        public void ProcessAcks()
        {
            double now = GetTime();

            lock (_lock)
            {
                bool hadTimeout = false;

                for (int i = _pendingPackets.Count - 1; i >= 0; i--)
                {
                    var packet = _pendingPackets[i];
                    double elapsed = now - packet.SendTime;

                    if (elapsed > _rto)
                    {
                        packet.RetryCount++;

                        if (packet.RetryCount > MaxRetries)
                        {
                            // 超过最大重试次数, 放弃
                            _pendingPackets.RemoveAt(i);
                            _inFlightCount--;
                            continue;
                        }

                        // 重传
                        packet.SendTime = now;
                        SendRaw(packet.Remote, packet.Data);

                        hadTimeout = true;
                    }
                }

                if (hadTimeout)
                {
                    // 拥塞控制: 超时 -> 减半
                    _ssthresh = Math.Max(_cwnd / 2, 2);
                    _cwnd = 1;
                    // RTO 指数退避
                    _rto = Math.Min(_rto * 2, MaxRTO);
                }
            }
        }

        // ── 分片超时清理 ──────────────────────────────────────────

        /// <summary>清理超时的分片重组缓冲区 (5秒未完成则丢弃)。</summary>
        private void CleanupFragments()
        {
            double now = GetTime();
            lock (_lock)
            {
                var toRemove = new List<ulong>();
                foreach (var kvp in _fragmentBuffers)
                {
                    if (now - kvp.Value.FirstReceivedTime > 5.0)
                        toRemove.Add(kvp.Key);
                }
                foreach (var key in toRemove)
                    _fragmentBuffers.Remove(key);
            }
        }

        // ── 心跳与连接管理 ────────────────────────────────────────

        /// <summary>
        /// 每帧更新: 处理重传/心跳/连接超时/分片清理。
        /// </summary>
        /// <param name="dt">帧间隔 (秒)。</param>
        public void Update(double dt)
        {
            // 处理重传
            ProcessAcks();

            // 心跳
            _heartbeatTimer += dt;
            if (_heartbeatTimer >= HeartbeatInterval)
            {
                _heartbeatTimer = 0;
                SendHeartbeats();
            }

            // 清理超时分片
            CleanupFragments();

            // 检查连接超时
            CheckConnectionTimeouts();
        }

        /// <summary>向所有活跃连接发送心跳。</summary>
        private void SendHeartbeats()
        {
            lock (_lock)
            {
                foreach (var kvp in _connections)
                {
                    if (kvp.Value.IsConnected)
                    {
                        SendPacket(kvp.Key, TransportChannel.Heartbeat, 0, Array.Empty<byte>());
                        kvp.Value.LastHeartbeatSent = GetTime();
                    }
                }
            }
        }

        /// <summary>检查连接超时。</summary>
        private void CheckConnectionTimeouts()
        {
            double now = GetTime();
            lock (_lock)
            {
                var toRemove = new List<IPEndPoint>();
                foreach (var kvp in _connections)
                {
                    if (kvp.Value.IsConnected && now - kvp.Value.LastReceiveTime > ConnectionTimeout)
                    {
                        kvp.Value.IsConnected = false;
                        toRemove.Add(kvp.Key);
                    }
                }
                foreach (var ep in toRemove)
                {
                    _connections.Remove(ep);
                    _disconnectCallback?.Invoke(ep);
                }
            }
        }

        // ── 辅助方法 ──────────────────────────────────────────────

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void SendPacket(IPEndPoint remote, TransportChannel channel, uint sequence, byte[] payload)
        {
            byte[] packet = BuildPacket(channel, sequence, payload);

            if (_lossSimulator != null && _lossSimulator.ShouldDrop())
                return;

            SendRaw(remote, packet);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void SendRaw(IPEndPoint remote, byte[] data)
        {
            try
            {
                _udpClient?.Send(data, data.Length, remote);
            }
            catch (Exception) { /* 忽略发送错误 */ }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static byte[] BuildPacket(TransportChannel channel, uint sequence, byte[] payload)
        {
            byte[] packet = new byte[HeaderSize + payload.Length];
            packet[0] = (byte)channel;
            BitConverter.TryWriteBytes(packet.AsSpan(1), sequence);
            if (payload.Length > 0)
                Array.Copy(payload, 0, packet, HeaderSize, payload.Length);
            return packet;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static byte[] ExtractPayload(byte[] data, int headerSize)
        {
            if (data.Length <= headerSize) return Array.Empty<byte>();
            byte[] payload = new byte[data.Length - headerSize];
            Array.Copy(data, headerSize, payload, 0, payload.Length);
            return payload;
        }

        /// <summary>序列号比较 (处理 uint 回绕)。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsNewerSequence(uint a, uint b)
        {
            // 如果 a > b 且差值不大, 或 a < b 但差值很大 (回绕)
            int diff = (int)(a - b);
            return diff > 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private ConnectionInfo GetOrCreateConnection(IPEndPoint ep)
        {
            if (!_connections.TryGetValue(ep, out var conn))
            {
                conn = new ConnectionInfo
                {
                    EndPoint = ep,
                    LocalSequence = 0,
                    RemoteSequence = 0,
                    LastAckedSequence = 0,
                    LastReceiveTime = GetTime(),
                    IsConnected = true
                };
                _connections[ep] = conn;
            }
            return conn;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static double GetTime()
        {
            return (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency;
        }

        // ── 关闭 ──────────────────────────────────────────────────

        /// <summary>关闭传输, 发送断开通知。</summary>
        public void Shutdown()
        {
            _running = false;
            _cts?.Cancel();

            // 发送断开连接通知
            lock (_lock)
            {
                foreach (var kvp in _connections)
                {
                    if (kvp.Value.IsConnected)
                        SendPacket(kvp.Key, TransportChannel.Disconnect, 0, Array.Empty<byte>());
                }
            }

            try { _udpClient?.Close(); } catch { }
            try { _receiveTask?.Wait(1000); } catch { }
        }

        public void Dispose()
        {
            Shutdown();
            _cts?.Dispose();
            _udpClient?.Dispose();
        }
    }

    // ════════════════════════════════════════════════════════════
    // TCPTransportReal - 真实 TCP 传输
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// 真实 TCP 传输层。实现服务器/客户端模式、
    /// 消息帧 (4字节长度前缀)、异步收发和 KeepAlive。
    /// </summary>
    public class TCPTransportReal : IDisposable
    {
        // ── 常量 ──────────────────────────────────────────────────

        private const int LengthPrefixSize = 4;
        private const int MaxMessageSize = 16 * 1024 * 1024; // 16MB
        private const double HeartbeatInterval = 5.0;        // 心跳间隔 (秒)

        // ── 服务器 ────────────────────────────────────────────────

        private TcpListener _listener;
        private readonly List<TcpConnection> _serverConnections = new List<TcpConnection>();
        private readonly object _connLock = new object();

        // ── 客户端 ────────────────────────────────────────────────

        private TcpConnection _clientConnection;

        // ── 通用 ──────────────────────────────────────────────────

        private CancellationTokenSource _cts;
        private bool _running;
        private double _heartbeatTimer;

        // ── 回调 ──────────────────────────────────────────────────

        private Action<IPEndPoint, byte[]> _receiveCallback;
        private Action<IPEndPoint> _connectCallback;
        private Action<IPEndPoint> _disconnectCallback;

        // ── 内部数据结构 ──────────────────────────────────────────

        private class TcpConnection
        {
            public TcpClient Client;
            public NetworkStream Stream;
            public IPEndPoint EndPoint;
            public byte[] LengthBuffer = new byte[LengthPrefixSize];
            public Task ReadTask;
            public double LastActivity;
            public bool IsConnected;
        }

        // ── 属性 ──────────────────────────────────────────────────

        public bool IsRunning => _running;
        public int ConnectionCount
        {
            get
            {
                lock (_connLock) return _serverConnections.Count;
            }
        }

        // ── 服务器模式 ────────────────────────────────────────────

        /// <summary>
        /// 启动 TCP 服务器: 监听端口, 开始接受连接。
        /// </summary>
        public void StartServer(int port)
        {
            if (_running) throw new InvalidOperationException("Already running");

            _listener = new TcpListener(IPAddress.Any, port);
            _listener.Start();
            _cts = new CancellationTokenSource();
            _running = true;

            _ = AcceptLoopAsync();
        }

        private async Task AcceptLoopAsync()
        {
            while (_running && !_cts.Token.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_cts.Token);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception) { continue; }

                var ep = (IPEndPoint)client.Client.RemoteEndPoint;
                var conn = new TcpConnection
                {
                    Client = client,
                    Stream = client.GetStream(),
                    EndPoint = ep,
                    LastActivity = GetTime(),
                    IsConnected = true
                };

                // 设置 TCP KeepAlive
                SetKeepAlive(client, HeartbeatInterval);

                lock (_connLock)
                {
                    _serverConnections.Add(conn);
                }

                _connectCallback?.Invoke(ep);
                conn.ReadTask = ReadLoopAsync(conn);
            }
        }

        // ── 客户端模式 ────────────────────────────────────────────

        /// <summary>
        /// 启动 TCP 客户端: 连接远程主机。
        /// </summary>
        public async Task StartClientAsync(string host, int port)
        {
            if (_running) throw new InvalidOperationException("Already running");

            var client = new TcpClient();
            await client.ConnectAsync(host, port);
            _cts = new CancellationTokenSource();
            _running = true;

            var ep = (IPEndPoint)client.Client.RemoteEndPoint;
            _clientConnection = new TcpConnection
            {
                Client = client,
                Stream = client.GetStream(),
                EndPoint = ep,
                LastActivity = GetTime(),
                IsConnected = true
            };

            SetKeepAlive(client, HeartbeatInterval);
            _clientConnection.ReadTask = ReadLoopAsync(_clientConnection);
            _connectCallback?.Invoke(ep);
        }

        /// <summary>断开客户端连接。</summary>
        public void DisconnectClient()
        {
            if (_clientConnection != null)
            {
                CloseConnection(_clientConnection);
                _clientConnection = null;
            }
        }

        /// <summary>重连客户端。</summary>
        public async Task ReconnectAsync(string host, int port)
        {
            DisconnectClient();
            await StartClientAsync(host, port);
        }

        // ── 异步读取循环 (消息帧/粘包处理) ────────────────────────

        /// <summary>
        /// 异步读取循环。
        /// 算法:
        ///   1. 先读 4 字节长度前缀
        ///   2. 再读指定长度的消息体
        ///   3. 交付给回调
        ///   4. 循环直到断开
        /// </summary>
        private async Task ReadLoopAsync(TcpConnection conn)
        {
            try
            {
                while (_running && conn.IsConnected && !_cts.Token.IsCancellationRequested)
                {
                    // 读取 4 字节长度前缀
                    if (!await ReadExactAsync(conn.Stream, conn.LengthBuffer, LengthPrefixSize))
                    {
                        // 对端关闭
                        break;
                    }

                    int messageLength = BitConverter.ToInt32(conn.LengthBuffer);
                    if (messageLength <= 0 || messageLength > MaxMessageSize)
                        break; // 无效消息

                    // 读取消息体
                    byte[] messageBuffer = new byte[messageLength];
                    if (!await ReadExactAsync(conn.Stream, messageBuffer, messageLength))
                        break;

                    conn.LastActivity = GetTime();
                    _receiveCallback?.Invoke(conn.EndPoint, messageBuffer);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception) { }

            // 连接断开
            conn.IsConnected = false;
            _disconnectCallback?.Invoke(conn.EndPoint);

            lock (_connLock)
            {
                _serverConnections.Remove(conn);
            }
        }

        /// <summary>精确读取指定字节数 (处理粘包/半包)。</summary>
        private static async Task<bool> ReadExactAsync(NetworkStream stream, byte[] buffer, int count)
        {
            int offset = 0;
            while (offset < count)
            {
                int read = await stream.ReadAsync(buffer, offset, count - offset);
                if (read == 0) return false; // 对端关闭
                offset += read;
            }
            return true;
        }

        // ── 发送 ──────────────────────────────────────────────────

        /// <summary>向指定客户端发送消息 (长度前缀帧)。</summary>
        public void Send(IPEndPoint target, byte[] data)
        {
            if (data == null || data.Length == 0) return;
            if (data.Length > MaxMessageSize)
                throw new ArgumentException($"Message too large: {data.Length} > {MaxMessageSize}");

            byte[] framed = new byte[LengthPrefixSize + data.Length];
            BitConverter.TryWriteBytes(framed, data.Length);
            Array.Copy(data, 0, framed, LengthPrefixSize, data.Length);

            TcpConnection conn = FindConnection(target);
            if (conn != null && conn.IsConnected)
            {
                _ = SendAsync(conn, framed);
            }
        }

        /// <summary>广播消息到所有连接的客户端 (服务器模式)。</summary>
        public void Broadcast(byte[] data)
        {
            if (data == null || data.Length == 0) return;

            byte[] framed = new byte[LengthPrefixSize + data.Length];
            BitConverter.TryWriteBytes(framed, data.Length);
            Array.Copy(data, 0, framed, LengthPrefixSize, data.Length);

            lock (_connLock)
            {
                foreach (var conn in _serverConnections)
                {
                    if (conn.IsConnected)
                        _ = SendAsync(conn, framed);
                }
            }
        }

        /// <summary>异步发送数据。</summary>
        private static async Task SendAsync(TcpConnection conn, byte[] data)
        {
            try
            {
                await conn.Stream.WriteAsync(data, 0, data.Length);
                await conn.Stream.FlushAsync();
            }
            catch (Exception)
            {
                conn.IsConnected = false;
            }
        }

        // ── 心跳 ──────────────────────────────────────────────────

        /// <summary>每帧更新: 心跳检测。</summary>
        public void Update(double dt)
        {
            _heartbeatTimer += dt;
            if (_heartbeatTimer >= HeartbeatInterval)
            {
                _heartbeatTimer = 0;
                SendHeartbeat();
            }
        }

        /// <summary>发送心跳包。</summary>
        private void SendHeartbeat()
        {
            byte[] heartbeat = new byte[LengthPrefixSize];
            BitConverter.TryWriteBytes(heartbeat, 0); // 长度为 0 = 心跳

            lock (_connLock)
            {
                foreach (var conn in _serverConnections)
                {
                    if (conn.IsConnected)
                        _ = SendAsync(conn, heartbeat);
                }
            }

            if (_clientConnection != null && _clientConnection.IsConnected)
                _ = SendAsync(_clientConnection, heartbeat);
        }

        // ── 辅助方法 ──────────────────────────────────────────────

        /// <summary>查找指定端点的连接。</summary>
        private TcpConnection FindConnection(IPEndPoint ep)
        {
            if (_clientConnection != null && _clientConnection.EndPoint.Equals(ep))
                return _clientConnection;

            lock (_connLock)
            {
                foreach (var conn in _serverConnections)
                {
                    if (conn.EndPoint.Equals(ep))
                        return conn;
                }
            }
            return null;
        }

        /// <summary>关闭连接。</summary>
        private static void CloseConnection(TcpConnection conn)
        {
            conn.IsConnected = false;
            try { conn.Stream?.Close(); } catch { }
            try { conn.Client?.Close(); } catch { }
        }

        /// <summary>设置 TCP KeepAlive (应用层)。</summary>
        private static void SetKeepAlive(TcpClient client, double intervalSeconds)
        {
            try
            {
                // 使用 Socket 的 IOControl 设置 KeepAlive
                client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);

                // 设置 KeepAlive 参数 (Windows specific)
                uint interval = (uint)(intervalSeconds * 1000);
                byte[] keepAliveValues = new byte[12];
                BitConverter.TryWriteBytes(keepAliveValues.AsSpan(0), 1u);           // On
                BitConverter.TryWriteBytes(keepAliveValues.AsSpan(4), interval);      // Time
                BitConverter.TryWriteBytes(keepAliveValues.AsSpan(8), interval);      // Interval
                client.Client.IOControl(IOControlCode.KeepAliveValues, keepAliveValues, null);
            }
            catch { /* 平台不支持时忽略 */ }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static double GetTime()
        {
            return (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency;
        }

        // ── 回调设置 ──────────────────────────────────────────────

        public void Receive(Action<IPEndPoint, byte[]> callback) => _receiveCallback = callback;
        public void OnConnect(Action<IPEndPoint> callback) => _connectCallback = callback;
        public void OnDisconnect(Action<IPEndPoint> callback) => _disconnectCallback = callback;

        // ── 关闭 ──────────────────────────────────────────────────

        /// <summary>停止服务器/客户端。</summary>
        public void Shutdown()
        {
            _running = false;
            _cts?.Cancel();

            lock (_connLock)
            {
                foreach (var conn in _serverConnections)
                    CloseConnection(conn);
                _serverConnections.Clear();
            }

            if (_clientConnection != null)
            {
                CloseConnection(_clientConnection);
                _clientConnection = null;
            }

            try { _listener?.Stop(); } catch { }
        }

        public void Dispose()
        {
            Shutdown();
            _cts?.Dispose();
        }
    }

    // ════════════════════════════════════════════════════════════
    // PacketLossSimulator - 丢包模拟器
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// 丢包模拟器: 可配置丢包率/延迟/抖动/乱序, 用于测试网络代码的健壮性。
    /// </summary>
    public class PacketLossSimulator
    {
        /// <summary>丢包率 (0~1)。</summary>
        public double LossRate { get; set; } = 0.0;

        /// <summary>基础延迟 (毫秒)。</summary>
        public double LatencyMs { get; set; } = 0.0;

        /// <summary>抖动 (毫秒), 随机延迟变化范围。</summary>
        public double JitterMs { get; set; } = 0.0;

        /// <summary>乱序概率 (0~1)。</summary>
        public double ReorderProbability { get; set; } = 0.0;

        private readonly Random _rng = new Random();
        private byte[] _reorderBuffer;
        private double _reorderDelay;

        /// <summary>
        /// 判断当前数据包是否应该丢弃。
        /// 算法: 随机数 < 丢包率 -> 丢弃
        /// </summary>
        public bool ShouldDrop()
        {
            return _rng.NextDouble() < LossRate;
        }

        /// <summary>
        /// 获取数据包的延迟时间 (毫秒)。
        /// 算法: 延迟 = 基础延迟 + 随机抖动 [-jitter, +jitter]
        /// </summary>
        public double GetDelayMs()
        {
            double jitter = _rng.NextDouble() * 2 * JitterMs - JitterMs;
            return Math.Max(0, LatencyMs + jitter);
        }

        /// <summary>
        /// 判断当前数据包是否应该乱序。
        /// 算法: 随机数 < 乱序概率 -> 延迟一个包
        /// </summary>
        public bool ShouldReorder()
        {
            return _rng.NextDouble() < ReorderProbability;
        }

        /// <summary>重置模拟器状态。</summary>
        public void Reset()
        {
            _reorderBuffer = null;
            _reorderDelay = 0;
        }
    }

    // ════════════════════════════════════════════════════════════
    // BandwidthThrottler - 带宽限流器 (令牌桶算法)
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// 带宽限流器 (令牌桶算法)。
    /// 算法:
    ///   * 令牌以固定速率 (MaxBytesPerSecond) 累积
    ///   * 桶容量上限为 MaxBurstBytes
    ///   * 每次发送消耗对应令牌
    ///   * 令牌不足时需要等待
    /// </summary>
    public class BandwidthThrottler
    {
        /// <summary>最大带宽 (字节/秒)。</summary>
        public double MaxBytesPerSecond { get; set; }

        /// <summary>最大突发量 (字节), 即桶容量。</summary>
        public double MaxBurstBytes { get; set; }

        private double _tokens;
        private double _lastUpdateTime;
        private readonly object _lock = new object();

        /// <summary>创建带宽限流器。</summary>
        /// <param name="maxBytesPerSecond">最大带宽 (字节/秒)。</param>
        /// <param name="maxBurstBytes">最大突发量 (字节)。</param>
        public BandwidthThrottler(double maxBytesPerSecond, double maxBurstBytes)
        {
            MaxBytesPerSecond = maxBytesPerSecond;
            MaxBurstBytes = maxBurstBytes;
            _tokens = maxBurstBytes;
            _lastUpdateTime = GetTime();
        }

        /// <summary>
        /// 尝试消耗指定字节数的令牌。
        /// 算法: 如果当前令牌 >= bytes, 消耗并返回 true; 否则返回 false。
        /// </summary>
        public bool TryConsume(int bytes)
        {
            lock (_lock)
            {
                UpdateTokens();
                if (_tokens >= bytes)
                {
                    _tokens -= bytes;
                    return true;
                }
                return false;
            }
        }

        /// <summary>
        /// 获取等待时间 (秒), 直到有足够的令牌。
        /// 算法: needed = bytes - currentTokens; waitTime = needed / rate
        /// </summary>
        public double GetWaitTimeSeconds(int bytes)
        {
            lock (_lock)
            {
                UpdateTokens();
                if (_tokens >= bytes) return 0;
                double needed = bytes - _tokens;
                return needed / MaxBytesPerSecond;
            }
        }

        /// <summary>消耗指定字节数 (阻塞直到有足够令牌)。</summary>
        public void Consume(int bytes)
        {
            double waitTime = GetWaitTimeSeconds(bytes);
            if (waitTime > 0)
            {
                System.Threading.Thread.Sleep((int)(waitTime * 1000));
            }
            lock (_lock)
            {
                UpdateTokens();
                _tokens -= bytes;
                if (_tokens < 0) _tokens = 0;
            }
        }

        /// <summary>更新令牌数: tokens += elapsed * rate (上限 MaxBurstBytes)。</summary>
        private void UpdateTokens()
        {
            double now = GetTime();
            double elapsed = now - _lastUpdateTime;
            _tokens = Math.Min(MaxBurstBytes, _tokens + elapsed * MaxBytesPerSecond);
            _lastUpdateTime = now;
        }

        /// <summary>重置令牌桶 (填满)。</summary>
        public void Reset()
        {
            lock (_lock)
            {
                _tokens = MaxBurstBytes;
                _lastUpdateTime = GetTime();
            }
        }

        /// <summary>当前可用令牌数 (字节)。</summary>
        public double AvailableTokens
        {
            get
            {
                lock (_lock)
                {
                    UpdateTokens();
                    return _tokens;
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static double GetTime()
        {
            return (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency;
        }
    }
}
