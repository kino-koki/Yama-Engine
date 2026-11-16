// TimerDepthTests.cs —— 追平 Godot 3 的 Timer 节点（wait_time/one_shot/autostart/time_left/time_scale）

using System;
using YamaEngine.GameFramework;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests
{
    public static class TimerDepthTests
    {
        private static void AssertEqual<T>(T got, T want, string msg)
        {
            if (!Equals(got, want)) throw new Exception($"{msg} (got {got}, want {want}).");
        }

        private static void AssertTrue(bool cond, string msg)
        {
            if (!cond) throw new Exception($"{msg}");
        }

        private static void AssertFalse(bool cond, string msg)
        {
            if (cond) throw new Exception($"{msg}");
        }

        private static void AssertNear(float got, float want, string msg, float tol = 1e-3f)
        {
            if (MathF.Abs(got - want) > tol) throw new Exception($"{msg} (got {got}, want {want}).");
        }

        // ── 默认状态 ──
        public static void TestDefaults()
        {
            var go = new GameObjectReal("T");
            var t = go.AddComponent<TimerNode>();
            AssertNear(1.0f, t.WaitTime, "Default WaitTime=1.");
            AssertFalse(t.OneShot, "Default OneShot=false.");
            AssertFalse(t.Autostart, "Default Autostart=false.");
            AssertNear(1f, t.TimeScale, "Default TimeScale=1.");
            AssertFalse(t.IsRunning, "Not running before Start.");
            AssertEqual(0f, t.TimeLeft, "TimeLeft=0 when stopped.");
        }

        // ── one_shot：触发一次后停止 ──
        public static void TestOneShot()
        {
            var go = new GameObjectReal("T");
            var t = go.AddComponent<TimerNode>();
            t.WaitTime = 1.0f; t.OneShot = true;
            int fires = 0;
            go.GetSignal("timeout").Connect(_ => fires++);
            t.Start();
            t.Update(0.5f);
            AssertEqual(0, fires, "No fire at half.");
            AssertTrue(t.IsRunning, "Running at half.");
            t.Update(0.6f);
            AssertEqual(1, fires, "Fired once.");
            AssertFalse(t.IsRunning, "Stopped after fire.");
            t.Update(2.0f);
            AssertEqual(1, fires, "No refire after stop.");
        }

        // ── 循环：每个 WaitTime 触发一次 ──
        public static void TestLoop()
        {
            var go = new GameObjectReal("T");
            var t = go.AddComponent<TimerNode>();
            t.WaitTime = 1.0f; t.OneShot = false;
            int fires = 0;
            go.GetSignal("timeout").Connect(_ => fires++);
            t.Start();
            for (int i = 0; i < 30; i++) t.Update(0.1f); // 3s
            AssertEqual(3, fires, "3 fires over 3s.");
            AssertTrue(t.IsRunning, "Still running.");
        }

        // ── Stop 中止不触发 ──
        public static void TestStop()
        {
            var go = new GameObjectReal("T");
            var t = go.AddComponent<TimerNode>();
            t.WaitTime = 1.0f;
            int fires = 0;
            go.GetSignal("timeout").Connect(_ => fires++);
            t.Start();
            t.Update(0.9f);
            t.Stop();
            t.Update(5.0f);
            AssertEqual(0, fires, "Stopped no fire.");
            AssertFalse(t.IsRunning, "Not running after stop.");
        }

        // ── 运行中改 WaitTime 重置本次倒计时 ──
        public static void TestWaitTimeResetWhileRunning()
        {
            var go = new GameObjectReal("T");
            var t = go.AddComponent<TimerNode>();
            t.WaitTime = 2.0f; t.OneShot = true;
            t.Start();
            t.Update(1.0f); // 走一半
            t.WaitTime = 0.5f; // 重置到新时长
            AssertNear(0.5f, t.TimeLeft, "Reset to new WaitTime.");
            int fires = 0;
            go.GetSignal("timeout").Connect(_ => fires++);
            t.Update(0.6f);
            AssertEqual(1, fires, "Fired at new shorter WaitTime.");
        }

        // ── autostart：首次 Update 自动启动 ──
        public static void TestAutostart()
        {
            var go = new GameObjectReal("T");
            var t = go.AddComponent<TimerNode>();
            t.WaitTime = 1.0f; t.OneShot = true; t.Autostart = true;
            AssertFalse(t.IsRunning, "Not running until first Update.");
            t.Update(0.0001f); // 首帧触发 autostart
            AssertTrue(t.IsRunning, "Autostart ran on first Update.");
            // 走满 WaitTime 触发
            int fires = 0;
            go.GetSignal("timeout").Connect(_ => fires++);
            t.Update(1.0f);
            AssertEqual(1, fires, "Autostarted timer fired.");
        }

        // ── autostart=false 不自动启动 ──
        public static void TestNoAutostart()
        {
            var go = new GameObjectReal("T");
            var t = go.AddComponent<TimerNode>();
            t.WaitTime = 1.0f;
            t.Update(5.0f);
            AssertFalse(t.IsRunning, "Never started without Start/Autostart.");
        }

        // ── time_scale=2 倒计时双速 ──
        public static void TestTimeScaleDouble()
        {
            var go = new GameObjectReal("T");
            var t = go.AddComponent<TimerNode>();
            t.WaitTime = 1.0f; t.OneShot = true; t.TimeScale = 2f;
            int fires = 0;
            go.GetSignal("timeout").Connect(_ => fires++);
            t.Start();
            t.Update(0.5f); // 0.5s * 2 = 1.0s 倒计时 → 触发
            AssertEqual(1, fires, "TimeScale=2 fires in half real time.");
            AssertFalse(t.IsRunning, "Stopped.");
        }

        // ── time_scale=0 暂停倒计时 ──
        public static void TestTimeScaleZero()
        {
            var go = new GameObjectReal("T");
            var t = go.AddComponent<TimerNode>();
            t.WaitTime = 1.0f; t.TimeScale = 0f;
            int fires = 0;
            go.GetSignal("timeout").Connect(_ => fires++);
            t.Start();
            t.Update(10.0f);
            AssertEqual(0, fires, "TimeScale=0 never advances.");
            AssertTrue(t.IsRunning, "Still running (paused).");
            AssertNear(1.0f, t.TimeLeft, "TimeLeft unchanged.");
        }

        // ── time_scale=0.5 半速 ──
        public static void TestTimeScaleHalf()
        {
            var go = new GameObjectReal("T");
            var t = go.AddComponent<TimerNode>();
            t.WaitTime = 1.0f; t.OneShot = true; t.TimeScale = 0.5f;
            int fires = 0;
            go.GetSignal("timeout").Connect(_ => fires++);
            t.Start();
            t.Update(1.0f); // 1.0 * 0.5 = 0.5 倒计时 → 未到
            AssertEqual(0, fires, "Half speed not yet fired.");
            AssertTrue(t.IsRunning, "Still running.");
            AssertNear(0.5f, t.TimeLeft, "Half time left.");
            t.Update(1.0f); // 再 0.5 → 累计 1.0 → 触发
            AssertEqual(1, fires, "Fired after full scaled time.");
        }

        // ── 循环 timer 溢出时间不漂移 ──
        public static void TestLoopNoDrift()
        {
            var go = new GameObjectReal("T");
            var t = go.AddComponent<TimerNode>();
            t.WaitTime = 1.0f;
            t.Start();
            t.Update(1.25f); // 溢出 0.25 保留
            AssertNear(0.75f, t.TimeLeft, "Overflow preserved (0.75 left).");
        }

        // ── TimeLeft 走满归零（停止时为 0）──
        public static void TestTimeLeftZeroWhenStopped()
        {
            var go = new GameObjectReal("T");
            var t = go.AddComponent<TimerNode>();
            t.WaitTime = 0.5f;
            t.Start();
            AssertNear(0.5f, t.TimeLeft, "Full at start.");
            t.Stop();
            AssertEqual(0f, t.TimeLeft, "Zero when stopped.");
        }

        // ── TimeoutCount 累计 ──
        public static void TestTimeoutCount()
        {
            var go = new GameObjectReal("T");
            var t = go.AddComponent<TimerNode>();
            t.WaitTime = 0.2f;
            t.Start();
            for (int i = 0; i < 30; i++) t.Update(0.1f); // 3s / 0.2 = 15 次
            AssertEqual(15, t.TimeoutCount, "15 timeouts.");
        }
    }
}
