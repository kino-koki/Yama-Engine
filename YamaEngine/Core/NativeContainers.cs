// ============================================================================================
//  NativeContainers.cs
//  yama引擎 (YamaEngine) 高性能容器库
// ============================================================================================
//  提供针对游戏引擎优化的轻量级容器，减少 GC 分配和装箱开销。
//  包括：NativeList、NativeArray、RingBuffer、SparseSet、SmallBuffer、TokenList。
//  设计参考 Unity NativeContainer + UE TArray/TSet 但更轻量。
// ============================================================================================

using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace YamaEngine.Core
{
    // ====== NativeList —— 值类型动态数组 ======
    /// <summary>
    /// 最小化 GC 分配的动态数组。与 List<T> 功能相似但避免枚举器装箱。
    /// </summary>
    public struct NativeList<T> : IDisposable, IEnumerable<T>
    {
        private T[] _items;
        private int _count;

        public NativeList(int capacity)
        {
            _items = capacity > 0 ? new T[capacity] : Array.Empty<T>();
            _count = 0;
        }

        public int Count => _count;
        public int Capacity => _items.Length;

        public T this[int index]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                if ((uint)index >= (uint)_count) ThrowIndexOutOfRange();
                return _items[index];
            }
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set
            {
                if ((uint)index >= (uint)_count) ThrowIndexOutOfRange();
                _items[index] = value;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Add(T item)
        {
            if (_count >= _items.Length)
                Grow(_count + 1);
            _items[_count++] = item;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void AddRange(ReadOnlySpan<T> items)
        {
            int needed = _count + items.Length;
            if (needed > _items.Length)
                Grow(needed);
            items.CopyTo(new Span<T>(_items, _count, items.Length));
            _count = needed;
        }

        public void RemoveAt(int index)
        {
            if ((uint)index >= (uint)_count) ThrowIndexOutOfRange();
            _count--;
            if (index < _count)
                Array.Copy(_items, index + 1, _items, index, _count - index);
        }

        public bool Remove(T item)
        {
            int index = IndexOf(item);
            if (index >= 0) { RemoveAt(index); return true; }
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int IndexOf(T item) => Array.IndexOf(_items, item, 0, _count);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Contains(T item) => IndexOf(item) >= 0;

        public void Clear()
        {
            if (_items != null)
                Array.Clear(_items, 0, _count);
            _count = 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Span<T> AsSpan() => new Span<T>(_items, 0, _count);

        public T[] ToArray()
        {
            var result = new T[_count];
            Array.Copy(_items, result, _count);
            return result;
        }

        private void Grow(int minCapacity)
        {
            int newCapacity = _items.Length == 0 ? 4 : _items.Length * 2;
            if (newCapacity < minCapacity) newCapacity = minCapacity;
            Array.Resize(ref _items, newCapacity);
        }

        public void Dispose() { _items = Array.Empty<T>(); _count = 0; }

        private static void ThrowIndexOutOfRange() => throw new IndexOutOfRangeException("NativeList 索引越界");

        public Enumerator GetEnumerator() => new Enumerator(_items, _count);
        IEnumerator<T> IEnumerable<T>.GetEnumerator() => GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public struct Enumerator : IEnumerator<T>
        {
            private readonly T[] _items;
            private readonly int _count;
            private int _index;
            internal Enumerator(T[] items, int count) { _items = items; _count = count; _index = -1; }
            public T Current => _items[_index];
            object? IEnumerator.Current => Current;
            public bool MoveNext() => ++_index < _count;
            public void Reset() => _index = -1;
            public void Dispose() { }
        }
    }

    // ====== 小缓冲区优化数组 ======
    /// <summary>
    /// 存储小数据量的小型数组。在栈上分配固定大小缓冲区，超出后才堆分配。
    /// 避免小列表的 GC 分配。
    /// </summary>
    public struct SmallBuffer<T> : IDisposable
    {
        private const int InlineCapacity = 8;
        private T[]? _heapBuffer;
        private int _count;
        // 前 InlineCapacity 个元素存储在栈上（通过 stackalloc 或外部 Span）
        // 此处简化：直接用长度为 InlineCapacity 的堆数组模拟（实际应使用 unsafe）
        private T _v0, _v1, _v2, _v3, _v4, _v5, _v6, _v7;

        public SmallBuffer(ReadOnlySpan<T> initial) : this()
        {
            foreach (ref readonly var item in initial)
                Add(item);
        }

        public int Count => _count;

        public T this[int index]
        {
            get
            {
                if ((uint)index >= (uint)_count) ThrowOutOfRange();
                if (index < InlineCapacity)
                    return GetInline(index);
                return _heapBuffer![index - InlineCapacity];
            }
            set
            {
                if ((uint)index >= (uint)_count) ThrowOutOfRange();
                if (index < InlineCapacity)
                    SetInline(index, value);
                else
                    _heapBuffer![index - InlineCapacity] = value;
            }
        }

        public void Add(T item)
        {
            if (_count < InlineCapacity)
            {
                SetInline(_count, item);
                _count++;
            }
            else
            {
                if (_heapBuffer == null || _count - InlineCapacity >= _heapBuffer.Length)
                {
                    int newSize = _heapBuffer?.Length * 2 ?? 4;
                    var newBuf = new T[newSize];
                    if (_heapBuffer != null)
                        Array.Copy(_heapBuffer, newBuf, _count - InlineCapacity);
                    _heapBuffer = newBuf;
                }
                _heapBuffer![_count - InlineCapacity] = item;
                _count++;
            }
        }

        public void Clear() { _count = 0; _heapBuffer = null; }
        public void Dispose() { Clear(); }

        private T GetInline(int i) => i switch
        {
            0 => _v0, 1 => _v1, 2 => _v2, 3 => _v3,
            4 => _v4, 5 => _v5, 6 => _v6, 7 => _v7,
            _ => throw new IndexOutOfRangeException()
        };
        private void SetInline(int i, T v) { switch (i) { case 0: _v0 = v; break; case 1: _v1 = v; break; case 2: _v2 = v; break; case 3: _v3 = v; break; case 4: _v4 = v; break; case 5: _v5 = v; break; case 6: _v6 = v; break; case 7: _v7 = v; break; } }
        private static void ThrowOutOfRange() => throw new IndexOutOfRangeException("SmallBuffer 索引越界");
    }

    // ====== RingBuffer —— 环形缓冲区 ======
    /// <summary>
    /// 定长环形缓冲区。适用于音频缓冲区、历史记录、帧缓存等场景。
    /// </summary>
    public struct RingBuffer<T>
    {
        private readonly T[] _buffer;
        private int _head;
        private int _tail;
        private int _count;

        public RingBuffer(int capacity)
        {
            _buffer = new T[capacity];
            _head = 0;
            _tail = 0;
            _count = 0;
        }

        public int Count => _count;
        public int Capacity => _buffer.Length;
        public bool IsFull => _count == _buffer.Length;
        public bool IsEmpty => _count == 0;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Enqueue(T item)
        {
            _buffer[_tail] = item;
            _tail = (_tail + 1) % _buffer.Length;
            if (_count < _buffer.Length) _count++;
            else _head = (_head + 1) % _buffer.Length; // 覆盖最老
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T Dequeue()
        {
            if (_count == 0) throw new InvalidOperationException("RingBuffer 为空");
            var item = _buffer[_head];
            _head = (_head + 1) % _buffer.Length;
            _count--;
            return item;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryDequeue(out T item)
        {
            if (_count == 0) { item = default!; return false; }
            item = Dequeue();
            return true;
        }

        public T Peek()
        {
            if (_count == 0) throw new InvalidOperationException("RingBuffer 为空");
            return _buffer[_head];
        }

        public void Clear() { _head = 0; _tail = 0; _count = 0; }

        public T this[int index]
        {
            get
            {
                if ((uint)index >= (uint)_count) throw new IndexOutOfRangeException();
                return _buffer[(_head + index) % _buffer.Length];
            }
        }
    }

    // ====== SparseSet —— 稀疏集 ======
    /// <summary>
    /// 稀疏集（Dense+Sparse 双数组）。ECS Archetype 的核心数据结构。
    /// 支持 O(1) 的增删查和紧凑迭代。
    /// </summary>
    public struct SparseSet<T> : IDisposable
    {
        private int[] _sparse;    // ExternalID → dense-index
        private int[] _dense;     // dense-index → ExternalID
        private T[] _denseValues; // dense-index → Value
        private int _count;

        public SparseSet(int maxSparse = 64)
        {
            _sparse = new int[maxSparse];
            Array.Fill(_sparse, -1);
            _dense = new int[16];
            _denseValues = new T[16];
            _count = 0;
        }

        public int Count => _count;
        public int Capacity => _dense.Length;

        public bool Has(int externalId)
        {
            if ((uint)externalId >= (uint)_sparse.Length) return false;
            int denseIdx = _sparse[externalId];
            return denseIdx >= 0 && denseIdx < _count && _dense[denseIdx] == externalId;
        }

        public ref T Get(int externalId)
        {
            if (!Has(externalId)) throw new KeyNotFoundException($"SparseSet 未找到 ID {externalId}");
            return ref _denseValues[_sparse[externalId]];
        }

        public bool TryGet(int externalId, out T value)
        {
            if (Has(externalId))
            {
                value = _denseValues[_sparse[externalId]];
                return true;
            }
            value = default!;
            return false;
        }

        public void Add(int externalId, in T value)
        {
            EnsureSparseCapacity(externalId);
            if (Has(externalId)) { Get(externalId) = value; return; }

            if (_count >= _dense.Length)
            {
                int newCap = _dense.Length * 2;
                Array.Resize(ref _dense, newCap);
                Array.Resize(ref _denseValues, newCap);
            }

            _dense[_count] = externalId;
            _denseValues[_count] = value;
            _sparse[externalId] = _count;
            _count++;
        }

        public bool Remove(int externalId)
        {
            if (!Has(externalId)) return false;

            int idx = _sparse[externalId];
            int lastIdx = _count - 1;

            // 与最后一个元素交换
            if (idx != lastIdx)
            {
                int lastId = _dense[lastIdx];
                _dense[idx] = lastId;
                _denseValues[idx] = _denseValues[lastIdx];
                _sparse[lastId] = idx;
            }

            _sparse[externalId] = -1;
            _count--;
            return true;
        }

        public void Clear()
        {
            for (int i = 0; i < _count; i++)
                _sparse[_dense[i]] = -1;
            _count = 0;
        }

        private void EnsureSparseCapacity(int id)
        {
            if (id >= _sparse.Length)
            {
                int newSize = Math.Max(_sparse.Length * 2, id + 1);
                int[] newSparse = new int[newSize];
                Array.Fill(newSparse, -1);
                Array.Copy(_sparse, newSparse, _sparse.Length);
                _sparse = newSparse;
            }
        }

        public void Dispose() { Clear(); _sparse = Array.Empty<int>(); _dense = Array.Empty<int>(); _denseValues = Array.Empty<T>(); }

        // 紧凑迭代
        public ReadOnlySpan<T> Values => new ReadOnlySpan<T>(_denseValues, 0, _count);
        public ReadOnlySpan<int> IDs => new ReadOnlySpan<int>(_dense, 0, _count);
    }

    // ====== TokenList —— 带 ID 的条目列表 ======
    /// <summary>
    /// 每个元素拥有唯一 Token（uint）的列表。适合资源句柄管理。
    /// 删除不会移动其他元素（用 nextFree 链表追踪空闲槽）。
    /// </summary>
    public sealed class TokenList<T> : IEnumerable<T> where T : class
    {
        private T?[] _items = Array.Empty<T?>();
        private uint[] _generations = Array.Empty<uint>();
        private int _freeHead = -1;
        private int _count;

        public int Count => _count;

        public struct Token : IEquatable<Token>
        {
            public uint Id;
            public uint Generation;
            public bool Equals(Token other) => Id == other.Id && Generation == other.Generation;
            public override bool Equals(object? obj) => obj is Token t && Equals(t);
            public override int GetHashCode() => HashCode.Combine(Id, Generation);
            public static bool operator ==(Token a, Token b) => a.Equals(b);
            public static bool operator !=(Token a, Token b) => !a.Equals(b);
        }

        public Token Add(T item)
        {
            if (_freeHead >= 0)
            {
                int idx = _freeHead;
                // 从 free list 头部读取 nextFree（存储在 _items 索引位置，以 Generation 低24位存储）
                _freeHead = (int)(_generations[idx] & 0xFFFFFF);
                _items[idx] = item;
                return new Token { Id = (uint)idx, Generation = _generations[idx] };
            }

            int newIdx = _items.Length;
            Array.Resize(ref _items, newIdx + 1);
            Array.Resize(ref _generations, newIdx + 1);
            _items[newIdx] = item;
            return new Token { Id = (uint)newIdx, Generation = _generations[newIdx] };
        }

        public T? Get(Token token)
        {
            int idx = (int)token.Id;
            if ((uint)idx >= (uint)_items.Length) return null;
            if (_generations[idx] != token.Generation) return null;
            return _items[idx];
        }

        public bool Remove(Token token)
        {
            int idx = (int)token.Id;
            if ((uint)idx >= (uint)_items.Length) return false;
            if (_generations[idx] != token.Generation) return false;

            _items[idx] = null;
            _generations[idx] = ((_generations[idx] + 1) & 0xFFFFFF) | (uint)_freeHead << 24;
            _freeHead = idx;
            _count--;
            return true;
        }

        public void Clear()
        {
            Array.Clear(_items);
            _freeHead = -1;
            _count = 0;
        }

        public IEnumerator<T> GetEnumerator()
        {
            for (int i = 0; i < _items.Length; i++)
                if (_items[i] != null) yield return _items[i]!;
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
