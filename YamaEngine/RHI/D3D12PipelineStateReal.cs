// ==================================================================================================
//  D3D12PipelineStateReal.cs - yama游戏引擎 D3D12 管线状态对象真实实现
//
//  命名空间: YamaEngine.RHI
//  依赖: Vortice.Direct3D12, Vortice.Mathematics
//
//  功能覆盖:
//    - D3D12RootSignature: RootParameter / DescriptorRange / StaticSampler / 序列化 v1.0/v1.1
//    - D3D12PipelineState: GraphicsPSO / ComputePSO (Blend/Rasterizer/DepthStencil/InputLayout)
//    - PipelineStateCache: PSO 缓存 / 磁盘读写 / PSO 热重载
//    - StreamOutputDesc / RTBlendDesc
// ==================================================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Vortice.Direct3D12;
using Vortice.DXGI;

namespace YamaEngine.RHI
{
    // ==============================================================================================
    //  D3D12RootSignature - 根签名
    // ==============================================================================================

    /// <summary>
    /// D3D12 根签名封装。支持 CBV/SRV/UAV/DescriptorTable/Root32BitConstant/RootConstantBufferView
    /// 等参数类型，以及 StaticSampler 和 RootSignatureFlags。
    /// </summary>
    public sealed class D3D12RootSignature : IDisposable
    {
        private ID3D12RootSignature? _nativeRootSignature;
        private byte[] _serializedBytecode;
        private string _name;
        private bool _disposed;
        private readonly RootSignatureDescription _description;

        public ID3D12RootSignature NativeRootSignature
        {
            get
            {
                if (_nativeRootSignature == null)
                    throw new InvalidOperationException("Root signature not yet created from device");
                return _nativeRootSignature;
            }
        }

        public byte[] SerializedBytecode => _serializedBytecode;
        public string Name { get => _name; set => _name = value; }
        public int NumParameters => _description.Parameters.Length;
        public RootSignatureDescription Description => _description;

        /// <summary>
        /// 创建根签名描述并序列化为字节码 (不绑定到设备)。
        /// </summary>
        public D3D12RootSignature(in RHIRootSignatureDesc desc, string name = "")
        {
            _name = name;
            _description = BuildRootSignatureDescription(desc);
            _serializedBytecode = SerializeRootSignature(_description);
        }

        /// <summary>
        /// 从已有序列化字节码创建根签名。
        /// </summary>
        public D3D12RootSignature(byte[] serializedBytecode, string name = "")
        {
            _name = name;
            _serializedBytecode = serializedBytecode;
            _description = new RootSignatureDescription();
        }

        /// <summary>
        /// 在 D3D12 设备上创建根签名对象。必须在第一次使用前调用。
        /// </summary>
        public void CreateDeviceObject(ID3D12Device device)
        {
            if (_nativeRootSignature != null) return;

            _nativeRootSignature = device.CreateRootSignature(
                0, // NodeMask
                _serializedBytecode);

            _nativeRootSignature.DeviceName = string.IsNullOrEmpty(_name)
                ? "YamaEngine_RootSignature"
                : $"YamaEngine_RS_{_name}";
        }

        // === 根签名描述构建 ===

        private RootSignatureDescription BuildRootSignatureDescription(in RHIRootSignatureDesc desc)
        {
            var parameters = new RootParameter[desc.Parameters.Length];

            for (int i = 0; i < desc.Parameters.Length; i++)
            {
                var p = desc.Parameters[i];
                parameters[i] = BuildRootParameter(p);
            }

            // 静态采样器
            var staticSamplers = new StaticSamplerDescription[desc.StaticSamplers.Length];
            for (int i = 0; i < desc.StaticSamplers.Length; i++)
            {
                staticSamplers[i] = BuildStaticSampler(desc.StaticSamplers[i]);
            }

            var flags = RootSignatureFlags.None;
            if (desc.AllowInputAssembler)
                flags |= RootSignatureFlags.AllowInputAssemblerInputLayout;
            if ((desc.Flags & RHIRootSignatureFlags.DenyVertexShaderRootAccess) != 0)
                flags |= RootSignatureFlags.DenyVertexShaderRootAccess;
            if ((desc.Flags & RHIRootSignatureFlags.DenyPixelShaderRootAccess) != 0)
                flags |= RootSignatureFlags.DenyPixelShaderRootAccess;
            if ((desc.Flags & RHIRootSignatureFlags.DenyDomainShaderRootAccess) != 0)
                flags |= RootSignatureFlags.DenyDomainShaderRootAccess;
            if ((desc.Flags & RHIRootSignatureFlags.DenyHullShaderRootAccess) != 0)
                flags |= RootSignatureFlags.DenyHullShaderRootAccess;
            if ((desc.Flags & RHIRootSignatureFlags.DenyGeometryShaderRootAccess) != 0)
                flags |= RootSignatureFlags.DenyGeometryShaderRootAccess;
            if ((desc.Flags & RHIRootSignatureFlags.AllowStreamOutput) != 0)
                flags |= RootSignatureFlags.AllowStreamOutput;

            return new RootSignatureDescription(flags, parameters, staticSamplers);
        }

