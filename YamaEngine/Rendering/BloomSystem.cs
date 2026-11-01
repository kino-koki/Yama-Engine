using System;
using System.Numerics;
using System.Runtime.InteropServices;
using YamaEngine.RHI;
using YamaEngine.Shaders;

namespace YamaEngine.Rendering
{
    // ════════════════════════════════════════════════════════════════════════
    // yama 泛光系统 (Bloom System)
    //
    // 多级降采样 + 可分离高斯模糊 + 合成。
    // 支持：
    //   - 多级 mip 降采样（Kawase blur / 双边高斯）
    //   - 亮度阈值提取（luminance key）
    //   - 镜头光晕（lens flare ghost）
    //   - 色差泛光（anamorphic bloom）
    //   - 和色调映射集成
    // ════════════════════════════════════════════════════════════════════════

    public sealed class BloomConfig
    {
        public float Threshold = 1.0f;
        public float Knee = 0.5f;
        public float Intensity = 0.5f;
        public float Scatter = 0.7f;
        public int MipLevels = 6;
        public int DownsampleStep = 2;
        public bool EnableLensDirt = false;
        public string LensDirtTexture = string.Empty;
        public bool EnableAnamorphic = false;
        public float AnamorphicRatio = 2.0f;
        public bool EnableLensFlare = false;
        public int LensFlareGhosts = 5;
        public float LensFlareIntensity = 0.3f;
        public bool EnableTint = true;
        public Vector3 Tint = new Vector3(1.0f, 0.9f, 0.8f);
        public float MaxBrightness = 10.0f;
        public bool EnableDebugMipView = false;
        public int DebugMipLevel = 0;

        /// <summary>软阈值：smoothstep 转换。</summary>
        public float SoftKnee(float brightness)
        {
            float soft = brightness - Threshold + Knee;
            soft = Math.Clamp(soft, 0.0f, 2.0f * Knee);
            soft = soft * soft / (4.0f * Math.Max(Knee, 1e-6f) + 1e-6f);
            return Math.Max(soft, brightness - Threshold) / MathF.Max(MaxBrightness - Threshold, 1e-6f);
        }

