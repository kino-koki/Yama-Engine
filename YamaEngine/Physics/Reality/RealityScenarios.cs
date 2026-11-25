// ════════════════════════════════════════════════════════════════════════
// RealityScenarios.cs — Round 2：开放系统质量账本 / 三个可复现场景 / 随机压力漫步
// 目的：把"守恒"从单点断言变成可反复重放的场景回归，明天直接跑即可验真。
// ════════════════════════════════════════════════════════════════════════
using System.Numerics;

namespace YamaEngine.Physics.Reality;

/// <summary>燃烧是开放系统：固体燃料转成气态产物，单独记账，不混进封闭输运守恒。</summary>
public sealed class RealityMassLedger
{
    public double FuelConsumedKg;
    public double O2ConsumedMol;
    public double CO2ProducedMol;
    public double H2OProducedMol;
    public double SmokeProducedMol;
    public double ReactionExtentMol;
    public double NetGasMoles
        => CO2ProducedMol + H2OProducedMol + SmokeProducedMol - O2ConsumedMol;

    public void RecordCombustion(double dm, double o2Used, double co2, double h2o, double smoke)
    {
        FuelConsumedKg += dm; O2ConsumedMol += o2Used;
        CO2ProducedMol += co2; H2OProducedMol += h2o; SmokeProducedMol += smoke;
    }
    public override string ToString()
        => $"fuel={FuelConsumedKg:0.000}kg O2={O2ConsumedMol:0.000}mol "
         + $"CO2={CO2ProducedMol:0.000} H2O={H2OProducedMol:0.000} "
         + $"smoke={SmokeProducedMol:0.000} netGas={NetGasMoles:0.000}mol";
}

public sealed class RealityScenarioResult
{
    public string Name = "";
    public ConservationReport Conservation;
    public double MaxTemperatureK;
    public double FuelRemainingKg;
    public double WaterProducedMol;
    public int EventCount;
    public int Ignitions;
    public int Fractures;
    public string Notes = "";
    public override string ToString()
        => $"{Name}: {Conservation} Tmax={MaxTemperatureK:0.0}K events={EventCount} "
         + $"ignitions={Ignitions} fractures={Fractures}";
}

public static class RealityScenarioLibrary
{
    public static RealityScenarioResult WoodFireSpread()
    {
        var w = new RealityWorld(6, 1, 4, 0.5, seed: 7UL);
        w.Grid.Emissivity = 0.0;
        w.Combustion.SpreadBoostK = 300.0;   // 让预热真正越过燃点，形成蔓延
        foreach (var (x, z) in new[] { (2, 1), (3, 1), (1, 1), (2, 2), (2, 0) })
            w.Grid.SetCell(w.Grid.Index(x, 0, z), "Wood", x == 2 && z == 1 ? 700.0 : 293.15);
        w.ResetEnergyBaseline();
        w.Wind = new Vector3(0.4f, 0f, 0f);
        for (int i = 0; i < 120; i++) w.Step(1.0 / 60.0);
        return Summarize(w, "WoodFireSpread",
            "燃料+氧气+温度三要素、风向蔓延、熄灭；蔓延为预热升温事件");
    }

    public static RealityScenarioResult HydrogenCatalyticSynthesis()
    {
        var w = new RealityWorld(1, 1, 1, 0.5, seed: 11UL);
        w.Grid.Emissivity = 0.0;
        w.Grid.SetCell(0, "Air", 900.0);
        w.ResetEnergyBaseline();
        w.Species.Add(0, "H2", 2.0);
        w.Species.Add(0, "O2", 1.5);
        w.Catalysts.Add("Iron");
        double water0 = w.Species.At(0).GetValueOrDefault("H2O");
        for (int i = 0; i < 60; i++) w.Step(1.0 / 60.0);
        var r = Summarize(w, "HydrogenCatalyticSynthesis",
            "2H2+O2→2H2O 配平反应 + 铁催化 + 放热回写热场");
        r.WaterProducedMol = w.Species.At(0).GetValueOrDefault("H2O") - water0;
        return r;
    }

    public static RealityScenarioResult SteelThermalFracture()
    {
        var w = new RealityWorld(1, 1, 1, 0.5, seed: 13UL);
        w.Grid.Emissivity = 0.0;
        w.Grid.SetCell(0, "Steel", 1800.0);
        w.ResetEnergyBaseline();
        w.Degradation.SetStress(0, 1200.0);
        for (int i = 0; i < 200; i++) w.Step(1.0 / 60.0);
        return Summarize(w, "SteelThermalFracture",
            "高温软化降低屈服强度 → Miner 疲劳累积 → 断裂事件");
    }

    public static IReadOnlyList<RealityScenarioResult> RunAll()
        => new[] { WoodFireSpread(), HydrogenCatalyticSynthesis(), SteelThermalFracture() };

    private static RealityScenarioResult Summarize(RealityWorld w, string name, string notes)
    {
        double tmax = 0.0, fuel = 0.0;
        for (int i = 0; i < w.Grid.Count; i++)
        { tmax = Math.Max(tmax, w.Grid.T[i]); fuel += w.Grid.FuelKg[i]; }
        return new RealityScenarioResult
        {
            Name = name, Conservation = w.Conservation(), MaxTemperatureK = tmax,
            FuelRemainingKg = fuel, EventCount = w.Events.Count,
            Ignitions = w.Events.Count(e => e.Kind == "ignition"),
            Fractures = w.Events.Count(e => e.Kind == "fracture"),
            Notes = notes
        };
    }
}

/// <summary>随机 dt / 随机风 / 随机热源的长跑压力：全程有限、守恒、可重放。</summary>
public static class RealityStressWalk
{
    public sealed class WalkReport
    {
        public int Steps;
        public double WorstError;
        public bool AllFinite = true;
        public bool Deterministic;
        public override string ToString()
            => $"steps={Steps} worst={WorstError:0.00e+00} finite={AllFinite} replay={Deterministic}";
    }

    public static WalkReport Run(ulong seed, int steps)
    {
        var rng = new RealityRng(seed);
        var w = new RealityWorld(3, 2, 3, 0.5, seed);
        w.Grid.Emissivity = 0.0;
        double worst = 0.0;
        bool finite = true;
        for (int i = 0; i < Math.Max(0, steps); i++)
        {
            double dt = rng.Range(0.004, 1.0 / 30.0);
            w.Wind = new Vector3((float)rng.Range(-1.0, 1.0), 0f, (float)rng.Range(-1.0, 1.0));
            int cell = Math.Clamp((int)(rng.Next() * w.Grid.Count), 0, w.Grid.Count - 1);
            if (rng.Next() > 0.7) w.Grid.AddHeat(cell, rng.Range(0.0, 5000.0));
            w.Step(dt);
            var c = w.Conservation();
            worst = Math.Max(worst, Math.Max(c.EnergyError, Math.Max(c.MassError, c.ElementError)));
            for (int k = 0; k < w.Grid.Count; k++)
                if (!double.IsFinite(w.Grid.T[k]) || w.Grid.T[k] < 0.0) { finite = false; break; }
            if (!finite) break;
        }
        string snap = w.Snapshot();
        return new WalkReport
        {
            Steps = steps, WorstError = worst, AllFinite = finite,
            Deterministic = RealityWorld.VerifyDeterminism(snap, 1.0 / 60.0, 8)
        };
    }
}
