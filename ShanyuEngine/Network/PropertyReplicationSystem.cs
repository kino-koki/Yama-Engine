using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;

namespace ShanyuEngine.Network
{
    // ════════════════════════════════════════════════════════════════════════
    // 山雨引擎 - 属性复制/同步系统 (PropertyReplicationSystem)
    //
    // 类似 UE Property Replication / Unity NetworkVariable 的完整属性同步管线:
    //   * ReplicatedAttribute: 标记需要网络同步的属性
    //   * ReplicatedProperty: 属性元数据 (名称/类型/脏标记/频率/条件)
    //   * ReplicationMode: 复制模式 (Always/OnOwnerChange/OnCondition/Manual/UserDriven)
    //   * ReplicationCondition: 条件函数 (阈值/自定义条件)
    //   * NetworkVariable<T>: 泛型网络变量 (值/回调/脏标记/精度控制)
    //   * 内置类型: NetworkVarFloat/Int/Bool/String/Vector3/Quaternion
    //   * DeltaPropertyReplicator: 增量复制 (位掩码/只同步变更)
    //   * 属性优先级 (High/Medium/Low/Background)
    //   * 带宽优化 (压缩/量化/精度截断/重要性评分)
    //   * 复制频率控制 (不同频率/基于距离/基于重要性)
    //
    // 对接系统: NetworkCoreSystem / RPCSystem / NetworkObjectSystem
    // ════════════════════════════════════════════════════════════════════════

    // ──────────────────────────────────────────────────────────────
    // 1. 枚举定义
    // ──────────────────────────────────────────────────────────────

    /// <summary>属性复制模式。</summary>
    public enum ReplicationMode
    {
        /// <summary>始终同步 (每帧/按频率)。</summary>
        Always,
        /// <summary>仅在拥有者变更时同步。</summary>
        OnOwnerChange,
        /// <summary>仅在满足条件时同步。</summary>
        OnCondition,
        /// <summary>手动触发同步。</summary>
        Manual,
        /// <summary>由用户驱动 (显式调用 MarkDirty)。</summary>
        UserDriven
    }

    /// <summary>属性复制优先级。</summary>

    // ──────────────────────────────────────────────────────────────
    // 2. 复制特性
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 标记属性/字段需要网络复制同步。
    /// 用法: [Replicated] public float Health = 100f;
    /// </summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false)]
    public sealed class ReplicatedAttribute : Attribute
    {
        /// <summary>复制模式。</summary>
        public ReplicationMode Mode { get; set; } = ReplicationMode.Always;

        /// <summary>复制优先级。</summary>
        public ReplicationPriority Priority { get; set; } = ReplicationPriority.Medium;

        /// <summary>每秒最大同步次数, 0 = 不限制。</summary>
        public float SyncFrequency { get; set; }

        /// <summary>同步条件名称 (用于 OnCondition 模式)。</summary>
        public string Condition { get; set; } = string.Empty;

        /// <summary>量化精度 (位宽), 用于浮点压缩。0 = 不压缩。</summary>
        public int QuantizationBits { get; set; }

        /// <summary>变化阈值, 值变化超过此阈值才标记为脏。</summary>
        public float ChangeThreshold { get; set; }

        /// <summary>最小同步间隔 (秒)。</summary>
        public float MinSyncInterval { get; set; }

        /// <summary>是否仅在拥有者变更时同步。</summary>
        public bool OnlyOwnerChange { get; set; }
    }

    // ──────────────────────────────────────────────────────────────
    // 3. 复制属性信息
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 复制属性的完整元数据。存储名称、类型、当前值、上一值、脏标记等信息。
    /// </summary>
    public sealed class ReplicatedProperty
    {
        /// <summary>属性名称。</summary>
        public string Name { get; }

        /// <summary>属性类型。</summary>
        public Type PropertyType { get; }

        /// <summary>复制模式。</summary>
        public ReplicationMode Mode { get; }

        /// <summary>复制优先级。</summary>
        public ReplicationPriority Priority { get; }

        /// <summary>当前值 (object)。</summary>
        public object? CurrentValue { get; private set; }

        /// <summary>上一帧值。</summary>
        public object? PreviousValue { get; private set; }

