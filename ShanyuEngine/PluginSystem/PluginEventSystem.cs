using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

namespace ShanyuEngine.PluginSystem
{
    // ════════════════════════════════════════════════════════════════════════
    // 山雨引擎 - 插件事件系统 (PluginEventSystem)
    //
    // 跨插件事件总线与通信:
    //   * 事件注册 (插件注册事件处理器 / 事件过滤器 / 优先级)
    //   * 事件投递 (同步 / 异步 / 广播 / 定向 / 取消 / 拦截)
    //   * 事件类型 (引擎生命周期 / 游戏事件 / 渲染 / 输入 / 资产 / 自定义)
    //   * 事件取消 (Cancel(): 阻止后续处理器 / 阻止引擎默认行为)
    //   * 事件优先级 (Low/Normal/High/Highest/Monitor/Monitor_Last)
    //   * 事件日志 (事件历史 / 处理器耗时 / 异常捕获)
    // ════════════════════════════════════════════════════════════════════════

    #region 事件优先级

    /// <summary>事件处理器优先级。</summary>
    public enum EventPriority
    {
        /// <summary>最低优先级</summary>
        Low = 0,
        /// <summary>默认优先级</summary>
        Normal = 100,
        /// <summary>高优先级</summary>
        High = 200,
        /// <summary>最高优先级</summary>
        Highest = 300,
        /// <summary>监控优先级 (不修改事件, 仅观察)</summary>
        Monitor = 400,
        /// <summary>最终监控 (所有处理器之后)</summary>
        Monitor_Last = 500
    }

    #endregion

    #region 事件基类

    /// <summary>可取消事件基类。</summary>
    public abstract class PluginEvent
    {
        private bool _cancelled;

        /// <summary>事件是否已被取消。</summary>
        public bool IsCancelled => _cancelled;

        /// <summary>取消事件 (阻止后续处理器和引擎默认行为)。</summary>
        public virtual void Cancel() => _cancelled = true;

        /// <summary>恢复事件。</summary>
        public void Uncancel() => _cancelled = false;

        /// <summary>事件名称。</summary>
        public virtual string EventName => GetType().Name;

        /// <summary>事件时间戳。</summary>
        public DateTime Timestamp { get; } = DateTime.Now;

        /// <summary>事件来源插件ID。</summary>
        public string SourcePluginId { get; set; }

        public override string ToString() => $"{EventName} @{Timestamp:HH:mm:ss.fff}{(_cancelled ? " [Cancelled]" : "")}";
    }

    #endregion

    #region 内置事件类型

    #region 引擎生命周期事件

    /// <summary>引擎初始化事件。</summary>
    public sealed class EngineInitEvent : PluginEvent
    {
        public SemanticVersion EngineVersion { get; set; }
        public string CommandLine { get; set; }
    }

    /// <summary>引擎关闭事件。</summary>
    public sealed class EngineShutdownEvent : PluginEvent
    {
        public TimeSpan Uptime { get; set; }
    }

    /// <summary>世界加载事件。</summary>
    public sealed class WorldLoadEvent : PluginEvent
    {
        public string WorldName { get; set; }
        public string WorldPath { get; set; }
    }

    /// <summary>世界卸载事件。</summary>
    public sealed class WorldUnloadEvent : PluginEvent
    {
        public string WorldName { get; set; }
    }

    #endregion

    #region 游戏事件

    /// <summary>游戏开始事件。</summary>
    public sealed class GameStartEvent : PluginEvent
    {
        public string GameMode { get; set; }
        public string MapName { get; set; }
    }

    /// <summary>游戏结束事件。</summary>
    public sealed class GameEndEvent : PluginEvent
    {
        public string Result { get; set; }
        public TimeSpan Duration { get; set; }
    }

    /// <summary>玩家加入事件。</summary>
    public sealed class PlayerJoinEvent : PluginEvent
    {
        public string PlayerName { get; set; }
        public ulong PlayerId { get; set; }
    }

    /// <summary>玩家离开事件。</summary>
    public sealed class PlayerLeaveEvent : PluginEvent
    {
        public string PlayerName { get; set; }
        public ulong PlayerId { get; set; }
        public string Reason { get; set; }
    }

