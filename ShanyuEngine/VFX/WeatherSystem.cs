using System;
using System.Collections.Generic;
using System.Numerics;

namespace ShanyuEngine.VFX
{
    // ════════════════════════════════════════════════════════════════════════
    // 山雨引擎 - 天气系统
    //
    // 全局天气管理:
    //   * WeatherType: 12 种天气类型枚举
    //   * WeatherSystem: 当前/目标天气 + 平滑过渡
    //   * WeatherVolume: 天气体积区域 (不同区域不同天气)
    //   * 天气效果: Rain/Snow/Fog/Dust/Lightning/Wind
    //   * 天气参数: 温度/湿度/气压/能见度/风速/风向
    //   * 天气影响: 地面湿滑/影响音效/影响光照
    //   * 季节系统: 四季循环/昼夜循环联动
    // ════════════════════════════════════════════════════════════════════════

    #region WeatherType

    /// <summary>天气类型枚举。</summary>
    public enum WeatherType
    {
        Clear,
        Cloudy,
        Overcast,
        Rain,
        HeavyRain,
        Snow,
        HeavySnow,
        Fog,
        Dust,
        Sandstorm,
        Hail,
        Thunderstorm
    }

    /// <summary>季节枚举。</summary>
    public enum Season
    {
        Spring,
        Summer,
        Autumn,
        Winter
    }

    /// <summary>一天中的时段。</summary>
    public enum TimeOfDay
    {
        Dawn,
        Morning,
        Noon,
        Afternoon,
        Evening,
        Dusk,
        Night,
        Midnight
    }

    #endregion

    #region WeatherParameters

    /// <summary>天气参数集合。</summary>
    public class WeatherParameters
    {
        // ── 基础 ──
        public WeatherType Type { get; set; } = WeatherType.Clear;

        // ── 物理参数 ──
        public float Temperature { get; set; } = 20f;        // 摄氏度
        public float Humidity { get; set; } = 50f;            // 百分比 0-100
        public float Pressure { get; set; } = 1013.25f;      // hPa
        public float Visibility { get; set; } = 10f;         // km
        public float CloudCoverage { get; set; } = 0.2f;     // 0-1

        // ── 风 ──
        public float WindSpeed { get; set; } = 2f;           // m/s
        public float WindDirection { get; set; } = 0f;       // 角度 (0=北)
        public float GustSpeed { get; set; } = 0f;           // 阵风 m/s
        public float GustInterval { get; set; } = 5f;        // 阵风间隔
        public float Turbulence { get; set; } = 0f;          // 湍流强度 0-1

        // ── 降水 ──
        public float PrecipitationIntensity { get; set; }     // 0-1
        public float PrecipitationSize { get; set; } = 1f;     // 雨滴/雪花大小
        public float SnowAccumulation { get; set; }            // 积雪厚度

        // ── 雾 ──
        public float FogDensity { get; set; } = 0f;           // 0-1
        public float FogHeight { get; set; } = 0f;            // 雾高度
        public Vector3 FogColor { get; set; } = new Vector3(0.7f, 0.75f, 0.8f);

        // ── 闪电 ──
        public float LightningFrequency { get; set; }        // 次/分钟
        public float LightningIntensity { get; set; } = 1f;

        // ── 光照 ──
        public float SunIntensity { get; set; } = 1f;
        public float AmbientIntensity { get; set; } = 0.3f;
        public Vector3 SkyColor { get; set; } = new Vector3(0.4f, 0.6f, 0.9f);
        public Vector3 SunColor { get; set; } = new Vector3(1f, 0.95f, 0.8f);
        public float SkyBrightness { get; set; } = 1f;

        /// <summary>获取风向向量。</summary>
        public Vector3 GetWindDirectionVector()
        {
            float rad = WindDirection * MathF.PI / 180f;
            return new Vector3(MathF.Sin(rad), 0, MathF.Cos(rad));
        }

