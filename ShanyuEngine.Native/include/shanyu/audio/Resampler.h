#pragma once

namespace shanyu::audio {

/// <summary>SINC插值重采样（高质量，带抗混叠）。</summary>
/// <param name="input">输入采样数组。</param>
/// <param name="inputCount">输入采样数。</param>
/// <param name="inputRate">输入采样率。</param>
/// <param name="output">输出采样数组（预分配）。</param>
/// <param name="outputCount">输出采样数。</param>
/// <param name="outputRate">输出采样率。</param>
/// <param name="windowSize">SINC窗大小（默认32）。</param>
void ResampleSinc(
    const float* input, int inputCount, int inputRate,
    float* output, int outputCount, int outputRate,
    int windowSize = 32);

/// <summary>线性插值重采样（SSE加速，低延迟）。</summary>
void ResampleLinear(
    const float* input, int inputCount, int inputRate,
    float* output, int outputCount, int outputRate);

/// <summary>立方插值重采样。</summary>
void ResampleCubic(
    const float* input, int inputCount, int inputRate,
    float* output, int outputCount, int outputRate);

} // namespace shanyu::audio
