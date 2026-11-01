using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;

namespace YamaEngine.RHI
{
    // ──────────────────────────────────────────────────────────────
    // Enums
    // ──────────────────────────────────────────────────────────────

    public enum RHIFormat
    {
        Unknown = 0,
        R8_UNorm, R8_SNorm, R8_UInt, R8_SInt,
        R8G8_UNorm, R8G8_SNorm, R8G8_UInt, R8G8_SInt,
        R8G8B8A8_UNorm, R8G8B8A8_SNorm, R8G8B8A8_UInt, R8G8B8A8_SInt,
        B8G8R8A8_UNorm, B8G8R8A8_UNorm_SRGB,
        // Vulkan 兼容命名别名（Swapchain 等用）
        B8G8R8A8Srgb = B8G8R8A8_UNorm_SRGB,
        R10G10B10A2_UNorm, R10G10B10A2_UInt,
        R16_Float, R16_UNorm, R16_SNorm, R16_UInt, R16_SInt,
        R16G16_Float, R16G16_UNorm, R16G16_SNorm, R16G16_UInt, R16G16_SInt,
        R16G16B16A16_Float, R16G16B16A16_UNorm, R16G16B16A16_SNorm, R16G16B16A16_UInt, R16G16B16A16_SInt,
        R32_Float, R32_UInt, R32_SInt,
        R32G32_Float, R32G32_UInt, R32G32_SInt,
        R32G32B32_Float, R32G32B32_UInt, R32G32B32_SInt,
        R32G32B32A32_Float, R32G32B32A32_UInt, R32G32B32A32_SInt,
        R11G11B10_Float,
        D16_UNorm, D24_UNorm_S8_UInt, D32_Float, D32_Float_S8X24_UInt,
        BC1_UNorm, BC1_UNorm_SRGB, BC2_UNorm, BC2_UNorm_SRGB,
        BC3_UNorm, BC3_UNorm_SRGB, BC4_UNorm, BC4_SNorm,
        BC5_UNorm, BC5_SNorm, BC6H_UF16, BC6H_SF16,
        BC7_UNorm, BC7_UNorm_SRGB,
        // Aliases for D3D12 compatibility (SRgb casing + missing formats)
        R8G8B8A8_UNorm_SRgb = R8G8B8A8_UNorm,
        B8G8R8A8_UNorm_SRgb = B8G8R8A8_UNorm_SRGB,
        BC1_UNorm_SRgb = BC1_UNorm_SRGB,
        BC2_UNorm_SRgb = BC2_UNorm_SRGB,
        BC3_UNorm_SRgb = BC3_UNorm_SRGB,
        BC7_UNorm_SRgb = BC7_UNorm_SRGB,
        R9G9B9E5_SharedExp,
        BC4_SNorm_BC5_SNorm, // placeholder to maintain sequential numbering
    }

    public static class RHIFormatUtils
    {
        public static int BytesPerPixel(RHIFormat fmt)
        {
            return fmt switch
            {
                RHIFormat.R8_UNorm or RHIFormat.R8_SNorm or RHIFormat.R8_UInt or RHIFormat.R8_SInt => 1,
                RHIFormat.R8G8_UNorm or RHIFormat.R8G8_SNorm or RHIFormat.R8G8_UInt or RHIFormat.R8G8_SInt => 2,
                RHIFormat.R8G8B8A8_UNorm or RHIFormat.R8G8B8A8_SNorm or RHIFormat.R8G8B8A8_UInt or RHIFormat.R8G8B8A8_SInt => 4,
                RHIFormat.B8G8R8A8_UNorm or RHIFormat.B8G8R8A8_UNorm_SRGB => 4,
                RHIFormat.R10G10B10A2_UNorm or RHIFormat.R10G10B10A2_UInt => 4,
                RHIFormat.R16_Float or RHIFormat.R16_UNorm or RHIFormat.R16_SNorm or RHIFormat.R16_UInt or RHIFormat.R16_SInt => 2,
                RHIFormat.R16G16_Float or RHIFormat.R16G16_UNorm or RHIFormat.R16G16_SNorm or RHIFormat.R16G16_UInt or RHIFormat.R16G16_SInt => 4,
                RHIFormat.R16G16B16A16_Float or RHIFormat.R16G16B16A16_UNorm or RHIFormat.R16G16B16A16_SNorm or RHIFormat.R16G16B16A16_UInt or RHIFormat.R16G16B16A16_SInt => 8,
                RHIFormat.R32_Float or RHIFormat.R32_UInt or RHIFormat.R32_SInt => 4,
                RHIFormat.R32G32_Float or RHIFormat.R32G32_UInt or RHIFormat.R32G32_SInt => 8,
                RHIFormat.R32G32B32_Float or RHIFormat.R32G32B32_UInt or RHIFormat.R32G32B32_SInt => 12,
                RHIFormat.R32G32B32A32_Float or RHIFormat.R32G32B32A32_UInt or RHIFormat.R32G32B32A32_SInt => 16,
                RHIFormat.R11G11B10_Float => 4,
                RHIFormat.D16_UNorm => 2,
                RHIFormat.D24_UNorm_S8_UInt => 4,
                RHIFormat.D32_Float => 4,
                RHIFormat.D32_Float_S8X24_UInt => 5,
                _ => 0
            };
        }

        public static bool IsDepthFormat(RHIFormat fmt) =>
            fmt == RHIFormat.D16_UNorm || fmt == RHIFormat.D24_UNorm_S8_UInt ||
            fmt == RHIFormat.D32_Float || fmt == RHIFormat.D32_Float_S8X24_UInt;

