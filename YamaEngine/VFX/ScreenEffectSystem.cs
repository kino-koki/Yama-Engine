using System;
using System.Collections.Generic;
using System.Numerics;

namespace YamaEngine.VFX
{
    // ════════════════════════════════════════════════════════════════════════
    // yama引擎 - 屏幕特效系统
    //
    // 全屏屏幕特效 (游戏/战斗反馈):
    //   * DamageIndicatorEffect: 受伤指示 (方向/强度/颜色/脉冲)
    //   * ScreenBloodEffect: 屏幕血迹
    //   * ScreenFlashEffect: 全屏闪光 (白闪/红闪/自定义颜色+衰减)
    //   * ScreenShakeSystem: 屏幕震动 (幅度/频率/衰减/随机偏移/蓝图可控)
    //   * SlowMotionEffect: 慢动作 (时间缩放/插值/触发/恢复)
    //   * RadialBlurEffect: 径向模糊 (速度线/冲击波)
    //   * HeatDistortionEffect: 热空气扭曲
    //   * ScreenTransitionEffect: 屏幕过渡 (淡入/淡出/淡黑/溶解/斜切/遮罩)
    //   * WaterDropletEffect: 水滴效果 (数量/大小/折射/蒸发)
    // ════════════════════════════════════════════════════════════════════════

    #region ScreenEffect 基类

    /// <summary>
    /// 屏幕特效基类。
    /// </summary>
    public abstract class ScreenEffect
    {
        public string EffectName { get; protected set; } = "ScreenEffect";
        public bool Enabled { get; set; } = true;
        public bool IsActive { get; protected set; }
        public float Intensity { get; set; } = 1f;
        public float Duration { get; set; }
        public float Elapsed { get; protected set; }

        /// <summary>更新特效。</summary>
        public abstract void Update(float dt);

        /// <summary>渲染特效。</summary>
        public abstract void Render(object renderContext);

        /// <summary>触发特效。</summary>
        public virtual void Trigger() { IsActive = true; Elapsed = 0f; }

        /// <summary>停止特效。</summary>
        public virtual void Stop() { IsActive = false; Elapsed = 0f; }

        /// <summary>特效是否完成。</summary>
        public virtual bool IsFinished => Duration > 0f && Elapsed >= Duration;

        protected void AdvanceTime(float dt) => Elapsed += dt;
    }

    #endregion

    #region DamageIndicatorEffect

    /// <summary>受伤方向指示器。</summary>
    public class DamageIndicatorEffect : ScreenEffect
    {
        public struct DamageIndicator
        {
            public Vector3 DamageSourcePosition;
            public float DamageAmount;
            public Vector4 Color;
            public float PulseSpeed;
            public float PulseIntensity;
            public float Elapsed;
            public float Duration;
            public bool IsCritical;
        }

        private readonly List<DamageIndicator> _indicators = new List<DamageIndicator>();
        public int MaxIndicators { get; set; } = 8;
        public float IndicatorRadius { get; set; } = 0.4f;
        public float IndicatorSize { get; set; } = 0.05f;
        public float FadeDuration { get; set; } = 0.5f;
        public float CriticalFlashDuration { get; set; } = 0.3f;
        public Vector4 NormalColor { get; set; } = new Vector4(1, 0, 0, 0.8f);
        public Vector4 CriticalColor { get; set; } = new Vector4(1, 0.2f, 0, 1f);

        public DamageIndicatorEffect()
        {
            EffectName = "DamageIndicator";
            IsActive = true;
        }

        /// <summary>添加伤害指示。</summary>
        public void AddDamage(Vector3 sourcePosition, float damageAmount, bool isCritical = false)
        {
            if (_indicators.Count >= MaxIndicators)
                _indicators.RemoveAt(0);

            _indicators.Add(new DamageIndicator
            {
                DamageSourcePosition = sourcePosition,
                DamageAmount = damageAmount,
                Color = isCritical ? CriticalColor : NormalColor,
                PulseSpeed = isCritical ? 10f : 5f,
                PulseIntensity = isCritical ? 1f : 0.5f,
                Elapsed = 0f,
                Duration = isCritical ? CriticalFlashDuration : FadeDuration + 1f,
                IsCritical = isCritical
            });
        }

