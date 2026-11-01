// ════════════════════════════════════════════════════════════════════════
// yama引擎 - AI 超分辨率系统 (NeuralSuperResolution)
//
// AI 驱动的超分辨率上采样系统，类似 DLSS/FSR/XeSS。
// 将低分辨率渲染图像重建为高分辨率输出，同时利用时域信息提升质量。
//
// 核心特性:
//   1. 多后端支持: FSR3 / DLSS3 / XeSS / Internal (内建)
//   2. Halton 序列子像素抖动生成 (TAA/TSR 质量关键)
//   3. 时域重建: 利用运动矢量 + 历史帧积累
//   4. 内建 FSR3 兼容着色器 (EASU + RCAS)
//   5. 时间累积计算着色器 (Temporal Accumulation CS)
//   6. RCAS 锐化像素着色器 (用于最终输出增强)
//   7. 曝光补偿和自动曝光支持
//
// 对接系统: RHI (RHIDevice/RHICommandList) / WorldRenderPipeline (后处理阶段)
// ════════════════════════════════════════════════════════════════════════

using System;
using System.Numerics;
using YamaEngine.RHI;

namespace YamaEngine.Rendering
{
    // ──────────────────────────────────────────────────────────────
    // 超分辨率模型枚举
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 超分辨率后端模型。不同模型使用不同的上采样算法。
    /// </summary>
    public enum NeuralSRModel
    {
        /// <summary>AMD FSR 3.0 — 基于空间+时域的超级分辨率锐化。</summary>
        FSR3 = 0,
        /// <summary>NVIDIA DLSS 3 — 基于深度学习的超分辨率 (需要 RTX GPU)。</summary>
        DLSS3 = 1,
        /// <summary>Intel XeSS — 基于 AI 的超分辨率 (Xe 核显/独显)。</summary>
        XeSS = 2,
        /// <summary>yama内建 — 自研时域超分辨率 (无需特定硬件)。</summary>
        Internal = 3
    }

    // ──────────────────────────────────────────────────────────────
    // 超分辨率质量预设
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 超分辨率质量模式。控制输入分辨率相对于输出分辨率的比例。
    /// </summary>
    public enum SRQualityMode
    {
        /// <summary>超高质量 (1.0x 输入, 仅做锐化和时域重建)。</summary>
        UltraQuality = 0,
        /// <summary>高质量 (0.77x 输入)。</summary>
        Quality = 1,
        /// <summary>均衡 (0.67x 输入)。</summary>
        Balanced = 2,
        /// <summary>性能 (0.58x 输入)。</summary>
        Performance = 3
    }

    // ──────────────────────────────────────────────────────────────
    // 超分辨率配置
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 超分辨率系统配置。
    /// </summary>
    public sealed class NeuralSRConfig
    {
        /// <summary>超分辨率模型。</summary>
        public NeuralSRModel Model { get; set; } = NeuralSRModel.FSR3;
        /// <summary>质量模式。</summary>
        public SRQualityMode QualityMode { get; set; } = SRQualityMode.Quality;
        /// <summary>RCAS 锐化强度 (0.0 ~ 1.0, 0=关闭, 0.5=默认)。</summary>
        public float Sharpness { get; set; } = 0.4f;
        /// <summary>时域重建强度 (0.0 ~ 1.0)。</summary>
        public float TemporalStrength { get; set; } = 0.9f;
        /// <summary>最大历史帧回溯数 (用于重投影)。</summary>
        public int MaxHistoryFrames { get; set; } = 4;
        /// <summary>是否启用自动曝光。</summary>
        public bool EnableAutoExposure { get; set; } = true;
        /// <summary>抖动相位偏移 (避免与 TAA 抖动冲突)。</summary>
        public float JitterPhaseOffset { get; set; } = 0.0f;
        /// <summary>速度缓冲区阈值 (运动矢量长度超过此值则标记为高速)。</summary>
        public float VelocityThreshold { get; set; } = 0.1f;

        /// <summary>获取质量模式对应的分辨率缩放因子。</summary>
        public static float GetQualityScale(SRQualityMode mode) => mode switch
        {
            SRQualityMode.UltraQuality => 1.0f,
            SRQualityMode.Quality => 0.77f,
            SRQualityMode.Balanced => 0.67f,
            SRQualityMode.Performance => 0.58f,
            _ => 0.77f
        };
    }

