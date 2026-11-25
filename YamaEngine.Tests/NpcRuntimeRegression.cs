// NpcRuntimeRegression.cs — Round 3 回归套件 + 一键聚合入口
// 全部 Test*() 由 XunitAdapter 自动发现；RunAll() 不改任何既有文件即可被 Program.cs 一行调用。
using System.Numerics;
using System.Reflection;
using YamaEngine.NPC.Runtime;
using YamaEngine.Physics.Reality;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests;

public static class NpcRuntimeRegression
{
    // ── 环形命令日志 ────────────────────────────────────────────────

    public static void TestRtRingLogEvictsOldest()
    {
        var log = new NpcRtRingLog(3);
        for (int i = 1; i <= 5; i++)
            log.Add(new NpcRtCommand { Sequence = i, ActorId = "a", Kind = NpcRtIntentNames.Idle });
        AssertEqual(3, log.Count, "容量应为 3。");
        AssertEqual(5L, log.TotalAdded, "累计计数应为 5。");
        AssertEqual(2L, log.Dropped, "应淘汰 2 条最旧命令。");
        var latest = log.Latest(3);
        AssertEqual(5L, latest[0].Sequence, "最新一条应为 #5。");
        AssertEqual(3L, latest[2].Sequence, "最旧保留一条应为 #3。");
    }

    public static void TestRtRingLogByActorAndSnapshot()
    {
        var log = new NpcRtRingLog(8);
        log.Add(new NpcRtCommand { Sequence = 1, ActorId = "a", Kind = NpcRtIntentNames.GoTo,
            TargetPosition = new Vector3(2, 0, 3), Priority = 0.8f, Source = "heuristic" });
        log.Add(new NpcRtCommand { Sequence = 2, ActorId = "b", Kind = NpcRtIntentNames.Flee });
        log.Add(new NpcRtCommand { Sequence = 3, ActorId = "a", Kind = NpcRtIntentNames.Idle });
        AssertEqual(2, log.ByActor("a").Count, "按 actor 过滤应得到 2 条。");
        var bag = new NpcRtStateBag(); log.Capture(bag, "log");
        var log2 = new NpcRtRingLog(8); log2.Restore(NpcRtStateBag.Parse(bag.Dump()), "log");
        AssertEqual(3, log2.Count, "快照应恢复条数。");
        AssertEqual(3L, log2.Latest(1)[0].Sequence, "最新序号应恢复为 3。");
        AssertTrue(Vector3.Distance(log2.ByActor("a")[0].TargetPosition, new Vector3(2, 0, 3)) < 1e-5f,
            "向量字段应恢复。");
    }

    // ── 冲突仲裁 ────────────────────────────────────────────────────

    public static void TestRtConflictResolverPrefersReflexOverLlm()
    {
        var r = new NpcRtConflictResolver();
        var llm = new NpcRtCommand { Sequence = 1, ActorId = "a", Source = "llm", Priority = 0.99f };
        var reflex = new NpcRtCommand { Sequence = 2, ActorId = "a", Source = "reflex", Priority = 0.2f };
        var win = r.Resolve(llm, reflex);
        AssertEqual("reflex", win.Source, "反射命令应压过低可信度 LLM 命令，哪怕优先级更低。");
        AssertTrue(r.ResolvedCount >= 1, "应记录仲裁次数。");
    }

    public static void TestRtConflictResolverTieBreakBySequence()
    {
        var r = new NpcRtConflictResolver();
        var early = new NpcRtCommand { Sequence = 1, ActorId = "a", Source = "heuristic", Priority = 0.5f };
        var late = new NpcRtCommand { Sequence = 2, ActorId = "a", Source = "heuristic", Priority = 0.5f };
        AssertEqual(1L, r.Resolve(late, early).Sequence, "完全同分时早序号应胜出（稳定防抖）。");
    }

    public static void TestRtConflictMergeBatchOnePerActor()
    {
        var r = new NpcRtConflictResolver();
        var batch = new[]
        {
            new NpcRtCommand { Sequence = 1, ActorId = "b", Source = "heuristic", Priority = 0.4f },
            new NpcRtCommand { Sequence = 2, ActorId = "a", Source = "llm", Priority = 0.9f },
            new NpcRtCommand { Sequence = 3, ActorId = "a", Source = "reflex", Priority = 0.1f },
            new NpcRtCommand { Sequence = 4, ActorId = "b", Source = "scripted", Priority = 0.7f }
        };
        var winners = r.MergeBatch(batch);
        AssertEqual(2, winners.Count, "每个 actor 只应保留一个胜出命令。");
        AssertEqual("a", winners[0].ActorId, "输出应按 actorId 排序。");
        AssertEqual("reflex", winners[0].Source, "a 应由反射命令胜出。");
        AssertEqual("scripted", winners[1].Source, "b 应由脚本命令胜出。");
    }

    // ── 规模化 ──────────────────────────────────────────────────────