        /// <summary>线性插值两组天气参数。</summary>
        public static WeatherParameters Lerp(WeatherParameters a, WeatherParameters b, float t)
        {
            t = Math.Clamp(t, 0f, 1f);
            return new WeatherParameters
            {
                Type = t < 0.5f ? a.Type : b.Type,
                Temperature = a.Temperature + (b.Temperature - a.Temperature) * t,
                Humidity = a.Humidity + (b.Humidity - a.Humidity) * t,
                Pressure = a.Pressure + (b.Pressure - a.Pressure) * t,
                Visibility = a.Visibility + (b.Visibility - a.Visibility) * t,
                CloudCoverage = a.CloudCoverage + (b.CloudCoverage - a.CloudCoverage) * t,
                WindSpeed = a.WindSpeed + (b.WindSpeed - a.WindSpeed) * t,
                WindDirection = a.WindDirection + (b.WindDirection - a.WindDirection) * t,
                GustSpeed = a.GustSpeed + (b.GustSpeed - a.GustSpeed) * t,
                Turbulence = a.Turbulence + (b.Turbulence - a.Turbulence) * t,
                PrecipitationIntensity = a.PrecipitationIntensity + (b.PrecipitationIntensity - a.PrecipitationIntensity) * t,
                PrecipitationSize = a.PrecipitationSize + (b.PrecipitationSize - a.PrecipitationSize) * t,
                SnowAccumulation = a.SnowAccumulation + (b.SnowAccumulation - a.SnowAccumulation) * t,
                FogDensity = a.FogDensity + (b.FogDensity - a.FogDensity) * t,
                FogHeight = a.FogHeight + (b.FogHeight - a.FogHeight) * t,
                FogColor = Vector3.Lerp(a.FogColor, b.FogColor, t),
                LightningFrequency = a.LightningFrequency + (b.LightningFrequency - a.LightningFrequency) * t,
                LightningIntensity = a.LightningIntensity + (b.LightningIntensity - a.LightningIntensity) * t,
                SunIntensity = a.SunIntensity + (b.SunIntensity - a.SunIntensity) * t,
                AmbientIntensity = a.AmbientIntensity + (b.AmbientIntensity - a.AmbientIntensity) * t,
                SkyColor = Vector3.Lerp(a.SkyColor, b.SkyColor, t),
                SunColor = Vector3.Lerp(a.SunColor, b.SunColor, t),
                SkyBrightness = a.SkyBrightness + (b.SkyBrightness - a.SkyBrightness) * t
            };
        }