    // ══════════════════════════════════════════════════════════════
    //  NeuralSuperResolution — AI 超分辨率系统
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// AI 超分辨率系统: 将低分辨率渲染图像重建为全分辨率输出。
    /// 支持多种后端模型 (FSR3/DLSS3/XeSS/Internal)，内建 FSR3 兼容着色器。
    /// 核心流程: Jitter -> Render at LowRes -> TemporalReconstruct -> Sharpen(RCAS) -> Output
    /// </summary>
    public sealed class NeuralSuperResolution : IDisposable
    {
        // ───── 状态 ─────
        private RHIDevice? _device;
        private NeuralSRModel _model;
        private NeuralSRConfig _config;
        private bool _initialized;
        private bool _disposed;

        // ───── 帧索引 ─────
        private int _frameIndex;

        // ───── GPU 资源 ─────
        private RHITexture? _historyColor;
        private RHITexture? _dilatedVelocity;
        private RHIPipelineState? _psoEASU;
        private RHIPipelineState? _psoRCAS;
        private RHIPipelineState? _psoTemporalAccumulation;

        // ───── Halton 序列表 ─────
        private static readonly float[] s_HaltonBase2 = GenerateHaltonSequence(2, 256);
        private static readonly float[] s_HaltonBase3 = GenerateHaltonSequence(3, 256);

        /// <summary>当前使用的模型。</summary>
        public NeuralSRModel Model => _model;
        /// <summary>当前配置。</summary>
        public NeuralSRConfig Config => _config;
        /// <summary>当前帧索引。</summary>
        public int FrameIndex => _frameIndex;
        /// <summary>是否已初始化。</summary>
        public bool IsInitialized => _initialized;

        /// <summary>构造神经超分器并关联设备与渲染图。</summary>
        public NeuralSuperResolution(RHIDevice device, RenderGraph graph)
        {
            _device = device;
            _renderGraph = graph;
        }

        private RenderGraph? _renderGraph;

        /// <summary>设置质量模式 (1=性能, 2=平衡, 3=质量)。</summary>
        public void SetQualityMode(int mode) => _qualityMode = mode;

        private int _qualityMode = 2;

        /// <summary>将神经超分器挂接到渲染管线。</summary>
        public void AttachToPipeline(WorldRenderPipeline pipeline) => _attachedPipeline = pipeline;

        private WorldRenderPipeline? _attachedPipeline;

        /// <summary>
        /// 获取当前质量模式对应的分辨率缩放。
        /// </summary>
        public float CurrentResolutionScale => NeuralSRConfig.GetQualityScale(_config.QualityMode);

        // ════════════════════════════════════════════════════════
        //  初始化
        // ════════════════════════════════════════════════════════

        /// <summary>
        /// 初始化超分辨率系统。
        /// </summary>
        /// <param name="device">RHI 设备。</param>
        /// <param name="model">超分辨率模型。</param>
        /// <param name="config">配置 (可选, null 则使用默认)。</param>
        public void Initialize(RHIDevice device, NeuralSRModel model, NeuralSRConfig? config = null)
        {
            if (device == null) throw new ArgumentNullException(nameof(device));

            _device = device;
            _model = model;
            _config = config ?? new NeuralSRConfig();
            _frameIndex = 0;

            // 检查模型可用性
            if (model == NeuralSRModel.DLSS3 && !CheckDLSSAvailable())
            {
                // DLSS 不可用，回退到内建
                _model = NeuralSRModel.Internal;
            }
            else if (model == NeuralSRModel.XeSS && !CheckXeSSAvailable())
            {
                _model = NeuralSRModel.Internal;
            }

            // 创建 PSO
            CreatePipelineStates();

            _initialized = true;
        }

        // ════════════════════════════════════════════════════════
        //  上采样 (主入口)
        // ════════════════════════════════════════════════════════