    /// <summary>Actor 生成事件。</summary>
    public sealed class ActorSpawnEvent : PluginEvent
    {
        public string ActorType { get; set; }
        public Vector3 Position { get; set; }
        public Vector3 Rotation { get; set; }
        public ulong ActorId { get; set; }
    }

    /// <summary>Actor 销毁事件。</summary>
    public sealed class ActorDestroyEvent : PluginEvent
    {
        public ulong ActorId { get; set; }
        public string ActorType { get; set; }
    }

    #endregion

    #region 渲染事件

    /// <summary>渲染前事件。</summary>
    public sealed class PreRenderEvent : PluginEvent
    {
        public float DeltaTime { get; set; }
        public Matrix4x4 ViewMatrix { get; set; }
        public Matrix4x4 ProjectionMatrix { get; set; }
    }

    /// <summary>渲染后事件。</summary>
    public sealed class PostRenderEvent : PluginEvent
    {
        public float DeltaTime { get; set; }
        public float FrameTime { get; set; }
    }

    /// <summary>UI 渲染事件。</summary>
    public sealed class RenderUIEvent : PluginEvent
    {
        public float DeltaTime { get; set; }
    }

    /// <summary>调试渲染事件。</summary>
    public sealed class RenderDebugEvent : PluginEvent
    {
        public float DeltaTime { get; set; }
    }

    #endregion

    #region 输入事件

    /// <summary>按键事件。</summary>
    public sealed class KeyPressEvent : PluginEvent
    {
        public int KeyCode { get; set; }
        public bool IsRepeat { get; set; }
        public float DeltaTime { get; set; }
    }

    /// <summary>鼠标点击事件。</summary>
    public sealed class MouseClickEvent : PluginEvent
    {
        public int Button { get; set; }
        public Vector2 Position { get; set; }
        public int ClickCount { get; set; }
    }

    /// <summary>动作触发事件。</summary>
    public sealed class ActionTriggeredEvent : PluginEvent
    {
        public string ActionName { get; set; }
        public float Value { get; set; }
    }

    #endregion

    #region 资产事件

    /// <summary>资产加载事件。</summary>
    public sealed class AssetLoadedEvent : PluginEvent
    {
        public string AssetPath { get; set; }
        public string AssetType { get; set; }
        public string PluginId { get; set; }
    }

    /// <summary>资产卸载事件。</summary>
    public sealed class AssetUnloadedEvent : PluginEvent
    {
        public string AssetPath { get; set; }
    }

    /// <summary>资产重载事件。</summary>
    public sealed class AssetReloadedEvent : PluginEvent
    {
        public string AssetPath { get; set; }
        public string OldVersion { get; set; }
        public string NewVersion { get; set; }
    }

    #endregion

    #region 自定义事件

    /// <summary>通用自定义事件。</summary>
    public sealed class CustomEvent : PluginEvent
    {
        public new string EventName { get; set; }
        public Dictionary<string, object> Data { get; } = new Dictionary<string, object>();

        public T GetData<T>(string key)
        {
            if (Data.TryGetValue(key, out var val))
            {
                if (val is T typed) return typed;
                try { return (T)Convert.ChangeType(val, typeof(T)); } catch { }
            }
            return default;
        }

        public void SetData<T>(string key, T value) => Data[key] = value;
    }

    /// <summary>消息事件 (插件间消息传递)。</summary>
    public sealed class MessageEvent : PluginEvent
    {
        public string TargetPluginId { get; set; }
        public string Message { get; set; }
        public object Payload { get; set; }
    }

    #endregion

    #endregion

    #region 事件处理器注册

    /// <summary>事件处理器注册信息。</summary>
    internal sealed class EventHandlerRegistration
    {
        public Delegate Handler { get; set; }
        public string PluginId { get; set; }
        public EventPriority Priority { get; set; }
        public Func<PluginEvent, bool> Filter { get; set; }
        public bool IsAsync { get; set; }
        public DateTime RegisteredTime { get; } = DateTime.Now;
        public long InvokeCount { get; set; }
        public TimeSpan TotalInvokeTime { get; set; }
        public Exception LastException { get; set; }
    }

    #endregion

    #region 事件历史记录

