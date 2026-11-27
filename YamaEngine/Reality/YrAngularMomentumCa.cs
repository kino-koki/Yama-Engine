using System;
namespace YamaEngine.Reality
{
    /// <summary>
    /// 角动量版 Conservative Advancement（第十二轮）—— 清掉第十一轮
    /// 「常量 ω 外推」的局限。
    ///
    /// 自由刚体在各向异性惯量下，即使无力矩、世界系角动量 L 严格守恒，
    /// 角速度 ω 也会随姿态进动（RigidBody3D 把 L 而非 ω 当状态量正因如此）。
    /// 第十一轮 `YrFullRotationalCa` 用常量 ω 积分 q(t)=exp(ωt/2)⊗q0，
    /// 长时间大角速外推会有姿态漂移，从而误判接触时刻。
    ///
    /// 这一轮把 L 作为守恒状态量：
    ///   ω(q) = R(q) · diag(invI_local) · Rᵀ(q) · L
    ///   dq/dt = ½ (ω,0) ⊗ q     （与 RigidBody3D.Integrate 同一半隐式口径）
    /// 姿态用固定子步数值积分到 t，再重枚举 15 轴算精确 SAT 分离 dist(t)。
    /// CA 的 t 单调递增，因此积分器用**增量缓存**：只从上次 t 推进到新 t，
    /// 不重复积分历史。运动上界 |ω| ≤ max(invI)·|L| 保证不漏报。
    ///
    /// 诚实边界：姿态是**一阶半隐式数值积分**（子步 ≤ 2.5e-4），不是自由刚体
    /// 的解析解（Jacobi 椭圆函数），有截断误差；ω 随姿态更新但 L 不修改，
    /// 因此「L 严格守恒、ω 进动」的物理成立。接触判定仍用 15 轴 SAT 的
    /// 瞬时姿态，不是连续时间旋转扫掠根的精确解。
    /// </summary>
    public static class YrAngularMomentumCa
    {
        public const float Epsilon = 1e-4f;
        public const int MaxIterations = 128;
        public const float MaxSubstep = 2.5e-4f;

        public readonly struct YrAmCaResult
        {
            public readonly float Toi;
            public readonly int Iterations;
            public readonly bool Converged;
            public readonly float Residual;
            public readonly int Substeps;
            public YrAmCaResult(float toi, int iterations, bool converged,
                                float residual, int substeps)
            { Toi = toi; Iterations = iterations; Converged = converged;
              Residual = residual; Substeps = substeps; }
        }

        public struct YrAmState
        {
            public float T;
            public RQuat Q;
            public YrAmState(RQuat q0) { T = 0f; Q = q0; }
        }

        /// <summary>ω(q) = R·diag(invI)·Rᵀ·L，与 RigidBody3D 同口径。</summary>
        public static RVec3 OmegaAt(RQuat q, RVec3 angularMomentumWorld,
                                    RVec3 invInertiaLocal)
        {
            RVec3 lb = q.RotateInverse(angularMomentumWorld);
            return q.Rotate(new RVec3(lb.X * invInertiaLocal.X,
                                      lb.Y * invInertiaLocal.Y,
                                      lb.Z * invInertiaLocal.Z));
        }

        /// <summary>把姿态从 st.T 增量积分到 targetT（t 单调递增，缓存有效）。</summary>
        public static void AdvanceTo(ref YrAmState st, RVec3 L,
                                     RVec3 invI, float targetT)
        {
            if (targetT <= st.T) return;
            float span = targetT - st.T;
            int steps = Math.Max(1, (int)MathF.Ceiling(span / MaxSubstep));
            float h = span / steps;
            for (int s = 0; s < steps; s++)
            {
                RVec3 w = OmegaAt(st.Q, L, invI);
                var wq = new RQuat(w.X, w.Y, w.Z, 0f);
                RQuat dq = wq * st.Q;
                st.Q = new RQuat(
                    st.Q.X + 0.5f * dq.X * h,
                    st.Q.Y + 0.5f * dq.Y * h,
                    st.Q.Z + 0.5f * dq.Z * h,
                    st.Q.W + 0.5f * dq.W * h).Normalized();
            }
            st.T = targetT;
        }

        /// <summary>便捷入口：从 q0 积分到 T 的姿态（供测试对拍 RigidBody3D）。</summary>
        public static RQuat OrientationAt(RQuat q0, RVec3 L,
                                          RVec3 invI, float T)
        {
            var st = new YrAmState(q0);
            AdvanceTo(ref st, L, invI, T);
            return st.Q;
        }

