// ============================================================================================
//  EventBus.cs
//  yama引擎 (YamaEngine) 事件总线 / 消息系统
// ============================================================================================
//  解耦引擎各模块之间的通信，支持：
//    - 类型安全的事件发布/订阅
//    - 同步/异步/延迟投递
//    - 弱引用/强引用订阅两种模式
//    - 事件过滤器和优先级
//    - 性能计数
// ============================================================================================

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

namespace YamaEngine.Core
{
    // ====== 事件基类 ======
    /// <summary>
    /// 所有引擎事件必须继承自此基类。
    /// </summary>
    public abstract record EngineEvent
    {
        /// <summary>事件创建时间戳（引擎 GameTime）。</summary>
        public double Timestamp { get; internal set; }

        /// <summary>是否已被标记为已处理（某些事件可被拦截）。</summary>
        public bool Handled { get; set; }

        /// <summary>事件通道名称（用于过滤）。</summary>
        public virtual string Channel => "default";
    }

    // ====== 内置引擎事件 ======
    public record EngineStartedEvent : EngineEvent { public override string Channel => "engine"; }
    public record EngineShutdownEvent : EngineEvent { public override string Channel => "engine"; }
    public record EngineTickEvent(float DeltaTime) : EngineEvent { public override string Channel => "engine"; }
    public record SceneLoadedEvent(string SceneName) : EngineEvent { public override string Channel => "scene"; }
    public record SceneUnloadedEvent(string SceneName) : EngineEvent { public override string Channel => "scene"; }

    // ====== 过滤器 ======
    public delegate bool EventFilter(EngineEvent evt);

    // ====== 订阅令牌 ======
    /// <summary>订阅的句柄。用于取消订阅。</summary>
    public readonly struct EventSubscription : IEquatable<EventSubscription>
    {
        internal readonly ulong Id;
        internal readonly bool IsValid;

        public EventSubscription(ulong id) { Id = id; IsValid = true; }

        public bool Equals(EventSubscription other) => Id == other.Id;
        public override bool Equals(object? obj) => obj is EventSubscription s && Equals(s);
        public override int GetHashCode() => Id.GetHashCode();
        public static bool operator ==(EventSubscription a, EventSubscription b) => a.Id == b.Id;
        public static bool operator !=(EventSubscription a, EventSubscription b) => !a.Equals(b);
    }

    // ====== 内部订阅记录 ======
    internal class EventHandlerEntry
    {
        public ulong Id;
        public Type EventType;
        public WeakReference? WeakHandler;
        public Delegate? StrongHandler;
        public EventFilter? Filter;
        public int Priority;
        public string Channel;
        public string? Tag;
    }

    // ====== 投递模式 ======
    public enum DispatchMode
    {
        /// <summary>立即同步执行所有处理器。</summary>
        Immediate,
        /// <summary>排队到下一帧处理。</summary>
        Deferred,
        /// <summary>异步投递，不阻塞发布者。</summary>
        Async
    }

    // ====== 延迟事件 ======
    internal class DeferredEvent
    {
        public EngineEvent Event;
        public double DelayUntil;
        public DispatchMode Mode;
    }

    // ====== 事件总线 ======
    /// <summary>
    /// 全局事件总线。引擎所有模块通过它进行解耦通信。
    /// 用法：
    /// <code>
    /// EventBus.Publish(new SceneLoadedEvent("Main"));
    /// var sub = EventBus.Subscribe<SceneLoadedEvent>(evt => { ... });
    /// sub.Unsubscribe();
    /// </code>
    /// </summary>
    public static class EventBus
    {
        // ====== 处理器存储 ======
        private static readonly List<EventHandlerEntry> _handlers = new();
        private static readonly ConcurrentQueue<DeferredEvent> _deferredQueue = new();
        private static ulong _nextId = 1;
        private static readonly object _lock = new();

        // ====== 事件统计 ======
        private static long _totalEventsPublished;
        private static long _totalHandlersExecuted;
        private static long _totalErrors;

