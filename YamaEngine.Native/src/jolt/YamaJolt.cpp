// ============================================================================================
//  YamaJolt.cpp — Native C ABI wrapper around Jolt Physics.
//
//  Implements every SJ_* function declared in YamaJolt.h (and consumed by the C# P/Invoke
//  layer JoltPhysicsBridge.cs). Owns Jolt object lifetimes; managed code pairs every Create
//  with the matching Destroy.
//
//  内部结构：
//    - SJ_PhysicsSystem 持有 JPH::PhysicsSystem + TempAllocator + JobSystem + SmartEdgeFilter
//    - SJ_ShapeSettings 持有 JPH::ShapeSettings（可变配置）+ 可能的 EdgeAdjacency（mesh）
//    - SJ_Shape 持有不可变 JPH::Shape（引用计数，Release 释放）
//    - SJ_Character 持有 JPH::CharacterVirtual
//
//  碰撞过滤模型：ObjectLayer = 层索引(0..15)；每体 CollisionGroup(GroupID=层索引, SubGroupID=mask)；
//  MaskGroupFilter 按 (a 在 b 的 mask 中) && (b 在 a 的 mask 中) 判定。查询用 QueryLayerFilter。
// ============================================================================================
#include "yama/jolt/YamaJolt.h"

#include <Jolt/Jolt.h>
#include <Jolt/RegisterTypes.h>
#include <Jolt/Core/Factory.h>
#include <Jolt/Core/TempAllocator.h>
#include <Jolt/Core/JobSystemThreadPool.h>
#include <Jolt/Physics/PhysicsSystem.h>
#include <Jolt/Physics/Body/Body.h>
#include <Jolt/Physics/Body/BodyCreationSettings.h>
#include <Jolt/Physics/Body/BodyInterface.h>
#include <Jolt/Physics/Body/BodyLock.h>
#include <Jolt/Physics/Body/BodyLockInterface.h>
#include <Jolt/Physics/Collision/Shape/Shape.h>
#include <Jolt/Physics/Collision/Shape/BoxShape.h>
#include <Jolt/Physics/Collision/Shape/SphereShape.h>
#include <Jolt/Physics/Collision/Shape/CapsuleShape.h>
#include <Jolt/Physics/Collision/Shape/CylinderShape.h>
#include <Jolt/Physics/Collision/Shape/PlaneShape.h>
#include <Jolt/Physics/Collision/Shape/MeshShape.h>
#include <Jolt/Physics/Collision/Shape/StaticCompoundShape.h>
#include <Jolt/Physics/Collision/Shape/CompoundShape.h>
#include <Jolt/Physics/Collision/GroupFilter.h>
#include <Jolt/Physics/Collision/CollisionCollector.h>
#include <Jolt/Physics/Collision/RayCast.h>
#include <Jolt/Physics/Collision/CastResult.h>
#include <Jolt/Physics/Collision/ShapeCast.h>
#include <Jolt/Physics/Collision/TransformedShape.h>
#include <Jolt/Physics/Constraints/FixedConstraint.h>
#include <Jolt/Physics/Constraints/PointConstraint.h>
#include <Jolt/Physics/Constraints/HingeConstraint.h>
#include <Jolt/Physics/Constraints/SliderConstraint.h>
#include <Jolt/Physics/Constraints/DistanceConstraint.h>
#include <Jolt/Physics/Character/CharacterVirtual.h>
#include <Jolt/Geometry/OrientedBox.h>
#include <Jolt/Math/Vec3.h>
#include <Jolt/Math/Quat.h>

#include <memory>
#include <mutex>
#include <unordered_map>
#include <vector>
#include <cstring>
#include <cmath>

#include "EdgeAdjacency.h"
#include "SmartEdgeFilter.h"
#include "MeshAdjacencyRegistry.h"

// Jolt 单例抑制（避免重复定义）
JPH_SUPPRESS_WARNINGS

// --------------------------------------------------------------------------------------------
//  全局 mesh 邻接注册表实现（声明见 MeshAdjacencyRegistry.h）。
//  finalize 时注册，destroy 时注销。所有 SmartEdgeFilter 共享查询。
// --------------------------------------------------------------------------------------------
namespace yama {
namespace {
std::mutex g_meshRegMutex;
std::unordered_map<const JPH::Shape*, std::shared_ptr<EdgeAdjacency>> g_meshRegistry;
} // namespace

void RegisterMeshAdjacencyGlobal(const JPH::Shape* s, std::shared_ptr<EdgeAdjacency> adj)
{
    std::lock_guard<std::mutex> lk(g_meshRegMutex);
    g_meshRegistry[s] = std::move(adj);
}

EdgeAdjacency* GetMeshAdjacencyGlobal(const JPH::Shape* s)
{
    std::lock_guard<std::mutex> lk(g_meshRegMutex);
    auto it = g_meshRegistry.find(s);
    return it == g_meshRegistry.end() ? nullptr : it->second.get();
}

void UnregisterMeshAdjacencyGlobal(const JPH::Shape* s)
{
    std::lock_guard<std::mutex> lk(g_meshRegMutex);
    g_meshRegistry.erase(s);
}
} // namespace yama

namespace {

// --------------------------------------------------------------------------------------------
//  全局初始化引用计数
// --------------------------------------------------------------------------------------------
std::mutex g_initMutex;
int g_initCount = 0;
JPH::Factory* g_factory = nullptr;

// --------------------------------------------------------------------------------------------
//  转换辅助
// --------------------------------------------------------------------------------------------
inline JPH::Vec3 ToJPH(const SJ_Vec3& v) { return JPH::Vec3(v.X, v.Y, v.Z); }
inline SJ_Vec3 FromJPH(const JPH::Vec3& v) { return { v.GetX(), v.GetY(), v.GetZ() }; }
inline JPH::Quat ToJPHQ(const SJ_Quat& q) { return JPH::Quat(q.X, q.Y, q.Z, q.W); }
inline SJ_Quat FromJPHQ(const JPH::Quat& q) { return { q.GetX(), q.GetY(), q.GetZ(), q.GetW() }; }

// C# layer 是位掩码(1<<n)；转成层索引 n。
inline uint16_t LayerBitToIndex(uint16_t layerBit)
{
    if (layerBit == 0) return 0;
    unsigned long idx;
    _BitScanForward(&idx, layerBit);
    return (uint16_t)idx;
}

// --------------------------------------------------------------------------------------------
//  碰撞过滤：层接口 / GroupFilter / 查询过滤器
// --------------------------------------------------------------------------------------------
class BPLayerInterface final : public JPH::BroadPhaseLayerInterface
{
public:
    virtual JPH::uint GetNumBroadPhaseLayers() const override { return 1; }
    virtual JPH::BroadPhaseLayer GetBroadPhaseLayer(JPH::ObjectLayer inLayer) const override
    {
        return JPH::BroadPhaseLayer(0);
    }
#if defined(JPH_EXTERNAL_PROFILE) || defined(JPH_PROFILE_ENABLED)
    virtual const char* GetBroadPhaseLayerName(JPH::BroadPhaseLayer inLayer) const override { return "Default"; }
#endif
};

// 全部允许；精确过滤由 GroupFilter 完成。
class AllCollideLayerPairFilter final : public JPH::ObjectLayerPairFilter
{
public:
    virtual bool ShouldCollide(JPH::ObjectLayer, JPH::ObjectLayer) const override { return true; }
};

class AllCollideObjVsBpFilter final : public JPH::ObjectVsBroadPhaseLayerFilter
{
public:
    virtual bool ShouldCollide(JPH::ObjectLayer, JPH::BroadPhaseLayer) const override { return true; }
};

// 按位掩码精确判定。GroupID=层索引, SubGroupID=该体的碰撞掩码。
class MaskGroupFilter final : public JPH::GroupFilter
{
public:
    virtual bool CanCollide(const JPH::CollisionGroup& a, const JPH::CollisionGroup& b) const override
    {
        uint32_t ga = a.GetGroupID();
        uint32_t ma = a.GetSubGroupID();
        uint32_t gb = b.GetGroupID();
        uint32_t mb = b.GetSubGroupID();
        // a 的层在 b 的 mask 中 且 b 的层在 a 的 mask 中
        return ((1u << ga) & mb) != 0 && ((1u << gb) & ma) != 0;
    }
};
JPH::Ref<MaskGroupFilter> g_maskFilter = new MaskGroupFilter;

// 查询用：接受 layer 索引在 queryMask 中的体。
class QueryLayerFilter final : public JPH::ObjectLayerFilter
{
public:
    QueryLayerFilter(uint16_t queryMask) : _mask(queryMask) {}
    virtual bool ShouldCollide(JPH::ObjectLayer inLayer) const override
    {
        return (_mask & (1u << inLayer)) != 0;
    }
private:
    uint16_t _mask;
};

// --------------------------------------------------------------------------------------------
//  句柄内部结构
// --------------------------------------------------------------------------------------------
struct SJ_PhysicsSystem
{
    JPH::PhysicsSystem* system = nullptr;
    JPH::TempAllocatorImpl* tempAllocator = nullptr;
    JPH::JobSystemThreadPool* jobSystem = nullptr;
    BPLayerInterface bpLayerInterface;
    AllCollideLayerPairFilter layerPairFilter;
    AllCollideObjVsBpFilter objVsBpFilter;
    std::unique_ptr<yama::SmartEdgeFilter> edgeFilter;
    ~SJ_PhysicsSystem()
    {
        delete system;
        delete tempAllocator;
        delete jobSystem;
    }
};

struct SJ_ShapeSettings
{
    JPH::Ref<JPH::ShapeSettings> settings;
    // mesh 专用：finalize 后转交给全局注册表
    std::shared_ptr<yama::EdgeAdjacency> meshAdjacency;
    // compound 子项暂存（finalize 时 AddChild 已应用，无需保存）
};

struct SJ_Shape
{
    JPH::Ref<const JPH::Shape> shape;
};

struct SJ_Character
{
    JPH::Ref<JPH::CharacterVirtual> character;
    // CharacterVirtual 更新需要的临时缓冲
    JPH::TempAllocatorImpl* tempAlloc = nullptr;
};

// --------------------------------------------------------------------------------------------
//  约束句柄：用 BodyID+约束类型 在系统中查找。Jolt 约束是 TwoBodyConstraint*。
//  我们在系统上维护一个 constraintId → Constraint* 表。
// --------------------------------------------------------------------------------------------
struct ConstraintEntry { JPH::Ref<JPH::Constraint> constraint; };

std::mutex g_constraintMutex;
uint32_t g_nextConstraintId = 1;

// 每系统的约束表
std::unordered_map<SJ_PhysicsSystem*, std::unordered_map<uint32_t, ConstraintEntry>> g_constraints;
std::mutex g_constraintsMapMutex;

uint32_t RegisterConstraint(SJ_PhysicsSystem* sys, JPH::Constraint* c)
{
    uint32_t id;
    {
        std::lock_guard<std::mutex> lk(g_constraintMutex);
        id = g_nextConstraintId++;
    }
    std::lock_guard<std::mutex> lk(g_constraintsMapMutex);
    g_constraints[sys][id] = { c };
    return id;
}

JPH::Constraint* LookupConstraint(SJ_PhysicsSystem* sys, uint32_t id)
{
    std::lock_guard<std::mutex> lk(g_constraintsMapMutex);
    auto it = g_constraints.find(sys);
    if (it == g_constraints.end()) return nullptr;
    auto jt = it->second.find(id);
    return jt == it->second.end() ? nullptr : jt->second.constraint.GetPtr();
}

void UnregisterConstraint(SJ_PhysicsSystem* sys, uint32_t id)
{
    std::lock_guard<std::mutex> lk(g_constraintsMapMutex);
    auto it = g_constraints.find(sys);
    if (it != g_constraints.end()) it->second.erase(id);
}

// --------------------------------------------------------------------------------------------
//  BodyInterface 取得（自动加锁版）
// --------------------------------------------------------------------------------------------
inline JPH::BodyInterface& GetBodyInterface(SJ_PhysicsSystem* sys)
{
    return sys->system->GetBodyInterface();
}
inline const JPH::BodyInterface& GetBodyInterfaceNoLock(SJ_PhysicsSystem* sys)
{
    return sys->system->GetBodyInterface().GetNonLockingInterface();
}

// BodyID 从 C# uint 重建
inline JPH::BodyID ToBodyID(uint32_t id) { return JPH::BodyID(id); }

} // namespace