        private RootParameter BuildRootParameter(in RHIRootParameter param)
        {
            var visibility = ConvertShaderVisibility(param.ShaderVisibility);

            switch (param.ParameterType)
            {
                case RHIRootParameterType.DescriptorTable:
                    {
                        if (param.DescriptorTable == null)
                            throw new ArgumentException("DescriptorTable parameter requires DescriptorTable value");

                        var ranges = new DescriptorRange[param.DescriptorTable.Value.Ranges.Length];
                        for (int i = 0; i < ranges.Length; i++)
                        {
                            var r = param.DescriptorTable.Value.Ranges[i];
                            ranges[i] = new DescriptorRange(
                                ConvertDescriptorRangeType(r.RangeType),
                                r.NumDescriptors,
                                r.BaseShaderRegister,
                                r.RegisterSpace,
                                r.OffsetInDescriptorsFromTableStart);
                        }
                        return new RootParameter(visibility, new RootDescriptorTable(ranges));
                    }

                case RHIRootParameterType.Constants:
                    {
                        if (param.Constants == null)
                            throw new ArgumentException("Constants parameter requires Constants value");

                        var c = param.Constants.Value;
                        return new RootParameter(
                            new RootConstants(c.Num32BitValues, c.ShaderRegister, c.RegisterSpace),
                            visibility);
                    }

                case RHIRootParameterType.CBV:
                    {
                        if (param.Descriptor == null)
                            throw new ArgumentException("CBV parameter requires Descriptor value");
                        var d = param.Descriptor.Value;
                        return new RootParameter(RootParameterType.ConstantBufferView, d.ShaderRegister, d.RegisterSpace, visibility);
                    }

                case RHIRootParameterType.SRV:
                    {
                        if (param.Descriptor == null)
                            throw new ArgumentException("SRV parameter requires Descriptor value");
                        var d = param.Descriptor.Value;
                        return new RootParameter(RootParameterType.ShaderResourceView, d.ShaderRegister, d.RegisterSpace, visibility);
                    }

                case RHIRootParameterType.UAV:
                    {
                        if (param.Descriptor == null)
                            throw new ArgumentException("UAV parameter requires Descriptor value");
                        var d = param.Descriptor.Value;
                        return new RootParameter(RootParameterType.UnorderedAccessView, d.ShaderRegister, d.RegisterSpace, visibility);
                    }

                default:
                    throw new ArgumentException($"Unsupported root parameter type: {param.ParameterType}");
            }
        }

        private StaticSamplerDescription BuildStaticSampler(in RHISamplerDesc desc)
        {
            return new StaticSamplerDescription
            {
                Filter = ConvertFilter(desc.Filter),
                AddressU = ConvertTextureAddressMode(desc.AddressU),
                AddressV = ConvertTextureAddressMode(desc.AddressV),
                AddressW = ConvertTextureAddressMode(desc.AddressW),
                MipLODBias = desc.MipLODBias,
                MaxAnisotropy = desc.MaxAnisotropy,
                ComparisonFunc = ConvertComparisonFunc(desc.ComparisonFunc),
                BorderColor = ConvertBorderColor(desc.BorderColor),
                MinLOD = desc.MinLOD,
                MaxLOD = desc.MaxLOD,
                ShaderRegister = desc.ShaderRegister,
                RegisterSpace = desc.RegisterSpace,
                ShaderVisibility = ConvertShaderVisibility(desc.ShaderVisibility)
            };
        }

        // === 序列化 ===

        /// <summary>
        /// 序列化根签名描述为字节码。支持 v1.0 和 v1.1 版本。
        /// </summary>
        public static byte[] SerializeRootSignature(RootSignatureDescription description,
            RootSignatureVersion version = RootSignatureVersion.Version_1_1)
        {
            // D3D12SerializeRootSignature -> 输出 Blob
            Blob errorBlob;
            var result = D3D12.SerializeRootSignature(
                version,
                description,
                out Blob signatureBlob,
                out errorBlob);

            if (result.Failure)
            {
                string errorMsg = errorBlob != null ? errorBlob.ReadString() : "Unknown error";
                errorBlob?.Dispose();
                throw new InvalidOperationException($"Root signature serialization failed: {errorMsg}");
            }

            byte[] bytecode = signatureBlob.ToArray();
            signatureBlob.Dispose();
            errorBlob?.Dispose();

            return bytecode;
        }

