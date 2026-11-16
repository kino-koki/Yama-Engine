using System;
using System.Numerics;
using YamaEngine.Input.Enhanced;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests
{
    // ══════════════════════════════════════════════════════════════
    // InputDepth — UE5.8 级 Enhanced Input 深度验证
    // 覆盖：修饰器纯值变换管线（DeadZone/Scale/Invert/Clamp/Curve/Step/Swizzle/Smooth/链式管线）
    //       触发器状态机 Evaluate 分支（Press/Release/Tap/SlowTap 释放分支）
    // ══════════════════════════════════════════════════════════════
    public static class InputTests
    {
        private static void AssertNear(float got, float want, float tol, string msg)
        {
            AssertTrue(MathF.Abs(got - want) <= tol,
                $"{msg} (got {got:F4}, want {want:F4}±{tol:F4})");
        }

        // ── 死区 (DeadZone) Inner 类型：低于 Min 归零，区间 [Min,Max] 重映射到 [0,1] ──
        public static void TestDeadZoneInnerRemapsAndZeroes()
        {
            var dz = new DeadZoneModifier(min: 0.2f, max: 1.0f, DeadZoneType.Inner);
            // 低于 Min 归零
            AssertNear(dz.ProcessValue(0.1f), 0f, 1e-5f, "DeadZone.Inner below Min should return 0.");
            // 等于 Min 也归零 (<= 判断)
            AssertNear(dz.ProcessValue(0.2f), 0f, 1e-5f, "DeadZone.Inner at Min boundary should return 0.");
            // 中段线性重映射 (0.6 - 0.2) / (1.0 - 0.2) = 0.5
            AssertNear(dz.ProcessValue(0.6f), 0.5f, 1e-5f, "DeadZone.Inner should remap [Min,Max]→[0,1] linearly.");
            // 顶部到 1.0
            AssertNear(dz.ProcessValue(1.0f), 1.0f, 1e-5f, "DeadZone.Inner at Max should map to 1.");
            // 负方向：-0.6 → -0.5
            AssertNear(dz.ProcessValue(-0.6f), -0.5f, 1e-5f, "DeadZone.Inner should handle negative side symmetric.");
            // 禁用时不处理
            dz.Enabled = false;
            AssertNear(dz.ProcessValue(0.05f), 0.05f, 1e-5f, "Disabled DeadZone should pass value through.");
        }

        // ── 死区径向 (Vector2 Radial)：基于向量长度重映射 ──
        public static void TestDeadZoneRadialVector2()
        {
            var dz = new DeadZoneModifier(min: 0.2f, max: 1.0f, DeadZoneType.Inner) { Radial = true };
            // 长度 0.1 (< Min) → 零向量
            var small = dz.ProcessValue(new Vector2(0.1f, 0f));
            AssertTrue(small.Length() < 1e-5f, "Radial DeadZone below Min should zero the vector.");
            // 长度 1.0 沿 X → 保留方向，长度 1.0
            var full = dz.ProcessValue(new Vector2(1f, 0f));
            AssertNear(full.X, 1f, 1e-5f, "Radial DeadZone at Max magnitude should preserve length 1.");
            // 长度 0.6 沿 X → 重映射长度 0.5
            var mid = dz.ProcessValue(new Vector2(0.6f, 0f));
            AssertNear(mid.Length(), 0.5f, 1e-3f, "Radial DeadZone mid magnitude should remap linearly.");
            // 长度 0.6 沿 (1,1)/√2 → 方向保留
            var diag = dz.ProcessValue(Vector2.Normalize(new Vector2(1, 1)) * 0.6f);
            AssertTrue(MathF.Abs(diag.X - diag.Y) < 1e-4f, "Radial DeadZone should preserve direction (X==Y for diagonal).");
            AssertNear(diag.Length(), 0.5f, 1e-3f, "Radial DeadZone diagonal length should also remap to 0.5.");
        }

        // ── ScaleModifier：统一系数 & 分轴系数 ──
        public static void TestScaleModifierUniformAndPerAxis()
        {
            var uniform = new ScaleModifier(2.5f);
            AssertNear(uniform.ProcessValue(0.4f), 1.0f, 1e-5f, "ScaleModifier should multiply float by Factor.");
            var vec2 = uniform.ProcessValue(new Vector2(2f, -3f));
            AssertNear(vec2.X, 5f, 1e-5f, "ScaleModifier uniform should scale Vector2.X.");
            AssertNear(vec2.Y, -7.5f, 1e-5f, "ScaleModifier uniform should scale Vector2.Y.");

            // 分轴系数 (X=2, Y=0.5)
            var perAxis = new ScaleModifier(2f, 0.5f, 1f);
            AssertTrue(!perAxis.Uniform, "PerAxis ScaleModifier constructor should set Uniform=false.");
            var scaled = perAxis.ProcessValue(new Vector2(3f, 4f));
            AssertNear(scaled.X, 6f, 1e-5f, "PerAxis ScaleModifier X should use PerAxisFactor.X.");
            AssertNear(scaled.Y, 2f, 1e-5f, "PerAxis ScaleModifier Y should use PerAxisFactor.Y.");
        }

        // ── InvertModifier：分量取反 ──
        public static void TestInvertModifierComponents()
        {
            var invertX = new InvertModifier(invertX: true, invertY: false, invertZ: false);
            AssertNear(invertX.ProcessValue(0.7f), -0.7f, 1e-5f, "InvertModifier should negate float when InvertX.");
            var v = invertX.ProcessValue(new Vector2(3f, 4f));
            AssertNear(v.X, -3f, 1e-5f, "InvertModifier should negate X when InvertX.");
            AssertNear(v.Y, 4f, 1e-5f, "InvertModifier should NOT negate Y when InvertY=false.");
            var v3 = invertX.ProcessValue(new Vector3(1f, 2f, 3f));
            AssertNear(v3.Z, 3f, 1e-5f, "InvertModifier should NOT negate Z when InvertZ=false.");
        }

        // ── ClampModifier：值域裁剪 ──
        public static void TestClampModifierBounds()
        {
            var clamp = new ClampModifier(min: -1f, max: 1f);
            AssertNear(clamp.ProcessValue(2.0f), 1.0f, 1e-5f, "Clamp should clip above Max.");
            AssertNear(clamp.ProcessValue(-5.0f), -1.0f, 1e-5f, "Clamp should clip below Min.");
            AssertNear(clamp.ProcessValue(0.5f), 0.5f, 1e-5f, "Clamp should pass through in-range value.");
            var cv = clamp.ProcessValue(new Vector3(2f, -3f, 0.5f));
            AssertNear(cv.X, 1f, 1e-5f, "Clamp Vector3.X should clip to Max.");
            AssertNear(cv.Y, -1f, 1e-5f, "Clamp Vector3.Y should clip to Min.");
            AssertNear(cv.Z, 0.5f, 1e-5f, "Clamp Vector3.Z should pass through.");
        }

        // ── CurveModifier：gamma 曲线（指数=2 时 0.5²=0.25） ──
        public static void TestCurveModifierGamma()
        {
            var curve = new CurveModifier(exponent: 2.0f);
            AssertNear(curve.ProcessValue(0.5f), 0.25f, 1e-5f, "Curve exponent=2 should square magnitude.");
            AssertNear(curve.ProcessValue(-0.5f), -0.25f, 1e-5f, "Curve should preserve sign.");
            AssertNear(curve.ProcessValue(1.0f), 1.0f, 1e-5f, "Curve at |1| should stay 1.");
        }

        // ── StepModifier：量化为离散等级 ──
        public static void TestStepModifierQuantizes()
        {
            var step = new StepModifier(steps: 5); // 等级：0, 0.25, 0.5, 0.75, 1.0
            // 0.3 → round(0.3 * 4) / 4 = round(1.2)/4 = 1/4 = 0.25
            AssertNear(step.ProcessValue(0.3f), 0.25f, 1e-5f, "Step should quantize to nearest level.");
            AssertNear(step.ProcessValue(0.7f), 0.75f, 1e-5f, "Step should snap to 0.75 for 0.7.");
            AssertNear(step.ProcessValue(1.0f), 1.0f, 1e-5f, "Step at 1.0 should stay 1.0.");
            AssertNear(step.ProcessValue(2.0f), 1.0f, 1e-5f, "Step should clamp to 1.0 first.");
        }

        // ── SwizzleModifier：分量重排 XY→YX ──
        public static void TestSwizzleModifierReorders()
        {
            var swizzle = new SwizzleModifier("YX");
            var result = swizzle.ProcessValue(new Vector2(1f, 2f));
            AssertNear(result.X, 2f, 1e-5f, "Swizzle YX should output Y as X.");
            AssertNear(result.Y, 1f, 1e-5f, "Swizzle YX should output X as Y.");
        }

        // ── SmoothModifier：指数移动平均 (EMA) 收敛到稳定值 ──
        public static void TestSmoothModifierEMAConverges()
        {
            var smooth = new SmoothModifier(smoothingFactor: 0.5f, SmoothAlgorithm.Exponential);
            // 第一帧：直接采用
            AssertNear(smooth.ProcessValue(1.0f), 1.0f, 1e-5f, "EMA first sample should initialize to value.");
            // 第二帧输入 0：0.5*1 + 0.5*0 = 0.5
            AssertNear(smooth.ProcessValue(0.0f), 0.5f, 1e-5f, "EMA second sample should blend at alpha=0.5.");
            // 第三帧输入 0：0.5*0.5 + 0.5*0 = 0.25
            AssertNear(smooth.ProcessValue(0.0f), 0.25f, 1e-5f, "EMA should exponentially decay toward 0.");
            smooth.Reset();
            // 重置后重新初始化
            AssertNear(smooth.ProcessValue(0.8f), 0.8f, 1e-5f, "After Reset, EMA should re-initialize to new value.");
        }

        // ── ModifierPipeline：链式组合多个修改器按顺序执行 ──
        public static void TestModifierPipelineChainsInOrder()
        {
            // 管线：Scale(2) → Clamp[-1,1] → DeadZone.Inner(0.2,1.0)
            var pipeline = new ModifierPipeline("TestPipeline");
            pipeline.Add(new ScaleModifier(2f))
                    .Add(new ClampModifier(-1f, 1f))
                    .Add(new DeadZoneModifier(0.2f, 1.0f, DeadZoneType.Inner));
            // input 0.6 → scale 1.2 → clamp 1.0 → deadzone 1.0
            AssertNear(pipeline.Process(0.6f), 1.0f, 1e-5f, "Pipeline should apply Scale→Clamp→DeadZone in order.");
            // input 0.3 → scale 0.6 → clamp 0.6 → deadzone (0.6-0.2)/0.8 = 0.5
            AssertNear(pipeline.Process(0.3f), 0.5f, 1e-5f, "Pipeline mid-range should remap through DeadZone.");
            // 禁用某环节：禁用 Clamp，input 0.6 → scale 1.2 → deadzone: (1.2-0.2)/0.8 = 1.25
            pipeline.Modifiers[1].Enabled = false;
            AssertNear(pipeline.Process(0.6f), 1.25f, 1e-4f, "Disabling a pipeline modifier should skip its transform.");
        }

        // ── HoldModifier：按住不足阈值返回 0，达到阈值后值通过 ──
        public static void TestHoldModifierGatesByHoldTime()
        {
            var hold = new HoldModifier(holdTime: 0.3f);
            hold.CurrentHoldTime = 0.1f; // 不足
            AssertNear(hold.ProcessValue(1.0f), 0f, 1e-5f, "HoldModifier below threshold should return 0.");
            hold.CurrentHoldTime = 0.3f; // 达到
            AssertNear(hold.ProcessValue(1.0f), 1.0f, 1e-5f, "HoldModifier at threshold should pass value through.");
            AssertTrue(!hold.IsWaiting, "IsWaiting should be false when CurrentHoldTime >= HoldTime.");
        }

        // ── TapModifier：短按通过，长按归零 ──
        public static void TestTapModifierShortPressOnly()
        {
            var tap = new TapModifier(maxDuration: 0.3f);
            tap.CurrentHoldTime = 0.1f;
            AssertNear(tap.ProcessValue(0.8f), 0.8f, 1e-5f, "TapModifier within MaxDuration should pass value.");
            tap.CurrentHoldTime = 0.5f;
            AssertNear(tap.ProcessValue(0.8f), 0f, 1e-5f, "TapModifier beyond MaxDuration should return 0.");
            AssertTrue(tap.IsTimedOut, "IsTimedOut should be true after exceeding MaxDuration.");
        }

        // ── PressTrigger：fresh 动作 State=Waiting → 非 Started，按下也不触发 ──
        public static void TestPressTriggerRequiresStartedState()
        {
            var action = new EnhancedInputAction("Fire", InputActionType.Button);
            var trigger = new PressTrigger();
            // fresh action State=Waiting，即使 controlValue>0.5，因非 Started 状态返回 None
            var pressed = trigger.Evaluate(action, value: 1f, controlValue: 1f);
            AssertTrue(pressed == TriggerState.None,
                "PressTrigger should return None for fresh action (State=Waiting, not Started).");
            // 释放：controlValue<0.5 → Canceled
            var released = trigger.Evaluate(action, value: 0f, controlValue: 0f);
            AssertTrue(released == TriggerState.Canceled,
                "PressTrigger should return Canceled when controlValue<0.5.");
        }

        // ── ReleaseTrigger：fresh 动作 HoldTime=0 → 无按住记录 → 返回 None（不误触发）──
        public static void TestReleaseTriggerNoPriorPressReturnsNone()
        {
            var action = new EnhancedInputAction("Interact", InputActionType.Button);
            var trigger = new ReleaseTrigger();
            // HoldTime=0 (fresh) → 即便 controlValue<0.5 也不该触发
            var released = trigger.Evaluate(action, value: 0f, controlValue: 0f);
            AssertTrue(released == TriggerState.None,
                "ReleaseTrigger should return None when HoldTime=0 (no prior press).");
            // controlValue>0.5 → Potential (等待释放)
            var holding = trigger.Evaluate(action, value: 1f, controlValue: 1f);
            AssertTrue(holding == TriggerState.Potential,
                "ReleaseTrigger should return Potential while control held.");
        }

        // ── TapTrigger：fresh 动作 HoldTime=0（无按住记录）→ 释放时不触发 ──
        public static void TestTapTriggerNoPriorPressDoesNotFire()
        {
            var action = new EnhancedInputAction("Jump", InputActionType.Button);
            var trigger = new TapTrigger(maxHoldTime: 0.3f);
            // fresh action HoldTime=0 → 释放时不满足 HoldTime>0 条件 → None
            var released = trigger.Evaluate(action, value: 0f, controlValue: 0f);
            AssertTrue(released == TriggerState.None,
                "TapTrigger should return None on release with HoldTime=0 (no tap recorded).");
            // 按住中 (controlValue>0.5) → Potential
            var holding = trigger.Evaluate(action, value: 1f, controlValue: 1f);
            AssertTrue(holding == TriggerState.Potential,
                "TapTrigger should return Potential while held within MaxHoldTime.");
        }

        // ── SlowTapTrigger：fresh 动作 HoldTime=0 → 长按释放也不触发 ──
        public static void TestSlowTapTriggerRequiresMinHoldTime()
        {
            var action = new EnhancedInputAction("ChargeAttack", InputActionType.Button);
            var trigger = new SlowTapTrigger(minHoldTime: 0.5f);
            // HoldTime=0 < MinHoldTime → 释放返回 Canceled
            var released = trigger.Evaluate(action, value: 0f, controlValue: 0f);
            AssertTrue(released == TriggerState.Canceled,
                "SlowTapTrigger should return Canceled when released below MinHoldTime.");
        }

        // ── TriggerState 枚举语义：确认四个状态值定义正确 ──
        public static void TestTriggerStateEnumValues()
        {
            AssertEqual(0, (int)TriggerState.None, "TriggerState.None should be 0.");
            AssertEqual(1, (int)TriggerState.Potential, "TriggerState.Potential should be 1.");
            AssertEqual(2, (int)TriggerState.Triggered, "TriggerState.Triggered should be 2.");
            AssertEqual(3, (int)TriggerState.Canceled, "TriggerState.Canceled should be 3.");
        }
    }
}
