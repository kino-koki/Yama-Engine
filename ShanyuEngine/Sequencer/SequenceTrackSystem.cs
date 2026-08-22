using System;
using System.Collections.Generic;
using System.Numerics;

namespace ShanyuEngine.Sequencer
{
    // ════════════════════════════════════════════════════════════════════════
    // 山雨引擎 - 序列器轨道系统 (Sequence Track System)
    // --------------------------------------------------------------------------
    // 提供所有内置轨道类型及其片段实现：
    //   Transform / Animation / Audio / Camera / Event / Visibility /
    //   Material / Light / Particle / GameplayTag / CinematicShot / Subtitle /
    //   MasterTrack
    //   以及多片段重叠混合系统。
    // ════════════════════════════════════════════════════════════════════════

    // ──────────────────────────────────────────────────────────────────────────
    // TransformTrack / TransformSection - 变换轨道
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>变换片段：驱动对象的位置/旋转/缩放。</summary>
    public class TransformSection : SequenceSection
    {
        public override bool CanBlend => true;

        /// <summary>位置曲线关键帧列表（时间, Vector3）。</summary>
        public List<(double time, Vector3 value)> PositionKeys { get; set; } = new();

        /// <summary>旋转曲线关键帧列表（时间, Quaternion）。</summary>
        public List<(double time, Quaternion value)> RotationKeys { get; set; } = new();

        /// <summary>缩放曲线关键帧列表（时间, Vector3）。</summary>
        public List<(double time, Vector3 value)> ScaleKeys { get; set; } = new();

        /// <summary>是否使用相对偏移。</summary>
        public bool UseRelativeOffset { get; set; }

        /// <summary>评估结果（上次评估的位置）。</summary>
        public Vector3 EvaluatedPosition { get; private set; }
        public Quaternion EvaluatedRotation { get; private set; } = Quaternion.Identity;
        public Vector3 EvaluatedScale { get; private set; } = Vector3.One;

        /// <summary>应用变换的回调。</summary>
        public Action<object?, Vector3, Quaternion, Vector3>? ApplyTransform { get; set; }

        public TransformSection() { Color = 0xFF44AA44; }

        public override void Evaluate(double globalTime, object? context)
        {
            if (!IsEnabled || !ContainsTime(globalTime)) return;
            double local = GetLocalTime(globalTime);
            if (local < 0) return;

            var target = OwnerTrack?.BoundObject ?? context;
            EvaluatedPosition = SampleVector3(PositionKeys, local);
            EvaluatedRotation = SampleQuaternion(RotationKeys, local);
            EvaluatedScale = SampleVector3(ScaleKeys, local, Vector3.One);

            ApplyTransform?.Invoke(target, EvaluatedPosition, EvaluatedRotation, EvaluatedScale);
        }

        /// <summary>添加位置关键帧。</summary>
        public void AddPositionKey(double time, Vector3 value)
        {
            PositionKeys.Add((time, value));
            PositionKeys.Sort((a, b) => a.time.CompareTo(b.time));
        }

        /// <summary>添加旋转关键帧。</summary>
        public void AddRotationKey(double time, Quaternion value)
        {
            RotationKeys.Add((time, value));
            RotationKeys.Sort((a, b) => a.time.CompareTo(b.time));
        }

        /// <summary>添加缩放关键帧。</summary>
        public void AddScaleKey(double time, Vector3 value)
        {
            ScaleKeys.Add((time, value));
            ScaleKeys.Sort((a, b) => a.time.CompareTo(b.time));
        }

        // 简单线性采样
        internal static Vector3 SampleVector3(List<(double time, Vector3 value)> keys, double time, Vector3 defaultValue = default)
        {
            if (keys.Count == 0) return defaultValue;
            if (keys.Count == 1) return keys[0].value;

            if (time <= keys[0].time) return keys[0].value;
            if (time >= keys[keys.Count - 1].time) return keys[keys.Count - 1].value;

            for (int i = 0; i < keys.Count - 1; i++)
            {
                if (time >= keys[i].time && time <= keys[i + 1].time)
                {
                    double range = keys[i + 1].time - keys[i].time;
                    if (range < 1e-8) return keys[i].value;
                    float t = (float)((time - keys[i].time) / range);
                    return Vector3.Lerp(keys[i].value, keys[i + 1].value, t);
                }
            }
            return defaultValue;
        }

        internal static Quaternion SampleQuaternion(List<(double time, Quaternion value)> keys, double time)
        {
            if (keys.Count == 0) return Quaternion.Identity;
            if (keys.Count == 1) return keys[0].value;

            if (time <= keys[0].time) return keys[0].value;
            if (time >= keys[keys.Count - 1].time) return keys[keys.Count - 1].value;

            for (int i = 0; i < keys.Count - 1; i++)
            {
                if (time >= keys[i].time && time <= keys[i + 1].time)
                {
                    double range = keys[i + 1].time - keys[i].time;
                    if (range < 1e-8) return keys[i].value;
                    float t = (float)((time - keys[i].time) / range);
                    return Quaternion.Slerp(keys[i].value, keys[i + 1].value, t);
                }
            }
            return Quaternion.Identity;
        }
    }

