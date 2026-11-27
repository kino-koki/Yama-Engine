using System;
namespace YamaEngine.Reality
{
    /// <summary>
    /// 两体接触上下文（质量 / 惯量 / 姿态 / 质心 / 摩擦系数）。
    /// 第十二轮的 wrench 行权重是外部标量，本轮改为从两体惯量张量算有效质量。
    /// </summary>
    public readonly struct YrContactMass
    {
        public readonly float InvMassA, InvMassB;
        public readonly RVec3 InvInertiaLocalA, InvInertiaLocalB;
        public readonly RQuat QA, QB;
        public readonly RVec3 CenterA, CenterB;
        public readonly float Mu;

        public YrContactMass(float invMassA, float invMassB,
                             RVec3 invInertiaLocalA, RVec3 invInertiaLocalB,
                             RQuat qa, RQuat qb, RVec3 centerA, RVec3 centerB,
                             float mu)
        {
            InvMassA = invMassA; InvMassB = invMassB;
            InvInertiaLocalA = invInertiaLocalA; InvInertiaLocalB = invInertiaLocalB;
            QA = qa; QB = qb; CenterA = centerA; CenterB = centerB; Mu = mu;
        }

        /// <summary>各向同性单位质量 / 球惯量的默认上下文（便于测试构造）。</summary>
        public static YrContactMass Uniform(float invMass, RVec3 invInertia,
                                            RVec3 centerA, RVec3 centerB, float mu)
            => new YrContactMass(invMass, invMass, invInertia, invInertia,
                                 RQuat.Identity, RQuat.Identity, centerA, centerB, mu);
    }

    /// <summary>
    /// 接触有效质量（sequential impulse 的 K 矩阵对角项）与库仑摩擦锥。
    ///
    /// 对接触方向 d 与接触点 p：
    ///   r_a = p − c_a,  r_b = p − c_b
    ///   K(d) = invM_a + invM_b
    ///        + (r_a×d)·I_a⁻¹(r_a×d) + (r_b×d)·I_b⁻¹(r_b×d)
    ///   m_eff = 1/K
    /// 世界系 I⁻¹ 作用 = R·diag(invI_local)·Rᵀ，与 RigidBody3D 同口径。
    ///
    /// 摩擦锥（库仑）：切向约束的力度上界为 μ·λ_n。缩减里把切向 6D 子行
    /// 乘 `min(1, μ·sqrt(K_n/K_t))`，让「切向有效质量越重」的方向越不被优先选。
    ///
    /// 诚实边界：K 只取**单个方向的对角项**，忽略法向与切向的耦合项
    /// （完整 3×3 K 矩阵的非对角元）；摩擦锥缩放是一阶近似，不是 LCP/SOCP
    /// 里的精确锥投影；接触点用单一 p 而非逐点 r_a/r_b 的独立约束。
    /// </summary>
    public static class YrContactEffectiveMass
    {
        /// <summary>世界系 I⁻¹ 作用于向量：R·diag(invI)·Rᵀ·v。</summary>
        public static RVec3 ApplyWorldInvInertia(RQuat q, RVec3 invI, RVec3 v)
        {
            RVec3 lb = q.RotateInverse(v);
            return q.Rotate(new RVec3(lb.X * invI.X, lb.Y * invI.Y, lb.Z * invI.Z));
        }

        /// <summary>有效质量的倒数 K(d)。K 越大 → 越难推动 → m_eff 越小。</summary>
        public static float EffectiveMassReciprocal(in YrContactMass m,
                                                     RVec3 point, RVec3 dir)
        {
            RVec3 d = dir.Normalized();
            if (d.LengthSq() < 0.5f) d = new RVec3(0f, 0f, 1f);
            RVec3 ra = point - m.CenterA, rb = point - m.CenterB;
            RVec3 ta = ra.Cross(d), tb = rb.Cross(d);
            RVec3 ia = ApplyWorldInvInertia(m.QA, m.InvInertiaLocalA, ta);
            RVec3 ib = ApplyWorldInvInertia(m.QB, m.InvInertiaLocalB, tb);
            return m.InvMassA + m.InvMassB + ta.Dot(ia) + tb.Dot(ib);
        }

