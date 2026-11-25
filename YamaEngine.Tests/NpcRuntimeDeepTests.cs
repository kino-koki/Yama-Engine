// NpcRuntimeDeepTests.cs — Round 5 / Round 6 深度回归（纯 CPU、离线、确定性）
// XunitAdapter 自动发现所有 Test*()；也可由 NpcRuntimeRegression.RunAll() 聚合调用。
using System.Numerics;
using YamaEngine.NPC.Runtime;
using YamaEngine.Physics.Reality;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests;

public static class NpcRuntimeDeepTests
{
    // ── Round 5：信念融合 ────────────────────────────────────────────

    public static void TestRtBeliefFusionAccumulatesEvidence()
    {
        var s = new NpcRtBeliefStore();
        var p1 = new NpcRtPercept("threat", "狼", new Vector3(1, 0, 0), 0.8f, -0.9f, 0.0);
        var p2 = new NpcRtPercept("threat", "狼", new Vector3(3, 0, 0), 0.9f, -0.8f, 1.0);
        s.Observe("a", p1, 0.0);
        var b = s.Observe("a", p2, 1.0);
        AssertEqual(2, b.EvidenceCount, "同主体多次感知应累积证据数。");
        AssertTrue(b.Confidence >= 0.8f, "置信度不应低于单次证据。");
        AssertTrue(b.LastKnownPosition.X > 1f, "最后已知位置应向新证据移动。");
        AssertTrue(b.Valence < 0f, "威胁信念应保持负效价。");
    }

    public static void TestRtBeliefContradictionLowersConfidence()
    {
        var s = new NpcRtBeliefStore();
        var agree = new NpcRtBeliefStore();
        var p1 = new NpcRtPercept("social", "旅人", Vector3.Zero, 0.8f, 0.9f, 0.0);
        var p2 = new NpcRtPercept("social", "旅人", Vector3.Zero, 0.8f, -0.9f, 0.0);
        var b1 = s.Observe("a", p1, 0.0); var b2 = s.Observe("a", p2, 0.1);
        var a1 = agree.Observe("a", p1, 0.0); var a2 = agree.Observe("a", p1, 0.1);
        AssertTrue(b2.Confidence < a2.Confidence, "矛盾效价应削弱置信度。");
        AssertTrue(b1.EvidenceCount == 2 && a1.EvidenceCount == 2, "两组都应累积两次证据。");
    }

    public static void TestRtBeliefStaleDecaysAndEvicts()
    {
        var s = new NpcRtBeliefStore { DefaultTtlSeconds = 0.5, DecayPerSecond = 20f, MinConfidence = 0.05f };
        s.Observe("a", new NpcRtPercept("threat", "狼", Vector3.Zero, 0.6f, -1f, 0.0), 0.0);
        AssertEqual(1, s.Query("a").Count, "初始应有一条信念。");
        for (int i = 0; i < 20; i++) s.Tick(0.1, 1.0 + i * 0.1);
        AssertEqual(0, s.Query("a").Count, "陈旧信念应衰减到被移除。");
        AssertTrue(s.TotalEvictions > 0, "应记录淘汰计数。");
    }

    public static void TestRtBeliefTopThreatAndSnapshot()
    {
        var s = new NpcRtBeliefStore();
        s.Assert("a", "狼", "threat", new Vector3(5, 0, 0), 0.9f, -0.9f, 0.0);
        s.Assert("a", "面包", "resource", Vector3.Zero, 0.4f, 0.5f, 0.0);
        AssertTrue(s.TopThreat("a") != null, "应识别最强威胁。");
        AssertEqual("狼", s.TopThreat("a")!.Subject, "威胁主体应为狼。");
        var bag = new NpcRtStateBag(); s.Capture(bag, "b");
        string dump = bag.Dump();
        var s2 = new NpcRtBeliefStore(); s2.Restore(NpcRtStateBag.Parse(dump), "b");
        var bag2 = new NpcRtStateBag(); s2.Capture(bag2, "b");
        AssertEqual(dump, bag2.Dump(), "信念库快照应逐字往返一致。");
    }

    // ── Round 5：公平调度（修饿死） ───────────────────────────────────

    public static void TestRtFairnessNoStarvation()
    {
        var actors = new List<NpcRtActor>();
        for (int i = 0; i < 20; i++) actors.Add(new NpcRtActor("n" + i.ToString("D2"), "NPC" + i));
        AssertTrue(NpcRtFairScheduler.VerifyNoStarvation(actors, 60, 3),
            "20 actor / 每帧 3 名额，60 帧内每个 actor 都应至少被选中一次。");
    }

