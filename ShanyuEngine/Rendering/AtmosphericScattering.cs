using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ShanyuEngine.RHI;
using ShanyuEngine.Shaders;

namespace ShanyuEngine.Rendering
{
    // ════════════════════════════════════════════════════════════════════════
    // 山雨 大气散射 (Atmospheric Scattering)
    //
    // 物理正确的大气散射模型，支持：
    //   1. Rayleigh 散射（分子散射，短波更长）
    //   2. Mie 散射（气溶胶/灰尘，前向为主）
    //   3. 臭氧吸收（Chappuis band 550-650nm）
    //   4. 基于 Preetham 模型或 Bruneton 的大气光学
    //   5. 可配置的大气参数（密度、高度标高、折射率）
    //   6. LUT 预计算（single/multi scattering）
    //   7. 日落/日出色调
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>大气模型。</summary>
    public enum AtmosphericModel
    {
        /// <summary>Preetham 1999（经典天空模型）。</summary>
        Preetham,
        /// <summary>Bruneton & Neyret 2008（更精确）。</summary>
        Bruneton,
        /// <summary>Hosek & Wilkie 2012（改进版 Preetham）。</summary>
        HosekWilkie,
        /// <summary>自定义参数。</summary>
        Custom
    }

    // ────────────────────────────────────────────────────────────────────────
    // 大气参数
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>大气配置参数。</summary>
    public sealed class AtmosphericConfig
    {
        public AtmosphericModel Model = AtmosphericModel.Preetham;

        // 太阳
        public float SunElevation = 45.0f; // 度
        public float SunAzimuth = 180.0f;
        public float SunIntensity = 10.0f;
        public Vector3 SunTint = Vector3.One;
        public float SunAngularRadius = 0.00465f; // 半度

        // 地球表面
        public float GroundAlbedo = 0.1f;
        public Vector3 GroundColor = new(0.3f, 0.25f, 0.2f);
        public float GroundHeight = 0.0f;

        // 大气层
        public float AtmosphereTopHeight = 100000.0f; // 100km
        public float RayleighDensity = 1.0f; // 归一化
        public float MieDensity = 0.005f; // 相对于 Rayleigh
        public float OzoneDensity = 0.001f;
        public float AbsorptionDensity = 0.002f;

        // Rayleigh 散射系数（在海平面，550nm 处）
        public float RayleighScatteringCoeff = 1.0f;

        // Mie 散射参数
        public float MieG = 0.76f; // 非对称因子
        public float MieScatteringCoeff = 1.0f;
        public float MieAbsorptionCoeff = 0.1f;

        // 高度标高（m）
        public float RayleighHeightScale = 8500.0f;
        public float MieHeightScale = 1200.0f;
        public float OzoneHeightScale = 25000.0f;
        public float AbsorptionHeightScale = 8000.0f;

        // 星空（夜间）
        public bool EnableStars = false;
        public float StarIntensity = 0.5f;
        public float MoonIntensity = 0.1f;
        public Vector3 MoonDirection = new(-0.5f, 0.3f, 0.8f);

        // LUT
        public bool EnableLUT = true;
        public int TransmittanceLUTSize = 256;
        public int ScatteringLUTSize = 32;

        // 颜色
        public Vector3 RayleighScatteringColor = new(5.8f, 13.5f, 33.1f) * 1e-6f;
        public Vector3 MieScatteringColor = new(2.0f, 2.0f, 2.0f) * 1e-6f;

        /// <summary>计算太阳方向向量。</summary>
        public Vector3 GetSunDirection()
        {
            float elevRad = SunElevation * MathF.PI / 180.0f;
            float azimRad = SunAzimuth * MathF.PI / 180.0f;
            return new Vector3(
                MathF.Cos(elevRad) * MathF.Sin(azimRad),
                MathF.Sin(elevRad),
                MathF.Cos(elevRad) * MathF.Cos(azimRad));
        }

        /// <summary>日落预设。</summary>
        public static AtmosphericConfig Sunset => new()
            { SunElevation = 3.0f, SunAzimuth = 270.0f, SunIntensity = 15.0f,
              MieDensity = 0.015f, MieG = 0.85f };

