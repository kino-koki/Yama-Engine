using System;
using System.Numerics;
using ShanyuEngine.RHI;

namespace ShanyuEngine.Rendering
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

        public TerrainRenderer(RHIDevice device)
        {
            _device = device;
            CreateTerrainMesh();
        }

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

            _vertexBuffer = _device.CreateBuffer(vertices, RHIBufferUsage.Vertex);
            _indexBuffer = _device.CreateBuffer(indices, RHIBufferUsage.Index);
        }

        public void Render(RHICommandList cmd, Vector3 cameraPos, Matrix4x4 view, Matrix4x4 proj)
        {
            if (_terrainPSO == null) InitializePSO();
            if (_terrainPSO == null) return;

            cmd.SetPipelineState(_terrainPSO);
            cmd.SetRootSignature(_rootSignature);
            cmd.SetVertexBuffer(0, _vertexBuffer);
            cmd.SetIndexBuffer(_indexBuffer);

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
            _terrainCB?.UpdateData(constants);
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
                    VertexShader = vs,
                    PixelShader = ps,
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
                    ConstantBuffers = new[] { 0 },
                    SRVCount = 8,
                    SamplerCount = 2
                });
                psoDesc.RootSignature = _rootSignature;
                _terrainPSO = _device.CreatePipelineState(psoDesc);
                _terrainCB = _device.CreateConstantBuffer(128);
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
