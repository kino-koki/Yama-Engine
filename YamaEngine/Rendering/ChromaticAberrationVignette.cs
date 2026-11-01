using System;
using System.Numerics;
using System.Runtime.InteropServices;
using YamaEngine.RHI;
using YamaEngine.Shaders;

namespace YamaEngine.Rendering
{
    // ════════════════════════════════════════════════════════════════════════
    // yama 镜头特效系统 (Chromatic Aberration + Vignette + Film Grain + ColorGrading)
    //
    // 合并在一个文件中，包含四种镜头/色彩后处理特效：
    //   1. ChromaticAberrationEffect：RGB通道径向偏移色差
    //   2. VignetteEffect：画面边缘暗化
    //   3. FilmGrainEffect：基于时间的伪随机胶片颗粒
    //   4. ColorGradingEffect：ASC CDL色彩校正 + LUT + 分离调整
    // ════════════════════════════════════════════════════════════════════════

    // ────────────────────────────────────────────────────────────────────────
    // 色差 (Chromatic Aberration)
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>色差设置。</summary>
    [Serializable]
    public struct ChromaticAberrationSettings
    {
        /// <summary>是否启用。</summary>
        public bool Enabled;

        /// <summary>色差强度 [0, 1]。值越大偏移越明显。</summary>
        public float Intensity;

        /// <summary>从屏幕中心开始生效的径向距离 [0, 1]。</summary>
        public float StartOffset;

        /// <summary>默认设置。</summary>
        public static ChromaticAberrationSettings Default => new()
        {
            Enabled = true, Intensity = 0.1f, StartOffset = 0.5f
        };

        public static ChromaticAberrationSettings Disabled => new() { Enabled = false };
    }

    /// <summary>
    /// 色差效果：RGB通道分别偏移采样。
    /// 算法：
    ///   direction = normalize(uv - 0.5)
    ///   Red:   sample(uv + direction * intensity * (1 - dist))
    ///   Green: sample(uv) （不偏移）
    ///   Blue:  sample(uv - direction * intensity * (1 - dist))
    /// 基于镜头模型的径向偏移，远离中心的像素偏移更大。
    /// </summary>
    public static class ChromaticAberrationEffect
    {
        /// <summary>
        /// CPU 侧色差计算（用于验证/预览）。
        /// </summary>
        /// <param name="getPixel">采样函数。</param>
        /// <param name="uv">UV 坐标。</param>
        /// <param name="intensity">色差强度。</param>
        /// <param name="startOffset">起始偏移。</param>
        /// <returns>色差后的颜色。</returns>
        public static Vector3 Apply(
            Func<Vector2, Vector3> getPixel, Vector2 uv, float intensity, float startOffset)
        {
            Vector2 center = new Vector2(0.5f, 0.5f);
            Vector2 delta = uv - center;
            float dist = delta.Length();
            Vector2 direction = dist > 0.0001f ? delta / dist : Vector2.Zero;

            // 径向偏移量：基于到中心距离的比例
            float radialFactor = Math.Max(0.0f, dist - startOffset) / Math.Max(1.0f - startOffset, 0.0001f);
            radialFactor = Math.Clamp(radialFactor, 0.0f, 1.0f);
            float offset = intensity * radialFactor;

            // R 通道：正向偏移
            Vector2 uvR = uv + direction * offset;
            // G 通道：不偏移
            Vector2 uvG = uv;
            // B 通道：反向偏移
            Vector2 uvB = uv - direction * offset;

            // UV Clamping 到 [0, 1]
            uvR = Vector2.Clamp(uvR, Vector2.Zero, Vector2.One);
            uvB = Vector2.Clamp(uvB, Vector2.Zero, Vector2.One);

            return new Vector3(
                getPixel(uvR).X,
                getPixel(uvG).Y,
                getPixel(uvB).Z
            );
        }

