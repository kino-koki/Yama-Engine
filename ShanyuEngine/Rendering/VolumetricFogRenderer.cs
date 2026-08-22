using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ShanyuEngine.RHI;
using ShanyuEngine.Shaders;

namespace ShanyuEngine.Rendering
{
    // ════════════════════════════════════════════════════════════════════════
    // 山雨 体积雾渲染器 (Volumetric Fog Renderer)
    //
    // 实现基于 Ray Marching 的体积雾，支持：
    //   1. 3D 体积纹理表示雾密度
    //   2. Froxel（Frustum Voxel）方法：把视锥体划分为 3D 网格
    //   3. Epipolar 采样：沿 epipolar 线稀疏采样 + 双线性插值
    //   4. 多光源散射（Mie 散射相位函数）
    //   5. 阴影投射到雾中（Shadow Map 采样）
    //   6. 噪声纹理驱动密度变化（Perlin/Worley）
    //   7. 高度雾（指数衰减）
    //   8. 局部雾体积（球体/盒子定义区域）
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>体积雾模式。</summary>
    public enum VolumetricFogMode
    {
        /// <summary>Froxel Ray Marching（最通用）。</summary>
        FroxelRayMarch,
        /// <summary>Epipolar 采样（更快但质量稍低）。</summary>
        EpipolarSampling,
        /// <summary>简单高度雾（最快，没有体积感）。</summary>
        HeightFog
    }

    /// <summary>雾散射模型。</summary>
    public enum FogScatteringModel
    {
        /// <summary>Henyey-Greenstein（可调非对称性）。</summary>
        HenyeyGreenstein,
        /// <summary>Rayleigh（空气分子，对称）。</summary>
        Rayleigh,
        /// <summary>Schlick 近似 HG（更快）。</summary>
        Schlick,
        /// <summary>Cornette-Shank（更精确的 Mie 近似）。</summary>
        CornetteShank
    }

    // ────────────────────────────────────────────────────────────────────────
    // 配置
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>体积雾配置。</summary>
    public sealed class VolumetricFogConfig
    {
        public VolumetricFogMode Mode = VolumetricFogMode.FroxelRayMarch;
        public FogScatteringModel Scattering = FogScatteringModel.HenyeyGreenstein;

        // 基础雾参数
        public float FogDensity = 0.02f;
        public Vector3 FogAlbedo = new(0.9f, 0.92f, 0.95f);
        public float FogHeightFalloff = 0.05f;
        public float FogHeightOffset = 0.0f;
        public float FogStartDistance = 0.0f;
        public float FogEndDistance = 500.0f;
        public float MaxOpacity = 0.98f;

        // Ray Marching 参数
        public int RayMarchSteps = 64;
        public float MaxTraceDistance = 300.0f;

        // Froxel 网格
        public int FroxelWidth = 160;
        public int FroxelHeight = 90;
        public int FroxelDepth = 64;

        // Epipolar 参数
        public int EpipolarSamplesPerRay = 32;
        public int EpipolarRayCount = 64;

        // 散射
        public float ScatteringG = 0.3f; // 非对称因子 [-1, 1]
        public float ScatteringAnisotropy = 0.0f;
        public float AmbientLightContrib = 0.1f;

        // 噪声
        public bool EnableNoise = true;
        public float NoiseScale = 5.0f;
        public float NoiseIntensity = 0.5f;
        public float NoiseSpeed = 0.1f;
        public int NoiseOctaves = 4;

        // 阴影
        public bool EnableShadowMap = true;
        public float ShadowMapJitter = 1.5f;

        // 光源
        public int MaxVolumetricLights = 4;
        public float LightShaftIntensity = 1.0f;

        // Temporal
        public bool EnableTemporal = true;
        public float TemporalAlpha = 0.9f;

        // Debug
        public bool EnableDebugDraw = false;

        /// <summary>计算 3D 网格总 voxel 数。</summary>
        public int TotalFroxelCount => FroxelWidth * FroxelHeight * FroxelDepth;

        /// <summary>估算显存占用。</summary>
        public ulong EstimateMemoryBytes() =>
            (ulong)TotalFroxelCount * 16 * 2 + // 密度 + 光照各 RGBA16F
            (ulong)FroxelWidth * FroxelHeight * 16; // 积分结果

        public static VolumetricFogConfig Default => new();

        public static VolumetricFogConfig Performance => new()
        {
            Mode = VolumetricFogMode.HeightFog, RayMarchSteps = 32, EnableNoise = false,
            EnableShadowMap = false, EnableTemporal = false
        };

        public static VolumetricFogConfig HighQuality => new()
        {
            RayMarchSteps = 96, FroxelDepth = 96, EnableNoise = true,
            NoiseOctaves = 5, EnableShadowMap = true, EnableTemporal = true
        };
    }

