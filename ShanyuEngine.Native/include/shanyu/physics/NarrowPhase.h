#pragma once

namespace shanyu::physics {

/// <summary>GJK距离算法：计算两个凸包间的最短距离。</summary>
/// <returns>最短距离，负值表示穿透。</returns>
float GJKDistance(
    const float* verticesA, int countA,
    const float* verticesB, int countB,
    const float* transformA, const float* transformB,
    float* outNormal,
    float* outPointA,
    float* outPointB);

/// <summary>EPA算法：计算穿透深度和接触法线。</summary>
float EPADepth(
    const float* verticesA, int countA,
    const float* verticesB, int countB,
    const float* transformA, const float* transformB,
    float* outNormal,
    float* outContactPoint);

/// <summary>支持函数：返回凸包在给定方向上的最远点。</summary>
void SupportPoint(
    const float* vertices, int count,
    const float* transform,
    float dirX, float dirY, float dirZ,
    float* outPoint);

} // namespace shanyu::physics
