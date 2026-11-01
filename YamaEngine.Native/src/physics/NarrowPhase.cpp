#include "yama/physics/NarrowPhase.h"
#include <cmath>
#include <algorithm>

namespace yama::physics {

static const int GJK_MAX_ITERATIONS = 32;
static const float GJK_TOLERANCE = 1e-5f;
static const int EPA_MAX_ITERATIONS = 32;
static const int EPA_MAX_FACES = 128;

// ============================================================================
// 简单向量运算（3D）
// ============================================================================
struct Vec3
{
    float x, y, z;
    Vec3() = default;
    Vec3(float x_, float y_, float z_) : x(x_), y(y_), z(z_) {}
    Vec3 operator+(const Vec3& o) const { return Vec3(x + o.x, y + o.y, z + o.z); }
    Vec3 operator-(const Vec3& o) const { return Vec3(x - o.x, y - o.y, z - o.z); }
    Vec3 operator*(float s) const { return Vec3(x * s, y * s, z * s); }
    Vec3 operator-() const { return Vec3(-x, -y, -z); }
    float Dot(const Vec3& o) const { return x * o.x + y * o.y + z * o.z; }
    float LengthSq() const { return x * x + y * y + z * z; }
    float Length() const { return std::sqrt(LengthSq()); }
    Vec3 Normalized() const
    {
        float len = Length();
        if (len < 1e-6f) return Vec3(0, 0, 0);
        return Vec3(x / len, y / len, z / len);
    }
    static Vec3 Cross(const Vec3& a, const Vec3& b)
    {
        return Vec3(
            a.y * b.z - a.z * b.y,
            a.z * b.x - a.x * b.z,
            a.x * b.y - a.y * b.x);
    }
};

// ============================================================================
// 变换顶点
// ============================================================================
static Vec3 TransformPoint(const float* transform, const Vec3& p)
{
    // 行主序4x4矩阵，假设最后一行为[0,0,0,1]
    return Vec3(
        transform[0] * p.x + transform[1] * p.y + transform[2] * p.z + transform[3],
        transform[4] * p.x + transform[5] * p.y + transform[6] * p.z + transform[7],
        transform[8] * p.x + transform[9] * p.y + transform[10] * p.z + transform[11]
    );
}

void SupportPoint(const float* vertices, int count, const float* transform,
                  float dirX, float dirY, float dirZ, float* outPoint)
{
    Vec3 dir(dirX, dirY, dirZ);
    Vec3 best;
    float maxDot = -1e30f;

    for (int i = 0; i < count; ++i)
    {
        Vec3 v(vertices[i * 3 + 0], vertices[i * 3 + 1], vertices[i * 3 + 2]);
        if (transform)
            v = TransformPoint(transform, v);
        float d = v.Dot(dir);
        if (d > maxDot)
        {
            maxDot = d;
            best = v;
        }
    }

    outPoint[0] = best.x;
    outPoint[1] = best.y;
    outPoint[2] = best.z;
}

// ============================================================================
// GJK 实现
// ============================================================================
struct Simplex
{
    Vec3 points[4];
    Vec3 aPoints[4]; // 物体A上的点
    Vec3 bPoints[4]; // 物体B上的点
    int count = 0;

