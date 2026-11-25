// ════════════════════════════════════════════════════════════════════════
// RealityActiveSet.cs — Round 6：热膨胀附加应力 / 局部激活 / 快照 schema
// 活跃集是「性能近似」而非物理近似：默认关闭，显式启用时跳过静止格的非必要计算；
// 热传导仍全量推进，因此能量/质量/元素守恒不受影响。
// ════════════════════════════════════════════════════════════════════════
using System.Globalization;

namespace YamaEngine.Physics.Reality;

/// <summary>完全约束下的热膨胀应力 σ = E·α·ΔT（MPa）。纯函数，可单元测试。</summary>
public sealed class ThermalExpansion
{
    public double ElasticModulusMPa = 200000.0;   // 钢量级
    public double LinearExpansionPerK = 12e-6;
    public double MaxStressMPa = 1e6;

    public double StressMPa(double deltaK, ChemSubstance? substance = null)
    {
        double alpha = substance is null ? LinearExpansionPerK : InferAlpha(substance);
        double e = substance is null ? ElasticModulusMPa : InferModulus(substance);
        return Math.Min(MaxStressMPa, Math.Abs(e * alpha * deltaK));
    }

    /// <summary>把热应力叠加到既有结构单元上（温度高于环境才产生压应力）。</summary>
    public double ApplyTo(MaterialDegradation degradation, ThermalGrid grid, int cell)
    {
        double delta = grid.T[cell] - ChemUnits.AmbientK;
        if (delta <= 0.0 || cell >= degradation.Cells.Count) return 0.0;
        double s = StressMPa(delta, grid.Table.Get(grid.Substance[cell]));
        degradation.SetStress(cell, degradation.Cells[cell].StressMPa + s);
        return s;
    }

    private double InferAlpha(ChemSubstance s) => s.Name switch
    {
        "Iron" or "Steel" => 12e-6,
        "Concrete" => 10e-6,
        "Wood" => 5e-6,
        "Water" => 2.1e-4,
        _ => LinearExpansionPerK
    };
    private double InferModulus(ChemSubstance s) => s.Name switch
    {
        "Iron" or "Steel" => 200000.0,
        "Concrete" => 30000.0,
        "Wood" => 10000.0,
        _ => ElasticModulusMPa * 0.01
    };
}

/// <summary>活跃格集合：温度/燃料/事件变化超过阈值的格子才需要重算。</summary>
public sealed class ActiveCellSet
{
    private readonly HashSet<int> _active = new();
    private double[] _lastT = Array.Empty<double>();
    private double[] _lastFuel = Array.Empty<double>();

    public double TemperatureEpsilonK { get; set; } = 0.05;
    public double FuelEpsilonKg { get; set; } = 1e-6;
    public long TotalActivations { get; private set; }
    public int Count => _active.Count;
    public IReadOnlyCollection<int> Active => _active;
    public bool Enabled { get; set; }

    public void Initialize(ThermalGrid grid)
    {
        _lastT = (double[])grid.T.Clone();
        _lastFuel = (double[])grid.FuelKg.Clone();
        _active.Clear();
        for (int i = 0; i < grid.Count; i++) _active.Add(i);
    }

    /// <summary>比对上一帧状态，刷新活跃集；返回活跃格数。</summary>
    public int Refresh(ThermalGrid grid, IReadOnlyList<RealityEvent> recentEvents)
    {
        if (_lastT.Length != grid.Count) Initialize(grid);
        _active.Clear();
        for (int i = 0; i < grid.Count; i++)
        {
            bool changed = Math.Abs(grid.T[i] - _lastT[i]) > TemperatureEpsilonK
                        || Math.Abs(grid.FuelKg[i] - _lastFuel[i]) > FuelEpsilonKg
                        || grid.FuelKg[i] > 1e-6;
            if (changed) _active.Add(i);
        }
        foreach (var e in recentEvents)
            if (e.Cell >= 0 && e.Cell < grid.Count) _active.Add(e.Cell);
        for (int i = 0; i < grid.Count; i++)
        { _lastT[i] = grid.T[i]; _lastFuel[i] = grid.FuelKg[i]; }
        TotalActivations += _active.Count;
        return _active.Count;
    }

    public bool ShouldCompute(int cell) => !Enabled || _active.Contains(cell);

    public void Capture(IDictionary<string, string> d, string p)
    {
        d[p + ".n"] = _active.Count.ToString(CultureInfo.InvariantCulture);
        int i = 0;
        foreach (var c in _active.OrderBy(x => x))
            d[p + ".c" + i++] = c.ToString(CultureInfo.InvariantCulture);
        d[p + ".total"] = TotalActivations.ToString(CultureInfo.InvariantCulture);
        d[p + ".enabled"] = Enabled ? "1" : "0";
        d[p + ".nt"] = _lastT.Length.ToString(CultureInfo.InvariantCulture);
        for (int j = 0; j < _lastT.Length; j++)
            d[p + ".t" + j] = _lastT[j].ToString("R", CultureInfo.InvariantCulture);
        for (int j = 0; j < _lastFuel.Length; j++)
            d[p + ".f" + j] = _lastFuel[j].ToString("R", CultureInfo.InvariantCulture);
    }

    public void Restore(IReadOnlyDictionary<string, string> d, string p)
    {
        _active.Clear();
        int n = GetInt(d, p + ".n");
        for (int i = 0; i < n; i++) _active.Add(GetInt(d, p + ".c" + i));
        TotalActivations = GetLong(d, p + ".total");
        Enabled = d.GetValueOrDefault(p + ".enabled") == "1";
        int nt = GetInt(d, p + ".nt");
        if (nt > 0)
        {
            _lastT = new double[nt]; _lastFuel = new double[nt];
            for (int j = 0; j < nt; j++)
            {
                _lastT[j] = GetDouble(d, p + ".t" + j);
                _lastFuel[j] = GetDouble(d, p + ".f" + j);
            }
        }
    }

    private static int GetInt(IReadOnlyDictionary<string, string> d, string k)
        => int.TryParse(d.GetValueOrDefault(k), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var v) ? v : 0;
    private static long GetLong(IReadOnlyDictionary<string, string> d, string k)
        => long.TryParse(d.GetValueOrDefault(k), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var v) ? v : 0L;
    private static double GetDouble(IReadOnlyDictionary<string, string> d, string k)
        => double.TryParse(d.GetValueOrDefault(k), NumberStyles.Float,
            CultureInfo.InvariantCulture, out var v) ? v : 0.0;
}

/// <summary>快照 schema 版本：Restore 时拒绝未来版本，避免静默错误解码。</summary>
public static class RealitySchema
{
    public const int Current = 2;
    public const int LegacyUnversioned = 1;
    public static bool IsSupported(int version) => version >= 1 && version <= Current;
    public static void Validate(int version)
    {
        if (!IsSupported(version))
            throw new InvalidOperationException(
                $"不支持的 RealitySnapshot schema 版本 {version}（当前 {Current}）。");
    }
}
