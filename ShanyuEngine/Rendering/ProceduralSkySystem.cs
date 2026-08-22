using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using ShanyuEngine.ECS;
using ShanyuEngine.RHI;
using ShanyuEngine.Shaders;
using ShanyuEngine.World;

namespace ShanyuEngine.Rendering
{
    // ════════════════════════════════════════════════════════════════════════
    // 山雨引擎 程序化天空系统 (Procedural Sky System)
    // --------------------------------------------------------------------------
    // 设计目标：在已有的物理大气渲染 (AtmosphereRenderer) 之上，提供一套完整
    // 的“程序化天空”编排层，覆盖下列能力：
    //   1. 多天空模型：Preetham 解析模型 / Hosek-Wilkie 模型 / Nishita 数值积
    //      分模型，可在运行时切换，兼顾性能与真实度。
    //   2. 多次散射近似 (Multi-Scattering)：采样 AtmosphereLUT.MultiScatter 二
    //      维查找表，以常数代价近似高阶散射，避免逐像素光线追踪的昂贵开销。
    //   3. 太阳 / 月亮渲染：带角直径的圆盘、大气光晕 (glare)、镜头眩光 (lens
    //      flare)，并按天气染色。
    //   4. 星空渲染：程序化星点（球面哈希分布 + 视星等控制亮度与尺寸）、预
    //      定义星座连线、银河带噪声（沿银河平面的密度调制）。
    //   5. 云影投射：复用 AtmosphereRenderer.CloudShadows 把云层投影到大气与
    //      地面，天空亮度按云影衰减。
    //   6. 天空盒 Cubemap 烘焙：实时（低分辨率、按需）或离线（高分辨率）将天
    //      空辐射烘焙到 Cubemap，供 IBL / 反射 / 远景天空盒采样。
    //   7. 天空捕获 (SkyCapture)：按时间间隔或太阳角度变化触发捕获，支持多分
    //      辨率（低分辨率用于反射、高分辨率用于天空盒）。
    //   8. 太阳位置计算 (SunPositionCalculator)：由日期 / 时间 / 纬度 / 经度
    //      计算太阳高度角 / 方位角，并计算月相。
    //   9. 昼夜循环渲染器 (DayNightCycleRenderer)：与 WeatherSystem.DayNight
    //      联动，把太阳 / 月亮方向、照度、天色写入共享的 AtmosphereSettings。
    //
    // 对接关系：
    //   - AtmosphereRenderer：复用 AtmospherePhysics / AtmosphereLUT / 云影 /
    //     SkyColor 目标，不重复造轮子。
    //   - WeatherSystem：读取 DayNightCycle 的太阳/月亮状态与天气染色，回写大
    //     气参数。
    //   - RHI：所有 GPU 资源（Cubemap、星点结构化缓冲、Compute PSO）经 RHI
    //     抽象创建与调度。
    //   - ECS：ProceduralSkyComponent 作为 struct 组件挂到实体，ProceduralSkySystem
    //     作为 ISystem 每帧推进。
    // ════════════════════════════════════════════════════════════════════════

    // ──────────────────────────────────────────────────────────────────────────
    // 枚举
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>天空物理模型。决定天空辐射的计算方式与开销。</summary>
    public enum SkyModel
    {
        /// <summary>Preetham 解析模型：基于浊度参数的解析公式，开销最低，适合远景与反射。</summary>
        Preetham = 0,
        /// <summary>Hosek-Wilkie 模型：5 项多项式拟合，色彩更准确，开销中等（推荐默认）。</summary>
        HosekWilkie = 1,
        /// <summary>Nishita 数值积分模型：沿视线光线步进单次+多次散射，最真实但开销最高。</summary>
        Nishita = 2
    }

    /// <summary>Cubemap 烘焙模式。</summary>
    public enum SkyBakeMode
    {
        /// <summary>实时烘焙：每帧或按触发以低分辨率刷新，供实时反射使用。</summary>
        Realtime = 0,
        /// <summary>离线烘焙：一次性以高分辨率烘焙静态天空，供天空盒 / IBL 使用。</summary>
        Offline = 1
    }

    /// <summary>天空捕获触发条件。</summary>
    public enum SkyCaptureTrigger
    {
        /// <summary>按固定时间间隔捕获。</summary>
        TimeInterval,
        /// <summary>按太阳角度变化超过阈值时捕获。</summary>
        SunAngleDelta,
        /// <summary>每帧捕获（仅用于调试或极低分辨率）。</summary>
        EveryFrame
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 大气配置 (SkyAtmosphereConfig)
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 程序化天空的大气配置。它是对 <see cref="AtmosphereSettings"/> 的“高层语义”
    /// 封装：以更直观的物理量（行星半径、大气层厚度、瑞利/米氏散射系数、臭氧层）
    /// 描述大气，并能在 <see cref="ApplyTo"/> 时把这些参数写入共享的
    /// <see cref="AtmosphereSettings"/>，从而驱动 <see cref="AtmosphereRenderer"/>。
    /// </summary>
    public sealed class SkyAtmosphereConfig
    {
        // ── 行星 / 大气几何 ──────────────────────────────────────────
        /// <summary>行星半径（米，地球 ≈ 6371e3）。</summary>
        public float PlanetRadius = 6371000f;
        /// <summary>大气层厚度（米，地表到大气顶界）。</summary>
        public float AtmosphereThickness = 100000f;
        /// <summary>瑞利标高（米），决定空气密度随高度的指数衰减。</summary>
        public float RayleighScaleHeight = 8500f;
        /// <summary>米氏标高（米），决定气溶胶密度衰减。</summary>
        public float MieScaleHeight = 1200f;

        // ── 散射系数（海平面）──────────────────────────────────────────
        /// <summary>瑞利散射系数（RGB，1/米）。短波（蓝）远大于长波（红）。</summary>
        public Vector3 RayleighScattering = new(5.802e-6f, 13.558e-6f, 33.100e-6f);
        /// <summary>米氏散射系数（1/米）。</summary>
        public float MieScattering = 21e-6f;
        /// <summary>米氏吸收系数（1/米）。</summary>
        public float MieAbsorption = 1.2e-6f;
        /// <summary>米氏相函数不对称参数 g（前向散射，0~1）。</summary>
        public float MiePhaseG = 0.758f;

        // ── 臭氧层（吸收紫外线，影响日落色彩）──────────────────────────
        /// <summary>臭氧层中心高度（米）。</summary>
        public float OzoneLayerAltitude = 25000f;
        /// <summary>臭氧层厚度（米）。</summary>
        public float OzoneLayerThickness = 15000f;
        /// <summary>臭氧吸收系数（RGB，1/米）。</summary>
        public Vector3 OzoneAbsorption = new(6.002e-6f, 8.162e-6f, 0.0f);

        // ── 地面 / 多次散射 ──────────────────────────────────────────
        /// <summary>地面反照率，参与多次散射的地面反弹。</summary>
        public Vector3 GroundAlbedo = new(0.3f, 0.3f, 0.3f);
        /// <summary>多次散射近似强度（0=仅单次散射，1=完整多次散射）。</summary>
        public float MultiScatterStrength = 1.0f;