// ============================================================================================
//  库生命周期
// ============================================================================================
extern "C" SJ_API uint8_t SJ_CALL SJ_Init(int32_t maxJobs, int32_t maxBarriers, uint32_t tempAllocatorSize)
{
    std::lock_guard<std::mutex> lk(g_initMutex);
    if (g_initCount == 0)
    {
        JPH::RegisterDefaultAllocator();
        g_factory = new JPH::Factory();
        JPH::Factory::sInstance = g_factory;
        JPH::RegisterTypes();
    }
    g_initCount++;
    (void)maxJobs; (void)maxBarriers; (void)tempAllocatorSize;
    return 1;
}

extern "C" SJ_API void SJ_CALL SJ_Shutdown()
{
    std::lock_guard<std::mutex> lk(g_initMutex);
    if (g_initCount > 0)
    {
        g_initCount--;
        if (g_initCount == 0)
        {
            JPH::UnregisterTypes();
            if (g_factory) { delete g_factory; g_factory = nullptr; JPH::Factory::sInstance = nullptr; }
        }
    }
}

extern "C" SJ_API const char* SJ_CALL SJ_GetVersion()
{
    return JPH::JPH_VERSION_STRING;
}

// ============================================================================================
//  PhysicsSystem
// ============================================================================================
extern "C" SJ_API SJ_PhysicsSystemHandle SJ_CALL SJ_PhysicsSystem_Create(const SJ_PhysicsSettings* settings)
{
    auto* sys = new SJ_PhysicsSystem();

    uint32_t tempSize = settings ? (uint32_t)settings->MaxInflightBodyPairs * 1024 + 1024 * 1024 : 16 * 1024 * 1024;
    sys->tempAllocator = new JPH::TempAllocatorImpl((int)std::max(tempSize, (uint32_t)(4 * 1024 * 1024)));

    int numJobs = std::max(2, (int)std::thread::hardware_concurrency());
    sys->jobSystem = new JPH::JobSystemThreadPool((int)std::max(numJobs, 2), (int)std::max(numJobs + 2, 4), sys->tempAllocator);

    int maxBodies = settings ? settings->MaxBodies : 1024;
    int maxBodyPairs = settings ? settings->MaxBodyPairs : 1024;
    int maxContactConstraints = settings ? settings->MaxContactConstraints : 1024;

    sys->system = new JPH::PhysicsSystem();
    sys->system->Init(maxBodies, numJobs, maxBodyPairs, maxContactConstraints,
                      sys->bpLayerInterface, sys->objVsBpFilter, sys->layerPairFilter);

    if (settings)
    {
        sys->system->SetGravity(ToJPH(settings->Gravity));
        // Jolt PhysicsSettings 微调
        JPH::PhysicsSettings ps = sys->system->GetPhysicsSettings();
        ps.mBaumgarte = settings->Baumgarte;
        ps.mSpeculativeContactDistance = settings->SpeculativeContactDistance;
        ps.mPenetrationSlop = settings->PenetrationSlop;
        ps.mLinearCastThreshold = settings->LinearCastThreshold;
        ps.mDeterministicSimulation = settings->DeterministicSimulation != 0;
        sys->system->SetPhysicsSettings(ps);
    }

    // 安装 SmartEdgeFilter 作为 ContactListener
    sys->edgeFilter = std::make_unique<yama::SmartEdgeFilter>();
    sys->system->SetContactListener(sys->edgeFilter.get());

    return reinterpret_cast<SJ_PhysicsSystemHandle>(sys);
}

extern "C" SJ_API void SJ_CALL SJ_PhysicsSystem_Destroy(SJ_PhysicsSystemHandle system)
{
    auto* sys = reinterpret_cast<SJ_PhysicsSystem*>(system);
    if (!sys) return;
    // 清理约束表
    {
        std::lock_guard<std::mutex> lk(g_constraintsMapMutex);
        g_constraints.erase(sys);
    }
    delete sys;
}

extern "C" SJ_API int32_t SJ_CALL SJ_PhysicsSystem_Step(SJ_PhysicsSystemHandle system, float deltaTime, int32_t collisionSteps)
{
    auto* sys = reinterpret_cast<SJ_PhysicsSystem*>(system);
    if (!sys || !sys->system) return 0;
    if (collisionSteps < 1) collisionSteps = 1;
    sys->system->Update(deltaTime, collisionSteps, sys->tempAllocator, sys->jobSystem);
    return collisionSteps;
}

extern "C" SJ_API void SJ_CALL SJ_PhysicsSystem_SetGravity(SJ_PhysicsSystemHandle system, SJ_Vec3 gravity)
{
    auto* sys = reinterpret_cast<SJ_PhysicsSystem*>(system);
    if (sys && sys->system) sys->system->SetGravity(ToJPH(gravity));
}

extern "C" SJ_API SJ_Vec3 SJ_CALL SJ_PhysicsSystem_GetGravity(SJ_PhysicsSystemHandle system)
{
    auto* sys = reinterpret_cast<SJ_PhysicsSystem*>(system);
    if (!sys || !sys->system) return {0,0,0};
    return FromJPH(sys->system->GetGravity());
}

extern "C" SJ_API int32_t SJ_CALL SJ_PhysicsSystem_GetActiveBodyCount(SJ_PhysicsSystemHandle system)
{
    auto* sys = reinterpret_cast<SJ_PhysicsSystem*>(system);
    if (!sys || !sys->system) return 0;
    return sys->system->GetNumActiveBodies(JPH::EBodyType::RigidBody);
}

extern "C" SJ_API int32_t SJ_CALL SJ_PhysicsSystem_GetBodyCount(SJ_PhysicsSystemHandle system)
{
    auto* sys = reinterpret_cast<SJ_PhysicsSystem*>(system);
    if (!sys || !sys->system) return 0;
    return sys->system->GetBodyCount();
}

extern "C" SJ_API void SJ_CALL SJ_PhysicsSystem_SetContactListener(
    SJ_PhysicsSystemHandle system,
    SJ_ContactAddedCb addedCb, SJ_ContactRemovedCb removedCb,
    SJ_ValidateContactCb validateCb, SJ_ContactPersistedCb persistedCb)
{
    auto* sys = reinterpret_cast<SJ_PhysicsSystem*>(system);
    if (!sys || !sys->edgeFilter) return;
    sys->edgeFilter->SetUserCallbacks(
        (void*)addedCb, (void*)removedCb, (void*)validateCb, (void*)persistedCb);
}