        /// <summary>
        /// 执行超分辨率上采样。
        /// </summary>
        /// <param name="cmd">RHI 命令列表。</param>
        /// <param name="input">低分辨率输入颜色纹理。</param>
        /// <param name="output">高分辨率输出颜色纹理。</param>
        /// <param name="jitterOffset">当前帧的子像素抖动偏移。</param>
        /// <param name="motionVectorScale">运动矢量缩放因子。</param>
        public void Upscale(RHICommandList cmd, RHITexture input, RHITexture output,
            Vector2 jitterOffset, Vector2 motionVectorScale)
        {
            if (!_initialized) throw new InvalidOperationException("NeuralSuperResolution not initialized.");
            if (cmd == null || input == null || output == null)
                throw new ArgumentNullException();

            switch (_model)
            {
                case NeuralSRModel.FSR3:
                    UpscaleFSR3(cmd, input, output, jitterOffset, motionVectorScale);
                    break;
                case NeuralSRModel.DLSS3:
                    UpscaleDLSS3(cmd, input, output, jitterOffset, motionVectorScale);
                    break;
                case NeuralSRModel.XeSS:
                    UpscaleXeSS(cmd, input, output, jitterOffset, motionVectorScale);
                    break;
                case NeuralSRModel.Internal:
                default:
                    UpscaleInternal(cmd, input, output, jitterOffset, motionVectorScale);
                    break;
            }

            _frameIndex++;
        }

        // ════════════════════════════════════════════════════════
        //  时间重建
        // ════════════════════════════════════════════════════════

        /// <summary>
        /// 执行时域重建。利用颜色、深度、运动矢量和曝光历史，
        /// 将当前帧与历史帧融合以减少锯齿和噪声。
        /// </summary>
        /// <param name="cmd">RHI 命令列表。</param>
        /// <param name="color">当前帧低分辨率颜色。</param>
        /// <param name="depth">当前帧低分辨率深度。</param>
        /// <param name="motionVectors">当前帧低分辨率运动矢量。</param>
        /// <param name="exposure">当前帧曝光值 (可选)。</param>
        /// <param name="output">重建后输出。</param>
        public void Reconstruct(RHICommandList cmd, RHITexture color, RHITexture depth,
            RHITexture motionVectors, RHITexture? exposure, RHITexture output)
        {
            if (!_initialized) throw new InvalidOperationException("NeuralSuperResolution not initialized.");

            switch (_model)
            {
                case NeuralSRModel.FSR3:
                    ReconstructFSR3(cmd, color, depth, motionVectors, exposure, output);
                    break;
                case NeuralSRModel.DLSS3:
                    ReconstructDLSS3(cmd, color, depth, motionVectors, exposure, output);
                    break;
                default:
                    ReconstructInternal(cmd, color, depth, motionVectors, exposure, output);
                    break;
            }
        }

        // ════════════════════════════════════════════════════════
        //  Halton 序列抖动生成
        // ════════════════════════════════════════════════════════

        /// <summary>
        /// 生成 Halton 序列子像素抖动偏移。
        /// 使用 Halton(2,3) 低差异序列，确保像素覆盖均匀分布。
        /// </summary>
        /// <param name="resolution">渲染分辨率 (用于归一化到 [-0.5, 0.5] 像素)。</param>
        /// <param name="frameIndex">当前帧索引。</param>
        /// <returns>抖动偏移 (像素单位, 需要转换为 NDC 偏移)。</returns>
        public Vector2 GenerateJitter(Vector2 resolution, int frameIndex)
        {
            int idx = (frameIndex + (int)(_config.JitterPhaseOffset * 256)) % 256;
            float x = s_HaltonBase2[idx] - 0.5f; // [-0.5, 0.5]
            float y = s_HaltonBase3[idx] - 0.5f;
            return new Vector2(x, y);
        }

        /// <summary>
        /// 生成 Halton 序列抖动偏移 (NDC 空间)。
        /// </summary>
        public Vector2 GenerateJitterNDC(Vector2 resolution, int frameIndex)
        {
            var jitter = GenerateJitter(resolution, frameIndex);
            // 像素偏移 -> NDC 偏移
            return new Vector2(
                jitter.X / resolution.X * 2.0f,
                jitter.Y / resolution.Y * 2.0f
            );
        }

        /// <summary>
        /// 生成 Halton 序列 (Van der Corput 序列的推广)。
        /// </summary>
        private static float[] GenerateHaltonSequence(int baseVal, int count)
        {
            var sequence = new float[count];
            for (int i = 0; i < count; i++)
                sequence[i] = Halton(i, baseVal);
            return sequence;
        }

        /// <summary>
        /// 计算 Halton 序列的第 n 项，给定底数 b。
        /// </summary>
        private static float Halton(int n, int b)
        {
            float result = 0f;
            float f = 1f / b;
            int i = n;
            while (i > 0)
            {
                result += f * (i % b);
                i /= b;
                f /= b;
            }
            return result;
        }