        public override void Update(float dt)
        {
            for (int i = _indicators.Count - 1; i >= 0; i--)
            {
                var ind = _indicators[i];
                ind.Elapsed += dt;
                _indicators[i] = ind;
                if (ind.Elapsed >= ind.Duration)
                    _indicators.RemoveAt(i);
            }
        }

        public override void Render(object renderContext)
        {
            // GPU: 在屏幕边缘绘制方向指示器
        }

        public IReadOnlyList<DamageIndicator> GetIndicators() => _indicators;
    }

    #endregion

    #region ScreenBloodEffect

    /// <summary>屏幕血迹效果。</summary>
    public class ScreenBloodEffect : ScreenEffect
    {
        public float BloodAmount { get; set; }
        public float BloodIntensity { get; set; } = 0.5f;
        public Vector4 BloodColor { get; set; } = new Vector4(0.5f, 0f, 0f, 0.6f);
        public float DripSpeed { get; set; } = 0.1f;
        public string BloodTexture { get; set; }
        public float DesaturationAmount { get; set; } = 0.2f;
        public float VignetteIntensity { get; set; } = 0.3f;
        public float EdgeDarkening { get; set; } = 0.5f;
        public float RecoverySpeed { get; set; } = 0.3f;

        public ScreenBloodEffect()
        {
            EffectName = "ScreenBlood";
            Duration = 5f;
        }

        public override void Trigger()
        {
            base.Trigger();
            BloodAmount = 1f;
        }

        public override void Update(float dt)
        {
            if (!IsActive) return;
            AdvanceTime(dt);

            if (IsFinished)
            {
                BloodAmount = Math.Max(0f, BloodAmount - RecoverySpeed * dt);
                if (BloodAmount <= 0f)
                    Stop();
            }
        }

        public override void Render(object renderContext)
        {
            // GPU: 血迹纹理叠加 + 暗角 + 去饱和
        }
    }

    #endregion

    #region ScreenFlashEffect

    /// <summary>全屏闪光效果。</summary>
    public class ScreenFlashEffect : ScreenEffect
    {
        public Vector4 FlashColor { get; set; } = Vector4.One;
        public float FlashIntensity { get; set; } = 1f;
        public float FadeSpeed { get; set; } = 2f;
        public bool FadeOut { get; set; } = true;
        public float CurrentAlpha { get; private set; }

        public ScreenFlashEffect()
        {
            EffectName = "ScreenFlash";
            Duration = 1f;
        }

        public void TriggerFlash(Vector4 color, float intensity, float duration)
        {
            FlashColor = color;
            FlashIntensity = intensity;
            Duration = duration;
            Trigger();
        }

        /// <summary>白色闪光。</summary>
        public void WhiteFlash(float intensity = 1f, float duration = 0.5f)
            => TriggerFlash(new Vector4(1, 1, 1, 1), intensity, duration);

        /// <summary>红色闪光 (受伤反馈)。</summary>
        public void RedFlash(float intensity = 0.8f, float duration = 0.3f)
            => TriggerFlash(new Vector4(1, 0, 0, 0.8f), intensity, duration);

        public override void Update(float dt)
        {
            if (!IsActive) return;
            AdvanceTime(dt);

            if (FadeOut)
                CurrentAlpha = FlashIntensity * Math.Max(0f, 1f - Elapsed * FadeSpeed);

            if (IsFinished)
                Stop();
        }

        public override void Render(object renderContext)
        {
            // GPU: 全屏颜色叠加
        }
    }

    #endregion

    #region ScreenShakeSystem

    /// <summary>
    /// 屏幕震动系统。
    /// 支持多震动叠加、衰减、随机偏移。
    /// </summary>
    public class ScreenShakeSystem : ScreenEffect
    {
        public struct ShakeParams
        {
            public float Amplitude;
            public float Frequency;
            public float Duration;
            public float Elapsed;
            public float Decay;
            public Vector3 Offset;
            public Quaternion RotationOffset;
            public float RandomSeed;
            public bool UsePerlinNoise;
            public bool AffectsFOV;
            public float FOVOffset;
        }