    void Add(const Vec3& p, const Vec3& a, const Vec3& b)
    {
        points[count] = p;
        aPoints[count] = a;
        bPoints[count] = b;
        ++count;
    }
};

static Vec3 Support(const float* vertsA, int countA, const float* transA,
                    const float* vertsB, int countB, const float* transB,
                    const Vec3& dir, Vec3& outA, Vec3& outB)
{
    SupportPoint(vertsA, countA, transA, dir.x, dir.y, dir.z, reinterpret_cast<float*>(&outA));
    SupportPoint(vertsB, countB, transB, -dir.x, -dir.y, -dir.z, reinterpret_cast<float*>(&outB));
    return outA - outB;
}

static bool GJK_DoSimplex(Simplex& s, Vec3& dir)
{
    // 根据单纯形维度处理
    if (s.count == 2)
    {
        // 线段
        Vec3 ab = s.points[1] - s.points[0];
        Vec3 a0 = -s.points[0];
        if (ab.Dot(a0) > 0)
            dir = Vec3::Cross(Vec3::Cross(ab, a0), ab);
        else
        {
            s.points[0] = s.points[1];
            s.aPoints[0] = s.aPoints[1];
            s.bPoints[0] = s.bPoints[1];
            s.count = 1;
            dir = a0;
        }
        return false;
    }
    else if (s.count == 3)
    {
        // 三角形
        Vec3 ab = s.points[1] - s.points[0];
        Vec3 ac = s.points[2] - s.points[0];
        Vec3 abc = Vec3::Cross(ab, ac);
        Vec3 a0 = -s.points[0];

        if (Vec3::Cross(abc, ac).Dot(a0) > 0)
        {
            if (ac.Dot(a0) > 0)
            {
                s.points[1] = s.points[2];
                s.aPoints[1] = s.aPoints[2];
                s.bPoints[1] = s.bPoints[2];
                s.count = 2;
                dir = Vec3::Cross(Vec3::Cross(ac, a0), ac);
            }
            else
            {
                if (ab.Dot(a0) > 0)
                {
                    s.count = 2;
                    dir = Vec3::Cross(Vec3::Cross(ab, a0), ab);
                }
                else
                {
                    s.count = 1;
                    dir = a0;
                }
            }
        }
        else
        {
            if (Vec3::Cross(ab, abc).Dot(a0) > 0)
            {
                if (ab.Dot(a0) > 0)
                {
                    s.count = 2;
                    dir = Vec3::Cross(Vec3::Cross(ab, a0), ab);
                }
                else
                {
                    s.count = 1;
                    dir = a0;
                }
            }
            else
            {
                if (abc.Dot(a0) > 0)
                    dir = abc;
                else
                {
                    std::swap(s.points[1], s.points[2]);
                    std::swap(s.aPoints[1], s.aPoints[2]);
                    std::swap(s.bPoints[1], s.bPoints[2]);
                    dir = -abc;
                }
            }
        }
        return false;
    }
    else if (s.count == 4)
    {
        // 四面体 - 检查原点是否在内部
        Vec3 a = s.points[0], b = s.points[1], c = s.points[2], d = s.points[3];
        Vec3 ab = b - a, ac = c - a, ad = d - a;
        Vec3 abc = Vec3::Cross(ab, ac);
        Vec3 acd = Vec3::Cross(ac, ad);
        Vec3 adb = Vec3::Cross(ad, ab);
        Vec3 a0 = -a;

        if (abc.Dot(a0) > 0)
        {
            s.count = 3;
            return GJK_DoSimplex(s, dir);
        }
        if (acd.Dot(a0) > 0)
        {
            s.points[1] = c; s.aPoints[1] = s.aPoints[2]; s.bPoints[1] = s.bPoints[2];
            s.points[2] = d; s.aPoints[2] = s.aPoints[3]; s.bPoints[2] = s.bPoints[3];
            s.count = 3;
            return GJK_DoSimplex(s, dir);
        }
        if (adb.Dot(a0) > 0)
        {
            s.points[1] = d; s.aPoints[1] = s.aPoints[3]; s.bPoints[1] = s.bPoints[3];
            s.points[2] = b; s.aPoints[2] = s.aPoints[1]; s.bPoints[2] = s.bPoints[1];
            s.count = 3;
            return GJK_DoSimplex(s, dir);
        }
        return true; // 原点在四面体内
    }
    return false;
}

float GJKDistance(const float* verticesA, int countA,
                  const float* verticesB, int countB,
                  const float* transformA, const float* transformB,
                  float* outNormal, float* outPointA, float* outPointB)
{
    Simplex s;
    Vec3 dir(1.0f, 1.0f, 1.0f);
    Vec3 a, b;
    Vec3 support = Support(verticesA, countA, transformA,
                           verticesB, countB, transformB,
                           dir, a, b);
    s.Add(support, a, b);
    dir = -support;

    float distSq = 1e30f;
    for (int iter = 0; iter < GJK_MAX_ITERATIONS; ++iter)
    {
        support = Support(verticesA, countA, transformA,
                          verticesB, countB, transformB,
                          dir, a, b);

        if (support.Dot(dir) < 0)
        {
            // 无交集，计算距离
            Vec3 closest;
            if (s.count == 1)
                closest = s.points[0];
            else if (s.count == 2)
            {
                Vec3 ab = s.points[1] - s.points[0];
                float t = -s.points[0].Dot(ab) / ab.Dot(ab);
                t = std::max(0.0f, std::min(1.0f, t));
                closest = s.points[0] + ab * t;
            }
            else
            {
                // 三角形最近点（简化）
                closest = s.points[0];
            }

            float d = closest.Length();
            Vec3 normal = closest.Normalized();
            if (outNormal)
            {
                outNormal[0] = normal.x;
                outNormal[1] = normal.y;
                outNormal[2] = normal.z;
            }
            if (outPointA) { outPointA[0] = a.x; outPointA[1] = a.y; outPointA[2] = a.z; }
            if (outPointB) { outPointB[0] = b.x; outPointB[1] = b.y; outPointB[2] = b.z; }
            return d;
        }

        s.Add(support, a, b);
        if (GJK_DoSimplex(s, dir))
        {
            // 穿透，调用EPA
            return -EPADepth(verticesA, countA, verticesB, countB,
                             transformA, transformB, outNormal, outPointA);
        }

        float newDistSq = dir.LengthSq();
        if (newDistSq < distSq)
            distSq = newDistSq;
        else if (std::abs(newDistSq - distSq) < GJK_TOLERANCE)
            break;
    }

    // 收敛但未找到精确解，返回近似距离
    float d = std::sqrt(distSq);
    if (outNormal)
    {
        Vec3 n = dir.Normalized();
        outNormal[0] = n.x; outNormal[1] = n.y; outNormal[2] = n.z;
    }
    return d;
}

// ============================================================================
// EPA 实现（简化版）
// ============================================================================
struct EPAFace
{
    Vec3 a, b, c;
    Vec3 normal;
    float dist;
};

float EPADepth(const float* verticesA, int countA,
               const float* verticesB, int countB,
               const float* transformA, const float* transformB,
               float* outNormal, float* outContactPoint)
{
    // 简化EPA实现：使用GJK最终单纯形扩展
    // 实际生产环境需要完整多面体管理

    Vec3 dir(1, 0, 0);
    Vec3 a, b;
    Vec3 support = Support(verticesA, countA, transformA,
                           verticesB, countB, transformB,
                           dir, a, b);

    float depth = support.Length();
    Vec3 normal = support.Normalized();

    if (outNormal)
    {
        outNormal[0] = normal.x;
        outNormal[1] = normal.y;
        outNormal[2] = normal.z;
    }
    if (outContactPoint)
    {
        outContactPoint[0] = (a.x + b.x) * 0.5f;
        outContactPoint[1] = (a.y + b.y) * 0.5f;
        outContactPoint[2] = (a.z + b.z) * 0.5f;
    }

    return depth > 0 ? depth : 0;
}

} // namespace yama::physics
