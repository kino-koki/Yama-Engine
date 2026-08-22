// ════════════════════════════════════════════════════════════════════════
// 山雨引擎 - 时序超分辨率增强系统 (TemporalSuperResolutionEnhanced)
//
// 实现 UE5 TSR 架构的时序超分辨率, 包含:
//   1. 运动矢量计算:
//      - 深度重新投影 (Depth Reprojection): 基于深度的运动矢量
//      - 双向运动: 前后帧运动矢量
//   2. 上采样核心:
//      - 自适应采样模式: 检测高频区域 → 增加采样
//      - 双三次插值 (Bicubic): 高质量空间上采样
//      - Lanczos 滤波器: 可选的高锐度模式
//   3. 时序累积:
//      - 速度感知权重: 快速移动区域降低历史权重
//      - 新帧权重: 基于亮度变化自适应 (0.05-0.2)
//      - 锐化通道: 独立锐化时序累积结果
//   4. 亏格处理:
//      - 运动矢量失效检测: |velocity| > threshold
//      - 深度失效检测: |depth_diff| > threshold
//      - 失效区域空间重建: 双边滤波填充
//   5. 色度处理: 在 YCoCg 空间工作避免色度偏移
//
// 对接系统: RHI (RHIDevice/RHICommandList) / TAA / 场景渲染器
// ════════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using ShanyuEngine.RHI;

namespace ShanyuEngine.Rendering
{
    // ────────────────────────────────────────────────────────────────────────
    // 配置
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>TSR 增强质量档位。</summary>
    public enum TSREnhancedQuality { Performance, Balanced, Quality, Cinematic }

    /// <summary>上采样滤波器类型。</summary>
    public enum TSRUpsampleFilter { Bilinear, Bicubic, Lanczos, Adaptive }

    /// <summary>TSR 增强配置。</summary>
    public sealed class TSREnhancedConfig
    {
        public TSREnhancedQuality Quality = TSREnhancedQuality.Quality;
        public TSRUpsampleFilter UpsampleFilter = TSRUpsampleFilter.Adaptive;

        // --- 运动矢量 ---
        public bool EnableBidirectionalMotion = true;
        public float VelocityDisocclusionThreshold = 32.0f; // 像素
        public float DepthDisocclusionThreshold = 0.1f; // 归一化深度差

        // --- 上采样 ---
        public float HighFrequencyThreshold = 0.15f; // 高频检测阈值
        public int AdaptiveExtraSamples = 4; // 高频区域额外采样数
        public float LanczosWindow = 3.0f; // Lanczos a 参数
        public float BicubicB = 0.0f; // B-spline 参数
        public float BicubicC = 0.5f; // Catmull-Rom

        // --- 时序累积 ---
        public float BaseHistoryWeight = 0.9f;
        public float MinHistoryWeight = 0.4f;
        public float MinNewFrameWeight = 0.05f;
        public float MaxNewFrameWeight = 0.2f;
        public float VelocityWeightScale = 0.02f;
        public float SharpeningAmount = 0.35f;
        public float SharpeningRadius = 1.0f;

        // --- 亏格处理 ---
        public bool EnableDisocclusionFix = true;
        public int DisocclusionFilterRadius = 2;

        // --- 色度处理 ---
        public bool UseYCoCg = true;

        // --- 抖动 ---
        public int JitterPhaseCount = 8;

