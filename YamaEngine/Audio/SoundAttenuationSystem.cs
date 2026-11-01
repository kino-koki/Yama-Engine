// ==============================================================================
//  yama引擎 YamaEngine — 声音衰减 / 声音类 / 声音混音 / 并发 / 虚拟化 / 空间化 / SoundCue 系统
//  文件：SoundAttenuationSystem.cs
//  命名空间：YamaEngine.Audio
// -----------------------------------------------------------------------------
//  本文件对齐 Unreal Engine 5 的音频子系统，覆盖以下八大模块：
//
//    一、Sound Attenuation   声音衰减   —— 距离/形状/锥角/多普勒/空气吸收/遮挡/混响发送
//    二、Sound Class         声音类     —— 层级化音量控制（父类音量沿链下传累乘）
//    三、Sound Mix           声音混音   —— 临时覆盖声音类音量（淡入淡出 + 栈式叠加）
//    四、Sound Concurrency   声音并发   —— 同组最大并发数与超限裁剪策略
//    五、Voice Virtualization 声音虚拟化 —— 超过活跃上限时按优先级/距离裁剪并保留状态
//    六、AudioSpatializer    空间化器   —— 每帧把衰减/Doppler/遮挡/AirAbsorption 写入实例
//    七、SoundCue            声音提示   —— 轻量节点图（WavePlayer/Random/Loop/Modulator...）
//    八、SoundAttenuationSystem 主系统  —— ECS ISystem，顶层调度所有子系统
//
//  与现有系统的对接（签名严格对齐，不修改 AudioSystem.cs / AudioEffectsSystem.cs）：
//    * YamaEngine.Audio.AudioSystem           —— PlayOneShot / PlayLooping / Stop /
//          SetBusVolume / GetBusVolume / SetBusMuted / SetListenerPosition /
//          UpdateInstancePosition / Update / GetBusStates
//    * YamaEngine.Audio.AudioInstance         —— Position/Velocity/Volume/Pitch/Pan/
//          MinDistance/MaxDistance/RolloffFactor/DopplerFactor/Is3D/Bus/Id/Clip/State
//    * YamaEngine.Audio.AudioListener         —— Position/Rotation/Velocity/IsActive/EntityId
//    * YamaEngine.ECS.EntityComponentSystem  —— IComponent(struct) / ISystem /
//          TransformComponent / QueryWithComponent / GetComponent / SetComponent
//    * YamaEngine.Physics.PhysicsWorld        —— Raycast(origin, direction, maxDistance, ...)
//
//  命名冲突规避：
//    * AudioBus 在 YamaEngine.Audio 中是枚举（Master/Music/SFX/...），在
//      YamaEngine.Audio.Effects 中是类（效果链总线）。本文件不复用该名，
//      音量类层级改用 SoundClass / SoundClassTree 表达。
//    * 所有新增类型均与现有 YamaEngine.Audio / .Effects 类型无重名。
// ==============================================================================

using System;
using System.Collections.Generic;
using System.Numerics;
using YamaEngine.ECS;
using YamaEngine.Physics;

namespace YamaEngine.Audio
{
    // ════════════════════════════════════════════════════════════════════════════
    //  枚举
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 衰减模型（对齐 UE5 Attenuation Distance Algorithm）。
    /// 决定音量随距离从 <see cref="SoundAttenuationSettings.InnerRadius"/> 到
    /// <see cref="SoundAttenuationSettings.FalloffDistance"/> 的下降曲线形状。
    /// </summary>
    public enum AttenuationModel
    {
        /// <summary>无衰减：任意距离均保持全音量。</summary>
        None,
        /// <summary>线性衰减：音量随距离线性下降到 0。</summary>
        Linear,
        /// <summary>对数衰减：近处缓慢下降、远处快速下降。</summary>
        Logarithmic,
        /// <summary>反平方衰减：物理真实的能量随距离平方衰减。</summary>
        InverseSquare,
        /// <summary>自然衰减：指数衰减 e^(-k*t)，声音自然消散。</summary>
        Natural,
        /// <summary>反比例衰减：gain = refDist / (refDist + rolloff * (dist - refDist))。</summary>
        Inverse,
        /// <summary>指数衰减：gain = (dist / refDist) ^ (-rolloff)。</summary>
        Exponential,
        /// <summary>自定义曲线衰减（DistanceAttenuation 兼容）。</summary>
        Custom,
        /// <summary>自定义曲线：由 <see cref="SoundAttenuationSettings.CustomCurve"/> 决定。</summary>
        CustomCurve
    }

    /// <summary>
    /// 衰减形状（对齐 UE5 Attenuation Shape）。决定"距离"如何从体积形状中计算。
    /// </summary>
    public enum AttenuationShape
    {
        /// <summary>球形：以声源为中心的球。</summary>
        Sphere,
        /// <summary>盒形：以声源为中心的轴对齐盒。</summary>
        Box,
        /// <summary>胶囊形：胶囊体。</summary>
        Capsule,
        /// <summary>锥形：方向性，配合 <see cref="ConeSettings"/> 做角度衰减。</summary>
        Cone
    }

    /// <summary>并发超限时的裁剪策略。</summary>
    public enum ConcurrencyResolution
    {
        /// <summary>停止距离听者最远的实例。</summary>
        StopFurthest,
        /// <summary>停止当前有效音量最小的实例。</summary>
        StopQuietest,
        /// <summary>停止最早开始播放的实例。</summary>
        StopOldest
    }

    /// <summary>SoundCue 节点类型。</summary>
    public enum SoundCueNodeType
    {
        WavePlayer,
        Random,
        Loop,
        Delay,
        Modulator,
        Attenuation,
        Concurrency,
        Class,
        Mixer
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  一、Sound Attenuation 声音衰减
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>自定义衰减曲线关键帧。</summary>
    public struct AttenuationKeyframe
    {
        public float Time;   // 归一化 [0,1]
        public float Value;  // 音量系数 [0,1]

        public AttenuationKeyframe(float time, float value)
        {
            Time = time;
            Value = value;
        }
    }

    /// <summary>自定义衰减曲线（Hermite 平滑插值）。</summary>
    public sealed class AttenuationCurve
    {
        private readonly List<AttenuationKeyframe> _keys = new();
        private bool _sorted = true;

        public int KeyCount => _keys.Count;
        public bool IsEmpty => _keys.Count == 0;

        public void AddKey(float time, float value)
        {
            _keys.Add(new AttenuationKeyframe(time, Math.Clamp(value, 0f, 1f)));
            _sorted = false;
        }

        public void Clear() => _keys.Clear();

        private void EnsureSorted()
        {
            if (!_sorted)
            {
                _keys.Sort((a, b) => a.Time.CompareTo(b.Time));
                _sorted = true;
            }
        }

        /// <summary>在归一化时间 t 处求值（Smoothstep 缓动）。</summary>
        public float Evaluate(float t)
        {
            if (_keys.Count == 0) return 1f;
            EnsureSorted();
            if (_keys.Count == 1) return _keys[0].Value;
            if (t <= _keys[0].Time) return _keys[0].Value;
            if (t >= _keys[^1].Time) return _keys[^1].Value;

            int lo = 0, hi = _keys.Count - 1;
            while (hi - lo > 1)
            {
                int mid = (lo + hi) >> 1;
                if (_keys[mid].Time <= t) lo = mid; else hi = mid;
            }
            var a = _keys[lo];
            var b = _keys[hi];
            float dt = b.Time - a.Time;
            float u = dt > 1e-6f ? (t - a.Time) / dt : 0f;
            float s = u * u * (3f - 2f * u); // Smoothstep
            return a.Value + (b.Value - a.Value) * s;
        }
    }

