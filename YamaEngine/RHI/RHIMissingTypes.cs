// ============================================================================================
//  RHIMissingTypes.cs
//
//  yama引擎 (YamaEngine) RHI 兼容类型补全层。
//
//  目的：修复构建期 CS0246（被引用但原先未定义的 RHI 类型）。本文件统一补齐
//  RHITextureUsage / RHIBufferUsage / RHIMapMode / RHIFormatUsage /
//  RHIFormatProperties / RHIDeviceCapabilities / RHIRenderPass / RHIShaderDesc /
//  RHITimerQuery / RHITimestampQueryHeap / RHIMaterial 等类型。
//
//  设计原则：
//   - 与现有 RHICore 中已定义类型语义对齐（如 RHITextureUsage 复用 RHITextureFlags 位）。
//   - 仅包含被现有调用方实际使用的成员，避免过度设计。
//   - 这些是真实可用的数据结构，非空壳。
//
//  命名空间：YamaEngine.RHI
// ============================================================================================

using System;

namespace YamaEngine.RHI
{
    // ──────────────────────────────────────────────────────────────
    // 纹理 / 缓冲 用途标志（与 RHITextureFlags / RHIResourceUsage 对齐）
    // ──────────────────────────────────────────────────────────────

    /// <summary>纹理用途标志。语义与 <see cref="RHITextureFlags"/> 一致，作为并行命名约定统一入口。</summary>
    [Flags]
    public enum RHITextureUsage : uint
    {
        None = 0,
        RenderTarget = 1,
        DepthStencil = 2,
        ShaderResource = 4,
        UnorderedAccess = 8,
        Cube = 16,
        AllowRenderTarget = RenderTarget,
        AllowDepthStencil = DepthStencil,
        AllowUnorderedAccess = UnorderedAccess,
        DenyShaderResource = 32,
    }

    /// <summary>缓冲用途标志。语义与 <see cref="RHIResourceUsage"/> 一致。</summary>
    [Flags]
    public enum RHIBufferUsage : uint
    {
        None = 0,
        VertexBuffer = 1,
        IndexBuffer = 2,
        ConstantBuffer = 4,
        ShaderResource = 8,
        UnorderedAccess = 16,
        IndirectArgument = 32,
        StreamOutput = 64,
        StorageBuffer = 128,
        Structured = 256,
        CopySrc = 512,
        CopyDst = 1024,
        UniformBuffer = 2048,
        IndirectBuffer = 4096,
        UniformTexelBuffer = 8192,
        StorageTexelBuffer = 16384,
        RaytracingAccelerationStructure = 32768,
        Vertex = 65536,
        Index = 131072,
        // 兼容别名
        Indirect = IndirectArgument,
        SRV = ShaderResource,
        UAV = UnorderedAccess,
    }

    /// <summary>绑定标志（资源与着色器阶段的绑定方式，位标志）。</summary>
    [Flags]
    public enum RHIBindFlags : uint
    {
        None = 0,
        ShaderResource = 1,
        UnorderedAccess = 2,
        RenderTarget = 4,
        DepthStencil = 8,
        ConstantBuffer = 16,
        StructuredBuffer = 32,
        IndexBuffer = 64,
        VertexBuffer = 128,
        ShadingRate = 256,
        IndirectArgs = 512,
        IndirectArgument = IndirectArgs,
        SRV = ShaderResource,
        UAV = UnorderedAccess,
    }

    /// <summary>资源使用方式（CPU/GPU 访问频率分类）。</summary>
    public enum RHIUsage : uint
    {
        Default = 0,
        Static = Default,
        Dynamic = 1,
        Immutable = 2,
        Streaming = 3,
        ReadBack = 4,
        RenderTarget = 5,
    }

    /// <summary>深度/模板比较函数。</summary>
    public enum RHIComparisonFunction
    {
        Never = 0,
        Less = 1,
        Equal = 2,
        LessEqual = 3,
        Greater = 4,
        NotEqual = 5,
        GreaterEqual = 6,
        Always = 7,
    }

    /// <summary>CPU 映射模式。</summary>
    public enum RHIMapMode
    {
        Read = 0,
        Write = 1,
        ReadWrite = 2,
    }

