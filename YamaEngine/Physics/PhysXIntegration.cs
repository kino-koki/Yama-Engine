// ============================================================================================
//  PhysXIntegration.cs
//  yama引擎 (YamaEngine) NVIDIA PhysX 5 物理引擎集成层
// ============================================================================================
//  将 NVIDIA PhysX 5 SDK 封装为与现有 PhysicsWorld API 兼容的物理世界实现。
//  使 Runtime/Physics/PhysicsBridge 无需修改即可切换到 PhysX 后端。
// ============================================================================================

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;

namespace YamaEngine.Physics
{
    // ====== PhysX 5 P/Invoke 互操作 ======
    /// <summary>PhysX 5 原生 API P/Invoke 声明（最小子集）</summary>
    public static class PhysXNative
    {
        private const string PhysXDll = "PhysX_64.dll";
        private const string PhysXCommonDll = "PhysXCommon_64.dll";
        private const string PhysXFoundationDll = "PhysXFoundation_64.dll";
        private const string PhysXCookingDll = "PhysXCooking_64.dll";
        private const string PhysXExtensionsDll = "PhysXExtensions_64.dll";

        // Foundation
        /// <summary>PhysX 版本号（5.0）。</summary>
        public const uint PX_PHYSICS_VERSION = 0x00050000;
        [DllImport(PhysXFoundationDll)] public static extern IntPtr PxCreateFoundation(uint version, IntPtr allocator, IntPtr errorCallback);

        // Physics
        [DllImport(PhysXDll)] public static extern IntPtr PxCreatePhysics(uint version, IntPtr foundation, IntPtr tolerancesScale, bool trackOutstandingAllocations, IntPtr pvd);
        [DllImport(PhysXDll)] public static extern IntPtr PxGetPhysics();

        // Scene
        [DllImport(PhysXDll)] public static extern IntPtr phys_PxCreateScene(IntPtr physics, IntPtr sceneDesc);
        [DllImport(PhysXDll)] public static extern void PxScene_release(IntPtr scene);
        [DllImport(PhysXDll)] public static extern void PxScene_simulate(IntPtr scene, float elapsedTime);
        [DllImport(PhysXDll)] public static extern bool PxScene_fetchResults(IntPtr scene, bool block);
        [DllImport(PhysXDll)] public static extern void PxScene_addActor(IntPtr scene, IntPtr actor, IntPtr userData);
        [DllImport(PhysXDll)] public static extern void PxScene_removeActor(IntPtr scene, IntPtr actor, bool wakeOnLostTouch);
        [DllImport(PhysXDll)] public static extern void PxScene_setGravity(IntPtr scene, ref Vector3 gravity);
        [DllImport(PhysXDll)] public static extern void PxScene_getGravity(IntPtr scene, out Vector3 gravity);
        [DllImport(PhysXDll)] public static extern uint PxScene_getNbActors(IntPtr scene, uint actorType);
        [DllImport(PhysXDll)] public static extern uint PxScene_getActors(IntPtr scene, uint actorType, IntPtr[] userBuffer, uint bufferSize, uint startIndex);
        [DllImport(PhysXDll)] public static extern IntPtr PxScene_createRaycast(IntPtr scene);

        // Actor
        [DllImport(PhysXDll)] public static extern IntPtr PxRigidActor_getGlobalPose(IntPtr actor);
        [DllImport(PhysXDll)] public static extern void PxRigidActor_setGlobalPose(IntPtr actor, IntPtr pose, bool wake);
        [DllImport(PhysXDll)] public static extern void PxRigidActor_attachShape(IntPtr actor, IntPtr shape);
        [DllImport(PhysXDll)] public static extern void PxRigidActor_detachShape(IntPtr actor, IntPtr shape, bool wakeOnLostTouch);
        [DllImport(PhysXDll)] public static extern uint PxRigidActor_getNbShapes(IntPtr actor);
        [DllImport(PhysXDll)] public static extern uint PxRigidActor_getShapes(IntPtr actor, IntPtr[] userBuffer, uint bufferSize, uint startIndex);