        private readonly List<ShakeParams> _activeShakes = new List<ShakeParams>();
        private Vector3 _totalOffset;
        private Quaternion _totalRotation;
        private float _totalFOVOffset;

        public Vector3 CurrentOffset => _totalOffset;
        public Quaternion CurrentRotation => _totalRotation;
        public float CurrentFOVOffset => _totalFOVOffset;
        public bool IsShaking => _activeShakes.Count > 0;

        public ScreenShakeSystem()
        {
            EffectName = "ScreenShake";
            IsActive = true;
        }

        /// <summary>触发简单屏幕震动。</summary>
        public void Shake(float amplitude, float frequency, float duration, float decay = 1f)
        {
            _activeShakes.Add(new ShakeParams
            {
                Amplitude = amplitude,
                Frequency = frequency,
                Duration = duration,
                Elapsed = 0f,
                Decay = decay,
                RandomSeed = Random.Shared.NextSingle() * 100f,
                UsePerlinNoise = false
            });
        }

        /// <summary>触发 Perlin 噪声震动 (更自然)。</summary>
        public void PerlinShake(float amplitude, float frequency, float duration, float decay = 1f)
        {
            _activeShakes.Add(new ShakeParams
            {
                Amplitude = amplitude,
                Frequency = frequency,
                Duration = duration,
                Elapsed = 0f,
                Decay = decay,
                RandomSeed = Random.Shared.NextSingle() * 100f,
                UsePerlinNoise = true
            });
        }

        /// <summary>带 FOV 偏移的震动。</summary>
        public void ShakeWithFOV(float amplitude, float frequency, float duration, float fovOffset)
        {
            var shake = new ShakeParams
            {
                Amplitude = amplitude,
                Frequency = frequency,
                Duration = duration,
                Elapsed = 0f,
                Decay = 1f,
                RandomSeed = Random.Shared.NextSingle() * 100f,
                AffectsFOV = true,
                FOVOffset = fovOffset
            };
            _activeShakes.Add(shake);
        }

        public override void Update(float dt)
        {
            _totalOffset = Vector3.Zero;
            _totalRotation = Quaternion.Identity;
            _totalFOVOffset = 0f;

            for (int i = _activeShakes.Count - 1; i >= 0; i--)
            {
                var shake = _activeShakes[i];
                shake.Elapsed += dt;

                if (shake.Elapsed >= shake.Duration)
                {
                    _activeShakes.RemoveAt(i);
                    continue;
                }

                // 衰减因子
                float progress = shake.Elapsed / shake.Duration;
                float decayFactor = MathF.Pow(1f - progress, shake.Decay);

                float amp = shake.Amplitude * decayFactor;
                float t = shake.Elapsed * shake.Frequency;
                float seed = shake.RandomSeed;

                Vector3 offset;
                if (shake.UsePerlinNoise)
                {
                    offset = new Vector3(
                        SimpleNoise(t + seed) * amp,
                        SimpleNoise(t + seed + 31.7f) * amp,
                        SimpleNoise(t + seed + 67.3f) * amp * 0.3f);
                }
                else
                {
                    offset = new Vector3(
                        MathF.Sin(t * 2.1f + seed) * MathF.Cos(t * 1.3f) * amp,
                        MathF.Cos(t * 1.7f + seed) * MathF.Sin(t * 2.3f) * amp,
                        MathF.Sin(t * 0.9f + seed) * amp * 0.2f);
                }

                shake.Offset = offset;
                _totalOffset += offset;

                // 旋转偏移
                float rotAmount = amp * 0.002f;
                shake.RotationOffset = Quaternion.CreateFromYawPitchRoll(
                    MathF.Sin(t * 1.5f + seed) * rotAmount,
                    MathF.Cos(t * 1.8f + seed) * rotAmount,
                    MathF.Sin(t * 1.2f + seed) * rotAmount * 0.5f);
                _totalRotation = _totalRotation * shake.RotationOffset;

                if (shake.AffectsFOV)
                    _totalFOVOffset += shake.FOVOffset * decayFactor;

                _activeShakes[i] = shake;
            }
        }