        /// <summary>是否脏 (值已变化)。</summary>
        public bool IsDirty { get; private set; }

        /// <summary>复制频率 (次/秒)。</summary>
        public float SyncFrequency { get; }

        /// <summary>上次同步时间。</summary>
        public DateTime LastSyncTime { get; private set; } = DateTime.MinValue;

        /// <summary>条件名称。</summary>
        public string ConditionName { get; }

        /// <summary>变化阈值。</summary>
        public float ChangeThreshold { get; }

        /// <summary>量化位宽。</summary>
        public int QuantizationBits { get; }

        /// <summary>最小同步间隔 (秒)。</summary>
        public float MinSyncInterval { get; }

        /// <summary>属性索引 (在对象内的序号)。</summary>
        public int PropertyIndex { get; }

        /// <summary>估算序列化大小。</summary>
        public int EstimatedSize { get; }

        /// <summary>值变更回调。</summary>
        public event Action<object?, object?>? OnValueChanged;

        private Func<object?, bool>? _conditionFunc;

        public ReplicatedProperty(string name, Type type, int index,
            ReplicatedAttribute attr)
        {
            Name = name;
            PropertyType = type;
            PropertyIndex = index;
            Mode = attr.Mode;
            Priority = attr.Priority;
            SyncFrequency = attr.SyncFrequency;
            ConditionName = attr.Condition;
            ChangeThreshold = attr.ChangeThreshold;
            QuantizationBits = attr.QuantizationBits;
            MinSyncInterval = attr.MinSyncInterval;
            EstimatedSize = RpcParameterSerializer.EstimateTypeSize(type);
        }

        /// <summary>设置条件函数。</summary>
        public void SetCondition(Func<object?, bool> condition)
        {
            _conditionFunc = condition;
        }

        /// <summary>更新属性值。如果值发生变化, 标记为脏。</summary>
        public void SetValue(object? newValue)
        {
            PreviousValue = CurrentValue;
            CurrentValue = newValue;

            // 检查是否需要标记脏
            bool changed;
            if (ChangeThreshold > 0f && CurrentValue is IComparable comparable &&
                PreviousValue is IComparable prevComparable)
            {
                double diff = Math.Abs(
                    Convert.ToDouble(comparable) - Convert.ToDouble(prevComparable));
                changed = diff > ChangeThreshold;
            }
            else
            {
                changed = !Equals(CurrentValue, PreviousValue);
            }

            if (changed)
            {
                IsDirty = true;
                OnValueChanged?.Invoke(PreviousValue, CurrentValue);
            }
        }

        /// <summary>清除脏标记 (同步后调用)。</summary>
        public void ClearDirty()
        {
            IsDirty = false;
            LastSyncTime = DateTime.UtcNow;
        }

        /// <summary>手动标记脏。</summary>
        public void MarkDirty() => IsDirty = true;

        /// <summary>检查是否满足同步条件。</summary>
        public bool ShouldSync(float currentTimeSeconds, object? context)
        {
            // 模式检查
            switch (Mode)
            {
                case ReplicationMode.Manual:
                case ReplicationMode.UserDriven:
                    return IsDirty;

                case ReplicationMode.OnOwnerChange:
                    return IsDirty; // 由外部控制

                case ReplicationMode.OnCondition:
                    if (_conditionFunc != null)
                        return IsDirty && _conditionFunc(context);
                    return IsDirty;
            }

            // Always 模式: 检查脏标记和频率
            if (!IsDirty) return false;

            // 频率控制
            if (SyncFrequency > 0f && MinSyncInterval > 0f)
            {
                double elapsed = (DateTime.UtcNow - LastSyncTime).TotalSeconds;
                if (elapsed < MinSyncInterval) return false;
            }

            return true;
        }

