// ════════════════════════════════════════════════════════════════════════
// RealityThermodynamics.cs — 格点热场（传导/辐射/相变/潜热）+ 燃烧传播
// 内部传导用面通量对称累加：净交换恒为 0，能量账本可逐 joule 核算。
// 显式格式自动按 CFL 条件拆子步，避免超步长导致非物理振荡。
// ════════════════════════════════════════════════════════════════════════
using System.Numerics;

namespace YamaEngine.Physics.Reality;

public sealed class ThermalGrid
{
    public readonly int Nx, Ny, Nz;
    public readonly double CellSize;
    public readonly double[] T;               // K
    public readonly double[] MassKg;
    public readonly double[] Cp;
    public readonly double[] Conductivity;
    public readonly double[] FuelKg;
    public readonly double[] LatentEnergy;   // 已吸收的潜热（J），计入总能量
    public readonly double[] LatentCapacity; // 该格相变总需求（J）
    public readonly string[] Substance;
    public readonly ChemSubstanceTable Table;

    public double EnergyIn;
    public double EnergyOut;
    public double AmbientK = ChemUnits.AmbientK;
    public double Emissivity = 0.85;
    public double MaxCflFraction = 0.20;
    public int LastSubsteps;

    public ThermalGrid(int nx, int ny, int nz, double cellSize, ChemSubstanceTable table,
        string defaultSubstance = "Air", double defaultKelvin = 293.15)
    {
        Nx = Math.Max(1, nx); Ny = Math.Max(1, ny); Nz = Math.Max(1, nz);
        CellSize = Math.Max(1e-3, cellSize); Table = table;
        int n = Nx * Ny * Nz;
        T = new double[n]; MassKg = new double[n]; Cp = new double[n];
        Conductivity = new double[n]; FuelKg = new double[n];
        LatentEnergy = new double[n]; LatentCapacity = new double[n]; Substance = new string[n];
        for (int i = 0; i < n; i++) SetCell(i, defaultSubstance, defaultKelvin);
    }

    public int Count => T.Length;
    public int Index(int x, int y, int z) => (z * Ny + y) * Nx + x;
    public (int x, int y, int z) Coords(int i)
        => (i % Nx, (i / Nx) % Ny, i / (Nx * Ny));

    public void SetCell(int i, string substanceName, double kelvin)
    {
        var s = Table.Get(substanceName);
        Substance[i] = substanceName;
        T[i] = kelvin;
        MassKg[i] = s.DensityKgM3 * Math.Pow(CellSize, 3.0);
        Cp[i] = s.SpecificHeatJKgK;
        Conductivity[i] = s.ConductivityWMK;
        FuelKg[i] = s.IsFuel ? MassKg[i] : 0.0;
        LatentCapacity[i] = Math.Max(1.0, MassKg[i] * Math.Max(s.LatentFusionKJKg, s.LatentVaporKJKg) * 1000.0);
        LatentEnergy[i] = 0.0;
    }

    /// <summary>恢复快照后按材料名重算物性，不改动温度/燃料/潜热状态。</summary>
    public void RefreshMaterial(int i)
    {
        var s = Table.Get(Substance[i]);
        MassKg[i] = s.DensityKgM3 * Math.Pow(CellSize, 3.0);
        Cp[i] = s.SpecificHeatJKgK;
        Conductivity[i] = s.ConductivityWMK;
        LatentCapacity[i] = Math.Max(1.0, MassKg[i] * Math.Max(s.LatentFusionKJKg, s.LatentVaporKJKg) * 1000.0);
    }

    public double SensibleEnergy(int i) => MassKg[i] * Cp[i] * T[i];
    public double TotalEnergy()
    {
        double e = 0.0;
        for (int i = 0; i < Count; i++) e += SensibleEnergy(i) + LatentEnergy[i];
        return e;
    }
    public void AddHeat(int i, double joules)
    {
        if (joules > 0.0) EnergyIn += joules; else EnergyOut += -joules;
        double cap = Math.Max(1e-9, MassKg[i] * Cp[i]);
        ApplyTemperatureDelta(i, joules / cap);
    }

