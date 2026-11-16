using System;
using System.Collections.Generic;

namespace YamaEngine.GameFramework
{
    // ════════════════════════════════════════════════════════════════════════
    // yama引擎 - 延迟调用 (DeferredCall)
    //
    //   对应 Godot 的 call_deferred / set_deferred：把操作排队到当前帧末尾
    //   （或下一帧开始）执行，避免在信号回调/Update 中途修改场景树导致的
    //   "迭代中修改集合"崩溃。GameLoop 在所有 Update 之后调用 Flush()。
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 延迟调用队列。CallDeferred 排队，Flush 在帧末统一执行并清空。
    /// 线程不安全（游戏主线程专用）；Flush 中新排入的调用留到下次 Flush
    /// （避免无限递归：回调里再 CallDeferred 不会在同一次 Flush 内执行）。
    /// </summary>
    public static class DeferredCallQueue
    {
        private static readonly List<Action> _queue = new();
        private static readonly List<Action> _flushing = new();
        private static bool _isFlushing;

        /// <summary>当前排队的延迟调用数量。</summary>
        public static int PendingCount => _queue.Count;

        /// <summary>是否正在 Flush 中（回调内可检测）。</summary>
        public static bool IsFlushing => _isFlushing;

        /// <summary>
        /// 排队一个延迟调用。对应 Godot 的 call_deferred。
        /// 调用将在下一次 Flush() 时执行（帧末）。
        /// </summary>
        public static void CallDeferred(Action action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            _queue.Add(action);
        }

        /// <summary>
        /// 排队一个带参数的延迟调用（闭包捕获参数）。
        /// </summary>
        public static void CallDeferred<T>(Action<T> action, T arg)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            T captured = arg;
            _queue.Add(() => action(captured));
        }

        /// <summary>
        /// 执行并清空当前排队的所有延迟调用。
        /// Flush 中回调新排入的调用留到下次 Flush（不在本次执行，防无限递归）。
        /// 回调抛异常时：记录并继续执行剩余调用（一个坏回调不阻塞整帧）。
        /// </summary>
        public static void Flush()
        {
            if (_isFlushing) return; // 重入保护
            _isFlushing = true;
            try
            {
                // 交换到 flushing 列表，queue 清空后回调可继续排队
                _flushing.AddRange(_queue);
                _queue.Clear();
                foreach (var action in _flushing)
                {
                    try
                    {
                        action();
                    }
                    catch (Exception ex)
                    {
                        Diagnostics.Log.Error(
                            $"[DeferredCall] 延迟调用异常: {ex.Message}",
                            Diagnostics.LogCategory.Core);
                    }
                }
                _flushing.Clear();
            }
            finally
            {
                _isFlushing = false;
            }
        }

        /// <summary>清空队列（不执行）。用于场景销毁/测试重置。</summary>
        public static void Clear()
        {
            if (_isFlushing) return;
            _queue.Clear();
            _flushing.Clear();
        }
    }
}
