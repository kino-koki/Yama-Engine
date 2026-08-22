using System;
using System.Collections.Generic;
using System.Numerics;

namespace ShanyuEngine.Sequencer
{
    // ════════════════════════════════════════════════════════════════════════
    // 山雨引擎 - 虚拟相机系统 (Cinemachine System)
    // --------------------------------------------------------------------------
    // 类似 Unity Cinemachine：虚拟相机 + 大脑架构。
    //   - VirtualCamera 基类及其 8 种子类型
    //   - CinemachineBrain：管理所有虚拟相机、优先级切换、混合过渡
    //   - CinemachineBlend：混合定义（Cut/Linear/EaseInOut/HardCut/CustomCurve）
    //   - CameraShake / ImpulseListener / CameraNoise：震动与噪声
    //   - 用户控制覆盖、无效回退
    // ════════════════════════════════════════════════════════════════════════

    // ──────────────────────────────────────────────────────────────────────────
    // 枚举定义
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>虚拟相机状态。</summary>
    public enum VirtualCameraState
    {
        /// <summary>待机。</summary>
        Idle,
        /// <summary>正在跟随（活跃输出）。</summary>
        Live,
        /// <summary>正在混合过渡中。</summary>
        Blending,
        /// <summary>被用户覆盖接管。</summary>
        UserOverride
    }

    /// <summary>混合风格。</summary>
    public enum CinemachineBlendStyle
    {
        /// <summary>硬切：瞬间切换。</summary>
        HardCut,
        /// <summary>线性混合。</summary>
        Linear,
        /// <summary>缓入缓出混合。</summary>
        EaseInOut,
        /// <summary>自定义曲线混合。</summary>
        CustomCurve
    }

    /// <summary>组合相机模式。</summary>
    public enum GroupComposeMode
    {
        /// <summary>框住所有目标。</summary>
        Group,
        /// <summary>仅框住最小目标。</summary>
        Smallest,
        /// <summary>框住最远目标。</summary>
        Farthest,
        /// <summary>框住最近目标。</summary>
        Nearest
    }

    /// <summary>路径结束行为。</summary>
    public enum PathEndBehavior
    {
        /// <summary>停止。</summary>
        Stop,
        /// <summary>循环。</summary>
        Loop,
        /// <summary>回弹。</summary>
        PingPong
    }

    // ──────────────────────────────────────────────────────────────────────────
    // CinemachineBlend - 混合定义
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>相机混合定义：描述从源相机到目标相机的过渡方式。</summary>
    public class CinemachineBlend
    {
        /// <summary>源虚拟相机。</summary>
        public VirtualCamera? SourceCamera { get; set; }

        /// <summary>目标虚拟相机。</summary>
        public VirtualCamera? TargetCamera { get; set; }

        /// <summary>混合总时间（秒）。</summary>
        public double Duration { get; set; } = 0.5;

        /// <summary>混合风格。</summary>
        public CinemachineBlendStyle Style { get; set; } = CinemachineBlendStyle.EaseInOut;

        /// <summary>自定义混合曲线（Style=CustomCurve 时使用）。</summary>
        public Func<float, float>? CustomCurve { get; set; }

        /// <summary>混合进度（0=源，1=目标）。</summary>
        public float Progress { get; private set; }

        /// <summary>混合已用时间。</summary>
        private double _elapsed;

        /// <summary>混合是否完成。</summary>
        public bool IsComplete => Progress >= 1.0f;

        /// <summary>计算当前混合权重。</summary>
        public float GetBlendWeight()
        {
            return EvaluateCurve(Progress);
        }

        /// <summary>评估混合曲线。</summary>
        private float EvaluateCurve(float t)
        {
            t = Math.Clamp(t, 0f, 1f);

            return Style switch
            {
                CinemachineBlendStyle.HardCut => t >= 0.5f ? 1f : 0f,
                CinemachineBlendStyle.Linear => t,
                CinemachineBlendStyle.EaseInOut => SmoothStep(t),
                CinemachineBlendStyle.CustomCurve => CustomCurve?.Invoke(t) ?? t,
                _ => t
            };
        }

        /// <summary>更新混合进度。</summary>
        public void Update(double deltaTime)
        {
            if (IsComplete) return;

            _elapsed += deltaTime;
            if (Duration > 0)
            {
                Progress = (float)Math.Clamp(_elapsed / Duration, 0, 1);
            }
            else
            {
                Progress = 1.0f;
            }
        }

        /// <summary>重置混合。</summary>
        public void Reset()
        {
            Progress = 0f;
            _elapsed = 0;
        }

