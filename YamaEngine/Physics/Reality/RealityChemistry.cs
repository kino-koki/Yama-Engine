// ════════════════════════════════════════════════════════════════════════
// RealityChemistry.cs — 反应式解析 / 元素守恒校验 / Arrhenius 动力学 / 反应器
// 任何反应在注册前必须配平；每步反应进度受反应物存量硬限制，绝不吃成负摩尔。
// ════════════════════════════════════════════════════════════════════════
using System.Globalization;

namespace YamaEngine.Physics.Reality;

public static class ChemFormula
{
    /// <summary>解析无括号化学式，如 C6H10O5 / Fe / O2 / C2H6O。</summary>
    public static Dictionary<string, int> CountAtoms(string formula)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        int i = 0;
        while (i < formula.Length)
        {
            if (!char.IsUpper(formula[i])) { i++; continue; }
            int start = i++;
            while (i < formula.Length && char.IsLower(formula[i])) i++;
            string element = formula.Substring(start, i - start);
            int ds = i;
            while (i < formula.Length && char.IsDigit(formula[i])) i++;
            int count = ds == i ? 1 : int.Parse(formula.Substring(ds, i - ds), CultureInfo.InvariantCulture);
            result[element] = result.TryGetValue(element, out var old) ? old + count : count;
        }
        return result;
    }
    public static Dictionary<string, double> Multiply(Dictionary<string, int> atoms, double coefficient)
        => atoms.ToDictionary(kv => kv.Key, kv => kv.Value * coefficient, StringComparer.Ordinal);

    public static bool SameBalance(IReadOnlyDictionary<string, double> a,
        IReadOnlyDictionary<string, double> b, double tol = 1e-9)
    {
        var keys = a.Keys.Union(b.Keys, StringComparer.Ordinal);
        foreach (var k in keys)
        {
            double av = a.TryGetValue(k, out var x) ? x : 0.0;
            double bv = b.TryGetValue(k, out var y) ? y : 0.0;
            if (Math.Abs(av - bv) > tol * Math.Max(1.0, Math.Max(Math.Abs(av), Math.Abs(bv)))) return false;
        }
        return true;
    }
}

public readonly struct ChemTerm
{
    public readonly double Coefficient;
    public readonly string Formula;
    public ChemTerm(double coefficient, string formula) { Coefficient = coefficient; Formula = formula; }
}

public sealed class ChemEquation
{
    public string Text = "";
    public IReadOnlyList<ChemTerm> Reactants = Array.Empty<ChemTerm>();
    public IReadOnlyList<ChemTerm> Products = Array.Empty<ChemTerm>();
    public bool IsBalanced;
    public string BalanceReport = "";

    public static ChemEquation Parse(string text)
    {
        var eq = new ChemEquation { Text = text };
        int arrow = text.IndexOf("->", StringComparison.Ordinal);
        if (arrow < 0) { eq.BalanceReport = "missing_arrow"; return eq; }
        eq.Reactants = ParseSide(text.Substring(0, arrow));
        eq.Products = ParseSide(text.Substring(arrow + 2));
        if (eq.Reactants.Count == 0 || eq.Products.Count == 0)
        { eq.BalanceReport = "empty_side"; return eq; }
        eq.IsBalanced = CheckBalance(eq.Reactants, eq.Products, out var report);
        eq.BalanceReport = report;
        return eq;
    }

    private static IReadOnlyList<ChemTerm> ParseSide(string side)
    {
        var list = new List<ChemTerm>();
        foreach (var raw in side.Split('+'))
        {
            var tokens = raw.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0) continue;
            if (double.TryParse(tokens[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double c)
                && tokens.Length >= 2)
                list.Add(new ChemTerm(c, tokens[1]));
            else list.Add(new ChemTerm(1.0, tokens[0]));
        }
        return list;
    }

    public static bool CheckBalance(IReadOnlyList<ChemTerm> reactants,
        IReadOnlyList<ChemTerm> products, out string report)
    {
        var left = new Dictionary<string, double>(StringComparer.Ordinal);
        var right = new Dictionary<string, double>(StringComparer.Ordinal);
        Accumulate(left, reactants, 1.0);
        Accumulate(right, products, 1.0);
        bool ok = ChemFormula.SameBalance(left, right);
        report = ok ? "balanced" : Describe(left) + " != " + Describe(right);
        return ok;
    }

    private static void Accumulate(Dictionary<string, double> acc,
        IReadOnlyList<ChemTerm> terms, double sign)
    {
        foreach (var t in terms)
            foreach (var kv in ChemFormula.Multiply(ChemFormula.CountAtoms(t.Formula), t.Coefficient * sign))
                acc[kv.Key] = acc.TryGetValue(kv.Key, out var v) ? v + kv.Value : kv.Value;
    }

    private static string Describe(Dictionary<string, double> d) => string.Join(",",
        d.OrderBy(k => k.Key, StringComparer.Ordinal)
         .Select(k => k.Key + ":" + k.Value.ToString("0.###", CultureInfo.InvariantCulture)));