// ============================================================================================
//  Shapes
// ============================================================================================
extern "C" SJ_API SJ_ShapeSettingsHandle SJ_CALL SJ_Shape_Box_Create(SJ_Vec3 halfExtent)
{
    auto* ss = new SJ_ShapeSettings();
    ss->settings = new JPH::BoxShape(ToJPH(halfExtent));
    return reinterpret_cast<SJ_ShapeSettingsHandle>(ss);
}

extern "C" SJ_API SJ_ShapeSettingsHandle SJ_CALL SJ_Shape_Sphere_Create(float radius)
{
    auto* ss = new SJ_ShapeSettings();
    ss->settings = new JPH::SphereShape(radius);
    return reinterpret_cast<SJ_ShapeSettingsHandle>(ss);
}

extern "C" SJ_API SJ_ShapeSettingsHandle SJ_CALL SJ_Shape_Capsule_Create(float halfHeightOfCylinder, float radius)
{
    auto* ss = new SJ_ShapeSettings();
    ss->settings = new JPH::CapsuleShape(halfHeightOfCylinder, radius);
    return reinterpret_cast<SJ_ShapeSettingsHandle>(ss);
}

extern "C" SJ_API SJ_ShapeSettingsHandle SJ_CALL SJ_Shape_Cylinder_Create(float halfHeight, float radius)
{
    auto* ss = new SJ_ShapeSettings();
    ss->settings = new JPH::CylinderShape(halfHeight, radius);
    return reinterpret_cast<SJ_ShapeSettingsHandle>(ss);
}

extern "C" SJ_API SJ_ShapeSettingsHandle SJ_CALL SJ_Shape_Plane_Create(SJ_Vec3 normal, float constant)
{
    auto* ss = new SJ_ShapeSettings();
    // Jolt PlaneShape: 平面由法线 + 原点到平面的距离(constant)定义
    ss->settings = new JPH::PlaneShape(JPH::Plane(ToJPH(normal).Normalized(), constant), nullptr, 1.0f);
    return reinterpret_cast<SJ_ShapeSettingsHandle>(ss);
}

extern "C" SJ_API SJ_ShapeSettingsHandle SJ_CALL SJ_Shape_Mesh_Create(
    const SJ_Vec3* vertices, int32_t vertexCount,
    const uint32_t* indices, int32_t indexCount)
{
    auto* ss = new SJ_ShapeSettings();

    // 构建 Jolt 顶点/索引列表
    JPH::VertexList jv;
    jv.resize(vertexCount);
    for (int i = 0; i < vertexCount; ++i)
    {
        JPH::Float3 f = { vertices[i].X, vertices[i].Y, vertices[i].Z };
        jv[i] = f;
    }
    JPH::IndexedTriangleList jt;
    jt.resize(indexCount / 3);
    for (int i = 0; i < (int)jt.size(); ++i)
    {
        jt[i].mIdx[0] = indices[i*3+0];
        jt[i].mIdx[1] = indices[i*3+1];
        jt[i].mIdx[2] = indices[i*3+2];
    }
    ss->settings = new JPH::MeshShapeSettings(jv, jt);

    // 构建 EdgeAdjacency（用 float 顶点 + uint32 索引）
    auto adj = std::make_shared<yama::EdgeAdjacency>();
    std::vector<float> vertsFlat((size_t)vertexCount * 3);
    for (int i = 0; i < vertexCount; ++i)
    {
        vertsFlat[i*3+0] = vertices[i].X;
        vertsFlat[i*3+1] = vertices[i].Y;
        vertsFlat[i*3+2] = vertices[i].Z;
    }
    adj->Build(vertsFlat.data(), vertexCount, indices, indexCount);
    ss->meshAdjacency = std::move(adj);

    return reinterpret_cast<SJ_ShapeSettingsHandle>(ss);
}

extern "C" SJ_API SJ_ShapeSettingsHandle SJ_CALL SJ_Shape_Compound_Create()
{
    auto* ss = new SJ_ShapeSettings();
    ss->settings = new JPH::StaticCompoundShapeSettings();
    return reinterpret_cast<SJ_ShapeSettingsHandle>(ss);
}

extern "C" SJ_API int32_t SJ_CALL SJ_Compound_AddChild(SJ_ShapeSettingsHandle compound, SJ_ShapeHandle shape, SJ_Vec3 pos, SJ_Quat rot)
{
    auto* ss = reinterpret_cast<SJ_ShapeSettings*>(compound);
    auto* sh = reinterpret_cast<SJ_Shape*>(shape);
    if (!ss || !sh) return -1;
    auto* compoundSettings = dynamic_cast<JPH::StaticCompoundShapeSettings*>(ss->settings.GetPtr());
    if (!compoundSettings) return -1;
    compoundSettings->AddShape(ToJPH(pos), ToJPHQ(rot), sh->shape);
    return (int32_t)compoundSettings->GetSubShapes().size() - 1;
}

extern "C" SJ_API SJ_ShapeHandle SJ_CALL SJ_Shape_Finalize(SJ_ShapeSettingsHandle settings)
{
    auto* ss = reinterpret_cast<SJ_ShapeSettings*>(settings);
    if (!ss) return nullptr;
    JPH::Shape::ShapeResult result = ss->settings->Create();
    if (!result.IsValid())
    {
        // finalize 失败：不释放 settings（调用方负责 Destroy）
        return nullptr;
    }
    auto* sh = new SJ_Shape();
    sh->shape = result.Get();

    // mesh 邻接转交全局注册表
    if (ss->meshAdjacency && ss->meshAdjacency->IsValid())
    {
        RegisterMeshAdjacencyGlobal(sh->shape.GetPtr(), ss->meshAdjacency);
        ss->meshAdjacency.reset();
    }
    return reinterpret_cast<SJ_ShapeHandle>(sh);
}

extern "C" SJ_API void SJ_CALL SJ_ShapeSettings_Destroy(SJ_ShapeSettingsHandle settings)
{
    delete reinterpret_cast<SJ_ShapeSettings*>(settings);
}

extern "C" SJ_API void SJ_CALL SJ_Shape_Destroy(SJ_ShapeHandle shape)
{
    auto* sh = reinterpret_cast<SJ_Shape*>(shape);
    if (!sh) return;
    // 注销 mesh 邻接
    if (sh->shape)
    {
        UnregisterMeshAdjacencyGlobal(sh->shape.GetPtr());
    }
    delete sh;
}

// ============================================================================================
//  Body interface
// ============================================================================================
extern "C" SJ_API uint32_t SJ_CALL SJ_Body_CreateAndAdd(SJ_PhysicsSystemHandle system, const SJ_BodyCreationSettings* settings)
{
    auto* sys = reinterpret_cast<SJ_PhysicsSystem*>(system);
    if (!sys || !sys->system || !settings) return 0;
    auto* sh = reinterpret_cast<SJ_Shape*>(settings->Shape);
    if (!sh || !sh->shape) return 0;

    JPH::EActivation activation = (settings->MotionType == (int)SJ_MT_Static)
        ? JPH::EActivation::DontActivate : JPH::EActivation::Activate;

    JPH::EBodyMotion motionType = (JPH::EBodyMotion)settings->MotionType;

    JPH::BodyCreationSettings bcs;
    bcs.SetShape(sh->shape);
    bcs.mPosition = JPH::RVec3(settings->Position.X, settings->Position.Y, settings->Position.Z);
    bcs.mRotation = ToJPHQ(settings->Rotation);
    bcs.mLinearVelocity = ToJPH(settings->LinearVelocity);
    bcs.mAngularVelocity = ToJPH(settings->AngularVelocity);
    bcs.mRestitution = settings->Restitution;
    bcs.mFriction = settings->Friction;
    bcs.mLinearDamping = settings->LinearDamping;
    bcs.mAngularDamping = settings->AngularDamping;
    bcs.mMaxLinearVelocity = settings->MaxLinearVelocity;
    bcs.mMaxAngularVelocity = settings->MaxAngularVelocity;
    bcs.mGravityFactor = settings->GravityFactor;
    bcs.mMotionType = motionType;
    bcs.mObjectLayer = LayerBitToIndex(settings->ObjectLayer);
    bcs.mIsSensor = settings->IsSensor != 0;
    bcs.mAllowSleeping = settings->AllowSleeping != 0;
    bcs.mUseManifoldReduction = true;
    bcs.mEnhancedInternalEdgeRemoval = settings->EnhancedInternalEdgeRemoval != 0;

    // 设置质量与 CollisionGroup
    if (motionType == JPH::EBodyMotion::Dynamic && settings->Mass > 0.0f)
    {
        // 通过 MotionProperties 设置质量（覆盖 Shape 的密度推导）
        bcs.mOverrideMassProperties = JPH::EOverrideMassProperties::CalculateInertia;
        bcs.mMassPropertiesOverride.ScaleToMass(settings->Mass);
    }

    bcs.mCollisionGroup = JPH::CollisionGroup(g_maskFilter,
        (JPH::CollisionGroup::GroupID)LayerBitToIndex(settings->ObjectLayer),
        (JPH::CollisionGroup::SubGroupID)settings->CollisionMask);

    auto& bi = GetBodyInterface(sys);
    JPH::BodyID::FSMResult r = bi.AddBody(bcs);
    if (!r.IsSuccess()) return 0;
    return r.GetBodyID().GetIndexAndSequenceNumber();
}

extern "C" SJ_API void SJ_CALL SJ_Body_RemoveAndDestroy(SJ_PhysicsSystemHandle system, uint32_t bodyId)
{
    auto* sys = reinterpret_cast<SJ_PhysicsSystem*>(system);
    if (!sys || !sys->system) return;
    auto& bi = GetBodyInterface(sys);
    bi.RemoveBody(ToBodyID(bodyId));
    bi.DestroyBody(ToBodyID(bodyId));
}

