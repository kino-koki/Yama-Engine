using System;
namespace YamaEngine.Reality
{
    /// <summary>
    /// 线性/刚性形状匹配约束（Müller 2005 的 XPBD 形式）。
    /// 与「逐对距离约束」不同，它用整体形变梯度 A = (Σ p qᵀ)(Σ q qᵀ)⁻¹
    /// 生成每个点的目标位置，能表达体积/剪切/旋转的整体恢复，是软体与
    /// 布料不塌陷的关键。A 可再经极分解提取旋转，得到纯刚性目标。
    /// </summary>
    public sealed class YrShapeMatch
    {
        private float[] _rx = Array.Empty<float>();
        private float[] _ry = Array.Empty<float>();
        private float[] _rz = Array.Empty<float>();
        private YrMat3 _restInv;
        private RVec3 _restCm;
        private bool _degX, _degY, _degZ;
        private int _n;

        public int Count => _n;
        public bool RigidOnly { get; set; } = false;
        public float Stiffness { get; set; } = 0.8f;

        public void Initialize(float[] rx, float[] ry, float[] rz)
        {
            if (rx == null || ry == null || rz == null) throw new ArgumentNullException(nameof(rx));
            _n = Math.Min(rx.Length, Math.Min(ry.Length, rz.Length));
            if (_n < 1) throw new ArgumentException("形状至少需要 1 个点");
            _rx = new float[_n]; _ry = new float[_n]; _rz = new float[_n];
            double cx = 0, cy = 0, cz = 0;
            for (int i = 0; i < _n; i++)
            {
                _rx[i] = rx[i]; _ry[i] = ry[i]; _rz[i] = rz[i];
                cx += rx[i]; cy += ry[i]; cz += rz[i];
            }
            _restCm = new RVec3((float)(cx / _n), (float)(cy / _n), (float)(cz / _n));

            // Σ q qᵀ（q 为相对质心的 rest 偏移）
            double s00 = 0, s01 = 0, s02 = 0, s10 = 0, s11 = 0, s12 = 0, s20 = 0, s21 = 0, s22 = 0;
            for (int i = 0; i < _n; i++)
            {
                float x = _rx[i] - _restCm.X, y = _ry[i] - _restCm.Y, z = _rz[i] - _restCm.Z;
                s00 += (double)x * x; s01 += (double)x * y; s02 += (double)x * z;
                s10 += (double)y * x; s11 += (double)y * y; s12 += (double)y * z;
                s20 += (double)z * x; s21 += (double)z * y; s22 += (double)z * z;
            }
            // 共面/共线点云的 Σqqᵀ 奇异，直接求逆会失败并让形变梯度失真。
            // Tikhonov 正则化保住非退化方向；缺失方向在 DeformationGradient 里
            // 假设「无变形」（取单位行），这是信息不足时的最小假设。
            double trace = s00 + s11 + s22;
            double eps = trace * 1e-9 + 1e-12;
            _degX = s00 < eps; _degY = s11 < eps; _degZ = s22 < eps;
            float lambda = (float)(trace * 1e-6 + 1e-9);
            var cov = new YrMat3((float)s00 + lambda, (float)s01, (float)s02,
                                 (float)s10, (float)s11 + lambda, (float)s12,
                                 (float)s20, (float)s21, (float)s22 + lambda);
            _restInv = cov.TryInverse(out YrMat3 inv) ? inv : YrMat3.Identity;
        }

