// ════════════════════════════════════════════════════════════════════════
// RealityWorld.cs — 统一世界：反应 → 燃烧 → 热传导 → 输运 → 退化 → 守恒核算
// 固定子步、确定性、快照可回放；所有 GPU/渲染/既有刚体只经 IRealityBodySink 接缝。
// ════════════════════════════════════════════════════════════════════════
using System.Numerics;
using System.Text.Json;

namespace YamaEngine.Physics.Reality;

public readonly struct ConservationReport
{
    public readonly double EnergyError;
    public readonly double MassError;
    public readonly double ElementError;
    public readonly double MaxFluxRatio;
    public ConservationReport(double e, double m, double el, double f)
    { EnergyError = e; MassError = m; ElementError = el; MaxFluxRatio = f; }
    public bool Within(double tol) => EnergyError <= tol && MassError <= tol && ElementError <= tol;
    public override string ToString()
        => $"energy={EnergyError:0.00e+00} mass={MassError:0.00e+00} element={ElementError:0.00e+00} flux={MaxFluxRatio:0.00}";
}

public sealed class RealitySnapshot
{
    public int Schema { get; set; } = RealitySchema.Current;
    public bool[] VesselSealed { get; set; } = Array.Empty<bool>();
    public bool[] VesselBurst { get; set; } = Array.Empty<bool>();
    public double[] VesselBurstPa { get; set; } = Array.Empty<double>();
    public Dictionary<string, string> ActiveState { get; set; } = new(StringComparer.Ordinal);
    public int Nx { get; set; } = 8;
    public int Ny { get; set; } = 4;
    public int Nz { get; set; } = 8;
    public double CellSize { get; set; } = 0.25;
    public double Time { get; set; }
    public long Step { get; set; }
    public ulong Rng { get; set; }
    public double EnergyIn { get; set; }
    public double EnergyOut { get; set; }
    public double WindX { get; set; }
    public double WindY { get; set; }
    public double WindZ { get; set; }
    public double Emissivity { get; set; } = 0.85;
    public double AmbientK { get; set; } = ChemUnits.AmbientK;
    public double MaxCflFraction { get; set; } = 0.20;
    public double CombustionMinO2 { get; set; } = 0.10;
    public double CombustionBurnRate { get; set; } = 0.02;
    public double CombustionSpreadBoost { get; set; } = 60.0;
    public double MinerExponent { get; set; } = 2.0;
    public double FatigueRate { get; set; } = 0.01;
    public double ThermalSofteningK { get; set; } = 2000.0;
    public double CorrosionScale { get; set; } = 1.0;
    public double[] T { get; set; } = Array.Empty<double>();
    public double[] Fuel { get; set; } = Array.Empty<double>();
    public double[] Latent { get; set; } = Array.Empty<double>();
    public double[] Damage { get; set; } = Array.Empty<double>();
    public double[] Corrosion { get; set; } = Array.Empty<double>();
    public double[] Integrity { get; set; } = Array.Empty<double>();
    public double[] Stress { get; set; } = Array.Empty<double>();
    public string[] Substance { get; set; } = Array.Empty<string>();
    public double[] EventTime { get; set; } = Array.Empty<double>();
    public double[] EventValue { get; set; } = Array.Empty<double>();
    public int[] EventCell { get; set; } = Array.Empty<int>();
    public string[] EventKind { get; set; } = Array.Empty<string>();
    public string[] EventDetail { get; set; } = Array.Empty<string>();
    public Dictionary<string, double[]> Species { get; set; } = new(StringComparer.Ordinal);
}

public sealed class RealityWorld
{
    public readonly ThermalGrid Grid;
    public readonly SpeciesField Species;
    public readonly ChemReactor Reactor;
    public readonly CombustionModel Combustion;
    public readonly MaterialDegradation Degradation;
    public readonly RealityRng Rng;
    public readonly PressureVessel Vessels;
    public readonly ActiveCellSet Active = new();
    public readonly ThermalExpansion Expansion = new();
    public readonly List<ReversibleReaction> Equilibria = new();
    public readonly List<RealityEvent> Events = new();
    public readonly HashSet<string> Catalysts = new(StringComparer.OrdinalIgnoreCase);

    public IRealityBodySink? BodySink { get; set; }
    public Vector3 Wind { get; set; }
    public double TimeSeconds { get; private set; }
    public long StepIndex { get; private set; }
    public double FixedStep { get; set; } = 1.0 / 60.0;
    public double InitialEnergy { get; private set; }