        /// <summary>创建预设天气参数。</summary>
        public static WeatherParameters CreatePreset(WeatherType type)
        {
            return type switch
            {
                WeatherType.Clear => new WeatherParameters
                {
                    Type = WeatherType.Clear, Temperature = 25f, Humidity = 40f,
                    Visibility = 15f, CloudCoverage = 0.1f, SunIntensity = 1.2f,
                    SkyColor = new Vector3(0.3f, 0.5f, 0.9f)
                },
                WeatherType.Cloudy => new WeatherParameters
                {
                    Type = WeatherType.Cloudy, Temperature = 22f, Humidity = 55f,
                    Visibility = 10f, CloudCoverage = 0.6f, SunIntensity = 0.7f,
                    SkyColor = new Vector3(0.5f, 0.55f, 0.65f)
                },
                WeatherType.Rain => new WeatherParameters
                {
                    Type = WeatherType.Rain, Temperature = 18f, Humidity = 85f,
                    Visibility = 5f, CloudCoverage = 0.9f, SunIntensity = 0.3f,
                    PrecipitationIntensity = 0.5f, PrecipitationSize = 1f,
                    FogDensity = 0.1f, SkyColor = new Vector3(0.4f, 0.42f, 0.45f)
                },
                WeatherType.HeavyRain => new WeatherParameters
                {
                    Type = WeatherType.HeavyRain, Temperature = 16f, Humidity = 95f,
                    Visibility = 2f, CloudCoverage = 1f, SunIntensity = 0.1f,
                    PrecipitationIntensity = 1f, PrecipitationSize = 1.2f,
                    FogDensity = 0.2f, WindSpeed = 8f, GustSpeed = 15f,
                    SkyColor = new Vector3(0.35f, 0.35f, 0.4f)
                },
                WeatherType.Snow => new WeatherParameters
                {
                    Type = WeatherType.Snow, Temperature = -2f, Humidity = 70f,
                    Visibility = 5f, CloudCoverage = 0.8f, SunIntensity = 0.4f,
                    PrecipitationIntensity = 0.4f, PrecipitationSize = 0.5f,
                    FogDensity = 0.15f, SkyColor = new Vector3(0.6f, 0.62f, 0.65f)
                },
                WeatherType.HeavySnow => new WeatherParameters
                {
                    Type = WeatherType.HeavySnow, Temperature = -8f, Humidity = 80f,
                    Visibility = 1f, CloudCoverage = 1f, SunIntensity = 0.2f,
                    PrecipitationIntensity = 0.9f, PrecipitationSize = 0.8f,
                    FogDensity = 0.3f, WindSpeed = 5f, SkyColor = new Vector3(0.55f, 0.55f, 0.6f)
                },
                WeatherType.Fog => new WeatherParameters
                {
                    Type = WeatherType.Fog, Temperature = 15f, Humidity = 90f,
                    Visibility = 0.5f, CloudCoverage = 0.7f, SunIntensity = 0.2f,
                    FogDensity = 0.8f, FogHeight = 50f,
                    FogColor = new Vector3(0.7f, 0.72f, 0.75f), SkyColor = new Vector3(0.6f, 0.62f, 0.65f)
                },
                WeatherType.Sandstorm => new WeatherParameters
                {
                    Type = WeatherType.Sandstorm, Temperature = 35f, Humidity = 20f,
                    Visibility = 0.3f, CloudCoverage = 0.5f, SunIntensity = 0.4f,
                    WindSpeed = 20f, GustSpeed = 30f, Turbulence = 0.8f,
                    FogDensity = 0.5f, FogColor = new Vector3(0.8f, 0.7f, 0.5f),
                    SkyColor = new Vector3(0.7f, 0.6f, 0.4f)
                },
                WeatherType.Thunderstorm => new WeatherParameters
                {
                    Type = WeatherType.Thunderstorm, Temperature = 20f, Humidity = 95f,
                    Visibility = 3f, CloudCoverage = 1f, SunIntensity = 0.1f,
                    PrecipitationIntensity = 0.8f, WindSpeed = 12f, GustSpeed = 25f,
                    LightningFrequency = 10f, LightningIntensity = 2f,
                    SkyColor = new Vector3(0.2f, 0.2f, 0.25f)
                },
                _ => new WeatherParameters() // Default Clear
            };
        }
    }

    #endregion

    #region WeatherVolume

    /// <summary>
    /// 天气体积区域。不同区域可以有不同天气。
    /// </summary>
    public class WeatherVolume
    {
        public string Name { get; set; } = "WeatherVolume";
        public Guid Id { get; } = Guid.NewGuid();
        public bool IsEnabled { get; set; } = true;

        // ── 体积 ──
        public Vector3 Position { get; set; } = Vector3.Zero;
        public Vector3 BoxExtent { get; set; } = new Vector3(500, 200, 500);
        public float SphereRadius { get; set; } = 500f;
        public bool UseBox { get; set; } = true;

        // ── 天气 ──
        public WeatherParameters Weather { get; set; } = new WeatherParameters();
        public float Priority { get; set; } = 0f;
        public float BlendWeight { get; set; } = 1f;

        /// <summary>检查点是否在体积内。</summary>
        public bool ContainsPoint(Vector3 point)
        {
            if (!IsEnabled) return false;
            if (UseBox)
            {
                Vector3 delta = point - Position;
                return Math.Abs(delta.X) <= BoxExtent.X &&
                       Math.Abs(delta.Y) <= BoxExtent.Y &&
                       Math.Abs(delta.Z) <= BoxExtent.Z;
            }
            return Vector3.Distance(point, Position) <= SphereRadius;
        }
    }

