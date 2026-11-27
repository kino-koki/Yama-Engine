using System;
namespace YamaEngine.Reality
{
    /// <summary>
    /// Position-Based Fluids：以密度约束强制不可压缩性（相对旧格点扩散是本质升级）。
    /// 预测 → 邻域 → ρ/λ → 含人工压力的 Δp → 位置修正 → XSPH 粘性。
    /// 核函数与梯度系数来自 3D SPH 标准归一化，质量/密度可核算。
    /// </summary>
    public sealed class PbfFluid
    {
        private readonly SpatialHashGrid _grid = new SpatialHashGrid();
        private int[][] _neighbors = Array.Empty<int[]>();
        private float[] _density = Array.Empty<float>();
        private float[] _lambda = Array.Empty<float>();
        private float[] _dx = Array.Empty<float>();
        private float[] _dy = Array.Empty<float>();
        private float[] _dz = Array.Empty<float>();
        private float[] _px = Array.Empty<float>();
        private float[] _py = Array.Empty<float>();
        private float[] _pz = Array.Empty<float>();
        private int _n;

        public float RestDensity { get; set; } = 1000f;
        public float ParticleMass { get; set; } = 1f;
        public float SmoothingLength { get; set; } = 0.2f;
        public float Epsilon { get; set; } = 1e-4f;
        public float ArtificialPressureK { get; set; } = 0.001f;
        public float XsphViscosity { get; set; } = 0.02f;
        public float[] Density => _density;

        public void Step(float[] px, float[] py, float[] pz,
                         float[] vx, float[] vy, float[] vz,
                         float dt, int iterations, float gravity,
                         float[]? buoyancyAccel = null)
        {
            if (px == null || py == null || pz == null) throw new ArgumentNullException(nameof(px));
            _n = Math.Min(px.Length, Math.Min(py.Length, pz.Length));
            if (vx == null || vy == null || vz == null) throw new ArgumentNullException(nameof(vx));
            if (vx.Length < _n || vy.Length < _n || vz.Length < _n) throw new ArgumentException("速度数组长度不足");
            EnsureCapacity();
            float h = SmoothingLength;
            float poly6 = RealityKernels.Poly6Coef(h);
            float spiky = RealityKernels.SpikyGradCoef(h);

            for (int i = 0; i < _n; i++)
            {
                _px[i] = px[i]; _py[i] = py[i]; _pz[i] = pz[i];
                float accelY = gravity + (buoyancyAccel != null && i < buoyancyAccel.Length
                                          ? buoyancyAccel[i] : 0f);
                vy[i] += accelY * dt;
                px[i] += vx[i] * dt; py[i] += vy[i] * dt; pz[i] += vz[i] * dt;
            }

            _grid.Build(px, py, pz, _n, h);
            for (int i = 0; i < _n; i++) _neighbors[i] = _grid.NeighborsOf(i);

            for (int it = 0; it < iterations; it++)
            {
                for (int i = 0; i < _n; i++) ComputeDensityLambda(i, poly6, spiky);
                for (int i = 0; i < _n; i++) ComputeDeltaPosition(i, spiky, poly6);
                for (int i = 0; i < _n; i++)
                {
                    px[i] += _dx[i]; py[i] += _dy[i]; pz[i] += _dz[i];
                }
            }

            for (int i = 0; i < _n; i++)
            {
                vx[i] = (px[i] - _px[i]) / dt;
                vy[i] = (py[i] - _py[i]) / dt;
                vz[i] = (pz[i] - _pz[i]) / dt;
            }
            ApplyXsph(vx, vy, vz, poly6);
        }

        private void EnsureCapacity()
        {
            if (_neighbors.Length < _n)
            {
                _neighbors = new int[_n][];
                for (int i = 0; i < _n; i++) _neighbors[i] = Array.Empty<int>();
            }
            if (_density.Length < _n)
            {
                _density = new float[_n]; _lambda = new float[_n];
                _dx = new float[_n]; _dy = new float[_n]; _dz = new float[_n];
                _px = new float[_n]; _py = new float[_n]; _pz = new float[_n];
            }
            for (int i = 0; i < _n; i++) { _dx[i] = 0f; _dy[i] = 0f; _dz[i] = 0f; }
        }

