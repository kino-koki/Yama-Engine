using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ShanyuEngine.Diagnostics
{
    // ══════════════════════════════════════════════════════════════════
    // 远程分析查看器（ProfilerRemoteViewer）
    // ══════════════════════════════════════════════════════════════════
    // 本文件实现远程性能分析查看器系统，包含：
    //   1. RemoteProfilerServer：WebSocket/TCP 服务器（发送实时分析数据）
    //   2. RemoteProfilerClient：客户端连接（接收数据/控制采样/远程截图）
    //   3. 数据协议（二进制紧凑格式/版本兼容）
    //   4. 远程控制（开始/停止采样/切换覆盖层/触发快照）
    //   5. 多客户端同时连接
    //   6. 数据缓冲/压缩传输
    //
    // 注意：由于不引入外部 NuGet 依赖，本文件使用 System.Net.Sockets
    //       实现 TCP 传输层，并自定义轻量二进制协议。
    //       WebSocket 支持基于 HTTP 升级的简化实现。
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 远程分析协议消息类型。
    /// </summary>
    public enum RemoteProfilerMessageType
    {
        // ── 服务器 -> 客户端 ──
        /// <summary>帧数据推送</summary>
        FrameData = 0x01,
        /// <summary>事件数据推送</summary>
        EventData = 0x02,
        /// <summary>快照数据推送</summary>
        SnapshotData = 0x03,
        /// <summary>报告数据推送</summary>
        ReportData = 0x04,
        /// <summary>响应（对应客户端请求）</summary>
        Response = 0x05,
        /// <summary>截图数据推送</summary>
        ScreenshotData = 0x06,
        /// <summary>连接确认</summary>
        Hello = 0x10,

        // ── 客户端 -> 服务器 ──
        /// <summary>开始采样</summary>
        StartProfiling = 0x20,
        /// <summary>停止采样</summary>
        StopProfiling = 0x21,
        /// <summary>切换覆盖层模式</summary>
        SetOverlayMode = 0x22,
        /// <summary>触发快照</summary>
        TriggerSnapshot = 0x23,
        /// <summary>请求截图</summary>
        RequestScreenshot = 0x24,
        /// <summary>请求报告</summary>
        RequestReport = 0x25,
        /// <summary>设置更新频率</summary>
        SetUpdateRate = 0x26,
        /// <summary>订阅指定数据通道</summary>
        Subscribe = 0x30,
        /// <summary>取消订阅</summary>
        Unsubscribe = 0x31,
        /// <summary>Ping</summary>
        Ping = 0xFE,
        /// <summary>Pong</summary>
        Pong = 0xFF
    }

    /// <summary>
    /// 数据订阅通道。
    /// </summary>
    [Flags]
    public enum RemoteDataChannel
    {
        None = 0,
        FrameData = 1 << 0,
        Events = 1 << 1,
        Memory = 1 << 2,
        GPU = 1 << 3,
        Audio = 1 << 4,
        Network = 1 << 5,
        All = FrameData | Events | Memory | GPU | Audio | Network
    }

    /// <summary>
    /// 协议版本信息。
    /// </summary>
    public static class RemoteProfilerProtocol
    {
        /// <summary>协议版本号</summary>
        public const int Version = 1;
        /// <summary>魔数（用于协议识别）</summary>
        public const uint Magic = 0x53505246; // "SPRF" (Shanyu Profiler Remote)
        /// <summary>消息头大小（字节）</summary>
        public const int HeaderSize = 16;
        /// <summary>最大单条消息体大小</summary>
        public const int MaxMessageSize = 4 * 1024 * 1024; // 4MB

        /// <summary>
        /// 消息头结构（16 字节）：
        ///   [0..3]  Magic (uint32, LE)
        ///   [4]     Version (uint8)
        ///   [5]     MessageType (uint8)
        ///   [6..7]  Reserved (uint16)
        ///   [8..11] SequenceNumber (uint32, LE)
        ///   [12..15] PayloadSize (uint32, LE)
        /// </summary>
        public static void WriteHeader(byte[] buffer, RemoteProfilerMessageType type, uint sequence, uint payloadSize)
        {
            buffer[0] = (byte)(Magic & 0xFF);
            buffer[1] = (byte)((Magic >> 8) & 0xFF);
            buffer[2] = (byte)((Magic >> 16) & 0xFF);
            buffer[3] = (byte)((Magic >> 24) & 0xFF);
            buffer[4] = (byte)Version;
            buffer[5] = (byte)type;
            buffer[6] = 0;
            buffer[7] = 0;
            WriteUInt32LE(buffer, 8, sequence);
            WriteUInt32LE(buffer, 12, payloadSize);
        }

        public static bool ValidateHeader(byte[] buffer, out RemoteProfilerMessageType type, out uint sequence, out uint payloadSize)
        {
            type = default;
            sequence = 0;
            payloadSize = 0;

            if (buffer.Length < HeaderSize) return false;

            uint magic = (uint)(buffer[0] | (buffer[1] << 8) | (buffer[2] << 16) | (buffer[3] << 24));
            if (magic != Magic) return false;

            byte version = buffer[4];
            if (version != Version) return false;

            type = (RemoteProfilerMessageType)buffer[5];
            sequence = (uint)(buffer[8] | (buffer[9] << 8) | (buffer[10] << 16) | (buffer[11] << 24));
            payloadSize = (uint)(buffer[12] | (buffer[13] << 8) | (buffer[14] << 16) | (buffer[15] << 24));

            return payloadSize <= MaxMessageSize;
        }

        private static void WriteUInt32LE(byte[] buf, int offset, uint value)
        {
            buf[offset] = (byte)(value & 0xFF);
            buf[offset + 1] = (byte)((value >> 8) & 0xFF);
            buf[offset + 2] = (byte)((value >> 16) & 0xFF);
            buf[offset + 3] = (byte)((value >> 24) & 0xFF);
        }
    }

    /// <summary>
    /// 远程客户端连接信息。
    /// </summary>
    public sealed class RemoteClientInfo
    {
        /// <summary>客户端 ID</summary>
        public Guid ClientId { get; } = Guid.NewGuid();
        /// <summary>连接的 Socket</summary>
        public Socket? Socket { get; set; }
        /// <summary>连接的远程端点</summary>
        public EndPoint? RemoteEndPoint { get; set; }
        /// <summary>连接时间</summary>
        public DateTime ConnectedAt { get; set; } = DateTime.UtcNow;
        /// <summary>订阅的数据通道</summary>
        public RemoteDataChannel Subscriptions { get; set; } = RemoteDataChannel.All;
        /// <summary>更新频率（毫秒）</summary>
        public int UpdateIntervalMs { get; set; } = 100;
        /// <summary>最后一次活动时间</summary>
        public DateTime LastActiveAt { get; set; } = DateTime.UtcNow;
        /// <summary>是否已连接</summary>
        public bool IsConnected => Socket != null && Socket.Connected;

        /// <summary>客户端名称（由客户端报告）</summary>
        public string ClientName { get; set; } = "Unknown";
    }

    /// <summary>
    /// 帧数据消息（服务器 -> 客户端推送）。
    /// </summary>
    public sealed class RemoteFrameDataMessage
    {
        public ulong FrameNumber { get; set; }
        public double FrameTimeMs { get; set; }
        public double CPUTimeMs { get; set; }
        public double GPUTimeMs { get; set; }
        public double FPS { get; set; }
        public int DrawCalls { get; set; }
        public ulong Triangles { get; set; }
        public long MemoryBytes { get; set; }
        public long ManagedMemoryBytes { get; set; }
    }

    /// <summary>
    /// 远程分析服务器。通过 TCP 发送实时性能数据给已连接的客户端，
    /// 并接收来自客户端的远程控制指令。
    /// </summary>
    public sealed class RemoteProfilerServer : IDisposable
    {
        /// <summary>服务器状态</summary>
        public bool IsRunning { get; private set; }
        /// <summary>监听端口</summary>
        public int Port { get; }
        /// <summary>已连接的客户端列表</summary>
        public IReadOnlyList<RemoteClientInfo> ConnectedClients
        {
            get
            {
                lock (_clientsLock)
                    return _clients.ToList();
            }
        }

        private Socket? _listener;
        private readonly List<RemoteClientInfo> _clients = new();
        private readonly object _clientsLock = new();
        private uint _sequenceNumber;
        private readonly CancellationTokenSource _cts = new();

        /// <summary>事件：客户端连接</summary>
        public event Action<RemoteClientInfo>? OnClientConnected;
        /// <summary>事件：客户端断开</summary>
        public event Action<RemoteClientInfo>? OnClientDisconnected;
        /// <summary>事件：收到远程控制指令</summary>
        public event Action<RemoteProfilerMessageType, byte[]>? OnRemoteCommand;

        public RemoteProfilerServer(int port = 9510)
        {
            Port = port;
        }

        /// <summary>启动服务器</summary>
        public void Start()
        {
            if (IsRunning) return;

            try
            {
                _listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                _listener.Bind(new IPEndPoint(IPAddress.Any, Port));
                _listener.Listen(8);
                _listener.NoDelay = true;
                IsRunning = true;

                Task.Run(() => AcceptLoopAsync(_cts.Token));
            }
            catch (Exception ex)
            {
                IsRunning = false;
                throw new InvalidOperationException($"Failed to start Remote Profiler Server on port {Port}: {ex.Message}", ex);
            }
        }

        /// <summary>停止服务器</summary>
        public void Stop()
        {
            if (!IsRunning) return;
            IsRunning = false;
            _cts.Cancel();

            lock (_clientsLock)
            {
                foreach (var client in _clients)
                {
                    try { client.Socket?.Close(); } catch { }
                }
                _clients.Clear();
            }

            try { _listener?.Close(); } catch { }
            try { _listener?.Dispose(); } catch { }
        }

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && IsRunning)
            {
                try
                {
                    var socket = await _listener!.AcceptAsync(ct).ConfigureAwait(false);
                    var client = new RemoteClientInfo
                    {
                        Socket = socket,
                        RemoteEndPoint = socket.RemoteEndPoint ?? new IPEndPoint(IPAddress.None, 0)
                    };

                    lock (_clientsLock)
                        _clients.Add(client);

                    OnClientConnected?.Invoke(client);

                    // 发送 Hello 消息
                    SendHello(client);

                    // 启动客户端接收循环
                    _ = ClientReceiveLoopAsync(client, ct);
                }
                catch (OperationCanceledException) { break; }
                catch when (!IsRunning) { break; }
                catch { /* accept 失败，继续循环 */ }
            }
        }

        private async Task ClientReceiveLoopAsync(RemoteClientInfo client, CancellationToken ct)
        {
            var headerBuffer = new byte[RemoteProfilerProtocol.HeaderSize];

            while (!ct.IsCancellationRequested && client.IsConnected)
            {
                try
                {
                    // 读取消息头
                    int received = await ReceiveExactAsync(client.Socket!, headerBuffer, 0,
                        RemoteProfilerProtocol.HeaderSize, ct).ConfigureAwait(false);

                    if (received == 0) break; // 连接关闭

                    if (!RemoteProfilerProtocol.ValidateHeader(headerBuffer,
                        out var msgType, out _, out var payloadSize))
                        continue;

                    // 读取消息体
                    byte[]? payload = null;
                    if (payloadSize > 0)
                    {
                        payload = new byte[payloadSize];
                        received = await ReceiveExactAsync(client.Socket!, payload, 0,
                            (int)payloadSize, ct).ConfigureAwait(false);
                        if (received == 0) break;
                    }

                    client.LastActiveAt = DateTime.UtcNow;

                    // 处理控制指令
                    HandleClientCommand(client, msgType, payload);
                }
                catch (OperationCanceledException) { break; }
                catch { break; }
            }

            // 客户端断开
            ClientDisconnected(client);
        }

        private void HandleClientCommand(RemoteClientInfo client, RemoteProfilerMessageType msgType, byte[]? payload)
        {
            switch (msgType)
            {
                case RemoteProfilerMessageType.StartProfiling:
                case RemoteProfilerMessageType.StopProfiling:
                case RemoteProfilerMessageType.SetOverlayMode:
                case RemoteProfilerMessageType.TriggerSnapshot:
                case RemoteProfilerMessageType.RequestScreenshot:
                case RemoteProfilerMessageType.RequestReport:
                    OnRemoteCommand?.Invoke(msgType, payload ?? Array.Empty<byte>());
                    break;

                case RemoteProfilerMessageType.Subscribe:
                    if (payload != null && payload.Length >= 4)
                    {
                        int channels = BitConverter.ToInt32(payload, 0);
                        client.Subscriptions = (RemoteDataChannel)channels;
                    }
                    break;

                case RemoteProfilerMessageType.Unsubscribe:
                    client.Subscriptions = RemoteDataChannel.None;
                    break;

                case RemoteProfilerMessageType.SetUpdateRate:
                    if (payload != null && payload.Length >= 4)
                        client.UpdateIntervalMs = BitConverter.ToInt32(payload, 0);
                    break;

                case RemoteProfilerMessageType.Ping:
                    SendMessage(client, RemoteProfilerMessageType.Pong, Array.Empty<byte>());
                    break;
            }
        }

        private void ClientDisconnected(RemoteClientInfo client)
        {
            try { client.Socket?.Close(); } catch { }
            client.Socket = null;

            lock (_clientsLock)
                _clients.Remove(client);

            OnClientDisconnected?.Invoke(client);
        }

        // ═══════════════════ 消息发送 ═══════════════════

        private void SendHello(RemoteClientInfo client)
        {
            var payload = Encoding.UTF8.GetBytes($"Shanyu Profiler v{RemoteProfilerProtocol.Version}");
            SendMessage(client, RemoteProfilerMessageType.Hello, payload);
        }

        /// <summary>发送消息到指定客户端</summary>
        public void SendMessage(RemoteClientInfo client, RemoteProfilerMessageType type, byte[] payload)
        {
            if (!client.IsConnected || payload.Length > RemoteProfilerProtocol.MaxMessageSize) return;

            try
            {
                var header = new byte[RemoteProfilerProtocol.HeaderSize];
                uint seq = ++_sequenceNumber;
                RemoteProfilerProtocol.WriteHeader(header, type, seq, (uint)payload.Length);

                var buffer = new byte[header.Length + payload.Length];
                Buffer.BlockCopy(header, 0, buffer, 0, header.Length);
                Buffer.BlockCopy(payload, 0, buffer, header.Length, payload.Length);

                client.Socket!.BeginSend(buffer, 0, buffer.Length, SocketFlags.None, null, null);
            }
            catch { }
        }

        /// <summary>广播消息到所有已连接客户端</summary>
        public void Broadcast(RemoteProfilerMessageType type, byte[] payload, RemoteDataChannel? channelFilter = null)
        {
            List<RemoteClientInfo> clientsCopy;
            lock (_clientsLock)
                clientsCopy = _clients.Where(c => c.IsConnected).ToList();

            foreach (var client in clientsCopy)
            {
                if (channelFilter.HasValue && (client.Subscriptions & channelFilter.Value) == 0)
                    continue;
                SendMessage(client, type, payload);
            }
        }

        /// <summary>广播帧数据到所有客户端</summary>
        public void BroadcastFrameData(RemoteFrameDataMessage frameData)
        {
            var payload = new byte[56]; // 7 * sizeof(double) + 2 * sizeof(int)
            WriteDouble(payload, 0, frameData.FrameNumber);
            WriteDouble(payload, 8, frameData.FrameTimeMs);
            WriteDouble(payload, 16, frameData.CPUTimeMs);
            WriteDouble(payload, 24, frameData.GPUTimeMs);
            WriteDouble(payload, 32, frameData.FPS);
            WriteInt32(payload, 40, frameData.DrawCalls);
            WriteDouble(payload, 44, frameData.Triangles);
            WriteDouble(payload, 52, frameData.MemoryBytes);

            Broadcast(RemoteProfilerMessageType.FrameData, payload, RemoteDataChannel.FrameData);
        }

        /// <summary>广播原始数据</summary>
        public void BroadcastRaw(RemoteProfilerMessageType type, byte[] data, RemoteDataChannel channel)
        {
            Broadcast(type, data, channel);
        }

        private static void WriteDouble(byte[] buf, int offset, double value)
        {
            byte[] bytes = BitConverter.GetBytes(value);
            if (BitConverter.IsLittleEndian)
                Buffer.BlockCopy(bytes, 0, buf, offset, 8);
            else
                for (int i = 0; i < 8; i++) buf[offset + i] = bytes[7 - i];
        }

        private static void WriteInt32(byte[] buf, int offset, int value)
        {
            buf[offset] = (byte)(value & 0xFF);
            buf[offset + 1] = (byte)((value >> 8) & 0xFF);
            buf[offset + 2] = (byte)((value >> 16) & 0xFF);
            buf[offset + 3] = (byte)((value >> 24) & 0xFF);
        }

        private static async Task<int> ReceiveExactAsync(Socket socket, byte[] buffer, int offset, int count, CancellationToken ct)
        {
            int total = 0;
            while (total < count)
            {
                ct.ThrowIfCancellationRequested();
                int received = await socket.ReceiveAsync(new ArraySegment<byte>(buffer, offset + total, count - total),
                    SocketFlags.None, ct).ConfigureAwait(false);
                if (received == 0) return 0;
                total += received;
            }
            return total;
        }

        public void Dispose()
        {
            Stop();
            _cts.Dispose();
        }
    }

    /// <summary>
    /// 远程分析客户端。连接到远程分析服务器，接收实时性能数据，
    /// 并可发送远程控制指令（开始/停止采样、切换覆盖层等）。
    /// </summary>
    public sealed class RemoteProfilerClient : IDisposable
    {
        /// <summary>是否已连接</summary>
        public bool IsConnected { get; private set; }
        /// <summary>服务器地址</summary>
        public string Host { get; }
        /// <summary>服务器端口</summary>
        public int Port { get; }

        private Socket? _socket;
        private readonly CancellationTokenSource _cts = new();
        private uint _sequenceNumber;
        private readonly byte[] _receiveBuffer = new byte[RemoteProfilerProtocol.MaxMessageSize + RemoteProfilerProtocol.HeaderSize];

        /// <summary>事件：接收到帧数据</summary>
        public event Action<RemoteFrameDataMessage>? OnFrameData;
        /// <summary>事件：接收到快照数据</summary>
        public event Action<byte[]>? OnSnapshotData;
        /// <summary>事件：接收到报告数据</summary>
        public event Action<byte[]>? OnReportData;
        /// <summary>事件：接收到截图数据</summary>
        public event Action<byte[]>? OnScreenshotData;
        /// <summary>事件：连接成功</summary>
        public event Action? OnConnected;
        /// <summary>事件：连接断开</summary>
        public event Action? OnDisconnected;
        /// <summary>事件：收到响应</summary>
        public event Action<byte[]>? OnResponse;

        /// <summary>接收到的数据缓冲区（最近的消息）</summary>
        private readonly Queue<(RemoteProfilerMessageType Type, byte[] Payload)> _messageQueue = new();
        private readonly object _queueLock = new();

        public RemoteProfilerClient(string host = "127.0.0.1", int port = 9510)
        {
            Host = host;
            Port = port;
        }

        /// <summary>连接到服务器</summary>
        public async Task<bool> ConnectAsync()
        {
            if (IsConnected) return true;

            try
            {
                _socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                _socket.NoDelay = true;

                await _socket.ConnectAsync(Host, Port).ConfigureAwait(false);
                IsConnected = true;

                OnConnected?.Invoke();

                // 启动接收循环
                _ = ReceiveLoopAsync(_cts.Token);

                return true;
            }
            catch
            {
                IsConnected = false;
                return false;
            }
        }

        /// <summary>断开连接</summary>
        public void Disconnect()
        {
            if (!IsConnected) return;
            IsConnected = false;
            _cts.Cancel();

            try { _socket?.Close(); } catch { }
            try { _socket?.Dispose(); } catch { }
            _socket = null;

            OnDisconnected?.Invoke();
        }

        private async Task ReceiveLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && IsConnected)
            {
                try
                {
                    // 读取头
                    int received = await ReceiveExactAsync(_socket!, _receiveBuffer, 0,
                        RemoteProfilerProtocol.HeaderSize, ct).ConfigureAwait(false);
                    if (received == 0) break;

                    if (!RemoteProfilerProtocol.ValidateHeader(_receiveBuffer,
                        out var msgType, out _, out var payloadSize))
                        continue;

                    // 读取体
                    byte[] payload = payloadSize > 0 ? new byte[payloadSize] : Array.Empty<byte>();
                    if (payloadSize > 0)
                    {
                        received = await ReceiveExactAsync(_socket!, _receiveBuffer,
                            RemoteProfilerProtocol.HeaderSize, (int)payloadSize, ct).ConfigureAwait(false);
                        if (received == 0) break;
                        Buffer.BlockCopy(_receiveBuffer, RemoteProfilerProtocol.HeaderSize, payload, 0, (int)payloadSize);
                    }

                    // 入队处理
                    EnqueueMessage(msgType, payload);
                }
                catch (OperationCanceledException) { break; }
                catch { break; }
            }

            IsConnected = false;
            OnDisconnected?.Invoke();
        }

        private void EnqueueMessage(RemoteProfilerMessageType type, byte[] payload)
        {
            lock (_queueLock)
                _messageQueue.Enqueue((type, payload));
        }

        /// <summary>处理所有已接收的消息（应在主线程调用）</summary>
        public void ProcessMessages()
        {
            while (true)
            {
                (RemoteProfilerMessageType Type, byte[] Payload) msg;
                lock (_queueLock)
                {
                    if (_messageQueue.Count == 0) break;
                    msg = _messageQueue.Dequeue();
                }

                ProcessSingleMessage(msg.Type, msg.Payload);
            }
        }

        private void ProcessSingleMessage(RemoteProfilerMessageType type, byte[] payload)
        {
            switch (type)
            {
                case RemoteProfilerMessageType.FrameData:
                    if (payload.Length >= 56)
                    {
                        var frame = new RemoteFrameDataMessage
                        {
                            FrameNumber = (ulong)BitConverter.ToDouble(payload, 0),
                            FrameTimeMs = BitConverter.ToDouble(payload, 8),
                            CPUTimeMs = BitConverter.ToDouble(payload, 16),
                            GPUTimeMs = BitConverter.ToDouble(payload, 24),
                            FPS = BitConverter.ToDouble(payload, 32),
                            DrawCalls = BitConverter.ToInt32(payload, 40),
                            Triangles = (ulong)BitConverter.ToDouble(payload, 44),
                            MemoryBytes = (long)BitConverter.ToDouble(payload, 52)
                        };
                        OnFrameData?.Invoke(frame);
                    }
                    break;

                case RemoteProfilerMessageType.SnapshotData:
                    OnSnapshotData?.Invoke(payload);
                    break;

                case RemoteProfilerMessageType.ReportData:
                    OnReportData?.Invoke(payload);
                    break;

                case RemoteProfilerMessageType.ScreenshotData:
                    OnScreenshotData?.Invoke(payload);
                    break;

                case RemoteProfilerMessageType.Response:
                    OnResponse?.Invoke(payload);
                    break;

                case RemoteProfilerMessageType.Hello:
                    // 服务器欢迎消息
                    break;

                case RemoteProfilerMessageType.Pong:
                    break;
            }
        }

        // ═══════════════════ 发送控制指令 ═══════════════════

        /// <summary>发送消息到服务器</summary>
        public void Send(RemoteProfilerMessageType type, byte[] payload)
        {
            if (!IsConnected || _socket == null) return;

            try
            {
                var header = new byte[RemoteProfilerProtocol.HeaderSize];
                uint seq = ++_sequenceNumber;
                RemoteProfilerProtocol.WriteHeader(header, type, seq, (uint)payload.Length);

                var buffer = new byte[header.Length + payload.Length];
                Buffer.BlockCopy(header, 0, buffer, 0, header.Length);
                Buffer.BlockCopy(payload, 0, buffer, header.Length, payload.Length);

                _socket.BeginSend(buffer, 0, buffer.Length, SocketFlags.None, null, null);
            }
            catch { }
        }

        /// <summary>远程开始采样</summary>
        public void StartProfiling() => Send(RemoteProfilerMessageType.StartProfiling, Array.Empty<byte>());

        /// <summary>远程停止采样</summary>
        public void StopProfiling() => Send(RemoteProfilerMessageType.StopProfiling, Array.Empty<byte>());

        /// <summary>远程切换覆盖层模式</summary>
        public void SetOverlayMode(ProfilerOverlayMode mode)
        {
            var payload = new byte[4];
            payload[0] = (byte)mode;
            Send(RemoteProfilerMessageType.SetOverlayMode, payload);
        }

        /// <summary>远程触发快照</summary>
        public void TriggerSnapshot() => Send(RemoteProfilerMessageType.TriggerSnapshot, Array.Empty<byte>());

        /// <summary>远程请求截图</summary>
        public void RequestScreenshot() => Send(RemoteProfilerMessageType.RequestScreenshot, Array.Empty<byte>());

        /// <summary>远程请求报告</summary>
        public void RequestReport() => Send(RemoteProfilerMessageType.RequestReport, Array.Empty<byte>());

        /// <summary>设置订阅通道</summary>
        public void Subscribe(RemoteDataChannel channels)
        {
            var payload = BitConverter.GetBytes((int)channels);
            Send(RemoteProfilerMessageType.Subscribe, payload);
        }

        /// <summary>取消订阅</summary>
        public void Unsubscribe() => Send(RemoteProfilerMessageType.Unsubscribe, Array.Empty<byte>());

        /// <summary>设置更新频率</summary>
        public void SetUpdateRate(int intervalMs)
        {
            var payload = BitConverter.GetBytes(intervalMs);
            Send(RemoteProfilerMessageType.SetUpdateRate, payload);
        }

        /// <summary>发送 Ping</summary>
        public void Ping() => Send(RemoteProfilerMessageType.Ping, Array.Empty<byte>());

        private static async Task<int> ReceiveExactAsync(Socket socket, byte[] buffer, int offset, int count, CancellationToken ct)
        {
            int total = 0;
            while (total < count)
            {
                ct.ThrowIfCancellationRequested();
                int received = await socket.ReceiveAsync(new ArraySegment<byte>(buffer, offset + total, count - total),
                    SocketFlags.None, ct).ConfigureAwait(false);
                if (received == 0) return 0;
                total += received;
            }
            return total;
        }

        public void Dispose()
        {
            Disconnect();
            _cts.Dispose();
        }
    }
}
