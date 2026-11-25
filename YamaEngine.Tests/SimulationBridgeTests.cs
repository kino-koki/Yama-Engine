// SimulationBridgeTests.cs — Round 7：跨模块桥接 / 故障注入 / 守恒门禁
// 验证物理事件真的变成 NPC 信念、异常输入被拦下、双世界快照可逐字重放。
using System.Numerics;
using YamaEngine.NPC.Runtime;
using YamaEngine.Physics.Reality;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests;

public static class SimulationBridgeTests
{
    public static void TestSimBridgeHeatRequestsAreValidated()
    {
        var c = new SimulationCoordinator();
        c.Phys.Grid.Emissivity = 0.0;
        c.PushHeat(0, 1000.0, "a");
        c.PushHeat(0, double.NaN, "a");
        c.PushHeat(0, double.PositiveInfinity, "a");
        c.PushHeat(9999, 500.0, "a");
        c.Step(1f / 60f);
        AssertEqual(1L, c.Stats.HeatRequestsApplied, "只有合法热请求应落地。");
        AssertEqual(2L, c.Stats.NonFiniteRejected, "NaN / Inf 应被拒绝。");
        AssertEqual(1L, c.Stats.OutOfRangeRejected, "越界格号应被拒绝。");
        AssertTrue(c.Phys.Grid.EnergyIn > 0.0, "合法热源应计入能量账本。");
    }

    public static void TestSimBridgeBurstBecomesNpcThreatBelief()
    {
        var c = new SimulationCoordinator(1UL, 2UL, 1, 1, 3, 0.5);
        c.Phys.Grid.Emissivity = 0.0;
        for (int i = 0; i < 6; i++)
        {
            var a = c.Npc.Register("n" + i, "NPC" + i);
            a.Position = new Vector3(i * 0.2f, 0f, 0f);
        }
        for (int i = 0; i < c.Phys.Grid.Count; i++) c.Phys.Grid.T[i] = 2000.0;
        c.Phys.Vessels.SetSealed(1, true, burstPa: 1000.0);
        c.Step(1f / 60f);
        AssertTrue(c.Stats.PhysicsEventsSeen > 0, "应观察到物理事件。");
        AssertTrue(c.Stats.PerceptsEmitted > 0, "burst 应转换成 NPC 感知。");
        int threats = c.Npc.Actors.Keys.Sum(id => c.Npc.Beliefs.Query(id, "threat").Count);
        AssertTrue(threats > 0, "NPC 应形成关于爆裂的持续威胁信念，而非每帧重新发现。");
    }

    public static void TestSimBridgeChemPerceptIsValenced()
    {
        var c = new SimulationCoordinator();
        c.Phys.Grid.Emissivity = 0.0;
        c.Npc.Register("a", "A").Position = Vector3.Zero;
        // 直接注入一个点火事件（走同一条桥接路径）
        c.Phys.Events.Add(new RealityEvent(0.0, "ignition", 0, "spread", 600.0));
        c.StepOnce(1f / 60f);
        AssertTrue(c.Stats.PerceptsEmitted > 0, "点火事件应产生化学感知。");
        AssertTrue(c.Stats.PhysicsEventsSeen > 0, "事件计数应递增。");
        AssertTrue(c.ChemPerceptValence < 0f, "化学感知默认应为负效价。");
    }

    public static void TestSimBridgeConservationGatePasses()
    {
        var c = new SimulationCoordinator();
        c.Phys.Grid.Emissivity = 0.0;
        for (int i = 0; i < 30; i++) c.Step(1f / 60f);
        var r = c.Phys.Conservation();
        AssertTrue(c.Gate.Pass(r), "默认场景下守恒门禁应通过：" + r);
        c.Gate.Assert(r); // 不应抛异常
    }

    public static void TestSimBridgeGateRejectsBadReport()
    {
        var gate = new ConservationGate();
        AssertThrows<InvalidOperationException>(
            () => gate.Assert(new ConservationReport(1.0, 0.0, 0.0, 0.0)),
            "能量误差超限时门禁必须抛异常。");
        AssertTrue(gate.Pass(new ConservationReport(0.0, 0.0, 0.0, 0.0)),
            "全零误差应通过。");
    }

    public static void TestSimBridgeSnapshotRoundtripIsExact()
    {
        var c = new SimulationCoordinator(7UL, 8UL, 2, 1, 2, 0.5);
        c.Phys.Grid.Emissivity = 0.0;
        c.Npc.Register("a", "A").Position = new Vector3(0.2f, 0f, 0.2f);
        c.Npc.Register("b", "B").Position = new Vector3(0.6f, 0f, 0.3f);
        c.PushHeat(0, 500.0, "a");
        for (int i = 0; i < 10; i++) c.Step(1f / 60f);
        string snap = c.Snapshot();
        var c2 = new SimulationCoordinator(7UL, 8UL, 2, 1, 2, 0.5);
        c2.Restore(snap);
        AssertEqual(snap, c2.Snapshot(), "双世界 + 桥接统计的快照应逐字往返一致。");
    }

