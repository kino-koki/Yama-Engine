// ════════════════════════════════════════════════════════════════════════
// RealityGas.cs — Round 6：气体状态方程 / 密闭超压 / 泄压爆裂 / 可逆反应
// 补的是「有热无压」的缺口：燃烧升温在密闭空间应升压，超压应爆裂并推动刚体。
// 泄压只把摩尔搬到邻格，总摩尔严格守恒；冲量只经 IRealityBodySink 发出。
// ════════════════════════════════════════════════════════════════════════
using System.Numerics;

namespace YamaEngine.Physics.Reality;

/// <summary>理想气体 PV=nRT 与派生量，全部为纯函数、显式单位。</summary>
public static class IdealGas
{
    /// <param name="moles">总摩尔 mol</param>
    /// <param name="kelvin">温度 K</param>
    /// <param name="volumeM3">体积 m³</param>
    /// <returns>压力 Pa</returns>
    public static double PressurePa(double moles, double kelvin, double volumeM3)
        => moles * ChemUnits.GasConstant * Math.Max(0.0, kelvin) / Math.Max(1e-9, volumeM3);

    public static double Moles(double pressurePa, double kelvin, double volumeM3)
        => pressurePa * Math.Max(1e-9, volumeM3) / (ChemUnits.GasConstant * Math.Max(1e-9, kelvin));

    public static double VolumeM3(double moles, double kelvin, double pressurePa)
        => moles * ChemUnits.GasConstant * Math.Max(0.0, kelvin) / Math.Max(1e-9, pressurePa);

    /// <summary>定容升温后的压力比（盖-吕萨克定律），用于快速判定。</summary>
    public static double PressureRatio(double t0K, double t1K)
        => Math.Max(0.0, t1K) / Math.Max(1e-9, t0K);

    /// <summary>标准状态下 1 m³ 空气摩尔数（ sanity check 用）。</summary>
    public static double StandardAirMolesPerM3
        => 1.0 / ChemUnits.StandardMolarVolumeM3;
}

public sealed class PressureVessel
{
    public sealed class Cell
    {
        public bool Sealed;
        public double BurstPressurePa = 5.0 * ChemUnits.AtmospherePa;
        public bool Burst;
    }

    private readonly Cell[] _cells;
    public IReadOnlyList<Cell> Cells => _cells;
    public long BurstCount { get; private set; }
    public double MaxObservedPressurePa { get; private set; }
    public double ImpulseScale { get; set; } = 0.02;

    public PressureVessel(int cellCount)
    { _cells = new Cell[cellCount]; for (int i = 0; i < cellCount; i++) _cells[i] = new Cell(); }

    public void SetSealed(int cell, bool sealedCell = true, double burstPa = 5.0 * ChemUnits.AtmospherePa)
    {
        if (cell < 0 || cell >= _cells.Length) return;
        _cells[cell].Sealed = sealedCell;
        _cells[cell].BurstPressurePa = burstPa;
    }

    public double PressureAt(int cell, SpeciesField species, double kelvin, double cellVolumeM3)
        => IdealGas.PressurePa(species.At(cell).Values.Sum(), kelvin, cellVolumeM3);

    /// <summary>
    /// 检测超压；爆裂时把该格气体按邻格均分（总摩尔守恒），并给刚体发冲量。
    /// 返回本次爆裂的格数。
    /// </summary>
    public int Step(ThermalGrid grid, SpeciesField species,
        IRealityBodySink? sink, List<RealityEvent> events, double time)
    {
        int bursts = 0;
        double vol = Math.Pow(grid.CellSize, 3.0);
        for (int i = 0; i < grid.Count && i < _cells.Length; i++)
        {
            var c = _cells[i];
            double p = PressureAt(i, species, grid.T[i], vol);
            MaxObservedPressurePa = Math.Max(MaxObservedPressurePa, p);
            if (!c.Sealed || c.Burst || p <= c.BurstPressurePa) continue;
            c.Burst = true; c.Sealed = false; BurstCount++; bursts++;
            events.Add(new RealityEvent(time, "burst", i, "overpressure", p));
            var (x, y, z) = grid.Coords(i);
            var neighbours = new List<int>();
            if (x > 0) neighbours.Add(grid.Index(x - 1, y, z));
            if (x < grid.Nx - 1) neighbours.Add(grid.Index(x + 1, y, z));
            if (y > 0) neighbours.Add(grid.Index(x, y - 1, z));
            if (y < grid.Ny - 1) neighbours.Add(grid.Index(x, y + 1, z));
            if (z > 0) neighbours.Add(grid.Index(x, y, z - 1));
            if (z < grid.Nz - 1) neighbours.Add(grid.Index(x, y, z + 1));
            if (neighbours.Count > 0)
            {
                var bag = species.At(i);
                var keys = bag.Keys.ToArray();
                foreach (var k in keys)
                {
                    double total = bag.GetValueOrDefault(k);
                    if (total <= 0.0) continue;
                    double share = total / (neighbours.Count + 1);
                    bag[k] = share;                       // 自身保留一份
                    foreach (int j in neighbours)
                        species.At(j)[k] = species.At(j).GetValueOrDefault(k) + share;
                }
            }
            float impulse = (float)(p * ImpulseScale * vol);
            var dir = new Vector3((float)(x - grid.Nx * 0.5), 0.2f, (float)(z - grid.Nz * 0.5));
            sink?.ApplyImpulse(i, Vector3.Normalize(dir.LengthSquared() > 1e-4f ? dir : Vector3.UnitX)
                * impulse, Vector3.Zero);
        }
        return bursts;
    }
}

