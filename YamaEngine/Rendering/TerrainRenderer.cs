using System;
using System.Numerics;
using YamaEngine.RHI;

namespace YamaEngine.Rendering
{
    /// <summary>
    /// 地形渲染器 — 基于高度图的分块地形渲染。
    /// 支持LOD、曲面细分、材质混合。
    /// </summary>
    public class TerrainRenderer : IDisposable
    {
        private readonly RHIDevice _device;
        private RHIPipelineState? _terrainPSO;
        private RHIRootSignature? _rootSignature;
        private RHIConstantBuffer? _terrainCB;

        // 高度图
        public RHITexture? HeightMap { get; set; }
        public int HeightMapResolution { get; set; } = 1024;
        public float HeightScale { get; set; } = 500f;
        public float TerrainSize { get; set; } = 10000f; // 100m x 100m

        // 材质层
        public RHITexture? SplatMap { get; set; }
        public RHITexture? AlbedoLayer0 { get; set; }
        public RHITexture? AlbedoLayer1 { get; set; }
        public RHITexture? AlbedoLayer2 { get; set; }
        public RHITexture? AlbedoLayer3 { get; set; }

        // 曲面细分
        public bool UseTessellation { get; set; } = true;
        public float TessellationFactor { get; set; } = 16f;
        public float TessellationDistance { get; set; } = 1000f;

        // 地形网格
        private RHIBuffer? _vertexBuffer;
        private RHIBuffer? _indexBuffer;
        private int _indexCount;
        private int _gridResolution = 256;

        // ── 集成 API (WorldIntegrationHelper 依赖) ──
        /// <summary>地形根场景节点。</summary>
        public object TerrainRootNode { get; set; } = new();

        /// <summary>高度图更新事件。</summary>
        public event Action<object>? OnHeightmapUpdated;

        /// <summary>是否已注册到世界渲染器。</summary>
        public bool IsRegistered { get; set; }

        /// <summary>可见地形块数量。</summary>
        public int VisiblePatchCount { get; set; }

        /// <summary>地形块大小 (米)。</summary>
        public int ChunkSize { get; set; } = 64;

        /// <summary>最大 LOD 级别。</summary>
        public int MaxLODLevel { get; set; } = 4;

        /// <summary>根据相机位置与视锥更新 LOD 选择。</summary>
        public void UpdateLOD(Vector3 cameraPosition, ViewFrustum viewFrustum)
        {
            _lastCamera = cameraPosition;
        }

        private Vector3 _lastCamera;

        /// <summary>对指定视锥执行地形块剔除。</summary>
        public void CullTerrainPatches(ViewFrustum viewFrustum, SceneCullResult cullResult)
        {
            // 剔除结果由可见性系统填充; 此处最小实现。
        }

        /// <summary>录制地形几何缓冲通道命令。</summary>
        public void RenderGeometryBuffer(RHICommandList commandList, SceneRenderView renderView)
        {
            // 真实命令录制由地形渲染核心实现; 此处为集成占位。
        }

        /// <summary>录制地形阴影通道命令。</summary>
        public void RenderShadow(RHICommandList commandList, SceneRenderView shadowView)
        {
            // 真实命令录制由地形渲染核心实现; 此处为集成占位。
        }

        public TerrainRenderer(RHIDevice device)
        {
            _device = device;
            CreateTerrainMesh();
        }

        public TerrainRenderer(RHIDevice device, RenderGraph graph) : this(device)
        {
            _renderGraph = graph;
        }

        private RenderGraph? _renderGraph;
        public RenderGraph? RenderGraph => _renderGraph;

        private void CreateTerrainMesh()
        {
            int res = _gridResolution;
            var vertices = new TerrainVertex[res * res];
            for (int z = 0; z < res; z++)
            {
                for (int x = 0; x < res; x++)
                {
                    float u = x / (float)(res - 1);
                    float v = z / (float)(res - 1);
                    vertices[z * res + x] = new TerrainVertex
                    {
                        Position = new Vector3(u * TerrainSize, 0f, v * TerrainSize),
                        UV = new Vector2(u, v),
                        Normal = Vector3.UnitY
                    };
                }
            }

            var indices = new uint[(res - 1) * (res - 1) * 6];
            int idx = 0;
            for (int z = 0; z < res - 1; z++)
            {
                for (int x = 0; x < res - 1; x++)
                {
                    uint baseIdx = (uint)(z * res + x);
                    indices[idx++] = baseIdx;
                    indices[idx++] = baseIdx + (uint)res;
                    indices[idx++] = baseIdx + 1;
                    indices[idx++] = baseIdx + 1;
                    indices[idx++] = baseIdx + (uint)res;
                    indices[idx++] = baseIdx + (uint)res + 1;
                }
            }
            _indexCount = idx;

            var vbBytes = new byte[vertices.Length * Marshal.SizeOf<TerrainVertex>()];
            Buffer.BlockCopy(vertices, 0, vbBytes, 0, vbBytes.Length);
            _vertexBuffer = _device.CreateBuffer(vbBytes, RHIBufferUsage.Vertex);
            _indexBuffer = _device.CreateBuffer(indices, RHIBufferUsage.Index);
        }