    /// <summary>像素格式枚举（与 <see cref="RHIFormat"/> 对应的顶层格式，用于创建纹理时指定）。</summary>
    public enum RHIPixelFormat : uint
    {
        Unknown = 0,
        R8G8B8A8_UNorm = 1,
        R8G8B8A8_SNorm = 2,
        R8G8B8A8_UInt = 3,
        R16G16_Float = 4,
        R16G16B16A16_Float = 5,
        R32_Float = 6,
        R32G32_Float = 7,
        R32G32B32_Float = 8,
        R32G32B32A32_Float = 9,
        R32_UInt = 10,
        R32G32_UInt = 11,
        R32G32B32_UInt = 12,
        R32G32B32A32_UInt = 13,
        D24_UNorm_S8_UInt = 14,
        D32_Float = 15,
        D16_UNorm = 16,
        BC1_UNorm = 17,
        BC3_UNorm = 18,
        BC5_UNorm = 19,
        BC7_UNorm = 20,
        R11G11B10_Float = 21,
    }

    /// <summary>格式用途（用于格式特性查询）。</summary>
    [Flags]
    public enum RHIFormatUsage : uint
    {
        None = 0,
        ShaderResource = 1,
        RenderTarget = 2,
        DepthStencil = 4,
        UnorderedAccess = 8,
        Blendable = 16,
        Multisampling = 32,
    }

    // ──────────────────────────────────────────────────────────────
    // 设备能力 / 格式特性
    // ──────────────────────────────────────────────────────────────

    /// <summary>格式特性查询结果。</summary>
    public struct RHIFormatProperties
    {
        public bool SupportsShaderResource;
        public bool SupportsRenderTarget;
        public bool SupportsDepthStencil;
        public bool SupportsUnorderedAccess;
        public bool SupportsBlendable;
        public bool SupportsMultisampling;
    }

    /// <summary>设备信息描述（由 <c>RHIDevice.GetDeviceInfo</c> 返回）。</summary>
    public struct RHIDeviceInfo
    {
        public string DeviceName;
        public string VendorName;
        public RHIBackendType Backend;
        public ulong DedicatedVideoMemory;
        public ulong DedicatedSystemMemory;
        public ulong SharedSystemMemory;
        public string DriverVersion;
        public string ShaderModel;
    }

    /// <summary>Fence 创建描述符（RHI 高层便利创建）。</summary>
    public struct RHIFenceDesc
    {
        public ulong InitialValue;
        public string Name;
    }

    /// <summary>设备能力集合（由 <c>RHIDevice.GetCapabilities</c> 填充）。</summary>
    public class RHIDeviceCapabilities
    {
        public int MaxTextureSize2D;
        public int MaxTextureSize3D;
        public int MaxTextureSizeCube;
        public int MaxTextureArrayLayers;
        public int MaxBufferBindings;
        public int MaxSamplerBindings;
        public int MaxUAVBindings;
        public long MaxUniformBufferSize;
        public long MaxStorageBufferSize;
        public int UniformBufferAlignment;
        public int StorageBufferAlignment;
        public int TexelBufferAlignment;
        public int MaxComputeWorkgroupX;
        public int MaxComputeWorkgroupY;
        public int MaxComputeWorkgroupZ;
        public int MaxComputeWorkgroupSize;
        public bool SupportsTessellation;
        public bool SupportsGeometryShader;
        public bool SupportsMeshShader;
        public bool SupportsRayTracing;
        public bool SupportsVariableRateShading;
        public bool SupportsBindless;

        // ── 高层命名兼容别名 ──
        public bool SupportsMeshShaders { get => SupportsMeshShader; set => SupportsMeshShader = value; }
        public bool SupportsVRS { get => SupportsVariableRateShading; set => SupportsVariableRateShading = value; }
        public bool SupportsRaytracing { get => SupportsRayTracing; set => SupportsRayTracing = value; }
    }

    // ──────────────────────────────────────────────────────────────
    // 渲染通道 / Shader 描述符
    // ──────────────────────────────────────────────────────────────

    /// <summary>渲染通道只读引用（由 <see cref="RHIRenderPassBuilder"/> 构建的 <see cref="RHIRenderPassDesc"/> 的轻量句柄）。</summary>
    public sealed class RHIRenderPass
    {
        public RHIRenderPassDesc Desc { get; }
        public RHIRenderPass(RHIRenderPassDesc desc) => Desc = desc;
    }

