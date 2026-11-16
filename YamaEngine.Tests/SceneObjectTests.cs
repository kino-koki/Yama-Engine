// SceneObjectTests.cs —— 追平 Godot 3 对象生命周期与查询：SetActive/Instantiate/FindObjectsOfType/对象池/分组/信号
// 覆盖：激活切换 OnEnable/OnDisable、非激活不参与 Update、Instantiate 深拷贝层级与组件、
//       FindObjectsOfType 仅返回活跃对象、对象池 Get/Release 复用与上限、
//       分组 add_to_group/is_in_group/get_nodes_in_group、节点信号 Connect/Emit/oneShot/Disconnect。

using System;
using System.Collections.Generic;
using System.Numerics;
using YamaEngine.Core;
using YamaEngine.GameFramework;
using static YamaEngine.Tests.TestHelpers;
namespace YamaEngine.Tests
{
    public static class SceneObjectTests
    {
        // ── 生命周期计数组件（无参构造，可被 Instantiate 克隆） ──
        public sealed class CounterComponent : ComponentReal
        {
            public int EnableCalls, DisableCalls, UpdateCalls, AwakeCalls;
            public override void Awake() { base.Awake(); AwakeCalls++; }
            public override void OnEnable() => EnableCalls++;
            public override void OnDisable() => DisableCalls++;
            public override void Update(float dt) => UpdateCalls++;
        }

        // ── SetActive(false) → OnDisable；重新激活 → OnEnable ──
        public static void TestSetActiveTogglesEnableDisable()
        {
            var go = new GameObjectReal("Toggle");
            var c = go.AddComponent<CounterComponent>();
            go.Initialize();
            go.Start(); // 激活路径：Awake→Start→OnEnable

            AssertTrue(go.IsActive, "Object must start active.");
            AssertEqual(1, c.EnableCalls, "Initial activation must fire OnEnable once.");

            go.SetActive(false);
            AssertTrue(!go.IsActive, "SetActive(false) must deactivate.");
            AssertEqual(1, c.DisableCalls, "Deactivation must fire OnDisable.");

            go.SetActive(false); // 幂等
            AssertEqual(1, c.DisableCalls, "Redundant SetActive(false) must not re-fire OnDisable.");

            go.SetActive(true);
            AssertTrue(go.IsActive, "SetActive(true) must reactivate.");
            AssertEqual(2, c.EnableCalls, "Reactivation must fire OnEnable again.");
        }

        // ── 非激活对象不参与 Update ──
        public static void TestInactiveSkipsUpdate()
        {
            var go = new GameObjectReal("UpdGate");
            var c = go.AddComponent<CounterComponent>();
            go.Initialize();
            go.Start();

            go.Update(0.016f);
            AssertEqual(1, c.UpdateCalls, "Active object must update.");

            go.SetActive(false);
            go.Update(0.016f);
            AssertEqual(1, c.UpdateCalls, "Inactive object must skip Update.");

            go.SetActive(true);
            go.Update(0.016f);
            AssertEqual(2, c.UpdateCalls, "Reactivated object must resume Update.");
        }

        // ── Instantiate 深拷贝：新 Id、独立层级、复制变换与组件 ──
        public static void TestInstantiateDeepCopiesHierarchy()
        {
            var root = new GameObjectReal("Root") { Tag = "Enemy", Layer = 3 };
            root.Transform.LocalPosition = new System.Numerics.Vector3(1, 2, 3);
            var rc = root.AddComponent<CounterComponent>();

            var child = new GameObjectReal("Child");
            child.Transform.LocalPosition = new System.Numerics.Vector3(0, 5, 0);
            child.SetParent(root, worldPositionStays: false); // 保持本地偏移
            var cc = child.AddComponent<CounterComponent>();

            var clone = root.Instantiate();

            AssertTrue(clone.Id != root.Id, "Clone must have a distinct Id.");
            AssertEqual("Root", clone.Name, "Clone must copy Name.");
            AssertEqual("Enemy", clone.Tag, "Clone must copy Tag.");
            AssertEqual(3, clone.Layer, "Clone must copy Layer.");
            AssertTrue(clone.GetComponent<CounterComponent>() != null, "Clone must copy components.");
            AssertTrue(!ReferenceEquals(clone.GetComponent<CounterComponent>(), rc),
                "Clone component must be a new instance, not shared.");
            AssertEqual(1, clone.ChildCount, "Clone must reproduce child hierarchy.");
            AssertEqual("Child", clone.GetChild(0).Name, "Clone child must copy name.");
            AssertTrue(!ReferenceEquals(clone.GetChild(0).GetComponent<CounterComponent>(), cc),
                "Clone child component must be a new instance.");

            // 变换拷贝：根的世界位置 == 原根本地位置（原为根）
            var p = clone.Transform.Position;
            AssertNear(p.X, 1f, 1e-4f, "Clone root X");
            AssertNear(p.Y, 2f, 1e-4f, "Clone root Y");
            AssertNear(p.Z, 3f, 1e-4f, "Clone root Z");
            // 子对象本地位置保留
            var cp = clone.GetChild(0).Transform.LocalPosition;
            AssertNear(cp.Y, 5f, 1e-4f, "Clone child local Y preserved.");
        }

