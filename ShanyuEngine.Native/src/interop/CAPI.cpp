#include "shanyu/interop/CAPI.h"
#include "shanyu/math/SIMD.h"
#include "shanyu/physics/BroadPhase.h"
#include "shanyu/physics/NarrowPhase.h"
#include "shanyu/physics/Solver.h"
#include "shanyu/audio/FFT.h"
#include "shanyu/audio/Resampler.h"
#include "shanyu/rhi/D3D12CommandList.h"

#include <cstring>
#include <cstdlib>
#include <immintrin.h>

// ============================================================================
// 数学 SIMD
// ============================================================================

SHANYU_API void shanyu_simd_matrix_multiply(const float* a, const float* b, float* out, int count)
{
    shanyu::math::simd_matrix_multiply_4x4_batch(a, b, out, static_cast<std::size_t>(count));
}

SHANYU_API void shanyu_simd_vector_transform(const float* vec, const float* mat, float* out, int count)
{
    shanyu::math::simd_vector_transform_batch(vec, mat, out, static_cast<std::size_t>(count));
}

// ============================================================================
// 物理广相 Broad Phase
// ============================================================================

SHANYU_API int shanyu_physics_broadphase_sap(
    const float* aabbs, int bodyCount, int* outPairs, int maxPairs)
{
    return shanyu::physics::BroadPhaseSAP(aabbs, bodyCount, outPairs, maxPairs);
}

// ============================================================================
// 物理窄相 Narrow Phase
// ============================================================================

SHANYU_API float shanyu_physics_gjk_distance(
    const float* verticesA, int countA,
    const float* verticesB, int countB,
    const float* transformA, const float* transformB,
    float* outNormal, float* outPointA, float* outPointB)
{
    return shanyu::physics::GJKDistance(verticesA, countA, verticesB, countB,
                                        transformA, transformB,
                                        outNormal, outPointA, outPointB);
}

SHANYU_API float shanyu_physics_epa_depth(
    const float* verticesA, int countA,
    const float* verticesB, int countB,
    const float* transformA, const float* transformB,
    float* outNormal, float* outContactPoint)
{
    return shanyu::physics::EPADepth(verticesA, countA, verticesB, countB,
                                     transformA, transformB,
                                     outNormal, outContactPoint);
}

// ============================================================================
// 物理求解器 Solver
// ============================================================================

SHANYU_API void shanyu_physics_solver_velocity(
    const float* invMass,
    const float* invInertia,
    float* velocities,
    float* angularVelocities,
    const float* constraints,
    int constraintCount,
    int bodyCount,
    int iterations)
{
    shanyu::physics::SolveVelocityConstraints(invMass, invInertia,
                                               velocities, angularVelocities,
                                               constraints,
                                               constraintCount, bodyCount,
                                               iterations);
}

// ============================================================================
// 音频 FFT
// ============================================================================

SHANYU_API void shanyu_audio_fft_forward(float* real, float* imag, int n)
{
    shanyu::audio::FFTForward(real, imag, n);
}

SHANYU_API void shanyu_audio_fft_inverse(float* real, float* imag, int n)
{
    shanyu::audio::FFTInverse(real, imag, n);
}

// ============================================================================
// 音频重采样
// ============================================================================

SHANYU_API void shanyu_audio_resample_sinc(
    const float* input, int inputCount, int inputRate,
    float* output, int outputCount, int outputRate)
{
    shanyu::audio::ResampleSinc(input, inputCount, inputRate,
                                output, outputCount, outputRate,
                                32); // 默认Lanczos窗口半宽
}

SHANYU_API void shanyu_audio_resample_linear(
    const float* input, int inputCount, int inputRate,
    float* output, int outputCount, int outputRate)
{
    shanyu::audio::ResampleLinear(input, inputCount, inputRate,
                                  output, outputCount, outputRate);
}

// ============================================================================
// RHI 命令缓冲
// ============================================================================

static thread_local shanyu::rhi::ICommandBuffer* s_currentCmdBuffer = nullptr;

SHANYU_API void* shanyu_rhi_create_command_buffer()
{
    return shanyu::rhi::CreateCommandBuffer();
}

SHANYU_API void shanyu_rhi_destroy_command_buffer(void* handle)
{
    if (handle)
    {
        shanyu::rhi::DestroyCommandBuffer(static_cast<shanyu::rhi::ICommandBuffer*>(handle));
    }
}

SHANYU_API void shanyu_rhi_cmd_draw_indexed(void* handle, int indexCount, int instanceCount)
{
    if (handle)
    {
        static_cast<shanyu::rhi::ICommandBuffer*>(handle)->DrawIndexed(
            static_cast<uint32_t>(indexCount), static_cast<uint32_t>(instanceCount));
    }
}

SHANYU_API void shanyu_rhi_cmd_resource_barrier(void* handle, int type, void* resource)
{
    if (handle)
    {
        static_cast<shanyu::rhi::ICommandBuffer*>(handle)->ResourceBarrier(
            static_cast<uint32_t>(type), resource);
    }
}

SHANYU_API void shanyu_rhi_submit_command_buffer(void* handle)
{
    // 预留：实际提交到队列，当前版本为桩实现
    (void)handle;
}

// ============================================================================
// 版本与初始化
// ============================================================================

SHANYU_API const char* shanyu_get_version_string()
{
    return "ShanyuEngine.Native 1.0.0";
}

SHANYU_API int shanyu_cpu_feature_detect()
{
    int features = 0;

    int cpuInfo[4] = { 0 };
    __cpuid(cpuInfo, 1);

    // SSE2: bit 26 of EDX
    if (cpuInfo[3] & (1 << 26))
        features |= 1;

    // AVX: bit 28 of ECX
    if (cpuInfo[2] & (1 << 28))
        features |= 2;

    // AVX2: bit 5 of EBX (leaf 7 subleaf 0)
    int cpuInfo7[4] = { 0 };
    __cpuidex(cpuInfo7, 7, 0);
    if (cpuInfo7[1] & (1 << 5))
        features |= 4;

    // AVX-512F: bit 16 of EBX (leaf 7 subleaf 0)
    if (cpuInfo7[1] & (1 << 16))
        features |= 8;

    return features;
}
