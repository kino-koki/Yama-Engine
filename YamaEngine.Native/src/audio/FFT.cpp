#include "yama/audio/FFT.h"
#include <immintrin.h>
#include <cmath>
#include <utility>

namespace yama::audio {

static void bit_reverse_swap(float* real, float* imag, int n)
{
    int j = 0;
    for (int i = 0; i < n; i++)
    {
        if (j > i)
        {
            std::swap(real[i], real[j]);
            std::swap(imag[i], imag[j]);
        }
        int m = n >> 1;
        while (m >= 1 && j >= m)
        {
            j -= m;
            m >>= 1;
        }
        j += m;
    }
}

void FFTForward(float* real, float* imag, int n)
{
    bit_reverse_swap(real, imag, n);

    for (int s = 1; s < n; s <<= 1)
    {
        float angle = -3.14159265359f / s;
        float wpr = std::cos(angle);
        float wpi = std::sin(angle);

        for (int i = 0; i < n; i += (s << 1))
        {
            float wr = 1.0f, wi = 0.0f;
            for (int j = 0; j < s; j++)
            {
                int k = i + j + s;
                float tr = wr * real[k] - wi * imag[k];
                float ti = wr * imag[k] + wi * real[k];
                real[k] = real[i + j] - tr;
                imag[k] = imag[i + j] - ti;
                real[i + j] += tr;
                imag[i + j] += ti;

                float temp = wr;
                wr = wr * wpr - wi * wpi;
                wi = temp * wpi + wi * wpr;
            }
        }
    }
}

void FFTInverse(float* real, float* imag, int n)
{
    for (int i = 0; i < n; i++) imag[i] = -imag[i];
    FFTForward(real, imag, n);
    for (int i = 0; i < n; i++)
    {
        real[i] /= n;
        imag[i] = -imag[i] / n;
    }
}

void FFTRealForward(const float* input, float* outReal, float* outImag, int n)
{
    for (int i = 0; i < n; i++)
    {
        outReal[i] = input[i];
        outImag[i] = 0.0f;
    }
    FFTForward(outReal, outImag, n);
}

void FFTBitReverse(float* real, float* imag, int n)
{
    bit_reverse_swap(real, imag, n);
}

} // namespace yama::audio
