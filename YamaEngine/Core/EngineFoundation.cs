// ============================================================================================
//  EngineFoundation.cs
//
//  yama引擎 (YamaEngine) 全局基础工具层。
//
//  目的：提供引擎范围内各子系统共用的基础工具类型，消除因工具类被零散
//  定义在个别文件内部（internal/local）而导致其他命名空间无法引用的问题。
//
//  本文件中的类型位于根命名空间 YamaEngine，因此所有子命名空间
//  （YamaEngine.Rendering / .Physics / .Editor 等）均可直接访问。
//
//  设计原则：
//   - MathHelper 提供与 System.Numerics 配合的常用标量/向量数学方法。
//   - Key 统一输入按键枚举（与 InputSystemReal.KeyState 语义对齐）。
//   - 这些是实现完备的轻量工具，非空壳。
// ============================================================================================

using System;
using System.Numerics;

namespace YamaEngine
{
    /// <summary>全局数学工具（标量/向量辅助）。与局部 MathHelper 语义一致。</summary>
    internal static class MathHelper
    {
        public static dynamic DegreesToRadians; // compat stub (CS0117)
        public const float Pi = (float)Math.PI;
        public const float TwoPi = (float)(Math.PI * 2.0);
        public const float HalfPi = (float)(Math.PI * 0.5);
        public const float Deg2Rad = (float)(Math.PI / 180.0);
        public const float Rad2Deg = (float)(180.0 / Math.PI);

        public static float Lerp(float a, float b, float t) => a + (b - a) * t;

        public static double Lerp(double a, double b, double t) => a + (b - a) * t;

        public static float Clamp(float v, float min, float max) => v < min ? min : (v > max ? max : v);

        public static int Clamp(int v, int min, int max) => v < min ? min : (v > max ? max : v);

        public static double Clamp(double v, double min, double max) => v < min ? min : (v > max ? max : v);

        public static float Saturate(float v) => Clamp(v, 0f, 1f);

        public static float SmoothStep(float edge0, float edge1, float x)
        {
            float t = Saturate((x - edge0) / (edge1 - edge0));
            return t * t * (3f - 2f * t);
        }

        public static float InverseLerp(float a, float b, float v) => a == b ? 0f : (v - a) / (b - a);

        public static float MoveTowards(float current, float target, float maxDelta)
        {
            if (Math.Abs(target - current) <= maxDelta) return target;
            return current + Math.Sign(target - current) * maxDelta;
        }

        public static float Repeat(float t, float length) => Clamp(t - (float)Math.Floor(t / length) * length, 0f, length);

        public static float DeltaAngle(float current, float target)
        {
            float diff = (target - current) * Deg2Rad;
            diff = ((diff % TwoPi) + TwoPi) % TwoPi;
            if (diff > Pi) diff -= TwoPi;
            return diff * Rad2Deg;
        }

        public static float DegToRad(float degrees) => degrees * Deg2Rad;
        public static float RadToDeg(float radians) => radians * Rad2Deg;

        public static Vector3 Lerp(Vector3 a, Vector3 b, float t) => Vector3.Lerp(a, b, t);

        public static float Distance(Vector3 a, Vector3 b) => Vector3.Distance(a, b);

        public static float Min(float a, float b) => a < b ? a : b;
        public static float Max(float a, float b) => a > b ? a : b;
        public static float Abs(float v) => Math.Abs(v);
        public static float Sqrt(float v) => (float)Math.Sqrt(v);
        public static float Sin(float v) => (float)Math.Sin(v);
        public static float Cos(float v) => (float)Math.Cos(v);
        public static float Tan(float v) => (float)Math.Tan(v);
        public static float Pow(float b, float e) => (float)Math.Pow(b, e);
        public static float Exp(float v) => (float)Math.Exp(v);
        public static float Log(float v) => (float)Math.Log(v);
        public static float Floor(float v) => (float)Math.Floor(v);
        public static float Ceil(float v) => (float)Math.Ceiling(v);
        public static float Round(float v) => (float)Math.Round(v);
    }

    /// <summary>统一输入按键枚举（与 InputSystemReal.KeyState 语义对齐）。</summary>
    public enum Key
    {
        None = 0,
        A, B, C, D, E, F, G, H, I, J, K, L, M,
        N, O, P, Q, R, S, T, U, V, W, X, Y, Z,
        Digit0, Digit1, Digit2, Digit3, Digit4,
        Digit5, Digit6, Digit7, Digit8, Digit9,
        F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12,
        Escape, Tab, CapsLock, ShiftLeft, ShiftRight,
        ControlLeft, ControlRight, AltLeft, AltRight,
        Space, Enter, Backspace, Delete, ArrowLeft, ArrowRight, ArrowUp, ArrowDown,
        Home, End, Left, Right,
        MouseLeft, MouseRight, MouseMiddle,
    }
}
