using System;
using System.Collections.Generic;
using System.Numerics;

namespace YamaEngine.Cinematics
{
    // ============================================================================================
    //  CinematicCameraController.cs
    //
    //  yama引擎 —— 电影相机控制器 (Cinematic Camera Controller)
    //
    //  本文件实现面向电影制作的完整相机控制系统，与 SequencerSystem.cs 协同工作。
    //  提供四种相机模式及多种后处理效果：
    //
    //  主要组成：
    //    1. ICinematicCamera         —— 电影相机接口
    //    2. RailCamera               —— 轨道相机（沿路径运动、围绕目标旋转）
    //    3. KeyframeCamera          —— 关键帧相机（从序列器 Keyframe 驱动）
    //    4. CameraShakeBase          —— 相机抖动基类
    //       - WalkBobShake           —— 行走摇晃
    //       - LookNoiseShake         —— 视线噪声抖动
    //    5. CameraShakeController    —— 抖动混合控制器
    //    6. CameraPostProcess        —— 后处理效果容器
    //       - DepthOfFieldEffect     —— 景深 (DoF)
    //       - MotionBlurEffect      —— 运动模糊
    //       - LensFlareEffect       —— 镜头光晕
    //    7. CinematicCameraSystem    —— 电影相机系统（统一管理、评估、输出）
    // ============================================================================================

    // ──────────────────────────────────────────────────────────────────────────
    // 枚举定义
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>电影相机模式。</summary>
    public enum CinematicCameraMode
    {
        /// <summary>轨道相机：沿预设路径运动或围绕目标旋转。</summary>
        Rail,
        /// <summary>关键帧相机：完全由序列器关键帧驱动。</summary>
        Keyframe,
        /// <summary>跟随相机：平滑跟随目标。</summary>
        Follow,
        /// <summary>自由相机：手动控制或脚本控制。</summary>
        Free
    }

    /// <summary>轨道相机运动模式。</summary>
    public enum RailCameraMotionMode
    {
        /// <summary>沿路径前进（Dolly/Tracking）。</summary>
        PathFollow,
        /// <summary>围绕目标旋转（Orbit）。</summary>
        Orbit,
        /// <summary>起重机升降（Crane）。</summary>
        Crane,
        /// <summary>组合：路径 + 高度变化。</summary>
        Combined
    }

    /// <summary>景散模式。</summary>
    public enum DepthOfFieldMode
    {
        /// <summary>禁用。</summary>
        Off,
        /// <summary>手动指定焦距。</summary>
        Manual,
        /// <summary>基于相机对焦目标自动计算。</summary>
        FocusTracking
    }

    /// <summary>运动模糊模式。</summary>
    public enum MotionBlurMode
    {
        /// <summary>禁用。</summary>
        Off,
        /// <summary>基于相机运动速度。</summary>
        VelocityBased,
        /// <summary>基于物体运动。</summary>
        ObjectBased,
        /// <summary>组合。</summary>
        Combined
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 电影相机接口
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 电影相机接口。所有电影相机必须实现此接口，
    /// 提供统一的 GetState 和 Update 方法。
    /// </summary>
    public interface ICinematicCamera
    {
        /// <summary>相机模式。</summary>
        CinematicCameraMode Mode { get; }

        /// <summary>获取当前相机状态。</summary>
        CinematicCameraState GetState();

        /// <summary>每帧更新。</summary>
        void Update(float deltaTime);

        /// <summary>重置相机状态。</summary>
        void Reset();
    }

    /// <summary>
    /// 电影相机瞬时状态。包含位置、朝向、FOV 及后处理参数。
    /// </summary>
    public struct CinematicCameraState
    {
        /// <summary>世界空间位置。</summary>
        public Vector3 Position;

        /// <summary>世界空间朝向（前方向量）。</summary>
        public Vector3 Forward;

        /// <summary>世界空间上方向量。</summary>
        public Vector3 Up;

        /// <summary>视场角（度）。</summary>
        public float FieldOfView;

        /// <summary>焦距（毫米，用于景深计算）。</summary>
        public float FocalLength;

        /// <summary>光圈值（f-stop），影响景深。</summary>
        public float Aperture;

        /// <summary>当前抖动偏移。</summary>
        public Vector3 ShakeOffset;

        /// <summary>当前抖动旋转。</summary>
        public Quaternion ShakeRotation;

        /// <summary>计算视图矩阵。</summary>
        public Matrix4x4 GetViewMatrix()
        {
            Vector3 target = Position + Forward;
            return Matrix4x4.CreateLookAt(Position, target, Up);
        }

