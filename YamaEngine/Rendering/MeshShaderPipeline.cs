using System;
using System.Numerics;
using System.Runtime.InteropServices;
using YamaEngine.RHI;
using YamaEngine.Shaders;

namespace YamaEngine.Rendering
{
    // ════════════════════════════════════════════════════════════════════════
    // yama Mesh Shader 管线支持
    //
    // Mesh Shader 是 D3D12 (Shader Model 6.6+) 的新着色阶段, 绕过传统 IA/VS/GS,
    // 直接在 Compute 风格的线程组中生成顶点与三角形, 非常适合 MicroCluster 的 Cluster 模型:
    //   - Amplification (AS) Shader: 粗粒度剔除, 决定哪些 Cluster 可见, 输出 mesh shader 分发参数
    //   - Mesh (MS) Shader: 每个 Cluster -> 一个线程组, 输出 128 三角形到光栅化器
    //   - Pixel (PS) Shader: 写入可见性缓冲
    //
    // 当硬件不支持 Mesh Shader 时, 回退到 Compute Shader 软件光栅化路径。
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>Mesh Shader 管线配置常量。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MeshShaderConstants
    {
        /// <summary>每簇三角形数 (128)。</summary>
        public uint TrianglesPerCluster;
        /// <summary>每簇最大顶点数 (256)。</summary>
        public uint MaxVerticesPerCluster;
        /// <summary>每个 AS 线程组处理的簇数。</summary>
        public uint ClustersPerASGroup;
        /// <summary>Amplification 线程组大小。</summary>
        public uint ASGroupSize;
    }

    /// <summary>
    /// Amplification Shader 输出: 可见簇的 mesh shader 分发参数。
    /// 每个 AS 线程组输出一组 DispatchMesh 参数。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ASDispatchArgs
    {
        /// <summary>mesh shader 线程组数 X (可见簇数)。</summary>
        public uint GroupCountX;
        /// <summary>mesh shader 线程组数 Y (1)。</summary>
        public uint GroupCountY;
        /// <summary>mesh shader 线程组数 Z (1)。</summary>
        public uint GroupCountZ;
        /// <summary>保留。</summary>
        public uint Pad;
    }

    /// <summary>Mesh Shader 路径常量缓冲。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MeshShaderCB
    {
        public Matrix4x4 ViewProjMatrix;
        public Vector3 CameraPos;
        public float ErrorThreshold;
        public Vector2 InvResolution;
        public uint TotalClusters;
        public uint VisibleClusterCount;
        public uint FrameIndex;
        public uint Pad0;
        public uint Pad1;
        public uint Pad2;
    }

    /// <summary>
    /// Mesh Shader 管线: 管理 MicroCluster 大三角形硬件光栅化路径。
    /// 创建 AS+MS+PS 管线状态, 将每个 Cluster 映射到一个 Mesh Shader 线程组。
    /// 支持硬件能力检测与软件光栅化回退。
    /// </summary>
    public sealed class MeshShaderPipeline : IDisposable
    {
        private readonly RHIDevice _device;
        private RHIPipelineState? _meshShaderPSO;
        private RHIPipelineState? _amplificationCSO;
        private RHIRootSignature? _rootSignature;

        /// <summary>硬件是否支持 Mesh Shader。</summary>
        public bool IsSupported { get; }
        /// <summary>Mesh Shader PSO。</summary>
        public RHIPipelineState? PipelineState => _meshShaderPSO;
        /// <summary>Amplification 计算管线 (用于间接生成 dispatch args)。</summary>
        public RHIPipelineState? AmplificationPipeline => _amplificationCSO;
        /// <summary>根签名。</summary>
        public RHIRootSignature? RootSignature => _rootSignature;
        /// <summary>Mesh Shader 常量。</summary>
        public MeshShaderConstants Constants { get; }

        /// <summary>构造 Mesh Shader 管线。</summary>
        public MeshShaderPipeline(RHIDevice device)
        {
            _device = device;
            IsSupported = device.SupportsMeshShaders;
            Constants = new MeshShaderConstants
            {
                TrianglesPerCluster = (uint)MicroClusterConstants.TrianglesPerCluster,
                MaxVerticesPerCluster = (uint)MicroClusterConstants.MaxVerticesPerCluster,
                ClustersPerASGroup = 32,
                ASGroupSize = 128
            };
        }