    /// <summary>锥形衰减设置。</summary>
    public struct ConeSettings
    {
        /// <summary>内锥半角（度）。</summary>
        public float InnerAngle;
        /// <summary>外锥半角（度）。</summary>
        public float OuterAngle;
        /// <summary>外锥之外的音量增益 [0,1]。</summary>
        public float OuterGain;

        public static ConeSettings Default => new ConeSettings
        {
            InnerAngle = 30f,
            OuterAngle = 60f,
            OuterGain = 0.3f
        };
    }

    /// <summary>
    /// 声音衰减设置（对齐 UE5 SoundAttenuation 资产）。
    /// 描述一个声源在 3D 空间中的衰减行为。
    /// </summary>
    public sealed class SoundAttenuationSettings
    {
        /// <summary>内半径（厘米）。此距离内保持全音量。</summary>
        public float InnerRadius = 200f;

        /// <summary>衰减距离（厘米）。到达此距离后音量归零。</summary>
        public float FalloffDistance = 5000f;

        /// <summary>衰减模型。</summary>
        public AttenuationModel AttenuationModel = AttenuationModel.Logarithmic;

        /// <summary>自定义衰减曲线。</summary>
        public AttenuationCurve CustomCurve = new();

        /// <summary>衰减形状。</summary>
        public AttenuationShape AttenuationShape = AttenuationShape.Sphere;

        /// <summary>盒形/胶囊形的等效半范围。</summary>
        public Vector3 ShapeExtents = new Vector3(100f, 100f, 100f);

        /// <summary>锥形衰减参数。</summary>
        public ConeSettings ConeSettings = ConeSettings.Default;

        /// <summary>锥形前向方向。</summary>
        public Vector3 ConeForward = new Vector3(0f, 0f, -1f);

        /// <summary>立体声扩展 [0,1]。</summary>
        public float StereoSpread = 0.5f;

        /// <summary>是否启用多普勒效应。</summary>
        public bool DopplerEnabled = true;

        /// <summary>多普勒强度倍率。</summary>
        public float DopplerFactor = 1f;

        /// <summary>是否启用空气吸收。</summary>
        public bool AirAbsorptionEnabled = false;

        /// <summary>空气吸收强度 [0,1]。</summary>
        public float AirAbsorptionAmount = 0.5f;

        /// <summary>是否启用听者聚焦。</summary>
        public bool ListenerFocusEnabled = false;

        /// <summary>听者聚焦增益倍率。</summary>
        public float ListenerFocusGain = 1.2f;

        /// <summary>听者聚焦半角（度）。</summary>
        public float ListenerFocusAngle = 45f;

        /// <summary>是否启用遮挡。</summary>
        public bool OcclusionEnabled = false;

        /// <summary>被遮挡时的音量衰减系数 [0,1]。</summary>
        public float OcclusionVolumeAttenuation = 0.4f;

        /// <summary>被遮挡时的低通截止频率（Hz）。</summary>
        public float OcclusionLowpassCutoff = 800f;

        /// <summary>混响发送量 [0,1]。</summary>
        public float ReverbSend = 0f;

        /// <summary>优先级。</summary>
        public int Priority = 0;

        // ── 计算方法 ──────────────────────────────────────────────────────────

        /// <summary>
        /// 计算距离衰减音量系数 [0,1]。
        /// 综合距离模型 + 衰减形状（含锥角）。
        /// </summary>
        public float ComputeAttenuation(Vector3 sourcePos, Vector3 listenerPos, Quaternion listenerRot)
        {
            float rawDist = Vector3.Distance(sourcePos, listenerPos);

            // 把体积形状折算为"有效球距"
            float effInner = InnerRadius;
            switch (AttenuationShape)
            {
                case AttenuationShape.Box:
                case AttenuationShape.Capsule:
                    effInner = InnerRadius + Math.Min(ShapeExtents.X, Math.Min(ShapeExtents.Y, ShapeExtents.Z));
                    break;
            }

            float dist = Math.Max(0f, rawDist - effInner);
            float atten;

            if (AttenuationModel == AttenuationModel.None)
            {
                atten = 1f;
            }
            else if (dist <= 0f)
            {
                atten = 1f;
            }
            else if (dist >= FalloffDistance)
            {
                atten = 0f;
            }
            else
            {
                float t = dist / Math.Max(0.0001f, FalloffDistance);
                switch (AttenuationModel)
                {
                    case AttenuationModel.Linear:
                        atten = 1f - t;
                        break;

                    case AttenuationModel.Logarithmic:
                    {
                        float k = 9f;
                        float raw(float x) => 1f / (1f + k * x);
                        float v1 = raw(1f);
                        atten = (raw(t) - v1) / (1f - v1);
                        break;
                    }

                    case AttenuationModel.InverseSquare:
                    {
                        float d = effInner + dist;
                        float raw(float x) => (effInner * effInner) / Math.Max(1e-6f, x * x);
                        float v0 = raw(effInner);
                        float v1 = raw(effInner + FalloffDistance);
                        atten = (raw(d) - v1) / (v0 - v1);
                        break;
                    }

                    case AttenuationModel.Natural:
                    {
                        // 指数衰减 e^(-k*t)，k 控制衰减速度
                        float k = 4.6f; // 使得 t=1 时 atten≈0.01
                        atten = MathF.Exp(-k * t);
                        break;
                    }

                    case AttenuationModel.CustomCurve:
                        atten = CustomCurve.Evaluate(t);
                        break;

                    default:
                        atten = 1f - t;
                        break;
                }
            }

            // 锥形方向性衰减
            if (AttenuationShape == AttenuationShape.Cone)
                atten *= ComputeConeAttenuation(sourcePos, listenerPos);

            return Math.Clamp(atten, 0f, 1f);
        }

        /// <summary>计算锥形方向性衰减系数。</summary>
        private float ComputeConeAttenuation(Vector3 sourcePos, Vector3 listenerPos)
        {
            Vector3 toListener = listenerPos - sourcePos;
            float len = toListener.Length();
            if (len < 1e-4f) return 1f;
            toListener /= len;

            Vector3 fwd = ConeForward;
            float fwdLen = fwd.Length();
            if (fwdLen < 1e-4f) return 1f;
            fwd /= fwdLen;

            float cosAngle = Math.Clamp(Vector3.Dot(fwd, toListener), -1f, 1f);
            float angleDeg = MathF.Acos(cosAngle) * (180f / MathF.PI);

            float inner = ConeSettings.InnerAngle;
            float outer = ConeSettings.OuterAngle;
            if (angleDeg <= inner) return 1f;
            if (angleDeg >= outer) return ConeSettings.OuterGain;
            float k = (angleDeg - inner) / Math.Max(0.0001f, outer - inner);
            return 1f + (ConeSettings.OuterGain - 1f) * k;
        }

        /// <summary>
        /// 计算多普勒音调倍率。
        /// 公式：pitch = (c - v_listener) / (c + v_source)
        /// </summary>
        public float ComputeDopplerPitch(Vector3 sourceVel, Vector3 listenerVel)
        {
            if (!DopplerEnabled || DopplerFactor <= 0f) return 1f;

            const float SpeedOfSound = 343f; // m/s
            float sourceReceding = sourceVel.X;   // + 远离听者
            float listenerReceding = listenerVel.X; // + 远离声源

            float denom = SpeedOfSound + sourceReceding * DopplerFactor;
            float numer = SpeedOfSound - listenerReceding * DopplerFactor;
            if (denom < 1f) denom = 1f;

            return Math.Clamp(numer / denom, 0.5f, 2f);
        }

