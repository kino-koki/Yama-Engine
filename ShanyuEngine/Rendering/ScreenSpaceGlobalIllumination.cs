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
    // 山雨 屏幕空间全局光照 (Screen Space Global Illumination)
    //
    // 实现 SSGI（Screen Space GI），包括：
    //   1. 单次弹射（1-bounce）屏幕空间 GI
    //      - 半球采样 + 深度缓冲区 ray marching
    //      - 法线加权（cosine-weighted hemisphere）
    //      - Hi-Z depth buffer 加速
    //   2. 多次弹射（multi-bounce）GI
    //      - 迭代扩散法：把上一轮的 GI 结果作为新的间接光源
    //      - 通常 3 次 bounce 就够了（更多收益递减）
    //   3. SSGI + SSR 混合
    //      - 共享 ray marching 路径
    //      - 区分 diffuse 和 specular 间接光照
    //   4. 时域累积
    //      - TGI (Temporal Global Illumination)
    //      - 基于运动矢量的历史帧复用
    //      - 方差裁剪减少闪烁
    //   5. 空间滤波
    //      - 边缘感知双边滤波
    //      - A-Trous 小波变换（减少噪声同时保持细节）
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>SSGI 模式。</summary>
    public enum SSGIMode
    {
        SingleBounce,
        MultiBounce,
        SpecularOnly
    }

    /// <summary>SSGI 采样策略。</summary>
    public enum SSGISampleStrategy
    {
        HemisphereUniform,
        CosineWeightedHemisphere,
        BRDFImportance,
        Hammersley,
        GoldenRatio
    }

    /// <summary>SSGI 质量。</summary>
    public enum SSGIQuality
    {
        Low,
        Medium,
        High,
        Ultra
    }

    // ────────────────────────────────────────────────────────────────────────
    // 配置
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>SSGI 配置。</summary>
    public sealed class SSGIConfig
    {
        public SSGIMode Mode = SSGIMode.MultiBounce;
        public SSGISampleStrategy SampleStrategy = SSGISampleStrategy.CosineWeightedHemisphere;
        public int SampleCount = 16;
        public int RaySteps = 16;
        public float RayStepSize = 0.3f;
        public float MaxRayDistance = 5.0f;
        public float Intensity = 1.0f;
        public float DepthBias = 0.05f;
        public int BounceCount = 3;
        public float BounceAttenuation = 0.5f;
        public float RoughnessThreshold = 0.5f;
        public bool HalfResolution = true;
        public bool EnableHiZ = true;
        public bool EnableTemporal = true;
        public float TemporalAlpha = 0.88f;
        public float VarianceClampThreshold = 3.0f;
        public int SpatialBlurRadius = 3;
        public bool EnableSpatialFilter = true;
        public int ATrousLevels = 4;
        public bool EnableDebugDraw = false;
        public float NormalTolerance = 0.5f;

        public void ApplyQuality(SSGIQuality quality)
        {
            switch (quality)
            {
                case SSGIQuality.Low:
                    SampleCount = 4; RaySteps = 8; HalfResolution = true;
                    EnableTemporal = false; EnableSpatialFilter = true;
                    SpatialBlurRadius = 3; ATrousLevels = 2; BounceCount = 1; break;
                case SSGIQuality.Medium:
                    SampleCount = 8; RaySteps = 12; HalfResolution = true;
                    EnableTemporal = true; EnableSpatialFilter = true;
                    SpatialBlurRadius = 4; ATrousLevels = 3; BounceCount = 2; break;
                case SSGIQuality.High:
                    SampleCount = 16; RaySteps = 16; HalfResolution = true;
                    EnableTemporal = true; EnableSpatialFilter = true;
                    SpatialBlurRadius = 4; ATrousLevels = 4; BounceCount = 3; break;
                case SSGIQuality.Ultra:
                    SampleCount = 32; RaySteps = 24; HalfResolution = false;
                    EnableTemporal = true; TemporalAlpha = 0.92f;
                    EnableSpatialFilter = true; SpatialBlurRadius = 5;
                    ATrousLevels = 5; BounceCount = 4; break;
            }
        }

        public static SSGIConfig Default => new() { ApplyQuality(SSGIQuality.High) };
    }

    // ────────────────────────────────────────────────────────────────────────
    // 采样生成
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>SSGI 采样点生成器。</summary>
    public static class SSGISampleGenerator
    {
        /// <summary>Cosine 加权半球采样。</summary>
        public static Vector3[] GenerateCosineHemisphere(int count, uint seed = 42)
        {
            var samples = new Vector3[count];
            float goldenAngle = 2.39996323f;
            for (int i = 0; i < count; i++)
            {
                float r = MathF.Sqrt((float)(i + 0.5) / count);
                float theta = i * goldenAngle + seed * 0.01f;
                samples[i] = new Vector3(r * MathF.Cos(theta), r * MathF.Sin(theta),
                    MathF.Sqrt(1.0f - r * r));
            }
            return samples;
        }

        /// <summary>Hammersley 采样。</summary>
        public static (Vector2 Xi, Vector3 Direction)[] GenerateHammersley(int count)
        {
            var samples = new (Vector2, Vector3)[count];
            for (int i = 0; i < count; i++)
            {
                float u = HammersleyRadicalInverse(i);
                float v = (float)(i + 0.5) / count;
                float r = MathF.Sqrt(u);
                float phi = 2.0f * MathF.PI * v;
                samples[i] = (new Vector2(u, v), new Vector3(
                    r * MathF.Cos(phi), r * MathF.Sin(phi), MathF.Sqrt(1 - r * r)));
            }
            return samples;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float HammersleyRadicalInverse(uint bits)
        {
            bits = (bits << 16u) | (bits >> 16u);
            bits = ((bits & 0x55555555u) << 1u) | ((bits & 0xAAAAAAAAu) >> 1u);
            bits = ((bits & 0x33333333u) << 2u) | ((bits & 0xCCCCCCCCu) >> 2u);
            bits = ((bits & 0x0F0F0F0Fu) << 4u) | ((bits & 0xF0F0F0F0u) >> 4u);
            bits = ((bits & 0x00FF00FFu) << 8u) | ((bits & 0xFF00FF00u) >> 8u);
            return (float)(bits) * 2.3283064365386963e-10f;
        }

        /// <summary>BRDF 重要性采样（GGX）。</summary>
        public static Vector3 SampleGGX(Vector2 xi, float roughness)
        {
            float a = roughness * roughness;
            float phi = 2.0f * MathF.PI * xi.X;
            float cosTheta = MathF.Sqrt((1.0f - xi.Y) / (1.0f + (a * a - 1.0f) * xi.Y));
            float sinTheta = MathF.Sqrt(1.0f - cosTheta * cosTheta);
            return new Vector3(sinTheta * MathF.Cos(phi), sinTheta * MathF.Sin(phi), cosTheta);
        }

        /// <summary>生成分层低差异序列（多 bounce 用）。</summary>
        public static Vector3[][] GenerateMultiBounceSamples(int bounceCount, int samplesPerBounce)
        {
            var all = new Vector3[bounceCount][];
            for (int b = 0; b < bounceCount; b++)
                all[b] = GenerateCosineHemisphere(samplesPerBounce, (uint)(42 + b * 7919));
            return all;
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // 常量缓冲
    // ────────────────────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    internal struct SSGICB
    {
        public Matrix4x4 InvViewProj;
        public Vector2 InvResolution;
        public Vector2 InvFullResolution;
        public float Intensity;
        public float DepthBias;
        public float RayStepSize;
        public float MaxRayDistance;
        public int SampleCount;
        public int RaySteps;
        public int Mode;
        public int BounceIndex;
        public float BounceAttenuation;
        public float RoughnessThreshold;
        public float NormalTolerance;
        public float SpatialBlurSigma;
        public int SpatialBlurRadius;
        public int FrameIndex;
        public float Pad0;
        public Vector3 CameraPos;
        public int EnableHiZ;
        public int ATrousLevel;
        public int EnableTemporal;
        public float TemporalAlpha;
        public float VarianceClamp;
        public float Pad1, Pad2, Pad3;
    }

    // ────────────────────────────────────────────────────────────────────────
    // Hi-Z Depth Buffer 构建器
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>Hi-Z 深度缓冲区构建（用于 SSGI 和 SSR 的 ray 加速）。</summary>
    public sealed class HiZBuilder : IDisposable
    {
        private readonly RHIDevice _device;
        private RHITexture? _hizMipChain;
        private int _width, _height, _mipLevels;
        private RHIConstantBuffer? _cb;
        private bool _disposed;

        public RHITexture HiZTexture => _hizMipChain!;
        public int MipLevels => _mipLevels;

        public HiZBuilder(RHIDevice device, int width, int height)
        {
            _device = device;
            _width = width;
            _height = height;
            _mipLevels = 1 + (int)MathF.Floor(MathF.Log2(MathF.Max(width, height)));
            Initialize();
        }

        private void Initialize()
        {
            _hizMipChain = _device.CreateTexture(new RHITextureDesc
            {
                Width = _width, Height = _height, MipLevels = _mipLevels,
                Format = RHIFormat.R32_Float,
                Usage = RHITextureUsage.UnorderedAccess | RHITextureUsage.ShaderResource,
                DebugName = "HiZ_DepthMips"
            });
            _cb = _device.CreateConstantBuffer(16);
        }

        /// <summary>构建 Hi-Z mip chain。</summary>
        public void Build(RHITexture depthBuffer)
        {
            _device.CopyTextureRegion(_hizMipChain!, depthBuffer, mipLevel: 0);
            int w = _width, h = _height;
            for (int mip = 1; mip < _mipLevels; mip++)
            {
                w = Math.Max(1, w / 2); h = Math.Max(1, h / 2);
                _device.SetShaderResource(0, _hizMipChain!, mip - 1);
                _device.SetUnorderedAccessView(0, _hizMipChain!, mip);
                _device.Dispatch((w + 7) / 8, (h + 7) / 8, 1);
            }
        }

        /// <summary>Hi-Z ray marching 交叉测试（CPU 侧参考实现）。</summary>
        public static bool HiZTrace(float[] hizMips, int mipLevels, int width, int height,
            Vector3 rayOrigin, Vector3 rayDir, float maxDist, out float hitT, out int hitMip)
        {
            hitT = float.MaxValue;
            hitMip = 0;
            float t = 0;
            int currentMip = 0;
            float lastDepth = 0;

            while (t < maxDist)
            {
                Vector3 pos = rayOrigin + rayDir * t;
                float sx = pos.X * 0.5f + 0.5f;
                float sy = pos.Y * 0.5f + 0.5f;
                if (sx < 0 || sx > 1 || sy < 0 || sy > 1) break;

                int mipW = Math.Max(1, width >> currentMip);
                int mipH = Math.Max(1, height >> currentMip);
                int px = Math.Clamp((int)(sx * mipW), 0, mipW - 1);
                int py = Math.Clamp((int)(sy * mipH), 0, mipH - 1);

                int mipOffset = 0;
                for (int m = 0; m < currentMip; m++)
                {
                    int mw = Math.Max(1, width >> m);
                    int mh = Math.Max(1, height >> m);
                    mipOffset += mw * mh;
                }
                float cellDepth = hizMips[mipOffset + py * mipW + px];

                if (pos.Z < cellDepth)
                {
                    if (currentMip == 0) { hitT = t; hitMip = 0; return true; }
                    currentMip--;
                    t = lastDepth;
                }
                else
                {
                    lastDepth = t;
                    if (currentMip < mipLevels - 1) currentMip++;
                    t += MathF.Max(0.5f, t * 0.2f) * (1 << currentMip);
                }
            }
            return false;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _hizMipChain?.Dispose(); _cb?.Dispose(); _disposed = true;
            GC.SuppressFinalize(this);
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // 多弹射 GI 扩散器
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>多弹射 GI 扩散器。</summary>
    public sealed class MultiBounceGIDiffuser : IDisposable
    {
        private readonly RHIDevice _device;
        private RHITexture? _bounceSrc, _bounceDst;
        private RHITexture? _bouncedDiffuse, _bouncedSpecular;
        private int _width, _height;
        private bool _disposed;

        public RHITexture? AccumulatedGI { get; private set; }

        public MultiBounceGIDiffuser(RHIDevice device, int width, int height)
        {
            _device = device;
            _width = width; _height = height;
            Initialize();
        }

        private void Initialize()
        {
            _bounceSrc = _device.CreateTexture(new RHITextureDesc
            {
                Width = _width, Height = _height,
                Format = RHIFormat.R16G16B16A16_Float,
                Usage = RHITextureUsage.UnorderedAccess | RHITextureUsage.ShaderResource,
                DebugName = "GI_BounceSrc"
            });
            _bounceDst = _device.CreateTexture(new RHITextureDesc
            {
                Width = _width, Height = _height,
                Format = RHIFormat.R16G16B16A16_Float,
                Usage = RHITextureUsage.UnorderedAccess | RHITextureUsage.ShaderResource,
                DebugName = "GI_BounceDst"
            });
            _bouncedDiffuse = _device.CreateTexture(new RHITextureDesc
            {
                Width = _width, Height = _height,
                Format = RHIFormat.R11G11B10_Float,
                Usage = RHITextureUsage.UnorderedAccess | RHITextureUsage.ShaderResource,
                DebugName = "GI_Diffuse"
            });
            _bouncedSpecular = _device.CreateTexture(new RHITextureDesc
            {
                Width = _width, Height = _height,
                Format = RHIFormat.R11G11B10_Float,
                Usage = RHITextureUsage.UnorderedAccess | RHITextureUsage.ShaderResource,
                DebugName = "GI_Specular"
            });
            AccumulatedGI = _device.CreateTexture(new RHITextureDesc
            {
                Width = _width, Height = _height,
                Format = RHIFormat.R11G11B10_Float,
                Usage = RHITextureUsage.UnorderedAccess | RHITextureUsage.ShaderResource,
                DebugName = "GI_Accumulated"
            });
        }

        /// <summary>执行一次 bounce 扩散。</summary>
        public void DiffuseBounce(
            RHITexture prevIndirectLight, RHITexture depthBuffer,
            RHITexture normalBuffer, RHITexture albedoBuffer,
            float attenuation, int bounceIndex)
        {
            // hack: 用空间滤波近似 diffuse bounce
            // 完整实现需要 ray marching 把 prevIndirectLight 投射到周围表面上
            // 但那个太慢了，空间模糊虽然物理不正确但视觉效果可以接受
            int gx = (_width + 7) / 8;
            int gy = (_height + 7) / 8;

            // 把上一 bounce 的 GI 结果 * albedo * attenuation -> bouncedDiffuse
            _device.SetShaderResource(0, prevIndirectLight);
            _device.SetShaderResource(1, albedoBuffer);
            _device.SetShaderResource(2, normalBuffer);
            _device.SetShaderResource(3, depthBuffer);
            _device.SetUnorderedAccessView(0, _bounceDst!);
            _device.Dispatch(gx, gy, 1);

            // 累加到全局 GI
            _device.SetShaderResource(0, AccumulatedGI!);
            _device.SetShaderResource(1, _bounceDst!);
            _device.SetUnorderedAccessView(0, _bounceSrc!);
            _device.Dispatch(gx, gy, 1);

            // 交换
            (_bounceSrc, _bounceDst) = (_bounceDst, _bounceSrc);
        }

        /// <summary>重置累积。</summary>
        public void ResetAccumulation()
        {
            if (AccumulatedGI != null)
                _device.ClearUAV(AccumulatedGI, Vector4.Zero);
        }

        /// <summary>获取扩散强度估计。</summary>
        public float EstimateBounceEnergy(float avgLuminance, int bounceIndex, float attenuation)
        {
            // 每次 bounce 大约衰减到原来的 20-40%
            float bounceFalloff = MathF.Pow(0.3f, bounceIndex);
            return avgLuminance * bounceFalloff * attenuation;
        }

        /// <summary>CPU 侧 bounce 模拟（调试）。</summary>
        public static Vector3 SimulateBounce(Vector3 incoming, Vector3 albedo, Vector3 normal,
            float roughness, float attenuation)
        {
            // Lambert diffuse 反射
            float ndotl = Math.Max(0, Vector3.Dot(normal, new Vector3(0, 1, 0)));
            Vector3 diffuse = incoming * albedo * ndotl * attenuation * 0.3183098f; // 1/PI

            // 简化的 specular 反射
            Vector3 viewDir = new(0, 0, 1);
            Vector3 halfVec = Vector3.Normalize(viewDir + new Vector3(0, 1, 0));
            float ndoth = Math.Max(0, Vector3.Dot(normal, halfVec));
            float a = roughness * roughness;
            float d = a * a / (MathF.PI * MathF.Pow(ndoth * ndoth * (a * a - 1) + 1, 2));
            Vector3 spec = incoming * d * attenuation * 0.04f; // F0=0.04

            return Vector3.Clamp(diffuse + spec, Vector3.Zero, new Vector3(10, 10, 10));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _bounceSrc?.Dispose(); _bounceDst?.Dispose();
            _bouncedDiffuse?.Dispose(); _bouncedSpecular?.Dispose();
            AccumulatedGI?.Dispose();
            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // A-Trous 小波滤波器
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>A-Trous 小波变换滤波（用于 SSGI 降噪）。</summary>
    public static class ATrousFilter
    {
        /// <summary>A-Trous 5x5 核（步长为 2^level）。</summary>
        public static readonly (int OffsetX, int OffsetY, float Weight)[] Kernel5x5 =
        {
            (-2, -2, 1.0f/256), (-1, -2, 1.0f/64), (0, -2, 3.0f/128), (1, -2, 1.0f/64), (2, -2, 1.0f/256),
            (-2, -1, 1.0f/64),  (-1, -1, 1.0f/16), (0, -1, 3.0f/32),  (1, -1, 1.0f/16), (2, -1, 1.0f/64),
            (-2,  0, 3.0f/128), (-1,  0, 3.0f/32), (0,  0, 0.0f),     (1,  0, 3.0f/32), (2,  0, 3.0f/128),
            (-2,  1, 1.0f/64),  (-1,  1, 1.0f/16), (0,  1, 3.0f/32),  (1,  1, 1.0f/16), (2,  1, 1.0f/64),
            (-2,  2, 1.0f/256), (-1,  2, 1.0f/64), (0,  2, 3.0f/128), (1,  2, 1.0f/64), (2,  2, 1.0f/256)
        };

        /// <summary>执行 A-Trous 滤波（单层级）。</summary>
        public static void FilterLevel(
            RHITexture input, RHITexture output, RHITexture depthBuffer,
            RHITexture normalBuffer, int level, RHIDevice device)
        {
            int step = 1 << level;
            // GPU compute shader 处理
            // NOTE: CPU 侧参考实现，GPU 版在 compute shader 里跑
            int gx = (input.Width + 7) / 8;
            int gy = (input.Height + 7) / 8;
            device.SetShaderResource(0, input);
            device.SetShaderResource(1, depthBuffer);
            device.SetShaderResource(2, normalBuffer);
            device.SetUnorderedAccessView(0, output);
            device.Dispatch(gx, gy, 1);
        }

        /// <summary>CPU 侧 A-Trous 滤波参考实现。</summary>
        public static Vector3 FilterPixelCPU(
            Vector4[] giBuffer, int bufW, int bufH, int px, int py, int level,
            float[] depthBuf, Vector3[] normalBuf,
            float depthThreshold = 0.01f, float normalThreshold = 0.5f)
        {
            int step = 1 << level;
            Vector3 result = Vector3.Zero;
            float totalWeight = 0;
            float centerDepth = depthBuf[py * bufW + px];
            Vector3 centerNormal = normalBuf[py * bufW + px];

            foreach (var (ox, oy, w) in Kernel5x5)
            {
                if (w == 0) continue;
                int sx = px + ox * step;
                int sy = py + oy * step;
                if (sx < 0 || sx >= bufW || sy < 0 || sy >= bufH) continue;

                float sampleDepth = depthBuf[sy * bufW + sx];
                Vector3 sampleNormal = normalBuf[sy * bufW + sx];
                Vector4 sampleGI = giBuffer[sy * bufW + sx];

                // 深度权重
                float depthDiff = Math.Abs(centerDepth - sampleDepth);
                float depthW = MathF.Exp(-depthDiff / MathF.Max(depthThreshold, 1e-6f));

                // 法线权重
                float normalDot = Vector3.Dot(centerNormal, sampleNormal);
                float normalW = MathF.Pow(Math.Max(0, normalDot), 32.0f);

                float finalW = w * depthW * normalW;
                result += new Vector3(sampleGI.X, sampleGI.Y, sampleGI.Z) * finalW;
                totalWeight += finalW;
            }

            if (totalWeight > 0) result /= totalWeight;
            return result;
        }

        /// <summary>获取指定层级的步长。</summary>
        public static int GetStep(int level) => 1 << level;

        /// <summary>预估核权重总和（归一化用）。</summary>
        public static float GetKernelWeightSum()
        {
            float sum = 0;
            foreach (var (_, _, w) in Kernel5x5) sum += w;
            return sum;
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // SSGI 主系统
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>SSGI 结果。</summary>
    public struct SSGIResult
    {
        public RHITexture CombinedGI;
        public RHITexture? DiffuseGI;
        public RHITexture? SpecularGI;
        public double TotalComputeTimeMs;
        public int BounceCount;
    }

    /// <summary>屏幕空间全局光照系统。</summary>
    public sealed class ScreenSpaceGlobalIllumination : IDisposable
    {
        private readonly RHIDevice _device;
        private SSGIConfig _config;
        private int _width, _height;
        private int _giWidth, _giHeight;
        private bool _disposed;
        private int _frameIndex;

        // GPU 资源
        private RHIConstantBuffer? _ssgiCB;
        private RHITexture? _diffuseGI;
        private RHITexture? _specularGI;
        private RHITexture? _combinedGI;
        private RHITexture? _historyGI;
        private RHITexture? _tempGI;
        private RHITexture? _filteredGI;
        private RHIPipelineState? _tracePSO;
        private RHIPipelineState? _temporalPSO;
        private RHIPipelineState? _atrousPSO;
        private RHIPipelineState? _combinePSO;
        private RHIPipelineState? _bouncePSO;

        private HiZBuilder? _hiZBuilder;
        private MultiBounceGIDiffuser? _diffuser;

        // 性能统计
        private double _lastTimeMs;
        private double _avgTimeMs;
        private int _samples;

        public SSGIConfig Config
        {
            get => _config;
            set { _config = value ?? SSGIConfig.Default; RecreateResources(); }
        }

        public RHITexture? Output => _combinedGI;
        public double LastTimeMs => _lastTimeMs;

        public ScreenSpaceGlobalIllumination(RHIDevice device, int width, int height, SSGIConfig? config = null)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));
            _width = width; _height = height;
            _config = config ?? SSGIConfig.Default;
            RecreateResources();
        }

        private void RecreateResources()
        {
            _giWidth = _config.HalfResolution ? (_width + 1) / 2 : _width;
            _giHeight = _config.HalfResolution ? (_height + 1) / 2 : _height;

            _ssgiCB = _device.CreateConstantBuffer(SSGICB.SizeOf);

            _diffuseGI = _device.CreateTexture(new RHITextureDesc
            {
                Width = _giWidth, Height = _giHeight,
                Format = RHIFormat.R11G11B10_Float,
                Usage = RHITextureUsage.UnorderedAccess | RHITextureUsage.ShaderResource,
                DebugName = "SSGI_Diffuse"
            });

            _specularGI = _device.CreateTexture(new RHITextureDesc
            {
                Width = _giWidth, Height = _giHeight,
                Format = RHIFormat.R11G11B10_Float,
                Usage = RHITextureUsage.UnorderedAccess | RHITextureUsage.ShaderResource,
                DebugName = "SSGI_Specular"
            });

            _combinedGI = _device.CreateTexture(new RHITextureDesc
            {
                Width = _giWidth, Height = _giHeight,
                Format = RHIFormat.R16G16B16A16_Float,
                Usage = RHITextureUsage.UnorderedAccess | RHITextureUsage.ShaderResource,
                DebugName = "SSGI_Combined"
            });

            _tempGI = _device.CreateTexture(new RHITextureDesc
            {
                Width = _giWidth, Height = _giHeight,
                Format = RHIFormat.R16G16B16A16_Float,
                Usage = RHITextureUsage.UnorderedAccess | RHITextureUsage.ShaderResource,
                DebugName = "SSGI_Temp"
            });

            _filteredGI = _device.CreateTexture(new RHITextureDesc
            {
                Width = _giWidth, Height = _giHeight,
                Format = RHIFormat.R16G16B16A16_Float,
                Usage = RHITextureUsage.UnorderedAccess | RHITextureUsage.ShaderResource,
                DebugName = "SSGI_Filtered"
            });

            if (_config.EnableTemporal)
            {
                _historyGI = _device.CreateTexture(new RHITextureDesc
                {
                    Width = _giWidth, Height = _giHeight,
                    Format = RHIFormat.R16G16B16A16_Float,
                    Usage = RHITextureUsage.UnorderedAccess | RHITextureUsage.ShaderResource,
                    DebugName = "SSGI_History"
                });
            }

            if (_config.EnableHiZ)
                _hiZBuilder = new HiZBuilder(_device, _width, _height);

            if (_config.Mode == SSGIMode.MultiBounce)
                _diffuser = new MultiBounceGIDiffuser(_device, _giWidth, _giHeight);

            Console.WriteLine($"[SSGI] 初始化完成，GI分辨率 {_giWidth}x{_giHeight}");
        }

        /// <summary>执行 SSGI。</summary>
        public SSGIResult Execute(
            RHITexture depthBuffer, RHITexture normalBuffer,
            RHITexture albedoBuffer, RHITexture pbrBuffer,
            RHITexture? motionVectors = null)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();

            // 1. 构建深度金字塔
            if (_hiZBuilder != null)
                _hiZBuilder.Build(depthBuffer);

            // 2. 首次弹射
            TraceFirstBounce(depthBuffer, normalBuffer, albedoBuffer, pbrBuffer);

            // 3. 多弹射
            if (_config.Mode == SSGIMode.MultiBounce && _diffuser != null)
            {
                _diffuser.ResetAccumulation();
                var prevGI = _combinedGI!;
                for (int b = 0; b < _config.BounceCount - 1; b++)
                {
                    float atten = _config.BounceAttenuation * MathF.Pow(0.5f, b);
                    _diffuser.DiffuseBounce(prevGI, depthBuffer, normalBuffer, albedoBuffer, atten, b + 1);
                    prevGI = _diffuser.AccumulatedGI!;
                }
            }

            // 4. 空间滤波
            RHITexture filteredOutput = _combinedGI!;
            if (_config.EnableSpatialFilter)
            {
                filteredOutput = ApplySpatialFilter(_combinedGI!, depthBuffer, normalBuffer);
            }

            // 5. 时域累积
            if (_config.EnableTemporal && motionVectors != null && _historyGI != null)
            {
                ApplyTemporalFilter(filteredOutput, motionVectors);
                filteredOutput = _historyGI!;
            }

            sw.Stop();
            _lastTimeMs = sw.Elapsed.TotalMilliseconds;
            _avgTimeMs = _avgTimeMs * 0.9 + _lastTimeMs * 0.1;
            _samples++;
            _frameIndex++;

            return new SSGIResult
            {
                CombinedGI = filteredOutput,
                DiffuseGI = _diffuseGI,
                SpecularGI = _specularGI,
                TotalComputeTimeMs = _lastTimeMs,
                BounceCount = _config.Mode == SSGIMode.MultiBounce ? _config.BounceCount : 1
            };
        }

        private void TraceFirstBounce(
            RHITexture depth, RHITexture normal, RHITexture albedo, RHITexture pbr)
        {
            var cb = new SSGICB
            {
                InvResolution = new Vector2(1.0f / _giWidth, 1.0f / _giHeight),
                InvFullResolution = new Vector2(1.0f / _width, 1.0f / _height),
                Intensity = _config.Intensity,
                DepthBias = _config.DepthBias,
                RayStepSize = _config.RayStepSize,
                MaxRayDistance = _config.MaxRayDistance,
                SampleCount = _config.SampleCount,
                RaySteps = _config.RaySteps,
                Mode = (int)_config.Mode,
                BounceIndex = 0,
                BounceAttenuation = _config.BounceAttenuation,
                RoughnessThreshold = _config.RoughnessThreshold,
                NormalTolerance = _config.NormalTolerance,
                FrameIndex = _frameIndex,
                EnableHiZ = _config.EnableHiZ ? 1 : 0
            };
            _device.UpdateBuffer(_ssgiCB!, cb);

            _device.SetPipelineState(_tracePSO!);
            _device.SetShaderResource(0, depth);
            _device.SetShaderResource(1, normal);
            _device.SetShaderResource(2, albedo);
            _device.SetShaderResource(3, pbr);
            if (_hiZBuilder != null)
                _device.SetShaderResource(4, _hiZBuilder.HiZTexture);
            _device.SetUnorderedAccessView(0, _diffuseGI!);
            _device.SetUnorderedAccessView(1, _specularGI!);
            _device.SetConstantBuffer(0, _ssgiCB!);
            _device.Dispatch((_giWidth + 7) / 8, (_giHeight + 7) / 8, 1);

            // 合并 diffuse + specular
            _device.SetShaderResource(0, _diffuseGI!);
            _device.SetShaderResource(1, _specularGI!);
            _device.SetUnorderedAccessView(0, _combinedGI!);
            _device.Dispatch((_giWidth + 7) / 8, (_giHeight + 7) / 8, 1);
        }

        private RHITexture ApplySpatialFilter(RHITexture input, RHITexture depth, RHITexture normal)
        {
            if (_config.ATrousLevels > 0)
            {
                // A-Trous 多层级滤波
                var src = input;
                var dst = _tempGI!;
                for (int lvl = 0; lvl < _config.ATrousLevels; lvl++)
                {
                    ATrousFilter.FilterLevel(src, dst, depth, normal, lvl, _device);
                    (src, dst) = (dst, src);
                }
                return src;
            }
            else
            {
                // 简单双边模糊
                _device.SetShaderResource(0, input);
                _device.SetShaderResource(1, depth);
                _device.SetShaderResource(2, normal);
                _device.SetUnorderedAccessView(0, _filteredGI!);
                _device.Dispatch((_giWidth + 7) / 8, (_giHeight + 7) / 8, 1);
                return _filteredGI!;
            }
        }

        private void ApplyTemporalFilter(RHITexture current, RHITexture motionVectors)
        {
            var cb = new SSGICB
            {
                InvResolution = new Vector2(1.0f / _giWidth, 1.0f / _giHeight),
                TemporalAlpha = _config.TemporalAlpha,
                VarianceClamp = _config.VarianceClampThreshold,
                EnableTemporal = 1,
                FrameIndex = _frameIndex
            };
            _device.UpdateBuffer(_ssgiCB!, cb);

            _device.SetShaderResource(0, current);
            _device.SetShaderResource(1, _historyGI!);
            _device.SetShaderResource(2, motionVectors);
            _device.SetUnorderedAccessView(0, _historyGI!);
            _device.SetConstantBuffer(0, _ssgiCB!);
            _device.Dispatch((_giWidth + 7) / 8, (_giHeight + 7) / 8, 1);
        }

        /// <summary>调整分辨率。</summary>
        public void Resize(int width, int height)
        {
            if (_width == width && _height == height) return;
            _width = width; _height = height;
            _hiZBuilder?.Dispose();
            _diffuser?.Dispose();
            RecreateResources();
        }

        /// <summary>重置时域历史。</summary>
        public void ResetTemporal()
        {
            if (_historyGI != null)
                _device.ClearUAV(_historyGI, Vector4.Zero);
        }

        /// <summary>获取统计信息。</summary>
        public string GetStats() =>
            $"SSGI: {_config.Mode}, Res={_giWidth}x{_giHeight}, " +
            $"Last={_lastTimeMs:F2}ms, Avg={_avgTimeMs:F2}ms, " +
            $"Bounces={_config.BounceCount}, Samples={_config.SampleCount}";

        /// <summary>CPU 端 SSGI 计算（调试用）。</summary>
        public static Vector3 ComputeSSGI_CPU(
            float centerDepth, Vector3 centerNormal, Vector3 centerAlbedo,
            float[] depthBuffer, Vector3[] normalBuffer, Vector3[] albedoBuffer,
            int bufW, int bufH, int px, int py,
            float intensity, int sampleCount, float maxDist, float stepSize)
        {
            if (centerDepth <= 0) return Vector3.Zero;
            var samples = SSGISampleGenerator.GenerateCosineHemisphere(sampleCount);
            Vector3 gi = Vector3.Zero;

            for (int i = 0; i < sampleCount; i++)
            {
                // TBN 变换
                Vector3 n = centerNormal;
                Vector3 up = Math.Abs(n.Y) < 0.999f ? Vector3.UnitY : Vector3.UnitX;
                Vector3 t = Vector3.Normalize(Vector3.Cross(up, n));
                Vector3 b = Vector3.Cross(n, t);
                Vector3 worldDir = t * samples[i].X + b * samples[i].Y + n * samples[i].Z;

                // Ray marching
                float hit = 0;
                for (int s = 1; s <= 16; s++)
                {
                    float dist = stepSize * s;
                    if (dist > maxDist) break;
                    int sx = px + (int)(worldDir.X * dist * bufW * 0.01f);
                    int sy = py + (int)(worldDir.Y * dist * bufH * 0.01f);
                    sx = ((sx % bufW) + bufW) % bufW;
                    sy = ((sy % bufH) + bufH) % bufH;
                    float sDepth = depthBuffer[sy * bufW + sx];
                    if (sDepth > 0 && sDepth < centerDepth - 0.01f)
                    {
                        hit = 1.0f - s / 16.0f;
                        Vector3 hitAlbedo = albedoBuffer[sy * bufW + sx];
                        Vector3 hitNormal = normalBuffer[sy * bufW + sx];
                        float nDot = Math.Max(0, Vector3.Dot(-worldDir, hitNormal));
                        gi += hitAlbedo * nDot * hit * intensity;
                        break;
                    }
                }
            }

            gi *= centerAlbedo / sampleCount;
            return Vector3.Clamp(gi, Vector3.Zero, new Vector3(5, 5, 5));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _ssgiCB?.Dispose();
            _diffuseGI?.Dispose(); _specularGI?.Dispose();
            _combinedGI?.Dispose(); _historyGI?.Dispose();
            _tempGI?.Dispose(); _filteredGI?.Dispose();
            _tracePSO?.Dispose(); _temporalPSO?.Dispose();
            _atrousPSO?.Dispose(); _combinePSO?.Dispose();
            _bouncePSO?.Dispose();
            _hiZBuilder?.Dispose(); _diffuser?.Dispose();
            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // 辅助工具
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>SSGI 调试可视化。</summary>
    public static class SSGIDebugView
    {
        /// <summary>生成 debug 可视化数据。</summary>
        public static string GenerateDebugInfo(
            double computeTimeMs, int bounceCount, int sampleCount,
            int width, int height, bool halfRes)
        {
            return $"SSGI Debug\n" +
                   $"  Compute Time: {computeTimeMs:F2}ms\n" +
                   $"  Bounces: {bounceCount}\n" +
                   $"  Samples/Pixel: {sampleCount}\n" +
                   $"  Resolution: {width}x{height} (half: {halfRes})\n" +
                   $"  Est. Memory: {(width * height * 8 * (bounceCount + 2)) / 1024.0 / 1024.0:F1}MB";
        }
    }

    /// <summary>GI 亮度估计（用于自适应采样）。</summary>
    public static class GIBrightnessEstimator
    {
        /// <summary>从 GI 纹理估算平均亮度。</summary>
        public static float EstimateAverageLuminance(RHITexture giTexture, RHIDevice device)
        {
            // 简化：用 1x1 mip 获取平均值
            // 实际实现用 GPU readback + async query
            return 0.5f; // placeholder
        }

        /// <summary>根据亮度动态调整采样数。</summary>
        public static int AdaptiveSampleCount(float avgLuminance, int baseSamples, int maxSamples)
        {
            // 暗处少采样（人眼不敏感），亮处多采样
            float weight = Math.Clamp(avgLuminance * 2.0f, 0.2f, 1.0f);
            return Math.Clamp((int)(baseSamples * weight), 4, maxSamples);
        }
    }
}