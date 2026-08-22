// ════════════════════════════════════════════════════════════════════════
// 山雨引擎 - 后处理系统 (PostProcessSystem)
//
// 实现色调映射 + 泛光(Bloom) 后处理管线。
// 渲染流程: 场景渲染 → 亮度提取 → 下采样 → 高斯模糊 → 上采样合并 → 色调映射 → 输出。
// ════════════════════════════════════════════════════════════════════════

using System;
using System.Numerics;
using System.Runtime.InteropServices;
using ShanyuEngine.RHI;

namespace ShanyuEngine.Rendering
{
    /// <summary>
    /// 后处理系统 — 色调映射 + 泛光(Bloom)。
    /// </summary>
    public class PostProcessSystem : IDisposable
    {
        private readonly RHIDevice _device;
        private int _width;
        private int _height;

        // 中间渲染目标
        private RHITexture? _sceneColorTexture;      // 场景颜色 (R16G16B16A16_Float)
        private RHITexture? _bloomTexture1;          // Bloom 下采样 1 (1/2)
        private RHITexture? _bloomTexture2;          // Bloom 下采样 2 (1/4)
        private RHITexture? _bloomTexture3;          // Bloom 下采样 3 (1/8)

        // 视图
        private RHIDescriptorHandle _sceneColorRTV;
        private RHIDescriptorHandle _sceneColorSRV;
        private RHIDescriptorHandle _bloomSRV1, _bloomSRV2, _bloomSRV3;
        private RHIDescriptorHandle _bloomRTV1, _bloomRTV2, _bloomRTV3;

        // 根签名和 PSO
        private RHIRootSignature? _rootSignature;
        private RHIPipelineState? _tonemapPSO;
        private RHIPipelineState? _bloomExtractPSO;
        private RHIPipelineState? _bloomBlurHPSO;    // 水平模糊
        private RHIPipelineState? _bloomBlurVPSO;    // 垂直模糊

        // 着色器
        private RHIShaderBytecode _fullscreenVS;
        private RHIShaderBytecode _bloomExtractPS;
        private RHIShaderBytecode _bloomBlurPS;
        private RHIShaderBytecode _tonemapPS;

        // 全屏四边形 VB/IB
        private RHIBuffer? _fullscreenVB;
        private RHIBuffer? _fullscreenIB;

        // 常量缓冲区
        private RHIBuffer? _postProcessCB;

        /// <summary>当前宽度。</summary>
        public int Width => _width;
        /// <summary>当前高度。</summary>
        public int Height => _height;

        /// <summary>曝光值。</summary>
        public float Exposure = 1.0f;
        /// <summary>泛光强度。</summary>
        public float BloomIntensity = 0.8f;
        /// <summary>泛光阈值。</summary>
        public float BloomThreshold = 1.0f;

