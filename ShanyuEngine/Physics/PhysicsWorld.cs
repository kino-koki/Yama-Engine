// ============================================================================================
//  PhysicsWorld.cs — Jolt 物理世界的 C# 管理封装。
//
//  这是引擎物理的统一入口，封装 JoltPhysicsBridge 的 P/Invoke 调用，提供面向对象的 API。
//  所有 Jolt 对象生命周期由本类管理：Create 与 Destroy 配对。
//
//  四项自研创新可作为可选模块挂接：
//    - AdaptiveSubstepper   ：StepAdaptive 自动子步
//    - OriginShiftCoordinator：大世界流动原点
//    - StackStabilizer      ：堆叠微震抑制
//    - SmartEdgeFilter（native 侧）：通过 SetEdgeFilterThreshold 配置
//
//  生命周期：
//    using var world = new PhysicsWorld(PhysicsWorldSettings.Default);
//    var box = world.CreateBoxShape(new Vector3(0.5f));
//    uint id = world.AddDynamicBody(box, position: new Vector3(0,10,0), mass: 1f);
//    world.Step(1f/60f);
// ============================================================================================
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;

namespace ShanyuEngine.Physics
{
    /// <summary>
    /// 物理世界配置。映射到 native SJ_PhysicsSettings。
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public class PhysicsWorldSettings
    {
        public int MaxBodies = 4096;
        public int MaxBodyPairs = 65536;
        public int MaxContactConstraints = 8192;
        public Vector3 Gravity = new(0, -9.81f, 0);
        public float FixedTimestep = 1f / 60f;
        public float Baumgarte = 0.2f;
        public float SpeculativeContactDistance = 0.02f;
        public float PenetrationSlop = 0.02f;
        public float LinearCastThreshold = 0.5f;
        public bool Deterministic = false;

        public static PhysicsWorldSettings Default => new();
    }

    /// <summary>
    /// Jolt 物理世界。封装 PhysicsSystemHandle，提供 body/shape/query/constraint/character 全套 API。
    /// </summary>
    public sealed class PhysicsWorld : IDisposable
    {
        private PhysicsSystemHandle _handle;
        private bool _disposed;
        private bool _ownsSystem;
        private readonly List<uint> _bodyIds = new();
        private readonly List<NativeContactManifold> _contactManifolds = new();

        // 接触回调 delegate 必须保持存活（P/Invoke 需要）
        private ContactAddedCallback? _addedCb;
        private ContactPersistedCallback? _persistedCb;
        private ContactRemovedCallback? _removedCb;
        private ValidateContactCallback? _validateCb;

        // 可选创新模块
        private AdaptiveSubstepper? _adaptive;
        private OriginShiftCoordinator? _originShift;
        private StackStabilizer? _stabilizer;

        /// <summary>Native PhysicsSystem 句柄（供 Unity* 系统等直接调用 JoltNative 用）。</summary>
        public PhysicsSystemHandle NativeHandle => _handle;

        /// <summary>固定时间步长（秒）。Step(dt) 用此做累积；StepAdaptive 用此做子步基准。</summary>
        public float FixedTimestep { get; set; } = 1f / 60f;

        /// <summary>重力向量。</summary>
        public Vector3 Gravity
        {
            get => JoltNative.SJ_PhysicsSystem_GetGravity(_handle).ToVector3();
            set => JoltNative.SJ_PhysicsSystem_SetGravity(_handle, value.ToNative());
        }

        /// <summary>当前体总数（含静态）。</summary>
        public int BodyCount => JoltNative.SJ_PhysicsSystem_GetBodyCount(_handle);

        /// <summary>当前活动（非睡眠）体数。</summary>
        public int ActiveBodyCount => JoltNative.SJ_PhysicsSystem_GetActiveBodyCount(_handle);

        /// <summary>本帧接触流形列表（由接触回调填充，供上层 CollisionEventSystem / 角色 推断接触）。</summary>
        public IReadOnlyList<NativeContactManifold> ContactManifolds => _contactManifolds;

        // ========================================================================================
        //  构造 / 销毁
        // ========================================================================================

