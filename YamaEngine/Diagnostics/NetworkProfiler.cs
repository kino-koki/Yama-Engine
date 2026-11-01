// ============================================================================================
//  NetworkProfiler.cs — 网络分析封装
//  提供网络带宽、延迟、丢包率的实时监控接口。
//  数据由 Networking 子系统填充，Profiler 层负责展示。
// ============================================================================================

using System;

namespace YamaEngine.Diagnostics
{
    /// <summary>网络流量统计。</summary>
    public sealed class NetworkTrafficStats
    {
        public long BytesSent { get; set; }
        public long BytesReceived { get; set; }
    }

    /// <summary>网络延迟统计。</summary>
    public sealed class NetworkLatencyStats
    {
        public double CurrentRTTMs { get; set; }
        public double PacketLossRate { get; set; }
    }

    /// <summary>网络质量等级。</summary>
    public enum NetworkQuality
    {
        Poor = 0,
        Fair = 1,
        Good = 2,
        Excellent = 3
    }

    /// <summary>网络分析器。</summary>
    public class NetworkProfiler
    {
        private long _bytesSentPerSecond;
        private long _bytesReceivedPerSecond;
        private double _averageLatencyMs;
        private double _packetLossRate;

        /// <summary>每秒发送字节数。</summary>
        public long BytesSentPerSecond { get => _bytesSentPerSecond; set => _bytesSentPerSecond = value; }

        /// <summary>每秒接收字节数。</summary>
        public long BytesReceivedPerSecond { get => _bytesReceivedPerSecond; set => _bytesReceivedPerSecond = value; }

        /// <summary>平均延迟 (ms)。</summary>
        public float AverageLatencyMs { get => (float)_averageLatencyMs; set => _averageLatencyMs = value; }

        /// <summary>丢包率 (0~1)。</summary>
        public float PacketLossRate { get => (float)_packetLossRate; set => _packetLossRate = value; }

        /// <summary>活跃连接数。</summary>
        public int ConnectionCount { get; set; }

        /// <summary>获取网络流量统计。</summary>
        public NetworkTrafficStats GetTrafficStats()
        {
            return new NetworkTrafficStats
            {
                BytesSent = _bytesSentPerSecond,
                BytesReceived = _bytesReceivedPerSecond
            };
        }

        /// <summary>获取网络延迟统计。</summary>
        public NetworkLatencyStats GetLatencyStats()
        {
            return new NetworkLatencyStats
            {
                CurrentRTTMs = _averageLatencyMs,
                PacketLossRate = _packetLossRate
            };
        }

        /// <summary>评估网络质量。</summary>
        public NetworkQuality EvaluateQuality()
        {
            if (_packetLossRate > 0.1 || _averageLatencyMs > 300) return NetworkQuality.Poor;
            if (_packetLossRate > 0.03 || _averageLatencyMs > 150) return NetworkQuality.Fair;
            if (_packetLossRate > 0.01 || _averageLatencyMs > 60) return NetworkQuality.Good;
            return NetworkQuality.Excellent;
        }

        /// <summary>获取网络摘要文本。</summary>
        public string GetSummary()
            => $"Sent {FormatBytes(_bytesSentPerSecond)}/s, Recv {FormatBytes(_bytesReceivedPerSecond)}/s, RTT {_averageLatencyMs:F1}ms";

        /// <summary>更新带宽统计。</summary>
        public void UpdateBandwidth() { }

        /// <summary>重置统计。</summary>
        public void Reset()
        {
            _bytesSentPerSecond = 0;
            _bytesReceivedPerSecond = 0;
            _averageLatencyMs = 0;
            _packetLossRate = 0;
            ConnectionCount = 0;
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            return $"{bytes / (1024.0 * 1024):F1} MB";
        }
    }
}