using System;
namespace YamaEngine.Reality
{
    /// <summary>
    /// 统一编排：固定步 → 热/化学 → 强度退化与断裂 → XPBD 刚柔体 → PBF 流体
    /// → 守恒账本校验 → 确定性快照。所有子系统共享一个账本，任何能量/质量
    /// 变化都必须显式过账，杜绝"隐式产生能量"。
    /// </summary>
    public sealed class RealityPhysicsWorld
    {
        private readonly ConservationLedger _ledger = new ConservationLedger();
        private readonly XpbdSolver _xpbd = new XpbdSolver();
        private readonly PbfFluid _pbf = new PbfFluid();
        private readonly ThermoStructuralCoupler _thermal;
        private readonly FractureModel _fracture;
        private readonly YrFixedClock _clock = new YrFixedClock(1.0 / 60.0, 8);
        private readonly YrIslandSolver _islands = new YrIslandSolver();
        private int _tick;

        public RealityPhysicsWorld()
        {
            _thermal = new ThermoStructuralCoupler(RealityMaterial.Steel(), _ledger);
            _fracture = new FractureModel(1e4f, 3f, _ledger);
        }

        public ConservationLedger Ledger => _ledger;
        public XpbdSolver Xpbd => _xpbd;
        public PbfFluid Fluid => _pbf;
        public ThermoStructuralCoupler Thermal => _thermal;
        public FractureModel Fracture => _fracture;
        public YrFixedClock Clock => _clock;
        public YrIslandSolver Islands => _islands;
        public int Tick => _tick;

        public void Step(float dt)
        {
            if (dt <= 0f) throw new ArgumentOutOfRangeException(nameof(dt));
            _thermal.Step(dt);
            int n = _thermal.Count;
            for (int i = 0; i < n; i++)
            {
                _fracture.Accumulate(i, _thermal.StressAt(i), MathF.Max(1f, _thermal.StrengthAt(i)), 1f);
                _fracture.Evaluate(i, _thermal.StressAt(i), 2.0e11f, 1e-4f);
            }
            _xpbd.Step(dt, 4, 8, 0f);
            _tick++;
        }

        /// <summary>
        /// 热↔流体闭环：温度场经 Boussinesq 生成浮力加速度，再喂给 PBF 预测步。
        /// 这是把上一轮「热-结构」单向耦合升级为热/流体/结构三者闭环的入口。
        /// </summary>
        public void StepWithBuoyancy(float[] px, float[] py, float[] pz,
                                     float[] vx, float[] vy, float[] vz,
                                     float dt, int fluidIterations,
                                     float referenceTemperature, float thermalExpansion)
        {
            if (dt <= 0f) throw new ArgumentOutOfRangeException(nameof(dt));
            _thermal.Step(dt);
            int n = Math.Min(_thermal.Count, vy.Length);
            var buoyancy = new float[vy.Length];
            for (int i = 0; i < n; i++)
                buoyancy[i] = ThermoFluidCoupler.BuoyancyAcceleration(
                    _thermal.Temperature[i], referenceTemperature, thermalExpansion,
                    ThermoFluidCoupler.DefaultGravity).Y;
            _pbf.Step(px, py, pz, vx, vy, vz, dt, fluidIterations, -9.81f, buoyancy);
            int m = _thermal.Count;
            for (int i = 0; i < m; i++)
            {
                _fracture.Accumulate(i, _thermal.StressAt(i), MathF.Max(1f, _thermal.StrengthAt(i)), 1f);
                _fracture.Evaluate(i, _thermal.StressAt(i), 2.0e11f, 1e-4f);
            }
            _xpbd.Step(dt, 4, 8, 0f);
            _tick++;
        }

        /// <summary>
        /// 按真实帧 dt 推进固定物理步（Godot 3 `_physics_process` 语义）。
        /// 返回本帧实际执行的步数；渲染侧用 Clock.InterpolationAlpha() 插值。
        /// </summary>
        public int FixedStepAdvance(float realDt)
        {
            int steps = _clock.Advance(realDt);
            for (int s = 0; s < steps; s++) Step(_clock.FixedDt);
            return steps;
        }

        /// <summary>用约束图重建岛；空图时每个粒子自成一岛。</summary>
        public void AttachIslands(int particleCount, (int A, int B)[] edges)
            => _islands.Build(particleCount, edges);

        /// <summary>按固定顺序拼接状态，用于逐位确定性回放校验。</summary>
        public float[] Snapshot()
        {
            var list = new System.Collections.Generic.List<float>();
            list.Add(_tick);
            var p = _xpbd.Particles;
            list.Add(p.Length);
            for (int i = 0; i < p.Length; i++)
            {
                list.Add(p[i].Position.X); list.Add(p[i].Position.Y); list.Add(p[i].Position.Z);
                list.Add(p[i].Velocity.X); list.Add(p[i].Velocity.Y); list.Add(p[i].Velocity.Z);
            }
            list.Add(_thermal.Count);
            for (int i = 0; i < _thermal.Count; i++)
            {
                list.Add(_thermal.Temperature[i]);
                list.Add(_thermal.Strength[i]);
                list.Add(_thermal.Fuel[i]);
            }
            list.Add(_fracture.Count);
            for (int i = 0; i < _fracture.Count; i++)
                list.Add(_fracture.Fatigue[i]);
            list.Add((float)_ledger.MassStored);
            list.Add((float)_ledger.EnergyStored);
            return list.ToArray();
        }
    }
}