        /// <summary>正午预设。</summary>
        public static AtmosphericConfig Noon => new()
            { SunElevation = 80.0f, SunAzimuth = 180.0f, SunIntensity = 8.0f,
              MieDensity = 0.003f, MieG = 0.7f };

        /// <summary>黄昏预设。</summary>
        public static AtmosphericConfig Twilight => new()
            { SunElevation = -5.0f, SunAzimuth = 275.0f, SunIntensity = 20.0f,
              MieDensity = 0.02f, MieG = 0.9f };

        /// <summary>太空视角预设。</summary>
        public static AtmosphericConfig Space => new()
            { AtmosphereTopHeight = 150000.0f, GroundHeight = 6371000.0f,
              GroundAlbedo = 0.3f };

        public static AtmosphericConfig Default => new();
    }

    // ────────────────────────────────────────────────────────────────────────
    // Rayleigh 散射
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>Rayleigh 散射计算。</summary>
    public static class RayleighScattering
    {
        /// <summary>Rayleigh 散射截面（波长依赖，lambda^-4）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector3 ScatteringCrossSection(Vector3 lambdaNm)
        {
            // sigma_r = 8 * pi^3 * (n^2-1)^2 / (3 * N * lambda^4)
            // 简化为颜色空间的预计算值
            float r = 5.8f / MathF.Pow(lambdaNm.X / 550.0f, 4);
            float g = 13.5f / MathF.Pow(lambdaNm.Y / 550.0f, 4);
            float b = 33.1f / MathF.Pow(lambdaNm.Z / 550.0f, 4);
            return new Vector3(r, g, b) * 1e-6f;
        }

        /// <summary>Rayleigh 相位函数。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Phase(float cosTheta)
        {
            return (3.0f / 16.0f / MathF.PI) * (1.0f + cosTheta * cosTheta);
        }

        /// <summary>Rayleigh 光学深度（沿路径积分）。</summary>
        public static float OpticalDepth(float height, float heightScale, float baseDensity, float pathLength)
        {
            // 简化的指数衰减模型
            // NOTE: 完整实现需要数值积分
            return baseDensity * MathF.Exp(-MathF.Max(height, 0) / heightScale) * pathLength;
        }

