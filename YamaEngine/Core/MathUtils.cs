// ============================================================================================
//  MathUtils.cs
//  yama引擎 (YamaEngine) 数学工具集
// ============================================================================================
//  提供引擎核心需要的数学基础设施：随机数生成、插值、几何工具、颜色转换、Easing 函数等。
// ============================================================================================

using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace YamaEngine.Core
{
    // ====== 引擎随机数 ======
    /// <summary>
    /// yama引擎随机数生成器。基于 XorShift128+ 算法，比 System.Random 更快更均匀。
    /// 提供线程独立的实例。
    /// </summary>
    public class YamaRandom
    {
        // XorShift128+ 状态
        private ulong _s0, _s1;

        /// <summary>当前线程的随机数实例。</summary>
        [ThreadStatic]
        private static YamaRandom? _threadInstance;
        public static YamaRandom ThreadInstance => _threadInstance ??= new YamaRandom(Guid.NewGuid().GetHashCode());

        /// <summary>全局随机数实例（非线程安全，需要外部同步）。</summary>
        public static YamaRandom Global { get; } = new YamaRandom(Environment.TickCount);

        public YamaRandom(int seed)
        {
            // XorShift128+ 要求两状态不同时为零，否则退化为全零序列（产生恒定输出）。
            // 用 splitmix64 混合种子，确保非零且分布良好。
            ulong z = (ulong)(uint)seed + 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            _s0 = z ^ (z >> 31);
            _s1 = _s0 + 0x9E3779B97F4A7C15UL;
            if (_s0 == 0 && _s1 == 0) _s1 = 0x9E3779B97F4A7C15UL;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private ulong NextULong()
        {
            ulong s1 = _s0, s0 = _s1;
            _s0 = s0;
            s1 ^= s1 << 23;
            _s1 = s1 ^ s0 ^ (s1 >> 18) ^ (s0 >> 5);
            return _s1 + s0;
        }

        public int Next() => (int)(NextULong() & 0x7FFFFFFF);

        public int Next(int max)
        {
            if (max <= 0) throw new ArgumentOutOfRangeException(nameof(max));
            return (int)(NextULong() % (ulong)max);
        }

        public int Next(int min, int max)
        {
            if (min > max) throw new ArgumentOutOfRangeException(nameof(min), "min must be <= max.");
            if (min == max) return min;
            return min + (int)(NextULong() % (ulong)(max - min));
        }

        public float NextFloat() => (NextULong() >> 11) * (1.0f / 9007199254740992.0f);

        public float NextFloat(float min, float max)
        {
            if (min > max) throw new ArgumentOutOfRangeException(nameof(min), "min must be <= max.");
            return min + NextFloat() * (max - min);
        }

        public double NextDouble() => (NextULong() >> 11) * (1.0 / 9007199254740992.0);

        public Vector2 NextVector2(float min = 0, float max = 1) => new(NextFloat(min, max), NextFloat(min, max));

        public Vector3 NextVector3(float min = 0, float max = 1) => new(NextFloat(min, max), NextFloat(min, max), NextFloat(min, max));

        /// <summary>单位球面上的随机点。</summary>
        public Vector3 OnUnitSphere()
        {
            float theta = NextFloat(0, MathF.PI * 2);
            float phi = MathF.Acos(2 * NextFloat() - 1);
            return new Vector3(
                MathF.Sin(phi) * MathF.Cos(theta),
                MathF.Cos(phi),
                MathF.Sin(phi) * MathF.Sin(theta));
        }

        /// <summary>单位圆上的随机点。</summary>
        public Vector2 OnUnitCircle()
        {
            float angle = NextFloat(0, MathF.PI * 2);
            return new Vector2(MathF.Cos(angle), MathF.Sin(angle));
        }
    }

    // ====== 数学常量 ======
    public static class MathConstants
    {
        public const float PI = MathF.PI;
        public const float PI2 = MathF.PI * 2;
        public const float PI_OVER_2 = MathF.PI / 2;
        public const float PI_OVER_4 = MathF.PI / 4;
        public const float DEG_TO_RAD = MathF.PI / 180.0f;
        public const float RAD_TO_DEG = 180.0f / MathF.PI;
        public const float EPSILON = 1e-6f;
        public const float FLOAT_EPSILON = 1.1920929e-7f;
        public const float INFINITY = float.PositiveInfinity;
    }

    // ====== 数学工具方法 ======
    public static class MathUtils
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Clamp(float value, float min, float max)
            => value < min ? min : (value > max ? max : value);

        /// <summary>弧度 → 角度系数（等效 180 / PI）。</summary>
        public const float Rad2Deg = 180.0f / MathF.PI;

        /// <summary>角度 → 弧度系数（等效 PI / 180）。</summary>
        public const float Deg2Rad = MathF.PI / 180.0f;

        /// <summary>将值限制到 [0, 1]。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Clamp01(float value) => value < 0 ? 0 : (value > 1 ? 1 : value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Clamp(int value, int min, int max)
            => value < min ? min : (value > max ? max : value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Lerp(float a, float b, float t) => a + (b - a) * t;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector2 Lerp(Vector2 a, Vector2 b, float t) => a + (b - a) * t;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector3 Lerp(Vector3 a, Vector3 b, float t) => a + (b - a) * t;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector4 Lerp(Vector4 a, Vector4 b, float t) => a + (b - a) * t;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float InverseLerp(float a, float b, float value)
            => Math.Abs(b - a) < MathConstants.EPSILON ? 0 : (value - a) / (b - a);

        /// <summary>三次插值（平滑步进）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float SmoothStep(float t) => t * t * (3 - 2 * t);

        /// <summary>五次插值（更平滑）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float SmootherStep(float t) => t * t * t * (t * (t * 6 - 15) + 10);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float SmoothStep(float edge0, float edge1, float x)
        {
            float t = Clamp((x - edge0) / (edge1 - edge0), 0, 1);
            return SmoothStep(t);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Remap(float value, float inMin, float inMax, float outMin, float outMax)
            => Lerp(outMin, outMax, InverseLerp(inMin, inMax, value));

        /// <summary>绝对值。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Abs(float v) => Math.Abs(v);

        /// <summary>符号：-1/0/1。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Sign(float v) => Math.Sign(v);

        /// <summary>平方根。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Sqrt(float v) => MathF.Sqrt(v);

        /// <summary>将角度归一化到 [0, 360)。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float NormalizeAngle(float degrees)
        {
            degrees = degrees % 360.0f;
            if (degrees < 0) degrees += 360.0f;
            return degrees;
        }

        /// <summary>将弧度归一化到 [0, 2PI)。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float NormalizeRadian(float radians)
        {
            radians = radians % MathConstants.PI2;
            if (radians < 0) radians += MathConstants.PI2;
            return radians;
        }

        /// <summary>向量角度差（度）。对零向量安全：任一输入为零向量时返回 0。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float AngleBetween(Vector3 from, Vector3 to)
        {
            float lenSq = from.LengthSquared() * to.LengthSquared();
            if (lenSq < MathConstants.EPSILON) return 0; // 任一为零向量，角度未定义，返回 0
            float dot = Clamp(Vector3.Dot(from, to) / MathF.Sqrt(lenSq), -1, 1);
            return MathF.Acos(dot) * MathConstants.RAD_TO_DEG;
        }

        /// <summary>点到线段的最近距离。对退化线段（a==b）安全：退化为点到点距离。</summary>
        public static float DistanceToSegment(Vector3 point, Vector3 a, Vector3 b)
        {
            var ab = b - a;
            float lenSq = ab.LengthSquared();
            if (lenSq < MathConstants.EPSILON)
                return Vector3.Distance(point, a); // 退化线段：点距离
            var ap = point - a;
            float t = Clamp(Vector3.Dot(ap, ab) / lenSq, 0, 1);
            var closest = a + ab * t;
            return Vector3.Distance(point, closest);
        }

        /// <summary>四元数球面插值。</summary>
        public static Quaternion Slerp(Quaternion a, Quaternion b, float t)
        {
            return Quaternion.Slerp(a, b, t);
        }

        /// <summary>2D 点到 AABB 的最近距离。</summary>
        public static float DistanceToAABB(Vector2 point, Vector2 min, Vector2 max)
        {
            float dx = Math.Max(min.X - point.X, Math.Max(0, point.X - max.X));
            float dy = Math.Max(min.Y - point.Y, Math.Max(0, point.Y - max.Y));
            return MathF.Sqrt(dx * dx + dy * dy);
        }

        /// <summary>射线与平面的交点。</summary>
        public static bool RayPlaneIntersection(Vector3 rayOrigin, Vector3 rayDir, Vector3 planePoint, Vector3 planeNormal, out float t)
        {
            float denom = Vector3.Dot(planeNormal, rayDir);
            if (Math.Abs(denom) < MathConstants.EPSILON)
            {
                t = 0;
                return false;
            }
            t = Vector3.Dot(planePoint - rayOrigin, planeNormal) / denom;
            return t >= 0;
        }

        /// <summary>判断点是否在三角形内。退化三角形（共线/零面积）返回 false。</summary>
        public static bool PointInTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            var v0 = c - a;
            var v1 = b - a;
            var v2 = p - a;

            float dot00 = Vector2.Dot(v0, v0);
            float dot01 = Vector2.Dot(v0, v1);
            float dot02 = Vector2.Dot(v0, v2);
            float dot11 = Vector2.Dot(v1, v1);
            float dot12 = Vector2.Dot(v1, v2);

            float denom = dot00 * dot11 - dot01 * dot01;
            if (Math.Abs(denom) < MathConstants.EPSILON) return false; // 退化三角形
            float inv = 1.0f / denom;
            float u = (dot11 * dot02 - dot01 * dot12) * inv;
            float v = (dot00 * dot12 - dot01 * dot02) * inv;

            return u >= 0 && v >= 0 && u + v <= 1;
        }
    }

    // ====== Easing 函数 ======
    public static class Easing
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Linear(float t) => t;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float QuadIn(float t) => t * t;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float QuadOut(float t) => t * (2 - t);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float QuadInOut(float t) => t < 0.5f ? 2 * t * t : -1 + (4 - 2 * t) * t;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float CubicIn(float t) => t * t * t;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float CubicOut(float t) { t -= 1; return t * t * t + 1; }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float CubicInOut(float t)
            => t < 0.5f ? 4 * t * t * t : (t - 1) * (2 * t - 2) * (2 * t - 2) + 1;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float ExpoIn(float t) => t <= 0 ? 0 : MathF.Pow(2, 10 * (t - 1));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float ExpoOut(float t) => t >= 1 ? 1 : 1 - MathF.Pow(2, -10 * t);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float ElasticIn(float t)
        {
            if (t == 0 || t == 1) return t;
            return -MathF.Pow(2, 10 * (t - 1)) * MathF.Sin((t - 1.1f) * 5 * MathF.PI);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float ElasticOut(float t)
        {
            if (t == 0 || t == 1) return t;
            return MathF.Pow(2, -10 * t) * MathF.Sin((t - 0.1f) * 5 * MathF.PI) + 1;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float BounceOut(float t)
        {
            if (t < 1 / 2.75f) return 7.5625f * t * t;
            if (t < 2 / 2.75f) { t -= 1.5f / 2.75f; return 7.5625f * t * t + 0.75f; }
            if (t < 2.5f / 2.75f) { t -= 2.25f / 2.75f; return 7.5625f * t * t + 0.9375f; }
            t -= 2.625f / 2.75f; return 7.5625f * t * t + 0.984375f;
        }
    }

    // ====== 半精度浮点数 ======
    /// <summary>Float32 ↔ Float16 转换工具。</summary>
    public static class HalfPrecision
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ushort FloatToHalf(float f)
        {
            // 简化版 float → half 转换，忽略 NaN 特殊处理
            var bits = BitConverter.SingleToInt32Bits(f);
            int sign = (bits >> 16) & 0x8000;
            int exp = ((bits >> 23) & 0xFF) - 127 + 15;
            int mantissa = (bits >> 13) & 0x3FF;

            if (exp <= 0)
            {
                // 次正规数 / 下溢：限制右移位数，避免未定义行为（移位数 >= 32）
                if (exp < -10) return (ushort)sign; // 过小，下溢为（带符号）零
                mantissa = (mantissa | 0x400) >> (1 - exp);
                exp = 0;
            }
            else if (exp > 30) { exp = 31; mantissa = 0; }
            else { mantissa &= 0x3FF; }

            return (ushort)(sign | (exp << 10) | mantissa);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float HalfToFloat(ushort h)
        {
            int sign = (h >> 15) & 0x1;
            int exp = (h >> 10) & 0x1F;
            int mantissa = h & 0x3FF;

            int f32Exp;
            if (exp == 0) f32Exp = mantissa == 0 ? 0 : 127 - 15;
            else if (exp == 31) { f32Exp = 255; mantissa <<= 13; return BitConverter.Int32BitsToSingle((sign << 31) | (f32Exp << 23) | mantissa); }
            else f32Exp = exp - 15 + 127;

            return BitConverter.Int32BitsToSingle((sign << 31) | (f32Exp << 23) | (mantissa << 13));
        }
    }

    // ====== 几何工具 ======
    public static class GeometryUtils
    {
        /// <summary>计算 AABB 包围盒。空数组抛出 ArgumentException（AABB 未定义）。</summary>
        public static void ComputeAABB(Vector3[] vertices, out Vector3 min, out Vector3 max)
        {
            if (vertices == null) throw new ArgumentNullException(nameof(vertices));
            if (vertices.Length == 0) throw new ArgumentException("Cannot compute AABB of an empty vertex set.", nameof(vertices));
            min = new Vector3(float.MaxValue);
            max = new Vector3(float.MinValue);
            foreach (var v in vertices)
            {
                min = Vector3.Min(min, v);
                max = Vector3.Max(max, v);
            }
        }

        /// <summary>AABB 中心。</summary>
        public static Vector3 AABBCenter(Vector3 min, Vector3 max) => (min + max) * 0.5f;

        /// <summary>AABB 尺寸。</summary>
        public static Vector3 AABBExtents(Vector3 min, Vector3 max) => (max - min) * 0.5f;

        /// <summary>包围球半径。</summary>
        public static float BoundingSphereRadius(Vector3[] vertices)
        {
            if (vertices == null) throw new ArgumentNullException(nameof(vertices));
            if (vertices.Length == 0) return 0;
            ComputeAABB(vertices, out var min, out var max);
            var center = AABBCenter(min, max);
            float maxDistSq = 0;
            foreach (var v in vertices)
                maxDistSq = Math.Max(maxDistSq, Vector3.DistanceSquared(center, v));
            return MathF.Sqrt(maxDistSq);
        }
    }

    // ====== 颜色工具 ======
    public static class ColorUtils
    {
        /// <summary>HSV → RGB。</summary>
        public static Vector4 HSVToRGB(float h, float s, float v, float a = 1)
        {
            h = h % 360; if (h < 0) h += 360;
            float c = v * s;
            float hp = h / 60;
            float x = c * (1 - Math.Abs(hp % 2 - 1));
            float r, g, b;
            if (hp < 1) { r = c; g = x; b = 0; }
            else if (hp < 2) { r = x; g = c; b = 0; }
            else if (hp < 3) { r = 0; g = c; b = x; }
            else if (hp < 4) { r = 0; g = x; b = c; }
            else if (hp < 5) { r = x; g = 0; b = c; }
            else { r = c; g = 0; b = x; }
            float m = v - c;
            return new Vector4(r + m, g + m, b + m, a);
        }

        /// <summary>RGB → HSV。</summary>
        public static Vector4 RGBToHSV(Vector4 rgb)
        {
            float r = rgb.X, g = rgb.Y, b = rgb.Z;
            float mx = Math.Max(r, Math.Max(g, b));
            float mn = Math.Min(r, Math.Min(g, b));
            float delta = mx - mn;

            float h = 0;
            if (delta > 0.0001f)
            {
                if (mx == r) h = 60 * (((g - b) / delta) % 6);
                else if (mx == g) h = 60 * ((b - r) / delta + 2);
                else h = 60 * ((r - g) / delta + 4);
                if (h < 0) h += 360;
            }
            float s = mx > 0.0001f ? delta / mx : 0;
            return new Vector4(h, s, mx, rgb.W);
        }

        /// <summary>十六进制颜色 → Vector4。</summary>
        public static Vector4 HexToRGB(uint hex)
        {
            return new Vector4(
                ((hex >> 16) & 0xFF) / 255.0f,
                ((hex >> 8) & 0xFF) / 255.0f,
                (hex & 0xFF) / 255.0f,
                1.0f);
        }
    }
}
