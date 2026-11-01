using System;
using YamaEngine.Core;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using YamaEngine.RHI;
using YamaEngine.Shaders;

namespace YamaEngine.Rendering
{
    // ════════════════════════════════════════════════════════════════════════
    // yama 体积云渲染器 (Volumetric Cloud Renderer)
    //
    // 基于 Ray Marching 的程序化体积云，参考 Horizon: Zero Dawn
    // 和 NVIDIA Heterogeneous Volume Ray Tracing 论文。
    //
    // 特性：
    //   1. 程序化噪声（Perlin + Worley FBM 多层叠加）
    //   2. Ray Marching + 光步进（light marching）计算自阴影
    //   3. 珍珠贝效应（Powder/pearl）
    //   4. 多层云（高积云/卷层云/积雨云预设）
    //   5. 时间动画（云的流动、消散、生成）
    //   6. 2D/3D LUT 优化（预计算部分光照）
    //   7. 时域上采样
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>云类型预设。</summary>
    public enum CloudPreset
    {
        /// <summary>晴天稀疏积云。</summary>
        ClearSky,
        /// <summary>多云（积云）。</summary>
        PartlyCloudy,
        /// <summary>厚云覆盖。</summary>
        Overcast,
        /// <summary>积雨云/暴风云。</summary>
        Cumulonimbus,
        /// <summary>卷云（高空薄云）。</summary>
        Cirrus,
        /// <summary>自定义。</summary>
        Custom
    }

    /// <summary>体积云配置。</summary>
    public sealed class VolumetricCloudConfig
    {
        // 云层参数
        public float CloudBaseHeight = 2000.0f;
        public float CloudTopHeight = 4000.0f;
        public float CloudBaseDensity = 0.5f;
        public float CloudDensityMultiplier = 1.0f;
        public float CloudCoverage = 0.5f;
        public float CloudType = 0.5f; // 0=积云 1=层云

        // Ray Marching
        public int RayMarchSteps = 64;
        public int LightMarchSteps = 6;
        public float MaxRayDistance = 30000.0f;
        public float RayStepScale = 1.0f;

        // 噪声
        public float NoiseScale = 3.0e-5f;
        public float DetailNoiseScale = 1.0e-4f;
        public float CurlNoiseStrength = 0.1f;
        public float AnvilBias = 0.0f;
        public int NoiseOctaves = 6;
        public int DetailOctaves = 4;

        // 光照
        public float ScatteringG = 0.8f; // 云的前向散射很强
        public float AbsorptionCoefficient = 0.003f;
        public float PowderEffect = 0.3f;
        public float SilverLiningIntensity = 1.0f;
        public float MultiScatteringBounces = 2;
        public float AmbientOcclusionStrength = 0.3f;
        public float ForwardScattering = 0.6f;

        // 动画
        public float WindSpeed = 100.0f;
        public Vector3 WindDirection = new(1, 0, 0.3f);
        public float CoverageAnimationSpeed = 0.01f;
        public float DetailAnimationSpeed = 0.05f;

        // 优化
        public bool HalfResolution = true;
        public bool EnableTemporal = true;
        public float TemporalAlpha = 0.85f;
        public bool Enable2DLUT = true;
        public int LUTSize = 128;

        // 质量
        public CloudPreset Preset = CloudPreset.PartlyCloudy;

        public static VolumetricCloudConfig Default => new();
        public static VolumetricCloudConfig HighQuality => new()
        {
            RayMarchSteps = 128, LightMarchSteps = 12, NoiseOctaves = 8,
            DetailOctaves = 6, MultiScatteringBounces = 4, HalfResolution = false
        };

