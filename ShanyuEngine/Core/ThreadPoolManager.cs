using System.Collections.Concurrent;
using System.Diagnostics;

namespace ShanyuEngine.Core;

/// <summary>
/// Managed thread pool for parallel task execution in the engine.
/// Separates game tasks from .NET ThreadPool to avoid starvation of
/// runtime tasks and provides priority queuing, progress tracking,
/// and task dependency support.
/// </summary>
public sealed class ThreadPoolManager : IDisposable
{
    private readonly Thread[] _workers;
    private readonly ConcurrentQueue<TaskItem> _highPriority = new();
    private readonly ConcurrentQueue<TaskItem> _normalPriority = new();
    private readonly ConcurrentQueue<TaskItem> _lowPriority = new();
    private readonly AutoResetEvent _signal = new(false);
    private volatile bool _disposed;
    private int _totalScheduled;
    private int _totalCompleted;

    /// <summary>Number of worker threads.</summary>
    public int WorkerCount => _workers.Length;

    /// <summary>Total tasks scheduled since creation.</summary>
    public int TotalScheduled => _totalScheduled;

    /// <summary>Total tasks completed since creation.</summary>
    public int TotalCompleted => _totalCompleted;

    /// <summary>Approximate number of pending tasks.</summary>
    public int PendingCount =>
        _highPriority.Count + _normalPriority.Count + _lowPriority.Count;

    /// <summary>Create a thread pool with optimal thread count.</summary>
    public ThreadPoolManager(int? threadCount = null)
    {
        int count = threadCount ?? Math.Max(2, Environment.ProcessorCount - 1);
        _workers = new Thread[count];

        for (int i = 0; i < count; i++)
        {
            _workers[i] = new Thread(WorkerLoop)
            {
                Name = $"ShanyuWorker-{i}",
                IsBackground = true,
                Priority = ThreadPriority.BelowNormal,
            };
            _workers[i].Start();
        }
    }

    #region Scheduling

    /// <summary>Schedule a task with normal priority.</summary>
    public TaskHandle Schedule(Func<object?> action, string? name = null, int priority = 0)
    {
        var handle = new TaskHandle(name ?? $"Task-{Interlocked.Increment(ref _totalScheduled)}");
        var item = new TaskItem(handle, action, priority);

        if (priority >= 1)
            _highPriority.Enqueue(item);
        else if (priority <= -1)
            _lowPriority.Enqueue(item);
        else
            _normalPriority.Enqueue(item);

        Interlocked.Increment(ref _totalScheduled);
        _signal.Set();
        return handle;
    }

    /// <summary>Schedule a task and return a Task that completes when it finishes.</summary>
    public System.Threading.Tasks.Task<object?> ScheduleAsync(Func<object?> action, string? name = null, int priority = 0)
    {
        var tcs = new System.Threading.Tasks.TaskCompletionSource<object?>();
        Schedule(() =>
        {
            try
            {
                var result = action();
                tcs.TrySetResult(result);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
            return null;
        }, name, priority);
        return tcs.Task;
    }

    /// <summary>Schedule multiple independent tasks in parallel.</summary>
    public TaskHandle[] ScheduleBatch(IEnumerable<Func<object?>> actions, string? batchName = null)
    {
        var handles = new List<TaskHandle>();
        int idx = 0;
        foreach (var action in actions)
        {
            handles.Add(Schedule(action, $"{batchName ?? "Batch"}-{idx++}"));
        }
        return handles.ToArray();
    }

    /// <summary>Wait for all given handles to complete.</summary>
    public void WaitAll(params TaskHandle[] handles)
    {
        foreach (var h in handles)
            h.Wait();
    }

    /// <summary>Run an action on the main thread (placeholder for dispatch pattern).</summary>
    public event Action<Action>? OnMainThreadRequest;

    public void RunOnMainThread(Action action)
    {
        if (OnMainThreadRequest != null)
            OnMainThreadRequest(action);
        else
            action(); // Fallback: run inline
    }

    #endregion

    #region Worker Loop

    private void WorkerLoop()
    {
        while (!_disposed)
        {
            if (!TryDequeue(out var item))
            {
                _signal.WaitOne(100);
                continue;
            }

            try
            {
                item.Handle.Result = item.Action();
            }
            catch (Exception ex)
            {
                item.Handle.Exception = ex;
                Diagnostics.Logger.Error($"[ThreadPool] {item.Handle.Name} failed: {ex.Message}");
            }
            finally
            {
                item.Handle.IsComplete = true;
                item.Handle._signal.Set();
                Interlocked.Increment(ref _totalCompleted);
            }
        }
    }

    private bool TryDequeue(out TaskItem item)
    {
        if (_highPriority.TryDequeue(out item!)) return true;
        if (_normalPriority.TryDequeue(out item!)) return true;
        if (_lowPriority.TryDequeue(out item!)) return true;
        return false;
    }

    #endregion

    #region Stats

    public ThreadPoolStats GetStats() => new()
    {
        WorkerCount = _workers.Length,
        Scheduled = _totalScheduled,
        Completed = _totalCompleted,
        Pending = PendingCount,
    };

    public struct ThreadPoolStats
    {
        public int WorkerCount;
        public int Scheduled;
        public int Completed;
        public int Pending;
        public override string ToString() =>
            $"Workers: {WorkerCount}, Completed: {Completed}/{Scheduled}, Pending: {Pending}";
    }

    #endregion

    public void Dispose()
    {
        _disposed = true;
        _signal.Set();
        foreach (var w in _workers)
            if (w.IsAlive) w.Join(500);
        _signal.Dispose();
    }

    #region Internal Types

    private sealed record TaskItem(TaskHandle Handle, Func<object?> Action, int Priority);

    private struct TaskItemRecord
    {
        public TaskHandle Handle;
        public Func<object?> Action;
        public int Priority;
    }

    #endregion
}

/// <summary>Tracks a scheduled task's completion state.</summary>
public sealed class TaskHandle
{
    internal readonly AutoResetEvent _signal = new(false);

    public string Name { get; }
    public bool IsComplete { get; internal set; }
    public object? Result { get; internal set; }
    public Exception? Exception { get; internal set; }

    internal TaskHandle(string name) { Name = name; }

    /// <summary>Block until this task completes.</summary>
    public void Wait()
    {
        if (!IsComplete)
            _signal.WaitOne();
    }

    /// <summary>Wait with timeout in milliseconds.</summary>
    public bool Wait(int timeoutMs)
    {
        if (IsComplete) return true;
        return _signal.WaitOne(timeoutMs);
    }

    /// <summary>Get result, rethrowing if exception occurred.</summary>
    public T GetResult<T>()
    {
        Wait();
        if (Exception != null) throw Exception;
        return (T)(Result ?? default(T)!);
    }
}
