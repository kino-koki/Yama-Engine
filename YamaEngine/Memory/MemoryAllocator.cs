using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace YamaEngine.Memory
{
    // ============================================================
    //  1. 内存块基础类型
    // ============================================================

    /// <summary>
    /// 连续内存块描述
    /// </summary>
    public struct MemoryBlock
    {
        public IntPtr Pointer;
        public int Size;
        public bool IsAllocated;

        public MemoryBlock(IntPtr pointer, int size)
        {
            Pointer = pointer;
            Size = size;
            IsAllocated = false;
        }

        public void Clear() => IsAllocated = false;
    }

    /// <summary>
    /// 内存分配类别，用于统计追踪
    /// </summary>
    public enum MemoryCategory
    {
        General,
        Rendering,
        Physics,
        Audio,
        Scripting,
        Animation,
        Networking,
        Streaming
    }

    // ============================================================
    //  2. 对齐工具
    // ============================================================

    internal static class AlignmentHelper
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int AlignUp(int size, int alignment)
        {
            return (size + alignment - 1) & ~(alignment - 1);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long AlignUp(long size, long alignment)
        {
            return (size + alignment - 1) & ~(alignment - 1);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static IntPtr AlignUp(IntPtr address, int alignment)
        {
            long masked = (alignment - 1);
            return new IntPtr((address.ToInt64() + masked) & ~masked);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsAligned(IntPtr address, int alignment)
        {
            return (address.ToInt64() & (alignment - 1)) == 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsPowerOfTwo(int value)
        {
            return value != 0 && (value & (value - 1)) == 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int NextPowerOfTwo(int value)
        {
            if (value <= 0) return 1;
            value--;
            value |= value >> 1;
            value |= value >> 2;
            value |= value >> 4;
            value |= value >> 8;
            value |= value >> 16;
            return value + 1;
        }
    }

    // ============================================================
    //  3. SafeHandle 包装 — 确保非托管内存释放
    // ============================================================

    /// <summary>
    /// SafeHandle 包装的内存块，确保 Dispose 时释放非托管内存
    /// </summary>
    public sealed class SafeMemoryHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public int Size { get; }

        public SafeMemoryHandle(int size)
            : base(true)
        {
            Size = size;
            SetHandle(Marshal.AllocHGlobal(size));
        }

        public SafeMemoryHandle(IntPtr existingHandle, int size, bool ownsHandle)
            : base(ownsHandle)
        {
            Size = size;
            SetHandle(existingHandle);
        }

        public unsafe Span<byte> AsSpan()
        {
            if (IsInvalid) return Span<byte>.Empty;
            return new Span<byte>((void*)handle, Size);
        }

        public unsafe Memory<byte> AsMemory()
        {
            if (IsInvalid) return Memory<byte>.Empty;
            return new Memory<byte>(AsSpan().ToArray());
        }

        protected override bool ReleaseHandle()
        {
            Marshal.FreeHGlobal(handle);
            return true;
        }

        public static SafeMemoryHandle Null => new SafeMemoryHandle(IntPtr.Zero, 0, false);
    }

    // ============================================================
    //  4. ArenaAllocator — 线性分配器（批量释放）
    // ============================================================

    /// <summary>
    /// 线性（Bump Pointer）分配器。
    /// 分配 O(1)，释放只能批量 Reset，适用于每帧临时分配（渲染命令、物理碰撞检测等）。
    /// </summary>
    public sealed class ArenaAllocator : IDisposable
    {
        private IntPtr _basePtr;
        private int _offset;
        private int _capacity;
        private readonly int _alignment;
        private bool _disposed;

        /// <summary>当前已使用字节数</summary>
        public int UsedBytes => _offset;

        /// <summary>总容量</summary>
        public int Capacity => _capacity;

        /// <summary>剩余可用字节数</summary>
        public int AvailableBytes => _capacity - _offset;

        /// <summary>使用率 0~1</summary>
        public float Utilization => (float)_offset / _capacity;

        /// <summary>
        /// 创建 Arena 分配器
        /// </summary>
        /// <param name="capacity">总容量（字节）</param>
        /// <param name="alignment">对齐字节数（必须为2的幂）</param>
        public ArenaAllocator(int capacity, int alignment = 16)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            if (!AlignmentHelper.IsPowerOfTwo(alignment))
                throw new ArgumentException("Alignment must be a power of two.", nameof(alignment));

            _capacity = capacity;
            _alignment = alignment;
            _basePtr = Marshal.AllocHGlobal(capacity);
            _offset = 0;
        }

        /// <summary>
        /// 从 Arena 中线性分配。O(1)，仅移动 bump pointer。
        /// </summary>
        public IntPtr Allocate(int size)
        {
            if (size < 0) throw new ArgumentOutOfRangeException(nameof(size));

            int alignedSize = AlignmentHelper.AlignUp(size, _alignment);
            int newOffset = _offset + alignedSize;

            if (newOffset > _capacity)
            {
                // 尝试扩展（分配新块并替换，旧块暂由GC回收——实际生产中应更精细管理）
                int newCapacity = Math.Max(_capacity * 2, newOffset);
                IntPtr newBase = Marshal.AllocHGlobal(newCapacity);
                unsafe
                {
                    Buffer.MemoryCopy(
                        (void*)_basePtr, (void*)newBase,
                        newCapacity, _offset);
                }
                Marshal.FreeHGlobal(_basePtr);
                _basePtr = newBase;
                _capacity = newCapacity;
            }

            IntPtr result = _basePtr + _offset;
            _offset = newOffset;
            return result;
        }

        /// <summary>
        /// 分配并零初始化
        /// </summary>
        public IntPtr AllocateZeroed(int size)
        {
            IntPtr ptr = Allocate(size);
            unsafe { Buffer.MemoryCopy(null, (void*)ptr, size, size); }
            return ptr;
        }

        /// <summary>
        /// 分配指定类型的结构体并返回引用
        /// </summary>
        public unsafe ref T Allocate<T>() where T : unmanaged
        {
            int size = sizeof(T);
            IntPtr ptr = Allocate(size);
            return ref *(T*)ptr;
        }

        /// <summary>
        /// 分配数组
        /// </summary>
        public unsafe IntPtr AllocateArray<T>(int count) where T : unmanaged
        {
            return Allocate(sizeof(T) * count);
        }

        /// <summary>
        /// 将 bump pointer 回退到零，一次性释放所有分配。不逐块释放。
        /// </summary>
        public void Reset()
        {
            _offset = 0;
        }

        /// <summary>
        /// 保存当前偏移作为标记
        /// </summary>
        public int GetMarker()
        {
            return _offset;
        }

        /// <summary>
        /// 回退到指定标记位置，释放标记之后的所有分配
        /// </summary>
        public void FreeToMarker(int marker)
        {
            if (marker < 0 || marker > _offset)
                throw new ArgumentOutOfRangeException(nameof(marker));
            _offset = marker;
        }

        /// <summary>
        /// 获取从起始位置到当前偏移的 Span
        /// </summary>
        public unsafe Span<byte> AsSpan()
        {
            return new Span<byte>((void*)_basePtr, _offset);
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                Marshal.FreeHGlobal(_basePtr);
                _basePtr = IntPtr.Zero;
                _disposed = true;
            }
            GC.SuppressFinalize(this);
        }

        ~ArenaAllocator()
        {
            Dispose();
        }
    }

    // ============================================================
    //  5. ThreadStatic 线程局部 Arena — 无锁每帧临时分配
    // ============================================================

    /// <summary>
    /// 线程局部 Arena 工厂。每个线程自动获得独立的 Arena 实例，无需加锁。
    /// 典型用法：每帧开始时调用 ResetAll()，帧结束由各系统自行 Reset。
    /// </summary>
    public static class ThreadLocalArena
    {
        [ThreadStatic]
        private static ArenaAllocator? t_frameArena;

        /// <summary>
        /// 获取当前线程的帧临时 Arena（默认16MB、16字节对齐）
        /// </summary>
        public static ArenaAllocator GetFrameArena(int capacity = 16 * 1024 * 1024, int alignment = 16)
        {
            if (t_frameArena == null)
                t_frameArena = new ArenaAllocator(capacity, alignment);
            return t_frameArena;
        }

        /// <summary>
        /// 重置当前线程 Arena
        /// </summary>
        public static void ResetCurrent()
        {
            t_frameArena?.Reset();
        }

        /// <summary>
        /// 释放当前线程 Arena 资源（线程退出时调用）
        /// </summary>
        public static void DisposeCurrent()
        {
            t_frameArena?.Dispose();
            t_frameArena = null;
        }
    }

    // ============================================================
    //  6. PoolAllocator — 固定大小对象池
    // ============================================================

    /// <summary>
    /// 固定大小对象池。Allocate/Free 均 O(1)。
    /// 内部使用侵入式空闲链表，每个块头 8 字节（next指针）。
    /// 支持桶式分级大小（8/16/32/64/128/256/512/1024/2048/4096）。
    /// </summary>
    public sealed class PoolAllocator : IDisposable
    {
        // 预定义大小桶
        public static readonly int[] DefaultBucketSizes =
        {
            8, 16, 32, 64, 128, 256, 512, 1024, 2048, 4096
        };

        private readonly struct Bucket
        {
            public readonly int BlockSize;
            public readonly int BlockCount;
            public readonly IntPtr BasePtr;

            public Bucket(int blockSize, int blockCount, IntPtr basePtr)
            {
                BlockSize = blockSize;
                BlockCount = blockCount;
                BasePtr = basePtr;
            }
        }

        private readonly Bucket[] _buckets;
        // 每个 bucket 的空闲链表头（使用 int 索引，-1 表示空）
        private readonly int[] _freeListHeads;
        private readonly object[] _bucketLocks;
        // 在每个块的头部嵌入 next 索引（4字节 int），所以实际用户可用 = blockSize - 4
        internal const int HeaderSize = sizeof(int);

        private bool _disposed;
        private long _totalAllocations;
        private long _totalFrees;

        public int BucketCount => _buckets.Length;

        /// <summary>总分配次数</summary>
        public long TotalAllocations => Interlocked.Read(ref _totalAllocations);

        /// <summary>总释放次数</summary>
        public long TotalFrees => Interlocked.Read(ref _totalFrees);

        /// <summary>
        /// 创建池分配器
        /// </summary>
        /// <param name="bucketSizes">各桶块大小</param>
        /// <param name="blocksPerBucket">每个桶的块数</param>
        public PoolAllocator(int[]? bucketSizes = null, int blocksPerBucket = 1024)
        {
            bucketSizes ??= DefaultBucketSizes;
            _buckets = new Bucket[bucketSizes.Length];
            _freeListHeads = new int[bucketSizes.Length];
            _bucketLocks = new object[bucketSizes.Length];

            for (int i = 0; i < bucketSizes.Length; i++)
            {
                int blockFullSize = bucketSizes[i]; // 包含 header
                int totalBytes = blockFullSize * blocksPerBucket;
                IntPtr basePtr = Marshal.AllocHGlobal(totalBytes);

                // 初始化空闲链表：每块头写入下一块索引
                unsafe
                {
                    byte* ptr = (byte*)basePtr;
                    for (int j = 0; j < blocksPerBucket - 1; j++)
                    {
                        *(int*)(ptr + j * blockFullSize) = j + 1;
                    }
                    *(int*)(ptr + (blocksPerBucket - 1) * blockFullSize) = -1;
                }

                _freeListHeads[i] = 0;
                _bucketLocks[i] = new object();
                _buckets[i] = new Bucket(blockFullSize, blocksPerBucket, basePtr);
            }
        }

        /// <summary>
        /// 从最匹配的桶中分配一块。返回用户可用指针（跳过4字节头）。
        /// </summary>
        public IntPtr Allocate(int size)
        {
            int bucketIndex = FindBucket(size + HeaderSize);
            if (bucketIndex < 0)
                throw new OutOfMemoryException(
                    $"Requested size {size} exceeds maximum bucket size {_buckets[^1].BlockSize - HeaderSize}");

            lock (_bucketLocks[bucketIndex])
            {
                ref int head = ref _freeListHeads[bucketIndex];
                if (head < 0)
                    throw new OutOfMemoryException(
                        $"Pool bucket {_buckets[bucketIndex].BlockSize} is exhausted.");

                int blockIndex = head;
                IntPtr blockPtr = _buckets[bucketIndex].BasePtr + blockIndex * _buckets[bucketIndex].BlockSize;

                unsafe
                {
                    head = *(int*)blockPtr;
                }

                Interlocked.Increment(ref _totalAllocations);

                // 返回跳过 header 的用户区域
                return blockPtr + HeaderSize;
            }
        }

        /// <summary>
        /// 将块释放回对应桶的空闲链表。O(1)。
        /// </summary>
        public void Free(IntPtr ptr)
        {
            if (ptr == IntPtr.Zero) return;

            int bucketIndex = FindBucketForPointer(ptr);
            if (bucketIndex < 0)
                throw new ArgumentException("Pointer does not belong to any bucket.", nameof(ptr));

            lock (_bucketLocks[bucketIndex])
            {
                IntPtr userPtr = ptr;
                IntPtr blockPtr = userPtr - HeaderSize;

                unsafe
                {
                    *(int*)blockPtr = _freeListHeads[bucketIndex];
                }

                _freeListHeads[bucketIndex] =
                    (int)((blockPtr - _buckets[bucketIndex].BasePtr) / _buckets[bucketIndex].BlockSize);

                Interlocked.Increment(ref _totalFrees);
            }
        }

        /// <summary>
        /// 查询各桶使用情况
        /// </summary>
        public (int BucketSize, int BlockCount, int FreeCount)[] GetBucketStats()
        {
            var stats = new (int, int, int)[_buckets.Length];
            for (int i = 0; i < _buckets.Length; i++)
            {
                int freeCount = CountFreeInBucket(i);
                stats[i] = (_buckets[i].BlockSize - HeaderSize, _buckets[i].BlockCount, freeCount);
            }
            return stats;
        }

        private int FindBucket(int totalSize)
        {
            for (int i = 0; i < _buckets.Length; i++)
            {
                if (_buckets[i].BlockSize >= totalSize)
                    return i;
            }
            return -1;
        }

        private int FindBucketForPointer(IntPtr ptr)
        {
            for (int i = 0; i < _buckets.Length; i++)
            {
                long offset = ptr.ToInt64() - HeaderSize - _buckets[i].BasePtr.ToInt64();
                if (offset >= 0 && offset < _buckets[i].BlockSize * _buckets[i].BlockCount)
                {
                    if (offset % _buckets[i].BlockSize == 0)
                        return i;
                }
            }
            return -1;
        }

        private int CountFreeInBucket(int bucketIndex)
        {
            lock (_bucketLocks[bucketIndex])
            {
                int count = 0;
                int current = _freeListHeads[bucketIndex];
                while (current >= 0)
                {
                    count++;
                    IntPtr blockPtr = _buckets[bucketIndex].BasePtr + current * _buckets[bucketIndex].BlockSize;
                    unsafe { current = *(int*)blockPtr; }
                }
                return count;
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                foreach (var bucket in _buckets)
                    Marshal.FreeHGlobal(bucket.BasePtr);
                _disposed = true;
            }
            GC.SuppressFinalize(this);
        }

        ~PoolAllocator()
        {
            Dispose();
        }
    }

    // ============================================================
    //  7. ScratchAllocator — 标记/释放临时分配器
    // ============================================================

    /// <summary>
    /// 临时分配器，基于标记/释放（GetMarker/FreeToMarker）模式。
    /// 适用于中间计算：序列化临时缓冲区、字符串拼接等。
    /// </summary>
    public sealed class ScratchAllocator : IDisposable
    {
        private IntPtr _basePtr;
        private int _offset;
        private readonly int _capacity;
        private readonly int _alignment;
        private bool _disposed;

        public int UsedBytes => _offset;
        public int Capacity => _capacity;
        public int AvailableBytes => _capacity - _offset;

        public ScratchAllocator(int capacity = 4 * 1024 * 1024, int alignment = 16)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            if (!AlignmentHelper.IsPowerOfTwo(alignment))
                throw new ArgumentException("Alignment must be power of two.", nameof(alignment));

            _capacity = capacity;
            _alignment = alignment;
            _basePtr = Marshal.AllocHGlobal(capacity);
            _offset = 0;
        }

        /// <summary>
        /// 分配临时内存
        /// </summary>
        public IntPtr Allocate(int size)
        {
            if (size < 0) throw new ArgumentOutOfRangeException(nameof(size));
            int aligned = AlignmentHelper.AlignUp(size, _alignment);

            if (_offset + aligned > _capacity)
                throw new OutOfMemoryException("Scratch allocator overflow.");

            IntPtr result = _basePtr + _offset;
            _offset += aligned;
            return result;
        }

        /// <summary>
        /// 获取当前标记（用于后续 FreeToMarker）
        /// </summary>
        public int GetMarker() => _offset;

        /// <summary>
        /// 释放标记之后的所有临时分配
        /// </summary>
        public void FreeToMarker(int marker)
        {
            if (marker < 0 || marker > _offset)
                throw new ArgumentOutOfRangeException(nameof(marker));
            _offset = marker;
        }

        /// <summary>
        /// 获取从标记到当前偏移的 Span
        /// </summary>
        public unsafe Span<byte> GetSpanFromMarker(int marker)
        {
            if (marker < 0 || marker > _offset)
                throw new ArgumentOutOfRangeException(nameof(marker));
            return new Span<byte>((void*)(_basePtr + marker), _offset - marker);
        }

        /// <summary>
        /// 释放所有临时内存
        /// </summary>
        public void Reset() => _offset = 0;

        public void Dispose()
        {
            if (!_disposed)
            {
                Marshal.FreeHGlobal(_basePtr);
                _basePtr = IntPtr.Zero;
                _disposed = true;
            }
            GC.SuppressFinalize(this);
        }

        ~ScratchAllocator() => Dispose();
    }

    // ============================================================
    //  8. StackAllocator — 栈式分配器（Push/Pop）
    // ============================================================

    /// <summary>
    /// 栈式分配器，支持 Push/Pop 模式。
    /// 分配时保存旧指针，释放时回退。不支持随机释放。
    /// </summary>
    public sealed class StackAllocator : IDisposable
    {
        private IntPtr _basePtr;
        private int _offset;
        private readonly int _capacity;
        private readonly int _alignment;
        private bool _disposed;

        public int UsedBytes => _offset;
        public int Capacity => _capacity;
        public int AvailableBytes => _capacity - _offset;

        public StackAllocator(int capacity = 2 * 1024 * 1024, int alignment = 16)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            if (!AlignmentHelper.IsPowerOfTwo(alignment))
                throw new ArgumentException("Alignment must be power of two.", nameof(alignment));

            _capacity = capacity;
            _alignment = alignment;
            _basePtr = Marshal.AllocHGlobal(capacity);
            _offset = 0;
        }

        /// <summary>
        /// 入栈分配。返回分配的指针。
        /// </summary>
        public IntPtr Push(int size)
        {
            if (size < 0) throw new ArgumentOutOfRangeException(nameof(size));
            int aligned = AlignmentHelper.AlignUp(size, _alignment);

            if (_offset + aligned > _capacity)
                throw new OutOfMemoryException("Stack allocator overflow.");

            IntPtr result = _basePtr + _offset;
            _offset += aligned;
            return result;
        }

        /// <summary>
        /// 入栈分配结构体
        /// </summary>
        public unsafe ref T Push<T>() where T : unmanaged
        {
            int size = sizeof(T);
            IntPtr ptr = Push(size);
            return ref *(T*)ptr;
        }

        /// <summary>
        /// 保存当前栈顶标记
        /// </summary>
        public int GetTopMarker() => _offset;

        /// <summary>
        /// 弹出栈到指定大小（回退指针）
        /// </summary>
        public void Pop(int size)
        {
            if (size < 0 || size > _offset)
                throw new ArgumentOutOfRangeException(nameof(size));
            _offset -= size;
        }

        /// <summary>
        /// 弹出到标记位置
        /// </summary>
        public void PopToMarker(int marker)
        {
            if (marker < 0 || marker > _offset)
                throw new ArgumentOutOfRangeException(nameof(marker));
            _offset = marker;
        }

        /// <summary>
        /// 弹出最近一次 Push
        /// </summary>
        public void PopLast(int size)
        {
            Pop(size);
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                Marshal.FreeHGlobal(_basePtr);
                _basePtr = IntPtr.Zero;
                _disposed = true;
            }
            GC.SuppressFinalize(this);
        }

        ~StackAllocator() => Dispose();
    }

    // ============================================================
    //  9. MemoryStats — 全局内存统计
    // ============================================================

    /// <summary>
    /// 引擎全局内存统计追踪器，按类别统计。
    /// </summary>
    public sealed class MemoryStats
    {
        private static readonly Lazy<MemoryStats> _instance = new(() => new MemoryStats());
        public static MemoryStats Instance => _instance.Value;

        private long _totalAllocated;
        private long _totalFreed;
        private long _peakUsage;
        private long _allocationCount;
        private long _freeCount;

        private readonly long[] _categoryAllocated = new long[Enum.GetValues(typeof(MemoryCategory)).Length];
        private readonly long[] _categoryFreed = new long[Enum.GetValues(typeof(MemoryCategory)).Length];
        private readonly long[] _categoryPeak = new long[Enum.GetValues(typeof(MemoryCategory)).Length];
        private readonly object _lock = new();

        /// <summary>当前总已分配字节数</summary>
        public long TotalAllocated => Interlocked.Read(ref _totalAllocated);

        /// <summary>总已释放字节数</summary>
        public long TotalFreed => Interlocked.Read(ref _totalFreed);

        /// <summary>当前活跃使用字节数</summary>
        public long CurrentUsage => Interlocked.Read(ref _totalAllocated) - Interlocked.Read(ref _totalFreed);

        /// <summary>历史峰值使用</summary>
        public long PeakUsage => Interlocked.Read(ref _peakUsage);

        /// <summary>总分配次数</summary>
        public long AllocationCount => Interlocked.Read(ref _allocationCount);

        /// <summary>总释放次数</summary>
        public long FreeCount => Interlocked.Read(ref _freeCount);

        public MemoryStats()
        {
            // 默认构造
        }

        /// <summary>
        /// 记录一次分配
        /// </summary>
        public void RecordAlloc(long size, MemoryCategory category = MemoryCategory.General)
        {
            Interlocked.Add(ref _totalAllocated, size);
            Interlocked.Increment(ref _allocationCount);

            long currentUsage = Interlocked.Read(ref _totalAllocated) - Interlocked.Read(ref _totalFreed);
            long peak = Interlocked.Read(ref _peakUsage);
            while (currentUsage > peak)
            {
                long oldPeak = Interlocked.CompareExchange(ref _peakUsage, currentUsage, peak);
                if (oldPeak == peak) break;
                peak = oldPeak;
            }

            int catIdx = (int)category;
            lock (_lock)
            {
                _categoryAllocated[catIdx] += size;
                long catCurrent = _categoryAllocated[catIdx] - _categoryFreed[catIdx];
                if (catCurrent > _categoryPeak[catIdx])
                    _categoryPeak[catIdx] = catCurrent;
            }
        }

        /// <summary>
        /// 记录一次释放
        /// </summary>
        public void RecordFree(long size, MemoryCategory category = MemoryCategory.General)
        {
            Interlocked.Add(ref _totalFreed, size);
            Interlocked.Increment(ref _freeCount);

            int catIdx = (int)category;
            lock (_lock)
            {
                _categoryFreed[catIdx] += size;
            }
        }

        /// <summary>
        /// 获取各分类统计快照
        /// </summary>
        public (MemoryCategory Category, long Allocated, long Freed, long Current, long Peak)[] GetCategoryStats()
        {
            var categories = (MemoryCategory[])Enum.GetValues(typeof(MemoryCategory));
            var result = new (MemoryCategory, long, long, long, long)[categories.Length];
            lock (_lock)
            {
                for (int i = 0; i < categories.Length; i++)
                {
                    result[i] = (
                        categories[i],
                        _categoryAllocated[i],
                        _categoryFreed[i],
                        _categoryAllocated[i] - _categoryFreed[i],
                        _categoryPeak[i]
                    );
                }
            }
            return result;
        }

        /// <summary>
        /// 重置所有统计
        /// </summary>
        public void Reset()
        {
            Interlocked.Exchange(ref _totalAllocated, 0);
            Interlocked.Exchange(ref _totalFreed, 0);
            Interlocked.Exchange(ref _peakUsage, 0);
            Interlocked.Exchange(ref _allocationCount, 0);
            Interlocked.Exchange(ref _freeCount, 0);
            lock (_lock)
            {
                Array.Clear(_categoryAllocated);
                Array.Clear(_categoryFreed);
                Array.Clear(_categoryPeak);
            }
        }

        public override string ToString()
        {
            return $"[MemoryStats] Current={CurrentUsage / 1024}KB " +
                   $"Peak={PeakUsage / 1024}KB " +
                   $"Allocs={AllocationCount} " +
                   $"Frees={FreeCount}";
        }
    }

    // ============================================================
    //  10. 引擎专用 ArrayPool<T> 封装
    // ============================================================

    /// <summary>
    /// 引擎专用 ArrayPool 封装，提供分类追踪和统计集成。
    /// 可配置每桶最大数组长度和最大数组数量。
    /// </summary>
    public sealed class EngineArrayPool<T> where T : struct
    {
        private readonly System.Buffers.ArrayPool<T> _innerPool;
        private readonly MemoryCategory _category;
        private long _rentCount;
        private long _returnCount;
        private long _totalBytesRented;

        /// <summary>租用次数</summary>
        public long RentCount => Interlocked.Read(ref _rentCount);

        /// <summary>归还次数</summary>
        public long ReturnCount => Interlocked.Read(ref _returnCount);

        /// <summary>累计租用字节</summary>
        public long TotalBytesRented => Interlocked.Read(ref _totalBytesRented);

        public EngineArrayPool(MemoryCategory category = MemoryCategory.General)
        {
            _innerPool = System.Buffers.ArrayPool<T>.Shared;
            _category = category;
        }

        /// <summary>
        /// 租用至少指定长度的数组
        /// </summary>
        public T[] Rent(int minimumLength)
        {
            var array = _innerPool.Rent(minimumLength);
            Interlocked.Increment(ref _rentCount);
            Interlocked.Add(ref _totalBytesRented, array.Length * Unsafe.SizeOf<T>());
            MemoryStats.Instance.RecordAlloc(array.Length * Unsafe.SizeOf<T>(), _category);
            return array;
        }

        /// <summary>
        /// 归还数组到池中
        /// </summary>
        public void Return(T[] array, bool clearArray = false)
        {
            if (array == null) return;
            _innerPool.Return(array, clearArray);
            Interlocked.Increment(ref _returnCount);
            MemoryStats.Instance.RecordFree(array.Length * Unsafe.SizeOf<T>(), _category);
        }

        /// <summary>
        /// 使用租用数组执行操作，自动归还
        /// </summary>
        public void Using(int minimumLength, Action<T[]> action)
        {
            var buffer = Rent(minimumLength);
            try
            {
                action(buffer);
            }
            finally
            {
                Return(buffer);
            }
        }

        /// <summary>
        /// 使用租用数组执行操作并返回结果，自动归还
        /// </summary>
        public TResult Using<TResult>(int minimumLength, Func<T[], TResult> func)
        {
            var buffer = Rent(minimumLength);
            try
            {
                return func(buffer);
            }
            finally
            {
                Return(buffer);
            }
        }
    }

    // ============================================================
    //  11. LockFreeStack — CAS 无锁栈（用于线程安全 PoolAllocator）
    // ============================================================

    /// <summary>
    /// CAS 无锁栈，用于线程安全版本的 PoolAllocator 入队操作。
    /// </summary>
    internal sealed class LockFreeStack
    {
        private sealed class Node
        {
            public IntPtr Value;
            public Node? Next;
            public Node(IntPtr value) { Value = value; Next = null; }
        }

        private Node? _head;

        /// <summary>
        /// CAS 压栈
        /// </summary>
        public void Push(IntPtr value)
        {
            var newNode = new Node(value);
            do
            {
                newNode.Next = _head;
            }
            while (Interlocked.CompareExchange(ref _head, newNode, newNode.Next) != newNode.Next);
        }

        /// <summary>
        /// CAS 弹栈，空栈返回 IntPtr.Zero
        /// </summary>
        public IntPtr Pop()
        {
            while (true)
            {
                Node? head = _head;
                if (head == null) return IntPtr.Zero;
                Node? next = head.Next;
                if (Interlocked.CompareExchange(ref _head, next, head) == head)
                {
                    return head.Value;
                }
            }
        }

        /// <summary>栈中元素数（近似值）</summary>
        public int Count
        {
            get
            {
                int count = 0;
                Node? current = _head;
                while (current != null)
                {
                    count++;
                    current = current.Next;
                }
                return count;
            }
        }
    }

    // ============================================================
    //  12. ThreadSafePoolAllocator — CAS 无锁版对象池
    // ============================================================

    /// <summary>
    /// 线程安全版 PoolAllocator，使用 CAS 无锁入队。
    /// 适用于粒子系统、ECS 组件等频繁跨线程创建销毁的场景。
    /// </summary>
    public sealed class ThreadSafePoolAllocator : IDisposable
    {
        private readonly int _blockSize;
        private readonly int _blockFullSize;
        private readonly int _blockCount;
        private readonly IntPtr _basePtr;
        private readonly LockFreeStack _freeStack;
        private bool _disposed;
        private long _allocationCount;
        private long _freeCount;

        public int BlockSize => _blockSize;
        public int AvailableBlocks => _freeStack.Count;
        public long TotalAllocations => Interlocked.Read(ref _allocationCount);
        public long TotalFrees => Interlocked.Read(ref _freeCount);

        public ThreadSafePoolAllocator(int blockSize, int blockCount = 4096)
        {
            if (blockSize <= 0) throw new ArgumentOutOfRangeException(nameof(blockSize));
            if (blockCount <= 0) throw new ArgumentOutOfRangeException(nameof(blockCount));

            _blockSize = blockSize;
            _blockFullSize = blockSize + PoolAllocator.HeaderSize;
            _blockCount = blockCount;
            _basePtr = Marshal.AllocHGlobal(_blockFullSize * blockCount);
            _freeStack = new LockFreeStack();

            // 所有块压入无锁栈
            for (int i = blockCount - 1; i >= 0; i--)
            {
                IntPtr blockPtr = _basePtr + i * _blockFullSize;
                _freeStack.Push(blockPtr);
            }
        }

        /// <summary>
        /// CAS 无锁分配
        /// </summary>
        public IntPtr Allocate()
        {
            IntPtr blockPtr = _freeStack.Pop();
            if (blockPtr == IntPtr.Zero)
                throw new OutOfMemoryException("Thread-safe pool exhausted.");

            Interlocked.Increment(ref _allocationCount);
            // 返回用户区域（跳过 header）
            return blockPtr + PoolAllocator.HeaderSize;
        }

        /// <summary>
        /// CAS 无锁释放
        /// </summary>
        public void Free(IntPtr userPtr)
        {
            if (userPtr == IntPtr.Zero) return;

            IntPtr blockPtr = userPtr - PoolAllocator.HeaderSize;

            // 验证归属
            long offset = blockPtr.ToInt64() - _basePtr.ToInt64();
            if (offset < 0 || offset >= (long)_blockCount * _blockFullSize || offset % _blockFullSize != 0)
                throw new ArgumentException("Pointer does not belong to this pool.", nameof(userPtr));

            _freeStack.Push(blockPtr);
            Interlocked.Increment(ref _freeCount);
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                Marshal.FreeHGlobal(_basePtr);
                _disposed = true;
            }
            GC.SuppressFinalize(this);
        }

        ~ThreadSafePoolAllocator() => Dispose();
    }

    // ============================================================
    //  13. MemoryManager — 统一内存管理入口
    // ============================================================

    /// <summary>
    /// 引擎内存管理统一入口，提供各分配器的工厂方法和全局配置。
    /// </summary>
    public static class MemoryManager
    {
        private static PoolAllocator? _defaultPool;
        private static readonly object _poolInitLock = new();

        /// <summary>默认对象池（线程安全）</summary>
        public static PoolAllocator DefaultPool
        {
            get
            {
                if (_defaultPool == null)
                {
                    lock (_poolInitLock)
                    {
                        _defaultPool ??= new PoolAllocator();
                    }
                }
                return _defaultPool;
            }
        }

        /// <summary>全局统计</summary>
        public static MemoryStats Stats => MemoryStats.Instance;

        /// <summary>
        /// 创建帧临时 Arena
        /// </summary>
        public static ArenaAllocator CreateFrameArena(int capacity = 16 * 1024 * 1024)
        {
            return new ArenaAllocator(capacity, 16);
        }

        /// <summary>
        /// 创建 Scratch 分配器
        /// </summary>
        public static ScratchAllocator CreateScratch(int capacity = 4 * 1024 * 1024)
        {
            return new ScratchAllocator(capacity);
        }

        /// <summary>
        /// 创建栈分配器
        /// </summary>
        public static StackAllocator CreateStack(int capacity = 2 * 1024 * 1024)
        {
            return new StackAllocator(capacity);
        }

        /// <summary>
        /// 创建线程安全对象池
        /// </summary>
        public static ThreadSafePoolAllocator CreateThreadSafePool(int blockSize, int count = 4096)
        {
            return new ThreadSafePoolAllocator(blockSize, count);
        }

        /// <summary>
        /// 创建分类 ArrayPool
        /// </summary>
        public static EngineArrayPool<T> CreateArrayPool<T>(MemoryCategory category = MemoryCategory.General)
            where T : struct
        {
            return new EngineArrayPool<T>(category);
        }
    }
}