/// <summary>可逆反应：van 't Hoff 平衡常数 + 正逆净速率，反应进度双向限位。</summary>
public sealed class ReversibleReaction
{
    public ChemEquation Equation = ChemEquation.Parse("H2 -> H2");
    public double ForwardPreExponential = 1.0;
    public double ActivationEnergyJPerMol = 50000.0;
    public double StandardEnthalpyKJPerMol = -50.0;   // van 't Hoff d(lnK)/dT
    public double ReferenceK = 1.0;                    // T0 下的平衡常数
    public double ReferenceTemperatureK = 298.15;
    public double EnthalpyKJPerMol = 0.0;
    public long ForwardSteps;
    public long ReverseSteps;

    public double EquilibriumK(double kelvin)
    {
        double dh = StandardEnthalpyKJPerMol * 1000.0;
        double exponent = -dh / ChemUnits.GasConstant
                        * (1.0 / Math.Max(1e-9, kelvin) - 1.0 / ReferenceTemperatureK);
        return Math.Clamp(ReferenceK * Math.Exp(Math.Clamp(exponent, -50.0, 50.0)), 1e-12, 1e12);
    }

    private static double Product(IReadOnlyList<ChemTerm> terms, IReadOnlyDictionary<string, double> moles,
        double volume)
    {
        double v = 1.0;
        foreach (var t in terms)
            v *= Math.Pow(Math.Max(0.0, moles.GetValueOrDefault(t.Formula)) / Math.Max(1e-6, volume),
                          Math.Min(t.Coefficient, 3.0));
        return v;
    }

    /// <summary>推进一步；正向 extent>0、逆向 extent&lt;0，返回释放热量（J，正=放热）。</summary>
    public double Step(double kelvin, double dt, Dictionary<string, double> moles,
        double volume, List<RealityEvent> events, double time, int cell)
    {
        if (!Equation.IsBalanced || kelvin <= 0.0 || dt <= 0.0) return 0.0;
        double kf = ForwardPreExponential
                  * Math.Exp(-ActivationEnergyJPerMol / (ChemUnits.GasConstant * kelvin));
        double kr = kf / Math.Max(1e-12, EquilibriumK(kelvin));
        double fwdRate = kf * Product(Equation.Reactants, moles, volume);
        double revRate = kr * Product(Equation.Products, moles, volume);
        double net = (fwdRate - revRate) * dt;
        if (Math.Abs(net) < 1e-12) return 0.0;

        double extent;
        if (net > 0.0)
        {
            extent = Math.Min(net, ChemEquation.LimitingExtent(Equation.Reactants, moles));
            if (extent <= 0.0) return 0.0;
            ForwardSteps++;
        }
        else
        {
            extent = -Math.Min(-net, ChemEquation.LimitingExtent(Equation.Products, moles));
            if (extent >= 0.0) return 0.0;
            ReverseSteps++;
        }
        foreach (var t in Equation.Reactants)
            moles[t.Formula] = Math.Max(0.0, moles.GetValueOrDefault(t.Formula) - t.Coefficient * extent);
        foreach (var t in Equation.Products)
            moles[t.Formula] = Math.Max(0.0, moles.GetValueOrDefault(t.Formula) + t.Coefficient * extent);
        double heat = -EnthalpyKJPerMol * 1000.0 * extent;
        events.Add(new RealityEvent(time, "reaction", cell, Equation.Text + (extent > 0 ? "→" : "←"), heat));
        return heat;
    }
}