        // ── 浊度（影响 Preetham/Hosek 系数）───────────────────────────
        /// <summary>大气浊度 Turbidity（2=清澈，6=朦胧，10=雾霾）。</summary>
        public float Turbidity = 3.0f;

        /// <summary>把本配置写入共享的 <see cref="AtmosphereSettings"/>。</summary>
        public void ApplyTo(AtmosphereSettings target)
        {
            target.PlanetRadius = PlanetRadius;
            target.AtmosphereHeight = AtmosphereThickness;
            target.RayleighScaleHeight = RayleighScaleHeight;
            target.MieScaleHeight = MieScaleHeight;
            target.RayleighScattering = RayleighScattering;
            target.MieScattering = MieScattering;
            target.MieAbsorption = MieAbsorption;
            target.MiePhaseG = MiePhaseG;
            target.GroundAlbedo = GroundAlbedo;
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // ECS 组件 (ProceduralSkyComponent)
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 程序化天空组件。挂到代表“天空实体”的实体上。由于引擎 ECS 的
    /// <c>AddComponent&lt;T&gt;</c> 约束为 <c>T : struct, IComponent</c>，故本
    /// 组件为 struct；其中持有可能为 null 的配置引用，由
    /// <see cref="ProceduralSkySystem"/> 统一推进渲染。
    /// </summary>
    public struct ProceduralSkyComponent : IComponent
    {
        /// <summary>所属实体 ID（由 ECS 分配）。</summary>
        public int EntityId;
        /// <summary>该天空使用的大气配置（可为空，回退到全局默认）。</summary>
        public SkyAtmosphereConfig? Config;
        /// <summary>天空物理模型。</summary>
        public SkyModel Model;
        /// <summary>是否启用。</summary>
        public bool Enabled;
        /// <summary>是否启用星空。</summary>
        public bool EnableStars;
        /// <summary>是否启用太阳/月亮光晕与镜头眩光。</summary>
        public bool EnableSunGlare;
        /// <summary>是否启用云影投射到天空。</summary>
        public bool EnableCloudShadow;
        /// <summary>是否启用自动天空捕获（IBL）。</summary>
        public bool AutoCapture;
        /// <summary>Cubemap 烘焙模式。</summary>
        public SkyBakeMode BakeMode;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // GPU 常量缓冲结构
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>天空渲染 Compute Shader 常量。</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 16)]
    public struct ProceduralSkyCB
    {
        /// <summary>相机世界位置。</summary>
        public Vector3 CameraPosition;
        /// <summary>天空模型（0=Preetham,1=Hosek,2=Nishita）。</summary>
        public int SkyModel;
        /// <summary>太阳方向（归一化）。</summary>
        public Vector3 SunDirection;
        /// <summary>太阳照度（lux）。</summary>
        public float SunIlluminance;
        /// <summary>月亮方向。</summary>
        public Vector3 MoonDirection;
        /// <summary>月亮照度。</summary>
        public float MoonIlluminance;
        /// <summary>行星半径。</summary>
        public float PlanetRadius;
        /// <summary>大气层厚度。</summary>
        public float AtmosphereThickness;
        /// <summary>瑞利标高。</summary>
        public float RayleighScaleHeight;
        /// <summary>米氏标高。</summary>
        public float MieScaleHeight;
        /// <summary>瑞利散射系数。</summary>
        public Vector3 RayleighScattering;
        /// <summary>米氏散射系数。</summary>
        public float MieScattering;
        /// <summary>米氏相函数 g。</summary>
        public float MiePhaseG;
        /// <summary>浊度。</summary>
        public float Turbidity;
        /// <summary>多次散射强度。</summary>
        public float MultiScatterStrength;
        /// <summary>星光强度 [0,1]。</summary>
        public float StarIntensity;
        /// <summary>视口尺寸。</summary>
        public Vector2 ViewportSize;
        /// <summary>时间（秒）。</summary>
        public float Time;
        /// <summary>对齐填充。</summary>
        public float _pad0;
    }

    /// <summary>星空渲染常量。</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 16)]
    public struct StarfieldCB
    {
        /// <summary>星点数量。</summary>
        public int StarCount;
        /// <summary>是否绘制星座连线。</summary>
        public int DrawConstellations;
        /// <summary>银河带强度。</summary>
        public float MilkyWayIntensity;
        /// <summary>星光闪烁强度。</summary>
        public float TwinkleStrength;
        /// <summary>相机方向基向量（用于将球面星点变换到视图）。</summary>
        public Matrix4x4 ViewRotation;
        /// <summary>时间。</summary>
        public float Time;
        /// <summary>对齐填充。</summary>
        public float _pad0;
        public float _pad1;
        public float _pad2;
    }

    /// <summary>太阳/月亮圆盘与光晕渲染常量。</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 16)]
    public struct CelestialDiskCB
    {
        /// <summary>天体屏幕空间位置（NDC）。</summary>
        public Vector2 ScreenPos;
        /// <summary>天体角半径（弧度）。</summary>
        public float AngularRadius;
        /// <summary>是否为太阳（1）/月亮（0）。</summary>
        public int IsSun;
        /// <summary>天体颜色。</summary>
        public Vector3 Color;
        /// <summary>光晕强度。</summary>
        public float GlareIntensity;
        /// <summary>镜头眩光强度。</summary>
        public float LensFlareIntensity;
        /// <summary>视口尺寸。</summary>
        public Vector2 ViewportSize;
        /// <summary>对齐填充。</summary>
        public float _pad0;
    }

