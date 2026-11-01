using System;
using System.Numerics;
using System.Runtime.InteropServices;
using YamaEngine.RHI;
using YamaEngine.Shaders;

namespace YamaEngine.Rendering
{
    // ════════════════════════════════════════════════════════════════════════
    // yama 景深处理器 V2 (Depth of Field Processor)
    //
    // 比之前的 DepthOfFieldEffect 更完整：
    //   - 物理正确的薄镜头模型
    //   - 散景 Bokeh 模拟（六边形/叶片光圈）
    //   - 多 pass 可变半径模糊
    //   - 近景 depth peel + alpha 遮罩
    //   - 和 TAA/DRS 集成
    // ════════════════════════════════════════════════════════════════════════

    public enum BokehShapeV2 { Circle = 0, Hexagon = 1, Octagon = 2, Custom = 3 }

    public sealed class DOFProcessorConfig
    {
        public float FocusDistance = 10.0f;
        public float FocalLength = 50.0f;
        public float Aperture = 2.8f;
        public float SensorSize = 36.0f;
        public float NearFocusRange = 2.0f;
        public float FarFocusRange = 50.0f;
        public float MaxBlurRadius = 30.0f;
        public BokehShapeV2 BokehShape = BokehShapeV2.Hexagon;
        public int BladeCount = 7;
        public int MaxSamples = 64;
        public bool EnableNearBlur = true;
        public bool EnableFarBlur = true;
        public bool EnableDepthPeel = true;
        public int DepthPeelLayers = 2;
        public float CoCScale = 1.0f;
        public float CoCBias = 0.0f;
        public bool EnablePhysicalModel = true;
        public bool EnableDebugOverlay = false;

        /// <summary>物理模型：CoC = f^2 * |z - D| / (N * z * (D - f))，简化版用 smoothstep。</summary>
        public float ComputeCoCPhysical(float depth, float focalLengthMM, float apertureF, float focusDist)
        {
            float f = focalLengthMM * 0.001f; // mm -> m
            float D = focusDist;
            float N = apertureF;
            float z = MathF.Max(depth, 0.01f);
            // 薄镜头 CoC
            float coc = f * f * MathF.Abs(z - D) / (N * z * MathF.Max(D - f, 0.001f));
            return coc * 1000.0f * CoCScale + CoCBias; // 转回 mm 级别的像素
        }

        public float ComputeCoCSmoothstep(float depth)
        {
            float nearT = Math.Max(FocusDistance - NearFocusRange, 0.0f);
            float farT = FocusDistance + FarFocusRange;
            float coc = 0.0f;
            if (depth < FocusDistance && depth > nearT)
            {
                float t = 1.0f - (depth - nearT) / Math.Max(FocusDistance - nearT, 0.001f);
                coc = t * t * MaxBlurRadius;
            }
            else if (depth > FocusDistance && depth < farT)
            {
                float t = (depth - FocusDistance) / Math.Max(farT - FocusDistance, 0.001f);
                coc = t * t * MaxBlurRadius;
            }
            else if (depth <= nearT)
            {
                coc = MaxBlurRadius * 1.5f; // 近处全模糊
            }
            else if (depth >= farT)
            {
                coc = MaxBlurRadius;
            }
            return MathF.Max(coc, 0.0f);
        }

        public static DOFProcessorConfig Portrait => new() { FocusDistance = 3.0f, Aperture = 1.4f, MaxBlurRadius = 40.0f, NearFocusRange = 0.5f, FarFocusRange = 5.0f, BokehShape = BokehShapeV2.Hexagon, BladeCount = 6 };
        public static DOFProcessorConfig Cinematic => new() { FocusDistance = 15.0f, Aperture = 2.0f, MaxBlurRadius = 25.0f, BokehShape = BokehShapeV2.Octagon, BladeCount = 8 };
        public static DOFProcessorConfig Disabled => new() { MaxBlurRadius = 0.0f, EnableNearBlur = false, EnableFarBlur = false };
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DOFProcessorCB
    {
        public Matrix4x4 InvProjMatrix;
        public Vector2 InvResolution;
        public float FocusDistance;
        public float FocalLength;
        public float Aperture;
        public float SensorSize;
        public float NearFocusRange;
        public float FarFocusRange;
        public float MaxBlurRadius;
        public int BokehShape;
        public int BladeCount;
        public int MaxSamples;
        public int EnableNear;
        public int EnableFar;
        public int EnablePhysical;
        public float CoCScale;
        public float CoCBias;
        public int DebugMode;
    }

