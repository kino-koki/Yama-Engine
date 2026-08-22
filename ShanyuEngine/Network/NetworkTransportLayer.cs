// ============================================================================
// NetworkTransportLayer.cs
// 山雨游戏引擎 - 网络传输层
// 支持 UDP / TCP / 可靠UDP 三种传输模式
// ============================================================================
// UDP丢包处理是真头疼, 这块代码花了我不少时间调
// TCP稳定但延迟高, UDP快但不可靠, 可靠UDP是折中方案
// ============================================================================

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace ShanyuEngine.Network
{
    /// <summary>
    /// 传输协议类型枚举
    /// </summary>

    /// <summary>
    /// 连接状态
    /// </summary>

    /// <summary>
    /// 数据包优先级 - 高优先级的包优先发送
    /// </summary>
    public enum PacketPriority
    {
        /// <summary>低优先级 - 聊天消息之类的</summary>
        Low = 0,
        /// <summary>中优先级 - 一般游戏数据</summary>
        Medium = 1,
        /// <summary>高优先级 - 位置同步之类的重要数据</summary>
        High = 2,
        /// <summary>紧急 - 断线通知等</summary>
        Critical = 3
    }

    /// <summary>
    /// 网络数据包结构
    /// </summary>
    public struct NetworkPacket
    {
        /// <summary>包序号</summary>
        public uint Sequence;
        /// <summary>ACK序号</summary>
        public uint AckSequence;
        /// <summary>ACK位域 - 用来批量确认收到的包</summary>
        public uint AckBitfield;
        /// <summary>包类型</summary>
        public PacketType Type;
        /// <summary>优先级</summary>
        public PacketPriority Priority;
        /// <summary>是否需要可靠传输</summary>
        public bool Reliable;
        /// <summary>数据负载</summary>
        public byte[] Payload;
        /// <summary>发送时间戳(毫秒)</summary>
        public long Timestamp;
        /// <summary>目标端点</summary>
        public IPEndPoint RemoteEndpoint;

        /// <summary>
        /// 创建一个新包
        /// </summary>
        public static NetworkPacket Create(PacketType type, byte[] payload, bool reliable = false)
        {
            return new NetworkPacket
            {
                Sequence = 0,
                AckSequence = 0,
                AckBitfield = 0,
                Type = type,
                Priority = PacketPriority.Medium,
                Reliable = reliable,
                Payload = payload,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
        }
    }

    /// <summary>
    /// 包类型枚举
    /// </summary>
    public enum PacketType : byte
    {
        /// <summary>握手包 - 建立连接用</summary>
        Handshake = 0,
        /// <summary>握手确认</summary>
        HandshakeAck = 1,
        /// <summary>心跳包 - 保持连接</summary>
        Heartbeat = 2,
        /// <summary>心跳回复</summary>
        HeartbeatAck = 3,
        /// <summary>断开连接</summary>
        Disconnect = 4,
        /// <summary>ACK确认包</summary>
        Ack = 5,
        /// <summary>数据包</summary>
        Data = 6,
        /// <summary>分片包</summary>
        Fragment = 7,
        /// <summary>RPC调用</summary>
        RPC = 8,
        /// <summary>同步数据</summary>
        Sync = 9,
        /// <summary>语音数据</summary>
        Voice = 10,
        /// <summary>文件传输</summary>
        FileTransfer = 11
    }

    /// <summary>
    /// 连接配置
    /// </summary>
    public class TransportConfig
    {
        /// <summary>使用的协议</summary>
        public TransportProtocol Protocol { get; set; } = TransportProtocol.ReliableUDP;

        /// <summary>绑定端口</summary>
        public int Port { get; set; } = 7777;

        /// <summary>最大连接数</summary>
        public int MaxConnections { get; set; } = 64;

        /// <summary>发送缓冲区大小(字节)</summary>
        public int SendBufferSize { get; set; } = 1024 * 256;

        /// <summary>接收缓冲区大小(字节)</summary>
        public int ReceiveBufferSize { get; set; } = 1024 * 256;

        /// <summary>最大包大小(字节)</summary>
        public int MaxPacketSize { get; set; } = 1400;

        /// <summary>心跳间隔(毫秒)</summary>
        public int HeartbeatInterval { get; set; } = 1000;

        /// <summary>连接超时(毫秒)</summary>
        public int ConnectionTimeout { get; set; } = 10000;

        /// <summary>可靠包重发间隔(毫秒)</summary>
        public int ResendInterval { get; set; } = 100;

        /// <summary>最大重发次数</summary>
        public int MaxResendCount { get; set; } = 10;

        /// <summary>滑动窗口大小</summary>
        public int SlidingWindowSize { get; set; } = 256;

        /// <summary>是否启用Nagle算法</summary>
        public bool EnableNagle { get; set; } = false;

        /// <summary>是否启用拥塞控制</summary>
        public bool EnableCongestionControl { get; set; } = true;

        /// <summary>初始拥塞窗口</summary>
        public int InitialCongestionWindow { get; set; } = 10;

        /// <summary>MTU大小</summary>
        public int MTU { get; set; } = 1500;

        /// <summary>是否启用IPv6</summary>
        public bool EnableIPv6 { get; set; } = false;

        /// <summary>是否启用QoS</summary>
        public bool EnableQoS { get; set; } = false;

        /// <summary>发送速率限制(字节/秒, 0=不限制)</summary>
        public int SendRateLimit { get; set; } = 0;

        /// <summary>接收速率限制(字节/秒, 0=不限制)</summary>
        public int ReceiveRateLimit { get; set; } = 0;

        /// <summary>默认配置</summary>
        public static TransportConfig Default => new TransportConfig();
    }

    /// <summary>
    /// 连接信息
    /// </summary>
    public class ConnectionInfo
    {
        /// <summary>连接ID</summary>
        public int ConnectionId { get; set; }

        /// <summary>远程端点</summary>
        public IPEndPoint RemoteEndpoint { get; set; }

        /// <summary>连接状态</summary>
        public ConnectionState State { get; set; }

        /// <summary>连接建立时间</summary>
        public DateTime ConnectTime { get; set; }

        /// <summary>最后收到数据的时间</summary>
        public DateTime LastReceiveTime { get; set; }

        /// <summary>最后发送数据的时间</summary>
        public DateTime LastSendTime { get; set; }

        /// <summary>本地发送序号</summary>
        public uint LocalSequence { get; set; }

        /// <summary>远程发送序号</summary>
        public uint RemoteSequence { get; set; }

        /// <summary>已确认的远程序号</summary>
        public uint AckedSequence { get; set; }

        /// <summary>ACK位域</summary>
        public uint AckBitfield { get; set; }

        /// <summary>RTT(往返时间, 毫秒)</summary>
        public float RTT { get; set; }

        /// <summary>平滑RTT</summary>
        public float SmoothedRTT { get; set; }

        /// <summary>RTT方差</summary>
        public float RTTVar { get; set; }

        /// <summary>丢包率(0-1)</summary>
        public float PacketLoss { get; set; }

        /// <summary>发送字节总数</summary>
        public long BytesSent { get; set; }

        /// <summary>接收字节总数</summary>
        public long BytesReceived { get; set; }

        /// <summary>发送包总数</summary>
        public long PacketsSent { get; set; }

        /// <summary>接收包总数</summary>
        public long PacketsReceived { get; set; }

        /// <summary>丢失包总数</summary>
        public long PacketsLost { get; set; }

        /// <summary>待确认的可靠包队列</summary>
        public Queue<uint> PendingAcks { get; set; } = new Queue<uint>();

        /// <summary>已发送但未确认的可靠包</summary>
        public Dictionary<uint, PendingPacket> UnackedPackets { get; set; } = new Dictionary<uint, PendingPacket>();

        /// <summary>接收到的包序号集合(用于生成ACK位域)</summary>
        public HashSet<uint> ReceivedSequences { get; set; } = new HashSet<uint>();

        /// <summary>拥塞窗口大小</summary>
        public int CongestionWindow { get; set; } = 10;

        /// <summary>慢启动阈值</summary>
        public int SlowStartThreshold { get; set; } = 256;

        /// <summary>是否在慢启动阶段</summary>
        public bool InSlowStart { get; set; } = true;

        /// <summary>重复ACK计数</summary>
        public int DuplicateAckCount { get; set; }

        /// <summary>最后收到的ACK序号</summary>
        public uint LastAckSequence { get; set; }

        /// <summary>创建连接信息
        public ConnectionInfo(int id, IPEndPoint endpoint)
        {
            ConnectionId = id;
            RemoteEndpoint = endpoint;
            State = ConnectionState.Connecting;
            ConnectTime = DateTime.UtcNow;
            LastReceiveTime = ConnectTime;
            LastSendTime = ConnectTime;
        }
    }

    /// <summary>
    /// 待确认的数据包(用于可靠传输)
    /// </summary>
    public class PendingPacket
    {
        /// <summary>包序号</summary>
        public uint Sequence { get; set; }

        /// <summary>数据</summary>
        public byte[] Data { get; set; }

        /// <summary>首次发送时间</summary>
        public long FirstSendTime { get; set; }

        /// <summary>最后发送时间</summary>
        public long LastSendTime { get; set; }

        /// <summary>重发次数</summary>
        public int ResendCount { get; set; }

        /// <summary>包类型</summary>
        public PacketType Type { get; set; }

        /// <summary>优先级</summary>
        public PacketPriority Priority { get; set; }

        /// <summary>是否已确认</summary>
        public bool Acked { get; set; }
    }

    /// <summary>
    /// 连接事件参数
    /// </summary>
    public class ConnectionEventArgs : EventArgs
    {
        public int ConnectionId { get; set; }
        public IPEndPoint RemoteEndpoint { get; set; }
        public string Reason { get; set; }
    }

    /// <summary>
    /// 包接收事件参数
    /// </summary>
    public class PacketReceivedEventArgs : EventArgs
    {
        public int ConnectionId { get; set; }
        public NetworkPacket Packet { get; set; }
    }

    /// <summary>
    /// 网络传输层 - 核心类
    /// 负责底层的网络数据收发
    /// </summary>
    public class NetworkTransportLayer : IDisposable
    {
        // 配置
        private readonly TransportConfig _config;

        // UDP socket
        private UdpClient _udpClient;

        // TCP listener (服务端用)
        private TcpListener _tcpListener;

        // TCP clients
        private readonly Dictionary<int, TcpClient> _tcpClients = new Dictionary<int, TcpClient>();

        // 连接表
        private readonly Dictionary<int, ConnectionInfo> _connections = new Dictionary<int, ConnectionInfo>();

        // 端点到连接ID的映射
        private readonly Dictionary<string, int> _endpointToConnection = new Dictionary<string, int>();

        // 下一个连接ID
        private int _nextConnectionId = 1;

        // 发送队列(按优先级)
        private readonly ConcurrentQueue<NetworkPacket>[] _sendQueues = new ConcurrentQueue<NetworkPacket>[4];

        // 接收队列
        private readonly ConcurrentQueue<NetworkPacket> _receiveQueue = new ConcurrentQueue<NetworkPacket>();

        // 待发送的可靠包队列
        private readonly ConcurrentQueue<NetworkPacket> _reliableSendQueue = new ConcurrentQueue<NetworkPacket>();

        // 运行标志
        private volatile bool _running = false;

        // 线程
        private Thread _sendThread;
        private Thread _receiveThread;
        private Thread _heartbeatThread;
        private Thread _resendThread;

        // 信号量 - 有数据可发送时触发
        private readonly AutoResetEvent _sendSignal = new AutoResetEvent(false);

        // 序号生成锁
        private readonly object _sequenceLock = new object();

        // 连接表锁
        private readonly object _connectionLock = new object();

        // 丢弃的序号集合(防止重复处理)
        private readonly Dictionary<int, HashSet<uint>> _processedSequences = new Dictionary<int, HashSet<uint>>();

        // 是否是服务端
        private bool _isServer = false;

        // 本地绑定端点
        private IPEndPoint _localEndpoint;

        // 统计数据
        private long _totalBytesSent;
        private long _totalBytesReceived;
        private long _totalPacketsSent;
        private long _totalPacketsReceived;

        // 发送速率控制
        private long _sendRateBytesSent;
        private long _sendRateWindowStart;
        private readonly object _rateLimitLock = new object();

        // 分片重组缓冲
        private readonly Dictionary<int, Dictionary<int, FragmentAssembler>> _fragmentBuffers = new Dictionary<int, Dictionary<int, FragmentAssembler>>();

        // 拥塞控制参数
        private float _congestionBackoff = 1.0f;

        // 是否已释放
        private bool _disposed = false;

        // 随机数生成器(用于模拟丢包测试)
        private readonly Random _random = new Random();

        // 模拟丢包率(测试用, 0-1)
        private float _simulateLossRate = 0.0f;

        // 模拟延迟(毫秒, 测试用)
        private int _simulateLatency = 0;

        // 模拟抖动(毫秒, 测试用)
        private int _simulateJitter = 0;

        // ==================== 事件 ====================

        /// <summary>新连接建立时触发</summary>
        public event EventHandler<ConnectionEventArgs> OnConnected;

        /// <summary>连接断开时触发</summary>
        public event EventHandler<ConnectionEventArgs> OnDisconnected;

        /// <summary>收到数据包时触发</summary>
        public event EventHandler<PacketReceivedEventArgs> OnPacketReceived;

        /// <summary>连接超时时触发</summary>
        public event EventHandler<ConnectionEventArgs> OnConnectionTimedOut;

        // ==================== 构造函数 ====================

        /// <summary>
        /// 创建传输层实例
        /// </summary>
        /// <param name="config">传输配置</param>
        public NetworkTransportLayer(TransportConfig config = null)
        {
            _config = config ?? TransportConfig.Default;

            // 初始化各优先级的发送队列
            for (int i = 0; i < 4; i++)
            {
                _sendQueues[i] = new ConcurrentQueue<NetworkPacket>();
            }
        }

        // ==================== 启动/停止 ====================

        /// <summary>
        /// 作为服务端启动
        /// </summary>
        public void StartServer()
        {
            if (_running)
            {
                throw new InvalidOperationException("传输层已经在运行了");
            }

            _isServer = true;
            _localEndpoint = new IPEndPoint(_config.EnableIPv6 ? IPAddress.IPv6Any : IPAddress.Any, _config.Port);

            try
            {
                if (_config.Protocol == TransportProtocol.UDP || _config.Protocol == TransportProtocol.ReliableUDP)
                {
                    _udpClient = new UdpClient(_localEndpoint);
                    _udpClient.SendBufferSize = _config.SendBufferSize;
                    _udpClient.ReceiveBufferSize = _config.ReceiveBufferSize;
                }

                if (_config.Protocol == TransportProtocol.TCP)
                {
                    _tcpListener = new TcpListener(_localEndpoint);
                    _tcpListener.Start(_config.MaxConnections);
                }

                _running = true;

                // 启动工作线程
                StartWorkerThreads();

                Console.WriteLine($"[NetworkTransport] 服务端启动成功, 端口: {_config.Port}, 协议: {_config.Protocol}");
            }
            catch (Exception ex)
            {
                _running = false;
                throw new InvalidOperationException($"服务端启动失败: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 作为客户端启动
        /// </summary>
        public void StartClient()
        {
            if (_running)
            {
                throw new InvalidOperationException("传输层已经在运行了");
            }

            _isServer = false;
            _localEndpoint = new IPEndPoint(_config.EnableIPv6 ? IPAddress.IPv6Any : IPAddress.Any, 0);

            try
            {
                if (_config.Protocol == TransportProtocol.UDP || _config.Protocol == TransportProtocol.ReliableUDP)
                {
                    _udpClient = new UdpClient(_localEndpoint);
                    _udpClient.SendBufferSize = _config.SendBufferSize;
                    _udpClient.ReceiveBufferSize = _config.ReceiveBufferSize;
                }

                _running = true;

                // 启动工作线程
                StartWorkerThreads();

                Console.WriteLine($"[NetworkTransport] 客户端启动成功, 本地端口: {((IPEndPoint)_udpClient.Client.LocalEndPoint).Port}");
            }
            catch (Exception ex)
            {
                _running = false;
                throw new InvalidOperationException($"客户端启动失败: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 停止传输层
        /// </summary>
        public void Stop()
        {
            if (!_running) return;

            _running = false;
            _sendSignal.Set();

            // 断开所有连接
            lock (_connectionLock)
            {
                foreach (var conn in _connections.Values)
                {
                    if (conn.State == ConnectionState.Connected)
                    {
                        SendDisconnect(conn.ConnectionId, "服务关闭");
                    }
                    conn.State = ConnectionState.Disconnected;
                }
            }

            // 等待线程结束
            _sendThread?.Join(2000);
            _receiveThread?.Join(2000);
            _heartbeatThread?.Join(2000);
            _resendThread?.Join(2000);

            // 关闭socket
            try { _udpClient?.Close(); } catch { }
            try { _tcpListener?.Stop(); } catch { }

            lock (_connectionLock)
            {
                foreach (var tcp in _tcpClients.Values)
                {
                    try { tcp.Close(); } catch { }
                }
                _tcpClients.Clear();
                _connections.Clear();
                _endpointToConnection.Clear();
            }

            Console.WriteLine("[NetworkTransport] 传输层已停止");
        }

        /// <summary>
        /// 启动工作线程
        /// </summary>
        private void StartWorkerThreads()
        {
            _sendThread = new Thread(SendLoop)
            {
                Name = "NetworkSend",
                IsBackground = true
            };
            _sendThread.Start();

            _receiveThread = new Thread(ReceiveLoop)
            {
                Name = "NetworkReceive",
                IsBackground = true
            };
            _receiveThread.Start();

            _heartbeatThread = new Thread(HeartbeatLoop)
            {
                Name = "NetworkHeartbeat",
                IsBackground = true
            };
            _heartbeatThread.Start();

            _resendThread = new Thread(ResendLoop)
            {
                Name = "NetworkResend",
                IsBackground = true
            };
            _resendThread.Start();
        }

        // ==================== 连接管理 ====================

        /// <summary>
        /// 连接到远程服务器
        /// </summary>
        /// <param name="host">服务器地址</param>
        /// <param name="port">服务器端口</param>
        /// <returns>连接ID</returns>
        public int Connect(string host, int port)
        {
            IPAddress address;
            if (!IPAddress.TryParse(host, out address))
            {
                var addresses = Dns.GetHostAddresses(host);
                if (addresses.Length == 0)
                {
                    throw new InvalidOperationException($"无法解析主机地址: {host}");
                }
                address = addresses[0];
            }

            var endpoint = new IPEndPoint(address, port);
            return Connect(endpoint);
        }

        /// <summary>
        /// 连接到远程服务器
        /// </summary>
        /// <param name="endpoint">远程端点</param>
        /// <returns>连接ID</returns>
        public int Connect(IPEndPoint endpoint)
        {
            if (_config.Protocol == TransportProtocol.TCP)
            {
                return ConnectTCP(endpoint);
            }

            // UDP/ReliableUDP
            int connectionId;
            lock (_connectionLock)
            {
                connectionId = _nextConnectionId++;
                var conn = new ConnectionInfo(connectionId, endpoint);
                _connections[connectionId] = conn;
                _endpointToConnection[endpoint.ToString()] = connectionId;
                _processedSequences[connectionId] = new HashSet<uint>();
            }

            // 发送握手包
            var handshakeData = new byte[] { (byte)PacketType.Handshake, 0x53, 0x59 }; // "SY"
            var packet = NetworkPacket.Create(PacketType.Handshake, handshakeData, true);
            packet.RemoteEndpoint = endpoint;
            packet.Priority = PacketPriority.Critical;
            EnqueuePacket(packet);

            // 更新连接状态
            lock (_connectionLock)
            {
                if (_connections.TryGetValue(connectionId, out var conn))
                {
                    conn.LastSendTime = DateTime.UtcNow;
                }
            }

            Console.WriteLine($"[NetworkTransport] 正在连接到 {endpoint}, 连接ID: {connectionId}");
            return connectionId;
        }

        /// <summary>
        /// TCP连接
        /// </summary>
        private int ConnectTCP(IPEndPoint endpoint)
        {
            var tcpClient = new TcpClient();
            tcpClient.SendBufferSize = _config.SendBufferSize;
            tcpClient.ReceiveBufferSize = _config.ReceiveBufferSize;

            try
            {
                tcpClient.Connect(endpoint);

                int connectionId;
                lock (_connectionLock)
                {
                    connectionId = _nextConnectionId++;
                    var conn = new ConnectionInfo(connectionId, endpoint)
                    {
                        State = ConnectionState.Connected
                    };
                    _connections[connectionId] = conn;
                    _tcpClients[connectionId] = tcpClient;
                    _endpointToConnection[endpoint.ToString()] = connectionId;
                }

                OnConnected?.Invoke(this, new ConnectionEventArgs
                {
                    ConnectionId = connectionId,
                    RemoteEndpoint = endpoint
                });

                // 启动TCP接收线程
                var stream = tcpClient.GetStream();
                Task.Run(() => ReceiveTCPLoop(connectionId, stream));

                return connectionId;
            }
            catch (Exception ex)
            {
                tcpClient.Close();
                throw new InvalidOperationException($"TCP连接失败: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 断开连接
        /// </summary>
        /// <param name="connectionId">连接ID</param>
        /// <param name="reason">断开原因</param>
        public void Disconnect(int connectionId, string reason = "")
        {
            lock (_connectionLock)
            {
                if (!_connections.TryGetValue(connectionId, out var conn))
                {
                    return;
                }

                if (conn.State == ConnectionState.Disconnected || conn.State == ConnectionState.Disconnecting)
                {
                    return;
                }

                conn.State = ConnectionState.Disconnecting;
            }

            // 发送断开连接包
            SendDisconnect(connectionId, reason);

            // 延迟一点再真正关闭
            Task.Run(() =>
            {
                Thread.Sleep(500);
                InternalDisconnect(connectionId, reason);
            });
        }

        /// <summary>
        /// 内部断开连接
        /// </summary>
        private void InternalDisconnect(int connectionId, string reason)
        {
            lock (_connectionLock)
            {
                if (!_connections.TryGetValue(connectionId, out var conn))
                {
                    return;
                }

                conn.State = ConnectionState.Disconnected;

                if (conn.RemoteEndpoint != null)
                {
                    _endpointToConnection.Remove(conn.RemoteEndpoint.ToString());
                }

                if (_tcpClients.TryGetValue(connectionId, out var tcp))
                {
                    try { tcp.Close(); } catch { }
                    _tcpClients.Remove(connectionId);
                }

                _processedSequences.Remove(connectionId);
                _fragmentBuffers.Remove(connectionId);

                _connections.Remove(connectionId);
            }

            OnDisconnected?.Invoke(this, new ConnectionEventArgs
            {
                ConnectionId = connectionId,
                Reason = reason
            });

            Console.WriteLine($"[NetworkTransport] 连接 {connectionId} 已断开: {reason}");
        }

        /// <summary>
        /// 发送断开连接包
        /// </summary>
        private void SendDisconnect(int connectionId, string reason)
        {
            var reasonBytes = System.Text.Encoding.UTF8.GetBytes(reason ?? "");
            var data = new byte[1 + reasonBytes.Length];
            data[0] = (byte)PacketType.Disconnect;
            Buffer.BlockCopy(reasonBytes, 0, data, 1, reasonBytes.Length);

            var packet = NetworkPacket.Create(PacketType.Disconnect, data, true);
            packet.Priority = PacketPriority.Critical;

            lock (_connectionLock)
            {
                if (_connections.TryGetValue(connectionId, out var conn))
                {
                    packet.RemoteEndpoint = conn.RemoteEndpoint;
                }
            }

            if (packet.RemoteEndpoint != null)
            {
                EnqueuePacket(packet);
            }
        }

        // ==================== 数据发送 ====================

        /// <summary>
        /// 发送数据包
        /// </summary>
        /// <param name="connectionId">连接ID</param>
        /// <param name="data">数据</param>
        /// <param name="reliable">是否可靠</param>
        /// <param name="priority">优先级</param>
        public void Send(int connectionId, byte[] data, bool reliable = false, PacketPriority priority = PacketPriority.Medium)
        {
            if (data == null || data.Length == 0)
            {
                return;
            }

            if (data.Length > _config.MaxPacketSize)
            {
                // 数据太大, 需要分片
                SendFragmented(connectionId, data, reliable, priority);
                return;
            }

            var packet = NetworkPacket.Create(PacketType.Data, data, reliable);
            packet.Priority = priority;

            lock (_connectionLock)
            {
                if (_connections.TryGetValue(connectionId, out var conn))
                {
                    packet.RemoteEndpoint = conn.RemoteEndpoint;
                }
            }

            if (packet.RemoteEndpoint != null)
            {
                EnqueuePacket(packet);
            }
        }

        /// <summary>
        /// 发送数据包到指定端点
        /// </summary>
        public void SendTo(IPEndPoint endpoint, byte[] data, bool reliable = false, PacketPriority priority = PacketPriority.Medium)
        {
            var packet = NetworkPacket.Create(PacketType.Data, data, reliable);
            packet.Priority = priority;
            packet.RemoteEndpoint = endpoint;
            EnqueuePacket(packet);
        }

        /// <summary>
        /// 广播数据到所有连接
        /// </summary>
        public void Broadcast(byte[] data, bool reliable = false, PacketPriority priority = PacketPriority.Medium)
        {
            List<int> connectionIds;
            lock (_connectionLock)
            {
                connectionIds = new List<int>(_connections.Keys);
            }

            foreach (var id in connectionIds)
            {
                Send(id, data, reliable, priority);
            }
        }

        /// <summary>
        /// 广播数据(排除指定连接)
        /// </summary>
        public void BroadcastExcept(int excludeConnectionId, byte[] data, bool reliable = false, PacketPriority priority = PacketPriority.Medium)
        {
            List<int> connectionIds;
            lock (_connectionLock)
            {
                connectionIds = new List<int>(_connections.Keys);
            }

            foreach (var id in connectionIds)
            {
                if (id != excludeConnectionId)
                {
                    Send(id, data, reliable, priority);
                }
            }
        }

        /// <summary>
        /// 分片发送大数据
        /// </summary>
        private void SendFragmented(int connectionId, byte[] data, bool reliable, PacketPriority priority)
        {
            int headerSize = 9; // type(1) + groupId(4) + fragmentIndex(2) + totalFragments(2)
            int maxFragmentSize = _config.MaxPacketSize - headerSize;
            int totalFragments = (data.Length + maxFragmentSize - 1) / maxFragmentSize;

            // 生成一个随机的组ID
            int groupId = _random.Next();

            for (int i = 0; i < totalFragments; i++)
            {
                int offset = i * maxFragmentSize;
                int size = Math.Min(maxFragmentSize, data.Length - offset);

                var fragmentData = new byte[headerSize + size];
                fragmentData[0] = (byte)PacketType.Fragment;
                BitConverter.TryWriteBytes(new Span<byte>(fragmentData, 1, 4), groupId);
                BitConverter.TryWriteBytes(new Span<byte>(fragmentData, 5, 2), (ushort)i);
                BitConverter.TryWriteBytes(new Span<byte>(fragmentData, 7, 2), (ushort)totalFragments);
                Buffer.BlockCopy(data, offset, fragmentData, headerSize, size);

                var packet = NetworkPacket.Create(PacketType.Fragment, fragmentData, reliable);
                packet.Priority = priority;

                lock (_connectionLock)
                {
                    if (_connections.TryGetValue(connectionId, out var conn))
                    {
                        packet.RemoteEndpoint = conn.RemoteEndpoint;
                    }
                }

                if (packet.RemoteEndpoint != null)
                {
                    EnqueuePacket(packet);
                }
            }

            Console.WriteLine($"[NetworkTransport] 分片发送: {data.Length}字节, {totalFragments}片, 组ID: {groupId}");
        }

        /// <summary>
        /// 将包加入发送队列
        /// </summary>
        private void EnqueuePacket(NetworkPacket packet)
        {
            int queueIndex = (int)packet.Priority;
            _sendQueues[queueIndex].Enqueue(packet);
            _sendSignal.Set();
        }

        // ==================== 发送循环 ====================

        /// <summary>
        /// 发送线程主循环
        /// </summary>
        private void SendLoop()
        {
            while (_running)
            {
                // 等待有数据可发送
                _sendSignal.WaitOne(10);

                // 从高优先级到低优先级依次处理
                bool sentAny = false;

                for (int i = 3; i >= 0; i--)
                {
                    while (_sendQueues[i].TryDequeue(out var packet))
                    {
                        // 速率限制检查
                        if (_config.SendRateLimit > 0 && !CheckSendRate())
                        {
                            // 速率超限, 放回队列等下次发
                            _sendQueues[i].Enqueue(packet);
                            break;
                        }

                        SendPacket(packet);
                        sentAny = true;
                    }
                }

                // 处理待重发的可靠包
                ProcessResendPackets();

                if (!sentAny)
                {
                    Thread.Sleep(1);
                }
            }
        }

        /// <summary>
        /// 检查发送速率
        /// </summary>
        private bool CheckSendRate()
        {
            lock (_rateLimitLock)
            {
                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                // 如果窗口超过1秒, 重置
                if (now - _sendRateWindowStart > 1000)
                {
                    _sendRateWindowStart = now;
                    _sendRateBytesSent = 0;
                }

                if (_sendRateBytesSent >= _config.SendRateLimit)
                {
                    return false;
                }

                return true;
            }
        }

        /// <summary>
        /// 实际发送数据包
        /// </summary>
        private void SendPacket(NetworkPacket packet)
        {
            if (packet.RemoteEndpoint == null) return;

            // 模拟丢包(测试用)
            if (_simulateLossRate > 0 && _random.NextDouble() < _simulateLossRate)
            {
                // 模拟丢包, 直接丢弃
                Interlocked.Increment(ref _totalPacketsSent);
                return;
            }

            // 查找连接
            ConnectionInfo conn = null;
            int connectionId = -1;
            lock (_connectionLock)
            {
                if (_endpointToConnection.TryGetValue(packet.RemoteEndpoint.ToString(), out connectionId))
                {
                    _connections.TryGetValue(connectionId, out conn);
                }
            }

            // 构建最终发送的数据
            byte[] sendData;

            if (_config.Protocol == TransportProtocol.ReliableUDP || packet.Reliable)
            {
                // 给包加上序号
                if (conn != null)
                {
                    packet.Sequence = conn.LocalSequence++;
                    packet.AckSequence = conn.RemoteSequence;
                    packet.AckBitfield = GenerateAckBitfield(conn);

                    // 如果是可靠包, 加入待确认队列
                    if (packet.Reliable)
                    {
                        var pending = new PendingPacket
                        {
                            Sequence = packet.Sequence,
                            Data = SerializePacket(packet),
                            FirstSendTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                            LastSendTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                            Type = packet.Type,
                            Priority = packet.Priority
                        };

                        lock (_connectionLock)
                        {
                            conn.UnackedPackets[packet.Sequence] = pending;
                        }
                    }
                }

                sendData = SerializePacket(packet);
            }
            else
            {
                // 普通UDP, 直接发数据
                sendData = new byte[1 + packet.Payload.Length];
                sendData[0] = (byte)packet.Type;
                Buffer.BlockCopy(packet.Payload, 0, sendData, 1, packet.Payload.Length);
            }

            // 模拟延迟和抖动
            if (_simulateLatency > 0)
            {
                int delay = _simulateLatency;
                if (_simulateJitter > 0)
                {
                    delay += _random.Next(-_simulateJitter, _simulateJitter);
                }
                var data = sendData;
                var ep = packet.RemoteEndpoint;
                Task.Run(() =>
                {
                    Thread.Sleep(delay);
                    if (_running && _udpClient != null)
                    {
                        try
                        {
                            _udpClient.Send(data, data.Length, ep);
                        }
                        catch { }
                    }
                });
            }
            else
            {
                // 直接发送
                if (_config.Protocol == TransportProtocol.TCP && conn != null)
                {
                    SendTCP(connectionId, sendData);
                }
                else
                {
                    try
                    {
                        _udpClient.Send(sendData, sendData.Length, packet.RemoteEndpoint);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[NetworkTransport] UDP发送失败: {ex.Message}");
                    }
                }
            }

            // 更新统计
            Interlocked.Add(ref _totalBytesSent, sendData.Length);
            Interlocked.Increment(ref _totalPacketsSent);

            if (conn != null)
            {
                conn.BytesSent += sendData.Length;
                conn.PacketsSent++;
                conn.LastSendTime = DateTime.UtcNow;

                // 更新发送速率计数
                lock (_rateLimitLock)
                {
                    _sendRateBytesSent += sendData.Length;
                }
            }
        }

        /// <summary>
        /// TCP发送
        /// </summary>
        private void SendTCP(int connectionId, byte[] data)
        {
            lock (_connectionLock)
            {
                if (!_tcpClients.TryGetValue(connectionId, out var tcp) || !tcp.Connected)
                {
                    return;
                }

                try
                {
                    var stream = tcp.GetStream();

                    // TCP需要加长度头, 避免粘包
                    var lengthBytes = BitConverter.GetBytes(data.Length);
                    stream.Write(lengthBytes, 0, 4);
                    stream.Write(data, 0, data.Length);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[NetworkTransport] TCP发送失败: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// 序列化数据包
        /// </summary>
        private byte[] SerializePacket(NetworkPacket packet)
        {
            // 包头: type(1) + seq(4) + ack(4) + ackBitfield(4) + flags(1) = 14字节
            int headerSize = 14;
            var data = new byte[headerSize + (packet.Payload?.Length ?? 0)];

            data[0] = (byte)packet.Type;
            BitConverter.TryWriteBytes(new Span<byte>(data, 1, 4), packet.Sequence);
            BitConverter.TryWriteBytes(new Span<byte>(data, 5, 4), packet.AckSequence);
            BitConverter.TryWriteBytes(new Span<byte>(data, 9, 4), packet.AckBitfield);

            byte flags = 0;
            if (packet.Reliable) flags |= 0x01;
            data[13] = flags;

            if (packet.Payload != null && packet.Payload.Length > 0)
            {
                Buffer.BlockCopy(packet.Payload, 0, data, headerSize, packet.Payload.Length);
            }

            return data;
        }

        /// <summary>
        /// 反序列化数据包
        /// </summary>
        private NetworkPacket DeserializePacket(byte[] data, IPEndPoint remoteEndpoint)
        {
            if (data == null || data.Length < 14)
            {
                return default;
            }

            var packet = new NetworkPacket
            {
                Type = (PacketType)data[0],
                Sequence = BitConverter.ToUInt32(data, 1),
                AckSequence = BitConverter.ToUInt32(data, 5),
                AckBitfield = BitConverter.ToUInt32(data, 9),
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                RemoteEndpoint = remoteEndpoint
            };

            byte flags = data[13];
            packet.Reliable = (flags & 0x01) != 0;

            int payloadSize = data.Length - 14;
            if (payloadSize > 0)
            {
                packet.Payload = new byte[payloadSize];
                Buffer.BlockCopy(data, 14, packet.Payload, 0, payloadSize);
            }

            return packet;
        }

        // ==================== 接收循环 ====================

        /// <summary>
        /// 接收线程主循环
        /// </summary>
        private void ReceiveLoop()
        {
            IPEndPoint remoteEP = new IPEndPoint(IPAddress.Any, 0);

            while (_running)
            {
                if (_udpClient == null)
                {
                    Thread.Sleep(10);
                    continue;
                }

                byte[] data;
                try
                {
                    data = _udpClient.Receive(ref remoteEP);
                }
                catch (SocketException ex)
                {
                    if (_running)
                    {
                        // UDP接收错误一般是可恢复的
                        if (ex.SocketErrorCode != SocketError.TimedOut &&
                            ex.SocketErrorCode != SocketError.ConnectionReset)
                        {
                            Console.WriteLine($"[NetworkTransport] UDP接收错误: {ex.SocketErrorCode}");
                        }
                    }
                    continue;
                }
                catch (Exception)
                {
                    if (_running) continue;
                    break;
                }

                if (data == null || data.Length == 0) continue;

                // 更新统计
                Interlocked.Add(ref _totalBytesReceived, data.Length);
                Interlocked.Increment(ref _totalPacketsReceived);

                // 查找或创建连接
                int connectionId = GetOrCreateConnection(remoteEP, out bool isNewConnection);

                // 处理接收到的数据
                ProcessReceivedData(connectionId, data, remoteEP, isNewConnection);
            }
        }

        /// <summary>
        /// TCP接收循环
        /// </summary>
        private void ReceiveTCPLoop(int connectionId, NetworkStream stream)
        {
            try
            {
                while (_running && stream.CanRead)
                {
                    // 读取长度头
                    var lengthBuffer = new byte[4];
                    int bytesRead = 0;
                    while (bytesRead < 4)
                    {
                        int read = stream.Read(lengthBuffer, bytesRead, 4 - bytesRead);
                        if (read == 0)
                        {
                            // 连接关闭
                            InternalDisconnect(connectionId, "远程关闭连接");
                            return;
                        }
                        bytesRead += read;
                    }

                    int dataLength = BitConverter.ToInt32(lengthBuffer, 0);
                    if (dataLength <= 0 || dataLength > _config.MaxPacketSize * 1000)
                    {
                        InternalDisconnect(connectionId, "无效的数据长度");
                        return;
                    }

                    // 读取数据
                    var dataBuffer = new byte[dataLength];
                    bytesRead = 0;
                    while (bytesRead < dataLength)
                    {
                        int read = stream.Read(dataBuffer, bytesRead, dataLength - bytesRead);
                        if (read == 0)
                        {
                            InternalDisconnect(connectionId, "连接中断");
                            return;
                        }
                        bytesRead += read;
                    }

                    // 处理接收到的数据
                    var endpoint = default(IPEndPoint);
                    lock (_connectionLock)
                    {
                        if (_connections.TryGetValue(connectionId, out var conn))
                        {
                            endpoint = conn.RemoteEndpoint;
                            conn.BytesReceived += dataLength;
                            conn.PacketsReceived++;
                            conn.LastReceiveTime = DateTime.UtcNow;
                        }
                    }

                    if (endpoint != null)
                    {
                        var packet = DeserializePacket(dataBuffer, endpoint);
                        _receiveQueue.Enqueue(packet);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[NetworkTransport] TCP接收错误: {ex.Message}");
                InternalDisconnect(connectionId, $"接收错误: {ex.Message}");
            }
        }

        /// <summary>
        /// 获取或创建连接
        /// </summary>
        private int GetOrCreateConnection(IPEndPoint endpoint, out bool isNewConnection)
        {
            string key = endpoint.ToString();
            isNewConnection = false;

            lock (_connectionLock)
            {
                if (_endpointToConnection.TryGetValue(key, out int id))
                {
                    return id;
                }

                // 只有服务端才会自动接受新连接
                if (_isServer)
                {
                    int connectionId = _nextConnectionId++;
                    var conn = new ConnectionInfo(connectionId, endpoint)
                    {
                        State = ConnectionState.Connected
                    };
                    _connections[connectionId] = conn;
                    _endpointToConnection[key] = connectionId;
                    _processedSequences[connectionId] = new HashSet<uint>();
                    isNewConnection = true;
                    return connectionId;
                }

                return -1;
            }
        }

        /// <summary>
        /// 处理接收到的数据
        /// </summary>
        private void ProcessReceivedData(int connectionId, byte[] data, IPEndPoint remoteEndpoint, bool isNewConnection)
        {
            ConnectionInfo conn = null;
            lock (_connectionLock)
            {
                if (connectionId >= 0 && _connections.TryGetValue(connectionId, out conn))
                {
                    conn.BytesReceived += data.Length;
                    conn.PacketsReceived++;
                    conn.LastReceiveTime = DateTime.UtcNow;
                }
            }

            // 判断是普通UDP包还是带序号的包
            if (data.Length < 14)
            {
                // 普通UDP包(只有type + payload)
                if (data.Length >= 1)
                {
                    var type = (PacketType)data[0];
                    var payload = new byte[data.Length - 1];
                    if (payload.Length > 0)
                    {
                        Buffer.BlockCopy(data, 1, payload, 0, payload.Length);
                    }

                    var packet = new NetworkPacket
                    {
                        Type = type,
                        Payload = payload,
                        RemoteEndpoint = remoteEndpoint,
                        Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                    };

                    HandlePacket(connectionId, packet, conn, isNewConnection);
                }
                return;
            }

            // 带序号的包
            var netPacket = DeserializePacket(data, remoteEndpoint);

            // 处理ACK
            if (conn != null && netPacket.AckSequence > 0)
            {
                ProcessAck(conn, netPacket.AckSequence, netPacket.AckBitfield);
            }

            // 记录接收到的序号
            if (conn != null && netPacket.Sequence > 0)
            {
                // 更新远程序号
                if (IsNewerSequence(netPacket.Sequence, conn.RemoteSequence))
                {
                    conn.RemoteSequence = netPacket.Sequence;
                }

                // 记录序号用于生成ACK位域
                conn.ReceivedSequences.Add(netPacket.Sequence);

                // 限制集合大小
                if (conn.ReceivedSequences.Count > 1024)
                {
                    // 清理旧的序号
                    var toRemove = new List<uint>();
                    foreach (var seq in conn.ReceivedSequences)
                    {
                        if (seq < conn.RemoteSequence - 512)
                        {
                            toRemove.Add(seq);
                        }
                    }
                    foreach (var seq in toRemove)
                    {
                        conn.ReceivedSequences.Remove(seq);
                    }
                }

                // 检查是否是重复包
                if (_processedSequences.TryGetValue(connectionId, out var processed))
                {
                    if (processed.Contains(netPacket.Sequence))
                    {
                        // 重复包, 已经处理过了, 只需要发ACK
                        return;
                    }

                    processed.Add(netPacket.Sequence);

                    // 限制集合大小
                    if (processed.Count > 2048)
                    {
                        // 清理旧的序号
                        var oldSeqs = new List<uint>(processed);
                        oldSeqs.Sort();
                        int toRemove = processed.Count - 1024;
                        for (int i = 0; i < toRemove; i++)
                        {
                            processed.Remove(oldSeqs[i]);
                        }
                    }
                }
            }

            HandlePacket(connectionId, netPacket, conn, isNewConnection);
        }

        /// <summary>
        /// 处理数据包
        /// </summary>
        private void HandlePacket(int connectionId, NetworkPacket packet, ConnectionInfo conn, bool isNewConnection)
        {
            switch (packet.Type)
            {
                case PacketType.Handshake:
                    HandleHandshake(connectionId, packet, conn, isNewConnection);
                    break;

                case PacketType.HandshakeAck:
                    HandleHandshakeAck(connectionId, conn);
                    break;

                case PacketType.Heartbeat:
                    // 回复心跳ACK
                    if (conn != null)
                    {
                        var ackPacket = NetworkPacket.Create(PacketType.HeartbeatAck, new byte[0], false);
                        ackPacket.RemoteEndpoint = conn.RemoteEndpoint;
                        ackPacket.Priority = PacketPriority.Critical;
                        EnqueuePacket(ackPacket);
                    }
                    break;

                case PacketType.HeartbeatAck:
                    // 心跳回复, 更新RTT
                    if (conn != null)
                    {
                        UpdateRTT(conn, packet.Timestamp);
                    }
                    break;

                case PacketType.Disconnect:
                    var reason = packet.Payload != null && packet.Payload.Length > 1
                        ? System.Text.Encoding.UTF8.GetString(packet.Payload, 1, packet.Payload.Length - 1)
                        : "未知原因";
                    InternalDisconnect(connectionId, reason);
                    break;

                case PacketType.Ack:
                    // ACK确认包 - 在ProcessAck里处理了
                    break;

                case PacketType.Fragment:
                    HandleFragment(connectionId, packet, conn);
                    break;

                case PacketType.Data:
                case PacketType.RPC:
                case PacketType.Sync:
                case PacketType.Voice:
                case PacketType.FileTransfer:
                    // 数据包, 加入接收队列等待处理
                    _receiveQueue.Enqueue(packet);
                    OnPacketReceived?.Invoke(this, new PacketReceivedEventArgs
                    {
                        ConnectionId = connectionId,
                        Packet = packet
                    });
                    break;
            }
        }

        /// <summary>
        /// 处理握手包
        /// </summary>
        private void HandleHandshake(int connectionId, NetworkPacket packet, ConnectionInfo conn, bool isNewConnection)
        {
            if (_isServer && isNewConnection)
            {
                // 服务端收到客户端握手, 回复握手ACK
                Console.WriteLine($"[NetworkTransport] 收到连接请求, 连接ID: {connectionId}");

                if (conn != null)
                {
                    conn.State = ConnectionState.Connected;
                    conn.ConnectTime = DateTime.UtcNow;
                }

                var ackData = new byte[] { (byte)PacketType.HandshakeAck, 0x41, 0x43 }; // "AC"
                var ackPacket = NetworkPacket.Create(PacketType.HandshakeAck, ackData, true);
                ackPacket.Priority = PacketPriority.Critical;
                if (conn != null)
                {
                    ackPacket.RemoteEndpoint = conn.RemoteEndpoint;
                }
                EnqueuePacket(ackPacket);

                OnConnected?.Invoke(this, new ConnectionEventArgs
                {
                    ConnectionId = connectionId,
                    RemoteEndpoint = conn?.RemoteEndpoint
                });
            }
        }

        /// <summary>
        /// 处理握手ACK
        /// </summary>
        private void HandleHandshakeAck(int connectionId, ConnectionInfo conn)
        {
            if (!_isServer && conn != null && conn.State == ConnectionState.Connecting)
            {
                conn.State = ConnectionState.Connected;
                conn.ConnectTime = DateTime.UtcNow;

                Console.WriteLine($"[NetworkTransport] 连接建立成功, 连接ID: {connectionId}");

                OnConnected?.Invoke(this, new ConnectionEventArgs
                {
                    ConnectionId = connectionId,
                    RemoteEndpoint = conn.RemoteEndpoint
                });
            }
        }

        // ==================== ACK处理 ====================

        /// <summary>
        /// 处理接收到的ACK
        /// </summary>
        private void ProcessAck(ConnectionInfo conn, uint ackSequence, uint ackBitfield)
        {
            // 处理主ACK
            AckPacket(conn, ackSequence);

            // 处理ACK位域
            for (int i = 0; i < 32; i++)
            {
                if ((ackBitfield & (1u << i)) != 0)
                {
                    uint seq = (uint)(ackSequence - i - 1);
                    AckPacket(conn, seq);
                }
            }
        }

        /// <summary>
        /// 确认一个包
        /// </summary>
        private void AckPacket(ConnectionInfo conn, uint sequence)
        {
            lock (_connectionLock)
            {
                if (conn.UnackedPackets.TryGetValue(sequence, out var pending))
                {
                    pending.Acked = true;

                    // 计算RTT
                    long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    long rtt = now - pending.FirstSendTime;

                    UpdateRTT(conn, rtt);

                    // 从待确认队列移除
                    conn.UnackedPackets.Remove(sequence);

                    // 拥塞控制 - 收到ACK, 增加窗口
                    if (_config.EnableCongestionControl)
                    {
                        IncreaseCongestionWindow(conn);
                    }

                    conn.DuplicateAckCount = 0;
                    conn.LastAckSequence = sequence;
                }
                else
                {
                    // 可能是重复ACK
                    if (sequence == conn.LastAckSequence)
                    {
                        conn.DuplicateAckCount++;

                        // 3次重复ACK, 触发快速重传
                        if (conn.DuplicateAckCount >= 3 && _config.EnableCongestionControl)
                        {
                            FastRetransmit(conn);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// 更新RTT
        /// </summary>
        private void UpdateRTT(ConnectionInfo conn, long rttMs)
        {
            if (rttMs < 0 || rttMs > 5000) return; // 异常值过滤

            float rtt = rttMs;

            if (conn.SmoothedRTT == 0)
            {
                // 第一次测量
                conn.SmoothedRTT = rtt;
                conn.RTTVar = rtt / 2;
            }
            else
            {
                // RFC 6298 算法
                float diff = Math.Abs(conn.SmoothedRTT - rtt);
                conn.RTTVar = conn.RTTVar * 0.75f + diff * 0.25f;
                conn.SmoothedRTT = conn.SmoothedRTT * 0.875f + rtt * 0.125f;
            }

            conn.RTT = rtt;
        }

        /// <summary>
        /// 生成ACK位域
        /// </summary>
        private uint GenerateAckBitfield(ConnectionInfo conn)
        {
            uint bitfield = 0;
            uint latest = conn.RemoteSequence;

            for (int i = 0; i < 32; i++)
            {
                uint seq = (uint)(latest - i - 1);
                if (conn.ReceivedSequences.Contains(seq))
                {
                    bitfield |= (1u << i);
                }
            }

            return bitfield;
        }

        // ==================== 拥塞控制 ====================

        /// <summary>
        /// 增加拥塞窗口
        /// </summary>
        private void IncreaseCongestionWindow(ConnectionInfo conn)
        {
            if (conn.InSlowStart)
            {
                // 慢启动 - 指数增长
                conn.CongestionWindow++;
                if (conn.CongestionWindow >= conn.SlowStartThreshold)
                {
                    conn.InSlowStart = false;
                }
            }
            else
            {
                // 拥塞避免 - 线性增长
                conn.CongestionWindow += 1.0f / conn.CongestionWindow;
            }
        }

        /// <summary>
        /// 快速重传
        /// </summary>
        private void FastRetransmit(ConnectionInfo conn)
        {
            // 减半拥塞窗口
            conn.SlowStartThreshold = Math.Max(conn.CongestionWindow / 2, 2);
            conn.CongestionWindow = conn.SlowStartThreshold;
            conn.InSlowStart = false;
            conn.DuplicateAckCount = 0;

            // 重传最早的未确认包
            uint? oldestSeq = null;
            lock (_connectionLock)
            {
                foreach (var seq in conn.UnackedPackets.Keys)
                {
                    if (oldestSeq == null || IsNewerSequence(oldestSeq.Value, seq))
                    {
                        oldestSeq = seq;
                    }
                }
            }

            if (oldestSeq.HasValue)
            {
                lock (_connectionLock)
                {
                    if (conn.UnackedPackets.TryGetValue(oldestSeq.Value, out var pending))
                    {
                        pending.LastSendTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        pending.ResendCount++;

                        // 直接重发
                        if (_udpClient != null)
                        {
                            try
                            {
                                _udpClient.Send(pending.Data, pending.Data.Length, conn.RemoteEndpoint);
                            }
                            catch { }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// 拥塞回退
        /// </summary>
        private void CongestionBackoff(ConnectionInfo conn)
        {
            conn.SlowStartThreshold = Math.Max(conn.CongestionWindow / 2, 2);
            conn.CongestionWindow = 1;
            conn.InSlowStart = true;
        }

        // ==================== 分片重组 ====================

        /// <summary>
        /// 处理分片包
        /// </summary>
        private void HandleFragment(int connectionId, NetworkPacket packet, ConnectionInfo conn)
        {
            if (packet.Payload == null || packet.Payload.Length < 9) return;

            int groupId = BitConverter.ToInt32(packet.Payload, 1);
            int fragmentIndex = BitConverter.ToUInt16(packet.Payload, 5);
            int totalFragments = BitConverter.ToUInt16(packet.Payload, 7);

            lock (_connectionLock)
            {
                if (!_fragmentBuffers.TryGetValue(connectionId, out var connectionBuffers))
                {
                    connectionBuffers = new Dictionary<int, FragmentAssembler>();
                    _fragmentBuffers[connectionId] = connectionBuffers;
                }

                if (!connectionBuffers.TryGetValue(groupId, out var assembler))
                {
                    assembler = new FragmentAssembler(totalFragments);
                    connectionBuffers[groupId] = assembler;
                }

                // 添加分片
                int fragmentSize = packet.Payload.Length - 9;
                var fragmentData = new byte[fragmentSize];
                Buffer.BlockCopy(packet.Payload, 9, fragmentData, 0, fragmentSize);

                assembler.AddFragment(fragmentIndex, fragmentData);

                // 检查是否完成
                if (assembler.IsComplete)
                {
                    var completeData = assembler.Assemble();

                    // 移除缓冲
                    connectionBuffers.Remove(groupId);

                    // 处理完整的包
                    var completePacket = NetworkPacket.Create(PacketType.Data, completeData, false);
                    completePacket.RemoteEndpoint = packet.RemoteEndpoint;

                    _receiveQueue.Enqueue(completePacket);
                    OnPacketReceived?.Invoke(this, new PacketReceivedEventArgs
                    {
                        ConnectionId = connectionId,
                        Packet = completePacket
                    });
                }
            }
        }

        // ==================== 心跳 ====================

        /// <summary>
        /// 心跳线程
        /// </summary>
        private void HeartbeatLoop()
        {
            while (_running)
            {
                Thread.Sleep(_config.HeartbeatInterval);

                List<ConnectionInfo> connections;
                lock (_connectionLock)
                {
                    connections = new List<ConnectionInfo>(_connections.Values);
                }

                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                foreach (var conn in connections)
                {
                    if (conn.State != ConnectionState.Connected) continue;

                    // 检查超时
                    long lastReceiveAge = now - new DateTimeOffset(conn.LastReceiveTime).ToUnixTimeMilliseconds();
                    if (lastReceiveAge > _config.ConnectionTimeout)
                    {
                        Console.WriteLine($"[NetworkTransport] 连接 {conn.ConnectionId} 超时 (最后接收: {lastReceiveAge}ms前)");

                        OnConnectionTimedOut?.Invoke(this, new ConnectionEventArgs
                        {
                            ConnectionId = conn.ConnectionId,
                            RemoteEndpoint = conn.RemoteEndpoint,
                            Reason = "连接超时"
                        });

                        InternalDisconnect(conn.ConnectionId, "连接超时");
                        continue;
                    }

                    // 发送心跳
                    var heartbeat = NetworkPacket.Create(PacketType.Heartbeat, new byte[8], false);
                    heartbeat.Priority = PacketPriority.Critical;
                    heartbeat.RemoteEndpoint = conn.RemoteEndpoint;

                    // 把当前时间戳放在负载里
                    var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    BitConverter.TryWriteBytes(new Span<byte>(heartbeat.Payload, 0, 8), timestamp);

                    EnqueuePacket(heartbeat);
                }
            }
        }

        // ==================== 重发 ====================

        /// <summary>
        /// 重发线程
        /// </summary>
        private void ResendLoop()
        {
            while (_running)
            {
                Thread.Sleep(_config.ResendInterval);
                ProcessResendPackets();
            }
        }

        /// <summary>
        /// 处理需要重发的包
        /// </summary>
        private void ProcessResendPackets()
        {
            List<ConnectionInfo> connections;
            lock (_connectionLock)
            {
                connections = new List<ConnectionInfo>(_connections.Values);
            }

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            foreach (var conn in connections)
            {
                if (conn.State != ConnectionState.Connected) continue;

                List<uint> toResend = new List<uint>();
                List<uint> toRemove = new List<uint>();

                lock (_connectionLock)
                {
                    foreach (var kvp in conn.UnackedPackets)
                    {
                        var pending = kvp.Value;

                        // 检查是否超时需要重发
                        long age = now - pending.LastSendTime;
                        long timeout = (long)(conn.SmoothedRTT + 4 * conn.RTTVar + 50);

                        if (age > timeout)
                        {
                            if (pending.ResendCount >= _config.MaxResendCount)
                            {
                                // 超过最大重发次数, 放弃
                                toRemove.Add(kvp.Key);

                                if (pending.Type == PacketType.Handshake || pending.Type == PacketType.HandshakeAck)
                                {
                                    // 握手失败
                                    InternalDisconnect(conn.ConnectionId, "握手超时");
                                }
                            }
                            else
                            {
                                toResend.Add(kvp.Key);
                            }
                        }
                    }
                }

                // 执行重发
                foreach (var seq in toResend)
                {
                    ResendPacket(conn, seq, now);
                }

                // 移除放弃的包
                if (toRemove.Count > 0)
                {
                    lock (_connectionLock)
                    {
                        foreach (var seq in toRemove)
                        {
                            conn.UnackedPackets.Remove(seq);
                        }
                    }

                    // 触发拥塞回退
                    if (_config.EnableCongestionControl && toRemove.Count > 0)
                    {
                        CongestionBackoff(conn);
                        conn.PacketsLost += toRemove.Count;
                    }
                }
            }
        }

        /// <summary>
        /// 重发单个包
        /// </summary>
        private void ResendPacket(ConnectionInfo conn, uint sequence, long now)
        {
            lock (_connectionLock)
            {
                if (conn.UnackedPackets.TryGetValue(sequence, out var pending))
                {
                    pending.LastSendTime = now;
                    pending.ResendCount++;

                    if (_udpClient != null)
                    {
                        try
                        {
                            _udpClient.Send(pending.Data, pending.Data.Length, conn.RemoteEndpoint);
                            conn.BytesSent += pending.Data.Length;
                            conn.PacketsSent++;

                            Interlocked.Add(ref _totalBytesSent, pending.Data.Length);
                            Interlocked.Increment(ref _totalPacketsSent);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[NetworkTransport] 重发失败: {ex.Message}");
                        }
                    }
                }
            }
        }

        // ==================== 工具方法 ====================

        /// <summary>
        /// 判断seq是否比ackSeq新
        /// </summary>
        private bool IsNewerSequence(uint seq, uint ackSeq)
        {
            // 处理序号回绕
            int diff = (int)(seq - ackSeq);
            return diff > 0;
        }

        /// <summary>
        /// 获取连接信息
        /// </summary>
        public ConnectionInfo GetConnection(int connectionId)
        {
            lock (_connectionLock)
            {
                if (_connections.TryGetValue(connectionId, out var conn))
                {
                    return conn;
                }
            }
            return null;
        }

        /// <summary>
        /// 获取所有连接
        /// </summary>
        public List<ConnectionInfo> GetAllConnections()
        {
            lock (_connectionLock)
            {
                return new List<ConnectionInfo>(_connections.Values);
            }
        }

        /// <summary>
        /// 从接收队列取一个包
        /// </summary>
        public bool TryReceive(out NetworkPacket packet)
        {
            return _receiveQueue.TryDequeue(out packet);
        }

        /// <summary>
        /// 获取接收队列中的包数量
        /// </summary>
        public int ReceiveQueueCount => _receiveQueue.Count;

        /// <summary>
        /// 获取待发送的包总数
        /// </summary>
        public int PendingSendCount
        {
            get
            {
                int count = 0;
                for (int i = 0; i < 4; i++)
                {
                    count += _sendQueues[i].Count;
                }
                return count;
            }
        }

        // ==================== 模拟网络条件 ====================

        /// <summary>
        /// 设置模拟丢包率(测试用)
        /// </summary>
        public void SetSimulateLossRate(float rate)
        {
            _simulateLossRate = Math.Clamp(rate, 0f, 1f);
            Console.WriteLine($"[NetworkTransport] 模拟丢包率设置为: {_simulateLossRate:P}");
        }

        /// <summary>
        /// 设置模拟延迟(测试用)
        /// </summary>
        public void SetSimulateLatency(int latencyMs, int jitterMs = 0)
        {
            _simulateLatency = Math.Max(0, latencyMs);
            _simulateJitter = Math.Max(0, jitterMs);
            Console.WriteLine($"[NetworkTransport] 模拟延迟设置为: {_simulateLatency}ms (抖动: {_simulateJitter}ms)");
        }

        // ==================== 统计信息 ====================

        /// <summary>
        /// 获取总发送字节数
        /// </summary>
        public long TotalBytesSent => Interlocked.Read(ref _totalBytesSent);

        /// <summary>
        /// 获取总接收字节数
        /// </summary>
        public long TotalBytesReceived => Interlocked.Read(ref _totalBytesReceived);

        /// <summary>
        /// 获取总发送包数
        /// </summary>
        public long TotalPacketsSent => Interlocked.Read(ref _totalPacketsSent);

        /// <summary>
        /// 获取总接收包数
        /// </summary>
        public long TotalPacketsReceived => Interlocked.Read(ref _totalPacketsReceived);

        /// <summary>
        /// 获取发送速率(bytes/s)
        /// </summary>
        public float SendRate
        {
            get
            {
                lock (_rateLimitLock)
                {
                    long elapsed = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _sendRateWindowStart;
                    if (elapsed <= 0) return 0;
                    return _sendRateBytesSent * 1000f / elapsed;
                }
            }
        }

        /// <summary>
        /// 是否正在运行
        /// </summary>
        public bool IsRunning => _running;

        /// <summary>
        /// 是否是服务端
        /// </summary>
        public bool IsServer => _isServer;

        /// <summary>
        /// 本地端点
        /// </summary>
        public IPEndPoint LocalEndpoint => _localEndpoint;

        /// <summary>
        /// 获取传输统计报告
        /// </summary>
        public string GetStatisticsReport()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("===== 传输层统计 =====");
            sb.AppendLine($"总发送: {TotalBytesSent:N0} 字节, {TotalPacketsSent:N0} 包");
            sb.AppendLine($"总接收: {TotalBytesReceived:N0} 字节, {TotalPacketsReceived:N0} 包");
            sb.AppendLine($"发送速率: {SendRate / 1024:F2} KB/s");
            sb.AppendLine($"待发送: {PendingSendCount} 包");
            sb.AppendLine($"待接收: {ReceiveQueueCount} 包");
            sb.AppendLine($"活跃连接数: {_connections.Count}");

            lock (_connectionLock)
            {
                foreach (var conn in _connections.Values)
                {
                    sb.AppendLine($"  连接 {conn.ConnectionId} ({conn.RemoteEndpoint}):");
                    sb.AppendLine($"    状态: {conn.State}");
                    sb.AppendLine($"    RTT: {conn.SmoothedRTT:F1}ms (±{conn.RTTVar:F1}ms)");
                    sb.AppendLine($"    丢包率: {conn.PacketLoss:P2}");
                    sb.AppendLine($"    发送: {conn.BytesSent:N0}字节 / {conn.PacketsSent:N0}包");
                    sb.AppendLine($"    接收: {conn.BytesReceived:N0}字节 / {conn.PacketsReceived:N0}包");
                    sb.AppendLine($"    拥塞窗口: {conn.CongestionWindow}");
                    sb.AppendLine($"    未确认包: {conn.UnackedPackets.Count}");
                }
            }

            return sb.ToString();
        }

        // ==================== Dispose ====================

        /// <summary>
        /// 释放资源
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed) return;
            _disposed = true;

            if (disposing)
            {
                Stop();
                _sendSignal?.Dispose();
            }
        }

        ~NetworkTransportLayer()
        {
            Dispose(false);
        }
    }

    /// <summary>
    /// 分片重组器
    /// </summary>
    internal class FragmentAssembler
    {
        private readonly byte[][] _fragments;
        private readonly bool[] _received;
        private int _count;
        private readonly int _total;

        public FragmentAssembler(int totalFragments)
        {
            _total = totalFragments;
            _fragments = new byte[totalFragments][];
            _received = new bool[totalFragments];
            _count = 0;
        }

        public void AddFragment(int index, byte[] data)
        {
            if (index < 0 || index >= _total) return;
            if (_received[index]) return;

            _fragments[index] = data;
            _received[index] = true;
            _count++;
        }

        public bool IsComplete => _count == _total;

        public byte[] Assemble()
        {
            if (!IsComplete) return null;

            int totalSize = 0;
            for (int i = 0; i < _total; i++)
            {
                totalSize += _fragments[i].Length;
            }

            var result = new byte[totalSize];
            int offset = 0;
            for (int i = 0; i < _total; i++)
            {
                Buffer.BlockCopy(_fragments[i], 0, result, offset, _fragments[i].Length);
                offset += _fragments[i].Length;
            }

            return result;
        }
    }
}