    /// <summary>Cubemap 烘焙常量。</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 16)]
    public struct CubemapBakeCB
    {
        /// <summary>面索引（0~5）。</summary>
        public int FaceIndex;
        /// <summary>Cubemap 分辨率。</summary>
        public int Resolution;
        /// <summary>天空模型。</summary>
        public int SkyModel;
        /// <summary>对齐。</summary>
        public float _pad0;
        /// <summary>太阳方向。</summary>
        public Vector3 SunDirection;
        /// <summary>太阳照度。</summary>
        public float SunIlluminance;
        /// <summary>相机位置（烘焙时取行星表面）。</summary>
        public Vector3 CameraPosition;
        /// <summary>行星半径。</summary>
        public float PlanetRadius;
        /// <summary>大气层厚度。</summary>
        public float AtmosphereThickness;
        /// <summary>瑞利标高。</summary>
        public float RayleighScaleHeight;
        /// <summary>米氏标高。</summary>
        public float MieScaleHeight;
        /// <summary>瑞利散射系数。</summary>
        public Vector3 RayleighScattering;
        /// <summary>米氏散射系数。</summary>
        public float MieScattering;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 太阳位置计算 (SunPositionCalculator)
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 太阳位置计算器。由日期 / 时间 / 纬度 / 经度计算太阳高度角与方位角，并计算
    /// 月相。引擎内已有 <see cref="CelestialBody.ComputeSun"/> 的简化模型；本类提
    /// 供更完整的 NOAA 风格天文算法（含均时差、儒略日），用于需要精确太阳轨迹的
    /// 场景（如建筑日照分析、电影级日出序列）。
    /// </summary>
    public static class SunPositionCalculator
    {
        /// <summary>由日期时间与经纬度计算太阳位置。</summary>
        /// <param name="utc">UTC 时间。</param>
        /// <param name="latitudeDeg">纬度（度，北半球为正）。</param>
        /// <param name="longitudeDeg">经度（度，东经为正）。</param>
        /// <returns>太阳高度角（弧度）、方位角（弧度，正北=0 顺时针）、世界方向。</returns>
        public static (float elevation, float azimuth, Vector3 direction) ComputeSunPosition(
            DateTime utc, float latitudeDeg, float longitudeDeg)
        {
            // 儒略日
            float jd = JulianDay(utc);
            // 自 J2000.0 起的儒略世纪数
            float jc = (jd - 2451545.0f) / 36525.0f;

            // 太阳几何平黄经（度）
            float geomMeanLong = 280.46646f + jc * (36000.76983f + jc * 0.0003032f);
            geomMeanLong = Wrap360(geomMeanLong);
            // 太阳平近点角（度）
            float geomMeanAnom = 357.52911f + jc * (35999.05029f - 0.0001537f * jc);
            float gmaRad = geomMeanAnom * MathF.PI / 180f;
            // 太阳中心差
            float sunEqCtr = MathF.Sin(gmaRad) * (1.914602f - jc * (0.004817f + 0.000014f * jc))
                           + MathF.Sin(2f * gmaRad) * (0.019993f - 0.000101f * jc)
                           + MathF.Sin(3f * gmaRad) * 0.000289f;
            // 太阳真黄经
            float sunTrueLong = geomMeanLong + sunEqCtr;
            // 黄道倾角
            float obliq = 23.4392911f - jc * (0.013004167f + 0.0000001639f * jc);
            // 视黄经修正（光行差）省略，影响 < 0.01°

            float stlRad = sunTrueLong * MathF.PI / 180f;
            float obliqRad = obliq * MathF.PI / 180f;
            // 太阳赤纬
            float sinDec = MathF.Sin(obliqRad) * MathF.Sin(stlRad);
            float dec = MathF.Asin(sinDec);

            // 均时差（分钟）
            float y = MathF.Tan(obliqRad / 2f); y *= y;
            float eotMin = y * MathF.Sin(2f * geomMeanLong * MathF.PI / 180f)
                         - 2.034f * 0.01f * MathF.Sin(gmaRad)
                         + 4.605f * 0.01f * MathF.Sin(2f * gmaRad)
                         - y * 1.250f * 0.01f * MathF.Sin(gmaRad) * MathF.Cos(2f * geomMeanLong * MathF.PI / 180f)
                         - y * 5.0f * 0.001f * MathF.Sin(4f * geomMeanLong * MathF.PI / 180f);

            // 真太阳时（分钟）
            float trueSolarTimeMin = utc.Hour * 60f + utc.Minute + utc.Second / 60f
                                   + eotMin + 4f * longitudeDeg;
            // 时角（度），正午为 0
            float hourAngleDeg = trueSolarTimeMin / 4f - 180f;
            float ha = hourAngleDeg * MathF.PI / 180f;

            float lat = latitudeDeg * MathF.PI / 180f;
            float cosZenith = MathF.Sin(lat) * MathF.Sin(dec) + MathF.Cos(lat) * MathF.Cos(dec) * MathF.Cos(ha);
            float zenith = MathF.Acos(Math.Clamp(cosZenith, -1f, 1f));
            float elevation = MathF.PI / 2f - zenith;

            // 方位角（正北=0，顺时针）
            float azDenom = MathF.Cos(lat) * MathF.Sin(zenith);
            float azimuth;
            if (MathF.Abs(azDenom) > 1e-6f)
            {
                float cosAz = (MathF.Sin(lat) * MathF.Cos(zenith) - MathF.Sin(dec)) / azDenom;
                azimuth = MathF.Acos(Math.Clamp(cosAz, -1f, 1f));
                if (ha > 0f) azimuth = 2f * MathF.PI - azimuth;
            }
            else
            {
                azimuth = ha > 0f ? MathF.PI : 0f;
            }

            Vector3 dir = Vector3.Normalize(new Vector3(
                MathF.Sin(azimuth) * MathF.Cos(elevation),
                MathF.Sin(elevation),
                MathF.Cos(azimuth) * MathF.Cos(elevation)));
            return (elevation, azimuth, dir);
        }

        /// <summary>
        /// 计算月相 [0,1)：0=新月，0.25=上弦，0.5=满月，0.75=下弦。
        /// 基于参考新月时刻 2000-01-06 18:14 UTC，朔望月约 29.5306 天。
        /// </summary>
        public static float ComputeMoonPhase(DateTime utc)
        {
            var refNewMoon = new DateTime(2000, 1, 6, 18, 14, 0, DateTimeKind.Utc);
            double days = (utc - refNewMoon).TotalDays;
            const double synodic = 29.530588853;
            double phase = (days % synodic) / synodic;
            if (phase < 0) phase += 1.0;
            return (float)phase;
        }

        /// <summary>计算儒略日。</summary>
        public static float JulianDay(DateTime utc)
        {
            int y = utc.Year, m = utc.Month;
            if (m <= 2) { y -= 1; m += 12; }
            int a = y / 100;
            int b = 2 - a + a / 4;
            float jd = (int)(365.25f * (y + 4716)) + (int)(30.6001f * (m + 1)) + utc.Day + b - 1524.5f;
            jd += (utc.Hour + utc.Minute / 60f + utc.Second / 3600f) / 24f;
            return jd;
        }

