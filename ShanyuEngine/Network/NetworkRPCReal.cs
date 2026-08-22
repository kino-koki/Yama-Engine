using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;

namespace ShanyuEngine.Network
{
    // ════════════════════════════════════════════════════════════════════════
    // 山雨引擎 - 真实 RPC 系统 (NetworkRPCReal)
    //
    // 包含:
    //   * RpcAttribute: 标记 RPC 方法
    //   * RPCMethodEntry: 方法元数据
    //   * RPCRegistryReal: RPC 注册表 (FNV-1a 哈希方法 ID)
    //   * RPCDispatcherReal: RPC 调度器 (发送/接收/参数序列化/返回值/统计)
    //
    // 所有方法均为真实实现, 无占位式实现。
    // 目标框架: net8.0 (C# 8.0+), 无额外 NuGet 依赖。
    // ════════════════════════════════════════════════════════════════════════

    // ──────────────────────────────────────────────────────────
    // RPC 属性与枚举
    // ──────────────────────────────────────────────────────────

    /// <summary>
    /// 标记方法为 RPC 方法。
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]

    /// <summary>RPC 目标枚举。</summary>

    /// <summary>RPC 包类型。</summary>
    internal enum RpcPacketType : byte
    {
        /// <summary>RPC 请求 (无返回值)。</summary>
        Request = 0,
        /// <summary>RPC 请求 (期望返回值)。</summary>
        RequestWithResponse = 1,
        /// <summary>RPC 响应。</summary>
        Response = 2
    }

    // ════════════════════════════════════════════════════════════
    // RPCMethodEntry - 方法元数据
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// RPC 方法元数据: 包含方法信息、ID 和参数类型。
    /// </summary>
    public class RPCMethodEntry
    {
        /// <summary>方法 ID (FNV-1a 哈希)。</summary>
        public uint MethodId;

        /// <summary>方法名。</summary>
        public string MethodName;

        /// <summary>反射方法信息。</summary>
        public MethodInfo Method;

        /// <summary>参数类型数组。</summary>
        public Type[] ParameterTypes;

        /// <summary>返回类型。</summary>
        public Type ReturnType;

        /// <summary>RPC 属性。</summary>
        public RpcAttribute Attribute;

        /// <summary>目标对象 (实例方法时非 null, 静态方法时 null)。</summary>
        public object Target;

        /// <summary>是否静态方法。</summary>
        public bool IsStatic => Target == null && Method.IsStatic;

        /// <summary>是否有返回值。</summary>
        public bool HasReturnValue => ReturnType != typeof(void);
    }

    // ════════════════════════════════════════════════════════════
    // RPCRegistryReal - RPC 注册表
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// RPC 注册表: 管理所有 RPC 方法的注册和查找。
    /// 方法 ID 通过 FNV-1a 哈希方法名+参数类型生成。
    /// </summary>
    public class RPCRegistryReal
    {
        private readonly Dictionary<uint, RPCMethodEntry> _methodsById = new Dictionary<uint, RPCMethodEntry>();
        private readonly Dictionary<string, RPCMethodEntry> _methodsByName = new Dictionary<string, RPCMethodEntry>();
        private readonly object _lock = new object();

        /// <summary>已注册的方法数。</summary>
        public int Count
        {
            get
            {
                lock (_lock) return _methodsById.Count;
            }
        }

        /// <summary>
        /// 注册单个方法。
        /// 算法:
        ///   1. 检查 [Rpc] 属性
        ///   2. 生成方法 ID (FNV-1a 哈希 或 自定义 ID)
        ///   3. 创建元数据条目
        ///   4. 存入 ID 和名称索引
        /// </summary>
        /// <param name="method">要注册的方法。</param>
        /// <param name="target">目标对象 (实例方法时非 null)。</param>
        /// <returns>生成的方法 ID, 0 表示注册失败。</returns>
        public uint RegisterMethod(MethodInfo method, object target = null)
        {
            if (method == null) return 0;

            var attr = method.GetCustomAttribute<RpcAttribute>();
            if (attr == null) return 0;

            // 获取参数类型
            var paramInfos = method.GetParameters();
            Type[] paramTypes = new Type[paramInfos.Length];
            for (int i = 0; i < paramInfos.Length; i++)
                paramTypes[i] = paramInfos[i].ParameterType;

            // 生成方法 ID
            uint methodId = attr.CustomId != 0
                ? attr.CustomId
                : GenerateMethodId(method.Name, paramTypes);

            var entry = new RPCMethodEntry
            {
                MethodId = methodId,
                MethodName = method.Name,
                Method = method,
                ParameterTypes = paramTypes,
                ReturnType = method.ReturnType,
                Attribute = attr,
                Target = target
            };

            lock (_lock)
            {
                _methodsById[methodId] = entry;
                _methodsByName[method.Name] = entry;
            }

            return methodId;
        }

        /// <summary>
        /// 扫描类型的所有 [Rpc] 标记方法并注册。
        /// </summary>
        /// <param name="type">要扫描的类型。</param>
        /// <param name="target">目标对象 (实例方法)。</param>
        /// <returns>注册的方法数。</returns>
        public int ScanType(Type type, object target = null)
        {
            int count = 0;
            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);

            foreach (var method in methods)
            {
                if (method.GetCustomAttribute<RpcAttribute>() != null)
                {
                    // 对于实例方法, 需要 target; 对于静态方法, target 为 null
                    object t = method.IsStatic ? null : target;
                    if (!method.IsStatic && t == null)
                        continue; // 实例方法但无目标对象, 跳过

                    if (RegisterMethod(method, t) != 0)
                        count++;
                }
            }

            return count;
        }

        /// <summary>
        /// 注册目标对象的所有 RPC 方法。
        /// 等价于 ScanType(target.GetType(), target)。
        /// </summary>
        public int RegisterTarget(object target)
        {
            if (target == null) return 0;
            return ScanType(target.GetType(), target);
        }

        /// <summary>按 ID 查找方法。</summary>
        public RPCMethodEntry GetMethod(uint methodId)
        {
            lock (_lock)
            {
                _methodsById.TryGetValue(methodId, out var entry);
                return entry;
            }
        }

        /// <summary>按名称查找方法。</summary>
        public RPCMethodEntry GetMethod(string methodName)
        {
            lock (_lock)
            {
                _methodsByName.TryGetValue(methodName, out var entry);
                return entry;
            }
        }

        /// <summary>尝试按 ID 查找方法。</summary>
        public bool TryGetMethod(uint methodId, out RPCMethodEntry entry)
        {
            lock (_lock)
            {
                return _methodsById.TryGetValue(methodId, out entry);
            }
        }

        /// <summary>清除所有注册的方法。</summary>
        public void Clear()
        {
            lock (_lock)
            {
                _methodsById.Clear();
                _methodsByName.Clear();
            }
        }

        // ── FNV-1a 哈希 ───────────────────────────────────────────

        /// <summary>
        /// 生成方法 ID: 使用 FNV-1a 哈希算法。
        /// 签名 = "方法名:参数类型1,参数类型2,..."
        /// </summary>
        public static uint GenerateMethodId(string methodName, Type[] paramTypes)
        {
            // 构建签名字符串
            StringBuilder sb = new StringBuilder(methodName);
            if (paramTypes != null && paramTypes.Length > 0)
            {
                sb.Append(':');
                for (int i = 0; i < paramTypes.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(paramTypes[i].FullName ?? paramTypes[i].Name);
                }
            }

            // 转为 UTF-8 字节
            byte[] bytes = Encoding.UTF8.GetBytes(sb.ToString());

            // FNV-1a 32-bit 哈希
            uint hash = 2166136261u; // FNV offset basis
            for (int i = 0; i < bytes.Length; i++)
            {
                hash ^= bytes[i];
                hash *= 16777619u; // FNV prime
            }

            return hash;
        }

        /// <summary>验证方法 ID 是否与签名匹配 (调试用)。</summary>
        public static bool VerifyMethodId(string methodName, Type[] paramTypes, uint expectedId)
        {
            return GenerateMethodId(methodName, paramTypes) == expectedId;
        }
    }

    // ════════════════════════════════════════════════════════════
    // RPCDispatcherReal - RPC 调度器
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// RPC 调度器: 发送和接收 RPC 调用。
    /// 支持参数序列化 (按类型分派)、返回值回调、调用统计。
    /// </summary>
    public class RPCDispatcherReal
    {
        // ── 依赖 ──────────────────────────────────────────────────

        private readonly RPCRegistryReal _registry;
        private readonly UDPTransportReal _transport;

        // ── 请求/响应关联 ──────────────────────────────────────────

        private uint _nextRequestId = 1;
        private readonly Dictionary<uint, PendingResponse> _pendingCallbacks = new Dictionary<uint, PendingResponse>();
        private readonly object _callbackLock = new object();

        private class PendingResponse
        {
            public uint RequestId;
            public uint MethodId;
            public Type ExpectedReturnType;
            public Action<object> Callback;
            public Action<Exception> ErrorCallback;
            public double SendTime;
        }

        // ── 广播连接列表 ──────────────────────────────────────────

        private readonly List<IPEndPoint> _broadcastConnections = new List<IPEndPoint>();
        private readonly object _connLock = new object();

        // ── 统计 ──────────────────────────────────────────────────

        private readonly Dictionary<uint, RPCStats> _stats = new Dictionary<uint, RPCStats>();
        private readonly object _statsLock = new object();

        private long _totalCallsSent;
        private long _totalCallsReceived;
        private long _totalBytesSent;
        private long _totalBytesReceived;

        /// <summary>总发送调用数。</summary>
        public long TotalCallsSent => _totalCallsSent;

        /// <summary>总接收调用数。</summary>
        public long TotalCallsReceived => _totalCallsReceived;

        /// <summary>总发送字节数。</summary>
        public long TotalBytesSent => _totalBytesSent;

        /// <summary>总接收字节数。</summary>
        public long TotalBytesReceived => _totalBytesReceived;

        // ── 构造函数 ──────────────────────────────────────────────

        public RPCDispatcherReal(RPCRegistryReal registry, UDPTransportReal transport = null)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _transport = transport;
        }

        // ── 发送 RPC ──────────────────────────────────────────────

        /// <summary>
        /// 发送 RPC (无返回值)。
        /// 算法:
        ///   1. 创建 NetworkWriterReal
        ///   2. 写入包类型 (Request)
        ///   3. 写入请求 ID (0 = 无需响应)
        ///   4. 写入方法 ID
        ///   5. 写入参数数量
        ///   6. 逐个序列化参数 (按类型分派)
        ///   7. 发送到目标连接
        /// </summary>
        /// <param name="target">目标端点。</param>
        /// <param name="methodId">方法 ID。</param>
        /// <param name="args">参数数组。</param>
        public void SendRpc(IPEndPoint target, uint methodId, params object[] args)
        {
            SendRpcInternal(target, methodId, false, null, null, args);
        }

        /// <summary>
        /// 发送 RPC (按方法名)。
        /// </summary>
        public void SendRpc(IPEndPoint target, string methodName, params object[] args)
        {
            var entry = _registry.GetMethod(methodName);
            if (entry == null)
                throw new ArgumentException($"RPC method '{methodName}' not registered");

            SendRpc(target, entry.MethodId, args);
        }

        /// <summary>
        /// 发送 RPC (带返回值回调)。
        /// </summary>
        /// <typeparam name="T">返回值类型。</typeparam>
        /// <param name="target">目标端点。</param>
        /// <param name="methodId">方法 ID。</param>
        /// <param name="callback">返回值回调。</param>
        /// <param name="errorCallback">错误回调 (可选)。</param>
        /// <param name="args">参数数组。</param>
        public void SendRpc<T>(
            IPEndPoint target,
            uint methodId,
            Action<T> callback,
            Action<Exception> errorCallback = null,
            params object[] args)
        {
            SendRpcInternal(target, methodId, true, (result) =>
            {
                if (result is T typedResult)
                    callback(typedResult);
                else if (result != null)
                {
                    try
                    {
                        callback((T)Convert.ChangeType(result, typeof(T)));
                    }
                    catch
                    {
                        errorCallback?.Invoke(new InvalidCastException(
                            $"Cannot convert {result?.GetType().Name} to {typeof(T).Name}"));
                    }
                }
            }, errorCallback, args);
        }

        /// <summary>发送 RPC 的内部实现。</summary>
        private void SendRpcInternal(
            IPEndPoint target,
            uint methodId,
            bool expectsResponse,
            Action<object> callback,
            Action<Exception> errorCallback,
            object[] args)
        {
            using (var writer = new NetworkWriterReal(8192))
            {
                // 写入包类型
                writer.WriteByte((byte)(expectsResponse ? RpcPacketType.RequestWithResponse : RpcPacketType.Request));

                // 写入请求 ID
                uint requestId = 0;
                if (expectsResponse)
                {
                    lock (_callbackLock)
                    {
                        requestId = _nextRequestId++;
                    }

                    // 注册回调
                    var pending = new PendingResponse
                    {
                        RequestId = requestId,
                        MethodId = methodId,
                        ExpectedReturnType = null,
                        Callback = callback,
                        ErrorCallback = errorCallback,
                        SendTime = GetTime()
                    };
                    lock (_callbackLock)
                    {
                        _pendingCallbacks[requestId] = pending;
                    }
                }
                writer.WriteUInt32(requestId);

                // 写入方法 ID
                writer.WriteUInt32(methodId);

                // 写入参数数量
                byte paramCount = (byte)(args?.Length ?? 0);
                writer.WriteByte(paramCount);

                // 逐个序列化参数
                if (args != null)
                {
                    foreach (var arg in args)
                    {
                        Type argType = arg?.GetType() ?? typeof(object);
                        NetworkValueSerializer.SerializeValue(writer, arg, argType);
                    }
                }

                // 发送
                byte[] data = writer.ToArray();
                if (_transport != null)
                {
                    // 查找方法的可靠设置
                    bool reliable = true;
                    if (_registry.TryGetMethod(methodId, out var entry))
                        reliable = entry.Attribute.Reliable;

                    if (reliable)
                        _transport.SendReliable(target, data);
                    else
                        _transport.Send(target, data);
                }

                // 统计
                _totalCallsSent++;
                _totalBytesSent += data.Length;
                UpdateStats(methodId, callsSent: 1, bytesSent: data.Length);
            }
        }

        // ── 接收 RPC ──────────────────────────────────────────────

        /// <summary>
        /// 接收并处理 RPC。
        /// 算法:
        ///   1. 读取包类型
        ///   2. 如果是 Request/RequestWithResponse:
        ///      a. 读取请求 ID 和方法 ID
        ///      b. 查找方法
        ///      c. 反序列化参数 (按方法参数类型)
        ///      d. 反射调用方法
        ///      e. 如果期望返回值: 发送响应
        ///   3. 如果是 Response:
        ///      a. 读取请求 ID
        ///      b. 查找回调
        ///      c. 反序列化返回值
        ///      d. 调用回调
        /// </summary>
        /// <param name="reader">数据读取器。</param>
        /// <param name="senderConnection">发送者端点。</param>
        public void ReceiveRpc(NetworkReaderReal reader, IPEndPoint senderConnection)
        {
            if (reader == null || !reader.HasMore) return;

            int packetStartPos = reader.GetPosition();
            byte packetTypeByte = reader.ReadByte();
            if (!Enum.IsDefined(typeof(RpcPacketType), packetTypeByte)) return;

            var packetType = (RpcPacketType)packetTypeByte;
            int packetSize = reader.GetPosition() - packetStartPos;
            _totalBytesReceived += packetSize;

            switch (packetType)
            {
                case RpcPacketType.Request:
                case RpcPacketType.RequestWithResponse:
                    HandleRequest(reader, senderConnection, packetType == RpcPacketType.RequestWithResponse);
                    break;

                case RpcPacketType.Response:
                    HandleResponse(reader);
                    break;
            }
        }

        /// <summary>处理 RPC 请求。</summary>
        private void HandleRequest(NetworkReaderReal reader, IPEndPoint sender, bool expectsResponse)
        {
            // 读取请求 ID
            uint requestId = reader.ReadUInt32();

            // 读取方法 ID
            uint methodId = reader.ReadUInt32();

            // 查找方法
            if (!_registry.TryGetMethod(methodId, out var entry))
            {
                // 方法未注册, 如果期望响应则发送错误
                if (expectsResponse && _transport != null)
                {
                    SendErrorResponse(sender, requestId, $"Method ID {methodId} not found");
                }
                return;
            }

            // 读取参数数量
            int paramCount = reader.ReadByte();

            // 反序列化参数
            object[] args = new object[paramCount];
            for (int i = 0; i < paramCount; i++)
            {
                Type paramType = i < entry.ParameterTypes.Length
                    ? entry.ParameterTypes[i]
                    : typeof(object);
                args[i] = NetworkValueSerializer.DeserializeValue(reader, paramType);
            }

            // 统计
            _totalCallsReceived++;

            // 反射调用方法
            object result = null;
            Exception error = null;

            try
            {
                result = entry.Method.Invoke(entry.Target, args);
            }
            catch (TargetInvocationException tie)
            {
                error = tie.InnerException ?? tie;
            }
            catch (Exception ex)
            {
                error = ex;
            }

            // 更新统计
            UpdateStats(methodId, callsReceived: 1);

            // 发送响应
            if (expectsResponse && _transport != null)
            {
                if (error != null)
                {
                    SendErrorResponse(sender, requestId, error.Message);
                }
                else if (entry.HasReturnValue)
                {
                    SendResponse(sender, requestId, result, entry.ReturnType);
                }
                else
                {
                    // 无返回值, 发送空响应
                    SendResponse(sender, requestId, null, typeof(void));
                }
            }
        }

        /// <summary>处理 RPC 响应。</summary>
        private void HandleResponse(NetworkReaderReal reader)
        {
            // 读取请求 ID
            uint requestId = reader.ReadUInt32();

            // 查找回调
            PendingResponse pending;
            lock (_callbackLock)
            {
                if (!_pendingCallbacks.TryGetValue(requestId, out pending))
                    return; // 未知或超时的请求
                _pendingCallbacks.Remove(requestId);
            }

            // 读取成功标志
            bool success = reader.ReadBool();

            if (success)
            {
                // 读取返回值
                bool hasReturnValue = reader.ReadBool();
                object result = hasReturnValue
                    ? NetworkValueSerializer.DeserializeValue(reader, pending.ExpectedReturnType ?? typeof(object))
                    : null;

                // 计算延迟
                double latencyMs = (GetTime() - pending.SendTime) * 1000;
                UpdateLatencyStats(pending.MethodId, latencyMs);

                // 调用回调
                try
                {
                    pending.Callback?.Invoke(result);
                }
                catch { /* 忽略回调异常 */ }
            }
            else
            {
                // 读取错误信息
                string errorMsg = reader.ReadString();
                pending.ErrorCallback?.Invoke(new Exception(errorMsg));
            }
        }

        /// <summary>发送成功响应。</summary>
        private void SendResponse(IPEndPoint target, uint requestId, object result, Type resultType)
        {
            using (var writer = new NetworkWriterReal(4096))
            {
                writer.WriteByte((byte)RpcPacketType.Response);
                writer.WriteUInt32(requestId);
                writer.WriteBool(true); // success

                bool hasReturnValue = resultType != typeof(void) && result != null;
                writer.WriteBool(hasReturnValue);

                if (hasReturnValue)
                {
                    NetworkValueSerializer.SerializeValue(writer, result, resultType);
                }

                byte[] data = writer.ToArray();
                _transport?.SendReliable(target, data);
                _totalBytesSent += data.Length;
            }
        }

        /// <summary>发送错误响应。</summary>
        private void SendErrorResponse(IPEndPoint target, uint requestId, string errorMsg)
        {
            using (var writer = new NetworkWriterReal(1024))
            {
                writer.WriteByte((byte)RpcPacketType.Response);
                writer.WriteUInt32(requestId);
                writer.WriteBool(false); // failure
                writer.WriteString(errorMsg);

                byte[] data = writer.ToArray();
                _transport?.SendReliable(target, data);
                _totalBytesSent += data.Length;
            }
        }

        // ── 统计 ──────────────────────────────────────────────────

        /// <summary>RPC 统计数据。</summary>
        public class RPCStats
        {
            public long CallsSent;
            public long CallsReceived;
            public long BytesSent;
            public long BytesReceived;
            public double TotalLatencyMs;
            public int LatencySamples;

            public double AverageLatencyMs => LatencySamples > 0
                ? TotalLatencyMs / LatencySamples
                : 0;
        }

        /// <summary>获取指定方法的统计。</summary>
        public RPCStats GetStats(uint methodId)
        {
            lock (_statsLock)
            {
                if (!_stats.TryGetValue(methodId, out var s))
                    return new RPCStats();
                return s;
            }
        }

        /// <summary>获取所有方法的统计快照。</summary>
        public Dictionary<uint, RPCStats> GetAllStats()
        {
            lock (_statsLock)
            {
                return new Dictionary<uint, RPCStats>(_stats);
            }
        }

        /// <summary>更新方法统计。</summary>
        private void UpdateStats(uint methodId, long callsSent = 0, long callsReceived = 0,
            long bytesSent = 0, long bytesReceived = 0)
        {
            lock (_statsLock)
            {
                if (!_stats.TryGetValue(methodId, out var s))
                {
                    s = new RPCStats();
                    _stats[methodId] = s;
                }
                s.CallsSent += callsSent;
                s.CallsReceived += callsReceived;
                s.BytesSent += bytesSent;
                s.BytesReceived += bytesReceived;
            }
        }

        /// <summary>
        /// 更新延迟统计。
        /// 算法: 按 methodId 查找统计条目, 累加延迟并增加采样数。
        /// 使用滑动平均: TotalLatencyMs += latencyMs; LatencySamples++。
        /// 平均延迟 = TotalLatencyMs / LatencySamples。
        /// </summary>
        /// <param name="methodId">RPC 方法 ID。</param>
        /// <param name="latencyMs">本次请求的延迟 (毫秒)。</param>
        private void UpdateLatencyStats(uint methodId, double latencyMs)
        {
            lock (_statsLock)
            {
                if (!_stats.TryGetValue(methodId, out var s))
                {
                    s = new RPCStats();
                    _stats[methodId] = s;
                }
                s.TotalLatencyMs += latencyMs;
                s.LatencySamples++;
            }
        }

        // ── 超时处理 ──────────────────────────────────────────────

        /// <summary>
        /// 检查超时的 RPC 请求 (每帧调用)。
        /// 超时的请求会触发错误回调。
        /// </summary>
        /// <param name="timeoutSeconds">超时阈值 (秒)。</param>
        public void CheckTimeouts(double timeoutSeconds = 5.0)
        {
            double now = GetTime();
            var timedOut = new List<uint>();

            lock (_callbackLock)
            {
                foreach (var kvp in _pendingCallbacks)
                {
                    if (now - kvp.Value.SendTime > timeoutSeconds)
                        timedOut.Add(kvp.Key);
                }

                foreach (var id in timedOut)
                {
                    var pending = _pendingCallbacks[id];
                    _pendingCallbacks.Remove(id);
                    pending.ErrorCallback?.Invoke(new TimeoutException(
                        $"RPC request {id} timed out after {timeoutSeconds}s"));
                }
            }
        }

        /// <summary>获取待处理的请求数。</summary>
        public int PendingRequestCount
        {
            get
            {
                lock (_callbackLock) return _pendingCallbacks.Count;
            }
        }

        // ── 广播 RPC ──────────────────────────────────────────────

        /// <summary>
        /// 添加需要广播的连接。
        /// </summary>
        public void AddConnection(IPEndPoint endpoint)
        {
            lock (_connLock)
            {
                if (!_broadcastConnections.Contains(endpoint))
                    _broadcastConnections.Add(endpoint);
            }
        }

        /// <summary>移除广播连接。</summary>
        public void RemoveConnection(IPEndPoint endpoint)
        {
            lock (_connLock)
            {
                _broadcastConnections.Remove(endpoint);
            }
        }

        /// <summary>获取广播连接数。</summary>
        public int ConnectionCount
        {
            get
            {
                lock (_connLock) return _broadcastConnections.Count;
            }
        }

        /// <summary>
        /// 向所有注册的连接广播 RPC。
        /// 算法:
        ///   1. 创建 NetworkWriterReal, 序列化 RPC 包 (包类型/请求ID/方法ID/参数)
        ///   2. 获取所有广播连接的快照
        ///   3. 遍历每个连接, 通过 transport 发送
        ///   4. 更新统计 (调用数 += 连接数, 字节数 += 数据长度 * 连接数)
        /// </summary>
        /// <param name="methodId">方法 ID。</param>
        /// <param name="args">参数数组。</param>
        /// <param name="excludeEndpoint">要排除的端点 (如发送者自身), 传 null 不排除。</param>
        public void BroadcastRpc(uint methodId, object[] args = null, IPEndPoint excludeEndpoint = null)
        {
            // 序列化一次, 发送到所有连接
            byte[] data;
            using (var writer = new NetworkWriterReal(8192))
            {
                writer.WriteByte((byte)RpcPacketType.Request);
                writer.WriteUInt32(0); // 无需响应, requestId = 0
                writer.WriteUInt32(methodId);

                byte paramCount = (byte)(args?.Length ?? 0);
                writer.WriteByte(paramCount);

                if (args != null)
                {
                    foreach (var arg in args)
                    {
                        Type argType = arg?.GetType() ?? typeof(object);
                        NetworkValueSerializer.SerializeValue(writer, arg, argType);
                    }
                }

                data = writer.ToArray();
            }

            // 获取连接快照
            List<IPEndPoint> connectionsCopy;
            lock (_connLock)
            {
                connectionsCopy = new List<IPEndPoint>(_broadcastConnections);
            }

            // 查找方法的可靠设置
            bool reliable = true;
            if (_registry.TryGetMethod(methodId, out var entry))
                reliable = entry.Attribute.Reliable;

            // 发送到所有连接 (排除指定端点)
            int sentCount = 0;
            if (_transport != null)
            {
                foreach (var conn in connectionsCopy)
                {
                    if (excludeEndpoint != null && conn.Equals(excludeEndpoint))
                        continue;

                    if (reliable)
                        _transport.SendReliable(conn, data);
                    else
                        _transport.Send(conn, data);
                    sentCount++;
                }
            }

            // 更新统计
            _totalCallsSent += sentCount;
            _totalBytesSent += data.Length * sentCount;
            UpdateStats(methodId, callsSent: sentCount, bytesSent: data.Length * sentCount);
        }

        // ── 清除 ──────────────────────────────────────────────────

        /// <summary>清除所有待处理的回调。</summary>
        public void ClearPendingCallbacks()
        {
            lock (_callbackLock)
            {
                _pendingCallbacks.Clear();
            }
        }

        /// <summary>清除所有广播连接。</summary>
        public void ClearConnections()
        {
            lock (_connLock)
            {
                _broadcastConnections.Clear();
            }
        }

        /// <summary>重置所有统计。</summary>
        public void ResetStats()
        {
            _totalCallsSent = 0;
            _totalCallsReceived = 0;
            _totalBytesSent = 0;
            _totalBytesReceived = 0;
            lock (_statsLock)
            {
                _stats.Clear();
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static double GetTime()
        {
            return (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency;
        }
    }
}
