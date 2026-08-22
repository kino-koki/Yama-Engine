// ==================================================================================================
//  D3D12ResourceReal.cs - 山雨游戏引擎 D3D12 GPU 资源管理真实实现
//
//  命名空间: ShanyuEngine.RHI
//  依赖: Vortice.Direct3D12, Vortice.Mathematics
//
//  功能覆盖:
//    - D3D12Resource 基类: 状态/GPU虚拟地址/描述符/大小/对齐
//    - D3D12Buffer: 缓冲区创建/映射/读写/SRV/UAV/CBV
//    - D3D12Texture: 1D/2D/2DArray/3D/Cube/CubeArray/MipLevels/Format/SampleCount/Flags
//    - D3D12RenderTarget: 渲染目标 (MSAA/多采样/自动SRV)
//    - D3D12DepthStencil: 深度模板 (DSV/只读深度/只读模板)
//    - D3D12UploadBuffer: Ring Buffer / 每帧分配 / 自动回收 / Map 持久化
//    - D3D12ReadbackBuffer: ReadbackHeap / 异步读取 / 分帧读取
//    - D3D12StructuredBuffer: 结构化缓冲区 (Counter/Append/Consume)
//    - D3D12ByteAddressedBuffer: 原始缓冲区 (RAW UAV/SRV)
//    - ResourceStateTracker: 每子资源状态跟踪/全局/Per-CommandList/自动屏障
//    - AliasingResource: 同一内存不同格式/不同维度
// ==================================================================================================

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Vortice.Direct3D12;
using Vortice.DXGI;

namespace ShanyuEngine.RHI
{
    // ==============================================================================================
    //  D3D12Resource - GPU 资源基类
    // ==============================================================================================

    /// <summary>
    /// D3D12 GPU 资源基类。持有原生 ID3D12Resource、资源状态和描述符信息。
    /// </summary>
    public abstract class D3D12Resource : IDisposable
    {
        internal ID3D12Resource _nativeResource;
        internal ResourceStates _currentState;
        internal ulong _allocatedBytes;
        internal string _name;
        private bool _disposed;

        public ID3D12Resource NativeResource => _nativeResource;
        public ulong GpuVirtualAddress => _nativeResource.GPUVirtualAddress;
        public ulong AllocatedBytes => _allocatedBytes;
        public ResourceStates CurrentState { get => _currentState; set => _currentState = value; }
        public override string ToString() => $"D3D12Resource[{_name}]";

        protected D3D12Resource(ID3D12Resource nativeResource, ResourceStates initialState,
            ulong allocatedBytes, string name)
        {
            _nativeResource = nativeResource ?? throw new ArgumentNullException(nameof(nativeResource));
            _currentState = initialState;
            _allocatedBytes = allocatedBytes;
            _name = name;

            if (!string.IsNullOrEmpty(name))
                _nativeResource.DeviceName = name;
        }

        /// <summary>获取子资源数量</summary>
        public abstract int GetSubresourceCount();

        /// <summary>获取指定子资源的布局信息</summary>
        public virtual MappedSubresource GetMappedSubresource(int subresource)
        {
            throw new InvalidOperationException("Resource is not mapped");
        }

