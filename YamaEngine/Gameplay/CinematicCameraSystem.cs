using System;
using System.Collections.Generic;
using System.Numerics;
using YamaEngine.ECS;
using YamaEngine.Physics;

namespace YamaEngine.Gameplay
{
    // ============================================================================================
    //  CinematicCameraSystem.cs
    //
    //  yama引擎 —— 电影摄像机系统
    //
    //  本文件实现一套面向过场动画与高品质游戏视角的摄像机解决方案，对齐 UE 的
    //  CameraActor / SpringArm / CameraModifier / CameraShake / CameraRig 设计，同时
    //  与引擎自身的 GameplayFramework（Actor / ActorComponent）、ECS、PhysicsWorld
    //  射线查询对接。
    //
    //  主要组成：
    //    1. CameraComponent          —— 基础相机组件（FOV / 宽高比 / 近远裁剪 / 视图投影矩阵）
    //    2. SpringArmComponent       —— 弹簧臂（延迟跟随 / 碰撞回缩 / 插座偏移）
    //    3. CinematicCameraSettings  —— 电影相机参数（焦距 / 光圈 / 快门 / ISO / 景深）
    //    4. CinematicCamera          —— 电影摄像机（继承 CameraComponent，附加电影参数）
    //    5. CameraShake 系列         —— 摄像机抖动（噪声驱动 / 曲线驱动 / 衰减）
    //    6. CameraModifier           —— 摄像机修改器基类，可叠加形成修改器栈
    //    7. CameraBlending           —— 多摄像机混合（Cut / Ease / Blend）
    //    8. CameraRig 系列           —— 摄像机摇臂 / 轨道 / 起重机
    //    9. 视角控制                 —— 第一人称 / 第三人称 / 俯视 / 侧视
    //   10. 摄像机碰撞检测           —— 射线 / 穿透回缩
    //   11. Look-At 约束             —— 注视目标
    //
    //  注意：本命名空间下不存在与 ECS.CameraComponent 的命名冲突（后者位于 YamaEngine.ECS）。
    // ============================================================================================

    /// <summary>摄像机视角模式。决定基础跟随几何与默认参数。</summary>
    public enum CameraViewMode
    {
        /// <summary>第一人称：相机贴在角色眼部，随角色旋转。</summary>
        FirstPerson,
        /// <summary>第三人称：相机位于角色后方/上方，由弹簧臂支撑。</summary>
        ThirdPerson,
        /// <summary>俯视：相机从正上方俯瞰目标。</summary>
        TopDown,
        /// <summary>侧视：相机从侧面观察目标（横版卷轴风格）。</summary>
        SideView,
        /// <summary>自由：完全脱离目标，由输入直接控制。</summary>
        Free
    }

    /// <summary>摄像机混合方式，控制两台相机之间切换时的过渡。</summary>
    public enum CameraBlendMode
    {
        /// <summary>硬切：无过渡，立即切换。</summary>
        Cut,
        /// <summary>缓动：使用内置缓动函数（线性 / 二次 / 三次）过渡。</summary>
        Ease,
        /// <summary>混合：在一段时间内线性插值位置与朝向。</summary>
        Blend
    }

    /// <summary>缓动函数类型，用于 CameraBlendMode.Ease。</summary>
    public enum EaseType
    {
        Linear,
        EaseInQuad,
        EaseOutQuad,
        EaseInOutQuad,
        EaseInOutCubic
    }

    /// <summary>
    ///   基础相机组件。挂在 Actor 上，提供视图矩阵、投影矩阵与可调参数。
    ///   它是 SpringArm 与 CinematicCamera 的共同基类。
    /// </summary>
    public class CameraComponent : ActorComponent
    {
        /// <summary>垂直视场角（度）。</summary>
        public float FieldOfView { get; set; } = 60f;

        /// <summary>宽高比（宽 / 高）。</summary>
        public float AspectRatio { get; set; } = 16f / 9f;

        /// <summary>近裁剪面距离（米）。</summary>
        public float NearClip { get; set; } = 0.1f;

        /// <summary>远裁剪面距离（米）。</summary>
        public float FarClip { get; set; } = 1000f;

        /// <summary>是否使用正交投影（俯视 / 侧视常用）。</summary>
        public bool IsOrthographic { get; set; }

        /// <summary>正交投影半高（世界单位）。</summary>
        public float OrthographicSize { get; set; } = 10f;

        /// <summary>是否作为主相机输出到屏幕。</summary>
        public bool IsMain { get; set; }

        /// <summary>相机的局部位置偏移（相对于所属 Actor）。</summary>
        public Vector3 LocalOffset { get; set; } = Vector3.Zero;

        /// <summary>写入最终世界变换（基类空实现，CinematicCamera 覆盖以合并抖动）。</summary>
        public virtual void SetWorldTransform(Vector3 position, Quaternion rotation)
        {
            // 默认无操作；子类（如 CinematicCamera）保存基座位置/旋转。
        }

        /// <summary>当前视角模式。</summary>
        public CameraViewMode ViewMode { get; set; } = CameraViewMode.ThirdPerson;

