using System;
using System.Numerics;

namespace YamaEngine.Physics
{
    // ============================================================================================
    //  UnityCharacterController.cs
    //
    //  角色控制器系统 — 替代原 CharacterController.cs，提供更完整的 Unity 风格角色控制。
    //
    //  功能：
    //    - 角色控制参数（移动速度 / 跳跃力 / 旋转速度 / 斜率限制 / 步高）
    //    - 移动逻辑（Move 方法 — 滑动碰撞体移动）
    //    - 重力处理（自定义重力 / 地面检测）
    //    - 跳跃（Jump / IsGrounded / FallMultiplier）
    //    - 斜坡处理（最大坡度角 / 坡度滑动）
    //    - 台阶处理（最大步高 / 自动台阶）
    //    - 墙壁滑动
    //    - 空中控制
    //    - 地面锁定
    //    - 与物理层级联动
    // ============================================================================================

    /// <summary>
    ///   角色控制器配置参数。
    /// </summary>
    public sealed class UnityCharacterSettings
    {
        /// <summary>胶囊体半径。</summary>
        public float Radius = 0.3f;

        /// <summary>胶囊体半高（不含半球）。</summary>
        public float HalfHeight = 0.6f;

        /// <summary>最大行走速度 (m/s)。</summary>
        public float WalkSpeed = 5f;

        /// <summary>最大奔跑速度 (m/s)。</summary>
        public float RunSpeed = 8f;

        /// <summary>跳跃初始速度 (m/s)。</summary>
        public float JumpSpeed = 6f;

        /// <summary>重力倍率（下落加速倍率）。</summary>
        public float GravityMultiplier = 2f;

        /// <summary>下落时的重力倍率（用于更快的下落）。</summary>
        public float FallMultiplier = 2.5f;

        /// <summary>最大可攀爬斜坡角度（度）。</summary>
        public float SlopeLimit = 50f;

        /// <summary>最大可跨越步高 (米)。</summary>
        public float StepHeight = 0.3f;

        /// <summary>加速度 (m/s^2)。</summary>
        public float Acceleration = 40f;

        /// <summary>减速度（无输入时）(m/s^2)。</summary>
        public float Deceleration = 60f;

        /// <summary>旋转速度 (rad/s)。</summary>
        public float RotationSpeed = 12f;

        /// <summary>空中控制倍率（0=空中无法移动，1=与地面相同）。</summary>
        public float AirControl = 0.3f;

        /// <summary>贴地距离 (m)。用于防止在台阶/斜坡上弹跳。</summary>
        public float StickToGroundDistance = 0.05f;

        /// <summary>质量（用于推动其他刚体）。</summary>
        public float Mass = 70f;

        /// <summary>碰撞层。</summary>
        public PhysicsLayer Layer = PhysicsLayer.Player;

        /// <summary>碰撞层级掩码。</summary>
        public uint LayerMask = PhysicsLayerMask.GetMask(
            PhysicsLayer.Default, PhysicsLayer.NPC,
            PhysicsLayer.Environment, PhysicsLayer.Weapon,
            PhysicsLayer.Vehicle, PhysicsLayer.Trigger,
            PhysicsLayer.Water);

        /// <summary>是否自动面向移动方向。</summary>
        public bool AutoRotate = true;

        /// <summary>墙壁滑动的摩擦力倍率（0=完全滑下，1=粘在墙上）。</summary>
        public float WallFriction = 0.1f;
    }

    /// <summary>
    ///   角色控制器状态。
    /// </summary>
    public struct UnityCharacterState
    {
        /// <summary>当前位置。</summary>
        public Vector3 Position;

        /// <summary>当前旋转。</summary>
        public Quaternion Rotation;

        /// <summary>当前线速度。</summary>
        public Vector3 Velocity;

        /// <summary>是否在地面上。</summary>
        public bool IsGrounded;

        /// <summary>地面法线。</summary>
        public Vector3 GroundNormal;

        /// <summary>地面接触点。</summary>
        public Vector3 GroundPoint;

        /// <summary>是否接触墙壁。</summary>
        public bool IsTouchingWall;

        /// <summary>墙壁法线。</summary>
        public Vector3 WallNormal;

        /// <summary>当前水平速度 (m/s)。</summary>
        public float HorizontalSpeed;

        /// <summary>当前攀登的步高。</summary>
        public float StepHeightClimbed;
    }

    /// <summary>
    ///   Unity 风格的角色控制器。替代原 CharacterController.cs。
    ///   基于 Jolt 的 CharacterVirtual，增加 Unity 风格的 API 和功能：
    ///   - Move / SimpleMove 方法
    ///   - 重力自定义
    ///   - 斜坡/台阶/墙壁滑动
    ///   - 空中控制
    ///   - 与物理层级联动
    /// </summary>
    public sealed class UnityCharacterController : IDisposable
    {
        private readonly PhysicsWorld _world;
        private CharacterHandle _handle;
        private readonly UnityCharacterSettings _settings;
        private bool _disposed;

