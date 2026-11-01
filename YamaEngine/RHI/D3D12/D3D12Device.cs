// ==================================================================================================
//  D3D12Device.cs  -  yama游戏引擎 (YamaEngine) Direct3D 12 渲染后端完整实现
//
//  命名空间:  YamaEngine.RHI.D3D12
//  依赖:      Vortice.Direct3D12, Vortice.DXGI, Vortice.Direct3D, Vortice.Mathematics
//
//  功能覆盖:
//    1. D3D12Device 继承 RHIDevice，实现全部抽象方法
//    2. D3D12Buffer / D3D12Texture / D3D12CommandQueue / D3D12CommandList
//       D3D12DescriptorHeap / D3D12RootSignature / D3D12PipelineState
//       D3D12Fence / D3D12SwapChain
//    3. 描述符堆管理 (CBV_SRV_UAV / Sampler / RTV / DSV)
//    4. Root Signature 序列化 (Version 1.1)
//    5. PSO 创建 (Graphics / Compute / Mesh Shader)
//    6. 命令列表录制、资源屏障、Copy 操作
//    7. SwapChain (DXGI Flip Model)
//    8. 资源上传 (Upload Buffer + Copy)
//    9. Shader 编译 (d3dcompiler.dll D3DCompile)
//   10. DXGI 适配器枚举与功能级别检测
//   11. Mesh Shader / Ray Tracing / VRS 特性检测
//   12. 资源命名 (D3D12 Object Name)
// ==================================================================================================

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Vortice.Mathematics;
using Vortice;
using Feature = Vortice.Direct3D12.Feature;
using ShaderResourceViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension;

namespace YamaEngine.RHI.D3D12
{
    // ==============================================================================================
    //  区域一: 格式 / 枚举映射器
    //  将引擎 RHI 抽象枚举转换为 D3D12 / DXGI 原生枚举
    // ==============================================================================================

    /// <summary>
    /// RHI 格式到 DXGI_FORMAT 的映射，以及各种枚举的转换工具。
    /// </summary>
    internal static class D3D12FormatMapper
    {
        /// <summary>将 RHIFormat 转换为 Vortice DXGI Format</summary>
        public static Format ToDxgiFormat(RHIFormat fmt)
        {
            return fmt switch
            {
                RHIFormat.Unknown               => Format.Unknown,
                RHIFormat.R8_UNorm              => Format.R8_UNorm,
                RHIFormat.R8_SNorm              => Format.R8_SNorm,
                RHIFormat.R8_UInt               => Format.R8_UInt,
                RHIFormat.R8_SInt               => Format.R8_SInt,
                RHIFormat.R8G8_UNorm            => Format.R8G8_UNorm,
                RHIFormat.R8G8_SNorm            => Format.R8G8_SNorm,
                RHIFormat.R8G8_UInt             => Format.R8G8_UInt,
                RHIFormat.R8G8_SInt             => Format.R8G8_SInt,
                RHIFormat.R8G8B8A8_UNorm        => Format.R8G8B8A8_UNorm,
                RHIFormat.R8G8B8A8_UNorm_SRgb   => Format.R8G8B8A8_UNorm_SRgb,
                RHIFormat.R8G8B8A8_UInt         => Format.R8G8B8A8_UInt,
                RHIFormat.R8G8B8A8_SInt         => Format.R8G8B8A8_SInt,
                RHIFormat.B8G8R8A8_UNorm        => Format.B8G8R8A8_UNorm,
                RHIFormat.B8G8R8A8_UNorm_SRgb   => Format.B8G8R8A8_UNorm_SRgb,
                RHIFormat.R10G10B10A2_UNorm     => Format.R10G10B10A2_UNorm,
                RHIFormat.R10G10B10A2_UInt      => Format.R10G10B10A2_UInt,
                RHIFormat.R16_Float             => Format.R16_Float,
                RHIFormat.R16_UNorm             => Format.R16_UNorm,
                RHIFormat.R16_SNorm             => Format.R16_SNorm,
                RHIFormat.R16_UInt              => Format.R16_UInt,
                RHIFormat.R16_SInt              => Format.R16_SInt,
                RHIFormat.R16G16_Float          => Format.R16G16_Float,
                RHIFormat.R16G16_UNorm          => Format.R16G16_UNorm,
                RHIFormat.R16G16_SNorm          => Format.R16G16_SNorm,
                RHIFormat.R16G16_UInt           => Format.R16G16_UInt,
                RHIFormat.R16G16_SInt           => Format.R16G16_SInt,
                RHIFormat.R16G16B16A16_Float    => Format.R16G16B16A16_Float,
                RHIFormat.R16G16B16A16_UNorm    => Format.R16G16B16A16_UNorm,
                RHIFormat.R16G16B16A16_UInt     => Format.R16G16B16A16_UInt,
                RHIFormat.R32_Float             => Format.R32_Float,
                RHIFormat.R32_UInt              => Format.R32_UInt,
                RHIFormat.R32_SInt              => Format.R32_SInt,
                RHIFormat.R32G32_Float          => Format.R32G32_Float,
                RHIFormat.R32G32_UInt           => Format.R32G32_UInt,
                RHIFormat.R32G32_SInt           => Format.R32G32_SInt,
                RHIFormat.R32G32B32_Float       => Format.R32G32B32_Float,
                RHIFormat.R32G32B32_UInt        => Format.R32G32B32_UInt,
                RHIFormat.R32G32B32_SInt        => Format.R32G32B32_SInt,
                RHIFormat.R32G32B32A32_Float    => Format.R32G32B32A32_Float,
                RHIFormat.R32G32B32A32_UInt     => Format.R32G32B32A32_UInt,
                RHIFormat.R32G32B32A32_SInt     => Format.R32G32B32A32_SInt,
                RHIFormat.R11G11B10_Float       => Format.R11G11B10_Float,
                RHIFormat.R9G9B9E5_SharedExp    => Format.R9G9B9E5_SharedExp,
                RHIFormat.D16_UNorm             => Format.D16_UNorm,
                RHIFormat.D32_Float             => Format.D32_Float,
                RHIFormat.D24_UNorm_S8_UInt     => Format.D24_UNorm_S8_UInt,
                RHIFormat.D32_Float_S8X24_UInt  => Format.D32_Float_S8X24_UInt,
                RHIFormat.BC1_UNorm             => Format.BC1_UNorm,
                RHIFormat.BC1_UNorm_SRgb        => Format.BC1_UNorm_SRgb,
                RHIFormat.BC3_UNorm             => Format.BC3_UNorm,
                RHIFormat.BC3_UNorm_SRgb        => Format.BC3_UNorm_SRgb,
                RHIFormat.BC4_UNorm             => Format.BC4_UNorm,
                RHIFormat.BC5_UNorm             => Format.BC5_UNorm,
                RHIFormat.BC6H_UF16             => Format.BC6H_Uf16,
                RHIFormat.BC7_UNorm             => Format.BC7_UNorm,
                RHIFormat.BC7_UNorm_SRgb        => Format.BC7_UNorm_SRgb,
                _                               => Format.Unknown
            };
        }

        /// <summary>将 RHIResourceState 转换为 Vortice ResourceStates（值完全对应，直接强转）</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ResourceStates ToResourceStates(RHIResourceState state)
            => (ResourceStates)(uint)state;

        /// <summary>将 Vortice ResourceStates 转换为 RHIResourceState</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static RHIResourceState ToRHIResourceStates(ResourceStates state)
            => (RHIResourceState)(uint)state;

        /// <summary>将 RHIHeapType 转换为 Vortice HeapType</summary>
        public static HeapType ToHeapType(RHIHeapType type) => type switch
        {
            RHIHeapType.Default  => HeapType.Default,
            RHIHeapType.Upload   => HeapType.Upload,
            RHIHeapType.Readback => HeapType.Readback,
            _ => HeapType.Default
        };

        /// <summary>将 RHIQueueType 转换为 Vortice CommandListType</summary>
        public static CommandListType ToCommandListType(RHIQueueType type) => type switch
        {
            RHIQueueType.Graphics => CommandListType.Direct,
            RHIQueueType.Compute  => CommandListType.Compute,
            RHIQueueType.Copy     => CommandListType.Copy,
            _ => CommandListType.Direct
        };

        /// <summary>将 RHIDescriptorHeapType 转换为 Vortice DescriptorHeapType</summary>
        public static DescriptorHeapType ToDescriptorHeapType(RHIDescriptorHeapType type) => type switch
        {
            RHIDescriptorHeapType.CBV_SRV_UAV => DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView,
            RHIDescriptorHeapType.Sampler     => DescriptorHeapType.Sampler,
            RHIDescriptorHeapType.RTV         => DescriptorHeapType.RenderTargetView,
            RHIDescriptorHeapType.DSV         => DescriptorHeapType.DepthStencilView,
            _ => DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView
        };

        /// <summary>将 RHIFilter 转换为 Vortice Filter</summary>
        public static Filter ToFilter(RHIFilter filter) => filter switch
        {
            RHIFilter.MinMagMipPoint        => Filter.MinMagMipPoint,
            RHIFilter.MinMagMipLinear       => Filter.MinMagMipLinear,
            RHIFilter.Anisotropic           => Filter.Anisotropic,
            RHIFilter.ComparisonMinMagMipPoint  => Filter.ComparisonMinMagMipPoint,
            RHIFilter.ComparisonMinMagMipLinear => Filter.ComparisonMinMagMipLinear,
            RHIFilter.ComparisonAnisotropic     => Filter.ComparisonAnisotropic,
            RHIFilter.MinimumMinMagMipLinear    => Filter.MinimumMinMagMipLinear,
            RHIFilter.MaximumMinMagMipLinear    => Filter.MaximumMinMagMipLinear,
            _ => Filter.MinMagMipLinear
        };

        /// <summary>将 RHITextureAddressMode 转换为 Vortice TextureAddressMode</summary>
        public static TextureAddressMode ToAddressMode(RHITextureAddressMode mode) => mode switch
        {
            RHITextureAddressMode.Wrap       => TextureAddressMode.Wrap,
            RHITextureAddressMode.Mirror     => TextureAddressMode.Mirror,
            RHITextureAddressMode.Clamp      => TextureAddressMode.Clamp,
            RHITextureAddressMode.Border     => TextureAddressMode.Border,
            RHITextureAddressMode.MirrorOnce => TextureAddressMode.MirrorOnce,
            _ => TextureAddressMode.Wrap
        };

        /// <summary>将 RHIComparisonFunc 转换为 Vortice ComparisonFunction</summary>
        public static ComparisonFunction ToComparisonFunc(RHIComparisonFunc func) => func switch
        {
            RHIComparisonFunc.Never         => ComparisonFunction.Never,
            RHIComparisonFunc.Less          => ComparisonFunction.Less,
            RHIComparisonFunc.Equal         => ComparisonFunction.Equal,
            RHIComparisonFunc.LessEqual     => ComparisonFunction.LessEqual,
            RHIComparisonFunc.Greater       => ComparisonFunction.Greater,
            RHIComparisonFunc.NotEqual      => ComparisonFunction.NotEqual,
            RHIComparisonFunc.GreaterEqual  => ComparisonFunction.GreaterEqual,
            RHIComparisonFunc.Always        => ComparisonFunction.Always,
            _ => ComparisonFunction.Less
        };

        /// <summary>将 RHIComparison 转换为 Vortice ComparisonFunction (重载)</summary>
        public static ComparisonFunction ToComparisonFunc(RHIComparison func) => func switch
        {
            RHIComparison.Never         => ComparisonFunction.Never,
            RHIComparison.Less          => ComparisonFunction.Less,
            RHIComparison.Equal         => ComparisonFunction.Equal,
            RHIComparison.LessEqual     => ComparisonFunction.LessEqual,
            RHIComparison.Greater       => ComparisonFunction.Greater,
            RHIComparison.NotEqual      => ComparisonFunction.NotEqual,
            RHIComparison.GreaterEqual  => ComparisonFunction.GreaterEqual,
            RHIComparison.Always        => ComparisonFunction.Always,
            _ => ComparisonFunction.Never
        };

        /// <summary>将 RHIShaderVisibility 转换为 Vortice ShaderVisibility</summary>
        public static ShaderVisibility ToShaderVisibility(RHIShaderVisibility vis) => vis switch
        {
            RHIShaderVisibility.All         => ShaderVisibility.All,
            RHIShaderVisibility.Vertex      => ShaderVisibility.Vertex,
            RHIShaderVisibility.Hull        => ShaderVisibility.Hull,
            RHIShaderVisibility.Domain      => ShaderVisibility.Domain,
            RHIShaderVisibility.Geometry    => ShaderVisibility.Geometry,
            RHIShaderVisibility.Pixel       => ShaderVisibility.Pixel,
            RHIShaderVisibility.Amplification => ShaderVisibility.Amplification,
            RHIShaderVisibility.Mesh        => ShaderVisibility.Mesh,
            _ => ShaderVisibility.All
        };

        /// <summary>将 RHIRootParameterType 转换为 Vortice RootParameterType</summary>
        public static RootParameterType ToRootParameterType(RHIRootParameterType type) => type switch
        {
            RHIRootParameterType.DescriptorTable => RootParameterType.DescriptorTable,
            RHIRootParameterType.Constants       => RootParameterType.Constant32Bits,
            RHIRootParameterType.CBV             => RootParameterType.ConstantBufferView,
            RHIRootParameterType.SRV             => RootParameterType.ShaderResourceView,
            RHIRootParameterType.UAV             => RootParameterType.UnorderedAccessView,
            _ => RootParameterType.DescriptorTable
        };

        /// <summary>将 RHIDescriptorRangeType 转换为 Vortice DescriptorRangeType</summary>
        public static DescriptorRangeType ToDescriptorRangeType(RHIDescriptorRangeType type) => type switch
        {
            RHIDescriptorRangeType.SRV => DescriptorRangeType.ShaderResourceView,
            RHIDescriptorRangeType.UAV => DescriptorRangeType.UnorderedAccessView,
            RHIDescriptorRangeType.CBV => DescriptorRangeType.ConstantBufferView,
            RHIDescriptorRangeType.Sampler => DescriptorRangeType.Sampler,
            _ => DescriptorRangeType.ShaderResourceView
        };

        /// <summary>将 RHIPrimitiveTopologyType 转换为 Vortice PrimitiveTopologyType</summary>
        public static PrimitiveTopologyType ToPrimitiveTopologyType(RHIPrimitiveTopologyType type) => type switch
        {
            RHIPrimitiveTopologyType.Undefined  => PrimitiveTopologyType.Undefined,
            RHIPrimitiveTopologyType.Point      => PrimitiveTopologyType.Point,
            RHIPrimitiveTopologyType.Line       => PrimitiveTopologyType.Line,
            RHIPrimitiveTopologyType.Triangle   => PrimitiveTopologyType.Triangle,
            RHIPrimitiveTopologyType.Patch      => PrimitiveTopologyType.Patch,
            _ => PrimitiveTopologyType.Triangle
        };

        /// <summary>将 RHIPrimitiveTopology（完整拓扑）推导为 Vortice PrimitiveTopologyType（类型类别）</summary>
        public static PrimitiveTopologyType ToPrimitiveTopologyType(RHIPrimitiveTopology topology) => topology switch
        {
            RHIPrimitiveTopology.PointList     => PrimitiveTopologyType.Point,
            RHIPrimitiveTopology.LineList      => PrimitiveTopologyType.Line,
            RHIPrimitiveTopology.LineStrip     => PrimitiveTopologyType.Line,
            RHIPrimitiveTopology.LineListAdj   => PrimitiveTopologyType.Line,
            RHIPrimitiveTopology.LineStripAdj  => PrimitiveTopologyType.Line,
            RHIPrimitiveTopology.PatchList     => PrimitiveTopologyType.Patch,
            _ => PrimitiveTopologyType.Triangle
        };

        /// <summary>将 RHIPrimitiveTopology 转换为 Vortice PrimitiveTopology</summary>
        public static PrimitiveTopology ToPrimitiveTopology(RHIPrimitiveTopology topology) => topology switch
        {
            RHIPrimitiveTopology.PointList          => PrimitiveTopology.PointList,
            RHIPrimitiveTopology.LineList           => PrimitiveTopology.LineList,
            RHIPrimitiveTopology.LineStrip          => PrimitiveTopology.LineStrip,
            RHIPrimitiveTopology.TriangleList       => PrimitiveTopology.TriangleList,
            RHIPrimitiveTopology.TriangleStrip      => PrimitiveTopology.TriangleStrip,
            RHIPrimitiveTopology.LineListAdj        => PrimitiveTopology.LineListAdjacency,
            RHIPrimitiveTopology.LineStripAdj       => PrimitiveTopology.LineStripAdjacency,
            RHIPrimitiveTopology.TriangleListAdj    => PrimitiveTopology.TriangleListAdjacency,
            RHIPrimitiveTopology.TriangleStripAdj   => PrimitiveTopology.TriangleStripAdjacency,
            _ => PrimitiveTopology.TriangleList
        };

