// UnityCoroutineYieldDepthTests.cs — Unity5 Coroutine / yield 指令深度验证
// 覆盖：WaitForSeconds（含负数 clamp）、WaitForEndOfFrame/WaitForFixedUpdate 标记、
//       CustomYieldInstruction.KeepWaiting、Coroutine.IsDone/IsRunning/Stop/Matches。
using System;
using System.Collections;
using System.Collections.Generic;
using YamaEngine.Scripting;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests;

public static class UnityCoroutineYieldDepthTests
{
    private static void AssertFalse(bool c, string msg) => AssertTrue(!c, msg);
    private static void Near(float got, float want, float tol, string msg)
        => AssertTrue(MathF.Abs(got - want) <= tol, $"{msg}（期望 {want}，实际 {got}）");

    // ════════════════════════════════════════════════════════════════
    //  一、yield 指令类型
    // ════════════════════════════════════════════════════════════════

    /// <summary>WaitForSeconds 保存秒数。</summary>
    public static void TestWaitForSecondsStores()
    {
        var w = new WaitForSeconds(1.5f);
        Near(w.Seconds, 1.5f, 0.0001f, "Seconds=1.5");
    }

    /// <summary>WaitForSeconds 负数 clamp 到 0。</summary>
    public static void TestWaitForSecondsClampNegative()
    {
        var w = new WaitForSeconds(-3.0f);
        Near(w.Seconds, 0f, 0.0001f, "负数 clamp 到 0");
    }

    /// <summary>WaitForSeconds 零值。</summary>
    public static void TestWaitForSecondsZero()
    {
        var w = new WaitForSeconds(0f);
        Near(w.Seconds, 0f, 0.0001f, "Seconds=0");
    }

    /// <summary>WaitForEndOfFrame 是标记类。</summary>
    public static void TestWaitForEndOfFrameMarker()
    {
        var w = new WaitForEndOfFrame();
        AssertTrue(w is object, "WaitForEndOfFrame 可实例化");
    }

    /// <summary>WaitForFixedUpdate 是标记类。</summary>
    public static void TestWaitForFixedUpdateMarker()
    {
        var w = new WaitForFixedUpdate();
        AssertTrue(w is object, "WaitForFixedUpdate 可实例化");
    }

    /// <summary>CustomYieldInstruction 抽象类：KeepWaiting 由子类实现。</summary>
    public static void TestCustomYieldKeepWaiting()
    {
        var cyi = new TestYield { Result = true };
        AssertTrue(cyi.KeepWaiting, "KeepWaiting=true");
        cyi.Result = false;
        AssertFalse(cyi.KeepWaiting, "KeepWaiting=false");
    }

    /// <summary>CustomYieldInstruction 不可直接实例化。</summary>
    public static void TestCustomYieldAbstract()
    {
        AssertTrue(typeof(CustomYieldInstruction).IsAbstract, "CustomYieldInstruction 是抽象类");
    }

    private sealed class TestYield : CustomYieldInstruction
    {
        public bool Result;
        public override bool KeepWaiting => Result;
    }

    // ════════════════════════════════════════════════════════════════
    //  二、Coroutine 状态（通过 ScriptComponent 派生类驱动）
    // ════════════════════════════════════════════════════════════════

    /// <summary>StartCoroutine 返回 Coroutine，IsRunning=true / IsDone=false。</summary>
    public static void TestCoroutineStartState()
    {
        var comp = new TestComp();
        var routine = TestRoutineOneFrame();
        var co = comp.StartCoroutine(routine);
        AssertTrue(co is not null, "StartCoroutine 返回 Coroutine");
        AssertTrue(co!.IsRunning, "刚启动 IsRunning=true");
        AssertFalse(co.IsDone, "刚启动 IsDone=false");
        AssertEqual(1, comp.CoroutineCount, "CoroutineCount=1");
    }

    /// <summary>Coroutine 跑完一帧后 IsDone=true。</summary>
    public static void TestCoroutineOneFrame()
    {
        var comp = new TestComp();
        var co = comp.StartCoroutine(TestRoutineOneFrame());
        AssertTrue(co.IsRunning, "初始运行中");
        comp.TickCoroutines(0.016f, CoroutinePhase.Update);
        AssertFalse(co.IsRunning, "跑完一帧后 IsDone");
        AssertEqual(0, comp.CoroutineCount, "完成后从列表移除");
    }

    /// <summary>WaitForSeconds(1.0) 需要 1 秒 Update 才恢复。</summary>
    public static void TestCoroutineWaitForSeconds()
    {
        var comp = new TestComp();
        int endReached = 0;
        IEnumerator Routine()
        {
            yield return new WaitForSeconds(1.0f);
            endReached++;
        }
        var co = comp.StartCoroutine(Routine());
        // 0.5 秒不够
        comp.TickCoroutines(0.5f, CoroutinePhase.Update);
        AssertEqual(0, endReached, "0.5 秒还没到");
        AssertTrue(co.IsRunning, "还在等");
        // 再 0.6 秒（累计 1.1）应该到了
        comp.TickCoroutines(0.6f, CoroutinePhase.Update);
        AssertEqual(1, endReached, "累计 1.1 秒到达");
        AssertFalse(co.IsRunning, "跑完");
    }

    /// <summary>WaitForSeconds 在 FixedUpdate phase 不推进。</summary>
    public static void TestCoroutineWaitSecondsFixedPhase()
    {
        var comp = new TestComp();
        int endReached = 0;
        IEnumerator Routine()
        {
            yield return new WaitForSeconds(0.5f);
            endReached++;
        }
        comp.StartCoroutine(Routine());
        // FixedUpdate phase 不驱动 timer
        comp.TickCoroutines(1.0f, CoroutinePhase.FixedUpdate);
        AssertEqual(0, endReached, "Fixed phase 不推进 WaitForSeconds");
        comp.TickCoroutines(1.0f, CoroutinePhase.EndOfFrame);
        AssertEqual(0, endReached, "EndOfFrame phase 也不推进");
    }

