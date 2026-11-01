using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using Silk.NET.Vulkan;

using Buffer = Silk.NET.Vulkan.Buffer;
using Semaphore = Silk.NET.Vulkan.Semaphore;
namespace YamaEngine.RHI.Vulkan
{
    // ════════════════════════════════════════════════════════════════
    // Format / Enum Mapping
    // ════════════════════════════════════════════════════════════════

    internal static class VkFormatMapper
    {
        public static Format ToVkFormat(RHIFormat fmt)
        {
            return fmt switch
            {
                RHIFormat.R8_UNorm => Format.R8Unorm,
                RHIFormat.R8_SNorm => Format.R8SNorm,
                RHIFormat.R8_UInt => Format.R8Uint,
                RHIFormat.R8_SInt => Format.R8Sint,
                RHIFormat.R8G8_UNorm => Format.R8G8Unorm,
                RHIFormat.R8G8_SNorm => Format.R8G8SNorm,
                RHIFormat.R8G8_UInt => Format.R8G8Uint,
                RHIFormat.R8G8_SInt => Format.R8G8Sint,
                RHIFormat.R8G8B8A8_UNorm => Format.R8G8B8A8Unorm,
                RHIFormat.R8G8B8A8_SNorm => Format.R8G8B8A8SNorm,
                RHIFormat.R8G8B8A8_UInt => Format.R8G8B8A8Uint,
                RHIFormat.R8G8B8A8_SInt => Format.R8G8B8A8Sint,
                RHIFormat.B8G8R8A8_UNorm => Format.B8G8R8A8Unorm,
                RHIFormat.B8G8R8A8_UNorm_SRGB => Format.B8G8R8A8Srgb,
                RHIFormat.R10G10B10A2_UNorm => Format.A2B10G10R10UnormPack32,
                RHIFormat.R10G10B10A2_UInt => Format.A2B10G10R10UintPack32,
                RHIFormat.R16_Float => Format.R16Sfloat,
                RHIFormat.R16_UNorm => Format.R16Unorm,
                RHIFormat.R16_SNorm => Format.R16SNorm,
                RHIFormat.R16_UInt => Format.R16Uint,
                RHIFormat.R16_SInt => Format.R16Sint,
                RHIFormat.R16G16_Float => Format.R16G16Sfloat,
                RHIFormat.R16G16_UNorm => Format.R16G16Unorm,
                RHIFormat.R16G16_SNorm => Format.R16G16SNorm,
                RHIFormat.R16G16_UInt => Format.R16G16Uint,
                RHIFormat.R16G16_SInt => Format.R16G16Sint,
                RHIFormat.R16G16B16A16_Float => Format.R16G16B16A16Sfloat,
                RHIFormat.R16G16B16A16_UNorm => Format.R16G16B16A16Unorm,
                RHIFormat.R16G16B16A16_SNorm => Format.R16G16B16A16SNorm,
                RHIFormat.R16G16B16A16_UInt => Format.R16G16B16A16Uint,
                RHIFormat.R16G16B16A16_SInt => Format.R16G16B16A16Sint,
                RHIFormat.R32_Float => Format.R32Sfloat,
                RHIFormat.R32_UInt => Format.R32Uint,
                RHIFormat.R32_SInt => Format.R32Sint,
                RHIFormat.R32G32_Float => Format.R32G32Sfloat,
                RHIFormat.R32G32_UInt => Format.R32G32Uint,
                RHIFormat.R32G32_SInt => Format.R32G32Sint,
                RHIFormat.R32G32B32_Float => Format.R32G32B32Sfloat,
                RHIFormat.R32G32B32_UInt => Format.R32G32B32Uint,
                RHIFormat.R32G32B32_SInt => Format.R32G32B32Sint,
                RHIFormat.R32G32B32A32_Float => Format.R32G32B32A32Sfloat,
                RHIFormat.R32G32B32A32_UInt => Format.R32G32B32A32Uint,
                RHIFormat.R32G32B32A32_SInt => Format.R32G32B32A32Sint,
                RHIFormat.R11G11B10_Float => Format.B10G11R11UfloatPack32,
                RHIFormat.D16_UNorm => Format.D16Unorm,
                RHIFormat.D24_UNorm_S8_UInt => Format.D24UnormS8Uint,
                RHIFormat.D32_Float => Format.D32Sfloat,
                RHIFormat.D32_Float_S8X24_UInt => Format.D32SfloatS8Uint,
                RHIFormat.BC1_UNorm => Format.BC1RgbaUnormBlock,
                RHIFormat.BC1_UNorm_SRGB => Format.BC1RgbaSrgbBlock,
                RHIFormat.BC2_UNorm => Format.BC2UnormBlock,
                RHIFormat.BC2_UNorm_SRGB => Format.BC2SrgbBlock,
                RHIFormat.BC3_UNorm => Format.BC3UnormBlock,
                RHIFormat.BC3_UNorm_SRGB => Format.BC3SrgbBlock,
                RHIFormat.BC4_UNorm => Format.BC4UnormBlock,
                RHIFormat.BC4_SNorm => Format.BC4SNormBlock,
                RHIFormat.BC5_UNorm => Format.BC5UnormBlock,
                RHIFormat.BC5_SNorm => Format.BC5SNormBlock,
                RHIFormat.BC6H_UF16 => Format.BC6HUfloatBlock,
                RHIFormat.BC6H_SF16 => Format.BC6HSfloatBlock,
                RHIFormat.BC7_UNorm => Format.BC7UnormBlock,
                RHIFormat.BC7_UNorm_SRGB => Format.BC7SrgbBlock,
                _ => Format.Undefined
            };
        }

