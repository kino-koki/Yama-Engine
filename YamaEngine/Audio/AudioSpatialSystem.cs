using System;
using System.Collections.Generic;
using System.Numerics;

namespace YamaEngine.Audio
{
    // ════════════════════════════════════════════════════════════════════════
    // yama 3D 空间音频系统 (Audio Spatial System)
    //
    // HRTF 空间化、距离衰减、遮挡检测、混响区域、多普勒效应、
    // 声音传播、多监听者支持。
    // ════════════════════════════════════════════════════════════════════════

    // ──────────────────────────────────────────────────────────────────────
    //  距离衰减模型
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>距离衰减模型类型。</summary>
    public enum DistanceAttenuationModel
    {
        /// <summary>线性衰减 (1/r)。</summary>
        Linear,
        /// <summary>反比例衰减 (1/r^2)。</summary>
        InverseSquare,
        /// <summary>对数衰减 (类似 OpenAL AL_INVERSE_DISTANCE_CLAMPED)。</summary>
        Logarithmic,
        /// <summary>自定义曲线。</summary>
        Custom
    }

    /// <summary>HRTF 空间化模式。</summary>
    public enum HRTFMode
    {
        Disabled,
        /// <summary>简化 HRTF (基于角度的 ITD/IAD 近似)。</summary>
        Simple,
        /// <summary>完整 HRTF (需要 HRTF 数据集)。</summary>
        Full,
        /// <summary>自动 (根据系统支持选择)。</summary>
        Auto
    }

    /// <summary>声音传播路径类型。</summary>
    public enum AudioPropagationType
    {
        /// <summary>直达声 (视线无遮挡)。</summary>
        Direct,
        /// <summary>间接声 (一次反射)。</summary>
        Indirect,
        /// <summary>反射声 (多次反射)。</summary>
        Reflection,
        /// <summary>衍射声 (绕过障碍物边缘)。</summary>
        Diffraction
    }

    // ──────────────────────────────────────────────────────────────────────
    //  3D 音频设置
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>3D 空间化设置。</summary>
    public sealed class Audio3DSettings
    {
        /// <summary>距离衰减模型。</summary>
        public DistanceAttenuationModel AttenuationModel { get; set; } = DistanceAttenuationModel.Logarithmic;

        /// <summary>最小距离 (此距离内不衰减)。</summary>
        public float MinDistance { get; set; } = 1f;

        /// <summary>最大距离 (此距离外衰减到 0)。</summary>
        public float MaxDistance { get; set; } = 100f;

        /// <summary>滚降因子 (衰减速度倍率)。</summary>
        public float RolloffFactor { get; set; } = 1f;

        /// <summary>扩散角/内锥 (度)。</summary>
        public float InnerConeAngle { get; set; } = 360f;

        /// <summary>外锥 (度)。</summary>
        public float OuterConeAngle { get; set; } = 360f;

        /// <summary>外锥音量 (0 = 静音)。</summary>
        public float OuterConeVolume { get; set; } = 0f;

        /// <summary>自定义衰减曲线。</summary>
        public AudioParameterCurve? CustomCurve { get; set; }

        /// <summary>多普勒因子 (0 = 禁用, 1 = 真实, >1 = 夸张)。</summary>
        public float DopplerFactor { get; set; } = 1f;

        /// <summary>速度参考值 (声速 m/s)。</summary>
        public float SpeedOfSound { get; set; } = 343f;

        /// <summary>HRTF 模式。</summary>
        public HRTFMode HrtfMode { get; set; } = HRTFMode.Auto;

