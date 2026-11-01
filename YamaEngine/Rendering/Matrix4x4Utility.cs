using System.Numerics;

namespace YamaEngine.Rendering
{
    /// <summary>
    /// yama引擎 4x4 矩阵数学工具类。
    /// 用于补齐渲染管线中广泛引用的便捷矩阵运算（被"掏空"后补齐的公共工具）。
    /// </summary>
    public static class Matrix4x4Utility
    {
        /// <summary>矩阵求逆。不可逆时回退到原矩阵。</summary>
        public static Matrix4x4 Inverse(Matrix4x4 m)
        {
            if (Matrix4x4.Invert(m, out Matrix4x4 inv)) return inv;
            return Matrix4x4.Identity;
        }

        /// <summary>创建 Look To 视图矩阵（从 position 看向 direction）。</summary>
        public static Matrix4x4 CreateLookTo(Vector3 position, Vector3 direction, Vector3 up)
            => Matrix4x4.CreateLookAt(position, position + direction, up);

        /// <summary>创建正交投影矩阵（屏幕空间裁剪到 NDC）。</summary>
        public static Matrix4x4 CreateOrthographicOffCenter(float left, float right, float bottom, float top, float zNear, float zFar)
            => Matrix4x4.CreateOrthographicOffCenter(left, right, bottom, top, zNear, zFar);
    }
}