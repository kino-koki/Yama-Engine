using System;
using System.Numerics;

namespace ShanyuEngine.Physics
{
    // ============================================================================================
    //  CharacterController.cs
    //
    //  Capsule-based character controller built on Jolt's CharacterVirtual. Unlike a rigid body,
    //  a character controller is moved kinematically each frame using collision queries, which
    //  gives precise control over movement, stair-stepping, slope handling, and sliding.
    //
    //  Features:
    //    - Horizontal movement with configurable speed and acceleration.
    //    - Jumping with gravity and optional variable jump height.
    //    - Slope limit: surfaces steeper than MaxSlopeAngle are treated as walls (slide down).
    //    - Step climbing: automatically walks up steps shorter than StepHeight.
    //    - Ground detection with state (OnGround / OnSteepGround / InAir).
    //    - Push-out from other dynamic bodies.
    //    - Stick-to-floor to prevent floating on stairs.
    //
    //  The controller does NOT own a Jolt body; it uses the physics system's broadphase to
    //  query collisions each Update. Call <see cref="Move"/> or <see cref="Update"/> every
    //  fixed timestep.
    // ============================================================================================

    /// <summary>Result of a character ground check.</summary>
    public enum CharacterGroundState
    {
        /// <summary>Character is firmly on a walkable surface.</summary>
        OnGround,
        /// <summary>Standing on a surface steeper than the slope limit (will slide).</summary>
        OnSteepGround,
        /// <summary>Not supported (e.g. standing on another character).</summary>
        NotSupported,
        /// <summary>In the air (falling or jumping).</summary>
        InAir,
    }

    /// <summary>Configuration for a character controller.</summary>
    public sealed class CharacterSettings
    {
        /// <summary>Radius of the capsule (metres).</summary>
        public float Radius = 0.3f;

        /// <summary>
        ///   Half-height of the cylindrical portion of the capsule (metres). The total capsule
        ///   height is <c>2 * (Radius + HalfHeight)</c>.
        /// </summary>
        public float HalfHeight = 0.6f;

        /// <summary>Maximum slope angle the character can walk on (radians). Default ~50 degrees.</summary>
        public float MaxSlopeAngle = 50f * MathF.PI / 180f;

        /// <summary>Maximum step height the character can climb without jumping (metres).</summary>
        public float StepHeight = 0.3f;

        /// <summary>Walk speed (m/s).</summary>
        public float WalkSpeed = 5.0f;

        /// <summary>Sprint speed multiplier applied to WalkSpeed.</summary>
        public float SprintMultiplier = 1.8f;

        /// <summary>How quickly the character accelerates toward the target horizontal velocity (m/s²).</summary>
        public float Acceleration = 40.0f;

        /// <summary>How quickly the character decelerates when no input is given (m/s²).</summary>
        public float Deceleration = 60.0f;

        /// <summary>Initial jump velocity (m/s). Applied as an upward impulse on Jump().</summary>
        public float JumpSpeed = 6.0f;

        /// <summary>Gravity applied to the character when airborne (m/s²). Overrides world gravity if set.</summary>
        public float? OverrideGravity;

        /// <summary>Mass used when pushing dynamic bodies (kg). Higher = pushes harder.</summary>
        public float Mass = 70.0f;

        /// <summary>How strongly the character sticks to the floor (metres). Prevents bouncing on stairs.</summary>
        public float StickToFloorDistance = 0.05f;

        /// <summary>Collision layer for the character's own queries.</summary>
        public ushort ObjectLayer = 1;

        /// <summary>Collision mask (which layers the character collides with).</summary>
        public ushort CollisionMask = 0xFFFF;

        /// <summary>Whether the character can rotate to face movement direction. Off for top-down games.</summary>
        public bool AutoRotate = true;

        /// <summary>How fast the character rotates to face the movement direction (rad/s).</summary>
        public float RotationSpeed = 12.0f;
    }

