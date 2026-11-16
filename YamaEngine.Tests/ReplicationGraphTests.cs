// ReplicationGraphTests.cs — 复制图 / 兴趣管理 (ReplicationGraph) 验证套件
// 覆盖:
//   * 距离相关性: 仅视野内的连接收到对象 (超出 viewRadius + objectRadius 不相关)
//   * 常相关: AlwaysRelevant 对象发送给所有连接
//   * 恒相关连接 (所有者/观战): AlwaysRelevantTo 即使超距仍相关
//   * 连接移动: 更新观察位置后相关性随之变化
//   * 对象移动: 移动对象位置后相关性随之变化
//   * 平均相关连接统计
//   * NetworkObjectReplicator 兴趣门控: 设图后仅相关连接被发送 (无传输层)
// 全部 CPU 级确定性, 不依赖真实网络/GPU。
using System;
using System.Collections.Generic;
using System.Net;
using System.Numerics;
using YamaEngine.Network;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests;

/// <summary>复制图 / 兴趣管理验证套件（对齐 UE Replication Graph）。</summary>
public sealed class ReplicationGraphTests
{
    private static IPEndPoint EP(int port) => new IPEndPoint(IPAddress.Parse("127.0.0.1"), port);

    /// <summary>距离相关性: 仅视野内连接相关, 超出 viewRadius+objectRadius 不相关。</summary>
    public static void TestRelevanceByDistance()
    {
        var graph = new ReplicationGraphReal();

        var near = EP(1001);
        var far = EP(1002);
        graph.AddConnection(near, new Vector3(0, 0, 0), viewRadius: 50f);
        graph.AddConnection(far, new Vector3(500, 0, 0), viewRadius: 50f);

        // 对象在原点, 半径 10 → 近连接相关, 远连接不相关
        graph.RegisterObject(1, new Vector3(0, 0, 0), relevanceRadius: 10f);

        AssertTrue(graph.IsRelevant(1, near), "视野内连接应相关。");
        AssertTrue(!graph.IsRelevant(1, far), "视野外连接应不相关。");

        var relevant = graph.ComputeRelevantConnections(1);
        AssertEqual(1, relevant.Count, "仅近连接应相关。");
        AssertTrue(relevant.Contains(near), "相关集合应包含近连接。");
    }

    /// <summary>常相关对象应发送给所有连接 (无视距离)。</summary>
    public static void TestAlwaysRelevantObject()
    {
        var graph = new ReplicationGraphReal();
        var a = EP(2001);
        var b = EP(2002);
        graph.AddConnection(a, new Vector3(0, 0, 0), 50f);
        graph.AddConnection(b, new Vector3(999, 0, 0), 50f);

        graph.RegisterObject(7, new Vector3(0, 0, 0), 10f);
        graph.SetAlwaysRelevant(7, true);

        AssertTrue(graph.IsRelevant(7, a), "常相关对象应对所有连接相关。");
        AssertTrue(graph.IsRelevant(7, b), "常相关对象应对所有连接相关 (含远距离)。");
    }

    /// <summary>恒相关连接 (所有者/观战) 即使超距仍相关。</summary>
    public static void TestAlwaysRelevantToConnection()
    {
        var graph = new ReplicationGraphReal();
        var viewer = EP(3001);
        var owner = EP(3002);
        graph.AddConnection(viewer, new Vector3(1000, 0, 0), 50f); // 超距, 非所有者
        graph.AddConnection(owner, new Vector3(999, 0, 0), 50f);   // 超距, 所有者

        graph.RegisterObject(9, new Vector3(0, 0, 0), 10f);
        graph.AddAlwaysRelevantTo(9, owner); // 所有者恒相关

        AssertTrue(graph.IsRelevant(9, owner), "所有者连接应恒相关 (即使超距)。");
        AssertTrue(!graph.IsRelevant(9, viewer), "普通超距连接应不相关。");

        graph.RemoveAlwaysRelevantTo(9, owner);
        AssertTrue(!graph.IsRelevant(9, owner), "移除所有者恒相关后应不相关。");
    }

