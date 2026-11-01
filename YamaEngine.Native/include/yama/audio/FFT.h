#pragma once

namespace yama::audio {

/// <summary>正向FFT（Cooley-Tukey radix-2，AVX2加速）。</summary>
/// <param name="real">实部数组（输入/输出），长度必须为2的幂。</param>
/// <param name="imag">虚部数组（输入/输出）。</param>
/// <param name="n">长度（2的幂，最大支持65536）。</param>
void FFTForward(float* real, float* imag, int n);

/// <summary>逆FFT。</summary>
void FFTInverse(float* real, float* imag, int n);

/// <summary>实数FFT（包装：实数输入→复数频谱）。</summary>
void FFTRealForward(const float* input, float* outReal, float* outImag, int n);

/// <summary>位反转重排（内部使用）。</summary>
void FFTBitReverse(float* real, float* imag, int n);

} // namespace yama::audio
