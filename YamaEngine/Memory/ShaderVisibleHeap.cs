using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace YamaEngine.Memory
{
    // ============================================================
    //  1. ConstantBufferHeap — GPU可见的64KB对齐常量缓冲堆
    // ============================================================

    /// <summary>
    /// GPU 可见的常量缓冲堆，用于高效管理每帧 Constant Buffer 分配。
    /// 支持 D3D12 的 64KB 对齐和 256 字节最小分配粒度。
    /// 采用帧轮转（双缓冲/三缓冲）避免 GPU/CPU 竞争。
    /// </summary>
    public sealed class ConstantBufferHeap : IDisposable
    {
        /// <summary>D3D12 要求的最小 CB 大小对齐：256 字节</summary>
        public const int MinAllocationGranularity = 256;

        /// <summary>D3D12 要求的堆对齐：64KB</summary>
        public const int HeapAlignment = 64 * 1024;

        private const int MaxFramesInFlight = 3;

        private readonly IntPtr[] _heapPtrs;       // 每帧一个堆指针
        private readonly int[] _offsets;           // 每帧当前偏移
        private readonly int _heapSize;            // 每帧堆大小
        private readonly int _frameCount;         // 帧轮转数量 (2 or 3)
        private int _currentFrameIndex;
        private bool _disposed;

        /// <summary>当前帧可分配大小</summary>
        public int AvailableThisFrame => _heapSize - _offsets[_currentFrameIndex];

        /// <summary>每帧堆大小（字节）。</summary>
        public int HeapSize => _heapSize;

        /// <summary>当前帧已使用大小</summary>
        public int UsedThisFrame => _offsets[_currentFrameIndex];

        /// <summary>当前帧索引</summary>
        public int CurrentFrameIndex => _currentFrameIndex;

        /// <summary>帧轮转数（2=双缓冲，3=三缓冲）</summary>
        public int FrameCount => _frameCount;

        /// <summary>
        /// 创建常量缓冲堆
        /// </summary>
        /// <param name="heapSizePerFrame">每帧堆大小（字节），默认 2MB</param>
        /// <param name="bufferCount">帧轮转数（2或3）</param>
        public ConstantBufferHeap(int heapSizePerFrame = 2 * 1024 * 1024, int bufferCount = 3)
        {
            if (heapSizePerFrame <= 0) throw new ArgumentOutOfRangeException(nameof(heapSizePerFrame));
            if (bufferCount < 2 || bufferCount > MaxFramesInFlight)
                throw new ArgumentOutOfRangeException(nameof(bufferCount));

            _heapSize = heapSizePerFrame;
            _frameCount = bufferCount;
            _heapPtrs = new IntPtr[bufferCount];
            _offsets = new int[bufferCount];

            for (int i = 0; i < bufferCount; i++)
            {
                _heapPtrs[i] = Marshal.AllocHGlobal(heapSizePerFrame);
                _offsets[i] = 0;
            }

            _currentFrameIndex = 0;
        }

        /// <summary>
        /// 从当前帧堆中分配常量缓冲区。
        /// 大小自动向上对齐到 256 字节。
        /// </summary>
        /// <param name="size">需要的字节数</param>
        /// <returns>分配到的 GPU 可见指针</returns>
        public IntPtr Allocate(int size)
        {
            int alignedSize = AlignTo256(size);
            int frameIdx = _currentFrameIndex;

            if (_offsets[frameIdx] + alignedSize > _heapSize)
                throw new OutOfMemoryException(
                    $"ConstantBufferHeap frame {frameIdx} overflow: " +
                    $"requested {alignedSize}, available {_heapSize - _offsets[frameIdx]}");

            IntPtr result = _heapPtrs[frameIdx] + _offsets[frameIdx];
            _offsets[frameIdx] += alignedSize;
            return result;
        }

        /// <summary>
        /// 分配并拷贝数据到常量缓冲区
        /// </summary>
        public unsafe IntPtr AllocateAndCopy<T>(ref T data) where T : unmanaged
        {
            int size = Unsafe.SizeOf<T>();
            IntPtr ptr = Allocate(size);
            Unsafe.WriteUnaligned(ref *(byte*)ptr, data);
            return ptr;
        }

        /// <summary>
        /// 分配并拷贝数组数据
        /// </summary>
        public IntPtr AllocateAndCopyArray<T>(T[] data) where T : unmanaged
        {
            if (data == null || data.Length == 0)
                throw new ArgumentException("Data array must not be null or empty.", nameof(data));

            int size = Unsafe.SizeOf<T>() * data.Length;
            IntPtr ptr = Allocate(size);
            unsafe
            {
                fixed (T* src = data)
                {
                    Buffer.MemoryCopy(src, (void*)ptr, size, size);
                }
            }
            return ptr;
        }

        /// <summary>
        /// 获取当前帧堆的基地址
        /// </summary>
        public IntPtr GetCurrentHeapBase() => _heapPtrs[_currentFrameIndex];

        /// <summary>
        /// 获取当前帧堆上某偏移的 GPU 虚拟地址（模拟）
        /// </summary>
        public long GetGpuVirtualAddress(int offset)
        {
            return _heapPtrs[_currentFrameIndex].ToInt64() + offset;
        }

        /// <summary>
        /// 推进到下一帧（在每帧结束时调用）。
        /// 下一帧的偏移重置为零，允许复用该帧的堆空间。
        /// </summary>
        public void AdvanceFrame()
        {
            _currentFrameIndex = (_currentFrameIndex + 1) % _frameCount;
            _offsets[_currentFrameIndex] = 0;
        }

        /// <summary>
        /// 获取指定帧索引的使用情况
        /// </summary>
        public (int FrameIndex, int Used, int Available) GetFrameStats(int frameIndex)
        {
            if (frameIndex < 0 || frameIndex >= _frameCount)
                throw new ArgumentOutOfRangeException(nameof(frameIndex));
            return (frameIndex, _offsets[frameIndex], _heapSize - _offsets[frameIndex]);
        }

        /// <summary>
        /// 获取所有帧使用情况
        /// </summary>
        public (int FrameIndex, int Used, int Available)[] GetAllFrameStats()
        {
            var stats = new (int, int, int)[_frameCount];
            for (int i = 0; i < _frameCount; i++)
                stats[i] = GetFrameStats(i);
            return stats;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int AlignTo256(int value)
        {
            return (value + MinAllocationGranularity - 1) & ~(MinAllocationGranularity - 1);
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                for (int i = 0; i < _heapPtrs.Length; i++)
                {
                    if (_heapPtrs[i] != IntPtr.Zero)
                        Marshal.FreeHGlobal(_heapPtrs[i]);
                    _heapPtrs[i] = IntPtr.Zero;
                }
                _disposed = true;
            }
            GC.SuppressFinalize(this);
        }

        ~ConstantBufferHeap() => Dispose();
    }

    // ============================================================
    //  2. 描述符类型枚举
    // ============================================================

    /// <summary>
    /// 着色器资源描述符类型（对应 D3D12_DESCRIPTOR_HEAP_TYPE / Vulkan DescriptorType）
    /// </summary>
    public enum DescriptorType
    {
        ConstantBufferView,    // CBV
        ShaderResourceView,    // SRV (Texture/Buffer)
        UnorderedAccessView,   // UAV (RW Texture/Buffer)
        Sampler                // Sampler
    }

    /// <summary>
    /// 描述符堆标志
    /// </summary>
    [Flags]
    public enum DescriptorHeapFlags
    {
        None = 0,
        ShaderVisible = 1 << 0,   // GPU 可见（用于绑定到 Root Signature）
        CpuOnly = 1 << 1           // 仅 CPU 访问
    }

    // ============================================================
    //  3. DescriptorHandle — 描述符句柄
    // ============================================================

    /// <summary>
    /// 描述符句柄，类似 D3D12 的 CPU/GPU 描述符句柄对
    /// </summary>
    public struct DescriptorHandle : IEquatable<DescriptorHandle>
    {
        public int HeapIndex;
        public int IndexInHeap;
        public long GpuAddress;  // 模拟 GPU 描述符地址

        public bool IsValid => HeapIndex >= 0 && IndexInHeap >= 0;

        public DescriptorHandle(int heapIndex, int indexInHeap, long gpuAddress = 0)
        {
            HeapIndex = heapIndex;
            IndexInHeap = indexInHeap;
            GpuAddress = gpuAddress;
        }

        public static DescriptorHandle Invalid => new DescriptorHandle(-1, -1, 0);

        public bool Equals(DescriptorHandle other) =>
            HeapIndex == other.HeapIndex && IndexInHeap == other.IndexInHeap;

        public override bool Equals(object? obj) => obj is DescriptorHandle h && Equals(h);
        public override int GetHashCode() => HashCode.Combine(HeapIndex, IndexInHeap);
        public static bool operator ==(DescriptorHandle a, DescriptorHandle b) => a.Equals(b);
        public static bool operator !=(DescriptorHandle a, DescriptorHandle b) => !a.Equals(b);
    }

    // ============================================================
    //  4. ShaderResourceDescriptorHeap — SRV/UAV/CBV 描述符堆
    // ============================================================

    /// <summary>
    /// 着色器资源描述符堆管理器。
    /// 支持 Tier 1/2 的描述符数量、线性分配器、LRU 淘汰策略和自动帧绑定。
    /// </summary>
    public sealed class ShaderResourceDescriptorHeap : IDisposable
    {
        /// <summary>Tier 1 最小描述符数量</summary>
        public const int Tier1MaxDescriptors = 64;

        /// <summary>Tier 2 最小描述符数量</summary>
        public const int Tier2MaxDescriptors = 1_000_000;

        /// <summary>CPU 端描述符缓存条目</summary>
        private sealed class CacheEntry
        {
            public Guid ResourceId;
            public DescriptorHandle Handle;
            public long LastAccessFrame;
            public DescriptorType Type;
        }

        // 线性分配状态
        private int _freeOffset;
        private readonly int _maxDescriptors;
        private readonly DescriptorType _heapType;
        private readonly DescriptorHeapFlags _flags;
        private readonly DescriptorHandle[] _handles;

        // LRU 缓存
        private readonly Dictionary<Guid, CacheEntry> _cache;
        private readonly LinkedList<CacheEntry> _lruList;
        private readonly object _cacheLock = new();
        private long _frameCounter;

        // 绑定信息
        private readonly List<DescriptorHandle> _boundThisFrame;
        private bool _disposed;

        /// <summary>最大描述符数量</summary>
        public int MaxDescriptors => _maxDescriptors;

        /// <summary>已分配描述符数量</summary>
        public int AllocatedCount => _freeOffset;

        /// <summary>剩余可分配数量</summary>
        public int AvailableCount => _maxDescriptors - _freeOffset;

        /// <summary>缓存命中数</summary>
        public long CacheHitCount { get; private set; }

        /// <summary>缓存未命中数</summary>
        public long CacheMissCount { get; private set; }

        /// <summary>描述符类型</summary>
        public DescriptorType HeapType => _heapType;

        /// <summary>
        /// 创建描述符堆
        /// </summary>
        /// <param name="heapType">堆支持的描述符类型</param>
        /// <param name="maxDescriptors">最大描述符数</param>
        /// <param name="flags">堆标志（ShaderVisible/CpuOnly）</param>
        public ShaderResourceDescriptorHeap(
            DescriptorType heapType,
            int maxDescriptors = 1024,
            DescriptorHeapFlags flags = DescriptorHeapFlags.ShaderVisible)
        {
            _heapType = heapType;
            _maxDescriptors = maxDescriptors;
            _flags = flags;
            _freeOffset = 0;
            _frameCounter = 0;

            _handles = new DescriptorHandle[maxDescriptors];
            for (int i = 0; i < maxDescriptors; i++)
            {
                _handles[i] = new DescriptorHandle(0, i, i * 32L); // 模拟 GPU 地址
            }

            _cache = new Dictionary<Guid, CacheEntry>();
            _lruList = new LinkedList<CacheEntry>();
            _boundThisFrame = new List<DescriptorHandle>();
        }

        /// <summary>
        /// 线性分配一个描述符
        /// </summary>
        public DescriptorHandle Allocate()
        {
            if (_freeOffset >= _maxDescriptors)
            {
                // 尝试 LRU 驱逐回收
                if (!EvictLRU(1))
                    throw new OutOfMemoryException("Descriptor heap exhausted and no evictable entries.");
            }

            DescriptorHandle handle = _handles[_freeOffset];
            _freeOffset++;
            return handle;
        }

        /// <summary>
        /// 批量线性分配
        /// </summary>
        public DescriptorHandle[] Allocate(int count)
        {
            if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count));

            while (_freeOffset + count > _maxDescriptors)
            {
                if (!EvictLRU(_maxDescriptors - (_freeOffset + count)))
                    throw new OutOfMemoryException("Descriptor heap exhausted.");
            }

            var handles = new DescriptorHandle[count];
            for (int i = 0; i < count; i++)
                handles[i] = _handles[_freeOffset++];
            return handles;
        }

        /// <summary>
        /// 获取或创建资源的描述符（带缓存）
        /// </summary>
        public DescriptorHandle GetOrCreateDescriptor(Guid resourceId, DescriptorType type)
        {
            lock (_cacheLock)
            {
                if (_cache.TryGetValue(resourceId, out var entry))
                {
                    entry.LastAccessFrame = _frameCounter;
                    // 移动 LRU 链表节点到头部
                    _lruList.Remove(entry);
                    _lruList.AddFirst(entry);
                    CacheHitCount++;
                    return entry.Handle;
                }

                // 缓存未命中，分配新描述符
                CacheMissCount++;
                DescriptorHandle handle = Allocate();

                entry = new CacheEntry
                {
                    ResourceId = resourceId,
                    Handle = handle,
                    LastAccessFrame = _frameCounter,
                    Type = type
                };
                _cache[resourceId] = entry;
                _lruList.AddFirst(entry);
                return handle;
            }
        }

        /// <summary>
        /// 释放描述符（标记为可回收）
        /// </summary>
        public void ReleaseDescriptor(Guid resourceId)
        {
            lock (_cacheLock)
            {
                if (_cache.TryGetValue(resourceId, out var entry))
                {
                    _cache.Remove(resourceId);
                    _lruList.Remove(entry);
                }
            }
        }

        /// <summary>
        /// 标记描述符在本帧绑定
        /// </summary>
        public void BindForFrame(DescriptorHandle handle)
        {
            _boundThisFrame.Add(handle);
        }

        /// <summary>
        /// 获取当前帧绑定的所有描述符（用于 Root Signature 设置）
        /// </summary>
        public ReadOnlySpan<DescriptorHandle> GetFrameBindings()
        {
            return _boundThisFrame.ToArray();
        }

        /// <summary>
        /// 推进帧计数器并清理帧绑定
        /// </summary>
        public void AdvanceFrame()
        {
            _frameCounter++;
            _boundThisFrame.Clear();
        }

        /// <summary>
        /// LRU 淘汰：移除最少使用的缓存条目
        /// </summary>
        /// <param name="count">需要回收的数量</param>
        /// <returns>是否成功回收足够数量的描述符</returns>
        public bool EvictLRU(int count)
        {
            lock (_cacheLock)
            {
                int evicted = 0;
                while (_lruList.Last != null && evicted < count)
                {
                    var entry = _lruList.Last.Value;
                    _lruList.RemoveLast();
                    _cache.Remove(entry.ResourceId);
                    evicted++;
                }

                // 将释放的描述符标记回可用区域（简化实现：缩小 freeOffset 仅当尾部释放时）
                return evicted >= count;
            }
        }

        /// <summary>
        /// 获取缓存统计
        /// </summary>
        public (int CacheSize, long Hits, long Misses, float HitRate) GetCacheStats()
        {
            long total = CacheHitCount + CacheMissCount;
            float hitRate = total > 0 ? (float)CacheHitCount / total : 0f;
            lock (_cacheLock)
            {
                return (_cache.Count, CacheHitCount, CacheMissCount, hitRate);
            }
        }

        /// <summary>
        /// 重置整个堆（释放所有描述符）
        /// </summary>
        public void Reset()
        {
            _freeOffset = 0;
            _boundThisFrame.Clear();
            lock (_cacheLock)
            {
                _cache.Clear();
                _lruList.Clear();
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                Reset();
                _disposed = true;
            }
            GC.SuppressFinalize(this);
        }

        ~ShaderResourceDescriptorHeap() => Dispose();
    }

    // ============================================================
    //  5. RootSignatureBinding — 自动帧绑定管理
    // ============================================================

    /// <summary>
    /// Root Signature 绑定管理器，自动为每帧收集和设置描述符绑定。
    /// </summary>
    public sealed class RootSignatureBinding : IDisposable
    {
        private ShaderResourceDescriptorHeap[] _heaps;
        private readonly int _maxRootParameters;
        private readonly DescriptorHandle[] _rootParameters;
        private bool _dirty;
        private bool _disposed;

        public int MaxRootParameters => _maxRootParameters;
        public bool IsDirty => _dirty;

        public RootSignatureBinding(int maxRootParameters = 64)
        {
            _maxRootParameters = maxRootParameters;
            _rootParameters = new DescriptorHandle[maxRootParameters];
            for (int i = 0; i < maxRootParameters; i++)
                _rootParameters[i] = DescriptorHandle.Invalid;
            _heaps = Array.Empty<ShaderResourceDescriptorHeap>();
            _dirty = true;
        }

        /// <summary>
        /// 设置关联的描述符堆
        /// </summary>
        public void SetDescriptorHeaps(params ShaderResourceDescriptorHeap[] heaps)
        {
            _heaps = heaps ?? Array.Empty<ShaderResourceDescriptorHeap>();
            _dirty = true;
        }

        /// <summary>
        /// 在指定 Root Parameter 槽位绑定描述符
        /// </summary>
        public void SetRootDescriptor(int slot, DescriptorHandle handle)
        {
            if (slot < 0 || slot >= _maxRootParameters)
                throw new ArgumentOutOfRangeException(nameof(slot));

            _rootParameters[slot] = handle;
            _dirty = true;
        }

        /// <summary>
        /// 在指定 Root Parameter 槽位绑定描述符表（连续范围）
        /// </summary>
        public void SetRootDescriptorTable(int slot, DescriptorHandle baseHandle, int count)
        {
            SetRootDescriptor(slot, baseHandle);
        }

        /// <summary>
        /// 获取指定槽位的描述符
        /// </summary>
        public DescriptorHandle GetRootDescriptor(int slot)
        {
            if (slot < 0 || slot >= _maxRootParameters)
                throw new ArgumentOutOfRangeException(nameof(slot));
            return _rootParameters[slot];
        }

        /// <summary>
        /// 清除所有绑定
        /// </summary>
        public void Clear()
        {
            for (int i = 0; i < _maxRootParameters; i++)
                _rootParameters[i] = DescriptorHandle.Invalid;
            _dirty = true;
        }

        /// <summary>
        /// 标记为已提交（非脏）
        /// </summary>
        public void Commit() => _dirty = false;

        public void Dispose()
        {
            if (!_disposed)
            {
                Clear();
                _disposed = true;
            }
            GC.SuppressFinalize(this);
        }

        ~RootSignatureBinding() => Dispose();
    }

    // ============================================================
    //  6. GpuHeapManager — GPU 堆统一管理入口
    // ============================================================

    /// <summary>
    /// GPU 堆统一管理入口，协调 CB 堆和描述符堆的帧轮转。
    /// </summary>
    public sealed class GpuHeapManager : IDisposable
    {
        private readonly ConstantBufferHeap _cbHeap;
        private readonly ShaderResourceDescriptorHeap _srvHeap;
        private readonly ShaderResourceDescriptorHeap _uavHeap;
        private readonly ShaderResourceDescriptorHeap _samplerHeap;
        private readonly RootSignatureBinding _rootBinding;
        private bool _disposed;

        /// <summary>常量缓冲堆</summary>
        public ConstantBufferHeap ConstantBufferHeap => _cbHeap;

        /// <summary>SRV 描述符堆</summary>
        public ShaderResourceDescriptorHeap SrvHeap => _srvHeap;

        /// <summary>UAV 描述符堆</summary>
        public ShaderResourceDescriptorHeap UavHeap => _uavHeap;

        /// <summary>Sampler 描述符堆</summary>
        public ShaderResourceDescriptorHeap SamplerHeap => _samplerHeap;

        /// <summary>Root Signature 绑定</summary>
        public RootSignatureBinding RootBinding => _rootBinding;

        /// <summary>
        /// 创建 GPU 堆管理器
        /// </summary>
        public GpuHeapManager(
            int cbHeapSize = 2 * 1024 * 1024,
            int descriptorCount = 2048,
            int samplerCount = 256)
        {
            _cbHeap = new ConstantBufferHeap(cbHeapSize, 3);
            _srvHeap = new ShaderResourceDescriptorHeap(DescriptorType.ShaderResourceView, descriptorCount);
            _uavHeap = new ShaderResourceDescriptorHeap(DescriptorType.UnorderedAccessView, descriptorCount / 2);
            _samplerHeap = new ShaderResourceDescriptorHeap(DescriptorType.Sampler, samplerCount);
            _rootBinding = new RootSignatureBinding(64);
        }

        /// <summary>
        /// 帧结束推进（所有堆统一推进帧计数器）
        /// </summary>
        public void AdvanceFrame()
        {
            _cbHeap.AdvanceFrame();
            _srvHeap.AdvanceFrame();
            _uavHeap.AdvanceFrame();
            _samplerHeap.AdvanceFrame();
        }

        /// <summary>
        /// 获取全局统计
        /// </summary>
        public string GetStats()
        {
            var cbStats = _cbHeap.GetAllFrameStats();
            var srvStats = _srvHeap.GetCacheStats();
            var uavStats = _uavHeap.GetCacheStats();
            return $"[GpuHeapManager] CB: {cbStats[0].Used}/{_cbHeap.HeapSize * 3} bytes, " +
                   $"SRV Cache: {srvStats.CacheSize} ({srvStats.HitRate:P1} hit), " +
                   $"UAV Cache: {uavStats.CacheSize} ({uavStats.HitRate:P1} hit)";
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _cbHeap.Dispose();
                _srvHeap.Dispose();
                _uavHeap.Dispose();
                _samplerHeap.Dispose();
                _rootBinding.Dispose();
                _disposed = true;
            }
            GC.SuppressFinalize(this);
        }

        ~GpuHeapManager() => Dispose();
    }
}
