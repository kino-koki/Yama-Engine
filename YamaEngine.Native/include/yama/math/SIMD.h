#pragma once

#include <cstddef>
#include <cstdint>

namespace yama::math {

// ============================================================================
// SIMD 矩阵/向量运算接口
// ============================================================================

/// <summary>批量4x4矩阵乘法 C = A * B。</summary>
void simd_matrix_multiply_4x4_batch(
    const float* __restrict a,
    const float* __restrict b,
    float* __restrict out,
    std::size_t count);

/// <summary>批量向量-矩阵变换 out = vec * mat（行向量）。</summary>
void simd_vector_transform_batch(
    const float* __restrict vec,
    const float* __restrict mat,
    float* __restrict out,
    std::size_t count);

/// <summary>批量向量点积。</summary>
void simd_vector_dot_batch(
    const float* __restrict a,
    const float* __restrict b,
    float* __restrict out,
    std::size_t count);

/// <summary>批量向量叉积。</summary>
void simd_vector_cross_batch(
    const float* __restrict a,
    const float* __restrict b,
    float* __restrict out,
    std::size_t count);

/// <summary>AABB-AABB 批量相交测试（AVX2，一次8个）。</summary>
void simd_aabb_intersect_batch(
    const float* __restrict aabbsA,
    const float* __restrict aabbsB,
    uint32_t* __restrict outMask,
    std::size_t count);

/// <summary>矩阵转置（4x4）。</summary>
void simd_matrix_transpose_4x4(const float* __restrict src, float* __restrict dst);

/// <summary>矩阵求逆（4x4，假设仿射变换）。</summary>
bool simd_matrix_inverse_4x4_affine(const float* __restrict src, float* __restrict dst);

} // namespace yama::math
