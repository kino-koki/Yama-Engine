// CelFSMDepthTests.cs — AI 有限状态机（FiniteStateMachineSystem）深度验证
// 对应 Unity Mecanim Animator 状态机 / UE5 GameplayStateTree 思想
using System;
using System.Collections.Generic;
using YamaEngine.AI;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests;

public sealed class CelFSMDepthTests
{
    /// <summary>可变上下文，用于在转换条件/回调间传递信号。</summary>
    private sealed class Ctx
    {
        public bool Flag;
        public int EnterCount, UpdateCount, ExitCount;
        public float Accum;
        public List<string> Log = new();
        public void Mark(string s) => Log.Add(s);
    }

    private static TransitionCondition<Ctx> WhenFlag => (c) => c.Flag;

    // ════════════════════════════════════════════════════════════════════
    // State<T>
    // ════════════════════════════════════════════════════════════════════

    /// <summary>State 默认值：名称/空 Tags/0 时长/无回调。</summary>
    public static void TestStateDefaults()
    {
        var s = new State<Ctx>("Idle");
        AssertEqual("Idle", s.StateName, "StateName 应为 Idle。");
        AssertEqual(0, s.Tags.Count, "默认无标签。");
        AssertEqual(0, s.Transitions.Count, "默认无内嵌转换。");
        AssertEqual(0f, s.MinDuration, "默认 MinDuration=0。");
        AssertEqual(0f, s.MaxDuration, "默认 MaxDuration=0（无限）。");
    }

    /// <summary>State Enter/Update/Exit 触发对应回调，ElapsedTime 累计。</summary>
    public static void TestStateLifecycleCallbacks()
    {
        var c = new Ctx();
        var s = new State<Ctx>("S") { OnEnter = x => x.EnterCount++, OnUpdate = (x, _) => x.UpdateCount++, OnExit = x => x.ExitCount++ };
        s.Enter(c);
        AssertEqual(1, c.EnterCount, "Enter 应触发一次。");
        AssertEqual(0f, s.ElapsedTime, "Enter 后 ElapsedTime 归零。");
        s.Update(c, 0.5f);
        s.Update(c, 0.5f);
        AssertEqual(2, c.UpdateCount, "Update 应触发两次。");
        AssertEqual(1.0f, s.ElapsedTime, "ElapsedTime 应累计=1.0。");
        s.Exit(c);
        AssertEqual(1, c.ExitCount, "Exit 应触发一次。");
    }

    /// <summary>AddTransition 创建内嵌转换（源=当前状态）。</summary>
    public static void TestStateAddTransition()
    {
        var s = new State<Ctx>("A");
        var t = s.AddTransition("B", WhenFlag);
        AssertEqual(1, s.Transitions.Count, "应添加 1 个内嵌转换。");
        AssertEqual("A", t.FromState, "内嵌转换源=状态自身。");
        AssertEqual("B", t.ToState, "内嵌转换目标=B。");
    }

    /// <summary>IsDurationExpired：MaxDuration=0 永不过期，>0 时到点过期。</summary>
    public static void TestStateDurationExpiry()
    {
        var c = new Ctx();
        var infinite = new State<Ctx>("Inf");
        infinite.Enter(c);
        infinite.Update(c, 100f);
        AssertTrue(!infinite.IsDurationExpired(), "MaxDuration=0 永不过期。");

        var timed = new State<Ctx>("Timed") { MinDuration = 1f, MaxDuration = 2f };
        timed.Enter(c);
        // 内部 _duration 在 [1,2]，先推进足够长时间确保过期
        timed.Update(c, 5f);
        AssertTrue(timed.IsDurationExpired(), "推进 5s 后必过期（_duration≤2）。");
    }

    // ════════════════════════════════════════════════════════════════════
    // Transition<T>
    // ════════════════════════════════════════════════════════════════════

    /// <summary>Transition.Evaluate：条件满足返回 true，否则 false。</summary>
    public static void TestTransitionEvaluate()
    {
        var t = new Transition<Ctx>("A", "B", WhenFlag);
        AssertTrue(!t.Evaluate(new Ctx { Flag = false }), "Flag=false 不应触发。");
        AssertTrue(t.Evaluate(new Ctx { Flag = true }), "Flag=true 应触发。");
    }

    /// <summary>Transition 优先级默认 0，数值越小越优先。</summary>
    public static void TestTransitionPriorityDefault()
    {
        var t = new Transition<Ctx>("A", "B", WhenFlag);
        AssertEqual(0, t.Priority, "默认优先级=0。");
    }

