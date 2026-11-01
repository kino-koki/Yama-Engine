// ============================================================================================
//  EdgeAdjacency.cpp — 实现见 EdgeAdjacency.h
// ============================================================================================
#include "EdgeAdjacency.h"

#include <cmath>
#include <algorithm>

namespace yama {

// --------------------------------------------------------------------------------------------
//  小型 3D 向量工具（避免引入 Jolt 头文件，保持本文件可独立测试）
// --------------------------------------------------------------------------------------------
namespace {
struct V3 { float x, y, z; };

inline V3 vsub(const V3& a, const V3& b) { return { a.x - b.x, a.y - b.y, a.z - b.z }; }
inline V3 vadd(const V3& a, const V3& b) { return { a.x + b.x, a.y + b.y, a.z + b.z }; }
inline V3 vmul(const V3& a, float s) { return { a.x * s, a.y * s, a.z * s }; }
inline float vdot(const V3& a, const V3& b) { return a.x*b.x + a.y*b.y + a.z*b.z; }
inline V3 vcross(const V3& a, const V3& b) {
    return { a.y*b.z - a.z*b.y, a.z*b.x - a.x*b.z, a.x*b.y - a.y*b.x };
}
inline float vlen(const V3& a) { return std::sqrt(vdot(a, a)); }
inline V3 vnorm(const V3& a) { float l = vlen(a); return l > 1e-12f ? vmul(a, 1.0f / l) : V3{0,0,0}; }

inline V3 load3(const float* p) { return { p[0], p[1], p[2] }; }
inline void store3(const V3& v, float* p) { p[0]=v.x; p[1]=v.y; p[2]=v.z; }
} // namespace

// --------------------------------------------------------------------------------------------
void EdgeAdjacency::Build(const float* vertices, int vertexCount,
                          const uint32_t* indices, int indexCount)
{
    _vertices.assign(vertices, vertices + (size_t)vertexCount * 3);
    _indices.assign(indices, indices + indexCount);

    const int triCount = indexCount / 3;
    _faceNormals.resize((size_t)triCount * 3);
    _adjacency.assign((size_t)triCount * 3, -1);
    _edgeMap.clear();
    _edgeMap.reserve((size_t)triCount * 3);

    // 1) 计算每三角面单位法线
    for (int t = 0; t < triCount; ++t)
    {
        uint32_t i0 = _indices[t*3+0];
        uint32_t i1 = _indices[t*3+1];
        uint32_t i2 = _indices[t*3+2];
        V3 v0 = load3(&_vertices[(size_t)i0*3]);
        V3 v1 = load3(&_vertices[(size_t)i1*3]);
        V3 v2 = load3(&_vertices[(size_t)i2*3]);
        V3 n = vnorm(vcross(vsub(v1, v0), vsub(v2, v0)));
        store3(n, &_faceNormals[(size_t)t*3]);
    }

    // 2) 构建边→三角面映射。每条边最多被 2 个三角面共享。
    for (int t = 0; t < triCount; ++t)
    {
        for (int e = 0; e < 3; ++e)
        {
            uint32_t a = _indices[t*3 + e];
            uint32_t b = _indices[t*3 + ((e + 1) % 3)];
            uint64_t key = MakeEdgeKey(a, b);
            auto it = _edgeMap.find(key);
            if (it == _edgeMap.end())
            {
                _edgeMap.emplace(key, std::make_pair(t, -1));
            }
            else
            {
                // 第二个三角面共享此边
                if (it->second.second == -1)
                    it->second.second = t;
                // 超过 2 个三角面共享同一边（非流形）——忽略额外三角面，邻接取前两个
            }
        }
    }

    // 3) 填充每三角面每条边的邻接三角面索引
    for (int t = 0; t < triCount; ++t)
    {
        for (int e = 0; e < 3; ++e)
        {
            uint32_t a = _indices[t*3 + e];
            uint32_t b = _indices[t*3 + ((e + 1) % 3)];
            uint64_t key = MakeEdgeKey(a, b);
            auto it = _edgeMap.find(key);
            if (it != _edgeMap.end())
            {
                const auto& pr = it->second;
                _adjacency[(size_t)t*3 + e] = (pr.first == t) ? pr.second : pr.first;
            }
        }
    }
}

// --------------------------------------------------------------------------------------------
//  点在三角面上：用重心坐标。先快速平面距离剔除，再算重心。
// --------------------------------------------------------------------------------------------
bool EdgeAdjacency::PointOnTriangle(int triIndex, const float p[3], float tolerance, float bary[3]) const
{
    uint32_t i0 = _indices[triIndex*3+0];
    uint32_t i1 = _indices[triIndex*3+1];
    uint32_t i2 = _indices[triIndex*3+2];
    V3 v0 = load3(&_vertices[(size_t)i0*3]);
    V3 v1 = load3(&_vertices[(size_t)i1*3]);
    V3 v2 = load3(&_vertices[(size_t)i2*3]);
    V3 P = load3(p);

    V3 n = load3(&_faceNormals[(size_t)triIndex*3]);
    // 平面距离剔除
    float d = vdot(n, vsub(P, v0));
    if (std::fabs(d) > tolerance) return false;

    // 重心坐标（投影到最对齐的轴平面以避免退化）
    V3 edge1 = vsub(v1, v0);
    V3 edge2 = vsub(v2, v0);
    V3 vp    = vsub(P, v0);
    float d00 = vdot(edge1, edge1);
    float d01 = vdot(edge1, edge2);
    float d11 = vdot(edge2, edge2);
    float d0p = vdot(edge1, vp);
    float d1p = vdot(edge2, vp);
    float denom = d00 * d11 - d01 * d01;
    if (std::fabs(denom) < 1e-12f) return false;

    float v = (d11 * d0p - d01 * d1p) / denom;
    float w = (d00 * d1p - d01 * d0p) / denom;
    float u = 1.0f - v - w;

    // 给点一些边距容差（点可能略超边界）
    float pad = tolerance * 0.5f;
    bary[0] = u; bary[1] = v; bary[2] = w;
    return (u >= -pad && v >= -pad && w >= -pad);
}

// --------------------------------------------------------------------------------------------
int EdgeAdjacency::FindTriangle(const float p[3], float tolerance) const
{
    const int triCount = TriangleCount();
    float bary[3];
    for (int t = 0; t < triCount; ++t)
    {
        if (PointOnTriangle(t, p, tolerance, bary))
            return t;
    }
    return -1;
}

// --------------------------------------------------------------------------------------------
void EdgeAdjacency::GetFaceNormal(int triIndex, float outNormal[3]) const
{
    outNormal[0] = _faceNormals[(size_t)triIndex*3+0];
    outNormal[1] = _faceNormals[(size_t)triIndex*3+1];
    outNormal[2] = _faceNormals[(size_t)triIndex*3+2];
}

// --------------------------------------------------------------------------------------------
int EdgeAdjacency::GetAdjacentTriangle(int triIndex, int edgeLocalIndex) const
{
    if (triIndex < 0) return -1;
    return _adjacency[(size_t)triIndex*3 + edgeLocalIndex];
}

// --------------------------------------------------------------------------------------------
float EdgeAdjacency::DihedralAngle(const float n0[3], const float n1[3])
{
    V3 a = vnorm(load3(n0));
    V3 b = vnorm(load3(n1));
    float c = vdot(a, b);
    if (c > 1.0f) c = 1.0f;
    if (c < -1.0f) c = -1.0f;
    return std::acos(c);
}

// --------------------------------------------------------------------------------------------
void EdgeAdjacency::GetTriangleVertices(int triIndex, float outV0[3], float outV1[3], float outV2[3]) const
{
    uint32_t i0 = _indices[triIndex*3+0];
    uint32_t i1 = _indices[triIndex*3+1];
    uint32_t i2 = _indices[triIndex*3+2];
    store3(load3(&_vertices[(size_t)i0*3]), outV0);
    store3(load3(&_vertices[(size_t)i1*3]), outV1);
    store3(load3(&_vertices[(size_t)i2*3]), outV2);
}

} // namespace yama