    /// <summary>
    ///   Snapshot of the character controller's state after an update.
    /// </summary>
    public readonly struct CharacterState
    {
        /// <summary>World position of the character's center.</summary>
        public readonly Vector3 Position;

        /// <summary>Current rotation.</summary>
        public readonly Quaternion Rotation;

        /// <summary>Current linear velocity (m/s).</summary>
        public readonly Vector3 Velocity;

        /// <summary>Ground state this frame.</summary>
        public readonly CharacterGroundState GroundState;

        /// <summary>Surface normal of the ground beneath the character (Up if airborne).</summary>
        public readonly Vector3 GroundNormal;

        /// <summary>World position of the ground contact point (if grounded).</summary>
        public readonly Vector3 GroundPosition;

        /// <summary>Whether a jump was triggered this frame.</summary>
        public readonly bool Jumped;

        /// <summary>Actual horizontal speed (m/s), useful for animation.</summary>
        public readonly float HorizontalSpeed;

        public CharacterState(Vector3 pos, Quaternion rot, Vector3 vel,
                              CharacterGroundState ground, Vector3 groundNormal,
                              Vector3 groundPos, bool jumped, float hSpeed)
        {
            Position = pos;
            Rotation = rot;
            Velocity = vel;
            GroundState = ground;
            GroundNormal = groundNormal;
            GroundPosition = groundPos;
            Jumped = jumped;
            HorizontalSpeed = hSpeed;
        }
    }

    /// <summary>
    ///   A capsule-based character controller that moves through the physics world using
    ///   collision queries, with built-in slope handling, step climbing, and jumping.
    /// </summary>
    public sealed class CharacterController : IDisposable
    {
        private readonly PhysicsSystemHandle _system;
        private readonly CharacterHandle _handle;
        private readonly CharacterSettings _settings;
        private bool _disposed;

        // Current managed-side velocity (used for smoothing / acceleration).
        private Vector3 _horizontalVelocity;
        private float _verticalVelocity;
        private bool _jumpRequested;
        private float _facingAngle;

        /// <summary>The physics system this controller belongs to.</summary>
        public PhysicsSystemHandle System => _system;

        /// <summary>The native character handle.</summary>
        public CharacterHandle Handle => _handle;

        /// <summary>The settings used to create this controller (mutable fields are read live).</summary>
        public CharacterSettings Settings => _settings;

        /// <summary>Whether the controller has been disposed.</summary>
        public bool IsDisposed => _disposed;

        /// <summary>Last computed state after an Update.</summary>
        public CharacterState State { get; private set; }

        /// <summary>Whether the character is currently grounded.</summary>
        public bool IsGrounded => State.GroundState == CharacterGroundState.OnGround;

        /// <summary>Current world position.</summary>
        public Vector3 Position => State.Position;

        /// <summary>Current rotation.</summary>
        public Quaternion Rotation => State.Rotation;

        /// <summary>Current velocity (m/s).</summary>
        public Vector3 Velocity => State.Velocity;

        internal CharacterController(PhysicsSystemHandle system, CharacterSettings settings)
        {
            _system = system;
            _settings = settings;

            _handle = JoltNative.SJ_Character_Create(
                system,
                settings.Radius,
                settings.HalfHeight,
                Vector3.Zero,
                Quaternion.Identity,
                settings.ObjectLayer,
                settings.CollisionMask,
                settings.MaxSlopeAngle,
                settings.StepHeight,
                settings.Mass);

            if (_handle.IsNull)
                throw new InvalidOperationException("Failed to create native character controller.");

            State = new CharacterState(
                Vector3.Zero, Quaternion.Identity, Vector3.Zero,
                CharacterGroundState.InAir, Vector3.UnitY, Vector3.Zero, false, 0f);
        }

        // ----------------------------------------------------------------------------------------
        //  Positioning
        // ----------------------------------------------------------------------------------------

        /// <summary>Teleport the character to a world position (bypasses collision).</summary>
        public void SetPosition(Vector3 position)
        {
            ThrowIfDisposed();
            JoltNative.SJ_Character_SetPosition(_system, _handle, position);
            _horizontalVelocity = Vector3.Zero;
            _verticalVelocity = 0f;
        }

        /// <summary>Set the character's rotation directly.</summary>
        public void SetRotation(Quaternion rotation)
        {
            ThrowIfDisposed();
            JoltNative.SJ_Character_SetRotation(_system, _handle, rotation);
        }

        /// <summary>Get the current world position from the native side.</summary>
        public Vector3 GetNativePosition()
        {
            ThrowIfDisposed();
            return JoltNative.SJ_Character_GetPosition(_system, _handle);
        }