        // Rigid Dynamic
        [DllImport(PhysXDll)] public static extern IntPtr PxCreateDynamic(IntPtr physics, IntPtr transform, IntPtr geometry, IntPtr material, float density);
        [DllImport(PhysXDll)] public static extern void PxRigidDynamic_release(IntPtr actor);
        [DllImport(PhysXDll)] public static extern void PxRigidDynamic_setLinearVelocity(IntPtr actor, ref Vector3 velocity, bool wake);
        [DllImport(PhysXDll)] public static extern void PxRigidDynamic_getLinearVelocity(IntPtr actor, out Vector3 velocity);
        [DllImport(PhysXDll)] public static extern void PxRigidDynamic_setAngularVelocity(IntPtr actor, ref Vector3 velocity, bool wake);
        [DllImport(PhysXDll)] public static extern void PxRigidDynamic_getAngularVelocity(IntPtr actor, out Vector3 velocity);
        [DllImport(PhysXDll)] public static extern void PxRigidDynamic_addForce(IntPtr actor, ref Vector3 force, uint forceMode, bool wake);
        [DllImport(PhysXDll)] public static extern void PxRigidDynamic_addTorque(IntPtr actor, ref Vector3 torque, uint forceMode, bool wake);
        [DllImport(PhysXDll)] public static extern void PxRigidDynamic_setMass(IntPtr actor, float mass);
        [DllImport(PhysXDll)] public static extern float PxRigidDynamic_getMass(IntPtr actor);
        [DllImport(PhysXDll)] public static extern void PxRigidDynamic_setLinearDamping(IntPtr actor, float damping);
        [DllImport(PhysXDll)] public static extern void PxRigidDynamic_setAngularDamping(IntPtr actor, float damping);
        [DllImport(PhysXDll)] public static extern void PxRigidDynamic_setRigidBodyFlag(IntPtr actor, uint flag, bool value);
        [DllImport(PhysXDll)] public static extern bool PxRigidDynamic_isSleeping(IntPtr actor);
        [DllImport(PhysXDll)] public static extern void PxRigidDynamic_putToSleep(IntPtr actor);
        [DllImport(PhysXDll)] public static extern void PxRigidDynamic_wakeUp(IntPtr actor);
        [DllImport(PhysXDll)] public static extern void PxRigidDynamic_setSleepThreshold(IntPtr actor, float threshold);

        // Rigid Static
        [DllImport(PhysXDll)] public static extern IntPtr PxCreateStatic(IntPtr physics, IntPtr transform, IntPtr geometry, IntPtr material);
        [DllImport(PhysXDll)] public static extern void PxRigidStatic_release(IntPtr actor);

        // Shape
        [DllImport(PhysXDll)] public static extern IntPtr PxPhysics_createShape(IntPtr physics, IntPtr geometry, IntPtr material, bool isExclusive, uint shapeFlags);
        [DllImport(PhysXDll)] public static extern void PxShape_release(IntPtr shape);
        [DllImport(PhysXDll)] public static extern void PxShape_setLocalPose(IntPtr shape, IntPtr pose);
        [DllImport(PhysXDll)] public static extern void PxShape_setSimulationFilterData(IntPtr shape, ref FilterData data);
        [DllImport(PhysXDll)] public static extern void PxShape_setQueryFilterData(IntPtr shape, ref FilterData data);
        [DllImport(PhysXDll)] public static extern void PxShape_setFlag(IntPtr shape, uint flag, bool value);

        // Material
        [DllImport(PhysXDll)] public static extern IntPtr PxPhysics_createMaterial(IntPtr physics, float staticFriction, float dynamicFriction, float restitution);
        [DllImport(PhysXDll)] public static extern void PxMaterial_release(IntPtr material);
        [DllImport(PhysXDll)] public static extern void PxMaterial_setStaticFriction(IntPtr material, float friction);
        [DllImport(PhysXDll)] public static extern void PxMaterial_setDynamicFriction(IntPtr material, float friction);
        [DllImport(PhysXDll)] public static extern void PxMaterial_setRestitution(IntPtr material, float restitution);

