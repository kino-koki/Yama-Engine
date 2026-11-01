using System.Runtime.InteropServices;

namespace YamaEngine.NativeInterop
{
    public static class NativeMath
    {
        private const string DllName = "YamaEngine.Native";

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void yama_simd_matrix_multiply(
            float[] a, float[] b, float[] output, int count);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void yama_simd_vector_transform(
            float[] vec, float[] mat, float[] output, int count);
    }
}