        // ── Instantiate 克隆独立于原对象（改克隆不影响原） ──
        public static void TestInstantiateIsIndependent()
        {
            var original = new GameObjectReal("Orig") { Tag = "A" };
            original.AddComponent<CounterComponent>();
            var clone = original.Instantiate();

            clone.Tag = "B";
            clone.Name = "Clone";
            AssertEqual("A", original.Tag, "Mutating clone Tag must not affect original.");
            AssertEqual("Orig", original.Name, "Mutating clone Name must not affect original.");
        }

        // ── FindObjectsOfType 仅返回挂载该组件的活跃对象 ──
        public static void TestFindObjectsOfTypeFiltersActiveAndType()
        {
            var scene = new SceneReal("QueryScene");
            var withComp1 = new GameObjectReal("C1");
            withComp1.AddComponent<CounterComponent>();
            var withComp2 = new GameObjectReal("C2");
            withComp2.AddComponent<CounterComponent>();
            var without = new GameObjectReal("NoComp");

            var inactive = new GameObjectReal("Inactive");
            inactive.AddComponent<CounterComponent>();
            inactive.SetActive(false); // 非激活

            scene.AddRootObject(withComp1);
            scene.AddRootObject(withComp2);
            scene.AddRootObject(without);
            scene.AddRootObject(inactive);

            var found = scene.FindObjectsOfType<CounterComponent>();
            AssertEqual(2, found.Count, "FindObjectsOfType must return only active objects with the component.");
            AssertTrue(found.Contains(withComp1) && found.Contains(withComp2),
                "FindObjectsOfType must include both active holders.");

            // 激活第三个后计数变化
            inactive.SetActive(true);
            var found2 = scene.FindObjectsOfType<CounterComponent>();
            AssertEqual(3, found2.Count, "Activating a holder must add it to FindObjectsOfType results.");
        }

        // ── FindComponentsOfType 返回组件实例 ──
        public static void TestFindComponentsOfTypeReturnsInstances()
        {
            var scene = new SceneReal("CompScene");
            var a = new GameObjectReal("A");
            var ca = a.AddComponent<CounterComponent>();
            var b = new GameObjectReal("B");
            var cb = b.AddComponent<CounterComponent>();
            scene.AddRootObject(a);
            scene.AddRootObject(b);

            var comps = scene.FindComponentsOfType<CounterComponent>();
            AssertEqual(2, comps.Count, "Must find both component instances.");
            AssertTrue(comps.Contains(ca) && comps.Contains(cb), "Must return the actual component instances.");
        }

        // ── 对象池：Get 激活、Release 停用并复用同一实例 ──
        public static void TestPoolGetReleaseReusesInstance()
        {
            var proto = new GameObjectReal("Bullet");
            proto.AddComponent<CounterComponent>();
            var pool = new GameObjectPool(proto, initialCapacity: 0);

            AssertEqual(0, pool.TotalCount, "Empty pool starts with no objects.");

            var a = pool.Get();
            AssertTrue(a != null, "Pool must create object on demand.");
            AssertTrue(a.IsActive, "Get must return an active object.");

            var b = pool.Get();
            AssertTrue(b != null && !ReferenceEquals(a, b), "Second Get must yield a distinct instance.");

            // 归还 a
            AssertTrue(pool.Release(a), "Release of a leased object must succeed.");
            AssertTrue(!a.IsActive, "Released object must be deactivated.");
            AssertEqual(1, pool.AvailableCount, "Released object must enter the available list.");
            AssertEqual(1, pool.LeasedCount, "Only b remains leased.");

            // 再次 Get 应复用 a（LIFO）
            var c = pool.Get();
            AssertTrue(ReferenceEquals(a, c), "Get after Release must reuse the pooled instance.");
            AssertTrue(c.IsActive, "Reused instance must be active again.");
        }

        // ── 对象池上限：达上限且无空闲时 Get 返回 null；Release 超容丢弃 ──
        public static void TestPoolRespectsMaxSize()
        {
            var proto = new GameObjectReal("P");
            var pool = new GameObjectPool(proto, initialCapacity: 1, maxSize: 1);

            var a = pool.Get();
            AssertTrue(a != null, "Initial object available.");

            var b = pool.Get(); // 无空闲且已达上限
            AssertTrue(b == null, "Get must return null when at MaxSize with no idle objects.");

            AssertTrue(pool.Release(a), "Release must succeed.");
            // 再 Release 同一对象应失败（已不在租借集合）
            AssertTrue(!pool.Release(a), "Double-release must fail.");
        }

        // ── 分组：AddToGroup/IsInGroup/RemoveFromGroup ──
        public static void TestGroupMembershipAndQuery()
        {
            var scene = new SceneReal("GroupScene");
            var a = new GameObjectReal("A");
            var b = new GameObjectReal("B");
            var c = new GameObjectReal("C");
            a.AddToGroup("enemies");
            b.AddToGroup("enemies");
            a.AddToGroup("fast");
            c.AddToGroup("allies");

            AssertTrue(a.IsInGroup("enemies"), "A must be in enemies.");
            AssertTrue(a.IsInGroup("fast"), "A must be in fast.");
            AssertTrue(!a.IsInGroup("allies"), "A must not be in allies.");

            scene.AddRootObject(a);
            scene.AddRootObject(b);
            scene.AddRootObject(c);

            var enemies = scene.GetNodesInGroup("enemies");
            AssertEqual(2, enemies.Count, "Must find both enemies.");
            AssertTrue(enemies.Contains(a) && enemies.Contains(b), "Must include both enemy nodes.");

            var fast = scene.GetNodesInGroup("fast");
            AssertEqual(1, fast.Count, "Must find one fast node.");
            AssertEqual(2, scene.CountNodesInGroup("enemies"), "CountNodesInGroup must count group members.");

            // 移出分组
            a.RemoveFromGroup("enemies");
            AssertTrue(!a.IsInGroup("enemies"), "A must leave enemies after RemoveFromGroup.");
            AssertEqual(1, scene.GetNodesInGroup("enemies").Count, "Removing A must shrink enemies group.");
        }