        /// <summary>计算投影矩阵。</summary>
        public Matrix4x4 GetProjectionMatrix(float aspectRatio, float nearPlane, float farPlane)
        {
            float fovRad = MathF.PI / 180f * FieldOfView;
            return Matrix4x4.CreatePerspectiveFieldOfView(fovRad, aspectRatio, nearPlane, farPlane);
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 轨道相机 (Rail Camera)
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 轨道相机。沿预设路径运动或围绕目标旋转。
    /// 支持四种运动模式：路径跟随(PathFollow)、环绕(Orbit)、
    /// 起重机升降(Crane)和组合模式(Combined)。
    /// </summary>
    public class RailCamera : ICinematicCamera
    {
        public CinematicCameraMode Mode => CinematicCameraMode.Rail;

        // ── 基础参数 ──

        /// <summary>相机当前位置。</summary>
        public Vector3 Position { get; set; }

        /// <summary>相机当前朝向。</summary>
        public Vector3 Forward { get; set; }

        /// <summary>相机当前上方向。</summary>
        public Vector3 Up { get; set; } = Vector3.UnitY;

        /// <summary>视场角（度）。</summary>
        public float FieldOfView { get; set; } = 60f;

        /// <summary>焦距（毫米）。</summary>
        public float FocalLength { get; set; } = 35f;

        /// <summary>光圈值。</summary>
        public float Aperture { get; set; } = 2.8f;

        // ── 运动模式参数 ──

        /// <summary>运动模式。</summary>
        public RailCameraMotionMode MotionMode { get; set; } = RailCameraMotionMode.PathFollow;

        /// <summary>路径点列表（用于 PathFollow 和 Combined 模式）。</summary>
        public readonly List<Vector3> PathPoints = new();

        /// <summary>路径速度（单位/秒）。</summary>
        public float PathSpeed { get; set; } = 5f;

        /// <summary>路径是否循环。</summary>
        public bool PathLoop { get; set; }

        // ── Orbit 参数 ──

        /// <summary>环绕中心目标位置。</summary>
        public Vector3 OrbitTarget { get; set; }

        /// <summary>轨道半径。</summary>
        public float OrbitRadius { get; set; } = 10f;

        /// <summary>轨道高度角（弧度，0=水平，PI/2=正上方）。</summary>
        public float OrbitPitch { get; set; } = 0.3f;

        /// <summary>当前轨道偏航角（弧度）。</summary>
        public float OrbitYaw { get; set; }

        /// <summary>轨道旋转速度（弧度/秒）。</summary>
        public float OrbitSpeed { get; set; } = 0.5f;

        /// <summary>是否始终注视目标。</summary>
        public bool AlwaysLookAtTarget { get; set; } = true;

        // ── Crane 参数 ──

        /// <summary>起重机起始高度。</summary>
        public float CraneStartHeight { get; set; }

        /// <summary>起重机结束高度。</summary>
        public float CraneEndHeight { get; set; } = 20f;

        /// <summary>起重机升降速度（单位/秒）。</summary>
        public float CraneSpeed { get; set; } = 2f;

        // ── 运行时状态 ──

        private float _pathProgress;
        private float _craneHeight;
        private bool _craneRising = true;

        /// <summary>设置路径点。</summary>
        public void SetPath(IReadOnlyList<Vector3> points)
        {
            PathPoints.Clear();
            foreach (var p in points)
                PathPoints.Add(p);
        }

        public CinematicCameraState GetState()
        {
            return new CinematicCameraState
            {
                Position = Position,
                Forward = Forward,
                Up = Up,
                FieldOfView = FieldOfView,
                FocalLength = FocalLength,
                Aperture = Aperture,
                ShakeOffset = Vector3.Zero,
                ShakeRotation = Quaternion.Identity
            };
        }

        public void Update(float deltaTime)
        {
            switch (MotionMode)
            {
                case RailCameraMotionMode.PathFollow:
                    UpdatePathFollow(deltaTime);
                    break;

                case RailCameraMotionMode.Orbit:
                    UpdateOrbit(deltaTime);
                    break;

                case RailCameraMotionMode.Crane:
                    UpdateCrane(deltaTime);
                    break;

                case RailCameraMotionMode.Combined:
                    UpdateCombined(deltaTime);
                    break;
            }

            // 注视目标
            if (AlwaysLookAtTarget && MotionMode != RailCameraMotionMode.PathFollow)
            {
                Vector3 target = MotionMode == RailCameraMotionMode.Orbit
                    ? OrbitTarget
                    : Position + Forward;
                Forward = Vector3.Normalize(target - Position);
            }
        }

        private void UpdatePathFollow(float deltaTime)
        {
            if (PathPoints.Count < 2) return;

            float totalLength = CalculatePathLength();
            if (totalLength < 1e-4f) return;

            _pathProgress += (deltaTime * PathSpeed) / totalLength;

            if (PathLoop)
            {
                _pathProgress = _pathProgress % 1f;
            }
            else
            {
                _pathProgress = Math.Clamp(_pathProgress, 0f, 1f);
            }

            // 沿路径插值位置
            float targetDist = _pathProgress * totalLength;
            float accumDist = 0f;

            for (int i = 0; i < PathPoints.Count - 1; i++)
            {
                float segLen = Vector3.Distance(PathPoints[i], PathPoints[i + 1]);
                if (accumDist + segLen >= targetDist)
                {
                    float segT = segLen > 1e-5f
                        ? (targetDist - accumDist) / segLen
                        : 0f;
                    Position = Vector3.Lerp(PathPoints[i], PathPoints[i + 1], segT);
                    Forward = Vector3.Normalize(PathPoints[i + 1] - PathPoints[i]);
                    break;
                }
                accumDist += segLen;
            }
        }

        private void UpdateOrbit(float deltaTime)
        {
            OrbitYaw += deltaTime * OrbitSpeed;

            float x = OrbitRadius * MathF.Cos(OrbitYaw) * MathF.Cos(OrbitPitch);
            float z = OrbitRadius * MathF.Sin(OrbitYaw) * MathF.Cos(OrbitPitch);
            float y = OrbitRadius * MathF.Sin(OrbitPitch);

            Position = OrbitTarget + new Vector3(x, y, z);
        }

        private void UpdateCrane(float deltaTime)
        {
            if (_craneRising)
            {
                _craneHeight += deltaTime * CraneSpeed;
                if (_craneHeight >= CraneEndHeight)
                {
                    _craneHeight = CraneEndHeight;
                    _craneRising = false;
                }
            }
            else
            {
                _craneHeight -= deltaTime * CraneSpeed;
                if (_craneHeight <= CraneStartHeight)
                {
                    _craneHeight = CraneStartHeight;
                    _craneRising = true;
                }
            }

            Position = new Vector3(Position.X, _craneHeight, Position.Z);
        }

        private void UpdateCombined(float deltaTime)
        {
            // 先更新路径跟随位置（XZ 平面）
            if (PathPoints.Count >= 2)
            {
                float totalLength = CalculatePathLength();
                if (totalLength > 1e-4f)
                {
                    _pathProgress += (deltaTime * PathSpeed) / totalLength;
                    if (PathLoop) _pathProgress = _pathProgress % 1f;
                    else _pathProgress = Math.Clamp(_pathProgress, 0f, 1f);

                    float targetDist = _pathProgress * totalLength;
                    float accumDist = 0f;

                    for (int i = 0; i < PathPoints.Count - 1; i++)
                    {
                        float segLen = Vector3.Distance(PathPoints[i], PathPoints[i + 1]);
                        if (accumDist + segLen >= targetDist)
                        {
                            float segT = segLen > 1e-5f
                                ? (targetDist - accumDist) / segLen
                                : 0f;
                            Vector3 pathPos = Vector3.Lerp(PathPoints[i], PathPoints[i + 1], segT);
                            Position = new Vector3(pathPos.X, Position.Y, pathPos.Z);
                            Forward = Vector3.Normalize(PathPoints[i + 1] - PathPoints[i]);
                            break;
                        }
                        accumDist += segLen;
                    }
                }
            }

            // 叠加起重机升降（Y 轴）
            UpdateCrane(deltaTime);
        }

        private float CalculatePathLength()
        {
            float len = 0f;
            for (int i = 0; i < PathPoints.Count - 1; i++)
                len += Vector3.Distance(PathPoints[i], PathPoints[i + 1]);
            return len;
        }

        public void Reset()
        {
            _pathProgress = 0f;
            _craneHeight = CraneStartHeight;
            _craneRising = true;
            OrbitYaw = 0f;
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 关键帧相机 (Keyframe Camera)
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 关键帧相机。完全由序列器的关键帧通道驱动位置、朝向、FOV。
    /// 与 CinematicSequence 的 CameraTrack 协同工作。
    /// </summary>
    public class KeyframeCamera : ICinematicCamera
    {
        public CinematicCameraMode Mode => CinematicCameraMode.Keyframe;

        /// <summary>相机当前位置。</summary>
        public Vector3 Position { get; private set; }

        /// <summary>相机当前朝向。</summary>
        public Vector3 Forward { get; private set; } = Vector3.UnitZ;

        /// <summary>相机当前上方向。</summary>
        public Vector3 Up { get; private set; } = Vector3.UnitY;

        /// <summary>当前视场角（度）。</summary>
        public float FieldOfView { get; private set; } = 60f;

        /// <summary>当前焦距。</summary>
        public float FocalLength { get; private set; } = 35f;

        /// <summary>当前光圈。</summary>
        public float Aperture { get; private set; } = 2.8f;

        // ── 关键帧通道 ──

        /// <summary>位置通道。</summary>
        public CinematicChannel<Vector3> PositionChannel { get; } = new()
        {
            Interpolator = Vector3ChannelInterpolator.Interpolate
        };

        /// <summary>朝向通道（以 Euler 角 XYZ 存储，内部转换为 Forward/Up）。</summary>
        public CinematicChannel<Vector3> LookAtChannel { get; } = new()
        {
            Interpolator = Vector3ChannelInterpolator.Interpolate
        };

        /// <summary>FOV 通道。</summary>
        public CinematicChannel<float> FovChannel { get; } = new()
        {
            Interpolator = FloatChannelInterpolator.Interpolate
        };

        /// <summary>焦距通道。</summary>
        public CinematicChannel<float> FocalLengthChannel { get; } = new()
        {
            Interpolator = FloatChannelInterpolator.Interpolate
        };

        /// <summary>光圈通道。</summary>
        public CinematicChannel<float> ApertureChannel { get; } = new()
        {
            Interpolator = FloatChannelInterpolator.Interpolate
        };

        // ── 注视目标 ──

        /// <summary>是否始终注视目标（优先于 LookAt 通道）。</summary>
        public bool AlwaysLookAtTarget { get; set; }

        /// <summary>注视目标位置通道。</summary>
        public CinematicChannel<Vector3> TargetChannel { get; } = new()
        {
            Interpolator = Vector3ChannelInterpolator.Interpolate
        };

        // ── 运行时状态 ──

        private float _lastEvalTime = -1f;

        /// <summary>
        /// 从序列器时间评估相机状态。由 CinematicCameraSystem 在每帧调用。
        /// </summary>
        /// <param name="time">序列时间（秒）。</param>
        public void EvaluateFromSequencer(float time)
        {
            if (time == _lastEvalTime) return;
            _lastEvalTime = time;

            // 位置
            if (!PositionChannel.IsEmpty)
                Position = PositionChannel.EvaluateAtTime(time);

            // FOV
            if (!FovChannel.IsEmpty)
                FieldOfView = FovChannel.EvaluateAtTime(time);

            // 焦距
            if (!FocalLengthChannel.IsEmpty)
                FocalLength = FocalLengthChannel.EvaluateAtTime(time);

            // 光圈
            if (!ApertureChannel.IsEmpty)
                Aperture = ApertureChannel.EvaluateAtTime(time);

            // 朝向
            if (AlwaysLookAtTarget && !TargetChannel.IsEmpty)
            {
                Vector3 target = TargetChannel.EvaluateAtTime(time);
                Forward = Vector3.Normalize(target - Position);
            }
            else if (!LookAtChannel.IsEmpty)
            {
                Vector3 euler = LookAtChannel.EvaluateAtTime(time);
                // 从 Euler 角构建 Forward 向量
                Quaternion rot = Quaternion.CreateFromYawPitchRoll(
                    euler.Y, euler.X, euler.Z);
                Forward = Vector3.Transform(Vector3.UnitZ, rot);
                Up = Vector3.Transform(Vector3.UnitY, rot);
            }
        }

        public CinematicCameraState GetState()
        {
            return new CinematicCameraState
            {
                Position = Position,
                Forward = Forward,
                Up = Up,
                FieldOfView = FieldOfView,
                FocalLength = FocalLength,
                Aperture = Aperture,
                ShakeOffset = Vector3.Zero,
                ShakeRotation = Quaternion.Identity
            };
        }

        public void Update(float deltaTime)
        {
            // 关键帧相机由 EvaluateFromSequencer 驱动，Update 为空操作
        }

        public void Reset()
        {
            _lastEvalTime = -1f;
            Position = Vector3.Zero;
            Forward = Vector3.UnitZ;
            Up = Vector3.UnitY;
            FieldOfView = 60f;
            FocalLength = 35f;
            Aperture = 2.8f;
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 相机抖动系统 (Camera Shake System)
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 相机抖动基类。定义抖动的生命周期和衰减曲线。
    /// </summary>
    public abstract class CameraShakeBase
    {
        /// <summary>抖动名称。</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>抖动持续时间（秒，0=无限）。</summary>
        public float Duration { get; set; } = 1f;

        /// <summary>已运行时间。</summary>
        protected float ElapsedTime { get; private set; }

        /// <summary>是否正在播放。</summary>
        public bool IsPlaying { get; private set; }

        /// <summary>是否已完成（非无限时长时）。</summary>
        public bool IsFinished => Duration > 0f && ElapsedTime >= Duration;

        /// <summary>抖动强度倍率。</summary>
        public float IntensityScale { get; set; } = 1f;

        /// <summary>抖动优先级（数值越大优先级越高）。</summary>
        public int Priority { get; set; }

        /// <summary>是否自动衰减（按生命周期淡出）。</summary>
        public bool AutoAttenuate { get; set; } = true;

        /// <summary>衰减曲线形状（0=线性, 1=指数衰减）。</summary>
        public float AttenuationCurve { get; set; } = 0.5f;

        /// <summary>
        /// 计算当前抖动偏移和旋转。子类实现具体的噪声算法。
        /// </summary>
        /// <param name="deltaTime">帧间隔。</param>
        /// <param name="offset">输出位置偏移。</param>
        /// <param name="rotation">输出旋转偏移。</param>
        public abstract void Sample(float deltaTime, out Vector3 offset, out Quaternion rotation);

        /// <summary>开始播放。</summary>
        public virtual void Play()
        {
            ElapsedTime = 0f;
            IsPlaying = true;
        }

        /// <summary>停止播放。</summary>
        public virtual void Stop()
        {
            IsPlaying = false;
        }

        /// <summary>每帧更新内部计时器。</summary>
        protected void TickTimer(float deltaTime)
        {
            ElapsedTime += deltaTime;
            if (Duration > 0f && ElapsedTime >= Duration)
            {
                IsPlaying = false;
            }
        }

        /// <summary>计算当前衰减系数 [0, 1]。</summary>
        protected float GetAttenuation()
        {
            if (!AutoAttenuate || Duration <= 0f) return 1f;
            float t = ElapsedTime / Duration;
            // 混合线性和指数衰减
            float linear = 1f - t;
            float exponential = MathF.Exp(-3f * t);
            return linear * (1f - AttenuationCurve) + exponential * AttenuationCurve;
        }
    }

    /// <summary>
    /// 行走摇晃抖动。模拟行走/奔跑时的相机自然上下左右晃动。
    /// </summary>
    public class WalkBobShake : CameraShakeBase
    {
        /// <summary>摇晃频率（Hz）。</summary>
        public float BobFrequency { get; set; } = 6f;

        /// <summary>垂直摇晃幅度。</summary>
        public float VerticalAmplitude { get; set; } = 0.05f;

        /// <summary>水平摇晃幅度。</summary>
        public float HorizontalAmplitude { get; set; } = 0.03f;

        /// <summary>滚动摇晃幅度（弧度）。</summary>
        public float RollAmplitude { get; set; } = 0.01f;

        /// <summary>速度因子（由外部传入移动速度来调节频率和幅度）。</summary>
        public float SpeedFactor { get; set; } = 1f;

        private float _phase;

        public WalkBobShake()
        {
            Name = "WalkBob";
            Duration = 0f; // 无限持续，手动停止
        }

        public override void Sample(float deltaTime, out Vector3 offset, out Quaternion rotation)
        {
            TickTimer(deltaTime);

            float freq = BobFrequency * SpeedFactor;
            _phase += deltaTime * freq * MathF.PI * 2f;

            float attenuation = GetAttenuation() * IntensityScale;

            float x = MathF.Sin(_phase) * HorizontalAmplitude * attenuation;
            float y = MathF.Abs(MathF.Cos(_phase)) * VerticalAmplitude * attenuation;
            float roll = MathF.Sin(_phase * 0.5f) * RollAmplitude * attenuation;

            offset = new Vector3(x, y, 0f);
            rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, roll);
        }

        public override void Reset()
        {
            base.Stop();
            _phase = 0f;
            ElapsedTime = 0f;
        }
    }

    /// <summary>
    /// 视线噪声抖动。模拟手持摄影机的微弱随机晃动。
    /// 使用基于时间的伪随机噪声生成自然的不规则运动。
    /// </summary>
    public class LookNoiseShake : CameraShakeBase
    {
        /// <summary>噪声频率。</summary>
        public float NoiseFrequency { get; set; } = 1.5f;

        /// <summary>位置噪声幅度。</summary>
        public float PositionNoiseAmplitude { get; set; } = 0.02f;

        /// <summary>旋转噪声幅度（弧度）。</summary>
        public float RotationNoiseAmplitude { get; set; } = 0.005f;

        /// <summary>噪声种子（改变种子产生不同噪声模式）。</summary>
        public int NoiseSeed { get; set; } = 42;

        private float _time;
        private Random? _random;

        public LookNoiseShake()
        {
            Name = "LookNoise";
            Duration = 0f; // 无限持续
        }

        /// <summary>简易值噪声函数（基于种子的伪随机）。</summary>
        private float ValueNoise(float t, int seed)
        {
            _random ??= new Random(NoiseSeed + seed);
            int i = (int)MathF.Floor(t);
            float f = t - i;
            // 平滑插值
            float u = f * f * (3f - 2f * f);

            uint hashA = (uint)new Random(NoiseSeed + seed + i).Next();
            uint hashB = (uint)new Random(NoiseSeed + seed + i + 1).Next();
            float a = (hashA % 10000) / 10000f * 2f - 1f;
            float b = (hashB % 10000) / 10000f * 2f - 1f;

            return a + (b - a) * u;
        }

        public override void Sample(float deltaTime, out Vector3 offset, out Quaternion rotation)
        {
            TickTimer(deltaTime);

            _time += deltaTime;
            float attenuation = GetAttenuation() * IntensityScale;

            float nx = ValueNoise(_time * NoiseFrequency, 0) * PositionNoiseAmplitude * attenuation;
            float ny = ValueNoise(_time * NoiseFrequency + 100f, 1) * PositionNoiseAmplitude * attenuation;
            float nz = ValueNoise(_time * NoiseFrequency + 200f, 2) * PositionNoiseAmplitude * attenuation;

            float pitchNoise = ValueNoise(_time * NoiseFrequency * 0.7f + 300f, 3) * RotationNoiseAmplitude * attenuation;
            float yawNoise = ValueNoise(_time * NoiseFrequency * 0.7f + 400f, 4) * RotationNoiseAmplitude * attenuation;
            float rollNoise = ValueNoise(_time * NoiseFrequency * 0.5f + 500f, 5) * RotationNoiseAmplitude * attenuation * 0.5f;

            offset = new Vector3(nx, ny, nz);
            rotation = Quaternion.CreateFromYawPitchRoll(yawNoise, pitchNoise, rollNoise);
        }

        public override void Reset()
        {
            base.Stop();
            _time = 0f;
            ElapsedTime = 0f;
            _random = null;
        }
    }

    /// <summary>
    /// 相机抖动控制器。管理多个抖动源的混合，按优先级排列，
    /// 输出最终的偏移和旋转叠加结果。
    /// </summary>
    public class CameraShakeController
    {
        /// <summary>当前活跃的抖动列表。</summary>
        private readonly List<CameraShakeBase> _activeShakes = new();

        /// <summary>最终混合的抖动偏移。</summary>
        public Vector3 CurrentOffset { get; private set; }

        /// <summary>最终混合的抖动旋转。</summary>
        public Quaternion CurrentRotation { get; private set; } = Quaternion.Identity;

        /// <summary>全局抖动强度倍率。</summary>
        public float GlobalIntensityScale { get; set; } = 1f;

        /// <summary>最大同时活跃抖动数。</summary>
        public int MaxActiveShakes { get; set; } = 4;

        /// <summary>添加抖动源。</summary>
        public void AddShake(CameraShakeBase shake)
        {
            if (_activeShakes.Count >= MaxActiveShakes)
            {
                // 移除优先级最低的
                int minIdx = 0;
                for (int i = 1; i < _activeShakes.Count; i++)
                {
                    if (_activeShakes[i].Priority < _activeShakes[minIdx].Priority)
                        minIdx = i;
                }
                if (shake.Priority > _activeShakes[minIdx].Priority)
                    _activeShakes.RemoveAt(minIdx);
                else
                    return; // 新抖动优先级太低，忽略
            }

            shake.Play();
            _activeShakes.Add(shake);
        }

        /// <summary>移除指定抖动。</summary>
        public bool RemoveShake(CameraShakeBase shake)
        {
            shake.Stop();
            return _activeShakes.Remove(shake);
        }

        /// <summary>移除所有抖动。</summary>
        public void ClearAllShakes()
        {
            foreach (var shake in _activeShakes)
                shake.Stop();
            _activeShakes.Clear();
            CurrentOffset = Vector3.Zero;
            CurrentRotation = Quaternion.Identity;
        }

        /// <summary>每帧更新。采样所有活跃抖动并混合输出。</summary>
        public void Update(float deltaTime)
        {
            Vector3 totalOffset = Vector3.Zero;
            Quaternion totalRotation = Quaternion.Identity;

            // 从后向前遍历，移除已完成的抖动
            for (int i = _activeShakes.Count - 1; i >= 0; i--)
            {
                var shake = _activeShakes[i];

                if (shake.IsFinished)
                {
                    _activeShakes.RemoveAt(i);
                    continue;
                }

                shake.Sample(deltaTime, out Vector3 offset, out Quaternion rotation);

                // 叠加偏移（位置直接相加）
                totalOffset += offset;

                // 叠加旋转（四元数乘法组合）
                totalRotation = Quaternion.Multiply(totalRotation, rotation);
            }

            CurrentOffset = totalOffset * GlobalIntensityScale;
            CurrentRotation = totalRotation;
        }

        /// <summary>获取活跃抖动数量。</summary>
        public int ActiveShakeCount => _activeShakes.Count;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 后处理效果
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 景深效果 (Depth of Field)。
    /// 模拟真实镜头的焦点/离焦区域，产生背景虚化或前景虚化。
    /// </summary>
    public class DepthOfFieldEffect
    {
        /// <summary>景深模式。</summary>
        public DepthOfFieldMode Mode { get; set; } = DepthOfFieldMode.Off;

        /// <summary>对焦距离（世界单位，Manual 模式）。</summary>
        public float FocusDistance { get; set; } = 10f;

        /// <summary>焦点目标位置（FocusTracking 模式）。</summary>
        public Vector3 FocusTarget { get; set; }

        /// <summary>焦点范围（在此范围内完全清晰）。</summary>
        public float FocusRange { get; set; } = 2f;

        /// <summary>近焦散距离（从此距离开始虚化）。</summary>
        public float NearFocusDistance { get; set; } = 1f;

        /// <summary>远焦散距离（超过此距离完全虚化）。</summary>
        public float FarFocusDistance { get; set; } = 100f;

        /// <summary>最大虚化强度（0=无虚化, 1=完全虚化）。</summary>
        public float MaxBokehIntensity { get; set; } = 0.8f;

        /// <summary>散景形状（0=圆形, 1=六边形, 2=八边形）。</summary>
        public int BokehShape { get; set; }

        /// <summary>散景旋转角度。</summary>
        public float BokehRotation { get; set; }

        /// <summary>是否启用景深。</summary>
        public bool IsEnabled => Mode != DepthOfFieldMode.Off;

        /// <summary>
        /// 计算给定距离处的虚化强度。
        /// </summary>
        /// <param name="distance">到相机的距离。</param>
        /// <param name="cameraPosition">相机位置（FocusTracking 模式需要）。</param>
        /// <returns>虚化强度 [0, 1]。</returns>
        public float CalculateBlurAmount(float distance, Vector3 cameraPosition = default)
        {
            if (!IsEnabled) return 0f;

            float focusDist = Mode == DepthOfFieldMode.FocusTracking
                ? Vector3.Distance(cameraPosition, FocusTarget)
                : FocusDistance;

            float nearEdge = MathF.Max(0f, focusDist - FocusRange * 0.5f);
            float farEdge = focusDist + FocusRange * 0.5f;

            // 在焦点范围内：清晰
            if (distance >= nearEdge && distance <= farEdge)
                return 0f;

            float blur;
            if (distance < nearEdge)
            {
                // 近景虚化
                float t = (nearEdge - distance) / MathF.Max(0.01f, nearEdge - NearFocusDistance);
                blur = Math.Clamp(t, 0f, 1f);
            }
            else
            {
                // 远景虚化
                float t = (distance - farEdge) / MathF.Max(0.01f, FarFocusDistance - farEdge);
                blur = Math.Clamp(t, 0f, 1f);
            }

            return blur * MaxBokehIntensity;
        }

        /// <summary>重置参数。</summary>
        public void Reset()
        {
            Mode = DepthOfFieldMode.Off;
            FocusDistance = 10f;
            FocusRange = 2f;
            NearFocusDistance = 1f;
            FarFocusDistance = 100f;
            MaxBokehIntensity = 0.8f;
        }
    }

    /// <summary>
    /// 运动模糊效果 (Motion Blur)。
    /// 基于相机或物体运动速度产生模糊拖影。
    /// </summary>
    public class MotionBlurEffect
    {
        /// <summary>运动模糊模式。</summary>
        public MotionBlurMode Mode { get; set; } = MotionBlurMode.Off;

        /// <summary>模糊强度（0=无, 1=最大）。</summary>
        public float Intensity { get; set; } = 0.5f;

        /// <summary>最大模糊长度（像素）。</summary>
        public float MaxBlurLength { get; set; } = 20f;

        /// <summary>速度阈值（超过此速度才产生模糊）。</summary>
        public float VelocityThreshold { get; set; } = 0.1f;

        /// <summary>是否仅对运动方向模糊（vs 全方向）。</summary>
        public bool DirectionalOnly { get; set; } = true;

        /// <summary>上一帧相机位置（用于计算速度）。</summary>
        private Vector3 _prevCameraPosition;

        /// <summary>上一帧相机旋转。</summary>
        private Quaternion _prevCameraRotation;

        /// <summary>上一帧速度缓存。</summary>
        private Vector3 _lastVelocity;

        /// <summary>是否启用运动模糊。</summary>
        public bool IsEnabled => Mode != MotionBlurMode.Off;

        /// <summary>
        /// 计算相机运动速度产生的模糊量。
        /// </summary>
        /// <param name="cameraPosition">当前相机位置。</param>
        /// <param name="cameraRotation">当前相机旋转。</param>
        /// <param name="deltaTime">帧间隔。</param>
        /// <returns>模糊强度 [0, 1]。</returns>
        public float CalculateCameraMotionBlur(
            Vector3 cameraPosition, Quaternion cameraRotation, float deltaTime)
        {
            if (!IsEnabled || deltaTime < 1e-6f) return 0f;

            if (Mode == MotionBlurMode.ObjectBased) return 0f;

            Vector3 velocity = (cameraPosition - _prevCameraPosition) / deltaTime;
            _lastVelocity = velocity;

            float speed = velocity.Length();
            _prevCameraPosition = cameraPosition;
            _prevCameraRotation = cameraRotation;

            if (speed < VelocityThreshold) return 0f;

            float normalizedSpeed = Math.Clamp((speed - VelocityThreshold) / 10f, 0f, 1f);
            return normalizedSpeed * Intensity;
        }

        /// <summary>获取上一帧的相机运动速度（用于物体模糊）。</summary>
        public Vector3 GetLastCameraVelocity() => _lastVelocity;

        /// <summary>重置。</summary>
        public void Reset()
        {
            Mode = MotionBlurMode.Off;
            _prevCameraPosition = Vector3.Zero;
            _prevCameraRotation = Quaternion.Identity;
            _lastVelocity = Vector3.Zero;
        }
    }

    /// <summary>
    /// 镜头光晕效果 (Lens Flare)。
    /// 模拟光源直射镜头时产生的光晕、色散和条纹。
    /// </summary>
    public class LensFlareEffect
    {
        /// <summary>是否启用。</summary>
        public bool IsEnabled { get; set; }

        /// <summary>光晕强度。</summary>
        public float Intensity { get; set; } = 0.6f;

        /// <summary>光晕阈值（屏幕亮度超过此值才产生光晕）。</summary>
        public float Threshold { get; set; } = 0.8f;

        /// <summary>光晕色彩（叠加到光晕上的色调）。</summary>
        public Vector3 Tint { get; set; } = new Vector3(1f, 0.9f, 0.7f);

        /// <summary>光晕数量（Ghost 数量）。</summary>
        public int GhostCount { get; set; } = 5;

        /// <summary>光晕分布偏移。</summary>
        public float GhostSpread { get; set; } = 0.3f;

        /// <summary>色散强度（Chromatic Aberration）。</summary>
        public float ChromaticAberration { get; set; } = 0.02f;

        /// <summary>星芒数量（0=无, 4=四角星, 6=六角星）。</summary>
        public int StarBurstCount { get; set; } = 0;

        /// <summary>星芒长度。</summary>
        public float StarBurstLength { get; set; } = 0.3f;

        /// <summary>光晕淡入淡出时间（秒）。</summary>
        public float FadeDuration { get; set; } = 0.5f;

        /// <summary>光源位置（世界空间）。</summary>
        public Vector3 LightPosition { get; set; }

        // ── 运行时状态 ──

        private float _fadeAlpha;
        private bool _isFadingIn;
        private bool _isFadingOut;
        private float _fadeTimer;

        /// <summary>开始淡入。</summary>
        public void FadeIn()
        {
            _isFadingIn = true;
            _isFadingOut = false;
            _fadeTimer = 0f;
        }

        /// <summary>开始淡出。</summary>
        public void FadeOut()
        {
            _isFadingIn = false;
            _isFadingOut = true;
            _fadeTimer = 0f;
        }

        /// <summary>每帧更新淡入淡出。</summary>
        public void Update(float deltaTime)
        {
            if (_isFadingIn)
            {
                _fadeTimer += deltaTime;
                _fadeAlpha = Math.Clamp(_fadeTimer / MathF.Max(0.01f, FadeDuration), 0f, 1f);
                if (_fadeAlpha >= 1f) _isFadingIn = false;
            }
            else if (_isFadingOut)
            {
                _fadeTimer += deltaTime;
                _fadeAlpha = 1f - Math.Clamp(_fadeTimer / MathF.Max(0.01f, FadeDuration), 0f, 1f);
                if (_fadeAlpha <= 0f)
                {
                    _fadeAlpha = 0f;
                    _isFadingOut = false;
                }
            }
        }

        /// <summary>获取当前有效强度（含淡入淡出）。</summary>
        public float GetEffectiveIntensity()
        {
            return IsEnabled ? Intensity * _fadeAlpha : 0f;
        }

        /// <summary>重置。</summary>
        public void Reset()
        {
            IsEnabled = false;
            _fadeAlpha = 0f;
            _isFadingIn = false;
            _isFadingOut = false;
            _fadeTimer = 0f;
        }
    }

    /// <summary>
    /// 相机后处理容器。统一管理所有后处理效果。
    /// </summary>
    public class CameraPostProcess
    {
        /// <summary>景深效果。</summary>
        public DepthOfFieldEffect DepthOfField { get; } = new();

        /// <summary>运动模糊效果。</summary>
        public MotionBlurEffect MotionBlur { get; } = new();

        /// <summary>镜头光晕效果。</summary>
        public LensFlareEffect LensFlare { get; } = new();

        /// <summary>是否启用任意后处理效果。</summary>
        public bool HasAnyEffect =>
            DepthOfField.IsEnabled || MotionBlur.IsEnabled || LensFlare.IsEnabled;

        /// <summary>每帧更新所有后处理效果。</summary>
        public void Update(float deltaTime)
        {
            LensFlare.Update(deltaTime);
        }

        /// <summary>重置所有效果。</summary>
        public void ResetAll()
        {
            DepthOfField.Reset();
            MotionBlur.Reset();
            LensFlare.Reset();
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 电影相机系统 (Cinematic Camera System) —— 统一管理
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 电影相机系统。统一管理所有电影相机、抖动和后处理效果的评估，
    /// 输出最终的相机状态供渲染管线使用。
    /// </summary>
    public class CinematicCameraSystem
    {
        /// <summary>所有注册的电影相机。</summary>
        private readonly Dictionary<string, ICinematicCamera> _cameras = new();

        /// <summary>当前活跃相机名称。</summary>
        public string ActiveCameraName { get; private set; } = string.Empty;

        /// <summary>抖动控制器。</summary>
        public CameraShakeController ShakeController { get; } = new();

        /// <summary>后处理效果。</summary>
        public CameraPostProcess PostProcess { get; } = new();

        /// <summary>最终输出状态（含抖动叠加）。</summary>
        public CinematicCameraState FinalState { get; private set; }

        /// <summary>相机切换事件。</summary>
        public event Action<string, string>? OnCameraSwitched;

        /// <summary>每帧状态更新事件。</summary>
        public event Action<CinematicCameraState>? OnStateUpdated;

        /// <summary>注册相机。</summary>
        public void RegisterCamera(string name, ICinematicCamera camera)
        {
            _cameras[name] = camera;
        }

        /// <summary>注销相机。</summary>
        public bool UnregisterCamera(string name)
        {
            if (name == ActiveCameraName)
                ActiveCameraName = string.Empty;
            return _cameras.Remove(name);
        }

        /// <summary>切换活跃相机。</summary>
        public bool SwitchCamera(string name)
        {
            if (!_cameras.ContainsKey(name)) return false;

            string prevCam = ActiveCameraName;
            ActiveCameraName = name;
            OnCameraSwitched?.Invoke(prevCam, name);
            return true;
        }

        /// <summary>获取指定相机。</summary>
        public ICinematicCamera? GetCamera(string name)
        {
            return _cameras.TryGetValue(name, out var cam) ? cam : null;
        }

        /// <summary>获取当前活跃相机。</summary>
        public ICinematicCamera? GetActiveCamera()
        {
            return string.IsNullOrEmpty(ActiveCameraName)
                ? null
                : GetCamera(ActiveCameraName);
        }

        /// <summary>
        /// 每帧更新。评估活跃相机、叠加抖动、计算后处理参数，输出最终状态。
        /// </summary>
        /// <param name="deltaTime">帧间隔（秒）。</param>
        public void Update(float deltaTime)
        {
            var activeCam = GetActiveCamera();
            if (activeCam == null)
            {
                FinalState = default;
                return;
            }

            // 更新相机
            activeCam.Update(deltaTime);

            // 获取基础状态
            CinematicCameraState state = activeCam.GetState();

            // 更新并叠加抖动
            ShakeController.Update(deltaTime);
            state.ShakeOffset = ShakeController.CurrentOffset;
            state.ShakeRotation = ShakeController.CurrentRotation;

            // 叠加抖动到最终位置和朝向
            state.Position += ShakeController.CurrentOffset;
            if (ShakeController.CurrentRotation != Quaternion.Identity)
            {
                state.Forward = Vector3.Transform(
                    state.Forward, ShakeController.CurrentRotation);
                state.Up = Vector3.Transform(
                    state.Up, ShakeController.CurrentRotation);
            }

            // 更新后处理
            PostProcess.Update(deltaTime);
            if (PostProcess.MotionBlur.IsEnabled)
            {
                PostProcess.MotionBlur.CalculateCameraMotionBlur(
                    state.Position, Quaternion.Identity, deltaTime);
            }

            FinalState = state;
            OnStateUpdated?.Invoke(FinalState);
        }

        /// <summary>
        /// 从序列器时间评估关键帧相机。
        /// </summary>
        /// <param name="cameraName">相机名称。</param>
        /// <param name="time">序列时间。</param>
        public void EvaluateKeyframeCamera(string cameraName, float time)
        {
            if (GetCamera(cameraName) is KeyframeCamera kfCam)
            {
                kfCam.EvaluateFromSequencer(time);
            }
        }

        /// <summary>重置所有相机和效果。</summary>
        public void ResetAll()
        {
            foreach (var cam in _cameras.Values)
                cam.Reset();

            ShakeController.ClearAllShakes();
            PostProcess.ResetAll();
            ActiveCameraName = string.Empty;
            FinalState = default;
        }

        /// <summary>获取已注册相机数量。</summary>
        public int CameraCount => _cameras.Count;
    }
}