        /// <summary>将 RHIFormat 转换为 IndexBufferView 使用的 Format</summary>
        public static Format ToIndexFormat(RHIFormat fmt) => fmt switch
        {
            RHIFormat.R16_UInt  => Format.R16_UInt,
            RHIFormat.R32_UInt  => Format.R32_UInt,
            _ => Format.R32_UInt
        };

        /// <summary>将 RHIBorderColor 转换为 Vortice StaticBorderColor</summary>
        public static StaticBorderColor ToStaticBorderColor(RHIBorderColor color) => color switch
        {
            RHIBorderColor.TransparentBlack => StaticBorderColor.TransparentBlack,
            RHIBorderColor.OpaqueBlack      => StaticBorderColor.OpaqueBlack,
            RHIBorderColor.OpaqueWhite      => StaticBorderColor.OpaqueWhite,
            _ => StaticBorderColor.TransparentBlack
        };

        /// <summary>将 RHIShaderStage 转换为 Shader 编译目标前缀字符串</summary>
        public static string ToShaderStagePrefix(RHIShaderStage stage) => stage switch
        {
            RHIShaderStage.Vertex         => "vs",
            RHIShaderStage.Hull           => "hs",
            RHIShaderStage.Domain         => "ds",
            RHIShaderStage.Geometry       => "gs",
            RHIShaderStage.Pixel          => "ps",
            RHIShaderStage.Compute        => "cs",
            RHIShaderStage.Mesh           => "ms",
            RHIShaderStage.Amplification  => "as",
            RHIShaderStage.Library        => "lib",
            _ => "ps"
        };

        /// <summary>将 RHIShaderModel 转换为 Shader 编译版本字符串</summary>
        public static string ToShaderModelString(RHIShaderModel sm) => sm switch
        {
            RHIShaderModel.SM_5_1 => "5_1",
            RHIShaderModel.SM_6_0 => "6_0",
            RHIShaderModel.SM_6_1 => "6_1",
            RHIShaderModel.SM_6_2 => "6_2",
            RHIShaderModel.SM_6_3 => "6_3",
            RHIShaderModel.SM_6_4 => "6_4",
            RHIShaderModel.SM_6_5 => "6_5",
            RHIShaderModel.SM_6_6 => "6_6",
            RHIShaderModel.SM_6_7 => "6_7",
            RHIShaderModel.SM_6_8 => "6_8",
            _ => "6_0"
        };
    }

    // ==============================================================================================
    //  区域二: D3D12Buffer — 缓冲区资源封装
    // ==============================================================================================

    /// <summary>
    /// D3D12 缓冲区资源，封装 ID3D12Resource。
    /// 支持 Vertex/Index/Constant/Structured/ByteAddress/Upload 等类型。
    /// </summary>
    public sealed class D3D12Buffer : RHIBuffer
    {
        internal readonly ID3D12Resource Resource;

        /// <summary>缓冲区的 GPU 虚拟地址（用于根描述符绑定）</summary>
        public override ulong GpuVirtualAddress { get; }

        /// <summary>缓冲区描述信息</summary>
        public override RHIBufferDesc? BufferDesc { get; }

        /// <summary>资源当前状态（用于跟踪屏障）</summary>
        public override RHIResourceState CurrentState { get; set; }

        /// <summary>资源名称</summary>
        public override string Name { get; set; } = string.Empty;

        /// <summary>已分配字节数</summary>
        public override ulong AllocatedBytes { get; }

        internal D3D12Buffer(ID3D12Resource resource, in RHIBufferDesc desc, RHIResourceState initialState, string name)
        {
            Resource = resource;
            BufferDesc = desc;
            CurrentState = initialState;
            Name = name;
            AllocatedBytes = (ulong)desc.SizeInBytes;
            GpuVirtualAddress = resource.GPUVirtualAddress;

            if (!string.IsNullOrEmpty(name))
                resource.Name = name;
        }

        /// <summary>将上传堆缓冲区映射到 CPU 可写内存</summary>
        public IntPtr Map()
        {
            Resource.Map(0, IntPtr.Zero, out IntPtr pData);
            return pData;
        }

        /// <summary>取消映射</summary>
        public void Unmap()
        {
            Resource.Unmap(0, IntPtr.Zero);
        }

        public override void Dispose()
        {
            Resource?.Dispose();
        }
    }

    // ==============================================================================================
    //  区域三: D3D12Texture — 纹理资源封装
    // ==============================================================================================

    /// <summary>
    /// D3D12 纹理资源，封装 ID3D12Resource。
    /// 支持 1D/2D/3D/Cube/Array 各类型纹理及 RenderTarget/DepthStencil/UnorderedAccess 标记。
    /// </summary>
    public sealed class D3D12Texture : RHITexture
    {
        internal readonly ID3D12Resource Resource;

        public override RHITextureDesc? TextureDesc { get; }
        public override RHIResourceState CurrentState { get; set; }
        public override string Name { get; set; } = string.Empty;
        public override ulong AllocatedBytes { get; }

        /// <summary>纹理的 DXGI 原生格式</summary>
        public Format DxgiFormat { get; }

        internal D3D12Texture(ID3D12Resource resource, in RHITextureDesc desc, RHIResourceState initialState, string name)
        {
            Resource = resource;
            TextureDesc = desc;
            CurrentState = initialState;
            Name = name;
            DxgiFormat = D3D12FormatMapper.ToDxgiFormat(desc.Format);
            // 估算分配大小（粗略计算）
            AllocatedBytes = (ulong)desc.Width * (ulong)desc.Height * (ulong)Math.Max(1, desc.DepthOrArraySize)
                             * (ulong)Math.Max(1, desc.MipLevels) * 4;

            if (!string.IsNullOrEmpty(name))
                resource.Name = name;
        }

        /// <summary>从交换链获取的纹理（无需重新创建，包装现有资源）</summary>
        internal D3D12Texture(ID3D12Resource resource, in RHITextureDesc desc, RHIResourceState initialState)
        {
            Resource = resource;
            TextureDesc = desc;
            CurrentState = initialState;
            DxgiFormat = D3D12FormatMapper.ToDxgiFormat(desc.Format);
            AllocatedBytes = 0;
        }

        public override void Dispose()
        {
            Resource?.Dispose();
        }
    }

    // ==============================================================================================
    //  区域四: D3D12DescriptorHeap — 描述符堆管理
    // ==============================================================================================

    /// <summary>
    /// 描述符堆管理器，支持 CBV_SRV_UAV / Sampler / RTV / DSV 四种类型。
    /// 内部使用空闲列表（栈）实现描述符分配与回收。
    /// </summary>
    public sealed class D3D12DescriptorHeap : RHIDescriptorHeap
    {
        internal readonly ID3D12DescriptorHeap Heap;
        private readonly CpuDescriptorHandle _cpuStart;
        private readonly GpuDescriptorHandle _gpuStart;
        private readonly uint _descriptorSize;
        private readonly Stack<int> _freeList = new();
        private int _nextIndex = 0;

        public override RHIDescriptorHeapType Type { get; protected set; }
        public override int Capacity { get; protected set; }
        public override int AllocatedCount => _nextIndex - _freeList.Count;
        public override bool ShaderVisible { get; }
        public override string Name { get; set; } = string.Empty;

        /// <summary>每个描述符的句柄增量大小（字节）</summary>
        public override uint DescriptorSize => _descriptorSize;

        internal D3D12DescriptorHeap(D3D12Device device, RHIDescriptorHeapType type, int capacity, bool shaderVisible, string name)
        {
            Type = type;
            Capacity = capacity;
            ShaderVisible = shaderVisible;
            Name = name;

            var heapType = D3D12FormatMapper.ToDescriptorHeapType(type);
            // RTV / DSV 堆不能标记为 ShaderVisible
            bool canBeShaderVisible = heapType != DescriptorHeapType.RenderTargetView
                                   && heapType != DescriptorHeapType.DepthStencilView;
            var flags = (shaderVisible && canBeShaderVisible)
                ? DescriptorHeapFlags.ShaderVisible
                : DescriptorHeapFlags.None;

            var desc = new DescriptorHeapDescription
            {
                Type = heapType,
                DescriptorCount = (uint)capacity,
                Flags = flags,
                NodeMask = 0
            };

            Heap = device.NativeDevice.CreateDescriptorHeap(desc);
            _cpuStart = Heap.GetCPUDescriptorHandleForHeapStart();
            _gpuStart = (flags & DescriptorHeapFlags.ShaderVisible) != 0
                ? Heap.GetGPUDescriptorHandleForHeapStart()
                : default;
            _descriptorSize = device.NativeDevice.GetDescriptorHandleIncrementSize(heapType);

            if (!string.IsNullOrEmpty(name))
                Heap.Name = name;
        }

        /// <summary>分配一个描述符句柄</summary>
        public override RHIDescriptorHandle Allocate()
        {
            int index;
            if (_freeList.Count > 0)
            {
                index = _freeList.Pop();
            }
            else
            {
                if (_nextIndex >= Capacity)
                    throw new InvalidOperationException($"描述符堆已满 (容量={Capacity}, 类型={Type})");
                index = _nextIndex++;
            }

            CpuDescriptorHandle cpu = _cpuStart + (index * (int)_descriptorSize);
            GpuDescriptorHandle gpu = _gpuStart.Ptr != 0
                ? _gpuStart + (index * (int)_descriptorSize)
                : default;

            return new RHIDescriptorHandle
            {
                CpuHandle = (ulong)cpu.Ptr.ToInt64(),
                GpuHandle = gpu.Ptr,
                Index = index
            };
        }

        /// <summary>释放描述符句柄（归还到空闲列表）</summary>
        public override void Free(RHIDescriptorHandle handle)
        {
            if (handle.Index >= 0 && handle.Index < Capacity)
                _freeList.Push(handle.Index);
        }

        /// <summary>根据索引获取 CPU 描述符句柄</summary>
        public CpuDescriptorHandle GetCpuHandle(int index)
            => _cpuStart + (index * (int)_descriptorSize);

        /// <summary>根据索引获取 GPU 描述符句柄</summary>
        public GpuDescriptorHandle GetGpuHandle(int index)
            => _gpuStart + (index * (int)_descriptorSize);

        public override void Dispose()
        {
            Heap?.Dispose();
        }
    }

    // ==============================================================================================
    //  区域五: D3D12RootSignature — 根签名封装
    // ==============================================================================================

    /// <summary>
    /// D3D12 根签名，通过 D3D12SerializeVersionedRootSignature 序列化后创建。
    /// 支持 DescriptorTable / Constants / CBV / SRV / UAV 参数及静态采样器。
    /// </summary>
    public sealed class D3D12RootSignature : RHIRootSignature
    {
        internal readonly ID3D12RootSignature NativeRootSignature;
        public override int NumParameters { get; }
        public override string Name { get; set; } = string.Empty;

        /// <summary>根参数描述缓存（用于绑定时的校验）</summary>
        internal readonly RHIRootParameter[] Parameters;

        internal D3D12RootSignature(ID3D12RootSignature rootSig, RHIRootParameter[] parameters, string name)
        {
            NativeRootSignature = rootSig;
            Parameters = parameters;
            NumParameters = parameters.Length;
            Name = name;
            if (!string.IsNullOrEmpty(name))
                rootSig.Name = name;
        }

        public override void Dispose()
        {
            NativeRootSignature?.Dispose();
        }
    }

    // ==============================================================================================
    //  区域六: D3D12PipelineState — 管线状态对象封装
    // ==============================================================================================

    /// <summary>
    /// D3D12 管线状态对象 (PSO)，支持 Graphics / Compute / Mesh Shader 三种类型。
    /// </summary>
    public sealed class D3D12PipelineState : RHIPipelineState
    {
        internal readonly ID3D12PipelineState NativePSO;
        public override RHIPipelineType PipelineType { get; }
        public override string Name { get; set; } = string.Empty;

        internal D3D12PipelineState(ID3D12PipelineState pso, RHIPipelineType type, string name)
        {
            NativePSO = pso;
            PipelineType = type;
            Name = name;
            if (!string.IsNullOrEmpty(name))
                pso.Name = name;
        }

        public override void Dispose()
        {
            NativePSO?.Dispose();
        }
    }

    // ==============================================================================================
    //  区域七: D3D12Fence — 围栏封装
    // ==============================================================================================

    /// <summary>
    /// D3D12 围栏，用于 GPU/CPU 同步。
    /// 内部维护 Win32 事件句柄，支持 SetEventOnCompletion 异步等待。
    /// </summary>
    public sealed class D3D12Fence : RHIFence
    {
        internal readonly ID3D12Fence NativeFence;
        private readonly IntPtr _eventHandle;

        public override ulong CompletedValue => NativeFence.CompletedValue;
        public override string Name { get; set; } = string.Empty;

        internal D3D12Fence(ID3D12Fence fence, ulong initialValue, string name)
        {
            NativeFence = fence;
            Name = name;
            // 创建自动重置事件用于等待
            _eventHandle = CreateEvent(IntPtr.Zero, false, false, IntPtr.Zero);
            if (_eventHandle == IntPtr.Zero)
                throw new InvalidOperationException("无法创建 Fence 等待事件句柄");

            if (!string.IsNullOrEmpty(name))
                fence.Name = name;
        }

        /// <summary>从 CPU 发出信号</summary>
        public override void Signal(ulong value)
        {
            NativeFence.Signal(value).CheckError();
        }

        /// <summary>等待围栏达到指定值（阻塞当前线程）</summary>
        public override void Wait(ulong value, uint timeoutMs = uint.MaxValue)
        {
            if (NativeFence.CompletedValue >= value)
                return;

            NativeFence.SetEventOnCompletion(value, _eventHandle).CheckError();
            WaitForSingleObject(_eventHandle, timeoutMs);
        }

        /// <summary>在命令队列上发出信号（GPU 侧）</summary>
        public void SignalOnQueue(ID3D12CommandQueue queue, ulong value)
        {
            queue.Signal(NativeFence, value).CheckError();
        }

        /// <summary>在命令队列上等待（GPU 侧）</summary>
        public void WaitOnQueue(ID3D12CommandQueue queue, ulong value)
        {
            queue.Wait(NativeFence, value).CheckError();
        }

        public override void Dispose()
        {
            NativeFence?.Dispose();
            if (_eventHandle != IntPtr.Zero)
                CloseHandle(_eventHandle);
        }

        // ── Win32 API P/Invoke ──
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateEvent(IntPtr lpEventAttributes, bool bManualReset, bool bInitialState, IntPtr lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);
    }

    // ==============================================================================================
    //  区域八: D3D12CommandQueue — 命令队列封装
    // ==============================================================================================

    /// <summary>
    /// D3D12 命令队列，支持 Direct / Compute / Copy 三种类型。
    /// 提供命令列表执行、GPU 围栏信号/等待、刷新等功能。
    /// </summary>
    public sealed class D3D12CommandQueue : RHICommandQueue
    {
        internal readonly ID3D12CommandQueue Queue;
        internal readonly D3D12Device Device;
        private readonly D3D12Fence _fence;
        private ulong _fenceValue = 0;

        public override RHIQueueType QueueType { get; protected set; }
        public override string Name { get; set; } = string.Empty;

        internal D3D12CommandQueue(D3D12Device device, RHIQueueType type, string name)
        {
            Device = device;
            QueueType = type;
            Name = name;

            var cmdListType = D3D12FormatMapper.ToCommandListType(type);
            var desc = new CommandQueueDescription
            {
                Type = cmdListType,
                Priority = CommandQueuePriority.Normal,
                Flags = CommandQueueFlags.None,
                NodeMask = 0
            };

            Queue = device.NativeDevice.CreateCommandQueue(desc);
            _fence = device.CreateFenceInternal(0, name + "_Fence");

            if (!string.IsNullOrEmpty(name))
                Queue.Name = name;
        }

        /// <summary>执行命令列表数组</summary>
        public override void ExecuteCommandLists(params RHICommandList[] commandLists)
        {
            if (commandLists == null || commandLists.Length == 0)
                return;

            var lists = new ID3D12CommandList[commandLists.Length];
            for (int i = 0; i < commandLists.Length; i++)
                lists[i] = ((D3D12CommandList)commandLists[i]).NativeCommandList;

            Queue.ExecuteCommandLists(lists);
        }

        /// <summary>在 GPU 上发出信号</summary>
        public override void Signal(RHIFence fence, ulong value)
        {
            var d3dFence = (D3D12Fence)fence;
            d3dFence.SignalOnQueue(Queue, value);
        }

        /// <summary>在 GPU 上等待围栏</summary>
        public override void Wait(RHIFence fence, ulong value)
        {
            var d3dFence = (D3D12Fence)fence;
            d3dFence.WaitOnQueue(Queue, value);
        }

