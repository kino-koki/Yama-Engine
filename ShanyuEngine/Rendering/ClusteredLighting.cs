using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using ShanyuEngine.RHI;
using ShanyuEngine.Shaders;

namespace ShanyuEngine.Rendering
{
    // ════════════════════════════════════════════════════════════════════════
    // 山雨 Clustered Lighting (超越 UE7 级别)
    //
    // 基于分簇(Cluster)的高效光照剔除系统:
    //   1. 视锥体 3D 分簇: 将屏幕划分为 64x64 tile, Z 轴对数分布,
    //      每个簇存储影响它的光源索引列表。
    //   2. 光源分配: 点光源/聚光灯/方向光通过 AABB-Frustum 测试
    //      映射到对应簇, 生成 Light Link List。
    //   3. Clustered Deferred Shading: 光照计算时每个像素只遍历
    //      其所在簇的光源列表, 从 O(N) 降到 O(1~8)。
    //   4. 支持数千动态光源, 内存占用固定 (与分辨率无关的簇数量)。
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>光源类型。数值与底层光照数据布局对齐，可用于 (LightType) 强转。</summary>
    public enum LightType
    {
        Directional = 0,
        Point = 1,
        Spot = 2,
        Rectangle = 3,
        Disk = 4,
        Area = 5,
        Rect = 6,
        Sphere = 7,
        Tube = 8
    }

    /// <summary>GPU 光源数据结构 (与着色器布局严格一致)。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct GPULight
    {
        public Vector3 Position;
        public float Range;
        public Vector3 Direction;
        public float SpotAngle;
        public Vector3 Color;
        public float Intensity;
        public Vector3 ShadowMapOffset;
        public int Type;
        public Vector2 ShadowMapScale;
        public int CastShadows;
        public int Pad;
    }

    /// <summary>集群光照配置。</summary>
    public sealed class ClusteredLightingSettings
    {
        /// <summary>屏幕 X 方向簇数量。</summary>
        public int ClusterCountX = 64;
        /// <summary>屏幕 Y 方向簇数量。</summary>
        public int ClusterCountY = 64;
        /// <summary>视图 Z 方向簇数量。</summary>
        public int ClusterCountZ = 32;
        /// <summary>近平面。</summary>
        public float NearPlane = 0.1f;
        /// <summary>远平面。</summary>
        public float FarPlane = 1000.0f;
        /// <summary>Z 分布指数 (0=线性, 1=均匀对数)。</summary>
        public float ZDistributionExp = 2.0f;
        /// <summary>每簇最大光源数。</summary>
        public int MaxLightsPerCluster = 128;
        /// <summary>全局最大光源数。</summary>
        public int MaxTotalLights = 1024;
        /// <summary>启用热力图调试。</summary>
        public bool DebugHeatmap = false;
    }

    /// <summary>单个簇的光照列表信息。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ClusterLightData
    {
        public uint Offset;
        public uint Count;
        public uint Pad0;
        public uint Pad1;
    }

    /// <summary>集群光照常量缓冲。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ClusteredLightingCB
    {
        public Matrix4x4 InvProj;
        public Matrix4x4 ViewMatrix;
        public Vector2 ClusterSize;
        public float NearPlane;
        public float FarPlane;
        public float ZDistributionExp;
        public int ClusterCountX;
        public int ClusterCountY;
        public int ClusterCountZ;
        public int TotalClusterCount;
        public int LightCount;
        public int MaxLightsPerCluster;
        public Vector2 Resolution;
        public float Pad0;
    }

    /// <summary>
    /// Clustered Lighting 主系统: 管理分簇、光源分配与光照计算。
    /// </summary>
    public sealed class ClusteredLighting : IDisposable
    {
        private readonly RHIDevice _device;
        private readonly ClusteredLightingSettings _settings;

        // GPU 资源
        private RHIBuffer? _lightBuffer;
        private RHIBuffer? _clusterLightDataBuffer;
        private RHIBuffer? _lightIndexBuffer;
        private RHIBuffer? _lightCounterBuffer;
        private RHIBuffer? _dispatchArgsBuffer;
        private RHITexture? _debugHeatmap;

        // 管线
        private RHIRootSignature? _rootSignature;
        private RHIPipelineState? _clusterBuildPipeline;
        private RHIPipelineState? _lightCullingPipeline;
        private RHIPipelineState? _lightingPipeline;

