using System;
namespace YamaEngine.Reality
{
    /// <summary>
    /// 热 ↔ 流体双向耦合：
    ///  · Boussinesq 浮力（温度 → 密度扰动 → 体力），让热场真正推动流体；
    ///  · 半拉格朗日对流（速度场输运温度），让流体反过来改写热场；
    ///  · 涡量约束（补回 PBF 数值耗散丢失的涡），规则网格中心差分实现。
    /// 上一轮热-结构耦合是单向的，这一轮把「热」「流体」「结构」三者闭环。
    /// </summary>
    public static class ThermoFluidCoupler
    {
        private static readonly RVec3 DownGravity = new RVec3(0f, -9.81f, 0f);

        /// <summary>Boussinesq：a = −β(T−T0)·g，g 向下时 T&gt;T0 产生向上的浮力。</summary>
        public static RVec3 BuoyancyAcceleration(float temperature, float referenceTemperature,
                                                 float thermalExpansion, RVec3 gravity)
        {
            return gravity * (-thermalExpansion * (temperature - referenceTemperature));
        }

        public static void ApplyBuoyancy(float[] temperatures, float[] vy,
                                         float referenceTemperature, float thermalExpansion,
                                         float dt, RVec3 gravity)
        {
            if (temperatures == null || vy == null) throw new ArgumentNullException(nameof(temperatures));
            int n = Math.Min(temperatures.Length, vy.Length);
            for (int i = 0; i < n; i++)
                vy[i] += BuoyancyAcceleration(temperatures[i], referenceTemperature,
                                              thermalExpansion, gravity).Y * dt;
        }

        public static RVec3 DefaultGravity => DownGravity;

        /// <summary>
        /// 半拉格朗日对流：x_back = x − u·dt，三线性插值取样。零速度场时逐位不变。
        /// </summary>
        public static float[] AdvectScalar(float[] field, float[] vx, float[] vy, float[] vz,
                                           int nx, int ny, int nz, float spacing, float dt)
        {
            if (field == null) throw new ArgumentNullException(nameof(field));
            int total = nx * ny * nz;
            if (field.Length < total) throw new ArgumentException("场长度不足");
            var dst = new float[total];
            for (int k = 0; k < nz; k++)
                for (int j = 0; j < ny; j++)
                    for (int i = 0; i < nx; i++)
                    {
                        int idx = Idx(i, j, k, nx, ny);
                        float bx = i - vx[idx] * dt / spacing;
                        float by = j - vy[idx] * dt / spacing;
                        float bz = k - vz[idx] * dt / spacing;
                        dst[idx] = Sample(field, bx, by, bz, nx, ny, nz);
                    }
            return dst;
        }

        private static int Idx(int i, int j, int k, int nx, int ny) => (k * ny + j) * nx + i;
        private static int ClampI(int v, int hi) => v < 0 ? 0 : (v > hi ? hi : v);

        private static float Sample(float[] f, float x, float y, float z, int nx, int ny, int nz)
        {
            // 上界取 nx-1 而非 nx-1.001：后者会把 i=1 压成 0.999，
            // 零速度场对流就不再逐位恒等（半拉格朗日采样的经典边界坑）。
            x = RealityKernels.Clamp(x, 0f, nx - 1f);
            y = RealityKernels.Clamp(y, 0f, ny - 1f);
            z = RealityKernels.Clamp(z, 0f, nz - 1f);
            int i0 = (int)x, j0 = (int)y, k0 = (int)z;
            int i1 = ClampI(i0 + 1, nx - 1), j1 = ClampI(j0 + 1, ny - 1), k1 = ClampI(k0 + 1, nz - 1);
            float tx = x - i0, ty = y - j0, tz = z - k0;
            float c00 = f[Idx(i0, j0, k0, nx, ny)] * (1f - tx) + f[Idx(i1, j0, k0, nx, ny)] * tx;
            float c10 = f[Idx(i0, j1, k0, nx, ny)] * (1f - tx) + f[Idx(i1, j1, k0, nx, ny)] * tx;
            float c01 = f[Idx(i0, j0, k1, nx, ny)] * (1f - tx) + f[Idx(i1, j0, k1, nx, ny)] * tx;
            float c11 = f[Idx(i0, j1, k1, nx, ny)] * (1f - tx) + f[Idx(i1, j1, k1, nx, ny)] * tx;
            float c0 = c00 * (1f - ty) + c10 * ty;
            float c1 = c01 * (1f - ty) + c11 * ty;
            return c0 * (1f - tz) + c1 * tz;
        }

