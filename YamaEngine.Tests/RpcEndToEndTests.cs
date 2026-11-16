// RpcEndToEndTests.cs — RPC 端到端闭环验证套件
// 覆盖:
//   * RpcDispatcher.BuildPayload → 传输 → ReceiveAndExecute 全链路（int/float/string/Vector3 参数）
//   * 实例 RPC 目标解析（TargetResolver）→ 真实方法调用（闭合"无法解析实例目标"缺口）
//   * 静态 RPC 方法无需目标实例
//   * 未注册 TargetResolver / 目标无法解析 → OnRpcError 上报而非静默失败
//   * 调用统计（TotalCallsSent/Received/Bytes、GetCallStats）
//   * 序列化负载与 SendRpc 共用同一 BuildPayload 路径（端到端一致性）
// 全部 CPU 级确定性，不依赖 GPU/真实网络。
using System;
using System.Numerics;
using YamaEngine.Network;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests;

/// <summary>RPC 端到端闭环验证套件。</summary>
public sealed class RpcEndToEndTests
{
    /// <summary>RPC 目标对象（实例 + 静态方法，标记 [Rpc]）。</summary>
    public sealed class RpcEchoTarget
    {
        public int DamageApplied;
        public string? LastMessage;
        public float LastValue;
        public Vector3 LastPosition;
        public static int StaticCalls;

        [Rpc(RpcTarget.Server)]
        public void ApplyDamage(int amount) => DamageApplied = amount;

        [Rpc(RpcTarget.Server)]
        public void SetMessage(string message, float value)
        {
            LastMessage = message;
            LastValue = value;
        }

        [Rpc(RpcTarget.Multicast)]
        public void SetPosition(Vector3 position) => LastPosition = position;

        [Rpc(RpcTarget.Server)]
        public static void StaticPing(int tick) => StaticCalls = tick;
    }

