// ============================================================================================
//  ObjectPool.cs
//  山雨引擎 (ShanyuEngine) 泛型对象池与帧分配器系统
// ============================================================================================
//  提供多种内存池化管理方案，减少 GC 压力，提升性能：
//    - ObjectPool<T> : 泛型对象池（支持工厂构造/重置委托）
//    - FrameAllocator : 每帧线性分配，帧末整体回退
//    - MemoryPool : 固定大小块的堆式分配器
//    - ScopeAllocator : 作用域分配器（using 语句自动释放）
// ============================================================================================

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

namespace ShanyuEngine.Memory
{
    // ====== 对象池接口 ======
    public interface IObjectPool
    {
        /// <summary>池中当前可用的对象数。</summary>
        int AvailableCount { get; }

        /// <summary>池的总容量。</summary>
        int Capacity { get; }

        /// <summary>清空池。</summary>
        void Clear();
    }

    // ====== 泛型对象池 ======
    /// <summary>
    /// 泛型对象池。用于重用临时对象，降低 GC 压力。
    /// 线程安全。
    /// </summary>
    public class ObjectPool<T> : IObjectPool, IDisposable where T : class
    {
        private readonly ConcurrentBag<T> _objects = new();
        private readonly Func<T> _factory;
        private readonly Action<T>? _onReset;
        private readonly Action<T>? _onGet;
        private volatile bool _disposed;

        public int AvailableCount => _objects.Count;
        public int Capacity { get; }
        public int TotalCreated { get; private set; }

        /// <summary>
        /// 创建对象池。
        /// </summary>
        /// <param name="factory">创建新实例的工厂函数。</param>
        /// <param name="onReset">归还时调用的重置函数。</param>
        /// <param name="onGet">取出时调用的初始化函数。</param>
        /// <param name="capacity">最大容量（0 表示无限制）。</param>
        public ObjectPool(Func<T> factory, Action<T>? onReset = null, Action<T>? onGet = null, int capacity = 0)
        {
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
            _onReset = onReset;
            _onGet = onGet;
            Capacity = capacity;
        }

        /// <summary>从池中获取一个对象。如果池为空则创建新实例。</summary>
        public T Get()
        {
            if (_disposed) return _factory();

            if (_objects.TryTake(out var obj))
            {
                _onGet?.Invoke(obj);
                return obj;
            }

            TotalCreated++;
            var newObj = _factory();
            _onGet?.Invoke(newObj);
            return newObj;
        }

        /// <summary>将对象归还到池中。</summary>
        public void Return(T obj)
        {
            if (_disposed || obj == null) return;

            _onReset?.Invoke(obj);

            if (Capacity > 0 && _objects.Count >= Capacity)
                return; // 超过最大容量，直接丢弃

            _objects.Add(obj);
        }

        /// <summary>预填充池。</summary>
        public void Prewarm(int count)
        {
            for (int i = 0; i < count; i++)
            {
                var obj = _factory();
                _onReset?.Invoke(obj);
                TotalCreated++;
                if (Capacity <= 0 || _objects.Count < Capacity)
                    _objects.Add(obj);
            }
        }

        public void Clear()
        {
            while (_objects.TryTake(out _)) { }
        }

        public void Dispose()
        {
            _disposed = true;
            Clear();
        }
    }

    // ====== 结构体对象池（值类型，无 GC 分配） ======
    /// <summary>
    /// 值类型对象池。不涉及 GC 分配，适合高性能场景。
    /// 用栈数组实现线程本地存储。
    /// </summary>
    public struct StructPool<T> where T : struct
    {
        private T[] _items;
        private int _count;
        private readonly Func<T> _factory;