        // Geometry
        [DllImport(PhysXCommonDll)] public static extern IntPtr PxSphereGeometry_new(float radius);
        [DllImport(PhysXCommonDll)] public static extern IntPtr PxBoxGeometry_new(float hx, float hy, float hz);
        [DllImport(PhysXCommonDll)] public static extern IntPtr PxCapsuleGeometry_new(float radius, float halfHeight);
        [DllImport(PhysXCommonDll)] public static extern IntPtr PxPlaneGeometry_new();

        // Transform / Pose
        [DllImport(PhysXCommonDll)] public static extern IntPtr PxTransform_new(Vector3 p, Quaternion q);

        // Query
        [DllImport(PhysXDll)] public static extern bool PxScene_raycast(IntPtr scene, ref Vector3 origin, ref Vector3 unitDir, float distance, out RaycastHit hit, uint hitFlags, ref QueryFilterData filterData);
        [DllImport(PhysXDll)] public static extern uint PxScene_overlap(IntPtr scene, ref QueryFilterData filterData);
        [DllImport(PhysXDll)] public static extern uint PxScene_sweep(IntPtr scene, ref QueryFilterData filterData);

        // Cooking
        [DllImport(PhysXCookingDll)] public static extern IntPtr PxCreateCooking(uint version, IntPtr foundation, IntPtr cookingParams);
        [DllImport(PhysXCookingDll)] public static extern bool PxCooking_cookTriangleMesh(IntPtr cooking, IntPtr desc, IntPtr stream, out IntPtr condition);

        // Extensions
        [DllImport(PhysXExtensionsDll)] public static extern void PxInitExtensions(IntPtr physics, IntPtr pvd);
        [DllImport(PhysXExtensionsDll)] public static extern void PxCloseExtensions();
        [DllImport(PhysXExtensionsDll)] public static extern IntPtr PxCreatePlane(IntPtr physics, ref Vector3 normal, float distance, IntPtr material);

        // Constants
        public const uint PxActorTypeFlagRigidDynamic = 1 << 1;
        public const uint PxActorTypeFlagRigidStatic = 1 << 2;
        public const uint PxForceModeForce = 0;
        public const uint PxForceModeImpulse = 1;
        public const uint PxForceModeVelocityChange = 2;
        public const uint PxForceModeAcceleration = 3;
        public const uint PxShapeFlagSimulationShape = 1 << 0;
        public const uint PxShapeFlagSceneQueryShape = 1 << 1;
        public const uint PxShapeFlagTriggerShape = 1 << 2;
        public const uint PxRigidBodyFlagKinematic = 1 << 0;
        public const uint PxHitFlagPosition = 1 << 0;
        public const uint PxHitFlagNormal = 1 << 1;
        public const uint PxHitFlagDistance = 1 << 2;
        public const uint PxHitFlagDefault = PxHitFlagPosition | PxHitFlagNormal | PxHitFlagDistance;
    }

    [StructLayout(LayoutKind.Sequential)] public struct FilterData { public uint Word0, Word1, Word2, Word3; }
    [StructLayout(LayoutKind.Sequential)] public struct QueryFilterData { public FilterData Data; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] public struct RaycastHit
    {
        public Vector3 Position;
        public Vector3 Normal;
        public float Distance;
        public float U, V;
        public uint FaceIndex;
        public IntPtr Actor;
        public IntPtr Shape;
        public uint Flags;
    }

    // ====== 力施加模式 ======
    public enum PhysXForceMode
    {
        Force = 0,
        Impulse = 1,
        VelocityChange = 2,
        Acceleration = 3
    }

    // ====== 碰撞事件类型 ======
    public enum ContactEventType { Enter, Stay, Exit }

    // ====== PhysX 物理世界封装 ======
    /// <summary>PhysX 5 物理世界 —— 与 PhysicsWorldReal API 兼容的封装层</summary>
    public class PhysXWorld : IDisposable
    {
        // 原生句柄
        private IntPtr _foundation;
        private IntPtr _physics;
        private IntPtr _scene;
        private IntPtr _cooking;
        private IntPtr _pvd; // 调试用