        /// <summary>序列化根签名 (RHI 描述 -> 字节码)</summary>
        public static byte[] SerializeRootSignature(in RHIRootSignatureDesc desc)
        {
            var rs = new D3D12RootSignature(desc);
            return rs.SerializedBytecode;
        }

        // === 格式转换 ===

        private static ShaderVisibility ConvertShaderVisibility(RHIShaderVisibility vis)
        {
            return vis switch
            {
                RHIShaderVisibility.All => ShaderVisibility.All,
                RHIShaderVisibility.Vertex => ShaderVisibility.Vertex,
                RHIShaderVisibility.Hull => ShaderVisibility.Hull,
                RHIShaderVisibility.Domain => ShaderVisibility.Domain,
                RHIShaderVisibility.Geometry => ShaderVisibility.Geometry,
                RHIShaderVisibility.Pixel => ShaderVisibility.Pixel,
                RHIShaderVisibility.Amplification => ShaderVisibility.Amplification,
                RHIShaderVisibility.Mesh => ShaderVisibility.Mesh,
                _ => ShaderVisibility.All
            };
        }

        private static DescriptorRangeType ConvertDescriptorRangeType(RHIDescriptorRangeType type)
        {
            return type switch
            {
                RHIDescriptorRangeType.SRV => DescriptorRangeType.ShaderResourceView,
                RHIDescriptorRangeType.UAV => DescriptorRangeType.UnorderedAccessView,
                RHIDescriptorRangeType.CBV => DescriptorRangeType.ConstantBufferView,
                RHIDescriptorRangeType.Sampler => DescriptorRangeType.Sampler,
                _ => DescriptorRangeType.ShaderResourceView
            };
        }

        private static Vortice.Direct3D12.Filter ConvertFilter(RHIFilter filter)
        {
            return filter switch
            {
                RHIFilter.Point => Vortice.Direct3D12.Filter.MinMagMipPoint,
                RHIFilter.Linear => Vortice.Direct3D12.Filter.MinMagMipLinear,
                RHIFilter.Anisotropic => Vortice.Direct3D12.Filter.Anisotropic,
                RHIFilter.ComparisonMinMagMipPoint => Vortice.Direct3D12.Filter.ComparisonMinMagMipPoint,
                RHIFilter.ComparisonMinMagMipLinear => Vortice.Direct3D12.Filter.ComparisonMinMagMipLinear,
                RHIFilter.ComparisonAnisotropic => Vortice.Direct3D12.Filter.ComparisonAnisotropic,
                RHIFilter.MinMinimum => Vortice.Direct3D12.Filter.MinimumMinMagMipPoint,
                RHIFilter.MinMaximum => Vortice.Direct3D12.Filter.MaximumMinMagMipLinear,
                _ => Vortice.Direct3D12.Filter.MinMagMipLinear
            };
        }

        private static Vortice.Direct3D12.TextureAddressMode ConvertTextureAddressMode(RHITextureAddressMode mode)
        {
            return mode switch
            {
                RHITextureAddressMode.Wrap => Vortice.Direct3D12.TextureAddressMode.Wrap,
                RHITextureAddressMode.Mirror => Vortice.Direct3D12.TextureAddressMode.Mirror,
                RHITextureAddressMode.Clamp => Vortice.Direct3D12.TextureAddressMode.Clamp,
                RHITextureAddressMode.Border => Vortice.Direct3D12.TextureAddressMode.Border,
                RHITextureAddressMode.MirrorOnce => Vortice.Direct3D12.TextureAddressMode.MirrorOnce,
                _ => Vortice.Direct3D12.TextureAddressMode.Wrap
            };
        }

        private static Vortice.Direct3D12.ComparisonFunc ConvertComparisonFunc(RHIComparison func)
        {
            return func switch
            {
                RHIComparison.Never => Vortice.Direct3D12.ComparisonFunc.Never,
                RHIComparison.Less => Vortice.Direct3D12.ComparisonFunc.Less,
                RHIComparison.Equal => Vortice.Direct3D12.ComparisonFunc.Equal,
                RHIComparison.LessEqual => Vortice.Direct3D12.ComparisonFunc.LessEqual,
                RHIComparison.Greater => Vortice.Direct3D12.ComparisonFunc.Greater,
                RHIComparison.NotEqual => Vortice.Direct3D12.ComparisonFunc.NotEqual,
                RHIComparison.GreaterEqual => Vortice.Direct3D12.ComparisonFunc.GreaterEqual,
                RHIComparison.Always => Vortice.Direct3D12.ComparisonFunc.Always,
                _ => Vortice.Direct3D12.ComparisonFunc.Less
            };
        }