    // ────────────────────────────────────────────────────────────────────────
    // 局部雾体积
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>雾体积形状。</summary>

    /// <summary>局部雾体积。</summary>
    public class FogVolume
    {
        public FogVolumeShape Shape = FogVolumeShape.Sphere;
        public Vector3 Position;
        public Vector3 Extents = new(10, 10, 10);
        public float Density = 0.1f;
        public Vector3 Color = Vector3.One;
        public float HeightFalloff = 0.1f;
        public bool Enabled = true;

        /// <summary>计算点是否在体积内以及密度。</summary>
        public float SampleDensity(Vector3 worldPos)
        {
            if (!Enabled) return 0;

            float dist;
            switch (Shape)
            {
                case FogVolumeShape.Sphere:
                    dist = Vector3.Distance(worldPos, Position) / MathF.Max(Extents.X, 0.01f);
                    break;
                case FogVolumeShape.Box:
                    var delta = (worldPos - Position) / MathF.Max(Extents, new Vector3(0.01f));
                    dist = Math.Max(Math.Abs(delta.X), Math.Max(Math.Abs(delta.Y), Math.Abs(delta.Z)));
                    break;
                case FogVolumeShape.Cylinder:
                    float hDist = new Vector2(worldPos.X - Position.X, worldPos.Z - Position.Z).Length() / MathF.Max(Extents.X, 0.01f);
                    float vDist = Math.Abs(worldPos.Y - Position.Y) / MathF.Max(Extents.Y, 0.01f);
                    dist = Math.Max(hDist, vDist);
                    break;
                default:
                    dist = 999;
                    break;
            }

            if (dist >= 1.0f) return 0;
            float fade = 1.0f - dist * dist; // 二次衰减
            return Density * fade * MathF.Exp(-MathF.Max(worldPos.Y - Position.Y, 0) * HeightFalloff);
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // 散射函数
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>体积散射相位函数。</summary>
    public static class ScatteringFunctions
    {
        /// <summary>Henyey-Greenstein 相位函数。</summary>
        /// <param name="cosTheta">光线方向和视线方向的点积。</param>
        /// <param name="g">非对称因子 (-1=完全后向, 0=各向同性, 1=完全前向)。</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float HenyeyGreenstein(float cosTheta, float g)
        {
            float g2 = g * g;
            float denom = 1.0f + g2 - 2.0f * g * cosTheta;
 //这块先这样，回头再优化：分母为0的时候会出问题
            if (MathF.Abs(denom) < 1e-6f) denom = 1e-6f;
            return (1.0f - g2) / (4.0f * MathF.PI * denom * MathF.Sqrt(MathF.Max(denom, 1e-6f)));
        }

        /// <summary>多 lobes HG（双峰散射）。</summary>
        public static float DualLobeHG(float cosTheta, float gForward, float gBack, float weight)
        {
            return weight * HenyeyGreenstein(cosTheta, gForward) +
                   (1.0f - weight) * HenyeyGreenstein(cosTheta, gBack);
        }

        /// <summary>Schlick 近似（更快）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Schlick(float cosTheta, float g)
        {
            float k = (1.0f + g) * (1.0f + g) / 8.0f;
            float denom = 1.0f + k - 2.0f * k * cosTheta;
            if (MathF.Abs(denom) < 1e-6f) denom = 1e-6f;
            return (1.0f + k * k) / (4.0f * MathF.PI * denom * denom);
        }

        /// <summary>Cornette-Shank（更精确的 Mie 近似）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float CornetteShank(float cosTheta, float g)
        {
            float g2 = g * g;
            float denom = 1.0f + g2 - 2.0f * g * cosTheta;
            if (MathF.Abs(denom) < 1e-6f) denom = 1e-6f;
            float p = (3.0f * (1.0f - g2)) / (2.0f * (2.0f + g2));
            return p * (1.0f + cosTheta * cosTheta) / MathF.Pow(denom, 1.5f);
        }

        /// <summary>Rayleigh 相位函数。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Rayleigh(float cosTheta)
        {
            return (3.0f / 16.0f / MathF.PI) * (1.0f + cosTheta * cosTheta);
        }

        /// <summary>统一调用。</summary>
        public static float Evaluate(FogScatteringModel model, float cosTheta, float g)
        {
            return model switch
            {
                FogScatteringModel.HenyeyGreenstein => HenyeyGreenstein(cosTheta, g),
                FogScatteringModel.Schlick => Schlick(cosTheta, g),
                FogScatteringModel.CornetteShank => CornetteShank(cosTheta, g),
                FogScatteringModel.Rayleigh => Rayleigh(cosTheta),
                _ => HenyeyGreenstein(cosTheta, g)
            };
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // 常量缓冲
    // ────────────────────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    internal struct VolumetricFogCB
    {
        public Matrix4x4 InvViewProj;
        public Matrix4x4 View;
        public Matrix4x4 Proj;
        public Vector3 CameraPos;
        public float NearPlane;
        public Vector3 SunDirection;
        public float SunIntensity;
        public Vector3 SunColor;
        public float Pad0;
        public Vector2 InvResolution;
        public float FogDensity;
        public float FogHeightFalloff;
        public float FogHeightOffset;
        public float ScatteringG;
        public float MaxOpacity;
        public int RayMarchSteps;
        public int Mode;
        public int FroxelDepth;
        public float NoiseScale;
        public float NoiseIntensity;
        public float NoiseSpeed;
        public int NoiseOctaves;
        public float AmbientLight;
        public int MaxLights;
        public float LightShaftIntensity;
        public int FrameIndex;
        public float TemporalAlpha;
        public int EnableTemporal;
        // Per-light 数据（最多 4 盏）
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
        public Vector4 LightPosRadius0, LightPosRadius1, LightPosRadius2, LightPosRadius3;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
        public Vector4 LightColorIntensity0, LightColorIntensity1, LightColorIntensity2, LightColorIntensity3;
    }

    // ────────────────────────────────────────────────────────────────────────
    // Froxel 网格
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>Froxel（视锥体体素）网格管理。</summary>
    public sealed class FroxelGrid : IDisposable
    {
        private readonly RHIDevice _device;
        private int _width, _height, _depth;
        private bool _disposed;

        public RHITexture? DensityVolume;
        public RHITexture? LightVolume;
        public RHITexture? ScatteringVolume;

        public int Width => _width;
        public int Height => _height;
        public int Depth => _depth;
        public int TotalCells => _width * _height * _depth;

        public FroxelGrid(RHIDevice device, int width, int height, int depth)
        {
            _device = device;
            _width = width; _height = height; _depth = depth;
            CreateResources();
        }

        private void CreateResources()
        {
            DensityVolume = _device.CreateTexture(new RHITextureDesc
            {
                Width = _width, Height = _height, Depth = _depth,
                Format = RHIFormat.R16_Float,
                Usage = RHITextureUsage.UnorderedAccess | RHITextureUsage.ShaderResource,
                DebugName = "Fog_DensityVol"
            });

            LightVolume = _device.CreateTexture(new RHITextureDesc
            {
                Width = _width, Height = _height, Depth = _depth,
                Format = RHIFormat.R16G16B16A16_Float,
                Usage = RHITextureUsage.UnorderedAccess | RHITextureUsage.ShaderResource,
                DebugName = "Fog_LightVol"
            });

            ScatteringVolume = _device.CreateTexture(new RHITextureDesc
            {
                Width = _width, Height = _height, Depth = _depth,
                Format = RHIFormat.R11G11B10_Float,
                Usage = RHITextureUsage.UnorderedAccess | RHITextureUsage.ShaderResource,
                DebugName = "Fog_ScatterVol"
            });
        }

        /// <summary>清除体积数据。</summary>
        public void Clear()
        {
            if (DensityVolume != null) _device.ClearUAV(DensityVolume, Vector4.Zero);
            if (LightVolume != null) _device.ClearUAV(LightVolume, Vector4.Zero);
            if (ScatteringVolume != null) _device.ClearUAV(ScatteringVolume, Vector4.Zero);
        }

        /// <summary>计算 froxel 到世界空间的映射参数。</summary>
        public FroxelToWorldParams ComputeWorldParams(
            Matrix4x4 invViewProj, Vector3 cameraPos, float nearPlane, float farPlane)
        {
            // Froxel 在深度方向用对数分布（近处密，远处疏）
            float near = nearPlane;
            float far = farPlane;
            float logNear = MathF.Log(MathF.Max(near, 0.01f));
            float logFar = MathF.Log(MathF.Max(far, 0.01f));
            float logRange = logFar - logNear;

            return new FroxelToWorldParams
            {
                InvViewProj = invViewProj,
                CameraPos = cameraPos,
                Near = near,
                Far = far,
                LogNear = logNear,
                LogRange = logRange,
                Width = _width,
                Height = _height,
                Depth = _depth
            };
        }

        public void Dispose()
        {
            if (_disposed) return;
            DensityVolume?.Dispose(); LightVolume?.Dispose(); ScatteringVolume?.Dispose();
            _disposed = true; GC.SuppressFinalize(this);
        }
    }

    /// <summary>Froxel 到世界空间的映射参数。</summary>
    public struct FroxelToWorldParams
    {
        public Matrix4x4 InvViewProj;
        public Vector3 CameraPos;
        public float Near, Far, LogNear, LogRange;
        public int Width, Height, Depth;

        /// <summary>获取 froxel 中心的世界空间深度（对数分布）。</summary>
        public float GetDepth(int zSlice)
        {
            float t = (zSlice + 0.5f) / Depth;
            return MathF.Exp(LogNear + t * LogRange);
        }

        /// <summary>获取 froxel 中心的世界空间位置。</summary>
        public Vector3 GetWorldPos(int x, int y, int z)
        {
            float depth = GetDepth(z);
            // NDC
            float nx = (x + 0.5f) / Width * 2.0f - 1.0f;
            float ny = 1.0f - (y + 0.5f) / Height * 2.0f;
            float nz = depth * 2.0f - 1.0f;

            // Unproject
            var clipPos = new Vector4(nx, ny, nz, 1.0f);
            var worldPos = Vector4.Transform(clipPos, InvViewProj);
            return new Vector3(worldPos.X / worldPos.W, worldPos.Y / worldPos.W, worldPos.Z / worldPos.W);
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // 主系统
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>体积雾渲染器。</summary>

    // ────────────────────────────────────────────────────────────────────────
    // Epipolar 采样（优化路径）
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>Epipolar 体积雾采样器。</summary>
    public sealed class EpipolarFogSampler
    {
        private readonly RHIDevice _device;
        private RHITexture? _epipolarImage;
        private RHITexture? _epipolarDepth;
        private int _rayCount, _samplesPerRay;

        public EpipolarFogSampler(RHIDevice device, int rayCount, int samplesPerRay)
        {
            _device = device;
            _rayCount = rayCount;
            _samplesPerRay = samplesPerRay;

            _epipolarImage = _device.CreateTexture(new RHITextureDesc
            {
                Width = _samplesPerRay, Height = _rayCount,
                Format = RHIFormat.R16G16B16A16_Float,
                Usage = RHITextureUsage.UnorderedAccess | RHITextureUsage.ShaderResource,
                DebugName = "Epipolar_FogImage"
            });

            _epipolarDepth = _device.CreateTexture(new RHITextureDesc
            {
                Width = _samplesPerRay, Height = _rayCount,
                Format = RHIFormat.R32_Float,
                Usage = RHITextureUsage.UnorderedAccess | RHITextureUsage.ShaderResource,
                DebugName = "Epipolar_FogDepth"
            });
        }

        /// <summary>沿 epipolar 线采样。</summary>
        public void SampleEpipolar(
            RHITexture lightShaftMap, Vector3 lightPos,
            Matrix4x4 viewProj, int screenWidth, int screenHeight)
        {
            // 生成 epipolar 线的端点
            // epipolar 线 = 光源到屏幕边缘的线段
            // 在线上均匀采样
            int gx = (_samplesPerRay + 7) / 8;
            int gy = (_rayCount + 7) / 8;
            _device.Dispatch(gx, gy, 1);
        }

        /// <summary>从 epipolar 图像重建全屏雾。</summary>
        public void Reconstruct(RHITexture output, RHITexture depthBuffer)
        {
            int gx = (output.Width + 7) / 8;
            int gy = (output.Height + 7) / 8;
            _device.SetShaderResource(0, _epipolarImage!);
            _device.SetShaderResource(1, _epipolarDepth!);
            _device.SetShaderResource(2, depthBuffer);
            _device.SetUnorderedAccessView(0, output);
            _device.Dispatch(gx, gy, 1);
        }

        /// <summary>CPU 端 epipolar 线生成。</summary>
        public static (Vector2 Start, Vector2 End)[] GenerateEpipolarLines(
            Vector3 lightPos, Matrix4x4 viewProj,
            int screenW, int screenH, int rayCount)
        {
            // 光源在屏幕空间的位置
            var clipPos = Vector4.Transform(new Vector4(lightPos, 1), viewProj);
            Vector2 lightScreen = new(
                (clipPos.X / clipPos.W * 0.5f + 0.5f) * screenW,
                (1.0f - clipPos.Y / clipPos.W * 0.5f) * screenH);

            var lines = new (Vector2, Vector2)[rayCount];
            float angleStep = MathF.PI * 2.0f / rayCount;
            float maxR = MathF.Sqrt(screenW * screenW + screenH * screenH) * 0.5f;

            for (int i = 0; i < rayCount; i++)
            {
                float angle = i * angleStep;
                Vector2 end = new(
                    lightScreen.X + MathF.Cos(angle) * maxR,
                    lightScreen.Y + MathF.Sin(angle) * maxR);
                lines[i] = (lightScreen, end);
            }

            return lines;
        }
    }
}