        public static RHIFormat FromVkFormat(Format fmt)
        {
            return fmt switch
            {
                Format.R8Unorm => RHIFormat.R8_UNorm,
                Format.R8SNorm => RHIFormat.R8_SNorm,
                Format.R8Uint => RHIFormat.R8_UInt,
                Format.R8Sint => RHIFormat.R8_SInt,
                Format.R8G8Unorm => RHIFormat.R8G8_UNorm,
                Format.R8G8SNorm => RHIFormat.R8G8_SNorm,
                Format.R8G8Uint => RHIFormat.R8G8_UInt,
                Format.R8G8Sint => RHIFormat.R8G8_SInt,
                Format.R8G8B8A8Unorm => RHIFormat.R8G8B8A8_UNorm,
                Format.R8G8B8A8SNorm => RHIFormat.R8G8B8A8_SNorm,
                Format.R8G8B8A8Uint => RHIFormat.R8G8B8A8_UInt,
                Format.R8G8B8A8Sint => RHIFormat.R8G8B8A8_SInt,
                Format.B8G8R8A8Unorm => RHIFormat.B8G8R8A8_UNorm,
                Format.B8G8R8A8Srgb => RHIFormat.B8G8R8A8_UNorm_SRGB,
                Format.A2B10G10R10UnormPack32 => RHIFormat.R10G10B10A2_UNorm,
                Format.A2B10G10R10UintPack32 => RHIFormat.R10G10B10A2_UInt,
                Format.R16Sfloat => RHIFormat.R16_Float,
                Format.R16Unorm => RHIFormat.R16_UNorm,
                Format.R16SNorm => RHIFormat.R16_SNorm,
                Format.R16Uint => RHIFormat.R16_UInt,
                Format.R16Sint => RHIFormat.R16_SInt,
                Format.R16G16Sfloat => RHIFormat.R16G16_Float,
                Format.R16G16Unorm => RHIFormat.R16G16_UNorm,
                Format.R16G16SNorm => RHIFormat.R16G16_SNorm,
                Format.R16G16Uint => RHIFormat.R16G16_UInt,
                Format.R16G16Sint => RHIFormat.R16G16_SInt,
                Format.R16G16B16A16Sfloat => RHIFormat.R16G16B16A16_Float,
                Format.R16G16B16A16Unorm => RHIFormat.R16G16B16A16_UNorm,
                Format.R16G16B16A16SNorm => RHIFormat.R16G16B16A16_SNorm,
                Format.R16G16B16A16Uint => RHIFormat.R16G16B16A16_UInt,
                Format.R16G16B16A16Sint => RHIFormat.R16G16B16A16_SInt,
                Format.R32Sfloat => RHIFormat.R32_Float,
                Format.R32Uint => RHIFormat.R32_UInt,
                Format.R32Sint => RHIFormat.R32_SInt,
                Format.R32G32Sfloat => RHIFormat.R32G32_Float,
                Format.R32G32Uint => RHIFormat.R32G32_UInt,
                Format.R32G32Sint => RHIFormat.R32G32_SInt,
                Format.R32G32B32Sfloat => RHIFormat.R32G32B32_Float,
                Format.R32G32B32Uint => RHIFormat.R32G32B32_UInt,
                Format.R32G32B32Sint => RHIFormat.R32G32B32_SInt,
                Format.R32G32B32A32Sfloat => RHIFormat.R32G32B32A32_Float,
                Format.R32G32B32A32Uint => RHIFormat.R32G32B32A32_UInt,
                Format.R32G32B32A32Sint => RHIFormat.R32G32B32A32_SInt,
                Format.B10G11R11UfloatPack32 => RHIFormat.R11G11B10_Float,
                Format.D16Unorm => RHIFormat.D16_UNorm,
                Format.D24UnormS8Uint => RHIFormat.D24_UNorm_S8_UInt,
                Format.D32Sfloat => RHIFormat.D32_Float,
                Format.D32SfloatS8Uint => RHIFormat.D32_Float_S8X24_UInt,
                _ => RHIFormat.Unknown
            };
        }

        public static BlendFactor ToVkBlendFactor(RHIBlend b)
        {
            return b switch
            {
                RHIBlend.Zero => BlendFactor.Zero,
                RHIBlend.One => BlendFactor.One,
                RHIBlend.SrcColor => BlendFactor.SrcColor,
                RHIBlend.InvSrcColor => BlendFactor.OneMinusSrcColor,
                RHIBlend.SrcAlpha => BlendFactor.SrcAlpha,
                RHIBlend.InvSrcAlpha => BlendFactor.OneMinusSrcAlpha,
                RHIBlend.DestAlpha => BlendFactor.DstAlpha,
                RHIBlend.InvDestAlpha => BlendFactor.OneMinusDstAlpha,
                RHIBlend.DestColor => BlendFactor.DstColor,
                RHIBlend.InvDestColor => BlendFactor.OneMinusDstColor,
                RHIBlend.SrcAlphaSaturate => BlendFactor.SrcAlphaSaturate,
                RHIBlend.BlendFactor => BlendFactor.ConstantColor,
                RHIBlend.InvBlendFactor => BlendFactor.OneMinusConstantColor,
                RHIBlend.Src1Color => BlendFactor.Src1Color,
                RHIBlend.InvSrc1Color => BlendFactor.OneMinusSrc1Color,
                RHIBlend.Src1Alpha => BlendFactor.Src1Alpha,
                RHIBlend.InvSrc1Alpha => BlendFactor.OneMinusSrc1Alpha,
                _ => BlendFactor.Zero
            };
        }

        public static BlendOp ToVkBlendOp(RHIBlendOp op)
        {
            return op switch
            {
                RHIBlendOp.Add => BlendOp.Add,
                RHIBlendOp.Subtract => BlendOp.Subtract,
                RHIBlendOp.RevSubtract => BlendOp.ReverseSubtract,
                RHIBlendOp.Min => BlendOp.Min,
                RHIBlendOp.Max => BlendOp.Max,
                _ => BlendOp.Add
            };
        }

