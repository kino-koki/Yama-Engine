// ============================================================================================
//  YamaJolt.h — Public C ABI for the YamaJolt native wrapper around Jolt Physics.
//
//  This header is the single source of truth for the C ABI consumed by the C# P/Invoke layer
//  (YamaEngine/Physics/JoltPhysicsBridge.cs). Every function here has a matching extern
//  declaration in that file; the struct layouts and enum values MUST stay in sync.
//
//  All functions use cdecl calling convention (the C# side declares [DllImport(..., CallingConvention
//  = CallingConvention.Cdecl)]). Handles are opaque pointers; the C# side wraps them in typed
//  readonly structs (PhysicsSystemHandle / ShapeHandle / CharacterHandle) that carry a single
//  IntPtr and therefore marshal as a pointer-sized value.
//
//  Lifetime contract: every Create must be paired with the matching Destroy. The native library
//  owns all Jolt object lifetimes; managed code never touches Jolt directly.
// ============================================================================================
#ifndef SHANYU_JOLT_H
#define SHANYU_JOLT_H

#include <stdint.h>
#include <stddef.h>

#ifdef _WIN32
    #define SJ_CALL __cdecl
    #ifdef SHANYUJOLT_EXPORTS
        #define SJ_API __declspec(dllexport)
    #else
        #define SJ_API __declspec(dllimport)
    #endif
#else
    #define SJ_CALL
    #define SJ_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

// --------------------------------------------------------------------------------------------
//  Opaque handle types. Each maps 1:1 to a C# typed handle struct (single IntPtr field).
// --------------------------------------------------------------------------------------------
typedef struct SJ_PhysicsSystem* SJ_PhysicsSystemHandle;
typedef struct SJ_ShapeSettings* SJ_ShapeSettingsHandle;
typedef struct SJ_Shape*         SJ_ShapeHandle;
typedef struct SJ_Character*     SJ_CharacterHandle;

// --------------------------------------------------------------------------------------------
//  Blittable value types. Layout matches the C# [StructLayout(LayoutKind.Sequential, Pack=4/8)]
//  declarations exactly. Do not reorder fields.
// --------------------------------------------------------------------------------------------

typedef struct { float X, Y, Z; } SJ_Vec3;

typedef struct { float X, Y, Z, W; } SJ_Quat;

// Pack=4 on C# side.
#pragma pack(push, 4)
typedef struct {
    int32_t MaxBodies;
    int32_t MaxBodyPairs;
    int32_t MaxContactConstraints;
    int32_t NumBodyMutexes;
    int32_t BroadPhaseLayerCount;
    int32_t ObjectLayerCount;
    SJ_Vec3 Gravity;
    float   FixedTimestep;
    float   Baumgarte;
    float   SpeculativeContactDistance;
    float   PenetrationSlop;
    float   LinearCastThreshold;
    float   MaxInflightBodyPairs;
    uint8_t DeterministicSimulation;
} SJ_PhysicsSettings;

typedef struct {
    uint32_t    BodyId;
    SJ_Vec3     Position;
    SJ_Vec3     Normal;
    float       Fraction;
    float       Distance;
    uint8_t     SubShapeId;
} SJ_RaycastResult;

typedef struct {
    uint32_t BodyIdA;
    uint32_t BodyIdB;
    uint16_t SubShapeIdA;
    uint16_t SubShapeIdB;
    SJ_Vec3  AverageContactPoint;
    SJ_Vec3  AverageNormal;     // points from B to A
    float    PenetrationDepth;
    int32_t  ContactPointCount;
} SJ_ContactManifold;

typedef struct {
    uint8_t  GroundState;
    SJ_Vec3  GroundPosition;
    SJ_Vec3  GroundNormal;
    float    GroundY;
    uint32_t GroundBodyId;
    float    SlopeCosAngle;
} SJ_GroundState;

typedef struct {
    float   MaxSlopeAngle;
    float   UpStandHeight;
    float   DownStandHeight;
    float   StickToFloorDistance;
    float   WalkStairsExtraDistance;
    float   WalkStairsUpExtraHeight;
    SJ_Vec3 Up;
} SJ_CharacterUpdateSettings;
#pragma pack(pop)

