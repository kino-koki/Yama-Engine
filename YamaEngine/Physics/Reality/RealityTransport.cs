// ════════════════════════════════════════════════════════════════════════
// RealityTransport.cs — 多组分 advection-diffusion + 材料疲劳/热软化/腐蚀/断裂
// 输运采用「面上净通量」写法：i→j 与 j→i 共用同一个 F，摩尔数严格守恒。
// 破坏为 CPU 级事件，只通过 IRealityBodySink 通知既有刚体系统，不碰渲染。
// ════════════════════════════════════════════════════════════════════════
using System.Numerics;

namespace YamaEngine.Physics.Reality;

public sealed class SpeciesField
{
    private readonly Dictionary<string, double>[] _cells;
    public readonly int Count;
    public bool ClampFlux = true;
    public double LastExchange;   // 本步面通量绝对值之和（监控用，非质量损失）

    public SpeciesField(int cellCount, IEnumerable<string> speciesNames,
        double initialMolesPerSpecies = 0.0)
    {
        Count = Math.Max(1, cellCount);
        _cells = new Dictionary<string, double>[Count];
        var names = speciesNames.ToArray();
        for (int i = 0; i < Count; i++)
        {
            _cells[i] = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var n in names) _cells[i][n] = initialMolesPerSpecies;
        }
    }

    public Dictionary<string, double> At(int i) => _cells[i];
    public double Total(string species)
    {
        double s = 0.0;
        for (int i = 0; i < Count; i++) s += _cells[i].GetValueOrDefault(species);
        return s;
    }
    public double TotalAll()
    {
        double s = 0.0;
        for (int i = 0; i < Count; i++) foreach (var v in _cells[i].Values) s += v;
        return s;
    }
    public Dictionary<string, double> SnapshotTotals()
        => _cells[0].Keys.ToDictionary(k => k, k => Total(k), StringComparer.Ordinal);

    public void Add(int cell, string species, double moles)
    {
        if (cell < 0 || cell >= Count) return;
        _cells[cell][species] = Math.Max(0.0, _cells[cell].GetValueOrDefault(species) + moles);
    }

    /// <summary>封闭边界固定步长输运；返回本步面通量绝对值之和，摩尔数严格守恒。</summary>
    public double Step(double dt, double cellSize, Vector3 wind,
        Func<int, double> diffusivityAt, int nx, int ny, int nz, out double maxFluxRatio)
    {
        maxFluxRatio = 0.0;
        LastExchange = 0.0;
        if (dt <= 0.0 || Count == 0) return 0.0;
        double h = Math.Max(1e-3, cellSize);
        var species = _cells[0].Keys.ToArray();
        var delta = new Dictionary<string, double>[Count];
        for (int i = 0; i < Count; i++) delta[i] = new Dictionary<string, double>(StringComparer.Ordinal);

        var dirs = new (int dx, int dy, int dz, double u)[]
        { (1, 0, 0, wind.X), (0, 1, 0, wind.Y), (0, 0, 1, wind.Z) };

        foreach (var sp in species)
        {
            foreach (var (dx, dy, dz, u) in dirs)
            {
                for (int z = 0; z < nz; z++)
                    for (int y = 0; y < ny; y++)
                        for (int x = 0; x < nx; x++)
                        {
                            int i = (z * ny + y) * nx + x;
                            int xx = x + dx, yy = y + dy, zz = z + dz;
                            if (xx >= nx || yy >= ny || zz >= nz || i >= Count) continue;
                            int j = (zz * ny + yy) * nx + xx;
                            if (j >= Count) continue;
                            double ci = _cells[i].GetValueOrDefault(sp);
                            double cj = _cells[j].GetValueOrDefault(sp);
                            double d = Math.Max(0.0, diffusivityAt(i));
                            double adv = u * dt / h * (u >= 0.0 ? ci : cj);
                            double diff = d * dt / (h * h) * (ci - cj);
                            double f = adv + diff;
                            if (ClampFlux)
                            {
                                double cap = 0.25 * (ci + cj);
                                f = Math.Clamp(f, -cap, cap);
                            }
                            maxFluxRatio = Math.Max(maxFluxRatio,
                                Math.Abs(f) / Math.Max(1e-9, ci + cj));
                            LastExchange += Math.Abs(f);
                            delta[i][sp] = delta[i].GetValueOrDefault(sp) - f;
                            delta[j][sp] = delta[j].GetValueOrDefault(sp) + f;
                        }
            }
        }

        for (int i = 0; i < Count; i++)
            foreach (var kv in delta[i])
                _cells[i][kv.Key] = Math.Max(0.0, _cells[i].GetValueOrDefault(kv.Key) + kv.Value);
        return LastExchange;
    }

    public double BalanceError(IReadOnlyDictionary<string, double> before)
    {
        double worst = 0.0;
        foreach (var kv in before)
            worst = Math.Max(worst, RealityAssert.RelativeError(kv.Value, Total(kv.Key)));
        return worst;
    }
}

public sealed class DegradationCell
{
    public double StressMPa;
    public double Damage;
    public double Corrosion;
    public double Integrity = 1.0;
    public bool Broken;
}

public sealed class MaterialDegradation
{
    public double MinerExponent = 2.0;
    public double FatigueRatePerS = 0.01;
    public double ThermalSofteningK = 2000.0;
    public double CorrosionScale = 1.0;
    public long FractureCount;

    private readonly DegradationCell[] _cells;
    public IReadOnlyList<DegradationCell> Cells => _cells;

    public MaterialDegradation(int cellCount) { _cells = new DegradationCell[cellCount];
        for (int i = 0; i < cellCount; i++) _cells[i] = new DegradationCell(); }

    public void SetStress(int cell, double mpa) { if (cell >= 0 && cell < _cells.Length) _cells[cell].StressMPa = mpa; }

    public void Step(ThermalGrid grid, SpeciesField species, double dt,
        IRealityBodySink? sink, List<RealityEvent> events, double time)
    {
        for (int i = 0; i < _cells.Length && i < grid.Count; i++)
        {
            var c = _cells[i];
            if (c.Broken) continue;
            var sub = grid.Table.Get(grid.Substance[i]);
            double baseYield = Math.Max(1.0, sub.YieldStrengthMPa);
            double softening = Math.Clamp(1.0 - Math.Max(0.0, grid.T[i] - ChemUnits.AmbientK) / ThermalSofteningK, 0.1, 1.0);
            double yield = baseYield * softening;
            double ratio = Math.Max(0.0, c.StressMPa) / yield;
            c.Damage += Math.Pow(ratio, MinerExponent) * FatigueRatePerS * dt;

            double acid = species.At(i).GetValueOrDefault("CO2") + species.At(i).GetValueOrDefault("H2O") * 0.1;
            c.Corrosion += sub.CorrosionRatePerMol * acid * dt * CorrosionScale;

            // 火焰直接削弱结构
            if (grid.FuelKg[i] <= 1e-6 && grid.T[i] > sub.MeltingK * 0.6)
                c.Damage += 0.002 * dt;

            c.Integrity = Math.Clamp(1.0 - c.Damage - c.Corrosion, 0.0, 1.0);
            if (c.Integrity <= 0.0)
            {
                c.Broken = true; FractureCount++;
                int fragments = Math.Clamp((int)(1 + Math.Sqrt(Math.Max(1.0, ratio)) * 4), 1, 64);
                events.Add(new RealityEvent(time, "fracture", i, sub.Name, fragments));
                sink?.MarkBroken(i, fragments);
                sink?.ApplyImpulse(i, new Vector3(0f, (float)(ratio * 10.0), 0f), Vector3.Zero);
            }
        }
    }
}