        public override void Render(object renderContext)
        {
            // 应用偏移到相机
        }

        private static float SimpleNoise(float x)
        {
            return MathF.Sin(x * 1.1f + MathF.Sin(x * 0.7f) * 3.14f) * 0.5f;
        }
    }

    #endregion

    #region SlowMotionEffect

    /// <summary>
    /// 慢动作效果。时间缩放/插值/触发/恢复。
    /// </summary>
    public class SlowMotionEffect : ScreenEffect
    {
        public float TimeScale { get; private set; } = 1f;
        public float TargetTimeScale { get; set; } = 0.2f;
        public float TransitionSpeed { get; set; } = 2f;
        public float RecoverySpeed { get; set; } = 1f;
        public bool AutoRecover { get; set; } = true;
        public float SlowMotionDuration { get; set; } = 2f;
        private float _slowMotionTimer;
        private bool _isRecovering;

        // 音频效果
        public float AudioPitchScale { get; set; } = 0.5f;
        public bool AffectAudio { get; set; } = true;

        // 视觉效果
        public float RadialBlurIntensity { get; set; } = 0.1f;
        public float ChromaticAberration { get; set; } = 0.05f;
        public float SaturationBoost { get; set; } = 0.1f;
        public float VignetteBoost { get; set; } = 0.2f;

        public SlowMotionEffect()
        {
            EffectName = "SlowMotion";
            Duration = 5f;
        }

        /// <summary>触发慢动作。</summary>
        public void EnterSlowMotion(float timeScale = 0.2f, float duration = 2f)
        {
            TargetTimeScale = timeScale;
            SlowMotionDuration = duration;
            _slowMotionTimer = 0f;
            _isRecovering = false;
            Trigger();
        }

        /// <summary>退出慢动作。</summary>
        public void ExitSlowMotion()
        {
            _isRecovering = true;
        }

        public override void Update(float dt)
        {
            if (!IsActive) return;

            float realDt = dt * TimeScale;
            AdvanceTime(dt);

            if (!_isRecovering)
            {
                // 进入慢动作 (加速降低)
                float speed = TransitionSpeed * 3f;
                TimeScale += (TargetTimeScale - TimeScale) * Math.Min(1f, speed * dt);

                _slowMotionTimer += realDt;
                if (AutoRecover && _slowMotionTimer >= SlowMotionDuration)
                    _isRecovering = true;
            }
            else
            {
                // 恢复正常速度
                TimeScale += (1f - TimeScale) * Math.Min(1f, RecoverySpeed * dt);

                if (TimeScale >= 0.99f)
                {
                    TimeScale = 1f;
                    Stop();
                }
            }
        }

        public override void Render(object renderContext)
        {
            // GPU: 可选的视觉特效 (径向模糊/色差/暗角)
        }

        public float GetScaledDeltaTime(float dt) => dt * TimeScale;
    }

    #endregion

    #region RadialBlurEffect

    /// <summary>
    /// 径向模糊效果 (速度线/冲击波)。
    /// </summary>
    public class RadialBlurEffect : ScreenEffect
    {
        public Vector2 Center { get; set; } = new Vector2(0.5f, 0.5f);
        public float Intensity { get; set; } = 0.3f;
        public float Width { get; set; } = 0.5f;
        public float Falloff { get; set; } = 1f;
        public int SampleCount { get; set; } = 12;
        public bool Animate { get; set; }
        public float AnimationSpeed { get; set; } = 2f;
        public bool IsShockwave { get; set; }
        public float ShockwaveRadius { get; set; }
        public float ShockwaveWidth { get; set; } = 0.1f;
        public float ShockwaveSpeed { get; set; } = 1f;

        public RadialBlurEffect()
        {
            EffectName = "RadialBlur";
            Duration = 1f;
        }