    public static void TestRtFairnessPrefersLongestWait()
    {
        var actors = new List<NpcRtActor>
        {
            new("a", "A") { LastDecisionTick = 10 },
            new("b", "B") { LastDecisionTick = 1 },
            new("c", "C") { LastDecisionTick = 5 }
        };
        var sched = new NpcRtFairScheduler();
        var picked = sched.Select(actors, 10, 2);
        AssertEqual(2, picked.Count, "应选出 2 个。");
        AssertEqual("b", picked[0].Id, "等待最久的 b 应优先。");
        AssertEqual("c", picked[1].Id, "其次为 c。");
    }

    public static void TestRtFairnessDiagnoseReportsWait()
    {
        var actors = new List<NpcRtActor>
        { new("a", "A") { LastDecisionTick = 2 }, new("b", "B") { LastDecisionTick = 8 } };
        var rep = new NpcRtFairScheduler().Diagnose(actors, 10, 1);
        AssertEqual(2, rep.Candidates, "候选数应为 2。");
        AssertEqual(1, rep.Selected, "选中数应为 1。");
        AssertEqual(8L, rep.MaxWaitTicks, "最大等待应为 8 tick。");
        AssertEqual(2L, rep.MinWaitTicks, "最小等待应为 2 tick。");
    }

    public static void TestRtWorldBudgetStillRespectedAfterFairness()
    {
        var w = new NpcRuntimeWorld(9UL, new NpcRtBudget { MaxFullDecisionsPerTick = 3 });
        for (int i = 0; i < 50; i++) w.Register("n" + i.ToString("D2"), "NPC" + i);
        long prev = 0;
        for (int t = 0; t < 25; t++)
        {
            w.Tick(0.1f);
            long now = w.Commands.Count == 0 ? 0 : w.Commands[w.Commands.Count - 1].Sequence;
            AssertTrue(now - prev <= 3, "接入公平调度后单帧决策数仍不得超过预算。");
            prev = now;
        }
    }

    // ── Round 5：路径点导航 ──────────────────────────────────────────

    private sealed class RecordingExecutor : INpcRtExecutor
    {
        public readonly List<NpcRtCommand> Applied = new();
        public int Ticks;
        public bool Apply(NpcRtCommand c) { Applied.Add(c); return true; }
        public void Tick(string id, float dt) { Ticks++; }
        public void Stop(string id) { }
    }

    public static void TestRtWaypointPlannerProducesQueueAndArrives()
    {
        var p = new NpcRtWaypointPlanner();
        p.SetDestination("a", Vector3.Zero, new Vector3(10, 0, 0));
        AssertTrue(p.WaypointCount("a") > 0, "应设置路径点队列。");
        AssertTrue(p.CurrentWaypoint("a")!.Value.X > 0f, "首个路径点应在目标方向上。");
        var cur = Vector3.Zero;
        for (int i = 0; i < 200; i++)
        {
            var wp = p.CurrentWaypoint("a");
            if (!wp.HasValue) break;
            cur = wp.Value;                     // 模拟沿路径点逐步移动
            p.Tick("a", cur, 0.1f);
        }
        AssertEqual(0, p.WaypointCount("a"), "到达后队列应清空。");
        AssertTrue(p.TotalArrivals > 0L, "应记录到达次数。");
    }

    public static void TestRtWaypointPlannerDetectsStuckAndReplans()
    {
        var p = new NpcRtWaypointPlanner { StuckSeconds = 0.3f, MaxReplans = 2 };
        p.SetDestination("a", Vector3.Zero, new Vector3(10, 0, 0));
        for (int i = 0; i < 10; i++) p.Tick("a", Vector3.Zero, 0.1f);
        AssertTrue(p.ReplanCount("a") > 0, "原地不动应触发重规划。");
        AssertTrue(p.TotalReplans > 0L, "应累计重规划次数。");
        AssertTrue(p.ReplanCount("a") <= p.MaxReplans, "重规划次数不得超过上限。");
    }

    public static void TestRtWaypointPlannerSnapshotRoundtrip()
    {
        var p = new NpcRtWaypointPlanner();
        p.SetDestination("a", Vector3.Zero, new Vector3(6, 0, 2));
        var bag = new NpcRtStateBag(); p.Capture(bag, "w");
        string dump = bag.Dump();
        var p2 = new NpcRtWaypointPlanner(); p2.Restore(NpcRtStateBag.Parse(dump), "w");
        var bag2 = new NpcRtStateBag(); p2.Capture(bag2, "w");
        AssertEqual(dump, bag2.Dump(), "路径状态应逐字往返一致。");
    }