        /// <summary>m_eff = 1/K（K 近零时返回 0，避免除零）。</summary>
        public static float EffectiveMass(in YrContactMass m, RVec3 point, RVec3 dir)
        {
            float k = EffectiveMassReciprocal(m, point, dir);
            return k > 1e-9f ? 1f / k : 0f;
        }

        /// <summary>切向行在摩擦锥下的缩放因子（一阶近似）。</summary>
        public static float TangentialConeScale(in YrContactMass m, RVec3 point,
                                                RVec3 normal, RVec3 tangent)
        {
            float kn = EffectiveMassReciprocal(m, point, normal);
            float kt = EffectiveMassReciprocal(m, point, tangent);
            if (kt <= 1e-9f) return 0f;
            float scale = m.Mu * MathF.Sqrt(MathF.Max(0f, kn) / kt);
            return MathF.Min(1f, MathF.Max(0f, scale));
        }

        /// <summary>库仑摩擦锥内部性判定（严格 |λ_t| ≤ μ·λ_n）。</summary>
        public static bool InsideFrictionCone(float lambdaNormal,
                                              RVec3 lambdaTangent, float mu)
        {
            if (lambdaNormal < 0f) return false;
            float limit = mu * lambdaNormal;
            return lambdaTangent.Length() <= limit + 1e-9f;
        }

        /// <summary>把切向冲量投影回摩擦锥（返回缩放后的切向冲量）。</summary>
        public static RVec3 ProjectToFrictionCone(float lambdaNormal,
                                                  RVec3 lambdaTangent, float mu)
        {
            float len = lambdaTangent.Length();
            float limit = mu * MathF.Max(0f, lambdaNormal);
            if (len <= limit || len <= 1e-9f) return lambdaTangent;
            return lambdaTangent * (limit / len);
        }

        /// <summary>
        /// 用有效质量加权的 18D 接触 Jacobian 行：
        ///   [n, r×n, t1, r×t1, t2, r×t2]，法向子行乘 1/√K_n，
        ///   切向子行乘 (1/√K_t)·摩擦锥缩放，最后整体归一化。
        /// </summary>
        public static float[] WeightedContactRow(in YrContactMass m,
                                                  RVec3 point, RVec3 normal)
        {
            YrContactReduction.TangentBasis(normal,
                out RVec3 nn, out RVec3 t1, out RVec3 t2);
            RVec3 r = point - m.CenterA;
            RVec3[] dirs = new RVec3[] { nn, t1, t2 };
            float kn = EffectiveMassReciprocal(m, point, nn);
            float k1 = EffectiveMassReciprocal(m, point, t1);
            float k2 = EffectiveMassReciprocal(m, point, t2);
            float wn = kn > 1e-9f ? 1f / MathF.Sqrt(kn) : 0f;
            float w1 = k1 > 1e-9f ? 1f / MathF.Sqrt(k1) : 0f;
            float w2 = k2 > 1e-9f ? 1f / MathF.Sqrt(k2) : 0f;
            float c1 = TangentialConeScale(m, point, nn, t1);
            float c2 = TangentialConeScale(m, point, nn, t2);
            float[] w = new float[] { wn, w1 * c1, w2 * c2 };

            var j = new float[18];
            for (int k = 0; k < 3; k++)
            {
                RVec3 v = dirs[k];
                RVec3 c = r.Cross(v);
                j[k * 6 + 0] = v.X * w[k];
                j[k * 6 + 1] = v.Y * w[k];
                j[k * 6 + 2] = v.Z * w[k];
                j[k * 6 + 3] = c.X * w[k];
                j[k * 6 + 4] = c.Y * w[k];
                j[k * 6 + 5] = c.Z * w[k];
            }
            float norm = 0f;
            for (int i = 0; i < 18; i++) norm += j[i] * j[i];
            norm = MathF.Sqrt(norm);
            if (norm > 1e-9f)
                for (int i = 0; i < 18; i++) j[i] /= norm;
            return j;
        }
    }
}
