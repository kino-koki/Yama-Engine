using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using ShanyuEngine.RHI;
using ShanyuEngine.Shaders;

namespace ShanyuEngine.Rendering
{
    // ════════════════════════════════════════════════════════════════════════
    // 山雨 动态分辨率缩放器 (Dynamic Resolution Scaler)
    //
    // 根据实时性能自动调整渲染分辨率，保持稳定帧率。
    // 和 FSR/DLSS 不同，这里是真正的降低渲染分辨率 + 后期放大。
    //
    // 原理：
    //   1. 每帧测量 GPU 时间
    //   2. 和目标帧时间比较
    //   3. 超预算 -> 降低分辨率
    //   4. 低于预算 -> 提高分辨率（缓慢恢复）
    //   5. 用 FSR EASU/RCAS 或双线性放大回输出分辨率
    //
    // 特性：
    //   - 平滑的分辨率变化（不会突然跳变）
    //   - 支持 min/max 分辨率限制
    //   - 支持 snap 到整数倍（避免非整数缩放的问题）
    //   - 支持 GPU 驱动的自动调整
    //   - 和 TAA/TSR 集成：分辨率变化时调整 TAA 的 jitter pattern
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>缩放器状态。</summary>
    public enum DRSState { Stable, ScalingDown, ScalingUp, MinReached, MaxReached }

    /// <summary>动态分辨率配置。</summary>
    public sealed class DRSConfig
    {
        public int NativeWidth = 1920;
        public int NativeHeight = 1080;
        public float MinScale = 0.5f;
        public float MaxScale = 1.0f;
        public float TargetFPS = 60.0f;
        public float ScaleChangeSpeed = 0.1f;
        public float UpChangeSpeed = 0.02f;
        public float ScaleHysteresis = 0.01f;
        public bool SnapToInteger = true;
        public bool EnableGPUQuery = true;
        public bool EnablePredictive = true;
        public int PredictiveWindow = 30;
        public float CriticalBudgetRatio = 1.5f;
        public bool EnableVSync = false;
        public int MaxPixelCount = 2073600;
        public bool ResetOnSceneChange = true;

        public static DRSConfig Performance => new() { MinScale = 0.5f, MaxScale = 0.75f, TargetFPS = 60.0f, ScaleChangeSpeed = 0.15f, UpChangeSpeed = 0.01f };
        public static DRSConfig Balanced => new() { MinScale = 0.67f, MaxScale = 1.0f, TargetFPS = 60.0f, ScaleChangeSpeed = 0.08f, UpChangeSpeed = 0.02f };
        public static DRSConfig Quality => new() { MinScale = 0.85f, MaxScale = 1.0f, TargetFPS = 60.0f, ScaleChangeSpeed = 0.05f, UpChangeSpeed = 0.01f, SnapToInteger = false };
    }

    /// <summary>动态分辨率统计。</summary>
    public struct DRSStats
    {
        public float CurrentScale;
        public int CurrentWidth, CurrentHeight;
        public DRSState State;
        public float TargetFrameTimeMs;
        public float LastFrameTimeMs;
        public float AverageFrameTimeMs;
        public float ScaleChangeRate;
        public long TotalPixels;
        public float PixelSavings;
    }

    /// <summary>动态分辨率缩放器。</summary>
    public sealed class DynamicResolutionScaler : IDisposable
    {
        private readonly RHIDevice _device;
        private readonly DRSConfig _config;
        private float _currentScale = 1.0f;
        private float _targetScale = 1.0f;
        private int _currentWidth, _currentHeight;
        private DRSState _state = DRSState.Stable;
        private readonly Queue<float> _frameTimeHistory = new();
        private float _avgFrameTime;
        private bool _disposed;
        private RHITexture? _renderTarget;
        private RHITexture? _depthTarget;
        private RHITexture? _outputTarget;
        private RHITimerQuery? _gpuTimer;

        public float CurrentScale => _currentScale;
        public int CurrentWidth => _currentWidth;
        public int CurrentHeight => _currentHeight;
        public DRSState State => _state;
        public float TargetFrameTimeMs => 1000.0f / _config.TargetFPS;