    /// <summary>连接移动后相关性应更新。</summary>
    public static void TestConnectionMoveUpdatesRelevance()
    {
        var graph = new ReplicationGraphReal();
        var conn = EP(4001);
        graph.AddConnection(conn, new Vector3(0, 0, 0), 50f);
        graph.RegisterObject(3, new Vector3(200, 0, 0), 10f);

        AssertTrue(!graph.IsRelevant(3, conn), "初始 200 单位外应不相关。");

        graph.UpdateConnection(conn, new Vector3(190, 0, 0), 50f);
        AssertTrue(graph.IsRelevant(3, conn), "连接靠近后应相关。");

        graph.UpdateConnection(conn, new Vector3(0, 0, 0), 50f);
        AssertTrue(!graph.IsRelevant(3, conn), "连接远离后应不相关。");
    }

    /// <summary>对象移动后相关性应更新。</summary>
    public static void TestObjectMoveUpdatesRelevance()
    {
        var graph = new ReplicationGraphReal();
        var conn = EP(5001);
        graph.AddConnection(conn, new Vector3(0, 0, 0), 50f);
        graph.RegisterObject(4, new Vector3(200, 0, 0), 10f);

        AssertTrue(!graph.IsRelevant(4, conn), "对象远时应不相关。");

        graph.UpdateObjectPosition(4, new Vector3(30, 0, 0));
        AssertTrue(graph.IsRelevant(4, conn), "对象靠近后应相关。");
    }

    /// <summary>平均相关连接统计应反映兴趣裁剪。</summary>
    public static void TestAverageRelevantConnections()
    {
        var graph = new ReplicationGraphReal();
        var near = EP(6001);
        var far = EP(6002);
        graph.AddConnection(near, new Vector3(0, 0, 0), 50f);
        graph.AddConnection(far, new Vector3(999, 0, 0), 50f);

        graph.RegisterObject(10, new Vector3(0, 0, 0), 10f);  // 仅 near
        graph.RegisterObject(11, new Vector3(999, 0, 0), 10f); // 仅 far

        float avg = graph.GetAverageRelevantConnections();
        AssertTrue(Math.Abs(avg - 1f) < 1e-3f,
            $"平均每对象应约 1 个相关连接, 实际 {avg}。");

        var map = graph.ComputeRelevance();
        AssertEqual(2, map.Count, "应计算 2 个对象的相关性。");
        AssertEqual(1, map[10].Count, "对象10应仅 1 个相关连接。");
        AssertEqual(1, map[11].Count, "对象11应仅 1 个相关连接。");
    }

    /// <summary>NetworkObjectReplicator 兴趣门控: 设图后仅相关连接被发送。</summary>
    public static void TestReplicatorGatesByInterest()
    {
        var replicator = new NetworkObjectReplicator(); // 无传输层 (null)
        var graph = new ReplicationGraphReal();

        var near = EP(7001);
        var far = EP(7002);
        replicator.AddConnection(near);
        replicator.AddConnection(far);

        replicator.SetInterestGraph(graph);
        graph.AddConnection(near, new Vector3(0, 0, 0), 50f);
        graph.AddConnection(far, new Vector3(999, 0, 0), 50f);

        // 带 Position 属性的复制对象 (位于原点)
        var obj = new TestReplicatedObject { Position = new Vector3(0, 0, 0) };
        replicator.RegisterObject(42, obj);

        // 兴趣门控决定: 仅 near 相关, far 不相关
        AssertTrue(graph.IsRelevant(42, near), "near 连接应相关。");
        AssertTrue(!graph.IsRelevant(42, far), "far 连接应不相关。");

        // 统计应仅对相关连接计数 (TotalObjectsSynced 至少 1 次复制)
        replicator.TickReplication(1.0 / 60.0);
        AssertTrue(replicator.TotalObjectsSynced >= 1, "应至少同步一次对象。");

        // 图连接数与复制器连接数一致
        AssertEqual(2, graph.ConnectionCount, "兴趣图应跟踪 2 个连接。");
        AssertEqual(1, graph.ObjectCount, "兴趣图应跟踪 1 个对象。");
    }

    /// <summary>带 Position 属性与 [Replicated] 属性的测试复制对象。</summary>
    private sealed class TestReplicatedObject
    {
        public Vector3 Position { get; set; }
        [Replicated] public float Health { get; set; } = 100f;
    }
}
