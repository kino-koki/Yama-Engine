using System;
using System.Collections.Generic;
using System.Threading;

namespace YamaEngine.Scripting.Abi
{
    public readonly struct SignalSubscription : IEquatable<SignalSubscription>
    {
        public ulong Id { get; }
        public string Signal { get; }
        public SignalSubscription(ulong id, string signal) { Id = id; Signal = signal ?? string.Empty; }
        public bool Equals(SignalSubscription other) => Id == other.Id && string.Equals(Signal, other.Signal, StringComparison.Ordinal);
        public override bool Equals(object? obj) => obj is SignalSubscription other && Equals(other);
        public override int GetHashCode() => Id.GetHashCode();
    }

    /// <summary>信号总线：订阅/取消/触发，单个处理器抛错不影响其它处理器。</summary>
    public sealed class SignalBus
    {
        private sealed class Entry
        {
            public ulong Id;
            public Action<IReadOnlyList<ScriptVariant>> Handler = null!;
            public bool OneShot;
        }

        private readonly object _gate = new object();
        private readonly Dictionary<string, List<Entry>> _handlers = new Dictionary<string, List<Entry>>(StringComparer.Ordinal);
        private readonly List<Exception> _errors = new List<Exception>();
        private long _next;

        public SignalSubscription Connect(string signal, Action<IReadOnlyList<ScriptVariant>> handler, bool oneShot = false)
        {
            if (string.IsNullOrEmpty(signal)) throw new ArgumentException("Signal name must be non-empty.", nameof(signal));
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            ulong id = unchecked((ulong)Interlocked.Increment(ref _next));
            var entry = new Entry { Id = id, Handler = handler, OneShot = oneShot };
            lock (_gate)
            {
                if (!_handlers.TryGetValue(signal, out var list))
                {
                    list = new List<Entry>();
                    _handlers[signal] = list;
                }
                list.Add(entry);
            }
            return new SignalSubscription(id, signal);
        }

        public bool Disconnect(SignalSubscription subscription)
        {
            if (subscription.Id == 0) return false;
            lock (_gate)
            {
                if (!_handlers.TryGetValue(subscription.Signal, out var list)) return false;
                int removed = list.RemoveAll(e => e.Id == subscription.Id);
                if (list.Count == 0) _handlers.Remove(subscription.Signal);
                return removed > 0;
            }
        }

        public int Emit(string signal, params ScriptVariant[] args)
        {
            if (string.IsNullOrEmpty(signal)) return 0;
            IReadOnlyList<ScriptVariant> payload = args ?? Array.Empty<ScriptVariant>();
            Entry[] snapshot;
            lock (_gate)
            {
                snapshot = _handlers.TryGetValue(signal, out var list) ? list.ToArray() : Array.Empty<Entry>();
            }
            int delivered = 0;
            foreach (var entry in snapshot)
            {
                try { entry.Handler(payload); delivered++; }
                catch (Exception ex) { lock (_gate) { _errors.Add(ex); } }
                if (entry.OneShot) Disconnect(new SignalSubscription(entry.Id, signal));
            }
            return delivered;
        }

        public IReadOnlyList<Exception> Errors
        {
            get { lock (_gate) { return _errors.ToArray(); } }
        }

        public int SubscriberCount(string signal)
        {
            lock (_gate) { return _handlers.TryGetValue(signal, out var list) ? list.Count : 0; }
        }
    }
}
