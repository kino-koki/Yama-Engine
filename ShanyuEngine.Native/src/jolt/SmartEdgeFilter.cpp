// ============================================================================================
//  SmartEdgeFilter.cpp — 见 SmartEdgeFilter.h
// ============================================================================================
#include "SmartEdgeFilter.h"

#include <Jolt/Jolt.h>
#include <Jolt/Physics/Body/Body.h>
#include <Jolt/Physics/Collision/CollideShapeResult.h>
#include <Jolt/Physics/Collision/ContactListener.h>
#include <Jolt/Physics/Collision/Shape/Shape.h>
#include <Jolt/Geometry/AABox.h>

#include <cmath>
#include <algorithm>
#include <shared_mutex>

#include "ShanyuJolt.h"  // SJ_ContactManifold 等 ABI 类型 + 用户回调 typedef
#include "MeshAdjacencyRegistry.h"

namespace shanyu {

// --------------------------------------------------------------------------------------------
//  小向量工具（与 EdgeAdjacency.cpp 同款，保持本文件独立）
// --------------------------------------------------------------------------------------------
namespace {
struct V3 { float x, y, z; };
inline V3 vsub(const V3& a, const V3& b) { return { a.x - b.x, a.y - b.y, a.z - b.z }; }
inline V3 vadd(const V3& a, const V3& b) { return { a.x + b.x, a.y + b.y, a.z + b.z }; }
inline V3 vmul(const V3& a, float s) { return { a.x * s, a.y * s, a.z * s }; }
inline float vdot(const V3& a, const V3& b) { return a.x*b.x + a.y*b.y + a.z*b.z; }
inline V3 vcross(const V3& a, const V3& b) { return { a.y*b.z - a.z*b.y, a.z*b.x - a.x*b.z, a.x*b.y - a.y*b.x }; }
inline float vlen(const V3& a) { return std::sqrt(vdot(a, a)); }
inline V3 vnorm(const V3& a) { float l = vlen(a); return l > 1e-12f ? vmul(a, 1.0f/l) : V3{0,0,0}; }

// 点 P 到线段 AB 的最近距离
float pointSegDist(const V3& p, const V3& a, const V3& b) {
    V3 ab = vsub(b, a);
    float t = vdot(vsub(p, a), ab) / std::max(1e-12f, vdot(ab, ab));
    t = std::clamp(t, 0.0f, 1.0f);
    V3 closest = vadd(a, vmul(ab, t));
    return vlen(vsub(p, closest));
}
} // namespace

// --------------------------------------------------------------------------------------------
SmartEdgeFilter::SmartEdgeFilter() = default;

// --------------------------------------------------------------------------------------------
void SmartEdgeFilter::RegisterMesh(const JPH::Shape* finalizedShape, std::shared_ptr<EdgeAdjacency> adj)
{
    if (!finalizedShape || !adj || !adj->IsValid()) return;
    RegisterMeshAdjacencyGlobal(finalizedShape, std::move(adj));
}

void SmartEdgeFilter::UnregisterMesh(const JPH::Shape* finalizedShape)
{
    UnregisterMeshAdjacencyGlobal(finalizedShape);
}

bool SmartEdgeFilter::IsMesh(const JPH::Shape* shape) const
{
    return GetMeshAdjacencyGlobal(shape) != nullptr;
}

EdgeAdjacency* SmartEdgeFilter::GetAdjacency(const JPH::Shape* shape) const
{
    return GetMeshAdjacencyGlobal(shape);
}

// --------------------------------------------------------------------------------------------
void SmartEdgeFilter::SetBodyThreshold(JPH::BodyID bodyId, float angleThresholdRadians)
{
    std::lock_guard<std::mutex> lk(_mutex);
    if (angleThresholdRadians <= 0.0f)
        _bodyThresholds.erase(bodyId.GetIndexAndSequenceNumber());
    else
        _bodyThresholds[bodyId.GetIndexAndSequenceNumber()] = angleThresholdRadians;
}

float SmartEdgeFilter::GetBodyThreshold(JPH::BodyID bodyId) const
{
    std::lock_guard<std::mutex> lk(_mutex);
    auto it = _bodyThresholds.find(bodyId.GetIndexAndSequenceNumber());
    return it == _bodyThresholds.end() ? 0.0f : it->second;
}

void SmartEdgeFilter::ClearBody(JPH::BodyID bodyId)
{
    std::lock_guard<std::mutex> lk(_mutex);
    _bodyThresholds.erase(bodyId.GetIndexAndSequenceNumber());
}

// --------------------------------------------------------------------------------------------
void SmartEdgeFilter::SetUserCallbacks(void* addedCb, void* removedCb, void* validateCb, void* persistedCb)
{
    std::lock_guard<std::mutex> lk(_mutex);
    _userAddedCb = addedCb; _userRemovedCb = removedCb;
    _userValidateCb = validateCb; _userPersistedCb = persistedCb;
}

void SmartEdgeFilter::ClearUserCallbacks()
{
    std::lock_guard<std::mutex> lk(_mutex);
    _userAddedCb = _userRemovedCb = _userValidateCb = _userPersistedCb = nullptr;
}

// --------------------------------------------------------------------------------------------
//  核心鬼影边检测
// --------------------------------------------------------------------------------------------
bool SmartEdgeFilter::IsGhostEdgeContact(const JPH::Body& meshBody, const JPH::Body& otherBody,
    JPH::RVec3Arg inBaseOffset, const JPH::CollideShapeResult& inCollisionResult,
    float bodyThreshold) const
{
    // meshBody 一定是网格体（调用方保证）。接触点取 mesh 侧的接触点。
    // mContactPointOn1 相对 inBaseOffset。若 meshBody 是 body1 用 On1，否则用 On2。
    // 调用方传入时已确定 meshBody 是哪一侧；这里通过比较指针判定。
    bool meshIsBody1 = (&meshBody == &inCollisionResult ? false : false); // 占位，实际由调用方传入接触点
    // 调用方会保证传入正确的接触点；这里我们两个都试，取在网格上的那个。
    JPH::Vec3 cpRel = inCollisionResult.mContactPointOn1; // 相对 baseOffset
    // 世界接触点（double）
    JPH::DVec3 worldPointD = inBaseOffset + JPH::DVec3(cpRel);
    JPH::Vec3 worldPoint = JPH::Vec3(worldPointD);

    // 转 mesh 局部空间
    JPH::RMat44 invMesh = meshBody.GetWorldTransform().InversedRotationTranslation();
    JPH::Vec3 localPoint = invMesh * worldPoint;
    float p[3] = { localPoint.GetX(), localPoint.GetY(), localPoint.GetZ() };

    EdgeAdjacency* adj = GetAdjacency(meshBody.GetShape());
    if (!adj) return false;

    int tri = adj->FindTriangle(p, kPointOnTriTolerance);
    if (tri < 0) return false;

    // 面法线（局部）→ 世界
    float fnLocal[3];
    adj->GetFaceNormal(tri, fnLocal);
    JPH::Vec3 faceNormalWorld = meshBody.GetRotation() * JPH::Vec3(fnLocal[0], fnLocal[1], fnLocal[2]);
    V3 faceN = { faceNormalWorld.GetX(), faceNormalWorld.GetY(), faceNormalWorld.GetZ() };

    // 接触法线 = mPenetrationAxis（从 body1 指向 body2）
    JPH::Vec3 penAxis = inCollisionResult.mPenetrationAxis.Normalized();
    V3 contactN = { penAxis.GetX(), penAxis.GetY(), penAxis.GetZ() };

    // 接触法线与面法线的夹角。注意方向：若 meshBody 是 body2，penetrationAxis 从 1 指向 2（即指向 mesh），
    // 则支持法线应取 -contactN。统一成"指向 other body 的法线"再比。
    // 简化：取 |contactN · faceN| 的角度（不区分方向，因为面法线可能朝外或朝内）。
    float d = vdot(vnorm(contactN), vnorm(faceN));
    if (d > 1.0f) d = 1.0f; if (d < -1.0f) d = -1.0f;
    float deviation = std::acos(std::fabs(d)); // 0 = 平行，pi/2 = 垂直

    // 若接触法线与面法线基本平行（< 容差），这是正常的面接触，不是鬼影。
    if (deviation < kFaceNormalTolerance) return false;

    // 疑似边接触：检查接触点是否接近某条"近共面边"
    float v0[3], v1[3], v2[3];
    adj->GetTriangleVertices(tri, v0, v1, v2);
    V3 P = { p[0], p[1], p[2] };
    V3 tv[3] = { {v0[0],v0[1],v0[2]}, {v1[0],v1[1],v1[2]}, {v2[0],v2[1],v2[2]} };

    for (int e = 0; e < 3; ++e)
    {
        int adjTri = adj->GetAdjacentTriangle(tri, e);
        if (adjTri < 0) continue; // 边界边，无邻接，不视为鬼影

        float an[3];
        adj->GetFaceNormal(adjTri, an);
        float dihedral = EdgeAdjacency::DihedralAngle(fnLocal, an);

        // 邻接三角面近共面 → 这条边是内部鬼影边候选
        if (dihedral < bodyThreshold)
        {
            // 接触点是否接近这条边
            const V3& ea = tv[e];
            const V3& eb = tv[(e + 1) % 3];
            float dist = pointSegDist(P, ea, eb);
            if (dist < kEdgeProximityTol + kPointOnTriTolerance)
            {
                // 鬼影边接触 → 拒绝
                return true;
            }
        }
    }
    return false;
}

// --------------------------------------------------------------------------------------------
JPH::ValidateResult SmartEdgeFilter::OnContactValidate(const JPH::Body& inBody1, const JPH::Body& inBody2,
    JPH::RVec3Arg inBaseOffset, const JPH::CollideShapeResult& inCollisionResult)
{
    if (IsEnabled())
    {
        // 判定哪一侧是网格体
        const JPH::Shape* s1 = inBody1.GetShape();
        const JPH::Shape* s2 = inBody2.GetShape();
        bool m1 = IsMesh(s1);
        bool m2 = IsMesh(s2);

        if (m1 != m2) // 恰好一侧是网格
        {
            const JPH::Body& meshBody = m1 ? inBody1 : inBody2;
            const JPH::Body& otherBody = m1 ? inBody2 : inBody1;
            float thr = GetBodyThreshold(otherBody.GetID());
            if (thr > 0.0f)
            {
                if (IsGhostEdgeContact(meshBody, otherBody, inBaseOffset, inCollisionResult, thr))
                    return JPH::ValidateResult::RejectContact;
            }
        }
    }

    // 转发给用户 validate 回调
    void* uv = nullptr;
    {
        std::lock_guard<std::mutex> lk(_mutex);
        uv = _userValidateCb;
    }
    if (uv)
    {
        using Cb = int32_t(SJ_CALL*)(uint32_t, uint32_t);
        auto cb = reinterpret_cast<Cb>(uv);
        int32_t r = cb(inBody1.GetID().GetIndexAndSequenceNumber(),
                       inBody2.GetID().GetIndexAndSequenceNumber());
        // SJ_ValidateResult → JPH::ValidateResult
        switch (r)
        {
            case 0: return JPH::ValidateResult::RejectAllContacts;
            case 1: return JPH::ValidateResult::RejectContact;
            case 2: return JPH::ValidateResult::AcceptAllContactsForThisBodyPair;
            default: return JPH::ValidateResult::AcceptContact;
        }
    }
    return JPH::ValidateResult::AcceptContact;
}

// --------------------------------------------------------------------------------------------
void SmartEdgeFilter::DispatchAdded(const JPH::Body& b1, const JPH::Body& b2, const JPH::ContactManifold& m) const
{
    void* cb;
    {
        std::lock_guard<std::mutex> lk(_mutex);
        cb = _userAddedCb;
    }
    if (!cb) return;

    SJ_ContactManifold out{};
    out.BodyIdA = b1.GetID().GetIndexAndSequenceNumber();
    out.BodyIdB = b2.GetID().GetIndexAndSequenceNumber();
    out.SubShapeIdA = static_cast<uint16_t>(m.mSubShapeID1.GetValue() & 0xFFFF);
    out.SubShapeIdB = static_cast<uint16_t>(m.mSubShapeID2.GetValue() & 0xFFFF);
    out.AverageNormal = { m.mWorldSpaceNormal.GetX(), m.mWorldSpaceNormal.GetY(), m.mWorldSpaceNormal.GetZ() };
    // C# 约定：法线从 B 指向 A。Jolt mWorldSpaceNormal 从 1 指向 2，即 A→B，故取反。
    out.AverageNormal.X = -out.AverageNormal.X;
    out.AverageNormal.Y = -out.AverageNormal.Y;
    out.AverageNormal.Z = -out.AverageNormal.Z;
    // 接触点取第一个接触点（相对 baseOffset → 世界）
    if (!m.mRelativeContactPointsOn1.empty())
    {
        JPH::Vec3 rp = m.mRelativeContactPointsOn1[0];
        out.AverageContactPoint = { rp.GetX(), rp.GetY(), rp.GetZ() };
    }
    out.PenetrationDepth = m.mPenetrationDepth;
    out.ContactPointCount = static_cast<int>(m.mRelativeContactPointsOn1.size());

    using Cb = void(SJ_CALL*)(SJ_ContactManifold);
    reinterpret_cast<Cb>(cb)(out);
}

void SmartEdgeFilter::DispatchPersisted(const JPH::Body& b1, const JPH::Body& b2, const JPH::ContactManifold& m) const
{
    void* cb;
    {
        std::lock_guard<std::mutex> lk(_mutex);
        cb = _userPersistedCb;
    }
    if (!cb) return;

    SJ_ContactManifold out{};
    out.BodyIdA = b1.GetID().GetIndexAndSequenceNumber();
    out.BodyIdB = b2.GetID().GetIndexAndSequenceNumber();
    out.SubShapeIdA = static_cast<uint16_t>(m.mSubShapeID1.GetValue() & 0xFFFF);
    out.SubShapeIdB = static_cast<uint16_t>(m.mSubShapeID2.GetValue() & 0xFFFF);
    out.AverageNormal = { -m.mWorldSpaceNormal.GetX(), -m.mWorldSpaceNormal.GetY(), -m.mWorldSpaceNormal.GetZ() };
    if (!m.mRelativeContactPointsOn1.empty())
    {
        JPH::Vec3 rp = m.mRelativeContactPointsOn1[0];
        out.AverageContactPoint = { rp.GetX(), rp.GetY(), rp.GetZ() };
    }
    out.PenetrationDepth = m.mPenetrationDepth;
    out.ContactPointCount = static_cast<int>(m.mRelativeContactPointsOn1.size());

    using Cb = void(SJ_CALL*)(SJ_ContactManifold);
    reinterpret_cast<Cb>(cb)(out);
}

// --------------------------------------------------------------------------------------------
void SmartEdgeFilter::OnContactAdded(const JPH::Body& inBody1, const JPH::Body& inBody2,
    const JPH::ContactManifold& inManifold, JPH::ContactSettings& ioSettings)
{
    (void)ioSettings;
    DispatchAdded(inBody1, inBody2, inManifold);
}

void SmartEdgeFilter::OnContactPersisted(const JPH::Body& inBody1, const JPH::Body& inBody2,
    const JPH::ContactManifold& inManifold, JPH::ContactSettings& ioSettings)
{
    (void)ioSettings;
    DispatchPersisted(inBody1, inBody2, inManifold);
}

void SmartEdgeFilter::OnContactRemoved(const JPH::SubShapeIDPair& inSubShapeIDPair)
{
    void* cb;
    {
        std::lock_guard<std::mutex> lk(_mutex);
        cb = _userRemovedCb;
    }
    if (!cb) return;

    using Cb = void(SJ_CALL*)(uint32_t, uint32_t);
    auto c = reinterpret_cast<Cb>(cb);
    c(inSubShapeIDPair.GetBody1ID().GetIndexAndSequenceNumber(),
      inSubShapeIDPair.GetBody2ID().GetIndexAndSequenceNumber());
}

} // namespace shanyu