        public PostProcessSystem(RHIDevice device, int width, int height)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));
            _width = Math.Max(1, width);
            _height = Math.Max(1, height);
        }

        public void Initialize()
        {
            CreateRenderTargets();
            CreateFullscreenQuad();
            CompileShaders();
            CreateRootSignature();
            CreatePSOs();
            CreateConstantBuffer();
        }

        /// <summary>调整尺寸（尺寸变化时重新创建资源）。</summary>
        public void Resize(int width, int height)
        {
            width = Math.Max(1, width);
            height = Math.Max(1, height);
            if (width == _width && height == _height) return;
            _width = width;
            _height = height;

            // 释放旧资源
            _sceneColorTexture?.Dispose();
            _bloomTexture1?.Dispose();
            _bloomTexture2?.Dispose();
            _bloomTexture3?.Dispose();
            _sceneColorTexture = null;
            _bloomTexture1 = null;
            _bloomTexture2 = null;
            _bloomTexture3 = null;
            _sceneColorRTV = default;
            _sceneColorSRV = default;
            _bloomSRV1 = default; _bloomSRV2 = default; _bloomSRV3 = default;
            _bloomRTV1 = default; _bloomRTV2 = default; _bloomRTV3 = default;

            CreateRenderTargets();
        }

        /// <summary>获取场景颜色渲染目标（主渲染通道输出到这里）。</summary>
        public RHIDescriptorHandle GetSceneColorRTV() => _sceneColorRTV;

        /// <summary>获取场景颜色着色器资源视图。</summary>
        public RHIDescriptorHandle GetSceneColorSRV() => _sceneColorSRV;

        /// <summary>
        /// 执行后处理：输入场景颜色纹理 → 输出到屏幕。
        /// </summary>
        public void RenderPostProcess(RHICommandList cmd, RHIDescriptorHandle outputRTV, int outputWidth, int outputHeight)
        {
            if (cmd == null || _sceneColorTexture == null) return;
            if (!outputRTV.IsValid) return;

            // ── 1. Bloom 亮度提取 ──
            // 从 _sceneColorSRV 读取，提取高亮度像素到 _bloomRTV1
            cmd.TransitionTo(_sceneColorTexture, RHIResourceState.PixelShaderResource);
            cmd.TransitionTo(_bloomTexture1!, RHIResourceState.RenderTarget);
            cmd.OMSetRenderTargets(new[] { _bloomRTV1 }, null);
            cmd.SetViewport(new RHIViewport(0, 0, Math.Max(1, _width / 2), Math.Max(1, _height / 2), 0, 1));
            cmd.SetScissorRect(new RHIScissorRect(0, 0, Math.Max(1, _width / 2), Math.Max(1, _height / 2)));
            cmd.SetPipelineState(_bloomExtractPSO!);
            cmd.SetGraphicsRootSignature(_rootSignature!);
            cmd.SetGraphicsRootDescriptorTable(1, _sceneColorSRV);
            SetBloomExtractConstants(cmd);
            DrawFullscreenQuad(cmd);

            // ── 2. Bloom 下采样 + 模糊 ──
            // _bloomTexture1 → 水平模糊 → _bloomTexture2
            cmd.TransitionTo(_bloomTexture1!, RHIResourceState.PixelShaderResource);
            cmd.TransitionTo(_bloomTexture2!, RHIResourceState.RenderTarget);
            cmd.OMSetRenderTargets(new[] { _bloomRTV2 }, null);
            cmd.SetViewport(new RHIViewport(0, 0, Math.Max(1, _width / 4), Math.Max(1, _height / 4), 0, 1));
            cmd.SetScissorRect(new RHIScissorRect(0, 0, Math.Max(1, _width / 4), Math.Max(1, _height / 4)));
            cmd.SetPipelineState(_bloomBlurHPSO!);
            cmd.SetGraphicsRootDescriptorTable(1, _bloomSRV1);
            SetBlurConstants(cmd, new Vector2(1.0f / Math.Max(1, _width / 2), 0.0f));
            DrawFullscreenQuad(cmd);

            // _bloomTexture2 → 垂直模糊 → _bloomTexture3
            cmd.TransitionTo(_bloomTexture2!, RHIResourceState.PixelShaderResource);
            cmd.TransitionTo(_bloomTexture3!, RHIResourceState.RenderTarget);
            cmd.OMSetRenderTargets(new[] { _bloomRTV3 }, null);
            cmd.SetViewport(new RHIViewport(0, 0, Math.Max(1, _width / 8), Math.Max(1, _height / 8), 0, 1));
            cmd.SetScissorRect(new RHIScissorRect(0, 0, Math.Max(1, _width / 8), Math.Max(1, _height / 8)));
            cmd.SetPipelineState(_bloomBlurVPSO!);
            cmd.SetGraphicsRootDescriptorTable(1, _bloomSRV2);
            SetBlurConstants(cmd, new Vector2(0.0f, 1.0f / Math.Max(1, _height / 4)));
            DrawFullscreenQuad(cmd);

            // ── 3. 色调映射 + Bloom 合并 ──
            // _sceneColorSRV + _bloomSRV3 → 输出到 outputRTV
            cmd.TransitionTo(_bloomTexture3!, RHIResourceState.PixelShaderResource);
            cmd.TransitionTo(_sceneColorTexture, RHIResourceState.PixelShaderResource);
            cmd.OMSetRenderTargets(new[] { outputRTV }, null);
            cmd.SetViewport(new RHIViewport(0, 0, outputWidth, outputHeight, 0, 1));
            cmd.SetScissorRect(new RHIScissorRect(0, 0, outputWidth, outputHeight));
            cmd.SetPipelineState(_tonemapPSO!);
            cmd.SetGraphicsRootSignature(_rootSignature!);
            cmd.SetGraphicsRootDescriptorTable(1, _sceneColorSRV);
            cmd.SetGraphicsRootDescriptorTable(2, _bloomSRV3);
            SetTonemapConstants(cmd);
            DrawFullscreenQuad(cmd);
        }

        // ──────────────────────────────────────────────────────────────
        // 常量设置
        // ──────────────────────────────────────────────────────────────

        private void SetBloomExtractConstants(RHICommandList cmd)
        {
            var data = new PostProcessConstants
            {
                BloomThreshold = BloomThreshold,
                BloomIntensity = BloomIntensity,
                Exposure = Exposure
            };
            _device.UploadToBuffer(_postProcessCB!, new[] { data }, 0);
            cmd.SetGraphicsRootConstantBufferView(0, _postProcessCB!);
        }

        private void SetBlurConstants(RHICommandList cmd, Vector2 direction)
        {
            var data = new PostProcessConstants
            {
                BlurDirection = direction,
                TexelSize = new Vector2(1.0f / _width, 1.0f / _height),
                BloomThreshold = BloomThreshold,
                BloomIntensity = BloomIntensity,
                Exposure = Exposure
            };
            _device.UploadToBuffer(_postProcessCB!, new[] { data }, 0);
            cmd.SetGraphicsRootConstantBufferView(0, _postProcessCB!);
        }

        private void SetTonemapConstants(RHICommandList cmd)
        {
            var data = new PostProcessConstants
            {
                BloomThreshold = BloomThreshold,
                BloomIntensity = BloomIntensity,
                Exposure = Exposure
            };
            _device.UploadToBuffer(_postProcessCB!, new[] { data }, 0);
            cmd.SetGraphicsRootConstantBufferView(0, _postProcessCB!);
        }

        // ──────────────────────────────────────────────────────────────
        // 全屏四边形绘制
        // ──────────────────────────────────────────────────────────────

        private void DrawFullscreenQuad(RHICommandList cmd)
        {
            cmd.IASetPrimitiveTopology(RHIPrimitiveTopology.TriangleList);
            cmd.IASetVertexBuffers(0, _fullscreenVB!, 0, 16, 64u);
            cmd.IASetIndexBuffer(_fullscreenIB!, 0, (uint)(6 * sizeof(uint)), RHIFormat.R32_UInt);
            cmd.DrawIndexedInstanced(6, 1, 0, 0, 0);
        }

        // ──────────────────────────────────────────────────────────────
        // 资源创建
        // ──────────────────────────────────────────────────────────────

        private void CreateRenderTargets()
        {
            // 创建 R16G16B16A16_Float 场景颜色纹理
            var sceneColorDesc = new RHITextureDesc
            {
                Dimension = RHIResourceDimension.Texture2D,
                Format = RHIFormat.R16G16B16A16_Float,
                Width = _width,
                Height = _height,
                DepthOrArraySize = 1,
                MipLevels = 1,
                SampleCount = 1,
                Flags = RHITextureFlags.RenderTarget | RHITextureFlags.ShaderResource
            };
            _sceneColorTexture = _device.CreateTexture(sceneColorDesc, "PostProcessSceneColor");
            _sceneColorTexture.CurrentState = RHIResourceState.Common;
            _sceneColorRTV = _device.CreateRTV(_sceneColorTexture);
            _sceneColorSRV = _device.CreateSRV(_sceneColorTexture, 0, 1);

            // Bloom 纹理 1 (1/2)
            var bloomDesc1 = sceneColorDesc;
            bloomDesc1.Width = Math.Max(1, _width / 2);
            bloomDesc1.Height = Math.Max(1, _height / 2);
            _bloomTexture1 = _device.CreateTexture(bloomDesc1, "BloomTexture1");
            _bloomTexture1.CurrentState = RHIResourceState.Common;
            _bloomRTV1 = _device.CreateRTV(_bloomTexture1);
            _bloomSRV1 = _device.CreateSRV(_bloomTexture1, 0, 1);

            // Bloom 纹理 2 (1/4)
            var bloomDesc2 = sceneColorDesc;
            bloomDesc2.Width = Math.Max(1, _width / 4);
            bloomDesc2.Height = Math.Max(1, _height / 4);
            _bloomTexture2 = _device.CreateTexture(bloomDesc2, "BloomTexture2");
            _bloomTexture2.CurrentState = RHIResourceState.Common;
            _bloomRTV2 = _device.CreateRTV(_bloomTexture2);
            _bloomSRV2 = _device.CreateSRV(_bloomTexture2, 0, 1);

            // Bloom 纹理 3 (1/8)
            var bloomDesc3 = sceneColorDesc;
            bloomDesc3.Width = Math.Max(1, _width / 8);
            bloomDesc3.Height = Math.Max(1, _height / 8);
            _bloomTexture3 = _device.CreateTexture(bloomDesc3, "BloomTexture3");
            _bloomTexture3.CurrentState = RHIResourceState.Common;
            _bloomRTV3 = _device.CreateRTV(_bloomTexture3);
            _bloomSRV3 = _device.CreateSRV(_bloomTexture3, 0, 1);
        }

        private void CreateFullscreenQuad()
        {
            // 4个顶点：(-1,-1), (1,-1), (-1,1), (1,1)
            // 6个索引：2个三角形
            // 顶点布局: Position(float2) + UV(float2) = 16B
            var vertices = new[]
            {
                new Vector4(-1.0f, -1.0f, 0.0f, 1.0f),
                new Vector4( 1.0f, -1.0f, 1.0f, 1.0f),
                new Vector4(-1.0f,  1.0f, 0.0f, 0.0f),
                new Vector4( 1.0f,  1.0f, 1.0f, 0.0f)
            };

            var vbDesc = new RHIBufferDesc
            {
                SizeInBytes = (ulong)(vertices.Length * 16),
                HeapType = RHIResourceHeapType.Default,
                Flags = RHIBufferFlags.VertexBuffer,
                Stride = 16
            };
            _fullscreenVB = _device.CreateBuffer(vbDesc, "FullscreenQuadVB");
            _fullscreenVB.CurrentState = RHIResourceState.Common;
            _device.UploadToBuffer(_fullscreenVB, vertices, 0);

            uint[] indices = { 0, 1, 2, 2, 1, 3 };
            var ibDesc = new RHIBufferDesc
            {
                SizeInBytes = (ulong)(indices.Length * sizeof(uint)),
                HeapType = RHIResourceHeapType.Default,
                Flags = RHIBufferFlags.IndexBuffer,
                Stride = (uint)sizeof(uint)
            };
            _fullscreenIB = _device.CreateBuffer(ibDesc, "FullscreenQuadIB");
            _fullscreenIB.CurrentState = RHIResourceState.Common;
            _device.UploadToBuffer(_fullscreenIB, indices, 0);
        }

        private void CompileShaders()
        {
            _fullscreenVS = _device.CompileShader(
                BuiltinShaders.PostProcessVertexShader, "main",
                RHIShaderStage.Vertex, RHIShaderModel.SM_5_1, "PostProcessVS");

            _bloomExtractPS = _device.CompileShader(
                BuiltinShaders.BloomExtractPixelShader, "main",
                RHIShaderStage.Pixel, RHIShaderModel.SM_5_1, "BloomExtractPS");

            _bloomBlurPS = _device.CompileShader(
                BuiltinShaders.BloomBlurPixelShader, "main",
                RHIShaderStage.Pixel, RHIShaderModel.SM_5_1, "BloomBlurPS");

            _tonemapPS = _device.CompileShader(
                BuiltinShaders.TonemapPixelShader, "main",
                RHIShaderStage.Pixel, RHIShaderModel.SM_5_1, "TonemapPS");
        }

        private void CreateRootSignature()
        {
            var rootSigDesc = new RHIRootSignatureDesc
            {
                Parameters = new[]
                {
                    // Param 0: CBV(b0) - PostProcess constants
                    new RHIRootParameter
                    {
                        ParameterType = RHIRootParameterType.CBV,
                        ShaderVisibility = RHIShaderVisibility.All,
                        Descriptor = new RHIRootDescriptor { ShaderRegister = 0, RegisterSpace = 0 }
                    },
                    // Param 1: DescriptorTable(SRV t0-t1) - SceneColor / Bloom inputs
                    new RHIRootParameter
                    {
                        ParameterType = RHIRootParameterType.DescriptorTable,
                        ShaderVisibility = RHIShaderVisibility.Pixel,
                        DescriptorTable = new RHIRootDescriptorTable
                        {
                            Ranges = new[]
                            {
                                new RHIRootDescriptorRange
                                {
                                    RangeType = RHIDescriptorRangeType.SRV,
                                    NumDescriptors = 2,
                                    BaseShaderRegister = 0,
                                    RegisterSpace = 0,
                                    OffsetInDescriptorsFromTableStart = 0
                                }
                            }
                        }
                    }
                },
                StaticSamplers = new[]
                {
                    new RHISamplerDesc
                    {
                        Filter = RHIFilter.Linear,
                        AddressU = RHITextureAddressMode.Clamp,
                        AddressV = RHITextureAddressMode.Clamp,
                        AddressW = RHITextureAddressMode.Clamp,
                        ShaderRegister = 0,
                        RegisterSpace = 0,
                        ShaderVisibility = RHIShaderVisibility.Pixel
                    }
                },
                AllowInputAssembler = true,
                Flags = RHIRootSignatureFlags.AllowInputAssemblerInputLayout
            };
            _rootSignature = _device.CreateRootSignature(rootSigDesc, "PostProcessRootSignature");
        }

        private void CreatePSOs()
        {
            // 全屏四边形输入布局: Position(float2) + UV(float2) = 16B
            var inputLayout = new[]
            {
                new RHIInputElement("POSITION", RHIFormat.R32G32_Float, 0, 0),
                new RHIInputElement("TEXCOORD", RHIFormat.R32G32_Float, 0, 8)
            };

            var depthDisabled = new RHIDepthDesc
            {
                DepthEnable = false,
                StencilEnable = false
            };

            // Bloom Extract PSO
            var extractDesc = new RHIGraphicsPipelineDesc
            {
                Name = "BloomExtractPSO",
                VS = _fullscreenVS,
                PS = _bloomExtractPS,
                InputLayout = inputLayout,
                PrimitiveTopology = RHIPrimitiveTopology.TriangleList,
                BlendState = RHIBlendDesc.Default,
                DepthStencilState = depthDisabled,
                RasterizerState = RHIRasterizerDesc.NoCull,
                RTVFormats = new[] { RHIFormat.R16G16B16A16_Float },
                RootSignature = _rootSignature
            };
            _bloomExtractPSO = _device.CreateGraphicsPipelineState(extractDesc, "BloomExtractPSO");

            // Bloom Blur H PSO
            var blurDesc = new RHIGraphicsPipelineDesc
            {
                Name = "BloomBlurHPSO",
                VS = _fullscreenVS,
                PS = _bloomBlurPS,
                InputLayout = inputLayout,
                PrimitiveTopology = RHIPrimitiveTopology.TriangleList,
                BlendState = RHIBlendDesc.Default,
                DepthStencilState = depthDisabled,
                RasterizerState = RHIRasterizerDesc.NoCull,
                RTVFormats = new[] { RHIFormat.R16G16B16A16_Float },
                RootSignature = _rootSignature
            };
            _bloomBlurHPSO = _device.CreateGraphicsPipelineState(blurDesc, "BloomBlurHPSO");

            // Bloom Blur V PSO (same as H but different name)
            var blurVDesc = new RHIGraphicsPipelineDesc
            {
                Name = "BloomBlurVPSO",
                VS = _fullscreenVS,
                PS = _bloomBlurPS,
                InputLayout = inputLayout,
                PrimitiveTopology = RHIPrimitiveTopology.TriangleList,
                BlendState = RHIBlendDesc.Default,
                DepthStencilState = depthDisabled,
                RasterizerState = RHIRasterizerDesc.NoCull,
                RTVFormats = new[] { RHIFormat.R16G16B16A16_Float },
                RootSignature = _rootSignature
            };
            _bloomBlurVPSO = _device.CreateGraphicsPipelineState(blurVDesc, "BloomBlurVPSO");

            // Tonemap PSO
            var tonemapDesc = new RHIGraphicsPipelineDesc
            {
                Name = "TonemapPSO",
                VS = _fullscreenVS,
                PS = _tonemapPS,
                InputLayout = inputLayout,
                PrimitiveTopology = RHIPrimitiveTopology.TriangleList,
                BlendState = RHIBlendDesc.Default,
                DepthStencilState = depthDisabled,
                RasterizerState = RHIRasterizerDesc.NoCull,
                RTVFormats = new[] { RHIFormat.R8G8B8A8_UNorm },
                RootSignature = _rootSignature
            };
            _tonemapPSO = _device.CreateGraphicsPipelineState(tonemapDesc, "TonemapPSO");
        }

        private void CreateConstantBuffer()
        {
            var cbDesc = new RHIBufferDesc
            {
                SizeInBytes = 256,
                HeapType = RHIResourceHeapType.Default,
                Flags = RHIBufferFlags.ConstantBuffer,
                Stride = 256
            };
            _postProcessCB = _device.CreateBuffer(cbDesc, "PostProcessConstantsCB");
            _postProcessCB.CurrentState = RHIResourceState.Common;
        }

        public void Dispose()
        {
            _sceneColorTexture?.Dispose();
            _bloomTexture1?.Dispose();
            _bloomTexture2?.Dispose();
            _bloomTexture3?.Dispose();
            _fullscreenVB?.Dispose();
            _fullscreenIB?.Dispose();
            _postProcessCB?.Dispose();
            _rootSignature?.Dispose();
            _tonemapPSO?.Dispose();
            _bloomExtractPSO?.Dispose();
            _bloomBlurHPSO?.Dispose();
            _bloomBlurVPSO?.Dispose();
            GC.SuppressFinalize(this);
        }

        // ──────────────────────────────────────────────────────────────
        // 常量缓冲区布局
        // ──────────────────────────────────────────────────────────────

        [StructLayout(LayoutKind.Sequential)]
        private struct PostProcessConstants
        {
            public Vector2 BlurDirection;
            public Vector2 TexelSize;
            public float BloomThreshold;
            public float BloomIntensity;
            public float Exposure;
            public float Pad0;
            // Pad to 256 bytes for CB alignment
            public Vector4 Pad1;
            public Vector4 Pad2;
            public Vector4 Pad3;
            public Vector4 Pad4;
            public Vector4 Pad5;
            public Vector4 Pad6;
            public Vector4 Pad7;
            public Vector4 Pad8;
            public Vector4 Pad9;
            public Vector4 Pad10;
            public Vector4 Pad11;
            public Vector4 Pad12;
            public Vector4 Pad13;
            public Vector4 Pad14;
        }
    }
}
