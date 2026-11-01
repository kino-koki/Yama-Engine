using System;
using System.Numerics;

namespace YamaEngine.Physics
{
    // ============================================================================================
    //  Constraints.cs
    //
    //  High-level constraint (joint) system built on top of the native Jolt constraint API.
    //  Each constraint connects two rigid bodies and restricts their relative motion.
    //
    //  Constraint types provided:
    //    - Ball (Point)  : ball-and-socket joint, free rotation around a shared pivot point.
    //    - Hinge         : single-axis rotational joint with optional angle limits and motor.
    //    - Slider        : single-axis translational joint with optional distance limits and motor.
    //    - Distance      : keeps two points within a min/max distance range.
    //    - Fixed         : welds two bodies together completely.
    //
    //  All constraints reference bodies by their Jolt BodyID (uint). The PhysicsWorld owns the
    //  PhysicsSystemHandle and passes it to constraint operations. Constraints are created via
    //  ConstraintFactory / PhysicsWorld.CreateConstraint and tracked by the world.
    // ============================================================================================

    /// <summary>
    ///   Motor drive mode for hinge and slider constraints.
    /// </summary>
    public enum ConstraintMotorMode
    {
        /// <summary>Motor is disabled (free movement within limits).</summary>
        Off = 0,
        /// <summary>Drive toward a target velocity (speed control).</summary>
        Velocity = 1,
        /// <summary>Drive toward a target position (position control).</summary>
        Position = 2,
    }

    /// <summary>
    ///   Describes the kind of a constraint, mirroring the native Jolt constraint types.
    /// </summary>
    public enum ConstraintKind
    {
        Fixed,
        Ball,
        Hinge,
        Slider,
        Distance,
    }

    /// <summary>
    ///   Base class for all physics constraints. A constraint is a native object identified by
    ///   a non-zero uint handle. Constraints are owned by the <see cref="PhysicsWorld"/> that
    ///   created them; disposing removes the native constraint.
    /// </summary>
    public abstract class Constraint : IDisposable
    {
        /// <summary>The physics system this constraint lives in.</summary>
        protected PhysicsSystemHandle System { get; }

        /// <summary>Native Jolt constraint ID. Zero means destroyed / not created.</summary>
        public uint NativeId { get; private set; }

        /// <summary>The first body connected by this constraint.</summary>
        public uint BodyA { get; }

        /// <summary>The second body connected by this constraint (0 if world-space constraint).</summary>
        public uint BodyB { get; }

        /// <summary>Type of this constraint.</summary>
        public ConstraintKind Kind { get; }

        /// <summary>Whether the constraint has been disposed.</summary>
        public bool IsDisposed => NativeId == 0;

        /// <summary>Optional user tag for game-side bookkeeping.</summary>
        public object? Tag { get; set; }

        /// <summary>是否已断裂（超过强度极限自动断裂）。</summary>
        public bool IsBroken { get; protected set; }

        /// <summary>约束在物理世界刚体列表中的索引 A（用于求解岛连通判定）。</summary>
        public int BodyIndexA { get; protected set; }

        /// <summary>约束在物理世界刚体列表中的索引 B。</summary>
        public int BodyIndexB { get; protected set; }

        private bool _disposed;

        protected Constraint(PhysicsSystemHandle system, ConstraintKind kind, uint bodyA, uint bodyB, uint nativeId)
        {
            System = system;
            Kind = kind;
            BodyA = bodyA;
            BodyB = bodyB;
            NativeId = nativeId;
        }

        /// <summary>Called by subclasses to store the native ID after creation.</summary>
        internal void SetNativeId(uint id)
        {
            NativeId = id;
            if (id != 0)
                JoltNative.SJ_Constraint_Activate(System, id);
        }

        /// <summary>
        ///   Set the spring frequency (Hz) used by soft limit springs. 0 disables the spring.
        /// </summary>
        public void SetFrequency(float frequencyHz)
        {
            ThrowIfDisposed();
            JoltNative.SJ_Constraint_SetFrequency(System, NativeId, frequencyHz);
        }