        // 资源追踪
        private readonly List<IntPtr> _bodies = new();
        private readonly List<IntPtr> _statics = new();
        private readonly List<IntPtr> _shapes = new();
        private readonly Dictionary<IntPtr, IntPtr> _materials = new(); // ptr -> physics ptr
        private readonly Dictionary<int, IntPtr> _entityToActor = new(); // entityId -> PxActor
        private readonly Dictionary<IntPtr, int> _actorToEntity = new(); // PxActor -> entityId

        // 事件队列
        private readonly List<(int entityA, int entityB, ContactEventType type)> _collisionEvents = new();
        private readonly List<(int entity, int other, ContactEventType type)> _triggerEvents = new();

        // 配置
        public Vector3 Gravity { get; set; } = new Vector3(0, -9.81f, 0);
        public float FixedTimestep { get; set; } = 1.0f / 60.0f;
        public int VelocityIterations { get; set; } = 8;
        public int PositionIterations { get; set; } = 4;
        public int MaxSubSteps { get; set; } = 4;
        public bool EnableCCD { get; set; } = true;
        public bool IsPaused { get; set; } = false;

        // 状态
        public int BodyCount => _bodies.Count;
        public int StaticBodyCount => _statics.Count;
        public bool IsInitialized => _physics != IntPtr.Zero && _scene != IntPtr.Zero;

        public event Action<int, int>? OnCollisionEnter;
        public event Action<int, int>? OnCollisionStay;
        public event Action<int, int>? OnCollisionExit;
        public event Action<int, int>? OnTriggerEnter;
        public event Action<int, int>? OnTriggerExit;

        // ====== 初始化 ======
        public void Initialize()
        {
            if (IsInitialized) return;

            // 创建 Foundation (使用默认分配器)
            _foundation = PhysXNative.PxCreateFoundation(PhysXNative.PX_PHYSICS_VERSION, IntPtr.Zero, IntPtr.Zero);
            if (_foundation == IntPtr.Zero) throw new InvalidOperationException("PhysX Foundation 创建失败");

            // 创建 Physics
            _physics = PhysXNative.PxCreatePhysics(PhysXNative.PX_PHYSICS_VERSION, _foundation, IntPtr.Zero, false, IntPtr.Zero);
            if (_physics == IntPtr.Zero) throw new InvalidOperationException("PhysX Physics 创建失败");

            // 初始化 Extensions
            PhysXNative.PxInitExtensions(_physics, IntPtr.Zero);

            // 创建 Cooking
            _cooking = PhysXNative.PxCreateCooking(PhysXNative.PX_PHYSICS_VERSION, _foundation, IntPtr.Zero);

            // 创建默认材质
            _defaultMaterial = CreateMaterial(0.5f, 0.5f, 0.5f);

            // 创建 Scene
            _scene = CreateScene();
            if (_scene == IntPtr.Zero) throw new InvalidOperationException("PhysX Scene 创建失败");
        }

        private IntPtr _defaultMaterial;
        private IntPtr CreateScene()
        {
            // 使用原生 API 直接创建 SceneDesc 并创建 Scene
            // 简化：直接通过 P/Invoke 调用 createScene
            var sceneDesc = IntPtr.Zero; // 实际实现需要分配 SceneDesc 内存
            return PhysXNative.phys_PxCreateScene(_physics, sceneDesc);
        }

        // ====== 材质管理 ======
        public IntPtr CreateMaterial(float staticFriction, float dynamicFriction, float restitution)
        {
            var mat = PhysXNative.PxPhysics_createMaterial(_physics, staticFriction, dynamicFriction, restitution);
            _materials[mat] = _physics;
            return mat;
        }

        public IntPtr GetDefaultMaterial() => _defaultMaterial;

