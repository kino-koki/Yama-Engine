using System;
namespace YamaEngine.Reality
{
    /// <summary>自研对称 3×3 矩阵（6 个独立分量）。</summary>
    public readonly struct YrMat3Sym
    {
        public readonly float M00, M01, M02, M11, M12, M22;
        public YrMat3Sym(float m00, float m01, float m02,
                         float m11, float m12, float m22)
        { M00 = m00; M01 = m01; M02 = m02; M11 = m11; M12 = m12; M22 = m22; }

        public float At(int i, int j)
        {
            if (i > j) { int t = i; i = j; j = t; }
            if (i == 0) return j == 0 ? M00 : (j == 1 ? M01 : M02);
            if (i == 1) return j == 1 ? M11 : M12;
            return M22;
        }
        public static YrMat3Sym Identity => new YrMat3Sym(1f, 0f, 0f, 1f, 0f, 0f);
    }

    /// <summary>自研完整 3×3 矩阵（用于 Cholesky 下三角 / Q 特征向量）。</summary>
    public readonly struct YrMat3Full
    {
        public readonly float A00, A01, A02, A10, A11, A12, A20, A21, A22;
        public YrMat3Full(float a00, float a01, float a02,
                      float a10, float a11, float a12,
                      float a20, float a21, float a22)
        { A00 = a00; A01 = a01; A02 = a02; A10 = a10; A11 = a11; A12 = a12;
          A20 = a20; A21 = a21; A22 = a22; }
        public float At(int i, int j)
        {
            if (i == 0) return j == 0 ? A00 : (j == 1 ? A01 : A02);
            if (i == 1) return j == 0 ? A10 : (j == 1 ? A11 : A12);
            return j == 0 ? A20 : (j == 1 ? A21 : A22);
        }
    }

    /// <summary>
    /// 完整 3×3 接触有效质量矩阵 K（第十三轮只取对角项，本轮补非对角耦合）。
    ///
    /// 在接触正交基 (n, t1, t2) 下：
    ///   K_ij = (invM_a + invM_b)(d_i·d_j)
    ///        + (r_a×d_i)·I_a⁻¹(r_a×d_j) + (r_b×d_i)·I_b⁻¹(r_b×d_j)
    /// 平动项因基正交只留对角；**角项产生非对角耦合**，这是第十三轮忽略的部分。
    /// K 对称正定（两体惯量正 + 力臂项半正定）。
    ///
    /// 自研数值路径：Cholesky 解 Kx=b、Jacobi 特征分解求 K^{-1/2}（白化矩阵 C），
    /// 用 C 混合三个 6D wrench 行得到含耦合的 18D 接触 Jacobian 行。
    /// 当 K 恰好对角时，C=diag(1/√K_kk)，逐位退化为第十三轮
    /// `YrContactEffectiveMass.WeightedContactRow`（parity 可判定）。
    ///
    /// 诚实边界：仍是单点接触的 3×3 近似（非逐点独立约束的完整 LCP K 矩阵）；
    /// 摩擦锥缩放沿用一阶近似；Jacobi 是固定 48 次扫描的数值特征分解。
    /// </summary>
    public static class YrContactMassMatrix3x3
    {
        public const int JacobiSweeps = 48;
        public const float EigenTolerance = 1e-9f;

        public static void Basis(RVec3 normal, out RVec3 n, out RVec3 t1, out RVec3 t2)
            => YrContactReduction.TangentBasis(normal, out n, out t1, out t2);

        /// <summary>构造完整 K（6 个独立分量）。</summary>
        public static YrMat3Sym BuildK(in YrContactMass m, RVec3 point)
            => BuildK(m, point, new RVec3(0f, 0f, 1f));

        public static YrMat3Sym BuildK(in YrContactMass m, RVec3 point, RVec3 normal)
        {
            Basis(normal, out RVec3 n, out RVec3 t1, out RVec3 t2);
            return BuildK(m, point, n, t1, t2);
        }

        /// <summary>
        /// 第十五轮：允许调用方指定接触正交基 (n,t1,t2)。
        /// YrContactSolver 用自己的 Basis(n)（seed=(0,1,0)/(1,0,0)）构造，
        /// 与 YrContactReduction.TangentBasis 的基不同；接线时必须显式传入，
        /// 否则 K 的三个方向会与求解器的切向 λ 分量错位。
        /// </summary>
        public static YrMat3Sym BuildK(in YrContactMass m, RVec3 point,
                                       RVec3 n, RVec3 t1, RVec3 t2)
        {
            RVec3[] d = new RVec3[] { n, t1, t2 };
            RVec3 ra = point - m.CenterA, rb = point - m.CenterB;
            float invMSum = m.InvMassA + m.InvMassB;
            var k = new float[9];
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                {
                    float v = invMSum * d[i].Dot(d[j]);
                    RVec3 ca = ra.Cross(d[i]), cb = rb.Cross(d[i]);
                    RVec3 da = ra.Cross(d[j]), db = rb.Cross(d[j]);
                    v += ca.Dot(YrContactEffectiveMass.ApplyWorldInvInertia(
                            m.QA, m.InvInertiaLocalA, da));
                    v += cb.Dot(YrContactEffectiveMass.ApplyWorldInvInertia(
                            m.QB, m.InvInertiaLocalB, db));
                    k[i * 3 + j] = v;
                }
            return new YrMat3Sym(k[0], k[1], k[2], k[4], k[5], k[8]);
        }