    #endregion

    #region 天气效果子系统

    /// <summary>雨效果。</summary>
    public class RainEffect
    {
        public float RainIntensity { get; set; } = 0.5f;
        public float DropSize { get; set; } = 0.01f;
        public float DropSpeed { get; set; } = 15f;
        public float DropAngle { get; set; } = 5f; // 偏移角度
        public float SplashSize { get; set; } = 0.05f;
        public float RippleLifetime { get; set; } = 1f;
        public bool EnableRipples { get; set; } = true;
        public bool EnableSplashes { get; set; } = true;
        public string RainTexture { get; set; }
        public string SplashTexture { get; set; }
        public string RippleTexture { get; set; }

        // 受风影响
        public void ApplyWind(Vector3 windDirection, float windSpeed)
        {
            DropAngle = MathF.Atan2(windDirection.X, windSpeed) * (180f / MathF.PI) * 0.5f;
        }

        public void Update(float dt, float intensity)
        {
            RainIntensity = intensity;
        }
    }

    /// <summary>雪效果。</summary>
    public class SnowEffect
    {
        public float SnowIntensity { get; set; } = 0.5f;
        public float FlakeSize { get; set; } = 0.02f;
        public float FallSpeed { get; set; } = 1f;
        public float WindEffect { get; set; } = 0.3f;
        public float WobbleAmount { get; set; } = 0.5f;
        public float WobbleFrequency { get; set; } = 2f;
        public bool EnableAccumulation { get; set; } = true;
        public float AccumulationRate { get; set; } = 0.01f;
        public float MaxAccumulation { get; set; } = 0.5f;
        public float CurrentAccumulation { get; private set; }
        public bool EnableFootprints { get; set; }
        public string SnowTexture { get; set; }

        public void Update(float dt, float intensity)
        {
            SnowIntensity = intensity;

            if (EnableAccumulation)
            {
                CurrentAccumulation = Math.Min(
                    MaxAccumulation,
                    CurrentAccumulation + AccumulationRate * intensity * dt);
            }
        }

        public void ResetAccumulation() => CurrentAccumulation = 0f;
    }

    /// <summary>雾效果。</summary>
    public class FogEffect
    {
        public float Density { get; set; }
        public float Height { get; set; }
        public Vector3 Color { get; set; } = new Vector3(0.7f, 0.75f, 0.8f);
        public float StartDistance { get; set; } = 0f;
        public float EndDistance { get; set; } = 100f;
        public bool HeightFog { get; set; }
        public float HeightFogDensity { get; set; } = 0.1f;
        public float HeightFogFalloff { get; set; } = 0.2f;
        public float TransitionSpeed { get; set; } = 1f;
        public bool VolumetricFog { get; set; }
        public float VolumetricFogScattering { get; set; } = 1f;

        private float _currentDensity;

        public void Update(float dt, float targetDensity)
        {
            _currentDensity += (targetDensity - _currentDensity) * Math.Min(1f, TransitionSpeed * dt);
            Density = _currentDensity;
        }
    }

    /// <summary>沙尘效果。</summary>
    public class DustEffect
    {
        public float DustIntensity { get; set; }
        public float ParticleSize { get; set; } = 0.05f;
        public float Visibility { get; set; } = 10f;
        public Vector3 WindDirection { get; set; }
        public float WindSpeed { get; set; }
        public string DustTexture { get; set; }

        public void Update(float dt, float intensity, Vector3 windDir, float windSpd)
        {
            DustIntensity = intensity;
            WindDirection = windDir;
            WindSpeed = windSpd;
            Visibility = 10f * (1f - intensity);
        }
    }