        // ── 分组：非活跃对象不参与 get_nodes_in_group，但仍计入 Count ──
        public static void TestGroupInactiveExcludedFromQuery()
        {
            var scene = new SceneReal("GroupInactive");
            var active = new GameObjectReal("Active");
            var inactive = new GameObjectReal("Inactive");
            active.AddToGroup("units");
            inactive.AddToGroup("units");
            inactive.SetActive(false);
            scene.AddRootObject(active);
            scene.AddRootObject(inactive);

            var units = scene.GetNodesInGroup("units");
            AssertEqual(1, units.Count, "get_nodes_in_group must exclude inactive objects.");
            AssertTrue(units.Contains(active), "Must include the active member.");
            AssertEqual(2, scene.CountNodesInGroup("units"), "CountNodesInGroup includes all members regardless of active.");
        }

        // ── 节点信号：Connect/Emit 多处理器按序触发 ──
        public static void TestSignalConnectEmitOrder()
        {
            var go = new GameObjectReal("SigNode");
            var sig = go.AddSignal("health_changed");

            var order = new List<string>();
            sig.Connect(_ => order.Add("first"));
            sig.Connect(_ => order.Add("second"));
            AssertEqual(2, sig.ConnectionCount, "Two handlers must be connected.");

            sig.Emit();
            AssertEqual(2, order.Count, "Both handlers must fire.");
            AssertEqual("first", order[0], "First handler must fire first.");
            AssertEqual("second", order[1], "Second handler must fire second.");
        }

        // ── 节点信号：Emit 传递参数；oneShot 自动断开 ──
        public static void TestSignalOneShotAndArgs()
        {
            var go = new GameObjectReal("OneShotNode");
            var sig = go.AddSignal("died");

            object? oneShotReceived = null;
            object? normalReceived = null;
            sig.Connect(v => { normalReceived = v; }, oneShot: false);
            sig.Connect(v => { oneShotReceived = v; }, oneShot: true);

            sig.Emit(42);
            AssertEqual(42, oneShotReceived, "oneShot handler must receive the emitted argument.");
            AssertEqual(42, normalReceived, "Normal handler must receive the emitted argument.");
            AssertEqual(1, sig.ConnectionCount, "oneShot must auto-disconnect after firing.");

            oneShotReceived = null; // 清零以证明 oneShot 不再触发
            sig.Emit(99); // 仅 normal handler 仍在
            AssertEqual(99, normalReceived, "Normal handler must fire again with new arg.");
            AssertTrue(oneShotReceived == null, "oneShot must not fire again after disconnect.");
        }

        // ── 节点信号：Disconnect 按 id 断开；HasSignal 反映连接状态 ──
        public static void TestSignalDisconnectAndHasSignal()
        {
            var go = new GameObjectReal("DiscNode");
            var sig = go.AddSignal("hit");
            int calls = 0;
            int id = sig.Connect(_ => calls++);

            AssertTrue(go.HasSignal("hit"), "HasSignal must be true when connected.");
            AssertTrue(sig.Disconnect(id), "Disconnect of valid id must succeed.");
            AssertTrue(!go.HasSignal("hit"), "HasSignal must be false after disconnect.");
            AssertTrue(!sig.Disconnect(id), "Double-disconnect must fail.");

            sig.Emit();
            AssertEqual(0, calls, "Disconnected handler must not fire.");
        }

        // ── Timer 节点：单次触发后停止 ──
        public static void TestTimerOneShotEmitsAndStops()
        {
            var go = new GameObjectReal("TimerGo");
            var timer = go.AddComponent<TimerNode>();
            timer.WaitTime = 1.0f;
            timer.OneShot = true;

            int fires = 0;
            go.GetSignal("timeout").Connect(_ => fires++);

            AssertTrue(!timer.IsRunning, "Timer must not run before Start().");
            timer.Start();
            AssertTrue(timer.IsRunning, "Timer must run after Start().");

            // 未到时长：不触发
            timer.Update(0.5f);
            AssertEqual(0, fires, "Timer must not fire before WaitTime elapses.");
            AssertTrue(timer.IsRunning, "Timer must still run at half time.");

            // 到达时长：触发一次并停止
            timer.Update(0.6f);
            AssertEqual(1, fires, "Timer must fire once when WaitTime elapses.");
            AssertTrue(!timer.IsRunning, "OneShot timer must stop after firing.");
            AssertEqual(0f, timer.TimeLeft, "Stopped timer TimeLeft must be 0.");

            // 再 Update 不触发（已停止）
            timer.Update(2.0f);
            AssertEqual(1, fires, "Stopped OneShot timer must not fire again.");
        }

