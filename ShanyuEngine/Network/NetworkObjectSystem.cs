using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading;

namespace ShanyuEngine.Network
{
    // ════════════════════════════════════════════════════════════════════════
    // 山雨引擎 - 网络对象系统 (NetworkObjectSystem)
    //
    // 类似 UE Replicated Actor / Unity NetworkObject 的完整网络对象管线:
    //   * NetworkObject: 网络对象 (NetworkId/Owner/Role/生成/销毁/序列化)
    //   * NetworkRole 枚举: None/Authority/Owner/SimulatedProxy/AutonomousProxy
    //   * NetworkSpawner: 网络生成器 (生成/销毁/所有权分配/预生成池)
    //   * NetworkObjectManager: 对象注册表 (ID->对象映射/查找/遍历/统计)
    //   * 对象所有权转移: ChangeOwnership (服务器->客户端/客户端->服务器)
    //   * 对象销毁: NetworkDestroy (权威销毁/请求/延迟销毁)
    //   * 对象持久化: 跨越关卡/场景切换保留
    //
    // 对接系统: NetworkCoreSystem / RPCSystem / PropertyReplicationSystem
    // ════════════════════════════════════════════════════════════════════════

    // ──────────────────────────────────────────────────────────────
    // 1. 枚举定义
    // ──────────────────────────────────────────────────────────────

    /// <summary>网络角色。决定对象在本机的权限。</summary>
    public enum NetworkRole
    {
        /// <summary>无网络角色 (离线对象)。</summary>
        None,
        /// <summary>权威角色。服务器拥有此对象的状态。</summary>
        Authority,
        /// <summary>拥有者角色。客户端拥有此对象。</summary>
        Owner,
        /// <summary>模拟代理。非拥有者客户端上的副本。</summary>
        SimulatedProxy,
        /// <summary>自治代理。拥有者客户端上的可预测副本。</summary>
        AutonomousProxy
    }

    /// <summary>网络对象销毁原因。</summary>
    public enum DestroyReason
    {
        /// <summary>服务器主动销毁。</summary>
        ServerDestroy,
        /// <summary>客户端请求销毁。</summary>
        ClientRequest,
        /// <summary>连接断开导致的自动销毁。</summary>
        ConnectionLost,
        /// <summary>场景/关卡切换。</summary>
        LevelChange,
        /// <summary>超时自动销毁。</summary>
        Timeout
    }

    // ──────────────────────────────────────────────────────────────
    // 2. 网络对象
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 网络对象基类。所有需要网络同步的游戏对象均继承此类。
    /// 提供 NetworkId、Owner、Role、生成/销毁、序列化等核心功能。
    /// </summary>
    public class NetworkObject
    {
        // ── 标识 ──────────────────────────────────────────────────

        private static int _nextNetworkId = 1;

        /// <summary>网络唯一 ID。</summary>
        public int NetworkId { get; }

        /// <summary>对象名称。</summary>
        public string Name { get; set; } = "NetworkObject";

        /// <summary>对象类型名称 (用于序列化/生成)。</summary>
        public virtual string TypeName { get; } = "NetworkObject";

        // ── 所有权/角色 ──────────────────────────────────────────

        /// <summary>当前网络角色。</summary>
        public NetworkRole Role { get; private set; } = NetworkRole.None;

        /// <summary>是否被服务器拥有。</summary>
        public bool IsOwnedByServer => Role == NetworkRole.Authority;

        /// <summary>是否被客户端拥有。</summary>
        public bool IsOwnedByClient =>
            Role == NetworkRole.Owner || Role == NetworkRole.AutonomousProxy;

        /// <summary>拥有者连接 ID (0 = 服务器)。</summary>
        public int OwnerConnectionId { get; private set; }

        /// <summary>上一个拥有者连接 ID。</summary>
        public int PreviousOwnerConnectionId { get; private set; }

        // ── 变换 ──────────────────────────────────────────────────

        /// <summary>网络同步位置。</summary>
        public Vector3 Position { get; set; } = Vector3.Zero;

        /// <summary>网络同步旋转。</summary>
        public Quaternion Rotation { get; set; } = Quaternion.Identity;

