using System;
namespace YamaEngine.Reality
{
    /// <summary>
    /// 四面体符号体积约束（XPBD）。距离约束只能保边长，整体仍可压扁到零体积；
    /// 体积约束用 signed volume 的解析梯度直接恢复体积，是软体/可变形体的关键。
    ///
    /// V = (p1-p0)·((p2-p0)×(p3-p0)) / 6
    /// ∇V(p1) = ((p2-p0)×(p3-p0))/6，∇V(p0) = -Σ∇V(其余)
    /// 因此 Σ∇V = 0 —— 平移不变，约束自身不产生净外力（可逐项验证）。
    /// </summary>
    public sealed class YrVolumeConstraint
    {
        private int[] _tets = Array.Empty<int>();     // 每 4 个索引一个四面体
        private float[] _rest = Array.Empty<float>();
        private float[] _lambda = Array.Empty<float>();
        private float _compliance;

        public int TetrahedronCount => _rest.Length;
        public float Compliance => _compliance;

        public void Initialize(int[] tetrahedra, float[] px, float[] py, float[] pz,
                               float compliance)
        {
            if (tetrahedra == null || tetrahedra.Length % 4 != 0)
                throw new ArgumentException("四面体索引必须按 4 个一组");
            if (px == null || py == null || pz == null) throw new ArgumentNullException(nameof(px));
            _tets = (int[])tetrahedra.Clone();
            _compliance = MathF.Max(0f, compliance);
            int n = _tets.Length / 4;
            _rest = new float[n]; _lambda = new float[n];
            for (int t = 0; t < n; t++) _rest[t] = Volume(t, px, py, pz);
        }

        /// <summary>单个四面体有符号体积。</summary>
        public float Volume(int t, float[] px, float[] py, float[] pz)
        {
            int i0 = _tets[t * 4], i1 = _tets[t * 4 + 1];
            int i2 = _tets[t * 4 + 2], i3 = _tets[t * 4 + 3];
            RVec3 p0 = new RVec3(px[i0], py[i0], pz[i0]);
            RVec3 a = new RVec3(px[i1], py[i1], pz[i1]) - p0;
            RVec3 b = new RVec3(px[i2], py[i2], pz[i2]) - p0;
            RVec3 c = new RVec3(px[i3], py[i3], pz[i3]) - p0;
            return a.Dot(b.Cross(c)) / 6f;
        }

        public float TotalVolume(float[] px, float[] py, float[] pz)
        {
            float sum = 0f;
            for (int t = 0; t < _rest.Length; t++) sum += Volume(t, px, py, pz);
            return sum;
        }

        /// <summary>暴露四个梯度，用于断言平移不变性 Σg=0。</summary>
        public RVec3[] Gradients(int t, float[] px, float[] py, float[] pz)
        {
            int i0 = _tets[t * 4], i1 = _tets[t * 4 + 1];
            int i2 = _tets[t * 4 + 2], i3 = _tets[t * 4 + 3];
            RVec3 p0 = new RVec3(px[i0], py[i0], pz[i0]);
            RVec3 p1 = new RVec3(px[i1], py[i1], pz[i1]);
            RVec3 p2 = new RVec3(px[i2], py[i2], pz[i2]);
            RVec3 p3 = new RVec3(px[i3], py[i3], pz[i3]);
            RVec3 g1 = (p2 - p0).Cross(p3 - p0) * (1f / 6f);
            RVec3 g2 = (p3 - p0).Cross(p1 - p0) * (1f / 6f);
            RVec3 g3 = (p1 - p0).Cross(p2 - p0) * (1f / 6f);
            RVec3 g0 = -((g1 + g2) + g3);
            return new RVec3[] { g0, g1, g2, g3 };
        }

        public void Solve(float[] px, float[] py, float[] pz,
                          float[]? invMass, float dt, int iterations)
        {
            if (dt <= 0f) throw new ArgumentOutOfRangeException(nameof(dt));
            if (px == null || py == null || pz == null) throw new ArgumentNullException(nameof(px));
            float alphaTilde = _compliance / (dt * dt);
            for (int it = 0; it < iterations; it++)
            {
                for (int t = 0; t < _rest.Length; t++)
                {
                    int[] idx = new int[] {
                        _tets[t * 4], _tets[t * 4 + 1], _tets[t * 4 + 2], _tets[t * 4 + 3]
                    };
                    RVec3[] g = Gradients(t, px, py, pz);
                    float denom = 0f;
                    for (int q = 0; q < 4; q++)
                    {
                        float w = invMass != null && idx[q] < invMass.Length
                            ? MathF.Max(0f, invMass[idx[q]]) : 1f;
                        denom += w * g[q].LengthSq();
                    }
                    if (denom <= 1e-12f) continue;
                    float C = Volume(t, px, py, pz) - _rest[t];
                    float dLambda = (-C - alphaTilde * _lambda[t]) / (denom + alphaTilde);
                    _lambda[t] += dLambda;
                    for (int q = 0; q < 4; q++)
                    {
                        float w = invMass != null && idx[q] < invMass.Length
                            ? MathF.Max(0f, invMass[idx[q]]) : 1f;
                        RVec3 d = g[q] * (w * dLambda);
                        px[idx[q]] += d.X; py[idx[q]] += d.Y; pz[idx[q]] += d.Z;
                    }
                }
            }
        }
    }
}