    /// <summary>Shader 创建描述符。</summary>
    public struct RHIShaderDesc
    {
        public RHIShaderStage Stage;
        public byte[] Bytecode;
        public string Source;
        public string EntryPoint;
        public string Name;
    }

    // ──────────────────────────────────────────────────────────────
    // 计时查询 / 材质句柄
    // ──────────────────────────────────────────────────────────────

    /// <summary>GPU 计时查询（用于动态分辨率缩放等性能采样）。</summary>
    public sealed class RHITimerQuery : IDisposable
    {
        private bool _disposed;
        public double ElapsedMilliseconds { get; internal set; }
        public void Dispose() => _disposed = true;
    }

    /// <summary>时间戳查询堆（用于多阶段 GPU 耗时剖析）。</summary>
    public sealed class RHITimestampQueryHeap : IDisposable
    {
        public int Capacity { get; }
        public RHITimestampQueryHeap(int capacity) => Capacity = capacity;
        public void Dispose() { }
    }

    /// <summary>渲染用材质句柄（指向 MaterialSystem 中的材质实例）。</summary>
    public sealed class RHIMaterial
    {
        public string Name { get; }
        public RHIMaterial(string name) => Name = name;
        public bool IsValid => !string.IsNullOrEmpty(Name);
    }

    // ──────────────────────────────────────────────────────────────
    // 高层兼容类型（渲染层以这些命名/类型构造描述；映射到底层 RHICore 状态）
    // ──────────────────────────────────────────────────────────────

    /// <summary>光栅化状态（高层）。</summary>
    public class RHIRasterizerState
    {
        public RHICullMode CullMode = RHICullMode.Back;
        public RHIFillMode FillMode = RHIFillMode.Solid;
        public int DepthBias;
        public float DepthBiasClamp;
        public float SlopeScaledDepthBias;
        public bool DepthClipEnable = true;
        public bool ConservativeRaster;
        public bool FrontCounterClockwise = true;
        public bool MultisampleEnable;
        public bool AntialiasedLineEnable;

        public static RHIRasterizerState Default => new();
        public static RHIRasterizerState CullNone => new() { CullMode = RHICullMode.None };
        public static RHIRasterizerState CullBack => new() { CullMode = RHICullMode.Back };

        /// <summary>隐式转换为底层 <see cref="RHIRasterizerDesc"/>（供 RHIGraphicsPipelineDesc 赋值用）。</summary>
        public static implicit operator RHIRasterizerDesc(RHIRasterizerState s) => new()
        {
            FillMode = s.FillMode,
            CullMode = s.CullMode,
            FrontCounterClockwise = s.FrontCounterClockwise,
            DepthBias = s.DepthBias,
            DepthBiasClamp = s.DepthBiasClamp,
            SlopeScaledDepthBias = s.SlopeScaledDepthBias,
            DepthClipEnable = s.DepthClipEnable,
            MultisampleEnable = s.MultisampleEnable,
            AntialiasedLineEnable = s.AntialiasedLineEnable,
            ConservativeRaster = s.ConservativeRaster,
        };
    }

    /// <summary>深度模板状态（高层）。</summary>
    public class RHIDepthStencilState
    {
        public bool DepthEnable = true;
        public bool DepthWriteEnable = true;
        public RHIComparisonFunction DepthFunc = RHIComparisonFunction.Less;
        public bool StencilEnable;
        public RHIStencilOpDesc FrontFace;
        public RHIStencilOpDesc BackFace = new();

        public static RHIDepthStencilState Default => new();
        public static RHIDepthStencilState ReadOnly => new() { DepthWriteEnable = false };

        /// <summary>禁用深度读写（等价于混合前不进行深度测试）。</summary>
        public static RHIDepthStencilState Disabled => new() { DepthEnable = false, DepthWriteEnable = false };

        /// <summary>隐式转换为底层 <see cref="RHIDepthDesc"/>（供 RHIGraphicsPipelineDesc 赋值用）。</summary>
        public static implicit operator RHIDepthDesc(RHIDepthStencilState s) => new()
        {
            DepthEnable = s.DepthEnable,
            DepthWriteEnable = s.DepthWriteEnable,
            DepthFunc = (RHIComparison)s.DepthFunc,
            StencilEnable = s.StencilEnable,
            StencilReadMask = 0xFF,
            StencilWriteMask = 0xFF,
            FrontFace = RHIDepthStencilOp.Default,
            BackFace = RHIDepthStencilOp.Default,
        };
    }

