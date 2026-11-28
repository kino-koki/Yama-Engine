using System;
namespace YamaEngine.Reality
{
    /// <summary>第十八轮：全局块线性系统的求解模式（默认 Direct = 第十七轮逐字不变）。</summary>
    public enum YrBlockSolveMode
    {
        Direct = 0,
        BlockJacobi = 1,
        BlockGaussSeidel = 2,
        PreconditionedCG = 3
    }

    /// <summary>
    /// 迭代求解的可观测轨迹（纯观测量，**不进快照**）。
    /// 把「这个 K 到底适不适合迭代解」变成机器可判定的事实：
    /// 残差是否达标、与直接解的解漂移、是否安全退回、残差是否单调。
    /// </summary>
    public readonly struct YrBlockLinearTrace
    {
        public readonly YrBlockSolveMode Mode;
        public readonly int InnerIterations;
        public readonly float InitialResidual;
        public readonly float FinalResidual;
        public readonly float MaxResidual;
        public readonly float SolutionDiscrepancy;
        public readonly bool Converged;
        public readonly bool UsedFallback;
        public readonly bool ResidualMonotonic;
        public YrBlockLinearTrace(YrBlockSolveMode mode, int iters,
                                  float initR, float finalR, float maxR,
                                  float disc, bool converged, bool fallback, bool mono)
        { Mode = mode; InnerIterations = iters;
          InitialResidual = initR; FinalResidual = finalR; MaxResidual = maxR;
          SolutionDiscrepancy = disc; Converged = converged;
          UsedFallback = fallback; ResidualMonotonic = mono; }
    }

    /// <summary>
    /// 第十八轮：3m×3m 全局块系统的迭代求解谱系（自研）。
    ///
    /// 第十七轮用一次 Cholesky 直接解 `(K + damp·I)Δλ = rhs`。本轮补上
    /// 三条迭代路径，以**3×3 块对角**为预条件 M：
    ///   · BlockJacobi        x ← x + M⁻¹(b − Ax)
    ///   · BlockGaussSeidel   逐块立即更新（其余块用最新 x）
    ///   · PreconditionedCG   块预条件共轭梯度（能量范数下降）
    ///
    /// 诚实边界（推 CI 前 float32 预演定标，不放宽）：
    ///   · 良条件 SPD（cond≈2.2）上三种都收敛，GS 比 Jacobi 快；
    ///   · 本仓库物理 K 是**病态**的（两接触点法向行近共线，
    ///     `cond≈3.16e4`，块 Jacobi 谱半径 ≈0.9998），块 Jacobi / GS
    ///     60 步残差仍 >0.5，PCG 也卡在 ~2.5e-4 且**解漂移 ≈0.15**——
    ///     即「残差变小 ≠ 解变准」，这是病态系统的经典陷阱；
    ///   · 因此 `TrySolve` 用**双判据**守门：残差达标 **且** 与直接解的
    ///     漂移 ≤ `DefaultSolutionTolerance`。任一不满足即整组退回
    ///     直接 Cholesky 解，物理结果与第十七轮**逐位一致**，绝不制造
    ///     错误冲量或 NaN。
    ///   · 迭代模式默认关闭；本轮不是「用迭代取代直接解」，而是把
    ///     求解器的适用边界变成可判定事实，并提供安全退回。
    /// </summary>
    public static class YrBlockLinearSolvers
    {
        public const float DefaultTolerance = 1e-5f;
        public const float DefaultSolutionTolerance = 1e-3f;
        public const int DefaultMaxInnerIterations = 60;

        /// <summary>A = K + damp·I（与第十七轮 Cholesky 完全同一线性系统）。</summary>
        public static float[] BuildDampedSystem(float[] k, int n, float damping)
        {
            var a = (float[])k.Clone();
            for (int i = 0; i < n; i++) a[i * n + i] += damping;
            return a;
        }

        /// <summary>A·x（行优先 n×n，float32 逐步累加）。</summary>
        public static float[] Multiply(float[] a, int n, float[] x)
        {
            var o = new float[n];
            for (int i = 0; i < n; i++)
            {
                float s = 0f;
                for (int j = 0; j < n; j++) s += a[i * n + j] * x[j];
                o[i] = s;
            }
            return o;
        }

        public static float[] Residual(float[] a, int n, float[] b, float[] x)
        {
            float[] ax = Multiply(a, n, x);
            var r = new float[n];
            for (int i = 0; i < n; i++) r[i] = b[i] - ax[i];
            return r;
        }

