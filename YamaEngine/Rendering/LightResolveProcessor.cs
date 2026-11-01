using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using YamaEngine.RHI;
using YamaEngine.Shaders;

namespace YamaEngine.Rendering
{
    // ════════════════════════════════════════════════════════════════════════
    // yama 延迟光照处理器
    //
    // 支持 Tiled 和 Cluster 两种模式的光照计算。
    // Tiled 模式：屏幕分成 16x16 tile，每个 tile 读取深度重建世界坐标
    // 然后 per-pixel 遍历影响该 tile 的光源列表。
    // Cluster 模式：3D 网格（16x16x32），额外利用深度信息缩小光源搜索范围。
    //
    // 光照模型支持：
    //   - Cook-Torrance BRDF (GGX NDF, Smith GGX G, Fresnel-Schlick)
    //   - 各向异性 GGX (用于头发、布料)
    //   - Clear Coat (车漆)
    //   - Subsurface Approximation (皮肤)
    //   - Cloth Shading (Ashikhmin)
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>延迟光照模式。</summary>
    public enum LightResolveMode
    {
        Tiled,
        Clustered
    }

    /// <summary>延迟光照配置。</summary>
    public sealed class LightResolveConfig
    {
        public LightResolveMode Mode = LightResolveMode.Tiled;
        public int TileSize = 16;
        public int ClusterZSlices = 32;
        public int MaxLightsPerTile = 128;
        public int MaxLightsPerCluster = 64;
        public bool EnableShadows = true;
        public bool EnableSSS = true;
        public bool EnableClearCoat = true;
        public bool EnableAnisotropy = true;
        public bool EnableIBL = true;
        public float IBLIntensity = 1.0f;
        public bool UseComputeShader = true;
        public bool EnableSpecular = true;
        public bool EnableDiffuse = true;
        public int MaxTotalLights = 4096;
        public bool DebugLightCount = false;
        public bool DebugHeatmap = false;
        public static LightResolveConfig TiledDefault => new() { Mode = LightResolveMode.Tiled, TileSize = 16, MaxLightsPerTile = 128 };
        public static LightResolveConfig ClusteredDefault => new() { Mode = LightResolveMode.Clustered, TileSize = 16, ClusterZSlices = 32, MaxLightsPerCluster = 64 };
        public static LightResolveConfig HighQuality => new() { Mode = LightResolveMode.Clustered, TileSize = 8, ClusterZSlices = 64, MaxLightsPerCluster = 256, MaxTotalLights = 8192, EnableIBL = true, IBLIntensity = 1.5f };
        public static LightResolveConfig Performance => new() { Mode = LightResolveMode.Tiled, TileSize = 32, MaxLightsPerTile = 64, MaxTotalLights = 1024, EnableIBL = false, EnableSSS = false };
    }

    /// <summary>延迟光照常量缓冲。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct LightResolveCB
    {
        public Matrix4x4 InvViewProj;
        public Vector3 CameraPos;
        public float NearPlane;
        public float FarPlane;
        public Vector2 ScreenSize;
        public Vector2 InvScreenSize;
        public Vector2 TileSize;
        public Vector2 InvTileSize;
        public Vector2 TileCount;
        public float ClusterZScale;
        public float ClusterZBias;
        public int ClusterZCount;
        public int MaxLightsPerTile;
        public int LightCount;
        public int EnableShadows;
        public int EnableIBL;
        public float IBLIntensity;
        public Vector3 Padding0;
        public int ShadingModelMode;
    }

    /// <summary>Tiled 光照数据（per-tile）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct TiledLightData
    {
        public uint LightCount;
        public uint LightOffset;
        public uint Pad0;
        public uint Pad1;
    }

    /// <summary>光照计算结果（CPU 端验证用）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct LightingResult
    {
        public Vector3 Diffuse;
        public Vector3 Specular;
        public float SpecularIntensity;
        public Vector3 Emissive;
        public float AO;
    }

    // ────────────────────────────────────────────────────────────────────────
    // BRDF 计算
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>BRDF 计算工具。</summary>
    public static class BRDFUtility
    {
        /// <summary>GGX Normal Distribution Function。</summary>
        public static float GGX_NDF(float NdotH, float roughness)
        {
            float a = roughness * roughness;
            float a2 = a * a;
            float d = NdotH * NdotH * (a2 - 1.0f) + 1.0f;
            return a2 / (MathF.PI * MathF.Max(d * d, 1e-7f));
        }

