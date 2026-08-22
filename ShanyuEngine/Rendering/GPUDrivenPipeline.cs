using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using ShanyuEngine.RHI;
using ShanyuEngine.Shaders;

namespace ShanyuEngine.Rendering
{
    // ════════════════════════════════════════════════════════════════════════
    // 山雨 GPU 驱动渲染管线 (核心调度器)
    //
    // 统一编排 MicroCluster 虚拟几何体 + ProbeField GI + 虚拟阴影的全 GPU 驱动渲染流程。
    // CPU 端零回读: 所有剔除、LOD、光栅化决策均在 GPU 完成, CPU 仅提交间接命令。
    //
    // 管线阶段:
    //   1. 实例剔除 Pass (Compute): 视锥/HZB 遮挡剔除 -> 可见实例列表
    //   2. 集群剔除 Pass (Compute): 每实例展开簇, 簇级视锥/法线锥/HZB/LOD -> 可见簇列表
    //   3. Draw Call 合并 Pass (Compute): 相同材质簇合并为 MegaDraw, 生成间接参数
    //   4. 可见性缓冲生成 Pass: Mesh Shader 硬光栅 (大三角形) + 软件光栅 (子像素)
    //   5. 可见性缓冲解码 Pass: 解码簇/三角形/重心 -> 插值属性 -> GeometryBuffer
    //   6. ProbeField GI 更新 + 间接光汇聚
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>GPU 驱动管线配置。</summary>
    public sealed class GPUDrivenPipelineSettings
    {
        /// <summary>最大实例数。</summary>
        public int MaxInstances = 65536;
        /// <summary>最大可见实例数。</summary>
        public int MaxVisibleInstances = 32768;
        /// <summary>最大可见簇数。</summary>
        public int MaxVisibleClusters = 262144;
        /// <summary>启用软件光栅化 (子像素三角形)。</summary>
        public bool EnableSoftwareRaster = true;
        /// <summary>启用 Mesh Shader 硬件光栅。</summary>
        public bool EnableMeshShader = true;
        /// <summary>启用 HZB 遮挡剔除。</summary>
        public bool EnableHZBOcclusion = true;
        /// <summary>启用 Draw Call 合并 (MegaDraw)。</summary>
        public bool EnableDrawCallMerging = true;
        /// <summary>屏幕空间误差阈值。</summary>
        public float ErrorThreshold = MicroClusterConstants.DefaultErrorThreshold;
    }

    /// <summary>实例剔除常量缓冲。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct InstanceCullCB
    {
        public Matrix4x4 ViewProjMatrix;
        public Matrix4x4 PrevViewProjMatrix;
        public Vector3 CameraPos;
        public uint InstanceCount;
        public Vector2 InvResolution;
        public float LodDistanceScale;
        public uint FrameIndex;
        public uint Pad0;
        public uint Pad1;
        public uint Pad2;
    }

    /// <summary>集群剔除常量缓冲。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ClusterCullCB
    {
        public Matrix4x4 ViewProjMatrix;
        public Vector3 CameraPos;
        public float ErrorThreshold;
        public Vector2 InvResolution;
        public uint TotalClusters;
        public uint VisibleInstanceCount;
        public float LodDistanceScale;
        public uint FrameIndex;
        public uint Pad0;
        public uint Pad1;
    }

    /// <summary>Draw Call 合并常量缓冲。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct DrawMergeCB
    {
        public uint VisibleClusterCount;
        public uint MaxDrawCalls;
        public uint Pad0;
        public uint Pad1;
    }

    /// <summary>可见性缓冲生成常量缓冲。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct VisibilityGenCB
    {
        public Matrix4x4 ViewProjMatrix;
        public Vector2 InvResolution;
        public uint VisibleClusterCount;
        public uint FrameIndex;
        public uint Pad0;
        public uint Pad1;
        public uint Pad2;
        public uint Pad3;
    }