        /// <summary>
        /// 高级色差：基于径向畸变模型（Barrel/Pincushion）。
        /// 使用 Brown-Conrady 畸变模型模拟真实镜头色差。
        /// </summary>
        public static Vector3 ApplyLensModel(
            Func<Vector2, Vector3> getPixel, Vector2 uv,
            float k1, float k2, float chromaticScale)
        {
            Vector2 center = new Vector2(0.5f, 0.5f);
            Vector2 d = (uv - center) * 2.0f; // [-1, 1]
            float r2 = d.X * d.X + d.Y * d.Y;

            // 径向畸变: r_distorted = r * (1 + k1*r^2 + k2*r^4)
            float distortion = 1.0f + k1 * r2 + k2 * r2 * r2;

            // RGB 使用略微不同的畸变参数（色差的本质）
            Vector2 uvR = center + d * (distortion + chromaticScale * 0.5f) * 0.5f;
            Vector2 uvG = center + d * distortion * 0.5f;
            Vector2 uvB = center + d * (distortion - chromaticScale * 0.5f) * 0.5f;

            uvR = Vector2.Clamp(uvR, Vector2.Zero, Vector2.One);
            uvG = Vector2.Clamp(uvG, Vector2.Zero, Vector2.One);
            uvB = Vector2.Clamp(uvB, Vector2.Zero, Vector2.One);

            return new Vector3(getPixel(uvR).X, getPixel(uvG).Y, getPixel(uvB).Z);
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // 暗角 (Vignette)
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>暗角设置。</summary>
    [Serializable]
    public struct VignetteSettings
    {
        /// <summary>是否启用。</summary>
        public bool Enabled;

        /// <summary>暗角强度 [0, 1]。</summary>
        public float Intensity;

        /// <summary>过渡平滑度 [0, 1]。值越大暗角越柔和。</summary>
        public float Smoothness;

        /// <summary>暗角圆度 [0, 1]。1=圆形，0=方形。</summary>
        public float Roundness;

        /// <summary>暗角颜色（通常黑色）。</summary>
        public Vector3 Color;

        /// <summary>默认设置。</summary>
        public static VignetteSettings Default => new()
        {
            Enabled = true, Intensity = 0.4f, Smoothness = 0.5f,
            Roundness = 1.0f, Color = Vector3.Zero
        };

        public static VignetteSettings Disabled => new() { Enabled = false };
    }

    /// <summary>
    /// 暗角效果：画面边缘暗化。
    /// 算法：
    ///   dist = distance(uv, center)
    ///   vignette = smoothstep(0, 1 - smoothness, dist * roundness)
    ///   final = lerp(color, vignetteColor, vignette * intensity)
    /// </summary>
    public static class VignetteEffect
    {
        /// <summary>CPU 侧暗角计算。</summary>
        public static Vector3 Apply(Vector3 color, Vector2 uv, float intensity,
            float smoothness, float roundness, Vector3 vignetteColor)
        {
            Vector2 center = new Vector2(0.5f, 0.5f);

            // 圆度调整：roundness=1 完全圆形，roundness=0 完全方形
            Vector2 d = Vector2.Abs(uv - center) * 2.0f;
            float maxDist = Math.Max(d.X, d.Y) * (1.0f - roundness)
                          + d.Length() * roundness;

            // smoothstep 过渡
            float edge = Math.Clamp(maxDist, 0.0f, 1.0f);
            float innerEdge = 1.0f - smoothness;
            float vignette;

            if (edge <= innerEdge)
                vignette = 0.0f;
            else if (edge >= 1.0f)
                vignette = 1.0f;
            else
            {
                float t = (edge - innerEdge) / (1.0f - innerEdge);
                vignette = t * t * (3.0f - 2.0f * t); // smoothstep
            }

            vignette *= intensity;

            // 混合暗角颜色
            return color * (1.0f - vignette) + vignetteColor * vignette;
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // 胶片颗粒 (Film Grain)
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>颗粒类型。</summary>
    public enum FilmGrainType
    {
        /// <summary>细小颗粒（模拟ISO 100胶片）。</summary>
        Fine = 0,
        /// <summary>中等颗粒（模拟ISO 400胶片）。</summary>
        Medium = 1,
        /// <summary>粗大颗粒（模拟ISO 1600胶片）。</summary>
        Coarse = 2
    }

    /// <summary>胶片颗粒设置。</summary>
    [Serializable]
    public struct FilmGrainSettings
    {
        /// <summary>是否启用。</summary>
        public bool Enabled;

        /// <summary>颗粒强度 [0, 1]。</summary>
        public float Intensity;

        /// <summary>颗粒大小。影响噪声的空间频率。</summary>
        public float Size;

        /// <summary>响应曲线 [0, 1]。控制颗粒在亮/暗区域的表现。</summary>
        public float Response;

        /// <summary>颗粒类型。</summary>
        public FilmGrainType Type;

        /// <summary>默认设置。</summary>
        public static FilmGrainSettings Default => new()
        {
            Enabled = true, Intensity = 0.08f, Size = 1.5f,
            Response = 0.5f, Type = FilmGrainType.Medium
        };

        public static FilmGrainSettings Disabled => new() { Enabled = false };
    }

    /// <summary>
    /// 胶片颗粒效果：基于时间的伪随机噪声。
    /// 算法：
    ///   grain = rand(uv + time) * 2 - 1
    ///   final = color + grain * intensity * (1 - color * response)
    /// 速度优化：基于 UV 和时间戳的哈希函数生成伪随机数。
    /// 支持 TAA 抖动整合。
    /// </summary>
    public static class FilmGrainEffect
    {
        /// <summary>
        /// 基于整数坐标和种子的快速哈希函数。
        /// 用于生成可重复的伪随机噪声。
        /// </summary>
        public static float Hash(int x, int y, int seed)
        {
            // 大质数哈希
            uint h = (uint)(x * 374761393 + y * 668265263 + seed * 1274126177);
            h = (h ^ (h >> 13)) * 1103515245;
            h = h ^ (h >> 16);
            return (h & 0x7FFFFFFF) / (float)0x7FFFFFFF; // [0, 1]
        }

        /// <summary>
        /// 基于浮点 UV 和时间的噪声生成。
        /// </summary>
        public static float Noise(Vector2 uv, float time)
        {
            // 量化到颗粒大小
            int ix = (int)MathF.Floor(uv.X * 64.0f);
            int iy = (int)MathF.Floor(uv.Y * 64.0f);
            int seed = (int)(time * 100.0f);

            float n = Hash(ix, iy, seed);
            return n * 2.0f - 1.0f; // [-1, 1]
        }

        /// <summary>
        /// CPU 侧胶片颗粒应用。
        /// </summary>
        public static Vector3 Apply(Vector3 color, Vector2 uv, float time,
            float intensity, float size, float response)
        {
            // 缩放 UV 以控制颗粒大小
            Vector2 scaledUV = uv * (size * 32.0f);

            float grain = Noise(scaledUV, time);

            // 颗粒响应：暗区域颗粒更明显，亮区域被压缩
            // (1 - color * response) 确保暗区域有更强噪声
            float luminance = color.X * 0.2126f + color.Y * 0.7152f + color.Z * 0.0722f;
            float grainResponse = 1.0f - luminance * response;

            Vector3 result = color + new Vector3(grain * intensity * grainResponse);

            // Clamp 防止过曝
            return Vector3.Max(result, Vector3.Zero);
        }

        /// <summary>
        /// 生成 TAA 兼容的抖动噪声。
        /// 与 TAA 的 subpixel jitter 整合，避免与 TAA 去噪冲突。
        /// </summary>
        public static Vector2 TAAJitter(Vector2 uv, int frameIndex, float intensity)
        {
            // 使用 halton 序列生成低差异噪声
            float x = Halton(2, frameIndex) * 2.0f - 1.0f;
            float y = Halton(3, frameIndex) * 2.0f - 1.0f;

            return new Vector2(x, y) * intensity;
        }

        /// <summary>Halton 序列（低差异序列）。</summary>
        private static float Halton(int base_, int index)
        {
            float result = 0.0f;
            float fraction = 1.0f;
            int i = index;
            while (i > 0)
            {
                fraction /= base_;
                result += (i % base_) * fraction;
                i /= base_;
            }
            return result;
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // 色调映射设置 (Tone Mapping)
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>色调映射设置。</summary>
    [Serializable]
    public struct ToneMappingSettings
    {
        /// <summary>是否启用。</summary>
        public bool Enabled;
        /// <summary>曝光值 (EV)。</summary>
        public float Exposure;
        /// <summary>对比度。</summary>
        public float Contrast;
        /// <summary>饱和度。</summary>
        public float Saturation;

        /// <summary>默认设置。</summary>
        public static ToneMappingSettings Default => new()
        {
            Enabled = true, Exposure = 0.0f, Contrast = 1.0f, Saturation = 1.0f
        };
    }

    // ────────────────────────────────────────────────────────────────────────
    // God Ray 设置
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>上帝光线设置。</summary>
    [Serializable]
    public struct GodRaySettings
    {
        /// <summary>是否启用。</summary>
        public bool Enabled;
        /// <summary>光线强度。</summary>
        public float Intensity;
        /// <summary>光线衰减率。</summary>
        public float Decay;
        /// <summary>光线采样数。</summary>
        public int Samples;
        /// <summary>光线权重。</summary>
        public float Weight;

        /// <summary>默认设置。</summary>
        public static GodRaySettings Default => new()
        {
            Enabled = true, Intensity = 0.8f, Decay = 0.95f,
            Samples = 60, Weight = 0.4f
        };

        public static GodRaySettings Disabled => new() { Enabled = false };
    }

    // ────────────────────────────────────────────────────────────────────────
    // FXAA 设置
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>FXAA 设置。</summary>
    [Serializable]
    public struct FXAASettings
    {
        /// <summary>是否启用。</summary>
        public bool Enabled;
        /// <summary>质量子像素去锯齿强度。</summary>
        public float QualitySubpix;
        /// <summary>边缘检测阈值。</summary>
        public float EdgeThreshold;
        /// <summary>最小边缘阈值。</summary>
        public float EdgeThresholdMin;

        /// <summary>默认设置。</summary>
        public static FXAASettings Default => new()
        {
            Enabled = true, QualitySubpix = 0.75f,
            EdgeThreshold = 0.166f, EdgeThresholdMin = 0.0833f
        };
    }

    // ────────────────────────────────────────────────────────────────────────
    // 颜色分级 (Color Grading)
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 颜色分级设置。
    /// 支持 ASC CDL（美国电影摄影师协会色彩决策列表）色彩校正、
    /// 3D LUT 查找表、以及 Shadows/Midtones/Highlights 分离调整。
    /// </summary>
    [Serializable]
    public struct ColorGradingSettings
    {
        /// <summary>亮度调整 [-2, 2]。</summary>
        public float Brightness;
        /// <summary>对比度 [0, 3]。</summary>
        public float Contrast;
        /// <summary>饱和度 [0, 3]。</summary>
        public float Saturation;
        /// <summary>色相偏移 [-180, 180] 度。</summary>
        public float HueShift;
        /// <summary>色温 [-1 暖, +1 冷]。</summary>
        public float Temperature;
        /// <summary>色调偏移（绿-品红）。</summary>
        public float Tint;

        // ── ASC CDL: Lift / Gamma / Gain ──
        /// <summary>Lift（阴影提升）：影响暗部区域。</summary>
        public Vector3 Lift;
        /// <summary>Gamma（伽马曲线）：影响中间调。</summary>
        public Vector3 Gamma;
        /// <summary>Gain（增益）：影响高光区域。</summary>
        public Vector3 Gain;

        // ── 分离调整 ──
        /// <summary>阴影调整（Luminance 加权分离）。</summary>
        public Vector3 Shadows;
        /// <summary>中间调调整。</summary>
        public Vector3 Midtones;
        /// <summary>高光调整。</summary>
        public Vector3 Highlights;

        // ── LUT ──
        /// <summary>3D LUT 纹理（32x32x32）。</summary>
        public RHITexture? LutTexture;
        /// <summary>LUT 强度 [0, 1]。</summary>
        public float LutIntensity;

        // ── 色调映射 / 曝光 / 泛音 ──
        /// <summary>色调映射算子。</summary>
        public TonemapOperator Tonemap;
        /// <summary>曝光 [0, 8]，默认 1。</summary>
        public float Exposure;
        /// <summary>伽马值（曲线中点控制）。</summary>
        public float GammaValue;
        /// <summary>泛音（Vibrance）强度。</summary>
        public float Vibrance;
        /// <summary>分离调整过渡区间（阴影起止 / 高光起止）。</summary>
        public float ShadowStart;
        public float ShadowEnd;
        public float HighlightStart;
        public float HighlightEnd;
        /// <summary>暗角平滑度。</summary>
        public float VignetteSmoothness;
        /// <summary>该效果是否启用。</summary>
        public bool Enabled;

        /// <summary>默认设置。</summary>
        public static ColorGradingSettings Default => new()
        {
            Brightness = 0.0f,
            Contrast = 1.0f,
            Saturation = 1.0f,
            HueShift = 0.0f,
            Temperature = 0.0f,
            Tint = 0.0f,
            Lift = Vector3.Zero,
            Gamma = new Vector3(1, 1, 1),
            Gain = new Vector3(1, 1, 1),
            Shadows = new Vector3(1, 1, 1),
            Midtones = new Vector3(1, 1, 1),
            Highlights = new Vector3(1, 1, 1),
            LutTexture = null,
            LutIntensity = 1.0f,
            Tonemap = TonemapOperator.ACES,
            Exposure = 1.0f,
            GammaValue = 1.0f,
            Vibrance = 0.0f,
            ShadowStart = 0.0f,
            ShadowEnd = 0.1f,
            HighlightStart = 0.9f,
            HighlightEnd = 1.0f,
            VignetteSmoothness = 0.15f,
            Enabled = true
        };
    }

    /// <summary>
    /// 颜色分级效果。
    /// 包含 CDL 色彩校正、色温/色调调整、饱和度/对比度、
    /// Shadows/Midtones/Highlights 分离调整、LUT 采样。
    /// </summary>
    public static class ColorGradingEffect
    {
        /// <summary>
        /// ASC CDL 色彩校正：out = (in * gain + lift) ^ gamma
        /// 美国电影摄影师协会标准色彩决策列表。
        /// </summary>
        public static Vector3 ApplyCDL(Vector3 color, Vector3 slope, Vector3 offset, Vector3 power)
        {
            // slope = Gain, offset = Lift, power = Gamma
            Vector3 result = color * slope + offset;

            // 伽马校正（Gamma 通道，保护零值）
            result.X = MathF.Pow(Math.Max(0.0f, result.X), 1.0f / Math.Max(power.X, 0.001f));
            result.Y = MathF.Pow(Math.Max(0.0f, result.Y), 1.0f / Math.Max(power.Y, 0.001f));
            result.Z = MathF.Pow(Math.Max(0.0f, result.Z), 1.0f / Math.Max(power.Z, 0.001f));

            return result;
        }

        /// <summary>
        /// 色温调整。
        /// 使用色温到 RGB 的近似映射：
        ///   暖色(负温度)：增加红色，减少蓝色
        ///   冷色(正温度)：增加蓝色，减少红色
        /// </summary>
        public static Vector3 ApplyWhiteBalance(Vector3 color, float temperature, float tint)
        {
            // 简化的色温到 RGB 映射
            // temperature: -1(暖) ~ +1(冷)
            float r = 1.0f + temperature * 0.1f + tint * 0.05f;
            float g = 1.0f - Math.Abs(tint) * 0.05f;
            float b = 1.0f - temperature * 0.1f + tint * 0.05f;

            return new Vector3(
                Math.Max(0.0f, color.X * r),
                Math.Max(0.0f, color.Y * g),
                Math.Max(0.0f, color.Z * b)
            );
        }

        /// <summary>
        /// 色相旋转（Hue Shift）。
        /// 将 RGB 颜色旋转到 HSL 空间，偏移色相后再转回。
        /// </summary>
        public static Vector3 ApplyHueShift(Vector3 color, float hueShiftDegrees)
        {
            float hue = hueShiftDegrees * (MathF.PI / 180.0f);
            float cosH = MathF.Cos(hue);
            float sinH = MathF.Sin(hue);

            // Rodriguez 旋转公式（简化版）
            Vector3 result = new Vector3(
                color.X * (cosH + (1.0f - cosH) / 3.0f)
                + color.Y * ((1.0f - cosH) / 3.0f - MathF.Sqrt(1.0f / 3.0f) * sinH)
                + color.Z * ((1.0f - cosH) / 3.0f + MathF.Sqrt(1.0f / 3.0f) * sinH),

                color.X * ((1.0f - cosH) / 3.0f + MathF.Sqrt(1.0f / 3.0f) * sinH)
                + color.Y * (cosH + (1.0f - cosH) / 3.0f)
                + color.Z * ((1.0f - cosH) / 3.0f - MathF.Sqrt(1.0f / 3.0f) * sinH),

                color.X * ((1.0f - cosH) / 3.0f - MathF.Sqrt(1.0f / 3.0f) * sinH)
                + color.Y * ((1.0f - cosH) / 3.0f + MathF.Sqrt(1.0f / 3.0f) * sinH)
                + color.Z * (cosH + (1.0f - cosH) / 3.0f)
            );

            return Vector3.Max(result, Vector3.Zero);
        }

        /// <summary>
        /// 饱和度调整。
        /// 计算感知亮度后按饱和度系数在灰度和原色之间插值。
        /// </summary>
        public static Vector3 ApplySaturation(Vector3 color, float saturation)
        {
            float luminance = color.X * 0.2126f + color.Y * 0.7152f + color.Z * 0.0722f;
            Vector3 gray = new Vector3(luminance, luminance, luminance);
            return Vector3.Lerp(gray, color, saturation);
        }

        /// <summary>
        /// Shadows / Midtones / Highlights 分离调整。
        /// 使用 Luminance 加权将像素分离到三个区间：
        ///   - Shadows: luminance < 0.33
        ///   - Midtones: 0.33 <= luminance < 0.66
        ///   - Highlights: luminance >= 0.66
        /// 每个区间独立调整颜色，使用 smoothstep 过渡避免色带。
        /// </summary>
        public static Vector3 ApplyTonalRegions(Vector3 color,
            Vector3 shadows, Vector3 midtones, Vector3 highlights)
        {
            float luminance = color.X * 0.2126f + color.Y * 0.7152f + color.Z * 0.0722f;

            // Shadows 权重：亮度越低权重越大
            float shadowWeight = 1.0f - Smoothstep(0.0f, 0.5f, luminance);
            // Highlights 权重：亮度越高权重越大
            float highlightWeight = Smoothstep(0.5f, 1.0f, luminance);
            // Midtones 权重：中间区域
            float midtoneWeight = 1.0f - shadowWeight - highlightWeight;

            return color * (shadows * shadowWeight + midtones * midtoneWeight + highlights * highlightWeight);
        }

        /// <summary>smoothstep 辅助函数。</summary>
        private static float Smoothstep(float edge0, float edge1, float x)
        {
            float t = Math.Clamp((x - edge0) / (edge1 - edge0), 0.0f, 1.0f);
            return t * t * (3.0f - 2.0f * t);
        }

        /// <summary>
        /// 完整的颜色分级管线。
        /// 依次执行：CDL → 白平衡 → 色相旋转 → 饱和度 → 对比度/亮度 → 分离调整。
        /// </summary>
        public static Vector3 ApplyFullGrading(Vector3 color, in ColorGradingSettings settings)
        {
            // 1. ASC CDL
            color = ApplyCDL(color, settings.Gain, settings.Lift, settings.Gamma);

            // 2. 白平衡（色温+色调）
            color = ApplyWhiteBalance(color, settings.Temperature, settings.Tint);

            // 3. 色相旋转
            if (Math.Abs(settings.HueShift) > 0.01f)
                color = ApplyHueShift(color, settings.HueShift);

            // 4. 饱和度
            if (Math.Abs(settings.Saturation - 1.0f) > 0.001f)
                color = ApplySaturation(color, settings.Saturation);

            // 5. 对比度和亮度
            color *= settings.Contrast;
            color += new Vector3(settings.Brightness);

            // 6. Shadows/Midtones/Highlights 分离调整
            color = ApplyTonalRegions(color, settings.Shadows, settings.Midtones, settings.Highlights);

            return color;
        }

        /// <summary>
        /// 3D LUT 采样。
        /// 将 [0,1] 颜色映射到 32x32x32 3D纹理的 UVW 坐标。
        /// 算法：
        ///   uv.x = (blue * 31 + 0.5) / 32 / 8 + slice_x
        ///   uv.y = green * (31/32) + 0.5/32
        ///   slice = floor(red * 31)
        /// </summary>
        public static Vector2 ComputeLutUV(Vector3 color, int lutSize = 32)
        {
            float sliceCount = (float)lutSize;
            float sliceSize = sliceCount; // 2D 打包 LUT 的切片尺寸 = lutSize
            float slice = Math.Clamp(color.Z, 0.0f, 1.0f) * (sliceCount - 1.0f);
            float sliceIdx = MathF.Floor(slice);
            float sliceFrac = slice - sliceIdx;

            float u = (sliceIdx * sliceCount + color.X * (sliceCount - 1.0f) + 0.5f) / (sliceCount * sliceSize);
            float v = (color.Y * (sliceCount - 1.0f) + 0.5f) / sliceSize;

            return new Vector2(u, v);
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // 镜头特效组合管线 — 合并色差+暗角+颗粒+颜色分级为单Pass
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 镜头特效组合管线：将色差、暗角、胶片颗粒合并为一个 Compute Shader Pass 执行。
    /// 颜色分级（含LUT）通常在独立的 Tonemap Pass 中执行。
    /// </summary>
    public sealed class LensEffectPipeline : IDisposable
    {
        private readonly RHIDevice _device;

        // ── 管线 ──
        private RHIPipelineState? _lensPipeline;
        private RHIPipelineState? _colorGradingPipeline;

        private int _width, _height;
        private bool _initialized;

        /// <summary>是否已初始化。</summary>
        public bool IsInitialized => _initialized;

        public LensEffectPipeline(RHIDevice device)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));
        }

        /// <summary>初始化。</summary>
        public void Initialize(int width, int height)
        {
            _width = width;
            _height = height;
            CreatePipelines();
            _initialized = true;
        }

        private void CreatePipelines()
        {
            _lensPipeline = _device.CreateComputePipelineState(new RHIComputePipelineDesc
            {
                Name = "LensEffects",
                CS = ShaderManager.Instance.GetBytecode("PostProcess/LensEffects",
                    "LensEffectsCS", RHIShaderStage.Compute)
            });
            _colorGradingPipeline = _device.CreateComputePipelineState(new RHIComputePipelineDesc
            {
                Name = "ColorGrading",
                CS = ShaderManager.Instance.GetBytecode("PostProcess/ColorGrading",
                    "ColorGradingCS", RHIShaderStage.Compute)
            });
        }

        /// <summary>
        /// 执行镜头特效组合 Pass（色差+暗角+颗粒）。
        /// </summary>
        public void ExecuteLensEffects(RHICommandList cmdList, RHITexture source, RHITexture output,
            in ChromaticAberrationSettings ca, in VignetteSettings vig,
            in FilmGrainSettings grain, float time)
        {
            if (!_initialized || _lensPipeline == null) return;

            cmdList.SetPipelineState(_lensPipeline);
            cmdList.TransitionTo(source, RHIResourceState.NonPixelShaderResource);
            cmdList.TransitionTo(output, RHIResourceState.UnorderedAccess);

            // 打包所有参数到一个 CB（使用已有的 PostProcessLensCB）
            cmdList.Dispatch((_width + 7) / 8, (_height + 7) / 8, 1);
            cmdList.TransitionTo(output, RHIResourceState.NonPixelShaderResource);
        }

        /// <summary>
        /// 执行颜色分级 Pass。
        /// </summary>
        public void ExecuteColorGrading(RHICommandList cmdList, RHITexture source, RHITexture output,
            in ColorGradingSettings grading)
        {
            if (!_initialized || _colorGradingPipeline == null) return;

            cmdList.SetPipelineState(_colorGradingPipeline);
            cmdList.TransitionTo(source, RHIResourceState.NonPixelShaderResource);
            cmdList.TransitionTo(output, RHIResourceState.UnorderedAccess);

            cmdList.Dispatch((_width + 7) / 8, (_height + 7) / 8, 1);
            cmdList.TransitionTo(output, RHIResourceState.NonPixelShaderResource);
        }

        /// <summary>释放资源。</summary>
        public void Dispose()
        {
            _lensPipeline?.Dispose();
            _colorGradingPipeline?.Dispose();
            _initialized = false;
            GC.SuppressFinalize(this);
        }
    }
}
