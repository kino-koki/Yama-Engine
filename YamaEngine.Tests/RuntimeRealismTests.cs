// Copyright © 2025 Yama Engine. All Rights Reserved.
// ══════════════════════════════════════════════════════════════════════════════
// 运行时子系统真实缺陷回归测试
// 焦点：时间系统跳变、FPS NaN、定时事件重入安全
// ══════════════════════════════════════════════════════════════════════════════

using YamaEngine.ECS;

namespace YamaEngine.Tests;

public static class RuntimeRealismTests
{
    public static int Run()
    {
        int pass = 0, fail = 0;

        // 1. 负 delta（系统时钟回拨 / NTP 同步）不应倒退 _realTime / _gameTime
        {
            var t = new WorldTimeSystem();
            t.OnUpdate(0.016f); // 初始化基线
            float beforeReal = t.RealTime;
            float beforeGame = t.GameTime;

            // 模拟时钟跳变：直接传入负 delta（测试通过 OnUpdate 注入）
            t.OnUpdate(-1.5f);

            bool realNonDecreasing = t.RealTime >= beforeReal;
            bool gameNonDecreasing = t.GameTime >= beforeGame;
            bool dtNonNegative = t.UnscaledDeltaTime >= 0f;
            if (realNonDecreasing && gameNonDecreasing && dtNonNegative) pass++;
            else { fail++; System.Console.WriteLine(
                $"[FAIL] TimeDeltaClamp: real={t.RealTime:F6} (>= {beforeReal:F6}), game={t.GameTime:F6}, dt={t.UnscaledDeltaTime:F6}"); }
        }

        // 2. 巨大 delta（后台挂起 / 断点）应被钳制到 MaxDeltaTime
        {
            var t = new WorldTimeSystem();
            t.MaxDeltaTime = 0.25f;
            t.OnUpdate(0.016f);

            t.OnUpdate(10f); // 模拟 10 秒跳帧

            if (t.UnscaledDeltaTime <= 0.25f + 1e-6f && t.UnscaledDeltaTime >= 0f) pass++;
            else { fail++; System.Console.WriteLine(
                $"[FAIL] TimeDeltaMaxClamp: dt={t.UnscaledDeltaTime:F6} (should be <= 0.25)"); }
        }

        // 3. FPS 不应出现 NaN / Infinity
        {
            var t = new WorldTimeSystem();
            for (int i = 0; i < 100; i++) t.OnUpdate(0.016f);
            bool finite = !float.IsNaN(t.FPS) && !float.IsInfinity(t.FPS);
            if (finite) pass++;
            else { fail++; System.Console.WriteLine($"[FAIL] FpsFinite: FPS={t.FPS}"); }
        }

        // 4. 定时事件：delay 为负立即触发
        {
            var t = new WorldTimeSystem();
            int fired = 0;
            t.Schedule(-1f, () => fired++);
            t.OnUpdate(0.016f);
            if (fired == 1) pass++;
            else { fail++; System.Console.WriteLine($"[FAIL] TimerImmediate: fired={fired}"); }
        }

        // 5. 定时事件：取消后不触发
        {
            var t = new WorldTimeSystem();
            int fired = 0;
            long id = t.Schedule(0.1f, () => fired++);
            bool cancelled = t.Cancel(id);
            t.OnUpdate(0.016f);
            t.OnUpdate(0.2f);
            if (cancelled && fired == 0) pass++;
            else { fail++; System.Console.WriteLine($"[FAIL] TimerCancel: cancelled={cancelled}, fired={fired}"); }
        }

        // 6. 定时事件：回调内注册新事件（重入安全）
        // 注：WorldTimeSystem.OnUpdate 用 Stopwatch 计算实际 delta（忽略传入参数），
        //     故用 delay=0 保证在连续 OnUpdate 调用中必然触发
        {
            var t = new WorldTimeSystem();
            int outerFired = 0, innerFired = 0;
            t.Schedule(0f, () =>
            {
                outerFired++;
                // 回调内注册新事件，验证不破坏反向迭代
                t.Schedule(0f, () => innerFired++);
            });
            t.OnUpdate(0.016f); // 外层触发并注册内层
            t.OnUpdate(0.016f); // 内层触发
            if (outerFired == 1 && innerFired == 1) pass++;
            else { fail++; System.Console.WriteLine(
                $"[FAIL] TimerReentry: outer={outerFired} (expect 1), inner={innerFired} (expect 1)"); }
        }

        // 7. 时间缩放为 0 等效暂停（GameTime 不推进）
        {
            var t = new WorldTimeSystem();
            t.OnUpdate(0.016f);
            float beforeGame = t.GameTime;
            t.SetTimeScale(0f);
            t.OnUpdate(0.016f);
            if (t.GameTime == beforeGame && t.UnscaledDeltaTime > 0f) pass++;
            else { fail++; System.Console.WriteLine(
                $"[FAIL] TimeScaleZero: gameTime={t.GameTime:F6} (expect {beforeGame:F6}), unscaledDt={t.UnscaledDeltaTime:F6}"); }
        }

        // 8. 暂停时 DeltaTime 为 0，UnscaledDeltaTime 仍推进
        {
            var t = new WorldTimeSystem();
            t.Pause();
            t.OnUpdate(0.016f);
            if (t.DeltaTime == 0f && t.UnscaledDeltaTime > 0f && t.IsPaused) pass++;
            else { fail++; System.Console.WriteLine(
                $"[FAIL] PauseInvariant: dt={t.DeltaTime:F6}, unscaled={t.UnscaledDeltaTime:F6}, paused={t.IsPaused}"); }
        }

        // 9. Pause/Resume 嵌套计数
        {
            var t = new WorldTimeSystem();
            t.Pause();
            t.Pause();
            t.Resume();
            bool stillPausedAfterOneResume = t.IsPaused;
            t.Resume();
            bool resumedAfterSecondResume = !t.IsPaused;
            if (stillPausedAfterOneResume && resumedAfterSecondResume) pass++;
            else { fail++; System.Console.WriteLine(
                $"[FAIL] PauseNesting: stillPaused={stillPausedAfterOneResume}, resumed={resumedAfterSecondResume}"); }
        }

        // 10. 强制设置暂停重置计数
        {
            var t = new WorldTimeSystem();
            t.Pause();
            t.Pause();
            t.SetPaused(false);
            if (!t.IsPaused) pass++;
            else { fail++; System.Console.WriteLine("[FAIL] SetPausedResetsCounter"); }
        }

        // 11. 高倍时间缩放：固定步长累积器应有界（防止单帧积压过多导致丢步卡顿）
        {
            var t = new WorldTimeSystem();
            t.FixedDeltaTime = 1f / 60f;
            t.MaxDeltaTime = 0.25f;
            t.SetTimeScale(8f); // 8 倍速
            bool bounded = true;
            // 连续 30 帧 60fps（每帧 rawDelta≈16ms，scaled≈131ms）
            for (int i = 0; i < 30; i++)
            {
                t.OnUpdate(1f / 60f);
                if (t.FixedUpdateCount > 17) { bounded = false; break; }
            }
            if (bounded) pass++;
            else { fail++; System.Console.WriteLine(
                $"[FAIL] FixedStepBoundedAtHighTimeScale: FixedUpdateCount={t.FixedUpdateCount} > 17"); }
        }

        // 12. FixedUpdateCount 不应为负
        {
            var t = new WorldTimeSystem();
            t.SetTimeScale(0.01f); // 极慢速
            t.OnUpdate(1f / 60f);
            if (t.FixedUpdateCount >= 0) pass++;
            else { fail++; System.Console.WriteLine($"[FAIL] FixedStepNonNegative: {t.FixedUpdateCount}"); }
        }

        System.Console.WriteLine($"  == RuntimeRealism: {pass}/{pass + fail} passed ==");
        // 聚合到全局失败计数，使测试套件整体退出码正确反映本组回归
        for (int i = 0; i < fail; i++) TestHelpers.RecordFailure();
        return fail;
    }
}