    /// <summary>变换轨道：承载 TransformSection。</summary>
    public class TransformTrack : SequenceTrack
    {
        public override string TrackTypeName => "Transform";
        public TransformTrack() { Color = 0xFF44AA44; Name = "Transform"; }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // AnimationTrack / AnimationSection - 动画轨道
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>动画片段：播放骨骼动画或蒙太奇。</summary>
    public class AnimationSection : SequenceSection
    {
        public override bool CanBlend => true;

        /// <summary>动画资产引用。</summary>
        public object? AnimationAsset { get; set; }

        /// <summary>动画名称。</summary>
        public string AnimationName { get; set; } = string.Empty;

        /// <summary>混合入时间（秒）。</summary>
        public double BlendInTime { get; set; } = 0.25;

        /// <summary>混合出时间（秒）。</summary>
        public double BlendOutTime { get; set; } = 0.25;

        /// <summary>动画权重（0.0~1.0）。</summary>
        public float AnimationWeight { get; set; } = 1.0f;

        /// <summary>是否覆盖（不与现有动画混合）。</summary>
        public bool OverrideExisting { get; set; }

        /// <summary>相对偏移：动画从指定时间点开始播放。</summary>
        public double StartOffset { get; set; }

        /// <summary>播放速率。</summary>
        public float PlayRate { get; set; } = 1.0f;

        /// <summary>播放动画回调。</summary>
        public Action<object?, string, float, float>? PlayAnimation { get; set; }

        /// <summary>停止动画回调。</summary>
        public Action<object?, string, float>? StopAnimation { get; set; }

        public AnimationSection() { Color = 0xFF8855CC; }

        public override void Evaluate(double globalTime, object? context)
        {
            if (!IsEnabled || !ContainsTime(globalTime)) return;
            double local = GetLocalTime(globalTime);
            if (local < 0) return;

            var target = OwnerTrack?.BoundObject ?? context;

            // 计算混合权重
            float weight = AnimationWeight;
            double elapsed = globalTime - StartTime;

            if (elapsed < BlendInTime && BlendInTime > 0)
                weight *= (float)(elapsed / BlendInTime);

            double remaining = EndTime - globalTime;
            if (remaining < BlendOutTime && BlendOutTime > 0)
                weight *= (float)(remaining / BlendOutTime);

            weight = Math.Clamp(weight, 0f, 1f);

            PlayAnimation?.Invoke(target, AnimationName, weight * BlendWeight, PlayRate);
        }

        public override void OnExit()
        {
            var target = OwnerTrack?.BoundObject ?? null;
            StopAnimation?.Invoke(target, AnimationName, 0f);
        }
    }

    /// <summary>动画轨道：承载 AnimationSection。</summary>
    public class AnimationTrack : SequenceTrack
    {
        public override string TrackTypeName => "Animation";
        public AnimationTrack() { Color = 0xFF8855CC; Name = "Animation"; }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // AudioTrack / AudioSection - 音频轨道
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>音频片段：播放音频事件或音效。</summary>
    public class AudioSection : SequenceSection
    {
        /// <summary>音频事件引用。</summary>
        public object? AudioEvent { get; set; }

        /// <summary>音频资产路径。</summary>
        public string AudioAssetPath { get; set; } = string.Empty;

        /// <summary>音量（0.0~1.0）。</summary>
        public float Volume { get; set; } = 1.0f;

        /// <summary>音调缩放。</summary>
        public float Pitch { get; set; } = 1.0f;

        /// <summary>淡入时间（秒）。</summary>
        public double FadeInDuration { get; set; }

        /// <summary>淡出时间（秒）。</summary>
        public double FadeOutDuration { get; set; }

        /// <summary>是否循环。</summary>
        public bool LoopAudio { get; set; }

        /// <summary>当前淡入后的音量。</summary>
        public float CurrentVolume { get; private set; }

        /// <summary>播放音频回调。</summary>
        public Action<object?, string, float, float>? PlayAudio { get; set; }

        /// <summary>停止音频回调。</summary>
        public Action? StopAudio { get; set; }

        /// <summary>设置音量回调。</summary>
        public Action<float>? SetVolume { get; set; }

        public AudioSection() { Color = 0xFF4488FF; }

        public override void Evaluate(double globalTime, object? context)
        {
            if (!IsEnabled || !ContainsTime(globalTime)) return;

            double elapsed = globalTime - StartTime;
            float effectiveVolume = Volume * BlendWeight;

            // 淡入
            if (FadeInDuration > 0 && elapsed < FadeInDuration)
                effectiveVolume *= (float)(elapsed / FadeInDuration);

            // 淡出
            double remaining = EndTime - globalTime;
            if (FadeOutDuration > 0 && remaining < FadeOutDuration)
                effectiveVolume *= (float)(remaining / FadeOutDuration);

            CurrentVolume = Math.Clamp(effectiveVolume, 0f, 1f);
            SetVolume?.Invoke(CurrentVolume);
        }

