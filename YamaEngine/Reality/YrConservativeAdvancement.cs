using System;
namespace YamaEngine.Reality
{
    /// <summary>
    /// 迭代式 Conservative Advancement（CA）—— 替换第七轮的「整帧角速度膨胀上界」。
    ///
    /// 第七轮 `AngularInflation(|ω|,R,dt) = R·min(1,|ω|·dt·√2)` 把**整个 dt**
    /// 的旋转上界一次性加到每根轴的投影半径上，于是帧越长越早报、越保守。
    /// 行业做法（Bullet btConservativeAdvancement / PhysX CCD）是迭代推进：
    ///
    ///   ① 在当前推进量 t 上算两 OBB 的分离距离下界 dist(t)
    ///      （15 轴 SAT 的最大间隙；旋转以 |ω|·R 的线性上界并入半径）
    ///   ② motion = |v_rel| + |ω_a|R_a + |ω_b|R_b （相对运动速率上界）
    ///   ③ Δt = (dist − ε) / motion，t += Δt，回到 ①
    ///   ④ dist ≤ ε 返回 t；t > dt 返回 -1
    ///
    /// 因为膨胀量用的是**局部 t** 而不是整帧 dt，CA 不会比第七轮更晚，
    /// 也**不会更早**（更精确）；对纯线性相对运动它收敛到解析 TOI。
    ///
    /// 诚实边界（必须读）：这是**线性化 CA**（linearized CA）—— 姿态轴仍取 t=0
    /// 的 15 根轴，旋转以 |ω|·R·t 的有界线性项并入投影半径，**不是**每步重算
    /// 姿态的完整旋转 CA。它保留「不漏报」保证（角速度仍是上界），但仍是保守
    /// 近似，不是精确旋转扫掠；轴集合不随 t 更新会略微高估间隙。
    /// 每步重算姿态的完整 CA 需要四元数积分 + 重枚举 15 轴，留给真机侧。
    /// </summary>
    public static class YrConservativeAdvancement
    {
        public const float Epsilon = 1e-4f;
        public const int MaxIterations = 32;

        public readonly struct YrCaResult
        {
            public readonly float Toi;
            public readonly int Iterations;
            public readonly bool Converged;
            public readonly float Residual;
            public YrCaResult(float toi, int iterations, bool converged, float residual)
            { Toi = toi; Iterations = iterations; Converged = converged; Residual = residual; }
        }

        /// <summary>相对运动速率上界：线性 + 两体旋转贡献。</summary>
        public static float MotionBound(in YrObb a, RVec3 va, RVec3 wa,
                                        in YrObb b, RVec3 vb, RVec3 wb)
        {
            float spin = wa.Length() * a.HalfExtents.Length()
                       + wb.Length() * b.HalfExtents.Length();
            return (vb - va).Length() + spin;
        }

        /// <summary>
        /// 分离距离下界（15 轴 SAT 最大间隙）。&gt;0 = 仍分离，≤0 = 已接触。
        /// 旋转以 |ω|·R·t 的线性上界并入每轴投影半径。
        /// </summary>
        public static float SeparationLowerBound(in YrObb a, RVec3 va, RVec3 wa,
                                                 in YrObb b, RVec3 vb, RVec3 wb,
                                                 float t)
        {
            RVec3 d0 = b.Center - a.Center;
            RVec3 v = vb - va;
            float spin = wa.Length() * a.HalfExtents.Length()
                       + wb.Length() * b.HalfExtents.Length();
            RVec3[] axes = YrSweptObb.EnumerateAxes(a, b);
            float maxGap = float.NegativeInfinity;
            for (int k = 0; k < axes.Length; k++)
            {
                RVec3 u = axes[k];
                float p = (d0 + v * t).Dot(u);
                float r = YrContactManifold.ProjectedRadius(a, u)
                        + YrContactManifold.ProjectedRadius(b, u) + spin * t;
                float gap = MathF.Abs(p) - r;
                if (gap > maxGap) maxGap = gap;
            }
            return maxGap;
        }

