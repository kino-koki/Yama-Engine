using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using ShanyuEngine.RHI;
using ShanyuEngine.Shaders;

namespace ShanyuEngine.Rendering
{
    // ════════════════════════════════════════════════════════════════════════
    // 山雨 多分辨率渲染器 (Multi-Resolution Renderer)
    //
    // 核心思想：画面不同区域用不同分辨率渲染，然后 upscale 拼合。
    // 和 VRS 不同的是，这里是真的降低渲染分辨率（减少像素数量），
    // 而不只是降低着色频率。
    //
    // 应用场景：
    //   1. Foveated Rendering：中心高分辨率、边缘低分辨率
    //   2. VR 渲染：每眼独立分辨率
    //   3. 区域渲染：UI 区域低分辨率、3D 区域高分辨率
    //   4. 异构渲染：部分 pass 低分辨率、部分全分辨率
    //
    // 技术要点：
    //   - 多个渲染目标，不同分辨率
    //   - Scissor/Viewport 控制 draw 区域
    //   - 拼合 pass 用双线性/ lanczos upsampling
    //   - 接缝处理：分辨率交界处的过渡
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>多分辨率区域定义。</summary>
    public sealed class ResolutionRegion
    {
        public string Name = string.Empty;
        public float ScaleX = 1.0f;
        public float ScaleY = 1.0f;
        public float CenterX = 0.5f;
        public float CenterY = 0.5f;
        public float RadiusX = 0.5f;
        public float RadiusY = 0.5f;
        public float TransitionWidth = 0.02f;
        public int ActualWidth;
        public int ActualHeight;
        public int OffsetX;
        public int OffsetY;
        public RHITexture ColorTarget;
        public RHITexture DepthTarget;
        public bool IsElliptical = true;

        public bool ContainsPoint(float nx, float ny)
        {
            float dx = (nx - CenterX) / RadiusX;
            float dy = (ny - CenterY) / RadiusY;
            float dist = IsElliptical ? MathF.Sqrt(dx * dx + dy * dy) : Math.Max(MathF.Abs(dx), MathF.Abs(dy));
            return dist <= 1.0f + TransitionWidth;
        }

        public float GetWeight(float nx, float ny)
        {
            float dx = (nx - CenterX) / RadiusX;
            float dy = (ny - CenterY) / RadiusY;
            float dist = IsElliptical ? MathF.Sqrt(dx * dx + dy * dy) : Math.Max(MathF.Abs(dx), MathF.Abs(dy));
            if (dist >= 1.0f + TransitionWidth) return 0.0f;
            if (dist <= 1.0f) return 1.0f;
            return 1.0f - (dist - 1.0f) / TransitionWidth;
        }
    }

    /// <summary>Upsampling 方法。</summary>
    public enum UpsampleMethod { Bilinear, Bicubic, Lanczos3, AMD_FSR_EASU, AMD_FSR_RCAS }

    /// <summary>多分辨率渲染配置。</summary>
    public sealed class MultiResolutionConfig
    {
        public bool EnableFoveated = true;
        public float FoveatedInnerScale = 1.0f;
        public float FoveatedMiddleScale = 0.75f;
        public float FoveatedOuterScale = 0.5f;
        public float FoveatedInnerRadius = 0.15f;
        public float FoveatedMiddleRadius = 0.35f;
        public float FoveatedOuterRadius = 0.6f;
        public bool UseMouseAsGaze = true;
        public float GazeSmoothing = 0.1f;
        public UpsampleMethod UpsampleMethod = UpsampleMethod.Bilinear;
        public bool EnableSeamHandling = true;
        public float SeamBlendWidth = 0.01f;
        public int MaxRegionCount = 4;
        public bool EnableAutoRegion = true;
        public float MinScale = 0.5f;
        public float MaxScale = 1.0f;

        public static MultiResolutionConfig Quality => new() { FoveatedOuterScale = 0.75f, MinScale = 0.75f, UpsampleMethod = UpsampleMethod.Bicubic };
        public static MultiResolutionConfig Performance => new() { FoveatedOuterScale = 0.33f, MinScale = 0.33f, UpsampleMethod = UpsampleMethod.Bilinear };
    }