        /// <summary>触发速度线效果。</summary>
        public void TriggerSpeedLines(float intensity, float duration, Vector2? center = null)
        {
            Center = center ?? new Vector2(0.5f, 0.5f);
            Intensity = intensity;
            Duration = duration;
            IsShockwave = false;
            Trigger();
        }

        /// <summary>触发冲击波效果。</summary>
        public void TriggerShockwave(Vector2 center, float width, float speed, float duration)
        {
            Center = center;
            ShockwaveWidth = width;
            ShockwaveSpeed = speed;
            Duration = duration;
            ShockwaveRadius = 0f;
            IsShockwave = true;
            Trigger();
        }

        public override void Update(float dt)
        {
            if (!IsActive) return;
            AdvanceTime(dt);

            if (IsShockwave)
                ShockwaveRadius += ShockwaveSpeed * dt;

            if (IsFinished)
                Stop();
        }

        public override void Render(object renderContext)
        {
            // GPU: 径向 UV 偏移采样
        }
    }

    #endregion

    #region HeatDistortionEffect

    /// <summary>热空气扭曲效果。</summary>
    public class HeatDistortionEffect : ScreenEffect
    {
        public float DistortionStrength { get; set; } = 0.02f;
        public float NoiseScale { get; set; } = 1f;
        public float NoiseSpeed { get; set; } = 1f;
        public string NoiseTexture { get; set; }
        public Vector4 DistortionMask { get; set; } = new Vector4(0.5f, 0.5f, 1f, 1f);
        public bool UseHeatMap { get; set; }
        public Vector3 HeatSourcePosition { get; set; }
        public float HeatRadius { get; set; } = 5f;

        public HeatDistortionEffect()
        {
            EffectName = "HeatDistortion";
            Duration = -1f; // 持续
        }

        public override void Update(float dt)
        {
            if (!IsActive) return;
            AdvanceTime(dt);
        }

        public override void Render(object renderContext)
        {
            // GPU: 噪声纹理 → UV 偏移
        }
    }

    #endregion

    #region ScreenTransitionEffect

    /// <summary>屏幕过渡类型。</summary>
    public enum TransitionType
    {
        FadeIn,
        FadeOut,
        FadeToBlack,
        Dissolve,
        WipeLeft,
        WipeRight,
        WipeUp,
        WipeDown,
        CircleOpen,
        CircleClose,
        Diagonal,
        CustomMask
    }

    /// <summary>
    /// 屏幕过渡效果。
    /// </summary>
    public class ScreenTransitionEffect : ScreenEffect
    {
        public TransitionType Type { get; set; } = TransitionType.FadeToBlack;
        public Vector4 Color { get; set; } = Vector4.Zero;
        public string MaskTexture { get; set; }
        public float EdgeSoftness { get; set; } = 0.02f;
        public float EdgeWidth { get; set; } = 0.1f;
        public bool UseNoiseDissolve { get; set; }
        public float NoiseScale { get; set; } = 10f;
        public Vector2 CircleCenter { get; set; } = new Vector2(0.5f, 0.5f);

        // 事件
        public event Action OnTransitionStart;
        public event Action OnTransitionHalf;
        public event Action OnTransitionComplete;
        private bool _firedHalf;

        public ScreenTransitionEffect()
        {
            EffectName = "ScreenTransition";
            Duration = 2f;
        }

        /// <summary>执行淡出到黑色。</summary>
        public void FadeToBlack(float duration)
        {
            Type = TransitionType.FadeToBlack;
            Color = Vector4.Zero;
            Duration = duration;
            Trigger();
        }

        /// <summary>执行从黑色淡入。</summary>
        public void FadeFromBlack(float duration)
        {
            Type = TransitionType.FadeIn;
            Color = Vector4.Zero;
            Duration = duration;
            Trigger();
        }

        /// <summary>执行溶解过渡。</summary>
        public void DissolveTransition(float duration, string mask = null)
        {
            Type = TransitionType.Dissolve;
            Duration = duration;
            MaskTexture = mask;
            Trigger();
        }

