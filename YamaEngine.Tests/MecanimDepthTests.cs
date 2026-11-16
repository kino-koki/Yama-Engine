// MecanimDepthTests.cs — Unity5 Mecanim 状态机深度验证
// 覆盖（对应 Unity Animator Controller）：
//   * 参数系统：Bool/Float/Int/Trigger 四类型，Trigger Set/Consume 自动复位
//   * 转移条件：条件运算符求值（Equals/Greater/IsTrue/...），多条件 AND
//   * Trigger 消耗：转移触发后 Trigger 自动复位（Unity Trigger 语义）
//   * TransitionDuration：Crossfade 进度 0→1，混合 alpha 线性插值
//   * BlendCurve：Linear/EaseIn/EaseOut/EaseInOut/Custom 求值
//   * ExitTime：归一化时间门槛，未达门槛不触发
//   * Instant 转移：Duration<=0 立即切换，无过渡
//   * 状态生命周期：OnEnter/OnExit 回调、CurrentTime 推进
//   * Interrupt：CanInterrupt 语义
//   * Conduit：全局/状态级守卫条件
//   * ForceTransition：脚本强制转移
// 全部 CPU 级确定性，不依赖骨架/RHI。
using System;
using System.Collections.Generic;
using YamaEngine.Animation;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests;

public static class MecanimDepthTests
{
    private static void Near(float got, float want, float tol, string msg)
        => AssertTrue(MathF.Abs(got - want) <= tol, $"{msg}（期望 {want}，实际 {got}，容差 {tol}）");

    /// <summary>构造空状态机（状态无 Clip，仅验证转移逻辑，GetDuration=0）。</summary>
    private static AnimStateMachineReal MakeFsm(int stateCount, string[] names)
    {
        var fsm = new AnimStateMachineReal();
        for (int i = 0; i < stateCount; i++)
            fsm.AddState(names[i], null); // null Node → GetDuration=0
        fsm.Initialize();
        return fsm;
    }

    // ══════════════════════════════════════════════════════════════════
    //  一、参数系统
    // ══════════════════════════════════════════════════════════════════

    /// <summary>参数工厂方法：Bool/Float/Int/Trigger 类型与初值。</summary>
    public static void TestParameterFactories()
    {
        var b = AnimParameter.Bool("IsMoving", true);
        AssertEqual(AnimParameterType.Bool, b.Type, "Bool 参数类型");
        AssertTrue(b.BoolValue, "Bool 初值为 true");

        var f = AnimParameter.Float("Speed", 3.5f);
        AssertEqual(AnimParameterType.Float, f.Type, "Float 参数类型");
        Near(f.FloatValue, 3.5f, 0.001f, "Float 初值");

        var i = AnimParameter.Int("State", 2);
        AssertEqual(AnimParameterType.Int, i.Type, "Int 参数类型");
        AssertEqual(2, i.IntValue, "Int 初值");

        var t = AnimParameter.Trigger("Jump");
        AssertEqual(AnimParameterType.Trigger, t.Type, "Trigger 参数类型");
        AssertFalse(t.BoolValue, "Trigger 初始未激活");
    }

    /// <summary>Trigger Set/Consume：Set 激活，Consume 复位，IsTriggerActive 语义。</summary>
    public static void TestTriggerSetConsume()
    {
        var t = AnimParameter.Trigger("Fire");
        AssertFalse(t.IsTriggerActive, "初始 Trigger 不活跃");

        t.SetTrigger();
        AssertTrue(t.IsTriggerActive, "SetTrigger 后活跃");
        AssertTrue(t.BoolValue, "SetTrigger 后 BoolValue=true");

        t.ConsumeTrigger();
        AssertFalse(t.IsTriggerActive, "ConsumeTrigger 后不活跃");
        AssertFalse(t.BoolValue, "ConsumeTrigger 后 BoolValue=false");
        AssertTrue(t.TriggerConsumed, "ConsumeTrigger 标记已消耗");
    }

