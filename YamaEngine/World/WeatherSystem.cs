using System;
using System.Collections.Generic;
using System.Numerics;
using YamaEngine.Audio;
using YamaEngine.Particles;
using YamaEngine.RHI;
using YamaEngine.Rendering;

namespace YamaEngine.World
{
    // ════════════════════════════════════════════════════════════════════════
    // yama引擎 天气与昼夜循环系统 (Weather & Day-Night Cycle System)
    // --------------------------------------------------------------------------
    // 设计目标：提供一套完整的世界级动态环境系统，覆盖昼夜更替与多天气状态。
    //   - 昼夜循环 (Day/Night Cycle)：太阳/月亮位置的天文计算、天空颜色随
    //       太阳高度角过渡、夜间星光强度与闪烁
    //   - 天气状态机：晴天 / 多云 / 雨天 / 雪天 / 雾天 / 暴风雨 六种状态
    //   - 天气过渡：在两种天气间按时长平滑混合所有视觉参数
    //   - 体积云渲染接口：与 AtmosphereRenderer.VolumetricCloudRenderer 对接，
    //       动态更新云覆盖率/密度/风速
    //   - 降水效果：雨滴/雪花 GPU 粒子（经 GPUParticleSystem 模拟）
    //   - 雾效：指数雾 / 高度雾 / 体积雾，写入大气散射参数
    //   - 闪电：随机触发 + 延迟照明"天花板"（环境光瞬时提升后衰减）
    //   - 风力场：全局风向/风速 + Curl Noise 扰动，驱动粒子与植被摆动
    //   - 天气对环境的影响：地表湿润度、积水深度、积雪厚度累积与蒸发
    //   - 大气散射参数动态更新：太阳方向/照度/瑞利系数随时间变化
    //   - 与 AtmosphereRenderer 共享同一 AtmosphereSettings 实例，实时驱动渲染
    // ════════════════════════════════════════════════════════════════════════

    // ──────────────────────────────────────────────────────────────────────────
    // 枚举
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>天气类型。每种类型对应一组完整的视觉/物理参数配置。</summary>
    public enum WeatherType
    {
        /// <summary>晴天：少云、高照度、无降水。</summary>
        Clear,
        /// <summary>多云：中高云量、照度略降、无降水。</summary>
        Cloudy,
        /// <summary>雨天：厚云、降水、湿润度上升。</summary>
        Rain,
        /// <summary>雪天：降水为雪、积雪累积、低温。</summary>
        Snow,
        /// <summary>雾天：高雾密度、能见度骤降。</summary>
        Fog,
        /// <summary>暴风雨：厚云 + 强风 + 雨 + 闪电。</summary>
        Storm
    }

    /// <summary>雾的类型。</summary>
    public enum FogType
    {
        /// <summary>无雾。</summary>
        None,
        /// <summary>指数雾：密度随距离指数衰减，适合均匀雾霾。</summary>
        Exponential,
        /// <summary>高度雾：雾集中在低海拔，越高越稀薄。</summary>
        Height,
        /// <summary>体积雾：参与光照散射的参与介质，最真实也最昂贵。</summary>
        Volumetric
    }

    /// <summary>降水粒子类型。</summary>
    public enum PrecipitationType
    {
        /// <summary>无降水。</summary>
        None,
        /// <雨滴：高速下落、细长条形。</summary>
        Rain,
        /// <summary>雪花：低速飘落、随风摆动。</summary>
        Snow
    }

    /// <summary>天气过渡方式。</summary>
    public enum WeatherTransitionMode
    {
        /// <summary>立即切换（调试用）。</summary>
        Instant,
        /// <summary>线性混合。</summary>
        Linear,
        /// <summary>缓入缓出混合（推荐，视觉自然）。</summary>
        EaseInOut
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 天体 (CelestialBody)：太阳/月亮位置的天文计算
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 天体（太阳或月亮）的运行状态。基于简化天文模型由"当日时间"与"纬度"
    /// 计算高度角/方位角，进而得到世界空间方向向量。太阳用于驱动大气散射；
    /// 月亮用于夜间月光照明与海面高光。
    /// </summary>
    public struct CelestialBody
    {
        /// <summary>高度角（弧度，0=地平线，π/2=正上方）。</summary>
        public float Elevation;
        /// <summary>方位角（弧度，正北=0，顺时针）。</summary>
        public float Azimuth;
        /// <summary>世界空间方向（指向天体，已归一化）。</summary>
        public Vector3 Direction;
        /// <summary>照明强度 (0~1)，随高度角变化（地平线下为 0）。</summary>
        public float Intensity;

        /// <summary>
        /// 由时间与纬度计算太阳位置（简化模型）。
        /// </summary>
        /// <param name="hour">当日时间（0~24 小时，12=正午）。</param>
        /// <param name="latitude">纬度（弧度，北半球为正）。</param>
        /// <param name="declination">太阳赤纬（弧度，夏至≈+23.45°，冬至≈-23.45°）。</param>
        public static CelestialBody ComputeSun(float hour, float latitude, float declination)
        {
            var body = new CelestialBody();
            // 时角：正午为 0，每小时 15°（π/12 弧度）
            float hourAngle = (hour - 12f) * (MathF.PI / 12f);
            float sinLat = MathF.Sin(latitude);
            float cosLat = MathF.Cos(latitude);
            float sinDec = MathF.Sin(declination);
            float cosDec = MathF.Cos(declination);

            // 太阳高度角：sin(alt) = sin(φ)sin(δ) + cos(φ)cos(δ)cos(H)
            float sinAlt = sinLat * sinDec + cosLat * cosDec * MathF.Cos(hourAngle);
            body.Elevation = MathF.Asin(Math.Clamp(sinAlt, -1f, 1f));

            // 太阳方位角（正南为 0，东为负、西为正）
            float cosAz = (sinDec - sinAlt * sinLat) / (cosLat * MathF.Max(0.0001f, MathF.Cos(body.Elevation)));
            float azimuth = MathF.Acos(Math.Clamp(cosAz, -1f, 1f));
            if (hourAngle > 0f) azimuth = -azimuth; // 下午太阳在西
            body.Azimuth = azimuth + MathF.PI; // 转为正北=0 顺时针

            // 世界方向：X=东, Y=上, Z=北 -> 转换为引擎坐标（Y 朝上）
            float alt = body.Elevation;
            float az = body.Azimuth;
            body.Direction = Vector3.Normalize(new Vector3(
                MathF.Sin(az) * MathF.Cos(alt),
                MathF.Sin(alt),
                MathF.Cos(az) * MathF.Cos(alt)));

            // 照明强度：地平线附近过渡，地平线下为 0
            body.Intensity = Math.Clamp(MathF.Sin(alt) * 1.2f, 0f, 1f);
            // 日出/日落余晖：高度角在 [-0.1, 0.1] 时给予少量环境贡献
            if (alt > -0.15f && alt < 0.1f)
                body.Intensity = MathF.Max(body.Intensity, 0.12f);
            return body;
        }

