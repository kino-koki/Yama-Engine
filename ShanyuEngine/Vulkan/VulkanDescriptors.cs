using Vulkan = Silk.NET.Vulkan;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using Semaphore = Silk.NET.Vulkan.Semaphore;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace ShanyuEngine.RHI.Vulkan
{
    // ════════════════════════════════════════════════════════════════
    // Descriptor Handle Encoding
    // Encodes descriptor type and index/handle into RHIDescriptorHandle
    // ════════════════════════════════════════════════════════════════

    internal static class VkDescriptorHandleEncoder
    {
        // High bits of CpuHandle encode the descriptor type
        private const ulong TypeMask = 0xF000_0000_0000_0000;
        private const ulong IndexMask = 0x0FFF_FFFF_FFFF_FFFF;

        public const ulong TypeShift = 60;

        public enum HandleType : ulong
        {
            None = 0,
            CbvSrvUav = 1, // Bindless descriptor index
            Sampler = 2,   // Sampler descriptor index
            Rtv = 3,       // VkImageView handle stored directly
            Dsv = 4        // VkImageView handle stored directly
        }

        public static RHIDescriptorHandle EncodeIndex(HandleType type, int index)
        {
            return new RHIDescriptorHandle
            {
                CpuHandle = ((ulong)type << (int)TypeShift) | (uint)index,
                GpuHandle = (ulong)type,
                Index = index
            };
        }

        public static RHIDescriptorHandle EncodeImageView(HandleType type, ImageView view)
        {
            return new RHIDescriptorHandle
            {
                CpuHandle = ((ulong)type << (int)TypeShift) | (ulong)view.Handle,
                GpuHandle = (ulong)type,
                Index = -1
            };
        }

        public static HandleType GetHandleType(RHIDescriptorHandle handle)
        {
            return (HandleType)((handle.CpuHandle & TypeMask) >> (int)TypeShift);
        }

        public static int GetDescriptorIndex(RHIDescriptorHandle handle)
        {
            return (int)(handle.CpuHandle & IndexMask);
        }

        public static ImageView GetImageView(RHIDescriptorHandle handle)
        {
            return new ImageView((nint)(handle.CpuHandle & IndexMask));
        }
    }

    // ════════════════════════════════════════════════════════════════
    // Vulkan Descriptor Heap
    // Implements RHIDescriptorHeap for CBV/SRV/UAV and Sampler types
    // ════════════════════════════════════════════════════════════════

    public sealed unsafe class VulkanDescriptorHeap : RHIDescriptorHeap
    {
        internal readonly VulkanDevice Device;
        internal DescriptorPool VkPool;
        internal DescriptorSet VkSet;
        internal DescriptorSetLayout SetLayout;
        private readonly int _capacity;
        private readonly RHIDescriptorHeapType _type;
        private readonly Stack<int> _freeIndices = new();
        private readonly HashSet<int> _usedIndices = new();
        private bool _disposed;

        public VulkanDescriptorHeap(VulkanDevice device, RHIDescriptorHeapType type, int capacity, bool shaderVisible)
        {
            Device = device;
            _type = type;
            _capacity = capacity;
            Type = type;
            Capacity = capacity;

            for (int i = capacity - 1; i >= 0; i--)
                _freeIndices.Push(i);

            CreateDescriptorPoolAndSet();
        }

        private void CreateDescriptorPoolAndSet()
        {
            DescriptorType descType = _type switch
            {
                RHIDescriptorHeapType.CBV_SRV_UAV => DescriptorType.StorageBuffer,
                RHIDescriptorHeapType.Sampler => DescriptorType.Sampler,
                _ => DescriptorType.StorageBuffer
            };

            DescriptorPoolSize poolSize = new()
            {
                Type = descType,
                DescriptorCount = (uint)_capacity
            };

            DescriptorPoolCreateInfo poolInfo = new()
            {
                SType = StructureType.DescriptorPoolCreateInfo,
                PoolSizeCount = 1,
                PPoolSizes = &poolSize,
                MaxSets = 1,
                Flags = DescriptorPoolCreateFlags.UpdateAfterBindBit
            };

            VkHelper.Check(Device.Vk.CreateDescriptorPool(Device.VkDevice, &poolInfo, null, out VkPool),
                "Failed to create descriptor pool");

            // Create descriptor set layout with bindless support
            DescriptorSetLayoutBinding binding = new()
            {
                Binding = 0,
                DescriptorType = descType,
                DescriptorCount = (uint)_capacity,
                StageFlags = ShaderStageFlags.All,
            };

            DescriptorBindingFlags bindingFlags =
                DescriptorBindingFlags.UpdateAfterBindBit |
                DescriptorBindingFlags.PartiallyBoundBit |
                DescriptorBindingFlags.UpdateUnusedWhilePendingBit;

            DescriptorSetLayoutBindingFlagsCreateInfo bindingFlagsInfo = new()
            {
                SType = StructureType.DescriptorSetLayoutBindingFlagsCreateInfo,
                BindingCount = 1,
                PBindingFlags = &bindingFlags
            };

            DescriptorSetLayoutCreateInfo layoutInfo = new()
            {
                SType = StructureType.DescriptorSetLayoutCreateInfo,
                BindingCount = 1,
                PBindings = &binding,
                PNext = &bindingFlagsInfo,
                Flags = DescriptorSetLayoutCreateFlags.UpdateAfterBindPoolBit
            };

            VkHelper.Check(Device.Vk.CreateDescriptorSetLayout(Device.VkDevice, &layoutInfo, null, out SetLayout),
                "Failed to create descriptor set layout");

            DescriptorSetAllocateInfo allocInfo = new()
            {
                SType = StructureType.DescriptorSetAllocateInfo,
                DescriptorPool = VkPool,
                DescriptorSetCount = 1,
                PSetLayouts = &SetLayout
            };

            VkHelper.Check(Device.Vk.AllocateDescriptorSets(Device.VkDevice, &allocInfo, out VkSet),
                "Failed to allocate descriptor set");

            StartHandle = VkDescriptorHandleEncoder.EncodeIndex(
                _type == RHIDescriptorHeapType.Sampler
                    ? VkDescriptorHandleEncoder.HandleType.Sampler
                    : VkDescriptorHandleEncoder.HandleType.CbvSrvUav,
                0);
            DescriptorSize = 0; // Not used in the same way as D3D12
        }

        public override RHIDescriptorHandle Allocate()
        {
            int index = _freeIndices.Count > 0 ? _freeIndices.Pop() : -1;
            if (index < 0)
                throw new InvalidOperationException("Descriptor heap exhausted");

            _usedIndices.Add(index);

            var type = _type == RHIDescriptorHeapType.Sampler
                ? VkDescriptorHandleEncoder.HandleType.Sampler
                : VkDescriptorHandleEncoder.HandleType.CbvSrvUav;

            return VkDescriptorHandleEncoder.EncodeIndex(type, index);
        }

        public override void Free(RHIDescriptorHandle handle)
        {
            int index = VkDescriptorHandleEncoder.GetDescriptorIndex(handle);
            if (_usedIndices.Remove(index))
                _freeIndices.Push(index);
        }

        public override void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (VkPool.Handle != 0)
            {
                Device.Vk.DestroyDescriptorPool(Device.VkDevice, VkPool, null);
                VkPool = default;
            }
            if (SetLayout.Handle != 0)
            {
                Device.Vk.DestroyDescriptorSetLayout(Device.VkDevice, SetLayout, null);
                SetLayout = default;
            }
        }
    }

    // ════════════════════════════════════════════════════════════════
    // Sampler Cache
    // Caches VkSampler objects keyed by RHISamplerDesc
    // ════════════════════════════════════════════════════════════════

    internal sealed unsafe class SamplerCache : IDisposable
    {
        private readonly VulkanDevice _device;
        private readonly Dictionary<string, Sampler> _cache = new();
        private readonly Dictionary<int, RHIDescriptorHandle> _samplerHandles = new();
        private readonly Dictionary<int, Sampler> _samplerByIndex = new();
        private readonly VulkanDescriptorHeap _samplerHeap;
        private readonly object _lock = new();
        private bool _disposed;

        public SamplerCache(VulkanDevice device, int maxSamplers = 2048)
        {
            _device = device;
            _samplerHeap = new VulkanDescriptorHeap(device, RHIDescriptorHeapType.Sampler, maxSamplers, true);
        }

        public VulkanDescriptorHeap SamplerHeap => _samplerHeap;

        public Sampler GetSampler(int index)
        {
            lock (_lock)
            {
                return _samplerByIndex.TryGetValue(index, out Sampler s) ? s : default;
            }
        }

        public RHIDescriptorHandle GetOrCreateSampler(RHISamplerDesc desc)
        {
            string key = GetSamplerKey(desc);

            lock (_lock)
            {
                int hash = key.GetHashCode();
                if (_samplerHandles.TryGetValue(hash, out var existing))
                    return existing;

                Sampler sampler = CreateSampler(desc);
                _cache[key] = sampler;

                RHIDescriptorHandle handle = _samplerHeap.Allocate();
                int index = VkDescriptorHandleEncoder.GetDescriptorIndex(handle);
                _samplerByIndex[index] = sampler;
                WriteSamplerDescriptor(handle, sampler);
                _samplerHandles[hash] = handle;
                return handle;
            }
        }

        private Sampler CreateSampler(RHISamplerDesc desc)
        {
            Filter magFilter, minFilter;
            SamplerMipmapMode mipmapMode;
            bool anisotropyEnable = false;
            bool compareEnable = false;
            CompareOp compareOp = CompareOp.Never;
            float maxAnisotropy = 1.0f;

            switch (desc.Filter)
            {
                case RHIFilter.Point:
                    magFilter = Filter.Nearest; minFilter = Filter.Nearest; mipmapMode = SamplerMipmapMode.Nearest;
                    break;
                case RHIFilter.Linear:
                    magFilter = Filter.Linear; minFilter = Filter.Linear; mipmapMode = SamplerMipmapMode.Linear;
                    break;
                case RHIFilter.Anisotropic:
                    magFilter = Filter.Linear; minFilter = Filter.Linear; mipmapMode = SamplerMipmapMode.Linear;
                    anisotropyEnable = true; maxAnisotropy = desc.MaxAnisotropy;
                    break;
                case RHIFilter.ComparisonMinMagMipPoint:
                    magFilter = Filter.Nearest; minFilter = Filter.Nearest; mipmapMode = SamplerMipmapMode.Nearest;
                    compareEnable = true; compareOp = VkFormatMapper.ToVkCompareOp(desc.ComparisonFunc);
                    break;
                case RHIFilter.ComparisonMinMagMipLinear:
                    magFilter = Filter.Linear; minFilter = Filter.Linear; mipmapMode = SamplerMipmapMode.Linear;
                    compareEnable = true; compareOp = VkFormatMapper.ToVkCompareOp(desc.ComparisonFunc);
                    break;
                case RHIFilter.ComparisonAnisotropic:
                    magFilter = Filter.Linear; minFilter = Filter.Linear; mipmapMode = SamplerMipmapMode.Linear;
                    anisotropyEnable = true; maxAnisotropy = desc.MaxAnisotropy;
                    compareEnable = true; compareOp = VkFormatMapper.ToVkCompareOp(desc.ComparisonFunc);
                    break;
                default:
                    magFilter = Filter.Linear; minFilter = Filter.Linear; mipmapMode = SamplerMipmapMode.Linear;
                    break;
            }

            BorderColor borderColor = BorderColor.FloatTransparentBlack;
            if (desc.BorderColor == Vector4.Zero)
                borderColor = BorderColor.FloatTransparentBlack;
            else if (desc.BorderColor == Vector4.One)
                borderColor = BorderColor.FloatOpaqueWhite;
            else if (desc.BorderColor.X == 0 && desc.BorderColor.Y == 0 && desc.BorderColor.Z == 0 && desc.BorderColor.W == 1)
                borderColor = BorderColor.FloatOpaqueBlack;

            SamplerCreateInfo createInfo = new()
            {
                SType = StructureType.SamplerCreateInfo,
                MagFilter = magFilter,
                MinFilter = minFilter,
                MipmapMode = mipmapMode,
                AddressModeU = VkFormatMapper.ToVkAddressMode(desc.AddressU),
                AddressModeV = VkFormatMapper.ToVkAddressMode(desc.AddressV),
                AddressModeW = VkFormatMapper.ToVkAddressMode(desc.AddressW),
                MipLodBias = desc.MipLODBias,
                AnisotropyEnable = anisotropyEnable,
                MaxAnisotropy = maxAnisotropy,
                CompareEnable = compareEnable,
                CompareOp = compareOp,
                MinLod = desc.MinLOD,
                MaxLod = desc.MaxLOD,
                BorderColor = borderColor,
                UnnormalizedCoordinates = false
            };

            VkHelper.Check(_device.Vk.CreateSampler(_device.VkDevice, &createInfo, null, out Sampler sampler),
                "Failed to create sampler");
            return sampler;
        }

        private void WriteSamplerDescriptor(RHIDescriptorHandle handle, Sampler sampler)
        {
            int index = VkDescriptorHandleEncoder.GetDescriptorIndex(handle);

            DescriptorImageInfo imageInfo = new()
            {
                Sampler = sampler,
                ImageView = default,
                ImageLayout = ImageLayout.ShaderReadOnlyOptimal
            };

            WriteDescriptorSet write = new()
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = _samplerHeap.VkSet,
                DstBinding = 0,
                DstArrayElement = (uint)index,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.Sampler,
                PImageInfo = &imageInfo
            };

            _device.Vk.UpdateDescriptorSets(_device.VkDevice, 1, &write, 0, null);
        }

        private static string GetSamplerKey(RHISamplerDesc d)
        {
            return $"{d.Filter}_{d.AddressU}_{d.AddressV}_{d.AddressW}_{d.MipLODBias}_{d.MaxAnisotropy}_{d.ComparisonFunc}_{d.BorderColor.X}_{d.BorderColor.Y}_{d.BorderColor.Z}_{d.BorderColor.W}_{d.MinLOD}_{d.MaxLOD}";
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            foreach (var kvp in _cache)
            {
                if (kvp.Value.Handle != 0)
                    _device.Vk.DestroySampler(_device.VkDevice, kvp.Value, null);
            }
            _cache.Clear();
            _samplerHeap.Dispose();
        }
    }

    // ════════════════════════════════════════════════════════════════
    // Descriptor Manager
    // Manages bindless descriptor pools, RTV/DSV views, and descriptor writes
    // ════════════════════════════════════════════════════════════════

    internal sealed unsafe class VulkanDescriptorManager : IDisposable
    {
        internal readonly VulkanDevice Device;
        internal VulkanDescriptorHeap CbvSrvUavHeap;
        internal SamplerCache SamplerCache;
        internal DescriptorSetLayout BindlessSetLayout;
        internal DescriptorSetLayout SamplerSetLayout;

        // RTV/DSV storage - maps handle to ImageView
        private readonly Dictionary<int, ImageView> _rtvViews = new();
        private readonly Dictionary<int, ImageView> _dsvViews = new();
        private readonly Dictionary<int, VulkanTexture> _rtvTextures = new();
        private readonly Dictionary<int, VulkanTexture> _dsvTextures = new();
        private int _nextRtvId = 1;
        private int _nextDsvId = 1;
        private readonly object _lock = new();
        private bool _disposed;

        // Descriptor update templates
        private readonly Dictionary<ulong, DescriptorUpdateTemplate> _updateTemplates = new();

        public VulkanDescriptorManager(VulkanDevice device, int maxResources = 1000000, int maxSamplers = 2048)
        {
            Device = device;

            CbvSrvUavHeap = new VulkanDescriptorHeap(device, RHIDescriptorHeapType.CBV_SRV_UAV, maxResources, true);
            SamplerCache = new SamplerCache(device, maxSamplers);

            BindlessSetLayout = CbvSrvUavHeap.SetLayout;
            SamplerSetLayout = SamplerCache.SamplerHeap.SetLayout;
        }

        public DescriptorSet BindlessSet => CbvSrvUavHeap.VkSet;
        public DescriptorSet SamplerSet => SamplerCache.SamplerHeap.VkSet;

        // ── CBV (Uniform Buffer) ──
        public RHIDescriptorHandle CreateCBV(VulkanBuffer buffer, RHICBVDesc? desc)
        {
            RHIDescriptorHandle handle = CbvSrvUavHeap.Allocate();
            int index = VkDescriptorHandleEncoder.GetDescriptorIndex(handle);

            ulong range = desc?.SizeInBytes ?? (uint)buffer.BufferDesc!.Value.SizeInBytes;
            ulong offset = desc?.BufferLocation ?? 0;
            if (offset == 0)
                offset = buffer.Allocation.Offset;

            DescriptorBufferInfo bufInfo = new()
            {
                Buffer = buffer.VkBuffer,
                Offset = offset,
                Range = range
            };

            WriteDescriptorSet write = new()
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = CbvSrvUavHeap.VkSet,
                DstBinding = 0,
                DstArrayElement = (uint)index,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.UniformBuffer,
                PBufferInfo = &bufInfo
            };

            Device.Vk.UpdateDescriptorSets(Device.VkDevice, 1, &write, 0, null);
            return handle;
        }

        // ── SRV (Shader Resource View) ──
        public RHIDescriptorHandle CreateSRV(RHIResource resource, RHISRVDesc? desc)
        {
            RHIDescriptorHandle handle = CbvSrvUavHeap.Allocate();
            int index = VkDescriptorHandleEncoder.GetDescriptorIndex(handle);

            if (resource is VulkanBuffer buf)
            {
                DescriptorBufferInfo bufInfo = new()
                {
                    Buffer = buf.VkBuffer,
                    Offset = desc?.FirstElement ?? 0,
                    Range = desc?.NumElements > 0 ? desc.Value.NumElements * (buf.BufferDesc?.Stride ?? 4) : buf.Allocation.Size
                };

                WriteDescriptorSet write = new()
                {
                    SType = StructureType.WriteDescriptorSet,
                    DstSet = CbvSrvUavHeap.VkSet,
                    DstBinding = 0,
                    DstArrayElement = (uint)index,
                    DescriptorCount = 1,
                    DescriptorType = DescriptorType.StorageBuffer,
                    PBufferInfo = &bufInfo
                };

                Device.Vk.UpdateDescriptorSets(Device.VkDevice, 1, &write, 0, null);
            }
            else if (resource is VulkanTexture tex)
            {
                DescriptorImageInfo imgInfo = new()
                {
                    Sampler = default,
                    ImageView = tex.VkView,
                    ImageLayout = ImageLayout.ShaderReadOnlyOptimal
                };

                WriteDescriptorSet write = new()
                {
                    SType = StructureType.WriteDescriptorSet,
                    DstSet = CbvSrvUavHeap.VkSet,
                    DstBinding = 0,
                    DstArrayElement = (uint)index,
                    DescriptorCount = 1,
                    DescriptorType = DescriptorType.SampledImage,
                    PImageInfo = &imgInfo
                };

                Device.Vk.UpdateDescriptorSets(Device.VkDevice, 1, &write, 0, null);
            }

            return handle;
        }

        // ── UAV (Unordered Access View) ──
        public RHIDescriptorHandle CreateUAV(RHIResource resource, RHIUAVDesc? desc)
        {
            RHIDescriptorHandle handle = CbvSrvUavHeap.Allocate();
            int index = VkDescriptorHandleEncoder.GetDescriptorIndex(handle);

            if (resource is VulkanBuffer buf)
            {
                DescriptorBufferInfo bufInfo = new()
                {
                    Buffer = buf.VkBuffer,
                    Offset = desc?.FirstElement ?? 0,
                    Range = desc?.NumElements > 0 ? desc.Value.NumElements * (buf.BufferDesc?.Stride ?? 4) : buf.Allocation.Size
                };

                WriteDescriptorSet write = new()
                {
                    SType = StructureType.WriteDescriptorSet,
                    DstSet = CbvSrvUavHeap.VkSet,
                    DstBinding = 0,
                    DstArrayElement = (uint)index,
                    DescriptorCount = 1,
                    DescriptorType = DescriptorType.StorageBuffer,
                    PBufferInfo = &bufInfo
                };

                Device.Vk.UpdateDescriptorSets(Device.VkDevice, 1, &write, 0, null);
            }
            else if (resource is VulkanTexture tex)
            {
                DescriptorImageInfo imgInfo = new()
                {
                    Sampler = default,
                    ImageView = tex.VkView,
                    ImageLayout = ImageLayout.General
                };

                WriteDescriptorSet write = new()
                {
                    SType = StructureType.WriteDescriptorSet,
                    DstSet = CbvSrvUavHeap.VkSet,
                    DstBinding = 0,
                    DstArrayElement = (uint)index,
                    DescriptorCount = 1,
                    DescriptorType = DescriptorType.StorageImage,
                    PImageInfo = &imgInfo
                };

                Device.Vk.UpdateDescriptorSets(Device.VkDevice, 1, &write, 0, null);
            }

            return handle;
        }

        // ── RTV (Render Target View) ──
        public RHIDescriptorHandle CreateRTV(VulkanTexture texture, int mipSlice, int arraySlice)
        {
            lock (_lock)
            {
                RHITextureDesc desc = texture.TextureDesc!.Value;
                Format fmt = VkFormatMapper.ToVkFormat(desc.Format);

                ImageSubresourceRange range = new()
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    BaseMipLevel = (uint)mipSlice,
                    LevelCount = 1,
                    BaseArrayLayer = (uint)arraySlice,
                    LayerCount = 1
                };

                ImageViewCreateInfo viewInfo = new()
                {
                    SType = StructureType.ImageViewCreateInfo,
                    Image = texture.VkImage,
                    ViewType = ImageViewType.Type2D,
                    Format = fmt,
                    Components = new ComponentMapping(
                        ComponentSwizzle.Identity, ComponentSwizzle.Identity,
                        ComponentSwizzle.Identity, ComponentSwizzle.Identity),
                    SubresourceRange = range
                };

                VkHelper.Check(Device.Vk.CreateImageView(Device.VkDevice, &viewInfo, null, out ImageView view),
                    "Failed to create RTV image view");

                int id = _nextRtvId++;
                _rtvViews[id] = view;
                _rtvTextures[id] = texture;

                return new RHIDescriptorHandle
                {
                    CpuHandle = ((ulong)VkDescriptorHandleEncoder.HandleType.Rtv << (int)VkDescriptorHandleEncoder.TypeShift) | (uint)id,
                    GpuHandle = (ulong)VkDescriptorHandleEncoder.HandleType.Rtv,
                    Index = id
                };
            }
        }

        // ── DSV (Depth Stencil View) ──
        public RHIDescriptorHandle CreateDSV(VulkanTexture texture, int mipSlice, int arraySlice)
        {
            lock (_lock)
            {
                RHITextureDesc desc = texture.TextureDesc!.Value;
                Format fmt = VkFormatMapper.ToVkFormat(desc.Format);

                ImageSubresourceRange range = new()
                {
                    AspectMask = VkFormatMapper.GetAspectMask(fmt),
                    BaseMipLevel = (uint)mipSlice,
                    LevelCount = 1,
                    BaseArrayLayer = (uint)arraySlice,
                    LayerCount = 1
                };

                ImageViewCreateInfo viewInfo = new()
                {
                    SType = StructureType.ImageViewCreateInfo,
                    Image = texture.VkImage,
                    ViewType = ImageViewType.Type2D,
                    Format = fmt,
                    SubresourceRange = range
                };

                VkHelper.Check(Device.Vk.CreateImageView(Device.VkDevice, &viewInfo, null, out ImageView view),
                    "Failed to create DSV image view");

                int id = _nextDsvId++;
                _dsvViews[id] = view;
                _dsvTextures[id] = texture;

                return new RHIDescriptorHandle
                {
                    CpuHandle = ((ulong)VkDescriptorHandleEncoder.HandleType.Dsv << (int)VkDescriptorHandleEncoder.TypeShift) | (uint)id,
                    GpuHandle = (ulong)VkDescriptorHandleEncoder.HandleType.Dsv,
                    Index = id
                };
            }
        }

        public ImageView GetRTV(RHIDescriptorHandle handle)
        {
            int id = VkDescriptorHandleEncoder.GetDescriptorIndex(handle);
            return _rtvViews.TryGetValue(id, out ImageView view) ? view : default;
        }

        public ImageView GetDSV(RHIDescriptorHandle handle)
        {
            int id = VkDescriptorHandleEncoder.GetDescriptorIndex(handle);
            return _dsvViews.TryGetValue(id, out ImageView view) ? view : default;
        }

        public VulkanTexture GetRTVTexture(RHIDescriptorHandle handle)
        {
            int id = VkDescriptorHandleEncoder.GetDescriptorIndex(handle);
            return _rtvTextures.TryGetValue(id, out VulkanTexture tex) ? tex : null;
        }

        public VulkanTexture GetDSVTexture(RHIDescriptorHandle handle)
        {
            int id = VkDescriptorHandleEncoder.GetDescriptorIndex(handle);
            return _dsvTextures.TryGetValue(id, out VulkanTexture tex) ? tex : null;
        }

        public void FreeRTV(RHIDescriptorHandle handle)
        {
            lock (_lock)
            {
                int id = VkDescriptorHandleEncoder.GetDescriptorIndex(handle);
                if (_rtvViews.TryGetValue(id, out ImageView view))
                {
                    Device.Vk.DestroyImageView(Device.VkDevice, view, null);
                    _rtvViews.Remove(id);
                }
            }
        }

        public void FreeDSV(RHIDescriptorHandle handle)
        {
            lock (_lock)
            {
                int id = VkDescriptorHandleEncoder.GetDescriptorIndex(handle);
                if (_dsvViews.TryGetValue(id, out ImageView view))
                {
                    Device.Vk.DestroyImageView(Device.VkDevice, view, null);
                    _dsvViews.Remove(id);
                }
            }
        }

        // ── Combined Image Sampler (for convenience) ──
        public RHIDescriptorHandle CreateCombinedImageSampler(VulkanTexture texture, RHISamplerDesc samplerDesc)
        {
            RHIDescriptorHandle samplerHandle = SamplerCache.GetOrCreateSampler(samplerDesc);
            int samplerIndex = VkDescriptorHandleEncoder.GetDescriptorIndex(samplerHandle);

            RHIDescriptorHandle handle = CbvSrvUavHeap.Allocate();
            int index = VkDescriptorHandleEncoder.GetDescriptorIndex(handle);

            DescriptorImageInfo imgInfo = new()
            {
                Sampler = SamplerCache.GetSampler(samplerIndex),
                ImageView = texture.VkView,
                ImageLayout = ImageLayout.ShaderReadOnlyOptimal
            };

            WriteDescriptorSet write = new()
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = CbvSrvUavHeap.VkSet,
                DstBinding = 0,
                DstArrayElement = (uint)index,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.CombinedImageSampler,
                PImageInfo = &imgInfo
            };

            Device.Vk.UpdateDescriptorSets(Device.VkDevice, 1, &write, 0, null);
            return handle;
        }

        // ── Descriptor Update Template ──
        public DescriptorUpdateTemplate CreateUpdateTemplate(
            DescriptorType descType,
            DescriptorSetLayout setLayout,
            PipelineLayout pipelineLayout,
            uint binding,
            uint count)
        {
            DescriptorUpdateTemplateEntry entry = new()
            {
                DstBinding = binding,
                DstArrayElement = 0,
                DescriptorCount = count,
                DescriptorType = descType,
                Offset = 0,
                Stride = (nuint)GetDescriptorInfoSize(descType)
            };

            DescriptorUpdateTemplateCreateInfo createInfo = new()
            {
                SType = StructureType.DescriptorUpdateTemplateCreateInfo,
                DescriptorUpdateEntryCount = 1,
                PDescriptorUpdateEntries = &entry,
                TemplateType = DescriptorUpdateTemplateType.DescriptorSet,
                DescriptorSetLayout = setLayout,
                PipelineLayout = pipelineLayout,
                PipelineBindPoint = PipelineBindPoint.Graphics
            };

            VkHelper.Check(Device.Vk.CreateDescriptorUpdateTemplate(Device.VkDevice, &createInfo, null,
                out DescriptorUpdateTemplate template),
                "Failed to create descriptor update template");
            return template;
        }

        private static int GetDescriptorInfoSize(DescriptorType type)
        {
            return type switch
            {
                DescriptorType.UniformBuffer or DescriptorType.StorageBuffer or
                DescriptorType.UniformBufferDynamic or DescriptorType.StorageBufferDynamic =>
                    sizeof(DescriptorBufferInfo),
                DescriptorType.Sampler or DescriptorType.SampledImage or
                DescriptorType.StorageImage or DescriptorType.CombinedImageSampler =>
                    sizeof(DescriptorImageInfo),
                DescriptorType.UniformTexelBuffer or DescriptorType.StorageTexelBuffer =>
                    sizeof(BufferView),
                _ => sizeof(DescriptorBufferInfo)
            };
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            foreach (var kvp in _updateTemplates)
            {
                if (kvp.Value.Handle != 0)
                    Device.Vk.DestroyDescriptorUpdateTemplate(Device.VkDevice, kvp.Value, null);
            }
            _updateTemplates.Clear();

            foreach (var kvp in _rtvViews)
                Device.Vk.DestroyImageView(Device.VkDevice, kvp.Value, null);
            _rtvViews.Clear();

            foreach (var kvp in _dsvViews)
                Device.Vk.DestroyImageView(Device.VkDevice, kvp.Value, null);
            _dsvViews.Clear();

            SamplerCache?.Dispose();
            CbvSrvUavHeap?.Dispose();
        }
    }

    // Extension to access sampler by index
    internal static class SamplerCacheExtensions
    {
        public static Sampler GetSampler(this SamplerCache cache, int index)
        {
            return cache.GetSampler(index);
        }
    }
}