        /// <summary>网络同步缩放。</summary>
        public Vector3 Scale { get; set; } = Vector3.One;

        /// <summary>上一帧位置 (用于插值)。</summary>
        public Vector3 PreviousPosition { get; set; } = Vector3.Zero;

        /// <summary>上一帧旋转 (用于插值)。</summary>
        public Quaternion PreviousRotation { get; set; } = Quaternion.Identity;

        // ── 状态 ──────────────────────────────────────────────────

        /// <summary>是否已生成。</summary>
        public bool IsSpawned { get; private set; }

        /// <summary>是否正在销毁。</summary>
        public bool IsDestroying { get; private set; }

        /// <summary>是否已销毁。</summary>
        public bool IsDestroyed { get; private set; }

        /// <summary>是否持久化 (跨越关卡切换保留)。</summary>
        public bool IsPersistent { get; set; }

        /// <summary>是否仅在拥有者客户端生成。</summary>
        public bool SpawnWithOwnership { get; set; }

        /// <summary>对象标签 (用于查找/分组)。</summary>
        public HashSet<string> Tags { get; } = new HashSet<string>();

        /// <summary>自定义数据 (对象关联的额外信息)。</summary>
        public Dictionary<string, object> CustomData { get; } = new Dictionary<string, object>();

        // ── 延迟销毁 ──────────────────────────────────────────────

        private float? _destroyDelay;
        private float _destroyTimer;

        // ── 事件 ──────────────────────────────────────────────────

        /// <summary>对象生成事件。</summary>
        public event Action<NetworkObject>? OnSpawned;

        /// <summary>对象销毁事件。</summary>
        public event Action<NetworkObject, DestroyReason>? OnDestroyed;

        /// <summary>所有权变更事件。</summary>
        public event Action<NetworkObject, int, int>? OnOwnershipChanged;

        /// <summary>角色变更事件。</summary>
        public event Action<NetworkObject, NetworkRole, NetworkRole>? OnRoleChanged;

        /// <summary>属性变更事件。</summary>
        public event Action<NetworkObject>? OnStateChanged;

        /// <summary>客户端开始拥有此对象事件。</summary>
        public event Action<NetworkObject>? OnClientOwnershipGained;

        /// <summary>客户端失去此对象拥有权事件。</summary>
        public event Action<NetworkObject>? OnClientOwnershipLost;

        // ── 构造 ──────────────────────────────────────────────────

        public NetworkObject()
        {
            NetworkId = Interlocked.Increment(ref _nextNetworkId);
        }

        public NetworkObject(int networkId)
        {
            NetworkId = networkId;
            if (networkId >= _nextNetworkId)
                Interlocked.CompareExchange(ref _nextNetworkId, networkId + 1, _nextNetworkId);
        }

        // ── 生成/销毁 ────────────────────────────────────────────

        /// <summary>标记对象为已生成。</summary>
        internal void MarkSpawned()
        {
            IsSpawned = true;
            IsDestroyed = false;
            IsDestroying = false;
            OnSpawned?.Invoke(this);
        }

        /// <summary>标记对象为已销毁。</summary>
        internal void MarkDestroyed(DestroyReason reason)
        {
            IsDestroying = false;
            IsDestroyed = true;
            IsSpawned = false;
            OnDestroyed?.Invoke(this, reason);
        }

        // ── 角色管理 ──────────────────────────────────────────────

        /// <summary>设置网络角色。</summary>
        public void SetRole(NetworkRole newRole)
        {
            var oldRole = Role;
            Role = newRole;
            OnRoleChanged?.Invoke(this, oldRole, newRole);
        }

        // ── 所有权管理 ──────────────────────────────────────────