        /// <summary>刷新：发出信号并等待 GPU 完成</summary>
        public override void Flush()
        {
            _fenceValue++;
            _fence.SignalOnQueue(Queue, _fenceValue);
            _fence.Wait(_fenceValue);
        }

        /// <summary>获取此队列关联的内部围栏</summary>
        internal D3D12Fence Fence => _fence;

        /// <summary>当前围栏值</summary>
        internal ulong CurrentFenceValue => _fenceValue;

        /// <summary>递增围栏值并返回新值</summary>
        internal ulong IncrementFenceValue()
        {
            return ++_fenceValue;
        }

        public override void Dispose()
        {
            _fence?.Dispose();
            Queue?.Dispose();
        }
    }

    // ==============================================================================================
    //  区域九: D3D12CommandList — 命令列表录制
    // ==============================================================================================

    /// <summary>
    /// D3D12 命令列表，封装 ID3D12GraphicsCommandList + ID3D12CommandAllocator。
    /// 支持完整的图形/计算/拷贝命令录制：资源屏障、绘制、派发、描述符绑定、
    /// 视口/裁剪设置、渲染目标绑定、Clear、Copy、间接绘制等。
    /// </summary>
    public sealed class D3D12CommandList : RHICommandList
    {
        internal readonly ID3D12GraphicsCommandList NativeCommandList;
        internal readonly ID3D12CommandAllocator Allocator;
        private readonly D3D12Device _device;

        public override string Name { get; set; } = string.Empty;

        internal D3D12CommandList(D3D12Device device, RHIQueueType type, string name)
        {
            _device = device;
            Name = name;
            QueueType = type;
            var cmdListType = D3D12FormatMapper.ToCommandListType(type);

            Allocator = device.NativeDevice.CreateCommandAllocator(cmdListType);
            NativeCommandList = device.NativeDevice.CreateCommandList(
                0, cmdListType, Allocator, null);

            // 命令列表创建时处于"开放"状态，需要先 Close 以便后续 Reset
            NativeCommandList.Close();

            if (!string.IsNullOrEmpty(name))
            {
                Allocator.Name = name + "_Allocator";
                NativeCommandList.Name = name;
            }
        }

        /// <summary>重置命令列表与分配器，开始新一轮录制</summary>
        public override void Reset(RHIPipelineState? initialState = null)
        {
            Allocator.Reset();
            ID3D12PipelineState? pso = initialState != null
                ? ((D3D12PipelineState)initialState).NativePSO
                : null;
            NativeCommandList.Reset(Allocator, pso);
        }

        /// <summary>关闭命令列表，完成录制</summary>
        public override void Close()
        {
            NativeCommandList.Close();
        }

        // ── 资源屏障 ──

        /// <summary>添加资源屏障：状态转换</summary>
        public override void ResourceBarrier(RHIResource resource, RHIResourceState stateBefore, RHIResourceState stateAfter, uint subresource = 0xFFFFFFFF)
        {
            var d3dRes = GetNativeResource(resource);
            var barrier = ResourceBarrier.Transition(
                d3dRes,
                D3D12FormatMapper.ToResourceStates(stateBefore),
                D3D12FormatMapper.ToResourceStates(stateAfter),
                subresource);
            NativeCommandList.ResourceBarrier(barrier);
        }

        /// <summary>添加资源屏障：UAV 屏障</summary>
        public override void UAVBarrier(RHIResource? resource)
        {
            ID3D12Resource? res = resource != null ? GetNativeResource(resource) : null;
            var barrier = ResourceBarrier.UAV(res);
            NativeCommandList.ResourceBarrier(barrier);
        }

        /// <summary>添加多个资源屏障（批量）</summary>
        public void ResourceBarriers(params ResourceBarrier[] barriers)
        {
            if (barriers.Length > 0)
                NativeCommandList.ResourceBarrier(barriers);
        }

        // ── Copy 操作 ──

        /// <summary>拷贝缓冲区区域</summary>
        public override void CopyBufferRegion(RHIBuffer dst, ulong dstOffset, RHIBuffer src, ulong srcOffset, ulong numBytes)
        {
            NativeCommandList.CopyBufferRegion(
                ((D3D12Buffer)dst).Resource, dstOffset,
                ((D3D12Buffer)src).Resource, srcOffset, numBytes);
        }

        /// <summary>拷贝整个资源</summary>
        public override void CopyResource(RHIResource dst, RHIResource src)
        {
            NativeCommandList.CopyResource(GetNativeResource(dst), GetNativeResource(src));
        }

        /// <summary>拷贝纹理区域（从缓冲区到纹理）</summary>
        public override void CopyTextureRegion(RHITexture dst, uint dstSubresource, uint dstX, uint dstY, uint dstZ,
                                                RHIBuffer src, ulong srcOffset, uint srcRowPitch, uint srcSlicePitch,
                                                uint srcWidth, uint srcHeight)
        {
            var dstLoc = new TextureCopyLocation(((D3D12Texture)dst).Resource, dstSubresource);
            var footprint = new PlacedSubresourceFootPrint
            {
                Offset = srcOffset,
                Footprint = new SubresourceFootPrint
                {
                    Format = ((D3D12Texture)dst).DxgiFormat,
                    Width = (int)srcWidth,
                    Height = (int)srcHeight,
                    Depth = 1,
                    RowPitch = (int)srcRowPitch
                }
            };
            var srcLoc = new TextureCopyLocation(((D3D12Buffer)src).Resource, footprint);

            NativeCommandList.CopyTextureRegion(dstLoc, dstX, dstY, dstZ, srcLoc, null);
        }

        // ── 绘制 / 派发 ──

        public override void DrawInstanced(uint vertexCountPerInstance, uint instanceCount,
                                            uint startVertexLocation, uint startInstanceLocation)
        {
            NativeCommandList.DrawInstanced(vertexCountPerInstance, instanceCount,
                                            startVertexLocation, startInstanceLocation);
        }

        public override void DrawIndexedInstanced(uint indexCountPerInstance, uint instanceCount,
                                                   uint startIndexLocation, int baseVertexLocation,
                                                   uint startInstanceLocation)
        {
            NativeCommandList.DrawIndexedInstanced(indexCountPerInstance, instanceCount,
                                                   startIndexLocation, baseVertexLocation,
                                                   startInstanceLocation);
        }

        public override void Dispatch(uint groupCountX, uint groupCountY, uint groupCountZ)
        {
            NativeCommandList.Dispatch(groupCountX, groupCountY, groupCountZ);
        }

        /// <summary>Mesh Shader 线程组派发</summary>
        public override void DispatchMesh(uint groupCountX, uint groupCountY, uint groupCountZ)
        {
            NativeCommandList.DispatchMesh(groupCountX, groupCountY, groupCountZ);
        }

        public override void DrawIndexedInstancedIndirect(RHIBuffer argsBuffer, ulong argsOffset)
        {
            var sig = _device.GetDrawIndexedSignature();
            NativeCommandList.ExecuteIndirect(sig, ((D3D12Buffer)argsBuffer).Resource, argsOffset, null, 0);
        }

        public override void DispatchIndirect(RHIBuffer argsBuffer, ulong argsOffset)
        {
            var sig = _device.GetDispatchSignature();
            NativeCommandList.ExecuteIndirect(sig, ((D3D12Buffer)argsBuffer).Resource, argsOffset, null, 0);
        }

        // ── 管线状态 / 根签名绑定 ──

        public override void SetPipelineState(RHIPipelineState pso)
        {
            NativeCommandList.SetPipelineState(((D3D12PipelineState)pso).NativePSO);
        }

        public override void SetGraphicsRootSignature(RHIRootSignature rootSig)
        {
            NativeCommandList.SetGraphicsRootSignature(((D3D12RootSignature)rootSig).NativeRootSignature);
        }

        public override void SetComputeRootSignature(RHIRootSignature rootSig)
        {
            NativeCommandList.SetComputeRootSignature(((D3D12RootSignature)rootSig).NativeRootSignature);
        }

        public override void SetGraphicsRootDescriptorTable(uint rootParameterIndex, RHIDescriptorHandle handle)
        {
            NativeCommandList.SetGraphicsRootDescriptorTable(rootParameterIndex,
                new GpuDescriptorHandle(handle.GpuHandle));
        }

        public override void SetComputeRootDescriptorTable(uint rootParameterIndex, RHIDescriptorHandle handle)
        {
            NativeCommandList.SetComputeRootDescriptorTable(rootParameterIndex,
                new GpuDescriptorHandle(handle.GpuHandle));
        }

        public override void SetGraphicsRootConstantBufferView(uint rootParameterIndex, RHIBuffer buffer)
        {
            NativeCommandList.SetGraphicsRootConstantBufferView(rootParameterIndex,
                ((D3D12Buffer)buffer).GpuVirtualAddress);
        }

        public override void SetComputeRootConstantBufferView(uint rootParameterIndex, RHIBuffer buffer)
        {
            NativeCommandList.SetComputeRootConstantBufferView(rootParameterIndex,
                ((D3D12Buffer)buffer).GpuVirtualAddress);
        }

        public override void SetGraphicsRootShaderResourceView(uint rootParameterIndex, RHIBuffer buffer)
        {
            NativeCommandList.SetGraphicsRootShaderResourceView(rootParameterIndex,
                ((D3D12Buffer)buffer).GpuVirtualAddress);
        }

        public override void SetGraphicsRootUnorderedAccessView(uint rootParameterIndex, RHIBuffer buffer)
        {
            NativeCommandList.SetGraphicsRootUnorderedAccessView(rootParameterIndex,
                ((D3D12Buffer)buffer).GpuVirtualAddress);
        }

        // ── GPU 虚拟地址重载（用于直接传入 GpuVirtualAddress 的旧式调用）──

        public override void SetGraphicsRootConstantBufferView(uint rootParameterIndex, ulong gpuAddress)
        {
            NativeCommandList.SetGraphicsRootConstantBufferView(rootParameterIndex, gpuAddress);
        }

        public override void SetComputeRootConstantBufferView(uint rootParameterIndex, ulong gpuAddress)
        {
            NativeCommandList.SetComputeRootConstantBufferView(rootParameterIndex, gpuAddress);
        }

        public override void SetGraphicsRootShaderResourceView(uint rootParameterIndex, ulong gpuAddress)
        {
            NativeCommandList.SetGraphicsRootShaderResourceView(rootParameterIndex, gpuAddress);
        }

        public override void SetComputeRootShaderResourceView(uint rootParameterIndex, ulong gpuAddress)
        {
            NativeCommandList.SetComputeRootShaderResourceView(rootParameterIndex, gpuAddress);
        }

        public override void SetGraphicsRootUnorderedAccessView(uint rootParameterIndex, ulong gpuAddress)
        {
            NativeCommandList.SetGraphicsRootUnorderedAccessView(rootParameterIndex, gpuAddress);
        }

        public override void SetComputeRootUnorderedAccessView(uint rootParameterIndex, ulong gpuAddress)
        {
            NativeCommandList.SetComputeRootUnorderedAccessView(rootParameterIndex, gpuAddress);
        }

        public override unsafe void SetGraphicsRoot32BitConstants<T>(uint rootParameterIndex, uint num32BitValues,
                                                                      in T data, uint destOffsetIn32BitValues)
        {
            fixed (T* pData = &data)
            {
                NativeCommandList.SetGraphicsRoot32BitConstants(rootParameterIndex, num32BitValues,
                    (IntPtr)pData, destOffsetIn32BitValues);
            }
        }

        public override unsafe void SetComputeRoot32BitConstants<T>(uint rootParameterIndex, uint num32BitValues,
                                                                     in T data, uint destOffsetIn32BitValues)
        {
            fixed (T* pData = &data)
            {
                NativeCommandList.SetComputeRoot32BitConstants(rootParameterIndex, num32BitValues,
                    (IntPtr)pData, destOffsetIn32BitValues);
            }
        }

        // ── 输入装配 ──

        public override void IASetPrimitiveTopology(RHIPrimitiveTopology topology)
        {
            NativeCommandList.IASetPrimitiveTopology(D3D12FormatMapper.ToPrimitiveTopology(topology));
        }

        public override void IASetVertexBuffers(uint startSlot, RHIBuffer buffer, ulong offset, uint stride, uint sizeInBytes)
        {
            var view = new VertexBufferView(
                ((D3D12Buffer)buffer).GpuVirtualAddress + offset,
                sizeInBytes,
                stride);
            NativeCommandList.IASetVertexBuffers(startSlot, new[] { view });
        }

        public override void IASetIndexBuffer(RHIBuffer buffer, ulong offset, uint sizeInBytes, RHIFormat format)
        {
            var view = new IndexBufferView(
                ((D3D12Buffer)buffer).GpuVirtualAddress + offset,
                sizeInBytes,
                D3D12FormatMapper.ToIndexFormat(format));
            NativeCommandList.IASetIndexBuffer(view);
        }

        // ── 输出合并 / 渲染目标 ──

        public override void OMSetRenderTargets(RHIDescriptorHandle[] rtvHandles, RHIDescriptorHandle? dsvHandle)
        {
            var cpuHandles = new CpuDescriptorHandle[rtvHandles.Length];
            for (int i = 0; i < rtvHandles.Length; i++)
                cpuHandles[i] = new CpuDescriptorHandle((IntPtr)(long)rtvHandles[i].CpuHandle);

            CpuDescriptorHandle dsv = dsvHandle.HasValue
                ? new CpuDescriptorHandle((IntPtr)(long)dsvHandle.Value.CpuHandle)
                : default;

            NativeCommandList.OMSetRenderTargets(cpuHandles, false, dsvHandle.HasValue ? dsv : (CpuDescriptorHandle?)null);
        }

        public override void OMSetStencilRef(uint refValue)
        {
            NativeCommandList.OMSetStencilRef(refValue);
        }

        public override void OMSetBlendFactor(in Vector4 blendFactor)
        {
            NativeCommandList.OMSetBlendFactor(blendFactor);
        }

        // ── Clear 操作 ──

        public override void ClearRenderTargetView(RHIDescriptorHandle rtvHandle, in Vector4 clearColor)
        {
            var cpuHandle = new CpuDescriptorHandle((IntPtr)(long)rtvHandle.CpuHandle);
            NativeCommandList.ClearRenderTargetView(cpuHandle, new Color4(clearColor.X, clearColor.Y, clearColor.Z, clearColor.W));
        }

        public override void ClearDepthStencilView(RHIDescriptorHandle dsvHandle, bool clearDepth, bool clearStencil,
                                                     float depth, byte stencil)
        {
            var cpuHandle = new CpuDescriptorHandle((IntPtr)(long)dsvHandle.CpuHandle);
            ClearFlags flags = ClearFlags.None;
            if (clearDepth) flags |= ClearFlags.Depth;
            if (clearStencil) flags |= ClearFlags.Stencil;
            NativeCommandList.ClearDepthStencilView(cpuHandle, flags, depth, stencil);
        }

        // ── 光栅化状态 ──

        public override void RSSetViewport(float x, float y, float width, float height, float minDepth, float maxDepth)
        {
            NativeCommandList.RSSetViewport(new Viewport(x, y, width, height, minDepth, maxDepth));
        }

        public override void RSSetViewports(RHIViewport[] viewports)
        {
            var vp = new Viewport[viewports.Length];
            for (int i = 0; i < viewports.Length; i++)
                vp[i] = new Viewport(viewports[i].X, viewports[i].Y, viewports[i].Width,
                                     viewports[i].Height, viewports[i].MinDepth, viewports[i].MaxDepth);
            NativeCommandList.RSSetViewports(vp);
        }

        public override void RSSetScissorRect(int left, int top, int right, int bottom)
        {
            NativeCommandList.RSSetScissorRect(new RawRect(left, top, right, bottom));
        }

        public override void RSSetScissorRects(RHIRect[] rects)
        {
            var r = new RawRect[rects.Length];
            for (int i = 0; i < rects.Length; i++)
                r[i] = new RawRect(rects[i].Left, rects[i].Top, rects[i].Right, rects[i].Bottom);
            NativeCommandList.RSSetScissorRects(r);
        }

        // ── 资源命名 ──

        /// <summary>设置资源的 D3D12 对象名称（用于调试与 PIX/Visual Studio 图形调试器）</summary>
        public void SetResourceName(RHIResource resource, string name)
        {
            var nativeRes = GetNativeResource(resource);
            nativeRes.Name = name;
        }

        // ── 辅助方法 ──

        private static ID3D12Resource GetNativeResource(RHIResource resource)
        {
            if (resource is D3D12Buffer buf) return buf.Resource;
            if (resource is D3D12Texture tex) return tex.Resource;
            throw new ArgumentException($"未知的资源类型: {resource.GetType().Name}");
        }

        public override void Dispose()
        {
            NativeCommandList?.Dispose();
            Allocator?.Dispose();
        }
    }

    // ==============================================================================================
    //  区域十: D3D12SwapChain — 交换链封装 (DXGI Flip Model)
    // ==============================================================================================