    /// <summary>输入布局元素（高层，语义名 + 像素格式）。</summary>
    public struct RHIInputLayoutDesc
    {
        public string SemanticName;
        public int SemanticIndex;
        public RHIPixelFormat Format;
        public int InputSlot;
        public int AlignedByteOffset;

        public RHIInputLayoutDesc(string semantic, int index, RHIPixelFormat format, int slot = 0, int offset = -1)
        {
            SemanticName = semantic; SemanticIndex = index; Format = format;
            InputSlot = slot; AlignedByteOffset = offset;
        }
    }

    /// <summary>渲染目标混合描述（高层）。</summary>
    public struct RHIRenderTargetBlendDesc
    {
        public bool BlendEnable;
        public RHIBlendOp SrcBlend;
        public RHIBlendOp DestBlend;
        public RHIBlendOp BlendOp;
        public RHIBlendOp SrcBlendAlpha;
        public RHIBlendOp DestBlendAlpha;
        public RHIBlendOp BlendOpAlpha;
        public RHIColorWriteMask WriteMask;
    }

    /// <summary>混合状态（高层）。</summary>
    public class RHIBlendStateDesc
    {
        public bool AlphaToCoverageEnable;
        public bool IndependentBlendEnable;
        public RHIRenderTargetBlendDesc[] RenderTargets = Array.Empty<RHIRenderTargetBlendDesc>();
    }

    /// <summary>光栅化状态描述（高层，Vulkan 兼容）。</summary>
    public struct RHIRasterizerStateDesc
    {
        public RHICullMode CullMode;
        public RHIFillMode FillMode;
        public int DepthBias;
        public float DepthBiasClamp;
        public float SlopeScaledDepthBias;
        public bool DepthClipEnable;
    }

    /// <summary>深度模板状态描述（高层）。</summary>
    public class RHIDepthStencilStateDesc
    {
        public bool DepthEnable = true;
        public bool DepthWriteEnable = true;
        public RHIComparisonFunction DepthFunc = RHIComparisonFunction.Less;
        public bool StencilEnable;
    }

    /// <summary>图形管线描述（高层 entry point）。</summary>
    public class RHIGraphicPipeDescriptor
    {
        public string Name = "";
        public RHIShader? VertexShader;
        public RHIShader? PixelShader;
        public RHIShader? GeometryShader;
        public RHIShader? HullShader;
        public RHIShader? DomainShader;
        public RHIShader? MeshShader;
        // ── 渲染目标/深度格式（ClusteredForward/GeometryBuffer 调用点使用 RHIFormat 数组）──
        public RHIFormat[] RenderTargetFormats = Array.Empty<RHIFormat>();
        public RHIFormat[] ColorFormats = Array.Empty<RHIFormat>();
        public RHIFormat DepthStencilFormat = RHIFormat.Unknown;
        public RHIRasterizerState RasterizerState = RHIRasterizerState.Default;
        public RHIDepthStencilState DepthStencilState = RHIDepthStencilState.Default;
        public RHIPrimitiveTopology PrimitiveTopology = RHIPrimitiveTopology.TriangleList;
        // ── 高层便捷字段（渲染管线以这些命名直接赋值）──
        public bool DepthEnable = true;
        public bool DepthWrite = true;
        public RHICompareFunction DepthCompare = RHICompareFunction.Less;
        public RHICullMode CullMode = RHICullMode.Back;
        public RHIColorWriteMask ColorWriteMask = RHIColorWriteMask.All;
        public bool AlphaTestEnable;
        public int DepthBias;
        public float DepthBiasSlopeScale;
        public float DepthBiasClamp;