extern "C" SJ_API uint8_t SJ_CALL SJ_Body_IsValid(SJ_PhysicsSystemHandle system, uint32_t bodyId)
{
    auto* sys = reinterpret_cast<SJ_PhysicsSystem*>(system);
    if (!sys || !sys->system) return 0;
    return sys->system->GetBodyLockInterfaceNoLock().IsBodyValid(ToBodyID(bodyId)) ? 1 : 0;
}

extern "C" SJ_API void SJ_CALL SJ_Body_SetPosition(SJ_PhysicsSystemHandle system, uint32_t bodyId, SJ_Vec3 pos, SJ_Quat rot, int32_t activation)
{
    auto* sys = reinterpret_cast<SJ_PhysicsSystem*>(system);
    if (!sys || !sys->system) return;
    auto& bi = GetBodyInterface(sys);
    bi.SetPositionAndRotation(ToBodyID(bodyId), JPH::RVec3(pos.X, pos.Y, pos.Z), ToJPHQ(rot),
        activation == (int)SJ_ACT_DontActivate ? JPH::EActivation::DontActivate : JPH::EActivation::Activate);
}

extern "C" SJ_API void SJ_CALL SJ_Body_GetPosition(SJ_PhysicsSystemHandle system, uint32_t bodyId, SJ_Vec3* outPos, SJ_Quat* outRot)
{
    auto* sys = reinterpret_cast<SJ_PhysicsSystem*>(system);
    if (!sys || !sys->system || !outPos || !outRot) return;
    JPH::RVec3 p; JPH::Quat q;
    GetBodyInterface(sys).GetPositionAndRotation(ToBodyID(bodyId), p, q);
    *outPos = { (float)p.GetX(), (float)p.GetY(), (float)p.GetZ() };
    *outRot = FromJPHQ(q);
}

extern "C" SJ_API SJ_Vec3 SJ_CALL SJ_Body_GetPositionOnly(SJ_PhysicsSystemHandle system, uint32_t bodyId)
{
    auto* sys = reinterpret_cast<SJ_PhysicsSystem*>(system);
    if (!sys || !sys->system) return {0,0,0};
    JPH::RVec3 p = GetBodyInterface(sys).GetPosition(ToBodyID(bodyId));
    return { (float)p.GetX(), (float)p.GetY(), (float)p.GetZ() };
}

extern "C" SJ_API SJ_Quat SJ_CALL SJ_Body_GetRotation(SJ_PhysicsSystemHandle system, uint32_t bodyId)
{
    auto* sys = reinterpret_cast<SJ_PhysicsSystem*>(system);
    if (!sys || !sys->system) return {0,0,0,1};
    return FromJPHQ(GetBodyInterface(sys).GetRotation(ToBodyID(bodyId)));
}

extern "C" SJ_API void SJ_CALL SJ_Body_SetLinearVelocity(SJ_PhysicsSystemHandle system, uint32_t bodyId, SJ_Vec3 vel)
{
    auto* sys = reinterpret_cast<SJ_PhysicsSystem*>(system);
    if (!sys || !sys->system) return;
    GetBodyInterface(sys).SetLinearVelocity(ToBodyID(bodyId), ToJPH(vel));
}

extern "C" SJ_API SJ_Vec3 SJ_CALL SJ_Body_GetLinearVelocity(SJ_PhysicsSystemHandle system, uint32_t bodyId)
{
    auto* sys = reinterpret_cast<SJ_PhysicsSystem*>(system);
    if (!sys || !sys->system) return {0,0,0};
    return FromJPH(GetBodyInterface(sys).GetLinearVelocity(ToBodyID(bodyId)));
}

extern "C" SJ_API void SJ_CALL SJ_Body_SetAngularVelocity(SJ_PhysicsSystemHandle system, uint32_t bodyId, SJ_Vec3 vel)
{
    auto* sys = reinterpret_cast<SJ_PhysicsSystem*>(system);
    if (!sys || !sys->system) return;
    GetBodyInterface(sys).SetAngularVelocity(ToBodyID(bodyId), ToJPH(vel));
}

extern "C" SJ_API SJ_Vec3 SJ_CALL SJ_Body_GetAngularVelocity(SJ_PhysicsSystemHandle system, uint32_t bodyId)
{
    auto* sys = reinterpret_cast<SJ_PhysicsSystem*>(system);
    if (!sys || !sys->system) return {0,0,0};
    return FromJPH(GetBodyInterface(sys).GetAngularVelocity(ToBodyID(bodyId)));
}

extern "C" SJ_API void SJ_CALL SJ_Body_AddForce(SJ_PhysicsSystemHandle system, uint32_t bodyId, SJ_Vec3 force, int32_t activation)
{
    auto* sys = reinterpret_cast<SJ_PhysicsSystem*>(system);
    if (!sys || !sys->system) return;
    GetBodyInterface(sys).AddForce(ToBodyID(bodyId), ToJPH(force),
        activation == (int)SJ_ACT_DontActivate ? JPH::EActivation::DontActivate : JPH::EActivation::Activate);
}

extern "C" SJ_API void SJ_CALL SJ_Body_AddImpulse(SJ_PhysicsSystemHandle system, uint32_t bodyId, SJ_Vec3 impulse, int32_t activation)
{
    auto* sys = reinterpret_cast<SJ_PhysicsSystem*>(system);
    if (!sys || !sys->system) return;
    GetBodyInterface(sys).AddImpulse(ToBodyID(bodyId), ToJPH(impulse),
        activation == (int)SJ_ACT_DontActivate ? JPH::EActivation::DontActivate : JPH::EActivation::Activate);
}

extern "C" SJ_API void SJ_CALL SJ_Body_AddImpulseAtPosition(SJ_PhysicsSystemHandle system, uint32_t bodyId, SJ_Vec3 impulse, SJ_Vec3 worldPos, int32_t activation)
{
    auto* sys = reinterpret_cast<SJ_PhysicsSystem*>(system);
    if (!sys || !sys->system) return;
    GetBodyInterface(sys).AddImpulse(ToBodyID(bodyId), ToJPH(impulse), JPH::RVec3(worldPos.X, worldPos.Y, worldPos.Z),
        activation == (int)SJ_ACT_DontActivate ? JPH::EActivation::DontActivate : JPH::EActivation::Activate);
}

extern "C" SJ_API void SJ_CALL SJ_Body_AddTorque(SJ_PhysicsSystemHandle system, uint32_t bodyId, SJ_Vec3 torque, int32_t activation)
{
    auto* sys = reinterpret_cast<SJ_PhysicsSystem*>(system);
    if (!sys || !sys->system) return;
    GetBodyInterface(sys).AddTorque(ToBodyID(bodyId), ToJPH(torque),
        activation == (int)SJ_ACT_DontActivate ? JPH::EActivation::DontActivate : JPH::EActivation::Activate);
}

extern "C" SJ_API void SJ_CALL SJ_Body_SetRestitution(SJ_PhysicsSystemHandle system, uint32_t bodyId, float restitution)
{
    auto* sys = reinterpret_cast<SJ_PhysicsSystem*>(system);
    if (!sys || !sys->system) return;
    GetBodyInterface(sys).SetRestitution(ToBodyID(bodyId), restitution);
}

extern "C" SJ_API void SJ_CALL SJ_Body_SetFriction(SJ_PhysicsSystemHandle system, uint32_t bodyId, float friction)
{
    auto* sys = reinterpret_cast<SJ_PhysicsSystem*>(system);
    if (!sys || !sys->system) return;
    GetBodyInterface(sys).SetFriction(ToBodyID(bodyId), friction);
}

extern "C" SJ_API float SJ_CALL SJ_Body_GetRestitution(SJ_PhysicsSystemHandle system, uint32_t bodyId)
{
    auto* sys = reinterpret_cast<SJ_PhysicsSystem*>(system);
    if (!sys || !sys->system) return 0.0f;
    return GetBodyInterface(sys).GetRestitution(ToBodyID(bodyId));
}

extern "C" SJ_API float SJ_CALL SJ_Body_GetFriction(SJ_PhysicsSystemHandle system, uint32_t bodyId)
{
    auto* sys = reinterpret_cast<SJ_PhysicsSystem*>(system);
    if (!sys || !sys->system) return 0.0f;
    return GetBodyInterface(sys).GetFriction(ToBodyID(bodyId));
}

extern "C" SJ_API void SJ_CALL SJ_Body_SetMotionType(SJ_PhysicsSystemHandle system, uint32_t bodyId, int32_t motionType, int32_t activation)
{
    auto* sys = reinterpret_cast<SJ_PhysicsSystem*>(system);
    if (!sys || !sys->system) return;
    GetBodyInterface(sys).SetMotionType(ToBodyID(bodyId), (JPH::EBodyMotion)motionType,
        activation == (int)SJ_ACT_DontActivate ? JPH::EActivation::DontActivate : JPH::EActivation::Activate);
}

extern "C" SJ_API int32_t SJ_CALL SJ_Body_GetMotionType(SJ_PhysicsSystemHandle system, uint32_t bodyId)
{
    auto* sys = reinterpret_cast<SJ_PhysicsSystem*>(system);
    if (!sys || !sys->system) return (int)SJ_MT_Static;
    return (int32_t)GetBodyInterface(sys).GetMotionType(ToBodyID(bodyId));
}