    /// <summary>
    /// DXGI 交换链封装，使用 Flip Discard 模式。
    /// 管理后缓冲区纹理数组，支持 Present / Resize / 获取当前后缓冲区索引。
    /// </summary>
    public sealed class D3D12SwapChain : RHISwapChain
    {
        internal readonly IDXGISwapChain4 NativeSwapChain;
        private readonly D3D12Device _device;
        private readonly D3D12Texture[] _backBuffers;
        private readonly RHIDescriptorHandle[] _rtvHandles;

        public override int Width { get; protected set; }
        public override int Height { get; protected set; }
        public override RHIFormat Format { get; protected set; }
        public override int BufferCount { get; protected set; }
        public override int CurrentBackBufferIndex => (int)NativeSwapChain.CurrentBackBufferIndex;
        public override string Name { get; set; } = string.Empty;

        internal D3D12SwapChain(D3D12Device device, IntPtr hwnd, int width, int height,
                                 RHIFormat format, int bufferCount, bool vsync, string name)
        {
            _device = device;
            Width = width;
            Height = height;
            Format = format;
            BufferCount = bufferCount;
            Name = name;

            // 使用 Flip Discard 模式（DXGI Flip Model）
            var desc = new SwapChainDescription1
            {
                Width = width,
                Height = height,
                Format = D3D12FormatMapper.ToDxgiFormat(format),
                Stereo = false,
                SampleDescription = new SampleDescription(1, 0),
                BufferUsage = Usage.RenderTargetOutput,
                BufferCount = bufferCount,
                Scaling = Scaling.Stretch,
                SwapEffect = SwapEffect.FlipDiscard,
                AlphaMode = AlphaMode.Ignore,
                Flags = SwapChainFlags.None
            };

            // 交换链需要使用 Direct (Graphics) 命令队列
            var queue = (ID3D12CommandQueue)device.GetCommandQueue(RHIQueueType.Graphics)
                        .GetType().GetField("Queue",
                            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                        ?.GetValue(device.GetCommandQueue(RHIQueueType.Graphics));

            // 直接获取队列原生指针
            var d3dQueue = ((D3D12CommandQueue)device.GetCommandQueue(RHIQueueType.Graphics)).Queue;

            device.NativeFactory.CreateSwapChainForHwnd(
                d3dQueue, hwnd, desc, null, null,
                out IDXGISwapChain1? swapChain1).CheckError();

            NativeSwapChain = swapChain1!.QueryInterface<IDXGISwapChain4>();
            swapChain1.Dispose();

            // 获取后缓冲区并创建 RTV
            _backBuffers = new D3D12Texture[bufferCount];
            _rtvHandles = new RHIDescriptorHandle[bufferCount];
            var rtvHeap = device.GetRTVHeap();

            for (int i = 0; i < bufferCount; i++)
            {
                NativeSwapChain.GetBuffer<ID3D12Resource>((uint)i, out ID3D12Resource? backBuffer);
                _rtvHandles[i] = rtvHeap.Allocate();

                var texDesc = new RHITextureDesc
                {
                    Width = width,
                    Height = height,
                    DepthOrArraySize = 1,
                    MipLevels = 1,
                    Format = format,
                    Dimension = RHITextureDimension.Texture2D,
                    Flags = RHITextureFlags.AllowRenderTarget
                };

                _backBuffers[i] = new D3D12Texture(backBuffer!, texDesc, RHIResourceState.Common);
                _backBuffers[i].Name = $"{name}_BackBuffer_{i}";

                // 创建 RTV
                device.NativeDevice.CreateRenderTargetView(backBuffer!, null, rtvHeap.GetCpuHandle(_rtvHandles[i].Index));
            }

            if (!string.IsNullOrEmpty(name))
                NativeSwapChain.Name = name;
        }

        /// <summary>获取指定索引的后缓冲区纹理</summary>
        public override RHITexture GetBackBuffer(int index)
        {
            return _backBuffers[index];
        }

        /// <summary>获取指定索引后缓冲区的 RTV 描述符句柄</summary>
        public override RHIDescriptorHandle GetBackBufferRTV(int index)
        {
            return _rtvHandles[index];
        }

        /// <summary>获取当前后缓冲区纹理</summary>
        public override RHITexture GetCurrentBackBuffer()
        {
            return _backBuffers[CurrentBackBufferIndex];
        }

        /// <summary>获取当前后缓冲区的 RTV 描述符句柄</summary>
        public override RHIDescriptorHandle GetCurrentBackBufferRTV()
        {
            return _rtvHandles[CurrentBackBufferIndex];
        }

        /// <summary>呈现（翻转后缓冲区）</summary>
        /// <param name="vsync">是否启用垂直同步</param>
        public override void Present(bool vsync)
        {
            uint syncInterval = vsync ? 1u : 0u;
            NativeSwapChain.Present(syncInterval, PresentFlags.None);
        }

        /// <summary>调整交换链缓冲区大小</summary>
        public override void Resize(int newWidth, int newHeight)
        {
            if (newWidth == Width && newHeight == Height)
                return;

            // 释放旧的后缓冲区资源
            foreach (var buf in _backBuffers)
                buf?.Dispose();

            // 调整缓冲区
            NativeSwapChain.ResizeBuffers(BufferCount, newWidth, newHeight,
                D3D12FormatMapper.ToDxgiFormat(Format), 0).CheckError();

            Width = newWidth;
            Height = newHeight;

            // 重新获取后缓冲区
            var rtvHeap = _device.GetRTVHeap();
            for (int i = 0; i < BufferCount; i++)
            {
                NativeSwapChain.GetBuffer<ID3D12Resource>((uint)i, out ID3D12Resource? backBuffer);
                var texDesc = new RHITextureDesc
                {
                    Width = newWidth,
                    Height = newHeight,
                    DepthOrArraySize = 1,
                    MipLevels = 1,
                    Format = Format,
                    Dimension = RHITextureDimension.Texture2D,
                    Flags = RHITextureFlags.AllowRenderTarget
                };
                _backBuffers[i] = new D3D12Texture(backBuffer!, texDesc, RHIResourceState.Common);
                _backBuffers[i].Name = $"{Name}_BackBuffer_{i}";
                _device.NativeDevice.CreateRenderTargetView(backBuffer!, null, rtvHeap.GetCpuHandle(_rtvHandles[i].Index));
            }
        }

        public override void Dispose()
        {
            foreach (var buf in _backBuffers)
                buf?.Dispose();
            NativeSwapChain?.Dispose();
        }
    }

    // ==============================================================================================
    //  区域十一: D3D12ShaderCompiler — Shader 编译 (d3dcompiler.dll P/Invoke)
    // ==============================================================================================

    /// <summary>
    /// 通过 d3dcompiler.dll 的 D3DCompile 函数编译 HLSL Shader。
    /// 注意: D3DCompile (fxc) 最高支持 Shader Model 5.1。
    /// Shader Model 6.0+ 需要 DXC 编译器 (dxcompiler.dll)，此处仍尝试传递目标字符串，
    /// 如果 d3dcompiler.dll 不支持则返回失败信息。
    /// </summary>
    internal static unsafe class D3D12ShaderCompiler
    {
        // D3D 编译标志位
        private const uint D3DCOMPILE_DEBUG                = 0x00000001;
        private const uint D3DCOMPILE_SKIP_VALIDATION       = 0x00000004;
        private const uint D3DCOMPILE_SKIP_OPTIMIZATION     = 0x00000008;
        private const uint D3DCOMPILE_OPTIMIZATION_LEVEL0   = 0x00000000;
        private const uint D3DCOMPILE_OPTIMIZATION_LEVEL1   = 0x00001000;
        private const uint D3DCOMPILE_OPTIMIZATION_LEVEL2   = 0x00002000;
        private const uint D3DCOMPILE_OPTIMIZATION_LEVEL3   = 0x00003000;
        private const uint D3DCOMPILE_ENABLE_STRICTNESS     = 0x00000400;
        private const uint D3DCOMPILE_WARNINGS_ARE_ERRORS   = 0x00000800;
        private const uint D3DCOMPILE_ALL_RESOURCES_BOUND   = 0x00020000;
        private const uint D3DCOMPILE_RESOURCES_MAY_ALIAS   = 0x00000800;

        [DllImport("d3dcompiler.dll", CallingConvention = CallingConvention.Winapi)]
        private static extern int D3DCompile(
            void* pSrcData, IntPtr SrcDataSize,
            [MarshalAs(UnmanagedType.LPStr)] string? pSourceName,
            IntPtr pDefines,
            IntPtr pInclude,
            [MarshalAs(UnmanagedType.LPStr)] string pEntrypoint,
            [MarshalAs(UnmanagedType.LPStr)] string pTarget,
            uint Flags1, uint Flags2,
            out IntPtr ppCode,
            out IntPtr ppErrorMsgs);

        /// <summary>
        /// 编译 HLSL 源代码字符串。
        /// </summary>
        public static RHIShaderBytecode Compile(
            string hlslSource, string entryPoint, RHIShaderStage stage,
            RHIShaderModel shaderModel, string? sourceName = null, bool debug = false)
        {
            // 构建编译目标字符串，如 "vs_6_0", "ps_5_1", "ms_6_5"
            string target = $"{D3D12FormatMapper.ToShaderStagePrefix(stage)}_{D3D12FormatMapper.ToShaderModelString(shaderModel)}";

            // 编译标志
            uint flags = D3DCOMPILE_ENABLE_STRICTNESS;
            if (debug)
                flags |= D3DCOMPILE_DEBUG | D3DCOMPILE_SKIP_OPTIMIZATION;
            else
                flags |= D3DCOMPILE_OPTIMIZATION_LEVEL3;
            flags |= D3DCOMPILE_ALL_RESOURCES_BOUND;

            // 将源码编码为 UTF-8
            byte[] sourceBytes = Encoding.UTF8.GetBytes(hlslSource);
            // 添加 BOM 以帮助编译器识别编码
            byte[] bom = new byte[] { 0xEF, 0xBB, 0xBF };
            byte[] fullSource = new byte[bom.Length + sourceBytes.Length];
            Buffer.BlockCopy(bom, 0, fullSource, 0, bom.Length);
            Buffer.BlockCopy(sourceBytes, 0, fullSource, bom.Length, sourceBytes.Length);

            IntPtr blobPtr = IntPtr.Zero;
            IntPtr errorBlobPtr = IntPtr.Zero;

            try
            {
                fixed (byte* pSrc = fullSource)
                {
                    int hr = D3DCompile(
                        pSrc, (IntPtr)fullSource.Length,
                        sourceName,
                        IntPtr.Zero, IntPtr.Zero,
                        entryPoint, target,
                        flags, 0,
                        out blobPtr, out errorBlobPtr);
                }

                if (hr < 0)
                {
                    // 编译失败，提取错误信息
                    string errorMsg = "D3DCompile 失败 (HRESULT=0x" + hr.ToString("X8") + ")";
                    if (errorBlobPtr != IntPtr.Zero)
                    {
                        string? blobError = ReadBlobString(errorBlobPtr);
                        if (!string.IsNullOrEmpty(blobError))
                            errorMsg += "\n" + blobError;
                    }
                    throw new Exception(errorMsg);
                }

                // 提取编译后的字节码
                byte[] bytecode = ReadBlobData(blobPtr);
                return new RHIShaderBytecode(bytecode, entryPoint, stage, sourceName ?? "Shader");
            }
            finally
            {
                // 释放 COM 对象
                if (blobPtr != IntPtr.Zero) ReleaseComObject(blobPtr);
                if (errorBlobPtr != IntPtr.Zero) ReleaseComObject(errorBlobPtr);
            }
        }

        /// <summary>从文件编译 HLSL Shader</summary>
        public static RHIShaderBytecode CompileFromFile(
            string filePath, string entryPoint, RHIShaderStage stage,
            RHIShaderModel shaderModel, bool debug = false)
        {
            string source = System.IO.File.ReadAllText(filePath, Encoding.UTF8);
            return Compile(source, entryPoint, stage, shaderModel, filePath, debug);
        }

        // ── ID3DBlob 辅助方法 (通过 vtable 直接调用) ──

        /// <summary>通过 ID3DBlob vtable 读取缓冲区数据</summary>
        private static byte[] ReadBlobData(IntPtr blobPtr)
        {
            // ID3DBlob vtable: [0]QueryInterface [1]AddRef [2]Release [3]GetBufferPointer [4]GetBufferSize
            void** vtable = *(void***)blobPtr;
            var getBufferPointer = (delegate*<IntPtr, IntPtr>)vtable[3];
            var getBufferSize = (delegate*<IntPtr, IntPtr>)vtable[4];

            IntPtr dataPtr = getBufferPointer(blobPtr);
            IntPtr size = getBufferSize(blobPtr);

            byte[] result = new byte[(int)size];
            Marshal.Copy(dataPtr, result, 0, (int)size);
            return result;
        }

        /// <summary>通过 ID3DBlob vtable 读取错误信息字符串</summary>
        private static string? ReadBlobString(IntPtr blobPtr)
        {
            void** vtable = *(void***)blobPtr;
            var getBufferPointer = (delegate*<IntPtr, IntPtr>)vtable[3];
            var getBufferSize = (delegate*<IntPtr, IntPtr>)vtable[4];

            IntPtr dataPtr = getBufferPointer(blobPtr);
            int size = (int)getBufferSize(blobPtr);

            if (size == 0) return null;

            byte[] bytes = new byte[size];
            Marshal.Copy(dataPtr, bytes, 0, size);
            return Encoding.UTF8.GetString(bytes, 0, size).TrimEnd('\0', '\r', '\n');
        }

        /// <summary>调用 COM 对象的 Release 方法</summary>
        private static uint ReleaseComObject(IntPtr comPtr)
        {
            void** vtable = *(void***)comPtr;
            var release = (delegate*<IntPtr, uint>)vtable[2];
            return release(comPtr);
        }
    }

    // ==============================================================================================
    //  区域十二: PipelineStateStreamBuilder — Mesh Shader PSO 流构建器
    // ==============================================================================================

    /// <summary>
    /// 用于构建 D3D12 管线状态流 (Pipeline State Stream) 的辅助类。
    /// Mesh Shader PSO 无法使用标准的 GraphicsPipelineStateDescription 创建，
    /// 必须通过 ID3D12Device2::CreatePipelineState + PipelineStateStreamDescription 创建。
    /// 此类负责将各子对象按 D3D12 规定的内存布局写入连续内存。
    /// </summary>
    internal sealed unsafe class PipelineStateStreamBuilder : IDisposable
    {
        private byte* _buffer;
        private int _capacity;
        private int _offset;
        private const int Alignment = 8; // x64 指针对齐

        // 子对象类型枚举值 (D3D12_PIPELINE_STATE_SUBOBJECT_TYPE)
        public const int Type_RootSignature        = 0;
        public const int Type_VS                    = 1;
        public const int Type_PS                    = 2;
        public const int Type_DS                    = 3;
        public const int Type_HS                    = 4;
        public const int Type_GS                    = 5;
        public const int Type_CS                    = 6;
        public const int Type_Blend                 = 7;
        public const int Type_SampleMask            = 8;
        public const int Type_Rasterizer            = 9;
        public const int Type_DepthStencil          = 10;
        public const int Type_PrimitiveTopology     = 13;
        public const int Type_RTFormats             = 14;
        public const int Type_DSVFormat             = 15;
        public const int Type_SampleDesc            = 16;
        public const int Type_AS                    = 22; // Amplification Shader
        public const int Type_MS                    = 23; // Mesh Shader

        public PipelineStateStreamBuilder(int initialCapacity = 512)
        {
            _capacity = initialCapacity;
            _buffer = (byte*)NativeMemory.AlignedAlloc((nuint)_capacity, (nuint)Alignment);
            _offset = 0;
        }

        public IntPtr Data => (IntPtr)_buffer;
        public int Size => _offset;

        private void EnsureCapacity(int needed)
        {
            if (_offset + needed <= _capacity) return;
            while (_capacity < _offset + needed) _capacity *= 2;
            var newBuf = (byte*)NativeMemory.AlignedAlloc((nuint)_capacity, (nuint)Alignment);
            Buffer.MemoryCopy(_buffer, newBuf, _capacity, _offset);
            NativeMemory.AlignedFree(_buffer);
            _buffer = newBuf;
        }

        private void Align()
        {
            int rem = _offset % Alignment;
            if (rem == 0) return;
            int pad = Alignment - rem;
            EnsureCapacity(pad);
            for (int i = 0; i < pad; i++) _buffer[_offset++] = 0;
        }

        /// <summary>写入根签例子对象</summary>
        public void WriteRootSignature(IntPtr rootSigPtr)
        {
            Align();
            EnsureCapacity(sizeof(int) + sizeof(int) + sizeof(IntPtr));
            *(int*)(_buffer + _offset) = Type_RootSignature;
            _offset += 8;
            *(IntPtr*)(_buffer + _offset) = rootSigPtr;
            _offset += sizeof(IntPtr);
        }