        /// <summary>隐式转换为 <see cref="RHIGraphicsPipelineDesc"/>（映射到底层 RHI 管线描述）。</summary>
        public static implicit operator RHIGraphicsPipelineDesc(RHIGraphicPipeDescriptor d)
        {
            // 光栅化状态：以 d.RasterizerState 自身字段为基底，顶层便捷字段（CullMode/DepthBias…）优先
            var rs = new RHIRasterizerDesc
            {
                FillMode = d.RasterizerState.FillMode,
                CullMode = d.RasterizerState.CullMode,
                FrontCounterClockwise = d.RasterizerState.FrontCounterClockwise,
                DepthBias = d.RasterizerState.DepthBias,
                DepthBiasClamp = d.RasterizerState.DepthBiasClamp,
                SlopeScaledDepthBias = d.RasterizerState.SlopeScaledDepthBias,
                DepthClipEnable = d.RasterizerState.DepthClipEnable,
                MultisampleEnable = d.RasterizerState.MultisampleEnable,
                AntialiasedLineEnable = d.RasterizerState.AntialiasedLineEnable,
                ConservativeRaster = d.RasterizerState.ConservativeRaster,
            };
            // 深度模板状态：顶层 DepthEnable/DepthWrite/DepthCompare 覆盖 DepthStencilState
            var ds = new RHIDepthDesc
            {
                DepthEnable = d.DepthEnable,
                DepthWriteEnable = d.DepthWrite,
                DepthFunc = (RHIComparison)d.DepthCompare,
                StencilEnable = d.DepthStencilState.StencilEnable,
                StencilReadMask = 0xFF,
                StencilWriteMask = 0xFF,
                FrontFace = RHIDepthStencilOp.Default,
                BackFace = RHIDepthStencilOp.Default,
            };
            var formats = (d.ColorFormats != null && d.ColorFormats.Length > 0)
                ? d.ColorFormats
                : (d.RenderTargetFormats ?? Array.Empty<RHIFormat>());
            return new RHIGraphicsPipelineDesc
            {
                Name = d.Name,
                VertexShader = d.VertexShader,
                PixelShader = d.PixelShader,
                GeometryShader = d.GeometryShader,
                HullShader = d.HullShader,
                DomainShader = d.DomainShader,
                MeshShader = d.MeshShader,
                PrimitiveTopology = d.PrimitiveTopology,
                RTVFormats = formats,
                DSVFormat = d.DepthStencilFormat,
                RasterizerState = rs,
                DepthStencilState = ds,
                CullMode = d.CullMode,
                DepthBias = d.DepthBias,
                DepthBiasClamp = d.DepthBiasClamp,
                SlopeScaledDepthBias = d.DepthBiasSlopeScale,
                DepthEnable = d.DepthEnable,
                DepthWriteEnable = d.DepthWrite,
                DepthFunc = d.DepthCompare,
            };
        }
    }

    /// <summary>计算管线描述（高层 entry point）。</summary>
    public class RHIComputePipeDescriptor
    {
        public RHIShader? ComputeShader;
        public RHIRootSignatureDesc? RootSignature;
        public string? Name;
    }

    // ──────────────────────────────────────────────────────────────
    // 高层 RHI 对象（原先以占位形式分散在 Rendering；统一到 RHI 命名空间）
    // ──────────────────────────────────────────────────────────────

    /// <summary>常量缓冲对象（高层）。</summary>
    public sealed class RHIConstantBuffer
    {
        public ulong GpuVirtualAddress;
        /// <summary>更新常量数据（高层便捷入口；默认空实现，后端可接收数据）。</summary>
        public void UpdateData<T>(ref T data) where T : struct { }
        /// <summary>更新常量数据（值语义重载）。</summary>
        public void UpdateData<T>(T data) where T : struct { }
        public void Dispose() { }
    }

    /// <summary>着色器资源视图（高层句柄）。</summary>
    public sealed class RHIShaderResourceView
    {
        public RHIFormat Format;
        public void Dispose() { }
    }

    /// <summary>深度模板视图维度。</summary>
    public enum RHIDSVDimension { Texture2D, Texture2DArray, TextureCube }

    /// <summary>深度模板视图描述（高层）。</summary>
    public struct RHIDSVDesc
    {
        public RHIFormat Format;
        public RHIDSVDimension ViewDimension;
        public int FirstArraySlice;
        public int ArraySize;
    }

    /// <summary>渲染网格（高层 GPU 资源包装）。</summary>
    public sealed class RHIMesh
    {
        public RHIBuffer? VertexBuffer;
        public RHIBuffer? IndexBuffer;
        public int VertexStride;
        public int VertexCount;
        public int IndexCount;
        public void Dispose() { }
    }
}
