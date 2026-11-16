// NetworkTransportIntegrationTests.cs — 网络真实传输层集成验证套件
// 覆盖:
//   * UDPTransport（System.Net.Sockets.UdpClient 真实 socket）本地回环端到端：
//     客户端 BuildPayload → 真实 Send → 服务器 Receive() → ReceiveAndExecute → 实例目标真实调用
//   * 回环传输后服务器 RpcDispatcher 调用统计（TotalCallsReceived / GetCallStats）同步
//   * 多参数（int/float/string）经真实 UDP 回环完整投递
//   * 静态 RPC 方法经真实 UDP 回环执行（无需目标实例）
//   * 服务器/客户端 Stop 后 IsRunning=false（资源释放）
// 使用真实 socket（非 mock）：证明 RpcDispatcher 与 ITransportLayer 的物理链路闭合。
// 仅用本地回环（IPAddress.Loopback），不依赖外部网络/GPU，确定性可重复。
using System;
using System.Net;
using System.Threading;
using YamaEngine.Network;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests;

/// <summary>网络真实传输层（socket）集成验证套件。</summary>
public sealed class NetworkTransportIntegrationTests
{
    /// <summary>构造 RpcDispatcher + 目标实例（复用 RpcEndToEndTests 的 RpcEchoTarget）。</summary>
    private static RpcDispatcher MakeDispatcher(out RpcRegistry registry, out RpcEndToEndTests.RpcEchoTarget target)
    {
        var reg = new RpcRegistry();
        reg.ScanAssembly(typeof(RpcEndToEndTests.RpcEchoTarget).Assembly);
        var tgt = new RpcEndToEndTests.RpcEchoTarget();
        var dispatcher = new RpcDispatcher(reg);
        dispatcher.TargetResolver = methodId =>
        {
            foreach (var m in reg.GetAllMethods())
            {
                if (m.MethodId == methodId && !m.MethodInfo.IsStatic)
                    return tgt;
            }
            return null;
        };
        registry = reg;
        target = tgt;
        return dispatcher;
    }

    private static RpcMethodInfo Find(RpcRegistry registry, string fullName)
    {
        var info = registry.FindByFullName(fullName);
        AssertTrue(info != null, $"RPC 方法 '{fullName}' 应已注册。");
        return info!;
    }

    /// <summary>探测一个当前空闲的本地 UDP 端口。</summary>
    private static int FindFreeUdpPort()
    {
        using var probe = new System.Net.Sockets.UdpClient(0);
        var ep = (IPEndPoint)probe.Client.LocalEndPoint!;
        return ep.Port;
    }

