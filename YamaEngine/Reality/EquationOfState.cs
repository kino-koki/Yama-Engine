using System;
namespace YamaEngine.Reality
{
    /// <summary>
    /// 弱可压缩流体的 Tait 状态方程与表面张力。
    /// PBF 强制 ρ≈ρ0 是「不可压缩」极限；真实流体仍有有限声速与可压缩激波，
    /// Tait EOS 把两者接起来：p = B[(ρ/ρ0)^γ − 1]，B = ρ0 c0² / γ。
    /// </summary>
    public sealed class EquationOfState
    {
        public float RestDensity { get; }
        public float SoundSpeed { get; }
        public float Gamma { get; }
        public float B { get; }

        public EquationOfState(float restDensity, float soundSpeed, float gamma)
        {
            RestDensity = restDensity > 0f ? restDensity : 1000f;
            SoundSpeed = soundSpeed > 0f ? soundSpeed : 100f;
            Gamma = gamma > 1f ? gamma : 7f;
            B = RestDensity * SoundSpeed * SoundSpeed / Gamma;
        }

        public float Pressure(float density)
        {
            float ratio = density / RestDensity;
            return B * (MathF.Pow(MathF.Max(1e-6f, ratio), Gamma) - 1f);
        }

        /// <summary>局部声速 c² = dp/dρ = γB/ρ0 · (ρ/ρ0)^(γ−1)。</summary>
        public float LocalSoundSpeed(float density)
        {
            float ratio = MathF.Max(1e-6f, density / RestDensity);
            float c2 = Gamma * B / RestDensity * MathF.Pow(ratio, Gamma - 1f);
            return MathF.Sqrt(MathF.Max(0f, c2));
        }

        /// <summary>
        /// Akinci 风格 cohesion 核的简化：r&lt;h 时沿连线向内的吸引力，
        /// r&gt;=h 为 0，r→0 也趋于 0（避免自作用奇点）。
        /// 返回「力系数」，乘以方向单位向量与粒子质量即为表面张力。
        /// </summary>
        public static float CohesionFactor(float r, float h, float sigma)
        {
            if (r <= 1e-9f || r >= h) return 0f;
            float q = r / h;
            // C(r) ∝ (1-q)^3 · q，峰值在 q=1/4，两头归零
            float t = 1f - q;
            return sigma * t * t * t * q;
        }

        public static RVec3 CohesionForce(RVec3 fromI, RVec3 toJ, float h, float sigma, float mass)
        {
            RVec3 d = toJ - fromI;
            float r = d.Length();
            float f = CohesionFactor(r, h, sigma);
            if (f <= 0f) return RVec3.Zero;
            return d * (f * mass / r);
        }
    }
}