        public static long TotalEventsPublished => Interlocked.Read(ref _totalEventsPublished);
        public static long TotalHandlersExecuted => Interlocked.Read(ref _totalHandlersExecuted);
        public static long TotalErrors => Interlocked.Read(ref _totalErrors);
        public static int ActiveSubscriptions { get { lock (_lock) return _handlers.Count; } }

        // ====== 发布事件 ======
        /// <summary>发布事件。立即同步执行所有匹配的处理器。</summary>
        public static void Publish<T>(T evt, DispatchMode mode = DispatchMode.Immediate) where T : EngineEvent
        {
            if (evt == null) return;

            evt.Timestamp = TimeManager.Instance.GameTime;
            Interlocked.Increment(ref _totalEventsPublished);

            if (mode == DispatchMode.Deferred)
            {
                _deferredQueue.Enqueue(new DeferredEvent { Event = evt, DelayUntil = 0, Mode = mode });
                return;
            }

            if (mode == DispatchMode.Async)
            {
                ThreadPool.QueueUserWorkItem(_ => DispatchImmediate(evt));
                return;
            }

            DispatchImmediate(evt);
        }

        /// <summary>延迟发布事件（指定延迟秒数）。</summary>
        public static void PublishDelayed<T>(T evt, float delaySeconds) where T : EngineEvent
        {
            _deferredQueue.Enqueue(new DeferredEvent
            {
                Event = evt,
                DelayUntil = TimeManager.Instance.GameTime + delaySeconds,
                Mode = DispatchMode.Immediate
            });
        }

        // ====== 订阅 ======
        /// <summary>使用强引用订阅事件。</summary>
        public static EventSubscription Subscribe<T>(Action<T> handler, EventFilter? filter = null,
            int priority = 0, string? tag = null, string? channel = null) where T : EngineEvent
        {
            var entry = new EventHandlerEntry
            {
                Id = Interlocked.Increment(ref _nextId),
                EventType = typeof(T),
                StrongHandler = handler,
                Filter = filter,
                Priority = priority,
                Channel = channel ?? "default",
                Tag = tag
            };

            lock (_lock) _handlers.Add(entry);

            return new EventSubscription(entry.Id);
        }

        /// <summary>使用弱引用订阅事件。当订阅者被 GC 后自动移除。</summary>
        public static EventSubscription SubscribeWeak<T>(object subscriber, Action<T> handler,
            EventFilter? filter = null, int priority = 0, string? tag = null) where T : EngineEvent
        {
            var entry = new EventHandlerEntry
            {
                Id = Interlocked.Increment(ref _nextId),
                EventType = typeof(T),
                WeakHandler = new WeakReference(subscriber),
                StrongHandler = handler,
                Filter = filter,
                Priority = priority,
                Channel = "default",
                Tag = tag
            };

            lock (_lock) _handlers.Add(entry);

            return new EventSubscription(entry.Id);
        }

        /// <summary>订阅所有事件类型（观察者）。</summary>
        public static EventSubscription SubscribeAll(Action<EngineEvent> handler, int priority = -10)
        {
            var entry = new EventHandlerEntry
            {
                Id = Interlocked.Increment(ref _nextId),
                EventType = typeof(EngineEvent),
                StrongHandler = handler,
                Priority = priority,
                Channel = "*"
            };

            lock (_lock) _handlers.Add(entry);

            return new EventSubscription(entry.Id);
        }

        // ====== 取消订阅 ======
        public static void Unsubscribe(EventSubscription subscription)
        {
            if (!subscription.IsValid) return;

            lock (_lock)
                _handlers.RemoveAll(h => h.Id == subscription.Id);
        }

        // ====== 按标签取消 ======
        public static void UnsubscribeByTag(string tag)
        {
            lock (_lock)
                _handlers.RemoveAll(h => h.Tag == tag);
        }

        // ====== 清除所有订阅 ======
        public static void Clear()
        {
            lock (_lock) _handlers.Clear();
        }

