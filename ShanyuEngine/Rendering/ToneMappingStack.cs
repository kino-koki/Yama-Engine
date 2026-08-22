using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ShanyuEngine.RHI;
using ShanyuEngine.Shaders;

namespace ShanyuEngine.Rendering
{
    // ════════════════════════════════════════════════════════════════════════
    // 山雨 色调映射栈 (Tone Mapping Stack)
    //
    // 支持多种色调映射曲线，可自由切换和组合：
    //   - ACES (Academy Color Encoding System) — 电影工业标准
    //   - ACES Filmic — Stephen Hill 的近似实现
    //   - Reinhard — 经典的全局色调映射
    //   - Hable (Uncharted 2) — 游戏常用
    //   - AgX — 新兴的电影级色调映射
    //   - UE4/5 标准 — sRGB 近似
    //   - Neutral — 纯线性截断
    //
    // 额外功能：
    //   - 曝光补偿 (Exposure Compensation)
    //   - 伽马校正
    //   - 色调映射前后 luminance 统计
    //   - HDR/SDR 自动切换
    // ════════════════════════════════════════════════════════════════════════

    public enum ToneMappingMode { ACES, ACES_Filmic, Reinhard, ReinhardExtended, Hable, AgX, UEFour, Neutral, Custom }

    public sealed class ToneMappingConfig
    {
        public ToneMappingMode Mode = ToneMappingMode.ACES;
        public float ExposureCompensation = 0.0f;
        public float PreExposure = 1.0f;
        public float Gamma = 2.2f;
        public bool EnableGammaCorrection = true;
        public float MaxNits = 1000.0f;
        public float MinNits = 0.005f;
        public bool OutputHDR = false;
        public float Contrast = 1.0f;
        public float Brightness = 1.0f;
        public float Saturation = 1.0f;
        public float Temperature = 6500.0f;
        public float Tint = 0.0f;
        public bool EnableLuminanceStats = false;
        public Vector4 CustomCurve = new(0.0f, 0.0f, 0.0f, 1.0f);

        public static ToneMappingConfig ACES_Default => new() { Mode = ToneMappingMode.ACES, ExposureCompensation = 0.0f, Gamma = 2.2f };
        public static ToneMappingConfig Filmic => new() { Mode = ToneMappingMode.ACES_Filmic, ExposureCompensation = 0.5f };
        public static ToneMappingConfig Reinhard => new() { Mode = ToneMappingMode.Reinhard };
        public static ToneMappingConfig HDR => new() { Mode = ToneMappingMode.ACES, OutputHDR = true, MaxNits = 2000.0f, EnableGammaCorrection = false };
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ToneMappingCB
    {
        public Vector2 InvResolution;
        public float ExposureComp;
        public float PreExposure;
        public float Gamma;
        public int Mode;
        public int EnableGamma;
        public float MaxNits;
        public float MinNits;
        public int OutputHDR;
        public float Contrast;
        public float Brightness;
        public float Saturation;
        public float Temperature;
        public float Tint;
        public Vector4 CustomCurve;
    }

