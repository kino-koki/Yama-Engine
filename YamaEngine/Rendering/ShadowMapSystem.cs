// ════════════════════════════════════════════════════════════════════════
// yama引擎 - ShadowMap 阴影系统
//
// 基于简单的方向光 Shadow Map。
// 使用单个 2048x2048 深度纹理，正交投影（适合方向光）。
// ════════════════════════════════════════════════════════════════════════

using System;
using System.Numerics;
using YamaEngine.Assets;
using YamaEngine.RHI;

namespace YamaEngine.Rendering
{
    /// <summary>
    /// ShadowMap 阴影系统 — 基于简单的方向光 Shadow Map。
    /// 使用单个 2048x2048 深度纹理，正交投影（适合方向光）。
    /// </summary>
    public class ShadowMapSystem : IDisposable
    {
        private readonly RHIDevice _device;

        // ShadowMap 纹理和视图
        private RHITexture? _shadowMapTexture;
        private RHIDescriptorHandle _shadowMapDSV;    // 深度模板视图（用于渲染）
        private RHIDescriptorHandle _shadowMapSRV;    // 着色器资源视图（用于采样）

        // ShadowMap 尺寸
        public const int ShadowMapSize = 2048;

        // 阴影渲染管线
        private RHIPipelineState? _shadowPSO;
        private RHIRootSignature? _shadowRootSignature;
        private RHIShaderBytecode? _shadowVS;

        // 阴影常量缓冲区 (b0: WorldViewProj)
        private RHIBuffer? _shadowCB;

        // 可复用的常量数据数组
        private readonly ShadowConstants[] _shadowDataArray = new ShadowConstants[1];

        // 光源视角矩阵
        private Matrix4x4 _lightViewMatrix;
        private Matrix4x4 _lightProjectionMatrix;
        private Matrix4x4 _lightViewProjectionMatrix;

        // D3D12 常量缓冲区对齐要求
        private const uint ConstantBufferAlignment = 256;

        /// <summary>阴影常量缓冲区布局（与 HLSL 侧严格对应）</summary>
        public struct ShadowConstants
        {
            public Matrix4x4 WorldViewProj;
        }

