using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading;

using LogLevel = ShanyuEngine.Config.LogLevel;
namespace ShanyuEngine.Network
{
    // ════════════════════════════════════════════════════════════════════════
    // 山雨引擎 - 网络调试系统 (NetworkDebugSystem)
    //
    // 完整的网络调试与诊断管线, 涵盖:
    //   * NetworkStats: 实时统计 (发送/接收字节/包数/带宽/延迟/抖动/丢包)
    //   * NetworkConsole: 网络控制台命令 (netstat/players/kick/ban/ping/dump/trace)
    //   * NetworkLogger: 网络日志 (RPC/属性变更/连接事件)
    //   * NetworkConditionSimulator: 网络模拟 (延迟注入/丢包/带宽限制/抖动)
    //   * NetworkVisualizer: 连接状态/数据流图/延迟图/带宽图
    //   * NetworkProfiler 集成 (对接 Diagnostics.NetworkProfiler)
    //
    // 对接系统: NetworkCoreSystem / RPCSystem / 全部网络子系统
    // ════════════════════════════════════════════════════════════════════════

    // ──────────────────────────────────────────────────────────────
    // 1. 网络统计
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 网络实时统计数据。追踪发送/接收字节、包数、带宽、延迟、抖动、丢包率等。
    /// </summary>
    public sealed class NetworkStats
    {
        // ── 流量 ──────────────────────────────────────────────────

        /// <summary>发送字节总数。</summary>
        public long BytesSent { get; private set; }
        /// <summary>接收字节总数。</summary>
        public long BytesReceived { get; private set; }
        /// <summary>发送包总数。</summary>
        public long PacketsSent { get; private set; }
        /// <summary>接收包总数。</summary>
        public long PacketsReceived { get; private set; }
        /// <summary>上行带宽 (字节/秒)。</summary>
        public double SendBandwidthBps { get; private set; }
        /// <summary>下行带宽 (字节/秒)。</summary>
        public double ReceiveBandwidthBps { get; private set; }

        // ── 延迟 ──────────────────────────────────────────────────

        /// <summary>当前 RTT (毫秒)。</summary>
        public float RTT { get; private set; }
        /// <summary>平均 RTT (毫秒)。</summary>
        public float AverageRTT { get; private set; }
        /// <summary>最小 RTT (毫秒)。</summary>
        public float MinRTT { get; private set; } = float.MaxValue;
        /// <summary>最大 RTT (毫秒)。</summary>
        public float MaxRTT;
        /// <summary>RTT 抖动 (毫秒)。</summary>
        public float Jitter { get; private set; }

        // ── 丢包 ──────────────────────────────────────────────────

        /// <summary>发送丢包数。</summary>
        public long PacketsLostSent { get; private set; }
        /// <summary>接收丢包数。</summary>
        public long PacketsLostReceived { get; private set; }
        /// <summary>丢包率 (0~1)。</summary>
        public float PacketLossRate { get; private set; }

        // ── RPC / 属性同步 ────────────────────────────────────────

        /// <summary>RPC 调用总数。</summary>
        public long RPCCallCount { get; private set; }
        /// <summary>属性同步总数。</summary>
        public long PropertySyncCount { get; private set; }

        // ── 带宽采样 ──────────────────────────────────────────────

        private long _lastBytesSent;
        private long _lastBytesReceived;
        private DateTime _lastSampleTime = DateTime.UtcNow;
        private float _rttSmoothFactor = 0.1f;
        private float _rttAccum;
        private int _rttSamples;
        private float _jitterAccum;
        private int _jitterSamples;

        /// <summary>记录发送数据。</summary>
        public void RecordSent(int bytes)
        {
            Interlocked.Add(ref BytesSent, bytes);
            Interlocked.Increment(ref PacketsSent);
        }

        /// <summary>记录接收数据。</summary>
        public void RecordReceived(int bytes)
        {
            Interlocked.Add(ref BytesReceived, bytes);
            Interlocked.Increment(ref PacketsReceived);
        }