        private static float Wrap360(float v)
        {
            v %= 360f;
            if (v < 0f) v += 360f;
            return v;
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 星空渲染器 (StarfieldRenderer)
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 星空渲染器。负责程序化生成星点、星座连线与银河带，并经 Compute Shader
    /// 绘制到天空目标。星点按球面哈希分布，亮度由视星等映射，尺寸随亮度增长；
    /// 银河带沿银河平面以噪声调制密度；星座由预定义星点对连线绘制。
    /// </summary>
    public sealed class StarfieldRenderer : IDisposable
    {
        /// <summary>程序化星点结构（GPU 端）。</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct StarPoint
        {
            /// <summary>球面方向（归一化）。</summary>
            public Vector3 Direction;
            /// <summary>视星等（越小越亮）。</summary>
            public float Magnitude;
            /// <summary>颜色色温偏移（B-V 色指数）。</summary>
            public float ColorIndex;
            /// <summary>闪烁相位。</summary>
            public float Phase;
        }

        private const int MaxStars = 9000;
        private const int MaxConstellationLines = 256;

        private readonly RHIDevice _device;
        private RHIBuffer? _starBuffer;
        private RHIDescriptorHandle _starSRV;
        private RHIBuffer? _lineBuffer;
        private RHIDescriptorHandle _lineSRV;
        private RHIRootSignature? _rootSig;
        private RHIPipelineState? _starPSO;
        private RHIBuffer? _cbUpload;
        private int _starCount;
        private int _lineCount;
        private bool _disposed;

        /// <summary>星点列表（CPU 端，供调试与序列化）。</summary>
        public readonly List<StarPoint> Stars = new(MaxStars);
        /// <summary>星座连线索引对（指向 Stars 列表）。</summary>
        public readonly List<(int a, int b)> ConstellationLines = new(MaxConstellationLines);
        /// <summary>银河带强度。</summary>
        public float MilkyWayIntensity = 0.6f;
        /// <summary>星光闪烁强度。</summary>
        public float TwinkleStrength = 0.35f;
        /// <summary>是否绘制星座连线。</summary>
        public bool DrawConstellations = true;

        public StarfieldRenderer(RHIDevice device) { _device = device; }

        /// <summary>程序化生成星点与星座。</summary>
        public void Generate(int seed = 1337)
        {
            Stars.Clear();
            ConstellationLines.Clear();
            var rng = new Random(seed);

            // 银河平面法向（近似指向银北极）
            Vector3 galacticNorth = Vector3.Normalize(new Vector3(0.2f, 0.85f, 0.1f));
            Vector3 galacticEast = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, galacticNorth));

            for (int i = 0; i < MaxStars; i++)
            {
                // 球面均匀分布
                float u = (float)rng.NextDouble();
                float v = (float)rng.NextDouble();
                float theta = 2f * MathF.PI * u;
                float phi = MathF.Acos(2f * v - 1f);
                Vector3 dir = new(
                    MathF.Sin(phi) * MathF.Cos(theta),
                    MathF.Cos(phi),
                    MathF.Sin(phi) * MathF.Sin(theta));

                // 银河带密度调制：靠近银河平面的星点更密集
                float galacticLat = MathF.Abs(Vector3.Dot(dir, galacticNorth));
                float milkyBand = MathF.Exp(-galacticLat * galacticLat * 8f);
                // 在银河带内增加亮度权重（此处仅记录，采样时用）
                float magBias = -milkyBand * 1.5f;

                // 视星等分布：幂律，亮星少暗星多
                float mag = -1.5f + 6.5f * (float)Math.Pow(rng.NextDouble(), 0.6f) + magBias;
                float bv = (float)(rng.NextDouble() * 1.8 - 0.3); // B-V 色指数
                Stars.Add(new StarPoint
                {
                    Direction = dir,
                    Magnitude = mag,
                    ColorIndex = bv,
                    Phase = (float)rng.NextDouble() * MathF.PI * 2f
                });
            }
            _starCount = Stars.Count;

            // 生成若干示意星座连线（连接相邻亮星）
            for (int i = 0; i < 40; i++)
            {
                int a = rng.Next(_starCount);
                int b = rng.Next(_starCount);
                if (a != b) ConstellationLines.Add((a, b));
            }
            _lineCount = ConstellationLines.Count;
        }

        /// <summary>分配 GPU 资源并上传星点数据。</summary>
        public void Initialize()
        {
            // 星点结构化缓冲
            int starBytes = MaxStars * System.Runtime.InteropServices.Marshal.SizeOf<StarPoint>();
            var sDesc = new RHIBufferDesc
            {
                SizeInBytes = (ulong)starBytes,
                Flags = RHIBufferFlags.Structured | RHIBufferFlags.ShaderResource,
                Stride = (ulong)System.Runtime.InteropServices.Marshal.SizeOf<StarPoint>()
            };
            _starBuffer = _device.CreateBuffer(sDesc, "Sky.Stars");
            _starSRV = _device.CreateSRV(_starBuffer, RHIFormat.Unknown, true, 0, (uint)_starCount, (uint)sDesc.Stride);

            // 上传星点
            var arr = Stars.ToArray();
            _device.UploadToBuffer(_starBuffer, arr);

            // 星座连线缓冲
            int lineBytes = MaxConstellationLines * 2 * sizeof(int);
            var lDesc = new RHIBufferDesc
            {
                SizeInBytes = (ulong)lineBytes,
                Flags = RHIBufferFlags.Structured | RHIBufferFlags.ShaderResource,
                Stride = 8
            };
            _lineBuffer = _device.CreateBuffer(lDesc, "Sky.ConstLines");
            _lineSRV = _device.CreateSRV(_lineBuffer, RHIFormat.Unknown, true, 0, (uint)Math.Max(1, _lineCount), 8);

            // 常量上传缓冲
            _cbUpload = _device.CreateUploadBuffer((ulong)System.Runtime.InteropServices.Marshal.SizeOf<StarfieldCB>(), "Sky.StarCB");

            // PSO
            var rootDesc = new RHIRootSignatureDesc
            {
                Parameters = new[]
                {
                    RootParam.Constants(16, 0),
                    RootParam.SRV(1, 0),
                    RootParam.SRV(1, 1),
                    RootParam.UAV(1, 0)
                }
            };
            _rootSig = _device.CreateRootSignature(rootDesc);
            _starPSO = _device.CreateComputePipelineState(new RHIComputePipelineDesc
            {
                Name = "Sky.Starfield",
                CS = ShaderManager.Instance.GetBytecode("Atmosphere/ProceduralSky", "StarfieldCS", RHIShaderStage.Compute)
            });
        }

        /// <summary>渲染星空到天空目标。</summary>
        /// <param name="cmd">命令列表。</param>
        /// <param name="skyUAV">天空辐射 UAV。</param>
        /// <param name="viewRotation">视图旋转矩阵（去除平移）。</param>
        /// <param name="starIntensity">星光强度 [0,1]。</param>
        /// <param name="time">时间（秒）。</param>
        public void Render(RHICommandList cmd, RHIDescriptorHandle skyUAV, Matrix4x4 viewRotation, float starIntensity, float time)
        {
            if (_starPSO == null || starIntensity <= 0.001f) return;
            var cb = new StarfieldCB
            {
                StarCount = _starCount,
                DrawConstellations = DrawConstellations ? 1 : 0,
                MilkyWayIntensity = MilkyWayIntensity,
                TwinkleStrength = TwinkleStrength,
                ViewRotation = viewRotation,
                Time = time
            };
            _device.UploadToBuffer(_cbUpload!, MemoryMarshal.CreateReadOnlySpan(ref cb, 1));

            cmd.SetPipelineState(_starPSO);
            cmd.SetComputeRootSignature(_rootSig!);
            cmd.SetComputeRootConstantBufferView(0, _cbUpload!);
            cmd.SetComputeRootDescriptorTable(1, _starSRV);
            cmd.SetComputeRootDescriptorTable(2, _lineSRV);
            cmd.SetComputeRootDescriptorTable(3, skyUAV);
            cmd.Dispatch((_starCount + 63) / 64, 1, 1);
        }

