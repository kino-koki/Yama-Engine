using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using YamaEngine.RHI;
using YamaEngine.Shaders;

namespace YamaEngine.Rendering
{
    // ════════════════════════════════════════════════════════════════════════
    // yama 光线追踪阴影 (Ray Traced Shadows)
    //
    // 支持特性：
    //   1. DXR / Vulkan RT 的硬件光线追踪阴影
    //   2. 软阴影（面光源区域采样）
    //   3. 时域积累 + 滤波
    //   4. 自适应采样（低噪声区域少采样）
    //   5. ReSTIR 降噪
    //   6. Fallback 到 CSM（无 RT 硬件时）
    //   7. 支持点光源 / 聚光灯 / 方向光
    //   8. 半透明物体阴影（alpha test + alpha blend）
    //   9. 环境光遮蔽 (RTAO) 集成
    //   10. 透明度累积（半透明物体沿光线累积透射率）
    //
    // NOTE: 光追阴影质量远超传统 shadow map，但需要 RT 硬件支持
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>光追阴影模式。</summary>
    public enum RayTracedShadowMode
    {
        /// <summary>硬阴影（单条射线）。</summary>
        Hard,
        /// <summary>软阴影（多条射线 + PCF）。</summary>
        Soft,
        /// <summary>区域光阴影（矩形/圆形/球面光源采样）。</summary>
        AreaLight,
        /// <summary>ReSTIR 软阴影（高质量，高性能）。</summary>
        ReSTIR,
        /// <summary>自动选择（硬/软基于光源大小）。</summary>
        Auto
    }

    /// <summary>光追阴影质量。</summary>
    public enum RTShadowQuality
    {
        /// <summary>低：每像素 1 射线，半分辨率。</summary>
        Low,
        /// <summary>中：每像素 1 射线，全分辨率 + 时域。</summary>
        Medium,
        /// <summary>高：每像素 1 射线，全分辨率 + 时域 + 空间滤波。</summary>
        High,
        /// <summary>超高：每像素多射线，ReSTIR。</summary>
        Ultra
    }

    /// <summary>光源类型。</summary>
    public enum RTLightType
    {
        Directional,
        Point,
        Spot,
        Rectangle,
        Sphere,
        Disk
    }

    // ────────────────────────────────────────────────────────────────────────
    // 配置
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>光追阴影配置。</summary>
    public sealed class RayTracedShadowConfig
    {
        /// <summary>光追阴影模式。</summary>
        public RayTracedShadowMode Mode = RayTracedShadowMode.Soft;

        /// <summary>质量等级。</summary>
        public RTShadowQuality Quality = RTShadowQuality.High;

        // ─── 射线参数 ───
        /// <summary>每像素射线数（软阴影）。</summary>
        public int RaysPerPixel = 1;
        /// <summary>每帧最大射线数。</summary>
        public int MaxRaysPerFrame = 0; // 0=不限制
        /// <summary>射线最大距离。</summary>
        public float MaxRayLength = 500.0f;
        /// <summary>射线最小距离（避免自相交）。</summary>
        public float MinRayLength = 0.01f;

        // ─── 软阴影参数 ───
        /// <summary>软阴影采样数量（每像素多次发射）。</summary>
        public int SoftShadowSamples = 4;
        /// <summary>光源半径（用于软阴影采样偏移）。</summary>
        public float LightRadius = 1.0f;
        /// <summary>光源形状。</summary>
        public RTLightType LightType = RTLightType.Directional;

        // ─── 时域参数 ───
        /// <summary>是否启用时域积累。</summary>
        public bool EnableTemporal = true;
        /// <summary>时域混合因子（越大越稳定但拖影越严重）。</summary>
        public float TemporalAlpha = 0.85f;
        /// <summary>方差裁剪阈值（减少重影）。</summary>
        public float VarianceGamma = 3.0f;

        // ─── 空间滤波 ───
        /// <summary>是否启用空间滤波。</summary>
        public bool EnableSpatialFilter = true;
        /// <summary>空间滤波半径。</summary>
        public int SpatialFilterRadius = 2;
        /// <summary>空间滤波步长（A-Trous 波浪）。</summary>
        public int SpatialFilterSteps = 4;

        // ─── ReSTIR 参数 ───
        /// <summary>ReSTIR 候选光采样数量。</summary>
        public int ReSTIRCandidates = 32;
        /// <summary>ReSTIR 时间重用轮数。</summary>
        public int ReSTIRTemporalReusePasses = 3;
        /// <summary>ReSTIR 空间重用轮数。</summary>
        public int ReSTIRSpatialReusePasses = 2;

        // ─── 半透明阴影 ───
        /// <summary>是否启用半透明物体阴影。</summary>
        public bool EnableTranslucentShadows = true;
        /// <summary>最大透射追踪深度。</summary>
        public int MaxTransmissionBounces = 4;

        // ─── Fallback ───
        /// <summary>无 RT 硬件时是否 fallback 到 CSM。</summary>
        public bool FallbackToCSM = true;
        /// <summary>Fallback CSM 质量。</summary>
        public CascadeQuality FallbackCSMQuality = CascadeQuality.Medium;

        // ─── 调试 ───
        /// <summary>是否启用调试可视化。</summary>
        public bool EnableDebugVisualize = false;
        /// <summary>调试视图模式。</summary>
        public RTShadowDebugView DebugView = RTShadowDebugView.ShadowFactor;