        /// <summary>创建物理世界并初始化 Jolt。自动调用 SJ_Init。</summary>
        public PhysicsWorld(PhysicsWorldSettings? settings = null)
        {
            settings ??= PhysicsWorldSettings.Default;
            JoltNative.SJ_Init(0, 0, 0);
            _ownsSystem = true;
            FixedTimestep = settings.FixedTimestep;

            var ns = new NativePhysicsSettings
            {
                MaxBodies = settings.MaxBodies,
                MaxBodyPairs = settings.MaxBodyPairs,
                MaxContactConstraints = settings.MaxContactConstraints,
                NumBodyMutexes = 0,
                BroadPhaseLayerCount = 1,
                ObjectLayerCount = 16,
                Gravity = settings.Gravity.ToNative(),
                FixedTimestep = settings.FixedTimestep,
                Baumgarte = settings.Baumgarte,
                SpeculativeContactDistance = settings.SpeculativeContactDistance,
                PenetrationSlop = settings.PenetrationSlop,
                LinearCastThreshold = settings.LinearCastThreshold,
                MaxInflightBodyPairs = settings.MaxBodyPairs,
                DeterministicSimulation = (byte)(settings.Deterministic ? 1 : 0),
            };
            _handle = JoltNative.SJ_PhysicsSystem_Create(in ns);

            // 安装接触回调（捕获到本实例）
            _addedCb = OnContactAdded;
            _persistedCb = OnContactPersisted;
            _removedCb = OnContactRemoved;
            _validateCb = OnContactValidate;
            JoltNative.SJ_PhysicsSystem_SetContactListener(
                _handle,
                Marshal.GetFunctionPointerForDelegate(_addedCb),
                Marshal.GetFunctionPointerForDelegate(_removedCb),
                Marshal.GetFunctionPointerForDelegate(_validateCb),
                Marshal.GetFunctionPointerForDelegate(_persistedCb));
        }

        ~PhysicsWorld() => Dispose(false);

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private void Dispose(bool disposing)
        {
            if (_disposed) return;
            _disposed = true;
            if (_ownsSystem && _handle.IsValid)
            {
                JoltNative.SJ_PhysicsSystem_Destroy(_handle);
            }
            _handle = default;
            if (disposing)
            {
                _bodyIds.Clear();
                _contactManifolds.Clear();
            }
            // 仅当本实例 init 了 Jolt 才 shutdown。
            // 注意：多世界共享 Jolt 时应外部统一 Shutdown；此处按单世界简化。
            if (_ownsSystem) JoltNative.SJ_Shutdown();
        }

        // ========================================================================================
        //  步进
        // ========================================================================================

        /// <summary>以指定子步数步进。</summary>
        public void Step(float deltaTime, int collisionSteps)
        {
            if (_disposed) return;
            _originShift?.BeforeStep();
            JoltNative.SJ_PhysicsSystem_Step(_handle, deltaTime, Math.Max(1, collisionSteps));
            _stabilizer?.AfterStep(this, deltaTime);
            _originShift?.AfterStep();
        }

        /// <summary>以固定步长步进（默认 1 子步）。若挂接了 AdaptiveSubstepper，用 StepAdaptive 自动子步。</summary>
        public void Step(float deltaTime)
        {
            if (_adaptive != null)
                _adaptive.StepAdaptive(this, deltaTime);
            else
                Step(deltaTime, 1);
        }

        // ========================================================================================
        //  Shape 创建
        // ========================================================================================
        public ShapeHandle CreateBoxShape(Vector3 halfExtent)
            => Finalize(JoltNative.SJ_Shape_Box_Create(halfExtent.ToNative()));
        public ShapeHandle CreateSphereShape(float radius)
            => Finalize(JoltNative.SJ_Shape_Sphere_Create(radius));
        public ShapeHandle CreateCapsuleShape(float halfHeight, float radius)
            => Finalize(JoltNative.SJ_Shape_Capsule_Create(halfHeight, radius));
        public ShapeHandle CreateCylinderShape(float halfHeight, float radius)
            => Finalize(JoltNative.SJ_Shape_Cylinder_Create(halfHeight, radius));
        public ShapeHandle CreatePlaneShape(Vector3 normal, float constant)
            => Finalize(JoltNative.SJ_Shape_Plane_Create(normal.ToNative(), constant));

        /// <summary>创建三角网格 shape（用于静态碰撞网格）。</summary>
        public ShapeHandle CreateMeshShape(ReadOnlySpan<Vector3> vertices, ReadOnlySpan<uint> indices)
        {
            NativeVec3[] nv = new NativeVec3[vertices.Length];
            for (int i = 0; i < vertices.Length; i++) nv[i] = vertices[i].ToNative();
            uint[] idx = indices.ToArray();
            return Finalize(JoltNative.SJ_Shape_Mesh_Create(nv, nv.Length, idx, idx.Length));
        }

