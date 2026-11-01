using System;
using System.Numerics;
using System.Runtime.InteropServices;

namespace YamaEngine.Physics
{
    // ============================================================================================
    //  JoltPhysicsBridge.cs
    //
    //  Low-level P/Invoke bindings to the native "YamaJolt" C++ wrapper library, which wraps
    //  Jolt Physics (https://github.com/jrouwe/JoltPhysics). The native library exposes a flat
    //  C ABI so that it can be consumed from C# via P/Invoke.
    //
    //  Architecture:
    //
    //     +-------------------+        P/Invoke         +-------------------+
    //     | PhysicsWorld.cs   |  <====================> | YamaJolt.dll    |
    //     | (managed, safe)   |     (this file)         | (native C++ ABI)  |
    //     +-------------------+                         |   -> JoltPhysics  |
    //                                                   +-------------------+
    //
    //  All handles returned from native are opaque pointers (System.IntPtr) encapsulated in
    //  typed handle structs to prevent accidental misuse. The native library is responsible
    //  for all Jolt object lifetimes; managed code must pair every Create with a Destroy.
    //
    //  Native library name:  YamaJolt   (resolves to YamaJolt.dll on Windows)
    //
    //  The native binary must be placed alongside the managed DLL (in the runtimes/win-x64/native
    //  folder or the application base directory) so the default loader can find it.
    // ============================================================================================

    /// <summary>
    ///   Name of the native Jolt wrapper library (without extension). The loader resolves this
    ///   to "YamaJolt.dll" on Windows.
    /// </summary>
    internal static class JoltNativeLib
    {
        public const string Name = "YamaJolt";
    }

    // --------------------------------------------------------------------------------------------
    //  Native interop value-types (blittable, LayoutKind.Sequential so they match the C struct
    //  memory layout exactly). These mirror the structs defined on the native side.
    // --------------------------------------------------------------------------------------------

    /// <summary>Native Vector3 - blittable counterpart of System.Numerics.Vector3.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct NativeVec3
    {
        public float X;
        public float Y;
        public float Z;

        public NativeVec3(float x, float y, float z) { X = x; Y = y; Z = z; }

        public static implicit operator NativeVec3(Vector3 v) => new(v.X, v.Y, v.Z);
        public static implicit operator Vector3(NativeVec3 v) => new(v.X, v.Y, v.Z);
        public static implicit operator PhysicsVector3(NativeVec3 v) => new PhysicsVector3(v.X, v.Y, v.Z);

        /// <summary>长度的平方（平方根前的快速比较）。</summary>
        public float LengthSquared() => X * X + Y * Y + Z * Z;
    }

    /// <summary>Native Quaternion - blittable counterpart of System.Numerics.Quaternion.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct NativeQuat
    {
        public float X;
        public float Y;
        public float Z;
        public float W;

        public NativeQuat(float x, float y, float z, float w) { X = x; Y = y; Z = z; W = w; }

        public static implicit operator NativeQuat(Quaternion q) => new(q.X, q.Y, q.Z, q.W);
        public static implicit operator Quaternion(NativeQuat q) => new(q.X, q.Y, q.Z, q.W);
    }

    /// <summary>
    ///   Native physics system settings. Passed to <see cref="JoltNative.SJ_PhysicsSystem_Create"/>.
    ///   Mirrors Jolt's BPLayerInterfaceImpl + PhysicsSystem construction parameters.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    internal struct NativePhysicsSettings
    {
        public int MaxBodies;
        public int MaxBodyPairs;
        public int MaxContactConstraints;
        public int NumBodyMutexes;
        public int BroadPhaseLayerCount;
        public int ObjectLayerCount;
        public NativeVec3 Gravity;
        public float FixedTimestep;
        public float Baumgarte;              // Position correction factor (0..1)
        public float SpeculativeContactDistance;
        public float PenetrationSlop;
        public float LinearCastThreshold;    // Use continuous collision detection above this speed
        public float MaxInflightBodyPairs;
        public byte DeterministicSimulation; // 1 = deterministic, 0 = not
    }

    /// <summary>
    ///   Native body creation settings. Mirrors Jolt's BodyCreationSettings.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct NativeBodyCreationSettings
    {
        public IntPtr Shape;                 // Immutable Shape* (from SJ_Shape_Finalize)
        public NativeVec3 Position;
        public NativeQuat Rotation;
        public float Mass;                   // 0 for static
        public NativeVec3 LinearVelocity;
        public NativeVec3 AngularVelocity;
        public float Restitution;
        public float Friction;
        public float LinearDamping;
        public float AngularDamping;
        public float MaxLinearVelocity;
        public float MaxAngularVelocity;
        public float GravityFactor;          // 1.0 = full gravity, 0.0 = no gravity
        public int MotionType;               // SJ_MotionType
        public ushort ObjectLayer;           // Collision layer
        public ushort CollisionMask;         // Mask for filtering
        public byte IsSensor;                // 1 = trigger/sensor body
        public byte AllowSleeping;
        public byte EnhancedInternalEdgeRemoval;
    }

    /// <summary>Native ray-cast result returned by SJ_PhysicsSystem_Raycast.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct NativeRaycastResult
    {
        public uint BodyId;                  // Jolt BodyID value
        public NativeVec3 Position;
        public NativeVec3 Normal;
        public float Fraction;               // 0..1 along the ray
        public float Distance;               // World-space distance
        public byte SubShapeId;              // triangle / face id within compound/mesh
    }

    /// <summary>Native contact manifold data returned by the contact listener callback.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct NativeContactManifold
    {
        public uint BodyIdA;
        public uint BodyIdB;
        public ushort SubShapeIdA;
        public ushort SubShapeIdB;
        public NativeVec3 AverageContactPoint;
        public NativeVec3 AverageNormal;      // Points from B to A
        public float PenetrationDepth;
        public int ContactPointCount;

        /// <summary>对方（B）刚体索引（由 BodyIdB 派生）。</summary>
        public int BodyIndexB => (int)BodyIdB;

        /// <summary>接触点数组（由上层在回调中填充，供 C# 侧读取）。</summary>
        public NativeContactPoint[]? Points;
    }

