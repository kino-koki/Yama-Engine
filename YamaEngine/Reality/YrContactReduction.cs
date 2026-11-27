using System;
namespace YamaEngine.Reality
{
    /// <summary>
    /// 接触流形缩减：把多面裁剪得到的 &gt;4 个点降到求解器可承受的 4 点，
    /// 同时尽量保留几何覆盖（PhysX / Jolt 的 manifold reduction 思路）。
    ///
    /// 策略：先取侵入最深的点（保证不丢主接触），再用「到已选集合的
    /// 最小距离最大」的 farthest-point 贪心逐次补点；所有比较用严格阈值 +
    /// Id 升序 tie-break，输出按 Id 排序 → 逐位确定。
    ///
    /// 诚实边界：这是**几何覆盖**启发式，不是求解器意义上的最优 4 点
    /// （后者需按接触 Jacobian 的线性独立性选择）；也未考虑摩擦锥。
    /// </summary>
    public static class YrContactReduction
    {
        public readonly struct YrReducedPoint
        {
            public readonly RVec3 Point;
            public readonly int Id;
            public readonly float Depth;
            public YrReducedPoint(RVec3 point, int id, float depth)
            { Point = point; Id = id; Depth = depth; }
        }

        public const int DefaultMax = 4;

        public static YrReducedPoint[] Reduce(RVec3[] points, int[] ids,
                                              float[] depths, int max = DefaultMax)
        {
            if (points == null || ids == null || depths == null)
                throw new ArgumentNullException(nameof(points));
            int n = Math.Min(points.Length, Math.Min(ids.Length, depths.Length));
            if (n == 0 || max <= 0) return Array.Empty<YrReducedPoint>();
            int take = Math.Min(max, n);

            // (depth desc, id asc) 排序，确定性
            var order = new int[n];
            for (int i = 0; i < n; i++) order[i] = i;
            float[] dp = depths; int[] idp = ids;
            Array.Sort(order, (x, y) => {
                if (dp[x] > dp[y] + 1e-9f) return -1;
                if (dp[x] < dp[y] - 1e-9f) return 1;
                return idp[x].CompareTo(idp[y]);
            });

            var chosen = new System.Collections.Generic.List<int>(take);
            chosen.Add(order[0]);   // 最深点必保留
            while (chosen.Count < take)
            {
                int bestIdx = -1;
                float bestScore = -1f;
                int bestId = int.MaxValue;
                for (int c = 0; c < n; c++)
                {
                    int idx = order[c];
                    bool already = false;
                    for (int q = 0; q < chosen.Count; q++)
                        if (chosen[q] == idx) { already = true; break; }
                    if (already) continue;
                    // 到已选集合的最小距离
                    float minD = float.PositiveInfinity;
                    for (int q = 0; q < chosen.Count; q++)
                    {
                        float d = (points[idx] - points[chosen[q]]).Length();
                        if (d < minD) minD = d;
                    }
                    bool better = minD > bestScore + 1e-9f
                               || (MathF.Abs(minD - bestScore) <= 1e-9f
                                   && ids[idx] < bestId);
                    if (better) { bestScore = minD; bestIdx = idx; bestId = ids[idx]; }
                }
                if (bestIdx < 0) break;
                chosen.Add(bestIdx);
            }

            var result = new YrReducedPoint[chosen.Count];
            for (int i = 0; i < chosen.Count; i++)
            {
                int idx = chosen[i];
                result[i] = new YrReducedPoint(points[idx], ids[idx], depths[idx]);
            }
            // 输出按 Id 排序，避免依赖贪心插入顺序
            Array.Sort(result, (x, y) => x.Id.CompareTo(y.Id));
            return result;
        }

        /// <summary>供测试：最远点采样的覆盖度（所选点到最近所选点的最小距离均值）。</summary>
        public static float Spread(RVec3[] points)
        {
            if (points == null || points.Length < 2) return 0f;
            float sum = 0f;
            for (int i = 0; i < points.Length; i++)
            {
                float minD = float.PositiveInfinity;
                for (int j = 0; j < points.Length; j++)
                {
                    if (i == j) continue;
                    float d = (points[i] - points[j]).Length();
                    if (d < minD) minD = d;
                }
                if (minD < float.PositiveInfinity) sum += minD;
            }
            return sum / points.Length;
        }
    }
}