        /// <summary>写入 Shader 字节码子对象 (VS/PS/CS/MS/AS 等)</summary>
        public void WriteShaderBytecode(int type, IntPtr shaderPtr, int shaderSize)
        {
            Align();
            // 布局: type(4) + pad(4) + pShaderBytecode(8) + BytecodeLength(8) = 24
            EnsureCapacity(24);
            *(int*)(_buffer + _offset) = type;
            _offset += 8;
            *(IntPtr*)(_buffer + _offset) = shaderPtr;
            _offset += 8;
            *(IntPtr*)(_buffer + _offset) = (IntPtr)shaderSize;
            _offset += 8;
        }

        /// <summary>写入渲染目标格式数组子对象</summary>
        public void WriteRTFormats(Format[] formats, int count)
        {
            Align();
            // D3D12_RT_FORMAT_ARRAY: DXGI_FORMAT[8](32) + NumRenderTargets(4) + pad(4) = 40
            EnsureCapacity(40);
            *(int*)(_buffer + _offset) = Type_RTFormats;
            _offset += 8;
            // 写入格式数组 (8 个 DXGI_FORMAT = 32 字节)
            for (int i = 0; i < 8; i++)
                *(int*)(_buffer + _offset + i * 4) = i < count ? (int)formats[i] : (int)Format.Unknown;
            _offset += 32;
            *(uint*)(_buffer + _offset) = (uint)count;
            _offset += 4;
            _offset += 4; // padding
        }

        /// <summary>写入深度模板格式子对象</summary>
        public void WriteDSVFormat(Format format)
        {
            Align();
            EnsureCapacity(8 + 4 + 4);
            *(int*)(_buffer + _offset) = Type_DSVFormat;
            _offset += 8;
            *(int*)(_buffer + _offset) = (int)format;
            _offset += 8;
        }

        /// <summary>写入采样描述子对象</summary>
        public void WriteSampleDesc(uint sampleCount, uint sampleQuality)
        {
            Align();
            EnsureCapacity(8 + 8);
            *(int*)(_buffer + _offset) = Type_SampleDesc;
            _offset += 8;
            *(uint*)(_buffer + _offset) = sampleCount;
            *(uint*)(_buffer + _offset + 4) = sampleQuality;
            _offset += 8;
        }

        /// <summary>写入图元拓扑类型子对象</summary>
        public void WritePrimitiveTopology(PrimitiveTopologyType type)
        {
            Align();
            EnsureCapacity(8 + 4 + 4);
            *(int*)(_buffer + _offset) = Type_PrimitiveTopology;
            _offset += 8;
            *(int*)(_buffer + _offset) = (int)type;
            _offset += 8;
        }

        public void Dispose()
        {
            if (_buffer != null)
            {
                NativeMemory.AlignedFree(_buffer);
                _buffer = null;
            }
        }
    }

    // ==============================================================================================
    //  区域十三: D3D12Device — D3D12 设备主类 (继承 RHIDevice)
    // ==============================================================================================

    /// <summary>
    /// yama引擎 Direct3D 12 渲染后端设备实现。
    /// 
    /// 负责管理:
    ///   - DXGI 工厂与适配器枚举
    ///   - ID3D12Device 设备创建与调试层
    ///   - 功能级别与特性检测 (Mesh Shader / Ray Tracing / VRS)
    ///   - 命令队列缓存 (Graphics / Compute / Copy)
    ///   - 描述符堆池 (CBV_SRV_UAV / Sampler / RTV / DSV)
    ///   - 资源创建 (Buffer / Texture / Upload Buffer)
    ///   - 描述符创建 (CBV / SRV / UAV / RTV / DSV / Sampler)
    ///   - 根签名序列化与创建
    ///   - PSO 创建 (Graphics / Compute / Mesh Shader)
    ///   - 命令列表与围栏创建
    ///   - 交换链创建 (Flip Model)
    ///   - Shader 编译 (d3dcompiler.dll)
    ///   - 资源上传 (Upload Buffer + Copy)
    /// </summary>
    /// <summary>Mesh Shader 支持层级 (Vortice 3.2.0 本地定义)</summary>
    public enum MeshShaderSupportTier { NotSupported, Tier1, Tier2 }

    public sealed class D3D12Device : RHIDevice
    {
        // ── 原生 D3D12 对象 ──
        internal ID3D12Device NativeDevice;
        internal IDXGIFactory6 NativeFactory;
        internal IDXGIAdapter1 NativeAdapter;

        // ── 调试对象 (ID3D12Debug/ID3D12InfoQueue 在 Vortice 3.2.0 中不可访问) ──
        // private ID3D12Debug? _debug;
        // private ID3D12InfoQueue? _infoQueue;

        // ── 命令队列缓存 ──
        private readonly Dictionary<RHIQueueType, D3D12CommandQueue> _commandQueues = new();

        // ── 描述符堆池 ──
        private D3D12DescriptorHeap? _cbvSrvUavHeap;
        private D3D12DescriptorHeap? _samplerHeap;
        private D3D12DescriptorHeap? _rtvHeap;
        private D3D12DescriptorHeap? _dsvHeap;
        private readonly object _heapLock = new();

        // ── 采样器缓存 ──
        private readonly Dictionary<int, RHIDescriptorHandle> _samplerCache = new();

        // ── 间接命令签名缓存 ──
        private ID3D12CommandSignature? _drawIndexedSig;
        private ID3D12CommandSignature? _dispatchSig;

        // ── 上传用同步资源 ──
        private D3D12CommandQueue? _uploadQueue;

        // ── 设备信息属性 ──
        public override string AdapterName { get; }
        public override ulong DedicatedVideoMemory { get; }
        public override ulong DedicatedSystemMemory { get; }
        public override ulong SharedSystemMemory { get; }
        public override RHIShaderModel MaxShaderModel { get; }
        public override bool SupportsMeshShaders { get; }
        public override bool SupportsRayTracing { get; }
        public override bool SupportsVariableRateShading { get; }
        public override bool SupportsSamplerFeedback { get; }
        public override string MaxFeatureLevel { get; }
        public override string Name { get; set; } = "D3D12Device";

        // ==========================================================================================
        //  构造函数: 创建 DXGI 工厂 → 枚举适配器 → 创建设备 → 特性检测
        // ==========================================================================================

        /// <summary>
        /// 创建 D3D12 设备。
        /// </summary>
        /// <param name="enableDebugLayer">是否启用 D3D12 调试层</param>
        /// <param name="enableGpuBasedValidation">是否启用 GPU 校验（调试子层，性能开销大）</param>
        /// <param name="adapterIndex">指定适配器索引（-1 表示自动选择最佳）</param>
        public D3D12Device(bool enableDebugLayer = false, bool enableGpuBasedValidation = false, int adapterIndex = -1)
        {
            // ── 1. 初始化调试层 (ID3D12Debug 在 Vortice 3.2.0 中不可访问, 已禁用) ──
            // if (enableDebugLayer) { D3D12.D3D12GetDebugInterface(...); ... }

            // ── 2. 创建 DXGI 工厂 ──
            var factoryFlags = false;
            DXGI.CreateDXGIFactory2<IDXGIFactory6>(factoryFlags, out IDXGIFactory6? factory).CheckError();
            NativeFactory = factory!;

            // ── 3. 枚举适配器并选择最佳 ──
            NativeAdapter = EnumerateAdapters(adapterIndex);

            // 获取适配器描述
            var adapterDesc = NativeAdapter.Description1;
            AdapterName = adapterDesc.Description;
            DedicatedVideoMemory = adapterDesc.DedicatedVideoMemory;
            DedicatedSystemMemory = adapterDesc.DedicatedSystemMemory;
            SharedSystemMemory = adapterDesc.SharedSystemMemory;

            // ── 4. 创建 D3D12 设备（尝试不同功能级别） ──
            FeatureLevel[] tryLevels = {
                FeatureLevel.Level_12_1, FeatureLevel.Level_12_0,
                FeatureLevel.Level_11_1, FeatureLevel.Level_11_0
            };

            FeatureLevel achievedLevel = FeatureLevel.Level_11_0;
            ID3D12Device? device = null;

            foreach (var fl in tryLevels)
            {
                if (global::Vortice.Direct3D12.D3D12.D3D12CreateDevice(NativeAdapter, fl, out ID3D12Device? testDevice).Success)
                {
                    device = testDevice;
                    achievedLevel = fl;
                    break;
                }
            }

            if (device == null)
                throw new InvalidOperationException("无法创建 D3D12 设备：未找到支持 D3D12 的 GPU 适配器");

            NativeDevice = device;
            MaxFeatureLevel = achievedLevel switch
            {
                FeatureLevel.Level_12_1 => "12.1",
                FeatureLevel.Level_12_0 => "12.0",
                FeatureLevel.Level_11_1 => "11.1",
                FeatureLevel.Level_11_0 => "11.0",
                _ => "11.0"
            };

            // ── 5. 信息队列 (ID3D12InfoQueue 在 Vortice 3.2.0 中不可访问, 已禁用) ──
            // if (enableDebugLayer) { _infoQueue = NativeDevice.QueryInterface<ID3D12InfoQueue>(); ... }

            // ── 6. 特性检测: Mesh Shader / Ray Tracing / VRS / Sampler Feedback ──
            DetectFeatures(out var meshTier, out var rtTier, out var vrsTier, out var feedbackTier);

            SupportsMeshShaders = meshTier != MeshShaderSupportTier.NotSupported;
            SupportsRayTracing = rtTier != RaytracingTier.NotSupported;
            SupportsVariableRateShading = vrsTier != VariableShadingRateTier.NotSupported;
            SupportsSamplerFeedback = feedbackTier != SamplerFeedbackTier.NotSupported;

            // Shader Model 支持检测
            MaxShaderModel = DetectMaxShaderModel();

            // 根据特性设置设备名称
            NativeDevice.Name = $"YamaEngine_D3D12Device_{AdapterName}";
        }

        // ==========================================================================================
        //  DXGI 适配器枚举
        // ==========================================================================================

        /// <summary>
        /// 枚举 DXGI 适配器并打印信息，返回选定的适配器。
        /// 优先选择独显（非软件适配器），按专用显存排序。
        /// </summary>
        private IDXGIAdapter1 EnumerateAdapters(int preferredIndex)
        {
            IDXGIAdapter1? bestAdapter = null;
            ulong bestVram = 0;
            int index = 0;

            while (NativeFactory.EnumAdapters1((uint)index, out IDXGIAdapter1? adapter).Success)
            {
                var desc = adapter.Description1;

                // 跳过软件适配器 (Microsoft Basic Render Driver)
                bool isSoftware = (desc.Flags & AdapterFlags.Software) != 0;
                string name = desc.Description;

                Console.WriteLine($"[D3D12] 适配器 #{index}: {name} | " +
                                  $"显存={desc.DedicatedVideoMemory / 1024 / 1024}MB | " +
                                  $"系统显存={desc.DedicatedSystemMemory / 1024 / 1024}MB | " +
                                  $"共享内存={desc.SharedSystemMemory / 1024 / 1024}MB | " +
                                  (isSoftware ? "软件" : "硬件"));

                if (preferredIndex >= 0)
                {
                    // 指定索引直接返回
                    if (index == preferredIndex)
                    {
                        bestAdapter = adapter;
                        break;
                    }
                }
                else
                {
                    // 自动选择：跳过软件适配器，选择专用显存最大的
                    if (!isSoftware && desc.DedicatedVideoMemory > bestVram)
                    {
                        bestAdapter?.Dispose();
                        bestAdapter = adapter;
                        bestVram = desc.DedicatedVideoMemory;
                        continue;
                    }
                }

                adapter.Dispose();
                index++;
            }

            if (bestAdapter == null)
            {
                // 回退到默认适配器
                NativeFactory.EnumAdapters1(0, out IDXGIAdapter1? fallback).CheckError();
                bestAdapter = fallback!;
            }

            return bestAdapter!;
        }

        // ==========================================================================================
        //  特性检测
        // ==========================================================================================

        /// <summary>检测 Mesh Shader / Ray Tracing / VRS / Sampler Feedback 特性</summary>
        private void DetectFeatures(
            out MeshShaderSupportTier meshTier,
            out RaytracingTier rtTier,
            out VariableShadingRateTier vrsTier,
            out SamplerFeedbackTier feedbackTier)
        {
            meshTier = MeshShaderSupportTier.NotSupported;
            rtTier = RaytracingTier.NotSupported;
            vrsTier = VariableShadingRateTier.NotSupported;
            feedbackTier = SamplerFeedbackTier.NotSupported;

            try
            {
                // Mesh Shader (D3D12_OPTIONS5) - MeshShaderSupportTier 属性在 Vortice 3.2.0 中不可用
                // var opt5 = new FeatureDataD3D12Options5();
                // NativeDevice.CheckFeatureSupport(Feature.D3D12Options5, ref opt5);
                // meshTier = opt5.MeshShaderSupport;

                // VRS (D3D12 OPTIONS6)
                var opt6 = new FeatureDataD3D12Options6();
                NativeDevice.CheckFeatureSupport(Feature.Options6, ref opt6);
                vrsTier = opt6.VariableShadingRateTier;

                // Ray Tracing (D3D12 OPTIONS7)
                var opt7 = new FeatureDataD3D12Options7();
                NativeDevice.CheckFeatureSupport(Feature.Options7, ref opt7);
                rtTier = opt7.RaytracingTier;

                // Sampler Feedback (D3D12 OPTIONS7)
                feedbackTier = opt7.SamplerFeedbackTier;
            }
            catch
            {
                // 某些特性可能在不支持的硬件上抛异常，忽略即可
            }

            Console.WriteLine($"[D3D12] 特性检测: MeshShader={meshTier}, RayTracing={rtTier}, " +
                              $"VRS={vrsTier}, SamplerFeedback={feedbackTier}");
        }

        /// <summary>检测支持的最高 Shader Model</summary>
        private RHIShaderModel DetectMaxShaderModel()
        {
            // 依次尝试检测 SM 6.8 到 6.0
            RHIShaderModel[] models = {
                RHIShaderModel.SM_6_8, RHIShaderModel.SM_6_7, RHIShaderModel.SM_6_6,
                RHIShaderModel.SM_6_5, RHIShaderModel.SM_6_4, RHIShaderModel.SM_6_3,
                RHIShaderModel.SM_6_2, RHIShaderModel.SM_6_1, RHIShaderModel.SM_6_0
            };

            foreach (var sm in models)
            {
                try
                {
                    var data = new FeatureDataShaderModel { HighestShaderModel = (ShaderModel)(int)sm };
                    if (NativeDevice.CheckFeatureSupport(Feature.ShaderModel, ref data).Success)
                    {
                        Console.WriteLine($"[D3D12] 最高 Shader Model: {sm}");
                        return sm;
                    }
                }
                catch { /* continue trying */ }
            }

            return RHIShaderModel.SM_6_0;
        }

        // ==========================================================================================
        //  命令队列管理
        // ==========================================================================================

        public override RHICommandQueue GetCommandQueue(RHIQueueType type)
        {
            if (!_commandQueues.TryGetValue(type, out var queue))
            {
                queue = new D3D12CommandQueue(this, type, $"Queue_{type}");
                _commandQueues[type] = queue;
            }
            return queue;
        }

        // ==========================================================================================
        //  描述符堆管理
        // ==========================================================================================

        /// <summary>获取或创建 CBV/SRV/UAV 描述符堆</summary>
        internal D3D12DescriptorHeap GetCbvSrvUavHeap()
        {
            if (_cbvSrvUavHeap == null)
            {
                lock (_heapLock)
                {
                    _cbvSrvUavHeap ??= new D3D12DescriptorHeap(this,
                        RHIDescriptorHeapType.CBV_SRV_UAV, 16384, true, "CBV_SRV_UAV_Heap");
                }
            }
            return _cbvSrvUavHeap;
        }

        /// <summary>获取或创建 Sampler 描述符堆</summary>
        internal D3D12DescriptorHeap GetSamplerHeap()
        {
            if (_samplerHeap == null)
            {
                lock (_heapLock)
                {
                    _samplerHeap ??= new D3D12DescriptorHeap(this,
                        RHIDescriptorHeapType.Sampler, 2048, true, "Sampler_Heap");
                }
            }
            return _samplerHeap;
        }

        /// <summary>获取或创建 RTV 描述符堆</summary>
        internal D3D12DescriptorHeap GetRTVHeap()
        {
            if (_rtvHeap == null)
            {
                lock (_heapLock)
                {
                    _rtvHeap ??= new D3D12DescriptorHeap(this,
                        RHIDescriptorHeapType.RTV, 256, false, "RTV_Heap");
                }
            }
            return _rtvHeap;
        }

