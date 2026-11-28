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

        /// <summary>
        /// 按完整接触 Jacobian（含摩擦切向）缩减（第十二轮）。
        ///
        /// `ReduceByWrench` 只构造法向的 6D 行 `[n, r×n]`，丢掉了两个摩擦切向
        /// 方向上的力臂信息。这里每个接触点构造 **18D 行**：
        ///   `[ n, r×n, t1, r×t1, t2, r×t2 ]`（t1,t2 为法向的正交基）
        /// 即法向 + 两个切向各自的 6D wrench，做严格 modified Gram-Schmidt
        /// 挑残差最大的点，等价于在含摩擦的约束空间里选线性最独立的接触。
        ///
        /// linearWeight / angularWeight 允许按两体有效质量给线性段与角段加权；
        /// 默认是 1:1。诚实边界：这是**外部标量权重**，不是从两体惯量张量算出的
        /// 完整有效质量矩阵；仍未含摩擦锥的非线性边界与切向耦合。
        /// </summary>
        public static YrReducedPoint[] ReduceByContactJacobian(
            RVec3[] points, int[] ids, float[] depths, RVec3 normal, RVec3 center,
            int max = DefaultMax, float linearWeight = 1f, float angularWeight = 1f)
        {
            if (points == null || ids == null || depths == null)
                throw new ArgumentNullException(nameof(points));
            int n = Math.Min(points.Length, Math.Min(ids.Length, depths.Length));
            if (n == 0 || max <= 0) return Array.Empty<YrReducedPoint>();
            int take = Math.Min(max, n);
            TangentBasis(normal, out RVec3 nn, out RVec3 t1, out RVec3 t2);

            var rows = new float[n][];
            for (int i = 0; i < n; i++)
                rows[i] = ContactRow(points[i], center, nn, t1, t2,
                                     linearWeight, angularWeight);

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
            basis.Add(NormalizeN((float[])rows[order[0]].Clone()));

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
                    float r = ResidualNormN(rows[idx], basis);
                    bool better = r > bestResidual + 1e-9f
                               || (MathF.Abs(r - bestResidual) <= 1e-9f
                                   && ids[idx] < bestId);
                    if (better) { bestResidual = r; bestIdx = idx; bestId = ids[idx]; }
                }
                if (bestIdx < 0) break;
                chosen.Add(bestIdx);
                var ortho = OrthogonalizeN(rows[bestIdx], basis);
                basis.Add(NormN(ortho) > 1e-9f ? NormalizeN(ortho) : new float[18]);
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

        /// <summary>确定性法向正交基：取与 n 最不平行的坐标轴做 Gram-Schmidt。</summary>
        public static void TangentBasis(RVec3 normal, out RVec3 n,
                                        out RVec3 t1, out RVec3 t2)
        {
            n = normal.Normalized();
            if (n.LengthSq() < 0.5f) n = new RVec3(0f, 0f, 1f);
            float ax = MathF.Abs(n.X), ay = MathF.Abs(n.Y), az = MathF.Abs(n.Z);
            RVec3 e = (ax <= ay && ax <= az) ? new RVec3(1f, 0f, 0f)
                    : (ay <= az ? new RVec3(0f, 1f, 0f) : new RVec3(0f, 0f, 1f));
            t1 = (e - n * n.Dot(e)).Normalized();
            t2 = n.Cross(t1).Normalized();
        }

        /// <summary>暴露 18D 接触 Jacobian 行，供测试断言切向力臂非零。</summary>
        public static float[] ContactJacobianRow(RVec3 p, RVec3 center, RVec3 normal,
                                                 float linearWeight = 1f,
                                                 float angularWeight = 1f)
        {
            TangentBasis(normal, out RVec3 nn, out RVec3 t1, out RVec3 t2);
            return ContactRow(p, center, nn, t1, t2, linearWeight, angularWeight);
        }

        private static float[] ContactRow(RVec3 p, RVec3 center, RVec3 n,
                                          RVec3 t1, RVec3 t2,
                                          float lw, float aw)
        {
            RVec3 r = p - center;
            RVec3[] dirs = new RVec3[] { n, t1, t2 };
            var j = new float[18];
            for (int k = 0; k < 3; k++)
            {
                RVec3 v = dirs[k];
                j[k * 6 + 0] = v.X * lw;
                j[k * 6 + 1] = v.Y * lw;
                j[k * 6 + 2] = v.Z * lw;
                RVec3 c = r.Cross(v);
                j[k * 6 + 3] = c.X * aw;
                j[k * 6 + 4] = c.Y * aw;
                j[k * 6 + 5] = c.Z * aw;
            }
            return NormalizeN(j);
        }

        private static float[] OrthogonalizeN(float[] j,
            System.Collections.Generic.List<float[]> basis)
        {
            var v = (float[])j.Clone();
            for (int q = 0; q < basis.Count; q++)
            {
                float d = 0f;
                for (int k = 0; k < v.Length; k++) d += v[k] * basis[q][k];
                for (int k = 0; k < v.Length; k++) v[k] -= d * basis[q][k];
            }
            return v;
        }

        private static float NormN(float[] v)
        {
            float s = 0f;
            for (int k = 0; k < v.Length; k++) s += v[k] * v[k];
            return MathF.Sqrt(s);
        }

        private static float[] NormalizeN(float[] v)
        {
            float nrm = NormN(v);
            if (nrm <= 1e-9f) return new float[v.Length];
            var r = new float[v.Length];
            for (int k = 0; k < v.Length; k++) r[k] = v[k] / nrm;
            return r;
        }

        private static float ResidualNormN(float[] j,
            System.Collections.Generic.List<float[]> basis)
            => NormN(OrthogonalizeN(j, basis));

        /// <summary>
        /// 通用缩减入口：直接给一组（已归一化、等长的）约束行，按严格 MGS
        /// 线性独立性挑至多 max 个。第十三轮把矩阵构造与选点解耦，
        /// 第十/十一/十二轮的 Reduce / ReduceByWrench / ReduceByContactJacobian
        /// 语义都保持不变。
        /// </summary>
        public static YrReducedPoint[] ReduceByRows(float[][] rows, int[] ids,
                                                     float[] depths, int max = DefaultMax)
        {
            if (rows == null || ids == null || depths == null)
                throw new ArgumentNullException(nameof(rows));
            int n = Math.Min(rows.Length, Math.Min(ids.Length, depths.Length));
            if (n == 0 || max <= 0) return Array.Empty<YrReducedPoint>();
            int dim = rows[0] == null ? 0 : rows[0].Length;
            if (dim <= 0) return Array.Empty<YrReducedPoint>();
            int take = Math.Min(max, n);

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
            basis.Add(NormalizeN((float[])rows[order[0]].Clone()));

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
                    float r = ResidualNormN(rows[idx], basis);
                    bool better = r > bestResidual + 1e-9f
                               || (MathF.Abs(r - bestResidual) <= 1e-9f
                                   && ids[idx] < bestId);
                    if (better) { bestResidual = r; bestIdx = idx; bestId = ids[idx]; }
                }
                if (bestIdx < 0) break;
                chosen.Add(bestIdx);
                var ortho = OrthogonalizeN(rows[bestIdx], basis);
                basis.Add(NormN(ortho) > 1e-9f ? NormalizeN(ortho) : new float[dim]);
            }

            // 注意：通用入口只返回 Id/Depth/Point 的载体，调用方负责回填 Point。
            var result = new YrReducedPoint[chosen.Count];
            for (int i = 0; i < chosen.Count; i++)
            {
                int idx = chosen[i];
                result[i] = new YrReducedPoint(RVec3.Zero, ids[idx], depths[idx]);
            }
            Array.Sort(result, (x, y) => x.Id.CompareTo(y.Id));
            return result;
        }

        /// <summary>
        /// 按两体有效质量矩阵 + 摩擦锥加权的 18D 接触 Jacobian 缩减（第十三轮）。
        /// 每一行的权重从 `YrContactEffectiveMass.WeightedContactRow` 算出，
        /// 不再是外部标量。返回的 Point 已回填。
        /// </summary>
        public static YrReducedPoint[] ReduceByEffectiveMass(
            RVec3[] points, int[] ids, float[] depths, RVec3 normal,
            in YrContactMass mass, int max = DefaultMax)
        {
            if (points == null) throw new ArgumentNullException(nameof(points));
            int n = Math.Min(points.Length,
                             Math.Min(ids == null ? 0 : ids.Length,
                                      depths == null ? 0 : depths.Length));
            if (n == 0 || max <= 0) return Array.Empty<YrReducedPoint>();
            var rows = new float[n][];
            for (int i = 0; i < n; i++)
                rows[i] = YrContactEffectiveMass.WeightedContactRow(
                    mass, points[i], normal);
            var red = ReduceByRows(rows, ids, depths, max);
            // 回填 3D 点（Id 唯一，用 Id 查原点集）
            var byId = new System.Collections.Generic.Dictionary<int, RVec3>();
            for (int i = 0; i < n; i++) byId[ids[i]] = points[i];
            var result = new YrReducedPoint[red.Length];
            for (int i = 0; i < red.Length; i++)
            {
                RVec3 pt = byId.TryGetValue(red[i].Id, out RVec3 v) ? v : RVec3.Zero;
                result[i] = new YrReducedPoint(pt, red[i].Id, red[i].Depth);
            }
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