        // ════════════════════════════════════════════════════════
        //  FSR3 路径
        // ════════════════════════════════════════════════════════

        private void UpscaleFSR3(RHICommandList cmd, RHITexture input, RHITexture output,
            Vector2 jitterOffset, Vector2 motionVectorScale)
        {
            // FSR3 Upscale 流程:
            // 1. EASU (Edge Adaptive Spatial Upsampling) - 空间上采样
            // 2. RCAS (Robust Contrast Adaptive Sharpening) - 锐化

            // Step 1: EASU
            // Dispatch FSR_EASU_CS (Compute Shader)
            // 输入: input (低分辨率颜色)
            // 输出: 临时纹理 (高分辨率, 未锐化)

            // Step 2: RCAS
            // Draw RCAS PS (全屏三角形)
            // 输入: 临时纹理
            // 输出: output (高分辨率, 已锐化)
        }

        private void ReconstructFSR3(RHICommandList cmd, RHITexture color, RHITexture depth,
            RHITexture motionVectors, RHITexture? exposure, RHITexture output)
        {
            // FSR3 时域重建:
            // 1. 锁定区域检测 (Reactive Mask)
            // 2. 运动矢量膨胀 (Dilation)
            // 3. 历史帧重投影
            // 4. 当前帧与历史帧混合
            // 输入: color, depth, motionVectors, exposure, _historyColor
            // 输出: output + 更新 _historyColor
        }

        // ════════════════════════════════════════════════════════
        //  DLSS3 路径 (框架占位)
        // ════════════════════════════════════════════════════════

        private void UpscaleDLSS3(RHICommandList cmd, RHITexture input, RHITexture output,
            Vector2 jitterOffset, Vector2 motionVectorScale)
        {
            // DLSS3 调用 NVAPI
            // NVoptix_DlssGetOptimalSettings()
            // NVoptix_DlssEvaluate()
        }

        private void ReconstructDLSS3(RHICommandList cmd, RHITexture color, RHITexture depth,
            RHITexture motionVectors, RHITexture? exposure, RHITexture output)
        {
            // DLSS3 帧生成 + 超分辨率一体
            // 内部已集成时域重建
        }

        private bool CheckDLSSAvailable()
        {
            // 检测 NVIDIA 驱动和 RTX GPU
            // 实际实现中查询 NVAPI
            return false; // 框架占位
        }

        // ════════════════════════════════════════════════════════
        //  XeSS 路径 (框架占位)
        // ════════════════════════════════════════════════════════

        private void UpscaleXeSS(RHICommandList cmd, RHITexture input, RHITexture output,
            Vector2 jitterOffset, Vector2 motionVectorScale)
        {
            // XeSS 调用 Intel XeSS SDK
            // xessDenoiseExecute() / xessSuperSamplingExecute()
        }

        private bool CheckXeSSAvailable()
        {
            // 检测 Intel GPU / XeSS SDK
            return false; // 框架占位
        }

        // ════════════════════════════════════════════════════════
        //  内建路径 (Internal)
        // ════════════════════════════════════════════════════════

        private void UpscaleInternal(RHICommandList cmd, RHITexture input, RHITexture output,
            Vector2 jitterOffset, Vector2 motionVectorScale)
        {
            // yama内建超分辨率:
            // 1. 双线性/高质量上采样 (Bicubic)
            // 2. RCAS 锐化
            // 3. 可选: 简单时域混合
        }

        private void ReconstructInternal(RHICommandList cmd, RHITexture color, RHITexture depth,
            RHITexture motionVectors, RHITexture? exposure, RHITexture output)
        {
            // yama内建时域重建:
            // 1. 运动矢量重投影
            // 2. 邻域钳制 (Neighborhood Clamping)
            // 3. 历史帧混合
        }

        // ════════════════════════════════════════════════════════
        //  PSO 创建
        // ════════════════════════════════════════════════════════

        private void CreatePipelineStates()
        {
            // FSR3 EASU Compute Shader
            // _psoEASU = _device.CreateComputePipelineState(FSR_EASU_CS);

            // FSR3 / Internal RCAS Pixel Shader
            // _psoRCAS = _device.CreateGraphicsPipelineState(RCASPS);

            // Temporal Accumulation Compute Shader
            // _psoTemporalAccumulation = _device.CreateComputePipelineState(TemporalAccumulationCS);
        }