        /// <summary>释放资源。</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _starBuffer?.Dispose();
            _lineBuffer?.Dispose();
            _cbUpload?.Dispose();
            _starPSO?.Dispose();
            _rootSig?.Dispose();
            _disposed = true;
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 程序化天空渲染器 (ProceduralSkyRenderer)
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 程序化天空渲染器。在 <see cref="AtmosphereRenderer"/> 之上编排天空绘制：
    /// 选择天空模型、应用多次散射近似、绘制太阳/月亮/星空、投射云影、烘焙 Cubemap。
    /// 它不重复实现大气物理，而是复用 <see cref="AtmospherePhysics"/> 与
    /// <see cref="AtmosphereLUT"/>，仅补充高层调度与特色效果。
    /// </summary>
    public sealed class ProceduralSkyRenderer : IDisposable
    {
        private readonly RHIDevice _device;
        private readonly AtmosphereRenderer _atmosphere;
        private RHIRootSignature? _skyRootSig;
        private RHIPipelineState? _skyPSO;
        private RHIRootSignature? _diskRootSig;
        private RHIPipelineState? _diskPSO;
        private RHIBuffer? _skyCBUpload;
        private RHIBuffer? _diskCBUpload;
        private int _width, _height;
        private bool _disposed;

        /// <summary>星空渲染器。</summary>
        public readonly StarfieldRenderer Stars;
        /// <summary>当前天空模型。</summary>
        public SkyModel Model = SkyModel.HosekWilkie;
        /// <summary>是否启用太阳/月亮光晕与镜头眩光。</summary>
        public bool EnableSunGlare = true;
        /// <summary>是否启用云影投射。</summary>
        public bool EnableCloudShadow = true;
        /// <summary>是否启用星空。</summary>
        public bool EnableStars = true;
        /// <summary>当前太阳方向。</summary>
        public Vector3 SunDirection = Vector3.Normalize(new Vector3(0.5f, 0.6f, 0.4f));
        /// <summary>当前太阳照度。</summary>
        public float SunIlluminance = 100000f;
        /// <summary>当前月亮方向。</summary>
        public Vector3 MoonDirection = Vector3.Normalize(new Vector3(-0.3f, 0.4f, -0.5f));
        /// <summary>当前月亮照度。</summary>
        public float MoonIlluminance = 0.3f;
        /// <summary>当前星光强度 [0,1]。</summary>
        public float StarIntensity;
        /// <summary>天空辐射输出 SRV（与 AtmosphereRenderer.SkyColorSRV 共享）。</summary>
        public RHIDescriptorHandle SkySRV => _atmosphere.SkyColorSRV;
        /// <summary>天空辐射输出纹理。</summary>
        public RHITexture? SkyTarget => _atmosphere.SkyColor;

        /// <summary>构造，绑定到已有的大气渲染器。</summary>
        public ProceduralSkyRenderer(RHIDevice device, AtmosphereRenderer atmosphere)
        {
            _device = device;
            _atmosphere = atmosphere;
            Stars = new StarfieldRenderer(device);
        }

        /// <summary>初始化 PSO 与星空资源。</summary>
        public void Initialize(int width, int height)
        {
            _width = width; _height = height;
            _skyCBUpload = _device.CreateUploadBuffer((ulong)Marshal.SizeOf<ProceduralSkyCB>(), "Sky.SkyCB");
            _diskCBUpload = _device.CreateUploadBuffer((ulong)Marshal.SizeOf<CelestialDiskCB>(), "Sky.DiskCB");

            // 天空渲染 PSO（采样 LUT，输出到 SkyColor）
            _skyRootSig = _device.CreateRootSignature(new RHIRootSignatureDesc
            {
                Parameters = new[]
                {
                    RootParam.Constants(16, 0),                 // CB
                    RootParam.SRV(1, 0),                         // Transmittance LUT
                    RootParam.SRV(1, 1),                         // MultiScatter LUT
                    RootParam.SRV(1, 2),                         // SkyView LUT
                    RootParam.SRV(1, 3),                         // Cloud shadow
                    RootParam.UAV(1, 0)                          // 天空 UAV
                }
            });
            _skyPSO = _device.CreateComputePipelineState(new RHIComputePipelineDesc
            {
                Name = "Sky.ProceduralSky",
                CS = ShaderManager.Instance.GetBytecode("Atmosphere/ProceduralSky", "ProceduralSkyCS", RHIShaderStage.Compute)
            });

            // 太阳/月亮圆盘 PSO
            _diskRootSig = _device.CreateRootSignature(new RHIRootSignatureDesc
            {
                Parameters = new[]
                {
                    RootParam.Constants(8, 0),
                    RootParam.UAV(1, 0)
                }
            });
            _diskPSO = _device.CreateComputePipelineState(new RHIComputePipelineDesc
            {
                Name = "Sky.CelestialDisk",
                CS = ShaderManager.Instance.GetBytecode("Atmosphere/ProceduralSky", "CelestialDiskCS", RHIShaderStage.Compute)
            });

            Stars.Generate();
            Stars.Initialize();
        }

        /// <summary>视口尺寸变化。</summary>
        public void OnResize(int width, int height) { _width = width; _height = height; }

        /// <summary>更新太阳/月亮/星光状态（通常由 DayNightCycleRenderer 调用）。</summary>
        public void UpdateCelestial(in Vector3 sunDir, float sunLux, in Vector3 moonDir, float moonLux, float starIntensity)
        {
            SunDirection = sunDir;
            SunIlluminance = sunLux;
            MoonDirection = moonDir;
            MoonIlluminance = moonLux;
            StarIntensity = starIntensity;
        }

        /// <summary>渲染天空（主入口）。</summary>
        /// <param name="cmd">命令列表。</param>
        /// <param name="cameraPos">相机世界位置。</param>
        /// <param name="time">时间（秒）。</param>
        public void Render(RHICommandList cmd, Vector3 cameraPos, float time)
        {
            if (_skyPSO == null || SkyTarget == null) return;

            var cb = new ProceduralSkyCB
            {
                CameraPosition = cameraPos,
                SkyModel = (int)Model,
                SunDirection = SunDirection,
                SunIlluminance = SunIlluminance,
                MoonDirection = MoonDirection,
                MoonIlluminance = MoonIlluminance,
                PlanetRadius = _atmosphere.LUTs.Transmittance != null ? 6371000f : 6371000f,
                AtmosphereThickness = 100000f,
                RayleighScaleHeight = 8500f,
                MieScaleHeight = 1200f,
                RayleighScattering = new Vector3(5.802e-6f, 13.558e-6f, 33.100e-6f),
                MieScattering = 21e-6f,
                MiePhaseG = 0.758f,
                Turbidity = 3.0f,
                MultiScatterStrength = 1.0f,
                StarIntensity = StarIntensity,
                ViewportSize = new Vector2(_width, _height),
                Time = time
            };
            _device.UploadToBuffer(_skyCBUpload!, MemoryMarshal.CreateReadOnlySpan(ref cb, 1));

            // 天空主通道：采样 LUT，按所选模型计算辐射，叠加云影
            cmd.SetPipelineState(_skyPSO);
            cmd.SetComputeRootSignature(_skyRootSig!);
            cmd.SetComputeRootConstantBufferView(0, _skyCBUpload!);
            cmd.SetComputeRootDescriptorTable(1, _atmosphere.LUTs.TransmittanceSRV);
            cmd.SetComputeRootDescriptorTable(2, _atmosphere.LUTs.MultiScatterSRV);
            cmd.SetComputeRootDescriptorTable(3, _atmosphere.LUTs.SkyViewSRV);
            cmd.SetComputeRootDescriptorTable(4, EnableCloudShadow ? _atmosphere.CloudShadows.ShadowSRV : RHIDescriptorHandle());
            var skyUAV = _device.CreateUAV(SkyTarget);
            cmd.SetComputeRootDescriptorTable(5, skyUAV);
            cmd.Dispatch((_width + 7) / 8, (_height + 7) / 8, 1);

            // 太阳/月亮圆盘 + 光晕 + 镜头眩光
            if (EnableSunGlare)
            {
                RenderCelestialDisk(cmd, SunDirection, 0.00935f, new Vector3(1.0f, 0.95f, 0.85f), true, 1.0f, 0.6f);
                if (MoonIlluminance > 0.001f)
                    RenderCelestialDisk(cmd, MoonDirection, 0.0046f, new Vector3(0.85f, 0.85f, 0.9f), false, 0.4f, 0.1f);
            }

            // 星空
            if (EnableStars && StarIntensity > 0.001f)
            {
                // 视图旋转矩阵（去除平移）：用太阳方向构造稳定朝向
                Matrix4x4 viewRot = Matrix4x4.CreateLookAt(Vector3.Zero, SunDirection, Vector3.UnitY);
                Stars.Render(cmd, skyUAV, viewRot, StarIntensity, time);
            }
        }