    /// <summary>事件历史记录。</summary>
    public sealed class EventHistoryEntry
    {
        public string EventName { get; set; }
        public DateTime Timestamp { get; set; }
        public string SourcePluginId { get; set; }
        public bool WasCancelled { get; set; }
        public int HandlerCount { get; set; }
        public TimeSpan TotalHandleTime { get; set; }
        public List<string> HandlerPluginIds { get; } = new List<string>();
        public List<Exception> Exceptions { get; } = new List<Exception>();
    }

    #endregion

    #region 插件事件总线

    /// <summary>跨插件事件总线。</summary>
    public sealed class PluginEventBus
    {
        private static readonly Lazy<PluginEventBus> _instance =
            new Lazy<PluginEventBus>(() => new PluginEventBus());
        public static PluginEventBus Instance => _instance.Value;

        // 事件处理器: EventType -> List of registrations (sorted by priority desc)
        private readonly ConcurrentDictionary<Type, List<EventHandlerRegistration>> _handlers =
            new ConcurrentDictionary<Type, List<EventHandlerRegistration>>();
        private readonly object _handlerLock = new object();

        // 事件历史
        private readonly ConcurrentQueue<EventHistoryEntry> _history = new ConcurrentQueue<EventHistoryEntry>();
        public int MaxHistorySize { get; set; } = 1000;

        // 异步事件队列
        private readonly ConcurrentQueue<PluginEvent> _asyncQueue = new ConcurrentQueue<PluginEvent>();
        private readonly CancellationTokenSource _asyncCts = new CancellationTokenSource();
        private Task _asyncProcessor;

        // 日志
        private readonly PluginLogger _logger = new PluginLogger("EventBus");

        // 配置
        public bool EnableHistory { get; set; } = true;
        public bool EnableProfiling { get; set; } = true;
        public bool CaptureExceptions { get; set; } = true;
        public bool IsAsyncProcessing { get; private set; }

        // 事件
        public event Action<PluginEvent> OnEventPublished;
        public event Action<PluginEvent, Exception> OnHandlerException;

        private PluginEventBus() { }

        #region 注册

        /// <summary>注册事件处理器。</summary>
        public void Subscribe<T>(Action<T> handler, string pluginId = null, EventPriority priority = EventPriority.Normal)
            where T : PluginEvent
        {
            Subscribe<T>(handler, pluginId, priority, null);
        }

        /// <summary>注册带过滤器的事件处理器。</summary>
        public void Subscribe<T>(Action<T> handler, string pluginId, EventPriority priority, Func<T, bool> filter)
            where T : PluginEvent
        {
            var type = typeof(T);
            var registration = new EventHandlerRegistration
            {
                Handler = handler,
                PluginId = pluginId,
                Priority = priority,
                Filter = filter != null ? e => filter((T)e) : null,
                IsAsync = false
            };

            lock (_handlerLock)
            {
                if (!_handlers.TryGetValue(type, out var list))
                {
                    list = new List<EventHandlerRegistration>();
                    _handlers[type] = list;
                }
                list.Add(registration);
                list.Sort((a, b) => b.Priority.CompareTo(a.Priority)); // 高优先级在前
            }

            _logger.Debug($"注册事件处理器: {type.Name} [{pluginId}] P:{priority}");
        }

        /// <summary>注册异步事件处理器。</summary>
        public void SubscribeAsync<T>(Func<T, Task> handler, string pluginId = null, EventPriority priority = EventPriority.Normal)
            where T : PluginEvent
        {
            var type = typeof(T);
            var registration = new EventHandlerRegistration
            {
                Handler = handler,
                PluginId = pluginId,
                Priority = priority,
                IsAsync = true
            };

            lock (_handlerLock)
            {
                if (!_handlers.TryGetValue(type, out var list))
                {
                    list = new List<EventHandlerRegistration>();
                    _handlers[type] = list;
                }
                list.Add(registration);
                list.Sort((a, b) => b.Priority.CompareTo(a.Priority));
            }

            _logger.Debug($"注册异步事件处理器: {type.Name} [{pluginId}] P:{priority}");
        }

        /// <summary>注销插件的所有事件处理器。</summary>
        public void UnsubscribeAll(string pluginId)
        {
            lock (_handlerLock)
            {
                foreach (var kv in _handlers)
                {
                    kv.Value.RemoveAll(r => r.PluginId == pluginId);
                }
            }
            _logger.Debug($"注销插件所有事件处理器: {pluginId}");
        }