    /// <summary>MegaDraw 记录: 合并相同材质的簇。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MegaDrawRecord
    {
        /// <summary>材质索引。</summary>
        public uint MaterialIndex;
        /// <summary>合并的簇数。</summary>
        public uint ClusterCount;
        /// <summary>簇列表起始偏移。</summary>
        public uint ClusterListStart;
        /// <summary>间接参数偏移。</summary>
        public uint IndirectArgsOffset;
    }

    /// <summary>GPU 驱动管线统计信息。</summary>
    public sealed class GPUDrivenPipelineStats
    {
        /// <summary>总实例数。</summary>
        public uint TotalInstances;
        /// <summary>可见实例数。</summary>
        public uint VisibleInstances;
        /// <summary>总簇数。</summary>
        public uint TotalClusters;
        /// <summary>可见簇数。</summary>
        public uint VisibleClusters;
        /// <summary>软件光栅簇数。</summary>
        public uint SoftwareRasterClusters;
        /// <summary>硬件光栅簇数。</summary>
        public uint HardwareRasterClusters;
        /// <summary>合并后的 Draw Call 数。</summary>
        public uint MergedDrawCalls;
        /// <summary>管线耗时 (毫秒)。</summary>
        public float PipelineTimeMs;
    }

    /// <summary>
    /// GPU 驱动渲染管线: 核心调度器。
    /// 编排实例剔除 -> 集群剔除 -> Draw 合并 -> 可见性缓冲生成 -> 属性解码的完整流程。
    /// </summary>
    public sealed class GPUDrivenPipeline : IDisposable
    {
        private readonly RHIDevice _device;
        private readonly GPUDrivenPipelineSettings _settings;
        private readonly GPUScene _gpuScene;
        private readonly SoftwareRasterizer _softwareRasterizer;
        private readonly MeshShaderPipeline _meshShaderPipeline;
        private readonly HybridRasterStrategy _rasterStrategy;

        private RHIPipelineState? _instanceCullPipeline;
        private RHIPipelineState? _clusterCullPipeline;
        private RHIPipelineState? _drawMergePipeline;
        private RHIPipelineState? _visibilityDecodePipeline;
        private RHIRootSignature? _rootSignature;

        private RHIBuffer? _megaDrawBuffer;
        private RHIBuffer? _mergedClusterListBuffer;

        private readonly GPUDrivenPipelineStats _stats = new();

        /// <summary>GPU Scene。</summary>
        public GPUScene GPUScene => _gpuScene;
        /// <summary>软件光栅化器。</summary>
        public SoftwareRasterizer SoftwareRasterizer => _softwareRasterizer;
        /// <summary>Mesh Shader 管线。</summary>
        public MeshShaderPipeline MeshShaderPipeline => _meshShaderPipeline;
        /// <summary>混合光栅化策略。</summary>
        public HybridRasterStrategy RasterStrategy => _rasterStrategy;
        /// <summary>统计信息。</summary>
        public GPUDrivenPipelineStats Stats => _stats;
        /// <summary>设置。</summary>
        public GPUDrivenPipelineSettings Settings => _settings;
        /// <summary>MegaDraw 缓冲。</summary>
        public RHIBuffer? MegaDrawBuffer => _megaDrawBuffer;

        /// <summary>构造 GPU 驱动管线。</summary>
        public GPUDrivenPipeline(RHIDevice device, GPUDrivenPipelineSettings? settings = null)
        {
            _device = device;
            _settings = settings ?? new GPUDrivenPipelineSettings();
            _gpuScene = new GPUScene(device, _settings.MaxInstances, _settings.MaxVisibleInstances, _settings.MaxVisibleClusters);
            _softwareRasterizer = new SoftwareRasterizer(device);
            _meshShaderPipeline = new MeshShaderPipeline(device);
            _rasterStrategy = new HybridRasterStrategy(_meshShaderPipeline, _softwareRasterizer);
        }