        /// <summary>根据距离计算衰减增益。</summary>
        public float CalculateAttenuation(float distance)
        {
            distance = Math.Max(0.001f, distance);

            if (distance <= MinDistance) return 1f;
            if (distance >= MaxDistance) return 0f;

            if (CustomCurve != null)
                return CustomCurve.Evaluate(distance / MaxDistance);

            float distRatio = (distance - MinDistance) / (MaxDistance - MinDistance);

            return AttenuationModel switch
            {
                DistanceAttenuationModel.Linear => 1f - distRatio * RolloffFactor,
                DistanceAttenuationModel.InverseSquare => MinDistance / (MinDistance + RolloffFactor * (distance - MinDistance)),
                DistanceAttenuationModel.Logarithmic => (float)(1f - RolloffFactor * Math.Log10(distance / MinDistance + 1f) / Math.Log10(MaxDistance / MinDistance + 1f)),
                _ => 1f - distRatio
            };
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    //  AudioListener — 监听者
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>音频监听者，代表"耳朵"位置。</summary>
    public sealed class AudioListener
    {
        private static int _nextId;

        /// <summary>监听者 ID。</summary>
        public int ListenerId { get; }

        /// <summary>监听者名称。</summary>
        public string Name { get; set; } = "Listener";

        /// <summary>世界空间位置。</summary>
        public Vector3 Position { get; set; }

        /// <summary>监听者朝向四元数。</summary>
        public Quaternion Rotation { get; set; } = Quaternion.Identity;

        /// <summary>前方向。</summary>
        public Vector3 Forward { get; set; } = -Vector3.UnitZ;

        /// <summary>上方向。</summary>
        public Vector3 Up { get; set; } = Vector3.UnitY;

        /// <summary>速度。</summary>
        public Vector3 Velocity { get; set; }

        /// <summary>右方向 (计算属性)。</summary>
        public Vector3 Right => Vector3.Normalize(Vector3.Cross(Forward, Up));

        /// <summary>增益。</summary>
        public float Gain { get; set; } = 1f;

        /// <summary>是否激活。</summary>
        public bool IsActive { get; set; } = true;

        public AudioListener()
        {
            ListenerId = ++_nextId;
        }

        /// <summary>设置朝向 (通过前向量和上向量)。</summary>
        public void SetOrientation(Vector3 forward, Vector3 up)
        {
            Forward = Vector3.Normalize(forward);
            Up = Vector3.Normalize(up);
        }

        /// <summary>将世界坐标转换为监听者局部坐标。</summary>
        public Vector3 WorldToLocal(Vector3 worldPos)
        {
            Vector3 delta = worldPos - Position;
            Matrix4x4 viewMatrix = Matrix4x4.CreateLookAt(Position, Position + Forward, Up);
            Vector3 local = Vector3.Transform(delta, Matrix4x4.Transpose(viewMatrix));
            return local;
        }

        /// <summary>计算声源到监听者的方向 (归一化)。</summary>
        public Vector3 GetDirectionTo(Vector3 sourcePosition)
        {
            return Vector3.Normalize(sourcePosition - Position);
        }

        /// <summary>计算声源到监听者的距离。</summary>
        public float GetDistanceTo(Vector3 sourcePosition)
        {
            return Vector3.Distance(sourcePosition, Position);
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    //  AudioObstruction — 障碍物检测
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>障碍物信息。</summary>
    public sealed class AudioObstruction
    {
        /// <summary>是否被遮挡。</summary>
        public bool IsObstructed { get; set; }

        /// <summary>遮挡因子 [0 = 完全遮挡, 1 = 无遮挡]。</summary>
        public float OcclusionFactor { get; set; } = 1f;

        /// <summary>遮挡次数 (射线穿过多少个障碍物)。</summary>
        public int HitCount { get; set; }

        /// <summary>材质吸收系数 (每次遮挡吸收多少)。</summary>
        public float AbsorptionCoefficient { get; set; } = 0.5f;

        /// <summary>计算最终遮挡衰减。</summary>
        public float CalculateAttenuation()
        {
            if (!IsObstructed) return 1f;
            return (float)Math.Pow(AbsorptionCoefficient, HitCount);
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    //  AudioOcclusionSystem — 遮挡系统
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>遮挡检测委托 (由物理引擎/碰撞系统实现)。</summary>
    public delegate bool AudioRaycastDelegate(Vector3 from, Vector3 to, out AudioObstruction obstruction);

    /// <summary>音频遮挡系统，基于射线检测计算遮挡因子。</summary>
    public sealed class AudioOcclusionSystem
    {
        /// <summary>射线检测回调 (由外部物理引擎注入)。</summary>
        public AudioRaycastDelegate? RaycastCallback { get; set; }

        /// <summary>最大射线检测距离。</summary>
        public float MaxRayDistance { get; set; } = 200f;

        /// <summary>是否启用遮挡系统。</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>遮挡更新频率 (每帧/每N帧)。</summary>
        public int UpdateFrequency { get; set; } = 1;

        private int _frameCounter;

        /// <summary>检测两个点之间的遮挡。</summary>
        public AudioObstruction TestOcclusion(Vector3 sourcePos, Vector3 listenerPos)
        {
            var obstruction = new AudioObstruction
            {
                IsObstructed = false,
                OcclusionFactor = 1f,
                HitCount = 0
            };

            if (!Enabled || RaycastCallback == null) return obstruction;
            if (Vector3.Distance(sourcePos, listenerPos) > MaxRayDistance) return obstruction;

            if (RaycastCallback(sourcePos, listenerPos, out var result))
            {
                obstruction.IsObstructed = result.IsObstructed;
                obstruction.OcclusionFactor = result.OcclusionFactor;
                obstruction.HitCount = result.HitCount;
                obstruction.AbsorptionCoefficient = result.AbsorptionCoefficient;
            }

            return obstruction;
        }

        /// <summary>每帧更新计数器。</summary>
        public void OnFrameUpdate()
        {
            _frameCounter++;
        }

        /// <summary>是否应该在此帧更新遮挡检测。</summary>
        public bool ShouldUpdateThisFrame()
        {
            return _frameCounter % Math.Max(1, UpdateFrequency) == 0;
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    //  AudioReverbPreset — 混响预设
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>混响预设参数 (EAX/EFX 风格)。</summary>
    public sealed class AudioReverbPreset
    {
        /// <summary>预设名称。</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>房间大小 (0-100)。</summary>
        public float RoomSize { get; set; } = 50f;

        /// <summary>衰减时间 (秒)。</summary>
        public float DecayTime { get; set; } = 1.5f;

        /// <summary>预延迟 (秒)。</summary>
        public float PreDelay { get; set; } = 0.02f;

        /// <summary>扩散 (0-100)。</summary>
        public float Diffusion { get; set; } = 100f;

        /// <summary>密度 (0-100)。</summary>
        public float Density { get; set; } = 100f;

        /// <summary>低频增益 (dB)。</summary>
        public float LowFrequencyGain { get; set; } = 0f;

        /// <summary>高频衰减 (dB)。</summary>
        public float HighFrequencyCuttoff { get; set; } = 5000f;

        /// <summary>低频衰减 (dB)。</summary>
        public float LowFrequencyCuttoff { get; set; } = 250f;

        /// <summary>早期反射增益。</summary>
        public float ReflectionsGain { get; set; } = 0f;

        /// <summary>晚期混响增益。</summary>
        public float LateReverbGain { get; set; } = -26f;

        /// <summary>早期反射延迟。</summary>
        public float ReflectionsDelay { get; set; } = 0f;

        /// <summary>晚期混响延迟。</summary>
        public float LateReverbDelay { get; set; } = 0f;

        /// <summary>回声密度。</summary>
        public float EchoTime { get; set; } = 0.25f;

        /// <summary>回声深度。</summary>
        public float EchoDepth { get; set; } = 0f;

        /// <summary>空气吸收。</summary>
        public float AirAbsorption { get; set; } = 0f;

        /// <summary>房间滚降。</summary>
        public float RoomRolloff { get; set; } = 0f;

        // ── 内置预设工厂 ──

        /// <summary>创建关闭 (无混响) 预设。</summary>
        public static AudioReverbPreset Off => new() { Name = "Off", DecayTime = 0f, LateReverbGain = -10000f };

        /// <summary>小房间。</summary>
        public static AudioReverbPreset SmallRoom => CreatePreset("SmallRoom", 1.1f, 0.8f, 0.005f, 83f, 100f);

        /// <summary>大房间。</summary>
        public static AudioReverbPreset LargeRoom => CreatePreset("LargeRoom", 2.5f, 1.8f, 0.01f, 90f, 80f);

        /// <summary>大厅。</summary>
        public static AudioReverbPreset Hall => CreatePreset("Hall", 4.0f, 3.5f, 0.03f, 85f, 70f);

        /// <summary>走廊。</summary>
        public static AudioReverbPreset Corridor => CreatePreset("Corridor", 1.5f, 1.5f, 0.015f, 75f, 85f);

        /// <summary>山洞。</summary>
        public static AudioReverbPreset Cave => CreatePreset("Cave", 5.0f, 5.0f, 0.05f, 60f, 50f);

        /// <summary>森林。</summary>
        public static AudioReverbPreset Forest => CreatePreset("Forest", 3.0f, 2.5f, 0.06f, 95f, 30f);

        /// <summary>水下。</summary>
        public static AudioReverbPreset Underwater => CreatePreset("Underwater", 1.5f, 3.0f, 0.0f, 30f, 20f, highFreqCut: 500f);

        /// <summary>金属管道。</summary>
        public static AudioReverbPreset MetalPipe => CreatePreset("MetalPipe", 1.0f, 2.0f, 0.005f, 50f, 90f, highFreqCut: 8000f);

        private static AudioReverbPreset CreatePreset(string name, float size, float decay, float preDelay,
            float diffusion, float density, float lowFreqGain = 0f, float highFreqCut = 5000f)
        {
            return new AudioReverbPreset
            {
                Name = name,
                RoomSize = size * 10f,
                DecayTime = decay,
                PreDelay = preDelay,
                Diffusion = diffusion,
                Density = density,
                LowFrequencyGain = lowFreqGain,
                HighFrequencyCuttoff = highFreqCut
            };
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    //  AudioReverbZone — 混响区域
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>混响区域形状。</summary>
    public enum ReverbZoneShape
    {
        Box,
        Sphere
    }

    /// <summary>混响区域：进入/离开时自动切换混响预设。</summary>
    public sealed class AudioReverbZone
    {
        private static int _nextId;

        /// <summary>区域 ID。</summary>
        public int ZoneId { get; }

        /// <summary>区域名称。</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>区域形状。</summary>
        public ReverbZoneShape Shape { get; set; } = ReverbZoneShape.Box;

        /// <summary>区域中心位置。</summary>
        public Vector3 Position { get; set; }

        /// <summary>区域范围 (Box: 半尺寸, Sphere: 半径)。</summary>
        public Vector3 Size { get; set; } = new Vector3(10f, 5f, 10f);

        /// <summary>区域内混响预设。</summary>
        public AudioReverbPreset ReverbPreset { get; set; } = AudioReverbPreset.Off;

        /// <summary>过渡时间 (秒)。</summary>
        public float TransitionTime { get; set; } = 0.5f;

        /// <summary>优先级 (重叠区域时高优先级覆盖)。</summary>
        public int Priority { get; set; }

        /// <summary>当前活跃度 (0-1 混合过渡)。</summary>
        public float ActiveFactor { get; private set; }

        /// <summary>是否包含指定点。</summary>
        public bool ContainsPoint(Vector3 point)
        {
            return Shape switch
            {
                ReverbZoneShape.Box => ContainsBox(point),
                ReverbZoneShape.Sphere => ContainsSphere(point),
                _ => false
            };
        }

        /// <summary>计算点到区域的过渡因子 (用于平滑过渡)。</summary>
        public float CalculateTransitionFactor(Vector3 point)
        {
            // 简化：区域内=1，区域外=0，可扩展边缘渐变
            return ContainsPoint(point) ? 1f : 0f;
        }

        /// <summary>更新过渡。</summary>
        public void UpdateTransition(float deltaTime, float targetFactor)
        {
            float speed = TransitionTime > 0.001f ? 1f / TransitionTime : 1000f;
            if (ActiveFactor < targetFactor)
                ActiveFactor = Math.Min(targetFactor, ActiveFactor + speed * deltaTime);
            else if (ActiveFactor > targetFactor)
                ActiveFactor = Math.Max(targetFactor, ActiveFactor - speed * deltaTime);
        }

        private bool ContainsBox(Vector3 point)
        {
            Vector3 half = Size;
            Vector3 delta = point - Position;
            return Math.Abs(delta.X) <= half.X &&
                   Math.Abs(delta.Y) <= half.Y &&
                   Math.Abs(delta.Z) <= half.Z;
        }

        private bool ContainsSphere(Vector3 point)
        {
            float dist = Vector3.Distance(point, Position);
            return dist <= Size.X; // Sphere: 使用 X 分量作为半径
        }

        public AudioReverbZone()
        {
            ZoneId = ++_nextId;
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    //  DopplerEffect — 多普勒效应
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>多普勒效应处理器。</summary>
    public sealed class DopplerEffect
    {
        /// <summary>多普勒因子 (0=禁用, 1=真实, >1=夸张)。</summary>
        public float DopplerFactor { get; set; } = 1f;

        /// <summary>声速参考值 (m/s)。</summary>
        public float SpeedOfSound { get; set; } = 343f;

        /// <summary>计算多普勒频率偏移系数 (返回值乘以原始音调)。</summary>
        public float CalculateFrequencyShift(Vector3 sourcePos, Vector3 sourceVel,
            Vector3 listenerPos, Vector3 listenerVel)
        {
            Vector3 direction = Vector3.Normalize(listenerPos - sourcePos);

            // 源沿方向的速度分量
            float sourceVelocityAlong = Vector3.Dot(sourceVel, direction);

            // 监听者沿方向的速度分量
            float listenerVelocityAlong = Vector3.Dot(listenerVel, direction);

            // 多普勒公式: f' = f * (v + vl) / (v + vs)
            float v = SpeedOfSound * DopplerFactor;
            float numerator = v + listenerVelocityAlong;
            float denominator = v + sourceVelocityAlong;

            if (Math.Abs(denominator) < 0.001f) return 1f;

            return Math.Clamp(numerator / denominator, 0.5f, 2.0f);
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    //  AudioPropagation — 声音传播
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>声音传播路径信息。</summary>
    public sealed class AudioPropagationPath
    {
        /// <summary>路径类型。</summary>
        public AudioPropagationType Type { get; set; }

        /// <summary>路径点列表 (从源到监听者)。</summary>
        public List<Vector3> PathPoints { get; } = new();

        /// <summary>路径总长度。</summary>
        public float TotalLength { get; set; }

        /// <summary>传播延迟 (秒)。</summary>
        public float Delay { get; set; }

        /// <summary>路径增益 (含衰减)。</summary>
        public float Gain { get; set; } = 1f;

        /// <summary>路径上反射材质吸收系数。</summary>
        public float Absorption { get; set; }
    }

    /// <summary>声音传播系统，计算直达/反射/衍射路径。</summary>
    public sealed class AudioPropagation
    {
        /// <summary>最大反射次数。</summary>
        public int MaxReflections { get; set; } = 2;

        /// <summary>最大衍射次数。</summary>
        public int MaxDiffractions { get; set; } = 1;

        /// <summary>最大路径数。</summary>
        public int MaxPaths { get; set; } = 8;

        /// <summary>是否启用反射路径。</summary>
        public bool EnableReflections { get; set; } = true;

        /// <summary>是否启用衍射路径。</summary>
        public bool EnableDiffractions { get; set; } = true;

        /// <summary>计算从声源到监听者的所有传播路径。</summary>
        public List<AudioPropagationPath> CalculatePaths(
            Vector3 sourcePos, Vector3 listenerPos,
            float speedOfSound = 343f)
        {
            var paths = new List<AudioPropagationPath>();

            // 直达路径
            var directPath = new AudioPropagationPath
            {
                Type = AudioPropagationType.Direct,
                Gain = 1f
            };
            directPath.PathPoints.Add(sourcePos);
            directPath.PathPoints.Add(listenerPos);
            directPath.TotalLength = Vector3.Distance(sourcePos, listenerPos);
            directPath.Delay = directPath.TotalLength / speedOfSound;
            paths.Add(directPath);

            // 反射路径 (简化: 假设墙壁反射)
            if (EnableReflections)
            {
                for (int i = 0; i < MaxReflections && paths.Count < MaxPaths; i++)
                {
                    var refPath = new AudioPropagationPath
                    {
                        Type = AudioPropagationType.Reflection,
                        Gain = 0.3f / (i + 1)
                    };
                    refPath.PathPoints.Add(sourcePos);
                    // 简化反射点 (中点偏移)
                    Vector3 mid = (sourcePos + listenerPos) * 0.5f + new Vector3(0, i * 2f, 0);
                    refPath.PathPoints.Add(mid);
                    refPath.PathPoints.Add(listenerPos);
                    refPath.TotalLength = 0f;
                    for (int j = 0; j < refPath.PathPoints.Count - 1; j++)
                        refPath.TotalLength += Vector3.Distance(refPath.PathPoints[j], refPath.PathPoints[j + 1]);
                    refPath.Delay = refPath.TotalLength / speedOfSound;
                    paths.Add(refPath);
                }
            }

            return paths;
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    //  AudioSpatializer — 空间化处理器
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>3D 空间化处理结果。</summary>
    public sealed class SpatializationResult
    {
        /// <summary>声源到监听者距离。</summary>
        public float Distance { get; set; }
        /// <summary>总音量系数 [0,1]。</summary>
        public float VolumeFactor { get; set; } = 1f;
        /// <summary>音调系数。</summary>
        public float PitchFactor { get; set; } = 1f;
        /// <summary>声像 [-1(左),0(中),1(右)]。</summary>
        public float Pan { get; set; }
        /// <summary>低通截止频率 (Hz)，用于遮挡/距离。</summary>
        public float LowpassCutoff { get; set; } = 20000f;
        /// <summary>混响发送量 [0,1]。</summary>
        public float ReverbSend { get; set; }
        /// <summary>是否被遮挡。</summary>
        public bool Occluded { get; set; }
        /// <summary>方向性因子 [0,1]。</summary>
        public float DirectivityFactor { get; set; } = 1f;
        /// <summary>相对监听者前向的方位角 (弧度)。</summary>
        public float AzimuthRad { get; set; }
        /// <summary>俯仰角 (弧度)。</summary>
        public float ElevationRad { get; set; }

        /// <summary>左声道增益。</summary>
        public float LeftGain { get; set; } = 1f;

        /// <summary>右声道增益。</summary>
        public float RightGain { get; set; } = 1f;

        /// <summary>距离衰减增益。</summary>
        public float DistanceGain { get; set; } = 1f;

        /// <summary>遮挡衰减增益。</summary>
        public float OcclusionGain { get; set; } = 1f;

        /// <summary>多普勒音调偏移。</summary>
        public float DopplerPitch { get; set; } = 1f;

        /// <summary>扩散角/锥角增益。</summary>
        public float ConeGain { get; set; } = 1f;

        /// <summary>最终左/右输出。</summary>
        public float FinalLeft => LeftGain * DistanceGain * OcclusionGain * ConeGain;
        public float FinalRight => RightGain * DistanceGain * OcclusionGain * ConeGain;
    }

    /// <summary>空间化处理器 (HRTF/平移/衰减/遮挡)。</summary>
    public sealed class AudioSpatializer
    {
        /// <summary>全局 3D 设置。</summary>
        public Audio3DSettings Settings { get; } = new();

        /// <summary>多普勒效应处理器。</summary>
        public DopplerEffect Doppler { get; } = new();

        /// <summary>遮挡系统。</summary>
        public AudioOcclusionSystem Occlusion { get; } = new();

        /// <summary>传播系统。</summary>
        public AudioPropagation Propagation { get; } = new();

        /// <summary>计算声源的完整空间化结果。</summary>
        public SpatializationResult Spatialize(
            Vector3 sourcePos, Vector3 sourceVel, Vector3 sourceForward,
            AudioListener listener)
        {
            var result = new SpatializationResult();

            // 1. 距离衰减
            float distance = listener.GetDistanceTo(sourcePos);
            result.DistanceGain = Settings.CalculateAttenuation(distance);

            // 2. 声像 (Pan) - 基于角度
            Vector3 localPos = listener.WorldToLocal(sourcePos);
            float angle = (float)Math.Atan2(localPos.X, -localPos.Z); // 相对于监听者前方的角度

            // HRTF 简化模型: 基于角度的左右增益
            if (Settings.HrtfMode != HRTFMode.Disabled)
            {
                // 简单正弦声像
                float panFactor = (float)Math.Sin(angle);
                result.LeftGain = Math.Clamp(1f - panFactor, 0f, 1f);
                result.RightGain = Math.Clamp(1f + panFactor, 0f, 1f);

                // 简化 ITD (耳间时间差) 近似 - 距离越远越明显
                float itdGain = Math.Clamp(distance / Settings.MaxDistance, 0f, 0.3f);
                result.LeftGain *= (1f - itdGain * 0.1f);
                result.RightGain *= (1f + itdGain * 0.1f);
            }

            // 3. 扩散角 (锥角)
            if (Settings.InnerConeAngle < 360f || Settings.OuterConeAngle < 360f)
            {
                Vector3 toListener = listener.GetDirectionTo(sourcePos);
                float sourceAngle = (float)Math.Acos(Math.Clamp(
                    Vector3.Dot(Vector3.Normalize(sourceForward), -toListener), -1f, 1f)) * (180f / MathF.PI);

                if (sourceAngle <= Settings.InnerConeAngle)
                    result.ConeGain = 1f;
                else if (sourceAngle >= Settings.OuterConeAngle)
                    result.ConeGain = Settings.OuterConeVolume;
                else
                {
                    float innerRad = Settings.InnerConeAngle * (MathF.PI / 180f);
                    float outerRad = Settings.OuterConeAngle * (MathF.PI / 180f);
                    float angleRad = sourceAngle * (MathF.PI / 180f);
                    float t = (angleRad - innerRad) / Math.Max(0.001f, outerRad - innerRad);
                    result.ConeGain = 1f + (Settings.OuterConeVolume - 1f) * t;
                }
            }

            // 4. 遮挡
            if (Occlusion.Enabled)
            {
                var obstruction = Occlusion.TestOcclusion(sourcePos, listener.Position);
                result.OcclusionGain = obstruction.CalculateAttenuation();
            }

            // 5. 多普勒
            result.DopplerPitch = Doppler.CalculateFrequencyShift(
                sourcePos, sourceVel, listener.Position, listener.Velocity);

            return result;
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    //  AudioReverbZoneManager — 混响区域管理器
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>混响区域管理器。</summary>
    public sealed class AudioReverbZoneManager
    {
        /// <summary>所有混响区域。</summary>
        public List<AudioReverbZone> Zones { get; } = new();

        /// <summary>当前活跃的混响预设 (混合后)。</summary>
        public AudioReverbPreset CurrentPreset { get; private set; } = AudioReverbPreset.Off;

        /// <summary>添加区域。</summary>
        public void AddZone(AudioReverbZone zone)
        {
            Zones.Add(zone);
        }

        /// <summary>移除区域。</summary>
        public bool RemoveZone(AudioReverbZone zone)
        {
            return Zones.Remove(zone);
        }

        /// <summary>更新所有区域，计算当前监听者所在区域。</summary>
        public void Update(Vector3 listenerPosition, float deltaTime)
        {
            AudioReverbPreset bestPreset = AudioReverbPreset.Off;
            int bestPriority = -1;
            float bestFactor = 0f;

            foreach (var zone in Zones)
            {
                float factor = zone.CalculateTransitionFactor(listenerPosition);
                zone.UpdateTransition(deltaTime, factor);

                if (zone.ActiveFactor > 0f && zone.Priority > bestPriority)
                {
                    bestPriority = zone.Priority;
                    bestPreset = zone.ReverbPreset;
                    bestFactor = zone.ActiveFactor;
                }
            }

            // 简化：直接使用最高优先级预设
            if (bestFactor > 0f)
                CurrentPreset = bestPreset;
            else
                CurrentPreset = AudioReverbPreset.Off;
        }
    }
}