        /// <summary>设置拥有者连接 ID。</summary>
        public void SetOwner(int connectionId)
        {
            int oldOwner = OwnerConnectionId;
            PreviousOwnerConnectionId = oldOwner;
            OwnerConnectionId = connectionId;

            // 服务器上 Authority, 客户端上 Owner 或 AutonomousProxy
            var manager = NetworkManager.Instance;

            if (manager.Role == NetworkRole.Server || manager.Role == NetworkRole.Host)
            {
                if (connectionId == 0)
                    SetRole(NetworkRole.Authority);
                else
                    SetRole(NetworkRole.Authority); // 服务器始终 Authority
            }
            else
            {
                var localConn = manager.LocalConnection;
                var serverConn = manager.ServerConnection;

                if (connectionId == 0)
                {
                    SetRole(NetworkRole.SimulatedProxy);
                }
                else if (localConn != null && connectionId == localConn.ConnectionId)
                {
                    SetRole(NetworkRole.AutonomousProxy);
                    OnClientOwnershipGained?.Invoke(this);
                }
                else if (serverConn != null && connectionId == serverConn.ConnectionId)
                {
                    SetRole(NetworkRole.AutonomousProxy);
                }
                else
                {
                    SetRole(NetworkRole.SimulatedProxy);
                }
            }

            OnOwnershipChanged?.Invoke(this, oldOwner, connectionId);
        }

        /// <summary>请求所有权转移 (服务器端执行)。</summary>
        public void ChangeOwnership(int newOwnerConnectionId)
        {
            var manager = NetworkManager.Instance;
            if (manager.Role != NetworkRole.Server && manager.Role != NetworkRole.Host)
            {
                // 客户端发送所有权转移请求到服务器
                var writer = new NetworkWriter(32);
                writer.WriteUShort(10); // 所有权转移消息类型
                writer.WriteInt(NetworkId);
                writer.WriteInt(newOwnerConnectionId);
                manager.SendTo(manager.ServerConnection?.ConnectionId ?? 0,
                    writer.Data, writer.Length);
                return;
            }

            // 服务器端直接执行
            int oldOwner = OwnerConnectionId;

            // 通知旧拥有者失去所有权
            if (oldOwner != newOwnerConnectionId && oldOwner != 0)
            {
                var notifyWriter = new NetworkWriter(24);
                notifyWriter.WriteUShort(11); // 所有权丢失消息类型
                notifyWriter.WriteInt(NetworkId);
                manager.SendTo(oldOwner, notifyWriter.Data, notifyWriter.Length);
            }

            SetOwner(newOwnerConnectionId);

            // 通知新拥有者获得所有权
            if (newOwnerConnectionId != 0)
            {
                var grantWriter = new NetworkWriter(24);
                grantWriter.WriteUShort(12); // 所有权获得消息类型
                grantWriter.WriteInt(NetworkId);
                manager.SendTo(newOwnerConnectionId, grantWriter.Data, grantWriter.Length);
            }
        }

        /// <summary>销毁此对象 (仅服务器权威)。</summary>
        public void NetworkDestroy(float delay = 0f)
        {
            var manager = NetworkManager.Instance;
            if (manager.Role != NetworkRole.Server && manager.Role != NetworkRole.Host)
            {
                // 客户端请求服务器销毁
                var writer = new NetworkWriter(16);
                writer.WriteUShort(20); // 销毁请求消息类型
                writer.WriteInt(NetworkId);
                manager.SendTo(manager.ServerConnection?.ConnectionId ?? 0,
                    writer.Data, writer.Length);
                return;
            }

            if (delay > 0f)
            {
                _destroyDelay = delay;
                _destroyTimer = 0f;
                IsDestroying = true;
                return;
            }

            ExecuteDestroy(DestroyReason.ServerDestroy);
        }

        private void ExecuteDestroy(DestroyReason reason)
        {
            var manager = NetworkManager.Instance;

            // 广播销毁消息到所有客户端
            var writer = new NetworkWriter(16);
            writer.WriteUShort(21); // 销毁通知消息类型
            writer.WriteInt(NetworkId);
            writer.WriteByte((byte)reason);
            manager.BroadcastToAll(writer.Data, writer.Length);

            MarkDestroyed(reason);

            // 从对象管理器中移除
            NetworkObjectManager.Instance.Unregister(this);
        }

        // ── 更新 ──────────────────────────────────────────────────