        /// <summary>注销特定事件的所有处理器。</summary>
        public void Unsubscribe<T>(string pluginId = null) where T : PluginEvent
        {
            var type = typeof(T);
            lock (_handlerLock)
            {
                if (_handlers.TryGetValue(type, out var list))
                {
                    if (pluginId == null)
                        list.Clear();
                    else
                        list.RemoveAll(r => r.PluginId == pluginId);
                }
            }
        }

        /// <summary>注销单个处理器。</summary>
        public void Unsubscribe<T>(Action<T> handler) where T : PluginEvent
        {
            var type = typeof(T);
            lock (_handlerLock)
            {
                if (_handlers.TryGetValue(type, out var list))
                {
                    list.RemoveAll(r => r.Handler == (Delegate)handler);
                }
            }
        }

        #endregion

        #region 投递

        /// <summary>同步发布事件。</summary>
        public void Publish<T>(T evt) where T : PluginEvent
        {
            if (evt == null) return;

            var type = typeof(T);
            var history = new EventHistoryEntry
            {
                EventName = evt.EventName,
                Timestamp = evt.Timestamp,
                SourcePluginId = evt.SourcePluginId
            };

            OnEventPublished?.Invoke(evt);

            List<EventHandlerRegistration> handlers;
            lock (_handlerLock)
            {
                if (!_handlers.TryGetValue(type, out handlers) || handlers.Count == 0)
                {
                    if (EnableHistory) RecordHistory(history);
                    return;
                }
                handlers = handlers.ToList(); // 快照
            }

            var sw = EnableProfiling ? Stopwatch.StartNew() : null;

            foreach (var reg in handlers)
            {
                if (evt.IsCancelled && reg.Priority < EventPriority.Monitor)
                    continue; // 被取消后只允许 Monitor 级别处理器执行

                // 过滤器
                if (reg.Filter != null && !reg.Filter(evt))
                    continue;

                history.HandlerCount++;
                if (!string.IsNullOrEmpty(reg.PluginId))
                    history.HandlerPluginIds.Add(reg.PluginId);

                try
                {
                    var handlerSw = EnableProfiling ? Stopwatch.StartNew() : null;

                    if (reg.IsAsync)
                    {
                        // 异步处理器同步等待 (简化处理)
                        var asyncHandler = (Func<T, Task>)reg.Handler;
                        asyncHandler(evt).GetAwaiter().GetResult();
                    }
                    else
                    {
                        var syncHandler = (Action<T>)reg.Handler;
                        syncHandler(evt);
                    }

                    reg.InvokeCount++;

                    if (handlerSw != null)
                    {
                        handlerSw.Stop();
                        reg.TotalInvokeTime += handlerSw.Elapsed;
                    }
                }
                catch (Exception ex)
                {
                    reg.LastException = ex;
                    history.Exceptions.Add(ex);
                    if (CaptureExceptions)
                    {
                        _logger.Error($"事件处理器异常: {type.Name} [{reg.PluginId}]", ex);
                        OnHandlerException?.Invoke(evt, ex);
                    }
                }
            }

            history.WasCancelled = evt.IsCancelled;
            if (sw != null)
            {
                sw.Stop();
                history.TotalHandleTime = sw.Elapsed;
            }

            if (EnableHistory) RecordHistory(history);
        }

        /// <summary>异步发布事件。</summary>
        public void PublishAsync<T>(T evt) where T : PluginEvent
        {
            _asyncQueue.Enqueue(evt);
        }

        /// <summary>广播事件 (所有处理器)。</summary>
        public void Broadcast<T>(T evt) where T : PluginEvent
        {
            Publish(evt);
        }

        /// <summary>定向发布事件 (仅指定插件)。</summary>
        public void Send<T>(T evt, string targetPluginId) where T : PluginEvent
        {
            if (evt == null) return;
            var type = typeof(T);

            List<EventHandlerRegistration> handlers;
            lock (_handlerLock)
            {
                if (!_handlers.TryGetValue(type, out handlers) || handlers.Count == 0)
                    return;
                handlers = handlers.Where(h => h.PluginId == targetPluginId).ToList();
            }

            foreach (var reg in handlers)
            {
                if (evt.IsCancelled) continue;
                if (reg.Filter != null && !reg.Filter(evt)) continue;

                try
                {
                    if (reg.IsAsync)
                        ((Func<T, Task>)reg.Handler)(evt).GetAwaiter().GetResult();
                    else
                        ((Action<T>)reg.Handler)(evt);
                    reg.InvokeCount++;
                }
                catch (Exception ex)
                {
                    _logger.Error($"定向事件处理器异常: {type.Name} [{reg.PluginId}]", ex);
                    OnHandlerException?.Invoke(evt, ex);
                }
            }
        }