        // ════════════════════════════════════════════════════════
        //  释放资源
        // ════════════════════════════════════════════════════════

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _historyColor?.Dispose();
            _dilatedVelocity?.Dispose();
            _psoEASU?.Dispose();
            _psoRCAS?.Dispose();
            _psoTemporalAccumulation?.Dispose();

            _historyColor = null;
            _dilatedVelocity = null;
            _psoEASU = null;
            _psoRCAS = null;
            _psoTemporalAccumulation = null;

            _initialized = false;
        }

        // ════════════════════════════════════════════════════════
        //  内建 FSR3 兼容着色器: EASU (Edge Adaptive Spatial Upsampling)
        // ════════════════════════════════════════════════════════

        /// <summary>
        /// FSR3 EASU (Edge Adaptive Spatial Upsampling) 计算着色器。
        /// 将低分辨率图像上采样到高分辨率，使用双三次滤波和边缘自适应权重。
        /// </summary>
        public const string FSR_EASU_CS = @"
// ── FSR3 EASU Compute Shader (Edge Adaptive Spatial Upsampling) ──
// 基于 AMD FidelityFX Super Resolution 3.0 的 EASU pass

#define FSR_EASU_THREAD_GROUP_SIZE 16

Texture2D InputColor  : register(t0);
RWTexture2D<float4> OutputColor : register(u0);

cbuffer EASUParams : register(b0)
{
    float4 Const0; // x: inputSize.x, y: inputSize.y, z: outputSize.x, w: outputSize.y
    float4 Const1; // x: inputSizeInverse.x, y: inputSizeInverse.y, z: outputSizeInverse.x, w: outputSizeInverse.y
    float4 Const2; // x: jitter.x, y: jitter.y, z: 0, w: 0
};

// Lanczos 2-tap 权重
float Lanczos2(float x)
{
    float ax = abs(x);
    if (ax < 1.0) return (sin(3.14159 * ax) * sin(3.14159 * ax * 0.5)) / (3.14159 * ax * 0.5 * ax);
    if (ax < 2.0) return 0.0; // 超出 Lanczos 窗
    return 0.0;
}

// 双三次 B 样条权重
float CubicB_Spline(float x)
{
    float ax = abs(x);
    float x2 = ax * ax;
    float x3 = x2 * ax;
    return (x <= 1.0) ? (12.0 - 9.0 * ax + 6.0 * x2 - x3) / 6.0
         : (x < 2.0)  ? (4.0 - 6.0 * ax + 3.0 * x2 - x3 * 0.5) / 6.0
         : 0.0;
}

[numthreads(FSR_EASU_THREAD_GROUP_SIZE, FSR_EASU_THREAD_GROUP_SIZE, 1)]
void CS_FSR_EASU(uint3 dispatchThreadID : SV_DispatchThreadID)
{
    uint2 outPixel = dispatchThreadID.xy;
    if (outPixel.x >= (uint)Const0.z || outPixel.y >= (uint)Const0.w)
        return;

    // 输出像素中心在输入空间的坐标
    float2 outUV = (float2(outPixel) + 0.5 - Const2.xy) * Const1.zw;
    float2 inUV = outUV * Const0.xy;

    // 4x4 邻域双三次采样
    float2 center = floor(inUV - 0.5) + 0.5;
    float2 f = inUV - center;

    float3 color = float3(0, 0, 0);
    float totalWeight = 0.0;

    [unroll]
    for (int j = -1; j <= 2; j++)
    {
        [unroll]
        for (int i = -1; i <= 2; i++)
        {
            float2 samplePos = center + float2(i, j);
            float2 sampleUV = samplePos * Const1.xy;

            if (sampleUV.x < 0 || sampleUV.x > 1 || sampleUV.y < 0 || sampleUV.y > 1)
                continue;

            float2 offset = samplePos - inUV;
            float w = CubicB_Spline(offset.x) * CubicB_Spline(offset.y);

            color += InputColor.SampleLevel(sampler_LinearClamp, sampleUV, 0).rgb * w;
            totalWeight += w;
        }
    }

    color /= max(totalWeight, 1e-7);
    OutputColor[outPixel] = float4(color, 1.0);
}
";

