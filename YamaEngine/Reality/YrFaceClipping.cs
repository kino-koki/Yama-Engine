using System;
namespace YamaEngine.Reality
{
    /// <summary>
    /// 完整接触流形：Sutherland-Hodgman 面裁剪。
    ///
    /// 第五轮的 `YrContactManifold.Build` 是「取入射体 8 顶点里侵入最深的至多 4 个」，
    /// 对边-边接触、共面面接触会给出偏斜甚至退化的点集。这里做标准做法：
    ///   ① 参考面 = A 上法向与碰撞法向最对齐的面；
    ///   ② 入射面 = B 上法向最反向的面，取其 4 个顶点；
    ///   ③ 把入射面顶点投到参考面局部 2D 坐标 (u,v)，
    ///      用参考面矩形的 4 条侧边做 Sutherland-Hodgman 裁剪；
    ///   ④ 按侵入深度 d 保留至多 4 点，输出参考面上的 3D 接触点。
    ///
    /// 诚实边界：这仍是**面-面**近似（取单对参考/入射面），不处理多面同时接触、
    /// 也不生成带持久 id 的逐点 warm start 缓存；旋转盒的 incident 面选择是启发式。
    /// 但它已经不再是「顶点侵入近似」，是真正的面裁剪流形。
    /// </summary>
    public static class YrFaceClipping
    {
        public const int MaxPoints = 4;
        private const float Eps = 1e-5f;

        private struct ClipPoint
        {
            public float U, V, D;
            public RVec3 P;
        }

        private static readonly RVec3 Ex = new RVec3(1f, 0f, 0f);
        private static readonly RVec3 Ey = new RVec3(0f, 1f, 0f);
        private static readonly RVec3 Ez = new RVec3(0f, 0f, 1f);

        private static RVec3[] AxesOf(in YrObb o)
            => new RVec3[] {
                o.Orientation.Rotate(Ex), o.Orientation.Rotate(Ey), o.Orientation.Rotate(Ez)
            };

        private static float Half(in YrObb o, int k)
            => k == 0 ? o.HalfExtents.X : (k == 1 ? o.HalfExtents.Y : o.HalfExtents.Z);

        /// <summary>参考面信息，暴露给测试用于验证裁剪点落在矩形内。</summary>
        public readonly struct RefFace
        {
            public readonly RVec3 Center;
            public readonly RVec3 T1, T2;
            public readonly float H1, H2;
            public readonly RVec3 Normal;
            public RefFace(RVec3 center, RVec3 t1, RVec3 t2, float h1, float h2, RVec3 normal)
            { Center = center; T1 = t1; T2 = t2; H1 = h1; H2 = h2; Normal = normal; }
        }

        public static RefFace ReferenceFace(in YrObb a, RVec3 n)
        {
            RVec3[] ax = AxesOf(a);
            int kref = 0; float best = -1f; int sign = 1;
            for (int k = 0; k < 3; k++)
            {
                float d = n.Dot(ax[k]);
                if (MathF.Abs(d) > best) { best = MathF.Abs(d); kref = k; sign = d >= 0f ? 1 : -1; }
            }
            RVec3 center = a.Center + ax[kref] * (sign * Half(a, kref));
            int k1 = (kref + 1) % 3, k2 = (kref + 2) % 3;
            return new RefFace(center, ax[k1], ax[k2], Half(a, k1), Half(a, k2),
                               ax[kref] * (float)sign);
        }

        /// <summary>入射面 4 个顶点（顺序固定：-,- / +,- / +,+ / -,+）。</summary>
        public static RVec3[] IncidentFace(in YrObb b, RVec3 n)
        {
            RVec3[] ax = AxesOf(b);
            int kinc = 0; float best = float.MaxValue; int sign = 1;
            for (int k = 0; k < 3; k++)
            {
                float d = n.Dot(ax[k]);
                if (d < best) { best = d; kinc = k; sign = d >= 0f ? 1 : -1; }
            }
            RVec3 center = b.Center + ax[kinc] * (sign * Half(b, kinc));
            int k1 = (kinc + 1) % 3, k2 = (kinc + 2) % 3;
            RVec3 t1 = ax[k1] * Half(b, k1), t2 = ax[k2] * Half(b, k2);
            return new RVec3[] {
                center - t1 - t2, center + t1 - t2,
                center + t1 + t2, center - t1 + t2
            };
        }