        public static CompareOp ToVkCompareOp(RHIComparison c)
        {
            return c switch
            {
                RHIComparison.Never => CompareOp.Never,
                RHIComparison.Less => CompareOp.Less,
                RHIComparison.Equal => CompareOp.Equal,
                RHIComparison.LessEqual => CompareOp.LessOrEqual,
                RHIComparison.Greater => CompareOp.Greater,
                RHIComparison.NotEqual => CompareOp.NotEqual,
                RHIComparison.GreaterEqual => CompareOp.GreaterOrEqual,
                RHIComparison.Always => CompareOp.Always,
                _ => CompareOp.Never
            };
        }

        public static StencilOp ToVkStencilOp(RHIStencilOp op)
        {
            return op switch
            {
                RHIStencilOp.Keep => StencilOp.Keep,
                RHIStencilOp.Zero => StencilOp.Zero,
                RHIStencilOp.Replace => StencilOp.Replace,
                RHIStencilOp.IncrSaturate => StencilOp.IncrementAndClamp,
                RHIStencilOp.DecrSaturate => StencilOp.DecrementAndClamp,
                RHIStencilOp.Invert => StencilOp.Invert,
                RHIStencilOp.Incr => StencilOp.IncrementAndWrap,
                RHIStencilOp.Decr => StencilOp.DecrementAndWrap,
                _ => StencilOp.Keep
            };
        }

        public static CullModeFlags ToVkCullMode(RHICullMode c)
        {
            return c switch
            {
                RHICullMode.None => CullModeFlags.None,
                RHICullMode.Front => CullModeFlags.FrontBit,
                RHICullMode.Back => CullModeFlags.BackBit,
                _ => CullModeFlags.None
            };
        }

        public static PolygonMode ToVkPolygonMode(RHIFillMode f)
        {
            return f switch
            {
                RHIFillMode.Solid => PolygonMode.Fill,
                RHIFillMode.Wireframe => PolygonMode.Line,
                _ => PolygonMode.Fill
            };
        }

        public static PrimitiveTopology ToVkTopology(RHIPrimitiveTopology t)
        {
            return t switch
            {
                RHIPrimitiveTopology.PointList => PrimitiveTopology.PointList,
                RHIPrimitiveTopology.LineList => PrimitiveTopology.LineList,
                RHIPrimitiveTopology.LineStrip => PrimitiveTopology.LineStrip,
                RHIPrimitiveTopology.TriangleList => PrimitiveTopology.TriangleList,
                RHIPrimitiveTopology.TriangleStrip => PrimitiveTopology.TriangleStrip,
                RHIPrimitiveTopology.TriangleFan => PrimitiveTopology.TriangleFan,
                RHIPrimitiveTopology.PatchList => PrimitiveTopology.PatchList,
                _ => PrimitiveTopology.TriangleList
            };
        }

        public static ShaderStageFlags ToVkShaderStage(RHIShaderStage s)
        {
            return s switch
            {
                RHIShaderStage.Vertex => ShaderStageFlags.VertexBit,
                RHIShaderStage.Hull => ShaderStageFlags.TessellationControlBit,
                RHIShaderStage.Domain => ShaderStageFlags.TessellationEvaluationBit,
                RHIShaderStage.Geometry => ShaderStageFlags.GeometryBit,
                RHIShaderStage.Pixel => ShaderStageFlags.FragmentBit,
                RHIShaderStage.Compute => ShaderStageFlags.ComputeBit,
                RHIShaderStage.Mesh => ShaderStageFlags.MeshBitExt,
                RHIShaderStage.Amplification => ShaderStageFlags.TaskBitExt,
                RHIShaderStage.AllGraphics => ShaderStageFlags.AllGraphics,
                RHIShaderStage.All => ShaderStageFlags.All,
                _ => ShaderStageFlags.None
            };
        }

        public static ShaderStageFlags ToVkShaderVisibility(RHIShaderVisibility v)
        {
            return v switch
            {
                RHIShaderVisibility.All => ShaderStageFlags.All,
                RHIShaderVisibility.Vertex => ShaderStageFlags.VertexBit,
                RHIShaderVisibility.Hull => ShaderStageFlags.TessellationControlBit,
                RHIShaderVisibility.Domain => ShaderStageFlags.TessellationEvaluationBit,
                RHIShaderVisibility.Geometry => ShaderStageFlags.GeometryBit,
                RHIShaderVisibility.Pixel => ShaderStageFlags.FragmentBit,
                RHIShaderVisibility.Amplification => ShaderStageFlags.TaskBitExt,
                RHIShaderVisibility.Mesh => ShaderStageFlags.MeshBitExt,
                _ => ShaderStageFlags.All
            };
        }

        public static SamplerAddressMode ToVkAddressMode(RHITextureAddressMode m)
        {
            return m switch
            {
                RHITextureAddressMode.Wrap => SamplerAddressMode.Repeat,
                RHITextureAddressMode.Mirror => SamplerAddressMode.MirroredRepeat,
                RHITextureAddressMode.Clamp => SamplerAddressMode.ClampToEdge,
                RHITextureAddressMode.Border => SamplerAddressMode.ClampToBorder,
                RHITextureAddressMode.MirrorOnce => SamplerAddressMode.MirrorClampToEdge,
                _ => SamplerAddressMode.Repeat
            };
        }

        public static DescriptorType ToVkDescriptorType(RHIRootParameterType t)
        {
            return t switch
            {
                RHIRootParameterType.CBV => DescriptorType.UniformBuffer,
                RHIRootParameterType.SRV => DescriptorType.StorageBuffer,
                RHIRootParameterType.UAV => DescriptorType.StorageBuffer,
                RHIRootParameterType.Sampler => DescriptorType.Sampler,
                _ => DescriptorType.UniformBuffer
            };
        }