        public override void OnEnter()
        {
            PlayAudio?.Invoke(
                OwnerTrack?.BoundObject ?? null,
                AudioAssetPath, Volume * BlendWeight, Pitch);
        }

        public override void OnExit()
        {
            StopAudio?.Invoke();
        }
    }

    /// <summary>音频轨道：承载 AudioSection。</summary>
    public class AudioTrack : SequenceTrack
    {
        public override string TrackTypeName => "Audio";
        public AudioTrack() { Color = 0xFF4488FF; Name = "Audio"; }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // CameraTrack / CameraSection - 相机轨道
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>相机片段：控制相机切换和相机参数动画。</summary>
    public class CameraSection : SequenceSection
    {
        public override bool CanBlend => true;

        /// <summary>相机引用。</summary>
        public object? CameraReference { get; set; }

        /// <summary>相机名称。</summary>
        public string CameraName { get; set; } = string.Empty;

        /// <summary>FOV（视场角）。</summary>
        public float FieldOfView { get; set; } = 60.0f;

        /// <summary>是否动画 FOV。</summary>
        public bool AnimateFOV { get; set; }

        /// <summary>FOV 动画关键帧。</summary>
        public List<(double time, float fov)> FovKeys { get; set; } = new();

        /// <summary>后期效果配置引用。</summary>
        public object? PostProcessSettings { get; set; }

        /// <summary>过渡到下一相机的时间（秒）。</summary>
        public double TransitionTime { get; set; } = 0.5;

        /// <summary>过渡曲线类型。</summary>
        public string TransitionCurveType { get; set; } = "EaseInOut";

        /// <summary>切换相机回调。</summary>
        public Action<object?, double, string>? SwitchCamera { get; set; }

        /// <summary>设置 FOV 回调。</summary>
        public Action<float>? SetFOV { get; set; }

        public CameraSection() { Color = 0xFFFF6644; }

        public override void Evaluate(double globalTime, object? context)
        {
            if (!IsEnabled || !ContainsTime(globalTime)) return;

            // FOV 动画
            if (AnimateFOV && FovKeys.Count > 0)
            {
                double local = GetLocalTime(globalTime);
                float fov = SampleFloat(FovKeys, local, FieldOfView);
                SetFOV?.Invoke(fov);
            }
        }

        public override void OnEnter()
        {
            SwitchCamera?.Invoke(CameraReference, TransitionTime, TransitionCurveType);
            if (!AnimateFOV) SetFOV?.Invoke(FieldOfView);
        }

        private static float SampleFloat(List<(double time, float fov)> keys, double time, float defaultFov)
        {
            if (keys.Count == 0) return defaultFov;
            if (keys.Count == 1) return keys[0].fov;
            if (time <= keys[0].time) return keys[0].fov;
            if (time >= keys[keys.Count - 1].time) return keys[keys.Count - 1].fov;
            for (int i = 0; i < keys.Count - 1; i++)
            {
                if (time >= keys[i].time && time <= keys[i + 1].time)
                {
                    double range = keys[i + 1].time - keys[i].time;
                    float t = range < 1e-8 ? 0 : (float)((time - keys[i].time) / range);
                    return keys[i].fov + (keys[i + 1].fov - keys[i].fov) * t;
                }
            }
            return defaultFov;
        }
    }

    /// <summary>相机轨道：承载 CameraSection。</summary>
    public class CameraTrack : SequenceTrack
    {
        public override string TrackTypeName => "Camera";
        public CameraTrack() { Color = 0xFFFF6644; Name = "Camera"; }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // EventTrack / EventSection - 事件轨道
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>事件片段：在指定时间触发事件。</summary>
    public class EventSection : SequenceSection
    {
        /// <summary>事件名称。</summary>
        public string EventName { get; set; } = string.Empty;

        /// <summary>事件参数。</summary>
        public Dictionary<string, object> EventParams { get; set; } = new();

        /// <summary>触发时间点（秒）。</summary>
        public double TriggerTime { get; set; }

        /// <summary>是否一次性触发。</summary>
        public bool OneShot { get; set; } = true;

        /// <summary>是否已触发。</summary>
        private bool _triggered;

        /// <summary>触发间隔（秒，持续触发模式下使用）。</summary>
        public double RepeatInterval { get; set; } = 1.0;

        /// <summary>上次触发时间。</summary>
        private double _lastTriggerTime = -1;

        /// <summary>触发事件回调。</summary>
        public Action<string, Dictionary<string, object>>? OnTrigger { get; set; }

        public EventSection() { Color = 0xFFFFCC00; }