    public double LastEnergyError { get; private set; }
    public double LastMassError { get; private set; }
    public double LastElementError { get; private set; }
    public double LastMaxFluxRatio { get; private set; }

    private static readonly string[] DefaultSpecies =
        { "O2", "CO2", "H2O", "Smoke", "CH4", "H2", "N2", "C8H18", "C2H6O" };

    public RealityWorld(int nx = 8, int ny = 4, int nz = 8, double cellSize = 0.25,
        ulong seed = 20261124UL, string defaultSubstance = "Air")
    {
        var table = new ChemSubstanceTable();
        Grid = new ThermalGrid(nx, ny, nz, cellSize, table, defaultSubstance);
        Species = new SpeciesField(Grid.Count, DefaultSpecies);
        Reactor = new ChemReactor(table);
        Combustion = new CombustionModel();
        Degradation = new MaterialDegradation(Grid.Count);
        Vessels = new PressureVessel(Grid.Count);
        Rng = new RealityRng(seed);
        InitialEnergy = Grid.TotalEnergy();
        Active.Initialize(Grid);
        SeedAir();
        RegisterDefaultReactions();
    }

    private void SeedAir()
    {
        double v = Math.Pow(Grid.CellSize, 3.0) / ChemUnits.StandardMolarVolumeM3;
        for (int i = 0; i < Grid.Count; i++)
        {
            Species.Add(i, "N2", v * 0.79);
            Species.Add(i, "O2", v * 0.21);
        }
    }

    private void RegisterDefaultReactions()
    {
        Reactor.Register(new ChemReaction
        {
            Equation = ChemEquation.Parse("2 H2 + O2 -> 2 H2O"),
            ActivationEnergyJPerMol = 60000, PreExponential = 8.0,
            EnthalpyKJPerMol = -571.6, Catalyst = "Iron", CatalystMultiplier = 4.0
        });
        Reactor.Register(new ChemReaction
        {
            Equation = ChemEquation.Parse("CH4 + 2 O2 -> CO2 + 2 H2O"),
            ActivationEnergyJPerMol = 90000, PreExponential = 12.0,
            EnthalpyKJPerMol = -890.3
        });
    }

    /// <summary>推进 dt，内部拆成不超过 FixedStep 的确定性子步。</summary>
    public void Step(double dt)
    {
        int n = Math.Clamp((int)Math.Ceiling(Math.Max(0.0, dt) / Math.Max(1e-6, FixedStep)), 1, 4096);
        double sub = Math.Max(0.0, dt) / n;
        for (int i = 0; i < n; i++) StepOnce(sub);
    }

    public void StepOnce(double dt)
    {
        StepIndex++;
        TimeSeconds += dt;
        double cellVolume = Math.Pow(Grid.CellSize, 3.0);
        var beforeTotals = Species.SnapshotTotals();
        double worstElement = 0.0;
        int eventStart = Events.Count;

        // 1) 化学反应：逐格推进，放热回写热场；同时核算元素守恒
        for (int i = 0; i < Grid.Count; i++)
        {
            if (!Active.ShouldCompute(i)) continue;
            var moles = Species.At(i);
            var before = new Dictionary<string, double>(moles, StringComparer.Ordinal);
            double heat = Reactor.Step(Grid.T[i], dt, moles, cellVolume,
                Catalysts, Events, TimeSeconds, i);
            foreach (var eq in Equilibria)
                heat += eq.Step(Grid.T[i], dt, moles, cellVolume, Events, TimeSeconds, i);
            if (Math.Abs(heat) > 1e-12) Grid.AddHeat(i, heat);
            worstElement = Math.Max(worstElement,
                ChemReactor.ElementBalanceError(before, moles));
        }
        LastElementError = worstElement;
        LastMassError = Species.BalanceError(beforeTotals);

        // 2) 燃烧：燃料 → 热 + 产物 + 蔓延（开放系统，质量核算不含此步）
        Combustion.Step(Grid, i => Species.At(i), dt, Wind, Events, TimeSeconds);

        // 2b) 密闭超压检测：爆裂并把气体搬到邻格（总摩尔守恒），冲量发给刚体
        Vessels.Step(Grid, Species, BodySink, Events, TimeSeconds);

        // 3) 热传导（含相变、辐射）
        Grid.Step(dt, Events, TimeSeconds);

        // 4) 多组分输运（封闭边界，严格守恒）
        Species.Step(dt, Grid.CellSize, Wind,
            i => Grid.Table.Get(Grid.Substance[i]).DiffusivityM2S,
            Grid.Nx, Grid.Ny, Grid.Nz, out double flux);
        LastMaxFluxRatio = flux;

        // 5) 结构退化与断裂
        Degradation.Step(Grid, Species, dt, BodySink, Events, TimeSeconds);

        // 6) 活跃集刷新（默认关闭；显式启用时只裁剪下一帧的非必要反应计算）
        var thisStepEvents = Events.GetRange(Math.Min(eventStart, Events.Count),
            Math.Max(0, Events.Count - eventStart));
        Active.Refresh(Grid, thisStepEvents);

        LastEnergyError = Grid.EnergyBalanceError(InitialEnergy);
        if (Events.Count > 2048) Events.RemoveRange(0, Events.Count - 2048);
    }

