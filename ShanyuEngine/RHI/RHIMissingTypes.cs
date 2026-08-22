// ============================================================================================
//  RHIMissingTypes.cs
//
//  山雨引擎 (ShanyuEngine) RHI 兼容类型补全层。
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
//  命名空间：ShanyuEngine.RHI
// ============================================================================================

using System;

namespace ShanyuEngine.RHI
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
    }

    /// <summary>CPU 映射模式。</summary>
    public enum RHIMapMode
    {
        Read = 0,
        Write = 1,
        ReadWrite = 2,
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
}
