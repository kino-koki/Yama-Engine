// ============================================================================
//  YamaEngine.Network.ReplicationGraphReal.cs
//  yama引擎 -- 复制图 / 兴趣管理 (Replication Graph + Interest Management)
// ----------------------------------------------------------------------------
//  闭合"兴趣管理/ReplicationGraph"缺口:
//    * ReplicationConnectionView : 连接观察者 (位置 + 视野半径)
//    * ReplicationInterestObject : 被复制对象 (位置 + 相关性半径)
//    * ReplicationGraphReal       : 每帧按 距离/所有者/常相关 计算每对象→每连接相关性
//      输出 per-object relevant-connection 集合, 供 NetworkObjectReplicator
//      按连接门控发送 (而非广播到所有连接)。
//  对齐 UE Replication Graph (ReplicationGraph) 的分层相关性:
//    - 距离: dist(viewer, object) <= viewRadius + objectRadius
//    - 所有者: object 的 AlwaysRelevantTo (如所属连接) 恒相关
//    - 常相关: AlwaysRelevant 恒发送给所有连接
// ============================================================================

using System;
using System.Collections.Generic;
using System.Net;
using System.Numerics;

namespace YamaEngine.Network
{
    /// <summary>
    /// 连接观察者视图。描述一个客户端连接的观察位置与视野半径。
    /// </summary>
    public sealed class ReplicationConnectionView
    {
        public IPEndPoint Endpoint { get; }
        public Vector3 Position { get; set; }
        public float ViewRadius { get; set; }

        public ReplicationConnectionView(IPEndPoint endpoint, Vector3 position, float viewRadius)
        {
            Endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
            Position = position;
            ViewRadius = viewRadius;
        }

        /// <summary>该连接是否"看见"给定对象位置。</summary>
        public bool Sees(Vector3 objectPosition, float objectRadius)
        {
            return Vector3.Distance(Position, objectPosition) <= ViewRadius + objectRadius;
        }
    }

    /// <summary>
    /// 被复制对象的兴趣状态。位置 + 相关性半径 + 常相关标记/集合。
    /// </summary>
    public sealed class ReplicationInterestObject
    {
        public ulong ObjectId { get; }
        public Vector3 Position { get; set; }
        public float RelevanceRadius { get; set; }

        /// <summary>恒相关: 发送给所有连接。</summary>
        public bool AlwaysRelevant { get; set; }

        /// <summary>恒相关连接集 (如所属连接/观战者)。</summary>
        public HashSet<IPEndPoint> AlwaysRelevantTo { get; } = new HashSet<IPEndPoint>();

        public ReplicationInterestObject(ulong objectId, Vector3 position, float relevanceRadius = 100f)
        {
            ObjectId = objectId;
            Position = position;
            RelevanceRadius = relevanceRadius;
        }
    }

    /// <summary>
    /// 复制图。每帧计算每个复制对象到每个连接的相关性 (兴趣集合)。
    /// 输出供 <see cref="NetworkObjectReplicator"/> 按连接门控发送。
    /// </summary>
    public sealed class ReplicationGraphReal
    {
        private readonly Dictionary<IPEndPoint, ReplicationConnectionView> _connections =
            new Dictionary<IPEndPoint, ReplicationConnectionView>();
        private readonly Dictionary<ulong, ReplicationInterestObject> _objects =
            new Dictionary<ulong, ReplicationInterestObject>();

        /// <summary>连接数。</summary>
        public int ConnectionCount => _connections.Count;
        /// <summary>对象数。</summary>
        public int ObjectCount => _objects.Count;

        // ── 连接管理 ──────────────────────────────────────────────

        /// <summary>添加/更新连接观察者。</summary>
        public void AddConnection(IPEndPoint endpoint, Vector3 position, float viewRadius)
        {
            if (endpoint == null) return;
            _connections[endpoint] = new ReplicationConnectionView(endpoint, position, viewRadius);
        }

        /// <summary>更新连接观察位置/视野。</summary>
        public void UpdateConnection(IPEndPoint endpoint, Vector3 position, float viewRadius)
        {
            if (endpoint == null) return;
            if (_connections.TryGetValue(endpoint, out var view))
            {
                view.Position = position;
                view.ViewRadius = viewRadius;
            }
            else
            {
                _connections[endpoint] = new ReplicationConnectionView(endpoint, position, viewRadius);
            }
        }