        /// <summary>获取或创建 DSV 描述符堆</summary>
        internal D3D12DescriptorHeap GetDSVHeap()
        {
            if (_dsvHeap == null)
            {
                lock (_heapLock)
                {
                    _dsvHeap ??= new D3D12DescriptorHeap(this,
                        RHIDescriptorHeapType.DSV, 128, false, "DSV_Heap");
                }
            }
            return _dsvHeap;
        }

        // ==========================================================================================
        //  描述符创建 (CBV / SRV / UAV / RTV / DSV / Sampler)
        // ==========================================================================================

        /// <summary>创建描述符堆</summary>
        public override RHIDescriptorHeap CreateDescriptorHeap(RHIDescriptorHeapType type, int capacity, bool shaderVisible = true)
        {
            return new D3D12DescriptorHeap(this, type, capacity, shaderVisible, "");
        }

        /// <summary>创建常量缓冲区视图 (CBV)</summary>
        public override RHIDescriptorHandle CreateCBV(RHIBuffer buffer, ulong offset, uint sizeInBytes)
        {
            var heap = GetCbvSrvUavHeap();
            var handle = heap.Allocate();
            // CBV 大小必须为 256 的倍数
            uint cbvSize = (uint)((sizeInBytes + 255) & ~255u);
            var desc = new ConstantBufferViewDescription(
                ((D3D12Buffer)buffer).GpuVirtualAddress + offset, cbvSize);
            NativeDevice.CreateConstantBufferView(desc, heap.GetCpuHandle(handle.Index));
            return handle;
        }

        /// <summary>创建 Shader 资源视图 (SRV) — 缓冲区</summary>
        public override RHIDescriptorHandle CreateSRV(RHIBuffer buffer, RHIFormat format, bool isStructured, uint firstElement, uint numElements, uint structureStride)
        {
            var heap = GetCbvSrvUavHeap();
            var handle = heap.Allocate();
            var desc = new ShaderResourceViewDescription
            {
                Format = isStructured ? Format.Unknown : D3D12FormatMapper.ToDxgiFormat(format),
                ViewDimension = ShaderResourceViewDimension.Buffer,
                Shader4ComponentMapping = 0x1688,
            };
            desc.Buffer.FirstElement = firstElement;
            desc.Buffer.NumElements = numElements;
            desc.Buffer.StructureByteStride = isStructured ? structureStride : 0;
            desc.Buffer.Flags = BufferShaderResourceViewFlags.None;

            NativeDevice.CreateShaderResourceView(((D3D12Buffer)buffer).Resource, desc,
                heap.GetCpuHandle(handle.Index));
            return handle;
        }

        /// <summary>创建 Shader 资源视图 (SRV) — 纹理</summary>
        public RHIDescriptorHandle CreateSRV(D3D12Texture texture, RHIFormat? overrideFormat = null,
                                              uint mostDetailedMip = 0, uint mipLevels = 0,
                                              uint firstArraySlice = 0, uint arraySize = 1)
        {
            var heap = GetCbvSrvUavHeap();
            var handle = heap.Allocate();
            var desc = new ShaderResourceViewDescription
            {
                Format = overrideFormat.HasValue
                    ? D3D12FormatMapper.ToDxgiFormat(overrideFormat.Value)
                    : texture.DxgiFormat,
                ViewDimension = ShaderResourceViewDimension.Texture2D,
                Shader4ComponentMapping = 0x1688,
            };
            desc.Texture2D.MostDetailedMip = mostDetailedMip;
            desc.Texture2D.MipLevels = mipLevels;
            desc.Texture2D.PlaneSlice = 0;
            desc.Texture2D.ResourceMinLODClamp = 0.0f;

            NativeDevice.CreateShaderResourceView(texture.Resource, desc,
                heap.GetCpuHandle(handle.Index));
            return handle;
        }

        /// <summary>创建无序访问视图 (UAV) — 缓冲区</summary>
        public override RHIDescriptorHandle CreateUAV(RHIBuffer buffer, RHIFormat format, bool isStructured,
                                                       uint firstElement, uint numElements, uint structureStride,
                                                       RHIBuffer? counterBuffer = null, ulong counterOffset = 0)
        {
            var heap = GetCbvSrvUavHeap();
            var handle = heap.Allocate();
            var desc = new UnorderedAccessViewDescription
            {
                Format = isStructured ? Format.Unknown : D3D12FormatMapper.ToDxgiFormat(format),
                ViewDimension = UnorderedAccessViewDimension.Buffer,
            };
            desc.Buffer.FirstElement = firstElement;
            desc.Buffer.NumElements = numElements;
            desc.Buffer.StructureByteStride = isStructured ? structureStride : 0;
            desc.Buffer.CounterOffsetInBytes = counterOffset;
            desc.Buffer.Flags = BufferUnorderedAccessViewFlags.None;

            ID3D12Resource? counter = counterBuffer != null
                ? ((D3D12Buffer)counterBuffer).Resource
                : null;

            NativeDevice.CreateUnorderedAccessView(
                ((D3D12Buffer)buffer).Resource, counter, desc,
                heap.GetCpuHandle(handle.Index));
            return handle;
        }

        // ── 纹理 SRV / UAV 重载（覆盖 RHIDevice 中保留默认实现的虚方法）──

        /// <summary>创建 SRV — 纹理（便捷重载：指定 mip 范围）</summary>
        public override RHIDescriptorHandle CreateSRV(RHITexture texture, uint mostDetailedMip = 0, uint mipLevels = 0)
        {
            // 复用已有的 D3D12Texture SRV 辅助方法
            return CreateSRV((D3D12Texture)texture, null, mostDetailedMip, mipLevels);
        }

        /// <summary>创建 UAV — 纹理（便捷重载：指定 mip 切片）</summary>
        public override RHIDescriptorHandle CreateUAV(RHITexture texture, uint mipSlice = 0)
        {
            var heap = GetCbvSrvUavHeap();
            var handle = heap.Allocate();
            var tex = (D3D12Texture)texture;
            var desc = new UnorderedAccessViewDescription
            {
                Format = tex.DxgiFormat,
                ViewDimension = UnorderedAccessViewDimension.Texture2D
            };
            desc.Texture2D.MipSlice = mipSlice;
            desc.Texture2D.PlaneSlice = 0;

            NativeDevice.CreateUnorderedAccessView(tex.Resource, null, desc,
                heap.GetCpuHandle(handle.Index));
            return handle;
        }

        /// <summary>创建 SRV — 纹理（描述符结构重载，支持完整维度/mip/数组配置）</summary>
        public override RHIDescriptorHandle CreateSRV(RHITexture texture, RHISRVDesc desc)
        {
            var heap = GetCbvSrvUavHeap();
            var handle = heap.Allocate();
            var tex = (D3D12Texture)texture;
            var viewDesc = new ShaderResourceViewDescription
            {
                Format = desc.Format == RHIFormat.Unknown
                    ? tex.DxgiFormat
                    : D3D12FormatMapper.ToDxgiFormat(desc.Format),
                Shader4ComponentMapping = 0x1688
            };

            switch (desc.Dimension)
            {
                case RHIResourceDimension.Buffer:
                    viewDesc.ViewDimension = ShaderResourceViewDimension.Buffer;
                    viewDesc.Buffer.FirstElement = desc.FirstElement;
                    viewDesc.Buffer.NumElements = desc.NumElements;
                    viewDesc.Buffer.Flags = BufferShaderResourceViewFlags.None;
                    break;
                case RHIResourceDimension.Texture1D:
                    viewDesc.ViewDimension = ShaderResourceViewDimension.Texture1D;
                    viewDesc.Texture1D.MostDetailedMip = desc.MostDetailedMip;
                    viewDesc.Texture1D.MipLevels = desc.MipLevels;
                    viewDesc.Texture1D.ResourceMinLODClamp = desc.ResourceMinLODClamp;
                    break;
                case RHIResourceDimension.Texture3D:
                    viewDesc.ViewDimension = ShaderResourceViewDimension.Texture3D;
                    viewDesc.Texture3D.MostDetailedMip = desc.MostDetailedMip;
                    viewDesc.Texture3D.MipLevels = desc.MipLevels;
                    viewDesc.Texture3D.ResourceMinLODClamp = desc.ResourceMinLODClamp;
                    break;
                case RHIResourceDimension.Texture2D:
                default:
                    // 当指定了数组大小时使用 Texture2DArray 视图
                    if (desc.ArraySize > 0)
                    {
                        viewDesc.ViewDimension = ShaderResourceViewDimension.Texture2DArray;
                        viewDesc.Texture2DArray.MostDetailedMip = desc.MostDetailedMip;
                        viewDesc.Texture2DArray.MipLevels = desc.MipLevels;
                        viewDesc.Texture2DArray.FirstArraySlice = desc.First2DArraySlice;
                        viewDesc.Texture2DArray.ArraySize = desc.ArraySize;
                        viewDesc.Texture2DArray.PlaneSlice = desc.PlaneSlice;
                        viewDesc.Texture2DArray.ResourceMinLODClamp = desc.ResourceMinLODClamp;
                    }
                    else
                    {
                        viewDesc.ViewDimension = ShaderResourceViewDimension.Texture2D;
                        viewDesc.Texture2D.MostDetailedMip = desc.MostDetailedMip;
                        viewDesc.Texture2D.MipLevels = desc.MipLevels;
                        viewDesc.Texture2D.PlaneSlice = desc.PlaneSlice;
                        viewDesc.Texture2D.ResourceMinLODClamp = desc.ResourceMinLODClamp;
                    }
                    break;
            }

            NativeDevice.CreateShaderResourceView(tex.Resource, viewDesc,
                heap.GetCpuHandle(handle.Index));
            return handle;
        }

        /// <summary>创建 UAV — 纹理（描述符结构重载，支持完整维度/mip/数组配置）</summary>
        public override RHIDescriptorHandle CreateUAV(RHITexture texture, RHIUAVDesc desc)
        {
            var heap = GetCbvSrvUavHeap();
            var handle = heap.Allocate();
            var tex = (D3D12Texture)texture;
            var viewDesc = new UnorderedAccessViewDescription
            {
                Format = desc.Format == RHIFormat.Unknown
                    ? tex.DxgiFormat
                    : D3D12FormatMapper.ToDxgiFormat(desc.Format)
            };

            switch (desc.Dimension)
            {
                case RHIResourceDimension.Texture3D:
                    viewDesc.ViewDimension = UnorderedAccessViewDimension.Texture3D;
                    viewDesc.Texture3D.MipSlice = desc.MipSlice;
                    viewDesc.Texture3D.FirstWSlice = desc.FirstArraySlice;
                    viewDesc.Texture3D.WSize = desc.ArraySize;
                    break;
                case RHIResourceDimension.Texture2D:
                default:
                    if (desc.ArraySize > 0)
                    {
                        viewDesc.ViewDimension = UnorderedAccessViewDimension.Texture2DArray;
                        viewDesc.Texture2DArray.MipSlice = desc.MipSlice;
                        viewDesc.Texture2DArray.FirstArraySlice = desc.FirstArraySlice;
                        viewDesc.Texture2DArray.ArraySize = desc.ArraySize;
                        viewDesc.Texture2DArray.PlaneSlice = desc.PlaneSlice;
                    }
                    else
                    {
                        viewDesc.ViewDimension = UnorderedAccessViewDimension.Texture2D;
                        viewDesc.Texture2D.MipSlice = desc.MipSlice;
                        viewDesc.Texture2D.PlaneSlice = desc.PlaneSlice;
                    }
                    break;
            }

            NativeDevice.CreateUnorderedAccessView(tex.Resource, null, viewDesc,
                heap.GetCpuHandle(handle.Index));
            return handle;
        }

        /// <summary>创建渲染目标视图 (RTV)</summary>
        public override RHIDescriptorHandle CreateRTV(RHITexture texture, uint mipSlice = 0, uint arraySlice = 0)
        {
            var heap = GetRTVHeap();
            var handle = heap.Allocate();
            var tex = (D3D12Texture)texture;
            var desc = new RenderTargetViewDescription
            {
                Format = tex.DxgiFormat,
                ViewDimension = RenderTargetViewDimension.Texture2D
            };
            desc.Texture2D.MipSlice = mipSlice;
            desc.Texture2D.PlaneSlice = 0;

            NativeDevice.CreateRenderTargetView(tex.Resource, desc,
                heap.GetCpuHandle(handle.Index));
            return handle;
        }

        /// <summary>创建深度模板视图 (DSV)</summary>
        public override RHIDescriptorHandle CreateDSV(RHITexture texture, uint mipSlice = 0, uint arraySlice = 0)
        {
            var heap = GetDSVHeap();
            var handle = heap.Allocate();
            var tex = (D3D12Texture)texture;
            var desc = new DepthStencilViewDescription
            {
                Format = tex.DxgiFormat,
                ViewDimension = DepthStencilViewDimension.Texture2D
            };
            desc.Texture2D.MipSlice = mipSlice;

            NativeDevice.CreateDepthStencilView(tex.Resource, desc,
                heap.GetCpuHandle(handle.Index));
            return handle;
        }

        /// <summary>创建采样器</summary>
        public override RHIDescriptorHandle CreateSampler(RHISamplerDesc desc)
        {
            var heap = GetSamplerHeap();
            var handle = heap.Allocate();

            var samplerDesc = new SamplerDescription
            {
                Filter = D3D12FormatMapper.ToFilter(desc.Filter),
                AddressU = D3D12FormatMapper.ToAddressMode(desc.AddressU),
                AddressV = D3D12FormatMapper.ToAddressMode(desc.AddressV),
                AddressW = D3D12FormatMapper.ToAddressMode(desc.AddressW),
                MipLODBias = desc.MipLODBias,
                MaxAnisotropy = desc.MaxAnisotropy,
                ComparisonFunction = desc.Filter >= RHIFilter.ComparisonMinMagMipPoint
                    ? D3D12FormatMapper.ToComparisonFunc(desc.ComparisonFunc)
                    : ComparisonFunction.Never,
                BorderColor = D3D12FormatMapper.ToStaticBorderColor(desc.BorderColor),
                MinLOD = desc.MinLOD,
                MaxLOD = desc.MaxLOD
            };

            NativeDevice.CreateSampler(samplerDesc, heap.GetCpuHandle(handle.Index));
            return handle;
        }

        /// <summary>获取或创建采样器（带缓存，相同描述只创建一次）</summary>
        public override RHIDescriptorHandle GetOrCreateSampler(RHISamplerDesc desc)
        {
            int hash = HashSamplerDesc(desc);
            lock (_samplerCache)
            {
                if (_samplerCache.TryGetValue(hash, out var cached))
                    return cached;
                var handle = CreateSampler(desc);
                _samplerCache[hash] = handle;
                return handle;
            }
        }

        private static int HashSamplerDesc(RHISamplerDesc desc)
        {
            unchecked
            {
                int h = 17;
                h = h * 31 + (int)desc.Filter;
                h = h * 31 + (int)desc.AddressU;
                h = h * 31 + (int)desc.AddressV;
                h = h * 31 + (int)desc.AddressW;
                h = h * 31 + desc.MipLODBias.GetHashCode();
                h = h * 31 + desc.MaxAnisotropy;
                h = h * 31 + (int)desc.ComparisonFunc;
                h = h * 31 + (int)desc.BorderColor;
                h = h * 31 + desc.MinLOD.GetHashCode();
                h = h * 31 + desc.MaxLOD.GetHashCode();
                return h;
            }
        }

        // ==========================================================================================
        //  资源创建 (Buffer / Texture / Upload Buffer)
        // ==========================================================================================

        /// <summary>创建缓冲区资源</summary>
        public override RHIBuffer CreateBuffer(in RHIBufferDesc desc, string name = "")
        {
            var heapType = D3D12FormatMapper.ToHeapType(desc.HeapType);
            var heapProps = new HeapProperties(heapType);

            var resourceDesc = ResourceDescription.Buffer((ulong)desc.SizeInBytes);

            // 设置资源标志
            ResourceFlags flags = ResourceFlags.None;
            if ((desc.Flags & RHIBufferFlags.AllowUnorderedAccess) != 0)
                flags |= ResourceFlags.AllowUnorderedAccess;
            if ((desc.Flags & RHIBufferFlags.DenyShaderResource) != 0)
                flags |= ResourceFlags.DenyShaderResource;
            resourceDesc.Flags = flags;

            var initialState = desc.HeapType == RHIHeapType.Upload
                ? ResourceStates.GenericRead
                : ResourceStates.Common;

            var resource = NativeDevice.CreateCommittedResource(
                heapProps, HeapFlags.None, resourceDesc, initialState, null);

            if (!string.IsNullOrEmpty(name))
                resource.Name = name;

            return new D3D12Buffer(resource, desc,
                D3D12FormatMapper.ToRHIResourceStates(initialState), name);
        }

