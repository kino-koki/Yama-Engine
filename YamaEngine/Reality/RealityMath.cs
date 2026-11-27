using System;
namespace YamaEngine.Reality
{
    /// <summary>轻量三维向量（结构体，无 GC，便于 SoA 与原生内核共享布局）。</summary>
    public readonly struct RVec3
    {
        public readonly float X, Y, Z;
        public RVec3(float x, float y, float z) { X = x; Y = y; Z = z; }
        public static RVec3 Zero => new RVec3(0f, 0f, 0f);
        public static RVec3 operator +(RVec3 a, RVec3 b) => new RVec3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static RVec3 operator -(RVec3 a, RVec3 b) => new RVec3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static RVec3 operator *(RVec3 a, float s) => new RVec3(a.X * s, a.Y * s, a.Z * s);
        public static RVec3 operator *(float s, RVec3 a) => a * s;
        public static RVec3 operator -(RVec3 a) => new RVec3(-a.X, -a.Y, -a.Z);
        public float Dot(RVec3 b) => X * b.X + Y * b.Y + Z * b.Z;
        public RVec3 Cross(RVec3 b) => new RVec3(
            Y * b.Z - Z * b.Y, Z * b.X - X * b.Z, X * b.Y - Y * b.X);
        public float LengthSq() => X * X + Y * Y + Z * Z;
        public float Length() => MathF.Sqrt(LengthSq());
        public RVec3 Normalized()
        {
            float l = Length();
            return l > 1e-9f ? this * (1f / l) : Zero;
        }
    }

    /// <summary>SPH 核函数与数值工具。系数均为 3D 标准归一化系数。</summary>
    public static class RealityKernels
    {
        /// <summary>Poly6 核 3D: 315/(64*pi*h^9) * (h^2-r^2)^3, r&lt;h。</summary>
        public static float Poly6Coef(float h)
        {
            double h3 = (double)h * h * h;
            return (float)(315.0 / (64.0 * Math.PI * h3 * h3 * h3));
        }

        /// <summary>Spiky 梯度系数 3D: -45/(pi*h^6)。</summary>
        public static float SpikyGradCoef(float h)
        {
            double h3 = (double)h * h * h;
            return (float)(-45.0 / (Math.PI * h3 * h3));
        }

        public static float Poly6(float r2, float h, float coef)
        {
            float d = h * h - r2;
            if (d <= 0f) return 0f;
            return coef * d * d * d;
        }

        /// <summary>返回 Spiky 梯度系数乘以 (h-r)^2 / r，再乘方向向量即为梯度。</summary>
        public static float SpikyGradFactor(float r, float h, float coef)
        {
            if (r <= 1e-9f || r >= h) return 0f;
            float t = h - r;
            return coef * t * t / r;
        }

        public static float Clamp(float v, float lo, float hi) => v < lo ? lo : (v > hi ? hi : v);
        public static float Clamp01(float v) => Clamp(v, 0f, 1f);
    }
}