    /// <summary>显热→潜热的相变平台：温度钉在相点，直到潜热预算耗尽。</summary>
    private void ApplyTemperatureDelta(int i, double deltaK)
    {
        var s = Table.Get(Substance[i]);
        double target = T[i] + deltaK;
        double phaseK = s.BoilingK > s.MeltingK ? s.BoilingK : s.MeltingK;
        if (phaseK > 0.0 && target > phaseK && T[i] <= phaseK && LatentEnergy[i] < LatentCapacity[i])
        {
            double cap = Math.Max(1e-9, MassKg[i] * Cp[i]);
            double excess = (target - phaseK) * cap;
            double room = LatentCapacity[i] - LatentEnergy[i];
            double used = Math.Min(excess, room);
            LatentEnergy[i] += used;
            T[i] = phaseK;
            if (used < excess) T[i] += (excess - used) / cap;
            return;
        }
        T[i] = Math.Clamp(target, 0.0, 20000.0);
    }

    /// <summary>按 CFL 自动拆子步的显式传导；边界按辐射/对流对外记账。</summary>
    public void Step(double dt, List<RealityEvent> events, double time)
    {
        if (dt <= 0.0) return;
        double h = CellSize;
        double alphaMax = 0.0;
        for (int i = 0; i < Count; i++)
            alphaMax = Math.Max(alphaMax, Conductivity[i] / Math.Max(1e-9, MassKg[i] * Cp[i] / (h * h * h)));
        double stable = alphaMax > 0.0
            ? MaxCflFraction * CellSize * CellSize / (6.0 * alphaMax) : dt;
        int sub = Math.Clamp((int)Math.Ceiling(dt / Math.Max(1e-9, stable)), 1, 64);
        LastSubsteps = sub;
        double sdt = dt / sub;
        double area = h * h;
        for (int step = 0; step < sub; step++)
        {
            var delta = new double[Count];
            for (int i = 0; i < Count; i++)
            {
                var (x, y, z) = Coords(i);
                AccumulateFace(delta, i, x - 1, y, z, area, sdt);
                AccumulateFace(delta, i, x + 1, y, z, area, sdt);
                AccumulateFace(delta, i, x, y - 1, z, area, sdt);
                AccumulateFace(delta, i, x, y + 1, z, area, sdt);
                AccumulateFace(delta, i, x, y, z - 1, area, sdt);
                AccumulateFace(delta, i, x, y, z + 1, area, sdt);
            }
            for (int i = 0; i < Count; i++)
            {
                // 边界辐射（只在最外层近似计入，避免每面重复计账）
                var (x, y, z) = Coords(i);
                bool boundary = x == 0 || x == Nx - 1 || y == 0 || y == Ny - 1 || z == 0 || z == Nz - 1;
                if (boundary)
                {
                    double t4 = Math.Pow(Math.Max(0.0, T[i]), 4.0) - Math.Pow(AmbientK, 4.0);
                    double rad = Emissivity * ChemUnits.StefanBoltzmann * area * t4 * sdt;
                    delta[i] -= rad;
                    EnergyOut += Math.Max(0.0, rad);
                }
                if (Math.Abs(delta[i]) > 1e-12)
                    ApplyTemperatureDelta(i, delta[i] / Math.Max(1e-9, MassKg[i] * Cp[i]));
            }
        }
    }

    private void AccumulateFace(double[] delta, int i, int x, int y, int z, double area, double dt)
    {
        if (x < 0 || x >= Nx || y < 0 || y >= Ny || z < 0 || z >= Nz) return;
        int j = Index(x, y, z);
        double kFace = 2.0 * Conductivity[i] * Conductivity[j]
                     / Math.Max(1e-9, Conductivity[i] + Conductivity[j]);
        double q = kFace * area / CellSize * (T[j] - T[i]) * dt;
        delta[i] += q;     // 对称累加由两侧各自计算，净和恒为 0
    }

    /// <summary>能量账本：|(E0 + 输入 - 输出) - E1| / 尺度。</summary>
    public double EnergyBalanceError(double initialEnergy)
    {
        double expected = initialEnergy + EnergyIn - EnergyOut;
        return RealityAssert.RelativeError(expected, TotalEnergy());
    }
}