    /// <summary>闪电效果。</summary>
    public class LightningEffect
    {
        public float Frequency { get; set; } // 次/分钟
        public float Intensity { get; set; } = 1f;
        public float FlashDuration { get; set; } = 0.1f;
        public float DoubleFlashInterval { get; set; } = 0.15f;
        public bool EnableDoubleFlash { get; set; } = true;
        public Vector3 LightColor { get; set; } = new Vector3(0.8f, 0.85f, 1f);
        public float LightRadius { get; set; } = 10000f;
        public float ThunderDelay { get; set; } = 3f; // 雷声延迟 (秒)
        public bool IsFlashing { get; private set; }
        public float FlashTimer { get; private set; }
        private float _lightningTimer;
        private float _currentFlashDuration;
        private int _flashCount;

        public event Action OnLightningFlash;
        public event Action OnThunderSound;

        public void Update(float dt)
        {
            if (Frequency <= 0f) return;

            _lightningTimer += dt;

            if (IsFlashing)
            {
                FlashTimer += dt;
                if (FlashTimer >= _currentFlashDuration)
                {
                    if (EnableDoubleFlash && _flashCount == 1)
                    {
                        _flashCount = 2;
                        FlashTimer = 0f;
                        _currentFlashDuration = FlashDuration * 0.6f;
                        OnLightningFlash?.Invoke();
                    }
                    else
                    {
                        IsFlashing = false;
                    }
                }
            }
            else
            {
                float interval = 60f / Math.Max(1f, Frequency);
                if (_lightningTimer >= interval)
                {
                    _lightningTimer = 0f;
                    TriggerFlash();
                }
            }
        }

        private void TriggerFlash()
        {
            IsFlashing = true;
            FlashTimer = 0f;
            _currentFlashDuration = FlashDuration;
            _flashCount = 0;
            OnLightningFlash?.Invoke();
            // 延迟雷声
            // Note: 需要异步延迟触发
        }

        public float GetCurrentFlashIntensity()
        {
            if (!IsFlashing) return 0f;
            return Intensity * Math.Max(0f, 1f - FlashTimer / _currentFlashDuration);
        }
    }

    /// <summary>风系统。</summary>
    public class WindSystem
    {
        public Vector3 Direction { get; set; } = Vector3.UnitX;
        public float Speed { get; set; } = 2f;
        public float GustSpeed { get; set; } = 0f;
        public float GustDuration { get; set; } = 1f;
        public float GustInterval { get; set; } = 5f;
        public float Turbulence { get; set; } = 0f;
        public bool IsGusting { get; private set; }
        public float CurrentGustSpeed { get; private set; }

        private float _gustTimer;
        private float _gustElapsed;

        // 影响目标
        public bool AffectParticles { get; set; } = true;
        public bool AffectVegetation { get; set; } = true;
        public bool AffectWater { get; set; } = true;
        public bool AffectCloth { get; set; } = true;
        public bool AffectFlags { get; set; } = true;

        /// <summary>获取当前有效风速 (含阵风)。</summary>
        public float GetEffectiveSpeed()
        {
            return Speed + (IsGusting ? CurrentGustSpeed : 0f);
        }

        /// <summary>获取当前有效风向 (含湍流扰动)。</summary>
        public Vector3 GetEffectiveDirection(float seed = 0f)
        {
            Vector3 dir = Direction;
            if (Turbulence > 0f)
            {
                float t = seed + _gustTimer;
                dir.X += MathF.Sin(t * 2.1f) * Turbulence * 0.3f;
                dir.Z += MathF.Cos(t * 1.7f) * Turbulence * 0.3f;
            }
            return Vector3.Normalize(dir);
        }

        public void Update(float dt)
        {
            if (GustSpeed <= 0f) return;

            if (IsGusting)
            {
                _gustElapsed += dt;
                float t = _gustElapsed / GustDuration;

                // 阵风包络 (先升后降)
                CurrentGustSpeed = GustSpeed * MathF.Sin(t * MathF.PI);

                if (_gustElapsed >= GustDuration)
                {
                    IsGusting = false;
                    _gustTimer = 0f;
                    CurrentGustSpeed = 0f;
                }
            }
            else
            {
                _gustTimer += dt;
                if (_gustTimer >= GustInterval)
                {
                    IsGusting = true;
                    _gustElapsed = 0f;
                }
            }
        }
    }