extern "C" SJ_API void SJ_CALL SJ_Body_SetMotionQuality(SJ_PhysicsSystemHandle system, uint32_t bodyId, int32_t quality)
{
    auto* sys = reinterpret_cast<SJ_PhysicsSystem*>(system);
    if (!sys || !sys->system) return;
    GetBodyInterface(sys).SetMotionQuality(ToBodyID(bodyId), (JPH::EMotionQuality)quality);
}

extern "C" SJ_API void SJ_CALL SJ_Body_Activate(SJ_PhysicsSystemHandle system, uint32_t bodyId)
{
    auto* sys = reinterpret_cast<SJ_PhysicsSystem*>(system);
    if (!sys || !sys->system) return;
    GetBodyInterface(sys).ActivateBody(ToBodyID(bodyId));
}

extern "C" SJ_API void SJ_CALL SJ_Body_Deactivate(SJ_PhysicsSystemHandle system, uint32_t bodyId)
{
    auto* sys = reinterpret_cast<SJ_PhysicsSystem*>(system);
    if (!sys || !sys->system) return;
    GetBodyInterface(sys).DeactivateBody(ToBodyID(bodyId));
}

extern "C" SJ_API uint8_t SJ_CALL SJ_Body_IsActive(SJ_PhysicsSystemHandle system, uint32_t bodyId)
{
    auto* sys = reinterpret_cast<SJ_PhysicsSystem*>(system);
    if (!sys || !sys->system) return 0;
    return GetBodyInterface(sys).IsActive(ToBodyID(bodyId)) ? 1 : 0;
}

extern "C" SJ_API void SJ_CALL SJ_Body_SetGravityFactor(SJ_PhysicsSystemHandle system, uint32_t bodyId, float factor)
{
    auto* sys = reinterpret_cast<SJ_PhysicsSystem*>(system);
    if (!sys || !sys->system) return;
    GetBodyInterface(sys).SetGravityFactor(ToBodyID(bodyId), factor);
}

extern "C" SJ_API void SJ_CALL SJ_Body_SetIsSensor(SJ_PhysicsSystemHandle system, uint32_t bodyId, uint8_t isSensor)
{
    auto* sys = reinterpret_cast<SJ_PhysicsSystem*>(system);
    if (!sys || !sys->system) return;
    GetBodyInterface(sys).SetIsSensor(ToBodyID(bodyId), isSensor != 0);
}

extern "C" SJ_API void SJ_CALL SJ_Body_SetObjectLayer(SJ_PhysicsSystemHandle system, uint32_t bodyId, uint16_t layer, uint16_t mask)
{
    auto* sys = reinterpret_cast<SJ_PhysicsSystem*>(system);
    if (!sys || !sys->system) return;
    auto& bi = GetBodyInterface(sys);
    bi.SetObjectLayer(ToBodyID(bodyId), LayerBitToIndex(layer));
    // 更新 CollisionGroup
    JPH::CollisionGroup cg(g_maskFilter,
        (JPH::CollisionGroup::GroupID)LayerBitToIndex(layer),
        (JPH::CollisionGroup::SubGroupID)mask);
    bi.SetCollisionGroup(ToBodyID(bodyId), cg);
}

extern "C" SJ_API void SJ_CALL SJ_Body_GetWorldBounds(SJ_PhysicsSystemHandle system, uint32_t bodyId, SJ_Vec3* outMin, SJ_Vec3* outMax)
{
    auto* sys = reinterpret_cast<SJ_PhysicsSystem*>(system);
    if (!sys || !sys->system || !outMin || !outMax) return;
    JPH::AABox box = GetBodyInterface(sys).GetWorldSpaceBounds(ToBodyID(bodyId));
    *outMin = FromJPH(box.mMin);
    *outMax = FromJPH(box.mMax);
}

extern "C" SJ_API int32_t SJ_CALL SJ_Body_GetUserData(SJ_PhysicsSystemHandle system, uint32_t bodyId)
{
    auto* sys = reinterpret_cast<SJ_PhysicsSystem*>(system);
    if (!sys || !sys->system) return 0;
    uint64_t ud = GetBodyInterface(sys).GetUserData(ToBodyID(bodyId));
    return (int32_t)ud;
}

extern "C" SJ_API void SJ_CALL SJ_Body_SetUserData(SJ_PhysicsSystemHandle system, uint32_t bodyId, int32_t entityId)
{
    auto* sys = reinterpret_cast<SJ_PhysicsSystem*>(system);
    if (!sys || !sys->system) return;
    GetBodyInterface(sys).SetUserData(ToBodyID(bodyId), (uint64_t)entityId);
}

// ============================================================================================
//  Queries
// ============================================================================================

// 单射线最近命中
extern "C" SJ_API uint8_t SJ_CALL SJ_Query_Raycast(SJ_PhysicsSystemHandle system, SJ_Vec3 origin, SJ_Vec3 direction, float maxDistance, uint16_t layer, uint16_t mask, SJ_RaycastResult* outResult)
{
    auto* sys = reinterpret_cast<SJ_PhysicsSystem*>(system);
    if (!sys || !sys->system || !outResult) return 0;
    JPH::RRayCast ray(JPH::RVec3(origin.X, origin.Y, origin.Z), ToJPH(direction) * maxDistance);
    JPH::ClosestHitCollisionCollector<JPH::RayCastBodyCollector> collector;
    sys->system->GetNarrowPhaseQuery().CastRay(ray, collector, QueryLayerFilter(mask), {});
    if (!collector.HadHit()) return 0;
    const JPH::RayCastResult& r = collector.mHit;
    outResult->BodyId = r.mBodyID.GetIndexAndSequenceNumber();
    JPH::Vec3 hitPos = ray.GetPointOnRay(r.mFraction);
    outResult->Position = FromJPH(hitPos);
    outResult->Normal = FromJPH(r.mBodyID.IsValid() ? sys->system->GetBodyLockInterfaceNoLock().GetBody(r.mBodyID).GetWorldSpaceSurfaceNormal(r.mSubShapeID2, hitPos) : JPH::Vec3(0,1,0));
    outResult->Fraction = r.mFraction;
    outResult->Distance = r.mFraction * maxDistance;
    outResult->SubShapeId = (uint8_t)(r.mSubShapeID2.GetValue() & 0xFF);
    return 1;
}

// 多射线命中
extern "C" SJ_API int32_t SJ_CALL SJ_Query_RaycastMulti(SJ_PhysicsSystemHandle system, SJ_Vec3 origin, SJ_Vec3 direction, float maxDistance, uint16_t layer, uint16_t mask, SJ_RaycastResult* outResults, int32_t maxResults)
{
    auto* sys = reinterpret_cast<SJ_PhysicsSystem*>(system);
    if (!sys || !sys->system || !outResults || maxResults <= 0) return 0;
    JPH::RRayCast ray(JPH::RVec3(origin.X, origin.Y, origin.Z), ToJPH(direction) * maxDistance);
    JPH::AllHitCollisionCollector<JPH::RayCastBodyCollector> collector;
    sys->system->GetNarrowPhaseQuery().CastRay(ray, collector, QueryLayerFilter(mask), {});
    collector.Sort();
    int32_t count = std::min((int32_t)collector.mHits.size(), maxResults);
    for (int32_t i = 0; i < count; ++i)
    {
        const JPH::RayCastResult& r = collector.mHits[i];
        outResults[i].BodyId = r.mBodyID.GetIndexAndSequenceNumber();
        JPH::Vec3 hitPos = ray.GetPointOnRay(r.mFraction);
        outResults[i].Position = FromJPH(hitPos);
        outResults[i].Normal = FromJPH(sys->system->GetBodyLockInterfaceNoLock().GetBody(r.mBodyID).GetWorldSpaceSurfaceNormal(r.mSubShapeID2, hitPos));
        outResults[i].Fraction = r.mFraction;
        outResults[i].Distance = r.mFraction * maxDistance;
        outResults[i].SubShapeId = (uint8_t)(r.mSubShapeID2.GetValue() & 0xFF);
    }
    return count;
}

// 球重叠
extern "C" SJ_API int32_t SJ_CALL SJ_Query_OverlapSphere(SJ_PhysicsSystemHandle system, SJ_Vec3 center, float radius, uint16_t layer, uint16_t mask, uint32_t* outBodyIds, int32_t maxResults)
{
    auto* sys = reinterpret_cast<SJ_PhysicsSystem*>(system);
    if (!sys || !sys->system || !outBodyIds || maxResults <= 0) return 0;
    JPH::SphereShape sphere(radius);
    JPH::AllHitCollisionCollector<JPH::CollideShapeBodyCollector> collector;
    sys->system->GetNarrowPhaseQuery().CollideShape(sphere, JPH::Vec3::sReplicate(1.0f),
        JPH::RMat44::sTranslation(JPH::RVec3(center.X, center.Y, center.Z)),
        JPH::CollideShapeSettings(), JPH::RVec3(0,0,0), collector, {}, QueryLayerFilter(mask));
    int32_t count = std::min((int32_t)collector.mHits.size(), maxResults);
    for (int32_t i = 0; i < count; ++i) outBodyIds[i] = collector.mHits[i].mBodyID2.GetIndexAndSequenceNumber();
    return count;
}

