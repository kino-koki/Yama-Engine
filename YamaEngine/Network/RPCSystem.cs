using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text;
using System.Threading;

namespace YamaEngine.Network
{
    // ════════════════════════════════════════════════════════════════════════
    // yama引擎 - RPC 远程过程调用系统 (RPCSystem)
    //
    // 类似 UE RPC / Unity Netcode RPC 的完整远程调用管线, 涵盖:
    //   * RpcAttribute: 标记 RPC 方法, 指定目标/投递方式/条件
    //   * RpcTarget 枚举: Server/Client/Owner/Multicast/Everyone
    //   * RpcDelivery 枚举: Reliable/Unreliable/ReliableOrdered/UnreliableSequenced
    //   * RpcMethodInfo: 方法反射信息
    //   * RpcRegistry: RPC 注册表, 扫描程序集/注册/反注册/查找
    //   * RpcCall: 远程调用实例 (方法ID/参数序列化/目标连接/回调)
    //   * 参数序列化: 自动序列化所有基础类型 + Vector3 + Quaternion + Transform + byte[]
    //   * RpcCondition: 调用条件
    //   * RPC 日志/调试: 调用计数/延迟/带宽统计
    //   * NetworkRPC: 静态工具类, SendRpc<T>(target, method, args...)
    //
    // 对接系统: NetworkCoreSystem / PropertyReplicationSystem
    // ════════════════════════════════════════════════════════════════════════

    // ──────────────────────────────────────────────────────────────
    // 1. 枚举定义
    // ──────────────────────────────────────────────────────────────

    /// <summary>RPC 调用目标。</summary>
    public enum RpcTarget
    {
        /// <summary>客户端 -> 服务器执行。</summary>
        Server,
        /// <summary>服务器 -> 指定客户端执行。</summary>
        Client,
        /// <summary>服务器 -> 拥有者客户端执行。</summary>
        Owner,
        /// <summary>服务器 -> 所有客户端执行。</summary>
        Multicast,
        /// <summary>所有人 (包括服务器和所有客户端)。</summary>
        Everyone
    }

    /// <summary>RPC 投递方式, 决定可靠性与有序性。</summary>
    public enum RpcDelivery
    {
        /// <summary>可靠有序: 保证到达且顺序正确。</summary>
        ReliableOrdered,
        /// <summary>可靠无序: 保证到达但不保证顺序。</summary>
        ReliableUnordered,
        /// <summary>不可靠: 最低延迟, 可丢包。</summary>
        Unreliable,
        /// <summary>不可靠有序: 只保留最新值。</summary>
        UnreliableSequenced
    }

    // ──────────────────────────────────────────────────────────────
    // 2. RPC 特性
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// RPC 方法标记特性。
    /// 用法: [Rpc(Server)] / [Rpc(Client)] / [Rpc(Owner)] / [Rpc(Multicast)] / [Rpc(Unreliable)]
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
    public sealed class RpcAttribute : Attribute
    {
        /// <summary>调用目标。</summary>
        public RpcTarget Target { get; set; } = RpcTarget.Server;

        /// <summary>投递方式。</summary>
        public RpcDelivery Delivery { get; set; } = RpcDelivery.ReliableOrdered;

        /// <summary>调用条件函数名称 (可选)。</summary>
        public string Condition { get; set; } = string.Empty;

        /// <summary>是否跳过拥有者 (仅 Multicast 有效)。</summary>
        public bool SkipOwner { get; set; }

        /// <summary>自定义方法 ID。为 0 时自动生成。</summary>
        public uint CustomId { get; set; }

        /// <summary>是否可靠投递。默认遵循 Delivery 设置。</summary>
        public bool Reliable { get; set; } = true;

        /// <summary>创建 Server RPC 标记。</summary>
        public RpcAttribute() { }

        /// <summary>用枚举快速创建。</summary>
        public RpcAttribute(RpcTarget target)
        {
            Target = target;
        }