        /// <summary>跟随目标 Actor（通常是被观察的 Pawn）。</summary>
        public Actor? Target { get; set; }

        // —— 运行时计算得到的世界变换与矩阵 ——
        private Vector3 _worldPosition = Vector3.Zero;
        private Quaternion _worldRotation = Quaternion.Identity;

        /// <summary>相机在世界空间中的位置。</summary>
        public Vector3 WorldPosition => _worldPosition;

        /// <summary>相机在世界空间中的朝向。</summary>
        public Quaternion WorldRotation => _worldRotation;

        /// <summary>相机前向（看向的方向）。</summary>
        public Vector3 Forward => Vector3.Transform(-Vector3.UnitZ, _worldRotation);

        /// <summary>相机右向。</summary>
        public Vector3 Right => Vector3.Transform(Vector3.UnitX, _worldRotation);

        /// <summary>相机上向。</summary>
        public Vector3 Up => Vector3.Transform(Vector3.UnitY, _worldRotation);

        /// <summary>计算视图矩阵（世界 → 观察）。</summary>
        public Matrix4x4 ViewMatrix
        {
            get
            {
                var eye = _worldPosition;
                var target = eye + Forward;
                return Matrix4x4.CreateLookAt(eye, target, Up);
            }
        }

        /// <summary>计算投影矩阵（观察 → 裁剪）。</summary>
        public Matrix4x4 ProjectionMatrix
        {
            get
            {
                float fovRad = FieldOfView * MathF.PI / 180f;
                return IsOrthographic
                    ? Matrix4x4.CreateOrthographic(OrthographicSize * AspectRatio, OrthographicSize, NearClip, FarClip)
                    : Matrix4x4.CreatePerspectiveFieldOfView(fovRad, AspectRatio, NearClip, FarClip);
            }
        }

        /// <summary>视图 × 投影组合矩阵。</summary>
        public Matrix4x4 ViewProjection => ViewMatrix * ProjectionMatrix;

        /// <summary>
        ///   根据所属 Actor / 跟随目标更新世界变换。子类可重写以引入弹簧臂、抖动等。
        /// </summary>
        protected virtual void UpdateTransform(float deltaTime)
        {
            Actor? anchor = Target ?? Owner;
            if (anchor == null)
            {
                _worldPosition = LocalOffset;
                _worldRotation = Quaternion.Identity;
                return;
            }

            // 不同视角模式决定基础几何
            switch (ViewMode)
            {
                case CameraViewMode.FirstPerson:
                    _worldPosition = anchor.Position + Vector3.Transform(LocalOffset, anchor.Rotation);
                    _worldRotation = anchor.Rotation;
                    break;

                case CameraViewMode.ThirdPerson:
                    // 基础位置在目标后方上方，由弹簧臂组件进一步处理
                    var back = Vector3.Transform(new Vector3(0, 1.5f, 6f), anchor.Rotation);
                    _worldPosition = anchor.Position + back + LocalOffset;
                    _worldRotation = LookRotation(anchor.Position - _worldPosition, Vector3.UnitY);
                    break;

                case CameraViewMode.TopDown:
                    _worldPosition = anchor.Position + new Vector3(0, 20f, 0.01f) + LocalOffset;
                    _worldRotation = LookRotation(Vector3.UnitY * -1f, Vector3.UnitZ);
                    break;

                case CameraViewMode.SideView:
                    _worldPosition = anchor.Position + new Vector3(10f, 2f, 0) + LocalOffset;
                    _worldRotation = LookRotation(anchor.Position - _worldPosition, Vector3.UnitY);
                    break;

                case CameraViewMode.Free:
                    _worldPosition = anchor.Position + LocalOffset;
                    _worldRotation = anchor.Rotation;
                    break;
            }
        }

        protected override void OnTick(float deltaTime) => UpdateTransform(deltaTime);

        /// <summary>用前向与上向构造朝向四元数。</summary>
        protected static Quaternion LookRotation(Vector3 forward, Vector3 up)
        {
            if (forward.LengthSquared() < 1e-8f) return Quaternion.Identity;
            forward = Vector3.Normalize(forward);
            var zaxis = -forward;
            var xaxis = Vector3.Normalize(Vector3.Cross(up, zaxis));
            var yaxis = Vector3.Cross(zaxis, xaxis);
            var m = new Matrix4x4(
                xaxis.X, yaxis.X, zaxis.X, 0,
                xaxis.Y, yaxis.Y, zaxis.Y, 0,
                xaxis.Z, yaxis.Z, zaxis.Z, 0,
                0, 0, 0, 1);
            Matrix4x4.Invert(m, out var inv);
            return Quaternion.CreateFromRotationMatrix(inv);
        }
    }

    // ============================================================================================
    //  SpringArmComponent —— 弹簧臂
    // ============================================================================================

    /// <summary>
    ///   弹簧臂组件。负责在相机与目标之间插入一段"臂"，提供：
    ///     - 延迟跟随（位置 / 朝向的弹簧阻尼平滑）
    ///     - 碰撞回缩（探测到障碍物时把相机拉到安全距离）
    ///     - 插座偏移（在臂端附加偏移，用于越肩视角等）
    /// </summary>
    public class SpringArmComponent : ActorComponent
    {
        /// <summary>弹簧臂长度（米）。</summary>
        public float TargetArmLength { get; set; } = 5f;