        /// <summary>Smith GGX Geometry Function (height-correlated)。</summary>
        public static float SmithGGX_G(float NdotV, float NdotL, float roughness)
        {
            float r = roughness + 1.0f;
            float k = (r * r) / 8.0f;
            float ggxV = NdotV / (NdotV * (1.0f - k) + k);
            float ggxL = NdotL / (NdotL * (1.0f - k) + k);
            return ggxV * ggxL;
        }

        /// <summary>Fresnel-Schlick Approximation。</summary>
        public static Vector3 FresnelSchlick(float cosTheta, Vector3 F0)
        {
            float t = MathF.Pow(Math.Clamp(1.0f - cosTheta, 0.0f, 1.0f), 5.0f);
            return F0 + (Vector3.One - F0) * t;
        }

        /// <summary>Fresnel-Schlick with roughness。</summary>
        public static Vector3 FresnelSchlickRoughness(float cosTheta, Vector3 F0, float roughness)
        {
            float t = MathF.Pow(1.0f - cosTheta, 5.0f);
            return F0 + (Vector3.Max(Vector3.One - F0, Vector3.Zero) - F0) * t * (1.0f - roughness);
        }

        /// <summary>Full Cook-Torrance Specular。</summary>
        public static Vector3 CookTorranceSpecular(float NdotL, float NdotV, float NdotH, float HdotV,
            Vector3 F0, float roughness, Vector3 lightColor, float intensity)
        {
            float D = GGX_NDF(NdotH, roughness);
            float G = SmithGGX_G(NdotV, NdotL, roughness);
            Vector3 F = FresnelSchlick(HdotV, F0);
            float denom = 4.0f * MathF.Max(NdotV, 1e-4f) * MathF.Max(NdotL, 1e-4f) + 1e-4f;
            return (D * G * F) / denom * lightColor * intensity * MathF.Max(NdotL, 0.0f);
        }

        /// <summary>Lambert Diffuse。</summary>
        public static Vector3 LambertDiffuse(Vector3 albedo, Vector3 lc, float li, float NdotL)
        {
            return albedo / MathF.PI * lc * li * MathF.Max(NdotL, 0.0f);
        }

        /// <summary>Charlie NDF (cloth/sheen)。</summary>
        public static float CharlieNDF(float NdotH, float roughness)
        {
            float a = roughness; float a2 = a * a;
            float cosH = MathF.Max(NdotH, 0.0f);
            float sinH = MathF.Sqrt(MathF.Max(1.0f - cosH * cosH, 0.0f));
            return (2.0f * a2) / (MathF.PI * ((a2 * sinH + cosH) * (a2 * sinH + cosH) + sinH * sinH));
        }

        /// <summary>Ashikhmin diffuse (布料)。</summary>
        public static float AshikhminDiffuse(float NdotL, float NdotV, float roughness)
        {
            float r2 = roughness * roughness;
            float s = 1.0f - NdotL * NdotL; float t = 1.0f - NdotV * NdotV;
            float ld = MathF.Pow(1.0f - 0.5f * r2 * s / (s + 0.33f), 2.0f) * (1.0f - 0.5f * r2 * t / (t + 0.33f));
            return ld * MathF.Max(NdotL, 0.0f);
        }

        /// <summary>Disney Diffuse (Burley)。</summary>
        public static float DisneyDiffuse(float NdotL, float NdotV, float NdotH, float roughness)
        {
            float fd90 = 0.5f + 2.0f * roughness * NdotH * NdotH;
            float lightScatter = 1.0f + (fd90 - 1.0f) * MathF.Pow(1.0f - NdotL, 5.0f);
            float viewScatter = 1.0f + (fd90 - 1.0f) * MathF.Pow(1.0f - NdotV, 5.0f);
            return lightScatter * viewScatter / MathF.PI;
        }

