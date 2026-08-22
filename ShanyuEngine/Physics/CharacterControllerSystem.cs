using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace ShanyuEngine.Physics
{
    // ============================================================================================
    //  CharacterControllerSystem.cs
    //
    //  一个独立的角色控制器系统。和Dynamic Rigidbody不一样，角色控制器是Kinematic的，
    //  不受力影响，但能推Dynamic刚体，也能被静态物挡住。
    //
    //  设计思路参考了PhysX的CCT和Jolt Character。核心就是:
    //    1) 分轴扫描(Sweep)做移动 - 先走Y轴解决上下楼梯/重力，再走XZ轴解决水平碰撞
    //    2) 滑墙处理 - 如果碰墙了，把速度沿墙法线投影，继续滑
    //    3) 自动踏步 - 小障碍直接跨过去，不用跳
    //    4) 斜坡限制 - 太陡的坡不让上
    //
    //  注意：这玩意调了好久才对，尤其是斜坡和踏步一起处理的时候各种bug。
    //  TODO: 多人游戏时多个角色互相碰撞的处理还没做
    // ============================================================================================

    /// <summary>
    ///   角色控制器的形状类型。
    ///   胶囊适合大多数人形角色，Box适合一些方形物体(比如推箱子小游戏)。
    /// </summary>
    public enum CharacterShapeType
    {
        /// <summary>胶囊(沿Y轴)。最常用，人形角色首选。</summary>
        Capsule,

        /// <summary>立方体。适合非人形，比如方块怪。</summary>
        Box,

        /// <summary>圆柱体。和胶囊差不多但底是平的，不会一直滚。</summary>
        Cylinder,
    }

    /// <summary>
    ///   角色控制器的状态。外面读取这个来判断角色现在在地上/空中/爬墙。
    /// </summary>
    public enum CharacterState
    {
        /// <summary>站在地上</summary>
        OnGround,

        /// <summary>在空中(下落中)</summary>
        Falling,

        /// <summary>在空中(上升中，比如刚跳起来)</summary>
        Jumping,

        /// <summary>正在爬斜坡</summary>
        OnSlope,

        /// <summary>碰墙了</summary>
        Blocked,

        /// <summary>在水里(浮力效果)</summary>
        Swimming,

        /// <summary>挂在墙边</summary>
        LedgeHanging,
    }

    /// <summary>
    ///   角色移动模式。Walk是正常走，Run是跑步，Crouch是蹲下，Slide是滑铲。
    ///   滑铲时胶囊变矮变长，可以钻缝。
    /// </summary>
    public enum MovementMode
    {
        Walk,
        Run,
        Crouch,
        Slide,
        Sliding,    // 滑铲中(滑行阶段)
        Air,        // 空中
        Swimming,   // 游泳
        Climbing,   // 攀爬
    }

    /// <summary>
    ///   角色控制器的配置参数。一堆可调的常量，调参用。
    /// </summary>
    public struct CharacterConfig
    {
        /// <summary>形状类型。默认胶囊。</summary>
        public CharacterShapeType ShapeType;

        /// <summary>胶囊半径。</summary>
        public float CapsuleRadius;

        /// <summary>胶囊高度(从脚到头顶，不含两个半球)。</summary>
        public float CapsuleHeight;

        /// <summary>盒子半尺寸(XZ平面)。</summary>
        public Vector3 BoxHalfExtents;

        /// <summary>最大行走速度(m/s)。</summary>
        public float MaxWalkSpeed;

        /// <summary>最大奔跑速度(m/s)。</summary>
        public float MaxRunSpeed;

        /// <summary>蹲下时的最大速度。</summary>
        public float MaxCrouchSpeed;

        /// <summary>空中最大速度(控制力比地面弱)。</summary>
        public float MaxAirSpeed;

        /// <summary>加速度。地面加速度一般比较大。</summary>
        public float GroundAcceleration;

        /// <summary>空中加速度。比地面小很多，不然就是个飞行游戏了。</summary>
        public float AirAcceleration;

        /// <summary>减速度(松开方向键时)。</summary>
        public float GroundDeceleration;

        /// <summary>跳跃高度(米)。</summary>
        public float JumpHeight;

        /// <summary>跳跃次数。2 = 二段跳。</summary>
        public int MaxJumpCount;

        /// <summary>重力倍率。1.0 = 正常重力。</summary>
        public float GravityScale;

        /// <summary>能否在空中变向。</summary>
        public bool CanAirControl;

        /// <summary>空中控制力度(0~1)。</summary>
        public float AirControlFactor;

        /// <summary>能否滑墙(贴墙时按方向能贴住)。</summary>
        public bool CanWallSlide;

        /// <summary>滑墙速度上限。</summary>
        public float WallSlideMaxSpeed;

        /// <summary>能否蹬墙跳。</summary>
        public bool CanWallJump;

        /// <summary>蹬墙跳的水平速度。</summary>
        public float WallJumpHorizontalSpeed;

        /// <summary>蹬墙跳的垂直速度。</summary>
        public float WallJumpVerticalSpeed;

        /// <summary>最大爬坡角度(度)。超过这个角度就上不去。</summary>
        public float MaxSlopeAngle;

        /// <summary>自动踏步高度。低于这个高度的台阶自动跨过去。</summary>
        public float StepHeight;

        /// <summary>地面检测距离。从脚底向下多少距离算"接触地面"。</summary>
        public float GroundCheckDistance;

        /// <summary>地面检测球半径(用球扫更稳)。</summary>
        public float GroundProbeRadius;

        /// <summary>能否推动Dynamic刚体。</summary>
        public bool CanPushBodies;

        /// <summary>推力大小。太大会推飞所有东西。</summary>
        public float PushForce;

        /// <summary>角色质量(用于计算推力)。</summary>
        public float Mass;

        /// <summary>碰撞层(对外是哪个层)。</summary>
        public ushort CollisionLayer;

        /// <summary>碰撞掩码(和哪些层碰撞)。</summary>
        public ushort CollisionMask;

        /// <summary>蹲下时的高度缩减。</summary>
        public float CrouchHeightReduction;

        /// <summary>滑铲最小速度。低于这个速度自动停止滑铲。</summary>
        public float SlideMinSpeed;

        /// <summary>滑铲加速度(重力分量+人为加成)。</summary>
        public float SlideAcceleration;

        /// <summary>滑铲减速度(平地时减速)。</summary>
        public float SlideDeceleration;

        /// <summary>摩擦系数(和地面材质有关)。</summary>
        public float Friction;

        /// <summary>是否能爬梯子。</summary>
        public bool CanClimbLadder;

        /// <summary>爬梯子速度。</summary>
        public float ClimbSpeed;

        /// <summary>是否能游水。</summary>
        public bool CanSwim;

        /// <summary>游水速度。</summary>
        public float SwimSpeed;

        /// <summary>浮力强度。1=中性浮力，>1上浮，<1下沉。</summary>
        public float Buoyancy;

        /// <summary>水阻力。</summary>
        public float WaterDrag;

        /// <summary>最大坠落速度(terminal velocity)，防止下落速度无限增长。</summary>
        public float MaxFallSpeed;

        /// <summary>转向速度(度/秒)。控制角色朝向变化的快慢。</summary>
        public float TurnSpeed;

        /// <summary>是否自动转向到移动方向。</summary>
        public bool AutoFaceMoveDirection;

        /// <summary>默认配置。</summary>
        public static CharacterConfig Default => new()
        {
            ShapeType = CharacterShapeType.Capsule,
            CapsuleRadius = 0.35f,
            CapsuleHeight = 1.8f,
            BoxHalfExtents = new(0.4f, 0.9f, 0.4f),
            MaxWalkSpeed = 5.5f,
            MaxRunSpeed = 9.0f,
            MaxCrouchSpeed = 2.5f,
            MaxAirSpeed = 7.0f,
            GroundAcceleration = 60f,
            AirAcceleration = 15f,
            GroundDeceleration = 80f,
            JumpHeight = 1.2f,
            MaxJumpCount = 2,
            GravityScale = 1.0f,
            CanAirControl = true,
            AirControlFactor = 0.5f,
            CanWallSlide = true,
            WallSlideMaxSpeed = 4f,
            CanWallJump = true,
            WallJumpHorizontalSpeed = 6f,
            WallJumpVerticalSpeed = 7f,
            MaxSlopeAngle = 55f,
            StepHeight = 0.35f,
            GroundCheckDistance = 0.15f,
            GroundProbeRadius = 0.2f,
            CanPushBodies = true,
            PushForce = 80f,
            Mass = 70f,
            CollisionLayer = PhysicsLayers.Character,
            CollisionMask = PhysicsLayers.All,
            CrouchHeightReduction = 0.7f,
            SlideMinSpeed = 2f,
            SlideAcceleration = 12f,
            SlideDeceleration = 6f,
            Friction = 0.8f,
            CanClimbLadder = true,
            ClimbSpeed = 3f,
            CanSwim = true,
            SwimSpeed = 3.5f,
            Buoyancy = 1.05f,
            WaterDrag = 4f,
            MaxFallSpeed = 55f,
            TurnSpeed = 720f,
            AutoFaceMoveDirection = true,
        };
    }

    /// <summary>
    ///   角色碰撞信息。每次碰撞检测都填充这个，外面可以读取来播放音效/触发事件。
    /// </summary>
    public struct CharacterCollisionInfo
    {
        /// <summary>碰到的物体ID。0表示没有。</summary>
        public int HitEntityId;

        /// <summary>接触点(世界坐标)。</summary>
        public Vector3 Point;

        /// <summary>接触法线(从被碰物体指向角色)。</summary>
        public Vector3 Normal;

        /// <summary>穿透深度。</summary>
        public float Penetration;

        /// <summary>碰撞运动学物体时该物体的速度。</summary>
        public Vector3 OtherVelocity;

        /// <summary>接触的物体是否是Dynamic刚体。</summary>
        public bool IsDynamicBody;

        /// <summary>接触的物体是否可踩(比如移动平台)。</summary>
        public bool IsPlatform;

        /// <summary>所属表面类型(地面/墙/天花板)。</summary>
        public SurfaceType SurfaceKind;
    }

    /// <summary>表面类型分类。</summary>
    public enum SurfaceType
    {
        None,
        Ground,
        Wall,
        Ceiling,
        Slope,
        Water,
        Ladder,
    }

    /// <summary>
    ///   角色与世界的交互查询接口。
    ///   实现这个接口来连接角色控制器和实际的物理世界。
    /// </summary>
    public interface ICharacterWorldQuery
    {
        /// <summary>形状扫描。返回是否碰到东西和碰撞信息。</summary>
        bool Sweep(
            Vector3 start, Quaternion orientation,
            CharacterShapeType shapeType,
            float radius, float height, Vector3 halfExtents,
            Vector3 direction, float distance,
            out CharacterCollisionInfo hit);

        /// <summary>重叠查询。返回所有重叠的物体ID。</summary>
        int Overlap(
            Vector3 center, Quaternion orientation,
            CharacterShapeType shapeType,
            float radius, float height, Vector3 halfExtents,
            int[] results, int maxResults);

        /// <summary>射线检测。用于地面检测。</summary>
        bool Raycast(
            Vector3 origin, Vector3 direction, float distance,
            out CharacterCollisionInfo hit);

        /// <summary>对一个刚体施加冲量(用于推物体)。</summary>
        void ApplyImpulseToBody(int bodyId, Vector3 impulse, Vector3 worldPoint);

        /// <summary>获取刚体在某点的速度(用于移动平台)。</summary>
        Vector3 GetBodyVelocityAtPoint(int bodyId, Vector3 worldPoint);

        /// <summary>判断点是否在水里。</summary>
        bool IsPointInWater(Vector3 point, out float waterDepth);

        /// <summary>判断点是否在梯子区域。</summary>
        bool IsPointNearLadder(Vector3 point, out Vector3 ladderNormal, out float ladderHeight);
    }

    /// <summary>
    ///   角色控制器输入。由外部游戏代码填充。
    /// </summary>
    public struct CharacterInput
    {
        /// <summary>移动方向(XZ平面，已归一化)。零向量表示不动。</summary>
        public Vector2 MoveDirection;

        /// <summary>视角朝向(用于把MoveDirection从屏幕空间转到世界空间)。</summary>
        public Quaternion CameraRotation;

        /// <summary>是否按住奔跑键。</summary>
        public bool IsRunning;

        /// <summary>是否按住蹲下键。</summary>
        public bool IsCrouching;

        /// <summary>是否在当前帧按下了跳跃键(边缘触发，按住不算)。</summary>
        public bool JumpPressed;

        /// <summary>是否按住跳跃键(持续按住)。</summary>
        public bool JumpHeld;

        /// <summary>是否按下了互动键(用于爬梯子、抓边缘)。</summary>
        public bool InteractPressed;

        /// <summary>是否按下了滑铲键。</summary>
        public bool SlidePressed;

        /// <summary>是否按住瞄准键(瞄准时移动慢)。</summary>
        public bool IsAiming;

        /// <summary>外部施加的力(比如被爆炸推开)。</summary>
        public Vector3 ExternalForce;

        /// <summary>外部施加的速度(瞬间改变速度，比如被击退)。</summary>
        public Vector3 ExternalVelocity;
    }

    /// <summary>
    ///   角色控制器输出。每帧更新后返回给外部代码。
    /// </summary>
    public struct CharacterOutput
    {
        /// <summary>更新后的世界位置。</summary>
        public Vector3 Position;

        /// <summary>更新后的朝向。</summary>
        public Quaternion Orientation;

        /// <summary>当前水平速度(XZ)。</summary>
        public Vector3 HorizontalVelocity;

        /// <summary>当前垂直速度(Y)。</summary>
        public float VerticalVelocity;

        /// <summary>当前状态。</summary>
        public CharacterState State;

        /// <summary>当前移动模式。</summary>
        public MovementMode Mode;

        /// <summary>当前地面法线。零表示不在地面。</summary>
        public Vector3 GroundNormal;

        /// <summary>当前踩的物体ID。0表示地面。</summary>
        public int GroundEntityId;

        /// <summary>剩余跳跃次数。</summary>
        public int JumpsRemaining;

        /// <summary>是否在本帧起跳。</summary>
        public bool DidJump;

        /// <summary>是否在本帧落地。</summary>
        public bool DidLand;

        /// <summary>本帧所有碰撞信息(用于音效/特效)。</summary>
        public List<CharacterCollisionInfo> Collisions;

        /// <summary>当前实际形状高度(蹲下时变小)。</summary>
        public float CurrentHeight;

        /// <summary>是否在水中。</summary>
        public bool IsInWater;

        /// <summary>当前水深(0=没水, 1=脚, 2=腰, 3=完全浸没)。</summary>
        public float WaterDepth;

        /// <summary>是否在爬梯子。</summary>
        public bool IsOnLadder;

        /// <summary>是否在滑墙。</summary>
        public bool IsWallSliding;

        /// <summary>是否在抓边缘。</summary>
        public bool IsLedgeHanging;

        /// <summary>当前移动速度大小。</summary>
        public float Speed => HorizontalVelocity.Length();
    }

    /// <summary>
    ///   角色控制器。一个完整的Kinematic角色控制器实现。
    ///   继承IDisposable因为可能有非托管资源。
    /// </summary>
    public sealed class CharacterControllerSystem : IDisposable
    {
        // ---- 内部状态 ----
        private CharacterConfig _config;
        private ICharacterWorldQuery? _world;
        private Vector3 _position;
        private Quaternion _orientation = Quaternion.Identity;
        private Vector3 _horizontalVel;
        private float _verticalVel;
        private CharacterState _state = CharacterState.OnGround;
        private MovementMode _mode = MovementMode.Walk;
        private Vector3 _groundNormal = Vector3.UnitY;
        private int _groundEntityId;
        private int _jumpsRemaining;
        private bool _didJump;
        private bool _didLand;
        private Vector3 _wallNormal;
        private bool _wallSliding;
        private bool _ledgeHanging;
        private Vector3 _ledgeHangPoint;
        private bool _onLadder;
        private Vector3 _ladderNormal;
        private bool _inWater;
        private float _waterDepth;
        private float _currentHeight;
        private float _targetHeight;
        private float _crouchBlend;
        private float _slideTimer;
        private float _wallSlideTimer;
        private float _coyoteTime;      // 离开地面后的宽限期，这段时间内还能跳
        private float _jumpBufferTime;  // 按跳跃键后的缓冲时间，落地后立即跳
        private bool _jumpBuffered;
        private readonly List<CharacterCollisionInfo> _collisions = new(16);
        private readonly List<CharacterCollisionInfo> _frameCollisions = new(16);

        private bool _disposed;
        private float _turnVelocity;    // 用于平滑转向

        // 性能统计
        private int _sweepCount;
        private int _overlapCount;
        private float _lastUpdateTime;

        /// <summary>当前配置。</summary>
        public ref CharacterConfig Config => ref _config;

        /// <summary>当前位置(只读)。</summary>
        public Vector3 Position => _position;

        /// <summary>当前朝向(只读)。</summary>
        public Quaternion Orientation => _orientation;

        /// <summary>当前水平速度(只读)。</summary>
        public Vector3 HorizontalVelocity => _horizontalVel;

        /// <summary>当前垂直速度(只读)。</summary>
        public float VerticalVelocity => _verticalVel;

        /// <summary>当前状态。</summary>
        public CharacterState State => _state;

        /// <summary>当前移动模式。</summary>
        public MovementMode Mode => _mode;

        /// <summary>是否在地面。</summary>
        public bool IsOnGround => _state == CharacterState.OnGround || _state == CharacterState.OnSlope;

        /// <summary>剩余跳跃次数。</summary>
        public int JumpsRemaining => _jumpsRemaining;

        /// <summary>当前地面法线。</summary>
        public Vector3 GroundNormal => _groundNormal;

        /// <summary>本帧碰撞列表。</summary>
        public IReadOnlyList<CharacterCollisionInfo> Collisions => _collisions;

        // ========================================================================================
        //  构造函数
        // ========================================================================================

        /// <summary>用默认配置创建角色控制器。</summary>
        public CharacterControllerSystem() : this(CharacterConfig.Default) { }

        /// <summary>用自定义配置创建角色控制器。</summary>
        public CharacterControllerSystem(CharacterConfig config)
        {
            _config = config;
            _currentHeight = GetShapeHeight(ref _config);
            _targetHeight = _currentHeight;
            _jumpsRemaining = config.MaxJumpCount;
        }

        /// <summary>绑定到物理世界。</summary>
        public void BindWorld(ICharacterWorldQuery world)
        {
            _world = world ?? throw new ArgumentNullException(nameof(world));
        }

        /// <summary>设置初始位置。</summary>
        public void SetPosition(Vector3 position)
        {
            _position = position;
        }

        /// <summary>设置初始朝向。</summary>
        public void SetOrientation(Quaternion orientation)
        {
            _orientation = Quaternion.Normalize(orientation);
        }

        /// <summary>强制设置速度(比如被传送后清零)。</summary>
        public void SetVelocity(Vector3 horizontal, float vertical)
        {
            _horizontalVel = horizontal;
            _verticalVel = vertical;
        }

        /// <summary>重置状态(比如角色死亡后重生)。</summary>
        public void Reset()
        {
            _horizontalVel = Vector3.Zero;
            _verticalVel = 0;
            _state = CharacterState.OnGround;
            _mode = MovementMode.Walk;
            _jumpsRemaining = _config.MaxJumpCount;
            _coyoteTime = 0;
            _jumpBufferTime = 0;
            _jumpBuffered = false;
            _wallSliding = false;
            _ledgeHanging = false;
            _onLadder = false;
            _inWater = false;
            _crouchBlend = 0;
            _slideTimer = 0;
        }

        // ========================================================================================
        //  主更新函数
        // ========================================================================================

        /// <summary>
        ///   每帧更新。传入输入和dt，返回输出。
        /// </summary>
        public CharacterOutput Update(CharacterInput input, float dt)
        {
            if (_world == null)
                throw new InvalidOperationException("CharacterController还没绑定World，调BindWorld先。");
            if (dt <= 0f)
                return BuildOutput();

            _sweepCount = 0;
            _overlapCount = 0;
            _frameCollisions.Clear();
            var startTime = System.Diagnostics.Stopwatch.GetTimestamp();

            // 1. 处理外部输入
            ApplyExternalInputs(input, dt);

            // 2. 检测当前环境(水/梯子/地面/墙)
            DetectEnvironment(dt);

            // 3. 决定移动模式
            DetermineMovementMode(input, dt);

            // 4. 处理蹲下(高度过渡)
            UpdateCrouch(dt);

            // 5. 处理水平移动
            UpdateHorizontalMovement(input, dt);

            // 6. 处理垂直移动(重力/跳跃/滑墙)
            UpdateVerticalMovement(input, dt);

            // 7. 把速度转成位置，做碰撞检测
            MoveCharacter(dt);

            // 8. 后处理:更新状态、清理
            PostUpdate(dt);

            // 性能统计
            _lastUpdateTime = (float)(System.Diagnostics.Stopwatch.GetTimestamp() - startTime) /
                              System.Diagnostics.Stopwatch.Frequency * 1000f;

            _didJump = false;
            _didLand = false;
            return BuildOutput();
        }

        // ========================================================================================
        //  输入处理
        // ========================================================================================

        private void ApplyExternalInputs(CharacterInput input, float dt)
        {
            // 外部力:积分到速度
            if (input.ExternalForce != Vector3.Zero)
            {
                _horizontalVel += new Vector3(input.ExternalForce.X, 0, input.ExternalForce.Z) *
                                  (dt / MathF.Max(0.1f, _config.Mass));
                _verticalVel += input.ExternalForce.Y * (dt / MathF.Max(0.1f, _config.Mass));
            }

            // 外部速度:直接叠加
            if (input.ExternalVelocity != Vector3.Zero)
            {
                _horizontalVel += new Vector3(input.ExternalVelocity.X, 0, input.ExternalVelocity.Z);
                _verticalVel += input.ExternalVelocity.Y;
                // 唤醒:可能进入飞行/被击退状态
                if (input.ExternalVelocity.Y > 0.1f)
                {
                    _state = CharacterState.Jumping;
                }
            }
        }

        // ========================================================================================
        //  环境检测
        // ========================================================================================

        private void DetectEnvironment(float dt)
        {
            // 检测水
            bool wasInWater = _inWater;
            _inWater = _world!.IsPointInWater(_position + new Vector3(0, _currentHeight * 0.5f, 0), out _waterDepth);

            // 检测梯子
            bool wasOnLadder = _onLadder;
            _onLadder = _world.IsPointNearLadder(_position + new Vector3(0, _currentHeight * 0.5f, 0),
                out _ladderNormal, out _);

            // 检测地面
            bool wasOnGround = IsOnGround;
            DetectGround();

            // 离地处理
            if (wasOnGround && !IsOnGround && _verticalVel <= 0f)
            {
                // 进入"土狼时间"，这段时间内还能跳
                _coyoteTime = 0.15f;
            }
            if (_coyoteTime > 0) _coyoteTime -= dt;

            // 落地处理
            if (!wasOnGround && IsOnGround)
            {
                _didLand = true;
                _jumpsRemaining = _config.MaxJumpCount;
                _coyoteTime = 0;
                _wallSliding = false;
                _wallSlideTimer = 0;
            }

            // 检测墙(滑墙用)
            if (!IsOnGround && _verticalVel < 0 && _config.CanWallSlide)
            {
                DetectWall();
            }
            else
            {
                _wallSliding = false;
                _wallSlideTimer = 0;
            }

            // 检测边缘(挂墙用)
            // TODO: 这块逻辑挺复杂，暂时简化
            if (!IsOnGround && _verticalVel < 0 && !_wallSliding)
            {
                // DetectLedge();
            }
        }

        private void DetectGround()
        {
            if (_world == null) return;

            Vector3 footPos = _position + new Vector3(0, -_currentHeight * 0.5f, 0);
            Vector3 downDir = -Vector3.UnitY;
            float checkDist = _config.GroundCheckDistance + _config.GroundProbeRadius;

            // 用球扫描检测地面，比射线稳
            bool hit = _world.Sweep(
                footPos, _orientation,
                _config.ShapeType,
                _config.CapsuleRadius, _config.CapsuleHeight, _config.BoxHalfExtents,
                downDir, checkDist,
                out var groundHit);

            _sweepCount++;

            if (hit)
            {
                float slopeAngle = MathF.Acos(Math.Clamp(Vector3.Dot(groundHit.Normal, Vector3.UnitY), -1f, 1f)) *
                                   (180f / MathF.PI);

                if (slopeAngle <= _config.MaxSlopeAngle)
                {
                    _groundNormal = groundHit.Normal;
                    _groundEntityId = groundHit.HitEntityId;
                    if (_state == CharacterState.Falling || _state == CharacterState.Jumping)
                    {
                        // 落地了
                    }
                    _state = slopeAngle > 5f ? CharacterState.OnSlope : CharacterState.OnGround;
                }
                else
                {
                    // 太陡了，当墙处理
                    _groundNormal = Vector3.Zero;
                    _groundEntityId = 0;
                    if (!IsInAirState())
                        _state = CharacterState.Falling;
                }
            }
            else
            {
                _groundNormal = Vector3.Zero;
                _groundEntityId = 0;
                if (!IsInAirState() && !_inWater && !_onLadder)
                    _state = CharacterState.Falling;
            }
        }

        private void DetectWall()
        {
            if (_world == null) return;
            if (_horizontalVel.LengthSquared() < 1f) return;

            Vector3 dir = Vector3.Normalize(new Vector3(_horizontalVel.X, 0, _horizontalVel.Z));
            Vector3 center = _position;

            bool hit = _world.Sweep(
                center, _orientation,
                _config.ShapeType,
                _config.CapsuleRadius, _config.CapsuleHeight, _config.BoxHalfExtents,
                dir, 0.15f,
                out var wallHit);

            _sweepCount++;

            if (hit)
            {
                // 检查是不是垂直墙(法线水平)
                float wallAngle = MathF.Abs(Vector3.Dot(wallHit.Normal, Vector3.UnitY));
                if (wallAngle < 0.3f) // 接近水平的法线 = 垂直的墙
                {
                    _wallNormal = wallHit.Normal;
                    _wallSliding = true;
                    _wallSlideTimer += 1f / 60f; // 简化
                    _frameCollisions.Add(wallHit);
                }
            }
        }

        private bool IsInAirState() =>
            _state == CharacterState.Falling || _state == CharacterState.Jumping;

        // ========================================================================================
        //  移动模式
        // ========================================================================================

        private void DetermineMovementMode(CharacterInput input, float dt)
        {
            MovementMode newMode = _mode;

            // 优先级: 水中 > 梯子 > 滑铲 > 蹲下 > 奔跑 > 走 > 空中
            if (_inWater && _waterDepth > 0.5f)
            {
                newMode = MovementMode.Swimming;
            }
            else if (_onLadder)
            {
                newMode = MovementMode.Climbing;
            }
            else if (!IsOnGround)
            {
                newMode = MovementMode.Air;
            }
            else if (input.SlidePressed && _mode != MovementMode.Sliding && _horizontalVel.Length() > _config.SlideMinSpeed * 2f)
            {
                // 开始滑铲
                newMode = MovementMode.Sliding;
                _slideTimer = 0;
            }
            else if (_mode == MovementMode.Sliding)
            {
                // 滑铲中
                _slideTimer += dt;
                if (_horizontalVel.Length() < _config.SlideMinSpeed || !IsOnGround)
                {
                    // 速度太低或离地，结束滑铲
                    newMode = input.IsCrouching ? MovementMode.Crouch : MovementMode.Walk;
                }
            }
            else if (input.IsCrouching)
            {
                newMode = MovementMode.Crouch;
            }
            else if (input.IsRunning)
            {
                newMode = MovementMode.Run;
            }
            else
            {
                newMode = MovementMode.Walk;
            }

            _mode = newMode;
        }

        // ========================================================================================
        //  蹲下
        // ========================================================================================

        private void UpdateCrouch(float dt)
        {
            float fullHeight = GetShapeHeight(ref _config);
            float crouchHeight = fullHeight - _config.CrouchHeightReduction;

            if (_mode == MovementMode.Crouch || _mode == MovementMode.Sliding)
            {
                _targetHeight = crouchHeight;
                _crouchBlend = Math.Min(1f, _crouchBlend + dt * 10f);
            }
            else
            {
                // 检查头顶有没有东西，能不能站起来
                if (_currentHeight < fullHeight - 0.01f)
                {
                    bool canStand = !_world!.Sweep(
                        _position, _orientation,
                        _config.ShapeType,
                        _config.CapsuleRadius, fullHeight, _config.BoxHalfExtents,
                        Vector3.UnitY, 0.1f,
                        out _);
                    _sweepCount++;

                    if (canStand)
                    {
                        _targetHeight = fullHeight;
                        _crouchBlend = Math.Max(0f, _crouchBlend - dt * 10f);
                    }
                    else
                    {
                        // 头顶有东西，强制蹲下
                        _targetHeight = crouchHeight;
                        _mode = MovementMode.Crouch;
                    }
                }
                else
                {
                    _targetHeight = fullHeight;
                    _crouchBlend = 0;
                }
            }

            // 平滑过渡高度
            _currentHeight = MathHelper.Lerp(_currentHeight, _targetHeight, Math.Min(1f, dt * 12f));
        }

        // ========================================================================================
        //  水平移动
        // ========================================================================================

        private void UpdateHorizontalMovement(CharacterInput input, float dt)
        {
            // 计算目标速度
            Vector3 moveDir = ComputeMoveDirection(input);
            float maxSpeed = GetMaxSpeedForMode();

            Vector3 targetVel = moveDir * maxSpeed;

            // 如果在斜坡上，把速度沿斜面投影
            if (_state == CharacterState.OnSlope && _groundNormal != Vector3.Zero)
            {
                targetVel = ProjectOnPlane(targetVel, _groundNormal);
                // 限制投影后的速度大小
                if (targetVel.LengthSquared() > maxSpeed * maxSpeed)
                    targetVel = Vector3.Normalize(targetVel) * maxSpeed;
            }

            // 加速度/减速度
            float accel;
            if (moveDir != Vector3.Zero)
            {
                accel = IsOnGround ? _config.GroundAcceleration : _config.AirAcceleration;
                if (!IsOnGround && _config.CanAirControl)
                    accel *= _config.AirControlFactor;
                if (_mode == MovementMode.Sliding)
                    accel = 0; // 滑铲中不主动加速
            }
            else
            {
                accel = IsOnGround ? -_config.GroundDeceleration : 0;
                if (_mode == MovementMode.Sliding)
                    accel = -_config.SlideDeceleration;
            }

            // 应用加速度
            if (accel != 0)
            {
                Vector3 velDiff = targetVel - _horizontalVel;
                float maxDelta = Math.Abs(accel) * dt;
                if (velDiff.LengthSquared() > maxDelta * maxDelta)
                    velDiff = Vector3.Normalize(velDiff) * maxDelta;
                _horizontalVel += velDiff;
            }
            else if (_mode == MovementMode.Sliding)
            {
                // 滑铲时根据地面坡度加速
                if (_groundNormal != Vector3.Zero)
                {
                    Vector3 slopeDir = ProjectOnPlane(Vector3.UnitY * -1f, _groundNormal);
                    slopeDir = Vector3.Normalize(slopeDir);
                    _horizontalVel += slopeDir * _config.SlideAcceleration * dt;
                }
            }

            // 摩擦(地面才有)
            if (IsOnGround && _mode != MovementMode.Sliding)
            {
                float frictionForce = _config.Friction * dt;
                if (_horizontalVel.LengthSquared() < frictionForce * frictionForce)
                    _horizontalVel = Vector3.Zero;
                else
                    _horizontalVel -= Vector3.Normalize(_horizontalVel) * frictionForce;
            }

            // 水阻力
            if (_inWater)
            {
                float drag = _config.WaterDrag * dt;
                if (_horizontalVel.LengthSquared() < drag * drag)
                    _horizontalVel = Vector3.Zero;
                else
                    _horizontalVel -= Vector3.Normalize(_horizontalVel) * drag;
            }

            // 限速
            float maxVel = maxSpeed * 1.5f; // 允许稍微超速(被外力推时)
            if (_mode == MovementMode.Sliding) maxVel = maxSpeed * 2.5f; // 滑铲可以更快
            if (_horizontalVel.LengthSquared() > maxVel * maxVel)
                _horizontalVel = Vector3.Normalize(_horizontalVel) * maxVel;

            // 自动转向
            if (_config.AutoFaceMoveDirection && moveDir != Vector3.Zero)
            {
                float targetYaw = MathF.Atan2(moveDir.X, moveDir.Z);
                float currentYaw = ExtractYaw(_orientation);
                float deltaYaw = MathHelper.WrapAngle(targetYaw - currentYaw);
                float maxTurn = _config.TurnSpeed * MathF.PI / 180f * dt;
                if (Math.Abs(deltaYaw) > maxTurn)
                    deltaYaw = Math.Sign(deltaYaw) * maxTurn;
                _orientation = Quaternion.CreateFromYawPitchRoll(deltaYaw, 0, 0) * _orientation;
                _orientation = Quaternion.Normalize(_orientation);
            }
        }

        private Vector3 ComputeMoveDirection(CharacterInput input)
        {
            if (input.MoveDirection == Vector2.Zero)
                return Vector3.Zero;

            // 把屏幕空间的方向转到世界空间
            Vector3 forward = Vector3.Transform(-Vector3.UnitZ, input.CameraRotation);
            forward.Y = 0;
            if (forward.LengthSquared() < 1e-6f)
                forward = -Vector3.UnitZ;
            forward = Vector3.Normalize(forward);

            Vector3 right = Vector3.Transform(Vector3.UnitX, input.CameraRotation);
            right.Y = 0;
            if (right.LengthSquared() < 1e-6f)
                right = Vector3.UnitX;
            right = Vector3.Normalize(right);

            Vector3 worldDir = forward * input.MoveDirection.Y + right * input.MoveDirection.X;
            if (worldDir.LengthSquared() > 1e-6f)
                worldDir = Vector3.Normalize(worldDir);

            return worldDir;
        }

        private float GetMaxSpeedForMode()
        {
            return _mode switch
            {
                MovementMode.Run => _config.MaxRunSpeed,
                MovementMode.Crouch => _config.MaxCrouchSpeed,
                MovementMode.Sliding => _config.MaxRunSpeed * 1.6f, // 滑铲可以更快
                MovementMode.Air => _config.MaxAirSpeed,
                MovementMode.Swimming => _config.SwimSpeed,
                MovementMode.Climbing => _config.ClimbSpeed,
                MovementMode.Walk => _config.MaxWalkSpeed,
                _ => _config.MaxWalkSpeed,
            };
        }

        // ========================================================================================
        //  垂直移动
        // ========================================================================================

        private void UpdateVerticalMovement(CharacterInput input, float dt)
        {
            // 跳跃缓冲:按了跳跃但还在空中，落地后立即跳
            if (input.JumpPressed)
            {
                _jumpBuffered = true;
                _jumpBufferTime = 0.15f;
            }
            if (_jumpBufferTime > 0)
            {
                _jumpBufferTime -= dt;
                if (_jumpBufferTime <= 0)
                    _jumpBuffered = false;
            }

            // 游泳模式:浮力
            if (_mode == MovementMode.Swimming)
            {
                UpdateSwimming(input, dt);
                return;
            }

            // 爬梯子模式
            if (_mode == MovementMode.Climbing)
            {
                UpdateClimbing(input, dt);
                return;
            }

            // 滑墙
            if (_wallSliding && _config.CanWallSlide)
            {
                UpdateWallSlide(input, dt);
                return;
            }

            // 正常重力
            if (!IsOnGround || _verticalVel > 0f)
            {
                float gravity = 9.81f * _config.GravityScale;
                _verticalVel -= gravity * dt;

                // 跳跃键长按减少下落速度(可变跳跃高度)
                if (_verticalVel < 0 && input.JumpHeld && _state == CharacterState.Jumping)
                {
                    _verticalVel += gravity * 0.5f * dt; // 抵消一半重力
                }

                // 限制最大下落速度
                _verticalVel = Math.Max(_verticalVel, -_config.MaxFallSpeed);

                if (_verticalVel > 0)
                    _state = CharacterState.Jumping;
                else if (_verticalVel < 0 && _state != CharacterState.Falling)
                    _state = CharacterState.Falling;
            }
            else
            {
                // 在地面，垂直速度归零(但保留一点下沉让贴地)
                _verticalVel = Math.Min(_verticalVel, 0);
                _verticalVel = Math.Max(_verticalVel, -2f);
            }

            // 尝试跳跃
            TryJump(input);
        }

        private void UpdateSwimming(CharacterInput input, float dt)
        {
            // 浮力: 让角色浮在水面附近
            float buoyancyAccel = 9.81f * _config.Buoyancy;
            _verticalVel += buoyancyAccel * dt;

            // 上下移动(按键控制)
            if (input.JumpHeld)
                _verticalVel += 15f * dt;
            if (input.IsCrouching)
                _verticalVel -= 15f * dt;

            // 水阻力
            _verticalVel *= MathF.Max(0, 1f - _config.WaterDrag * dt);

            // 限制
            _verticalVel = Math.Clamp(_verticalVel, -_config.SwimSpeed, _config.SwimSpeed);

            _state = CharacterState.Swimming;
        }

        private void UpdateClimbing(CharacterInput input, float dt)
        {
            _verticalVel = 0;
            // W上 S下
            if (input.MoveDirection.Y > 0.1f)
                _verticalVel = _config.ClimbSpeed;
            else if (input.MoveDirection.Y < -0.1f)
                _verticalVel = -_config.ClimbSpeed;

            _state = CharacterState.OnGround; // 爬梯子时也当OnGround处理(能跳)
        }

        private void UpdateWallSlide(CharacterInput input, float dt)
        {
            // 限制下落速度
            if (_verticalVel < -_config.WallSlideMaxSpeed)
                _verticalVel = -_config.WallSlideMaxSpeed;

            // 蹬墙跳
            if (input.JumpPressed && _config.CanWallJump)
            {
                // 沿墙法线方向跳
                _horizontalVel = _wallNormal * _config.WallJumpHorizontalSpeed;
                _verticalVel = _config.WallJumpVerticalSpeed;
                _wallSliding = false;
                _state = CharacterState.Jumping;
                _jumpsRemaining = _config.MaxJumpCount - 1;
                _didJump = true;
            }
        }

        private void TryJump(CharacterInput input)
        {
            bool wantJump = input.JumpPressed || (_jumpBuffered && IsOnGround);

            if (!wantJump) return;

            // 土狼时间:刚离地还能跳
            bool canCoyoteJump = !IsOnGround && _coyoteTime > 0 && _jumpsRemaining == _config.MaxJumpCount;
            if (canCoyoteJump)
            {
                _jumpsRemaining = _config.MaxJumpCount;
                _coyoteTime = 0;
            }

            if (_jumpsRemaining > 0 || canCoyoteJump)
            {
                // 算跳跃初速度: v = sqrt(2 * g * h)
                float jumpVel = MathF.Sqrt(2f * 9.81f * _config.GravityScale * _config.JumpHeight);
                _verticalVel = jumpVel;
                _jumpsRemaining--;
                _didJump = true;
                _jumpBuffered = false;
                _state = CharacterState.Jumping;

                // 二段跳时可以重置水平速度方向
                if (_jumpsRemaining < _config.MaxJumpCount - 1 && input.MoveDirection != Vector2.Zero)
                {
                    Vector3 moveDir = ComputeMoveDirection(input);
                    _horizontalVel = moveDir * GetMaxSpeedForMode();
                }
            }
        }

        // ========================================================================================
        //  位置移动+碰撞
        // ========================================================================================

        private void MoveCharacter(float dt)
        {
            // 完整速度
            Vector3 velocity = new(_horizontalVel.X, _verticalVel, _horizontalVel.Z);

            // 分轴移动: 先Y后XZ
            // Y轴
            Vector3 yMove = new(0, velocity.Y, 0) * dt;
            MoveAxis(yMove, Axis.Y);

            // XZ轴
            Vector3 xzMove = new(velocity.X, 0, velocity.Z) * dt;
            MoveAxis(xzMove, Axis.XZ);

            // 移动平台: 跟随
            if (IsOnGround && _groundEntityId != 0)
            {
                Vector3 platformVel = _world!.GetBodyVelocityAtPoint(_groundEntityId,
                    _position + new Vector3(0, -_currentHeight * 0.5f, 0));
                // 把平台水平速度加到角色上
                _position += new Vector3(platformVel.X, 0, platformVel.Z) * dt;
            }
        }

        private enum Axis { Y, XZ }

        private void MoveAxis(Vector3 delta, Axis axis)
        {
            if (delta == Vector3.Zero) return;
            if (_world == null) return;

            float distance = delta.Length();
            if (distance < 1e-5f) return;

            Vector3 dir = delta / distance;

            // 扫描
            bool hit = _world.Sweep(
                _position, _orientation,
                _config.ShapeType,
                _config.CapsuleRadius, _currentHeight, _config.BoxHalfExtents,
                dir, distance,
                out var collision);

            _sweepCount++;

            if (!hit)
            {
                // 没碰到，直接移动
                _position += delta;
                return;
            }

            // 碰到了，先移动到接触点(留一点slop)
            float moveDist = Math.Max(0, collision.Penetration > 0 ? collision.Distance - 0.001f : collision.Distance);
            _position += dir * moveDist;

            _frameCollisions.Add(collision);

            // 处理碰撞响应
            HandleCollision(collision, dir, axis);

            // 推Dynamic刚体
            if (_config.CanPushBodies && collision.IsDynamicBody && collision.HitEntityId != 0)
            {
                Vector3 pushDir = dir;
                float pushMag = _config.PushForce * dt;
                _world.ApplyImpulseToBody(collision.HitEntityId,
                    pushDir * pushMag, collision.Point);
            }

            // 滑墙:如果有剩余位移，沿墙法线滑动
            float remaining = distance - moveDist;
            if (remaining > 0.001f)
            {
                Vector3 slideDir = ProjectOnPlane(dir, collision.Normal);
                if (slideDir.LengthSquared() > 1e-6f)
                {
                    slideDir = Vector3.Normalize(slideDir);
                    Vector3 slideDelta = slideDir * remaining;

                    // 滑动后也要检测碰撞
                    bool hit2 = _world.Sweep(
                        _position, _orientation,
                        _config.ShapeType,
                        _config.CapsuleRadius, _currentHeight, _config.BoxHalfExtents,
                        slideDir, remaining,
                        out var collision2);
                    _sweepCount++;

                    if (hit2)
                    {
                        float slideDist = Math.Max(0, collision2.Distance - 0.001f);
                        _position += slideDir * slideDist;
                        _frameCollisions.Add(collision2);
                    }
                    else
                    {
                        _position += slideDelta;
                    }
                }
            }

            // 自动踏步(只对水平移动有效)
            if (axis == Axis.XZ && _config.StepHeight > 0 && IsOnGround)
            {
                TryStep(dir, distance - moveDist);
            }
        }

        private void HandleCollision(CharacterCollisionInfo col, Vector3 moveDir, Axis axis)
        {
            // 根据法线判断是地面/墙/天花板
            float upDot = Vector3.Dot(col.Normal, Vector3.UnitY);

            if (upDot > 0.7f)
            {
                // 地面
                col.SurfaceKind = SurfaceType.Ground;
                if (axis == Axis.Y && _verticalVel < 0)
                {
                    _verticalVel = 0;
                    if (!IsOnGround)
                    {
                        _didLand = true;
                        _jumpsRemaining = _config.MaxJumpCount;
                    }
                    _state = CharacterState.OnGround;
                    _groundNormal = col.Normal;
                    _groundEntityId = col.HitEntityId;
                }
            }
            else if (upDot < -0.7f)
            {
                // 天花板
                col.SurfaceKind = SurfaceType.Ceiling;
                if (axis == Axis.Y && _verticalVel > 0)
                    _verticalVel = 0;
            }
            else
            {
                // 墙
                col.SurfaceKind = SurfaceType.Wall;
                if (axis == Axis.XZ)
                {
                    // 把速度沿墙投影
                    _horizontalVel = ProjectOnPlane(_horizontalVel, col.Normal);
                    // 限制投影后速度
                    float maxSpeed = GetMaxSpeedForMode();
                    if (_horizontalVel.LengthSquared() > maxSpeed * maxSpeed)
                        _horizontalVel = Vector3.Normalize(_horizontalVel) * maxSpeed;
                }
            }
        }

        private void TryStep(Vector3 dir, float remainingDist)
        {
            if (_world == null) return;
            if (remainingDist <= 0.001f) return;

            // 在当前位置抬高StepHeight，再沿dir扫描
            Vector3 raisedPos = _position + new Vector3(0, _config.StepHeight, 0);

            bool hit = _world.Sweep(
                raisedPos, _orientation,
                _config.ShapeType,
                _config.CapsuleRadius, _currentHeight, _config.BoxHalfExtents,
                dir, remainingDist + 0.05f,
                out var stepHit);
            _sweepCount++;

            if (hit) return;

            // 抬高后能走过去，再向下扫描检测地面
            Vector3 afterStepPos = raisedPos + dir * (remainingDist + 0.05f);
            bool groundHit = _world.Sweep(
                afterStepPos, _orientation,
                _config.ShapeType,
                _config.CapsuleRadius, _currentHeight, _config.BoxHalfExtents,
                -Vector3.UnitY, _config.StepHeight + 0.05f,
                out var groundCol);
            _sweepCount++;

            if (groundHit)
            {
                // 成功踏步
                _position = afterStepPos + new Vector3(0, -(_config.StepHeight + 0.05f - groundCol.Distance), 0);
            }
        }

        // ========================================================================================
        //  后处理
        // ========================================================================================

        private void PostUpdate(float dt)
        {
            // 把本帧碰撞copy到对外列表
            _collisions.Clear();
            _collisions.AddRange(_frameCollisions);

            // 检测是否掉出世界
            if (_position.Y < -1000f)
            {
                // 防御性:掉太低了，停止
                _verticalVel = 0;
                _position.Y = -1000f;
            }
        }

        // ========================================================================================
        //  输出构建
        // ========================================================================================

        private CharacterOutput BuildOutput()
        {
            return new CharacterOutput
            {
                Position = _position,
                Orientation = _orientation,
                HorizontalVelocity = _horizontalVel,
                VerticalVelocity = _verticalVel,
                State = _state,
                Mode = _mode,
                GroundNormal = _groundNormal,
                GroundEntityId = _groundEntityId,
                JumpsRemaining = _jumpsRemaining,
                DidJump = _didJump,
                DidLand = _didLand,
                Collisions = _collisions,
                CurrentHeight = _currentHeight,
                IsInWater = _inWater,
                WaterDepth = _waterDepth,
                IsOnLadder = _onLadder,
                IsWallSliding = _wallSliding,
                IsLedgeHanging = _ledgeHanging,
            };
        }

        // ========================================================================================
        //  工具函数
        // ========================================================================================

        private static float GetShapeHeight(ref CharacterConfig config)
        {
            return config.ShapeType switch
            {
                CharacterShapeType.Capsule => config.CapsuleHeight + config.CapsuleRadius * 2f,
                CharacterShapeType.Box => config.BoxHalfExtents.Y * 2f,
                CharacterShapeType.Cylinder => config.CapsuleHeight,
                _ => config.CapsuleHeight,
            };
        }

        private static Vector3 ProjectOnPlane(Vector3 vector, Vector3 planeNormal)
        {
            return vector - Vector3.Dot(vector, planeNormal) * planeNormal;
        }

        private static float ExtractYaw(Quaternion q)
        {
            // 从四元数提取Yaw角
            Vector3 forward = Vector3.Transform(-Vector3.UnitZ, q);
            return MathF.Atan2(forward.X, forward.Z);
        }

        /// <summary>获取最近一帧的性能统计。</summary>
        public void GetStats(out int sweeps, out int overlaps, out float updateTimeMs)
        {
            sweeps = _sweepCount;
            overlaps = _overlapCount;
            updateTimeMs = _lastUpdateTime;
        }

        /// <summary>手动施加一个冲量(被击退等)。</summary>
        public void ApplyKnockback(Vector3 direction, float strength)
        {
            _horizontalVel += new Vector3(direction.X, 0, direction.Z) * strength;
            _verticalVel += direction.Y * strength;
            if (direction.Y > 0.1f)
                _state = CharacterState.Jumping;
        }

        /// <summary>手动设置位置(传送)。</summary>
        public void Teleport(Vector3 position, Quaternion? orientation = null)
        {
            _position = position;
            if (orientation.HasValue)
                _orientation = Quaternion.Normalize(orientation.Value);
            _horizontalVel = Vector3.Zero;
            _verticalVel = 0;
            _coyoteTime = 0;
            _jumpBuffered = false;
        }

        /// <summary>强制停止移动。</summary>
        public void Stop()
        {
            _horizontalVel = Vector3.Zero;
            _verticalVel = 0;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _world = null;
            _collisions.Clear();
            _frameCollisions.Clear();
        }
    }

    // ============================================================================================
    //  辅助数学工具
    // ============================================================================================

    internal static class MathHelper
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Lerp(float a, float b, float t) => a + (b - a) * t;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector3 Lerp(Vector3 a, Vector3 b, float t) => a + (b - a) * t;

        /// <summary>把角度归一到 [-PI, PI]。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float WrapAngle(float angle)
        {
            while (angle > MathF.PI) angle -= MathF.PI * 2f;
            while (angle < -MathF.PI) angle += MathF.PI * 2f;
            return angle;
        }

        /// <summary>把角度归一到 [0, 2PI)。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float WrapAnglePositive(float angle)
        {
            angle = WrapAngle(angle);
            return angle < 0 ? angle + MathF.PI * 2f : angle;
        }

        /// <summary>平滑插值，帧率无关。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float SmoothDamp(float current, float target, ref float velocity,
            float smoothTime, float dt, float maxSpeed = float.MaxValue)
        {
            smoothTime = MathF.Max(0.0001f, smoothTime);
            float omega = 2f / smoothTime;
            float x = omega * dt;
            float exp = 1f / (1f + x + 0.48f * x * x + 0.235f * x * x * x);
            float change = current - target;
            float originalTo = target;
            float maxChange = maxSpeed * smoothTime;
            change = Math.Clamp(change, -maxChange, maxChange);
            target = current - change;
            float temp = (velocity + omega * change) * dt;
            velocity = (velocity - omega * temp) * exp;
            float output = target + (change + temp) * exp;
            if (originalTo - current > 0f == output > originalTo)
            {
                output = originalTo;
                velocity = (output - originalTo) / dt;
            }
            return output;
        }
    }

    // ============================================================================================
    //  多角色管理器
    //  一个场景里可能有多个角色(比如双人合作游戏)，统一管理。
    // ============================================================================================

    /// <summary>
    ///   角色控制器管理器。管理一个场景里所有的角色控制器。
    /// </summary>
    public sealed class CharacterControllerManager
    {
        private readonly Dictionary<int, CharacterControllerSystem> _controllers = new();
        private readonly List<CharacterControllerSystem> _updateList = new();
        private int _nextId = 1;
        private ICharacterWorldQuery? _world;

        /// <summary>控制器数量。</summary>
        public int Count => _controllers.Count;

        /// <summary>绑定物理世界。</summary>
        public void BindWorld(ICharacterWorldQuery world)
        {
            _world = world ?? throw new ArgumentNullException(nameof(world));
            foreach (var c in _updateList)
                c.BindWorld(world);
        }

        /// <summary>创建一个新控制器。</summary>
        public int Create(CharacterConfig config)
        {
            int id = _nextId++;
            var controller = new CharacterControllerSystem(config);
            if (_world != null)
                controller.BindWorld(_world);
            _controllers[id] = controller;
            _updateList.Add(controller);
            return id;
        }

        /// <summary>创建一个默认配置的控制器。</summary>
        public int Create()
        {
            return Create(CharacterConfig.Default);
        }

        /// <summary>获取控制器。</summary>
        public CharacterControllerSystem? Get(int id)
        {
            return _controllers.TryGetValue(id, out var c) ? c : null;
        }

        /// <summary>移除控制器。</summary>
        public bool Remove(int id)
        {
            if (_controllers.TryGetValue(id, out var c))
            {
                _controllers.Remove(id);
                _updateList.Remove(c);
                c.Dispose();
                return true;
            }
            return false;
        }

        /// <summary>更新所有控制器。</summary>
        public void UpdateAll(Dictionary<int, CharacterInput> inputs, float dt)
        {
            foreach (var kvp in _controllers)
            {
                if (inputs.TryGetValue(kvp.Key, out var input))
                {
                    kvp.Value.Update(input, dt);
                }
            }
        }

        /// <summary>更新所有控制器(用同一个输入，调试用)。</summary>
        public void UpdateAll(CharacterInput input, float dt)
        {
            foreach (var c in _updateList)
                c.Update(input, dt);
        }

        /// <summary>清空所有控制器。</summary>
        public void Clear()
        {
            foreach (var c in _updateList)
                c.Dispose();
            _controllers.Clear();
            _updateList.Clear();
        }
    }

    // ============================================================================================
    //  预设配置
    //  一些常用的角色配置，省得每次都自己调参。
    // ============================================================================================

    /// <summary>一些预设的角色配置。</summary>
    public static class CharacterPresets
    {
        /// <summary>标准FPS角色: 中等身高，速度适中，能跳。</summary>
        public static CharacterConfig FPSCharacter => new()
        {
            ShapeType = CharacterShapeType.Capsule,
            CapsuleRadius = 0.35f,
            CapsuleHeight = 1.7f,
            MaxWalkSpeed = 5.5f,
            MaxRunSpeed = 8.5f,
            MaxCrouchSpeed = 2.5f,
            MaxAirSpeed = 6.0f,
            GroundAcceleration = 70f,
            AirAcceleration = 20f,
            GroundDeceleration = 100f,
            JumpHeight = 1.1f,
            MaxJumpCount = 1,
            GravityScale = 1.0f,
            CanAirControl = true,
            AirControlFactor = 0.4f,
            CanWallSlide = false,
            CanWallJump = false,
            MaxSlopeAngle = 50f,
            StepHeight = 0.3f,
            GroundCheckDistance = 0.12f,
            GroundProbeRadius = 0.18f,
            CanPushBodies = true,
            PushForce = 60f,
            Mass = 75f,
            CrouchHeightReduction = 0.6f,
            SlideMinSpeed = 3f,
            SlideAcceleration = 10f,
            SlideDeceleration = 8f,
            Friction = 0.85f,
            CanClimbLadder = true,
            ClimbSpeed = 3f,
            CanSwim = true,
            SwimSpeed = 3f,
            Buoyancy = 1.0f,
            WaterDrag = 5f,
            MaxFallSpeed = 50f,
            TurnSpeed = 720f,
            AutoFaceMoveDirection = false, // FPS角色一般不自动转向
            CollisionLayer = PhysicsLayers.Character,
            CollisionMask = PhysicsLayers.All,
        };

        /// <summary>第三人称动作游戏角色: 能跑能跳能滑铲。</summary>
        public static CharacterConfig ActionCharacter => new()
        {
            ShapeType = CharacterShapeType.Capsule,
            CapsuleRadius = 0.4f,
            CapsuleHeight = 1.85f,
            MaxWalkSpeed = 4.5f,
            MaxRunSpeed = 10f,
            MaxCrouchSpeed = 2.2f,
            MaxAirSpeed = 9f,
            GroundAcceleration = 80f,
            AirAcceleration = 25f,
            GroundDeceleration = 120f,
            JumpHeight = 1.5f,
            MaxJumpCount = 2,
            GravityScale = 1.2f,
            CanAirControl = true,
            AirControlFactor = 0.6f,
            CanWallSlide = true,
            WallSlideMaxSpeed = 5f,
            CanWallJump = true,
            WallJumpHorizontalSpeed = 7f,
            WallJumpVerticalSpeed = 8f,
            MaxSlopeAngle = 55f,
            StepHeight = 0.4f,
            GroundCheckDistance = 0.15f,
            GroundProbeRadius = 0.22f,
            CanPushBodies = true,
            PushForce = 100f,
            Mass = 70f,
            CrouchHeightReduction = 0.75f,
            SlideMinSpeed = 4f,
            SlideAcceleration = 15f,
            SlideDeceleration = 7f,
            Friction = 0.75f,
            CanClimbLadder = true,
            ClimbSpeed = 3.5f,
            CanSwim = true,
            SwimSpeed = 3.5f,
            Buoyancy = 1.05f,
            WaterDrag = 4f,
            MaxFallSpeed = 60f,
            TurnSpeed = 720f,
            AutoFaceMoveDirection = true,
            CollisionLayer = PhysicsLayers.Character,
            CollisionMask = PhysicsLayers.All,
        };

        /// <summary>平台游戏角色: 跳跃能力强，操作感强。</summary>
        public static CharacterConfig PlatformerCharacter => new()
        {
            ShapeType = CharacterShapeType.Capsule,
            CapsuleRadius = 0.3f,
            CapsuleHeight = 1.4f,
            MaxWalkSpeed = 5f,
            MaxRunSpeed = 7f,
            MaxCrouchSpeed = 2f,
            MaxAirSpeed = 7f,
            GroundAcceleration = 100f,
            AirAcceleration = 40f,
            GroundDeceleration = 150f,
            JumpHeight = 2.0f,
            MaxJumpCount = 2,
            GravityScale = 1.5f,
            CanAirControl = true,
            AirControlFactor = 0.8f,
            CanWallSlide = true,
            WallSlideMaxSpeed = 3f,
            CanWallJump = true,
            WallJumpHorizontalSpeed = 8f,
            WallJumpVerticalSpeed = 9f,
            MaxSlopeAngle = 60f,
            StepHeight = 0.35f,
            GroundCheckDistance = 0.15f,
            GroundProbeRadius = 0.2f,
            CanPushBodies = false,
            PushForce = 0,
            Mass = 50f,
            CrouchHeightReduction = 0.5f,
            SlideMinSpeed = 3f,
            SlideAcceleration = 12f,
            SlideDeceleration = 6f,
            Friction = 0.6f,
            CanClimbLadder = false,
            ClimbSpeed = 0,
            CanSwim = false,
            SwimSpeed = 0,
            Buoyancy = 0,
            WaterDrag = 0,
            MaxFallSpeed = 40f,
            TurnSpeed = 1440f,
            AutoFaceMoveDirection = true,
            CollisionLayer = PhysicsLayers.Character,
            CollisionMask = PhysicsLayers.All,
        };

        /// <summary>载具驾驶员: 站立，速度低，不能跳。</summary>
        public static CharacterConfig DriverCharacter => new()
        {
            ShapeType = CharacterShapeType.Capsule,
            CapsuleRadius = 0.3f,
            CapsuleHeight = 1.5f,
            MaxWalkSpeed = 2f,
            MaxRunSpeed = 3f,
            MaxCrouchSpeed = 1f,
            MaxAirSpeed = 2f,
            GroundAcceleration = 30f,
            AirAcceleration = 10f,
            GroundDeceleration = 50f,
            JumpHeight = 0.3f,
            MaxJumpCount = 1,
            GravityScale = 1.0f,
            CanAirControl = false,
            AirControlFactor = 0,
            CanWallSlide = false,
            CanWallJump = false,
            MaxSlopeAngle = 45f,
            StepHeight = 0.2f,
            GroundCheckDistance = 0.1f,
            GroundProbeRadius = 0.15f,
            CanPushBodies = false,
            PushForce = 0,
            Mass = 80f,
            CrouchHeightReduction = 0.3f,
            SlideMinSpeed = 0,
            SlideAcceleration = 0,
            SlideDeceleration = 0,
            Friction = 0.9f,
            CanClimbLadder = false,
            ClimbSpeed = 0,
            CanSwim = false,
            SwimSpeed = 0,
            Buoyancy = 0,
            WaterDrag = 0,
            MaxFallSpeed = 30f,
            TurnSpeed = 360f,
            AutoFaceMoveDirection = false,
            CollisionLayer = PhysicsLayers.Character,
            CollisionMask = PhysicsLayers.All,
        };

        /// <summary>小怪/动物: 矮小，速度快，跳跃强。</summary>
        public static CharacterConfig SmallCreature => new()
        {
            ShapeType = CharacterShapeType.Capsule,
            CapsuleRadius = 0.2f,
            CapsuleHeight = 0.6f,
            MaxWalkSpeed = 4f,
            MaxRunSpeed = 8f,
            MaxCrouchSpeed = 2f,
            MaxAirSpeed = 6f,
            GroundAcceleration = 120f,
            AirAcceleration = 50f,
            GroundDeceleration = 100f,
            JumpHeight = 1.5f,
            MaxJumpCount = 2,
            GravityScale = 1.0f,
            CanAirControl = true,
            AirControlFactor = 0.9f,
            CanWallSlide = false,
            CanWallJump = false,
            MaxSlopeAngle = 70f,
            StepHeight = 0.25f,
            GroundCheckDistance = 0.1f,
            GroundProbeRadius = 0.12f,
            CanPushBodies = false,
            PushForce = 0,
            Mass = 15f,
            CrouchHeightReduction = 0.3f,
            SlideMinSpeed = 0,
            SlideAcceleration = 0,
            SlideDeceleration = 0,
            Friction = 0.7f,
            CanClimbLadder = true,
            ClimbSpeed = 4f,
            CanSwim = true,
            SwimSpeed = 4f,
            Buoyancy = 1.1f,
            WaterDrag = 3f,
            MaxFallSpeed = 25f,
            TurnSpeed = 1440f,
            AutoFaceMoveDirection = true,
            CollisionLayer = PhysicsLayers.Character,
            CollisionMask = PhysicsLayers.All,
        };

        /// <summary>大型Boss: 高大，移动慢，推力强。</summary>
        public static CharacterConfig LargeBoss => new()
        {
            ShapeType = CharacterShapeType.Capsule,
            CapsuleRadius = 0.8f,
            CapsuleHeight = 3.5f,
            MaxWalkSpeed = 3f,
            MaxRunSpeed = 6f,
            MaxCrouchSpeed = 0,
            MaxAirSpeed = 3f,
            GroundAcceleration = 40f,
            AirAcceleration = 15f,
            GroundDeceleration = 60f,
            JumpHeight = 0.8f,
            MaxJumpCount = 1,
            GravityScale = 1.5f,
            CanAirControl = false,
            AirControlFactor = 0.1f,
            CanWallSlide = false,
            CanWallJump = false,
            MaxSlopeAngle = 40f,
            StepHeight = 0.5f,
            GroundCheckDistance = 0.2f,
            GroundProbeRadius = 0.4f,
            CanPushBodies = true,
            PushForce = 500f,
            Mass = 500f,
            CrouchHeightReduction = 0,
            SlideMinSpeed = 0,
            SlideAcceleration = 0,
            SlideDeceleration = 0,
            Friction = 0.95f,
            CanClimbLadder = false,
            ClimbSpeed = 0,
            CanSwim = false,
            SwimSpeed = 0,
            Buoyancy = 0,
            WaterDrag = 0,
            MaxFallSpeed = 30f,
            TurnSpeed = 180f,
            AutoFaceMoveDirection = true,
            CollisionLayer = PhysicsLayers.Enemy,
            CollisionMask = PhysicsLayers.All,
        };
    }

    // ============================================================================================
    //  扩展:连招/状态机集成
    //  角色控制器本身不管动画/连招，但可以提供状态给外部用。
    // ============================================================================================

    /// <summary>
    ///   角色事件类型。外部可以监听这些事件来播放音效/特效/动画。
    /// </summary>
    public enum CharacterEventType
    {
        Land,
        Jump,
        DoubleJump,
        WallJump,
        WallSlideStart,
        WallSlideEnd,
        SlideStart,
        SlideEnd,
        CrouchStart,
        CrouchEnd,
        EnterWater,
        ExitWater,
        LadderGrab,
        LadderRelease,
        Step,
        HitWall,
        Fall,
    }

    /// <summary>角色事件。</summary>
    public struct CharacterEvent
    {
        public CharacterEventType Type;
        public Vector3 Position;
        public Vector3 Normal;
        public float Speed;
        public int EntityId;
    }

    /// <summary>
    ///   事件队列。CharacterControllerSystem每帧产生事件，外面读取。
    /// </summary>
    public sealed class CharacterEventQueue
    {
        private readonly Queue<CharacterEvent> _events = new();
        private readonly int _maxEvents;

        public CharacterEventQueue(int maxEvents = 64)
        {
            _maxEvents = maxEvents;
        }

        public void Enqueue(CharacterEvent evt)
        {
            _events.Enqueue(evt);
            while (_events.Count > _maxEvents)
                _events.Dequeue();
        }

        public bool TryDequeue(out CharacterEvent evt)
        {
            return _events.TryDequeue(out evt);
        }

        public void Clear() => _events.Clear();

        public int Count => _events.Count;
    }

    // ============================================================================================
    //  调试用API
    // ============================================================================================

    /// <summary>
    ///   角色调试信息。给PhysicsDebugger用。
    /// </summary>
    public struct CharacterDebugInfo
    {
        public Vector3 Position;
        public Quaternion Orientation;
        public Vector3 Velocity;
        public Vector3 GroundNormal;
        public CharacterState State;
        public MovementMode Mode;
        public float CurrentHeight;
        public float CurrentRadius;
        public bool IsOnGround;
        public bool IsWallSliding;
        public bool IsInWater;
        public int JumpsRemaining;
        public Vector3 WallNormal;
        public Vector3 MoveDirection;
    }

    /// <summary>
    ///   调试信息扩展方法。
    /// </summary>
    public static class CharacterDebugExtensions
    {
        public static CharacterDebugInfo GetDebugInfo(this CharacterControllerSystem controller)
        {
            return new CharacterDebugInfo
            {
                Position = controller.Position,
                Orientation = controller.Orientation,
                Velocity = new Vector3(
                    controller.HorizontalVelocity.X,
                    controller.VerticalVelocity,
                    controller.HorizontalVelocity.Z),
                GroundNormal = controller.GroundNormal,
                State = controller.State,
                Mode = controller.Mode,
                CurrentHeight = 0, // 内部字段，外部拿不到，这里给0
                CurrentRadius = 0,
                IsOnGround = controller.IsOnGround,
                IsWallSliding = false,
                IsInWater = false,
                JumpsRemaining = controller.JumpsRemaining,
                WallNormal = Vector3.Zero,
                MoveDirection = controller.HorizontalVelocity,
            };
        }
    }

    // ============================================================================================
    //  力场/效果区域集成
    //  角色进入特定区域时受到额外影响(减速带、弹跳板、传送带等)。
    // ============================================================================================

    /// <summary>区域效果类型。</summary>
    public enum ZoneEffectType
    {
        SpeedBoost,
        SlowZone,
        BouncePad,
        Conveyor,
        GravityWell,
        NoJumpZone,
        NoGravityZone,
        DamageZone,
    }

    /// <summary>区域效果。</summary>
    public struct ZoneEffect
    {
        public ZoneEffectType Type;
        public float Strength;
        public Vector3 Direction;
        public float Radius;
        public Vector3 Center;
    }

    /// <summary>区域效果管理器。</summary>
    public sealed class CharacterZoneManager
    {
        private readonly List<ZoneEffect> _zones = new();
        private readonly Dictionary<int, List<ZoneEffect>> _activeZones = new();

        public int ZoneCount => _zones.Count;

        public void AddZone(ZoneEffect zone)
        {
            _zones.Add(zone);
        }

        public void RemoveZone(ZoneEffect zone)
        {
            _zones.Remove(zone);
        }

        public void Clear()
        {
            _zones.Clear();
            _activeZones.Clear();
        }

        /// <summary>查询某个角色当前处于哪些区域内。</summary>
        public List<ZoneEffect> GetActiveZones(int characterId, Vector3 position)
        {
            var result = new List<ZoneEffect>();
            foreach (var zone in _zones)
            {
                float distSq = Vector3.DistanceSquared(position, zone.Center);
                if (distSq <= zone.Radius * zone.Radius)
                    result.Add(zone);
            }
            _activeZones[characterId] = result;
            return result;
        }

        /// <summary>把区域效果应用到角色输入上。</summary>
        public void ApplyZones(ref CharacterInput input, Vector3 position, ref Vector3 externalForce)
        {
            // 这个简化版直接遍历，正经实现要缓存
            foreach (var zone in _zones)
            {
                float distSq = Vector3.DistanceSquared(position, zone.Center);
                if (distSq > zone.Radius * zone.Radius) continue;

                switch (zone.Type)
                {
                    case ZoneEffectType.SpeedBoost:
                        // 加速度方向力
                        externalForce += zone.Direction * zone.Strength;
                        break;
                    case ZoneEffectType.SlowZone:
                        // 减速:反向施加力
                        externalForce -= new Vector3(input.MoveDirection.X, 0, input.MoveDirection.Y) *
                                         zone.Strength;
                        break;
                    case ZoneEffectType.BouncePad:
                        // 弹跳:瞬间速度
                        input = input with { ExternalVelocity = input.ExternalVelocity + zone.Direction * zone.Strength };
                        break;
                    case ZoneEffectType.Conveyor:
                        // 传送带:持续推力
                        externalForce += zone.Direction * zone.Strength;
                        break;
                    case ZoneEffectType.GravityWell:
                        // 重力井:向中心拉
                        Vector3 toCenter = zone.Center - position;
                        float dist = MathF.Sqrt(distSq);
                        if (dist > 0.1f)
                            externalForce += (toCenter / dist) * zone.Strength;
                        break;
                    case ZoneEffectType.NoJumpZone:
                        input.JumpPressed = false;
                        input.JumpHeld = false;
                        break;
                    case ZoneEffectType.NoGravityZone:
                        // 减少重力:这里简单实现，外部要处理
                        externalForce += Vector3.UnitY * 9.81f * zone.Strength;
                        break;
                }
            }
        }
    }

    // ============================================================================================
    //  AI导航集成
    //  给AI用的简化接口，能直接设目标点。
    // ============================================================================================

    /// <summary>
    ///   AI角色控制器。封装了寻路+输入生成。
    /// </summary>
    public sealed class AICharacterController
    {
        private readonly CharacterControllerSystem _controller;
        private Vector3 _target;
        private float _stopDistance = 0.5f;
        private bool _hasTarget;
        private bool _isRunning;
        private float _jumpCheckDistance = 0.5f;

        public CharacterControllerSystem Controller => _controller;
        public Vector3 Target => _target;
        public bool HasTarget => _hasTarget;
        public float StopDistance
        {
            get => _stopDistance;
            set => _stopDistance = MathF.Max(0.1f, value);
        }

        public bool IsRunning
        {
            get => _isRunning;
            set => _isRunning = value;
        }

        public AICharacterController(CharacterControllerSystem controller)
        {
            _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        }

        /// <summary>设置目标点。</summary>
        public void SetTarget(Vector3 target, bool run = false)
        {
            _target = target;
            _hasTarget = true;
            _isRunning = run;
        }

        /// <summary>清除目标。</summary>
        public void ClearTarget()
        {
            _hasTarget = false;
        }

        /// <summary>更新AI，生成输入并交给控制器。</summary>
        public CharacterOutput Update(Quaternion cameraRotation, float dt)
        {
            CharacterInput input = new()
            {
                CameraRotation = cameraRotation,
                IsRunning = _isRunning,
            };

            if (_hasTarget)
            {
                Vector3 toTarget = _target - _controller.Position;
                toTarget.Y = 0;
                float dist = toTarget.Length();

                if (dist > _stopDistance)
                {
                    toTarget = toTarget / dist;
                    // 把世界方向转成屏幕空间方向
                    Vector3 forward = Vector3.Transform(-Vector3.UnitZ, cameraRotation);
                    forward.Y = 0;
                    forward = Vector3.Normalize(forward);
                    Vector3 right = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, forward));

                    input.MoveDirection = new Vector2(
                        Vector3.Dot(toTarget, right),
                        Vector3.Dot(toTarget, forward));

                    // 检测前方障碍，尝试跳过
                    // TODO: 真正的AI寻路要调NavigationSystem
                    if (dist < 5f && MathF.Abs(toTarget.Y) < 0.3f)
                    {
                        // 简化:检测前方是否有高障碍
                        // 实际项目里用NavMesh
                    }
                }
                else
                {
                    // 到了
                    _hasTarget = false;
                }
            }

            return _controller.Update(input, dt);
        }
    }

    // ============================================================================================
    //  网络同步辅助
    //  网络游戏里，客户端预测+服务器回滚需要的工具。
    // ============================================================================================

    /// <summary>
    ///   角色状态快照。用于网络同步。
    /// </summary>
    [Serializable]
    public struct CharacterStateSnapshot
    {
        public Vector3 Position;
        public Quaternion Orientation;
        public Vector3 HorizontalVelocity;
        public float VerticalVelocity;
        public CharacterState State;
        public MovementMode Mode;
        public int JumpsRemaining;
        public float CurrentHeight;
        public int SequenceNumber;  // 帧序号，用于回滚
        public float Timestamp;
    }

    /// <summary>
    ///   角色状态历史记录。用于客户端预测和回滚。
    /// </summary>
    public sealed class CharacterStateHistory
    {
        private readonly Queue<CharacterStateSnapshot> _history = new();
        private readonly int _maxSize;

        public CharacterStateHistory(int maxSize = 120)
        {
            _maxSize = maxSize;
        }

        public void Record(CharacterControllerSystem controller, int sequence, float timestamp)
        {
            var snapshot = new CharacterStateSnapshot
            {
                Position = controller.Position,
                Orientation = controller.Orientation,
                HorizontalVelocity = controller.HorizontalVelocity,
                VerticalVelocity = controller.VerticalVelocity,
                State = controller.State,
                Mode = controller.Mode,
                JumpsRemaining = controller.JumpsRemaining,
                SequenceNumber = sequence,
                Timestamp = timestamp,
            };
            _history.Enqueue(snapshot);
            while (_history.Count > _maxSize)
                _history.Dequeue();
        }

        /// <summary>获取指定帧序号的状态快照。</summary>
        public bool TryGetSnapshot(int sequence, out CharacterStateSnapshot snapshot)
        {
            foreach (var s in _history)
            {
                if (s.SequenceNumber == sequence)
                {
                    snapshot = s;
                    return true;
                }
            }
            snapshot = default;
            return false;
        }

        /// <summary>获取最近的状态。</summary>
        public CharacterStateSnapshot Latest =>
            _history.Count > 0 ? _history.ToArray()[_history.Count - 1] : default;

        public int Count => _history.Count;

        public void Clear() => _history.Clear();
    }

    // ============================================================================================
    //  录制/回放
    //  用于debug和录像功能。
    // ============================================================================================

    /// <summary>角色输入录制帧。</summary>
    [Serializable]
    public struct CharacterInputFrame
    {
        public float Timestamp;
        public CharacterInput Input;
        public Vector3 Position;
        public Quaternion Orientation;
    }

    /// <summary>角色输入录制器。</summary>
    public sealed class CharacterInputRecorder
    {
        private readonly List<CharacterInputFrame> _frames = new();
        private bool _recording;
        private float _recordTime;

        public bool IsRecording => _recording;
        public int FrameCount => _frames.Count;

        public void StartRecording()
        {
            _frames.Clear();
            _recording = true;
            _recordTime = 0;
        }

        public void StopRecording()
        {
            _recording = false;
        }

        public void RecordFrame(CharacterInput input, Vector3 pos, Quaternion ori, float dt)
        {
            if (!_recording) return;
            _recordTime += dt;
            _frames.Add(new CharacterInputFrame
            {
                Timestamp = _recordTime,
                Input = input,
                Position = pos,
                Orientation = ori,
            });
        }

        public IReadOnlyList<CharacterInputFrame> Frames => _frames;

        public void SaveToFile(string path)
        {
            // 简化:实际用json/binary序列化
            // TODO: 实现真正的序列化
        }

        public void LoadFromFile(string path)
        {
            // TODO
        }
    }

    // ============================================================================================
    //  摄像机辅助
    //  第三人称相机跟随角色。
    // ============================================================================================

    /// <summary>第三人称跟随相机。</summary>
    public sealed class ThirdPersonFollowCamera
    {
        private Vector3 _cameraPos;
        private Vector3 _lookAt;
        private float _yaw;
        private float _pitch = -0.2f;
        private float _distance = 4f;
        private float _minPitch = -1.2f;
        private float _maxPitch = 0.6f;
        private float _minDistance = 2f;
        private float _maxDistance = 8f;
        private float _smoothing = 8f;

        public Vector3 Position => _cameraPos;
        public Vector3 LookAt => _lookAt;
        public float Yaw
        {
            get => _yaw;
            set => _yaw = value;
        }
        public float Pitch
        {
            get => _pitch;
            set => _pitch = Math.Clamp(value, _minPitch, _maxPitch);
        }
        public float Distance
        {
            get => _distance;
            set => _distance = Math.Clamp(value, _minDistance, _maxDistance);
        }

        public ThirdPersonFollowCamera(Vector3 initialPos)
        {
            _cameraPos = initialPos;
            _lookAt = initialPos;
        }

        public void Update(Vector3 targetPos, float dt, Vector2 lookDelta)
        {
            _yaw -= lookDelta.X * dt * 0.1f;
            _pitch += lookDelta.Y * dt * 0.1f;
            _pitch = Math.Clamp(_pitch, _minPitch, _maxPitch);

            // 球坐标算相机位置
            float cosPitch = MathF.Cos(_pitch);
            Vector3 offset = new(
                MathF.Sin(_yaw) * cosPitch * _distance,
                MathF.Sin(-_pitch) * _distance,
                MathF.Cos(_yaw) * cosPitch * _distance);

            Vector3 desiredPos = targetPos + offset + new Vector3(0, 1.5f, 0);
            _cameraPos = MathHelper.Lerp(_cameraPos, desiredPos, Math.Min(1f, _smoothing * dt));
            _lookAt = MathHelper.Lerp(_lookAt, targetPos + new Vector3(0, 1.2f, 0), Math.Min(1f, _smoothing * dt));
        }

        public Quaternion GetCameraRotation()
        {
            return Quaternion.CreateFromYawPitchRoll(_yaw, _pitch, 0);
        }
    }
}