        /// <summary>Cholesky：K = L·Lᵀ，返回下三角 L（上三角为 0）。非正定返回 false。</summary>
        public static bool Cholesky(in YrMat3Sym k, out YrMat3Full lower)
        {
            lower = default;
            var l = new float[9];
            for (int i = 0; i < 3; i++)
                for (int j = 0; j <= i; j++)
                {
                    float s = k.At(i, j);
                    for (int p = 0; p < j; p++) s -= l[i * 3 + p] * l[j * 3 + p];
                    if (i == j)
                    {
                        if (s <= 1e-12f) return false;
                        l[i * 3 + j] = MathF.Sqrt(s);
                    }
                    else l[i * 3 + j] = s / l[j * 3 + j];
                }
            lower = new YrMat3Full(l[0], 0f, 0f, l[3], l[4], 0f, l[6], l[7], l[8]);
            return true;
        }

        /// <summary>解 Kx=b（前代 + 回代）。</summary>
        public static RVec3 Solve(in YrMat3Sym k, RVec3 b)
        {
            if (!Cholesky(k, out YrMat3Full l)) return RVec3.Zero;
            float y0 = b.X / l.At(0, 0);
            float y1 = (b.Y - l.At(1, 0) * y0) / l.At(1, 1);
            float y2 = (b.Z - l.At(2, 0) * y0 - l.At(2, 1) * y1) / l.At(2, 2);
            float x2 = y2 / l.At(2, 2);
            float x1 = (y1 - l.At(2, 1) * x2) / l.At(1, 1);
            float x0 = (y0 - l.At(1, 0) * x1 - l.At(2, 0) * x2) / l.At(0, 0);
            return new RVec3(x0, x1, x2);
        }

        /// <summary>
        /// 一次分解同时完成正定判定与求解（第十五轮求解器接线用）。
        /// 非正定返回 false 且 x=Zero，调用方据此安全退回标量路径。
        /// </summary>
        public static bool TrySolve(in YrMat3Sym k, RVec3 b, out RVec3 x)
        {
            if (!Cholesky(k, out YrMat3Full l)) { x = RVec3.Zero; return false; }
            float y0 = b.X / l.At(0, 0);
            float y1 = (b.Y - l.At(1, 0) * y0) / l.At(1, 1);
            float y2 = (b.Z - l.At(2, 0) * y0 - l.At(2, 1) * y1) / l.At(2, 2);
            float x2 = y2 / l.At(2, 2);
            float x1 = (y1 - l.At(2, 1) * x2) / l.At(1, 1);
            float x0 = (y0 - l.At(1, 0) * x1 - l.At(2, 0) * x2) / l.At(0, 0);
            x = new RVec3(x0, x1, x2);
            return true;
        }

        /// <summary>自研 cyclic Jacobi 对称特征分解：QᵀKQ = diag(lambda)。</summary>
        public static void JacobiEigen(in YrMat3Sym k, out RVec3 lambda, out YrMat3Full q)
        {
            var a = new float[9] { k.M00, k.M01, k.M02,
                                   k.M01, k.M11, k.M12,
                                   k.M02, k.M12, k.M22 };
            var v = new float[9] { 1f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f };
            for (int sweep = 0; sweep < JacobiSweeps; sweep++)
            {
                int p = 0, qq = 1; float best = MathF.Abs(a[1]);
                if (MathF.Abs(a[2]) > best) { best = MathF.Abs(a[2]); p = 0; qq = 2; }
                if (MathF.Abs(a[5]) > best) { best = MathF.Abs(a[5]); p = 1; qq = 2; }
                if (best <= EigenTolerance) break;
                float app = a[p * 3 + p], aqq = a[qq * 3 + qq], apq = a[p * 3 + qq];
                float theta = 0.5f * MathF.Atan2(2f * apq, aqq - app);
                float c = MathF.Cos(theta), s = MathF.Sin(theta);
                for (int r = 0; r < 3; r++)
                {
                    float arp = a[r * 3 + p], arq = a[r * 3 + qq];
                    a[r * 3 + p] = c * arp - s * arq;
                    a[r * 3 + qq] = s * arp + c * arq;
                }
                for (int r = 0; r < 3; r++)
                {
                    float apr = a[p * 3 + r], aqr = a[qq * 3 + r];
                    a[p * 3 + r] = c * apr - s * aqr;
                    a[qq * 3 + r] = s * apr + c * aqr;
                }
                for (int r = 0; r < 3; r++)
                {
                    float vrp = v[r * 3 + p], vrq = v[r * 3 + qq];
                    v[r * 3 + p] = c * vrp - s * vrq;
                    v[r * 3 + qq] = s * vrp + c * vrq;
                }
            }
            lambda = new RVec3(a[0], a[4], a[8]);
            q = new YrMat3Full(v[0], v[1], v[2], v[3], v[4], v[5], v[6], v[7], v[8]);
        }