        /// <summary>记录 RTT。</summary>
        public void RecordRTT(float rttMs)
        {
            RTT = rttMs;
            AverageRTT = AverageRTT == 0 ? rttMs : AverageRTT * (1f - _rttSmoothFactor) + rttMs * _rttSmoothFactor;
            MinRTT = Math.Min(MinRTT, rttMs);
            MaxRTT = Math.Max(MaxRTT, rttMs);

            float jitterDelta = Math.Abs(rttMs - AverageRTT);
            Jitter = Jitter == 0 ? jitterDelta : Jitter * 0.9f + jitterDelta * 0.1f;

            _rttAccum += rttMs;
            _rttSamples++;
        }

        /// <summary>记录丢包。</summary>
        public void RecordPacketLoss(int sentCount, int receivedCount)
        {
            PacketsLostSent = sentCount - receivedCount;
            if (sentCount > 0)
                PacketLossRate = (float)PacketsLostSent / sentCount;
        }

        /// <summary>记录 RPC 调用。</summary>
        public void RecordRPC() => Interlocked.Increment(ref RPCCallCount);

        /// <summary>记录属性同步。</summary>
        public void RecordPropertySync() => Interlocked.Increment(ref PropertySyncCount);

        /// <summary>更新带宽采样 (每秒调用一次)。</summary>
        public void UpdateBandwidthSample()
        {
            double elapsed = (DateTime.UtcNow - _lastSampleTime).TotalSeconds;
            if (elapsed < 0.1) return;

            double sentDelta = BytesSent - _lastBytesSent;
            double recvDelta = BytesReceived - _lastBytesReceived;
            SendBandwidthBps = sentDelta / elapsed;
            ReceiveBandwidthBps = recvDelta / elapsed;

            _lastBytesSent = BytesSent;
            _lastBytesReceived = BytesReceived;
            _lastSampleTime = DateTime.UtcNow;
        }

        /// <summary>获取格式化统计字符串。</summary>
        public string GetFormattedStats()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== Network Stats ===");
            sb.AppendLine($"  Bytes:   Sent={BytesSent >> 10}KB  Recv={BytesReceived >> 10}KB");
            sb.AppendLine($"  Packets: Sent={PacketsSent}  Recv={PacketsReceived}");
            sb.AppendLine($"  Bandwidth: Up={SendBandwidthBps:F0}B/s  Down={ReceiveBandwidthBps:F0}B/s");
            sb.AppendLine($"  RTT:      Current={RTT:F1}ms  Avg={AverageRTT:F1}ms  Jitter={Jitter:F1}ms");
            sb.AppendLine($"  RTT Range: Min={MinRTT:F1}ms  Max={MaxRTT:F1}ms");
            sb.AppendLine($"  Loss:     Rate={PacketLossRate * 100:F1}%  Lost={PacketsLostSent}");
            sb.AppendLine($"  RPC:      {RPCCallCount} calls");
            sb.AppendLine($"  PropSync: {PropertySyncCount} syncs");
            return sb.ToString();
        }

