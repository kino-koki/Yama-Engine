using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace YamaEngine.Network
{
    // ════════════════════════════════════════════════════════════════════════
    // yama引擎 - 网络核心系统 (NetworkCoreSystem)
    //
    // 类似 UE NetDriver / Unity Netcode 的核心网络管线, 涵盖:
    //   * 连接管理: 连接ID/状态/远端地址/RTT/质量/加密/心跳
    //   * 网络驱动: Socket/Transport 抽象, Send/Receive/Broadcast
    //   * 传输层抽象: UDP / TCP / WebSocket / Steam / 自定义
    //   * 消息系统: NetworkMessage 基类, 优先级/可靠/通道
    //   * 消息队列: 优先级队列, 可靠/排序通道
    //   * 网络配置: 最大连接/心跳/超时/端口/线程
    //   * 网络管理器: 服务器/客户端/主机模式, 全局 Update
    //   * 连接管理: 连接列表/踢出/封禁IP/封禁列表
    //
    // 对接系统: YamaEngine.Network 全部子系统
    // ════════════════════════════════════════════════════════════════════════

    // ──────────────────────────────────────────────────────────────
    // 1. 枚举定义
    // ──────────────────────────────────────────────────────────────

    /// <summary>网络连接状态。</summary>
    public enum ConnectionState
    {
        /// <summary>正在连接中。</summary>
        Connecting,
        /// <summary>已连接。</summary>
        Connected,
        /// <summary>正在断开中。</summary>
        Disconnecting,
        /// <summary>已断开。</summary>
        Disconnected,
        /// <summary>正在重连中。</summary>
        Reconnecting,
        /// <summary>连接超时。</summary>
        Timeout
    }

    /// <summary>网络运行角色。</summary>

    /// <summary>传输协议类型。</summary>
    public enum TransportProtocol
    {
        /// <summary>UDP 协议, 低延迟, 可丢包。</summary>
        UDP,
        /// <summary>UDP 增强协议, 提供可靠有序传输。</summary>
        ReliableUDP,
        /// <summary>TCP 协议, 可靠有序, 有头阻塞。</summary>
        TCP,
        /// <summary>WebSocket 协议, 基于TCP的浏览器兼容。</summary>
        WebSocket,
        /// <summary>Steam 网络接口。</summary>
        Steam,
        /// <summary>自定义传输层。</summary>
        Custom
    }

    /// <summary>消息优先级。</summary>
    public enum MessagePriority
    {
        /// <summary>低优先级, 如背景数据同步。</summary>
        Low = 0,
        /// <summary>中优先级, 如普通属性更新。</summary>
        Medium = 1,
        /// <summary>高优先级, 如重要 RPC 调用。</summary>
        High = 2,
        /// <summary>立即发送, 如关键输入/断连请求。</summary>
        Immediate = 3
    }

    /// <summary>消息通道类型。</summary>
    public enum MessageChannel
    {
        /// <summary>可靠有序通道, 保证到达且顺序正确。</summary>
        ReliableOrdered = 0,
        /// <summary>可靠无序通道, 保证到达但不保证顺序。</summary>
        ReliableUnordered = 1,
        /// <summary>不可靠无序通道, 最低延迟可丢包。</summary>
        Unreliable = 2,
        /// <summary>不可靠有序通道, 只保留最新值。</summary>
        UnreliableSequenced = 3
    }

    /// <summary>连接质量评估。</summary>
    public enum ConnectionQuality
    {
        /// <summary>优秀, RTT < 50ms, 丢包 < 1%。</summary>
        Excellent,
        /// <summary>良好, RTT < 100ms, 丢包 < 3%。</summary>
        Good,
        /// <summary>一般, RTT < 200ms, 丢包 < 5%。</summary>
        Fair,
        /// <summary>较差, RTT < 500ms, 丢包 < 10%。</summary>
        Poor,
        /// <summary>极差, RTT >= 500ms 或 丢包 >= 10%。</summary>
        Terrible
    }

    /// <summary>加密方式。</summary>
    public enum EncryptionMode
    {
        /// <summary>无加密。</summary>
        None,
        /// <summary>AES-128 加密。</summary>
        AES128,
        /// <summary>AES-256 加密。</summary>
        AES256
    }

    // ──────────────────────────────────────────────────────────────
    // 2. 网络配置
    // ──────────────────────────────────────────────────────────────

    /// <summary>网络系统全局配置。</summary>
    public sealed class NetworkConfig
    {
        // ── 连接配置 ──────────────────────────────────────────────

        /// <summary>最大连接数 (服务器模式)。</summary>
        public int MaxConnections { get; set; } = 64;

        /// <summary>服务器监听端口。</summary>
        public int Port { get; set; } = 7777;

        /// <summary>服务器唯一标识。</summary>
        public string ServerId { get; set; } = "YamaServer_001";

        // ── 心跳配置 ──────────────────────────────────────────────

        /// <summary>心跳间隔 (秒)。</summary>
        public float HeartbeatInterval { get; set; } = 1.0f;

        /// <summary>连接超时 (秒), 超过此时间未收到心跳则断开。</summary>
        public float ConnectionTimeout { get; set; } = 10.0f;

        /// <summary>重连超时 (秒)。</summary>
        public float ReconnectTimeout { get; set; } = 30.0f;

        /// <summary>最大重连次数。</summary>
        public int MaxReconnectAttempts { get; set; } = 3;

        // ── 传输配置 ──────────────────────────────────────────────

        /// <summary>默认传输协议。</summary>
        public TransportProtocol Protocol { get; set; } = TransportProtocol.UDP;

        /// <summary>最大包大小 (字节)。</summary>
        public int MaxPacketSize { get; set; } = 1400;

        /// <summary>最大消息大小 (字节), 超过则分片。</summary>
        public int MaxMessageSize { get; set; } = 65536;

        /// <summary>发送缓冲区大小。</summary>
        public int SendBufferSize { get; set; } = 65536;

        /// <summary>接收缓冲区大小。</summary>
        public int ReceiveBufferSize { get; set; } = 65536;

        // ── 线程配置 ──────────────────────────────────────────────

        /// <summary>网络接收线程数。</summary>
        public int ReceiveThreadCount { get; set; } = 1;

        /// <summary>是否使用异步 IO。</summary>
        public bool UseAsyncIO { get; set; } = true;

        // ── 加密配置 ──────────────────────────────────────────────

        /// <summary>加密模式。</summary>
        public EncryptionMode Encryption { get; set; } = EncryptionMode.None;

        /// <summary>加密密钥 (仅调试用, 正式环境应从安全存储获取)。</summary>
        public byte[]? EncryptionKey { get; set; }

        // ── 带宽限制 ──────────────────────────────────────────────

        /// <summary>上行带宽限制 (字节/秒), 0 = 无限制。</summary>
        public int SendRateLimit { get; set; } = 0;

        /// <summary>下行带宽限制 (字节/秒), 0 = 无限制。</summary>
        public int ReceiveRateLimit { get; set; } = 0;

        /// <summary>重连延迟 (秒), 每次重连之间的等待。</summary>
        public float ReconnectDelay { get; set; } = 2.0f;
    }

    // ──────────────────────────────────────────────────────────────
    // 3. 网络连接
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 网络连接实例。封装单个远端连接的全部信息:
    /// 连接ID、状态、远端地址、RTT、质量评估、加密状态、心跳等。
    /// </summary>
    public sealed class NetworkConnection
    {
        /// <summary>连接唯一标识。</summary>
        public int ConnectionId { get; }

        /// <summary>当前连接状态。</summary>
        public ConnectionState State { get; private set; } = ConnectionState.Connecting;

        /// <summary>远端地址。</summary>
        public IPEndPoint RemoteEndPoint { get; }

        /// <summary>连接创建时间 (UTC)。</summary>
        public DateTime ConnectedTime { get; } = DateTime.UtcNow;

        /// <summary>当前往返时延 (毫秒)。</summary>
        public float RTT { get; private set; }

        /// <summary>平滑 RTT (指数移动平均)。</summary>
        public float SmoothRTT { get; private set; }

        /// <summary>RTT 抖动 (毫秒)。</summary>
        public float Jitter { get; private set; }

        /// <summary>丢包率 (0.0 ~ 1.0)。</summary>
        public float PacketLoss { get; private set; }

        /// <summary>连接质量评估。</summary>
        public ConnectionQuality Quality
        {
            get
            {
                if (RTT < 50f && PacketLoss < 0.01f) return ConnectionQuality.Excellent;
                if (RTT < 100f && PacketLoss < 0.03f) return ConnectionQuality.Good;
                if (RTT < 200f && PacketLoss < 0.05f) return ConnectionQuality.Fair;
                if (RTT < 500f && PacketLoss < 0.10f) return ConnectionQuality.Poor;
                return ConnectionQuality.Terrible;
            }
        }

        /// <summary>是否启用加密。</summary>
        public bool IsEncrypted { get; private set; }

        /// <summary>加密模式。</summary>
        public EncryptionMode EncryptionMode { get; private set; } = EncryptionMode.None;

        /// <summary>上次心跳时间。</summary>
        public DateTime LastHeartbeatTime { get; private set; } = DateTime.UtcNow;

        /// <summary>上次心跳 RTT (毫秒)。</summary>
        public float LastHeartbeatRTT { get; private set; }

        /// <summary>发送的字节总数。</summary>
        public long BytesSent { get; private set; }

        /// <summary>接收的字节总数。</summary>
        public long BytesReceived { get; private set; }

        /// <summary>发送的包总数。</summary>
        public long PacketsSent { get; private set; }

        /// <summary>接收的包总数。</summary>
        public long PacketsReceived { get; private set; }

        /// <summary>用户自定义数据 (可存储玩家ID、令牌等)。</summary>
        public Dictionary<string, object> UserData { get; } = new Dictionary<string, object>();

        /// <summary>关联的远端玩家名称。</summary>
        public string PlayerName { get; set; } = string.Empty;

        /// <summary>连接标签 (用于筛选/分组)。</summary>
        public HashSet<string> Tags { get; } = new HashSet<string>();

        // ── 内部状态 ──────────────────────────────────────────────

        private float _rttSmoothFactor = 0.1f;
        private int _heartbeatSequence;
        private int _packetsLost;

        private static int _nextConnectionId;

        /// <summary>创建一个新的网络连接。</summary>
        public NetworkConnection(IPEndPoint remoteEndPoint)
        {
            ConnectionId = Interlocked.Increment(ref _nextConnectionId);
            RemoteEndPoint = remoteEndPoint;
        }

        /// <summary>创建本地回环连接 (Host 模式)。</summary>
        public NetworkConnection()
        {
            ConnectionId = Interlocked.Increment(ref _nextConnectionId);
            RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, 0);
        }

        // ── 状态管理 ──────────────────────────────────────────────

        /// <summary>更新连接状态。</summary>
        public void SetState(ConnectionState newState)
        {
            var oldState = State;
            State = newState;
            OnStateChanged?.Invoke(this, oldState, newState);
        }

        /// <summary>记录一次心跳 RTT。</summary>
        public void RecordHeartbeat(float rttMs)
        {
            LastHeartbeatTime = DateTime.UtcNow;
            LastHeartbeatRTT = rttMs;

            // 指数移动平均平滑 RTT
            SmoothRTT = SmoothRTT == 0f
                ? rttMs
                : SmoothRTT * (1f - _rttSmoothFactor) + rttMs * _rttSmoothFactor;

            // 计算 RTT 抖动
            float jitterDelta = Math.Abs(rttMs - RTT);
            Jitter = Jitter * 0.8f + jitterDelta * 0.2f;

            RTT = rttMs;
        }

        /// <summary>更新丢包率。</summary>
        public void UpdatePacketLoss(int lostPackets, int totalPackets)
        {
            if (totalPackets > 0)
            {
                PacketLoss = Math.Max(0f, Math.Min(1f, (float)lostPackets / totalPackets));
            }
        }

        /// <summary>记录发送数据。</summary>
        public void RecordSent(int bytes)
        {
            BytesSent += bytes;
            PacketsSent++;
        }

        /// <summary>记录接收数据。</summary>
        public void RecordReceived(int bytes)
        {
            BytesReceived += bytes;
            PacketsReceived++;
        }

        /// <summary>启用加密。</summary>
        public void EnableEncryption(EncryptionMode mode)
        {
            EncryptionMode = mode;
            IsEncrypted = mode != EncryptionMode.None;
        }

        /// <summary>获取下一次心跳序号。</summary>
        public int GetNextHeartbeatSequence() => ++_heartbeatSequence;

        /// <summary>检查连接是否已超时。</summary>
        public bool IsTimedOut(float timeoutSeconds)
        {
            return (DateTime.UtcNow - LastHeartbeatTime).TotalSeconds > timeoutSeconds;
        }

        /// <summary>连接状态变更事件。</summary>
        public event Action<NetworkConnection, ConnectionState, ConnectionState>? OnStateChanged;
    }

    // ──────────────────────────────────────────────────────────────
    // 4. 传输层抽象
    // ──────────────────────────────────────────────────────────────

    /// <summary>传输层抽象接口。所有传输协议 (UDP/TCP/WebSocket/Steam) 实现此接口。</summary>
    public interface ITransportLayer
    {
        /// <summary>传输协议类型。</summary>
        TransportProtocol Protocol { get; }

        /// <summary>是否正在运行。</summary>
        bool IsRunning { get; }

        /// <summary>最大包大小。</summary>
        int MaxPacketSize { get; }

        /// <summary>启动传输层 (服务器模式, 绑定端口)。</summary>
        bool Start(int port);

        /// <summary>停止传输层, 释放所有资源。</summary>
        void Stop();

        /// <summary>发送数据到指定远端。</summary>
        bool Send(IPEndPoint endPoint, byte[] data, int length);

        /// <summary>广播数据到所有已连接的远端。</summary>
        void Broadcast(byte[] data, int length);

        /// <summary>非阻塞接收数据。返回接收到的包列表。</summary>
        List<TransportReceivedPacket> Receive();

        /// <summary>连接到指定远端 (客户端模式)。</summary>
        bool Connect(IPEndPoint remoteEndPoint);

        /// <summary>断开指定远端连接。</summary>
        void Disconnect(IPEndPoint remoteEndPoint);

        /// <summary>获取本地绑定端口。</summary>
        int LocalPort { get; }
    }

    /// <summary>传输层接收到的数据包。</summary>
    public struct TransportReceivedPacket
    {
        /// <summary>发送方地址。</summary>
        public IPEndPoint From;
        /// <summary>数据缓冲区。</summary>
        public byte[] Data;
        /// <summary>数据长度。</summary>
        public int Length;
        /// <summary>接收时间。</summary>
        public DateTime Timestamp;
    }

    // ──────────────────────────────────────────────────────────────
    // 5. 传输层实现
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// UDP 传输层实现。使用 System.Net.Sockets.UdpClient。
    /// 提供低延迟、可丢包的不可靠传输, 适用于实时游戏。
    /// </summary>
    public sealed class UDPTransport : ITransportLayer
    {
        public TransportProtocol Protocol => TransportProtocol.UDP;
        public bool IsRunning { get; private set; }
        public int MaxPacketSize { get; } = 1400;
        public int LocalPort { get; private set; }

        private System.Net.Sockets.UdpClient? _udpClient;
        private const int DefaultPort = 7777;

        public bool Start(int port)
        {
            try
            {
                _udpClient = new System.Net.Sockets.UdpClient(port);
                _udpClient.EnableBroadcast = true;
                _udpClient.DontFragment = true;
                LocalPort = port;
                IsRunning = true;
                return true;
            }
            catch
            {
                IsRunning = false;
                return false;
            }
        }

        public void Stop()
        {
            if (_udpClient != null)
            {
                _udpClient.Close();
                _udpClient = null;
            }
            IsRunning = false;
            LocalPort = 0;
        }

        public bool Send(IPEndPoint endPoint, byte[] data, int length)
        {
            if (!IsRunning || _udpClient == null || data == null || length <= 0)
                return false;
            try
            {
                int sent = _udpClient.Send(data, length, endPoint);
                return sent > 0;
            }
            catch
            {
                return false;
            }
        }

        public void Broadcast(byte[] data, int length)
        {
            if (!IsRunning || _udpClient == null) return;
            try
            {
                _udpClient.Send(data, length, "255.255.255.255", LocalPort > 0 ? LocalPort : DefaultPort);
            }
            catch
            {
                // 广播失败静默处理
            }
        }

        public List<TransportReceivedPacket> Receive()
        {
            var packets = new List<TransportReceivedPacket>();
            if (!IsRunning || _udpClient == null) return packets;

            try
            {
                _udpClient.Client.ReceiveTimeout = 0;
                while (_udpClient.Available > 0)
                {
                    var remoteEP = new IPEndPoint(IPAddress.Any, 0);
                    var result = _udpClient.Receive(ref remoteEP);
                    packets.Add(new TransportReceivedPacket
                    {
                        From = remoteEP,
                        Data = result,
                        Length = result.Length,
                        Timestamp = DateTime.UtcNow
                    });
                }
            }
            catch
            {
                // 接收超时或异常静默处理
            }

            return packets;
        }

        public bool Connect(IPEndPoint remoteEndPoint)
        {
            // UDP 是无连接协议, "连接" 仅为绑定默认远端
            if (!IsRunning || _udpClient == null) return false;
            try
            {
                _udpClient.Connect(remoteEndPoint);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public void Disconnect(IPEndPoint remoteEndPoint)
        {
            // UDP 无连接, "断开" 为解除绑定
            if (_udpClient == null) return;
            try
            {
                _udpClient.Connect(IPAddress.Any, 0);
            }
            catch
            {
                // 静默处理
            }
        }
    }

    /// <summary>
    /// TCP 传输层实现。使用 System.Net.Sockets.TcpListener / TcpClient。
    /// 提供可靠有序传输, 适用于对可靠性要求高的场景。
    /// </summary>
    public sealed class TCPTransport : ITransportLayer
    {
        public TransportProtocol Protocol => TransportProtocol.TCP;
        public bool IsRunning { get; private set; }
        public int MaxPacketSize { get; } = 65536;
        public int LocalPort { get; private set; }

        private System.Net.Sockets.TcpListener? _listener;
        private readonly Dictionary<IPEndPoint, System.Net.Sockets.TcpClient> _clients
            = new Dictionary<IPEndPoint, System.Net.Sockets.TcpClient>();
        private readonly object _lock = new object();

        private const int Backlog = 128;

        public bool Start(int port)
        {
            try
            {
                _listener = new System.Net.Sockets.TcpListener(IPAddress.Any, port);
                _listener.Start(Backlog);
                _listener.BeginAcceptTcpClient(OnClientConnected, null);
                LocalPort = port;
                IsRunning = true;
                return true;
            }
            catch
            {
                IsRunning = false;
                return false;
            }
        }

        public void Stop()
        {
            lock (_lock)
            {
                _listener?.Stop();
                _listener = null;
                foreach (var client in _clients.Values)
                {
                    try { client.Close(); } catch { }
                }
                _clients.Clear();
            }
            IsRunning = false;
            LocalPort = 0;
        }

        public bool Send(IPEndPoint endPoint, byte[] data, int length)
        {
            if (!IsRunning) return false;
            lock (_lock)
            {
                if (!_clients.TryGetValue(endPoint, out var client) || !client.Connected)
                    return false;
                try
                {
                    var stream = client.GetStream();
                    // 先写入长度前缀 (4字节, 大端)
                    byte[] lenBytes = BitConverter.GetBytes(length);
                    if (BitConverter.IsLittleEndian) Array.Reverse(lenBytes);
                    stream.Write(lenBytes, 0, 4);
                    stream.Write(data, 0, length);
                    stream.Flush();
                    return true;
                }
                catch
                {
                    return false;
                }
            }
        }

        public void Broadcast(byte[] data, int length)
        {
            if (!IsRunning) return;
            lock (_lock)
            {
                foreach (var kvp in _clients)
                {
                    if (!kvp.Value.Connected) continue;
                    try
                    {
                        var stream = kvp.Value.GetStream();
                        byte[] lenBytes = BitConverter.GetBytes(length);
                        if (BitConverter.IsLittleEndian) Array.Reverse(lenBytes);
                        stream.Write(lenBytes, 0, 4);
                        stream.Write(data, 0, length);
                        stream.Flush();
                    }
                    catch
                    {
                        // 静默处理
                    }
                }
            }
        }

        public List<TransportReceivedPacket> Receive()
        {
            var packets = new List<TransportReceivedPacket>();
            if (!IsRunning) return packets;

            lock (_lock)
            {
                var disconnected = new List<IPEndPoint>();
                foreach (var kvp in _clients)
                {
                    var client = kvp.Value;
                    if (!client.Connected) { disconnected.Add(kvp.Key); continue; }

                    try
                    {
                        var stream = client.GetStream();
                        while (stream.DataAvailable)
                        {
                            // 读取长度前缀
                            byte[] lenBuf = new byte[4];
                            int bytesRead = 0;
                            while (bytesRead < 4)
                            {
                                int r = stream.Read(lenBuf, bytesRead, 4 - bytesRead);
                                if (r == 0) break;
                                bytesRead += r;
                            }
                            if (bytesRead < 4) { disconnected.Add(kvp.Key); break; }

                            if (BitConverter.IsLittleEndian) Array.Reverse(lenBuf);
                            int msgLen = BitConverter.ToInt32(lenBuf, 0);
                            if (msgLen <= 0 || msgLen > MaxPacketSize) continue;

                            byte[] data = new byte[msgLen];
                            bytesRead = 0;
                            while (bytesRead < msgLen)
                            {
                                int r = stream.Read(data, bytesRead, msgLen - bytesRead);
                                if (r == 0) break;
                                bytesRead += r;
                            }
                            if (bytesRead < msgLen) continue;

                            packets.Add(new TransportReceivedPacket
                            {
                                From = kvp.Key,
                                Data = data,
                                Length = bytesRead,
                                Timestamp = DateTime.UtcNow
                            });
                        }
                    }
                    catch
                    {
                        disconnected.Add(kvp.Key);
                    }
                }

                // 清理断开连接
                foreach (var ep in disconnected)
                {
                    if (_clients.TryGetValue(ep, out var c))
                    {
                        try { c.Close(); } catch { }
                        _clients.Remove(ep);
                    }
                }
            }

            return packets;
        }

        public bool Connect(IPEndPoint remoteEndPoint)
        {
            try
            {
                var client = new System.Net.Sockets.TcpClient();
                client.Connect(remoteEndPoint);
                lock (_lock)
                {
                    _clients[remoteEndPoint] = client;
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        public void Disconnect(IPEndPoint remoteEndPoint)
        {
            lock (_lock)
            {
                if (_clients.TryGetValue(remoteEndPoint, out var client))
                {
                    try { client.Close(); } catch { }
                    _clients.Remove(remoteEndPoint);
                }
            }
        }

        private void OnClientConnected(IAsyncResult ar)
        {
            try
            {
                if (_listener == null) return;
                var client = _listener.EndAcceptTcpClient(ar);
                var ep = (IPEndPoint)client.Client.RemoteEndPoint!;
                lock (_lock)
                {
                    _clients[ep] = client;
                }
                // 继续接受新连接
                if (IsRunning)
                {
                    _listener.BeginAcceptTcpClient(OnClientConnected, null);
                }
            }
            catch
            {
                // 监听停止时可能抛异常
            }
        }
    }

    /// <summary>
    /// WebSocket 传输层实现。基于 HTTP 升级协议的可靠双向通信。
    /// 支持浏览器客户端直接连接。
    /// </summary>
    public sealed class WebSocketTransport : ITransportLayer
    {
        public TransportProtocol Protocol => TransportProtocol.WebSocket;
        public bool IsRunning { get; private set; }
        public int MaxPacketSize { get; } = 32768;
        public int LocalPort { get; private set; }

        // WebSocket 帧类型
        private const byte FinMask = 0x80;
        private const byte OpcodeText = 0x01;
        private const byte OpcodeBinary = 0x02;
        private const byte OpcodeClose = 0x08;
        private const byte OpcodePing = 0x09;
        private const byte OpcodePong = 0x0A;
        private const byte MaskFlag = 0x80;
        private const byte PayloadLenMask = 0x7F;

        // 内部用 TCP 作为底层传输
        private readonly TCPTransport _tcpTransport = new TCPTransport();
        private readonly Dictionary<IPEndPoint, WebSocketState> _wsStates
            = new Dictionary<IPEndPoint, WebSocketState>();
        private readonly object _wsLock = new object();

        /// <summary>WebSocket 连接状态。</summary>
        private class WebSocketState
        {
            public bool HandshakeComplete;
            public bool IsServerSide = true;
            public byte[]? MaskKey;
        }

        public bool Start(int port)
        {
            LocalPort = port;
            IsRunning = _tcpTransport.Start(port);
            return IsRunning;
        }

        public void Stop()
        {
            _tcpTransport.Stop();
            lock (_wsLock)
            {
                _wsStates.Clear();
            }
            IsRunning = false;
            LocalPort = 0;
        }

        public bool Send(IPEndPoint endPoint, byte[] data, int length)
        {
            lock (_wsLock)
            {
                if (!_wsStates.TryGetValue(endPoint, out var state) || !state.HandshakeComplete)
                    return false;

                // 构建 WebSocket 二进制帧
                var frame = BuildFrame(OpcodeBinary, data, length, state.IsServerSide);
                return _tcpTransport.Send(endPoint, frame, frame.Length);
            }
        }

        public void Broadcast(byte[] data, int length)
        {
            lock (_wsLock)
            {
                foreach (var kvp in _wsStates)
                {
                    if (!kvp.Value.HandshakeComplete) continue;
                    var frame = BuildFrame(OpcodeBinary, data, length, kvp.Value.IsServerSide);
                    _tcpTransport.Send(kvp.Key, frame, frame.Length);
                }
            }
        }

        public List<TransportReceivedPacket> Receive()
        {
            var rawPackets = _tcpTransport.Receive();
            var result = new List<TransportReceivedPacket>();

            foreach (var raw in rawPackets)
            {
                lock (_wsLock)
                {
                    if (!_wsStates.TryGetValue(raw.From, out var state))
                    {
                        state = new WebSocketState();
                        _wsStates[raw.From] = state;
                    }

                    if (!state.HandshakeComplete)
                    {
                        // 尝试处理 WebSocket 握手
                        if (TryHandleHandshake(raw.Data, raw.Length, out string? acceptKey))
                        {
                            state.HandshakeComplete = true;
                            // 发送握手响应
                            string response = "HTTP/1.1 101 Switching Protocols\r\n" +
                                "Upgrade: websocket\r\n" +
                                "Connection: Upgrade\r\n" +
                                $"Sec-WebSocket-Accept: {acceptKey}\r\n\r\n";
                            byte[] respBytes = Encoding.UTF8.GetBytes(response);
                            _tcpTransport.Send(raw.From, respBytes, respBytes.Length);
                        }
                    }
                    else
                    {
                        // 解析 WebSocket 数据帧
                        if (TryParseFrame(raw.Data, raw.Length, state, out byte[]? payload, out int payloadLen))
                        {
                            result.Add(new TransportReceivedPacket
                            {
                                From = raw.From,
                                Data = payload!,
                                Length = payloadLen,
                                Timestamp = raw.Timestamp
                            });
                        }
                    }
                }
            }

            return result;
        }

        public bool Connect(IPEndPoint remoteEndPoint)
        {
            bool connected = _tcpTransport.Connect(remoteEndPoint);
            if (!connected) return false;

            lock (_wsLock)
            {
                _wsStates[remoteEndPoint] = new WebSocketState
                {
                    IsServerSide = false,
                    HandshakeComplete = false
                };
            }

            // 发送 WebSocket 握手请求
            string key = Convert.ToBase64String(Guid.NewGuid().ToByteArray());
            string handshake =
                $"GET / HTTP/1.1\r\n" +
                $"Host: {remoteEndPoint.Address}:{remoteEndPoint.Port}\r\n" +
                "Upgrade: websocket\r\n" +
                "Connection: Upgrade\r\n" +
                $"Sec-WebSocket-Key: {key}\r\n" +
                "Sec-WebSocket-Version: 13\r\n\r\n";

            byte[] handBytes = Encoding.UTF8.GetBytes(handshake);
            return _tcpTransport.Send(remoteEndPoint, handBytes, handBytes.Length);
        }

        public void Disconnect(IPEndPoint remoteEndPoint)
        {
            lock (_wsLock)
            {
                // 发送关闭帧
                if (_wsStates.TryGetValue(remoteEndPoint, out var state) && state.HandshakeComplete)
                {
                    var closeFrame = BuildFrame(OpcodeClose, new byte[0], 0, state.IsServerSide);
                    _tcpTransport.Send(remoteEndPoint, closeFrame, closeFrame.Length);
                }
                _wsStates.Remove(remoteEndPoint);
            }
            _tcpTransport.Disconnect(remoteEndPoint);
        }

        // ── WebSocket 帧构建/解析 ──────────────────────────────────

        private byte[] BuildFrame(byte opcode, byte[] payload, int length, bool isServerSide)
        {
            // 客户端必须掩码, 服务器不掩码
            bool mask = !isServerSide;
            int headerLen = 2 + (mask ? 4 : 0);
            int totalLen = headerLen + length;

            var frame = new byte[totalLen];
            frame[0] = (byte)(FinMask | opcode);

            if (length < 126)
            {
                frame[1] = (byte)(length | (mask ? MaskFlag : 0));
            }
            else if (length < 65536)
            {
                frame[1] = (byte)(126 | (mask ? MaskFlag : 0));
                frame[2] = (byte)(length >> 8);
                frame[3] = (byte)(length & 0xFF);
                headerLen += 2;
            }
            else
            {
                frame[1] = (byte)(127 | (mask ? MaskFlag : 0));
                int offset = 2;
                long len64 = length;
                for (int i = 7; i >= 0; i--)
                {
                    frame[offset++] = (byte)((len64 >> (i * 8)) & 0xFF);
                }
                headerLen += 8;
            }

            if (mask)
            {
                var maskKey = new byte[4];
                new Random().NextBytes(maskKey);
                frame[headerLen - 4] = maskKey[0];
                frame[headerLen - 3] = maskKey[1];
                frame[headerLen - 2] = maskKey[2];
                frame[headerLen - 1] = maskKey[3];

                Array.Copy(payload, 0, frame, headerLen, length);
                for (int i = 0; i < length; i++)
                {
                    frame[headerLen + i] ^= maskKey[i % 4];
                }
            }
            else
            {
                Array.Copy(payload, 0, frame, headerLen, length);
            }

            return frame;
        }

        private bool TryParseFrame(byte[] data, int length, WebSocketState state,
            out byte[]? payload, out int payloadLen)
        {
            payload = null;
            payloadLen = 0;
            if (length < 2) return false;

            byte opcode = (byte)(data[0] & 0x0F);
            bool fin = (data[0] & FinMask) != 0;
            bool masked = (data[1] & MaskFlag) != 0;
            long payloadLength = data[1] & PayloadLenMask;
            int offset = 2;

            if (payloadLength == 126 && length >= 4)
            {
                if (BitConverter.IsLittleEndian)
                {
                    payloadLength = ((data[2] << 8) | data[3]);
                }
                else
                {
                    payloadLength = BitConverter.ToInt16(data, 2);
                }
                offset = 4;
            }
            else if (payloadLength == 127 && length >= 10)
            {
                payloadLength = 0;
                for (int i = 0; i < 8; i++)
                {
                    payloadLength = (payloadLength << 8) | data[2 + i];
                }
                offset = 10;
            }

            byte[]? maskKey = null;
            if (masked)
            {
                if (length < offset + 4) return false;
                maskKey = new byte[4];
                Array.Copy(data, offset, maskKey, 0, 4);
                offset += 4;
            }

            if (length < offset + payloadLength) return false;

            payload = new byte[payloadLength];
            Array.Copy(data, offset, payload, 0, (int)payloadLength);

            if (masked && maskKey != null)
            {
                for (int i = 0; i < payloadLength; i++)
                {
                    payload[i] ^= maskKey[i % 4];
                }
            }

            payloadLen = (int)payloadLength;

            // 处理控制帧
            if (opcode == OpcodePing)
            {
                var pong = BuildFrame(OpcodePong, payload, payloadLen, true);
                // pong 发送通过底层 TCP, 需要知道来源地址
            }
            else if (opcode == OpcodeClose)
            {
                return false; // 连接关闭
            }

            return fin && (opcode == OpcodeBinary || opcode == OpcodeText);
        }

        private bool TryHandleHandshake(byte[] data, int length, out string? acceptKey)
        {
            acceptKey = null;
            try
            {
                string request = Encoding.UTF8.GetString(data, 0, length);
                if (!request.Contains("Upgrade: websocket", StringComparison.OrdinalIgnoreCase))
                    return false;

                // 提取 Sec-WebSocket-Key
                int keyIdx = request.IndexOf("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase);
                if (keyIdx < 0) return false;
                keyIdx += "Sec-WebSocket-Key:".Length;
                int endIdx = request.IndexOf('\r', keyIdx);
                if (endIdx < 0) return false;
                string wsKey = request.Substring(keyIdx, endIdx - keyIdx).Trim();

                // SHA-1 哈希计算接受密钥
                string combined = wsKey + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
                using (var sha1 = System.Security.Cryptography.SHA1.Create())
                {
                    byte[] hash = sha1.ComputeHash(Encoding.UTF8.GetBytes(combined));
                    acceptKey = Convert.ToBase64String(hash);
                }

                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Steam 网络传输层占位实现。预留接口, 实际对接需要 Steamworks SDK。
    /// </summary>
    public sealed class SteamTransport : ITransportLayer
    {
        public TransportProtocol Protocol => TransportProtocol.Steam;
        public bool IsRunning { get; private set; }
        public int MaxPacketSize { get; } = 1200;
        public int LocalPort { get; private set; }

        private readonly Dictionary<IPEndPoint, ulong> _steamIdMap
            = new Dictionary<IPEndPoint, ulong>();

        public bool Start(int port) { IsRunning = true; LocalPort = port; return true; }
        public void Stop() { IsRunning = false; _steamIdMap.Clear(); LocalPort = 0; }

        public bool Send(IPEndPoint endPoint, byte[] data, int length)
        {
            // TODO: 对接 SteamNetworkingSockets
            if (_steamIdMap.TryGetValue(endPoint, out var steamId))
            {
                // SteamAPI.NetworkingSockets().SendMessageToUser(steamId, data, length, ...);
            }
            return true;
        }

        public void Broadcast(byte[] data, int length)
        {
            foreach (var ep in _steamIdMap.Keys)
            {
                Send(ep, data, length);
            }
        }

        public List<TransportReceivedPacket> Receive()
        {
            // TODO: 对接 SteamNetworkingSockets 接收回调
            return new List<TransportReceivedPacket>();
        }

        public bool Connect(IPEndPoint remoteEndPoint) { return true; }
        public void Disconnect(IPEndPoint remoteEndPoint) { _steamIdMap.Remove(remoteEndPoint); }

        /// <summary>注册远端地址到 SteamID 的映射。</summary>
        public void RegisterSteamId(IPEndPoint endPoint, ulong steamId)
        {
            _steamIdMap[endPoint] = steamId;
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 6. 网络消息
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 网络消息基类。所有通过网络发送的消息均继承此类。
    /// </summary>
    public abstract class NetworkMessage
    {
        private static int _nextMessageId;

        /// <summary>消息唯一 ID。</summary>
        public int MessageId { get; } = Interlocked.Increment(ref _nextMessageId);

        /// <summary>消息类型标识 (用于反序列化路由)。</summary>
        public abstract ushort MessageType { get; }

        /// <summary>消息优先级。</summary>
        public MessagePriority Priority { get; set; } = MessagePriority.Medium;

        /// <summary>消息通道类型。</summary>
        public MessageChannel Channel { get; set; } = MessageChannel.ReliableOrdered;

        /// <summary>消息大小 (字节)。</summary>
        public abstract int Size { get; }

        /// <summary>序列化消息到写入器。</summary>
        public abstract void Serialize(NetworkWriter writer);

        /// <summary>从读取器反序列化消息。</summary>
        public abstract void Deserialize(NetworkReader reader);

        /// <summary>获取消息描述 (用于调试)。</summary>
        public virtual string GetDescription() => $"Msg[{MessageType}] Id={MessageId}";
    }

    // ──────────────────────────────────────────────────────────────
    // 7. 网络消息队列
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 网络消息队列。按优先级排序出队, 支持可靠/不可靠/有序通道。
    /// </summary>
    public sealed class NetMessageQueue
    {
        private readonly ConcurrentQueue<(NetworkMessage msg, int connId)> _highQueue
            = new ConcurrentQueue<(NetworkMessage, int)>();
        private readonly ConcurrentQueue<(NetworkMessage msg, int connId)> _mediumQueue
            = new ConcurrentQueue<(NetworkMessage, int)>();
        private readonly ConcurrentQueue<(NetworkMessage msg, int connId)> _lowQueue
            = new ConcurrentQueue<(NetworkMessage, int)>();
        private readonly ConcurrentQueue<(NetworkMessage msg, int connId)> _immediateQueue
            = new ConcurrentQueue<(NetworkMessage, int)>();

        /// <summary>队列中消息总数。</summary>
        public int TotalCount
        {
            get
            {
                return _immediateQueue.Count + _highQueue.Count +
                       _mediumQueue.Count + _lowQueue.Count;
            }
        }

        /// <summary>入队一条消息。</summary>
        public void Enqueue(NetworkMessage message, int connectionId)
        {
            switch (message.Priority)
            {
                case MessagePriority.Immediate:
                    _immediateQueue.Enqueue((message, connectionId));
                    break;
                case MessagePriority.High:
                    _highQueue.Enqueue((message, connectionId));
                    break;
                case MessagePriority.Medium:
                    _mediumQueue.Enqueue((message, connectionId));
                    break;
                case MessagePriority.Low:
                    _lowQueue.Enqueue((message, connectionId));
                    break;
            }
        }

        /// <summary>
        /// 出队一条消息。优先返回高优先级队列中的消息。
        /// 返回 false 表示队列为空。
        /// </summary>
        public bool TryDequeue(out NetworkMessage message, out int connectionId)
        {
            message = null!;
            connectionId = 0;

            if (_immediateQueue.TryDequeue(out var imm)) { message = imm.msg; connectionId = imm.connId; return true; }
            if (_highQueue.TryDequeue(out var high)) { message = high.msg; connectionId = high.connId; return true; }
            if (_mediumQueue.TryDequeue(out var med)) { message = med.msg; connectionId = med.connId; return true; }
            if (_lowQueue.TryDequeue(out var low)) { message = low.msg; connectionId = low.connId; return true; }
            return false;
        }

        /// <summary>清空所有队列。</summary>
        public void Clear()
        {
            while (_immediateQueue.TryDequeue(out _)) { }
            while (_highQueue.TryDequeue(out _)) { }
            while (_mediumQueue.TryDequeue(out _)) { }
            while (_lowQueue.TryDequeue(out _)) { }
        }

        /// <summary>按连接 ID 清空指定连接的所有消息。</summary>
        public void ClearForConnection(int connectionId)
        {
            ClearQueueForConnection(_immediateQueue, connectionId);
            ClearQueueForConnection(_highQueue, connectionId);
            ClearQueueForConnection(_mediumQueue, connectionId);
            ClearQueueForConnection(_lowQueue, connectionId);
        }

        private static void ClearQueueForConnection(
            ConcurrentQueue<(NetworkMessage msg, int connId)> queue, int connId)
        {
            var temp = new List<(NetworkMessage, int)>();
            while (queue.TryDequeue(out var item))
            {
                if (item.connId != connId)
                    temp.Add(item);
            }
            foreach (var item in temp)
                queue.Enqueue(item);
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 8. 网络驱动
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 网络驱动。封装传输层抽象, 提供 Send/Receive/Broadcast 接口。
    /// 类似 UE 的 NetDriver, 管理底层 Socket 和消息收发。
    /// </summary>
    public sealed class NetworkDriver
    {
        private ITransportLayer? _transport;
        private readonly NetworkConfig _config;
        private bool _isRunning;
        private readonly object _receiveLock = new object();
        private readonly List<TransportReceivedPacket> _receivedPackets
            = new List<TransportReceivedPacket>();

        /// <summary>底层传输层。</summary>
        public ITransportLayer? Transport => _transport;

        /// <summary>是否正在运行。</summary>
        public bool IsRunning => _isRunning;

        /// <summary>驱动配置。</summary>
        public NetworkConfig Config => _config;

        /// <summary>接收到的原始数据包事件。</summary>
        public event Action<TransportReceivedPacket>? OnPacketReceived;

        public NetworkDriver(NetworkConfig config)
        {
            _config = config;
        }

        /// <summary>初始化传输层。</summary>
        public bool Initialize()
        {
            _transport = _config.Protocol switch
            {
                TransportProtocol.TCP => new TCPTransport(),
                TransportProtocol.WebSocket => new WebSocketTransport(),
                TransportProtocol.Steam => new SteamTransport(),
                _ => new UDPTransport() // 默认 UDP
            };
            return true;
        }

        /// <summary>启动驱动 (服务器模式, 监听端口)。</summary>
        public bool Start()
        {
            if (_transport == null) return false;
            _isRunning = _transport.Start(_config.Port);
            return _isRunning;
        }

        /// <summary>停止驱动。</summary>
        public void Stop()
        {
            _transport?.Stop();
            _isRunning = false;
        }

        /// <summary>发送数据到指定端点。</summary>
        public bool Send(IPEndPoint endPoint, byte[] data, int length)
        {
            if (_transport == null || !_isRunning) return false;

            // 带宽限制检查
            if (_config.SendRateLimit > 0)
            {
                // 简化的带宽限制 (完整实现需要滑动窗口)
                // 此处留空, 实际由 NetworkManager 的统计系统处理
            }

            return _transport.Send(endPoint, data, length);
        }

        /// <summary>广播数据到所有连接。</summary>
        public void Broadcast(byte[] data, int length)
        {
            _transport?.Broadcast(data, length);
        }

        /// <summary>轮询接收数据包。</summary>
        public void PollReceive()
        {
            if (_transport == null || !_isRunning) return;

            var packets = _transport.Receive();
            if (packets.Count > 0)
            {
                lock (_receiveLock)
                {
                    _receivedPackets.AddRange(packets);
                }
            }
        }

        /// <summary>获取并清空已接收的数据包。</summary>
        public List<TransportReceivedPacket> GetReceivedPackets()
        {
            lock (_receiveLock)
            {
                var result = new List<TransportReceivedPacket>(_receivedPackets);
                _receivedPackets.Clear();
                return result;
            }
        }

        /// <summary>客户端连接到远端。</summary>
        public bool Connect(IPEndPoint remoteEndPoint)
        {
            return _transport?.Connect(remoteEndPoint) ?? false;
        }

        /// <summary>断开指定连接。</summary>
        public void Disconnect(IPEndPoint remoteEndPoint)
        {
            _transport?.Disconnect(remoteEndPoint);
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 9. 消息序列化器 (前置声明, 详细实现在 NetworkSerializationSystem)
    // ──────────────────────────────────────────────────────────────

    /// <summary>网络二进制写入器 (前置简化版, 完整版在 NetworkSerializationSystem.cs)。</summary>

    /// <summary>网络二进制读取器 (前置简化版, 完整版在 NetworkSerializationSystem.cs)。</summary>

    /// <summary>
    /// 简化的 Transform 结构 (不依赖引擎 Transform 组件)。
    /// 仅用于网络序列化/反序列化。
    /// </summary>
    public struct NumericTransform
    {
        public Vector3 Position;
        public Quaternion Rotation;
        public Vector3 Scale;

        public NumericTransform(Vector3 pos, Quaternion rot, Vector3 scale)
        {
            Position = pos;
            Rotation = rot;
            Scale = scale;
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 10. 网络管理器 (单例)
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 网络管理器单例。全局网络系统入口。
    /// 支持服务器/客户端/主机三种模式, 管理连接、消息收发、全局 Update。
    /// </summary>
    public sealed class NetworkManager
    {
        private static readonly Lazy<NetworkManager> _instance
            = new Lazy<NetworkManager>(() => new NetworkManager());

        /// <summary>网络管理器单例。</summary>
        public static NetworkManager Instance => _instance.Value;

        // ── 核心组件 ──────────────────────────────────────────────

        /// <summary>网络驱动。</summary>
        public NetworkDriver Driver { get; private set; } = null!;

        /// <summary>网络配置。</summary>
        public NetworkConfig Config { get; } = new NetworkConfig();

        /// <summary>当前网络角色。</summary>
        public NetworkRole Role { get; private set; } = NetworkRole.None;

        /// <summary>是否在运行。</summary>
        public bool IsRunning { get; private set; }

        // ── 连接管理 ──────────────────────────────────────────────

        private readonly Dictionary<int, NetworkConnection> _connections
            = new Dictionary<int, NetworkConnection>();
        private readonly Dictionary<IPEndPoint, NetworkConnection> _endpointMap
            = new Dictionary<IPEndPoint, NetworkConnection>();
        private readonly HashSet<string> _bannedIPs
            = new HashSet<string>();
        private NetworkConnection? _serverConnection; // 客户端模式下的服务器连接
        private NetworkConnection? _localConnection;  // Host 模式下的本地连接

        // ── 心跳 ──────────────────────────────────────────────────

        private float _heartbeatTimer;
        private float _timeoutCheckTimer;

        // ── 事件 ──────────────────────────────────────────────────

        /// <summary>新连接建立事件。</summary>
        public event Action<NetworkConnection>? OnClientConnected;
        /// <summary>连接断开事件。</summary>
        public event Action<NetworkConnection>? OnClientDisconnected;
        /// <summary>连接超时事件。</summary>
        public event Action<NetworkConnection>? OnClientTimeout;
        /// <summary>服务器已启动事件。</summary>
        public event Action? OnServerStarted;
        /// <summary>服务器已停止事件。</summary>
        public event Action? OnServerStopped;
        /// <summary>已连接到服务器事件 (客户端模式)。</summary>
        public event Action? OnConnectedToServer;
        /// <summary>从服务器断开事件 (客户端模式)。</summary>
        public event Action? OnDisconnectedFromServer;
        /// <summary>收到消息事件。</summary>
        public event Action<NetworkConnection, byte[], int>? OnMessageReceived;

        private NetworkManager() { }

        // ── 服务器模式 ────────────────────────────────────────────

        /// <summary>以服务器模式启动。</summary>
        public bool StartServer(int? port = null)
        {
            if (IsRunning) return false;

            if (port.HasValue) Config.Port = port.Value;
            Driver = new NetworkDriver(Config);
            if (!Driver.Initialize() || !Driver.Start()) return false;

            Role = NetworkRole.Server;
            IsRunning = true;
            OnServerStarted?.Invoke();
            return true;
        }

        // ── 客户端模式 ────────────────────────────────────────────

        /// <summary>以客户端模式连接到服务器。</summary>
        public bool ConnectToServer(string host, int port)
        {
            if (IsRunning) return false;

            Config.Port = port;
            Driver = new NetworkDriver(Config);
            if (!Driver.Initialize()) return false;

            var endPoint = new IPEndPoint(IPAddress.Parse(host), port);
            _serverConnection = new NetworkConnection(endPoint);
            _serverConnection.SetState(ConnectionState.Connecting);

            Driver.Start(); // 启动接收
            Driver.Connect(endPoint);

            Role = NetworkRole.Client;
            IsRunning = true;
            return true;
        }

        /// <summary>连接到服务器。</summary>
        public bool ConnectToServer(IPAddress address, int port)
            => ConnectToServer(address.ToString(), port);

        // ── 主机模式 ──────────────────────────────────────────────

        /// <summary>以主机模式启动 (既是服务器又是客户端)。</summary>
        public bool StartHost(int? port = null)
        {
            if (!StartServer(port)) return false;

            // 创建本地回环连接
            _localConnection = new NetworkConnection();
            _localConnection.SetState(ConnectionState.Connected);
            _localConnection.PlayerName = "HostPlayer";
            RegisterConnection(_localConnection);

            return true;
        }

        /// <summary>停止网络服务 (服务器/主机)。</summary>
        public void StopServer()
        {
            if (!IsRunning) return;

            // 断开所有客户端
            foreach (var conn in _connections.Values.ToList())
            {
                DisconnectClient(conn.ConnectionId, "Server shutting down");
            }

            Driver?.Stop();
            IsRunning = false;
            Role = NetworkRole.None;
            OnServerStopped?.Invoke();
        }

        /// <summary>断开与服务器的连接 (客户端模式)。</summary>
        public void DisconnectFromServer()
        {
            if (Role != NetworkRole.Client || _serverConnection == null) return;

            DisconnectClient(_serverConnection.ConnectionId, "Client disconnected");
            Driver?.Stop();
            IsRunning = false;
            Role = NetworkRole.None;
            OnDisconnectedFromServer?.Invoke();
        }

        /// <summary>停止所有网络活动。</summary>
        public void Shutdown()
        {
            if (Role == NetworkRole.Client)
                DisconnectFromServer();
            else
                StopServer();

            _connections.Clear();
            _endpointMap.Clear();
            _serverConnection = null;
            _localConnection = null;
        }

        // ── 连接管理 ──────────────────────────────────────────────

        /// <summary>注册新连接。</summary>
        internal void RegisterConnection(NetworkConnection connection)
        {
            if (connection == null) return;
            lock (_connections)
            {
                _connections[connection.ConnectionId] = connection;
                _endpointMap[connection.RemoteEndPoint] = connection;
            }
        }

        /// <summary>断开指定客户端连接。</summary>
        public void DisconnectClient(int connectionId, string reason = "Disconnected")
        {
            lock (_connections)
            {
                if (_connections.TryGetValue(connectionId, out var conn))
                {
                    conn.SetState(ConnectionState.Disconnecting);

                    if (conn.RemoteEndPoint != null && conn.RemoteEndPoint.Port != 0)
                    {
                        Driver?.Disconnect(conn.RemoteEndPoint);
                    }

                    _connections.Remove(connectionId);
                    _endpointMap.Remove(conn.RemoteEndPoint);
                    conn.SetState(ConnectionState.Disconnected);

                    OnClientDisconnected?.Invoke(conn);
                }
            }
        }

        /// <summary>踢出指定连接。</summary>
        public void KickClient(int connectionId, string reason = "Kicked")
        {
            DisconnectClient(connectionId, reason);
        }

        /// <summary>封禁 IP 地址。</summary>
        public void BanIP(string ipAddress)
        {
            _bannedIPs.Add(ipAddress);
            // 同时断开该 IP 的所有连接
            lock (_connections)
            {
                var toKick = _connections.Values
                    .Where(c => c.RemoteEndPoint.Address.ToString() == ipAddress)
                    .ToList();
                foreach (var conn in toKick)
                {
                    KickClient(conn.ConnectionId, "IP Banned");
                }
            }
        }

        /// <summary>解除 IP 封禁。</summary>
        public void UnbanIP(string ipAddress) { _bannedIPs.Remove(ipAddress); }

        /// <summary>检查 IP 是否被封禁。</summary>
        public bool IsIPBanned(string ipAddress) => _bannedIPs.Contains(ipAddress);

        /// <summary>获取当前连接列表。</summary>
        public List<NetworkConnection> GetConnections()
        {
            lock (_connections)
            {
                return _connections.Values.ToList();
            }
        }

        /// <summary>获取连接数。</summary>
        public int ConnectionCount
        {
            get
            {
                lock (_connections) { return _connections.Count; }
            }
        }

        /// <summary>根据连接 ID 查找连接。</summary>
        public NetworkConnection? GetConnection(int connectionId)
        {
            lock (_connections)
            {
                return _connections.TryGetValue(connectionId, out var conn) ? conn : null;
            }
        }

        /// <summary>根据端点查找连接。</summary>
        public NetworkConnection? GetConnectionByEndpoint(IPEndPoint endPoint)
        {
            lock (_connections)
            {
                return _endpointMap.TryGetValue(endPoint, out var conn) ? conn : null;
            }
        }

        /// <summary>获取服务器连接 (客户端模式)。</summary>
        public NetworkConnection? ServerConnection => _serverConnection;

        /// <summary>获取本地连接 (Host 模式)。</summary>
        public NetworkConnection? LocalConnection => _localConnection;

        /// <summary>获取封禁列表。</summary>
        public IReadOnlyCollection<string> BannedIPs => _bannedIPs;

        // ── 发送 ──────────────────────────────────────────────────

        /// <summary>发送消息到指定连接。</summary>
        public bool SendTo(int connectionId, byte[] data, int length)
        {
            if (!IsRunning || Driver == null) return false;
            lock (_connections)
            {
                if (!_connections.TryGetValue(connectionId, out var conn)) return false;
                bool sent = Driver.Send(conn.RemoteEndPoint, data, length);
                if (sent) conn.RecordSent(length);
                return sent;
            }
        }

        /// <summary>广播消息到所有客户端 (服务器/Host 模式)。</summary>
        public void BroadcastToAll(byte[] data, int length)
        {
            if (!IsRunning || Driver == null) return;
            Driver.Broadcast(data, length);
            lock (_connections)
            {
                foreach (var conn in _connections.Values)
                    conn.RecordSent(length);
            }
        }

        /// <summary>发送到所有客户端 (排除指定连接)。</summary>
        public void BroadcastToAllExcept(int excludeConnectionId, byte[] data, int length)
        {
            if (!IsRunning || Driver == null) return;
            lock (_connections)
            {
                foreach (var conn in _connections.Values)
                {
                    if (conn.ConnectionId == excludeConnectionId) continue;
                    bool sent = Driver.Send(conn.RemoteEndPoint, data, length);
                    if (sent) conn.RecordSent(length);
                }
            }
        }

        // ── 全局 Update ───────────────────────────────────────────

        /// <summary>每帧更新 (由引擎主循环调用)。</summary>
        public void Update(float deltaTime)
        {
            if (!IsRunning || Driver == null) return;

            // 1. 轮询接收
            Driver.PollReceive();
            var packets = Driver.GetReceivedPackets();

            foreach (var packet in packets)
            {
                // 检查封禁
                if (_bannedIPs.Contains(packet.From.Address.ToString()))
                    continue;

                // 查找或创建连接
                var conn = GetConnectionByEndpoint(packet.From);
                if (conn != null)
                {
                    conn.RecordReceived(packet.Length);
                    OnMessageReceived?.Invoke(conn, packet.Data, packet.Length);
                }
                else if (Role == NetworkRole.Server)
                {
                    // 服务器收到未知端点的包, 可能是握手请求
                    HandleIncomingConnection(packet);
                }
            }

            // 2. 心跳发送
            _heartbeatTimer += deltaTime;
            if (_heartbeatTimer >= Config.HeartbeatInterval)
            {
                _heartbeatTimer = 0f;
                SendHeartbeats();
            }

            // 3. 超时检测
            _timeoutCheckTimer += deltaTime;
            if (_timeoutCheckTimer >= 1.0f)
            {
                _timeoutCheckTimer = 0f;
                CheckTimeouts();
            }
        }

        private void HandleIncomingConnection(TransportReceivedPacket packet)
        {
            if (ConnectionCount >= Config.MaxConnections)
                return;

            // 检查是否封禁
            if (_bannedIPs.Contains(packet.From.Address.ToString()))
                return;

            // 创建新连接
            var conn = new NetworkConnection(packet.From);
            conn.SetState(ConnectionState.Connected);
            conn.RecordReceived(packet.Length);
            RegisterConnection(conn);

            OnClientConnected?.Invoke(conn);
            OnMessageReceived?.Invoke(conn, packet.Data, packet.Length);
        }

        private void SendHeartbeats()
        {
            // 服务器向所有客户端发送心跳
            if (Role == NetworkRole.Server || Role == NetworkRole.Host)
            {
                var conns = GetConnections();
                foreach (var conn in conns)
                {
                    if (conn.State == ConnectionState.Connected)
                    {
                        var writer = new NetworkWriter(16);
                        writer.WriteUShort(0); // 心跳包类型
                        writer.WriteInt(conn.GetNextHeartbeatSequence());
                        writer.WriteInt(Environment.TickCount);
                        SendTo(conn.ConnectionId, writer.Data, writer.Length);
                    }
                }
            }
        }

        private void CheckTimeouts()
        {
            lock (_connections)
            {
                var timedOut = _connections.Values
                    .Where(c => c.State == ConnectionState.Connected && c.IsTimedOut(Config.ConnectionTimeout))
                    .ToList();

                foreach (var conn in timedOut)
                {
                    conn.SetState(ConnectionState.Timeout);
                    DisconnectClient(conn.ConnectionId, "Connection timeout");
                    OnClientTimeout?.Invoke(conn);
                }
            }
        }

        /// <summary>获取当前模式描述字符串。</summary>
        public override string ToString() => $"NetworkManager [{Role}] Connections={ConnectionCount}";
    }
}