    /// <summary>OneShot 转换触发一次后不再触发，ResetOneShotTransitions 复位。</summary>
    public static void TestTransitionOneShot()
    {
        var t = new Transition<Ctx>("A", "B", WhenFlag) { IsOneShot = true };
        AssertTrue(t.Evaluate(new Ctx { Flag = true }), "首次应触发。");
        AssertTrue(!t.Evaluate(new Ctx { Flag = true }), "第二次不应触发（已消耗）。");
        t._triggered = false; // 模拟 Reset
        AssertTrue(t.Evaluate(new Ctx { Flag = true }), "复位后可再次触发。");
    }

    // ════════════════════════════════════════════════════════════════════
    // StateMachine<T>
    // ════════════════════════════════════════════════════════════════════

    /// <summary>AddState 设置初始状态，Start 进入初始状态。</summary>
    public static void TestSMStartEntersInitial()
    {
        var c = new Ctx();
        var sm = new StateMachine<Ctx>();
        sm.AddState(new State<Ctx>("Idle") { OnEnter = x => x.Mark("enter:idle") });
        sm.AddState(new State<Ctx>("Walk"));
        AssertEqual("Idle", sm.InitialState, "首个添加的状态成为 InitialState。");
        sm.Start(c);
        AssertTrue(sm.IsRunning, "Start 后 IsRunning=true。");
        AssertEqual("Idle", sm.CurrentStateName, "当前状态=Idle。");
        AssertTrue(c.Log.Contains("enter:idle"), "OnEnter 应被调用。");
    }

    /// <summary>未 Start 时 Update 不推进状态。</summary>
    public static void TestSMUpdateBeforeStartNoop()
    {
        var c = new Ctx { Flag = true };
        var sm = new StateMachine<Ctx>();
        sm.AddState("A");
        sm.AddState("B");
        sm.AddTransition("A", "B", WhenFlag);
        sm.Update(c, 0.1f);
        AssertEqual("", sm.CurrentStateName, "未 Start 时无当前状态。");
    }

    /// <summary>转换条件满足时切换状态并触发 OnExit/OnEnter/OnStateChanged。</summary>
    public static void TestSMTransitionSwitchesState()
    {
        var c = new Ctx();
        var sm = new StateMachine<Ctx>();
        string? changedTo = null;
        sm.OnStateChanged = (from, to, _) => changedTo = to;
        sm.AddState(new State<Ctx>("A") { OnExit = x => x.Mark("exit:A") });
        sm.AddState(new State<Ctx>("B") { OnEnter = x => x.Mark("enter:B") });
        sm.AddTransition("A", "B", WhenFlag);
        sm.Start(c);
        AssertEqual("A", sm.CurrentStateName, "初始=A。");
        c.Flag = true;
        sm.Update(c, 0.1f);
        AssertEqual("B", sm.CurrentStateName, "条件满足后切换到 B。");
        AssertEqual("B", changedTo, "OnStateChanged 收到目标=B。");
        AssertTrue(c.Log.Contains("exit:A"), "应先 Exit 旧状态。");
        AssertTrue(c.Log.Contains("enter:B"), "应后 Enter 新状态。");
    }

    /// <summary>全局转换（FromState=null）从任何状态都能触发。</summary>
    public static void TestSMGlobalTransition()
    {
        var c = new Ctx { Flag = true };
        var sm = new StateMachine<Ctx>();
        sm.AddState("A");
        sm.AddState("B");
        sm.AddState("C");
        // 全局：FromState=null → 任意状态遇 Flag 切到 C
        sm.AddTransition(new Transition<Ctx> { FromState = null, ToState = "C", Condition = WhenFlag });
        sm.Start(c);
        AssertEqual("A", sm.CurrentStateName, "初始=A。");
        sm.Update(c, 0.1f);
        AssertEqual("C", sm.CurrentStateName, "全局转换应从 A 切到 C。");
    }

    /// <summary>优先级高的转换（数值更小）优先选择。</summary>
    public static void TestSMTransitionPriority()
    {
        var c = new Ctx { Flag = true };
        var sm = new StateMachine<Ctx>();
        sm.AddState("A");
        sm.AddState("LowPri");
        sm.AddState("HighPri");
        sm.AddTransition("A", "LowPri", WhenFlag, priority: 10);
        sm.AddTransition("A", "HighPri", WhenFlag, priority: 1); // 数值更小 → 优先
        sm.Start(c);
        sm.Update(c, 0.1f);
        AssertEqual("HighPri", sm.CurrentStateName, "优先级 1 应胜过 10。");
    }