        /// <summary>重置所有统计。</summary>
        public void Reset()
        {
            BytesSent = 0; BytesReceived = 0;
            PacketsSent = 0; PacketsReceived = 0;
            SendBandwidthBps = 0; ReceiveBandwidthBps = 0;
            RTT = 0; AverageRTT = 0; MinRTT = float.MaxValue; MaxRTT = 0;
            Jitter = 0; PacketLossRate = 0;
            PacketsLostSent = 0; PacketsLostReceived = 0;
            RPCCallCount = 0; PropertySyncCount = 0;
            _lastSampleTime = DateTime.UtcNow;
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 2. 网络日志
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 网络日志系统。记录所有 RPC 调用、属性变更、连接事件等。
    /// </summary>
    public sealed class NetworkLogger
    {
        private static readonly Lazy<NetworkLogger> _instance
            = new Lazy<NetworkLogger>(() => new NetworkLogger());

        /// <summary>单例。</summary>
        public static NetworkLogger Instance => _instance.Value;

        private readonly ConcurrentQueue<LogEntry> _logQueue = new ConcurrentQueue<LogEntry>();
        private readonly List<LogEntry> _history = new List<LogEntry>();
        private int _maxHistorySize = 10000;

        /// <summary>日志级别。</summary>

        /// <summary>日志条目。</summary>
        public sealed class LogEntry
        {
            public DateTime Timestamp { get; }
            public LogLevel Level { get; }
            public string Category { get; }
            public string Message { get; }
            public int ConnectionId { get; }

            public LogEntry(LogLevel level, string category, string message, int connId = 0)
            {
                Timestamp = DateTime.UtcNow;
                Level = level;
                Category = category;
                Message = message;
                ConnectionId = connId;
            }

            public override string ToString() =>
                $"[{Timestamp:HH:mm:ss.fff}] [{Level}] [{Category}] {Message}";
        }

        private NetworkLogger() { }

        /// <summary>记录调试日志。</summary>
        public void Debug(string category, string message, int connId = 0)
            => Log(LogLevel.Debug, category, message, connId);

        /// <summary>记录信息日志。</summary>
        public void Info(string category, string message, int connId = 0)
            => Log(LogLevel.Info, category, message, connId);

        /// <summary>记录警告日志。</summary>
        public void Warning(string category, string message, int connId = 0)
            => Log(LogLevel.Warning, category, message, connId);

        /// <summary>记录错误日志。</summary>
        public void Error(string category, string message, int connId = 0)
            => Log(LogLevel.Error, category, message, connId);

        private void Log(LogLevel level, string category, string message, int connId)
        {
            var entry = new LogEntry(level, category, message, connId);
            _logQueue.Enqueue(entry);

            lock (_history)
            {
                _history.Add(entry);
                if (_history.Count > _maxHistorySize)
                    _history.RemoveAt(0);
            }
        }

        /// <summary>记录 RPC 调用。</summary>
        public void LogRPC(string methodName, int connId, string direction)
        {
            Info("RPC", $"{direction}: {methodName} (conn={connId})", connId);
        }

        /// <summary>记录属性同步。</summary>
        public void LogPropertySync(string propName, object? oldValue, object? newValue, int networkId)
        {
            Debug("PropSync",
                $"Obj[{networkId}] {propName}: {oldValue} -> {newValue}");
        }

        /// <summary>记录连接事件。</summary>
        public void LogConnectionEvent(string evt, int connId, string detail = "")
        {
            Info("Connection", $"{evt} conn={connId} {detail}", connId);
        }

        /// <summary>获取最近日志。</summary>
        public List<LogEntry> GetRecentLogs(int count = 100)
        {
            lock (_history)
            {
                int start = Math.Max(0, _history.Count - count);
                return _history.GetRange(start, _history.Count - start);
            }
        }

        /// <summary>按类别过滤日志。</summary>
        public List<LogEntry> GetLogsByCategory(string category)
        {
            lock (_history)
            {
                return _history.Where(e => e.Category == category).ToList();
            }
        }

        /// <summary>按级别过滤日志。</summary>
        public List<LogEntry> GetLogsByLevel(LogLevel level)
        {
            lock (_history)
            {
                return _history.Where(e => e.Level == level).ToList();
            }
        }

        /// <summary>处理待处理的日志队列。</summary>
        public void ProcessPending()
        {
            while (_logQueue.TryDequeue(out _)) { }
        }

        /// <summary>清空日志。</summary>
        public void Clear()
        {
            _history.Clear();
            while (_logQueue.TryDequeue(out _)) { }
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 3. 网络条件模拟器
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 网络条件模拟器。用于开发调试, 注入延迟、丢包、带宽限制和抖动,
    /// 模拟不同网络环境。
    /// </summary>
    public sealed class NetworkConditionSimulator
    {
        /// <summary>模拟配置。</summary>
        public sealed class SimulationConfig
        {
            /// <summary>注入延迟 (毫秒)。</summary>
            public float LatencyMs { get; set; }

            /// <summary>延迟抖动 (毫秒)。</summary>
            public float JitterMs { get; set; }

            /// <summary>丢包率 (0~1)。</summary>
            public float PacketLossRate { get; set; }

            /// <summary>上行带宽限制 (字节/秒), 0=无限制。</summary>
            public float BandwidthLimitBps { get; set; }

            /// <summary>是否启用模拟。</summary>
            public bool IsEnabled { get; set; }
        }

        private SimulationConfig _config = new SimulationConfig();
        private readonly Random _random = new Random();
        private long _bytesSentThisSecond;
        private DateTime _bandwidthWindowStart = DateTime.UtcNow;

        /// <summary>当前模拟配置。</summary>
        public SimulationConfig Config => _config;

        /// <summary>是否启用模拟。</summary>
        public bool IsEnabled => _config.IsEnabled;

        /// <summary>预设条件。</summary>
        public static readonly SimulationConfig Perfect = new SimulationConfig
            { LatencyMs = 0, JitterMs = 0, PacketLossRate = 0f, BandwidthLimitBps = 0 };

        public static readonly SimulationConfig Good3G = new SimulationConfig
            { LatencyMs = 100, JitterMs = 30, PacketLossRate = 0.01f, BandwidthLimitBps = 100000 };

        public static readonly SimulationConfig Poor3G = new SimulationConfig
            { LatencyMs = 300, JitterMs = 100, PacketLossRate = 0.05f, BandwidthLimitBps = 50000 };

        public static readonly SimulationConfig EdgeCase = new SimulationConfig
            { LatencyMs = 500, JitterMs = 200, PacketLossRate = 0.15f, BandwidthLimitBps = 20000 };

        /// <summary>设置模拟配置。</summary>
        public void SetConfig(SimulationConfig config)
        {
            _config = config;
        }

        /// <summary>启用/禁用模拟。</summary>
        public void SetEnabled(bool enabled) => _config.IsEnabled = enabled;

        /// <summary>使用预设配置。</summary>
        public void ApplyPreset(SimulationConfig preset)
        {
            _config.LatencyMs = preset.LatencyMs;
            _config.JitterMs = preset.JitterMs;
            _config.PacketLossRate = preset.PacketLossRate;
            _config.BandwidthLimitBps = preset.BandwidthLimitBps;
            _config.IsEnabled = true;
        }

        /// <summary>
        /// 模拟发送延迟。返回应该延迟的毫秒数。
        /// </summary>
        public float SimulateSendDelay()
        {
            if (!_config.IsEnabled) return 0f;

            // 带宽限制检查
            CheckBandwidthLimit();

            // 丢包检查
            if (ShouldDrop()) return -1f; // -1 表示应该丢弃

            // 计算延迟 (基础延迟 + 抖动)
            float jitter = (float)(_random.NextDouble() * 2 - 1) * _config.JitterMs;
            return Math.Max(0, _config.LatencyMs + jitter);
        }

        /// <summary>检查是否应该丢弃包。</summary>
        public bool ShouldDrop()
        {
            if (!_config.IsEnabled || _config.PacketLossRate <= 0) return false;
            return _random.NextDouble() < _config.PacketLossRate;
        }

        /// <summary>检查带宽限制。</summary>
        private void CheckBandwidthLimit()
        {
            if (_config.BandwidthLimitBps <= 0) return;

            double elapsed = (DateTime.UtcNow - _bandwidthWindowStart).TotalSeconds;
            if (elapsed >= 1.0)
            {
                _bytesSentThisSecond = 0;
                _bandwidthWindowStart = DateTime.UtcNow;
            }
        }

        /// <summary>重置带宽窗口。</summary>
        public void ResetBandwidthWindow()
        {
            _bytesSentThisSecond = 0;
            _bandwidthWindowStart = DateTime.UtcNow;
        }

        /// <summary>获取模拟条件描述。</summary>
        public string GetDescription()
        {
            if (!_config.IsEnabled) return "Network simulation: Disabled";
            return $"Network simulation: Latency={_config.LatencyMs:F0}ms " +
                   $"Jitter={_config.JitterMs:F0}ms " +
                   $"Loss={_config.PacketLossRate * 100:F1}% " +
                   $"BWLimit={_config.BandwidthLimitBps:F0}B/s";
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 4. 网络控制台命令
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 网络控制台命令处理器。支持以下命令:
    ///   netstat    - 显示网络统计
    ///   players    - 列出所有连接
    ///   kick <id>  - 踢出指定连接
    ///   ban <ip>   - 封禁 IP
    ///   ping <id>  - Ping 指定连接
    ///   dump       - 导出网络对象列表
    ///   trace      - 启用/禁用网络追踪
    ///   show       - 显示网络信息
    /// </summary>
    public sealed class NetworkConsole
    {
        private static readonly Lazy<NetworkConsole> _instance
            = new Lazy<NetworkConsole>(() => new NetworkConsole());

        /// <summary>单例。</summary>
        public static NetworkConsole Instance => _instance.Value;

        private bool _isTraceEnabled;
        private NetworkStats _stats;
        private NetworkConditionSimulator _simulator = new NetworkConditionSimulator();

        /// <summary>网络统计。</summary>
        public NetworkStats Stats => _stats;

        /// <summary>网络模拟器。</summary>
        public NetworkConditionSimulator Simulator => _simulator;

        /// <summary>是否启用追踪。</summary>
        public bool IsTraceEnabled => _isTraceEnabled;

        /// <summary>命令执行事件。</summary>
        public event Action<string, string>? OnCommandExecuted;

        private NetworkConsole() { }

        /// <summary>执行控制台命令。</summary>
        public string ExecuteCommand(string command)
        {
            string[] parts = command.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return "Empty command";

            string cmd = parts[0].ToLowerInvariant();
            string result;

            switch (cmd)
            {
                case "netstat":
                    result = ExecuteNetstat();
                    break;

                case "players":
                    result = ExecutePlayers();
                    break;

                case "kick":
                    result = parts.Length >= 2
                        ? ExecuteKick(int.Parse(parts[1]))
                        : "Usage: kick <connectionId>";
                    break;

                case "ban":
                    result = parts.Length >= 2
                        ? ExecuteBan(parts[1])
                        : "Usage: ban <ipAddress>";
                    break;

                case "unban":
                    result = parts.Length >= 2
                        ? ExecuteUnban(parts[1])
                        : "Usage: unban <ipAddress>";
                    break;

                case "ping":
                    result = parts.Length >= 2
                        ? ExecutePing(int.Parse(parts[1]))
                        : ExecutePingAll();
                    break;

                case "dump":
                    result = ExecuteDump();
                    break;

                case "trace":
                    _isTraceEnabled = !_isTraceEnabled;
                    result = $"Network trace: {_isTraceEnabled}";
                    break;

                case "show":
                    result = ExecuteShow(parts);
                    break;

                case "sim":
                    result = ExecuteSimulation(parts);
                    break;

                case "help":
                    result = GetHelp();
                    break;

                default:
                    result = $"Unknown command: {cmd}. Type 'help' for available commands.";
                    break;
            }

            OnCommandExecuted?.Invoke(cmd, result);
            return result;
        }

        private string ExecuteNetstat()
        {
            return _stats.GetFormattedStats();
        }

        private string ExecutePlayers()
        {
            var manager = NetworkManager.Instance;
            var conns = manager.GetConnections();
            if (conns.Count == 0) return "No players connected";

            var sb = new StringBuilder();
            sb.AppendLine($"Players ({conns.Count}):");
            sb.AppendLine("  ID  | Name            | State        | RTT(ms) | Loss   | Addr");
            sb.AppendLine("  -----|-----------------|--------------|---------|--------|---------");
            foreach (var conn in conns)
            {
                sb.AppendLine($"  {conn.ConnectionId,4} | {conn.PlayerName,-15} | {conn.State,-12} | " +
                    $"{conn.SmoothRTT,7:F1} | {conn.PacketLoss * 100,6:F1}% | {conn.RemoteEndPoint}");
            }
            return sb.ToString();
        }

        private string ExecuteKick(int connId)
        {
            var manager = NetworkManager.Instance;
            manager.KickClient(connId, "Kicked by console");
            return $"Kicked connection {connId}";
        }

        private string ExecuteBan(string ip)
        {
            var manager = NetworkManager.Instance;
            manager.BanIP(ip);
            return $"Banned IP: {ip}";
        }

        private string ExecuteUnban(string ip)
        {
            var manager = NetworkManager.Instance;
            manager.UnbanIP(ip);
            return $"Unbanned IP: {ip}";
        }

        private string ExecutePing(int connId)
        {
            var conn = NetworkManager.Instance.GetConnection(connId);
            if (conn == null) return $"Connection {connId} not found";
            return $"Ping conn={connId}: RTT={conn.RTT:F1}ms SmoothRTT={conn.SmoothRTT:F1}ms " +
                   $"Jitter={conn.Jitter:F1}ms Quality={conn.Quality}";
        }

        private string ExecutePingAll()
        {
            var conns = NetworkManager.Instance.GetConnections();
            if (conns.Count == 0) return "No connections";
            var sb = new StringBuilder();
            foreach (var conn in conns)
            {
                sb.AppendLine($"  Conn[{conn.ConnectionId}]: RTT={conn.SmoothRTT:F1}ms " +
                    $"Quality={conn.Quality} Loss={conn.PacketLoss * 100:F1}%");
            }
            return sb.ToString();
        }

        private string ExecuteDump()
        {
            var objMgr = NetworkObjectManager.Instance;
            return objMgr.GetStatistics();
        }

        private string ExecuteShow(string[] parts)
        {
            if (parts.Length < 2) return "Usage: show [connections|objects|stats|sim]";
            string what = parts[1].ToLowerInvariant();
            return what switch
            {
                "connections" => ExecutePlayers(),
                "objects" => ExecuteDump(),
                "stats" => ExecuteNetstat(),
                "sim" => _simulator.GetDescription(),
                _ => $"Unknown show target: {what}"
            };
        }

        private string ExecuteSimulation(string[] parts)
        {
            if (parts.Length < 2) return "Usage: sim [perfect|good3g|poor3g|edge|off|custom <lat> <jitter> <loss>]";
            string preset = parts[1].ToLowerInvariant();
            switch (preset)
            {
                case "perfect": _simulator.ApplyPreset(NetworkConditionSimulator.Perfect); break;
                case "good3g": _simulator.ApplyPreset(NetworkConditionSimulator.Good3G); break;
                case "poor3g": _simulator.ApplyPreset(NetworkConditionSimulator.Poor3G); break;
                case "edge": _simulator.ApplyPreset(NetworkConditionSimulator.EdgeCase); break;
                case "off": _simulator.SetEnabled(false); break;
                case "custom":
                    if (parts.Length >= 5)
                    {
                        _simulator.ApplyPreset(new NetworkConditionSimulator.SimulationConfig
                        {
                            LatencyMs = float.Parse(parts[2]),
                            JitterMs = float.Parse(parts[3]),
                            PacketLossRate = float.Parse(parts[4]),
                            BandwidthLimitBps = 0,
                            IsEnabled = true
                        });
                    }
                    else return "Usage: sim custom <latency> <jitter> <lossRate>";
                    break;
                default: return $"Unknown preset: {preset}";
            }
            return _simulator.GetDescription();
        }

        private string GetHelp()
        {
            return @"Network Console Commands:
  netstat            - Show network statistics
  players            - List all connected players
  kick <id>          - Kick a player by connection ID
  ban <ip>           - Ban an IP address
  unban <ip>         - Unban an IP address
  ping <id>          - Ping a specific connection
  ping               - Ping all connections
  dump               - Dump network object info
  trace              - Toggle network tracing
  show <target>      - Show info (connections|objects|stats|sim)
  sim <preset>       - Set network simulation (perfect|good3g|poor3g|edge|off)
  sim custom <l> <j> <loss> - Custom simulation parameters
  help               - Show this help";
        }

        /// <summary>每帧更新统计。</summary>
        public void Update(float deltaTime)
        {
            _stats.UpdateBandwidthSample();
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 5. 网络可视化
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 网络可视化数据。生成连接状态、数据流图、延迟图和带宽图的描述数据。
    /// </summary>
    public sealed class NetworkVisualizer
    {
        private readonly List<float> _rttHistory = new List<float>();
        private readonly List<float> _sendBandwidthHistory = new List<float>();
        private readonly List<float> _recvBandwidthHistory = new List<float>();
        private readonly List<float> _lossHistory = new List<float>();
        private int _maxHistorySize = 300; // 5分钟 @ 10Hz

        /// <summary>RTT 历史数据。</summary>
        public IReadOnlyList<float> RTTHistory => _rttHistory;

        /// <summary>上行带宽历史。</summary>
        public IReadOnlyList<float> SendBandwidthHistory => _sendBandwidthHistory;

        /// <summary>下行带宽历史。</summary>
        public IReadOnlyList<float> RecvBandwidthHistory => _recvBandwidthHistory;

        /// <summary>丢包率历史。</summary>
        public IReadOnlyList<float> LossHistory => _lossHistory;

        /// <summary>最大历史数据点数。</summary>
        public int MaxHistorySize
        {
            get => _maxHistorySize;
            set => _maxHistorySize = Math.Max(10, value);
        }

        /// <summary>采样当前数据。</summary>
        public void Sample(float rttMs, float sendBw, float recvBw, float lossRate)
        {
            _rttHistory.Add(rttMs);
            _sendBandwidthHistory.Add(sendBw);
            _recvBandwidthHistory.Add(recvBw);
            _lossHistory.Add(lossRate);

            TrimHistory();
        }

        private void TrimHistory()
        {
            while (_rttHistory.Count > _maxHistorySize) _rttHistory.RemoveAt(0);
            while (_sendBandwidthHistory.Count > _maxHistorySize) _sendBandwidthHistory.RemoveAt(0);
            while (_recvBandwidthHistory.Count > _maxHistorySize) _recvBandwidthHistory.RemoveAt(0);
            while (_lossHistory.Count > _maxHistorySize) _lossHistory.RemoveAt(0);
        }

        /// <summary>生成 ASCII 延迟图。</summary>
        public string GenerateRTTChart(int width = 60, int height = 10)
        {
            if (_rttHistory.Count < 2) return "Insufficient data for RTT chart";

            float maxRtt = _rttHistory.Max();
            float minRtt = _rttHistory.Min();
            float range = maxRtt - minRtt;
            if (range < 1f) range = 1f;

            var sb = new StringBuilder();
            sb.AppendLine($"RTT Chart (min={minRtt:F0}ms max={maxRtt:F0}ms):");

            for (int row = height - 1; row >= 0; row--)
            {
                sb.Append("  ");
                float threshold = minRtt + (range * row / (float)(height - 1));

                // 下采样到 width 个点
                int step = Math.Max(1, _rttHistory.Count / width);
                for (int col = 0; col < width; col++)
                {
                    int idx = Math.Min(col * step, _rttHistory.Count - 1);
                    if (_rttHistory[idx] >= threshold)
                        sb.Append('#');
                    else
                        sb.Append('.');
                }
                sb.AppendLine();
            }
            return sb.ToString();
        }

        /// <summary>生成 ASCII 带宽图。</summary>
        public string GenerateBandwidthChart(int width = 60, int height = 10)
        {
            if (_sendBandwidthHistory.Count < 2) return "Insufficient data for bandwidth chart";

            float maxBw = Math.Max(
                _sendBandwidthHistory.Max(),
                _recvBandwidthHistory.Max());
            if (maxBw < 1f) maxBw = 1f;

            var sb = new StringBuilder();
            sb.AppendLine($"Bandwidth Chart (max={maxBw:F0}B/s):");

            for (int row = height - 1; row >= 0; row--)
            {
                sb.Append("  ");
                float threshold = maxBw * row / (float)(height - 1);
                int step = Math.Max(1, _sendBandwidthHistory.Count / width);

                for (int col = 0; col < width; col++)
                {
                    int idx = Math.Min(col * step, _sendBandwidthHistory.Count - 1);
                    float up = _sendBandwidthHistory[idx];
                    float down = idx < _recvBandwidthHistory.Count
                        ? _recvBandwidthHistory[idx] : 0f;

                    char c = (up >= threshold && down >= threshold) ? 'B' :
                             (up >= threshold) ? '^' :
                             (down >= threshold) ? 'v' : '.';
                    sb.Append(c);
                }
                sb.AppendLine();
            }
            sb.AppendLine("  Legend: ^=Upload v=Download B=Both .=None");
            return sb.ToString();
        }

        /// <summary>生成连接状态摘要。</summary>
        public string GenerateConnectionSummary()
        {
            var manager = NetworkManager.Instance;
            var conns = manager.GetConnections();

            var sb = new StringBuilder();
            sb.AppendLine($"Network: {manager.Role} | Connections: {conns.Count}/{manager.Config.MaxConnections}");

            foreach (var conn in conns)
            {
                string qualityBar = conn.Quality switch
                {
                    ConnectionQuality.Excellent => "████████████ 100%",
                    ConnectionQuality.Good => "██████████░░  80%",
                    ConnectionQuality.Fair => "████████░░░░  60%",
                    ConnectionQuality.Poor => "████░░░░░░░░  40%",
                    ConnectionQuality.Terrible => "░░░░░░░░░░░   0%",
                    _ => "░░░░░░░░░░░   0%"
                };

                sb.AppendLine($"  [{conn.ConnectionId}] {conn.PlayerName} " +
                    $"RTT={conn.SmoothRTT:F0}ms [{qualityBar}]");
            }
            return sb.ToString();
        }

        /// <summary>清空历史数据。</summary>
        public void ClearHistory()
        {
            _rttHistory.Clear();
            _sendBandwidthHistory.Clear();
            _recvBandwidthHistory.Clear();
            _lossHistory.Clear();
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 6. NetworkProfiler 集成
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 网络分析器集成。将网络调试数据对接到引擎的诊断系统。
    /// </summary>
    public sealed class NetworkProfilerBridge
    {
        private static readonly Lazy<NetworkProfilerBridge> _instance
            = new Lazy<NetworkProfilerBridge>(() => new NetworkProfilerBridge());

        /// <summary>单例。</summary>
        public static NetworkProfilerBridge Instance => _instance.Value;

        private readonly NetworkStats _stats;
        private readonly NetworkVisualizer _visualizer = new NetworkVisualizer();
        private bool _isProfiling;

        /// <summary>网络统计。</summary>
        public NetworkStats Stats => _stats;

        /// <summary>可视化器。</summary>
        public NetworkVisualizer Visualizer => _visualizer;

        /// <summary>是否正在分析。</summary>
        public bool IsProfiling => _isProfiling;

        private NetworkProfilerBridge() { }

        /// <summary>开始分析。</summary>
        public void StartProfiling()
        {
            _isProfiling = true;
            _stats.Reset();
            _visualizer.ClearHistory();
        }

        /// <summary>停止分析。</summary>
        public void StopProfiling()
        {
            _isProfiling = false;
        }

        /// <summary>每帧采样。</summary>
        public void Sample(float rtt, float sendBw, float recvBw, float lossRate)
        {
            if (!_isProfiling) return;
            _stats.RecordRTT(rtt);
            _visualizer.Sample(rtt, sendBw, recvBw, lossRate);
        }

        /// <summary>生成完整报告。</summary>
        public string GenerateReport()
        {
            var sb = new StringBuilder();
            sb.AppendLine("╔════════════════════════════════════════════════╗");
            sb.AppendLine("║        山雨引擎 - 网络性能分析报告            ║");
            sb.AppendLine("╠════════════════════════════════════════════════╣");
            sb.AppendLine(_stats.GetFormattedStats());
            sb.AppendLine("─────────────────────────────────────────────");
            sb.AppendLine(_visualizer.GenerateRTTChart());
            sb.AppendLine("─────────────────────────────────────────────");
            sb.AppendLine(_visualizer.GenerateBandwidthChart());
            sb.AppendLine("─────────────────────────────────────────────");
            sb.AppendLine(_visualizer.GenerateConnectionSummary());
            sb.AppendLine("─────────────────────────────────────────────");
            sb.AppendLine(NetworkConsole.Instance.Simulator.GetDescription());
            sb.AppendLine("╚════════════════════════════════════════════════╝");
            return sb.ToString();
        }
    }
}