        /// <summary>
        /// 月亮位置：简化为与太阳近似相反的相位，月相用相位角控制亮度。
        /// </summary>
        /// <param name="hour">当日时间。</param>
        /// <param name="latitude">纬度。</param>
        /// <param name="phase">月相 [0,1]（0=新月,0.5=满月,1=新月）。</param>
        public static CelestialBody ComputeMoon(float hour, float latitude, float phase)
        {
            // 月亮大约在太阳对面方向升起，相位偏移约 12 小时 * 月相
            float moonHour = (hour + 12f + phase * 12f) % 24f;
            // 月亮赤纬简化为与太阳相反
            var body = ComputeSun(moonHour, latitude, -23.45f * MathF.PI / 180f);
            // 月光照度：满月约 0.05~0.1 lux 相对量级
            float phaseBrightness = 1f - MathF.Abs(phase - 0.5f) * 2f; // 0..1
            body.Intensity *= 0.1f * phaseBrightness;
            return body;
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 昼夜循环 (DayNightCycle)
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 昼夜循环控制器。维护"当日时间"并按真实/缩放速率推进，计算太阳与月亮
    /// 位置，插值天空颜色，控制星光强度。把太阳方向/照度写入共享的
    /// <see cref="AtmosphereSettings"/>，从而驱动整个大气渲染管线。
    /// </summary>
    public class DayNightCycle
    {
        /// <summary>当日时间（小时，0~24）。12=正午，0/24=午夜。</summary>
        public float TimeOfDay = 8f;
        /// <summary>一天对应的现实秒数（默认 1200 秒=20 分钟一个昼夜）。</summary>
        public float DayLengthSeconds = 1200f;
        /// <summary>场景纬度（弧度，北半球为正）。</summary>
        public float Latitude = 35f * MathF.PI / 180f;
        /// <summary>一年中的天数 [1,365]，用于计算太阳赤纬。</summary>
        public int DayOfYear = 172;
        /// <summary>月相 [0,1]。</summary>
        public float MoonPhase = 0.5f;

        /// <summary>当前太阳。</summary>
        public CelestialBody Sun;
        /// <summary>当前月亮。</summary>
        public CelestialBody Moon;

        // ── 天空颜色关键帧（按太阳高度角过渡）──
        /// <summary>正午天顶颜色。</summary>
        public Vector3 DayZenithColor = new(0.25f, 0.45f, 0.85f);
        /// <summary>正午地平线颜色。</summary>
        public Vector3 DayHorizonColor = new(0.65f, 0.78f, 0.92f);
        /// <summary>日出/日落地平线颜色。</summary>
        public Vector3 SunsetColor = new(0.95f, 0.45f, 0.20f);
        /// <summary>夜间天顶颜色。</summary>
        public Vector3 NightZenithColor = new(0.02f, 0.03f, 0.08f);
        /// <summary>夜间地平线颜色。</summary>
        public Vector3 NightHorizonColor = new(0.05f, 0.06f, 0.12f);

        /// <summary>当前天顶颜色（插值后）。</summary>
        public Vector3 CurrentZenithColor;
        /// <summary>当前地平线颜色（插值后）。</summary>
        public Vector3 CurrentHorizonColor;
        /// <summary>当前星光强度 [0,1]。</summary>
        public float StarIntensity;
        /// <summary>太阳照度倍率（写入 AtmosphereSettings.SunIlluminance）。</summary>
        public float SunIlluminanceScale = 100000f;
        /// <summary>月光照度倍率。</summary>
        public float MoonIlluminanceScale = 0.3f;

        /// <summary>共享的大气设置（由 WeatherSystem 注入，与 AtmosphereRenderer 共用同一实例）。</summary>
        public AtmosphereSettings? Atmosphere;

        /// <summary>推进时间并重算所有派生量。</summary>
        public void Update(float dt)
        {
            // 推进当日时间
            TimeOfDay += (24f / DayLengthSeconds) * dt;
            if (TimeOfDay >= 24f) TimeOfDay -= 24f;
            if (TimeOfDay < 0f) TimeOfDay += 24f;

            // 计算太阳赤纬（简化年度模型）
            float declination = 23.45f * MathF.PI / 180f *
                MathF.Sin(2f * MathF.PI * (DayOfYear - 81) / 365f);

            Sun = CelestialBody.ComputeSun(TimeOfDay, Latitude, declination);
            Moon = CelestialBody.ComputeMoon(TimeOfDay, Latitude, MoonPhase);

            // 由太阳高度角混合天空颜色
            BlendSkyColors(Sun.Elevation);

            // 星光强度：太阳低于地平线时显现
            StarIntensity = Math.Clamp(-Sun.Elevation * 2.5f, 0f, 1f);

            // 写入大气设置（驱动渲染）
            if (Atmosphere != null)
            {
                Atmosphere.SunDirection = Sun.Direction;
                // 太阳在地平线附近偏橙红（瑞利散射增强），高空偏白
                float alt = Sun.Elevation;
                if (alt > 0f)
                {
                    float dayBlend = Math.Clamp(alt / (MathF.PI * 0.25f), 0f, 1f);
                    Atmosphere.SunIlluminance = SunIlluminanceScale * dayBlend;
                }
                else
                {
                    Atmosphere.SunIlluminance = 0f;
                }
            }
        }

        /// <summary>根据太阳高度角在天/夜/日落三态间插值天空颜色。</summary>
        private void BlendSkyColors(float sunAlt)
        {
            // sunAlt > 10° : 白天
            // 0° < sunAlt < 10° : 日出/日落过渡
            // sunAlt < 0° : 夜间
            if (sunAlt > 0.1745f) // > 10°
            {
                CurrentZenithColor = DayZenithColor;
                CurrentHorizonColor = DayHorizonColor;
            }
            else if (sunAlt > 0f)
            {
                float t = sunAlt / 0.1745f; // 0..1
                CurrentHorizonColor = Vector3.Lerp(SunsetColor, DayHorizonColor, t);
                CurrentZenithColor = Vector3.Lerp(new Vector3(0.15f, 0.18f, 0.40f), DayZenithColor, t);
            }
            else
            {
                float t = Math.Clamp(-sunAlt / 0.1745f, 0f, 1f); // 0..1 入夜
                CurrentHorizonColor = Vector3.Lerp(SunsetColor * 0.4f, NightHorizonColor, t);
                CurrentZenithColor = Vector3.Lerp(new Vector3(0.08f, 0.10f, 0.25f), NightZenithColor, t);
            }
        }

        /// <summary>设置到指定时间（小时）。</summary>
        public void SetTime(float hour) { TimeOfDay = hour % 24f; }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 天气状态配置 (WeatherProfile)
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 单种天气的完整参数配置。包含云、降水、雾、风、闪电、环境效果等参数。
    /// 天气过渡即在两个 Profile 之间按权重混合这些数值。
    /// </summary>
    public class WeatherProfile
    {
        public WeatherType Type;
        /// <summary>云覆盖率 [0,1]。</summary>
        public float CloudCoverage = 0.3f;
        /// <summary>云密度倍率。</summary>
        public float CloudDensity = 1f;
        /// <summary>降水类型。</summary>
        public PrecipitationType Precipitation = PrecipitationType.None;
        /// <summary>降水强度（粒子发射速率倍率）。</summary>
        public float PrecipitationIntensity = 1f;
        /// <summary>雾类型。</summary>
        public FogType Fog = FogType.None;
        /// <summary>雾密度。</summary>
        public float FogDensity = 0.02f;
        /// <summary>雾起始高度（高度雾用）。</summary>
        public float FogStartHeight = 0f;
        /// <summary>雾衰减高度（高度雾用，越高越稀薄）。</summary>
        public float FogHeightFalloff = 0.5f;
        /// <summary>风向（水平，归一化）。</summary>
        public Vector2 WindDirection = new(1f, 0f);
        /// <summary>风速 (m/s)。</summary>
        public float WindSpeed = 5f;
        /// <summary>风湍流强度（Curl Noise 幅度）。</summary>
        public float WindTurbulence = 0.2f;
        /// <summary>是否启用闪电。</summary>
        public bool EnableLightning;
        /// <summary>闪电频率（每分钟次数）。</summary>
        public float LightningFrequency = 6f;
        /// <summary>环境光倍率（影响整体明暗）。</summary>
        public float AmbientScale = 1f;
        /// <summary>天顶颜色染色。</summary>
        public Vector3 SkyTint = new(1f, 1f, 1f);
        /// <summary>雾颜色。</summary>
        public Vector3 FogColor = new(0.7f, 0.75f, 0.8f);

        /// <summary>构造默认 Profile。</summary>
        public WeatherProfile(WeatherType type) { Type = type; ConfigureByType(); }

        private void ConfigureByType()
        {
            switch (Type)
            {
                case WeatherType.Clear:
                    CloudCoverage = 0.15f; CloudDensity = 0.8f;
                    Fog = FogType.None; WindSpeed = 3f; WindTurbulence = 0.1f;
                    AmbientScale = 1.1f; SkyTint = new Vector3(1f, 1f, 1f);
                    break;
                case WeatherType.Cloudy:
                    CloudCoverage = 0.7f; CloudDensity = 1.1f;
                    Fog = FogType.Exponential; FogDensity = 0.015f;
                    WindSpeed = 8f; AmbientScale = 0.85f;
                    SkyTint = new Vector3(0.85f, 0.87f, 0.92f);
                    break;
                case WeatherType.Rain:
                    CloudCoverage = 0.9f; CloudDensity = 1.3f;
                    Precipitation = PrecipitationType.Rain; PrecipitationIntensity = 1.2f;
                    Fog = FogType.Exponential; FogDensity = 0.03f;
                    WindSpeed = 10f; WindTurbulence = 0.3f;
                    AmbientScale = 0.6f; SkyTint = new Vector3(0.6f, 0.65f, 0.72f);
                    FogColor = new Vector3(0.55f, 0.58f, 0.62f);
                    break;
                case WeatherType.Snow:
                    CloudCoverage = 0.85f; CloudDensity = 1.2f;
                    Precipitation = PrecipitationType.Snow; PrecipitationIntensity = 0.8f;
                    Fog = FogType.Height; FogDensity = 0.025f; FogHeightFalloff = 0.3f;
                    WindSpeed = 6f; WindTurbulence = 0.5f;
                    AmbientScale = 1.2f; SkyTint = new Vector3(0.9f, 0.92f, 0.96f);
                    FogColor = new Vector3(0.82f, 0.85f, 0.9f);
                    break;
                case WeatherType.Fog:
                    CloudCoverage = 0.5f; CloudDensity = 1f;
                    Fog = FogType.Volumetric; FogDensity = 0.06f; FogHeightFalloff = 0.4f;
                    WindSpeed = 1f; AmbientScale = 0.75f;
                    SkyTint = new Vector3(0.8f, 0.82f, 0.85f);
                    FogColor = new Vector3(0.78f, 0.8f, 0.83f);
                    break;
                case WeatherType.Storm:
                    CloudCoverage = 0.98f; CloudDensity = 1.5f;
                    Precipitation = PrecipitationType.Rain; PrecipitationIntensity = 2f;
                    Fog = FogType.Exponential; FogDensity = 0.04f;
                    WindSpeed = 20f; WindTurbulence = 0.6f;
                    EnableLightning = true; LightningFrequency = 12f;
                    AmbientScale = 0.4f; SkyTint = new Vector3(0.35f, 0.38f, 0.45f);
                    FogColor = new Vector3(0.3f, 0.32f, 0.36f);
                    break;
            }
        }

        /// <summary>在两个 Profile 间按权重 t [0,1] 线性混合，返回新 Profile。</summary>
        public static WeatherProfile Lerp(WeatherProfile a, WeatherProfile b, float t)
        {
            var r = new WeatherProfile(b.Type) // 类型取目标
            {
                CloudCoverage = MathHelper.Lerp(a.CloudCoverage, b.CloudCoverage, t),
                CloudDensity = MathHelper.Lerp(a.CloudDensity, b.CloudDensity, t),
                Precipitation = t > 0.5f ? b.Precipitation : a.Precipitation,
                PrecipitationIntensity = MathHelper.Lerp(a.PrecipitationIntensity, b.PrecipitationIntensity, t),
                Fog = t > 0.5f ? b.Fog : a.Fog,
                FogDensity = MathHelper.Lerp(a.FogDensity, b.FogDensity, t),
                FogStartHeight = MathHelper.Lerp(a.FogStartHeight, b.FogStartHeight, t),
                FogHeightFalloff = MathHelper.Lerp(a.FogHeightFalloff, b.FogHeightFalloff, t),
                WindDirection = Vector2.Normalize(Vector2.Lerp(a.WindDirection, b.WindDirection, t)),
                WindSpeed = MathHelper.Lerp(a.WindSpeed, b.WindSpeed, t),
                WindTurbulence = MathHelper.Lerp(a.WindTurbulence, b.WindTurbulence, t),
                EnableLightning = b.EnableLightning && t > 0.5f,
                LightningFrequency = MathHelper.Lerp(a.LightningFrequency, b.LightningFrequency, t),
                AmbientScale = MathHelper.Lerp(a.AmbientScale, b.AmbientScale, t),
                SkyTint = Vector3.Lerp(a.SkyTint, b.SkyTint, t),
                FogColor = Vector3.Lerp(a.FogColor, b.FogColor, t)
            };
            return r;
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 雾效系统 (FogSystem)
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 雾效系统。把当前雾参数写入大气设置，支持指数雾/高度雾/体积雾三种模型。
    /// 体积雾额外提供一个参与介质密度场接口（由渲染管线采样）。
    /// </summary>
    public class FogSystem
    {
        /// <summary>雾类型。</summary>
        public FogType Type = FogType.None;
        /// <summary>雾密度。</summary>
        public float Density = 0.02f;
        /// <summary>雾起始高度。</summary>
        public float StartHeight;
        /// <summary>高度衰减。</summary>
        public float HeightFalloff = 0.5f;
        /// <summary>雾颜色。</summary>
        public Vector3 Color = new(0.7f, 0.75f, 0.8f);
        /// <summary>体积雾参与介质最大距离。</summary>
        public float VolumetricMaxDistance = 2000f;

        /// <summary>共享大气设置。</summary>
        public AtmosphereSettings? Atmosphere;

        /// <summary>按当前参数更新大气雾设置。</summary>
        public void Apply()
        {
            if (Atmosphere == null) return;
            switch (Type)
            {
                case FogType.None:
                    Atmosphere.FogDensity = 0f;
                    Atmosphere.EnableAerialPerspective = true;
                    break;
                case FogType.Exponential:
                case FogType.Volumetric:
                    Atmosphere.FogDensity = Density;
                    Atmosphere.FogMaxDistance = VolumetricMaxDistance;
                    Atmosphere.EnableAerialPerspective = true;
                    break;
                case FogType.Height:
                    // 高度雾用低密度 + 限制最大距离近似
                    Atmosphere.FogDensity = Density;
                    Atmosphere.FogMaxDistance = 3000f;
                    Atmosphere.EnableAerialPerspective = true;
                    break;
            }
        }

        /// <summary>
        /// 采样高度雾密度。海拔越高密度越低：d(h) = density * exp(-(h-start)/falloff)。
        /// 供体积雾着色器或 CPU 端遮挡查询使用。
        /// </summary>
        public float SampleHeightFog(float worldHeight)
        {
            if (Type != FogType.Height && Type != FogType.Volumetric) return Density;
            float h = worldHeight - StartHeight;
            return Density * MathF.Exp(-MathF.Max(0f, h) / MathF.Max(0.01f, HeightFalloff));
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 风力场 (WindField)
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 全局风力场。提供方向/风速，并叠加 Curl Noise 扰动，供植被摆动、
    /// 粒子漂移、体积云运动、水面波纹共同采样。
    /// </summary>
    public class WindField
    {
        /// <summary>全局风向（水平，归一化）。</summary>
        public Vector2 Direction = new(1f, 0f);
        /// <summary>全局风速 (m/s)。</summary>
        public float Speed = 5f;
        /// <summary>湍流强度（噪声幅度占比）。</summary>
        public float Turbulence = 0.2f;
        /// <summary>阵风频率 (Hz)。</summary>
        public float GustFrequency = 0.3f;

        private float _time;

        /// <summary>推进风力场时间，更新阵风相位。</summary>
        public void Update(float dt) { _time += dt; }

        /// <summary>
        /// 采样某点的风力向量（含湍流扰动）。简化模型：在主方向上叠加
        /// 基于位置与时间的正弦扰动，模拟阵风。
        /// </summary>
        public Vector3 Sample(Vector3 worldPos)
        {
            // 主风（水平）
            Vector3 baseWind = new Vector3(Direction.X, 0f, Direction.Y) * Speed;

            // 阵风：低频幅度调制
            float gust = 1f + Turbulence * MathF.Sin(_time * GustFrequency * 2f * MathF.PI
                + worldPos.X * 0.05f + worldPos.Z * 0.05f);

            // 湍流扰动（双正弦近似 Curl Noise）
            float turbX = Turbulence * Speed * 0.5f *
                MathF.Sin(_time * 1.3f + worldPos.Z * 0.1f);
            float turbZ = Turbulence * Speed * 0.5f *
                MathF.Cos(_time * 1.1f + worldPos.X * 0.1f);

            return new Vector3(baseWind.X * gust + turbX, 0f, baseWind.Z * gust + turbZ);
        }

        /// <summary>瞬时风速标量（含阵风）。</summary>
        public float CurrentSpeed => Speed * (1f + Turbulence * MathF.Sin(_time * GustFrequency * 2f * MathF.PI));
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 降水系统 (PrecipitationSystem)
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 降水系统。基于 GPU 粒子系统发射雨滴/雪花。雨滴高速竖直下落、细长拉伸；
    /// 雪花低速飘落、随风摆动。降水强度控制发射速率。
    /// </summary>
    public class PrecipitationSystem
    {
        /// <summary>GPU 粒子系统引用。</summary>
        public GPUParticleSystem? Particles;
        /// <summary>降水类型。</summary>
        public PrecipitationType Type = PrecipitationType.None;
        /// <summary>降水强度（发射速率倍率）。</summary>
        public float Intensity = 1f;
        /// <summary>降水覆盖范围半径（粒子发射盒半边长）。</summary>
        public float CoverageRadius = 500f;
        /// <summary>降水高度（发射盒顶部相对相机的高度）。</summary>
        public float Height = 300f;
        /// <summary>当前相机位置（发射盒跟随相机）。</summary>
        public Vector3 CameraPosition;

        private bool _emitting;

        /// <summary>按当前参数更新降水发射状态。</summary>
        public void Update(float dt, Vector3 cameraPos, in WindField wind)
        {
            CameraPosition = cameraPos;
            if (Particles == null) return;

            bool shouldEmit = Type != PrecipitationType.None && Intensity > 0.01f;
            if (shouldEmit && !_emitting)
            {
                StartEmission(wind);
                _emitting = true;
            }
            else if (!shouldEmit && _emitting)
            {
                StopEmission();
                _emitting = false;
            }
            else if (_emitting)
            {
                UpdateEmission(wind);
            }
        }

        /// <summary>启动降水发射（配置发射器参数）。</summary>
        private void StartEmission(in WindField wind)
        {
            // 实际引擎中：在此创建/配置 GPUEmitterDesc
            // 雨滴：高速下落、细长条形、强重力
            // 雪花：低速、随风摆动、较大阻力
            // 这里给出参数语义注释，具体下发至 GPUParticleSystem 的发射器描述。
            // GPUEmitterDesc 描述见 Particles/GPUParticleSystem.cs
        }

        /// <summary>每帧更新发射参数（跟随相机、随风偏移）。</summary>
        private void UpdateEmission(in WindField wind)
        {
            // 发射盒中心跟随相机，并根据风速横向偏移下落方向
            // 雨滴受风影响偏角 = atan(windSpeed / fallSpeed)
            Vector3 windVec = wind.Sample(CameraPosition);
            // 此处可更新发射器 Position / 初始速度方向
        }

        /// <summary>停止降水发射。</summary>
        private void StopEmission()
        {
            // 把发射速率置零，让现存粒子自然消亡
        }

        /// <summary>切换降水类型。</summary>
        public void SetType(PrecipitationType type)
        {
            if (Type == type) return;
            if (_emitting) StopEmission();
            Type = type;
            _emitting = false;
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 闪电系统 (LightningSystem)
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 闪电系统。在暴风雨天气下按频率随机触发闪电，触发后产生一次"延迟照明
    /// 天花板"：环境光强度瞬时提升，随后按指数衰减，模拟闪电照亮整个天空。
    /// 同时支持雷声音频播放（经 AudioSystem 延迟播放）。
    /// </summary>
    public class LightningSystem
    {
        /// <summary>是否启用。</summary>
        public bool Enabled;
        /// <summary>闪电频率（每分钟次数）。</summary>
        public float FrequencyPerMinute = 6f;
        /// <summary>闪光强度峰值。</summary>
        public float PeakIntensity = 3f;
        /// <summary>闪光衰减时间常数（秒，指数衰减）。</summary>
        public float DecayTau = 0.25f;
        /// <summary>闪光到雷声的延迟（秒，按距离近似，约 3s/km）。</summary>
        public float ThunderDelay = 2f;
        /// <summary>雷声音频片段名。</summary>
        public string ThunderClipName = "Thunder";

        /// <summary>当前闪光强度（衰减中）。</summary>
        public float FlashIntensity;
        /// <summary>音频系统引用（播放雷声）。</summary>
        public AudioSystem? Audio;

        private float _nextStrikeTimer;
        private float _thunderCountdown = -1f;
        private Vector3 _strikePosition;
        private readonly Random _rng = new();

        /// <summary>每帧更新：倒计时触发、衰减闪光、延迟播放雷声。</summary>
        public void Update(float dt, Vector3 listenerPos, float coverageRadius)
        {
            if (!Enabled) { FlashIntensity = 0f; return; }

            // 衰减闪光
            FlashIntensity *= MathF.Exp(-dt / MathF.Max(0.001f, DecayTau));
            if (FlashIntensity < 0.001f) FlashIntensity = 0f;

            // 倒计时下一次闪电
            _nextStrikeTimer -= dt;
            if (_nextStrikeTimer <= 0f)
            {
                Strike(listenerPos, coverageRadius);
                // 重置倒计时：在平均间隔附近随机
                float avgInterval = 60f / MathF.Max(0.1f, FrequencyPerMinute);
                _nextStrikeTimer = avgInterval * (0.5f + (float)_rng.NextDouble());
            }

            // 延迟雷声
            if (_thunderCountdown > 0f)
            {
                _thunderCountdown -= dt;
                if (_thunderCountdown <= 0f)
                {
                    Audio?.PlayOneShot(Audio.GetClip(ThunderClipName), _strikePosition, 1f, 0.9f);
                    _thunderCountdown = -1f;
                }
            }
        }

        /// <summary>触发一次闪电。</summary>
        private void Strike(Vector3 listenerPos, float coverageRadius)
        {
            FlashIntensity = PeakIntensity * (0.7f + 0.3f * (float)_rng.NextDouble());
            // 在听众周围随机位置生成闪电
            float ang = (float)_rng.NextDouble() * MathF.PI * 2f;
            float dist = (float)_rng.NextDouble() * coverageRadius;
            _strikePosition = listenerPos + new Vector3(MathF.Cos(ang) * dist, 0f, MathF.Sin(ang) * dist);
            // 雷声延迟按距离
            float delay = Vector3.Distance(listenerPos, _strikePosition) / 343f;
            _thunderCountdown = MathF.Max(0.2f, delay);
        }

        /// <summary>立即停止所有闪光。</summary>
        public void Reset() { FlashIntensity = 0f; _thunderCountdown = -1f; _nextStrikeTimer = 0f; }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 天气环境效果 (WeatherEnvironmentEffects)
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 天气对环境的累积影响。维护地表湿润度、积水深度、积雪厚度三项标量场，
    /// 它们随天气状态缓慢累积或蒸发，并供材质/着色器采样以表现湿润反光、
    /// 积水镜面、积雪覆盖等效果。
    /// </summary>
    public class WeatherEnvironmentEffects
    {
        /// <summary>地表湿润度 [0,1]。</summary>
        public float Wetness;
        /// <summary>积水深度 (m)。</summary>
        public float PuddleDepth;
        /// <summary>积雪厚度 (m)。</summary>
        public float SnowAccumulation;

        /// <summary>湿润累积速率（每秒，雨天为正）。</summary>
        public float WetnessAccumulateRate = 0.05f;
        /// <summary>湿润蒸发速率（每秒，晴天为正）。</summary>
        public float WetnessEvaporateRate = 0.02f;
        /// <summary>积雪累积速率。</summary>
        public float SnowAccumulateRate = 0.01f;
        /// <summary>积雪融化速率。</summary>
        public float SnowMeltRate = 0.005f;

        /// <summary>
        /// 按当前天气类型推进环境效果累积/消散。
        /// </summary>
        public void Update(float dt, WeatherType current, float precipitationIntensity)
        {
            switch (current)
            {
                case WeatherType.Rain:
                case WeatherType.Storm:
                    Wetness = MathF.Min(1f, Wetness + WetnessAccumulateRate * precipitationIntensity * dt);
                    PuddleDepth = MathF.Min(0.05f, PuddleDepth + 0.002f * precipitationIntensity * dt);
                    SnowAccumulation = MathF.Max(0f, SnowAccumulation - SnowMeltRate * dt);
                    break;
                case WeatherType.Snow:
                    SnowAccumulation = MathF.Min(2f, SnowAccumulation + SnowAccumulateRate * precipitationIntensity * dt);
                    Wetness = MathF.Max(0f, Wetness - WetnessEvaporateRate * 0.5f * dt);
                    break;
                case WeatherType.Clear:
                    Wetness = MathF.Max(0f, Wetness - WetnessEvaporateRate * dt);
                    PuddleDepth = MathF.Max(0f, PuddleDepth - 0.001f * dt);
                    SnowAccumulation = MathF.Max(0f, SnowAccumulation - SnowMeltRate * dt);
                    break;
                default:
                    Wetness = MathF.Max(0f, Wetness - WetnessEvaporateRate * 0.3f * dt);
                    break;
            }
        }

        /// <summary>重置所有效果（场景切换时）。</summary>
        public void Reset() { Wetness = 0f; PuddleDepth = 0f; SnowAccumulation = 0f; }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 体积云渲染接口 (VolumetricCloudInterface)
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 体积云渲染接口。把天气系统的云参数（覆盖率/密度/风向/风速）下发到
    /// <see cref="AtmosphereRenderer"/> 共享的 <see cref="AtmosphereSettings"/>，
    /// 由 VolumetricCloudRenderer 在光线步进时采样。本类仅负责参数写入与
    /// 天气驱动的云层演化（如暴风雨时云量上升）。
    /// </summary>
    public class VolumetricCloudInterface
    {
        /// <summary>共享大气设置。</summary>
        public AtmosphereSettings? Atmosphere;
        /// <summary>当前云覆盖率。</summary>
        public float Coverage = 0.5f;
        /// <summary>当前云密度。</summary>
        public float Density = 1f;
        /// <summary>云层风向。</summary>
        public Vector2 WindDirection = new(1f, 0f);
        /// <summary>云层风速。</summary>
        public float WindSpeed = 15f;
        /// <summary>是否启用体积云。</summary>
        public bool Enabled = true;

        /// <summary>把云参数写入大气设置。</summary>
        public void Apply()
        {
            if (Atmosphere == null) return;
            Atmosphere.EnableClouds = Enabled;
            Atmosphere.CloudCoverage = Coverage;
            Atmosphere.CloudDensity = Density;
            Atmosphere.CloudWindDirection = Vector2.Normalize(WindDirection);
            Atmosphere.CloudWindSpeed = WindSpeed;
        }

        /// <summary>由风力场驱动云层移动方向与速度。</summary>
        public void SyncFromWind(in WindField wind)
        {
            WindDirection = wind.Direction;
            WindSpeed = MathHelper.Lerp(8f, 40f, Math.Clamp(wind.Speed / 25f, 0f, 1f));
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 天气状态机 (WeatherStateMachine)
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 天气状态机。维护当前天气与目标天气，并在两者间按过渡时长与模式平滑
    /// 混合，输出一个实时 <see cref="WeatherProfile"/>。过渡完成即切换状态。
    /// </summary>
    public class WeatherStateMachine
    {
        /// <summary>当前天气类型（过渡完成后更新）。</summary>
        public WeatherType CurrentType { get; private set; } = WeatherType.Clear;
        /// <summary>目标天气类型。</summary>
        public WeatherType TargetType { get; private set; } = WeatherType.Clear;
        /// <summary>过渡模式。</summary>
        public WeatherTransitionMode TransitionMode = WeatherTransitionMode.EaseInOut;
        /// <summary>过渡时长（秒）。</summary>
        public float TransitionDuration = 10f;
        /// <summary>过渡进度 [0,1]。</summary>
        public float TransitionProgress = 1f;

        private readonly Dictionary<WeatherType, WeatherProfile> _profiles = new();
        /// <summary>当前混合后的实时 Profile。</summary>
        public WeatherProfile CurrentProfile;

        public WeatherStateMachine()
        {
            foreach (WeatherType t in Enum.GetValues(typeof(WeatherType)))
                _profiles[t] = new WeatherProfile(t);
            CurrentProfile = new WeatherProfile(WeatherType.Clear);
        }

        /// <summary>请求切换到目标天气，启动过渡。</summary>
        public void SetWeather(WeatherType target, float transitionDuration = -1f,
            WeatherTransitionMode mode = WeatherTransitionMode.EaseInOut)
        {
            if (target == TargetType && TransitionProgress >= 1f) return;
            // 若正在过渡中，把当前混合态作为新起点
            if (TransitionProgress < 1f)
                _profiles[CurrentType] = WeatherProfile.Lerp(
                    _profiles[CurrentType], _profiles[TargetType], TransitionProgress);

            CurrentType = TargetType;
            TargetType = target;
            TransitionMode = mode;
            TransitionDuration = transitionDuration > 0f ? transitionDuration : TransitionDuration;
            TransitionProgress = 0f;
        }

        /// <summary>立即切换（无过渡）。</summary>
        public void SetWeatherInstant(WeatherType target)
        {
            CurrentType = target;
            TargetType = target;
            TransitionProgress = 1f;
            CurrentProfile = new WeatherProfile(target);
        }

        /// <summary>每帧推进过渡，更新 CurrentProfile。</summary>
        public void Update(float dt)
        {
            if (TransitionProgress >= 1f)
            {
                CurrentProfile = _profiles[TargetType];
                CurrentType = TargetType;
                return;
            }

            TransitionProgress = MathF.Min(1f, TransitionProgress + dt / MathF.Max(0.01f, TransitionDuration));
            float t = TransitionMode switch
            {
                WeatherTransitionMode.Instant => 1f,
                WeatherTransitionMode.Linear => TransitionProgress,
                // EaseInOut: smoothstep
                _ => TransitionProgress * TransitionProgress * (3f - 2f * TransitionProgress)
            };
            CurrentProfile = WeatherProfile.Lerp(_profiles[CurrentType], _profiles[TargetType], t);

            if (TransitionProgress >= 1f)
            {
                CurrentType = TargetType;
                CurrentProfile = _profiles[TargetType];
            }
        }

        /// <summary>获取某天气类型的原始 Profile（可外部修改后生效）。</summary>
        public WeatherProfile GetProfile(WeatherType t) => _profiles[t];
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 辅助数学 (MathHelper)
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>少量数学辅助函数，避免依赖外部库。</summary>
    public static class WeatherMathHelper
    {
        /// <summary>线性插值。</summary>
        public static float Lerp(float a, float b, float t) => a + (b - a) * t;
        /// <summary>钳制。</summary>
        public static float Clamp(float v, float min, float max) =>
            v < min ? min : (v > max ? max : v);
        /// <summary>平滑阶跃。</summary>
        public static float Smoothstep(float edge0, float edge1, float x)
        {
            float t = Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
            return t * t * (3f - 2f * t);
        }
    }

    /// <summary>MathHelper 别名，供 Profile 内部使用。</summary>

    // ──────────────────────────────────────────────────────────────────────────
    // WeatherSystem：顶层编排器
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 天气系统顶层编排器。聚合昼夜循环、天气状态机、雾、风、降水、闪电、
    /// 体积云接口、环境效果等子系统，统一每帧推进，并把结果写入共享的
    /// <see cref="AtmosphereSettings"/>，从而驱动 <see cref="AtmosphereRenderer"/>。
    /// </summary>
    public class WeatherSystem
    {
        /// <summary>共享大气设置（与 AtmosphereRenderer 共用同一实例）。</summary>
        public AtmosphereSettings Atmosphere;
        /// <summary>大气渲染器引用（可选，用于访问子渲染器）。</summary>
        public AtmosphereRenderer? AtmosphereRenderer;
        /// <summary>GPU 粒子系统（降水）。</summary>
        public GPUParticleSystem? Particles;
        /// <summary>音频系统（雷声）。</summary>
        public AudioSystem? Audio;
        /// <summary>RHI 设备（可选，用于体积雾/闪电纹理）。</summary>
        public RHIDevice? Device;

        // ── 子系统 ──
        /// <summary>昼夜循环。</summary>
        public readonly DayNightCycle DayNight = new();
        /// <summary>天气状态机。</summary>
        public readonly WeatherStateMachine StateMachine = new();
        /// <summary>雾效系统。</summary>
        public readonly FogSystem Fog = new();
        /// <summary>风力场。</summary>
        public readonly WindField Wind = new();
        /// <summary>降水系统。</summary>
        public readonly PrecipitationSystem Precipitation = new();
        /// <summary>闪电系统。</summary>
        public readonly LightningSystem Lightning = new();
        /// <summary>体积云接口。</summary>
        public readonly VolumetricCloudInterface Clouds = new();
        /// <summary>环境效果。</summary>
        public readonly WeatherEnvironmentEffects Environment = new();

        /// <summary>是否暂停昼夜推进（仍推进天气）。</summary>
        public bool PauseDayNight;
        /// <summary>降水覆盖半径。</summary>
        public float PrecipitationRadius = 500f;

        /// <summary>
        /// 构造天气系统。传入与 AtmosphereRenderer 共享的 <paramref name="atmosphere"/> 设置实例。
        /// </summary>
        public WeatherSystem(AtmosphereSettings atmosphere)
        {
            Atmosphere = atmosphere ?? throw new ArgumentNullException(nameof(atmosphere));
            // 把共享设置下发到各子系统
            DayNight.Atmosphere = Atmosphere;
            Fog.Atmosphere = Atmosphere;
            Clouds.Atmosphere = Atmosphere;
            Precipitation.Particles = Particles;
            Lightning.Audio = Audio;
        }

        /// <summary>绑定引擎子系统引用（在引擎启动后调用）。</summary>
        public void BindSystems(AtmosphereRenderer? renderer, GPUParticleSystem? particles,
            AudioSystem? audio, RHIDevice? device)
        {
            AtmosphereRenderer = renderer;
            Particles = particles;
            Audio = audio;
            Device = device;
            Precipitation.Particles = particles;
            Lightning.Audio = audio;
        }

        /// <summary>请求切换天气（带过渡）。</summary>
        public void SetWeather(WeatherType target, float transitionDuration = 10f,
            WeatherTransitionMode mode = WeatherTransitionMode.EaseInOut)
            => StateMachine.SetWeather(target, transitionDuration, mode);

        /// <summary>立即切换天气。</summary>
        public void SetWeatherInstant(WeatherType target) => StateMachine.SetWeatherInstant(target);

        /// <summary>设置当日时间（小时）。</summary>
        public void SetTimeOfDay(float hour) => DayNight.SetTime(hour);

        // ── 缓存上一帧降水类型，用于切换时重置粒子 ──
        private PrecipitationType _lastPrecip = PrecipitationType.None;

        /// <summary>
        /// 每帧更新。统一推进所有子系统并把结果写入大气设置。
        /// </summary>
        /// <param name="dt">帧间隔（秒）。</param>
        /// <param name="cameraPos">当前相机世界位置（降水跟随相机）。</param>
        public void Update(float dt, Vector3 cameraPos)
        {
            // 1. 昼夜循环（驱动太阳方向/照度/天空色）
            if (!PauseDayNight) DayNight.Update(dt);

            // 2. 天气状态机过渡（产出实时 Profile）
            StateMachine.Update(dt);
            var profile = StateMachine.CurrentProfile;

            // 3. 风力场
            Wind.Direction = profile.WindDirection;
            Wind.Speed = profile.WindSpeed;
            Wind.Turbulence = profile.WindTurbulence;
            Wind.Update(dt);

            // 4. 云
            Clouds.Coverage = profile.CloudCoverage;
            Clouds.Density = profile.CloudDensity;
            Clouds.SyncFromWind(in Wind);
            Clouds.Apply();

            // 5. 雾
            Fog.Type = profile.Fog;
            Fog.Density = profile.FogDensity;
            Fog.StartHeight = profile.FogStartHeight;
            Fog.HeightFalloff = profile.FogHeightFalloff;
            Fog.Color = profile.FogColor;
            Fog.Apply();

            // 6. 降水（切换类型时先停止）
            if (profile.Precipitation != _lastPrecip)
            {
                Precipitation.SetType(profile.Precipitation);
                _lastPrecip = profile.Precipitation;
            }
            Precipitation.Intensity = profile.PrecipitationIntensity;
            Precipitation.Update(dt, cameraPos, in Wind);

            // 7. 闪电
            Lightning.Enabled = profile.EnableLightning;
            Lightning.FrequencyPerMinute = profile.LightningFrequency;
            Lightning.Update(dt, cameraPos, PrecipitationRadius);

            // 8. 环境效果累积
            Environment.Update(dt, StateMachine.CurrentType, profile.PrecipitationIntensity);

            // 9. 把闪电闪光与天色染色叠加进大气（提升环境光）
            ApplyLightningAndTint(profile);
        }

        /// <summary>把闪电瞬时闪光与天气天色染色应用到大气设置。</summary>
        private void ApplyLightningAndTint(WeatherProfile profile)
        {
            // 闪电闪光：临时提升太阳照度（叠加在昼夜之上）
            if (Lightning.FlashIntensity > 0.001f)
            {
                float flashLux = Lightning.FlashIntensity * 80000f;
                Atmosphere.SunIlluminance += flashLux;
            }

            // 天气天色染色：调整瑞利散射系数（多云/暴风时整体偏灰暗）
            Vector3 tint = profile.SkyTint * profile.AmbientScale;
            // 仅在白天染色，夜间保持原样
            if (DayNight.Sun.Elevation > 0f)
            {
                Vector3 baseRayleigh = new(5.802e-6f, 13.558e-6f, 33.100e-6f);
                Atmosphere.RayleighScattering = baseRayleigh * tint;
            }
        }

        /// <summary>获取当前天空颜色（含天气染色）。</summary>
        public (Vector3 zenith, Vector3 horizon) GetSkyColors()
        {
            var profile = StateMachine.CurrentProfile;
            Vector3 zen = DayNight.CurrentZenithColor * profile.SkyTint;
            Vector3 hor = DayNight.CurrentHorizonColor * profile.SkyTint;
            // 闪电闪光叠加白光
            if (Lightning.FlashIntensity > 0.001f)
            {
                Vector3 flash = new Vector3(1f) * Lightning.FlashIntensity;
                zen += flash; hor += flash;
            }
            return (zen, hor);
        }

        /// <summary>获取当前星光强度（夜间 + 无厚重云层时显现）。</summary>
        public float GetStarIntensity()
        {
            float cloud = StateMachine.CurrentProfile.CloudCoverage;
            // 云覆盖越高星光越弱
            return DayNight.StarIntensity * (1f - cloud * 0.8f);
        }

        /// <summary>获取当前月光方向（用于海面高光/夜间阴影）。</summary>
        public Vector3 GetMoonDirection() => DayNight.Moon.Direction;

        /// <summary>重置天气系统（场景切换）。</summary>
        public void Reset()
        {
            StateMachine.SetWeatherInstant(WeatherType.Clear);
            Environment.Reset();
            Lightning.Reset();
            Precipitation.SetType(PrecipitationType.None);
            _lastPrecip = PrecipitationType.None;
        }
    }
}