        // 内部状态
        private Vector3 _horizontalVelocity;
        private float _verticalVelocity;
        private bool _jumpRequested;
        private float _facingAngle;
        private UnityCharacterState _state;

        /// <summary>基础设置（只读）。</summary>
        public UnityCharacterSettings Settings => _settings;

        /// <summary>当前状态。</summary>
        public UnityCharacterState State => _state;

        /// <summary>当前位置。</summary>
        public Vector3 Position => _state.Position;

        /// <summary>当前旋转。</summary>
        public Quaternion Rotation => _state.Rotation;

        /// <summary>当前速度。</summary>
        public Vector3 Velocity => _state.Velocity;

        /// <summary>是否在地面。</summary>
        public bool IsGrounded => _state.IsGrounded;

        internal UnityCharacterController(PhysicsWorld world, UnityCharacterSettings settings)
        {
            _world = world ?? throw new ArgumentNullException(nameof(world));
            _settings = settings;

            float slopeRad = settings.SlopeLimit * MathF.PI / 180f;

            _handle = JoltNative.SJ_Character_Create(
                world.NativeHandle,
                settings.Radius,
                settings.HalfHeight,
                Vector3.Zero,
                Quaternion.Identity,
                (ushort)(1 << (int)settings.Layer),
                (ushort)(settings.LayerMask & 0xFFFF),
                slopeRad,
                settings.StepHeight,
                settings.Mass);

            if (_handle.IsNull)
                throw new InvalidOperationException("Failed to create native character controller.");

            _state = new UnityCharacterState
            {
                Position = Vector3.Zero,
                Rotation = Quaternion.Identity,
                IsGrounded = false,
                GroundNormal = Vector3.UnitY,
            };
        }

        // ========================================================================================
        //  移动 API
        // ========================================================================================

        /// <summary>
        ///   移动角色。输入期望的移动方向和速度。
        /// </summary>
        /// <param name="direction">期望移动方向（世界空间，水平分量有效）。</param>
        /// <param name="run">是否奔跑。</param>
        /// <param name="dt">时间步长。</param>
        /// <param name="gravity">重力向量。</param>
        public void Move(Vector3 direction, bool run, float dt, Vector3 gravity)
        {
            if (_disposed) return;

            float targetSpeed = run ? _settings.RunSpeed : _settings.WalkSpeed;
            bool hasInput = direction.LengthSquared() > 1e-6f;

            // 水平方向（取水平分量）
            Vector3 moveDir = hasInput
                ? Vector3.Normalize(new Vector3(direction.X, 0, direction.Z))
                : Vector3.Zero;

            Vector3 targetVelocity = moveDir * targetSpeed;

            // 空中控制衰减
            float airFactor = _state.IsGrounded ? 1f : _settings.AirControl;
            float accel = hasInput ? _settings.Acceleration : _settings.Deceleration;
            accel *= airFactor;

            // 平滑速度
            Vector3 currentHoriz = new(_horizontalVelocity.X, 0, _horizontalVelocity.Z);
            _horizontalVelocity = Vector3.Lerp(currentHoriz, targetVelocity,
                Math.Clamp(accel * dt, 0f, 1f));

            // 重力与跳跃
            bool wasGrounded = _state.IsGrounded;

            if (_jumpRequested && wasGrounded)
            {
                _verticalVelocity = _settings.JumpSpeed;
                _jumpRequested = false;
            }
            else
            {
                _jumpRequested = false;
                if (!wasGrounded)
                {
                    // 使用 FallMultiplier 使下落更快
                    float gravScale = _verticalVelocity < 0
                        ? _settings.FallMultiplier
                        : _settings.GravityMultiplier;
                    _verticalVelocity += gravity.Y * gravScale * dt;
                }
                else
                {
                    _verticalVelocity = 0f;
                }
            }

            // 组合速度
            Vector3 desiredVelocity = new(
                _horizontalVelocity.X,
                _verticalVelocity,
                _horizontalVelocity.Z);

            // 执行原生更新
            var updateSettings = new NativeCharacterUpdateSettings
            {
                MaxSlopeAngle = _settings.SlopeLimit * MathF.PI / 180f,
                UpStandHeight = _settings.StepHeight,
                DownStandHeight = _settings.StepHeight,
                StickToFloorDistance = _settings.StickToGroundDistance,
                WalkStairsExtraDistance = 0.1f,
                WalkStairsUpExtraHeight = _settings.StepHeight * 0.5f,
                Up = Vector3.UnitY,
            };

            Vector3 newPos = JoltNative.SJ_Character_Update(
                _world.NativeHandle, _handle, dt,
                gravity, desiredVelocity,
                _settings.SlopeLimit * MathF.PI / 180f,
                in updateSettings,
                out NativeGroundState nativeGround);

            // 更新状态
            var groundState = (SJ_GroundState)nativeGround.GroundState;
            _state.IsGrounded = groundState == SJ_GroundState.OnGround;
            _state.GroundNormal = nativeGround.GroundNormal.LengthSquared() > 1e-6f
                ? nativeGround.GroundNormal
                : Vector3.UnitY;
            _state.GroundPoint = nativeGround.GroundPosition;
            _state.Position = newPos;
            _state.Velocity = desiredVelocity;
            _state.HorizontalSpeed = new Vector2(_horizontalVelocity.X, _horizontalVelocity.Z).Length();

            // 墙壁检测：如果在地面上但速度被阻挡
            _state.IsTouchingWall = !_state.IsGrounded &&
                newPos == _state.Position &&
                hasInput;

            // 自动旋转
            if (_settings.AutoRotate && _horizontalVelocity.LengthSquared() > 0.1f)
            {
                float targetAngle = MathF.Atan2(_horizontalVelocity.X, _horizontalVelocity.Z);
                float delta = AngleDelta(targetAngle, _facingAngle);
                _facingAngle += Math.Clamp(delta, -_settings.RotationSpeed * dt, _settings.RotationSpeed * dt);
                _state.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, _facingAngle);
            }