        // ====== 刚体创建 ======
        public IntPtr CreateDynamicBody(Vector3 position, Quaternion rotation, float mass, IntPtr shape, bool isKinematic = false)
        {
            var pose = PhysXNative.PxTransform_new(position, rotation);
            var actor = PhysXNative.PxCreateDynamic(_physics, pose, GetGeometryFromShape(shape), _defaultMaterial, mass > 0 ? mass : 1.0f);
            if (actor == IntPtr.Zero) return IntPtr.Zero;

            PhysXNative.PxRigidActor_attachShape(actor, shape);
            if (isKinematic) PhysXNative.PxRigidDynamic_setRigidBodyFlag(actor, PhysXNative.PxRigidBodyFlagKinematic, true);
            if (mass > 0) PhysXNative.PxRigidDynamic_setMass(actor, mass);

            PhysXNative.PxScene_addActor(_scene, actor, IntPtr.Zero);
            _bodies.Add(actor);
            return actor;
        }

        public IntPtr CreateStaticBody(Vector3 position, Quaternion rotation, IntPtr shape)
        {
            var pose = PhysXNative.PxTransform_new(position, rotation);
            var actor = PhysXNative.PxCreateStatic(_physics, pose, GetGeometryFromShape(shape), _defaultMaterial);
            if (actor == IntPtr.Zero) return IntPtr.Zero;

            PhysXNative.PxRigidActor_attachShape(actor, shape);
            PhysXNative.PxScene_addActor(_scene, actor, IntPtr.Zero);
            _statics.Add(actor);
            return actor;
        }

        private IntPtr GetGeometryFromShape(IntPtr shape) => IntPtr.Zero; // 简化占位，实际需要读取 shape 的几何体

        // ====== 形状创建 ======
        public IntPtr CreateSphereShape(float radius, IntPtr? material = null)
        {
            var geo = PhysXNative.PxSphereGeometry_new(radius);
            var mat = material ?? _defaultMaterial;
            var shape = PhysXNative.PxPhysics_createShape(_physics, geo, mat, false, PhysXNative.PxShapeFlagSimulationShape | PhysXNative.PxShapeFlagSceneQueryShape);
            if (shape != IntPtr.Zero) _shapes.Add(shape);
            return shape;
        }

        public IntPtr CreateBoxShape(Vector3 halfExtents, IntPtr? material = null)
        {
            var geo = PhysXNative.PxBoxGeometry_new(halfExtents.X, halfExtents.Y, halfExtents.Z);
            var mat = material ?? _defaultMaterial;
            var shape = PhysXNative.PxPhysics_createShape(_physics, geo, mat, false, PhysXNative.PxShapeFlagSimulationShape | PhysXNative.PxShapeFlagSceneQueryShape);
            if (shape != IntPtr.Zero) _shapes.Add(shape);
            return shape;
        }

        public IntPtr CreateCapsuleShape(float radius, float halfHeight, IntPtr? material = null)
        {
            var geo = PhysXNative.PxCapsuleGeometry_new(radius, halfHeight);
            var mat = material ?? _defaultMaterial;
            var shape = PhysXNative.PxPhysics_createShape(_physics, geo, mat, false, PhysXNative.PxShapeFlagSimulationShape | PhysXNative.PxShapeFlagSceneQueryShape);
            if (shape != IntPtr.Zero) _shapes.Add(shape);
            return shape;
        }

        public IntPtr CreateTriggerShape(IntPtr geometry, IntPtr? material = null)
        {
            var mat = material ?? _defaultMaterial;
            var shape = PhysXNative.PxPhysics_createShape(_physics, geometry, mat, false, PhysXNative.PxShapeFlagTriggerShape | PhysXNative.PxShapeFlagSceneQueryShape);
            if (shape != IntPtr.Zero) _shapes.Add(shape);
            return shape;
        }

        // ====== ECS 集成：Entity <-> Actor 映射 ======
        public void BindEntity(int entityId, IntPtr actor)
        {
            _entityToActor[entityId] = actor;
            _actorToEntity[actor] = entityId;
        }

