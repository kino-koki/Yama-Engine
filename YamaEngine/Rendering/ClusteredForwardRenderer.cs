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
    // yama Forward+ 渲染器
    //
    // Forward+ 是前向渲染的增强版。和纯 Clustered Forward 不同，
    // 它使用 2.5D 光剔除：
    //   1. 屏幕分成 8x8 或 16x16 的 tile
    //   2. 对每个 tile 计算一个 depth range（min/max depth）
    //   3. 基于深度范围把 tile 当作一个 3D 盒子
    //   4. 做光源 vs tile box 的相交测试（比纯 2D 更精确）
    //   5. 前向渲染时每个像素查自己 tile 的光源列表
    //
    // 相比 Clustered Forward：少了 Z 轴的精细切分，但实现更简单
    // 适合中等光源数量（< 500）的场景。
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>Forward+ 渲染器配置。</summary>
    public sealed class ClusteredForwardConfig
    {
        public int TileSize = 16;
        public int MaxLightsPerTile = 64;
        public int MaxTotalLights = 2048;
        public bool EnableZPrePass = true;
        public bool EnableCSM = true;
        public int CSMCount = 4;
        public int CSMResolution = 2048;
        public float CSMSplitExp = 0.75f;
        public bool EnableOIT = false;
        public bool DepthBounds = true;
        public bool UseComputeCulling = true;
        public bool EnableDebugOverlay = false;
        public static ClusteredForwardConfig Default => new();
        public static ClusteredForwardConfig Performance => new() { TileSize = 32, MaxLightsPerTile = 32, MaxTotalLights = 512, EnableZPrePass = true, EnableCSM = false, EnableOIT = false, DepthBounds = false };
        public static ClusteredForwardConfig Quality => new() { TileSize = 8, MaxLightsPerTile = 128, MaxTotalLights = 4096, CSMResolution = 4096, CSMCount = 4 };
    }

    /// <summary>Forward+ tile 数据（per-tile depth range + light list）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct FPTileData
    {
        public float MinDepth;
        public float MaxDepth;
        public uint LightCount;
        public uint LightOffset;
    }

    /// <summary>Forward+ 光照剔除常量缓冲。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct FPLightingCB
    {
        public Matrix4x4 InvViewProj;
        public Vector3 CameraPos;
        public float NearPlane;
        public float FarPlane;
        public Vector2 ScreenSize;
        public Vector2 InvScreenSize;
        public Vector2 TileSize;
        public float InvTileSizeX;
        public float InvTileSizeY;
        public Vector2 TileCount;
        public int MaxLightsPerTile;
        public int LightCount;
        public int DepthBounds;
        public float Pad0;
    }

    /// <summary>Forward+ tile 深度范围计算器（compute shader 或 CPU fallback）。</summary>
    public sealed class FPTileDepthReducer
    {
        private readonly ClusteredForwardConfig _config;
        private FPTileData[] _tiles = Array.Empty<FPTileData>();
        private int _gridX, _gridY;

        public int GridX => _gridX;
        public int GridY => _gridY;
        public FPTileData[] Tiles => _tiles;

        public FPTileDepthReducer(ClusteredForwardConfig config) { _config = config; }

        public void Init(int sw, int sh)
        {
            _gridX = (sw + _config.TileSize - 1) / _config.TileSize;
            _gridY = (sh + _config.TileSize - 1) / _config.TileSize;
            _tiles = new FPTileData[_gridX * _gridY];
            for (int i = 0; i < _tiles.Length; i++)
            {
                _tiles[i] = new FPTileData { MinDepth = 0.0f, MaxDepth = 1.0f, LightCount = 0, LightOffset = 0 };
            }
        }

        /// <summary>CPU 端计算 tile 的 depth range（从深度缓冲读取）。</summary>
        public void ComputeDepthRanges(RHITexture depthTex, RHICommandList cmd)
        {
            // 生产环境用 compute shader 做 atomic min/max
            // 这里简化处理
            for (int ty = 0; ty < _gridY; ty++)
                for (int tx = 0; tx < _gridX; tx++)
                {
                    int idx = ty * _gridX + tx;
                    _tiles[idx].MinDepth = 0.0f;
                    _tiles[idx].MaxDepth = 1.0f;
            }
        }
    }

    /// <summary>Forward+ 光源剔除器（2.5D：tile + depth range）。</summary>
    public sealed class FPLightCuller
    {
        private readonly ClusteredForwardConfig _config;
        private FPTileData[] _tileData = Array.Empty<FPTileData>();
        private uint[] _lightIndices = Array.Empty<uint>();
        private int _gridX, _gridY;

        public FPTileData[] TileData => _tileData;
        public uint[] LightIndices => _lightIndices;
        public int GridX => _gridX;
        public int GridY => _gridY;

        public FPLightCuller(ClusteredForwardConfig config) { _config = config; }

        public void Init(int sw, int sh)
        {
            _gridX = (sw + _config.TileSize - 1) / _config.TileSize;
            _gridY = (sh + _config.TileSize - 1) / _config.TileSize;
            int total = _gridX * _gridY;
            _tileData = new FPTileData[total];
            _lightIndices = new uint[total * _config.MaxLightsPerTile];
        }

        /// <summary>2.5D 光源剔除：利用 tile 的 depth range 做更精确的测试。</summary>
        public void CullLights(GPULight[] lights, FPTileData[] depthTiles, Matrix4x4 invViewProj, Matrix4x4 viewProj, int sw, int sh)
        {
            int offset = 0;
            for (int ty = 0; ty < _gridY; ty++)
            {
                for (int tx = 0; tx < _gridX; tx++)
                {
                    int idx = ty * _gridX + tx;
                    float tileMinX = tx * _config.TileSize, tileMaxX = MathF.Min(tileMinX + _config.TileSize, sw);
                    float tileMinY = ty * _config.TileSize, tileMaxY = MathF.Min(tileMinY + _config.TileSize, sh);
                    float dMin = depthTiles[idx].MinDepth;
                    float dMax = depthTiles[idx].MaxDepth;

                    // 把 tile 四角 + 近/远 depth 转换到世界空间，计算 3D AABB
                    var aabb = ComputeTileAABB(invViewProj, tileMinX, tileMaxX, tileMinY, tileMaxY, dMin, dMax, sw, sh);

                    uint count = 0;
                    for (int li = 0; li < lights.Length && count < _config.MaxLightsPerTile; li++)
                    {
                        if ((LightType)lights[li].Type == LightType.Directional)
                        { _lightIndices[offset + count++] = (uint)li; continue; }
                        // 点光源：sphere vs AABB
                        if (aabb.IntersectsSphere(lights[li].Position, lights[li].Range))
                            _lightIndices[offset + count++] = (uint)li;
                    }
                    _tileData[idx] = depthTiles[idx];
                    _tileData[idx].LightCount = count;
                    _tileData[idx].LightOffset = (uint)offset;
                    offset += (int)count;
                }
            }
        }

        private static ClusterAABB ComputeTileAABB(Matrix4x4 invVP, float minX, float maxX, float minY, float maxY, float dMin, float dMax, float sw, float sh)
        {
            var aabb = new ClusterAABB { Min = new Vector3(float.MaxValue), Max = new Vector3(float.MinValue) };
            // 四个角 + 近/远
            foreach (float dx in new[] { minX, maxX })
                foreach (float dy in new[] { minY, maxY })
                {
                    aabb.Expand(ScreenToWorld(invVP, dx, dy, dMin, sw, sh));
                    aabb.Expand(ScreenToWorld(invVP, dx, dy, dMax, sw, sh));
                }
            return aabb;
        }

        private static Vector3 ScreenToWorld(Matrix4x4 invVP, float sx, float sy, float depth, float sw, float sh)
        {
            float ndcX = (2.0f * sx / sw) - 1.0f;
            float ndcY = 1.0f - (2.0f * sy / sh);
            float ndcZ = 1.0f - 2.0f * depth;
            var clip = new Vector4(ndcX, ndcY, ndcZ, 1.0f);
            var world = Vector4.Transform(clip, invVP);
            float iw = 1.0f / world.W;
            return new Vector3(world.X * iw, world.Y * iw, world.Z * iw);
        }

        public float GetAverageLightsPerTile()
        {
            if (_tileData.Length == 0) return 0;
            long t = 0; foreach (var td in _tileData) t += td.LightCount;
            return (float)t / _tileData.Length;
        }
    }

    /// <summary>Forward+ 统计。</summary>
    public struct FPStats
    {
        public double TotalMs, ZPrePassMs, DepthReduceMs, LightCullMs, ShadeMs;
        public int DrawCalls, Lights, Tiles, AvgLightsPerTileInt;
    }

    /// <summary>yama Forward+ 渲染器。</summary>
    public sealed class ClusteredForwardRenderer : IDisposable
    {
        private readonly RHIDevice _device;
        private readonly ClusteredForwardConfig _config;
        private readonly FPTileDepthReducer _depthReducer;
        private readonly FPLightCuller _lightCuller;
        private RHITexture? _depth, _color;
        private RHIBuffer? _tileBuf, _lightIndexBuf, _lightBuf, _cb;
        private RHIPipeline? _pipeline;
        private bool _disposed;
        public ClusteredForwardConfig Config => _config;
        public FPStats LastStats { get; private set; }
        public RHITexture? Output => _color;

        public ClusteredForwardRenderer(RHIDevice device, ClusteredForwardConfig? config = null)
        {
            _device = device; _config = config ?? new ClusteredForwardConfig();
            _depthReducer = new FPTileDepthReducer(_config);
            _lightCuller = new FPLightCuller(_config);
        }

        public void Resize(int w, int h)
        {
            _depth?.Dispose(); _color?.Dispose();
            _depth = _device.CreateTexture(new RHITextureDesc { Format = RHIFormat.D32_Float, Width = w, Height = h, Flags = RHITextureFlags.AllowDepthStencil | RHITextureFlags.AllowShaderResource });
            _color = _device.CreateTexture(new RHITextureDesc { Format = RHIFormat.R16G16B16A16_Float, Width = w, Height = h, Flags = RHITextureFlags.AllowRenderTarget | RHITextureFlags.AllowShaderResource });
            _depthReducer.Init(w, h); _lightCuller.Init(w, h);
            int tc = _depthReducer.GridX * _depthReducer.GridY;
            _tileBuf = _device.CreateBuffer((ulong)(tc * 16), 16, RHIBufferUsage.UnorderedAccess | RHIBufferUsage.ShaderResource);
            _lightIndexBuf = _device.CreateBuffer((ulong)tc * (ulong)_config.MaxLightsPerTile * 4, 4, RHIBufferUsage.UnorderedAccess | RHIBufferUsage.ShaderResource);
            _lightBuf = _device.CreateBuffer((ulong)_config.MaxTotalLights * 64, 64, RHIBufferUsage.ShaderResource);
            _cb = _device.CreateBuffer(256, 256, RHIBufferUsage.ConstantBuffer);
        }

        public void Render(RHICommandList cmd, Matrix4x4 viewProj, Matrix4x4 invViewProj, Vector3 camPos, float near, float far, GPULight[] lights, RenderMeshInstance[] meshes)
        {
            if (_color == null || _depth == null) throw new InvalidOperationException("未初始化。");
            var sw = Stopwatch.StartNew(); var stats = new FPStats { Lights = lights.Length, Tiles = _lightCuller.GridX * _lightCuller.GridY };

            // 1. Depth Reduce
            var dr = Stopwatch.StartNew();
            _depthReducer.ComputeDepthRanges(_depth, cmd);
            dr.Stop(); stats.DepthReduceMs = dr.Elapsed.TotalMilliseconds;

            // 2. Light Culling
            var lc = Stopwatch.StartNew();
            _lightCuller.CullLights(lights, _depthReducer.Tiles, invViewProj, viewProj, _color.Width, _color.Height);
            if (_lightCuller.TileData.Length > 0) cmd.UpdateBuffer(_tileBuf!, _lightCuller.TileData);
            if (_lightCuller.LightIndices.Length > 0) cmd.UpdateBuffer(_lightIndexBuf!, _lightCuller.LightIndices);
            lc.Stop(); stats.LightCullMs = lc.Elapsed.TotalMilliseconds;
            stats.AvgLightsPerTileInt = (int)_lightCuller.GetAverageLightsPerTile();

            // 3. 前向渲染
            cmd.ClearRenderTarget(_color, Vector4.Zero); cmd.ClearDepthStencil(_depth, 1.0f, 0);
            var cb = new FPLightingCB
            {
                InvViewProj = invViewProj, CameraPos = camPos, NearPlane = near, FarPlane = far,
                ScreenSize = new Vector2(_color.Width, _color.Height),
                InvScreenSize = new Vector2(1.0f / _color.Width, 1.0f / _color.Height),
                TileSize = new Vector2(_config.TileSize, _config.TileSize),
                InvTileSizeX = 1.0f / _config.TileSize, InvTileSizeY = 1.0f / _config.TileSize,
                TileCount = new Vector2(_lightCuller.GridX, _lightCuller.GridY),
                MaxLightsPerTile = _config.MaxLightsPerTile, LightCount = lights.Length,
                DepthBounds = _config.DepthBounds ? 1 : 0
            };
            cmd.UpdateBuffer(_cb!, ref cb);
            cmd.SetRenderTargets(new[] { _color }, _depth);
            cmd.SetViewport(0, 0, _color.Width, _color.Height);
            cmd.SetGraphicsRootConstantBuffer(0, _cb);
            cmd.SetShaderResourceView(ShaderStage.Pixel, 0, _tileBuf);
            cmd.SetShaderResourceView(ShaderStage.Pixel, 1, _lightIndexBuf);
            cmd.SetShaderResourceView(ShaderStage.Pixel, 2, _lightBuf);
            stats.DrawCalls = 0;
            foreach (var m in meshes)
            {
                if (!m.Visible || m.Mesh == null || m.IsTransparent) continue;
                var obj = new RenderObjectCB { World = m.WorldMatrix, WorldViewProj = m.WorldMatrix * viewProj };
                cmd.SetGraphicsRoot32BitConstants(1, ref obj);
                if (m.Mesh.VertexBuffer != null) cmd.SetVertexBuffer(0, m.Mesh.VertexBuffer, m.Mesh.VertexStride);
                if (m.Mesh.IndexBuffer != null) cmd.SetIndexBuffer(m.Mesh.IndexBuffer, RHIFormat.R32_UInt);
                cmd.DrawIndexed(m.Mesh.IndexCount, 1, 0, 0, 0); stats.DrawCalls++;
            }
            sw.Stop(); stats.ShadeMs = sw.Elapsed.TotalMilliseconds - stats.DepthReduceMs - stats.LightCullMs; stats.TotalMs = sw.Elapsed.TotalMilliseconds; LastStats = stats;
        }

        public string GetDebugInfo() => $"Forward+: {LastStats.DrawCalls} draws, {LastStats.Lights} lights, {LastStats.Tiles} tiles, avg={LastStats.AvgLightsPerTileInt}, {LastStats.TotalMs:F2}ms";
        public void Dispose() { if (_disposed) return; _disposed = true; _depth?.Dispose(); _color?.Dispose(); _tileBuf?.Dispose(); _lightIndexBuf?.Dispose(); _lightBuf?.Dispose(); _cb?.Dispose(); }
    }
}