        /// <summary>发送消息给指定插件。</summary>
        public void SendMessage(string targetPluginId, string message, object payload = null, string sourcePluginId = null)
        {
            var msg = new MessageEvent
            {
                TargetPluginId = targetPluginId,
                Message = message,
                Payload = payload,
                SourcePluginId = sourcePluginId
            };
            Send(msg, targetPluginId);
        }

        #endregion

        #region 异步处理

        /// <summary>启动异步事件处理。</summary>
        public void StartAsyncProcessing()
        {
            if (IsAsyncProcessing) return;
            IsAsyncProcessing = true;

            _asyncProcessor = Task.Run(async () =>
            {
                while (!_asyncCts.Token.IsCancellationRequested)
                {
                    while (_asyncQueue.TryDequeue(out var evt))
                    {
                        try
                        {
                            Publish((dynamic)evt);
                        }
                        catch (Exception ex)
                        {
                            _logger.Error("异步事件处理异常", ex);
                        }
                    }
                    await Task.Delay(16, _asyncCts.Token); // ~60fps
                }
            }, _asyncCts.Token);
        }

        /// <summary>停止异步事件处理。</summary>
        public void StopAsyncProcessing()
        {
            IsAsyncProcessing = false;
            _asyncCts.Cancel();
            _asyncProcessor?.Wait(1000);
        }

        #endregion

        #region 历史

        private void RecordHistory(EventHistoryEntry entry)
        {
            _history.Enqueue(entry);
            while (_history.Count > MaxHistorySize)
                _history.TryDequeue(out _);
        }

        /// <summary>获取事件历史。</summary>
        public IReadOnlyList<EventHistoryEntry> GetHistory(int count = 100)
        {
            return _history.Reverse().Take(count).ToList();
        }

        /// <summary>获取事件统计。</summary>
        public Dictionary<string, (int Count, TimeSpan TotalTime)> GetEventStats()
        {
            var stats = new Dictionary<string, (int, TimeSpan)>();
            foreach (var entry in _history)
            {
                if (!stats.ContainsKey(entry.EventName))
                    stats[entry.EventName] = (0, TimeSpan.Zero);
                var s = stats[entry.EventName];
                stats[entry.EventName] = (s.Count + 1, s.TotalTime + entry.TotalHandleTime);
            }
            return stats;
        }

        /// <summary>获取处理器统计。</summary>
        public Dictionary<string, (long InvokeCount, TimeSpan TotalTime, Exception LastException)> GetHandlerStats()
        {
            var stats = new Dictionary<string, (long, TimeSpan, Exception)>();
            lock (_handlerLock)
            {
                foreach (var kv in _handlers)
                {
                    foreach (var reg in kv.Value)
                    {
                        string key = $"{kv.Value.GetType().Name} [{reg.PluginId}]";
                        stats[key] = (reg.InvokeCount, reg.TotalInvokeTime, reg.LastException);
                    }
                }
            }
            return stats;
        }

        /// <summary>清除历史。</summary>
        public void ClearHistory()
        {
            while (_history.TryDequeue(out _)) { }
        }

        #endregion

        #region 查询

        /// <summary>获取已注册的事件类型。</summary>
        public List<Type> GetRegisteredEventTypes()
        {
            return _handlers.Keys.ToList();
        }

        /// <summary>获取事件处理器数量。</summary>
        public int GetHandlerCount<T>() where T : PluginEvent
        {
            return _handlers.TryGetValue(typeof(T), out var list) ? list.Count : 0;
        }

        /// <summary>获取插件注册的事件处理器数量。</summary>
        public int GetPluginHandlerCount(string pluginId)
        {
            int count = 0;
            lock (_handlerLock)
            {
                foreach (var kv in _handlers)
                    count += kv.Value.Count(r => r.PluginId == pluginId);
            }
            return count;
        }

        #endregion
    }

    #endregion
}