        private static ShapeHandle Finalize(ShapeSettingsHandle settings)
        {
            try { return JoltNative.SJ_Shape_Finalize(settings); }
            finally { JoltNative.SJ_ShapeSettings_Destroy(settings); }
        }

        // ========================================================================================
        //  Body 创建 / 销毁
        // ========================================================================================

        /// <summary>添加动态体。</summary>
        public uint AddDynamicBody(ShapeHandle shape, Vector3 position, float mass,
            Quaternion? rotation = null, uint layer = 1, uint mask = 0xFFFF,
            float restitution = 0.2f, float friction = 0.6f)
        {
            return AddBody(shape, position, rotation ?? Quaternion.Identity,
                mass, BodyMotionType.Dynamic, layer, mask, restitution, friction);
        }

        /// <summary>添加静态体（如地面/网格）。</summary>
        public uint AddStaticBody(ShapeHandle shape, Vector3 position, Quaternion? rotation = null,
            uint layer = 1, uint mask = 0xFFFF, float restitution = 0.2f, float friction = 0.6f)
        {
            return AddBody(shape, position, rotation ?? Quaternion.Identity,
                0f, BodyMotionType.Static, layer, mask, restitution, friction);
        }

        /// <summary>添加运动学体（由代码驱动位置，不受力影响）。</summary>
        public uint AddKinematicBody(ShapeHandle shape, Vector3 position, Quaternion? rotation = null,
            uint layer = 1, uint mask = 0xFFFF, float restitution = 0.2f, float friction = 0.6f)
        {
            return AddBody(shape, position, rotation ?? Quaternion.Identity,
                0f, BodyMotionType.Kinematic, layer, mask, restitution, friction);
        }

        public uint AddBody(ShapeHandle shape, Vector3 position, Quaternion rotation,
            float mass, BodyMotionType motionType, uint layer, uint mask,
            float restitution, float friction)
        {
            var bcs = new NativeBodyCreationSettings
            {
                Shape = shape.Value,
                Position = position.ToNative(),
                Rotation = rotation.ToNative(),
                Mass = mass,
                LinearVelocity = default,
                AngularVelocity = default,
                Restitution = restitution,
                Friction = friction,
                LinearDamping = 0.05f,
                AngularDamping = 0.25f,
                MaxLinearVelocity = 500f,
                MaxAngularVelocity = 64f,
                GravityFactor = 1f,
                MotionType = (int)ToSJ(motionType),
                ObjectLayer = (ushort)layer,
                CollisionMask = (ushort)mask,
                IsSensor = 0,
                AllowSleeping = 1,
                EnhancedInternalEdgeRemoval = 1, // 配合 SmartEdgeFilter
            };
            uint id = JoltNative.SJ_Body_CreateAndAdd(_handle, in bcs);
            if (id != 0) _bodyIds.Add(id);
            return id;
        }

        public void RemoveBody(uint bodyId)
        {
            JoltNative.SJ_Body_RemoveAndDestroy(_handle, bodyId);
            _bodyIds.Remove(bodyId);
        }

        public bool IsValid(uint bodyId) => JoltNative.SJ_Body_IsValid(_handle, bodyId);

        /// <summary>本世界管理的所有 body ID（含已移除前的快照）。</summary>
        public IEnumerable<uint> GetAllBodyIds() => _bodyIds;

        // ========================================================================================
        //  Body 属性 Get/Set
        // ========================================================================================
        public Vector3 GetPosition(uint bodyId) => JoltNative.SJ_Body_GetPositionOnly(_handle, bodyId).ToVector3();
        public Quaternion GetRotation(uint bodyId) => JoltNative.SJ_Body_GetRotation(_handle, bodyId).ToQuaternion();

        public void SetPosition(uint bodyId, Vector3 position, Quaternion rotation, bool activate = true)
            => JoltNative.SJ_Body_SetPosition(_handle, bodyId, position.ToNative(), rotation.ToNative(),
                activate ? (int)SJ_Activation.Activate : (int)SJ_Activation.DontActivate);

        public Vector3 GetLinearVelocity(uint bodyId) => JoltNative.SJ_Body_GetLinearVelocity(_handle, bodyId).ToVector3();
        public void SetLinearVelocity(uint bodyId, Vector3 velocity) => JoltNative.SJ_Body_SetLinearVelocity(_handle, bodyId, velocity.ToNative());