    /// <summary>轮询服务器传输层直到收到包（最多 ~2 秒）。返回第一个包；超时返回 null。</summary>
    private static TransportReceivedPacket? WaitForPacket(ITransportLayer transport)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            var packets = transport.Receive();
            if (packets.Count > 0)
                return packets[0];
            Thread.Sleep(10);
        }
        return null;
    }

    /// <summary>真实 UDP 回环：客户端 BuildPayload → Send → 服务器 Receive → ReceiveAndExecute 实例方法真实调用。</summary>
    public static void TestUdpLoopbackDeliversRpcToInstance()
    {
        var serverDispatcher = MakeDispatcher(out var registry, out var target);
        var method = Find(registry, "RpcEchoTarget.ApplyDamage");

        var serverPort = FindFreeUdpPort();
        var server = new UDPTransport();
        try
        {
            AssertTrue(server.Start(serverPort), $"服务器 UDP 应绑定端口 {serverPort}。");
            AssertTrue(server.IsRunning, "服务器应处于运行状态。");

            var client = new UDPTransport();
            try
            {
                AssertTrue(client.Start(0), "客户端 UDP 应启动（系统分配端口）。");
                var serverEP = new IPEndPoint(IPAddress.Loopback, serverPort);
                // UDP 无连接：无需 Connect，直接 Send 到目标端点（Connect 后再用带端点的 Send 会因已连接 socket 失败）
                var payload = serverDispatcher.BuildPayload(method, new object?[] { 99 });
                AssertTrue(client.Send(serverEP, payload, payload.Length), "客户端应经真实 socket 发送成功。");

                var packet = WaitForPacket(server);
                AssertTrue(packet != null, "服务器应经真实 UDP socket 收到包。");

                // 真实传输送达 → RPC 分发执行实例目标
                serverDispatcher.ReceiveAndExecute(
                    new NetworkConnection(packet.Value.From), packet.Value.Data, packet.Value.Length);

                AssertEqual(99, target.DamageApplied, "真实 UDP 回环后实例方法应执行。");
                AssertTrue(serverDispatcher.TotalCallsReceived == 1, "服务器接收计数应为 1。");
                AssertTrue(serverDispatcher.TotalBytesReceived > 0, "服务器应记录接收字节。");

                var stats = serverDispatcher.GetCallStats("RpcEchoTarget.ApplyDamage");
                AssertTrue(stats != null && stats.CallCount == 1, "经真实传输的调用统计应为 1。");
            }
            finally
            {
                client.Stop();
                AssertTrue(!client.IsRunning, "客户端 Stop 后应停止运行。");
            }
        }
        finally
        {
            server.Stop();
            AssertTrue(!server.IsRunning, "服务器 Stop 后应停止运行。");
        }
    }

    /// <summary>真实 UDP 回环：多参数（int/float/string）完整投递。</summary>
    public static void TestUdpLoopbackDeliversMultiParameter()
    {
        var serverDispatcher = MakeDispatcher(out var registry, out var target);
        var method = Find(registry, "RpcEchoTarget.SetMessage");

        var serverPort = FindFreeUdpPort();
        var server = new UDPTransport();
        try
        {
            AssertTrue(server.Start(serverPort), "服务器 UDP 应绑定端口。");
            var client = new UDPTransport();
            try
            {
                AssertTrue(client.Start(0), "客户端 UDP 应启动。");
                var serverEP = new IPEndPoint(IPAddress.Loopback, serverPort);

                var payload = serverDispatcher.BuildPayload(method, new object?[] { "alpha", 4.25f });
                AssertTrue(client.Send(serverEP, payload, payload.Length), "客户端应发送。");

                var packet = WaitForPacket(server);
                AssertTrue(packet != null, "服务器应收到包。");
                serverDispatcher.ReceiveAndExecute(
                    new NetworkConnection(packet.Value.From), packet.Value.Data, packet.Value.Length);

                AssertEqual("alpha", target.LastMessage, "真实回环应投递 string 参数。");
                AssertEqual(4.25f, target.LastValue, "真实回环应投递 float 参数。");
            }
            finally { client.Stop(); }
        }
        finally { server.Stop(); }
    }

    /// <summary>真实 UDP 回环：静态 RPC 方法执行（无需目标实例）。</summary>
    public static void TestUdpLoopbackDeliversStaticRpc()
    {
        var registry = new RpcRegistry();
        registry.ScanAssembly(typeof(RpcEndToEndTests.RpcEchoTarget).Assembly);
        var serverDispatcher = new RpcDispatcher(registry); // 不设 TargetResolver
        var method = Find(registry, "RpcEchoTarget.StaticPing");
        RpcEndToEndTests.RpcEchoTarget.StaticCalls = 0;

        var serverPort = FindFreeUdpPort();
        var server = new UDPTransport();
        try
        {
            AssertTrue(server.Start(serverPort), "服务器 UDP 应绑定端口。");
            var client = new UDPTransport();
            try
            {
                AssertTrue(client.Start(0), "客户端 UDP 应启动。");
                var serverEP = new IPEndPoint(IPAddress.Loopback, serverPort);

                var payload = serverDispatcher.BuildPayload(method, new object?[] { 1234 });
                AssertTrue(client.Send(serverEP, payload, payload.Length), "客户端应发送。");

                var packet = WaitForPacket(server);
                AssertTrue(packet != null, "服务器应收到包。");
                serverDispatcher.ReceiveAndExecute(
                    new NetworkConnection(packet.Value.From), packet.Value.Data, packet.Value.Length);

                AssertEqual(1234, RpcEndToEndTests.RpcEchoTarget.StaticCalls, "真实回环后静态 RPC 应执行。");
                AssertTrue(serverDispatcher.TotalErrors == 0, "静态 RPC 经真实传输不应产生错误。");
            }
            finally { client.Stop(); }
        }
        finally { server.Stop(); }
    }
}