        private static float SmoothStep(float t)
        {
            return t * t * (3f - 2f * t);
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // VirtualCamera 基类
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 虚拟相机基类：所有 Cinemachine 相机的共同抽象。
    /// 不直接渲染，而是计算理想相机状态，由 CinemachineBrain 应用到输出相机。
    /// </summary>
    public class VirtualCamera
    {
        /// <summary>相机唯一标识。</summary>
        public Guid Id { get; } = Guid.NewGuid();

        /// <summary>相机名称。</summary>
        public string Name { get; set; } = "VirtualCamera";

        /// <summary>优先级（数值越大优先级越高）。</summary>
        public int Priority { get; set; } = 10;

        /// <summary>是否激活。</summary>
        public bool IsActive { get; set; } = true;

        /// <summary>混合时间（切换到此相机时的过渡时长）。</summary>
        public double BlendTime { get; set; } = 0.5;

        /// <summary>混合风格。</summary>
        public CinemachineBlendStyle BlendStyle { get; set; } = CinemachineBlendStyle.EaseInOut;

        /// <summary>自定义混合曲线。</summary>
        public Func<float, float>? CustomBlendCurve { get; set; }

        /// <summary>相机当前状态。</summary>
        public VirtualCameraState State { get; set; } = VirtualCameraState.Idle;

        /// <summary>当前计算的相机位置。</summary>
        public Vector3 CurrentPosition { get; protected set; }

        /// <summary>当前计算的相机朝向（旋转四元数）。</summary>
        public Quaternion CurrentRotation { get; protected set; } = Quaternion.Identity;

        /// <summary>当前计算的 FOV。</summary>
        public float CurrentFOV { get; protected set; } = 60.0f;

        /// <summary>输出相机的引用（实际渲染用相机）。</summary>
        public object? OutputCamera { get; set; }

        /// <summary>关联的震动噪声。</summary>
        public CameraNoise? Noise { get; set; }

        /// <summary>关联的震动配置。</summary>
        public CameraShake? Shake { get; set; }

        /// <summary>跟随目标对象。</summary>
        public object? FollowTarget { get; set; }

        /// <summary>看向目标对象。</summary>
        public object? LookAtTarget { get; set; }

        public VirtualCamera() { }

        public VirtualCamera(string name, int priority = 10)
        {
            Name = name;
            Priority = priority;
        }

        /// <summary>更新相机状态（每帧调用）。</summary>
        public virtual void UpdateState(float deltaTime)
        {
            // 应用震动噪声
            if (Noise != null)
            {
                var noiseOffset = Noise.Evaluate(deltaTime);
                CurrentPosition += noiseOffset.Position;
                CurrentRotation = QuaternionMultiply(CurrentRotation, noiseOffset.Rotation);
            }
        }

        /// <summary>获取相机状态（位置、旋转、FOV）。</summary>
        public virtual (Vector3 position, Quaternion rotation, float fov) GetState()
        {
            return (CurrentPosition, CurrentRotation, CurrentFOV);
        }

        /// <summary>设置直接位置（用于编辑器预览）。</summary>
        public void SetDirectState(Vector3 position, Quaternion rotation, float fov)
        {
            CurrentPosition = position;
            CurrentRotation = rotation;
            CurrentFOV = fov;
        }

        /// <summary>四元数乘法辅助。</summary>
        protected static Quaternion QuaternionMultiply(Quaternion a, Quaternion b)
        {
            return new Quaternion(
                a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
                a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X,
                a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W,
                a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z
            );
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // FollowCamera - 跟随相机
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 跟随相机：跟随目标对象移动。
    /// 支持偏移、阻尼、死区、硬限/软限、lookahead。
    /// </summary>
    public class FollowCamera : VirtualCamera
    {
        /// <summary>跟随偏移（相对目标的本地偏移）。</summary>
        public Vector3 FollowOffset { get; set; } = new Vector3(0, 5, -10);

        /// <summary>位置阻尼系数（0=无阻尼，1=最大阻尼）。</summary>
        public float PositionDamping { get; set; } = 0.5f;

        /// <summary>旋转阻尼系数。</summary>
        public float RotationDamping { get; set; } = 0.5f;

        /// <summary>死区半径（目标移动小于此值不触发跟随）。</summary>
        public float DeadZoneRadius { get; set; }

        /// <summary>硬限（跟随距离的绝对上限）。</summary>
        public float HardLimitDistance { get; set; } = float.MaxValue;

        /// <summary>软限（超过此距离开始拉回）。</summary>
        public float SoftLimitDistance { get; set; } = float.MaxValue;

        /// <summary>软限区内的拉回力度。</summary>
        public float SoftLimitForce { get; set; } = 1.0f;

        /// <summary>前瞻（lookahead）：预测目标移动方向。</summary>
        public float LookaheadTime { get; set; }

        /// <summary>前瞻平滑度。</summary>
        public float LookaheadSmoothing { get; set; } = 5.0f;

        /// <summary>上一次的目标位置（用于计算速度）。</summary>
        private Vector3 _lastTargetPosition;

        /// <summary>速度缓存（用于 lookahead）。</summary>
        private Vector3 _velocity;

        public FollowCamera() { }
        public FollowCamera(string name, int priority = 10) : base(name, priority) { }

        public override void UpdateState(float deltaTime)
        {
            base.UpdateState(deltaTime);

            if (FollowTarget == null) return;

            // 获取目标位置（简化：通过回调或反射，这里使用委托模式）
            // 实际引擎中会通过 Transform 组件获取世界位置
            _velocity = (CurrentPosition - _lastTargetPosition) / Math.Max(deltaTime, 1e-6f);
            _lastTargetPosition = CurrentPosition;

            Vector3 targetPos = CurrentPosition; // 简化
            Vector3 desiredPos = targetPos + FollowOffset;

            // Lookahead
            if (LookaheadTime > 0)
            {
                desiredPos += _velocity * LookaheadTime;
            }

            // 应用阻尼
            float dampFactor = 1f - Math.Clamp(PositionDamping, 0, 1);
            CurrentPosition = Vector3.Lerp(CurrentPosition, desiredPos, dampFactor);

            // 硬限
            float dist = Vector3.Distance(CurrentPosition, targetPos);
            if (dist > HardLimitDistance)
            {
                Vector3 dir = Vector3.Normalize(CurrentPosition - targetPos);
                CurrentPosition = targetPos + dir * HardLimitDistance;
            }
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // LookAtCamera - 看向相机
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 看向相机：保持看向目标对象，支持阻尼、维持、死区、偏移。
    /// </summary>
    public class LookAtCamera : VirtualCamera
    {
        /// <summary>看向偏移（相对目标的偏移）。</summary>
        public Vector3 LookAtOffset { get; set; }

        /// <summary>旋转阻尼系数。</summary>
        public float RotationDamping { get; set; } = 0.5f;

        /// <summary>是否在目标消失时维持最后的朝向。</summary>
        public bool MaintainLookAt { get; set; } = true;

        /// <summary>死区角度（度）。</summary>
        public float DeadZoneAngle { get; set; }

        /// <summary>上一次的目标位置。</summary>
        private Vector3 _lastLookAtPosition;

        public LookAtCamera() { }
        public LookAtCamera(string name, int priority = 10) : base(name, priority) { }

        public override void UpdateState(float deltaTime)
        {
            base.UpdateState(deltaTime);

            if (LookAtTarget == null)
            {
                if (MaintainLookAt) return;
                return;
            }

            Vector3 targetPos = CurrentPosition; // 简化
            Vector3 lookTarget = targetPos + LookAtOffset;

            Vector3 forward = lookTarget - CurrentPosition;
            if (forward.LengthSquared() < 1e-6f) return;

            forward = Vector3.Normalize(forward);
            Quaternion desiredRot = QuaternionLookRotation(forward, Vector3.UnitY);

            float dampFactor = 1f - Math.Clamp(RotationDamping, 0, 1);
            CurrentRotation = Quaternion.Slerp(CurrentRotation, desiredRot, dampFactor);

            _lastLookAtPosition = lookTarget;
        }

        private static Quaternion QuaternionLookRotation(Vector3 forward, Vector3 up)
        {
            forward = Vector3.Normalize(forward);
            Vector3 right = Vector3.Normalize(Vector3.Cross(forward, up));
            Vector3 correctedUp = Vector3.Cross(right, forward);

            var m = new Matrix4x4(
                right.X, right.Y, right.Z, 0,
                correctedUp.X, correctedUp.Y, correctedUp.Z, 0,
                -forward.X, -forward.Y, -forward.Z, 0,
                0, 0, 0, 1);

            return Quaternion.CreateFromRotationMatrix(m);
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // FreeLookCamera - 自由观察相机
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 自由观察相机（FreeLook）：3 个环形区域（顶部/中部/底部），
    /// 各有独立 rig，可在 3 个区域间平滑过渡。
    /// </summary>
    public class FreeLookCamera : VirtualCamera
    {
        /// <summary>顶部 Rig 配置。</summary>
        public FreeLookRig TopRig { get; set; } = new FreeLookRig();

        /// <summary>中部 Rig 配置。</summary>
        public FreeLookRig MiddleRig { get; set; } = new FreeLookRig();

        /// <summary>底部 Rig 配置。</summary>
        public FreeLookRig BottomRig { get; set; } = new FreeLookRig();

        /// <summary>围绕的轴线（X=左右，Y=上下）。</summary>
        public Vector3 OrbitAxis { get; set; } = Vector3.UnitY;

        /// <summary>当前垂直角度（-1=底部，0=中部，1=顶部）。</summary>
        public float VerticalAngle { get; set; }

        /// <summary>当前水平角度（度）。</summary>
        public float HorizontalAngle { get; set; }

        /// <summary>垂直角度变化速度。</summary>
        public float VerticalSpeed { get; set; } = 2.0f;

        /// <summary>水平角度变化速度。</summary>
        public float HorizontalSpeed { get; set; } = 100.0f;

        public FreeLookCamera() { }
        public FreeLookCamera(string name, int priority = 10) : base(name, priority) { }

        public override void UpdateState(float deltaTime)
        {
            base.UpdateState(deltaTime);

            // 根据 VerticalAngle 混合三个 Rig
            float topWeight = Math.Clamp(VerticalAngle, 0, 1);
            float bottomWeight = Math.Clamp(-VerticalAngle, 0, 1);
            float midWeight = 1.0f - topWeight - bottomWeight;

            Vector3 topPos = TopRig.GetRigPosition(FollowTarget, HorizontalAngle, OrbitAxis);
            Vector3 midPos = MiddleRig.GetRigPosition(FollowTarget, HorizontalAngle, OrbitAxis);
            Vector3 botPos = BottomRig.GetRigPosition(FollowTarget, HorizontalAngle, OrbitAxis);

            CurrentPosition = topPos * topWeight + midPos * midWeight + botPos * bottomWeight;
        }
    }

    /// <summary>FreeLook 相机的单个 Rig（环带配置）。</summary>
    public class FreeLookRig
    {
        /// <summary>围绕半径。</summary>
        public float Radius { get; set; } = 10.0f;

        /// <summary>高度偏移。</summary>
        public float Height { get; set; }

        /// <summary>阻尼系数。</summary>
        public float Damping { get; set; } = 0.5f;

        public Vector3 GetRigPosition(object? target, float horizontalAngle, Vector3 orbitAxis)
        {
            // 简化：使用水平角度在 XZ 平面旋转
            float rad = horizontalAngle * MathF.PI / 180.0f;
            float x = MathF.Cos(rad) * Radius;
            float z = MathF.Sin(rad) * Radius;
            return new Vector3(x, Height, z);
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // GroupCamera - 组合相机
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 组合相机（Group）：同时跟踪多个目标，
    /// 使用不同的组合模式决定如何构图。
    /// </summary>
    public class GroupCamera : VirtualCamera
    {
        /// <summary>目标列表。</summary>
        public List<object?> Targets { get; set; } = new();

        /// <summary>组合模式。</summary>
        public GroupComposeMode ComposeMode { get; set; } = GroupComposeMode.Group;

        /// <summary>框架大小（屏幕空间占比）。</summary>
        public float FrameSize { get; set; } = 0.5f;

        /// <summary>位移（整体偏移）。</summary>
        public Vector3 Displacement { get; set; }

        /// <summary>阻尼系数。</summary>
        public float Damping { get; set; } = 0.5f;

        /// <summary>目标位置获取回调。</summary>
        public Func<object?, Vector3>? GetTargetPosition { get; set; }

        public GroupCamera() { }
        public GroupCamera(string name, int priority = 10) : base(name, priority) { }

        public override void UpdateState(float deltaTime)
        {
            base.UpdateState(deltaTime);

            if (Targets.Count == 0 || GetTargetPosition == null) return;

            // 计算目标的中心点和包围球
            Vector3 center = Vector3.Zero;
            float maxDist = 0;

            foreach (var target in Targets)
            {
                if (target == null) continue;
                Vector3 pos = GetTargetPosition(target);
                center += pos;

                float dist = Vector3.Distance(pos, center);
                if (dist > maxDist) maxDist = dist;
            }

            center /= Math.Max(1, Targets.Count);

            Vector3 desiredPos = center + Displacement;

            float dampFactor = 1f - Math.Clamp(Damping, 0, 1);
            CurrentPosition = Vector3.Lerp(CurrentPosition, desiredPos, dampFactor);

            // 调整 FOV 以容纳所有目标
            float desiredFov = maxDist > 0 ? MathF.Atan2(maxDist, 10f) * (180f / MathF.PI) * 2 : 60f;
            CurrentFOV = Math.Clamp(desiredFov, 20f, 120f);
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // PathCamera - 路径相机
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 路径相机：沿预定义路径移动。
    /// </summary>
    public class PathCamera : VirtualCamera
    {
        /// <summary>路径点列表。</summary>
        public List<Vector3> PathPoints { get; set; } = new();

        /// <summary>移动速度（单位/秒）。</summary>
        public float Speed { get; set; } = 5.0f;

        /// <summary>朝向模式：沿路径方向 / 固定朝向 / 看向目标。</summary>
        public int OrientMode { get; set; } // 0=沿路径, 1=固定, 2=看向目标

        /// <summary>固定朝向。</summary>
        public Quaternion FixedRotation { get; set; } = Quaternion.Identity;

        /// <summary>路径结束行为。</summary>
        public PathEndBehavior EndBehavior { get; set; } = PathEndBehavior.Stop;

        /// <summary>当前路径进度（0~1）。</summary>
        public float PathProgress { get; private set; }

        /// <summary>当前路径方向（正向/反向）。</summary>
        private bool _forwardDirection = true;

        public PathCamera() { }
        public PathCamera(string name, int priority = 10) : base(name, priority) { }

        public override void UpdateState(float deltaTime)
        {
            base.UpdateState(deltaTime);

            if (PathPoints.Count < 2) return;

            // 计算路径总长度
            float totalLength = CalculatePathLength();
            if (totalLength < 1e-6f) return;

            float deltaProgress = (Speed * deltaTime) / totalLength;

            if (_forwardDirection)
            {
                PathProgress += deltaProgress;
                if (PathProgress >= 1.0f)
                {
                    switch (EndBehavior)
                    {
                        case PathEndBehavior.Stop:
                            PathProgress = 1.0f;
                            break;
                        case PathEndBehavior.Loop:
                            PathProgress -= 1.0f;
                            break;
                        case PathEndBehavior.PingPong:
                            PathProgress = 2.0f - PathProgress;
                            _forwardDirection = false;
                            break;
                    }
                }
            }
            else
            {
                PathProgress -= deltaProgress;
                if (PathProgress <= 0)
                {
                    PathProgress = -PathProgress;
                    _forwardDirection = true;
                }
            }

            PathProgress = Math.Clamp(PathProgress, 0, 1);
            (Vector3 pos, Vector3 tangent) = EvaluatePath(PathProgress);

            CurrentPosition = pos;

            if (OrientMode == 0) // 沿路径方向
            {
                if (tangent.LengthSquared() > 1e-6f)
                {
                    Vector3 forward = Vector3.Normalize(tangent);
                    CurrentRotation = LookAtCamera.QuaternionLookRotation(forward, Vector3.UnitY);
                }
            }
        }

        /// <summary>评估路径上的位置和切线。</summary>
        private (Vector3 position, Vector3 tangent) EvaluatePath(float t)
        {
            int segCount = PathPoints.Count - 1;
            float scaledT = t * segCount;
            int segIndex = Math.Min((int)scaledT, segCount - 1);
            float segT = scaledT - segIndex;

            Vector3 p0 = PathPoints[Math.Max(0, segIndex - 1)];
            Vector3 p1 = PathPoints[segIndex];
            Vector3 p2 = PathPoints[Math.Min(segCount, segIndex + 1)];
            Vector3 p3 = PathPoints[Math.Min(segCount, segIndex + 2)];

            Vector3 position = CatmullRom(p0, p1, p2, p3, segT);
            Vector3 tangent = CatmullRomTangent(p0, p1, p2, p3, segT);
            return (position, tangent);
        }

        private static Vector3 CatmullRom(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
        {
            float t2 = t * t;
            float t3 = t2 * t;
            return 0.5f * ((2 * p1) +
                (-p0 + p2) * t +
                (2 * p0 - 5 * p1 + 4 * p2 - p3) * t2 +
                (-p0 + 3 * p1 - 3 * p2 + p3) * t3);
        }

        private static Vector3 CatmullRomTangent(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
        {
            float t2 = t * t;
            return 0.5f * ((-p0 + p2) +
                (4 * p0 - 10 * p1 + 8 * p2 - 2 * p3) * t +
                (-3 * p0 + 9 * p1 - 9 * p2 + 3 * p3) * t2);
        }

        private float CalculatePathLength()
        {
            float length = 0;
            for (int i = 1; i < PathPoints.Count; i++)
                length += Vector3.Distance(PathPoints[i - 1], PathPoints[i]);
            return length;
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // StateDrivenCamera - 状态驱动相机
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 状态驱动相机：根据状态自动切换到对应的子相机。
    /// </summary>
    public class StateDrivenCamera : VirtualCamera
    {
        /// <summary>状态→相机映射。</summary>
        public Dictionary<string, VirtualCamera> StateCameraMap { get; set; } = new();

        /// <summary>当前状态。</summary>
        public string CurrentState { get; set; } = string.Empty;

        /// <summary>默认相机（无匹配状态时使用）。</summary>
        public VirtualCamera? DefaultCamera { get; set; }

        /// <summary>状态切换回调（由外部状态机驱动）。</summary>
        public Action<string>? OnStateChanged { get; set; }

        /// <summary>当前活跃的子相机。</summary>
        public VirtualCamera? ActiveChildCamera { get; private set; }

        public StateDrivenCamera() { }
        public StateDrivenCamera(string name, int priority = 10) : base(name, priority) { }

        /// <summary>设置当前状态。</summary>
        public void SetState(string state)
        {
            CurrentState = state;
            OnStateChanged?.Invoke(state);

            if (StateCameraMap.TryGetValue(state, out var cam))
            {
                ActiveChildCamera = cam;
            }
            else
            {
                ActiveChildCamera = DefaultCamera;
            }
        }

        public override void UpdateState(float deltaTime)
        {
            base.UpdateState(deltaTime);
            ActiveChildCamera?.UpdateState(deltaTime);

            if (ActiveChildCamera != null)
            {
                var (pos, rot, fov) = ActiveChildCamera.GetState();
                CurrentPosition = pos;
                CurrentRotation = rot;
                CurrentFOV = fov;
            }
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // ClearShotCamera - 智能最佳镜头
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 智能最佳镜头（ClearShot）：维护多个候选相机，
    /// 通过评分系统自动选择当前最佳构图。
    /// </summary>
    public class ClearShotCamera : VirtualCamera
    {
        /// <summary>候选相机列表。</summary>
        public List<VirtualCamera> CandidateCameras { get; set; } = new();

        /// <summary>评分回调（返回 0~1 的分数）。</summary>
        public Func<VirtualCamera, float>? ScoreCamera { get; set; }

        /// <summary>当前最佳相机。</summary>
        public VirtualCamera? BestCamera { get; private set; }

        /// <summary>最低切换阈值（分数差值大于此值才切换）。</summary>
        public float SwitchThreshold { get; set; } = 0.1f;

        /// <summary>当前最佳分数。</summary>
        private float _bestScore;

        /// <summary>评分刷新间隔（秒）。</summary>
        public double ScoreRefreshInterval { get; set; } = 0.5;

        /// <summary>上次评分时间。</summary>
        private double _lastScoreTime;

        public ClearShotCamera() { }
        public ClearShotCamera(string name, int priority = 10) : base(name, priority) { }

        public override void UpdateState(float deltaTime)
        {
            base.UpdateState(deltaTime);

            _lastScoreTime += deltaTime;
            if (_lastScoreTime >= ScoreRefreshInterval)
            {
                _lastScoreTime = 0;
                EvaluateBestCamera();
            }

            if (BestCamera != null)
            {
                BestCamera.UpdateState(deltaTime);
                var (pos, rot, fov) = BestCamera.GetState();
                CurrentPosition = pos;
                CurrentRotation = rot;
                CurrentFOV = fov;
            }
        }

        private void EvaluateBestCamera()
        {
            if (ScoreCamera == null || CandidateCameras.Count == 0) return;

            VirtualCamera? best = null;
            float bestScore = -1;

            foreach (var cam in CandidateCameras)
            {
                float score = ScoreCamera(cam);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = cam;
                }
            }

            if (best != null && best != BestCamera)
            {
                if (BestCamera == null || Math.Abs(bestScore - _bestScore) >= SwitchThreshold)
                {
                    BestCamera = best;
                    _bestScore = bestScore;
                }
            }
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // CameraShake - 相机震动
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>相机震动配置。</summary>
    public class CameraShake
    {
        /// <summary>震动名称。</summary>
        public string Name { get; set; } = "Shake";

        /// <summary>震动强度。</summary>
        public float Intensity { get; set; } = 1.0f;

        /// <summary>震动频率。</summary>
        public float Frequency { get; set; } = 10.0f;

        /// <summary>位置震动幅度。</summary>
        public Vector3 PositionAmplitude { get; set; } = new Vector3(0.1f, 0.1f, 0.1f);

        /// <summary>旋转震动幅度（欧拉角，度）。</summary>
        public Vector3 RotationAmplitude { get; set; } = new Vector3(2f, 2f, 2f);

        /// <summary>阻尼（震动衰减速度）。</summary>
        public float Damping { get; set; } = 1.0f;

        /// <summary>随机种子（确保可重现）。</summary>
        public int RandomSeed { get; set; }

        /// <summary>震动持续时间（秒，0=无限）。</summary>
        public float Duration { get; set; }

        /// <summary>当前已运行时间。</summary>
        public float ElapsedTime { get; private set; }

        /// <summary>震动是否活跃。</summary>
        public bool IsActive { get; private set; }

        private Random? _random;

        /// <summary>开始震动。</summary>
        public void Start()
        {
            ElapsedTime = 0;
            IsActive = true;
            _random = new Random(RandomSeed == 0 ? Environment.TickCount : RandomSeed);
        }

        /// <summary>停止震动。</summary>
        public void Stop()
        {
            IsActive = false;
        }

        /// <summary>评估震动偏移。</summary>
        public (Vector3 position, Quaternion rotation) Evaluate(float deltaTime)
        {
            if (!IsActive) return (Vector3.Zero, Quaternion.Identity);

            ElapsedTime += deltaTime;

            if (Duration > 0 && ElapsedTime >= Duration)
            {
                IsActive = false;
                return (Vector3.Zero, Quaternion.Identity);
            }

            float decay = MathF.Exp(-Damping * ElapsedTime);
            float time = ElapsedTime * Frequency;

            _random ??= new Random();

            // 伪随机震动
            float px = (float)(_random.NextDouble() * 2 - 1) * PositionAmplitude.X * decay;
            float py = (float)(_random.NextDouble() * 2 - 1) * PositionAmplitude.Y * decay;
            float pz = (float)(_random.NextDouble() * 2 - 1) * PositionAmplitude.Z * decay;

            float rx = (float)(_random.NextDouble() * 2 - 1) * RotationAmplitude.X * decay;
            float ry = (float)(_random.NextDouble() * 2 - 1) * RotationAmplitude.Y * decay;
            float rz = (float)(_random.NextDouble() * 2 - 1) * RotationAmplitude.Z * decay;

            return (
                new Vector3(px, py, pz),
                Quaternion.CreateFromYawPitchRoll(
                    rz * MathF.PI / 180f,
                    rx * MathF.PI / 180f,
                    ry * MathF.PI / 180f)
            );
        }

        /// <summary>重置震动状态。</summary>
        public void Reset()
        {
            ElapsedTime = 0;
            IsActive = false;
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // CameraNoise - 相机噪声
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>噪声类型。</summary>
    public enum CameraNoiseType
    {
        Basic,
        Perlin
    }

    /// <summary>
    /// 相机噪声：为相机提供持续的细微抖动效果。
    /// 支持基本噪声和 Perlin 噪声，支持多频率叠加。
    /// </summary>
    public class CameraNoise
    {
        /// <summary>噪声类型。</summary>
        public CameraNoiseType NoiseType { get; set; } = CameraNoiseType.Perlin;

        /// <summary>位置噪声强度。</summary>
        public float PositionIntensity { get; set; } = 0.02f;

        /// <summary>旋转噪声强度（度）。</summary>
        public float RotationIntensity { get; set; } = 0.5f;

        /// <summary>频率。</summary>
        public float Frequency { get; set; } = 1.0f;

        /// <summary>多频率叠加层。</summary>
        public List<NoiseLayer> Layers { get; set; } = new();

        /// <summary>噪声时间累加器。</summary>
        private float _time;

        /// <summary>简易 Perlin 噪声状态。</summary>
        private float _noiseSeed;

        public CameraNoise() { _noiseSeed = 0.5f; }

        /// <summary>评估噪声偏移。</summary>
        public (Vector3 position, Quaternion rotation) Evaluate(float deltaTime)
        {
            _time += deltaTime;

            float posAmp = PositionIntensity;
            float rotAmp = RotationIntensity;

            // 叠加多层噪声
            foreach (var layer in Layers)
            {
                posAmp += layer.PositionIntensity;
                rotAmp += layer.RotationIntensity;
            }

            Vector3 posNoise = new Vector3(
                PerlinNoise(_time * Frequency) * posAmp,
                PerlinNoise(_time * Frequency + 100) * posAmp,
                PerlinNoise(_time * Frequency + 200) * posAmp
            );

            Quaternion rotNoise = Quaternion.CreateFromYawPitchRoll(
                PerlinNoise(_time * Frequency + 300) * rotAmp * MathF.PI / 180f,
                PerlinNoise(_time * Frequency + 400) * rotAmp * MathF.PI / 180f,
                PerlinNoise(_time * Frequency + 500) * rotAmp * MathF.PI / 180f
            );

            return (posNoise, rotNoise);
        }

        /// <summary>简易 Perlin 噪声实现。</summary>
        private float PerlinNoise(float x)
        {
            int xi = (int)Math.Floor(x);
            float xf = x - xi;

            // 使用简单的哈希函数
            float hash0 = Hash(xi);
            float hash1 = Hash(xi + 1);

            // Smoothstep 插值
            float t = xf * xf * (3f - 2f * xf);
            return hash0 + (hash1 - hash0) * t;
        }

        private static float Hash(int n)
        {
            n = (n << 13) ^ n;
            return 1.0f - ((n * (n * n * 15731 + 789221) + 1376312589) & 0x7fffffff) / 1073741824.0f;
        }
    }

    /// <summary>噪声层：用于多频率叠加。</summary>
    public class NoiseLayer
    {
        /// <summary>位置强度。</summary>
        public float PositionIntensity { get; set; }

        /// <summary>旋转强度。</summary>
        public float RotationIntensity { get; set; }

        /// <summary>频率倍率。</summary>
        public float FrequencyMultiplier { get; set; } = 2.0f;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // ImpulseListener - 冲量监听器
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 冲量监听器：接收震动事件（冲量），支持频道过滤、叠加、衰减。
    /// </summary>
    public class ImpulseListener
    {
        /// <summary>频道名称（空字符串=所有频道）。</summary>
        public string Channel { get; set; } = string.Empty;

        /// <summary>是否启用。</summary>
        public bool IsEnabled { get; set; } = true;

        /// <summary>冲量增益。</summary>
        public float Gain { get; set; } = 1.0f;

        /// <summary>衰减模式。</summary>
        public bool UseDecay { get; set; } = true;

        /// <summary>活跃的冲量列表。</summary>
        private readonly List<ActiveImpulse> _activeImpulses = new();

        /// <summary>当前总冲量偏移。</summary>
        public Vector3 CurrentPositionOffset { get; private set; }
        public Quaternion CurrentRotationOffset { get; private set; } = Quaternion.Identity;

        /// <summary>接收冲量事件。</summary>
        public void ReceiveImpulse(CameraShake shake, string channel = "")
        {
            if (!IsEnabled) return;
            if (!string.IsNullOrEmpty(Channel) && Channel != channel) return;

            _activeImpulses.Add(new ActiveImpulse
            {
                Shake = shake,
                RemainingTime = shake.Duration,
                TotalTime = shake.Duration
            });
            shake.Start();
        }

        /// <summary>每帧更新。</summary>
        public void Update(float deltaTime)
        {
            Vector3 totalPos = Vector3.Zero;
            Vector3 totalRotEuler = Vector3.Zero;

            for (int i = _activeImpulses.Count - 1; i >= 0; i--)
            {
                var impulse = _activeImpulses[i];
                var (pos, rot) = impulse.Shake.Evaluate(deltaTime);

                totalPos += pos * Gain;
                // 简化旋转叠加
                totalRotEuler += new Vector3(
                    rot.X * Gain, rot.Y * Gain, rot.Z * Gain);

                if (!impulse.Shake.IsActive)
                {
                    _activeImpulses.RemoveAt(i);
                }
            }

            CurrentPositionOffset = totalPos;
            CurrentRotationOffset = Quaternion.CreateFromYawPitchRoll(
                totalRotEuler.X, totalRotEuler.Y, totalRotEuler.Z);
        }

        private class ActiveImpulse
        {
            public CameraShake Shake { get; set; } = null!;
            public float RemainingTime { get; set; }
            public float TotalTime { get; set; }
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // CinemachineBrain - 大脑
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Cinemachine 大脑：管理所有虚拟相机，根据优先级自动切换，
    /// 处理混合过渡、无效回退、用户控制覆盖。
    /// </summary>
    public class CinemachineBrain
    {
        /// <summary>所有注册的虚拟相机。</summary>
        private readonly List<VirtualCamera> _cameras = new();

        /// <summary>当前活跃的混合过渡。</summary>
        private CinemachineBlend? _activeBlend;

        /// <summary>当前输出相机。</summary>
        public VirtualCamera? ActiveCamera { get; private set; }

        /// <summary>默认回退相机。</summary>
        public VirtualCamera? FallbackCamera { get; set; }

        /// <summary>冲量监听器。</summary>
        public ImpulseListener ImpulseListener { get; set; } = new();

        /// <summary>用户控制覆盖状态。</summary>
        public bool IsUserOverride { get; private set; }

        /// <summary>用户覆盖恢复延迟（秒）。</summary>
        public float UserOverrideRecoveryDelay { get; set; } = 1.0f;

        /// <summary>用户覆盖恢复计时器。</summary>
        private float _userOverrideTimer;

        /// <summary>用户覆盖前的活跃相机。</summary>
        private VirtualCamera? _preOverrideCamera;

        /// <summary>输出相机更新回调。</summary>
        public Action<Vector3, Quaternion, float>? ApplyToOutputCamera { get; set; }

        /// <summary>相机切换事件。</summary>
        public event Action<VirtualCamera?, VirtualCamera?>? OnCameraSwitch;

        /// <summary>获取所有相机（只读）。</summary>
        public IReadOnlyList<VirtualCamera> Cameras => _cameras;

        public CinemachineBrain() { }

        /// <summary>注册虚拟相机。</summary>
        public void AddCamera(VirtualCamera camera)
        {
            if (camera == null) throw new ArgumentNullException(nameof(camera));
            _cameras.Add(camera);
            _cameras.Sort((a, b) => b.Priority.CompareTo(a.Priority));
        }

        /// <summary>移除虚拟相机。</summary>
        public bool RemoveCamera(VirtualCamera camera)
        {
            if (_cameras.Remove(camera))
            {
                if (ActiveCamera == camera)
                    ActiveCamera = null;
                return true;
            }
            return false;
        }

        /// <summary>触发冲量事件（震动）。</summary>
        public void TriggerImpulse(CameraShake shake, string channel = "")
        {
            ImpulseListener.ReceiveImpulse(shake, channel);
        }

        /// <summary>开始用户控制覆盖。</summary>
        public void BeginUserOverride()
        {
            if (IsUserOverride) return;
            IsUserOverride = true;
            _preOverrideCamera = ActiveCamera;
            _userOverrideTimer = 0;
        }

        /// <summary>结束用户控制覆盖，恢复之前的相机。</summary>
        public void EndUserOverride()
        {
            if (!IsUserOverride) return;
            IsUserOverride = false;
            _userOverrideTimer = 0;
        }

        /// <summary>每帧更新大脑。</summary>
        public void Update(float deltaTime)
        {
            // 更新冲量监听器
            ImpulseListener.Update(deltaTime);

            // 用户控制覆盖恢复计时
            if (IsUserOverride)
            {
                _userOverrideTimer += deltaTime;
                return; // 用户覆盖时不更新虚拟相机
            }

            // 确定当前最高优先级的活跃相机
            VirtualCamera? bestCamera = GetHighestPriorityActiveCamera();

            // 无效回退
            if (bestCamera == null && FallbackCamera != null)
            {
                bestCamera = FallbackCamera;
            }

            // 检查是否需要切换
            if (bestCamera != null && bestCamera != ActiveCamera)
            {
                StartBlend(ActiveCamera, bestCamera);
            }

            // 更新混合过渡
            if (_activeBlend != null && !_activeBlend.IsComplete)
            {
                _activeBlend.Update(deltaTime);

                // 混合两台相机的状态
                if (_activeBlend.SourceCamera != null && _activeBlend.TargetCamera != null)
                {
                    var src = _activeBlend.SourceCamera.GetState();
                    var dst = _activeBlend.TargetCamera.GetState();
                    float weight = _activeBlend.GetBlendWeight();

                    Vector3 pos = Vector3.Lerp(src.position, dst.position, weight);
                    Quaternion rot = Quaternion.Slerp(src.rotation, dst.rotation, weight);
                    float fov = src.fov + (dst.fov - src.fov) * weight;

                    // 应用冲量偏移
                    pos += ImpulseListener.CurrentPositionOffset;

                    ApplyToOutputCamera?.Invoke(pos, rot, fov);
                }
            }
            else if (ActiveCamera != null)
            {
                // 无混合，直接输出活跃相机
                ActiveCamera.UpdateState(deltaTime);

                var (pos, rot, fov) = ActiveCamera.GetState();
                pos += ImpulseListener.CurrentPositionOffset;

                ApplyToOutputCamera?.Invoke(pos, rot, fov);
            }

            // 混合完成后清理
            if (_activeBlend != null && _activeBlend.IsComplete)
            {
                ActiveCamera = _activeBlend.TargetCamera;
                _activeBlend = null;
            }
        }

        /// <summary>获取当前最高优先级的活跃相机。</summary>
        private VirtualCamera? GetHighestPriorityActiveCamera()
        {
            foreach (var cam in _cameras)
            {
                if (cam.IsActive)
                    return cam;
            }
            return null;
        }

        /// <summary>启动混合过渡。</summary>
        private void StartBlend(VirtualCamera? from, VirtualCamera? to)
        {
            if (to == null) return;

            var oldCamera = ActiveCamera;
            ActiveCamera = to;

            if (to.BlendTime <= 0 || from == null)
            {
                // 无混合（硬切）
                _activeBlend = null;
            }
            else
            {
                _activeBlend = new CinemachineBlend
                {
                    SourceCamera = from,
                    TargetCamera = to,
                    Duration = to.BlendTime,
                    Style = to.BlendStyle,
                    CustomCurve = to.CustomBlendCurve
                };
            }

            OnCameraSwitch?.Invoke(oldCamera, to);
        }
    }
}