        public Vector3 GetAngularVelocity(uint bodyId) => JoltNative.SJ_Body_GetAngularVelocity(_handle, bodyId).ToVector3();
        public void SetAngularVelocity(uint bodyId, Vector3 velocity) => JoltNative.SJ_Body_SetAngularVelocity(_handle, bodyId, velocity.ToNative());

        public void AddForce(uint bodyId, Vector3 force, bool activate = true)
            => JoltNative.SJ_Body_AddForce(_handle, bodyId, force.ToNative(), activate ? (int)SJ_Activation.Activate : (int)SJ_Activation.DontActivate);
        public void AddImpulse(uint bodyId, Vector3 impulse, bool activate = true)
            => JoltNative.SJ_Body_AddImpulse(_handle, bodyId, impulse.ToNative(), activate ? (int)SJ_Activation.Activate : (int)SJ_Activation.DontActivate);
        public void AddImpulseAtPosition(uint bodyId, Vector3 impulse, Vector3 worldPos, bool activate = true)
            => JoltNative.SJ_Body_AddImpulseAtPosition(_handle, bodyId, impulse.ToNative(), worldPos.ToNative(), activate ? (int)SJ_Activation.Activate : (int)SJ_Activation.DontActivate);
        public void AddTorque(uint bodyId, Vector3 torque, bool activate = true)
            => JoltNative.SJ_Body_AddTorque(_handle, bodyId, torque.ToNative(), activate ? (int)SJ_Activation.Activate : (int)SJ_Activation.DontActivate);

        public float Restitution { set => JoltNative.SJ_Body_SetRestitution(_handle, 0, value); }
        public void SetRestitution(uint bodyId, float r) => JoltNative.SJ_Body_SetRestitution(_handle, bodyId, r);
        public void SetFriction(uint bodyId, float f) => JoltNative.SJ_Body_SetFriction(_handle, bodyId, f);
        public float GetRestitution(uint bodyId) => JoltNative.SJ_Body_GetRestitution(_handle, bodyId);
        public float GetFriction(uint bodyId) => JoltNative.SJ_Body_GetFriction(_handle, bodyId);

        public void SetMotionType(uint bodyId, BodyMotionType type, bool activate = true)
            => JoltNative.SJ_Body_SetMotionType(_handle, bodyId, (int)ToSJ(type), activate ? (int)SJ_Activation.Activate : (int)SJ_Activation.DontActivate);
        public BodyMotionType GetMotionType(uint bodyId)
            => FromSJ((SJ_MotionType)JoltNative.SJ_Body_GetMotionType(_handle, bodyId));

        public void SetMotionQuality(uint bodyId, SJ_MotionQuality quality) => JoltNative.SJ_Body_SetMotionQuality(_handle, bodyId, (int)quality);
        public void ActivateBody(uint bodyId) => JoltNative.SJ_Body_Activate(_handle, bodyId);
        public void DeactivateBody(uint bodyId) => JoltNative.SJ_Body_Deactivate(_handle, bodyId);
        public bool IsBodyActive(uint bodyId) => JoltNative.SJ_Body_IsActive(_handle, bodyId);
        public void SetGravityFactor(uint bodyId, float factor) => JoltNative.SJ_Body_SetGravityFactor(_handle, bodyId, factor);
        public void SetIsSensor(uint bodyId, bool sensor) => JoltNative.SJ_Body_SetIsSensor(_handle, bodyId, sensor);
        public void SetObjectLayer(uint bodyId, uint layer, uint mask) => JoltNative.SJ_Body_SetObjectLayer(_handle, bodyId, (ushort)layer, (ushort)mask);

        public (Vector3 min, Vector3 max) GetWorldBounds(uint bodyId)
        {
            JoltNative.SJ_Body_GetWorldBounds(_handle, bodyId, out var mn, out var mx);
            return (mn.ToVector3(), mx.ToVector3());
        }

        public int GetUserData(uint bodyId) => JoltNative.SJ_Body_GetUserData(_handle, bodyId);
        public void SetUserData(uint bodyId, int entityId) => JoltNative.SJ_Body_SetUserData(_handle, bodyId, entityId);