        /// <summary>初始化所有子系统。</summary>
        public void Initialize()
        {
            _gpuScene.Initialize();
            _softwareRasterizer.Initialize();

            bool meshShaderOk = _settings.EnableMeshShader ? _meshShaderPipeline.Initialize() : false;
            if (!meshShaderOk && _settings.EnableMeshShader)
            {
                // 硬件不支持 Mesh Shader, 强制启用软件光栅化回退
                _settings.EnableSoftwareRaster = true;
            }

            CreateRootSignature();
            CreatePipelines();
            AllocateMergeBuffers();
        }

        /// <summary>创建根签名。</summary>
        private void CreateRootSignature()
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
                StaticSamplers = new[] { RHISamplerDesc.PointClamp, RHISamplerDesc.LinearWrap }
            };
            _rootSignature = _device.CreateRootSignature(rootDesc, "GPUDrivenRootSig");
        }

        /// <summary>创建所有 Compute 管线。</summary>
        private void CreatePipelines()
        {
            _instanceCullPipeline = _device.CreateComputePipelineState(new RHIComputePipelineDesc
            {
                Name = "InstanceCull",
                CS = ShaderManager.Instance.GetBytecode("MicroCluster/MicroClusterCull", "InstanceCullCS", RHIShaderStage.Compute),
                RootSignature = _rootSignature
            });

            _clusterCullPipeline = _device.CreateComputePipelineState(new RHIComputePipelineDesc
            {
                Name = "ClusterCull",
                CS = ShaderManager.Instance.GetBytecode("MicroCluster/MicroClusterCull", "ClusterCullCS", RHIShaderStage.Compute),
                RootSignature = _rootSignature
            });

            _drawMergePipeline = _device.CreateComputePipelineState(new RHIComputePipelineDesc
            {
                Name = "DrawMerge",
                CS = ShaderManager.Instance.GetBytecode("MicroCluster/MicroClusterCull", "DrawMergeCS", RHIShaderStage.Compute),
                RootSignature = _rootSignature
            });

            _visibilityDecodePipeline = _device.CreateComputePipelineState(new RHIComputePipelineDesc
            {
                Name = "VisibilityDecode",
                CS = ShaderManager.Instance.GetBytecode("MicroCluster/MicroClusterVisibilityBuffer", "VisibilityDecodeCS", RHIShaderStage.Compute),
                RootSignature = _rootSignature
            });
        }

        /// <summary>分配 Draw Call 合并所需缓冲。</summary>
        private void AllocateMergeBuffers()
        {
            _megaDrawBuffer = _device.CreateBuffer(new RHIBufferDesc
            {
                SizeInBytes = (ulong)(1024 * Marshal.SizeOf<MegaDrawRecord>()),
                Flags = RHIBufferFlags.Structured | RHIBufferFlags.UnorderedAccess | RHIBufferFlags.ShaderResource,
                Stride = (ulong)Marshal.SizeOf<MegaDrawRecord>(),
                HeapType = RHIResourceHeapType.Default
            }, "GPUDriven_MegaDraw");

            _mergedClusterListBuffer = _device.CreateBuffer(new RHIBufferDesc
            {
                SizeInBytes = (ulong)(_settings.MaxVisibleClusters * 8),
                Flags = RHIBufferFlags.Structured | RHIBufferFlags.UnorderedAccess | RHIBufferFlags.ShaderResource,
                Stride = 8,
                HeapType = RHIResourceHeapType.Default
            }, "GPUDriven_MergedClusterList");
        }

        /// <summary>根据屏幕尺寸重建资源。</summary>
        public void Resize(int width, int height)
        {
            _softwareRasterizer.Resize(width, height);
            _gpuScene.ResizeHZB(width, height);
        }

        // ════════════════════════════════════════════════════════════════════
        // 阶段 1: GPU 驱动实例剔除
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// 实例剔除 Pass: Compute Shader 执行实例级剔除 (视锥/HZB 遮挡)。
        /// 实例数据存储在 StructuredBuffer, 输出可见实例列表。
        /// </summary>
        public void CullInstances(RHICommandList cmdList, in Matrix4x4 viewProj, in Matrix4x4 prevViewProj,
            Vector3 cameraPos, int width, int height)
        {
            if (_instanceCullPipeline == null || _rootSignature == null) return;

            // 上传脏实例
            _gpuScene.UploadDirtyInstances(cmdList);
            _gpuScene.ResetCullCounters(cmdList);

            cmdList.SetComputeRootSignature(_rootSignature);
            cmdList.SetPipelineState(_instanceCullPipeline);

            var cb = new InstanceCullCB
            {
                ViewProjMatrix = viewProj,
                PrevViewProjMatrix = prevViewProj,
                CameraPos = cameraPos,
                InstanceCount = _gpuScene.ActiveInstanceCount,
                InvResolution = new Vector2(1f / width, 1f / height),
                LodDistanceScale = 1.0f,
                FrameIndex = (uint)Environment.TickCount
            };

            // 实例缓冲 -> SRV, 可见实例缓冲 -> UAV
            cmdList.TransitionTo(_gpuScene.InstanceBuffer!, RHIResourceState.NonPixelShaderResource);
            cmdList.TransitionTo(_gpuScene.VisibleInstanceBuffer!, RHIResourceState.UnorderedAccess);
            if (_settings.EnableHZBOcclusion && _gpuScene.HZBTexture != null)
                cmdList.TransitionTo(_gpuScene.HZBTexture, RHIResourceState.NonPixelShaderResource);

            int groups = (int)((cb.InstanceCount + 63) / 64);
            cmdList.Dispatch(Math.Max(1, groups), 1, 1);

            cmdList.TransitionTo(_gpuScene.VisibleInstanceBuffer!, RHIResourceState.NonPixelShaderResource);

            _stats.TotalInstances = cb.InstanceCount;
        }

        // ════════════════════════════════════════════════════════════════════
        // 阶段 2: 集群级剔除
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// 集群剔除 Pass: 将每个可见实例展开为簇, 对每簇执行视锥/法线锥/HZB/LOD 剔除。
        /// 输出可见簇列表与软件/硬件光栅分类。
        /// </summary>
        public void CullClusters(RHICommandList cmdList, in Matrix4x4 viewProj, Vector3 cameraPos, int width, int height)
        {
            if (_clusterCullPipeline == null || _rootSignature == null) return;

            cmdList.SetComputeRootSignature(_rootSignature);
            cmdList.SetPipelineState(_clusterCullPipeline);

            var cb = new ClusterCullCB
            {
                ViewProjMatrix = viewProj,
                CameraPos = cameraPos,
                ErrorThreshold = _settings.ErrorThreshold,
                InvResolution = new Vector2(1f / width, 1f / height),
                TotalClusters = 0, // 由 GPU 从实例缓冲计算
                VisibleInstanceCount = _stats.TotalInstances,
                LodDistanceScale = 1.0f,
                FrameIndex = (uint)Environment.TickCount
            };

            cmdList.TransitionTo(_gpuScene.VisibleClusterBuffer!, RHIResourceState.UnorderedAccess);
            cmdList.TransitionTo(_gpuScene.MeshShaderIndirectArgs!, RHIResourceState.UnorderedAccess);

            // 每可见实例分配线程组进行簇展开与剔除
            int groups = (int)((cb.VisibleInstanceCount + 31) / 32);
            cmdList.Dispatch(Math.Max(1, groups), 1, 1);

            cmdList.TransitionTo(_gpuScene.VisibleClusterBuffer!, RHIResourceState.NonPixelShaderResource);
            cmdList.TransitionTo(_gpuScene.MeshShaderIndirectArgs!, RHIResourceState.IndirectArgument);
        }

        // ════════════════════════════════════════════════════════════════════
        // 阶段 3: Draw Call 合并
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Draw Call 合并 Pass: 将相同材质的簇合并为 MegaDraw, 管理间接参数缓冲。
        /// 减少 Draw Call 数量, 提升驱动开销效率。
        /// </summary>
        public void MergeDrawCalls(RHICommandList cmdList)
        {
            if (!_settings.EnableDrawCallMerging || _drawMergePipeline == null || _rootSignature == null) return;

            cmdList.SetComputeRootSignature(_rootSignature);
            cmdList.SetPipelineState(_drawMergePipeline);

            var cb = new DrawMergeCB
            {
                VisibleClusterCount = _stats.VisibleClusters,
                MaxDrawCalls = 1024
            };

            cmdList.TransitionTo(_megaDrawBuffer!, RHIResourceState.UnorderedAccess);
            cmdList.TransitionTo(_mergedClusterListBuffer!, RHIResourceState.UnorderedAccess);

            int groups = (int)((cb.VisibleClusterCount + 63) / 64);
            cmdList.Dispatch(Math.Max(1, groups), 1, 1);

            cmdList.TransitionTo(_megaDrawBuffer!, RHIResourceState.NonPixelShaderResource);
            cmdList.TransitionTo(_mergedClusterListBuffer!, RHIResourceState.NonPixelShaderResource);
        }

        // ════════════════════════════════════════════════════════════════════
        // 阶段 4: 可见性缓冲生成
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// 可见性缓冲生成 Pass。
        /// 大三角形走 Mesh Shader 硬件光栅, 子像素三角形走软件光栅化。
        /// 输出可见性缓冲 (R32G32_UINT: instanceId/triangleId/barycentric)。
        /// </summary>
        /// <param name="cmdList">命令列表。</param>
        /// <param name="visibilityTexture">可见性缓冲纹理 (R32G32_UINT)。</param>
        /// <param name="depthTexture">深度纹理 (D32_Float)。</param>
        /// <param name="viewProj">视图投影矩阵。</param>
        /// <param name="width">渲染宽度。</param>
        /// <param name="height">渲染高度。</param>
        public void GenerateVisibilityBuffer(RHICommandList cmdList, RHITexture visibilityTexture,
            RHITexture depthTexture, in Matrix4x4 viewProj, int width, int height)
        {
            var rtv = _device.CreateRTV(visibilityTexture);
            var dsv = _device.CreateDSV(depthTexture);

            // --- 硬件光栅化路径 (Mesh Shader, 大三角形) ---
            if (_settings.EnableMeshShader && _meshShaderPipeline.IsSupported && _gpuScene.MeshShaderIndirectArgs != null)
            {
                var cb = new MeshShaderCB
                {
                    ViewProjMatrix = viewProj,
                    InvResolution = new Vector2(1f / width, 1f / height),
                    VisibleClusterCount = _stats.HardwareRasterClusters,
                    FrameIndex = (uint)Environment.TickCount
                };
                _meshShaderPipeline.Execute(cmdList, _gpuScene.VisibleClusterBuffer!,
                    _gpuScene.MeshShaderIndirectArgs!, rtv, dsv, cb);
            }

            // --- 软件光栅化路径 (Compute, 子像素三角形) ---
            if (_settings.EnableSoftwareRaster && _softwareRasterizer.IsInitialized)
            {
                var swCB = new SoftwareRasterCB
                {
                    ViewProjMatrix = viewProj,
                    InvResolution = new Vector2(1f / width, 1f / height),
                    VisibleClusterCount = _stats.SoftwareRasterClusters,
                    TileCountX = (uint)((width + SoftwareRasterConstants.TileSize - 1) / SoftwareRasterConstants.TileSize),
                    TileCountY = (uint)((height + SoftwareRasterConstants.TileSize - 1) / SoftwareRasterConstants.TileSize),
                    FrameIndex = (uint)Environment.TickCount
                };

                // Bin 分配 -> Tile 光栅化
                _softwareRasterizer.BinAssignPass(cmdList, _gpuScene.VisibleClusterBuffer!, swCB);
                _softwareRasterizer.RasterizePass(cmdList, visibilityTexture, depthTexture, _gpuScene.HZBTexture);
            }
        }

        // ════════════════════════════════════════════════════════════════════
        // 阶段 5: 可见性缓冲解码
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// 可见性缓冲解码 Pass: 从可见性缓冲读取可见三角形,
        /// 插值顶点属性 (Position/Normal/UV/Tangent), 输出 GeometryBuffer。
        /// </summary>
        /// <param name="cmdList">命令列表。</param>
        /// <param name="visibilityTexture">可见性缓冲纹理。</param>
        /// <param name="geometrybufferTargets">GeometryBuffer 输出纹理数组。</param>
        /// <param name="invViewProj">逆视图投影矩阵。</param>
        /// <param name="cameraPos">相机位置。</param>
        /// <param name="width">宽度。</param>
        /// <param name="height">高度。</param>
        public void DecodeVisibilityBuffer(RHICommandList cmdList, RHITexture visibilityTexture,
            RHITexture[] geometrybufferTargets, in Matrix4x4 invViewProj, Vector3 cameraPos, int width, int height)
        {
            if (_visibilityDecodePipeline == null || _rootSignature == null) return;

            // 软件光栅化器自带解码 Pass
            var cb = new AttributeDecodeCB
            {
                InvViewProjMatrix = invViewProj,
                CameraPos = cameraPos,
                InvResolution = new Vector2(1f / width, 1f / height),
                MaterialCount = 256
            };
            _softwareRasterizer.DecodePass(cmdList, visibilityTexture, geometrybufferTargets, cb);
        }

        // ════════════════════════════════════════════════════════════════════
        // 完整帧流程
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// 执行完整的 GPU 驱动渲染帧。
        /// 编排: 实例剔除 -> 集群剔除 -> Draw 合并 -> HZB 生成 -> 可见性缓冲生成 -> 解码。
        /// </summary>
        /// <param name="cmdList">命令列表。</param>
        /// <param name="view">视图矩阵。</param>
        /// <param name="proj">投影矩阵。</param>
        /// <param name="prevViewProj">上一帧视图投影。</param>
        /// <param name="cameraPos">相机位置。</param>
        /// <param name="depthTexture">深度纹理 (用于 HZB)。</param>
        /// <param name="visibilityTexture">可见性缓冲纹理。</param>
        /// <param name="geometrybufferTargets">GeometryBuffer 输出纹理。</param>
        /// <param name="width">渲染宽度。</param>
        /// <param name="height">渲染高度。</param>
        public void RenderFrame(RHICommandList cmdList, in Matrix4x4 view, in Matrix4x4 proj,
            in Matrix4x4 prevViewProj, Vector3 cameraPos,
            RHITexture depthTexture, RHITexture visibilityTexture, RHITexture[] geometrybufferTargets,
            int width, int height)
        {
            var viewProj = view * proj;

            // 1. 实例剔除
            CullInstances(cmdList, viewProj, prevViewProj, cameraPos, width, height);

            // 2. 集群剔除
            CullClusters(cmdList, viewProj, cameraPos, width, height);

            // 3. Draw Call 合并
            MergeDrawCalls(cmdList);

            // 4. HZB 生成 (供遮挡剔除使用, 双缓冲: 本帧用上一帧 HZB)
            if (_settings.EnableHZBOcclusion)
                _gpuScene.BuildHZB(cmdList, depthTexture);

            // 5. 可见性缓冲生成 (硬件 + 软件光栅化)
            GenerateVisibilityBuffer(cmdList, visibilityTexture, depthTexture, viewProj, width, height);

            // 6. 可见性缓冲解码 -> GeometryBuffer
            Matrix4x4.Invert(viewProj, out var invViewProj);
            DecodeVisibilityBuffer(cmdList, visibilityTexture, geometrybufferTargets, invViewProj, cameraPos, width, height);
        }

        /// <summary>释放所有资源。</summary>
        public void Dispose()
        {
            _gpuScene.Dispose();
            _softwareRasterizer.Dispose();
            _meshShaderPipeline.Dispose();
            _instanceCullPipeline?.Dispose();
            _clusterCullPipeline?.Dispose();
            _drawMergePipeline?.Dispose();
            _visibilityDecodePipeline?.Dispose();
            _rootSignature?.Dispose();
            _megaDrawBuffer?.Dispose();
            _mergedClusterListBuffer?.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