        /// <summary>插座偏移（在臂端坐标系下）。</summary>
        public Vector3 SocketOffset { get; set; } = Vector3.Zero;

        /// <summary>位置延迟系数（0=瞬时跟随，1=几乎不跟随）。</summary>
        public float PositionLag { get; set; } = 0.15f;

        /// <summary>朝向延迟系数。</summary>
        public float RotationLag { get; set; } = 0.1f;

        /// <summary>是否启用碰撞回缩。</summary>
        public bool DoCollisionTest { get; set; } = true;

        /// <summary>碰撞探测用的球体半径。</summary>
        public float ProbeRadius { get; set; } = 0.3f;

        /// <summary>碰撞回缩时保留的最小臂长。</summary>
        public float MinArmLength { get; set; } = 0.5f;

        /// <summary>关联的物理世界（用于碰撞射线/球体探测）。</summary>
        public PhysicsWorld? PhysicsWorld { get; set; }

        // —— 运行时平滑量 ——
        private Vector3 _smoothedPosition;
        private Quaternion _smoothedRotation = Quaternion.Identity;
        private bool _initialized;

        /// <summary>当前臂端世界位置（含碰撞回缩后的实际位置）。</summary>
        public Vector3 SocketWorldPosition { get; private set; }

        /// <summary>当前臂端世界朝向。</summary>
        public Quaternion SocketWorldRotation { get; private set; } = Quaternion.Identity;

        /// <summary>当前实际臂长（可能因碰撞短于 TargetArmLength）。</summary>
        public float CurrentArmLength { get; private set; }

        protected override void OnTick(float deltaTime)
        {
            if (Owner == null) return;

            // 弹簧臂以所属 Actor 的位置/朝向作为锚点
            Vector3 anchorPos = Owner.Position;
            Quaternion anchorRot = Owner.Rotation;

            // 计算理想臂端（无碰撞时）
            var armDir = Vector3.Transform(-Vector3.UnitZ, anchorRot); // 朝向 Actor 后方
            Vector3 idealSocket = anchorPos + armDir * TargetArmLength + SocketOffset;

            // 初始化平滑量，避免开局时大幅度插值
            if (!_initialized)
            {
                _smoothedPosition = anchorPos;
                _smoothedRotation = anchorRot;
                _initialized = true;
            }

            // 位置弹簧延迟：指数平滑
            float posAlpha = 1f - MathF.Pow(PositionLag, deltaTime * 60f);
            _smoothedPosition = Vector3.Lerp(_smoothedPosition, anchorPos, Math.Clamp(posAlpha, 0f, 1f));

            // 朝向弹簧延迟：球面插值
            float rotAlpha = 1f - MathF.Pow(RotationLag, deltaTime * 60f);
            _smoothedRotation = Quaternion.Slerp(_smoothedRotation, anchorRot, Math.Clamp(rotAlpha, 0f, 1f));

            // 重新计算臂端（基于平滑后的锚点）
            var smoothArmDir = Vector3.Transform(-Vector3.UnitZ, _smoothedRotation);
            Vector3 smoothSocket = _smoothedPosition + smoothArmDir * TargetArmLength + SocketOffset;

            // 碰撞回缩：从锚点向臂端做球体扫描，命中则缩短臂长
            float armLength = TargetArmLength;
            if (DoCollisionTest && PhysicsWorld != null)
            {
                float probeDist = TargetArmLength;
                var probeDir = smoothArmDir;
                // 用射线代替球扫（Jolt 球扫在此引擎暂以射线 + 半径近似）
                bool rayHit = PhysicsWorld.Raycast(_smoothedPosition, probeDir, probeDist + ProbeRadius, out var hit);
                if (rayHit && hit.Distance < probeDist + ProbeRadius)
                {
                    // 留出探针半径的余量
                    armLength = Math.Max(MinArmLength, hit.Distance - ProbeRadius);
                }
            }
            CurrentArmLength = armLength;

            SocketWorldPosition = _smoothedPosition + smoothArmDir * armLength + SocketOffset;
            SocketWorldRotation = _smoothedRotation;
        }

        /// <summary>把指定相机组件绑定到本弹簧臂的臂端。</summary>
        public void AttachCamera(CameraComponent camera)
        {
            // 由 CinematicCameraSystem 在每帧调用 ApplyToCamera 完成实际写入
            ApplyToCamera(camera);
        }

        /// <summary>将臂端变换写入相机（供系统调用）。</summary>
        internal void ApplyToCamera(CameraComponent camera)
        {
            if (camera == null) return;
            camera.SetWorldTransform(SocketWorldPosition, SocketWorldRotation);
        }

        /// <summary>重置平滑状态（用于瞬移后的重新锚定）。</summary>
        public void Reset()
        {
            _initialized = false;
        }
    }

    // ============================================================================================
    //  CinematicCamera —— 电影摄像机
    // ============================================================================================