        public IntPtr GetActorForEntity(int entityId) => _entityToActor.TryGetValue(entityId, out var actor) ? actor : IntPtr.Zero;
        public int GetEntityForActor(IntPtr actor) => _actorToEntity.TryGetValue(actor, out var entityId) ? entityId : -1;

        public void RemoveEntityBinding(int entityId)
        {
            if (_entityToActor.TryGetValue(entityId, out var actor))
            {
                _entityToActor.Remove(entityId);
                _actorToEntity.Remove(actor);
            }
        }

        // ====== 物理步进 ======
        public void Step(float deltaTime)
        {
            if (!IsInitialized || IsPaused) return;

            // 固定时间步进
            float accumulator = 0;
            accumulator += deltaTime;
            int steps = 0;
            while (accumulator >= FixedTimestep && steps < MaxSubSteps)
            {
                PhysXNative.PxScene_simulate(_scene, FixedTimestep);
                PhysXNative.PxScene_fetchResults(_scene, true);
                accumulator -= FixedTimestep;
                steps++;
            }

            // 处理碰撞事件（简化：实际应通过 PhysX 的 simulation event callback）
            ProcessCollisionEvents();
        }

        private void ProcessCollisionEvents()
        {
            //  PhysX 碰撞事件需通过 PxSimulationEventCallback 获取
            //  此处为简化实现，实际需注册回调并从回调中提取 contact pairs
            foreach (var ev in _collisionEvents)
            {
                switch (ev.type)
                {
                    case ContactEventType.Enter: OnCollisionEnter?.Invoke(ev.entityA, ev.entityB); break;
                    case ContactEventType.Stay: OnCollisionStay?.Invoke(ev.entityA, ev.entityB); break;
                    case ContactEventType.Exit: OnCollisionExit?.Invoke(ev.entityA, ev.entityB); break;
                }
            }
            _collisionEvents.Clear();

            foreach (var ev in _triggerEvents)
            {
                switch (ev.type)
                {
                    case ContactEventType.Enter: OnTriggerEnter?.Invoke(ev.entity, ev.other); break;
                    case ContactEventType.Exit: OnTriggerExit?.Invoke(ev.entity, ev.other); break;
                }
            }
            _triggerEvents.Clear();
        }

        // ====== 查询 ======
        public bool Raycast(Vector3 origin, Vector3 direction, float maxDistance, out RaycastHit hit)
        {
            hit = default;
            if (!IsInitialized) return false;
            var filter = new QueryFilterData();
            return PhysXNative.PxScene_raycast(_scene, ref origin, ref direction, maxDistance, out hit, PhysXNative.PxHitFlagDefault, ref filter);
        }

        // ====== 刚体操作 ======
        public void SetActorPosition(IntPtr actor, Vector3 position)
        {
            var pose = PhysXNative.PxTransform_new(position, Quaternion.Identity);
            PhysXNative.PxRigidActor_setGlobalPose(actor, pose, true);
        }

        public void SetActorRotation(IntPtr actor, Quaternion rotation)
        {
            var pos = GetActorPosition(actor);
            var pose = PhysXNative.PxTransform_new(pos, rotation);
            PhysXNative.PxRigidActor_setGlobalPose(actor, pose, true);
        }

        public Vector3 GetActorPosition(IntPtr actor)
        {
            var pose = PhysXNative.PxRigidActor_getGlobalPose(actor);
            // 实际应从 PxTransform 结构体读取位置
            return Vector3.Zero; // 占位：需实现结构体解包
        }