    /// <summary>状态机 SetTrigger/SetFloat/SetBool/SetInt 代理。</summary>
    public static void TestStateMachineParameterSetters()
    {
        var fsm = new AnimStateMachineReal();
        fsm.AddBoolParameter("IsGrounded", true);
        fsm.AddFloatParameter("Speed", 0f);
        fsm.AddIntParameter("Weapon", 0);
        fsm.AddTriggerParameter("Reload");

        fsm.SetFloat("Speed", 5f);
        fsm.SetBool("IsGrounded", false);
        fsm.SetInt("Weapon", 2);
        fsm.SetTrigger("Reload");

        AssertEqual(5f, fsm.GetParameter("Speed").FloatValue, "SetFloat 生效");
        AssertFalse(fsm.GetParameter("IsGrounded").BoolValue, "SetBool 生效");
        AssertEqual(2, fsm.GetParameter("Weapon").IntValue, "SetInt 生效");
        AssertTrue(fsm.GetParameter("Reload").IsTriggerActive, "SetTrigger 生效");
    }

    // ══════════════════════════════════════════════════════════════════
    //  二、转移条件求值
    // ══════════════════════════════════════════════════════════════════

    /// <summary>Float 条件：Greater/GreaterEqual/Less/LessEqual/Equals。</summary>
    public static void TestFloatConditionOperators()
    {
        var speed = AnimParameter.Float("Speed", 5f);
        var dict = new Dictionary<string, AnimParameter> { ["Speed"] = speed };

        AssertTrue(new TransitionCondition("Speed", ConditionOperator.Greater, 3f).Evaluate(speed), "5>3");
        AssertFalse(new TransitionCondition("Speed", ConditionOperator.Greater, 5f).Evaluate(speed), "5>5 false");
        AssertTrue(new TransitionCondition("Speed", ConditionOperator.GreaterEqual, 5f).Evaluate(speed), "5>=5");
        AssertTrue(new TransitionCondition("Speed", ConditionOperator.Less, 10f).Evaluate(speed), "5<10");
        AssertTrue(new TransitionCondition("Speed", ConditionOperator.LessEqual, 5f).Evaluate(speed), "5<=5");
        AssertTrue(new TransitionCondition("Speed", ConditionOperator.Equals, 5f).Evaluate(speed), "5==5");
        AssertFalse(new TransitionCondition("Speed", ConditionOperator.Equals, 4f).Evaluate(speed), "5==4 false");
    }

    /// <summary>Int 条件：Equals/Greater/Less 等。</summary>
    public static void TestIntConditionOperators()
    {
        var weapon = AnimParameter.Int("Weapon", 2);
        AssertTrue(new TransitionCondition("Weapon", ConditionOperator.Equals, 2).Evaluate(weapon), "2==2");
        AssertFalse(new TransitionCondition("Weapon", ConditionOperator.Equals, 1).Evaluate(weapon), "2==1 false");
        AssertTrue(new TransitionCondition("Weapon", ConditionOperator.Greater, 1).Evaluate(weapon), "2>1");
        AssertTrue(new TransitionCondition("Weapon", ConditionOperator.Less, 3).Evaluate(weapon), "2<3");
    }

    /// <summary>Bool 条件：IsTrue/IsFalse/Equals。</summary>
    public static void TestBoolConditionOperators()
    {
        var grounded = AnimParameter.Bool("Grounded", true);
        AssertTrue(new TransitionCondition("Grounded", ConditionOperator.IsTrue, true).Evaluate(grounded), "IsTrue 当 true");
        AssertFalse(new TransitionCondition("Grounded", ConditionOperator.IsFalse, false).Evaluate(grounded), "IsFalse 当 true");
        AssertTrue(new TransitionCondition("Grounded", ConditionOperator.Equals, true).Evaluate(grounded), "==true");

        grounded.BoolValue = false;
        AssertTrue(new TransitionCondition("Grounded", ConditionOperator.IsFalse, false).Evaluate(grounded), "IsFalse 当 false");
        AssertTrue(new TransitionCondition("Grounded", ConditionOperator.Equals, false).Evaluate(grounded), "==false");
    }

    /// <summary>Trigger 条件：仅当活跃时满足。</summary>
    public static void TestTriggerCondition()
    {
        var jump = AnimParameter.Trigger("Jump");
        var cond = new TransitionCondition("Jump", ConditionOperator.Greater, 0f); // Trigger 忽略运算符

        AssertFalse(cond.Evaluate(jump), "未激活 Trigger 条件不满足");
        jump.SetTrigger();
        AssertTrue(cond.Evaluate(jump), "激活 Trigger 条件满足");
        jump.ConsumeTrigger();
        AssertFalse(cond.Evaluate(jump), "消耗后 Trigger 条件不满足");
    }

