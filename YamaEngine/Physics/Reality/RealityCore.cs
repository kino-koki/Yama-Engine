// ════════════════════════════════════════════════════════════════════════
// RealityCore.cs — 物理化学核心：物质属性表 / 单位 / 守恒断言 / 随机源 / 宿主接口
// 设计立场：CPU 级、固定步长、质量与能量守恒可核算；不追求视觉拟真，不碰 GPU/渲染。
// 目录位于 Physics/Reality/，由 csproj 的 <Compile Include="Physics\**\*.cs" /> 自动纳入。
// ════════════════════════════════════════════════════════════════════════
using System.Numerics;

namespace YamaEngine.Physics.Reality;

public enum ChemPhase { Solid, Liquid, Gas, Plasma }

public sealed class ChemSubstance
{
    public string Name = "";
    public string Formula = "";
    public double MolarMassGPerMol = 18.0;      // g/mol
    public double DensityKgM3 = 1000.0;
    public double SpecificHeatJKgK = 4186.0;
    public double ConductivityWMK = 0.6;
    public double MeltingK = 273.15;
    public double BoilingK = 373.15;
    public double LatentFusionKJKg = 334.0;
    public double LatentVaporKJKg = 2260.0;
    public double AutoignitionK = 0.0;          // 0 = 不可燃
    public double HeatOfCombustionMJKg = 0.0;
    public double DiffusivityM2S = 1e-5;
    public double YieldStrengthMPa = 0.0;
    public double CorrosionRatePerMol = 0.0;
    public bool IsFuel => AutoignitionK > 0.0 && HeatOfCombustionMJKg > 0.0;

    public ChemPhase PhaseAt(double kelvin)
        => kelvin < MeltingK ? ChemPhase.Solid
         : kelvin < BoilingK ? ChemPhase.Liquid : ChemPhase.Gas;
}

/// <summary>内置物质库：数值为工程近似量级，用于守恒与耦合验证，可整体替换。</summary>
public sealed class ChemSubstanceTable
{
    private readonly Dictionary<string, ChemSubstance> _items = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, ChemSubstance> Items => _items;

    public ChemSubstanceTable(bool withDefaults = true)
    {
        if (withDefaults) foreach (var s in Defaults()) Register(s);
    }
    public void Register(ChemSubstance s) => _items[s.Name] = s;
    public ChemSubstance Get(string name) => _items.TryGetValue(name, out var s) ? s : _items["Air"];
    public bool TryGet(string name, out ChemSubstance s)
    {
        if (_items.TryGetValue(name, out var found)) { s = found; return true; }
        s = _items["Air"];
        return false;
    }

