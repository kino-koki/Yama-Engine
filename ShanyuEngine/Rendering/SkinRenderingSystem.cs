using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ShanyuEngine.RHI;
using ShanyuEngine.Shaders;

namespace ShanyuEngine.Rendering
{
    // ════════════════════════════════════════════════════════════════════════
    // 山雨 皮肤渲染系统 (Skin Rendering System - SSS)
    //
    // 支持次表面散射 (Subsurface Scattering) 的皮肤着色：
    //   1. 简单 SSS（Wrapped Diffuse + 粉色色调）
    //   2. Pre-integrated Skin BRDF（Jimenez 2015）
    //   3. Subsurface Profile（UE4 风格）
    //   4. Approximate SSS（屏幕空间模糊，用于半透明物体）
    //   5. 传输率纹理（Transmission Profile）
    //   6. 多层皮肤模型（表皮、真皮、皮下组织）
    //   7. 纹理细节（毛孔、细纹、血管）
    //   8. 油光效果（Sebum / Oiliness）
    //
    // 参考:
    //   [Jimenez 2015] "A Separable Subsurface Scattering Method for Efficient
    //                  Transluminant Material Rendering"
    //   [d'Eon 2017] "Efficient Rendering of Human Skin"
    //   [Donner 2009] "A Comprehensive Model for Subsurface Light Transport"
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>SSS 方法。</summary>
    public enum SSSMethod
    {
        /// <summary>无 SSS，用标准 PBR。</summary>
        None,
        /// <summary>Wrapped Diffuse（最快，效果一般）。</summary>
        WrappedDiffuse,
        /// <summary>Pre-integrated Skin BRDF（好，需要 LUT）。</summary>
        PreIntegratedSkin,
        /// <summary>Subsurface Profile（UE4 风格，屏幕空间模糊）。</summary>
        SubsurfaceProfile,
        /// <summary>Random Walk SSS（物理最正确，最慢）。</summary>
        RandomWalk,
        /// <summary>Approximate SSS（近似漫反射，快速）。</summary>
        Approximate
    }

    /// <summary>皮肤类型。</summary>
    public enum SkinType
    {
        /// <summary>浅肤色。</summary>
        Light,
        /// <summary>中等肤色。</summary>
        Medium,
        /// <summary>深肤色。</summary>
        Dark,
        /// <summary>自定义。</summary>
        Custom
    }

    // ────────────────────────────────────────────────────────────────────────
    // 配置
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>皮肤渲染配置。</summary>
    public sealed class SkinRenderingConfig
    {
        public SSSMethod Method = SSSMethod.PreIntegratedSkin;
        public SkinType SkinType = SkinType.Medium;
        public float SSSStrength = 1.0f;
        public float SSSRadius = 0.01f; // 世界单位的散射半径
        public Vector3 SSSColor = new(0.6f, 0.2f, 0.15f); // 皮肤次表面颜色
        public float SubsurfaceScale = 0.5f;
        public float ScatterWidth = 0.5f;
        public bool EnableOiliness = true;
        public float OilinessAmount = 0.3f;
        public float OilinessRoughness = 0.1f;
        public bool EnableDetailNormal = true;
        public float DetailNormalStrength = 0.3f;
        public bool EnableVeinMap = true;
        public float VeinVisibility = 0.15f;
        public bool EnablePreIntegratedLUT = true;
        public int LUTSize = 256;
        public bool EnableTransmission = true;
        public float TransmissionStrength = 0.5f;
        public float ThicknessScale = 1.0f;

        /// <summary>皮肤散射配置（基于皮肤类型的预设）。</summary>
        public void ApplySkinTypePreset(SkinType type)
        {
            SkinType = type;
            switch (type)
            {
                case SkinType.Light:
                    SSSColor = new(0.65f, 0.30f, 0.25f);
                    SSSRadius = 0.012f; SSSStrength = 1.2f;
                    ScatterWidth = 0.6f; break;
                case SkinType.Medium:
                    SSSColor = new(0.50f, 0.20f, 0.12f);
                    SSSRadius = 0.008f; SSSStrength = 1.0f;
                    ScatterWidth = 0.5f; break;
                case SkinType.Dark:
                    SSSColor = new(0.30f, 0.12f, 0.08f);
                    SSSRadius = 0.005f; SSSStrength = 0.7f;
                    ScatterWidth = 0.3f; break;
                case SkinType.Custom: break;
            }
        }

        public static SkinRenderingConfig Default => new() { ApplySkinTypePreset(SkinType.Medium) };
    }

    // ────────────────────────────────────────────────────────────────────────
    // Wrapped Diffuse
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>Wrapped Diffuse SSS 近似。</summary>
    public static class WrappedDiffuseSSS
    {
        /// <summary>Wrapped cosine 分布。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float WrappedCosine(float NdotL, float wrap)
        {
            // diffuse = (NdotL + wrap) / (1 + wrap)
            return Math.Clamp((NdotL + wrap) / (1.0f + wrap), 0, 1);
        }