    /// <summary>Native 接触点（供 NativeContactManifold.Points 使用）。</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct NativeContactPoint
    {
        public NativeVec3 Position;
        public NativeVec3 Normal;
        public float Separation;
    }

    /// <summary>Native character ground-state result.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    internal struct NativeGroundState
    {
        public byte GroundState;              // SJ_GroundState
        public NativeVec3 GroundPosition;
        public NativeVec3 GroundNormal;
        public float GroundY;                 // Y coordinate of ground at character position
        public uint GroundBodyId;
        public float SlopeCosAngle;           // Cosine of slope angle
    }

    /// <summary>Native character update configuration.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    internal struct NativeCharacterUpdateSettings
    {
        public float MaxSlopeAngle;           // Max walkable slope angle (radians)
        public float UpStandHeight;           // Step height that can be climbed
        public float DownStandHeight;         // Step height that can be descended
        public float StickToFloorDistance;    // Keep character stuck to floor within this distance
        public float WalkStairsExtraDistance;
        public float WalkStairsUpExtraHeight;
        public NativeVec3 Up;                 // Up direction (usually 0,1,0)
    }

    // --------------------------------------------------------------------------------------------
    //  Enums — values must match the native #defines / enum values exactly.
    // --------------------------------------------------------------------------------------------

    /// <summary>Motion quality (discrete vs. continuous collision detection).</summary>
    public enum SJ_MotionQuality : int
    {
        Discrete = 0,
        LinearCast = 1,
    }

    /// <summary>Body motion type.</summary>
    internal enum SJ_MotionType : int
    {
        Static = 0,
        Kinematic = 1,
        Dynamic = 2,
    }

    /// <summary>Activation mode for body interface operations.</summary>
    internal enum SJ_Activation : int
    {
        Activate = 0,
        DontActivate = 1,
        ActivateIfActive = 2,
    }

    /// <summary>Character controller ground state.</summary>
    internal enum SJ_GroundState : byte
    {
        OnGround = 0,
        OnSteepGround = 1,
        NotSupported = 2,
        InAir = 3,
    }

    /// <summary>Constraint / joint types.</summary>
    internal enum SJ_ConstraintType : int
    {
        Fixed = 0,
        Point = 1,      // Ball-and-socket
        Hinge = 2,
        Slider = 3,
        Cone = 4,
        Distance = 5,
        SwingTwist = 6,
        SixDOF = 7,
        Path = 8,
        RackAndPinion = 9,
        Pulley = 10,
        Gear = 11,
    }

    /// <summary>Validate result returned by the contact listener (collision accept/reject).</summary>
    internal enum SJ_ValidateResult : int
    {
        RejectAllContacts = 0,
        RejectContact = 1,
        AcceptAllContactsForThisBodyPair = 2,
        AcceptContact = 3,
    }

    // --------------------------------------------------------------------------------------------
    //  Typed opaque handles. Wrapping IntPtr in a struct gives type-safety so a Shape handle
    //  cannot be accidentally passed where a PhysicsSystem handle is expected.
    // --------------------------------------------------------------------------------------------

    /// <summary>Opaque handle to a native Jolt PhysicsSystem.</summary>
    public readonly struct PhysicsSystemHandle : IEquatable<PhysicsSystemHandle>
    {
        public readonly IntPtr Value;
        public PhysicsSystemHandle(IntPtr v) { Value = v; }
        public bool IsNull => Value == IntPtr.Zero;
        public bool IsValid => Value != IntPtr.Zero;
        public bool Equals(PhysicsSystemHandle o) => Value == o.Value;
        public override bool Equals(object? obj) => obj is PhysicsSystemHandle h && Equals(h);
        public override int GetHashCode() => Value.GetHashCode();
        public static bool operator ==(PhysicsSystemHandle a, PhysicsSystemHandle b) => a.Value == b.Value;
        public static bool operator !=(PhysicsSystemHandle a, PhysicsSystemHandle b) => a.Value != b.Value;
    }

    internal readonly struct ShapeSettingsHandle : IEquatable<ShapeSettingsHandle>
    {
        public readonly IntPtr Value;
        public ShapeSettingsHandle(IntPtr v) { Value = v; }
        public bool IsNull => Value == IntPtr.Zero;
        public bool Equals(ShapeSettingsHandle o) => Value == o.Value;
        public override bool Equals(object? obj) => obj is ShapeSettingsHandle h && Equals(h);
        public override int GetHashCode() => Value.GetHashCode();
    }

    /// <summary>Opaque handle to an immutable native Jolt Shape.</summary>
    public readonly struct ShapeHandle : IEquatable<ShapeHandle>
    {
        public readonly IntPtr Value;
        public ShapeHandle(IntPtr v) { Value = v; }
        public bool IsNull => Value == IntPtr.Zero;
        public bool Equals(ShapeHandle o) => Value == o.Value;
        public override bool Equals(object? obj) => obj is ShapeHandle h && Equals(h);
        public override int GetHashCode() => Value.GetHashCode();
    }

    /// <summary>Opaque handle to a native Jolt character controller.</summary>
    public readonly struct CharacterHandle : IEquatable<CharacterHandle>
    {
        public readonly IntPtr Value;
        public CharacterHandle(IntPtr v) { Value = v; }
        public bool IsNull => Value == IntPtr.Zero;
        public bool Equals(CharacterHandle o) => Value == o.Value;
        public override bool Equals(object? obj) => obj is CharacterHandle h && Equals(h);
        public override int GetHashCode() => Value.GetHashCode();
    }

    // --------------------------------------------------------------------------------------------
    //  Callback delegates for collision events. The native side holds raw function pointers;
    //  the managed side MUST keep these delegates alive (the caller stores them in fields).
    // --------------------------------------------------------------------------------------------

    /// <summary>
    ///   Called when two bodies begin contact. Native passes the manifold by value.
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void ContactAddedCallback(NativeContactManifold manifold);

    /// <summary>Called when a contact between two bodies is removed.</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void ContactRemovedCallback(uint bodyIdA, uint bodyIdB);

    /// <summary>
    ///   Called to validate whether a contact should be created. Return AcceptContact to allow.
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int ValidateContactCallback(uint bodyIdA, uint bodyIdB);

