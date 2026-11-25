// RealityPhysicsTests.cs — 物理化学核心验证套件（纯 CPU、离线、守恒可核算）
// 覆盖：化学式/配平、动力学、热传导、相变、燃烧、输运、断裂、快照回放。
using System.Numerics;
using YamaEngine.Physics.Reality;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests;

public static class RealityPhysicsTests
{
    public static void TestRealityFormulaParsing()
    {
        var c = ChemFormula.CountAtoms("C6H10O5");
        AssertEqual(6, c["C"], "碳数应解析。");
        AssertEqual(10, c["H"], "氢数应解析。");
        AssertEqual(5, c["O"], "氧数应解析。");
        AssertEqual(2, ChemFormula.CountAtoms("O2")["O"], "双原子氧。");
        AssertEqual(1, ChemFormula.CountAtoms("Fe")["Fe"], "单元素默认下标 1。");
    }

    public static void TestRealityBalancedEquation()
    {
        var eq = ChemEquation.Parse("2 H2 + O2 -> 2 H2O");
        AssertTrue(eq.IsBalanced, "合成水反应式应配平。");
        AssertEqual(3, eq.Reactants.Count, "反应物项数。");
        AssertEqual(1, eq.Products.Count, "产物项数。");
        AssertTrue(Math.Abs(eq.Products[0].Coefficient - 2.0) < 1e-9, "产物系数应为 2。");
    }

    public static void TestRealityUnbalancedEquationIsRejected()
    {
        var eq = ChemEquation.Parse("H2 + O2 -> H2O");
        AssertTrue(!eq.IsBalanced, "未配平反应式必须被识别。");
        var reactor = new ChemReactor(new ChemSubstanceTable());
        bool ok = reactor.Register(new ChemReaction { Equation = eq });
        AssertTrue(!ok, "未配平反应不得注册。");
        AssertEqual(1L, reactor.RejectedUnbalanced, "拒绝计数应递增。");
    }

    public static void TestRealityLimitingExtentNeverGoesNegative()
    {
        var eq = ChemEquation.Parse("CH4 + 2 O2 -> CO2 + 2 H2O");
        var moles = new Dictionary<string, double> { ["CH4"] = 0.1, ["O2"] = 0.05 };
        double limit = ChemEquation.LimitingExtent(eq.Reactants, moles);
        AssertTrue(Math.Abs(limit - 0.025) < 1e-9, "受氧气限制的反应进度应为 0.025。");
        AssertTrue(limit >= 0.0, "反应进度不得为负。");
        var reactor = new ChemReactor(new ChemSubstanceTable());
        reactor.Register(new ChemReaction { Equation = eq, PreExponential = 1e9,
            ActivationEnergyJPerMol = 0.0, EnthalpyKJPerMol = -890.0 });
        var ev = new List<RealityEvent>();
        reactor.Step(600.0, 10.0, moles, 1.0, new HashSet<string>(), ev, 0.0, 0);
        AssertTrue(moles["O2"] >= 0.0 && moles["CH4"] >= 0.0, "反应物绝不能被吃成负摩尔。");
    }

    public static void TestRealityArrheniusRateRisesWithTemperature()
    {
        var r = new ChemReaction { Equation = ChemEquation.Parse("2 H2 + O2 -> 2 H2O") };
        var moles = new Dictionary<string, double> { ["H2"] = 1.0, ["O2"] = 1.0 };
        double low = r.Rate(300.0, moles, false);
        double high = r.Rate(900.0, moles, false);
        AssertTrue(high > low, "升温应提高 Arrhenius 速率。");
        AssertTrue(low >= 0.0 && double.IsFinite(high), "速率必须有限且非负。");
    }