        public override void Evaluate(double globalTime, object? context)
        {
            if (!IsEnabled || !ContainsTime(globalTime)) return;
            double elapsed = globalTime - StartTime;

            if (OneShot)
            {
                if (!_triggered && elapsed >= TriggerTime)
                {
                    _triggered = true;
                    OnTrigger?.Invoke(EventName, EventParams);
                }
            }
            else
            {
                if (elapsed >= TriggerTime)
                {
                    if (_lastTriggerTime < 0 || (globalTime - _lastTriggerTime) >= RepeatInterval)
                    {
                        _lastTriggerTime = globalTime;
                        OnTrigger?.Invoke(EventName, EventParams);
                    }
                }
            }
        }

        public override void OnEnter()
        {
            _triggered = false;
            _lastTriggerTime = -1;
        }

        public override void OnExit()
        {
            _triggered = false;
            _lastTriggerTime = -1;
        }
    }

    /// <summary>事件轨道：承载 EventSection。</summary>
    public class EventTrack : SequenceTrack
    {
        public override string TrackTypeName => "Event";
        public EventTrack() { Color = 0xFFFFCC00; Name = "Event"; }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // VisibilityTrack / VisibilitySection - 可见性轨道
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>可见性片段：控制对象的显示/隐藏。</summary>
    public class VisibilitySection : SequenceSection
    {
        /// <summary>目标可见性。</summary>
        public bool TargetVisibility { get; set; } = true;

        /// <summary>淡入时间（秒，0 表示瞬间切换）。</summary>
        public double FadeInTime { get; set; }

        /// <summary>淡出时间（秒）。</summary>
        public double FadeOutTime { get; set; }

        /// <summary>当前透明度（0=透明，1=完全不透明）。</summary>
        public float CurrentOpacity { get; private set; } = 1.0f;

        /// <summary>设置可见性回调。</summary>
        public Action<object?, bool>? SetVisibility { get; set; }

        /// <summary>设置透明度回调。</summary>
        public Action<float>? SetOpacity { get; set; }

        public VisibilitySection() { Color = 0xFFAA88FF; }

        public override void Evaluate(double globalTime, object? context)
        {
            if (!IsEnabled || !ContainsTime(globalTime)) return;
            var target = OwnerTrack?.BoundObject ?? context;

            if (TargetVisibility)
            {
                double elapsed = globalTime - StartTime;
                if (FadeInTime > 0 && elapsed < FadeInTime)
                    CurrentOpacity = (float)(elapsed / FadeInTime);
                else
                    CurrentOpacity = 1.0f;
            }
            else
            {
                double remaining = EndTime - globalTime;
                if (FadeOutTime > 0 && remaining < FadeOutTime)
                    CurrentOpacity = (float)(remaining / FadeOutTime);
                else
                    CurrentOpacity = 0.0f;
            }

            SetOpacity?.Invoke(CurrentOpacity);
            SetVisibility?.Invoke(target, CurrentOpacity > 0.01f);
        }

        public override void OnEnter()
        {
            var target = OwnerTrack?.BoundObject ?? null;
            SetVisibility?.Invoke(target, TargetVisibility);
        }

        public override void OnExit()
        {
            var target = OwnerTrack?.BoundObject ?? null;
            SetVisibility?.Invoke(target, !TargetVisibility);
        }
    }

    /// <summary>可见性轨道：承载 VisibilitySection。</summary>
    public class VisibilityTrack : SequenceTrack
    {
        public override string TrackTypeName => "Visibility";
        public VisibilityTrack() { Color = 0xFFAA88FF; Name = "Visibility"; }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // MaterialTrack / MaterialSection - 材质轨道
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>材质片段：驱动材质参数动画。</summary>
    public class MaterialSection : SequenceSection
    {
        public override bool CanBlend => true;

        /// <summary>颜色参数关键帧。</summary>
        public List<(double time, Vector4 color)> ColorKeys { get; set; } = new();

        /// <summary>标量参数关键帧。</summary>
        public List<(double time, float value)> ScalarKeys { get; set; } = new();

        /// <summary>向量参数关键帧。</summary>
        public List<(double time, Vector4 value)> VectorKeys { get; set; } = new();

        /// <summary>颜色参数名称。</summary>
        public string ColorParamName { get; set; } = "BaseColor";

        /// <summary>标量参数名称。</summary>
        public string ScalarParamName { get; set; } = "Opacity";

        /// <summary>向量参数名称。</summary>
        public string VectorParamName { get; set; } = "Emissive";

        /// <summary>设置颜色参数回调。</summary>
        public Action<string, Vector4>? SetColorParam { get; set; }

        /// <summary>设置标量参数回调。</summary>
        public Action<string, float>? SetScalarParam { get; set; }

        /// <summary>设置向量参数回调。</summary>
        public Action<string, Vector4>? SetVectorParam { get; set; }

        public MaterialSection() { Color = 0xFFCC44FF; }

        public override void Evaluate(double globalTime, object? context)
        {
            if (!IsEnabled || !ContainsTime(globalTime)) return;
            double local = GetLocalTime(globalTime);
            if (local < 0) return;

            if (ColorKeys.Count > 0)
                SetColorParam?.Invoke(ColorParamName, SampleVec4(ColorKeys, local));

            if (ScalarKeys.Count > 0)
                SetScalarParam?.Invoke(ScalarParamName, SampleFloat(ScalarKeys, local, 0));

            if (VectorKeys.Count > 0)
                SetVectorParam?.Invoke(VectorParamName, SampleVec4(VectorKeys, local));
        }

