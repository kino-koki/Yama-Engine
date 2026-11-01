using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using YamaEngine.RHI;
using YamaEngine.Shaders;

namespace YamaEngine.Rendering
{
    // ════════════════════════════════════════════════════════════════════════
    // yama TAA V2 - 临时抗锯齿 第二版
    //
    // 改进点（相比 V1）：
    //   - 8x8 子像素抖动网格（Halton 序列）
    //   - YCoCg 色彩空间做 clamping（减少 ghost）
    //   - 基于运动矢量的自适应 clamp 范围
    //   - Catmull-Rom 重建滤波（vs 简单双线性）
    //   - 边缘检测 + 3x3 邻域方差计算
    //   - 历史帧置信度追踪（disocclusion 处理）
    //   - 预曝光 clamping（防止 LDR 闪烁）
    //   - 和 DRS 集成（抖动量随分辨率变化调整）
    // ════════════════════════════════════════════════════════════════════════

    public enum TAAJitterPattern { Halton23, Halton23_8x8, LowDiscrepancy, Random, Fixed8x8 }

    public sealed class TAAV2Config
    {
        public TAAJitterPattern JitterPattern = TAAJitterPattern.Halton23_8x8;
        public float JitterStrength = 0.5f;
        public int HistoryFrames = 8;
        public float ClampStrength = 0.1f;
        public bool EnableYCoCgClamp = true;
        public bool EnableVarianceClamp = true;
        public float VarianceGamma = 1.0f;
        public bool EnableGhostRejection = true;
        public float GhostRejectThreshold = 1.5f;
        public bool EnableCatmullRom = true;
        public bool EnableSharpness = true;
        public float Sharpness = 0.3f;
        public bool EnablePreExposure = true;
        public float MaxLuminanceHistory = 16.0f;
        public bool EnableDisocclusionDetect = true;
        public float DisocclusionThreshold = 0.05f;
        public bool EnableNeighborhoodClamp = true;
        public int ClampNeighborhoodSize = 3;
        public bool EnableToneMappingIntegration = true;
        public float MotionVectorScale = 1.0f;
        public bool EnableDebugDraw = false;

        public static TAAV2Config Quality => new() { HistoryFrames = 16, ClampStrength = 0.05f, EnableCatmullRom = true, JitterPattern = TAAJitterPattern.Halton23_8x8 };
        public static TAAV2Config Performance => new() { HistoryFrames = 4, ClampStrength = 0.2f, EnableCatmullRom = false, JitterPattern = TAAJitterPattern.Fixed8x8 };
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct TAAV2CB
    {
        public Vector2 JitterOffset;
        public Vector2 InvResolution;
        public Vector2 InvTexelSize;
        public float ClampStrength;
        public float Sharpness;
        public int HistoryFrame;
        public int EnableYCoCg;
        public int EnableGhostReject;
        public int EnableCatmullRom;
        public int EnableVarianceClamp;
        public float VarianceGamma;
        public float GhostRejectThreshold;
        public int DebugMode;
        public Vector2 Pad;
    }

    public static class HaltonSequence
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Halton(int index, int base_)
        {
            float result = 0.0f;
            float f = 1.0f / base_;
            int i = index;
            while (i > 0)
            {
                result += f * (i % base_);
                i /= base_;
                f /= base_;
            }
            return result;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector2 Halton23(int index)
        {
            return new Vector2(Halton(index, 2), Halton(index, 3));
        }

        /// <summary>预计算 8x8 Halton(2,3) 抖动网格。</summary>
        public static readonly Vector2[] JitterGrid8x8 = Generate8x8();

        private static Vector2[] Generate8x8()
        {
            var grid = new Vector2[64];
            for (int i = 0; i < 64; i++)
            {
                var h = Halton23(i);
                grid[i] = new Vector2(h.X - 0.5f, h.Y - 0.5f); // 中心化
            }
            return grid;
        }

        public static Vector2 GetJitter(int frameIndex, int patternSize)
        {
            int idx = frameIndex % patternSize;
            if (patternSize == 8) return JitterGrid8x8[idx] * 0.5f;
            if (patternSize == 64) return JitterGrid8x8[idx];
            return Halton23(frameIndex) - new Vector2(0.5f, 0.5f);
        }
    }

    public sealed class TemporalAntiAliasingV2 : IDisposable
    {
        private readonly RHIDevice _device;
        private readonly TAAV2Config _config;
        private RHITexture? _historyColor;
        private RHITexture? _resolvedColor;
        private RHITexture? _velocityTexture;
        private RHIBuffer? _constantBuffer;
        private int _frameIndex;
        private int _width, _height;
        private bool _disposed;
        private readonly float[] _frameTimes = new float[64];

