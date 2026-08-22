using System;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Diagnostics;

namespace ShanyuEngine.Network
{
    // ════════════════════════════════════════════════════════════════════════
    // 山雨引擎 - 真实属性复制引擎 (NetworkReplicationReal)
    //
    // 包含:
    //   * ReplicatedAttribute: 标记需要复制的属性
    //   * INetworkSerializableReal: 自定义可序列化接口
    //   * NetworkValueSerializer: 按类型分派的值序列化/反序列化/比较
    //   * ReplicatedPropertyReal<T>: 带脏标记/频率/条件的复制属性
    //   * ReplicationSerializer: 反射序列化器 (位掩码 + 按优先级)
    //   * DeltaSerializer: 增量序列化器 (只发送变化的属性)
    //   * ReplicationPriorityQueue: 最大堆优先级队列 (带宽预算)
    //   * NetworkObjectReplicator: 网络对象复制器 (每帧 Tick)
    //
    // 所有方法均为真实实现, 无占位式实现。
    // 目标框架: net8.0 (C# 8.0+), 无额外 NuGet 依赖。
    // ════════════════════════════════════════════════════════════════════════

    // ──────────────────────────────────────────────────────────
    // 属性与接口定义
    // ──────────────────────────────────────────────────────────

    /// <summary>
    /// 标记属性需要网络复制。
    /// </summary>
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]

    /// <summary>复制条件枚举。</summary>
    public enum ReplicationCondition
    {
        /// <summary>总是复制。</summary>
        Always = 0,
        /// <summary>仅在值变化时复制。</summary>
        OnChange = 1,
        /// <summary>仅复制给所有者。</summary>
        OwnerOnly = 2,
        /// <summary>仅复制给可见连接。</summary>
        IfVisible = 3
    }

    /// <summary>
    /// 自定义网络可序列化接口。
    /// 实现此接口的类型可被复制系统和 RPC 系统使用。
    /// </summary>
    public interface INetworkSerializableReal
    {
        void Serialize(NetworkWriterReal writer);
        void Deserialize(NetworkReaderReal reader);
    }

    // ════════════════════════════════════════════════════════════
    // NetworkValueSerializer - 按类型分派的值序列化工具
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// 按运行时类型分派的值序列化/反序列化/比较工具。
    /// 被复制序列化器和 RPC 系统共用。
    /// </summary>
    public static class NetworkValueSerializer
    {
        /// <summary>按类型将值序列化到写入器。</summary>
        public static void SerializeValue(NetworkWriterReal writer, object value, Type type)
        {
            if (value == null)
            {
                writer.WriteBool(false);
                return;
            }
            writer.WriteBool(true);

            if (type == typeof(int)) writer.WriteInt32((int)value);
            else if (type == typeof(uint)) writer.WriteUInt32((uint)value);
            else if (type == typeof(float)) writer.WriteFloat((float)value);
            else if (type == typeof(double)) writer.WriteDouble((double)value);
            else if (type == typeof(bool)) writer.WriteBool((bool)value);
            else if (type == typeof(byte)) writer.WriteByte((byte)value);
            else if (type == typeof(sbyte)) writer.WriteSByte((sbyte)value);
            else if (type == typeof(short)) writer.WriteInt16((short)value);
            else if (type == typeof(ushort)) writer.WriteUInt16((ushort)value);
            else if (type == typeof(long)) writer.WriteInt64((long)value);
            else if (type == typeof(ulong)) writer.WriteUInt64((ulong)value);
            else if (type == typeof(string)) writer.WriteString((string)value);
            else if (type == typeof(Vector2)) writer.WriteVector2((Vector2)value);
            else if (type == typeof(Vector3)) writer.WriteVector3((Vector3)value);
            else if (type == typeof(Vector4)) writer.WriteVector4((Vector4)value);
            else if (type == typeof(Quaternion)) writer.WriteQuaternion((Quaternion)value);
            else if (type == typeof(Matrix4x4)) writer.WriteMatrix4x4((Matrix4x4)value);
            else if (typeof(INetworkSerializableReal).IsAssignableFrom(type))
                ((INetworkSerializableReal)value).Serialize(writer);
            else if (type.IsArray)
            {
                var elementType = type.GetElementType();
                var array = (Array)value;
                writer.WriteInt32(array.Length);
                foreach (var item in array)
                    SerializeValue(writer, item, elementType);
            }
            else
                throw new NotSupportedException($"Type {type.FullName} is not supported for network serialization");
        }

        /// <summary>按类型从读取器反序列化值。</summary>
        public static object DeserializeValue(NetworkReaderReal reader, Type type)
        {
            bool hasValue = reader.ReadBool();
            if (!hasValue) return type.IsValueType ? Activator.CreateInstance(type) : null;

            if (type == typeof(int)) return reader.ReadInt32();
            if (type == typeof(uint)) return reader.ReadUInt32();
            if (type == typeof(float)) return reader.ReadFloat();
            if (type == typeof(double)) return reader.ReadDouble();
            if (type == typeof(bool)) return reader.ReadBool();
            if (type == typeof(byte)) return reader.ReadByte();
            if (type == typeof(sbyte)) return reader.ReadSByte();
            if (type == typeof(short)) return reader.ReadInt16();
            if (type == typeof(ushort)) return reader.ReadUInt16();
            if (type == typeof(long)) return reader.ReadInt64();
            if (type == typeof(ulong)) return reader.ReadUInt64();
            if (type == typeof(string)) return reader.ReadString();
            if (type == typeof(Vector2)) return reader.ReadVector2();
            if (type == typeof(Vector3)) return reader.ReadVector3();
            if (type == typeof(Vector4)) return reader.ReadVector4();
            if (type == typeof(Quaternion)) return reader.ReadQuaternion();
            if (type == typeof(Matrix4x4)) return reader.ReadMatrix4x4();
            if (typeof(INetworkSerializableReal).IsAssignableFrom(type))
            {
                var obj = (INetworkSerializableReal)Activator.CreateInstance(type);
                obj.Deserialize(reader);
                return obj;
            }
            if (type.IsArray)
            {
                var elementType = type.GetElementType();
                int length = reader.ReadInt32();
                var array = Array.CreateInstance(elementType, length);
                for (int i = 0; i < length; i++)
                    array.SetValue(DeserializeValue(reader, elementType), i);
                return array;
            }
            throw new NotSupportedException($"Type {type.FullName} is not supported for network deserialization");
        }

        /// <summary>比较两个值是否相等 (考虑浮点阈值)。</summary>
        public static bool ValuesEqual(object a, object b, Type type, float threshold = 0f)
        {
            if (a == null && b == null) return true;
            if (a == null || b == null) return false;

            if (type == typeof(float))
            {
                float fa = (float)a, fb = (float)b;
                return Math.Abs(fa - fb) <= threshold;
            }
            if (type == typeof(double))
            {
                double da = (double)a, db = (double)b;
                return Math.Abs(da - db) <= threshold;
            }
            if (type == typeof(Vector2))
            {
                float dist = Vector2.Distance((Vector2)a, (Vector2)b);
                return dist <= threshold;
            }
            if (type == typeof(Vector3))
            {
                float dist = Vector3.Distance((Vector3)a, (Vector3)b);
                return dist <= threshold;
            }
            if (type == typeof(Vector4))
            {
                float dist = Vector4.Distance((Vector4)a, (Vector4)b);
                return dist <= threshold;
            }
            if (type == typeof(Quaternion))
            {
                float dot = Quaternion.Dot((Quaternion)a, (Quaternion)b);
                return Math.Abs(dot) >= 1f - threshold;
            }

            return Equals(a, b);
        }

        /// <summary>估算类型的序列化大小 (字节)。</summary>
        public static int EstimateSize(Type type)
        {
            if (type == typeof(int) || type == typeof(uint) || type == typeof(float)) return 5;
            if (type == typeof(long) || type == typeof(ulong) || type == typeof(double)) return 9;
            if (type == typeof(bool) || type == typeof(byte) || type == typeof(sbyte)) return 2;
            if (type == typeof(short) || type == typeof(ushort)) return 3;
            if (type == typeof(string)) return 32;
            if (type == typeof(Vector2)) return 9;
            if (type == typeof(Vector3)) return 13;
            if (type == typeof(Vector4) || type == typeof(Quaternion)) return 17;
            if (type == typeof(Matrix4x4)) return 65;
            return 32; // 默认估计
        }
    }

    // ════════════════════════════════════════════════════════════
    // ReplicatedPropertyReal<T> - 复制属性
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// 泛型复制属性, 封装脏标记、复制频率和条件判断。
    /// </summary>
    /// <typeparam name="T">属性值类型。</typeparam>
    public class ReplicatedPropertyReal<T>
    {
        private T _currentValue;
        private T _lastSyncedValue;
        private bool _dirty;
        private float _replicationRateHz;
        private double _lastSyncTime;
        private float _minChangeThreshold;
        private Func<object, bool> _conditionFunc;

        /// <summary>当前值。</summary>
        public T Value => _currentValue;

        /// <summary>上一同步值。</summary>
        public T LastSyncedValue => _lastSyncedValue;

        /// <summary>是否为脏 (需要同步)。</summary>
        public bool IsDirty => _dirty;

        /// <summary>复制频率限制 (Hz, 0 = 无限制)。</summary>
        public float ReplicationRateHz
        {
            get => _replicationRateHz;
            set => _replicationRateHz = value;
        }

        /// <summary>最小变化阈值 (浮点类型有效)。</summary>
        public float MinChangeThreshold
        {
            get => _minChangeThreshold;
            set => _minChangeThreshold = value;
        }

        /// <summary>创建复制属性。</summary>
        /// <param name="initialValue">初始值。</param>
        /// <param name="replicationRateHz">复制频率 (Hz, 0 = 无限制)。</param>
        public ReplicatedPropertyReal(T initialValue = default, float replicationRateHz = 0f)
        {
            _currentValue = initialValue;
            _lastSyncedValue = initialValue;
            _dirty = false;
            _replicationRateHz = replicationRateHz;
            _lastSyncTime = 0;
            _minChangeThreshold = 0f;
        }

        /// <summary>
        /// 设置值。如果值发生变化 (超过阈值), 标记为脏。
        /// </summary>
        public void SetValue(T value)
        {
            if (!NetworkValueSerializer.ValuesEqual(value, _currentValue, typeof(T), _minChangeThreshold))
            {
                _currentValue = value;
                _dirty = true;
            }
        }

        /// <summary>强制标记为脏。</summary>
        public void MarkDirty() => _dirty = true;

        /// <summary>清除脏标记。</summary>
        public void ClearDirty() => _dirty = false;

        /// <summary>
        /// 判断是否应该复制到指定连接。
        /// 算法: 脏标记 AND 频率检查 AND 条件检查
        /// </summary>
        /// <param name="connection">目标连接 (传给条件函数)。</param>
        public bool ShouldReplicate(object connection)
        {
            if (!_dirty) return false;

            // 频率检查
            if (_replicationRateHz > 0)
            {
                double now = GetTime();
                double minInterval = 1.0 / _replicationRateHz;
                if (now - _lastSyncTime < minInterval)
                    return false;
            }

            // 条件检查
            if (_conditionFunc != null && !_conditionFunc(connection))
                return false;

            return true;
        }

        /// <summary>设置条件函数。</summary>
        public void SetCondition(Func<object, bool> condition)
        {
            _conditionFunc = condition;
        }

        /// <summary>
        /// 序列化当前值到写入器。
        /// 序列化后清除脏标记, 更新同步时间和上一同步值。
        /// </summary>
        public void Serialize(NetworkWriterReal writer)
        {
            NetworkValueSerializer.SerializeValue(writer, _currentValue, typeof(T));
            _lastSyncedValue = _currentValue;
            _dirty = false;
            _lastSyncTime = GetTime();
        }

        /// <summary>
        /// 从读取器反序列化。
        /// </summary>
        public void Deserialize(NetworkReaderReal reader)
        {
            _currentValue = (T)NetworkValueSerializer.DeserializeValue(reader, typeof(T));
            _lastSyncedValue = _currentValue;
            _dirty = false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static double GetTime()
        {
            return (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency;
        }
    }

    // ════════════════════════════════════════════════════════════
    // ReplicationSerializer - 反射序列化器
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// 反射序列化器: 扫描 [Replicated] 标记的属性,
    /// 按优先级排序, 使用位掩码标记哪些属性被包含。
    /// </summary>
    public class ReplicationSerializer
    {
        // ── 元数据缓存 ────────────────────────────────────────────

        private readonly Dictionary<Type, PropertyMetadata[]> _metadataCache = new Dictionary<Type, PropertyMetadata[]>();
        private readonly object _cacheLock = new object();

        private class PropertyMetadata
        {
            public PropertyInfo Info;
            public ReplicatedAttribute Attribute;
            public int Index;
        }

        /// <summary>
        /// 序列化对象的所有脏复制属性。
        /// 格式: [uint32 位掩码] [脏属性值...]
        /// </summary>
        public void SerializeObject(object obj, NetworkWriterReal writer)
        {
            if (obj == null) { writer.WriteUInt32(0); return; }

            var metadata = GetMetadata(obj.GetType());

            // 构建位掩码: 哪些属性需要序列化
            uint mask = 0;
            for (int i = 0; i < metadata.Length && i < 32; i++)
            {
                // 检查属性是否需要同步 (简单版: 总是同步, DeltaSerializer 做增量)
                mask |= (1u << i);
            }

            writer.WriteUInt32(mask);

            // 按优先级顺序序列化属性
            for (int i = 0; i < metadata.Length && i < 32; i++)
            {
                if ((mask & (1u << i)) != 0)
                {
                    object value = metadata[i].Info.GetValue(obj);
                    NetworkValueSerializer.SerializeValue(writer, value, metadata[i].Info.PropertyType);
                }
            }
        }

        /// <summary>
        /// 反序列化对象属性。
        /// 格式: [uint32 位掩码] [属性值...]
        /// </summary>
        public void DeserializeObject(object obj, NetworkReaderReal reader)
        {
            if (obj == null) { reader.ReadUInt32(); return; }

            var metadata = GetMetadata(obj.GetType());
            uint mask = reader.ReadUInt32();

            for (int i = 0; i < metadata.Length && i < 32; i++)
            {
                if ((mask & (1u << i)) != 0)
                {
                    object value = NetworkValueSerializer.DeserializeValue(reader, metadata[i].Info.PropertyType);
                    try { metadata[i].Info.SetValue(obj, value); }
                    catch { /* 忽略设置失败 */ }
                }
            }
        }

        /// <summary>获取对象的复制属性元数据 (带缓存, 按优先级排序)。</summary>
        public PropertyMetadata[] GetMetadata(Type type)
        {
            lock (_cacheLock)
            {
                if (_metadataCache.TryGetValue(type, out var cached))
                    return cached;
            }

            var properties = type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            var list = new List<PropertyMetadata>();

            foreach (var prop in properties)
            {
                var attr = prop.GetCustomAttribute<ReplicatedAttribute>();
                if (attr != null && prop.CanRead && prop.CanWrite)
                {
                    list.Add(new PropertyMetadata
                    {
                        Info = prop,
                        Attribute = attr,
                        Index = list.Count
                    });
                }
            }

            // 按优先级降序排序
            list.Sort((a, b) => b.Attribute.Priority.CompareTo(a.Attribute.Priority));

            // 重新分配索引 (排序后)
            for (int i = 0; i < list.Count; i++)
                list[i].Index = i;

            var result = list.ToArray();

            lock (_cacheLock)
            {
                _metadataCache[type] = result;
            }

            return result;
        }

        /// <summary>估算对象序列化后的字节大小。</summary>
        public int EstimateObjectSize(object obj)
        {
            if (obj == null) return 4;
            var metadata = GetMetadata(obj.GetType());
            int size = 4; // 位掩码
            foreach (var m in metadata)
                size += NetworkValueSerializer.EstimateSize(m.Info.PropertyType);
            return size;
        }
    }

    // ════════════════════════════════════════════════════════════
    // DeltaSerializer - 增量序列化器
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// 增量序列化器: 只发送变化的属性 (基于上次同步值)。
    /// 支持浮点阈值和 32-bit 位掩码优化。
    /// </summary>
    public class DeltaSerializer
    {
        private readonly ReplicationSerializer _baseSerializer;
        private readonly Dictionary<(ulong, int), object> _lastSyncedValues = new Dictionary<(ulong, int), object>();
        private readonly object _lock = new object();

        public DeltaSerializer()
        {
            _baseSerializer = new ReplicationSerializer();
        }

        /// <summary>
        /// 增量序列化对象。
        /// 算法:
        ///   1. 获取所有 [Replicated] 属性 (已按优先级排序)
        ///   2. 逐个比较当前值与上次同步值 (考虑浮点阈值)
        ///   3. 构建位掩码: 变化的属性标记为 1
        ///   4. 只序列化变化的属性
        ///   5. 更新上次同步值
        /// </summary>
        /// <param name="obj">要序列化的对象。</param>
        /// <param name="objectId">对象唯一 ID。</param>
        /// <param name="writer">目标写入器。</param>
        /// <returns>是否有任何属性被序列化。</returns>
        public bool SerializeDelta(object obj, ulong objectId, NetworkWriterReal writer)
        {
            if (obj == null)
            {
                writer.WriteUInt32(0);
                return false;
            }

            var metadata = _baseSerializer.GetMetadata(obj.GetType());

            // 构建位掩码: 哪些属性发生了变化
            uint mask = 0;
            var currentValues = new object[metadata.Length];

            for (int i = 0; i < metadata.Length && i < 32; i++)
            {
                currentValues[i] = metadata[i].Info.GetValue(obj);

                object lastValue;
                lock (_lock)
                {
                    _lastSyncedValues.TryGetValue((objectId, i), out lastValue);
                }

                float threshold = metadata[i].Attribute.MinChangeThreshold;
                bool changed = !NetworkValueSerializer.ValuesEqual(currentValues[i], lastValue,
                    metadata[i].Info.PropertyType, threshold);

                if (changed)
                    mask |= (1u << i);
            }

            writer.WriteUInt32(mask);

            // 序列化变化的属性
            bool anyChanged = false;
            for (int i = 0; i < metadata.Length && i < 32; i++)
            {
                if ((mask & (1u << i)) != 0)
                {
                    NetworkValueSerializer.SerializeValue(writer, currentValues[i], metadata[i].Info.PropertyType);
                    anyChanged = true;

                    // 更新上次同步值
                    lock (_lock)
                    {
                        _lastSyncedValues[(objectId, i)] = currentValues[i];
                    }
                }
            }

            return anyChanged;
        }

        /// <summary>
        /// 反序列化增量数据。
        /// 与 ReplicationSerializer.DeserializeObject 相同。
        /// </summary>
        public void DeserializeDelta(object obj, NetworkReaderReal reader)
        {
            _baseSerializer.DeserializeObject(obj, reader);
        }

        /// <summary>清除指定对象的所有增量记录。</summary>
        public void ClearObject(ulong objectId)
        {
            lock (_lock)
            {
                var keysToRemove = new List<(ulong, int)>();
                foreach (var key in _lastSyncedValues.Keys)
                {
                    if (key.Item1 == objectId)
                        keysToRemove.Add(key);
                }
                foreach (var key in keysToRemove)
                    _lastSyncedValues.Remove(key);
            }
        }

        /// <summary>清除所有增量记录。</summary>
        public void ClearAll()
        {
            lock (_lock)
            {
                _lastSyncedValues.Clear();
            }
        }
    }

    // ════════════════════════════════════════════════════════════
    // ReplicationPriorityQueue - 复制优先级队列 (最大堆)
    // ════════════════════════════════════════════════════════════

    /// <summary>复制队列条目。</summary>
    public struct ReplicationEntry
    {
        public ulong ObjectId;
        public object Object;
        public double Score;          // 优先级分数 (越高越优先)
        public int EstimatedSize;     // 估计序列化大小
        public System.Net.IPEndPoint Target;
    }

    /// <summary>
    /// 复制优先级队列: 基于最大堆实现。
    /// 支持带宽预算: 每帧固定带宽, 按优先级分配, 超出预算的低优先级推迟。
    /// </summary>
    public class ReplicationPriorityQueue
    {
        private ReplicationEntry[] _heap = new ReplicationEntry[64];
        private int _count;
        private int _bandwidthBudgetBytes;
        private int _usedBandwidthBytes;

        /// <summary>设置每帧带宽预算 (字节)。</summary>
        public void SetBandwidthBudget(int bytesPerFrame)
        {
            _bandwidthBudgetBytes = bytesPerFrame;
        }

        /// <summary>当前队列中的条目数。</summary>
        public int Count => _count;

        /// <summary>已使用的带宽 (字节)。</summary>
        public int UsedBandwidth => _usedBandwidthBytes;

        /// <summary>剩余带宽 (字节)。</summary>
        public int RemainingBandwidth => _bandwidthBudgetBytes - _usedBandwidthBytes;

        /// <summary>入队: 添加到堆末尾, 向上冒泡。</summary>
        public void Enqueue(ReplicationEntry entry)
        {
            if (_count >= _heap.Length)
            {
                Array.Resize(ref _heap, _heap.Length * 2);
            }

            _heap[_count] = entry;
            BubbleUp(_count);
            _count++;
        }

        /// <summary>
        /// 出队: 取出堆顶 (最高优先级)。
        /// 如果超出带宽预算, 返回 false。
        /// </summary>
        public bool TryDequeue(out ReplicationEntry entry)
        {
            if (_count == 0)
            {
                entry = default;
                return false;
            }

            // 带宽预算检查
            if (_usedBandwidthBytes + _heap[0].EstimatedSize > _bandwidthBudgetBytes && _bandwidthBudgetBytes > 0)
            {
                entry = default;
                return false;
            }

            entry = _heap[0];
            _count--;

            if (_count > 0)
            {
                _heap[0] = _heap[_count];
                _heap[_count] = default;
                BubbleDown(0);
            }
            else
            {
                _heap[0] = default;
            }

            _usedBandwidthBytes += entry.EstimatedSize;
            return true;
        }

        /// <summary>重置带宽计数器 (每帧开始调用)。</summary>
        public void ResetBandwidth()
        {
            _usedBandwidthBytes = 0;
        }

        /// <summary>清空队列。</summary>
        public void Clear()
        {
            Array.Clear(_heap, 0, _count);
            _count = 0;
            _usedBandwidthBytes = 0;
        }

        // ── 堆操作 ────────────────────────────────────────────────

        /// <summary>向上冒泡 (维护最大堆性质)。</summary>
        private void BubbleUp(int index)
        {
            while (index > 0)
            {
                int parent = (index - 1) / 2;
                if (_heap[index].Score <= _heap[parent].Score) break;

                Swap(index, parent);
                index = parent;
            }
        }

        /// <summary>向下冒泡 (维护最大堆性质)。</summary>
        private void BubbleDown(int index)
        {
            while (true)
            {
                int left = 2 * index + 1;
                int right = 2 * index + 2;
                int largest = index;

                if (left < _count && _heap[left].Score > _heap[largest].Score)
                    largest = left;
                if (right < _count && _heap[right].Score > _heap[largest].Score)
                    largest = right;

                if (largest == index) break;

                Swap(index, largest);
                index = largest;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void Swap(int a, int b)
        {
            ReplicationEntry temp = _heap[a];
            _heap[a] = _heap[b];
            _heap[b] = temp;
        }
    }

    // ════════════════════════════════════════════════════════════
    // NetworkObjectReplicator - 网络对象复制器
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// 网络对象复制器: 每帧收集脏对象, 按优先级排序,
    /// 在带宽预算内序列化并发送到相关连接。
    /// </summary>
    public class NetworkObjectReplicator
    {
        // ── 对象注册表 ────────────────────────────────────────────

        private readonly Dictionary<ulong, object> _networkObjects = new Dictionary<ulong, object>();
        private readonly Dictionary<ulong, double> _lastSyncTimes = new Dictionary<ulong, double>();
        private readonly object _registryLock = new object();

        // ── 序列化器 ──────────────────────────────────────────────

        private readonly ReplicationSerializer _serializer = new ReplicationSerializer();
        private readonly DeltaSerializer _deltaSerializer = new DeltaSerializer();
        private readonly ReplicationPriorityQueue _priorityQueue = new ReplicationPriorityQueue();

        // ── 传输 ──────────────────────────────────────────────────

        private readonly UDPTransportReal _transport;
        private readonly List<System.Net.IPEndPoint> _connections = new List<System.Net.IPEndPoint>();

        // ── 配置 ──────────────────────────────────────────────────

        private int _bandwidthBudgetBytes = 16384; // 16KB/帧
        private Vector3 _viewerPosition;

        /// <summary>对象生成工厂: (objectId, typeName) => object。</summary>
        private Func<ulong, string, object> _spawnFactory;

        // ── 统计 ──────────────────────────────────────────────────

        private long _totalBytesSent;
        private long _totalObjectsSynced;

        public long TotalBytesSent => _totalBytesSent;
        public long TotalObjectsSynced => _totalObjectsSynced;

        // ── 构造函数 ──────────────────────────────────────────────

        public NetworkObjectReplicator(UDPTransportReal transport = null)
        {
            _transport = transport;
            _priorityQueue.SetBandwidthBudget(_bandwidthBudgetBytes);
        }

        // ── 对象管理 ──────────────────────────────────────────────

        /// <summary>注册网络对象。</summary>
        public void RegisterObject(ulong objectId, object obj)
        {
            lock (_registryLock)
            {
                _networkObjects[objectId] = obj;
                _lastSyncTimes[objectId] = 0;
            }
        }

        /// <summary>注销网络对象。</summary>
        public void UnregisterObject(ulong objectId)
        {
            lock (_registryLock)
            {
                _networkObjects.Remove(objectId);
                _lastSyncTimes.Remove(objectId);
                _deltaSerializer.ClearObject(objectId);
            }
        }

        /// <summary>查找网络对象。</summary>
        public bool TryGetObject(ulong objectId, out object obj)
        {
            lock (_registryLock)
            {
                return _networkObjects.TryGetValue(objectId, out obj);
            }
        }

        /// <summary>设置对象生成工厂 (接收端用)。</summary>
        public void SetSpawnFactory(Func<ulong, string, object> factory)
        {
            _spawnFactory = factory;
        }

        /// <summary>添加需要同步的连接。</summary>
        public void AddConnection(System.Net.IPEndPoint endpoint)
        {
            lock (_connections)
            {
                if (!_connections.Contains(endpoint))
                    _connections.Add(endpoint);
            }
        }

        /// <summary>移除连接。</summary>
        public void RemoveConnection(System.Net.IPEndPoint endpoint)
        {
            lock (_connections)
            {
                _connections.Remove(endpoint);
            }
        }

        /// <summary>设置观察者位置 (用于距离优先级计算)。</summary>
        public void SetViewerPosition(Vector3 position)
        {
            _viewerPosition = position;
        }

        /// <summary>设置每帧带宽预算。</summary>
        public void SetBandwidthBudget(int bytesPerFrame)
        {
            _bandwidthBudgetBytes = bytesPerFrame;
            _priorityQueue.SetBandwidthBudget(bytesPerFrame);
        }

        // ── 每帧复制 ──────────────────────────────────────────────

        /// <summary>
        /// 每帧调用: 执行复制。
        /// 算法:
        ///   1. 收集所有需要复制的网络对象
        ///   2. 计算每个对象的优先级分数 (距离 + 重要性 + 时间)
        ///   3. 入优先级队列
        ///   4. 在带宽预算内逐个出队
        ///   5. 序列化并发送到所有相关连接
        /// </summary>
        /// <param name="dt">帧间隔 (秒)。</param>
        public void TickReplication(double dt)
        {
            _priorityQueue.Clear();
            _priorityQueue.ResetBandwidth();

            double now = GetTime();
            List<(ulong, object)> objectsToSync = new List<(ulong, object)>();

            // 1. 收集所有需要复制的对象
            lock (_registryLock)
            {
                foreach (var kvp in _networkObjects)
                {
                    objectsToSync.Add((kvp.Key, kvp.Value));
                }
            }

            // 2. 计算优先级并入队
            foreach (var (objectId, obj) in objectsToSync)
            {
                double lastSync = 0;
                lock (_registryLock)
                {
                    if (_lastSyncTimes.TryGetValue(objectId, out var t))
                        lastSync = t;
                }

                double timeSinceSync = now - lastSync;
                float priorityScore = CalculatePriorityScore(obj, timeSinceSync);
                int estimatedSize = _serializer.EstimateObjectSize(obj);

                var entry = new ReplicationEntry
                {
                    ObjectId = objectId,
                    Object = obj,
                    Score = priorityScore,
                    EstimatedSize = estimatedSize,
                    Target = null // 广播到所有连接
                };

                _priorityQueue.Enqueue(entry);
            }

            // 3. 在带宽预算内出队并发送
            List<System.Net.IPEndPoint> connectionsCopy;
            lock (_connections)
            {
                connectionsCopy = new List<System.Net.IPEndPoint>(_connections);
            }

            while (_priorityQueue.TryDequeue(out var entry))
            {
                // 序列化对象
                using (var writer = new NetworkWriterReal(8192))
                {
                    // 写入对象 ID
                    writer.WriteUInt64(entry.ObjectId);

                    // 增量序列化
                    bool hasChanges = _deltaSerializer.SerializeDelta(entry.Object, entry.ObjectId, writer);

                    if (hasChanges && writer.Length > 8) // 8 = objectId 大小
                    {
                        byte[] data = writer.ToArray();

                        // 发送到所有相关连接
                        foreach (var conn in connectionsCopy)
                        {
                            _transport?.SendReliable(conn, data);
                        }

                        _totalBytesSent += data.Length * connectionsCopy.Count;
                        _totalObjectsSynced++;
                    }
                }

                // 更新同步时间
                lock (_registryLock)
                {
                    _lastSyncTimes[entry.ObjectId] = now;
                }
            }
        }

        /// <summary>
        /// 计算对象的优先级分数。
        /// 算法: score = importance * distanceFactor * timeFactor
        ///   distanceFactor = 1 / (1 + distance) (越近越高)
        ///   timeFactor = timeSinceSync (越久未同步越高)
        /// </summary>
        private float CalculatePriorityScore(object obj, double timeSinceSync)
        {
            // 基础重要性
            float importance = 1f;

            // 获取属性元数据中的最高优先级
            var metadata = _serializer.GetMetadata(obj.GetType());
            if (metadata.Length > 0)
            {
                int maxPriority = 0;
                foreach (var m in metadata)
                    maxPriority = Math.Max(maxPriority, m.Attribute.Priority);
                importance = 1f + maxPriority * 0.5f;
            }

            // 距离因子 (如果有 Position 属性)
            float distanceFactor = 1f;
            var posProp = obj.GetType().GetProperty("Position");
            if (posProp != null && posProp.PropertyType == typeof(Vector3))
            {
                var pos = (Vector3)posProp.GetValue(obj);
                float dist = Vector3.Distance(pos, _viewerPosition);
                distanceFactor = 1f / (1f + dist * 0.01f);
            }

            // 时间因子
            float timeFactor = (float)Math.Min(timeSinceSync, 10.0);

            return importance * distanceFactor * (1f + timeFactor * 0.5f);
        }

        // ── 接收复制 ──────────────────────────────────────────────

        /// <summary>
        /// 接收复制数据。
        /// 算法:
        ///   1. 读取对象 ID
        ///   2. 查找对象 (不存在 -> 通过工厂生成)
        ///   3. 反序列化属性
        ///   4. 应用到对象
        /// </summary>
        public void ReceiveReplication(NetworkReaderReal reader, System.Net.IPEndPoint connection)
        {
            // 1. 读取对象 ID
            ulong objectId = reader.ReadUInt64();

            // 2. 查找对象
            object obj;
            bool existed;

            lock (_registryLock)
            {
                existed = _networkObjects.TryGetValue(objectId, out obj);
            }

            if (!existed)
            {
                // 对象不存在, 尝试通过工厂生成
                if (_spawnFactory == null)
                    return; // 无工厂, 丢弃

                // 读取类型名 (在增量数据之前)
                string typeName = reader.ReadString();
                obj = _spawnFactory(objectId, typeName);
                if (obj == null) return;

                lock (_registryLock)
                {
                    _networkObjects[objectId] = obj;
                    _lastSyncTimes[objectId] = 0;
                }

                // 反序列化全部属性
                _serializer.DeserializeObject(obj, reader);
            }
            else
            {
                // 对象已存在, 增量反序列化
                _deltaSerializer.DeserializeDelta(obj, reader);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static double GetTime()
        {
            return (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency;
        }
    }
}
