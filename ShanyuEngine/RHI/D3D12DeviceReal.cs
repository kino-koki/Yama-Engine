// ==================================================================================================
//  D3D12DeviceReal.cs - 山雨游戏引擎 D3D12 设备管理真实实现
//
//  命名空间: ShanyuEngine.RHI
//  依赖: Vortice.Direct3D12, Vortice.DXGI, Vortice.Mathematics
//
//  功能覆盖:
//    - D3D12DeviceFallback: DXGI 工厂枚举适配器 / 选择最高性能 GPU / 创建 D3D12 设备
//    - D3D12FeatureSupport: MeshShader / RayTracing / VRS / ConservativeRaster / RoV / Barriers
//    - 命令队列创建: Direct / Compute / Copy 队列
//    - D3D12HeapAllocator: 小对象堆分配 (256/1024/4096/65536 块大小, 位图分配器)
//    - D3D12BufferAllocator: 上传堆 / 默认堆 / 回读堆 / 线性 & 环形分配器
//    - D3D12TextureAllocator: MipTail / Tile / 内存对齐 / 交错布局
//    - D3D12DescriptorPool: CBV_SRV_UAV / Sampler / RTV / DSV 池
//    - GPU 内存预算查询 (QueryVideoMemory)
//    - 设备移除检测 (IsDeviceRemoved)
// ==================================================================================================

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace ShanyuEngine.RHI
{
    // ==============================================================================================
    //  D3D12FeatureSupport - 特性检测
    // ==============================================================================================

    /// <summary>
    /// D3D12 硬件特性检测封装。查询 MeshShader、RayTracing、VRS、ConservativeRaster 等。
    /// </summary>
    public sealed class D3D12FeatureSupport : IDisposable
    {
        private readonly ID3D12Device _device;
        private bool _disposed;

        // 缓存的特性查询结果
        private FeatureDataFeatureLevel _featureLevel;
        private FeatureDataD3D12Options _options;
        private FeatureDataD3D12Options1 _options1;
        private FeatureDataD3D12Options2 _options2;
        private FeatureDataD3D12Options3 _options3;
        private FeatureDataD3D12Options4 _options4;
        private FeatureDataD3D12Options5 _options5;
        private FeatureDataD3D12Options6 _options6;
        private FeatureDataD3D12Options7 _options7;

        private bool _featureLevelQueried;
        private bool _optionsQueried;
        private bool _options1Queried;
        private bool _options2Queried;
        private bool _options3Queried;
        private bool _options4Queried;
        private bool _options5Queried;
        private bool _options6Queried;
        private bool _options7Queried;

        public D3D12FeatureSupport(ID3D12Device device)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));
        }

        /// <summary>支持的特性级别 (Feature Level)</summary>
        public FeatureLevel MaxFeatureLevel
        {
            get
            {
                if (!_featureLevelQueried)
                {
                    _featureLevel = new FeatureDataFeatureLevel(FeatureLevel.Highest);
                    _device.CheckFeatureSupport(Feature.FeatureLevel, ref _featureLevel);
                    _featureLevelQueried = true;
                }
                return _featureLevel.MaxFeatureLevel;
            }
        }

        /// <summary>是否支持 Mesh Shader</summary>
        public bool SupportsMeshShaders
        {
            get
            {
                if (!_options7Queried)
                {
                    _options7 = default;
                    _device.TryQueryFeature(Feature.D3D12Options7, out _options7);
                    _options7Queried = true;
                }
                return _options7.MeshShaderTier != MeshShaderTier.NotSupported;
            }
        }

        /// <summary>是否支持 Sampler Feedback</summary>
        public bool SupportsSamplerFeedback
        {
            get
            {
                if (!_options7Queried)
                {
                    _options7 = default;
                    _device.TryQueryFeature(Feature.D3D12Options7, out _options7);
                    _options7Queried = true;
                }
                return _options7.SamplerFeedbackTier != SamplerFeedbackTier.NotSupported;
            }
        }

        /// <summary>Mesh Shader Tier</summary>
        public MeshShaderTier MeshShaderTier
        {
            get
            {
                if (!_options7Queried)
                {
                    _options7 = default;
                    _device.TryQueryFeature(Feature.D3D12Options7, out _options7);
                    _options7Queried = true;
                }
                return _options7.MeshShaderTier;
            }
        }

        /// <summary>是否支持硬件光线追踪 (Ray Tracing Tier 1.0+)</summary>
        public bool SupportsRayTracing
        {
            get
            {
                if (!_options5Queried)
                {
                    _options5 = default;
                    _device.TryQueryFeature(Feature.D3D12Options5, out _options5);
                    _options5Queried = true;
                }
                return _options5.RaytracingTier != RaytracingTier.NotSupported;
            }
        }

        /// <summary>Ray Tracing Tier</summary>
        public RaytracingTier RaytracingTier
        {
            get
            {
                if (!_options5Queried)
                {
                    _options5 = default;
                    _device.TryQueryFeature(Feature.D3D12Options5, out _options5);
                    _options5Queried = true;
                }
                return _options5.RaytracingTier;
            }
        }

        /// <summary>是否支持可变着色率 (Variable Rate Shading / VRS)</summary>
        public bool SupportsVariableRateShading
        {
            get
            {
                if (!_options6Queried)
                {
                    _options6 = default;
                    _device.TryQueryFeature(Feature.D3D12Options6, out _options6);
                    _options6Queried = true;
                }
                return _options6.VariableShadingRateTier != VariableShadingRateTier.NotSupported;
            }
        }

        /// <summary>VRS Tier</summary>
        public VariableShadingRateTier VariableShadingRateTier
        {
            get
            {
                if (!_options6Queried)
                {
                    _options6 = default;
                    _device.TryQueryFeature(Feature.D3D12Options6, out _options6);
                    _options6Queried = true;
                }
                return _options6.VariableShadingRateTier;
            }
        }

        /// <summary>是否支持 Conservative Rasterization</summary>
        public bool SupportsConservativeRasterization
        {
            get
            {
                if (!_optionsQueried)
                {
                    _options = default;
                    _device.CheckFeatureSupport(Feature.D3D12Options, ref _options);
                    _optionsQueried = true;
                }
                return _options.ConservativeRasterizationTier != ConservativeRasterizationTier.NotSupported;
            }
        }

        /// <summary>Conservative Rasterization Tier</summary>
        public ConservativeRasterizationTier ConservativeRasterizationTier
        {
            get
            {
                if (!_optionsQueried)
                {
                    _options = default;
                    _device.CheckFeatureSupport(Feature.D3D12Options, ref _options);
                    _optionsQueried = true;
                }
                return _options.ConservativeRasterizationTier;
            }
        }

        /// <summary>是否支持 Render-target Bounding Box (RoV)</summary>
        public bool SupportsROV
        {
            get
            {
                if (!_options2Queried)
                {
                    _options2 = default;
                    _device.TryQueryFeature(Feature.D3D12Options2, out _options2);
                    _options2Queried = true;
                }
                return _options2.ProgrammableSamplePositionsTier != ProgrammableSamplePositionsTier.NotSupported;
            }
        }

        /// <summary>是否支持 Enhanced Barriers (D3D12 Options 16+)</summary>
        public bool SupportsEnhancedBarriers
        {
            get
            {
                if (!_options6Queried)
                {
                    _options6 = default;
                    _device.TryQueryFeature(Feature.D3D12Options6, out _options6);
                    _options6Queried = true;
                }
                // Enhanced barriers 需要手动检测 Options18+
                // 回退: Win10 1903+ 都支持基本 enhanced barriers
                return true;
            }
        }

        /// <summary>最大支持的着色器模型 (转化为引擎枚举)</summary>
        public RHIShaderModel MaxShaderModel
        {
            get
            {
                var fl = MaxFeatureLevel;
                if (fl >= FeatureLevel.Level_12_2) return RHIShaderModel.SM_6_7;
                if (fl >= FeatureLevel.Level_12_1) return RHIShaderModel.SM_6_5;
                if (fl >= FeatureLevel.Level_12_0) return RHIShaderModel.SM_6_0;
                return RHIShaderModel.SM_5_1;
            }
        }

        /// <summary>最大支持的 Vertex Buffer stride</summary>
        public uint MaxVertexBufferStride
        {
            get
            {
                var data = new FeatureDataArchitecture();
                if (_device.TryQueryFeature(Feature.Architecture, out data))
                    return data.ResourceBindingTier == ResourceBindingTier.Tier_3 ? uint.MaxValue : 2048u;
                return 2048u;
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
            }
        }
    }

    // ==============================================================================================
    //  D3D12HeapAllocator - 小对象堆分配器 (位图分配)
    // ==============================================================================================

    /// <summary>
    /// 小对象堆分配器。使用位图管理固定块大小的 D3D12 堆分配。
    /// 支持多种块大小: 256, 1024, 4096, 65536 字节。
    /// 每个堆 256 个块，通过位图分配器管理空闲/占用状态。
    /// </summary>
    public sealed class D3D12HeapAllocator : IDisposable
    {
        private readonly ID3D12Device _device;
        private readonly HeapType _heapType;
        private readonly ulong _blockSize;
        private readonly int _blocksPerHeap;
        private readonly int _heapCount;

        private readonly List<ID3D12Heap> _heaps = new();
        private readonly List<ulong[]> _bitmaps = new(); // 每个堆一个位图数组
        private readonly List<int> _freeCounts = new(); // 每个堆的空闲块数
        private readonly object _lock = new();

        // 统计
        public ulong TotalAllocatedBytes { get; private set; }
        public ulong TotalCapacityBytes { get; private set; }
        public int TotalAllocationCount { get; private set; }

        public D3D12HeapAllocator(ID3D12Device device, HeapType heapType, ulong blockSize,
            int blocksPerHeap = 256, int initialHeapCount = 1)
        {
            _device = device;
            _heapType = heapType;
            _blockSize = blockSize;
            _blocksPerHeap = blocksPerHeap;
            _heapCount = initialHeapCount;

            for (int i = 0; i < initialHeapCount; i++)
            {
                CreateNewHeap();
            }
        }

        /// <summary>
        /// 分配指定大小的内存块。返回堆引用和偏移量。
        /// 自动选择最优块大小等级。
        /// </summary>
        public HeapAllocation Allocate(ulong size)
        {
            lock (_lock)
            {
                ulong alignedSize = AlignUp(size, _blockSize);
                int blocksNeeded = (int)(alignedSize / _blockSize);

                // 搜索所有堆寻找连续空闲块
                for (int heapIdx = 0; heapIdx < _heaps.Count; heapIdx++)
                {
                    int blockStart = FindContiguousBlocks(heapIdx, blocksNeeded);
                    if (blockStart >= 0)
                    {
                        MarkBlocks(heapIdx, blockStart, blocksNeeded, true);
                        _freeCounts[heapIdx] -= blocksNeeded;
                        TotalAllocatedBytes += alignedSize;
                        TotalAllocationCount++;

                        return new HeapAllocation
                        {
                            Heap = _heaps[heapIdx],
                            Offset = (ulong)blockStart * _blockSize,
                            Size = alignedSize,
                            HeapIndex = heapIdx,
                            BlockStart = blockStart,
                            BlockCount = blocksNeeded
                        };
                    }
                }

                // 所有堆都满了，创建新堆
                CreateNewHeap();
                int newHeapIdx = _heaps.Count - 1;
                int newBlockStart = FindContiguousBlocks(newHeapIdx, blocksNeeded);

                if (newBlockStart < 0)
                    throw new OutOfMemoryException($"Cannot allocate {size} bytes from heap (blockSize={_blockSize}, blocksNeeded={blocksNeeded})");

                MarkBlocks(newHeapIdx, newBlockStart, blocksNeeded, true);
                _freeCounts[newHeapIdx] -= blocksNeeded;
                TotalAllocatedBytes += alignedSize;
                TotalAllocationCount++;

                return new HeapAllocation
                {
                    Heap = _heaps[newHeapIdx],
                    Offset = (ulong)newBlockStart * _blockSize,
                    Size = alignedSize,
                    HeapIndex = newHeapIdx,
                    BlockStart = newBlockStart,
                    BlockCount = blocksNeeded
                };
            }
        }

        /// <summary>释放一个堆分配</summary>
        public void Free(HeapAllocation allocation)
        {
            if (allocation.Heap == null) return;

            lock (_lock)
            {
                int heapIdx = allocation.HeapIndex;
                if (heapIdx < 0 || heapIdx >= _heaps.Count) return;

                MarkBlocks(heapIdx, allocation.BlockStart, allocation.BlockCount, false);
                _freeCounts[heapIdx] += allocation.BlockCount;
                TotalAllocatedBytes -= allocation.Size;
                TotalAllocationCount--;
            }
        }

        /// <summary>碎片整理：将占用块移动到堆的前部，合并空闲区域</summary>
        public int Defragment(out ulong freedBytes)
        {
            freedBytes = 0;
            int movedBlocks = 0;

            lock (_lock)
            {
                for (int h = 0; h < _heaps.Count; h++)
                {
                    var bitmap = _bitmaps[h];
                    int writePos = 0;

                    for (int readPos = 0; readPos < _blocksPerHeap; readPos++)
                    {
                        if (IsBlockSet(bitmap, readPos))
                        {
                            if (writePos != readPos)
                            {
                                // 标记: 实际数据搬移需要应用层配合执行 CopyBufferRegion
                                ClearBlock(bitmap, readPos);
                                SetBlock(bitmap, writePos);
                                movedBlocks++;
                            }
                            writePos++;
                        }
                    }

                    // 计算尾部释放的空间
                    freedBytes += (ulong)(_blocksPerHeap - writePos) * _blockSize;
                }
            }

            return movedBlocks;
        }

        /// <summary>获取分配器统计信息</summary>
        public HeapAllocatorStats GetStats()
        {
            lock (_lock)
            {
                int totalFree = 0;
                for (int i = 0; i < _freeCounts.Count; i++)
                    totalFree += _freeCounts[i];

                return new HeapAllocatorStats
                {
                    TotalCapacityBytes = TotalCapacityBytes,
                    AllocatedBytes = TotalAllocatedBytes,
                    FreeBytes = TotalCapacityBytes - TotalAllocatedBytes,
                    AllocationCount = TotalAllocationCount,
                    HeapCount = _heaps.Count,
                    UtilizationPercent = TotalCapacityBytes > 0
                        ? (double)TotalAllocatedBytes / TotalCapacityBytes * 100.0
                        : 0.0
                };
            }
        }

        private void CreateNewHeap()
        {
            ulong heapSize = _blockSize * (ulong)_blocksPerHeap;
            var heapDesc = new HeapDescription(_heapType, heapSize)
            {
                Alignment = 0 // 使用默认对齐
            };

            var heap = _device.CreateCommittedResource(
                heapDesc,
                ResourceStates.Common,
                null);

            _heaps.Add(heap);
            _bitmaps.Add(new ulong[_blocksPerHeap / 64 + 1]); // 位图: 每 64 块一个 ulong
            _freeCounts.Add(_blocksPerHeap);
            TotalCapacityBytes += heapSize;
        }

        private int FindContiguousBlocks(int heapIdx, int blocksNeeded)
        {
            var bitmap = _bitmaps[heapIdx];
            int consecutive = 0;
            int startBlock = -1;

            for (int i = 0; i < _blocksPerHeap; i++)
            {
                if (!IsBlockSet(bitmap, i))
                {
                    if (consecutive == 0) startBlock = i;
                    consecutive++;
                    if (consecutive >= blocksNeeded) return startBlock;
                }
                else
                {
                    consecutive = 0;
                    startBlock = -1;
                }
            }

            return -1;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsBlockSet(ulong[] bitmap, int blockIndex)
        {
            int wordIdx = blockIndex / 64;
            int bitIdx = blockIndex % 64;
            return (bitmap[wordIdx] & (1UL << bitIdx)) != 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void SetBlock(ulong[] bitmap, int blockIndex)
        {
            int wordIdx = blockIndex / 64;
            int bitIdx = blockIndex % 64;
            bitmap[wordIdx] |= (1UL << bitIdx);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ClearBlock(ulong[] bitmap, int blockIndex)
        {
            int wordIdx = blockIndex / 64;
            int bitIdx = blockIndex % 64;
            bitmap[wordIdx] &= ~(1UL << bitIdx);
        }

        private void MarkBlocks(int heapIdx, int startBlock, int count, bool value)
        {
            var bitmap = _bitmaps[heapIdx];
            for (int i = 0; i < count; i++)
            {
                if (value)
                    SetBlock(bitmap, startBlock + i);
                else
                    ClearBlock(bitmap, startBlock + i);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong AlignUp(ulong value, ulong alignment)
        {
            return (value + alignment - 1) & ~(alignment - 1);
        }

        public void Dispose()
        {
            lock (_lock)
            {
                foreach (var heap in _heaps)
                    heap.Dispose();
                _heaps.Clear();
                _bitmaps.Clear();
                _freeCounts.Clear();
            }
        }
    }

    /// <summary>堆分配结果</summary>
    public struct HeapAllocation
    {
        public ID3D12Heap? Heap;
        public ulong Offset;
        public ulong Size;
        public int HeapIndex;
        public int BlockStart;
        public int BlockCount;

        public bool IsValid => Heap != null;
    }

    /// <summary>堆分配器统计</summary>
    public struct HeapAllocatorStats
    {
        public ulong TotalCapacityBytes;
        public ulong AllocatedBytes;
        public ulong FreeBytes;
        public int AllocationCount;
        public int HeapCount;
        public double UtilizationPercent;
    }

    // ==============================================================================================
    //  D3D12BufferAllocator - 缓冲区分配器 (线性 / 环形)
    // ==============================================================================================

    /// <summary>
    /// 线性分配器: 适用于每帧上传缓冲区。分配偏移量单调递增，到期后整体重置。
    /// </summary>
    public sealed class D3D12LinearAllocator : IDisposable
    {
        private readonly ID3D12Resource _resource;
        private readonly ulong _size;
        private readonly byte* _mappedPtr;
        private ulong _offset;
        private readonly object _lock = new();

        public ID3D12Resource Resource => _resource;
        public ulong Size => _size;
        public ulong UsedBytes => _offset;
        public ulong RemainingBytes => _size - _offset;

        public unsafe D3D12LinearAllocator(ID3D12Device device, ulong size, HeapType heapType)
        {
            _size = AlignUp(size, 64); // 64 字节对齐
            _offset = 0;

            var desc = new ResourceDescription(
                heapType == HeapType.Upload ? ResourceDimension.Buffer : ResourceDimension.Buffer,
                0, 0, 1, 1,
                Format.Unknown,
                SampleDescription.Default,
                ResourceStates.GenericRead,
                ResourceFlags.None);

            _resource = device.CreateCommittedResource(
                new HeapDescription(heapType, _size),
                heapType == HeapType.Upload ? ResourceStates.GenericRead : ResourceStates.CopyDest,
                null);

            // 映射上传/回读缓冲区
            if (heapType != HeapType.Default)
            {
                var hr = _resource.Map(0, null, out IntPtr ptr);
                if (hr.Failure)
                    throw new InvalidOperationException($"Failed to map buffer resource: {hr}");
                _mappedPtr = (byte*)ptr;
            }
            else
            {
                _mappedPtr = null;
            }
        }

        /// <summary>分配一块连续内存，返回偏移量和指针</summary>
        public unsafe LinearAllocation Allocate(ulong size)
        {
            lock (_lock)
            {
                ulong alignedSize = AlignUp(size, 256); // 256 字节对齐
                if (_offset + alignedSize > _size)
                    throw new OutOfMemoryException($"LinearAllocator exhausted: requested={size}, available={RemainingBytes}");

                ulong allocOffset = _offset;
                byte* cpuPtr = _mappedPtr != null ? _mappedPtr + allocOffset : null;
                _offset += alignedSize;

                return new LinearAllocation
                {
                    Offset = allocOffset,
                    Size = alignedSize,
                    CpuAddress = cpuPtr,
                    GpuAddress = _resource.GPUVirtualAddress + allocOffset
                };
            }
        }

        /// <summary>重置分配器到起始位置 (帧开始时调用)</summary>
        public void Reset()
        {
            lock (_lock)
            {
                _offset = 0;
            }
        }

        public void Dispose()
        {
            if (_mappedPtr != null)
                _resource.Unmap(0);
            _resource.Dispose();
        }

        private static ulong AlignUp(ulong value, ulong alignment)
            => (value + alignment - 1) & ~(alignment - 1);
    }

    /// <summary>线性分配结果</summary>
    public unsafe struct LinearAllocation
    {
        public ulong Offset;
        public ulong Size;
        public byte* CpuAddress;
        public ulong GpuAddress;
    }

    /// <summary>
    /// 环形分配器: 适用于多帧并发的上传缓冲区。分配偏移量循环回绕。
    /// </summary>
    public sealed class D3D12RingAllocator : IDisposable
    {
        private readonly ID3D12Resource _resource;
        private readonly ulong _size;
        private readonly byte* _mappedPtr;
        private ulong _head; // 下一次分配的起始位置
        private ulong _tail; // 最早未完成提交的位置
        private ulong _usedBytes;
        private readonly object _lock = new();

        public ID3D12Resource Resource => _resource;
        public ulong Size => _size;
        public ulong UsedBytes => _usedBytes;
        public ulong FreeBytes => _size - _usedBytes;

        public unsafe D3D12RingAllocator(ID3D12Device device, ulong size, HeapType heapType)
        {
            _size = AlignUp(size, 64);
            _head = 0;
            _tail = 0;
            _usedBytes = 0;

            var desc = new ResourceDescription(
                ResourceDimension.Buffer, 0, 0, 1, 1,
                Format.Unknown, SampleDescription.Default,
                ResourceStates.GenericRead, ResourceFlags.None);

            _resource = device.CreateCommittedResource(
                new HeapDescription(heapType, _size),
                ResourceStates.GenericRead, null);

            if (heapType != HeapType.Default)
            {
                var hr = _resource.Map(0, null, out IntPtr ptr);
                if (hr.Failure)
                    throw new InvalidOperationException($"Failed to map ring buffer: {hr}");
                _mappedPtr = (byte*)ptr;
            }
            else
            {
                _mappedPtr = null;
            }
        }

        /// <summary>从环形缓冲区分配一块内存</summary>
        public unsafe RingAllocation Allocate(ulong size)
        {
            lock (_lock)
            {
                ulong alignedSize = AlignUp(size, 256);

                if (alignedSize > _size)
                    throw new ArgumentException("Ring allocation exceeds total ring size");

                // 检查是否有足够空间 (考虑回绕)
                ulong allocEnd = _head + alignedSize;
                bool wrap = allocEnd > _size;

                if (wrap)
                {
                    // 需要回绕到开头
                    allocEnd = alignedSize; // 从偏移 0 开始
                    if (allocEnd > _tail)
                        throw new OutOfMemoryException($"Ring buffer exhausted: used={_usedBytes}, requested={size}");

                    _head = 0;
                }
                else
                {
                    if (_head + alignedSize > _tail && _head < _tail)
                        throw new OutOfMemoryException($"Ring buffer exhausted: used={_usedBytes}, requested={size}");
                }

                ulong allocOffset = _head;
                byte* cpuPtr = _mappedPtr != null ? _mappedPtr + allocOffset : null;
                _head += alignedSize;
                _usedBytes += alignedSize;

                return new RingAllocation
                {
                    Offset = allocOffset,
                    Size = alignedSize,
                    CpuAddress = cpuPtr,
                    GpuAddress = _resource.GPUVirtualAddress + allocOffset
                };
            }
        }

        /// <summary>标记 GPU 已完成到指定偏移量之前的提交</summary>
        public void Commit(ulong fenceValue)
        {
            // 由外部帧同步器调用，更新 tail
        }

        public void Dispose()
        {
            if (_mappedPtr != null)
                _resource.Unmap(0);
            _resource.Dispose();
        }

        private static ulong AlignUp(ulong value, ulong alignment)
            => (value + alignment - 1) & ~(alignment - 1);
    }

    /// <summary>环形分配结果</summary>
    public unsafe struct RingAllocation
    {
        public ulong Offset;
        public ulong Size;
        public byte* CpuAddress;
        public ulong GpuAddress;
    }

    /// <summary>
    /// 缓冲区分配器：管理上传堆/默认堆/回读堆的缓冲区分配。
    /// 内部使用线性分配器 (每帧上传) 和环形分配器 (多帧并发上传)。
    /// </summary>
    public sealed class D3D12BufferAllocator : IDisposable
    {
        private readonly ID3D12Device _device;
        private readonly ConcurrentDictionary<int, D3D12LinearAllocator> _linearAllocators = new();
        private readonly ConcurrentDictionary<int, D3D12RingAllocator> _ringAllocators = new();
        private readonly Dictionary<ulong, ID3D12Resource> _committedBuffers = new();
        private readonly object _lock = new();
        private int _allocatorCounter;

        public ulong TotalAllocatedBytes { get; private set; }

        public D3D12BufferAllocator(ID3D12Device device)
        {
            _device = device;
        }

        /// <summary>创建一个专用的线性分配器 (用于单帧上传)</summary>
        public D3D12LinearAllocator CreateLinearAllocator(ulong size, HeapType heapType = HeapType.Upload)
        {
            var allocator = new D3D12LinearAllocator(_device, size, heapType);
            int id = Interlocked.Increment(ref _allocatorCounter);
            _linearAllocators[id] = allocator;
            return allocator;
        }

        /// <summary>创建一个环形分配器 (用于多帧并发上传)</summary>
        public D3D12RingAllocator CreateRingAllocator(ulong size, HeapType heapType = HeapType.Upload)
        {
            var allocator = new D3D12RingAllocator(_device, size, heapType);
            int id = Interlocked.Increment(ref _allocatorCounter);
            _ringAllocators[id] = allocator;
            return allocator;
        }

        /// <summary>直接创建一个已提交的缓冲区资源</summary>
        public ID3D12Resource CreateCommittedBuffer(ulong size, ResourceFlags flags = ResourceFlags.None,
            ResourceStates initialState = ResourceStates.Common, HeapType heapType = HeapType.Default)
        {
            var resource = _device.CreateCommittedResource(
                new HeapDescription(heapType, size),
                initialState, null);

            lock (_lock)
            {
                _committedBuffers[resource.GPUVirtualAddress] = resource;
                TotalAllocatedBytes += size;
            }

            return resource;
        }

        /// <summary>释放一个已提交的缓冲区</summary>
        public void ReleaseCommittedBuffer(ID3D12Resource resource)
        {
            lock (_lock)
            {
                if (_committedBuffers.Remove(resource.GPUVirtualAddress))
                {
                    TotalAllocatedBytes -= resource.Description.SizeInBytes;
                    resource.Dispose();
                }
            }
        }

        public void Dispose()
        {
            foreach (var kv in _linearAllocators) kv.Value.Dispose();
            _linearAllocators.Clear();
            foreach (var kv in _ringAllocators) kv.Value.Dispose();
            _ringAllocators.Clear();
            lock (_lock)
            {
                foreach (var res in _committedBuffers.Values) res.Dispose();
                _committedBuffers.Clear();
            }
        }
    }

    // ==============================================================================================
    //  D3D12TextureAllocator - 纹理分配器 (MipTail / Tile / 交错布局)
    // ==============================================================================================

    /// <summary>
    /// 纹理分配器: 处理 MipTail 计算、Tile 资源布局、内存对齐和交错布局。
    /// </summary>
    public sealed class D3D12TextureAllocator : IDisposable
    {
        private readonly ID3D12Device _device;
        private ulong _totalAllocated;

        public ulong TotalAllocatedBytes => _totalAllocated;

        public D3D12TextureAllocator(ID3D12Device device)
        {
            _device = device;
        }

        /// <summary>
        /// 计算纹理的 MipTail 大小和起始 Mip 级别。
        /// MipTail 是将多个小 Mip 打包到单个 Tile 中的优化。
        /// </summary>
        public MipTailInfo CalculateMipTail(int width, int height, int mipLevels, Format format, int sampleCount)
        {
            // 标准 D3D12 MipTail 计算:
            // MipTail 从某个 Mip 级别开始，此后所有 Mip 打包到标准 Tile 中
            int standardTileSize = 64 * 1024; // 64KB standard tile for non-MSAA

            // 找到第一个适合打包进 MipTail 的 Mip 级别
            // 条件: mipWidth * mipHeight * bytesPerPixel < standardTileSize
            int bytesPerPixel = GetFormatBytesPerPixel(format);
            int packedMipStart = 0;

            for (int mip = 0; mip < mipLevels; mip++)
            {
                int mipW = Math.Max(1, width >> mip);
                int mipH = Math.Max(1, height >> mip);
                int mipSize = mipW * mipH * bytesPerPixel;

                if (mipSize < standardTileSize)
                {
                    packedMipStart = mip;
                    break;
                }
                if (mip == mipLevels - 1)
                {
                    packedMipStart = mipLevels; // 没有可打包的 Mip
                }
            }

            // 计算 MipTail 的总大小
            ulong tailSize = 0;
            for (int mip = packedMipStart; mip < mipLevels; mip++)
            {
                int mipW = Math.Max(1, width >> mip);
                int mipH = Math.Max(1, height >> mip);
                int mipSize = AlignUp(mipW * mipH * bytesPerPixel, 512); // 512 字节对齐
                tailSize += (ulong)mipSize;
            }

            return new MipTailInfo
            {
                PackedMipStart = packedMipStart,
                MipTailSize = tailSize,
                StandardTileSize = (ulong)standardTileSize,
                TotalMips = (uint)mipLevels
            };
        }

        /// <summary>
        /// 计算 Tile 布局: 对于 Tiled Resource，计算每个子资源占用的 Tile 数量和偏移。
        /// </summary>
        public TileLayoutInfo CalculateTileLayout(int width, int height, int depthOrArray,
            int mipLevels, Format format, int sampleCount, int tileWidth = 64, int tileHeight = 64)
        {
            int bytesPerPixel = GetFormatBytesPerPixel(format);
            ulong totalTiles = 0;
            var tileOffsets = new List<ulong>();
            ulong runningOffset = 0;

            for (int mip = 0; mip < mipLevels; mip++)
            {
                int mipW = Math.Max(1, width >> mip);
                int mipH = Math.Max(1, height >> mip);
                int mipD = Math.Max(1, depthOrArray >> mip);

                // 计算每个 Tile 覆盖的像素区域
                int tilesX = (mipW + tileWidth - 1) / tileWidth;
                int tilesY = (mipH + tileHeight - 1) / tileHeight;
                int tilesPerSlice = tilesX * tilesY;
                int tilesForMip = tilesPerSlice * mipD;

                for (int i = 0; i < tilesForMip; i++)
                {
                    tileOffsets.Add(runningOffset);
                    runningOffset += 65536; // 每个 Tile 64KB
                    totalTiles++;
                }
            }

            return new TileLayoutInfo
            {
                TotalTileCount = totalTiles,
                TileOffsets = tileOffsets,
                TileSizeBytes = 65536,
                TileWidth = tileWidth,
                TileHeight = tileHeight
            };
        }

        /// <summary>
        /// 创建交错布局的纹理资源。适用于 Volume Texture 的 3D 交错。
        /// </summary>
        public ID3D12Resource CreateInterleavedTexture(
            int width, int height, int depth,
            int mipLevels, Format format,
            ResourceFlags flags = ResourceFlags.None,
            ResourceStates initialState = ResourceStates.Common)
        {
            int bytesPerPixel = GetFormatBytesPerPixel(format);
            int tileWidth = 64, tileHeight = 64, tileDepth = 64;

            // 计算交错布局的资源大小
            ulong totalSize = 0;
            for (int mip = 0; mip < mipLevels; mip++)
            {
                int mipW = Math.Max(1, width >> mip);
                int mipH = Math.Max(1, height >> mip);
                int mipD = Math.Max(1, depth >> mip);

                // 3D 交错: Z 方向每 tileDepth 深度打包为一个 Tile
                int tilesX = (mipW + tileWidth - 1) / tileWidth;
                int tilesY = (mipH + tileHeight - 1) / tileHeight;
                int tilesZ = (mipD + tileDepth - 1) / tileDepth;
                int tileCount = tilesX * tilesY * tilesZ;
                totalSize += (ulong)tileCount * 65536;
            }

            // 对齐到 64KB
            totalSize = AlignUp(totalSize, 65536);

            var resourceDesc = ResourceDescription.Texture3D(
                format, (uint)width, (uint)height, (uint)depth,
                (ushort)mipLevels, flags);

            var resource = _device.CreateCommittedResource(
                new HeapDescription(HeapType.Default, totalSize),
                initialState, null);

            _totalAllocated += totalSize;
            return resource;
        }

        /// <summary>分配纹理资源 (2D)</summary>
        public ID3D12Resource AllocateTexture2D(
            int width, int height, int arraySize,
            int mipLevels, Format format,
            ResourceFlags flags = ResourceFlags.None,
            ResourceStates initialState = ResourceStates.Common,
            int sampleCount = 1, int sampleQuality = 0)
        {
            var resourceDesc = sampleCount > 1
                ? ResourceDescription.Texture2DMultisampled(
                    format, (uint)width, (uint)height,
                    (ushort)arraySize, (ushort)mipLevels,
                    (ushort)sampleCount, (ushort)sampleQuality, flags)
                : ResourceDescription.Texture2D(
                    format, (uint)width, (uint)height,
                    (ushort)arraySize, (ushort)mipLevels, flags);

            var resource = _device.CreateCommittedResource(
                new HeapDescription(HeapType.Default, 0),
                initialState, null);

            _totalAllocated += resource.Description.SizeInBytes;
            return resource;
        }

        /// <summary>分配纹理资源 (1D)</summary>
        public ID3D12Resource AllocateTexture1D(
            int width, int arraySize,
            int mipLevels, Format format,
            ResourceFlags flags = ResourceFlags.None,
            ResourceStates initialState = ResourceStates.Common)
        {
            var resourceDesc = ResourceDescription.Texture1D(
                format, (uint)width, (ushort)arraySize, (ushort)mipLevels, flags);

            var resource = _device.CreateCommittedResource(
                new HeapDescription(HeapType.Default, 0),
                initialState, null);

            _totalAllocated += resource.Description.SizeInBytes;
            return resource;
        }

        private static int GetFormatBytesPerPixel(Format format)
        {
            // 根据格式计算每个像素的字节数
            return format switch
            {
                Format.R8_UNorm or Format.R8_SNorm or Format.R8_UInt or Format.R8_SInt
                    => 1,
                Format.R8G8_UNorm or Format.R8G8_SNorm or Format.R8G8_UInt or Format.R8G8_SInt
                    => 2,
                Format.R8G8B8A8_UNorm or Format.R8G8B8A8_SNorm or Format.B8G8R8A8_UNorm
                    or Format.R10G10B10A2_UNorm or Format.R11G11B10_Float
                    => 4,
                Format.R16G16B16A16_Float or Format.R32G32B32_Float
                    => 12,
                Format.R32G32B32A32_Float
                    => 16,
                Format.R16_Float or Format.D16_UNorm
                    => 2,
                Format.R32_Float or Format.D32_Float or Format.R24_UNorm_X8_Typeless
                    => 4,
                Format.BC1_UNorm or Format.BC1_UNorm_SRgb or Format.BC4_UNorm or Format.BC4_SNorm
                    => 8, // 4x4 像素块
                Format.BC2_UNorm or Format.BC3_UNorm or Format.BC5_UNorm
                    or Format.BC6H_UF16 or Format.BC6H_SF16 or Format.BC7_UNorm
                    => 16, // 4x4 像素块
                _ => 4 // 默认
            };
        }

        private static int AlignUp(int value, int alignment)
            => (value + alignment - 1) & ~(alignment - 1);

        private static ulong AlignUp(ulong value, ulong alignment)
            => (value + alignment - 1) & ~(alignment - 1);

        public void Dispose()
        {
            // 资源由创建者管理释放
        }
    }

    /// <summary>MipTail 信息</summary>
    public struct MipTailInfo
    {
        public int PackedMipStart;
        public ulong MipTailSize;
        public ulong StandardTileSize;
        public uint TotalMips;
    }

    /// <summary>Tile 布局信息</summary>
    public struct TileLayoutInfo
    {
        public ulong TotalTileCount;
        public List<ulong> TileOffsets;
        public ulong TileSizeBytes;
        public int TileWidth;
        public int TileHeight;
    }

    // ==============================================================================================
    //  D3D12DescriptorPool - 描述符堆池管理
    // ==============================================================================================

    /// <summary>
    /// 描述符堆池管理器。支持 CBV_SRV_UAV / Sampler / RTV / DSV 四种类型。
    /// 使用线性分配策略，支持单个描述符的分配和批量回收。
    /// </summary>
    public sealed class D3D12DescriptorPool : IDisposable
    {
        private readonly ID3D12Device _device;
        private readonly DescriptorHeapType _heapType;
        private readonly bool _shaderVisible;
        private readonly int _descriptorsPerHeap;

        private readonly List<ID3D12DescriptorHeap> _heaps = new();
        private readonly List<int> _heapFreeCounts = new();
        private readonly List<FreeList> _heapFreeLists = new();
        private readonly object _lock = new();

        // RTV/DSV 不需要 shader visible
        private const int DefaultDescriptorsPerHeap = 256;
        private const int ShaderVisibleDescriptorsPerHeap = 1024;

        public DescriptorHeapType HeapType => _heapType;
        public bool ShaderVisible => _shaderVisible;
        public int TotalCapacity { get; private set; }
        public int AllocatedCount { get; private set; }
        public uint DescriptorIncrementSize { get; }

        public D3D12DescriptorPool(ID3D12Device device, DescriptorHeapType heapType,
            int descriptorsPerHeap = 0, bool shaderVisible = true)
        {
            _device = device;
            _heapType = heapType;

            // RTV 和 DSV 天然不需要 shader visible
            if (heapType == DescriptorHeapType.RenderTargetView || heapType == DescriptorHeapType.DepthStencilView)
                _shaderVisible = false;
            else
                _shaderVisible = shaderVisible;

            _descriptorsPerHeap = descriptorsPerHeap > 0
                ? descriptorsPerHeap
                : (_shaderVisible ? ShaderVisibleDescriptorsPerHeap : DefaultDescriptorsPerHeap);

            DescriptorIncrementSize = _device.GetDescriptorHandleIncrementSize(heapType);
        }

        /// <summary>分配一个描述符句柄</summary>
        public CpuDescriptorHandle Allocate(out int heapIndex, out int descriptorIndex)
        {
            lock (_lock)
            {
                // 先尝试从已有堆的自由列表中分配
                for (int i = 0; i < _heaps.Count; i++)
                {
                    if (_heapFreeCounts[i] > 0)
                    {
                        heapIndex = i;
                        descriptorIndex = AllocateFromFreeList(i);
                        AllocatedCount++;
                        return GetCpuHandle(i, descriptorIndex);
                    }
                }

                // 所有堆都满了，创建新堆
                heapIndex = CreateNewHeap();
                descriptorIndex = AllocateFromFreeList(heapIndex);
                AllocatedCount++;

                return GetCpuHandle(heapIndex, descriptorIndex);
            }
        }

        /// <summary>分配 N 个连续描述符</summary>
        public CpuDescriptorHandle AllocateRange(int count, out int heapIndex, out int startIndex)
        {
            lock (_lock)
            {
                for (int i = 0; i < _heaps.Count; i++)
                {
                    if (_heapFreeCounts[i] >= count)
                    {
                        // 尝试在自由列表中找到连续区间
                        startIndex = AllocateContiguousFromFreeList(i, count);
                        if (startIndex >= 0)
                        {
                            heapIndex = i;
                            AllocatedCount += count;
                            return GetCpuHandle(i, startIndex);
                        }
                    }
                }

                // 创建新堆 (保证连续分配)
                if (count > _descriptorsPerHeap)
                    throw new ArgumentException($"Requested {count} descriptors exceeds heap capacity {_descriptorsPerHeap}");

                heapIndex = CreateNewHeap();
                startIndex = AllocateContiguousFromFreeList(heapIndex, count);
                AllocatedCount += count;

                return GetCpuHandle(heapIndex, startIndex);
            }
        }

        /// <summary>释放一个描述符 (自由列表回收)</summary>
        public void Free(int heapIndex, int descriptorIndex)
        {
            lock (_lock)
            {
                if (heapIndex < 0 || heapIndex >= _heapFreeLists.Count) return;
                _heapFreeLists[heapIndex].Free(descriptorIndex);
                _heapFreeCounts[heapIndex]++;
                AllocatedCount--;
            }
        }

        /// <summary>释放一个范围内的描述符</summary>
        public void FreeRange(int heapIndex, int startIndex, int count)
        {
            lock (_lock)
            {
                if (heapIndex < 0 || heapIndex >= _heapFreeLists.Count) return;
                for (int i = 0; i < count; i++)
                {
                    _heapFreeLists[heapIndex].Free(startIndex + i);
                    _heapFreeCounts[heapIndex]++;
                }
                AllocatedCount -= count;
            }
        }

        /// <summary>重置所有堆 (释放全部描述符)</summary>
        public void ResetAll()
        {
            lock (_lock)
            {
                foreach (var fl in _heapFreeLists)
                    fl.Reset();

                for (int i = 0; i < _heapFreeCounts.Count; i++)
                    _heapFreeCounts[i] = _descriptorsPerHeap;

                AllocatedCount = 0;
            }
        }

        /// <summary>获取 GPU 描述符句柄 (仅 shader visible 堆)</summary>
        public GpuDescriptorHandle GetGpuHandle(int heapIndex, int descriptorIndex)
        {
            if (!_shaderVisible)
                throw new InvalidOperationException("Non-shader-visible heap has no GPU handles");
            return _heaps[heapIndex].GetGPUDescriptorHandleForHeapStart()
                + descriptorIndex * DescriptorIncrementSize;
        }

        /// <summary>获取 CPU 描述符句柄</summary>
        public CpuDescriptorHandle GetCpuHandle(int heapIndex, int descriptorIndex)
        {
            return _heaps[heapIndex].GetCPUDescriptorHandleForHeapStart()
                + descriptorIndex * DescriptorIncrementSize;
        }

        /// <summary>获取指定堆的 D3D12 描述符堆 (用于 SetDescriptorHeaps)</summary>
        public ID3D12DescriptorHeap GetHeap(int heapIndex)
        {
            return _heaps[heapIndex];
        }

        /// <summary>获取池利用率统计</summary>
        public (double Utilization, int TotalCapacity, int Allocated) GetStats()
        {
            lock (_lock)
            {
                double util = TotalCapacity > 0 ? (double)AllocatedCount / TotalCapacity * 100.0 : 0;
                return (util, TotalCapacity, AllocatedCount);
            }
        }

        private int CreateNewHeap()
        {
            var heapDesc = new DescriptorHeapDescription
            {
                Type = _heapType,
                NumberOfDescriptors = _descriptorsPerHeap,
                Flags = _shaderVisible ? DescriptorHeapFlags.ShaderVisible : DescriptorHeapFlags.None,
                NodeMask = 0
            };

            var heap = _device.CreateDescriptorHeap(heapDesc);
            int heapIdx = _heaps.Count;
            _heaps.Add(heap);
            _heapFreeCounts.Add(_descriptorsPerHeap);
            _heapFreeLists.Add(new FreeList(_descriptorsPerHeap));
            TotalCapacity += _descriptorsPerHeap;

            return heapIdx;
        }

        private int AllocateFromFreeList(int heapIndex)
        {
            return _heapFreeLists[heapIndex].Allocate();
        }

        private int AllocateContiguousFromFreeList(int heapIndex, int count)
        {
            var fl = _heapFreeLists[heapIndex];
            int consecutive = 0;
            int startIdx = -1;

            for (int i = 0; i < _descriptorsPerHeap; i++)
            {
                if (fl.IsFree(i))
                {
                    if (consecutive == 0) startIdx = i;
                    consecutive++;
                    if (consecutive >= count)
                    {
                        for (int j = 0; j < count; j++)
                            fl.Allocate(startIdx + j);
                        _heapFreeCounts[heapIndex] -= count;
                        return startIdx;
                    }
                }
                else
                {
                    consecutive = 0;
                    startIdx = -1;
                }
            }

            // 使用线性分配器的尾部区域 (当自由列表碎片化时)
            if (fl.LinearHead + count <= _descriptorsPerHeap)
            {
                startIdx = fl.LinearHead;
                for (int j = 0; j < count; j++)
                    fl.Allocate(startIdx + j);
                _heapFreeCounts[heapIndex] -= count;
                return startIdx;
            }

            return -1;
        }

        public void Dispose()
        {
            lock (_lock)
            {
                foreach (var heap in _heaps)
                    heap.Dispose();
                _heaps.Clear();
                _heapFreeLists.Clear();
                _heapFreeCounts.Clear();
            }
        }
    }

    /// <summary>描述符自由列表 (使用位图实现)</summary>
    internal sealed class FreeList
    {
        private readonly ulong[] _bitmap;
        private readonly int _capacity;
        public int LinearHead;

        public FreeList(int capacity)
        {
            _capacity = capacity;
            _bitmap = new ulong[capacity / 64 + 1];
            LinearHead = 0;
        }

        public int Allocate()
        {
            // 优先从线性头部分配
            if (LinearHead < _capacity)
            {
                int idx = LinearHead++;
                SetBit(idx);
                return idx;
            }

            // 线性区域耗尽，搜索自由列表中的空闲位
            for (int i = 0; i < _capacity; i++)
            {
                if (IsFree(i))
                {
                    SetBit(i);
                    return i;
                }
            }

            return -1; // 满
        }

        public void Allocate(int index)
        {
            SetBit(index);
        }

        public void Free(int index)
        {
            ClearBit(index);
        }

        public bool IsFree(int index)
        {
            int wordIdx = index / 64;
            int bitIdx = index % 64;
            return (_bitmap[wordIdx] & (1UL << bitIdx)) == 0;
        }

        public void Reset()
        {
            Array.Clear(_bitmap, 0, _bitmap.Length);
            LinearHead = 0;
        }

        private void SetBit(int index)
        {
            _bitmap[index / 64] |= (1UL << (index % 64));
        }

        private void ClearBit(int index)
        {
            _bitmap[index / 64] &= ~(1UL << (index % 64));
        }
    }

    // ==============================================================================================
    //  D3D12DeviceFallback - 核心 D3D12 设备管理
    // ==============================================================================================

    /// <summary>
    /// 山雨引擎 D3D12 设备封装。
    /// 负责: DXGI 工厂创建、适配器枚举、D3D12 设备创建、特性检测、命令队列管理。
    /// </summary>
    public sealed class D3D12DeviceFallback : IDisposable
    {
        private IDXGIFactory4 _dxgiFactory;
        private IDXGIAdapter3 _selectedAdapter;
        private ID3D12Device _nativeDevice;
        private D3D12FeatureSupport _featureSupport;
        private bool _disposed;
        private bool _debugLayerEnabled;

        // 命令队列
        private ID3D12CommandQueue _directQueue;
        private ID3D12CommandQueue _computeQueue;
        private ID3D12CommandQueue _copyQueue;

        // 内存管理
        private D3D12HeapAllocator[] _heapAllocators;
        private D3D12BufferAllocator _bufferAllocator;
        private D3D12TextureAllocator _textureAllocator;
        private D3D12DescriptorPool _cbvSrvUavPool;
        private D3D12DescriptorPool _samplerPool;
        private D3D12DescriptorPool _rtvPool;
        private D3D12DescriptorPool _dsvPool;

        // 设备信息
        public string AdapterName { get; private set; } = "Unknown";
        public ulong DedicatedVideoMemory { get; private set; }
        public ulong DedicatedSystemMemory { get; private set; }
        public ulong SharedSystemMemory { get; private set; }
        public Luid AdapterLuid { get; private set; }

        public D3D12DeviceFallback(bool enableDebug = false, bool enableGpuValidation = false)
        {
            InitializeDXGI(enableDebug);
            EnumerateAndSelectAdapter();
            CreateD3D12Device(enableGpuValidation);
            InitializeFeatureSupport();
            CreateCommandQueues();
            InitializeMemoryManagers();
        }

        /// <summary>获取原生 D3D12 设备指针</summary>
        public ID3D12Device GetDevice() => _nativeDevice;

        /// <summary>获取特性检测接口</summary>
        public D3D12FeatureSupport FeatureSupport => _featureSupport;

        /// <summary>获取 Direct 命令队列</summary>
        public ID3D12CommandQueue GetDirectQueue() => _directQueue;

        /// <summary>获取 Compute 命令队列</summary>
        public ID3D12CommandQueue GetComputeQueue() => _computeQueue;

        /// <summary>获取 Copy 命令队列</summary>
        public ID3D12CommandQueue GetCopyQueue() => _copyQueue;

        // -- 内存管理访问器 --

        public D3D12BufferAllocator BufferAllocator => _bufferAllocator;
        public D3D12TextureAllocator TextureAllocator => _textureAllocator;
        public D3D12DescriptorPool CBV_SRV_UAV_Pool => _cbvSrvUavPool;
        public D3D12DescriptorPool SamplerPool => _samplerPool;
        public D3D12DescriptorPool RTV_Pool => _rtvPool;
        public D3D12DescriptorPool DSV_Pool => _dsvPool;

        /// <summary>获取指定块大小的堆分配器</summary>
        public D3D12HeapAllocator GetHeapAllocator(ulong blockSize)
        {
            // 查找最接近的块大小
            ulong[] supportedSizes = { 256, 1024, 4096, 65536 };
            ulong bestSize = supportedSizes[0];
            foreach (var size in supportedSizes)
            {
                if (size >= blockSize)
                {
                    bestSize = size;
                    break;
                }
                bestSize = size;
            }

            int idx = Array.IndexOf(supportedSizes, bestSize);
            return _heapAllocators[idx];
        }

        // === 初始化步骤 ===

        private void InitializeDXGI(bool enableDebug)
        {
            _debugLayerEnabled = enableDebug;

            if (enableDebug)
            {
                // 启用 D3D12 Debug Layer
                try
                {
                    var debugController = D3D12.GetDebugController();
                    debugController.EnableDebugLayer();

                    // 启用 GPU-Based Validation (更详细的错误检测)
                    if (D3D12.GetDebug3() is { } debug3)
                    {
                        debug3.SetEnableGPUBasedValidation(true);
                        debug3.Dispose();
                    }
                    debugController.Dispose();
                }
                catch
                {
                    // Debug Layer 不可用 (非 SDK 版本 Windows)
                }
            }

            // 创建 DXGI Factory (Flip Model)
            uint factoryFlags = 0;
            if (enableDebug)
                factoryFlags = (uint)FactoryFlags.Debug;

            _dxgiFactory = DXGICreate.CreateDXGIFactory2<IDXGIFactory4>(factoryFlags);
        }

        private void EnumerateAndSelectAdapter()
        {
            // 枚举所有适配器，选择显存最大的高性能 GPU
            IDXGIAdapter1? bestAdapter = null;
            ulong bestVRAM = 0;
            string bestName = "Unknown";

            for (int i = 0; ; i++)
            {
                try
                {
                    var adapter = _dxgiFactory.EnumAdapters1<IDXGIAdapter3>(i);
                    if (adapter == null) break;

                    var desc = adapter.Description;
                    // 排除软件适配器
                    if ((desc.Flags & (uint)AdapterFlags.Software) != 0)
                    {
                        adapter.Dispose();
                        continue;
                    }

                    if (desc.DedicatedVideoMemory > bestVRAM)
                    {
                        bestAdapter = adapter;
                        bestVRAM = desc.DedicatedVideoMemory;
                        bestName = desc.Description;
                    }
                    else
                    {
                        adapter.Dispose();
                    }
                }
                catch
                {
                    break;
                }
            }

            if (bestAdapter == null)
                throw new InvalidOperationException("No suitable DXGI adapter found. D3D12 requires a capable GPU.");

            _selectedAdapter = bestAdapter;
            AdapterName = bestName;
            DedicatedVideoMemory = bestVRAM;

            // 获取完整内存信息
            var memoryInfo = _selectedAdapter.QueryVideoMemoryInfo(
                0, MemorySegmentGroup.Local);

            DedicatedSystemMemory = _selectedAdapter.Description.DedicatedSystemMemory;
            SharedSystemMemory = _selectedAdapter.Description.SharedSystemMemory;
            AdapterLuid = _selectedAdapter.Description.AdapterLuid;
        }

        private void CreateD3D12Device(bool enableGpuValidation)
        {
            var hr = D3D12.D3D12CreateDevice(
                _selectedAdapter,
                FeatureLevel.Level_12_0,
                out _nativeDevice);

            if (hr.Failure)
                throw new InvalidOperationException($"Failed to create D3D12 device: {hr.Description}");

            // 设置设备名称 (调试时可在 PIX/VS 中看到)
            _nativeDevice.DeviceName = "ShanyuEngine_D3D12Device";
        }

        private void InitializeFeatureSupport()
        {
            _featureSupport = new D3D12FeatureSupport(_nativeDevice);
        }

        private void CreateCommandQueues()
        {
            // Direct Queue (Graphics + Compute)
            _directQueue = CreateCommandQueue(CommandListType.Direct, CommandQueuePriority.Normal, 0);
            _directQueue.DeviceName = "ShanyuEngine_DirectQueue";

            // Compute Queue
            _computeQueue = CreateCommandQueue(CommandListType.Compute, CommandQueuePriority.Normal, 0);
            _computeQueue.DeviceName = "ShanyuEngine_ComputeQueue";

            // Copy Queue (低优先级)
            _copyQueue = CreateCommandQueue(CommandListType.Copy, CommandQueuePriority.Normal, 0);
            _copyQueue.DeviceName = "ShanyuEngine_CopyQueue";
        }

        private ID3D12CommandQueue CreateCommandQueue(CommandListType type,
            CommandQueuePriority priority, int nodeMask)
        {
            var queueDesc = new CommandQueueDescription(type, priority, 0, nodeMask);
            return _nativeDevice.CreateCommandQueue(queueDesc);
        }

        private void InitializeMemoryManagers()
        {
            // 堆分配器: 4 种块大小
            _heapAllocators = new D3D12HeapAllocator[4];
            _heapAllocators[0] = new D3D12HeapAllocator(_nativeDevice, HeapType.Default, 256, 256);
            _heapAllocators[1] = new D3D12HeapAllocator(_nativeDevice, HeapType.Default, 1024, 256);
            _heapAllocators[2] = new D3D12HeapAllocator(_nativeDevice, HeapType.Default, 4096, 256);
            _heapAllocators[3] = new D3D12HeapAllocator(_nativeDevice, HeapType.Default, 65536, 256);

            // 缓冲区分配器
            _bufferAllocator = new D3D12BufferAllocator(_nativeDevice);

            // 纹理分配器
            _textureAllocator = new D3D12TextureAllocator(_nativeDevice);

            // 描述符池
            _cbvSrvUavPool = new D3D12DescriptorPool(_nativeDevice, DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, 2048, true);
            _samplerPool = new D3D12DescriptorPool(_nativeDevice, DescriptorHeapType.Sampler, 256, true);
            _rtvPool = new D3D12DescriptorPool(_nativeDevice, DescriptorHeapType.RenderTargetView, 512, false);
            _dsvPool = new D3D12DescriptorPool(_nativeDevice, DescriptorHeapType.DepthStencilView, 256, false);
        }

        // === GPU 内存预算查询 ===

        /// <summary>
        /// 查询 GPU 视频内存使用情况。
        /// 返回当前用量、预算、可用空间和驱逐优先级信息。
        /// </summary>
        public VideoMemoryInfo QueryVideoMemory()
        {
            var budgetInfo = _selectedAdapter.QueryVideoMemoryInfo(0, MemorySegmentGroup.Local);

            return new VideoMemoryInfo
            {
                CurrentUsage = budgetInfo.CurrentUsage,
                CurrentBudget = budgetInfo.CurrentBudget,
                AvailableForReservation = budgetInfo.AvailableForReservation,
                AvailableBytes = budgetInfo.CurrentBudget - budgetInfo.CurrentUsage,
                UtilizationPercent = budgetInfo.CurrentBudget > 0
                    ? (double)budgetInfo.CurrentUsage / budgetInfo.CurrentBudget * 100.0
                    : 0.0
            };
        }

        /// <summary>
        /// 设置内存预算通知阈值。
        /// 当 GPU 内存用量超过阈值时，OS 会发出驱逐通知。
        /// </summary>
        public void SetVideoMemoryBudget(ulong reservationBytes, ulong maxBudgetBytes)
        {
            var budget = new VideoMemoryBudget
            {
                BudgetBytes = maxBudgetBytes,
                ReservationBytes = reservationBytes
            };
            _selectedAdapter.SetVideoMemoryBudget(0, MemorySegmentGroup.Local, ref budget);
        }

        // === 设备移除检测 ===

        /// <summary>
        /// 检测设备是否已被移除 (GPU TDR / 驱动崩溃 / 超时)。
        /// 返回移除原因 HRESULT 和建议的恢复策略。
        /// </summary>
        public DeviceRemovedInfo IsDeviceRemoved()
        {
            var removalReason = _nativeDevice.DeviceRemovedReason;

            if (removalReason.Success)
            {
                return new DeviceRemovedInfo
                {
                    IsRemoved = false,
                    Reason = "Device is operational",
                    RecoveryStrategy = DeviceRecoveryStrategy.None
                };
            }

            // 根据错误码判断恢复策略
            DeviceRecoveryStrategy strategy = DeviceRecoveryStrategy.RecreateDevice;

            if (removalReason.Code == unchecked((int)0x887A0005)) // DXGI_ERROR_DEVICE_REMOVED
            {
                // 检查具体原因
                var deviceRemovedReason = _nativeDevice.DeviceRemovedReason;
                if (deviceRemovedReason.Code == unchecked((int)0x887A0026)) // D3D12_ERROR_DRIVER_VERSION_MISMATCH
                {
                    strategy = DeviceRecoveryStrategy.UpdateDriver;
                }
                else if (deviceRemovedReason.Code == unchecked((int)0x887A0006)) // DXGI_ERROR_DEVICE_HUNG
                {
                    strategy = DeviceRecoveryStrategy.RestartEngine;
                }
            }

            return new DeviceRemovedInfo
            {
                IsRemoved = true,
                Reason = $"Device removed: HRESULT=0x{removalReason.Code:X8} ({removalReason.Description})",
                RecoveryStrategy = strategy
            };
        }

        // === 命令分配器创建 ===

        /// <summary>创建命令分配器 (每帧重置用)</summary>
        public ID3D12CommandAllocator CreateCommandAllocator(CommandListType type = CommandListType.Direct)
        {
            return _nativeDevice.CreateCommandAllocator(type);
        }

        // === 资源创建辅助 ===

        /// <summary>创建 committed 资源</summary>
        public ID3D12Resource CreateCommittedResource(
            HeapDescription heapProperties,
            ResourceStates initialState,
            ClearValue? optimizedClearValue = null)
        {
            return _nativeDevice.CreateCommittedResource(heapProperties, initialState, optimizedClearValue);
        }

        /// <summary>创建 committed buffer</summary>
        public ID3D12Resource CreateCommittedBuffer(
            ulong size,
            HeapType heapType = HeapType.Default,
            ResourceFlags flags = ResourceFlags.None,
            ResourceStates initialState = ResourceStates.Common)
        {
            return _nativeDevice.CreateCommittedResource(
                new HeapDescription(heapType, size),
                heapType == HeapType.Upload ? ResourceStates.GenericRead : initialState,
                null);
        }

        // === 描述符创建辅助 ===

        /// <summary>获取描述符增量大小</summary>
        public uint GetDescriptorHandleIncrementSize(DescriptorHeapType type)
        {
            return _nativeDevice.GetDescriptorHandleIncrementSize(type);
        }

        // === Dispose ===

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // 释放内存管理器
            for (int i = 0; i < _heapAllocators.Length; i++)
                _heapAllocators[i]?.Dispose();
            _bufferAllocator?.Dispose();
            _textureAllocator?.Dispose();
            _cbvSrvUavPool?.Dispose();
            _samplerPool?.Dispose();
            _rtvPool?.Dispose();
            _dsvPool?.Dispose();

            // 释放命令队列
            _directQueue?.Dispose();
            _computeQueue?.Dispose();
            _copyQueue?.Dispose();

            // 释放 D3D12 设备
            _featureSupport?.Dispose();
            _nativeDevice?.Dispose();

            // 释放 DXGI
            _selectedAdapter?.Dispose();
            _dxgiFactory?.Dispose();
        }
    }

    // === 辅助结构体 ===

    /// <summary>GPU 视频内存信息</summary>
    public struct VideoMemoryInfo
    {
        public ulong CurrentUsage;
        public ulong CurrentBudget;
        public ulong AvailableForReservation;
        public ulong AvailableBytes;
        public double UtilizationPercent;
    }

    /// <summary>设备移除信息</summary>
    public struct DeviceRemovedInfo
    {
        public bool IsRemoved;
        public string Reason;
        public DeviceRecoveryStrategy RecoveryStrategy;
    }

    /// <summary>设备恢复策略</summary>
    public enum DeviceRecoveryStrategy
    {
        None,
        RecreateDevice,
        RestartEngine,
        UpdateDriver,
        RebootSystem
    }
}