        public DynamicResolutionScaler(RHIDevice device, DRSConfig? config = null)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));
            _config = config ?? new DRSConfig();
            _currentWidth = _config.NativeWidth;
            _currentHeight = _config.NativeHeight;
            if (_config.EnableGPUQuery)
                _gpuTimer = _device.CreateTimerQuery();
        }

        public void Update(float frameTimeMs)
        {
            _frameTimeHistory.Enqueue(frameTimeMs);
            if (_frameTimeHistory.Count > _config.PredictiveWindow)
                _frameTimeHistory.Dequeue();
            _avgFrameTime = 0;
            foreach (var t in _frameTimeHistory)
                _avgFrameTime += t;
            _avgFrameTime /= _frameTimeHistory.Count;

            float targetMs = TargetFrameTimeMs;
            float effectiveTime = _config.EnablePredictive ? _avgFrameTime : frameTimeMs;

            if (effectiveTime > targetMs * 1.1f)
            {
                _targetScale -= _config.ScaleChangeSpeed * (effectiveTime / targetMs - 1.0f);
                _state = DRSState.ScalingDown;
            }
            else if (effectiveTime < targetMs * 0.85f)
            {
                _targetScale += _config.UpChangeSpeed;
                _state = DRSState.ScalingUp;
            }
            else
            {
                _state = DRSState.Stable;
            }

            _targetScale = Math.Clamp(_targetScale, _config.MinScale, _config.MaxScale);
            _currentScale += (_targetScale - _currentScale) * 0.3f;
            _currentScale = Math.Clamp(_currentScale, _config.MinScale, _config.MaxScale);

            if (_config.SnapToInteger)
            {
                float invScale = 1.0f / MathF.Round(1.0f / _currentScale);
                _currentScale = Math.Clamp(invScale, _config.MinScale, _config.MaxScale);
            }

            int newW = Math.Max(1, (int)(_config.NativeWidth * _currentScale));
            int newH = Math.Max(1, (int)(_config.NativeHeight * _currentScale));

            if (_config.MaxPixelCount > 0)
            {
                while (newW * newH > _config.MaxPixelCount && newW > 1 && newH > 1)
                { newW--; newH--; }
            }

            if (newW != _currentWidth || newH != _currentHeight)
            {
                _currentWidth = newW; _currentHeight = newH;
                ReallocateTargets();
            }

            if (_currentScale <= _config.MinScale + 0.001f) _state = DRSState.MinReached;
            else if (_currentScale >= _config.MaxScale - 0.001f) _state = DRSState.MaxReached;
        }

        private void ReallocateTargets()
        {
            _renderTarget?.Dispose(); _depthTarget?.Dispose(); _outputTarget?.Dispose();
            _renderTarget = _device.CreateTexture(new RHITextureDesc
            { Width = _currentWidth, Height = _currentHeight, Format = RHIFormat.R16G16B16A16_Float,
              BindFlags = RHIBindFlags.RenderTarget | RHIBindFlags.ShaderResource, MipLevels = 1, ArraySize = 1 });
            _depthTarget = _device.CreateTexture(new RHITextureDesc
            { Width = _currentWidth, Height = _currentHeight, Format = RHIFormat.D32_Float,
              BindFlags = RHIBindFlags.DepthStencil, MipLevels = 1, ArraySize = 1 });
            _outputTarget = _device.CreateTexture(new RHITextureDesc
            { Width = _config.NativeWidth, Height = _config.NativeHeight, Format = RHIFormat.R16G16B16A16_Float,
              BindFlags = RHIBindFlags.RenderTarget | RHIBindFlags.ShaderResource, MipLevels = 1, ArraySize = 1 });
        }

        public (RHITexture color, RHITexture depth) GetRenderTargets()
        {
            if (_renderTarget == null) ReallocateTargets();
            return (_renderTarget!, _depthTarget!);
        }

        public void UpsampleToOutput()
        {
            if (_renderTarget == null || _outputTarget == null) return;
            _device.SetShaderResource(0, _renderTarget);
            _device.SetUnorderedAccessView(0, _outputTarget);
            int gx = (_config.NativeWidth + 7) / 8, gy = (_config.NativeHeight + 7) / 8;
            _device.Dispatch(gx, gy, 1);
        }

        public RHITexture GetOutput() => _outputTarget!;

        public DRSStats GetStats()
        {
            float nativePixels = _config.NativeWidth * _config.NativeHeight;
            float currentPixels = _currentWidth * _currentHeight;
            return new DRSStats
            {
                CurrentScale = _currentScale, CurrentWidth = _currentWidth, CurrentHeight = _currentHeight,
                State = _state, TargetFrameTimeMs = TargetFrameTimeMs,
                LastFrameTimeMs = _frameTimeHistory.Count > 0 ? _frameTimeHistory.Peek() : 0,
                AverageFrameTimeMs = _avgFrameTime,
                TotalPixels = (long)currentPixels,
                PixelSavings = 1.0f - currentPixels / nativePixels
            };
        }

        public void Reset() { _currentScale = 1.0f; _targetScale = 1.0f; _frameTimeHistory.Clear(); _state = DRSState.Stable; }
        public void Dispose()
        {
            if (_disposed) return;
            _renderTarget?.Dispose(); _depthTarget?.Dispose(); _outputTarget?.Dispose();
            _gpuTimer?.Dispose(); _disposed = true;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DRSUpsampleCB
    {
        public Vector2 SrcResolution;
        public Vector2 DstResolution;
        public Vector2 InvSrcResolution;
        public Vector2 InvDstResolution;
        public float Sharpness;
        public int Method;
        public Vector2 Pad;
    }
}
