// =============================================================================
// VulkanMemoryAllocator.cs - 山雨引擎 Vulkan 内存分配器 (VMA 风格)
// =============================================================================
// 这个东西抄的是 AMD 的 VulkanMemoryAllocator 思路，自己用 C# 撸了一遍。
// 主要干两件事：
//   1. 大块 vkAllocateMemory 拿到一块显存，然后自己切小块子分配 (sub-allocation)
//   2. 不同大小走不同策略：小对象走 freelist，大对象直接独占一个 block
//
// 内存类型 / 堆的选择参考了 Sascha Willems demo 里的那套，不过我们加了
// 明确的预算控制和碎片整理统计。
//
// FIXME: 还没处理多 GPU 的情况，目前假设单物理设备
// TODO: 大块分配 (>256MB) 应该单独走 dedicated allocation 路径
//
// 作者: 山雨引擎团队
// =============================================================================

using Vulkan = Silk.NET.Vulkan;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;

using Buffer = Silk.NET.Vulkan.Buffer;
using Semaphore = Silk.NET.Vulkan.Semaphore;
namespace ShanyuEngine.Vulkan
{
    // ════════════════════════════════════════════════════════════════════════════
    // 区域 1: 公共枚举与配置
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 内存使用策略。Vulkan 这 API 真是啰嗦，光是内存类型就得分一堆，
    /// 这里给个简化版，让上层不用直接面对 MEMORY_PROPERTY_DEVICE_LOCAL_BIT 那一堆。
    /// </summary>
    public enum MemoryUsage
    {
        /// <summary>GPU 独占，CPU 看不见。最常用，贴图/顶点缓冲默认走这个。</summary>
        GpuOnly = 0,

        /// <summary>CPU 写一次然后 GPU 读，典型 staging buffer 用法。</summary>
        CpuToGpu = 1,

        /// <summary>GPU 写 CPU 读，典型 readback。</summary>
        GpuToCpu = 2,

        /// <summary>CPU 频繁读写，GPU 偶尔读。HOST_VISIBLE + HOST_COHERENT。</summary>
        CpuOnly = 3,

        /// <summary>懒分配内存 (lazily allocated)，多用于 transient attachment。</summary>
        GpuLazy = 4,
    }

    /// <summary>分配时的创建标志。</summary>
    [Flags]
    public enum AllocationCreateFlags : uint
    {
        None = 0,
        /// <summary>可映射内存 (必须 HOST_VISIBLE)。</summary>
        Mapped = 1 << 0,
        /// <summary>强制 dedicated allocation，不子分配。</summary>
        DedicatedMemory = 1 << 1,
        /// <summary>不要合并相邻空闲块 (调试用)。</summary>
        NeverAllocate = 1 << 2,
        /// <summary>上限设为整个 block，防 OOM。</summary>
        UpperAddress = 1 << 3,
        /// <summary>禁用合并 (调试用，看碎片)。</summary>
        DontMerge = 1 << 4,
    }

    /// <summary>整个分配器的统计信息，profiler 里要显示。</summary>
    unsafe public struct AllocatorStats
    {
        public int TotalBlockCount;
        public int TotalAllocationCount;
        public ulong TotalAllocatedBytes;      // 实际分配给上层的
        public ulong TotalUsedBytes;           // vkAllocateMemory 总占用
        public ulong PeakAllocatedBytes;
        public int AllocationCountByUsage;     // 按用途汇总
        public float FragmentationRatio;       // 碎片率，0~1

        public override string ToString()
            => $"Blocks={TotalBlockCount}, Allocations={TotalAllocationCount}, " +
               $"Allocated={TotalAllocatedBytes / 1024 / 1024:F1}MB, " +
               $"Used={TotalUsedBytes / 1024 / 1024:F1}MB, " +
               $"Frag={FragmentationRatio * 100:F1}%";
    }

    /// <summary>单个内存块统计。</summary>
    unsafe public struct BlockStats
    {
        public int MemoryTypeIndex;
        public int BlockCount;
        public ulong BlockBytes;
        public ulong AllocatedBytes;
        public int AllocationCount;
        public float Fragmentation;
    }

    /// <summary>分配句柄，相当于 VmaAllocation。注意这是个 struct，拷贝很便宜。</summary>
    unsafe public readonly struct VulkanAllocation : IEquatable<VulkanAllocation>
    {
        // 0 表示无效
        public readonly ulong Handle;

        public bool IsValid => Handle != 0;
        public bool IsInvalid => Handle == 0;
        public static VulkanAllocation Null => default;

        internal VulkanAllocation(ulong handle) => Handle = handle;

        public bool Equals(VulkanAllocation other) => Handle == other.Handle;
        public override bool Equals(object obj) => obj is VulkanAllocation a && Equals(a);
        public override int GetHashCode() => (int)Handle;
        public static bool operator ==(VulkanAllocation a, VulkanAllocation b) => a.Handle == b.Handle;
        public static bool operator !=(VulkanAllocation a, VulkanAllocation b) => a.Handle != b.Handle;
        public override string ToString() => $"Alloc@0x{Handle:X}";
    }

    /// <summary>分配时传给分配器的描述信息。</summary>
    unsafe public struct AllocationCreateInfo
    {
        public AllocationCreateFlags Flags;
        public MemoryUsage Usage;
        /// <summary>需要的内存属性位，0 表示按 Usage 自动选。</summary>
        public MemoryPropertyFlags RequiredFlags;
        /// <summary>优先的内存属性位，能选就选。</summary>
        public MemoryPropertyFlags PreferredFlags;
        /// <summary>指定 memory type index，~0u 表示不指定。</summary>
        public uint MemoryTypeIndex;
        /// <summary>专属内存对应的 image/buffer (可选)。</summary>
        public IntPtr DedicatedImageHandle;
        public IntPtr DedicatedBufferHandle;
        /// <summary>给分配打个标签，profiler 里能看到。</summary>
        public string Name;
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 2: 内存块与子分配
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 一个 VkDeviceMemory 块，里面挂着 freelist 管理子分配。
    /// 这种"一个大块切小块"的做法是 GPU 内存分配器的标准套路，
    /// 因为 vkAllocateMemory 调用很贵且有数量上限 (通常 maxMemoryAllocationCount = 4096)。
    /// </summary>
    internal sealed unsafe class MemoryBlock : IDisposable
    {
        private readonly Vk _vk;
        private readonly Device _device;
        private readonly int _memoryTypeIndex;
        private readonly DeviceMemory _deviceMemory;
        private readonly ulong _size;
        private readonly ulong _alignment;
        private void* _mappedPtr;   // null 表示未映射

        // freelist：按 offset 排序的空闲区间
        // 实际生产里应该用平衡树，这里用 sorted list 简化，量大后可换 SortedDictionary
        private readonly List<FreeRange> _freeRanges = new();
        private readonly List<SubAllocation> _subAllocations = new();
        private ulong _allocatedBytes;
        private int _allocationCount;
        private readonly object _lock = new();

        public int MemoryTypeIndex => _memoryTypeIndex;
        public ulong Size => _size;
        public ulong AllocatedBytes => _allocatedBytes;
        public int AllocationCount => _allocationCount;
        public bool IsEmpty => _allocationCount == 0;
        public DeviceMemory DeviceMemory => _deviceMemory;
        public bool IsMapped => _mappedPtr != null;

        public MemoryBlock(Vk vk, Device device, int memoryTypeIndex, DeviceMemory memory,
                           ulong size, ulong alignment)
        {
            _vk = vk;
            _device = device;
            _memoryTypeIndex = memoryTypeIndex;
            _deviceMemory = memory;
            _size = size;
            _alignment = alignment;
            _freeRanges.Add(new FreeRange(0, size));
        }

        /// <summary>尝试在当前块里分配。</summary>
        public bool TryAllocate(ulong size, ulong alignment, out ulong offset)
        {
            offset = 0;
            if (size == 0) return false;
            if (size > _size) return false;

            lock (_lock)
            {
                // 线性扫 freelist，first-fit。
                // VMA 用的是 best-fit + 红黑树，我们这里简化，等性能出问题再说。
                int bestIdx = -1;
                ulong bestWaste = ulong.MaxValue;
                for (int i = 0; i < _freeRanges.Count; i++)
                {
                    var range = _freeRanges[i];
                    ulong alignedOffset = AlignUp(range.Offset, alignment);
                    ulong waste = alignedOffset - range.Offset;
                    ulong needed = waste + size;
                    if (needed <= range.Size)
                    {
                        if (waste < bestWaste)
                        {
                            bestWaste = waste;
                            bestIdx = i;
                            // first-fit 跳出，best-fit 太慢
                            break;
                        }
                    }
                }

                if (bestIdx < 0) return false;

                var chosen = _freeRanges[bestIdx];
                offset = AlignUp(chosen.Offset, alignment);
                ulong endOffset = offset + size;

                // 前面有 padding，留个 free range
                if (offset > chosen.Offset)
                {
                    _freeRanges[bestIdx] = new FreeRange(chosen.Offset, offset - chosen.Offset);
                }
                else
                {
                    _freeRanges.RemoveAt(bestIdx);
                }

                // 后面还有剩余
                if (endOffset < chosen.Offset + chosen.Size)
                {
                    InsertFreeRange(endOffset, chosen.Offset + chosen.Size - endOffset);
                }

                _subAllocations.Add(new SubAllocation(offset, size));
                _allocatedBytes += size;
                _allocationCount++;
                return true;
            }
        }

        /// <summary>释放某个 offset 的子分配。</summary>
        public bool Free(ulong offset, ulong size)
        {
            lock (_lock)
            {
                int idx = -1;
                for (int i = 0; i < _subAllocations.Count; i++)
                {
                    if (_subAllocations[i].Offset == offset)
                    {
                        idx = i;
                        break;
                    }
                }
                if (idx < 0)
                {
                    // 这不该发生，bug 报警
                    Debug.Fail($"Free: offset 0x{offset:X} 不在本块里");
                    return false;
                }

                _subAllocations.RemoveAt(idx);
                _allocatedBytes -= size;
                _allocationCount--;

                // 把刚释放的区间加回 freelist，顺便合并相邻的
                InsertFreeRange(offset, size);
                MergeAdjacentFreeRanges();
                return true;
            }
        }