        public static bool IsCompressed(RHIFormat fmt) =>
            fmt >= RHIFormat.BC1_UNorm && fmt <= RHIFormat.BC7_UNorm_SRGB;

        public static bool IsSRGB(RHIFormat fmt) =>
            fmt == RHIFormat.B8G8R8A8_UNorm_SRGB || fmt == RHIFormat.BC1_UNorm_SRGB ||
            fmt == RHIFormat.BC2_UNorm_SRGB || fmt == RHIFormat.BC3_UNorm_SRGB ||
            fmt == RHIFormat.BC7_UNorm_SRGB;
    }

    public enum RHIBlend
    {
        Zero, One,
        SrcColor, InvSrcColor, SrcAlpha, InvSrcAlpha,
        DestAlpha, InvDestAlpha, DestColor, InvDestColor,
        SrcAlphaSaturate, BlendFactor, InvBlendFactor,
        Src1Color, InvSrc1Color, Src1Alpha, InvSrc1Alpha
    }

    public enum RHIBlendOp { Add, Subtract, RevSubtract, Min, Max }

    /// <summary>颜色写入掩码（D3D12 兼容）。</summary>
    [Flags]
    public enum RHIColorWriteMask : byte
    {
        None = 0,
        Red = 1,
        Green = 2,
        Blue = 4,
        Alpha = 8,
        All = Red | Green | Blue | Alpha,
    }

    public enum RHIComparison { Never, Less, Equal, LessEqual, Greater, NotEqual, GreaterEqual, Always }

    public enum RHIStencilOp { Keep, Zero, Replace, IncrSaturate, DecrSaturate, Invert, Incr, Decr }

    public enum RHICullMode { None, Front, Back }

    public enum RHIFillMode { Solid, Wireframe }

    public enum RHIPrimitiveTopology
    {
        PointList, LineList, LineStrip,
        TriangleList, TriangleStrip, TriangleFan,
        PatchList,
        // D3D12 adjacency variants
        LineListAdj, LineStripAdj, TriangleListAdj, TriangleStripAdj
    }

    public enum RHIShaderStage
    {
        Vertex = 1, Hull = 2, Domain = 4, Geometry = 8,
        Pixel = 16, Compute = 32, Mesh = 64, Amplification = 128,
        AllGraphics = 31, All = 0xFF,
        Library = 256
    }

    public enum RHIShaderModel
    {
        SM_5_1, SM_6_0, SM_6_1, SM_6_2, SM_6_3, SM_6_4, SM_6_5, SM_6_6, SM_6_7, SM_6_8,
        // 兼容别名（无下划线命名）
        SM5_0 = SM_5_1,
        // 5_0 别名（RHI 支持的最低模型为 5_1，5_0 视为等价）
        SM_5_0 = SM_5_1,
        SM5_1 = SM_5_1,
        SM6_0 = SM_6_0,
        SM6_1 = SM_6_1,
        SM6_2 = SM_6_2,
        SM6_3 = SM_6_3,
        SM6_4 = SM_6_4,
        SM6_5 = SM_6_5,
        SM6_6 = SM_6_6,
        SM6_7 = SM_6_7,
        SM6_8 = SM_6_8,
    }

    public enum RHIResourceHeapType { Default, Upload, Readback }

    // RHIHeapType 已移除 — 使用 global using 别名指向 RHIResourceHeapType

    /// <summary>Alias for RHIComparison (D3D12 compatibility)</summary>
    public enum RHIComparisonFunc { Never, Less, Equal, LessEqual, Greater, NotEqual, GreaterEqual, Always }

    /// <summary>Primitive topology type for pipeline state</summary>
    public enum RHIPrimitiveTopologyType { Undefined, Point, Line, Triangle, Patch }

    /// <summary>Border color for samplers</summary>
    public enum RHIBorderColor { TransparentBlack, OpaqueBlack, OpaqueWhite }

    public enum RHIResourceState
    {
        Common = 0,
        VertexAndConstantBuffer = 1,
        IndexBuffer = 2,
        RenderTarget = 4,
        UnorderedAccess = 8,
        DepthWrite = 16,
        DepthRead = 32,
        // 兼容别名
        DepthStencilWrite = DepthWrite,
        DepthStencilRead = DepthRead,
        NonPixelShaderResource = 64,
        PixelShaderResource = 128,
        ShaderResource = 192,
        StreamOut = 256,
        IndirectArgument = 512,
        CopyDest = 1024,
        CopySource = 2048,
        ResolveDest = 4096,
        ResolveSource = 8192,
        GenericRead = 2755,
        Present = 16384,
        RaytracingAccelerationStructure = 32768,
        // 生命周期状态（RHIResourceTracker 等使用）
        Active = 1 << 30,
        Destroyed = 1 << 29,
        PendingDestroy = 1 << 28,
    }

    public enum RHIDescriptorHeapType { CBV_SRV_UAV, Sampler, RTV, DSV }

    public enum RHIQueueType { Graphics, Compute, Copy }

    public enum RHIFilter
    {
        Point, Linear, Anisotropic,
        ComparisonMinMagMipPoint, ComparisonMinMagMipLinear,
        ComparisonAnisotropic, MinMinimum, MinMaximum,
        // Aliases for D3D12 compatibility
        MinMagMipPoint = Point,
        MinMagMipLinear = Linear,
        MinimumMinMagMipLinear = MinMinimum,
        MaximumMinMagMipLinear = MinMaximum
    }

    public enum RHITextureAddressMode { Wrap, Mirror, Clamp, Border, MirrorOnce }

    public enum RHIResourceDimension { Buffer, Texture1D, Texture2D, Texture3D }