        public void AddColorKey(double time, Vector4 color) { ColorKeys.Add((time, color)); }
        public void AddScalarKey(double time, float value) { ScalarKeys.Add((time, value)); }
        public void AddVectorKey(double time, Vector4 value) { VectorKeys.Add((time, value)); }

        private static Vector4 SampleVec4(List<(double time, Vector4 value)> keys, double time)
        {
            if (keys.Count == 0) return Vector4.Zero;
            if (keys.Count == 1) return keys[0].value;
            if (time <= keys[0].time) return keys[0].value;
            if (time >= keys[keys.Count - 1].time) return keys[keys.Count - 1].value;
            for (int i = 0; i < keys.Count - 1; i++)
            {
                if (time >= keys[i].time && time <= keys[i + 1].time)
                {
                    double range = keys[i + 1].time - keys[i].time;
                    float t = range < 1e-8 ? 0 : (float)((time - keys[i].time) / range);
                    return Vector4.Lerp(keys[i].value, keys[i + 1].value, t);
                }
            }
            return Vector4.Zero;
        }

        private static float SampleFloat(List<(double time, float value)> keys, double time, float def)
        {
            if (keys.Count == 0) return def;
            if (keys.Count == 1) return keys[0].value;
            if (time <= keys[0].time) return keys[0].value;
            if (time >= keys[keys.Count - 1].time) return keys[keys.Count - 1].value;
            for (int i = 0; i < keys.Count - 1; i++)
            {
                if (time >= keys[i].time && time <= keys[i + 1].time)
                {
                    double range = keys[i + 1].time - keys[i].time;
                    float t = range < 1e-8 ? 0 : (float)((time - keys[i].time) / range);
                    return keys[i].value + (keys[i + 1].value - keys[i].value) * t;
                }
            }
            return def;
        }
    }

    /// <summary>材质轨道：承载 MaterialSection。</summary>
    public class MaterialTrack : SequenceTrack
    {
        public override string TrackTypeName => "Material";
        public MaterialTrack() { Color = 0xFFCC44FF; Name = "Material"; }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // LightTrack / LightSection - 灯光轨道
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>灯光片段：控制灯光参数动画。</summary>
    public class LightSection : SequenceSection
    {
        public override bool CanBlend => true;

        /// <summary>颜色关键帧。</summary>
        public List<(double time, Vector3 color)> ColorKeys { get; set; } = new();

        /// <summary>强度关键帧。</summary>
        public List<(double time, float intensity)> IntensityKeys { get; set; } = new();

        /// <summary>衰减半径关键帧。</summary>
        public List<(double time, float attenuation)> AttenuationKeys { get; set; } = new();

        /// <summary>阴影强度关键帧。</summary>
        public List<(double time, float shadowStrength)> ShadowKeys { get; set; } = new();

        /// <summary>设置颜色回调。</summary>
        public Action<Vector3>? SetLightColor { get; set; }

        /// <summary>设置强度回调。</summary>
        public Action<float>? SetLightIntensity { get; set; }

        /// <summary>设置衰减回调。</summary>
        public Action<float>? SetAttenuation { get; set; }

        /// <summary>设置阴影回调。</summary>
        public Action<float>? SetShadowStrength { get; set; }

        public LightSection() { Color = 0xFFFFDD44; }

        public override void Evaluate(double globalTime, object? context)
        {
            if (!IsEnabled || !ContainsTime(globalTime)) return;
            double local = GetLocalTime(globalTime);
            if (local < 0) return;

            if (ColorKeys.Count > 0)
                SetLightColor?.Invoke(TransformSection.SampleVector3(ColorKeys, local));

            if (IntensityKeys.Count > 0)
                SetLightIntensity?.Invoke(MaterialSection.SampleFloat(IntensityKeys, local, 1));

            if (AttenuationKeys.Count > 0)
                SetAttenuation?.Invoke(MaterialSection.SampleFloat(AttenuationKeys, local, 10));

            if (ShadowKeys.Count > 0)
                SetShadowStrength?.Invoke(MaterialSection.SampleFloat(ShadowKeys, local, 1));
        }
    }

    /// <summary>灯光轨道：承载 LightSection。</summary>
    public class LightTrack : SequenceTrack
    {
        public override string TrackTypeName => "Light";
        public LightTrack() { Color = 0xFFFFDD44; Name = "Light"; }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // ParticleTrack / ParticleSection - 粒子轨道
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>粒子片段：控制粒子系统的发射与停止。</summary>
    public class ParticleSection : SequenceSection
    {
        /// <summary>粒子系统引用。</summary>
        public object? ParticleSystem { get; set; }

        /// <summary>粒子系统名称。</summary>
        public string ParticleSystemName { get; set; } = string.Empty;