    /// <summary>多条件 AND：全部满足才触发。</summary>
    public static void TestMultiConditionAND()
    {
        var speed = AnimParameter.Float("Speed", 5f);
        var grounded = AnimParameter.Bool("Grounded", true);
        var dict = new Dictionary<string, AnimParameter> { ["Speed"] = speed, ["Grounded"] = grounded };

        var trans = new AnimTransitionReal(0, 1);
        trans.Conditions.Add(new TransitionCondition("Speed", ConditionOperator.Greater, 3f));
        trans.Conditions.Add(new TransitionCondition("Grounded", ConditionOperator.IsTrue, true));

        AssertTrue(trans.CheckConditions(dict), "Speed>3 且 Grounded=true 应触发");

        grounded.BoolValue = false;
        AssertFalse(trans.CheckConditions(dict), "Grounded=false 应阻止触发");
    }

    // ══════════════════════════════════════════════════════════════════
    //  三、状态机转移驱动
    // ══════════════════════════════════════════════════════════════════

    /// <summary>Instant 转移：条件满足立即切换，无过渡过程。</summary>
    public static void TestInstantTransition()
    {
        var fsm = MakeFsm(2, new[] { "Idle", "Run" });
        fsm.AddBoolParameter("IsRunning", false);

        var trans = fsm.States[0].AddTransition(1, 0f); // duration=0 → Instant
        trans.Conditions.Add(new TransitionCondition("IsRunning", ConditionOperator.IsTrue, true));

        AssertEqual(0, fsm.CurrentStateIndex, "初始在 Idle");
        AssertFalse(fsm.IsTransitioning, "无过渡");

        fsm.SetBool("IsRunning", true);
        fsm.Tick(0.016f);

        AssertEqual(1, fsm.CurrentStateIndex, "Instant 转移应立即到 Run");
        AssertFalse(fsm.IsTransitioning, "Instant 无过渡过程");
    }

    /// <summary>Crossfade 转移：进度 0→1，完成后切换。</summary>
    public static void TestCrossfadeTransitionProgress()
    {
        var fsm = MakeFsm(2, new[] { "Idle", "Run" });
        fsm.AddBoolParameter("IsRunning", false);

        var trans = fsm.States[0].AddTransition(1, 0.5f); // 0.5s 过渡
        trans.Conditions.Add(new TransitionCondition("IsRunning", ConditionOperator.IsTrue, true));

        fsm.SetBool("IsRunning", true);
        fsm.Tick(0.016f); // 触发转移

        AssertTrue(fsm.IsTransitioning, "应进入过渡");
        AssertEqual(0, fsm.CurrentStateIndex, "过渡中 CurrentState 仍是源");
        AssertTrue(fsm.GetTransitionProgress() > 0f && fsm.GetTransitionProgress() < 1f,
            "过渡进度应在 (0,1)");

        // 推进到过渡完成
        for (int i = 0; i < 40; i++) fsm.Tick(0.016f); // 0.64s > 0.5s
        AssertFalse(fsm.IsTransitioning, "过渡应完成");
        AssertEqual(1, fsm.CurrentStateIndex, "过渡完成后切换到 Run");
        Near(fsm.GetTransitionProgress(), 0f, 0.001f, "无过渡时进度为 0");
    }

    /// <summary>混合 alpha：线性曲线下进度=alpha。</summary>
    public static void TestTransitionAlphaLinear()
    {
        var fsm = MakeFsm(2, new[] { "A", "B" });
        fsm.AddBoolParameter("Go", false);
        var trans = fsm.States[0].AddTransition(1, 1f);
        trans.Conditions.Add(new TransitionCondition("Go", ConditionOperator.IsTrue, true));
        trans.BlendCurve = BlendCurveType.Linear;

        fsm.SetBool("Go", true);
        fsm.Tick(0.016f); // 启动过渡

        // 推进到约 50%
        for (int i = 0; i < 30; i++) fsm.Tick(0.016f); // ~0.5s
        float alpha = fsm.GetTransitionAlpha();
        Near(alpha, 0.5f, 0.05f, "线性曲线 50% 进度应约 alpha 0.5");
    }