        /// <summary>每帧更新 (由引擎主循环调用)。</summary>
        public virtual void Update(float deltaTime)
        {
            // 延迟销毁计时
            if (IsDestroying && _destroyDelay.HasValue)
            {
                _destroyTimer += deltaTime;
                if (_destroyTimer >= _destroyDelay.Value)
                {
                    ExecuteDestroy(DestroyReason.ServerDestroy);
                }
            }
        }

        // ── 序列化 ────────────────────────────────────────────────

        /// <summary>序列化对象状态 (用于网络同步)。</summary>
        public virtual void SerializeState(NetworkWriter writer)
        {
            writer.WriteInt(NetworkId);
            writer.WriteString(TypeName);
            writer.WriteString(Name);
            writer.WriteInt(OwnerConnectionId);
            writer.WriteByte((byte)Role);
            writer.WriteVector3(Position);
            writer.WriteQuaternion(Rotation);
            writer.WriteVector3(Scale);
            writer.WriteBool(IsPersistent);
        }

        /// <summary>反序列化对象状态。</summary>
        public virtual void DeserializeState(NetworkReader reader)
        {
            // NetworkId 已在生成时设置
            string typeName = reader.ReadString();
            Name = reader.ReadString();
            int ownerId = reader.ReadInt();
            NetworkRole role = (NetworkRole)reader.ReadByte();

            PreviousPosition = Position;
            PreviousRotation = Rotation;

            Position = reader.ReadVector3();
            Rotation = reader.ReadQuaternion();
            Scale = reader.ReadVector3();
            IsPersistent = reader.ReadBool();

            SetOwner(ownerId);
            SetRole(role);
            OnStateChanged?.Invoke(this);
        }

        /// <summary>获取对象描述。</summary>
        public override string ToString() =>
            $"{TypeName}[{NetworkId}] Name={Name} Role={Role} Owner={OwnerConnectionId}";
    }

    // ──────────────────────────────────────────────────────────────
    // 3. 网络对象管理器
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 网络对象管理器。全局注册表, 维护 NetworkId -> NetworkObject 映射。
    /// 提供查找、遍历、统计等功能。
    /// </summary>
    public sealed class NetworkObjectManager
    {
        private static readonly Lazy<NetworkObjectManager> _instance
            = new Lazy<NetworkObjectManager>(() => new NetworkObjectManager());

        /// <summary>单例。</summary>
        public static NetworkObjectManager Instance => _instance.Value;

        private readonly ConcurrentDictionary<int, NetworkObject> _objects
            = new ConcurrentDictionary<int, NetworkObject>();
        private readonly ConcurrentDictionary<Type, List<int>> _objectsByType
            = new ConcurrentDictionary<Type, List<int>>();
        private readonly ConcurrentDictionary<string, List<int>> _objectsByName
            = new ConcurrentDictionary<string, List<int>>();
        private readonly ConcurrentDictionary<int, List<int>> _objectsByOwner
            = new ConcurrentDictionary<int, List<int>>();

        // ── 统计 ──────────────────────────────────────────────────

        private long _totalSpawned;
        private long _totalDestroyed;

        /// <summary>当前注册对象数。</summary>
        public int ObjectCount => _objects.Count;
        /// <summary>总生成数。</summary>
        public long TotalSpawned => Interlocked.Read(ref _totalSpawned);
        /// <summary>总销毁数。</summary>
        public long TotalDestroyed => Interlocked.Read(ref _totalDestroyed);

        /// <summary>对象注册事件。</summary>
        public event Action<NetworkObject>? OnObjectRegistered;
        /// <summary>对象注销事件。</summary>
        public event Action<NetworkObject>? OnObjectUnregistered;

        private NetworkObjectManager() { }

