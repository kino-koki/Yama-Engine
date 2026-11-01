using System;
using System.Numerics;
using System.Runtime.InteropServices;
using YamaEngine.RHI;
using YamaEngine.Shaders;

namespace YamaEngine.Rendering
{
    // ════════════════════════════════════════════════════════════════════════
    // yama 运动模糊系统 (Motion Blur System)
    //
    // 支持两种运动模糊：
    //   1. Camera Motion Blur (CMB)：相机快速移动时的全局运动模糊
    //   2. Per-Object Motion Blur (POMB)：物体快速移动/旋转时的独立运动模糊
    //
    // 算法：
    //   - 基于运动矢量的速度缓冲
    //   - tile-based 最大速度计算（减少计算量）
    //   - 多 pass gather 模糊（沿运动方向采样）
    //   - 考虑曝光时间的运动强度
    // ════════════════════════════════════════════════════════════════════════

    public enum MotionBlurQuality { Off, Low, Medium, High, Ultra }

    public sealed class MotionBlurConfig
    {
        public MotionBlurQuality Quality = MotionBlurQuality.Medium;
        public float Intensity = 0.5f;
        public float ShutterAngle = 180.0f; // 度，180 = 半帧曝光，360 = 全帧曝光
        public int MaxSampleCount = 16;
        public float MinVelocityThreshold = 0.01f;
        public float MaxVelocityClamp = 50.0f;
        public bool EnableCameraMotionBlur = true;
        public bool EnableObjectMotionBlur = true;
        public bool EnableVelocityPrepass = true;
        public int TileSize = 16;
        public bool UseGatherMethod = true;
        public float DepthWeight = 0.5f; // 深度越近模糊越少
        public bool EnableCenterWeight = true;
        public bool EnableForegroundBlur = true;
        public bool EnableBackgroundBlur = true;
        public bool EnableDebugDraw = false;

        public int GetEffectiveSampleCount() => Quality switch
        {
            MotionBlurQuality.Low => 4, MotionBlurQuality.Medium => 8,
            MotionBlurQuality.High => 12, MotionBlurQuality.Ultra => 16, _ => 0
        };

        public static MotionBlurConfig Performance => new() { Quality = MotionBlurQuality.Low, Intensity = 0.3f, MaxSampleCount = 4, TileSize = 32 };
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MotionBlurCB
    {
        public Vector2 InvResolution;
        public float Intensity;
        public float ShutterAngleNorm; // [0, 1]
        public int SampleCount;
        public float MinVelocity;
        public float MaxVelocity;
        public int TileSize;
        public int EnableCamera;
        public int EnableObject;
        public float DepthWeight;
        public int EnableDebug;
        public Vector3 Pad;
    }

    /// <summary>相机运动数据。</summary>
    public struct CameraMotionData
    {
        public Vector3 PreviousPosition;
        public Vector3 CurrentPosition;
        public Quaternion PreviousRotation;
        public Quaternion CurrentRotation;
        public float FoV;
        public float AspectRatio;
        public float NearPlane;
        public float ExposureTime; // 归一化到帧时间

        public Vector3 AngularVelocity { get; set; }
        public Vector3 LinearVelocity { get; set; }

        public void CalculateVelocities(float deltaTime)
        {
            if (deltaTime < 1e-6f) { LinearVelocity = Vector3.Zero; AngularVelocity = Vector3.Zero; return; }
            LinearVelocity = (CurrentPosition - PreviousPosition) / deltaTime;
            // 角速度从四元数差值估算
            float dot = Math.Clamp(Quaternion.Dot(PreviousRotation, CurrentRotation), -1.0f, 1.0f);
            float angle = 2.0f * MathF.Acos(MathF.Abs(dot)) / deltaTime;
            var diff = Quaternion.Concatenate(Quaternion.Inverse(PreviousRotation), CurrentRotation);
            AngularVelocity = new Vector3(diff.X, diff.Y, diff.Z) * angle * MathF.Sign(dot);
        }

        /// <summary>估算屏幕空间的最大运动速度（像素/帧）。</summary>
        public float EstimateMaxScreenVelocity(int screenWidth, float fov)
        {
            float linearMag = LinearVelocity.Length();
            float angularMag = AngularVelocity.Length();
            float linearPixels = linearMag * screenWidth / (2.0f * MathF.Tan(fov * 0.5f * MathF.PI / 180.0f));
            float angularPixels = angularMag * screenWidth * 0.5f * MathF.PI / 180.0f;
            return linearPixels + angularPixels;
        }
    }

    /// <summary>Tile 级别的运动模糊数据（用于 gather 模式的快速路径）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MotionBlurTile
    {
        public float MaxVelocityX;
        public float MaxVelocityY;
        public float AvgDepth;
        public float MaxVelocityLength;
    }

    public sealed class MotionBlurSystem : IDisposable
    {
        private readonly RHIDevice _device;
        private readonly MotionBlurConfig _config;
        private RHITexture? _velocityBuffer;
        private RHITexture? _tileMaxVelocity;
        private RHITexture? _blurredOutput;
        private RHIBuffer? _cb;
        private RHITexture? _tempTarget;
        private int _width, _height, _tileCountX, _tileCountY;
        private CameraMotionData _cameraMotion;
        private bool _disposed;

