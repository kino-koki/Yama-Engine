// ============================================================================
// NetworkReplicationSystem.cs
// yama游戏引擎 - 网络复制系统
// 负责属性的自动同步和复制
// ============================================================================
// 属性同步这块代码写起来挺繁琐的 但这是网络游戏的核心
// 服务端的属性变化要自动同步到客户端 不能让开发者手动处理
// ============================================================================

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;

namespace YamaEngine.Network
{
    /// <summary>
    /// 复制模式
    /// </summary>

    /// <summary>
    /// 复制优先级
    /// </summary>
    public enum ReplicationPriority
    {
        /// <summary>低 - 很久才同步一次</summary>
        Low,
        /// <summary>中 - 正常同步频率</summary>
        Medium,
        /// <summary>高 - 频繁同步</summary>
        High,
        /// <summary>最高 - 每帧都同步</summary>
        Highest,
        /// <summary>关键 - 立即同步, 不排队</summary>
        Critical
    }

    /// <summary>
    /// 复制条件
    /// </summary>

    /// <summary>
    /// 网络对象属性标记
    /// 标记需要同步的属性
    /// </summary>
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
    public class NetworkPropertyAttribute : Attribute
    {
        /// <summary>复制模式</summary>
        public ReplicationMode Mode { get; set; } = ReplicationMode.ServerToClient;

        /// <summary>优先级</summary>
        public ReplicationPriority Priority { get; set; } = ReplicationPriority.Medium;

        /// <summary>同步条件</summary>
        public ReplicationCondition Condition { get; set; } = ReplicationCondition.OnChange;

        /// <summary>变化阈值(浮点数比较用)</summary>
        public float Threshold { get; set; } = 0.001f;

        /// <summary>是否在通道0(可靠)</summary>
        public bool Reliable { get; set; } = false;

        /// <summary>通道ID</summary>
        public int ChannelId { get; set; } = 0;
    }

    /// <summary>
    /// 网络对象标记
    /// 标记一个类可以被网络复制
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
    public class NetworkObjectAttribute : Attribute
    {
        /// <summary>对象类型ID(必须唯一)</summary>
        public ushort TypeId { get; set; }

        /// <summary>是否在玩家附近才同步</summary>
        public bool RelevanceByDistance { get; set; } = false;

        /// <summary>相关性距离</summary>
        public float RelevanceDistance { get; set; } = 1000f;

        /// <summary>是否总是相关</summary>
        public bool AlwaysRelevant { get; set; } = false;
    }

    /// <summary>
    /// 网络对象接口
    /// 所有需要网络同步的对象都要实现这个接口
    /// </summary>
    public interface INetworkObject
    {
        /// <summary>对象唯一ID</summary>
        uint NetworkId { get; set; }

        /// <summary>对象类型ID</summary>
        ushort TypeId { get; }

        /// <summary>是否在服务端</summary>
        bool IsServer { get; set; }

        /// <summary>所有者连接ID(客户端对象)</summary>
        int OwnerConnectionId { get; set; }

        /// <summary>是否已经初始化</summary>
        bool IsInitialized { get; set; }

        /// <summary>对象被创建时调用</summary>
        void OnNetworkSpawn();

        /// <summary>对象被销毁时调用</summary>
        void OnNetworkDespawn();

        /// <summary>序列化</summary>
        void NetworkSerialize(NetworkWriter writer);

        /// <summary>反序列化</summary>
        void NetworkDeserialize(NetworkReader reader);
    }

    /// <summary>
    /// 网络对象基类
    /// 提供默认实现
    /// </summary>
    public abstract class NetworkObjectBase : INetworkObject
    {
        public uint NetworkId { get; set; }
        public abstract ushort TypeId { get; }
        public bool IsServer { get; set; }
        public int OwnerConnectionId { get; set; } = -1;
        public bool IsInitialized { get; set; }

        public virtual void OnNetworkSpawn() { }
        public virtual void OnNetworkDespawn() { }

        public virtual void NetworkSerialize(NetworkWriter writer) { }
        public virtual void NetworkDeserialize(NetworkReader reader) { }
    }

    /// <summary>
    /// 属性元数据
    /// 描述一个需要同步的属性
    /// </summary>
    public class PropertyMetadata
    {
        /// <summary>属性信息</summary>
        public PropertyInfo PropertyInfo { get; set; }

        /// <summary>网络属性配置</summary>
        public NetworkPropertyAttribute Attribute { get; set; }

        /// <summary>上次同步的值</summary>
        public object LastSyncedValue { get; set; }

        /// <summary>上次同步的时间</summary>
        public long LastSyncTime { get; set; }

        /// <summary>是否是初始同步</summary>
        public bool IsInitialSync { get; set; } = true;

        /// <summary>属性索引</summary>
        public int PropertyIndex { get; set; }
    }

    /// <summary>
    /// 网络对象元数据
    /// 描述一个网络对象类型
    /// </summary>
    public class NetworkObjectMetadata
    {
        /// <summary>类型</summary>
        public Type Type { get; set; }

        /// <summary>类型ID</summary>
        public ushort TypeId { get; set; }

        /// <summary>网络对象属性</summary>
        public NetworkObjectAttribute Attribute { get; set; }

        /// <summary>需要同步的属性列表</summary>
        public List<PropertyMetadata> Properties { get; set; } = new List<PropertyMetadata>();

        /// <summary>工厂函数</summary>
        public Func<INetworkObject> Factory { get; set; }

        /// <summary>是否按距离相关性</summary>
        public bool RelevanceByDistance => Attribute?.RelevanceByDistance ?? false;

        /// <summary>相关性距离</summary>
        public float RelevanceDistance => Attribute?.RelevanceDistance ?? 1000f;
    }

    /// <summary>
    /// 网络对象实例信息
    /// </summary>
    public class NetworkObjectInstance
    {
        /// <summary>对象</summary>
        public INetworkObject Object { get; set; }

        /// <summary>元数据</summary>
        public NetworkObjectMetadata Metadata { get; set; }

        /// <summary>属性值缓存</summary>
        public Dictionary<int, object> PropertyValueCache { get; set; } = new Dictionary<int, object>();

        /// <summary>上次全量同步时间</summary>
        public long LastFullSyncTime { get; set; }

        /// <summary>相关连接集合</summary>
        public HashSet<int> RelevantConnections { get; set; } = new HashSet<int>();

        /// <summary>对象位置(用于相关性判断)</summary>
        public System.Numerics.Vector3 Position { get; set; }

        /// <summary>是否被销毁</summary>
        public bool IsDestroyed { get; set; }