        /// <summary>注册网络对象。</summary>
        public void Register(NetworkObject obj)
        {
            if (obj == null || !obj.IsSpawned) return;

            _objects[obj.NetworkId] = obj;

            var type = obj.GetType();
            if (!_objectsByType.TryGetValue(type, out var typeList))
            {
                typeList = new List<int>();
                _objectsByType[type] = typeList;
            }
            lock (typeList) { if (!typeList.Contains(obj.NetworkId)) typeList.Add(obj.NetworkId); }

            var name = obj.Name;
            if (!_objectsByName.TryGetValue(name, out var nameList))
            {
                nameList = new List<int>();
                _objectsByName[name] = nameList;
            }
            lock (nameList) { nameList.Add(obj.NetworkId); }

            if (!_objectsByOwner.TryGetValue(obj.OwnerConnectionId, out var ownerList))
            {
                ownerList = new List<int>();
                _objectsByOwner[obj.OwnerConnectionId] = ownerList;
            }
            lock (ownerList) { ownerList.Add(obj.NetworkId); }

            Interlocked.Increment(ref _totalSpawned);
            OnObjectRegistered?.Invoke(obj);
        }

        /// <summary>注销网络对象。</summary>
        public void Unregister(NetworkObject obj)
        {
            if (obj == null) return;

            _objects.TryRemove(obj.NetworkId, out _);

            if (_objectsByType.TryGetValue(obj.GetType(), out var typeList))
                lock (typeList) { typeList.Remove(obj.NetworkId); }

            if (_objectsByName.TryGetValue(obj.Name, out var nameList))
                lock (nameList) { nameList.Remove(obj.NetworkId); }

            if (_objectsByOwner.TryGetValue(obj.OwnerConnectionId, out var ownerList))
                lock (ownerList) { ownerList.Remove(obj.NetworkId); }

            Interlocked.Increment(ref _totalDestroyed);
            OnObjectUnregistered?.Invoke(obj);
        }

        /// <summary>根据 NetworkId 查找对象。</summary>
        public NetworkObject? FindById(int networkId)
        {
            return _objects.TryGetValue(networkId, out var obj) ? obj : null;
        }

        /// <summary>根据名称查找所有对象。</summary>
        public List<NetworkObject> FindByName(string name)
        {
            var result = new List<NetworkObject>();
            if (_objectsByName.TryGetValue(name, out var ids))
            {
                lock (ids)
                {
                    foreach (var id in ids)
                    {
                        if (_objects.TryGetValue(id, out var obj))
                            result.Add(obj);
                    }
                }
            }
            return result;
        }

        /// <summary>根据类型查找所有对象。</summary>
        public List<T> FindByType<T>() where T : NetworkObject
        {
            var result = new List<T>();
            var type = typeof(T);
            if (_objectsByType.TryGetValue(type, out var ids))
            {
                lock (ids)
                {
                    foreach (var id in ids)
                    {
                        if (_objects.TryGetValue(id, out var obj) && obj is T typed)
                            result.Add(typed);
                    }
                }
            }
            return result;
        }

        /// <summary>根据拥有者连接 ID 查找所有对象。</summary>
        public List<NetworkObject> FindByOwner(int connectionId)
        {
            var result = new List<NetworkObject>();
            if (_objectsByOwner.TryGetValue(connectionId, out var ids))
            {
                lock (ids)
                {
                    foreach (var id in ids)
                    {
                        if (_objects.TryGetValue(id, out var obj))
                            result.Add(obj);
                    }
                }
            }
            return result;
        }

        /// <summary>根据标签查找对象。</summary>
        public List<NetworkObject> FindByTag(string tag)
        {
            return _objects.Values.Where(obj => obj.Tags.Contains(tag)).ToList();
        }

        /// <summary>遍历所有对象。</summary>
        public IEnumerable<NetworkObject> GetAll()
        {
            return _objects.Values;
        }

        /// <summary>更新所有对象的延迟销毁。</summary>
        public void UpdateAll(float deltaTime)
        {
            foreach (var obj in _objects.Values)
            {
                if (!obj.IsDestroyed)
                    obj.Update(deltaTime);
            }
        }

        /// <summary>销毁指定连接的所有对象。</summary>
        public void DestroyObjectsForConnection(int connectionId, DestroyReason reason)
        {
            var objs = FindByOwner(connectionId).ToList();
            foreach (var obj in objs)
            {
                if (!obj.IsPersistent)
                    obj.NetworkDestroy();
            }
        }