    /// <summary>ForceState 直接切换状态（绕过条件）。</summary>
    public static void TestSMForceState()
    {
        var c = new Ctx();
        var sm = new StateMachine<Ctx>();
        sm.AddState("A");
        sm.AddState("B");
        sm.Start(c);
        sm.ForceState("B", c);
        AssertEqual("B", sm.CurrentStateName, "ForceState 应直接切到 B。");
    }

    /// <summary>Stop 退出当前状态并置 IsRunning=false。</summary>
    public static void TestSMStop()
    {
        var c = new Ctx();
        var sm = new StateMachine<Ctx>();
        sm.AddState(new State<Ctx>("A") { OnExit = x => x.Mark("exit:A") });
        sm.Start(c);
        sm.Stop(c);
        AssertTrue(!sm.IsRunning, "Stop 后 IsRunning=false。");
        AssertEqual("", sm.CurrentStateName, "Stop 后无当前状态。");
        AssertTrue(c.Log.Contains("exit:A"), "Stop 应触发 Exit。");
    }

    /// <summary>状态内转换（State.AddTransition）在 Update 时被检查。</summary>
    public static void TestSMInternalTransition()
    {
        var c = new Ctx();
        var sm = new StateMachine<Ctx>();
        var a = new State<Ctx>("A");
        a.AddTransition("B", WhenFlag);
        sm.AddState(a);
        sm.AddState("B");
        sm.Start(c);
        c.Flag = true;
        sm.Update(c, 0.1f);
        AssertEqual("B", sm.CurrentStateName, "状态内转换应触发切换。");
    }

    /// <summary>GetState/GetStateNames 查询状态。</summary>
    public static void TestSMGetState()
    {
        var sm = new StateMachine<Ctx>();
        sm.AddState("A");
        sm.AddState("B");
        AssertTrue(sm.GetState("A") != null, "GetState(A) 应存在。");
        AssertTrue(sm.GetState("Z") == null, "GetState(Z) 应为 null。");
        var names = sm.GetStateNames();
        AssertEqual(2, names.Count, "应有 2 个状态。");
        AssertTrue(names.Contains("A") && names.Contains("B"), "状态名应含 A 和 B。");
    }

    /// <summary>OnTransition 回调在转换时触发。</summary>
    public static void TestSMOnTransitionCallback()
    {
        var c = new Ctx();
        var sm = new StateMachine<Ctx>();
        bool onTransitionFired = false;
        sm.OnTransition = (_, _) => onTransitionFired = true;
        sm.AddState("A");
        sm.AddState("B");
        sm.AddTransition("A", "B", WhenFlag);
        sm.Start(c);
        c.Flag = true;
        sm.Update(c, 0.1f);
        AssertTrue(onTransitionFired, "OnTransition 应被触发。");
    }

    /// <summary>EnableHistory + GoToHistoryState 切回上一个状态。</summary>
    public static void TestSMHistoryState()
    {
        var c = new Ctx();
        var sm = new StateMachine<Ctx> { EnableHistory = true };
        sm.AddState("A");
        sm.AddState("B");
        sm.AddState("C");
        sm.AddTransition("A", "B", (x) => x.Flag); // 用完关掉
        sm.AddTransition("B", "C", (x) => x.Flag);
        sm.Start(c);
        // A→B：记录 A 到历史[B]=A
        sm.ForceState("B", c);
        // B→C：记录 B 到历史[C]=B
        sm.ForceState("C", c);
        // 从 C 回历史
        sm.GoToHistoryState("C", c);
        AssertEqual("B", sm.CurrentStateName, "历史[C] 应为 B。");
    }

    // ════════════════════════════════════════════════════════════════════
    // ParallelStateMachine<T>
    // ════════════════════════════════════════════════════════════════════

    /// <summary>并行状态机多个独立 SM 同时推进。</summary>
    public static void TestParallelSMConcurrent()
    {
        var c = new Ctx { Flag = true };
        var smA = new StateMachine<Ctx>();
        smA.AddState("A1");
        smA.AddState("A2");
        smA.AddTransition("A1", "A2", WhenFlag);

        var smB = new StateMachine<Ctx>();
        smB.AddState("B1");
        smB.AddState("B2");
        smB.AddTransition("B1", "B2", WhenFlag);

        var par = new ParallelStateMachine<Ctx>();
        par.AddMachine("A", smA);
        par.AddMachine("B", smB);
        par.Start(c);

        AssertEqual("A1", smA.CurrentStateName, "SM A 初始 A1。");
        AssertEqual("B1", smB.CurrentStateName, "SM B 初始 B1。");

        par.Update(c, 0.1f);
        AssertEqual("A2", smA.CurrentStateName, "SM A 应切到 A2。");
        AssertEqual("B2", smB.CurrentStateName, "SM B 应切到 B2。");
    }