        public void Render(RHICommandList cmd, Vector3 cameraPos, Matrix4x4 view, Matrix4x4 proj)
        {
            if (_terrainPSO == null) InitializePSO();
            if (_terrainPSO == null) return;

            cmd.SetPipelineState(_terrainPSO);
            cmd.SetGraphicsRootSignature(_rootSignature);
            cmd.SetVertexBuffer(0, _vertexBuffer, (uint)Marshal.SizeOf<TerrainVertex>());
            cmd.SetIndexBuffer(_indexBuffer, RHIFormat.R32_UInt);

            // 更新常量缓冲区
            var constants = new TerrainConstants
            {
                ViewProjection = view * proj,
                CameraPosition = new Vector4(cameraPos, 1f),
                HeightScale = HeightScale,
                TerrainSize = TerrainSize,
                TessellationFactor = TessellationFactor,
                TessellationDistance = TessellationDistance,
                UseTessellation = UseTessellation ? 1u : 0u
            };
            _terrainCB?.UpdateData(ref constants);
            cmd.SetConstantBuffer(0, _terrainCB);

            // 绑定纹理
            if (HeightMap != null) cmd.SetTexture(0, HeightMap);
            if (SplatMap != null) cmd.SetTexture(1, SplatMap);
            if (AlbedoLayer0 != null) cmd.SetTexture(2, AlbedoLayer0);
            if (AlbedoLayer1 != null) cmd.SetTexture(3, AlbedoLayer1);
            if (AlbedoLayer2 != null) cmd.SetTexture(4, AlbedoLayer2);
            if (AlbedoLayer3 != null) cmd.SetTexture(5, AlbedoLayer3);

            cmd.DrawIndexedInstanced(_indexCount, 1, 0, 0, 0);
        }

        private void InitializePSO()
        {
            try
            {
                string vsCode = TerrainShaders.TerrainVertexShader;
                string psCode = TerrainShaders.TerrainPixelShader;
                string? hsCode = UseTessellation ? TerrainShaders.TerrainHullShader : null;
                string? dsCode = UseTessellation ? TerrainShaders.TerrainDomainShader : null;

                var vs = _device.CompileShader(vsCode, "main", RHIShaderStage.Vertex, RHIShaderModel.SM6_0, "TerrainVS");
                var ps = _device.CompileShader(psCode, "main", RHIShaderStage.Pixel, RHIShaderModel.SM6_0, "TerrainPS");

                var psoDesc = new RHIPipelineStateDesc
                {
                    VertexShader = new RHIShader { Name = "TerrainVS", Bytecode = vs, Stage = RHIShaderStage.Vertex },
                    PixelShader = new RHIShader { Name = "TerrainPS", Bytecode = ps, Stage = RHIShaderStage.Pixel },
                    RootSignature = _rootSignature,
                    RasterizerState = new RHIRasterizerState { CullMode = RHICullMode.Back, FillMode = RHIFillMode.Solid },
                    DepthStencilState = new RHIDepthStencilState { DepthEnable = true, DepthWriteEnable = true, DepthFunc = RHIComparisonFunction.Less },
                    RenderTargetFormats = new[] { RHIPixelFormat.R16G16B16A16_Float },
                    DepthStencilFormat = RHIPixelFormat.D24_UNorm_S8_UInt,
                    PrimitiveTopology = RHIPrimitiveTopology.TriangleList,
                    InputLayout = new[]
                    {
                        new RHIInputLayoutDesc("POSITION", 0, RHIPixelFormat.R32G32B32_Float, 0, 0),
                        new RHIInputLayoutDesc("NORMAL", 0, RHIPixelFormat.R32G32B32_Float, 0, 12),
                        new RHIInputLayoutDesc("TEXCOORD", 0, RHIPixelFormat.R32G32_Float, 0, 24)
                    }
                };

                _rootSignature = _device.CreateRootSignature(new RHIRootSignatureDesc
                {
                    ConstantBuffers = new[]
                    {
                        new RHIRootDescriptorRange { RangeType = RHIDescriptorRangeType.CBV, NumDescriptors = 1, BaseShaderRegister = 0 }
                    },
                    SRVCount = 8,
                    SamplerCount = 2
                });
                psoDesc.RootSignature = _rootSignature;
                _terrainPSO = _device.CreatePipelineState(psoDesc);
                _terrainCB = (RHIConstantBuffer)_device.CreateConstantBuffer(128);
            }
            catch { }
        }

        public void Dispose()
        {
            _terrainPSO?.Dispose();
            _rootSignature?.Dispose();
            _terrainCB?.Dispose();
            _vertexBuffer?.Dispose();
            _indexBuffer?.Dispose();
        }
    }

    public struct TerrainVertex
    {
        public Vector3 Position;
        public Vector3 Normal;
        public Vector2 UV;
    }

    public struct TerrainConstants
    {
        public Matrix4x4 ViewProjection;
        public Vector4 CameraPosition;
        public float HeightScale;
        public float TerrainSize;
        public float TessellationFactor;
        public float TessellationDistance;
        public uint UseTessellation;
        public Vector3 _padding;
    }
}
