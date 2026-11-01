// =============================================================================
//  Yama Engine - ParticleRenderer.cs
//  yama引擎 - GPU 粒子渲染器
//
//  将 FlowVFX 模拟的粒子数据渲染为屏幕上的点精灵（公告板四边形）。
//  管线特性：
//    * CPU 侧展开公告板四边形 → 上传到动态 VB/IB
//    * 顶点着色器只做 ViewProjection 变换
//    * 像素着色器输出软圆形遮罩（AlphaBlend）
//    * 深度测试开启、深度写入关闭（保证与场景正确穿插）
//    * 支持可选粒子纹理（默认白色圆形）
// =============================================================================
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using YamaEngine.RHI;

namespace YamaEngine.Rendering
{
    /// <summary>
    /// 单个粒子数据（CPU → GPU 桥接格式）。
    /// </summary>
    public struct ParticleData
    {
        public Vector3 Position;
        public Vector3 Velocity;
        public float Size;
        public Vector4 Color;
        public float Life;      // 0.0 = 新生, 1.0 = 死亡
        public float MaxLife;
        public float Rotation;
    }

    /// <summary>
    /// GPU 粒子渲染器 — 将 FlowVFX 模拟的粒子数据渲染为屏幕上的点精灵。
    /// </summary>
    public class ParticleRenderer : IDisposable
    {
        private readonly RHIDevice _device;

        // 粒子渲染管线
        private RHIPipelineState? _particlePSO;
        private RHIRootSignature? _particleRootSignature;

        // GPU 缓冲区
        private RHIBuffer? _particleVB;
        private RHIBuffer? _particleIB;
        private RHIBuffer? _frameCB;
        private RHIBuffer? _emitterCB;
        private int _particleVertexCount;
        private int _particleIndexCount;

        // 粒子纹理（可选）
        private RHITexture? _particleTexture;
        private RHIDescriptorHandle _particleTextureSRV;
        private RHIDescriptorHeap? _srvHeap;

        // 着色器
        private RHIShaderBytecode? _particleVS;
        private RHIShaderBytecode? _particlePS;

        // 常量
        private const int ConstantBufferAlignment = 256;

        public ParticleRenderer(RHIDevice device)
        {
            _device = device;
        }

        /// <summary>
        /// 初始化粒子渲染管线：编译着色器、创建根签名、PSO、默认纹理。
        /// </summary>
        public void Initialize()
        {
            // ── 编译粒子着色器 ──
            _particleVS = _device.CompileShader(
                ParticleShaders.ParticleVertexShader, "main",
                RHIShaderStage.Vertex, RHIShaderModel.SM_5_1, "ParticleVS");

            _particlePS = _device.CompileShader(
                ParticleShaders.ParticlePixelShader, "main",
                RHIShaderStage.Pixel, RHIShaderModel.SM_5_1, "ParticlePS");

            // ── 创建根签名 ──
            // 参数0: CBV(b0) — FrameConstants (ViewProjection / CameraPos)
            // 参数1: CBV(b1) — EmitterConstants (EmitterWorld / ColorTint / GlobalSize)
            // 参数2: DescriptorTable(SRV t0) — ParticleTexture
            // 静态采样器 s0: LinearWrap
            var rootSigDesc = new RHIRootSignatureDesc
            {
                Parameters = new[]
                {
                    new RHIRootParameter
                    {
                        ParameterType = RHIRootParameterType.CBV,
                        ShaderVisibility = RHIShaderVisibility.All,
                        Descriptor = new RHIRootDescriptor { ShaderRegister = 0, RegisterSpace = 0 }
                    },
                    new RHIRootParameter
                    {
                        ParameterType = RHIRootParameterType.CBV,
                        ShaderVisibility = RHIShaderVisibility.All,
                        Descriptor = new RHIRootDescriptor { ShaderRegister = 1, RegisterSpace = 0 }
                    },
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
                                    NumDescriptors = 1,
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
                        AddressU = RHITextureAddressMode.Wrap,
                        AddressV = RHITextureAddressMode.Wrap,
                        AddressW = RHITextureAddressMode.Wrap,
                        ShaderRegister = 0,
                        RegisterSpace = 0,
                        ShaderVisibility = RHIShaderVisibility.Pixel
                    }
                },
                AllowInputAssembler = true,
                Flags = RHIRootSignatureFlags.AllowInputAssemblerInputLayout
            };
            _particleRootSignature = _device.CreateRootSignature(rootSigDesc, "ParticleRootSignature");

