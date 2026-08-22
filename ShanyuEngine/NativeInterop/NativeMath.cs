using System.Runtime.InteropServices;

namespace ShanyuEngine.NativeInterop
{
    public static class NativeMath
    {
        private const string DllName = "ShanyuEngine.Native";

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void shanyu_simd_matrix_multiply(
            float[] a, float[] b, float[] output, int count);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void shanyu_simd_vector_transform(
            float[] vec, float[] mat, float[] output, int count);
    }
}
