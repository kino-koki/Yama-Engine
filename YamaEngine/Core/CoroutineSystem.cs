// ============================================================================================
//  CoroutineSystem.cs
//  yama引擎 (YamaEngine) 协程系统
// ============================================================================================
//  提供类似 Unity Coroutine 的异步流程控制，支持：
//    - 等待时间 / 等待帧 / 等待条件
//    - 协程嵌套（StartCoroutine 内再启动协程）
//    - 协程暂停 / 停止
//    - 异常安全
// ============================================================================================

using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

namespace YamaEngine.Core
{
    // ====== 协程等待指令 ======
    public abstract class YieldInstruction
    {
        internal abstract bool IsDone();
    }

    /// <summary>等待指定秒数（受时间缩放影响）。</summary>
    public sealed class WaitForSeconds : YieldInstruction
    {
        private readonly float _seconds;
        private double _startTime;

        public WaitForSeconds(float seconds) => _seconds = seconds;

        internal override bool IsDone()
        {
            if (_startTime == 0) _startTime = TimeManager.Instance.GameTime;
            return TimeManager.Instance.GameTime - _startTime >= _seconds;
        }

        internal void Reset() => _startTime = 0;
    }

    /// <summary>等待指定帧数。</summary>
    public sealed class WaitForFrames : YieldInstruction
    {
        private readonly int _frames;
        private ulong _startFrame;

        public WaitForFrames(int frames) => _frames = frames;

        internal override bool IsDone()
        {
            if (_startFrame == 0) _startFrame = TimeManager.Instance.FrameCount;
            return TimeManager.Instance.FrameCount - _startFrame >= (ulong)_frames;
        }
    }

    /// <summary>等待条件表达式为真。</summary>
    public sealed class WaitUntil : YieldInstruction
    {
        private readonly Func<bool> _condition;
        public WaitUntil(Func<bool> condition) => _condition = condition;
        internal override bool IsDone() => _condition();
    }

    /// <summary>等待所有子指令完成。</summary>
    public sealed class WaitAll : YieldInstruction
    {
        private readonly YieldInstruction[] _instructions;
        public WaitAll(params YieldInstruction[] instructions) => _instructions = instructions;
        internal override bool IsDone()
        {
            foreach (var instr in _instructions)
                if (!instr.IsDone()) return false;
            return true;
        }
    }

    /// <summary>等待任意一个子指令完成。</summary>
    public sealed class WaitAny : YieldInstruction
    {
        private readonly YieldInstruction[] _instructions;
        public WaitAny(params YieldInstruction[] instructions) => _instructions = instructions;
        internal override bool IsDone()
        {
            foreach (var instr in _instructions)
                if (instr.IsDone()) return true;
            return false;
        }
    }

    /// <summary>等待下一帧的固定步进更新。</summary>
    public sealed class WaitForFixedUpdate : YieldInstruction
    {
        private bool _hasPassed;
        internal override bool IsDone()
        {
            if (_hasPassed) return true;
            _hasPassed = true;
            return false;
        }
        internal void Reset() => _hasPassed = false;
    }

    // ====== 协程句柄 ======
    public sealed class CoroutineHandle
    {
        internal int Id;
        internal IEnumerator Enumerator;
        internal bool IsPaused;
        internal string Name;

        /// <summary>协程是否仍在运行。</summary>
        public bool IsRunning { get; internal set; }

        /// <summary>协程是否已完成。</summary>
        public bool IsCompleted { get; internal set; }

        /// <summary>协程是否被异常终止。</summary>
        public bool HasError { get; internal set; }

        /// <summary>协程错误信息。</summary>
        public string? ErrorMessage { get; internal set; }

        /// <summary>主动停止此协程。</summary>
        public void Stop()
        {
            IsRunning = false;
            IsCompleted = true;
        }

        /// <summary>暂停协程。</summary>
        public void Pause() => IsPaused = true;

        /// <summary>恢复协程。</summary>
        public void Resume() => IsPaused = false;
    }

    // ====== 协程系统 ======
    /// <summary>
    /// 引擎协程系统。提供类似 Unity Coroutine 的异步流程控制。
    /// 用法：
    /// <code>
    /// var handle = CoroutineSystem.StartCoroutine(MyCoroutine());
    /// CoroutineSystem.StopCoroutine(handle);
    /// 
    /// IEnumerator MyCoroutine() {
    ///     yield return new WaitForSeconds(1.0f);
    ///     // 等待 1 秒后继续
    ///     yield return new WaitForFrames(2);
    ///     // 等待 2 帧后继续
    /// }
    /// </code>
    /// </summary>
    public static class CoroutineSystem
    {
        private static readonly List<CoroutineHandle> _coroutines = new();
        private static readonly List<CoroutineHandle> _pendingAdd = new();
        private static readonly List<CoroutineHandle> _pendingRemove = new();
        private static int _nextId = 1;
        private static readonly object _lock = new();

        public static int ActiveCoroutineCount { get; private set; }

