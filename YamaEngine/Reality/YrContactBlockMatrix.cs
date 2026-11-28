using System;
namespace YamaEngine.Reality
{
    /// <summary>
    /// 第十七轮：跨接触点的全局耦合块矩阵（自研）。
    ///
    /// 第十五轮只解单个接触点内部的 3×3 K；本轮把**同一 (A,B) 对**的 m 个
    /// 接触点组装成一个 3m×3m 对称矩阵，一次 Cholesky 分解后整体求解，
    /// 从而保留「不同接触点之间通过共享刚体产生的冲量耦合」。
    ///
    /// 每点 k 用自己的接触正交基 (n_k, t1_k, t2_k)；块 (k,l) 的元素：
    ///   K_kl[α,β] = (invM_a + invM_b)(d_k^α·d_l^β)
    ///     + (r_a^k×d_k^α)·I_a⁻¹(r_a^l×d_l^β)
    ///     + (r_b^k×d_k^α)·I_b⁻¹(r_b^l×d_l^β)
    /// 由于 I⁻¹ 对称，K_lk = K_klᵀ，整体对称；对角块 K_kk 逐位等于第十四轮
    /// `YrContactMassMatrix3x3.BuildK`（parity 可判定）。
    ///
    /// 不同 (A,B) 对之间没有共享刚体 → 耦合块为零，因此按对分组独立求解
    /// 与全局求解等价；分组按 (minA,maxA,原序) 定序，保证逐位确定。
    ///
    /// 诚实边界：K = J M⁻¹ Jᵀ 在接触行线性相关时半正定（本仓库两体重叠共面
    /// 场景最小特征值可达 1e-8），故求解用**可调 Tikhonov 阻尼**正则化
    /// （默认 1e-3，只加在 Cholesky 的对角主元上，不改 K 的物理含义）。
    /// 这是投影 Jacobi 迭代 + 互补钳制/圆锥投影，**不是精确 LCP/SOCP**；
    /// K 帧内固定，不随姿态更新；摩擦锥仍是二维圆锥投影。
    /// </summary>
    public static class YrContactBlockMatrix
    {
        /// <summary>默认求解阻尼（只作用于 Cholesky 主元，不写回 K）。</summary>
        public const float DefaultDamping = 1e-3f;

        /// <summary>
        /// 自研对称正定 Cholesky：a 为行优先 n×n，返回下三角 L（上三角为 0）。
        /// 主元加 damping 正则化；非正定返回 false 且 lower 为空，调用方据此安全退回。
        /// </summary>
        public static bool Cholesky(float[] a, int n, float damping, out float[] lower)
        {
            lower = new float[n * n];
            for (int i = 0; i < n; i++)
            {
                for (int j = 0; j <= i; j++)
                {
                    float s = a[i * n + j];
                    if (i == j) s += damping;
                    for (int p = 0; p < j; p++) s -= lower[i * n + p] * lower[j * n + p];
                    if (i == j)
                    {
                        if (s <= 1e-12f) { lower = Array.Empty<float>(); return false; }
                        lower[i * n + j] = MathF.Sqrt(s);
                    }
                    else lower[i * n + j] = s / lower[j * n + j];
                }
            }
            return true;
        }

        /// <summary>解 L Lᵀ x = b（前代 + 回代）。</summary>
        public static float[] Solve(float[] lower, int n, float[] b)
        {
            var y = new float[n];
            for (int i = 0; i < n; i++)
            {
                float s = b[i];
                for (int p = 0; p < i; p++) s -= lower[i * n + p] * y[p];
                y[i] = s / lower[i * n + i];
            }
            var x = new float[n];
            for (int i = n - 1; i >= 0; i--)
            {
                float s = y[i];
                for (int p = i + 1; p < n; p++) s -= lower[p * n + i] * x[p];
                x[i] = s / lower[i * n + i];
            }
            return x;
        }

        /// <summary>
        /// 组装同一 (A,B) 对、m 个接触点的 3m×3m 全局 K。
        /// 末尾做一次对称化，消除 float32 构造顺序带来的 ~1ulp 不对称
        /// （半正定矩阵对这点误差极敏感，不对称会让 Cholesky 主元假性变负）。
        /// </summary>
        public static float[] BuildGlobalK(YrContact[] contacts, int[] idx, int m,
                                           RigidBody3D[] bodies,
                                           out RVec3[] ns, out RVec3[] t1s, out RVec3[] t2s)
        {
            int n = 3 * m;
            var k = new float[n * n];
            ns = new RVec3[m];
            t1s = new RVec3[m];
            t2s = new RVec3[m];
            for (int q = 0; q < m; q++)
            {
                ns[q] = contacts[idx[q]].Normal.Normalized();
                var basis = YrContactSolver.Basis(ns[q]);
                t1s[q] = basis.T1;
                t2s[q] = basis.T2;
            }

            var c0 = contacts[idx[0]];
            RigidBody3D a = bodies[c0.BodyA], b = bodies[c0.BodyB];
            float invMSum = a.InvMass + b.InvMass;

            for (int q = 0; q < m; q++)
            {
                int i = idx[q];
                RVec3 ra = contacts[i].Point - a.Position;
                RVec3 rb = contacts[i].Point - b.Position;
                var di = new RVec3[] { ns[q], t1s[q], t2s[q] };
                for (int r = 0; r < m; r++)
                {
                    int j = idx[r];
                    RVec3 ra2 = contacts[j].Point - a.Position;
                    RVec3 rb2 = contacts[j].Point - b.Position;
                    var dj = new RVec3[] { ns[r], t1s[r], t2s[r] };
                    for (int x = 0; x < 3; x++)
                    {
                        for (int y = 0; y < 3; y++)
                        {
                            float v = invMSum * di[x].Dot(dj[y]);
                            RVec3 ca = ra.Cross(di[x]), da = ra2.Cross(dj[y]);
                            v += ca.Dot(YrContactEffectiveMass.ApplyWorldInvInertia(
                                    a.Orientation, a.InvInertiaLocal, da));
                            RVec3 cb = rb.Cross(di[x]), db = rb2.Cross(dj[y]);
                            v += cb.Dot(YrContactEffectiveMass.ApplyWorldInvInertia(
                                    b.Orientation, b.InvInertiaLocal, db));
                            k[(3 * q + x) * n + (3 * r + y)] = v;
                        }
                    }
                }
            }

            // 对称化：只融合上/下三角，不改对角块语义（对角块本身已对称）。
            for (int i = 0; i < n; i++)
            {
                for (int j = i + 1; j < n; j++)
                {
                    float v = 0.5f * (k[i * n + j] + k[j * n + i]);
                    k[i * n + j] = v;
                    k[j * n + i] = v;
                }
            }
            return k;
        }

        /// <summary>最大跨块 Frobenius 范数（可判定「确实组装了跨点耦合」）。</summary>
        public static float MaxCrossBlockNorm(float[] k, int m)
        {
            int n = 3 * m;
            float best = 0f;
            for (int q = 0; q < m; q++)
            {
                for (int r = 0; r < m; r++)
                {
                    if (q == r) continue;
                    float s = 0f;
                    for (int x = 0; x < 3; x++)
                        for (int y = 0; y < 3; y++)
                        {
                            float v = k[(3 * q + x) * n + (3 * r + y)];
                            s += v * v;
                        }
                    s = MathF.Sqrt(s);
                    if (s > best) best = s;
                }
            }
            return best;
        }
    }
}