        /// <summary>创建时间</summary>
        public DateTime CreateTime { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// 网络写入器
    /// </summary>
    public class NetworkWriter
    {
        private byte[] _buffer;
        private int _position;
        private int _length;

        public NetworkWriter(int initialCapacity = 1024)
        {
            _buffer = new byte[initialCapacity];
            _position = 0;
            _length = 0;
        }

        public int Position => _position;
        public int Length => _length;
        public byte[] Buffer => _buffer;

        // ── 兼容别名（高层调用方使用的短命名）──
        public byte[] Data => _buffer;
        public void WriteInt(int value) => WriteInt32(value);
        public void WriteUInt(uint value) => WriteUInt32(value);
        public void WriteShort(short value) => WriteInt16(value);
        public void WriteUShort(ushort value) => WriteUInt16(value);
        public void WriteFloat(float value) => WriteSingle(value);
        public void WriteLong(long value) { EnsureCapacity(8); BitConverter.TryWriteBytes(new Span<byte>(_buffer, _position, 8), value); _position += 8; _length = Math.Max(_length, _position); }
        public void WriteULong(ulong value) { EnsureCapacity(8); BitConverter.TryWriteBytes(new Span<byte>(_buffer, _position, 8), value); _position += 8; _length = Math.Max(_length, _position); }

        public void EnsureCapacity(int needed)
        {
            if (_position + needed > _buffer.Length)
            {
                int newCapacity = _buffer.Length * 2;
                while (newCapacity < _position + needed)
                {
                    newCapacity *= 2;
                }
                Array.Resize(ref _buffer, newCapacity);
            }
        }

        public void WriteByte(byte value)
        {
            EnsureCapacity(1);
            _buffer[_position++] = value;
            _length = Math.Max(_length, _position);
        }

        public void WriteBytes(byte[] data, int offset, int count)
        {
            EnsureCapacity(count);
            System.Buffer.BlockCopy(data, offset, _buffer, _position, count);
            _position += count;
            _length = Math.Max(_length, _position);
        }

        public void WriteInt32(int value)
        {
            EnsureCapacity(4);
            BitConverter.TryWriteBytes(new Span<byte>(_buffer, _position, 4), value);
            _position += 4;
            _length = Math.Max(_length, _position);
        }

        public void WriteUInt32(uint value)
        {
            EnsureCapacity(4);
            BitConverter.TryWriteBytes(new Span<byte>(_buffer, _position, 4), value);
            _position += 4;
            _length = Math.Max(_length, _position);
        }

        public void WriteInt16(short value)
        {
            EnsureCapacity(2);
            BitConverter.TryWriteBytes(new Span<byte>(_buffer, _position, 2), value);
            _position += 2;
            _length = Math.Max(_length, _position);
        }

        public void WriteUInt16(ushort value)
        {
            EnsureCapacity(2);
            BitConverter.TryWriteBytes(new Span<byte>(_buffer, _position, 2), value);
            _position += 2;
            _length = Math.Max(_length, _position);
        }

        public void WriteSingle(float value)
        {
            EnsureCapacity(4);
            BitConverter.TryWriteBytes(new Span<byte>(_buffer, _position, 4), value);
            _position += 4;
            _length = Math.Max(_length, _position);
        }

        public void WriteDouble(double value)
        {
            EnsureCapacity(8);
            BitConverter.TryWriteBytes(new Span<byte>(_buffer, _position, 8), value);
            _position += 8;
            _length = Math.Max(_length, _position);
        }

        public void WriteBool(bool value)
        {
            WriteByte((byte)(value ? 1 : 0));
        }

        public void WriteString(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                WriteUInt16(0);
                return;
            }

            var bytes = System.Text.Encoding.UTF8.GetBytes(value);
            WriteUInt16((ushort)bytes.Length);
            WriteBytes(bytes, 0, bytes.Length);
        }

        /// <summary>写入整个字节数组 (从 0 开始)。</summary>
        public void WriteBytes(byte[] data)
            => WriteBytes(data, 0, data.Length);

        /// <summary>写入有符号字节。</summary>
        public void WriteSByte(sbyte value)
            => WriteByte((byte)value);

        /// <summary>写入四维向量。</summary>
        public void WriteVector4(System.Numerics.Vector4 value)
        {
            WriteSingle(value.X);
            WriteSingle(value.Y);
            WriteSingle(value.Z);
            WriteSingle(value.W);
        }

        /// <summary>写入变换 (位置 + 旋转 + 缩放)。</summary>
        public void WriteTransform(NumericTransform value)
        {
            WriteVector3(value.Position);
            WriteQuaternion(value.Rotation);
            WriteVector3(value.Scale);
        }

        public void WriteVector3(System.Numerics.Vector3 value)
        {
            WriteSingle(value.X);
            WriteSingle(value.Y);
            WriteSingle(value.Z);
        }

        public void WriteVector2(System.Numerics.Vector2 value)
        {
            WriteSingle(value.X);
            WriteSingle(value.Y);
        }

        public void WriteQuaternion(System.Numerics.Quaternion value)
        {
            WriteSingle(value.X);
            WriteSingle(value.Y);
            WriteSingle(value.Z);
            WriteSingle(value.W);
        }

        public void WriteByteArray(byte[] data)
        {
            if (data == null)
            {
                WriteInt32(0);
                return;
            }
            WriteInt32(data.Length);
            WriteBytes(data, 0, data.Length);
        }

        public byte[] ToArray()
        {
            var result = new byte[_length];
            System.Buffer.BlockCopy(_buffer, 0, result, 0, _length);
            return result;
        }

        public void Reset()
        {
            _position = 0;
            _length = 0;
        }
    }

    /// <summary>
    /// 网络读取器
    /// </summary>
    public class NetworkReader
    {
        private byte[] _buffer;
        private int _position;
        private int _length;

        public NetworkReader(byte[] data, int offset = 0, int length = -1)
        {
            _buffer = data;
            _position = offset;
            _length = length >= 0 ? offset + length : data.Length;
        }

        public int Position => _position;
        public int Remaining => _length - _position;

        public byte ReadByte()
        {
            if (_position >= _length) throw new InvalidOperationException("读取超出缓冲区范围");
            return _buffer[_position++];
        }

        public byte[] ReadBytes(int count)
        {
            if (_position + count > _length) throw new InvalidOperationException("读取超出缓冲区范围");
            var result = new byte[count];
            System.Buffer.BlockCopy(_buffer, _position, result, 0, count);
            _position += count;
            return result;
        }

        public int ReadInt32()
        {
            if (_position + 4 > _length) throw new InvalidOperationException("读取超出缓冲区范围");
            int value = BitConverter.ToInt32(_buffer, _position);
            _position += 4;
            return value;
        }

        public uint ReadUInt32()
        {
            if (_position + 4 > _length) throw new InvalidOperationException("读取超出缓冲区范围");
            uint value = BitConverter.ToUInt32(_buffer, _position);
            _position += 4;
            return value;
        }