// Pack=8 on C# side (NativeBodyCreationSettings).
#pragma pack(push, 8)
typedef struct {
    SJ_ShapeHandle Shape;            // immutable Shape* (from SJ_Shape_Finalize)
    SJ_Vec3  Position;
    SJ_Quat  Rotation;
    float    Mass;                   // 0 for static
    SJ_Vec3  LinearVelocity;
    SJ_Vec3  AngularVelocity;
    float    Restitution;
    float    Friction;
    float    LinearDamping;
    float    AngularDamping;
    float    MaxLinearVelocity;
    float    MaxAngularVelocity;
    float    GravityFactor;          // 1.0 = full gravity, 0.0 = none
    int32_t  MotionType;             // SJ_MotionType
    uint16_t ObjectLayer;
    uint16_t CollisionMask;
    uint8_t  IsSensor;
    uint8_t  AllowSleeping;
    uint8_t  EnhancedInternalEdgeRemoval;
} SJ_BodyCreationSettings;
#pragma pack(pop)

// --------------------------------------------------------------------------------------------
//  Enums — values must match the C# enums exactly.
// --------------------------------------------------------------------------------------------
enum SJ_MotionQuality : int32_t { SJ_MQ_Discrete = 0, SJ_MQ_LinearCast = 1 };
enum SJ_MotionType     : int32_t { SJ_MT_Static = 0, SJ_MT_Kinematic = 1, SJ_MT_Dynamic = 2 };
enum SJ_Activation     : int32_t { SJ_ACT_Activate = 0, SJ_ACT_DontActivate = 1, SJ_ACT_ActivateIfActive = 2 };
enum SJ_GroundState    : uint8_t { SJ_GS_OnGround = 0, SJ_GS_OnSteepGround = 1, SJ_GS_NotSupported = 2, SJ_GS_InAir = 3 };
enum SJ_ValidateResult : int32_t { SJ_VR_RejectAllContacts = 0, SJ_VR_RejectContact = 1, SJ_VR_AcceptAllContactsForThisBodyPair = 2, SJ_VR_AcceptContact = 3 };

// --------------------------------------------------------------------------------------------
//  Contact listener callbacks. cdecl, matching the C# [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
//  delegates. The C# caller MUST keep the delegates alive (pinned) for the duration the listener
//  is installed.
// --------------------------------------------------------------------------------------------
typedef void (SJ_CALL *SJ_ContactAddedCb)(SJ_ContactManifold manifold);
typedef void (SJ_CALL *SJ_ContactRemovedCb)(uint32_t bodyIdA, uint32_t bodyIdB);
typedef int32_t (SJ_CALL *SJ_ValidateContactCb)(uint32_t bodyIdA, uint32_t bodyIdB);
typedef void (SJ_CALL *SJ_ContactPersistedCb)(SJ_ContactManifold manifold);

// ============================================================================================
//  Library lifecycle
// ============================================================================================
SJ_API uint8_t SJ_CALL SJ_Init(int32_t maxJobs, int32_t maxBarriers, uint32_t tempAllocatorSize);
SJ_API void     SJ_CALL SJ_Shutdown();
SJ_API const char* SJ_CALL SJ_GetVersion();

// ============================================================================================
//  PhysicsSystem
// ============================================================================================
SJ_API SJ_PhysicsSystemHandle SJ_CALL SJ_PhysicsSystem_Create(const SJ_PhysicsSettings* settings);
SJ_API void                   SJ_CALL SJ_PhysicsSystem_Destroy(SJ_PhysicsSystemHandle system);
SJ_API int32_t                SJ_CALL SJ_PhysicsSystem_Step(SJ_PhysicsSystemHandle system, float deltaTime, int32_t collisionSteps);
SJ_API void                   SJ_CALL SJ_PhysicsSystem_SetGravity(SJ_PhysicsSystemHandle system, SJ_Vec3 gravity);
SJ_API SJ_Vec3                SJ_CALL SJ_PhysicsSystem_GetGravity(SJ_PhysicsSystemHandle system);
SJ_API int32_t                SJ_CALL SJ_PhysicsSystem_GetActiveBodyCount(SJ_PhysicsSystemHandle system);
SJ_API int32_t                SJ_CALL SJ_PhysicsSystem_GetBodyCount(SJ_PhysicsSystemHandle system);
SJ_API void                   SJ_CALL SJ_PhysicsSystem_SetContactListener(
    SJ_PhysicsSystemHandle system,
    SJ_ContactAddedCb addedCb,
    SJ_ContactRemovedCb removedCb,
    SJ_ValidateContactCb validateCb,
    SJ_ContactPersistedCb persistedCb);

