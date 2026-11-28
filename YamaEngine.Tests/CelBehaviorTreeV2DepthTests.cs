using System;
using System.Collections.Generic;
using System.Numerics;
using YamaEngine.AI;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests
{
    /// <summary>
    /// 行为树 V2（Blackboard + 装饰器 + 组合节点 + Builder）深度测试。
    /// 对齐 UE5 BehaviorTree / Behavior Designer。
    /// </summary>
    public static class CelBehaviorTreeV2DepthTests
    {
        // ── 私有辅助 ──
        private static void AssertFalse(bool cond, string msg) { if (cond) throw new Exception(msg); }
        private static void Near(float want, float got, float tol, string msg)
        {
            if (MathF.Abs(want - got) > tol) throw new Exception($"{msg}: want {want}, got {got}");
        }
        private static BTContext MakeCtx(Blackboard? bb = null, float dt = 0.016f)
        {
            return new BTContext
            {
                Blackboard = bb ?? new Blackboard(),
                DeltaTime = dt,
                AgentId = 1,
            };
        }

        // ── Blackboard ──

        public static void TestBBDefaults()
        {
            var bb = new Blackboard();
            AssertFalse(bb.HasKey("Any"), "empty bb HasKey should be false");
            AssertFalse(bb.GetBool("b"), "default bool false");
            AssertEqual(0, bb.GetInt("i"), "default int");
            Near(0f, bb.GetFloat("f"), 1e-5f, "default float");
        }

        public static void TestBBTypeSafeAccessors()
        {
            var bb = new Blackboard();
            bb.SetBool("b", true); AssertTrue(bb.GetBool("b"), "bool");
            bb.SetInt("i", 42); AssertEqual(42, bb.GetInt("i"), "int");
            bb.SetFloat("f", 1.5f); Near(1.5f, bb.GetFloat("f"), 1e-5f, "float");
            var v = new Vector3(1, 2, 3);
            bb.SetVector3("v", v); AssertEqual(v, bb.GetVector3("v"), "vec");
            bb.SetString("s", "hello");
            AssertEqual("hello", bb.GetDynamicValue<string>("s") ?? "", "str");
            bb.SetEntityId("e", 7); AssertEqual(7, bb.GetEntityId("e"), "entity");
        }

        public static void TestBBDynamicFallback()
        {
            // 无定义时所有读写都落到动态键
            var bb = new Blackboard();
            AssertFalse(bb.HasKey("dyn"), "before");
            bb.SetBool("dyn", true);
            AssertTrue(bb.HasKey("dyn"), "after set");
            AssertTrue(bb.GetBool("dyn"), "get");
            // 覆盖
            bb.SetBool("dyn", false);
            AssertFalse(bb.GetBool("dyn"), "overwrite");
        }

        public static void TestBBClear()
        {
            var bb = new Blackboard();
            bb.SetInt("a", 1); bb.SetInt("b", 2);
            AssertTrue(bb.HasKey("a") && bb.HasKey("b"), "both set");
            bb.Clear();
            AssertEqual(0, bb.GetInt("a"), "a cleared");
            AssertEqual(0, bb.GetInt("b"), "b cleared");
        }

        public static void TestBBSerializeRoundtrip()
        {
            var bb = new Blackboard();
            bb.SetInt("hp", 100); bb.SetBool("alive", true); bb.SetFloat("speed", 5.0f);
            var dict = bb.Serialize();
            // 无定义时走动态键，Serialize 加 "dyn:" 前缀
            AssertTrue(dict.ContainsKey("dyn:hp") && dict.ContainsKey("dyn:alive") && dict.ContainsKey("dyn:speed"), "all keys serialized with dyn: prefix");
            AssertEqual(100, (int)dict["dyn:hp"]!, "hp value");

            var bb2 = new Blackboard();
            bb2.Deserialize(dict);
            AssertEqual(100, bb2.GetInt("hp"), "roundtrip hp");
            AssertTrue(bb2.GetBool("alive"), "roundtrip alive");
            Near(5.0f, bb2.GetFloat("speed"), 1e-5f, "roundtrip speed");
        }

        // ── BTNodeV2 基类 ──

        public static void TestNodeDefaults()
        {
            var n = new BTNoOp();
            AssertEqual("NoOp", n.Name, "default name");
            AssertTrue(n.Parent == null, "root has no parent");
            AssertEqual(0, n.Children.Count, "no children");
            AssertEqual(BTNodeStatus.NotStarted, n.Status, "default status");
            AssertFalse(n.IsRunning, "not running");
        }

        public static void TestNodeAddChild()
        {
            var parent = new BTSequence();
            var c1 = new BTNoOp(); var c2 = new BTNoOp();
            parent.AddChild(c1); parent.AddChild(c2);
            AssertEqual(2, parent.Children.Count, "child count");
            AssertTrue(ReferenceEquals(c1.Parent, parent), "c1 parent");
            AssertTrue(ReferenceEquals(c2.Parent, parent), "c2 parent");
        }

        public static void TestNodeReset()
        {
            var seq = new BTSequence();
            var task = new BTTask("t", _ => BTNodeStatus.Running);
            seq.AddChild(task);
            var ctx = MakeCtx();
            task.Execute(ctx);
            AssertEqual(BTNodeStatus.Running, task.Status, "task running");
            seq.Reset();
            AssertEqual(BTNodeStatus.NotStarted, task.Status, "task reset");
            AssertEqual(BTNodeStatus.NotStarted, seq.Status, "seq reset");
        }

        public static void TestNodeAbort()
        {
            bool abortCalled = false;
            // 用 BTWait 进入 Running 状态
            var wait = new BTWait(1.0f);
            var deco = new TestAbortDecorator(wait, () => abortCalled = true);
            var ctx = MakeCtx();
            wait.Execute(ctx); // 进入 Running
            AssertTrue(wait.IsRunning, "wait running");
            deco.Abort(ctx);
            AssertTrue(abortCalled, "OnAbort callback");
            AssertEqual(BTNodeStatus.Aborted, deco.Status, "deco aborted");
        }

        // 测试用装饰器，注入 OnAbort 回调；Execute 透传并跟随子节点 Status
        private sealed class TestAbortDecorator : BTDecoratorV2
        {
            private readonly Action _onAbort;
            public TestAbortDecorator(BTNodeV2 child, Action onAbort) : base(child) { _onAbort = onAbort; Name = "TestAbort"; }
            public override BTNodeStatus Execute(BTContext context)
            {
                var s = Child!.Execute(context);
                Status = s;
                return s;
            }
            public override void OnAbort(BTContext context) => _onAbort();
        }

        // ── Composite: Sequence ──

        public static void TestSequenceAllSuccess()
        {
            var seq = new BTSequence(new BTNoOp(), new BTNoOp(), new BTNoOp());
            var ctx = MakeCtx();
            // 每 tick 只评估一个子节点
            AssertEqual(BTNodeStatus.Running, seq.Execute(ctx), "tick 1 Running");
            AssertEqual(BTNodeStatus.Running, seq.Execute(ctx), "tick 2 Running");
            AssertEqual(BTNodeStatus.Success, seq.Execute(ctx), "tick 3 all done Success");
        }

        public static void TestSequenceFailure()
        {
            var seq = new BTSequence(
                new BTTask("ok", _ => BTNodeStatus.Success),
                new BTTask("fail", _ => BTNodeStatus.Failure),
                new BTTask("never", _ => BTNodeStatus.Success));
            var ctx = MakeCtx();
            AssertEqual(BTNodeStatus.Running, seq.Execute(ctx), "first child ok -> Running");
            AssertEqual(BTNodeStatus.Failure, seq.Execute(ctx), "second child fails -> Failure");
        }

        public static void TestSequenceRunningCarriesOver()
        {
            var seq = new BTSequence(
                new BTTask("ok", _ => BTNodeStatus.Success),
                new BTTask("run", _ => BTNodeStatus.Running));
            var ctx = MakeCtx();
            AssertEqual(BTNodeStatus.Running, seq.Execute(ctx), "first tick Running");
            AssertEqual(BTNodeStatus.Running, seq.Execute(ctx), "second child Running");
        }

        public static void TestSequenceStaysRunningUntilAllDone()
        {
            int calls = 0;
            var seq = new BTSequence(
                new BTTask("a", _ => { calls++; return BTNodeStatus.Success; }),
                new BTTask("b", _ => { calls++; return BTNodeStatus.Success; }),
                new BTTask("c", _ => { calls++; return BTNodeStatus.Success; }));
            var ctx = MakeCtx();
            AssertEqual(BTNodeStatus.Running, seq.Execute(ctx), "tick 1");
            AssertEqual(BTNodeStatus.Running, seq.Execute(ctx), "tick 2");
            AssertEqual(BTNodeStatus.Success, seq.Execute(ctx), "tick 3 done");
            AssertEqual(3, calls, "all children executed once");
        }

        // ── Composite: Selector ──

        public static void TestSelectorFirstSuccess()
        {
            int c2 = 0, c3 = 0;
            var sel = new BTSelector(
                new BTTask("win", _ => BTNodeStatus.Success),
                new BTTask("c2", _ => { c2++; return BTNodeStatus.Success; }),
                new BTTask("c3", _ => { c3++; return BTNodeStatus.Success; }));
            var ctx = MakeCtx();
            AssertEqual(BTNodeStatus.Success, sel.Execute(ctx), "first success");
            AssertEqual(0, c2, "c2 skipped");
            AssertEqual(0, c3, "c3 skipped");
        }

        public static void TestSelectorAllFailure()
        {
            var sel = new BTSelector(
                new BTTask("f1", _ => BTNodeStatus.Failure),
                new BTTask("f2", _ => BTNodeStatus.Failure));
            var ctx = MakeCtx();
            // 每 tick 评估一个子节点：失败后推进 _currentChild 并返 Running
            AssertEqual(BTNodeStatus.Running, sel.Execute(ctx), "first child failed -> Running");
            // 第二 tick 评估最后一个子节点也失败，推进后 _currentChild >= Count，返 Failure 并复位
            AssertEqual(BTNodeStatus.Failure, sel.Execute(ctx), "all children failed -> Failure");
        }

        public static void TestSelectorFallsThroughFailure()
        {
            int c2 = 0;
            var sel = new BTSelector(
                new BTTask("f", _ => BTNodeStatus.Failure),
                new BTTask("w", _ => { c2++; return BTNodeStatus.Success; }));
            var ctx = MakeCtx();
            AssertEqual(BTNodeStatus.Running, sel.Execute(ctx), "first child failed -> advance");
            AssertEqual(BTNodeStatus.Success, sel.Execute(ctx), "second child succeeds");
            AssertEqual(1, c2, "c2 ran once");
        }

        // ── Composite: Parallel ──

        public static void TestParallelSuccessAll()
        {
            var p = new BTParallel { SuccessPolicy = BTParallelPolicy.All };
            p.AddChild(new BTTask("a", _ => BTNodeStatus.Success));
            p.AddChild(new BTTask("b", _ => BTNodeStatus.Success));
            var ctx = MakeCtx();
            AssertEqual(BTNodeStatus.Success, p.Execute(ctx), "all success");
        }

        public static void TestParallelSuccessAllOneFails()
        {
            var p = new BTParallel { SuccessPolicy = BTParallelPolicy.All };
            p.AddChild(new BTTask("a", _ => BTNodeStatus.Success));
            p.AddChild(new BTTask("b", _ => BTNodeStatus.Failure));
            var ctx = MakeCtx();
            AssertEqual(BTNodeStatus.Failure, p.Execute(ctx), "one fail + All policy -> Failure");
        }

        public static void TestParallelSuccessAny()
        {
            var p = new BTParallel { SuccessPolicy = BTParallelPolicy.Any, FailurePolicy = BTParallelPolicy.All };
            p.AddChild(new BTTask("a", _ => BTNodeStatus.Failure));
            p.AddChild(new BTTask("b", _ => BTNodeStatus.Success));
            var ctx = MakeCtx();
            AssertEqual(BTNodeStatus.Success, p.Execute(ctx), "any success -> Success");
        }

        public static void TestParallelRunningChild()
        {
            var p = new BTParallel();
            p.AddChild(new BTTask("a", _ => BTNodeStatus.Success));
            p.AddChild(new BTTask("b", _ => BTNodeStatus.Running));
            var ctx = MakeCtx();
            AssertEqual(BTNodeStatus.Running, p.Execute(ctx), "running child -> Running");
        }

        // ── Composite: Random ──

        public static void TestRandomSelectorEventuallySucceeds()
        {
            // 随机选择器：其中含一个必成功节点 + 多个失败节点，多次 tick 至少成功一次
            var sel = new BTRandomSelector(
                new BTTask("f1", _ => BTNodeStatus.Failure),
                new BTTask("f2", _ => BTNodeStatus.Failure),
                new BTTask("win", _ => BTNodeStatus.Success));
            var ctx = MakeCtx();
            bool succeeded = false;
            for (int i = 0; i < 20 && !succeeded; i++)
            {
                sel.Reset();
                if (sel.Execute(ctx) == BTNodeStatus.Success) succeeded = true;
            }
            AssertTrue(succeeded, "random selector should eventually pick win");
        }

        public static void TestWeightedRandomSelectsOnlyChild()
        {
            // 只有一个子节点，权重随便
            var sel = new BTWeightedRandomSelector();
            sel.Weights.Add(1.0f);
            sel.AddChild(new BTTask("only", _ => BTNodeStatus.Success));
            var ctx = MakeCtx();
            AssertEqual(BTNodeStatus.Success, sel.Execute(ctx), "single child selected");
        }

        public static void TestWeightedRandomNoChildren()
        {
            var sel = new BTWeightedRandomSelector();
            var ctx = MakeCtx();
            AssertEqual(BTNodeStatus.Failure, sel.Execute(ctx), "no children -> Failure");
        }

        // ── Decorator: Inverter ──

        public static void TestInverter()
        {
            var inv = new BTInverter(new BTTask("a", _ => BTNodeStatus.Success));
            var ctx = MakeCtx();
            AssertEqual(BTNodeStatus.Failure, inv.Execute(ctx), "Success -> Failure");
            inv.Reset();
            var inv2 = new BTInverter(new BTTask("b", _ => BTNodeStatus.Failure));
            AssertEqual(BTNodeStatus.Success, inv2.Execute(ctx), "Failure -> Success");
        }

        public static void TestInverterPassthroughRunning()
        {
            var inv = new BTInverter(new BTTask("r", _ => BTNodeStatus.Running));
            var ctx = MakeCtx();
            AssertEqual(BTNodeStatus.Running, inv.Execute(ctx), "Running passthrough");
        }

        // ── Decorator: Repeater ──

        public static void TestRepeaterCount()
        {
            int calls = 0;
            var rep = new BTRepeater(new BTTask("a", _ => { calls++; return BTNodeStatus.Success; }), 3);
            var ctx = MakeCtx();
            var s = rep.Execute(ctx);
            AssertEqual(BTNodeStatus.Success, s, "repeater completes");
            AssertEqual(3, calls, "repeated 3 times");
        }

        public static void TestRepeaterInfinite()
        {
            int calls = 0;
            // Count=-1 无限重复；用 Running 任务让 repeater 在 Running 后退出
            var rep = new BTRepeater(new BTTask("r", _ => { calls++; if (calls >= 5) return BTNodeStatus.Running; return BTNodeStatus.Success; }), -1);
            var ctx = MakeCtx();
            var s = rep.Execute(ctx);
            AssertEqual(BTNodeStatus.Running, s, "running after 5 successes");
            AssertEqual(5, calls, "called 5 times");
        }

        public static void TestRepeaterFailureStops()
        {
            int calls = 0;
            var rep = new BTRepeater(new BTTask("f", _ => { calls++; return BTNodeStatus.Failure; }), 10);
            var ctx = MakeCtx();
            AssertEqual(BTNodeStatus.Failure, rep.Execute(ctx), "failure stops repeater");
            AssertEqual(1, calls, "only one attempt (RepeatOnFailure=false)");
        }

        public static void TestRepeaterRepeatOnFailure()
        {
            int calls = 0;
            var rep = new BTRepeater(new BTTask("f", _ => { calls++; return BTNodeStatus.Failure; }), 3)
            { RepeatOnFailure = true };
            var ctx = MakeCtx();
            AssertEqual(BTNodeStatus.Success, rep.Execute(ctx), "repeat-on-failure completes count");
            AssertEqual(3, calls, "3 attempts");
        }

        // ── Decorator: Timeout ──

        public static void TestTimeoutElapses()
        {
            var timeout = new BTTimeout(0.5f, new BTWait(10.0f));
            var ctx = MakeCtx(dt: 0.2f);
            AssertEqual(BTNodeStatus.Running, timeout.Execute(ctx), "first tick Running");
            AssertEqual(BTNodeStatus.Running, timeout.Execute(ctx), "second tick still Running (0.4s)");
            AssertEqual(BTNodeStatus.Failure, timeout.Execute(ctx), "third tick timeout (0.6s >= 0.5s)");
        }

        public static void TestTimeoutResetsOnSuccess()
        {
            int call = 0;
            var timeout = new BTTimeout(1.0f, new BTTask("insta", _ => { call++; return BTNodeStatus.Success; }));
            var ctx = MakeCtx(dt: 0.5f);
            AssertEqual(BTNodeStatus.Success, timeout.Execute(ctx), "immediate success resets timer");
            // 再 tick: timer 被重置为 0，然后累加到 0.5，仍 < 1.0
            AssertEqual(BTNodeStatus.Success, timeout.Execute(ctx), "second call also success");
            AssertEqual(2, call, "child called twice");
        }

        // ── Decorator: CoolDown ──

        public static void TestCoolDownBlocksAfterSuccess()
        {
            int calls = 0;
            var cd = new BTCoolDown(1.0f, new BTTask("a", _ => { calls++; return BTNodeStatus.Success; }));
            var ctx = MakeCtx(dt: 0.5f);
            AssertEqual(BTNodeStatus.Success, cd.Execute(ctx), "first success");
            AssertEqual(BTNodeStatus.Failure, cd.Execute(ctx), "cooldown blocks");
            AssertEqual(1, calls, "child not called during cooldown");
            // 再过 1.0s 后 timer 减完，再过一帧才能执行
            cd.Execute(ctx); // timer 1.0 -> 0.5, Failure
            AssertEqual(BTNodeStatus.Success, cd.Execute(ctx), "cooldown expired, success again");
            AssertEqual(2, calls, "child called again");
        }

        // ── Decorator: BlackboardCheck ──

        public static void TestBBCheckEqualBool()
        {
            var bb = new Blackboard(); bb.SetBool("flag", true);
            var check = new BTBlackboardCheck("flag", BlackboardKeyType.Bool, BTBlackboardCheck.ComparisonOp.Equal, true,
                new BTTask("body", _ => BTNodeStatus.Success));
            var ctx = MakeCtx(bb);
            AssertEqual(BTNodeStatus.Success, check.Execute(ctx), "bool equal true");
            // 改成 false
            bb.SetBool("flag", false);
            check.Reset();
            AssertEqual(BTNodeStatus.Failure, check.Execute(ctx), "bool equal false");
        }

        public static void TestBBCheckEqualInt()
        {
            var bb = new Blackboard(); bb.SetInt("hp", 50);
            var check = new BTBlackboardCheck("hp", BlackboardKeyType.Int, BTBlackboardCheck.ComparisonOp.Equal, 50,
                new BTTask("b", _ => BTNodeStatus.Success));
            var ctx = MakeCtx(bb);
            AssertEqual(BTNodeStatus.Success, check.Execute(ctx), "int equal");
            bb.SetInt("hp", 40); check.Reset();
            AssertEqual(BTNodeStatus.Failure, check.Execute(ctx), "int not equal");
        }

        public static void TestBBCheckGreaterLess()
        {
            var bb = new Blackboard(); bb.SetFloat("dist", 10.0f);
            var gt = new BTBlackboardCheck("dist", BlackboardKeyType.Float, BTBlackboardCheck.ComparisonOp.Greater, 5.0f,
                new BTNoOp());
            var lt = new BTBlackboardCheck("dist", BlackboardKeyType.Float, BTBlackboardCheck.ComparisonOp.Less, 5.0f,
                new BTNoOp());
            var ctx = MakeCtx(bb);
            AssertEqual(BTNodeStatus.Success, gt.Execute(ctx), "10 > 5");
            AssertEqual(BTNodeStatus.Failure, lt.Execute(ctx), "10 < 5 fail");
        }

        public static void TestBBCheckIsSetIsNotSet()
        {
            var bb = new Blackboard();
            bb.SetInt("target", 0); // 动态键
            var isSet = new BTBlackboardCheck("target", BlackboardKeyType.Int, BTBlackboardCheck.ComparisonOp.IsSet, null, new BTNoOp());
            var isNotSet = new BTBlackboardCheck("missing", BlackboardKeyType.Int, BTBlackboardCheck.ComparisonOp.IsNotSet, null, new BTNoOp());
            var ctx = MakeCtx(bb);
            AssertEqual(BTNodeStatus.Success, isSet.Execute(ctx), "target is set");
            AssertEqual(BTNodeStatus.Success, isNotSet.Execute(ctx), "missing is not set");
        }

        public static void TestBBCheckNotEqualInt()
        {
            var bb = new Blackboard(); bb.SetInt("state", 1);
            var check = new BTBlackboardCheck("state", BlackboardKeyType.Int, BTBlackboardCheck.ComparisonOp.NotEqual, 2,
                new BTNoOp());
            var ctx = MakeCtx(bb);
            AssertEqual(BTNodeStatus.Success, check.Execute(ctx), "1 != 2");
            bb.SetInt("state", 2); check.Reset();
            AssertEqual(BTNodeStatus.Failure, check.Execute(ctx), "2 != 2 fail");
        }

        // ── Decorator: UntilSuccess ──

        public static void TestUntilSuccessRetries()
        {
            int calls = 0;
            var us = new BTUntilSuccess(new BTTask("f", _ => { calls++; return calls >= 3 ? BTNodeStatus.Success : BTNodeStatus.Failure; }));
            var ctx = MakeCtx();
            // 第一次 tick 失败（attempt 1）→ Running
            AssertEqual(BTNodeStatus.Running, us.Execute(ctx), "first attempt fail -> Running");
            // 第二次 tick 失败（attempt 2）→ Running
            AssertEqual(BTNodeStatus.Running, us.Execute(ctx), "second attempt fail -> Running");
            // 第三次 tick 成功（attempt 3）
            AssertEqual(BTNodeStatus.Success, us.Execute(ctx), "third attempt success");
            AssertEqual(3, calls, "3 attempts total");
        }

        public static void TestUntilSuccessMaxAttempts()
        {
            int calls = 0;
            var us = new BTUntilSuccess(new BTTask("f", _ => { calls++; return BTNodeStatus.Failure; }), 3);
            var ctx = MakeCtx();
            AssertEqual(BTNodeStatus.Running, us.Execute(ctx), "attempt 1 fail");
            AssertEqual(BTNodeStatus.Running, us.Execute(ctx), "attempt 2 fail");
            AssertEqual(BTNodeStatus.Failure, us.Execute(ctx), "attempt 3 fail -> max reached");
            AssertEqual(3, calls, "3 attempts total");
        }

        // ── Decorator: Condition ──

        public static void TestConditionPassesAndFails()
        {
            bool flag = true;
            var cond = new BTCondition(_ => flag, new BTTask("body", _ => BTNodeStatus.Success));
            var ctx = MakeCtx();
            AssertEqual(BTNodeStatus.Success, cond.Execute(ctx), "condition true");
            flag = false; cond.Reset();
            AssertEqual(BTNodeStatus.Failure, cond.Execute(ctx), "condition false");
        }

        // ── Task ──

        public static void TestNoOp()
        {
            var ctx = MakeCtx();
            AssertEqual(BTNodeStatus.Success, new BTNoOp().Execute(ctx), "noop success");
        }

        public static void TestWait()
        {
            var wait = new BTWait(0.5f);
            var ctx = MakeCtx(dt: 0.2f);
            AssertEqual(BTNodeStatus.Running, wait.Execute(ctx), "0.2s Running");
            AssertEqual(BTNodeStatus.Running, wait.Execute(ctx), "0.4s Running");
            AssertEqual(BTNodeStatus.Success, wait.Execute(ctx), "0.6s Success");
        }

        public static void TestWaitForConditionTimeout()
        {
            var wait = new BTWaitForCondition(_ => false) { Timeout = 0.5f };
            var ctx = MakeCtx(dt: 0.3f);
            AssertEqual(BTNodeStatus.Running, wait.Execute(ctx), "still waiting");
            AssertEqual(BTNodeStatus.Failure, wait.Execute(ctx), "timeout");
        }

        public static void TestWaitForConditionMet()
        {
            bool ready = false;
            var wait = new BTWaitForCondition(_ => ready);
            var ctx = MakeCtx(dt: 0.1f);
            AssertEqual(BTNodeStatus.Running, wait.Execute(ctx), "not ready");
            ready = true;
            AssertEqual(BTNodeStatus.Success, wait.Execute(ctx), "ready");
        }

        public static void TestTaskLambda()
        {
            var task = new BTTask("custom", ctx => BTNodeStatus.Success);
            var ctx = MakeCtx();
            AssertEqual(BTNodeStatus.Success, task.Execute(ctx), "lambda success");
        }

        public static void TestTaskExceptionBecomesError()
        {
            var task = new BTTask("throw", _ => throw new InvalidOperationException("boom"));
            var logs = new List<string>();
            var ctx = MakeCtx();
            ctx.LogError = msg => logs.Add(msg);
            var s = task.Execute(ctx);
            AssertEqual(BTNodeStatus.Error, s, "exception -> Error");
            AssertTrue(logs.Count > 0, "error logged");
        }

        public static void TestMoveToReachesTarget()
        {
            var bb = new Blackboard();
            bb.SetVector3("dest", new Vector3(10, 0, 0));
            var moveTo = new BTMoveTo(_ => new Vector3(10, 0, 0), acceptance: 1.0f);
            var ctx = MakeCtx(bb, dt: 0.1f);
            ctx.GetAgentPositionFn = () => new Vector3(9.5f, 0, 0); // 距离 0.5 < 1.0
            AssertEqual(BTNodeStatus.Success, moveTo.Execute(ctx), "within acceptance");
            // 记录了 move target
            AssertEqual(new Vector3(10, 0, 0), bb.GetVector3("BT_MoveTarget"), "move target recorded");
        }

        public static void TestMoveToRunningWhenFar()
        {
            var moveTo = new BTMoveTo(_ => new Vector3(100, 0, 0), acceptance: 0.5f);
            var ctx = MakeCtx(dt: 0.1f);
            ctx.GetAgentPositionFn = () => Vector3.Zero;
            AssertEqual(BTNodeStatus.Running, moveTo.Execute(ctx), "far from target");
        }

        public static void TestMoveToOnAbortCallback()
        {
            bool aborted = false;
            var moveTo = new BTMoveTo(_ => new Vector3(100, 0, 0), 0.5f);
            var ctx = MakeCtx(dt: 0.1f);
            ctx.GetAgentPositionFn = () => Vector3.Zero;
            ctx.OnMoveAborted = () => aborted = true;
            moveTo.Execute(ctx);
            moveTo.OnAbort(ctx);
            AssertTrue(aborted, "abort callback fired");
        }

        public static void TestPlayAnimationNoWait()
        {
            string? played = null;
            var anim = new BTPlayAnimation("Attack", waitComplete: false);
            var ctx = MakeCtx();
            ctx.OnPlayAnimation = name => played = name;
            AssertEqual(BTNodeStatus.Success, anim.Execute(ctx), "no-wait immediate success");
            AssertEqual("Attack", played, "animation triggered");
        }

        public static void TestPlayAnimationWaitComplete()
        {
            string? played = null;
            bool finished = false;
            var anim = new BTPlayAnimation("Hit", waitComplete: true);
            var ctx = MakeCtx(dt: 0.1f);
            ctx.OnPlayAnimation = name => played = name;
            ctx.IsAnimationFinished = _ => finished;
            AssertEqual(BTNodeStatus.Running, anim.Execute(ctx), "playing");
            AssertEqual("Hit", played, "animation triggered");
            finished = true;
            AssertEqual(BTNodeStatus.Success, anim.Execute(ctx), "finished -> Success");
        }

        // ── BehaviorTreeV2 主类 ──

        public static void TestTreeDefaults()
        {
            var tree = new BehaviorTreeV2("MyTree");
            AssertEqual("MyTree", tree.Name, "name");
            AssertTrue(tree.Root == null, "no root");
            AssertTrue(tree.Blackboard != null, "has blackboard");
            AssertTrue(tree.IsActive, "active by default");
            Near(0f, tree.TickInterval, 1e-5f, "tick interval 0");
        }

        public static void TestTreeTickNoContext()
        {
            var tree = new BehaviorTreeV2();
            tree.SetRoot(new BTNoOp());
            AssertEqual(BTNodeStatus.NotStarted, tree.Tick(0.016f), "no context -> NotStarted");
        }

        public static void TestTreeTickInactive()
        {
            var tree = new BehaviorTreeV2();
            tree.SetRoot(new BTNoOp());
            tree.InitializeContext(1, () => Vector3.Zero);
            tree.IsActive = false;
            AssertEqual(BTNodeStatus.NotStarted, tree.Tick(0.016f), "inactive -> NotStarted");
        }

        public static void TestTreeTickIntervalThrottles()
        {
            int calls = 0;
            var tree = new BehaviorTreeV2();
            tree.TickInterval = 0.5f;
            tree.SetRoot(new BTTask("count", _ => { calls++; return BTNodeStatus.Success; }));
            tree.InitializeContext(1, () => Vector3.Zero);
            // dt=0.1 不够 0.5 阈值，多次 tick 累积
            for (int i = 0; i < 3; i++) tree.Tick(0.1f);
            AssertEqual(0, calls, "below interval, no tick yet");
            tree.Tick(0.3f); // 累积 0.6 >= 0.5, 触发
            AssertEqual(1, calls, "triggered once");
        }

        public static void TestTreeAbort()
        {
            bool aborted = false;
            var tree2 = new BehaviorTreeV2();
            var abortableTask = new TestAbortDecorator(new BTTask("r", _ => BTNodeStatus.Running), () => aborted = true);
            tree2.SetRoot(abortableTask);
            tree2.InitializeContext(1, () => Vector3.Zero);
            tree2.Tick(0.1f);
            tree2.Abort();
            AssertTrue(aborted, "tree abort propagated");
        }

        public static void TestTreeResetClearsBlackboard()
        {
            var tree = new BehaviorTreeV2();
            tree.SetRoot(new BTNoOp());
            tree.InitializeContext(1, () => Vector3.Zero);
            tree.Blackboard.SetInt("x", 42);
            tree.Reset();
            AssertEqual(0, tree.Blackboard.GetInt("x"), "blackboard cleared");
        }

        // ── BTBuilder ──

        public static void TestBuilderSimpleSequence()
        {
            int a = 0, b = 0;
            var tree = new BTBuilder("Seq")
                .Sequence()
                    .Task("A", _ => { a++; return BTNodeStatus.Success; })
                    .Task("B", _ => { b++; return BTNodeStatus.Success; })
                .End()
                .Build();
            AssertEqual("Seq", tree.Name, "tree name");
            AssertTrue(tree.Root is BTSequence, "root is sequence");
            tree.InitializeContext(1, () => Vector3.Zero);
            AssertEqual(BTNodeStatus.Running, tree.Tick(0.016f), "tick 1 Running");
            AssertEqual(1, a, "a ran once");
            AssertEqual(BTNodeStatus.Success, tree.Tick(0.016f), "tick 2 Success");
            AssertEqual(1, b, "b ran once");
        }

        public static void TestBuilderSelector()
        {
            var tree = new BTBuilder("Sel")
                .Selector()
                    .Task("fail", _ => BTNodeStatus.Failure)
                    .Task("win", _ => BTNodeStatus.Success)
                .End()
                .Build();
            tree.InitializeContext(1, () => Vector3.Zero);
            AssertEqual(BTNodeStatus.Running, tree.Tick(0.016f), "first child failed -> advance");
            AssertEqual(BTNodeStatus.Success, tree.Tick(0.016f), "second child succeeds");
        }

        public static void TestBuilderNested()
        {
            int moved = 0;
            var tree = new BTBuilder("Nested")
                .Selector()
                    .Sequence()                       // 尝试一个序列
                        .Task("pre", _ => BTNodeStatus.Failure) // 直接失败
                    .End()                            // 结束 Sequence
                    .Sequence()                       // 备选序列
                        .Task("check", _ => BTNodeStatus.Success)
                        .Task("move", _ => { moved++; return BTNodeStatus.Success; })
                    .End()
                .End()
                .Build();
            tree.InitializeContext(1, () => Vector3.Zero);
            // 每 tick 只走一个子节点，最终走到 move
            var s = tree.Tick(0.016f); // selector starts first seq; seq evaluates pre=Failure -> seq fails, selector advances
            // tick 2: selector evaluates second seq; seq evaluates check=Success, advances
            tree.Tick(0.016f);
            // tick 3: seq evaluates move -> Success, seq returns Success, selector returns Success
            var s3 = tree.Tick(0.016f);
            AssertEqual(BTNodeStatus.Success, s3, "nested fallback succeeds after 3 ticks");
            AssertEqual(1, moved, "move task ran");
        }

        public static void TestBuilderDecorators()
        {
            int runs = 0;
            var tree = new BTBuilder("Deco")
                .Sequence()
                    .Condition(_ => true)             // 通过
                        .Task("body", _ => { runs++; return BTNodeStatus.Success; })
                    .End()
                    .Inverter()
                        .Task("never", _ => BTNodeStatus.Success) // 反转后失败
                    .End()
                .End()
                .Build();
            tree.InitializeContext(1, () => Vector3.Zero);
            // tick 1: Sequence starts first child (Condition) -> executes body -> Success; Sequence advances to Running
            tree.Tick(0.016f);
            AssertEqual(1, runs, "body ran once");
            // tick 2: Sequence evaluates Inverter -> Inverter(Task Success)=Failure -> Sequence fails
            AssertEqual(BTNodeStatus.Failure, tree.Tick(0.016f), "inverter makes seq fail");
        }

        public static void TestBuilderBlackboardCheckNode()
        {
            var tree = new BTBuilder("BB")
                .Sequence()
                    .BlackboardCheck("hp", BlackboardKeyType.Int, BTBlackboardCheck.ComparisonOp.Greater, 50)
                        .Task("attack", _ => BTNodeStatus.Success)
                    .End()
                .End()
                .Build();
            tree.InitializeContext(1, () => Vector3.Zero);
            // 默认 hp=0 → 检查失败 → Sequence Failure
            AssertEqual(BTNodeStatus.Failure, tree.Tick(0.016f), "hp too low");
            // 设置 hp=100（不调用 Reset 以避免清空黑板）
            tree.Blackboard.SetInt("hp", 100);
            // Sequence 在 Failure 后会复位 _currentChild=0，同一 tick 内 BlackboardCheck 通过→attack 立即执行→Success
            AssertEqual(BTNodeStatus.Success, tree.Tick(0.016f), "hp high enough -> Success");
        }

        public static void TestBuilderUnclosedThrows()
        {
            bool threw = false;
            try
            {
                new BTBuilder("x").Sequence().Task("a", _ => BTNodeStatus.Success).Build();
            }
            catch (InvalidOperationException) { threw = true; }
            AssertTrue(threw, "unclosed Sequence throws");
        }

        public static void TestBuilderEmptyThrows()
        {
            bool threw = false;
            try { new BTBuilder("empty").Build(); }
            catch (InvalidOperationException) { threw = true; }
            AssertTrue(threw, "empty tree throws");
        }

        public static void TestBuilderWaitAndMoveTo()
        {
            Vector3? lastMoveTarget = null;
            var tree = new BTBuilder("Move")
                .Sequence()
                    .MoveTo(_ => new Vector3(0, 0, 0), acceptance: 100.0f)
                    .Wait(0.05f)
                .End()
                .Build();
            tree.InitializeContext(1, () => Vector3.Zero);
            tree.SetCallbacks(onMove: v => lastMoveTarget = v);
            // 移动在 acceptance 内立即成功，然后 Wait 累积 dt
            var s1 = tree.Tick(0.02f);
            AssertEqual(BTNodeStatus.Running, s1, "wait still running");
            AssertTrue(lastMoveTarget.HasValue, "move target callback fired");
            AssertEqual(Vector3.Zero, lastMoveTarget.Value, "move target is zero");
            var s2 = tree.Tick(0.05f);
            AssertEqual(BTNodeStatus.Success, s2, "wait completed");
        }
    }
}