        /// <summary>Set the spring damping ratio (0 = no damping, 1 = critical damping).</summary>
        public void SetDamping(float damping)
        {
            ThrowIfDisposed();
            JoltNative.SJ_Constraint_SetDamping(System, NativeId, damping);
        }

        /// <summary>Remove and destroy the native constraint. Safe to call multiple times.</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (NativeId != 0)
            {
                JoltNative.SJ_Constraint_Destroy(System, NativeId);
                NativeId = 0;
            }
            GC.SuppressFinalize(this);
        }

        protected void ThrowIfDisposed()
        {
            if (NativeId == 0)
                throw new ObjectDisposedException(GetType().Name, "Constraint has been disposed.");
        }

        /// <summary>速度约束预求解（子类可重写）。</summary>
        public virtual void InitVelocitySolve(IRigidBodyProxy[] bodies, float dt) { }

        /// <summary>速度约束迭代求解（子类可重写）。</summary>
        public virtual void SolveVelocity(IRigidBodyProxy[] bodies, float dt) { }

        /// <summary>位置约束预求解（子类可重写）。</summary>
        public virtual void InitPositionSolve(IRigidBodyProxy[] bodies, float dt) { }

        /// <summary>位置约束迭代求解（子类可重写）。</summary>
        public virtual void SolvePosition(IRigidBodyProxy[] bodies, float dt) { }

        /// <summary>断裂检测（子类可重写）。</summary>
        public virtual void CheckBreak() { }

        /// <summary>记录约束连接的两个刚体索引。</summary>
        internal void SetBodyIndices(int bodyIndexA, int bodyIndexB)
        {
            BodyIndexA = bodyIndexA;
            BodyIndexB = bodyIndexB;
        }