    /// <summary>
    ///   电影相机参数集合。模仿真实摄影机的物理量，用于景深 / 曝光 / 动态模糊等后处理。
    /// </summary>
    public struct CinematicCameraSettings
    {
        /// <summary>焦距（毫米）。影响 FOV 与景深。</summary>
        public float FocalLengthMm;

        /// <summary>光圈 f 值（如 1.8 / 2.8 / 5.6）。越小光圈越大，景深越浅。</summary>
        public float ApertureFStop;

        /// <summary>快门速度（秒的倒数，如 1/60 = 0.0167）。</summary>
        public float ShutterSpeed;

        /// <summary>感光度 ISO。</summary>
        public float ISO;

        /// <summary>对焦距离（米）。</summary>
        public float FocusDistance;

        /// <summary>是否启用景深。</summary>
        public bool EnableDepthOfField;

        /// <summary>景深模糊强度（后处理用）。</summary>
        public float BokehIntensity;

        /// <summary>默认配置（50mm / f2.8 / 1/60 / ISO 400）。</summary>
        public static CinematicCameraSettings Default => new()
        {
            FocalLengthMm = 50f,
            ApertureFStop = 2.8f,
            ShutterSpeed = 1f / 60f,
            ISO = 400f,
            FocusDistance = 10f,
            EnableDepthOfField = true,
            BokehIntensity = 1f
        };

        /// <summary>
        ///   根据焦距与传感器尺寸（默认 36mm 全画幅）计算水平 FOV（度）。
        /// </summary>
        public readonly float ComputeHorizontalFOV(float sensorWidthMm = 36f)
        {
            return 2f * MathF.Atan(sensorWidthMm * 0.5f / FocalLengthMm) * 180f / MathF.PI;
        }

        /// <summary>计算曝光值 EV100。</summary>
        public readonly float ComputeEV100()
        {
            // EV100 = log2(N^2 / t) - log2(ISO / 100)
            return MathF.Log2(ApertureFStop * ApertureFStop / ShutterSpeed) - MathF.Log2(ISO / 100f);
        }
    }

    /// <summary>
    ///   电影摄像机。在 CameraComponent 基础上附加电影参数、景深控制，并可由修改器/抖动进一步
    ///   调整最终输出变换。
    /// </summary>
    public class CinematicCamera : CameraComponent
    {
        /// <summary>电影参数。</summary>
        public CinematicCameraSettings CinematicSettings { get; set; } = CinematicCameraSettings.Default;

        /// <summary>绑定的弹簧臂（可选）。若设置，则变换由弹簧臂驱动。</summary>
        public SpringArmComponent? SpringArm { get; set; }

        /// <summary>Look-At 约束目标（世界位置）。设置后相机会始终注视该点。</summary>
        public Vector3? LookAtTarget { get; set; }

        /// <summary>Look-At 上向量。</summary>
        public Vector3 LookAtUp { get; set; } = Vector3.UnitY;

        /// <summary>Look-At 平滑系数（0=瞬时，1=极慢）。</summary>
        public float LookAtLag { get; set; } = 0.1f;

        // 抖动与修改器输出
        private Vector3 _shakeOffset = Vector3.Zero;
        private Quaternion _shakeRotation = Quaternion.Identity;

        /// <summary>抖动产生的位置偏移（已应用衰减）。</summary>
        public Vector3 ShakeOffset => _shakeOffset;

        /// <summary>抖动产生的旋转偏移。</summary>
        public Quaternion ShakeRotation => _shakeRotation;

        /// <summary>外部写入最终世界变换（供 SpringArm / 系统调用）。</summary>
        public override void SetWorldTransform(Vector3 position, Quaternion rotation)
        {
            // 不直接覆盖，保留以便 ApplyFinalTransform 合并抖动
            _basePosition = position;
            _baseRotation = rotation;
        }

        /// <summary>
        ///   外部直接写入最终变换（供 CameraModifier / CameraBlending 在抖动合并之后
        ///   再次覆盖最终输出使用）。
        /// </summary>
        internal void SetFinalTransform(Vector3 position, Quaternion rotation)
        {
            _finalPosition = position;
            _finalRotation = rotation;
        }

        private Vector3 _basePosition = Vector3.Zero;
        private Quaternion _baseRotation = Quaternion.Identity;

        protected override void UpdateTransform(float deltaTime)
        {
            // 1. 若有弹簧臂，先驱动臂（更新臂端变换），再写入相机基础变换
            if (SpringArm != null)
            {
                SpringArm.InvokeTick(deltaTime);
                SpringArm.ApplyToCamera(this); // 写入 _basePosition/_baseRotation
            }
            else
            {
                base.UpdateTransform(deltaTime);
                _basePosition = WorldPosition;
                _baseRotation = WorldRotation;
            }

            // 2. Look-At 约束（覆盖朝向）
            if (LookAtTarget.HasValue)
            {
                var desiredRot = LookRotation(LookAtTarget.Value - _basePosition, LookAtUp);
                float alpha = 1f - MathF.Pow(LookAtLag, deltaTime * 60f);
                _baseRotation = Quaternion.Slerp(_baseRotation, desiredRot, Math.Clamp(alpha, 0f, 1f));
            }

            // 3. 合并抖动偏移
            var finalPos = _basePosition + Vector3.Transform(_shakeOffset, _baseRotation);
            var finalRot = _baseRotation * _shakeRotation;

            // 写入最终变换
            SetFinal(finalPos, finalRot);

            // 4. 同步电影参数到基础 FOV（让景深与视图一致）
            FieldOfView = CinematicSettings.ComputeHorizontalFOV();
        }