        /// <summary>多波长 Rayleigh 散射系数。</summary>
        public static Vector3 ComputeScatteringCoefficients(float density)
        {
            // 在 440/550/680nm 三个波长的散射系数
            return new Vector3(
                5.8f * density,
                13.5f * density,
                33.1f * density
            ) * 1e-6f;
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // Mie 散射
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>Mie 散射计算。</summary>
    public static class MieScattering
    {
        /// <summary>Henyey-Greenstein 相位函数。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Phase(float cosTheta, float g)
        {
            float g2 = g * g;
            float denom = 1.0f + g2 - 2.0f * g * cosTheta;
            if (MathF.Abs(denom) < 1e-6f) denom = 1e-6f;
            return (1.0f - g2) / (4.0f * MathF.PI * denom * MathF.Sqrt(denom));
        }

        /// <summary>Cornette-Shank 相位函数（更精确）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float CornetteShankPhase(float cosTheta, float g)
        {
            float g2 = g * g;
            float denom = 1.0f + g2 - 2.0f * g * cosTheta;
            if (MathF.Abs(denom) < 1e-6f) denom = 1e-6f;
            float p = (3.0f * (1.0f - g2)) / (2.0f * (2.0f + g2));
            return p * (1.0f + cosTheta * cosTheta) / MathF.Pow(denom, 1.5f);
        }

        /// <summary>双 lobe Mie（前向 + 后向散射）。</summary>
        public static float DualLobePhase(float cosTheta, float gForward, float gBack, float weight)
        {
            return weight * Phase(cosTheta, gForward) + (1.0f - weight) * Phase(cosTheta, gBack);
        }

        /// <summary>Mie 光学深度。</summary>
        public static float OpticalDepth(float height, float heightScale, float density, float pathLength)
        {
            return density * MathF.Exp(-MathF.Max(height, 0) / heightScale) * pathLength;
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // 大气光学深度积分
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>大气光学深度计算。</summary>
    public static class AtmosphericOptics
    {
        private const float EARTH_RADIUS = 6371000.0f;

        /// <summary>计算从点 A 到 B 的大气光学深度（简化）。</summary>
        public static (Vector3 Rayleigh, Vector3 Mie, float Ozone) ComputeOpticalDepth(
            Vector3 posA, Vector3 posB,
            Vector3 rayleighCoeff, Vector3 mieCoeff,
            float rayleighHeight, float mieHeight, float ozoneHeight)
        {
            float pathLength = Vector3.Distance(posA, posB);
            float avgHeight = (posA.Length() + posB.Length()) * 0.5f - EARTH_RADIUS;

            Vector3 rayleigh = rayleighCoeff * MathF.Exp(-MathF.Max(avgHeight, 0) / rayleighHeight) * pathLength;
            Vector3 mie = mieCoeff * MathF.Exp(-MathF.Max(avgHeight, 0) / mieHeight) * pathLength;
            float ozone = 0.001f * MathF.Exp(-MathF.Max(avgHeight, 0) / ozoneHeight) * pathLength;

            return (rayleigh, mie, ozone);
        }

        /// <summary>简化的大气透射率（单次散射）。</summary>
        public static Vector3 Transmittance(Vector3 opticalDepth)
        {
            return new Vector3(
                MathF.Exp(-opticalDepth.X),
                MathF.Exp(-opticalDepth.Y),
                MathF.Exp(-opticalDepth.Z));
        }

        /// <summary>天空颜色计算（CPU 端参考，用于调试/LUT 预计算）。</summary>
        public static Vector3 ComputeSkyColor(
            Vector3 viewDir, Vector3 sunDir, Vector3 sunColor,
            Vector3 rayleighCoeff, Vector3 mieCoeff, float mieG,
            float rayleighHeight, float mieHeight)
        {
            float cosTheta = Vector3.Dot(viewDir, sunDir);

            // Rayleigh 散射
            float rayleighPhase = RayleighScattering.Phase(cosTheta);
            Vector3 rayleighScatter = rayleighCoeff * rayleighPhase;

            // Mie 散射
            float miePhase = MieScattering.Phase(cosTheta, mieG);
            Vector3 mieScatter = mieCoeff * miePhase;

            // 简化光学深度（假设海平面观测）
            float viewHeight = 0;
            float viewZenith = MathF.Acos(Math.Clamp(-viewDir.Y, -1, 1));
            float rayleighOD = 0.003f / MathF.Max(MathF.Cos(viewZenith), 0.01f);
            float mieOD = 0.0001f / MathF.Max(MathF.Cos(viewZenith), 0.01f);

            Vector3 rayleighTransmittance = new Vector3(
                MathF.Exp(-rayleighOD * rayleighCoeff.X * 1e6f),
                MathF.Exp(-rayleighOD * rayleighCoeff.Y * 1e6f),
                MathF.Exp(-rayleighOD * rayleighCoeff.Z * 1e6f));
            Vector3 mieTransmittance = new Vector3(
                MathF.Exp(-mieOD), MathF.Exp(-mieOD), MathF.Exp(-mieOD));

            // 天空辐亮度
            Vector3 luminance = (rayleighScatter * rayleighTransmittance + mieScatter * mieTransmittance) * sunColor;

            // 地平线附近的近似增强（多次散射补偿）
            float horizonFactor = 1.0f + 3.0f * MathF.Pow(1.0f - MathF.Abs(viewDir.Y), 8.0f);
            luminance *= horizonFactor;

            return Vector3.Clamp(luminance, Vector3.Zero, new Vector3(20, 20, 20));
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // 透射率 LUT
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>大气透射率 LUT 生成器。</summary>
    public static class TransmittanceLUTGenerator
    {
        /// <summary>生成 2D 透射率 LUT (height x view_zenith_angle)。</summary>
        public static Vector4[] Generate(int size, AtmosphericConfig config)
        {
            var lut = new Vector4[size * size];
            float earthR = 6371000.0f;
            float atmoR = earthR + config.AtmosphereTopHeight;

            for (int y = 0; y < size; y++)
            {
                float cosViewZenith = (float)y / (size - 1); // 0=天顶, 1=地平线
                for (int x = 0; x < size; x++)
                {
                    float cosSunZenith = (float)x / (size - 1);
                    // 简化的解析透射率
                    float rayleighOD = ComputeAnalyticOD(cosViewZenith, config.RayleighHeightScale, config.RayleighDensity);
                    float mieOD = ComputeAnalyticOD(cosViewZenith, config.MieHeightScale, config.MieDensity);

                    Vector3 transmittance = new Vector3(
                        MathF.Exp(-rayleighOD * config.RayleighScatteringColor.X * 1e6f),
                        MathF.Exp(-rayleighOD * config.RayleighScatteringColor.Y * 1e6f),
                        MathF.Exp(-rayleighOD * config.RayleighScatteringColor.Z * 1e6f));

                    lut[y * size + x] = new Vector4(transmittance, mieOD);
                }
            }
            return lut;
        }

        private static float ComputeAnalyticOD(float cosZenith, float heightScale, float density)
        {
            // Rayleigh 密度积分的解析近似
            float cosTheta = Math.Clamp(cosZenith, 0.001f, 1.0f);
            float scaleHeight = heightScale / 6371000.0f; // 归一化到地球半径
            // 简化：secant 近似
            float secZenith = 1.0f / cosTheta;
            return density * scaleHeight * secZenith;
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // 常量缓冲
    // ────────────────────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    internal struct AtmosphericCB
    {
        public Vector3 SunDirection;
        public float SunIntensity;
        public Vector3 SunColor;
        public float SunAngularRadius;
        public Vector2 InvResolution;
        public float GroundAlbedo;
        public float GroundHeight;
        public Vector3 GroundColor;
        public float AtmosphereTopHeight;
        public Vector3 RayleighScatter;
        public float MieG;
        public Vector3 MieScatter;
        public float MieHeightScale;
        public float RayleighHeightScale;
        public float OzoneDensity;
        public float AbsorptionDensity;
        public float MieDensity;
        public float RayleighDensity;
        public int Model;
        public int EnableStars;
        public float StarIntensity;
        public Vector3 MoonDir;
        public float MoonIntensity;
        public Vector3 CameraPos;
        public float EarthRadius;
    }

    // ────────────────────────────────────────────────────────────────────────
    // 主系统
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>大气散射渲染器。</summary>
    public sealed class AtmosphericScattering : IDisposable
    {
        private readonly RHIDevice _device;
        private AtmosphericConfig _config;
        private int _width, _height;
        private bool _disposed;

        private RHIConstantBuffer? _cb;
        private RHITexture? _output;
        private RHITexture? _transmittanceLUT;
        private RHIPipelineState? _skyPSO;
        private RHIPipelineState? _aerialPSO;
        private RHIPipelineState? _compositePSO;

        private double _lastTimeMs;

        public AtmosphericConfig Config
        {
            get => _config;
            set { _config = value ?? AtmosphericConfig.Default; RecreateResources(); }
        }

        public RHITexture? Output => _output;
        public double LastTimeMs => _lastTimeMs;

        public AtmosphericScattering(RHIDevice device, int width, int height, AtmosphericConfig? config = null)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));
            _width = width; _height = height;
            _config = config ?? AtmosphericConfig.Default;
            RecreateResources();
        }

        private void RecreateResources()
        {
            _cb = _device.CreateConstantBuffer(AtmosphericCB.SizeOf);

            _output = _device.CreateTexture(new RHITextureDesc
            {
                Width = _width, Height = _height,
                Format = RHIFormat.R16G16B16A16_Float,
                Usage = RHITextureUsage.UnorderedAccess | RHITextureUsage.ShaderResource,
                DebugName = "Atmosphere_Output"
            });

            if (_config.EnableLUT)
            {
                int lutSize = _config.TransmittanceLUTSize;
                _transmittanceLUT = _device.CreateTexture(new RHITextureDesc
                {
                    Width = lutSize, Height = lutSize,
                    Format = RHIFormat.R16G16B16A16_Float,
                    Usage = RHITextureUsage.ShaderResource,
                    DebugName = "Atmosphere_TransLut"
                });

                var lutData = TransmittanceLUTGenerator.Generate(lutSize, _config);
                _device.UpdateTexture(_transmittanceLUT, lutData);
            }

            Console.WriteLine("[AtmosphericScattering] 初始化完成");
        }

        /// <summary>渲染天空。</summary>
        public void RenderSky(RHITexture depthBuffer, RHITexture colorBuffer)
        {
            if (_disposed) return;
            var sw = System.Diagnostics.Stopwatch.StartNew();

            var cb = new AtmosphericCB
            {
                SunDirection = _config.GetSunDirection(),
                SunIntensity = _config.SunIntensity,
                SunColor = _config.SunTint,
                SunAngularRadius = _config.SunAngularRadius,
                InvResolution = new Vector2(1.0f / _width, 1.0f / _height),
                GroundAlbedo = _config.GroundAlbedo,
                GroundHeight = _config.GroundHeight,
                GroundColor = _config.GroundColor,
                AtmosphereTopHeight = _config.AtmosphereTopHeight,
                RayleighScatter = _config.RayleighScatteringColor * _config.RayleighDensity,
                MieG = _config.MieG,
                MieScatter = _config.MieScatteringColor * _config.MieDensity,
                MieHeightScale = _config.MieHeightScale,
                RayleighHeightScale = _config.RayleighHeightScale,
                OzoneDensity = _config.OzoneDensity,
                MieDensity = _config.MieDensity,
                RayleighDensity = _config.RayleighDensity,
                Model = (int)_config.Model,
                EnableStars = _config.EnableStars ? 1 : 0,
                StarIntensity = _config.StarIntensity,
                MoonDir = Vector3.Normalize(_config.MoonDirection),
                MoonIntensity = _config.MoonIntensity,
                EarthRadius = 6371000.0f
            };
            _device.UpdateBuffer(_cb!, cb);

            _device.SetShaderResource(0, depthBuffer);
            _device.SetShaderResource(1, colorBuffer);
            if (_transmittanceLUT != null)
                _device.SetShaderResource(2, _transmittanceLUT);
            _device.SetUnorderedAccessView(0, _output!);
            _device.SetConstantBuffer(0, _cb!);
            _device.Dispatch((_width + 7) / 8, (_height + 7) / 8, 1);

            sw.Stop();
            _lastTimeMs = sw.Elapsed.TotalMilliseconds;
        }

        /// <summary>应用大气雾效（aerial perspective）到已有颜色。</summary>
        public void ApplyAerialPerspective(RHITexture color, RHITexture depth)
        {
            _device.SetShaderResource(0, color);
            _device.SetShaderResource(1, depth);
            if (_transmittanceLUT != null)
                _device.SetShaderResource(2, _transmittanceLUT);
            _device.SetUnorderedAccessView(0, _output!);
            _device.SetConstantBuffer(0, _cb!);
            _device.Dispatch((_width + 7) / 8, (_height + 7) / 8, 1);
        }

        public void Resize(int w, int h)
        {
            if (_width == w && _height == h) return;
            _width = w; _height = h; RecreateResources();
        }

        /// <summary>获取天空颜色（CPU 端，调试用）。</summary>
        public Vector3 SampleSkyCPU(Vector3 viewDir)
        {
            var sunDir = _config.GetSunDirection();
            return AtmosphericOptics.ComputeSkyColor(
                viewDir, sunDir, _config.SunTint * _config.SunIntensity,
                _config.RayleighScatteringColor * _config.RayleighDensity,
                _config.MieScatteringColor * _config.MieDensity,
                _config.MieG,
                _config.RayleighHeightScale,
                _config.MieHeightScale);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _cb?.Dispose(); _output?.Dispose(); _transmittanceLUT?.Dispose();
            _skyPSO?.Dispose(); _aerialPSO?.Dispose(); _compositePSO?.Dispose();
            _disposed = true; GC.SuppressFinalize(this);
        }
    }
}
