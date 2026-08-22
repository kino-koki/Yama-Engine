// ============================================================================================
//  CollisionDetectionReal.cs
//
//  山雨引擎 (ShanyuEngine) 真实碰撞检测模块 —— 纯手写实现，无外部物理库依赖。
//
//  内容：
//    - 基础数学：PhysicsVector3 / PhysicsQuaternion / PhysicsMatrix3x3 / PhysicsMatrix4x4 / PhysicsTransform
//    - GJK算法（Gilbert-Johnson-Keerthi）：凸体碰撞检测 / 距离 / 射线
//    - EPA算法（Expanding Polytope Algorithm）：穿透深度与接触法线
//    - SAT（Separating Axis Theorem）：OBB-OBB / AABB-AABB / OBB-AABB / Sphere-OBB / Capsule-Capsule / Sphere-Sphere / Sphere-AABB
//    - 射线检测：Sphere / AABB / OBB / Triangle(Moller-Trumbore) / ConvexMesh / Capsule / HeightField
//    - 碰撞形状体系：Shape枚举 + 派生类 + 凸多面体 + 高度图
//    - 碰撞分发器（ShapeCollisionDispatcher）：形状对 → 算法路由
//
//  命名空间：ShanyuEngine.Physics
//  目标框架：net8.0 / C# 8.0+
//  外部依赖：仅 System.Numerics
// ============================================================================================

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace ShanyuEngine.Physics
{
    #region ======================== 基础数学 ========================

    /// <summary>物理专用向量（扩展 System.Numerics.Vector3 不足的功能）。</summary>
    public struct PhysicsVector3
    {
        public float X, Y, Z;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public PhysicsVector3(float x, float y, float z) { X = x; Y = y; Z = z; }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public PhysicsVector3(Vector3 v) { X = v.X; Y = v.Y; Z = v.Z; }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static implicit operator Vector3(PhysicsVector3 v) => new Vector3(v.X, v.Y, v.Z);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static implicit operator PhysicsVector3(Vector3 v) => new PhysicsVector3(v.X, v.Y, v.Z);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static PhysicsVector3 operator +(PhysicsVector3 a, PhysicsVector3 b)
            => new PhysicsVector3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static PhysicsVector3 operator -(PhysicsVector3 a, PhysicsVector3 b)
            => new PhysicsVector3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static PhysicsVector3 operator -(PhysicsVector3 v)
            => new PhysicsVector3(-v.X, -v.Y, -v.Z);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static PhysicsVector3 operator *(PhysicsVector3 v, float s)
            => new PhysicsVector3(v.X * s, v.Y * s, v.Z * s);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static PhysicsVector3 operator *(float s, PhysicsVector3 v)
            => new PhysicsVector3(v.X * s, v.Y * s, v.Z * s);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static PhysicsVector3 operator *(PhysicsVector3 a, PhysicsVector3 b)
            => new PhysicsVector3(a.X * b.X, a.Y * b.Y, a.Z * b.Z);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static PhysicsVector3 operator /(PhysicsVector3 v, float s)
        {
            float inv = 1.0f / s;
            return new PhysicsVector3(v.X * inv, v.Y * inv, v.Z * inv);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float Dot(PhysicsVector3 b) => X * b.X + Y * b.Y + Z * b.Z;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public PhysicsVector3 Cross(PhysicsVector3 b)
            => new PhysicsVector3(
                Y * b.Z - Z * b.Y,
                Z * b.X - X * b.Z,
                X * b.Y - Y * b.X);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float LengthSquared() => X * X + Y * Y + Z * Z;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float Length() => (float)Math.Sqrt(LengthSquared());

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public PhysicsVector3 Normalized()
        {
            float len = Length();
            if (len < 1e-12f) return Zero;
            return this / len;
        }

        /// <summary>反射向量：v - 2 * dot(v,n) * n</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public PhysicsVector3 Reflect(PhysicsVector3 normal)
        {
            float d = 2.0f * Dot(normal);
            return this - d * normal;
        }

        /// <summary>投影到 b 方向上的分量。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public PhysicsVector3 Project(PhysicsVector3 b)
        {
            float lenSq = b.LengthSquared();
            if (lenSq < 1e-12f) return Zero;
            return b * (Dot(b) / lenSq);
        }

        /// <summary>返回垂直于 v 的一个向量。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public PhysicsVector3 Perpendicular()
        {
            PhysicsVector3 a = Abs();
            if (a.X <= a.Y && a.X <= a.Z)
                return new PhysicsVector3(0, -Z, Y);
            if (a.Y <= a.Z)
                return new PhysicsVector3(-Z, 0, X);
            return new PhysicsVector3(-Y, X, 0);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static PhysicsVector3 Abs(PhysicsVector3 v)
            => new PhysicsVector3(Math.Abs(v.X), Math.Abs(v.Y), Math.Abs(v.Z));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public PhysicsVector3 Abs() => Abs(this);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static PhysicsVector3 Min(PhysicsVector3 a, PhysicsVector3 b)
            => new PhysicsVector3(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Min(a.Z, b.Z));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static PhysicsVector3 Max(PhysicsVector3 a, PhysicsVector3 b)
            => new PhysicsVector3(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), Math.Max(a.Z, b.Z));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static PhysicsVector3 Lerp(PhysicsVector3 a, PhysicsVector3 b, float t)
            => new PhysicsVector3(
                a.X + (b.X - a.X) * t,
                a.Y + (b.Y - a.Y) * t,
                a.Z + (b.Z - a.Z) * t);

        /// <summary>球面线性插值。</summary>
        public static PhysicsVector3 Slerp(PhysicsVector3 a, PhysicsVector3 b, float t)
        {
            float lenA = a.Length(), lenB = b.Length();
            if (lenA < 1e-12f || lenB < 1e-12f) return Lerp(a, b, t);
            PhysicsVector3 nA = a / lenA, nB = b / lenB;
            float dot = nA.Dot(nB);
            dot = Math.Clamp(dot, -1.0f, 1.0f);
            float theta = (float)Math.Acos(dot);
            if (theta < 1e-6f) return Lerp(a, b, t);
            float sinTheta = (float)Math.Sin(theta);
            float sa = (float)Math.Sin((1.0f - t) * theta) / sinTheta;
            float sb = (float)Math.Sin(t * theta) / sinTheta;
            return nA * sa * lenA + nB * sb * lenB;
        }

        /// <summary>绕任意轴旋转（Rodrigues公式）。</summary>
        public PhysicsVector3 RotateBy(PhysicsVector3 axis, float angleRadians)
        {
            PhysicsVector3 n = axis.Normalized();
            float cos = (float)Math.Cos(angleRadians);
            float sin = (float)Math.Sin(angleRadians);
            return this * cos + n.Cross(this) * sin + n * n.Dot(this) * (1.0f - cos);
        }

        /// <summary>绕任意轴旋转角度。</summary>
        public static float Angle(PhysicsVector3 from, PhysicsVector3 to)
        {
            float lenA = from.Length(), lenB = to.Length();
            if (lenA < 1e-12f || lenB < 1e-12f) return 0;
            float d = from.Dot(to) / (lenA * lenB);
            d = Math.Clamp(d, -1.0f, 1.0f);
            return (float)Math.Acos(d);
        }

        /// <summary>两向量之间的旋转轴。</summary>
        public static PhysicsVector3 Axis(PhysicsVector3 from, PhysicsVector3 to)
        {
            PhysicsVector3 cross = from.Cross(to);
            float len = cross.Length();
            if (len < 1e-12f) return new PhysicsVector3(0, 1, 0);
            return cross / len;
        }

        public static readonly PhysicsVector3 Zero = new PhysicsVector3(0, 0, 0);
        public static readonly PhysicsVector3 One = new PhysicsVector3(1, 1, 1);
        public static readonly PhysicsVector3 UnitX = new PhysicsVector3(1, 0, 0);
        public static readonly PhysicsVector3 UnitY = new PhysicsVector3(0, 1, 0);
        public static readonly PhysicsVector3 UnitZ = new PhysicsVector3(0, 0, 1);
        public static readonly PhysicsVector3 MaxValue = new PhysicsVector3(float.MaxValue, float.MaxValue, float.MaxValue);
        public static readonly PhysicsVector3 MinValue = new PhysicsVector3(float.MinValue, float.MinValue, float.MinValue);

        public override string ToString() => $"({X:F4}, {Y:F4}, {Z:F4})";
    }

    /// <summary>物理专用二维向量（与 <see cref="PhysicsVector3"/> 同风格，用于切线冲量等二维量）。</summary>
    public struct PhysicsVector2
    {
        public float X, Y;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public PhysicsVector2(float x, float y) { X = x; Y = y; }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public PhysicsVector2(Vector2 v) { X = v.X; Y = v.Y; }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static implicit operator Vector2(PhysicsVector2 v) => new Vector2(v.X, v.Y);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static implicit operator PhysicsVector2(Vector2 v) => new PhysicsVector2(v.X, v.Y);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static PhysicsVector2 operator +(PhysicsVector2 a, PhysicsVector2 b)
            => new PhysicsVector2(a.X + b.X, a.Y + b.Y);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static PhysicsVector2 operator -(PhysicsVector2 a, PhysicsVector2 b)
            => new PhysicsVector2(a.X - b.X, a.Y - b.Y);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static PhysicsVector2 operator -(PhysicsVector2 v)
            => new PhysicsVector2(-v.X, -v.Y);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static PhysicsVector2 operator *(PhysicsVector2 v, float s)
            => new PhysicsVector2(v.X * s, v.Y * s);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static PhysicsVector2 operator *(float s, PhysicsVector2 v)
            => new PhysicsVector2(v.X * s, v.Y * s);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static PhysicsVector2 operator *(PhysicsVector2 a, PhysicsVector2 b)
            => new PhysicsVector2(a.X * b.X, a.Y * b.Y);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static PhysicsVector2 operator /(PhysicsVector2 v, float s)
        {
            float inv = 1.0f / s;
            return new PhysicsVector2(v.X * inv, v.Y * inv);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float Dot(PhysicsVector2 b) => X * b.X + Y * b.Y;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float LengthSquared() => X * X + Y * Y;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float Length() => MathF.Sqrt(X * X + Y * Y);

        public static readonly PhysicsVector2 Zero = new PhysicsVector2(0, 0);
        public static readonly PhysicsVector2 One = new PhysicsVector2(1, 1);
        public static readonly PhysicsVector2 UnitX = new PhysicsVector2(1, 0);
        public static readonly PhysicsVector2 UnitY = new PhysicsVector2(0, 1);

        public override string ToString() => $"({X:F4}, {Y:F4})";
    }

    /// <summary>物理四元数。</summary>
    public struct PhysicsQuaternion
    {
        public float X, Y, Z, W;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public PhysicsQuaternion(float x, float y, float z, float w) { X = x; Y = y; Z = z; W = w; }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public PhysicsQuaternion(Quaternion q) { X = q.X; Y = q.Y; Z = q.Z; W = q.W; }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static implicit operator Quaternion(PhysicsQuaternion q) => new Quaternion(q.X, q.Y, q.Z, q.W);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static implicit operator PhysicsQuaternion(Quaternion q) => new PhysicsQuaternion(q.X, q.Y, q.Z, q.W);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static PhysicsQuaternion operator *(PhysicsQuaternion a, PhysicsQuaternion b)
        {
            return new PhysicsQuaternion(
                a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
                a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X,
                a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W,
                a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public PhysicsQuaternion Conjugate() => new PhysicsQuaternion(-X, -Y, -Z, W);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float LengthSquared() => X * X + Y * Y + Z * Z + W * W;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float Length() => (float)Math.Sqrt(LengthSquared());

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public PhysicsQuaternion Normalized()
        {
            float len = Length();
            if (len < 1e-12f) return Identity;
            float inv = 1.0f / len;
            return new PhysicsQuaternion(X * inv, Y * inv, Z * inv, W * inv);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public PhysicsQuaternion Inverse()
        {
            float lenSq = LengthSquared();
            if (lenSq < 1e-12f) return Identity;
            float inv = 1.0f / lenSq;
            return new PhysicsQuaternion(-X * inv, -Y * inv, -Z * inv, W * inv);
        }

        /// <summary>从轴-角构造四元数。</summary>
        public static PhysicsQuaternion FromAxisAngle(PhysicsVector3 axis, float angleRadians)
        {
            float halfAngle = angleRadians * 0.5f;
            float sin = (float)Math.Sin(halfAngle);
            float cos = (float)Math.Cos(halfAngle);
            PhysicsVector3 n = axis.Normalized();
            return new PhysicsQuaternion(n.X * sin, n.Y * sin, n.Z * sin, cos);
        }

        /// <summary>转换为3x3旋转矩阵。</summary>
        public PhysicsMatrix3x3 ToMatrix3x3()
        {
            float xx = X * X, yy = Y * Y, zz = Z * Z;
            float xy = X * Y, xz = X * Z, yz = Y * Z;
            float wx = W * X, wy = W * Y, wz = W * Z;
            return new PhysicsMatrix3x3(
                1.0f - 2.0f * (yy + zz), 2.0f * (xy - wz), 2.0f * (xz + wy),
                2.0f * (xy + wz), 1.0f - 2.0f * (xx + zz), 2.0f * (yz - wx),
                2.0f * (xz - wy), 2.0f * (yz + wx), 1.0f - 2.0f * (xx + yy));
        }

        /// <summary>线性插值（需要归一化）。</summary>
        public static PhysicsQuaternion Lerp(PhysicsQuaternion a, PhysicsQuaternion b, float t)
        {
            if (a.Dot(b) < 0) b = new PhysicsQuaternion(-b.X, -b.Y, -b.Z, -b.W);
            return new PhysicsQuaternion(
                a.X + (b.X - a.X) * t,
                a.Y + (b.Y - a.Y) * t,
                a.Z + (b.Z - a.Z) * t,
                a.W + (b.W - a.W) * t).Normalized();
        }

        /// <summary>球面线性插值。</summary>
        public static PhysicsQuaternion Slerp(PhysicsQuaternion a, PhysicsQuaternion b, float t)
        {
            float dot = a.Dot(b);
            PhysicsQuaternion q = b;
            if (dot < 0) { dot = -dot; q = new PhysicsQuaternion(-b.X, -b.Y, -b.Z, -b.W); }
            if (dot > 0.9995f) return Lerp(a, q, t);
            float theta = (float)Math.Acos(Math.Clamp(dot, -1f, 1f));
            float sinTheta = (float)Math.Sin(theta);
            float sa = (float)Math.Sin((1.0f - t) * theta) / sinTheta;
            float sb = (float)Math.Sin(t * theta) / sinTheta;
            return new PhysicsQuaternion(
                sa * a.X + sb * q.X, sa * a.Y + sb * q.Y,
                sa * a.Z + sb * q.Z, sa * a.W + sb * q.W).Normalized();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private float Dot(PhysicsQuaternion b) => X * b.X + Y * b.Y + Z * b.Z + W * b.W;

        public static readonly PhysicsQuaternion Identity = new PhysicsQuaternion(0, 0, 0, 1);
    }

    /// <summary>物理3x3矩阵（列主序存储）。</summary>
    public struct PhysicsMatrix3x3
    {
        public float M00, M01, M02; // Column 0
        public float M10, M11, M12; // Column 1
        public float M20, M21, M22; // Column 2

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public PhysicsMatrix3x3(
            float m00, float m01, float m02,
            float m10, float m11, float m12,
            float m20, float m21, float m22)
        {
            M00 = m00; M01 = m01; M02 = m02;
            M10 = m10; M11 = m11; M12 = m12;
            M20 = m20; M21 = m21; M22 = m22;
        }

        /// <summary>矩阵 × 向量。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public PhysicsVector3 Multiply(PhysicsVector3 v)
        {
            return new PhysicsVector3(
                M00 * v.X + M10 * v.Y + M20 * v.Z,
                M01 * v.X + M11 * v.Y + M21 * v.Z,
                M02 * v.X + M12 * v.Y + M22 * v.Z);
        }

        /// <summary>矩阵 × 矩阵。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public PhysicsMatrix3x3 Multiply(PhysicsMatrix3x3 b)
        {
            return new PhysicsMatrix3x3(
                M00 * b.M00 + M10 * b.M01 + M20 * b.M02,
                M01 * b.M00 + M11 * b.M01 + M21 * b.M02,
                M02 * b.M00 + M12 * b.M01 + M22 * b.M02,
                M00 * b.M10 + M10 * b.M11 + M20 * b.M12,
                M01 * b.M10 + M11 * b.M11 + M21 * b.M12,
                M02 * b.M10 + M12 * b.M11 + M22 * b.M12,
                M00 * b.M20 + M10 * b.M21 + M20 * b.M22,
                M01 * b.M20 + M11 * b.M21 + M21 * b.M22,
                M02 * b.M20 + M12 * b.M21 + M22 * b.M22);
        }

        /// <summary>转置。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public PhysicsMatrix3x3 Transpose()
        {
            return new PhysicsMatrix3x3(
                M00, M10, M20,
                M01, M11, M21,
                M02, M12, M22);
        }

        /// <summary>行列式。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float Determinant()
        {
            return M00 * (M11 * M22 - M12 * M21)
                 - M01 * (M10 * M22 - M12 * M20)
                 + M02 * (M10 * M21 - M11 * M20);
        }

        /// <summary>逆矩阵（3x3伴随矩阵法）。</summary>
        public PhysicsMatrix3x3 Inverse()
        {
            float det = Determinant();
            if (Math.Abs(det) < 1e-12f) return Identity;
            float invDet = 1.0f / det;
            return new PhysicsMatrix3x3(
                (M11 * M22 - M12 * M21) * invDet,
                (M02 * M21 - M01 * M22) * invDet,
                (M01 * M12 - M02 * M11) * invDet,
                (M12 * M20 - M10 * M22) * invDet,
                (M00 * M22 - M02 * M20) * invDet,
                (M02 * M10 - M00 * M12) * invDet,
                (M10 * M21 - M11 * M20) * invDet,
                (M01 * M20 - M00 * M21) * invDet,
                (M00 * M11 - M01 * M10) * invDet);
        }

        /// <summary>用三个基向量设置矩阵。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static PhysicsMatrix3x3 SetBasis(PhysicsVector3 col0, PhysicsVector3 col1, PhysicsVector3 col2)
            => new PhysicsMatrix3x3(col0.X, col1.X, col2.X, col0.Y, col1.Y, col2.Y, col0.Z, col1.Z, col2.Z);

        /// <summary>提取列基向量。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ExtractBasis(out PhysicsVector3 col0, out PhysicsVector3 col1, out PhysicsVector3 col2)
        {
            col0 = new PhysicsVector3(M00, M10, M20);
            col1 = new PhysicsVector3(M01, M11, M21);
            col2 = new PhysicsVector3(M02, M12, M22);
        }

        public static readonly PhysicsMatrix3x3 Identity = new PhysicsMatrix3x3(
            1, 0, 0, 0, 1, 0, 0, 0, 1);
    }

    /// <summary>物理4x4变换矩阵（列主序）。</summary>
    public struct PhysicsMatrix4x4
    {
        public float M00, M01, M02, M03;
        public float M10, M11, M12, M13;
        public float M20, M21, M22, M23;
        public float M30, M31, M32, M33;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public PhysicsMatrix4x4(
            float m00, float m01, float m02, float m03,
            float m10, float m11, float m12, float m13,
            float m20, float m21, float m22, float m23,
            float m30, float m31, float m32, float m33)
        {
            M00 = m00; M01 = m01; M02 = m02; M03 = m03;
            M10 = m10; M11 = m11; M12 = m12; M13 = m13;
            M20 = m20; M21 = m21; M22 = m22; M23 = m23;
            M30 = m30; M31 = m31; M32 = m32; M33 = m33;
        }

        /// <summary>矩阵 × 向量（齐次坐标，w=1）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public PhysicsVector3 MultiplyPoint(PhysicsVector3 v)
        {
            float x = M00 * v.X + M10 * v.Y + M20 * v.Z + M30;
            float y = M01 * v.X + M11 * v.Y + M21 * v.Z + M31;
            float z = M02 * v.X + M12 * v.Y + M22 * v.Z + M32;
            return new PhysicsVector3(x, y, z);
        }

        /// <summary>矩阵 × 方向向量（w=0，忽略平移）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public PhysicsVector3 MultiplyDirection(PhysicsVector3 v)
        {
            return new PhysicsVector3(
                M00 * v.X + M10 * v.Y + M20 * v.Z,
                M01 * v.X + M11 * v.Y + M21 * v.Z,
                M02 * v.X + M12 * v.Y + M22 * v.Z);
        }

        /// <summary>矩阵 × 矩阵。</summary>
        public PhysicsMatrix4x4 Multiply(PhysicsMatrix4x4 b)
        {
            return new PhysicsMatrix4x4(
                M00 * b.M00 + M10 * b.M01 + M20 * b.M02 + M30 * b.M03,
                M01 * b.M00 + M11 * b.M01 + M21 * b.M02 + M31 * b.M03,
                M02 * b.M00 + M12 * b.M01 + M22 * b.M02 + M32 * b.M03,
                M03 * b.M00 + M13 * b.M01 + M23 * b.M02 + M33 * b.M03,
                M00 * b.M10 + M10 * b.M11 + M20 * b.M12 + M30 * b.M13,
                M01 * b.M10 + M11 * b.M11 + M21 * b.M12 + M31 * b.M13,
                M02 * b.M10 + M12 * b.M11 + M22 * b.M12 + M32 * b.M13,
                M03 * b.M10 + M13 * b.M11 + M23 * b.M12 + M33 * b.M13,
                M00 * b.M20 + M10 * b.M21 + M20 * b.M22 + M30 * b.M23,
                M01 * b.M20 + M11 * b.M21 + M21 * b.M22 + M31 * b.M23,
                M02 * b.M20 + M12 * b.M21 + M22 * b.M22 + M32 * b.M23,
                M03 * b.M20 + M13 * b.M21 + M23 * b.M22 + M33 * b.M23,
                M00 * b.M30 + M10 * b.M31 + M20 * b.M32 + M30 * b.M33,
                M01 * b.M30 + M11 * b.M31 + M21 * b.M32 + M31 * b.M33,
                M02 * b.M30 + M12 * b.M31 + M22 * b.M32 + M32 * b.M33,
                M03 * b.M30 + M13 * b.M31 + M23 * b.M32 + M33 * b.M33);
        }

        /// <summary>提取左上角 3x3。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public PhysicsMatrix3x3 ToMatrix3x3()
            => new PhysicsMatrix3x3(M00, M01, M02, M10, M11, M12, M20, M21, M22);

        /// <summary>从位置/旋转/缩放构造。</summary>
        public static PhysicsMatrix4x4 FromTRS(PhysicsVector3 translation, PhysicsQuaternion rotation, PhysicsVector3 scale)
        {
            PhysicsMatrix3x3 rotMat = rotation.ToMatrix3x3();
            PhysicsMatrix3x3 scaleMat = PhysicsMatrix3x3.SetBasis(
                new PhysicsVector3(scale.X, 0, 0),
                new PhysicsVector3(0, scale.Y, 0),
                new PhysicsVector3(0, 0, scale.Z));
            PhysicsMatrix3x3 rs = rotMat.Multiply(scaleMat);
            return new PhysicsMatrix4x4(
                rs.M00, rs.M01, rs.M02, 0,
                rs.M10, rs.M11, rs.M12, 0,
                rs.M20, rs.M21, rs.M22, 0,
                translation.X, translation.Y, translation.Z, 1);
        }

        /// <summary>提取TRS分量。</summary>
        public void ExtractTRS(out PhysicsVector3 translation, out PhysicsQuaternion rotation, out PhysicsVector3 scale)
        {
            translation = new PhysicsVector3(M30, M31, M32);
            PhysicsVector3 col0 = new PhysicsVector3(M00, M10, M20);
            PhysicsVector3 col1 = new PhysicsVector3(M01, M11, M21);
            PhysicsVector3 col2 = new PhysicsVector3(M02, M12, M22);
            scale = new PhysicsVector3(col0.Length(), col1.Length(), col2.Length());
            if (scale.X > 1e-12f) col0 = col0 / scale.X;
            if (scale.Y > 1e-12f) col1 = col1 / scale.Y;
            if (scale.Z > 1e-12f) col2 = col2 / scale.Z;
            PhysicsMatrix3x3 rotMat = PhysicsMatrix3x3.SetBasis(col0, col1, col2);
            rotation = FromMatrix3x3(rotMat);
        }

        /// <summary>4x4矩阵的逆（分块法）。</summary>
        public PhysicsMatrix4x4 Inverse()
        {
            PhysicsMatrix3x3 rot = ToMatrix3x3();
            PhysicsMatrix3x3 invRot = rot.Inverse();
            PhysicsVector3 t = new PhysicsVector3(M30, M31, M32);
            PhysicsVector3 invT = invRot.Multiply(-t);
            return new PhysicsMatrix4x4(
                invRot.M00, invRot.M01, invRot.M02, 0,
                invRot.M10, invRot.M11, invRot.M12, 0,
                invRot.M20, invRot.M21, invRot.M22, 0,
                invT.X, invT.Y, invT.Z, 1);
        }

        /// <summary>从3x3矩阵提取四元数（Shepperd法）。</summary>
        private static PhysicsQuaternion FromMatrix3x3(PhysicsMatrix3x3 m)
        {
            float trace = m.M00 + m.M11 + m.M22;
            if (trace > 0)
            {
                float s = (float)Math.Sqrt(trace + 1.0f) * 2.0f;
                float invS = 1.0f / s;
                return new PhysicsQuaternion(
                    (m.M21 - m.M12) * invS,
                    (m.M02 - m.M20) * invS,
                    (m.M10 - m.M01) * invS,
                    0.25f * s);
            }
            if (m.M00 > m.M11 && m.M00 > m.M22)
            {
                float s = (float)Math.Sqrt(1.0f + m.M00 - m.M11 - m.M22) * 2.0f;
                float invS = 1.0f / s;
                return new PhysicsQuaternion(
                    0.25f * s,
                    (m.M01 + m.M10) * invS,
                    (m.M02 + m.M20) * invS,
                    (m.M21 - m.M12) * invS);
            }
            if (m.M11 > m.M22)
            {
                float s = (float)Math.Sqrt(1.0f + m.M11 - m.M00 - m.M22) * 2.0f;
                float invS = 1.0f / s;
                return new PhysicsQuaternion(
                    (m.M01 + m.M10) * invS,
                    0.25f * s,
                    (m.M12 + m.M21) * invS,
                    (m.M02 - m.M20) * invS);
            }
            float s2 = (float)Math.Sqrt(1.0f + m.M22 - m.M00 - m.M11) * 2.0f;
            float invS2 = 1.0f / s2;
            return new PhysicsQuaternion(
                (m.M02 + m.M20) * invS2,
                (m.M12 + m.M21) * invS2,
                0.25f * s2,
                (m.M10 - m.M01) * invS2);
        }

        public static readonly PhysicsMatrix4x4 Identity = new PhysicsMatrix4x4(
            1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1);
    }

    /// <summary>刚体变换（位置 + 旋转 + 缩放）。</summary>
    public struct PhysicsTransform
    {
        public PhysicsVector3 Position;
        public PhysicsQuaternion Rotation;
        public PhysicsVector3 Scale;

        public PhysicsTransform(PhysicsVector3 position, PhysicsQuaternion rotation, PhysicsVector3 scale)
        {
            Position = position; Rotation = rotation; Scale = scale;
        }

        public PhysicsTransform(PhysicsVector3 position, PhysicsQuaternion rotation)
            : this(position, rotation, PhysicsVector3.One) { }

        public PhysicsMatrix4x4 ToMatrix() => PhysicsMatrix4x4.FromTRS(Position, Rotation, Scale);

        public PhysicsTransform Inverse()
        {
            PhysicsQuaternion invRot = Rotation.Inverse();
            PhysicsVector3 invScale = new PhysicsVector3(
                Scale.X > 1e-12f ? 1.0f / Scale.X : 0,
                Scale.Y > 1e-12f ? 1.0f / Scale.Y : 0,
                Scale.Z > 1e-12f ? 1.0f / Scale.Z : 0);
            PhysicsVector3 invPos = invRot.Multiply(invScale * -Position);
            return new PhysicsTransform(invPos, invRot, invScale);
        }

        /// <summary>变换一个点（w=1）。</summary>
        public PhysicsVector3 TransformPoint(PhysicsVector3 p) => ToMatrix().MultiplyPoint(p);

        /// <summary>变换一个方向（w=0，忽略平移）。</summary>
        public PhysicsVector3 TransformDirection(PhysicsVector3 d)
            => Rotation.ToMatrix3x3().Multiply(d);

        /// <summary>变换一个向量（方向+缩放，无平移）。</summary>
        public PhysicsVector3 TransformVector(PhysicsVector3 v)
            => Rotation.ToMatrix3x3().Multiply(new PhysicsVector3(v.X * Scale.X, v.Y * Scale.Y, v.Z * Scale.Z));

        public static readonly PhysicsTransform Identity = new PhysicsTransform(
            PhysicsVector3.Zero, PhysicsQuaternion.Identity, PhysicsVector3.One);
    }

    #endregion

    #region ======================== GJK 算法 ========================

    /// <summary>GJK支持函数委托。给定方向d，返回物体上沿d最远的点。</summary>
    public delegate PhysicsVector3 GJKSupportFunction(PhysicsVector3 direction);

    /// <summary>GJK单纯形（1~4个顶点）。</summary>
    public struct GJKSimplex
    {
        private const int MAX_VERTICES = 4;
        public int Count;
        public PhysicsVector3 A, B, C, D;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Clear() { Count = 0; }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void AddVertex(PhysicsVector3 v) { SetVertex(Count, v); Count++; }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void SetVertex(int idx, PhysicsVector3 v)
        {
            switch (idx)
            {
                case 0: A = v; break;
                case 1: B = v; break;
                case 2: C = v; break;
                case 3: D = v; break;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public PhysicsVector3 GetVertex(int idx)
        {
            return idx switch { 0 => A, 1 => B, 2 => C, 3 => D, _ => PhysicsVector3.Zero };
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void RemoveVertex(int idx)
        {
            for (int i = idx; i < Count - 1; i++)
                SetVertex(i, GetVertex(i + 1));
            Count--;
        }

        /// <summary>计算单纯形上离原点最近的点，以及搜索方向。返回 true 表示单纯形包含原点。</summary>
        public bool ClosestToOrigin(out PhysicsVector3 closest, out PhysicsVector3 direction)
        {
            closest = PhysicsVector3.Zero;
            direction = PhysicsVector3.Zero;

            switch (Count)
            {
                case 1:
                    closest = A;
                    direction = (-A).Normalized();
                    return false;

                case 2:
                    return ClosestLineSegment(out closest, out direction);

                case 3:
                    return ClosestTriangle(out closest, out direction);

                case 4:
                    return ClosestTetrahedron(out closest, out direction);
            }

            return false;
        }

        /// <summary>线段（2顶点）最近点。</summary>
        private bool ClosestLineSegment(out PhysicsVector3 closest, out PhysicsVector3 direction)
        {
            PhysicsVector3 ab = B - A;
            PhysicsVector3 ao = -A;
            float d = ab.Dot(ao);
            if (d <= 0)
            {
                closest = A;
                direction = ao.Normalized();
                RemoveVertex(1);
                return false;
            }
            float lenSq = ab.LengthSquared();
            PhysicsVector3 bo = -B;
            float e = ab.Dot(bo);
            if (e >= 0)
            {
                closest = B;
                direction = bo.Normalized();
                RemoveVertex(0);
                Count = 1;
                SetVertex(0, B);
                return false;
            }
            float t = Math.Clamp(d / lenSq, 0, 1);
            closest = A + t * ab;
            direction = (-closest).Normalized();
            return false;
        }

        /// <summary>三角形（3顶点）最近点。</summary>
        private bool ClosestTriangle(out PhysicsVector3 closest, out PhysicsVector3 direction)
        {
            PhysicsVector3 ab = B - A, ac = C - A;
            PhysicsVector3 ao = -A;
            PhysicsVector3 abc = ab.Cross(ac);
            float normalDotAO = abc.Dot(ao);

            if (normalDotAO > 0) abc = -abc;
            PhysicsVector3 abPerp = abc.Cross(ab);
            PhysicsVector3 acPerp = ac.Cross(abc);

            if (abPerp.Dot(ao) > 0)
            {
                RemoveVertex(2);
                Count = 2;
                ClosestLineSegment(out closest, out direction);
                return false;
            }
            if (acPerp.Dot(ao) > 0)
            {
                RemoveVertex(0);
                Count = 2;
                SetVertex(0, B); SetVertex(1, C);
                ClosestLineSegment(out closest, out direction);
                return false;
            }

            if (normalDotAO < 0)
            {
                closest = A;
                direction = abc.Normalized();
                return false;
            }

            closest = PhysicsVector3.Zero;
            return true;
        }

        /// <summary>四面体（4顶点）最近点 —— 完整 GJK 单纯形进化。</summary>
        private bool ClosestTetrahedron(out PhysicsVector3 closest, out PhysicsVector3 direction)
        {
            PhysicsVector3 ab = B - A, ac = C - A, ad = D - A;
            PhysicsVector3 ao = -A;
            PhysicsVector3 abc = ab.Cross(ac);
            PhysicsVector3 abd = ab.Cross(ad);
            PhysicsVector3 acd = ac.Cross(ad);

            // 检测原点是否在各面外侧
            bool insideABC = abc.Dot(ad) < 0;
            bool insideABD = abd.Dot(ac) < 0;
            bool insideACD = acd.Dot(ab) < 0;

            if (!insideABC && !insideABD && !insideACD)
            {
                // 原点在四面体外部
                float distABC = abc.Dot(ao);
                float distABD = abd.Dot(ao);
                float distACD = acd.Dot(ao);

                if (distABC > distABD && distABC > distACD)
                {
                    if (abc.Dot(ao) > 0)
                    {
                        RemoveVertex(3);
                        Count = 3;
                    }
                    else
                    {
                        RemoveVertex(2);
                        Count = 3;
                    }
                }
                else if (distABD > distACD)
                {
                    if (abd.Dot(ao) > 0)
                    {
                        PhysicsVector3 tmp = A; A = C; C = D; D = tmp;
                        Count = 3;
                    }
                    else
                    {
                        PhysicsVector3 tmp = A; A = B; B = D; D = tmp;
                        Count = 3;
                    }
                }
                else
                {
                    if (acd.Dot(ao) > 0)
                    {
                        RemoveVertex(1);
                        Count = 3;
                    }
                    else
                    {
                        RemoveVertex(0);
                        Count = 3;
                        A = B; B = C; C = D;
                    }
                }

                ClosestTriangle(out closest, out direction);
                return false;
            }

            closest = PhysicsVector3.Zero;
            direction = PhysicsVector3.Zero;
            return true;
        }

        /// <summary>退化检测：检查单纯形是否退化（顶点重合或共线/共面冗余）。</summary>
        public bool IsDegenerate(float epsilon = 1e-6f)
        {
            for (int i = 0; i < Count; i++)
                for (int j = i + 1; j < Count; j++)
                    if ((GetVertex(i) - GetVertex(j)).LengthSquared() < epsilon * epsilon)
                        return true;
            return false;
        }
    }

    /// <summary>GJK碰撞检测结果。</summary>
    public struct GJKResult
    {
        public bool Intersecting;
        public bool Separated;
        public PhysicsVector3 SeparationDirection;
        public float SeparationDistance;
        public GJKSimplex Simplex;
    }

    /// <summary>GJK算法实现。</summary>
    public static class GJK
    {
        private const int MAX_ITERATIONS = 64;
        private const float TOLERANCE = 1e-6f;

        /// <summary>Minkowski差支持函数。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static PhysicsVector3 Support(GJKSupportFunction supportA, GJKSupportFunction supportB, PhysicsVector3 d)
        {
            PhysicsVector3 pA = supportA(d);
            PhysicsVector3 pB = supportB(-d);
            return pA - pB;
        }

        /// <summary>GJK碰撞检测。</summary>
        public static GJKResult Intersect(GJKSupportFunction supportA, GJKSupportFunction supportB)
        {
            GJKResult result = default;
            GJKSimplex simplex = default;
            simplex.Clear();

            PhysicsVector3 direction = PhysicsVector3.UnitX;
            PhysicsVector3 startPoint = Support(supportA, supportB, direction);
            simplex.AddVertex(startPoint);
            direction = -startPoint;

            for (int i = 0; i < MAX_ITERATIONS; i++)
            {
                if (direction.LengthSquared() < TOLERANCE * TOLERANCE)
                {
                    result.Intersecting = true;
                    result.Simplex = simplex;
                    return result;
                }

                PhysicsVector3 newPoint = Support(supportA, supportB, direction);
                if (newPoint.Dot(direction) + TOLERANCE < 0)
                {
                    result.Separated = true;
                    result.SeparationDirection = direction;
                    result.SeparationDistance = newPoint.Dot(direction);
                    return result;
                }

                simplex.AddVertex(newPoint);

                if (simplex.Count > MAX_VERTICES) simplex.Count = MAX_VERTICES;

                PhysicsVector3 closest;
                bool containsOrigin = simplex.ClosestToOrigin(out closest, out direction);

                if (containsOrigin)
                {
                    result.Intersecting = true;
                    result.Simplex = simplex;
                    return result;
                }

                if (closest.LengthSquared() < TOLERANCE * TOLERANCE)
                {
                    result.Intersecting = true;
                    result.Simplex = simplex;
                    return result;
                }

                if (simplex.IsDegenerate(TOLERANCE))
                {
                    result.Separated = true;
                    result.SeparationDirection = direction;
                    result.SeparationDistance = closest.Length();
                    return result;
                }
            }

            result.Separated = true;
            result.SeparationDirection = direction;
            result.SeparationDistance = 0;
            return result;
        }

        /// <summary>GJK距离计算（基于GJK子步骤 + EPA辅助或直接迭代）。</summary>
        public static float Distance(GJKSupportFunction supportA, GJKSupportFunction supportB, out PhysicsVector3 closestA, out PhysicsVector3 closestB)
        {
            closestA = PhysicsVector3.Zero;
            closestB = PhysicsVector3.Zero;

            PhysicsVector3 dir = PhysicsVector3.UnitX;
            PhysicsVector3 s = Support(supportA, supportB, dir);
            PhysicsVector3 lastDir = dir;
            float lastDistance = s.Length();

            for (int iter = 0; iter < MAX_ITERATIONS; iter++)
            {
                dir = -s.Normalized();
                if (dir.LengthSquared() < TOLERANCE * TOLERANCE) break;

                PhysicsVector3 newS = Support(supportA, supportB, dir);
                float dist = newS.Length();

                PhysicsVector3 movement = newS - s;
                float improvement = movement.Dot(dir);
                if (dist < TOLERANCE) break;
                if (improvement < TOLERANCE * dist) break;

                s = newS;
                lastDistance = dist;
            }

            float finalDist = lastDistance;
            PhysicsVector3 pA = supportA(-s.Normalized());
            PhysicsVector3 pB = supportB(s.Normalized());
            float ratio = finalDist > TOLERANCE ? 1.0f / finalDist : 0;
            closestA = pA;
            closestB = pB;

            return finalDist;
        }

        /// <summary>GJK射线检测。</summary>
        public static bool Raycast(
            GJKSupportFunction supportA, GJKSupportFunction supportB,
            PhysicsVector3 origin, PhysicsVector3 direction, float maxDistance,
            out float hitDistance)
        {
            hitDistance = 0;

            PhysicsVector3 normalizedDir = direction.Normalized();
            PhysicsVector3 d = normalizedDir;
            PhysicsVector3 p = origin;
            float lambda = 0;
            float lastLambda = 0;
            GJKSimplex simplex = default;
            simplex.Clear();

            PhysicsVector3 sA = supportA(-d);
            PhysicsVector3 sB = supportB(d);
            PhysicsVector3 v = sA - sB - p;

            if (v.Dot(d) > 0)
            {
                hitDistance = maxDistance;
                return false;
            }

            simplex.AddVertex(v);

            for (int iter = 0; iter < MAX_ITERATIONS; iter++)
            {
                PhysicsVector3 closest;
                PhysicsVector3 searchDir;
                simplex.ClosestToOrigin(out closest, out searchDir);

                if (closest.LengthSquared() < TOLERANCE * TOLERANCE)
                {
                    hitDistance = lambda;
                    return true;
                }

                PhysicsVector3 w = closest;
                float dotWd = w.Dot(d);
                if (dotWd > 0)
                {
                    float lambdaNew = lambda + (w.Length() / dotWd) * (-w.Length() - closest.Length());
                    if (lambdaNew < 0) lambdaNew = 0;
                    if (lambdaNew > maxDistance) break;
                    if (lambdaNew - lastLambda < TOLERANCE) break;
                    lastLambda = lambdaNew;
                    lambda = lambdaNew;
                }

                PhysicsVector3 newSA = supportA(-searchDir);
                PhysicsVector3 newSB = supportB(searchDir);
                PhysicsVector3 newV = newSA - newSB;
                newV = newV - p - lambda * normalizedDir;

                if (simplex.Count >= 4) simplex.Count = 3;

                if ((newV - w).Dot(searchDir) <= 0)
                {
                    hitDistance = maxDistance;
                    return false;
                }

                simplex.AddVertex(newV);
            }

            hitDistance = maxDistance;
            return false;
        }

        private const int MAX_VERTICES = 4;
    }

    #endregion

    #region ======================== EPA 算法 ========================

    /// <summary>EPA边。</summary>
    public struct EPAEdge
    {
        public int VertexA;
        public int VertexB;
        public PhysicsVector3 Normal;
        public float Length;
    }

    /// <summary>EPA三角形。</summary>
    public struct EPATriangle
    {
        public int VertexA, VertexB, VertexC;
        public PhysicsVector3 Normal;
        public float Distance;
        public bool IsFeasible;
        public int AdjacentEdgeAB;
        public int AdjacentEdgeBC;
        public int AdjacentEdgeCA;
    }

    /// <summary>EPA网格。</summary>
    public class EPAMesh
    {
        public List<PhysicsVector3> Vertices = new List<PhysicsVector3>();
        public List<EPATriangle> Triangles = new List<EPATriangle>();

        public void Clear() { Vertices.Clear(); Triangles.Clear(); }

        public void AddVertex(PhysicsVector3 v) => Vertices.Add(v);

        public int AddTriangle(int a, int b, int c)
        {
            PhysicsVector3 va = Vertices[a], vb = Vertices[b], vc = Vertices[c];
            PhysicsVector3 ab = vb - va, ac = vc - va;
            PhysicsVector3 normal = ab.Cross(ac).Normalized();
            float dist = normal.Dot(va);

            Triangles.Add(new EPATriangle
            {
                VertexA = a, VertexB = b, VertexC = c,
                Normal = normal, Distance = dist, IsFeasible = true,
                AdjacentEdgeAB = -1, AdjacentEdgeBC = -1, AdjacentEdgeCA = -1
            });
            return Triangles.Count - 1;
        }

        public void RemoveInvalidTriangles()
        {
            Triangles.RemoveAll(t => !t.IsFeasible);
        }

        /// <summary>提取轮廓（所有外露边）。</summary>
        public List<(int a, int b)> ExtractHorizon(PhysicsVector3 direction)
        {
            var horizon = new List<(int a, int b)>();
            for (int i = 0; i < Triangles.Count; i++)
            {
                var tri = Triangles[i];
                if (!tri.IsFeasible) continue;
                if (tri.Normal.Dot(direction) < 0)
                {
                    tri.IsFeasible = false;
                    Triangles[i] = tri;
                }
            }
            for (int i = 0; i < Triangles.Count; i++)
            {
                var tri = Triangles[i];
                if (!tri.IsFeasible) continue;
                if (!IsEdgeShared(i, tri.VertexA, tri.VertexB))
                    horizon.Add((tri.VertexA, tri.VertexB));
                if (!IsEdgeShared(i, tri.VertexB, tri.VertexC))
                    horizon.Add((tri.VertexB, tri.VertexC));
                if (!IsEdgeShared(i, tri.VertexC, tri.VertexA))
                    horizon.Add((tri.VertexC, tri.VertexA));
            }
            return horizon;
        }

        private bool IsEdgeShared(int triIdx, int a, int b)
        {
            for (int i = 0; i < Triangles.Count; i++)
            {
                if (i == triIdx || !Triangles[i].IsFeasible) continue;
                var t = Triangles[i];
                if ((t.VertexA == a && t.VertexB == b) || (t.VertexA == b && t.VertexB == a)) return true;
                if ((t.VertexB == a && t.VertexC == b) || (t.VertexB == b && t.VertexC == a)) return true;
                if ((t.VertexC == a && t.VertexA == b) || (t.VertexC == b && t.VertexA == a)) return true;
            }
            return false;
        }
    }

    /// <summary>EPA接触信息。</summary>
    public struct EPAContactInfo
    {
        public PhysicsVector3 Normal;
        public float Distance;
        public PhysicsVector3 ContactPoint;
    }

    /// <summary>EPA（Expanding Polytope Algorithm）实现。</summary>
    public static class EPA
    {
        private const float EPA_TOLERANCE = 1e-6f;
        private const int DEFAULT_MAX_ITERATIONS = 64;

        /// <summary>从GJK单纯形扩展EPA，计算穿透深度和接触信息。</summary>
        public static EPAContactInfo Expand(
            GJKSimplex simplex,
            GJKSupportFunction supportA,
            GJKSupportFunction supportB,
            int maxIterations = DEFAULT_MAX_ITERATIONS)
        {
            EPAContactInfo result = default;
            result.Normal = PhysicsVector3.UnitY;
            result.Distance = 0;

            EPAMesh mesh = new EPAMesh();
            for (int i = 0; i < simplex.Count; i++)
                mesh.AddVertex(simplex.GetVertex(i));

            // 从单纯形构建初始四面体
            BuildInitialTetrahedron(mesh, ref simplex);

            // 确保法线朝外（远离原点）
            for (int i = 0; i < mesh.Triangles.Count; i++)
            {
                var tri = mesh.Triangles[i];
                PhysicsVector3 v = mesh.Vertices[tri.VertexA];
                if (tri.Normal.Dot(v) < 0)
                {
                    int tmp = tri.VertexB; tri.VertexB = tri.VertexC; tri.VertexC = tmp;
                    tri.Normal = -tri.Normal;
                    tri.Distance = -tri.Distance;
                    mesh.Triangles[i] = tri;
                }
            }

            for (int iter = 0; iter < maxIterations; iter++)
            {
                // 找最近面
                int bestTriIdx = -1;
                float bestDist = float.MaxValue;
                for (int i = 0; i < mesh.Triangles.Count; i++)
                {
                    if (!mesh.Triangles[i].IsFeasible) continue;
                    float d = Math.Abs(mesh.Triangles[i].Distance);
                    if (d < bestDist)
                    {
                        bestDist = d;
                        bestTriIdx = i;
                    }
                }

                if (bestTriIdx < 0) break;

                var bestTri = mesh.Triangles[bestTriIdx];
                PhysicsVector3 newDir = bestTri.Normal;

                // 计算新支持点
                PhysicsVector3 newSupport = GJK.Support(supportA, supportB, newDir);

                // 收敛检测
                float newDist = newDir.Dot(newSupport);
                if (newDist - bestDist < EPA_TOLERANCE)
                {
                    result.Normal = bestTri.Normal;
                    result.Distance = bestDist;
                    result.ContactPoint = bestTri.Normal * bestDist * 0.5f;
                    return result;
                }

                // 移除可见面 + 提取轮廓
                List<(int a, int b)> horizon = mesh.ExtractHorizon(newDir);
                mesh.RemoveInvalidTriangles();

                if (horizon.Count == 0) break;

                int newIdx = mesh.Vertices.Count;
                mesh.AddVertex(newSupport);

                // 用轮廓 + 新点构建新面
                foreach (var edge in horizon)
                {
                    mesh.AddTriangle(edge.a, edge.b, newIdx);
                }

                // 修正新面法线
                for (int i = 0; i < mesh.Triangles.Count; i++)
                {
                    var tri = mesh.Triangles[i];
                    if (!tri.IsFeasible) continue;
                    PhysicsVector3 v = mesh.Vertices[tri.VertexA];
                    if (tri.Normal.Dot(v) < 0)
                    {
                        int tmp = tri.VertexB; tri.VertexB = tri.VertexC; tri.VertexC = tmp;
                        tri.Normal = -tri.Normal;
                        tri.Distance = -tri.Distance;
                        mesh.Triangles[i] = tri;
                    }
                }
            }

            // 未收敛，返回最佳近似
            if (mesh.Triangles.Count > 0)
            {
                float minDist = float.MaxValue;
                EPATriangle best = mesh.Triangles[0];
                foreach (var tri in mesh.Triangles)
                {
                    if (tri.IsFeasible && Math.Abs(tri.Distance) < minDist)
                    {
                        minDist = Math.Abs(tri.Distance);
                        best = tri;
                    }
                }
                result.Normal = best.Normal;
                result.Distance = Math.Abs(best.Distance);
                result.ContactPoint = best.Normal * best.Distance * 0.5f;
            }

            return result;
        }

        /// <summary>从GJK单纯形顶点构建初始EPA四面体。</summary>
        private static void BuildInitialTetrahedron(EPAMesh mesh, ref GJKSimplex simplex)
        {
            if (simplex.Count == 4) return;

            PhysicsVector3 a = simplex.A;
            PhysicsVector3 b = simplex.Count > 1 ? simplex.B : a + new PhysicsVector3(1, 0, 0);
            PhysicsVector3 c = simplex.Count > 2 ? simplex.C : a + new PhysicsVector3(0, 1, 0);
            PhysicsVector3 d = simplex.Count > 3 ? simplex.D : PhysicsVector3.Zero;

            if (simplex.Count < 3)
            {
                PhysicsVector3 ab = b - a;
                PhysicsVector3 perp = ab.Perpendicular();
                c = a + perp.Normalized() * ab.Length();
            }
            if (simplex.Count < 4)
            {
                PhysicsVector3 ab = b - a, ac = c - a;
                PhysicsVector3 normal = ab.Cross(ac);
                if (normal.LengthSquared() < EPA_TOLERANCE * EPA_TOLERANCE)
                    normal = ab.Perpendicular();
                float dist = a.Length();
                d = a + normal.Normalized() * dist;
            }

            mesh.Clear();
            mesh.AddVertex(a);
            mesh.AddVertex(b);
            mesh.AddVertex(c);
            mesh.AddVertex(d);
        }
    }

    #endregion

    #region ======================== SAT 分离轴定理 ========================

    /// <summary>方向包围盒（AABB）。</summary>
    public struct AABBShape
    {
        public PhysicsVector3 Min;
        public PhysicsVector3 Max;
        public PhysicsVector3 Center => (Min + Max) * 0.5f;
        public PhysicsVector3 Extents => (Max - Min) * 0.5f;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public AABBShape(PhysicsVector3 min, PhysicsVector3 max) { Min = min; Max = max; }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Contains(PhysicsVector3 p)
            => p.X >= Min.X && p.X <= Max.X && p.Y >= Min.Y && p.Y <= Max.Y && p.Z >= Min.Z && p.Z <= Max.Z;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public PhysicsVector3 ClosestPoint(PhysicsVector3 p)
            => PhysicsVector3.Max(Min, PhysicsVector3.Min(Max, p));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float SurfaceArea()
        {
            PhysicsVector3 e = Max - Min;
            return 2.0f * (e.X * e.Y + e.Y * e.Z + e.X * e.Z);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float Volume()
        {
            PhysicsVector3 e = Max - Min;
            return e.X * e.Y * e.Z;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Expand(PhysicsVector3 v) { Min = Min - v; Max = Max + v; }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static AABBShape Union(AABBShape a, AABBShape b)
            => new AABBShape(PhysicsVector3.Min(a.Min, b.Min), PhysicsVector3.Max(a.Max, b.Max));
    }

    /// <summary>有向包围盒（OBB）。</summary>
    public struct OBBShape
    {
        public PhysicsVector3 Center;
        public PhysicsVector3 HalfExtents;
        public PhysicsQuaternion Rotation;

        public OBBShape(PhysicsVector3 center, PhysicsVector3 halfExtents, PhysicsQuaternion rotation)
        {
            Center = center; HalfExtents = halfExtents; Rotation = rotation;
        }

        /// <summary>获取3个局部轴。</summary>
        public void GetAxes(out PhysicsVector3 axisX, out PhysicsVector3 axisY, out PhysicsVector3 axisZ)
        {
            PhysicsMatrix3x3 rot = Rotation.ToMatrix3x3();
            rot.ExtractBasis(out axisX, out axisY, out axisZ);
        }

        /// <summary>OBB → AABB（近似）。</summary>
        public AABBShape ToAABB()
        {
            PhysicsMatrix3x3 rot = Rotation.ToMatrix3x3();
            PhysicsVector3 ax = new PhysicsVector3(Math.Abs(rot.M00) + Math.Abs(rot.M10) + Math.Abs(rot.M20),
                                                    Math.Abs(rot.M01) + Math.Abs(rot.M11) + Math.Abs(rot.M21),
                                                    Math.Abs(rot.M02) + Math.Abs(rot.M12) + Math.Abs(rot.M22));
            PhysicsVector3 e = HalfExtents * ax;
            return new AABBShape(Center - e, Center + e);
        }

        /// <summary>获取所有8个顶点。</summary>
        public PhysicsVector3[] GetVertices()
        {
            PhysicsMatrix3x3 rot = Rotation.ToMatrix3x3();
            rot.ExtractBasis(out PhysicsVector3 ax, out PhysicsVector3 ay, out PhysicsVector3 az);
            ax = ax * HalfExtents.X; ay = ay * HalfExtents.Y; az = az * HalfExtents.Z;
            return new PhysicsVector3[]
            {
                Center + ax + ay + az, Center - ax + ay + az,
                Center + ax - ay + az, Center - ax - ay + az,
                Center + ax + ay - az, Center - ax + ay - az,
                Center + ax - ay - az, Center - ax - ay - az,
            };
        }

        /// <summary>最近点到OBB表面的距离。</summary>
        public PhysicsVector3 ClosestPoint(PhysicsVector3 p)
        {
            PhysicsVector3 local = (Rotation.Inverse() * new PhysicsVector4(p.X, p.Y, p.Z, 1)).ToPhysicsVector3();
            local = PhysicsVector3.Max(-HalfExtents, PhysicsVector3.Min(HalfExtents, local));
            return (Rotation * new PhysicsVector4(local.X, local.Y, local.Z, 1)).ToPhysicsVector3() + Center;
        }
    }

    /// <summary>球体形状。</summary>
    public struct SphereShapeData
    {
        public PhysicsVector3 Center;
        public float Radius;

        public SphereShapeData(PhysicsVector3 center, float radius)
        {
            Center = center; Radius = radius;
        }
    }

    /// <summary>胶囊体形状。</summary>
    public struct CapsuleShapeData
    {
        public PhysicsVector3 PointA;
        public PhysicsVector3 PointB;
        public float Radius;

        public CapsuleShapeData(PhysicsVector3 pointA, PhysicsVector3 pointB, float radius)
        {
            PointA = pointA; PointB = pointB; Radius = radius;
        }
    }

    /// <summary>SAT（分离轴定理）碰撞检测。</summary>
    public static class SAT
    {
        /// <summary>OBB vs OBB 碰撞检测（15轴测试）。</summary>
        public static bool IntersectOBBvsOBB(OBBShape a, OBBShape b,
            out PhysicsVector3 normal, out float penetration)
        {
            normal = PhysicsVector3.Zero;
            penetration = 0;

            a.GetAxes(out PhysicsVector3 aX, out PhysicsVector3 aY, out PhysicsVector3 aZ);
            b.GetAxes(out PhysicsVector3 bX, out PhysicsVector3 bY, out PhysicsVector3 bZ);

            PhysicsVector3 t = b.Center - a.Center;
            PhysicsVector3[] axes = new PhysicsVector3[15];
            axes[0] = aX; axes[1] = aY; axes[2] = aZ;
            axes[3] = bX; axes[4] = bY; axes[5] = bZ;
            axes[6] = aX.Cross(bX); axes[7] = aX.Cross(bY); axes[8] = aX.Cross(bZ);
            axes[9] = aY.Cross(bX); axes[10] = aY.Cross(bY); axes[11] = aY.Cross(bZ);
            axes[12] = aZ.Cross(bX); axes[13] = aZ.Cross(bY); axes[14] = aZ.Cross(bZ);

            float minOverlap = float.MaxValue;
            int minAxis = -1;

            for (int i = 0; i < 15; i++)
            {
                PhysicsVector3 axis = axes[i];
                float len = axis.Length();
                if (len < 1e-6f) continue;
                axis = axis / len;

                float ra = ProjectOBBHalf(a, axis, aX, aY, aZ);
                float rb = ProjectOBBHalf(b, axis, bX, bY, bZ);
                float d = Math.Abs(t.Dot(axis));
                float overlap = ra + rb - d;

                if (overlap <= 0) return false;
                if (overlap < minOverlap)
                {
                    minOverlap = overlap;
                    minAxis = i;
                }
            }

            if (minAxis < 0) return false;

            normal = axes[minAxis];
            float lenN = normal.Length();
            if (lenN > 1e-6f) normal = normal / lenN;
            if (t.Dot(normal) < 0) normal = -normal;
            penetration = minOverlap;
            return true;
        }

        private static float ProjectOBBHalf(OBBShape obb, PhysicsVector3 axis,
            PhysicsVector3 aX, PhysicsVector3 aY, PhysicsVector3 aZ)
        {
            return Math.Abs(obb.HalfExtents.X * aX.Dot(axis))
                 + Math.Abs(obb.HalfExtents.Y * aY.Dot(axis))
                 + Math.Abs(obb.HalfExtents.Z * aZ.Dot(axis));
        }

        /// <summary>AABB vs AABB。</summary>
        public static bool IntersectAABBvsAABB(AABBShape a, AABBShape b,
            out PhysicsVector3 normal, out float penetration)
        {
            PhysicsVector3 d1 = b.Max - a.Min;
            PhysicsVector3 d2 = a.Max - b.Min;
            PhysicsVector3 overlap = PhysicsVector3.Min(d1, d2);

            float minOverlap = Math.Min(overlap.X, Math.Min(overlap.Y, overlap.Z));
            if (minOverlap <= 0) { normal = PhysicsVector3.Zero; penetration = 0; return false; }

            normal = PhysicsVector3.Zero;
            penetration = minOverlap;
            if (minOverlap == overlap.X) normal = PhysicsVector3.UnitX;
            else if (minOverlap == overlap.Y) normal = PhysicsVector3.UnitY;
            else normal = PhysicsVector3.UnitZ;

            PhysicsVector3 dir = b.Center - a.Center;
            if (dir.Dot(normal) < 0) normal = -normal;
            return true;
        }

        /// <summary>OBB vs AABB。</summary>
        public static bool IntersectOBBvsAABB(OBBShape obb, AABBShape aabb,
            out PhysicsVector3 normal, out float penetration)
        {
            OBBShape obbB = new OBBShape(
                aabb.Center, aabb.Extents, PhysicsQuaternion.Identity);
            return IntersectOBBvsOBB(obb, obbB, out normal, out penetration);
        }

        /// <summary>Sphere vs OBB。</summary>
        public static bool IntersectSpherevsOBB(SphereShapeData sphere, OBBShape obb,
            out PhysicsVector3 normal, out float penetration)
        {
            normal = PhysicsVector3.Zero;
            penetration = 0;

            PhysicsVector3 closest = obb.ClosestPoint(sphere.Center);
            PhysicsVector3 diff = sphere.Center - closest;
            float distSq = diff.LengthSquared();

            if (distSq > sphere.Radius * sphere.Radius) return false;

            float dist = (float)Math.Sqrt(distSq);
            if (dist < 1e-6f)
            {
                PhysicsVector3 local = sphere.Center - obb.Center;
                obb.GetAxes(out PhysicsVector3 ax, out PhysicsVector3 ay, out PhysicsVector3 az);
                float dx = Math.Abs(local.Dot(ax)) - obb.HalfExtents.X;
                float dy = Math.Abs(local.Dot(ay)) - obb.HalfExtents.Y;
                float dz = Math.Abs(local.Dot(az)) - obb.HalfExtents.Z;

                if (dx > dy && dx > dz) normal = (local.Dot(ax) > 0) ? ax : -ax;
                else if (dy > dz) normal = (local.Dot(ay) > 0) ? ay : -ay;
                else normal = (local.Dot(az) > 0) ? az : -az;

                penetration = sphere.Radius;
                return true;
            }

            normal = diff / dist;
            penetration = sphere.Radius - dist;
            return true;
        }

        /// <summary>Sphere vs Sphere。</summary>
        public static bool IntersectSpherevsSphere(SphereShapeData a, SphereShapeData b,
            out PhysicsVector3 normal, out float penetration)
        {
            PhysicsVector3 diff = b.Center - a.Center;
            float distSq = diff.LengthSquared();
            float radiusSum = a.Radius + b.Radius;

            if (distSq > radiusSum * radiusSum)
            {
                normal = PhysicsVector3.Zero; penetration = 0; return false;
            }

            float dist = (float)Math.Sqrt(distSq);
            if (dist < 1e-6f)
            {
                normal = PhysicsVector3.UnitY;
                penetration = radiusSum;
            }
            else
            {
                normal = diff / dist;
                penetration = radiusSum - dist;
            }
            return true;
        }

        /// <summary>Sphere vs AABB。</summary>
        public static bool IntersectSpherevsAABB(SphereShapeData sphere, AABBShape aabb,
            out PhysicsVector3 normal, out float penetration)
        {
            normal = PhysicsVector3.Zero;
            penetration = 0;

            PhysicsVector3 closest = aabb.ClosestPoint(sphere.Center);
            PhysicsVector3 diff = sphere.Center - closest;
            float distSq = diff.LengthSquared();

            if (distSq > sphere.Radius * sphere.Radius) return false;

            float dist = (float)Math.Sqrt(distSq);
            if (dist < 1e-6f)
            {
                PhysicsVector3 local = sphere.Center - aabb.Center;
                PhysicsVector3 absLocal = PhysicsVector3.Abs(local);
                PhysicsVector3 extents = aabb.Extents;
                PhysicsVector3 d = absLocal - extents;
                if (d.X > d.Y && d.X > d.Z) normal = PhysicsVector3.UnitX;
                else if (d.Y > d.Z) normal = PhysicsVector3.UnitY;
                else normal = PhysicsVector3.UnitZ;
                if (local.Dot(normal) < 0) normal = -normal;
                penetration = sphere.Radius;
                return true;
            }

            normal = diff / dist;
            penetration = sphere.Radius - dist;
            return true;
        }

        /// <summary>Capsule vs Capsule（线段最近点 + 半径）。</summary>
        public static bool IntersectCapsulevsCapsule(CapsuleShapeData a, CapsuleShapeData b,
            out PhysicsVector3 normal, out float penetration)
        {
            normal = PhysicsVector3.Zero;
            penetration = 0;

            ClosestPointsOnSegments(a.PointA, a.PointB, b.PointA, b.PointB,
                out PhysicsVector3 closestA, out PhysicsVector3 closestB);

            PhysicsVector3 diff = closestB - closestA;
            float distSq = diff.LengthSquared();
            float radiusSum = a.Radius + b.Radius;

            if (distSq > radiusSum * radiusSum) return false;

            float dist = (float)Math.Sqrt(distSq);
            if (dist < 1e-6f)
            {
                PhysicsVector3 segA = (a.PointB - a.PointA).Normalized();
                PhysicsVector3 segB = (b.PointB - b.PointA).Normalized();
                PhysicsVector3 cross = segA.Cross(segB);
                float crossLen = cross.Length();
                normal = crossLen > 1e-6f ? cross / crossLen : segA.Perpendicular();
                penetration = radiusSum;
            }
            else
            {
                normal = diff / dist;
                penetration = radiusSum - dist;
            }
            return true;
        }

        /// <summary>两线段最近点。</summary>
        internal static void ClosestPointsOnSegments(
            PhysicsVector3 p1, PhysicsVector3 q1, PhysicsVector3 p2, PhysicsVector3 q2,
            out PhysicsVector3 closest1, out PhysicsVector3 closest2)
        {
            PhysicsVector3 d1 = q1 - p1;
            PhysicsVector3 d2 = q2 - p2;
            PhysicsVector3 r = p1 - p2;
            float a = d1.Dot(d1), e = d2.Dot(d2), f = d2.Dot(r);
            float c = d1.Dot(r), b = d1.Dot(d2);
            float denom = a * e - b * b;

            float s, t;
            if (denom < 1e-6f)
            {
                s = 0;
                t = (b * c > e * c) ? Math.Clamp((e * c - b * f) / (e * e - b * b), 0, 1) : 0;
            }
            else
            {
                s = Math.Clamp((b * f - c * e) / denom, 0, 1);
                t = (b * s + f) / e;
                if (t < 0) { s = Math.Clamp(-c / a, 0, 1); t = 0; }
                else if (t > 1) { s = Math.Clamp((b - c) / a, 0, 1); t = 1; }
            }

            closest1 = p1 + s * d1;
            closest2 = p2 + t * d2;
        }
    }

    #endregion

    #region ======================== 射线检测 ========================

    /// <summary>射线检测结果。</summary>
    public struct RaycastHitData
    {
        public PhysicsVector3 Point;
        public PhysicsVector3 Normal;
        public float Distance;
        public float T;
        public object HitObject;
    }

    /// <summary>射线检测工具。</summary>
    public static class Raycast
    {
        /// <summary>射线 vs 球体（二次方程求解）。</summary>
        public static bool RaycastSphere(PhysicsVector3 origin, PhysicsVector3 direction,
            SphereShapeData sphere, out float t)
        {
            t = float.MaxValue;
            PhysicsVector3 oc = origin - sphere.Center;
            float a = direction.Dot(direction);
            float b = 2.0f * oc.Dot(direction);
            float c = oc.Dot(oc) - sphere.Radius * sphere.Radius;
            float discriminant = b * b - 4 * a * c;
            if (discriminant < 0) return false;
            float sqrtD = (float)Math.Sqrt(discriminant);
            float t0 = (-b - sqrtD) / (2.0f * a);
            float t1 = (-b + sqrtD) / (2.0f * a);
            t = t0 >= 0 ? t0 : (t1 >= 0 ? t1 : float.MaxValue);
            return t >= 0;
        }

        /// <summary>射线 vs AABB（Slab方法）。</summary>
        public static bool RaycastAABB(PhysicsVector3 origin, PhysicsVector3 direction,
            AABBShape aabb, out float tMin, out float tMax)
        {
            tMin = 0; tMax = float.MaxValue;

            for (int i = 0; i < 3; i++)
            {
                float o = i == 0 ? origin.X : (i == 1 ? origin.Y : origin.Z);
                float d = i == 0 ? direction.X : (i == 1 ? direction.Y : direction.Z);
                float min = i == 0 ? aabb.Min.X : (i == 1 ? aabb.Min.Y : aabb.Min.Z);
                float max = i == 0 ? aabb.Max.X : (i == 1 ? aabb.Max.Y : aabb.Max.Z);

                if (Math.Abs(d) < 1e-8f)
                {
                    if (o < min || o > max) return false;
                }
                else
                {
                    float invD = 1.0f / d;
                    float t1 = (min - o) * invD;
                    float t2 = (max - o) * invD;
                    if (t1 > t2) { float tmp = t1; t1 = t2; t2 = tmp; }
                    tMin = Math.Max(tMin, t1);
                    tMax = Math.Min(tMax, t2);
                    if (tMin > tMax) return false;
                }
            }
            return true;
        }

        /// <summary>射线 vs OBB（变换到OBB局部空间 + Slab）。</summary>
        public static bool RaycastOBB(PhysicsVector3 origin, PhysicsVector3 direction,
            OBBShape obb, out float t)
        {
            t = float.MaxValue;

            PhysicsTransform invTransform = new PhysicsTransform(
                -obb.Center, obb.Rotation.Inverse(),
                new PhysicsVector3(
                    obb.HalfExtents.X > 1e-6f ? 1f / obb.HalfExtents.X : 0,
                    obb.HalfExtents.Y > 1e-6f ? 1f / obb.HalfExtents.Y : 0,
                    obb.HalfExtents.Z > 1e-6f ? 1f / obb.HalfExtents.Z : 0));

            PhysicsVector3 localOrigin = origin - obb.Center;
            PhysicsVector3 localDir = obb.Rotation.Inverse().ToMatrix3x3().Multiply(direction);

            AABBShape localAABB = new AABBShape(
                -obb.HalfExtents, obb.HalfExtents);

            if (RaycastAABB(localOrigin, localDir, localAABB, out float tMin, out float tMax))
            {
                t = tMin >= 0 ? tMin : (tMax >= 0 ? tMax : float.MaxValue);
                return t >= 0;
            }
            return false;
        }

        /// <summary>射线 vs 三角形（Moller-Trumbore算法）。</summary>
        public static bool RaycastTriangle(PhysicsVector3 origin, PhysicsVector3 direction,
            PhysicsVector3 v0, PhysicsVector3 v1, PhysicsVector3 v2,
            out float t, out float u, out float v)
        {
            t = float.MaxValue; u = 0; v = 0;

            PhysicsVector3 edge1 = v1 - v0;
            PhysicsVector3 edge2 = v2 - v0;
            PhysicsVector3 h = direction.Cross(edge2);
            float a = edge1.Dot(h);

            if (Math.Abs(a) < 1e-8f) return false;
            float f = 1.0f / a;
            PhysicsVector3 s = origin - v0;
            u = f * s.Dot(h);
            if (u < 0.0f || u > 1.0f) return false;
            PhysicsVector3 q = s.Cross(edge1);
            v = f * direction.Dot(q);
            if (v < 0.0f || u + v > 1.0f) return false;
            t = f * edge2.Dot(q);
            return t >= 0;
        }

        /// <summary>射线 vs 凸多面体（遍历面法线SAT）。</summary>
        public static bool RaycastConvexMesh(PhysicsVector3 origin, PhysicsVector3 direction,
            PhysicsVector3[] vertices, int[][] faces,
            out float t, out PhysicsVector3 hitNormal)
        {
            t = float.MaxValue;
            hitNormal = PhysicsVector3.Zero;

            if (vertices == null || faces == null || faces.Length == 0) return false;

            float tMin = 0, tMax = float.MaxValue;
            PhysicsVector3 normalMin = PhysicsVector3.Zero;

            foreach (var face in faces)
            {
                if (face.Length < 3) continue;
                PhysicsVector3 v0 = vertices[face[0]];
                PhysicsVector3 v1 = vertices[face[1]];
                PhysicsVector3 v2 = vertices[face[2]];
                PhysicsVector3 edge1 = v1 - v0, edge2 = v2 - v0;
                PhysicsVector3 faceNormal = edge1.Cross(edge2).Normalized();
                float d = faceNormal.Dot(v0);
                float denom = faceNormal.Dot(direction);

                if (Math.Abs(denom) < 1e-8f)
                {
                    if (faceNormal.Dot(origin) - d > 0) return false;
                    continue;
                }

                float tHit = (d - faceNormal.Dot(origin)) / denom;
                if (denom < 0)
                {
                    if (tHit > tMin) { tMin = tHit; normalMin = faceNormal; }
                }
                else
                {
                    tMax = Math.Min(tMax, tHit);
                }

                if (tMin > tMax) return false;
            }

            if (tMin <= tMax && tMin >= 0)
            {
                t = tMin;
                hitNormal = normalMin;
                return true;
            }
            return false;
        }

        /// <summary>射线 vs 胶囊体（线段最近点 + 球体检测）。</summary>
        public static bool RaycastCapsule(PhysicsVector3 origin, PhysicsVector3 direction,
            CapsuleShapeData capsule, out float t)
        {
            t = float.MaxValue;

            SAT.ClosestPointsOnSegments(
                origin, origin + direction * 10000f,
                capsule.PointA, capsule.PointB,
                out PhysicsVector3 closestRay, out PhysicsVector3 closestSeg);

            PhysicsVector3 diff = closestRay - closestSeg;
            float distSq = diff.LengthSquared();

            if (distSq > capsule.Radius * capsule.Radius) return false;

            float dist = (float)Math.Sqrt(distSq);
            PhysicsVector3 rayDir = direction;
            float rayLen = rayDir.Length();
            if (rayLen > 1e-6f) rayDir = rayDir / rayLen;

            if (dist < 1e-6f)
            {
                PhysicsVector3 seg = capsule.PointB - capsule.PointA;
                PhysicsVector3 cross = rayDir.Cross(seg);
                float crossLen = cross.Length();
                if (crossLen < 1e-6f) return false;
                PhysicsVector3 perp = cross / crossLen;
                PhysicsVector3 testPoint = closestRay + perp * capsule.Radius;
                t = (testPoint - origin).Dot(rayDir);
                return t >= 0;
            }

            PhysicsVector3 dirToRay = diff / dist;
            PhysicsVector3 contactOnSeg = closestSeg + dirToRay * capsule.Radius;
            t = (contactOnSeg - origin).Dot(rayDir);
            return t >= 0;
        }

        /// <summary>射线 vs 高度图（双线性插值列遍历）。</summary>
        public static bool RaycastHeightField(PhysicsVector3 origin, PhysicsVector3 direction,
            float[] heightData, int width, int height,
            PhysicsVector3 scale, PhysicsVector3 offset, out float t, out PhysicsVector3 hitNormal)
        {
            t = float.MaxValue;
            hitNormal = PhysicsVector3.UnitY;

            if (heightData == null || width < 2 || height < 2) return false;

            PhysicsVector3 cellSize = new PhysicsVector3(scale.X / width, scale.Y, scale.Z / height);

            float bestT = float.MaxValue;
            for (int row = 0; row < height - 1; row++)
            {
                for (int col = 0; col < width - 1; col++)
                {
                    int idx00 = row * width + col;
                    int idx10 = row * width + col + 1;
                    int idx01 = (row + 1) * width + col;
                    int idx11 = (row + 1) * width + col + 1;

                    if (idx00 >= heightData.Length || idx11 >= heightData.Length) continue;

                    float h00 = heightData[idx00], h10 = heightData[idx10];
                    float h01 = heightData[idx01], h11 = heightData[idx11];

                    PhysicsVector3 v00 = new PhysicsVector3(col * cellSize.X + offset.X, h00 * scale.Y + offset.Y, row * cellSize.Z + offset.Z);
                    PhysicsVector3 v10 = new PhysicsVector3((col + 1) * cellSize.X + offset.X, h10 * scale.Y + offset.Y, row * cellSize.Z + offset.Z);
                    PhysicsVector3 v01 = new PhysicsVector3(col * cellSize.X + offset.X, h01 * scale.Y + offset.Y, (row + 1) * cellSize.Z + offset.Z);
                    PhysicsVector3 v11 = new PhysicsVector3((col + 1) * cellSize.X + offset.X, h11 * scale.Y + offset.Y, (row + 1) * cellSize.Z + offset.Z);

                    if (RaycastTriangle(origin, direction, v00, v10, v01, out float t0, out float u0, out float v0))
                    {
                        if (t0 < bestT)
                        {
                            bestT = t0;
                            PhysicsVector3 e1 = v10 - v00, e2 = v01 - v00;
                            hitNormal = e1.Cross(e2).Normalized();
                            if (hitNormal.Y < 0) hitNormal = -hitNormal;
                        }
                    }
                    if (RaycastTriangle(origin, direction, v10, v11, v01, out float t1, out _, out _))
                    {
                        if (t1 < bestT)
                        {
                            bestT = t1;
                            PhysicsVector3 e1 = v11 - v10, e2 = v01 - v10;
                            hitNormal = e1.Cross(e2).Normalized();
                            if (hitNormal.Y < 0) hitNormal = -hitNormal;
                        }
                    }
                }
            }

            if (bestT < float.MaxValue) { t = bestT; return true; }
            return false;
        }
    }

    #endregion

    #region ======================== 碰撞形状体系 ========================

    /// <summary>形状类型枚举。</summary>
    public enum RealShapeType
    {
        Sphere,
        Box,
        AABB,
        OBB,
        Capsule,
        ConvexMesh,
        Triangle,
        Plane,
        HeightField,
        Composite,
    }

    /// <summary>碰撞形状基类。</summary>
    public abstract class Shape
    {
        public abstract RealShapeType Type { get; }
        public abstract AABBShape ComputeAABB(PhysicsTransform transform);
        public abstract PhysicsVector3 GetSupportPoint(PhysicsVector3 direction, PhysicsTransform transform);

        /// <summary>计算体积。</summary>
        public abstract float ComputeVolume();
    }

    /// <summary>球体形状。</summary>
    public class RealSphereShape : Shape
    {
        public float Radius;

        public RealSphereShape(float radius) { Radius = Math.Max(radius, 0.001f); }

        public override RealShapeType Type => RealShapeType.Sphere;

        public override AABBShape ComputeAABB(PhysicsTransform transform)
        {
            PhysicsVector3 center = transform.Position;
            PhysicsVector3 scaledRadius = new PhysicsVector3(Radius * transform.Scale.X,
                Radius * transform.Scale.Y, Radius * transform.Scale.Z);
            return new AABBShape(center - scaledRadius, center + scaledRadius);
        }

        public override PhysicsVector3 GetSupportPoint(PhysicsVector3 direction, PhysicsTransform transform)
        {
            PhysicsVector3 n = direction.Normalized();
            PhysicsVector3 localPoint = n * Radius;
            return transform.TransformPoint(localPoint);
        }

        public override float ComputeVolume() => (4.0f / 3.0f) * MathF.PI * Radius * Radius * Radius;
    }

    /// <summary>长方体形状（中心在原点的AABB）。</summary>
    public class RealBoxShape : Shape
    {
        public PhysicsVector3 HalfExtents;

        public RealBoxShape(PhysicsVector3 halfExtents) { HalfExtents = halfExtents; }

        public override RealShapeType Type => RealShapeType.Box;

        public override AABBShape ComputeAABB(PhysicsTransform transform)
        {
            PhysicsMatrix3x3 rot = transform.Rotation.ToMatrix3x3();
            PhysicsVector3 ax = new PhysicsVector3(Math.Abs(rot.M00), Math.Abs(rot.M01), Math.Abs(rot.M02));
            PhysicsVector3 ay = new PhysicsVector3(Math.Abs(rot.M10), Math.Abs(rot.M11), Math.Abs(rot.M12));
            PhysicsVector3 az = new PhysicsVector3(Math.Abs(rot.M20), Math.Abs(rot.M21), Math.Abs(rot.M22));
            PhysicsVector3 e = new PhysicsVector3(
                ax.Dot(HalfExtents) * transform.Scale.X,
                ay.Dot(HalfExtents) * transform.Scale.Y,
                az.Dot(HalfExtents) * transform.Scale.Z);
            return new AABBShape(transform.Position - e, transform.Position + e);
        }

        public override PhysicsVector3 GetSupportPoint(PhysicsVector3 direction, PhysicsTransform transform)
        {
            PhysicsVector3 localDir = transform.Rotation.Inverse().ToMatrix3x3().Multiply(direction);
            PhysicsVector3 localPoint = new PhysicsVector3(
                localDir.X >= 0 ? HalfExtents.X : -HalfExtents.X,
                localDir.Y >= 0 ? HalfExtents.Y : -HalfExtents.Y,
                localDir.Z >= 0 ? HalfExtents.Z : -HalfExtents.Z);
            return transform.TransformPoint(localPoint);
        }

        public override float ComputeVolume()
            => 8.0f * HalfExtents.X * HalfExtents.Y * HalfExtents.Z;
    }

    /// <summary>凸多面体形状。</summary>
    public class ConvexMeshShape : Shape
    {
        public PhysicsVector3[] Vertices;
        public int[][] Faces;
        public PhysicsVector3[] Normals;
        public AABBShape LocalAABB;

        public ConvexMeshShape(PhysicsVector3[] vertices, int[][] faces)
        {
            Vertices = vertices ?? Array.Empty<PhysicsVector3>();
            Faces = faces ?? Array.Empty<int[]>();
            ComputeNormals();
            ComputeLocalAABB();
        }

        private void ComputeNormals()
        {
            Normals = new PhysicsVector3[Faces.Length];
            for (int i = 0; i < Faces.Length; i++)
            {
                if (Faces[i].Length < 3) { Normals[i] = PhysicsVector3.UnitY; continue; }
                PhysicsVector3 v0 = Vertices[Faces[i][0]], v1 = Vertices[Faces[i][1]], v2 = Vertices[Faces[i][2]];
                PhysicsVector3 edge1 = v1 - v0, edge2 = v2 - v0;
                Normals[i] = edge1.Cross(edge2).Normalized();
            }
        }

        private void ComputeLocalAABB()
        {
            if (Vertices.Length == 0) { LocalAABB = new AABBShape(PhysicsVector3.Zero, PhysicsVector3.Zero); return; }
            PhysicsVector3 min = Vertices[0], max = Vertices[0];
            for (int i = 1; i < Vertices.Length; i++)
            {
                min = PhysicsVector3.Min(min, Vertices[i]);
                max = PhysicsVector3.Max(max, Vertices[i]);
            }
            LocalAABB = new AABBShape(min, max);
        }

        public override RealShapeType Type => RealShapeType.ConvexMesh;

        public override AABBShape ComputeAABB(PhysicsTransform transform)
        {
            if (Vertices.Length == 0) return new AABBShape(PhysicsVector3.Zero, PhysicsVector3.Zero);
            PhysicsVector3 min = transform.TransformPoint(Vertices[0]);
            PhysicsVector3 max = min;
            for (int i = 1; i < Vertices.Length; i++)
            {
                PhysicsVector3 tp = transform.TransformPoint(Vertices[i]);
                min = PhysicsVector3.Min(min, tp);
                max = PhysicsVector3.Max(max, tp);
            }
            return new AABBShape(min, max);
        }

        public override PhysicsVector3 GetSupportPoint(PhysicsVector3 direction, PhysicsTransform transform)
        {
            if (Vertices.Length == 0) return transform.Position;
            PhysicsVector3 localDir = transform.Rotation.Inverse().ToMatrix3x3().Multiply(direction);
            float maxDot = float.MinValue;
            PhysicsVector3 bestVertex = Vertices[0];
            for (int i = 0; i < Vertices.Length; i++)
            {
                float d = Vertices[i].Dot(localDir);
                if (d > maxDot) { maxDot = d; bestVertex = Vertices[i]; }
            }
            return transform.TransformPoint(bestVertex);
        }

        public override float ComputeVolume()
        {
            float volume = 0;
            for (int i = 0; i < Faces.Length; i++)
            {
                if (Faces[i].Length < 3) continue;
                PhysicsVector3 v0 = Vertices[Faces[i][0]], v1 = Vertices[Faces[i][1]], v2 = Vertices[Faces[i][2]];
                PhysicsVector3 cross = v1.Cross(v2);
                volume += v0.Dot(cross) / 6.0f;
            }
            return Math.Abs(volume);
        }
    }

    /// <summary>高度图形状。</summary>
    public class HeightFieldShape : Shape
    {
        public float[] Heights;
        public int Width;
        public int HeightCount;
        public PhysicsVector3 Scale;
        public PhysicsVector3 Offset;

        public HeightFieldShape(float[] heights, int width, int heightCount,
            PhysicsVector3 scale, PhysicsVector3 offset)
        {
            Heights = heights ?? Array.Empty<float>();
            Width = width; HeightCount = heightCount;
            Scale = scale; Offset = offset;
        }

        public override RealShapeType Type => RealShapeType.HeightField;

        public override AABBShape ComputeAABB(PhysicsTransform transform)
        {
            float minH = float.MaxValue, maxH = float.MinValue;
            for (int i = 0; i < Heights.Length; i++)
            {
                minH = Math.Min(minH, Heights[i]);
                maxH = Math.Max(maxH, Heights[i]);
            }
            PhysicsVector3 min = new PhysicsVector3(Offset.X, Offset.Y + minH * Scale.Y, Offset.Z);
            PhysicsVector3 max = new PhysicsVector3(
                Offset.X + Width * Scale.X, Offset.Y + maxH * Scale.Y, Offset.Z + HeightCount * Scale.Z);
            return new AABBShape(min, max);
        }

        public override PhysicsVector3 GetSupportPoint(PhysicsVector3 direction, PhysicsTransform transform)
        {
            float maxHeight = float.MinValue;
            int bestIdx = 0;
            for (int i = 0; i < Heights.Length; i++)
            {
                if (Heights[i] > maxHeight) { maxHeight = Heights[i]; bestIdx = i; }
            }
            int col = bestIdx % Width, row = bestIdx / Width;
            return new PhysicsVector3(col * Scale.X + Offset.X, maxHeight * Scale.Y + Offset.Y, row * Scale.Z + Offset.Z);
        }

        public override float ComputeVolume()
        {
            float volume = 0;
            for (int i = 0; i < Heights.Length; i++)
            {
                volume += Heights[i] * Scale.Y * Scale.X * Scale.Z;
            }
            return Math.Abs(volume);
        }

        /// <summary>采样高度（双线性插值）。</summary>
        public float SampleHeight(float x, float z)
        {
            float fx = x / Scale.X, fz = z / Scale.Z;
            int col = Math.Clamp((int)Math.Floor(fx), 0, Width - 2);
            int row = Math.Clamp((int)Math.Floor(fz), 0, HeightCount - 2);
            float u = fx - col, v = fz - row;
            int idx00 = row * Width + col;
            int idx10 = row * Width + col + 1;
            int idx01 = (row + 1) * Width + col;
            int idx11 = (row + 1) * Width + col + 1;
            if (idx11 >= Heights.Length) return idx00 < Heights.Length ? Heights[idx00] * Scale.Y : 0;
            float h00 = Heights[idx00], h10 = Heights[idx10], h01 = Heights[idx01], h11 = Heights[idx11];
            float h = (h00 * (1 - u) + h10 * u) * (1 - v) + (h01 * (1 - u) + h11 * u) * v;
            return h * Scale.Y + Offset.Y;
        }

        /// <summary>计算指定位置的法线（中心差分）。</summary>
        public PhysicsVector3 ComputeNormal(float x, float z)
        {
            float eps = Scale.X * 0.5f;
            float hL = SampleHeight(x - eps, z);
            float hR = SampleHeight(x + eps, z);
            float hD = SampleHeight(x, z - eps);
            float hU = SampleHeight(x, z + eps);
            return new PhysicsVector3(hL - hR, 2 * eps, hD - hU).Normalized();
        }
    }

    /// <summary>胶囊体形状。</summary>
    public class RealCapsuleShape : Shape
    {
        public float Radius;
        public float HalfHeight;

        public RealCapsuleShape(float radius, float halfHeight)
        {
            Radius = Math.Max(radius, 0.001f);
            HalfHeight = Math.Max(halfHeight, 0.001f);
        }

        public override RealShapeType Type => RealShapeType.Capsule;

        public override AABBShape ComputeAABB(PhysicsTransform transform)
        {
            PhysicsVector3 top = transform.TransformPoint(new PhysicsVector3(0, HalfHeight, 0));
            PhysicsVector3 bottom = transform.TransformPoint(new PhysicsVector3(0, -HalfHeight, 0));
            PhysicsVector3 min = PhysicsVector3.Min(top, bottom) - new PhysicsVector3(Radius);
            PhysicsVector3 max = PhysicsVector3.Max(top, bottom) + new PhysicsVector3(Radius);
            return new AABBShape(min, max);
        }

        public override PhysicsVector3 GetSupportPoint(PhysicsVector3 direction, PhysicsTransform transform)
        {
            PhysicsVector3 localDir = transform.Rotation.Inverse().ToMatrix3x3().Multiply(direction).Normalized();
            PhysicsVector3 localPoint;
            if (localDir.Y > 0) localPoint = new PhysicsVector3(0, HalfHeight, 0);
            else if (localDir.Y < 0) localPoint = new PhysicsVector3(0, -HalfHeight, 0);
            else
            {
                PhysicsVector3 horiz = new PhysicsVector3(localDir.X, 0, localDir.Z);
                float horizLen = horiz.Length();
                if (horizLen > 1e-6f) horiz = horiz / horizLen;
                else horiz = PhysicsVector3.UnitX;
                localPoint = horiz * Radius;
            }
            return transform.TransformPoint(localPoint);
        }

        public override float ComputeVolume()
        {
            float cylinderVol = MathF.PI * Radius * Radius * 2 * HalfHeight;
            float sphereVol = (4.0f / 3.0f) * MathF.PI * Radius * Radius * Radius;
            return cylinderVol + sphereVol;
        }
    }

    /// <summary>平面形状。</summary>
    public class RealPlaneShape : Shape
    {
        public PhysicsVector3 Normal;
        public float Distance;

        public RealPlaneShape(PhysicsVector3 normal, float distance)
        {
            Normal = normal.Normalized(); Distance = distance;
        }

        public override RealShapeType Type => RealShapeType.Plane;

        public override AABBShape ComputeAABB(PhysicsTransform transform)
        {
            PhysicsVector3 n = transform.TransformDirection(Normal);
            PhysicsVector3 large = new PhysicsVector3(10000, 10000, 10000);
            PhysicsVector3 center = n * (Distance + transform.Position.Dot(n));
            return new AABBShape(center - large, center + large);
        }

        public override PhysicsVector3 GetSupportPoint(PhysicsVector3 direction, PhysicsTransform transform)
        {
            PhysicsVector3 n = transform.TransformDirection(Normal);
            float d = direction.Dot(n);
            if (d > 1e-6f) return n * 10000 + transform.Position;
            return -n * 10000 + transform.Position;
        }

        public override float ComputeVolume() => 0;
    }

    /// <summary>复合形状。</summary>
    public class CompositeShape : Shape
    {
        public List<(Shape Shape, PhysicsTransform LocalTransform)> Children = new List<(Shape, PhysicsTransform)>();

        public override RealShapeType Type => RealShapeType.Composite;

        public void AddChild(Shape shape, PhysicsTransform localTransform)
            => Children.Add((shape, localTransform));

        public override AABBShape ComputeAABB(PhysicsTransform transform)
        {
            if (Children.Count == 0) return new AABBShape(PhysicsVector3.Zero, PhysicsVector3.Zero);
            AABBShape result = Children[0].Shape.ComputeAABB(
                CombineTransforms(transform, Children[0].LocalTransform));
            for (int i = 1; i < Children.Count; i++)
            {
                AABBShape childAABB = Children[i].Shape.ComputeAABB(
                    CombineTransforms(transform, Children[i].LocalTransform));
                result = AABBShape.Union(result, childAABB);
            }
            return result;
        }

        public override PhysicsVector3 GetSupportPoint(PhysicsVector3 direction, PhysicsTransform transform)
        {
            float maxDot = float.MinValue;
            PhysicsVector3 bestPoint = transform.Position;
            foreach (var child in Children)
            {
                PhysicsVector3 pt = child.Shape.GetSupportPoint(direction,
                    CombineTransforms(transform, child.LocalTransform));
                float d = pt.Dot(direction);
                if (d > maxDot) { maxDot = d; bestPoint = pt; }
            }
            return bestPoint;
        }

        public override float ComputeVolume()
        {
            float vol = 0;
            foreach (var child in Children) vol += child.Shape.ComputeVolume();
            return vol;
        }

        private static PhysicsTransform CombineTransforms(PhysicsTransform parent, PhysicsTransform child)
        {
            PhysicsMatrix4x4 parentMat = parent.ToMatrix();
            PhysicsMatrix4x4 childMat = child.ToMatrix();
            PhysicsMatrix4x4 combined = parentMat.Multiply(childMat);
            combined.ExtractTRS(out PhysicsVector3 pos, out PhysicsQuaternion rot, out PhysicsVector3 scale);
            return new PhysicsTransform(pos, rot, scale);
        }
    }

    #endregion

    #region ======================== 碰撞分发器 ========================

    /// <summary>碰撞检测分发器 —— 根据形状对选择对应碰撞算法。</summary>
    public class ShapeCollisionDispatcher
    {
        public delegate bool CollisionTest(Shape a, PhysicsTransform tA, Shape b, PhysicsTransform tB,
            out PhysicsVector3 normal, out float penetration);

        private Dictionary<(RealShapeType, RealShapeType), CollisionTest> _dispatchTable
            = new Dictionary<(RealShapeType, RealShapeType), CollisionTest>();

        public ShapeCollisionDispatcher()
        {
            RegisterDefaults();
        }

        /// <summary>注册碰撞测试。</summary>
        public void Register(RealShapeType a, RealShapeType b, CollisionTest test)
        {
            _dispatchTable[(a, b)] = test;
            _dispatchTable[(b, a)] = (sa, ta, sb, tb, out PhysicsVector3 n, out float p) =>
            {
                bool hit = test(sb, tb, sa, ta, out n, out p);
                n = -n;
                return hit;
            };
        }

        /// <summary>分发碰撞检测。</summary>
        public bool TestCollision(Shape a, PhysicsTransform tA, Shape b, PhysicsTransform tB,
            out PhysicsVector3 normal, out float penetration)
        {
            normal = PhysicsVector3.Zero;
            penetration = 0;

            if (a == null || b == null) return false;

            var key = (a.Type, b.Type);
            if (_dispatchTable.TryGetValue(key, out var test))
                return test(a, tA, b, tB, out normal, out penetration);

            // 通用GJK+EPA后备
            return GJKFallback(a, tA, b, tB, out normal, out penetration);
        }

        private void RegisterDefaults()
        {
            // Sphere vs Sphere
            Register(RealShapeType.Sphere, RealShapeType.Sphere, (sa, ta, sb, tb, out PhysicsVector3 n, out float p) =>
            {
                RealSphereShape sA = (RealSphereShape)sa, sB = (RealSphereShape)sb;
                var dataA = new SphereShapeData(ta.Position, sA.Radius);
                var dataB = new SphereShapeData(tb.Position, sB.Radius);
                return SAT.IntersectSpherevsSphere(dataA, dataB, out n, out p);
            });

            // Box vs Box (uses OBB vs OBB)
            Register(RealShapeType.Box, RealShapeType.Box, (sa, ta, sb, tb, out PhysicsVector3 n, out float p) =>
            {
                RealBoxShape bA = (RealBoxShape)sa, bB = (RealBoxShape)sb;
                var obbA = new OBBShape(ta.Position, bA.HalfExtents * ta.Scale, ta.Rotation);
                var obbB = new OBBShape(tb.Position, bB.HalfExtents * tb.Scale, tb.Rotation);
                return SAT.IntersectOBBvsOBB(obbA, obbB, out n, out p);
            });

            // Sphere vs Box
            Register(RealShapeType.Sphere, RealShapeType.Box, (sa, ta, sb, tb, out PhysicsVector3 n, out float p) =>
            {
                RealSphereShape sphere = (RealSphereShape)sa;
                RealBoxShape box = (RealBoxShape)sb;
                var sphereData = new SphereShapeData(ta.Position, sphere.Radius);
                var obb = new OBBShape(tb.Position, box.HalfExtents * tb.Scale, tb.Rotation);
                return SAT.IntersectSpherevsOBB(sphereData, obb, out n, out p);
            });

            // Capsule vs Capsule
            Register(RealShapeType.Capsule, RealShapeType.Capsule, (sa, ta, sb, tb, out PhysicsVector3 n, out float p) =>
            {
                RealCapsuleShape cA = (RealCapsuleShape)sa, cB = (RealCapsuleShape)sb;
                var capA = new CapsuleShapeData(
                    ta.TransformPoint(new PhysicsVector3(0, cA.HalfHeight, 0)),
                    ta.TransformPoint(new PhysicsVector3(0, -cA.HalfHeight, 0)), cA.Radius);
                var capB = new CapsuleShapeData(
                    tb.TransformPoint(new PhysicsVector3(0, cB.HalfHeight, 0)),
                    tb.TransformPoint(new PhysicsVector3(0, -cB.HalfHeight, 0)), cB.Radius);
                return SAT.IntersectCapsulevsCapsule(capA, capB, out n, out p);
            });
        }

        /// <summary>通用GJK+EPA后备碰撞检测。</summary>
        private bool GJKFallback(Shape a, PhysicsTransform tA, Shape b, PhysicsTransform tB,
            out PhysicsVector3 normal, out float penetration)
        {
            normal = PhysicsVector3.Zero;
            penetration = 0;

            GJKSupportFunction supportA = d => a.GetSupportPoint(d, tA);
            GJKSupportFunction supportB = d => b.GetSupportPoint(d, tB);

            GJKResult result = GJK.Intersect(supportA, supportB);

            if (result.Intersecting)
            {
                EPAContactInfo contact = EPA.Expand(result.Simplex, supportA, supportB);
                normal = contact.Normal;
                penetration = contact.Distance;
                return penetration > 0;
            }

            return false;
        }
    }

    #endregion

    #region ======================== 内部辅助 ========================

    /// <summary>扩展 System.Numerics.Vector4 用于矩阵运算的辅助方法。</summary>
    internal static class Vector4Extensions
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static PhysicsVector3 ToPhysicsVector3(this Vector4 v)
            => new PhysicsVector3(v.X, v.Y, v.Z);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector4 ToVector4(this PhysicsVector3 v, float w = 1.0f)
            => new Vector4(v.X, v.Y, v.Z, w);
    }

    #endregion
}