        // ── Timer 节点：循环模式自动重启，累计触发 ──
        public static void TestTimerLoopRestarts()
        {
            var go = new GameObjectReal("LoopTimer");
            var timer = go.AddComponent<TimerNode>();
            timer.WaitTime = 1.0f;
            timer.OneShot = false; // 循环

            int fires = 0;
            go.GetSignal("timeout").Connect(_ => fires++);
            timer.Start();

            // 3 秒 = 3 次触发（1.0/2.0/3.0 边界）
            for (int i = 0; i < 30; i++)
                timer.Update(0.1f);

            AssertEqual(3, fires, "Loop timer must fire once per WaitTime over 3 seconds.");
            AssertTrue(timer.IsRunning, "Loop timer must keep running after timeouts.");
            AssertEqual(3, timer.TimeoutCount, "TimeoutCount must accumulate.");
            // 循环计时器归零后应立即重启：剩余时间在 (0, WaitTime] 内
            AssertTrue(timer.TimeLeft > 0f && timer.TimeLeft <= 1.0f,
                $"Loop timer TimeLeft must be in (0,WaitTime], got {timer.TimeLeft:F3}");
        }

        // ── Timer 节点：Stop 中途取消，不触发 timeout ──
        public static void TestTimerStopCancelsWithoutFire()
        {
            var go = new GameObjectReal("CancelTimer");
            var timer = go.AddComponent<TimerNode>();
            timer.WaitTime = 1.0f;
            timer.OneShot = true;

            int fires = 0;
            go.GetSignal("timeout").Connect(_ => fires++);
            timer.Start();

            timer.Update(0.9f); // 接近但未到
            timer.Stop();
            AssertTrue(!timer.IsRunning, "Stop must halt the timer.");

            timer.Update(5.0f); // 大步进：不应触发（已停止）
            AssertEqual(0, fires, "Stopped timer must not fire even after large dt.");
        }

        // ── Timer 节点：WaitTime 运行中改变会重置当前倒计时 ──
        public static void TestTimerWaitTimeChangeResetsCountdown()
        {
            var go = new GameObjectReal("ReconfigTimer");
            var timer = go.AddComponent<TimerNode>();
            timer.WaitTime = 2.0f;
            timer.OneShot = true;

            int fires = 0;
            go.GetSignal("timeout").Connect(_ => fires++);
            timer.Start();

            timer.Update(1.0f); // 走了一半
            timer.WaitTime = 0.5f; // 运行中改时长 → 重置本次倒计时
            AssertEqual(0.5f, timer.TimeLeft, "Changing WaitTime while running resets countdown.");

            timer.Update(0.4f); // 未到新时长
            AssertEqual(0, fires, "Must not fire before new WaitTime.");

            timer.Update(0.2f); // 累计 0.6 > 0.5 → 触发
            AssertEqual(1, fires, "Must fire once new WaitTime elapses.");
        }

        // ── PackedScene：模板反复实例化出独立副本 ──
        public static void TestPackedSceneInstantiateIndependent()
        {
            var proto = new GameObjectReal("Enemy") { Tag = "mob", Layer = 2 };
            proto.AddComponent<CounterComponent>();
            var packed = new PackedScene(proto);

            AssertEqual("Enemy", packed.Name, "PackedScene name must come from prototype.");
            AssertEqual(1, packed.ComponentCount, "PackedScene must report prototype component count.");

            var a = packed.Instantiate();
            var b = packed.Instantiate();

            AssertTrue(a.Id != b.Id, "Two instances must have distinct Ids.");
            AssertTrue(a.Id != proto.Id && b.Id != proto.Id, "Instances must not alias the prototype.");
            AssertTrue(a.GetComponent<CounterComponent>() != null, "Instance must copy components.");

            // 实例间互不影响，也不影响原型
            a.Tag = "changed";
            AssertEqual("mob", b.Tag, "Mutating one instance must not affect another.");
            AssertEqual("mob", proto.Tag, "Mutating an instance must not affect the prototype.");
        }

        // ── PackedScene：批量实例化 count 棵独立树 ──
        public static void TestPackedSceneInstantiateMultiple()
        {
            var proto = new GameObjectReal("Bullet");
            proto.AddComponent<CounterComponent>();
            var packed = new PackedScene(proto);

            var batch = packed.InstantiateMultiple(5);
            AssertEqual(5, batch.Count, "InstantiateMultiple must produce the requested count.");

            // 全部独立 Id
            var ids = new HashSet<long>();
            foreach (var inst in batch)
                ids.Add(inst.Id);
            AssertEqual(5, ids.Count, "All batch instances must have distinct Ids.");
        }

        // ── Tween 缓动函数：QuadOut/QuadIn/CubicOut 关键值 ──
        public static void TestTweenEaseKnownValues()
        {
            // Linear: f(t)=t
            AssertNear(TweenNode.ApplyEase(0.5f, TweenEase.Linear), 0.5f, 1e-4f, "Linear(0.5)");
            // QuadIn: f(t)=t²
            AssertNear(TweenNode.ApplyEase(0.5f, TweenEase.QuadIn), 0.25f, 1e-4f, "QuadIn(0.5)");
            // QuadOut: f(t)=1-(1-t)²
            AssertNear(TweenNode.ApplyEase(0.5f, TweenEase.QuadOut), 0.75f, 1e-4f, "QuadOut(0.5)");
            // CubicOut: f(t)=1-(1-t)³
            AssertNear(TweenNode.ApplyEase(0.5f, TweenEase.CubicOut), 0.875f, 1e-4f, "CubicOut(0.5)");
            // 边界：所有缓动 f(0)=0, f(1)=1
            foreach (TweenEase e in Enum.GetValues<TweenEase>())
            {
                AssertNear(TweenNode.ApplyEase(0f, e), 0f, 1e-5f, $"{e}(0)");
                AssertNear(TweenNode.ApplyEase(1f, e), 1f, 1e-5f, $"{e}(1)");
            }
        }

