#include "yama/audio/Resampler.h"
#include <cmath>

namespace yama::audio {

static float sinc(float x)
{
    if (std::abs(x) < 1e-6f) return 1.0f;
    float piX = 3.14159265359f * x;
    return std::sin(piX) / piX;
}

static float lanczos_window(float x, int a)
{
    if (std::abs(x) < 1e-6f) return 1.0f;
    if (std::abs(x) >= a) return 0.0f;
    float piX = 3.14159265359f * x;
    return sinc(x) * sinc(x / a);
}

void ResampleSinc(
    const float* input, int inputCount, int inputRate,
    float* output, int outputCount, int outputRate,
    int windowSize)
{
    double ratio = static_cast<double>(inputRate) / static_cast<double>(outputRate);

    for (int i = 0; i < outputCount; i++)
    {
        double srcPos = i * ratio;
        int center = static_cast<int>(srcPos);
        double sum = 0.0;
        double weightSum = 0.0;

        for (int j = center - windowSize; j <= center + windowSize; j++)
        {
            if (j < 0 || j >= inputCount) continue;
            double dist = srcPos - j;
            float w = lanczos_window(static_cast<float>(dist), windowSize);
            sum += input[j] * w;
            weightSum += w;
        }

        output[i] = weightSum > 0.0 ? static_cast<float>(sum / weightSum) : 0.0f;
    }
}

void ResampleLinear(
    const float* input, int inputCount, int inputRate,
    float* output, int outputCount, int outputRate)
{
    double ratio = static_cast<double>(inputRate) / static_cast<double>(outputRate);

    for (int i = 0; i < outputCount; i++)
    {
        double srcPos = i * ratio;
        int idx0 = static_cast<int>(srcPos);
        int idx1 = idx0 + 1;
        double t = srcPos - idx0;

        if (idx0 < 0) idx0 = 0;
        if (idx1 >= inputCount) idx1 = inputCount - 1;

        float v0 = input[idx0];
        float v1 = input[idx1];
        output[i] = v0 + static_cast<float>(t) * (v1 - v0);
    }
}

void ResampleCubic(
    const float* input, int inputCount, int inputRate,
    float* output, int outputCount, int outputRate)
{
    double ratio = static_cast<double>(inputRate) / static_cast<double>(outputRate);

    for (int i = 0; i < outputCount; i++)
    {
        double srcPos = i * ratio;
        int idx = static_cast<int>(srcPos);
        double t = srcPos - idx;

        int idx0 = idx - 1;
        int idx1 = idx;
        int idx2 = idx + 1;
        int idx3 = idx + 2;

        if (idx0 < 0) idx0 = 0;
        if (idx1 < 0) idx1 = 0;
        if (idx2 >= inputCount) idx2 = inputCount - 1;
        if (idx3 >= inputCount) idx3 = inputCount - 1;

        float v0 = input[idx0];
        float v1 = input[idx1];
        float v2 = input[idx2];
        float v3 = input[idx3];

        float t2 = static_cast<float>(t * t);
        float t3 = t2 * static_cast<float>(t);

        float a0 = -0.5f * v0 + 1.5f * v1 - 1.5f * v2 + 0.5f * v3;
        float a1 = v0 - 2.5f * v1 + 2.0f * v2 - 0.5f * v3;
        float a2 = -0.5f * v0 + 0.5f * v2;
        float a3 = v1;

        output[i] = a0 * t3 + a1 * t2 + a2 * static_cast<float>(t) + a3;
    }
}

} // namespace yama::audio
