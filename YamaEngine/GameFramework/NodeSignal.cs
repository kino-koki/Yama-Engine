using System;
using System.Collections.Generic;

namespace YamaEngine.GameFramework
{
    // ════════════════════════════════════════════════════════════════════════
    // yama引擎 - 节点信号 (Signal)
    //
    //   Godot 风格的节点级观察者模式：信号由节点携带，外部连接处理器，
    //   触发时按连接顺序调用。与全局 EventBus（类型化广播）互补——
    //   Signal 是节点实例级、具名、支持断开与一次性连接。
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 节点信号：由 GameObject 携带的具名事件源。
    /// 用法：
    /// <code>
    /// var sig = go.AddSignal("health_changed");
    /// sig.Connect(v => Debug.Log(v));
    /// sig.Emit(42);
    /// </code>
    /// </summary>
    public sealed class NodeSignal
    {
        private readonly List<SignalConnection> _connections = new();
        private int _nextId = 1;

        /// <summary>信号名称。</summary>
        public string Name { get; }

        /// <summary>持有此信号的节点。</summary>
        public GameObjectReal Owner { get; }

        /// <summary>当前连接的处理器数量。</summary>
        public int ConnectionCount => _connections.Count;

        internal NodeSignal(string name, GameObjectReal owner)
        {
            Name = name;
            Owner = owner;
        }

        /// <summary>
        /// 连接一个处理器。返回连接 id（用于断开）。
        /// </summary>
        /// <param name="handler">处理器，接收信号参数对象。</param>
        /// <param name="oneShot">true 时处理器触发一次后自动断开。</param>
        public int Connect(Action<object?> handler, bool oneShot = false)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            int id = _nextId++;
            _connections.Add(new SignalConnection(id, handler, oneShot));
            return id;
        }

        /// <summary>断开指定 id 的连接。返回是否成功断开。</summary>
        public bool Disconnect(int connectionId)
        {
            for (int i = _connections.Count - 1; i >= 0; i--)
            {
                if (_connections[i].Id == connectionId)
                {
                    _connections.RemoveAt(i);
                    return true;
                }
            }
            return false;
        }

        /// <summary>断开所有连接。</summary>
        public void DisconnectAll() => _connections.Clear();

        /// <summary>
        /// 触发信号，按连接顺序调用所有处理器。
        /// oneShot 连接在本次触发后自动断开。
        /// </summary>
        public void Emit(object? arg = null)
        {
            // 复制一份，允许处理器在触发中修改连接列表
            var snapshot = new List<SignalConnection>(_connections);
            var toRemove = new List<int>();
            foreach (var conn in snapshot)
            {
                conn.Handler(arg);
                if (conn.OneShot)
                    toRemove.Add(conn.Id);
            }
            foreach (var id in toRemove)
                Disconnect(id);
        }

        private sealed record SignalConnection(int Id, Action<object?> Handler, bool OneShot);
    }

    /// <summary>
    /// GameObject 的信号扩展。信号在首次 Add/Get 时惰性创建并挂到节点上。
    /// </summary>
    public static class NodeSignalExtensions
    {
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<long,
            Dictionary<string, NodeSignal>> _signalMap = new();

        /// <summary>获取（或创建）节点上的具名信号。</summary>
        public static NodeSignal GetSignal(this GameObjectReal go, string name)
        {
            var dict = _signalMap.GetOrAdd(go.Id, _ => new Dictionary<string, NodeSignal>());
            lock (dict)
            {
                if (!dict.TryGetValue(name, out var sig))
                {
                    sig = new NodeSignal(name, go);
                    dict[name] = sig;
                }
                return sig;
            }
        }

        /// <summary>获取或创建节点信号（AddSignal 别名，语义更贴近 Godot）。</summary>
        public static NodeSignal AddSignal(this GameObjectReal go, string name)
            => go.GetSignal(name);

        /// <summary>节点上是否已有此信号（且已连接至少一个处理器）。</summary>
        public static bool HasSignal(this GameObjectReal go, string name)
        {
            if (!_signalMap.TryGetValue(go.Id, out var dict)) return false;
            lock (dict)
                return dict.TryGetValue(name, out var sig) && sig.ConnectionCount > 0;
        }

        /// <summary>清理节点上的全部信号（节点销毁时调用）。</summary>
        public static void ClearSignals(this GameObjectReal go)
            => _signalMap.TryRemove(go.Id, out _);
    }
}