        public void ApplyPreset(CloudPreset preset)
        {
            Preset = preset;
            switch (preset)
            {
                case CloudPreset.ClearSky:
                    CloudCoverage = 0.2f; CloudBaseDensity = 0.3f;
                    CloudBaseHeight = 3000.0f; CloudType = 0.3f; break;
                case CloudPreset.PartlyCloudy:
                    CloudCoverage = 0.5f; CloudBaseDensity = 0.5f;
                    CloudBaseHeight = 2000.0f; CloudTopHeight = 4000.0f; break;
                case CloudPreset.Overcast:
                    CloudCoverage = 0.95f; CloudBaseDensity = 0.8f;
                    CloudType = 0.8f; CloudBaseHeight = 1000.0f; break;
                case CloudPreset.Cumulonimbus:
                    CloudCoverage = 0.7f; CloudBaseDensity = 1.0f;
                    CloudTopHeight = 8000.0f; CloudBaseHeight = 500.0f; AnvilBias = 0.5f; break;
                case CloudPreset.Cirrus:
                    CloudCoverage = 0.3f; CloudBaseDensity = 0.1f;
                    CloudBaseHeight = 8000.0f; CloudTopHeight = 12000.0f; CloudType = 0.1f; break;
            }
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // 常量缓冲
    // ────────────────────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    internal struct CloudCB
    {
        public Matrix4x4 InvViewProj;
        public Vector3 CameraPos;
        public float NearPlane;
        public Vector2 InvResolution;
        public float CloudBaseHeight;
        public float CloudTopHeight;
        public float CloudThickness; // top - base
        public float CloudBaseDensity;
        public float CloudDensityMul;
        public float CloudCoverage;
        public float CloudType;
        public Vector3 SunDirection;
        public float SunIntensity;
        public Vector3 SunColor;
        public float ScatteringG;
        public int RayMarchSteps;
        public int LightMarchSteps;
        public float Absorption;
        public float PowderEffect;
        public float SilverLining;
        public int MultiScatterBounces;
        public float AmbientAO;
        public float ForwardScatter;
        public float WindSpeed;
        public Vector3 WindDir;
        public float Time;
        public float NoiseScale;
        public float DetailScale;
        public float CurlStrength;
        public float AnvilBias;
        public int NoiseOctaves;
        public int DetailOctaves;
        public int FrameIndex;
        public float TemporalAlpha;
        public float MaxRayDist;
        public int Enable2DLUT;
        public int LUTSize;
        public float CoverageAnimSpeed;
        public float DetailAnimSpeed;
    }

    // ────────────────────────────────────────────────────────────────────────
    // 程序化噪声（CPU 侧参考）
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>云密度采样（CPU 参考实现）。</summary>
    public static class CloudDensitySampler
    {
        /// <summary>多层 FBM 噪声采样云密度。</summary>
        public static float SampleDensity(
            Vector3 worldPos, float time,
            float noiseScale, float detailScale,
            float coverage, float cloudType, float baseDensity,
            int octaves, int detailOctaves,
            Vector3 windDir, float windSpeed)
        {
            // 应用风偏移
            Vector3 windOffset = windDir * (windSpeed * time);
            Vector3 samplePos = worldPos + windOffset;

            // 大尺度噪声（确定云的形状）
            float bigNoise = FBM(samplePos * noiseScale, octaves);

            // 细节噪声（云的边缘细节）
            float detailNoise = FBM(samplePos * detailScale, detailOctaves) * 0.5f;

            // Coverage 控制：低于 coverage 阈值的地方没有云
            float cloudShape = bigNoise - (1.0f - coverage);
            cloudShape = Math.Clamp(cloudShape / MathF.Max(coverage, 0.01f), 0, 1);

            // 混合云类型
            float stratusNoise = bigNoise * 0.6f + 0.4f; // 层云更均匀
            float cumulusNoise = bigNoise; // 积云更蓬松
            cloudShape = MathUtils.Lerp(cumulusNoise, stratusNoise, cloudType);

            // 应用 base density
            float density = cloudShape * baseDensity;

            // 添加细节（只在边缘附近）
            float edgeFactor = 1.0f - MathF.Abs(cloudShape - 0.5f) * 2.0f;
            density += detailNoise * edgeFactor * 0.3f;

            return Math.Clamp(density, 0, 1);
        }

        /// <summary>FBM（分形布朗运动）噪声。</summary>
        public static float FBM(Vector3 p, int octaves)
        {
            float value = 0, amplitude = 0.5f, frequency = 1.0f;
            for (int i = 0; i < octaves; i++)
            {
                value += amplitude * Noise3D(p * frequency);
                amplitude *= 0.5f;
                frequency *= 2.0f;
            }
            return value;
        }

        /// <summary>简单的 3D value noise（简化版，实际用 GPU 的 gradient noise）。</summary>
        private static float Noise3D(Vector3 p)
        {
            // hash-based value noise
            int ix = (int)MathF.Floor(p.X), iy = (int)MathF.Floor(p.Y), iz = (int)MathF.Floor(p.Z);
            float fx = p.X - ix, fy = p.Y - iy, fz = p.Z - iz;
            float ux = fx * fx * (3 - 2 * fx);
            float uy = fy * fy * (3 - 2 * fy);
            float uz = fz * fz * (3 - 2 * fz);

            float lerp(float a, float b, float t) => a + (b - a) * t;
            float hash(int x, int y, int z)
            {
                uint h = (uint)(x * 374761393 + y * 668265263 + z * 1274126177);
                h = (h ^ (h >> 13)) * 1103515245;
                return (float)(h & 0xFFFF) / 65535.0f;
            }

            return lerp(
                lerp(
                    lerp(hash(ix, iy, iz), hash(ix+1, iy, iz), ux),
                    lerp(hash(ix, iy+1, iz), hash(ix+1, iy+1, iz), ux), uy),
                lerp(
                    lerp(hash(ix, iy, iz+1), hash(ix+1, iy, iz+1), ux),
                    lerp(hash(ix, iy+1, iz+1), hash(ix+1, iy+1, iz+1), ux), uy),
                uz);
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // 2D LUT 预计算
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>云光照 2D LUT 预计算器。</summary>
    public static class CloudLUTGenerator
    {
        /// <summary>生成 2D 光照 LUT (density x light_angle)。</summary>
        public static Vector4[] GenerateLightingLUT(
            int size, float scatteringG, float absorption,
            int multiBounce, float powderEffect)
        {
            var lut = new Vector4[size * size];
            for (int y = 0; y < size; y++)
            {
                float density = (float)y / (size - 1) * 10.0f; // 0~10
                for (int x = 0; x < size; x++)
                {
                    float cosAngle = (float)x / (size - 1) * 2.0f - 1.0f; // -1~1
                    var result = ComputeLighting(density, cosAngle, scatteringG,
                        absorption, multiBounce, powderEffect);
                    lut[y * size + x] = result;
                }
            }
            return lut;
        }

        private static Vector4 ComputeLighting(
            float density, float cosAngle, float g,
            float absorption, int bounces, float powder)
        {
            // HG phase function
            float g2 = g * g;
            float denom = 1.0f + g2 - 2.0f * g * cosAngle;
            if (MathF.Abs(denom) < 1e-6f) denom = 1e-6f;
            float phase = (1.0f - g2) / (4.0f * MathF.PI * denom * MathF.Sqrt(MathF.Max(denom, 1e-6f)));

            // Beer's law transmittance
            float transmittance = MathF.Exp(-density * absorption);

            // Powder effect (多次散射前向增强)
            float powderEffect = 1.0f - MathF.Exp(-density * powder * 2.0f);
            float light = transmittance * phase + powderEffect * 0.25f;

            // 多次 bounce 近似
            for (int b = 1; b < bounces; b++)
            {
                float bounceTrans = MathF.Exp(-density * absorption * (b + 1) * 0.5f);
                light += bounceTrans * 0.3f / (b + 1);
            }

            return new Vector4(light, transmittance, phase, 0);
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // 主系统
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>体积云渲染器。</summary>
}