    /// <summary>
    ///   Called every step for persistent contacts (after solve).
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void ContactPersistedCallback(NativeContactManifold manifold);

    // --------------------------------------------------------------------------------------------
    //  P/Invoke declarations. Grouped by subsystem. The CallingConvention is Cdecl throughout
    //  because the native library uses plain C functions.
    // --------------------------------------------------------------------------------------------

    internal static class JoltNative
    {
        private const string Lib = JoltNativeLib.Name;
        private const CallingConvention CC = CallingConvention.Cdecl;

        // ---- Library init / shutdown -------------------------------------------------------

        /// <summary>
        ///   Register the default allocator and job system. Must be called once before any
        ///   PhysicsSystem is created. Returns false if registration failed.
        /// </summary>
        [DllImport(Lib, CallingConvention = CC)]
        [return: MarshalAs(UnmanagedType.U1)]
        public static extern bool SJ_Init(int maxJobs, int maxBarriers, uint tempAllocatorSize);

        /// <summary>Shut down the Jolt global state and free the temp allocator / job system.</summary>
        [DllImport(Lib, CallingConvention = CC)]
        public static extern void SJ_Shutdown();

        /// <summary>Returns the Jolt library version string (e.g. "5.3.0").</summary>
        [DllImport(Lib, CallingConvention = CC)]
        public static extern IntPtr SJ_GetVersion();

        // ---- PhysicsSystem lifecycle -------------------------------------------------------

        /// <summary>Create a Jolt PhysicsSystem with the supplied settings.</summary>
        [DllImport(Lib, CallingConvention = CC)]
        public static extern PhysicsSystemHandle SJ_PhysicsSystem_Create(in NativePhysicsSettings settings);

        /// <summary>Destroy a PhysicsSystem and all bodies within it.</summary>
        [DllImport(Lib, CallingConvention = CC)]
        public static extern void SJ_PhysicsSystem_Destroy(PhysicsSystemHandle system);

        /// <summary>
        ///   Step the simulation forward by <paramref name="deltaTime"/> using
        ///   <paramref name="collisionSteps"/> sub-steps. Returns the number of steps actually taken.
        /// </summary>
        [DllImport(Lib, CallingConvention = CC)]
        public static extern int SJ_PhysicsSystem_Step(PhysicsSystemHandle system, float deltaTime, int collisionSteps);

        /// <summary>Set the gravity vector for the system.</summary>
        [DllImport(Lib, CallingConvention = CC)]
        public static extern void SJ_PhysicsSystem_SetGravity(PhysicsSystemHandle system, NativeVec3 gravity);

        /// <summary>Get the current gravity vector.</summary>
        [DllImport(Lib, CallingConvention = CC)]
        public static extern NativeVec3 SJ_PhysicsSystem_GetGravity(PhysicsSystemHandle system);

        /// <summary>Number of active (non-sleeping) bodies.</summary>
        [DllImport(Lib, CallingConvention = CC)]
        public static extern int SJ_PhysicsSystem_GetActiveBodyCount(PhysicsSystemHandle system);

        /// <summary>Total number of bodies in the system.</summary>
        [DllImport(Lib, CallingConvention = CC)]
        public static extern int SJ_PhysicsSystem_GetBodyCount(PhysicsSystemHandle system);

        /// <summary>Register managed collision callbacks. Pass IntPtr.Zero to clear a callback.</summary>
        [DllImport(Lib, CallingConvention = CC)]
        public static extern void SJ_PhysicsSystem_SetContactListener(
            PhysicsSystemHandle system,
            IntPtr addedCb,
            IntPtr removedCb,
            IntPtr validateCb,
            IntPtr persistedCb);

        // ---- Shape settings (configuration objects) ---------------------------------------

        [DllImport(Lib, CallingConvention = CC)]
        public static extern ShapeSettingsHandle SJ_Shape_Box_Create(NativeVec3 halfExtent);

        [DllImport(Lib, CallingConvention = CC)]
        public static extern ShapeSettingsHandle SJ_Shape_Sphere_Create(float radius);

        [DllImport(Lib, CallingConvention = CC)]
        public static extern ShapeSettingsHandle SJ_Shape_Capsule_Create(float halfHeightOfCylinder, float radius);

        [DllImport(Lib, CallingConvention = CC)]
        public static extern ShapeSettingsHandle SJ_Shape_Cylinder_Create(float halfHeight, float radius);

        [DllImport(Lib, CallingConvention = CC)]
        public static extern ShapeSettingsHandle SJ_Shape_Plane_Create(NativeVec3 normal, float constant);

        /// <summary>
        ///   Create a mesh (triangle) shape. The native side copies the vertex/index data, so
        ///   the managed arrays can be freed after the call. Mesh shapes are always static.
        /// </summary>
        [DllImport(Lib, CallingConvention = CC)]
        public static extern ShapeSettingsHandle SJ_Shape_Mesh_Create(
            [In] NativeVec3[] vertices, int vertexCount,
            [In] uint[] indices, int indexCount);

        /// <summary>Create a compound shape settings object (mutable until finalized).</summary>
        [DllImport(Lib, CallingConvention = CC)]
        public static extern ShapeSettingsHandle SJ_Shape_Compound_Create();

        /// <summary>
        ///   Add a finalized child shape to a compound. Returns the child index or -1 on failure.
        /// </summary>
        [DllImport(Lib, CallingConvention = CC)]
        public static extern int SJ_Compound_AddChild(ShapeSettingsHandle compound, ShapeHandle shape, NativeVec3 pos, NativeQuat rot);

        /// <summary>
        ///   Finalize a ShapeSettings into an immutable Shape that can be attached to a body.
        ///   The ShapeSettings handle is consumed and should no longer be used.
        /// </summary>
        [DllImport(Lib, CallingConvention = CC)]
        public static extern ShapeHandle SJ_Shape_Finalize(ShapeSettingsHandle settings);

        /// <summary>Destroy a ShapeSettings object (only if not yet finalized).</summary>
        [DllImport(Lib, CallingConvention = CC)]
        public static extern void SJ_ShapeSettings_Destroy(ShapeSettingsHandle settings);