        public static ImageAspectFlags GetAspectMask(Format fmt)
        {
            if (fmt == Format.D16Unorm || fmt == Format.D32Sfloat)
                return ImageAspectFlags.DepthBit;
            if (fmt == Format.D24UnormS8Uint || fmt == Format.D32SfloatS8Uint)
                return ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit;
            return ImageAspectFlags.ColorBit;
        }

        public static (ImageLayout, AccessFlags, PipelineStageFlags) ResourceStateToVk(RHIResourceState state)
        {
            return state switch
            {
                RHIResourceState.Common => (ImageLayout.General, AccessFlags.None, PipelineStageFlags.AllCommandsBit),
                RHIResourceState.VertexAndConstantBuffer => (ImageLayout.Undefined,
                    AccessFlags.VertexAttributeReadBit | AccessFlags.UniformReadBit, PipelineStageFlags.VertexInputBit | PipelineStageFlags.VertexShaderBit),
                RHIResourceState.IndexBuffer => (ImageLayout.Undefined, AccessFlags.IndexReadBit, PipelineStageFlags.VertexInputBit),
                RHIResourceState.RenderTarget => (ImageLayout.ColorAttachmentOptimal, AccessFlags.ColorAttachmentWriteBit | AccessFlags.ColorAttachmentReadBit, PipelineStageFlags.ColorAttachmentOutputBit),
                RHIResourceState.UnorderedAccess => (ImageLayout.General, AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit, PipelineStageFlags.AllGraphicsBit | PipelineStageFlags.ComputeShaderBit),
                RHIResourceState.DepthWrite => (ImageLayout.DepthStencilAttachmentOptimal, AccessFlags.DepthStencilAttachmentReadBit | AccessFlags.DepthStencilAttachmentWriteBit, PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit),
                RHIResourceState.DepthRead => (ImageLayout.DepthStencilReadOnlyOptimal, AccessFlags.DepthStencilAttachmentReadBit | AccessFlags.ShaderReadBit, PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit | PipelineStageFlags.FragmentShaderBit),
                RHIResourceState.NonPixelShaderResource => (ImageLayout.ShaderReadOnlyOptimal, AccessFlags.ShaderReadBit, PipelineStageFlags.ComputeShaderBit | PipelineStageFlags.VertexShaderBit | PipelineStageFlags.GeometryShaderBit),
                RHIResourceState.PixelShaderResource => (ImageLayout.ShaderReadOnlyOptimal, AccessFlags.ShaderReadBit, PipelineStageFlags.FragmentShaderBit),
                RHIResourceState.ShaderResource => (ImageLayout.ShaderReadOnlyOptimal, AccessFlags.ShaderReadBit, PipelineStageFlags.AllGraphicsBit | PipelineStageFlags.ComputeShaderBit),
                RHIResourceState.CopyDest => (ImageLayout.TransferDstOptimal, AccessFlags.TransferWriteBit, PipelineStageFlags.TransferBit),
                RHIResourceState.CopySource => (ImageLayout.TransferSrcOptimal, AccessFlags.TransferReadBit, PipelineStageFlags.TransferBit),
                RHIResourceState.IndirectArgument => (ImageLayout.Undefined, AccessFlags.IndirectCommandReadBit, PipelineStageFlags.DrawIndirectBit),
                RHIResourceState.Present => (ImageLayout.PresentSrcKhr, AccessFlags.None, PipelineStageFlags.AllCommandsBit),
                RHIResourceState.RaytracingAccelerationStructure => (ImageLayout.General, AccessFlags.AccelerationStructureReadBitKhr | AccessFlags.AccelerationStructureWriteBitKhr, PipelineStageFlags.AccelerationStructureBuildBitKhr),
                _ => (ImageLayout.General, AccessFlags.None, PipelineStageFlags.AllCommandsBit)
            };
        }

        public static SampleCountFlags ToVkSampleCount(int sampleCount)
        {
            return sampleCount switch
            {
                1 => SampleCountFlags.Count1Bit,
                2 => SampleCountFlags.Count2Bit,
                4 => SampleCountFlags.Count4Bit,
                8 => SampleCountFlags.Count8Bit,
                16 => SampleCountFlags.Count16Bit,
                32 => SampleCountFlags.Count32Bit,
                64 => SampleCountFlags.Count64Bit,
                _ => SampleCountFlags.Count1Bit
            };
        }
    }

    // ════════════════════════════════════════════════════════════════
    // Utility Helpers
    // ════════════════════════════════════════════════════════════════

    internal static class VkHelper
    {
        public static void Check(Result result, string message = "Vulkan operation failed")
        {
            if (result != Result.Success)
                throw new InvalidOperationException($"{message} (VkResult: {result})");
        }

        public static byte[] StringToUtf8(string s)
        {
            var bytes = new byte[Encoding.UTF8.GetByteCount(s) + 1];
            Encoding.UTF8.GetBytes(s, 0, s.Length, bytes, 0);
            return bytes;
        }

        public static unsafe string PtrToString(byte* ptr)
        {
            if (ptr == null) return string.Empty;
            int len = 0;
            while (ptr[len] != 0) len++;
            return Encoding.UTF8.GetString(ptr, len);
        }

        public static uint FindMemoryType(PhysicalDeviceMemoryProperties memProps, uint typeFilter, MemoryPropertyFlags props)
        {
            for (int i = 0; i < memProps.MemoryTypeCount; i++)
            {
                if ((typeFilter & (1u << i)) != 0 &&
                    (memProps.MemoryTypes[i].PropertyFlags & props) == props)
                    return (uint)i;
            }
            throw new InvalidOperationException("Failed to find suitable memory type");
        }

        public static bool HasFlag<T>(T value, T flag) where T : struct, Enum
        {
            long v = Convert.ToInt64(value);
            long f = Convert.ToInt64(flag);
            return (v & f) == f;
        }
    }

    // ════════════════════════════════════════════════════════════════
    // Memory Allocator (VMA-style block-based suballocation)
    // ════════════════════════════════════════════════════════════════