        public static float Norm(float[] v)
        {
            float s = 0f;
            for (int i = 0; i < v.Length; i++) s += v[i] * v[i];
            return MathF.Sqrt(s);
        }

        public static float Dot(float[] u, float[] v)
        {
            float s = 0f;
            for (int i = 0; i < u.Length; i++) s += u[i] * v[i];
            return s;
        }

        /// <summary>
        /// 自研 3×3 块 Cholesky：输入行优先 3×3，返回下三角 L（9 个 float）。
        /// 非正定返回 false，调用方据此安全退回。
        /// </summary>
        public static bool Cholesky3(float[] blk, out float[] lower)
        {
            lower = new float[9];
            for (int i = 0; i < 3; i++)
            {
                for (int j = 0; j <= i; j++)
                {
                    float s = blk[i * 3 + j];
                    for (int p = 0; p < j; p++)
                        s -= lower[i * 3 + p] * lower[j * 3 + p];
                    if (i == j)
                    {
                        if (s <= 1e-12f) { lower = Array.Empty<float>(); return false; }
                        lower[i * 3 + j] = MathF.Sqrt(s);
                    }
                    else lower[i * 3 + j] = s / lower[j * 3 + j];
                }
            }
            return true;
        }

        /// <summary>解 L Lᵀ y = r（3 维前代 + 回代）。</summary>
        public static float[] Solve3(float[] lower, float[] r)
        {
            var y = new float[3];
            for (int i = 0; i < 3; i++)
            {
                float s = r[i];
                for (int p = 0; p < i; p++) s -= lower[i * 3 + p] * y[p];
                y[i] = s / lower[i * 3 + i];
            }
            var x = new float[3];
            for (int i = 2; i >= 0; i--)
            {
                float s = y[i];
                for (int p = i + 1; p < 3; p++) s -= lower[p * 3 + i] * x[p];
                x[i] = s / lower[i * 3 + i];
            }
            return x;
        }

        /// <summary>
        /// 构造 3×3 块对角预条件：对 m 个对角块各做一次 Cholesky，
        /// 结果按块连续存成 9·m 个 float。任一块非正定返回 false。
        /// </summary>
        public static bool BuildBlockPreconditioner(float[] a, int n, int m,
                                                     out float[] lowers)
        {
            lowers = new float[9 * m];
            for (int q = 0; q < m; q++)
            {
                var blk = new float[9];
                for (int i = 0; i < 3; i++)
                    for (int j = 0; j < 3; j++)
                        blk[i * 3 + j] = a[(3 * q + i) * n + (3 * q + j)];
                if (!Cholesky3(blk, out float[] l))
                { lowers = Array.Empty<float>(); return false; }
                for (int t = 0; t < 9; t++) lowers[q * 9 + t] = l[t];
            }
            return true;
        }

        /// <summary>把 M⁻¹ 作用到残差 r 上（逐 3×3 块）。</summary>
        public static float[] ApplyPreconditioner(float[] lowers, int m, float[] r)
        {
            var z = new float[3 * m];
            for (int q = 0; q < m; q++)
            {
                var rq = new float[3] { r[3 * q], r[3 * q + 1], r[3 * q + 2] };
                float[] d = Solve3Block(lowers, q, rq);
                z[3 * q] = d[0]; z[3 * q + 1] = d[1]; z[3 * q + 2] = d[2];
            }
            return z;
        }

        private static float[] Solve3Block(float[] lowers, int q, float[] r)
        {
            var l = new float[9];
            for (int t = 0; t < 9; t++) l[t] = lowers[q * 9 + t];
            return Solve3(l, r);
        }

