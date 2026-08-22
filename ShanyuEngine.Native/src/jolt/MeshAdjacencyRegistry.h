// ============================================================================================
//  MeshAdjacencyRegistry.h — 全局 mesh 边邻接注册表声明。
//
//  SJ_Shape_Finalize 在 finalize mesh shape 时注册 EdgeAdjacency；所有 PhysicsSystem 的
//  SmartEdgeFilter 共享查询（mesh shape 可跨系统复用）。实现在 ShanyuJolt.cpp。
// ============================================================================================
#ifndef SHANYU_MESH_ADJACENCY_REGISTRY_H
#define SHANYU_MESH_ADJACENCY_REGISTRY_H

#include <memory>

namespace JPH { class Shape; }
namespace shanyu { class EdgeAdjacency; }

namespace shanyu {

// 注册一个 finalized mesh shape 的边邻接表。重复注册会覆盖。
void RegisterMeshAdjacencyGlobal(const JPH::Shape* shape, std::shared_ptr<EdgeAdjacency> adj);

// 查询。未注册返回 nullptr。
EdgeAdjacency* GetMeshAdjacencyGlobal(const JPH::Shape* shape);

// 注销。
void UnregisterMeshAdjacencyGlobal(const JPH::Shape* shape);

} // namespace shanyu

#endif // SHANYU_MESH_ADJACENCY_REGISTRY_H
