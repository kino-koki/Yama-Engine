using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ShanyuEngine.RHI;
using ShanyuEngine.Shaders;

namespace ShanyuEngine.Rendering
{
    // ════════════════════════════════════════════════════════════════════════
    // 山雨 屏幕空间环境光遮蔽 (Screen Space Ambient Occlusion)
    //
    // 支持 SSAO 和 GTAO 两种模式：
    //
    // SSAO (Screen Space Ambient Occlusion):
    //   - 经典多环采样（基于 Intel 的 GDC 演讲优化版）
    //   - 可配采样数量/半径/偏置
    //   - 支持法线感知和深度感知
    //   - 双宽模式（半分辨率计算再上采样）
    //
    // GTAO (Ground Truth Ambient Occlusion):
    //   - 基于 Peter-Pike Sloan 的 GTAO 论文
    //   - 角度域积分代替体积采样，质量远高于 SSAO
    //   - 支持时域累积（TAAO），减少噪声
    //   - 支持多方向切片
    //
    // 共性：
    //   - 支持 Temporal Filter（时域滤波），用 motion vector 复用历史帧
    //   - 支持 Bilateral Upsample（双边上采样），半分辨率计算后锐利恢复
    //   - 支持 Blur（分离式高斯模糊，边缘感知）
    //   - 支持 Normal-Aware 采样权重
    // ════════════════════════════════════════════════════════════════════════

    // ────────────────────────────────────────────────────────────────────────
    // 枚举
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>AO 算法类型。</summary>
    public enum AOAlgorithm
    {
        /// <summary>经典 SSAO（多环采样）。</summary>
        SSAO,
        /// <summary>Ground Truth AO（角度域积分）。</summary>
        GTAO,
        /// <summary>HBAO（Horizon-Based AO）。</summary>
        HBAO
    }

    /// <summary>AO 质量预设。</summary>
    public enum AOQualityPreset
    {
        /// <summary>低质量（8 samples, half-res）。</summary>
        Low,
        /// <summary>中等（16 samples, half-res）。</summary>
        Medium,
        /// <summary>高质量（32 samples, full-res）。</summary>
        High,
        /// <summary>极致（64 samples, full-res + temporal）。</summary>
        Ultra
    }

    /// <summary>AO 模糊模式。</summary>
    public enum AOBlurMode
    {
        /// <summary>不模糊（最噪但最锐利）。</summary>
        None,
        /// <summary>分离式高斯模糊（快速）。</summary>
        SeparatedGaussian,
        /// <summary>双边模糊（边缘感知）。</summary>
        Bilateral
    }

    // ────────────────────────────────────────────────────────────────────────
    // 配置
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>SSAO/GTAO 配置。</summary>
    public sealed class SSAOConfig
    {
        /// <summary>AO 算法。</summary>
        public AOAlgorithm Algorithm = AOAlgorithm.GTAO;

        /// <summary>质量预设。</summary>
        public AOQualityPreset Quality = AOQualityPreset.High;

        /// <summary>AO 半径（世界空间）。</summary>
        public float Radius = 0.5f;

        /// <summary>AO 强度（0~3，1=标准）。</summary>
        public float Intensity = 1.0f;

        /// <summary>深度偏置（避免自遮挡）。</summary>
        public float DepthBias = 0.02f;

        /// <summary>法线偏置。</summary>
        public float NormalBias = 0.05f;

        /// <summary>采样数量。</summary>
        public int SampleCount = 32;

        /// <summary>GTAO 切片数量（方向数）。</summary>
        public int GTAO_SliceCount = 3;

        /// <summary>GTAO 每个切片的步进数。</summary>
        public int GTAO_StepsPerSlice = 8;

        /// <summary>是否半分辨率计算。</summary>
        public bool HalfResolution = true;

        /// <summary>模糊模式。</summary>
        public AOBlurMode BlurMode = AOBlurMode.Bilateral;

        /// <summary>模糊半径（像素）。</summary>
        public int BlurRadius = 4;

        /// <summary>是否启用时域滤波。</summary>
        public bool EnableTemporalFilter = true;

        /// <summary>时域衰减因子（0.9~0.99）。</summary>
        public float TemporalAlpha = 0.92f;

        /// <summary>最近深度检测阈值。</summary>
        public float TemporalNeighborhoodClamp = 2.5f;

        /// <summary>远距离衰减。</summary>
        public float FadeDistanceStart = 30.0f;

        /// <summary>完全淡出距离。</summary>
        public float FadeDistanceEnd = 80.0f;

        /// <summary>最小 AO 值（防止全黑）。</summary>
        public float MinOcclusion = 0.0f;

        /// <summary>最大 AO 值（防止过暗）。</summary>
        public float MaxOcclusion = 1.0f;