        public short ReadInt16()
        {
            if (_position + 2 > _length) throw new InvalidOperationException("读取超出缓冲区范围");
            short value = BitConverter.ToInt16(_buffer, _position);
            _position += 2;
            return value;
        }

        public ushort ReadUInt16()
        {
            if (_position + 2 > _length) throw new InvalidOperationException("读取超出缓冲区范围");
            ushort value = BitConverter.ToUInt16(_buffer, _position);
            _position += 2;
            return value;
        }

        public float ReadSingle()
        {
            if (_position + 4 > _length) throw new InvalidOperationException("读取超出缓冲区范围");
            float value = BitConverter.ToSingle(_buffer, _position);
            _position += 4;
            return value;
        }

        public double ReadDouble()
        {
            if (_position + 8 > _length) throw new InvalidOperationException("读取超出缓冲区范围");
            double value = BitConverter.ToDouble(_buffer, _position);
            _position += 8;
            return value;
        }

        public bool ReadBool()
        {
            return ReadByte() != 0;
        }

        public string ReadString()
        {
            int length = ReadUInt16();
            if (length == 0) return string.Empty;
            var bytes = ReadBytes(length);
            return System.Text.Encoding.UTF8.GetString(bytes);
        }

        /// <summary>读取有符号字节。</summary>
        public sbyte ReadSByte()
            => (sbyte)ReadByte();

        /// <summary>读取四维向量。</summary>
        public System.Numerics.Vector4 ReadVector4()
        {
            return new System.Numerics.Vector4(ReadSingle(), ReadSingle(), ReadSingle(), ReadSingle());
        }

        /// <summary>读取变换 (位置 + 旋转 + 缩放)。</summary>
        public NumericTransform ReadTransform()
        {
            var pos = ReadVector3();
            var rot = ReadQuaternion();
            var scale = ReadVector3();
            return new NumericTransform(pos, rot, scale);
        }

        public System.Numerics.Vector3 ReadVector3()
        {
            return new System.Numerics.Vector3(ReadSingle(), ReadSingle(), ReadSingle());
        }

        public System.Numerics.Vector2 ReadVector2()
        {
            return new System.Numerics.Vector2(ReadSingle(), ReadSingle());
        }

        public System.Numerics.Quaternion ReadQuaternion()
        {
            return new System.Numerics.Quaternion(ReadSingle(), ReadSingle(), ReadSingle(), ReadSingle());
        }

        public byte[] ReadByteArray()
        {
            int length = ReadInt32();
            if (length == 0) return null;
            return ReadBytes(length);
        }

        // ── 兼容别名（高层调用方使用的短命名）──
        public int ReadInt() => ReadInt32();
        public uint ReadUInt() => ReadUInt32();
        public short ReadShort() => ReadInt16();
        public ushort ReadUShort() => ReadUInt16();
        public float ReadFloat() => ReadSingle();
        public long ReadLong()
        {
            if (_position + 8 > _length) throw new InvalidOperationException("读取超出缓冲区范围");
            long value = BitConverter.ToInt64(_buffer, _position);
            _position += 8;
            return value;
        }
        public ulong ReadULong()
        {
            if (_position + 8 > _length) throw new InvalidOperationException("读取超出缓冲区范围");
            ulong value = BitConverter.ToUInt64(_buffer, _position);
            _position += 8;
            return value;
        }
    }

    /// <summary>
    /// 同步消息类型
    /// </summary>
    public enum ReplicationMessageType : byte
    {
        /// <summary>创建对象</summary>
        Spawn = 0,
        /// <summary>销毁对象</summary>
        Despawn = 1,
        /// <summary>属性同步</summary>
        PropertySync = 2,
        /// <summary>全量同步</summary>
        FullSync = 3,
        /// <summary>所有权变更</summary>
        OwnershipChange = 4,
        /// <summary>对象请求</summary>
        ObjectRequest = 5,
        /// <summary>对象请求响应</summary>
        ObjectRequestResponse = 6
    }

    /// <summary>
    /// 网络复制系统
    /// 负责对象的创建/销毁同步和属性同步
    /// </summary>
    public class NetworkReplicationSystem : IDisposable
    {
        // 传输层引用
        private readonly NetworkTransportLayer _transport;

        // 类型ID到元数据的映射
        private readonly Dictionary<ushort, NetworkObjectMetadata> _typeMetadata = new Dictionary<ushort, NetworkObjectMetadata>();

        // 类型到元数据的映射
        private readonly Dictionary<Type, NetworkObjectMetadata> _typeToMetadata = new Dictionary<Type, NetworkObjectMetadata>();

        // 网络ID到对象实例的映射
        private readonly Dictionary<uint, NetworkObjectInstance> _networkObjects = new Dictionary<uint, NetworkObjectInstance>();

        // 待发送的属性更新队列(按优先级)
        private readonly ConcurrentQueue<PropertyUpdate>[] _updateQueues = new ConcurrentQueue<PropertyUpdate>[4];

        // 下一个网络ID
        private uint _nextNetworkId = 1;

        // 对象表锁
        private readonly object _objectLock = new object();

        // 是否是服务端
        private bool _isServer;

        // 同步间隔(毫秒)
        private int _syncInterval = 50; // 20fps

        // 上次同步时间
        private long _lastSyncTime;

        // 本地连接ID(客户端)
        private int _localConnectionId = -1;

        // 待处理的对象请求
        private readonly Dictionary<uint, DateTime> _pendingObjectRequests = new Dictionary<uint, DateTime>();

        // 已注册的属性变化回调
        private readonly Dictionary<uint, Dictionary<int, List<Action<object>>>> _propertyCallbacks = new Dictionary<uint, Dictionary<int, List<Action<object>>>>();

        // 是否已释放
        private bool _disposed;

        // 统计数据
        private long _totalObjectsSpawned;
        private long _totalObjectsDespawned;
        private long _totalPropertyUpdates;
        private long _totalBytesSynced;

        // 同步线程
        private Thread _syncThread;
        private volatile bool _running;

        // ==================== 事件 ====================

        /// <summary>对象被创建时触发</summary>
        public event Action<INetworkObject> OnObjectSpawned;

        /// <summary>对象被销毁时触发</summary>
        public event Action<INetworkObject> OnObjectDespawned;

        /// <summary>属性更新时触发</summary>
        public event Action<uint, int, object> OnPropertyChanged;

        // ==================== 构造函数 ====================

        public NetworkReplicationSystem(NetworkTransportLayer transport)
        {
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));

            for (int i = 0; i < 4; i++)
            {
                _updateQueues[i] = new ConcurrentQueue<PropertyUpdate>();
            }

