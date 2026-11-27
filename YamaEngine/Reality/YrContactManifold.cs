using System;
namespace YamaEngine.Reality
{
    /// <summary>
    /// 接触流形生成：把 SAT 的「单个近似接触点」扩成最多 4 个接触点。
    ///
    /// 做法：取入射体 B 的 8 个顶点，计算每个顶点沿 -n 侵入参考体 A 的深度
    /// `d = ra - (v - centerA)·n`（ra 为 A 在 n 方向的投影半径），
    /// 保留 d&gt;0 且最深的至多 4 点；一点都没有时回退到 SAT 的单点。
    ///
    /// 诚实边界：这是顶点侵入近似，**不是** Sutherland-Hodgman 面裁剪得到的
    /// 完整接触流形；边-边接触、共面面接触的精确流形留给真机侧。
    /// </summary>
    public static class YrContactManifold
    {
        public const int MaxPoints = 4;

        /// <summary>A 在方向 n 上的投影半径。</summary>
        public static float ProjectedRadius(in YrObb o, RVec3 n)
        {
            RVec3 ux = o.Orientation.Rotate(new RVec3(1f, 0f, 0f));
            RVec3 uy = o.Orientation.Rotate(new RVec3(0f, 1f, 0f));
            RVec3 uz = o.Orientation.Rotate(new RVec3(0f, 0f, 1f));
            return MathF.Abs(n.Dot(ux)) * o.HalfExtents.X
                 + MathF.Abs(n.Dot(uy)) * o.HalfExtents.Y
                 + MathF.Abs(n.Dot(uz)) * o.HalfExtents.Z;
        }

        public static RVec3[] Build(in YrObb a, in YrObb b, in YrSatHit hit)
        {
            if (!hit.Hit) return Array.Empty<RVec3>();
            RVec3 n = hit.Normal.Normalized();
            float ra = ProjectedRadius(a, n);
            RVec3[] verts = YrBroadPhase.ObbVertices(b);

            // (depth, index, point)；插入排序按 depth 降序、index 升序
            var cand = new System.Collections.Generic.List<(float Depth, int Index, RVec3 Point)>();
            for (int i = 0; i < verts.Length; i++)
            {
                float d = ra - (verts[i] - a.Center).Dot(n);
                if (d <= 0f) continue;
                (float Depth, int Index, RVec3 Point) item = (d, i, verts[i]);
                int pos = cand.Count;
                for (int k = 0; k < cand.Count; k++)
                {
                    bool before = item.Depth > cand[k].Depth + 1e-9f
                               || (MathF.Abs(item.Depth - cand[k].Depth) <= 1e-9f
                                   && item.Index < cand[k].Index);
                    if (before) { pos = k; break; }
                }
                cand.Insert(pos, item);
            }

            int take = Math.Min(MaxPoints, cand.Count);
            if (take == 0) return new RVec3[] { hit.ContactPoint };
            var result = new RVec3[take];
            for (int i = 0; i < take; i++) result[i] = cand[i].Point;
            return result;
        }
    }
}