        /// <summary>Destroy an immutable Shape. Only call when the shape is no longer referenced by any body.</summary>
        [DllImport(Lib, CallingConvention = CC)]
        public static extern void SJ_Shape_Destroy(ShapeHandle shape);

        // ---- Body interface ----------------------------------------------------------------

        /// <summary>
        ///   Create and add a body in one call. Returns the Jolt BodyID (0 on failure).
        /// </summary>
        [DllImport(Lib, CallingConvention = CC)]
        public static extern uint SJ_Body_CreateAndAdd(PhysicsSystemHandle system, in NativeBodyCreationSettings settings);

        /// <summary>Remove a body from the simulation and destroy it.</summary>
        [DllImport(Lib, CallingConvention = CC)]
        public static extern void SJ_Body_RemoveAndDestroy(PhysicsSystemHandle system, uint bodyId);

        /// <summary>Check whether a body ID is still valid.</summary>
        [DllImport(Lib, CallingConvention = CC)]
        [return: MarshalAs(UnmanagedType.U1)]
        public static extern bool SJ_Body_IsValid(PhysicsSystemHandle system, uint bodyId);

        /// <summary>Set the world transform of a body.</summary>
        [DllImport(Lib, CallingConvention = CC)]
        public static extern void SJ_Body_SetPosition(PhysicsSystemHandle system, uint bodyId, NativeVec3 pos, NativeQuat rot, int activation);

        /// <summary>Get the world transform of a body.</summary>
        [DllImport(Lib, CallingConvention = CC)]
        public static extern void SJ_Body_GetPosition(PhysicsSystemHandle system, uint bodyId, out NativeVec3 pos, out NativeQuat rot);

        /// <summary>Get the world-space position only.</summary>
        [DllImport(Lib, CallingConvention = CC)]
        public static extern NativeVec3 SJ_Body_GetPositionOnly(PhysicsSystemHandle system, uint bodyId);

        /// <summary>Get the world-space rotation only.</summary>
        [DllImport(Lib, CallingConvention = CC)]
        public static extern NativeQuat SJ_Body_GetRotation(PhysicsSystemHandle system, uint bodyId);

        /// <summary>Set linear velocity.</summary>
        [DllImport(Lib, CallingConvention = CC)]
        public static extern void SJ_Body_SetLinearVelocity(PhysicsSystemHandle system, uint bodyId, NativeVec3 vel);

        /// <summary>Get linear velocity.</summary>
        [DllImport(Lib, CallingConvention = CC)]
        public static extern NativeVec3 SJ_Body_GetLinearVelocity(PhysicsSystemHandle system, uint bodyId);

        /// <summary>Set angular velocity.</summary>
        [DllImport(Lib, CallingConvention = CC)]
        public static extern void SJ_Body_SetAngularVelocity(PhysicsSystemHandle system, uint bodyId, NativeVec3 vel);

        /// <summary>Get angular velocity.</summary>
        [DllImport(Lib, CallingConvention = CC)]
        public static extern NativeVec3 SJ_Body_GetAngularVelocity(PhysicsSystemHandle system, uint bodyId);

        /// <summary>Add a force (accumulated over the next step) to a body.</summary>
        [DllImport(Lib, CallingConvention = CC)]
        public static extern void SJ_Body_AddForce(PhysicsSystemHandle system, uint bodyId, NativeVec3 force, int activation);

        /// <summary>Add an impulse (instantaneous velocity change) to a body at its center of mass.</summary>
        [DllImport(Lib, CallingConvention = CC)]
        public static extern void SJ_Body_AddImpulse(PhysicsSystemHandle system, uint bodyId, NativeVec3 impulse, int activation);

        /// <summary>Add an impulse at a specific world position (generates torque).</summary>
        [DllImport(Lib, CallingConvention = CC)]
        public static extern void SJ_Body_AddImpulseAtPosition(PhysicsSystemHandle system, uint bodyId, NativeVec3 impulse, NativeVec3 worldPos, int activation);

        /// <summary>Add a torque (accumulated over the next step).</summary>
        [DllImport(Lib, CallingConvention = CC)]
        public static extern void SJ_Body_AddTorque(PhysicsSystemHandle system, uint bodyId, NativeVec3 torque, int activation);

        /// <summary>Set the restitution (bounciness) of a body.</summary>
        [DllImport(Lib, CallingConvention = CC)]
        public static extern void SJ_Body_SetRestitution(PhysicsSystemHandle system, uint bodyId, float restitution);

        /// <summary>Set the friction of a body.</summary>
        [DllImport(Lib, CallingConvention = CC)]
        public static extern void SJ_Body_SetFriction(PhysicsSystemHandle system, uint bodyId, float friction);

        /// <summary>Get the current restitution.</summary>
        [DllImport(Lib, CallingConvention = CC)]
        public static extern float SJ_Body_GetRestitution(PhysicsSystemHandle system, uint bodyId);

        /// <summary>Get the current friction.</summary>
        [DllImport(Lib, CallingConvention = CC)]
        public static extern float SJ_Body_GetFriction(PhysicsSystemHandle system, uint bodyId);

        /// <summary>Set the motion type (static / kinematic / dynamic).</summary>
        [DllImport(Lib, CallingConvention = CC)]
        public static extern void SJ_Body_SetMotionType(PhysicsSystemHandle system, uint bodyId, int motionType, int activation);

        /// <summary>Get the motion type.</summary>
        [DllImport(Lib, CallingConvention = CC)]
        public static extern int SJ_Body_GetMotionType(PhysicsSystemHandle system, uint bodyId);

        /// <summary>Set the motion quality (discrete / linear-cast CCD).</summary>
        [DllImport(Lib, CallingConvention = CC)]
        public static extern void SJ_Body_SetMotionQuality(PhysicsSystemHandle system, uint bodyId, int quality);

        /// <summary>Activate a body (wake it up).</summary>
        [DllImport(Lib, CallingConvention = CC)]
        public static extern void SJ_Body_Activate(PhysicsSystemHandle system, uint bodyId);

        /// <summary>Force a body to sleep.</summary>
        [DllImport(Lib, CallingConvention = CC)]
        public static extern void SJ_Body_Deactivate(PhysicsSystemHandle system, uint bodyId);

