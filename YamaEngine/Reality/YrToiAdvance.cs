using System;
namespace YamaEngine.Reality
{
    /// <summary>单个 TOI 子步的观测。</summary>
    public readonly struct YrToiStepInfo
    {
        public readonly float Span;
        public readonly int Contacts;
        public readonly float Toi;
        public YrToiStepInfo(float span, int contacts, float toi)
        { Span = span; Contacts = contacts; Toi = toi; }
    }

    /// <summary>
    /// 连续碰撞子步回退：把固定步 dt 按首次接触时刻拆成 [0,toi] 与 [toi,dt]，
    /// 在每个子步末用**纯速度级冲量**求解接触，绝不调用 CorrectPositions。
    ///
    /// 这解决第五轮留下的两个真问题：
    ///  ① 高速物体恰好本帧穿过薄板 → 拆步后在 toi 时刻捕获；
    ///  ② 位置投影是非物理瞬移，改变 r×(mv) → 这里完全不做位置修正，
    ///     因此总线动量与「自旋+轨道」总角动量严格守恒。
    ///
    /// 受控进入量 ToiSlop：tEnter 时刻 SAT 的 overlap 恰为 0 会被判 miss，
    /// 所以子步终点取 tEnter + 极小量，让约束有正的穿透深度可解。
    /// 这是 CCD 的标准做法，不是放宽容差。
    /// </summary>
    public sealed class YrToiAdvance
    {
        public const float ToiSlop = 1e-3f;
        public const float MinStep = 1e-6f;

        /// <summary>启用角速度保守膨胀的 CCD（默认 false）。</summary>
        public bool UseAngularInflation { get; set; } = false;
        public int LastCcdPairs { get; private set; }
        public int LastSubsteps { get; private set; }
        public int LastHits { get; private set; }
        public int LastContactPoints { get; private set; }
        public YrToiStepInfo[] LastSteps { get; private set; } = Array.Empty<YrToiStepInfo>();

        /// <summary>
        /// 推进一个固定步。obbs 为就地更新的工作数组；halfExtents 用于重同步。
        /// joints 在每个子步末求解（速度级），同样不做位置投影。
        /// </summary>
        public void Advance(RigidBody3D[] bodies, YrObb[] obbs, RVec3[] halfExtents,
                            YrNarrowPhase narrow, YrContactSolver contacts,
                            YrJointSolver joints, float dt, YrSurface surface,
                            int iterations, int maxSubsteps,
                            (int, int)[]? ccdPairs = null)
        {
            if (bodies == null || obbs == null || halfExtents == null)
                throw new ArgumentNullException(nameof(bodies));
            if (dt <= 0f) throw new ArgumentOutOfRangeException(nameof(dt));
            int n = Math.Min(bodies.Length, obbs.Length);
            if (halfExtents.Length < n) throw new ArgumentException("半尺寸数组不足");
            int cap = Math.Max(1, maxSubsteps);

            Sync(n, bodies, obbs, halfExtents);
            var steps = new System.Collections.Generic.List<YrToiStepInfo>();
            float remaining = dt;
            int hits = 0, totalPts = 0;
            // 一旦某子步已产生接触并用冲量求解，剩余时间直接一次积分完：
            // 否则「已接触 + 相对速度归零」会让 CCD 反复返回 toi≈0，
            // 把 cap 耗在零步长空转上，反而少推进了时间。
            bool contactResolved = false;

            for (int guard = 0; guard < cap && remaining > MinStep; guard++)
            {
                float toi;
                if (contactResolved) toi = -1f;
                else if (ccdPairs != null)
                {
                    // 候选对由整帧扫掠 AABB BVH 提供，是「整帧」超集；
                    // 子步 remaining 变小后仍是保守超集，不会漏报。
                    // 注意：BVH 的轨迹盒本身已含角速度膨胀，因此此分支下
                    // UseAngularInflation 不再叠加（避免双重保守）。
                    LastCcdPairs = ccdPairs.Length;
                    toi = YrSweptObb.EarliestToi(ccdPairs, obbs, bodies, remaining);
                }
                else
                {
                    LastCcdPairs = -1;
                    toi = UseAngularInflation
                        ? YrSweptObb.EarliestToiAllAngular(obbs, bodies, remaining)
                        : YrSweptObb.EarliestToiAll(obbs, bodies, remaining);
                }
                float span;
                if (toi >= 0f && toi < remaining)
                {
                    // 受控进入接触区，保证 SAT 得到正的穿透深度
                    span = MathF.Min(remaining, MathF.Max(MinStep, toi + ToiSlop));
                }
                else span = remaining;

                for (int i = 0; i < n; i++) bodies[i].Integrate(span);
                remaining -= span;
                Sync(n, bodies, obbs, halfExtents);

                var pairs = YrBroadPhase.SweepAndPrune(obbs);
                YrContact[] cs = narrow.Build(pairs, obbs, bodies, span, surface);
                contacts.SetContacts(cs);
                contacts.Prepare();
                contacts.Solve(iterations);
                narrow.CacheWarm(contacts.Contacts);
                joints.Solve(span, iterations);

                if (cs.Length > 0) { hits++; totalPts += cs.Length; contactResolved = true; }
                steps.Add(new YrToiStepInfo(span, cs.Length, toi));

                // toi<0 时 span 已等于 remaining，时间已全部推进，自然结束
                if (toi < 0f) break;
            }

            LastSubsteps = steps.Count;
            LastHits = hits;
            LastContactPoints = totalPts;
            LastSteps = steps.ToArray();
        }

        private static void Sync(int n, RigidBody3D[] bodies, YrObb[] obbs, RVec3[] halfExtents)
        {
            for (int i = 0; i < n; i++)
                obbs[i] = new YrObb(bodies[i].Position, bodies[i].Orientation, halfExtents[i]);
        }
    }
}