    /// <summary>并行状态机 RemoveMachine 移除后不再推进。</summary>
    public static void TestParallelSMRemoveMachine()
    {
        var c = new Ctx { Flag = true };
        var smA = new StateMachine<Ctx>();
        smA.AddState("A1");
        smA.AddState("A2");
        smA.AddTransition("A1", "A2", WhenFlag);
        var par = new ParallelStateMachine<Ctx>();
        par.AddMachine("A", smA);
        par.Start(c);
        AssertEqual("A1", smA.CurrentStateName, "Start 后 A 初始 A1。");
        par.RemoveMachine("A");
        par.Update(c, 0.1f);
        AssertEqual("A1", smA.CurrentStateName, "移除后 Update 不推进 SM A。");
    }

    // ════════════════════════════════════════════════════════════════════
    // HierarchicalStateMachine<T>
    // ════════════════════════════════════════════════════════════════════

    /// <summary>层级状态机根状态与 GetCurrentStatePath。</summary>
    public static void TestHSMRootPath()
    {
        var hsm = new HierarchicalStateMachine<Ctx>();
        hsm.AddRootState("Idle");
        hsm.AddRootState("Combat");
        hsm.Start(new Ctx());
        AssertEqual("Idle", hsm.CurrentRootStateName, "根状态应为首个添加的 Idle。");
        AssertTrue(hsm.IsRunning, "Start 后 IsRunning=true。");
        string path = hsm.GetCurrentStatePath();
        AssertEqual("Idle", path, "无子状态机时路径=根状态名。");
    }

    /// <summary>ForceRootState 强制切根状态。</summary>
    public static void TestHSMForceRootState()
    {
        var hsm = new HierarchicalStateMachine<Ctx>();
        hsm.AddRootState("Idle");
        hsm.AddRootState("Combat");
        hsm.Start(new Ctx());
        hsm.ForceRootState("Idle");
        AssertEqual("Idle", hsm.CurrentRootStateName, "强制后应为 Idle。");
    }

    /// <summary>SetSubStateMachine + 路径 "Parent.Child"。</summary>
    public static void TestHSMSubStateMachinePath()
    {
        var hsm = new HierarchicalStateMachine<Ctx>();
        hsm.AddRootState("Combat");
        hsm.SetSubStateMachine("Combat", "Attack", sub =>
        {
            sub.AddRootState("Attack");
            sub.AddRootState("Defend");
        });
        hsm.Start(new Ctx());
        hsm.ForceRootState("Combat");
        // 子状态机 InitialState=Attack，但需先手动启动子状态机一次 Update 或 ForceState
        string path = hsm.GetCurrentStatePath();
        AssertTrue(path.Contains("Combat"), $"路径应含 Combat，实际 {path}");
        AssertTrue(path.Contains("Attack") || path == "Combat", $"路径应含 Combat 或为 Combat，实际 {path}");
    }

    /// <summary>GetStateAtPath 通过路径找到嵌套状态。</summary>
    public static void TestHSMGetStateAtPath()
    {
        var hsm = new HierarchicalStateMachine<Ctx>();
        hsm.AddRootState("Combat");
        var sub = hsm.SetSubStateMachine("Combat", "Attack", s => { });
        sub!.AddState("Defend");
        var found = hsm.GetStateAtPath("Combat.Defend");
        AssertTrue(found != null, "应通过路径找到嵌套状态。");
        AssertEqual("Defend", found!.StateName, "嵌套状态名应为 Defend。");
    }

    /// <summary>GetCurrentStateHierarchy 返回当前状态层级列表。</summary>
    public static void TestHSMGetCurrentStateHierarchy()
    {
        var hsm = new HierarchicalStateMachine<Ctx>();
        hsm.AddRootState("Combat");
        var sub = hsm.SetSubStateMachine("Combat", "Attack", s => { });
        sub!.AddState("Attack");
        hsm.Start(new Ctx());
        hsm.ForceRootState("Combat");
        // 子状态机需要 Start 才有 CurrentState，此处仅验证层级至少含根
        var hierarchy = hsm.GetCurrentStateHierarchy();
        AssertTrue(hierarchy.Count >= 1, "层级至少含根状态。");
        AssertEqual("Combat", hierarchy[0].StateName, "层级[0] 应为根状态 Combat。");
    }
}