// 盒重叠
extern "C" SJ_API int32_t SJ_CALL SJ_Query_OverlapBox(SJ_PhysicsSystemHandle system, SJ_Vec3 center, SJ_Vec3 halfExtent, uint16_t layer, uint16_t mask, uint32_t* outBodyIds, int32_t maxResults)
{
    auto* sys = reinterpret_cast<SJ_PhysicsSystem*>(system);
    if (!sys || !sys->system || !outBodyIds || maxResults <= 0) return 0;
    JPH::BoxShape box(ToJPH(halfExtent));
    JPH::AllHitCollisionCollector<JPH::CollideShapeBodyCollector> collector;
    sys->system->GetNarrowPhaseQuery().CollideShape(box, JPH::Vec3::sReplicate(1.0f),
        JPH::RMat44::sTranslation(JPH::RVec3(center.X, center.Y, center.Z)),
        JPH::CollideShapeSettings(), JPH::RVec3(0,0,0), collector, {}, QueryLayerFilter(mask));
    int32_t count = std::min((int32_t)collector.mHits.size(), maxResults);
    for (int32_t i = 0; i < count; ++i) outBodyIds[i] = collector.mHits[i].mBodyID2.GetIndexAndSequenceNumber();
    return count;
}

// 胶囊重叠
extern "C" SJ_API int32_t SJ_CALL SJ_Query_OverlapCapsule(SJ_PhysicsSystemHandle system, SJ_Vec3 point1, SJ_Vec3 point2, float radius, uint16_t layer, uint16_t mask, uint32_t* outBodyIds, int32_t maxResults)
{
    auto* sys = reinterpret_cast<SJ_PhysicsSystem*>(system);
    if (!sys || !sys->system || !outBodyIds || maxResults <= 0) return 0;
    JPH::Vec3 p1 = ToJPH(point1), p2 = ToJPH(point2);
    float halfLen = (p2 - p1).Length() * 0.5f;
    JPH::CapsuleShape capsule(halfLen, radius);
    JPH::Vec3 center = (p1 + p2) * 0.5f;
    // 胶囊沿 Y 轴；旋转使其对齐 (p2-p1)
    JPH::Vec3 axis = (p2 - p1).Normalized();
    JPH::Quat rot = JPH::Quat::sFromTo(JPH::Vec3::sAxisY(), axis);
    JPH::AllHitCollisionCollector<JPH::CollideShapeBodyCollector> collector;
    sys->system->GetNarrowPhaseQuery().CollideShape(capsule, JPH::Vec3::sReplicate(1.0f),
        JPH::RMat44::sRotationTranslation(rot, JPH::RVec3(center.GetX(), center.GetY(), center.GetZ())),
        JPH::CollideShapeSettings(), JPH::RVec3(0,0,0), collector, {}, QueryLayerFilter(mask));
    int32_t count = std::min((int32_t)collector.mHits.size(), maxResults);
    for (int32_t i = 0; i < count; ++i) outBodyIds[i] = collector.mHits[i].mBodyID2.GetIndexAndSequenceNumber();
    return count;
}

// 凸点云重叠
extern "C" SJ_API int32_t SJ_CALL SJ_Query_OverlapShape(SJ_PhysicsSystemHandle system, const SJ_Vec3* points, int32_t pointCount, SJ_Vec3 position, SJ_Quat rotation, uint16_t layer, uint16_t mask, uint32_t* outBodyIds, int32_t maxResults)
{
    auto* sys = reinterpret_cast<SJ_PhysicsSystem*>(system);
    if (!sys || !sys->system || !points || pointCount < 4 || !outBodyIds || maxResults <= 0) return 0;
    JPH::ConvexHullShapeSettings chs;
    JPH::Array<JPH::Vec3> pts;
    pts.reserve(pointCount);
    for (int i = 0; i < pointCount; ++i) pts.push_back(ToJPH(points[i]));
    chs.mPoints = pts;
    JPH::Shape::ShapeResult sr = chs.Create();
    if (!sr.IsValid()) return 0;
    JPH::AllHitCollisionCollector<JPH::CollideShapeBodyCollector> collector;
    sys->system->GetNarrowPhaseQuery().CollideShape(*sr.Get(), JPH::Vec3::sReplicate(1.0f),
        JPH::RMat44::sRotationTranslation(ToJPHQ(rotation), JPH::RVec3(position.X, position.Y, position.Z)),
        JPH::CollideShapeSettings(), JPH::RVec3(0,0,0), collector, {}, QueryLayerFilter(mask));
    int32_t count = std::min((int32_t)collector.mHits.size(), maxResults);
    for (int32_t i = 0; i < count; ++i) outBodyIds[i] = collector.mHits[i].mBodyID2.GetIndexAndSequenceNumber();
    return count;
}

// 形状碰撞取接触点
extern "C" SJ_API int32_t SJ_CALL SJ_Query_CollideShape(SJ_PhysicsSystemHandle system, SJ_ShapeHandle shape, SJ_Vec3 position, SJ_Quat rotation, uint16_t layer, uint16_t mask, SJ_RaycastResult* outContacts, int32_t maxContacts)
{
    auto* sys = reinterpret_cast<SJ_PhysicsSystem*>(system);
    auto* sh = reinterpret_cast<SJ_Shape*>(shape);
    if (!sys || !sys->system || !sh || !sh->shape || !outContacts || maxContacts <= 0) return 0;
    JPH::AllHitCollisionCollector<JPH::CollideShapeCollector> collector;
    sys->system->GetNarrowPhaseQuery().CollideShape(*sh->shape, JPH::Vec3::sReplicate(1.0f),
        JPH::RMat44::sRotationTranslation(ToJPHQ(rotation), JPH::RVec3(position.X, position.Y, position.Z)),
        JPH::CollideShapeSettings(), JPH::RVec3(0,0,0), collector, {}, QueryLayerFilter(mask));
    int32_t count = std::min((int32_t)collector.mHits.size(), maxContacts);
    for (int32_t i = 0; i < count; ++i)
    {
        const JPH::CollideShapeResult& r = collector.mHits[i];
        outContacts[i].BodyId = r.mBodyID2.GetIndexAndSequenceNumber();
        outContacts[i].Position = FromJPH(r.mContactPointOn2);
        outContacts[i].Normal = FromJPH(-r.mPenetrationAxis.Normalized());
        outContacts[i].PenetrationDepth = 0; // 占位，CollideShapeResult 无直接穿透深度字段
        outContacts[i].Fraction = 0;
        outContacts[i].Distance = 0;
        outContacts[i].SubShapeId = (uint8_t)(r.mSubShapeID2.GetValue() & 0xFF);
    }
    return count;
}

// ============================================================================================
//  Constraints
// ============================================================================================
extern "C" SJ_API uint32_t SJ_CALL SJ_Constraint_CreateFixed(SJ_PhysicsSystemHandle system, uint32_t bodyA, uint32_t bodyB)
{
    auto* sys = reinterpret_cast<SJ_PhysicsSystem*>(system);
    if (!sys || !sys->system) return 0;
    auto& bi = GetBodyInterface(sys);
    JPH::Body* ba = bi.IsAdded(ToBodyID(bodyA)) ? &sys->system->GetBodyLockInterfaceNoLock().GetBody(ToBodyID(bodyA)) : nullptr;
    JPH::Body* bb = bi.IsAdded(ToBodyID(bodyB)) ? &sys->system->GetBodyLockInterfaceNoLock().GetBody(ToBodyID(bodyB)) : nullptr;
    if (!ba || !bb) return 0;
    JPH::FixedConstraintSettings cs;
    cs.mAutoCalculatePoint = true;
    auto* c = new JPH::FixedConstraint(*ba, *bb, cs);
    uint32_t id = RegisterConstraint(sys, c);
    sys->system->AddConstraint(c);
    return id;
}

extern "C" SJ_API uint32_t SJ_CALL SJ_Constraint_CreatePoint(SJ_PhysicsSystemHandle system, uint32_t bodyA, uint32_t bodyB, SJ_Vec3 pivotA, SJ_Vec3 pivotB)
{
    auto* sys = reinterpret_cast<SJ_PhysicsSystem*>(system);
    if (!sys || !sys->system) return 0;
    JPH::PointConstraintSettings cs;
    cs.mSpace = JPH::EConstraintSpace::WorldSpace;
    cs.mPoint1 = JPH::RVec3(pivotA.X, pivotA.Y, pivotA.Z);
    cs.mPoint2 = JPH::RVec3(pivotB.X, pivotB.Y, pivotB.Z);
    JPH::Constraint* c = cs.Create(ToBodyID(bodyA), ToBodyID(bodyB), *sys->system);
    uint32_t id = RegisterConstraint(sys, c);
    sys->system->AddConstraint(c);
    return id;
}

extern "C" SJ_API uint32_t SJ_CALL SJ_Constraint_CreateHinge(SJ_PhysicsSystemHandle system, uint32_t bodyA, uint32_t bodyB, SJ_Vec3 pivotA, SJ_Vec3 pivotB, SJ_Vec3 hingeAxisA, SJ_Vec3 hingeAxisB)
{
    auto* sys = reinterpret_cast<SJ_PhysicsSystem*>(system);
    if (!sys || !sys->system) return 0;
    JPH::HingeConstraintSettings cs;
    cs.mSpace = JPH::EConstraintSpace::WorldSpace;
    cs.mPoint1 = JPH::RVec3(pivotA.X, pivotA.Y, pivotA.Z);
    cs.mPoint2 = JPH::RVec3(pivotB.X, pivotB.Y, pivotB.Z);
    cs.mHingeAxis1 = ToJPH(hingeAxisA).Normalized();
    cs.mHingeAxis2 = ToJPH(hingeAxisB).Normalized();
    JPH::Constraint* c = cs.Create(ToBodyID(bodyA), ToBodyID(bodyB), *sys->system);
    uint32_t id = RegisterConstraint(sys, c);
    sys->system->AddConstraint(c);
    return id;
}