            // ── 创建 PSO ──
            // AlphaBlend, DepthRead, NoCull
            var psoDesc = new RHIGraphicsPipelineDesc
            {
                Name = "ParticlePSO",
                VS = _particleVS,
                PS = _particlePS,
                InputLayout = new[]
                {
                    new RHIInputElement("POSITION", RHIFormat.R32G32B32_Float, 0, 0),
                    new RHIInputElement("TEXCOORD", RHIFormat.R32G32_Float, 0, 12),
                    new RHIInputElement("COLOR", RHIFormat.R32G32B32A32_Float, 0, 20)
                },
                PrimitiveTopology = RHIPrimitiveTopology.TriangleList,
                BlendState = RHIBlendDesc.AlphaBlend,
                DepthStencilState = RHIDepthDesc.DepthRead,
                RasterizerState = RHIRasterizerDesc.NoCull,
                RTVFormats = new[] { RHIFormat.R8G8B8A8_UNorm },
                DSVFormat = RHIFormat.D32_Float,
                RootSignature = _particleRootSignature
            };
            _particlePSO = _device.CreateGraphicsPipelineState(psoDesc, "ParticlePSO");

            // ── 创建常量缓冲区 ──
            var cbDesc = new RHIBufferDesc
            {
                SizeInBytes = ConstantBufferAlignment,
                HeapType = RHIResourceHeapType.Default,
                Flags = RHIBufferFlags.ConstantBuffer,
                Stride = ConstantBufferAlignment
            };
            _frameCB = _device.CreateBuffer(cbDesc, "ParticleFrameCB");
            _frameCB.CurrentState = RHIResourceState.Common;

            _emitterCB = _device.CreateBuffer(cbDesc, "ParticleEmitterCB");
            _emitterCB.CurrentState = RHIResourceState.Common;

            // ── 创建默认粒子纹理（白色 1x1）──
            CreateDefaultParticleTexture();
        }

        /// <summary>
        /// 上传粒子数据到 GPU。每个粒子展开为 4 个顶点（公告板）。
        /// </summary>
        public void UploadParticles(IReadOnlyList<ParticleData> particles, Vector3 cameraRight, Vector3 cameraUp)
        {
            if (particles.Count == 0)
            {
                _particleVertexCount = 0;
                _particleIndexCount = 0;
                return;
            }

            // 构建四边形顶点数据
            byte[] vertexData = ParticleShaders.BuildParticleQuadBuffer(particles, cameraRight, cameraUp);
            byte[] indexData = ParticleShaders.BuildParticleIndexBuffer(particles.Count);

            int requiredVBSize = vertexData.Length;
            int requiredIBSize = indexData.Length;

            // 若缓冲区不足则重新创建
            if (_particleVB == null || (int)_particleVB.AllocatedBytes < requiredVBSize)
            {
                _particleVB?.Dispose();
                _particleVB = _device.CreateBuffer(new RHIBufferDesc
                {
                    SizeInBytes = (ulong)Math.Max(requiredVBSize, 1024),
                    HeapType = RHIResourceHeapType.Default,
                    Flags = RHIBufferFlags.VertexBuffer,
                    Stride = (ulong)ParticleShaders.ParticleVertexStride
                }, "ParticleVB");
                _particleVB.CurrentState = RHIResourceState.Common;
            }

            if (_particleIB == null || (int)_particleIB.AllocatedBytes < requiredIBSize)
            {
                _particleIB?.Dispose();
                _particleIB = _device.CreateBuffer(new RHIBufferDesc
                {
                    SizeInBytes = (ulong)Math.Max(requiredIBSize, 1024),
                    HeapType = RHIResourceHeapType.Default,
                    Flags = RHIBufferFlags.IndexBuffer,
                    Stride = sizeof(ushort)
                }, "ParticleIB");
                _particleIB.CurrentState = RHIResourceState.Common;
            }

            // 上传数据
            _device.UploadToBuffer(_particleVB, vertexData.AsSpan());
            _device.UploadToBuffer(_particleIB, indexData.AsSpan());

            _particleVertexCount = particles.Count * 4;
            _particleIndexCount = particles.Count * 6;
        }