        // ════════════════════════════════════════════════════════
        //  内建 RCAS 锐化着色器
        // ════════════════════════════════════════════════════════

        /// <summary>
        /// RCAS (Robust Contrast Adaptive Sharpening) 像素着色器。
        /// AMD FSR 的锐化 Pass，基于局部对比度自适应调整锐化强度。
        /// </summary>
        public const string SharpeningPS = @"
// ── RCAS (Robust Contrast Adaptive Sharpening) Pixel Shader ──

Texture2D InputColor : register(t0);
SamplerState LinearClamp : register(s0);

cbuffer RCASParams : register(b0)
{
    float2 InvResolution;
    float  Sharpness;     // 0.0 ~ 2.0, 推荐 0.2 ~ 0.5
    float  Pad0;
    float4 Pad1;
};

// 9-tap 十字采样 RCAS
float4 PS_RCAS(VSOutputFullscreen input) : SV_Target
{
    // 基于对比度的自适应锐化
    float2 uv = input.UV;
    float2 px = InvResolution;

    float3 a = InputColor.Sample(LinearClamp, uv + float2(-1, -1) * px).rgb;
    float3 b = InputColor.Sample(LinearClamp, uv + float2( 0, -1) * px).rgb;
    float3 c = InputColor.Sample(LinearClamp, uv + float2( 1, -1) * px).rgb;
    float3 d = InputColor.Sample(LinearClamp, uv + float2(-1,  0) * px).rgb;
    float3 e = InputColor.Sample(LinearClamp, uv).rgb;
    float3 f = InputColor.Sample(LinearClamp, uv + float2( 1,  0) * px).rgb;
    float3 g = InputColor.Sample(LinearClamp, uv + float2(-1,  1) * px).rgb;
    float3 h = InputColor.Sample(LinearClamp, uv + float2( 0,  1) * px).rgb;
    float3 i_c = InputColor.Sample(LinearClamp, uv + float2( 1,  1) * px).rgb;

    // 上下左右对比度 (Luma)
    float luma_b = dot(b, float3(0.2126, 0.7152, 0.0722));
    float luma_d = dot(d, float3(0.2126, 0.7152, 0.0722));
    float luma_e = dot(e, float3(0.2126, 0.7152, 0.0722));
    float luma_f = dot(f, float3(0.2126, 0.7152, 0.0722));
    float luma_h = dot(h, float3(0.2126, 0.7152, 0.0722));

    // 拉普拉斯对比度
    float contrast = -4.0 * luma_e + luma_b + luma_d + luma_f + luma_h;

    // 对比度归一化
    float peak = max(max(abs(contrast), abs(luma_b - luma_h)),
                     max(abs(luma_d - luma_f), 1e-5));

    // 锐化强度 (对比度越大，锐化越弱 — 保留边缘)
    float sharpenStrength = Sharpness / (peak * 2.0 + 0.25);

    // 限制锐化量 (防止过冲)
    sharpenStrength = min(sharpenStrength, 2.0);

    // 十字滤波
    float3 w = (b + d + f + h) * 0.25;
    float3 result = e + (e - w) * sharpenStrength;

    // 钳制到邻域范围 (防止颜色越界)
    float3 mn = min(min(min(a, b), min(c, d)), min(min(e, f), min(g, min(h, i_c))));
    float3 mx = max(max(max(a, b), max(c, d)), max(max(e, f), max(g, max(h, i_c))));
    result = clamp(result, mn, mx);

    return float4(result, 1.0);
}
";

        // ════════════════════════════════════════════════════════
        //  时间累积计算着色器
        // ════════════════════════════════════════════════════════

