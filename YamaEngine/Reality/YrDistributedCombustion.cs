using System;
namespace YamaEngine.Reality
{
    /// <summary>
    /// 分布式（逐格点）反应–扩散燃烧。
    ///
    /// 相对 YrCombustionScenario 的「集总燃料池」，这里把化学升级为空间分布：
    ///   · 组分场逐格点存储（燃料 / 氧化剂 / 产物 CO2 / 产物 H2O）；
    ///   · 组分像热量一样显式扩散（带 CFL 子步），火焰才能「传」而不是瞬间全域点燃；
    ///   · 限流反应物：反应进度受 min(燃料, 氧化剂/计量比) 约束，
    ///     缺氧时燃料烧不完（不完全燃烧），不会凭空多放出热量；
    ///   · 火焰锋面：燃点门控 + Arrhenius，热扩散点燃邻居 → 锋面逐格推进。
    ///
    /// 反应模型（甲烷类，元素 [C, H, O]）：CH4 + 2 O2 -&gt; CO2 + 2 H2O。
    /// 配平校验：C(-1+1)=0、H(-4+4)=0、O(-4+2+2)=0，构造时即断言配平。
    ///
    /// 显式近似（不隐藏）：
    ///   · 一维格点链，扩散为最近邻显式格式；
    ///   · 单步总包反应，不含自由基链式分支（真实烃燃烧是多步机理）；
    ///   · 燃点门控为硬阈值阶跃（保证确定性与锋面清晰），非平滑 Sigmoid；
    ///   · 燃料/氧化剂浓度为集总摩尔量，非严格摩尔体积分数。
    /// 所有能量流经 ConservationLedger 显式记账；元素总量在反应+扩散下守恒。
    /// </summary>
    public sealed class YrDistributedCombustion
    {
        // 物种索引
        public const int Fuel = 0;       // CH4
        public const int Oxidizer = 1;   // O2
        public const int ProductA = 2;   // CO2
        public const int ProductB = 3;   // H2O
        public const int SpeciesCount = 4;

        // 元素索引 [C, H, O]
        public const int ElemC = 0;
        public const int ElemH = 1;
        public const int ElemO = 2;
        public const int ElementCount = 3;

        /// <summary>各物种元素组成 [C, H, O]。</summary>
        public static readonly double[][] SpeciesElements =
        {
            new double[] { 1, 4, 0 },  // CH4
            new double[] { 0, 0, 2 },  // O2
            new double[] { 1, 0, 2 },  // CO2
            new double[] { 0, 2, 1 },  // H2O
        };

        /// <summary>净计量数（负=反应物，正=产物）：CH4 -1, O2 -2, CO2 +1, H2O +2。</summary>
        public static readonly float[] Stoich = { -1f, -2f, 1f, 2f };

        public const float GasConstant = 8.314462618f;

        private readonly int _n;
        private readonly ConservationLedger _ledger;

        private readonly float[][] _conc;    // [species][cell]
        private readonly float[][] _buf;     // 扩散写缓冲
        private readonly float[] _temp;      // 共享温度场
        private readonly float[] _tempBuf;

        private readonly float _preExponential;
        private readonly float _activationEnergy; // J/mol
        private readonly float _heatRelease;      // J per mol 反应进度（正=放热）
        private readonly float _ignitionTemp;     // K，硬阈值
        private readonly float _heatCapacity;     // J/(mol·K)
        private readonly float _spacing;          // 格点间距 m
        private readonly float _speciesDiffusivity; // m^2/s
        private readonly float _thermalDiffusivity; // m^2/s
        private readonly float _maxBurnFraction;  // 每帧最大燃耗比例（有限火焰速度）

        private int _tick;
        private float _totalHeatReleased;

        public YrDistributedCombustion(
            int cells,
            float[] initialFuel,
            float[] initialOxidizer,
            float ambientTemperature,
            float preExponential,
            float activationEnergy,
            float heatReleasePerMol,
            float ignitionTemperature,
            float heatCapacity,
            float spacing,
            float speciesDiffusivity,
            float thermalDiffusivity,
            float maxBurnFractionPerFrame,
            ConservationLedger ledger)
        {
            if (cells < 1) throw new ArgumentOutOfRangeException(nameof(cells));
            if (initialFuel == null || initialFuel.Length < cells)
                throw new ArgumentException("初始燃料场长度不足", nameof(initialFuel));
            if (initialOxidizer == null || initialOxidizer.Length < cells)
                throw new ArgumentException("初始氧化剂场长度不足", nameof(initialOxidizer));
            _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));