    internal unsafe struct VkAllocation
    {
        public DeviceMemory Memory;
        public ulong Offset;
        public ulong Size;
        public uint MemoryTypeIndex;
        public bool IsDedicated;
        public void* MappedPtr;
    }

    internal sealed unsafe class VkMemoryAllocator : IDisposable
    {
        private readonly Vk _vk;
        private readonly Device _device;
        private readonly PhysicalDevice _physicalDevice;
        private readonly PhysicalDeviceMemoryProperties _memProps;
        private readonly object _lock = new();
        private readonly List<MemoryBlock>[] _blockPools;
        private const ulong DefaultBlockSize = 256 * 1024 * 1024; // 256 MB
        private const ulong DedicatedThreshold = 4 * 1024 * 1024; // 4 MB
        private bool _disposed;

        public VkMemoryAllocator(Vk vk, Device device, PhysicalDevice physicalDevice)
        {
            _vk = vk;
            _device = device;
            _physicalDevice = physicalDevice;
            _vk.GetPhysicalDeviceMemoryProperties(_physicalDevice, out _memProps);
            _blockPools = new List<MemoryBlock>[_memProps.MemoryTypeCount];
            for (int i = 0; i < _blockPools.Length; i++)
                _blockPools[i] = new List<MemoryBlock>();
        }

        public VkAllocation Allocate(MemoryRequirements reqs, MemoryPropertyFlags preferredFlags)
        {
            lock (_lock)
            {
                uint memoryTypeIndex = VkHelper.FindMemoryType(_memProps, reqs.MemoryTypeBits, preferredFlags);

                if (reqs.Size >= DedicatedThreshold)
                    return AllocateDedicated(reqs, memoryTypeIndex);

                return AllocateFromBlock(reqs, memoryTypeIndex);
            }
        }

        public VkAllocation AllocateForBuffer(Buffer buffer, MemoryPropertyFlags preferredFlags)
        {
            _vk.GetBufferMemoryRequirements(_device, buffer, out MemoryRequirements reqs);
            VkAllocation alloc = Allocate(reqs, preferredFlags);
            VkHelper.Check(_vk.BindBufferMemory(_device, buffer, alloc.Memory, alloc.Offset),
                "Failed to bind buffer memory");
            return alloc;
        }

        public VkAllocation AllocateForImage(Image image, MemoryPropertyFlags preferredFlags)
        {
            _vk.GetImageMemoryRequirements(_device, image, out MemoryRequirements reqs);
            VkAllocation alloc = Allocate(reqs, preferredFlags);
            VkHelper.Check(_vk.BindImageMemory(_device, image, alloc.Memory, alloc.Offset),
                "Failed to bind image memory");
            return alloc;
        }

        private VkAllocation AllocateDedicated(MemoryRequirements reqs, uint memoryTypeIndex)
        {
            MemoryAllocateInfo allocInfo = new()
            {
                SType = StructureType.MemoryAllocateInfo,
                AllocationSize = reqs.Size,
                MemoryTypeIndex = memoryTypeIndex
            };

            VkHelper.Check(_vk.AllocateMemory(_device, &allocInfo, null, out DeviceMemory memory),
                "Failed to allocate dedicated device memory");

            void* mappedPtr = null;
            MemoryPropertyFlags typeFlags = _memProps.MemoryTypes[(int)memoryTypeIndex].PropertyFlags;
            if ((typeFlags & MemoryPropertyFlags.HostVisibleBit) != 0)
            {
                VkHelper.Check(_vk.MapMemory(_device, memory, 0, reqs.Size, 0, &mappedPtr),
                    "Failed to map dedicated memory");
            }

            return new VkAllocation
            {
                Memory = memory,
                Offset = 0,
                Size = reqs.Size,
                MemoryTypeIndex = memoryTypeIndex,
                IsDedicated = true,
                MappedPtr = mappedPtr
            };
        }

        private VkAllocation AllocateFromBlock(MemoryRequirements reqs, uint memoryTypeIndex)
        {
            foreach (var block in _blockPools[memoryTypeIndex])
            {
                if (block.TryAllocate(reqs, out VkAllocation alloc))
                    return alloc;
            }

            ulong blockSize = Math.Max(DefaultBlockSize, reqs.Size * 4);
            var newBlock = new MemoryBlock(_vk, _device, memoryTypeIndex, blockSize, _memProps);
            _blockPools[memoryTypeIndex].Add(newBlock);

            if (!newBlock.TryAllocate(reqs, out VkAllocation alloc2))
                throw new InvalidOperationException("Failed to allocate from new memory block");

            return alloc2;
        }

        public void Free(VkAllocation alloc)
        {
            lock (_lock)
            {
                if (alloc.IsDedicated)
                {
                    if (alloc.MappedPtr != null)
                        _vk.UnmapMemory(_device, alloc.Memory);
                    _vk.FreeMemory(_device, alloc.Memory, null);
                    return;
                }

                foreach (var block in _blockPools[alloc.MemoryTypeIndex])
                {
                    if (block.Memory.Handle == alloc.Memory.Handle)
                    {
                        block.Free(alloc);
                        return;
                    }
                }
            }
        }

        public void* Map(VkAllocation alloc)
        {
            if (alloc.MappedPtr != null)
                return (byte*)alloc.MappedPtr + alloc.Offset;

            void* ptr = null;
            VkHelper.Check(_vk.MapMemory(_device, alloc.Memory, alloc.Offset, alloc.Size, 0, &ptr),
                "Failed to map memory");
            return ptr;
        }

        public void Unmap(VkAllocation alloc)
        {
            if (alloc.MappedPtr == null)
                _vk.UnmapMemory(_device, alloc.Memory);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            lock (_lock)
            {
                for (int i = 0; i < _blockPools.Length; i++)
                {
                    foreach (var block in _blockPools[i])
                        block.Dispose();
                    _blockPools[i].Clear();
                }
            }
        }
    }