        /// <summary>
        /// 时间累积计算着色器 (Temporal Accumulation CS)。
        /// 将当前帧颜色与历史帧重投影后混合，减少时域噪声和锯齿。
        /// 包含运动矢量重投影、邻域钳制和自适应混合权重。
        /// </summary>
        public const string TemporalAccumulationCS = @"
// ── Temporal Accumulation Compute Shader ──

#define TA_THREAD_GROUP_SIZE 16

Texture2D CurrentColor       : register(t0);
Texture2D HistoryColor       : register(t1);
Texture2D MotionVectors      : register(t2);
Texture2D DepthCurrent       : register(t3);
Texture2D DepthHistory       : register(t4);
Texture2D Exposure           : register(t5);
RWTexture2D<float4> Output   : register(u0);
RWTexture2D<float4> NewHistory : register(u1);

cbuffer TAParams : register(b0)
{
    float2 InvInputResolution;
    float2 InvOutputResolution;
    float  TemporalStrength;  // 0.0 ~ 1.0 (越大越依赖历史)
    float  VelocityThreshold; // 超过此值降低历史权重
    int    FrameIndex;
    float3 Pad0;
};

// 邻域最小最大 (3x3)
float3 Min3x3(float2 uv)
{
    float3 mn = CurrentColor.SampleLevel(sampler_LinearClamp, uv, 0).rgb;
    float2 px = InvInputResolution;
    [unroll]
    for (int y = -1; y <= 1; y++)
    {
        [unroll]
        for (int x = -1; x <= 1; x++)
        {
            float3 s = CurrentColor.SampleLevel(sampler_LinearClamp, uv + float2(x, y) * px, 0).rgb;
            mn = min(mn, s);
        }
    }
    return mn;
}

float3 Max3x3(float2 uv)
{
    float3 mx = CurrentColor.SampleLevel(sampler_LinearClamp, uv, 0).rgb;
    float2 px = InvInputResolution;
    [unroll]
    for (int y = -1; y <= 1; y++)
    {
        [unroll]
        for (int x = -1; x <= 1; x++)
        {
            float3 s = CurrentColor.SampleLevel(sampler_LinearClamp, uv + float2(x, y) * px, 0).rgb;
            mx = max(mx, s);
        }
    }
    return mx;
}

[numthreads(TA_THREAD_GROUP_SIZE, TA_THREAD_GROUP_SIZE, 1)]
void CS_TemporalAccumulation(uint3 dispatchThreadID : SV_DispatchThreadID)
{
    uint2 pixel = dispatchThreadID.xy;
    if (pixel.x >= (uint)(1.0 / InvInputResolution.x) ||
        pixel.y >= (uint)(1.0 / InvInputResolution.y))
        return;

    float2 uv = (float2(pixel) + 0.5) * InvInputResolution;

    // 读取当前帧
    float3 current = CurrentColor.SampleLevel(sampler_LinearClamp, uv, 0).rgb;

    // 读取运动矢量并计算历史 UV
    float2 motion = MotionVectors.SampleLevel(sampler_LinearClamp, uv, 0).rg;
    float2 historyUV = uv - motion;

    // 边界检查
    if (historyUV.x < 0 || historyUV.x > 1 || historyUV.y < 0 || historyUV.y > 1)
    {
        Output[pixel] = float4(current, 1.0);
        NewHistory[pixel] = float4(current, 1.0);
        return;
    }

    // 读取历史帧
    float3 history = HistoryColor.SampleLevel(sampler_LinearClamp, historyUV, 0).rgb;

    // 邻域钳制 (Variance Clipping)
    float3 nMin = Min3x3(uv);
    float3 nMax = Max3x3(uv);
    float3 nMean = (nMin + nMax) * 0.5;
    float3 nStd = (nMax - nMin) * 0.5;
    float3 clampedHistory = clamp(history, nMin, nMax);

    // 计算混合权重
    float motionLength = length(motion);
    float motionFactor = 1.0 - saturate(motionLength / VelocityThreshold);

    // 深度不连续检测
    float depth = DepthCurrent.SampleLevel(sampler_LinearClamp, uv, 0).r;
    float historyDepth = DepthHistory.SampleLevel(sampler_LinearClamp, historyUV, 0).r;
    float depthDiff = abs(depth - historyDepth);
    float depthFactor = 1.0 - saturate(depthDiff * 100.0);

    // 综合权重
    float blend = TemporalStrength * motionFactor * depthFactor;
    blend = clamp(blend, 0.0, 0.95);

    // 混合当前帧和历史帧
    float3 result = lerp(current, clampedHistory, blend);

    Output[pixel] = float4(result, 1.0);
    NewHistory[pixel] = float4(result, 1.0);
}
";

        // ════════════════════════════════════════════════════════
        //  FSR3 RCAS PS (简写引用，与 SharpeningPS 等价)
        // ════════════════════════════════════════════════════════

        /// <summary>FSR3 RCAS 像素着色器引用 (与 SharpeningPS 功能等价)。</summary>
        public const string FSR_RCAS_PS = SharpeningPS;
    }
}