        /// <summary>创建带初始数据的缓冲区（通过上传堆 + Copy）</summary>
        public override RHIBuffer CreateCommittedBuffer(in RHIBufferDesc desc, byte[] initialData, string name = "")
        {
            var buffer = CreateBuffer(desc, name);

            if (initialData != null && initialData.Length > 0)
            {
                UploadToBuffer(buffer, initialData, 0);
            }

            return buffer;
        }

        /// <summary>创建上传堆缓冲区（CPU 可写，GPU 可读）</summary>
        public override RHIBuffer CreateUploadBuffer(ulong sizeInBytes, string name = "")
        {
            var desc = new RHIBufferDesc
            {
                SizeInBytes = (int)sizeInBytes,
                HeapType = RHIHeapType.Upload,
                Flags = RHIBufferFlags.None
            };
            return CreateBuffer(desc, name);
        }

        /// <summary>创建纹理资源</summary>
        public override RHITexture CreateTexture(in RHITextureDesc desc, string name = "")
        {
            var heapProps = new HeapProperties(HeapType.Default);

            ResourceFlags flags = ResourceFlags.None;
            if ((desc.Flags & RHITextureFlags.AllowRenderTarget) != 0)
                flags |= ResourceFlags.AllowRenderTarget;
            if ((desc.Flags & RHITextureFlags.AllowDepthStencil) != 0)
                flags |= ResourceFlags.AllowDepthStencil;
            if ((desc.Flags & RHITextureFlags.AllowUnorderedAccess) != 0)
                flags |= ResourceFlags.AllowUnorderedAccess;
            if ((desc.Flags & RHITextureFlags.DenyShaderResource) != 0)
                flags |= ResourceFlags.DenyShaderResource;

            var dxgiFormat = D3D12FormatMapper.ToDxgiFormat(desc.Format);

            ResourceDescription resourceDesc = desc.Dimension switch
            {
                RHITextureDimension.Texture1D => ResourceDescription.Texture1D(
                    dxgiFormat, (int)desc.Width, (int)desc.DepthOrArraySize,
                    (int)desc.MipLevels, flags),
                RHITextureDimension.Texture2D => ResourceDescription.Texture2D(
                    dxgiFormat, (int)desc.Width, (int)desc.Height,
                    (int)desc.DepthOrArraySize, (int)desc.MipLevels,
                    (int)desc.SampleCount, (int)desc.SampleQuality,
                    TextureLayout.Unknown, flags),
                RHITextureDimension.Texture3D => ResourceDescription.Texture3D(
                    dxgiFormat, (int)desc.Width, (int)desc.Height,
                    (int)desc.DepthOrArraySize, (int)desc.MipLevels, flags),
                _ => ResourceDescription.Texture2D(
                    dxgiFormat, (int)desc.Width, (int)desc.Height, 1, 1, 1, 0,
                    TextureLayout.Unknown, flags)
            };

            // 优化清除值（用于 RTV/DSV）
            ClearValue? clearValue = null;
            if ((flags & ResourceFlags.AllowRenderTarget) != 0)
            {
                clearValue = new ClearValue
                {
                    Format = dxgiFormat,
                    Color = new Color4(0, 0, 0, 0)
                };
            }
            else if ((flags & ResourceFlags.AllowDepthStencil) != 0)
            {
                clearValue = new ClearValue
                {
                    Format = dxgiFormat,
                    DepthStencil = new DepthStencilValue(1.0f, 0)
                };
            }

            var initialState = (flags & ResourceFlags.AllowRenderTarget) != 0
                ? ResourceStates.RenderTarget
                : (flags & ResourceFlags.AllowDepthStencil) != 0
                    ? ResourceStates.DepthWrite
                    : ResourceStates.Common;

            var resource = NativeDevice.CreateCommittedResource(
                heapProps, HeapFlags.None, resourceDesc, initialState, clearValue);

            if (!string.IsNullOrEmpty(name))
                resource.Name = name;

            return new D3D12Texture(resource, desc,
                D3D12FormatMapper.ToRHIResourceStates(initialState), name);
        }

        // ==========================================================================================
        //  资源上传 (Upload Buffer + Copy)
        // ==========================================================================================

        /// <summary>上传数据到缓冲区（同步：创建上传堆 → Map → Copy → 命令拷贝 → 等待）</summary>
        public override void UploadToBuffer<T>(RHIBuffer dest, ReadOnlySpan<T> data, ulong destOffset)
        {
            ulong byteSize = (ulong)(data.Length * Unsafe.SizeOf<T>());
            if (byteSize == 0) return;

            // 创建上传堆缓冲区
            var upload = CreateUploadBuffer(byteSize, "Upload_Temp");

            // 映射并写入数据
            IntPtr pMapped = upload.Map();
            try
            {
                // 将 Span 数据拷贝到映射内存
                ref byte srcRef = ref Unsafe.As<T, byte>(ref MemoryMarshal.GetReference(data));
                unsafe
                {
                    Buffer.MemoryCopy(
                        Unsafe.AsPointer(ref srcRef),
                        (void*)pMapped,
                        byteSize, byteSize);
                }
            }
            finally
            {
                upload.Unmap();
            }

            // 创建 Copy 命令列表执行拷贝
            var copyQueue = GetUploadQueue();
            var cmdList = new D3D12CommandList(this, RHIQueueType.Copy, "Upload_CmdList");
            cmdList.Reset();

            // 屏障：目标从 Common 转为 CopyDest
            cmdList.ResourceBarrier(dest, dest.CurrentState, RHIResourceState.CopyDest);
            cmdList.CopyBufferRegion(dest, destOffset, upload, 0, byteSize);
            // 屏障：目标从 CopyDest 转回原状态
            cmdList.ResourceBarrier(dest, RHIResourceState.CopyDest, dest.CurrentState);

            cmdList.Close();
            copyQueue.ExecuteCommandLists(cmdList);
            copyQueue.Flush();

            // 清理临时资源
            cmdList.Dispose();
            upload.Dispose();
        }

        /// <summary>上传数据到缓冲区（byte[] 重载）</summary>
        public void UploadToBuffer(RHIBuffer dest, byte[] data, ulong destOffset)
        {
            if (data == null || data.Length == 0) return;
            ulong byteSize = (ulong)data.Length;
            var upload = CreateUploadBuffer(byteSize, "Upload_Temp");

            IntPtr pMapped = upload.Map();
            try
            {
                Marshal.Copy(data, 0, pMapped, data.Length);
            }
            finally
            {
                upload.Unmap();
            }

            var copyQueue = GetUploadQueue();
            var cmdList = new D3D12CommandList(this, RHIQueueType.Copy, "Upload_CmdList");
            cmdList.Reset();
            cmdList.ResourceBarrier(dest, dest.CurrentState, RHIResourceState.CopyDest);
            cmdList.CopyBufferRegion(dest, destOffset, upload, 0, byteSize);
            cmdList.ResourceBarrier(dest, RHIResourceState.CopyDest, dest.CurrentState);
            cmdList.Close();
            copyQueue.ExecuteCommandLists(cmdList);
            copyQueue.Flush();
            cmdList.Dispose();
            upload.Dispose();
        }

        /// <summary>
        /// 上传纹理数据（RHI 基类虚方法实现）。
        /// 将 ReadOnlySpan 转换为 byte[] 并委托给 D3D12 具体实现。
        /// </summary>
        public override void UploadToTexture(RHITexture dest, ReadOnlySpan<byte> data, int width, int height, int mipIndex = 0, int sliceIndex = 0)
        {
            if (dest is D3D12Texture d3dTex)
            {
                byte[] arr = data.ToArray();
                uint subresource = (uint)(mipIndex + sliceIndex * 1); // sliceIndex * mipCount 简化
                UploadToTexture(d3dTex, arr, (uint)width, (uint)height, subresource);
            }
        }

        /// <summary>上传纹理数据（支持 2D 纹理单子资源上传）</summary>
        public new void UploadToTexture(D3D12Texture dest, byte[] pixelData, uint width, uint height,
                                     uint subresource = 0, uint rowPitch = 0)
        {
            // 计算行间距（必须 256 字节对齐）
            uint alignedRowPitch = rowPitch > 0 ? rowPitch : (uint)((width * 4 + 255) & ~255u);
            ulong sliceSize = alignedRowPitch * height;
            ulong uploadSize = sliceSize;

            var upload = CreateUploadBuffer(uploadSize, "Upload_Texture_Temp");

            // 映射并拷贝数据（逐行拷贝，处理行间距对齐）
            IntPtr pMapped = upload.Map();
            try
            {
                uint srcRowPitch = width * 4; // 假设 RGBA8
                unsafe
                {
                    byte* pDst = (byte*)pMapped;
                    fixed (byte* pSrc = pixelData)
                    {
                        for (uint y = 0; y < height; y++)
                        {
                            Buffer.MemoryCopy(
                                pSrc + y * srcRowPitch,
                                pDst + y * alignedRowPitch,
                                srcRowPitch, srcRowPitch);
                        }
                    }
                }
            }
            finally
            {
                upload.Unmap();
            }

            // 执行 CopyTextureRegion
            var copyQueue = GetUploadQueue();
            var cmdList = new D3D12CommandList(this, RHIQueueType.Copy, "Upload_Texture_Cmd");
            cmdList.Reset();
            cmdList.ResourceBarrier(dest, dest.CurrentState, RHIResourceState.CopyDest);
            cmdList.CopyTextureRegion(dest, subresource, 0, 0, 0, upload, 0,
                                       alignedRowPitch, (uint)sliceSize, width, height);
            cmdList.ResourceBarrier(dest, RHIResourceState.CopyDest, dest.CurrentState);
            cmdList.Close();
            copyQueue.ExecuteCommandLists(cmdList);
            copyQueue.Flush();
            cmdList.Dispose();
            upload.Dispose();
        }

        /// <summary>获取上传用 Copy 命令队列（懒初始化）</summary>
        private D3D12CommandQueue GetUploadQueue()
        {
            if (_uploadQueue == null)
            {
                _uploadQueue = (D3D12CommandQueue)GetCommandQueue(RHIQueueType.Copy);
            }
            return _uploadQueue;
        }

        // ==========================================================================================
        //  Root Signature 序列化与创建
        // ==========================================================================================

        /// <summary>
        /// 创建根签名。
        /// 使用 D3D12SerializeVersionedRootSignature 序列化 Root Signature 1.1 版本。
        /// 支持 DescriptorTable / Constants / CBV / SRV / UAV 参数和静态采样器。
        /// </summary>
        public override RHIRootSignature CreateRootSignature(in RHIRootSignatureDesc desc, string name = "")
        {
            // 构建 Vortice 根参数数组
            var parameters = new RootParameter1[desc.Parameters.Length];
            for (int i = 0; i < desc.Parameters.Length; i++)
            {
                var p = desc.Parameters[i];
                switch (p.ParameterType)
                {
                    case RHIRootParameterType.DescriptorTable:
                    {
                        var ranges = new DescriptorRange1[p.DescriptorTable.Value.Ranges.Length];
                        for (int j = 0; j < ranges.Length; j++)
                        {
                            var r = p.DescriptorTable.Value.Ranges[j];
                            ranges[j] = new DescriptorRange1(
                                D3D12FormatMapper.ToDescriptorRangeType(r.RangeType),
                                r.NumDescriptors,
                                r.BaseShaderRegister,
                                r.RegisterSpace,
                                DescriptorRangeFlags.None,
                                (uint)(r.OffsetInDescriptorsFromTableStart < 0
                                    ? uint.MaxValue
                                    : r.OffsetInDescriptorsFromTableStart));
                        }
                        parameters[i] = new RootParameter1(
                            RootParameterType.DescriptorTable,
                            new RootDescriptorTable { DescriptorRanges = ranges },
                            D3D12FormatMapper.ToShaderVisibility(p.ShaderVisibility));
                        break;
                    }
                    case RHIRootParameterType.Constants:
                    {
                        var c = p.Constants.Value;
                        parameters[i] = new RootParameter1(
                            new RootConstants(c.Num32BitValues, c.ShaderRegister, c.RegisterSpace),
                            D3D12FormatMapper.ToShaderVisibility(p.ShaderVisibility));
                        break;
                    }
                    case RHIRootParameterType.CBV:
                    case RHIRootParameterType.SRV:
                    case RHIRootParameterType.UAV:
                    {
                        var d = p.Descriptor.Value;
                        parameters[i] = new RootParameter1(
                            D3D12FormatMapper.ToRootParameterType(p.ParameterType),
                            new RootDescriptor1(d.ShaderRegister, d.RegisterSpace, RootDescriptorFlags.None),
                            D3D12FormatMapper.ToShaderVisibility(p.ShaderVisibility));
                        break;
                    }
                }
            }

            // 构建静态采样器数组
            var staticSamplers = new StaticSamplerDescription1[desc.StaticSamplers.Length];
            for (int i = 0; i < desc.StaticSamplers.Length; i++)
            {
                var s = desc.StaticSamplers[i];
                staticSamplers[i] = new StaticSamplerDescription1(
                    D3D12FormatMapper.ToFilter(s.Filter),
                    D3D12FormatMapper.ToAddressMode(s.AddressU),
                    D3D12FormatMapper.ToAddressMode(s.AddressV),
                    D3D12FormatMapper.ToAddressMode(s.AddressW),
                    s.MipLODBias,
                    s.MaxAnisotropy,
                    D3D12FormatMapper.ToComparisonFunc(s.ComparisonFunc),
                    D3D12FormatMapper.ToStaticBorderColor(s.BorderColor),
                    s.MinLOD,
                    s.MaxLOD,
                    s.ShaderRegister,
                    s.RegisterSpace,
                    D3D12FormatMapper.ToShaderVisibility(s.ShaderVisibility));
            }

            // 根签名标志
            RootSignatureFlags flags = RootSignatureFlags.None;
            if (desc.AllowInputAssembler || desc.Flags.HasFlag(RHIRootSignatureFlags.AllowInputAssemblerInputLayout))
                flags |= RootSignatureFlags.AllowInputAssemblerInputLayout;
            if (desc.Flags.HasFlag(RHIRootSignatureFlags.DenyVertexShaderRootAccess))
                flags |= RootSignatureFlags.DenyVertexShaderRootAccess;
            if (desc.Flags.HasFlag(RHIRootSignatureFlags.DenyPixelShaderRootAccess))
                flags |= RootSignatureFlags.DenyPixelShaderRootAccess;
            if (desc.Flags.HasFlag(RHIRootSignatureFlags.AllowStreamOutput))
                flags |= RootSignatureFlags.AllowStreamOutput;

            // 创建 1.1 版本描述并序列化
            var rootDesc1 = new RootSignatureDescription1(flags, parameters, staticSamplers);
            var versionedDesc = new VersionedRootSignatureDescription(rootDesc1);

            global::Vortice.Direct3D12.D3D12.D3D12SerializeVersionedRootSignature(
                versionedDesc, out Blob? blob, out Blob? errorBlob).CheckError();

            if (errorBlob != null)
            {
                // 如果有错误信息，输出到调试控制台
                errorBlob.Dispose();
            }

            // 创建根签名
            var rootSig = NativeDevice.CreateRootSignature(0,
                blob!.BufferPointer, blob.BufferSize);

            blob.Dispose();

            if (!string.IsNullOrEmpty(name))
                rootSig.Name = name;

            return new D3D12RootSignature(rootSig, desc.Parameters, name);
        }

        // ==========================================================================================
        //  PSO 创建 (Graphics / Compute / Mesh)
        // ==========================================================================================