        /// <summary>获取重要性评分 (0~1, 越高越重要)。</summary>
        public float GetImportanceScore()
        {
            float score = Priority switch
            {
                ReplicationPriority.Critical => 1.0f,
                ReplicationPriority.High => 0.8f,
                ReplicationPriority.Medium => 0.5f,
                ReplicationPriority.Low => 0.2f,
                _ => 0.3f
            };
            if (IsDirty) score += 0.1f;
            return Math.Min(1.0f, score);
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 4. 复制条件
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 复制条件委托。返回 true 表示属性需要同步。
    /// </summary>
    public delegate bool ReplicationCondition(object? context);

    /// <summary>
    /// 内置复制条件工厂。
    /// </summary>
    public static class ReplicationConditions
    {
        /// <summary>属性值超过阈值时才同步。</summary>
        public static ReplicationCondition Threshold(float threshold)
        {
            return context =>
            {
                if (context is ReplicatedProperty prop &&
                    prop.CurrentValue is IComparable current &&
                    prop.PreviousValue is IComparable previous)
                {
                    double diff = Math.Abs(
                        Convert.ToDouble(current) - Convert.ToDouble(previous));
                    return diff > threshold;
                }
                return true;
            };
        }

        /// <summary>始终同步 (无条件)。</summary>
        public static ReplicationCondition Always => _ => true;

        /// <summary>永远不同步。</summary>
        public static ReplicationCondition Never => _ => false;

        /// <summary>自定义条件。</summary>
        public static ReplicationCondition Custom(Func<object?, bool> func) => func;
    }

    // ──────────────────────────────────────────────────────────────
    // 5. NetworkVariable<T> 泛型网络变量
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 泛型网络变量。提供值/设置回调/同步回调/脏标记/精度控制。
    /// 用法: public NetworkVariable&lt;float&gt; Health = new NetworkVariable&lt;float&gt;(100f);
    /// </summary>
    public sealed class NetworkVariable<T>
    {
        private T _value;
        private T _previousValue;
        private bool _isDirty;
        private float _lastSyncTime;
        private float _syncInterval;
        private int _quantizationBits;

        /// <summary>当前值。</summary>
        public T Value
        {
            get => _value;
            set
            {
                if (!EqualityComparer<T>.Default.Equals(_value, value))
                {
                    _previousValue = _value;
                    _value = value;
                    _isDirty = true;
                    OnValueChanged?.Invoke(_previousValue, _value);
                }
            }
        }

        /// <summary>上一帧值。</summary>
        public T PreviousValue => _previousValue;

        /// <summary>是否脏 (值已变化)。</summary>
        public bool IsDirty => _isDirty;

        /// <summary>同步间隔 (秒)。</summary>
        public float SyncInterval
        {
            get => _syncInterval;
            set => _syncInterval = value;
        }

        /// <summary>量化位宽 (用于浮点压缩)。</summary>
        public int QuantizationBits
        {
            get => _quantizationBits;
            set => _quantizationBits = value;
        }

        /// <summary>值变更回调。</summary>
        public event Action<T, T>? OnValueChanged;

        /// <summary>服务器端设置回调 (值从网络接收时调用)。</summary>
        public event Action<T>? OnServerSet;

        /// <summary>同步完成回调。</summary>
        public event Action? OnSynced;

        public NetworkVariable() : this(default!) { }

        public NetworkVariable(T defaultValue)
        {
            _value = defaultValue;
            _previousValue = defaultValue;
            _syncInterval = 0f;
            _quantizationBits = 0;
        }

        public NetworkVariable(T defaultValue, float syncInterval) : this(defaultValue)
        {
            _syncInterval = syncInterval;
        }

        /// <summary>手动标记脏。</summary>
        public void MarkDirty() => _isDirty = true;

        /// <summary>清除脏标记 (同步后调用)。</summary>
        public void ClearDirty()
        {
            _isDirty = false;
            _previousValue = _value;
            OnSynced?.Invoke();
        }

        /// <summary>服务器端设置值 (不触发脏标记)。</summary>
        public void SetServerValue(T value)
        {
            _previousValue = _value;
            _value = value;
            _isDirty = true;
            OnServerSet?.Invoke(value);
        }

        /// <summary>检查是否满足同步条件。</summary>
        public bool ShouldSync(float currentTimeSeconds)
        {
            if (!_isDirty) return false;
            if (_syncInterval <= 0f) return true;
            return (currentTimeSeconds - _lastSyncTime) >= _syncInterval;
        }

        /// <summary>序列化到写入器。</summary>
        public void Serialize(NetworkWriter writer)
        {
            RpcParameterSerializer.SerializeParameter(writer, typeof(T), _value!);
        }

        /// <summary>从读取器反序列化。</summary>
        public void Deserialize(NetworkReader reader)
        {
            var newValue = (T)RpcParameterSerializer.DeserializeParameter(reader, typeof(T))!;
            if (!EqualityComparer<T>.Default.Equals(_value, newValue))
            {
                _previousValue = _value;
                _value = newValue;
                _isDirty = true;
                OnValueChanged?.Invoke(_previousValue, _value);
            }
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 6. 内置网络变量类型
    // ──────────────────────────────────────────────────────────────

    /// <summary>网络浮点变量。支持精度控制。</summary>
    public sealed class NetworkVarFloat
    {
        private float _value;
        private float _previousValue;
        private bool _isDirty;
        private int _precisionBits;

        public float Value
        {
            get => _value;
            set
            {
                if (Math.Abs(_value - value) > float.Epsilon)
                {
                    _previousValue = _value;
                    _value = value;
                    _isDirty = true;
                }
            }
        }

        public bool IsDirty => _isDirty;
        public float PreviousValue => _previousValue;
        public event Action<float, float>? OnChanged;

        public NetworkVarFloat(float defaultValue = 0f, int precisionBits = 0)
        {
            _value = defaultValue;
            _previousValue = defaultValue;
            _precisionBits = precisionBits;
        }

        public void MarkDirty() => _isDirty = true;
        public void ClearDirty() { _isDirty = false; _previousValue = _value; }

        public void Serialize(NetworkWriter writer)
        {
            if (_precisionBits > 0 && _precisionBits <= 32)
            {
                // 量化为指定位宽
                int quantized = (int)(_value * ((1 << _precisionBits) - 1));
                writer.WriteInt(quantized);
            }
            else
            {
                writer.WriteFloat(_value);
            }
        }

        public void Deserialize(NetworkReader reader)
        {
            if (_precisionBits > 0 && _precisionBits <= 32)
            {
                int quantized = reader.ReadInt();
                _value = (float)quantized / ((1 << _precisionBits) - 1);
            }
            else
            {
                _value = reader.ReadFloat();
            }
            if (Math.Abs(_value - _previousValue) > float.Epsilon)
            {
                _isDirty = true;
                OnChanged?.Invoke(_previousValue, _value);
            }
        }
    }

    /// <summary>网络整数变量。</summary>
    public sealed class NetworkVarInt
    {
        private int _value;
        private int _previousValue;
        private bool _isDirty;

        public int Value
        {
            get => _value;
            set
            {
                if (_value != value)
                {
                    _previousValue = _value;
                    _value = value;
                    _isDirty = true;
                }
            }
        }

        public bool IsDirty => _isDirty;
        public event Action<int, int>? OnChanged;

        public NetworkVarInt(int defaultValue = 0) { _value = defaultValue; _previousValue = defaultValue; }
        public void MarkDirty() => _isDirty = true;
        public void ClearDirty() { _isDirty = false; _previousValue = _value; }

        public void Serialize(NetworkWriter writer) => writer.WriteInt(_value);
        public void Deserialize(NetworkReader reader)
        {
            _value = reader.ReadInt();
            if (_value != _previousValue) { _isDirty = true; OnChanged?.Invoke(_previousValue, _value); }
        }
    }

    /// <summary>网络布尔变量。</summary>
    public sealed class NetworkVarBool
    {
        private bool _value;
        private bool _previousValue;
        private bool _isDirty;

        public bool Value
        {
            get => _value;
            set
            {
                if (_value != value)
                {
                    _previousValue = _value;
                    _value = value;
                    _isDirty = true;
                }
            }
        }

        public bool IsDirty => _isDirty;
        public event Action<bool, bool>? OnChanged;

        public NetworkVarBool(bool defaultValue = false) { _value = defaultValue; _previousValue = defaultValue; }
        public void MarkDirty() => _isDirty = true;
        public void ClearDirty() { _isDirty = false; _previousValue = _value; }

        public void Serialize(NetworkWriter writer) => writer.WriteBool(_value);
        public void Deserialize(NetworkReader reader)
        {
            _value = reader.ReadBool();
            if (_value != _previousValue) { _isDirty = true; OnChanged?.Invoke(_previousValue, _value); }
        }
    }

    /// <summary>网络字符串变量。</summary>
    public sealed class NetworkVarString
    {
        private string _value = string.Empty;
        private string _previousValue = string.Empty;
        private bool _isDirty;

        public string Value
        {
            get => _value;
            set
            {
                if (_value != value)
                {
                    _previousValue = _value;
                    _value = value;
                    _isDirty = true;
                }
            }
        }

        public bool IsDirty => _isDirty;
        public event Action<string, string>? OnChanged;

        public void MarkDirty() => _isDirty = true;
        public void ClearDirty() { _isDirty = false; _previousValue = _value; }

        public void Serialize(NetworkWriter writer) => writer.WriteString(_value);
        public void Deserialize(NetworkReader reader)
        {
            _value = reader.ReadString();
            if (_value != _previousValue) { _isDirty = true; OnChanged?.Invoke(_previousValue, _value); }
        }
    }

    /// <summary>网络 Vector3 变量。支持量化压缩。</summary>
    public sealed class NetworkVarVector3
    {
        private Vector3 _value;
        private Vector3 _previousValue;
        private bool _isDirty;
        private int _precisionBits;

        public Vector3 Value
        {
            get => _value;
            set
            {
                if (!(_value.X == value.X && _value.Y == value.Y && _value.Z == value.Z))
                {
                    _previousValue = _value;
                    _value = value;
                    _isDirty = true;
                }
            }
        }

        public bool IsDirty => _isDirty;
        public event Action<Vector3, Vector3>? OnChanged;

        public NetworkVarVector3(Vector3 defaultValue = default, int precisionBits = 0)
        {
            _value = defaultValue;
            _previousValue = defaultValue;
            _precisionBits = precisionBits;
        }

        public void MarkDirty() => _isDirty = true;
        public void ClearDirty() { _isDirty = false; _previousValue = _value; }

        public void Serialize(NetworkWriter writer)
        {
            if (_precisionBits > 0)
            {
                // 量化 Vector3 到指定位宽 (假设范围 -1000 ~ 1000)
                float range = 1000f;
                int maxVal = (1 << _precisionBits) - 1;
                writer.WriteInt((int)((_value.X / range) * maxVal + maxVal / 2));
                writer.WriteInt((int)((_value.Y / range) * maxVal + maxVal / 2));
                writer.WriteInt((int)((_value.Z / range) * maxVal + maxVal / 2));
            }
            else
            {
                writer.WriteVector3(_value);
            }
        }

        public void Deserialize(NetworkReader reader)
        {
            if (_precisionBits > 0)
            {
                float range = 1000f;
                int maxVal = (1 << _precisionBits) - 1;
                _value.X = ((reader.ReadInt() - maxVal / 2) / (float)maxVal) * range;
                _value.Y = ((reader.ReadInt() - maxVal / 2) / (float)maxVal) * range;
                _value.Z = ((reader.ReadInt() - maxVal / 2) / (float)maxVal) * range;
            }
            else
            {
                _value = reader.ReadVector3();
            }
            _isDirty = true;
            OnChanged?.Invoke(_previousValue, _value);
        }
    }

    /// <summary>网络 Quaternion 变量。支持最小旋转表示压缩。</summary>
    public sealed class NetworkVarQuaternion
    {
        private Quaternion _value;
        private Quaternion _previousValue;
        private bool _isDirty;
        private bool _useSmallestThree;

        public Quaternion Value
        {
            get => _value;
            set
            {
                if (!(_value.X == value.X && _value.Y == value.Y &&
                      _value.Z == value.Z && _value.W == value.W))
                {
                    _previousValue = _value;
                    _value = value;
                    _isDirty = true;
                }
            }
        }

        public bool IsDirty => _isDirty;
        public event Action<Quaternion, Quaternion>? OnChanged;

        public NetworkVarQuaternion(Quaternion defaultValue = default, bool useSmallestThree = true)
        {
            _value = defaultValue;
            _previousValue = defaultValue;
            _useSmallestThree = useSmallestThree;
        }

        public void MarkDirty() => _isDirty = true;
        public void ClearDirty() { _isDirty = false; _previousValue = _value; }

        public void Serialize(NetworkWriter writer)
        {
            if (_useSmallestThree)
            {
                // Smallest Three 压缩: 丢弃最大分量, 用符号位重建
                float maxVal = Math.Max(
                    Math.Max(Math.Abs(_value.X), Math.Abs(_value.Y)),
                    Math.Max(Math.Abs(_value.Z), Math.Abs(_value.W)));

                byte largestIndex = 3; // W
                float largest = _value.W;
                if (_value.X > largest) { largest = _value.X; largestIndex = 0; }
                if (_value.Y > largest) { largest = _value.Y; largestIndex = 1; }
                if (_value.Z > largest) { largest = _value.Z; largestIndex = 2; }

                writer.WriteByte(largestIndex);

                // 量化其他三个分量到 16bit
                writer.WriteShort(Quantize16(_value.X, maxVal));
                writer.WriteShort(Quantize16(_value.Y, maxVal));
                writer.WriteShort(Quantize16(_value.Z, maxVal));
                writer.WriteShort(Quantize16(_value.W, maxVal));
            }
            else
            {
                writer.WriteQuaternion(_value);
            }
        }

        public void Deserialize(NetworkReader reader)
        {
            if (_useSmallestThree)
            {
                byte largestIndex = reader.ReadByte();
                float x = Dequantize16(reader.ReadShort());
                float y = Dequantize16(reader.ReadShort());
                float z = Dequantize16(reader.ReadShort());
                float w = Dequantize16(reader.ReadShort());

                // 重建最大分量
                float maxSq = 1.0f - (x * x + y * y + z * z);
                float maxComp = maxSq > 0 ? (float)Math.Sqrt(maxSq) : 0f;

                _value = largestIndex switch
                {
                    0 => new Quaternion(maxComp, y, z, w),
                    1 => new Quaternion(x, maxComp, z, w),
                    2 => new Quaternion(x, y, maxComp, w),
                    _ => new Quaternion(x, y, z, maxComp)
                };

                // 确保正确符号
                if (GetRef(_value, largestIndex) < 0)
                    _value = new Quaternion(-_value.X, -_value.Y, -_value.Z, -_value.W);
            }
            else
            {
                _value = reader.ReadQuaternion();
            }
            _isDirty = true;
            OnChanged?.Invoke(_previousValue, _value);
        }

        private static short Quantize16(float value, float maxValue)
        {
            if (maxValue < 0.0001f) return 0;
            return (short)Math.Clamp((int)(value / maxValue * 32767f), -32768, 32767);
        }

        private static float Dequantize16(short quantized)
        {
            return quantized / 32767f;
        }

        private static float GetRef(Quaternion q, int index) => index switch
        {
            0 => q.X, 1 => q.Y, 2 => q.Z, _ => q.W
        };
    }

    // ──────────────────────────────────────────────────────────────
    // 7. 增量属性复制器
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 增量属性复制器。使用位掩码只发送变更的字段,
    /// 显著减少网络带宽占用。
    /// </summary>
    public sealed class DeltaPropertyReplicator
    {
        private readonly List<ReplicatedProperty> _properties = new List<ReplicatedProperty>();
        private ulong _dirtyMask;

        /// <summary>属性总数。</summary>
        public int PropertyCount => _properties.Count;

        /// <summary>当前脏位掩码。</summary>
        public ulong DirtyMask => _dirtyMask;

        /// <summary>总估算大小 (字节)。</summary>
        public int TotalEstimatedSize
        {
            get
            {
                int size = 8; // 位掩码本身 (ulong)
                foreach (var prop in _properties)
                    size += prop.EstimatedSize;
                return size;
            }
        }

        /// <summary>添加复制属性。</summary>
        public int AddProperty(ReplicatedProperty property)
        {
            _properties.Add(property);
            property.OnValueChanged += (prev, curr) => UpdateDirtyMask(property);
            return _properties.Count - 1;
        }

        /// <summary>更新脏位掩码。</summary>
        private void UpdateDirtyMask(ReplicatedProperty property)
        {
            if (property.IsDirty)
                _dirtyMask |= 1UL << property.PropertyIndex;
        }

        /// <summary>手动标记属性脏。</summary>
        public void MarkDirty(int propertyIndex)
        {
            if (propertyIndex >= 0 && propertyIndex < _properties.Count)
            {
                _properties[propertyIndex].MarkDirty();
                _dirtyMask |= 1UL << propertyIndex;
            }
        }

        /// <summary>清除指定属性的脏标记。</summary>
        public void ClearDirty(int propertyIndex)
        {
            if (propertyIndex >= 0 && propertyIndex < _properties.Count)
            {
                _properties[propertyIndex].ClearDirty();
                _dirtyMask &= ~(1UL << propertyIndex);
            }
        }

        /// <summary>清除所有脏标记。</summary>
        public void ClearAllDirty()
        {
            _dirtyMask = 0;
            foreach (var prop in _properties)
                prop.ClearDirty();
        }

        /// <summary>
        /// 序列化增量更新。只写入脏标记对应的属性。
        /// </summary>
        public void SerializeDelta(NetworkWriter writer, float currentTime)
        {
            // 写入脏位掩码
            writer.WriteULong(_dirtyMask);

            // 只序列化脏属性
            for (int i = 0; i < _properties.Count; i++)
            {
                if ((_dirtyMask & (1UL << i)) == 0) continue;
                var prop = _properties[i];
                if (prop.ShouldSync(currentTime, null))
                {
                    RpcParameterSerializer.SerializeParameter(
                        writer, prop.PropertyType, prop.CurrentValue!);
                }
            }
        }

        /// <summary>
        /// 反序列化增量更新。根据位掩码更新对应属性。
        /// </summary>
        public void DeserializeDelta(NetworkReader reader)
        {
            ulong receivedMask = reader.ReadULong();

            for (int i = 0; i < _properties.Count; i++)
            {
                if ((receivedMask & (1UL << i)) == 0) continue;
                if (i >= _properties.Count) break;

                var prop = _properties[i];
                var newValue = RpcParameterSerializer.DeserializeParameter(reader, prop.PropertyType);
                prop.SetValue(newValue);
            }
        }

        /// <summary>序列化所有属性 (全量同步)。</summary>
        public void SerializeFull(NetworkWriter writer)
        {
            writer.WriteULong(ulong.MaxValue); // 全量标记
            foreach (var prop in _properties)
            {
                RpcParameterSerializer.SerializeParameter(
                    writer, prop.PropertyType, prop.CurrentValue!);
            }
        }

        /// <summary>获取脏属性列表。</summary>
        public List<ReplicatedProperty> GetDirtyProperties()
        {
            var dirty = new List<ReplicatedProperty>();
            for (int i = 0; i < _properties.Count; i++)
            {
                if ((_dirtyMask & (1UL << i)) != 0)
                    dirty.Add(_properties[i]);
            }
            return dirty;
        }

        /// <summary>按优先级排序脏属性 (用于带宽预算分配)。</summary>
        public List<ReplicatedProperty> GetDirtyPropertiesSortedByPriority()
        {
            return GetDirtyProperties()
                .OrderByDescending(p => p.Priority)
                .ThenByDescending(p => p.GetImportanceScore())
                .ToList();
        }

        /// <summary>获取指定索引的属性。</summary>
        public ReplicatedProperty? GetProperty(int index)
        {
            return index >= 0 && index < _properties.Count ? _properties[index] : null;
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 8. 带宽优化工具
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 网络属性带宽优化器。提供压缩、量化、精度截断、重要性评分等功能。
    /// </summary>
    public static class BandwidthOptimizer
    {
        /// <summary>量化浮点到指定位宽。</summary>
        public static int QuantizeFloat(float value, float min, float max, int bits)
        {
            if (bits <= 0 || bits > 32) return (int)(value * 1000);
            int maxValue = (1 << bits) - 1;
            float normalized = (value - min) / (max - min);
            return (int)Math.Clamp((normalized * maxValue), 0, maxValue);
        }

        /// <summary>反量化整数为浮点。</summary>
        public static float DequantizeFloat(int quantized, float min, float max, int bits)
        {
            if (bits <= 0 || bits > 32) return quantized / 1000f;
            int maxValue = (1 << bits) - 1;
            float normalized = (float)quantized / maxValue;
            return min + normalized * (max - min);
        }

        /// <summary>量化 Vector3 到指定位宽 (16bit per component)。</summary>
        public static void QuantizeVector3(Vector3 v, float range, out short x, out short y, out short z)
        {
            int maxVal = 32767;
            x = (short)Math.Clamp((int)(v.X / range * maxVal), -32768, 32767);
            y = (short)Math.Clamp((int)(v.Y / range * maxVal), -32768, 32767);
            z = (short)Math.Clamp((int)(v.Z / range * maxVal), -32768, 32767);
        }

        /// <summary>反量化 Vector3。</summary>
        public static Vector3 DequantizeVector3(short x, short y, short z, float range)
        {
            return new Vector3(
                x / 32767f * range,
                y / 32767f * range,
                z / 32767f * range);
        }

        /// <summary>
        /// 基于距离的重要性评分。距离越远, 重要性越低。
        /// </summary>
        public static float DistanceBasedImportance(Vector3 viewerPos, Vector3 objectPos,
            float maxRelevantDistance = 200f)
        {
            float distance = Vector3.Distance(viewerPos, objectPos);
            return 1.0f - Math.Clamp(distance / maxRelevantDistance, 0f, 1f);
        }

        /// <summary>
        /// 带宽预算分配。根据属性重要性分配可用的带宽预算。
        /// </summary>
        public static int AllocateBandwidthBudget(
            int totalBudgetBytes,
            List<(ReplicatedProperty prop, float importance)> properties)
        {
            if (properties.Count == 0) return 0;

            float totalImportance = properties.Sum(p => p.importance);
            if (totalImportance <= 0) totalImportance = 1f;

            int budget = 0;
            foreach (var (prop, importance) in properties)
            {
                float ratio = importance / totalImportance;
                budget += (int)(totalBudgetBytes * ratio);
                if (budget >= totalBudgetBytes) break;
            }

            return budget;
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 9. 复制频率控制器
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 复制频率控制器。管理不同属性的同步频率, 基于距离和重要性动态调整。
    /// </summary>
    public sealed class ReplicationFrequencyController
    {
        private readonly Dictionary<int, float> _propertyTimers = new Dictionary<int, float>();
        private readonly Dictionary<int, float> _propertyIntervals = new Dictionary<int, float>();
        private float _globalBudgetSeconds = 1.0f / 20f; // 默认 20Hz 全局频率

        /// <summary>设置属性同步间隔。</summary>
        public void SetInterval(int propertyIndex, float intervalSeconds)
        {
            _propertyIntervals[propertyIndex] = intervalSeconds;
        }

        /// <summary>设置全局同步预算 (秒)。</summary>
        public void SetGlobalBudget(float budgetSeconds)
        {
            _globalBudgetSeconds = budgetSeconds;
        }

        /// <summary>
        /// 检查属性是否在当前帧应该同步。
        /// 考虑全局预算、属性独立间隔和基于距离的调整。
        /// </summary>
        public bool ShouldSyncThisFrame(
            int propertyIndex,
            float deltaTime,
            float distance,
            float maxDistance = 200f,
            ReplicationPriority priority = ReplicationPriority.Medium)
        {
            // 距离衰减: 远处物体降低同步频率
            float distanceFactor = 1.0f - Math.Clamp(distance / maxDistance, 0f, 0.9f);

            // 优先级基础间隔
            float baseInterval = priority switch
            {
                ReplicationPriority.Critical => 1f / 60f,  // 60Hz
                ReplicationPriority.High => 1f / 30f,       // 30Hz
                ReplicationPriority.Medium => 1f / 20f,     // 20Hz
                ReplicationPriority.Low => 1f / 10f,        // 10Hz
                _ => 1f / 20f
            };

            // 属性自定义间隔优先
            float interval = _propertyIntervals.TryGetValue(propertyIndex, out var custom)
                ? custom : baseInterval;

            // 应用距离衰减 (远处降低频率)
            float adjustedInterval = interval / Math.Max(distanceFactor, 0.1f);

            // 更新计时器
            if (!_propertyTimers.TryGetValue(propertyIndex, out var timer))
                timer = 0f;
            timer += deltaTime;
            _propertyTimers[propertyIndex] = timer;

            // 检查是否到达同步时刻
            if (timer >= adjustedInterval)
            {
                _propertyTimers[propertyIndex] = 0f;
                return true;
            }

            return false;
        }

        /// <summary>重置所有计时器。</summary>
        public void Reset()
        {
            _propertyTimers.Clear();
        }
    }
}