        /// <summary>执行圆形展开过渡。</summary>
        public void CircleOpenTransition(float duration, Vector2? center = null)
        {
            Type = TransitionType.CircleOpen;
            Duration = duration;
            CircleCenter = center ?? new Vector2(0.5f, 0.5f);
            Trigger();
        }

        public override void Trigger()
        {
            base.Trigger();
            _firedHalf = false;
            OnTransitionStart?.Invoke();
        }

        public override void Update(float dt)
        {
            if (!IsActive) return;
            AdvanceTime(dt);

            float progress = Duration > 0 ? Elapsed / Duration : 0f;
            if (progress >= 0.5f && !_firedHalf)
            {
                _firedHalf = true;
                OnTransitionHalf?.Invoke();
            }

            if (IsFinished)
            {
                OnTransitionComplete?.Invoke();
                Stop();
            }
        }

        public override void Render(object renderContext)
        {
            // GPU: 过渡效果渲染
        }

        /// <summary>获取当前过渡进度 [0..1]。</summary>
        public float GetProgress() => Duration > 0 ? Math.Clamp(Elapsed / Duration, 0, 1) : 0f;
    }

    #endregion

    #region WaterDropletEffect

    /// <summary>水滴效果。</summary>
    public class WaterDropletEffect : ScreenEffect
    {
        public struct WaterDroplet
        {
            public Vector2 Position;
            public float Size;
            public float RefractionStrength;
            public float Life;
            public float MaxLife;
            public float Velocity;
            public float RandomSeed;
            public bool IsDripping;
        }

        public int MaxDroplets { get; set; } = 64;
        public float SpawnRate { get; set; } = 5f;
        public float MinSize { get; set; } = 0.002f;
        public float MaxSize { get; set; } = 0.01f;
        public float Refraction { get; set; } = 0.1f;
        public float EvaporationTime { get; set; } = 3f;
        public float DripSpeed { get; set; } = 0.02f;
        public float DripAcceleration { get; set; } = 0.001f;

        private readonly List<WaterDroplet> _droplets = new List<WaterDroplet>();
        private float _spawnAccumulator;

        public WaterDropletEffect()
        {
            EffectName = "WaterDroplet";
            Duration = -1f;
        }

        /// <summary>溅射水滴 (从指定位置)。</summary>
        public void Splash(Vector2 position, int count = 5, float spread = 0.1f)
        {
            for (int i = 0; i < count; i++)
            {
                if (_droplets.Count >= MaxDroplets) break;

                _droplets.Add(new WaterDroplet
                {
                    Position = new Vector2(
                        position.X + (Random.Shared.NextSingle() * 2 - 1) * spread,
                        position.Y + (Random.Shared.NextSingle() * 2 - 1) * spread),
                    Size = MinSize + Random.Shared.NextSingle() * (MaxSize - MinSize) * 0.5f,
                    RefractionStrength = Refraction,
                    Life = 0f,
                    MaxLife = EvaporationTime * (0.5f + Random.Shared.NextSingle() * 0.5f),
                    Velocity = DripSpeed * Random.Shared.NextSingle(),
                    RandomSeed = Random.Shared.NextSingle() * 100f,
                    IsDripping = true
                });
            }
        }