// ============================================================================================
//  Shapes
// ============================================================================================
SJ_API SJ_ShapeSettingsHandle SJ_CALL SJ_Shape_Box_Create(SJ_Vec3 halfExtent);
SJ_API SJ_ShapeSettingsHandle SJ_CALL SJ_Shape_Sphere_Create(float radius);
SJ_API SJ_ShapeSettingsHandle SJ_CALL SJ_Shape_Capsule_Create(float halfHeightOfCylinder, float radius);
SJ_API SJ_ShapeSettingsHandle SJ_CALL SJ_Shape_Cylinder_Create(float halfHeight, float radius);
SJ_API SJ_ShapeSettingsHandle SJ_CALL SJ_Shape_Plane_Create(SJ_Vec3 normal, float constant);
SJ_API SJ_ShapeSettingsHandle SJ_CALL SJ_Shape_Mesh_Create(
    const SJ_Vec3* vertices, int32_t vertexCount,
    const uint32_t* indices, int32_t indexCount);
SJ_API SJ_ShapeSettingsHandle SJ_CALL SJ_Shape_Compound_Create();
SJ_API int32_t                SJ_CALL SJ_Compound_AddChild(SJ_ShapeSettingsHandle compound, SJ_ShapeHandle shape, SJ_Vec3 pos, SJ_Quat rot);
SJ_API SJ_ShapeHandle         SJ_CALL SJ_Shape_Finalize(SJ_ShapeSettingsHandle settings);
SJ_API void                   SJ_CALL SJ_ShapeSettings_Destroy(SJ_ShapeSettingsHandle settings);
SJ_API void                   SJ_CALL SJ_Shape_Destroy(SJ_ShapeHandle shape);