        public StructPool(int capacity, Func<T> factory)
        {
            _items = new T[capacity];
            _count = 0;
            _factory = factory;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T Get()
        {
            if (_count > 0)
            {
                _count--;
                return _items[_count];
            }
            return _factory();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Return(T item)
        {
            if (_count < _items.Length)
            {
                _items[_count] = item;
                _count++;
            }
        }

        public void Clear() => _count = 0;
    }

    // ====== 帧分配器 ======
    /// <summary>
    /// 每帧线性分配器。在 FrameStart 时标记位置，FrameEnd 时整体回退。
    /// 非常适合每帧临时数据分配。
    /// </summary>
    public sealed class FrameAllocator : IDisposable
    {
        private byte[] _buffer;
        private int _offset;
        private int _markOffset;
        private readonly int _maxFrameAlloc;
        private readonly object _lock = new();

        public int TotalAllocated => _offset;
        public int Capacity => _buffer.Length;
        public int FreeSpace => _buffer.Length - _offset;

        public FrameAllocator(int initialSize = 1024 * 1024, int maxPerFrame = 256 * 1024)
        {
            _buffer = new byte[initialSize];
            _offset = 0;
            _markOffset = 0;
            _maxFrameAlloc = maxPerFrame;
        }

        /// <summary>标记当前帧起始位置。</summary>
        public void FrameStart()
        {
            lock (_lock)
            {
                _markOffset = _offset;
            }
        }

        /// <summary>回滚到帧起始位置。</summary>
        public void FrameEnd()
        {
            lock (_lock)
            {
                _offset = _markOffset;
            }
        }

        /// <summary>分配指定字节数的内存。</summary>
        public IntPtr Allocate(int sizeInBytes, int alignment = 16)
        {
            if (sizeInBytes <= 0) return IntPtr.Zero;

            lock (_lock)
            {
                // 对齐
                int alignedOffset = (_offset + alignment - 1) & ~(alignment - 1);
                int newOffset = alignedOffset + sizeInBytes;

                if (newOffset > _buffer.Length)
                {
                    // 扩大缓冲区
                    int newSize = Math.Max(_buffer.Length * 2, newOffset);
                    Array.Resize(ref _buffer, newSize);
                }

                // 检查单帧分配上限
                int frameAlloc = newOffset - _markOffset;
                if (frameAlloc > _maxFrameAlloc)
                {
                    throw new InvalidOperationException(
                        $"帧分配超出上限: {frameAlloc} > {_maxFrameAlloc}");
                }

                _offset = newOffset;

                // 返回 GCHandle 或直接用 unsafe 指针
                // 由于安全原因，这里返回 IntPtr.Zero 并通过帧缓存访问
                // 实际使用中配合 unsafe 代码
                unsafe
                {
                    fixed (byte* ptr = &_buffer[alignedOffset])
                    {
                        return new IntPtr(ptr);
                    }
                }
            }
        }

        /// <summary>分配并写入结构体。</summary>
        public IntPtr AllocateStruct<T>(in T data) where T : unmanaged
        {
            int size = System.Runtime.InteropServices.Marshal.SizeOf<T>();
            var ptr = Allocate(size);
            System.Runtime.InteropServices.Marshal.StructureToPtr(data, ptr, false);
            return ptr;
        }

        /// <summary>重置所有数据。</summary>
        public void Reset()
        {
            lock (_lock)
            {
                _offset = 0;
                _markOffset = 0;
            }
        }

        public void Dispose()
        {
            _buffer = Array.Empty<byte>();
            _offset = 0;
        }
    }

    // ====== 内存块池 ======
    /// <summary>
    /// 固定大小块的池化管理器。适用于频繁分配/释放同大小内存块的场景。
    /// </summary>
    public sealed class MemoryPool
    {
        private readonly int _blockSize;
        private readonly int _maxBlocks;
        private readonly ConcurrentStack<byte[]> _freeBlocks = new();
        private int _totalAllocated;

        public int BlockSize => _blockSize;
        public int AvailableBlocks => _freeBlocks.Count;
        public int TotalAllocatedBlocks => _totalAllocated;

        public MemoryPool(int blockSize, int maxBlocks = 1024)
        {
            _blockSize = blockSize;
            _maxBlocks = maxBlocks;
        }

        public byte[] Rent()
        {
            if (_freeBlocks.TryPop(out var block))
                return block;

            Interlocked.Increment(ref _totalAllocated);
            return new byte[_blockSize];
        }

        public void Return(byte[] block)
        {
            if (block == null || block.Length != _blockSize) return;

            if (_freeBlocks.Count < _maxBlocks)
                _freeBlocks.Push(block);
        }

        public void Prewarm(int count)
        {
            for (int i = 0; i < count; i++)
            {
                if (_freeBlocks.Count >= _maxBlocks) break;
                var block = new byte[_blockSize];
                _freeBlocks.Push(block);
                Interlocked.Increment(ref _totalAllocated);
            }
        }

        public void Clear()
        {
            while (_freeBlocks.TryPop(out _)) { }
        }
    }

    // ====== 作用域分配器 ======
    /// <summary>
    /// 使用 using 语句的作用域分配器。在作用域结束后自动释放分配的内存。
    /// </summary>
    public ref struct ScopeAllocator
    {
        private Span<byte> _buffer;
        private int _offset;

        public ScopeAllocator(Span<byte> buffer)
        {
            _buffer = buffer;
            _offset = 0;
        }

        public Span<byte> Allocate(int size)
        {
            if (_offset + size > _buffer.Length)
                throw new OutOfMemoryException("ScopeAllocator 空间不足");

            var span = _buffer.Slice(_offset, size);
            _offset += size;
            return span;
        }

        public void Dispose()
        {
            _offset = 0;
        }
    }

    // ====== 线程本地对象池 ======
    /// <summary>
    /// 每个线程独立的对象池，避免锁竞争。
    /// </summary>
    public sealed class ThreadLocalPool<T> where T : class, new()
    {
        private readonly ThreadLocal<Stack<T>> _localPool = new(() => new Stack<T>());
        private readonly Func<T> _factory;

        public ThreadLocalPool(Func<T>? factory = null)
        {
            _factory = factory ?? (() => new T());
        }

        public T Get()
        {
            var stack = _localPool.Value!;
            if (stack.Count > 0)
                return stack.Pop();

            return _factory();
        }

        public void Return(T obj)
        {
            var stack = _localPool.Value!;
            if (stack.Count < 64) // 每线程上限
                stack.Push(obj);
        }

        public void Clear()
        {
            _localPool.Value?.Clear();
        }
    }

    // ====== 池化 List（最常用场景） ======
    /// <summary>
    /// 池化 List<T> —— 引擎中最常用的集合类型。
    /// </summary>
    public static class SharedListPool
    {
        private static readonly ObjectPool<List<int>> _intPool = new(() => new List<int>(32));
        private static readonly ObjectPool<List<float>> _floatPool = new(() => new List<float>(32));

        private static readonly ConcurrentDictionary<Type, object> _pools = new();

        public static List<T> Get<T>()
        {
            var pool = GetPool<T>();
            return pool.Get();
        }

        public static void Return<T>(List<T> list)
        {
            if (list == null) return;
            list.Clear();
            var pool = GetPool<T>();
            pool.Return(list);
        }

        private static ObjectPool<List<T>> GetPool<T>()
        {
            var type = typeof(T);
            if (_pools.TryGetValue(type, out var existing))
                return (ObjectPool<List<T>>)existing;

            var pool = new ObjectPool<List<T>>(
                () => new List<T>(32),
                list => list.Clear());

            _pools[type] = pool;
            return pool;
        }
    }
}