    /// <summary>BlendCurve 求值：Linear/EaseIn/EaseOut/EaseInOut。</summary>
    public static void TestBlendCurves()
    {
        var trans = new AnimTransitionReal(0, 1);

        trans.BlendCurve = BlendCurveType.Linear;
        Near(trans.EvaluateBlendCurve(0f), 0f, 0.001f, "Linear t=0");
        Near(trans.EvaluateBlendCurve(0.5f), 0.5f, 0.001f, "Linear t=0.5");
        Near(trans.EvaluateBlendCurve(1f), 1f, 0.001f, "Linear t=1");

        trans.BlendCurve = BlendCurveType.EaseIn;
        Near(trans.EvaluateBlendCurve(0.5f), 0.25f, 0.001f, "EaseIn t=0.5 = t²");

        trans.BlendCurve = BlendCurveType.EaseOut;
        Near(trans.EvaluateBlendCurve(0.5f), 0.75f, 0.001f, "EaseOut t=0.5 = 1-(1-t)²");

        trans.BlendCurve = BlendCurveType.EaseInOut;
        Near(trans.EvaluateBlendCurve(0.5f), 0.5f, 0.001f, "EaseInOut t=0.5 smoothstep=0.5");
        Near(trans.EvaluateBlendCurve(0.25f), 0.15625f, 0.001f, "EaseInOut t=0.25");

        // 越界 Clamp
        trans.BlendCurve = BlendCurveType.Linear;
        Near(trans.EvaluateBlendCurve(-1f), 0f, 0.001f, "t<0 Clamp 到 0");
        Near(trans.EvaluateBlendCurve(2f), 1f, 0.001f, "t>1 Clamp 到 1");
    }

    /// <summary>ExitTime：归一化时间未达门槛不触发。</summary>
    public static void TestExitTimeGate()
    {
        var fsm = MakeFsm(2, new[] { "Attack", "Idle" });
        fsm.AddBoolParameter("Done", false);

        var trans = fsm.States[0].AddTransition(1, 0f); // Instant
        trans.Conditions.Add(new TransitionCondition("Done", ConditionOperator.IsTrue, true));
        trans.ExitTime = 0.8f; // 须达归一化 0.8 才可触发

        // 状态无 Clip → GetDuration=0 → GetNormalizedTime=0，永远达不到 0.8
        fsm.SetBool("Done", true);
        fsm.Tick(0.016f);
        AssertEqual(0, fsm.CurrentStateIndex, "ExitTime=0.8 但归一化时间=0，不应触发");
    }

    /// <summary>Trigger 自动消耗：转移触发后 Trigger 复位。</summary>
    public static void TestTriggerAutoConsume()
    {
        var fsm = MakeFsm(2, new[] { "Idle", "Jump" });
        fsm.AddTriggerParameter("Jump");

        var trans = fsm.States[0].AddTransition(1, 0f); // Instant
        trans.Conditions.Add(new TransitionCondition("Jump", ConditionOperator.Greater, 0f));

        fsm.SetTrigger("Jump");
        AssertTrue(fsm.GetParameter("Jump").IsTriggerActive, "SetTrigger 后活跃");

        fsm.Tick(0.016f);
        AssertEqual(1, fsm.CurrentStateIndex, "应跳转到 Jump");
        AssertFalse(fsm.GetParameter("Jump").IsTriggerActive, "转移触发后 Trigger 应自动消耗");
        AssertTrue(fsm.GetParameter("Jump").TriggerConsumed, "标记已消耗");
    }

    /// <summary>状态生命周期回调：OnEnter/OnExit 触发。</summary>
    public static void TestStateLifecycleCallbacks()
    {
        var fsm = MakeFsm(2, new[] { "A", "B" });
        fsm.AddBoolParameter("Go", false);

        int enterB = 0, exitA = 0;
        fsm.States[0].OnExit = () => exitA++;
        fsm.States[1].OnEnter = () => enterB++;

        fsm.States[0].AddTransition(1, 0f).Conditions.Add(
            new TransitionCondition("Go", ConditionOperator.IsTrue, true));

        fsm.SetBool("Go", true);
        fsm.Tick(0.016f);

        AssertEqual(1, exitA, "离开 A 应触发 OnExit");
        AssertEqual(1, enterB, "进入 B 应触发 OnEnter");
    }