            // 注册事件
            _transport.OnPacketReceived += OnPacketReceived;
            _transport.OnConnected += OnConnected;
            _transport.OnDisconnected += OnDisconnected;
        }

        // ==================== 注册 ====================

        /// <summary>
        /// 注册网络对象类型
        /// </summary>
        /// <typeparam name="T">对象类型</typeparam>
        public void RegisterType<T>() where T : INetworkObject, new()
        {
            var type = typeof(T);
            var typeAttr = type.GetCustomAttribute<NetworkObjectAttribute>();

            if (typeAttr == null)
            {
                throw new InvalidOperationException($"类型 {type.Name} 缺少 NetworkObjectAttribute");
            }

            var metadata = new NetworkObjectMetadata
            {
                Type = type,
                TypeId = typeAttr.TypeId,
                Attribute = typeAttr,
                Factory = () => new T()
            };

            // 扫描属性
            int propIndex = 0;
            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var propAttr = prop.GetCustomAttribute<NetworkPropertyAttribute>();
                if (propAttr != null && propAttr.Mode != ReplicationMode.None)
                {
                    var propMeta = new PropertyMetadata
                    {
                        PropertyInfo = prop,
                        Attribute = propAttr,
                        PropertyIndex = propIndex++,
                        IsInitialSync = true
                    };
                    metadata.Properties.Add(propMeta);
                }
            }

            lock (_objectLock)
            {
                _typeMetadata[metadata.TypeId] = metadata;
                _typeToMetadata[type] = metadata;
            }

