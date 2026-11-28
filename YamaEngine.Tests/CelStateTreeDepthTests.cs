using System;
using System.Collections.Generic;
using System.Numerics;
using YamaEngine.AI;
using YamaEngine.ECS;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests
{
    /// <summary>
    /// StateTree（状态树）深度测试。对齐 UE5 StateTree。
    /// 覆盖：枚举 / Handle / Task 生命周期 / 内置任务 / 条件 / 转移 / 资产 / 实例 / ECS 系统 / 调试器。
    /// </summary>
    public static class CelStateTreeDepthTests
    {
        private static void Near(float want, float got, float tol, string msg)
        {
            if (MathF.Abs(want - got) > tol) throw new Exception($"{msg}: want {want}, got {got}");
        }
        private static void AssertFalse(bool cond, string msg) { if (cond) throw new Exception(msg); }

        private static StateTreeContext MakeCtx(Blackboard bb, float dt = 0.1f, EntityComponentSystem? ecs = null)
        {
            var ctx = new StateTreeContext(1, bb) { DeltaTime = dt, WorldTime = 10f, ECS = ecs };
            return ctx;
        }

        /// <summary>记录生命周期回调的自定义任务（测试用）。</summary>
        private sealed class RecordingTask : StateTreeTask
        {
            public List<string> Log = new();
            public EStateTreeRunStatus TickResult = EStateTreeRunStatus.Running;
            public RecordingTask(string name) { Name = name; }
            public override void Enter(StateTreeContext ctx) { Log.Add("Enter"); base.Enter(ctx); }
            public override EStateTreeRunStatus Tick(StateTreeContext ctx) { Log.Add("Tick"); return TickResult; }
            public override void Exit(StateTreeContext ctx, EStateTreeRunStatus status) { Log.Add("Exit"); base.Exit(ctx, status); }
        }

        /// <summary>始终满足的条件（测试用）。</summary>
        private sealed class TrueCondition : StateTreeCondition
        {
            public TrueCondition() { Name = "True"; }
            public override bool Evaluate(StateTreeContext ctx) => true;
        }

        /// <summary>始终不满足的条件。</summary>
        private sealed class FalseCondition : StateTreeCondition
        {
            public FalseCondition() { Name = "False"; }
            public override bool Evaluate(StateTreeContext ctx) => false;
        }

        /// <summary>构造一个含两个状态、一个转移的资产。</summary>
        private static StateTreeAsset MakeTwoStateAsset()
        {
            var asset = new StateTreeAsset { Name = "Test", RootState = "A" };
            var a = new StateTreeState { Name = "A" };
            var trans = new StateTreeTransition { TargetState = "B" };
            trans.Conditions.Add(new TimerCondition { Duration = 0.5f });
            a.Transitions.Add(trans);
            asset.AddState(a);
            var b = new StateTreeState { Name = "B" };
            b.Tasks.Add(new WaitTask { WaitDuration = 1f });
            asset.AddState(b);
            return asset;
        }

        // ── 枚举 ──

        public static void TestEnumTransitionType()
        {
            AssertEqual(7, Enum.GetValues<EStateTreeTransitionType>().Length, "7 transition types");
            AssertEqual(EStateTreeTransitionType.GotoState, (EStateTreeTransitionType)0, "GotoState=0");
        }

        public static void TestEnumRunStatus()
        {
            AssertEqual(4, Enum.GetValues<EStateTreeRunStatus>().Length, "4 run status");
            AssertEqual(EStateTreeRunStatus.Running, (EStateTreeRunStatus)0, "Running=0");
        }

        public static void TestEnumComparisonOp()
        {
            AssertEqual(7, Enum.GetValues<EStateTreeComparisonOp>().Length, "7 comparison ops");
        }

        public static void TestEnumBlackboardValueType()
        {
            AssertEqual(6, Enum.GetValues<BlackboardValueType>().Length, "6 value types");
        }

        // ── Handle ──

        public static void TestHandleInvalidDefault()
        {
            var h = default(StateTreeHandle);
            AssertFalse(h.IsValid, "default handle invalid");
        }

        public static void TestHandleWithNullInstance()
        {
            var h = new StateTreeHandle(5, null);
            AssertFalse(h.IsValid, "null instance invalid");
            AssertEqual("", h.GetCurrentState(), "null instance empty state");
            AssertEqual(EStateTreeRunStatus.Uninitialized, h.GetStatus(), "null instance uninitialized");
        }

        public static void TestHandleWithZeroEntity()
        {
            var asset = new StateTreeAsset { Name = "T", RootState = "A" };
            asset.AddState(new StateTreeState { Name = "A" });
            var inst = new StateTreeInstance(asset, new Blackboard());
            var h = new StateTreeHandle(0, inst);
            AssertFalse(h.IsValid, "zero entity invalid");
        }

        // ── Task 生命周期 ──

        public static void TestTaskDefaults()
        {
            var t = new RecordingTask("Test");
            AssertEqual("Test", t.Name, "name");
            AssertFalse(t.bIsActive, "not active by default");
            AssertEqual("Inactive", t.GetDebugInfo(), "debug info inactive");
        }

        public static void TestTaskEnterExitToggle()
        {
            var t = new RecordingTask("T");
            var ctx = MakeCtx(new Blackboard());
            t.Enter(ctx);
            AssertTrue(t.bIsActive, "active after Enter");
            AssertEqual("Active", t.GetDebugInfo(), "debug info active");
            t.Exit(ctx, EStateTreeRunStatus.Succeeded);
            AssertFalse(t.bIsActive, "inactive after Exit");
        }

        // ── WaitTask ──

        public static void TestWaitTask()
        {
            var t = new WaitTask { WaitDuration = 0.5f };
            var ctx = MakeCtx(new Blackboard(), dt: 0.3f);
            t.Enter(ctx);
            AssertTrue(t.bIsActive, "active");
            AssertEqual(EStateTreeRunStatus.Running, t.Tick(ctx), "0.3s < 0.5s Running");
            AssertEqual(EStateTreeRunStatus.Succeeded, t.Tick(ctx), "0.6s >= 0.5s Succeeded");
        }

        public static void TestWaitTaskSucceeds()
        {
            var t = new WaitTask { WaitDuration = 0.5f };
            var ctx = MakeCtx(new Blackboard(), dt: 0.25f);
            t.Enter(ctx);
            AssertEqual(EStateTreeRunStatus.Running, t.Tick(ctx), "0.25s Running");
            AssertEqual(EStateTreeRunStatus.Succeeded, t.Tick(ctx), "0.5s borderline → Succeeded");
            AssertEqual(EStateTreeRunStatus.Succeeded, t.Tick(ctx), "0.75s Succeeded");
        }

        public static void TestWaitTaskResetsOnEnter()
        {
            var t = new WaitTask { WaitDuration = 1f };
            var ctx = MakeCtx(new Blackboard(), dt: 0.5f);
            t.Enter(ctx);
            t.Tick(ctx);
            t.Exit(ctx, EStateTreeRunStatus.Running);
            t.Enter(ctx);
            // 重进后计时器归零
            AssertEqual(EStateTreeRunStatus.Running, t.Tick(ctx), "0.5s after re-enter, still Running");
        }

        // ── SetBlackboardTask ──

        public static void TestSetBlackboardBool()
        {
            var bb = new Blackboard();
            var t = new SetBlackboardTask { Key = "flag", ValueType = BlackboardValueType.Boolean, BoolValue = true };
            var ctx = MakeCtx(bb);
            t.Enter(ctx);
            AssertTrue(bb.GetBool("flag"), "bool set on enter");
            AssertEqual(EStateTreeRunStatus.Succeeded, t.Tick(ctx), "tick returns Succeeded");
        }

        public static void TestSetBlackboardInt()
        {
            var bb = new Blackboard();
            var t = new SetBlackboardTask { Key = "count", ValueType = BlackboardValueType.Integer, IntValue = 42 };
            t.Enter(MakeCtx(bb));
            AssertEqual(42, bb.GetInt("count"), "int set");
        }

        public static void TestSetBlackboardFloat()
        {
            var bb = new Blackboard();
            var t = new SetBlackboardTask { Key = "speed", ValueType = BlackboardValueType.Float, FloatValue = 3.5f };
            t.Enter(MakeCtx(bb));
            Near(3.5f, bb.GetFloat("speed"), 1e-5f, "float set");
        }

        public static void TestSetBlackboardString()
        {
            var bb = new Blackboard();
            var t = new SetBlackboardTask { Key = "name", ValueType = BlackboardValueType.String, StringValue = "hello" };
            t.Enter(MakeCtx(bb));
            AssertEqual("hello", bb.GetDynamicValue<string>("name") ?? "", "string set");
        }

        public static void TestSetBlackboardVector3()
        {
            var bb = new Blackboard();
            var v = new Vector3(1, 2, 3);
            var t = new SetBlackboardTask { Key = "pos", ValueType = BlackboardValueType.Vector3, Vector3Value = v };
            t.Enter(MakeCtx(bb));
            AssertEqual(v, bb.GetVector3("pos"), "vec set");
        }

        public static void TestSetBlackboardEntityId()
        {
            var bb = new Blackboard();
            var t = new SetBlackboardTask { Key = "target", ValueType = BlackboardValueType.EntityId, EntityIdValue = 7 };
            t.Enter(MakeCtx(bb));
            AssertEqual(7, bb.GetEntityId("target"), "entity set");
        }

        // ── PlayAnimationStateTask ──

        public static void TestPlayAnimationNonLoop()
        {
            var bb = new Blackboard();
            var t = new PlayAnimationStateTask { AnimationState = "Attack", Duration = 0.5f, bLoop = false };
            var ctx = MakeCtx(bb, dt: 0.3f);
            t.Enter(ctx);
            AssertEqual("Attack", bb.GetDynamicValue<string>("CurrentAnimationState") ?? "", "anim state written");
            AssertEqual(EStateTreeRunStatus.Running, t.Tick(ctx), "0.3s Running");
            AssertEqual(EStateTreeRunStatus.Succeeded, t.Tick(ctx), "0.6s Succeeded");
        }

        public static void TestPlayAnimationLoop()
        {
            var t = new PlayAnimationStateTask { AnimationState = "Idle", Duration = 0.1f, bLoop = true };
            var ctx = MakeCtx(new Blackboard(), dt: 0.5f);
            t.Enter(ctx);
            // 循环模式：永远 Running
            AssertEqual(EStateTreeRunStatus.Running, t.Tick(ctx), "loop always Running");
            AssertEqual(EStateTreeRunStatus.Running, t.Tick(ctx), "still Running");
        }

        // ── BlackboardCondition ──

        public static void TestBBConditionEqual()
        {
            var bb = new Blackboard();
            bb.SetFloat("hp", 50f);
            var ctx = MakeCtx(bb);
            var c = new BlackboardCondition { Key = "hp", Op = EStateTreeComparisonOp.Equal, CompareValue = 50f };
            AssertTrue(c.Evaluate(ctx), "50 == 50");
            c.CompareValue = 60f;
            AssertFalse(c.Evaluate(ctx), "50 != 60");
        }

        public static void TestBBConditionMissingKey()
        {
            var c = new BlackboardCondition { Key = "nonexistent", Op = EStateTreeComparisonOp.Equal, CompareValue = 0f };
            AssertFalse(c.Evaluate(MakeCtx(new Blackboard())), "missing key → false");
        }

        public static void TestBBConditionGreaterThan()
        {
            var bb = new Blackboard();
            bb.SetFloat("score", 100f);
            var ctx = MakeCtx(bb);
            var c = new BlackboardCondition { Key = "score", Op = EStateTreeComparisonOp.Greater, CompareValue = 50f };
            AssertTrue(c.Evaluate(ctx), "100 > 50");
            c.CompareValue = 100f;
            AssertFalse(c.Evaluate(ctx), "100 > 100 false");
        }

        public static void TestBBConditionInRange()
        {
            var bb = new Blackboard();
            bb.SetFloat("val", 5f);
            var ctx = MakeCtx(bb);
            var c = new BlackboardCondition { Key = "val", Op = EStateTreeComparisonOp.InRange, RangeMin = 1f, RangeMax = 10f };
            AssertTrue(c.Evaluate(ctx), "5 in [1,10]");
            c.RangeMin = 6f;
            AssertFalse(c.Evaluate(ctx), "5 not in [6,10]");
        }

        // ── TimerCondition ──

        public static void TestTimerConditionNotYet()
        {
            var ctx = MakeCtx(new Blackboard());
            ctx.StateData.Reset(0f);
            var c = new TimerCondition { Duration = 2f };
            AssertFalse(c.Evaluate(ctx), "0s < 2s");
        }

        public static void TestTimerConditionExpired()
        {
            var ctx = MakeCtx(new Blackboard());
            ctx.StateData.Reset(0f);
            ctx.StateData.Advance(3f); // 累计 3s
            var c = new TimerCondition { Duration = 2f };
            AssertTrue(c.Evaluate(ctx), "3s >= 2s");
        }

        // ── HasTargetCondition ──

        public static void TestHasTargetNoKey()
        {
            var c = new HasTargetCondition { TargetEntityKey = "Target" };
            AssertFalse(c.Evaluate(MakeCtx(new Blackboard())), "no key → false");
        }

        public static void TestHasTargetZeroId()
        {
            var bb = new Blackboard();
            bb.SetEntityId("Target", 0);
            AssertFalse(new HasTargetCondition().Evaluate(MakeCtx(bb)), "zero id → false");
        }

        // ── HealthCondition ──

        public static void TestHealthConditionLowHp()
        {
            var bb = new Blackboard();
            bb.SetFloat("Health", 20f);
            var c = new HealthCondition { HealthKey = "Health", Op = EStateTreeComparisonOp.LessEqual, Threshold = 30f };
            AssertTrue(c.Evaluate(MakeCtx(bb)), "20 <= 30");
        }

        public static void TestHealthConditionHighHp()
        {
            var bb = new Blackboard();
            bb.SetFloat("Health", 80f);
            var c = new HealthCondition { HealthKey = "Health", Op = EStateTreeComparisonOp.LessEqual, Threshold = 30f };
            AssertFalse(c.Evaluate(MakeCtx(bb)), "80 > 30 → false");
        }

        // ── Transition 评估 ──

        public static void TestTransitionEmptyConditionsAlwaysTrue()
        {
            var t = new StateTreeTransition();
            AssertTrue(t.Evaluate(MakeCtx(new Blackboard())), "no conditions → always true");
        }

        public static void TestTransitionCanTransitionGate()
        {
            var t = new StateTreeTransition();
            t.Conditions.Add(new TrueCondition());
            t.bCanTransition = false;
            AssertFalse(t.Evaluate(MakeCtx(new Blackboard())), "gated off → false");
        }

        public static void TestTransitionAndLogic()
        {
            var t = new StateTreeTransition();
            t.Conditions.Add(new TrueCondition());
            t.Conditions.Add(new FalseCondition());
            AssertFalse(t.Evaluate(MakeCtx(new Blackboard())), "AND: one false → false");
        }

        // ── StateTreeState ──

        public static void TestStateEvaluateEnterConditions()
        {
            var s = new StateTreeState { Name = "A" };
            s.EnterConditions.Add(new TrueCondition());
            AssertTrue(s.EvaluateEnterConditions(MakeCtx(new Blackboard())), "enter cond met");
            var s2 = new StateTreeState { Name = "B" };
            s2.EnterConditions.Add(new FalseCondition());
            AssertFalse(s2.EvaluateEnterConditions(MakeCtx(new Blackboard())), "enter cond not met");
        }

        public static void TestStateSortTransitions()
        {
            var s = new StateTreeState { Name = "A" };
            var t1 = new StateTreeTransition { TargetState = "Low", Priority = 10 };
            var t2 = new StateTreeTransition { TargetState = "High", Priority = 1 };
            s.Transitions.Add(t1);
            s.Transitions.Add(t2);
            s.SortTransitions();
            AssertEqual("High", s.Transitions[0].TargetState, "priority 1 first");
            AssertEqual("Low", s.Transitions[1].TargetState, "priority 10 second");
        }

        // ── StateTreeAsset ──

        public static void TestAssetAddAndGetState()
        {
            var a = new StateTreeAsset { Name = "AI" };
            a.AddState(new StateTreeState { Name = "Patrol" });
            a.AddState(new StateTreeState { Name = "Chase" });
            AssertEqual(2, a.States.Count, "2 states");
            AssertTrue(a.GetState("Patrol") != null, "found Patrol");
            AssertTrue(a.GetState("Missing") == null, "missing returns null");
        }

        public static void TestAssetNextState()
        {
            var a = new StateTreeAsset { Name = "Seq", RootState = "A" };
            a.AddState(new StateTreeState { Name = "A" });
            a.AddState(new StateTreeState { Name = "B" });
            a.AddState(new StateTreeState { Name = "C" });
            AssertEqual("B", a.GetNextStateName("A"), "A→B");
            AssertEqual("C", a.GetNextStateName("B"), "B→C");
            AssertTrue(a.GetNextStateName("C") == null, "C is last → null");
        }

        public static void TestAssetDuplicateStateOverwrites()
        {
            var a = new StateTreeAsset { Name = "AI", RootState = "A" };
            a.AddState(new StateTreeState { Name = "A" });
            a.AddState(new StateTreeState { Name = "A" }); // 覆盖
            AssertEqual(1, a.States.Count, "still 1 state");
            AssertEqual(1, a.StateOrder.Count, "order not duplicated");
        }

        // ── StateTreeInstance ──

        public static void TestInstanceBind()
        {
            var inst = new StateTreeInstance(null, new Blackboard());
            var asset = MakeTwoStateAsset();
            AssertTrue(inst.Bind(asset), "bind success");
            AssertEqual("Test", inst.AssetName, "asset name");
        }

        public static void TestInstanceBindEmptyAsset()
        {
            var inst = new StateTreeInstance(null, new Blackboard());
            var empty = new StateTreeAsset { Name = "Empty" };
            AssertFalse(inst.Bind(empty), "empty asset bind fails");
        }

        public static void TestInstanceStart()
        {
            var asset = MakeTwoStateAsset();
            var inst = new StateTreeInstance(asset, new Blackboard());
            inst.Start();
            AssertTrue(inst.bStarted, "started");
            AssertEqual(EStateTreeRunStatus.Running, inst.Status, "running");
            AssertEqual("A", inst.GetCurrentState(), "entered root state A");
        }

        public static void TestInstanceStartNoAsset()
        {
            var inst = new StateTreeInstance(null, new Blackboard());
            inst.Start();
            AssertEqual(EStateTreeRunStatus.Failed, inst.Status, "no asset → Failed");
        }

        public static void TestInstanceStop()
        {
            var asset = MakeTwoStateAsset();
            var inst = new StateTreeInstance(asset, new Blackboard());
            inst.Start();
            inst.Stop();
            AssertFalse(inst.bStarted, "stopped");
            AssertEqual(EStateTreeRunStatus.Uninitialized, inst.Status, "uninitialized after stop");
            AssertEqual("", inst.GetCurrentState(), "no current state");
        }

        public static void TestInstanceTickTransitionsAfterTimer()
        {
            var asset = MakeTwoStateAsset();
            var inst = new StateTreeInstance(asset, new Blackboard());
            inst.Start();
            AssertEqual("A", inst.GetCurrentState(), "in A");
            var ctx = MakeCtx(inst.Blackboard, dt: 0.3f);
            // Tick 0.3s < 0.5s: 仍在 A
            inst.Tick(0.3f, ctx);
            AssertEqual("A", inst.GetCurrentState(), "still A before timer");
            // Tick 0.3s: 累计 0.6s >= 0.5s → 转移到 B
            inst.Tick(0.3f, ctx);
            AssertEqual("B", inst.GetCurrentState(), "transitioned to B");
        }

        public static void TestInstanceReenterState()
        {
            var asset = new StateTreeAsset { Name = "R", RootState = "A" };
            var recA = new RecordingTask("Rec");
            var stateA = new StateTreeState { Name = "A" };
            stateA.Tasks.Add(recA);
            var reenter = new StateTreeTransition { TransitionType = EStateTreeTransitionType.ReenterState };
            reenter.Conditions.Add(new TimerCondition { Duration = 0.3f });
            stateA.Transitions.Add(reenter);
            asset.AddState(stateA);

            var inst = new StateTreeInstance(asset, new Blackboard());
            inst.Start();
            var ctx = MakeCtx(inst.Blackboard, dt: 0.4f);
            inst.Tick(0.4f, ctx);
            AssertEqual("A", inst.GetCurrentState(), "still A after reenter");
            // 应该经历 Enter→Tick→Exit→Enter
            AssertEqual("Enter", recA.Log[0], "first Enter");
            AssertTrue(recA.Log.Contains("Exit"), "Exit called during reenter");
        }

        public static void TestInstanceNextStateWraps()
        {
            var asset = new StateTreeAsset { Name = "Seq", RootState = "A" };
            var a = new StateTreeState { Name = "A" };
            var next = new StateTreeTransition { TransitionType = EStateTreeTransitionType.NextState };
            next.Conditions.Add(new TimerCondition { Duration = 0.2f });
            a.Transitions.Add(next);
            asset.AddState(a);
            asset.AddState(new StateTreeState { Name = "B" });

            var inst = new StateTreeInstance(asset, new Blackboard());
            inst.Start();
            var ctx = MakeCtx(inst.Blackboard, dt: 0.3f);
            inst.Tick(0.3f, ctx);
            AssertEqual("B", inst.GetCurrentState(), "A → B via NextState");
        }

        public static void TestInstancePause()
        {
            var asset = MakeTwoStateAsset();
            var inst = new StateTreeInstance(asset, new Blackboard());
            inst.Start();
            inst.bPaused = true;
            var ctx = MakeCtx(inst.Blackboard, dt: 0.3f);
            inst.Tick(0.3f, ctx);
            // 暂停时不 Tick，状态不变
            AssertEqual("A", inst.GetCurrentState(), "paused, still A");
        }

        public static void TestInstanceSucceededTransition()
        {
            var asset = new StateTreeAsset { Name = "S", RootState = "A" };
            var a = new StateTreeState { Name = "A" };
            var done = new StateTreeTransition { TransitionType = EStateTreeTransitionType.Succeeded };
            done.Conditions.Add(new TimerCondition { Duration = 0.1f });
            a.Transitions.Add(done);
            asset.AddState(a);

            var inst = new StateTreeInstance(asset, new Blackboard());
            inst.Start();
            var ctx = MakeCtx(inst.Blackboard, dt: 0.2f);
            inst.Tick(0.2f, ctx);
            AssertEqual(EStateTreeRunStatus.Succeeded, inst.Status, "succeeded");
            AssertFalse(inst.bStarted, "no longer started");
        }

        public static void TestInstanceFailedTransition()
        {
            var asset = new StateTreeAsset { Name = "F", RootState = "A" };
            var a = new StateTreeState { Name = "A" };
            var fail = new StateTreeTransition { TransitionType = EStateTreeTransitionType.Failed };
            fail.Conditions.Add(new TimerCondition { Duration = 0.1f });
            a.Transitions.Add(fail);
            asset.AddState(a);

            var inst = new StateTreeInstance(asset, new Blackboard());
            inst.Start();
            var ctx = MakeCtx(inst.Blackboard, dt: 0.2f);
            inst.Tick(0.2f, ctx);
            AssertEqual(EStateTreeRunStatus.Failed, inst.Status, "failed");
        }

        public static void TestInstanceReset()
        {
            var asset = MakeTwoStateAsset();
            var inst = new StateTreeInstance(asset, new Blackboard());
            inst.Start();
            // 推进到 B
            var ctx = MakeCtx(inst.Blackboard, dt: 0.6f);
            inst.Tick(0.6f, ctx);
            AssertEqual("B", inst.GetCurrentState(), "in B");
            // Reset 回到 A
            inst.Reset();
            AssertEqual(EStateTreeRunStatus.Running, inst.Status, "running after reset");
            AssertEqual("A", inst.GetCurrentState(), "back to root A");
        }

        public static void TestInstanceStateDataResetsOnEnter()
        {
            var asset = new StateTreeAsset { Name = "D", RootState = "A" };
            var a = new StateTreeState { Name = "A" };
            asset.AddState(a);
            var inst = new StateTreeInstance(asset, new Blackboard());
            inst.Start();
            AssertTrue(inst != null, "instance alive");
        }

        // ── ECS 集成：StateTreeSystem ──

        public static void TestSystemDefaults()
        {
            var sys = new StateTreeSystem();
            AssertEqual("StateTreeSystem", sys.Name, "system name");
            AssertTrue(sys.Navigation == null, "no nav");
            AssertTrue(sys.PhysicsWorld == null, "no physics");
            Near(0f, sys.WorldTime, 1e-5f, "world time 0");
            AssertEqual(0, sys.ActiveInstanceCount, "no active instances");
            AssertTrue(sys.Debugger != null, "has debugger");
        }

        public static void TestSystemAssignAndTick()
        {
            var ecs = new EntityComponentSystem();
            int eid = ecs.CreateEntity();
            var sys = new StateTreeSystem();
            var asset = MakeTwoStateAsset();
            sys.AssignStateTree(ecs, eid, asset);
            AssertTrue(ecs.HasComponent<StateTreeComponent>(eid), "component added");
            // 首次 Update 懒初始化并启动
            sys.Update(0.1f, ecs);
            AssertTrue(sys.ActiveInstanceCount > 0, "active instance");
            var comp = ecs.GetComponent<StateTreeComponent>(eid);
            AssertTrue(comp.Instance != null, "instance created");
            AssertEqual("A", comp.Instance!.GetCurrentState(), "in root state");
        }

        public static void TestSystemTickRateThrottling()
        {
            var ecs = new EntityComponentSystem();
            int eid = ecs.CreateEntity();
            var sys = new StateTreeSystem();
            var asset = new StateTreeAsset { Name = "T", RootState = "A" };
            var task = new RecordingTask("Rec");
            var state = new StateTreeState { Name = "A" };
            state.Tasks.Add(task);
            asset.AddState(state);
            sys.AssignStateTree(ecs, eid, asset, tickRate: 10f); // 10Hz → 0.1s 间隔
            // dt=0.05 < 0.1 间隔 → 不 Tick
            sys.Update(0.05f, ecs);
            var comp = ecs.GetComponent<StateTreeComponent>(eid);
            AssertTrue(comp.Instance != null, "instance created");
            var tickCountBefore = task.Log.Count;
            // 再 Update dt=0.05, 累计 0.1 >= 0.1 → Tick
            sys.Update(0.05f, ecs);
            var tickCountAfter = task.Log.Count;
            AssertTrue(tickCountAfter > tickCountBefore, "ticked after accumulation");
        }

        public static void TestSystemDisabledComponent()
        {
            var ecs = new EntityComponentSystem();
            int eid = ecs.CreateEntity();
            var sys = new StateTreeSystem();
            var asset = MakeTwoStateAsset();
            sys.AssignStateTree(ecs, eid, asset);
            // 禁用组件
            var comp = ecs.GetComponent<StateTreeComponent>(eid);
            comp.bEnabled = false;
            ecs.SetComponent(eid, comp);
            sys.Update(0.1f, ecs);
            AssertEqual(0, sys.ActiveInstanceCount, "disabled → no active instances");
        }

        // ── StateTreeDebugger ──

        public static void TestDebuggerClearFrame()
        {
            var dbg = new StateTreeDebugger();
            var asset = MakeTwoStateAsset();
            var inst = new StateTreeInstance(asset, new Blackboard());
            inst.Start();
            dbg.RecordInstance(42, inst);
            AssertEqual(1, dbg.Snapshots.Count, "1 snapshot");
            dbg.ClearFrame();
            AssertEqual(0, dbg.Snapshots.Count, "cleared");
        }

        public static void TestDebuggerGetSnapshot()
        {
            var dbg = new StateTreeDebugger();
            var asset = MakeTwoStateAsset();
            var inst = new StateTreeInstance(asset, new Blackboard());
            inst.Start();
            dbg.RecordInstance(7, inst);
            var snap = dbg.GetSnapshot(7);
            AssertTrue(snap != null, "found snapshot");
            AssertEqual(7, snap!.EntityId, "entity id");
            AssertEqual("Test", snap.AssetName, "asset name");
            AssertEqual("A", snap.CurrentState, "current state");
            AssertTrue(dbg.GetSnapshot(999) == null, "missing returns null");
        }

        public static void TestDebuggerSummary()
        {
            var dbg = new StateTreeDebugger();
            var asset = MakeTwoStateAsset();
            var inst = new StateTreeInstance(asset, new Blackboard());
            inst.Start();
            dbg.RecordInstance(1, inst);
            var summary = dbg.GetDebugSummary();
            AssertTrue(summary.Contains("StateTree Debug"), "summary header");
            AssertTrue(summary.Contains("Entity#1"), "entity listed");
        }
    }
}