    public static void TestRealityCatalystMultipliesRate()
    {
        var r = new ChemReaction { Equation = ChemEquation.Parse("2 H2 + O2 -> 2 H2O"),
            Catalyst = "Iron", CatalystMultiplier = 4.0 };
        var moles = new Dictionary<string, double> { ["H2"] = 1.0, ["O2"] = 1.0 };
        double plain = r.Rate(500.0, moles, false);
        double catalyzed = r.Rate(500.0, moles, true);
        AssertTrue(Math.Abs(catalyzed - plain * 4.0) < 1e-9, "催化剂应只放大指前因子 4 倍。");
    }

    public static void TestRealityReactionConservesElements()
    {
        var eq = ChemEquation.Parse("CH4 + 2 O2 -> CO2 + 2 H2O");
        var before = new Dictionary<string, double> { ["CH4"] = 2.0, ["O2"] = 8.0, ["CO2"] = 0.0, ["H2O"] = 0.0 };
        var after = new Dictionary<string, double>(before);
        double extent = 2.0;
        foreach (var t in eq.Reactants) after[t.Formula] -= t.Coefficient * extent;
        foreach (var t in eq.Products) after[t.Formula] += t.Coefficient * extent;
        AssertTrue(ChemReactor.ElementBalanceError(before, after) < 1e-9,
            "反应前后每种元素原子摩尔数必须守恒。");
    }

    public static void TestRealityThermalConductionConservesEnergy()
    {
        var g = new ThermalGrid(2, 1, 1, 0.1, new ChemSubstanceTable(), "Air", 300.0);
        g.Emissivity = 0.0;
        g.SetCell(1, "Air", 400.0);
        double e0 = g.TotalEnergy();
        g.Step(0.001, new List<RealityEvent>(), 0.0);
        AssertTrue(g.T[0] > 300.0, "冷格应升温。");
        AssertTrue(g.T[1] < 400.0, "热格应降温。");
        AssertTrue(g.EnergyBalanceError(e0) < 1e-9, "关闭辐射后内部传导必须严格守恒。");
        AssertTrue(g.LastSubsteps >= 1, "应记录自动拆分的子步数。");
    }

    public static void TestRealityPhaseChangeClampsAtBoilingPoint()
    {
        var g = new ThermalGrid(1, 1, 1, 0.1, new ChemSubstanceTable(), "Water", 293.15);
        g.Emissivity = 0.0;
        double cap = g.MassKg[0] * g.Cp[0];
        g.AddHeat(0, cap * (373.15 - 293.15) + 1e5);
        AssertTrue(g.T[0] <= 373.15 + 1e-6, "沸腾平台应把温度钉在沸点。");
        AssertTrue(g.LatentEnergy[0] > 0.0, "多余热量应记入潜热账本。");
        AssertTrue(g.LatentEnergy[0] <= g.LatentCapacity[0], "潜热不得超出预算。");
        double e0 = cap * 293.15;
        AssertTrue(g.EnergyBalanceError(e0) < 1e-6, "显热+潜热总和必须守恒。");
    }

    public static void TestRealityCombustionRequiresOxygen()
    {
        var w = new RealityWorld(1, 1, 1, 0.5);
        w.Grid.Emissivity = 0.0;
        w.Grid.SetCell(0, "Wood", 650.0);
        w.Species.At(0)["O2"] = 0.0;
        double fuel0 = w.Grid.FuelKg[0];
        var ev = new List<RealityEvent>();
        w.Combustion.Step(w.Grid, i => w.Species.At(i), 0.1, Vector3.Zero, ev, 0.0);
        AssertTrue(Math.Abs(w.Grid.FuelKg[0] - fuel0) < 1e-9, "无氧气时不得燃烧。");
    }