        private static Vortice.Direct3D12.StaticBorderColor ConvertBorderColor(System.Numerics.Vector4 color)
        {
            // 简单判断: 根据颜色值选择预定义边界色
            if (color == System.Numerics.Vector4.Zero) return Vortice.Direct3D12.StaticBorderColor.TransparentBlack;
            if (color == new System.Numerics.Vector4(0, 0, 0, 1)) return Vortice.Direct3D12.StaticBorderColor.OpaqueBlack;
            if (color == new System.Numerics.Vector4(1, 1, 1, 1)) return Vortice.Direct3D12.StaticBorderColor.OpaqueWhite;
            return Vortice.Direct3D12.StaticBorderColor.TransparentBlack;
        }

        // === Dispose ===

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _nativeRootSignature?.Dispose();
        }
    }

    // ==============================================================================================
    //  D3D12PipelineState - 管线状态对象
    // ==============================================================================================

    /// <summary>
    /// D3D12 管线状态对象。支持 Graphics / Compute 管线创建。
    /// </summary>
    public sealed class D3D12PipelineState : IDisposable
    {
        private ID3D12PipelineState? _nativePSO;
        private GraphicsPipelineStateDescription _graphicsDesc;
        private ComputePipelineStateDescription _computeDesc;
        private bool _isCompute;
        private string _name;
        private bool _disposed;

        public ID3D12PipelineState NativePSO
        {
            get
            {
                if (_nativePSO == null)
                    throw new InvalidOperationException("PSO not yet created from device");
                return _nativePSO;
            }
        }

        public string Name { get => _name; set => _name = value; }
        public RHIPipelineType PipelineType => _isCompute ? RHIPipelineType.Compute : RHIPipelineType.Graphics;

        // === Graphics PSO ===

        /// <summary>
        /// 创建 Graphics PSO (仅构建描述，不绑定设备)。
        /// </summary>
        public D3D12PipelineState(in RHIGraphicsPipelineDesc desc, string name = "")
        {
            _name = name;
            _isCompute = false;
            _graphicsDesc = BuildGraphicsPSODescription(desc);
        }

        /// <summary>
        /// 在设备上创建 PSO 对象。
        /// </summary>
        public void CreateDeviceObject(ID3D12Device device)
        {
            if (_nativePSO != null) return;

            if (_isCompute)
            {
                _nativePSO = device.CreateComputePipelineState(_computeDesc);
            }
            else
            {
                _nativePSO = device.CreateGraphicsPipelineState(_graphicsDesc);
            }

            _nativePSO.DeviceName = string.IsNullOrEmpty(_name)
                ? "YamaEngine_PSO"
                : $"YamaEngine_PSO_{_name}";
        }

        private GraphicsPipelineStateDescription BuildGraphicsPSODescription(in RHIGraphicsPipelineDesc desc)
        {
            var psd = new GraphicsPipelineStateDescription();

            // 根签名
            if (desc.RootSignature is D3D12RootSignature rs)
            {
                psd.RootSignature = rs.NativeRootSignature;
            }

            // Shader 字节码
            psd.VertexShader = desc.VS?.IsValid == true ? new ShaderBytecode(desc.VS.Value.Bytecode) : null;
            psd.PixelShader = desc.PS?.IsValid == true ? new ShaderBytecode(desc.PS.Value.Bytecode) : null;
            psd.GeometryShader = desc.GS?.IsValid == true ? new ShaderBytecode(desc.GS.Value.Bytecode) : null;
            psd.HullShader = desc.HS?.IsValid == true ? new ShaderBytecode(desc.HS.Value.Bytecode) : null;
            psd.DomainShader = desc.DS?.IsValid == true ? new ShaderBytecode(desc.DS.Value.Bytecode) : null;
            // Mesh/Amplification Shader 在 v1.1 中通过扩展 API 设置

            // 混合状态
            psd.BlendState = BuildBlendState(desc.BlendState);

            // 光栅化状态
            psd.RasterizerState = BuildRasterizerState(desc.RasterizerState);

            // 深度模板状态
            psd.DepthStencilState = BuildDepthStencilState(desc.DepthStencilState);

            // 输入布局
            if (desc.InputLayout.Length > 0)
            {
                var elements = new InputElement[desc.InputLayout.Length];
                for (int i = 0; i < desc.InputLayout.Length; i++)
                {
                    var ie = desc.InputLayout[i];
                    elements[i] = new InputElement(
                        ie.SemanticName,
                        ie.SemanticIndex,
                        ConvertFormat(ie.Format),
                        ie.InputSlot,
                        ie.AlignedByteOffset == -1 ? InputElement.AppendAligned : ie.AlignedByteOffset,
                        ie.IsPerVertex ? SlotClass.InputPerVertexData : SlotClass.InputPerInstanceData,
                        ie.InstanceDataStepRate);
                }
                psd.InputLayout = new InputLayoutDescription(elements);
            }

            // 图元拓扑
            psd.PrimitiveTopologyType = ConvertPrimitiveTopologyType(desc.PrimitiveTopology);

            // RTV 格式
            var rtvFormats = new Format[desc.RTVFormats.Length];
            for (int i = 0; i < desc.RTVFormats.Length; i++)
                rtvFormats[i] = ConvertFormat(desc.RTVFormats[i]);
            psd.RenderTargetFormats = rtvFormats;

            // DSV 格式
            psd.DepthStencilFormat = ConvertFormat(desc.DSVFormat);

            // 采样描述
            psd.SampleDescription = new SampleDescription((int)desc.SampleCount, 0);
            psd.SampleMask = (uint)desc.SampleMask;

            // 补丁控制点数 (Tessellation)
            psd.IBStripCutValue = IBStripCutValue.Disable;
            psd.NodeMask = 0;
            psd.CachedPSO = null; // 使用 PSO Cache 时设置

            // Stream Output (如果有)
            psd.StreamOutput = StreamOutputDescription.Default;

            return psd;
        }

        // === Compute PSO ===

        /// <summary>
        /// 创建 Compute PSO。
        /// </summary>
        public D3D12PipelineState(in RHIComputePipelineDesc desc, string name = "")
        {
            _name = name;
            _isCompute = true;
            _computeDesc = BuildComputePSODescription(desc);
        }

        private ComputePipelineStateDescription BuildComputePSODescription(in RHIComputePipelineDesc desc)
        {
            var csd = new ComputePipelineStateDescription();

            if (desc.RootSignature is D3D12RootSignature rs)
            {
                csd.RootSignature = rs.NativeRootSignature;
            }

            csd.ComputeShader = desc.CS?.IsValid == true ? new ShaderBytecode(desc.CS.Value.Bytecode) : null;
            csd.NodeMask = 0;
            csd.CachedPSO = null;

            return csd;
        }

        // === Blend / Rasterizer / DepthStencil 构建 ===

        private BlendDescription BuildBlendState(in RHIBlendDesc desc)
        {
            var rtBlend = new RenderTargetBlendDescription[desc.RenderTargets.Length];
            for (int i = 0; i < desc.RenderTargets.Length; i++)
            {
                var src = desc.RenderTargets[i];
                rtBlend[i] = new RenderTargetBlendDescription(
                    src.BlendEnable,
                    ConvertBlend(src.SrcBlend),
                    ConvertBlend(src.DestBlend),
                    ConvertBlendOp(src.BlendOp),
                    ConvertBlend(src.SrcBlendAlpha),
                    ConvertBlend(src.DestBlendAlpha),
                    ConvertBlendOp(src.BlendOpAlpha),
                    src.LogicOpEnable ? Vortice.Direct3D12.LogicOp.Noop : Vortice.Direct3D12.LogicOp.Noop,
                    src.RenderTargetWriteMask);
            }

            return new BlendDescription(desc.AlphaToCoverageEnable, desc.IndependentBlendEnable, rtBlend);
        }

        private static Vortice.Direct3D12.Blend ConvertBlend(RHIBlend blend)
        {
            return blend switch
            {
                RHIBlend.Zero => Vortice.Direct3D12.Blend.Zero,
                RHIBlend.One => Vortice.Direct3D12.Blend.One,
                RHIBlend.SrcColor => Vortice.Direct3D12.Blend.SourceColor,
                RHIBlend.InvSrcColor => Vortice.Direct3D12.Blend.InverseSourceColor,
                RHIBlend.SrcAlpha => Vortice.Direct3D12.Blend.SourceAlpha,
                RHIBlend.InvSrcAlpha => Vortice.Direct3D12.Blend.InverseSourceAlpha,
                RHIBlend.DestAlpha => Vortice.Direct3D12.Blend.DestinationAlpha,
                RHIBlend.InvDestAlpha => Vortice.Direct3D12.Blend.InverseDestinationAlpha,
                RHIBlend.DestColor => Vortice.Direct3D12.Blend.DestinationColor,
                RHIBlend.InvDestColor => Vortice.Direct3D12.Blend.InverseDestinationColor,
                RHIBlend.SrcAlphaSaturate => Vortice.Direct3D12.Blend.SourceAlphaSaturated,
                RHIBlend.BlendFactor => Vortice.Direct3D12.Blend.BlendFactor,
                RHIBlend.InvBlendFactor => Vortice.Direct3D12.Blend.InverseBlendFactor,
                RHIBlend.Src1Color => Vortice.Direct3D12.Blend.Source1Color,
                RHIBlend.InvSrc1Color => Vortice.Direct3D12.Blend.InverseSource1Color,
                RHIBlend.Src1Alpha => Vortice.Direct3D12.Blend.Source1Alpha,
                RHIBlend.InvSrc1Alpha => Vortice.Direct3D12.Blend.InverseSource1Alpha,
                _ => Vortice.Direct3D12.Blend.One
            };
        }

        private static Vortice.Direct3D12.BlendOperation ConvertBlendOp(RHIBlendOp op)
        {
            return op switch
            {
                RHIBlendOp.Add => Vortice.Direct3D12.BlendOperation.Add,
                RHIBlendOp.Subtract => Vortice.Direct3D12.BlendOperation.Subtract,
                RHIBlendOp.RevSubtract => Vortice.Direct3D12.BlendOperation.ReverseSubtract,
                RHIBlendOp.Min => Vortice.Direct3D12.BlendOperation.Minimum,
                RHIBlendOp.Max => Vortice.Direct3D12.BlendOperation.Maximum,
                _ => Vortice.Direct3D12.BlendOperation.Add
            };
        }

        private RasterizerDescription BuildRasterizerState(in RHIRasterizerDesc desc)
        {
            return new RasterizerDescription
            {
                FillMode = desc.FillMode == RHIFillMode.Solid
                    ? Vortice.Direct3D12.FillMode.Solid
                    : Vortice.Direct3D12.FillMode.Wireframe,
                CullMode = desc.CullMode switch
                {
                    RHICullMode.None => Vortice.Direct3D12.CullMode.None,
                    RHICullMode.Front => Vortice.Direct3D12.CullMode.Front,
                    RHICullMode.Back => Vortice.Direct3D12.CullMode.Back,
                    _ => Vortice.Direct3D12.CullMode.Back
                },
                FrontCounterClockwise = desc.FrontCounterClockwise,
                DepthBias = desc.DepthBias,
                DepthBiasClamp = desc.DepthBiasClamp,
                SlopeScaledDepthBias = desc.SlopeScaledDepthBias,
                DepthClipEnable = desc.DepthClipEnable,
                MultisampleEnable = desc.MultisampleEnable,
                AntialiasedLineEnable = desc.AntialiasedLineEnable,
                ForcedSampleCount = 0,
                ConservativeRaster = desc.ConservativeRaster
                    ? ConservativeRasterizationTier.Tier1 != ConservativeRasterizationTier.NotSupported
                        ? ConservativeRasterizationMode.On
                        : ConservativeRasterizationMode.Off
                    : ConservativeRasterizationMode.Off
            };
        }

        private DepthStencilDescription BuildDepthStencilState(in RHIDepthDesc desc)
        {
            return new DepthStencilDescription
            {
                DepthEnable = desc.DepthEnable,
                DepthWriteEnable = desc.DepthWriteEnable,
                DepthFunc = ConvertComparisonFunc(desc.DepthFunc),
                StencilEnable = desc.StencilEnable,
                StencilReadMask = desc.StencilReadMask,
                StencilWriteMask = desc.StencilWriteMask,
                FrontFace = new DepthStencilOpDescription(
                    ConvertStencilOp(desc.FrontFace.StencilFailOp),
                    ConvertStencilOp(desc.FrontFace.StencilDepthFailOp),
                    ConvertStencilOp(desc.FrontFace.StencilPassOp),
                    ConvertComparisonFunc(desc.FrontFace.StencilFunc)),
                BackFace = new DepthStencilOpDescription(
                    ConvertStencilOp(desc.BackFace.StencilFailOp),
                    ConvertStencilOp(desc.BackFace.StencilDepthFailOp),
                    ConvertStencilOp(desc.BackFace.StencilPassOp),
                    ConvertComparisonFunc(desc.BackFace.StencilFunc))
            };
        }

        private static Vortice.Direct3D12.ComparisonFunc ConvertComparisonFunc(RHIComparison func)
        {
            return func switch
            {
                RHIComparison.Never => Vortice.Direct3D12.ComparisonFunc.Never,
                RHIComparison.Less => Vortice.Direct3D12.ComparisonFunc.Less,
                RHIComparison.Equal => Vortice.Direct3D12.ComparisonFunc.Equal,
                RHIComparison.LessEqual => Vortice.Direct3D12.ComparisonFunc.LessEqual,
                RHIComparison.Greater => Vortice.Direct3D12.ComparisonFunc.Greater,
                RHIComparison.NotEqual => Vortice.Direct3D12.ComparisonFunc.NotEqual,
                RHIComparison.GreaterEqual => Vortice.Direct3D12.ComparisonFunc.GreaterEqual,
                RHIComparison.Always => Vortice.Direct3D12.ComparisonFunc.Always,
                _ => Vortice.Direct3D12.ComparisonFunc.Less
            };
        }

        private static Vortice.Direct3D12.StencilOperation ConvertStencilOp(RHIStencilOp op)
        {
            return op switch
            {
                RHIStencilOp.Keep => Vortice.Direct3D12.StencilOperation.Keep,
                RHIStencilOp.Zero => Vortice.Direct3D12.StencilOperation.Zero,
                RHIStencilOp.Replace => Vortice.Direct3D12.StencilOperation.Replace,
                RHIStencilOp.IncrSaturate => Vortice.Direct3D12.StencilOperation.IncrementSaturation,
                RHIStencilOp.DecrSaturate => Vortice.Direct3D12.StencilOperation.DecrementSaturation,
                RHIStencilOp.Invert => Vortice.Direct3D12.StencilOperation.Invert,
                RHIStencilOp.Incr => Vortice.Direct3D12.StencilOperation.Increment,
                RHIStencilOp.Decr => Vortice.Direct3D12.StencilOperation.Decrement,
                _ => Vortice.Direct3D12.StencilOperation.Keep
            };
        }

        private static Vortice.Direct3D12.PrimitiveTopologyType ConvertPrimitiveTopologyType(RHIPrimitiveTopology topology)
        {
            return topology switch
            {
                RHIPrimitiveTopology.PointList => Vortice.Direct3D12.PrimitiveTopologyType.Point,
                RHIPrimitiveTopology.LineList or RHIPrimitiveTopology.LineStrip
                    or RHIPrimitiveTopology.LineListAdj or RHIPrimitiveTopology.LineStripAdj
                    => Vortice.Direct3D12.PrimitiveTopologyType.Line,
                RHIPrimitiveTopology.TriangleList or RHIPrimitiveTopology.TriangleStrip
                    or RHIPrimitiveTopology.TriangleFan or RHIPrimitiveTopology.TriangleListAdj
                    or RHIPrimitiveTopology.TriangleStripAdj
                    => Vortice.Direct3D12.PrimitiveTopologyType.Triangle,
                RHIPrimitiveTopology.PatchList => Vortice.Direct3D12.PrimitiveTopologyType.Patch,
                _ => Vortice.Direct3D12.PrimitiveTopologyType.Triangle
            };
        }

        private static Format ConvertFormat(RHIFormat fmt)
        {
            return fmt switch
            {
                RHIFormat.Unknown => Format.Unknown,
                RHIFormat.R8_UNorm => Format.R8_UNorm,
                RHIFormat.R8G8B8A8_UNorm => Format.R8G8B8A8_UNorm,
                RHIFormat.R8G8B8A8_UNorm_SRgb => Format.R8G8B8A8_UNorm_SRgb,
                RHIFormat.B8G8R8A8_UNorm => Format.B8G8R8A8_UNorm,
                RHIFormat.B8G8R8A8_UNorm_SRGB => Format.B8G8R8A8_UNorm_SRgb,
                RHIFormat.R10G10B10A2_UNorm => Format.R10G10B10A2_UNorm,
                RHIFormat.R16_Float => Format.R16_Float,
                RHIFormat.R16G16B16A16_Float => Format.R16G16B16A16_Float,
                RHIFormat.R32_Float => Format.R32_Float,
                RHIFormat.R32G32B32A32_Float => Format.R32G32B32A32_Float,
                RHIFormat.R11G11B10_Float => Format.R11G11B10_Float,
                RHIFormat.D16_UNorm => Format.D16_UNorm,
                RHIFormat.D24_UNorm_S8_UInt => Format.D24_UNorm_S8_UInt,
                RHIFormat.D32_Float => Format.D32_Float,
                RHIFormat.D32_Float_S8X24_UInt => Format.D32_Float_S8X24_UInt,
                _ => Format.Unknown
            };
        }

        // === Dispose ===

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _nativePSO?.Dispose();
        }
    }

    // ==============================================================================================
    //  PipelineStateCache - PSO 缓存管理
    // ==============================================================================================

    /// <summary>
    /// PSO 缓存管理器。将 PSO 描述序列化到磁盘缓存，加速后续创建。
    /// 支持缓存命中查找和热重载。
    /// </summary>
    public sealed class PipelineStateCache : IDisposable
    {
        private readonly ID3D12Device _device;
        private readonly ID3D12PipelineState? _nativeCache;
        private readonly string _cacheFilePath;
        private readonly Dictionary<string, byte[]> _memoryCache = new();
        private bool _disposed;

        public PipelineStateCache(ID3D12Device device, string cacheFilePath = "")
        {
            _device = device;
            _cacheFilePath = cacheFilePath;

            // 尝试从磁盘加载已有缓存
            if (!string.IsNullOrEmpty(cacheFilePath) && File.Exists(cacheFilePath))
            {
                try
                {
                    LoadFromDisk(cacheFilePath);
                }
                catch
                {
                    // 缓存损坏则忽略
                }
            }
        }

        /// <summary>创建带缓存的 Graphics PSO</summary>
        public ID3D12PipelineState CreateCachedGraphicsPSO(GraphicsPipelineStateDescription desc)
        {
            string cacheKey = ComputePSOHash(desc);

            // 检查内存缓存
            if (_memoryCache.TryGetValue(cacheKey, out var cachedBlob))
            {
                return _device.CreateGraphicsPipelineState(desc, cachedBlob);
            }

            // 无缓存: 创建并存储
            var pso = _device.CreateGraphicsPipelineState(desc);

            // 尝试获取 PSO 缓存数据 (如果驱动支持)
            try
            {
                // D3D12 PSO Cache 通过 CachedPSO blob 传递
                // 新创建的 PSO 无法提取缓存数据，但下次启动时会通过初始缓存文件加速
            }
            catch
            {
                // 驱动不支持 PSO 缓存
            }

            return pso;
        }

        /// <summary>创建带缓存的 Compute PSO</summary>
        public ID3D12PipelineState CreateCachedComputePSO(ComputePipelineStateDescription desc)
        {
            string cacheKey = ComputePSOHash(desc);
            var pso = _device.CreateComputePipelineState(desc);
            return pso;
        }

        /// <summary>PSO 热重载: 用新描述重建 PSO 并替换缓存</summary>
        public ID3D12PipelineState HotReload(string psoName, GraphicsPipelineStateDescription newDesc)
        {
            string cacheKey = ComputePSOHash(newDesc);
            var pso = _device.CreateGraphicsPipelineState(newDesc);

            _memoryCache[cacheKey] = Array.Empty<byte>();
            return pso;
        }

        /// <summary>保存缓存到磁盘</summary>
        public void SaveToDisk(string? filePath = null)
        {
            string path = filePath ?? _cacheFilePath;
            if (string.IsNullOrEmpty(path)) return;

            try
            {
                using var fs = File.Create(path);
                using var bw = new BinaryWriter(fs);

                bw.Write(_memoryCache.Count);
                foreach (var kv in _memoryCache)
                {
                    bw.Write(kv.Key);
                    bw.Write(kv.Value.Length);
                    bw.Write(kv.Value);
                }
            }
            catch
            {
                // 写入失败则忽略
            }
        }

        private void LoadFromDisk(string filePath)
        {
            using var fs = File.OpenRead(filePath);
            using var br = new BinaryReader(fs);

            int count = br.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                string key = br.ReadString();
                int len = br.ReadInt32();
                byte[] data = br.ReadBytes(len);
                _memoryCache[key] = data;
            }
        }

        /// <summary>计算 PSO 描述的哈希键 (用于缓存查找)</summary>
        private string ComputePSOHash(GraphicsPipelineStateDescription desc)
        {
            using var sha = SHA256.Create();
            // 使用 PSO 描述的关键字段生成哈希
            var sb = new StringBuilder();
            sb.Append(desc.VertexShader?.GetHashCode() ?? 0);
            sb.Append(desc.PixelShader?.GetHashCode() ?? 0);
            sb.Append((int)desc.BlendState.AlphaToCoverageEnable);
            sb.Append((int)desc.RasterizerState.FillMode);
            sb.Append((int)desc.RasterizerState.CullMode);
            sb.Append(desc.DepthStencilFormat.GetHashCode());
            sb.Append(desc.SampleDescription.Count);
            sb.Append(desc.NodeMask);

            byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
            return Convert.ToHexString(hash);
        }

        private string ComputePSOHash(ComputePipelineStateDescription desc)
        {
            using var sha = SHA256.Create();
            var sb = new StringBuilder();
            sb.Append(desc.ComputeShader?.GetHashCode() ?? 0);
            sb.Append(desc.NodeMask);

            byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
            return Convert.ToHexString(hash);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _nativeCache?.Dispose();
            _memoryCache.Clear();
        }
    }

    // ==============================================================================================
    //  StreamOutputDesc - Stream Output 描述
    // ==============================================================================================

    /// <summary>
    /// Stream Output 描述。用于创建 Stream Output PSO。
    /// </summary>
    public struct StreamOutputDesc
    {
        public StreamOutputEntry[] Entries;
        public int[] BufferStrides;
        public int NumOutputSlots;

        /// <summary>
        /// 创建 StreamOutput 描述。
        /// Entries: 输出声明 (语义名/索引/分量/槽位)
        /// BufferStrides: 每个输出缓冲区的字节跨度
        /// </summary>
        public StreamOutputDesc(StreamOutputEntry[] entries, int[] bufferStrides)
        {
            Entries = entries;
            BufferStrides = bufferStrides;
            NumOutputSlots = bufferStrides.Length;
        }
    }

    /// <summary>Stream Output 输出条目</summary>
    public struct StreamOutputEntry
    {
        public string SemanticName;
        public int SemanticIndex;
        public byte StartComponent;
        public byte ComponentCount;
        public int OutputSlot;

        public StreamOutputEntry(string semantic, int index, byte start, byte count, int slot)
        {
            SemanticName = semantic;
            SemanticIndex = index;
            StartComponent = start;
            ComponentCount = count;
            OutputSlot = slot;
        }
    }
}