    private static IEnumerable<ChemSubstance> Defaults()
    {
        yield return new ChemSubstance { Name = "Water", Formula = "H2O", MolarMassGPerMol = 18.015,
            DensityKgM3 = 1000, SpecificHeatJKgK = 4186, ConductivityWMK = 0.606,
            MeltingK = 273.15, BoilingK = 373.15, LatentFusionKJKg = 334, LatentVaporKJKg = 2257,
            DiffusivityM2S = 1.43e-7 };
        yield return new ChemSubstance { Name = "Iron", Formula = "Fe", MolarMassGPerMol = 55.845,
            DensityKgM3 = 7870, SpecificHeatJKgK = 449, ConductivityWMK = 80.2,
            MeltingK = 1811, BoilingK = 3134, LatentFusionKJKg = 247, LatentVaporKJKg = 6090,
            DiffusivityM2S = 2.3e-5, YieldStrengthMPa = 250, CorrosionRatePerMol = 0.002 };
        yield return new ChemSubstance { Name = "Steel", Formula = "Fe", MolarMassGPerMol = 55.85,
            DensityKgM3 = 7850, SpecificHeatJKgK = 490, ConductivityWMK = 50,
            MeltingK = 1700, BoilingK = 3000, LatentFusionKJKg = 270, LatentVaporKJKg = 6100,
            DiffusivityM2S = 1.2e-5, YieldStrengthMPa = 400, CorrosionRatePerMol = 0.001 };
        yield return new ChemSubstance { Name = "Wood", Formula = "C6H10O5", MolarMassGPerMol = 162,
            DensityKgM3 = 700, SpecificHeatJKgK = 1700, ConductivityWMK = 0.12,
            MeltingK = 0, BoilingK = 600, AutoignitionK = 573, HeatOfCombustionMJKg = 18.5,
            DiffusivityM2S = 1e-6, YieldStrengthMPa = 40 };
        yield return new ChemSubstance { Name = "Gasoline", Formula = "C8H18", MolarMassGPerMol = 114,
            DensityKgM3 = 740, SpecificHeatJKgK = 2220, ConductivityWMK = 0.15,
            MeltingK = 200, BoilingK = 350, AutoignitionK = 520, HeatOfCombustionMJKg = 46.0,
            DiffusivityM2S = 1e-5 };
        yield return new ChemSubstance { Name = "Concrete", Formula = "CaO", MolarMassGPerMol = 56,
            DensityKgM3 = 2400, SpecificHeatJKgK = 880, ConductivityWMK = 1.4,
            MeltingK = 1700, BoilingK = 3000, DiffusivityM2S = 1e-9,
            YieldStrengthMPa = 30, CorrosionRatePerMol = 0.0005 };
        yield return new ChemSubstance { Name = "Air", Formula = "N2", MolarMassGPerMol = 28.97,
            DensityKgM3 = 1.225, SpecificHeatJKgK = 1005, ConductivityWMK = 0.026,
            MeltingK = 0, BoilingK = 80, DiffusivityM2S = 2e-5 };
        yield return new ChemSubstance { Name = "O2", Formula = "O2", MolarMassGPerMol = 32,
            DensityKgM3 = 1.429, SpecificHeatJKgK = 918, ConductivityWMK = 0.027,
            MeltingK = 54, BoilingK = 90, DiffusivityM2S = 2e-5 };
        yield return new ChemSubstance { Name = "CO2", Formula = "CO2", MolarMassGPerMol = 44,
            DensityKgM3 = 1.977, SpecificHeatJKgK = 846, ConductivityWMK = 0.017,
            MeltingK = 0, BoilingK = 195, DiffusivityM2S = 1.6e-5 };
        yield return new ChemSubstance { Name = "Methane", Formula = "CH4", MolarMassGPerMol = 16,
            DensityKgM3 = 0.668, SpecificHeatJKgK = 2200, ConductivityWMK = 0.034,
            MeltingK = 90, BoilingK = 111, AutoignitionK = 810, HeatOfCombustionMJKg = 55.5,
            DiffusivityM2S = 2.2e-5 };
        yield return new ChemSubstance { Name = "Hydrogen", Formula = "H2", MolarMassGPerMol = 2.016,
            DensityKgM3 = 0.0899, SpecificHeatJKgK = 14300, ConductivityWMK = 0.18,
            MeltingK = 14, BoilingK = 20, AutoignitionK = 773, HeatOfCombustionMJKg = 142.0,
            DiffusivityM2S = 6e-5 };
        yield return new ChemSubstance { Name = "Ethanol", Formula = "C2H6O", MolarMassGPerMol = 46,
            DensityKgM3 = 789, SpecificHeatJKgK = 2440, ConductivityWMK = 0.17,
            MeltingK = 159, BoilingK = 351, AutoignitionK = 636, HeatOfCombustionMJKg = 29.7,
            DiffusivityM2S = 1.2e-5 };
        yield return new ChemSubstance { Name = "Smoke", Formula = "C", MolarMassGPerMol = 12,
            DensityKgM3 = 1.1, SpecificHeatJKgK = 1100, ConductivityWMK = 0.03,
            MeltingK = 0, BoilingK = 4000, DiffusivityM2S = 3e-5 };
    }
}

public static class ChemUnits
{
    public const double GasConstant = 8.314462618;       // J/(mol·K)
    public const double StefanBoltzmann = 5.670374419e-8; // W/(m²·K⁴)
    public const double KelvinZeroC = 273.15;
    public const double AmbientK = 293.15;
    public const double AtmospherePa = 101325.0;
    public const double StandardMolarVolumeM3 = 0.024465; // m³/mol @ 25°C 1atm
}

public static class RealityAssert
{
    public static bool Near(double a, double b, double relTol = 1e-6, double absTol = 1e-9)
    {
        double diff = Math.Abs(a - b);
        if (diff <= absTol) return true;
        return diff <= relTol * Math.Max(Math.Abs(a), Math.Abs(b));
    }
    public static double RelativeError(double expected, double actual)
        => Math.Abs(expected) < 1e-12 ? Math.Abs(actual) : Math.Abs(actual - expected) / Math.Abs(expected);
}

public readonly struct RealityEvent
{
    public readonly double Time;
    public readonly string Kind;     // ignition / extinction / phase_change / fracture / reaction
    public readonly int Cell;
    public readonly string Detail;
    public readonly double Value;
    public RealityEvent(double time, string kind, int cell, string detail, double value)
    { Time = time; Kind = kind; Cell = cell; Detail = detail; Value = value; }
    public override string ToString() => $"{Time:0.000} {Kind}@{Cell} {Detail} {Value:0.###}";
}

/// <summary>确定性随机源：同种子同输入必得同结果。</summary>
public sealed class RealityRng
{
    private ulong _s;
    public RealityRng(ulong seed) => _s = seed == 0 ? 0x2545F4914F6CDD1DUL : seed;
    public ulong State => _s;
    public void SetState(ulong s) => _s = s;
    public double Next()
    {
        ulong x = _s; x ^= x << 13; x ^= x >> 7; x ^= x << 17; _s = x;
        return (x >> 11) * (1.0 / 9007199254740992.0);
    }
    public double Range(double a, double b) => a + (b - a) * Next();
}

/// <summary>与既有刚体/破坏系统的唯一接缝；明天由你那边实现，核心不依赖具体类。</summary>
public interface IRealityBodySink
{
    void ApplyImpulse(int bodyId, Vector3 impulse, Vector3 applicationPoint);
    void SetTemperature(int bodyId, double kelvin);
    void MarkBroken(int bodyId, int fragmentCount);
}