    public static double LimitingExtent(IReadOnlyList<ChemTerm> reactants,
        IReadOnlyDictionary<string, double> availableMoles)
    {
        double limit = double.MaxValue;
        foreach (var t in reactants)
        {
            double have = availableMoles.TryGetValue(t.Formula, out var m) ? m : 0.0;
            if (t.Coefficient <= 0.0) return 0.0;
            limit = Math.Min(limit, have / t.Coefficient);
        }
        return limit == double.MaxValue ? 0.0 : Math.Max(0.0, limit);
    }
}

public sealed class ChemReaction
{
    public ChemEquation Equation = ChemEquation.Parse("H2 -> H2");
    public double ActivationEnergyJPerMol = 50000.0;
    public double PreExponential = 1.0;
    public double EnthalpyKJPerMol = 0.0;      // 负 = 放热
    public string Catalyst = "";
    public double CatalystMultiplier = 1.0;
    public bool Enabled = true;

    /// <summary>Arrhenius 速率 × 反应物浓度驱动，催化剂只改指前因子。</summary>
    public double Rate(double kelvin, IReadOnlyDictionary<string, double> moles,
        bool catalystPresent, double cellVolumeM3 = 1.0)
    {
        if (kelvin <= 0.0 || !Enabled || !Equation.IsBalanced) return 0.0;
        double k = PreExponential * Math.Exp(-ActivationEnergyJPerMol / (ChemUnits.GasConstant * kelvin));
        if (catalystPresent) k *= CatalystMultiplier;
        double drive = 1.0;
        foreach (var t in Equation.Reactants)
        {
            double c = (moles.TryGetValue(t.Formula, out var m) ? Math.Max(0.0, m) : 0.0)
                       / Math.Max(1e-6, cellVolumeM3);
            drive *= Math.Pow(c, Math.Min(t.Coefficient, 3.0));
        }
        return k * drive;
    }
}

public sealed class ChemReactor
{
    private readonly List<ChemReaction> _reactions = new();
    public IReadOnlyList<ChemReaction> Reactions => _reactions;
    public readonly ChemSubstanceTable Table;
    public long TotalReactionSteps;
    public long RejectedUnbalanced;

    public ChemReactor(ChemSubstanceTable table) { Table = table; }

    public bool Register(ChemReaction r)
    {
        if (!r.Equation.IsBalanced) { RejectedUnbalanced++; return false; }
        _reactions.Add(r); return true;
    }

    /// <summary>推进反应；返回本步释放/吸收的热量（焦耳，正=放热）。</summary>
    public double Step(double kelvin, double dt, Dictionary<string, double> moles,
        double cellVolumeM3, ISet<string> catalysts, List<RealityEvent> events,
        double time, int cell)
    {
        double totalHeat = 0.0;
        foreach (var r in _reactions)
        {
            double rate = r.Rate(kelvin, moles,
                r.Catalyst.Length > 0 && catalysts.Contains(r.Catalyst), cellVolumeM3);
            if (rate <= 0.0) continue;
            double wanted = rate * dt;
            double limit = ChemEquation.LimitingExtent(r.Equation.Reactants, moles);
            double extent = Math.Min(wanted, limit);
            if (extent <= 1e-12) continue;
            foreach (var t in r.Equation.Reactants)
                moles[t.Formula] = Math.Max(0.0, moles.GetValueOrDefault(t.Formula) - t.Coefficient * extent);
            foreach (var t in r.Equation.Products)
                moles[t.Formula] = moles.GetValueOrDefault(t.Formula) + t.Coefficient * extent;
            double heat = -r.EnthalpyKJPerMol * 1000.0 * extent;
            totalHeat += heat;
            TotalReactionSteps++;
            if (Math.Abs(heat) > 1.0)
                events.Add(new RealityEvent(time, "reaction", cell, r.Equation.Text, heat));
        }
        return totalHeat;
    }

    /// <summary>元素守恒核算：反应前后各元素原子摩尔数逐一比对，返回最大相对偏差。</summary>
    public static double ElementBalanceError(IReadOnlyDictionary<string, double> before,
        IReadOnlyDictionary<string, double> after)
    {
        var b = AtomTotals(before);
        var a = AtomTotals(after);
        double worst = 0.0;
        foreach (var k in b.Keys.Union(a.Keys, StringComparer.Ordinal))
        {
            double bv = b.TryGetValue(k, out var x) ? x : 0.0;
            double av = a.TryGetValue(k, out var y) ? y : 0.0;
            worst = Math.Max(worst, RealityAssert.RelativeError(bv, av));
        }
        return worst;
    }

    private static Dictionary<string, double> AtomTotals(IReadOnlyDictionary<string, double> moles)
    {
        var totals = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var kv in moles)
            foreach (var atom in ChemFormula.Multiply(ChemFormula.CountAtoms(kv.Key), kv.Value))
                totals[atom.Key] = totals.GetValueOrDefault(atom.Key) + atom.Value;
        return totals;
    }
}