    public static void TestRtWaypointExecutorWrapsInner()
    {
        var inner = new RecordingExecutor();
        var wrap = new NpcRtWaypointExecutor(inner);
        bool ok = wrap.Apply(new NpcRtCommand { ActorId = "a", Kind = NpcRtIntentNames.GoTo,
            TargetPosition = new Vector3(5, 0, 0) });
        AssertTrue(ok, "包装执行器应返回底层结果。");
        AssertTrue(wrap.Planner.WaypointCount("a") > 0, "GoTo 应自动生成路径点。");
        wrap.Tick("a", 0.1f);
        AssertTrue(inner.Ticks >= 1, "底层执行器应被逐帧推进。");
        AssertTrue(inner.Applied.Count >= 2, "路径点应转成下游命令。");
    }

    // ── Round 6：理想气体 / 压力 / 可逆反应 ──────────────────────────

    public static void TestRealityIdealGasStandardState()
    {
        double p = IdealGas.PressurePa(1.0, 273.15, 0.022414);
        AssertTrue(RealityAssert.RelativeError(ChemUnits.AtmospherePa, p) < 0.01,
            "1 mol @273.15K /22.414L 应约等于 1 atm，实际 " + p.ToString("0.0"));
        double n = IdealGas.Moles(ChemUnits.AtmospherePa, 273.15, 0.022414);
        AssertTrue(Math.Abs(n - 1.0) < 0.01, "逆算摩尔数应回到 1。");
    }

    public static void TestRealityGasPressureRatioIsLinearInKelvin()
    {
        AssertTrue(Math.Abs(IdealGas.PressureRatio(300.0, 600.0) - 2.0) < 1e-9,
            "定容下温度翻倍压力应翻倍。");
        AssertTrue(IdealGas.StandardAirMolesPerM3 > 40.0, "标况 1m³ 空气应约 40.9 mol。");
    }

    public static void TestRealityVesselBurstConservesMoles()
    {
        var w = new RealityWorld(1, 1, 3, 0.5);
        w.Grid.Emissivity = 0.0;
        double before = w.Species.TotalAll();
        for (int i = 0; i < w.Grid.Count; i++) w.Grid.T[i] = 2000.0;
        w.Vessels.SetSealed(1, true, burstPa: 1000.0);
        var ev = new List<RealityEvent>();
        int bursts = w.Vessels.Step(w.Grid, w.Species, null, ev, 0.0);
        AssertEqual(1, bursts, "超压应触发一次爆裂。");
        AssertTrue(ev.Exists(e => e.Kind == "burst"), "事件流应包含 burst。");
        AssertTrue(RealityAssert.Near(before, w.Species.TotalAll(), 1e-9, 1e-9),
            "泄压只在邻格间搬运，总摩尔必须守恒。");
        AssertTrue(w.Vessels.Cells[1].Burst, "该格应标记为已爆裂。");
    }

    public static void TestRealityReversibleEquilibriumKTrend()
    {
        var r = new ReversibleReaction
        { Equation = ChemEquation.Parse("N2 + 3 H2 -> 2 NH3"),
          StandardEnthalpyKJPerMol = -92.0, ReferenceK = 100.0 };
        double kLow = r.EquilibriumK(300.0), kHigh = r.EquilibriumK(900.0);
        AssertTrue(kLow > kHigh, "放热反应的平衡常数应随温度升高而下降。");
        AssertTrue(kLow > 0.0 && double.IsFinite(kHigh), "K 必须有限且为正。");
    }