        private void ComputeDensityLambda(int i, float poly6, float spiky)
        {
            float h2 = SmoothingLength * SmoothingLength;
            double rho = ParticleMass * poly6 * h2 * h2 * h2; // 自贡献 (r=0)
            var nb = _neighbors[i];
            double sumGrad2 = 0.0;
            var gi = new RVec3(0f, 0f, 0f);
            for (int t = 0; t < nb.Length; t++)
            {
                int j = nb[t];
                float rx = _px[j] - _px[i], ry = _py[j] - _py[i], rz = _pz[j] - _pz[i];
                float r2 = rx * rx + ry * ry + rz * rz;
                if (r2 >= h2) continue;
                rho += ParticleMass * RealityKernels.Poly6(r2, SmoothingLength, poly6);
                float r = MathF.Sqrt(r2);
                float f = RealityKernels.SpikyGradFactor(r, SmoothingLength, spiky) * ParticleMass / RestDensity;
                var g = new RVec3(rx * f, ry * f, rz * f);
                sumGrad2 += g.LengthSq();
                gi = gi - g;
            }
            sumGrad2 += gi.LengthSq();
            _density[i] = (float)rho;
            float C = (float)(rho / RestDensity) - 1f;
            _lambda[i] = -C / (float)(sumGrad2 + Epsilon);
        }

        private void ComputeDeltaPosition(int i, float spiky, float poly6)
        {
            var nb = _neighbors[i];
            float h = SmoothingLength;
            float h2 = h * h;
            float wdq = RealityKernels.Poly6(0.04f * h2, h, poly6);
            if (wdq <= 1e-12f) wdq = 1e-12f;
            double ax = 0, ay = 0, az = 0;
            for (int t = 0; t < nb.Length; t++)
            {
                int j = nb[t];
                float rx = _px[j] - _px[i], ry = _py[j] - _py[i], rz = _pz[j] - _pz[i];
                float r2 = rx * rx + ry * ry + rz * rz;
                if (r2 >= h2) continue;
                float r = MathF.Sqrt(r2);
                float w = RealityKernels.Poly6(r2, h, poly6);
                float ratio = w / wdq;
                float scorr = -ArtificialPressureK * ratio * ratio * ratio * ratio;
                float f = RealityKernels.SpikyGradFactor(r, h, spiky) * (_lambda[i] + _lambda[j] + scorr);
                ax += rx * f; ay += ry * f; az += rz * f;
            }
            _dx[i] = (float)(ax / RestDensity);
            _dy[i] = (float)(ay / RestDensity);
            _dz[i] = (float)(az / RestDensity);
        }

        private void ApplyXsph(float[] vx, float[] vy, float[] vz, float poly6)
        {
            float h2 = SmoothingLength * SmoothingLength;
            var nvx = new float[_n]; var nvy = new float[_n]; var nvz = new float[_n];
            for (int i = 0; i < _n; i++)
            {
                double ax = 0, ay = 0, az = 0;
                var nb = _neighbors[i];
                for (int t = 0; t < nb.Length; t++)
                {
                    int j = nb[t];
                    float rx = _px[j] - _px[i], ry = _py[j] - _py[i], rz = _pz[j] - _pz[i];
                    float r2 = rx * rx + ry * ry + rz * rz;
                    if (r2 >= h2) continue;
                    float w = RealityKernels.Poly6(r2, SmoothingLength, poly6);
                    ax += (vx[j] - vx[i]) * w; ay += (vy[j] - vy[i]) * w; az += (vz[j] - vz[i]) * w;
                }
                nvx[i] = vx[i] + XsphViscosity * (float)ax;
                nvy[i] = vy[i] + XsphViscosity * (float)ay;
                nvz[i] = vz[i] + XsphViscosity * (float)az;
            }
            for (int i = 0; i < _n; i++) { vx[i] = nvx[i]; vy[i] = nvy[i]; vz[i] = nvz[i]; }
        }

        public float MaxDensityError()
        {
            float m = 0f;
            for (int i = 0; i < _n; i++)
            {
                float e = MathF.Abs(_density[i] / RestDensity - 1f);
                if (e > m) m = e;
            }
            return m;
        }
    }
}