    /// <summary>纹理类型（高层创建纹理时使用，语义对应 <see cref="RHIResourceDimension"/>）。</summary>
    public enum RHITextureType
    {
        TextureUnknown = 0,
        Texture1D = 1,
        Texture2D = 2,
        Texture3D = 3,
        TextureCube = 4,
        Texture1DArray = 5,
        Texture2DArray = 6,
        TextureCubeArray = 7,
    }

    // RHITextureDimension 已移除 — 使用 global using 别名指向 RHIResourceDimension

    public enum RHIPipelineType { Graphics, Compute, Mesh, RayTracing }

    /// <summary>Root signature flags (D3D12 compatibility)</summary>
    [Flags]
    public enum RHIRootSignatureFlags : uint
    {
        None = 0,
        AllowInputAssemblerInputLayout = 1,
        DenyVertexShaderRootAccess = 2,
        DenyHullShaderRootAccess = 4,
        DenyDomainShaderRootAccess = 8,
        DenyGeometryShaderRootAccess = 16,
        DenyPixelShaderRootAccess = 32,
        AllowStreamOutput = 64,
        LocalRootSignature = 128,
        DenyAmplificationShaderRootAccess = 256,
        DenyMeshShaderRootAccess = 512
    }

    /// <summary>Input classification for vertex layout (D3D12 compatibility)</summary>
    public enum RHIInputClassification { PerVertexData, PerInstanceData }

    /// <summary>Depth write mask (D3D12 compatibility)</summary>
    public enum RHIDepthWriteMask { Zero, All }

