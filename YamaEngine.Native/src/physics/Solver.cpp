#include "yama/physics/Solver.h"
#include <cmath>

namespace yama::physics {

void SolveVelocityConstraints(
    const float* invMass,
    const float* invInertia,
    float* velocities,
    float* angularVelocities,
    const float* constraints,
    int constraintCount,
    int bodyCount,
    int iterations)
{
    // constraints紧凑格式（每约束N个float）：
    // [bodyA, bodyB, normalX, normalY, normalZ,
    //  raX, raY, raZ, rbX, rbY, rbZ,
    //  bias, impulse, friction, restitution]
    const int STRIDE = 16;

    for (int iter = 0; iter < iterations; ++iter)
    {
        for (int i = 0; i < constraintCount; ++i)
        {
            const float* c = constraints + i * STRIDE;
            int bodyA = (int)c[0];
            int bodyB = (int)c[1];
            if (bodyA < 0 || bodyB < 0 || bodyA >= bodyCount || bodyB >= bodyCount)
                continue;

            float nx = c[2], ny = c[3], nz = c[4];
            float raX = c[5], raY = c[6], raZ = c[7];
            float rbX = c[8], rbY = c[9], rbZ = c[10];
            float bias = c[11];
            float friction = c[14];
            float restitution = c[15];

            float* vA = velocities + bodyA * 3;
            float* vB = velocities + bodyB * 3;
            float* wA = angularVelocities + bodyA * 3;
            float* wB = angularVelocities + bodyB * 3;

            float mA = invMass[bodyA];
            float mB = invMass[bodyB];

            // 相对速度
            float rvX = (vB[0] + (wB[1] * rbZ - wB[2] * rbY)) -
                        (vA[0] + (wA[1] * raZ - wA[2] * raY));
            float rvY = (vB[1] + (wB[2] * rbX - wB[0] * rbZ)) -
                        (vA[1] + (wA[2] * raX - wA[0] * raZ));
            float rvZ = (vB[2] + (wB[0] * rbY - wB[1] * rbX)) -
                        (vA[2] + (wA[0] * raY - wA[1] * raX));

            float vn = rvX * nx + rvY * ny + rvZ * nz;

            // 有效质量
            float iiA = invInertia[bodyA * 3 + 0];
            float iiB = invInertia[bodyB * 3 + 0];

            float rna = raX * nx + raY * ny + raZ * nz;
            float rnb = rbX * nx + rbY * ny + rbZ * nz;
            float wa = iiA * rna * rna;
            float wb = iiB * rnb * rnb;
            float effectiveMass = 1.0f / (mA + mB + wa + wb + 1e-6f);

            // 脉冲
            float j = -(vn + bias) * effectiveMass;
            float jOld = c[12];
            float jMax = jOld + j;
            if (jMax < 0) jMax = 0;
            j = jMax - jOld;

            // 应用脉冲
            float jx = j * nx, jy = j * ny, jz = j * nz;
            vA[0] -= jx * mA; vA[1] -= jy * mA; vA[2] -= jz * mA;
            vB[0] += jx * mB; vB[1] += jy * mB; vB[2] += jz * mB;

            // 摩擦力（简化切向）
            if (friction > 0)
            {
                float rvTX = rvX - vn * nx;
                float rvTY = rvY - vn * ny;
                float rvTZ = rvZ - vn * nz;
                float rvTLen = std::sqrt(rvTX * rvTX + rvTY * rvTY + rvTZ * rvTZ);
                if (rvTLen > 1e-6f)
                {
                    float tx = rvTX / rvTLen;
                    float ty = rvTY / rvTLen;
                    float tz = rvTZ / rvTLen;
                    float jt = -rvTLen * effectiveMass * friction;
                    vA[0] -= jt * tx * mA; vA[1] -= jt * ty * mA; vA[2] -= jt * tz * mA;
                    vB[0] += jt * tx * mB; vB[1] += jt * ty * mB; vB[2] += jt * tz * mB;
                }
            }
        }
    }
}

void SolvePositionConstraints(
    const float* invMass,
    float* positions,
    float* orientations,
    const float* contacts,
    int contactCount,
    int iterations)
{
    const int STRIDE = 10; // [bodyA, bodyB, normalX,Y,Z, penetration, raX,Y,Z, rbX,Y,Z]

    for (int iter = 0; iter < iterations; ++iter)
    {
        for (int i = 0; i < contactCount; ++i)
        {
            const float* ct = contacts + i * STRIDE;
            int bodyA = (int)ct[0];
            int bodyB = (int)ct[1];
            if (bodyA < 0 || bodyB < 0)
                continue;

            float nx = ct[2], ny = ct[3], nz = ct[4];
            float penetration = ct[5];
            if (penetration <= 0)
                continue;

            float mA = invMass[bodyA];
            float mB = invMass[bodyB];
            float totalMass = mA + mB;
            if (totalMass < 1e-6f)
                continue;

            float correction = (penetration - 1e-3f) / totalMass; // 允许0.001 slop
            if (correction < 0) correction = 0;
            correction *= 0.2f; // Baumgarte因子

            float* pA = positions + bodyA * 3;
            float* pB = positions + bodyB * 3;

            pA[0] -= correction * nx * mA;
            pA[1] -= correction * ny * mA;
            pA[2] -= correction * nz * mA;

            pB[0] += correction * nx * mB;
            pB[1] += correction * ny * mB;
            pB[2] += correction * nz * mB;
        }
    }
}

} // namespace yama::physics