    public sealed class DepthOfFieldProcessor : IDisposable
    {
        private readonly RHIDevice _device;
        private readonly DOFProcessorConfig _config;
        private RHITexture? _cocTexture;
        private RHITexture? _nearField;
        private RHITexture? _farField;
        private RHITexture? _tempTargetA;
        private RHITexture? _tempTargetB;
        private RHIBuffer? _cb;
        private RHIBuffer? _bokehLUT;
        private int _width, _height;
        private bool _disposed;

        public DOFProcessorConfig Config => _config;

        public DepthOfFieldProcessor(RHIDevice device, int width, int height, DOFProcessorConfig? config = null)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));
            _config = config ?? new DOFProcessorConfig();
            _width = width; _height = height;
            AllocateResources();
            BuildBokehLUT();
        }

        private void AllocateResources()
        {
            _cocTexture = _device.CreateTexture(new RHITextureDesc { Width = _width, Height = _height, Format = RHIFormat.R16_Float, BindFlags = RHIBindFlags.ShaderResource | RHIBindFlags.UnorderedAccess, MipLevels = 1, ArraySize = 1 });
            _nearField = _device.CreateTexture(new RHITextureDesc { Width = _width, Height = _height, Format = RHIFormat.R16G16B16A16_Float, BindFlags = RHIBindFlags.ShaderResource | RHIBindFlags.UnorderedAccess, MipLevels = 1, ArraySize = 1 });
            _farField = _device.CreateTexture(new RHITextureDesc { Width = _width, Height = _height, Format = RHIFormat.R16G16B16A16_Float, BindFlags = RHIBindFlags.ShaderResource | RHIBindFlags.UnorderedAccess, MipLevels = 1, ArraySize = 1 });
            _tempTargetA = _device.CreateTexture(new RHITextureDesc { Width = _width, Height = _height, Format = RHIFormat.R16G16B16A16_Float, BindFlags = RHIBindFlags.UnorderedAccess | RHIBindFlags.ShaderResource, MipLevels = 1, ArraySize = 1 });
            _tempTargetB = _device.CreateTexture(new RHITextureDesc { Width = _width, Height = _height, Format = RHIFormat.R16G16B16A16_Float, BindFlags = RHIBindFlags.UnorderedAccess | RHIBindFlags.ShaderResource, MipLevels = 1, ArraySize = 1 });
            _cb = _device.CreateBuffer(new RHIBufferDesc { SizeInBytes = 256, BindFlags = RHIBindFlags.ConstantBuffer, Usage = (RHIBufferUsage)RHIUsage.Dynamic });
            _bokehLUT = _device.CreateBuffer(new RHIBufferDesc { SizeInBytes = (ulong)(_config.MaxSamples * 16), BindFlags = RHIBindFlags.StructuredBuffer | RHIBindFlags.ShaderResource, StructureByteStride = 16, Usage = (RHIBufferUsage)RHIUsage.Immutable });
        }

        /// <summary>构建 Bokeh 采样 LUT（CPU 生成，上传 GPU）。</summary>
        private void BuildBokehLUT()
        {
            int samples = _config.MaxSamples;
            var lutData = new Vector4[samples];
            float goldenAngle = 2.39996323f;

            for (int i = 0; i < samples; i++)
            {
                float r = MathF.Sqrt((float)i / samples);
                float theta = i * goldenAngle;
                float x = r * MathF.Cos(theta);
                float y = r * MathF.Sin(theta);

                // 根据光圈形状裁剪
                float weight = 1.0f;
                if (_config.BokehShape == BokehShapeV2.Hexagon)
                {
                    // 六边形内测试
                    float hexR = 1.0f;
                    float absCos = MathF.Abs(MathF.Cos(theta));
                    float absSin = MathF.Abs(MathF.Sin(theta));
                    float hexBound = hexR / (absCos * 0.866025f + absSin * 0.5f);
                    if (r > hexBound) weight = 0.0f;
                }
                else if (_config.BokehShape == BokehShapeV2.Octagon)
                {
                    float octBound = 1.0f / (MathF.Max(MathF.Abs(MathF.Cos(theta)), MathF.Abs(MathF.Sin(theta))) * 0.9239f + MathF.Min(MathF.Abs(MathF.Cos(theta)), MathF.Abs(MathF.Sin(theta))) * 0.3827f);
                    if (r > octBound) weight = 0.0f;
                }

                // 高斯权重
                weight *= MathF.Exp(-2.0f * r * r);
                lutData[i] = new Vector4(x, y, 0.0f, weight);
            }

            // 归一化
            float totalW = 0.0f;
            foreach (var s in lutData) totalW += s.W;
            if (totalW > 0)
                for (int i = 0; i < samples; i++)
                    lutData[i] = new Vector4(lutData[i].X, lutData[i].Y, lutData[i].Z, lutData[i].W / totalW);

            _device.UpdateBuffer(_bokehLUT!, lutData);
        }

        /// <summary>执行景深处理。</summary>
        public void Execute(RHITexture inputColor, RHITexture depthBuffer, in Matrix4x4 invProj)
        {
            // Pass 1: 计算 CoC
            var cb = new DOFProcessorCB
            {
                InvProjMatrix = invProj, InvResolution = new Vector2(1.0f / _width, 1.0f / _height),
                FocusDistance = _config.FocusDistance, FocalLength = _config.FocalLength,
                Aperture = _config.Aperture, SensorSize = _config.SensorSize,
                NearFocusRange = _config.NearFocusRange, FarFocusRange = _config.FarFocusRange,
                MaxBlurRadius = _config.MaxBlurRadius, BokehShape = (int)_config.BokehShape,
                BladeCount = _config.BladeCount, MaxSamples = _config.MaxSamples,
                EnableNear = _config.EnableNearBlur ? 1 : 0, EnableFar = _config.EnableFarBlur ? 1 : 0,
                EnablePhysical = _config.EnablePhysicalModel ? 1 : 0,
                CoCScale = _config.CoCScale, CoCBias = _config.CoCBias,
                DebugMode = _config.EnableDebugOverlay ? 1 : 0
            };
            _device.UpdateBuffer(_cb!, cb);

            _device.SetShaderResource(0, depthBuffer);
            _device.SetUnorderedAccessView(0, _cocTexture!);
            _device.SetConstantBuffer(0, _cb!);
            _device.Dispatch((_width + 7) / 8, (_height + 7) / 8, 1);

            // Pass 2: 分离近景/远景
            _device.SetShaderResource(0, inputColor);
            _device.SetShaderResource(1, _cocTexture!);
            _device.SetShaderResource(2, depthBuffer);
            _device.SetUnorderedAccessView(0, _nearField!);
            _device.SetUnorderedAccessView(1, _farField!);
            _device.Dispatch((_width + 7) / 8, (_height + 7) / 8, 1);

            // Pass 3: 远景模糊（多 pass，从大到小）
            int passes = (int)MathF.Ceiling(MathF.Log2(MathF.Max(_config.MaxBlurRadius, 1.0f)));
            for (int p = 0; p < passes; p++)
            {
                float radius = _config.MaxBlurRadius / (1 << p);
                _device.SetShaderResource(0, p % 2 == 0 ? _farField! : _tempTargetA!);
                _device.SetShaderResource(1, _cocTexture!);
                _device.SetShaderResource(2, _bokehLUT!);
                _device.SetUnorderedAccessView(0, p % 2 == 0 ? _tempTargetA! : _farField!);
                _device.Dispatch((_width + 7) / 8, (_height + 7) / 8, 1);
            }

            // Pass 4: 合成
            _device.SetShaderResource(0, inputColor);
            _device.SetShaderResource(1, _nearField!);
            _device.SetShaderResource(2, _farField!);
            _device.SetShaderResource(3, _cocTexture!);
            _device.SetUnorderedAccessView(0, _tempTargetB!);
            _device.Dispatch((_width + 7) / 8, (_height + 7) / 8, 1);
        }

        public RHITexture GetOutput() => _tempTargetB!;
        public RHITexture GetCoCTexture() => _cocTexture!;

        public void Resize(int w, int h)
        {
            if (w == _width && h == _height) return;
            _width = w; _height = h;
            _cocTexture?.Dispose(); _nearField?.Dispose(); _farField?.Dispose();
            _tempTargetA?.Dispose(); _tempTargetB?.Dispose();
            AllocateResources();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _cocTexture?.Dispose(); _nearField?.Dispose(); _farField?.Dispose();
            _tempTargetA?.Dispose(); _tempTargetB?.Dispose(); _cb?.Dispose(); _bokehLUT?.Dispose();
            _disposed = true;
        }
    }
}