// ============================================================================================
//  Body interface
// ============================================================================================
SJ_API uint32_t  SJ_CALL SJ_Body_CreateAndAdd(SJ_PhysicsSystemHandle system, const SJ_BodyCreationSettings* settings);
SJ_API void      SJ_CALL SJ_Body_RemoveAndDestroy(SJ_PhysicsSystemHandle system, uint32_t bodyId);
SJ_API uint8_t   SJ_CALL SJ_Body_IsValid(SJ_PhysicsSystemHandle system, uint32_t bodyId);
SJ_API void      SJ_CALL SJ_Body_SetPosition(SJ_PhysicsSystemHandle system, uint32_t bodyId, SJ_Vec3 pos, SJ_Quat rot, int32_t activation);
SJ_API void      SJ_CALL SJ_Body_GetPosition(SJ_PhysicsSystemHandle system, uint32_t bodyId, SJ_Vec3* outPos, SJ_Quat* outRot);
SJ_API SJ_Vec3   SJ_CALL SJ_Body_GetPositionOnly(SJ_PhysicsSystemHandle system, uint32_t bodyId);
SJ_API SJ_Quat   SJ_CALL SJ_Body_GetRotation(SJ_PhysicsSystemHandle system, uint32_t bodyId);
SJ_API void      SJ_CALL SJ_Body_SetLinearVelocity(SJ_PhysicsSystemHandle system, uint32_t bodyId, SJ_Vec3 vel);
SJ_API SJ_Vec3   SJ_CALL SJ_Body_GetLinearVelocity(SJ_PhysicsSystemHandle system, uint32_t bodyId);
SJ_API void      SJ_CALL SJ_Body_SetAngularVelocity(SJ_PhysicsSystemHandle system, uint32_t bodyId, SJ_Vec3 vel);
SJ_API SJ_Vec3   SJ_CALL SJ_Body_GetAngularVelocity(SJ_PhysicsSystemHandle system, uint32_t bodyId);
SJ_API void      SJ_CALL SJ_Body_AddForce(SJ_PhysicsSystemHandle system, uint32_t bodyId, SJ_Vec3 force, int32_t activation);
SJ_API void      SJ_CALL SJ_Body_AddImpulse(SJ_PhysicsSystemHandle system, uint32_t bodyId, SJ_Vec3 impulse, int32_t activation);
SJ_API void      SJ_CALL SJ_Body_AddImpulseAtPosition(SJ_PhysicsSystemHandle system, uint32_t bodyId, SJ_Vec3 impulse, SJ_Vec3 worldPos, int32_t activation);
SJ_API void      SJ_CALL SJ_Body_AddTorque(SJ_PhysicsSystemHandle system, uint32_t bodyId, SJ_Vec3 torque, int32_t activation);
SJ_API void      SJ_CALL SJ_Body_SetRestitution(SJ_PhysicsSystemHandle system, uint32_t bodyId, float restitution);
SJ_API void      SJ_CALL SJ_Body_SetFriction(SJ_PhysicsSystemHandle system, uint32_t bodyId, float friction);
SJ_API float     SJ_CALL SJ_Body_GetRestitution(SJ_PhysicsSystemHandle system, uint32_t bodyId);
SJ_API float     SJ_CALL SJ_Body_GetFriction(SJ_PhysicsSystemHandle system, uint32_t bodyId);
SJ_API void      SJ_CALL SJ_Body_SetMotionType(SJ_PhysicsSystemHandle system, uint32_t bodyId, int32_t motionType, int32_t activation);
SJ_API int32_t   SJ_CALL SJ_Body_GetMotionType(SJ_PhysicsSystemHandle system, uint32_t bodyId);
SJ_API void      SJ_CALL SJ_Body_SetMotionQuality(SJ_PhysicsSystemHandle system, uint32_t bodyId, int32_t quality);
SJ_API void      SJ_CALL SJ_Body_Activate(SJ_PhysicsSystemHandle system, uint32_t bodyId);
SJ_API void      SJ_CALL SJ_Body_Deactivate(SJ_PhysicsSystemHandle system, uint32_t bodyId);
SJ_API uint8_t   SJ_CALL SJ_Body_IsActive(SJ_PhysicsSystemHandle system, uint32_t bodyId);
SJ_API void      SJ_CALL SJ_Body_SetGravityFactor(SJ_PhysicsSystemHandle system, uint32_t bodyId, float factor);
SJ_API void      SJ_CALL SJ_Body_SetIsSensor(SJ_PhysicsSystemHandle system, uint32_t bodyId, uint8_t isSensor);
SJ_API void      SJ_CALL SJ_Body_SetObjectLayer(SJ_PhysicsSystemHandle system, uint32_t bodyId, uint16_t layer, uint16_t mask);
SJ_API void      SJ_CALL SJ_Body_GetWorldBounds(SJ_PhysicsSystemHandle system, uint32_t bodyId, SJ_Vec3* outMin, SJ_Vec3* outMax);
SJ_API int32_t   SJ_CALL SJ_Body_GetUserData(SJ_PhysicsSystemHandle system, uint32_t bodyId);
SJ_API void      SJ_CALL SJ_Body_SetUserData(SJ_PhysicsSystemHandle system, uint32_t bodyId, int32_t entityId);