        /// <summary>粒子参数覆盖。</summary>
        public Dictionary<string, float> ParameterOverrides { get; set; } = new();

        /// <summary>激活动画（粒子系统激活时的过渡效果）。</summary>
        public double ActivationFadeTime { get; set; }

        /// <summary>是否在片段开始时发射。</summary>
        public bool EmitOnEnter { get; set; } = true;

        /// <summary>是否在片段结束时停止。</summary>
        public bool StopOnExit { get; set; } = true;

        /// <summary>发射粒子回调。</summary>
        public Action<object?, Dictionary<string, float>>? EmitParticles { get; set; }

        /// <summary>停止粒子回调。</summary>
        public Action? StopParticles { get; set; }

        /// <summary>设置参数回调。</summary>
        public Action<string, float>? SetParticleParam { get; set; }

        public ParticleSection() { Color = 0xFF44DDAA; }

        public override void Evaluate(double globalTime, object? context)
        {
            if (!IsEnabled || !ContainsTime(globalTime)) return;

            // 应用参数覆盖
            foreach (var param in ParameterOverrides)
            {
                SetParticleParam?.Invoke(param.Key, param.Value);
            }
        }

        public override void OnEnter()
        {
            if (EmitOnEnter)
                EmitParticles?.Invoke(ParticleSystem, ParameterOverrides);
        }

        public override void OnExit()
        {
            if (StopOnExit)
                StopParticles?.Invoke();
        }
    }

    /// <summary>粒子轨道：承载 ParticleSection。</summary>
    public class ParticleTrack : SequenceTrack
    {
        public override string TrackTypeName => "Particle";
        public ParticleTrack() { Color = 0xFF44DDAA; Name = "Particle"; }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // GameplayTagTrack / GameplayTagSection - GameplayTag 轨道
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>GameplayTag 片段：在指定时间内添加/移除 GameplayTag。</summary>
    public class GameplayTagSection : SequenceSection
    {
        /// <summary>标签名称列表。</summary>
        public List<string> Tags { get; set; } = new();

        /// <summary>是否为添加模式（true=添加，false=移除）。</summary>
        public bool IsAdd { get; set; } = true;

        /// <summary>添加标签回调。</summary>
        public Action<object?, List<string>>? AddTags { get; set; }

        /// <summary>移除标签回调。</summary>
        public Action<object?, List<string>>? RemoveTags { get; set; }

        public GameplayTagSection() { Color = 0xFF88CCAA; }

        public override void Evaluate(double globalTime, object? context)
        {
            // 标签的评估由 OnEnter/OnExit 处理
        }

        public override void OnEnter()
        {
            var target = OwnerTrack?.BoundObject ?? null;
            if (IsAdd)
                AddTags?.Invoke(target, Tags);
            else
                RemoveTags?.Invoke(target, Tags);
        }

        public override void OnExit()
        {
            var target = OwnerTrack?.BoundObject ?? null;
            if (IsAdd)
                RemoveTags?.Invoke(target, Tags);
            else
                AddTags?.Invoke(target, Tags);
        }
    }

    /// <summary>GameplayTag 轨道：承载 GameplayTagSection。</summary>
    public class GameplayTagTrack : SequenceTrack
    {
        public override string TrackTypeName => "GameplayTag";
        public GameplayTagTrack() { Color = 0xFF88CCAA; Name = "GameplayTag"; }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // CinematicShotTrack / CinematicShotSection - 电影镜头轨道
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>镜头语言类型。</summary>
    public enum CinematicShotType
    {
        /// <summary>推进（Dolly In）。</summary>
        DollyIn,
        /// <summary>拉远（Dolly Out）。</summary>
        DollyOut,
        /// <summary>跟随。</summary>
        Follow,
        /// <summary>摇摄（Pan）。</summary>
        Pan,
        /// <summary>俯仰（Tilt）。</summary>
        Tilt,
        /// <summary>手持。</summary>
        Handheld,
        /// <summary>震动。</summary>
        Shake,
        /// <summary>环绕。</summary>
        Orbit,
        /// <summary> Crane/Jib 上下升降。</summary>
        Crane,
        /// <summary>自定义。</summary>
        Custom
    }

    /// <summary>电影镜头片段：定义一个镜头语言动作。</summary>
    public class CinematicShotSection : SequenceSection
    {
        public override bool CanBlend => true;

        /// <summary>镜头类型。</summary>
        public CinematicShotType ShotType { get; set; } = CinematicShotType.Follow;

        /// <summary>虚拟相机引用（对接 CinemachineSystem）。</summary>
        public object? VirtualCameraRef { get; set; }

        /// <summary>目标对象引用。</summary>
        public object? TargetObject { get; set; }

        /// <summary>推进/拉远的距离。</summary>
        public float DollyDistance { get; set; } = 5.0f;

        /// <summary>摇摄角度（度）。</summary>
        public float PanAngle { get; set; }

        /// <summary>俯仰角度（度）。</summary>
        public float TiltAngle { get; set; }