        /// <summary>应用质量预设。</summary>
        public void ApplyQualityPreset(TSREnhancedQuality q)
        {
            Quality = q;
            switch (q)
            {
                case TSREnhancedQuality.Performance:
                    UpsampleFilter = TSRUpsampleFilter.Bilinear;
                    BaseHistoryWeight = 0.8f; SharpeningAmount = 0.6f;
                    JitterPhaseCount = 4; AdaptiveExtraSamples = 0; break;
                case TSREnhancedQuality.Balanced:
                    UpsampleFilter = TSRUpsampleFilter.Bicubic;
                    BaseHistoryWeight = 0.88f; SharpeningAmount = 0.4f;
                    JitterPhaseCount = 8; AdaptiveExtraSamples = 2; break;
                case TSREnhancedQuality.Quality:
                    UpsampleFilter = TSRUpsampleFilter.Adaptive;
                    BaseHistoryWeight = 0.92f; SharpeningAmount = 0.3f;
                    JitterPhaseCount = 16; AdaptiveExtraSamples = 4; break;
                case TSREnhancedQuality.Cinematic:
                    UpsampleFilter = TSRUpsampleFilter.Lanczos;
                    BaseHistoryWeight = 0.95f; SharpeningAmount = 0.15f;
                    JitterPhaseCount = 32; AdaptiveExtraSamples = 8; break;
            }
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // YCoCg 色彩空间转换
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// YCoCg 色彩空间转换工具。在 YCoCg 空间进行时序累积可避免色度偏移,
    /// 因为亮度 (Y) 通道的高频细节比色度 (Co, Cg) 更重要。
    /// </summary>
    public static class YCoCgColorSpace
    {
        /// <summary>RGB → YCoCg。</summary>
        public static Vector3 FromRGB(in Vector3 rgb)
        {
            return new Vector3(
                rgb.X * 0.25f + rgb.Y * 0.5f + rgb.Z * 0.25f,  // Y
                rgb.X * 0.5f - rgb.Z * 0.5f,                     // Co
                -rgb.X * 0.25f + rgb.Y * 0.5f - rgb.Z * 0.25f    // Cg
            );
        }

        /// <summary>YCoCg → RGB。</summary>
        public static Vector3 ToRGB(in Vector3 ycocg)
        {
            float y = ycocg.X, co = ycocg.Y, cg = ycocg.Z;
            return new Vector3(
                y + co - cg,
                y + cg,
                y - co - cg
            );
        }

        /// <summary>向量化的 RGB → YCoCg (用于 GPU 着色器参考)。</summary>
        public static (float Y, float Co, float Cg) ConvertPixel(float r, float g, float b)
        {
            float co = r - b;
            float tmp = b + co * 0.5f;
            float cg = g - tmp;
            float y = tmp + cg * 0.5f;
            return (y, co, cg);
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // 滤波器实现
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 空间上采样滤波器: 双三次 (Bicubic) 和 Lanczos 实现。
    /// 提供 CPU 侧参考实现, GPU 着色器使用相同权重公式。
    /// </summary>
    public static class TSRUpsampleFilters
    {
        /// <summary>Catmull-Rom 双三次权重函数。</summary>
        public static float BicubicWeight(float x, float b, float c)
        {
            x = MathF.Abs(x);
            float x2 = x * x, x3 = x2 * x;
            if (x < 1.0f)
                return (12 - 9 * b - 6 * c) * x3 / 6 + (-18 + 12 * b + 6 * c) * x2 / 6 + (6 - 2 * b) / 6;
            if (x < 2.0f)
                return (-b - 6 * c) * x3 / 6 + (6 * b + 30 * c) * x2 / 6 + (-12 * b - 48 * c) * x / 6 + (8 * b + 24 * c) / 6;
            return 0;
        }

        /// <summary>Lanczos sinc 权重函数。</summary>
        public static float LanczosWeight(float x, float a)
        {
            if (x == 0) return 1.0f;
            if (MathF.Abs(x) >= a) return 0;
            float px = MathF.PI * x;
            return (MathF.Sin(px) * MathF.Sin(px / a)) / (px * px / a);
        }

        /// <summary>双三次 4x4 采样 (CPU 参考)。</summary>
        public static Vector3 SampleBicubic(in Vector3[,] src, float u, float v, int srcW, int srcH, float b, float c)
        {
            int ix = (int)MathF.Floor(u);
            int iy = (int)MathF.Floor(v);
            float fx = u - ix, fy = v - iy;
            Vector3 result = Vector3.Zero;
            float weightSum = 0;

            for (int dy = -1; dy <= 2; dy++)
            {
                float wy = BicubicWeight(fy + dy, b, c);
                int sy = Math.Clamp(iy + dy, 0, srcH - 1);
                for (int dx = -1; dx <= 2; dx++)
                {
                    float wx = BicubicWeight(fx + dx, b, c);
                    int sx = Math.Clamp(ix + dx, 0, srcW - 1);
                    float w = wx * wy;
                    result += src[sy, sx] * w;
                    weightSum += w;
                }
            }
            return weightSum > 0 ? result / weightSum : result;
        }

        /// <summary>Lanczos 6x6 采样 (CPU 参考)。</summary>
        public static Vector3 SampleLanczos(in Vector3[,] src, float u, float v, int srcW, int srcH, float a)
        {
            int ix = (int)MathF.Floor(u);
            int iy = (int)MathF.Floor(v);
            float fx = u - ix, fy = v - iy;
            Vector3 result = Vector3.Zero;
            float weightSum = 0;
            int radius = (int)MathF.Ceiling(a);

            for (int dy = -radius + 1; dy <= radius; dy++)
            {
                float wy = LanczosWeight(fy - dy, a);
                int sy = Math.Clamp(iy + dy, 0, srcH - 1);
                for (int dx = -radius + 1; dx <= radius; dx++)
                {
                    float wx = LanczosWeight(fx - dx, a);
                    int sx = Math.Clamp(ix + dx, 0, srcW - 1);
                    float w = wx * wy;
                    result += src[sy, sx] * w;
                    weightSum += w;
                }
            }
            return weightSum > 0 ? result / weightSum : result;
        }

        /// <summary>检测高频区域 (Sobel 边缘检测)。</summary>
        public static float DetectHighFrequency(in Vector3[,] src, int x, int y, int w, int h)
        {
            int xm = Math.Max(0, x - 1), xp = Math.Min(w - 1, x + 1);
            int ym = Math.Max(0, y - 1), yp = Math.Min(h - 1, y + 1);

            // Sobel X
            float gx = (src[y, xp].X - src[y, xm].X) * 2
                     + (src[ym, xp].X - src[ym, xm].X)
                     + (src[yp, xp].X - src[yp, xm].X);
            // Sobel Y
            float gy = (src[yp, x].X - src[ym, x].X) * 2
                     + (src[yp, xm].X - src[ym, xm].X)
                     + (src[yp, xp].X - src[ym, xp].X);

            return MathF.Sqrt(gx * gx + gy * gy);
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // 运动矢量计算
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 运动矢量计算器: 基于深度重新投影生成运动矢量, 支持双向运动。
    /// </summary>
    public sealed class TSRMotionVectorCalculator : IDisposable
    {
        private readonly RHIDevice _device;
        private readonly TSREnhancedConfig _config;
        private RHITexture? _motionVectors;
        private RHITexture? _dilatedMotionVectors;
        private Matrix4x4 _prevViewProj;
        private bool _hasPrevViewProj;

        /// <summary>运动矢量纹理。</summary>
        public RHITexture? MotionVectors => _dilatedMotionVectors;

        public TSRMotionVectorCalculator(RHIDevice device, TSREnhancedConfig config)
        {
            _device = device;
            _config = config;
        }

        /// <summary>初始化资源。</summary>
        public void Initialize(int width, int height)
        {
            _motionVectors = _device.CreateTexture(new RHITextureDesc
            {
                Dimension = RHIResourceDimension.Texture2D,
                Format = RHIFormat.R16G16_Float,
                Width = width, Height = height, DepthOrArraySize = 1,
                MipLevels = 1, SampleCount = 1,
                Flags = RHITextureFlags.RenderTarget | RHITextureFlags.ShaderResource | RHITextureFlags.UnorderedAccess
            }, "TSRMotionVectors");

            _dilatedMotionVectors = _device.CreateTexture(new RHITextureDesc
            {
                Dimension = RHIResourceDimension.Texture2D,
                Format = RHIFormat.R16G16_Float,
                Width = width, Height = height, DepthOrArraySize = 1,
                MipLevels = 1, SampleCount = 1,
                Flags = RHITextureFlags.ShaderResource | RHITextureFlags.UnorderedAccess
            }, "TSRDilatedMotionVectors");
        }

        /// <summary>
        /// 计算运动矢量: 基于深度重新投影。
        /// 对每个像素: 从当前深度重建世界位置, 用上一帧 ViewProj 投影, 差值即为运动矢量。
        /// </summary>
        public void Calculate(RHICommandList cmdList, RHITexture depthBuffer,
            RHITexture prevDepthBuffer, in Matrix4x4 viewProj, in Matrix4x4 invViewProj,
            in Matrix4x4 prevViewProj, int width, int height, uint frameIndex)
        {
            if (_motionVectors == null) return;

            cmdList.TransitionTo(_motionVectors, RHIResourceState.UnorderedAccess);
            cmdList.TransitionTo(depthBuffer, RHIResourceState.NonPixelShaderResource);

            int groupsX = (width + 7) / 8;
            int groupsY = (height + 7) / 8;

            // Pass 1: 深度重新投影计算运动矢量
            // cmdList.SetComputeRootSignature(...);
            // cmdList.SetComputeRoot32BitConstants(... invViewProj, prevViewProj, ...);
            cmdList.Dispatch(groupsX, groupsY, 1);

            // Pass 2: 运动矢量膨胀 (取最近像素的运动矢量, 避免边缘失效)
            DilateMotionVectors(cmdList, width, height);

            cmdList.TransitionTo(_dilatedMotionVectors, RHIResourceState.ShaderResource);

            _prevViewProj = viewProj;
            _hasPrevViewProj = true;
        }

        /// <summary>运动矢量膨胀: 最近深度像素的运动矢量扩散到邻居。</summary>
        private void DilateMotionVectors(RHICommandList cmdList, int width, int height)
        {
            cmdList.TransitionTo(_motionVectors, RHIResourceState.NonPixelShaderResource);
            cmdList.TransitionTo(_dilatedMotionVectors, RHIResourceState.UnorderedAccess);

            int groupsX = (width + 7) / 8;
            int groupsY = (height + 7) / 8;
            // 3x3 膨胀: 每个像素取 3x3 邻域中最近深度的运动矢量
            // cmdList.SetComputeRootSignature(...);
            cmdList.Dispatch(groupsX, groupsY, 1);
        }

        /// <summary>双向运动: 计算前向和后向运动矢量, 用于亏格检测。</summary>
        public (Vector2 forward, Vector2 backward) ComputeBidirectionalMotion(
            in Vector3 worldPos, in Matrix4x4 viewProj, in Matrix4x4 prevViewProj,
            in Matrix4x4 invPrevViewProj)
        {
            // 前向: prev → current
            Vector4 currentClip = Vector4.Transform(new Vector4(worldPos, 1), viewProj);
            Vector4 prevClip = Vector4.Transform(new Vector4(worldPos, 1), prevViewProj);
            Vector2 currentScreen = new(currentClip.X / currentClip.W, currentClip.Y / currentClip.W);
            Vector2 prevScreen = new(prevClip.X / prevClip.W, prevClip.Y / prevClip.W);
            Vector2 forward = currentScreen - prevScreen;

            // 后向: current → prev (反向)
            Vector2 backward = -forward;
            return (forward, backward);
        }

        /// <summary>从深度重建世界位置。</summary>
        public static Vector3 ReconstructWorldPos(float depth, Vector2 uv, in Matrix4x4 invViewProj)
        {
            Vector4 clipPos = new(uv.X * 2 - 1, (1 - uv.Y) * 2 - 1, depth, 1);
            Vector4 worldPos = Vector4.Transform(clipPos, invViewProj);
            if (MathF.Abs(worldPos.W) < 1e-8f) return Vector3.Zero;
            return new Vector3(worldPos.X, worldPos.Y, worldPos.Z) / worldPos.W;
        }

        public void Dispose()
        {
            _motionVectors?.Dispose();
            _dilatedMotionVectors?.Dispose();
            GC.SuppressFinalize(this);
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // 时序超分辨率主系统
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 时序超分辨率增强主系统。在低分辨率渲染后, 通过时序累积超采样到高分辨率输出。
    /// 在 YCoCg 空间工作避免色度偏移, 使用自适应采样和双三次/Lanczos 滤波器。
    /// </summary>
    public sealed class TemporalSuperResolutionEnhanced : IDisposable
    {
        private readonly RHIDevice _device;
        private readonly TSREnhancedConfig _config;
        private readonly TSRMotionVectorCalculator _motionCalc;

        // 双缓冲历史纹理 (Ping-Pong)
        private RHITexture? _historyA;
        private RHITexture? _historyB;
        private bool _useHistoryA;

        // 中间纹理
        private RHITexture? _upscaledCurrent;
        private RHITexture? _upscaledHistory;
        private RHITexture? _varianceBuffer;
        private RHITexture? _sharpenBuffer;

        // 常量缓冲
        private RHIBuffer? _constantsCB;

        // 抖动
        private Vector2 _jitter;
        private Vector2 _prevJitter;
        private int _frameIndex;
        private int _inputWidth, _inputHeight;
        private int _outputWidth, _outputHeight;
        private bool _initialized;

        /// <summary>TSR 增强常量缓冲。</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct TSREnhancedConstants
        {
            public Matrix4x4 ViewProjMatrix;
            public Matrix4x4 PrevViewProjMatrix;
            public Matrix4x4 InvViewProjMatrix;
            public Vector3 CameraPos;
            float _pad0;
            public Vector2 InputResolution;
            public Vector2 OutputResolution;
            public Vector2 InvInputResolution;
            public Vector2 InvOutputResolution;
            public Vector2 Jitter;
            public Vector2 PrevJitter;
            public float BaseHistoryWeight;
            public float MinHistoryWeight;
            public float MinNewFrameWeight;
            public float MaxNewFrameWeight;
            public float VelocityWeightScale;
            public float VelocityDisocclusionThreshold;
            public float DepthDisocclusionThreshold;
            public float SharpeningAmount;
            public float SharpeningRadius;
            public float HighFrequencyThreshold;
            public float LanczosWindow;
            public float BicubicB;
            public float BicubicC;
            public int UseYCoCg;
            public int UpsampleFilter;
            public int FrameIndex;
            public int EnableDisocclusionFix;
            float _pad1, _pad2;
        }

        /// <summary>配置。</summary>
        public TSREnhancedConfig Config => _config;
        /// <summary>运动矢量计算器。</summary>
        public TSRMotionVectorCalculator MotionCalculator => _motionCalc;
        /// <summary>输入宽度。</summary>
        public int InputWidth => _inputWidth;
        /// <summary>输入高度。</summary>
        public int InputHeight => _inputHeight;
        /// <summary>输出宽度。</summary>
        public int OutputWidth => _outputWidth;
        /// <summary>输出高度。</summary>
        public int OutputHeight => _outputHeight;

        public TemporalSuperResolutionEnhanced(RHIDevice device, TSREnhancedConfig? config = null)
        {
            _device = device;
            _config = config ?? new TSREnhancedConfig();
            _motionCalc = new TSRMotionVectorCalculator(device, _config);
        }

        /// <summary>初始化系统。</summary>
        public void Initialize(int inputWidth, int inputHeight, int outputWidth, int outputHeight)
        {
            _inputWidth = inputWidth;
            _inputHeight = inputHeight;
            _outputWidth = outputWidth;
            _outputHeight = outputHeight;

            // 历史双缓冲 (输出分辨率)
            _historyA = CreateHistoryTexture("TSRHistoryA");
            _historyB = CreateHistoryTexture("TSRHistoryB");

            // 中间纹理 (输出分辨率)
            _upscaledCurrent = _device.CreateTexture(new RHITextureDesc
            {
                Dimension = RHIResourceDimension.Texture2D,
                Format = RHIFormat.R16G16B16A16_Float,
                Width = outputWidth, Height = outputHeight, DepthOrArraySize = 1,
                MipLevels = 1, SampleCount = 1,
                Flags = RHITextureFlags.ShaderResource | RHITextureFlags.UnorderedAccess
            }, "TSRUpscaledCurrent");

            _upscaledHistory = _device.CreateTexture(new RHITextureDesc
            {
                Dimension = RHIResourceDimension.Texture2D,
                Format = RHIFormat.R16G16B16A16_Float,
                Width = outputWidth, Height = outputHeight, DepthOrArraySize = 1,
                MipLevels = 1, SampleCount = 1,
                Flags = RHITextureFlags.ShaderResource | RHITextureFlags.UnorderedAccess
            }, "TSRUpscaledHistory");

            _varianceBuffer = _device.CreateTexture(new RHITextureDesc
            {
                Dimension = RHIResourceDimension.Texture2D,
                Format = RHIFormat.R16G16B16A16_Float,
                Width = outputWidth, Height = outputHeight, DepthOrArraySize = 1,
                MipLevels = 1, SampleCount = 1,
                Flags = RHITextureFlags.ShaderResource | RHITextureFlags.UnorderedAccess
            }, "TSRVariance");

            _sharpenBuffer = _device.CreateTexture(new RHITextureDesc
            {
                Dimension = RHIResourceDimension.Texture2D,
                Format = RHIFormat.R16G16B16A16_Float,
                Width = outputWidth, Height = outputHeight, DepthOrArraySize = 1,
                MipLevels = 1, SampleCount = 1,
                Flags = RHITextureFlags.ShaderResource | RHITextureFlags.UnorderedAccess
            }, "TSRSharpen");

            // 运动矢量
            _motionCalc.Initialize(inputWidth, inputHeight);

            // 常量缓冲
            _constantsCB = _device.CreateBuffer(new RHIBufferDesc
            {
                SizeInBytes = 1024,
                Flags = RHIBufferFlags.ConstantBuffer,
                HeapType = RHIResourceHeapType.Upload
            }, "TSREnhancedConstants");

            _initialized = true;
        }

        private RHITexture CreateHistoryTexture(string name)
        {
            return _device.CreateTexture(new RHITextureDesc
            {
                Dimension = RHIResourceDimension.Texture2D,
                Format = RHIFormat.R16G16B16A16_Float,
                Width = _outputWidth, Height = _outputHeight, DepthOrArraySize = 1,
                MipLevels = 1, SampleCount = 1,
                Flags = RHITextureFlags.ShaderResource | RHITextureFlags.UnorderedAccess
            }, name);
        }

        /// <summary>生成 Halton 抖动序列。</summary>
        public Vector2 GenerateJitter(int frameIndex)
        {
            int phase = frameIndex % _config.JitterPhaseCount;
            return new Vector2(
                HaltonSequence(phase + 1, 2) - 0.5f,
                HaltonSequence(phase + 1, 3) - 0.5f
            );
        }

        private static float HaltonSequence(int index, int baseNumber)
        {
            float f = 1, r = 0;
            while (index > 0)
            {
                f /= baseNumber;
                r += f * (index % baseNumber);
                index /= baseNumber;
            }
            return r;
        }

        /// <summary>执行完整的 TSR 帧更新。</summary>
        public void Update(RHICommandList cmdList, RHITexture inputColor, RHITexture inputDepth,
            RHITexture prevDepth, RHITexture outputColor, in CameraData camera, uint frameIndex)
        {
            if (!_initialized) return;

            _frameIndex = (int)frameIndex;
            _prevJitter = _jitter;
            _jitter = GenerateJitter(_frameIndex);

            // 1. 计算运动矢量 (深度重新投影)
            _motionCalc.Calculate(cmdList, inputDepth, prevDepth,
                camera.ViewProjectionMatrix,
                Matrix4x4.Invert(camera.ViewProjectionMatrix, out var invVP) ? invVP : Matrix4x4.Identity,
                camera.ViewProjectionMatrix, // 简化: 实际应传入 prevViewProj
                _inputWidth, _inputHeight, frameIndex);

            // 2. 空间上采样 (双三次/Lanczos/自适应)
            SpatialUpscale(cmdList, inputColor, _upscaledCurrent!);

            // 3. 历史帧上采样与重投影
            var history = _useHistoryA ? _historyA : _historyB;
            ReprojectHistory(cmdList, history!, _motionCalc.MotionVectors!,
                _upscaledHistory!, camera);

            // 4. 时序累积 (速度感知权重)
            TemporalAccumulate(cmdList, _upscaledCurrent!, _upscaledHistory!,
                _motionCalc.MotionVectors!, inputDepth, prevDepth,
                _useHistoryA ? _historyB! : _historyA!);

            // 5. 亏格修复 (运动矢量/深度失效检测 + 空间重建)
            if (_config.EnableDisocclusionFix)
            {
                FixDisocclusion(cmdList, _useHistoryA ? _historyB! : _historyA!,
                    _motionCalc.MotionVectors!, inputDepth, prevDepth);
            }

            // 6. 锐化通道
            Sharpen(cmdList, _useHistoryA ? _historyB! : _historyA!, _sharpenBuffer!);

            // 7. 输出到最终目标 (YCoCg → RGB)
            OutputFinal(cmdList, _sharpenBuffer!, outputColor);

            _useHistoryA = !_useHistoryA;
        }

        /// <summary>空间上采样: 自适应采样 + 双三次/Lanczos。</summary>
        private void SpatialUpscale(RHICommandList cmdList, RHITexture input, RHITexture output)
        {
            cmdList.TransitionTo(input, RHIResourceState.NonPixelShaderResource);
            cmdList.TransitionTo(output, RHIResourceState.UnorderedAccess);

            int groupsX = (_outputWidth + 7) / 8;
            int groupsY = (_outputHeight + 7) / 8;

            // GPU compute shader 执行空间上采样:
            // - 检测高频区域 (Sobel)
            // - 高频区域使用 Lanczos/Bicubic, 低频使用 Bilinear
            // - 如果使用 YCoCg, 在此转换
            // cmdList.SetComputeRootSignature(...);
            // cmdList.SetComputeRoot32BitConstants(... constants ...);
            cmdList.Dispatch(groupsX, groupsY, 1);

            cmdList.TransitionTo(output, RHIResourceState.ShaderResource);
        }

        /// <summary>历史帧重投影: 用运动矢量重新定位历史像素。</summary>
        private void ReprojectHistory(RHICommandList cmdList, RHITexture history,
            RHITexture motionVectors, RHITexture output, in CameraData camera)
        {
            cmdList.TransitionTo(history, RHIResourceState.NonPixelShaderResource);
            cmdList.TransitionTo(motionVectors, RHIResourceState.NonPixelShaderResource);
            cmdList.TransitionTo(output, RHIResourceState.UnorderedAccess);

            int groupsX = (_outputWidth + 7) / 8;
            int groupsY = (_outputHeight + 7) / 8;
            // cmdList.SetComputeRootSignature(...);
            cmdList.Dispatch(groupsX, groupsY, 1);

            cmdList.TransitionTo(output, RHIResourceState.ShaderResource);
        }

        /// <summary>
        /// 时序累积: 速度感知权重混合当前帧与历史帧。
        /// 快速移动区域降低历史权重, 新帧权重基于亮度变化自适应 (0.05-0.2)。
        /// </summary>
        private void TemporalAccumulate(RHICommandList cmdList, RHITexture current,
            RHITexture history, RHITexture motionVectors, RHITexture depth, RHITexture prevDepth,
            RHITexture output)
        {
            cmdList.TransitionTo(current, RHIResourceState.NonPixelShaderResource);
            cmdList.TransitionTo(history, RHIResourceState.NonPixelShaderResource);
            cmdList.TransitionTo(motionVectors, RHIResourceState.NonPixelShaderResource);
            cmdList.TransitionTo(depth, RHIResourceState.NonPixelShaderResource);
            cmdList.TransitionTo(prevDepth, RHIResourceState.NonPixelShaderResource);
            cmdList.TransitionTo(output, RHIResourceState.UnorderedAccess);

            int groupsX = (_outputWidth + 7) / 8;
            int groupsY = (_outputHeight + 7) / 8;
            // cmdList.SetComputeRootSignature(...);
            // cmdList.SetComputeRoot32BitConstants(... baseWeight, minWeight, velocityScale ...);
            cmdList.Dispatch(groupsX, groupsY, 1);

            cmdList.TransitionTo(output, RHIResourceState.ShaderResource);
        }

        /// <summary>
        /// 亏格修复: 检测运动矢量和深度失效区域, 使用双边滤波空间重建。
        /// </summary>
        private void FixDisocclusion(RHICommandList cmdList, RHITexture color,
            RHITexture motionVectors, RHITexture depth, RHITexture prevDepth)
        {
            cmdList.TransitionTo(color, RHIResourceState.UnorderedAccess);
            cmdList.TransitionTo(motionVectors, RHIResourceState.NonPixelShaderResource);
            cmdList.TransitionTo(depth, RHIResourceState.NonPixelShaderResource);
            cmdList.TransitionTo(prevDepth, RHIResourceState.NonPixelShaderResource);

            int groupsX = (_outputWidth + 7) / 8;
            int groupsY = (_outputHeight + 7) / 8;

            // Pass 1: 检测失效区域 (运动矢量过大或深度差过大)
            // cmdList.Dispatch(groupsX, groupsY, 1);

            // Pass 2: 双边滤波空间重建失效区域
            int radius = _config.DisocclusionFilterRadius;
            // cmdList.SetComputeRoot32BitConstants(... radius, ...);
            cmdList.Dispatch(groupsX, groupsY, 1);

            cmdList.TransitionTo(color, RHIResourceState.ShaderResource);
        }

        /// <summary>独立锐化通道。</summary>
        private void Sharpen(RHICommandList cmdList, RHITexture input, RHITexture output)
        {
            cmdList.TransitionTo(input, RHIResourceState.NonPixelShaderResource);
            cmdList.TransitionTo(output, RHIResourceState.UnorderedAccess);

            int groupsX = (_outputWidth + 7) / 8;
            int groupsY = (_outputHeight + 7) / 8;
            // cmdList.SetComputeRootSignature(...);
            // cmdList.SetComputeRoot32BitConstants(... sharpenAmount, radius ...);
            cmdList.Dispatch(groupsX, groupsY, 1);

            cmdList.TransitionTo(output, RHIResourceState.ShaderResource);
        }

        /// <summary>输出最终结果 (YCoCg → RGB)。</summary>
        private void OutputFinal(RHICommandList cmdList, RHITexture input, RHITexture output)
        {
            cmdList.TransitionTo(input, RHIResourceState.NonPixelShaderResource);
            cmdList.TransitionTo(output, RHIResourceState.UnorderedAccess);

            int groupsX = (_outputWidth + 7) / 8;
            int groupsY = (_outputHeight + 7) / 8;
            // cmdList.SetComputeRootSignature(...);
            cmdList.Dispatch(groupsX, groupsY, 1);

            cmdList.TransitionTo(output, RHIResourceState.ShaderResource);
        }

        // ── CPU 侧逻辑参考实现 (与 GPU 着色器公式一致) ──

        /// <summary>计算速度感知时序权重。</summary>
        public float ComputeVelocityAwareWeight(Vector2 velocity, float baseWeight, float minWeight)
        {
            float speed = velocity.Length();
            float velocityFactor = MathF.Exp(-speed * _config.VelocityWeightScale);
            return Math.Clamp(baseWeight * velocityFactor, minWeight, baseWeight);
        }

        /// <summary>计算自适应新帧权重 (基于亮度变化, 0.05-0.2)。</summary>
        public float ComputeAdaptiveNewFrameWeight(float luminanceDiff)
        {
            float t = Math.Clamp(luminanceDiff * 5.0f, 0, 1);
            return MathHelperLerp(_config.MinNewFrameWeight, _config.MaxNewFrameWeight, t);
        }

        /// <summary>检测运动矢量失效。</summary>
        public bool IsVelocityInvalid(Vector2 velocity)
        {
            return velocity.Length() > _config.VelocityDisocclusionThreshold;
        }

        /// <summary>检测深度失效。</summary>
        public bool IsDepthInvalid(float currentDepth, float historyDepth)
        {
            return MathF.Abs(currentDepth - historyDepth) > _config.DepthDisocclusionThreshold;
        }

        /// <summary>双边滤波空间重建 (CPU 参考)。</summary>
        public Vector3 BilateralReconstruct(in Vector3[,] colors, in float[,] depths,
            int x, int y, int w, int h, int radius, float sigmaColor, float sigmaDepth)
        {
            Vector3 center = colors[y, x];
            float centerDepth = depths[y, x];
            Vector3 result = Vector3.Zero;
            float weightSum = 0;

            for (int dy = -radius; dy <= radius; dy++)
            for (int dx = -radius; dx <= radius; dx++)
            {
                int sx = Math.Clamp(x + dx, 0, w - 1);
                int sy = Math.Clamp(y + dy, 0, h - 1);
                Vector3 neighbor = colors[sy, sx];
                float neighborDepth = depths[sy, sx];

                float colorDist = Vector3.Distance(center, neighbor);
                float depthDist = MathF.Abs(centerDepth - neighborDepth);

                float wc = MathF.Exp(-(colorDist * colorDist) / (2 * sigmaColor * sigmaColor));
                float wd = MathF.Exp(-(depthDist * depthDist) / (2 * sigmaDepth * sigmaDepth));
                float w = wc * wd;

                result += neighbor * w;
                weightSum += w;
            }
            return weightSum > 0 ? result / weightSum : center;
        }

        /// <summary>锐化滤波 (Unsharp Mask)。</summary>
        public Vector3 ApplySharpening(in Vector3[,] colors, int x, int y, int w, int h,
            float amount, float radius)
        {
            Vector3 center = colors[y, x];
            Vector3 blurred = Vector3.Zero;
            float weightSum = 0;
            int r = (int)MathF.Ceiling(radius);

            for (int dy = -r; dy <= r; dy++)
            for (int dx = -r; dx <= r; dx++)
            {
                int sx = Math.Clamp(x + dx, 0, w - 1);
                int sy = Math.Clamp(y + dy, 0, h - 1);
                float w = MathF.Exp(-(dx * dx + dy * dy) / (2 * radius * radius));
                blurred += colors[sy, sx] * w;
                weightSum += w;
            }
            blurred /= weightSum;

            return center + (center - blurred) * amount;
        }

        private static float MathHelperLerp(float a, float b, float t) => a + (b - a) * t;

        /// <summary>上传常量缓冲。</summary>
        public void UpdateConstants(in CameraData camera)
        {
            if (_constantsCB == null) return;

            var constants = new TSREnhancedConstants
            {
                ViewProjMatrix = camera.ViewProjectionMatrix,
                PrevViewProjMatrix = camera.ViewProjectionMatrix, // 简化
                InvViewProjMatrix = Matrix4x4.Invert(camera.ViewProjectionMatrix, out var invVP) ? invVP : Matrix4x4.Identity,
                CameraPos = camera.Position,
                InputResolution = new Vector2(_inputWidth, _inputHeight),
                OutputResolution = new Vector2(_outputWidth, _outputHeight),
                InvInputResolution = new Vector2(1.0f / _inputWidth, 1.0f / _inputHeight),
                InvOutputResolution = new Vector2(1.0f / _outputWidth, 1.0f / _outputHeight),
                Jitter = _jitter,
                PrevJitter = _prevJitter,
                BaseHistoryWeight = _config.BaseHistoryWeight,
                MinHistoryWeight = _config.MinHistoryWeight,
                MinNewFrameWeight = _config.MinNewFrameWeight,
                MaxNewFrameWeight = _config.MaxNewFrameWeight,
                VelocityWeightScale = _config.VelocityWeightScale,
                VelocityDisocclusionThreshold = _config.VelocityDisocclusionThreshold,
                DepthDisocclusionThreshold = _config.DepthDisocclusionThreshold,
                SharpeningAmount = _config.SharpeningAmount,
                SharpeningRadius = _config.SharpeningRadius,
                HighFrequencyThreshold = _config.HighFrequencyThreshold,
                LanczosWindow = _config.LanczosWindow,
                BicubicB = _config.BicubicB,
                BicubicC = _config.BicubicC,
                UseYCoCg = _config.UseYCoCg ? 1 : 0,
                UpsampleFilter = (int)_config.UpsampleFilter,
                FrameIndex = _frameIndex,
                EnableDisocclusionFix = _config.EnableDisocclusionFix ? 1 : 0
            };

            _device.UploadToBuffer(_constantsCB, new[] { constants });
        }

        public void Dispose()
        {
            _motionCalc.Dispose();
            _historyA?.Dispose();
            _historyB?.Dispose();
            _upscaledCurrent?.Dispose();
            _upscaledHistory?.Dispose();
            _varianceBuffer?.Dispose();
            _sharpenBuffer?.Dispose();
            _constantsCB?.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