    public static void TestSimBridgeIsDeterministicFromSnapshot()
    {
        var c = new SimulationCoordinator(11UL, 12UL, 2, 1, 2, 0.5);
        c.Phys.Grid.Emissivity = 0.0;
        for (int i = 0; i < 4; i++)
            c.Npc.Register("n" + i, "NPC" + i).Position = new Vector3(i * 0.3f, 0f, 0f);
        for (int i = 0; i < 12; i++) c.Step(1f / 60f);
        AssertTrue(SimulationCoordinator.VerifyDeterminism(c.Snapshot(), 1f / 60f, 20, 2, 1, 2, 0.5),
            "同快照同 dt 序列重放两次必须完全一致。");
    }

    public static void TestSimBridgeCellToWorldIsStable()
    {
        var c = new SimulationCoordinator(1UL, 1UL, 2, 1, 2, 0.5);
        var p0 = c.CellToWorld(0);
        var p3 = c.CellToWorld(3);
        AssertTrue(Vector3.Distance(p0, Vector3.Zero) < 1e-6f, "第 0 格应在原点。");
        AssertTrue(p3.X > p0.X && p3.Z > p0.Z, "格索引增大应映射到更大的世界坐标。");
        AssertTrue(Vector3.Distance(c.CellToWorld(-1), Vector3.Zero) < 1e-6f,
            "非法格号应安全返回原点。");
    }

    public static void TestSimChaosRejectsNonFiniteAndStaysFinite()
    {
        var r = SimulationChaos.Run(20261125UL, 120);
        AssertTrue(r.AllFinite, "混沌注入下温度场必须全程有限且非负：" + r);
        AssertTrue(r.GatePassed, "混沌注入下守恒门禁不得被破坏：" + r);
        AssertTrue(r.NonFiniteRejected > 0, "应确实拦下过 NaN / Inf 热源：" + r);
        AssertEqual(120, r.Steps, "应完成全部步数（未提前崩溃）。");
    }

    public static void TestSimChaosIsDeterministic()
    {
        var a = SimulationChaos.Run(4242UL, 60).ToString();
        var b = SimulationChaos.Run(4242UL, 60).ToString();
        AssertEqual(a, b, "同种子混沌注入结果必须完全一致。");
    }

    public static void TestSimBridgeEventsAreNeverBridgedTwice()
    {
        // 回归 Round 7 的真实缺陷：物理层 Events 超过 2048 会裁掉头部，
        // 旧的"增量游标"回退到 0 会把历史事件重复转成感知。
        var c = new SimulationCoordinator();
        c.Phys.Grid.Emissivity = 0.0;
        c.MaxEventsPerStep = 100;
        c.Npc.Register("a", "A");
        for (int i = 0; i < 2100; i++)
            c.Phys.Events.Add(new RealityEvent(0.0, "ignition", 0, "t", 1.0));
        c.StepOnce(1f / 60f);
        long first = c.Stats.PhysicsEventsSeen;
        AssertEqual(100L, first, "单步消费量应严格等于限流上限。");
        c.StepOnce(1f / 60f);
        long second = c.Stats.PhysicsEventsSeen;
        AssertTrue(second - first <= 100L,
            "第二步只应消费接下来的 100 条，不得回退重放历史事件。");
        AssertTrue(second > first, "积压事件应在后续步继续被消费。");
        AssertTrue(double.IsFinite(c.Phys.Grid.T[0]), "大批量事件不得破坏热场。");
    }

    public static void TestSimBridgeEventBacklogIsBoundedAndReported()
    {
        var c = new SimulationCoordinator();
        c.Phys.Grid.Emissivity = 0.0;
        c.MaxEventsPerStep = 64;
        for (int i = 0; i < 500; i++)
            c.Phys.Events.Add(new RealityEvent(0.0, "ignition", 0, "t", 1.0));
        c.StepOnce(1f / 60f);
        AssertTrue(c.Stats.EventsDeferred > 0, "超出上限的事件应被推迟并计数。");
        AssertTrue(c.Stats.PeakEventBacklog > 0, "应记录峰值积压。");
        AssertTrue(c.Phys.Events.Count < 500, "已消费事件应出队，队列不得无限增长。");
        AssertTrue(c.Stats.PhysicsEventsSeen <= 64L, "单步桥接量不得超过上限。");
    }
}