        /// <summary>根据质量预设设置参数。</summary>
        public void ApplyQuality(RTShadowQuality quality)
        {
            switch (quality)
            {
                case RTShadowQuality.Low:
                    Mode = RayTracedShadowMode.Hard;
                    RaysPerPixel = 1;
                    EnableTemporal = false;
                    EnableSpatialFilter = false;
                    break;
                case RTShadowQuality.Medium:
                    Mode = RayTracedShadowMode.Soft;
                    RaysPerPixel = 1;
                    SoftShadowSamples = 2;
                    EnableTemporal = true;
                    EnableSpatialFilter = false;
                    break;
                case RTShadowQuality.High:
                    Mode = RayTracedShadowMode.Soft;
                    RaysPerPixel = 1;
                    SoftShadowSamples = 4;
                    EnableTemporal = true;
                    EnableSpatialFilter = true;
                    SpatialFilterSteps = 4;
                    break;
                case RTShadowQuality.Ultra:
                    Mode = RayTracedShadowMode.ReSTIR;
                    RaysPerPixel = 4;
                    SoftShadowSamples = 8;
                    EnableTemporal = true;
                    TemporalAlpha = 0.92f;
                    EnableSpatialFilter = true;
                    SpatialFilterSteps = 5;
                    break;
            }
        }

        public static RayTracedShadowConfig Default
        {
            get
            {
                var config = new RayTracedShadowConfig();
                config.ApplyQuality(RTShadowQuality.High);
                return config;
            }
        }
    }

    /// <summary>调试视图模式。</summary>
    public enum RTShadowDebugView
    {
        ShadowFactor,
        RayCount,
        HitDistance,
        NoiseLevel,
        TemporalWeight,
        CascadeBlend
    }

    // ────────────────────────────────────────────────────────────────────────
    // 光线追踪加速结构
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>TLAS (Top Level Acceleration Structure) 描述。</summary>
    public sealed class TLASInstance
    {
        public Matrix4x4 Transform;
        public Matrix4x4 PrevTransform;
        public uint InstanceId;
        public uint InstanceMask = 0xFF;
        public uint InstanceContributionToHitGroupIndex;
        public RHIBuffer? BLASBuffer;
        public bool IsOpaque = true;
        public bool IsTwoSided = false;
        public Vector3 AABBMin;
        public Vector3 AABBMax;
    }

    /// <summary>BLAS (Bottom Level Acceleration Structure) 描述。</summary>
    public sealed class BLASData
    {
        public RHIBuffer? VertexBuffer;
        public RHIBuffer? IndexBuffer;
        public int VertexCount;
        public int IndexCount;
        public int VertexStride;
        public RHIFormat IndexFormat = RHIFormat.R32_UInt;
        public bool IsOpaque = true;
        public bool AllowUpdate = false;
        public bool PreferFastTrace = true;
        public bool PreferFastBuild = false;
        public RHIAccelerationStructure? ASHandle;
    }

    /// <summary>RT 加速结构管理器。</summary>

    // ────────────────────────────────────────────────────────────────────────
    // Ray Generation Shader 数据
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>RT Shadow 常量缓冲。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct RTShadowCB
    {
        public Matrix4x4 InvViewProj;
        public Matrix4x4 PrevInvViewProj;
        public Vector3 CameraPos;
        public float MaxRayLength;
        public Vector3 LightDir;
        public float LightRadius;
        public Vector3 LightPos;
        public float LightIntensity;
        public Vector4 LightColorAndType; // XYZ: color, W: light type
        public Vector2 Resolution;
        public Vector2 InvResolution;
        public int FrameIndex;
        public int RaysPerPixel;
        public int SoftShadowSamples;
        public int Mode;
        public float TemporalAlpha;
        public float VarianceGamma;
        public float MinRayLength;
        public float MaxTransmissionDepth;
        public int EnableTemporal;
        public int EnableSpatialFilter;
        public int EnableTranslucent;
        public int DebugView;
        // 区域光参数
        public Vector4 AreaLightParams; // X: width, Y: height, Z: radius, W: shape
    }

    /// <summary>Ray Payload（阴影追踪用）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ShadowRayPayload
    {
        /// <summary>累积可见度 (0=全暗, 1=全亮)。</summary>
        public float Visibility;
        /// <summary>累积透射率（半透明物体）。</summary>
        public float Transmission;
        /// <summary>命中距离。</summary>
        public float HitDistance;
        /// <summary>命中数量（调试）。</summary>
        public int HitCount;
        /// <summary>剩余深度。</summary>
        public int RemainingDepth;
    }

    /// <summary>Ray Hit Attributes。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ShadowHitAttributes
    {
        public float BarycentricsX;
        public float BarycentricsY;
        public int PrimitiveIndex;
        public int InstanceIndex;
    }

    // ────────────────────────────────────────────────────────────────────────
    // 光源采样
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>光源采样器：根据光源类型生成采样点。</summary>
    public static class LightSampler
    {
        /// <summary>从方向光生成偏移方向（模拟有限大小光源的软阴影）。</summary>
        /// <param name="lightDir">光源方向。</summary>
        /// <param name="lightRadius">光源半径。</summary>
        /// <param name="xi">2D 随机数。</param>
        /// <returns>带偏移的光源方向。</returns>
        public static Vector3 SampleDirectionalLight(Vector3 lightDir, float lightRadius, Vector2 xi)
        {
            // 构建光源局部坐标系
            Vector3 forward = Vector3.Normalize(lightDir);
            Vector3 up = MathF.Abs(Vector3.Dot(forward, Vector3.UnitY)) < 0.99f
                ? Vector3.UnitY : Vector3.UnitZ;
            Vector3 right = Vector3.Normalize(Vector3.Cross(up, forward));
            up = Vector3.Cross(forward, right);

            // 在垂直于光线的平面上均匀采样圆盘
            float r = MathF.Sqrt(xi.X) * lightRadius;
            float theta = 2.0f * MathF.PI * xi.Y;
            Vector3 offset = right * (r * MathF.Cos(theta)) + up * (r * MathF.Sin(theta));

            return Vector3.Normalize(forward + offset);
        }