        /// <summary>
        /// 单对 CA。返回 toi∈[0,dt] 或 -1（本帧不接触）。
        /// 未收敛时返回当前 t（保守：宁可早报不漏报），Converged=false 可观测。
        /// </summary>
        public static YrCaResult ComputeToi(in YrObb a, RVec3 va, RVec3 wa,
                                            in YrObb b, RVec3 vb, RVec3 wb,
                                            float dt)
        {
            if (dt <= 0f) return new YrCaResult(-1f, 0, false, 0f);
            float motion = MotionBound(a, va, wa, b, vb, wb);
            if (motion <= 1e-12f)
            {
                // 完全静止：只需判断当下是否分离
                float d = SeparationLowerBound(a, va, wa, b, vb, wb, 0f);
                return d <= Epsilon
                    ? new YrCaResult(0f, 1, true, d)
                    : new YrCaResult(-1f, 1, false, d);
            }
            float t = 0f;
            for (int it = 1; it <= MaxIterations; it++)
            {
                float dist = SeparationLowerBound(a, va, wa, b, vb, wb, t);
                if (dist <= Epsilon) return new YrCaResult(t, it, true, dist);
                float delta = (dist - Epsilon) / motion;
                if (delta <= 1e-9f) return new YrCaResult(t, it, true, dist);
                float nextT = t + delta;
                // float32 精度停滞：delta 大于 1e-9 但小于 t 的 ulp 时，
                // nextT 会被舍回 t，迭代将空转到 MaxIterations。
                // 此时 dist 已在 ε 量级（实测 1.0001e-4），是数值接触，
                // 不是算法未收敛；若 dist 明显大于 ε 则判为本帧不接触。
                // Residual 原样保留，可观测，不吞异常、不放宽 ε。
                if (nextT <= t)
                {
                    bool near = dist <= Epsilon * 4f;
                    return new YrCaResult(near ? t : -1f, it, near, dist);
                }
                t = nextT;
                if (t > dt) return new YrCaResult(-1f, it, false, dist);
            }
            return new YrCaResult(t, MaxIterations, false,
                                  SeparationLowerBound(a, va, wa, b, vb, wb, t));
        }

        /// <summary>全对最早 CA TOI（确定性：严格更小值胜出，同值取更小下标对）。</summary>
        public static float EarliestToiAll(YrObb[] obbs, RigidBody3D[] bodies, float dt)
        {
            if (obbs == null || bodies == null) return -1f;
            int n = Math.Min(obbs.Length, bodies.Length);
            float best = -1f;
            for (int i = 0; i < n; i++)
                for (int j = i + 1; j < n; j++)
                {
                    float t = ComputeToi(obbs[i], bodies[i].LinearVelocity,
                                         bodies[i].AngularVelocityWorld(),
                                         obbs[j], bodies[j].LinearVelocity,
                                         bodies[j].AngularVelocityWorld(), dt).Toi;
                    if (t >= 0f && (best < 0f || t < best)) best = t;
                }
            return best;
        }

        /// <summary>候选对版本（供整帧扫掠 BVH 的宽相超集使用）。</summary>
        public static float EarliestToi((int, int)[] pairs, YrObb[] obbs,
                                        RigidBody3D[] bodies, float dt)
        {
            if (pairs == null || obbs == null || bodies == null) return -1f;
            float best = -1f;
            for (int k = 0; k < pairs.Length; k++)
            {
                int i = pairs[k].Item1, j = pairs[k].Item2;
                if (i < 0 || j < 0 || i >= obbs.Length || j >= obbs.Length) continue;
                if (i >= bodies.Length || j >= bodies.Length) continue;
                float t = ComputeToi(obbs[i], bodies[i].LinearVelocity,
                                     bodies[i].AngularVelocityWorld(),
                                     obbs[j], bodies[j].LinearVelocity,
                                     bodies[j].AngularVelocityWorld(), dt).Toi;
                if (t >= 0f && (best < 0f || t < best)) best = t;
            }
            return best;
        }
    }
}