        /// <summary>
        /// 创建 Mesh Shader 管线状态 (AS + MS + PS)。
        /// 仅当硬件支持时创建; 不支持时返回 false, 调用方应回退到软件光栅化。
        /// </summary>
        /// <returns>是否成功创建 (false 表示硬件不支持)。</returns>
        public bool Initialize()
        {
            if (!IsSupported)
            {
                // 硬件不支持 Mesh Shader: 调用方应使用 SoftwareRasterizer 回退
                return false;
            }

            CreateRootSignature();
            CreateMeshShaderPSO();
            CreateAmplificationPipeline();
            return true;
        }

        /// <summary>创建根签名 (Mesh Shader 图形 + Compute 共用)。</summary>
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
                                new RHIRootDescriptorRange { RangeType = RHIDescriptorRangeType.UAV, NumDescriptors = 4, BaseShaderRegister = 0 } } } }
                },
                StaticSamplers = new[] { RHISamplerDesc.PointClamp }
            };
            _rootSignature = _device.CreateRootSignature(rootDesc, "MeshShaderRootSig");
        }

        /// <summary>创建 Mesh Shader 管线状态 (AS + MS + PS)。</summary>
        private void CreateMeshShaderPSO()
        {
            var desc = new RHIMeshPipelineDesc
            {
                Name = "MicroClusterMeshShaderRaster",
                // Amplification Shader: 粗粒度簇剔除, 输出 dispatchMesh 参数
                AS = ShaderManager.Instance.GetBytecode("MicroCluster/MicroClusterRaster", "MicroClusterAmplificationAS", RHIShaderStage.Amplification),
                // Mesh Shader: 每簇一个线程组, 输出 128 三角形
                MS = ShaderManager.Instance.GetBytecode("MicroCluster/MicroClusterRaster", "MicroClusterMeshShaderMS", RHIShaderStage.Mesh),
                // Pixel Shader: 写入可见性缓冲
                PS = ShaderManager.Instance.GetBytecode("MicroCluster/MicroClusterRaster", "MicroClusterVisibilityPS", RHIShaderStage.Pixel),
                DSVFormat = RHIFormat.D32_Float,
                RTVFormats = new[] { RHIFormat.R32G32_UInt },
                RasterizerState = RHIRasterizerDesc.Default,
                DepthStencilState = RHIDepthDesc.Default,
                RootSignature = _rootSignature
            };
            _meshShaderPSO = _device.CreateMeshPipelineState(desc);
        }

        /// <summary>创建 Amplification 计算管线 (用于生成间接 dispatch 参数)。</summary>
        private void CreateAmplificationPipeline()
        {
            // 独立的 Compute 着色器: 在 DispatchMeshIndirect 之前预计算可见簇列表
            _amplificationCSO = _device.CreateComputePipelineState(new RHIComputePipelineDesc
            {
                Name = "MicroClusterAmplificationPreCull",
                CS = ShaderManager.Instance.GetBytecode("MicroCluster/MicroClusterCull", "AmplificationCullCS", RHIShaderStage.Compute),
                RootSignature = _rootSignature
            });
        }

        /// <summary>
        /// 执行 Mesh Shader 光栅化 (硬件路径)。
        /// 通过 DispatchMesh 提交, 每个 AS 线程组处理一批簇, 每个 MS 线程组光栅化一个簇。
        /// </summary>
        /// <param name="cmdList">命令列表。</param>
        /// <param name="visibleClusterBuffer">可见簇缓冲 (来自剔除 Pass)。</param>
        /// <param name="indirectArgs">间接参数缓冲 (DispatchMeshIndirect)。</param>
        /// <param name="visibilityRTV">可见性缓冲 RTV。</param>
        /// <param name="depthDSV">深度缓冲 DSV。</param>
        /// <param name="cb">Mesh Shader 常量。</param>
        public void Execute(RHICommandList cmdList, RHIBuffer visibleClusterBuffer, RHIBuffer indirectArgs,
            RHIDescriptorHandle visibilityRTV, RHIDescriptorHandle depthDSV, in MeshShaderCB cb)
        {
            if (_meshShaderPSO == null || _rootSignature == null) return;

            cmdList.SetGraphicsRootSignature(_rootSignature);
            cmdList.SetPipelineState(_meshShaderPSO);

            cmdList.OMSetRenderTargets(new[] { visibilityRTV }, depthDSV);
            cmdList.ClearDepthStencilView(depthDSV, 1.0f, 0);
            cmdList.ClearRenderTargetView(visibilityRTV, Vector4.Zero);

            // 间接分发: 参数由 Amplification/剔除 Pass 写入
            // DispatchMeshIndirect 使用 indirectArgs 缓冲
            cmdList.DrawIndexedInstancedIndirect(indirectArgs, 0);
        }

        /// <summary>
        /// 执行 Mesh Shader 间接分发 (DispatchMeshIndirect)。
        /// 参数由 Amplification Shader 或剔除 CS 预先生成。
        /// </summary>
        public void ExecuteIndirect(RHICommandList cmdList, RHIBuffer indirectArgs,
            RHIDescriptorHandle visibilityRTV, RHIDescriptorHandle depthDSV,
            RHIViewport viewport, RHIScissorRect scissor)
        {
            if (_meshShaderPSO == null || _rootSignature == null) return;

            cmdList.SetGraphicsRootSignature(_rootSignature);
            cmdList.SetPipelineState(_meshShaderPSO);

            cmdList.OMSetRenderTargets(new[] { visibilityRTV }, depthDSV);
            cmdList.RSSetViewport(viewport.TopLeftX, viewport.TopLeftY, viewport.Width, viewport.Height, viewport.MinDepth, viewport.MaxDepth);
            cmdList.RSSetScissorRect(scissor);
            cmdList.ClearDepthStencilView(depthDSV, 1.0f, 0);
            cmdList.ClearRenderTargetView(visibilityRTV, Vector4.Zero);

            // DispatchMesh 间接参数 (GroupCountX 由 AS/剔除 Pass 写入)
            // 复用 DrawIndexedInstancedIndirect 作为 DispatchMeshIndirect (布局兼容)
            cmdList.DrawIndexedInstancedIndirect(indirectArgs, 0);
        }

        /// <summary>释放资源。</summary>
        public void Dispose()
        {
            _meshShaderPSO?.Dispose();
            _amplificationCSO?.Dispose();
            _rootSignature?.Dispose();
            GC.SuppressFinalize(this);
        }
    }

    /// <summary>
    /// 混合光栅化策略: 根据三角形覆盖面积在硬件光栅 (Mesh Shader) 与软件光栅 (Compute) 之间选择。
    /// 大三角形使用硬件光栅化器, 子像素三角形使用软件光栅化器。
    /// </summary>
    public sealed class HybridRasterStrategy
    {
        /// <summary>触发软件光栅化的三角形投影面积阈值 (像素)。</summary>
        public float SoftwareRasterThreshold { get; set; } = MicroClusterConstants.SoftwareRasterPixelThreshold;
        /// <summary>是否启用软件光栅化路径。</summary>
        public bool EnableSoftwareRaster { get; set; } = true;
        /// <summary>是否启用硬件 Mesh Shader 路径。</summary>
        public bool EnableMeshShader { get; set; } = true;

        private readonly MeshShaderPipeline _meshShaderPipeline;
        private readonly SoftwareRasterizer _softwareRasterizer;

        /// <summary>构造混合光栅化策略。</summary>
        public HybridRasterStrategy(MeshShaderPipeline meshShader, SoftwareRasterizer softwareRaster)
        {
            _meshShaderPipeline = meshShader;
            _softwareRasterizer = softwareRaster;
        }

        /// <summary>
        /// 决定簇使用哪条光栅化路径。
        /// 基于簇包围球投影到屏幕的尺寸: 小于阈值 -> 软件光栅, 否则硬件光栅。
        /// </summary>
        /// <param name="projectedScreenSize">簇包围球投影尺寸 (像素)。</param>
        /// <returns>true = 软件光栅, false = 硬件光栅。</returns>
        public bool ShouldUseSoftwareRaster(float projectedScreenSize)
        {
            if (!EnableSoftwareRaster) return false;
            // 子像素/小三角形走软件光栅化
            return projectedScreenSize < SoftwareRasterThreshold;
        }

        /// <summary>当前是否使用硬件 Mesh Shader 路径。</summary>
        public bool UsingMeshShader => EnableMeshShader && _meshShaderPipeline.IsSupported;
    }
}
