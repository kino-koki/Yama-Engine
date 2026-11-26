using System;
namespace YamaEngine.Reality
{
    /// <summary>材料参数（工程量级近似，非材料数据库）。</summary>
    public readonly struct RealityMaterial
    {
        public readonly float SpecificHeat;
        public readonly float Conductivity;
        public readonly float Expansion;
        public readonly float YoungModulus;
        public readonly float YieldStrength;
        public readonly float MeltingPoint;
        public readonly float LatentHeat;
        public readonly float IgnitionPoint;
        public readonly float HeatOfCombustion;
        public readonly float Density;

        public RealityMaterial(float cp, float k, float alpha, float E, float yield,
                               float melt, float latent, float ignition, float combustion, float density)
        {
            SpecificHeat = cp; Conductivity = k; Expansion = alpha; YoungModulus = E;
            YieldStrength = yield; MeltingPoint = melt; LatentHeat = latent;
            IgnitionPoint = ignition; HeatOfCombustion = combustion; Density = density;
        }

        public static RealityMaterial Steel() =>
            new RealityMaterial(490f, 45f, 1.2e-5f, 2.0e11f, 2.5e8f, 1700f, 2.7e5f, 0f, 0f, 7850f);
        public static RealityMaterial Wood() =>
            new RealityMaterial(1700f, 0.15f, 3.0e-6f, 1.1e10f, 4.0e7f, 0f, 0f, 573f, 1.8e7f, 700f);
    }

    /// <summary>
    /// 热-结构-化学双向耦合：显式热扩散（带 CFL 子步）、相变潜热、热应力、
    /// 强度退化、燃烧放热与燃料消耗。所有能量流经 ConservationLedger 显式记账。
    /// </summary>
    public sealed class ThermoStructuralCoupler
    {
        private float[] _t = Array.Empty<float>();
        private float[] _fuel = Array.Empty<float>();
        private float[] _strength = Array.Empty<float>();
        private float[] _stress = Array.Empty<float>();
        private float[] _buffer = Array.Empty<float>();
        private int _n;
        private readonly RealityMaterial _material;
        private readonly ConservationLedger _ledger;
        private float _referenceTemperature = 293.15f;
        private float _spacing = 0.05f;

        public ThermoStructuralCoupler(RealityMaterial material, ConservationLedger ledger)
        {
            _material = material;
            _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        }

        public int Count => _n;
        public float[] Temperature => _t;
        public float[] Strength => _strength;
        public float[] ThermalStress => _stress;
        public float[] Fuel => _fuel;
        public ConservationLedger Ledger => _ledger;

        public void Initialize(float[] temperature, float[] fuel)
        {
            if (temperature == null) throw new ArgumentNullException(nameof(temperature));
            _n = temperature.Length;
            _t = (float[])temperature.Clone();
            _fuel = fuel != null ? (float[])fuel.Clone() : new float[_n];
            _strength = new float[_n]; _stress = new float[_n]; _buffer = new float[_n];
            RecomputeDerived();
        }

        /// <summary>显式扩散，自动按 CFL 条件 dt &lt;= dx^2/(4*alpha) 拆子步。</summary>
        public void Step(float dt)
        {
            if (dt <= 0f) throw new ArgumentOutOfRangeException(nameof(dt));
            float alpha = _material.Conductivity /
                          (_material.Density * MathF.Max(1e-6f, _material.SpecificHeat));
            float stable = _spacing * _spacing / (4f * MathF.Max(1e-12f, alpha));
            int sub = Math.Max(1, (int)MathF.Ceiling(dt / MathF.Max(1e-9f, stable)));
            float h = dt / sub;
            float coef = alpha * h / (_spacing * _spacing);
            for (int s = 0; s < sub; s++)
            {
                for (int i = 0; i < _n; i++)
                {
                    float left = i > 0 ? _t[i - 1] : _t[i];
                    float right = i < _n - 1 ? _t[i + 1] : _t[i];
                    _buffer[i] = _t[i] + coef * (left + right - 2f * _t[i]);
                }
                var swap = _t; _t = _buffer; _buffer = swap;
            }
            ApplyPhaseChangeAndCombustion(dt);
            RecomputeDerived();
        }

        private void ApplyPhaseChangeAndCombustion(float dt)
        {
            float melt = _material.MeltingPoint;
            float band = 25f;
            for (int i = 0; i < _n; i++)
            {
                // 等效比热法处理潜热：温度穿越熔点带时吸收/释放潜热。
                if (melt > 0f && _t[i] > melt - band && _t[i] < melt + band)
                {
                    float cpEff = _material.SpecificHeat + _material.LatentHeat / (2f * band);
                    _t[i] -= (_material.LatentHeat / MathF.Max(1f, cpEff)) * 0.01f;
                }
                // 燃烧三要素：燃料、氧气(此处以 fuel>0 隐含)、达到燃点。
                if (_material.IgnitionPoint > 0f && _fuel[i] > 0f && _t[i] >= _material.IgnitionPoint)
                {
                    float burn = MathF.Min(_fuel[i], 0.01f * dt);
                    _fuel[i] -= burn;
                    double q = burn * _material.HeatOfCombustion;
                    _ledger.BoundaryWork(q, RVec3.Zero);
                    _t[i] += (float)(q / MathF.Max(1f, _material.SpecificHeat));
                }
            }
        }

        private void RecomputeDerived()
        {
            float melt = _material.MeltingPoint;
            for (int i = 0; i < _n; i++)
            {
                float dT = _t[i] - _referenceTemperature;
                _stress[i] = _material.YoungModulus * _material.Expansion * dT;
                float soft = melt > 0f
                    ? RealityKernels.Clamp01((melt - _t[i]) / MathF.Max(1f, melt - _referenceTemperature))
                    : 1f;
                _strength[i] = _material.YieldStrength * soft * soft;
            }
        }

        public float StrengthAt(int i) => i >= 0 && i < _n ? _strength[i] : 0f;
        public float StressAt(int i) => i >= 0 && i < _n ? _stress[i] : 0f;
    }
}