            Console.WriteLine($"[Replication] 注册类型: {type.Name}, TypeId: {metadata.TypeId}, 同步属性数: {metadata.Properties.Count}");
        }

        /// <summary>
        /// 设置服务端/客户端模式
        /// </summary>
        public void SetServerMode(bool isServer)
        {
            _isServer = isServer;
        }

        /// <summary>
        /// 设置本地连接ID
        /// </summary>
        public void SetLocalConnectionId(int connectionId)
        {
            _localConnectionId = connectionId;
        }

        // ==================== 启动/停止 ====================

        /// <summary>
        /// 启动同步线程
        /// </summary>
        public void Start()
        {
            if (_running) return;
            _running = true;
            _syncThread = new Thread(SyncLoop)
            {
                Name = "ReplicationSync",
                IsBackground = true
            };
            _syncThread.Start();
        }

        /// <summary>
        /// 停止同步
        /// </summary>
        public void Stop()
        {
            _running = false;
            _syncThread?.Join(2000);
        }

        // ==================== 对象管理 ====================

        /// <summary>
        /// 生成网络对象(服务端调用)
        /// </summary>
        /// <typeparam name="T">对象类型</typeparam>
        /// <returns>创建的对象</returns>
        public T Spawn<T>() where T : INetworkObject, new()
        {
            return Spawn<T>(-1);
        }

        /// <summary>
        /// 生成网络对象(服务端调用)
        /// </summary>
        /// <typeparam name="T">对象类型</typeparam>
        /// <param name="ownerConnectionId">所有者连接ID</param>
        public T Spawn<T>(int ownerConnectionId) where T : INetworkObject, new()
        {
            if (!_isServer)
            {
                throw new InvalidOperationException("只有服务端可以Spawn对象");
            }

            var type = typeof(T);
            if (!_typeToMetadata.TryGetValue(type, out var metadata))
            {
                RegisterType<T>();
                _typeToMetadata.TryGetValue(type, out metadata);
            }

            if (metadata == null)
            {
                throw new InvalidOperationException($"类型 {type.Name} 未注册");
            }

            var obj = new T
            {
                NetworkId = GetNextNetworkId(),
                IsServer = true,
                OwnerConnectionId = ownerConnectionId
            };

            // 创建实例信息
            var instance = new NetworkObjectInstance
            {
                Object = obj,
                Metadata = metadata
            };

            // 缓存初始属性值
            foreach (var propMeta in metadata.Properties)
            {
                var value = propMeta.PropertyInfo.GetValue(obj);
                instance.PropertyValueCache[propMeta.PropertyIndex] = value;
                propMeta.LastSyncedValue = value;
            }

            lock (_objectLock)
            {
                _networkObjects[obj.NetworkId] = instance;
            }

            obj.IsInitialized = true;
            obj.OnNetworkSpawn();

            Interlocked.Increment(ref _totalObjectsSpawned);

            // 发送创建消息给所有客户端
            SendSpawnMessage(obj, metadata, -1);

            OnObjectSpawned?.Invoke(obj);

            Console.WriteLine($"[Replication] Spawn对象: Type={type.Name}, NetId={obj.NetworkId}, Owner={ownerConnectionId}");

            return obj;
        }

        /// <summary>
        /// 销毁网络对象(服务端调用)
        /// </summary>
        /// <param name="networkId">网络ID</param>
        public void Despawn(uint networkId)
        {
            NetworkObjectInstance instance;
            lock (_objectLock)
            {
                if (!_networkObjects.TryGetValue(networkId, out instance))
                {
                    return;
                }
                instance.IsDestroyed = true;
            }

            // 调用销毁回调
            instance.Object.OnNetworkDespawn();

            // 发送销毁消息
            SendDespawnMessage(networkId);

            lock (_objectLock)
            {
                _networkObjects.Remove(networkId);
            }

            Interlocked.Increment(ref _totalObjectsDespawned);

            OnObjectDespawned?.Invoke(instance.Object);

            Console.WriteLine($"[Replication] Despawn对象: NetId={networkId}");
        }

        /// <summary>
        /// 获取网络对象
        /// </summary>
        public INetworkObject GetObject(uint networkId)
        {
            lock (_objectLock)
            {
                if (_networkObjects.TryGetValue(networkId, out var instance))
                {
                    return instance.Object;
                }
            }
            return null;
        }

        /// <summary>
        /// 获取所有网络对象
        /// </summary>
        public List<INetworkObject> GetAllObjects()
        {
            lock (_objectLock)
            {
                var result = new List<INetworkObject>();
                foreach (var instance in _networkObjects.Values)
                {
                    if (!instance.IsDestroyed)
                    {
                        result.Add(instance.Object);
                    }
                }
                return result;
            }
        }

        /// <summary>
        /// 获取指定类型的所有对象
        /// </summary>
        public List<T> GetObjectsOfType<T>() where T : INetworkObject
        {
            var result = new List<T>();
            lock (_objectLock)
            {
                foreach (var instance in _networkObjects.Values)
                {
                    if (!instance.IsDestroyed && instance.Object is T typed)
                    {
                        result.Add(typed);
                    }
                }
            }
            return result;
        }

        /// <summary>
        /// 获取下一个网络ID
        /// </summary>
        private uint GetNextNetworkId()
        {
            // 简单递增, 服务端从1开始, 客户端从0x80000000开始避免冲突
            uint baseId = _isServer ? 1u : 0x80000000u;
            return baseId + (uint)Interlocked.Increment(ref _nextNetworkId);
        }

        // ==================== 属性同步 ====================

        /// <summary>
        /// 标记属性为脏(需要同步)
        /// </summary>
        /// <param name="networkId">网络ID</param>
        /// <param name="propertyIndex">属性索引</param>
        public void MarkDirty(uint networkId, int propertyIndex)
        {
            NetworkObjectInstance instance;
            lock (_objectLock)
            {
                if (!_networkObjects.TryGetValue(networkId, out instance))
                {
                    return;
                }
            }

            var propMeta = instance.Metadata.Properties.Find(p => p.PropertyIndex == propertyIndex);
            if (propMeta == null) return;

            var currentValue = propMeta.PropertyInfo.GetValue(instance.Object);

            // 加入更新队列
            var update = new PropertyUpdate
            {
                NetworkId = networkId,
                PropertyIndex = propertyIndex,
                Value = currentValue,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };

            int queueIndex = (int)propMeta.Attribute.Priority;
            _updateQueues[queueIndex].Enqueue(update);
        }

        /// <summary>
        /// 标记所有属性为脏
        /// </summary>
        public void MarkAllDirty(uint networkId)
        {
            NetworkObjectInstance instance;
            lock (_objectLock)
            {
                if (!_networkObjects.TryGetValue(networkId, out instance))
                {
                    return;
                }
            }

            foreach (var propMeta in instance.Metadata.Properties)
            {
                MarkDirty(networkId, propMeta.PropertyIndex);
            }
        }

        /// <summary>
        /// 注册属性变化回调
        /// </summary>
        public void RegisterPropertyCallback(uint networkId, int propertyIndex, Action<object> callback)
        {
            lock (_objectLock)
            {
                if (!_propertyCallbacks.TryGetValue(networkId, out var dict))
                {
                    _propertyCallbacks[networkId] = dict = new Dictionary<int, List<Action<object>>>();
                }
                if (!dict.TryGetValue(propertyIndex, out var list))
                {
                    dict[propertyIndex] = list = new List<Action<object>>();
                }
                list.Add(callback);
            }
        }

        // ==================== 同步循环 ====================

        /// <summary>
        /// 同步线程主循环
        /// </summary>
        private void SyncLoop()
        {
            while (_running)
            {
                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                if (now - _lastSyncTime >= _syncInterval)
                {
                    _lastSyncTime = now;
                    ProcessSync();
                }

                Thread.Sleep(5);
            }
        }

        /// <summary>
        /// 处理同步
        /// </summary>
        private void ProcessSync()
        {
            if (!_isServer) return; // 只有服务端主动同步

            // 检查所有对象的属性变化
            List<NetworkObjectInstance> instances;
            lock (_objectLock)
            {
                instances = new List<NetworkObjectInstance>(_networkObjects.Values);
            }

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            foreach (var instance in instances)
            {
                if (instance.IsDestroyed) continue;

                foreach (var propMeta in instance.Metadata.Properties)
                {
                    if (propMeta.Attribute.Mode == ReplicationMode.ClientToServer)
                    {
                        continue; // 客户端到服务端的属性不在这里处理
                    }

                    var currentValue = propMeta.PropertyInfo.GetValue(instance.Object);

                    // 检查是否需要同步
                    bool needsSync = false;

                    if (propMeta.IsInitialSync)
                    {
                        needsSync = true;
                        propMeta.IsInitialSync = false;
                    }
                    else if (propMeta.Attribute.Condition == ReplicationCondition.Always)
                    {
                        needsSync = true;
                    }
                    else if (propMeta.Attribute.Condition == ReplicationCondition.OnChange)
                    {
                        if (!ValuesEqual(currentValue, propMeta.LastSyncedValue, propMeta.Attribute.Threshold))
                        {
                            needsSync = true;
                        }
                    }

                    if (needsSync)
                    {
                        var update = new PropertyUpdate
                        {
                            NetworkId = instance.Object.NetworkId,
                            PropertyIndex = propMeta.PropertyIndex,
                            Value = currentValue,
                            Timestamp = now
                        };

                        int queueIndex = (int)propMeta.Attribute.Priority;
                        _updateQueues[queueIndex].Enqueue(update);

                        propMeta.LastSyncedValue = currentValue;
                        propMeta.LastSyncTime = now;
                    }
                }
            }

            // 发送属性更新
            SendPropertyUpdates();
        }

        /// <summary>
        /// 发送属性更新
        /// </summary>
        private void SendPropertyUpdates()
        {
            // 按对象分组, 减少包的数量
            var groupedUpdates = new Dictionary<uint, List<PropertyUpdate>>();

            for (int i = 3; i >= 0; i--)
            {
                while (_updateQueues[i].TryDequeue(out var update))
                {
                    if (!groupedUpdates.TryGetValue(update.NetworkId, out var list))
                    {
                        groupedUpdates[update.NetworkId] = list = new List<PropertyUpdate>();
                    }
                    list.Add(update);
                }
            }

            foreach (var kvp in groupedUpdates)
            {
                SendPropertySyncMessage(kvp.Key, kvp.Value);
            }
        }

        // ==================== 消息发送 ====================

        /// <summary>
        /// 发送创建对象消息
        /// </summary>
        private void SendSpawnMessage(INetworkObject obj, NetworkObjectMetadata metadata, int targetConnectionId)
        {
            var writer = new NetworkWriter(256);
            writer.WriteByte((byte)ReplicationMessageType.Spawn);
            writer.WriteUInt16(metadata.TypeId);
            writer.WriteUInt32(obj.NetworkId);
            writer.WriteInt32(obj.OwnerConnectionId);

            // 写入所有属性
            writer.WriteInt16((short)metadata.Properties.Count);
            foreach (var propMeta in metadata.Properties)
            {
                writer.WriteInt16((short)propMeta.PropertyIndex);
                var value = propMeta.PropertyInfo.GetValue(obj);
                WriteValue(writer, value, propMeta.PropertyInfo.PropertyType);
            }

            // 自定义序列化
            obj.NetworkSerialize(writer);

            var data = writer.ToArray();

            if (targetConnectionId >= 0)
            {
                _transport.Send(targetConnectionId, data, true, PacketPriority.High);
            }
            else
            {
                _transport.Broadcast(data, true, PacketPriority.High);
            }

            Interlocked.Add(ref _totalBytesSynced, data.Length);
        }

        /// <summary>
        /// 发送销毁对象消息
        /// </summary>
        private void SendDespawnMessage(uint networkId)
        {
            var writer = new NetworkWriter(16);
            writer.WriteByte((byte)ReplicationMessageType.Despawn);
            writer.WriteUInt32(networkId);

            var data = writer.ToArray();
            _transport.Broadcast(data, true, PacketPriority.High);

            Interlocked.Add(ref _totalBytesSynced, data.Length);
        }

        /// <summary>
        /// 发送属性同步消息
        /// </summary>
        private void SendPropertySyncMessage(uint networkId, List<PropertyUpdate> updates)
        {
            var writer = new NetworkWriter(256);
            writer.WriteByte((byte)ReplicationMessageType.PropertySync);
            writer.WriteUInt32(networkId);
            writer.WriteInt16((short)updates.Count);

            NetworkObjectInstance instance = null;
            lock (_objectLock)
            {
                _networkObjects.TryGetValue(networkId, out instance);
            }

            foreach (var update in updates)
            {
                writer.WriteInt16((short)update.PropertyIndex);

                if (instance != null)
                {
                    var propMeta = instance.Metadata.Properties.Find(p => p.PropertyIndex == update.PropertyIndex);
                    if (propMeta != null)
                    {
                        WriteValue(writer, update.Value, propMeta.PropertyInfo.PropertyType);
                        Interlocked.Increment(ref _totalPropertyUpdates);
                    }
                }
            }

            var data = writer.ToArray();

            // 判断可靠性 - 有可靠标记的属性用可靠传输
            bool reliable = false;
            if (instance != null)
            {
                foreach (var update in updates)
                {
                    var propMeta = instance.Metadata.Properties.Find(p => p.PropertyIndex == update.PropertyIndex);
                    if (propMeta != null && propMeta.Attribute.Reliable)
                    {
                        reliable = true;
                        break;
                    }
                }
            }

            _transport.Broadcast(data, reliable, PacketPriority.Medium);
            Interlocked.Add(ref _totalBytesSynced, data.Length);
        }

        // ==================== 消息接收 ====================

        /// <summary>
        /// 数据包接收事件
        /// </summary>
        private void OnPacketReceived(object sender, PacketReceivedEventArgs e)
        {
            if (e.Packet.Type != PacketType.Data) return;
            if (e.Packet.Payload == null || e.Packet.Payload.Length == 0) return;

            var reader = new NetworkReader(e.Packet.Payload);
            var msgType = (ReplicationMessageType)reader.ReadByte();

            switch (msgType)
            {
                case ReplicationMessageType.Spawn:
                    HandleSpawnMessage(reader, e.ConnectionId);
                    break;

                case ReplicationMessageType.Despawn:
                    HandleDespawnMessage(reader);
                    break;

                case ReplicationMessageType.PropertySync:
                    HandlePropertySyncMessage(reader);
                    break;

                case ReplicationMessageType.FullSync:
                    HandleFullSyncMessage(reader);
                    break;

                case ReplicationMessageType.OwnershipChange:
                    HandleOwnershipChangeMessage(reader);
                    break;

                case ReplicationMessageType.ObjectRequest:
                    HandleObjectRequestMessage(reader, e.ConnectionId);
                    break;

                case ReplicationMessageType.ObjectRequestResponse:
                    HandleObjectRequestResponseMessage(reader);
                    break;
            }
        }

        /// <summary>
        /// 处理创建对象消息
        /// </summary>
        private void HandleSpawnMessage(NetworkReader reader, int connectionId)
        {
            var typeId = reader.ReadUInt16();
            var networkId = reader.ReadUInt32();
            var ownerId = reader.ReadInt32();

            // 检查是否已存在
            lock (_objectLock)
            {
                if (_networkObjects.ContainsKey(networkId))
                {
                    // 已经存在, 可能是重复消息
                    return;
                }
            }

            // 查找类型
            NetworkObjectMetadata metadata;
            lock (_objectLock)
            {
                if (!_typeMetadata.TryGetValue(typeId, out metadata))
                {
                    Console.WriteLine($"[Replication] 收到未知类型ID: {typeId}");
                    return;
                }
            }

            // 创建对象
            var obj = metadata.Factory();
            obj.NetworkId = networkId;
            obj.IsServer = false;
            obj.OwnerConnectionId = ownerId;

            // 读取属性
            var propCount = reader.ReadInt16();
            for (int i = 0; i < propCount; i++)
            {
                var propIndex = reader.ReadInt16();
                var propMeta = metadata.Properties.Find(p => p.PropertyIndex == propIndex);
                if (propMeta != null)
                {
                    var value = ReadValue(reader, propMeta.PropertyInfo.PropertyType);
                    propMeta.PropertyInfo.SetValue(obj, value);
                    propMeta.LastSyncedValue = value;
                    propMeta.IsInitialSync = false;
                }
            }

            // 自定义反序列化
            obj.NetworkDeserialize(reader);

            var instance = new NetworkObjectInstance
            {
                Object = obj,
                Metadata = metadata
            };

            // 缓存属性值
            foreach (var propMeta in metadata.Properties)
            {
                instance.PropertyValueCache[propMeta.PropertyIndex] = propMeta.LastSyncedValue;
            }

            lock (_objectLock)
            {
                _networkObjects[networkId] = instance;
            }

            obj.IsInitialized = true;
            obj.OnNetworkSpawn();

            Interlocked.Increment(ref _totalObjectsSpawned);
            OnObjectSpawned?.Invoke(obj);

            Console.WriteLine($"[Replication] 接收Spawn对象: Type={metadata.Type.Name}, NetId={networkId}");
        }

        /// <summary>
        /// 处理销毁对象消息
        /// </summary>
        private void HandleDespawnMessage(NetworkReader reader)
        {
            var networkId = reader.ReadUInt32();

            NetworkObjectInstance instance;
            lock (_objectLock)
            {
                if (!_networkObjects.TryGetValue(networkId, out instance))
                {
                    return;
                }
                _networkObjects.Remove(networkId);
            }

            instance.Object.OnNetworkDespawn();
            Interlocked.Increment(ref _totalObjectsDespawned);
            OnObjectDespawned?.Invoke(instance.Object);
        }

        /// <summary>
        /// 处理属性同步消息
        /// </summary>
        private void HandlePropertySyncMessage(NetworkReader reader)
        {
            var networkId = reader.ReadUInt32();
            var propCount = reader.ReadInt16();

            NetworkObjectInstance instance;
            lock (_objectLock)
            {
                if (!_networkObjects.TryGetValue(networkId, out instance))
                {
                    // 对象不存在, 请求创建
                    RequestObject(networkId);
                    return;
                }
            }

            for (int i = 0; i < propCount; i++)
            {
                var propIndex = reader.ReadInt16();
                var propMeta = instance.Metadata.Properties.Find(p => p.PropertyIndex == propIndex);
                if (propMeta != null)
                {
                    var value = ReadValue(reader, propMeta.PropertyInfo.PropertyType);
                    propMeta.PropertyInfo.SetValue(instance.Object, value);
                    propMeta.LastSyncedValue = value;
                    propMeta.LastSyncTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    instance.PropertyValueCache[propIndex] = value;

                    OnPropertyChanged?.Invoke(networkId, propIndex, value);

                    // 触发回调
                    TriggerPropertyCallbacks(networkId, propIndex, value);
                }
            }
        }

        /// <summary>
        /// 处理全量同步消息
        /// </summary>
        private void HandleFullSyncMessage(NetworkReader reader)
        {
            var networkId = reader.ReadUInt32();

            NetworkObjectInstance instance;
            lock (_objectLock)
            {
                if (!_networkObjects.TryGetValue(networkId, out instance))
                {
                    return;
                }
            }

            var propCount = reader.ReadInt16();
            for (int i = 0; i < propCount; i++)
            {
                var propIndex = reader.ReadInt16();
                var propMeta = instance.Metadata.Properties.Find(p => p.PropertyIndex == propIndex);
                if (propMeta != null)
                {
                    var value = ReadValue(reader, propMeta.PropertyInfo.PropertyType);
                    propMeta.PropertyInfo.SetValue(instance.Object, value);
                    propMeta.LastSyncedValue = value;
                    instance.PropertyValueCache[propIndex] = value;
                }
            }
        }

        /// <summary>
        /// 处理所有权变更消息
        /// </summary>
        private void HandleOwnershipChangeMessage(NetworkReader reader)
        {
            var networkId = reader.ReadUInt32();
            var newOwnerId = reader.ReadInt32();

            NetworkObjectInstance instance;
            lock (_objectLock)
            {
                if (!_networkObjects.TryGetValue(networkId, out instance))
                {
                    return;
                }
            }

            instance.Object.OwnerConnectionId = newOwnerId;

            // 如果新所有者是本地连接
            if (newOwnerId == _localConnectionId)
            {
                instance.Object.IsServer = _isServer;
                Console.WriteLine($"[Replication] 获得对象所有权: NetId={networkId}");
            }
        }

        /// <summary>
        /// 处理对象请求
        /// </summary>
        private void HandleObjectRequestMessage(NetworkReader reader, int connectionId)
        {
            if (!_isServer) return;

            var networkId = reader.ReadUInt32();

            NetworkObjectInstance instance;
            lock (_objectLock)
            {
                if (!_networkObjects.TryGetValue(networkId, out instance))
                {
                    // 对象不存在, 回复空消息
                    return;
                }
            }

            // 重新发送创建消息
            SendSpawnMessage(instance.Object, instance.Metadata, connectionId);
        }

        /// <summary>
        /// 处理对象请求响应
        /// </summary>
        private void HandleObjectRequestResponseMessage(NetworkReader reader)
        {
            var networkId = reader.ReadUInt32();

            lock (_objectLock)
            {
                _pendingObjectRequests.Remove(networkId);
            }
        }

        /// <summary>
        /// 请求创建对象(客户端发现不存在的对象时调用)
        /// </summary>
        private void RequestObject(uint networkId)
        {
            lock (_objectLock)
            {
                if (_pendingObjectRequests.ContainsKey(networkId))
                {
                    // 已经在请求中了
                    return;
                }
                _pendingObjectRequests[networkId] = DateTime.UtcNow;
            }

            var writer = new NetworkWriter(16);
            writer.WriteByte((byte)ReplicationMessageType.ObjectRequest);
            writer.WriteUInt32(networkId);

            var data = writer.ToArray();
            _transport.Send(0, data, true, PacketPriority.High); // 发给服务端

            Console.WriteLine($"[Replication] 请求对象: NetId={networkId}");
        }

        /// <summary>
        /// 触发属性变化回调
        /// </summary>
        private void TriggerPropertyCallbacks(uint networkId, int propertyIndex, object value)
        {
            List<Action<object>> callbacks = null;
            lock (_objectLock)
            {
                if (_propertyCallbacks.TryGetValue(networkId, out var dict))
                {
                    if (dict.TryGetValue(propertyIndex, out var list))
                    {
                        callbacks = new List<Action<object>>(list);
                    }
                }
            }

            if (callbacks != null)
            {
                foreach (var callback in callbacks)
                {
                    try
                    {
                        callback(value);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[Replication] 属性回调异常: {ex.Message}");
                    }
                }
            }
        }

        // ==================== 连接事件 ====================

        private void OnConnected(object sender, ConnectionEventArgs e)
        {
            if (_isServer)
            {
                // 新客户端连接, 发送所有对象
                Task.Run(() =>
                {
                    Thread.Sleep(100); // 等客户端准备好
                    SendFullSyncToClient(e.ConnectionId);
                });
            }
        }

        private void OnDisconnected(object sender, ConnectionEventArgs e)
        {
            if (_isServer)
            {
                // 客户端断开, 清理它拥有的对象
                var toRemove = new List<uint>();
                lock (_objectLock)
                {
                    foreach (var kvp in _networkObjects)
                    {
                        if (kvp.Value.Object.OwnerConnectionId == e.ConnectionId)
                        {
                            toRemove.Add(kvp.Key);
                        }
                    }
                }

                foreach (var netId in toRemove)
                {
                    Despawn(netId);
                }
            }
        }

        /// <summary>
        /// 发送全量同步给新连接的客户端
        /// </summary>
        private void SendFullSyncToClient(int connectionId)
        {
            List<NetworkObjectInstance> instances;
            lock (_objectLock)
            {
                instances = new List<NetworkObjectInstance>(_networkObjects.Values);
            }

            foreach (var instance in instances)
            {
                if (instance.IsDestroyed) continue;
                SendSpawnMessage(instance.Object, instance.Metadata, connectionId);
            }

            Console.WriteLine($"[Replication] 全量同步给连接 {connectionId}, 对象数: {instances.Count}");
        }

        // ==================== 所有权管理 ====================

        /// <summary>
        /// 变更对象所有权
        /// </summary>
        public void ChangeOwnership(uint networkId, int newOwnerId)
        {
            if (!_isServer) return;

            NetworkObjectInstance instance;
            lock (_objectLock)
            {
                if (!_networkObjects.TryGetValue(networkId, out instance))
                {
                    return;
                }
            }

            instance.Object.OwnerConnectionId = newOwnerId;

            // 发送所有权变更消息
            var writer = new NetworkWriter(16);
            writer.WriteByte((byte)ReplicationMessageType.OwnershipChange);
            writer.WriteUInt32(networkId);
            writer.WriteInt32(newOwnerId);

            var data = writer.ToArray();
            _transport.Broadcast(data, true, PacketPriority.High);

            Console.WriteLine($"[Replication] 所有权变更: NetId={networkId}, 新所有者={newOwnerId}");
        }

        // ==================== 值读写 ====================

        /// <summary>
        /// 写入值
        /// </summary>
        private void WriteValue(NetworkWriter writer, object value, Type type)
        {
            if (value == null)
            {
                writer.WriteByte(0); // null标记
                return;
            }

            writer.WriteByte(1); // 非null

            if (type == typeof(int))
            {
                writer.WriteInt32((int)value);
            }
            else if (type == typeof(uint))
            {
                writer.WriteUInt32((uint)value);
            }
            else if (type == typeof(float))
            {
                writer.WriteSingle((float)value);
            }
            else if (type == typeof(double))
            {
                writer.WriteDouble((double)value);
            }
            else if (type == typeof(bool))
            {
                writer.WriteBool((bool)value);
            }
            else if (type == typeof(byte))
            {
                writer.WriteByte((byte)value);
            }
            else if (type == typeof(short))
            {
                writer.WriteInt16((short)value);
            }
            else if (type == typeof(ushort))
            {
                writer.WriteUInt16((ushort)value);
            }
            else if (type == typeof(string))
            {
                writer.WriteString((string)value);
            }
            else if (type == typeof(System.Numerics.Vector3))
            {
                writer.WriteVector3((System.Numerics.Vector3)value);
            }
            else if (type == typeof(System.Numerics.Vector2))
            {
                writer.WriteVector2((System.Numerics.Vector2)value);
            }
            else if (type == typeof(System.Numerics.Quaternion))
            {
                writer.WriteQuaternion((System.Numerics.Quaternion)value);
            }
            else if (type == typeof(byte[]))
            {
                writer.WriteByteArray((byte[])value);
            }
            else
            {
                // 尝试枚举
                if (type.IsEnum)
                {
                    writer.WriteInt32(Convert.ToInt32(value));
                }
                else
                {
                    throw new NotSupportedException($"不支持的类型: {type.Name}");
                }
            }
        }

        /// <summary>
        /// 读取值
        /// </summary>
        private object ReadValue(NetworkReader reader, Type type)
        {
            bool isNull = reader.ReadByte() == 0;
            if (isNull) return type.IsValueType ? Activator.CreateInstance(type) : null;

            if (type == typeof(int))
            {
                return reader.ReadInt32();
            }
            else if (type == typeof(uint))
            {
                return reader.ReadUInt32();
            }
            else if (type == typeof(float))
            {
                return reader.ReadSingle();
            }
            else if (type == typeof(double))
            {
                return reader.ReadDouble();
            }
            else if (type == typeof(bool))
            {
                return reader.ReadBool();
            }
            else if (type == typeof(byte))
            {
                return reader.ReadByte();
            }
            else if (type == typeof(short))
            {
                return reader.ReadInt16();
            }
            else if (type == typeof(ushort))
            {
                return reader.ReadUInt16();
            }
            else if (type == typeof(string))
            {
                return reader.ReadString();
            }
            else if (type == typeof(System.Numerics.Vector3))
            {
                return reader.ReadVector3();
            }
            else if (type == typeof(System.Numerics.Vector2))
            {
                return reader.ReadVector2();
            }
            else if (type == typeof(System.Numerics.Quaternion))
            {
                return reader.ReadQuaternion();
            }
            else if (type == typeof(byte[]))
            {
                return reader.ReadByteArray();
            }
            else if (type.IsEnum)
            {
                return Enum.ToObject(type, reader.ReadInt32());
            }

            throw new NotSupportedException($"不支持的类型: {type.Name}");
        }

        /// <summary>
        /// 判断两个值是否相等
        /// </summary>
        private bool ValuesEqual(object a, object b, float threshold)
        {
            if (a == null && b == null) return true;
            if (a == null || b == null) return false;

            if (a is float fa && b is float fb)
            {
                return Math.Abs(fa - fb) < threshold;
            }

            if (a is double da && b is double db)
            {
                return Math.Abs(da - db) < threshold;
            }

            if (a is System.Numerics.Vector3 va && b is System.Numerics.Vector3 vb)
            {
                return System.Numerics.Vector3.Distance(va, vb) < threshold;
            }

            if (a is System.Numerics.Vector2 va2 && b is System.Numerics.Vector2 vb2)
            {
                return System.Numerics.Vector2.Distance(va2, vb2) < threshold;
            }

            return a.Equals(b);
        }

        // ==================== 统计 ====================

        public long TotalObjectsSpawned => Interlocked.Read(ref _totalObjectsSpawned);
        public long TotalObjectsDespawned => Interlocked.Read(ref _totalObjectsDespawned);
        public long TotalPropertyUpdates => Interlocked.Read(ref _totalPropertyUpdates);
        public long TotalBytesSynced => Interlocked.Read(ref _totalBytesSynced);

        /// <summary>
        /// 获取统计报告
        /// </summary>
        public string GetStatisticsReport()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("===== 复制系统统计 =====");
            sb.AppendLine($"对象总数: {_networkObjects.Count}");
            sb.AppendLine($"已创建: {TotalObjectsSpawned:N0}");
            sb.AppendLine($"已销毁: {TotalObjectsDespawned:N0}");
            sb.AppendLine($"属性更新: {TotalPropertyUpdates:N0}");
            sb.AppendLine($"同步字节: {TotalBytesSynced:N0}");
            sb.AppendLine($"已注册类型: {_typeMetadata.Count}");

            int pendingUpdates = 0;
            for (int i = 0; i < 4; i++)
            {
                pendingUpdates += _updateQueues[i].Count;
            }
            sb.AppendLine($"待发送更新: {pendingUpdates}");

            return sb.ToString();
        }

        // ==================== Dispose ====================

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed) return;
            _disposed = true;

            if (disposing)
            {
                Stop();

                if (_transport != null)
                {
                    _transport.OnPacketReceived -= OnPacketReceived;
                    _transport.OnConnected -= OnConnected;
                    _transport.OnDisconnected -= OnDisconnected;
                }

                lock (_objectLock)
                {
                    _networkObjects.Clear();
                    _typeMetadata.Clear();
                    _typeToMetadata.Clear();
                    _propertyCallbacks.Clear();
                    _pendingObjectRequests.Clear();
                }
            }
        }

        ~NetworkReplicationSystem()
        {
            Dispose(false);
        }
    }

    /// <summary>
    /// 属性更新信息
    /// </summary>
    internal struct PropertyUpdate
    {
        public uint NetworkId;
        public int PropertyIndex;
        public object Value;
        public long Timestamp;
    }
}