        /// <summary>跟随偏移。</summary>
        public Vector3 FollowOffset { get; set; } = new Vector3(0, 3, -8);

        /// <summary>环绕半径。</summary>
        public float OrbitRadius { get; set; } = 5.0f;

        /// <summary>环绕速度。</summary>
        public float OrbitSpeed { get; set; } = 1.0f;

        /// <summary>手持晃动强度。</summary>
        public float HandheldIntensity { get; set; } = 0.1f;

        /// <summary>震动强度。</summary>
        public float ShakeIntensity { get; set; } = 0.5f;

        /// <summary>过渡时间到下一个镜头。</summary>
        public double TransitionDuration { get; set; } = 0.5;

        /// <summary>应用镜头回调。</summary>
        public Action<CinematicShotSection, float>? ApplyShot { get; set; }

        public CinematicShotSection() { Color = 0xFFFF8866; }

        public override void Evaluate(double globalTime, object? context)
        {
            if (!IsEnabled || !ContainsTime(globalTime)) return;
            double local = GetLocalTime(globalTime);
            if (local < 0) return;

            float progress = Duration > 0 ? (float)(local / Duration) : 0;
            ApplyShot?.Invoke(this, progress);
        }
    }

    /// <summary>电影镜头轨道：承载 CinematicShotSection。</summary>
    public class CinematicShotTrack : SequenceTrack
    {
        public override string TrackTypeName => "CinematicShot";
        public CinematicShotTrack() { Color = 0xFFFF8866; Name = "CinematicShot"; }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // SubtitleTrack / SubtitleSection - 字幕轨道
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>字幕对齐方式。</summary>
    public enum SubtitleAlignment
    {
        Center,
        Left,
        Right
    }

    /// <summary>字幕动画类型。</summary>
    public enum SubtitleAnimation
    {
        None,
        FadeIn,
        FadeOut,
        Typewriter,
        SlideUp,
        SlideDown
    }

    /// <summary>字幕片段：在指定时间显示字幕文本。</summary>
    public class SubtitleSection : SequenceSection
    {
        /// <summary>字幕文本。</summary>
        public string Text { get; set; } = string.Empty;

        /// <summary>说话人名称。</summary>
        public string SpeakerName { get; set; } = string.Empty;

        /// <summary>文本颜色。</summary>
        public Vector4 TextColor { get; set; } = new Vector4(1, 1, 1, 1);

        /// <summary>字体名称。</summary>
        public string FontName { get; set; } = "Default";

        /// <summary>字体大小。</summary>
        public float FontSize { get; set; } = 24.0f;

        /// <summary>对齐方式。</summary>
        public SubtitleAlignment Alignment { get; set; } = SubtitleAlignment.Center;

        /// <summary>字幕动画类型。</summary>
        public SubtitleAnimation Animation { get; set; } = SubtitleAnimation.FadeIn;

        /// <summary>动画持续时间（秒）。</summary>
        public double AnimationDuration { get; set; } = 0.3;

        /// <summary>当前透明度（由动画驱动）。</summary>
        public float CurrentAlpha { get; private set; }

        /// <summary>显示字幕回调。</summary>
        public Action<string, string, Vector4, string, float, SubtitleAlignment>? ShowSubtitle { get; set; }

        /// <summary>隐藏字幕回调。</summary>
        public Action? HideSubtitle { get; set; }

        /// <summary>更新字幕透明度回调。</summary>
        public Action<float>? UpdateSubtitleAlpha { get; set; }

        public SubtitleSection() { Color = 0xFF66BBFF; }

        public override void Evaluate(double globalTime, object? context)
        {
            if (!IsEnabled || !ContainsTime(globalTime)) return;

            double elapsed = globalTime - StartTime;
            double remaining = EndTime - globalTime;

            switch (Animation)
            {
                case SubtitleAnimation.FadeIn:
                    CurrentAlpha = AnimationDuration > 0
                        ? (float)Math.Min(1.0, elapsed / AnimationDuration)
                        : 1.0f;
                    break;

                case SubtitleAnimation.FadeOut:
                    CurrentAlpha = AnimationDuration > 0
                        ? (float)Math.Min(1.0, remaining / AnimationDuration)
                        : 1.0f;
                    break;

                case SubtitleAnimation.Typewriter:
                    CurrentAlpha = 1.0f;
                    break;

                default:
                    CurrentAlpha = 1.0f;
                    break;
            }

            UpdateSubtitleAlpha?.Invoke(CurrentAlpha);
        }

        public override void OnEnter()
        {
            ShowSubtitle?.Invoke(Text, SpeakerName, TextColor, FontName, FontSize, Alignment);
        }

        public override void OnExit()
        {
            HideSubtitle?.Invoke();
        }
    }

    /// <summary>字幕轨道：承载 SubtitleSection。</summary>
    public class SubtitleTrack : SequenceTrack
    {
        public override string TrackTypeName => "Subtitle";
        public SubtitleTrack() { Color = 0xFF66BBFF; Name = "Subtitle"; }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // MasterTrack - 主轨道
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>主轨道：控制序列全局参数（全局速度、时间缩放、回放范围）。</summary>
    public class MasterTrack : SequenceTrack
    {
        public override string TrackTypeName => "Master";

