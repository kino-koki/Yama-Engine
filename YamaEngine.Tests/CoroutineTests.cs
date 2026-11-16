// CoroutineTests.cs —— 协程系统深度验证
// 覆盖：StartCoroutine 推进/完成、WaitUntil 谓词、WaitForFrames 帧计数、嵌套协程、
//       暂停/恢复、StopCoroutine、StopAllCoroutines、异常终止 HasError。
// 全部确定性（用 WaitForFrames/WaitUntil，避免依赖真实 Stopwatch 时间）。

using System;
using System.Collections;
using YamaEngine.Core;
using YamaEngine.GameFramework;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests
{
    public static class CoroutineTests
    {
        // 每个用例前清空全局协程系统，避免相互污染
        private static void ResetCoroutines()
        {
            CoroutineSystem.StopAllCoroutines();
            CoroutineSystem.Update(); // 处理待删除
        }

        // ── 基本推进：每帧 MoveNext，序列推进完成后标记 IsCompleted ──
        public static void TestCoroutineBasicAdvanceAndComplete()
        {
            ResetCoroutines();
            int steps = 0;
            IEnumerator Run()
            {
                steps++;
                yield return null;
                steps++;
                yield return null;
                steps++;
            }

            var handle = CoroutineSystem.StartCoroutine(Run(), "basic");
            AssertTrue(handle.IsRunning, "Handle must be running right after Start.");
            AssertTrue(!handle.IsCompleted, "Handle must not be completed yet.");

            // 第 1 帧：执行到第一个 yield null
            CoroutineSystem.Update();
            AssertEqual(1, steps, "After frame 1, coroutine must have run 1 step.");

            // 第 2 帧
            CoroutineSystem.Update();
            AssertEqual(2, steps, "After frame 2, coroutine must have run 2 steps.");

            // 第 3 帧：执行最后一步，随后完成
            CoroutineSystem.Update();
            AssertEqual(3, steps, "After frame 3, coroutine must have run 3 steps.");
            AssertTrue(handle.IsCompleted, "Handle must be completed after final step.");
            AssertTrue(!handle.IsRunning, "Handle must not be running after completion.");
        }

        // ── WaitUntil：谓词为假时挂起，为真时推进 ──
        public static void TestCoroutineWaitUntilPredicate()
        {
            ResetCoroutines();
            bool flag = false;
            int reached = 0;
            IEnumerator Run()
            {
                yield return new WaitUntil(() => flag);
                reached = 1;
            }

            var handle = CoroutineSystem.StartCoroutine(Run(), "waituntil");

            CoroutineSystem.Update(); // 谓词为假 → 挂起
            AssertEqual(0, reached, "Must not advance while predicate is false.");

            CoroutineSystem.Update(); // 仍为假
            AssertEqual(0, reached, "Must still not advance on second false check.");

            flag = true;
            CoroutineSystem.Update(); // 谓词为真 → 推进到 reached=1 并完成
            AssertEqual(1, reached, "Must advance after predicate becomes true.");
            AssertTrue(handle.IsCompleted, "Coroutine must complete after WaitUntil passes.");
        }

        // ── WaitForFrames：基于帧计数等待指定帧数 ──
        public static void TestCoroutineWaitForFrames()
        {
            ResetCoroutines();
            TimeManager.Instance.Reset(); // 从帧 0 起算
            int frame = 0;
            IEnumerator Run()
            {
                yield return null; // 记录起始
                yield return new WaitForFrames(2);
                frame = 1; // 两帧后到达
            }

            CoroutineSystem.StartCoroutine(Run(), "waitframes");
            // 第 0 帧：推进 TimeManager（帧计数 → 1），再推进协程（执行到 yield null）
            TimeManager.Instance.Update();
            CoroutineSystem.Update();
            AssertEqual(0, frame, "Must not set frame before wait elapses.");

            // 逐帧推进直到 WaitForFrames(2) 到期（给足帧数，避免 off-by-one 假失败）
            for (int i = 0; i < 5 && frame == 0; i++)
            {
                TimeManager.Instance.Update();
                CoroutineSystem.Update();
            }
            AssertEqual(1, frame, "Must reach frame after WaitForFrames(2) elapses.");
        }

        // ── 嵌套协程：yield return 子 IEnumerator ──
        public static void TestCoroutineNestedSubRoutine()
        {
            ResetCoroutines();
            string order = "";
            IEnumerator Inner()
            {
                yield return null;
                order += "I";
            }
            IEnumerator Outer()
            {
                order += "O1";
                yield return Inner();
                order += "O2";
            }

            var handle = CoroutineSystem.StartCoroutine(Outer(), "nested");
            CoroutineSystem.Update(); // O1，进入子协程
            AssertTrue(order.Contains("O1"), "Outer must run first segment.");
            AssertTrue(!handle.IsCompleted, "Outer must wait for inner coroutine.");

            CoroutineSystem.Update(); // 子协程推进
            CoroutineSystem.Update(); // 子协程完成，Outer 继续 O2
            AssertTrue(order.Contains("I"), "Inner coroutine body must run.");
            AssertTrue(order.EndsWith("O2"), "Outer must resume after inner completes.");
            AssertTrue(handle.IsCompleted, "Outer must complete after inner finishes.");
        }

        // ── 暂停/恢复：Pause 停止推进，Resume 继续 ──
        public static void TestCoroutinePauseAndResume()
        {
            ResetCoroutines();
            int ticks = 0;
            IEnumerator Run()
            {
                while (true)
                {
                    ticks++;
                    yield return null;
                }
            }

            var handle = CoroutineSystem.StartCoroutine(Run(), "pausable");
            CoroutineSystem.Update();
            int afterFirst = ticks;
            AssertTrue(afterFirst > 0, "Coroutine must tick before pause.");

            handle.Pause();
            CoroutineSystem.Update();
            CoroutineSystem.Update();
            AssertEqual(afterFirst, ticks, "Paused coroutine must not advance.");

            handle.Resume();
            CoroutineSystem.Update();
            AssertTrue(ticks > afterFirst, "Resumed coroutine must advance again.");
        }

        // ── StopCoroutine：主动停止后不再推进 ──
        public static void TestCoroutineStopCoroutine()
        {
            ResetCoroutines();
            int ticks = 0;
            IEnumerator Run()
            {
                while (true)
                {
                    ticks++;
                    yield return null;
                }
            }

            var handle = CoroutineSystem.StartCoroutine(Run(), "stoppable");
            CoroutineSystem.Update();
            int beforeStop = ticks;

            handle.Stop();
            CoroutineSystem.Update();
            CoroutineSystem.Update();
            AssertEqual(beforeStop, ticks, "Stopped coroutine must not advance.");
            AssertTrue(handle.IsCompleted, "Stopped handle must be marked completed.");
        }

        // ── 异常终止：抛异常标记 HasError 并移除 ──
        public static void TestCoroutineExceptionTerminates()
        {
            ResetCoroutines();
            IEnumerator Run()
            {
                yield return null;
                throw new InvalidOperationException("boom");
            }

            var handle = CoroutineSystem.StartCoroutine(Run(), "throwing");
            CoroutineSystem.Update(); // 第一步正常
            AssertTrue(!handle.HasError, "No error before exception.");

            CoroutineSystem.Update(); // 第二步抛异常
            AssertTrue(handle.HasError, "Handle must flag HasError on exception.");
            AssertTrue(handle.IsCompleted, "Handle must be marked completed after error.");
            AssertTrue(handle.ErrorMessage != null && handle.ErrorMessage.Contains("boom"),
                "ErrorMessage must capture the exception message.");
            AssertEqual(0, CoroutineSystem.ActiveCoroutineCount,
                "Errored coroutine must be removed from active list.");
        }

        // ── StopAllCoroutines：清空全部 ──
        public static void TestCoroutineStopAll()
        {
            ResetCoroutines();
            IEnumerator Run()
            {
                while (true) yield return null;
            }
            CoroutineSystem.StartCoroutine(Run(), "a");
            CoroutineSystem.StartCoroutine(Run(), "b");
            CoroutineSystem.StartCoroutine(Run(), "c");
            CoroutineSystem.Update();
            AssertEqual(3, CoroutineSystem.ActiveCoroutineCount, "Three coroutines must be active.");

            CoroutineSystem.StopAllCoroutines();
            CoroutineSystem.Update();
            AssertEqual(0, CoroutineSystem.ActiveCoroutineCount, "StopAll must clear all coroutines.");
        }

        // ── yield return 信号：挂起直到信号触发 ──
        public static void TestCoroutineWaitForSignal()
        {
            ResetCoroutines();
            var go = new GameObjectReal("SignalNode");
            var sig = go.AddSignal("fired");

            bool reached = false;
            object? received = null;
            IEnumerator Run()
            {
                yield return sig; // 挂起直到信号触发
                reached = true;
            }

            CoroutineSystem.StartCoroutine(Run(), "waitforsignal");

            // 第 0 帧：MoveNext 遇到信号 yield，连接并挂起
            CoroutineSystem.Update();
            AssertTrue(!reached, "Coroutine must not advance before signal fires.");
            AssertTrue(go.HasSignal("fired"), "WaitForSignal must connect to the signal.");

            // 未触发信号 → 仍挂起
            CoroutineSystem.Update();
            AssertTrue(!reached, "Coroutine must stay suspended until signal fires.");

            // 触发信号 → 下一帧恢复
            sig.Emit(42);
            CoroutineSystem.Update();
            AssertTrue(reached, "Coroutine must advance after signal fires.");
        }

        // ── yield return 信号：协程停止时自动断开信号连接 ──
        public static void TestCoroutineWaitForSignalDisconnectOnStop()
        {
            ResetCoroutines();
            var go = new GameObjectReal("DiscNode");
            var sig = go.AddSignal("disc");

            IEnumerator Run()
            {
                yield return sig;
            }

            var handle = CoroutineSystem.StartCoroutine(Run(), "discsignal");
            CoroutineSystem.Update(); // 连接信号并挂起
            AssertTrue(go.HasSignal("disc"), "Signal must be connected while suspended.");

            handle.Stop();
            CoroutineSystem.Update(); // 清理
            AssertTrue(!go.HasSignal("disc"), "Signal connection must be released on stop.");
        }
    }
}