        // 光源列表
        private readonly List<GPULight> _lights = new();
        private GPULight[]? _gpuLights;

        private int _width, _height;
        private int _totalClusters;
        private bool _lightsDirty = true;

        public ClusteredLightingSettings Settings => _settings;
        public int LightCount => _lights.Count;
        public IReadOnlyList<GPULight> Lights => _lights;
        public RHIBuffer? LightBuffer => _lightBuffer;
        public RHIBuffer? ClusterLightDataBuffer => _clusterLightDataBuffer;
        public RHIBuffer? LightIndexBuffer => _lightIndexBuffer;

        public ClusteredLighting(RHIDevice device, ClusteredLightingSettings? settings = null)
        {
            _device = device;
            _settings = settings ?? new ClusteredLightingSettings();
        }

        /// <summary>初始化 GPU 资源与管线。</summary>
        public void Initialize(int width, int height)
        {
            _width = width;
            _height = height;
            _totalClusters = _settings.ClusterCountX * _settings.ClusterCountY * _settings.ClusterCountZ;

            CreatePipelines();
            AllocateResources();
        }

        private void CreatePipelines()
        {
            var rootDesc = new RHIRootSignatureDesc
            {
                Parameters = new RHIRootParameter[]
                {
                    new() { ParameterType = RHIRootParameterType.CBV, ShaderVisibility = RHIShaderVisibility.All,
                            Descriptor = new RHIRootDescriptor { ShaderRegister = 0 } },
                    new() { ParameterType = RHIRootParameterType.DescriptorTable, ShaderVisibility = RHIShaderVisibility.All,
                            DescriptorTable = new RHIRootDescriptorTable { Ranges = new[] {
                                new RHIRootDescriptorRange { RangeType = RHIDescriptorRangeType.SRV, NumDescriptors = 8, BaseShaderRegister = 0 } } } },
                    new() { ParameterType = RHIRootParameterType.DescriptorTable, ShaderVisibility = RHIShaderVisibility.All,
                            DescriptorTable = new RHIRootDescriptorTable { Ranges = new[] {
                                new RHIRootDescriptorRange { RangeType = RHIDescriptorRangeType.UAV, NumDescriptors = 8, BaseShaderRegister = 0 } } } }
                },
                StaticSamplers = new[] { RHISamplerDesc.LinearWrap, RHISamplerDesc.PointClamp }
            };
            _rootSignature = _device.CreateRootSignature(rootDesc);

            _clusterBuildPipeline = _device.CreateComputePipelineState(new RHIComputePipelineDesc
            { Name = "ClusterBuild", CS = ShaderManager.Instance.GetBytecode("Clustered/Clustered", "ClusterBuildCS", RHIShaderStage.Compute) });

            _lightCullingPipeline = _device.CreateComputePipelineState(new RHIComputePipelineDesc
            { Name = "LightCulling", CS = ShaderManager.Instance.GetBytecode("Clustered/Clustered", "LightCullingCS", RHIShaderStage.Compute) });

            _lightingPipeline = _device.CreateComputePipelineState(new RHIComputePipelineDesc
            { Name = "ClusteredLighting", CS = ShaderManager.Instance.GetBytecode("Clustered/Clustered", "ClusteredLightingCS", RHIShaderStage.Compute) });
        }