        /// <summary>Whether the body is currently active (awake).</summary>
        [DllImport(Lib, CallingConvention = CC)]
        [return: MarshalAs(UnmanagedType.U1)]
        public static extern bool SJ_Body_IsActive(PhysicsSystemHandle system, uint bodyId);

        /// <summary>Set the gravity factor (0 = no gravity, 1 = full gravity).</summary>
        [DllImport(Lib, CallingConvention = CC)]
        public static extern void SJ_Body_SetGravityFactor(PhysicsSystemHandle system, uint bodyId, float factor);

        /// <summary>Set whether the body is a sensor (trigger).</summary>
        [DllImport(Lib, CallingConvention = CC)]
        public static extern void SJ_Body_SetIsSensor(PhysicsSystemHandle system, uint bodyId, [MarshalAs(UnmanagedType.U1)] bool isSensor);

        /// <summary>Set the collision layer (object layer) of a body.</summary>
        [DllImport(Lib, CallingConvention = CC)]
        public static extern void SJ_Body_SetObjectLayer(PhysicsSystemHandle system, uint bodyId, ushort layer, ushort mask);

        /// <summary>Get the world-space bounding box of a body.</summary>
        [DllImport(Lib, CallingConvention = CC)]
        public static extern void SJ_Body_GetWorldBounds(PhysicsSystemHandle system, uint bodyId, out NativeVec3 min, out NativeVec3 max);

        /// <summary>
        ///   Get the EntityId user data stored on the body (set during creation via a side map).
        /// </summary>
        [DllImport(Lib, CallingConvention = CC)]
        public static extern int SJ_Body_GetUserData(PhysicsSystemHandle system, uint bodyId);

        /// <summary>Set the EntityId user data associated with a Jolt body.</summary>
        [DllImport(Lib, CallingConvention = CC)]
        public static extern void SJ_Body_SetUserData(PhysicsSystemHandle system, uint bodyId, int entityId);

        // ---- Queries (broadphase) ----------------------------------------------------------

        /// <summary>
        ///   Cast a ray into the world. Returns true if something was hit.
        /// </summary>
        [DllImport(Lib, CallingConvention = CC)]
        [return: MarshalAs(UnmanagedType.U1)]
        public static extern bool SJ_Query_Raycast(
            PhysicsSystemHandle system,
            NativeVec3 origin,
            NativeVec3 direction,
            float maxDistance,
            ushort layer,
            ushort mask,
            out NativeRaycastResult result);

        /// <summary>
        ///   Cast a ray and collect multiple hits (sorted nearest-first). Returns the hit count.
        /// </summary>
        [DllImport(Lib, CallingConvention = CC)]
        public static extern int SJ_Query_RaycastMulti(
            PhysicsSystemHandle system,
            NativeVec3 origin,
            NativeVec3 direction,
            float maxDistance,
            ushort layer,
            ushort mask,
            [Out] NativeRaycastResult[] results,
            int maxResults);

        /// <summary>
        ///   Test which bodies overlap a sphere. Returns the count of overlapping body IDs.
        /// </summary>
        [DllImport(Lib, CallingConvention = CC)]
        public static extern int SJ_Query_OverlapSphere(
            PhysicsSystemHandle system,
            NativeVec3 center,
            float radius,
            ushort layer,
            ushort mask,
            [Out] uint[] bodyIds,
            int maxResults);

        /// <summary>Test which bodies overlap an axis-aligned box.</summary>
        [DllImport(Lib, CallingConvention = CC)]
        public static extern int SJ_Query_OverlapBox(
            PhysicsSystemHandle system,
            NativeVec3 center,
            NativeVec3 halfExtent,
            ushort layer,
            ushort mask,
            [Out] uint[] bodyIds,
            int maxResults);

        /// <summary>Test which bodies overlap a capsule.</summary>
        [DllImport(Lib, CallingConvention = CC)]
        public static extern int SJ_Query_OverlapCapsule(
            PhysicsSystemHandle system,
            NativeVec3 point1,
            NativeVec3 point2,
            float radius,
            ushort layer,
            ushort mask,
            [Out] uint[] bodyIds,
            int maxResults);

        /// <summary>Test which bodies overlap a convex shape (defined by points).</summary>
        [DllImport(Lib, CallingConvention = CC)]
        public static extern int SJ_Query_OverlapShape(
            PhysicsSystemHandle system,
            [In] NativeVec3[] points,
            int pointCount,
            NativeVec3 position,
            NativeQuat rotation,
            ushort layer,
            ushort mask,
            [Out] uint[] bodyIds,
            int maxResults);

        /// <summary>
        ///   Collide a shape against the world at a given transform. Returns contact points.
        /// </summary>
        [DllImport(Lib, CallingConvention = CC)]
        public static extern int SJ_Query_CollideShape(
            PhysicsSystemHandle system,
            ShapeHandle shape,
            NativeVec3 position,
            NativeQuat rotation,
            ushort layer,
            ushort mask,
            [Out] NativeRaycastResult[] contacts,
            int maxContacts);

        // ---- Constraints -------------------------------------------------------------------

        /// <summary>Create a fixed constraint between two bodies (welds them together).</summary>
        [DllImport(Lib, CallingConvention = CC)]
        public static extern uint SJ_Constraint_CreateFixed(PhysicsSystemHandle system, uint bodyA, uint bodyB);

        /// <summary>Create a point (ball-and-socket) constraint.</summary>
        [DllImport(Lib, CallingConvention = CC)]
        public static extern uint SJ_Constraint_CreatePoint(
            PhysicsSystemHandle system, uint bodyA, uint bodyB,
            NativeVec3 pivotA, NativeVec3 pivotB);

        /// <summary>Create a hinge constraint between two bodies.</summary>
        [DllImport(Lib, CallingConvention = CC)]
        public static extern uint SJ_Constraint_CreateHinge(
            PhysicsSystemHandle system, uint bodyA, uint bodyB,
            NativeVec3 pivotA, NativeVec3 pivotB,
            NativeVec3 hingeAxisA, NativeVec3 hingeAxisB);