        /// <summary>获取所有对象的统计信息。</summary>
        public string GetStatistics()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"NetworkObjects: {ObjectCount} registered, {TotalSpawned} total spawned, {TotalDestroyed} total destroyed");
            sb.AppendLine($"  By Type: {_objectsByType.Count} types");
            foreach (var kvp in _objectsByType)
            {
                lock (kvp.Value) { sb.AppendLine($"    {kvp.Key.Name}: {kvp.Value.Count}"); }
            }
            return sb.ToString();
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 4. 网络生成器
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 网络对象生成器。管理网络对象的生成/销毁/所有权分配/预生成池。
    /// </summary>
    public sealed class NetworkSpawner
    {
        private static readonly Lazy<NetworkSpawner> _instance
            = new Lazy<NetworkSpawner>(() => new NetworkSpawner());

        /// <summary>单例。</summary>
        public static NetworkSpawner Instance => _instance.Value;

        // ── 对象工厂注册 ──────────────────────────────────────────

        private readonly ConcurrentDictionary<string, Func<NetworkObject>> _factories
            = new ConcurrentDictionary<string, Func<NetworkObject>>();

        // ── 预生成池 ──────────────────────────────────────────────

        private readonly ConcurrentDictionary<string, ConcurrentBag<NetworkObject>> _pools
            = new ConcurrentDictionary<string, ConcurrentBag<NetworkObject>>();
        private readonly ConcurrentDictionary<string, int> _poolMaxSizes
            = new ConcurrentDictionary<string, int>();

        // ── 统计 ──────────────────────────────────────────────────

        private long _totalSpawned;
        private long _totalPooled;
        private long _totalRecycled;

        /// <summary>总生成数。</summary>
        public long TotalSpawned => Interlocked.Read(ref _totalSpawned);
        /// <summary>总入池数。</summary>
        public long TotalPooled => Interlocked.Read(ref _totalPooled);
        /// <summary>总回收数。</summary>
        public long TotalRecycled => Interlocked.Read(ref _totalRecycled);

        /// <summary>对象生成事件。</summary>
        public event Action<NetworkObject>? OnSpawned;
        /// <summary>对象销毁事件 (生成器)。</summary>
        public event Action<NetworkObject, DestroyReason>? OnDestroyed;

        private NetworkSpawner() { }

        // ── 工厂注册 ──────────────────────────────────────────────

        /// <summary>注册对象工厂。</summary>
        public void RegisterFactory<T>(string typeName) where T : NetworkObject, new()
        {
            _factories[typeName] = () => new T();
        }

        /// <summary>注册对象工厂 (自定义构造)。</summary>
        public void RegisterFactory(string typeName, Func<NetworkObject> factory)
        {
            _factories[typeName] = factory;
        }

        /// <summary>反注册对象工厂。</summary>
        public void UnregisterFactory(string typeName)
        {
            _factories.TryRemove(typeName, out _);
        }

        // ── 对象生成 ──────────────────────────────────────────────

        /// <summary>生成网络对象 (服务器权威)。</summary>
        public T Spawn<T>(int ownerConnectionId = 0,
            Vector3? position = null, Quaternion? rotation = null)
            where T : NetworkObject, new()
        {
            return (T)Spawn(typeof(T).Name, ownerConnectionId, position, rotation);
        }

        /// <summary>生成网络对象 (指定类型名称)。</summary>
        public NetworkObject Spawn(string typeName, int ownerConnectionId = 0,
            Vector3? position = null, Quaternion? rotation = null)
        {
            var obj = CreateObject(typeName);
            if (obj == null)
            {
                obj = new NetworkObject();
                obj.Name = typeName;
            }

            if (position.HasValue) obj.Position = position.Value;
            if (rotation.HasValue) obj.Rotation = rotation.Value;

            SpawnObject(obj, ownerConnectionId);
            return obj;
        }

        /// <summary>生成并分配给指定玩家。</summary>
        public T SpawnForPlayer<T>(int playerConnectionId,
            Vector3? position = null, Quaternion? rotation = null)
            where T : NetworkObject, new()
        {
            return Spawn<T>(playerConnectionId, position, rotation);
        }