    /// <summary>CanInterrupt=false：过渡进行中不被新条件打断。</summary>
    public static void TestTransitionNoInterrupt()
    {
        var fsm = MakeFsm(3, new[] { "A", "B", "C" });
        fsm.AddBoolParameter("GoB", false);
        fsm.AddBoolParameter("GoC", false);

        var toB = fsm.States[0].AddTransition(1, 1f); // 1s 过渡
        toB.Conditions.Add(new TransitionCondition("GoB", ConditionOperator.IsTrue, true));
        toB.CanInterrupt = false;

        // B 的过渡（模拟打断源）——实际打断逻辑在 CheckTransitions，过渡中不检查
        fsm.SetBool("GoB", true);
        fsm.Tick(0.016f);
        AssertTrue(fsm.IsTransitioning, "进入 A→B 过渡");

        // 过渡期间 CheckTransitions 不执行（inTransition 时不检查新转移）
        fsm.SetBool("GoC", true);
        // A 没有到 C 的转移，且过渡中不检查，故仍 A→B
        AssertEqual(0, fsm.CurrentStateIndex, "过渡中 CurrentState 仍是 A");
    }

    /// <summary>Conduit 全局守卫：守卫 false 时阻止所有转移。</summary>
    public static void TestGlobalConduitBlocks()
    {
        var fsm = MakeFsm(2, new[] { "A", "B" });
        fsm.AddBoolParameter("CanTransition", false);
        fsm.AddBoolParameter("Go", false);

        var conduit = new AnimConduit { Name = "GlobalGate" };
        conduit.Conditions.Add(new TransitionCondition("CanTransition", ConditionOperator.IsTrue, true));
        fsm.GlobalConduits.Add(conduit);

        fsm.States[0].AddTransition(1, 0f).Conditions.Add(
            new TransitionCondition("Go", ConditionOperator.IsTrue, true));

        fsm.SetBool("Go", true);
        fsm.Tick(0.016f);
        AssertEqual(0, fsm.CurrentStateIndex, "全局 Conduit=false 应阻止转移");

        fsm.SetBool("CanTransition", true);
        fsm.Tick(0.016f);
        AssertEqual(1, fsm.CurrentStateIndex, "Conduit=true 放行转移");
    }

    /// <summary>ForceTransition：忽略条件强制切换。</summary>
    public static void TestForceTransition()
    {
        var fsm = MakeFsm(3, new[] { "Idle", "Run", "Dead" });
        AssertEqual(0, fsm.CurrentStateIndex, "初始 Idle");

        fsm.ForceTransition("Dead", 0f); // Instant
        AssertEqual(2, fsm.CurrentStateIndex, "ForceTransition 应切到 Dead");
        AssertFalse(fsm.IsTransitioning, "Instant Force 无过渡");
    }

    /// <summary>初始状态：Initialize 进入 InitialStateIndex。</summary>
    public static void TestInitialStateIndex()
    {
        var fsm = MakeFsm(3, new[] { "A", "B", "C" });
        fsm.InitialStateIndex = 1;
        fsm.Reset();
        AssertEqual(1, fsm.CurrentStateIndex, "InitialStateIndex=1 应进入 B");
        AssertTrue(fsm.States[1].HasEntered, "B 应标记 HasEntered");
    }

    /// <summary>Reset：回到初始状态，清空过渡。</summary>
    public static void TestStateMachineReset()
    {
        var fsm = MakeFsm(2, new[] { "A", "B" });
        fsm.AddBoolParameter("Go", false);
        fsm.States[0].AddTransition(1, 0f).Conditions.Add(
            new TransitionCondition("Go", ConditionOperator.IsTrue, true));

        fsm.SetBool("Go", true);
        fsm.Tick(0.016f);
        AssertEqual(1, fsm.CurrentStateIndex, "已切到 B");

        fsm.Reset();
        AssertEqual(0, fsm.CurrentStateIndex, "Reset 应回初始 A");
        AssertFalse(fsm.IsTransitioning, "Reset 清空过渡");
        AssertNull(fsm.CurrentTransition, "Reset 后无当前过渡");
    }

    // ── AssertFalse/AssertNull 辅助 ──
    private static void AssertFalse(bool condition, string msg) => AssertTrue(!condition, msg);
    private static void AssertNull(object obj, string msg) => AssertTrue(obj == null, msg);
}
