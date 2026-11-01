using System;
using System.Collections.Generic;
using System.Numerics;
using YamaEngine.RHI;
using YamaEngine.Rendering;

namespace YamaEngine.Rendering
{
    /// <summary>
    /// 世界渲染器 — 管理大规模开放世界的分层渲染。
    /// 整合地形、水体、大气、植被、建筑等子系统。
    /// </summary>
    public class WorldRenderer : IDisposable
    {
        private readonly RHIDevice _device;
        private TerrainRenderer? _terrainRenderer;
        private SceneRendererReal? _sceneRenderer;
        private GPUDrivenPipeline? _gpuDrivenPipeline;

        // 世界分块
        private readonly Dictionary<Vector2Int, WorldChunk> _chunks = new();
        public int ChunkSize { get; set; } = 1024; // meters
        public int RenderDistance { get; set; } = 4; // chunks

        // LOD系统
        public float LodDistance0 { get; set; } = 200f;
        public float LodDistance1 { get; set; } = 500f;
        public float LodDistance2 { get; set; } = 1500f;
        public float CullDistance { get; set; } = 3000f;

        // 相机
        public Vector3 CameraPosition { get; set; }
        public Matrix4x4 ViewMatrix { get; set; }
        public Matrix4x4 ProjectionMatrix { get; set; }

        // ── 集成 API (WorldIntegrationHelper 依赖) ──
        /// <summary>世界名称。</summary>
        public string WorldName { get; set; } = "YamaWorld";

        /// <summary>视图变更事件 (相机移动/视锥变化时触发)。</summary>
        public event Action<SceneViewInfo>? OnViewChanged;

        /// <summary>注册特殊场景节点 (如地形根节点)。</summary>
        public void RegisterSpecialNode(string name, object node)
        {
            if (!string.IsNullOrEmpty(name) && node != null)
                _specialNodes[name] = node;
        }

        private readonly Dictionary<string, object> _specialNodes = new();

        /// <summary>注册剔除提供商 (每帧执行视锥剔除)。</summary>
        public void RegisterCullProvider(string name, CullProviderDelegate provider)
        {
            if (!string.IsNullOrEmpty(name) && provider != null)
                _cullProviders[name] = provider;
        }

        private readonly Dictionary<string, CullProviderDelegate> _cullProviders = new();

        /// <summary>注册渲染提供商 (在对应通道录制 GPU 命令)。</summary>
        public void RegisterRenderProvider(string name, RenderProviderDelegate provider)
        {
            if (!string.IsNullOrEmpty(name) && provider != null)
                _renderProviders[name] = provider;
        }

        private readonly Dictionary<string, RenderProviderDelegate> _renderProviders = new();

        /// <summary>通知物理系统地形高度场已变更。</summary>
        public void NotifyPhysicsHeightmapChanged(object heightmapData)
        {
            _lastHeightmapData = heightmapData;
        }

        private object? _lastHeightmapData;

        public WorldRenderer(RHIDevice device)
        {
            _device = device;
        }

        public WorldRenderer(RHIDevice device, RenderGraph graph, SceneRendererReal sceneRenderer) : this(device)
        {
            _renderGraph = graph;
            _sceneRenderer = sceneRenderer;
        }

        private RenderGraph? _renderGraph;
        public RenderGraph? RenderGraph => _renderGraph;

        public void Initialize(TerrainRenderer terrainRenderer, SceneRendererReal sceneRenderer)
        {
            _terrainRenderer = terrainRenderer;
            _sceneRenderer = sceneRenderer;
            _gpuDrivenPipeline = new GPUDrivenPipeline(_device,
                _renderGraph ?? new RenderGraph(_device, "WorldRenderer.GPUDriven"));
        }

        public void UpdateCamera(Vector3 position, Matrix4x4 view, Matrix4x4 proj)
        {
            CameraPosition = position;
            ViewMatrix = view;
            ProjectionMatrix = proj;
            UpdateChunkVisibility();
        }

        private void UpdateChunkVisibility()
        {
            int cx = (int)(CameraPosition.X / ChunkSize);
            int cz = (int)(CameraPosition.Z / ChunkSize);

            // 标记需要加载的chunks
            for (int dx = -RenderDistance; dx <= RenderDistance; dx++)
            {
                for (int dz = -RenderDistance; dz <= RenderDistance; dz++)
                {
                    var key = new Vector2Int(cx + dx, cz + dz);
                    if (!_chunks.ContainsKey(key))
                    {
                        _chunks[key] = new WorldChunk(key, ChunkSize);
                    }
                }
            }

            // 卸载远处chunks
            var toRemove = new List<Vector2Int>();
            foreach (var chunk in _chunks.Keys)
            {
                if (Math.Abs(chunk.X - cx) > RenderDistance + 1 || Math.Abs(chunk.Y - cz) > RenderDistance + 1)
                    toRemove.Add(chunk);
            }
            foreach (var key in toRemove) _chunks.Remove(key);
        }

        public void RenderFrame(RHICommandList cmd, WorldRenderContext context)
        {
            // 1. GPU遮挡剔除
            _gpuDrivenPipeline?.ExecuteOcclusionCulling(cmd, context);

            // 2. 渲染天空/大气
            RenderAtmosphere(cmd, context);

            // 3. 渲染地形
            _terrainRenderer?.Render(cmd, CameraPosition, ViewMatrix, ProjectionMatrix);

            // 4. 渲染场景对象（使用GPU驱动管线）
            _sceneRenderer?.RenderScene(cmd, context.Scene!, context.Camera, context.DeltaTime);

            // 5. 后处理
            ApplyPostProcess(cmd, context);
        }

        private void RenderAtmosphere(RHICommandList cmd, WorldRenderContext context) { }
        private void ApplyPostProcess(RHICommandList cmd, WorldRenderContext context) { }

        public void Dispose()
        {
            _gpuDrivenPipeline?.Dispose();
            _terrainRenderer?.Dispose();
            foreach (var chunk in _chunks.Values) chunk.Dispose();
            _chunks.Clear();
        }
    }

    public class WorldChunk : IDisposable
    {
        public Vector2Int Coordinates { get; }
        public int Size { get; }
        public bool IsLoaded { get; private set; }
        public BoundingBox Bounds { get; }

        public WorldChunk(Vector2Int coords, int size)
        {
            Coordinates = coords;
            Size = size;
            Bounds = new BoundingBox(
                new Vector3(coords.X * size, -1000f, coords.Y * size),
                new Vector3((coords.X + 1) * size, 1000f, (coords.Y + 1) * size));
        }

        public void Load() { IsLoaded = true; }
        public void Unload() { IsLoaded = false; }
        public void Dispose() { Unload(); }
    }

    public struct Vector2Int : IEquatable<Vector2Int>
    {
        public int X, Y;
        public Vector2Int(int x, int y) { X = x; Y = y; }
        public bool Equals(Vector2Int other) => X == other.X && Y == other.Y;
        public override bool Equals(object? obj) => obj is Vector2Int other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(X, Y);
    }


}