// ============================================================================================
//  Queries
// ============================================================================================
SJ_API uint8_t   SJ_CALL SJ_Query_Raycast(SJ_PhysicsSystemHandle system, SJ_Vec3 origin, SJ_Vec3 direction, float maxDistance, uint16_t layer, uint16_t mask, SJ_RaycastResult* outResult);
SJ_API int32_t   SJ_CALL SJ_Query_RaycastMulti(SJ_PhysicsSystemHandle system, SJ_Vec3 origin, SJ_Vec3 direction, float maxDistance, uint16_t layer, uint16_t mask, SJ_RaycastResult* outResults, int32_t maxResults);
SJ_API int32_t   SJ_CALL SJ_Query_OverlapSphere(SJ_PhysicsSystemHandle system, SJ_Vec3 center, float radius, uint16_t layer, uint16_t mask, uint32_t* outBodyIds, int32_t maxResults);
SJ_API int32_t   SJ_CALL SJ_Query_OverlapBox(SJ_PhysicsSystemHandle system, SJ_Vec3 center, SJ_Vec3 halfExtent, uint16_t layer, uint16_t mask, uint32_t* outBodyIds, int32_t maxResults);
SJ_API int32_t   SJ_CALL SJ_Query_OverlapCapsule(SJ_PhysicsSystemHandle system, SJ_Vec3 point1, SJ_Vec3 point2, float radius, uint16_t layer, uint16_t mask, uint32_t* outBodyIds, int32_t maxResults);
SJ_API int32_t   SJ_CALL SJ_Query_OverlapShape(SJ_PhysicsSystemHandle system, const SJ_Vec3* points, int32_t pointCount, SJ_Vec3 position, SJ_Quat rotation, uint16_t layer, uint16_t mask, uint32_t* outBodyIds, int32_t maxResults);
SJ_API int32_t   SJ_CALL SJ_Query_CollideShape(SJ_PhysicsSystemHandle system, SJ_ShapeHandle shape, SJ_Vec3 position, SJ_Quat rotation, uint16_t layer, uint16_t mask, SJ_RaycastResult* outContacts, int32_t maxContacts);

// ============================================================================================
//  Constraints
// ============================================================================================
SJ_API uint32_t  SJ_CALL SJ_Constraint_CreateFixed(SJ_PhysicsSystemHandle system, uint32_t bodyA, uint32_t bodyB);
SJ_API uint32_t  SJ_CALL SJ_Constraint_CreatePoint(SJ_PhysicsSystemHandle system, uint32_t bodyA, uint32_t bodyB, SJ_Vec3 pivotA, SJ_Vec3 pivotB);
SJ_API uint32_t  SJ_CALL SJ_Constraint_CreateHinge(SJ_PhysicsSystemHandle system, uint32_t bodyA, uint32_t bodyB, SJ_Vec3 pivotA, SJ_Vec3 pivotB, SJ_Vec3 hingeAxisA, SJ_Vec3 hingeAxisB);
SJ_API uint32_t  SJ_CALL SJ_Constraint_CreateSlider(SJ_PhysicsSystemHandle system, uint32_t bodyA, uint32_t bodyB, SJ_Vec3 pivotA, SJ_Vec3 pivotB, SJ_Vec3 sliderAxisA, SJ_Vec3 sliderAxisB, float minLimit, float maxLimit);
SJ_API uint32_t  SJ_CALL SJ_Constraint_CreateDistance(SJ_PhysicsSystemHandle system, uint32_t bodyA, uint32_t bodyB, SJ_Vec3 pivotA, SJ_Vec3 pivotB, float minDistance, float maxDistance);
SJ_API void      SJ_CALL SJ_Constraint_Activate(SJ_PhysicsSystemHandle system, uint32_t constraintId);
SJ_API void      SJ_CALL SJ_Constraint_Destroy(SJ_PhysicsSystemHandle system, uint32_t constraintId);
SJ_API void      SJ_CALL SJ_Constraint_Hinge_SetLimits(SJ_PhysicsSystemHandle system, uint32_t constraintId, float minAngle, float maxAngle);
SJ_API void      SJ_CALL SJ_Constraint_Slider_SetLimits(SJ_PhysicsSystemHandle system, uint32_t constraintId, float minLimit, float maxLimit);
SJ_API void      SJ_CALL SJ_Constraint_SetFrequency(SJ_PhysicsSystemHandle system, uint32_t constraintId, float frequencyHz);
SJ_API void      SJ_CALL SJ_Constraint_SetDamping(SJ_PhysicsSystemHandle system, uint32_t constraintId, float damping);
SJ_API void      SJ_CALL SJ_Constraint_SetMotorState(SJ_PhysicsSystemHandle system, uint32_t constraintId, int32_t motorState);
SJ_API void      SJ_CALL SJ_Constraint_SetTargetVelocity(SJ_PhysicsSystemHandle system, uint32_t constraintId, float velocity);
SJ_API void      SJ_CALL SJ_Constraint_SetMaxMotorForce(SJ_PhysicsSystemHandle system, uint32_t constraintId, float maxForce);