        /// <summary>
        /// 渲染粒子。
        /// </summary>
        public void RenderParticles(RHICommandList cmd, in CameraData camera, Matrix4x4 emitterWorldMatrix)
        {
            if (_particlePSO == null || _particleRootSignature == null || _particleVertexCount == 0)
                return;

            // 更新并上传 FrameConstants
            var frameConstants = new ParticleShaders.ParticleFrameConstants
            {
                ViewProjectionMatrix = camera.ViewProjectionMatrix,
                CameraPosition = camera.Position
            };
            UploadConstantBuffer(cmd, _frameCB, frameConstants);

            // 更新并上传 EmitterConstants
            var emitterConstants = new ParticleShaders.ParticleEmitterConstants
            {
                EmitterWorldMatrix = emitterWorldMatrix,
                ParticleColorTint = new Vector4(1, 1, 1, 1),
                GlobalParticleSize = 1.0f
            };
            UploadConstantBuffer(cmd, _emitterCB, emitterConstants);

            // 绑定管线和根签名
            cmd.SetPipelineState(_particlePSO);
            cmd.SetGraphicsRootSignature(_particleRootSignature);

            // 绑定常量缓冲区
            cmd.SetGraphicsRootConstantBufferView(0, _frameCB);
            cmd.SetGraphicsRootConstantBufferView(1, _emitterCB);

            // 绑定纹理 SRV
            if (_srvHeap != null && _particleTextureSRV.IsValid)
            {
                cmd.SetGraphicsRootDescriptorTable(2, _particleTextureSRV);
            }

            // 绑定 VB/IB
            cmd.IASetPrimitiveTopology(RHIPrimitiveTopology.TriangleList);
            cmd.IASetVertexBuffers(0, _particleVB, ParticleShaders.ParticleVertexStride);
            cmd.IASetIndexBuffer(_particleIB, RHIFormat.R16_UInt);

            // 绘制
            cmd.DrawIndexedInstanced(_particleIndexCount, 1, 0, 0, 0);
        }

        /// <summary>
        /// 创建默认白色粒子纹理（1x1 RGBA8 UNorm）。
        /// </summary>
        private void CreateDefaultParticleTexture()
        {
            var texDesc = new RHITextureDesc
            {
                Dimension = RHIResourceDimension.Texture2D,
                Format = RHIFormat.R8G8B8A8_UNorm,
                Width = 1,
                Height = 1,
                DepthOrArraySize = 1,
                MipLevels = 1,
                SampleCount = 1,
                Flags = RHITextureFlags.ShaderResource
            };
            _particleTexture = _device.CreateTexture(texDesc, "DefaultParticleTexture");
            _particleTexture.CurrentState = RHIResourceState.Common;

            // 白色像素
            byte[] whitePixel = new byte[] { 255, 255, 255, 255 };
            _device.UploadToTexture(_particleTexture, whitePixel.AsSpan(), 1, 1);

            // 创建 SRV
            _srvHeap = _device.CreateDescriptorHeap(RHIDescriptorHeapType.CBV_SRV_UAV, 16, true);
            _particleTextureSRV = _device.CreateSRV(_particleTexture);
        }

        /// <summary>
        /// 将结构体数据上传到常量缓冲区（内部做 256 字节对齐）。
        /// </summary>
        private unsafe void UploadConstantBuffer<T>(RHICommandList cmd, RHIBuffer cb, in T data) where T : struct
        {
            int size = Marshal.SizeOf<T>();
            byte[] padded = new byte[ConstantBufferAlignment];
            fixed (byte* p = padded)
            {
                Marshal.StructureToPtr(data, (IntPtr)p, false);
            }
            _device.UploadToBuffer(cb, padded.AsSpan());
        }

        public void Dispose()
        {
            _particlePSO?.Dispose();
            _particleRootSignature?.Dispose();
            _particleVB?.Dispose();
            _particleIB?.Dispose();
            _frameCB?.Dispose();
            _emitterCB?.Dispose();
            _particleTexture?.Dispose();
            _srvHeap?.Dispose();
        }
    }
}