        private void AllocateResources()
        {
            int maxLights = _settings.MaxTotalLights;
            int totalClusters = _totalClusters;
            int maxLightsPerCluster = _settings.MaxLightsPerCluster;

            // 光源结构化缓冲
            var lightBufDesc = new RHIBufferDesc
            {
                SizeInBytes = (ulong)(maxLights * Marshal.SizeOf<GPULight>()),
                Flags = RHIBufferFlags.Structured | RHIBufferFlags.ShaderResource,
                Stride = (ulong)Marshal.SizeOf<GPULight>(),
                HeapType = RHIResourceHeapType.Default
            };
            _lightBuffer = _device.CreateBuffer(lightBufDesc, "ClusteredLights");

            // 每簇光源列表偏移+计数
            var clusterDataDesc = new RHIBufferDesc
            {
                SizeInBytes = (ulong)(totalClusters * Marshal.SizeOf<ClusterLightData>()),
                Flags = RHIBufferFlags.Structured | RHIBufferFlags.UnorderedAccess | RHIBufferFlags.ShaderResource,
                Stride = (ulong)Marshal.SizeOf<ClusterLightData>(),
                HeapType = RHIResourceHeapType.Default
            };
            _clusterLightDataBuffer = _device.CreateBuffer(clusterDataDesc, "ClusterLightData");

            // 全局光源索引列表 (链表式存储)
            var indexBufDesc = new RHIBufferDesc
            {
                SizeInBytes = (ulong)(totalClusters * maxLightsPerCluster * sizeof(uint)),
                Flags = RHIBufferFlags.Structured | RHIBufferFlags.UnorderedAccess | RHIBufferFlags.ShaderResource,
                Stride = sizeof(uint),
                HeapType = RHIResourceHeapType.Default
            };
            _lightIndexBuffer = _device.CreateBuffer(indexBufDesc, "ClusterLightIndices");

            // 原子计数器
            var counterDesc = new RHIBufferDesc
            {
                SizeInBytes = 16,
                Flags = RHIBufferFlags.UnorderedAccess | RHIBufferFlags.Structured,
                Stride = 4,
                HeapType = RHIResourceHeapType.Default
            };
            _lightCounterBuffer = _device.CreateBuffer(counterDesc, "ClusterLightCounter");

            // DispatchArgs (用于间接 dispatch)
            var argsDesc = new RHIBufferDesc
            {
                SizeInBytes = 24,
                Flags = RHIBufferFlags.IndirectArgs | RHIBufferFlags.UnorderedAccess,
                Stride = 4,
                HeapType = RHIResourceHeapType.Default
            };
            _dispatchArgsBuffer = _device.CreateBuffer(argsDesc, "ClusterDispatchArgs");

            // 调试热力图
            if (_settings.DebugHeatmap)
            {
                var heatDesc = new RHITextureDesc
                {
                    Dimension = RHIResourceDimension.Texture2D,
                    Format = RHIFormat.R8G8B8A8_UNorm,
                    Width = _settings.ClusterCountX,
                    Height = _settings.ClusterCountY,
                    DepthOrArraySize = 1,
                    MipLevels = 1,
                    SampleCount = 1,
                    Flags = RHITextureFlags.UnorderedAccess | RHITextureFlags.ShaderResource
                };
                _debugHeatmap = _device.CreateTexture(heatDesc, "ClusterDebugHeatmap");
            }
        }

        /// <summary>添加光源。</summary>
        public void AddLight(in GPULight light)
        {
            if (_lights.Count >= _settings.MaxTotalLights) return;
            _lights.Add(light);
            _lightsDirty = true;
        }

        /// <summary>清除所有光源。</summary>
        public void ClearLights()
        {
            _lights.Clear();
            _lightsDirty = true;
        }

        /// <summary>更新 GPU 光源缓冲 (若光源列表有变化)。</summary>
        public void UpdateLightBuffer(RHICommandList cmdList)
        {
            if (!_lightsDirty || _lightBuffer == null) return;
            _gpuLights = _lights.ToArray();
            _device.UploadToBuffer(_lightBuffer, _gpuLights);
            _lightsDirty = false;
        }

