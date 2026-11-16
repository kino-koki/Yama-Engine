// TweenLifecycleTests.cs —— 追平 Godot 3 的 Tween 生命期（delay/interval/callback/finished/parallel/chain/kill）

using System;
using System.Collections.Generic;
using System.Numerics;
using YamaEngine.UI;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests
{
    public static class TweenLifecycleTests
    {
        private const float Tol = 1e-3f;

        private static void AssertEqual<T>(T got, T want, string msg)
        {
            if (!Equals(got, want)) throw new Exception($"{msg} (got {got}, want {want}).");
        }

        private static void AssertNear(float got, float want, string msg, float tol = Tol)
        {
            if (MathF.Abs(got - want) > tol) throw new Exception($"{msg} (got {got}, want {want}±{tol}).");
        }

        private static void AssertTrue(bool cond, string msg)
        {
            if (!cond) throw new Exception(msg);
        }

        private static void AssertFalse(bool cond, string msg)
        {
            if (cond) throw new Exception(msg);
        }

        // ── 延迟：Start 后先等 delay 再推进属性 ──
        public static void TestDelay()
        {
            var node = new Node2D("N") { Position = new Vector2(0f, 0f) };
            var tween = new Tween();
            tween.TweenPropertyStep(node, TweenProperty.PositionX, 100f, 1f);
            tween.SetDelay(0.5f);
            tween.Start();

            // 0.3s < 0.5s delay → 未开始推进，位置不变
            tween.Update(0.3f);
            AssertNear(0f, node.Position.X, "During delay, no property advance.");

            // 再 0.3s（累计 0.6s > 0.5s delay），延迟结束，开始推进 0.1s 的属性
            tween.Update(0.3f);
            AssertTrue(node.Position.X > 0f, "After delay, property starts advancing.");
        }

        // ── delay=0 立即开始 ──
        public static void TestDelayZero()
        {
            var node = new Node2D("N") { Position = new Vector2(0f, 0f) };
            var tween = new Tween();
            tween.TweenPropertyStep(node, TweenProperty.PositionX, 100f, 0.1f);
            tween.SetDelay(0f);
            tween.Start();
            tween.Update(0.05f);
            AssertTrue(node.Position.X > 0f, "Delay=0 → immediately advancing.");
        }

        // ── Interval：纯等待步骤，期间属性不变 ──
        public static void TestInterval()
        {
            var node = new Node2D("N") { Position = new Vector2(0f, 0f) };
            var tween = new Tween();
            tween.TweenPropertyStep(node, TweenProperty.PositionX, 100f, 0.1f); // 0.1s
            tween.TweenInterval(0.3f);                                          // 0.3s 等待
            tween.Start();

            tween.Update(0.1f); // 属性到位
            AssertNear(100f, node.Position.X, "Property done after 0.1s.");

            tween.Update(0.15f); // 等待进行中（0.15/0.3）
            AssertNear(100f, node.Position.X, "Interval: X stays at 100.");

            tween.Update(0.15f); // 等待完成 → Done
            AssertEqual(TweenState.Done, tween.State, "After interval, tween Done.");
        }

        // ── Callback：回调在步骤执行时触发一次 ──
        public static void TestCallback()
        {
            int callCount = 0;
            var node = new Node2D("N") { Position = new Vector2(0f, 0f) };
            var tween = new Tween();
            tween.TweenPropertyStep(node, TweenProperty.PositionX, 50f, 0.1f);
            tween.TweenCallback(() => callCount++);
            tween.Start();

            AssertEqual(0, callCount, "Callback not fired before Start+Update.");

            tween.Update(0.1f); // 属性步骤完成，同帧前进到 callback 步骤
            AssertEqual(1, callCount, "Callback fired once after property done.");
        }

        // ── Callback 顺序：多个 callback 按顺序触发 ──
        public static void TestCallbackOrder()
        {
            var order = new List<int>();
            var node = new Node2D("N");
            var tween = new Tween();
            tween.TweenCallback(() => order.Add(1));
            tween.TweenCallback(() => order.Add(2));
            tween.TweenCallback(() => order.Add(3));
            tween.Start();

            tween.Update(1f);
            AssertEqual(3, order.Count, "All callbacks fired.");
            AssertEqual(1, order[0], "Callback order [0].");
            AssertEqual(2, order[1], "Callback order [1].");
            AssertEqual(3, order[2], "Callback order [2].");
        }

        // ── Finished 事件在完成后触发 ──
        public static void TestFinishedEvent()
        {
            bool finishedFired = false;
            var node = new Node2D("N") { Position = new Vector2(0f, 0f) };
            var tween = new Tween();
            tween.TweenPropertyStep(node, TweenProperty.PositionX, 100f, 0.2f);
            tween.Finished += () => finishedFired = true;
            tween.Start();

            AssertFalse(finishedFired, "Not fired before Done.");

            tween.Update(0.1f);
            AssertFalse(finishedFired, "Not fired mid-progress.");

            tween.Update(0.1f); // 到达终点
            AssertTrue(finishedFired, "Finished event fires when Done.");
        }

        // ── Finished 循环模式只在最终结束时触发 ──
        public static void TestFinishedOnlyOnFinalEnd()
        {
            int fireCount = 0;
            var node = new Node2D("N");
            var tween = new Tween();
            tween.TweenPropertyStep(node, TweenProperty.PositionX, 100f, 0.1f);
            tween.SetLoops(3);
            tween.Finished += () => fireCount++;
            tween.Start();

            for (int i = 0; i < 20; i++)
                tween.Update(0.05f); // 走完 3 轮

            AssertEqual(1, fireCount, "Finished fires only once at final end.");
        }

        // ── SetParallel：后续步骤与前一步并行 ──
        public static void TestSetParallel()
        {
            var node = new Node2D("N") { Position = new Vector2(0f, 0f) };
            var tween = new Tween();
            tween.TweenPropertyStep(node, TweenProperty.PositionX, 100f, 0.2f); // anchor
            tween.SetParallel(true);
            tween.TweenPropertyStep(node, TweenProperty.PositionY, 50f, 0.2f);  // parallel with anchor
            tween.Start();

            // 同时推进，0.2s 后两者都到终点
            tween.Update(0.2f);
            AssertNear(100f, node.Position.X, "X parallel advance.");
            AssertNear(50f, node.Position.Y, "Y parallel advance.");
        }

        // ── Chain 关闭并行，恢复顺序 ──
        public static void TestChain()
        {
            var node = new Node2D("N") { Position = new Vector2(0f, 0f) };
            var tween = new Tween();
            tween.SetParallel(true);
            tween.TweenPropertyStep(node, TweenProperty.PositionX, 100f, 0.2f);
            tween.Chain(); // 关闭并行
            tween.TweenPropertyStep(node, TweenProperty.PositionY, 50f, 0.2f);
            tween.Start();

            // 0.2s：X 到位，同帧连续推进到 Y（Y 也拿到 0.2f 时间直接完成）
            tween.Update(0.2f);
            AssertNear(100f, node.Position.X, "X done.");
            AssertNear(50f, node.Position.Y, "Y done (chain = sequential, but same-frame advance).");
        }

        // ── Kill 立即终止，状态置 Done ──
        public static void TestKill()
        {
            var node = new Node2D("N") { Position = new Vector2(0f, 0f) };
            var tween = new Tween();
            tween.TweenPropertyStep(node, TweenProperty.PositionX, 100f, 1f);
            tween.Start();
            AssertTrue(tween.IsRunning, "Running after Start.");

            tween.Kill();
            AssertEqual(TweenState.Done, tween.State, "State Done after Kill.");
            AssertFalse(tween.IsRunning, "Not running after Kill.");

            tween.Update(1f); // 不再推进
            AssertNear(0f, node.Position.X, "No advance after Kill.");
        }

        // ── TweenFactory 自动清理 Done 的 Tween ──
        public static void TestFactoryCleanup()
        {
            var factory = new TweenFactory();
            var node = new Node2D("N");
            var t1 = factory.CreateTween();
            t1.TweenPropertyStep(node, TweenProperty.PositionX, 100f, 0.1f);
            t1.Start();

            var t2 = factory.CreateTween();
            t2.TweenPropertyStep(node, TweenProperty.PositionY, 50f, 0.5f);
            t2.Start();

            AssertEqual(2, factory.ActiveCount, "Two active tweens.");

            factory.Update(0.1f); // t1 完成 → 清理
            AssertEqual(1, factory.ActiveCount, "One active after t1 done.");
        }

        // ── TweenInterval 作为并行组锚点 ──
        public static void TestIntervalAsGroupAnchor()
        {
            var node = new Node2D("N") { Position = new Vector2(0f, 0f) };
            var tween = new Tween();
            tween.TweenInterval(0.2f);  // 等待 0.2s
            tween.TweenPropertyStep(node, TweenProperty.PositionX, 100f, 0.1f);
            tween.TweenPropertyStep(node, TweenProperty.PositionY, 50f, 0.1f);
            tween.Start();

            // 单次 Update(0.2f)：interval 组(0.2s)完成 → 同帧连续推进到 X/Y 组(各0.1s，0.2f 时间够)
            tween.Update(0.2f);
            AssertNear(100f, node.Position.X, "After interval: X done (same-frame advance).");
            AssertNear(50f, node.Position.Y, "After interval: Y done (same-frame advance).");
        }
    }
}