        public void SetLinearVelocity(IntPtr actor, Vector3 velocity) => PhysXNative.PxRigidDynamic_setLinearVelocity(actor, ref velocity, true);
        public void GetLinearVelocity(IntPtr actor, out Vector3 velocity) => PhysXNative.PxRigidDynamic_getLinearVelocity(actor, out velocity);
        public void SetAngularVelocity(IntPtr actor, Vector3 velocity) => PhysXNative.PxRigidDynamic_setAngularVelocity(actor, ref velocity, true);
        public void AddForce(IntPtr actor, Vector3 force, PhysXForceMode mode = PhysXForceMode.Force) => PhysXNative.PxRigidDynamic_addForce(actor, ref force, (uint)mode, true);
        public void AddTorque(IntPtr actor, Vector3 torque, PhysXForceMode mode = PhysXForceMode.Force) => PhysXNative.PxRigidDynamic_addTorque(actor, ref torque, (uint)mode, true);
        public void SetMass(IntPtr actor, float mass) => PhysXNative.PxRigidDynamic_setMass(actor, mass);
        public float GetMass(IntPtr actor) => PhysXNative.PxRigidDynamic_getMass(actor);
        public void SetLinearDamping(IntPtr actor, float damping) => PhysXNative.PxRigidDynamic_setLinearDamping(actor, damping);
        public void SetAngularDamping(IntPtr actor, float damping) => PhysXNative.PxRigidDynamic_setAngularDamping(actor, damping);
        public bool IsSleeping(IntPtr actor) => PhysXNative.PxRigidDynamic_isSleeping(actor);
        public void WakeUp(IntPtr actor) => PhysXNative.PxRigidDynamic_wakeUp(actor);
        public void PutToSleep(IntPtr actor) => PhysXNative.PxRigidDynamic_putToSleep(actor);

        // ====== 销毁 ======
        public void DestroyActor(IntPtr actor)
        {
            if (actor == IntPtr.Zero) return;
            PhysXNative.PxScene_removeActor(_scene, actor, false);
            if (_bodies.Contains(actor)) { _bodies.Remove(actor); PhysXNative.PxRigidDynamic_release(actor); }
            else if (_statics.Contains(actor)) { _statics.Remove(actor); PhysXNative.PxRigidStatic_release(actor); }
            if (_actorToEntity.TryGetValue(actor, out var eid)) RemoveEntityBinding(eid);
        }

        public void DestroyShape(IntPtr shape)
        {
            if (shape == IntPtr.Zero) return;
            _shapes.Remove(shape);
            PhysXNative.PxShape_release(shape);
        }

        // ====== 清理 ======
        public void Clear()
        {
            foreach (var actor in _bodies.ToArray()) DestroyActor(actor);
            foreach (var actor in _statics.ToArray()) DestroyActor(actor);
            _bodies.Clear(); _statics.Clear();
            foreach (var shape in _shapes.ToArray()) DestroyShape(shape);
            _shapes.Clear();
            _entityToActor.Clear(); _actorToEntity.Clear();
        }

        public void Dispose()
        {
            Clear();
            if (_scene != IntPtr.Zero) { PhysXNative.PxScene_release(_scene); _scene = IntPtr.Zero; }
            if (_cooking != IntPtr.Zero) { /* PxCooking_release */ _cooking = IntPtr.Zero; }
            PhysXNative.PxCloseExtensions();
            // 注意：Foundation 和 Physics 的释放顺序需遵循 PhysX 生命周期
            _physics = IntPtr.Zero;
            _foundation = IntPtr.Zero;
        }
    }

    // ====== PhysX 物理系统（单例管理器） ======
    /// <summary>PhysX 物理系统管理器 —— 与现有 PhysicsSystem 接口兼容</summary>
    public class PhysXPhysicsSystem
    {
        public static PhysXPhysicsSystem Instance { get; } = new();
        public PhysXWorld World { get; private set; } = new();
        public bool IsRunning => World.IsInitialized;

        public void Initialize()
        {
            if (IsRunning) return;
            World.Initialize();
        }

        public void Update(float deltaTime)
        {
            if (!IsRunning) return;
            World.Step(deltaTime);
        }

        public void Shutdown()
        {
            World.Dispose();
            World = new PhysXWorld();
        }

        public void SetGravity(Vector3 gravity) => World.Gravity = gravity;
        public Vector3 GetGravity() => World.Gravity;
        public void Pause() => World.IsPaused = true;
        public void Resume() => World.IsPaused = false;
        public int GetBodyCount() => World.BodyCount;
        public int GetStaticBodyCount() => World.StaticBodyCount;
    }
}