        // ========================================================================================
        //  查询
        // ========================================================================================
        public bool Raycast(Vector3 origin, Vector3 direction, float maxDistance,
            uint mask = 0xFFFF, out NativeRaycastResult hit)
        {
            var r = new NativeRaycastResult();
            bool ok = JoltNative.SJ_Query_Raycast(_handle, origin.ToNative(), direction.ToNative(),
                maxDistance, 1, (ushort)mask, out r);
            hit = r;
            return ok;
        }

        public int RaycastMulti(Vector3 origin, Vector3 direction, float maxDistance,
            NativeRaycastResult[] results, uint mask = 0xFFFF)
            => JoltNative.SJ_Query_RaycastMulti(_handle, origin.ToNative(), direction.ToNative(),
                maxDistance, 1, (ushort)mask, results, results.Length);

        public int OverlapSphere(Vector3 center, float radius, uint[] bodyIds, uint mask = 0xFFFF)
            => JoltNative.SJ_Query_OverlapSphere(_handle, center.ToNative(), radius, 1, (ushort)mask, bodyIds, bodyIds.Length);

        public int OverlapBox(Vector3 center, Vector3 halfExtent, uint[] bodyIds, uint mask = 0xFFFF)
            => JoltNative.SJ_Query_OverlapBox(_handle, center.ToNative(), halfExtent.ToNative(), 1, (ushort)mask, bodyIds, bodyIds.Length);

        public int OverlapCapsule(Vector3 p1, Vector3 p2, float radius, uint[] bodyIds, uint mask = 0xFFFF)
            => JoltNative.SJ_Query_OverlapCapsule(_handle, p1.ToNative(), p2.ToNative(), radius, 1, (ushort)mask, bodyIds, bodyIds.Length);

        // ========================================================================================
        //  约束
        // ========================================================================================
        public uint CreateFixedConstraint(uint bodyA, uint bodyB) => JoltNative.SJ_Constraint_CreateFixed(_handle, bodyA, bodyB);
        public uint CreatePointConstraint(uint bodyA, uint bodyB, Vector3 pivotA, Vector3 pivotB)
            => JoltNative.SJ_Constraint_CreatePoint(_handle, bodyA, bodyB, pivotA.ToNative(), pivotB.ToNative());
        public uint CreateHingeConstraint(uint bodyA, uint bodyB, Vector3 pivotA, Vector3 pivotB, Vector3 axisA, Vector3 axisB)
            => JoltNative.SJ_Constraint_CreateHinge(_handle, bodyA, bodyB, pivotA.ToNative(), pivotB.ToNative(), axisA.ToNative(), axisB.ToNative());
        public uint CreateSliderConstraint(uint bodyA, uint bodyB, Vector3 pivotA, Vector3 pivotB, Vector3 axisA, Vector3 axisB, float minLimit, float maxLimit)
            => JoltNative.SJ_Constraint_CreateSlider(_handle, bodyA, bodyB, pivotA.ToNative(), pivotB.ToNative(), axisA.ToNative(), axisB.ToNative(), minLimit, maxLimit);
        public uint CreateDistanceConstraint(uint bodyA, uint bodyB, Vector3 pivotA, Vector3 pivotB, float minDist, float maxDist)
            => JoltNative.SJ_Constraint_CreateDistance(_handle, bodyA, bodyB, pivotA.ToNative(), pivotB.ToNative(), minDist, maxDist);

        public void DestroyConstraint(uint constraintId) => JoltNative.SJ_Constraint_Destroy(_handle, constraintId);
        public void SetHingeLimits(uint constraintId, float minAngle, float maxAngle) => JoltNative.SJ_Constraint_Hinge_SetLimits(_handle, constraintId, minAngle, maxAngle);
        public void SetSliderLimits(uint constraintId, float min, float max) => JoltNative.SJ_Constraint_Slider_SetLimits(_handle, constraintId, min, max);
        public void SetConstraintMotorState(uint constraintId, int motorState) => JoltNative.SJ_Constraint_SetMotorState(_handle, constraintId, motorState);
        public void SetConstraintTargetVelocity(uint constraintId, float velocity) => JoltNative.SJ_Constraint_SetTargetVelocity(_handle, constraintId, velocity);
        public void SetConstraintMaxMotorForce(uint constraintId, float maxForce) => JoltNative.SJ_Constraint_SetMaxMotorForce(_handle, constraintId, maxForce);