        /// <summary>创建图形管线状态对象 (Graphics PSO)</summary>
        public override RHIPipelineState CreateGraphicsPipelineState(in RHIGraphicsPipelineDesc desc, string name = "")
        {
            // 构建 InputLayout
            InputLayoutDescription? inputLayout = null;
            if (desc.InputLayout != null && desc.InputLayout.Length > 0)
            {
                var elements = new InputElementDescription[desc.InputLayout.Length];
                for (int i = 0; i < desc.InputLayout.Length; i++)
                {
                    var e = desc.InputLayout[i];
                    elements[i] = new InputElementDescription(
                        e.SemanticName,
                        e.SemanticIndex,
                        D3D12FormatMapper.ToDxgiFormat(e.Format),
                        e.InputSlot,
                        e.AlignedByteOffset,
                        e.Classification == RHIInputClassification.PerInstanceData
                            ? InputClassification.PerInstanceData
                            : InputClassification.PerVertexData,
                        e.InstanceDataStepRate);
                }
                inputLayout = new InputLayoutDescription(elements);
            }

            // 构建 RTV 格式数组
            var rtvFormats = new Format[8];
            int rtCount = 0;
            if (desc.RTVFormats != null)
            {
                rtCount = Math.Min(desc.RTVFormats.Length, 8);
                for (int i = 0; i < rtCount; i++)
                    rtvFormats[i] = D3D12FormatMapper.ToDxgiFormat(desc.RTVFormats[i]);
            }

            // 构建 PSO 描述
            var psoDesc = new GraphicsPipelineStateDescription
            {
                RootSignature = desc.RootSignature != null
                    ? ((D3D12RootSignature)desc.RootSignature).NativeRootSignature
                    : null!,
                InputLayout = inputLayout ?? default,
                PrimitiveTopologyType = D3D12FormatMapper.ToPrimitiveTopologyType(desc.PrimitiveTopology),
                BlendState = BuildBlendState(desc.BlendState),
                DepthStencilState = BuildDepthStencilState(desc.DepthStencilState),
                RasterizerState = BuildRasterizerState(desc.RasterizerState),
                SampleMask = uint.MaxValue,
                SampleDescription = new SampleDescription(
                    Math.Max(1, desc.SampleCount),
                    0),
                DepthStencilFormat = D3D12FormatMapper.ToDxgiFormat(desc.DSVFormat),
                RenderTargetFormats = rtvFormats,
                Flags = PipelineStateFlags.None
            };

            // 设置 Shader 字节码（使用 GCHandle 钉住数组）
            GCHandle? vsHandle = null, psHandle = null, gsHandle = null, hsHandle = null, dsHandle = null;
            try
            {
                if (desc.VS.IsValid)
                {
                    vsHandle = GCHandle.Alloc(desc.VS.Bytecode, GCHandleType.Pinned);
                    psoDesc.VS = new ShaderBytecode(vsHandle.Value.AddrOfPinnedObject(), desc.VS.Bytecode.Length);
                }
                if (desc.PS.IsValid)
                {
                    psHandle = GCHandle.Alloc(desc.PS.Bytecode, GCHandleType.Pinned);
                    psoDesc.PS = new ShaderBytecode(psHandle.Value.AddrOfPinnedObject(), desc.PS.Bytecode.Length);
                }
                if (desc.GS.IsValid)
                {
                    gsHandle = GCHandle.Alloc(desc.GS.Bytecode, GCHandleType.Pinned);
                    psoDesc.GS = new ShaderBytecode(gsHandle.Value.AddrOfPinnedObject(), desc.GS.Bytecode.Length);
                }
                if (desc.HS.IsValid)
                {
                    hsHandle = GCHandle.Alloc(desc.HS.Bytecode, GCHandleType.Pinned);
                    psoDesc.HS = new ShaderBytecode(hsHandle.Value.AddrOfPinnedObject(), desc.HS.Bytecode.Length);
                }
                if (desc.DS.IsValid)
                {
                    dsHandle = GCHandle.Alloc(desc.DS.Bytecode, GCHandleType.Pinned);
                    psoDesc.DS = new ShaderBytecode(dsHandle.Value.AddrOfPinnedObject(), desc.DS.Bytecode.Length);
                }

                var pso = NativeDevice.CreateGraphicsPipelineState(psoDesc);

                if (!string.IsNullOrEmpty(name))
                    pso.Name = name;

                return new D3D12PipelineState(pso, RHIPipelineType.Graphics, name);
            }
            finally
            {
                vsHandle?.Free();
                psHandle?.Free();
                gsHandle?.Free();
                hsHandle?.Free();
                dsHandle?.Free();
            }
        }

        /// <summary>创建计算管线状态对象 (Compute PSO)</summary>
        public override RHIPipelineState CreateComputePipelineState(in RHIComputePipelineDesc desc, string name = "")
        {
            var psoDesc = new ComputePipelineStateDescription
            {
                RootSignature = desc.RootSignature != null
                    ? ((D3D12RootSignature)desc.RootSignature).NativeRootSignature
                    : null!,
                Flags = PipelineStateFlags.None
            };

            GCHandle? csHandle = null;
            try
            {
                if (desc.CS.IsValid)
                {
                    csHandle = GCHandle.Alloc(desc.CS.Bytecode, GCHandleType.Pinned);
                    psoDesc.CS = new ShaderBytecode(csHandle.Value.AddrOfPinnedObject(), desc.CS.Bytecode.Length);
                }

                var pso = NativeDevice.CreateComputePipelineState(psoDesc);

                if (!string.IsNullOrEmpty(name))
                    pso.Name = name;

                return new D3D12PipelineState(pso, RHIPipelineType.Compute, name);
            }
            finally
            {
                csHandle?.Free();
            }
        }

        /// <summary>创建 Mesh Shader 管线状态对象 (Mesh PSO)</summary>
        /// <remarks>
        /// Mesh Shader PSO 需要使用 Pipeline State Stream API (ID3D12Device2::CreatePipelineState)，
        /// 因为标准的 GraphicsPipelineStateDescription 不包含 MS/AS 字段。
        /// </remarks>
        public override RHIPipelineState CreateMeshPipelineState(in RHIMeshPipelineDesc desc, string name = "")
        {
            var builder = new PipelineStateStreamBuilder();
            GCHandle? msHandle = null, asHandle = null, psHandle = null;

            try
            {
                // 根签名
                if (desc.RootSignature != null)
                {
                    var rootSig = ((D3D12RootSignature)desc.RootSignature).NativeRootSignature;
                    builder.WriteRootSignature(MarshallHelper.GetComPointer(rootSig));
                }

                // Mesh Shader (MS)
                if (desc.MS.IsValid)
                {
                    msHandle = GCHandle.Alloc(desc.MS.Bytecode, GCHandleType.Pinned);
                    builder.WriteShaderBytecode(PipelineStateStreamBuilder.Type_MS,
                        msHandle.Value.AddrOfPinnedObject(), desc.MS.Bytecode.Length);
                }

                // Amplification Shader (AS)
                if (desc.AS.IsValid)
                {
                    asHandle = GCHandle.Alloc(desc.AS.Bytecode, GCHandleType.Pinned);
                    builder.WriteShaderBytecode(PipelineStateStreamBuilder.Type_AS,
                        asHandle.Value.AddrOfPinnedObject(), desc.AS.Bytecode.Length);
                }

                // Pixel Shader (PS)
                if (desc.PS.IsValid)
                {
                    psHandle = GCHandle.Alloc(desc.PS.Bytecode, GCHandleType.Pinned);
                    builder.WriteShaderBytecode(PipelineStateStreamBuilder.Type_PS,
                        psHandle.Value.AddrOfPinnedObject(), desc.PS.Bytecode.Length);
                }

                // 渲染目标格式
                if (desc.RTVFormats != null && desc.RTVFormats.Length > 0)
                {
                    var formats = new Format[desc.RTVFormats.Length];
                    for (int i = 0; i < formats.Length; i++)
                        formats[i] = D3D12FormatMapper.ToDxgiFormat(desc.RTVFormats[i]);
                    builder.WriteRTFormats(formats, formats.Length);
                }

                // 深度模板格式
                if (desc.DSVFormat != RHIFormat.Unknown)
                {
                    builder.WriteDSVFormat(D3D12FormatMapper.ToDxgiFormat(desc.DSVFormat));
                }

                // 采样描述
                builder.WriteSampleDesc(Math.Max(1u, desc.SampleCount), 0);

                // 图元拓扑类型
                builder.WritePrimitiveTopology(PrimitiveTopologyType.Triangle);

                // 创建 PSO（使用 Stream API）
                var streamDesc = new PipelineStateStreamDescription
                {
                    SizeInBytes = (IntPtr)builder.Size,
                    SubObjectStream = builder.Data
                };

                var pso = NativeDevice.CreatePipelineState(streamDesc);

                if (!string.IsNullOrEmpty(name))
                    pso.Name = name;

                return new D3D12PipelineState(pso, RHIPipelineType.Mesh, name);
            }
            finally
            {
                msHandle?.Free();
                asHandle?.Free();
                psHandle?.Free();
                builder.Dispose();
            }
        }

        // ── PSO 描述构建辅助方法 ──

        private static BlendDescription BuildBlendState(RHIBlendDesc? blendDesc)
        {
            if (!blendDesc.HasValue)
                return new BlendDescription(AlphaToCoverageEnable: false, IndependentBlendEnable: false);

            var b = blendDesc.Value;
            var rt = new RenderTargetBlendDescription[8];
            for (int i = 0; i < 8; i++)
            {
                var rtDesc = i < b.RenderTargets?.Length ? b.RenderTargets[i] : default;
                rt[i] = new RenderTargetBlendDescription(
                    rtDesc.BlendEnable,
                    rtDesc.SourceBlend,
                    rtDesc.DestinationBlend,
                    rtDesc.BlendOperation,
                    rtDesc.SourceBlendAlpha,
                    rtDesc.DestinationBlendAlpha,
                    rtDesc.BlendOperationAlpha,
                    rtDesc.RenderTargetWriteMask);
            }
            return new BlendDescription(b.AlphaToCoverageEnable, b.IndependentBlendEnable, rt);
        }

        private static DepthStencilDescription BuildDepthStencilState(RHIDepthStencilDesc? dsDesc)
        {
            if (!dsDesc.HasValue)
                return new DepthStencilDescription(false, DepthWriteMask.Zero, ComparisonFunction.Less,
                                                    false, 0, 0, default, default);

            var d = dsDesc.Value;
            return new DepthStencilDescription(
                d.DepthEnable,
                d.DepthWriteMask == RHIDepthWriteMask.All ? DepthWriteMask.All : DepthWriteMask.Zero,
                D3D12FormatMapper.ToComparisonFunc(d.DepthFunc),
                d.StencilEnable,
                d.StencilReadMask,
                d.StencilWriteMask,
                BuildStencilOp(d.FrontFace),
                BuildStencilOp(d.BackFace));
        }

        private static DepthStencilOperationDescription BuildStencilOp(RHIDepthStencilOp op)
        {
            return new DepthStencilOperationDescription(
                (StencilOperation)op.StencilFailOp,
                (StencilOperation)op.StencilDepthFailOp,
                (StencilOperation)op.StencilPassOp,
                D3D12FormatMapper.ToComparisonFunc(op.StencilFunc));
        }

        private static RasterizerDescription BuildRasterizerState(RHIRasterizerDesc? rastDesc)
        {
            if (!rastDesc.HasValue)
                return new RasterizerDescription(
                    FillMode.Solid, CullMode.Back, false, 0, 0, 0, true, false, false, 0, ConservativeRasterizationMode.Off);

            var r = rastDesc.Value;
            return new RasterizerDescription(
                r.FillMode == RHIFillMode.Wireframe ? FillMode.Wireframe : FillMode.Solid,
                (CullMode)r.CullMode,
                r.FrontCounterClockwise,
                r.DepthBias,
                r.DepthBiasClamp,
                r.SlopeScaledDepthBias,
                r.DepthClipEnable,
                r.MultisampleEnable,
                r.AntialiasedLineEnable,
                r.ForcedSampleCount,
                r.ConservativeRaster ? ConservativeRasterizationMode.On : ConservativeRasterizationMode.Off);
        }

        // ==========================================================================================
        //  命令列表 / 围栏 / 交换链创建
        // ==========================================================================================

        public override RHICommandList CreateCommandList(RHIQueueType type, string name = "")
        {
            return new D3D12CommandList(this, type, name);
        }

        public override RHIFence CreateFence(ulong initialValue, string name = "")
        {
            return CreateFenceInternal(initialValue, name);
        }

        /// <summary>内部围栏创建方法（直接返回 D3D12Fence）</summary>
        internal D3D12Fence CreateFenceInternal(ulong initialValue, string name)
        {
            var fence = NativeDevice.CreateFence(initialValue, FenceFlags.None);
            if (!string.IsNullOrEmpty(name))
                fence.Name = name;
            return new D3D12Fence(fence, initialValue, name);
        }

        public override RHISwapChain CreateSwapChain(IntPtr hwnd, int width, int height,
                                                      RHIFormat format, int bufferCount, bool vsync, string name = "")
        {
            return new D3D12SwapChain(this, hwnd, width, height, format, bufferCount, vsync, name);
        }

        // ==========================================================================================
        //  Shader 编译
        // ==========================================================================================

        public override RHIShaderBytecode CompileShader(string hlslSource, string entryPoint,
                                                         RHIShaderStage stage, RHIShaderModel shaderModel,
                                                         string? sourceName = null, bool debug = false)
        {
            return D3D12ShaderCompiler.Compile(hlslSource, entryPoint, stage, shaderModel, sourceName, debug);
        }

        public override RHIShaderBytecode CompileShaderFromFile(string filePath, string entryPoint,
                                                                 RHIShaderStage stage, RHIShaderModel shaderModel,
                                                                 bool debug = false)
        {
            return D3D12ShaderCompiler.CompileFromFile(filePath, entryPoint, stage, shaderModel, debug);
        }

        // ==========================================================================================
        //  间接命令签名 (Indirect Command Signature)
        // ==========================================================================================

        /// <summary>获取 DrawIndexed 间接命令签名（懒创建缓存）</summary>
        internal ID3D12CommandSignature GetDrawIndexedSignature()
        {
            if (_drawIndexedSig == null)
            {
                var argDesc = new IndirectArgumentDescription(IndirectArgumentType.DrawIndexed);
                var sigDesc = new CommandSignatureDescription
                {
                    ByteStride = 20, // sizeof(D3D12_DRAW_INDEXED_ARGUMENTS) = 5 * sizeof(uint)
                    NodeMask = 0
                };
                sigDesc.IndirectArguments = new[] { argDesc };
                _drawIndexedSig = NativeDevice.CreateCommandSignature(sigDesc);
                _drawIndexedSig.Name = "DrawIndexedIndirect_Sig";
            }
            return _drawIndexedSig;
        }

        /// <summary>获取 Dispatch 间接命令签名（懒创建缓存）</summary>
        internal ID3D12CommandSignature GetDispatchSignature()
        {
            if (_dispatchSig == null)
            {
                var argDesc = new IndirectArgumentDescription(IndirectArgumentType.Dispatch);
                var sigDesc = new CommandSignatureDescription
                {
                    ByteStride = 12, // sizeof(D3D12_DISPATCH_ARGUMENTS) = 3 * sizeof(uint)
                    NodeMask = 0
                };
                sigDesc.IndirectArguments = new[] { argDesc };
                _dispatchSig = NativeDevice.CreateCommandSignature(sigDesc);
                _dispatchSig.Name = "DispatchIndirect_Sig";
            }
            return _dispatchSig;
        }

        // ==========================================================================================
        //  资源命名 (D3D12 Object Name — WKPDID_D3D12ObjectName)
        //  Vortice 的 SetName() 内部调用 SetPrivateData(WKPDID_D3D12ObjectName, ...)
        //  设置的名称会出现在 PIX/Visual Studio Graphics Debugger/RenderDoc 等工具中
        // ==========================================================================================

        /// <summary>
        /// 设置 D3D12 资源的调试名称。
        /// 内部通过 WKPDID_D3D12ObjectName 私有数据设置，可在调试工具中识别对象。
        /// </summary>
        public void SetObjectName(RHIResource resource, string name)
        {
            if (resource is D3D12Buffer buf)
                buf.Resource.Name = name;  // → SetPrivateData(WKPDID_D3D12ObjectName)
            else if (resource is D3D12Texture tex)
                tex.Resource.Name = name;  // → SetPrivateData(WKPDID_D3D12ObjectName)
        }

        /// <summary>
        /// 设置任意 D3D12 设备子对象的调试名称 (D3D12_OBJECT_NAME)。
        /// 适用于 PipelineState、RootSignature、CommandQueue、Fence、DescriptorHeap 等。
        /// </summary>
        public void SetObjectName(ID3D12DeviceChild obj, string name)
        {
            obj.Name = name;  // → SetPrivateData(WKPDID_D3D12ObjectName)
        }

        // ==========================================================================================
        //  释放
        // ==========================================================================================

        public override void Dispose()
        {
            // 等待所有命令队列完成
            foreach (var queue in _commandQueues.Values)
                queue.Flush();

            // 释放命令签名
            _drawIndexedSig?.Dispose();
            _dispatchSig?.Dispose();

            // 释放描述符堆
            _cbvSrvUavHeap?.Dispose();
            _samplerHeap?.Dispose();
            _rtvHeap?.Dispose();
            _dsvHeap?.Dispose();

            // 释放命令队列
            foreach (var queue in _commandQueues.Values)
                queue.Dispose();
            _commandQueues.Clear();

            // 释放信息队列与调试 (已禁用 - Vortice 3.2.0)
            // _infoQueue?.Dispose();
            // _debug?.Dispose();

            // 释放设备、适配器、工厂
            NativeDevice?.Dispose();
            NativeAdapter?.Dispose();
            NativeFactory?.Dispose();
        }
    }

    // ==============================================================================================
    //  区域十四: 辅助工具
    // ==============================================================================================

    /// <summary>COM 对象指针获取辅助</summary>
    internal static class MarshallHelper
    {
        /// <summary>获取 Vortice COM 对象的原生指针</summary>
        public static IntPtr GetComPointer(ID3D12DeviceChild obj)
        {
            // Vortice 的 COM 对象通过 NativePointer 属性暴露原生指针
            return obj.NativePointer;
        }
    }
}
