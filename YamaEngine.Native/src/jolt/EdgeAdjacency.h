// ============================================================================================
//  EdgeAdjacency.h — 网格边邻接表，供 SmartEdgeFilter 使用。
//
//  作用：在 SJ_Shape_Mesh_Create 时构建一次，存储顶点/索引/每三角面法线/边→邻接三角面映射。
//  SmartEdgeFilter 在 OnContactValidate 时用接触点定位三角面，查共享边的邻接三角面，
//  算二面角，判断是否为"近共面内部边"（鬼影碰撞来源）。
//
//  设计取舍：不依赖 Jolt 内部 SubShapeId 编码（跨版本不稳定），改用接触点几何定位三角面。
//  当前用暴力点在三角形内测试（O(三角面数)）。对几千三角面的地板足够；超大规模网格
//  可后续加 BVH（见 TODO）。
// ============================================================================================
#ifndef SHANYU_EDGE_ADJACENCY_H
#define SHANYU_EDGE_ADJACENCY_H

#include <cstdint>
#include <vector>
#include <unordered_map>

namespace yama {

class EdgeAdjacency {
public:
    // 构建邻接。vertices: xyz 交错, vertexCount 个顶点; indices: 三角形列表索引, indexCount 个。
    // 调用后内部存储顶点/索引副本、面法线、边邻接表。
    void Build(const float* vertices, int vertexCount,
               const uint32_t* indices, int indexCount);

    bool IsValid() const { return !_faceNormals.empty(); }
    int TriangleCount() const { return static_cast<int>(_indices.size()) / 3; }

    // 用世界→网格局部空间的接触点定位包含它的三角面。返回三角面索引或 -1。
    // tolerance: 点到三角形平面的距离容差（用于浮点误差）。返回的三角面满足点在平面内 + 重心坐标全在 [0,1]。
    int FindTriangle(const float p[3], float tolerance) const;

    // 取三角面的局部空间单位法线（3 个 float 写入 outNormal）。
    void GetFaceNormal(int triIndex, float outNormal[3]) const;

    // 取三角面 triIndex 的第 edgeLocalIndex(0..2) 条边共享的邻接三角面索引。
    // 无邻接返回 -1（边界边）。
    int GetAdjacentTriangle(int triIndex, int edgeLocalIndex) const;

    // 计算两三角面法线的二面角（弧度，0=共面）。两法线都应是单位向量。
    static float DihedralAngle(const float n0[3], const float n1[3]);

    // 取三角面三个顶点（局部空间），写入 outV0/outV1/outV2（各 3 float）。
    void GetTriangleVertices(int triIndex, float outV0[3], float outV1[3], float outV2[3]) const;

    // 判断点 p 是否在三角面 triIndex 上（重心坐标法）。bary 输出 u,v,w。
    bool PointOnTriangle(int triIndex, const float p[3], float tolerance, float bary[3]) const;

private:
    std::vector<float>    _vertices;     // xyz 交错
    std::vector<uint32_t> _indices;      // 三角形列表
    std::vector<float>    _faceNormals;  // 每三角面单位法线 xyz
    // 边键 = (minVertIdx << 32) | maxVertIdx；值为最多 2 个三角面索引。
    // 每个三角面的 3 条边的邻接存于 _adjacency[triIndex*3 + edgeLocalIndex]。
    std::vector<int>      _adjacency;
    std::unordered_map<uint64_t, std::pair<int,int>> _edgeMap;

    static uint64_t MakeEdgeKey(uint32_t a, uint32_t b) {
        uint32_t lo = a < b ? a : b;
        uint32_t hi = a < b ? b : a;
        return (static_cast<uint64_t>(hi) << 32) | lo;
    }
};

} // namespace yama

#endif // SHANYU_EDGE_ADJACENCY_H