    #endregion

    #region SeasonSystem

    /// <summary>季节系统参数。</summary>
    public class SeasonParameters
    {
        public Season CurrentSeason { get; set; } = Season.Spring;
        public float DayLength { get; set; } = 12f;          // 小时
        public float YearProgress { get; set; }             // 0-1 (0=春分)

        // 各季节温度范围
        public float SpringTemp { get; set; } = 15f;
        public float SummerTemp { get; set; } = 30f;
        public float AutumnTemp { get; set; } = 15f;
        public float WinterTemp { get; set; } = -5f;

        // 植被
        public float VegetationGreenness { get; set; } = 1f;
        public float FoliageDensity { get; set; } = 1f;
        public bool HasSnowOnTrees { get; set; }
        public float LeafFallAmount { get; set; }

        public float GetSeasonTemperature()
        {
            return CurrentSeason switch
            {
                Season.Spring => SpringTemp,
                Season.Summer => SummerTemp,
                Season.Autumn => AutumnTemp,
                Season.Winter => WinterTemp,
                _ => SpringTemp
            };
        }

        public static Season GetSeasonFromProgress(float yearProgress)
        {
            if (yearProgress < 0.25f) return Season.Spring;
            if (yearProgress < 0.5f) return Season.Summer;
            if (yearProgress < 0.75f) return Season.Autumn;
            return Season.Winter;
        }

        public TimeOfDay GetTimeOfDay(float hourOfDay)
        {
            if (hourOfDay < 5f) return TimeOfDay.Midnight;
            if (hourOfDay < 7f) return TimeOfDay.Dawn;
            if (hourOfDay < 10f) return TimeOfDay.Morning;
            if (hourOfDay < 14f) return TimeOfDay.Noon;
            if (hourOfDay < 17f) return TimeOfDay.Afternoon;
            if (hourOfDay < 19f) return TimeOfDay.Evening;
            if (hourOfDay < 21f) return TimeOfDay.Dusk;
            return TimeOfDay.Night;
        }
    }

    #endregion

    #region WeatherSystem

    /// <summary>
    /// 天气系统主类。管理当前天气、目标天气、过渡和所有子系统。
    /// </summary>
    public class WeatherSystem
    {
        // ── 状态 ──
        public WeatherParameters CurrentWeather { get; private set; } = WeatherParameters.CreatePreset(WeatherType.Clear);
        public WeatherParameters TargetWeather { get; set; } = WeatherParameters.CreatePreset(WeatherType.Clear);
        public float TransitionProgress { get; private set; }
        public float TransitionSpeed { get; set; } = 0.5f; // 0-1 完成速度 (0.5 = 2秒完成)
        public bool IsTransitioning => TransitionProgress > 0 && TransitionProgress < 1;

        // ── 天气效果子系统 ──
        public RainEffect Rain { get; } = new RainEffect();
        public SnowEffect Snow { get; } = new SnowEffect();
        public FogEffect Fog { get; } = new FogEffect();
        public DustEffect Dust { get; } = new DustEffect();
        public LightningEffect Lightning { get; } = new LightningEffect();
        public WindSystem Wind { get; } = new WindSystem();

        // ── 天气体积 ──
        private readonly List<WeatherVolume> _volumes = new List<WeatherVolume>();
        public bool EnableVolumes { get; set; } = true;

        // ── 季节 ──
        public SeasonParameters Season { get; } = new SeasonParameters();
        public bool EnableSeasons { get; set; }
        public float TimeOfDayHours { get; set; } = 12f;
        public float TimeSpeed { get; set; } = 1f;

        // ── 天气影响 ──
        public float GroundWetness { get; private set; }     // 0-1 地面湿润度
        public float GroundFriction { get; private set; }     // 0-1 地面摩擦系数 (受雨影响)
        public float SoundDampening { get; private set; }     // 音效阻尼 (雨天/雾天)
        public bool VehicleSlippery { get; private set; }