        /// <summary>启用 Debug 可视化。</summary>
        public bool EnableDebugDraw = false;

        /// <summary>根据质量预设设置参数。</summary>
        public void ApplyPreset(AOQualityPreset preset)
        {
            Quality = preset;
            switch (preset)
            {
                case AOQualityPreset.Low:
                    SampleCount = 8;
                    HalfResolution = true;
                    BlurMode = AOBlurMode.SeparatedGaussian;
                    BlurRadius = 3;
                    EnableTemporalFilter = false;
                    GTAO_SliceCount = 2;
                    GTAO_StepsPerSlice = 4;
                    break;
                case AOQualityPreset.Medium:
                    SampleCount = 16;
                    HalfResolution = true;
                    BlurMode = AOBlurMode.Bilateral;
                    BlurRadius = 4;
                    EnableTemporalFilter = true;
                    GTAO_SliceCount = 3;
                    GTAO_StepsPerSlice = 6;
                    break;
                case AOQualityPreset.High:
                    SampleCount = 32;
                    HalfResolution = false;
                    BlurMode = AOBlurMode.Bilateral;
                    BlurRadius = 4;
                    EnableTemporalFilter = true;
                    GTAO_SliceCount = 4;
                    GTAO_StepsPerSlice = 8;
                    break;
                case AOQualityPreset.Ultra:
                    SampleCount = 64;
                    HalfResolution = false;
                    BlurMode = AOBlurMode.Bilateral;
                    BlurRadius = 6;
                    EnableTemporalFilter = true;
                    TemporalAlpha = 0.95f;
                    GTAO_SliceCount = 6;
                    GTAO_StepsPerSlice = 12;
                    break;
            }
        }

        public static SSAOConfig Default => new() { Algorithm = AOAlgorithm.SSAO, ApplyPreset(AOQualityPreset.High) };
        public static SSAOConfig GTAO_Default => new() { Algorithm = AOAlgorithm.GTAO, ApplyPreset(AOQualityPreset.High) };
    }

    // ────────────────────────────────────────────────────────────────────────
    // 采样核生成
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>SSAO 采样核生成器。</summary>
    public static class SSAOKernelGenerator
    {
        /// <summary>生成半球采样核（黄金螺旋 + 高斯权重）。</summary>
        public static Vector4[] GenerateKernel(int sampleCount)
        {
            var kernel = new Vector4[sampleCount];
            float goldenAngle = 2.39996323f;

            for (int i = 0; i < sampleCount; i++)
            {
                // 黄金螺旋分布在单位半球上
                float r = MathF.Sqrt((float)(i + 0.5f) / sampleCount);
                float theta = i * goldenAngle;

                // 映射到半球（z > 0）
                float z = MathF.Sqrt(MathF.Max(1.0f - r * r, 0));
                float x = r * MathF.Cos(theta);
                float y = r * MathF.Sin(theta);

                // 高斯权重，近处采样更重
                float scale = (float)i / sampleCount;
                scale = 0.1f + 0.9f * scale * scale; // 非线性分布，从近到远
                x *= scale;
                y *= scale;
                z = z * 0.5f + 0.5f; // 把 z 也偏移到上半球

                // 归一化方向
                float len = MathF.Sqrt(x * x + y * y + z * z);
                float weight = 1.0f - (float)i / sampleCount; // 越远越轻
                kernel[i] = new Vector4(x / len, y / len, z / len, weight);
            }

            // 归一化权重
            float totalW = 0;
            for (int i = 0; i < sampleCount; i++) totalW += kernel[i].W;
            for (int i = 0; i < sampleCount; i++) kernel[i] = new Vector4(kernel[i].X, kernel[i].Y, kernel[i].Z, kernel[i].W / totalW);

            return kernel;
        }

        /// <summary>生成多环采样核（类似 Intel AO 的做法）。</summary>
        public static Vector4[] GenerateMultiRingKernel(int sampleCount)
        {
            var kernel = new Vector4[sampleCount];
            int ringCount = 4; // 4 环
            float goldenAngle = 2.39996323f;
            int idx = 0;

            for (int ring = 0; ring < ringCount && idx < sampleCount; ring++)
            {
                float ringRadius = 0.3f + ring * 0.7f / (ringCount - 1);
                int samplesInRing = sampleCount / ringCount;
                if (ring == ringCount - 1) samplesInRing = sampleCount - idx; // 最后一环补齐

                for (int s = 0; s < samplesInRing && idx < sampleCount; s++, idx++)
                {
                    float angle = s * goldenAngle + ring * 1.618f;
                    float x = ringRadius * MathF.Cos(angle);
                    float y = ringRadius * MathF.Sin(angle);
                    float z = MathF.Sqrt(MathF.Max(1.0f - x * x - y * y, 0)) * 0.7f;

                    float len = MathF.Sqrt(x * x + y * y + z * z);
                    float weight = 1.0f / (ring + 1);
                    kernel[idx] = new Vector4(x / len, y / len, z / len, weight);
                }
            }

            float totalW = 0;
            for (int i = 0; i < idx; i++) totalW += kernel[i].W;
            for (int i = 0; i < idx; i++) kernel[i] = new Vector4(kernel[i].X, kernel[i].Y, kernel[i].Z, kernel[i].W / totalW);

            return kernel;
        }