        /// <summary>执行生成流程。</summary>
        private void SpawnObject(NetworkObject obj, int ownerConnectionId)
        {
            obj.SetOwner(ownerConnectionId);
            obj.MarkSpawned();

            // 注册到对象管理器
            NetworkObjectManager.Instance.Register(obj);

            // 广播生成消息
            var manager = NetworkManager.Instance;
            if (manager.Role == NetworkRole.Server || manager.Role == NetworkRole.Host)
            {
                var writer = new NetworkWriter(256);
                writer.WriteUShort(5); // 生成消息类型
                obj.SerializeState(writer);
                manager.BroadcastToAll(writer.Data, writer.Length);
            }

            Interlocked.Increment(ref _totalSpawned);
            OnSpawned?.Invoke(obj);
        }

        /// <summary>从池中创建或创建新对象。</summary>
        private NetworkObject? CreateObject(string typeName)
        {
            // 先尝试从池中取
            if (_pools.TryGetValue(typeName, out var pool) && pool.TryTake(out var pooled))
            {
                Interlocked.Increment(ref _totalRecycled);
                return pooled;
            }

            // 从工厂创建
            if (_factories.TryGetValue(typeName, out var factory))
                return factory();

            return null;
        }

        // ── 对象销毁 ──────────────────────────────────────────────

        /// <summary>销毁网络对象。</summary>
        public void Destroy(NetworkObject obj, DestroyReason reason = DestroyReason.ServerDestroy)
        {
            if (obj == null || obj.IsDestroyed) return;

            // 如果有预生成池, 回收到池中
            if (_pools.ContainsKey(obj.TypeName))
            {
                ReturnToPool(obj);
            }
            else
            {
                obj.MarkDestroyed(reason);
                NetworkObjectManager.Instance.Unregister(obj);
            }

            // 广播销毁消息
            var manager = NetworkManager.Instance;
            if (manager.Role == NetworkRole.Server || manager.Role == NetworkRole.Host)
            {
                var writer = new NetworkWriter(16);
                writer.WriteUShort(21); // 销毁消息类型
                writer.WriteInt(obj.NetworkId);
                manager.BroadcastToAll(writer.Data, writer.Length);
            }

            OnDestroyed?.Invoke(obj, reason);
        }

        /// <summary>销毁指定 ID 的对象。</summary>
        public void DestroyById(int networkId, DestroyReason reason = DestroyReason.ServerDestroy)
        {
            var obj = NetworkObjectManager.Instance.FindById(networkId);
            if (obj != null) Destroy(obj, reason);
        }

        // ── 预生成池 ──────────────────────────────────────────────

        /// <summary>初始化对象池。</summary>
        public void InitializePool(string typeName, int initialCount, int maxCount)
        {
            _poolMaxSizes[typeName] = maxCount;
            var pool = _pools.GetOrAdd(typeName, _ => new ConcurrentBag<NetworkObject>());

            for (int i = 0; i < initialCount; i++)
            {
                var obj = CreateNewInstance(typeName);
                if (obj != null)
                {
                    obj.MarkSpawned();
                    pool.Add(obj);
                }
            }
        }

        /// <summary>将对象回收到池中。</summary>
        private void ReturnToPool(NetworkObject obj)
        {
            if (!_pools.TryGetValue(obj.TypeName, out var pool)) return;
            if (_poolMaxSizes.TryGetValue(obj.TypeName, out var max) && pool.Count >= max)
                return;

            // 重置对象状态
            obj.IsDestroyed = false;
            obj.IsSpawned = false;
            obj.IsDestroying = false;

            pool.Add(obj);
            Interlocked.Increment(ref _totalPooled);
        }

        private NetworkObject? CreateNewInstance(string typeName)
        {
            return _factories.TryGetValue(typeName, out var factory) ? factory() : null;
        }

        /// <summary>清空所有对象池。</summary>
        public void ClearPools()
        {
            foreach (var pool in _pools.Values)
                while (pool.TryTake(out _)) { }
            _pools.Clear();
            _poolMaxSizes.Clear();
        }
    }
}