        /// <summary>
        /// 涡量约束力 f = ε (N × ω)，N = ∇|ω| / |∇|ω||。
        /// 用中心差分在规则网格上求 curl 与梯度；边界用最近有效格。
        /// 均匀速度场 → ω=0 → 力为 0；剪切场 → 非零。
        /// </summary>
        public static void VorticityConfinement(float[] vx, float[] vy, float[] vz,
                                                int nx, int ny, int nz, float spacing,
                                                float epsilon, float[] outFx, float[] outFy, float[] outFz)
        {
            if (vx == null || outFx == null) throw new ArgumentNullException(nameof(vx));
            int total = nx * ny * nz;
            var wx = new float[total]; var wy = new float[total]; var wz = new float[total];
            var mag = new float[total];
            float inv = 1f / (2f * spacing);
            for (int k = 0; k < nz; k++)
                for (int j = 0; j < ny; j++)
                    for (int i = 0; i < nx; i++)
                    {
                        int c = Idx(i, j, k, nx, ny);
                        int xp = Idx(ClampI(i + 1, nx - 1), j, k, nx, ny);
                        int xm = Idx(ClampI(i - 1, nx - 1), j, k, nx, ny);
                        int yp = Idx(i, ClampI(j + 1, ny - 1), k, nx, ny);
                        int ym = Idx(i, ClampI(j - 1, ny - 1), k, nx, ny);
                        int zp = Idx(i, j, ClampI(k + 1, nz - 1), nx, ny);
                        int zm = Idx(i, j, ClampI(k - 1, nz - 1), nx, ny);
                        float dvzdy = (vz[yp] - vz[ym]) * inv, dvydz = (vy[zp] - vy[zm]) * inv;
                        float dvxdz = (vx[zp] - vx[zm]) * inv, dvzdx = (vz[xp] - vz[xm]) * inv;
                        float dvydx = (vy[xp] - vy[xm]) * inv, dvxdy = (vx[yp] - vx[ym]) * inv;
                        wx[c] = dvzdy - dvydz; wy[c] = dvxdz - dvzdx; wz[c] = dvydx - dvxdy;
                        mag[c] = MathF.Sqrt(wx[c] * wx[c] + wy[c] * wy[c] + wz[c] * wz[c]);
                    }
            for (int k = 0; k < nz; k++)
                for (int j = 0; j < ny; j++)
                    for (int i = 0; i < nx; i++)
                    {
                        int c = Idx(i, j, k, nx, ny);
                        int xp = Idx(ClampI(i + 1, nx - 1), j, k, nx, ny);
                        int xm = Idx(ClampI(i - 1, nx - 1), j, k, nx, ny);
                        int yp = Idx(i, ClampI(j + 1, ny - 1), k, nx, ny);
                        int ym = Idx(i, ClampI(j - 1, ny - 1), k, nx, ny);
                        int zp = Idx(i, j, ClampI(k + 1, nz - 1), nx, ny);
                        int zm = Idx(i, j, ClampI(k - 1, nz - 1), nx, ny);
                        float gx = (mag[xp] - mag[xm]) * inv, gy = (mag[yp] - mag[ym]) * inv,
                              gz = (mag[zp] - mag[zm]) * inv;
                        float gl = MathF.Sqrt(gx * gx + gy * gy + gz * gz);
                        if (gl < 1e-9f) { outFx[c] = 0f; outFy[c] = 0f; outFz[c] = 0f; continue; }
                        float nxv = gx / gl, nyv = gy / gl, nzv = gz / gl;
                        outFx[c] = epsilon * (nyv * wz[c] - nzv * wy[c]);
                        outFy[c] = epsilon * (nzv * wx[c] - nxv * wz[c]);
                        outFz[c] = epsilon * (nxv * wy[c] - nyv * wx[c]);
                    }
        }
    }
}