    public static void TestRtScaleProbe1000ActorsRespectsBudget()
    {
        var w = new NpcRuntimeWorld(777UL, new NpcRtBudget { MaxFullDecisionsPerTick = 8 });
        for (int i = 0; i < 1000; i++)
        {
            var a = w.Register("n" + i.ToString("D4"), "NPC" + i);
            a.Position = new Vector3(i % 32, 0f, i / 32);
        }
        var rep = NpcRtScaleProbe.Run(w, 30, 0.1f);
        AssertEqual(1000, rep.Actors, "应注册 1000 个 actor。");
        AssertEqual(30, rep.Ticks, "应推进 30 tick。");
        AssertTrue(rep.Commands > 0, "应产生命令。");
        AssertEqual(0, rep.BudgetViolations, "单帧决策数不得越过预算。");
        AssertTrue(rep.MaxCommandsPerTick <= 8, "峰值单帧命令数不得超过预算。");
    }

    public static void TestRtScaleProbeIsDeterministic()
    {
        var w = new NpcRuntimeWorld(31337UL);
        for (int i = 0; i < 64; i++) w.Register("n" + i.ToString("D2"), "NPC" + i);
        for (int i = 0; i < 20; i++) w.Tick(0.1f);
        AssertTrue(NpcRuntimeWorld.VerifyDeterminism(w.Snapshot(), 0.1f, 25),
            "规模化状态下回放仍须逐字一致。");
    }

    // ── 物理化学场景 ────────────────────────────────────────────────

    public static void TestRealityScenarioWoodFireIgnitesAndConserves()
    {
        var r = RealityScenarioLibrary.WoodFireSpread();
        AssertTrue(r.EventCount > 0, "燃烧场景应产生事件。");
        AssertTrue(r.Ignitions > 0, "应出现蔓延/预热点火事件。");
        AssertTrue(r.Conservation.EnergyError < 1e-6, "能量账本应守恒：" + r.Conservation);
        AssertTrue(r.Conservation.ElementError < 1e-9, "元素应守恒：" + r.Conservation);
    }

    public static void TestRealityScenarioHydrogenProducesWater()
    {
        var r = RealityScenarioLibrary.HydrogenCatalyticSynthesis();
        AssertTrue(r.WaterProducedMol > 0.0, "催化合成应净生成 H2O。");
        AssertTrue(r.Conservation.ElementError < 1e-9, "反应前后元素应守恒：" + r.Conservation);
        AssertTrue(double.IsFinite(r.MaxTemperatureK), "温度必须有限。");
    }

    public static void TestRealityScenarioSteelFracturesUnderHeat()
    {
        var r = RealityScenarioLibrary.SteelThermalFracture();
        AssertTrue(r.Fractures > 0, "高温软化 + 超载应触发断裂。");
        AssertTrue(r.MaxTemperatureK > 1000.0, "场景应处于高温。");
        AssertTrue(r.Conservation.MassError < 1e-9, "输运摩尔数应守恒：" + r.Conservation);
    }

    public static void TestRealityScenarioLibraryAllDeterministic()
    {
        var a = RealityScenarioLibrary.RunAll().Select(x => x.ToString()).ToArray();
        var b = RealityScenarioLibrary.RunAll().Select(x => x.ToString()).ToArray();
        AssertEqual(3, a.Length, "应有 3 个内置场景。");
        AssertEqual(string.Join("|", a), string.Join("|", b), "场景重复运行结果必须完全一致。");
    }

    public static void TestRealityStressWalkStaysFiniteAndConserved()
    {
        var r = RealityStressWalk.Run(20261124UL, 200);
        AssertTrue(r.AllFinite, "长跑中温度/状态必须全程有限且非负。");
        AssertTrue(r.WorstError < 1e-6, "随机压力全程守恒误差应低于 1e-6：" + r);
        AssertTrue(r.Deterministic, "压力末端快照应可重放：" + r);
    }

    public static void TestRealityMassLedgerNetGasIsConsistent()
    {
        var l = new RealityMassLedger();
        l.RecordCombustion(0.1, 0.3, 0.05, 0.08, 0.01);
        l.ReactionExtentMol = 0.02;
        AssertTrue(l.FuelConsumedKg > 0.0 && l.O2ConsumedMol > 0.0, "账本应记录燃料与氧气。");
        AssertTrue(Math.Abs(l.NetGasMoles - (-0.16)) < 1e-9, "净气相摩尔数应为产物减消耗。");
        AssertTrue(l.ToString().Contains("netGas"), "报告应包含净气相字段。");
    }

    // ── 一键聚合（不改既有 Program.cs 即可接入控制台运行器） ────────

    /// <summary>
    /// 反射聚合三个测试类的全部无参 Test*() 方法。
    /// Program.cs 只需追加一行 NpcRuntimeRegression.RunAll(); 即可跑完全部新增用例。
    /// 本方法不以 Test 开头，XunitAdapter 不会重复发现它。
    /// </summary>
    public static void RunAll()
    {
        var types = new[]
        {
            typeof(NpcRuntimeTests), typeof(RealityPhysicsTests), typeof(NpcRuntimeRegression),
            typeof(NpcRuntimeDeepTests), typeof(SimulationBridgeTests)
        };
        foreach (var t in types)
            foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Static)
                       .Where(m => m.Name.StartsWith("Test", StringComparison.Ordinal)
                                && m.ReturnType == typeof(void)
                                && m.GetParameters().Length == 0)
                       .OrderBy(m => m.Name, StringComparer.Ordinal))
                m.Invoke(null, null);
    }
}