        private static float MaxInv(RVec3 invI)
            => MathF.Max(invI.X, MathF.Max(invI.Y, invI.Z));

        /// <summary>运动速率上界：|v_rel| + maxInv·|L_a|·R_a + maxInv·|L_b|·R_b。</summary>
        public static float MotionBound(in YrObb a, RVec3 va, RVec3 La, RVec3 invIa,
                                        in YrObb b, RVec3 vb, RVec3 Lb, RVec3 invIb)
            => (vb - va).Length()
             + MaxInv(invIa) * La.Length() * a.HalfExtents.Length()
             + MaxInv(invIb) * Lb.Length() * b.HalfExtents.Length();

        /// <summary>
        /// 增量姿态积分器：持有两体的 (t,q) 状态与守恒量，按 t 单调递增推进。
        /// Substeps 可观测，便于核算数值成本。
        /// </summary>
        public sealed class YrAmIntegrator
        {
            private YrAmState _a, _b;
            private readonly YrObb _oa0, _ob0;
            private readonly RVec3 _va, _vb, _La, _Lb, _invIa, _invIb;
            public int Substeps { get; private set; }

            public YrAmIntegrator(in YrObb a, RVec3 va, RVec3 La, RVec3 invIa,
                                  in YrObb b, RVec3 vb, RVec3 Lb, RVec3 invIb)
            {
                _oa0 = a; _ob0 = b;
                _va = va; _vb = vb; _La = La; _Lb = Lb;
                _invIa = invIa; _invIb = invIb;
                _a = new YrAmState(a.Orientation);
                _b = new YrAmState(b.Orientation);
            }

            public float SeparationAt(float t)
            {
                float ta = _a.T, tb = _b.T;
                AdvanceTo(ref _a, _La, _invIa, t);
                AdvanceTo(ref _b, _Lb, _invIb, t);
                Substeps += CountSteps(t - ta) + CountSteps(t - tb);
                YrObb oa = new YrObb(_oa0.Center + _va * t, _a.Q, _oa0.HalfExtents);
                YrObb ob = new YrObb(_ob0.Center + _vb * t, _b.Q, _ob0.HalfExtents);
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

            private static int CountSteps(float span)
                => span <= 0f ? 0
                 : Math.Max(1, (int)MathF.Ceiling(span / MaxSubstep));
        }

        /// <summary>单对 L 版 CA。toi∈[0,dt] 或 −1。</summary>
        public static YrAmCaResult ComputeToi(in YrObb a, RVec3 va, RVec3 La, RVec3 invIa,
                                              in YrObb b, RVec3 vb, RVec3 Lb, RVec3 invIb,
                                              float dt)
        {
            if (dt <= 0f) return new YrAmCaResult(-1f, 0, false, 0f, 0);
            var integ = new YrAmIntegrator(a, va, La, invIa, b, vb, Lb, invIb);
            float motion = MotionBound(a, va, La, invIa, b, vb, Lb, invIb);
            if (motion <= 1e-12f)
            {
                float d0 = integ.SeparationAt(0f);
                return d0 <= Epsilon
                    ? new YrAmCaResult(0f, 1, true, d0, integ.Substeps)
                    : new YrAmCaResult(-1f, 1, false, d0, integ.Substeps);
            }
            float t = 0f;
            for (int it = 1; it <= MaxIterations; it++)
            {
                float dist = integ.SeparationAt(t);
                if (dist <= Epsilon)
                    return new YrAmCaResult(t, it, true, dist, integ.Substeps);
                float delta = (dist - Epsilon) / motion;
                if (delta <= 1e-9f)
                    return new YrAmCaResult(t, it, true, dist, integ.Substeps);
                float nextT = t + delta;
                // 绝不因 float32 停滞而返回 −1（那会把「数值推不动」误判成
                // 「本帧不接触」= 漏报）。返回最后确认分离时刻，Residual 可观测。
                if (nextT <= t)
                    return new YrAmCaResult(t, it, dist <= Epsilon * 4f,
                                            dist, integ.Substeps);
                t = nextT;
                if (t > dt)
                    return new YrAmCaResult(-1f, it, false, dist, integ.Substeps);
            }
            return new YrAmCaResult(t, MaxIterations, false,
                integ.SeparationAt(t), integ.Substeps);
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