/// <summary>燃烧三要素：燃料 + 氧气 + 温度；放热、产物、熄灭与邻格蔓延。</summary>
public sealed class CombustionModel
{
    public double MinO2Fraction = 0.10;
    public double BurnRateKgPerS = 0.02;
    public double OxygenPerKgFuel = 3.0;       // kg O2 / kg fuel（工程近似）
    public double SpreadBoostK = 60.0;
    public long IgnitionCount;
    public long ExtinctionCount;

    /// <param name="molesAt">返回该格组分摩尔字典的回调（由 SpeciesField 提供）。</param>
    public double Step(ThermalGrid grid, Func<int, Dictionary<string, double>> molesAt,
        double dt, Vector3 wind, List<RealityEvent> events, double time)
    {
        double totalHeat = 0.0;
        var ignite = new List<int>();
        for (int i = 0; i < grid.Count; i++)
        {
            var s = grid.Table.Get(grid.Substance[i]);
            var moles = molesAt(i);
            double o2 = moles.GetValueOrDefault("O2");
            double total = Math.Max(1e-9, moles.Values.Sum());
            double o2Fraction = o2 / total;
            bool hasFuel = grid.FuelKg[i] > 1e-6;
            bool hot = grid.T[i] >= s.AutoignitionK;
            if (!hasFuel || !s.IsFuel) continue;
            if (!hot || o2Fraction < MinO2Fraction) continue;

            double dm = Math.Min(grid.FuelKg[i], BurnRateKgPerS * dt * (0.3 + 0.7 * o2Fraction));
            double o2NeedKg = dm * OxygenPerKgFuel;
            double o2AvailableKg = o2 * 0.032;
            double scale = o2AvailableKg <= 0.0 ? 0.0 : Math.Min(1.0, o2AvailableKg / Math.Max(1e-9, o2NeedKg));
            dm *= scale;
            if (dm <= 1e-9) continue;

            grid.FuelKg[i] -= dm;
            double heat = dm * s.HeatOfCombustionMJKg * 1e6;
            grid.AddHeat(i, heat);
            totalHeat += heat;
            double o2UsedMol = dm * OxygenPerKgFuel / 0.032;
            moles["O2"] = Math.Max(0.0, o2 - o2UsedMol);
            moles["CO2"] = moles.GetValueOrDefault("CO2") + dm * 0.075;
            moles["H2O"] = moles.GetValueOrDefault("H2O") + dm * 0.055;
            moles["Smoke"] = moles.GetValueOrDefault("Smoke") + dm * 0.010;
            if (grid.FuelKg[i] <= 1e-6)
            {
                ExtinctionCount++;
                events.Add(new RealityEvent(time, "extinction", i, "fuel_exhausted", 0.0));
            }
            var (x, y, z) = grid.Coords(i);
            int wx = (int)Math.Round(wind.X), wz = (int)Math.Round(wind.Z);
            TryIgnite(grid, x + wx, y, z, ignite);
            TryIgnite(grid, x + 1, y, z, ignite);
            TryIgnite(grid, x - 1, y, z, ignite);
            TryIgnite(grid, x, y, z + 1, ignite);
            TryIgnite(grid, x, y, z - 1, ignite);
        }
        foreach (int j in ignite.Distinct())
        {
            var s = grid.Table.Get(grid.Substance[j]);
            if (grid.FuelKg[j] > 1e-6 && grid.T[j] < s.AutoignitionK)
            {
                grid.AddHeat(j, SpreadBoostK * Math.Max(1e-9, grid.MassKg[j] * grid.Cp[j]));
                IgnitionCount++;
                events.Add(new RealityEvent(time, "ignition", j, "spread", grid.T[j]));
            }
        }
        return totalHeat;
    }

    private static void TryIgnite(ThermalGrid g, int x, int y, int z, List<int> targets)
    {
        if (x < 0 || x >= g.Nx || y < 0 || y >= g.Ny || z < 0 || z >= g.Nz) return;
        targets.Add(g.Index(x, y, z));
    }
}