    internal sealed unsafe class MemoryBlock : IDisposable
    {
        private readonly Vk _vk;
        private readonly Device _device;
        private readonly List<FreeRegion> _freeRegions = new();
        private readonly object _lock = new();
        public DeviceMemory Memory { get; }
        public ulong Size { get; }
        public uint MemoryTypeIndex { get; }
        public void* MappedPtr { get; }
        private bool _disposed;

        public MemoryBlock(Vk vk, Device device, uint memoryTypeIndex, ulong size,
            PhysicalDeviceMemoryProperties memProps)
        {
            _vk = vk;
            _device = device;
            MemoryTypeIndex = memoryTypeIndex;
            Size = size;

            MemoryAllocateInfo allocInfo = new()
            {
                SType = StructureType.MemoryAllocateInfo,
                AllocationSize = size,
                MemoryTypeIndex = memoryTypeIndex
            };

            VkHelper.Check(_vk.AllocateMemory(_device, &allocInfo, null, out DeviceMemory memory),
                "Failed to allocate memory block");
            Memory = memory;

            MemoryPropertyFlags typeFlags = memProps.MemoryTypes[(int)memoryTypeIndex].PropertyFlags;
            if ((typeFlags & MemoryPropertyFlags.HostVisibleBit) != 0)
            {
                void* ptr = null;
                VkHelper.Check(_vk.MapMemory(_device, Memory, 0, Size, 0, &ptr),
                    "Failed to map memory block");
                MappedPtr = ptr;
            }

            _freeRegions.Add(new FreeRegion { Offset = 0, Size = Size });
        }

        public bool TryAllocate(MemoryRequirements reqs, out VkAllocation alloc)
        {
            lock (_lock)
            {
                int bestIdx = -1;
                ulong bestSize = ulong.MaxValue;

                for (int i = 0; i < _freeRegions.Count; i++)
                {
                    ulong alignedOffset = AlignUp(_freeRegions[i].Offset, reqs.Alignment);
                    ulong paddingAmount = alignedOffset - _freeRegions[i].Offset;
                    ulong requiredSize = paddingAmount + reqs.Size;

                    if (requiredSize <= _freeRegions[i].Size && _freeRegions[i].Size < bestSize)
                    {
                        bestIdx = i;
                        bestSize = _freeRegions[i].Size;
                    }
                }

                if (bestIdx < 0)
                {
                    alloc = default;
                    return false;
                }

                FreeRegion region = _freeRegions[bestIdx];
                ulong allocOffset = AlignUp(region.Offset, reqs.Alignment);
                ulong padding = allocOffset - region.Offset;
                ulong remainingSize = region.Size - padding - reqs.Size;

                _freeRegions.RemoveAt(bestIdx);

                if (padding > 0)
                    InsertFreeRegion(region.Offset, padding);
                if (remainingSize > 0)
                    InsertFreeRegion(allocOffset + reqs.Size, remainingSize);

                alloc = new VkAllocation
                {
                    Memory = Memory,
                    Offset = allocOffset,
                    Size = reqs.Size,
                    MemoryTypeIndex = MemoryTypeIndex,
                    IsDedicated = false,
                    MappedPtr = MappedPtr
                };
                return true;
            }
        }

        public void Free(VkAllocation alloc)
        {
            lock (_lock)
            {
                InsertFreeRegion(alloc.Offset, alloc.Size);
                MergeFreeRegions();
            }
        }

        private void InsertFreeRegion(ulong offset, ulong size)
        {
            int i = 0;
            while (i < _freeRegions.Count && _freeRegions[i].Offset < offset)
                i++;
            _freeRegions.Insert(i, new FreeRegion { Offset = offset, Size = size });
        }

        private void MergeFreeRegions()
        {
            for (int i = 0; i < _freeRegions.Count - 1; i++)
            {
                if (_freeRegions[i].Offset + _freeRegions[i].Size == _freeRegions[i + 1].Offset)
                {
                    _freeRegions[i] = new FreeRegion
                    {
                        Offset = _freeRegions[i].Offset,
                        Size = _freeRegions[i].Size + _freeRegions[i + 1].Size
                    };
                    _freeRegions.RemoveAt(i + 1);
                    i--;
                }
            }
        }

        private static ulong AlignUp(ulong value, ulong alignment)
        {
            return (value + alignment - 1) & ~(alignment - 1);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (MappedPtr != null)
                _vk.UnmapMemory(_device, Memory);
            _vk.FreeMemory(_device, Memory, null);
        }

        private struct FreeRegion
        {
            public ulong Offset;
            public ulong Size;
        }
    }

    // ════════════════════════════════════════════════════════════════
    // Vulkan Buffer
    // ════════════════════════════════════════════════════════════════

    public sealed unsafe class VulkanBuffer : RHIBuffer
    {
        internal readonly VulkanDevice Device;
        internal Buffer VkBuffer;
        internal VkAllocation Allocation;
        internal BufferUsageFlags UsageFlags;
        private ulong _gpuVA;
        private bool _disposed;
        private string _name = "";
        private RHIResourceState _currentState = RHIResourceState.Common;

