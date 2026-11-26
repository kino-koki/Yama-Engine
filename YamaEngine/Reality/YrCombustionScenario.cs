using System;
namespace YamaEngine.Reality
{
    /// <summary>场景单帧观测值。</summary>
    public readonly struct YrScenarioFrame
    {
        public readonly int Tick;
        public readonly float PeakTemperature;
        public readonly float BurnedFuel;
        public readonly int BrokenCount;
        public readonly double LedgerResidual;
        public YrScenarioFrame(int tick, float peak, float burned, int broken, double residual)
        { Tick = tick; PeakTemperature = peak; BurnedFuel = burned;
          BrokenCount = broken; LedgerResidual = residual; }
    }

    /// <summary>
    /// 端到端场景：化学 → 热 → 应力 → 强度退化 → 断裂 → 碎片，全部共用一本账。
    ///
    /// 单一责任划分（避免双重记账）：
    ///  · YrReactionNet 独占化学：燃料消耗、Arrhenius 速率、反应热进账本；
    ///  · ThermoStructuralCoupler 独占输运与结构响应（传入 fuel 全 0，
    ///    其内建燃烧分支不触发），只做扩散/相变/热应力/强度退化；
    ///  · FractureModel 独占断裂判据，释放能进账本；
    ///  · 场景只把「账本里已登记的反应热」按初始燃料权重分布到温度场，
    ///    不再二次记账。
    ///
    /// 化学为集总模型（一个燃料池），热场为分布模型（n 格点）——
    /// 这是显式近似，文档里不隐藏。
    /// </summary>
    public sealed class YrCombustionScenario
    {
        private readonly ConservationLedger _ledger = new ConservationLedger();
        private readonly ThermoStructuralCoupler _thermal;
        private readonly FractureModel _fracture;
        private readonly YrReactionNetwork _reactions;
        private readonly float[] _fuelWeights;
        private readonly float[] _conc;
        private readonly float _initialFuel;
        private readonly float _ambient;
        private readonly float _heatPerFuel;
        private readonly float _heatCapacityFactor;
        private int _tick;
        private float _lastBurned;

        public YrCombustionScenario(int cells, float ambientTemperature,
                                    float initialFuel, float[] fuelWeights,
                                    float heatOfCombustion, float heatCapacityFactor)
        {
            if (cells < 1) throw new ArgumentOutOfRangeException(nameof(cells));
            _ambient = ambientTemperature;
            _initialFuel = MathF.Max(0f, initialFuel);
            _heatPerFuel = MathF.Max(0f, heatOfCombustion);
            _heatCapacityFactor = MathF.Max(1f, heatCapacityFactor);
            _fuelWeights = new float[cells];
            double sum = 0.0;
            for (int i = 0; i < cells; i++)
            {
                float w = fuelWeights != null && i < fuelWeights.Length
                    ? MathF.Max(0f, fuelWeights[i]) : 1f;
                _fuelWeights[i] = w; sum += w;
            }
            if (sum <= 1e-12) { for (int i = 0; i < cells; i++) _fuelWeights[i] = 1f; sum = cells; }
            for (int i = 0; i < cells; i++) _fuelWeights[i] = (float)(_fuelWeights[i] / sum);

            var temp = new float[cells];
            var zeroFuel = new float[cells];
            for (int i = 0; i < cells; i++) temp[i] = ambientTemperature;
            _thermal = new ThermoStructuralCoupler(RealityMaterial.Steel(), _ledger);
            _thermal.Initialize(temp, zeroFuel);
            _fracture = new FractureModel(1e4f, 3f, _ledger);
            _fracture.Initialize(cells, 1e-5f);

            // Fuel -> Ash 一阶不可逆放热反应；元素向量 [C] 保证配平。
            var species = new YrSpecies[] {
                new YrSpecies { Name = "Fuel", Elements = new double[] { 1.0 } },
                new YrSpecies { Name = "Ash",  Elements = new double[] { 1.0 } }
            };
            var step = new YrReactionStep {
                Name = "combustion", Stoich = new float[] { -1f, 1f },
                ForwardOrder = new float[] { 1f, 0f }, ReverseOrder = new float[] { 0f, 1f },
                PreExponential = 0.8f, ActivationEnergy = 1200f,
                Enthalpy = -MathF.Max(1f, heatOfCombustion),
                EquilibriumK0 = 1f, Reversible = false
            };
            _reactions = new YrReactionNetwork(species, new YrReactionStep[] { step }, _ledger);
            _conc = new float[] { _initialFuel, 0f };
        }

        public int Tick => _tick;
        public ConservationLedger Ledger => _ledger;
        public float BurnedFuel => _lastBurned;
        public ThermoStructuralCoupler Thermal => _thermal;
        public FractureModel Fracture => _fracture;

        public YrScenarioFrame Step(float dt)
        {
            if (dt <= 0f) throw new ArgumentOutOfRangeException(nameof(dt));
            float meanT = 0f;
            var t = _thermal.Temperature;
            for (int i = 0; i < t.Length; i++) meanT += t[i];
            meanT /= MathF.Max(1, t.Length);

            float before = _conc[0];
            _reactions.Step(_conc, MathF.Max(1f, meanT), dt, 16);
            float burned = MathF.Max(0f, before - _conc[0]);
            _lastBurned = MathF.Max(0f, _initialFuel - _conc[0]);

            // 把「账本里已登记的反应热」按权重分布到温度场（不再二次记账）
            float dT = burned * _heatPerFuel / _heatCapacityFactor;
            for (int i = 0; i < t.Length; i++) t[i] += dT * _fuelWeights[i];

            _thermal.Step(dt);   // 扩散 + 相变 + 热应力 + 强度退化
            var t2 = _thermal.Temperature;
            for (int i = 0; i < t2.Length; i++)
            {
                _fracture.Accumulate(i, _thermal.StressAt(i),
                                     MathF.Max(1f, _thermal.StrengthAt(i)), 1f);
                _fracture.Evaluate(i, _thermal.StressAt(i), 2.0e11f, 1e-4f);
            }

            float peak = 0f; int broken = 0;
            for (int i = 0; i < t2.Length; i++)
            {
                if (t2[i] > peak) peak = t2[i];
                if (_fracture.IsBroken(i)) broken++;
            }
            _tick++;
            return new YrScenarioFrame(_tick, peak, _lastBurned,
                                       broken, _ledger.ClosedResidual());
        }

        /// <summary>按固定顺序拼接状态，用于逐位确定性回放校验。</summary>
        public float[] Snapshot()
        {
            var list = new System.Collections.Generic.List<float>();
            list.Add(_tick);
            list.Add(_conc[0]); list.Add(_conc[1]);
            list.Add((float)_ledger.MassStored);
            list.Add((float)_ledger.EnergyStored);
            var t = _thermal.Temperature;
            list.Add(t.Length);
            for (int i = 0; i < t.Length; i++)
            {
                list.Add(t[i]);
                list.Add(_thermal.Strength[i]);
                list.Add(_fracture.Fatigue[i]);
            }
            return list.ToArray();
        }
    }
}