    public ConservationReport Conservation()
        => new(LastEnergyError, LastMassError, LastElementError, LastMaxFluxRatio);

    /// <summary>修改材料/初始温度后重设能量基线，保持账本连续。</summary>
    public void ResetEnergyBaseline()
        => InitialEnergy = Grid.TotalEnergy() - Grid.EnergyIn + Grid.EnergyOut;

    public string Snapshot()
    {
        var s = new RealitySnapshot
        {
            Nx = Grid.Nx, Ny = Grid.Ny, Nz = Grid.Nz, CellSize = Grid.CellSize,
            Time = TimeSeconds, Step = StepIndex, Rng = Rng.State,
            EnergyIn = Grid.EnergyIn, EnergyOut = Grid.EnergyOut,
            WindX = Wind.X, WindY = Wind.Y, WindZ = Wind.Z,
            Emissivity = Grid.Emissivity, AmbientK = Grid.AmbientK,
            MaxCflFraction = Grid.MaxCflFraction,
            CombustionMinO2 = Combustion.MinO2Fraction,
            CombustionBurnRate = Combustion.BurnRateKgPerS,
            CombustionSpreadBoost = Combustion.SpreadBoostK,
            MinerExponent = Degradation.MinerExponent,
            FatigueRate = Degradation.FatigueRatePerS,
            ThermalSofteningK = Degradation.ThermalSofteningK,
            CorrosionScale = Degradation.CorrosionScale,
            T = (double[])Grid.T.Clone(), Fuel = (double[])Grid.FuelKg.Clone(),
            Latent = (double[])Grid.LatentEnergy.Clone(),
            Damage = Degradation.Cells.Select(c => c.Damage).ToArray(),
            Corrosion = Degradation.Cells.Select(c => c.Corrosion).ToArray(),
            Integrity = Degradation.Cells.Select(c => c.Integrity).ToArray(),
            Stress = Degradation.Cells.Select(c => c.StressMPa).ToArray(),
            Substance = (string[])Grid.Substance.Clone(),
            Schema = RealitySchema.Current,
            VesselSealed = Vessels.Cells.Select(c => c.Sealed).ToArray(),
            VesselBurst = Vessels.Cells.Select(c => c.Burst).ToArray(),
            VesselBurstPa = Vessels.Cells.Select(c => c.BurstPressurePa).ToArray(),
            EventTime = Events.Select(e => e.Time).ToArray(),
            EventValue = Events.Select(e => e.Value).ToArray(),
            EventCell = Events.Select(e => e.Cell).ToArray(),
            EventKind = Events.Select(e => e.Kind).ToArray(),
            EventDetail = Events.Select(e => e.Detail).ToArray()
        };
        foreach (var name in DefaultSpecies)
            s.Species[name] = Enumerable.Range(0, Grid.Count).Select(i => Species.At(i).GetValueOrDefault(name)).ToArray();
        Active.Capture(s.ActiveState, "active");
        return JsonSerializer.Serialize(s);
    }