        private void SetFinal(Vector3 pos, Quaternion rot)
        {
            // 通过反射写入基类只读字段不便，故提供内部缓冲并由属性返回
            _finalPosition = pos;
            _finalRotation = rot;
        }

        private Vector3 _finalPosition = Vector3.Zero;
        private Quaternion _finalRotation = Quaternion.Identity;

        /// <summary>抖动/约束叠加后的最终世界位置（供渲染读取）。</summary>
        public Vector3 FinalWorldPosition => _finalPosition;
        public Quaternion FinalWorldRotation => _finalRotation;
        public Matrix4x4 FinalViewMatrix
        {
            get
            {
                var eye = _finalPosition;
                var fwd = Vector3.Transform(-Vector3.UnitZ, _finalRotation);
                var up = Vector3.Transform(Vector3.UnitY, _finalRotation);
                return Matrix4x4.CreateLookAt(eye, eye + fwd, up);
            }
        }

        /// <summary>由系统注入抖动结果。</summary>
        internal void ApplyShake(Vector3 offset, Quaternion rotation)
        {
            _shakeOffset = offset;
            _shakeRotation = rotation;
        }
    }

    // ============================================================================================
    //  CameraShake —— 摄像机抖动
    // ============================================================================================

    /// <summary>抖动衰减方式。</summary>
    public enum ShakeDecay
    {
        /// <summary>指数衰减。</summary>
        Exponential,
        /// <summary>线性衰减。</summary>
        Linear,
        /// <summary>不衰减（持续抖动，由外部停止）。</summary>
        None
    }

    /// <summary>摄像机抖动基类。</summary>
    public abstract class CameraShake
    {
        /// <summary>位置幅度（世界单位）。</summary>
        public Vector3 PositionAmplitude = Vector3.One * 0.1f;

        /// <summary>旋转幅度（度）。</summary>
        public Vector3 RotationAmplitude = new(1f, 1f, 1f);

        /// <summary>抖动频率（Hz）。</summary>
        public float Frequency = 10f;

        /// <summary>衰减方式。</summary>
        public ShakeDecay Decay = ShakeDecay.Exponential;

        /// <summary>衰减时长（秒），0 表示瞬时。</summary>
        public float Duration = 0.5f;

        /// <summary>已运行时间。</summary>
        public float Elapsed;

        /// <summary>是否已完成。</summary>
        public bool IsFinished => Elapsed >= Duration && Decay != ShakeDecay.None;

        /// <summary>计算当前衰减系数（0..1）。</summary>
        protected float ComputeBlend(float dt)
        {
            Elapsed += dt;
            if (Decay == ShakeDecay.None) return 1f;
            if (Duration <= 0f) return 0f;
            float t = Math.Clamp(Elapsed / Duration, 0f, 1f);
            return Decay switch
            {
                ShakeDecay.Exponential => MathF.Exp(-3f * t),
                ShakeDecay.Linear => 1f - t,
                _ => 1f - t
            };
        }

        /// <summary>子类实现：根据时间与衰减生成 (位置偏移, 旋转偏移四元数)。</summary>
        public abstract (Vector3 offset, Quaternion rotation) Sample(float dt);
    }

    /// <summary>噪声驱动的摄像机抖动（伪 Perlin，由多频正弦叠加近似）。</summary>
    public class NoiseCameraShake : CameraShake
    {
        private float _seedX, _seedY, _seedZ;

        public NoiseCameraShake()
        {
            var rnd = new Random();
            _seedX = (float)rnd.NextDouble() * 1000f;
            _seedY = (float)rnd.NextDouble() * 1000f;
            _seedZ = (float)rnd.NextDouble() * 1000f;
        }

        public override (Vector3, Quaternion) Sample(float dt)
        {
            float blend = ComputeBlend(dt);
            float t = Elapsed * Frequency;
            // 多频叠加近似 1/f 噪声
            float nx = MathF.Sin(t + _seedX) * 0.6f + MathF.Sin(t * 2.3f + _seedX) * 0.3f + MathF.Sin(t * 5.1f + _seedX) * 0.1f;
            float ny = MathF.Sin(t * 1.1f + _seedY) * 0.6f + MathF.Sin(t * 2.7f + _seedY) * 0.3f + MathF.Sin(t * 4.9f + _seedY) * 0.1f;
            float nz = MathF.Sin(t * 0.9f + _seedZ) * 0.6f + MathF.Sin(t * 3.1f + _seedZ) * 0.3f + MathF.Sin(t * 6.3f + _seedZ) * 0.1f;

            var offset = new Vector3(nx, ny, nz) * PositionAmplitude * blend;
            var eulRot = new Vector3(nx, ny, nz) * RotationAmplitude * blend;
            float rad = MathF.PI / 180f;
            var rot = Quaternion.CreateFromYawPitchRoll(eulRot.Y * rad, eulRot.X * rad, eulRot.Z * rad);
            return (offset, rot);
        }
    }