        /// <summary>Create a slider constraint between two bodies.</summary>
        [DllImport(Lib, CallingConvention = CC)]
        public static extern uint SJ_Constraint_CreateSlider(
            PhysicsSystemHandle system, uint bodyA, uint bodyB,
            NativeVec3 pivotA, NativeVec3 pivotB,
            NativeVec3 sliderAxisA, NativeVec3 sliderAxisB,
            float minLimit, float maxLimit);

        /// <summary>Create a distance constraint between two bodies.</summary>
        [DllImport(Lib, CallingConvention = CC)]
        public static extern uint SJ_Constraint_CreateDistance(
            PhysicsSystemHandle system, uint bodyA, uint bodyB,
            NativeVec3 pivotA, NativeVec3 pivotB,
            float minDistance, float maxDistance);

        /// <summary>Activate a constraint (add it to the system).</summary>
        [DllImport(Lib, CallingConvention = CC)]
        public static extern void SJ_Constraint_Activate(PhysicsSystemHandle system, uint constraintId);

        /// <summary>Remove and destroy a constraint.</summary>
        [DllImport(Lib, CallingConvention = CC)]
        public static extern void SJ_Constraint_Destroy(PhysicsSystemHandle system, uint constraintId);

        /// <summary>Set a hinge angle limit (in radians). minAngle &lt; maxAngle.</summary>
        [DllImport(Lib, CallingConvention = CC)]
        public static extern void SJ_Constraint_Hinge_SetLimits(PhysicsSystemHandle system, uint constraintId, float minAngle, float maxAngle);

        /// <summary>Set a slider distance limit.</summary>
        [DllImport(Lib, CallingConvention = CC)]
        public static extern void SJ_Constraint_Slider_SetLimits(PhysicsSystemHandle system, uint constraintId, float minLimit, float maxLimit);

        /// <summary>Set the constraint's spring frequency (Hz) for soft limits.</summary>
        [DllImport(Lib, CallingConvention = CC)]
        public static extern void SJ_Constraint_SetFrequency(PhysicsSystemHandle system, uint constraintId, float frequencyHz);

        /// <summary>Set the constraint's spring damping ratio.</summary>
        [DllImport(Lib, CallingConvention = CC)]
        public static extern void SJ_Constraint_SetDamping(PhysicsSystemHandle system, uint constraintId, float damping);

        /// <summary>Enable/disable a motor on a hinge or slider constraint.</summary>
        [DllImport(Lib, CallingConvention = CC)]
        public static extern void SJ_Constraint_SetMotorState(PhysicsSystemHandle system, uint constraintId, int motorState);

        /// <summary>Set the target motor velocity for a hinge/slider.</summary>
        [DllImport(Lib, CallingConvention = CC)]
        public static extern void SJ_Constraint_SetTargetVelocity(PhysicsSystemHandle system, uint constraintId, float velocity);

        /// <summary>Set the maximum motor force/torque.</summary>
        [DllImport(Lib, CallingConvention = CC)]
        public static extern void SJ_Constraint_SetMaxMotorForce(PhysicsSystemHandle system, uint constraintId, float maxForce);

        // ---- Character controller ----------------------------------------------------------

        /// <summary>
        ///   Create a CharacterVirtual controller (capsule-based, no dedicated body — moved via
        ///   collision queries each update). This is Jolt's recommended approach for game
        ///   characters because it allows fine-grained control and stair/steep-slope handling.
        /// </summary>
        [DllImport(Lib, CallingConvention = CC)]
        public static extern CharacterHandle SJ_Character_Create(
            PhysicsSystemHandle system,
            float capsuleRadius,
            float capsuleHalfHeight,
            NativeVec3 position,
            NativeQuat rotation,
            ushort layer,
            ushort mask,
            float maxSlopeAngle,
            float upStandHeight,
            float mass);

        /// <summary>Destroy a character controller.</summary>
        [DllImport(Lib, CallingConvention = CC)]
        public static extern void SJ_Character_Destroy(PhysicsSystemHandle system, CharacterHandle character);

        /// <summary>
        ///   Update the character: applies gravity + desired horizontal velocity, runs collision,
        ///   handles stair-stepping and steep-slope sliding. Returns the resulting position.
        /// </summary>
        [DllImport(Lib, CallingConvention = CC)]
        public static extern NativeVec3 SJ_Character_Update(
            PhysicsSystemHandle system,
            CharacterHandle character,
            float dt,
            NativeVec3 gravity,
            NativeVec3 desiredVelocity,
            float maxWalkAngle,
            in NativeCharacterUpdateSettings settings,
            out NativeGroundState groundState);

        /// <summary>Teleport the character to a position (no collision check).</summary>
        [DllImport(Lib, CallingConvention = CC)]
        public static extern void SJ_Character_SetPosition(PhysicsSystemHandle system, CharacterHandle character, NativeVec3 position);

        /// <summary>Get the character's current world position.</summary>
        [DllImport(Lib, CallingConvention = CC)]
        public static extern NativeVec3 SJ_Character_GetPosition(PhysicsSystemHandle system, CharacterHandle character);

        /// <summary>Get the character's current rotation.</summary>
        [DllImport(Lib, CallingConvention = CC)]
        public static extern NativeQuat SJ_Character_GetRotation(PhysicsSystemHandle system, CharacterHandle character);

        /// <summary>Set the character's rotation.</summary>
        [DllImport(Lib, CallingConvention = CC)]
        public static extern void SJ_Character_SetRotation(PhysicsSystemHandle system, CharacterHandle character, NativeQuat rotation);

        /// <summary>Get the character's linear velocity.</summary>
        [DllImport(Lib, CallingConvention = CC)]
        public static extern NativeVec3 SJ_Character_GetLinearVelocity(PhysicsSystemHandle system, CharacterHandle character);

        /// <summary>Set the character's linear velocity (e.g. for jumping).</summary>
        [DllImport(Lib, CallingConvention = CC)]
        public static extern void SJ_Character_SetLinearVelocity(PhysicsSystemHandle system, CharacterHandle character, NativeVec3 velocity);