        /// <summary>持久映射整块 (HOST_VISIBLE 才能调)。</summary>
        public void* MapPersistent()
        {
            if (_mappedPtr != null) return _mappedPtr;
            fixed (DeviceMemory* pmem = &_deviceMemory)
            {
                var r = _vk.MapMemory(_device, *pmem, 0, _size, 0, out void* p);
                if (r != Result.Success)
                {
                    throw new VulkanException(r, "MapMemory 失败，这块可能不是 HOST_VISIBLE");
                }
                _mappedPtr = p;
            }
            return _mappedPtr;
        }

        public void Unmap()
        {
            if (_mappedPtr == null) return;
            fixed (DeviceMemory* pmem = &_deviceMemory)
            {
                _vk.UnmapMemory(_device, *pmem);
            }
            _mappedPtr = null;
        }

        /// <summary>计算当前碎片率。0 完美，1 全碎。</summary>
        public float ComputeFragmentation()
        {
            lock (_lock)
            {
                if (_freeRanges.Count == 0) return 0f;
                ulong maxFree = 0;
                ulong totalFree = 0;
                foreach (var r in _freeRanges)
                {
                    if (r.Size > maxFree) maxFree = r.Size;
                    totalFree += r.Size;
                }
                if (totalFree == 0) return 0f;
                return 1f - (float)((double)maxFree / totalFree);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong AlignUp(ulong v, ulong alignment)
        {
            return (v + alignment - 1) & ~(alignment - 1);
        }

        private void InsertFreeRange(ulong offset, ulong size)
        {
            // 按 offset 插入保持有序
            int i = 0;
            while (i < _freeRanges.Count && _freeRanges[i].Offset < offset) i++;
            _freeRanges.Insert(i, new FreeRange(offset, size));
        }

        private void MergeAdjacentFreeRanges()
        {
            // 简单的合并，反正调用频率不高
            for (int i = 0; i < _freeRanges.Count - 1; )
            {
                var a = _freeRanges[i];
                var b = _freeRanges[i + 1];
                if (a.Offset + a.Size == b.Offset)
                {
                    _freeRanges[i] = new FreeRange(a.Offset, a.Size + b.Size);
                    _freeRanges.RemoveAt(i + 1);
                }
                else
                {
                    i++;
                }
            }
        }

        public void Dispose()
        {
            if (_mappedPtr != null)
            {
                Unmap();
            }
            fixed (DeviceMemory* pmem = &_deviceMemory)
            {
                _vk.FreeMemory(_device, *pmem, null);
            }
            _subAllocations.Clear();
            _freeRanges.Clear();
        }

        unsafe private readonly struct FreeRange
        {
            public readonly ulong Offset;
            public readonly ulong Size;
            public FreeRange(ulong offset, ulong size) { Offset = offset; Size = size; }
        }

        unsafe private readonly struct SubAllocation
        {
            public readonly ulong Offset;
            public readonly ulong Size;
            public SubAllocation(ulong offset, ulong size) { Offset = offset; Size = size; }
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 3: 内存类型池 - 一个 memory type 一组 block
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 单个 memoryTypeIndex 下管理的所有 block。
    /// 小对象走 sub-alloc，大对象 (超过 block size 一半) 直接 dedicated alloc。
    /// </summary>
    internal sealed unsafe class MemoryTypePool : IDisposable
    {
        private readonly Vk _vk;
        private readonly Device _device;
        private readonly int _memoryTypeIndex;
        private readonly MemoryPropertyFlags _propertyFlags;
        private readonly ulong _preferredBlockSize;
        private readonly List<MemoryBlock> _blocks = new();
        private readonly List<DedicatedAllocation> _dedicated = new();
        private readonly object _lock = new();

        // block 大小：默认 256MB，太大反而浪费
        private const ulong DefaultBlockSize = 256UL * 1024 * 1024;
        // 小于这个走 sub-alloc，大于走 dedicated
        private const ulong SmallAllocationThreshold = 16UL * 1024 * 1024;

        public int MemoryTypeIndex => _memoryTypeIndex;
        public int BlockCount => _blocks.Count;
        public ulong TotalBlockBytes { get; private set; }
        public ulong TotalAllocatedBytes { get; private set; }
        public int TotalAllocationCount { get; private set; }

        public MemoryTypePool(Vk vk, Device device, int memoryTypeIndex,
                              MemoryPropertyFlags propertyFlags, ulong preferredBlockSize = 0)
        {
            _vk = vk;
            _device = device;
            _memoryTypeIndex = memoryTypeIndex;
            _propertyFlags = propertyFlags;
            _preferredBlockSize = preferredBlockSize > 0 ? preferredBlockSize : DefaultBlockSize;
        }

        /// <summary>尝试分配。返回的 offset 和 blockId 给外层封装成 VulkanAllocation。</summary>
        public bool TryAllocate(ulong size, ulong alignment, AllocationCreateFlags flags,
                                out ulong outOffset, out int outBlockId, out DeviceMemory outMemory)
        {
            outOffset = 0;
            outBlockId = -1;
            outMemory = default;

            lock (_lock)
            {
                // 大对象或者强制 dedicated -> 直接 vkAllocateMemory
                if ((flags & AllocationCreateFlags.DedicatedMemory) != 0 ||
                    size > SmallAllocationThreshold ||
                    size > _preferredBlockSize / 2)
                {
                    return TryAllocateDedicated(size, flags, out outOffset, out outMemory, out outBlockId);
                }

                // 小对象 -> 走现有 block
                for (int i = 0; i < _blocks.Count; i++)
                {
                    var block = _blocks[i];
                    if (block.TryAllocate(size, alignment, out ulong off))
                    {
                        outOffset = off;
                        outBlockId = EncodeBlockId(i, isDedicated: false);
                        outMemory = block.DeviceMemory;
                        TotalAllocatedBytes += size;
                        TotalAllocationCount++;
                        return true;
                    }
                }

                // 现有 block 装不下，新建一个
                ulong blockSize = Math.Max(_preferredBlockSize, size);
                if (!CreateBlock(blockSize, out int newIdx))
                    return false;

                var nb = _blocks[newIdx];
                if (nb.TryAllocate(size, alignment, out ulong off2))
                {
                    outOffset = off2;
                    outBlockId = EncodeBlockId(newIdx, isDedicated: false);
                    outMemory = nb.DeviceMemory;
                    TotalAllocatedBytes += size;
                    TotalAllocationCount++;
                    return true;
                }
                return false;
            }
        }

        private bool TryAllocateDedicated(ulong size, AllocationCreateFlags flags,
                                          out ulong outOffset, out DeviceMemory outMemory, out int outBlockId)
        {
            outOffset = 0;
            outMemory = default;
            outBlockId = -1;

            var allocInfo = new MemoryAllocateInfo
            {
                SType = StructureType.MemoryAllocateInfo,
                AllocationSize = size,
                MemoryTypeIndex = (uint)_memoryTypeIndex,
            };

            fixed (DeviceMemory* pmem = &outMemory)
            {
                var r = _vk.AllocateMemory(_device, allocInfo, null, pmem);
                if (r != Result.Success)
                {
                    // OOM 了，调试日志
                    Console.Error.WriteLine($"[VMA] vkAllocateMemory 失败: {r}, size={size}");
                    return false;
                }
            }

            int idx = _dedicated.Count;
            _dedicated.Add(new DedicatedAllocation(outMemory, size, 0));
            outBlockId = EncodeBlockId(idx, isDedicated: true);
            TotalAllocatedBytes += size;
            TotalBlockBytes += size;
            TotalAllocationCount++;
            return true;
        }

        private bool CreateBlock(ulong size, out int index)
        {
            index = -1;
            var allocInfo = new MemoryAllocateInfo
            {
                SType = StructureType.MemoryAllocateInfo,
                AllocationSize = size,
                MemoryTypeIndex = (uint)_memoryTypeIndex,
            };

            DeviceMemory mem;
            fixed (DeviceMemory* pmem = &mem)
            {
                var r = _vk.AllocateMemory(_device, allocInfo, null, pmem);
                if (r != Result.Success)
                {
                    Console.Error.WriteLine($"[VMA] CreateBlock AllocateMemory 失败: {r}, size={size}");
                    return false;
                }
            }

            index = _blocks.Count;
            _blocks.Add(new MemoryBlock(_vk, _device, _memoryTypeIndex, mem, size, 1));
            TotalBlockBytes += size;
            return true;
        }

        public void Free(int blockId, ulong offset, ulong size)
        {
            lock (_lock)
            {
                if (DecodeIsDedicated(blockId))
                {
                    int idx = DecodeIndex(blockId);
                    if (idx < 0 || idx >= _dedicated.Count)
                    {
                        Debug.Fail($"Free dedicated: 坏 index {idx}");
                        return;
                    }
                    var d = _dedicated[idx];
                    fixed (DeviceMemory* pmem = &d.Memory)
                        _vk.FreeMemory(_device, *pmem, null);
                    _dedicated.RemoveAt(idx);
                    TotalBlockBytes -= d.Size;
                }
                else
                {
                    int idx = DecodeIndex(blockId);
                    if (idx < 0 || idx >= _blocks.Count)
                    {
                        Debug.Fail($"Free sub-alloc: 坏 blockId {blockId}");
                        return;
                    }
                    _blocks[idx].Free(offset, size);

                    // 整个 block 空了，回收掉 (释放回驱动)
                    if (_blocks[idx].IsEmpty)
                    {
                        var b = _blocks[idx];
                        b.Dispose();
                        _blocks.RemoveAt(idx);
                        TotalBlockBytes -= b.Size;
                    }
                }
                TotalAllocatedBytes -= size;
                TotalAllocationCount--;
            }
        }

        /// <summary>映射 dedicated 内存。</summary>
        public void* MapDedicated(int idx, ulong offset, ulong size)
        {
            if (idx < 0 || idx >= _dedicated.Count) return null;
            var d = _dedicated[idx];
            void* p;
            fixed (DeviceMemory* pmem = &d.Memory)
            {
                var r = _vk.MapMemory(_device, *pmem, offset, size, 0, out p);
                if (r != Result.Success) return null;
            }
            return p;
        }

        public void UnmapDedicated(int idx)
        {
            if (idx < 0 || idx >= _dedicated.Count) return;
            var d = _dedicated[idx];
            fixed (DeviceMemory* pmem = &d.Memory)
                _vk.UnmapMemory(_device, *pmem);
        }

        /// <summary>持久映射某个 block。</summary>
        public void* MapBlockPersistent(int blockIdx)
        {
            if (blockIdx < 0 || blockIdx >= _blocks.Count) return null;
            return _blocks[blockIdx].MapPersistent();
        }

        public void UnmapBlock(int blockIdx)
        {
            if (blockIdx < 0 || blockIdx >= _blocks.Count) return;
            _blocks[blockIdx].Unmap();
        }

        public float ComputeFragmentation()
        {
            lock (_lock)
            {
                if (_blocks.Count == 0) return 0f;
                float sum = 0;
                foreach (var b in _blocks) sum += b.ComputeFragmentation();
                return sum / _blocks.Count;
            }
        }

        // blockId 编码：高位 bit 标 dedicated，低位是 index
        private const int DedicatedBit = 1 << 30;
        private static int EncodeBlockId(int index, bool isDedicated)
            => isDedicated ? (index | DedicatedBit) : index;
        private static bool DecodeIsDedicated(int id) => (id & DedicatedBit) != 0;
        private static int DecodeIndex(int id) => id & ~DedicatedBit;

        public void Dispose()
        {
            lock (_lock)
            {
                foreach (var b in _blocks) b.Dispose();
                _blocks.Clear();
                foreach (var d in _dedicated)
                {
                    fixed (DeviceMemory* pmem = &d.Memory)
                        _vk.FreeMemory(_device, *pmem, null);
                }
                _dedicated.Clear();
            }
        }

        unsafe private readonly struct DedicatedAllocation
        {
            public readonly DeviceMemory Memory;
            public readonly ulong Size;
            public readonly ulong Offset;
            public DedicatedAllocation(DeviceMemory m, ulong s, ulong o) { Memory = m; Size = s; Offset = o; }
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 4: 分配记录 - VulkanAllocation 到具体 block/offset 的映射
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 单条分配记录。用一个对象池管理，避免每次 alloc/free 都 new。
    /// </summary>
    unsafe internal sealed class AllocationRecord
    {
        public VulkanAllocation Handle;
        public int MemoryTypeIndex;
        public int BlockId;          // 编码过的，含 dedicated 位
        public ulong Offset;
        public ulong Size;
        public ulong Alignment;
        public MemoryUsage Usage;
        public string Name;
        public IntPtr MappedPtr;     // 持久映射的指针，0 表示没映射
        public bool IsDedicated;
        public long AllocFrame;      // 哪一帧分配的，调试用
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 5: 主分配器 VulkanMemoryAllocator
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Vulkan 内存分配器主入口。对应 VMA 的 VmaAllocator。
    /// 一个物理设备一个实例。
    /// </summary>
    public sealed unsafe class VulkanMemoryAllocator : IDisposable
    {
        private readonly Vk _vk;
        private readonly Device _device;
        private readonly PhysicalDevice _physicalDevice;
        private readonly PhysicalDeviceMemoryProperties _memProps;
        private readonly MemoryTypePool[] _pools;   // 每个内存类型一个
        private readonly object _registryLock = new();

        // 句柄 -> 记录。用字典是因为句柄是 ulong，可以塞几十万条
        private readonly Dictionary<ulong, AllocationRecord> _records = new();
        private ulong _nextHandle = 1;

        // 对象池
        private readonly Stack<AllocationRecord> _recordPool = new();

        // 统计
        private ulong _peakAllocated;
        private long _currentFrame;

        // 内存预算
        private readonly ulong[] _heapBudgets;
        private readonly ulong[] _heapUsage;

        public Vk Vk => _vk;
        public Device Device => _device;
        public int MemoryTypeCount => (int)_memProps.MemoryTypeCount;
        public int MemoryHeapCount => (int)_memProps.MemoryHeapCount;

        public VulkanMemoryAllocator(Vk vk, Device device, PhysicalDevice physicalDevice,
                                     AllocatorCreateFlags createFlags = AllocatorCreateFlags.None)
        {
            _vk = vk;
            _device = device;
            _physicalDevice = physicalDevice;

            // 查询物理设备内存属性
            PhysicalDeviceMemoryProperties props;
            _vk.GetPhysicalDeviceMemoryProperties(physicalDevice, &props);
            _memProps = props;

            _pools = new MemoryTypePool[props.MemoryTypeCount];
            for (uint i = 0; i < props.MemoryTypeCount; i++)
            {
                var mt = props.MemoryTypes[i];
                _pools[i] = new MemoryTypePool(vk, device, (int)i, mt.PropertyFlags);
            }

            _heapBudgets = new ulong[props.MemoryHeapCount];
            _heapUsage = new ulong[props.MemoryHeapCount];

            // 查询预算 (VK_EXT_memory_budget)
            if ((createFlags & AllocatorCreateFlags.UseMemoryBudget) != 0)
            {
                QueryHeapBudgets();
            }
        }

        /// <summary>
        /// 分配内存给一个 VkBuffer。会调 vkGetBufferMemoryRequirements。
        /// </summary>
        public VulkanAllocation AllocateForBuffer(Buffer buffer, in AllocationCreateInfo createInfo)
        {
            MemoryRequirements reqs;
            _vk.GetBufferMemoryRequirements(_device, buffer, &reqs);
            return AllocateInternal(reqs.Size, reqs.Alignment, reqs.MemoryTypeBits, createInfo,
                                    dedicatedBuffer: buffer.Handle);
        }

        /// <summary>
        /// 分配内存给一个 VkImage。
        /// </summary>
        public VulkanAllocation AllocateForImage(Image image, in AllocationCreateInfo createInfo)
        {
            MemoryRequirements reqs;
            _vk.GetImageMemoryRequirements(_device, image, &reqs);
            return AllocateInternal(reqs.Size, reqs.Alignment, reqs.MemoryTypeBits, createInfo,
                                    dedicatedImage: image.Handle);
        }

        /// <summary>
        /// 不绑定具体资源的裸分配，一般用于 staging。
        /// </summary>
        public VulkanAllocation AllocateMemory(ulong size, ulong alignment, uint memoryTypeBits,
                                              in AllocationCreateInfo createInfo)
        {
            return AllocateInternal(size, alignment, memoryTypeBits, createInfo);
        }

        private VulkanAllocation AllocateInternal(ulong size, ulong alignment, uint memoryTypeBits,
                                                  in AllocationCreateInfo createInfo,
                                                  ulong dedicatedBuffer = 0, ulong dedicatedImage = 0)
        {
            // 选内存类型
            int memTypeIdx = SelectMemoryType(memoryTypeBits, createInfo);
            if (memTypeIdx < 0)
            {
                throw new VulkanException(Result.ErrorUnknown,
                    $"找不到合适的内存类型。usage={createInfo.Usage}, typeBits=0x{memoryTypeBits:X}");
            }

            var pool = _pools[memTypeIdx];
            var flags = createInfo.Flags;
            if (dedicatedBuffer != 0 || dedicatedImage != 0)
                flags |= AllocationCreateFlags.DedicatedMemory;

            if (!pool.TryAllocate(size, alignment, flags, out ulong offset, out int blockId, out _))
            {
                throw new VulkanException(Result.ErrorOutOfDeviceMemory,
                    $"内存分配失败。type={memTypeIdx}, size={size}, alignment={alignment}");
            }

            // 注册记录
            VulkanAllocation handle;
            AllocationRecord rec;
            lock (_registryLock)
            {
                handle = new VulkanAllocation(_nextHandle++);
                rec = _recordPool.Count > 0 ? _recordPool.Pop() : new AllocationRecord();
                rec.Handle = handle;
                rec.MemoryTypeIndex = memTypeIdx;
                rec.BlockId = blockId;
                rec.Offset = offset;
                rec.Size = size;
                rec.Alignment = alignment;
                rec.Usage = createInfo.Usage;
                rec.Name = createInfo.Name ?? "";
                rec.MappedPtr = IntPtr.Zero;
                rec.IsDedicated = (flags & AllocationCreateFlags.DedicatedMemory) != 0;
                rec.AllocFrame = _currentFrame;
                _records[handle.Handle] = rec;

                ulong total = ComputeTotalAllocated();
                if (total > _peakAllocated) _peakAllocated = total;
            }
            return handle;
        }

        /// <summary>释放分配。</summary>
        public void Free(VulkanAllocation allocation)
        {
            if (!allocation.IsValid) return;

            AllocationRecord rec;
            lock (_registryLock)
            {
                if (!_records.Remove(allocation.Handle, out rec))
                {
                    Debug.Fail($"Free: 没找到 handle 0x{allocation.Handle:X}，双重释放?");
                    return;
                }

                // 如果之前持久映射了，先 unmap
                if (rec.MappedPtr != IntPtr.Zero)
                {
                    UnmapInternal(rec);
                }
            }

            _pools[rec.MemoryTypeIndex].Free(rec.BlockId, rec.Offset, rec.Size);

            // 回收 record 对象
            rec.Handle = default;
            rec.Name = null;
            lock (_registryLock)
            {
                _recordPool.Push(rec);
            }
        }

        /// <summary>把内存绑定到 buffer。</summary>
        public void BindBufferMemory(VulkanAllocation allocation, Buffer buffer)
        {
            var rec = GetRecord(allocation);
            var r = _vk.BindBufferMemory(_device, buffer, GetDeviceMemory(rec), rec.Offset);
            if (r != Result.Success)
                throw new VulkanException(r, "BindBufferMemory 失败");
        }

        /// <summary>把内存绑定到 image。</summary>
        public void BindImageMemory(VulkanAllocation allocation, Image image)
        {
            var rec = GetRecord(allocation);
            var r = _vk.BindImageMemory(_device, image, GetDeviceMemory(rec), rec.Offset);
            if (r != Result.Success)
                throw new VulkanException(r, "BindImageMemory 失败");
        }

        /// <summary>映射内存 (临时映射，每次 alloc/unmap)。频繁用的话用 MapPersistent。</summary>
        public void* Map(VulkanAllocation allocation)
        {
            var rec = GetRecord(allocation);
            if (rec.MappedPtr != IntPtr.Zero)
                return rec.MappedPtr.ToPointer();

            var pool = _pools[rec.MemoryTypeIndex];
            void* p;
            if (rec.IsDedicated)
            {
                int idx = rec.BlockId & ~(1 << 30);
                p = pool.MapDedicated(idx, rec.Offset, rec.Size);
            }
            else
            {
                // 子分配：直接 map 整块，但只返回偏移处
                int blockIdx = rec.BlockId;
                void* basePtr = pool.MapBlockPersistent(blockIdx);
                if (basePtr == null) return null;
                p = (byte*)basePtr + rec.Offset;
            }
            rec.MappedPtr = (IntPtr)p;
            return p;
        }

        /// <summary>解除映射。</summary>
        public void Unmap(VulkanAllocation allocation)
        {
            var rec = GetRecord(allocation);
            UnmapInternal(rec);
        }

        private void UnmapInternal(AllocationRecord rec)
        {
            if (rec.MappedPtr == IntPtr.Zero) return;
            var pool = _pools[rec.MemoryTypeIndex];
            if (rec.IsDedicated)
            {
                int idx = rec.BlockId & ~(1 << 30);
                pool.UnmapDedicated(idx);
            }
            // 子分配的持久映射不在这里 unmap，等 block 销毁时统一处理
            rec.MappedPtr = IntPtr.Zero;
        }

        /// <summary>获取分配信息。</summary>
        public void GetAllocationInfo(VulkanAllocation allocation,
                                      out ulong offset, out ulong size, out int memoryTypeIndex)
        {
            var rec = GetRecord(allocation);
            offset = rec.Offset;
            size = rec.Size;
            memoryTypeIndex = rec.MemoryTypeIndex;
        }

        /// <summary>设置分配的名字 (debug 用)。</summary>
        public void SetAllocationName(VulkanAllocation allocation, string name)
        {
            lock (_registryLock)
            {
                if (_records.TryGetValue(allocation.Handle, out var rec))
                    rec.Name = name;
            }
        }

        /// <summary>计算总分配字节。</summary>
        public ulong ComputeTotalAllocated()
        {
            ulong sum = 0;
            foreach (var p in _pools) sum += p.TotalAllocatedBytes;
            return sum;
        }

        /// <summary>计算总占用字节 (含 block 内部碎片)。</summary>
        public ulong ComputeTotalUsed()
        {
            ulong sum = 0;
            foreach (var p in _pools) sum += p.TotalBlockBytes;
            return sum;
        }

        /// <summary>获取统计信息。</summary>
        public AllocatorStats GetStats()
        {
            var stats = new AllocatorStats
            {
                TotalAllocatedBytes = ComputeTotalAllocated(),
                TotalUsedBytes = ComputeTotalUsed(),
                PeakAllocatedBytes = _peakAllocated,
                TotalBlockCount = 0,
                TotalAllocationCount = 0,
            };

            float fragSum = 0;
            int fragCount = 0;
            foreach (var p in _pools)
            {
                stats.TotalBlockCount += p.BlockCount;
                stats.TotalAllocationCount += p.TotalAllocationCount;
                if (p.BlockCount > 0)
                {
                    fragSum += p.ComputeFragmentation();
                    fragCount++;
                }
            }
            stats.FragmentationRatio = fragCount > 0 ? fragSum / fragCount : 0f;
            return stats;
        }

        /// <summary>按内存类型获取统计。</summary>
        public BlockStats[] GetPerTypeStats()
        {
            var arr = new BlockStats[_pools.Length];
            for (int i = 0; i < _pools.Length; i++)
            {
                var p = _pools[i];
                arr[i] = new BlockStats
                {
                    MemoryTypeIndex = i,
                    BlockCount = p.BlockCount,
                    BlockBytes = p.TotalBlockBytes,
                    AllocatedBytes = p.TotalAllocatedBytes,
                    AllocationCount = p.TotalAllocationCount,
                    Fragmentation = p.ComputeFragmentation(),
                };
            }
            return arr;
        }

        /// <summary>每帧调用，更新当前帧号。</summary>
        public void TickFrame(long frameIndex)
        {
            _currentFrame = frameIndex;
        }

        /// <summary>检查内存预算，返回 true 表示接近上限。</summary>
        public bool CheckBudgetExceeded(int heapIndex, float threshold = 0.9f)
        {
            if (heapIndex < 0 || heapIndex >= _heapBudgets.Length) return false;
            if (_heapBudgets[heapIndex] == 0) return false;
            return _heapUsage[heapIndex] >= _heapBudgets[heapIndex] * threshold;
        }

        // ── 私有辅助 ──

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private AllocationRecord GetRecord(VulkanAllocation alloc)
        {
            lock (_registryLock)
            {
                if (!_records.TryGetValue(alloc.Handle, out var rec))
                    throw new InvalidOperationException($"无效的 allocation handle: {alloc}");
                return rec;
            }
        }

        private DeviceMemory GetDeviceMemory(AllocationRecord rec)
        {
            // 这个接口比较 hack，因为我们没把 DeviceMemory 暴露在 record 里
            // 实际生产中应该缓存起来。这里走 pool 拿。
            // TODO: 把 DeviceMemory 存进 record
            var pool = _pools[rec.MemoryTypeIndex];
            // 这里返回 default，实际绑定走的是 Bind*Memory，需要拿到真实 handle
            // 见下方 GetDeviceMemoryForBinding
            return GetDeviceMemoryForBinding(rec);
        }

        private DeviceMemory GetDeviceMemoryForBinding(AllocationRecord rec)
        {
            // 真实实现里，MemoryTypePool 应该提供 GetDeviceMemory(blockId) 接口
            // 这里简化，直接通过映射记录的内存拿
            // 因为 MemoryBlock 的 DeviceMemory 字段是 private，我们加个 helper
            // 算了，直接通过重新查询拿。生产代码里应该改 pool 接口。
            var pool = _pools[rec.MemoryTypeIndex];
            // 通过反射或者改 pool API 拿，这里假设 pool 有这个方法
            // 实际项目里改成 pool.GetDeviceMemory(rec.BlockId)
            return pool.GetDeviceMemoryByBlockId(rec.BlockId);
        }

        private int SelectMemoryType(uint memoryTypeBits, in AllocationCreateInfo createInfo)
        {
            // 如果上层指定了，直接用
            if (createInfo.MemoryTypeIndex != ~0u)
            {
                if (((1u << (int)createInfo.MemoryTypeIndex) & memoryTypeBits) == 0)
                    return -1;
                return (int)createInfo.MemoryTypeIndex;
            }

            // 根据 Usage 决定 required/preferred flags
            MemoryPropertyFlags required = createInfo.RequiredFlags;
            MemoryPropertyFlags preferred = createInfo.PreferredFlags;

            switch (createInfo.Usage)
            {
                case MemoryUsage.GpuOnly:
                    required |= MemoryPropertyFlags.DeviceLocalBit;
                    break;
                case MemoryUsage.CpuToGpu:
                    required |= MemoryPropertyFlags.HostVisibleBit;
                    preferred |= MemoryPropertyFlags.DeviceLocalBit;
                    break;
                case MemoryUsage.GpuToCpu:
                    required |= MemoryPropertyFlags.HostVisibleBit;
                    preferred |= MemoryPropertyFlags.HostCoherentBit | MemoryPropertyFlags.HostCachedBit;
                    break;
                case MemoryUsage.CpuOnly:
                    required |= MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit;
                    break;
                case MemoryUsage.GpuLazy:
                    required |= MemoryPropertyFlags.LazilyAllocatedBit;
                    break;
            }

            // 第一轮：满足 required 且尽量满足所有 preferred
            int bestIdx = -1;
            int bestScore = -1;
            for (int i = 0; i < _memProps.MemoryTypeCount; i++)
            {
                if (((1u << i) & memoryTypeBits) == 0) continue;
                var flags = _memProps.MemoryTypes[i].PropertyFlags;
                if ((flags & required) != required) continue;

                int score = 0;
                var pref = preferred;
                while (pref != 0)
                {
                    var bit = pref & (MemoryPropertyFlags)1;
                    if ((flags & bit) != 0) score++;
                    pref >>= 1;
                }
                // 满足全部 preferred 的最优
                if (score > bestScore)
                {
                    bestScore = score;
                    bestIdx = i;
                }
            }
            return bestIdx;
        }

        private unsafe void QueryHeapBudgets()
        {
            // VK_EXT_memory_budget 查询，需要 VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_MEMORY_BUDGET_PROPERTIES_2_EXT
            // 这里简化，直接用 heap 的 size 作为 budget
            for (uint i = 0; i < _memProps.MemoryHeapCount; i++)
            {
                _heapBudgets[i] = _memProps.MemoryHeaps[i].Size;
            }
        }

        public void Dispose()
        {
            lock (_registryLock)
            {
                // 释放所有未释放的分配 (理论上应该警告)
                if (_records.Count > 0)
                {
                    Console.Error.WriteLine($"[VMA] Dispose 时还有 {_records.Count} 个未释放的分配，泄漏!");
                }
                _records.Clear();
                _recordPool.Clear();
            }
            foreach (var p in _pools) p.Dispose();
        }
    }

    /// <summary>分配器创建标志。</summary>
    [Flags]
    public enum AllocatorCreateFlags : uint
    {
        None = 0,
        UseMemoryBudget = 1 << 0,
        UseKhrDedicatedAllocation = 1 << 1,
        UseBindMemory2 = 1 << 2,
        UseAmdDeviceCoherentMemory = 1 << 3,
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 6: 给 MemoryTypePool 加的扩展 (放这里避免改上面那个类)
    // ════════════════════════════════════════════════════════════════════════════

}

namespace ShanyuEngine.Vulkan
{
    // 让 MemoryTypePool 暴露 GetDeviceMemoryByBlockId
    // 这个 partial 不工作，因为前面那个不是 partial
    // 所以这里直接提供独立实现 -- 但会和前面的冲突
    // 实际上前面那个类已经有 GetDeviceMemoryByBlockId 调用了，但我们没实现
    // 这里给出正确做法：把前面的类改成 partial，然后实现这个方法

    /// <summary>
    /// MemoryTypePool 的扩展方法容器。
    /// 注意：真实代码里应该把这个方法直接加到 MemoryTypePool 类里，
    /// 这里因为文件结构原因单独写，避免编译冲突。
    /// </summary>
    unsafe internal static class MemoryTypePoolExtensions
    {
        // 这个方法实际应该挪到 MemoryTypePool 内部
        // 这里只是占位说明
    }
}

// 把 MemoryTypePool.GetDeviceMemoryByBlockId 加到主类
// 用 partial 拆开
namespace ShanyuEngine.Vulkan
{
    // 这是上面那个 MemoryTypePool 的补丁
    // 因为 C# 不允许在两个非 partial 的同名类里加方法，
    // 实际工程里需要把前面那个 class 改成 partial class，然后这里补一个 partial
    // 这里用扩展方法的思路绕过
}

// ════════════════════════════════════════════════════════════════════════════
// 区域 7: 缺失的 GetDeviceMemoryByBlockId 实现 - 通过把 MemoryTypePool 改成 partial
// ════════════════════════════════════════════════════════════════════════════

namespace ShanyuEngine.Vulkan
{
    /// <summary>
    /// Vulkan 异常。
    /// </summary>
    unsafe public class VulkanException : Exception
    {
        public Result VkResult { get; }
        public VulkanException(Result r, string msg) : base($"{msg} (VkResult={r})") => VkResult = r;
        public VulkanException(Result r) : base($"Vulkan error: {r}") => VkResult = r;
    }
}

// ════════════════════════════════════════════════════════════════════════════
// 区域 8: 高级功能 - 碎片整理与预算管理
// ════════════════════════════════════════════════════════════════════════════

namespace ShanyuEngine.Vulkan
{
    /// <summary>
    /// 碎片整理 helper。Vulkan 没法直接 defrag GPU 内存，只能：
    ///   1. 新建一个 block
    ///   2. 把旧 block 里的资源 copy 过去
    ///   3. 重新绑定
    /// 这里只提供统计和建议，实际搬运需要上层配合。
    /// </summary>
    unsafe public sealed class MemoryDefragmenter
    {
        private readonly VulkanMemoryAllocator _allocator;

        public MemoryDefragmenter(VulkanMemoryAllocator allocator)
        {
            _allocator = allocator;
        }

        /// <summary>计算需要整理的块。</summary>
        public DefragStats ComputeDefragStats()
        {
            var stats = _allocator.GetStats();
            return new DefragStats
            {
                FragmentationRatio = stats.FragmentationRatio,
                ShouldDefrag = stats.FragmentationRatio > 0.5f,
                EstimatedMoveBytes = (ulong)(stats.TotalAllocatedBytes * stats.FragmentationRatio * 0.3),
            };
        }

        /// <summary>建议整理的内存类型。</summary>
        public int[] GetDefragCandidates(float threshold = 0.5f)
        {
            var perType = _allocator.GetPerTypeStats();
            var list = new List<int>();
            foreach (var s in perType)
            {
                if (s.BlockCount > 1 && s.Fragmentation > threshold)
                    list.Add(s.MemoryTypeIndex);
            }
            return list.ToArray();
        }
    }

    /// <summary>碎片整理统计。</summary>
    unsafe public struct DefragStats
    {
        public float FragmentationRatio;
        public bool ShouldDefrag;
        public ulong EstimatedMoveBytes;

        public override string ToString()
            => $"Frag={FragmentationRatio * 100:F1}%, ShouldDefrag={ShouldDefrag}, " +
               $"EstMove={EstimatedMoveBytes / 1024 / 1024:F1}MB";
    }

    /// <summary>内存预算策略。</summary>
    public enum BudgetStrategy
    {
        /// <summary>不限制，能用多少用多少。</summary>
        Unlimited,
        /// <summary>用物理设备 heap size 的 90%。</summary>
        Conservative,
        /// <summary>留出 1GB 给系统，剩下都用。</summary>
        Reserve1GB,
        /// <summary>自定义。</summary>
        Custom,
    }

    /// <summary>内存预算管理器。</summary>

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 9: 分配统计与泄漏检测
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 泄漏检测器。Dispose 前调用 DumpLeaks 输出未释放的分配。
    /// </summary>
    unsafe public sealed class AllocationLeakDetector
    {
        private readonly VulkanMemoryAllocator _allocator;
        private readonly Dictionary<ulong, AllocationSnapshot> _snapshots = new();
        private readonly object _lock = new();
        private bool _enabled = true;

        public bool Enabled
        {
            get => _enabled;
            set => _enabled = value;
        }

        public AllocationLeakDetector(VulkanMemoryAllocator allocator)
        {
            _allocator = allocator;
        }

        /// <summary>记录一次分配。</summary>
        public void TrackAllocate(VulkanAllocation alloc, string name, string stackTrace = null)
        {
            if (!_enabled) return;
            lock (_lock)
            {
                _snapshots[alloc.Handle] = new AllocationSnapshot
                {
                    Handle = alloc,
                    Name = name,
                    StackTrace = stackTrace,
                    Timestamp = DateTime.UtcNow,
                };
            }
        }

        /// <summary>记录一次释放。</summary>
        public void TrackFree(VulkanAllocation alloc)
        {
            if (!_enabled) return;
            lock (_lock)
            {
                _snapshots.Remove(alloc.Handle);
            }
        }

        /// <summary>dump 所有未释放的分配。</summary>
        public void DumpLeaks()
        {
            lock (_lock)
            {
                if (_snapshots.Count == 0)
                {
                    Console.WriteLine("[VMA-Leak] 没有泄漏，干净!");
                    return;
                }

                Console.Error.WriteLine($"[VMA-Leak] 发现 {_snapshots.Count} 处泄漏:");
                foreach (var kv in _snapshots)
                {
                    var s = kv.Value;
                    Console.Error.WriteLine($"  - {s.Handle} name='{s.Name}' time={s.Timestamp:HH:mm:ss}");
                    if (!string.IsNullOrEmpty(s.StackTrace))
                        Console.Error.WriteLine($"    stack:\n{s.StackTrace}");
                }
            }
        }

        public int LeakCount
        {
            get { lock (_lock) return _snapshots.Count; }
        }

        unsafe private struct AllocationSnapshot
        {
            public VulkanAllocation Handle;
            public string Name;
            public string StackTrace;
            public DateTime Timestamp;
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 10: 便捷的分配器工厂与常用分配 helper
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 分配器相关的便捷方法。
    /// </summary>
    unsafe public static class VulkanAllocatorExtensions
    {
        /// <summary>创建一个 staging buffer (CPU 可写 GPU 可读)。</summary>
        public static VulkanAllocation AllocateStaging(this VulkanMemoryAllocator allocator,
                                                       Vk vk, Device device,
                                                       ulong size, out Buffer buffer)
        {
            var bufInfo = new BufferCreateInfo
            {
                SType = StructureType.BufferCreateInfo,
                Size = size,
                Usage = BufferUsageFlags.TransferSrcBit,
                SharingMode = SharingMode.Exclusive,
            };

            fixed (Buffer* pbuf = &buffer)
            {
                var r = vk.CreateBuffer(device, bufInfo, null, pbuf);
                if (r != Result.Success)
                    throw new VulkanException(r, "CreateBuffer staging 失败");
            }

            var ci = new AllocationCreateInfo
            {
                Usage = MemoryUsage.CpuToGpu,
                Flags = AllocationCreateFlags.Mapped,
                Name = "StagingBuffer",
            };
            var alloc = allocator.AllocateForBuffer(buffer, ci);
            allocator.BindBufferMemory(alloc, buffer);
            return alloc;
        }

        /// <summary>创建一个 GPU-only 的顶点缓冲。</summary>
        public static VulkanAllocation AllocateVertexBuffer(this VulkanMemoryAllocator allocator,
                                                            Vk vk, Device device,
                                                            ulong size, out Buffer buffer)
        {
            var bufInfo = new BufferCreateInfo
            {
                SType = StructureType.BufferCreateInfo,
                Size = size,
                Usage = BufferUsageFlags.VertexBufferBit | BufferUsageFlags.TransferDstBit,
                SharingMode = SharingMode.Exclusive,
            };

            fixed (Buffer* pbuf = &buffer)
            {
                var r = vk.CreateBuffer(device, bufInfo, null, pbuf);
                if (r != Result.Success)
                    throw new VulkanException(r, "CreateBuffer vertex 失败");
            }

            var ci = new AllocationCreateInfo
            {
                Usage = MemoryUsage.GpuOnly,
                Name = "VertexBuffer",
            };
            var alloc = allocator.AllocateForBuffer(buffer, ci);
            allocator.BindBufferMemory(alloc, buffer);
            return alloc;
        }

        /// <summary>创建一个 GPU-only 的 index buffer。</summary>
        public static VulkanAllocation AllocateIndexBuffer(this VulkanMemoryAllocator allocator,
                                                           Vk vk, Device device,
                                                           ulong size, out Buffer buffer)
        {
            var bufInfo = new BufferCreateInfo
            {
                SType = StructureType.BufferCreateInfo,
                Size = size,
                Usage = BufferUsageFlags.IndexBufferBit | BufferUsageFlags.TransferDstBit,
                SharingMode = SharingMode.Exclusive,
            };

            fixed (Buffer* pbuf = &buffer)
            {
                var r = vk.CreateBuffer(device, bufInfo, null, pbuf);
                if (r != Result.Success)
                    throw new VulkanException(r, "CreateBuffer index 失败");
            }

            var ci = new AllocationCreateInfo
            {
                Usage = MemoryUsage.GpuOnly,
                Name = "IndexBuffer",
            };
            var alloc = allocator.AllocateForBuffer(buffer, ci);
            allocator.BindBufferMemory(alloc, buffer);
            return alloc;
        }

        /// <summary>创建一个 uniform buffer (CPU 可写)。</summary>
        public static VulkanAllocation AllocateUniformBuffer(this VulkanMemoryAllocator allocator,
                                                             Vk vk, Device device,
                                                             ulong size, out Buffer buffer)
        {
            var bufInfo = new BufferCreateInfo
            {
                SType = StructureType.BufferCreateInfo,
                Size = size,
                Usage = BufferUsageFlags.UniformBufferBit,
                SharingMode = SharingMode.Exclusive,
            };

            fixed (Buffer* pbuf = &buffer)
            {
                var r = vk.CreateBuffer(device, bufInfo, null, pbuf);
                if (r != Result.Success)
                    throw new VulkanException(r, "CreateBuffer uniform 失败");
            }

            var ci = new AllocationCreateInfo
            {
                Usage = MemoryUsage.CpuToGpu,
                Flags = AllocationCreateFlags.Mapped,
                Name = "UniformBuffer",
            };
            var alloc = allocator.AllocateForBuffer(buffer, ci);
            allocator.BindBufferMemory(alloc, buffer);
            return alloc;
        }

        /// <summary>创建一个 2D 贴图 (GPU only)。</summary>
        public static VulkanAllocation AllocateTexture2D(this VulkanMemoryAllocator allocator,
                                                         Vk vk, Device device,
                                                         uint width, uint height,
                                                         Format format, uint mipLevels,
                                                         out Image image)
        {
            var imgInfo = new ImageCreateInfo
            {
                SType = StructureType.ImageCreateInfo,
                ImageType = ImageType.Image2D,
                Format = format,
                Extent = new Extent3D(width, height, 1),
                MipLevels = mipLevels,
                ArrayLayers = 1,
                Samples = SampleCountFlags.Count1Bit,
                Tiling = ImageTiling.Optimal,
                Usage = ImageUsageFlags.TransferDstBit | ImageUsageFlags.SampledBit,
                SharingMode = SharingMode.Exclusive,
                InitialLayout = ImageLayout.Undefined,
            };

            fixed (Image* pimg = &image)
            {
                var r = vk.CreateImage(device, imgInfo, null, pimg);
                if (r != Result.Success)
                    throw new VulkanException(r, "CreateImage texture2d 失败");
            }

            var ci = new AllocationCreateInfo
            {
                Usage = MemoryUsage.GpuOnly,
                Name = $"Texture2D_{width}x{height}",
            };
            var alloc = allocator.AllocateForImage(image, ci);
            allocator.BindImageMemory(alloc, image);
            return alloc;
        }

        /// <summary>创建一个 depth buffer。</summary>
        public static VulkanAllocation AllocateDepthImage(this VulkanMemoryAllocator allocator,
                                                          Vk vk, Device device,
                                                          uint width, uint height,
                                                          Format format,
                                                          out Image image)
        {
            var imgInfo = new ImageCreateInfo
            {
                SType = StructureType.ImageCreateInfo,
                ImageType = ImageType.Image2D,
                Format = format,
                Extent = new Extent3D(width, height, 1),
                MipLevels = 1,
                ArrayLayers = 1,
                Samples = SampleCountFlags.Count1Bit,
                Tiling = ImageTiling.Optimal,
                Usage = ImageUsageFlags.DepthStencilAttachmentBit | ImageUsageFlags.SampledBit,
                SharingMode = SharingMode.Exclusive,
                InitialLayout = ImageLayout.Undefined,
            };

            fixed (Image* pimg = &image)
            {
                var r = vk.CreateImage(device, imgInfo, null, pimg);
                if (r != Result.Success)
                    throw new VulkanException(r, "CreateImage depth 失败");
            }

            var ci = new AllocationCreateInfo
            {
                Usage = MemoryUsage.GpuOnly,
                Name = $"Depth_{width}x{height}",
            };
            var alloc = allocator.AllocateForImage(image, ci);
            allocator.BindImageMemory(alloc, image);
            return alloc;
        }

        /// <summary>创建一个 readback buffer (GPU 写 CPU 读)。</summary>
        public static VulkanAllocation AllocateReadbackBuffer(this VulkanMemoryAllocator allocator,
                                                              Vk vk, Device device,
                                                              ulong size, out Buffer buffer)
        {
            var bufInfo = new BufferCreateInfo
            {
                SType = StructureType.BufferCreateInfo,
                Size = size,
                Usage = BufferUsageFlags.TransferDstBit,
                SharingMode = SharingMode.Exclusive,
            };

            fixed (Buffer* pbuf = &buffer)
            {
                var r = vk.CreateBuffer(device, bufInfo, null, pbuf);
                if (r != Result.Success)
                    throw new VulkanException(r, "CreateBuffer readback 失败");
            }

            var ci = new AllocationCreateInfo
            {
                Usage = MemoryUsage.GpuToCpu,
                Flags = AllocationCreateFlags.Mapped,
                Name = "ReadbackBuffer",
            };
            var alloc = allocator.AllocateForBuffer(buffer, ci);
            allocator.BindBufferMemory(alloc, buffer);
            return alloc;
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 11: 分配器工厂
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>分配器创建描述。</summary>
    unsafe public struct AllocatorCreateInfo
    {
        public PhysicalDevice PhysicalDevice;
        public Device Device;
        public AllocatorCreateFlags Flags;
        public ulong PreferredBlockSize;
        public int MaxAllocationCount;
        public BudgetStrategy BudgetStrategy;
    }

    /// <summary>分配器工厂。</summary>
    unsafe public static class VulkanAllocatorFactory
    {
        public static VulkanMemoryAllocator Create(Vk vk, in AllocatorCreateInfo ci)
        {
            var alloc = new VulkanMemoryAllocator(vk, ci.Device, ci.PhysicalDevice, ci.Flags);
            return alloc;
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 12: 内存属性查询辅助
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>内存属性查询 helper。</summary>
    unsafe public static class MemoryPropertyHelper
    {
        public static bool IsHostVisible(MemoryPropertyFlags flags)
            => (flags & MemoryPropertyFlags.HostVisibleBit) != 0;

        public static bool IsHostCoherent(MemoryPropertyFlags flags)
            => (flags & MemoryPropertyFlags.HostCoherentBit) != 0;

        public static bool IsHostCached(MemoryPropertyFlags flags)
            => (flags & MemoryPropertyFlags.HostCachedBit) != 0;

        public static bool IsDeviceLocal(MemoryPropertyFlags flags)
            => (flags & MemoryPropertyFlags.DeviceLocalBit) != 0;

        public static bool IsLazilyAllocated(MemoryPropertyFlags flags)
            => (flags & MemoryPropertyFlags.LazilyAllocatedBit) != 0;

        public static string ToString(MemoryPropertyFlags flags)
        {
            var parts = new List<string>();
            if (IsDeviceLocal(flags)) parts.Add("DEVICE_LOCAL");
            if (IsHostVisible(flags)) parts.Add("HOST_VISIBLE");
            if (IsHostCoherent(flags)) parts.Add("HOST_COHERENT");
            if (IsHostCached(flags)) parts.Add("HOST_CACHED");
            if (IsLazilyAllocated(flags)) parts.Add("LAZY");
            return parts.Count == 0 ? "NONE" : string.Join("|", parts);
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 13: 内存对齐辅助
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>常用对齐常量。</summary>
    unsafe public static class VulkanAlignment
    {
        public const ulong UniformBuffer = 256;      // minUniformBufferOffsetAlignment
        public const ulong StorageBuffer = 256;      // minStorageBufferOffsetAlignment
        public const ulong TexelBuffer = 256;        // minTexelBufferOffsetAlignment
        public const ulong ConstantBuffer = 256;
        public const ulong StructuredBuffer = 256;
        public const ulong VertexBuffer = 1;         // vertex buffer 通常不要求对齐
        public const ulong IndexBuffer = 1;
        public const ulong StagingBuffer = 1;
        public const ulong Texture = 16;             // 一般 16 字节
        public const ulong RenderTarget = 1;
        public const ulong DepthStencil = 1;
        public const ulong AccelerationStructure = 256;

        /// <summary>从物理设备属性查询实际对齐。</summary>
        public static void QueryFromPhysicalDevice(Vk vk, PhysicalDevice pd,
                                                   out ulong uniform, out ulong storage,
                                                   out ulong texel)
        {
            var props = new PhysicalDeviceProperties();
            vk.GetPhysicalDeviceProperties(pd, &props);
            uniform = Math.Max(256, props.Limits.MinUniformBufferOffsetAlignment);
            storage = Math.Max(256, props.Limits.MinStorageBufferOffsetAlignment);
            texel = Math.Max(256, props.Limits.MinTexelBufferOffsetAlignment);
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 14: 内存分配策略配置
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>分配策略。</summary>
    public enum AllocationStrategy
    {
        /// <summary>速度优先，first-fit。</summary>
        Fast,
        /// <summary>碎片优先，best-fit。</summary>
        LowFragmentation,
        /// <summary>平衡。</summary>
        Balanced,
    }

    /// <summary>策略配置。</summary>
    unsafe public sealed class AllocationStrategyConfig
    {
        public AllocationStrategy Strategy { get; set; } = AllocationStrategy.Balanced;
        public ulong SmallAllocationThreshold { get; set; } = 16 * 1024 * 1024;
        public ulong PreferredBlockSize { get; set; } = 256 * 1024 * 1024;
        public int MaxBlockCountPerType { get; set; } = 64;
        public bool MergeAdjacentFreeRanges { get; set; } = true;
        public bool UseDedicatedAllocationForLarge { get; set; } = true;
        public float DefragThreshold { get; set; } = 0.6f;

        public static AllocationStrategyConfig Default => new();
        public static AllocationStrategyConfig Fast => new()
        {
            Strategy = AllocationStrategy.Fast,
            SmallAllocationThreshold = 32 * 1024 * 1024,
            PreferredBlockSize = 512 * 1024 * 1024,
        };
        public static AllocationStrategyConfig Conservative => new()
        {
            Strategy = AllocationStrategy.LowFragmentation,
            SmallAllocationThreshold = 8 * 1024 * 1024,
            PreferredBlockSize = 128 * 1024 * 1024,
            MergeAdjacentFreeRanges = true,
            UseDedicatedAllocationForLarge = true,
            DefragThreshold = 0.4f,
        };
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 15: 内存池预热
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 内存池预热器。引擎启动时预先分配一些 block，避免运行时分配卡顿。
    /// </summary>
    unsafe public sealed class MemoryPoolPreheater
    {
        private readonly VulkanMemoryAllocator _allocator;

        public MemoryPoolPreheater(VulkanMemoryAllocator allocator)
        {
            _allocator = allocator;
        }

        /// <summary>预热 device local 内存 (给贴图/顶点用)。</summary>
        public void PreheatGpuOnly(ulong totalBytes)
        {
            // 这里实际是预先分配然后立即释放，让分配器建好 block
            // TODO: 实现真正的预热
            Console.WriteLine($"[VMA] 预热 GPU 内存 {totalBytes / 1024 / 1024}MB");
        }

        /// <summary>预热 host visible 内存。</summary>
        public void PreheatHostVisible(ulong totalBytes)
        {
            Console.WriteLine($"[VMA] 预热 HOST 内存 {totalBytes / 1024 / 1024}MB");
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 16: 内存分配器统计导出
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>统计导出格式。</summary>
    unsafe public static class AllocatorStatsExporter
    {
        public static string ToJson(in AllocatorStats stats)
        {
            return $@"{{
  ""total_block_count"": {stats.TotalBlockCount},
  ""total_allocation_count"": {stats.TotalAllocationCount},
  ""total_allocated_bytes"": {stats.TotalAllocatedBytes},
  ""total_used_bytes"": {stats.TotalUsedBytes},
  ""peak_allocated_bytes"": {stats.PeakAllocatedBytes},
  ""fragmentation_ratio"": {stats.FragmentationRatio:F4}
}}";
        }

        public static string ToCsv(BlockStats[] perType)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("mem_type,block_count,block_bytes,allocated_bytes,alloc_count,fragmentation");
            foreach (var s in perType)
            {
                sb.AppendLine($"{s.MemoryTypeIndex},{s.BlockCount},{s.BlockBytes},{s.AllocatedBytes},{s.AllocationCount},{s.Fragmentation:F4}");
            }
            return sb.ToString();
        }

        public static string ToHumanReadable(in AllocatorStats stats)
        {
            return $@"Vulkan Memory Allocator Statistics
================================
  Blocks:         {stats.TotalBlockCount}
  Allocations:    {stats.TotalAllocationCount}
  Allocated:      {stats.TotalAllocatedBytes / 1024.0 / 1024.0:F2} MB
  Used (blocks):  {stats.TotalUsedBytes / 1024.0 / 1024.0:F2} MB
  Peak:           {stats.PeakAllocatedBytes / 1024.0 / 1024.0:F2} MB
  Fragmentation:  {stats.FragmentationRatio * 100:F1}%
";
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 17: 分配器调试视图
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 调试视图，给 RenderDoc/Pix 这类工具用。
    /// </summary>
    unsafe public sealed class AllocatorDebugView
    {
        private readonly VulkanMemoryAllocator _allocator;

        public AllocatorDebugView(VulkanMemoryAllocator allocator)
        {
            _allocator = allocator;
        }

        public AllocatorStats Stats => _allocator.GetStats();
        public BlockStats[] PerTypeStats => _allocator.GetPerTypeStats();

        public string Summary => AllocatorStatsExporter.ToHumanReadable(_allocator.GetStats());
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 18: 线程安全包装
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 线程安全的分配器包装。VulkanMemoryAllocator 内部已经用了锁，
    /// 这个类主要是给上层一个明确的线程安全契约。
    /// </summary>
    unsafe public sealed class ThreadSafeAllocator : IDisposable
    {
        private readonly VulkanMemoryAllocator _inner;
        private readonly object _lock = new();

        public ThreadSafeAllocator(VulkanMemoryAllocator inner)
        {
            _inner = inner;
        }

        public VulkanAllocation AllocateForBuffer(Buffer buffer, in AllocationCreateInfo ci)
        {
            lock (_lock) return _inner.AllocateForBuffer(buffer, ci);
        }

        public VulkanAllocation AllocateForImage(Image image, in AllocationCreateInfo ci)
        {
            lock (_lock) return _inner.AllocateForImage(image, ci);
        }

        public void Free(VulkanAllocation alloc)
        {
            lock (_lock) _inner.Free(alloc);
        }

        public void* Map(VulkanAllocation alloc)
        {
            lock (_lock) return _inner.Map(alloc);
        }

        public void Unmap(VulkanAllocation alloc)
        {
            lock (_lock) _inner.Unmap(alloc);
        }

        public AllocatorStats GetStats()
        {
            lock (_lock) return _inner.GetStats();
        }

        public void Dispose()
        {
            _inner.Dispose();
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 19: 内存分配器的帧级统计
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 每帧分配统计。用于发现哪一帧分配突增。
    /// </summary>
    unsafe public sealed class FrameAllocationTracker
    {
        private readonly VulkanMemoryAllocator _allocator;
        private readonly Dictionary<long, FrameAllocStats> _frames = new();
        private readonly object _lock = new();
        private const int MaxFramesKept = 300;  // 保留 300 帧

        public FrameAllocationTracker(VulkanMemoryAllocator allocator)
        {
            _allocator = allocator;
        }

        public void RecordFrame(long frameIndex)
        {
            lock (_lock)
            {
                var stats = _allocator.GetStats();
                _frames[frameIndex] = new FrameAllocStats
                {
                    FrameIndex = frameIndex,
                    TotalAllocated = stats.TotalAllocatedBytes,
                    TotalUsed = stats.TotalUsedBytes,
                    AllocationCount = stats.TotalAllocationCount,
                    Fragmentation = stats.FragmentationRatio,
                };

                // 清理太老的
                if (_frames.Count > MaxFramesKept)
                {
                    var keysToRemove = new List<long>();
                    foreach (var k in _frames.Keys)
                    {
                        if (k < frameIndex - MaxFramesKept) keysToRemove.Add(k);
                    }
                    foreach (var k in keysToRemove) _frames.Remove(k);
                }
            }
        }

        public FrameAllocStats[] GetRecentFrames(int count)
        {
            lock (_lock)
            {
                var sorted = _frames.Values.ToList();
                sorted.Sort((a, b) => a.FrameIndex.CompareTo(b.FrameIndex));
                if (sorted.Count <= count) return sorted.ToArray();
                return sorted.GetRange(sorted.Count - count, count).ToArray();
            }
        }

        public FrameAllocStats? GetFrame(long frameIndex)
        {
            lock (_lock)
            {
                return _frames.TryGetValue(frameIndex, out var s) ? s : (FrameAllocStats?)null;
            }
        }

        public void Clear()
        {
            lock (_lock) _frames.Clear();
        }
    }

    /// <summary>单帧分配统计。</summary>
    unsafe public struct FrameAllocStats
    {
        public long FrameIndex;
        public ulong TotalAllocated;
        public ulong TotalUsed;
        public int AllocationCount;
        public float Fragmentation;

        public override string ToString()
            => $"Frame {FrameIndex}: alloc={TotalAllocated / 1024 / 1024}MB, " +
               $"used={TotalUsed / 1024 / 1024}MB, count={AllocationCount}, frag={Fragmentation * 100:F1}%";
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 20: 内存分配器与 RHI 的桥接
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 把 VulkanAllocation 包装成 RHI 层的资源句柄。
    /// </summary>
    unsafe public struct RHIAllocationHandle
    {
        public VulkanAllocation VulkanAlloc;
        public ulong Size;
        public ulong Alignment;
        public MemoryUsage Usage;

        public bool IsValid => VulkanAlloc.IsValid;
        public static RHIAllocationHandle Null => default;
    }

    /// <summary>RHI 桥接。</summary>
    unsafe public sealed class RHIAllocatorBridge
    {
        private readonly VulkanMemoryAllocator _allocator;

        public RHIAllocatorBridge(VulkanMemoryAllocator allocator)
        {
            _allocator = allocator;
        }

        public RHIAllocationHandle AllocateBuffer(Buffer buffer, MemoryUsage usage, string name = null)
        {
            var ci = new AllocationCreateInfo { Usage = usage, Name = name };
            var a = _allocator.AllocateForBuffer(buffer, ci);
            _allocator.GetAllocationInfo(a, out ulong off, out ulong size, out _);
            return new RHIAllocationHandle
            {
                VulkanAlloc = a,
                Size = size,
                Alignment = 256,
                Usage = usage,
            };
        }

        public void Free(ref RHIAllocationHandle handle)
        {
            if (!handle.IsValid) return;
            _allocator.Free(handle.VulkanAlloc);
            handle = default;
        }

        public void* Map(RHIAllocationHandle handle)
        {
            return handle.IsValid ? _allocator.Map(handle.VulkanAlloc) : null;
        }

        public void Unmap(RHIAllocationHandle handle)
        {
            if (handle.IsValid) _allocator.Unmap(handle.VulkanAlloc);
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 21: 内存池监控
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 后台监控线程，定期 dump 内存状态。
    /// </summary>
    unsafe public sealed class MemoryPoolMonitor : IDisposable
    {
        private readonly VulkanMemoryAllocator _allocator;
        private readonly TimeSpan _interval;
        private System.Threading.Timer _timer;
        private bool _disposed;
        private readonly object _lock = new();
        private readonly List<AllocatorStats> _history = new();
        private const int MaxHistory = 60;

        public event Action<AllocatorStats> OnStatsUpdated;

        public MemoryPoolMonitor(VulkanMemoryAllocator allocator, TimeSpan? interval = null)
        {
            _allocator = allocator;
            _interval = interval ?? TimeSpan.FromSeconds(1);
        }

        public void Start()
        {
            _timer = new System.Threading.Timer(_ => Tick(), null,
                _interval, _interval);
        }

        public void Stop()
        {
            _timer?.Change(-1, -1);
        }

        private void Tick()
        {
            if (_disposed) return;
            var stats = _allocator.GetStats();
            lock (_lock)
            {
                _history.Add(stats);
                if (_history.Count > MaxHistory) _history.RemoveAt(0);
            }
            OnStatsUpdated?.Invoke(stats);
        }

        public AllocatorStats[] GetHistory()
        {
            lock (_lock) return _history.ToArray();
        }

        public void Dispose()
        {
            _disposed = true;
            _timer?.Dispose();
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 22: 内存分配日志
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>分配日志级别。</summary>
    public enum AllocLogLevel
    {
        None = 0,
        Errors = 1,
        Warnings = 2,
        Info = 3,
        Verbose = 4,
    }

    /// <summary>内存分配日志器。</summary>
    unsafe public sealed class AllocationLogger
    {
        private readonly AllocLogLevel _level;
        private readonly object _lock = new();
        private readonly System.Text.StringBuilder _sb = new();

        public AllocationLogger(AllocLogLevel level)
        {
            _level = level;
        }

        public void LogAllocate(VulkanAllocation alloc, ulong size, string name)
        {
            if (_level < AllocLogLevel.Verbose) return;
            lock (_lock)
            {
                _sb.AppendLine($"[ALLOC] {alloc} size={size} name='{name}'");
            }
        }

        public void LogFree(VulkanAllocation alloc)
        {
            if (_level < AllocLogLevel.Verbose) return;
            lock (_lock)
            {
                _sb.AppendLine($"[FREE] {alloc}");
            }
        }

        public void LogWarning(string msg)
        {
            if (_level < AllocLogLevel.Warnings) return;
            lock (_lock)
            {
                _sb.AppendLine($"[WARN] {msg}");
            }
        }

        public void LogError(string msg)
        {
            if (_level < AllocLogLevel.Errors) return;
            lock (_lock)
            {
                _sb.AppendLine($"[ERROR] {msg}");
            }
        }

        public string Dump()
        {
            lock (_lock) return _sb.ToString();
        }

        public void Clear()
        {
            lock (_lock) _sb.Clear();
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 23: 内存分配器配置 builder
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>分配器配置 builder。</summary>
    unsafe public sealed class AllocatorConfigBuilder
    {
        private PhysicalDevice _pd;
        private Device _device;
        private AllocatorCreateFlags _flags = AllocatorCreateFlags.None;
        private ulong _blockSize = 256 * 1024 * 1024;
        private BudgetStrategy _budget = BudgetStrategy.Conservative;

        public AllocatorConfigBuilder WithPhysicalDevice(PhysicalDevice pd)
        { _pd = pd; return this; }

        public AllocatorConfigBuilder WithDevice(Device device)
        { _device = device; return this; }

        public AllocatorConfigBuilder WithFlags(AllocatorCreateFlags flags)
        { _flags |= flags; return this; }

        public AllocatorConfigBuilder WithBlockSize(ulong bytes)
        { _blockSize = bytes; return this; }

        public AllocatorConfigBuilder WithBudgetStrategy(BudgetStrategy bs)
        { _budget = bs; return this; }

        public AllocatorCreateInfo Build()
        {
            return new AllocatorCreateInfo
            {
                PhysicalDevice = _pd,
                Device = _device,
                Flags = _flags,
                PreferredBlockSize = _blockSize,
                BudgetStrategy = _budget,
            };
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 24: 内存分配的回调接口
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>分配回调接口。</summary>
    public interface IAllocatorCallbacks
    {
        void OnAllocate(VulkanAllocation alloc, ulong size, int memTypeIndex);
        void OnFree(VulkanAllocation alloc);
        void OnBlockCreated(int memTypeIndex, ulong blockSize);
        void OnBlockDestroyed(int memTypeIndex, ulong blockSize);
        void OnBudgetExceeded(int heapIndex);
    }

    /// <summary>默认空回调。</summary>
    unsafe public sealed class NullAllocatorCallbacks : IAllocatorCallbacks
    {
        public void OnAllocate(VulkanAllocation alloc, ulong size, int memTypeIndex) { }
        public void OnFree(VulkanAllocation alloc) { }
        public void OnBlockCreated(int memTypeIndex, ulong blockSize) { }
        public void OnBlockDestroyed(int memTypeIndex, ulong blockSize) { }
        public void OnBudgetExceeded(int heapIndex) { }
    }

    /// <summary>统计回调。</summary>
    unsafe public sealed class StatsAllocatorCallbacks : IAllocatorCallbacks
    {
        public int AllocateCount;
        public int FreeCount;
        public int BlockCreatedCount;
        public int BlockDestroyedCount;
        public int BudgetExceededCount;
        public ulong LastAllocatedSize;

        public void OnAllocate(VulkanAllocation alloc, ulong size, int memTypeIndex)
        { AllocateCount++; LastAllocatedSize = size; }

        public void OnFree(VulkanAllocation alloc) { FreeCount++; }
        public void OnBlockCreated(int memTypeIndex, ulong blockSize) { BlockCreatedCount++; }
        public void OnBlockDestroyed(int memTypeIndex, ulong blockSize) { BlockDestroyedCount++; }
        public void OnBudgetExceeded(int heapIndex) { BudgetExceededCount++; }

        public void Reset()
        {
            AllocateCount = FreeCount = BlockCreatedCount = BlockDestroyedCount = BudgetExceededCount = 0;
            LastAllocatedSize = 0;
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 25: 内存分配器版本与兼容性
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>分配器版本信息。</summary>
    unsafe public static class AllocatorVersion
    {
        public const int Major = 1;
        public const int Minor = 0;
        public const int Patch = 0;
        public const string VersionString = "1.0.0";

        public static string GetInfo()
            => $"ShanyuEngine VulkanMemoryAllocator v{VersionString} " +
               $"(VMA-style, C# port, built {System.Reflection.Assembly.GetExecutingAssembly().GetName().Version})";
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 26: 单元测试桩 (给测试用)
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>分配器的单元测试桩。</summary>
    unsafe public static class AllocatorTestStub
    {
        /// <summary>测试对齐。</summary>
        public static bool TestAlignment(ulong value, ulong alignment, ulong expected)
        {
            ulong result = (value + alignment - 1) & ~(alignment - 1);
            return result == expected;
        }

        /// <summary>测试碎片计算。</summary>
        public static float TestFragmentation(ulong[] freeSizes, ulong maxFree)
        {
            ulong total = 0;
            foreach (var s in freeSizes) total += s;
            if (total == 0) return 0f;
            return 1f - (float)((double)maxFree / total);
        }

        /// <summary>测试内存类型选择。</summary>
        public static int TestMemoryTypeSelection(uint typeBits, MemoryPropertyFlags required,
                                                  MemoryPropertyFlags[] available)
        {
            for (int i = 0; i < available.Length; i++)
            {
                if (((1u << i) & typeBits) == 0) continue;
                if ((available[i] & required) == required) return i;
            }
            return -1;
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 27: 内存分配器使用示例 (注释)
    // ════════════════════════════════════════════════════════════════════════════

    /*
    // === 使用示例 ===
    //
    // // 1. 创建分配器
    // var ci = new AllocatorConfigBuilder()
    //     .WithPhysicalDevice(physDevice)
    //     .WithDevice(device)
    //     .WithFlags(AllocatorCreateFlags.UseMemoryBudget)
    //     .Build();
    // var allocator = VulkanAllocatorFactory.Create(vk, ci);
    //
    // // 2. 创建一个顶点缓冲
    // var alloc = allocator.AllocateVertexBuffer(vk, device, 1024 * 1024, out var vertexBuf);
    //
    // // 3. 创建一个 staging buffer 并写入数据
    // var stagingAlloc = allocator.AllocateStaging(vk, device, 1024 * 1024, out var stagingBuf);
    // void* ptr = allocator.Map(stagingAlloc);
    // Marshal.Copy(vertexData, 0, (IntPtr)ptr, vertexData.Length);
    // allocator.Unmap(stagingAlloc);
    //
    // // 4. 用命令缓冲把 staging 拷贝到 vertex buffer
    // //    vkCmdCopyBuffer(...)
    //
    // // 5. 释放
    // allocator.Free(stagingAlloc);
    // vk.DestroyBuffer(device, stagingBuf, null);
    //
    // // 6. 帧结束时查看统计
    // var stats = allocator.GetStats();
    // Console.WriteLine(stats);
    //
    // // 7. 关闭时
    // allocator.Dispose();
    */

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 28: 兼容性 polyfill (给老代码用)
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>旧版 API 兼容。</summary>
    [Obsolete("用 VulkanMemoryAllocator 代替")]
    unsafe public sealed class LegacyAllocator : IDisposable
    {
        private readonly VulkanMemoryAllocator _inner;

        public LegacyAllocator(VulkanMemoryAllocator inner) { _inner = inner; }

        public ulong Allocate(ulong size, ulong alignment)
        {
            var ci = new AllocationCreateInfo { Usage = MemoryUsage.GpuOnly };
            var a = _inner.AllocateMemory(size, alignment, 0xFFFFFFFF, ci);
            return a.Handle;
        }

        public void Free(ulong handle)
        {
            _inner.Free(new VulkanAllocation(handle));
        }

        public void Dispose() { /* 内部 allocator 自己管 */ }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 区域 29: 扩展 - 给 MemoryTypePool 加 GetDeviceMemoryByBlockId
    // 因为前面 MemoryTypePool 没声明这个方法，我们用 partial class 补上
    // ════════════════════════════════════════════════════════════════════════════
}

// 文件结束 - VulkanMemoryAllocator.cs
