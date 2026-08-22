// ════════════════════════════════════════════════════════════════════════════
// 山雨引擎 - 组件数据访问器 (ComponentDataAccessor)
//
// 提供类型安全的组件数据访问。封装了从 ArchetypeChunk 读取/写入组件数据的细节。
//
// 主要功能:
//   * 泛型组件访问 (ComponentDataAccessor<T>)
//   * Span 访问（零拷贝）
//   * 多组件同时访问
//   * 只读/读写访问控制
//   * Unsafe 指针访问（高性能路径）
//
// 参考 Unity ECS 的 ArchetypeChunk.GetNativeArray
//
// FIXME: unsafe 指针路径还没做引用计数
// TODO: 加个只读访问器优化（只读不需要写回）
// ════════════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ShanyuEngine.ECS
{
    /// <summary>
    /// 组件数据访问器：提供类型安全的组件数据访问。
    /// </summary>
    public ref struct ComponentDataAccessor<T> where T : struct
    {
        private readonly ArchetypeChunk _chunk;
        private readonly int _componentTypeIndex;
        private readonly Span<T> _dataSpan;

        /// <summary>是否有效</summary>
        public bool IsValid => _chunk != null;

        /// <summary>组件数量</summary>
        public int Length => _dataSpan.Length;

        /// <summary>按索引访问组件数据</summary>
        public ref T this[int index] => ref _dataSpan[index];

        /// <summary>
        /// 从 chunk 创建访问器。
        /// </summary>
        public ComponentDataAccessor(ArchetypeChunk chunk)
        {
            _chunk = chunk;
            // FIXME: 实际应该从 chunk 获取组件类型索引并获取托管数组的 Span
            // 这里简化处理，创建临时数组
            _componentTypeIndex = -1;
            _dataSpan = Span<T>.Empty;
        }

        /// <summary>
        /// 从 chunk 创建访问器（指定类型索引）。
        /// </summary>
        public ComponentDataAccessor(ArchetypeChunk chunk, int componentTypeIndex)
        {
            _chunk = chunk;
            _componentTypeIndex = componentTypeIndex;
            // 获取组件数据的 Span
            _dataSpan = GetComponentSpan(chunk, componentTypeIndex);
        }

        /// <summary>
        /// 获取组件数据的 Span。
        /// </summary>
        public Span<T> AsSpan() => _dataSpan;

        /// <summary>
        /// 获取只读 Span。
        /// </summary>
        public ReadOnlySpan<T> AsReadOnlySpan() => _dataSpan;

        /// <summary>
        /// 获取组件数据的数组。
        /// </summary>
        public T[] ToArray() => _dataSpan.ToArray();

        /// <summary>
        /// 从另一个数组复制数据到组件。
        /// </summary>
        public void CopyFrom(ReadOnlySpan<T> source)
        {
            source.CopyTo(_dataSpan);
        }

        /// <summary>
        /// 把组件数据复制到另一个数组。
        /// </summary>
        public void CopyTo(Span<T> destination)
        {
            _dataSpan.CopyTo(destination);
        }

        /// <summary>
        /// 获取 unsafe 指针。
        /// </summary>
        public unsafe T* GetUnsafePtr()
        {
            return (T*)Unsafe.AsPointer(ref MemoryMarshal.GetReference(_dataSpan));
        }

        /// <summary>
        /// 获取第一个元素的引用。
        /// </summary>
        public ref T GetFirst() => ref _dataSpan[0];

        /// <summary>
        /// 获取最后一个元素的引用。
        /// </summary>
        public ref T GetLast() => ref _dataSpan[_dataSpan.Length - 1];

        // ──────────────────────────────────────────────────────────────────────
        // 辅助方法
        // ──────────────────────────────────────────────────────────────────────

        private static Span<T> GetComponentSpan(ArchetypeChunk chunk, int componentTypeIndex)
        {
            if (chunk == null || componentTypeIndex < 0)
                return Span<T>.Empty;

            // FIXME: 实际应该从 chunk 的内部数组获取 Span
            // 这里用替代方案：创建临时数组
            int count = chunk.EntityCount;
            if (count == 0) return Span<T>.Empty;

            var array = new T[count];
            return array.AsSpan();
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 多组件访问器
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 双组件访问器：同时访问两个组件类型。
    /// </summary>
    public ref struct ComponentDataAccessor<T1, T2>
        where T1 : struct
        where T2 : struct
    {
        public ComponentDataAccessor<T1> First;
        public ComponentDataAccessor<T2> Second;

        public int Length => First.Length;

        public ComponentDataAccessor(ArchetypeChunk chunk)
        {
            First = new ComponentDataAccessor<T1>(chunk);
            Second = new ComponentDataAccessor<T2>(chunk);
        }

        /// <summary>同时获取两个组件的值</summary>
        public (T1, T2) this[int index] => (First[index], Second[index]);
    }

    /// <summary>
    /// 三组件访问器。
    /// </summary>
    public ref struct ComponentDataAccessor<T1, T2, T3>
        where T1 : struct
        where T2 : struct
        where T3 : struct
    {
        public ComponentDataAccessor<T1> First;
        public ComponentDataAccessor<T2> Second;
        public ComponentDataAccessor<T3> Third;

        public int Length => First.Length;

        public ComponentDataAccessor(ArchetypeChunk chunk)
        {
            First = new ComponentDataAccessor<T1>(chunk);
            Second = new ComponentDataAccessor<T2>(chunk);
            Third = new ComponentDataAccessor<T3>(chunk);
        }

        /// <summary>同时获取三个组件的值</summary>
        public (T1, T2, T3) this[int index] => (First[index], Second[index], Third[index]);
    }

    /// <summary>
    /// 四组件访问器。
    /// </summary>
    public ref struct ComponentDataAccessor<T1, T2, T3, T4>
        where T1 : struct
        where T2 : struct
        where T3 : struct
        where T4 : struct
    {
        public ComponentDataAccessor<T1> First;
        public ComponentDataAccessor<T2> Second;
        public ComponentDataAccessor<T3> Third;
        public ComponentDataAccessor<T4> Fourth;

        public int Length => First.Length;

        public ComponentDataAccessor(ArchetypeChunk chunk)
        {
            First = new ComponentDataAccessor<T1>(chunk);
            Second = new ComponentDataAccessor<T2>(chunk);
            Third = new ComponentDataAccessor<T3>(chunk);
            Fourth = new ComponentDataAccessor<T4>(chunk);
        }

        /// <summary>同时获取四个组件的值</summary>
        public (T1, T2, T3, T4) this[int index] =>
            (First[index], Second[index], Third[index], Fourth[index]);
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 只读访问器
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 只读组件数据访问器。
    /// </summary>
    public ref struct ReadOnlyComponentDataAccessor<T> where T : struct
    {
        private readonly ReadOnlySpan<T> _data;

        public int Length => _data.Length;

        public ref readonly T this[int index] => ref _data[index];

        public ReadOnlyComponentDataAccessor(ArchetypeChunk chunk)
        {
            var accessor = new ComponentDataAccessor<T>(chunk);
            _data = accessor.AsReadOnlySpan();
        }

        public ReadOnlySpan<T> AsSpan() => _data;
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 可选组件访问器
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 可选组件访问器：组件可能不存在于 chunk 中。
    /// 如果存在，HasValue 为 true。
    /// </summary>
    public ref struct OptionalComponentDataAccessor<T> where T : struct
    {
        private readonly ComponentDataAccessor<T> _accessor;
        private readonly bool _hasValue;

        /// <summary>组件是否存在</summary>
        public bool HasValue => _hasValue;

        /// <summary>长度（如果不存在则为 0）</summary>
        public int Length => _hasValue ? _accessor.Length : 0;

        public OptionalComponentDataAccessor(ArchetypeChunk chunk)
        {
            // 检查 chunk 是否有这个组件
            _hasValue = chunk != null; // FIXME: 实际检查
            _accessor = _hasValue ? new ComponentDataAccessor<T>(chunk) : default;
        }

        /// <summary>获取访问器（如果存在）</summary>
        public bool TryGet(out ComponentDataAccessor<T> accessor)
        {
            if (_hasValue)
            {
                accessor = _accessor;
                return true;
            }
            accessor = default;
            return false;
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // Entity 访问器
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Entity 访问器：提供 chunk 中实体 ID 的访问。
    /// </summary>
    public ref struct EntityAccessor
    {
        private readonly ArchetypeChunk _chunk;
        private readonly Span<EntityReal> _entities;

        public int Length => _entities.Length;

        public ref EntityReal this[int index] => ref _entities[index];

        public EntityAccessor(ArchetypeChunk chunk)
        {
            _chunk = chunk;
            // FIXME: 实际从 chunk 获取实体数组
            _entities = Span<EntityReal>.Empty;
        }

        public Span<EntityReal> AsSpan() => _entities;
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 带实体的组件访问器
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 带实体引用的组件访问器：同时访问实体 ID 和组件数据。
    /// </summary>
    public ref struct EntityComponentAccessor<T> where T : struct
    {
        public EntityAccessor Entities;
        public ComponentDataAccessor<T> Component;

        public int Length => Component.Length;

        public EntityComponentAccessor(ArchetypeChunk chunk)
        {
            Entities = new EntityAccessor(chunk);
            Component = new ComponentDataAccessor<T>(chunk);
        }

        /// <summary>同时获取实体和组件</summary>
        public (EntityReal entity, T component) this[int index] =>
            (Entities[index], Component[index]);
    }

    /// <summary>
    /// 带实体引用的双组件访问器。
    /// </summary>
    public ref struct EntityComponentAccessor<T1, T2>
        where T1 : struct
        where T2 : struct
    {
        public EntityAccessor Entities;
        public ComponentDataAccessor<T1> Component1;
        public ComponentDataAccessor<T2> Component2;

        public int Length => Component1.Length;

        public EntityComponentAccessor(ArchetypeChunk chunk)
        {
            Entities = new EntityAccessor(chunk);
            Component1 = new ComponentDataAccessor<T1>(chunk);
            Component2 = new ComponentDataAccessor<T2>(chunk);
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 访问器辅助方法
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 组件访问器辅助方法。
    /// </summary>
    public static class ComponentAccessorExtensions
    {
        /// <summary>从 chunk 创建组件访问器</summary>
        public static ComponentDataAccessor<T> GetAccessor<T>(this ArchetypeChunk chunk) where T : struct
        {
            return new ComponentDataAccessor<T>(chunk);
        }

        /// <summary>从 chunk 创建只读访问器</summary>
        public static ReadOnlyComponentDataAccessor<T> GetReadOnlyAccessor<T>(this ArchetypeChunk chunk) where T : struct
        {
            return new ReadOnlyComponentDataAccessor<T>(chunk);
        }

        /// <summary>从 chunk 创建可选访问器</summary>
        public static OptionalComponentDataAccessor<T> GetOptionalAccessor<T>(this ArchetypeChunk chunk) where T : struct
        {
            return new OptionalComponentDataAccessor<T>(chunk);
        }

        /// <summary>从 chunk 创建实体访问器</summary>
        public static EntityAccessor GetEntityAccessor(this ArchetypeChunk chunk)
        {
            return new EntityAccessor(chunk);
        }

        /// <summary>从 chunk 创建带实体的组件访问器</summary>
        public static EntityComponentAccessor<T> GetEntityComponentAccessor<T>(this ArchetypeChunk chunk) where T : struct
        {
            return new EntityComponentAccessor<T>(chunk);
        }

        /// <summary>获取单个实体的组件数据</summary>
        public static T GetComponent<T>(this ArchetypeChunk chunk, int entityIndex) where T : struct
        {
            var accessor = new ComponentDataAccessor<T>(chunk);
            return accessor[entityIndex];
        }

        /// <summary>设置单个实体的组件数据</summary>
        public static void SetComponent<T>(this ArchetypeChunk chunk, int entityIndex, in T value) where T : struct
        {
            var accessor = new ComponentDataAccessor<T>(chunk);
            accessor[entityIndex] = value;
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 动态组件访问器（非泛型）
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 动态组件访问器：运行时通过类型索引访问组件。
    /// 比泛型版本慢，但更灵活。
    /// </summary>
    public sealed class DynamicComponentAccessor
    {
        private readonly ArchetypeChunk _chunk;
        private readonly ComponentTypeReal _componentType;

        /// <summary>组件类型</summary>
        public ComponentTypeReal ComponentType => _componentType;

        /// <summary>组件大小</summary>
        public int ComponentSize => _componentType.Size;

        /// <summary>实体数</summary>
        public int Length => _chunk?.EntityCount ?? 0;

        public DynamicComponentAccessor(ArchetypeChunk chunk, ComponentTypeReal componentType)
        {
            _chunk = chunk;
            _componentType = componentType;
        }

        /// <summary>获取组件数据（装箱）</summary>
        public object Get(int index)
        {
            // FIXME: 实际应该用反射或 unsafe
            return null;
        }

        /// <summary>设置组件数据（拆箱）</summary>
        public void Set(int index, object value)
        {
            // FIXME: 实际应该用反射或 unsafe
        }

        /// <summary>获取组件数据的 byte span</summary>
        public Span<byte> AsByteSpan()
        {
            // FIXME: 实际应该返回底层内存
            return Span<byte>.Empty;
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 组件缓冲区（用于批量操作）
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 组件缓冲区：临时存储组件数据用于批量操作。
    /// </summary>
    public sealed class ComponentBuffer<T> : IDisposable where T : struct
    {
        private T[] _buffer;
        private int _count;
        private bool _disposed;

        /// <summary>容量</summary>
        public int Capacity => _buffer?.Length ?? 0;

        /// <summary>当前数量</summary>
        public int Count => _count;

        /// <summary>是否已满</summary>
        public bool IsFull => _count >= Capacity;

        public ComponentBuffer(int capacity)
        {
            _buffer = new T[capacity];
            _count = 0;
        }

        /// <summary>添加数据</summary>
        public void Add(in T value)
        {
            if (_count >= Capacity)
                throw new InvalidOperationException("缓冲区已满");
            _buffer[_count++] = value;
        }

        /// <summary>批量添加</summary>
        public void AddRange(ReadOnlySpan<T> values)
        {
            foreach (ref readonly var v in values)
                Add(v);
        }

        /// <summary>获取 Span</summary>
        public Span<T> AsSpan() => _buffer.AsSpan(0, _count);

        /// <summary>获取只读 Span</summary>
        public ReadOnlySpan<T> AsReadOnlySpan() => _buffer.AsSpan(0, _count);

        /// <summary>清空</summary>
        public void Clear()
        {
            _count = 0;
        }

        /// <summary>按索引访问</summary>
        public ref T this[int index] => ref _buffer[index];

        /// <summary>转换为数组</summary>
        public T[] ToArray()
        {
            var result = new T[_count];
            Array.Copy(_buffer, result, _count);
            return result;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _buffer = null;
            _disposed = true;
        }
    }
}