        /// <summary>
        /// 三条迭代路径。返回迭代解 x，并输出原始诊断量（由 TrySolve 组装最终 trace）。
        /// 全程 float32，与推 CI 前预演逐位同口径。
        /// </summary>
        public static float[] Iterate(YrBlockSolveMode mode, float[] a, int n, int m,
                                      float[] lowers, float[] b,
                                      int maxIt, float tol,
                                      out int iters, out float initR,
                                      out float finalR, out float maxR,
                                      out bool converged, out bool monotonic)
        {
            var x = new float[n];
            initR = Norm(Residual(a, n, b, x));
            maxR = initR;
            finalR = initR;
            iters = 0;
            converged = false;
            monotonic = true;

            if (mode == YrBlockSolveMode.BlockJacobi)
            {
                for (int t = 0; t < maxIt; t++)
                {
                    iters++;
                    float[] r = Residual(a, n, b, x);
                    float[] dx = ApplyPreconditioner(lowers, m, r);
                    for (int i = 0; i < n; i++) x[i] += dx[i];
                    float rr = Norm(Residual(a, n, b, x));
                    if (rr > finalR) monotonic = false;
                    if (rr > maxR) maxR = rr;
                    finalR = rr;
                    if (rr <= tol) { converged = true; break; }
                }
                return x;
            }

            if (mode == YrBlockSolveMode.BlockGaussSeidel)
            {
                for (int t = 0; t < maxIt; t++)
                {
                    iters++;
                    for (int q = 0; q < m; q++)
                    {
                        float[] r = Residual(a, n, b, x);
                        var rq = new float[3] { r[3 * q], r[3 * q + 1], r[3 * q + 2] };
                        float[] d = Solve3Block(lowers, q, rq);
                        x[3 * q] += d[0];
                        x[3 * q + 1] += d[1];
                        x[3 * q + 2] += d[2];
                    }
                    float rr = Norm(Residual(a, n, b, x));
                    if (rr > finalR) monotonic = false;
                    if (rr > maxR) maxR = rr;
                    finalR = rr;
                    if (rr <= tol) { converged = true; break; }
                }
                return x;
            }

            // PreconditionedCG（块对角预条件）
            float[] rv = Residual(a, n, b, x);
            float[] z = ApplyPreconditioner(lowers, m, rv);
            float[] p = (float[])z.Clone();
            float rz = Dot(rv, z);
            for (int t = 0; t < maxIt; t++)
            {
                iters++;
                float[] ap = Multiply(a, n, p);
                float pap = Dot(p, ap);
                float alpha = rz / MathF.Max(pap, 1e-30f);
                for (int i = 0; i < n; i++)
                {
                    x[i] += alpha * p[i];
                    rv[i] -= alpha * ap[i];
                }
                float rr = Norm(Residual(a, n, b, x));
                if (rr > finalR) monotonic = false;
                if (rr > maxR) maxR = rr;
                finalR = rr;
                if (rr <= tol) { converged = true; break; }
                z = ApplyPreconditioner(lowers, m, rv);
                float rzNew = Dot(rv, z);
                float beta = rzNew / MathF.Max(rz, 1e-30f);
                for (int i = 0; i < n; i++) p[i] = z[i] + beta * p[i];
                rz = rzNew;
            }
            return x;
        }

        public static float MaxAbsDiff(float[] u, float[] v)
        {
            float d = 0f;
            for (int i = 0; i < u.Length; i++)
                d = MathF.Max(d, MathF.Abs(u[i] - v[i]));
            return d;
        }

        /// <summary>
        /// 双判据守门 + 安全退回：
        ///   Direct → 逐字走第十七轮 `YrContactBlockMatrix.Solve`；
        ///   迭代   → 残差达标 **且** 与直接解漂移 ≤ solTol 才采用迭代解，
        ///            否则整组退回直接解（物理逐位不变）。
        /// </summary>
        public static float[] TrySolve(YrBlockSolveMode mode,
                                       float[] k, int n, int m, float damping,
                                       float[] lower, float[] b,
                                       int maxIt, float tol, float solTol,
                                       out YrBlockLinearTrace trace)
        {
            float[] direct = YrContactBlockMatrix.Solve(lower, n, b);
            if (mode == YrBlockSolveMode.Direct)
            {
                trace = default;
                return direct;
            }

            float[] a = BuildDampedSystem(k, n, damping);
            if (!BuildBlockPreconditioner(a, n, m, out float[] lowers))
            {
                trace = new YrBlockLinearTrace(mode, 0, 0f, 0f, 0f,
                    0f, false, true, true);
                return direct;
            }

            float[] xi = Iterate(mode, a, n, m, lowers, b, maxIt, tol,
                out int iters, out float initR, out float finalR, out float maxR,
                out bool conv, out bool mono);
            float disc = MaxAbsDiff(xi, direct);
            bool useIterative = conv && disc <= solTol;
            trace = new YrBlockLinearTrace(mode, iters, initR, finalR, maxR,
                disc, conv && useIterative, !useIterative, mono);
            return useIterative ? xi : direct;
        }
    }
}