        // ── Tween 单步：float 插值按缓动曲线推进 ──
        public static void TestTweenFloatStepEasedProgress()
        {
            var go = new GameObjectReal("TweenGo");
            var tween = go.AddComponent<TweenNode>();
            float x = 0f;
            tween.TweenFloat(v => x = v, 0f, 10f, 1.0f, TweenEase.QuadOut);
            tween.Start();
            AssertTrue(tween.IsRunning, "Tween must run after Start().");

            // QuadOut 在 t=0.5 时值应为 0.75 * 10 = 7.5
            tween.Update(0.5f);
            AssertNear(x, 7.5f, 1e-3f, "QuadOut at t=0.5 must reach 7.5.");
            AssertTrue(tween.IsRunning, "Tween must still run at half duration.");

            // 完成后到达终点 10
            tween.Update(0.6f);
            AssertNear(x, 10f, 1e-4f, "Tween must reach target at end.");
            AssertTrue(tween.IsFinished, "Non-loop tween must finish after last step.");
            AssertEqual(1, tween.CompletedCycles, "One complete cycle.");
        }

        // ── Tween 多步顺序执行 ──
        public static void TestTweenSequentialSteps()
        {
            var go = new GameObjectReal("SeqTween");
            var tween = go.AddComponent<TweenNode>();
            var log = new List<string>();
            float x = 0f, y = 0f;
            tween.TweenFloat(v => { x = v; log.Add($"step0:{v:F1}"); }, 0f, 10f, 0.5f, TweenEase.Linear)
                 .Then()
                 .TweenFloat(v => { y = v; log.Add($"step1:{v:F1}"); }, 0f, 20f, 0.5f, TweenEase.Linear);
            AssertEqual(2, tween.StepCount, "Must have 2 steps.");

            tween.Start();
            AssertEqual(0, tween.CurrentStepIndex, "Must start at step 0.");

            // 0.5s：完成 step0，进入 step1
            tween.Update(0.5f);
            AssertEqual(1, tween.CurrentStepIndex, "Must advance to step 1 after step0 duration.");
            AssertNear(x, 10f, 1e-4f, "Step0 must reach 10.");

            // 0.5s：完成 step1
            tween.Update(0.5f);
            AssertNear(y, 20f, 1e-4f, "Step1 must reach 20.");
            AssertTrue(tween.IsFinished, "Both steps complete.");
        }

        // ── Tween 回调步骤：立即/延迟触发 ──
        public static void TestTweenCallbackStep()
        {
            var go = new GameObjectReal("CbTween");
            var tween = go.AddComponent<TweenNode>();
            int fired = 0;
            float dummy = 0f;
            tween.TweenFloat(v => dummy = v, 0f, 1f, 0.1f)
                 .Then()
                 .TweenCallback(() => fired++, 0f); // 立即回调（进入步骤时）
            tween.Start();

            tween.Update(0.1f); // 完成 step0
            // step1 是 0 duration 回调，同帧进入即触发
            tween.Update(0.01f);
            AssertEqual(1, fired, "Callback must fire when its step is entered.");
        }

        // ── Tween 循环：非无限循环指定次数 ──
        public static void TestTweenLoops()
        {
            var go = new GameObjectReal("LoopTween");
            var tween = go.AddComponent<TweenNode>();
            float x = 0f;
            tween.TweenFloat(v => x = v, 0f, 10f, 0.5f, TweenEase.Linear)
                 .SetLoops(3);
            tween.Start();

            // 播满 3 个完整周期 (3 * 0.5 = 1.5s)
            for (int i = 0; i < 30; i++)
                tween.Update(0.05f);

            AssertEqual(3, tween.CompletedCycles, "Must complete exactly 3 cycles.");
            AssertTrue(tween.IsFinished, "Loop tween must finish after set loops.");
        }

        // ── Tween 暂停/恢复 ──
        public static void TestTweenPauseResume()
        {
            var go = new GameObjectReal("PauseTween");
            var tween = go.AddComponent<TweenNode>();
            float x = 0f;
            tween.TweenFloat(v => x = v, 0f, 10f, 1.0f, TweenEase.Linear);
            tween.Start();

            tween.Update(0.3f);
            AssertNear(x, 3f, 1e-4f, "Before pause: x=3.");

            tween.Pause();
            AssertTrue(!tween.IsRunning, "Must be paused.");

            tween.Update(0.5f); // 暂停中不推进
            AssertNear(x, 3f, 1e-4f, "Paused tween must not advance.");

            tween.Resume();
            AssertTrue(tween.IsRunning, "Must resume.");
            tween.Update(0.2f);
            AssertNear(x, 5f, 1e-4f, "After resume: x=5 (0.3+0.2=0.5 elapsed of 1.0).");
        }

        // ── 延迟调用：排队后 Flush 才执行 ──
        public static void TestDeferredCallFlushesAtEndOfFrame()
        {
            DeferredCallQueue.Clear();
            int called = 0;
            DeferredCallQueue.CallDeferred(() => called++);
            AssertEqual(0, called, "Deferred call must not execute before Flush.");
            AssertEqual(1, DeferredCallQueue.PendingCount, "Must be 1 pending.");

            DeferredCallQueue.Flush();
            AssertEqual(1, called, "Flush must execute the deferred call.");
            AssertEqual(0, DeferredCallQueue.PendingCount, "Queue must be empty after Flush.");
        }

