using System;
namespace YamaEngine.Reality
{
    /// <summary>
    /// 割线/根细化加速的 Conservative Advancement（第十三轮）。
    ///
    /// 前几轮 CA 用固定步长 `Δt = (dist − ε)/motion`，其中 motion 是**最坏点
    /// 速度上界**（含旋转的最坏贡献）。带角速度时实际间隙下降远慢于该上界，
    /// 于是步长被严重低估、呈几何级数收敛 —— 真实固定步 1/60 实测最大 34 次。
    ///
    /// 本轮保留 CA 的不漏报骨架，把「用上界算步长」换成「用实测斜率外推 + 保守上限」：
    ///   lo  = 最后一个**经 dist 验证仍分离**的时刻（安全锚，永不越过接触）
    ///   slope = (d_prev − d_lo)/(lo − t_prev)   （最近两个采样点的实测斜率）
    ///   pred = lo + (d_lo − ε)/slope
    ///   pred 同时受 `lo + CapMultiplier·(d_lo−ε)/motion` 与 dt 截断；
    ///   slope 夹在 (MinSlope, motion) ⇒ pred − lo ≥ 标准保守步
    ///   采样 dist(pred)：≤ε → 命中并返回 pred；&gt;ε → lo 安全推进到 pred
    ///
    /// 为什么不漏报：所有 lo 都经 `dist(lo) &gt; ε` 实测验证，因此从未跨过首次
    /// 接触而不自知；命中点 pred 处 `dist ≤ ε` 即刚接触（精度 ε/motion），
    /// 不会带着深穿透返回；若 lo 推进到 dt 仍分离，则本帧确实不接触 → −1。
    /// CapMultiplier 限制单步最多 4 倍保守步，缓解 dist 非单调（旋转擦肩）
    /// 时割线跨过短暂接触窗口的风险。
    ///
    /// 诚实边界：不漏报依赖**采样验证 + 步长上限**，不是「dist 全局单调」的
    /// 数学证明；SAT gap 在旋转下可能非单调，本实现用 cap 缓解但未彻底排除。
    /// 姿态复用 YrAngularMomentumCa 的 L 版增量积分（各向异性进动正确），
    /// 依旧是一阶半隐式数值积分、非解析解。
    /// </summary>
    public static class YrSecantCa
    {
        public const float Epsilon = 1e-4f;
        public const int MaxIterations = 64;
        public const float CapMultiplier = 4f;
        public const float MinSlope = 1e-9f;

        public readonly struct YrSecantResult
        {
            public readonly float Toi;
            public readonly int Iterations;
            public readonly bool Converged;
            public readonly float Residual;
            public readonly int Substeps;
            /// <summary>实际用「实测斜率」而非保守 motion 推进的步数（加速机制观测）。</summary>
            public readonly int AcceleratedSteps;
            public YrSecantResult(float toi, int iterations, bool converged,
                                  float residual, int substeps, int acceleratedSteps = 0)
            { Toi = toi; Iterations = iterations; Converged = converged;
              Residual = residual; Substeps = substeps; AcceleratedSteps = acceleratedSteps; }
        }

        /// <summary>单对割线 CA。toi∈[0,dt] 或 −1（本帧确认分离）。</summary>
        public static YrSecantResult ComputeToi(
            in YrObb a, RVec3 va, RVec3 La, RVec3 invIa,
            in YrObb b, RVec3 vb, RVec3 Lb, RVec3 invIb, float dt)
        {
            if (dt <= 0f)
                return new YrSecantResult(-1f, 0, false, 0f, 0);

            var integ = new YrAngularMomentumCa.YrAmIntegrator(
                a, va, La, invIa, b, vb, Lb, invIb);
            float motion = YrAngularMomentumCa.MotionBound(
                a, va, La, invIa, b, vb, Lb, invIb);
            if (motion <= 1e-12f)
            {
                float d0 = integ.SeparationAt(0f);
                return d0 <= Epsilon
                    ? new YrSecantResult(0f, 1, true, d0, integ.Substeps)
                    : new YrSecantResult(-1f, 1, false, d0, integ.Substeps);
            }

            float lo = 0f;
            float dLo = integ.SeparationAt(0f);
            int n = 1;
            if (dLo <= Epsilon)
                return new YrSecantResult(0f, n, true, dLo, integ.Substeps);

            float prevT = -1f, prevD = 0f;
            int accelerated = 0;
            for (int it = 2; it <= MaxIterations; it++)
            {
                float conservative = (dLo - Epsilon) / motion;
                if (conservative <= 0f)
                    return new YrSecantResult(lo, n, false, dLo,
                                              integ.Substeps, accelerated);
                // 标准 CA 的严格分离判据：连保守步都超出本帧 ⇒ 本帧确实不接触。
                // 放在最前面，避免把 pred 截断到 dt 后再多采样一次，也避免
                // 「步长下界保险」把 pred 拉回 dt 之外（诊断轮第二处根因）。
                if (lo + conservative > dt)
                    return new YrSecantResult(-1f, n, false, dLo,
                                              integ.Substeps, accelerated);

                // 首步没有斜率样本 → 用保守 motion；之后只在
                // MinSlope < measured < motion 时用实测斜率（割线加速）。
                // measured > motion 时必须回退 motion，否则步长会比标准保守步
                // 还短，割线反而更慢（诊断轮真修）。slope <= motion 已保证
                // pred − lo >= conservative，无需额外的步长下界保险。
                float slope = motion;
                if (prevT >= 0f && lo > prevT)
                {
                    float measured = (prevD - dLo) / (lo - prevT);
                    if (measured > MinSlope && measured < motion)
                    {
                        slope = measured;
                        accelerated++;
                    }
                }
                if (slope <= MinSlope) slope = motion;

                float pred = lo + (dLo - Epsilon) / slope;
                float cap = lo + CapMultiplier * conservative;
                if (pred > cap) pred = cap;
                if (pred > dt) pred = dt;
                // float32 停滞：返回最后确认分离的时刻，绝不返回 −1
                // （那会把「数值推不动」误判成「本帧不接触」= 漏报）。
                if (pred <= lo)
                    return new YrSecantResult(lo, n, false, dLo,
                                              integ.Substeps, accelerated);

                float d = integ.SeparationAt(pred);
                n++;
                if (d <= Epsilon)
                    return new YrSecantResult(pred, n, true, d,
                                              integ.Substeps, accelerated);

                prevT = lo; prevD = dLo;
                lo = pred; dLo = d;
                if (lo >= dt)
                    return new YrSecantResult(-1f, n, false, dLo,
                                              integ.Substeps, accelerated);
            }
            // 迭代用尽：返回最后确认分离的时刻（不漏报），Converged=false 可观测。
            return new YrSecantResult(lo, n, false, dLo,
                                      integ.Substeps, accelerated);
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
                        bodies[i].AngularMomentum, bodies[i].InvInertiaLocal,
                        obbs[j], bodies[j].LinearVelocity,
                        bodies[j].AngularMomentum, bodies[j].InvInertiaLocal, dt).Toi;
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
                    bodies[i].AngularMomentum, bodies[i].InvInertiaLocal,
                    obbs[j], bodies[j].LinearVelocity,
                    bodies[j].AngularMomentum, bodies[j].InvInertiaLocal, dt).Toi;
                if (t >= 0f && (best < 0f || t < best)) best = t;
            }
            return best;
        }
    }
}
