using System;
namespace YamaEngine.Reality
{
    /// <summary>物种定义：元素组成向量（用于配平校验与组分守恒）。</summary>
    public sealed class YrSpecies
    {
        public string Name { get; init; } = "";
        public double[] Elements { get; init; } = Array.Empty<double>();
    }

    /// <summary>
    /// 一步反应：Stoich 为净计量数（负=反应物、正=产物），
    /// ForwardOrder / ReverseOrder 为各物种的反应级数。
    /// </summary>
    public sealed class YrReactionStep
    {
        public string Name { get; init; } = "";
        public float[] Stoich { get; init; } = Array.Empty<float>();
        public float[] ForwardOrder { get; init; } = Array.Empty<float>();
        public float[] ReverseOrder { get; init; } = Array.Empty<float>();
        public float PreExponential { get; init; } = 1f;
        public float ActivationEnergy { get; init; } = 0f;  // J/mol
        public float Enthalpy { get; init; } = 0f;          // J/mol（负=放热）
        public float EquilibriumK0 { get; init; } = 1f;
        public bool Reversible { get; init; } = false;
    }

    /// <summary>
    /// Arrhenius 动力学 + van't Hoff 平衡 + 显式子步 + 非负裁剪 + 元素守恒。
    /// 相对旧 Physics 的「反应式配平 + 单一 k(T)」，这轮把可逆、平衡、
    /// 多步网络、反应热过账和守恒断言接成一个可验证整体。
    /// </summary>
    public sealed class YrReactionNetwork
    {
        public const float GasConstant = 8.314462618f;
        private readonly YrSpecies[] _species;
        private readonly YrReactionStep[] _steps;
        private readonly ConservationLedger _ledger;

        public YrReactionNetwork(YrSpecies[] species, YrReactionStep[] steps, ConservationLedger ledger)
        {
            _species = species ?? Array.Empty<YrSpecies>();
            _steps = steps ?? Array.Empty<YrReactionStep>();
            _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        }

        public int SpeciesCount => _species.Length;
        public int StepCount => _steps.Length;

        /// <summary>校验每步每种元素的净计量和为 0（配平）。</summary>
        public bool IsBalanced(double tol = 1e-6)
        {
            if (_species.Length == 0) return false;
            int ne = _species[0].Elements.Length;
            foreach (var sp in _species)
                if (sp.Elements.Length != ne) return false;
            foreach (var r in _steps)
            {
                if (r.Stoich.Length != _species.Length) return false;
                for (int e = 0; e < ne; e++)
                {
                    double sum = 0.0;
                    for (int s = 0; s < _species.Length; s++)
                        sum += r.Stoich[s] * _species[s].Elements[e];
                    if (Math.Abs(sum) > tol) return false;
                }
            }
            return true;
        }

        public float Rate(int stepIndex, float[] conc, float temperature)
        {
            if (stepIndex < 0 || stepIndex >= _steps.Length) return 0f;
            var r = _steps[stepIndex];
            float T = MathF.Max(1f, temperature);
            float kf = r.PreExponential * MathF.Exp(-r.ActivationEnergy / (GasConstant * T));
            float vf = kf * Product(conc, r.ForwardOrder);
            if (!r.Reversible) return vf;
            float keq = MathF.Max(1e-12f, r.EquilibriumK0 *
                        MathF.Exp(-r.Enthalpy / (GasConstant * T)));
            float kr = kf / keq;
            float vr = kr * Product(conc, r.ReverseOrder);
            return vf - vr;
        }

        private static float Product(float[] conc, float[] order)
        {
            double p = 1.0;
            int n = Math.Min(conc.Length, order.Length);
            for (int s = 0; s < n; s++)
            {
                float o = order[s];
                float c = MathF.Max(0f, conc[s]);
                if (o == 0f) continue;
                p *= Math.Pow(c, o);
            }
            return (float)p;
        }

        /// <summary>显式子步推进；返回累计反应进度（供观测）。</summary>
        public float Step(float[] conc, float temperature, float dt, int substeps)
        {
            if (conc == null) throw new ArgumentNullException(nameof(conc));
            if (dt <= 0f || substeps < 1) throw new ArgumentOutOfRangeException(nameof(dt));
            float h = dt / substeps;
            float extent = 0f;
            for (int k = 0; k < substeps; k++)
            {
                for (int i = 0; i < _steps.Length; i++)
                {
                    var r = _steps[i];
                    float v = Rate(i, conc, temperature);
                    for (int s = 0; s < conc.Length && s < r.Stoich.Length; s++)
                        conc[s] = MathF.Max(0f, conc[s] + r.Stoich[s] * v * h);
                    extent += v * h;
                    // 反应热显式过账（负焓放热 → 向系统输入能量）
                    _ledger.BoundaryWork(-r.Enthalpy * v * h, RVec3.Zero);
                }
            }
            return extent;
        }

        /// <summary>按元素组成核算总元素量守恒（反应不应改变它）。</summary>
        public double ElementInventory(int element, float[] conc)
        {
            double sum = 0.0;
            int n = Math.Min(conc.Length, _species.Length);
            for (int s = 0; s < n; s++)
                sum += conc[s] * _species[s].Elements[element];
            return sum;
        }
    }
}