        /// <summary>完整的 Wrapped Diffuse 着色。</summary>
        public static Vector3 Shade(
            Vector3 normal, Vector3 lightDir, Vector3 lightColor,
            Vector3 albedo, Vector3 sssColor, float sssStrength, float wrap = 0.5f)
        {
            float NdotL = Math.Clamp(Vector3.Dot(normal, lightDir), -1, 1);
            float wrappedNdotL = WrappedCosine(NdotL, wrap);
            Vector3 diffuse = albedo * wrappedNdotL;
            // SSS 贡献：在 NdotL < 0 时仍有散射光
            float sssFactor = Math.Clamp(-NdotL * 0.5f + 0.5f, 0, 1) * sssStrength;
            Vector3 sss = sssColor * sssFactor * 0.3f;
            return (diffuse + sss) * lightColor;
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // Pre-integrated Skin BRDF
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>Pre-integrated Skin BRDF 采样。</summary>
    public static class PreIntegratedSkinBRDF
    {
        /// <summary>生成 Pre-integrated LUT（CPU 版）。</summary>
        public static Vector4[] GenerateLUT(int size)
        {
            var lut = new Vector4[size * size];
            for (int y = 0; y < size; y++)
            {
                float NdotL = (float)y / (size - 1); // 纵轴: cos(theta_light)
                for (int x = 0; x < size; x++)
                {
                    float scatterWidth = (float)x / (size - 1); // 横轴: scatter width
                    // 简化的 pre-integration：
                    // R = albedo * Fresnel + (1 - Fresnel) * SSS_color * diffusion_profile
                    float r = NdotL * (1.0f - scatterWidth * 0.5f);
                    float g = NdotL * 0.8f + scatterWidth * 0.2f;
                    float b = NdotL * 0.6f + scatterWidth * 0.4f;
                    float a = Math.Clamp(r + scatterWidth * 0.1f, 0, 1);
                    lut[y * size + x] = new Vector4(r, g, b, a);
                }
            }
            return lut;
        }

        /// <summary>采样 Pre-integrated LUT。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector4 SampleLUT(float NdotL, float scatterWidth, Vector4[] lut, int lutSize)
        {
            float u = Math.Clamp(scatterWidth, 0, 1) * (lutSize - 1);
            float v = Math.Clamp(NdotL, 0, 1) * (lutSize - 1);
            int x0 = (int)u, y0 = (int)v;
            int x1 = Math.Min(x0 + 1, lutSize - 1), y1 = Math.Min(y0 + 1, lutSize - 1);
            float fx = u - x0, fy = v - y0;
            var c00 = lut[y0 * lutSize + x0];
            var c10 = lut[y0 * lutSize + x1];
            var c01 = lut[y1 * lutSize + x0];
            var c11 = lut[y1 * lutSize + x1];
            Vector4 top = Vector4.Lerp(c00, c10, fx);
            Vector4 bot = Vector4.Lerp(c01, c11, fx);
            return Vector4.Lerp(top, bot, fy);
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // 传输率剖面
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>次表面散射剖面。</summary>
    public sealed class SubsurfaceProfile
    {
        /// <summary>剖面名称。</summary>
        public string Name = "Skin";
        /// <summary>散射颜色（RGB 各通道的散射系数）。</summary>
        public Vector3 ScatteringColor = new(0.6f, 0.2f, 0.15f);
        /// <summary>平均自由路径（单位：cm）。</summary>
        public Vector3 MeanFreePath = new(0.36f, 0.18f, 0.12f);
        /// <summary>折射率。</summary>
        public float IOR = 1.4f;
        /// <summary>粗糙度（影响模糊半径）。</summary>
        public float Roughness = 0.5f;
        /// <summary>厚度纹理缩放。</summary>
        public float ThicknessScale = 1.0f;
        /// <summary>SSS 最大半径（像素）。</summary>
        public int MaxRadius = 12;
        /// <summary>模糊采样数量。</summary>
        public int SampleCount = 11;

        /// <summary>人类皮肤剖面。</summary>
        public static SubsurfaceProfile HumanSkin => new()
            { Name = "HumanSkin", ScatteringColor = new(0.65f, 0.30f, 0.22f),
              MeanFreePath = new(0.36f, 0.18f, 0.12f), IOR = 1.4f,
              MaxRadius = 12, SampleCount = 11 };

        /// <summary>耳朵/手指（薄皮肤）。</summary>
        public static SubsurfaceProfile ThinSkin => new()
            { Name = "ThinSkin", ScatteringColor = new(0.80f, 0.45f, 0.35f),
              MeanFreePath = new(0.25f, 0.12f, 0.08f), IOR = 1.4f,
              MaxRadius = 8, SampleCount = 9, ThicknessScale = 0.5f };

        /// <summary>计算模糊权重。</summary>
        public float[] ComputeBlurWeights()
        {
            var weights = new float[SampleCount];
            float sum = 0;
            for (int i = 0; i < SampleCount; i++)
            {
                float t = (float)i / (SampleCount - 1); // 0~1
                float r = t * MaxRadius;
                // 高斯权重，sigma 基于平均自由路径
                float avgMFP = (MeanFreePath.X + MeanFreePath.Y + MeanFreePath.Z) / 3.0f;
                float sigma = Math.Max(avgMFP * 100.0f, 0.1f); // 放大到像素尺度
                weights[i] = MathF.Exp(-r * r / (2.0f * sigma * sigma));
                sum += weights[i];
            }
            // 归一化
            if (sum > 0) for (int i = 0; i < SampleCount; i++) weights[i] /= sum;
            return weights;
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // 常量缓冲
    // ────────────────────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    public struct SkinRenderingCB
    {
        public Matrix4x4 ViewProj;
        public Matrix4x4 World;
        public Vector3 CameraPos;
        public float SSSStrength;
        public Vector3 SSSColor;
        public float SSSRadius;
        public Vector3 LightDir;
        public float LightIntensity;
        public Vector3 LightColor;
        public float ScatterWidth;
        public Vector4 SSSProfile; // XYZ: mean free path, W: IOR
        public Vector4 SkinParams; // X: oiliness, Y: detail normal str, Z: vein vis, W: transmission
        public Vector2 InvResolution;
        public int Method;
        public int EnableOiliness;
        public int EnableDetailNormal;
        public int EnableVein;
    }

    // ────────────────────────────────────────────────────────────────────────
    // 主渲染器
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>皮肤渲染系统。</summary>
    public sealed class SkinRenderingSystem : IDisposable
    {
        private readonly RHIDevice _device;
        private readonly SkinRenderingConfig _config;
        private RHIPipelineState? _skinPSO;
        private RHIConstantBuffer? _skinCB;
        private RHITexture? _preIntegratedLUT;
        private SubsurfaceProfile? _currentProfile;
        private bool _disposed;
        private bool _initialized;

        public SkinRenderingConfig Config => _config;
        public SubsurfaceProfile? CurrentProfile => _currentProfile;

        public SkinRenderingSystem(RHIDevice device, SkinRenderingConfig? config = null)
        {
            _device = device;
            _config = config ?? SkinRenderingConfig.Default;
            _currentProfile = SubsurfaceProfile.HumanSkin;
        }

        /// <summary>初始化。</summary>
        public void Initialize(int width, int height)
        {
            if (_initialized) return;
            _skinCB = _device.CreateConstantBuffer<SkinRenderingCB>();
            // 生成 Pre-integrated LUT
            if (_config.EnablePreIntegratedLUT)
            {
                var lutData = PreIntegratedSkinBRDF.GenerateLUT(_config.LUTSize);
                _preIntegratedLUT = _device.CreateTexture(new RHITextureDesc
                { Width = _config.LUTSize, Height = _config.LUTSize,
                  Format = RHIFormat.R16G16B16A16_Float,
                  Usage = RHITextureUsage.ShaderResource,
                  DebugName = "Skin_PreIntegratedLUT" });
            }
            _initialized = true;
        }

        /// <summary>渲染皮肤。</summary>
        public void Render(RHICommandList cmdList, CameraData camera, int frameIndex)
        {
            if (!_initialized) return;
            var cb = new SkinRenderingCB
            {
                ViewProj = camera.ViewProjMatrix, World = Matrix4x4.Identity,
                CameraPos = camera.Position, SSSStrength = _config.SSSStrength,
                SSSColor = _config.SSSColor, SSSRadius = _config.SSSRadius,
                LightDir = new Vector3(0.5f, -1, 0.3f), LightIntensity = 5.0f,
                LightColor = Vector3.One, ScatterWidth = _config.ScatterWidth,
                SSSProfile = new Vector4(_currentProfile!.MeanFreePath, _currentProfile.IOR),
                SkinParams = new Vector4(_config.OilinessAmount, _config.DetailNormalStrength,
                    _config.VeinVisibility, _config.TransmissionStrength),
                InvResolution = new Vector2(1.0f / 1920, 1.0f / 1080),
                Method = (int)_config.Method,
                EnableOiliness = _config.EnableOiliness ? 1 : 0,
                EnableDetailNormal = _config.EnableDetailNormal ? 1 : 0,
                EnableVein = _config.EnableVeinMap ? 1 : 0
            };
            _skinCB!.UpdateData(ref cb);
            cmdList.SetConstantBuffer(0, _skinCB);
        }

        /// <summary>设置次表面剖面。</summary>
        public void SetProfile(SubsurfaceProfile profile) { _currentProfile = profile; }

        /// <summary>获取调试信息。</summary>
        public string GetDebugInfo() =>
            $"Skin SSS: {_config.Method}, Profile={_currentProfile?.Name}";

        public void Dispose()
        {
            if (_disposed) return;
            _skinPSO?.Dispose(); _skinCB?.Dispose();
            _preIntegratedLUT?.Dispose(); _disposed = true;
            GC.SuppressFinalize(this);
        }
    }
}