            // 同步到原生存储
            JoltNative.SJ_Character_SetLinearVelocity(_world.NativeHandle, _handle, desiredVelocity);
        }

        /// <summary>
        ///   简化移动接口。输入二维方向。
        /// </summary>
        public void SimpleMove(Vector2 input, bool run, float dt, Vector3 gravity)
        {
            Vector3 dir = new(input.X, 0, input.Y);
            Move(dir, run, dt, gravity);
        }

        /// <summary>
        ///   跳跃。
        /// </summary>
        public void Jump()
        {
            _jumpRequested = true;
        }

        /// <summary>
        ///   传送角色到指定位置（绕过碰撞检测）。
        /// </summary>
        public void Teleport(Vector3 position)
        {
            if (_disposed) return;
            JoltNative.SJ_Character_SetPosition(_world.NativeHandle, _handle, position);
            _state.Position = position;
            _horizontalVelocity = Vector3.Zero;
            _verticalVelocity = 0f;
        }

        /// <summary>
        ///   设置旋转。
        /// </summary>
        public void SetRotation(Quaternion rotation)
        {
            if (_disposed) return;
            JoltNative.SJ_Character_SetRotation(_world.NativeHandle, _handle, rotation);
            _state.Rotation = rotation;
            _facingAngle = MathF.Atan2(2f * rotation.Y * rotation.W - 2f * rotation.X * rotation.Z,
                1f - 2f * rotation.Y * rotation.Y - 2f * rotation.Z * rotation.Z);
        }

        // ========================================================================================
        //  查询
        // ========================================================================================

        /// <summary>获取地面 BodyId。</summary>
        public uint GetGroundBodyId()
        {
            return _disposed ? 0 :
                JoltNative.SJ_Character_GetGroundBodyId(_world.NativeHandle, _handle);
        }

        /// <summary>设置增强内边缘移除（减少网格接缝卡顿）。</summary>
        public void SetEnhancedEdgeRemoval(bool enabled)
        {
            if (!_disposed)
                JoltNative.SJ_Character_SetEnhancedInternalEdgeRemoval(
                    _world.NativeHandle, _handle, enabled);
        }

        // ========================================================================================
        //  物理层级联动
        // ========================================================================================

        /// <summary>
        ///   更新角色碰撞层级（运行时改变碰撞过滤）。
        /// </summary>
        public void SetCollisionLayers(PhysicsLayer layer, uint layerMask)
        {
            if (_disposed) return;
            ushort joltLayer = (ushort)(1 << (int)layer);
            ushort joltMask = (ushort)(layerMask & 0xFFFF);
            JoltNative.SJ_Body_SetObjectLayer(
                _world.NativeHandle, 0, joltLayer, joltMask);
        }

        // ========================================================================================
        //  生命周期
        // ========================================================================================

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (!_handle.IsNull)
                JoltNative.SJ_Character_Destroy(_world.NativeHandle, _handle);
            _handle = default;
            GC.SuppressFinalize(this);
        }

        private static float AngleDelta(float to, float from)
        {
            float d = to - from;
            while (d > MathF.PI) d -= 2f * MathF.PI;
            while (d < -MathF.PI) d += 2f * MathF.PI;
            return d;
        }
    }
}