// ============================================================================================
//  Character controller (CharacterVirtual)
// ============================================================================================
SJ_API SJ_CharacterHandle SJ_CALL SJ_Character_Create(SJ_PhysicsSystemHandle system, float capsuleRadius, float capsuleHalfHeight, SJ_Vec3 position, SJ_Quat rotation, uint16_t layer, uint16_t mask, float maxSlopeAngle, float upStandHeight, float mass);
SJ_API void               SJ_CALL SJ_Character_Destroy(SJ_PhysicsSystemHandle system, SJ_CharacterHandle character);
SJ_API SJ_Vec3            SJ_CALL SJ_Character_Update(SJ_PhysicsSystemHandle system, SJ_CharacterHandle character, float dt, SJ_Vec3 gravity, SJ_Vec3 desiredVelocity, float maxWalkAngle, const SJ_CharacterUpdateSettings* settings, SJ_GroundState* outGroundState);
SJ_API void               SJ_CALL SJ_Character_SetPosition(SJ_PhysicsSystemHandle system, SJ_CharacterHandle character, SJ_Vec3 position);
SJ_API SJ_Vec3            SJ_CALL SJ_Character_GetPosition(SJ_PhysicsSystemHandle system, SJ_CharacterHandle character);
SJ_API SJ_Quat            SJ_CALL SJ_Character_GetRotation(SJ_PhysicsSystemHandle system, SJ_CharacterHandle character);
SJ_API void               SJ_CALL SJ_Character_SetRotation(SJ_PhysicsSystemHandle system, SJ_CharacterHandle character, SJ_Quat rotation);
SJ_API SJ_Vec3            SJ_CALL SJ_Character_GetLinearVelocity(SJ_PhysicsSystemHandle system, SJ_CharacterHandle character);
SJ_API void               SJ_CALL SJ_Character_SetLinearVelocity(SJ_PhysicsSystemHandle system, SJ_CharacterHandle character, SJ_Vec3 velocity);
SJ_API uint8_t            SJ_CALL SJ_Character_GetGroundState(SJ_PhysicsSystemHandle system, SJ_CharacterHandle character);
SJ_API SJ_Vec3            SJ_CALL SJ_Character_GetGroundNormal(SJ_PhysicsSystemHandle system, SJ_CharacterHandle character);
SJ_API SJ_Vec3            SJ_CALL SJ_Character_GetGroundPosition(SJ_PhysicsSystemHandle system, SJ_CharacterHandle character);
SJ_API uint32_t           SJ_CALL SJ_Character_GetGroundBodyId(SJ_PhysicsSystemHandle system, SJ_CharacterHandle character);
SJ_API void               SJ_CALL SJ_Character_SetEnhancedInternalEdgeRemoval(SJ_PhysicsSystemHandle system, SJ_CharacterHandle character, uint8_t enabled);

// ============================================================================================
//  Debug / profiling
// ============================================================================================
SJ_API int32_t   SJ_CALL SJ_Debug_GetBodyStats(SJ_PhysicsSystemHandle system, uint32_t* outBodyIds, int32_t maxCount);

// ============================================================================================
//  SmartEdgeFilter — 自研创新：按角度阈值过滤三角网格内部边鬼影碰撞。
//  Per-body settings; set before stepping. angleThresholdRadians: edges whose adjacent-triangle
//  dihedral angle is below this are treated as coplanar (their normal edge-component is suppressed).
//  Set to 0 to disable filtering for a body (Jolt's native EnhancedInternalEdgeRemoval still applies).
// ============================================================================================
SJ_API void      SJ_CALL SJ_SmartEdgeFilter_SetBodyThreshold(SJ_PhysicsSystemHandle system, uint32_t bodyId, float angleThresholdRadians);
SJ_API float     SJ_CALL SJ_SmartEdgeFilter_GetBodyThreshold(SJ_PhysicsSystemHandle system, uint32_t bodyId);
SJ_API void      SJ_CALL SJ_SmartEdgeFilter_Enable(SJ_PhysicsSystemHandle system, uint8_t enabled);

#ifdef __cplusplus
}
#endif

#endif // SHANYU_JOLT_H