        // ----------------------------------------------------------------------------------------
        //  Input / movement
        // ----------------------------------------------------------------------------------------

        /// <summary>
        ///   Request a jump on the next Update. The jump only fires if the character is grounded.
        /// </summary>
        public void Jump()
        {
            _jumpRequested = true;
        }

        /// <summary>
        ///   Set the desired horizontal movement direction (in world space). The vector is
        ///   normalised internally; pass Vector3.Zero to stop.
        /// </summary>
        /// <param name="worldDirection">Desired move direction (need not be normalised).</param>
        /// <param name="sprint">Whether to apply the sprint speed multiplier.</param>
        public void Move(Vector3 worldDirection, bool sprint = false)
        {
            float speed = _settings.WalkSpeed * (sprint ? _settings.SprintMultiplier : 1f);

            Vector3 dir;
            if (worldDirection.LengthSquared() > 1e-6f)
                dir = Vector3.Normalize(new Vector3(worldDirection.X, 0, worldDirection.Z));
            else
                dir = Vector3.Zero;

            _targetHorizontal = dir * speed;
        }

        private Vector3 _targetHorizontal;

        // ----------------------------------------------------------------------------------------
        //  Update — called every fixed timestep by the PhysicsWorld.
        // ----------------------------------------------------------------------------------------

        /// <summary>
        ///   Advance the character by one fixed timestep. Applies gravity, smoothing, and runs
        ///   the native collision update (slope / stair / slide handling).
        /// </summary>
        /// <param name="dt">Fixed delta time (seconds).</param>
        /// <param name="worldGravity">Gravity vector from the physics world.</param>
        /// <returns>The updated character state.</returns>
        public CharacterState Update(float dt, Vector3 worldGravity)
        {
            ThrowIfDisposed();

            Vector3 gravity = _settings.OverrideGravity ?? worldGravity;

            // --- Smooth horizontal velocity toward target ------------------------------------
            Vector3 currentHoriz = new(_horizontalVelocity.X, 0, _horizontalVelocity.Z);
            float accel = _targetHorizontal.LengthSquared() > 1e-6f
                ? _settings.Acceleration
                : _settings.Deceleration;

            _horizontalVelocity = Vector3.Lerp(currentHoriz, _targetHorizontal, Math.Clamp(accel * dt, 0f, 1f));

            // --- Vertical velocity (gravity + jump) ------------------------------------------
            bool wasGrounded = State.GroundState == CharacterGroundState.OnGround;

            if (_jumpRequested && wasGrounded)
            {
                _verticalVelocity = _settings.JumpSpeed;
                _jumpRequested = false;
            }
            else
            {
                _jumpRequested = false;
                // Apply gravity (only when not grounded, or grounded on steep slope)
                if (!wasGrounded || State.GroundState == CharacterGroundState.OnSteepGround)
                    _verticalVelocity += gravity.Y * dt;
                else
                    _verticalVelocity = 0f;
            }

            // Combine into desired world velocity
            Vector3 desiredVelocity = new(
                _horizontalVelocity.X,
                _verticalVelocity,
                _horizontalVelocity.Z);

            // --- Native update (collision, stair-stepping, slope handling) -------------------
            var updateSettings = new NativeCharacterUpdateSettings
            {
                MaxSlopeAngle = _settings.MaxSlopeAngle,
                UpStandHeight = _settings.StepHeight,
                DownStandHeight = _settings.StepHeight,
                StickToFloorDistance = _settings.StickToFloorDistance,
                WalkStairsExtraDistance = 0.1f,
                WalkStairsUpExtraHeight = _settings.StepHeight * 0.5f,
                Up = Vector3.UnitY,
            };

            Vector3 newPos = JoltNative.SJ_Character_Update(
                _system, _handle, dt, gravity, desiredVelocity,
                _settings.MaxSlopeAngle, in updateSettings, out NativeGroundState nativeGround);

            // --- Read back ground state ------------------------------------------------------
            var groundState = (SJ_GroundState)nativeGround.GroundState switch
            {
                SJ_GroundState.OnGround => CharacterGroundState.OnGround,
                SJ_GroundState.OnSteepGround => CharacterGroundState.OnSteepGround,
                SJ_GroundState.NotSupported => CharacterGroundState.NotSupported,
                _ => CharacterGroundState.InAir,
            };

            Vector3 groundNormal = nativeGround.GroundNormal;
            if (groundNormal.LengthSquared() < 1e-6f)
                groundNormal = Vector3.UnitY;

            // If we just landed, cancel residual downward velocity.
            if (groundState == CharacterGroundState.OnGround && _verticalVelocity < 0f)
                _verticalVelocity = 0f;

            // If sliding on steep ground, apply gravity along the slope tangent.
            if (groundState == CharacterGroundState.OnSteepGround)
            {
                Vector3 slideDir = Vector3.Normalize(gravity - groundNormal * Vector3.Dot(gravity, groundNormal));
                _verticalVelocity += slideDir.Y * gravity.Length() * dt * 0.5f;
            }

            // --- Auto-rotate to face movement direction --------------------------------------
            Quaternion rot = State.Rotation;
            if (_settings.AutoRotate && _horizontalVelocity.LengthSquared() > 0.1f)
            {
                float targetAngle = MathF.Atan2(_horizontalVelocity.X, _horizontalVelocity.Z);
                // Smoothly interpolate the facing angle
                float delta = AngleDelta(targetAngle, _facingAngle);
                _facingAngle += Math.Clamp(delta, -_settings.RotationSpeed * dt, _settings.RotationSpeed * dt);
                rot = Quaternion.CreateFromAxisAngle(Vector3.UnitY, _facingAngle);
            }

            float horizSpeed = new Vector2(_horizontalVelocity.X, _horizontalVelocity.Z).Length();

            State = new CharacterState(
                newPos, rot, desiredVelocity,
                groundState, groundNormal,
                nativeGround.GroundPosition,
                State.Jumped || _verticalVelocity > 0.1f && !wasGrounded,
                horizSpeed);

            // Update the native character's stored velocity so queries reflect it.
            JoltNative.SJ_Character_SetLinearVelocity(_system, _handle, desiredVelocity);

            return State;
        }

