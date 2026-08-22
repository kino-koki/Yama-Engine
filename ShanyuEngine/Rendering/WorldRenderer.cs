using System;
using System.Collections.Generic;
using System.Numerics;
using ShanyuEngine.RHI;
using ShanyuEngine.Rendering;

namespace ShanyuEngine.Rendering
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

        public WorldRenderer(RHIDevice device)
        {
            _device = device;
        }

        public void Initialize(TerrainRenderer terrainRenderer, SceneRendererReal sceneRenderer)
        {
            _terrainRenderer = terrainRenderer;
            _sceneRenderer = sceneRenderer;
            _gpuDrivenPipeline = new GPUDrivenPipeline(_device, sceneRenderer);
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
            _sceneRenderer?.RenderScene(cmd, context.Scene);

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