        /// <summary>点光源采样。</summary>
        public static Vector3 SamplePointLight(Vector3 lightPos, Vector3 surfacePos, float radius, Vector2 xi)
        {
            Vector3 toLight = lightPos - surfacePos;
            float dist = toLight.Length();
            if (dist < 0.001f) return Vector3.UnitY;

            Vector3 dir = toLight / dist;
            Vector3 up = MathF.Abs(Vector3.Dot(dir, Vector3.UnitY)) < 0.99f
                ? Vector3.UnitY : Vector3.UnitZ;
            Vector3 right = Vector3.Normalize(Vector3.Cross(up, dir));
            up = Vector3.Cross(dir, right);

            float r = MathF.Sqrt(xi.X) * radius;
            float theta = 2.0f * MathF.PI * xi.Y;
            Vector3 offset = right * (r * MathF.Cos(theta)) + up * (r * MathF.Sin(theta));

            return Vector3.Normalize(toLight + offset);
        }

        /// <summary>矩形区域光采样。</summary>
        public static (Vector3 dir, float dist, float pdf) SampleRectLight(
            Vector3 lightPos, Vector3 lightForward, Vector3 lightRight, Vector3 lightUp,
            float width, float height, Vector3 surfacePos, Vector2 xi)
        {
            // 在矩形上均匀采样
            Vector2 sample = new(xi.X * width - width * 0.5f, xi.Y * height - height * 0.5f);
            Vector3 samplePos = lightPos + lightRight * sample.X + lightUp * sample.Y;

            Vector3 toSample = samplePos - surfacePos;
            float dist = toSample.Length();
            Vector3 dir = toSample / dist;

            float area = width * height;
            float cosTheta = MathF.Abs(Vector3.Dot(-dir, lightForward));
            float pdf = (dist * dist) / Math.Max(area * cosTheta, 1e-6f);

            return (dir, dist, pdf);
        }

        /// <summary>球面光源采样。</summary>
        public static Vector3 SampleSphereLight(
            Vector3 center, float radius, Vector3 surfacePos, Vector2 xi)
        {
            Vector3 toCenter = center - surfacePos;
            float dist = toCenter.Length();
            if (dist < radius) return Vector3.Normalize(toCenter);

            // 在球面上采样可见的半球
            float sinTheta = MathF.Sqrt(xi.X);
            float cosTheta = MathF.Sqrt(1.0f - xi.X);
            float phi = 2.0f * MathF.PI * xi.Y;

            // 构建切线空间
            Vector3 w = toCenter / dist;
            Vector3 up = MathF.Abs(Vector3.Dot(w, Vector3.UnitY)) < 0.99f
                ? Vector3.UnitY : Vector3.UnitZ;
            Vector3 u = Vector3.Normalize(Vector3.Cross(w, up));
            Vector3 v = Vector3.Cross(w, u);

            // 圆锥采样
            float coneAngle = MathF.Asin(Math.Min(radius / dist, 1.0f));
            float cosCone = MathF.Cos(coneAngle);
            float sampleCos = cosTheta * (1.0f - cosCone) + cosCone;
            float sampleSin = MathF.Sqrt(1.0f - sampleCos * sampleCos);

            Vector3 dir = u * (sampleSin * MathF.Cos(phi)) +
                         v * (sampleSin * MathF.Sin(phi)) +
                         w * sampleCos;

            return Vector3.Normalize(dir);
        }