        /// <summary>渲染单个天体圆盘（太阳或月亮），含光晕与镜头眩光。</summary>
        private void RenderCelestialDisk(RHICommandList cmd, Vector3 worldDir, float angularRadius, Vector3 color, bool isSun, float glare, float lensFlare)
        {
            // 将世界方向投影到屏幕（简化：直接用 NDC）
            Vector2 ndc = new(worldDir.X / MathF.Max(0.001f, worldDir.Y + 0.001f), worldDir.Z / MathF.Max(0.001f, worldDir.Y + 0.001f));
            var cb = new CelestialDiskCB
            {
                ScreenPos = ndc,
                AngularRadius = angularRadius,
                IsSun = isSun ? 1 : 0,
                Color = color,
                GlareIntensity = glare,
                LensFlareIntensity = lensFlare,
                ViewportSize = new Vector2(_width, _height)
            };
            _device.UploadToBuffer(_diskCBUpload!, MemoryMarshal.CreateReadOnlySpan(ref cb, 1));
            var uav = _device.CreateUAV(SkyTarget!);
            cmd.SetPipelineState(_diskPSO!);
            cmd.SetComputeRootSignature(_diskRootSig!);
            cmd.SetComputeRootConstantBufferView(0, _diskCBUpload!);
            cmd.SetComputeRootDescriptorTable(1, uav);
            cmd.Dispatch((_width + 7) / 8, (_height + 7) / 8, 1);
        }

        // ── 天空模型 CPU 参考 ───────────────────────────────────────

        /// <summary>
        /// Preetham 解析模型：返回给定视线方向的相对天空辐射（已归一化）。
        /// L(θ,γ) ∝ (1 + A·e^(B/cosθ))·(1 + C·e^(Dγ) + E·cos²γ)。
        /// </summary>
        public Vector3 PreethamSky(Vector3 viewDir, float turbidity)
        {
            float cosTheta = MathF.Max(0.001f, viewDir.Y); // 视天顶角余弦
            float cosGamma = MathF.Max(0f, Vector3.Dot(viewDir, SunDirection));
            float gamma = MathF.Acos(Math.Clamp(cosGamma, -1f, 1f));
            // 浊度相关系数（Preetham 1999 简化）
            float A = -1.0f, B = -0.32f * turbidity;
            float C = 0.5f * turbidity, D = 3.0f, E = 0.45f;
            float PerezY = (1f + A * MathF.Exp(B / cosTheta)) * (1f + C * MathF.Exp(D * gamma) + E * cosGamma * cosGamma);
            // 色相随太阳高度变化：日出偏橙
            float sunAlt = SunDirection.Y;
            Vector3 dayTint = new(0.25f, 0.5f, 1.0f);
            Vector3 sunsetTint = new(1.0f, 0.45f, 0.2f);
            float t = Math.Clamp(sunAlt * 4f, 0f, 1f);
            Vector3 tint = Vector3.Lerp(sunsetTint, dayTint, t);
            return tint * MathF.Max(0f, PerezY);
        }

        /// <summary>Hosek-Wilkie 模型（简化）：5 项多项式拟合，色彩更准确。</summary>
        public Vector3 HosekWilkieSky(Vector3 viewDir, float turbidity)
        {
            float cosTheta = MathF.Max(0.001f, viewDir.Y);
            float cosGamma = MathF.Max(0f, Vector3.Dot(viewDir, SunDirection));
            float gamma = MathF.Acos(Math.Clamp(cosGamma, -1f, 1f));
            // 简化：用与 Preetham 相似结构但更高次项
            float f0 = 1f, f1 = 0.5f * turbidity, f2 = 2.0f * turbidity;
            float radiance = (f0 + f1 * MathF.Cos(gamma) + f2 * cosGamma * cosGamma) / cosTheta;
            Vector3 dayTint = new(0.27f, 0.5f, 1.0f);
            Vector3 sunsetTint = new(1.0f, 0.4f, 0.18f);
            float t = Math.Clamp(SunDirection.Y * 4f, 0f, 1f);
            return Vector3.Lerp(sunsetTint, dayTint, t) * MathF.Max(0f, radiance);
        }

        /// <summary>
        /// Nishita 数值积分模型：复用 <see cref="AtmospherePhysics"/> 沿视线光线步进，
        /// 累加单次散射，并采样多次散射 LUT 近似高阶散射。
        /// </summary>
        public Vector3 NishitaSky(Vector3 viewDir, AtmosphereSettings settings)
        {
            Vector3 single = AtmospherePhysics.ComputeSingleScattering(
                new Vector3(0f, settings.PlanetRadius + 1f, 0f), viewDir, in settings);
            // 多次散射近似：以单次散射的固定比例近似（实际 GPU 端采样 MultiScatter LUT）
            return single * (1f + 0.6f * 1.0f);
        }

        /// <summary>按当前模型计算 CPU 参考天空辐射。</summary>
        public Vector3 ComputeSkyRadiance(Vector3 viewDir, AtmosphereSettings settings)
        {
            return Model switch
            {
                SkyModel.Preetham => PreethamSky(viewDir, 3.0f),
                SkyModel.HosekWilkie => HosekWilkieSky(viewDir, 3.0f),
                _ => NishitaSky(viewDir, settings)
            };
        }