        public ShadowMapSystem(RHIDevice device)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));
        }

        /// <summary>
        /// 初始化 ShadowMap 资源。
        /// </summary>
        public void Initialize()
        {
            // 1. 创建深度纹理 (D32_Float, ShaderResource | DepthStencil)
            var desc = new RHITextureDesc
            {
                Dimension = RHIResourceDimension.Texture2D,
                Format = RHIFormat.D32_Float,
                Width = ShadowMapSize,
                Height = ShadowMapSize,
                DepthOrArraySize = 1,
                MipLevels = 1,
                SampleCount = 1,
                Flags = RHITextureFlags.DepthStencil | RHITextureFlags.ShaderResource,
                OptimizedClearValue = RHIClearValueHelper.Depth(1.0f, 0, RHIFormat.D32_Float)
            };
            _shadowMapTexture = _device.CreateTexture(desc, "ShadowMap");
            _shadowMapTexture.CurrentState = RHIResourceState.DepthWrite;

            // 2. 创建 DSV 和 SRV
            _shadowMapDSV = _device.CreateDSV(_shadowMapTexture);
            _shadowMapSRV = _device.CreateSRV(_shadowMapTexture);

            // 3. 编译阴影深度着色器
            CompileShadowShaders();

            // 4. 创建阴影渲染 PSO
            CreateShadowPSO();

            // 5. 创建阴影常量缓冲区
            var cbDesc = new RHIBufferDesc
            {
                SizeInBytes = ConstantBufferAlignment,
                HeapType = RHIResourceHeapType.Default,
                Flags = RHIBufferFlags.ConstantBuffer,
                Stride = ConstantBufferAlignment
            };
            _shadowCB = _device.CreateBuffer(cbDesc, "ShadowConstantsCB");
            _shadowCB.CurrentState = RHIResourceState.Common;
        }

        /// <summary>
        /// 开始 ShadowMap 渲染通道 — 设置渲染目标、视口、PSO。
        /// 返回光源的 ViewProjection 矩阵，供调用方遍历绘制网格。
        /// </summary>
        public Matrix4x4 BeginShadowPass(RHICommandList cmd, Vector3 lightDirection, IRenderScene scene)
        {
            if (cmd == null) return Matrix4x4.Identity;
            EnsureInitialized();

            // 1. 计算光源视角/投影矩阵
            UpdateLightMatrices(lightDirection, scene);

            // 2. 资源屏障：确保 ShadowMap 处于 DepthWrite 状态
            if (_shadowMapTexture != null && _shadowMapTexture.CurrentState != RHIResourceState.DepthWrite)
            {
                cmd.TransitionTo(_shadowMapTexture, RHIResourceState.DepthWrite);
            }

            // 3. 设置渲染目标为 ShadowMap（只有深度，无颜色目标）
            cmd.OMSetRenderTargets(Array.Empty<RHIDescriptorHandle>(), _shadowMapDSV);
            cmd.ClearDepthStencilView(_shadowMapDSV, 1.0f, 0);

            // 4. 设置视口和裁剪矩形
            cmd.RSSetViewport(0, 0, ShadowMapSize, ShadowMapSize, 0, 1);
            cmd.RSSetScissorRect(0, 0, ShadowMapSize, ShadowMapSize);

            // 5. 设置阴影管线
            cmd.SetPipelineState(_shadowPSO!);
            cmd.SetGraphicsRootSignature(_shadowRootSignature!);
            cmd.IASetPrimitiveTopology(RHIPrimitiveTopology.TriangleList);

            return _lightViewProjectionMatrix;
        }

        /// <summary>
        /// 上传并绑定单个物体的 WorldViewProj 矩阵到阴影常量缓冲区。
        /// </summary>
        public void SetShadowObjectConstants(RHICommandList cmd, Matrix4x4 worldMatrix)
        {
            if (cmd == null || _shadowCB == null) return;

            var wvp = worldMatrix * _lightViewProjectionMatrix;
            _shadowDataArray[0] = new ShadowConstants { WorldViewProj = wvp };
            _device.UploadToBuffer(_shadowCB, _shadowDataArray, 0);
            cmd.SetGraphicsRootConstantBufferView(0, _shadowCB);
        }

        /// <summary>
        /// 结束 ShadowMap 渲染通道 — 执行资源屏障转换到 ShaderResource。
        /// </summary>
        public void EndShadowPass(RHICommandList cmd)
        {
            if (cmd == null || _shadowMapTexture == null) return;

            // 资源屏障：DepthWrite -> PixelShaderResource
            if (_shadowMapTexture.CurrentState != RHIResourceState.PixelShaderResource)
            {
                cmd.TransitionTo(_shadowMapTexture, RHIResourceState.PixelShaderResource);
            }
        }

        /// <summary>
        /// 获取 ShadowMap 的 SRV，供主渲染通道采样。
        /// </summary>
        public RHIDescriptorHandle GetShadowMapSRV() => _shadowMapSRV;

        /// <summary>
        /// 获取光源的 ViewProjection 矩阵，用于主渲染通道的阴影比较。
        /// </summary>
        public Matrix4x4 GetLightViewProjectionMatrix() => _lightViewProjectionMatrix;

        /// <summary>
        /// 获取阴影根签名（供外部复用，如骨骼蒙皮阴影）。
        /// </summary>
        public RHIRootSignature? GetShadowRootSignature() => _shadowRootSignature;

        /// <summary>
        /// 获取阴影 PSO（供外部复用）。
        /// </summary>
        public RHIPipelineState? GetShadowPSO() => _shadowPSO;

        private void EnsureInitialized()
        {
            if (_shadowMapTexture == null)
                Initialize();
        }

        private void UpdateLightMatrices(Vector3 lightDir, IRenderScene scene)
        {
            // 计算场景包围盒
            var bounds = CalculateSceneBounds(scene);
            var center = (bounds.Min + bounds.Max) * 0.5f;
            var size = bounds.Max - bounds.Min;
            var maxSize = MathF.Max(size.X, MathF.Max(size.Y, size.Z));
            maxSize = MathF.Max(maxSize, 1.0f); // 防止空场景

            // 光源位置：沿光照反方向偏移
            var lightPos = center - Vector3.Normalize(lightDir) * maxSize * 2.0f;

            // 视角矩阵：光源看向场景中心
            _lightViewMatrix = Matrix4x4.CreateLookAt(lightPos, center, Vector3.UnitY);

            // 正交投影矩阵
            var orthoSize = maxSize * 0.6f;
            _lightProjectionMatrix = Matrix4x4.CreateOrthographic(
                orthoSize * 2, orthoSize * 2, 0.1f, maxSize * 4.0f);

            _lightViewProjectionMatrix = _lightViewMatrix * _lightProjectionMatrix;
        }

        private void CompileShadowShaders()
        {
            string shadowVS = @"
cbuffer ShadowConstants : register(b0)
{
    float4x4 g_WorldViewProj;
};

struct VSInput
{
    float3 Position : POSITION;
};

struct VSOutput
{
    float4 Position : SV_POSITION;
    float Depth : DEPTH;
};

VSOutput main(VSInput input)
{
    VSOutput output;
    output.Position = mul(g_WorldViewProj, float4(input.Position, 1.0f));
    output.Depth = output.Position.z / output.Position.w;
    return output;
}
";
            _shadowVS = _device.CompileShader(shadowVS, "main",
                RHIShaderStage.Vertex, RHIShaderModel.SM_5_1, "ShadowVS");
        }

        private void CreateShadowPSO()
        {
            // 创建只输出深度的根签名: CBV(b0) = WorldViewProj
            var rootSigDesc = new RHIRootSignatureDesc
            {
                Parameters = new[]
                {
                    new RHIRootParameter
                    {
                        ParameterType = RHIRootParameterType.CBV,
                        ShaderVisibility = RHIShaderVisibility.All,
                        Descriptor = new RHIRootDescriptor { ShaderRegister = 0, RegisterSpace = 0 }
                    }
                },
                AllowInputAssembler = true,
                Flags = RHIRootSignatureFlags.AllowInputAssemblerInputLayout
            };
            _shadowRootSignature = _device.CreateRootSignature(rootSigDesc, "ShadowRootSignature");

            // 创建只输出深度的PSO
            // 无像素着色器（深度-only渲染）
            // DepthStencilState: DepthEnable=true, DepthFunc=Less, DepthWriteEnable=true
            // RasterizerState: CullFront（避免阴影悬浮/Shadow acne），DepthBias 设置
            var psoDesc = new RHIGraphicsPipelineDesc
            {
                Name = "ShadowPSO",
                VS = _shadowVS!.Value,
                PS = null, // 无像素着色器
                InputLayout = new[]
                {
                    new RHIInputElement("POSITION", RHIFormat.R32G32B32_Float, 0, 0),
                    new RHIInputElement("NORMAL",   RHIFormat.R32G32B32_Float, 0, 12),
                    new RHIInputElement("TEXCOORD", RHIFormat.R32G32_Float,    0, 24)
                },
                PrimitiveTopology = RHIPrimitiveTopology.TriangleList,
                BlendState = RHIBlendDesc.Default,
                DepthStencilState = new RHIDepthDesc
                {
                    DepthEnable = true,
                    DepthWriteEnable = true,
                    DepthFunc = RHIComparison.Less,
                    StencilEnable = false
                },
                RasterizerState = new RHIRasterizerDesc
                {
                    FillMode = RHIFillMode.Solid,
                    CullMode = RHICullMode.Front, // 正面剔除避免阴影悬浮
                    FrontCounterClockwise = true,
                    DepthBias = 100000,
                    DepthBiasClamp = 0.0f,
                    SlopeScaledDepthBias = 1.5f,
                    DepthClipEnable = true,
                    MultisampleEnable = false,
                    AntialiasedLineEnable = false,
                    ConservativeRaster = false
                },
                RTVFormats = Array.Empty<RHIFormat>(), // 无颜色输出
                DSVFormat = RHIFormat.D32_Float,
                RootSignature = _shadowRootSignature
            };
            _shadowPSO = _device.CreateGraphicsPipelineState(psoDesc, "ShadowPSO");
        }

        private static BoundingBox CalculateSceneBounds(IRenderScene scene)
        {
            if (scene?.Meshes == null || scene.Meshes.Count == 0)
            {
                return new BoundingBox(
                    new Vector3(-10, -10, -10),
                    new Vector3(10, 10, 10));
            }

            Vector3 min = new(float.MaxValue);
            Vector3 max = new(float.MinValue);

            foreach (var mesh in scene.Meshes)
            {
                if (mesh == null || !mesh.IsVisible) continue;

                // 从世界矩阵提取平移作为粗略中心
                Vector3 center = new(mesh.WorldMatrix.M41, mesh.WorldMatrix.M42, mesh.WorldMatrix.M43);
                // 使用一个默认半径估计包围盒（因为没有原始顶点数据）
                float radius = 2.0f;

                min = Vector3.Min(min, center - new Vector3(radius));
                max = Vector3.Max(max, center + new Vector3(radius));
            }

            if (min.X == float.MaxValue)
            {
                return new BoundingBox(
                    new Vector3(-10, -10, -10),
                    new Vector3(10, 10, 10));
            }

            return new BoundingBox(min, max);
        }

        public void Dispose()
        {
            _shadowPSO?.Dispose();
            _shadowRootSignature?.Dispose();
            _shadowCB?.Dispose();
            _shadowMapTexture?.Dispose();

            _shadowPSO = null;
            _shadowRootSignature = null;
            _shadowCB = null;
            _shadowMapTexture = null;
        }
    }
}
