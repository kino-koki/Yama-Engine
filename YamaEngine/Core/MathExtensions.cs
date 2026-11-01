// ============================================================================
//  MathExtensions.cs -- 数学扩展方法
//  yama引擎 YamaEngine.Core
// ----------------------------------------------------------------------------
//  为 System.Numerics 向量提供组件访问等扩展方法，避免在调用方重复内联。
// ============================================================================

using System.Collections.Generic;
using System.Numerics;

namespace System.Numerics
{
    /// <summary>
    /// <see cref="Vector3"/> 组件访问扩展。
    /// 提供按索引 (0=X, 1=Y, 2=Z) 读取分量的便捷方法。
    /// </summary>
    public static class Vector3MathExtensions
    {
        /// <summary>按索引获取分量：0→X, 1→Y, 2→Z（越界索引返回 0）。</summary>
        public static float GetComponent(this Vector3 v, int index)
        {
            switch (index)
            {
                case 0: return v.X;
                case 1: return v.Y;
                case 2: return v.Z;
                default: return 0.0f;
            }
        }

        /// <summary>取 <see cref="Vector4"/> 前三分量作为 <see cref="Vector3"/>。</summary>
        public static Vector3 AsVec3(this Vector4 v) => new Vector3(v.X, v.Y, v.Z);

        /// <summary>取 <see cref="Vector3"/> 前两分量作为 <see cref="Vector2"/>。</summary>
        public static Vector2 ToVector2(this Vector3 v) => new Vector2(v.X, v.Y);

        /// <summary>设置矩阵指定行（row: 0..3），写入 M11..M14 / M21..M24 / M31..M34 / M41..M44。</summary>
        public static void SetRow(this ref Matrix4x4 m, int row, Vector4 values)
        {
            switch (row)
            {
                case 0: m.M11 = values.X; m.M12 = values.Y; m.M13 = values.Z; m.M14 = values.W; break;
                case 1: m.M21 = values.X; m.M22 = values.Y; m.M23 = values.Z; m.M24 = values.W; break;
                case 2: m.M31 = values.X; m.M32 = values.Y; m.M33 = values.Z; m.M34 = values.W; break;
                case 3: m.M41 = values.X; m.M42 = values.Y; m.M43 = values.Z; m.M44 = values.W; break;
            }
        }
    }

    /// <summary>
    /// 只读列表通用扩展，补齐 <see cref="List{T}.Find"/> 在 <see cref="IReadOnlyList{T}"/> 上的缺失。
    /// </summary>
    public static class ReadOnlyListExtensions
    {
        /// <summary>按谓词查找第一个匹配项，无匹配返回 default。</summary>
        public static T? Find<T>(this IReadOnlyList<T> list, Predicate<T> match)
        {
            if (list == null || match == null) return default;
            for (int i = 0; i < list.Count; i++)
                if (match(list[i])) return list[i];
            return default;
        }
    }
}