        /// <summary>由当前点云计算形变梯度 A（含剪切/体积/旋转）。</summary>
        public YrMat3 DeformationGradient(float[] px, float[] py, float[] pz)
        {
            if (px == null) throw new ArgumentNullException(nameof(px));
            if (px.Length < _n || py.Length < _n || pz.Length < _n)
                throw new ArgumentException("位置数组长度不足");
            double cx = 0, cy = 0, cz = 0;
            for (int i = 0; i < _n; i++) { cx += px[i]; cy += py[i]; cz += pz[i]; }
            var cm = new RVec3((float)(cx / _n), (float)(cy / _n), (float)(cz / _n));
            double a00 = 0, a01 = 0, a02 = 0, a10 = 0, a11 = 0, a12 = 0, a20 = 0, a21 = 0, a22 = 0;
            for (int i = 0; i < _n; i++)
            {
                float x = px[i] - cm.X, y = py[i] - cm.Y, z = pz[i] - cm.Z;
                float qx = _rx[i] - _restCm.X, qy = _ry[i] - _restCm.Y, qz = _rz[i] - _restCm.Z;
                a00 += (double)x * qx; a01 += (double)x * qy; a02 += (double)x * qz;
                a10 += (double)y * qx; a11 += (double)y * qy; a12 += (double)y * qz;
                a20 += (double)z * qx; a21 += (double)z * qy; a22 += (double)z * qz;
            }
            var apq = new YrMat3((float)a00, (float)a01, (float)a02,
                                 (float)a10, (float)a11, (float)a12,
                                 (float)a20, (float)a21, (float)a22);
            // A = Apq · (Σqqᵀ)⁻¹  →  行主序下等价于 (restInvᵀ · apqᵀ)ᵀ
            YrMat3 at = apq.Transpose();
            YrMat3 rt = _restInv.Transpose();
            // (rt · at)ᵀ = atᵀ · rtᵀ = apq · restInv
            YrMat3 prod = Multiply(rt, at);
            YrMat3 a = prod.Transpose();
            if (_degX || _degY || _degZ)
                a = new YrMat3(
                    _degX ? 1f : a.M00, _degX ? 0f : a.M01, _degX ? 0f : a.M02,
                    _degY ? 0f : a.M10, _degY ? 1f : a.M11, _degY ? 0f : a.M12,
                    _degZ ? 0f : a.M20, _degZ ? 0f : a.M21, _degZ ? 1f : a.M22);
            return a;
        }

        private static YrMat3 Multiply(YrMat3 a, YrMat3 b) => new YrMat3(
            a.M00 * b.M00 + a.M01 * b.M10 + a.M02 * b.M20,
            a.M00 * b.M01 + a.M01 * b.M11 + a.M02 * b.M21,
            a.M00 * b.M02 + a.M01 * b.M12 + a.M02 * b.M22,
            a.M10 * b.M00 + a.M11 * b.M10 + a.M12 * b.M20,
            a.M10 * b.M01 + a.M11 * b.M11 + a.M12 * b.M21,
            a.M10 * b.M02 + a.M11 * b.M12 + a.M12 * b.M22,
            a.M20 * b.M00 + a.M21 * b.M10 + a.M22 * b.M20,
            a.M20 * b.M01 + a.M21 * b.M11 + a.M22 * b.M21,
            a.M20 * b.M02 + a.M21 * b.M12 + a.M22 * b.M22);

        /// <summary>把点云向形状目标修正一次（XPBD 式 stiffness 混合）。</summary>
        public void Solve(float[] px, float[] py, float[] pz)
        {
            YrMat3 a = DeformationGradient(px, py, pz);
            if (RigidOnly) a = YrMat3.ExtractRotation(a);
            double cx = 0, cy = 0, cz = 0;
            for (int i = 0; i < _n; i++) { cx += px[i]; cy += py[i]; cz += pz[i]; }
            var cm = new RVec3((float)(cx / _n), (float)(cy / _n), (float)(cz / _n));
            float s = RealityKernels.Clamp01(Stiffness);
            for (int i = 0; i < _n; i++)
            {
                var q = new RVec3(_rx[i] - _restCm.X, _ry[i] - _restCm.Y, _rz[i] - _restCm.Z);
                RVec3 g = cm + a.Multiply(q);
                px[i] += (g.X - px[i]) * s;
                py[i] += (g.Y - py[i]) * s;
                pz[i] += (g.Z - pz[i]) * s;
            }
        }
    }
}
