using System;
namespace YamaEngine.Reality
{
    /// <summary>
    /// 碰撞冲量求解：法向用恢复系数，切向用库仑摩擦锥投影。
    /// 与 XPBD 的位置修正互补——它给的是「碰撞瞬间的速度级冲量」，
    /// 这是刚体不穿透且不打滑的关键。全部为纯函数，便于逐项验证。
    /// </summary>
    public static class ContactFriction
    {
        /// <summary>法向冲量标量：j_n = -(1+e)(v_rel·n)/effMass。</summary>
        public static float NormalImpulse(RVec3 relativeVelocity, RVec3 normal,
                                          float restitution, float effectiveMass)
        {
            float vn = relativeVelocity.Dot(normal);
            if (vn >= 0f) return 0f; // 分离中，不产生冲量
            return -(1f + restitution) * vn * effectiveMass;
        }

        /// <summary>
        /// 切向冲量向量：先算完全消除切向相对速度所需的冲量，
        /// 若落在静摩擦锥内则全额施加，否则沿动摩擦方向按 μ_d·|j_n| 截断。
        /// </summary>
        public static RVec3 TangentImpulse(RVec3 relativeVelocity, RVec3 normal,
                                           float normalImpulse, float effectiveMass,
                                           float muStatic, float muDynamic)
        {
            float vn = relativeVelocity.Dot(normal);
            RVec3 vt = relativeVelocity - normal * vn;
            float speed = vt.Length();
            if (speed <= 1e-9f || normalImpulse <= 0f) return RVec3.Zero;
            RVec3 dir = vt * (1f / speed);
            float needed = speed * effectiveMass;      // 完全止滑所需
            float jn = MathF.Abs(normalImpulse);
            float staticLimit = muStatic * jn;
            if (needed <= staticLimit) return dir * (-needed);
            float dynamic = muDynamic * jn;
            return dir * (-MathF.Min(needed, dynamic));
        }

        /// <summary>把冲量施加为速度变化（返回新的相对速度，便于串行验证）。</summary>
        public static RVec3 ApplyImpulse(RVec3 relativeVelocity, RVec3 normal,
                                         float normalImpulse, RVec3 tangentImpulse,
                                         float invEffectiveMass)
        {
            return relativeVelocity + normal * (normalImpulse * invEffectiveMass)
                                    + tangentImpulse * invEffectiveMass;
        }

        /// <summary>库仑锥判定：切向冲量模长是否超过 μ·|j_n|。</summary>
        public static bool InsideCone(RVec3 tangentImpulse, float normalImpulse, float mu)
            => tangentImpulse.Length() <= mu * MathF.Abs(normalImpulse) + 1e-9f;
    }
}
