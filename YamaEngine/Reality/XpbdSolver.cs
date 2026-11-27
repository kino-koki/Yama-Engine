using System;
namespace YamaEngine.Reality
{
    /// <summary>
    /// XPBD（Extended Position Based Dynamics）统一约束求解器。
    /// 同一套 Δλ 公式处理距离、关节与地面接触；alphaTilde = compliance / dt^2
    /// 使刚度与子步数无关，这是相对手写冲量法最关键的物理正确性改进。
    /// 所有循环按索引升序，无字典遍历依赖 → 逐位确定性。
    /// </summary>
    public sealed class XpbdSolver
    {
        public struct Particle
        {
            public RVec3 Position;
            public RVec3 Previous;
            public RVec3 Velocity;
            public float InvMass;
        }

        public struct DistanceConstraint
        {
            public int A, B;
            public float RestLength;
            public float Compliance;
            public float Lambda;
        }

        private Particle[] _p = Array.Empty<Particle>();
        private DistanceConstraint[] _c = Array.Empty<DistanceConstraint>();
        private float _gravity = -9.81f;
        private float _damping = 0.999f;

        public int ParticleCount => _p.Length;
        public int ConstraintCount => _c.Length;
        public float Gravity { get => _gravity; set => _gravity = value; }

        public void SetParticles(Particle[] particles) => _p = particles ?? Array.Empty<Particle>();
        public void SetConstraints(DistanceConstraint[] constraints) => _c = constraints ?? Array.Empty<DistanceConstraint>();
        public Particle[] Particles => _p;
        public DistanceConstraint[] Constraints => _c;

        public void Step(float dt, int substeps, int iterations, float groundY)
        {
            if (dt <= 0f || substeps < 1) throw new ArgumentOutOfRangeException(nameof(dt));
            float h = dt / substeps;
            float alphaScale = 1f / (h * h);
            for (int s = 0; s < substeps; s++)
            {
                for (int i = 0; i < _p.Length; i++)
                {
                    ref var p = ref _p[i];
                    if (p.InvMass <= 0f) continue;
                    p.Previous = p.Position;
                    p.Velocity = p.Velocity + new RVec3(0f, _gravity * h, 0f);
                    p.Position = p.Position + p.Velocity * h;
                }
                for (int k = 0; k < _c.Length; k++) { ref var c = ref _c[k]; c.Lambda = 0f; }

                for (int it = 0; it < iterations; it++)
                {
                    for (int k = 0; k < _c.Length; k++) SolveDistance(k, alphaScale);
                    for (int i = 0; i < _p.Length; i++) SolveGround(i, groundY);
                }

                for (int i = 0; i < _p.Length; i++)
                {
                    ref var p = ref _p[i];
                    if (p.InvMass <= 0f) continue;
                    p.Velocity = (p.Position - p.Previous) * (1f / h) * _damping;
                }
            }
        }

        private void SolveDistance(int k, float alphaScale)
        {
            ref var c = ref _c[k];
            if (c.A < 0 || c.B < 0 || c.A >= _p.Length || c.B >= _p.Length) return;
            ref var a = ref _p[c.A]; ref var b = ref _p[c.B];
            float w = a.InvMass + b.InvMass;
            if (w <= 0f) return;
            RVec3 d = a.Position - b.Position;
            float len = d.Length();
            if (len <= 1e-9f) return;
            float C = len - c.RestLength;
            RVec3 n = d * (1f / len);
            float alphaTilde = c.Compliance * alphaScale;
            float dLambda = (-C - alphaTilde * c.Lambda) / (w + alphaTilde);
            c.Lambda += dLambda;
            a.Position = a.Position + n * (a.InvMass * dLambda);
            b.Position = b.Position - n * (b.InvMass * dLambda);
        }

        private void SolveGround(int i, float groundY)
        {
            ref var p = ref _p[i];
            if (p.InvMass <= 0f) return;
            float C = p.Position.Y - groundY;
            if (C >= 0f) return; // 单侧约束
            float dLambda = -C / p.InvMass; // 刚性单侧接触
            p.Position = p.Position + new RVec3(0f, p.InvMass * dLambda, 0f);
        }

        public float TotalKineticEnergy()
        {
            double e = 0.0;
            for (int i = 0; i < _p.Length; i++)
            {
                ref var p = ref _p[i];
                if (p.InvMass <= 0f) continue;
                double m = 1.0 / p.InvMass;
                e += 0.5 * m * p.Velocity.LengthSq();
            }
            return (float)e;
        }
    }
}
