#include "shanyu/physics/BroadPhase.h"
#include <immintrin.h>
#include <algorithm>
#include <vector>

namespace shanyu::physics {

struct SAPEntry
{
    float minX;
    float maxX;
    int bodyIndex;
};

int BroadPhaseSAP(const float* aabbs, int bodyCount, int* outPairs, int maxPairs)
{
    if (bodyCount <= 0 || maxPairs <= 0)
        return 0;

    // 构建SAPEntry数组
    std::vector<SAPEntry> entries;
    entries.reserve(bodyCount);
    for (int i = 0; i < bodyCount; ++i)
    {
        entries.push_back({
            aabbs[i * 6 + 0],
            aabbs[i * 6 + 3],
            i
        });
    }

    // 按minX排序
    std::sort(entries.begin(), entries.end(), [](const SAPEntry& a, const SAPEntry& b) {
        return a.minX < b.minX;
    });

    int pairCount = 0;

    for (int i = 0; i < bodyCount; ++i)
    {
        const SAPEntry& eA = entries[i];
        int idxA = eA.bodyIndex;

        // 由于已按minX排序，只需检查后续元素直到minX > eA.maxX
        for (int j = i + 1; j < bodyCount; ++j)
        {
            const SAPEntry& eB = entries[j];
            if (eB.minX > eA.maxX)
                break;

            int idxB = eB.bodyIndex;

            // Y轴检查
            float aMinY = aabbs[idxA * 6 + 1], aMaxY = aabbs[idxA * 6 + 4];
            float bMinY = aabbs[idxB * 6 + 1], bMaxY = aabbs[idxB * 6 + 4];
            if (aMinY > bMaxY || bMinY > aMaxY)
                continue;

            // Z轴检查
            float aMinZ = aabbs[idxA * 6 + 2], aMaxZ = aabbs[idxA * 6 + 5];
            float bMinZ = aabbs[idxB * 6 + 2], bMaxZ = aabbs[idxB * 6 + 5];
            if (aMinZ > bMaxZ || bMinZ > aMaxZ)
                continue;

            // 找到碰撞对
            if (pairCount < maxPairs)
            {
                outPairs[pairCount * 2 + 0] = idxA;
                outPairs[pairCount * 2 + 1] = idxB;
                ++pairCount;
            }
            else
            {
                return pairCount; // 缓冲区已满
            }
        }
    }

    return pairCount;
}

int BroadPhaseSIMD(const float* aabbs, int bodyCount, int* outPairs, int maxPairs)
{
    // 当前实现回退到标量SAP
    // 实际AVX2实现可同时加载8个AABB的minX/maxX进行向量化比较
    return BroadPhaseSAP(aabbs, bodyCount, outPairs, maxPairs);
}

} // namespace shanyu::physics