        /// <summary>K^{-1/2} = Q·diag(1/√λ)·Qᵀ（对称白化矩阵）。</summary>
        public static YrMat3Sym InverseSqrt(in YrMat3Sym k)
        {
            JacobiEigen(k, out RVec3 lam, out YrMat3Full q);
            float s0 = lam.X > 1e-9f ? 1f / MathF.Sqrt(lam.X) : 0f;
            float s1 = lam.Y > 1e-9f ? 1f / MathF.Sqrt(lam.Y) : 0f;
            float s2 = lam.Z > 1e-9f ? 1f / MathF.Sqrt(lam.Z) : 0f;
            var c = new float[9];
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                {
                    float v = q.At(i, 0) * q.At(j, 0) * s0
                            + q.At(i, 1) * q.At(j, 1) * s1
                            + q.At(i, 2) * q.At(j, 2) * s2;
                    c[i * 3 + j] = v;
                }
            return new YrMat3Sym(c[0], c[1], c[2], c[4], c[5], c[8]);
        }

        public static RVec3 Mul(in YrMat3Sym m, RVec3 v)
            => new RVec3(m.M00 * v.X + m.M01 * v.Y + m.M02 * v.Z,
                         m.M01 * v.X + m.M11 * v.Y + m.M12 * v.Z,
                         m.M02 * v.X + m.M12 * v.Y + m.M22 * v.Z);

        /// <summary>
        /// 含非对角耦合的 18D 接触行：3 个原始 6D wrench 行按 K^{-1/2} 混合，
        /// 再乘摩擦锥缩放。K 对角时逐位退化为第十三轮 WeightedContactRow。
        /// </summary>
        public static float[] CoupledContactRow(in YrContactMass m,
                                                 RVec3 point, RVec3 normal)
        {
            Basis(normal, out RVec3 n, out RVec3 t1, out RVec3 t2);
            RVec3[] d = new RVec3[] { n, t1, t2 };
            RVec3 r = point - m.CenterA;
            YrMat3Sym k = BuildK(m, point, normal);
            YrMat3Sym c = InverseSqrt(k);
            float kn = MathF.Max(k.At(0, 0), 1e-9f);
            var row = new float[18];
            for (int i = 0; i < 3; i++)
            {
                float kk = MathF.Max(k.At(i, i), 1e-9f);
                float cone = i == 0 ? 1f
                    : MathF.Min(1f, MathF.Max(0f, m.Mu * MathF.Sqrt(kn / kk)));
                for (int j = 0; j < 3; j++)
                {
                    float w = c.At(i, j) * cone;
                    RVec3 v = d[j];
                    RVec3 cr = r.Cross(v);
                    int o = i * 6;
                    row[o + 0] += v.X * w;
                    row[o + 1] += v.Y * w;
                    row[o + 2] += v.Z * w;
                    row[o + 3] += cr.X * w;
                    row[o + 4] += cr.Y * w;
                    row[o + 5] += cr.Z * w;
                }
            }
            float norm = 0f;
            for (int i = 0; i < 18; i++) norm += row[i] * row[i];
            norm = MathF.Sqrt(norm);
            if (norm > 1e-9f)
                for (int i = 0; i < 18; i++) row[i] /= norm;
            return row;
        }

        /// <summary>按耦合行做严格 MGS 缩减，回填 3D 点。</summary>
        public static YrContactReduction.YrReducedPoint[] ReduceByCoupledMass(
            RVec3[] points, int[] ids, float[] depths, RVec3 normal,
            in YrContactMass mass, int max = 4)
        {
            if (points == null) throw new ArgumentNullException(nameof(points));
            int n = Math.Min(points.Length,
                             Math.Min(ids == null ? 0 : ids.Length,
                                      depths == null ? 0 : depths.Length));
            if (n == 0 || max <= 0) return Array.Empty<YrContactReduction.YrReducedPoint>();
            var rows = new float[n][];
            for (int i = 0; i < n; i++)
                rows[i] = CoupledContactRow(mass, points[i], normal);
            var red = YrContactReduction.ReduceByRows(rows, ids, depths, max);
            var byId = new Dictionary<int, RVec3>();
            for (int i = 0; i < n; i++) byId[ids[i]] = points[i];
            var result = new YrContactReduction.YrReducedPoint[red.Length];
            for (int i = 0; i < red.Length; i++)
            {
                RVec3 pt = byId.TryGetValue(red[i].Id, out RVec3 v) ? v : RVec3.Zero;
                result[i] = new YrContactReduction.YrReducedPoint(pt, red[i].Id, red[i].Depth);
            }
            return result;
        }
    }
}