    /// <summary>曲线驱动的摄像机抖动（使用预置衰减包络）。</summary>
    public class CurveCameraShake : CameraShake
    {
        /// <summary>包络函数：攻击时间占比。</summary>
        public float AttackRatio = 0.1f;

        public override (Vector3, Quaternion) Sample(float dt)
        {
            float blend = ComputeBlend(dt);
            float t = Math.Clamp(Elapsed / Math.Max(Duration, 1e-4f), 0f, 1f);
            // 攻击-衰减包络
            float env;
            if (t < AttackRatio && AttackRatio > 0f)
                env = t / AttackRatio;
            else
                env = 1f - (t - AttackRatio) / Math.Max(1f - AttackRatio, 1e-4f);
            env = Math.Clamp(env, 0f, 1f) * blend;

            float s = MathF.Sin(Elapsed * Frequency * 2f * MathF.PI);
            var offset = new Vector3(s, s * 0.7f, s * 0.5f) * PositionAmplitude * env;
            var eulRot = new Vector3(s, s * 0.6f, s * 0.4f) * RotationAmplitude * env;
            float rad = MathF.PI / 180f;
            var rot = Quaternion.CreateFromYawPitchRoll(eulRot.Y * rad, eulRot.X * rad, eulRot.Z * rad);
            return (offset, rot);
        }
    }

    // ============================================================================================
    //  CameraModifier —— 摄像机修改器
    // ============================================================================================

    /// <summary>
    ///   摄像机修改器基类。修改器以栈的方式叠加在相机之上，可对最终视图变换做进一步调整
    ///   （例如固定方向、抖动、特殊视角）。每个修改器有自己的优先级与启用状态。
    /// </summary>
    public abstract class CameraModifier
    {
        /// <summary>优先级（越大越后处理，越接近最终输出）。</summary>
        public int Priority { get; set; }

        /// <summary>是否启用。</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>是否已销毁（不再参与处理）。</summary>
        public bool IsDestroyed { get; private set; }

        /// <summary>对输入变换做修改并返回输出变换。</summary>
        public abstract (Vector3 position, Quaternion rotation) Modify(
            Vector3 inPosition, Quaternion inRotation, float dt);

        /// <summary>销毁本修改器。</summary>
        public virtual void Destroy() => IsDestroyed = true;
    }

    /// <summary>固定偏移修改器：在最终位置上叠加一个固定偏移。</summary>
    public class FixedOffsetModifier : CameraModifier
    {
        public Vector3 Offset = Vector3.Zero;

        public override (Vector3, Quaternion) Modify(Vector3 inPosition, Quaternion inRotation, float dt)
            => (inPosition + Offset, inRotation);
    }

    /// <summary>
    ///   摄像机修改器栈。按优先级排序，依次调用 Modify。
    /// </summary>
    public class CameraModifierStack
    {
        private readonly List<CameraModifier> _modifiers = new();

        public void Add(CameraModifier modifier)
        {
            if (modifier == null) return;
            _modifiers.Add(modifier);
            _modifiers.Sort((a, b) => a.Priority.CompareTo(b.Priority));
        }

        public bool Remove(CameraModifier modifier) => _modifiers.Remove(modifier);

        public void ClearDestroyed()
        {
            _modifiers.RemoveAll(m => m.IsDestroyed);
        }

        /// <summary>依次应用所有启用的修改器。</summary>
        public (Vector3 position, Quaternion rotation) Apply(
            Vector3 position, Quaternion rotation, float dt)
        {
            foreach (var m in _modifiers)
            {
                if (!m.Enabled || m.IsDestroyed) continue;
                (position, rotation) = m.Modify(position, rotation, dt);
            }
            return (position, rotation);
        }
    }

    // ============================================================================================
    //  CameraBlending —— 多摄像机混合
    // ============================================================================================

    /// <summary>
    ///   一次摄像机混合的描述：从 A 相机过渡到 B 相机。
    /// </summary>
    public struct CameraBlendState
    {
        public CinematicCamera From;
        public CinematicCamera To;
        public CameraBlendMode Mode;
        public EaseType Ease;
        public float Duration;
        public float Elapsed;
        public bool Active;

        /// <summary>混合进度 0..1。</summary>
        public readonly float Alpha => Duration > 0f ? Math.Clamp(Elapsed / Duration, 0f, 1f) : 1f;
    }

    /// <summary>多摄像机混合管理器。</summary>
    public class CameraBlending
    {
        private CameraBlendState _state;

