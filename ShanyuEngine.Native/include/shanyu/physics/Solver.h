#pragma once

namespace shanyu::physics {

/// <summary>速度约束求解（Sequential Impulse）。</summary>
void SolveVelocityConstraints(
    const float* invMass,
    const float* invInertia,
    float* velocities,
    float* angularVelocities,
    const float* constraints,
    int constraintCount,
    int bodyCount,
    int iterations);

/// <summary>位置约束求解（穿透修正）。</summary>
void SolvePositionConstraints(
    const float* invMass,
    float* positions,
    float* orientations,
    const float* contacts,
    int contactCount,
    int iterations);

} // namespace shanyu::physics