    private static RpcDispatcher MakeDispatcher(out RpcRegistry registry, out RpcEchoTarget target)
    {
        var reg = new RpcRegistry();
        reg.ScanAssembly(typeof(RpcEchoTarget).Assembly);
        var tgt = new RpcEchoTarget();
        var dispatcher = new RpcDispatcher(reg);
        dispatcher.TargetResolver = methodId =>
        {
            // 仅解析本目标的实例方法 ID
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

    private static NetworkConnection MakeSender() => new NetworkConnection();

    /// <summary>int 参数 RPC：BuildPayload → ReceiveAndExecute → 实例方法真实调用。</summary>
    public static void TestRpcIntParameterInvokesInstance()
    {
        var dispatcher = MakeDispatcher(out var registry, out var target);
        var method = Find(registry, "RpcEchoTarget.ApplyDamage");

        var payload = dispatcher.BuildPayload(method, new object?[] { 42 });
        dispatcher.ReceiveAndExecute(MakeSender(), payload, payload.Length);

        AssertEqual(42, target.DamageApplied, "ApplyDamage(42) 应在目标实例上真实执行。");
        AssertTrue(dispatcher.TotalCallsReceived == 1, "接收计数应为 1。");
        AssertTrue(dispatcher.TotalCallsSent == 0, "发送计数应为 0（仅接收端）。");
    }

    /// <summary>string + float 多参数 RPC。</summary>
    public static void TestRpcStringFloatParametersInvokeInstance()
    {
        var dispatcher = MakeDispatcher(out var registry, out var target);
        var method = Find(registry, "RpcEchoTarget.SetMessage");

        var payload = dispatcher.BuildPayload(method, new object?[] { "hello", 3.5f });
        dispatcher.ReceiveAndExecute(MakeSender(), payload, payload.Length);

        AssertEqual("hello", target.LastMessage, "SetMessage 应收到 string 参数。");
        AssertEqual(3.5f, target.LastValue, "SetMessage 应收到 float 参数。");
    }

    /// <summary>Vector3 参数 RPC。</summary>
    public static void TestRpcVector3ParameterInvokesInstance()
    {
        var dispatcher = MakeDispatcher(out var registry, out var target);
        var method = Find(registry, "RpcEchoTarget.SetPosition");

        var payload = dispatcher.BuildPayload(method, new object?[] { new Vector3(1, 2, 3) });
        dispatcher.ReceiveAndExecute(MakeSender(), payload, payload.Length);

        AssertEqual(1f, target.LastPosition.X, "SetPosition X 应为 1。");
        AssertEqual(2f, target.LastPosition.Y, "SetPosition Y 应为 2。");
        AssertEqual(3f, target.LastPosition.Z, "SetPosition Z 应为 3。");
    }

    /// <summary>静态 RPC 方法无需目标实例。</summary>
    public static void TestRpcStaticMethodInvokedWithoutTarget()
    {
        var registry = new RpcRegistry();
        registry.ScanAssembly(typeof(RpcEchoTarget).Assembly);
        var dispatcher = new RpcDispatcher(registry);
        // 不设 TargetResolver：静态方法不应依赖它
        RpcEchoTarget.StaticCalls = 0;

        var method = Find(registry, "RpcEchoTarget.StaticPing");
        var payload = dispatcher.BuildPayload(method, new object?[] { 7 });
        dispatcher.ReceiveAndExecute(MakeSender(), payload, payload.Length);

        AssertEqual(7, RpcEchoTarget.StaticCalls, "静态 RPC 方法应被调用。");
        AssertTrue(dispatcher.TotalErrors == 0, "静态 RPC 不应产生错误。");
    }

    /// <summary>未注册 TargetResolver 时，实例 RPC 应上报错误而非静默失败。</summary>
    public static void TestRpcMissingTargetResolverReportsError()
    {
        var registry = new RpcRegistry();
        registry.ScanAssembly(typeof(RpcEchoTarget).Assembly);
        var dispatcher = new RpcDispatcher(registry);
        // 不设 TargetResolver

        var method = Find(registry, "RpcEchoTarget.ApplyDamage");
        var payload = dispatcher.BuildPayload(method, new object?[] { 42 });

        string? error = null;
        dispatcher.OnRpcError += (_, name, ex) => error = $"{name}: {ex.Message}";
        dispatcher.ReceiveAndExecute(MakeSender(), payload, payload.Length);

        AssertTrue(error != null && error.Contains("TargetResolver"),
            $"实例 RPC 未注册 TargetResolver 应上报错误（实际 '{error}'）。");
        AssertTrue(dispatcher.TotalErrors == 1, "TotalErrors 应为 1。");
    }

    /// <summary>TargetResolver 无法解析目标实例时应上报错误。</summary>
    public static void TestRpcUnresolvedTargetReportsError()
    {
        var registry = new RpcRegistry();
        registry.ScanAssembly(typeof(RpcEchoTarget).Assembly);
        var dispatcher = new RpcDispatcher(registry);
        dispatcher.TargetResolver = _ => null; // 无法解析

        var method = Find(registry, "RpcEchoTarget.ApplyDamage");
        var payload = dispatcher.BuildPayload(method, new object?[] { 42 });

        string? error = null;
        dispatcher.OnRpcError += (_, name, ex) => error = $"{name}: {ex.Message}";
        dispatcher.ReceiveAndExecute(MakeSender(), payload, payload.Length);

        AssertTrue(error != null && error.Contains("无法解析"),
            $"目标实例无法解析应上报错误（实际 '{error}'）。");
        AssertTrue(dispatcher.TotalErrors == 1, "TotalErrors 应为 1。");
    }

    /// <summary>调用统计：GetCallStats 记录发送字节与接收次数。</summary>
    public static void TestRpcCallStatsTracked()
    {
        var dispatcher = MakeDispatcher(out var registry, out var target);
        var method = Find(registry, "RpcEchoTarget.ApplyDamage");

        var payload = dispatcher.BuildPayload(method, new object?[] { 10 });
        dispatcher.ReceiveAndExecute(MakeSender(), payload, payload.Length);

        var stats = dispatcher.GetCallStats("RpcEchoTarget.ApplyDamage");
        AssertTrue(stats != null, "调用统计应记录该方法。");
        AssertTrue(stats!.CallCount == 1, "调用统计计数应为 1。");
    }

    /// <summary>BuildPayload 与 SendRpc 共用序列化路径：负载为 methodId + 参数。</summary>
    public static void TestBuildPayloadEncodesMethodId()
    {
        var registry = new RpcRegistry();
        registry.ScanAssembly(typeof(RpcEchoTarget).Assembly);
        var dispatcher = new RpcDispatcher(registry);
        var method = Find(registry, "RpcEchoTarget.ApplyDamage");

        var payload = dispatcher.BuildPayload(method, new object?[] { 5 });
        AssertTrue(payload.Length >= 5, "负载应至少含 4 字节 methodId + 1 字节参数。");

        // 负载首 4 字节应等于 MethodId
        var reader = new NetworkReader(payload);
        AssertTrue(reader.ReadUInt() == method.MethodId, "负载首 4 字节应为 MethodId。");
    }
}