        // ── 延迟调用：多次排队按顺序执行 ──
        public static void TestDeferredCallPreservesOrder()
        {
            DeferredCallQueue.Clear();
            var order = new List<int>();
            DeferredCallQueue.CallDeferred(() => order.Add(1));
            DeferredCallQueue.CallDeferred(() => order.Add(2));
            DeferredCallQueue.CallDeferred(() => order.Add(3));

            DeferredCallQueue.Flush();
            AssertEqual(3, order.Count, "All three calls must execute.");
            AssertEqual(1, order[0], "First queued must fire first.");
            AssertEqual(2, order[1], "Second queued must fire second.");
            AssertEqual(3, order[2], "Third queued must fire third.");
        }

        // ── 延迟调用：带参数的 CallDeferred<T> ──
        public static void TestDeferredCallWithArgs()
        {
            DeferredCallQueue.Clear();
            int received = 0;
            DeferredCallQueue.CallDeferred<int>((v) => received = v, 42);
            DeferredCallQueue.Flush();
            AssertEqual(42, received, "Deferred call with arg must receive the argument.");
        }

        // ── 延迟调用：回调中再 CallDeferred 留到下次 Flush（防无限递归） ──
        public static void TestDeferredCallNoReentrantFlush()
        {
            DeferredCallQueue.Clear();
            int outer = 0, inner = 0;
            DeferredCallQueue.CallDeferred(() =>
            {
                outer++;
                DeferredCallQueue.CallDeferred(() => inner++); // 帧中排队
                AssertTrue(DeferredCallQueue.IsFlushing, "Must be flushing during callback.");
            });

            DeferredCallQueue.Flush();
            AssertEqual(1, outer, "Outer call must execute on first Flush.");
            AssertEqual(0, inner, "Inner call queued during Flush must NOT execute on same Flush.");

            DeferredCallQueue.Flush();
            AssertEqual(1, inner, "Inner call must execute on next Flush.");
        }

        // ── 延迟调用：回调异常不阻塞剩余调用 ──
        public static void TestDeferredCallExceptionDoesNotBlockQueue()
        {
            DeferredCallQueue.Clear();
            int after = 0;
            DeferredCallQueue.CallDeferred(() => throw new InvalidOperationException("boom"));
            DeferredCallQueue.CallDeferred(() => after++);

            DeferredCallQueue.Flush(); // 第一个抛异常，第二个仍执行
            AssertEqual(1, after, "Call after a throwing call must still execute.");
        }

        // ── 延迟调用：安全地在信号回调中修改场景树（Godot 典型用法） ──
        public static void TestDeferredCallSafeSceneMutation()
        {
            DeferredCallQueue.Clear();
            var scene = new SceneReal("DeferredScene");
            var go = new GameObjectReal("ToBeAdded");

            // 在 Flush 期间添加到场景（模拟信号回调中途修改）
            DeferredCallQueue.CallDeferred(() => scene.AddRootObject(go));
            AssertEqual(0, scene.RootGameObjects.Count, "Scene must not be mutated before Flush.");

            DeferredCallQueue.Flush();
            AssertEqual(1, scene.RootGameObjects.Count, "Deferred AddRootObject must apply after Flush.");
        }

        // ── process_mode：Pausable（默认）暂停时不更新 ──
        public static void TestProcessModePausableSkipsWhenPaused()
        {
            var go = new GameObjectReal("Pausable");
            var c = go.AddComponent<CounterComponent>();
            go.ProcessMode = NodeProcessMode.Pausable;
            go.Initialize();
            go.Start();

            go.Update(0.016f);
            AssertEqual(1, c.UpdateCalls, "Pausable must update when not paused.");

            TimeManager.Instance.IsGamePaused = true;
            try
            {
                go.Update(0.016f);
                AssertEqual(1, c.UpdateCalls, "Pausable must NOT update when paused.");
                AssertTrue(!go.ShouldProcess(true), "ShouldProcess(paused=true) must be false for Pausable.");
                AssertTrue(go.ShouldProcess(false), "ShouldProcess(paused=false) must be true for Pausable.");
            }
            finally
            {
                TimeManager.Instance.IsGamePaused = false;
            }
        }

        // ── process_mode：Always 暂停时仍更新 ──
        public static void TestProcessModeAlwaysRunsWhenPaused()
        {
            var go = new GameObjectReal("Always");
            var c = go.AddComponent<CounterComponent>();
            go.ProcessMode = NodeProcessMode.Always;
            go.Initialize();
            go.Start();

            TimeManager.Instance.IsGamePaused = true;
            try
            {
                go.Update(0.016f);
                AssertEqual(1, c.UpdateCalls, "Always must update even when paused.");
                AssertTrue(go.ShouldProcess(true), "ShouldProcess(paused=true) must be true for Always.");
            }
            finally
            {
                TimeManager.Instance.IsGamePaused = false;
            }
        }

        // ── process_mode：WhenPaused 仅暂停时更新 ──
        public static void TestProcessModeWhenPausedOnlyRunsWhenPaused()
        {
            var go = new GameObjectReal("WhenPaused");
            var c = go.AddComponent<CounterComponent>();
            go.ProcessMode = NodeProcessMode.WhenPaused;
            go.Initialize();
            go.Start();

            go.Update(0.016f); // 未暂停 → 不更新
            AssertEqual(0, c.UpdateCalls, "WhenPaused must NOT update when not paused.");

            TimeManager.Instance.IsGamePaused = true;
            try
            {
                go.Update(0.016f); // 暂停 → 更新
                AssertEqual(1, c.UpdateCalls, "WhenPaused must update when paused.");
            }
            finally
            {
                TimeManager.Instance.IsGamePaused = false;
            }
        }

