// ============================================================================================
//  SmartEdgeFilter.h — 自研创新：按角度阈值过滤三角网格内部边鬼影碰撞。
//
//  痛点：Jolt 的 EnhancedInternalEdgeRemoval 仅开/关；三角面化地板上滑动会"磕边"。
//  本过滤器作为 PhysicsSystem 的 ContactListener 安装，在 OnContactValidate 中：
//    1. 判定接触双方是否有一方是已注册的网格体（mesh body）。
//    2. 用接触点定位三角面（EdgeAdjacency::FindTriangle）。
//    3. 若接触法线偏离三角面法线超过容差（疑似边接触），查该三角面 3 条边的邻接三角面。
//    4. 若接触点接近一条"近共面边"（邻接三角面二面角 < body 的阈值），视为鬼影碰撞 → 拒绝。
//    5. 真实几何边（邻接三角面非共面）正常接受。
//
//  本过滤器同时作为用户回调的转发层：用户通过 SJ_PhysicsSystem_SetContactListener 安装的
//  added/removed/validate/persisted 回调由本类在完成自身过滤后转发。
//
//  比 Jolt 二值 EnhancedInternalEdgeRemoval 更细粒度：可按 body 调阈值，且只压近共面边。
// ============================================================================================
#ifndef SHANYU_SMART_EDGE_FILTER_H
#define SHANYU_SMART_EDGE_FILTER_H

#include <Jolt/Jolt.h>
#include <Jolt/Physics/Collision/ContactListener.h>
#include <Jolt/Physics/Body/BodyID.h>

#include <unordered_map>
#include <memory>
#include <mutex>
#include <atomic>

#include "EdgeAdjacency.h"

namespace shanyu {

class SmartEdgeFilter final : public JPH::ContactListener
{
public:
    SmartEdgeFilter();

    // ---- 网格注册（由 SJ_Shape_Finalize 在 finalize mesh 时调用）----
    // finalizedShape: Jolt 不可变 Shape 指针（Body::GetShape() 返回的就是它）。
    void RegisterMesh(const JPH::Shape* finalizedShape, std::shared_ptr<EdgeAdjacency> adj);
    void UnregisterMesh(const JPH::Shape* finalizedShape);
    bool IsMesh(const JPH::Shape* shape) const;
    EdgeAdjacency* GetAdjacency(const JPH::Shape* shape) const;

    // ---- 每 body 阈值 ----
    // angleThresholdRadians: 邻接三角面二面角低于此值视为近共面边。0 = 该 body 不过滤。
    void SetBodyThreshold(JPH::BodyID bodyId, float angleThresholdRadians);
    float GetBodyThreshold(JPH::BodyID bodyId) const;
    void ClearBody(JPH::BodyID bodyId);

    // ---- 总开关 ----
    void SetEnabled(bool enabled) { _enabled.store(enabled, std::memory_order_relaxed); }
    bool IsEnabled() const { return _enabled.load(std::memory_order_relaxed); }

    // ---- 用户回调（由 SJ_PhysicsSystem_SetContactListener 设置）----
    // 这些是 cdecl 函数指针，由 C# 侧保持 delegate 存活。
    void SetUserCallbacks(void* addedCb, void* removedCb, void* validateCb, void* persistedCb);
    void ClearUserCallbacks();

    // ---- JPH::ContactListener 实现 ----
    virtual JPH::ValidateResult OnContactValidate(const JPH::Body& inBody1, const JPH::Body& inBody2,
        JPH::RVec3Arg inBaseOffset, const JPH::CollideShapeResult& inCollisionResult) override;
    virtual void OnContactAdded(const JPH::Body& inBody1, const JPH::Body& inBody2,
        const JPH::ContactManifold& inManifold, JPH::ContactSettings& ioSettings) override;
    virtual void OnContactPersisted(const JPH::Body& inBody1, const JPH::Body& inBody2,
        const JPH::ContactManifold& inManifold, JPH::ContactSettings& ioSettings) override;
    virtual void OnContactRemoved(const JPH::SubShapeIDPair& inSubShapeIDPair) override;

private:
    // 判定接触是否为鬼影边碰撞。返回 true 表示应拒绝。
    // meshBody: 网格体；otherBody: 另一侧（通常动态）。
    bool IsGhostEdgeContact(const JPH::Body& meshBody, const JPH::Body& otherBody,
        JPH::RVec3Arg inBaseOffset, const JPH::CollideShapeResult& inCollisionResult,
        float bodyThreshold) const;

    // 把 JPH::ContactManifold 转 SJ_ContactManifold 并调用用户 added/persisted 回调
    void DispatchAdded(const JPH::Body& b1, const JPH::Body& b2, const JPH::ContactManifold& m) const;
    void DispatchPersisted(const JPH::Body& b1, const JPH::Body& b2, const JPH::ContactManifold& m) const;

    mutable std::mutex _mutex;
    std::unordered_map<const JPH::Shape*, std::shared_ptr<EdgeAdjacency>> _meshAdjacency;
    std::unordered_map<uint32_t, float> _bodyThresholds; // key = BodyID.GetIndexAndSequenceNumber()
    std::atomic<bool> _enabled{true};

    // 用户回调（cdecl 函数指针）
    void* _userAddedCb     = nullptr;
    void* _userRemovedCb   = nullptr;
    void* _userValidateCb  = nullptr;
    void* _userPersistedCb = nullptr;

    // 过滤参数
    static constexpr float kFaceNormalTolerance = 0.087f; // ~5°，接触法线偏离面法线超过此值视为疑似边接触
    static constexpr float kEdgeProximityTol    = 0.01f;  // 接触点到边的距离容差（米）
    static constexpr float kPointOnTriTolerance = 0.02f;  // 点在三角面上的平面距离容差
};

} // namespace shanyu

#endif // SHANYU_SMART_EDGE_FILTER_H