    // ──────────────────────────────────────────────────────────────
    // Descriptor structures
    // ──────────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    public struct RHIBlendDesc
    {
        public bool AlphaToCoverageEnable;
        public bool IndependentBlendEnable;
        public RHIRenderTargetBlend[] RenderTargets;

        public static RHIBlendDesc Default => new()
        {
            AlphaToCoverageEnable = false,
            IndependentBlendEnable = false,
            RenderTargets = new[] { RHIRenderTargetBlend.Disabled }
        };

        public static RHIBlendDesc AdditiveBlend => new()
        {
            AlphaToCoverageEnable = false,
            IndependentBlendEnable = false,
            RenderTargets = new[] { new RHIRenderTargetBlend
            {
                BlendEnable = true,
                SrcBlend = RHIBlend.SrcAlpha,
                DestBlend = RHIBlend.One,
                BlendOp = RHIBlendOp.Add,
                SrcBlendAlpha = RHIBlend.SrcAlpha,
                DestBlendAlpha = RHIBlend.One,
                BlendOpAlpha = RHIBlendOp.Add,
                RenderTargetWriteMask = 0xF
            }}
        };

        public static RHIBlendDesc AlphaBlend => new()
        {
            AlphaToCoverageEnable = false,
            IndependentBlendEnable = false,
            RenderTargets = new[] { new RHIRenderTargetBlend
            {
                BlendEnable = true,
                SrcBlend = RHIBlend.SrcAlpha,
                DestBlend = RHIBlend.InvSrcAlpha,
                BlendOp = RHIBlendOp.Add,
                SrcBlendAlpha = RHIBlend.One,
                DestBlendAlpha = RHIBlend.InvSrcAlpha,
                BlendOpAlpha = RHIBlendOp.Add,
                RenderTargetWriteMask = 0xF
            }}
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RHIRenderTargetBlend
    {
        public bool BlendEnable;
        public bool LogicOpEnable;
        public RHIBlend SrcBlend;
        public RHIBlend DestBlend;
        public RHIBlendOp BlendOp;
        public RHIBlend SrcBlendAlpha;
        public RHIBlend DestBlendAlpha;
        public RHIBlendOp BlendOpAlpha;
        public byte RenderTargetWriteMask;

        public static RHIRenderTargetBlend Disabled => new()
        {
            BlendEnable = false,
            SrcBlend = RHIBlend.One,
            DestBlend = RHIBlend.Zero,
            BlendOp = RHIBlendOp.Add,
            SrcBlendAlpha = RHIBlend.One,
            DestBlendAlpha = RHIBlend.Zero,
            BlendOpAlpha = RHIBlendOp.Add,
            RenderTargetWriteMask = 0xF
        };

        public static RHIRenderTargetBlend Opaque => Disabled;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RHIDepthStencilDesc
    {
        public bool DepthEnable;
        public bool DepthWriteEnable;
        public RHIComparison DepthFunc;
        public bool StencilEnable;
        public byte StencilReadMask;
        public byte StencilWriteMask;
        public RHIDepthStencilOp FrontFace;
        public RHIDepthStencilOp BackFace;

        public static RHIDepthDesc Default => new()
        {
            DepthEnable = true,
            DepthWriteEnable = true,
            DepthFunc = RHIComparison.Less,
            StencilEnable = false,
            StencilReadMask = 0xFF,
            StencilWriteMask = 0xFF,
            FrontFace = RHIDepthStencilOp.Default,
            BackFace = RHIDepthStencilOp.Default
        };

        public static RHIDepthDesc None => new()
        {
            DepthEnable = false,
            DepthWriteEnable = false,
            DepthFunc = RHIComparison.Always,
            StencilEnable = false
        };

        public static RHIDepthDesc DepthRead => new()
        {
            DepthEnable = true,
            DepthWriteEnable = false,
            DepthFunc = RHIComparison.LessEqual,
            StencilEnable = false
        };

        public static RHIDepthDesc DepthWrite => new()
        {
            DepthEnable = true,
            DepthWriteEnable = true,
            DepthFunc = RHIComparison.Less,
            StencilEnable = false
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RHIDepthStencilOp
    {
        public RHIStencilOp StencilFailOp;
        public RHIStencilOp StencilDepthFailOp;
        public RHIStencilOp StencilPassOp;
        public RHIComparison StencilFunc;

        public static RHIDepthStencilOp Default => new()
        {
            StencilFailOp = RHIStencilOp.Keep,
            StencilDepthFailOp = RHIStencilOp.Keep,
            StencilPassOp = RHIStencilOp.Keep,
            StencilFunc = RHIComparison.Always
        };
    }

    // Alias for convenience (field name mismatch above)
    public struct RHIDepthDesc
    {
        public bool DepthEnable;
        public bool DepthWriteEnable;
        public RHIComparison DepthFunc;
        public bool StencilEnable;
        public byte StencilReadMask;
        public byte StencilWriteMask;
        public RHIDepthStencilOp FrontFace;
        public RHIDepthStencilOp BackFace;

        public static RHIDepthDesc Default => new()
        {
            DepthEnable = true, DepthWriteEnable = true,
            DepthFunc = RHIComparison.Less, StencilEnable = false,
            StencilReadMask = 0xFF, StencilWriteMask = 0xFF,
            FrontFace = RHIDepthStencilOp.Default, BackFace = RHIDepthStencilOp.Default
        };
        public static RHIDepthDesc None => new() { DepthEnable = false, DepthWriteEnable = false, DepthFunc = RHIComparison.Always, StencilEnable = false };
        public static RHIDepthDesc DepthRead => new() { DepthEnable = true, DepthWriteEnable = false, DepthFunc = RHIComparison.LessEqual, StencilEnable = false };
        public static RHIDepthDesc DepthWrite => new() { DepthEnable = true, DepthWriteEnable = true, DepthFunc = RHIComparison.Less, StencilEnable = false };
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RHIRasterizerDesc
    {
        public RHIFillMode FillMode;
        public RHICullMode CullMode;
        public bool FrontCounterClockwise;
        public int DepthBias;
        public float DepthBiasClamp;
        public float SlopeScaledDepthBias;
        public bool DepthClipEnable;
        public bool MultisampleEnable;
        public bool AntialiasedLineEnable;
        public bool ConservativeRaster;

        public static RHIRasterizerDesc Default => new()
        {
            FillMode = RHIFillMode.Solid, CullMode = RHICullMode.Back,
            FrontCounterClockwise = true, DepthBias = 0, DepthBiasClamp = 0,
            SlopeScaledDepthBias = 0, DepthClipEnable = true,
            MultisampleEnable = false, AntialiasedLineEnable = false,
            ConservativeRaster = false
        };

        public static RHIRasterizerDesc NoCull => new()
        {
            FillMode = RHIFillMode.Solid, CullMode = RHICullMode.None,
            FrontCounterClockwise = true, DepthClipEnable = true
        };

        public static RHIRasterizerDesc Wireframe => new()
        {
            FillMode = RHIFillMode.Wireframe, CullMode = RHICullMode.None,
            FrontCounterClockwise = true, DepthClipEnable = true
        };

        public static RHIRasterizerDesc Shadow => new()
        {
            FillMode = RHIFillMode.Solid, CullMode = RHICullMode.Front,
            FrontCounterClockwise = true, DepthBias = 100000,
            DepthBiasClamp = 0, SlopeScaledDepthBias = 1.5f,
            DepthClipEnable = false
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RHISamplerDesc
    {
        public RHIFilter Filter;
        public RHITextureAddressMode AddressU;
        public RHITextureAddressMode AddressV;
        public RHITextureAddressMode AddressW;
        public float MipLODBias;
        public int MaxAnisotropy;
        public RHIComparison ComparisonFunc;
        public Vector4 BorderColor;
        public float MinLOD;
        public float MaxLOD;

        /// <summary>是否为阴影比较采样器（高层便捷标志。配合 ComparisonFunc 使用）。</summary>
        public bool ShadowComparison;

        // 静态采样器绑定信息（用于根签名中的内联静态采样器）
        public uint ShaderRegister;
        public uint RegisterSpace;
        public RHIShaderVisibility ShaderVisibility;

            // ── 高层兼容别名（RHIDeviceExtensions 使用的类高端命名）──
            public float MinLod { get => MinLOD; set => MinLOD = value; }
            public float MaxLod { get => MaxLOD; set => MaxLOD = value; }
            public RHICompareFunction CompareFunction
            {
                get => (RHICompareFunction)ComparisonFunc;
                set => ComparisonFunc = (RHIComparison)value;
            }
            public string? Name;

        public static readonly RHISamplerDesc LinearClamp = new()
        {
            Filter = RHIFilter.Linear, AddressU = RHITextureAddressMode.Clamp,
            AddressV = RHITextureAddressMode.Clamp, AddressW = RHITextureAddressMode.Clamp,
            MipLODBias = 0, MaxAnisotropy = 1, ComparisonFunc = RHIComparison.Never,
            BorderColor = Vector4.Zero, MinLOD = float.MinValue, MaxLOD = float.MaxValue
        };

        public static RHISamplerDesc LinearWrap => new()
        {
            Filter = RHIFilter.Linear, AddressU = RHITextureAddressMode.Wrap,
            AddressV = RHITextureAddressMode.Wrap, AddressW = RHITextureAddressMode.Wrap,
            MipLODBias = 0, MaxAnisotropy = 1, ComparisonFunc = RHIComparison.Never,
            BorderColor = Vector4.Zero, MinLOD = float.MinValue, MaxLOD = float.MaxValue
        };

        public static RHISamplerDesc AnisotropicWrap => new()
        {
            Filter = RHIFilter.Anisotropic, AddressU = RHITextureAddressMode.Wrap,
            AddressV = RHITextureAddressMode.Wrap, AddressW = RHITextureAddressMode.Wrap,
            MipLODBias = 0, MaxAnisotropy = 16, ComparisonFunc = RHIComparison.Never,
            BorderColor = Vector4.Zero, MinLOD = float.MinValue, MaxLOD = float.MaxValue
        };

        public static RHISamplerDesc PointClamp => new()
        {
            Filter = RHIFilter.Point, AddressU = RHITextureAddressMode.Clamp,
            AddressV = RHITextureAddressMode.Clamp, AddressW = RHITextureAddressMode.Clamp,
            MipLODBias = 0, MaxAnisotropy = 1, ComparisonFunc = RHIComparison.Never,
            BorderColor = Vector4.Zero, MinLOD = float.MinValue, MaxLOD = float.MaxValue
        };

        public static RHISamplerDesc ShadowSampler => new()
        {
            Filter = RHIFilter.ComparisonMinMagMipLinear,
            AddressU = RHITextureAddressMode.Clamp,
            AddressV = RHITextureAddressMode.Clamp,
            AddressW = RHITextureAddressMode.Clamp,
            MipLODBias = 0, MaxAnisotropy = 1,
            ComparisonFunc = RHIComparison.LessEqual,
            BorderColor = Vector4.Zero, MinLOD = float.MinValue, MaxLOD = float.MaxValue
        };
    }

    // ──────────────────────────────────────────────────────────────
    // Resource descriptions
    // ──────────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    public struct RHIBufferDesc
    {
        public ulong SizeInBytes;
        public RHIFormat Format;
        public RHIResourceHeapType HeapType;
        public RHIBufferFlags Flags;
        public ulong Stride;

        // ── 高层兼容别名（RHIDeviceExtensions 及渲染管线使用的类 BGRA/D3D12 命名）──
        public ulong Size
        {
            get => SizeInBytes;
            set => SizeInBytes = value;
        }
        public RHIBufferUsage Usage
        {
            get => (RHIBufferUsage)(uint)Flags;
            set => Flags = (RHIBufferFlags)(uint)value;
        }
        public RHICpuAccess CpuAccess;
        public string? Name;

        // ── 兼容别名（高层代码使用的 BindFlags / DebugName / StructureByteStride）──
        public string? DebugName { get => Name; set => Name = value; }
        public RHIBindFlags BindFlags { get => (RHIBindFlags)(uint)Flags; set => Flags = (RHIBufferFlags)(uint)value; }
        public ulong StructureByteStride { get => Stride; set => Stride = value; }

        public bool IsConstantBuffer => (Flags & RHIBufferFlags.ConstantBuffer) != 0;
        public bool IsStructured => (Flags & RHIBufferFlags.Structured) != 0;
        public bool IsIndexBuffer => (Flags & RHIBufferFlags.IndexBuffer) != 0;
        public bool IsVertexBuffer => (Flags & RHIBufferFlags.VertexBuffer) != 0;
        public bool IsUnorderedAccess => (Flags & RHIBufferFlags.UnorderedAccess) != 0;
        public bool IsIndirectArgs => (Flags & RHIBufferFlags.IndirectArgs) != 0;
    }

    [Flags]
    public enum RHIBufferFlags : uint
    {
        None = 0,
        VertexBuffer = 1,
        IndexBuffer = 2,
        ConstantBuffer = 4,
        Structured = 8,
        UnorderedAccess = 16,
        IndirectArgs = 32,
        AccelerationStructure = 64,
        ShaderResource = 128,
        // D3D12 compatibility aliases
        AllowUnorderedAccess = UnorderedAccess,
        DenyShaderResource = 256,
        // 高层命名兼容别名
        StructuredBuffer = Structured,
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RHITextureDesc
    {
        public RHIResourceDimension Dimension;
        public RHIFormat Format;
        public int Width;
        public int Height;
        public int DepthOrArraySize;
        public int MipLevels;
        public int SampleCount;
        public int SampleQuality;
        public RHITextureFlags Flags;
        public RHIClearValue? OptimizedClearValue;

        // ── 高层兼容别名（RHIDeviceExtensions 及渲染管线使用的类 D3D12 命名）──
        public RHITextureType Type
        {
            get => (RHITextureType)Dimension;
            set => Dimension = (RHIResourceDimension)value;
        }
        public int Depth
        {
            get => DepthOrArraySize;
            set => DepthOrArraySize = value;
        }
        public int ArraySize
        {
            get => DepthOrArraySize;
            set => DepthOrArraySize = value;
        }
        public RHITextureUsage Usage
        {
            get => (RHITextureUsage)(uint)Flags;
            set => Flags = (RHITextureFlags)(uint)value;
        }
        public string? Name;

        // ── 兼容别名（高层代码使用的 DebugName / BindFlags / MiscFlags）──
        public string? DebugName { get => Name; set => Name = value; }
        public RHIBindFlags BindFlags { get => (RHIBindFlags)(uint)Flags; set => Flags = (RHITextureFlags)(uint)value; }
        public uint MiscFlags { get; set; }
    }

    [Flags]
    public enum RHITextureFlags : uint
    {
        None = 0,
        RenderTarget = 1,
        DepthStencil = 2,
        ShaderResource = 4,
        UnorderedAccess = 8,
        Cube = 16,
        // D3D12 compatibility aliases
        AllowRenderTarget = RenderTarget,
        AllowDepthStencil = DepthStencil,
        AllowUnorderedAccess = UnorderedAccess,
        AllowShaderResource = ShaderResource,
        DenyShaderResource = 32,
        // 高层面板 / 着色率（Variable Rate Shading）等附加标志
        Present = 64,
        AllowShadingRate = 128,
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RHIClearValue
    {
        public RHIFormat Format;
        public Vector4 Color;
        public float Depth;
        public byte Stencil;
    }

    public static class RHIClearValueHelper
    {
        public static RHIClearValue Color(RHIFormat fmt, Vector4 color) => new() { Format = fmt, Color = color };
        public static RHIClearValue Depth(float depth = 1.0f, byte stencil = 0, RHIFormat fmt = RHIFormat.D32_Float) =>
            new() { Format = fmt, Depth = depth, Stencil = stencil };
    }

    // ──────────────────────────────────────────────────────────────
    // Viewport / Scissor
    // ──────────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    public struct RHIViewport
    {
        public float TopLeftX;
        public float TopLeftY;
        public float Width;
        public float Height;
        public float MinDepth;
        public float MaxDepth;

        public float X { readonly get => TopLeftX; set => TopLeftX = value; }
        public float Y { readonly get => TopLeftY; set => TopLeftY = value; }

        public RHIViewport(float w, float h) : this(0, 0, w, h, 0, 1) { }
        public RHIViewport(float x, float y, float w, float h) : this(x, y, w, h, 0, 1) { }
        public RHIViewport(float x, float y, float w, float h, float minZ, float maxZ)
        { TopLeftX = x; TopLeftY = y; Width = w; Height = h; MinDepth = minZ; MaxDepth = maxZ; }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RHIScissorRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public RHIScissorRect(int l, int t, int r, int b) { Left = l; Top = t; Right = r; Bottom = b; }
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    /// <summary>Rectangle (D3D12 compatibility)</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct RHIRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public RHIRect(int l, int t, int r, int b) { Left = l; Top = t; Right = r; Bottom = b; }

        // ── 高层兼容别名（X/Y/Width/Height 命名）──
        public int X { readonly get => Left; set => Left = value; }
        public int Y { readonly get => Top; set => Top = value; }
        public int Width { readonly get => Right - Left; set => Right = Left + value; }
        public int Height { readonly get => Bottom - Top; set => Bottom = Top + value; }
    }

    // ──────────────────────────────────────────────────────────────
    // Shader bytecode
    // ──────────────────────────────────────────────────────────────

    public struct RHIShaderBytecode
    {
        public byte[] Bytecode;
        public string EntryPoint;
        public RHIShaderStage Stage;
        public string Name;

        public RHIShaderBytecode(byte[] bytecode, string entryPoint, RHIShaderStage stage, string name = "")
        { Bytecode = bytecode; EntryPoint = entryPoint; Stage = stage; Name = name; }

        public bool IsValid => Bytecode != null && Bytecode.Length > 0;
    }

    // ──────────────────────────────────────────────────────────────
    // Input layout
    // ──────────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    public struct RHIInputElement
    {
        public string SemanticName;
        public int SemanticIndex;
        public RHIFormat Format;
        public int InputSlot;
        public int AlignedByteOffset;
        public bool IsPerVertex;
        public int InstanceDataStepRate;

        public RHIInputElement(string semantic, RHIFormat format, int slot = 0, int offset = -1)
        {
            SemanticName = semantic; SemanticIndex = 0; Format = format;
            InputSlot = slot; AlignedByteOffset = offset;
            IsPerVertex = true; InstanceDataStepRate = 0;
        }

        public RHIInputElement(string semantic, int semanticIndex, RHIFormat format,
            int slot = 0, int offset = -1)
        {
            SemanticName = semantic; SemanticIndex = semanticIndex; Format = format;
            InputSlot = slot; AlignedByteOffset = offset;
            IsPerVertex = true; InstanceDataStepRate = 0;
        }
    }

    // ──────────────────────────────────────────────────────────────
    // Pipeline state description
    // ──────────────────────────────────────────────────────────────

    public class RHIGraphicsPipelineDesc
    {
        public string Name = "";
        public RHIShaderBytecode? VS, PS, GS, HS, DS, MS, AS, CS;
        // ── 高层兼容别名（RHIDeviceExtensions 使用的 RHIShader 对象）──
        public RHIShader? VertexShader;
        public RHIShader? PixelShader;
        public RHIInputElement[] InputLayout = Array.Empty<RHIInputElement>();
        public RHIPrimitiveTopology PrimitiveTopology = RHIPrimitiveTopology.TriangleList;
        public RHIBlendDesc BlendState = RHIBlendDesc.Default;
        public RHIDepthDesc DepthStencilState = RHIDepthDesc.Default;
        public RHIRasterizerDesc RasterizerState = RHIRasterizerDesc.Default;
        public RHIFormat[] RTVFormats = { RHIFormat.R8G8B8A8_UNorm };
        public RHIFormat DSVFormat = RHIFormat.Unknown;
        public int SampleCount = 1;
        public int SampleMask = unchecked((int)0xFFFFFFFF);
        public int PatchControlPointCount;
        public RHISamplerDesc[] StaticSamplers = Array.Empty<RHISamplerDesc>();
        /// <summary>关联的渲染通道（可选；为 null 时由管线内部推导）。</summary>
        public RHIRenderPass? RenderPass;
        /// <summary>关联的根签名（可选；为 null 时通过命令列表单独设置）</summary>
        public RHIRootSignature? RootSignature;
        public RHIPipelineType Type => RHIPipelineType.Graphics;

        // ── 高层命名兼容别名（渲染管线使用的 RenderTargetFormats / DepthStencilFormat）──
        public RHIFormat[] RenderTargetFormats
        {
            get => RTVFormats;
            set => RTVFormats = value;
        }
        public RHIFormat DepthStencilFormat
        {
            get => DSVFormat;
            set => DSVFormat = value;
        }
        /// <summary>着色器级放大阶段（Mesh pipeline）。</summary>
        public RHIShader? AmplificationShader;
        // ── 高层命名兼容别名（RHIPipelineStateManager 及渲染层读取/写入的着色器字段）──
        private RHIShader? _geometryShader;
        private RHIShader? _hullShader;
        private RHIShader? _domainShader;
        private RHIShader? _meshShader;
        public RHIShader? GeometryShader { get => _geometryShader; set => _geometryShader = value; }
        public RHIShader? HullShader { get => _hullShader; set => _hullShader = value; }
        public RHIShader? DomainShader { get => _domainShader; set => _domainShader = value; }
        public RHIShader? MeshShader { get => _meshShader; set => _meshShader = value; }
        /// <summary>输入布局（与 InputLayout 等价）。</summary>
        public RHIInputElement[] InputElements
        {
            get => InputLayout;
            set => InputLayout = value;
        }
        /// <summary>图元拓扑（与 PrimitiveTopology 等价）。</summary>
        public RHIPrimitiveTopology Topology
        {
            get => PrimitiveTopology;
            set => PrimitiveTopology = value;
        }
        /// <summary>填充模式（便捷访问 RasterizerState）。</summary>
        public RHIFillMode FillMode
        {
            get => RasterizerState.FillMode;
            set => RasterizerState.FillMode = value;
        }
        /// <summary>剔除模式（便捷访问 RasterizerState）。</summary>
        public RHICullMode CullMode
        {
            get => RasterizerState.CullMode;
            set => RasterizerState.CullMode = value;
        }
        /// <summary>光栅化深度偏移（便捷访问 RasterizerState）。</summary>
        public int DepthBias
        {
            get => RasterizerState.DepthBias;
            set => RasterizerState.DepthBias = value;
        }
        /// <summary>光栅化深度偏移钳制（便捷访问 RasterizerState）。</summary>
        public float DepthBiasClamp
        {
            get => RasterizerState.DepthBiasClamp;
            set => RasterizerState.DepthBiasClamp = value;
        }

        // ── 高层兼容字段（渲染层以这些命名直接赋值；与 BlendState / DepthStencilState / RasterizerState 语义一致）──
        public bool AlphaToCoverageEnable;
        public bool IndependentBlendEnable;
        public RHIRenderTargetBlend[] RenderTargetBlends = Array.Empty<RHIRenderTargetBlend>();
        public bool DepthEnable = true;
        public bool DepthWriteEnable = true;
        public RHICompareFunction DepthFunc = RHICompareFunction.Less;
        public bool StencilEnable;
        public RHIStencilOpDesc FrontFace;
        public RHIStencilOpDesc BackFace = new();
        public bool FrontCounterClockwise = true;
        public bool ConservativeRaster;
        public float SlopeScaledDepthBias;
        public bool DepthClipEnable = true;
        public bool MultisampleEnable;
        public bool AntialiasedLineEnable;
        public int NumRenderTargets;
    }

    public class RHIComputePipelineDesc
    {
        public string Name = "";
        public RHIShaderBytecode? CS;
        // ── 高层兼容别名（RHIDeviceExtensions 使用的 RHIShader 对象）──
        public RHIShader? ComputeShader;
        /// <summary>关联的根签名（可选；为 null 时通过命令列表单独设置）</summary>
        public RHIRootSignature? RootSignature;
    }

    public class RHIMeshPipelineDesc
    {
        public string Name = "";
        public RHIShaderBytecode? MS, PS, AS;
        public RHIBlendDesc BlendState = RHIBlendDesc.Default;
        public RHIDepthDesc DepthStencilState = RHIDepthDesc.Default;
        public RHIRasterizerDesc RasterizerState = RHIRasterizerDesc.Default;
        public RHIFormat[] RTVFormats = { RHIFormat.R8G8B8A8_UNorm };
        public RHIFormat DSVFormat = RHIFormat.Unknown;
        public int SampleCount = 1;
        /// <summary>关联的根签名（可选；为 null 时通过命令列表单独设置）</summary>
        public RHIRootSignature? RootSignature;
    }

    // ──────────────────────────────────────────────────────────────
    // Resource barrier
    // ──────────────────────────────────────────────────────────────

    public struct RHIResourceBarrier
    {
        public RHIResource Resource;
        public RHIResourceState StateBefore;
        public RHIResourceState StateAfter;
        public uint Subresource; // 0xFFFFFFFF = all

        public RHIResourceBarrier(RHIResource res, RHIResourceState before, RHIResourceState after, uint sub = 0xFFFFFFFF)
        { Resource = res; StateBefore = before; StateAfter = after; Subresource = sub; }
    }

    // ──────────────────────────────────────────────────────────────
    // Constant buffer view / SRV / UAV descriptors
    // ──────────────────────────────────────────────────────────────

    public struct RHICBVDesc
    {
        public ulong BufferLocation;
        public uint SizeInBytes;

        /// <summary>兼容别名（高层代码以 <c>BufferOffset</c> 访问）。</summary>
        public ulong BufferOffset
        {
            get => BufferLocation;
            set => BufferLocation = value;
        }
    }

    public enum RHISRVDimension { Buffer, Texture1D, Texture2D, Texture2DArray, Texture3D, TextureCube }

    // 统一后的 SRV 描述符：同时保留 RHIResourceDimension（Dimension）与
    // RHISRVDimension（ViewDimension）两套字段，兼容不同调用方。
    public struct RHISRVDesc
    {
        public RHIFormat Format;
        public RHIResourceDimension Dimension;
        public RHISRVDimension ViewDimension;
        public uint FirstElement;
        public uint NumElements;
        public uint First2DArraySlice;
        public uint ArraySize;
        public uint MostDetailedMip;
        public uint MipLevels;
        public uint PlaneSlice;
        public float ResourceMinLODClamp;
    }

    public struct RHIUAVDesc
    {
        public RHIFormat Format;
        public RHIResourceDimension Dimension;
        public uint FirstElement;
        public uint NumElements;
        public uint MipSlice;
        public uint FirstArraySlice;
        public uint ArraySize;
        public uint PlaneSlice;
    }

    // ──────────────────────────────────────────────────────────────
    // Root signature / binding
    // ──────────────────────────────────────────────────────────────

    public enum RHIRootParameterType { DescriptorTable, Constants, CBV, SRV, UAV, Sampler, StructuredBufferSRV }

    public enum RHIShaderVisibility { All, Vertex, Hull, Domain, Geometry, Pixel, Amplification, Mesh }

    public struct RHIRootParameter
    {
        public RHIRootParameterType ParameterType;
        public RHIShaderVisibility ShaderVisibility;
        public RHIRootDescriptorTable? DescriptorTable;
        public RHIRootConstants? Constants;
        public RHIRootDescriptor? Descriptor;
    }

    public struct RHIRootDescriptorTable
    {
        public RHIRootDescriptorRange[] Ranges;
    }

    public struct RHIRootDescriptorRange
    {
        public RHIDescriptorRangeType RangeType;
        public int NumDescriptors;
        public uint BaseShaderRegister;
        public uint RegisterSpace;
        public int OffsetInDescriptorsFromTableStart;
    }

    public enum RHIDescriptorRangeType { SRV, UAV, CBV, Sampler }

    public struct RHIRootConstants
    {
        public int Num32BitValues;
        public uint ShaderRegister;
        public uint RegisterSpace;
    }

    public struct RHIRootDescriptor
    {
        public uint ShaderRegister;
        public uint RegisterSpace;
    }

    public class RHIRootSignatureDesc
    {
        public RHIRootParameter[] Parameters = Array.Empty<RHIRootParameter>();
        public RHISamplerDesc[] StaticSamplers = Array.Empty<RHISamplerDesc>();
        public bool AllowInputAssembler;
        public bool AllowResourceOverride;
        /// <summary>根签名标志（D3D12 兼容；AllowInputAssembler 为 true 时自动包含 IA 标志）</summary>
        public RHIRootSignatureFlags Flags = RHIRootSignatureFlags.None;
        // ── 高层命名兼容别名 ──
        /// <summary>常量缓冲绑定参数（兼容旧命名）。</summary>
        public RHIRootDescriptorRange[]? ConstantBuffers;
        // ── 高层便捷计数 / 资源声明（渲染层以这些命名填充根签名描述）──
        public RHIRootDescriptorRange[]? ShaderResources;
        public int SRVCount;
        public int SamplerCount;
        public RHIRootDescriptorRange[]? UnorderedAccessViews;
    }

    // ──────────────────────────────────────────────────────────────
    // Fence / sync
    // ──────────────────────────────────────────────────────────────

    public enum RHIFenceFlags { None, Shared, NonMonitored }

    // ──────────────────────────────────────────────────────────────
    // Swap chain
    // ──────────────────────────────────────────────────────────────

    public enum RHISwapEffect { Discard, FlipDiscard, FlipSequential }

    public struct RHISwapChainDesc
    {
        public int Width;
        public int Height;
        public RHIFormat Format;
        public int BufferCount;
        public bool Windowed;
        public RHISwapEffect SwapEffect;
        public int SampleCount;
        public IntPtr OutputWindow; // HWND
        public bool VSync; // 垂直同步开关
    }

    // ========================================================================
    // 缺失的 RHI 对象类型桩（原抽象层未声明，引用处仅作为类型/字段/返回值使用）
    // ========================================================================

    /// <summary>着色器对象（由 RHIShaderBytecode 或源码编译得到）。</summary>
    public class RHIShader
    {
        public string Name { get; set; }
        public RHIShaderBytecode Bytecode { get; set; }
        public RHIShaderStage Stage { get; set; }

        /// <summary>着色器唯一标识（用于管线缓存哈希）。</summary>
        public ulong Id;
    }

    /// <summary>管线对象（图形/计算/网格管线）。</summary>
    public class RHIPipeline
    {
        public string Name { get; set; }
        public RHIPipelineType Type { get; set; }
        public void Dispose() { }
    }

    /// <summary>采样器对象。</summary>
    public class RHISampler
    {
        public string Name { get; set; }
        public RHISamplerDesc Desc { get; set; }
        public void Dispose() { }
    }

    /// <summary>模板操作描述（兼容 RHIDepthStencilOp）。</summary>
    public struct RHIStencilOpDesc
    {
        public RHIStencilOp StencilFailOp;
        public RHIStencilOp StencilDepthFailOp;
        public RHIStencilOp StencilPassOp;
        public RHIComparisonFunc StencilFunc;
    }

    /// <summary>CPU 访问标志。</summary>
    [System.Flags]
    public enum RHICpuAccess
    {
        None = 0,
        Write = 1,
        Read = 2
    }

    /// <summary>比较函数（兼容 RHIComparisonFunc）。</summary>
    public enum RHICompareFunction
    {
        Never, Less, Equal, LessEqual, Greater, NotEqual, GreaterEqual, Always
    }
}
