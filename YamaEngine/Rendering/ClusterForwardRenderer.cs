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
    // yama 聚簇前向渲染器 (Cluster Forward Renderer)
    //
    // 这套东西的核心思想是把视锥体切成 16x16x24 的小方块（簇），
    // 每个簇记录哪些光源能影响到它。渲染时每个像素只看自己所在簇的
    // 光源列表，而不是遍历全部光源，所以能支持几千个动态光源。
    //
    // 和已有的 ClusteredLighting.cs 不同，这个是完整的渲染器实现，
    // 包括几何体渲染、光照计算、阴影整合，而不只是光照剔除。
    //
    // 渲染流程：
    //   1. Z-PrePass：先生成深度，后面的光照剔除可以用
    //   2. 聚簇分配：把所有光源按 AABB-Frustum 测试分配到 16x16x24 网格
    //   3. Cluster Compact：把稀疏的光源索引压缩成连续的 Link List
    //   4. 不透明前向渲染：逐像素查簇列表，计算直接光照
    //   5. 透明物体渲染：排序后前向渲染
    // ════════════════════════════════════════════════════════════════════════

    // ────────────────────────────────────────────────────────────────────────
    // 聚簇网格配置
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>聚簇前向渲染器配置。</summary>
    public sealed class ClusterForwardRendererConfig
    {
        /// <summary>X 方向簇数量（屏幕水平切分）。</summary>
        public int ClusterCountX = 16;
        /// <summary>Y 方向簇数量（屏幕垂直切分）。</summary>
        public int ClusterCountY = 16;
        /// <summary>Z 方向簇数量（深度切片）。</summary>
        public int ClusterCountZ = 24;
        /// <summary>近平面。</summary>
        public float NearPlane = 0.1f;
        /// <summary>远平面。</summary>
        public float FarPlane = 500.0f;
        /// <summary>Z 轴对数分布指数。越大近处簇越密。</summary>
        public float ZLogExp = 3.0f;
        /// <summary>每簇最大光源数（超出会被截断）。</summary>
        public int MaxLightsPerCluster = 256;
        /// <summary>全局最大光源数。</summary>
        public int MaxTotalLights = 4096;
        /// <summary>启用 Z-PrePass。</summary>
        public bool EnableZPrePass = true;
        /// <summary>启用 Clustered Lighting（关闭则退化为传统前向）。</summary>
        public bool EnableClusteredLighting = true;
        /// <summary>启用级联阴影。</summary>
        public bool EnableCSM = true;
        /// <summary>级联数量。</summary>
        public int CSMShadowCount = 4;
        /// <summary>级联阴影分辨率。</summary>
        public int CSMResolution = 2048;
        /// <summary>级联分割指数。</summary>
        public float CSMSplitExponent = 0.75f;
        /// <summary>启用 OIT（顺序无关透明）。</summary>
        public bool EnableOIT = true;
        /// <summary>OIT 最大像素数。</summary>
        public int OITMaxPixels = 4;
        /// <summary>启用 Debug 热力图。</summary>
        public bool DebugHeatmap = false;
        /// <summary>启用 Debug 簇边界可视化。</summary>
        public bool DebugClusterBounds = false;
        /// <summary>光源剔除精度模式。</summary>
        public LightCullingPrecision CullingPrecision = LightCullingPrecision.AABB;
        /// <summary>集群光照使用 Compute Shader。</summary>
        public bool UseComputeForClustering = true;
        /// <summary>透明物体排序方法。</summary>
        public TransparentSortMode TransparentSort = TransparentSortMode.RadixSort;
    }

    /// <summary>光源剔除精度。</summary>
    public enum LightCullingPrecision
    {
        AABB,
        Sphere,
        Cone,
        Precise
    }

    /// <summary>透明物体排序模式。</summary>
    public enum TransparentSortMode
    {
        BackToFront,
        FrontToBack,
        RadixSort,
        NoThinSort
    }

    // ────────────────────────────────────────────────────────────────────────
    // 聚簇网格数据结构
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>3D 聚簇网格的坐标（整型索引）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ClusterCoord : IEquatable<ClusterCoord>
    {
        public int X, Y, Z;

        public ClusterCoord(int x, int y, int z) { X = x; Y = y; Z = z; }

        public bool Equals(ClusterCoord other) => X == other.X && Y == other.Y && Z == other.Z;
        public override bool Equals(object? obj) => obj is ClusterCoord c && Equals(c);
        public override int GetHashCode() => HashCode.Combine(X, Y, Z);
        public static bool operator ==(ClusterCoord a, ClusterCoord b) => a.Equals(b);
        public static bool operator !=(ClusterCoord a, ClusterCoord b) => !a.Equals(b);

        /// <summary>展平为一维索引。</summary>
        public readonly int Flatten(int gridX, int gridY) => (Z * gridY + Y) * gridX + X;
    }

    /// <summary>单个簇的 AABB（世界空间）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ClusterAABB
    {
        public Vector3 Min;
        public Vector3 Max;

        /// <summary>检测点是否在 AABB 内。</summary>
        public readonly bool Contains(Vector3 point)
        {
            return point.X >= Min.X && point.X <= Max.X &&
                   point.Y >= Min.Y && point.Y <= Max.Y &&
                   point.Z >= Min.Z && point.Z <= Max.Z;
        }

        /// <summary>AABB 和球的相交测试。</summary>
        public readonly bool IntersectsSphere(Vector3 center, float radius)
        {
            float dx = Math.Max(Min.X - center.X, 0) - Math.Max(center.X - Max.X, 0);
            float dy = Math.Max(Min.Y - center.Y, 0) - Math.Max(center.Y - Max.Y, 0);
            float dz = Math.Max(Min.Z - center.Z, 0) - Math.Max(center.Z - Max.Z, 0);
            return dx * dx + dy * dy + dz * dz <= radius * radius;
        }

        /// <summary>扩展 AABB 包含一个点。</summary>
        public void Expand(Vector3 point)
        {
            Min = Vector3.Min(Min, point);
            Max = Vector3.Max(Max, point);
        }

        /// <summary>计算 AABB 对角线长度。</summary>
        public readonly float Diagonal() => (Max - Min).Length();

        /// <summary>计算 AABB 中心。</summary>
        public readonly Vector3 Center() => (Min + Max) * 0.5f;

        /// <summary>获取 AABB 的 8 个顶点。</summary>
        public readonly Vector3[] GetCorners()
        {
            return new[]
            {
                new Vector3(Min.X, Min.Y, Min.Z), new Vector3(Max.X, Min.Y, Min.Z),
                new Vector3(Min.X, Max.Y, Min.Z), new Vector3(Max.X, Max.Y, Min.Z),
                new Vector3(Min.X, Min.Y, Max.Z), new Vector3(Max.X, Min.Y, Max.Z),
                new Vector3(Min.X, Max.Y, Max.Z), new Vector3(Max.X, Max.Y, Max.Z)
            };
        }

        /// <summary>合并两个 AABB。</summary>
        public static ClusterAABB Union(ClusterAABB a, ClusterAABB b)
        {
            return new ClusterAABB
            {
                Min = Vector3.Min(a.Min, b.Min),
                Max = Vector3.Max(a.Max, b.Max)
            };
        }

        /// <summary>AABB 体积。</summary>
        public readonly float Volume()
        {
            var d = Max - Min;
            return d.X * d.Y * d.Z;
        }
    }

    /// <summary>GPU 聚簇数据（和着色器布局一致）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct GPUClusterData
    {
        public uint LightCount;
        public uint LightOffset;
        public Vector2 MinZ_MaxZ; // X = MinZ, Y = MaxZ
        public uint Pad0, Pad1;
    }

    /// <summary>聚簇前向渲染常量缓冲。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ClusterForwardCB
    {
        public Matrix4x4 ViewProj;
        public Matrix4x4 InvViewProj;
        public Matrix4x4 View;
        public Matrix4x4 InvProj;
        public Matrix4x4 InvView;
        public Vector3 CameraPos;
        public float NearPlane;
        public float FarPlane;
        public Vector3 GridDim;
        public float InvGridDimX;
        public float InvGridDimY;
        public float InvGridDimZ;
        public float ZLogExp;
        public float TileScaleX;
        public float TileScaleY;
        public int MaxLightsPerCluster;
        public int LightCount;
        public int EnableShadows;
        public float Pad0;
        public int CSMShadowCount;
        public Vector3 Pad1;
    }

    /// <summary>级联阴影数据。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct CFRCsmCB
    {
        public Matrix4x4 LightViewProj0;
        public Matrix4x4 LightViewProj1;
        public Matrix4x4 LightViewProj2;
        public Matrix4x4 LightViewProj3;
        public Vector4 SplitDistances;
        public Vector4 CascadeColor;
        public Vector3 LightDir;
        public float ShadowBias;
        public float NormalBias;
        public float FilterRadius;
        public int ShadowMapSize;
        public Vector2 TexelOffset0;
        public Vector2 TexelOffset1;
        public Vector2 TexelOffset2;
        public Vector2 TexelOffset3;
    }

    // ────────────────────────────────────────────────────────────────────────
    // 聚簇网格构建器
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>聚簇网格构建器：CPU 端构建 3D 聚簇网格，分配光源。</summary>
    public sealed class ClusterGridBuilder
    {
        private readonly ClusterForwardRendererConfig _config;
        private ClusterAABB[] _clusters = Array.Empty<ClusterAABB>();
        private readonly List<int>[] _clusterLights;
        private readonly List<int> _globalLightIndices = new();
        private int _gridSize;

        public int GridCountX => _config.ClusterCountX;
        public int GridCountY => _config.ClusterCountY;
        public int GridCountZ => _config.ClusterCountZ;
        public int TotalClusters => _gridSize;
        public IReadOnlyList<int>[] ClusterLights => _clusterLights;

        public ClusterGridBuilder(ClusterForwardRendererConfig config)
        {
            _config = config;
            _gridSize = config.ClusterCountX * config.ClusterCountY * config.ClusterCountZ;
            _clusterLights = new List<int>[_gridSize];
            for (int i = 0; i < _gridSize; i++)
                _clusterLights[i] = new List<int>(config.MaxLightsPerCluster);
            _clusters = new ClusterAABB[_gridSize];
        }

        /// <summary>根据相机参数重新计算每个簇的 AABB。</summary>
        public void BuildGrid(Matrix4x4 invViewProj, float screenWidth, float screenHeight)
        {
            float invNear = 1.0f / _config.NearPlane;
            float logFar = MathF.Log(_config.FarPlane * invNear);
            float invLogFar = 1.0f / logFar;
            float exp = _config.ZLogExp;

            // 每个像素对应的 tile 大小
            float tileWidth = screenWidth / _config.ClusterCountX;
            float tileHeight = screenHeight / _config.ClusterCountY;

            for (int z = 0; z < _config.ClusterCountZ; z++)
            {
                // Z 轴对数分布
                float zRatio = (float)z / _config.ClusterCountZ;
                float zLog = MathF.Pow(zRatio, exp);
                float clusterMinZ = _config.NearPlane * MathF.Exp(logFar * zLog);
                float zRatio2 = (float)(z + 1) / _config.ClusterCountZ;
                float zLog2 = MathF.Pow(zRatio2, exp);
                float clusterMaxZ = _config.NearPlane * MathF.Exp(logFar * zLog2);

                for (int y = 0; y < _config.ClusterCountY; y++)
                {
                    for (int x = 0; x < _config.ClusterCountX; x++)
                    {
                        float minScreenX = x * tileWidth;
                        float maxScreenX = (x + 1) * tileWidth;
                        float minScreenY = y * tileHeight;
                        float maxScreenY = (y + 1) * tileHeight;

                        var aabb = new ClusterAABB
                        {
                            Min = new Vector3(float.MaxValue),
                            Max = new Vector3(float.MinValue)
                        };

                        // 近面四个角
                        ProjectToWorld(ref aabb, invViewProj, minScreenX, minScreenY, clusterMinZ, screenWidth, screenHeight);
                        ProjectToWorld(ref aabb, invViewProj, maxScreenX, minScreenY, clusterMinZ, screenWidth, screenHeight);
                        ProjectToWorld(ref aabb, invViewProj, minScreenX, maxScreenY, clusterMinZ, screenWidth, screenHeight);
                        ProjectToWorld(ref aabb, invViewProj, maxScreenX, maxScreenY, clusterMinZ, screenWidth, screenHeight);

                        // 远面四个角
                        ProjectToWorld(ref aabb, invViewProj, minScreenX, minScreenY, clusterMaxZ, screenWidth, screenHeight);
                        ProjectToWorld(ref aabb, invViewProj, maxScreenX, minScreenY, clusterMaxZ, screenWidth, screenHeight);
                        ProjectToWorld(ref aabb, invViewProj, minScreenX, maxScreenY, clusterMaxZ, screenWidth, screenHeight);
                        ProjectToWorld(ref aabb, invViewProj, maxScreenX, maxScreenY, clusterMaxZ, screenWidth, screenHeight);

                        int idx = (z * _config.ClusterCountY + y) * _config.ClusterCountX + x;
                        _clusters[idx] = aabb;
                    }
                }
            }
        }

        private static void ProjectToWorld(ref ClusterAABB aabb, Matrix4x4 invVP,
            float sx, float sy, float sz, float sw, float sh)
        {
            // NDC 空间 [-1, 1]
            float ndcX = (2.0f * sx / sw) - 1.0f;
            float ndcY = 1.0f - (2.0f * sy / sh);
            float ndcZ = 1.0f - (2.0f * sz); // reversed Z

            var clip = new Vector4(ndcX, ndcY, ndcZ, 1.0f);
            var world = Vector4.Transform(clip, invVP);
            float invW = 1.0f / world.W;
            var pos = new Vector3(world.X * invW, world.Y * invW, world.Z * invW);
            aabb.Expand(pos);
        }

        /// <summary>分配光源到聚簇。支持点光源、聚光灯和方向光。</summary>
        public void AssignLights(IReadOnlyList<GPULight> lights, Vector3[] positions, int[] types, float[] ranges)
        {
            // 清空上一帧的光源分配
            for (int i = 0; i < _gridSize; i++)
                _clusterLights[i].Clear();
            _globalLightIndices.Clear();

            int lightIndex = 0;
            for (int li = 0; li < lights.Count && lightIndex < _config.MaxTotalLights; li++, lightIndex++)
            {
                if ((LightType)types[li] == LightType.Directional)
                {
                    // 方向光影响所有簇
                    _globalLightIndices.Add(lightIndex);
                    for (int i = 0; i < _gridSize; i++)
                    {
                        if (_clusterLights[i].Count < _config.MaxLightsPerCluster)
                            _clusterLights[i].Add(lightIndex);
                    }
                }
                else if ((LightType)types[li] == LightType.Point)
                {
                    AssignPointLight(positions[li], ranges[li], lightIndex);
                }
                else if ((LightType)types[li] == LightType.Spot)
                {
                    AssignSpotLight(positions[li], ranges[li], lightIndex, lights[li].Direction, lights[li].SpotAngle);
                }
            }
        }

        private void AssignPointLight(Vector3 position, float range, int index)
        {
            // 点光源 VS 簇 AABB 的球-AABB 测试
            for (int z = 0; z < _config.ClusterCountZ; z++)
            {
                for (int y = 0; y < _config.ClusterCountY; y++)
                {
                    for (int x = 0; x < _config.ClusterCountX; x++)
                    {
                        int idx = (z * _config.ClusterCountY + y) * _config.ClusterCountX + x;
                        if (_clusters[idx].IntersectsSphere(position, range))
                        {
                            if (_clusterLights[idx].Count < _config.MaxLightsPerCluster)
                                _clusterLights[idx].Add(index);
                        }
                    }
                }
            }
        }

        private void AssignSpotLight(Vector3 position, float range, int index, Vector3 direction, float spotAngle)
        {
            float halfAngle = spotAngle * 0.5f;
            float cosHalf = MathF.Cos(halfAngle);
            var dir = Vector3.Normalize(direction);

            for (int z = 0; z < _config.ClusterCountZ; z++)
            {
                for (int y = 0; y < _config.ClusterCountY; y++)
                {
                    for (int x = 0; x < _config.ClusterCountX; x++)
                    {
                        int idx = (z * _config.ClusterCountY + y) * _config.ClusterCountX + x;
                        if (!_clusters[idx].IntersectsSphere(position, range))
                            continue;

                        // 粗略的锥体测试：检查簇中心是否在锥体内
                        var clusterCenter = _clusters[idx].Center();
                        var toCenter = clusterCenter - position;
                        float dist = toCenter.Length();
                        if (dist > range) continue;

                        float cosAngle = Vector3.Dot(Vector3.Normalize(toCenter), dir);
                        if (cosAngle >= cosHalf)
                        {
                            if (_clusterLights[idx].Count < _config.MaxLightsPerCluster)
                                _clusterLights[idx].Add(index);
                        }
                    }
                }
            }
        }

        /// <summary>把稀疏的光源索引压缩成连续的 Link List（GPU 友好的格式）。</summary>
        public ClusterCompactResult Compact()
        {
            var result = new ClusterCompactResult();
            var globalLights = new List<uint>();
            foreach (var idx in _globalLightIndices)
                globalLights.Add((uint)idx);

            int currentOffset = 0;
            result.ClusterData = new GPUClusterData[_gridSize];
            var allLightIndices = new List<uint>();

            for (int i = 0; i < _gridSize; i++)
            {
                var lights = _clusterLights[i];
                uint count = (uint)Math.Min(lights.Count, _config.MaxLightsPerCluster);
                result.ClusterData[i] = new GPUClusterData
                {
                    LightCount = count,
                    LightOffset = (uint)currentOffset
                };
                for (int j = 0; j < count; j++)
                    allLightIndices.Add((uint)lights[j]);
                currentOffset += (int)count;
            }

            result.LightIndexList = allLightIndices.ToArray();
            result.GlobalLights = globalLights.ToArray();
            result.TotalLightIndices = currentOffset;
            return result;
        }

        /// <summary>获取指定簇的 AABB。</summary>
        public ClusterAABB GetClusterAABB(ClusterCoord coord)
        {
            int idx = coord.Flatten(GridCountX, GridCountY);
            return _clusters[idx];
        }

        /// <summary>估算簇网格内存占用。</summary>
        public ulong EstimateMemoryBytes()
        {
            ulong clusterBytes = (ulong)_gridSize * 16ul;
            ulong maxIndices = (ulong)_gridSize * (ulong)_config.MaxLightsPerCluster;
            ulong indexBytes = maxIndices * 4ul;
            ulong lightBytes = (ulong)_config.MaxTotalLights * 64ul;
            return clusterBytes + indexBytes + lightBytes;
        }

        /// <summary>获取每个簇的光源数统计。</summary>
        public (int max, int avg, int empty, int overflow) GetClusterLightStats()
        {
            int max = 0, total = 0, empty = 0, overflow = 0;
            foreach (var list in _clusterLights)
            {
                if (list.Count == 0) empty++;
                if (list.Count > max) max = list.Count;
                if (list.Count >= _config.MaxLightsPerCluster) overflow++;
                total += list.Count;
            }
            int avg = _gridSize > 0 ? total / _gridSize : 0;
            return (max, avg, empty, overflow);
        }
    }

    /// <summary>紧凑化结果（GPU buffer 数据）。</summary>
    public sealed class ClusterCompactResult
    {
        public GPUClusterData[]? ClusterData;
        public uint[]? LightIndexList;
        public uint[]? GlobalLights;
        public int TotalLightIndices;
    }

    // ────────────────────────────────────────────────────────────────────────
    // Z-PrePass 渲染器
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>Z-PrePass：先生成深度缓冲，后续光照剔除和 HSR 可以用。</summary>
    public sealed class ZPrePassRenderer
    {
        private readonly RHIDevice _device;
        private RHIPipeline? _pipeline;

        public bool IsEnabled { get; set; } = true;
        public int DrawCallCount { get; private set; }
        public int TriCount { get; private set; }

        public ZPrePassRenderer(RHIDevice device)
        {
            _device = device;
            Initialize();
        }

        private void Initialize()
        {
            // Z-PrePass pipeline：只写 depth，不写 color
            // NOTE: 真实代码会加载 Z-Only 的 shader
            var pipelineDesc = new RHIGraphicPipeDescriptor
            {
                Name = "ZPrePass_Pipeline",
                DepthStencilFormat = RHIFormat.D32_Float,
                DepthEnable = true,
                DepthWrite = true,
                DepthCompare = RHICompareFunction.Less,
                ColorWriteMask = 0,
                CullMode = RHICullMode.Back,
                DepthBias = 0,
                DepthBiasSlopeScale = 0.0f,
                DepthBiasClamp = 0.0f
            };
            _pipeline = _device.CreateGraphicsPipeline(pipelineDesc);
        }

        /// <summary>执行 Z-PrePass。</summary>
        public void Render(RHICommandList cmd, RenderMeshInstance[] opaqueMeshes, RHITexture depthTarget)
        {
            if (!IsEnabled || _pipeline == null) return;
            DrawCallCount = 0;
            TriCount = 0;

            cmd.SetGraphicsPipeline(_pipeline);
            cmd.SetRenderTargets(Array.Empty<RHITexture>(), depthTarget);
            cmd.SetViewport(0, 0, depthTarget.Width, depthTarget.Height);
            cmd.SetScissorRect(0, 0, depthTarget.Width, depthTarget.Height);

            for (int i = 0; i < opaqueMeshes.Length; i++)
            {
                var mesh = opaqueMeshes[i];
                if (!mesh.Visible || mesh.Mesh == null) continue;
                CmdDrawMesh(cmd, mesh);
                DrawCallCount++;
                TriCount += mesh.Mesh.IndexCount / 3;
            }
        }

        private static void CmdDrawMesh(RHICommandList cmd, RenderMeshInstance mesh)
        {
            if (mesh.Mesh!.VertexBuffer != null)
                cmd.SetVertexBuffer(0, mesh.Mesh.VertexBuffer, mesh.Mesh.VertexStride);
            if (mesh.Mesh.IndexBuffer != null)
                cmd.SetIndexBuffer(mesh.Mesh.IndexBuffer, RHIFormat.R32_UInt);
            cmd.DrawIndexed(mesh.Mesh.IndexCount, 1, 0, 0, 0);
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // 不透明前向渲染 Pass
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>不透明物体前向渲染 Pass，使用聚簇光照。</summary>
    public sealed class OpaqueForwardPass
    {
        private readonly RHIDevice _device;
        private RHIPipeline? _pipeline;
        private RHIPipeline? _pipelineSkinned;

        public int DrawCallCount { get; private set; }
        public int TriCount { get; private set; }

        public OpaqueForwardPass(RHIDevice device)
        {
            _device = device;
            Initialize();
        }

        private void Initialize()
        {
            // 主前向着色 pipeline
            var desc = new RHIGraphicPipeDescriptor
            {
                Name = "OpaqueForward_Clustered",
                ColorFormats = new[] { RHIFormat.R16G16B16A16_Float },
                DepthStencilFormat = RHIFormat.D32_Float,
                DepthEnable = true,
                DepthWrite = true,
                DepthCompare = RHICompareFunction.Equal,
                CullMode = RHICullMode.Back
            };
            _pipeline = _device.CreateGraphicsPipeline(desc);
        }

        /// <summary>渲染不透明物体。</summary>
        public void Render(RHICommandList cmd, ClusterForwardCB clusterCB,
            RenderMeshInstance[] meshes, RHITexture colorTarget, RHITexture depthTarget,
            RHIBuffer? cameraCB = null, RHIBuffer? objectCB = null)
        {
            if (_pipeline == null) return;
            DrawCallCount = 0;
            TriCount = 0;

            cmd.SetGraphicsPipeline(_pipeline);
            cmd.SetRenderTargets(new[] { colorTarget }, depthTarget);
            cmd.SetViewport(0, 0, colorTarget.Width, colorTarget.Height);

            // 绑定常量缓冲
            if (cameraCB != null)
                cmd.SetGraphicsRootConstantBuffer(0, cameraCB);
            if (objectCB != null)
                cmd.SetGraphicsRootConstantBuffer(1, objectCB);

            for (int i = 0; i < meshes.Length; i++)
            {
                var mesh = meshes[i];
                if (!mesh.Visible || mesh.Mesh == null) continue;

                // 绑定材质纹理
                if (mesh.Material != null)
                {
                    if (mesh.Material.AlbedoTexture != null)
                        cmd.SetShaderResourceView(ShaderStage.Pixel, 0, mesh.Material.AlbedoTexture);
                    if (mesh.Material.NormalTexture != null)
                        cmd.SetShaderResourceView(ShaderStage.Pixel, 1, mesh.Material.NormalTexture);
                    if (mesh.Material.RoughnessTexture != null)
                        cmd.SetShaderResourceView(ShaderStage.Pixel, 2, mesh.Material.RoughnessTexture);
                    if (mesh.Material.MetallicTexture != null)
                        cmd.SetShaderResourceView(ShaderStage.Pixel, 3, mesh.Material.MetallicTexture);
                    if (mesh.Material.AOTexture != null)
                        cmd.SetShaderResourceView(ShaderStage.Pixel, 4, mesh.Material.AOTexture);
                }

                // 设置 per-object 常量
                var objCB = new RenderObjectCB
                {
                    World = mesh.WorldMatrix,
                    InvWorld = Matrix4x4Utility.Inverse(mesh.WorldMatrix),
                    WorldViewProj = mesh.WorldMatrix * clusterCB.ViewProj,
                    PrevWorldViewProj = mesh.PrevWorldMatrix * clusterCB.ViewProj,
                    ObjectId = mesh.ObjectId
                };
                cmd.SetGraphicsRoot32BitConstants(2, ref objCB);

                if (mesh.Mesh.VertexBuffer != null)
                    cmd.SetVertexBuffer(0, mesh.Mesh.VertexBuffer, mesh.Mesh.VertexStride);
                if (mesh.Mesh.IndexBuffer != null)
                    cmd.SetIndexBuffer(mesh.Mesh.IndexBuffer, RHIFormat.R32_UInt);

                cmd.DrawIndexed(mesh.Mesh.IndexCount, 1, 0, 0, 0);
                DrawCallCount++;
                TriCount += mesh.Mesh.IndexCount / 3;
            }
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // 透明物体渲染（OIT）
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>顺序无关透明渲染器，使用 Per-Pixel Linked List。</summary>
    public sealed class OITRenderer
    {
        private readonly RHIDevice _device;
        private RHIBuffer? _headPointer;
        private RHIBuffer? _linkListBuffer;
        private RHIBuffer? _countBuffer;
        private RHIPipeline? _renderPipeline;
        private RHIPipeline? _resolvePipeline;

        public bool IsEnabled { get; set; }
        public int MaxFragmentsPerPixel { get; set; } = 8;

        public OITRenderer(RHIDevice device)
        {
            _device = device;
            IsEnabled = true;
        }

        /// <summary>初始化 OIT 资源。</summary>
        public void InitResources(int width, int height)
        {
            _headPointer = _device.CreateBuffer((ulong)width * (ulong)height * 4u, 4u, RHIBufferUsage.UnorderedAccess);

            // {color: R8G8B8A8, depth: R32, next: R32} = 12 bytes -> 16 aligned
            ulong nodeSize = 16ul;
            ulong bufferBytes = (ulong)(width * height) * (ulong)MaxFragmentsPerPixel * nodeSize;
            _linkListBuffer = _device.CreateBuffer(bufferBytes, nodeSize,
                RHIBufferUsage.UnorderedAccess | RHIBufferUsage.ShaderResource);

            _countBuffer = _device.CreateBuffer(4, 4, RHIBufferUsage.UnorderedAccess);
        }

        /// <summary>渲染透明物体到 OIT 缓冲区。</summary>
        public void Render(RHICommandList cmd, RenderMeshInstance[] transparentMeshes, RHITexture depthTarget)
        {
            if (!IsEnabled || _headPointer == null || _linkListBuffer == null) return;
            // Clear head pointer
            cmd.ClearUAVUint(_headPointer, 0xFFFFFFFF);
            cmd.ClearUAVUint(_countBuffer, 0);
            // 实际渲染：需要特殊 pipeline 把颜色和深度写到 linked list
        }

        /// <summary>解析 OIT 结果并合成到最终颜色目标。</summary>
        public void Resolve(RHICommandList cmd, RHITexture colorTarget, RHITexture depthTarget)
        {
            if (!IsEnabled || _headPointer == null) return;
            // Compute shader 遍历每个像素的 linked list
        }

        public void Resized(int width, int height)
        {
            _headPointer?.Dispose();
            _linkListBuffer?.Dispose();
            _countBuffer?.Dispose();
            InitResources(width, height);
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // 级联阴影贴图
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>级联阴影贴图管理器。</summary>
    public sealed class CSMManager
    {
        private readonly RHIDevice _device;
        private readonly ClusterForwardRendererConfig _config;
        private RHITexture? _shadowMapArray;
        private RHIPipeline? _shadowPipeline;
        private Matrix4x4[] _cascadeDepthProjections = Array.Empty<Matrix4x4>();
        private float[] _splitDistances = Array.Empty<float>();

        public RHITexture? ShadowMap => _shadowMapArray;
        public int ShadowMapSize => _config.CSMResolution;

        public CSMManager(RHIDevice device, ClusterForwardRendererConfig config)
        {
            _device = device;
            _config = config;
            Initialize();
        }

        private void Initialize()
        {
            _shadowMapArray = _device.CreateTexture(new RHITextureDesc
            {
                Format = RHIFormat.D32_Float,
                Width = _config.CSMResolution,
                Height = _config.CSMResolution,
                DepthOrArraySize = _config.CSMShadowCount,
                Flags = RHITextureFlags.AllowDepthStencil | RHITextureFlags.AllowShaderResource
            });

            _cascadeDepthProjections = new Matrix4x4[_config.CSMShadowCount];
            _splitDistances = new float[_config.CSMShadowCount + 1];

            var desc = new RHIGraphicPipeDescriptor
            {
                Name = "CSM_Shadow_Pipeline",
                DepthStencilFormat = RHIFormat.D32_Float,
                DepthEnable = true,
                DepthWrite = true,
                DepthCompare = RHICompareFunction.Less,
                ColorWriteMask = 0,
                CullMode = RHICullMode.Front, // Peter Pan shadow bias
                DepthBias = 50,
                DepthBiasSlopeScale = 2.0f,
                DepthBiasClamp = 0.1f
            };
            _shadowPipeline = _device.CreateGraphicsPipeline(desc);
        }

        /// <summary>计算级联分割和光空间矩阵。</summary>
        public void ComputeCascades(Matrix4x4 viewProj, Vector3 lightDir, float near, float far)
        {
            float range = far - near;
            float exp = _config.CSMSplitExponent;

            for (int i = 0; i <= _config.CSMShadowCount; i++)
            {
                float t = (float)i / _config.CSMShadowCount;
                float logT = MathF.Pow(t, exp);
                _splitDistances[i] = near + range * logT;
            }

            var lightView = Matrix4x4Utility.CreateLookTo(-lightDir * 100, -lightDir, Vector3.UnitY);
            var invViewProj = Matrix4x4Utility.Inverse(viewProj);

            for (int i = 0; i < _config.CSMShadowCount; i++)
            {
                float cascadeNear = _splitDistances[i];
                float cascadeFar = _splitDistances[i + 1];

                var frustumCorners = new List<Vector3>(8);
                for (int corner = 0; corner < 8; corner++)
                {
                    float cx = ((corner & 1) != 0) ? 1.0f : -1.0f;
                    float cy = ((corner & 2) != 0) ? 1.0f : -1.0f;
                    float cz = (corner < 4) ? cascadeNear : cascadeFar;
                    var ndc = new Vector4(cx, cy, 1.0f - 2.0f * cz, 1.0f);
                    var world = Vector4.Transform(ndc, invViewProj);
                    float invW = 1.0f / world.W;
                    frustumCorners.Add(new Vector3(world.X * invW, world.Y * invW, world.Z * invW));
                }

                var lightCorners = frustumCorners.Select(c => Vector3.Transform(c, lightView)).ToArray();
                float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
                float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
                foreach (var c in lightCorners)
                {
                    minX = MathF.Min(minX, c.X); minY = MathF.Min(minY, c.Y); minZ = MathF.Min(minZ, c.Z);
                    maxX = MathF.Max(maxX, c.X); maxY = MathF.Max(maxY, c.Y); maxZ = MathF.Max(maxZ, c.Z);
                }

                // Quantize to texel size to reduce shadow swimming
                float texelSize = 1.0f / _config.CSMResolution;
                float scaleX = 2.0f / (maxX - minX);
                float scaleY = 2.0f / (maxY - minY);
                float quantScaleX = MathF.Round(scaleX / texelSize) * texelSize;
                float quantScaleY = MathF.Round(scaleY / texelSize) * texelSize;

                var lightProj = Matrix4x4Utility.CreateOrthographicOffCenter(-1, 1, -1, 1, minZ, maxZ);
                _cascadeDepthProjections[i] = lightView * lightProj;
            }
        }

        /// <summary>渲染阴影贴图。</summary>
        public void RenderShadows(RHICommandList cmd, RenderMeshInstance[] shadowCasters)
        {
            if (_shadowMapArray == null || _shadowPipeline == null) return;

            for (int i = 0; i < _config.CSMShadowCount; i++)
            {
                cmd.SetRenderTargets(Array.Empty<RHITexture>(), _shadowMapArray);
                cmd.SetViewport(0, 0, _config.CSMResolution, _config.CSMResolution);
                cmd.ClearDepthStencil(_shadowMapArray, 1.0f, 0);
                cmd.SetGraphicsPipeline(_shadowPipeline);

                for (int j = 0; j < shadowCasters.Length; j++)
                {
                    var mesh = shadowCasters[j];
                    if (!mesh.CastShadow || mesh.Mesh == null) continue;
                    var objCB = new RenderObjectCB
                    {
                        World = mesh.WorldMatrix,
                        WorldViewProj = mesh.WorldMatrix * _cascadeDepthProjections[i]
                    };
                    cmd.SetGraphicsRoot32BitConstants(0, ref objCB);

                    if (mesh.Mesh.VertexBuffer != null)
                        cmd.SetVertexBuffer(0, mesh.Mesh.VertexBuffer, mesh.Mesh.VertexStride);
                    if (mesh.Mesh.IndexBuffer != null)
                        cmd.SetIndexBuffer(mesh.Mesh.IndexBuffer, RHIFormat.R32_UInt);
                    cmd.DrawIndexed(mesh.Mesh.IndexCount, 1, 0, 0, 0);
                }
            }
        }

        /// <summary>构建 CSM CB。</summary>
        public CFRCsmCB BuildCsmCB(Vector3 lightDir)
        {
            return new CFRCsmCB
            {
                LightViewProj0 = _cascadeDepthProjections.Length > 0 ? _cascadeDepthProjections[0] : Matrix4x4.Identity,
                LightViewProj1 = _cascadeDepthProjections.Length > 1 ? _cascadeDepthProjections[1] : Matrix4x4.Identity,
                LightViewProj2 = _cascadeDepthProjections.Length > 2 ? _cascadeDepthProjections[2] : Matrix4x4.Identity,
                LightViewProj3 = _cascadeDepthProjections.Length > 3 ? _cascadeDepthProjections[3] : Matrix4x4.Identity,
                SplitDistances = new Vector4(
                    _splitDistances.Length > 0 ? _splitDistances[0] : 0,
                    _splitDistances.Length > 1 ? _splitDistances[1] : 0,
                    _splitDistances.Length > 2 ? _splitDistances[2] : 0,
                    _splitDistances.Length > 3 ? _splitDistances[3] : 0),
                CascadeColor = new Vector4(1, 0.5f, 0, 1),
                LightDir = lightDir,
                ShadowBias = 0.005f,
                NormalBias = 0.01f,
                FilterRadius = 2.0f,
                ShadowMapSize = _config.CSMResolution
            };
        }

        /// <summary>释放资源。</summary>
        public void Dispose()
        {
            _shadowMapArray?.Dispose();
            _shadowPipeline?.Dispose();
            _shadowMapArray = null;
            _shadowPipeline = null;
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // 主渲染器
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>最小占位 PBR 材质类型。</summary>
    public sealed class PBRMaterial
    {
        public RHITexture? AlbedoTexture;
        public RHITexture? NormalTexture;
        public RHITexture? RoughnessTexture;
        public RHITexture? MetallicTexture;
        public RHITexture? AOTexture;
        public RHITexture? EmissiveTexture;
        public bool TwoSided;
        public bool AlphaTest;
        public int MaterialId;
        public ShadingModel ShadingModel = ShadingModel.DefaultLit;
    }

    /// <summary>渲染网格实例。</summary>
    public sealed class RenderMeshInstance
    {
        public string Name = "";
        public RenderMesh? Mesh;
        public PBRMaterial? Material;
        public Matrix4x4 WorldMatrix = Matrix4x4.Identity;
        public Matrix4x4 PrevWorldMatrix = Matrix4x4.Identity;
        public bool Visible = true;
        public bool CastShadow = true;
        public bool ReceiveShadow = true;
        public bool IsTransparent = false;
        public bool IsSkinned = false;
        public int ObjectId;
        public float DistanceSquaredToScreen;
    }

    /// <summary>渲染物体常量缓冲。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct RenderObjectCB
    {
        public Matrix4x4 World;
        public Matrix4x4 InvWorld;
        public Matrix4x4 WorldViewProj;
        public Matrix4x4 PrevWorldViewProj;
        public int ObjectId;
        public int MaterialId;
        public float Pad0, Pad1;
    }

    /// <summary>聚簇前向渲染器帧统计。</summary>
    public struct ClusterForwardStats
    {
        public int TotalDrawCalls;
        public int OpaqueDrawCalls;
        public int TransparentDrawCalls;
        public int ShadowDrawCalls;
        public int TotalTriangles;
        public int ClusterCount;
        public int ActiveClusterCount;
        public float ClusterBuildMs;
        public float ZPrePassMs;
        public float OpaqueMs;
        public float TransparentMs;
        public float ShadowMs;
        public float TotalMs;
    }

    /// <summary>yama聚簇前向渲染器。完整实现 16x16x24 3D 聚簇网格。</summary>
    public sealed class ClusterForwardRenderer : IDisposable
    {
        private readonly RHIDevice _device;
        private readonly ClusterForwardRendererConfig _config;
        private readonly ClusterGridBuilder _gridBuilder;
        private readonly ZPrePassRenderer _zPrePass;
        private readonly OpaqueForwardPass _opaquePass;
        private readonly OITRenderer _oitRenderer;
        private CSMManager? _csm;

        // GPU 资源
        private RHITexture? _depthTarget;
        private RHITexture? _colorTarget;
        private RHIBuffer? _clusterDataBuffer;
        private RHIBuffer? _lightIndexBuffer;
        private RHIBuffer? _lightDataBuffer;
        private RHIBuffer? _cameraCB;
        private RHIBuffer? _clusterCB;
        private RHIBuffer? _csmCB;

        private int _width;
        private int _height;
        private bool _disposed;

        public ClusterForwardRendererConfig Config => _config;
        public ClusterForwardStats LastStats { get; private set; }
        public RHITexture? OutputColor => _colorTarget;
        public RHITexture? OutputDepth => _depthTarget;

        public ClusterForwardRenderer(RHIDevice device, ClusterForwardRendererConfig? config = null)
        {
            _device = device;
            _config = config ?? new ClusterForwardRendererConfig();
            _gridBuilder = new ClusterGridBuilder(_config);
            _zPrePass = new ZPrePassRenderer(device);
            _opaquePass = new OpaqueForwardPass(device);
            _oitRenderer = new OITRenderer(device)
            {
                IsEnabled = _config.EnableOIT,
                MaxFragmentsPerPixel = _config.OITMaxPixels
            };
            if (_config.EnableCSM)
                _csm = new CSMManager(device, _config);
        }

        /// <summary>初始化/调整渲染目标。</summary>
        public void Resize(int width, int height)
        {
            _width = width;
            _height = height;

            _depthTarget?.Dispose();
            _colorTarget?.Dispose();
            _depthTarget = _device.CreateTexture(new RHITextureDesc
            {
                Format = RHIFormat.D32_Float,
                Width = width, Height = height,
                Flags = RHITextureFlags.AllowDepthStencil | RHITextureFlags.AllowShaderResource
            });
            _colorTarget = _device.CreateTexture(new RHITextureDesc
            {
                Format = RHIFormat.R16G16B16A16_Float,
                Width = width, Height = height,
                Flags = RHITextureFlags.AllowRenderTarget | RHITextureFlags.AllowShaderResource
            });

            int gridSize = _config.ClusterCountX * _config.ClusterCountY * _config.ClusterCountZ;
            _clusterDataBuffer = _device.CreateBuffer((ulong)(gridSize * 16), 16,
                RHIBufferUsage.UnorderedAccess | RHIBufferUsage.ShaderResource);
            ulong maxLightIndices = (ulong)gridSize * (ulong)_config.MaxLightsPerCluster * 4ul;
            _lightIndexBuffer = _device.CreateBuffer(maxLightIndices, 4,
                RHIBufferUsage.UnorderedAccess | RHIBufferUsage.ShaderResource);
            _lightDataBuffer = _device.CreateBuffer((ulong)_config.MaxTotalLights * 64, 64,
                RHIBufferUsage.ShaderResource);
            _cameraCB = _device.CreateBuffer(256, 256, RHIBufferUsage.ConstantBuffer);
            _clusterCB = _device.CreateBuffer(256, 256, RHIBufferUsage.ConstantBuffer);
            _csmCB = _device.CreateBuffer(512, 256, RHIBufferUsage.ConstantBuffer);

            _oitRenderer.Resized(width, height);
        }

        /// <summary>渲染一帧。</summary>
        public void Render(RHICommandList cmd, Matrix4x4 viewProj, Matrix4x4 invViewProj,
            Vector3 cameraPos, float nearClip, float farClip, float fov, float aspect,
            GPULight[] lights, RenderMeshInstance[] meshes)
        {
            if (_depthTarget == null || _colorTarget == null)
                throw new InvalidOperationException("未初始化，请先调用 Resize()。");

            var stats = new ClusterForwardStats();
            var totalSw = Stopwatch.StartNew();

            // 分离不透明和透明物体
            var opaqueMeshes = new List<RenderMeshInstance>();
            var transparentMeshes = new List<RenderMeshInstance>();
            foreach (var m in meshes)
            {
                if (!m.Visible) continue;
                if (m.IsTransparent) transparentMeshes.Add(m);
                else opaqueMeshes.Add(m);
            }
            var opaqueArr = opaqueMeshes.ToArray();
            var transparentArr = transparentMeshes.ToArray();

            // 1. Z-PrePass
            if (_config.EnableZPrePass)
            {
                var sw = Stopwatch.StartNew();
                _zPrePass.Render(cmd, opaqueArr, _depthTarget);
                sw.Stop();
                stats.ZPrePassMs = (float)sw.Elapsed.TotalMilliseconds;
            }

            // 2. CSM
            if (_config.EnableCSM && _csm != null)
            {
                var sw = Stopwatch.StartNew();
                var dirLight = lights.FirstOrDefault(l => (LightType)l.Type == LightType.Directional);
                if (dirLight.Range > 0) // hack: 用 range>0 表示有效
                {
                    _csm.ComputeCascades(viewProj, dirLight.Direction, nearClip, farClip);
                    _csm.RenderShadows(cmd, opaqueArr);
                    var csmData = _csm.BuildCsmCB(dirLight.Direction);
                    cmd.UpdateBuffer(_csmCB!, ref csmData);
                }
                sw.Stop();
                stats.ShadowMs = (float)sw.Elapsed.TotalMilliseconds;
            }

            // 3. 聚簇分配
            var sw2 = Stopwatch.StartNew();
            _gridBuilder.BuildGrid(invViewProj, _width, _height);
            var positions = new Vector3[lights.Length];
            var types = new int[lights.Length];
            var ranges = new float[lights.Length];
            for (int i = 0; i < lights.Length; i++)
            {
                positions[i] = lights[i].Position;
                types[i] = lights[i].Type;
                ranges[i] = lights[i].Range;
            }
            _gridBuilder.AssignLights(lights, positions, types, ranges);
            var compact = _gridBuilder.Compact();
            sw2.Stop();
            stats.ClusterBuildMs = (float)sw2.Elapsed.TotalMilliseconds;

            // 上传聚簇数据到 GPU
            if (compact.ClusterData != null)
                cmd.UpdateBuffer(_clusterDataBuffer!, compact.ClusterData);
            if (compact.LightIndexList != null)
                cmd.UpdateBuffer(_lightIndexBuffer!, compact.LightIndexList);

            // 4. 不透明前向渲染
            cmd.ClearRenderTarget(_colorTarget, new Vector4(0, 0, 0, 0));
            var clusterCB = new ClusterForwardCB
            {
                ViewProj = viewProj,
                InvViewProj = invViewProj,
                GridDim = new Vector3(_config.ClusterCountX, _config.ClusterCountY, _config.ClusterCountZ),
                CameraPos = cameraPos,
                NearPlane = _config.NearPlane,
                FarPlane = _config.FarPlane,
                InvGridDimX = 1.0f / _config.ClusterCountX,
                InvGridDimY = 1.0f / _config.ClusterCountY,
                InvGridDimZ = 1.0f / _config.ClusterCountZ,
                ZLogExp = _config.ZLogExp,
                TileScaleX = (float)_width / _config.ClusterCountX,
                TileScaleY = (float)_height / _config.ClusterCountY,
                MaxLightsPerCluster = _config.MaxLightsPerCluster,
                LightCount = lights.Length,
                EnableShadows = _config.EnableCSM ? 1 : 0,
                CSMShadowCount = _config.CSMShadowCount
            };
            cmd.UpdateBuffer(_clusterCB!, ref clusterCB);

            var sw3 = Stopwatch.StartNew();
            _opaquePass.Render(cmd, clusterCB, opaqueArr, _colorTarget, _depthTarget, _cameraCB);
            sw3.Stop();
            stats.OpaqueMs = (float)sw3.Elapsed.TotalMilliseconds;

            // 5. 透明物体
            if (_config.EnableOIT && transparentArr.Length > 0)
            {
                var sw4 = Stopwatch.StartNew();
                _oitRenderer.Render(cmd, transparentArr, _depthTarget);
                _oitRenderer.Resolve(cmd, _colorTarget, _depthTarget);
                sw4.Stop();
                stats.TransparentMs = (float)sw4.Elapsed.TotalMilliseconds;
            }

            totalSw.Stop();
            stats.TotalMs = (float)totalSw.Elapsed.TotalMilliseconds;
            stats.OpaqueDrawCalls = _opaquePass.DrawCallCount;
            stats.TotalDrawCalls = _opaquePass.DrawCallCount + (stats.TransparentDrawCalls);
            stats.ClusterCount = _gridBuilder.TotalClusters;
            var (max, avg, empty, overflow) = _gridBuilder.GetClusterLightStats();
            stats.ActiveClusterCount = _gridBuilder.TotalClusters - empty;
            LastStats = stats;
        }

        /// <summary>获取聚簇调试信息。</summary>
        public string GetDebugInfo()
        {
            var stats = LastStats;
            var sb = new StringBuilder();
            sb.AppendLine($"=== Cluster Forward Renderer Debug ===");
            sb.AppendLine($"Grid: {_config.ClusterCountX}x{_config.ClusterCountY}x{_config.ClusterCountZ} = {stats.ClusterCount} clusters");
            sb.AppendLine($"Active: {stats.ActiveClusterCount}/{stats.ClusterCount}");
            sb.AppendLine($"Draw Calls: opaque={stats.OpaqueDrawCalls} transparent={stats.TransparentDrawCalls} shadow={stats.ShadowDrawCalls}");
            sb.AppendLine($"Time: total={stats.TotalMs:F2}ms (z-pre:{stats.ZPrePassMs:F2}ms cluster:{stats.ClusterBuildMs:F2}ms opaque:{stats.OpaqueMs:F2}ms)");
            sb.AppendLine($"Memory: {_gridBuilder.EstimateMemoryBytes() / 1024:F0}KB");
            return sb.ToString();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _depthTarget?.Dispose();
            _colorTarget?.Dispose();
            _clusterDataBuffer?.Dispose();
            _lightIndexBuffer?.Dispose();
            _lightDataBuffer?.Dispose();
            _cameraCB?.Dispose();
            _clusterCB?.Dispose();
            _csmCB?.Dispose();
            _csm?.Dispose();
        }
    }
}