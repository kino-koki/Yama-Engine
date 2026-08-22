// ============================================================================================
//  NetworkProfiler.cs — 网络分析封装
//  提供网络带宽、延迟、丢包率的实时监控接口。
//  数据由 Networking 子系统填充，Profiler 层负责展示。
// ============================================================================================

using System;

namespace ShanyuEngine.Diagnostics
{
    public class NetworkProfiler
    {
        /// <summary>每秒发送字节数。</summary>
        public static long BytesSentPerSecond { get; set; }

        /// <summary>每秒接收字节数。</summary>
        public static long BytesReceivedPerSecond { get; set; }

        /// <summary>平均延迟 (ms)。</summary>
        public static float AverageLatencyMs { get; set; }

        /// <summary>丢包率 (0~1)。</summary>
        public static float PacketLossRate { get; set; }

        /// <summary>活跃连接数。</summary>
        public static int ActiveConnections { get; set; }

        public static void Reset()
        {
            BytesSentPerSecond = 0;
            BytesReceivedPerSecond = 0;
            AverageLatencyMs = 0;
            PacketLossRate = 0;
            ActiveConnections = 0;
        }
    }
}