        public override void Update(float dt)
        {
            if (!IsActive) return;
            AdvanceTime(dt);

            // 生成新水滴
            _spawnAccumulator += SpawnRate * dt;
            while (_spawnAccumulator >= 1f && _droplets.Count < MaxDroplets)
            {
                _spawnAccumulator -= 1f;
                _droplets.Add(new WaterDroplet
                {
                    Position = new Vector2(Random.Shared.NextSingle(), 0f),
                    Size = MinSize + Random.Shared.NextSingle() * (MaxSize - MinSize),
                    RefractionStrength = Refraction * (0.5f + Random.Shared.NextSingle()),
                    Life = 0f,
                    MaxLife = EvaporationTime * (0.5f + Random.Shared.NextSingle() * 0.5f),
                    Velocity = DripSpeed * Random.Shared.NextSingle(),
                    RandomSeed = Random.Shared.NextSingle() * 100f,
                    IsDripping = true
                });
            }

            // 更新水滴
            for (int i = _droplets.Count - 1; i >= 0; i--)
            {
                var droplet = _droplets[i];
                droplet.Life += dt;

                if (droplet.IsDripping)
                {
                    droplet.Velocity += DripAcceleration * dt;
                    droplet.Position.Y += droplet.Velocity * dt;
                }

                // 水滴缩小 (蒸发)
                float lifeRatio = droplet.Life / droplet.MaxLife;
                droplet.Size *= Math.Max(0f, 1f - lifeRatio * dt * 0.5f);

                if (droplet.Life >= droplet.MaxLife || droplet.Size < MinSize * 0.1f || droplet.Position.Y > 1.2f)
                {
                    _droplets.RemoveAt(i);
                }
                else
                {
                    _droplets[i] = droplet;
                }
            }
        }

        public override void Render(object renderContext)
        {
            // GPU: 水滴折射效果
        }

        public IReadOnlyList<WaterDroplet> GetDroplets() => _droplets;
    }

    #endregion

    #region ScreenEffectManager

    /// <summary>屏幕特效管理器。</summary>
    public class ScreenEffectManager
    {
        private readonly List<ScreenEffect> _effects = new List<ScreenEffect>();

        public DamageIndicatorEffect DamageIndicator { get; } = new DamageIndicatorEffect();
        public ScreenBloodEffect ScreenBlood { get; } = new ScreenBloodEffect();
        public ScreenFlashEffect ScreenFlash { get; } = new ScreenFlashEffect();
        public ScreenShakeSystem ScreenShake { get; } = new ScreenShakeSystem();
        public SlowMotionEffect SlowMotion { get; } = new SlowMotionEffect();
        public RadialBlurEffect RadialBlur { get; } = new RadialBlurEffect();
        public HeatDistortionEffect HeatDistortion { get; } = new HeatDistortionEffect();
        public ScreenTransitionEffect ScreenTransition { get; } = new ScreenTransitionEffect();
        public WaterDropletEffect WaterDroplets { get; } = new WaterDropletEffect();

        public ScreenEffectManager()
        {
            _effects.Add(DamageIndicator);
            _effects.Add(ScreenBlood);
            _effects.Add(ScreenFlash);
            _effects.Add(ScreenShake);
            _effects.Add(SlowMotion);
            _effects.Add(RadialBlur);
            _effects.Add(HeatDistortion);
            _effects.Add(ScreenTransition);
            _effects.Add(WaterDroplets);
        }

        /// <summary>添加自定义效果。</summary>
        public void AddEffect(ScreenEffect effect) => _effects.Add(effect);

        /// <summary>移除效果。</summary>
        public bool RemoveEffect(ScreenEffect effect) => _effects.Remove(effect);

        /// <summary>更新所有效果。</summary>
        public void Update(float dt)
        {
            float scaledDt = SlowMotion.IsActive ? SlowMotion.GetScaledDeltaTime(dt) : dt;
            foreach (var effect in _effects)
            {
                if (effect.Enabled && (effect.IsActive || effect == SlowMotion))
                    effect.Update(scaledDt);
            }
        }

        /// <summary>渲染所有活跃效果。</summary>
        public void Render(object renderContext)
        {
            foreach (var effect in _effects)
            {
                if (effect.Enabled && effect.IsActive)
                    effect.Render(renderContext);
            }
        }

        /// <summary>获取当前时间缩放。</summary>
        public float GetCurrentTimeScale() => SlowMotion.IsActive ? SlowMotion.TimeScale : 1f;

        /// <summary>获取当前屏幕偏移 (震动)。</summary>
        public Vector3 GetCameraOffset() => ScreenShake.CurrentOffset;

        /// <summary>获取当前 FOV 偏移 (震动)。</summary>
        public float GetFOVOffset() => ScreenShake.CurrentFOVOffset;
    }

    #endregion
}