            _n = cells;
            _preExponential = MathF.Max(0f, preExponential);
            _activationEnergy = MathF.Max(0f, activationEnergy);
            _heatRelease = MathF.Max(0f, heatReleasePerMol);
            _ignitionTemp = ignitionTemperature;
            _heatCapacity = MathF.Max(1e-6f, heatCapacity);
            _spacing = MathF.Max(1e-9f, spacing);
            _speciesDiffusivity = MathF.Max(0f, speciesDiffusivity);
            _thermalDiffusivity = MathF.Max(0f, thermalDiffusivity);
            // 有限火焰速度：每帧最多烧掉本地燃料的这一比例，防止单帧燃尽导致火焰猝熄。
            _maxBurnFraction = RealityKernels.Clamp01(maxBurnFractionPerFrame);

            _conc = new float[SpeciesCount][];
            _buf = new float[SpeciesCount][];
            for (int s = 0; s < SpeciesCount; s++)
            {
                _conc[s] = new float[_n];
                _buf[s] = new float[_n];
            }
            _temp = new float[_n];
            _tempBuf = new float[_n];

            for (int i = 0; i < _n; i++)
            {
                _conc[Fuel][i] = MathF.Max(0f, initialFuel[i]);
                _conc[Oxidizer][i] = MathF.Max(0f, initialOxidizer[i]);
                _conc[ProductA][i] = 0f;
                _conc[ProductB][i] = 0f;
                _temp[i] = ambientTemperature;
            }

