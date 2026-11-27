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

        /// <summary>
        /// 按接触 Jacobian（6D wrench 行 [n, r×n]）的线性独立性缩减。
        ///
        /// 几何 farthest-point 只看点间距，会丢掉「力臂方向不同但点很近」的接触
        /// —— 而这些点对约束求解恰恰最有用。这里改用 modified Gram-Schmidt：
        /// 先保留侵入最深点，之后每次选「在已选 wrench 行张成空间里残差最大」
        /// 的点，等价于挑一组线性最独立的接触约束。
        ///
        /// 诚实边界：用单法向 n 与单参考中心 center 构造 6D 行，未含两体惯量
        /// 与摩擦锥切向；是 Jacobian 独立性的一阶近似，不是完整 LCP 选点。
        /// </summary>
        public static YrReducedPoint[] ReduceByWrench(RVec3[] points, int[] ids,
                                                      float[] depths, RVec3 normal,
                                                      RVec3 center, int max = DefaultMax)
        {
            if (points == null || ids == null || depths == null)
                throw new ArgumentNullException(nameof(points));
            int n = Math.Min(points.Length, Math.Min(ids.Length, depths.Length));
            if (n == 0 || max <= 0) return Array.Empty<YrReducedPoint>();
            int take = Math.Min(max, n);
            RVec3 nn = normal.Normalized();
            if (nn.LengthSq() < 0.5f) nn = new RVec3(0f, 0f, 1f);

            var rows = new float[n][];
            for (int i = 0; i < n; i++) rows[i] = WrenchRow(points[i], center, nn);

            // (depth desc, id asc) 排序，确定性
            float[] dp = depths; int[] idp = ids;
            var order = new int[n];
            for (int i = 0; i < n; i++) order[i] = i;
            Array.Sort(order, (x, y) => {
                if (dp[x] > dp[y] + 1e-9f) return -1;
                if (dp[x] < dp[y] - 1e-9f) return 1;
                return idp[x].CompareTo(idp[y]);
            });

            var chosen = new System.Collections.Generic.List<int>(take);
            var basis = new System.Collections.Generic.List<float[]>(take);
            chosen.Add(order[0]);
            basis.Add(Normalize((float[])rows[order[0]].Clone()));

            while (chosen.Count < take)
            {
                int bestIdx = -1;
                float bestResidual = -1f;
                int bestId = int.MaxValue;
                for (int c = 0; c < n; c++)
                {
                    int idx = order[c];
                    bool already = false;
                    for (int q = 0; q < chosen.Count; q++)
                        if (chosen[q] == idx) { already = true; break; }
                    if (already) continue;
                    float r = ResidualNorm(rows[idx], basis);
                    bool better = r > bestResidual + 1e-9f
                               || (MathF.Abs(r - bestResidual) <= 1e-9f
                                   && ids[idx] < bestId);
                    if (better) { bestResidual = r; bestIdx = idx; bestId = ids[idx]; }
                }
                if (bestIdx < 0) break;
                chosen.Add(bestIdx);
                // 严格 modified Gram-Schmidt：基里存的是正交归一化后的残差方向，
                // 而不是原始 wrench 行（否则后续投影不是正交投影）。
                var ortho = Orthogonalize(rows[bestIdx], basis);
                basis.Add(Norm(ortho) > 1e-9f ? Normalize(ortho) : new float[6]);
            }

            var result = new YrReducedPoint[chosen.Count];
            for (int i = 0; i < chosen.Count; i++)
            {
                int idx = chosen[i];
                result[i] = new YrReducedPoint(points[idx], ids[idx], depths[idx]);
            }
            Array.Sort(result, (x, y) => x.Id.CompareTo(y.Id));
            return result;
        }

        private static float[] WrenchRow(RVec3 p, RVec3 center, RVec3 n)
        {
            RVec3 t = (p - center).Cross(n);
            var j = new float[6] { n.X, n.Y, n.Z, t.X, t.Y, t.Z };
            float norm = 0f;
            for (int k = 0; k < 6; k++) norm += j[k] * j[k];
            norm = MathF.Sqrt(norm);
            if (norm > 1e-9f)
                for (int k = 0; k < 6; k++) j[k] /= norm;
            return j;
        }

        private static float[] Orthogonalize(float[] j,
            System.Collections.Generic.List<float[]> basis)
        {
            var v = (float[])j.Clone();
            for (int q = 0; q < basis.Count; q++)
            {
                float d = 0f;
                for (int k = 0; k < 6; k++) d += v[k] * basis[q][k];
                for (int k = 0; k < 6; k++) v[k] -= d * basis[q][k];
            }
            return v;
        }

        private static float Norm(float[] v)
        {
            float s = 0f;
            for (int k = 0; k < v.Length; k++) s += v[k] * v[k];
            return MathF.Sqrt(s);
        }

        private static float[] Normalize(float[] v)
        {
            float n = Norm(v);
            if (n <= 1e-9f) return new float[6];
            var r = new float[6];
            for (int k = 0; k < 6; k++) r[k] = v[k] / n;
            return r;
        }

        private static float ResidualNorm(float[] j,
            System.Collections.Generic.List<float[]> basis)
            => Norm(Orthogonalize(j, basis));

        /// <summary>供测试：一组选中点的 wrench 行 Gram-Schmidt 残差和（独立性指标）。</summary>
        public static float WrenchIndependence(RVec3[] points, RVec3 normal, RVec3 center)
        {
            if (points == null || points.Length == 0) return 0f;
            RVec3 nn = normal.Normalized();
            if (nn.LengthSq() < 0.5f) nn = new RVec3(0f, 0f, 1f);
            var basis = new System.Collections.Generic.List<float[]>();
            float sum = 0f;
            for (int i = 0; i < points.Length; i++)
            {
                float[] row = WrenchRow(points[i], center, nn);
                var ortho = Orthogonalize(row, basis);
                float r = Norm(ortho);
                sum += r;
                basis.Add(r > 1e-9f ? Normalize(ortho) : new float[6]);
            }
            return sum;
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
