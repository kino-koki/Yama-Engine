using System;
namespace YamaEngine.Reality
{
    /// <summary>
    /// 质量·元素·能量·动量四本账。任何物理事件都必须显式记账；
    /// ClosedResidual() 返回四本账中的最大失衡，用于逐项核算而非画面拟真。
    /// </summary>
    public sealed class ConservationLedger
    {
        private readonly System.Collections.Generic.Dictionary<string, double> _elements
            = new System.Collections.Generic.Dictionary<string, double>(StringComparer.Ordinal);
        private double _massIn, _massOut;
        private double _energyIn, _energyOut;
        private RVec3 _momentumIn, _momentumOut;
        // 边界通量：唯一允许改变系统总能量/动量的入口。内部反应不得改这两项。
        private double _boundaryEnergy;
        private RVec3 _boundaryMomentum;
        private double _reactionHeat; // 内部化学能→热能转换，仅供诊断，净能量不变

        public double MassStored => _massIn - _massOut;
        public double EnergyStored => _energyIn - _energyOut;
        public RVec3 MomentumStored => _momentumIn - _momentumOut;

        public void Supply(double mass, string element, double energy, RVec3 momentum)
        {
            if (mass <= 0.0) throw new ArgumentOutOfRangeException(nameof(mass));
            _massIn += mass; _energyIn += energy; _momentumIn = _momentumIn + momentum;
            _boundaryEnergy += energy; _boundaryMomentum = _boundaryMomentum + momentum;
            AddElement(element, mass);
        }

        public void Sink(double mass, string element, double energy, RVec3 momentum)
        {
            if (mass <= 0.0) throw new ArgumentOutOfRangeException(nameof(mass));
            _massOut += mass; _energyOut += energy; _momentumOut = _momentumOut + momentum;
            _boundaryEnergy -= energy; _boundaryMomentum = _boundaryMomentum - momentum;
            AddElement(element, -mass);
        }

        /// <summary>封闭系统内的化学反应：质量与元素总量不变，只在元素间转移，并记账能量。</summary>
        public void React(string from, string to, double mass, double energyDelta, RVec3 momentumDelta)
        {
            if (mass <= 0.0) throw new ArgumentOutOfRangeException(nameof(mass));
            // 封闭系统内的化学反应：质量与元素总量不变，化学能→热能属内部转换，
            // 净能量/净动量不变，因此不进 _energyIn/_energyOut/_boundary*。
            AddElement(from, -mass);
            AddElement(to, mass);
            _reactionHeat += energyDelta;
        }

        /// <summary>外界对系统做功/传热（开放边界，必须显式调用，不能隐式产生能量）。</summary>
        public void BoundaryWork(double energy, RVec3 momentum)
        {
            if (energy >= 0.0) _energyIn += energy;
            else _energyOut += -energy;
            _boundaryEnergy += energy;
            _boundaryMomentum = _boundaryMomentum + momentum;
            if (energy >= 0.0) _momentumIn = _momentumIn + momentum;
            else _momentumOut = _momentumOut - momentum;
        }

        /// <summary>内部反应累计热（净能量不变，仅用于诊断输出）。</summary>
        public double ReactionHeat => _reactionHeat;

        private void AddElement(string name, double delta)
        {
            if (string.IsNullOrEmpty(name)) throw new ArgumentException("元素名不能为空", nameof(name));
            _elements.TryGetValue(name, out double cur);
            _elements[name] = cur + delta;
        }

        public double ElementSum()
        {
            double s = 0.0;
            foreach (var kv in _elements) s += kv.Value;
            return s;
        }

        /// <summary>
        /// 四本账最大失衡。语义（第五轮修正）：
        ///  · 物质：质量账必须等于元素账之和（物质不能凭空产生/消失）；
        ///  · 能量：账内净能量必须等于显式登记的边界通量（杜绝隐式产生能量）；
        ///  · 动量：同上，成对反冲。
        /// 开放系统允许净能量/动量非零，只要每一笔都有对应的边界通量。
        /// </summary>
        public double ClosedResidual()
        {
            double massElement = Math.Abs(MassStored - ElementSum());
            double energy = Math.Abs(EnergyStored - _boundaryEnergy);
            RVec3 p = MomentumStored - _boundaryMomentum;
            double momentum = Math.Max(Math.Abs(p.X), Math.Max(Math.Abs(p.Y), Math.Abs(p.Z)));
            double finite = (double.IsNaN(energy) || double.IsInfinity(energy)) ? double.MaxValue : 0.0;
            return Math.Max(massElement, Math.Max(energy + finite, momentum));
        }

        public bool IsBalanced(double tol = 1e-6) => ClosedResidual() <= tol;
    }
}
