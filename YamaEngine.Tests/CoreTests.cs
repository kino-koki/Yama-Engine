using System;
using YamaEngine.Core;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests
{
    // ══════════════════════════════════════════════════════════════
    // CoreDepth — UE5.8 级引擎核心基础设施深度验证
    // 覆盖：YamaRandom 种子可复现 (XorShift128+)、MathUtils 插值/平滑、
    //       Easing 缓动端点、TimeManager 固定步长累积器、TimeScale 钳制
    // ══════════════════════════════════════════════════════════════
    public static class CoreTests
    {
        private static void AssertNear(float got, float want, float tol, string msg)
        {
            AssertTrue(MathF.Abs(got - want) <= tol,
                $"{msg} (got {got:F5}, want {want:F5}±{tol:F5})");
        }

        // ── YamaRandom 种子可复现：同 seed 生成完全相同的序列 ──
        public static void TestYamaRandomSeedReproducibility()
        {
            var a = new YamaRandom(seed: 42);
            var b = new YamaRandom(seed: 42);
            for (int i = 0; i < 100; i++)
            {
                int ia = a.Next(), ib = b.Next();
                AssertTrue(ia == ib, $"Same seed must produce identical int sequence at step {i}.");
            }
            // 新建同 seed 实例从头复现前 100 步（a/b 已耗尽，需独立起点）
            var c = new YamaRandom(seed: 42);
            var d = new YamaRandom(seed: 42);
            for (int i = 0; i < 100; i++)
                AssertTrue(c.Next() == d.Next(), "Fresh instances with same seed must replay identical sequence.");
        }

        // ── YamaRandom 区间语义：NextFloat 落在 [0,1)，Next(min,max) 落在 [min,max) ──
        public static void TestYamaRandomRangeSemantics()
        {
            var rng = new YamaRandom(seed: 7);
            for (int i = 0; i < 1000; i++)
            {
                float f = rng.NextFloat();
                AssertTrue(f >= 0f && f < 1f, $"NextFloat() must be in [0,1), got {f}.");
            }
            var rng2 = new YamaRandom(seed: 9);
            for (int i = 0; i < 1000; i++)
            {
                float v = rng2.NextFloat(-5f, 5f);
                AssertTrue(v >= -5f && v < 5f, $"NextFloat(min,max) must be in [min,max), got {v}.");
            }
            var rng3 = new YamaRandom(seed: 11);
            for (int i = 0; i < 1000; i++)
            {
                int n = rng3.Next(3, 10);
                AssertTrue(n >= 3 && n < 10, $"Next(min,max) must be in [min,max), got {n}.");
            }
        }

        // ── MathUtils.Lerp / InverseLerp：线性插值及其逆运算 ──
        public static void TestMathUtilsLerpInverseLerp()
        {
            AssertNear(MathUtils.Lerp(0f, 1f, 0.5f), 0.5f, 1e-6f, "Lerp midpoint should be 0.5.");
            AssertNear(MathUtils.Lerp(0f, 10f, 0f), 0f, 1e-6f, "Lerp at t=0 should be a.");
            AssertNear(MathUtils.Lerp(0f, 10f, 1f), 10f, 1e-6f, "Lerp at t=1 should be b.");
            AssertNear(MathUtils.Lerp(-1f, 1f, 0.25f), -0.5f, 1e-6f, "Lerp negative range.");
            // InverseLerp 是 Lerp 的逆：InverseLerp(a,b,Lerp(a,b,t))==t
            AssertNear(MathUtils.InverseLerp(0f, 10f, 5f), 0.5f, 1e-6f, "InverseLerp midpoint.");
            AssertNear(MathUtils.InverseLerp(0f, 10f, 0f), 0f, 1e-6f, "InverseLerp at a.");
            AssertNear(MathUtils.InverseLerp(0f, 10f, 10f), 1f, 1e-6f, "InverseLerp at b.");
        }

        // ── MathUtils.SmoothStep：平滑插值端点与中点对称性 ──
        public static void TestMathUtilsSmoothStep()
        {
            AssertNear(MathUtils.SmoothStep(0f), 0f, 1e-6f, "SmoothStep(0) should be 0.");
            AssertNear(MathUtils.SmoothStep(1f), 1f, 1e-6f, "SmoothStep(1) should be 1.");
            AssertNear(MathUtils.SmoothStep(0.5f), 0.5f, 1e-6f, "SmoothStep(0.5) should be 0.5 (symmetric).");
            // 单调递增
            float prev = -1f;
            for (int i = 0; i <= 10; i++)
            {
                float t = i / 10f;
                float s = MathUtils.SmoothStep(t);
                AssertTrue(s >= prev, "SmoothStep must be monotonically increasing.");
                prev = s;
            }
        }

        // ── Easing 缓动：端点值正确（In 从 0 起、Out 到 1 止）──
        public static void TestEasingEndpoints()
        {
            AssertNear(Easing.Linear(0f), 0f, 1e-6f, "Linear(0).");
            AssertNear(Easing.Linear(1f), 1f, 1e-6f, "Linear(1).");
            // 所有 In 曲线在 t=0 为 0，t=1 为 1
            float[] ones = { Easing.QuadIn(1f), Easing.CubicIn(1f), Easing.ExpoIn(1f), Easing.QuadOut(1f), Easing.CubicOut(1f), Easing.ExpoOut(1f) };
            foreach (var v in ones)
                AssertNear(v, 1f, 1e-5f, "All easing curves must map t=1 to 1.");
            float[] zeros = { Easing.QuadIn(0f), Easing.CubicIn(0f), Easing.ExpoIn(0f), Easing.QuadOut(0f), Easing.CubicOut(0f), Easing.ExpoOut(0f) };
            foreach (var v in zeros)
                AssertNear(v, 0f, 1e-5f, "All easing curves must map t=0 to 0.");
            // CubicOut(0.5) 快出：应 > 线性 0.5
            AssertTrue(Easing.CubicOut(0.5f) > 0.5f, "CubicOut(0.5) should exceed linear (ease-out accelerates early).");
        }

        // ── TimeManager 固定步长累积器：FixedDelta=FixedDeltaTime*FixedTimeScale，步数封顶 8 ──
        public static void TestTimeManagerFixedStepAccumulator()
        {
            var tm = TimeManager.Instance;
            tm.Reset();
            tm.TimeScale = 1f;
            tm.FixedTimeScale = 1f;
            tm.FixedDeltaTime = 0.02f; // 50Hz 固定步

            float accumulator = 0f;
            // EditorStep 直接设 UnscaledDeltaTime=deltaTime（不应用 TimeScale）
            // GetFixedStepCount 内部已执行 accumulator += UnscaledDeltaTime，外部不手动累加
            tm.EditorStep(0.1f); // 设 UnscaledDeltaTime=0.1
            int steps = tm.GetFixedStepCount(ref accumulator);
            AssertTrue(steps == 5, $"0.1s / 0.02s should yield exactly 5 fixed steps, got {steps}.");
            AssertNear(accumulator, 0f, 1e-6f, "Accumulator should be fully consumed after exact division.");

            // 小帧时间不足一步：累积器结转，下帧合并结算（0.05 → 0 步，剩 0.05；再 0.1 → 合计 0.15 → 7 步余 0.01）
            tm.FixedTimeScale = 1f;
            tm.FixedDeltaTime = 0.02f;
            accumulator = 0f;
            tm.EditorStep(0.05f);
            steps = tm.GetFixedStepCount(ref accumulator);
            AssertTrue(steps == 2, $"0.05s / 0.02s should yield 2 steps, got {steps}.");
            AssertNear(accumulator, 0.01f, 1e-6f, "Remainder should carry over (0.05 - 2*0.02 = 0.01).");
            tm.EditorStep(0.1f);
            steps = tm.GetFixedStepCount(ref accumulator);
            AssertTrue(steps == 5, $"Carried 0.01 + 0.1 = 0.11 should yield 5 steps (remainder 0.01), got {steps}.");

            // 步数封顶 8 防螺旋：巨大帧时间被限制（内部 while 条件 steps<8）
            accumulator = 0f;
            tm.EditorStep(1.0f); // 设 UnscaledDeltaTime=1.0
            steps = tm.GetFixedStepCount(ref accumulator);
            AssertTrue(steps == 8, $"Fixed step count must be capped at 8 (anti-spiral), got {steps}.");
        }

        // ── TimeManager.EditorStep：DeltaTime/GameTime/FrameCount 累积（不应用 TimeScale）──
        public static void TestTimeManagerEditorStepAccumulation()
        {
            var tm = TimeManager.Instance;
            tm.Reset();
            tm.EditorStep(0.016f);
            AssertTrue(tm.FrameCount == 1, "First EditorStep should increment frame count to 1.");
            AssertNear((float)tm.GameTime, 0.016f, 1e-6f, "GameTime should accumulate deltaTime.");

            tm.EditorStep(0.016f);
            AssertTrue(tm.FrameCount == 2, "Second EditorStep should increment frame count to 2.");
            AssertNear((float)tm.GameTime, 0.032f, 1e-5f, "GameTime should accumulate two steps.");
            AssertTrue(MathF.Abs(tm.DeltaTime - 0.016f) < 1e-6f, "EditorStep sets DeltaTime directly to argument.");
            AssertTrue(MathF.Abs(tm.UnscaledDeltaTime - 0.016f) < 1e-6f, "EditorStep sets UnscaledDeltaTime to argument (no scaling).");
        }

        // ── TimeScale 钳制：负值被钳到 0 ──
        public static void TestTimeScaleClampsToZero()
        {
            var tm = TimeManager.Instance;
            tm.TimeScale = -1f;
            AssertTrue(tm.TimeScale == 0f, "Negative TimeScale must be clamped to 0.");
            tm.TimeScale = 2.5f;
            AssertTrue(tm.TimeScale == 2.5f, "Positive TimeScale within range should be preserved.");
            tm.TimeScale = 0f;
            AssertTrue(tm.TimeScale == 0f, "Zero TimeScale should pause simulation scaling.");
            tm.TimeScale = 1f; // 复位
        }
    }
}