        // ── process_mode：Disabled 永不更新（即使激活） ──
        public static void TestProcessModeDisabledNeverRuns()
        {
            var go = new GameObjectReal("Disabled");
            var c = go.AddComponent<CounterComponent>();
            go.ProcessMode = NodeProcessMode.Disabled;
            go.Initialize();
            go.Start();

            go.Update(0.016f);
            AssertEqual(0, c.UpdateCalls, "Disabled must never update (not paused).");

            TimeManager.Instance.IsGamePaused = true;
            try
            {
                go.Update(0.016f);
                AssertEqual(0, c.UpdateCalls, "Disabled must never update (paused).");
                AssertTrue(!go.ShouldProcess(false), "ShouldProcess must be false for Disabled always.");
                AssertTrue(!go.ShouldProcess(true), "ShouldProcess must be false for Disabled when paused.");
            }
            finally
            {
                TimeManager.Instance.IsGamePaused = false;
            }
        }

        // ── process_mode：Inherit 无父级退化为 Pausable ──
        public static void TestProcessModeInheritDefaultsToPausable()
        {
            var go = new GameObjectReal("Inherit");
            var c = go.AddComponent<CounterComponent>();
            go.ProcessMode = NodeProcessMode.Inherit; // 默认
            go.Initialize();
            go.Start();

            go.Update(0.016f);
            AssertEqual(1, c.UpdateCalls, "Inherit (no parent) must behave as Pausable: update when not paused.");

            TimeManager.Instance.IsGamePaused = true;
            try
            {
                go.Update(0.016f);
                AssertEqual(1, c.UpdateCalls, "Inherit (no parent) must behave as Pausable: skip when paused.");
            }
            finally
            {
                TimeManager.Instance.IsGamePaused = false;
            }
        }

        // ── process_mode：IsActive=false 优先级高于 ProcessMode=Always ──
        public static void TestProcessModeInactiveOverridesAlways()
        {
            var go = new GameObjectReal("InactiveAlways");
            var c = go.AddComponent<CounterComponent>();
            go.ProcessMode = NodeProcessMode.Always;
            go.Initialize();
            go.Start();
            go.SetActive(false);

            go.Update(0.016f); // IsActive=false → 即使 Always 也不更新
            AssertEqual(0, c.UpdateCalls, "IsActive=false must override ProcessMode.Always.");

            go.SetActive(true);
            go.Update(0.016f);
            AssertEqual(1, c.UpdateCalls, "Reactivated Always object must resume updating.");
        }

        // ── AnimationPlayer：非循环播完触发 animation_finished 并停在末尾 ──
        public static void TestAnimationPlayerPlayOnceFinishesAndEmits()
        {
            var go = new GameObjectReal("AnimOnce");
            var player = go.AddComponent<AnimationPlayerReal>();
            var anim = new AnimationResource("rise", 1.0f, loop: false);
            var track = anim.AddVectorTrack(AnimationTrackType.Translation);
            track.AddKey(0f, Vector3.Zero);
            track.AddKey(1f, new Vector3(0, 2f, 0));
            player.AddAnimation(anim);

            int finished = 0;
            go.GetSignal("animation_finished").Connect(_ => finished++);

            AssertEqual(string.Empty, player.CurrentAnimation, "No animation before Play.");
            player.Play("rise");
            AssertTrue(player.IsPlaying, "Must be playing after Play.");

            // 逐步推进 1 秒
            for (int i = 0; i < 20; i++) go.Update(0.05f);
            AssertNear(2f, go.Transform.LocalPosition.Y, 1e-3f,
                "Non-loop anim must land at last key and drive LocalPosition.");
            AssertTrue(!player.IsPlaying, "Non-loop anim must stop after finishing.");
            AssertEqual(1, finished, "animation_finished must fire once for non-loop completion.");
        }

        // ── AnimationPlayer：循环动画回绕、不触发 finished ──
        public static void TestAnimationPlayerLoopWrapsWithoutFinish()
        {
            var go = new GameObjectReal("AnimLoop");
            var player = go.AddComponent<AnimationPlayerReal>();
            var anim = new AnimationResource("spin", 1.0f, loop: true);
            var track = anim.AddRotationTrack();
            track.AddKey(0f, Quaternion.Identity);
            track.AddKey(1f, Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI * 2f));
            player.AddAnimation(anim);

            int finished = 0;
            go.GetSignal("animation_finished").Connect(_ => finished++);

            player.Play("spin");
            for (int i = 0; i < 40; i++) go.Update(0.05f); // 播 2 秒 = 2 圈

            AssertTrue(player.IsPlaying, "Looping anim must keep playing.");
            AssertEqual(0, finished, "Looping anim must never emit animation_finished.");
            // 回绕后 position 应在 [0, Length] 内
            AssertTrue(player.CurrentPosition >= 0f && player.CurrentPosition <= 1.0001f,
                $"Loop position must wrap into [0,Length], got {player.CurrentPosition:F3}.");
        }