        public VulkanBuffer(VulkanDevice device, RHIBufferDesc desc)
        {
            Device = device;
            BufferDesc = desc;

            UsageFlags = BufferUsageFlags.TransferSrcBit | BufferUsageFlags.TransferDstBit;

            if (desc.IsVertexBuffer) UsageFlags |= BufferUsageFlags.VertexBufferBit;
            if (desc.IsIndexBuffer) UsageFlags |= BufferUsageFlags.IndexBufferBit;
            if (desc.IsConstantBuffer) UsageFlags |= BufferUsageFlags.UniformBufferBit;
            if (desc.IsStructured || (desc.Flags & RHIBufferFlags.ShaderResource) != 0) UsageFlags |= BufferUsageFlags.StorageBufferBit;
            if (desc.IsUnorderedAccess) UsageFlags |= BufferUsageFlags.StorageBufferBit | BufferUsageFlags.StorageTexelBufferBit;
            if (desc.IsIndirectArgs) UsageFlags |= BufferUsageFlags.IndirectBufferBit;
            if ((desc.Flags & RHIBufferFlags.AccelerationStructure) != 0)
                UsageFlags |= BufferUsageFlags.AccelerationStructureBuildInputReadOnlyBitKhr | BufferUsageFlags.ShaderDeviceAddressBit;
            if (desc.HeapType == RHIResourceHeapType.Default)
                UsageFlags |= BufferUsageFlags.ShaderDeviceAddressBit;

            BufferCreateInfo createInfo = new()
            {
                SType = StructureType.BufferCreateInfo,
                Size = desc.SizeInBytes,
                Usage = UsageFlags,
                SharingMode = SharingMode.Exclusive
            };

            VkHelper.Check(device.Vk.CreateBuffer(device.VkDevice, &createInfo, null, out VkBuffer),
                "Failed to create Vulkan buffer");

            MemoryPropertyFlags memFlags = desc.HeapType switch
            {
                RHIResourceHeapType.Upload => MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                RHIResourceHeapType.Readback => MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit | MemoryPropertyFlags.HostCachedBit,
                _ => MemoryPropertyFlags.DeviceLocalBit
            };

            Allocation = device.Allocator.AllocateForBuffer(VkBuffer, memFlags);

            if ((UsageFlags & BufferUsageFlags.ShaderDeviceAddressBit) != 0)
            {
                BufferDeviceAddressInfo addrInfo = new()
                {
                    SType = StructureType.BufferDeviceAddressInfo,
                    Buffer = VkBuffer
                };
                _gpuVA = device.Vk.GetBufferDeviceAddress(device.VkDevice, &addrInfo);
            }

            if (desc.HeapType == RHIResourceHeapType.Upload || desc.HeapType == RHIResourceHeapType.Readback)
            {
                _ = device.Allocator.Map(Allocation);
            }
        }

        public override ulong GpuVirtualAddress => _gpuVA;
        public override RHIBufferDesc? BufferDesc { get; }
        public override RHITextureDesc? TextureDesc => null;
        public override RHIResourceState CurrentState { get => _currentState; set => _currentState = value; }
        public override string Name { get => _name; set => _name = value; }
        public override ulong AllocatedBytes => Allocation.Size;

        public void* GetMappedPointer()
        {
            if (Allocation.MappedPtr == null)
                return Device.Allocator.Map(Allocation);
            return (byte*)Allocation.MappedPtr + Allocation.Offset;
        }