        // ── 事件 ──
        public event Action<WeatherType> OnWeatherChanged;
        public event Action<Season> OnSeasonChanged;

        private WeatherType _lastWeatherType;

        /// <summary>设置目标天气 (开始过渡)。</summary>
        public void SetWeather(WeatherType type)
        {
            SetWeather(WeatherParameters.CreatePreset(type));
        }

        /// <summary>设置目标天气参数。</summary>
        public void SetWeather(WeatherParameters target)
        {
            TargetWeather = target;
            TransitionProgress = 0f;
        }

        /// <summary>立即设置天气 (无过渡)。</summary>
        public void SetWeatherImmediate(WeatherType type)
        {
            CurrentWeather = WeatherParameters.CreatePreset(type);
            TargetWeather = CurrentWeather;
            TransitionProgress = 1f;
            ApplyWeatherType(CurrentWeather.Type);
        }

        /// <summary>添加天气体积。</summary>
        public void AddVolume(WeatherVolume volume)
        {
            _volumes.Add(volume);
        }

        /// <summary>移除天气体积。</summary>
        public bool RemoveVolume(WeatherVolume volume) => _volumes.Remove(volume);

        /// <summary>获取当前体积内的天气 (如果有)。</summary>
        public WeatherParameters GetVolumeWeather(Vector3 position)
        {
            WeatherVolume best = null;
            float bestPriority = float.MinValue;

            foreach (var volume in _volumes)
            {
                if (volume.ContainsPoint(position) && volume.Priority > bestPriority)
                {
                    best = volume;
                    bestPriority = volume.Priority;
                }
            }

            return best?.Weather ?? CurrentWeather;
        }

        /// <summary>主更新。</summary>
        public void Update(float dt, Vector3? cameraPosition = null)
        {
            // 季节时间推进
            if (EnableSeasons)
            {
                TimeOfDayHours += dt * TimeSpeed;
                if (TimeOfDayHours >= 24f) TimeOfDayHours -= 24f;

                Season.YearProgress += dt * TimeSpeed * 0.0001f; // ~1年 ≈ 几小时实际时间
                if (Season.YearProgress >= 1f) Season.YearProgress -= 1f;

                Season.CurrentSeason = Season.GetSeasonFromProgress(Season.YearProgress);

                // 昼夜联动光照
                UpdateTimeOfDayLighting();
            }

            // 天气过渡
            if (IsTransitioning)
            {
                TransitionProgress = Math.Min(1f, TransitionProgress + TransitionSpeed * dt);
                CurrentWeather = WeatherParameters.Lerp(CurrentWeather, TargetWeather, TransitionSpeed * dt);

                // 检测天气类型变化
                if (CurrentWeather.Type != _lastWeatherType)
                {
                    ApplyWeatherType(CurrentWeather.Type);
                    OnWeatherChanged?.Invoke(CurrentWeather.Type);
                    _lastWeatherType = CurrentWeather.Type;
                }
            }

            // 体积天气检测
            if (EnableVolumes && cameraPosition.HasValue)
            {
                var volumeWeather = GetVolumeWeather(cameraPosition.Value);
                if (volumeWeather.Type != CurrentWeather.Type)
                {
                    TargetWeather = volumeWeather;
                    TransitionProgress = 0f;
                }
            }

            // 更新子系统
            UpdateSubsystems(dt);

            // 更新天气影响
            UpdateInfluences(dt);
        }

