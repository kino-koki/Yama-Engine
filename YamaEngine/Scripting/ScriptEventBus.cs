using System;
using System.Collections.Generic;

namespace YamaEngine.Scripting
{
    /// <summary>
    /// 事件处理器委托（泛型事件负载）。
    /// </summary>
    public delegate void ScriptEventHandler<in T>(T eventData);

    /// <summary>
    /// 轻量事件总线：解耦游戏逻辑（脚本、ECS、系统）的事件发布/订阅。
    /// 线程不安全（应在主线程使用）。
    /// </summary>
    public sealed class ScriptEventBus
    {
        private readonly Dictionary<Type, object> _handlers = new();

        /// <summary>订阅事件。</summary>
        public IDisposable Subscribe<T>(ScriptEventHandler<T> handler)
        {
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            if (!_handlers.TryGetValue(typeof(T), out var raw) || raw is not List<ScriptEventHandler<T>> list)
            {
                list = new List<ScriptEventHandler<T>>();
                _handlers[typeof(T)] = list;
            }
            list.Add(handler);
            return new Subscription(this, typeof(T), handler);
        }

        /// <summary>取消订阅。</summary>
        public void Unsubscribe<T>(ScriptEventHandler<T> handler)
        {
            if (!_handlers.TryGetValue(typeof(T), out var raw) || raw is not List<ScriptEventHandler<T>> list)
                return;
            list.Remove(handler);
        }

        /// <summary>发布事件（所有订阅者依次调用）。</summary>
        public void Publish<T>(T eventData)
        {
            if (_handlers.TryGetValue(typeof(T), out var raw) && raw is List<ScriptEventHandler<T>> list)
            {
                // 复制一份，防止订阅者在回调中修改列表导致枚举异常。
                var snapshot = new List<ScriptEventHandler<T>>(list);
                foreach (var h in snapshot)
                    h(eventData);
            }
        }

        /// <summary>是否存在某事件的订阅者。</summary>
        public bool HasSubscribers<T>() =>
            _handlers.TryGetValue(typeof(T), out var raw) &&
            raw is List<ScriptEventHandler<T>> list && list.Count > 0;

        /// <summary>清空全部订阅。</summary>
        public void Clear() => _handlers.Clear();

        private void Remove(Type type, object handler)
        {
            if (_handlers.TryGetValue(type, out var raw) && raw is System.Collections.IList list)
                list.Remove(handler);
        }

        private sealed class Subscription : IDisposable
        {
            private readonly ScriptEventBus _bus;
            private readonly Type _type;
            private readonly object _handler;
            private bool _disposed;

            public Subscription(ScriptEventBus bus, Type type, object handler)
            {
                _bus = bus;
                _type = type;
                _handler = handler;
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                _bus.Remove(_type, _handler);
            }
        }
    }

    // =========================================================================
    //  通用游戏事件负载
    // =========================================================================

    /// <summary>游戏开始事件。</summary>
    public readonly struct GameStartedEvent
    {
        public readonly string GameName;
        public readonly double StartTimestamp;
        public GameStartedEvent(string gameName, double startTimestamp)
        {
            GameName = gameName;
            StartTimestamp = startTimestamp;
        }
    }

    /// <summary>暂停/恢复事件。</summary>
    public readonly struct GamePauseEvent
    {
        public readonly bool Paused;
        public GamePauseEvent(bool paused) => Paused = paused;
    }

    /// <summary>场景加载事件。</summary>
    public readonly struct SceneLoadedEvent
    {
        public readonly string SceneName;
        public SceneLoadedEvent(string sceneName) => SceneName = sceneName;
    }

    /// <summary>对象生成/销毁事件。</summary>
    public readonly struct ObjectSpawnedEvent
    {
        public readonly string Name;
        public readonly object? Instance;
        public ObjectSpawnedEvent(string name, object? instance)
        {
            Name = name;
            Instance = instance;
        }
    }

    public readonly struct ObjectDestroyedEvent
    {
        public readonly string Name;
        public ObjectDestroyedEvent(string name) => Name = name;
    }
}