        /// <summary>Get the character's ground state (on ground / steep / in air).</summary>
        [DllImport(Lib, CallingConvention = CC)]
        public static extern byte SJ_Character_GetGroundState(PhysicsSystemHandle system, CharacterHandle character);

        /// <summary>Get the character's ground normal (valid when grounded).</summary>
        [DllImport(Lib, CallingConvention = CC)]
        public static extern NativeVec3 SJ_Character_GetGroundNormal(PhysicsSystemHandle system, CharacterHandle character);

        /// <summary>Get the character's ground position (valid when grounded).</summary>
        [DllImport(Lib, CallingConvention = CC)]
        public static extern NativeVec3 SJ_Character_GetGroundPosition(PhysicsSystemHandle system, CharacterHandle character);

        /// <summary>Get the body ID the character is standing on.</summary>
        [DllImport(Lib, CallingConvention = CC)]
        public static extern uint SJ_Character_GetGroundBodyId(PhysicsSystemHandle system, CharacterHandle character);

        /// <summary>Set whether the character interacts with other dynamic bodies (push them).</summary>
        [DllImport(Lib, CallingConvention = CC)]
        public static extern void SJ_Character_SetEnhancedInternalEdgeRemoval(PhysicsSystemHandle system, CharacterHandle character, [MarshalAs(UnmanagedType.U1)] bool enabled);

        // ---- Debug / profiling -------------------------------------------------------------

        /// <summary>Save the broadphase stats into the provided buffer (debugging).</summary>
        [DllImport(Lib, CallingConvention = CC)]
        public static extern int SJ_Debug_GetBodyStats(PhysicsSystemHandle system, [Out] uint[] bodyIds, int maxCount);

        // ---- SmartEdgeFilter（自研创新：按角度阈值过滤三角网格内部边鬼影碰撞）-------------

        /// <summary>为某 body 设置边过滤角度阈值（弧度）。0 = 该 body 不过滤。</summary>
        [DllImport(Lib, CallingConvention = CC)]
        public static extern void SJ_SmartEdgeFilter_SetBodyThreshold(PhysicsSystemHandle system, uint bodyId, float angleThresholdRadians);

        /// <summary>读取某 body 的边过滤阈值。</summary>
        [DllImport(Lib, CallingConvention = CC)]
        public static extern float SJ_SmartEdgeFilter_GetBodyThreshold(PhysicsSystemHandle system, uint bodyId);

        /// <summary>总开关。</summary>
        [DllImport(Lib, CallingConvention = CC)]
        public static extern void SJ_SmartEdgeFilter_Enable(PhysicsSystemHandle system, [MarshalAs(UnmanagedType.U1)] bool enabled);
    }

    // --------------------------------------------------------------------------------------------
    //  JoltBridge — a thin safe wrapper that initialises the library once and provides
    //  convenience helpers. Higher-level code (PhysicsWorld) talks to JoltNative directly but
    //  uses this for bootstrap and error handling.
    // --------------------------------------------------------------------------------------------

    /// <summary>
    ///   Static bootstrap helper for the Jolt native library. Call <see cref="Initialize"/> once
    ///   at engine start-up; <see cref="Shutdown"/> at engine tear-down.
    /// </summary>
    public static class JoltBridge
    {
        private static int _initCount;
        private static readonly object _initLock = new();

        /// <summary>Whether the native library has been successfully initialised.</summary>
        public static bool IsInitialized { get; private set; }

        /// <summary>The Jolt version string reported by the native library (or null if not loaded).</summary>
        public static string? Version { get; private set; }

        /// <summary>
        ///   Initialise the Jolt library. Thread-safe and ref-counted; each <see cref="Initialize"/>
        ///   must be paired with a <see cref="Shutdown"/>.
        /// </summary>
        /// <param name="maxJobs">Max concurrent jobs (threads). Use ~Environment.ProcessorCount.</param>
        /// <param name="tempAllocatorSize">Size of the temporary allocator in bytes (e.g. 16 MB).</param>
        /// <returns>True if the library was newly initialised; false if already initialised.</returns>
        public static bool Initialize(int maxJobs = 0, uint tempAllocatorSize = 16 * 1024 * 1024)
        {
            lock (_initLock)
            {
                if (IsInitialized)
                {
                    _initCount++;
                    return false;
                }

                if (maxJobs <= 0)
                    maxJobs = Math.Max(2, Environment.ProcessorCount);

                bool ok = JoltNative.SJ_Init(maxJobs, maxJobs + 2, tempAllocatorSize);
                if (!ok)
                    throw new InvalidOperationException(
                        "Failed to initialise the native Jolt physics library (YamaJolt). " +
                        "Ensure the native binary is present in the runtimes/win-x64/native folder.");

                IntPtr verPtr = JoltNative.SJ_GetVersion();
                Version = verPtr == IntPtr.Zero ? "unknown" : Marshal.PtrToStringAnsi(verPtr);
                IsInitialized = true;
                _initCount = 1;
                return true;
            }
        }

        /// <summary>Decrement the init ref-count and shut down Jolt when it reaches zero.</summary>
        public static void Shutdown()
        {
            lock (_initLock)
            {
                if (!IsInitialized) return;
                _initCount--;
                if (_initCount <= 0)
                {
                    JoltNative.SJ_Shutdown();
                    IsInitialized = false;
                    Version = null;
                    _initCount = 0;
                }
            }
        }

        /// <summary>
        ///   Build a <see cref="NativePhysicsSettings"/> with sensible defaults for a game world.
        /// </summary>
        internal static NativePhysicsSettings DefaultSettings(
            int maxBodies = 16384,
            float fixedTimestep = 1f / 60f,
            Vector3? gravity = null)
        {
            return new NativePhysicsSettings
            {
                MaxBodies = maxBodies,
                MaxBodyPairs = maxBodies,
                MaxContactConstraints = 8192,
                NumBodyMutexes = 0,
                BroadPhaseLayerCount = 16,
                ObjectLayerCount = 1024,
                Gravity = gravity ?? new Vector3(0, -9.81f, 0),
                FixedTimestep = fixedTimestep,
                Baumgarte = 0.2f,
                SpeculativeContactDistance = 0.02f,
                PenetrationSlop = 0.02f,
                LinearCastThreshold = 0.5f,
                MaxInflightBodyPairs = 16384,
                DeterministicSimulation = 1,
            };
        }

