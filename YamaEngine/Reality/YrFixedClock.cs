using System;
namespace YamaEngine.Reality
{
    /// <summary>
    /// 固定物理步累加器（Godot 3 `_physics_process` 语义）：
    /// 真实帧 dt 可变，物理必须按固定 dt 推进并给出渲染插值 alpha。
    /// 带「死亡螺旋」保护：单帧超过 maxSubSteps 的部分丢弃而非无限追帧。
    /// 用 double 累加，避免 float 累积漂移破坏回放一致性。
    /// </summary>
    public sealed class YrFixedClock
    {
        private readonly double _fixedDt;
        private readonly int _maxSubSteps;
        private double _accumulator;
        private long _tick;

        public YrFixedClock(double fixedDt = 1.0 / 60.0, int maxSubSteps = 8)
        {
            if (fixedDt <= 0.0) throw new ArgumentOutOfRangeException(nameof(fixedDt));
            _fixedDt = fixedDt;
            _maxSubSteps = Math.Max(1, maxSubSteps);
        }

        public float FixedDt => (float)_fixedDt;
        public long Tick => _tick;
        public int MaxSubSteps => _maxSubSteps;
        public double Accumulator => _accumulator;

        /// <summary>渲染插值系数 ∈ [0,1)。</summary>
        public float InterpolationAlpha()
        {
            double a = _accumulator / _fixedDt;
            return (float)RealityKernels.Clamp01((float)a);
        }

        /// <summary>推进真实帧时间，返回本帧应执行的固定步数。</summary>
        public int Advance(double realDt)
        {
            if (realDt < 0.0) throw new ArgumentOutOfRangeException(nameof(realDt));
            _accumulator += realDt;
            int steps = (int)Math.Floor(_accumulator / _fixedDt + 1e-12);
            if (steps > _maxSubSteps)
            {
                // 超出部分不补跑，避免卡顿→更多步→更卡顿的死亡螺旋
                _accumulator -= (steps - _maxSubSteps) * _fixedDt;
                steps = _maxSubSteps;
            }
            if (steps > 0) _accumulator -= steps * _fixedDt;
            if (_accumulator < 0.0 && _accumulator > -1e-12) _accumulator = 0.0;
            _tick += steps;
            return steps;
        }

        public void Reset() { _accumulator = 0.0; _tick = 0; }
    }
}