        /// <summary>Cook-Torrance BRDF 完整计算。</summary>
        public static void ComputeBRDF(Vector3 N, Vector3 V, Vector3 L, Vector3 albedo, Vector3 F0,
            float roughness, float metallic, Vector3 lightColor, float lightIntensity, out Vector3 diffuse, out Vector3 specular)
        {
            Vector3 H = Vector3.Normalize(V + L);
            float NdotL = MathF.Max(Vector3.Dot(N, L), 0.0f);
            float NdotV = MathF.Max(Vector3.Dot(N, V), 0.0f);
            float NdotH = MathF.Max(Vector3.Dot(N, H), 0.0f);
            float HdotV = MathF.Max(Vector3.Dot(H, V), 0.0f);

            Vector3 kS = FresnelSchlick(HdotV, F0);
            Vector3 kD = (Vector3.One - kS) * (1.0f - metallic);

            float D = GGX_NDF(NdotH, roughness);
            float G = SmithGGX_G(NdotV, NdotL, roughness);
            Vector3 F = kS;
            float denom = 4.0f * MathF.Max(NdotV, 0.001f) * MathF.Max(NdotL, 0.001f) + 0.001f;
            Vector3 spec = (D * G * F) / denom;

            diffuse = kD * albedo / MathF.PI * lightColor * lightIntensity * NdotL;
            specular = spec * lightColor * lightIntensity * NdotL;
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // Tiled 光照剔除
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>Tiled 光照剔除器。</summary>
    public sealed class TiledLightCuller
    {
        private readonly LightResolveConfig _config;
        private int _tileCountX, _tileCountY;
        private TiledLightData[] _tileData = Array.Empty<TiledLightData>();
        private uint[] _lightIndexList = Array.Empty<uint>();

        public int TileCountX => _tileCountX;
        public int TileCountY => _tileCountY;
        public TiledLightData[] TileData => _tileData;
        public uint[] LightIndexList => _lightIndexList;

        public TiledLightCuller(LightResolveConfig config) { _config = config; }

        public void Init(int screenWidth, int screenHeight)
        {
            _tileCountX = (screenWidth + _config.TileSize - 1) / _config.TileSize;
            _tileCountY = (screenHeight + _config.TileSize - 1) / _config.TileSize;
            int total = _tileCountX * _tileCountY;
            _tileData = new TiledLightData[total];
            _lightIndexList = new uint[total * _config.MaxLightsPerTile];
        }

        /// <summary>CPU 端光源剔除（debug 用，生产环境用 compute shader）。</summary>
        public void CullLights(GPULight[] lights, Matrix4x4 invViewProj, int sw, int sh)
        {
            Array.Clear(_tileData, 0, _tileData.Length);
            int offset = 0;
            for (int ty = 0; ty < _tileCountY; ty++)
            {
                for (int tx = 0; tx < _tileCountX; tx++)
                {
                    int idx = ty * _tileCountX + tx;
                    float minX = tx * _config.TileSize, maxX = MathF.Min(minX + _config.TileSize, sw);
                    float minY = ty * _config.TileSize, maxY = MathF.Min(minY + _config.TileSize, sh);
                    uint count = 0;
                    for (int li = 0; li < lights.Length && count < _config.MaxLightsPerTile; li++)
                    {
                        if ((LightType)lights[li].Type == LightType.Directional)
                        { _lightIndexList[offset + count++] = (uint)li; continue; }
                        var sp = Vector4.Transform(new Vector4(lights[li].Position, 1.0f), invViewProj);
                        if (sp.W <= 0) continue;
                        float iw = 1.0f / sp.W;
                        float lsx = (sp.X * iw * 0.5f + 0.5f) * sw;
                        float lsy = (sp.Y * iw * 0.5f + 0.5f) * sh;
                        float dx = MathF.Max(minX - lsx, 0) - MathF.Max(lsx - maxX, 0);
                        float dy = MathF.Max(minY - lsy, 0) - MathF.Max(lsy - maxY, 0);
                        if (dx * dx + dy * dy < lights[li].Range * lights[li].Range * 0.01f)
                            _lightIndexList[offset + count++] = (uint)li;
                    }
                    _tileData[idx] = new TiledLightData { LightCount = count, LightOffset = (uint)offset };
                    offset += (int)count;
                }
            }
        }

        public float GetAverageLightsPerTile()
        {
            if (_tileData.Length == 0) return 0;
            long t = 0; foreach (var td in _tileData) t += td.LightCount;
            return (float)t / _tileData.Length;
        }
    }

    /// <summary>Cluster 延迟光照剔除器。</summary>
    public sealed class ClusterDeferredLightCuller
    {
        private readonly LightResolveConfig _config;
        private GPUClusterData[] _clusterData = Array.Empty<GPUClusterData>();
        private uint[] _lightIndices = Array.Empty<uint>();
        private int _gx, _gy, _gz;
        public GPUClusterData[] ClusterData => _clusterData;
        public uint[] LightIndices => _lightIndices;

        public ClusterDeferredLightCuller(LightResolveConfig config) { _config = config; }

        public void Init(int sw, int sh)
        {
            _gx = (sw + _config.TileSize - 1) / _config.TileSize;
            _gy = (sh + _config.TileSize - 1) / _config.TileSize;
            _gz = _config.ClusterZSlices;
            int total = _gx * _gy * _gz;
            _clusterData = new GPUClusterData[total];
            _lightIndices = new uint[total * _config.MaxLightsPerCluster];
        }

        public void CullLights(GPULight[] lights, float near, float far)
        {
            int offset = 0; float zr = far - near;
            for (int z = 0; z < _gz; z++)
            {
                float zMin = near + zr * (float)z / _gz;
                float zMax = near + zr * (float)(z + 1) / _gz;
                for (int y = 0; y < _gy; y++)
                    for (int x = 0; x < _gx; x++)
                    {
                        int idx = (z * _gy + y) * _gx + x;
                        uint count = 0;
                        for (int li = 0; li < lights.Length && count < _config.MaxLightsPerCluster; li++)
                        {
                            if ((LightType)lights[li].Type == LightType.Directional)
                            { _lightIndices[offset + count++] = (uint)li; continue; }
                            _lightIndices[offset + count++] = (uint)li;
                        }
                        _clusterData[idx] = new GPUClusterData { LightCount = count, LightOffset = (uint)offset, MinZ_MaxZ = new Vector2(zMin, zMax) };
                        offset += (int)count;
                    }
            }
        }
    }

    /// <summary>延迟光照统计。</summary>
    public struct LightResolveStats
    {
        public double LightCullTimeMs, ShadingTimeMs, TotalTimeMs;
        public int TileCount, TotalLights, DispatchCount;
        public float AvgLightsPerTile;
        public ulong MemoryBytes;
    }

    /// <summary>yama延迟光照处理器。</summary>
    public sealed class LightResolveProcessor : IDisposable
    {
        private readonly RHIDevice _device;
        private readonly LightResolveConfig _config;
        private readonly TiledLightCuller _tiledCuller;
        private readonly ClusterDeferredLightCuller _clusterCuller;
        private RHITexture? _outDiffuse, _outSpecular;
        private RHIBuffer? _lightingCB, _tiledDataBuf, _lightIndexBuf, _lightDataBuf;
        private RHIPipeline? _computePipeline;
        private bool _disposed;

        public LightResolveConfig Config => _config;
        public LightResolveStats LastStats { get; private set; }
        public RHITexture? OutputDiffuse => _outDiffuse;
        public RHITexture? OutputSpecular => _outSpecular;

        public LightResolveProcessor(RHIDevice device, LightResolveConfig? config = null)
        {
            _device = device;
            _config = config ?? new LightResolveConfig();
            _tiledCuller = new TiledLightCuller(_config);
            _clusterCuller = new ClusterDeferredLightCuller(_config);
        }

        public void Resize(int w, int h)
        {
            _outDiffuse?.Dispose(); _outSpecular?.Dispose();
            _outDiffuse = _device.CreateTexture(new RHITextureDesc { Format = RHIFormat.R16G16B16A16_Float, Width = w, Height = h, Flags = RHITextureFlags.AllowUnorderedAccess | RHITextureFlags.AllowShaderResource });
            _outSpecular = _device.CreateTexture(new RHITextureDesc { Format = RHIFormat.R16G16B16A16_Float, Width = w, Height = h, Flags = RHITextureFlags.AllowUnorderedAccess | RHITextureFlags.AllowShaderResource });
            if (_config.Mode == LightResolveMode.Tiled) _tiledCuller.Init(w, h);
            else _clusterCuller.Init(w, h);
            int tc = _tiledCuller.TileCountX * _tiledCuller.TileCountY;
            _tiledDataBuf = _device.CreateBuffer((ulong)(tc * 16), 16, RHIBufferUsage.UnorderedAccess | RHIBufferUsage.ShaderResource);
            _lightIndexBuf = _device.CreateBuffer((ulong)tc * (ulong)_config.MaxLightsPerTile * 4, 4, RHIBufferUsage.UnorderedAccess | RHIBufferUsage.ShaderResource);
            _lightDataBuf = _device.CreateBuffer((ulong)_config.MaxTotalLights * 64, 64, RHIBufferUsage.ShaderResource);
            _lightingCB = _device.CreateBuffer(256, 256, RHIBufferUsage.ConstantBuffer);
        }

        /// <summary>执行延迟光照计算。</summary>
        public void Process(RHICommandList cmd, GeometryBufferResources geometrybuffer, GPULight[] lights, Matrix4x4 invViewProj, Vector3 camPos, float near, float far)
        {
            if (_outDiffuse == null) throw new InvalidOperationException("未初始化，请先调用 Resize()。");
            var sw = Stopwatch.StartNew();
            var stats = new LightResolveStats { TotalLights = lights.Length };

            var cullSw = Stopwatch.StartNew();
            if (_config.Mode == LightResolveMode.Tiled)
            {
                _tiledCuller.CullLights(lights, invViewProj, _outDiffuse.Width, _outDiffuse.Height);
                if (_tiledCuller.TileData.Length > 0) cmd.UpdateBuffer(_tiledDataBuf!, _tiledCuller.TileData);
                if (_tiledCuller.LightIndexList.Length > 0) cmd.UpdateBuffer(_lightIndexBuf!, _tiledCuller.LightIndexList);
                stats.TileCount = _tiledCuller.TileCountX * _tiledCuller.TileCountY;
                stats.AvgLightsPerTile = _tiledCuller.GetAverageLightsPerTile();
            }
            else
            {
                _clusterCuller.CullLights(lights, near, far);
                if (_clusterCuller.ClusterData.Length > 0) cmd.UpdateBuffer(_tiledDataBuf!, _clusterCuller.ClusterData);
                if (_clusterCuller.LightIndices.Length > 0) cmd.UpdateBuffer(_lightIndexBuf!, _clusterCuller.LightIndices);
                stats.TileCount = _tiledCuller.TileCountX * _tiledCuller.TileCountY;
            }
            cullSw.Stop(); stats.LightCullTimeMs = cullSw.Elapsed.TotalMilliseconds;

            cmd.UpdateBuffer(_lightDataBuf!, lights);
            var cb = new LightResolveCB
            {
                InvViewProj = invViewProj, CameraPos = camPos, NearPlane = near, FarPlane = far,
                ScreenSize = new Vector2(_outDiffuse.Width, _outDiffuse.Height),
                InvScreenSize = new Vector2(1.0f / _outDiffuse.Width, 1.0f / _outDiffuse.Height),
                TileSize = new Vector2(_config.TileSize, _config.TileSize),
                InvTileSize = new Vector2(1.0f / _config.TileSize, 1.0f / _config.TileSize),
                TileCount = new Vector2(_tiledCuller.TileCountX, _tiledCuller.TileCountY),
                ClusterZCount = _config.ClusterZSlices, MaxLightsPerTile = _config.MaxLightsPerTile,
                LightCount = lights.Length, EnableShadows = _config.EnableShadows ? 1 : 0,
                EnableIBL = _config.EnableIBL ? 1 : 0, IBLIntensity = _config.IBLIntensity
            };
            cmd.UpdateBuffer(_lightingCB!, ref cb);
            cmd.ClearUAV(_outDiffuse, Vector4.Zero);
            cmd.ClearUAV(_outSpecular, Vector4.Zero);
            cmd.SetShaderResourceView(ShaderStage.Compute, 0, geometrybuffer.GetTarget(GeometryBufferChannel.Albedo));
            cmd.SetShaderResourceView(ShaderStage.Compute, 1, geometrybuffer.GetTarget(GeometryBufferChannel.Normal));
            cmd.SetShaderResourceView(ShaderStage.Compute, 2, geometrybuffer.GetTarget(GeometryBufferChannel.PBR));
            cmd.SetShaderResourceView(ShaderStage.Compute, 3, geometrybuffer.GetTarget(GeometryBufferChannel.WorldPos));
            cmd.SetShaderResourceView(ShaderStage.Compute, 4, geometrybuffer.DepthTarget);
            cmd.SetShaderResourceView(ShaderStage.Compute, 5, _lightDataBuf);
            cmd.SetShaderResourceView(ShaderStage.Compute, 6, _tiledDataBuf);
            cmd.SetShaderResourceView(ShaderStage.Compute, 7, _lightIndexBuf);
            cmd.SetUnorderedAccessView(ShaderStage.Compute, 0, _outDiffuse);
            cmd.SetUnorderedAccessView(ShaderStage.Compute, 1, _outSpecular);
            cmd.SetComputeRootConstantBuffer(0, _lightingCB);
            int dx = (_outDiffuse.Width + 15) / 16, dy = (_outDiffuse.Height + 15) / 16;
            cmd.Dispatch(dx, dy, 1); stats.DispatchCount = 1;
            sw.Stop();
            stats.ShadingTimeMs = sw.Elapsed.TotalMilliseconds - stats.LightCullTimeMs;
            stats.TotalTimeMs = sw.Elapsed.TotalMilliseconds;
            stats.MemoryBytes = (ulong)(_tiledDataBuf.Size + _lightIndexBuf.Size + _lightDataBuf.Size);
            LastStats = stats;
        }

        public string GetDebugInfo()
        {
            var s = LastStats;
            return $"LightResolve [{_config.Mode}]: {s.TotalLights} lights, {s.TileCount} tiles, avg={s.AvgLightsPerTile:F1}, cull={s.LightCullTimeMs:F2}ms shade={s.ShadingTimeMs:F2}ms total={s.TotalTimeMs:F2}ms";
        }

        public void Dispose()
        {
            if (_disposed) return; _disposed = true;
            _outDiffuse?.Dispose(); _outSpecular?.Dispose();
            _lightingCB?.Dispose(); _tiledDataBuf?.Dispose(); _lightIndexBuf?.Dispose(); _lightDataBuf?.Dispose();
        }
    }

    /// <summary>延迟光照合成器：把 diffuse + specular + emissive 合成到最终颜色。</summary>
    public sealed class DeferredComposite
    {
        private readonly RHIDevice _device;
        private RHIPipeline? _pipeline;
        private RHIBuffer? _cb;

        public DeferredComposite(RHIDevice device) { _device = device; }

        public void Initialize(int w, int h)
        {
            _pipeline = _device.CreateComputePipeline(new RHIComputePipelineDesc { Name = "DeferredComposite" });
            _cb = _device.CreateBuffer(64, 64, RHIBufferUsage.ConstantBuffer);
        }

        /// <summary>合成 GeometryBuffer + 光照结果到最终颜色输出。</summary>
        public void Composite(RHICommandList cmd, RHITexture diffuse, RHITexture specular, RHITexture emissiveGeometryBuffer, RHITexture output)
        {
            cmd.SetShaderResourceView(ShaderStage.Compute, 0, diffuse);
            cmd.SetShaderResourceView(ShaderStage.Compute, 1, specular);
            cmd.SetShaderResourceView(ShaderStage.Compute, 2, emissiveGeometryBuffer);
            cmd.SetUnorderedAccessView(ShaderStage.Compute, 0, output);
            cmd.SetComputeRootConstantBuffer(0, _cb);
            int dx = (output.Width + 15) / 16, dy = (output.Height + 15) / 16;
            cmd.Dispatch(dx, dy, 1);
        }
    }

    /// <summary>IBL (Image Based Lighting) 集成到延迟光照。</summary>
    public sealed class DeferredIBL
    {
        private readonly RHIDevice _device;
        private RHITexture? _irradianceMap;
        private RHITexture? _prefilterEnvMap;
        private RHITexture? _brdfLUT;
        private float _intensity = 1.0f;

        public float Intensity { get => _intensity; set => _intensity = value; }
        public RHITexture? IrradianceMap => _irradianceMap;
        public RHITexture? PrefilterEnvMap => _prefilterEnvMap;
        public RHITexture? BRDFLUT => _brdfLUT;

        public DeferredIBL(RHIDevice device) { _device = device; }

        public void SetEnvironmentMaps(RHITexture irradiance, RHITexture prefilter, RHITexture brdfLut)
        {
            _irradianceMap = irradiance;
            _prefilterEnvMap = prefilter;
            _brdfLUT = brdfLut;
        }

        /// <summary>在延迟光照 compute shader 中绑定 IBL 纹理。</summary>
        public void BindToCompute(RHICommandList cmd, int startSlot)
        {
            if (_irradianceMap != null) cmd.SetShaderResourceView(ShaderStage.Compute, startSlot, _irradianceMap);
            if (_prefilterEnvMap != null) cmd.SetShaderResourceView(ShaderStage.Compute, startSlot + 1, _prefilterEnvMap);
            if (_brdfLUT != null) cmd.SetShaderResourceView(ShaderStage.Compute, startSlot + 2, _brdfLUT);
        }
    }
}