    /// <summary>WaitForEndOfFrame 需 EndOfFrame phase 才恢复。</summary>
    public static void TestCoroutineWaitEndOfFrame()
    {
        var comp = new TestComp();
        int endReached = 0;
        IEnumerator Routine()
        {
            yield return new WaitForEndOfFrame();
            endReached++;
        }
        comp.StartCoroutine(Routine());
        comp.TickCoroutines(0.016f, CoroutinePhase.Update);
        AssertEqual(0, endReached, "Update phase 不驱动 EndOfFrame");
        comp.TickCoroutines(0.016f, CoroutinePhase.FixedUpdate);
        AssertEqual(0, endReached, "FixedUpdate phase 也不驱动");
        comp.TickCoroutines(0.016f, CoroutinePhase.EndOfFrame);
        AssertEqual(1, endReached, "EndOfFrame phase 恢复");
    }

    /// <summary>WaitForFixedUpdate 需 FixedUpdate phase 才恢复。</summary>
    public static void TestCoroutineWaitFixedUpdate()
    {
        var comp = new TestComp();
        int endReached = 0;
        IEnumerator Routine()
        {
            yield return new WaitForFixedUpdate();
            endReached++;
        }
        comp.StartCoroutine(Routine());
        comp.TickCoroutines(0.016f, CoroutinePhase.Update);
        AssertEqual(0, endReached, "Update phase 不驱动 FixedUpdate");
        comp.TickCoroutines(0.016f, CoroutinePhase.FixedUpdate);
        AssertEqual(1, endReached, "FixedUpdate phase 恢复");
    }

    /// <summary>null/yield return 0 → 等一帧。</summary>
    public static void TestCoroutineNullYield()
    {
        var comp = new TestComp();
        int endReached = 0;
        IEnumerator Routine()
        {
            yield return null;
            endReached++;
        }
        comp.StartCoroutine(Routine());
        comp.TickCoroutines(0.016f, CoroutinePhase.Update);
        AssertEqual(1, endReached, "null yield 等一帧后恢复");
    }

    /// <summary>StopCoroutine(Coroutine) 立即结束。</summary>
    public static void TestCoroutineStopEarly()
    {
        var comp = new TestComp();
        int endReached = 0;
        IEnumerator Routine()
        {
            yield return new WaitForSeconds(10.0f);
            endReached++;
        }
        var co = comp.StartCoroutine(Routine());
        comp.StopCoroutine(co);
        AssertTrue(co.IsDone, "Stop 后 IsDone=true");
        AssertFalse(co.IsRunning, "Stop 后 IsRunning=false");
        // Tick 不再推进
        comp.TickCoroutines(20.0f, CoroutinePhase.Update);
        AssertEqual(0, endReached, "Stop 后逻辑不执行");
        AssertEqual(0, comp.CoroutineCount, "Stop 后从列表移除");
    }

    /// <summary>StopAllCoroutines 结束所有。</summary>
    public static void TestCoroutineStopAll()
    {
        var comp = new TestComp();
        comp.StartCoroutine(TestRoutineOneFrame());
        comp.StartCoroutine(TestRoutineOneFrame());
        AssertEqual(2, comp.CoroutineCount, "2 个协程");
        comp.StopAllCoroutines();
        AssertEqual(0, comp.CoroutineCount, "StopAll 后全清");
    }

    /// <summary>嵌套 yield（IEnumerator）链式执行：一帧内跑完子例程。</summary>
    public static void TestCoroutineNested()
    {
        var comp = new TestComp();
        var log = new List<string>();
        IEnumerator Inner()
        {
            log.Add("B1");
            yield return null;
            log.Add("B2");
        }
        IEnumerator Outer()
        {
            log.Add("A1");
            yield return Inner();
            log.Add("A2");
        }
        var co = comp.StartCoroutine(Outer());
        comp.TickCoroutines(0.016f, CoroutinePhase.Update);
        // 引擎在一帧 Tick 内将嵌套子例程驱动到完成：B1→B2→A2
        AssertTrue(log.Contains("A1"), "Outer 开始");
        AssertTrue(log.Contains("B1"), "Inner 开始");
        AssertTrue(log.Contains("B2"), "Inner 完成");
        AssertTrue(log.Contains("A2"), "Outer 完成");
        AssertFalse(co.IsRunning, "嵌套完成后 IsDone");
        AssertEqual(4, log.Count, "共 4 个事件");
    }

    /// <summary>CustomYieldInstruction 驱动 KeepWaiting=false 时恢复。</summary>
    public static void TestCoroutineCustomYield()
    {
        var comp = new TestComp();
        int endReached = 0;
        var cyi = new TestYield { Result = true };
        IEnumerator Routine()
        {
            yield return cyi;
            endReached++;
        }
        comp.StartCoroutine(Routine());
        comp.TickCoroutines(0.016f, CoroutinePhase.Update);
        AssertEqual(0, endReached, "KeepWaiting=true 不恢复");
        cyi.Result = false;
        comp.TickCoroutines(0.016f, CoroutinePhase.Update);
        AssertEqual(1, endReached, "KeepWaiting=false 恢复");
    }

    // 协程例程
    private static IEnumerator TestRoutineOneFrame()
    {
        yield return null;
    }

    // 测试用 ScriptComponent（仅用协程 API，无需 ECS；TickCoroutines 是 internal，InternalsVisibleTo 已授权）
    private sealed class TestComp : ScriptComponent
    {
    }
}