        /// <summary>移除连接。</summary>
        public void RemoveConnection(IPEndPoint endpoint)
        {
            if (endpoint == null) return;
            _connections.Remove(endpoint);
        }

        /// <summary>获取连接视图 (不存在返回 null)。</summary>
        public ReplicationConnectionView GetConnection(IPEndPoint endpoint)
        {
            return endpoint != null && _connections.TryGetValue(endpoint, out var v) ? v : null;
        }

        // ── 对象管理 ──────────────────────────────────────────────

        /// <summary>注册复制对象。</summary>
        public void RegisterObject(ulong objectId, Vector3 position, float relevanceRadius = 100f)
        {
            _objects[objectId] = new ReplicationInterestObject(objectId, position, relevanceRadius);
        }

        /// <summary>更新对象位置。</summary>
        public void UpdateObjectPosition(ulong objectId, Vector3 position)
        {
            if (_objects.TryGetValue(objectId, out var obj))
                obj.Position = position;
        }

        /// <summary>设置对象恒相关 (发送给所有连接)。</summary>
        public void SetAlwaysRelevant(ulong objectId, bool always)
        {
            if (_objects.TryGetValue(objectId, out var obj))
                obj.AlwaysRelevant = always;
        }

        /// <summary>添加对象恒相关连接 (如所属连接)。</summary>
        public void AddAlwaysRelevantTo(ulong objectId, IPEndPoint endpoint)
        {
            if (endpoint != null && _objects.TryGetValue(objectId, out var obj))
                obj.AlwaysRelevantTo.Add(endpoint);
        }

        /// <summary>移除对象的恒相关连接。</summary>
        public void RemoveAlwaysRelevantTo(ulong objectId, IPEndPoint endpoint)
        {
            if (endpoint != null && _objects.TryGetValue(objectId, out var obj))
                obj.AlwaysRelevantTo.Remove(endpoint);
        }

        /// <summary>移除复制对象。</summary>
        public void RemoveObject(ulong objectId)
        {
            _objects.Remove(objectId);
        }

        /// <summary>获取对象兴趣状态 (不存在返回 null)。</summary>
        public ReplicationInterestObject GetObject(ulong objectId)
        {
            return _objects.TryGetValue(objectId, out var o) ? o : null;
        }

        // ── 相关性计算 ────────────────────────────────────────────

        /// <summary>
        /// 判断对象是否对某连接相关 (距离 + 恒相关 + 常相关)。
        /// 幂等, 可直接逐次调用; 也可先 <see cref="ComputeRelevance"/> 批量预热。
        /// </summary>
        public bool IsRelevant(ulong objectId, IPEndPoint endpoint)
        {
            if (endpoint == null) return false;
            if (!_objects.TryGetValue(objectId, out var obj)) return false;

            // 常相关: 发送给所有连接
            if (obj.AlwaysRelevant) return true;
            // 恒相关连接集 (所有者/观战)
            if (obj.AlwaysRelevantTo.Contains(endpoint)) return true;

            // 距离相关性
            if (_connections.TryGetValue(endpoint, out var view))
                return view.Sees(obj.Position, obj.RelevanceRadius);

            return false;
        }

        /// <summary>
        /// 计算对象的所有相关连接集合。
        /// </summary>
        public HashSet<IPEndPoint> ComputeRelevantConnections(ulong objectId)
        {
            var result = new HashSet<IPEndPoint>();
            foreach (var ep in _connections.Keys)
            {
                if (IsRelevant(objectId, ep))
                    result.Add(ep);
            }
            return result;
        }

        /// <summary>批量计算并返回 (objectId → relevant connections)。</summary>
        public Dictionary<ulong, HashSet<IPEndPoint>> ComputeRelevance()
        {
            var map = new Dictionary<ulong, HashSet<IPEndPoint>>();
            foreach (var objId in _objects.Keys)
                map[objId] = ComputeRelevantConnections(objId);
            return map;
        }

        /// <summary>统计: 平均每对象相关连接数。</summary>
        public float GetAverageRelevantConnections()
        {
            if (_objects.Count == 0) return 0f;
            int total = 0;
            foreach (var objId in _objects.Keys)
                total += ComputeRelevantConnections(objId).Count;
            return (float)total / _objects.Count;
        }

        /// <summary>清空图。</summary>
        public void Clear()
        {
            _connections.Clear();
            _objects.Clear();
        }
    }
}