        // ====== 启动协程 ======
        /// <summary>启动一个协程。</summary>
        public static CoroutineHandle StartCoroutine(IEnumerator enumerator, string? name = null)
        {
            if (enumerator == null)
                throw new ArgumentNullException(nameof(enumerator));

            var handle = new CoroutineHandle
            {
                Id = Interlocked.Increment(ref _nextId),
                Enumerator = enumerator,
                IsRunning = true,
                Name = name ?? $"Coroutine_{_nextId}"
            };

            lock (_lock)
                _pendingAdd.Add(handle);

            return handle;
        }

        /// <summary>启动一个协程（IEnumerable 重载，更方便使用 yield）。</summary>
        public static CoroutineHandle StartCoroutine(IEnumerable enumerable, string? name = null)
            => StartCoroutine(enumerable.GetEnumerator(), name);

        // ====== 停止协程 ======
        /// <summary>停止指定协程。</summary>
        public static void StopCoroutine(CoroutineHandle handle)
        {
            if (handle == null) return;
            handle.Stop();
            lock (_lock)
                _pendingRemove.Add(handle);
        }

        /// <summary>停止所有协程。</summary>
        public static void StopAllCoroutines()
        {
            lock (_lock)
            {
                foreach (var c in _coroutines) c.Stop();
                _coroutines.Clear();
                _pendingAdd.Clear();
                _pendingRemove.Clear();
            }
            ActiveCoroutineCount = 0;
        }

        // ====== 每帧更新 ======
        /// <summary>在每帧更新中调用，推动协程执行。</summary>
        public static void Update()
        {
            // 添加新协程
            if (_pendingAdd.Count > 0)
            {
                lock (_lock)
                {
                    _coroutines.AddRange(_pendingAdd);
                    _pendingAdd.Clear();
                }
            }

            // 执行协程
            for (int i = _coroutines.Count - 1; i >= 0; i--)
            {
                var handle = _coroutines[i];

                if (!handle.IsRunning || handle.IsCompleted)
                {
                    _coroutines.RemoveAt(i);
                    continue;
                }

                if (handle.IsPaused)
                    continue;

                try
                {
                    if (!MoveNext(handle))
                    {
                        // 协程已完成
                        handle.IsRunning = false;
                        handle.IsCompleted = true;
                        _coroutines.RemoveAt(i);
                    }
                }
                catch (Exception ex)
                {
                    // 异常终止
                    handle.IsRunning = false;
                    handle.IsCompleted = true;
                    handle.HasError = true;
                    handle.ErrorMessage = ex.Message;
                    Diagnostics.Log.Error(
                        $"[Coroutine] 协程 '{handle.Name}' 异常终止: {ex.Message}",
                        Diagnostics.LogCategory.Core);
                    _coroutines.RemoveAt(i);
                }
            }

            // 移除待删除
            if (_pendingRemove.Count > 0)
            {
                lock (_lock)
                    _pendingRemove.Clear();
            }

            ActiveCoroutineCount = _coroutines.Count;
        }

        // ====== 内部执行 ======
        private static bool MoveNext(CoroutineHandle handle)
        {
            if (!handle.Enumerator.MoveNext())
                return false;

            var current = handle.Enumerator.Current;

            if (current is YieldInstruction instruction)
            {
                // 如果指令已就绪，继续前进；否则阻塞
                if (instruction.IsDone())
                    return true;

                // 指令未就绪，递归处理等待队列
                // 在这里实际上是通过挂起来处理的：每次 Update 都会检查
                // 将当前指令替换为等待器
                if (current is WaitForSeconds wfs) { wfs.Reset(); return true; }
                if (current is WaitForFixedUpdate wfu) { wfu.Reset(); return true; }

                return true;
            }

            if (current is IEnumerator subEnumerator)
            {
                // 嵌套协程
                var subHandle = StartCoroutine(subEnumerator);
                // 将当前协程挂起直到子协程完成
                return WaitForSubCoroutine(handle, subHandle);
            }

            if (current is CoroutineHandle otherHandle)
            {
                // 等待另一个协程句柄
                return WaitForSubCoroutine(handle, otherHandle);
            }

            // Current 是 null 或其他 → 继续下一帧
            return true;
        }

        private static bool WaitForSubCoroutine(CoroutineHandle parent, CoroutineHandle child)
        {
            if (child.IsCompleted)
                return true; // 子协程完成，父协程继续

            // 子协程未完成，在下一次 Update 重新检查
            // 我们把子协程加入系统（已经做了）
            return false;
        }

        // ====== 从事件总线等待 ======
        /// <summary>创建等待事件的协程。</summary>
        public static IEnumerator WaitForEvent<T>(float timeout = 0) where T : EngineEvent
        {
            bool received = false;
            EventBus.SubscribeOnce<T>(_ => received = true);

            float elapsed = 0;
            while (!received)
            {
                if (timeout > 0)
                {
                    elapsed += TimeManager.Instance.DeltaTime;
                    if (elapsed >= timeout) yield break;
                }
                yield return null;
            }
        }

        // ====== 诊断 ======
        public static string GetReport()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"===== 协程系统报告 =====");
            sb.AppendLine($"活跃协程: {ActiveCoroutineCount}");
            sb.AppendLine($"待添加: {_pendingAdd.Count}");
            foreach (var c in _coroutines)
            {
                sb.AppendLine($"  #{c.Id} '{c.Name}' {(c.IsPaused ? "暂停" : "运行")}");
            }
            return sb.ToString();
        }
    }
}