            // 构造时断言总包反应配平（每种元素净计量和为 0）。
            for (int e = 0; e < ElementCount; e++)
            {
                double sum = 0.0;
                for (int s = 0; s < SpeciesCount; s++) sum += Stoich[s] * SpeciesElements[s][e];
                if (Math.Abs(sum) > 1e-9)
                    throw new InvalidOperationException(
                        "总包反应未配平：元素 " + e + " 净计量和 " + sum);
            }
        }

        public int Count => _n;
        public int Tick => _tick;
        public ConservationLedger Ledger => _ledger;
        public float[] Temperature => _temp;
        public float TotalHeatReleased => _totalHeatReleased;

        /// <summary>读取某格点某物种浓度。</summary>
        public float Conc(int species, int cell)
            => (species >= 0 && species < SpeciesCount && cell >= 0 && cell < _n)
                ? _conc[species][cell] : 0f;

        /// <summary>某格点是否已达到燃点（用于观测火焰锋面）。</summary>
        public bool IsIgnited(int cell)
            => cell >= 0 && cell < _n && _temp[cell] >= _ignitionTemp;

        /// <summary>已点燃格点数。</summary>
        public int IgnitedCount()
        {
            int c = 0;
            for (int i = 0; i < _n; i++) if (_temp[i] >= _ignitionTemp) c++;
            return c;
        }

        /// <summary>
        /// 点火源（火花塞 / 长明灯）：把某格点温度直接抬到给定值。
        /// 这是火焰传播的必要初始条件——否则全域等于环境温度、无格点达燃点，
        /// 反应永不启动。点火本身不注入能量记账（它是外部边界条件的理想化）。
        /// </summary>
        public void Ignite(int cell, float temperature)
        {
            if (cell < 0 || cell >= _n) throw new ArgumentOutOfRangeException(nameof(cell));
            _temp[cell] = MathF.Max(_temp[cell], temperature);
        }

        /// <summary>
        /// 全域某元素总量 = Σ_格点 Σ_物种 浓度·元素数。
        /// 反应只在物种间转移元素、扩散只在格点间搬移质量，故此量应严格守恒。
        /// </summary>
        public double ElementInventory(int element)
        {
            double sum = 0.0;
            for (int i = 0; i < _n; i++)
                for (int s = 0; s < SpeciesCount; s++)
                    sum += (double)_conc[s][i] * SpeciesElements[s][element];
            return sum;
        }

        /// <summary>
        /// 推进一帧：组分扩散 → （逐格点）反应与放热 → 热扩散。
        /// 扩散均按 CFL 条件拆子步；返回本帧释放的总反应热（J）。
        /// </summary>
        public float Step(float dt)
        {
            if (dt <= 0f) throw new ArgumentOutOfRangeException(nameof(dt));
            _tick++;

            // 1) 组分与热量扩散（最近邻显式格式，按 CFL 拆子步）
            float dMax = MathF.Max(_speciesDiffusivity, _thermalDiffusivity);
            if (dMax > 0f)
            {
                float stable = _spacing * _spacing / (2f * MathF.Max(1e-12f, dMax));
                int sub = Math.Max(1, (int)MathF.Ceiling(dt / MathF.Max(1e-9f, stable)));
                float h = dt / sub;
                for (int s = 0; s < sub; s++)
                {
                    DiffuseSpecies(h);
                    DiffuseHeat(h);
                }
            }

            // 2) 逐格点反应：燃点门控 + Arrhenius + 限流反应物
            float heatThisFrame = 0f;
            for (int i = 0; i < _n; i++)
            {
                if (_temp[i] < _ignitionTemp) continue;   // 未达燃点：不反应
                if (_conc[Fuel][i] <= 0f || _conc[Oxidizer][i] <= 0f) continue;

                float T = MathF.Max(1f, _temp[i]);
                float kf = _preExponential * MathF.Exp(-_activationEnergy / (GasConstant * T));
                float vf = kf * _conc[Fuel][i] * _conc[Oxidizer][i];
                // 本帧反应进度上限（三重约束）：
                //   · 限流反应物：extent &lt;= min(fuel, ox/2)——缺氧则烧不完；
                //   · 有限火焰速度：extent &lt;= maxBurnFraction·fuel——防单帧燃尽猝熄。
                float extentMax = MathF.Min(_conc[Fuel][i], _conc[Oxidizer][i] / 2f);
                extentMax = MathF.Min(extentMax, _maxBurnFraction * _conc[Fuel][i]);
                float extent = MathF.Min(vf * dt, extentMax);
                if (extent <= 0f) continue;

                _conc[Fuel][i] = MathF.Max(0f, _conc[Fuel][i] + Stoich[Fuel] * extent);
                _conc[Oxidizer][i] = MathF.Max(0f, _conc[Oxidizer][i] + Stoich[Oxidizer] * extent);
                _conc[ProductA][i] = MathF.Max(0f, _conc[ProductA][i] + Stoich[ProductA] * extent);
                _conc[ProductB][i] = MathF.Max(0f, _conc[ProductB][i] + Stoich[ProductB] * extent);

                float q = extent * _heatRelease;
                _temp[i] += q / _heatCapacity;
                heatThisFrame += q;
            }
            if (heatThisFrame > 0f)
            {
                _ledger.BoundaryWork(heatThisFrame, RVec3.Zero);
                _totalHeatReleased += heatThisFrame;
            }
            return heatThisFrame;
        }

        /// <summary>所有物种显式扩散一步（最近邻，长度 h）。</summary>
        private void DiffuseSpecies(float h)
        {
            float coef = _speciesDiffusivity * h / (_spacing * _spacing);
            if (coef <= 0f) return;
            for (int s = 0; s < SpeciesCount; s++)
            {
                var c = _conc[s]; var b = _buf[s];
                for (int i = 0; i < _n; i++)
                {
                    float left = i > 0 ? c[i - 1] : c[i];
                    float right = i < _n - 1 ? c[i + 1] : c[i];
                    b[i] = MathF.Max(0f, c[i] + coef * (left + right - 2f * c[i]));
                }
                _conc[s] = b; _buf[s] = c;
            }
        }

        /// <summary>温度场显式扩散一步（最近邻，长度 h）。就地回写以保持字段引用不变。</summary>
        private void DiffuseHeat(float h)
        {
            float coef = _thermalDiffusivity * h / (_spacing * _spacing);
            if (coef <= 0f) return;
            for (int i = 0; i < _n; i++)
            {
                float left = i > 0 ? _temp[i - 1] : _temp[i];
                float right = i < _n - 1 ? _temp[i + 1] : _temp[i];
                _tempBuf[i] = _temp[i] + coef * (left + right - 2f * _temp[i]);
            }
            for (int i = 0; i < _n; i++) _temp[i] = _tempBuf[i];
        }

        /// <summary>按固定顺序拼接状态，用于逐位确定性回放校验。</summary>
        public float[] Snapshot()
        {
            var list = new System.Collections.Generic.List<float>(2 + SpeciesCount * _n + _n + 2);
            list.Add(_tick);
            for (int s = 0; s < SpeciesCount; s++)
                for (int i = 0; i < _n; i++) list.Add(_conc[s][i]);
            for (int i = 0; i < _n; i++) list.Add(_temp[i]);
            list.Add(_totalHeatReleased);
            list.Add((float)_ledger.EnergyStored);
            return list.ToArray();
        }
    }
}