        /// <summary>每帧更新: 构建簇 AABB + 光源剔除 + 生成分簇列表。</summary>
        public void Update(RHICommandList cmdList, in Matrix4x4 view, in Matrix4x4 proj)
        {
            if (_clusterBuildPipeline == null || _lightCullingPipeline == null) return;

            UpdateLightBuffer(cmdList);

            // Pass 1: 构建每个簇的 AABB (视图空间)
            cmdList.SetComputeRootSignature(_rootSignature!);
            cmdList.SetPipelineState(_clusterBuildPipeline);
            cmdList.TransitionTo(_clusterLightDataBuffer!, RHIResourceState.UnorderedAccess);
            cmdList.TransitionTo(_lightCounterBuffer!, RHIResourceState.UnorderedAccess);

            var cb = new ClusteredLightingCB
            {
                InvProj = Matrix4x4.Identity,
                ViewMatrix = view,
                ClusterSize = new Vector2(
                    (float)_width / _settings.ClusterCountX,
                    (float)_height / _settings.ClusterCountY),
                NearPlane = _settings.NearPlane,
                FarPlane = _settings.FarPlane,
                ZDistributionExp = _settings.ZDistributionExp,
                ClusterCountX = _settings.ClusterCountX,
                ClusterCountY = _settings.ClusterCountY,
                ClusterCountZ = _settings.ClusterCountZ,
                TotalClusterCount = _totalClusters,
                LightCount = _lights.Count,
                MaxLightsPerCluster = _settings.MaxLightsPerCluster,
                Resolution = new Vector2(_width, _height)
            };

            int groupsX = (_settings.ClusterCountX + 7) / 8;
            int groupsY = (_settings.ClusterCountY + 7) / 8;
            int groupsZ = (_settings.ClusterCountZ + 3) / 4;
            cmdList.Dispatch(groupsX, groupsY, groupsZ);

            // UAV barrier 确保簇构建完成
            cmdList.UAVBarrier(_clusterLightDataBuffer);

            // Pass 2: 光源分配 (每个光源测试覆盖哪些簇)
            cmdList.SetPipelineState(_lightCullingPipeline);
            cmdList.TransitionTo(_lightBuffer!, RHIResourceState.NonPixelShaderResource);
            cmdList.TransitionTo(_clusterLightDataBuffer!, RHIResourceState.UnorderedAccess);
            cmdList.TransitionTo(_lightIndexBuffer!, RHIResourceState.UnorderedAccess);
            cmdList.TransitionTo(_lightCounterBuffer!, RHIResourceState.UnorderedAccess);

            int lightGroups = (_lights.Count + 63) / 64;
            cmdList.Dispatch(lightGroups, 1, 1);

            cmdList.TransitionTo(_clusterLightDataBuffer!, RHIResourceState.NonPixelShaderResource);
            cmdList.TransitionTo(_lightIndexBuffer!, RHIResourceState.NonPixelShaderResource);
            cmdList.TransitionTo(_lightCounterBuffer!, RHIResourceState.NonPixelShaderResource);
        }

        /// <summary>执行 Clustered Deferred Lighting Pass。</summary>
        public void RenderLighting(RHICommandList cmdList, RHITexture sceneColor,
            RHITexture depth, RHITexture geometrybufferA, RHITexture geometrybufferB, RHITexture geometrybufferC,
            in Matrix4x4 view, in Matrix4x4 proj, Vector3 cameraPos)
        {
            if (_lightingPipeline == null) return;

            cmdList.SetComputeRootSignature(_rootSignature!);
            cmdList.SetPipelineState(_lightingPipeline);

            cmdList.TransitionTo(sceneColor, RHIResourceState.UnorderedAccess);
            cmdList.TransitionTo(depth, RHIResourceState.NonPixelShaderResource);
            cmdList.TransitionTo(geometrybufferA, RHIResourceState.NonPixelShaderResource);
            cmdList.TransitionTo(geometrybufferB, RHIResourceState.NonPixelShaderResource);
            cmdList.TransitionTo(geometrybufferC, RHIResourceState.NonPixelShaderResource);
            cmdList.TransitionTo(_clusterLightDataBuffer!, RHIResourceState.NonPixelShaderResource);
            cmdList.TransitionTo(_lightIndexBuffer!, RHIResourceState.NonPixelShaderResource);
            cmdList.TransitionTo(_lightBuffer!, RHIResourceState.NonPixelShaderResource);

            cmdList.Dispatch((_width + 7) / 8, (_height + 7) / 8, 1);

            cmdList.TransitionTo(sceneColor, RHIResourceState.PixelShaderResource);
        }

        /// <summary>窗口尺寸变化。</summary>
        public void Resize(int width, int height)
        {
            _width = width;
            _height = height;
            _lightBuffer?.Dispose();
            _clusterLightDataBuffer?.Dispose();
            _lightIndexBuffer?.Dispose();
            _lightCounterBuffer?.Dispose();
            _dispatchArgsBuffer?.Dispose();
            _debugHeatmap?.Dispose();
            AllocateResources();
        }

        /// <summary>释放资源。</summary>
        public void Dispose()
        {
            _rootSignature?.Dispose();
            _clusterBuildPipeline?.Dispose();
            _lightCullingPipeline?.Dispose();
            _lightingPipeline?.Dispose();
            _lightBuffer?.Dispose();
            _clusterLightDataBuffer?.Dispose();
            _lightIndexBuffer?.Dispose();
            _lightCounterBuffer?.Dispose();
            _dispatchArgsBuffer?.Dispose();
            _debugHeatmap?.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