        public MotionBlurConfig Config => _config;
        public CameraMotionData CameraMotion => _cameraMotion;

        public MotionBlurSystem(RHIDevice device, int width, int height, MotionBlurConfig? config = null)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));
            _config = config ?? new MotionBlurConfig();
            _width = width; _height = height;
            _tileCountX = (width + _config.TileSize - 1) / _config.TileSize;
            _tileCountY = (height + _config.TileSize - 1) / _config.TileSize;
            AllocateResources();
        }

        private void AllocateResources()
        {
            _velocityBuffer = _device.CreateTexture(new RHITextureDesc
            { Width = _width, Height = _height, Format = RHIFormat.R16G16B16A16_Float,
              BindFlags = RHIBindFlags.ShaderResource | RHIBindFlags.UnorderedAccess, MipLevels = 1, ArraySize = 1 });
            _tileMaxVelocity = _device.CreateTexture(new RHITextureDesc
            { Width = _tileCountX, Height = _tileCountY, Format = RHIFormat.R16G16B16A16_Float,
              BindFlags = RHIBindFlags.ShaderResource | RHIBindFlags.UnorderedAccess, MipLevels = 1, ArraySize = 1 });
            _blurredOutput = _device.CreateTexture(new RHITextureDesc
            { Width = _width, Height = _height, Format = RHIFormat.R16G16B16A16_Float,
              BindFlags = RHIBindFlags.UnorderedAccess | RHIBindFlags.ShaderResource, MipLevels = 1, ArraySize = 1 });
            _tempTarget = _device.CreateTexture(new RHITextureDesc
            { Width = _width, Height = _height, Format = RHIFormat.R16G16B16A16_Float,
              BindFlags = RHIBindFlags.UnorderedAccess | RHIBindFlags.ShaderResource, MipLevels = 1, ArraySize = 1 });
            _cb = _device.CreateBuffer(new RHIBufferDesc
            { SizeInBytes = 256, BindFlags = RHIBindFlags.ConstantBuffer, Usage = (RHIBufferUsage)RHIUsage.Dynamic });
        }

        /// <summary>设置相机运动数据（从相机的 prev/current 矩阵推导）。</summary>
        public void SetCameraMotion(in CameraMotionData motion) { _cameraMotion = motion; }

        /// <summary>执行运动模糊。</summary>
        public void Execute(RHITexture inputColor, RHITexture motionVectors, RHITexture depthBuffer)
        {
            int samples = Math.Min(_config.GetEffectiveSampleCount(), _config.MaxSampleCount);
            float shutterNorm = _config.ShutterAngle / 360.0f;

            var cb = new MotionBlurCB
            {
                InvResolution = new Vector2(1.0f / _width, 1.0f / _height),
                Intensity = _config.Intensity,
                ShutterAngleNorm = shutterNorm,
                SampleCount = samples,
                MinVelocity = _config.MinVelocityThreshold,
                MaxVelocity = _config.MaxVelocityClamp,
                TileSize = _config.TileSize,
                EnableCamera = _config.EnableCameraMotionBlur ? 1 : 0,
                EnableObject = _config.EnableObjectMotionBlur ? 1 : 0,
                DepthWeight = _config.DepthWeight,
                EnableDebug = _config.EnableDebugDraw ? 1 : 0
            };
            _device.UpdateBuffer(_cb!, cb);

            _device.SetShaderResource(0, motionVectors);
            _device.SetShaderResource(1, depthBuffer);
            _device.SetUnorderedAccessView(0, _tileMaxVelocity!);
            _device.SetConstantBuffer(0, _cb!);
            _device.Dispatch(_tileCountX, _tileCountY, 1);

            _device.SetShaderResource(0, inputColor);
            _device.SetShaderResource(1, motionVectors);
            _device.SetShaderResource(2, _tileMaxVelocity!);
            _device.SetShaderResource(3, depthBuffer);
            _device.SetUnorderedAccessView(0, _blurredOutput!);
            int gx = (_width + 7) / 8, gy = (_height + 7) / 8;
            _device.Dispatch(gx, gy, 1);
        }

        public RHITexture GetOutput() => _blurredOutput!;

        public void Resize(int width, int height)
        {
            if (width == _width && height == _height) return;
            _width = width; _height = height;
            _tileCountX = (width + _config.TileSize - 1) / _config.TileSize;
            _tileCountY = (height + _config.TileSize - 1) / _config.TileSize;
            _velocityBuffer?.Dispose(); _tileMaxVelocity?.Dispose();
            _blurredOutput?.Dispose(); _tempTarget?.Dispose();
            AllocateResources();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _velocityBuffer?.Dispose(); _tileMaxVelocity?.Dispose();
            _blurredOutput?.Dispose(); _tempTarget?.Dispose(); _cb?.Dispose();
            _disposed = true;
        }
    }
}