        /// <summary>触发一次混合。</summary>
        public void BlendTo(CinematicCamera from, CinematicCamera to, CameraBlendMode mode,
                            float duration, EaseType ease = EaseType.EaseInOutCubic)
        {
            _state = new CameraBlendState
            {
                From = from,
                To = to,
                Mode = mode,
                Ease = ease,
                Duration = duration,
                Elapsed = 0f,
                Active = true
            };
        }

        /// <summary>硬切：立即切换到目标相机。</summary>
        public void CutTo(CinematicCamera to) => _state = new CameraBlendState
        {
            From = to,
            To = to,
            Mode = CameraBlendMode.Cut,
            Duration = 0f,
            Elapsed = 0f,
            Active = false
        };

        /// <summary>当前混合是否仍在进行。</summary>
        public bool IsBlending => _state.Active && _state.Elapsed < _state.Duration;

        /// <summary>推进混合并返回当前应使用的相机（混合中则返回 To，并在其上注入插值变换）。</summary>
        public CinematicCamera? Update(float dt)
        {
            if (!_state.Active) return _state.To;

            if (_state.Mode == CameraBlendMode.Cut)
            {
                _state.Active = false;
                return _state.To;
            }

            _state.Elapsed += dt;
            float alpha = _state.Alpha;
            float eased = ApplyEase(alpha, _state.Ease);

            // 把混合变换写入 To 相机作为最终输出（覆盖抖动后的最终变换）
            if (_state.From != null && _state.To != null)
            {
                var p = Vector3.Lerp(_state.From.FinalWorldPosition, _state.To.FinalWorldPosition, eased);
                var r = Quaternion.Slerp(_state.From.FinalWorldRotation, _state.To.FinalWorldRotation, eased);
                _state.To.SetFinalTransform(p, r);
            }

            if (alpha >= 1f) _state.Active = false;
            return _state.To;
        }

        private static float ApplyEase(float t, EaseType ease) => ease switch
        {
            EaseType.Linear => t,
            EaseType.EaseInQuad => t * t,
            EaseType.EaseOutQuad => 1f - (1f - t) * (1f - t),
            EaseType.EaseInOutQuad => t < 0.5f ? 2f * t * t : 1f - MathF.Pow(-2f * t + 2f, 2f) * 0.5f,
            EaseType.EaseInOutCubic => t < 0.5f ? 4f * t * t * t : 1f - MathF.Pow(-2f * t + 2f, 3f) * 0.5f,
            _ => t
        };
    }

    // ============================================================================================
    //  CameraRig —— 摄像机摇臂 / 轨道 / 起重机
    // ============================================================================================

    /// <summary>摇臂类型。</summary>
    public enum RigType { Crane, DollyTrack, Boom }

    /// <summary>
    ///   摄像机摇臂/轨道/起重机。沿预设路径驱动一个锚点 Actor，相机挂在锚点上，
    ///   用于实现电影感运镜。
    /// </summary>
    public class CameraRig
    {
        /// <summary>摇臂类型。</summary>
        public RigType Type { get; set; } = RigType.Crane;

        /// <summary>锚点 Actor（相机挂载点）。</summary>
        public Actor? RigActor { get; set; }

        /// <summary>轨道起点（世界坐标）。</summary>
        public Vector3 TrackStart { get; set; }

        /// <summary>轨道终点（世界坐标）。</summary>
        public Vector3 TrackEnd { get; set; }

        /// <summary>起重机臂长（米）。</summary>
        public float CraneArmLength { get; set; } = 8f;

        /// <summary>起重机臂俯仰角（度）。</summary>
        public float CranePitch { get; set; } = -20f;

        /// <summary>运动进度 0..1（外部驱动）。</summary>
        public float Progress { get; set; }

        /// <summary>是否循环（轨道用）。</summary>
        public bool Loop { get; set; }

        /// <summary>前进速度（进度/秒）。</summary>
        public float Speed { get; set; } = 0.1f;

        private float _t;

        /// <summary>每帧推进摇臂。</summary>
        public void Update(float dt)
        {
            if (RigActor == null) return;
            _t += Speed * dt;
            float p = Loop ? (_t % 1f) : Math.Clamp(_t, 0f, 1f);
            Progress = p;

            switch (Type)
            {
                case RigType.DollyTrack:
                    RigActor.Position = Vector3.Lerp(TrackStart, TrackEnd, p);
                    break;

                case RigType.Boom:
                    // 摇臂绕基座旋转
                    float angle = p * MathF.PI * 2f;
                    var basePos = TrackStart;
                    RigActor.Position = basePos + new Vector3(MathF.Cos(angle) * CraneArmLength, 0, MathF.Sin(angle) * CraneArmLength);
                    break;

                case RigType.Crane:
                    // 起重机：水平插值 + 俯仰决定高度
                    float pitchRad = CranePitch * MathF.PI / 180f;
                    float h = Vector3.Lerp(TrackStart, TrackEnd, p).X;
                    float z = Vector3.Lerp(TrackStart, TrackEnd, p).Z;
                    float y = TrackStart.Y + MathF.Sin(pitchRad) * CraneArmLength + p * 3f;
                    RigActor.Position = new Vector3(h, y, z);
                    break;
            }
        }
    }