        // ── AnimationPlayer：Pause 停止推进、Resume 继续 ──
        public static void TestAnimationPlayerPauseResumeHoldsPosition()
        {
            var go = new GameObjectReal("AnimPause");
            var player = go.AddComponent<AnimationPlayerReal>();
            var anim = new AnimationResource("slide", 2.0f, loop: false);
            var track = anim.AddVectorTrack(AnimationTrackType.Translation);
            track.AddKey(0f, Vector3.Zero);
            track.AddKey(2f, new Vector3(4f, 0, 0));
            player.AddAnimation(anim);

            player.Play("slide");
            go.Update(0.5f); // position=0.5
            float posBefore = player.CurrentPosition;
            player.Pause();
            go.Update(0.5f); // 暂停中不推进
            go.Update(0.5f);
            AssertNear(posBefore, player.CurrentPosition, 1e-4f,
                "Paused player must not advance position.");
            AssertTrue(player.IsPaused, "IsPaused must be true after Pause.");
            AssertTrue(player.IsPlaying, "IsPlaying stays true while paused.");

            player.Resume();
            go.Update(0.5f);
            AssertTrue(player.CurrentPosition > posBefore, "Resume must continue advancing.");
        }

        // ── AnimationPlayer：SpeedScale 缩放推进速率 ──
        public static void TestAnimationPlayerSpeedScaleScalesAdvance()
        {
            var go = new GameObjectReal("AnimSpeed");
            var player = go.AddComponent<AnimationPlayerReal>();
            var anim = new AnimationResource("run", 1.0f, loop: false);
            var track = anim.AddVectorTrack(AnimationTrackType.Translation);
            track.AddKey(0f, Vector3.Zero);
            track.AddKey(1f, new Vector3(0, 1f, 0));
            player.AddAnimation(anim);

            player.SpeedScale = 2f;
            player.Play("run");
            go.Update(0.5f); // dt*speed = 0.5*2 = 1.0 → 播完
            AssertNear(1.0f, player.CurrentPosition, 1e-3f,
                "SpeedScale=2 with dt=0.5 must advance position by 1.0.");
            AssertTrue(!player.IsPlaying, "Must finish once position reaches Length.");

            // SpeedScale=0 冻结
            player.SpeedScale = 0f;
            player.Play("run");
            go.Update(0.5f);
            AssertNear(0f, player.CurrentPosition, 1e-5f, "SpeedScale=0 must freeze position.");
        }

        // ── AnimationPlayer：Seek 跳转并立即应用该时刻的姿态 ──
        public static void TestAnimationPlayerSeekJumpsPosition()
        {
            var go = new GameObjectReal("AnimSeek");
            var player = go.AddComponent<AnimationPlayerReal>();
            var anim = new AnimationResource("move", 2.0f, loop: false);
            var track = anim.AddVectorTrack(AnimationTrackType.Translation);
            track.AddKey(0f, Vector3.Zero);
            track.AddKey(2f, new Vector3(0, 10f, 0));
            player.AddAnimation(anim);

            player.Play("move");
            player.Seek(1.0f); // 中点
            AssertNear(1.0f, player.CurrentPosition, 1e-5f, "Seek must set position directly.");
            AssertNear(5f, go.Transform.LocalPosition.Y, 1e-2f,
                "Seek to midpoint must apply interpolated pose (half of 10).");
        }

        // ── AnimationPlayer：播放队列 播完自动接播 ──
        public static void TestAnimationPlayerQueuePlaysNextAfterFinish()
        {
            var go = new GameObjectReal("AnimQueue");
            var player = go.AddComponent<AnimationPlayerReal>();
            var a1 = new AnimationResource("idle", 0.5f, loop: false);
            a1.AddFloatTrack("energy").AddKey(0f, 0f);
            var a2 = new AnimationResource("attack", 0.5f, loop: false);
            a2.AddFloatTrack("energy").AddKey(0f, 1f);
            player.AddAnimation(a1);
            player.AddAnimation(a2);

            var played = new List<string>();
            go.GetSignal("animation_finished").Connect(a => played.Add((string)a));

            player.Play("idle");
            player.QueueAnimation("attack");

            // 推进 1.5 秒：idle(0.5) 播完 → 自动接播 attack(0.5) 播完
            for (int i = 0; i < 30; i++) go.Update(0.05f);

            AssertEqual("idle", played.Count > 0 ? played[0] : "", "First finished must be idle.");
            AssertEqual("attack", played.Count > 1 ? played[1] : "", "Queue must auto-play attack after idle.");
            AssertTrue(!player.IsPlaying, "Both queued anims finished; player must stop.");
        }

        // ── AnimationPlayer：Value 轨道通过 value_changed 信号回调 ──
        public static void TestAnimationPlayerValueTrackEmitsValueChanged()
        {
            var go = new GameObjectReal("AnimValue");
            var player = go.AddComponent<AnimationPlayerReal>();
            var anim = new AnimationResource("fade", 1.0f, loop: false);
            var track = anim.AddFloatTrack("opacity");
            track.AddKey(0f, 1f);
            track.AddKey(1f, 0f);
            player.AddAnimation(anim);

            float lastOpacity = -1f;
            go.GetSignal("value_changed").Connect(a =>
            {
                var args = (ValueChangedArgs)a;
                if (args.Property == "opacity") lastOpacity = args.Value;
            });

            player.Play("fade");
            go.Update(0.5f); // 中点 opacity≈0.5
            AssertNear(0.5f, lastOpacity, 1e-2f,
                "Value track must emit value_changed with interpolated opacity at midpoint.");
        }

        private static void AssertNear(float got, float want, float tol, string msg)
            => AssertTrue(MathF.Abs(got - want) <= tol,
                $"{msg} (got {got:F4}, want {want:F4}±{tol:F4})");
    }
}
