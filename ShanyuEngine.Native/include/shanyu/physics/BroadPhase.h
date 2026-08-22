#pragma once

#include <cstddef>
#include <cstdint>

namespace shanyu::physics {

/// <summary>Sweep-and-Prune 广相碰撞检测。</summary>
/// <param name="aabbs">AABB数组 [minX,minY,minZ,maxX,maxY,maxZ] * bodyCount。</param>
/// <param name="bodyCount">刚体数量。</param>
/// <param name="outPairs">输出碰撞对 [bodyA, bodyB]。</param>
/// <param name="maxPairs">最大输出对数。</param>
/// <returns>实际碰撞对数量。</returns>
int BroadPhaseSAP(
    const float* aabbs,
    int bodyCount,
    int* outPairs,
    int maxPairs);

/// <summary>SIMD加速版本：使用AVX2同时处理多个AABB。</summary>
int BroadPhaseSIMD(
    const float* aabbs,
    int bodyCount,
    int* outPairs,
    int maxPairs);

} // namespace shanyu::physics