        /// <summary>
        ///   Build a <see cref="NativeBodyCreationSettings"/> for a dynamic body.
        /// </summary>
        internal static NativeBodyCreationSettings BodySettings(
            ShapeHandle shape,
            Vector3 position,
            Quaternion rotation,
            float mass,
            ushort objectLayer,
            ushort collisionMask,
            bool isSensor = false,
            float restitution = 0.2f,
            float friction = 0.6f)
        {
            int motionType = mass <= 0f
                ? (int)SJ_MotionType.Static
                : (int)SJ_MotionType.Dynamic;

            return new NativeBodyCreationSettings
            {
                Shape = shape.Value,
                Position = position,
                Rotation = rotation,
                Mass = mass,
                LinearVelocity = Vector3.Zero,
                AngularVelocity = Vector3.Zero,
                Restitution = restitution,
                Friction = friction,
                LinearDamping = 0.05f,
                AngularDamping = 0.25f,
                MaxLinearVelocity = 500f,
                MaxAngularVelocity = 50f,
                GravityFactor = 1f,
                MotionType = motionType,
                ObjectLayer = objectLayer,
                CollisionMask = collisionMask,
                IsSensor = (byte)(isSensor ? 1 : 0),
                AllowSleeping = 1,
                EnhancedInternalEdgeRemoval = 1,
            };
        }

        // ========================================================================================
        //  Unity 风格系统集成扩展
        //
        //  以下方法将新创建的 Unity 风格系统（层级/场景/光线投射/碰撞事件/刚体/碰撞体/
        //  角色控制/关节/布料）整合到 JoltBridge 中，提供统一的初始化入口。
        // ========================================================================================

        /// <summary>
        ///   创建 PhysicsLayerCollisionMatrix 并将其转换为 Jolt 兼容的模式。
        /// </summary>
        public static ushort[] CreateLayerCollisionMatrix(PhysicsLayerCollisionMatrix? matrix = null)
        {
            return (matrix ?? new PhysicsLayerCollisionMatrix()).ToJoltLayerMasks();
        }

        /// <summary>
        ///   创建 PhysicsSceneManager 并初始化默认场景。
        /// </summary>
        public static PhysicsSceneManager CreateSceneManager()
        {
            return new PhysicsSceneManager();
        }

        /// <summary>
        ///   在 PhysicsWorld 上创建 RaycastSystem 实例。
        /// </summary>
        public static RaycastSystem CreateRaycastSystem(PhysicsWorld world)
        {
            return new RaycastSystem(world);
        }

        /// <summary>
        ///   在 PhysicsWorld 上创建 CollisionEventSystem 实例，
        ///   并自动挂接到 PhysicsWorld 的碰撞事件回调。
        /// </summary>
        public static CollisionEventSystem CreateCollisionEventSystem(PhysicsWorld world)
        {
            var system = new CollisionEventSystem(world);

            // 挂接到 PhysicsWorld 的现有事件（这些事件在 PhysicsWorld 的 InstallContactListener 中触发）
            world.OnCollisionEnter += (a) => { };
            world.OnCollisionExit += (a, b) => { };

            return system;
        }

        /// <summary>
        ///   从 PhysicsWorld 和 BodyId 创建 RigidbodyComponent 封装。
        /// </summary>
        public static RigidbodyComponent CreateRigidbodyComponent(PhysicsWorld world, uint bodyId, int entityId)
        {
            return new RigidbodyComponent(world, bodyId, entityId);
        }

        /// <summary>
        ///   在 PhysicsWorld 上创建 ColliderSystem 实例。
        /// </summary>
        public static ColliderSystem CreateColliderSystem(PhysicsWorld world)
        {
            return new ColliderSystem(world);
        }

        /// <summary>
        ///   在 PhysicsWorld 上创建 UnityCharacterController 实例。
        /// </summary>
        public static UnityCharacterController CreateCharacterController(
            PhysicsWorld world, UnityCharacterSettings? settings = null)
        {
            return new UnityCharacterController(world, settings ?? new UnityCharacterSettings());
        }

        /// <summary>
        ///   在 PhysicsScene 上创建 UnityCharacterController 实例（向后兼容）。
        /// </summary>
        public static UnityCharacterController CreateCharacterController(
            PhysicsScene scene, UnityCharacterSettings? settings = null)
        {
            if (scene.World == null)
                throw new InvalidOperationException("PhysicsScene must have a valid PhysicsWorld (Jolt mode).");
            return new UnityCharacterController(scene.World, settings ?? new UnityCharacterSettings());
        }

        /// <summary>
        ///   创建 JointFactory 实例。
        /// </summary>
        public static JointFactory CreateJointFactory(PhysicsSystemHandle system)
        {
            return new JointFactory();
        }

        /// <summary>
        ///   创建布料系统管理器。
        /// </summary>
        public static UnityClothSystemManager CreateClothSystemManager()
        {
            return new UnityClothSystemManager();
        }

        /// <summary>
        ///   创建 PhysicsSceneManager 并添加一个默认场景。
        /// </summary>
        public static PhysicsSceneManager CreateDefaultSceneManager(out PhysicsSceneHandle defaultScene)
        {
            var manager = new PhysicsSceneManager();
            defaultScene = manager.CreateScene();
            return manager;
        }

        // ========================================================================================
        //  物理层级辅助方法
        // ========================================================================================

        /// <summary>
        ///   将 PhysicsLayer 枚举转换为 Jolt ObjectLayer (ushort)。
        ///   映射规则：PhysicsLayer 索引转换为位掩码格式。
        /// </summary>
        internal static ushort PhysicsLayerToJoltLayer(PhysicsLayer layer)
        {
            return (ushort)(1u << (int)layer);
        }

        /// <summary>
        ///   将 PhysicsLayerMask (uint) 转换为 Jolt CollisionMask (ushort)。
        ///   取低 16 位作为 Jolt 的碰撞掩码。
        /// </summary>
        internal static ushort LayerMaskToJoltMask(uint layerMask)
        {
            return (ushort)(layerMask & 0xFFFF);
        }
    }
}