    public static void TestRealityCombustionReleasesHeatAndProducts()
    {
        var w = new RealityWorld(1, 1, 1, 0.5);
        w.Grid.Emissivity = 0.0;
        w.Grid.SetCell(0, "Wood", 650.0);
        double fuel0 = w.Grid.FuelKg[0];
        double co20 = w.Species.At(0).GetValueOrDefault("CO2");
        var ev = new List<RealityEvent>();
        w.Combustion.Step(w.Grid, i => w.Species.At(i), 0.1, Vector3.Zero, ev, 0.0);
        AssertTrue(w.Grid.FuelKg[0] < fuel0, "燃料应被消耗。");
        AssertTrue(w.Species.At(0).GetValueOrDefault("CO2") > co20, "应生成燃烧产物 CO2。");
        AssertTrue(w.Grid.EnergyIn > 0.0, "放热应计入能量账本。");
        AssertTrue(w.Species.At(0).GetValueOrDefault("O2") >= 0.0, "氧气不得为负。");
    }

    public static void TestRealitySpeciesTransportConservesMoles()
    {
        var f = new SpeciesField(8, new[] { "O2", "CO2" });
        f.Add(0, "O2", 10.0);
        f.Add(7, "CO2", 4.0);
        double before = f.TotalAll();
        f.Step(0.01, 0.25, new Vector3(0.5f, 0f, 0f), _ => 1e-5, 8, 1, 1, out double flux);
        AssertTrue(f.LastExchange > 0.0, "存在梯度时应发生面交换。");
        AssertTrue(flux <= 0.25 + 1e-9, "通量钳制应限制单步比例。");
        AssertTrue(RealityAssert.Near(before, f.TotalAll(), 1e-9, 1e-9),
            "封闭边界输运必须严格守恒摩尔数。");
    }

    public static void TestRealityDegradationFracturesUnderOverload()
    {
        var w = new RealityWorld(1, 1, 1, 0.5);
        w.Grid.SetCell(0, "Steel", 293.15);
        w.Degradation.SetStress(0, 10000.0);
        var ev = new List<RealityEvent>();
        for (int i = 0; i < 20; i++)
            w.Degradation.Step(w.Grid, w.Species, 0.1, null, ev, i * 0.1);
        AssertTrue(w.Degradation.FractureCount > 0, "超载应触发断裂事件。");
        AssertTrue(w.Degradation.Cells[0].Broken, "单元应标记为已破坏。");
        AssertTrue(ev.Exists(e => e.Kind == "fracture"), "事件流应包含 fracture。");
    }

    public static void TestRealityWorldConservationWithinTolerance()
    {
        var w = new RealityWorld(4, 2, 4, 0.25);
        w.Grid.Emissivity = 0.0;
        for (int i = 0; i < 30; i++) w.Step(1.0 / 60.0);
        var r = w.Conservation();
        AssertTrue(r.EnergyError < 1e-6, "能量误差应低于 1e-6：" + r);
        AssertTrue(r.MassError < 1e-9, "质量误差应低于 1e-9：" + r);
        AssertTrue(r.ElementError < 1e-9, "元素守恒误差应低于 1e-9：" + r);
    }

    public static void TestRealityWorldSnapshotRoundtripIsExact()
    {
        var w = new RealityWorld(2, 1, 2, 0.5);
        w.Grid.Emissivity = 0.0;
        w.Grid.SetCell(0, "Wood", 600.0);
        w.ResetEnergyBaseline();
        w.Wind = new Vector3(0.2f, 0f, 0f);
        for (int i = 0; i < 5; i++) w.Step(1.0 / 60.0);
        string snap = w.Snapshot();
        var w2 = new RealityWorld(2, 1, 2, 0.5);
        w2.Restore(snap);
        AssertEqual(snap, w2.Snapshot(), "恢复后的状态指纹应逐字一致。");
    }

    public static void TestRealityWorldIsDeterministicFromSnapshot()
    {
        var w = new RealityWorld(2, 2, 2, 0.5);
        w.Grid.Emissivity = 0.0;
        w.Wind = new Vector3(0.1f, 0f, 0f);
        for (int i = 0; i < 10; i++) w.Step(1.0 / 60.0);
        AssertTrue(RealityWorld.VerifyDeterminism(w.Snapshot(), 1.0 / 60.0, 20),
            "同快照同 dt 序列重放两次必须完全一致。");
    }
}
