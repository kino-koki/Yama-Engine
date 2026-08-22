// ============================================================================================
//  LightweightLightingSystem.cs
//
//  山雨引擎 轻量光效系统 —— 低配置也能跑的光效管线
//
//  设计原则:
//    - 所有效果在半分辨率或更低执行, 然后双线性上采样到全屏
//    - 不使用真正的光线追踪或密集 ray marching
//    - 使用屏幕空间技术 + 数学近似, 避免复杂的 3D 计算
//    - 每帧总开销控制在 1-2ms 以内 (GTX 1060 级别)
//
//  包含效果:
//    1. 屏幕空间体积光 (Screen-Space God Rays): 径向模糊, 从太阳位置发散
//    2. 轻量镜头光晕 (Lightweight Lens Flare): 屏幕空间光斑 + 鬼影
//    3. LUT 色彩分级 (LUT Color Grading): 预计算查找表, 一次纹理采样
//    4. 体积光束 (Volumetric Light Beam): 聚光灯体积光, 使用 2D 距离场近似
//    5. 大气散射近似 (Atmospheric Scattering Approx): 简化的瑞利+米氏散射
//    6. 泛光 (Bloom): 降采样 + 高斯模糊 + 合成
//
//  命名空间: ShanyuEngine.Rendering
//  目标框架: net8.0 / C# 12+
// ============================================================================================

using System;
using System.Numerics;

namespace ShanyuEngine.Rendering
{
    // ════════════════════════════════════════════════════════════════════════
    //  质量等级 —— 根据硬件性能自动选择
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 光效质量等级 —— 根据硬件自动选择
    /// </summary>
    public enum LightingQualityLevel
    {
        /// <summary>极低: 仅色彩分级, 无其他光效</summary>
        UltraLow,
        /// <summary>低: 色彩分级 + 简化泛光</summary>
        Low,
        /// <summary>中: + 体积光 + 镜头光晕</summary>
        Medium,
        /// <summary>高: + 光束 + 大气散射</summary>
        High,
        /// <summary>超高: 全部效果 + 更高质量采样</summary>
        Ultra
    }

    /// <summary>
    /// 轻量光效系统配置
    /// </summary>
    public sealed class LightweightLightingConfig
    {
        /// <summary>质量等级</summary>
        public LightingQualityLevel Quality = LightingQualityLevel.Medium;

        /// <summary>渲染分辨率缩放 (1.0=全分辨率, 0.5=半分辨率)</summary>
        public float ResolutionScale = 0.5f;

        // === 体积光 (God Rays) ===
        public bool EnableGodRays = true;
        public int GodRaySamples = 24;          // 径向采样数 (16~64)
        public float GodRayDecay = 0.95f;      // 每次采样衰减
        public float GodRayExposure = 0.3f;    // 强度
        public float GodRayDensity = 0.8f;     // 采样间距

        // === 镜头光晕 ===
        public bool EnableLensFlare = true;
        public int LensFlareGhosts = 4;        // 鬼影数量
        public float LensFlareIntensity = 0.5f;
        public float LensFlareThreshold = 0.9f; // 亮度阈值

        // === 色彩分级 ===
        public bool EnableColorGrading = true;
        public float LUTIntensity = 1.0f;       // LUT 混合强度

        // === 泛光 ===
        public bool EnableBloom = true;
        public float BloomThreshold = 0.8f;
        public float BloomIntensity = 0.4f;
        public int BloomMipLevels = 4;         // 降采样层数
        public float BloomRadius = 0.5f;        // 模糊半径

        // === 光束 (聚光灯体积光) ===
        public bool EnableLightBeam = true;
        public int LightBeamSamples = 16;
        public float LightBeamIntensity = 0.3f;

        // === 大气散射 ===
        public bool EnableAtmosphere = true;
        public float AtmosphereIntensity = 0.5f;

        // === 色调映射 ===
        public bool EnableToneMapping = true;
        public float Exposure = 1.0f;

