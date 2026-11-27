using System;
namespace YamaEngine.Reality
{
    /// <summary>
    /// 断裂判据：线弹性断裂力学的能量释放率 G = sigma^2 * pi * a / E，
    /// 配合 Miner 疲劳累积 D += (sigma/uts)^m * cycles。
    /// 明确边界：这是工程判据，不是有限元裂纹扩展；不宣称与真实断裂形貌一致。
    /// </summary>
    public sealed class FractureModel
    {
        private float[] _fatigue = Array.Empty<float>();
        private float[] _crack = Array.Empty<float>();
        private bool[] _broken = Array.Empty<bool>();
        private int _n;
        private readonly float _toughness;
        private readonly float _exponent;
        private readonly ConservationLedger _ledger;

        public FractureModel(float toughness, float exponent, ConservationLedger ledger)
        {
            _toughness = toughness > 0f ? toughness : 1f;
            _exponent = exponent > 0f ? exponent : 3f;
            _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        }

        public int Count => _n;
        public float[] Fatigue => _fatigue;
        public bool IsBroken(int i) => i >= 0 && i < _n && _broken[i];

        public void Initialize(int n, float initialCrack)
        {
            if (n < 0) throw new ArgumentOutOfRangeException(nameof(n));
            _n = n;
            _fatigue = new float[n]; _crack = new float[n]; _broken = new bool[n];
            for (int i = 0; i < n; i++) _crack[i] = MathF.Max(1e-6f, initialCrack);
        }

        public void Accumulate(int i, float stress, float ultimateTensile, float cycles)
        {
            if (i < 0 || i >= _n || _broken[i]) return;
            float ratio = MathF.Abs(stress) / MathF.Max(1f, ultimateTensile);
            _fatigue[i] += MathF.Pow(ratio, _exponent) * MathF.Max(0f, cycles);
            _crack[i] += MathF.Max(0f, cycles) * ratio * 1e-7f;
        }

        /// <summary>返回本步断裂释放的能量（非负）；未断裂返回 0。</summary>
        public float Evaluate(int i, float stress, float youngModulus, float area)
        {
            if (i < 0 || i >= _n || _broken[i]) return 0f;
            float sigma2 = stress * stress;
            float G = sigma2 * MathF.PI * _crack[i] / MathF.Max(1f, youngModulus);
            if (G < _toughness && _fatigue[i] < 1f) return 0f;
            float released = G * MathF.Max(0f, area);
            _broken[i] = true;
            _ledger.BoundaryWork(released, RVec3.Zero);
            return released;
        }

        public float EnergyReleaseRate(int i, float stress, float youngModulus)
        {
            if (i < 0 || i >= _n) return 0f;
            return stress * stress * MathF.PI * _crack[i] / MathF.Max(1f, youngModulus);
        }
    }
}
