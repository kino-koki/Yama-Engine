using System;
using System.Threading;
using YamaEngine.Runtime;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests
{
    // ══════════════════════════════════════════════════════════════
    // JobsDepth — UE5.8 级作业系统深度验证
    // 覆盖：ParallelFor 批区间语义 + 余数批、依赖链顺序、
    //       JobHandle.Combine 聚合、IJob 单作业 Complete、Flush 排空
    // ══════════════════════════════════════════════════════════════
    public static class JobsTests
    {
        // ── ScheduleParallelFor：所有索引被批次覆盖（含余数批）──
        public static void TestParallelForCoversAllIndicesWithRemainder()
        {
            using var sched = new JobScheduler(workerCount: 2);
            sched.Start();
            int count = 105;               // 不整除 batchSize=32 → 4 批（32/32/32/9）
            int batchSize = 32;
            var results = new int[count];
            // 关键：Combine 句柄的 CompletionSource=null → Complete() 为 no-op。
            // 用 CountdownEvent 等所有批真正执行完毕（余数批 batchEnd=min 计入）。
            int batchCount = (count + batchSize - 1) / batchSize;
            using var done = new CountdownEvent(batchCount);

            sched.ScheduleParallelFor(0, count, batchSize, (s, e) =>
            {
                for (int i = s; i < e; i++) results[i] = i * 2;
                done.Signal();
            });
            AssertTrue(done.Wait(TimeSpan.FromSeconds(5)),
                "All ParallelFor batches (incl. remainder) should finish within 5s.");

            for (int i = 0; i < count; i++)
                AssertTrue(results[i] == i * 2,
                    $"Index {i} should be written (ParallelFor must cover all indices incl. remainder batch).");
        }

        // ── 依赖链：B 依赖 A → A 的写入对 B 可见，B 在 A 后执行 ──
        public static void TestScheduleDependencyOrdering()
        {
            using var sched = new JobScheduler(workerCount: 2);
            sched.Start();
            int step = 0;

            // 用两个闭包作业显式记录顺序
            var results = new int[2];
            var hA = sched.Schedule(new LambdaIJob(() =>
            {
                results[0] = 1;
                step = 1;
            }));
            var hB = sched.Schedule(new LambdaIJob(() =>
            {
                // B 只应在 A 完成后运行
                results[1] = step + 1;
            }), dependsOn: hA);

            hB.Complete(); // 阻塞直到 B（以及 A）完成

            AssertTrue(results[0] == 1, "Job A should have run.");
            AssertTrue(results[1] == 2, "Job B should run after A and see A's effect (step=1 → results[1]=2).");
            AssertTrue(hA.IsCompleted, "Dependency handle A should be completed after B completes.");
        }

        // ── JobHandle.Combine：聚合三个前置作业，全部完成后结果可见 ──
        public static void TestCombineHandleWaitsAllPrerequisites()
        {
            using var sched = new JobScheduler(workerCount: 2);
            sched.Start();
            var results = new int[3];
            // Combine 句柄持有聚合 TCS，在前置作业 TCS 全部 signal 后经 ContinueWith 翻转。
            // 用 CountdownEvent(3) 等三个前置作业体执行完，再轮询等待句柄状态传播完成。
            using var done = new CountdownEvent(3);

            var h0 = sched.Schedule(new LambdaIJob(() => { results[0] = 10; done.Signal(); }));
            var h1 = sched.Schedule(new LambdaIJob(() => { results[1] = 20; done.Signal(); }));
            var h2 = sched.Schedule(new LambdaIJob(() => { results[2] = 30; done.Signal(); }));

            var combined = JobHandle.Combine(h0, h1, h2);
            AssertTrue(done.Wait(TimeSpan.FromSeconds(5)), "All three prerequisites should finish within 5s.");

            // 完成信号经 TCS/ContinueWith 异步传播，轮询等待（避免慢机上 done.Signal 与 TCS 翻转之间的竞态）
            bool allCompleted = SpinWaitUntil(TimeSpan.FromSeconds(5),
                () => combined.IsCompleted && h0.IsCompleted && h1.IsCompleted && h2.IsCompleted);

            AssertTrue(combined.IsCompleted, "Combined handle IsCompleted should become true once all prerequisites finish.");
            AssertTrue(results[0] == 10 && results[1] == 20 && results[2] == 30,
                "All combined jobs should have executed and be visible.");
            // 前置句柄各自 IsCompleted 应翻转为 true（Worker signal TCS）
            AssertTrue(h0.IsCompleted && h1.IsCompleted && h2.IsCompleted,
                "Each prerequisite handle should be individually completed.");
        }

        /// <summary>轮询等待条件成立（用于 TCS/ContinueWith 异步传播的完成信号）。</summary>
        private static bool SpinWaitUntil(TimeSpan timeout, Func<bool> condition)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (condition()) return true;
                Thread.Sleep(10);
            }
            return condition();
        }

        // ── 单个 IJob：Schedule 后 Complete 阻塞、IsCompleted 翻转 ──
        public static void TestSingleJobCompleteSemantics()
        {
            using var sched = new JobScheduler(workerCount: 1);
            sched.Start();
            var flag = new int[1];
            var handle = sched.Schedule(new LambdaIJob(() => { Thread.Sleep(10); flag[0] = 42; }));

            // Complete 阻塞直到完成
            handle.Complete();
            AssertTrue(flag[0] == 42, "Job body should have run before Complete returns.");
            AssertTrue(handle.IsCompleted, "Handle should report completed after Complete().");
            AssertTrue(handle.IsValid, "Scheduled handle should be valid.");
        }

        // ── Flush：排空所有已提交作业（不持具体 handle 也能等待）──
        public static void TestFlushDrainsPendingJobs()
        {
            using var sched = new JobScheduler(workerCount: 2);
            sched.Start();
            var sum = new int[1];
            for (int i = 0; i < 50; i++)
            {
                int v = i;
                sched.Schedule(new LambdaIJob(() => Interlocked.Add(ref sum[0], v)));
            }
            sched.Flush();
            // 0+1+...+49 = 1225
            AssertTrue(Volatile.Read(ref sum[0]) == 1225,
                $"Flush should wait for all 50 jobs (expected sum 1225, got {Volatile.Read(ref sum[0])}).");
        }

        // ── ParallelFor 批次划分：batchSize=1 时每索引独立批次（覆盖边界）──
        public static void TestParallelForBatchSizeOneEachIndexSeparate()
        {
            using var sched = new JobScheduler(workerCount: 2);
            sched.Start();
            int count = 8;
            var mask = new bool[count];
            using var done = new CountdownEvent(count);

            sched.ScheduleParallelFor(0, count, batchSize: 1, (s, e) =>
            {
                // 每批只含一个索引
                AssertTrue(e - s == 1, $"batchSize=1 should yield single-index batch, got [{s},{e}).");
                mask[s] = true;
                done.Signal();
            });
            AssertTrue(done.Wait(TimeSpan.FromSeconds(5)),
                "All 8 single-index batches should finish within 5s.");

            for (int i = 0; i < count; i++)
                AssertTrue(mask[i], $"Each index must be visited exactly once (index {i} missed).");
        }

        // 轻量 Lambda→IJob 包装（测试专用，避免访问 private LambdaJob）
        private class LambdaIJob : IJob
        {
            private readonly Action _action;
            public LambdaIJob(Action action) { _action = action; }
            public void Execute() => _action();
        }
    }
}
