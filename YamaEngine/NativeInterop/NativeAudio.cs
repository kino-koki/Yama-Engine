using System.Runtime.InteropServices;

namespace YamaEngine.NativeInterop
{
    public static class NativeAudio
    {
        private const string DllName = "YamaEngine.Native";

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void yama_audio_fft_forward(float[] real, float[] imag, int n);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void yama_audio_fft_inverse(float[] real, float[] imag, int n);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void yama_audio_resample_sinc(
            float[] input, int inputCount, int inputRate,
            float[] output, int outputCount, int outputRate);
    }
}