        /// <summary>
        /// 烘焙天空到 Cubemap。对 6 个面分别 dispatch 天空渲染，输出到 Cubemap。
        /// </summary>
        /// <param name="cmd">命令列表。</param>
        /// <param name="resolution">单面分辨率。</param>
        /// <param name="bakeCBUpload">烘焙常量上传缓冲。</param>
        /// <param name="cubemapUAVs">6 个面的 UAV。</param>
        public void BakeCubemap(RHICommandList cmd, int resolution, RHIBuffer bakeCBUpload, RHIDescriptorHandle[] cubemapUAVs)
        {
            // 静态 6 方向
            Vector3[] faces =
            {
                new(1,0,0), new(-1,0,0), new(0,1,0), new(0,-1,0), new(0,0,1), new(0,0,-1)
            };
            for (int f = 0; f < 6; f++)
            {
                var cb = new CubemapBakeCB
                {
                    FaceIndex = f,
                    Resolution = resolution,
                    SkyModel = (int)Model,
                    SunDirection = SunDirection,
                    SunIlluminance = SunIlluminance,
                    CameraPosition = new Vector3(0, 6371001f, 0),
                    PlanetRadius = 6371000f,
                    AtmosphereThickness = 100000f,
                    RayleighScaleHeight = 8500f,
                    MieScaleHeight = 1200f,
                    RayleighScattering = new Vector3(5.802e-6f, 13.558e-6f, 33.100e-6f),
                    MieScattering = 21e-6f
                };
                _device.UploadToBuffer(bakeCBUpload, MemoryMarshal.CreateReadOnlySpan(ref cb, 1));
                cmd.SetPipelineState(_skyPSO!);
                cmd.SetComputeRootSignature(_skyRootSig!);
                cmd.SetComputeRootConstantBufferView(0, bakeCBUpload);
                cmd.SetComputeRootDescriptorTable(1, _atmosphere.LUTs.TransmittanceSRV);
                cmd.SetComputeRootDescriptorTable(2, _atmosphere.LUTs.MultiScatterSRV);
                cmd.SetComputeRootDescriptorTable(5, cubemapUAVs[f]);
                cmd.Dispatch((resolution + 7) / 8, (resolution + 7) / 8, 1);
            }
        }