        // ====== 内部投递 ======
        private static void DispatchImmediate(EngineEvent evt)
        {
            var evtType = evt.GetType();
            var channel = evt.Channel;

            List<EventHandlerEntry> snapshot;

            lock (_lock)
            {
                // 清理死弱引用
                _handlers.RemoveAll(h =>
                    h.WeakHandler != null && !h.WeakHandler.IsAlive);

                snapshot = new List<EventHandlerEntry>(_handlers);
            }

            // 按优先级排序并执行
            snapshot.Sort((a, b) => b.Priority.CompareTo(a.Priority));

            foreach (var entry in snapshot)
            {
                // 类型检查
                if (entry.EventType != typeof(EngineEvent) && !entry.EventType.IsAssignableFrom(evtType))
                    continue;

                // 通道过滤
                if (entry.Channel != "*" && entry.Channel != channel)
                    continue;

                // 自定义过滤
                if (entry.Filter != null && !entry.Filter(evt))
                    continue;

                // 弱引用检查
                if (entry.WeakHandler != null && !entry.WeakHandler.IsAlive)
                    continue;

                try
                {
                    entry.StrongHandler?.DynamicInvoke(evt);
                    Interlocked.Increment(ref _totalHandlersExecuted);

                    if (evt.Handled)
                        break; // 事件已被标记为已处理，停止传播
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref _totalErrors);
                    Diagnostics.Log.Error(
                        $"[EventBus] 处理事件 {evtType.Name} 时出错: {ex.Message}",
                        Diagnostics.LogCategory.Core);
                }
            }
        }

        // ====== 延迟队列处理（每帧调用） ======
        /// <summary>处理延迟投递的事件队列。在每帧末尾调用。</summary>
        public static void ProcessDeferredQueue()
        {
            var processed = new List<DeferredEvent>();
            double now = TimeManager.Instance.GameTime;

            while (_deferredQueue.TryDequeue(out var deferred))
            {
                if (deferred.DelayUntil > 0 && now < deferred.DelayUntil)
                {
                    // 还未到时间，重新入队
                    processed.Add(deferred);
                    continue;
                }

                DispatchImmediate(deferred.Event);
            }

            // 将未到时间的重新入队
            foreach (var d in processed)
                _deferredQueue.Enqueue(d);
        }

        // ====== 单次一次性事件 ======
        /// <summary>订阅只触发一次的事件。</summary>
        public static void SubscribeOnce<T>(Action<T> handler) where T : EngineEvent
        {
            EventSubscription? sub = null;
            sub = Subscribe<T>(evt =>
            {
                handler(evt);
                if (sub.HasValue)
                    Unsubscribe(sub.Value);
            });
        }

        // ====== 等待事件（异步/协程友好） ======
        /// <summary>返回一个 Task，在事件发布时完成。</summary>
        public static Task<T> WaitFor<T>(float timeout = 0) where T : EngineEvent
        {
            var tcs = new TaskCompletionSource<T>();

            EventSubscription? sub = null;
            sub = Subscribe<T>(evt =>
            {
                tcs.TrySetResult(evt);
                if (sub.HasValue) Unsubscribe(sub.Value);
            });

            if (timeout > 0)
            {
                ThreadPool.QueueUserWorkItem(async _ =>
                {
                    await Task.Delay((int)(timeout * 1000));
                    tcs.TrySetCanceled();
                    if (sub.HasValue) Unsubscribe(sub.Value);
                });
            }

            return tcs.Task;
        }

        // ====== 诊断 ======
        /// <summary>获取当前订阅信息。</summary>
        public static string GetSubscriptionReport()
        {
            lock (_lock)
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"===== 事件总线报告 =====");
                sb.AppendLine($"共发布: {TotalEventsPublished}");
                sb.AppendLine($"处理器执行: {TotalHandlersExecuted}");
                sb.AppendLine($"活跃订阅: {_handlers.Count}");
                sb.AppendLine($"延迟队列: {_deferredQueue.Count}");
                sb.AppendLine();

                var grouped = new Dictionary<string, int>();
                foreach (var h in _handlers)
                {
                    string key = h.EventType.Name;
                    grouped.TryGetValue(key, out int count);
                    grouped[key] = count + 1;
                }

                foreach (var kvp in grouped)
                    sb.AppendLine($"  {kvp.Key}: {kvp.Value} 订阅");

                return sb.ToString();
            }
        }
    }
}