    public void Restore(string json)
    {
        var s = JsonSerializer.Deserialize<RealitySnapshot>(json) ?? new RealitySnapshot();
        RealitySchema.Validate(s.Schema == 0 ? RealitySchema.LegacyUnversioned : s.Schema);
        if (s.Nx * s.Ny * s.Nz != Grid.Count)
            throw new InvalidOperationException(
                $"快照网格 {s.Nx}x{s.Ny}x{s.Nz} 与当前世界 {Grid.Nx}x{Grid.Ny}x{Grid.Nz} 不匹配。");
        for (int i = 0; i < Vessels.Cells.Count; i++)
        {
            if (i < s.VesselSealed.Length) Vessels.Cells[i].Sealed = s.VesselSealed[i];
            if (i < s.VesselBurst.Length) Vessels.Cells[i].Burst = s.VesselBurst[i];
            if (i < s.VesselBurstPa.Length) Vessels.Cells[i].BurstPressurePa = s.VesselBurstPa[i];
        }
        Active.Restore(s.ActiveState, "active");
        TimeSeconds = s.Time; StepIndex = s.Step; Rng.SetState(s.Rng);
        Grid.EnergyIn = s.EnergyIn; Grid.EnergyOut = s.EnergyOut;
        int version = s.Schema == 0 ? RealitySchema.LegacyUnversioned : s.Schema;
        if (version >= 2)   // v1 快照不含求解器参数，保留构造默认值以保持兼容
        {
            Grid.Emissivity = s.Emissivity; Grid.AmbientK = s.AmbientK;
            Grid.MaxCflFraction = s.MaxCflFraction;
            Combustion.MinO2Fraction = s.CombustionMinO2;
            Combustion.BurnRateKgPerS = s.CombustionBurnRate;
            Combustion.SpreadBoostK = s.CombustionSpreadBoost;
            Degradation.MinerExponent = s.MinerExponent;
            Degradation.FatigueRatePerS = s.FatigueRate;
            Degradation.ThermalSofteningK = s.ThermalSofteningK;
            Degradation.CorrosionScale = s.CorrosionScale;
        }
        Wind = new Vector3((float)s.WindX, (float)s.WindY, (float)s.WindZ);
        Copy(s.T, Grid.T); Copy(s.Fuel, Grid.FuelKg); Copy(s.Latent, Grid.LatentEnergy);
        for (int i = 0; i < Grid.Count && i < s.Substance.Length; i++)
            Grid.Substance[i] = s.Substance[i];
        for (int i = 0; i < Grid.Count; i++) Grid.RefreshMaterial(i);
        for (int i = 0; i < Degradation.Cells.Count; i++)
        {
            var c = Degradation.Cells[i];
            c.Damage = At(s.Damage, i); c.Corrosion = At(s.Corrosion, i);
            c.Integrity = At(s.Integrity, i, 1.0); c.Broken = c.Integrity <= 0.0;
            c.StressMPa = At(s.Stress, i);
        }
        foreach (var kv in s.Species)
            for (int i = 0; i < Grid.Count && i < kv.Value.Length; i++)
                Species.At(i)[kv.Key] = kv.Value[i];
        Events.Clear();
        for (int i = 0; i < s.EventKind.Length; i++)
            Events.Add(new RealityEvent(
                i < s.EventTime.Length ? s.EventTime[i] : 0.0,
                s.EventKind[i],
                i < s.EventCell.Length ? s.EventCell[i] : -1,
                i < s.EventDetail.Length ? s.EventDetail[i] : "",
                i < s.EventValue.Length ? s.EventValue[i] : 0.0));
        InitialEnergy = Grid.TotalEnergy() - Grid.EnergyIn + Grid.EnergyOut;
    }

    private static void Copy(double[] src, double[] dst)
    { for (int i = 0; i < dst.Length && i < src.Length; i++) dst[i] = src[i]; }
    private static double At(double[] a, int i, double d = 0.0) => i < a.Length ? a[i] : d;

    public static bool VerifyDeterminism(string snapshot, double dt, int steps)
        => VerifyDeterminism(snapshot, dt, steps, null);

    /// <summary>带配置回调的重放：启用可逆反应 / 密闭容器等可选子系统时用这个重载。</summary>
    public static bool VerifyDeterminism(string snapshot, double dt, int steps,
        Action<RealityWorld>? configure)
    {
        var meta = JsonSerializer.Deserialize<RealitySnapshot>(snapshot);
        int nx = meta?.Nx ?? 8, ny = meta?.Ny ?? 4, nz = meta?.Nz ?? 8;
        double cs = meta?.CellSize ?? 0.25;
        string Run()
        {
            var w = new RealityWorld(nx, ny, nz, cs);
            w.Restore(snapshot);
            configure?.Invoke(w);
            for (int i = 0; i < steps; i++) w.Step(dt);
            return w.Snapshot();
        }
        return string.Equals(Run(), Run(), StringComparison.Ordinal);
    }
}