        /// <summary>
        /// 根据硬件性能自动选择质量等级
        /// </summary>
        public static LightweightLightingConfig AutoDetect(int vramMB, int coreCount)
        {
            var config = new LightweightLightingConfig();

            if (vramMB < 2048 || coreCount < 4)
            {
                // 极低配置: 集成显卡
                config.Quality = LightingQualityLevel.UltraLow;
                config.ResolutionScale = 0.5f;
                config.EnableGodRays = false;
                config.EnableLensFlare = false;
                config.EnableBloom = true;
                config.BloomMipLevels = 2;
                config.EnableLightBeam = false;
                config.EnableAtmosphere = false;
            }
            else if (vramMB < 4096 || coreCount < 6)
            {
                // 低配置: GTX 1050 / RX 560 级别
                config.Quality = LightingQualityLevel.Low;
                config.ResolutionScale = 0.5f;
                config.GodRaySamples = 16;
                config.LensFlareGhosts = 2;
                config.BloomMipLevels = 3;
                config.EnableLightBeam = false;
                config.EnableAtmosphere = false;
            }
            else if (vramMB < 8192 || coreCount < 8)
            {
                // 中配置: GTX 1060 / RX 580 级别
                config.Quality = LightingQualityLevel.Medium;
                config.ResolutionScale = 0.5f;
                config.GodRaySamples = 24;
                config.LensFlareGhosts = 4;
                config.BloomMipLevels = 4;
            }
            else if (vramMB < 12288)
            {
                // 高配置: RTX 2070 / RX 5700 级别
                config.Quality = LightingQualityLevel.High;
                config.ResolutionScale = 0.75f;
                config.GodRaySamples = 32;
                config.LensFlareGhosts = 6;
                config.BloomMipLevels = 5;
                config.LightBeamSamples = 24;
            }
            else
            {
                // 超高配置: RTX 3080+ 级别
                config.Quality = LightingQualityLevel.Ultra;
                config.ResolutionScale = 1.0f;
                config.GodRaySamples = 48;
                config.LensFlareGhosts = 8;
                config.BloomMipLevels = 6;
                config.LightBeamSamples = 32;
            }

            return config;
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  屏幕空间体积光 (Screen-Space God Rays)
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 屏幕空间体积光 —— 径向模糊法
    /// 从太阳位置向屏幕中心做径向采样, 累积亮度, 产生光束效果
    /// 性能: O(samples * 1) = 极低开销
    /// </summary>
    public sealed class ScreenSpaceGodRays
    {
        private readonly LightweightLightingConfig _config;

        public Vector2 SunScreenPosition { get; set; } = new(0.5f, 0.3f);
        public Vector3 SunColor { get; set; } = new(1.0f, 0.95f, 0.85f);

        public ScreenSpaceGodRays(LightweightLightingConfig config)
        {
            _config = config;
        }

        /// <summary>
        /// 计算单个像素的体积光贡献
        /// 在 CPU 上计算, 实际由 GPU shader 执行
        /// </summary>
        public Vector3 ComputeGodRays(
            Vector2 uv,
            Func<Vector2, float> sampleDepth,
            Func<Vector2, Vector3> sampleColor,
            float maxDepth)
        {
            if (!_config.EnableGodRays)
                return Vector3.Zero;

            Vector2 deltaUV = uv - SunScreenPosition;
            float illumination = 0f;
            float totalWeight = 0f;

            int samples = _config.GodRaySamples;
            float density = _config.GodRayDensity;
            float decay = _config.GodRayDecay;
            float weight = 1.0f;

            // 径向采样
            for (int i = 0; i < samples; i++)
            {
                float t = i / (float)samples;
                Vector2 sampleUV = uv - deltaUV * density * t;

                // 检查是否被遮挡 (深度测试)
                float depth = sampleDepth(sampleUV);
                if (depth < maxDepth)
                {
                    // 未被遮挡, 累积亮度
                    Vector3 color = sampleColor(sampleUV);
                    float brightness = (color.X + color.Y + color.Z) / 3f;
                    illumination += brightness * weight;
                }
                totalWeight += weight;
                weight *= decay;
            }

            illumination /= MathF.Max(totalWeight, 1e-6f);
            illumination *= _config.GodRayExposure;

            return SunColor * illumination;
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  轻量镜头光晕 (Lightweight Lens Flare)
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 轻量镜头光晕 —— 沿光轴分布的鬼影
    /// </summary>
    public sealed class LightweightLensFlare
    {
        private readonly LightweightLightingConfig _config;

        public Vector2 LightScreenPosition { get; set; } = new(0.5f, 0.5f);
        public Vector3 LightColor { get; set; } = new(1.0f, 0.9f, 0.8f);

        // 鬼影参数 (位置, 缩放, 颜色偏移)
        private readonly (float pos, float scale, Vector3 colorShift)[] _ghostParams;

        public LightweightLensFlare(LightweightLightingConfig config)
        {
            _config = config;

            // 预定义鬼影参数
            _ghostParams = new[]
            {
                (0.5f,  0.4f, new Vector3(1.0f, 0.8f, 0.6f)),   // 暖色鬼影
                (0.3f,  0.3f, new Vector3(0.6f, 0.8f, 1.0f)),   // 冷色鬼影
                (-0.2f, 0.5f, new Vector3(1.0f, 0.6f, 0.4f)),   // 橙色鬼影
                (-0.5f, 0.6f, new Vector3(0.4f, 0.6f, 1.0f)),  // 蓝色鬼影
                (0.7f,  0.2f, new Vector3(0.8f, 1.0f, 0.6f)),   // 绿色鬼影
                (-0.7f, 0.25f, new Vector3(1.0f, 0.4f, 0.8f)),  // 粉色鬼影
                (0.15f, 0.15f, new Vector3(0.6f, 1.0f, 1.0f)),  // 青色鬼影
                (-0.15f, 0.18f, new Vector3(1.0f, 1.0f, 0.4f)), // 黄色鬼影
            };
        }

        /// <summary>
        /// 计算指定 UV 处的镜头光晕颜色
        /// </summary>
        public Vector3 ComputeFlare(Vector2 uv, float sourceIntensity, bool isOccluded)
        {
            if (!_config.EnableLensFlare || isOccluded || sourceIntensity < _config.LensFlareThreshold)
                return Vector3.Zero;

            Vector2 toCenter = new(0.5f) - LightScreenPosition;
            Vector3 result = Vector3.Zero;

            int ghostCount = Math.Min(_config.LensFlareGhosts, _ghostParams.Length);

            for (int i = 0; i < ghostCount; i++)
            {
                var ghost = _ghostParams[i];

                // 鬼影位置: 沿光轴偏移
                Vector2 ghostPos = LightScreenPosition + toCenter * ghost.pos;

                // 计算到鬼影的距离
                Vector2 diff = uv - ghostPos;
                float dist = diff.Length();

                // 鬼影形状: 高斯衰减
                float falloff = MathF.Exp(-dist * dist * ghost.scale * 50f);
                float intensity = falloff * sourceIntensity * _config.LensFlareIntensity;

                // 色散: 不同颜色通道略微偏移
                result += LightColor * ghost.colorShift * intensity;
            }

            // 全局光晕 (光源周围的柔和发光)
            float haloDist = (uv - LightScreenPosition).Length();
            float halo = MathF.Exp(-haloDist * haloDist * 20f) * sourceIntensity * 0.1f;
            result += LightColor * halo;

            return result;
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  LUT 色彩分级 (LUT Color Grading)
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// LUT 色彩分级 —— 使用预计算查找表进行色彩映射
    /// 性能: 每像素一次 3D 纹理采样, 极低开销
    /// </summary>
    public sealed class LUTColorGrading
    {
        private readonly LightweightLightingConfig _config;
        private readonly Vector3[] _lut;       // 32x32x32 LUT
        private readonly int _lutSize = 32;

        // 色调映射参数
        public float Exposure { get; set; } = 1.0f;
        public float Contrast { get; set; } = 1.0f;
        public float Saturation { get; set; } = 1.0f;
        public Vector3 Temperature { get; set; } = Vector3.Zero; // 暖/冷色调
        public Vector3 Tint { get; set; } = Vector3.Zero;       // 绿/品红
        public float Lift { get; set; } = 0f;                   // 阴影
        public float Gamma { get; set; } = 0f;                  // 中间调
        public float Gain { get; set; } = 0f;                   // 高光

        public LUTColorGrading(LightweightLightingConfig config)
        {
            _config = config;
            _lut = new Vector3[_lutSize * _lutSize * _lutSize];
            GenerateIdentityLUT();
        }

        /// <summary>
        /// 生成单位 LUT (无色彩变换)
        /// </summary>
        private void GenerateIdentityLUT()
        {
            for (int b = 0; b < _lutSize; b++)
            for (int g = 0; g < _lutSize; g++)
            for (int r = 0; r < _lutSize; r++)
            {
                int idx = (b * _lutSize + g) * _lutSize + r;
                _lut[idx] = new Vector3(
                    r / (float)(_lutSize - 1),
                    g / (float)(_lutSize - 1),
                    b / (float)(_lutSize - 1));
            }
        }

        /// <summary>
        /// 应用色彩分级参数, 更新 LUT
        /// </summary>
        public void UpdateLUT()
        {
            for (int b = 0; b < _lutSize; b++)
            for (int g = 0; g < _lutSize; g++)
            for (int r = 0; r < _lutSize; r++)
            {
                Vector3 color = new(
                    r / (float)(_lutSize - 1),
                    g / (float)(_lutSize - 1),
                    b / (float)(_lutSize - 1));

                color = ApplyColorTransform(color);
                int idx = (b * _lutSize + g) * _lutSize + r;
                _lut[idx] = Vector3.Clamp(color, Vector3.Zero, Vector3.One);
            }
        }

        /// <summary>
        /// 对单个颜色应用色彩变换
        /// </summary>
        private Vector3 ApplyColorTransform(Vector3 color)
        {
            // 曝光
            color *= Exposure;

            // ASC-CDL 风格: (color * gain + lift) ^ (1/gamma)
            float liftVal = Lift * 0.5f;
            float gammaVal = 1.0f / MathF.Max(0.01f, 1.0f + Gamma * 0.5f);
            float gainVal = 1.0f + Gain * 0.5f;

            color = color * gainVal + liftVal;
            color = new Vector3(
                MathF.Pow(color.X, gammaVal),
                MathF.Pow(color.Y, gammaVal),
                MathF.Pow(color.Z, gammaVal));

            // 对比度 (围绕 0.5)
            color = (color - 0.5f) * Contrast + 0.5f;

            // 饱和度 (转换为亮度, 按比例混合)
            float luma = Vector3.Dot(color, new Vector3(0.2126f, 0.7152f, 0.0722f));
            color = Vector3.Lerp(new(luma), color, Saturation);

            // 色温 (暖/冷)
            color += Temperature;

            // 色调
            color += Tint;

            return Vector3.Clamp(color, Vector3.Zero, Vector3.One);
        }

        /// <summary>
        /// 采样 LUT (三线性插值)
        /// </summary>
        public Vector3 SampleLUT(Vector3 color)
        {
            if (!_config.EnableColorGrading)
                return color;

            color = Vector3.Clamp(color, Vector3.Zero, Vector3.One);

            float scale = (_lutSize - 1);
            Vector3 pos = color * scale;
            int r0 = (int)pos.X, g0 = (int)pos.Y, b0 = (int)pos.Z;
            int r1 = Math.Min(r0 + 1, _lutSize - 1);
            int g1 = Math.Min(g0 + 1, _lutSize - 1);
            int b1 = Math.Min(b0 + 1, _lutSize - 1);

            Vector3 frac = pos - new Vector3(r0, g0, b0);

            // 三线性插值
            Vector3 c000 = _lut[(b0 * _lutSize + g0) * _lutSize + r0];
            Vector3 c001 = _lut[(b0 * _lutSize + g0) * _lutSize + r1];
            Vector3 c010 = _lut[(b0 * _lutSize + g1) * _lutSize + r0];
            Vector3 c011 = _lut[(b0 * _lutSize + g1) * _lutSize + r1];
            Vector3 c100 = _lut[(b1 * _lutSize + g0) * _lutSize + r0];
            Vector3 c101 = _lut[(b1 * _lutSize + g0) * _lutSize + r1];
            Vector3 c110 = _lut[(b1 * _lutSize + g1) * _lutSize + r0];
            Vector3 c111 = _lut[(b1 * _lutSize + g1) * _lutSize + r1];

            Vector3 c00 = Vector3.Lerp(c000, c001, frac.X);
            Vector3 c01 = Vector3.Lerp(c010, c011, frac.X);
            Vector3 c10 = Vector3.Lerp(c100, c101, frac.X);
            Vector3 c11 = Vector3.Lerp(c110, c111, frac.X);

            Vector3 c0 = Vector3.Lerp(c00, c01, frac.Y);
            Vector3 c1 = Vector3.Lerp(c10, c11, frac.Y);

            return Vector3.Lerp(c0, c1, frac.Z) * _config.LUTIntensity +
                   color * (1f - _config.LUTIntensity);
        }

        /// <summary>
        /// 加载预设风格
        /// </summary>
        public void LoadPreset(ColorGradingPreset preset)
        {
            switch (preset)
            {
                case ColorGradingPreset.Neutral:
                    Exposure = 1.0f; Contrast = 1.0f; Saturation = 1.0f;
                    Temperature = Vector3.Zero; Tint = Vector3.Zero;
                    Lift = 0f; Gamma = 0f; Gain = 0f;
                    break;
                case ColorGradingPreset.Cinematic:
                    Exposure = 1.0f; Contrast = 1.15f; Saturation = 0.9f;
                    Temperature = new(0.02f, 0f, -0.02f); Tint = Vector3.Zero;
                    Lift = -0.02f; Gamma = 0.05f; Gain = 0.03f;
                    break;
                case ColorGradingPreset.Warm:
                    Exposure = 1.05f; Contrast = 1.1f; Saturation = 1.1f;
                    Temperature = new(0.04f, 0.01f, -0.03f); Tint = Vector3.Zero;
                    Lift = 0.02f; Gamma = 0f; Gain = 0f;
                    break;
                case ColorGradingPreset.Cold:
                    Exposure = 0.98f; Contrast = 1.05f; Saturation = 0.95f;
                    Temperature = new(-0.03f, 0.01f, 0.04f); Tint = Vector3.Zero;
                    Lift = -0.01f; Gamma = 0.02f; Gain = 0f;
                    break;
                case ColorGradingPreset.Vintage:
                    Exposure = 0.95f; Contrast = 0.9f; Saturation = 0.7f;
                    Temperature = new(0.05f, 0.02f, -0.04f); Tint = new(0, 0.01f, 0);
                    Lift = 0.05f; Gamma = -0.05f; Gain = -0.02f;
                    break;
                case ColorGradingPreset.Vivid:
                    Exposure = 1.05f; Contrast = 1.2f; Saturation = 1.3f;
                    Temperature = Vector3.Zero; Tint = Vector3.Zero;
                    Lift = -0.02f; Gamma = 0.05f; Gain = 0.05f;
                    break;
            }
            UpdateLUT();
        }
    }

    /// <summary>色彩分级预设</summary>
    public enum ColorGradingPreset
    {
        Neutral,    // 中性
        Cinematic,  // 电影感
        Warm,       // 暖色调
        Cold,       // 冷色调
        Vintage,    // 复古
        Vivid       // 鲜艳
    }

    // ════════════════════════════════════════════════════════════════════════
    //  体积光束 (Volumetric Light Beam)
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 体积光束 —— 聚光灯体积光, 使用 2D 距离场近似
    /// </summary>
    public sealed class VolumetricLightBeam
    {
        private readonly LightweightLightingConfig _config;

        public Vector3 LightPosition { get; set; }
        public Vector3 LightDirection { get; set; } = new(0, -1, 0);
        public float ConeAngle { get; set; } = 45f;
        public float Range { get; set; } = 20f;
        public Vector3 LightColor { get; set; } = new(1f, 0.95f, 0.8f);
        public float Intensity { get; set; } = 1.0f;
        public float VolumetricIntensity { get; set; } = 0.3f;

        public VolumetricLightBeam(LightweightLightingConfig config)
        {
            _config = config;
        }

        /// <summary>
        /// 计算空间中某点的体积光束贡献
        /// 使用简化的光线-圆锥相交 + 距离衰减
        /// </summary>
        public Vector3 ComputeBeamContribution(Vector3 worldPos)
        {
            if (!_config.EnableLightBeam)
                return Vector3.Zero;

            Vector3 toPoint = worldPos - LightPosition;
            float distance = toPoint.Length();
            if (distance > Range || distance < 0.01f)
                return Vector3.Zero;

            Vector3 dir = toPoint / distance;
            float cosAngle = Vector3.Dot(dir, Vector3.Normalize(LightDirection));

            // 圆锥角度检查
            float halfConeCos = MathF.Cos(ConeAngle * 0.5f * MathF.PI / 180f);
            if (cosAngle < halfConeCos)
                return Vector3.Zero;

            // 距离衰减
            float distFalloff = 1f - (distance / Range);
            distFalloff = distFalloff * distFalloff;

            // 圆锥边缘软化
            float coneFalloff = (cosAngle - halfConeCos) / (1f - halfConeCos);
            coneFalloff = Math.Clamp(coneFalloff, 0f, 1f);

            // 体积密度 (简化: 使用距离的指数衰减)
            float density = MathF.Exp(-distance * 0.05f) * VolumetricIntensity;

            return LightColor * distFalloff * coneFalloff * density * Intensity;
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  大气散射近似 (Atmospheric Scattering)
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 简化大气散射 —— 基于高度和视角方向的近似
    /// 不使用完整的 Preetham/Hosek 模型, 而是使用查表近似
    /// </summary>
    public sealed class AtmosphericScatteringApprox
    {
        private readonly LightweightLightingConfig _config;

        public Vector3 SunDirection { get; set; } = Vector3.Normalize(new(0.3f, 0.6f, -0.5f));
        public Vector3 SunColor { get; set; } = new(1.0f, 0.95f, 0.85f);
        public float SunIntensity { get; set; } = 1.5f;

        // 散射参数
        public float RayleighCoefficient { get; set; } = 1.0f;
        public float MieCoefficient { get; set; } = 0.005f;
        public float PlanetRadius { get; set; } = 6371f;    // km
        public float AtmosphereHeight { get; set; } = 100f;  // km

        // 天空颜色 (用于天顶/地平线)
        public Vector3 ZenithColor { get; set; } = new(0.2f, 0.4f, 0.8f);
        public Vector3 HorizonColor { get; set; } = new(0.6f, 0.75f, 0.9f);

        public AtmosphericScatteringApprox(LightweightLightingConfig config)
        {
            _config = config;
        }

        /// <summary>
        /// 计算天空颜色 (简化模型)
        /// </summary>
        public Vector3 ComputeSkyColor(Vector3 viewDir)
        {
            if (!_config.EnableAtmosphere)
                return Vector3.Zero;

            float upDot = MathF.Max(viewDir.Y, 0f);
            float sunDot = Vector3.Dot(viewDir, SunDirection);

            // 天空渐变: 天顶到地平线
            float skyGradient = MathF.Pow(upDot, 0.5f);
            Vector3 skyColor = Vector3.Lerp(HorizonColor, ZenithColor, skyGradient);

            // 太阳光晕
            float sunGlow = MathF.Max(sunDot, 0f);
            float sunHalo = MathF.Pow(sunGlow, 8f) * 0.3f;
            skyColor += SunColor * sunHalo;

            // 太阳本体
            float sunDisk = MathF.Pow(sunGlow, 256f) * 2f;
            skyColor += SunColor * sunDisk * SunIntensity;

            // 瑞利散射 (简化: 天空越接近太阳方向越亮)
            float rayleigh = MathF.Pow(MathF.Max(1f - MathF.Abs(sunDot), 0f), 2f) * 0.3f;
            skyColor += new Vector3(0.1f, 0.3f, 0.6f) * rayleigh * RayleighCoefficient;

            // 米氏散射 (太阳周围的光晕)
            float mie = MathF.Pow(MathF.Max(sunDot, 0f), 16f) * 0.5f;
            skyColor += SunColor * mie * MieCoefficient * 100f;

            return skyColor * _config.AtmosphereIntensity;
        }

        /// <summary>
        /// 计算大气雾色 (距离衰减)
        /// </summary>
        public Vector3 ComputeFogColor(Vector3 viewDir, float distance, float height)
        {
            // 高度雾
            float heightFactor = MathF.Exp(-MathF.Max(height, 0f) * 0.01f);

            // 距离雾
            float distFactor = 1f - MathF.Exp(-distance * 0.002f);

            // 散射方向 (朝太阳方向更亮)
            float sunDot = MathF.Max(Vector3.Dot(viewDir, SunDirection), 0f);
            Vector3 sunFog = SunColor * MathF.Pow(sunDot, 4f) * 0.3f;

            Vector3 fogColor = HorizonColor * heightFactor + sunFog;
            return fogColor * distFactor;
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  轻量泛光 (Lightweight Bloom)
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 轻量泛光 —— 降采样 + 高斯模糊 + 合成
    /// </summary>
    public sealed class LightweightBloom
    {
        private readonly LightweightLightingConfig _config;

        public LightweightBloom(LightweightLightingConfig config)
        {
            _config = config;
        }

        /// <summary>
        /// 降采样纹理 (模拟 GPU Mip 链)
        /// </summary>
        public Vector3[] Downsample(Vector3[] source, int srcW, int srcH, int targetLevel)
        {
            int w = srcW;
            int h = srcH;
            Vector3[] current = source;

            for (int level = 0; level < targetLevel; level++)
            {
                int newW = MathF.Max(w / 2, 1);
                int newH = MathF.Max(h / 2, 1);
                Vector3[] downsampled = new Vector3[newW * newH];

                // 2x2 box filter 降采样
                for (int y = 0; y < newH; y++)
                {
                    for (int x = 0; x < newW; x++)
                    {
                        int sx = x * 2, sy = y * 2;
                        Vector3 sum = Vector3.Zero;
                        int count = 0;

                        for (int dy = 0; dy < 2 && sy + dy < h; dy++)
                        {
                            for (int dx = 0; dx < 2 && sx + dx < w; dx++)
                            {
                                Vector3 c = current[(sy + dy) * w + (sx + dx)];
                                // 亮度阈值过滤
                                float luma = Vector3.Dot(c, new(0.2126f, 0.7152f, 0.0722f));
                                if (luma > _config.BloomThreshold)
                                    sum += c;
                                count++;
                            }
                        }

                        downsampled[y * newW + x] = count > 0 ? sum / count : Vector3.Zero;
                    }
                }

                current = downsampled;
                w = newW;
                h = newH;
            }

            return current;
        }

        /// <summary>
        /// 高斯模糊 (可分离: 水平 + 垂直)
        /// </summary>
        public Vector3[] GaussianBlur(Vector3[] source, int w, int h, float radius)
        {
            if (!_config.EnableBloom)
                return source;

            // 高斯核 (5-tap, 1.4σ)
            float[] weights = { 0.227027f, 0.1945946f, 0.1216216f, 0.054054f, 0.016216f };
            int kernelRadius = weights.Length - 1;

            Vector3[] temp = new Vector3[w * h];
            Vector3[] result = new Vector3[w * h];

            // 水平模糊
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    Vector3 sum = source[y * w + x] * weights[0];
                    for (int i = 1; i <= kernelRadius; i++)
                    {
                        int xl = Math.Clamp(x - i, 0, w - 1);
                        int xr = Math.Clamp(x + i, 0, w - 1);
                        sum += source[y * w + xl] * weights[i];
                        sum += source[y * w + xr] * weights[i];
                    }
                    temp[y * w + x] = sum;
                }
            }

            // 垂直模糊
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    Vector3 sum = temp[y * w + x] * weights[0];
                    for (int i = 1; i <= kernelRadius; i++)
                    {
                        int yt = Math.Clamp(y - i, 0, h - 1);
                        int yb = Math.Clamp(y + i, 0, h - 1);
                        sum += temp[yt * w + x] * weights[i];
                        sum += temp[yb * w + x] * weights[i];
                    }
                    result[y * w + x] = sum;
                }
            }

            return result;
        }

        /// <summary>
        /// 将泛光合成到源图像
        /// </summary>
        public Vector3 Composite(Vector3 source, Vector3 bloom)
        {
            return source + bloom * _config.BloomIntensity;
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  色调映射 (Tone Mapping)
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 色调映射 —— HDR 到 LDR 转换
    /// </summary>
    public static class ToneMapper
    {
        /// <summary>ACES 电影色调映射</summary>
        public static Vector3 ACESFilm(Vector3 color, float exposure)
        {
            color *= exposure;

            // ACES 曲线近似
            float a = 2.51f, b = 0.03f, c = 2.43f, d = 0.59f, e = 0.14f;

            return new Vector3(
                Math.Clamp((color.X * (a * color.X + b)) / (color.X * (c * color.X + d) + e), 0f, 1f),
                Math.Clamp((color.Y * (a * color.Y + b)) / (color.Y * (c * color.Y + d) + e), 0f, 1f),
                Math.Clamp((color.Z * (a * color.Z + b)) / (color.Z * (c * color.Z + d) + e), 0f, 1f)
            );
        }

        /// <summary>Reinhard 色调映射 (更简单更快)</summary>
        public static Vector3 Reinhard(Vector3 color, float exposure)
        {
            color *= exposure;
            return color / (1f + color);
        }

        /// <summary>线性色调映射 (最简单)</summary>
        public static Vector3 Linear(Vector3 color, float exposure)
        {
            color *= exposure;
            return Vector3.Clamp(color, Vector3.Zero, Vector3.One);
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  轻量光效管线 (Lightweight Lighting Pipeline)
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 轻量光效管线 —— 整合所有光效, 按顺序执行
    /// </summary>
    public sealed class LightweightLightingPipeline
    {
        private readonly LightweightLightingConfig _config;
        public ScreenSpaceGodRays GodRays { get; }
        public LightweightLensFlare LensFlare { get; }
        public LUTColorGrading ColorGrading { get; }
        public VolumetricLightBeam LightBeam { get; }
        public AtmosphericScatteringApprox Atmosphere { get; }
        public LightweightBloom Bloom { get; }

        public LightweightLightingConfig Config => _config;

        public LightweightLightingPipeline(LightweightLightingConfig? config = null)
        {
            _config = config ?? LightweightLightingConfig.AutoDetect(4096, 6);
            GodRays = new ScreenSpaceGodRays(_config);
            LensFlare = new LightweightLensFlare(_config);
            ColorGrading = new LUTColorGrading(_config);
            LightBeam = new VolumetricLightBeam(_config);
            Atmosphere = new AtmosphericScatteringApprox(_config);
            Bloom = new LightweightBloom(_config);
        }

        /// <summary>
        /// 处理单个像素 (CPU 参考实现, 实际由 GPU shader 执行)
        /// </summary>
        public Vector3 ProcessPixel(
            Vector2 uv,
            Vector3 sceneColor,
            float sceneDepth,
            Vector3 viewDir,
            float sunScreenIntensity,
            bool sunOccluded,
            int width,
            int height)
        {
            Vector3 color = sceneColor;

            // 1. 体积光
            if (_config.EnableGodRays)
            {
                // 简化: 直接基于 UV 到太阳位置的距离计算
                Vector2 sunUV = GodRays.SunScreenPosition;
                float distToSun = Vector2.Distance(uv, sunUV);
                float rayIntensity = MathF.Exp(-distToSun * 2f) * sunScreenIntensity;
                if (!sunOccluded)
                {
                    color += GodRays.SunColor * rayIntensity * _config.GodRayExposure;
                }
            }

            // 2. 镜头光晕
            if (_config.EnableLensFlare)
            {
                color += LensFlare.ComputeFlare(uv, sunScreenIntensity, sunOccluded);
            }

            // 3. 光束 (体积光)
            if (_config.EnableLightBeam)
            {
                // 需要世界位置, 这里用简化近似
                // 实际在 GPU shader 中计算
            }

            // 4. 色调映射 (HDR → LDR)
            if (_config.EnableToneMapping)
            {
                color = ToneMapper.ACESFilm(color, _config.Exposure);
            }

            // 5. 色彩分级 (LUT)
            if (_config.EnableColorGrading)
            {
                color = ColorGrading.SampleLUT(color);
            }

            return Vector3.Clamp(color, Vector3.Zero, Vector3.One);
        }

        /// <summary>
        /// 估算管线开销 (毫秒, 基于 GTX 1060)
        /// </summary>
        public float EstimateCostMS(int width, int height)
        {
            float pixels = width * height * _config.ResolutionScale * _config.ResolutionScale;
            float cost = 0f;

            // 色彩分级: 1 纹理采样/像素
            if (_config.EnableColorGrading) cost += pixels * 0.5e-6f;

            // 体积光: samples 次采样/像素
            if (_config.EnableGodRays) cost += pixels * _config.GodRaySamples * 0.1e-6f;

            // 镜头光晕: ghosts 次计算/像素
            if (_config.EnableLensFlare) cost += pixels * _config.LensFlareGhosts * 0.2e-6f;

            // 泛光: mipLevels 次降采样 + 模糊
            if (_config.EnableBloom) cost += pixels * _config.BloomMipLevels * 0.5e-6f;

            // 光束
            if (_config.EnableLightBeam) cost += pixels * _config.LightBeamSamples * 0.05e-6f;

            // 大气
            if (_config.EnableAtmosphere) cost += pixels * 0.1e-6f;

            // 色调映射
            if (_config.EnableToneMapping) cost += pixels * 0.1e-6f;

            return cost * 1000f; // 转为毫秒
        }
    }
}