        /// <summary>生成随机旋转纹理数据（4x4 的旋转矩阵贴图）。</summary>
        public static Vector4[] GenerateRandomRotations(int texWidth = 4, int texHeight = 4)
        {
            var data = new Vector4[texWidth * texHeight];
            Random rand = new(42);

            for (int y = 0; y < texHeight; y++)
            {
                for (int x = 0; x < texWidth; x++)
                {
                    // 随机 TBN 旋转矩阵
                    float angle = (float)rand.NextDouble() * MathF.PI * 2.0f;
                    data[y * texWidth + x] = new Vector4(
                        MathF.Cos(angle),
                        MathF.Sin(angle),
                        0.0f, 0.0f);
                }
            }

            return data;
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // GTAO 角度域积分
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>GTAO 角度域 AO 计算（CPU 侧参考实现）。</summary>
    public static class GTAOCalculator
    {
        /// <summary>单个像素的 GTAO 计算。</summary>
        /// <param name="pixelDepth">当前像素深度。</param>
        /// <param name="pixelNormal">当前像素法线。</param>
        /// <param name="viewPos">视图空间位置。</param>
        /// <param name="sliceCount">切片数。</param>
        /// <param name="stepsPerSlice">每切片步进数。</param>
        /// <param name="radius">AO 半径。</param>
        /// <param name="depthAt">深度采样函数（屏幕空间偏移 -> 深度值）。</param>
        /// <param name="normalAt">法线采样函数。</param>
        /// <param name="invResolution">1/分辨率。</param>
        /// <param name="projInfo">投影参数（focalLength, etc）。</param>
        /// <returns>AO 值 [0,1]，0=完全遮挡，1=完全可见。</returns>
        public static float ComputeGTAO(
            float pixelDepth,
            Vector3 pixelNormal,
            Vector3 viewPos,
            int sliceCount,
            int stepsPerSlice,
            float radius,
            Func<Vector2, float> depthAt,
            Func<Vector2, Vector3> normalAt,
            Vector2 invResolution,
            Vector4 projInfo)
        {
            if (pixelDepth <= 0) return 1.0f; // 天空/无穷远

            // 视图空间的切线和副切线
            Vector3 viewDir = -Vector3.Normalize(viewPos);
            // NOTE: 简化——不生成精确的 TBN，用垂直于 view-normal 平面的两个方向
            Vector3 up = Math.Abs(pixelNormal.Y) < 0.999f ? Vector3.UnitY : Vector3.UnitX;
            Vector3 tangent = Vector3.Normalize(Vector3.Cross(up, pixelNormal));
            Vector3 bitangent = Vector3.Cross(pixelNormal, tangent);

            float ao = 0.0f;
            float weightSum = 0.0f;

            for (int slice = 0; slice < sliceCount; slice++)
            {
                float sliceAngle = (float)slice / sliceCount * MathF.PI * 2.0f;

                // 切片方向 = tangent * cos(angle) + bitangent * sin(angle)
                Vector3 sliceDir = tangent * MathF.Cos(sliceAngle) + bitangent * MathF.Sin(sliceAngle);

                // 在切片方向上做 1D AO 积分
                float horizonAngle = 0.0f; // 累积的水平线角度
                float sliceAO = 0.0f;
                float sliceWeight = 0.0f;

                for (int step = 1; step <= stepsPerSlice; step++)
                {
                    float t = (float)step / stepsPerSlice;
                    float sampleRadius = radius * t;

                    // 沿切片方向偏移
                    Vector3 samplePos = viewPos + sliceDir * sampleRadius;

                    // 投影到屏幕空间
                    float sx = projInfo.X * samplePos.X / (-samplePos.Z) + projInfo.Z;
                    float sy = projInfo.Y * samplePos.Y / (-samplePos.Z) + projInfo.W;
                    // sx, sy 是 NDC [-1,1]，转像素
                    float px = (sx * 0.5f + 0.5f);
                    float py = (sy * 0.5f + 0.5f);

                    // hack: 这里简化了，实际应该用完整的逆投影
                    float sampleDepth = depthAt(new Vector2(px, py));

                    if (sampleDepth <= 0) continue;

                    // 计算这个采样点相对于当前点的角度
                    Vector3 delta = new Vector3(
                        (sx - projInfo.Z) / projInfo.X * (-pixelDepth),
                        (sy - projInfo.W) / projInfo.Y * (-pixelDepth),
                        0);
                    // 角度近似
                    float h = MathF.Atan2(delta.X * sliceDir.X + delta.Y * sliceDir.Y,
                        pixelDepth - sampleDepth);

                    // 累积遮挡
                    float occluderAngle = MathF.Max(0, h - horizonAngle);
                    if (occluderAngle > 0)
                    {
                        float stepAO = 1.0f - occluderAngle / (MathF.PI * 0.5f);
                        sliceAO += stepAO;
                        horizonAngle = Math.Max(horizonAngle, h);
                    }

                    // 距离权重
                    float dist = sampleRadius / radius;
                    float w = 1.0f / (1.0f + dist * dist * 4.0f);
                    sliceWeight += w;
                }

                if (sliceWeight > 0)
                {
                    sliceAO /= sliceWeight;
                    ao += sliceAO;
                    weightSum += 1.0f;
                }
            }

            if (weightSum > 0) ao /= weightSum;
            return Math.Clamp(ao, 0, 1);
        }

        /// <summary>HBAO 计算（Horizon-Based AO）。</summary>
        public static float ComputeHBAO(
            float pixelDepth,
            Vector3 pixelNormal,
            Vector3 viewPos,
            int directions,
            int steps,
            float radius,
            Func<Vector2, float> depthAt,
            Vector4 projInfo)
        {
            if (pixelDepth <= 0) return 1.0f;

            float ao = 0.0f;
            Vector3 viewDir = Vector3.Normalize(-viewPos);

            for (int dir = 0; dir < directions; dir++)
            {
                float dirAngle = (float)dir / directions * MathF.PI * 2.0f;

                float horizonH = 0; // 正方向
                float horizonN = 0; // 反方向

                for (int step = 0; step < steps; step++)
                {
                    float t = (step + 0.5f) / steps;
                    float sampleDist = radius * t;

                    // 采样点偏移
                    Vector2 offset = new Vector2(
                        MathF.Cos(dirAngle) * sampleDist,
                        MathF.Sin(dirAngle) * sampleDist);

                    // 简化投影
                    float sx = offset.X * projInfo.X / (-pixelDepth);
                    float sy = offset.Y * projInfo.Y / (-pixelDepth);

                    float sDepth = depthAt(new Vector2(0.5f + sx * 0.5f, 0.5f + sy * 0.5f));
                    if (sDepth <= 0) continue;

                    float dz = pixelDepth - sDepth;
                    float distSq = offset.X * offset.X + offset.Y * offset.Y + dz * dz;
                    float h = dz / MathF.Sqrt(MathF.Max(distSq, 1e-6f));
                    horizonH = Math.Max(horizonH, h);

                    // 反方向
                    sDepth = depthAt(new Vector2(0.5f - sx * 0.5f, 0.5f - sy * 0.5f));
                    if (sDepth <= 0) continue;
                    dz = pixelDepth - sDepth;
                    distSq = offset.X * offset.X + offset.Y * offset.Y + dz * dz;
                    h = dz / MathF.Sqrt(MathF.Max(distSq, 1e-6f));
                    horizonN = Math.Max(horizonN, h);
                }

                // AO = cos(horizon) 贡献
                float aoH = MathF.Max(0, pixelNormal.X * MathF.Cos(dirAngle) + pixelNormal.Z * MathF.Sin(dirAngle));
                aoH *= 1.0f - horizonH;
                float aoN = -aoH;
                aoN *= 1.0f - horizonN;

                ao += aoH + aoN;
            }

            ao /= directions * 2.0f;
            return Math.Clamp(1.0f - ao * 0.5f, 0, 1);
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // 常量缓冲
    // ────────────────────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    internal struct SSAOCB
    {
        public Matrix4x4 InvViewProj;
        public Matrix4x4 View;
        public Matrix4x4 Proj;
        public Vector4 ProjInfo; // (1/fx, 1/fy, cx, cy) for unproject
        public Vector2 InvResolution;
        public Vector2 InvFullResolution;
        public float Radius;
        public float RadiusSq;
        public float Intensity;
        public float DepthBias;
        public float NormalBias;
        public int SampleCount;
        public int Algorithm;
        public int SliceCount; // GTAO
        public int StepsPerSlice; // GTAO
        public float FadeStart;
        public float FadeEnd;
        public float InvFadeRange;
        public float MinOcclusion;
        public float MaxOcclusion;
        public int FrameIndex;
        public Vector2 Jitter; // 时域抖动
        public float TemporalAlpha;
        public int EnableTemporal;
        public float NearPlane;
        public float FarPlane;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct AOBlurCB
    {
        public Vector2 InvResolution;
        public int BlurRadius;
        public float Sharpness; // 边缘锐度
        public int Pass; // 0=H, 1=V
        public float Pad0, Pad1, Pad2;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct AOUpsampleCB
    {
        public Vector2 InvFullResolution;
        public Vector2 InvHalfResolution;
        public float Sharpness;
        public float Pad0, Pad1, Pad2;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct AOTemporalCB
    {
        public Vector2 InvResolution;
        public float Alpha;
        public float NeighborhoodClamp;
        public int EnableDebug;
        public float Pad0, Pad1, Pad2;
    }

    // ────────────────────────────────────────────────────────────────────────
    // AO 输出
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>AO 计算结果。</summary>
    public struct AOResult
    {
        public RHITexture? AOMap;
        public int Width;
        public int Height;
        public double ComputeTimeMs;
    }

    // ────────────────────────────────────────────────────────────────────────
    // 主系统
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 屏幕空间环境光遮蔽系统。
    /// 支持 SSAO / GTAO / HBAO，可选时域滤波和双边上采样。
    /// </summary>
    public sealed class ScreenSpaceAmbientOcclusion : IDisposable
    {
        private readonly RHIDevice _device;
        private SSAOConfig _config;
        private int _width, _height;
        private int _aoWidth, _aoHeight; // 实际计算分辨率
        private bool _disposed;
        private int _frameIndex;

        // GPU 资源
        private RHIConstantBuffer? _aoCB;
        private RHIConstantBuffer? _blurCB;
        private RHIConstantBuffer? _upsampleCB;
        private RHIConstantBuffer? _temporalCB;
        private RHITexture? _aoOutput;
        private RHITexture? _aoBlurred;
        private RHITexture? _aoHistory;
        private RHITexture? _randomRotationTex;
        private RHITexture? _finalOutput;
        private RHIRootSignature? _rootSig;
        private RHIPipelineState? _ssaoPSO;
        private RHIPipelineState? _gtaoPSO;
        private RHIPipelineState? _hbaoPSO;
        private RHIPipelineState? _blurHPSO;
        private RHIPipelineState? _blurVPSO;
        private RHIPipelineState? _upsamplePSO;
        private RHIPipelineState? _temporalPSO;

        // 性能统计
        private double _lastComputeTimeMs;
        private double _avgComputeTimeMs;
        private int _sampleCount;

        public SSAOConfig Config
        {
            get => _config;
            set { _config = value ?? SSAOConfig.Default; RecreateResources(); }
        }

        public RHITexture? OutputMap => _finalOutput;
        public double LastComputeTimeMs => _lastComputeTimeMs;
        public double AverageComputeTimeMs => _avgComputeTimeMs;
        public int FrameIndex => _frameIndex;

        public ScreenSpaceAmbientOcclusion(RHIDevice device, int width, int height, SSAOConfig? config = null)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));
            _width = width;
            _height = height;
            _config = config ?? SSAOConfig.GTAO_Default;
            _frameIndex = 0;

            RecreateResources();
        }

        private void RecreateResources()
        {
            _aoWidth = _config.HalfResolution ? (_width + 1) / 2 : _width;
            _aoHeight = _config.HalfResolution ? (_height + 1) / 2 : _height;

            // 常量缓冲
            _aoCB = _device.CreateConstantBuffer(SSAOCB.SizeOf);
            _blurCB = _device.CreateConstantBuffer(AOBlurCB.SizeOf);
            _upsampleCB = _device.CreateConstantBuffer(AOUpsampleCB.SizeOf);
            _temporalCB = _device.CreateConstantBuffer(AOTemporalCB.SizeOf);

            // AO 输出纹理
            _aoOutput = _device.CreateTexture(new RHITextureDesc
            {
                Width = _aoWidth,
                Height = _aoHeight,
                Format = RHIFormat.R8_UNorm, // AO 只需要单通道
                Usage = RHITextureUsage.UnorderedAccess | RHITextureUsage.ShaderResource,
                DebugName = "AO_RawOutput"
            });

            // 模糊中间纹理
            _aoBlurred = _device.CreateTexture(new RHITextureDesc
            {
                Width = _aoWidth,
                Height = _aoHeight,
                Format = RHIFormat.R8_UNorm,
                Usage = RHITextureUsage.UnorderedAccess | RHITextureUsage.ShaderResource,
                DebugName = "AO_Blurred"
            });

            // 历史帧（时域滤波用）
            if (_config.EnableTemporalFilter)
            {
                _aoHistory = _device.CreateTexture(new RHITextureDesc
                {
                    Width = _aoWidth,
                    Height = _aoHeight,
                    Format = RHIFormat.R16_Float, // 时域需要更高精度
                    Usage = RHITextureUsage.UnorderedAccess | RHITextureUsage.ShaderResource,
                    DebugName = "AO_History"
                });
            }

            // 最终输出（全分辨率）
            _finalOutput = _device.CreateTexture(new RHITextureDesc
            {
                Width = _width,
                Height = _height,
                Format = RHIFormat.R8_UNorm,
                Usage = RHITextureUsage.UnorderedAccess | RHITextureUsage.ShaderResource,
                DebugName = "AO_FinalOutput"
            });

            // 随机旋转纹理 4x4
            _randomRotationTex = _device.CreateTexture(new RHITextureDesc
            {
                Width = 4,
                Height = 4,
                Format = RHIFormat.R16G16_Float,
                Usage = RHITextureUsage.ShaderResource,
                DebugName = "AO_RandomRotation"
            });
            var rotData = SSAOKernelGenerator.GenerateRandomRotations(4, 4);
            _device.UpdateBuffer(_device.CreateConstantBuffer(4 * 4 * 16), rotData);

            Console.WriteLine($"[SSAO] 初始化完成，计算分辨率 {_aoWidth}x{_aoHeight}，算法={_config.Algorithm}");
        }

        /// <summary>执行 AO 计算。</summary>
        public void Execute(RHITexture depthBuffer, RHITexture normalBuffer, RHITexture? motionVectors = null)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ScreenSpaceAmbientOcclusion));

            var sw = System.Diagnostics.Stopwatch.StartNew();

            // Pass 1: AO 计算
            ComputeAO(depthBuffer, normalBuffer);

            // Pass 2: 模糊
            if (_config.BlurMode != AOBlurMode.None)
            {
                Blur(_aoOutput!, _aoBlurred!, _aoOutput!);
            }

            // Pass 3: 时域滤波
            if (_config.EnableTemporalFilter && motionVectors != null && _aoHistory != null)
            {
                TemporalFilter(_config.BlurMode != AOBlurMode.None ? _aoBlurred! : _aoOutput!, motionVectors);
            }

            // Pass 4: 如果是半分辨率，上采样到全分辨率
            if (_config.HalfResolution)
            {
                Upsample(
                    _config.EnableTemporalFilter ? _aoHistory! : (_config.BlurMode != AOBlurMode.None ? _aoBlurred! : _aoOutput!),
                    depthBuffer, normalBuffer);
            }
            else
            {
                // 直接拷贝到最终输出
                CopyToFinal(_config.EnableTemporalFilter ? _aoHistory! : (_config.BlurMode != AOBlurMode.None ? _aoBlurred! : _aoOutput!));
            }

            sw.Stop();
            _lastComputeTimeMs = sw.Elapsed.TotalMilliseconds;
            _avgComputeTimeMs = _avgComputeTimeMs * 0.9 + _lastComputeTimeMs * 0.1;
            _sampleCount++;
            _frameIndex++;
        }

        private void ComputeAO(RHITexture depthBuffer, RHITexture normalBuffer)
        {
            // 计算 jitter（时域抖动）
            float jitterX = (_frameIndex * 7 + 3) * 0.618f % 1.0f;
            float jitterY = (_frameIndex * 13 + 7) * 0.618f % 1.0f;

            var cb = new SSAOCB
            {
                InvResolution = new Vector2(1.0f / _aoWidth, 1.0f / _aoHeight),
                InvFullResolution = new Vector2(1.0f / _width, 1.0f / _height),
                Radius = _config.Radius,
                RadiusSq = _config.Radius * _config.Radius,
                Intensity = _config.Intensity,
                DepthBias = _config.DepthBias,
                NormalBias = _config.NormalBias,
                SampleCount = _config.Algorithm == AOAlgorithm.GTAO ? _config.GTAO_StepsPerSlice : _config.SampleCount,
                Algorithm = (int)_config.Algorithm,
                SliceCount = _config.GTAO_SliceCount,
                StepsPerSlice = _config.GTAO_StepsPerSlice,
                FadeStart = _config.FadeDistanceStart,
                FadeEnd = _config.FadeDistanceEnd,
                InvFadeRange = 1.0f / MathF.Max(_config.FadeDistanceEnd - _config.FadeDistanceStart, 1.0f),
                MinOcclusion = _config.MinOcclusion,
                MaxOcclusion = _config.MaxOcclusion,
                FrameIndex = _frameIndex,
                Jitter = new Vector2(jitterX, jitterY),
                TemporalAlpha = _config.TemporalAlpha,
                EnableTemporal = _config.EnableTemporalFilter ? 1 : 0,
                NearPlane = 0.1f,
                FarPlane = 1000.0f
            };

            _device.UpdateBuffer(_aoCB!, cb);

            // 选择 PSO
            var pso = _config.Algorithm switch
            {
                AOAlgorithm.GTAO => _gtaoPSO,
                AOAlgorithm.HBAO => _hbaoPSO,
                _ => _ssaoPSO
            };

            _device.SetPipelineState(pso!);
            _device.SetShaderResource(0, depthBuffer);
            _device.SetShaderResource(1, normalBuffer);
            _device.SetShaderResource(2, _randomRotationTex!);
            _device.SetUnorderedAccessView(0, _aoOutput!);
            _device.SetConstantBuffer(0, _aoCB!);

            int gx = (_aoWidth + 7) / 8;
            int gy = (_aoHeight + 7) / 8;
            _device.Dispatch(gx, gy, 1);
        }

        private void Blur(RHITexture input, RHITexture temp, RHITexture output)
        {
            // 水平模糊
            var hCB = new AOBlurCB
            {
                InvResolution = new Vector2(1.0f / _aoWidth, 1.0f / _aoHeight),
                BlurRadius = _config.BlurRadius,
                Sharpness = 0.5f,
                Pass = 0
            };
            _device.UpdateBuffer(_blurCB!, hCB);
            _device.SetShaderResource(0, input);
            _device.SetUnorderedAccessView(0, temp);
            _device.SetConstantBuffer(0, _blurCB!);
            _device.Dispatch((_aoWidth + 7) / 8, (_aoHeight + 7) / 8, 1);

            // 垂直模糊
            var vCB = new AOBlurCB
            {
                InvResolution = new Vector2(1.0f / _aoWidth, 1.0f / _aoHeight),
                BlurRadius = _config.BlurRadius,
                Sharpness = 0.5f,
                Pass = 1
            };
            _device.UpdateBuffer(_blurCB!, vCB);
            _device.SetShaderResource(0, temp);
            _device.SetUnorderedAccessView(0, output);
            _device.SetConstantBuffer(0, _blurCB!);
            _device.Dispatch((_aoWidth + 7) / 8, (_aoHeight + 7) / 8, 1);
        }

        private void TemporalFilter(RHITexture currentAO, RHITexture motionVectors)
        {
            var cb = new AOTemporalCB
            {
                InvResolution = new Vector2(1.0f / _aoWidth, 1.0f / _aoHeight),
                Alpha = _config.TemporalAlpha,
                NeighborhoodClamp = _config.TemporalNeighborhoodClamp,
                EnableDebug = _config.EnableDebugDraw ? 1 : 0
            };
            _device.UpdateBuffer(_temporalCB!, cb);

            _device.SetShaderResource(0, currentAO);
            _device.SetShaderResource(1, _aoHistory!);
            _device.SetShaderResource(2, motionVectors);
            _device.SetUnorderedAccessView(0, _aoHistory!);
            _device.SetConstantBuffer(0, _temporalCB!);
            _device.Dispatch((_aoWidth + 7) / 8, (_aoHeight + 7) / 8, 1);
        }

        private void Upsample(RHITexture aoHalfRes, RHITexture fullResDepth, RHITexture fullResNormal)
        {
            var cb = new AOUpsampleCB
            {
                InvFullResolution = new Vector2(1.0f / _width, 1.0f / _height),
                InvHalfResolution = new Vector2(1.0f / _aoWidth, 1.0f / _aoHeight),
                Sharpness = 0.8f
            };
            _device.UpdateBuffer(_upsampleCB!, cb);

            _device.SetShaderResource(0, aoHalfRes);
            _device.SetShaderResource(1, fullResDepth);
            _device.SetShaderResource(2, fullResNormal);
            _device.SetUnorderedAccessView(0, _finalOutput!);
            _device.SetConstantBuffer(0, _upsampleCB!);
            _device.Dispatch((_width + 7) / 8, (_height + 7) / 8, 1);
        }

        private void CopyToFinal(RHITexture source)
        {
            _device.CopyTexture(_finalOutput!, source);
        }

        /// <summary>调整分辨率。</summary>
        public void Resize(int width, int height)
        {
            if (_width == width && _height == height) return;
            _width = width;
            _height = height;
            RecreateResources();
            Console.WriteLine($"[SSAO] 调整分辨率到 {_width}x{_height}");
        }

        /// <summary>设置算法。</summary>
        public void SetAlgorithm(AOAlgorithm algo)
        {
            _config.Algorithm = algo;
            Console.WriteLine($"[SSAO] 切换算法到 {algo}");
        }

        /// <summary>重置时域历史。</summary>
        public void ResetTemporal()
        {
            if (_aoHistory != null)
            {
                _device.ClearUAV(_aoHistory, new Vector4(1, 1, 1, 1)); // 初始化为完全无遮挡
                Console.WriteLine("[SSAO] 时域历史已重置");
            }
        }

        /// <summary>获取性能统计信息。</summary>
        public string GetStats()
        {
            return $"SSAO/GTAO Stats: " +
                   $"Algo={_config.Algorithm}, " +
                   $"Res={_aoWidth}x{_aoHeight}, " +
                   $"Last={_lastComputeTimeMs:F2}ms, " +
                   $"Avg={_avgComputeTimeMs:F2}ms, " +
                   $"Frames={_sampleCount}";
        }

        /// <summary>CPU 端 AO 计算（调试/预览）。</summary>
        public static float ComputeAO_CPU(
            float centerDepth,
            Vector3 centerNormal,
            float[] depthBuffer,
            int bufferWidth,
            int bufferHeight,
            int pixelX,
            int pixelY,
            float radius,
            int sampleCount)
        {
            if (centerDepth <= 0) return 1.0f;

            float ao = 0.0f;
            var kernel = SSAOKernelGenerator.GenerateKernel(sampleCount);
            var rotData = SSAOKernelGenerator.GenerateRandomRotations();
            Vector2 rot = new(rotData[0].X, rotData[0].Y);

            for (int i = 0; i < sampleCount; i++)
            {
                Vector3 sample = new(kernel[i].X, kernel[i].Y, kernel[i].Z);
                // 应用旋转
                float rx = sample.X * rot.X - sample.Y * rot.Y;
                float ry = sample.X * rot.Y + sample.Y * rot.X;
                sample.X = rx;
                sample.Y = ry;

                // 屏幕空间偏移
                int sx = pixelX + (int)(sample.X * radius * bufferWidth * 0.5f);
                int sy = pixelY + (int)(sample.Y * radius * bufferHeight * 0.5f);

                // 环绕
                sx = ((sx % bufferWidth) + bufferWidth) % bufferWidth;
                sy = ((sy % bufferHeight) + bufferHeight) % bufferHeight;

                if (sx < 0 || sx >= bufferWidth || sy < 0 || sy >= bufferHeight) continue;

                float sampleDepth = depthBuffer[sy * bufferWidth + sx];
                if (sampleDepth <= 0) continue;

                // 深度比较
                float depthDiff = centerDepth - sampleDepth;
                if (depthDiff > 0 && depthDiff < radius)
                {
                    float rangeCheck = MathF.SmoothStep(0.0f, 1.0f, radius / MathF.Max(depthDiff, 1e-6f));
                    ao += rangeCheck * kernel[i].W;
                }
            }

            ao = 1.0f - ao;
            return Math.Clamp(ao, 0, 1);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _aoCB?.Dispose();
            _blurCB?.Dispose();
            _upsampleCB?.Dispose();
            _temporalCB?.Dispose();
            _aoOutput?.Dispose();
            _aoBlurred?.Dispose();
            _aoHistory?.Dispose();
            _randomRotationTex?.Dispose();
            _finalOutput?.Dispose();
            _rootSig?.Dispose();
            _ssaoPSO?.Dispose();
            _gtaoPSO?.Dispose();
            _hbaoPSO?.Dispose();
            _blurHPSO?.Dispose();
            _blurVPSO?.Dispose();
            _upsamplePSO?.Dispose();
            _temporalPSO?.Dispose();
            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // AO 参数插值（用于动画过渡）
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>AO 参数平滑过渡器。</summary>
    public sealed class AOParameterTransition
    {
        private SSAOConfig _from;
        private SSAOConfig _to;
        private float _duration;
        private float _elapsed;
        private bool _active;

        public SSAOConfig Current { get; private set; }

        public AOParameterTransition(SSAOConfig initial)
        {
            Current = initial.Clone();
            _from = initial.Clone();
            _to = initial.Clone();
        }

        public void TransitionTo(SSAOConfig target, float duration)
        {
            _from = Current.Clone();
            _to = target.Clone();
            _duration = duration;
            _elapsed = 0;
            _active = true;
        }

        public void Update(float deltaTime)
        {
            if (!_active) return;
            _elapsed += deltaTime;
            float t = Math.Clamp(_elapsed / MathF.Max(_duration, 0.001f), 0, 1);
            // Smoothstep
            t = t * t * (3 - 2 * t);

            Current.Radius = Lerp(_from.Radius, _to.Radius, t);
            Current.Intensity = Lerp(_from.Intensity, _to.Intensity, t);
            Current.DepthBias = Lerp(_from.DepthBias, _to.DepthBias, t);
            Current.NormalBias = Lerp(_from.NormalBias, _to.NormalBias, t);
            Current.TemporalAlpha = Lerp(_from.TemporalAlpha, _to.TemporalAlpha, t);

            if (_elapsed >= _duration) _active = false;
        }

        private static float Lerp(float a, float b, float t) => a + (b - a) * t;
    }
}