        // ========================================================================================
        //  SmartEdgeFilter 配置（自研创新 1）
        // ========================================================================================
        /// <summary>为某 body 设置边过滤角度阈值（弧度）。邻接三角面二面角低于此值的边视为近共面，压掉鬼影碰撞。</summary>
        public void SetEdgeFilterThreshold(uint bodyId, float angleThresholdRadians)
            => JoltNative.SJ_SmartEdgeFilter_SetBodyThreshold(_handle, bodyId, angleThresholdRadians);
        public float GetEdgeFilterThreshold(uint bodyId)
            => JoltNative.SJ_SmartEdgeFilter_GetBodyThreshold(_handle, bodyId);
        public void EnableEdgeFilter(bool enabled)
            => JoltNative.SJ_SmartEdgeFilter_Enable(_handle, enabled);

        // ========================================================================================
        //  创新模块挂接
        // ========================================================================================
        /// <summary>挂接自适应子步进器。挂接后 Step(dt) 自动用 StepAdaptive。</summary>
        public AdaptiveSubstepper UseAdaptiveSubstepping(AdaptiveSubstepper? existing = null)
        {
            _adaptive = existing ?? new AdaptiveSubstepper();
            return _adaptive;
        }

        /// <summary>挂接大世界流动原点协调器。</summary>
        public OriginShiftCoordinator UseOriginShift(Vector3? initialOrigin = null, OriginShiftCoordinator? existing = null)
        {
            _originShift = existing ?? new OriginShiftCoordinator(this, initialOrigin ?? Vector3.Zero);
            return _originShift;
        }

        /// <summary>挂接堆叠稳定器。</summary>
        public StackStabilizer UseStackStabilizer(StackStabilizer? existing = null)
        {
            _stabilizer = existing ?? new StackStabilizer();
            return _stabilizer;
        }

        /// <summary>获取（或创建）流动原点协调器，供外部查询当前原点。</summary>
        public OriginShiftCoordinator? OriginShift => _originShift;

        // ========================================================================================
        //  接触回调（转发到事件 + 流形列表）
        // ========================================================================================
        public event Action<NativeContactManifold>? OnCollisionEnter;
        public event Action<NativeContactManifold>? OnCollisionStay;
        public event Action<uint, uint>? OnCollisionExit;

        private void OnContactAdded(NativeContactManifold manifold)
        {
            _contactManifolds.Add(manifold);
            OnCollisionEnter?.Invoke(manifold);
        }
        private void OnContactPersisted(NativeContactManifold manifold)
        {
            _contactManifolds.Add(manifold);
            OnCollisionStay?.Invoke(manifold);
        }
        private void OnContactRemoved(uint a, uint b) => OnCollisionExit?.Invoke(a, b);
        private int OnContactValidate(uint a, uint b) => (int)SJ_ValidateResult.AcceptContact;

        /// <summary>每帧 Step 前清空接触流形列表（供上层在 Step 后读取本帧接触）。</summary>
        public void ClearContactManifolds() => _contactManifolds.Clear();

        // ========================================================================================
        //  BodyMotionType ↔ SJ_MotionType 映射
        // ========================================================================================
        private static SJ_MotionType ToSJ(BodyMotionType t) => t switch
        {
            BodyMotionType.Static => SJ_MotionType.Static,
            BodyMotionType.Kinematic => SJ_MotionType.Kinematic,
            BodyMotionType.Dynamic => SJ_MotionType.Dynamic,
            _ => SJ_MotionType.Dynamic,
        };
        private static BodyMotionType FromSJ(SJ_MotionType t) => t switch
        {
            SJ_MotionType.Static => BodyMotionType.Static,
            SJ_MotionType.Kinematic => BodyMotionType.Kinematic,
            _ => BodyMotionType.Dynamic,
        };
    }

    // ============================================================================================
    //  NativeVec3 / NativeQuat 与 System.Numerics 的转换扩展
    // ============================================================================================
    internal static class NativeVectorExtensions
    {
        public static Vector3 ToVector3(this NativeVec3 v) => new(v.X, v.Y, v.Z);
        public static Quaternion ToQuaternion(this NativeQuat q) => new(q.X, q.Y, q.Z, q.W);
        public static NativeVec3 ToNative(this Vector3 v) => new() { X = v.X, Y = v.Y, Z = v.Z };
        public static NativeQuat ToNative(this Quaternion q) => new() { X = q.X, Y = q.Y, Z = q.Z, W = q.W };
    }
}