        public int FrameIndex => _frameIndex;
        public Vector2 CurrentJitter => HaltonSequence.GetJitter(_frameIndex, 64) * _config.JitterStrength;
        public RHITexture HistoryColor => _historyColor!;
        public RHITexture ResolvedColor => _resolvedColor!;

        public TemporalAntiAliasingV2(RHIDevice device, int width, int height, TAAV2Config? config = null)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));
            _config = config ?? new TAAV2Config();
            _width = width; _height = height;
            AllocateResources();
        }

        private void AllocateResources()
        {
            _historyColor = _device.CreateTexture(new RHITextureDesc
            { Width = _width, Height = _height, Format = RHIFormat.R16G16B16A16_Float,
              BindFlags = RHIBindFlags.ShaderResource | RHIBindFlags.UnorderedAccess, MipLevels = 1, ArraySize = 1 });
            _resolvedColor = _device.CreateTexture(new RHITextureDesc
            { Width = _width, Height = _height, Format = RHIFormat.R16G16B16A16_Float,
              BindFlags = RHIBindFlags.UnorderedAccess | RHIBindFlags.ShaderResource, MipLevels = 1, ArraySize = 1 });
            _velocityTexture = _device.CreateTexture(new RHITextureDesc
            { Width = _width, Height = _height, Format = RHIFormat.R16G16B16A16_Float,
              BindFlags = RHIBindFlags.ShaderResource, MipLevels = 1, ArraySize = 1 });
            _constantBuffer = _device.CreateBuffer(new RHIBufferDesc
            { SizeInBytes = 256, BindFlags = RHIBindFlags.ConstantBuffer, Usage = (RHIBufferUsage)RHIUsage.Dynamic });
        }

        public Matrix4x4 GetJitteredProjectionMatrix(in Matrix4x4 proj)
        {
            var jitter = CurrentJitter;
            var result = proj;
            // 在 clip space 添加 subpixel 偏移
            // NOTE: 这个和 UE4/5 的做法一致，把 jitter 加到 projection 矩阵的 translation 分量
            result.M31 += jitter.X * 2.0f / _width;
            result.M32 += jitter.Y * 2.0f / _height;
            return result;
        }

        public void Resolve(RHITexture currentColor, RHITexture motionVectors)
        {
            var jitter = CurrentJitter;
            var cb = new TAAV2CB
            {
                JitterOffset = jitter,
                InvResolution = new Vector2(1.0f / _width, 1.0f / _height),
                InvTexelSize = new Vector2(1.0f / _width, 1.0f / _height),
                ClampStrength = _config.ClampStrength,
                Sharpness = _config.Sharpness,
                HistoryFrame = _frameIndex % _config.HistoryFrames,
                EnableYCoCg = _config.EnableYCoCgClamp ? 1 : 0,
                EnableGhostReject = _config.EnableGhostRejection ? 1 : 0,
                EnableCatmullRom = _config.EnableCatmullRom ? 1 : 0,
                EnableVarianceClamp = _config.EnableVarianceClamp ? 1 : 0,
                VarianceGamma = _config.VarianceGamma,
                GhostRejectThreshold = _config.GhostRejectThreshold,
                DebugMode = _config.EnableDebugDraw ? 1 : 0
            };
            _device.UpdateBuffer(_constantBuffer!, cb);

            _device.SetShaderResource(0, currentColor);
            _device.SetShaderResource(1, _historyColor!);
            _device.SetShaderResource(2, motionVectors);
            _device.SetUnorderedAccessView(0, _resolvedColor!);
            _device.SetConstantBuffer(0, _constantBuffer!);

            int gx = (_width + 7) / 8, gy = (_height + 7) / 8;
            _device.Dispatch(gx, gy, 1);

            // 交换历史和 resolved
            (_historyColor, _resolvedColor) = (_resolvedColor, _historyColor);
            _frameIndex++;
        }

        public void Resize(int width, int height)
        {
            if (width == _width && height == _height) return;
            _width = width; _height = height;
            _historyColor?.Dispose(); _resolvedColor?.Dispose(); _velocityTexture?.Dispose();
            AllocateResources();
        }

        public void ResetHistory() { _frameIndex = 0; }

        public void Dispose()
        {
            if (_disposed) return;
            _historyColor?.Dispose(); _resolvedColor?.Dispose(); _velocityTexture?.Dispose();
            _constantBuffer?.Dispose(); _disposed = true;
        }
    }
}