        /// <summary>全局时间缩放曲线关键帧。</summary>
        public List<(double time, float scale)> TimeScaleKeys { get; set; } = new();

        /// <summary>全局速度倍率。</summary>
        public float GlobalSpeedMultiplier { get; set; } = 1.0f;

        /// <summary>回放范围起始。</summary>
        public double PlaybackRangeStart { get; set; }

        /// <summary>回放范围结束。</summary>
        public double PlaybackRangeEnd { get; set; }

        /// <summary>是否限制回放范围。</summary>
        public bool HasPlaybackRange { get; set; }

        public MasterTrack() { Color = 0xFF888888; Name = "Master"; Priority = int.MaxValue; }

        /// <summary>获取指定时间的全局时间缩放。</summary>
        public float GetTimeScale(double time)
        {
            if (TimeScaleKeys.Count == 0) return GlobalSpeedMultiplier;
            if (TimeScaleKeys.Count == 1) return TimeScaleKeys[0].scale * GlobalSpeedMultiplier;

            if (time <= TimeScaleKeys[0].time) return TimeScaleKeys[0].scale * GlobalSpeedMultiplier;
            if (time >= TimeScaleKeys[TimeScaleKeys.Count - 1].time)
                return TimeScaleKeys[TimeScaleKeys.Count - 1].scale * GlobalSpeedMultiplier;

            for (int i = 0; i < TimeScaleKeys.Count - 1; i++)
            {
                if (time >= TimeScaleKeys[i].time && time <= TimeScaleKeys[i + 1].time)
                {
                    double range = TimeScaleKeys[i + 1].time - TimeScaleKeys[i].time;
                    float t = range < 1e-8 ? 0 : (float)((time - TimeScaleKeys[i].time) / range);
                    float a = TimeScaleKeys[i].scale;
                    float b = TimeScaleKeys[i + 1].scale;
                    return (a + (b - a) * t) * GlobalSpeedMultiplier;
                }
            }
            return GlobalSpeedMultiplier;
        }

        public override void Evaluate(double time, object? context)
        {
            // 主轨道不承载普通片段，而是直接通过 TimeScaleKeys 驱动全局参数
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // TrackBlendingSystem - 轨道混合系统
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 轨道混合系统：处理同一轨道上多片段重叠时的混合过渡与交叉混合。
    /// </summary>
    public static class TrackBlendingSystem
    {
        /// <summary>
        /// 计算多片段重叠时的混合权重。
        /// 使用交叉淡化（CrossFade）策略：重叠区域内的片段权重按时间线性过渡。
        /// </summary>
        public static void CalculateBlendWeights(List<SequenceSection> overlappingSections, double time)
        {
            if (overlappingSections.Count <= 1) return;

            // 找到重叠区间
            double overlapStart = double.MinValue;
            double overlapEnd = double.MaxValue;

            foreach (var section in overlappingSections)
            {
                if (section.StartTime > overlapStart) overlapStart = section.StartTime;
                if (section.EndTime < overlapEnd) overlapEnd = section.EndTime;
            }

            if (overlapStart >= overlapEnd) return;

            double overlapDuration = overlapEnd - overlapStart;
            if (overlapDuration < 1e-8) return;

            // 计算每个片段在重叠区域内的权重
            foreach (var section in overlappingSections)
            {
                if (!section.CanBlend) continue;

                // 片段在重叠区域内的进度
                double sectionProgress;
                double sectionOverlapStart = Math.Max(section.StartTime, overlapStart);
                double sectionOverlapEnd = Math.Min(section.EndTime, overlapEnd);

                if (section.EndTime <= overlapEnd)
                {
                    // 片段正在退出：权重从 1 渐变到 0
                    sectionProgress = (time - sectionOverlapStart) / overlapDuration;
                    section.BlendWeight = (float)Math.Clamp(1.0 - sectionProgress, 0, 1);
                }
                else
                {
                    // 片段正在进入：权重从 0 渐变到 1
                    sectionProgress = (overlapEnd - sectionOverlapEnd) / overlapDuration;
                    section.BlendWeight = (float)Math.Clamp(1.0 - sectionProgress, 0, 1);
                }
            }
        }

        /// <summary>按优先级和权重对片段排序（高优先级在前）。</summary>
        public static List<SequenceSection> SortByPriorityAndWeight(List<SequenceSection> sections)
        {
            var sorted = new List<SequenceSection>(sections);
            sorted.Sort((a, b) =>
            {
                // 优先级高者在前
                int trackPriority = (b.OwnerTrack?.Priority ?? 0).CompareTo(a.OwnerTrack?.Priority ?? 0);
                if (trackPriority != 0) return trackPriority;

                // 权重高者在前
                return b.BlendWeight.CompareTo(a.BlendWeight);
            });
            return sorted;
        }
    }
}