    /// <summary>多分辨率渲染器。</summary>
    public sealed class MultiResolutionRenderer : IDisposable
    {
        private readonly RHIDevice _device;
        private readonly MultiResolutionConfig _config;
        private readonly List<ResolutionRegion> _regions = new();
        private RHITexture? _compositeTarget;
        private RHITexture? _blendMask;
        private int _width, _height;
        private float _gazeX = 0.5f, _gazeY = 0.5f;
        private bool _disposed;

        public int Width => _width;
        public int Height => _height;
        public IReadOnlyList<ResolutionRegion> Regions => _regions;

        public MultiResolutionRenderer(RHIDevice device, int width, int height, MultiResolutionConfig? config = null)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));
            _config = config ?? new MultiResolutionConfig();
            _width = width; _height = height;
            SetupRegions();
        }

        private void SetupRegions()
        {
            _regions.Clear();
            if (_config.EnableFoveated)
            {
                // 外圈（低分辨率）
                var outer = new ResolutionRegion
                {
                    Name = "Foveated_Outer", ScaleX = _config.FoveatedOuterScale, ScaleY = _config.FoveatedOuterScale,
                    CenterX = 0.5f, CenterY = 0.5f, RadiusX = _config.FoveatedOuterRadius,
                    RadiusY = _config.FoveatedOuterRadius * (float)_height / _width,
                    TransitionWidth = 0.05f
                };
                outer.ActualWidth = Math.Max(1, (int)(_width * outer.ScaleX));
                outer.ActualHeight = Math.Max(1, (int)(_height * outer.ScaleY));
                _regions.Add(outer);

                // 中圈
                var middle = new ResolutionRegion
                {
                    Name = "Foveated_Middle", ScaleX = _config.FoveatedMiddleScale, ScaleY = _config.FoveatedMiddleScale,
                    CenterX = 0.5f, CenterY = 0.5f, RadiusX = _config.FoveatedMiddleRadius,
                    RadiusY = _config.FoveatedMiddleRadius * (float)_height / _width,
                    TransitionWidth = 0.03f
                };
                middle.ActualWidth = Math.Max(1, (int)(_width * middle.ScaleX));
                middle.ActualHeight = Math.Max(1, (int)(_height * middle.ScaleY));
                _regions.Add(middle);

                // 内圈（全分辨率）
                var inner = new ResolutionRegion
                {
                    Name = "Foveated_Inner", ScaleX = _config.FoveatedInnerScale, ScaleY = _config.FoveatedInnerScale,
                    CenterX = 0.5f, CenterY = 0.5f, RadiusX = _config.FoveatedInnerRadius,
                    RadiusY = _config.FoveatedInnerRadius * (float)_height / _width,
                    TransitionWidth = 0.02f
                };
                inner.ActualWidth = Math.Max(1, (int)(_width * inner.ScaleX));
                inner.ActualHeight = Math.Max(1, (int)(_height * inner.ScaleY));
                _regions.Add(inner);
            }

            foreach (var region in _regions)
                AllocateRegionResources(region);

            _compositeTarget = _device.CreateTexture(new RHITextureDesc
            {
                Width = _width, Height = _height, Format = RHIFormat.R16G16B16A16_Float,
                BindFlags = RHIBindFlags.RenderTarget | RHIBindFlags.ShaderResource, MipLevels = 1, ArraySize = 1
            });

            _blendMask = _device.CreateTexture(new RHITextureDesc
            {
                Width = _width, Height = _height, Format = RHIFormat.R8_UNorm,
                BindFlags = RHIBindFlags.UnorderedAccess | RHIBindFlags.ShaderResource, MipLevels = 1, ArraySize = 1
            });
        }

        private void AllocateRegionResources(ResolutionRegion region)
        {
            region.ColorTarget = _device.CreateTexture(new RHITextureDesc
            {
                Width = region.ActualWidth, Height = region.ActualHeight,
                Format = RHIFormat.R16G16B16A16_Float,
                BindFlags = RHIBindFlags.RenderTarget | RHIBindFlags.ShaderResource,
                MipLevels = 1, ArraySize = 1
            });
            region.DepthTarget = _device.CreateTexture(new RHITextureDesc
            {
                Width = region.ActualWidth, Height = region.ActualHeight,
                Format = RHIFormat.D32_Float,
                BindFlags = RHIBindFlags.DepthStencil, MipLevels = 1, ArraySize = 1
            });
        }

        public void UpdateGaze(float mouseX, float mouseY)
        {
            if (!_config.UseMouseAsGaze || mouseX < 0 || mouseY < 0) return;
            float targetX = mouseX / _width, targetY = 1.0f - mouseY / _height;
            float s = _config.GazeSmoothing;
            _gazeX += (targetX - _gazeX) * s;
            _gazeY += (targetY - _gazeY) * s;
            foreach (var r in _regions) { r.CenterX = _gazeX; r.CenterY = _gazeY; }
        }

        public void SetRenderTargetsForRegion(int regionIndex)
        {
            if (regionIndex < 0 || regionIndex >= _regions.Count) return;
            var region = _regions[regionIndex];
            _device.SetRenderTargets(new[] { region.ColorTarget }, region.DepthTarget);
            _device.SetViewport(0, 0, region.ActualWidth, region.ActualHeight);
        }

        public void Composite()
        {
            // 生成 blend mask
            GenerateBlendMask();
            // Upsample 各区域到全分辨率并混合
            foreach (var region in _regions)
            {
                UpsampleRegion(region);
            }
        }

        private void GenerateBlendMask()
        {
            // 从内到外：内圈权重最高
            byte[] maskData = new byte[_width * _height];
            for (int y = 0; y < _height; y++)
                for (int x = 0; x < _width; x++)
                {
                    float nx = (x + 0.5f) / _width, ny = (y + 0.5f) / _height;
                    float maxWeight = 0.0f;
                    for (int i = _regions.Count - 1; i >= 0; i--)
                    {
                        float w = _regions[i].GetWeight(nx, ny);
                        if (w > maxWeight) maxWeight = w;
                    }
                    maskData[y * _width + x] = (byte)(maxWeight * 255.0f);
                }
            _device.UpdateTextureData(_blendMask!, maskData);
        }

        private void UpsampleRegion(ResolutionRegion region)
        {
            switch (_config.UpsampleMethod)
            {
                case UpsampleMethod.Bilinear: UpsampleBilinear(region); break;
                case UpsampleMethod.Bicubic: UpsampleBicubic(region); break;
                default: UpsampleBilinear(region); break;
            }
        }

        private void UpsampleBilinear(ResolutionRegion region)
        {
            int groupX = (_width + 7) / 8, groupY = (_height + 7) / 8;
            _device.SetShaderResource(0, region.ColorTarget);
            _device.SetShaderResource(1, _blendMask!);
            _device.SetUnorderedAccessView(0, _compositeTarget!);
            _device.Dispatch(groupX, groupY, 1);
        }

        private void UpsampleBicubic(ResolutionRegion region)
        {
            int groupX = (_width + 7) / 8, groupY = (_height + 7) / 8;
            _device.SetShaderResource(0, region.ColorTarget);
            _device.SetShaderResource(1, _blendMask!);
            _device.SetUnorderedAccessView(0, _compositeTarget!);
            _device.Dispatch(groupX, groupY, 1);
        }

        public RHITexture GetComposite() => _compositeTarget!;

        public void Resize(int width, int height)
        {
            if (width == _width && height == _height) return;
            _width = width; _height = height;
            foreach (var r in _regions) { r.ColorTarget?.Dispose(); r.DepthTarget?.Dispose(); }
            _compositeTarget?.Dispose(); _blendMask?.Dispose();
            SetupRegions();
        }

        public void Dispose()
        {
            if (_disposed) return;
            foreach (var r in _regions) { r.ColorTarget?.Dispose(); r.DepthTarget?.Dispose(); }
            _compositeTarget?.Dispose(); _blendMask?.Dispose();
            _disposed = true;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MultiResUpsampleCB
    {
        public Vector2 SrcResolution;
        public Vector2 DstResolution;
        public Vector2 SrcOffset;
        public Vector2 InvSrcResolution;
        public int Method;
        public float Sharpness;
        public Vector3 Pad;
    }
}