        public static BloomConfig Performance => new() { MipLevels = 4, Intensity = 0.3f, Scatter = 0.5f, EnableLensDirt = false, EnableAnamorphic = false };
        public static BloomConfig Quality => new() { MipLevels = 8, Intensity = 0.6f, Scatter = 0.8f, EnableLensDirt = true, EnableAnamorphic = true, AnamorphicRatio = 2.5f };
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BloomPrefilterCB
    {
        public Vector2 InvResolution;
        public float Threshold;
        public float SoftKnee;
        public Vector3 Tint;
        public float Knee;
        public float MaxBrightness;
        public int MipCount;
        public float Pad;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BloomDownsampleCB
    {
        public Vector2 SrcInvResolution;
        public int MipLevel;
        public Vector3 Pad;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BloomUpsampleCB
    {
        public Vector2 DstInvResolution;
        public float Scatter;
        public float Intensity;
        public int MipLevel;
        public Vector2 Pad;
        public float TintR, TintG, TintB;
        public int EnableTint;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BloomCompositeCB
    {
        public Vector2 InvResolution;
        public float BloomIntensity;
        public float DirtIntensity;
        public int EnableDirt;
        public int EnableFlare;
        public float FlareIntensity;
        public int FlareGhosts;
        public Vector3 Tint;
        public int EnableAnamorphic;
        public float AnamorphicRatio;
        public int HasDirt;
        public float Pad0;
        public float Pad1;
        public float Pad2;
    }

    public sealed class BloomSystem : IDisposable
    {
        private readonly RHIDevice _device;
        private readonly BloomConfig _config;
        private RHITexture[] _bloomMips = Array.Empty<RHITexture>();
        private RHITexture? _prefiltered;
        private RHITexture? _output;
        private RHIBuffer? _cb;
        private int _width, _height;
        private bool _disposed;

        public BloomConfig Config => _config;
        public int MipCount => _bloomMips.Length;

        public BloomSystem(RHIDevice device, int width, int height, BloomConfig? config = null)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));
            _config = config ?? new BloomConfig();
            _width = width; _height = height;
            AllocateResources();
        }

        private void AllocateResources()
        {
            _prefiltered = _device.CreateTexture(new RHITextureDesc
            { Width = _width, Height = _height, Format = RHIFormat.R16G16B16A16_Float,
              BindFlags = RHIBindFlags.ShaderResource | RHIBindFlags.UnorderedAccess, MipLevels = 1, ArraySize = 1 });

            int mipCount = _config.MipLevels;
            _bloomMips = new RHITexture[mipCount];
            int w = _width, h = _height;
            for (int i = 0; i < mipCount; i++)
            {
                w = Math.Max(1, w >> _config.DownsampleStep);
                h = Math.Max(1, h >> _config.DownsampleStep);
                _bloomMips[i] = _device.CreateTexture(new RHITextureDesc
                { Width = w, Height = h, Format = RHIFormat.R16G16B16A16_Float,
                  BindFlags = RHIBindFlags.ShaderResource | RHIBindFlags.UnorderedAccess, MipLevels = 1, ArraySize = 1 });
            }

            _output = _device.CreateTexture(new RHITextureDesc
            { Width = _width, Height = _height, Format = RHIFormat.R16G16B16A16_Float,
              BindFlags = RHIBindFlags.UnorderedAccess | RHIBindFlags.ShaderResource, MipLevels = 1, ArraySize = 1 });

            _cb = _device.CreateBuffer(new RHIBufferDesc { SizeInBytes = 256, BindFlags = RHIBindFlags.ConstantBuffer, Usage = (RHIBufferUsage)RHIUsage.Dynamic });
        }

        /// <summary>执行泛光。</summary>
        public void Execute(RHITexture hdrInput)
        {
            // Pass 1: Prefilter (亮度阈值提取)
            var preCB = new BloomPrefilterCB
            { InvResolution = new Vector2(1.0f / _width, 1.0f / _height), Threshold = _config.Threshold,
              Knee = _config.Knee, MaxBrightness = _config.MaxBrightness, MipCount = _config.MipLevels };
            _device.UpdateBuffer(_cb!, preCB);
            _device.SetShaderResource(0, hdrInput);
            _device.SetUnorderedAccessView(0, _prefiltered!);
            _device.SetConstantBuffer(0, _cb!);
            _device.Dispatch((_width + 7) / 8, (_height + 7) / 8, 1);

            // Pass 2: 逐级降采样
            for (int i = 0; i < _bloomMips.Length; i++)
            {
                var src = i == 0 ? _prefiltered! : _bloomMips[i - 1];
                int sw = src.Width, sh = src.Height;
                var dsCB = new BloomDownsampleCB { SrcInvResolution = new Vector2(1.0f / sw, 1.0f / sh), MipLevel = i };
                _device.UpdateBuffer(_cb!, dsCB);
                _device.SetShaderResource(0, src);
                _device.SetUnorderedAccessView(0, _bloomMips[i]);
                int dw = _bloomMips[i].Width, dh = _bloomMips[i].Height;
                _device.Dispatch((dw + 7) / 8, (dh + 7) / 8, 1);
            }

            // Pass 3: 逐级升采样 + 合成
            for (int i = _bloomMips.Length - 1; i >= 0; i--)
            {
                var usCB = new BloomUpsampleCB
                { DstInvResolution = new Vector2(1.0f / _bloomMips[i].Width, 1.0f / _bloomMips[i].Height),
                  Scatter = _config.Scatter, Intensity = _config.Intensity, MipLevel = i,
                  TintR = _config.Tint.X, TintG = _config.Tint.Y, TintB = _config.Tint.Z, EnableTint = _config.EnableTint ? 1 : 0 };
                _device.UpdateBuffer(_cb!, usCB);
                _device.SetShaderResource(0, _bloomMips[i]);
                _device.SetUnorderedAccessView(0, i > 0 ? _bloomMips[i - 1] : _output!);
                int dw = i > 0 ? _bloomMips[i - 1].Width : _width;
                int dh = i > 0 ? _bloomMips[i - 1].Height : _height;
                _device.Dispatch((dw + 7) / 8, (dh + 7) / 8, 1);
            }

            // Pass 4: 最终合成
            var compCB = new BloomCompositeCB
            { InvResolution = new Vector2(1.0f / _width, 1.0f / _height),
              BloomIntensity = _config.Intensity, DirtIntensity = 0.0f,
              EnableDirt = _config.EnableLensDirt ? 1 : 0, EnableFlare = _config.EnableLensFlare ? 1 : 0,
              FlareIntensity = _config.LensFlareIntensity, FlareGhosts = _config.LensFlareGhosts,
              Tint = _config.Tint, EnableAnamorphic = _config.EnableAnamorphic ? 1 : 0,
              AnamorphicRatio = _config.AnamorphicRatio };
            _device.UpdateBuffer(_cb!, compCB);
            _device.SetShaderResource(0, hdrInput);
            _device.SetShaderResource(1, _output!);
            _device.SetUnorderedAccessView(0, _output!);
            _device.Dispatch((_width + 7) / 8, (_height + 7) / 8, 1);
        }

        public RHITexture GetOutput() => _output!;
        public RHITexture GetMip(int level) => level >= 0 && level < _bloomMips.Length ? _bloomMips[level] : null!;

        public void Resize(int w, int h)
        {
            if (w == _width && h == _height) return;
            _width = w; _height = h;
            _prefiltered?.Dispose(); _output?.Dispose(); _cb?.Dispose();
            foreach (var m in _bloomMips) m.Dispose();
            AllocateResources();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _prefiltered?.Dispose(); _output?.Dispose(); _cb?.Dispose();
            foreach (var m in _bloomMips) m.Dispose();
            _disposed = true;
        }
    }
}