extern "C" SJ_API uint32_t SJ_CALL SJ_Constraint_CreateSlider(SJ_PhysicsSystemHandle system, uint32_t bodyA, uint32_t bodyB, SJ_Vec3 pivotA, SJ_Vec3 pivotB, SJ_Vec3 sliderAxisA, SJ_Vec3 sliderAxisB, float minLimit, float maxLimit)
{
    auto* sys = reinterpret_cast<SJ_PhysicsSystem*>(system);
    if (!sys || !sys->system) return 0;
    JPH::SliderConstraintSettings cs;
    cs.mSpace = JPH::EConstraintSpace::WorldSpace;
    cs.mPoint1 = JPH::RVec3(pivotA.X, pivotA.Y, pivotA.Z);
    cs.mSliderAxis1 = ToJPH(sliderAxisA).Normalized();
    cs.mPoint2 = JPH::RVec3(pivotB.X, pivotB.Y, pivotB.Z);
    cs.mSliderAxis2 = ToJPH(sliderAxisB).Normalized();
    cs.mLimitsMin = minLimit;
    cs.mLimitsMax = maxLimit;
    JPH::Constraint* c = cs.Create(ToBodyID(bodyA), ToBodyID(bodyB), *sys->system);
    uint32_t id = RegisterConstraint(sys, c);
    sys->system->AddConstraint(c);
    return id;
}

extern "C" SJ_API uint32_t SJ_CALL SJ_Constraint_CreateDistance(SJ_PhysicsSystemHandle system, uint32_t bodyA, uint32_t bodyB, SJ_Vec3 pivotA, SJ_Vec3 pivotB, float minDistance, float maxDistance)
{
    auto* sys = reinterpret_cast<SJ_PhysicsSystem*>(system);
    if (!sys || !sys->system) return 0;
    JPH::DistanceConstraintSettings cs;
    cs.mSpace = JPH::EConstraintSpace::WorldSpace;
    cs.mPoint1 = JPH::RVec3(pivotA.X, pivotA.Y, pivotA.Z);
    cs.mPoint2 = JPH::RVec3(pivotB.X, pivotB.Y, pivotB.Z);
    cs.mMinDistance = minDistance;
    cs.mMaxDistance = maxDistance;
    JPH::Constraint* c = cs.Create(ToBodyID(bodyA), ToBodyID(bodyB), *sys->system);
    uint32_t id = RegisterConstraint(sys, c);
    sys->system->AddConstraint(c);
    return id;
}

extern "C" SJ_API void SJ_CALL SJ_Constraint_Activate(SJ_PhysicsSystemHandle system, uint32_t constraintId)
{
    auto* sys = reinterpret_cast<SJ_PhysicsSystem*>(system);
    if (!sys || !sys->system) return;
    JPH::Constraint* c = LookupConstraint(sys, constraintId);
    if (c) sys->system->AddConstraint(c);
}

extern "C" SJ_API void SJ_CALL SJ_Constraint_Destroy(SJ_PhysicsSystemHandle system, uint32_t constraintId)
{
    auto* sys = reinterpret_cast<SJ_PhysicsSystem*>(system);
    if (!sys || !sys->system) return;
    JPH::Constraint* c = LookupConstraint(sys, constraintId);
    if (c) sys->system->RemoveConstraint(c);
    UnregisterConstraint(sys, constraintId);
}

extern "C" SJ_API void SJ_CALL SJ_Constraint_Hinge_SetLimits(SJ_PhysicsSystemHandle system, uint32_t constraintId, float minAngle, float maxAngle)
{
    auto* sys = reinterpret_cast<SJ_PhysicsSystem*>(system);
    JPH::Constraint* c = LookupConstraint(sys, constraintId);
    auto* h = dynamic_cast<JPH::HingeConstraint*>(c);
    if (h) h->SetLimits(minAngle, maxAngle);
}

extern "C" SJ_API void SJ_CALL SJ_Constraint_Slider_SetLimits(SJ_PhysicsSystemHandle system, uint32_t constraintId, float minLimit, float maxLimit)
{
    auto* sys = reinterpret_cast<SJ_PhysicsSystem*>(system);
    JPH::Constraint* c = LookupConstraint(sys, constraintId);
    auto* s = dynamic_cast<JPH::SliderConstraint*>(c);
    if (s) s->SetLimits(minLimit, maxLimit);
}

extern "C" SJ_API void SJ_CALL SJ_Constraint_SetFrequency(SJ_PhysicsSystemHandle system, uint32_t constraintId, float frequencyHz)
{
    auto* sys = reinterpret_cast<SJ_PhysicsSystem*>(system);
    JPH::Constraint* c = LookupConstraint(sys, constraintId);
    if (c) c->SetConstraintPriority(JPH::ConstraintPriority(0)); // 占位：频率在 settings 上，运行时通过 SetFrequency
    // Jolt 的 TwoBodyConstraint 没有直接运行时 SetFrequency；通过 SpringSettings 设置
    // 这里对支持的约束类型设置弹簧频率
    if (auto* h = dynamic_cast<JPH::HingeConstraint*>(c)) { (void)h; }
    (void)frequencyHz;
}

extern "C" SJ_API void SJ_CALL SJ_Constraint_SetDamping(SJ_PhysicsSystemHandle system, uint32_t constraintId, float damping)
{
    auto* sys = reinterpret_cast<SJ_PhysicsSystem*>(system);
    (void)sys; (void)constraintId; (void)damping;
    // 同上：Jolt 弹簧阻尼在 settings 上；运行时改动有限支持。留作后续扩展。
}

extern "C" SJ_API void SJ_CALL SJ_Constraint_SetMotorState(SJ_PhysicsSystemHandle system, uint32_t constraintId, int32_t motorState)
{
    auto* sys = reinterpret_cast<SJ_PhysicsSystem*>(system);
    JPH::Constraint* c = LookupConstraint(sys, constraintId);
    if (!c) return;
    JPH::EMotorState ms = (JPH::EMotorState)motorState;
    if (auto* h = dynamic_cast<JPH::HingeConstraint*>(c)) h->SetMotorState(ms);
    else if (auto* s = dynamic_cast<JPH::SliderConstraint*>(c)) s->SetMotorState(ms);
}

extern "C" SJ_API void SJ_CALL SJ_Constraint_SetTargetVelocity(SJ_PhysicsSystemHandle system, uint32_t constraintId, float velocity)
{
    auto* sys = reinterpret_cast<SJ_PhysicsSystem*>(system);
    JPH::Constraint* c = LookupConstraint(sys, constraintId);
    if (!c) return;
    if (auto* h = dynamic_cast<JPH::HingeConstraint*>(c)) h->SetTargetAngularVelocity(velocity);
    else if (auto* s = dynamic_cast<JPH::SliderConstraint*>(c)) s->SetTargetVelocity(velocity);
}

extern "C" SJ_API void SJ_CALL SJ_Constraint_SetMaxMotorForce(SJ_PhysicsSystemHandle system, uint32_t constraintId, float maxForce)
{
    auto* sys = reinterpret_cast<SJ_PhysicsSystem*>(system);
    JPH::Constraint* c = LookupConstraint(sys, constraintId);
    if (!c) return;
    if (auto* h = dynamic_cast<JPH::HingeConstraint*>(c)) h->SetMaxTorque(maxForce);
    else if (auto* s = dynamic_cast<JPH::SliderConstraint*>(c)) s->SetMaxMotorForce(maxForce);
}

// ============================================================================================
//  Character controller (CharacterVirtual)
// ============================================================================================
extern "C" SJ_API SJ_CharacterHandle SJ_CALL SJ_Character_Create(SJ_PhysicsSystemHandle system, float capsuleRadius, float capsuleHalfHeight, SJ_Vec3 position, SJ_Quat rotation, uint16_t layer, uint16_t mask, float maxSlopeAngle, float upStandHeight, float mass)
{
    auto* sys = reinterpret_cast<SJ_PhysicsSystem*>(system);
    if (!sys || !sys->system) return nullptr;
    JPH::Ref<JPH::CharacterVirtualSettings> settings = new JPH::CharacterVirtualSettings();
    settings->mShape = new JPH::CapsuleShape(capsuleHalfHeight, capsuleRadius);
    settings->mMaxSlopeAngle = maxSlopeAngle;
    settings->mUp = JPH::Vec3::sAxisY();
    settings->mSupportingVolume = JPH::AABox(JPH::Vec3(-1e6f, -1e6f, -1e6f), JPH::Vec3(1e6f, upStandHeight, 1e6f));
    settings->mMass = mass > 0 ? mass : 70.0f;
    settings->mMaxStrength = 100.0f;

    auto* ch = new SJ_Character();
    ch->tempAlloc = new JPH::TempAllocatorImpl(8 * 1024 * 1024);
    ch->character = new JPH::CharacterVirtual(settings,
        JPH::RVec3(position.X, position.Y, position.Z),
        ToJPHQ(rotation), maxSlopeAngle, sys->system, ch->tempAlloc);
    // 设置层/掩码
    ch->character->SetObjectLayer(LayerBitToIndex(layer));
    // CharacterVirtual 用 CollisionGroup 过滤
    JPH::CollisionGroup cg(g_maskFilter,
        (JPH::CollisionGroup::GroupID)LayerBitToIndex(layer),
        (JPH::CollisionGroup::SubGroupID)mask);
    ch->character->SetCollisionGroup(cg);
    return reinterpret_cast<SJ_CharacterHandle>(ch);
}

extern "C" SJ_API void SJ_CALL SJ_Character_Destroy(SJ_PhysicsSystemHandle system, SJ_CharacterHandle character)
{
    auto* ch = reinterpret_cast<SJ_Character*>(character);
    if (!ch) return;
    delete ch->tempAlloc;
    delete ch;
}

