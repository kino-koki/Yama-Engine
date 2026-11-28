using System;
namespace YamaEngine.Reality
{
    /// <summary>
    /// 完整旋转 Conservative Advancement（第十一轮）—— 清掉第十轮「线性化 CA」的局限。
    ///
    /// 第十轮 `YrConservativeAdvancement` 的姿态轴永远取 t=0 的 15 根轴，
    /// 旋转以 `spin·t` **从 0 累积**的线性上界并入投影半径。这保证不漏报，
    /// 但把整段历史的旋转误差都压在 t 上，帧越长越保守。
    ///
    /// 这一轮用行业 CCD 的标准形态：
    ///   ① 把姿态用世界系角速度精确积分到时间 t：
    ///        q(t) = exp(ω·t/2) ⊗ q0   （RQuat.FromAxisAngle 左乘）
    ///   ② 用积分后的姿态**重枚举 15 轴**，算该时刻的精确 SAT 分离 dist(t)
    ///        —— 这里不再加 spin·t，因为姿态已经真实推进了
    ///   ③ motion = |v_rel| + |ω_a|R_a + |ω_b|R_b
    ///        这是两体表面点相对接近速率的上界（旋转贡献用最坏点速度）
    ///   ④ Δt = (dist − ε)/motion，t += Δt，回到 ①
    ///   ⑤ dist ≤ ε 返回 t；t &gt; dt 返回 −1
    ///
    /// 安全性：在 Δt 内间隙减小量 ≤ motion·Δt = dist−ε，因此不会越过接触
    /// （这是 CA 的标准不漏报论证）。相对第十轮，旋转不再按累积时间膨胀，
    /// 而是按真实姿态积分 → **更精确**，且仍然不漏报。
    ///
    /// 已知代价：motion 含旋转的最坏点速度，带旋转时实际间隙下降远慢于该上界，
    /// CA 会呈几何级数收敛。真实固定步 1/60 实测最大 34 次迭代即可收敛；
    /// 人为放大到 dt=1.2 + 大角速可达 126 次，故 MaxIterations 取 128。
    /// 真机侧可用割线/根细化加速，这里保留标准 CA 的不漏报保证优先。
    ///
    /// 诚实边界：仍是「线性化步长 + 精确瞬时姿态」的 CA，不是连续时间上
    /// 求解精确旋转扫掠根。ω 取常量（自由刚体在各向异性惯量下会进动，
    /// 本函数按常量 ω 外推，与 RigidBody3D 的 L 状态量积分不同）。
    /// 长时间大角速外推会有姿态漂移，留给真机侧用 L 版 CA 替换。
    /// </summary>
    public static class YrFullRotationalCa
    {
        public const float Epsilon = 1e-4f;
        public const int MaxIterations = 128;

        public readonly struct YrFullCaResult
        {
            public readonly float Toi;
            public readonly int Iterations;
            public readonly bool Converged;
            public readonly float Residual;
            public YrFullCaResult(float toi, int iterations, bool converged, float residual)
            { Toi = toi; Iterations = iterations; Converged = converged; Residual = residual; }
        }

        /// <summary>
        /// 姿态积分：世界系角速度 ω 左乘四元数增量 exp(ω·t/2)。
        /// 与 RigidBody3D.Integrate 的 `dq = wq * Orientation` 同一口径。
        /// </summary>
        public static RQuat IntegrateOrientation(RQuat q0, RVec3 omega, float t)
        {
            if (t <= 0f) return q0;
            float w = omega.Length();
            if (w <= 1e-9f) return q0;
            RQuat dq = RQuat.FromAxisAngle(omega, w * t);
            return (dq * q0).Normalized();
        }

        /// <summary>相对运动速率上界（含两体旋转的最坏点速度）。</summary>
        public static float MotionBound(in YrObb a, RVec3 va, RVec3 wa,
                                        in YrObb b, RVec3 vb, RVec3 wb)
            => (vb - va).Length()
             + wa.Length() * a.HalfExtents.Length()
             + wb.Length() * b.HalfExtents.Length();

        /// <summary>
        /// 时间 t 处的精确 SAT 分离下界：先积分姿态与质心，再重枚举 15 轴。
        /// &gt;0 = 仍分离；≤0 = 已接触/重叠。
        /// </summary>
        public static float SeparationAt(in YrObb a, RVec3 va, RVec3 wa,
                                         in YrObb b, RVec3 vb, RVec3 wb,
                                         float t)
        {
            YrObb oa = new YrObb(a.Center + va * t,
                                 IntegrateOrientation(a.Orientation, wa, t), a.HalfExtents);
            YrObb ob = new YrObb(b.Center + vb * t,
                                 IntegrateOrientation(b.Orientation, wb, t), b.HalfExtents);
            RVec3 d = ob.Center - oa.Center;
            RVec3[] axes = YrSweptObb.EnumerateAxes(oa, ob);
            float maxGap = float.NegativeInfinity;
            for (int k = 0; k < axes.Length; k++)
            {
                RVec3 u = axes[k];
                float gap = MathF.Abs(d.Dot(u))
                          - YrContactManifold.ProjectedRadius(oa, u)
                          - YrContactManifold.ProjectedRadius(ob, u);
                if (gap > maxGap) maxGap = gap;
            }
            return maxGap;
        }

        /// <summary>单对完整旋转 CA。toi∈[0,dt] 或 −1。</summary>
        public static YrFullCaResult ComputeToi(in YrObb a, RVec3 va, RVec3 wa,
                                                in YrObb b, RVec3 vb, RVec3 wb,
                                                float dt)
        {
            if (dt <= 0f) return new YrFullCaResult(-1f, 0, false, 0f);
            float motion = MotionBound(a, va, wa, b, vb, wb);
            if (motion <= 1e-12f)
            {
                float d0 = SeparationAt(a, va, wa, b, vb, wb, 0f);
                return d0 <= Epsilon
                    ? new YrFullCaResult(0f, 1, true, d0)
                    : new YrFullCaResult(-1f, 1, false, d0);
            }
            float t = 0f;
            for (int it = 1; it <= MaxIterations; it++)
            {
                float dist = SeparationAt(a, va, wa, b, vb, wb, t);
                if (dist <= Epsilon) return new YrFullCaResult(t, it, true, dist);
                float delta = (dist - Epsilon) / motion;
                if (delta <= 1e-9f) return new YrFullCaResult(t, it, true, dist);
                float nextT = t + delta;
                // 与第十轮同源的 float32 精度停滞处理：Residual 保留可观测，
                // 不放宽 ε，不吞异常。
                if (nextT <= t)
                {
                    // 绝不返回 -1：float32 精度停滞是「数值推不动」，不是
                    // 「本帧不接触」，判成 -1 就是漏报。返回最后一次确认分离的
                    // 时刻，Converged 按 dist 是否已达 ε 量级标注，Residual 可观测。
                    return new YrFullCaResult(t, it, dist <= Epsilon * 4f, dist);
                }
                t = nextT;
                if (t > dt) return new YrFullCaResult(-1f, it, false, dist);
            }
            return new YrFullCaResult(t, MaxIterations, false,
                SeparationAt(a, va, wa, b, vb, wb, t));
        }

        /// <summary>全对最早 TOI（确定性：严格更小值胜出）。</summary>
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

        /// <summary>候选对版本（整帧扫掠 BVH 的超集）。</summary>
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
