using System;
using System.Collections.Concurrent;
using System.Threading;

namespace YamaEngine.Scripting.Abi
{
    /// <summary>节点的不透明句柄。脚本只持有 Id，不持有原生指针（硬约束 2）。</summary>
    public readonly struct NodeRef : IEquatable<NodeRef>
    {
        public ulong Id { get; }
        public bool IsValid => Id != 0;

        public NodeRef(ulong id) { Id = id; }
        public bool Equals(NodeRef other) => Id == other.Id;
        public override bool Equals(object? obj) => obj is NodeRef other && Equals(other);
        public override int GetHashCode() => Id.GetHashCode();
        public override string ToString() => "NodeRef(" + Id.ToString() + ")";
    }

    /// <summary>句柄表：把脚本可见的 NodeRef 映射到引擎对象，支持释放与批量清空。</summary>
    public sealed class NodeHandleTable
    {
        private readonly ConcurrentDictionary<ulong, object> _targets = new ConcurrentDictionary<ulong, object>();
        private long _next;

        public NodeRef Register(object target)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            ulong id = unchecked((ulong)Interlocked.Increment(ref _next));
            if (id == 0) id = unchecked((ulong)Interlocked.Increment(ref _next));
            _targets[id] = target;
            return new NodeRef(id);
        }

        public bool TryResolve(NodeRef node, out object? target)
        {
            if (node.IsValid && _targets.TryGetValue(node.Id, out var found))
            {
                target = found;
                return true;
            }
            target = null;
            return false;
        }

        public bool Release(NodeRef node) => node.IsValid && _targets.TryRemove(node.Id, out _);
        public int Count => _targets.Count;
        public void Clear() => _targets.Clear();
    }
}