        /// <summary>线性边函数 f(u,v)=au·u+av·v+c，f &gt;= -Eps 为内侧。</summary>
        private static float Edge(float u, float v, int edge, float h1, float h2)
            => edge == 0 ? u + h1 : edge == 1 ? h1 - u
             : edge == 2 ? v + h2 : h2 - v;

        private static ClipPoint Lerp(in ClipPoint A, in ClipPoint B, float t)
            => new ClipPoint {
                U = A.U + (B.U - A.U) * t,
                V = A.V + (B.V - A.V) * t,
                D = A.D + (B.D - A.D) * t,
                P = A.P + (B.P - A.P) * t
            };

        private static System.Collections.Generic.List<ClipPoint> Clip(
            System.Collections.Generic.List<ClipPoint> poly, int edge, float h1, float h2)
        {
            var result = new System.Collections.Generic.List<ClipPoint>();
            if (poly.Count == 0) return result;
            ClipPoint prev = poly[poly.Count - 1];
            float fp = Edge(prev.U, prev.V, edge, h1, h2);
            for (int i = 0; i < poly.Count; i++)
            {
                ClipPoint cur = poly[i];
                float fc = Edge(cur.U, cur.V, edge, h1, h2);
                bool inP = fp >= -Eps, inC = fc >= -Eps;
                if (inC)
                {
                    if (!inP) result.Add(Lerp(prev, cur, fp / (fp - fc)));
                    result.Add(cur);
                }
                else if (inP)
                {
                    result.Add(Lerp(prev, cur, fp / (fp - fc)));
                }
                prev = cur; fp = fc;
            }
            return result;
        }

        /// <summary>
        /// 面裁剪生成接触流形。返回参考面上的 3D 点（最多 4 个）；
        /// 无有效点时回退到 SAT 的单近似点，保证不返回空。
        /// </summary>
        public static RVec3[] Build(in YrObb a, in YrObb b, in YrSatHit hit)
        {
            if (!hit.Hit) return Array.Empty<RVec3>();
            RVec3 n = hit.Normal.Normalized();
            RefFace rf = ReferenceFace(a, n);
            RVec3[] incident = IncidentFace(b, n);

            var poly = new System.Collections.Generic.List<ClipPoint>();
            for (int i = 0; i < incident.Length; i++)
            {
                RVec3 p = incident[i];
                RVec3 rel = p - rf.Center;
                poly.Add(new ClipPoint {
                    U = rel.Dot(rf.T1), V = rel.Dot(rf.T2),
                    D = (rf.Center - p).Dot(n), P = p
                });
            }
            for (int e = 0; e < 4; e++) poly = Clip(poly, e, rf.H1, rf.H2);

            var cand = new System.Collections.Generic.List<(float D, int Index, ClipPoint Point)>();
            for (int i = 0; i < poly.Count; i++)
            {
                if (poly[i].D < -Eps) continue;
                (float D, int Index, ClipPoint Point) item = (poly[i].D, i, poly[i]);
                int pos = cand.Count;
                for (int k = 0; k < cand.Count; k++)
                {
                    bool before = item.D > cand[k].D + 1e-9f
                               || (MathF.Abs(item.D - cand[k].D) <= 1e-9f
                                   && item.Index < cand[k].Index);
                    if (before) { pos = k; break; }
                }
                cand.Insert(pos, item);
            }
            int take = Math.Min(MaxPoints, cand.Count);
            if (take == 0) return new RVec3[] { hit.ContactPoint };

            var result = new RVec3[take];
            for (int i = 0; i < take; i++)
            {
                ClipPoint cp = cand[i].Point;
                result[i] = rf.Center + rf.T1 * cp.U + rf.T2 * cp.V;
            }
            return result;
        }

        /// <summary>供测试断言：裁剪点是否落在参考面矩形内（含 Eps 余量）。</summary>
        public static bool InsideReference(in RefFace rf, RVec3 p, float tol = 1e-3f)
        {
            RVec3 rel = p - rf.Center;
            return MathF.Abs(rel.Dot(rf.T1)) <= rf.H1 + tol
                && MathF.Abs(rel.Dot(rf.T2)) <= rf.H2 + tol;
        }
    }
}