    // ============================================================================================
    //  摄像机碰撞检测（射线 / 穿透回缩）独立工具
    // ============================================================================================

    /// <summary>
    ///   摄像机碰撞工具。除了弹簧臂内置的回缩，这里提供更完整的穿透回缩逻辑：
    ///   当目标与相机之间被遮挡时，把相机拉到命中点前方，避免穿墙。
    /// </summary>
    public static class CameraCollision
    {
        /// <summary>
        ///   从目标向期望相机位置做球体扫描，返回不发生穿透的安全位置。
        /// </summary>
        /// <param name="physics">物理世界。</param>
        /// <param name="target">被观察目标位置。</param>
        /// <param name="desiredCameraPos">期望的相机位置。</param>
        /// <param name="probeRadius">探针半径。</param>
        /// <param name="minDistance">保留的最小距离。</param>
        /// <returns>安全相机位置。</returns>
        public static Vector3 ResolvePenetration(PhysicsWorld physics, Vector3 target,
            Vector3 desiredCameraPos, float probeRadius, float minDistance)
        {
            if (physics == null) return desiredCameraPos;
            var dir = desiredCameraPos - target;
            float desiredDist = dir.Length();
            if (desiredDist < 1e-4f) return desiredCameraPos;
            dir /= desiredDist;

            bool rayHit = physics.Raycast(target, dir, desiredDist + probeRadius, out var hit);
            if (!rayHit) return desiredCameraPos;

            float safeDist = Math.Max(minDistance, hit.Distance - probeRadius);
            return target + dir * safeDist;
        }
    }

    // ============================================================================================
    //  CinematicCameraSystem —— 系统入口（对接 ECS）
    // ============================================================================================

    /// <summary>
    ///   电影摄像机系统。每帧：
    ///     1. 更新所有 CinematicCamera（含弹簧臂 / Look-At / 电影参数）
    ///     2. 推进活跃的 CameraShake 并注入到对应相机
    ///     3. 应用 CameraModifierStack
    ///     4. 推进 CameraBlending
    ///   对接 ECS：可从 ECS 查询带有 CinematicCamera 引用的实体并集中更新。
    /// </summary>
    public class CinematicCameraSystem : ISystem
    {
        private readonly List<CinematicCamera> _cameras = new();
        private readonly List<(CinematicCamera cam, CameraShake shake)> _shakes = new();
        private readonly Dictionary<CinematicCamera, CameraModifierStack> _modifiers = new();
        private readonly CameraBlending _blending = new();
        private readonly CameraRig _rig = new();

        public string Name => "CinematicCameraSystem";

        public CameraBlending Blending => _blending;
        public CameraRig Rig => _rig;

        /// <summary>注册一个电影摄像机。</summary>
        public void Register(CinematicCamera camera) => _cameras.Add(camera);

        /// <summary>注销。</summary>
        public bool Unregister(CinematicCamera camera) => _cameras.Remove(camera);

        /// <summary>为指定相机播放抖动。</summary>
        public void PlayShake(CinematicCamera camera, CameraShake shake)
        {
            if (camera == null || shake == null) return;
            _shakes.Add((camera, shake));
        }

        /// <summary>为指定相机获取（或创建）修改器栈。</summary>
        public CameraModifierStack GetModifiers(CinematicCamera camera)
        {
            if (!_modifiers.TryGetValue(camera, out var stack))
            {
                stack = new CameraModifierStack();
                _modifiers[camera] = stack;
            }
            return stack;
        }

        /// <summary>请求混合到新相机。</summary>
        public void BlendTo(CinematicCamera from, CinematicCamera to, CameraBlendMode mode,
                            float duration, EaseType ease = EaseType.EaseInOutCubic)
            => _blending.BlendTo(from, to, mode, duration, ease);

        /// <summary>硬切到新相机。</summary>
        public void CutTo(CinematicCamera to) => _blending.CutTo(to);

        public void Update(float dt, EntityComponentSystem ecs)
        {
            // 1. 更新摇臂（驱动其锚点 Actor）
            _rig.Update(dt);

            // 2. 先推进抖动并注入相机（保证本帧 tick 能合并最新抖动，避免一帧延迟）
            for (int i = _shakes.Count - 1; i >= 0; i--)
            {
                var (cam, shake) = _shakes[i];
                var (offset, rot) = shake.Sample(dt);
                cam.ApplyShake(offset, rot);
                if (shake.IsFinished)
                    _shakes.RemoveAt(i);
            }

            // 3. 更新所有相机的基础变换（含弹簧臂 / Look-At / 抖动合并）
            foreach (var cam in _cameras)
                cam.InvokeTick(dt);

            // 4. 应用修改器栈（叠加到最终变换，覆盖最终输出）
            foreach (var kvp in _modifiers)
            {
                kvp.Value.ClearDestroyed();
                var (pos, rot) = kvp.Value.Apply(kvp.Key.FinalWorldPosition, kvp.Key.FinalWorldRotation, dt);
                kvp.Key.SetFinalTransform(pos, rot);
            }

            // 5. 推进混合（混合会覆盖 To 相机的最终变换）
            _blending.Update(dt);
        }
    }
}