        /// <summary>用字符串快速创建。</summary>
        public RpcAttribute(string targetName)
        {
            Target = targetName.ToLowerInvariant() switch
            {
                "server" => RpcTarget.Server,
                "client" => RpcTarget.Client,
                "owner" => RpcTarget.Owner,
                "multicast" => RpcTarget.Multicast,
                "everyone" => RpcTarget.Everyone,
                "unreliable" => RpcTarget.Server,
                _ => RpcTarget.Server
            };
            if (targetName.Equals("Unreliable", StringComparison.OrdinalIgnoreCase))
            {
                Delivery = RpcDelivery.Unreliable;
            }
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 3. RPC 方法反射信息
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// RPC 方法的反射信息。存储方法名、参数类型、目标、投递方式、条件等元数据。
    /// </summary>
    public sealed class RpcMethodInfo
    {
        /// <summary>方法唯一标识 (哈希值)。</summary>
        public uint MethodId { get; }

        /// <summary>声明类型。</summary>
        public Type DeclaringType { get; }

        /// <summary>方法名称。</summary>
        public string MethodName { get; }

        /// <summary>完整方法名 (类型.方法名)。</summary>
        public string FullName { get; }

        /// <summary>方法信息反射对象。</summary>
        public MethodInfo MethodInfo { get; }

        /// <summary>参数类型列表。</summary>
        public Type[] ParameterTypes { get; }

        /// <summary>RPC 调用目标。</summary>
        public RpcTarget Target { get; }

        /// <summary>RPC 投递方式。</summary>
        /// <summary>RPC 调用条件函数 (可选)。</summary>
        public string ConditionName { get; }

        /// <summary>是否跳过拥有者。</summary>
        public bool SkipOwner { get; }

        /// <summary>参数总大小 (估算)。</summary>
        public int EstimatedSize { get; private set; }

        public RpcMethodInfo(MethodInfo methodInfo, RpcAttribute attr)
        {
            MethodInfo = methodInfo;
            DeclaringType = methodInfo.DeclaringType!;
            MethodName = methodInfo.Name;
            FullName = $"{DeclaringType.Name}.{MethodName}";
            ParameterTypes = methodInfo.GetParameters().Select(p => p.ParameterType).ToArray();
            Target = attr.Target;
            ConditionName = attr.Condition;
            SkipOwner = attr.SkipOwner;

            // 生成方法 ID (基于名称哈希)
            MethodId = ComputeMethodId(FullName);
            EstimatedSize = ComputeEstimatedSize();
        }

        private static uint ComputeMethodId(string name)
        {
            // FNV-1a 哈希
            uint hash = 2166136261;
            foreach (char c in name)
            {
                hash ^= c;
                hash *= 16777619;
            }
            return hash;
        }

        private int ComputeEstimatedSize()
        {
            int size = 4; // MethodId
            foreach (var type in ParameterTypes)
            {
                size += RpcParameterSerializer.EstimateTypeSize(type);
            }
            return size;
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 4. RPC 参数序列化器
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// RPC 参数序列化工具。支持自动序列化所有基础类型及常用引擎类型。
    /// </summary>
    public static class RpcParameterSerializer
    {
        /// <summary>估算类型序列化大小 (字节)。</summary>
        public static int EstimateTypeSize(Type type)
        {
            if (type == typeof(bool)) return 1;
            if (type == typeof(byte) || type == typeof(sbyte)) return 1;
            if (type == typeof(short) || type == typeof(ushort)) return 2;
            if (type == typeof(int) || type == typeof(uint) || type == typeof(float)) return 4;
            if (type == typeof(long) || type == typeof(ulong) || type == typeof(double)) return 8;
            if (type == typeof(string)) return 256; // 估算
            if (type == typeof(byte[])) return 256; // 估算
            if (type == typeof(Vector2)) return 8;
            if (type == typeof(Vector3)) return 12;
            if (type == typeof(Vector4)) return 16;
            if (type == typeof(Quaternion)) return 16;
            if (type == typeof(NumericTransform)) return 40; // 12+16+12
            if (type == typeof(decimal)) return 16;
            if (type.IsEnum) return 4; // 枚举按 int 处理
            return 256; // 默认估算
        }

        /// <summary>序列化单个参数到写入器。</summary>
        public static void SerializeParameter(NetworkWriter writer, Type type, object value)
        {
            if (type == typeof(bool))
            {
                writer.WriteBool((bool)value);
            }
            else if (type == typeof(byte))
            {
                writer.WriteByte((byte)value);
            }
            else if (type == typeof(sbyte))
            {
                writer.WriteSByte((sbyte)value);
            }
            else if (type == typeof(short))
            {
                writer.WriteShort((short)value);
            }
            else if (type == typeof(ushort))
            {
                writer.WriteUShort((ushort)value);
            }
            else if (type == typeof(int))
            {
                writer.WriteInt((int)value);
            }
            else if (type == typeof(uint))
            {
                writer.WriteUInt((uint)value);
            }
            else if (type == typeof(long))
            {
                writer.WriteLong((long)value);
            }
            else if (type == typeof(ulong))
            {
                writer.WriteULong((ulong)value);
            }
            else if (type == typeof(float))
            {
                writer.WriteFloat((float)value);
            }
            else if (type == typeof(double))
            {
                writer.WriteDouble((double)value);
            }
            else if (type == typeof(decimal))
            {
                // decimal 按 byte[16] 序列化
                int[] bits = Decimal.GetBits((decimal)value);
                writer.WriteInt(bits.Length);
                foreach (int b in bits)
                    writer.WriteInt(b);
            }
            else if (type == typeof(string))
            {
                writer.WriteString((string)value);
            }
            else if (type == typeof(byte[]))
            {
                byte[] arr = (byte[])value;
                writer.WriteInt(arr.Length);
                writer.WriteBytes(arr);
            }
            else if (type == typeof(Vector2))
            {
                writer.WriteVector2((Vector2)value);
            }
            else if (type == typeof(Vector3))
            {
                writer.WriteVector3((Vector3)value);
            }
            else if (type == typeof(Vector4))
            {
                writer.WriteVector4((Vector4)value);
            }
            else if (type == typeof(Quaternion))
            {
                writer.WriteQuaternion((Quaternion)value);
            }
            else if (type == typeof(NumericTransform))
            {
                writer.WriteTransform((NumericTransform)value);
            }
            else if (type.IsEnum)
            {
                writer.WriteInt(Convert.ToInt32(value));
            }
            else
            {
                // 尝试调用 INetworkSerializable
                if (value is INetworkSerializable serializable)
                {
                    serializable.Serialize(writer);
                }
                else
                {
                    // 未知类型, 按字符串序列化
                    writer.WriteString(value?.ToString() ?? string.Empty);
                }
            }
        }

        /// <summary>从读取器反序列化单个参数。</summary>
        public static object? DeserializeParameter(NetworkReader reader, Type type)
        {
            if (type == typeof(bool)) return reader.ReadBool();
            if (type == typeof(byte)) return reader.ReadByte();
            if (type == typeof(sbyte)) return reader.ReadSByte();
            if (type == typeof(short)) return reader.ReadShort();
            if (type == typeof(ushort)) return reader.ReadUShort();
            if (type == typeof(int)) return reader.ReadInt();
            if (type == typeof(uint)) return reader.ReadUInt();
            if (type == typeof(long)) return reader.ReadLong();
            if (type == typeof(ulong)) return reader.ReadULong();
            if (type == typeof(float)) return reader.ReadFloat();
            if (type == typeof(double)) return reader.ReadDouble();
            if (type == typeof(decimal))
            {
                int count = reader.ReadInt();
                int[] bits = new int[count];
                for (int i = 0; i < count; i++) bits[i] = reader.ReadInt();
                return new decimal(bits);
            }
            if (type == typeof(string)) return reader.ReadString();
            if (type == typeof(byte[]))
            {
                int len = reader.ReadInt();
                return reader.ReadBytes(len);
            }
            if (type == typeof(Vector2)) return reader.ReadVector2();
            if (type == typeof(Vector3)) return reader.ReadVector3();
            if (type == typeof(Vector4)) return reader.ReadVector4();
            if (type == typeof(Quaternion)) return reader.ReadQuaternion();
            if (type == typeof(NumericTransform)) return reader.ReadTransform();
            if (type.IsEnum) return Enum.ToObject(type, reader.ReadInt());

            // 尝试 INetworkSerializable
            try
            {
                if (typeof(INetworkSerializable).IsAssignableFrom(type))
                {
                    var instance = (INetworkSerializable?)Activator.CreateInstance(type);
                    instance?.Deserialize(reader);
                    return instance;
                }
            }
            catch
            {
                // 忽略
            }

            // 未知类型, 读字符串
            return reader.ReadString();
        }

        /// <summary>序列化参数数组。</summary>
        public static void SerializeParameters(NetworkWriter writer, Type[] paramTypes, object?[] args)
        {
            writer.WriteByte((byte)paramTypes.Length);
            for (int i = 0; i < paramTypes.Length; i++)
            {
                SerializeParameter(writer, paramTypes[i], args[i]!);
            }
        }

        /// <summary>反序列化参数数组。</summary>
        public static object?[] DeserializeParameters(NetworkReader reader, Type[] paramTypes)
        {
            int count = reader.ReadByte();
            var args = new object?[count];
            for (int i = 0; i < count && i < paramTypes.Length; i++)
            {
                args[i] = DeserializeParameter(reader, paramTypes[i]);
            }
            return args;
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 5. RPC 调用条件
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// RPC 调用条件。只在满足条件时才发送/执行 RPC。
    /// </summary>
    public delegate bool RpcCondition(object context);

    /// <summary>
    /// RPC 条件注册器。管理所有命名的 RPC 条件函数。
    /// </summary>
    public static class RpcConditionRegistry
    {
        private static readonly ConcurrentDictionary<string, RpcCondition> _conditions
            = new ConcurrentDictionary<string, RpcCondition>();

        /// <summary>注册条件函数。</summary>
        public static void Register(string name, RpcCondition condition)
        {
            _conditions[name] = condition;
        }

        /// <summary>反注册条件函数。</summary>
        public static void Unregister(string name)
        {
            _conditions.TryRemove(name, out _);
        }

        /// <summary>检查指定条件是否满足。</summary>
        public static bool Evaluate(string name, object context)
        {
            if (string.IsNullOrEmpty(name)) return true;
            if (_conditions.TryGetValue(name, out var condition))
                return condition(context);
            return true; // 未找到条件, 默认允许
        }

        /// <summary>获取所有注册的条件名。</summary>
        public static IEnumerable<string> GetRegisteredNames() => _conditions.Keys;
    }

    // ──────────────────────────────────────────────────────────────
    // 6. RPC 调用实例
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// RPC 远程调用实例。包含方法 ID、参数数据、目标连接、回调等信息。
    /// </summary>
    public sealed class RpcCall
    {
        /// <summary>方法 ID。</summary>
        public uint MethodId { get; }

        /// <summary>序列化后的参数数据。</summary>
        public byte[] ParameterData { get; }

        /// <summary>参数数据长度。</summary>
        public int ParameterDataLength { get; }

        /// <summary>目标连接 ID (0 = 广播)。</summary>
        public int TargetConnectionId { get; set; }

        /// <summary>发送者连接 ID。</summary>
        public int SenderConnectionId { get; set; }

        /// <summary>RPC 调用目标。</summary>
        public RpcTarget Target { get; set; }

        /// <summary>RCP 投递方式。</summary>
        public RpcDelivery Delivery { get; set; }

        /// <summary>调用时间戳。</summary>
        public DateTime SendTime { get; } = DateTime.UtcNow;

        /// <summary>调用完成回调。</summary>
        public Action<bool>? OnComplete { get; set; }

        /// <summary>是否已发送。</summary>
        public bool IsSent { get; set; }

        /// <summary>是否已完成。</summary>
        public bool IsCompleted { get; set; }

        /// <summary>RPC 调用序号 (用于排序)。</summary>
        public ulong SequenceNumber { get; set; }

        public RpcCall(uint methodId, byte[] paramData, int paramLength)
        {
            MethodId = methodId;
            ParameterData = paramData;
            ParameterDataLength = paramLength;
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 7. RPC 注册表
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// RPC 注册表。管理所有 RPC 方法的注册、查找、反注册。
    /// 在引擎启动时扫描程序集中所有标记了 [Rpc] 的方法。
    /// </summary>
    public sealed class RpcRegistry
    {
        private readonly ConcurrentDictionary<uint, RpcMethodInfo> _methodsById
            = new ConcurrentDictionary<uint, RpcMethodInfo>();
        private readonly ConcurrentDictionary<string, RpcMethodInfo> _methodsByFullName
            = new ConcurrentDictionary<string, RpcMethodInfo>();
        private readonly ConcurrentDictionary<Type, List<RpcMethodInfo>> _methodsByType
            = new ConcurrentDictionary<Type, List<RpcMethodInfo>>();
        private readonly object _scanLock = new object();
        private bool _isScanned;

        /// <summary>已注册的方法总数。</summary>
        public int RegisteredCount => _methodsById.Count;

        /// <summary>扫描程序集, 注册所有标记了 [Rpc] 的方法。</summary>
        public void ScanAssembly(Assembly assembly)
        {
            lock (_scanLock)
            {
                foreach (var type in assembly.GetTypes())
                {
                    ScanType(type);
                }
                _isScanned = true;
            }
        }

        /// <summary>扫描单个类型的 RPC 方法。</summary>
        public void ScanType(Type type)
        {
            foreach (var method in type.GetMethods(
                BindingFlags.Instance | BindingFlags.Static |
                BindingFlags.Public | BindingFlags.NonPublic))
            {
                var attr = method.GetCustomAttribute<RpcAttribute>();
                if (attr == null) continue;

                var info = new RpcMethodInfo(method, attr);
                Register(info);
            }
        }

        /// <summary>注册一个 RPC 方法。</summary>
        public void Register(RpcMethodInfo methodInfo)
        {
            if (methodInfo == null) return;
            _methodsById[methodInfo.MethodId] = methodInfo;
            _methodsByFullName[methodInfo.FullName] = methodInfo;

            if (!_methodsByType.TryGetValue(methodInfo.DeclaringType, out var list))
            {
                list = new List<RpcMethodInfo>();
                _methodsByType[methodInfo.DeclaringType] = list;
            }
            lock (list)
            {
                if (!list.Any(m => m.MethodId == methodInfo.MethodId))
                    list.Add(methodInfo);
            }
        }

        /// <summary>反注册指定类型的所有 RPC 方法。</summary>
        public void UnregisterType(Type type)
        {
            if (_methodsByType.TryRemove(type, out var methods))
            {
                foreach (var method in methods)
                {
                    _methodsById.TryRemove(method.MethodId, out _);
                    _methodsByFullName.TryRemove(method.FullName, out _);
                }
            }
        }

        /// <summary>反注册指定方法。</summary>
        public void UnregisterMethod(string fullName)
        {
            if (_methodsByFullName.TryRemove(fullName, out var info))
            {
                _methodsById.TryRemove(info.MethodId, out _);
                if (_methodsByType.TryGetValue(info.DeclaringType, out var list))
                {
                    lock (list) { list.RemoveAll(m => m.MethodId == info.MethodId); }
                }
            }
        }

        /// <summary>根据方法 ID 查找 RPC 信息。</summary>
        public RpcMethodInfo? FindById(uint methodId)
        {
            return _methodsById.TryGetValue(methodId, out var info) ? info : null;
        }

        /// <summary>根据完整名称查找 RPC 信息。</summary>
        public RpcMethodInfo? FindByFullName(string fullName)
        {
            return _methodsByFullName.TryGetValue(fullName, out var info) ? info : null;
        }

        /// <summary>获取指定类型的所有 RPC 方法。</summary>
        public List<RpcMethodInfo> GetMethodsForType(Type type)
        {
            return _methodsByType.TryGetValue(type, out var list) ? list : new List<RpcMethodInfo>();
        }

        /// <summary>获取所有已注册的 RPC 方法。</summary>
        public IEnumerable<RpcMethodInfo> GetAllMethods() => _methodsById.Values;

        /// <summary>清空所有注册。</summary>
        public void Clear()
        {
            _methodsById.Clear();
            _methodsByFullName.Clear();
            _methodsByType.Clear();
            _isScanned = false;
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 8. RPC 调度器
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// RPC 调度器。负责发送和接收 RPC 调用, 路由到正确的目标。
    /// </summary>
    public sealed class RpcDispatcher
    {
        private readonly RpcRegistry _registry;
        private ulong _nextSequenceNumber;

        // ── 统计 ──────────────────────────────────────────────────

        private long _totalCallsSent;
        private long _totalCallsReceived;
        private long _totalBytesSent;
        private long _totalBytesReceived;
        private long _totalErrors;
        private double _totalCallLatency;

        /// <summary>已发送的 RPC 调用总数。</summary>
        public long TotalCallsSent => Interlocked.Read(ref _totalCallsSent);
        /// <summary>已接收的 RPC 调用总数。</summary>
        public long TotalCallsReceived => Interlocked.Read(ref _totalCallsReceived);
        /// <summary>已发送的 RPC 字节总数。</summary>
        public long TotalBytesSent => Interlocked.Read(ref _totalBytesSent);
        /// <summary>已接收的 RPC 字节总数。</summary>
        public long TotalBytesReceived => Interlocked.Read(ref _totalBytesReceived);
        /// <summary>RPC 错误总数。</summary>
        public long TotalErrors => Interlocked.Read(ref _totalErrors);
        /// <summary>RPC 平均延迟 (毫秒)。</summary>
        public double AverageLatencyMs => _totalCallsReceived > 0
            ? _totalCallLatency / _totalCallsReceived : 0;

        /// <summary>RPC 调用事件 (调试)。</summary>
        public event Action<string, RpcMethodInfo, object?[]>? OnRpcInvoked;
        public event Action<string, uint, int>? OnRpcReceived;
        public event Action<string, string, Exception>? OnRpcError;

        /// <summary>RPC 调用统计 (按方法名)。</summary>
        private readonly ConcurrentDictionary<string, RpcCallStats> _callStats
            = new ConcurrentDictionary<string, RpcCallStats>();

        public RpcDispatcher(RpcRegistry registry)
        {
            _registry = registry;
        }

        /// <summary>
        /// 发送 RPC 调用。
        /// </summary>
        public bool SendRpc(
            object target,
            string methodName,
            object?[] args,
            RpcDelivery delivery = RpcDelivery.ReliableOrdered)
        {
            var type = target.GetType();
            string fullName = $"{type.Name}.{methodName}";
            var methodInfo = _registry.FindByFullName(fullName);
            if (methodInfo == null)
            {
                OnRpcError?.Invoke("SendRpc", fullName,
                    new InvalidOperationException($"RPC method not found: {fullName}"));
                Interlocked.Increment(ref _totalErrors);
                return false;
            }

            return SendRpc(methodInfo, target, args, delivery);
        }

        /// <summary>发送 RPC 调用 (使用已知的 RpcMethodInfo)。</summary>
        public bool SendRpc(
            RpcMethodInfo methodInfo,
            object target,
            object?[] args,
            RpcDelivery delivery = RpcDelivery.ReliableOrdered)
        {
            try
            {
                // 序列化参数
                var writer = new NetworkWriter(256);
                writer.WriteUInt(methodInfo.MethodId);
                RpcParameterSerializer.SerializeParameters(
                    writer, methodInfo.ParameterTypes, args);

                var manager = NetworkManager.Instance;
                int targetConnId = 0;

                // 根据目标决定发送方式
                switch (methodInfo.Target)
                {
                    case RpcTarget.Server:
                        // 客户端发送到服务器
                        if (manager.ServerConnection != null)
                            targetConnId = manager.ServerConnection.ConnectionId;
                        break;

                    case RpcTarget.Client:
                        // 需要指定连接 ID
                        if (target is NetworkConnection conn)
                            targetConnId = conn.ConnectionId;
                        break;

                    case RpcTarget.Owner:
                        // 需要知道对象的拥有者连接
                        // 此处由 NetworkObjectSystem 处理
                        break;

                    case RpcTarget.Multicast:
                        targetConnId = 0; // 广播
                        break;

                    case RpcTarget.Everyone:
                        targetConnId = 0; // 广播
                        break;
                }

                var call = new RpcCall(methodInfo.MethodId, writer.Data, writer.Length)
                {
                    TargetConnectionId = targetConnId,
                    SenderConnectionId = manager.ServerConnection?.ConnectionId ?? 0,
                    Target = methodInfo.Target,
                    Delivery = delivery,
                    SequenceNumber = ++_nextSequenceNumber
                };

                // 发送
                bool sent;
                if (targetConnId != 0)
                {
                    sent = manager.SendTo(targetConnId, writer.Data, writer.Length);
                }
                else
                {
                    manager.BroadcastToAll(writer.Data, writer.Length);
                    sent = true;
                }

                if (sent)
                {
                    Interlocked.Increment(ref _totalCallsSent);
                    Interlocked.Add(ref _totalBytesSent, writer.Length);
                    call.IsSent = true;

                    RecordCall(methodInfo.FullName, writer.Length);

                    OnRpcInvoked?.Invoke("Send", methodInfo, args);
                }

                return sent;
            }
            catch (Exception ex)
            {
                OnRpcError?.Invoke("SendRpc", methodInfo.FullName, ex);
                Interlocked.Increment(ref _totalErrors);
                return false;
            }
        }

        /// <summary>
        /// 接收并执行 RPC 调用。
        /// </summary>
        public void ReceiveAndExecute(NetworkConnection sender, byte[] data, int length)
        {
            try
            {
                var reader = new NetworkReader(data);
                uint methodId = reader.ReadUInt();

                var methodInfo = _registry.FindById(methodId);
                if (methodInfo == null)
                {
                    OnRpcError?.Invoke("Receive", $"Id={methodId}",
                        new InvalidOperationException($"RPC method not found for ID: {methodId}"));
                    Interlocked.Increment(ref _totalErrors);
                    return;
                }

                // 反序列化参数
                var args = RpcParameterSerializer.DeserializeParameters(
                    reader, methodInfo.ParameterTypes);

                // 条件检查
                if (!string.IsNullOrEmpty(methodInfo.ConditionName))
                {
                    if (!RpcConditionRegistry.Evaluate(methodInfo.ConditionName, sender))
                        return;
                }

                // 调用方法
                var startTime = Stopwatch.GetTimestamp();
                object? targetInstance = null;
                if (!methodInfo.MethodInfo.IsStatic)
                {
                    // 需要从上下文中获取目标实例
                    // 实际实现中, 由 NetworkObject 系统提供
                    return;
                }

                methodInfo.MethodInfo.Invoke(targetInstance, args);

                double elapsedMs = (Stopwatch.GetTimestamp() - startTime) *
                    1000.0 / Stopwatch.Frequency;
                _totalCallLatency += elapsedMs;

                Interlocked.Increment(ref _totalCallsReceived);
                Interlocked.Add(ref _totalBytesReceived, length);

                OnRpcReceived?.Invoke("Execute", methodId, length);
                OnRpcInvoked?.Invoke("Receive", methodInfo, args);
            }
            catch (Exception ex)
            {
                OnRpcError?.Invoke("Execute", "Unknown", ex);
                Interlocked.Increment(ref _totalErrors);
            }
        }

        // ── 统计 ──────────────────────────────────────────────────

        private void RecordCall(string methodName, int bytes)
        {
            var stats = _callStats.GetOrAdd(methodName, _ => new RpcCallStats());
            stats.RecordCall(bytes);
        }

        /// <summary>获取指定方法的调用统计。</summary>
        public RpcCallStats? GetCallStats(string methodName)
        {
            return _callStats.TryGetValue(methodName, out var stats) ? stats : null;
        }

        /// <summary>获取所有方法的调用统计。</summary>
        public IEnumerable<KeyValuePair<string, RpcCallStats>> GetAllCallStats()
        {
            return _callStats;
        }

        /// <summary>重置所有统计。</summary>
        public void ResetStats()
        {
            _totalCallsSent = 0;
            _totalCallsReceived = 0;
            _totalBytesSent = 0;
            _totalBytesReceived = 0;
            _totalErrors = 0;
            _totalCallLatency = 0;
            _callStats.Clear();
        }
    }

    /// <summary>单个 RPC 方法的调用统计。</summary>
    public sealed class RpcCallStats
    {
        private long _callCount;
        private long _totalBytes;
        private long _errorCount;
        private double _minLatency = double.MaxValue;
        private double _maxLatency;
        private double _totalLatency;

        /// <summary>调用次数。</summary>
        public long CallCount => Interlocked.Read(ref _callCount);
        /// <summary>总字节数。</summary>
        public long TotalBytes => Interlocked.Read(ref _totalBytes);
        /// <summary>错误次数。</summary>
        public long ErrorCount => Interlocked.Read(ref _errorCount);
        /// <summary>平均字节数。</summary>
        public double AverageBytes => _callCount > 0 ? (double)_totalBytes / _callCount : 0;

        public void RecordCall(int bytes)
        {
            Interlocked.Increment(ref _callCount);
            Interlocked.Add(ref _totalBytes, bytes);
        }

        public void RecordError()
        {
            Interlocked.Increment(ref _errorCount);
        }

        public void RecordLatency(double ms)
        {
            lock (this)
            {
                _minLatency = Math.Min(_minLatency, ms);
                _maxLatency = Math.Max(_maxLatency, ms);
                _totalLatency += ms;
            }
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 9. NetworkRPC 静态工具类
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 网络 RPC 静态工具类。提供便捷的 RPC 发送接口。
    /// 用法: NetworkRPC.Send(target, "MethodName", arg1, arg2);
    /// </summary>
    public static class NetworkRPC
    {
        private static RpcRegistry? _registry;
        private static RpcDispatcher? _dispatcher;

        /// <summary>RPC 注册表 (延迟初始化)。</summary>
        public static RpcRegistry Registry => _registry ??= new RpcRegistry();

        /// <summary>RPC 调度器 (延迟初始化)。</summary>
        public static RpcDispatcher Dispatcher => _dispatcher ??= new RpcDispatcher(Registry);

        /// <summary>
        /// 扫描指定程序集的 RPC 方法。
        /// </summary>
        public static void ScanAssembly(Assembly assembly)
        {
            Registry.ScanAssembly(assembly);
        }

        /// <summary>
        /// 发送 RPC 调用。
        /// </summary>
        /// <typeparam name="T">目标对象类型。</typeparam>
        /// <param name="target">目标对象。</param>
        /// <param name="methodName">方法名。</param>
        /// <param name="args">方法参数。</param>
        public static void Send<T>(T target, string methodName, params object?[] args)
        {
            Dispatcher.SendRpc(target, methodName, args);
        }

        /// <summary>
        /// 发送 RPC 调用 (指定投递方式)。
        /// </summary>
        public static void Send<T>(T target, string methodName, RpcDelivery delivery,
            params object?[] args)
        {
            Dispatcher.SendRpc(target, methodName, args, delivery);
        }

        /// <summary>
        /// 发送 Server RPC (客户端 -> 服务器)。
        /// </summary>
        public static void ServerRpc<T>(T target, string methodName, params object?[] args)
        {
            Send(target, methodName, args);
        }

        /// <summary>
        /// 发送 Client RPC (服务器 -> 指定客户端)。
        /// </summary>
        public static void ClientRpc<T>(T target, string methodName, NetworkConnection client,
            params object?[] args)
        {
            // 通过 RpcDispatcher 发送到指定客户端
            var type = target.GetType();
            string fullName = $"{type.Name}.{methodName}";
            var methodInfo = Registry.FindByFullName(fullName);
            if (methodInfo == null) return;

            Dispatcher.SendRpc(methodInfo, target, args);
        }

        /// <summary>
        /// 发送 Multicast RPC (服务器 -> 所有客户端)。
        /// </summary>
        public static void MulticastRpc<T>(T target, string methodName, params object?[] args)
        {
            Send(target, methodName, args);
        }

        /// <summary>
        /// 发送不可靠 RPC (最低延迟, 可丢包)。
        /// </summary>
        public static void UnreliableRpc<T>(T target, string methodName, params object?[] args)
        {
            Send(target, methodName, RpcDelivery.Unreliable, args);
        }

        /// <summary>获取 RPC 调用统计。</summary>
        public static RpcCallStats? GetStats(string methodName)
        {
            return Dispatcher.GetCallStats(methodName);
        }

        /// <summary>获取所有 RPC 统计。</summary>
        public static IEnumerable<KeyValuePair<string, RpcCallStats>> GetAllStats()
        {
            return Dispatcher.GetAllCallStats();
        }

        /// <summary>重置所有 RPC 统计。</summary>
        public static void ResetStats() => Dispatcher.ResetStats();
    }
}