        /// <summary>计算空气吸收低通截止频率。</summary>
        public float ComputeAirAbsorptionCutoff(float distance)
        {
            if (!AirAbsorptionEnabled || AirAbsorptionAmount <= 0f) return 20000f;

            float t = Math.Clamp(distance / Math.Max(1f, FalloffDistance), 0f, 1f);
            float cutoff = 20000f - t * 19000f * AirAbsorptionAmount;
            return Math.Clamp(cutoff, 1000f, 20000f);
        }

        /// <summary>计算方向性因子 [0,1]。声源前向方向的增益加权。</summary>
        public float ComputeDirectivityFactor(Vector3 sourcePos, Vector3 sourceForward,
            Vector3 sourceUp, Vector3 listenerPos)
        {
            Vector3 toListener = listenerPos - sourcePos;
            float len = toListener.Length();
            if (len < 1e-4f) return 1f;
            toListener /= len;

            float fwdLen = sourceForward.Length();
            if (fwdLen < 1e-4f) return 1f;
            Vector3 fwd = sourceForward / fwdLen;

            // 方向性因子：余弦加权
            float cosA = Math.Clamp(Vector3.Dot(fwd, toListener), -1f, 1f);
            // 映射到 [0.2, 1.0]：正前方=1，正后方=0.2
            return 0.6f + 0.4f * (cosA * 0.5f + 0.5f);
        }

        /// <summary>计算环境混响发送量。距离越远发送量越高。</summary>
        public float ComputeReverbSend(float distance)
        {
            if (ReverbSend <= 0f) return 0f;
            float t = Math.Clamp(distance / Math.Max(1f, FalloffDistance), 0f, 1f);
            return ReverbSend * (0.2f + 0.8f * t); // 近处 20%，远处 100%
        }

        public static SoundAttenuationSettings Default => new SoundAttenuationSettings();
    }

    /// <summary>ECS 组件：把 SoundAttenuationSettings 绑定到实体。</summary>
    public struct SoundAttenuationComponent : IComponent
    {
        public SoundAttenuationSettings Settings;
        public Guid BoundInstanceId;
        public string? ClassName;
        public SoundConcurrency? Concurrency;

