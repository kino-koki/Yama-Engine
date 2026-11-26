using System;
namespace YamaEngine.Reality
{
    /// <summary>世界轴对齐包围盒。</summary>
    public readonly struct YrAabb
    {
        public readonly RVec3 Min;
        public readonly RVec3 Max;
        public YrAabb(RVec3 min, RVec3 max) { Min = min; Max = max; }

        public bool Overlaps(in YrAabb o)
            => Min.X <= o.Max.X && Max.X >= o.Min.X
            && Min.Y <= o.Max.Y && Max.Y >= o.Min.Y
            && Min.Z <= o.Max.Z && Max.Z >= o.Min.Z;

        public bool Contains(RVec3 p, float tol = 1e-4f)
            => p.X >= Min.X - tol && p.X <= Max.X + tol
            && p.Y >= Min.Y - tol && p.Y <= Max.Y + tol
            && p.Z >= Min.Z - tol && p.Z <= Max.Z + tol;
    }

    /// <summary>
    /// 宽相：Sweep and Prune（沿 X 轴排序 + 增量剪枝）。
    /// 相对「两两 O(N²) 跑 SAT」，宽相先把候选对压到近邻，是任意物体数下
    /// 窄相可负担的前提。插入排序 + 索引升序 tie-break，保证逐位确定、可回放。
    /// </summary>
    public static class YrBroadPhase
    {
        private static readonly RVec3 Ex = new RVec3(1f, 0f, 0f);
        private static readonly RVec3 Ey = new RVec3(0f, 1f, 0f);
        private static readonly RVec3 Ez = new RVec3(0f, 0f, 1f);

        /// <summary>由 OBB 的世界姿态计算其 AABB（顶点投影的解析包络）。</summary>
        public static YrAabb FromObb(in YrObb o)
        {
            RVec3 ux = o.Orientation.Rotate(Ex);
            RVec3 uy = o.Orientation.Rotate(Ey);
            RVec3 uz = o.Orientation.Rotate(Ez);
            RVec3 h = o.HalfExtents;
            var ext = new RVec3(
                MathF.Abs(ux.X) * h.X + MathF.Abs(uy.X) * h.Y + MathF.Abs(uz.X) * h.Z,
                MathF.Abs(ux.Y) * h.X + MathF.Abs(uy.Y) * h.Y + MathF.Abs(uz.Y) * h.Z,
                MathF.Abs(ux.Z) * h.X + MathF.Abs(uy.Z) * h.Y + MathF.Abs(uz.Z) * h.Z);
            return new YrAabb(o.Center - ext, o.Center + ext);
        }

        /// <summary>OBB 的 8 个世界顶点（顺序固定：sx,sy,sz ∈ {-1,+1}）。</summary>
        public static RVec3[] ObbVertices(in YrObb o)
        {
            var v = new RVec3[8];
            int k = 0;
            for (int sx = -1; sx <= 1; sx += 2)
                for (int sy = -1; sy <= 1; sy += 2)
                    for (int sz = -1; sz <= 1; sz += 2)
                    {
                        var local = new RVec3(sx * o.HalfExtents.X,
                                              sy * o.HalfExtents.Y,
                                              sz * o.HalfExtents.Z);
                        v[k++] = o.Center + o.Orientation.Rotate(local);
                    }
            return v;
        }

        /// <summary>按 minX 升序（同值按索引升序）的确定性插入排序。</summary>
        public static int[] SortByMinX(YrAabb[] boxes)
        {
            if (boxes == null) throw new ArgumentNullException(nameof(boxes));
            int n = boxes.Length;
            var order = new int[n];
            for (int i = 0; i < n; i++) order[i] = i;
            for (int i = 1; i < n; i++)
            {
                int cur = order[i];
                int j = i - 1;
                while (j >= 0 && ShouldSwap(boxes[cur], cur, boxes[order[j]], order[j]))
                {
                    order[j + 1] = order[j];
                    j--;
                }
                order[j + 1] = cur;
            }
            return order;
        }

        private static bool ShouldSwap(in YrAabb a, int ia, in YrAabb b, int ib)
        {
            if (a.Min.X < b.Min.X - 1e-9f) return true;
            if (a.Min.X > b.Min.X + 1e-9f) return false;
            return ia < ib; // tie-break：索引升序，绝不依赖排序实现的稳定性
        }

        /// <summary>
        /// 返回所有 AABB 重叠的候选对 (A&lt;B)。X 轴剪枝：一旦 b.minX &gt; a.maxX，
        /// 后续所有 b 都可跳过（因为已按 minX 升序）。
        /// </summary>
        public static (int, int)[] SweepAndPrune(YrObb[] obbs)
        {
            if (obbs == null) throw new ArgumentNullException(nameof(obbs));
            int n = obbs.Length;
            if (n < 2) return Array.Empty<(int, int)>();
            var boxes = new YrAabb[n];
            for (int i = 0; i < n; i++) boxes[i] = FromObb(obbs[i]);
            int[] order = SortByMinX(boxes);
            var pairs = new System.Collections.Generic.List<(int, int)>();
            for (int oi = 0; oi < n; oi++)
            {
                int ia = order[oi];
                for (int oj = oi + 1; oj < n; oj++)
                {
                    int ib = order[oj];
                    if (boxes[ib].Min.X > boxes[ia].Max.X + 1e-9f) break;
                    if (!boxes[ia].Overlaps(boxes[ib])) continue;
                    int a = ia < ib ? ia : ib;
                    int b = ia < ib ? ib : ia;
                    pairs.Add((a, b));
                }
            }
            // 按 (A,B) 字典序输出，与插入顺序无关，保证逐位确定
            pairs.Sort((x, y) => x.Item1 != y.Item1 ? x.Item1.CompareTo(y.Item1)
                                              : x.Item2.CompareTo(y.Item2));
            return pairs.ToArray();
        }
    }
}