        /// <summary>释放资源。</summary>
        public void Dispose()
        {
            if (_disposed) return;
            Stars.Dispose();
            _skyPSO?.Dispose();
            _skyRootSig?.Dispose();
            _diskPSO?.Dispose();
            _diskRootSig?.Dispose();
            _skyCBUpload?.Dispose();
            _diskCBUpload?.Dispose();
            _disposed = true;
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 天空捕获 (SkyCapture)
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 天空捕获器。把天空辐射烘焙到 Cubemap，供 IBL（漫反射辐照度 / 镜面反射预滤波）
    /// 与远景天空盒使用。支持按时间间隔或太阳角度变化触发捕获，并支持多分辨率：
    /// 低分辨率（64）用于实时反射，高分辨率（256）用于天空盒。
    /// </summary>
    public sealed class SkyCapture : IDisposable
    {
        private readonly RHIDevice _device;
        private readonly ProceduralSkyRenderer _sky;
        private RHITexture? _lowResCube;
        private RHITexture? _highResCube;
        private RHIDescriptorHandle _lowResSRV;
        private RHIDescriptorHandle _highResSRV;
        private RHIBuffer? _bakeCBUpload;
        private RHIDescriptorHandle[] _lowUAVs = new RHIDescriptorHandle[6];
        private RHIDescriptorHandle[] _highUAVs = new RHIDescriptorHandle[6];
        private float _captureTimer;
        private float _lastSunAlt;
        private bool _disposed;

        /// <summary>触发条件。</summary>
        public SkyCaptureTrigger Trigger = SkyCaptureTrigger.SunAngleDelta;
        /// <summary>时间间隔（秒，TimeInterval 模式）。</summary>
        public float Interval = 2.0f;
        /// <summary>太阳角度变化阈值（弧度，SunAngleDelta 模式）。</summary>
        public float SunAngleThreshold = 0.05f;
        /// <summary>低分辨率（反射用）。</summary>
        public const int LowRes = 64;
        /// <summary>高分辨率（天空盒用）。</summary>
        public const int HighRes = 256;
        /// <summary>低分辨率 Cubemap SRV。</summary>
        public RHIDescriptorHandle LowResSRV => _lowResSRV;
        /// <summary>高分辨率 Cubemap SRV。</summary>
        public RHIDescriptorHandle HighResSRV => _highResSRV;

        public SkyCapture(RHIDevice device, ProceduralSkyRenderer sky)
        {
            _device = device;
            _sky = sky;
        }

        /// <summary>分配 Cubemap 资源。</summary>
        public void Initialize()
        {
            _lowResCube = CreateCubemap(LowRes, "Sky.CaptureLow");
            _highResCube = CreateCubemap(HighRes, "Sky.CaptureHigh");
            _lowResSRV = _device.CreateSRV(_lowResCube);
            _highResSRV = _device.CreateSRV(_highResCube);
            // 逐面 UAV：Cubemap 在 D3D12 中以 2D 纹理数组形式创建无序访问视图，
            // 通过 FirstArraySlice 指定面索引、ArraySize=1 仅写入该面。
            for (int i = 0; i < 6; i++)
            {
                var faceDesc = new RHIUAVDesc
                {
                    Format = RHIFormat.R16G16B16A16_Float,
                    Dimension = RHIResourceDimension.Texture2D,
                    MipSlice = 0,
                    FirstArraySlice = (uint)i,
                    ArraySize = 1
                };
                _lowUAVs[i] = _device.CreateUAV(_lowResCube, faceDesc);
                _highUAVs[i] = _device.CreateUAV(_highResCube, faceDesc);
            }
            _bakeCBUpload = _device.CreateUploadBuffer((ulong)Marshal.SizeOf<CubemapBakeCB>(), "Sky.BakeCB");
        }

        private RHITexture CreateCubemap(int res, string name)
        {
            return _device.CreateTexture(new RHITextureDesc
            {
                Dimension = RHIResourceDimension.Texture2D,
                Format = RHIFormat.R16G16B16A16_Float,
                Width = res, Height = res, DepthOrArraySize = 6,
                MipLevels = 1, SampleCount = 1, SampleQuality = 0,
                Flags = RHITextureFlags.UnorderedAccess | RHITextureFlags.ShaderResource | RHITextureFlags.Cube
            }, name);
        }

        /// <summary>每帧更新，按触发条件决定是否捕获。</summary>
        public void Update(RHICommandList cmd, float dt, float sunAltitude)
        {
            bool shouldCapture = false;
            switch (Trigger)
            {
                case SkyCaptureTrigger.TimeInterval:
                    _captureTimer += dt;
                    if (_captureTimer >= Interval) { shouldCapture = true; _captureTimer = 0f; }
                    break;
                case SkyCaptureTrigger.SunAngleDelta:
                    if (MathF.Abs(sunAltitude - _lastSunAlt) > SunAngleThreshold) { shouldCapture = true; _lastSunAlt = sunAltitude; }
                    break;
                case SkyCaptureTrigger.EveryFrame:
                    shouldCapture = true;
                    break;
            }
            if (shouldCapture) Capture(cmd);
        }

        /// <summary>执行捕获：烘焙低分辨率与高分辨率 Cubemap。</summary>
        public void Capture(RHICommandList cmd)
        {
            if (_bakeCBUpload == null) return;
            // 低分辨率：实时刷新
            _sky.BakeCubemap(cmd, LowRes, _bakeCBUpload, _lowUAVs);
            // 高分辨率：仅在天空模型为 Nishita 或显式调用时烘焙
            _sky.BakeCubemap(cmd, HighRes, _bakeCBUpload, _highUAVs);
        }

        /// <summary>释放资源。</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _lowResCube?.Dispose();
            _highResCube?.Dispose();
            _bakeCBUpload?.Dispose();
            _disposed = true;
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 昼夜循环渲染器 (DayNightCycleRenderer)
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 昼夜循环渲染器。与 <see cref="WeatherSystem.DayNight"/>（DayNightCycle）联动：
    /// 读取其计算出的太阳/月亮位置、照度、星光强度与天色，写入
    /// <see cref="ProceduralSkyRenderer"/> 与共享的 <see cref="AtmosphereSettings"/>，
    /// 从而让程序化天空与天气系统保持同步。
    /// </summary>
    public sealed class DayNightCycleRenderer
    {
        /// <summary>程序化天空渲染器。</summary>
        public ProceduralSkyRenderer? Sky;
        /// <summary>共享大气设置。</summary>
        public AtmosphereSettings? Atmosphere;
        /// <summary>天气系统（用于读取昼夜状态与天气染色）。</summary>
        public WeatherSystem? Weather;
        /// <summary>是否覆盖太阳方向（用于编辑器手动调试）。</summary>
        public bool OverrideSunDirection;
        /// <summary>手动太阳方向（OverrideSunDirection=true 时生效）。</summary>
        public Vector3 ManualSunDirection = Vector3.Normalize(new Vector3(0.5f, 0.6f, 0.4f));

        /// <summary>从天气系统同步昼夜状态到天空渲染器。</summary>
        public void SyncFromWeather()
        {
            if (Weather == null || Sky == null) return;
            var dayNight = Weather.DayNight;
            Vector3 sunDir = OverrideSunDirection ? ManualSunDirection : dayNight.Sun.Direction;
            // 天气染色：多云/暴风时整体压暗
            float ambient = Weather.StateMachine.CurrentProfile.AmbientScale;
            float sunLux = dayNight.Sun.Elevation > 0f
                ? dayNight.SunIlluminanceScale * ambient
                : 0f;
            Sky.UpdateCelestial(sunDir, sunLux, dayNight.Moon.Direction, dayNight.Moon.Intensity * dayNight.MoonIlluminanceScale, Weather.GetStarIntensity());

            // 回写大气设置（太阳方向、照度）
            if (Atmosphere != null)
            {
                Atmosphere.SunDirection = sunDir;
                Atmosphere.SunIlluminance = sunLux;
            }
        }

        /// <summary>每帧推进：先同步昼夜状态，再驱动天空捕获。</summary>
        /// <param name="dt">帧间隔。</param>
        public void Update(float dt) { /* 状态已在 WeatherSystem.Update 中推进；此处仅做同步桥接 */ SyncFromWeather(); }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // ECS 系统 (ProceduralSkySystem)
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 程序化天空 ECS 系统。查询挂有 <see cref="ProceduralSkyComponent"/> 的实体，
    /// 把其配置应用到全局天空渲染器，并每帧推进渲染与捕获。引擎内通常只有一个
    /// 活跃的天空实体；若存在多个，以最高优先级（最后一个 Enabled）为准。
    /// </summary>
    public sealed class ProceduralSkySystem : ISystem, IDisposable
    {
        /// <summary>系统名称。</summary>
        public string Name => "ProceduralSkySystem";

        private readonly RHIDevice _device;
        /// <summary>程序化天空渲染器（由本系统持有）。</summary>
        public ProceduralSkyRenderer SkyRenderer;
        /// <summary>天空捕获器。</summary>
        public SkyCapture? Capture;
        /// <summary>昼夜循环渲染器。</summary>
        public DayNightCycleRenderer DayNightRenderer = new();
        /// <summary>大气渲染器引用。</summary>
        public AtmosphereRenderer? Atmosphere;
        /// <summary>天气系统引用。</summary>
        public WeatherSystem? Weather;
        /// <summary>主命令列表（由渲染管线注入）。</summary>
        public RHICommandList? CommandList;
        /// <summary>主相机位置。</summary>
        public Vector3 CameraPosition;
        /// <summary>当前时间（秒）。</summary>
        public float Time;
        private bool _disposed;

        /// <summary>构造：绑定设备与大气渲染器。</summary>
        public ProceduralSkySystem(RHIDevice device, AtmosphereRenderer atmosphere)
        {
            _device = device;
            Atmosphere = atmosphere;
            SkyRenderer = new ProceduralSkyRenderer(device, atmosphere);
        }

        /// <summary>初始化资源。</summary>
        /// <param name="width">视口宽。</param>
        /// <param name="height">视口高。</param>
        /// <param name="weather">天气系统（可选）。</param>
        public void Initialize(int width, int height, WeatherSystem? weather = null)
        {
            Weather = weather;
            SkyRenderer.Initialize(width, height);
            Capture = new SkyCapture(_device, SkyRenderer);
            Capture.Initialize();
            DayNightRenderer.Sky = SkyRenderer;
            DayNightRenderer.Weather = weather;
            // 把天气系统持有的共享 AtmosphereSettings 桥接给昼夜渲染器回写
            DayNightRenderer.Atmosphere = Weather?.Atmosphere;
        }

        /// <summary>视口尺寸变化。</summary>
        public void OnResize(int width, int height) => SkyRenderer.OnResize(width, height);

        /// <summary>每帧更新：同步组件配置、推进昼夜、渲染天空、更新捕获。</summary>
        public void Update(float dt, EntityComponentSystem ecs)
        {
            Time += dt;
            // 1. 查询天空实体，应用组件配置
            var entities = ecs.QueryWithComponent<ProceduralSkyComponent>();
            foreach (int id in entities)
            {
                var comp = ecs.GetComponent<ProceduralSkyComponent>(id);
                if (!comp.Enabled) continue;
                SkyRenderer.Model = comp.Model;
                SkyRenderer.EnableSunGlare = comp.EnableSunGlare;
                SkyRenderer.EnableCloudShadow = comp.EnableCloudShadow;
                SkyRenderer.EnableStars = comp.EnableStars;
                // 把组件配置写入共享的大气设置（天气系统持有），驱动 AtmosphereRenderer
                SkyAtmosphereConfig? cfg = comp.Config;
                AtmosphereSettings? atm = Weather?.Atmosphere;
                if (cfg != null && atm != null)
                    cfg.ApplyTo(atm);
            }

            // 2. 昼夜同步
            DayNightRenderer.Update(dt);

            // 3. 渲染天空
            if (CommandList != null)
            {
                SkyRenderer.Render(CommandList, CameraPosition, Time);

                // 4. 天空捕获（IBL）
                float sunAlt = Weather?.DayNight.Sun.Elevation ?? 0.5f;
                if (Capture != null) Capture.Update(CommandList, dt, sunAlt);
            }
        }

        /// <summary>释放资源。</summary>
        public void Dispose()
        {
            if (_disposed) return;
            SkyRenderer.Dispose();
            Capture?.Dispose();
            _disposed = true;
        }
    }
}