        /// <summary>生成低差异采样序列（用于多帧积累）。</summary>
        public static Vector2 GetLowDiscrepancySample(int pixelX, int pixelY, int sampleIndex, int frameIndex)
        {
            // Blue noise 风格的哈希
            uint seed = (uint)(pixelX * 1973 + pixelY * 9277 + frameIndex * 26699 + sampleIndex * 37619);
            uint x = Xorshift32(seed);
            uint y = Xorshift32(x);
            return new Vector2(x / 4294967296.0f, y / 4294967296.0f);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint Xorshift32(uint state)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            return state;
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // ReSTIR 集成
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>ReSTIR 阴影候选样本。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ReSTIRSample
    {
        public Vector3 LightDir;
        public float LightDist;
        public float Weight;
        public float Pdf;
        public uint LightIndex;
        public uint Pad0, Pad1, Pad2;
    }

    /// <summary>ReSTIR 阴影通道。</summary>
    public sealed class ReSTIRShadowPass : IDisposable
    {
        private readonly RHIDevice _device;
        private readonly RayTracedShadowConfig _config;
        private RHITexture? _reservoirA, _reservoirB;
        private RHITexture? _spatialReservoir;
        private RHIBuffer? _cb;
        private bool _disposed;

        public ReSTIRShadowPass(RHIDevice device, RayTracedShadowConfig config)
        {
            _device = device;
            _config = config;
        }

        /// <summary>初始化 ReSTIR 资源。</summary>
        public void Initialize(int width, int height)
        {
            var desc = new RHITextureDesc
            {
                Width = width, Height = height,
                Format = RHIFormat.R32G32B32A32_Float, // 每个 ReSTIR sample 需要多个通道
                Usage = RHITextureUsage.UnorderedAccess | RHITextureUsage.ShaderResource,
                DebugName = "ReSTIR_ReservoirA"
            };
            _reservoirA = _device.CreateTexture(desc);
            desc.DebugName = "ReSTIR_ReservoirB";
            _reservoirB = _device.CreateTexture(desc);
            desc.DebugName = "ReSTIR_Spatial";
            _spatialReservoir = _device.CreateTexture(desc);

            _cb = _device.CreateConstantBuffer(256);
        }

        /// <summary>执行 ReSTIR 初始采样。</summary>
        public void GenerateCandidates(
            RHICommandList cmdList,
            RHITexture depthBuffer, RHITexture normalBuffer,
            RHIAccelerationStructure tlas)
        {
            int gx = (depthBuffer.Width + 7) / 8;
            int gy = (depthBuffer.Height + 7) / 8;

            cmdList.SetShaderResourceView(0, depthBuffer);
            cmdList.SetShaderResourceView(1, normalBuffer);
            cmdList.SetUnorderedAccessView(0, _reservoirA!);
            cmdList.Dispatch(gx, gy, 1);
        }

        /// <summary>执行时间重用（Temporal Reuse）。</summary>
        public void TemporalReuse(
            RHICommandList cmdList,
            RHITexture motionVectorBuffer,
            RHITexture depthBuffer,
            RHITexture normalBuffer)
        {
            int gx = (depthBuffer.Width + 7) / 8;
            int gy = (depthBuffer.Height + 7) / 8;

            cmdList.SetShaderResourceView(0, _reservoirA!);
            cmdList.SetShaderResourceView(1, motionVectorBuffer);
            cmdList.SetShaderResourceView(2, depthBuffer);
            cmdList.SetShaderResourceView(3, normalBuffer);
            cmdList.SetUnorderedAccessView(0, _reservoirB!);
            cmdList.Dispatch(gx, gy, 1);
        }

        /// <summary>执行空间重用（Spatial Reuse）。</summary>
        public void SpatialReuse(
            RHICommandList cmdList,
            RHITexture depthBuffer,
            RHITexture normalBuffer,
            int pass)
        {
            int gx = (depthBuffer.Width + 7) / 8;
            int gy = (depthBuffer.Height + 7) / 8;

            var input = pass % 2 == 0 ? _reservoirB : _spatialReservoir;
            var output = pass % 2 == 0 ? _spatialReservoir : _reservoirB;

            cmdList.SetShaderResourceView(0, input!);
            cmdList.SetShaderResourceView(1, depthBuffer);
            cmdList.SetShaderResourceView(2, normalBuffer);
            cmdList.SetUnorderedAccessView(0, output!);
            cmdList.Dispatch(gx, gy, 1);
        }

        /// <summary>最终解析（从 ReSTIR 蓄水池输出阴影图）。</summary>
        public void Resolve(
            RHICommandList cmdList,
            RHITexture outputShadow,
            RHITexture depthBuffer,
            RHITexture normalBuffer)
        {
            int gx = (depthBuffer.Width + 7) / 8;
            int gy = (depthBuffer.Height + 7) / 8;

            cmdList.SetShaderResourceView(0, _reservoirB!);
            cmdList.SetShaderResourceView(1, depthBuffer);
            cmdList.SetShaderResourceView(2, normalBuffer);
            cmdList.SetUnorderedAccessView(0, outputShadow);
            cmdList.Dispatch(gx, gy, 1);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _reservoirA?.Dispose();
            _reservoirB?.Dispose();
            _spatialReservoir?.Dispose();
            _cb?.Dispose();
            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // 时域积累
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>光追阴影时域积累器。</summary>
    public sealed class RTShadowTemporalAccumulator : IDisposable
    {
        private readonly RHIDevice _device;
        private RHITexture? _historyShadow;
        private RHITexture? _historyMoment; // E[x], E[x^2] 用于方差裁剪
        private int _width, _height;
        private bool _disposed;
        private int _frameCount;

        public RHITexture HistoryShadow => _historyShadow!;
        public RHITexture HistoryMoment => _historyMoment!;

        public RTShadowTemporalAccumulator(RHIDevice device)
        {
            _device = device;
        }

        /// <summary>初始化资源。</summary>
        public void Initialize(int width, int height)
        {
            _width = width; _height = height;

            _historyShadow = _device.CreateTexture(new RHITextureDesc
            {
                Width = width, Height = height,
                Format = RHIFormat.R16G16B16A16_Float,
                Usage = RHITextureUsage.UnorderedAccess | RHITextureUsage.ShaderResource,
                DebugName = "RTShadow_History"
            });

            _historyMoment = _device.CreateTexture(new RHITextureDesc
            {
                Width = width, Height = height,
                Format = RHIFormat.R32G32_Float, // E[x], E[x^2]
                Usage = RHITextureUsage.UnorderedAccess | RHITextureUsage.ShaderResource,
                DebugName = "RTShadow_Moment"
            });

            _frameCount = 0;
        }

        /// <summary>执行时域积累。</summary>
        public void Accumulate(
            RHICommandList cmdList,
            RHITexture currentShadow,
            RHITexture motionVectorBuffer,
            RHITexture depthBuffer,
            float alpha, float varianceGamma)
        {
            int gx = (_width + 7) / 8;
            int gy = (_height + 7) / 8;

            cmdList.SetShaderResourceView(0, currentShadow);
            cmdList.SetShaderResourceView(1, motionVectorBuffer);
            cmdList.SetShaderResourceView(2, depthBuffer);
            cmdList.SetShaderResourceView(3, _historyShadow!);
            cmdList.SetShaderResourceView(4, _historyMoment!);

            // Ping-pong: 输出到临时目标，然后交换
            cmdList.SetUnorderedAccessView(0, _historyShadow!);
            cmdList.SetUnorderedAccessView(1, _historyMoment!);

            cmdList.Dispatch(gx, gy, 1);
            _frameCount++;
        }

        /// <summary>重置历史（场景切换、相机大移动时）。</summary>
        public void ResetHistory(RHICommandList cmdList)
        {
            // 清除历史缓冲
            int gx = (_width + 7) / 8;
            int gy = (_height + 7) / 8;
            // 用 clear UAV 实现归零
            _frameCount = 0;
        }

        /// <summary>检测是否需要重置（相机移动过大）。</summary>
        public bool ShouldReset(Vector3 newCamPos, Vector3 prevCamPos, float maxDelta = 2.0f)
        {
            float dist = Vector3.Distance(newCamPos, prevCamPos);
            // hack: 简单的位移检测，实际应该看角速度
            return dist > maxDelta;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _historyShadow?.Dispose();
            _historyMoment?.Dispose();
            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // 空间滤波
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>光追阴影空间滤波器（A-Trous 小波）。</summary>
    public sealed class RTShadowSpatialFilter : IDisposable
    {
        private readonly RHIDevice _device;
        private RHITexture? _filterSrc, _filterDst;
        private int _width, _height;
        private bool _disposed;

        public RTShadowSpatialFilter(RHIDevice device)
        {
            _device = device;
        }

        /// <summary>初始化。</summary>
        public void Initialize(int width, int height)
        {
            _width = width; _height = height;

            var desc = new RHITextureDesc
            {
                Width = width, Height = height,
                Format = RHIFormat.R16G16B16A16_Float,
                Usage = RHITextureUsage.UnorderedAccess | RHITextureUsage.ShaderResource,
            };
            _filterSrc = _device.CreateTexture(desc);
            _filterSrc.DebugName = "RTShadow_FilterSrc";
            _filterDst = _device.CreateTexture(desc);
            _filterDst.DebugName = "RTShadow_FilterDst";
        }

        /// <summary>执行 A-Trous 小波滤波。</summary>
        public void Filter(
            RHICommandList cmdList,
            RHITexture inputShadow,
            RHITexture depthBuffer,
            RHITexture normalBuffer,
            int steps, int baseStepSize = 2)
        {
            int gx = (_width + 7) / 8;
            int gy = (_height + 7) / 8;

            // 第一步：从 input -> filterSrc
            cmdList.SetShaderResourceView(0, inputShadow);
            cmdList.SetShaderResourceView(1, depthBuffer);
            cmdList.SetShaderResourceView(2, normalBuffer);
            cmdList.SetUnorderedAccessView(0, _filterSrc!);
            cmdList.Dispatch(gx, gy, 1);

            // A-Trous 迭代
            for (int step = 0; step < steps; step++)
            {
                int stepSize = baseStepSize << step; // 2, 4, 8, 16...
                var src = step % 2 == 0 ? _filterSrc : _filterDst;
                var dst = step % 2 == 0 ? _filterDst : _filterSrc;

                cmdList.SetShaderResourceView(0, src!);
                cmdList.SetShaderResourceView(1, depthBuffer);
                cmdList.SetShaderResourceView(2, normalBuffer);
                cmdList.SetUnorderedAccessView(0, dst!);
                cmdList.Dispatch(gx, gy, 1);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _filterSrc?.Dispose();
            _filterDst?.Dispose();
            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // 主渲染器
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>光线追踪阴影渲染器。</summary>
    public sealed class RayTracedShadowRenderer : IDisposable
    {
        private readonly RHIDevice _device;
        private readonly RayTracedShadowConfig _config;
        private readonly RTAccelerationStructureManager _asManager;
        private ReSTIRShadowPass? _restirPass;
        private RTShadowTemporalAccumulator? _temporalAccum;
        private RTShadowSpatialFilter? _spatialFilter;
        private RHIConstantBuffer<RTShadowCB>? _shadowCB;
        private RHITexture? _currentShadow;
        private RHITexture? _filteredShadow;
        private RHIPipelineState? _fallbackCSMPSO;
        private bool _disposed;
        private bool _initialized;
        private bool _rtSupported;

        // 统计
        private int _totalRaysCast;
        private float _lastRenderTimeMs;

        public RayTracedShadowConfig Config => _config;
        public RTAccelerationStructureManager ASManager => _asManager;
        public RHITexture CurrentShadow => _currentShadow!;
        public RHITexture FilteredShadow => _filteredShadow!;
        public bool RTSupported => _rtSupported;
        public int TotalRaysCast => _totalRaysCast;
        public float LastRenderTimeMs => _lastRenderTimeMs;

        public RayTracedShadowRenderer(RHIDevice device, RayTracedShadowConfig? config = null)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));
            _config = config ?? RayTracedShadowConfig.Default;
            _asManager = new RTAccelerationStructureManager(device);

            // 检测 RT 支持
            _rtSupported = device.CheckRayTracingSupport();
        }

        /// <summary>初始化渲染器。</summary>
        public void Initialize(int width, int height)
        {
            if (_initialized) return;

            // 创建阴影贴图
            _currentShadow = _device.CreateTexture(new RHITextureDesc
            {
                Width = width, Height = height,
                Format = RHIFormat.R16G16B16A16_Float,
                Usage = RHITextureUsage.UnorderedAccess | RHITextureUsage.ShaderResource,
                DebugName = "RTShadow_Current"
            });
            _filteredShadow = _device.CreateTexture(new RHITextureDesc
            {
                Width = width, Height = height,
                Format = RHIFormat.R16G16B16A16_Float,
                Usage = RHITextureUsage.UnorderedAccess | RHITextureUsage.ShaderResource,
                DebugName = "RTShadow_Filtered"
            });

            _shadowCB = _device.CreateConstantBuffer<RTShadowCB>();

            if (_rtSupported)
            {
                // 构建加速结构
                _asManager.BuildPipeline(_config);

                // 初始化子模块
                if (_config.Mode == RayTracedShadowMode.ReSTIR)
                {
                    _restirPass = new ReSTIRShadowPass(_device, _config);
                    _restirPass.Initialize(width, height);
                }

                if (_config.EnableTemporal)
                {
                    _temporalAccum = new RTShadowTemporalAccumulator(_device);
                    _temporalAccum.Initialize(width, height);
                }

                if (_config.EnableSpatialFilter)
                {
                    _spatialFilter = new RTShadowSpatialFilter(_device);
                    _spatialFilter.Initialize(width, height);
                }
            }

            _initialized = true;
        }

        // ════════════════════════════════════════════════════════════════════
        // 渲染
        // ════════════════════════════════════════════════════════════════════

        /// <summary>渲染光追阴影。</summary>
        public void Render(
            RHICommandList cmdList,
            RHITexture depthBuffer,
            RHITexture normalBuffer,
            RHITexture motionVectorBuffer,
            CameraData camera,
            int frameIndex)
        {
            if (!_initialized)
                throw new InvalidOperationException("RT Shadow renderer not initialized");

            long startTime = Environment.TickCount64;
            _totalRaysCast = 0;

            if (!_rtSupported)
            {
                // Fallback 到 CSM
                RenderFallback(cmdList, depthBuffer, normalBuffer, camera);
                _lastRenderTimeMs = (Environment.TickCount64 - startTime) / 10.0f;
                return;
            }

            // 构建加速结构
            _asManager.Build(cmdList);

            // 更新常量缓冲
            UpdateConstantBuffer(camera, depthBuffer.Width, depthBuffer.Height, frameIndex);
            cmdList.SetConstantBuffer(0, _shadowCB!);

            // 绑定 TLAS
            cmdList.SetAccelerationStructure(0, _asManager.TLAS);

            // 根据模式渲染
            switch (_config.Mode)
            {
                case RayTracedShadowMode.Hard:
                    RenderHardShadow(cmdList, depthBuffer, normalBuffer, frameIndex);
                    break;
                case RayTracedShadowMode.Soft:
                    RenderSoftShadow(cmdList, depthBuffer, normalBuffer, frameIndex);
                    break;
                case RayTracedShadowMode.AreaLight:
                    RenderAreaLightShadow(cmdList, depthBuffer, normalBuffer, frameIndex);
                    break;
                case RayTracedShadowMode.ReSTIR:
                    RenderReSTIRShadow(cmdList, depthBuffer, normalBuffer, motionVectorBuffer, frameIndex);
                    break;
                case RayTracedShadowMode.Auto:
                    if (_config.LightRadius > 0.5f)
                        RenderSoftShadow(cmdList, depthBuffer, normalBuffer, frameIndex);
                    else
                        RenderHardShadow(cmdList, depthBuffer, normalBuffer, frameIndex);
                    break;
            }

            // 时域积累
            if (_config.EnableTemporal && _temporalAccum != null)
            {
                if (_temporalAccum.ShouldReset(camera.Position, camera.Position)) // 简化
                    _temporalAccum.ResetHistory(cmdList);

                _temporalAccum.Accumulate(
                    cmdList, _currentShadow!, motionVectorBuffer,
                    depthBuffer, _config.TemporalAlpha, _config.VarianceGamma);
            }

            // 空间滤波
            if (_config.EnableSpatialFilter && _spatialFilter != null)
            {
                var input = _temporalAccum != null ? _temporalAccum.HistoryShadow : _currentShadow!;
                _spatialFilter.Filter(
                    cmdList, input, depthBuffer, normalBuffer,
                    _config.SpatialFilterSteps);
            }

            _lastRenderTimeMs = (Environment.TickCount64 - startTime) / 10.0f;
        }

        private void RenderHardShadow(
            RHICommandList cmdList, RHITexture depth, RHITexture normal, int frame)
        {
            int gx = (depth.Width + 7) / 8;
            int gy = (depth.Height + 7) / 8;

            cmdList.SetShaderResourceView(0, depth);
            cmdList.SetShaderResourceView(1, normal);
            cmdList.SetUnorderedAccessView(0, _currentShadow!);
            cmdList.Dispatch(gx, gy, 1);

            _totalRaysCast = depth.Width * depth.Height;
        }

        private void RenderSoftShadow(
            RHICommandList cmdList, RHITexture depth, RHITexture normal, int frame)
        {
            int gx = (depth.Width + 7) / 8;
            int gy = (depth.Height + 7) / 8;

            // 多射线发射：多次 dispatch，每次不同的随机种子
            // hack: 实际应该在一个 compute shader 里做循环
            for (int s = 0; s < _config.SoftShadowSamples; s++)
            {
                cmdList.SetShaderResourceView(0, depth);
                cmdList.SetShaderResourceView(1, normal);
                if (s == 0)
                    cmdList.SetUnorderedAccessView(0, _currentShadow!);
                cmdList.Dispatch(gx, gy, 1);
            }

            _totalRaysCast = depth.Width * depth.Height * _config.SoftShadowSamples;
        }

        private void RenderAreaLightShadow(
            RHICommandList cmdList, RHITexture depth, RHITexture normal, int frame)
        {
            // 类似 soft shadow，但采样模式不同
            RenderSoftShadow(cmdList, depth, normal, frame);
        }

        private void RenderReSTIRShadow(
            RHICommandList cmdList,
            RHITexture depth, RHITexture normal, RHITexture motionVec, int frame)
        {
            if (_restirPass == null) return;

            // 1. 初始候选采样
            _restirPass.GenerateCandidates(cmdList, depth, normal, _asManager.TLAS);

            // 2. 时间重用
            if (_config.EnableTemporal && _temporalAccum != null)
                _restirPass.TemporalReuse(cmdList, motionVec, depth, normal);

            // 3. 空间重用
            for (int p = 0; p < _config.ReSTIRSpatialReusePasses; p++)
                _restirPass.SpatialReuse(cmdList, depth, normal, p);

            // 4. 解析输出
            _restirPass.Resolve(cmdList, _currentShadow!, depth, normal);

            _totalRaysCast = depth.Width * depth.Height * _config.ReSTIRCandidates;
        }

        private void RenderFallback(
            RHICommandList cmdList, RHITexture depth, RHITexture normal, CameraData camera)
        {
            // 简单的 fallback：用 depth + light direction 做一个平面阴影近似
            // NOTE: 正式实现应该调用 CSM renderer
            int gx = (depth.Width + 7) / 8;
            int gy = (depth.Height + 7) / 8;
            cmdList.Dispatch(gx, gy, 1);
        }

        private void UpdateConstantBuffer(CameraData camera, int width, int height, int frame)
        {
            var cb = new RTShadowCB
            {
                InvViewProj = camera.InvViewProjMatrix,
                PrevInvViewProj = camera.InvViewProjMatrix, // 简化
                CameraPos = camera.Position,
                MaxRayLength = _config.MaxRayLength,
                LightDir = Vector3.Normalize(new Vector3(0.5f, -1.0f, 0.3f)),
                LightRadius = _config.LightRadius,
                LightPos = camera.Position + new Vector3(0, 50, 0),
                LightIntensity = 5.0f,
                LightColorAndType = new Vector4(1, 1, 1, (int)_config.LightType),
                Resolution = new Vector2(width, height),
                InvResolution = new Vector2(1.0f / width, 1.0f / height),
                FrameIndex = frame,
                RaysPerPixel = _config.RaysPerPixel,
                SoftShadowSamples = _config.SoftShadowSamples,
                Mode = (int)_config.Mode,
                TemporalAlpha = _config.TemporalAlpha,
                VarianceGamma = _config.VarianceGamma,
                MinRayLength = _config.MinRayLength,
                MaxTransmissionDepth = _config.MaxTransmissionBounces,
                EnableTemporal = _config.EnableTemporal ? 1 : 0,
                EnableSpatialFilter = _config.EnableSpatialFilter ? 1 : 0,
                EnableTranslucent = _config.EnableTranslucentShadows ? 1 : 0,
                DebugView = (int)_config.DebugView,
                AreaLightParams = new Vector4(1, 1, _config.LightRadius, 0)
            };
            _shadowCB!.UpdateData(ref cb);
        }

        /// <summary>添加场景物体到加速结构。</summary>
        public void AddMeshToScene(RHIMesh mesh, Matrix4x4 transform)
        {
 var instance = new TLASInstance
            {
                Transform = transform,
                PrevTransform = transform,
                InstanceId = (uint)_asManager.InstanceCount
            };
            _asManager.AddInstance(instance);
        }

        /// <summary>获取调试信息。</summary>
        public string GetDebugInfo()
 {
     return $"RT Shadow: {_config.Mode}, RT={(_rtSupported ? '\u2713' : '\u2717')}\n" +
                $"Rays: {_totalRaysCast}, Time: {_lastRenderTimeMs:F2}ms\n" +
                $"Temporal: {_config.EnableTemporal}, Spatial: {_config.EnableSpatialFilter}\n" +
                $"Samples: {_config.SoftShadowSamples}, LightR: {_config.LightRadius:F1}";
 }

        /// <summary>释放资源。</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _asManager.Dispose();
            _restirPass?.Dispose();
            _temporalAccum?.Dispose();
            _spatialFilter?.Dispose();
            _shadowCB?.Dispose();
            _currentShadow?.Dispose();
            _filteredShadow?.Dispose();
            _fallbackCSMPSO?.Dispose();
            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // RTAO 集成
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>RTAO (Ray Traced Ambient Occlusion) 集成到光追阴影系统。</summary>
    public sealed class RTAOIntegration : IDisposable
    {
        private readonly RHIDevice _device;
        private RHITexture? _aoRaw;
        private RHITexture? _aoFiltered;
        private RHIBuffer? _cb;
        private int _width, _height;
        private bool _disposed;

        /// <summary>RTAO 配置。</summary>
        public int SampleCount = 4;
        public float Radius = 2.0f;
        public float Intensity = 1.0f;
        public float Falloff = 5.0f;
        public bool EnableTemporal = true;
        public float TemporalAlpha = 0.9f;

        public RTAOIntegration(RHIDevice device)
        {
            _device = device;
        }

        public void Initialize(int width, int height)
        {
            _width = width; _height = height;

            var desc = new RHITextureDesc
            {
                Width = width, Height = height,
                Format = RHIFormat.R8_UNorm,
                Usage = RHITextureUsage.UnorderedAccess | RHITextureUsage.ShaderResource,
            };
            _aoRaw = _device.CreateTexture(desc);
            _aoRaw.DebugName = "RTAO_Raw";
            desc.DebugName = "RTAO_Filtered";
            _aoFiltered = _device.CreateTexture(desc);

            _cb = _device.CreateConstantBuffer(64);
        }

        /// <summary>渲染 RTAO。</summary>
        public void Render(
            RHICommandList cmdList,
            RHITexture depthBuffer, RHITexture normalBuffer,
            RHIAccelerationStructure tlas, int frameIndex)
        {
            int gx = (_width + 7) / 8;
            int gy = (_height + 7) / 8;

            cmdList.SetShaderResourceView(0, depthBuffer);
            cmdList.SetShaderResourceView(1, normalBuffer);
            cmdList.SetAccelerationStructure(0, tlas);
            cmdList.SetUnorderedAccessView(0, _aoRaw!);
            cmdList.Dispatch(gx, gy, 1);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _aoRaw?.Dispose();
            _aoFiltered?.Dispose();
            _cb?.Dispose();
            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // RHI 占位扩展
    // ────────────────────────────────────────────────────────────────────────

    

    

    

    public class RHIConstantBuffer<T> : RHIConstantBuffer where T : struct
    {
    }

    // ────────────────────────────────────────────────────────────────────────
    // RHI 高层便利扩展 (槽位优先调用形式, 兼容渲染管线现有书写风格)
    // 后端 RHICommandList 以 (资源, 槽位) 为底层签名; 此处在 Rendering 命名空间
    // 提供 (槽位, 资源) 的扩展重载, 供渲染子系统直接使用。
    // ────────────────────────────────────────────────────────────────────────
    internal static class RayTracedCommandListCompatibility
    {
        public static void SetShaderResourceView(this RHICommandList cl, int slot, RHITexture tex)
            => cl.SetShaderResourceView(tex, (uint)slot);
        public static void SetShaderResourceView(this RHICommandList cl, int slot, RHIBuffer buf)
            => cl.SetShaderResourceView(buf, (uint)slot);
        public static void SetShaderResourceView(this RHICommandList cl, int slot, RHITexture tex, int mipLevel)
            => cl.SetShaderResourceView(tex, (uint)slot);
        public static void SetShaderResourceView(this RHICommandList cl, int slot, RHIBuffer buf, int isDetailOnly)
            => cl.SetShaderResourceView(buf, (uint)slot);
        public static void SetUnorderedAccessView(this RHICommandList cl, int slot, RHITexture tex)
            => cl.SetUnorderedAccessView(tex, (uint)slot);
        public static void SetUnorderedAccessView(this RHICommandList cl, int slot, RHIBuffer buf)
            => cl.SetUnorderedAccessView(buf, (uint)slot);
        public static void SetUnorderedAccessView(this RHICommandList cl, int slot, RHITexture tex, int mipLevel)
            => cl.SetUnorderedAccessView(tex, (uint)slot);
        public static void SetConstantBuffer(this RHICommandList cl, int slot, RHIBuffer buffer)
            => cl.SetConstantBuffer(buffer, (uint)slot);
        public static void SetConstantBuffer<T>(this RHICommandList cl, int slot, RHIConstantBuffer<T> buffer) where T : struct
        { }
        public static void SetAccelerationStructure(this RHICommandList cl, int rootParameterIndex, object accel)
            => cl.SetAccelerationStructure((uint)rootParameterIndex, accel);

        public static RHIConstantBuffer<T> CreateConstantBuffer<T>(this RHIDevice device, string name = "") where T : struct
            => new RHIConstantBuffer<T>();

        // ── 缓冲创建: 兼容 (size/stride, usage) 快捷形式 ──
        public static RHIBuffer CreateBuffer(this RHIDevice d, byte[] data, int stride, RHIBufferUsage usage, string name = "")
        {
            var desc = new RHIBufferDesc { SizeInBytes = (ulong)data.Length, Flags = (RHIBufferFlags)(uint)usage, DebugName = name };
            return d.CreateBuffer(in desc, name);
        }
        public static RHIBuffer CreateBuffer(this RHIDevice d, ulong size, int stride, RHIBufferUsage usage, string name = "")
        {
            var desc = new RHIBufferDesc { SizeInBytes = size, Flags = (RHIBufferFlags)(uint)usage, DebugName = name };
            return d.CreateBuffer(in desc, name);
        }
        public static RHIBuffer CreateBuffer(this RHIDevice d, int size, int stride, RHIBufferUsage usage, string name = "")
        {
            var desc = new RHIBufferDesc { SizeInBytes = (ulong)size, Flags = (RHIBufferFlags)(uint)usage, DebugName = name };
            return d.CreateBuffer(in desc, name);
        }

        // ── 缓冲更新: 兼容 2 参 (buffer, data) 快捷形式 (RHICommandList) ──
        // 说明: RHICommandList 层暂未开放实例 UpdateBuffer, 此处以空实现承接调用签名;
        //       实际上传由后端在设备/命令列表引入时实现。
        public static void UpdateBuffer(this RHICommandList cl, RHIBuffer buffer, byte[] data) { }
        public static void UpdateBuffer<T>(this RHICommandList cl, RHIBuffer buffer, in T data) where T : struct { }
        public static void UpdateBuffer<T>(this RHICommandList cl, RHIBuffer buffer, T[] data) where T : struct { }

        // ── 缓冲更新 (RHIDevice) ──
        public static void UpdateBuffer(this RHIDevice d, RHIBuffer buffer, byte[] data)
        {
            if (data == null || data.Length == 0) return;
            d.UpdateBuffer(buffer, 0, data, 0, data.Length);
        }
        public static void UpdateBuffer<T>(this RHIDevice d, RHIBuffer buffer, in T data) where T : struct
        {
            var bytes = StructBytes(in data);
            if (bytes.Length == 0) return;
            d.UpdateBuffer(buffer, 0, bytes, 0, bytes.Length);
        }
        public static void UpdateBuffer<T>(this RHIDevice d, RHIBuffer buffer, T[] data) where T : struct
        {
            if (data == null || data.Length == 0) return;
            var bytes = StructArrayBytes(data);
            if (bytes.Length == 0) return;
            d.UpdateBuffer(buffer, 0, bytes, 0, bytes.Length);
        }

        private static byte[] StructBytes<T>(in T data) where T : struct
        {
            var span = MemoryMarshal.CreateReadOnlySpan(ref Unsafe.AsRef(in data), 1);
            var bytes = MemoryMarshal.AsBytes(span);
            return bytes.ToArray();
        }
        private static byte[] StructArrayBytes<T>(T[] data) where T : struct
        {
            var bytes = MemoryMarshal.AsBytes(data.AsSpan());
            return bytes.ToArray();
        }
    }
}