    /// <summary>色调映射函数库（CPU 侧，用于工具和验证）。</summary>
    public static class ToneMappingFunctions
    {
        // ACES RRT/ODT fit (Stephen Hill)
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector3 ACESFit(Vector3 x)
        {
            float a = 2.51f, b = 0.03f, c = 2.43f, d = 0.59f, e = 0.14f;
            return ClampSRGB(new Vector3(
                (x.X * (a * x.X + b)) / (x.X * (c * x.X + d) + e),
                (x.Y * (a * x.Y + b)) / (x.Y * (c * x.Y + d) + e),
                (x.Z * (a * x.Z + b)) / (x.Z * (c * x.Z + d) + e)));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector3 ACESFilmic(Vector3 x)
        {
            // ACES Filmic (Unreal Engine style)
            Vector3 a = new Vector3(0.6242f, 0.6242f, 0.6242f);
            Vector3 b = new Vector3(0.2990f, 0.2990f, 0.2990f);
            Vector3 c = new Vector3(0.0920f, 0.0920f, 0.0920f);
            Vector3 d = new Vector3(1.3100f, 1.3100f, 1.3100f);
            Vector3 e = new Vector3(1.0f, 1.0f, 1.0f);

            Vector3 numerator = Vector3.Multiply(Vector3.Multiply(x, a) + b, x) + c;
            Vector3 denominator = Vector3.Multiply(Vector3.Multiply(x, a) + d, x) + e;
            return ClampSRGB(numerator / denominator);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector3 Reinhard(Vector3 x)
        {
            float lum = 0.2126f * x.X + 0.7152f * x.Y + 0.0722f * x.Z;
            float reinhardLum = lum / (1.0f + lum);
            if (lum < 1e-6f) return Vector3.Zero;
            float scale = reinhardLum / lum;
            return new Vector3(x.X * scale, x.Y * scale, x.Z * scale);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector3 ReinhardExtended(Vector3 x, float whitePoint)
        {
            float lum = 0.2126f * x.X + 0.7152f * x.Y + 0.0722f * x.Z;
            float wpLum = whitePoint;
            float mappedLum = lum * (1.0f + lum / (wpLum * wpLum)) / (1.0f + lum);
            if (lum < 1e-6f) return Vector3.Zero;
            float scale = mappedLum / lum;
            return new Vector3(x.X * scale, x.Y * scale, x.Z * scale);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector3 Hable(Vector3 x)
        {
            float A = 0.22f, B = 0.30f, C = 2.51f, D = 0.30f, E = 0.10f, F = 0.20f, W = 11.2f;
            Vector3 ColorUtilityUCurve(Vector3 c) {
                return ((c * (A * c + C * B) + D * E) / (c * (A * c + B) + F * E)) - E / F;
            }
            float whiteScale = 1.0f / ColorUtilityUCurve(new Vector3(W, W, W)).X;
            return ClampSRGB(ColorUtilityUCurve(x) * whiteScale);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector3 AgX(Vector3 x)
        {
            // AgX tone mapping (Ben Cloward)
            Vector3 MulMatrix(Vector3 c, float m00, float m01, float m02, float m10, float m11, float m12, float m20, float m21, float m22)
            {
                return new Vector3(
                    c.X * m00 + c.Y * m01 + c.Z * m02,
                    c.X * m10 + c.Y * m11 + c.Z * m12,
                    c.X * m20 + c.Y * m21 + c.Z * m22);
            }
            Vector3 x2 = MulMatrix(x, 0.842479062253094f, 0.0784335999999998f, 0.0792177784589968f,
                0.0792177784589968f, 0.842479062253094f, 0.0784335999999998f,
                0.0792177784589968f, 0.0784335999999998f, 0.842479062253094f);
            // AgX S-curve
            Vector3 SigmoidAgX(Vector3 v) {
                Vector3 x2_ = v * v;
                Vector3 x3 = x2_ * v;
                return (x2_ * 2.51f + 0.03f) / (x3 * 2.43f + x2_ * 0.59f + v * 0.14f);
            }
            Vector3 mapped = SigmoidAgX(x2);
            return ClampSRGB(MulMatrix(mapped, 1.196995013324638f, -0.0986317016368459f, -0.0982305685425245f,
                -0.0982305685425245f, 1.196995013324638f, -0.0986317016368459f,
                -0.0986317016368459f, -0.0982305685425245f, 1.196995013324638f));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector3 UEFourToneMap(Vector3 x)
        {
            // UE4 approximate ACES
            x = new Vector3(MathF.Max(x.X, 0), MathF.Max(x.Y, 0), MathF.Max(x.Z, 0));
            float a = 0.0245786f, b = 0.000090537f, c = 0.983729f, d = 0.4329510f, e = 0.238081f;
            return new Vector3(MathF.Max((x.X * (a * x.X + b)) / (x.X * (c * x.X + d) + e), 0),
                MathF.Max((x.Y * (a * x.Y + b)) / (x.Y * (c * x.Y + d) + e), 0),
                MathF.Max((x.Z * (a * x.Z + b)) / (x.Z * (c * x.Z + d) + e), 0));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector3 ClampSRGB(Vector3 c) => new(
                Math.Clamp(c.X, 0.0f, 1.0f), Math.Clamp(c.Y, 0.0f, 1.0f), Math.Clamp(c.Z, 0.0f, 1.0f));

        public static float ComputeLuminance(Vector3 color) => 0.2126f * color.X + 0.7152f * color.Y + 0.0722f * color.Z;
        public static Vector3 ApplyExposure(Vector3 color, float exposure) => color * MathF.Pow(2.0f, exposure);
    }

    public sealed class ToneMappingStack : IDisposable
    {
        private readonly RHIDevice _device;
        private readonly ToneMappingConfig _config;
        private RHITexture? _output;
        private RHIBuffer? _cb;
        private RHIBuffer? _luminanceStatsBuffer;
        private int _width, _height;
        private bool _disposed;
        private float _avgLuminance, _maxLuminance;

        public ToneMappingConfig Config => _config;
        public float AverageLuminance => _avgLuminance;
        public float MaxLuminance => _maxLuminance;

        public ToneMappingStack(RHIDevice device, int width, int height, ToneMappingConfig? config = null)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));
            _config = config ?? new ToneMappingConfig();
            _width = width; _height = height;
            _output = _device.CreateTexture(new RHITextureDesc
            { Width = width, Height = height, Format = _config.OutputHDR ? RHIFormat.R16G16B16A16_Float : RHIFormat.R8G8B8A8_UNorm,
              BindFlags = RHIBindFlags.UnorderedAccess | RHIBindFlags.ShaderResource, MipLevels = 1, ArraySize = 1 });
            _cb = _device.CreateBuffer(new RHIBufferDesc { SizeInBytes = 256, BindFlags = RHIBindFlags.ConstantBuffer, Usage = RHIUsage.Dynamic });
            _luminanceStatsBuffer = _device.CreateBuffer(new RHIBufferDesc { SizeInBytes = 16, BindFlags = RHIBindFlags.UnorderedAccess | RHIBindFlags.ShaderResource, Usage = RHIUsage.Dynamic });
        }

        public void Execute(RHITexture hdrInput)
        {
            var cb = new ToneMappingCB
            { InvResolution = new Vector2(1.0f / _width, 1.0f / _height),
              ExposureComp = _config.ExposureCompensation, PreExposure = _config.PreExposure,
              Gamma = _config.Gamma, Mode = (int)_config.Mode, EnableGamma = _config.EnableGammaCorrection ? 1 : 0,
              MaxNits = _config.MaxNits, MinNits = _config.MinNits, OutputHDR = _config.OutputHDR ? 1 : 0,
              Contrast = _config.Contrast, Brightness = _config.Brightness, Saturation = _config.Saturation,
              Temperature = _config.Temperature, Tint = _config.Tint, CustomCurve = _config.CustomCurve };
            _device.UpdateBuffer(_cb!, cb);
            _device.SetShaderResource(0, hdrInput);
            _device.SetUnorderedAccessView(0, _output!);
            _device.SetUnorderedAccessView(1, _luminanceStatsBuffer!);
            _device.SetConstantBuffer(0, _cb!);
            _device.Dispatch((_width + 7) / 8, (_height + 7) / 8, 1);
        }

        public RHITexture GetOutput() => _output!;

        /// <summary>CPU 侧色调映射（工具用）。</summary>
        public Vector3 ApplyToneMappingCPU(Vector3 hdr)
        {
            hdr = ToneMappingFunctions.ApplyExposure(hdr, _config.ExposureCompensation) * _config.PreExposure;
            return _config.Mode switch
            {
                ToneMappingMode.ACES => ToneMappingFunctions.ACESFit(hdr),
                ToneMappingMode.ACES_Filmic => ToneMappingFunctions.ACESFilmic(hdr),
                ToneMappingMode.Reinhard => ToneMappingFunctions.Reinhard(hdr),
                ToneMappingMode.ReinhardExtended => ToneMappingFunctions.ReinhardExtended(hdr, 4.0f),
                ToneMappingMode.Hable => ToneMappingFunctions.Hable(hdr),
                ToneMappingMode.AgX => ToneMappingFunctions.AgX(hdr),
                ToneMappingMode.UEFour => ToneMappingFunctions.UEFourToneMap(hdr),
                _ => ToneMappingFunctions.ACESFit(hdr)
            };
        }

        public void Resize(int w, int h)
        {
            if (w == _width && h == _height) return;
            _width = w; _height = h;
            _output?.Dispose(); _output = _device.CreateTexture(new RHITextureDesc
            { Width = w, Height = h, Format = _config.OutputHDR ? RHIFormat.R16G16B16A16_Float : RHIFormat.R8G8B8A8_UNorm,
              BindFlags = RHIBindFlags.UnorderedAccess | RHIBindFlags.ShaderResource, MipLevels = 1, ArraySize = 1 });
        }

        public void Dispose() { if (_disposed) return; _output?.Dispose(); _cb?.Dispose(); _luminanceStatsBuffer?.Dispose(); _disposed = true; }
    }
}