        ~Constraint() => Dispose();
    }

    // --------------------------------------------------------------------------------------------
    //  Concrete constraint types
    // --------------------------------------------------------------------------------------------

    /// <summary>
    ///   Ball-and-socket (point) constraint. The two pivot points are kept coincident, but the
    ///   bodies may rotate freely around that shared point.
    /// </summary>
    public sealed class BallConstraint : Constraint
    {
        /// <summary>Pivot in body A's local space.</summary>
        public Vector3 LocalPivotA { get; }

        /// <summary>Pivot in body B's local space.</summary>
        public Vector3 LocalPivotB { get; }

        internal BallConstraint(PhysicsSystemHandle system, uint bodyA, uint bodyB,
                                 Vector3 localPivotA, Vector3 localPivotB, uint nativeId)
            : base(system, ConstraintKind.Ball, bodyA, bodyB, nativeId)
        {
            LocalPivotA = localPivotA;
            LocalPivotB = localPivotB;
        }
    }

    /// <summary>
    ///   Fixed (weld) constraint. The two bodies are rigidly welded together at their
    ///   current relative transform.
    /// </summary>
    public sealed class FixedConstraint : Constraint
    {
        internal FixedConstraint(PhysicsSystemHandle system, uint bodyA, uint bodyB, uint nativeId)
            : base(system, ConstraintKind.Fixed, bodyA, bodyB, nativeId) { }
    }

    /// <summary>
    ///   Hinge constraint. Allows rotation only around a single axis through a shared pivot.
    /// </summary>
    public sealed class HingeConstraint : Constraint
    {
        /// <summary>枢轴在 body A 本地空间。</summary>
        public Vector3 LocalPivotA { get; }

        /// <summary>旋转轴在 body A 本地空间。</summary>
        public Vector3 LocalAxisA { get; }

        /// <summary>枢轴在 body B 本地空间。</summary>
        public Vector3 LocalPivotB { get; }

        /// <summary>旋转轴在 body B 本地空间。</summary>
        public Vector3 LocalAxisB { get; }

        /// <summary>当前约束角度（弧度）。</summary>
        public float Angle { get; private set; }

        internal HingeConstraint(PhysicsSystemHandle system, uint bodyA, uint bodyB,
                                 Vector3 localPivotA, Vector3 localAxisA,
                                 Vector3 localPivotB, Vector3 localAxisB, uint nativeId)
            : base(system, ConstraintKind.Hinge, bodyA, bodyB, nativeId)
        {
            LocalPivotA = localPivotA;
            LocalAxisA = localAxisA;
            LocalPivotB = localPivotB;
            LocalAxisB = localAxisB;
        }
    }

    /// <summary>
    ///   Slider constraint. Allows translation only along a single axis.
    /// </summary>
    public sealed class SliderConstraint : Constraint
    {
        /// <summary>枢轴在 body A 本地空间。</summary>
        public Vector3 LocalPivotA { get; }

        /// <summary>滑动轴在 body A 本地空间。</summary>
        public Vector3 LocalAxisA { get; }

        /// <summary>枢轴在 body B 本地空间。</summary>
        public Vector3 LocalPivotB { get; }

        /// <summary>滑动轴在 body B 本地空间。</summary>
        public Vector3 LocalAxisB { get; }

        /// <summary>最小滑动距离。</summary>
        public float MinLimit { get; }

        /// <summary>最大滑动距离。</summary>
        public float MaxLimit { get; }

        internal SliderConstraint(PhysicsSystemHandle system, uint bodyA, uint bodyB,
                                  Vector3 localPivotA, Vector3 localAxisA,
                                  Vector3 localPivotB, Vector3 localAxisB,
                                  float minLimit, float maxLimit, uint nativeId)
            : base(system, ConstraintKind.Slider, bodyA, bodyB, nativeId)
        {
            LocalPivotA = localPivotA;
            LocalAxisA = localAxisA;
            LocalPivotB = localPivotB;
            LocalAxisB = localAxisB;
            MinLimit = minLimit;
            MaxLimit = maxLimit;
        }
    }

    /// <summary>
    ///   Distance constraint. Keeps the two pivot points within [minDistance, maxDistance].
    /// </summary>
    public sealed class DistanceConstraint : Constraint
    {
        /// <summary>锚点在 body A 的本地空间。</summary>
        public Vector3 LocalPivotA { get; }

        /// <summary>锚点在 body B 的本地空间。</summary>
        public Vector3 LocalPivotB { get; }

        /// <summary>最小距离。</summary>
        public float MinDistance { get; }

        /// <summary>最大距离。</summary>
        public float MaxDistance { get; }

        internal DistanceConstraint(PhysicsSystemHandle system, uint bodyA, uint bodyB,
                                    Vector3 localPivotA, Vector3 localPivotB,
                                    float minDistance, float maxDistance, uint nativeId)
            : base(system, ConstraintKind.Distance, bodyA, bodyB, nativeId)
        {
            LocalPivotA = localPivotA;
            LocalPivotB = localPivotB;
            MinDistance = minDistance;
            MaxDistance = maxDistance;
        }
    }

    /// <summary>
    ///   Hinge constraint. Allows rotation only around a single axis through a shared pivot.
    ///   Supports optional angle limits and a velocity/position motor.
    /// </summary>

    /// <summary>
    ///   Slider constraint. Allows translation only along a single axis. Supports optional
    ///   distance limits and a velocity/position motor.
    /// </summary>

    /// <summary>
    ///   Distance constraint. Keeps the two pivot points within [minDistance, maxDistance].
    ///   Useful for ropes, springs, and tethers.
    /// </summary>

    /// <summary>
    ///   Fixed constraint. Welds two bodies together so they move as one.
    /// </summary>

    // --------------------------------------------------------------------------------------------
    //  ConstraintFactory — used by PhysicsWorld to construct constraints and hand them back
    //  to the caller already created and activated.
    // --------------------------------------------------------------------------------------------

    /// <summary>
    ///   Factory that creates native Jolt constraints and wraps them in managed objects.
    ///   Instances are held by <see cref="PhysicsWorld"/>; you normally obtain constraints
    ///   through <c>PhysicsWorld.CreateHinge</c> etc. rather than calling this directly.
    /// </summary>
    public sealed class ConstraintFactory
    {
        private readonly PhysicsSystemHandle _system;

        internal ConstraintFactory(PhysicsSystemHandle system)
        {
            _system = system;
        }

        /// <summary>
        ///   Create a ball-and-socket constraint between two bodies.
        /// </summary>
        /// <param name="bodyA">First body Jolt ID.</param>
        /// <param name="bodyB">Second body Jolt ID.</param>
        /// <param name="worldPivot">Pivot point in world space (will be converted to local space for both bodies).</param>
        public BallConstraint CreateBall(uint bodyA, uint bodyB, Vector3 worldPivot)
        {
            // Convert world pivot to approximate local pivots. The native side also receives
            // the world pivot and does the exact local-space conversion using each body's
            // current transform, but we store the world pivot for the managed side.
            Vector3 localA = Vector3.Zero; // native computes from worldPivot
            Vector3 localB = Vector3.Zero;

            uint id = JoltNative.SJ_Constraint_CreatePoint(
                _system, bodyA, bodyB, worldPivot, worldPivot);

            var c = new BallConstraint(_system, bodyA, bodyB, localA, localB, id);
            c.SetNativeId(id);
            return c;
        }

        /// <summary>
        ///   Create a ball-and-socket constraint with explicit local-space pivots.
        /// </summary>
        public BallConstraint CreateBallLocal(uint bodyA, uint bodyB, Vector3 localPivotA, Vector3 localPivotB)
        {
            // We pass local pivots as-is; the native CreatePoint interprets them as local offsets.
            uint id = JoltNative.SJ_Constraint_CreatePoint(
                _system, bodyA, bodyB, localPivotA, localPivotB);

            var c = new BallConstraint(_system, bodyA, bodyB, localPivotA, localPivotB, id);
            c.SetNativeId(id);
            return c;
        }

        /// <summary>
        ///   Create a hinge constraint between two bodies.
        /// </summary>
        /// <param name="bodyA">First body Jolt ID.</param>
        /// <param name="bodyB">Second body Jolt ID.</param>
        /// <param name="worldPivot">Hinge pivot point in world space.</param>
        /// <param name="hingeAxis">Hinge rotation axis in world space (will be normalised).</param>
        public HingeConstraint CreateHinge(uint bodyA, uint bodyB, Vector3 worldPivot, Vector3 hingeAxis)
        {
            Vector3 axis = hingeAxis.LengthSquared() > 1e-8f
                ? Vector3.Normalize(hingeAxis)
                : Vector3.UnitZ;

            uint id = JoltNative.SJ_Constraint_CreateHinge(
                _system, bodyA, bodyB, worldPivot, worldPivot, axis, axis);

            var c = new HingeConstraint(_system, bodyA, bodyB, worldPivot, axis, worldPivot, axis, id);
            c.SetNativeId(id);
            return c;
        }

        /// <summary>
        ///   Create a hinge constraint with explicit local-space pivots and axes.
        /// </summary>
        public HingeConstraint CreateHingeLocal(
            uint bodyA, uint bodyB,
            Vector3 localPivotA, Vector3 localAxisA,
            Vector3 localPivotB, Vector3 localAxisB)
        {
            Vector3 axisA = localAxisA.LengthSquared() > 1e-8f ? Vector3.Normalize(localAxisA) : Vector3.UnitZ;
            Vector3 axisB = localAxisB.LengthSquared() > 1e-8f ? Vector3.Normalize(localAxisB) : Vector3.UnitZ;

            uint id = JoltNative.SJ_Constraint_CreateHinge(
                _system, bodyA, bodyB, localPivotA, localPivotB, axisA, axisB);

            var c = new HingeConstraint(_system, bodyA, bodyB, localPivotA, axisA, localPivotB, axisB, id);
            c.SetNativeId(id);
            return c;
        }

        /// <summary>
        ///   Create a slider constraint between two bodies.
        /// </summary>
        /// <param name="bodyA">First body Jolt ID.</param>
        /// <param name="bodyB">Second body Jolt ID.</param>
        /// <param name="worldPivot">Slider pivot in world space.</param>
        /// <param name="sliderAxis">Slide direction in world space (will be normalised).</param>
        /// <param name="minLimit">Minimum slide distance (metres).</param>
        /// <param name="maxLimit">Maximum slide distance (metres).</param>
        public SliderConstraint CreateSlider(
            uint bodyA, uint bodyB, Vector3 worldPivot, Vector3 sliderAxis,
            float minLimit = float.NegativeInfinity, float maxLimit = float.PositiveInfinity)
        {
            Vector3 axis = sliderAxis.LengthSquared() > 1e-8f
                ? Vector3.Normalize(sliderAxis)
                : Vector3.UnitZ;

            float min = float.IsNegativeInfinity(minLimit) ? -1e6f : minLimit;
            float max = float.IsPositiveInfinity(maxLimit) ? 1e6f : maxLimit;

            uint id = JoltNative.SJ_Constraint_CreateSlider(
                _system, bodyA, bodyB, worldPivot, worldPivot, axis, axis, min, max);

            var c = new SliderConstraint(_system, bodyA, bodyB, worldPivot, axis, worldPivot, axis, min, max, id);
            c.SetNativeId(id);
            return c;
        }

        /// <summary>
        ///   Create a slider constraint with explicit local-space pivots and axes.
        /// </summary>
        public SliderConstraint CreateSliderLocal(
            uint bodyA, uint bodyB,
            Vector3 localPivotA, Vector3 localAxisA,
            Vector3 localPivotB, Vector3 localAxisB,
            float minLimit = -1e6f, float maxLimit = 1e6f)
        {
            Vector3 axisA = localAxisA.LengthSquared() > 1e-8f ? Vector3.Normalize(localAxisA) : Vector3.UnitZ;
            Vector3 axisB = localAxisB.LengthSquared() > 1e-8f ? Vector3.Normalize(localAxisB) : Vector3.UnitZ;

            uint id = JoltNative.SJ_Constraint_CreateSlider(
                _system, bodyA, bodyB, localPivotA, localPivotB, axisA, axisB, minLimit, maxLimit);

            var c = new SliderConstraint(_system, bodyA, bodyB, localPivotA, axisA, localPivotB, axisB, minLimit, maxLimit, id);
            c.SetNativeId(id);
            return c;
        }

        /// <summary>
        ///   Create a distance (rope/spring) constraint.
        /// </summary>
        /// <param name="bodyA">First body Jolt ID.</param>
        /// <param name="bodyB">Second body Jolt ID.</param>
        /// <param name="worldPivotA">Anchor point on body A in world space.</param>
        /// <param name="worldPivotB">Anchor point on body B in world space.</param>
        /// <param name="minDistance">Minimum separation (metres). Use current distance for a rope.</param>
        /// <param name="maxDistance">Maximum separation (metres). Use current distance for a rigid rod.</param>
        public DistanceConstraint CreateDistance(
            uint bodyA, uint bodyB,
            Vector3 worldPivotA, Vector3 worldPivotB,
            float minDistance, float maxDistance)
        {
            uint id = JoltNative.SJ_Constraint_CreateDistance(
                _system, bodyA, bodyB, worldPivotA, worldPivotB, minDistance, maxDistance);

            var c = new DistanceConstraint(_system, bodyA, bodyB, worldPivotA, worldPivotB, minDistance, maxDistance, id);
            c.SetNativeId(id);
            return c;
        }

        /// <summary>
        ///   Create a fixed (weld) constraint between two bodies.
        /// </summary>
        public FixedConstraint CreateFixed(uint bodyA, uint bodyB)
        {
            uint id = JoltNative.SJ_Constraint_CreateFixed(_system, bodyA, bodyB);
            var c = new FixedConstraint(_system, bodyA, bodyB, id);
            c.SetNativeId(id);
            return c;
        }
    }
}