        private void ApplyWeatherType(WeatherType type)
        {
            // 根据天气类型启用/禁用子系统
            Rain.RainIntensity = type == WeatherType.Rain || type == WeatherType.HeavyRain || type == WeatherType.Thunderstorm
                ? CurrentWeather.PrecipitationIntensity : 0f;
            Snow.SnowIntensity = type == WeatherType.Snow || type == WeatherType.HeavySnow
                ? CurrentWeather.PrecipitationIntensity : 0f;
            Fog.Density = CurrentWeather.FogDensity;
            Dust.DustIntensity = type == WeatherType.Dust || type == WeatherType.Sandstorm
                ? (type == WeatherType.Sandstorm ? 1f : 0.5f) : 0f;
            Lightning.Frequency = type == WeatherType.Thunderstorm
                ? CurrentWeather.LightningFrequency : 0f;
            Wind.Speed = CurrentWeather.WindSpeed;
            Wind.Direction = CurrentWeather.GetWindDirectionVector();
            Wind.GustSpeed = CurrentWeather.GustSpeed;
            Wind.Turbulence = CurrentWeather.Turbulence;
        }

        private void UpdateSubsystems(float dt)
        {
            Rain.ApplyWind(Wind.GetEffectiveDirection(), Wind.GetEffectiveSpeed());
            Rain.Update(dt, Rain.RainIntensity);
            Snow.Update(dt, Snow.SnowIntensity);
            Fog.Update(dt, CurrentWeather.FogDensity);
            Dust.Update(dt, Dust.DustIntensity, Wind.GetEffectiveDirection(), Wind.GetEffectiveSpeed());
            Lightning.Update(dt);
            Wind.Update(dt);
        }

        private void UpdateInfluences(float dt)
        {
            // 地面湿润度 (雨天增加, 晴天减少)
            float rainFactor = Rain.RainIntensity;
            if (rainFactor > 0.1f)
                GroundWetness = Math.Min(1f, GroundWetness + rainFactor * dt * 0.5f);
            else
                GroundWetness = Math.Max(0f, GroundWetness - dt * 0.1f);

            // 地面摩擦 (湿滑时降低)
            GroundFriction = 1f - GroundWetness * 0.4f;
            VehicleSlippery = GroundWetness > 0.5f;

            // 音效阻尼 (雨天/雾天/沙尘暴时降低)
            float dampeningFactor = 0f;
            if (Rain.RainIntensity > 0.1f) dampeningFactor = Math.Max(dampeningFactor, Rain.RainIntensity * 0.3f);
            if (Fog.Density > 0.1f) dampeningFactor = Math.Max(dampeningFactor, Fog.Density * 0.2f);
            if (Dust.DustIntensity > 0.1f) dampeningFactor = Math.Max(dampeningFactor, Dust.DustIntensity * 0.15f);
            SoundDampening = dampeningFactor;
        }

        private void UpdateTimeOfDayLighting()
        {
            float hour = TimeOfDayHours;
            var tod = Season.GetTimeOfDay(hour);

            // 根据时间调整光照参数
            float sunAngle = ((hour - 6f) / 12f) * MathF.PI; // 6点日出, 18点日落

            if (tod == TimeOfDay.Night || tod == TimeOfDay.Midnight)
            {
                CurrentWeather.SunIntensity = 0.05f;
                CurrentWeather.AmbientIntensity = 0.05f;
                CurrentWeather.SkyColor = new Vector3(0.02f, 0.02f, 0.05f);
                CurrentWeather.SkyBrightness = 0.1f;
            }
            else if (tod == TimeOfDay.Dawn || tod == TimeOfDay.Dusk)
            {
                float t = tod == TimeOfDay.Dawn
                    ? (hour - 5f) / 2f
                    : (hour - 17f) / 2f;
                CurrentWeather.SunIntensity = 0.3f + t * 0.4f;
                CurrentWeather.AmbientIntensity = 0.15f;
                CurrentWeather.SkyColor = Vector3.Lerp(
                    new Vector3(0.8f, 0.3f, 0.1f),
                    new Vector3(0.3f, 0.5f, 0.9f), t);
                CurrentWeather.SkyBrightness = 0.4f + t * 0.3f;
            }
            else
            {
                CurrentWeather.SunIntensity = 1f;
                CurrentWeather.AmbientIntensity = 0.3f;
                CurrentWeather.SkyColor = new Vector3(0.3f, 0.5f, 0.9f);
                CurrentWeather.SkyBrightness = 1f;
            }
        }
    }

    #endregion
}