    public static void TestRealityReversibleReactionConservesElements()
    {
        var r = new ReversibleReaction
        { Equation = ChemEquation.Parse("2 H2 + O2 -> 2 H2O"),
          ForwardPreExponential = 5.0, ActivationEnergyJPerMol = 1000.0,
          ReferenceK = 0.01, EnthalpyKJPerMol = -571.6 };
        var moles = new Dictionary<string, double> { ["H2"] = 2.0, ["O2"] = 1.0, ["H2O"] = 0.0 };
        var before = new Dictionary<string, double>(moles);
        var ev = new List<RealityEvent>();
        r.Step(600.0, 0.1, moles, 1.0, ev, 0.0, 0);
        AssertTrue(r.ForwardSteps > 0, "初始无产物时应走正向。");
        AssertTrue(moles.Values.All(v => v >= 0.0), "任何组分都不得为负。");
        AssertTrue(ChemReactor.ElementBalanceError(before, moles) < 1e-9,
            "可逆反应前后元素必须守恒。");
        // 再以产物为主推进，应走逆向
        moles["H2"] = 0.0; moles["O2"] = 0.0; moles["H2O"] = 4.0;
        var before2 = new Dictionary<string, double>(moles);
        r.Step(600.0, 0.1, moles, 1.0, ev, 0.1, 0);
        AssertTrue(r.ReverseSteps > 0, "产物过量时应走逆向。");
        AssertTrue(ChemReactor.ElementBalanceError(before2, moles) < 1e-9,
            "逆向反应同样必须守恒元素。");
    }

    public static void TestRealityThermalExpansionStressScales()
    {
        var e = new ThermalExpansion();
        var table = new ChemSubstanceTable();
        double s1 = e.StressMPa(100.0, table.Get("Steel"));
        double s2 = e.StressMPa(200.0, table.Get("Steel"));
        AssertTrue(s2 > s1 && s1 > 0.0, "约束热膨胀应力应随温差线性增大。");
        AssertTrue(Math.Abs(s2 / s1 - 2.0) < 1e-6, "应力应与温差成正比。");
        var w = new RealityWorld(1, 1, 1, 0.5);
        w.Grid.SetCell(0, "Steel", 600.0);
        double applied = e.ApplyTo(w.Degradation, w.Grid, 0);
        AssertTrue(applied > 0.0 && w.Degradation.Cells[0].StressMPa > 0.0,
            "热应力应叠加到结构单元。");
    }

    public static void TestRealityActiveSetOnlyMarksChangedCells()
    {
        var w = new RealityWorld(2, 1, 2, 0.5);
        w.Grid.Emissivity = 0.0;
        w.Active.Initialize(w.Grid);
        w.Active.Refresh(w.Grid, Array.Empty<RealityEvent>());
        w.Grid.AddHeat(0, 1000.0);
        int n = w.Active.Refresh(w.Grid, Array.Empty<RealityEvent>());
        AssertTrue(n >= 1 && n < w.Grid.Count, "只有变化的格子应被标记为活跃。");
        AssertTrue(w.Active.ShouldCompute(0), "受热格应活跃。");
        w.Active.Enabled = true;
        AssertTrue(w.Active.ShouldCompute(0), "启用裁剪后活跃格仍应计算。");
    }

    public static void TestRealitySchemaRejectsFutureVersion()
    {
        RealitySchema.Validate(RealitySchema.Current);
        RealitySchema.Validate(RealitySchema.LegacyUnversioned);
        AssertThrows<InvalidOperationException>(() => RealitySchema.Validate(999),
            "未来版本必须拒绝，避免静默错解码。");
        AssertTrue(RealitySchema.Current >= 2, "本轮快照应为 schema v2。");
    }

    public static void TestRealityWorldSnapshotCarriesSchemaAndVessels()
    {
        var w = new RealityWorld(2, 1, 2, 0.5);
        w.Grid.Emissivity = 0.0;
        w.Vessels.SetSealed(0, true, 2.0 * ChemUnits.AtmospherePa);
        for (int i = 0; i < 5; i++) w.Step(1.0 / 60.0);
        string snap = w.Snapshot();
        var meta = System.Text.Json.JsonSerializer.Deserialize<RealitySnapshot>(snap);
        AssertEqual(RealitySchema.Current, meta!.Schema, "快照应带当前 schema 版本。");
        AssertEqual(4, meta.VesselSealed.Length, "应保存每格密封状态。");
        AssertTrue(meta.VesselSealed[0], "第 0 格应仍为密封。");
        var w2 = new RealityWorld(2, 1, 2, 0.5); w2.Restore(snap);
        AssertEqual(snap, w2.Snapshot(), "含新子系统的快照仍须逐字往返一致。");
    }

    public static void TestRealityActiveSetEnabledStillConserves()
    {
        var w = new RealityWorld(3, 1, 3, 0.5);
        w.Grid.Emissivity = 0.0;
        w.Active.Enabled = true;
        w.Active.Initialize(w.Grid);
        for (int i = 0; i < 40; i++) w.Step(1.0 / 60.0);
        AssertTrue(w.Conservation().Within(1e-6),
            "启用活跃集裁剪后三重守恒仍须成立：" + w.Conservation());
    }
}