        public override void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (VkBuffer.Handle != 0)
                Device.Vk.DestroyBuffer(Device.VkDevice, VkBuffer, null);
            Device.Allocator.Free(Allocation);
            VkBuffer = default;
        }
    }

    // ════════════════════════════════════════════════════════════════
    // Vulkan Texture
    // ════════════════════════════════════════════════════════════════

    public sealed unsafe class VulkanTexture : RHITexture
    {
        internal readonly VulkanDevice Device;
        internal Image VkImage;
        internal ImageView VkView;
        internal VkAllocation Allocation;
        internal ImageLayout CurrentLayout;
        internal bool IsSwapchainImage;
        private bool _disposed;
        private string _name = "";
        private RHIResourceState _currentState = RHIResourceState.Common;

        public VulkanTexture(VulkanDevice device, RHITextureDesc desc)
        {
            Device = device;
            TextureDesc = desc;
            IsSwapchainImage = false;

            ImageType imageType = desc.Dimension switch
            {
                RHIResourceDimension.Texture1D => ImageType.Type1D,
                RHIResourceDimension.Texture2D => ImageType.Type2D,
                RHIResourceDimension.Texture3D => ImageType.Type3D,
                _ => ImageType.Type2D
            };

            ImageUsageFlags usage = ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit;

            if ((desc.Flags & RHITextureFlags.RenderTarget) != 0)
                usage |= ImageUsageFlags.ColorAttachmentBit;
            if ((desc.Flags & RHITextureFlags.DepthStencil) != 0)
                usage |= ImageUsageFlags.DepthStencilAttachmentBit;
            if ((desc.Flags & RHITextureFlags.ShaderResource) != 0)
                usage |= ImageUsageFlags.SampledBit;
            if ((desc.Flags & RHITextureFlags.UnorderedAccess) != 0)
                usage |= ImageUsageFlags.StorageBit;

            Format vkFormat = VkFormatMapper.ToVkFormat(desc.Format);

            ImageCreateInfo createInfo = new()
            {
                SType = StructureType.ImageCreateInfo,
                ImageType = imageType,
                Format = vkFormat,
                Extent = new Extent3D((uint)desc.Width, (uint)desc.Height, (uint)(desc.Dimension == RHIResourceDimension.Texture3D ? desc.DepthOrArraySize : 1)),
                MipLevels = (uint)desc.MipLevels,
                ArrayLayers = (uint)(desc.Dimension == RHIResourceDimension.Texture3D ? 1 : desc.DepthOrArraySize),
                Samples = VkFormatMapper.ToVkSampleCount(desc.SampleCount),
                Tiling = ImageTiling.Optimal,
                Usage = usage,
                SharingMode = SharingMode.Exclusive,
                InitialLayout = ImageLayout.Undefined
            };

            CurrentLayout = ImageLayout.Undefined;

            VkHelper.Check(device.Vk.CreateImage(device.VkDevice, &createInfo, null, out VkImage),
                "Failed to create Vulkan image");

            Allocation = device.Allocator.AllocateForImage(VkImage, MemoryPropertyFlags.DeviceLocalBit);

            CreateDefaultView(desc, vkFormat);
        }

        // Constructor for wrapping swapchain images (no memory allocation)
        internal VulkanTexture(VulkanDevice device, Image existingImage, RHITextureDesc desc, Format vkFormat)
        {
            Device = device;
            TextureDesc = desc;
            VkImage = existingImage;
            IsSwapchainImage = true;
            Allocation = default;
            CurrentLayout = ImageLayout.Undefined;

            CreateDefaultView(desc, vkFormat);
        }

        private void CreateDefaultView(RHITextureDesc desc, Format vkFormat)
        {
            ImageViewType viewType = desc.Dimension switch
            {
                RHIResourceDimension.Texture1D when desc.DepthOrArraySize > 1 => ImageViewType.Type1DArray,
                RHIResourceDimension.Texture1D => ImageViewType.Type1D,
                RHIResourceDimension.Texture2D when (desc.Flags & RHITextureFlags.Cube) != 0 => ImageViewType.Cube,
                RHIResourceDimension.Texture2D when desc.DepthOrArraySize > 1 => ImageViewType.Type2DArray,
                RHIResourceDimension.Texture2D => ImageViewType.Type2D,
                RHIResourceDimension.Texture3D => ImageViewType.Type3D,
                _ => ImageViewType.Type2D
            };

            uint layerCount = viewType switch
            {
                ImageViewType.Cube => 6,
                ImageViewType.CubeArray => (uint)desc.DepthOrArraySize,
                ImageViewType.Type1DArray or ImageViewType.Type2DArray => (uint)desc.DepthOrArraySize,
                _ => 1
            };

            ImageSubresourceRange subresourceRange = new()
            {
                AspectMask = VkFormatMapper.GetAspectMask(vkFormat),
                BaseMipLevel = 0,
                LevelCount = (uint)desc.MipLevels,
                BaseArrayLayer = 0,
                LayerCount = layerCount
            };

            ImageViewCreateInfo viewInfo = new()
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = VkImage,
                ViewType = viewType,
                Format = vkFormat,
                SubresourceRange = subresourceRange
            };

            VkHelper.Check(Device.Vk.CreateImageView(Device.VkDevice, &viewInfo, null, out VkView),
                "Failed to create image view");
        }

        public ImageView CreateView(uint baseMip, uint levelCount, uint baseLayer, uint layerCount)
        {
            Format fmt = VkFormatMapper.ToVkFormat(TextureDesc!.Value.Format);

            ImageViewCreateInfo viewInfo = new()
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = VkImage,
                ViewType = ImageViewType.Type2D,
                Format = fmt,
                SubresourceRange = new ImageSubresourceRange
                {
                    AspectMask = VkFormatMapper.GetAspectMask(fmt),
                    BaseMipLevel = baseMip,
                    LevelCount = levelCount,
                    BaseArrayLayer = baseLayer,
                    LayerCount = layerCount
                }
            };

            VkHelper.Check(Device.Vk.CreateImageView(Device.VkDevice, &viewInfo, null, out ImageView view),
                "Failed to create image view");
            return view;
        }

        public override ulong GpuVirtualAddress => 0;
        public override RHIBufferDesc? BufferDesc => null;
        public override RHITextureDesc? TextureDesc { get; }
        public override RHIResourceState CurrentState { get => _currentState; set => _currentState = value; }
        public override string Name { get => _name; set => _name = value; }
        public override ulong AllocatedBytes => Allocation.Size;

        public override void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (VkView.Handle != 0)
                Device.Vk.DestroyImageView(Device.VkDevice, VkView, null);

            if (!IsSwapchainImage)
            {
                if (VkImage.Handle != 0)
                    Device.Vk.DestroyImage(Device.VkDevice, VkImage, null);
                Device.Allocator.Free(Allocation);
            }

            VkImage = default;
            VkView = default;
        }
    }

    // ════════════════════════════════════════════════════════════════
    // Vulkan Semaphore (timeline semaphore for sync)
    // ════════════════════════════════════════════════════════════════

    internal sealed unsafe class VkSemaphore : IDisposable
    {
        public Semaphore Handle;
        private readonly VulkanDevice _device;
        private bool _disposed;

        public VkSemaphore(VulkanDevice device, ulong initialValue = 0)
        {
            _device = device;

            SemaphoreTypeCreateInfo timelineInfo = new()
            {
                SType = StructureType.SemaphoreTypeCreateInfo,
                SemaphoreType = SemaphoreType.Timeline,
                InitialValue = initialValue
            };

            SemaphoreCreateInfo createInfo = new()
            {
                SType = StructureType.SemaphoreCreateInfo,
                PNext = &timelineInfo
            };

            VkHelper.Check(device.Vk.CreateSemaphore(device.VkDevice, &createInfo, null, out Handle),
                "Failed to create timeline semaphore");
        }

        public ulong GetValue()
        {
            VkHelper.Check(_device.Vk.GetSemaphoreCounterValue(_device.VkDevice, Handle, out ulong value),
                "Failed to get semaphore counter value");
            return value;
        }

        public void Signal(ulong value)
        {
            SemaphoreSignalInfo signalInfo = new()
            {
                SType = StructureType.SemaphoreSignalInfo,
                Semaphore = Handle,
                Value = value
            };
            VkHelper.Check(_device.Vk.SignalSemaphore(_device.VkDevice, &signalInfo),
                "Failed to signal semaphore");
        }

        public void Wait(ulong value, ulong timeoutNs = ulong.MaxValue)
        {
            Semaphore handle = Handle;
            SemaphoreWaitInfo waitInfo = new()
            {
                SType = StructureType.SemaphoreWaitInfo,
                Flags = SemaphoreWaitFlags.AnyBit,
                SemaphoreCount = 1,
                PSemaphores = &handle,
                PValues = &value
            };
            _device.Vk.WaitSemaphores(_device.VkDevice, &waitInfo, timeoutNs);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (Handle.Handle != 0)
                _device.Vk.DestroySemaphore(_device.VkDevice, Handle, null);
        }
    }
}