        /// <summary>Compute the shortest signed angular difference from 'from' to 'to'.</summary>
        private static float AngleDelta(float to, float from)
        {
            float d = to - from;
            while (d > MathF.PI) d -= 2f * MathF.PI;
            while (d < -MathF.PI) d += 2f * MathF.PI;
            return d;
        }

        // ----------------------------------------------------------------------------------------
        //  Queries
        // ----------------------------------------------------------------------------------------

        /// <summary>Get the raw native ground state byte.</summary>
        public CharacterGroundState GetGroundState()
        {
            ThrowIfDisposed();
            byte s = JoltNative.SJ_Character_GetGroundState(_system, _handle);
            return (SJ_GroundState)s switch
            {
                SJ_GroundState.OnGround => CharacterGroundState.OnGround,
                SJ_GroundState.OnSteepGround => CharacterGroundState.OnSteepGround,
                SJ_GroundState.NotSupported => CharacterGroundState.NotSupported,
                _ => CharacterGroundState.InAir,
            };
        }

        /// <summary>Get the ground normal beneath the character.</summary>
        public Vector3 GetGroundNormal()
        {
            ThrowIfDisposed();
            return JoltNative.SJ_Character_GetGroundNormal(_system, _handle);
        }

        /// <summary>Get the body ID the character is standing on (0 if airborne).</summary>
        public uint GetGroundBodyId()
        {
            ThrowIfDisposed();
            return JoltNative.SJ_Character_GetGroundBodyId(_system, _handle);
        }

        /// <summary>Enable or disable enhanced internal edge removal (reduces catching on mesh seams).</summary>
        public void SetEnhancedEdgeRemoval(bool enabled)
        {
            ThrowIfDisposed();
            JoltNative.SJ_Character_SetEnhancedInternalEdgeRemoval(_system, _handle, enabled);
        }

        // ----------------------------------------------------------------------------------------
        //  Disposal
        // ----------------------------------------------------------------------------------------

        /// <summary>Destroy the native character controller.</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (!_handle.IsNull)
                JoltNative.SJ_Character_Destroy(_system, _handle);
            GC.SuppressFinalize(this);
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(CharacterController));
        }

        ~CharacterController() => Dispose();
    }
}