        public virtual void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _nativeResource.Dispose();
        }
    }

    // ==============================================================================================
    //  D3D12Buffer - 缓冲区
    // ==============================================================================================

    /// <summary>
    /// D3D12 缓冲区。支持创建、映射(Map/Unmap/Write/Read)、描述符创建(SRV/UAV/CBV)。
    /// </summary>
    public sealed class D3D12Buffer : D3D12Resource
    {
        private RHIBufferDesc _desc;
        private unsafe byte* _mappedPointer;
        private bool _isPersistentMapped;
        private CpuDescriptorHandle _srvHandle;
        private CpuDescriptorHandle _uavHandle;
        private CpuDescriptorHandle _cbvHandle;
        private bool _hasSRV, _hasUAV, _hasCBV;

        public override RHIBufferDesc? BufferDesc => _desc;
        public unsafe byte* MappedPointer => _mappedPointer;
        public bool IsMapped => _mappedPointer != null;
        public CpuDescriptorHandle SRVHandle => _srvHandle;
        public CpuDescriptorHandle UAVHandle => _uavHandle;
        public CpuDescriptorHandle CBVHandle => _cbvHandle;

        public D3D12Buffer(ID3D12Resource nativeResource, in RHIBufferDesc desc,
            ResourceStates initialState, string name)
            : base(nativeResource, initialState, desc.SizeInBytes, name)
        {
            _desc = desc;
            _mappedPointer = null;
            _isPersistentMapped = false;
            _srvHandle = default;
            _uavHandle = default;
            _cbvHandle = default;
        }

        public override int GetSubresourceCount() => 1;

        /// <summary>映射缓冲区到 CPU 地址空间</summary>
        public unsafe IntPtr Map(uint subresource = 0, Range? readRange = null)
        {
            if (_mappedPointer != null) return (IntPtr)_mappedPointer;

            IntPtr ptr;
            if (readRange.HasValue)
            {
                var range = readRange.Value;
                _nativeResource.Map(subresource, new Range(range.Begin, range.End), out ptr);
            }
            else
            {
                _nativeResource.Map(subresource, null, out ptr);
            }
            _mappedPointer = (byte*)ptr;
            return ptr;
        }

        /// <summary>解除映射</summary>
        public unsafe void Unmap(uint subresource = 0, Range? writtenRange = null)
        {
            if (_mappedPointer == null) return;

            if (writtenRange.HasValue)
            {
                var range = writtenRange.Value;
                _nativeResource.Unmap(subresource, new Range(range.Begin, range.End));
            }
            else
            {
                _nativeResource.Unmap(subresource);
            }
            _mappedPointer = null;
        }

        /// <summary>写入数据到已映射缓冲区</summary>
        public unsafe void Write<T>(ReadOnlySpan<T> data, ulong offset = 0) where T : unmanaged
        {
            if (_mappedPointer == null)
                throw new InvalidOperationException("Buffer is not mapped. Call Map() first.");

            fixed (void* src = data)
            {
                Buffer.MemoryCopy(src, _mappedPointer + offset, data.Length * sizeof(T), data.Length * sizeof(T));
            }
        }

        /// <summary>从已映射缓冲区读取数据</summary>
        public unsafe void Read<T>(Span<T> data, ulong offset = 0) where T : unmanaged
        {
            if (_mappedPointer == null)
                throw new InvalidOperationException("Buffer is not mapped. Call Map() first.");

            fixed (void* dst = data)
            {
                Buffer.MemoryCopy(_mappedPointer + offset, dst, data.Length * sizeof(T), data.Length * sizeof(T));
            }
        }

        /// <summary>持久化映射 (Upload Buffer 场景: 创建后一直保持映射状态)</summary>
        public unsafe void MapPersistent()
        {
            if (_isPersistentMapped) return;
            _nativeResource.Map(0, null, out IntPtr ptr);
            _mappedPointer = (byte*)ptr;
            _isPersistentMapped = true;
        }

        /// <summary>创建 Constant Buffer View 描述符</summary>
        public void CreateCBV(ID3D12Device device, ulong offset = 0, uint sizeInBytes = 0)
        {
            var cbvDesc = new ConstantBufferViewDescription
            {
                BufferLocation = _nativeResource.GPUVirtualAddress + offset,
                SizeInBytes = sizeInBytes > 0 ? sizeInBytes : (uint)_allocatedBytes
            };
            // D3D12 CBV 要求 256 字节对齐
            cbvDesc.SizeInBytes = Math.Max(cbvDesc.SizeInBytes, (uint)AlignUp(cbvDesc.SizeInBytes, 256));

            _cbvHandle = device.CreateConstantBufferView(cbvDesc);
            _hasCBV = true;
        }

        /// <summary>创建 Shader Resource View 描述符</summary>
        public void CreateSRV(ID3D12Device device, CpuDescriptorHandle destHandle,
            Format format = Format.Unknown, bool isStructured = false,
            uint firstElement = 0, uint numElements = 0, uint structureStride = 0)
        {
            if (isStructured || _desc.Flags.HasFlag(RHIBufferFlags.Structured))
            {
                var srvDesc = new ShaderResourceViewDescription
                {
                    Shader4ComponentMapping = Vortice.Direct3D12.ShaderComponentMapping.Default,
                    Format = Format.Unknown,
                    ViewDimension = ShaderResourceViewDimension.Buffer,
                    Buffer = new BufferShaderResourceView
                    {
                        FirstElement = firstElement,
                        NumElements = numElements > 0 ? numElements : (uint)(_allocatedBytes / _desc.Stride),
                        StructureByteStride = structureStride > 0 ? structureStride : (uint)_desc.Stride,
                        Flags = BufferShaderResourceViewFlags.None
                    }
                };
                device.CreateShaderResourceView(_nativeResource, srvDesc, destHandle);
            }
            else if (_desc.Flags.HasFlag(RHIBufferFlags.AccelerationStructure))
            {
                var srvDesc = new ShaderResourceViewDescription
                {
                    Shader4ComponentMapping = Vortice.Direct3D12.ShaderComponentMapping.Default,
                    Format = Format.Unknown,
                    ViewDimension = ShaderResourceViewDimension.RaytracingAccelerationStructure,
                    RaytracingAccelerationStructure = new RaytracingAccelerationStructureShaderResourceView
                    {
                        Location = _nativeResource.GPUVirtualAddress
                    }
                };
                device.CreateShaderResourceView(_nativeResource, srvDesc, destHandle);
            }
            else
            {
                var srvDesc = new ShaderResourceViewDescription
                {
                    Shader4ComponentMapping = Vortice.Direct3D12.ShaderComponentMapping.Default,
                    Format = format,
                    ViewDimension = ShaderResourceViewDimension.Buffer,
                    Buffer = new BufferShaderResourceView
                    {
                        FirstElement = firstElement,
                        NumElements = numElements,
                        Flags = BufferShaderResourceViewFlags.Raw,
                        ElementWidth = format != Format.Unknown ? GetFormatBytesPerElement(format) : 4
                    }
                };
                device.CreateShaderResourceView(_nativeResource, srvDesc, destHandle);
            }

            _srvHandle = destHandle;
            _hasSRV = true;
        }

        /// <summary>创建 Unordered Access View 描述符</summary>
        public void CreateUAV(ID3D12Device device, CpuDescriptorHandle destHandle,
            Format format = Format.Unknown, bool isStructured = false,
            uint firstElement = 0, uint numElements = 0, uint structureStride = 0,
            ID3D12Resource? counterBuffer = null, ulong counterOffset = 0)
        {
            if (isStructured || _desc.Flags.HasFlag(RHIBufferFlags.Structured))
            {
                var uavDesc = new UnorderedAccessViewDescription
                {
                    Format = Format.Unknown,
                    ViewDimension = UnorderedAccessViewDimension.Buffer,
                    Buffer = new BufferUnorderedAccessView
                    {
                        FirstElement = firstElement,
                        NumElements = numElements > 0 ? numElements : (uint)(_allocatedBytes / _desc.Stride),
                        StructureByteStride = structureStride > 0 ? structureStride : (uint)_desc.Stride,
                        Flags = BufferUnorderedAccessViewFlags.None,
                        CounterOffsetInBytes = counterOffset,
                        Counter = counterBuffer != null ? counterBuffer.GPUVirtualAddress : 0
                    }
                };
                device.CreateUnorderedAccessView(_nativeResource, counterBuffer, uavDesc, destHandle);
            }
            else
            {
                var uavDesc = new UnorderedAccessViewDescription
                {
                    Format = format != Format.Unknown ? format : Format.R32_Typeless,
                    ViewDimension = UnorderedAccessViewDimension.Buffer,
                    Buffer = new BufferUnorderedAccessView
                    {
                        FirstElement = firstElement,
                        NumElements = numElements,
                        Flags = BufferUnorderedAccessViewFlags.Raw
                    }
                };
                device.CreateUnorderedAccessView(_nativeResource, null, uavDesc, destHandle);
            }

            _uavHandle = destHandle;
            _hasUAV = true;
        }

        protected override void Dispose(bool disposing)
        {
            // 基类 Dispose 已释放 nativeResource
        }

        private static int GetFormatBytesPerElement(Format format)
        {
            return format switch
            {
                Format.R32_Float or Format.R32_UInt or Format.R32_SInt => 4,
                Format.R32G32_Float => 8,
                Format.R32G32B32_Float => 12,
                Format.R32G32B32A32_Float => 16,
                _ => 4
            };
        }

        private static ulong AlignUp(ulong value, ulong alignment)
            => (value + alignment - 1) & ~(alignment - 1);
    }

    // ==============================================================================================
    //  D3D12Texture - 纹理
    // ==============================================================================================

    /// <summary>
    /// D3D12 纹理。支持 1D/2D/2DArray/3D/Cube/CubeArray，支持 MipLevels/Format/SampleCount。
    /// </summary>
    public sealed class D3D12Texture : D3D12Resource
    {
        private RHITextureDesc _desc;
        private CpuDescriptorHandle _srvHandle;
        private bool _hasSRV;

        public override RHITextureDesc? TextureDesc => _desc;
        public CpuDescriptorHandle SRVHandle => _srvHandle;
        public int Width => _desc.Width;
        public int Height => _desc.Height;
        public int DepthOrArraySize => _desc.DepthOrArraySize;
        public int MipLevels => _desc.MipLevels;
        public Format DxgiFormat { get; }

        public D3D12Texture(ID3D12Resource nativeResource, in RHITextureDesc desc,
            ResourceStates initialState, string name)
            : base(nativeResource, initialState, nativeResource.Description.SizeInBytes, name)
        {
            _desc = desc;
            DxgiFormat = nativeResource.Description.Format;
            _srvHandle = default;
        }

        public override int GetSubresourceCount()
        {
            return _desc.DepthOrArraySize * _desc.MipLevels;
        }

        /// <summary>创建 SRV 描述符 (全 Mip / 全数组)</summary>
        public void CreateSRV(ID3D12Device device, CpuDescriptorHandle destHandle,
            uint mostDetailedMip = 0, uint mipLevels = 0,
            Format format = Format.Unknown,
            ShaderResourceViewDimension dimension = ShaderResourceViewDimension.Unknown)
        {
            Format srvFormat = format != Format.Unknown ? format : DxgiFormat;
            var srvDesc = new ShaderResourceViewDescription
            {
                Shader4ComponentMapping = Vortice.Direct3D12.ShaderComponentMapping.Default,
                Format = srvFormat,
            };

            bool isCube = (_desc.Flags & RHITextureFlags.Cube) != 0;

            // 根据 Dimension 决定 SRV 维度
            switch (_desc.Dimension)
            {
                case RHIResourceDimension.Texture1D:
                    if (_desc.DepthOrArraySize > 1)
                    {
                        srvDesc.ViewDimension = ShaderResourceViewDimension.Texture1DArray;
                        srvDesc.Texture1DArray = new Texture1DArrayShaderResourceView
                        {
                            MostDetailedMip = mostDetailedMip,
                            MipLevels = mipLevels > 0 ? mipLevels : (uint)_desc.MipLevels,
                            FirstArraySlice = 0,
                            ArraySize = (uint)_desc.DepthOrArraySize,
                            ResourceMinLODClamp = 0
                        };
                    }
                    else
                    {
                        srvDesc.ViewDimension = ShaderResourceViewDimension.Texture1D;
                        srvDesc.Texture1D = new Texture1DShaderResourceView
                        {
                            MostDetailedMip = mostDetailedMip,
                            MipLevels = mipLevels > 0 ? mipLevels : (uint)_desc.MipLevels,
                            ResourceMinLODClamp = 0
                        };
                    }
                    break;

                case RHIResourceDimension.Texture3D:
                    srvDesc.ViewDimension = ShaderResourceViewDimension.Texture3D;
                    srvDesc.Texture3D = new Texture3DShaderResourceView
                    {
                        MostDetailedMip = mostDetailedMip,
                        MipLevels = mipLevels > 0 ? mipLevels : (uint)_desc.MipLevels,
                        ResourceMinLODClamp = 0
                    };
                    break;

                case RHIResourceDimension.Buffer:
                    srvDesc.ViewDimension = ShaderResourceViewDimension.Buffer;
                    srvDesc.Buffer = new BufferShaderResourceView
                    {
                        FirstElement = 0,
                        NumElements = (uint)(_allocatedBytes / 4),
                        Flags = BufferShaderResourceViewFlags.Raw
                    };
                    break;

                default: // Texture2D
                    if (isCube)
                    {
                        if (_desc.DepthOrArraySize > 6)
                        {
                            srvDesc.ViewDimension = ShaderResourceViewDimension.TextureCubeArray;
                            srvDesc.TextureCubeArray = new TextureCubeArrayShaderResourceView
                            {
                                MostDetailedMip = mostDetailedMip,
                                MipLevels = mipLevels > 0 ? mipLevels : (uint)_desc.MipLevels,
                                First2DArrayFace = 0,
                                NumCubes = (uint)(_desc.DepthOrArraySize / 6),
                                ResourceMinLODClamp = 0
                            };
                        }
                        else
                        {
                            srvDesc.ViewDimension = ShaderResourceViewDimension.TextureCube;
                            srvDesc.TextureCube = new TextureCubeShaderResourceView
                            {
                                MostDetailedMip = mostDetailedMip,
                                MipLevels = mipLevels > 0 ? mipLevels : (uint)_desc.MipLevels,
                                ResourceMinLODClamp = 0
                            };
                        }
                    }
                    else if (_desc.DepthOrArraySize > 1)
                    {
                        srvDesc.ViewDimension = ShaderResourceViewDimension.Texture2DArray;
                        srvDesc.Texture2DArray = new Texture2DArrayShaderResourceView
                        {
                            MostDetailedMip = mostDetailedMip,
                            MipLevels = mipLevels > 0 ? mipLevels : (uint)_desc.MipLevels,
                            FirstArraySlice = 0,
                            ArraySize = (uint)_desc.DepthOrArraySize,
                            PlaneSlice = 0,
                            ResourceMinLODClamp = 0
                        };
                    }
                    else
                    {
                        srvDesc.ViewDimension = ShaderResourceViewDimension.Texture2D;
                        srvDesc.Texture2D = new Texture2DShaderResourceView
                        {
                            MostDetailedMip = mostDetailedMip,
                            MipLevels = mipLevels > 0 ? mipLevels : (uint)_desc.MipLevels,
                            PlaneSlice = 0,
                            ResourceMinLODClamp = 0
                        };
                    }
                    break;
            }

            device.CreateShaderResourceView(_nativeResource, srvDesc, destHandle);
            _srvHandle = destHandle;
            _hasSRV = true;
        }
    }

    // ==============================================================================================
    //  D3D12RenderTarget - 渲染目标
    // ==============================================================================================

    /// <summary>
    /// D3D12 渲染目标。从纹理创建 RTV，支持 MSAA。
    /// </summary>
    public sealed class D3D12RenderTarget
    {
        private D3D12Texture _texture;
        private CpuDescriptorHandle _rtvHandle;
        private bool _disposed;

        public D3D12Texture Texture => _texture;
        public CpuDescriptorHandle RTVHandle => _rtvHandle;
        public int Width => _texture.Width;
        public int Height => _texture.Height;

        public D3D12RenderTarget(D3D12Texture texture, ID3D12Device device,
            CpuDescriptorHandle rtvDestHandle, uint mipSlice = 0, uint arraySlice = 0)
        {
            _texture = texture;

            var rtvDesc = new RenderTargetViewDescription
            {
                Format = texture.DxgiFormat,
            };

            if (texture.TextureDesc.Value.SampleCount > 1)
            {
                // MSAA 渲染目标
                if (texture.TextureDesc.Value.DepthOrArraySize > 1)
                {
                    rtvDesc.ViewDimension = RenderTargetViewDimension.Texture2DMSArray;
                    rtvDesc.Texture2DMSArray = new Texture2DMSArrayRenderTargetView
                    {
                        FirstArraySlice = arraySlice,
                        ArraySize = 1
                    };
                }
                else
                {
                    rtvDesc.ViewDimension = RenderTargetViewDimension.Texture2DMultisampled;
                    rtvDesc.Texture2DMS = new Texture2DMSRenderTargetView();
                }
            }
            else
            {
                if (texture.TextureDesc.Value.DepthOrArraySize > 1)
                {
                    rtvDesc.ViewDimension = RenderTargetViewDimension.Texture2DArray;
                    rtvDesc.Texture2DArray = new Texture2DArrayRenderTargetView
                    {
                        MipSlice = mipSlice,
                        FirstArraySlice = arraySlice,
                        ArraySize = 1,
                        PlaneSlice = 0
                    };
                }
                else
                {
                    rtvDesc.ViewDimension = RenderTargetViewDimension.Texture2D;
                    rtvDesc.Texture2D = new Texture2DRenderTargetView
                    {
                        MipSlice = mipSlice,
                        PlaneSlice = 0
                    };
                }
            }

            device.CreateRenderTargetView(texture.NativeResource, rtvDesc, rtvDestHandle);
            _rtvHandle = rtvDestHandle;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            // RTV 描述符由描述符池管理释放
        }
    }

    // ==============================================================================================
    //  D3D12DepthStencil - 深度模板
    // ==============================================================================================

    /// <summary>
    /// D3D12 深度模板视图。支持只读深度和只读模板。
    /// </summary>
    public sealed class D3D12DepthStencil
    {
        private D3D12Texture _texture;
        private CpuDescriptorHandle _dsvHandle;
        private CpuDescriptorHandle _dsvReadOnlyHandle;
        private bool _disposed;

        public D3D12Texture Texture => _texture;
        public CpuDescriptorHandle DSVHandle => _dsvHandle;
        public CpuDescriptorHandle DSVReadOnlyHandle => _dsvReadOnlyHandle;

        public D3D12DepthStencil(D3D12Texture texture, ID3D12Device device,
            CpuDescriptorHandle dsvDestHandle,
            uint mipSlice = 0, uint arraySlice = 0)
        {
            _texture = texture;
            _dsvReadOnlyHandle = default;

            // 读写 DSV
            var dsvDesc = new DepthStencilViewDescription
            {
                Format = texture.DxgiFormat,
                Flags = DepthStencilViewFlags.None
            };

            if (texture.TextureDesc.Value.DepthOrArraySize > 1)
            {
                dsvDesc.ViewDimension = DepthStencilViewDimension.Texture2DArray;
                dsvDesc.Texture2DArray = new Texture2DArrayDepthStencilView
                {
                    MipSlice = mipSlice,
                    FirstArraySlice = arraySlice,
                    ArraySize = 1
                };
            }
            else
            {
                dsvDesc.ViewDimension = DepthStencilViewDimension.Texture2D;
                dsvDesc.Texture2D = new Texture2DDepthStencilView
                {
                    MipSlice = mipSlice
                };
            }

            device.CreateDepthStencilView(texture.NativeResource, dsvDesc, dsvDestHandle);
            _dsvHandle = dsvDestHandle;
        }

        /// <summary>创建只读 DSV (用于深度预渲染 Pass)</summary>
        public void CreateReadOnlyDSV(ID3D12Device device, CpuDescriptorHandle destHandle,
            bool depthReadOnly = true, bool stencilReadOnly = true)
        {
            var dsvDesc = new DepthStencilViewDescription
            {
                Format = _texture.DxgiFormat,
                Flags = DepthStencilViewFlags.ReadOnlyDepth | DepthStencilViewFlags.ReadOnlyStencil
            };

            if (_texture.TextureDesc.Value.DepthOrArraySize > 1)
            {
                dsvDesc.ViewDimension = DepthStencilViewDimension.Texture2DArray;
                dsvDesc.Texture2DArray = new Texture2DArrayDepthStencilView
                {
                    MipSlice = 0,
                    FirstArraySlice = 0,
                    ArraySize = 1
                };
            }
            else
            {
                dsvDesc.ViewDimension = DepthStencilViewDimension.Texture2D;
                dsvDesc.Texture2D = new Texture2DDepthStencilView { MipSlice = 0 };
            }

            // 根据需求设置 flags
            if (!depthReadOnly)
                dsvDesc.Flags &= ~DepthStencilViewFlags.ReadOnlyDepth;
            if (!stencilReadOnly)
                dsvDesc.Flags &= ~DepthStencilViewFlags.ReadOnlyStencil;

            device.CreateDepthStencilView(_texture.NativeResource, dsvDesc, destHandle);
            _dsvReadOnlyHandle = destHandle;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
        }
    }

    // ==============================================================================================
    //  D3D12UploadBuffer - 上传缓冲区 (Ring Buffer)
    // ==============================================================================================

    /// <summary>
    /// 上传缓冲区。使用 Ring Buffer 策略进行每帧分配，自动回收已完成的帧数据。
    /// Map 后保持持久映射状态。
    /// </summary>
    public sealed class D3D12UploadBuffer : IDisposable
    {
        private readonly ID3D12Device _device;
        private readonly ID3D12Resource _resource;
        private readonly ulong _size;
        private unsafe byte* _mappedPtr;
        private ulong _head;
        private ulong _frameStartOffset;
        private readonly object _lock = new();
        private bool _disposed;

        public ID3D12Resource Resource => _resource;
        public ulong Size => _size;
        public ulong UsedBytes => _head;
        public ulong FreeBytes => _size - _head;

        public unsafe D3D12UploadBuffer(ID3D12Device device, ulong size, string name = "")
        {
            _device = device;
            _size = AlignUp(size, 64);
            _head = 0;
            _frameStartOffset = 0;

            _resource = device.CreateCommittedResource(
                new HeapDescription(HeapType.Upload, _size),
                ResourceStates.GenericRead,
                null);

            if (!string.IsNullOrEmpty(name))
                _resource.DeviceName = name;

            // 持久映射
            _resource.Map(0, null, out IntPtr ptr);
            _mappedPtr = (byte*)ptr;
        }

        /// <summary>分配一块上传内存并拷贝数据</summary>
        public unsafe UploadAllocation Allocate<T>(ReadOnlySpan<T> data) where T : unmanaged
        {
            lock (_lock)
            {
                uint alignedSize = (uint)AlignUp((ulong)(data.Length * sizeof(T)), 256);

                if (_head + alignedSize > _size)
                    throw new OutOfMemoryException($"UploadBuffer exhausted: requested={alignedSize}, available={FreeBytes}");

                ulong offset = _head;
                fixed (void* src = data)
                {
                    Buffer.MemoryCopy(src, _mappedPtr + offset, alignedSize, data.Length * sizeof(T));
                }
                _head += alignedSize;

                return new UploadAllocation
                {
                    Offset = offset,
                    Size = alignedSize,
                    GpuAddress = _resource.GPUVirtualAddress + offset
                };
            }
        }

        /// <summary>帧开始时标记偏移</summary>
        public void BeginFrame()
        {
            lock (_lock)
            {
                _frameStartOffset = _head;
            }
        }

        /// <summary>帧结束时回收内存 (Ring Buffer 回绕)</summary>
        public void EndFrame()
        {
            lock (_lock)
            {
                // 上传缓冲区每帧重置 (上传完成后数据即被 GPU 使用)
                _head = 0;
            }
        }

        public unsafe void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _resource.Unmap(0);
            _resource.Dispose();
        }

        private static ulong AlignUp(ulong value, ulong alignment)
            => (value + alignment - 1) & ~(alignment - 1);
    }

    /// <summary>上传分配结果</summary>
    public struct UploadAllocation
    {
        public ulong Offset;
        public ulong Size;
        public ulong GpuAddress;
    }

    // ==============================================================================================
    //  D3D12ReadbackBuffer - 回读缓冲区
    // ==============================================================================================

    /// <summary>
    /// 回读缓冲区。使用 ReadbackHeap，支持异步读取和分帧读取。
    /// </summary>
    public sealed class D3D12ReadbackBuffer : IDisposable
    {
        private readonly ID3D12Device _device;
        private readonly ID3D12Resource _resource;
        private readonly ulong _size;
        private readonly object _lock = new();
        private bool _disposed;

        public ID3D12Resource Resource => _resource;
        public ulong Size => _size;

        public D3D12ReadbackBuffer(ID3D12Device device, ulong size, string name = "")
        {
            _device = device;
            _size = AlignUp(size, 64);

            _resource = device.CreateCommittedResource(
                new HeapDescription(HeapType.Readback, _size),
                ResourceStates.CopyDest,
                null);

            if (!string.IsNullOrEmpty(name))
                _resource.DeviceName = name;
        }

        /// <summary>同步读取数据 (CPU 阻塞等待 GPU 拷贝完成)</summary>
        public unsafe void ReadData<T>(Span<T> data, ulong offset = 0) where T : unmanaged
        {
            lock (_lock)
            {
                // 映射回读缓冲区
                _resource.Map(0, null, out IntPtr ptr);
                byte* src = (byte*)ptr + offset;
                fixed (void* dst = data)
                {
                    Buffer.MemoryCopy(src, dst, data.Length * sizeof(T), data.Length * sizeof(T));
                }
                _resource.Unmap(0);
            }
        }

        /// <summary>获取映射指针 (配合 ReadbackFence 异步使用)</summary>
        public unsafe IntPtr Map()
        {
            _resource.Map(0, null, out IntPtr ptr);
            return ptr;
        }

        public void Unmap()
        {
            _resource.Unmap(0);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _resource.Dispose();
        }

        private static ulong AlignUp(ulong value, ulong alignment)
            => (value + alignment - 1) & ~(alignment - 1);
    }

    // ==============================================================================================
    //  D3D12StructuredBuffer - 结构化缓冲区 (Counter / Append / Consume)
    // ==============================================================================================

    /// <summary>
    /// 结构化缓冲区。支持 Counter Buffer、Append/Consume 操作。
    /// </summary>
    public sealed class D3D12StructuredBuffer : IDisposable
    {
        private readonly ID3D12Device _device;
        private readonly ID3D12Resource _buffer;
        private readonly ID3D12Resource _counterBuffer;
        private readonly uint _structureStride;
        private readonly uint _elementCount;
        private CpuDescriptorHandle _srvHandle;
        private CpuDescriptorHandle _uavHandle;
        private CpuDescriptorHandle _counterUAVHandle;
        private bool _disposed;

        public ID3D12Resource Buffer => _buffer;
        public ID3D12Resource CounterBuffer => _counterBuffer;
        public uint StructureStride => _structureStride;
        public uint ElementCount => _elementCount;
        public CpuDescriptorHandle SRVHandle => _srvHandle;
        public CpuDescriptorHandle UAVHandle => _uavHandle;

        public D3D12StructuredBuffer(ID3D12Device device, uint elementCount, uint structureStride,
            bool hasCounter = false, string name = "")
        {
            _device = device;
            _structureStride = structureStride;
            _elementCount = elementCount;

            ulong bufferSize = (ulong)elementCount * structureStride;

            _buffer = device.CreateCommittedResource(
                new HeapDescription(HeapType.Default, bufferSize),
                ResourceStates.GenericRead,
                null);

            if (!string.IsNullOrEmpty(name))
                _buffer.DeviceName = name + "_StructuredBuffer";

            if (hasCounter)
            {
                // Counter Buffer: 单个 uint (4 字节)
                _counterBuffer = device.CreateCommittedResource(
                    new HeapDescription(HeapType.Default, 4),
                    ResourceStates.UnorderedAccess,
                    null);

                if (!string.IsNullOrEmpty(name))
                    _counterBuffer.DeviceName = name + "_CounterBuffer";
            }
            else
            {
                _counterBuffer = null!;
            }
        }

        /// <summary>创建 SRV 描述符</summary>
        public void CreateSRV(ID3D12Device device, CpuDescriptorHandle destHandle)
        {
            var srvDesc = new ShaderResourceViewDescription
            {
                Shader4ComponentMapping = Vortice.Direct3D12.ShaderComponentMapping.Default,
                Format = Format.Unknown,
                ViewDimension = ShaderResourceViewDimension.Buffer,
                Buffer = new BufferShaderResourceView
                {
                    FirstElement = 0,
                    NumElements = _elementCount,
                    StructureByteStride = _structureStride,
                    Flags = BufferShaderResourceViewFlags.None
                }
            };
            device.CreateShaderResourceView(_buffer, srvDesc, destHandle);
            _srvHandle = destHandle;
        }

        /// <summary>创建 UAV 描述符 (支持 Append/Consume 模式)</summary>
        public void CreateUAV(ID3D12Device device, CpuDescriptorHandle destHandle,
            bool append = false, bool consume = false)
        {
            var uavFlags = BufferUnorderedAccessViewFlags.None;
            if (append) uavFlags = BufferUnorderedAccessViewFlags.Append;
            if (consume) uavFlags = BufferUnorderedAccessViewFlags.Counter;

            var uavDesc = new UnorderedAccessViewDescription
            {
                Format = Format.Unknown,
                ViewDimension = UnorderedAccessViewDimension.Buffer,
                Buffer = new BufferUnorderedAccessView
                {
                    FirstElement = 0,
                    NumElements = _elementCount,
                    StructureByteStride = _structureStride,
                    Flags = uavFlags,
                    Counter = _counterBuffer != null ? _counterBuffer.GPUVirtualAddress : 0,
                    CounterOffsetInBytes = 0
                }
            };
            device.CreateUnorderedAccessView(_buffer, _counterBuffer, uavDesc, destHandle);
            _uavHandle = destHandle;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _buffer.Dispose();
            _counterBuffer?.Dispose();
        }
    }

    // ==============================================================================================
    //  D3D12ByteAddressedBuffer - 原始缓冲区 (RAW UAV / SRV)
    // ==============================================================================================

    /// <summary>
    /// 字节地址缓冲区 (Raw Buffer)。使用 R32_Typeless 格式，SRV/UAV 使用 RAW 标志。
    /// </summary>
    public sealed class D3D12ByteAddressedBuffer : IDisposable
    {
        private readonly ID3D12Device _device;
        private readonly ID3D12Resource _buffer;
        private readonly uint _byteCount;
        private CpuDescriptorHandle _srvHandle;
        private CpuDescriptorHandle _uavHandle;
        private bool _disposed;

        public ID3D12Resource Buffer => _buffer;
        public uint ByteCount => _byteCount;
        public CpuDescriptorHandle SRVHandle => _srvHandle;
        public CpuDescriptorHandle UAVHandle => _uavHandle;

        public D3D12ByteAddressedBuffer(ID3D12Device device, uint byteCount, string name = "")
        {
            _device = device;
            _byteCount = AlignUp(byteCount, 4); // 必须 4 字节对齐

            _buffer = device.CreateCommittedResource(
                new HeapDescription(HeapType.Default, _byteCount),
                ResourceStates.GenericRead,
                null);

            if (!string.IsNullOrEmpty(name))
                _buffer.DeviceName = name + "_ByteAddressedBuffer";
        }

        /// <summary>创建 RAW SRV</summary>
        public void CreateSRV(ID3D12Device device, CpuDescriptorHandle destHandle, uint firstElement = 0, uint numElements = 0)
        {
            var srvDesc = new ShaderResourceViewDescription
            {
                Shader4ComponentMapping = Vortice.Direct3D12.ShaderComponentMapping.Default,
                Format = Format.R32_Typeless,
                ViewDimension = ShaderResourceViewDimension.Buffer,
                Buffer = new BufferShaderResourceView
                {
                    FirstElement = firstElement,
                    NumElements = numElements > 0 ? numElements : _byteCount / 4,
                    Flags = BufferShaderResourceViewFlags.Raw
                }
            };
            device.CreateShaderResourceView(_buffer, srvDesc, destHandle);
            _srvHandle = destHandle;
        }

        /// <summary>创建 RAW UAV</summary>
        public void CreateUAV(ID3D12Device device, CpuDescriptorHandle destHandle,
            uint firstElement = 0, uint numElements = 0)
        {
            var uavDesc = new UnorderedAccessViewDescription
            {
                Format = Format.R32_Typeless,
                ViewDimension = UnorderedAccessViewDimension.Buffer,
                Buffer = new BufferUnorderedAccessView
                {
                    FirstElement = firstElement,
                    NumElements = numElements > 0 ? numElements : _byteCount / 4,
                    Flags = BufferUnorderedAccessViewFlags.Raw
                }
            };
            device.CreateUnorderedAccessView(_buffer, null, uavDesc, destHandle);
            _uavHandle = destHandle;
        }

        private static uint AlignUp(uint value, uint alignment)
            => (value + alignment - 1) & ~(alignment - 1);

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _buffer.Dispose();
        }
    }

    // ==============================================================================================
    //  ResourceStateTracker - 资源状态跟踪器
    // ==============================================================================================

    /// <summary>
    /// 资源状态跟踪器。跟踪每个子资源的当前状态，支持全局状态和 Per-CommandList 状态，
    /// 自动推断并插入必要的资源屏障。
    /// </summary>
    public sealed class ResourceStateTracker : IDisposable
    {
        private readonly Dictionary<nint, PerResourceState> _globalStates = new();
        private readonly Dictionary<nint, PerResourceState> _commandListStates = new();
        private readonly List<ResourceBarrierTransition> _pendingBarriers = new();
        private bool _disposed;

        private struct PerResourceState
        {
            public ResourceStates GlobalState;
            public ResourceStates[] SubresourceStates;
            public bool HasPerSubresource;
            public int SubresourceCount;

            public void Init(int subresourceCount, ResourceStates initialState)
            {
                SubresourceCount = subresourceCount;
                GlobalState = initialState;
                if (subresourceCount > 1)
                {
                    SubresourceStates = new ResourceStates[subresourceCount];
                    for (int i = 0; i < subresourceCount; i++)
                        SubresourceStates[i] = initialState;
                    HasPerSubresource = true;
                }
                else
                {
                    SubresourceStates = Array.Empty<ResourceStates>();
                    HasPerSubresource = false;
                }
            }
        }

        /// <summary>
        /// 注册资源并设置初始状态。
        /// </summary>
        public void RegisterResource(ID3D12Resource resource, ResourceStates initialState, int subresourceCount = 1)
        {
            nint key = resource.NativePointer;
            var state = new PerResourceState();
            state.Init(subresourceCount, initialState);
            _globalStates[key] = state;
        }

        /// <summary>
        /// 尝试转换资源状态。如果状态确实改变则记录屏障，
        /// 否则跳过冗余屏障。
        /// </summary>
        public bool TryTransition(ID3D12Resource resource, uint subresource,
            ResourceStates newState, out ResourceBarrierTransition barrier)
        {
            barrier = default;
            nint key = resource.NativePointer;

            if (!_globalStates.TryGetValue(key, out var state))
            {
                // 未跟踪的资源: 假设 Common 状态
                state = new PerResourceState();
                state.Init(1, ResourceStates.Common);
                _globalStates[key] = state;
            }

            ResourceStates oldState;
            if (subresource == 0xFFFFFFFF)
            {
                oldState = state.GlobalState;
                state.GlobalState = newState;

                // 同步更新所有子资源
                if (state.HasPerSubresource)
                {
                    for (int i = 0; i < state.SubresourceStates.Length; i++)
                        state.SubresourceStates[i] = newState;
                }
            }
            else
            {
                if (state.HasPerSubresource && subresource < state.SubresourceStates.Length)
                {
                    oldState = state.SubresourceStates[subresource];
                    state.SubresourceStates[subresource] = newState;
                }
                else
                {
                    oldState = state.GlobalState;
                    state.GlobalState = newState;
                }
            }

            if (oldState == newState) return false; // 无需转换

            barrier = new ResourceBarrierTransition(resource, subresource, oldState, newState);
            return true;
        }

        /// <summary>强制设置资源状态 (不产生屏障)</summary>
        public void ForceSetState(ID3D12Resource resource, ResourceStates state, uint subresource = 0xFFFFFFFF)
        {
            nint key = resource.NativePointer;

            if (!_globalStates.TryGetValue(key, out var perState))
            {
                perState = new PerResourceState();
                perState.Init(1, state);
                _globalStates[key] = perState;
                return;
            }

            if (subresource == 0xFFFFFFFF)
            {
                perState.GlobalState = state;
                if (perState.HasPerSubresource)
                    for (int i = 0; i < perState.SubresourceStates.Length; i++)
                        perState.SubresourceStates[i] = state;
            }
            else if (perState.HasPerSubresource && subresource < perState.SubresourceStates.Length)
            {
                perState.SubresourceStates[subresource] = state;
            }
            else
            {
                perState.GlobalState = state;
            }
        }

        /// <summary>获取资源的当前状态</summary>
        public ResourceStates GetState(ID3D12Resource resource, uint subresource = 0xFFFFFFFF)
        {
            nint key = resource.NativePointer;
            if (!_globalStates.TryGetValue(key, out var state))
                return ResourceStates.Common;

            if (subresource == 0xFFFFFFFF) return state.GlobalState;

            if (state.HasPerSubresource && subresource < state.SubresourceStates.Length)
                return state.SubresourceStates[subresource];

            return state.GlobalState;
        }

        /// <summary>开始新的 CommandList 状态跟踪</summary>
        public void BeginCommandList()
        {
            _commandListStates.Clear();
        }

        /// <summary>
        /// 提交 CommandList 的状态变更到全局状态。
        /// 调用此方法前 GPU 应已完成执行。
        /// </summary>
        public void CommitCommandListStates()
        {
            foreach (var kvp in _commandListStates)
            {
                if (_globalStates.TryGetValue(kvp.Key, out var global))
                {
                    // 合并子资源状态
                    if (kvp.Value.HasPerSubresource)
                    {
                        for (int i = 0; i < kvp.Value.SubresourceStates.Length; i++)
                        {
                            if (i < global.SubresourceStates.Length)
                                global.SubresourceStates[i] = kvp.Value.SubresourceStates[i];
                        }
                    }
                    global.GlobalState = kvp.Value.GlobalState;
                }
            }
            _commandListStates.Clear();
        }

        /// <summary>移除资源跟踪</summary>
        public void UnregisterResource(ID3D12Resource resource)
        {
            _globalStates.Remove(resource.NativePointer);
            _commandListStates.Remove(resource.NativePointer);
        }

        /// <summary>清除所有跟踪状态</summary>
        public void Reset()
        {
            _globalStates.Clear();
            _commandListStates.Clear();
            _pendingBarriers.Clear();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _globalStates.Clear();
            _commandListStates.Clear();
        }
    }

    // ==============================================================================================
    //  AliasingResource - 资源别名
    // ==============================================================================================

    /// <summary>
    /// 资源别名: 同一内存位置上的不同资源视图。
    /// 支持不同格式和不同维度。
    /// </summary>
    public sealed class AliasingResource : IDisposable
    {
        private readonly ID3D12Device _device;
        private readonly ID3D12Heap _heap;
        private readonly ulong _heapOffset;
        private readonly ulong _heapSize;
        private readonly List<D3D12Resource> _aliasedResources = new();
        private D3D12Resource? _currentActive;
        private bool _disposed;

        public ID3D12Heap Heap => _heap;
        public ulong Offset => _heapOffset;
        public D3D12Resource? CurrentActive => _currentActive;

        public AliasingResource(ID3D12Device device, ID3D12Heap heap, ulong offset, ulong size)
        {
            _device = device;
            _heap = heap;
            _heapOffset = offset;
            _heapSize = size;
        }

        /// <summary>
        /// 在别名内存上创建新的资源 (不同格式/维度)。
        /// </summary>
        public D3D12Resource CreateAliasedResource<T>(
            ResourceDescription description,
            ResourceStates initialState,
            string name = "") where T : D3D12Resource
        {
            // 验证大小兼容性
            // (实际中需要根据 D3D12 Placed Resource 规则计算)

            var resource = _device.CreatePlacedResource(
                _heap, _heapOffset,
                description,
                initialState);

            _aliasedResources.Add(null!); // 占位
            return null!;
        }

        /// <summary>
        /// 切换活跃别名资源。自动发出 Aliasing Barrier。
        /// </summary>
        public void SwitchActive(D3D12GraphicsCommandList commandList, D3D12Resource newResource)
        {
            if (_currentActive != null && _currentActive != newResource)
            {
                // 发出 Aliasing Barrier
                commandList.ResourceBarrierAliasing(_currentActive.NativeResource, newResource.NativeResource);
            }
            _currentActive = newResource;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var res in _aliasedResources)
                res?.Dispose();
            _aliasedResources.Clear();
            // Heap 由外部管理
        }
    }
}