        public SoundAttenuationComponent(SoundAttenuationSettings? settings = null,
                                         string? className = null,
                                         SoundConcurrency? concurrency = null)
        {
            Settings = settings ?? SoundAttenuationSettings.Default;
            BoundInstanceId = Guid.Empty;
            ClassName = className;
            Concurrency = concurrency;
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  二、Sound Class 声音类
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 声音类（对齐 UE5 SoundClass）。层级化音量/音调控制：
    /// 最终音量 = 沿父链累乘(Volume) * VolumeAdjuster。
    /// </summary>
    public sealed class SoundClass
    {
        public string Name;
        public float Volume = 1f;
        public float Pitch = 1f;
        public float VolumeAdjuster = 1f;
        public float PitchAdjuster = 1f;
        public bool Muted;
        public bool Solo;
        public float PassiveVolumeMix = 0f;
        public List<SoundClass> ChildClasses = new();
        public SoundClass? Parent;

        public SoundClass(string name) { Name = name; }
    }

    /// <summary>
    /// 声音类树（对齐 UE5 SoundClass 树）。
    /// 默认创建 Master 为根，下挂 Music/SFX/Voice/Ambient/UI。
    /// </summary>
    public sealed class SoundClassTree
    {
        private readonly Dictionary<string, SoundClass> _classes = new();
        public const string RootName = "Master";

        public SoundClassTree()
        {
            Register(RootName, null);
            Register("Music", RootName);
            Register("SFX", RootName);
            Register("Voice", RootName);
            Register("Ambient", RootName);
            Register("UI", RootName);
        }

        public SoundClass Register(string name, string? parent)
        {
            if (string.IsNullOrEmpty(name))
                throw new ArgumentException("SoundClass 名不能为空", nameof(name));
            if (!_classes.TryGetValue(name, out var cls))
            {
                cls = new SoundClass(name);
                _classes[name] = cls;
            }
            if (!string.IsNullOrEmpty(parent) && _classes.TryGetValue(parent, out var parentCls))
            {
                cls.Parent = parentCls;
                if (!parentCls.ChildClasses.Contains(cls))
                    parentCls.ChildClasses.Add(cls);
            }
            return cls;
        }

        public SoundClass? GetClass(string name)
        {
            return _classes.TryGetValue(name, out var cls) ? cls : null;
        }

        public bool HasClass(string name) => _classes.ContainsKey(name);

        public float ComputeFinalVolume(string name)
        {
            if (!_classes.TryGetValue(name, out var cls)) return 1f;
            return ComputeFinalVolume(cls);
        }

        private float ComputeFinalVolume(SoundClass cls)
        {
            float parentVol = cls.Parent != null ? ComputeFinalVolume(cls.Parent) : 1f;
            float vol = parentVol * cls.Volume * cls.VolumeAdjuster;
            if (cls.Muted) return 0f;
            if (AnySoloInTree() && !cls.Solo)
                vol *= cls.PassiveVolumeMix;
            return Math.Clamp(vol, 0f, 2f);
        }

        public float ComputeFinalPitch(string name)
        {
            if (!_classes.TryGetValue(name, out var cls)) return 1f;
            float pitch = 1f;
            for (var c = cls; c != null; c = c.Parent)
                pitch *= c.Pitch * c.PitchAdjuster;
            return pitch;
        }

        public void SetVolume(string name, float volume)
        {
            if (_classes.TryGetValue(name, out var cls))
                cls.Volume = Math.Clamp(volume, 0f, 2f);
        }

        public void SetVolumeAdjuster(string name, float adjuster)
        {
            if (_classes.TryGetValue(name, out var cls))
                cls.VolumeAdjuster = Math.Clamp(adjuster, 0f, 2f);
        }

        public void SetPitchAdjuster(string name, float adjuster)
        {
            if (_classes.TryGetValue(name, out var cls))
                cls.PitchAdjuster = Math.Clamp(adjuster, 0.25f, 4f);
        }

        public void SetMuted(string name, bool muted)
        {
            if (_classes.TryGetValue(name, out var cls)) cls.Muted = muted;
        }

        public void SetSolo(string name, bool solo)
        {
            if (_classes.TryGetValue(name, out var cls)) cls.Solo = solo;
        }

        public bool AnySoloInTree()
        {
            foreach (var cls in _classes.Values)
                if (cls.Solo) return true;
            return false;
        }

        public void ResetAdjusters()
        {
            foreach (var cls in _classes.Values)
            {
                cls.VolumeAdjuster = 1f;
                cls.PitchAdjuster = 1f;
            }
        }

        public IEnumerable<SoundClass> AllClasses => _classes.Values;
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  三、Sound Mix 声音混音
    // ════════════════════════════════════════════════════════════════════════════

    public struct SoundMixClassOverride
    {
        public string ClassName;
        public float Volume;
        public float Pitch;

        public SoundMixClassOverride(string className, float volume = -1f, float pitch = -1f)
        {
            ClassName = className;
            Volume = volume;
            Pitch = pitch;
        }
    }

    /// <summary>
    /// 声音混音（对齐 UE5 SoundMix）。临时覆盖若干声音类的音量/音调，
    /// 支持淡入淡出与持续时间。多个混音以栈式叠加。
    /// </summary>
    public sealed class SoundMix
    {
        public string Name = "SoundMix";
        public List<SoundMixClassOverride> ClassOverrides = new();
        public float FadeInTime = 0.5f;
        public float FadeOutTime = 0.5f;
        public float Duration = 0f;
        public bool bApplyToChildren = true;

        public void AddOverride(string className, float volume = -1f, float pitch = -1f)
        {
            ClassOverrides.Add(new SoundMixClassOverride(className, volume, pitch));
        }
    }

    internal sealed class ActiveMixState
    {
        public SoundMix Mix;
        public float Elapsed;
        public float CurrentWeight;
        public bool Popping;
        public float PopElapsed;

        public ActiveMixState(SoundMix mix) { Mix = mix; }
    }

    /// <summary>
    /// 混音管理器。维护激活混音栈，每帧按淡入淡出插值各声音类的有效音量。
    /// </summary>
    public sealed class SoundMixManager
    {
        private readonly List<ActiveMixState> _stack = new();
        public int ActiveMixCount => _stack.Count;

        public void PushMix(SoundMix mix)
        {
            if (mix == null) return;
            foreach (var s in _stack)
                if (ReferenceEquals(s.Mix, mix)) return;
            _stack.Add(new ActiveMixState(mix));
        }

        public void PopMix(SoundMix mix)
        {
            if (mix == null) return;
            foreach (var s in _stack)
                if (ReferenceEquals(s.Mix, mix)) { s.Popping = true; s.PopElapsed = 0f; break; }
        }

        public void ClearAllMixes() => _stack.Clear();

        public void Update(float dt, SoundClassTree tree)
        {
            if (tree == null) return;

            tree.ResetAdjusters();

            for (int i = _stack.Count - 1; i >= 0; i--)
            {
                var s = _stack[i];
                if (!s.Popping)
                {
                    s.Elapsed += dt;
                    if (s.Mix.FadeInTime <= 0f) s.CurrentWeight = 1f;
                    else s.CurrentWeight = Math.Clamp(s.Elapsed / s.Mix.FadeInTime, 0f, 1f);

                    if (s.Mix.Duration > 0f && s.Elapsed >= s.Mix.Duration)
                    {
                        s.Popping = true;
                        s.PopElapsed = 0f;
                    }
                }
                else
                {
                    s.PopElapsed += dt;
                    if (s.Mix.FadeOutTime <= 0f) s.CurrentWeight = 0f;
                    else
                    {
                        float popT = Math.Clamp(s.PopElapsed / s.Mix.FadeOutTime, 0f, 1f);
                        s.CurrentWeight = (1f - popT) * s.CurrentWeight;
                    }
                    if (s.PopElapsed >= s.Mix.FadeOutTime)
                    {
                        _stack.RemoveAt(i);
                        continue;
                    }
                }
            }

            foreach (var s in _stack)
            {
                if (s.CurrentWeight <= 0f) continue;
                foreach (var ov in s.Mix.ClassOverrides)
                    ApplyOverride(tree, ov, s.Mix.bApplyToChildren, s.CurrentWeight);
            }
        }

        private void ApplyOverride(SoundClassTree tree, SoundMixClassOverride ov, bool applyToChildren, float weight)
        {
            var cls = tree.GetClass(ov.ClassName);
            if (cls == null) return;
            ApplyToClass(cls, ov, weight);
            if (applyToChildren) ApplyToChildrenRecursive(cls, ov, weight);
        }

        private void ApplyToClass(SoundClass cls, SoundMixClassOverride ov, float weight)
        {
            if (ov.Volume >= 0f)
            {
                float cur = cls.VolumeAdjuster;
                cls.VolumeAdjuster = cur + (ov.Volume - cur) * weight;
            }
            if (ov.Pitch >= 0f)
            {
                float cur = cls.PitchAdjuster;
                cls.PitchAdjuster = cur + (ov.Pitch - cur) * weight;
            }
        }

        private void ApplyToChildrenRecursive(SoundClass parent, SoundMixClassOverride ov, float weight)
        {
            foreach (var child in parent.ChildClasses)
            {
                ApplyToClass(child, ov, weight);
                ApplyToChildrenRecursive(child, ov, weight);
            }
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  四、Sound Concurrency 声音并发
    // ════════════════════════════════════════════════════════════════════════════

    public sealed class SoundConcurrency
    {
        public int MaxConcurrency = 8;
        public ConcurrencyResolution Resolution = ConcurrencyResolution.StopOldest;
        public string ConcurrencyGroup = "Default";
        public bool VirtualizeWhenExceeded = false;
    }

    internal sealed class ConcurrencyEntry
    {
        public Guid InstanceId;
        public Vector3 Position;
        public float EffectiveVolume;
        public DateTime StartTime;
        public string Group;
    }

    /// <summary>并发管理器。注册实例并按策略裁剪超限实例。</summary>
    public sealed class SoundConcurrencyManager
    {
        private readonly Dictionary<string, List<ConcurrencyEntry>> _groups = new();
        private readonly Dictionary<string, SoundConcurrency> _settings = new();
        private readonly AudioSystem _audio;

        public SoundConcurrencyManager(AudioSystem audio)
        {
            _audio = audio ?? throw new ArgumentNullException(nameof(audio));
        }

        public void RegisterSettings(SoundConcurrency concurrency)
        {
            if (concurrency == null) return;
            _settings[concurrency.ConcurrencyGroup] = concurrency;
            if (!_groups.ContainsKey(concurrency.ConcurrencyGroup))
                _groups[concurrency.ConcurrencyGroup] = new List<ConcurrencyEntry>();
        }

        public void RegisterInstance(AudioInstance inst, SoundConcurrency concurrency,
            Vector3 position, float effectiveVolume)
        {
            if (inst == null || concurrency == null) return;
            RegisterSettings(concurrency);

            string group = concurrency.ConcurrencyGroup;
            var list = _groups[group];
            list.Add(new ConcurrencyEntry
            {
                InstanceId = inst.Id,
                Position = position,
                EffectiveVolume = effectiveVolume,
                StartTime = DateTime.Now,
                Group = group
            });

            TrimGroup(list, concurrency);
        }

        private void TrimGroup(List<ConcurrencyEntry> list, SoundConcurrency concurrency)
        {
            while (list.Count > concurrency.MaxConcurrency && list.Count > 0)
            {
                ConcurrencyEntry victim = concurrency.Resolution switch
                {
                    ConcurrencyResolution.StopFurthest => FindFurthest(list),
                    ConcurrencyResolution.StopQuietest => FindQuietest(list),
                    ConcurrencyResolution.StopOldest => FindOldest(list),
                    _ => list[0]
                };
                list.Remove(victim);
                _audio.Stop(victim.InstanceId, 0.05f);
            }
        }

        private ConcurrencyEntry FindFurthest(List<ConcurrencyEntry> list)
        {
            var v = list[0];
            foreach (var e in list) if (e.EffectiveVolume < v.EffectiveVolume) v = e;
            return v;
        }

        private ConcurrencyEntry FindQuietest(List<ConcurrencyEntry> list)
        {
            var v = list[0];
            foreach (var e in list) if (e.EffectiveVolume < v.EffectiveVolume) v = e;
            return v;
        }

        private ConcurrencyEntry FindOldest(List<ConcurrencyEntry> list)
        {
            var v = list[0];
            foreach (var e in list) if (e.StartTime < v.StartTime) v = e;
            return v;
        }

        public void Update(float dt, AudioSystem audio, AudioListener? listener)
        {
            foreach (var kvp in _groups)
            {
                kvp.Value.RemoveAll(e => !IsInstanceAlive(audio, e.InstanceId));
            }
        }

        private bool IsInstanceAlive(AudioSystem audio, Guid id)
        {
            foreach (var bs in audio.GetBusStates())
                foreach (var inst in bs.Instances)
                    if (inst.Id == id)
                        return inst.State == AudioState.Playing ||
                               inst.State == AudioState.FadingIn ||
                               inst.State == AudioState.FadingOut;
            return false;
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  五、Voice Virtualization 声音虚拟化
    // ════════════════════════════════════════════════════════════════════════════

    public sealed class InstanceSpatialState
    {
        public Guid InstanceId;
        public float AuthoredVolume = 1f;
        public float AuthoredPitch = 1f;
        public SoundAttenuationSettings Settings;
        public string? ClassName;
        public SoundConcurrency? Concurrency;
        public DateTime RegisterTime;
        public DateTime? FadeOutStartTime;
        public bool FadingIn;
        public bool FadingOut;
        public float FadeInDuration;
        public float FadeOutDuration;
        public bool IsVirtualized;
        public float CachedVolumeFactor = 1f;
        public float CachedDistance;
        public int Priority;
        public int EntityId = -1;
        public Vector3 Position;
        public float CachedReverbSend;
        public float CachedLowpassCutoff;
    }

    /// <summary>
    /// 声音虚拟化系统（对齐 UE5 Voice Virtualization）。
    /// 活跃声音数超过上限时，按优先级 + 距离排序，低优先级声音被虚拟化。
    /// </summary>
    public sealed class VoiceVirtualizationSystem
    {
        public int MaxActiveVoices = 32;
        private readonly AudioSystem _audio;

        public VoiceVirtualizationSystem(AudioSystem audio)
        {
            _audio = audio ?? throw new ArgumentNullException(nameof(audio));
        }

        public void Update(float dt, Dictionary<Guid, InstanceSpatialState> states, AudioListener listener)
        {
            if (listener == null) return;

            var active = new List<InstanceSpatialState>();
            var virtualized = new List<InstanceSpatialState>();

            foreach (var s in states.Values)
            {
                if (!IsAlive(s)) continue;
                if (s.IsVirtualized) virtualized.Add(s);
                else active.Add(s);
            }

            // 排序：优先级（高） + 衰减后音量（高） + 距离（近）
            active.Sort((a, b) => Score(b).CompareTo(Score(a)));
            virtualized.Sort((a, b) => Score(b).CompareTo(Score(a)));

            // 虚拟化超出部分
            while (active.Count > MaxActiveVoices)
            {
                int last = active.Count - 1;
                var v = active[last];
                active.RemoveAt(last);
                Virtualize(v);
                virtualized.Add(v);
            }

            // 尝试恢复虚拟化声音
            for (int i = 0; i < virtualized.Count; i++)
            {
                if (active.Count >= MaxActiveVoices) break;
                var v = virtualized[i];
                if (active.Count == 0 || Score(v) > Score(active[^1]))
                {
                    v.IsVirtualized = false;
                }
            }
        }

        private float Score(InstanceSpatialState s)
        {
            float volScore = s.CachedVolumeFactor;
            float distScore = 1f / (1f + s.CachedDistance * 0.001f);
            float prioScore = s.Priority * 0.01f;
            return volScore * 2f + distScore + prioScore;
        }

        private void Virtualize(InstanceSpatialState s)
        {
            if (s.IsVirtualized) return;
            s.IsVirtualized = true;
            _audio.Stop(s.InstanceId, 0.05f);
        }

        private bool IsAlive(InstanceSpatialState s)
        {
            foreach (var bs in _audio.GetBusStates())
                foreach (var inst in bs.Instances)
                    if (inst.Id == s.InstanceId)
                        return true;
            return false;
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  六、AudioSpatializer 空间化器
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>空间化计算结果。</summary>

    /// <summary>
    /// 空间化器（对齐 UE5 Spatialization）。每帧把衰减 / Doppler / 遮挡 / AirAbsorption
    /// 应用到 Volume / Pitch / Pan。支持 HRTF 近似和立体声声像计算。
    /// </summary>
    public sealed class SoundAttenuator
    {
        public const float SpeedOfSound = 343f;

        /// <summary>是否启用 HRTF 近似。</summary>
        public bool EnableHRTF = false;

        /// <summary>
        /// 对一个实例应用衰减计算，返回空间化结果。
        /// </summary>
        public SpatializationResult ApplyAttenuation(AudioInstance inst,
            SoundAttenuationSettings atten,
            AudioListener listener,
            IPhysicsWorld? physicsForOcclusion)
        {
            var result = new SpatializationResult
            {
                VolumeFactor = 1f,
                PitchFactor = 1f,
                Pan = 0f,
                LowpassCutoff = 20000f,
                ReverbSend = atten.ReverbSend,
                Occluded = false,
                Distance = 0f,
                DirectivityFactor = 1f
            };

            if (inst == null || atten == null || listener == null) return result;

            Vector3 sourcePos = inst.Position;
            Vector3 listenerPos = listener.Position;
            float dist = Vector3.Distance(sourcePos, listenerPos);
            result.Distance = dist;

            // 1. 距离衰减（含形状与锥角）
            float vol = atten.ComputeAttenuation(sourcePos, listenerPos, listener.Rotation);
            result.VolumeFactor = vol;

            // 2. 听者聚焦
            if (atten.ListenerFocusEnabled)
            {
                vol *= ComputeListenerFocus(sourcePos, listener, atten);
                result.VolumeFactor = vol;
            }

            // 3. 方向性因子
            Vector3 listenerForward = Vector3.Transform(new Vector3(0, 0, -1), listener.Rotation);
            Vector3 listenerRight = Vector3.Transform(new Vector3(1, 0, 0), listener.Rotation);
            Vector3 listenerUp = Vector3.Transform(new Vector3(0, 1, 0), listener.Rotation);
            result.DirectivityFactor = atten.ComputeDirectivityFactor(
                sourcePos, listenerForward, listenerUp, listenerPos);

            // 4. 方位角和仰角（用于 HRTF）
            Vector3 toSource = sourcePos - listenerPos;
            Vector3 dir = dist > 1e-4f ? toSource / dist : Vector3.UnitZ;

            float azimuth = MathF.Atan2(
                Vector3.Dot(dir, listenerRight),
                Vector3.Dot(dir, -listenerForward));
            float elevation = MathF.Asin(Math.Clamp(dir.Y, -1f, 1f));
            result.AzimuthRad = azimuth;
            result.ElevationRad = elevation;

            // 5. 遮挡 + 空气吸收
            float cutoff = 20000f;
            if (atten.AirAbsorptionEnabled)
                cutoff = atten.ComputeAirAbsorptionCutoff(dist);

            if (atten.OcclusionEnabled && physicsForOcclusion != null)
            {
                bool occluded = OcclusionTrace(sourcePos, listenerPos, physicsForOcclusion);
                result.Occluded = occluded;
                if (occluded)
                {
                    result.VolumeFactor *= atten.OcclusionVolumeAttenuation;
                    cutoff = Math.Min(cutoff, atten.OcclusionLowpassCutoff);
                }
            }
            result.LowpassCutoff = cutoff;

            // 6. 多普勒
            Vector3 toListenerVec = dist > 1e-4f ? (listenerPos - sourcePos) / dist : Vector3.UnitZ;
            float sourceReceding = -Vector3.Dot(inst.Velocity, toListenerVec);
            float listenerReceding = Vector3.Dot(listener.Velocity, toListenerVec);
            var sRadial = new Vector3(sourceReceding, 0f, 0f);
            var lRadial = new Vector3(listenerReceding, 0f, 0f);
            result.PitchFactor = atten.ComputeDopplerPitch(sRadial, lRadial);

            // 7. 声像（HRTF 近似或简单立体声声像）
            if (EnableHRTF && dist > 1e-4f)
            {
                // HRTF 近似：基于方位角的 ILD + ITD
                float itdFactor = MathF.Cos(azimuth); // [-1, 1]
                result.Pan = Math.Clamp(itdFactor * 0.8f, -1f, 1f);
                // 方位角影响音量（头影效应近似）
                result.VolumeFactor *= (0.85f + 0.15f * MathF.Abs(MathF.Sin(elevation)));
            }
            else
            {
                result.Pan = ComputePan(sourcePos, listener, atten.StereoSpread);
            }

            // 8. 环境混响发送量
            result.ReverbSend = atten.ComputeReverbSend(dist);

            result.VolumeFactor = Math.Clamp(result.VolumeFactor, 0f, 1f);
            return result;
        }

        /// <summary>计算立体声声像 [-1,1]。</summary>
        public float ComputePan(Vector3 sourcePos, AudioListener listener, float spread = 1f)
        {
            Vector3 toSource = sourcePos - listener.Position;
            float dist = toSource.Length();
            if (dist < 1e-4f) return 0f;

            Vector3 right = Vector3.Transform(new Vector3(1f, 0f, 0f), listener.Rotation);
            float rLen = right.Length();
            if (rLen < 1e-4f) return 0f;
            right /= rLen;

            float pan = Vector3.Dot(toSource, right) / dist;
            return Math.Clamp(pan * spread, -1f, 1f);
        }

        /// <summary>听者聚焦增益。</summary>
        private float ComputeListenerFocus(Vector3 sourcePos, AudioListener listener,
            SoundAttenuationSettings atten)
        {
            Vector3 toSource = sourcePos - listener.Position;
            float len = toSource.Length();
            if (len < 1e-4f) return 1f;
            toSource /= len;

            Vector3 fwd = Vector3.Transform(new Vector3(0f, 0f, -1), listener.Rotation);
            float fLen = fwd.Length();
            if (fLen < 1e-4f) return 1f;
            fwd /= fLen;

            float cosA = Math.Clamp(Vector3.Dot(fwd, toSource), -1f, 1f);
            float angleDeg = MathF.Acos(cosA) * (180f / MathF.PI);
            if (angleDeg >= 90f) return 1f;
            if (angleDeg <= atten.ListenerFocusAngle) return atten.ListenerFocusGain;
            float k = (90f - angleDeg) / Math.Max(0.0001f, 90f - atten.ListenerFocusAngle);
            return 1f + (atten.ListenerFocusGain - 1f) * k;
        }

        /// <summary>遮挡射线检测。</summary>
        public bool OcclusionTrace(Vector3 sourcePos, Vector3 listenerPos, IPhysicsWorld physics)
        {
            if (physics == null) return false;
            Vector3 dir = listenerPos - sourcePos;
            float dist = dir.Length();
            if (dist < 0.05f) return false;
            dir /= dist;

            Vector3 origin = sourcePos + dir * 0.05f;
            bool hit = physics.Raycast(
                (PhysicsVector3)origin,
                (PhysicsVector3)dir,
                dist - 0.1f,
                out var hitData);
            return hit;
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  七、SoundCue 声音提示（轻量节点图）
    // ════════════════════════════════════════════════════════════════════════════

    public sealed class SoundCueResult
    {
        public AudioClip? Clip;
        public float Volume = 1f;
        public float Pitch = 1f;
        public bool Loop;
        public float Delay;
        public SoundAttenuationSettings? Attenuation;
        public SoundConcurrency? Concurrency;
        public string? ClassName;
        public int Priority;
    }

    public abstract class SoundCueNode
    {
        public SoundCueNodeType Type;
        public abstract SoundCueResult Evaluate();
    }

    public sealed class WavePlayerNode : SoundCueNode
    {
        public AudioClip? Clip;
        public float Volume = 1f;
        public float Pitch = 1f;

        public WavePlayerNode(AudioClip? clip = null) { Type = SoundCueNodeType.WavePlayer; Clip = clip; }
        public override SoundCueResult Evaluate()
        {
            return new SoundCueResult { Clip = Clip, Volume = Volume, Pitch = Pitch };
        }
    }

    public sealed class RandomNode : SoundCueNode
    {
        public List<SoundCueNode> Inputs = new();
        private readonly Random _rng = new();
        public RandomNode() { Type = SoundCueNodeType.Random; }
        public override SoundCueResult Evaluate()
        {
            if (Inputs.Count == 0) return new SoundCueResult();
            var node = Inputs[_rng.Next(Inputs.Count)];
            return node?.Evaluate() ?? new SoundCueResult();
        }
    }

    public sealed class LoopNode : SoundCueNode
    {
        public SoundCueNode? Input;
        public LoopNode(SoundCueNode? input = null) { Type = SoundCueNodeType.Loop; Input = input; }
        public override SoundCueResult Evaluate()
        {
            var r = Input?.Evaluate() ?? new SoundCueResult();
            r.Loop = true;
            return r;
        }
    }

    public sealed class DelayNode : SoundCueNode
    {
        public SoundCueNode? Input;
        public float Delay = 0f;
        public DelayNode(SoundCueNode? input = null, float delay = 0f)
        { Type = SoundCueNodeType.Delay; Input = input; Delay = delay; }
        public override SoundCueResult Evaluate()
        {
            var r = Input?.Evaluate() ?? new SoundCueResult();
            r.Delay += Delay;
            return r;
        }
    }

    public sealed class ModulatorNode : SoundCueNode
    {
        public SoundCueNode? Input;
        public float VolumeModulation = 0f;
        public float PitchModulation = 0f;
        private readonly Random _rng = new();
        public ModulatorNode(SoundCueNode? input = null) { Type = SoundCueNodeType.Modulator; Input = input; }
        public override SoundCueResult Evaluate()
        {
            var r = Input?.Evaluate() ?? new SoundCueResult();
            if (VolumeModulation > 0f)
                r.Volume *= 1f + ((float)_rng.NextDouble() * 2f - 1f) * VolumeModulation;
            if (PitchModulation > 0f)
                r.Pitch *= 1f + ((float)_rng.NextDouble() * 2f - 1f) * PitchModulation;
            return r;
        }
    }

    public sealed class AttenuationNode : SoundCueNode
    {
        public SoundCueNode? Input;
        public SoundAttenuationSettings? Attenuation;
        public AttenuationNode(SoundCueNode? input = null, SoundAttenuationSettings? atten = null)
        { Type = SoundCueNodeType.Attenuation; Input = input; Attenuation = atten; }
        public override SoundCueResult Evaluate()
        {
            var r = Input?.Evaluate() ?? new SoundCueResult();
            r.Attenuation = Attenuation;
            if (Attenuation != null) r.Priority = Attenuation.Priority;
            return r;
        }
    }

    public sealed class ConcurrencyNode : SoundCueNode
    {
        public SoundCueNode? Input;
        public SoundConcurrency? Concurrency;
        public ConcurrencyNode(SoundCueNode? input = null, SoundConcurrency? concurrency = null)
        { Type = SoundCueNodeType.Concurrency; Input = input; Concurrency = concurrency; }
        public override SoundCueResult Evaluate()
        {
            var r = Input?.Evaluate() ?? new SoundCueResult();
            r.Concurrency = Concurrency;
            return r;
        }
    }

    public sealed class ClassNode : SoundCueNode
    {
        public SoundCueNode? Input;
        public string? ClassName;
        public ClassNode(SoundCueNode? input = null, string? className = null)
        { Type = SoundCueNodeType.Class; Input = input; ClassName = className; }
        public override SoundCueResult Evaluate()
        {
            var r = Input?.Evaluate() ?? new SoundCueResult();
            r.ClassName = ClassName;
            return r;
        }
    }

    public sealed class MixerNode : SoundCueNode
    {
        public List<SoundCueNode> Inputs = new();
        public MixerNode() { Type = SoundCueNodeType.Mixer; }
        public override SoundCueResult Evaluate()
        {
            foreach (var node in Inputs)
            {
                if (node == null) continue;
                var r = node.Evaluate();
                if (r?.Clip != null) return r;
            }
            return new SoundCueResult();
        }
    }

    /// <summary>
    /// 声音提示（对齐 UE5 SoundCue）。持有根节点，求值后得到最终播放参数。
    /// </summary>
    public sealed class SoundCue
    {
        public SoundCueNode? Root;
        public string Name = "SoundCue";

        public SoundCueResult Evaluate()
        {
            return Root?.Evaluate() ?? new SoundCueResult();
        }

        public static SoundCue FromClip(AudioClip clip, SoundAttenuationSettings? atten = null, string? className = null)
        {
            var cue = new SoundCue { Root = new WavePlayerNode(clip) };
            if (atten != null || className != null)
                cue.Root = new ClassNode(new AttenuationNode(cue.Root, atten), className);
            return cue;
        }
    }

    /// <summary>SoundCue 播放器。</summary>
    public sealed class SoundCuePlayer
    {
        private readonly AudioSystem _audio;
        private readonly SoundAttenuationSystem _attenuationSystem;

        public SoundCuePlayer(AudioSystem audio, SoundAttenuationSystem attenuationSystem)
        {
            _audio = audio ?? throw new ArgumentNullException(nameof(audio));
            _attenuationSystem = attenuationSystem ?? throw new ArgumentNullException(nameof(attenuationSystem));
        }

        public AudioInstance? Play(SoundCue cue, Vector3 position)
        {
            if (cue == null) return null;
            var result = cue.Evaluate();
            if (result?.Clip == null) return null;
            return _attenuationSystem.PlayWithAttenuation(cue, position);
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  八、SoundAttenuationSystem 主系统（ECS ISystem）
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 声音衰减系统（对齐 UE5 Audio Engine 顶层调度）。实现 ISystem。
    /// 每帧：取 AudioSystem 的所有实例 + 监听者，对带 SoundAttenuationComponent
    /// 的实体应用衰减；驱动 SoundMixManager / SoundConcurrencyManager /
    /// VoiceVirtualizationSystem / AudioSpatializer。
    /// </summary>
    public sealed class SoundAttenuationSystem : ISystem
    {
        public string Name => "SoundAttenuationSystem";

        private readonly AudioSystem _audio;
        private readonly SoundClassTree _classTree;
        private readonly SoundMixManager _mixManager;
        private readonly SoundConcurrencyManager _concurrencyManager;
        private readonly VoiceVirtualizationSystem _virtualization;
        private readonly SoundAttenuator _spatializer;

        private readonly Dictionary<Guid, InstanceSpatialState> _states = new();

        public SoundAttenuationSettings DefaultSettings = SoundAttenuationSettings.Default;
        public IPhysicsWorld? PhysicsWorld;

        public AudioListener Listener = new AudioListener
        {
            Position = Vector3.Zero,
            Rotation = Quaternion.Identity,
            IsActive = true
        };

        public SoundClassTree ClassTree => _classTree;
        public SoundMixManager MixManager => _mixManager;
        public SoundConcurrencyManager ConcurrencyManager => _concurrencyManager;
        public VoiceVirtualizationSystem Virtualization => _virtualization;
        public SoundAttenuator Spatializer => _spatializer;
        public SoundCuePlayer CuePlayer { get; }

        public SoundAttenuationSystem(AudioSystem audio)
        {
            _audio = audio ?? throw new ArgumentNullException(nameof(audio));
            _classTree = new SoundClassTree();
            _mixManager = new SoundMixManager();
            _concurrencyManager = new SoundConcurrencyManager(_audio);
            _virtualization = new VoiceVirtualizationSystem(_audio);
            _spatializer = new SoundAttenuator();
            CuePlayer = new SoundCuePlayer(_audio, this);
        }

        public void Update(float dt, EntityComponentSystem ecs)
        {
            if (ecs == null) return;

            // 1. 同步监听者到 AudioSystem
            if (Listener != null && Listener.IsActive)
                _audio.SetListenerPosition(Listener.Position, Listener.Rotation);

            // 2. 更新混音管理器
            _mixManager.Update(dt, _classTree);

            // 3. 同步带 SoundAttenuationComponent 的实体
            SyncEntityBoundInstances(ecs);

            // 4. 空间化所有实例
            SpatializeInstances(dt);

            // 5. 更新并发管理器
            _concurrencyManager.Update(dt, _audio, Listener);

            // 6. 更新虚拟化
            _virtualization.Update(dt, _states, Listener);

            // 7. 清理已停止状态
            CleanupStoppedStates();
        }

        private void SyncEntityBoundInstances(EntityComponentSystem ecs)
        {
            var entities = ecs.QueryWithComponent<SoundAttenuationComponent>();
            foreach (var entityId in entities)
            {
                if (!ecs.HasComponent<TransformComponent>(entityId)) continue;
                var comp = ecs.GetComponent<SoundAttenuationComponent>(entityId);
                var transform = ecs.GetComponent<TransformComponent>(entityId);

                if (comp.Settings != null && comp.Settings.AttenuationShape == AttenuationShape.Cone)
                    comp.Settings.ConeForward = Vector3.Transform(new Vector3(0f, 0f, -1f), transform.Rotation);

                if (comp.BoundInstanceId != Guid.Empty)
                {
                    _audio.UpdateInstancePosition(comp.BoundInstanceId, transform.Position);
                    if (_states.TryGetValue(comp.BoundInstanceId, out var state))
                    {
                        state.Position = transform.Position;
                        state.Settings = comp.Settings ?? DefaultSettings;
                        state.ClassName = comp.ClassName;
                        state.EntityId = entityId;
                    }
                }
                ecs.SetComponent(entityId, comp);
            }
        }

        private void SpatializeInstances(float dt)
        {
            var listener = Listener;
            if (listener == null) return;

            foreach (var bs in _audio.GetBusStates())
            {
                foreach (var inst in bs.Instances)
                {
                    if (!_states.TryGetValue(inst.Id, out var state))
                        continue;

                    if (state.IsVirtualized)
                    {
                        state.Position = inst.Position;
                        state.CachedDistance = Vector3.Distance(inst.Position, listener.Position);
                        continue;
                    }

                    var settings = state.Settings ?? DefaultSettings;

                    // 中和 AudioSystem 内建 3D 衰减
                    inst.RolloffFactor = 0f;
                    inst.MaxDistance = float.MaxValue;
                    inst.Is3D = true;

                    var r = _spatializer.ApplyAttenuation(inst, settings, listener, PhysicsWorld);

                    float fadeFactor = ComputeFadeFactor(inst, state);

                    float classVol = 1f;
                    float classPitch = 1f;
                    if (!string.IsNullOrEmpty(state.ClassName))
                    {
                        classVol = _classTree.ComputeFinalVolume(state.ClassName);
                        classPitch = _classTree.ComputeFinalPitch(state.ClassName);
                    }

                    inst.Volume = state.AuthoredVolume * fadeFactor * classVol * r.VolumeFactor;
                    inst.Pitch = state.AuthoredPitch * classPitch * r.PitchFactor;
                    inst.Pan = r.Pan;

                    // 缓存用于虚拟化排序
                    state.CachedVolumeFactor = r.VolumeFactor;
                    state.CachedDistance = r.Distance;
                    state.CachedReverbSend = r.ReverbSend;
                    state.CachedLowpassCutoff = r.LowpassCutoff;
                    state.Position = inst.Position;

                    if (state.FadingOut && fadeFactor <= 0f)
                        inst.State = AudioState.Stopped;
                }
            }
        }

        private float ComputeFadeFactor(AudioInstance inst, InstanceSpatialState state)
        {
            if (inst.State == AudioState.FadingIn && !state.FadingIn && !state.FadingOut)
            {
                state.FadingIn = true;
                state.FadeInDuration = Math.Max(0.001f, inst.FadeDuration);
                state.RegisterTime = inst.StartTime;
                inst.State = AudioState.Playing;
            }

            if (inst.State == AudioState.FadingOut && !state.FadingOut)
            {
                state.FadingOut = true;
                state.FadeOutStartTime = DateTime.Now;
                state.FadeOutDuration = Math.Max(0.001f, inst.FadeDuration);
                inst.State = AudioState.Playing;
            }

            if (state.FadingOut)
            {
                float elapsed = (float)(DateTime.Now - (state.FadeOutStartTime ?? DateTime.Now)).TotalSeconds;
                return Math.Clamp(1f - elapsed / state.FadeOutDuration, 0f, 1f);
            }

            if (state.FadingIn)
            {
                float elapsed = (float)(DateTime.Now - state.RegisterTime).TotalSeconds;
                float f = Math.Clamp(elapsed / state.FadeInDuration, 0f, 1f);
                if (f >= 1f) state.FadingIn = false;
                return f;
            }

            return 1f;
        }

        private void CleanupStoppedStates()
        {
            if (_states.Count == 0) return;
            var aliveIds = new HashSet<Guid>();
            foreach (var bs in _audio.GetBusStates())
                foreach (var inst in bs.Instances)
                    aliveIds.Add(inst.Id);

            var toRemove = new List<Guid>();
            foreach (var kvp in _states)
                if (!aliveIds.Contains(kvp.Key))
                    toRemove.Add(kvp.Key);
            foreach (var id in toRemove)
                _states.Remove(id);
        }

        public AudioInstance? PlayWithAttenuation(SoundCue cue, Vector3 position)
        {
            if (cue == null) return null;
            var result = cue.Evaluate();
            if (result?.Clip == null) return null;

            var inst = _audio.PlayOneShot(result.Clip, position, result.Volume, result.Pitch);
            if (inst == null) return null;

            var state = new InstanceSpatialState
            {
                InstanceId = inst.Id,
                AuthoredVolume = result.Volume,
                AuthoredPitch = result.Pitch,
                Settings = result.Attenuation ?? DefaultSettings,
                ClassName = result.ClassName,
                Concurrency = result.Concurrency,
                RegisterTime = DateTime.Now,
                Priority = result.Priority,
                Position = position
            };
            _states[inst.Id] = state;

            inst.RolloffFactor = 0f;
            inst.MaxDistance = float.MaxValue;
            inst.Is3D = true;
            inst.Priority = result.Priority;

            if (result.Concurrency != null)
                _concurrencyManager.RegisterInstance(inst, result.Concurrency, position, result.Volume);

            return inst;
        }

        public AudioInstance? PlayWithAttenuation(AudioClip? clip, Vector3 position,
            SoundAttenuationSettings? atten = null, string? className = null,
            SoundConcurrency? concurrency = null, float volume = 1f, float pitch = 1f)
        {
            if (clip == null) return null;
            var inst = _audio.PlayOneShot(clip, position, volume, pitch);
            if (inst == null) return null;

            var state = new InstanceSpatialState
            {
                InstanceId = inst.Id,
                AuthoredVolume = volume,
                AuthoredPitch = pitch,
                Settings = atten ?? DefaultSettings,
                ClassName = className,
                Concurrency = concurrency,
                RegisterTime = DateTime.Now,
                Position = position
            };
            _states[inst.Id] = state;

            inst.RolloffFactor = 0f;
            inst.MaxDistance = float.MaxValue;
            inst.Is3D = true;

            if (concurrency != null)
                _concurrencyManager.RegisterInstance(inst, concurrency, position, volume);

            return inst;
        }

        public AudioInstance? PlayForEntity(int entityId, EntityComponentSystem ecs,
            SoundCue cue, Vector3? positionOverride = null)
        {
            if (!ecs.HasComponent<SoundAttenuationComponent>(entityId)) return null;
            if (!ecs.HasComponent<TransformComponent>(entityId)) return null;

            var transform = ecs.GetComponent<TransformComponent>(entityId);
            Vector3 pos = positionOverride ?? transform.Position;

            var inst = PlayWithAttenuation(cue, pos);
            if (inst == null) return null;

            var comp = ecs.GetComponent<SoundAttenuationComponent>(entityId);
            comp.BoundInstanceId = inst.Id;
            ecs.SetComponent(entityId, comp);

            if (_states.TryGetValue(inst.Id, out var state))
            {
                state.EntityId = entityId;
                state.Settings = comp.Settings ?? DefaultSettings;
                state.ClassName = comp.ClassName ?? state.ClassName;
                state.Concurrency = comp.Concurrency ?? state.Concurrency;
            }
            return inst;
        }

        public void SyncClassVolumesToBuses()
        {
            _audio.SetBusVolume(AudioBus.Master, _classTree.ComputeFinalVolume("Master"));
            _audio.SetBusVolume(AudioBus.Music, _classTree.ComputeFinalVolume("Music"));
            _audio.SetBusVolume(AudioBus.SFX, _classTree.ComputeFinalVolume("SFX"));
            _audio.SetBusVolume(AudioBus.Voice, _classTree.ComputeFinalVolume("Voice"));
            _audio.SetBusVolume(AudioBus.Ambient, _classTree.ComputeFinalVolume("Ambient"));
            _audio.SetBusVolume(AudioBus.UI, _classTree.ComputeFinalVolume("UI"));
        }

        public void StopInstance(Guid instanceId, float fadeOut = 0f)
        {
            if (_states.TryGetValue(instanceId, out var state))
            {
                if (fadeOut > 0f)
                {
                    state.FadingOut = true;
                    state.FadeOutStartTime = DateTime.Now;
                    state.FadeOutDuration = fadeOut;
                }
                else
                {
                    _audio.Stop(instanceId, 0f);
                }
            }
            else
            {
                _audio.Stop(instanceId, fadeOut);
            }
        }

        public int SpatializedInstanceCount => _states.Count;
    }
}
