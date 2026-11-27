using System;
namespace YamaEngine.Reality
{
    /// <summary>行主序 3×3 矩阵。用于形状匹配的形变梯度与极分解，避免引入新依赖。</summary>
    public readonly struct YrMat3
    {
        public readonly float M00, M01, M02, M10, M11, M12, M20, M21, M22;
        public YrMat3(float m00, float m01, float m02,
                      float m10, float m11, float m12,
                      float m20, float m21, float m22)
        { M00 = m00; M01 = m01; M02 = m02; M10 = m10; M11 = m11; M12 = m12; M20 = m20; M21 = m21; M22 = m22; }

        public static YrMat3 Identity => new YrMat3(1f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f);
        public static YrMat3 Zero => new YrMat3(0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f);

        public static YrMat3 operator +(YrMat3 a, YrMat3 b) => new YrMat3(
            a.M00 + b.M00, a.M01 + b.M01, a.M02 + b.M02,
            a.M10 + b.M10, a.M11 + b.M11, a.M12 + b.M12,
            a.M20 + b.M20, a.M21 + b.M21, a.M22 + b.M22);
        public static YrMat3 operator -(YrMat3 a, YrMat3 b) => new YrMat3(
            a.M00 - b.M00, a.M01 - b.M01, a.M02 - b.M02,
            a.M10 - b.M10, a.M11 - b.M11, a.M12 - b.M12,
            a.M20 - b.M20, a.M21 - b.M21, a.M22 - b.M22);
        public static YrMat3 operator *(YrMat3 a, float s) => new YrMat3(
            a.M00 * s, a.M01 * s, a.M02 * s, a.M10 * s, a.M11 * s, a.M12 * s,
            a.M20 * s, a.M21 * s, a.M22 * s);
        public static YrMat3 operator *(float s, YrMat3 a) => a * s;

        public YrMat3 Transpose() => new YrMat3(M00, M10, M20, M01, M11, M21, M02, M12, M22);

        public RVec3 Multiply(RVec3 v) => new RVec3(
            M00 * v.X + M01 * v.Y + M02 * v.Z,
            M10 * v.X + M11 * v.Y + M12 * v.Z,
            M20 * v.X + M21 * v.Y + M22 * v.Z);

        public float Trace() => M00 + M11 + M22;
        public float Frobenius() => MathF.Sqrt(
            M00 * M00 + M01 * M01 + M02 * M02 + M10 * M10 + M11 * M11 + M12 * M12
            + M20 * M20 + M21 * M21 + M22 * M22);

        public float Determinant() =>
            M00 * (M11 * M22 - M12 * M21)
          - M01 * (M10 * M22 - M12 * M20)
          + M02 * (M10 * M21 - M11 * M20);

        /// <summary>高斯-约当增广求逆；奇异时返回 false 并给出单位阵。</summary>
        public bool TryInverse(out YrMat3 inv)
        {
            var a = new double[3, 6];
            a[0, 0] = M00; a[0, 1] = M01; a[0, 2] = M02;
            a[1, 0] = M10; a[1, 1] = M11; a[1, 2] = M12;
            a[2, 0] = M20; a[2, 1] = M21; a[2, 2] = M22;
            a[0, 3] = 1; a[1, 4] = 1; a[2, 5] = 1;
            for (int col = 0; col < 3; col++)
            {
                int piv = col;
                for (int r = col + 1; r < 3; r++)
                    if (Math.Abs(a[r, col]) > Math.Abs(a[piv, col])) piv = r;
                if (Math.Abs(a[piv, col]) < 1e-12) { inv = Identity; return false; }
                if (piv != col)
                    for (int c = 0; c < 6; c++) { double t = a[col, c]; a[col, c] = a[piv, c]; a[piv, c] = t; }
                double p = a[col, col];
                for (int c = 0; c < 6; c++) a[col, c] /= p;
                for (int r = 0; r < 3; r++)
                {
                    if (r == col) continue;
                    double f = a[r, col];
                    if (f == 0.0) continue;
                    for (int c = 0; c < 6; c++) a[r, c] -= f * a[col, c];
                }
            }
            inv = new YrMat3((float)a[0, 3], (float)a[0, 4], (float)a[0, 5],
                             (float)a[1, 3], (float)a[1, 4], (float)a[1, 5],
                             (float)a[2, 3], (float)a[2, 4], (float)a[2, 5]);
            return true;
        }

        /// <summary>极分解的旋转因子：Higham 迭代 R ← ½(R + R⁻ᵀ)，收敛到正交部分。</summary>
        public static YrMat3 ExtractRotation(YrMat3 a)
        {
            if (a.Determinant() <= 1e-9f) return Identity;
            YrMat3 r = a;
            for (int k = 0; k < 12; k++)
            {
                if (!r.TryInverse(out YrMat3 ri)) return Identity;
                r = (r + ri.Transpose()) * 0.5f;
            }
            return r;
        }
    }
}