extern "C" SJ_API SJ_Vec3 SJ_CALL SJ_Character_Update(SJ_PhysicsSystemHandle system, SJ_CharacterHandle character, float dt, SJ_Vec3 gravity, SJ_Vec3 desiredVelocity, float maxWalkAngle, const SJ_CharacterUpdateSettings* settings, SJ_GroundState* outGroundState)
{
    auto* sys = reinterpret_cast<SJ_PhysicsSystem*>(system);
    auto* ch = reinterpret_cast<SJ_Character*>(character);
    if (!sys || !sys->system || !ch || !ch->character || !settings) return {0,0,0};

    ch->character->SetLinearVelocity(ToJPH(desiredVelocity + gravity * dt));

    JPH::CharacterVirtual::ExtendedUpdateSettings us;
    us.mStickToFloorStepDown = JPH::Vec3(0, -settings->StickToFloorDistance, 0);
    us.mWalkStairsStepUp = settings->WalkStairsExtraDistance > 0 ? JPH::Vec3(0, settings->WalkStairsUpExtraHeight, 0) : JPH::Vec3(0,0,0);
    us.mWalkStairsStepForwardTest = settings->WalkStairsExtraDistance;
    us.mWalkStairsStepDownExtra = settings->DownStandHeight;

    ch->character->ExtendedUpdate(dt,
        JPH::Vec3(-gravity.X, -gravity.Y, -gravity.Z),
        us,
        sys->system->GetDefaultBroadPhaseLayerFilter(),
        sys->system->GetDefaultLayerFilter(),
        {},
        *sys->tempAllocator);

    if (outGroundState)
    {
        const JPH::CharacterVirtual::EGroundState& gs = ch->character->GetGroundState();
        uint8_t s;
        switch (gs)
        {
            case JPH::CharacterVirtual::EGroundState::OnGround: s = SJ_GS_OnGround; break;
            case JPH::CharacterVirtual::EGroundState::OnSteepGround: s = SJ_GS_OnSteepGround; break;
            case JPH::CharacterVirtual::EGroundState::NotSupported: s = SJ_GS_NotSupported; break;
            default: s = SJ_GS_InAir; break;
        }
        outGroundState->GroundState = s;
        JPH::Vec3 gp = ch->character->GetGroundPosition();
        outGroundState->GroundPosition = FromJPH(gp);
        outGroundState->GroundNormal = FromJPH(ch->character->GetGroundNormal());
        outGroundState->GroundBodyId = ch->character->GetGroundBodyID().GetIndexAndSequenceNumber();
        outGroundState->GroundY = gp.GetY();
        outGroundState->SlopeCosAngle = std::cos(maxWalkAngle);
    }

    JPH::RVec3 pos = ch->character->GetPosition();
    return { (float)pos.GetX(), (float)pos.GetY(), (float)pos.GetZ() };
}

extern "C" SJ_API void SJ_CALL SJ_Character_SetPosition(SJ_PhysicsSystemHandle system, SJ_CharacterHandle character, SJ_Vec3 position)
{
    auto* ch = reinterpret_cast<SJ_Character*>(character);
    if (!ch || !ch->character) return;
    ch->character->SetPosition(JPH::RVec3(position.X, position.Y, position.Z));
}

extern "C" SJ_API SJ_Vec3 SJ_CALL SJ_Character_GetPosition(SJ_PhysicsSystemHandle system, SJ_CharacterHandle character)
{
    auto* ch = reinterpret_cast<SJ_Character*>(character);
    if (!ch || !ch->character) return {0,0,0};
    JPH::RVec3 p = ch->character->GetPosition();
    return { (float)p.GetX(), (float)p.GetY(), (float)p.GetZ() };
}

extern "C" SJ_API SJ_Quat SJ_CALL SJ_Character_GetRotation(SJ_PhysicsSystemHandle system, SJ_CharacterHandle character)
{
    auto* ch = reinterpret_cast<SJ_Character*>(character);
    if (!ch || !ch->character) return {0,0,0,1};
    return FromJPHQ(ch->character->GetRotation());
}

extern "C" SJ_API void SJ_CALL SJ_Character_SetRotation(SJ_PhysicsSystemHandle system, SJ_CharacterHandle character, SJ_Quat rotation)
{
    auto* ch = reinterpret_cast<SJ_Character*>(character);
    if (!ch || !ch->character) return;
    ch->character->SetRotation(ToJPHQ(rotation));
}

extern "C" SJ_API SJ_Vec3 SJ_CALL SJ_Character_GetLinearVelocity(SJ_PhysicsSystemHandle system, SJ_CharacterHandle character)
{
    auto* ch = reinterpret_cast<SJ_Character*>(character);
    if (!ch || !ch->character) return {0,0,0};
    return FromJPH(ch->character->GetLinearVelocity());
}

extern "C" SJ_API void SJ_CALL SJ_Character_SetLinearVelocity(SJ_PhysicsSystemHandle system, SJ_CharacterHandle character, SJ_Vec3 velocity)
{
    auto* ch = reinterpret_cast<SJ_Character*>(character);
    if (!ch || !ch->character) return;
    ch->character->SetLinearVelocity(ToJPH(velocity));
}

extern "C" SJ_API uint8_t SJ_CALL SJ_Character_GetGroundState(SJ_PhysicsSystemHandle system, SJ_CharacterHandle character)
{
    auto* ch = reinterpret_cast<SJ_Character*>(character);
    if (!ch || !ch->character) return SJ_GS_InAir;
    switch (ch->character->GetGroundState())
    {
        case JPH::CharacterVirtual::EGroundState::OnGround: return SJ_GS_OnGround;
        case JPH::CharacterVirtual::EGroundState::OnSteepGround: return SJ_GS_OnSteepGround;
        case JPH::CharacterVirtual::EGroundState::NotSupported: return SJ_GS_NotSupported;
        default: return SJ_GS_InAir;
    }
}

extern "C" SJ_API SJ_Vec3 SJ_CALL SJ_Character_GetGroundNormal(SJ_PhysicsSystemHandle system, SJ_CharacterHandle character)
{
    auto* ch = reinterpret_cast<SJ_Character*>(character);
    if (!ch || !ch->character) return {0,1,0};
    return FromJPH(ch->character->GetGroundNormal());
}

extern "C" SJ_API SJ_Vec3 SJ_CALL SJ_Character_GetGroundPosition(SJ_PhysicsSystemHandle system, SJ_CharacterHandle character)
{
    auto* ch = reinterpret_cast<SJ_Character*>(character);
    if (!ch || !ch->character) return {0,0,0};
    return FromJPH(ch->character->GetGroundPosition());
}

extern "C" SJ_API uint32_t SJ_CALL SJ_Character_GetGroundBodyId(SJ_PhysicsSystemHandle system, SJ_CharacterHandle character)
{
    auto* ch = reinterpret_cast<SJ_Character*>(character);
    if (!ch || !ch->character) return 0;
    return ch->character->GetGroundBodyID().GetIndexAndSequenceNumber();
}

extern "C" SJ_API void SJ_CALL SJ_Character_SetEnhancedInternalEdgeRemoval(SJ_PhysicsSystemHandle system, SJ_CharacterHandle character, uint8_t enabled)
{
    auto* ch = reinterpret_cast<SJ_Character>(character);
    if (!ch || !ch->character) return;
    ch->character->SetEnhancedInternalEdgeRemoval(enabled != 0);
}

// ============================================================================================
//  Debug
// ============================================================================================
extern "C" SJ_API int32_t SJ_CALL SJ_Debug_GetBodyStats(SJ_PhysicsSystemHandle system, uint32_t* outBodyIds, int32_t maxCount)
{
    auto* sys = reinterpret_cast<SJ_PhysicsSystem*>(system);
    if (!sys || !sys->system || !outBodyIds || maxCount <= 0) return 0;
    // 返回活动体 ID
    const JPH::BodyIDVector& active = sys->system->GetActiveBodies(JPH::EBodyType::RigidBody);
    int32_t count = std::min((int32_t)active.size(), maxCount);
    for (int32_t i = 0; i < count; ++i) outBodyIds[i] = active[i].GetIndexAndSequenceNumber();
    return count;
}

// ============================================================================================
//  SmartEdgeFilter — ABI 桥接
// ============================================================================================
extern "C" SJ_API void SJ_CALL SJ_SmartEdgeFilter_SetBodyThreshold(SJ_PhysicsSystemHandle system, uint32_t bodyId, float angleThresholdRadians)
{
    auto* sys = reinterpret_cast<SJ_PhysicsSystem*>(system);
    if (!sys || !sys->edgeFilter) return;
    sys->edgeFilter->SetBodyThreshold(ToBodyID(bodyId), angleThresholdRadians);
}

extern "C" SJ_API float SJ_CALL SJ_SmartEdgeFilter_GetBodyThreshold(SJ_PhysicsSystemHandle system, uint32_t bodyId)
{
    auto* sys = reinterpret_cast<SJ_PhysicsSystem*>(system);
    if (!sys || !sys->edgeFilter) return 0.0f;
    return sys->edgeFilter->GetBodyThreshold(ToBodyID(bodyId));
}

extern "C" SJ_API void SJ_CALL SJ_SmartEdgeFilter_Enable(SJ_PhysicsSystemHandle system, uint8_t enabled)
{
    auto* sys = reinterpret_cast<SJ_PhysicsSystem*>(system);
    if (!sys || !sys->edgeFilter) return;
    sys->edgeFilter->SetEnabled(enabled != 0);
}
