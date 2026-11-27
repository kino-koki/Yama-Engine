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
    /// 诚实边界：单面路径仍是面-面近似；第十轮新增 BuildMultiFace 支持多参考面，
    /// 但入射面仍按全局法向选一次、斜角多面接触仍是启发式。
    /// 逐点 Id 已稳定（第八轮），多面下按 (faceId,pointId) 分段（第十轮）。
    /// </summary>
    public static class YrFaceClipping
    {
        public const int MaxPoints = 4;
        private const float Eps = 1e-5f;

        private struct ClipPoint
        {
            public float U, V, D;
            public RVec3 P;
            /// <summary>
            /// 稳定接触点标识：初始为 incident 面顶点序号 0..3；
            /// 裁剪交点继承起点 Id（Lerp 里 Id = A.Id）。
            /// 因此同一接触特征跨帧保持同一 Id，可驱动逐点 warm start。
            /// </summary>
            public int Id;
        }

        /// <summary>带稳定 Id 的接触点。</summary>
        public readonly struct YrClippedPoint
        {
            public readonly RVec3 Point;
            public readonly int Id;
            public YrClippedPoint(RVec3 point, int id) { Point = point; Id = id; }
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
                P = A.P + (B.P - A.P) * t,
                Id = A.Id
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
        /// 面裁剪生成带稳定 Id 的接触流形（最多 4 点）；
        /// 无有效点时回退到 SAT 的单近似点（Id=0），保证不返回空。
        /// </summary>
        public static YrClippedPoint[] BuildTagged(in YrObb a, in YrObb b, in YrSatHit hit)
        {
            if (!hit.Hit) return Array.Empty<YrClippedPoint>();
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
                    D = (rf.Center - p).Dot(n), P = p, Id = i
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
            if (take == 0)
                return new YrClippedPoint[] { new YrClippedPoint(hit.ContactPoint, 0) };

            var result = new YrClippedPoint[take];
            for (int i = 0; i < take; i++)
            {
                ClipPoint cp = cand[i].Point;
                result[i] = new YrClippedPoint(
                    rf.Center + rf.T1 * cp.U + rf.T2 * cp.V, cp.Id);
            }
            return result;
        }

        /// <summary>兼容既有调用：只返回 3D 点（语义与第七轮完全一致）。</summary>
        public static RVec3[] Build(in YrObb a, in YrObb b, in YrSatHit hit)
        {
            var tagged = BuildTagged(a, b, hit);
            var result = new RVec3[tagged.Length];
            for (int i = 0; i < tagged.Length; i++) result[i] = tagged[i].Point;
            return result;
        }

        /// <summary>按显式 (axis,sign) 取 A 的某个面，faceId = axis*2 + (sign>0?1:0)。</summary>
        public static RefFace RefFaceFor(in YrObb a, int axis, int sign)
        {
            RVec3[] ax = AxesOf(a);
            RVec3 n = ax[axis] * (float)sign;
            RVec3 center = a.Center + n * Half(a, axis);
            int k1 = (axis + 1) % 3, k2 = (axis + 2) % 3;
            return new RefFace(center, ax[k1], ax[k2], Half(a, k1), Half(a, k2), n);
        }

        public static int FaceId(int axis, int sign) => axis * 2 + (sign > 0 ? 1 : 0);

        /// <summary>
        /// 多面接触流形：对 A 上与碰撞法向足够对齐的多个参考面分别做面裁剪，
        /// 每个点的稳定 Id = faceId*4 + 局部顶点/交点 Id（0..23）。
        ///
        /// 这解决第八轮留下的局限：「逐点 Id 只在单对参考/入射面内稳定，
        /// 多面同时接触时不同面可能复用同一 Id」。现在不同面的 Id 段不重叠，
        /// 逐点 warm start 在多面接触下也不会串台。
        ///
        /// 诚实边界：入射面仍按**全局碰撞法向**选一次（与 BuildTagged 一致），
        /// 不是每个参考面各自重选；轴对齐盒的共面接触可覆盖，斜角多面接触
        /// 仍是启发式。输出经 YrContactReduction 缩减到默认 4 点。
        /// </summary>
        public static YrClippedPoint[] BuildMultiFace(in YrObb a, in YrObb b,
                                                      in YrSatHit hit, int maxFaces = 3)
        {
            if (!hit.Hit) return Array.Empty<YrClippedPoint>();
            RVec3 n = hit.Normal.Normalized();
            RVec3[] ax = AxesOf(a);

            // 候选参考面：|n·faceNormal| >= 0.4，按 (|dot| desc, faceId asc) 排序
            var faces = new System.Collections.Generic.List<(float Align, int FaceId, int Axis, int Sign)>();
            for (int axis = 0; axis < 3; axis++)
                for (int sign = -1; sign <= 1; sign += 2)
                {
                    float al = MathF.Abs(n.Dot(ax[axis] * (float)sign));
                    if (al >= 0.4f) faces.Add((al, FaceId(axis, sign), axis, sign));
                }
            if (faces.Count == 0) return BuildTagged(a, b, hit);
            faces.Sort((x, y) => {
                if (x.Align > y.Align + 1e-9f) return -1;
                if (x.Align < y.Align - 1e-9f) return 1;
                return x.FaceId.CompareTo(y.FaceId);
            });
            int use = Math.Min(Math.Max(1, maxFaces), faces.Count);

            RVec3[] incident = IncidentFace(b, n);
            var all = new System.Collections.Generic.List<YrClippedPoint>();
            for (int f = 0; f < use; f++)
            {
                RefFace rf = RefFaceFor(a, faces[f].Axis, faces[f].Sign);
                int fid = faces[f].FaceId;

                var poly = new System.Collections.Generic.List<ClipPoint>();
                for (int i = 0; i < incident.Length; i++)
                {
                    RVec3 pt = incident[i];
                    RVec3 rel = pt - rf.Center;
                    poly.Add(new ClipPoint {
                        U = rel.Dot(rf.T1), V = rel.Dot(rf.T2),
                        D = (rf.Center - pt).Dot(n), P = pt, Id = i
                    });
                }
                for (int e = 0; e < 4; e++) poly = Clip(poly, e, rf.H1, rf.H2);

                // 按侵入深度取该面至多 4 点
                var cand = new System.Collections.Generic.List<(float D, int Index, ClipPoint Point)>();
                for (int i = 0; i < poly.Count; i++)
                {
                    if (poly[i].D < -Eps) continue;
                    (float D, int Index, ClipPoint Point) item = (poly[i].D, i, poly[i]);
                    int pos = cand.Count;
                    for (int q = 0; q < cand.Count; q++)
                    {
                        bool before = item.D > cand[q].D + 1e-9f
                                   || (MathF.Abs(item.D - cand[q].D) <= 1e-9f
                                       && item.Index < cand[q].Index);
                        if (before) { pos = q; break; }
                    }
                    cand.Insert(pos, item);
                }
                int cnt = Math.Min(MaxPoints, cand.Count);
                for (int i = 0; i < cnt; i++)
                {
                    ClipPoint cp = cand[i].Point;
                    all.Add(new YrClippedPoint(
                        rf.Center + rf.T1 * cp.U + rf.T2 * cp.V,
                        fid * MaxPoints + cp.Id));
                }
            }
            if (all.Count == 0) return BuildTagged(a, b, hit);
            all.Sort((x, y) => x.Id.CompareTo(y.Id));
            return all.ToArray();
        }

        /// <summary>多面 + 缩减的一体入口：返回至多 4 点，Id 保持 (faceId,pointId) 编码。</summary>
        public static YrClippedPoint[] BuildMultiFaceReduced(in YrObb a, in YrObb b,
                                                            in YrSatHit hit, int maxFaces = 3)
        {
            var all = BuildMultiFace(a, b, hit, maxFaces);
            if (all.Length <= 4) return all;
            var pts = new RVec3[all.Length];
            var ids = new int[all.Length];
            var dep = new float[all.Length];
            for (int i = 0; i < all.Length; i++)
            {
                pts[i] = all[i].Point; ids[i] = all[i].Id;
                dep[i] = (all[i].Point - a.Center).Length();
            }
            var red = YrContactReduction.Reduce(pts, ids, dep, 4);
            var result = new YrClippedPoint[red.Length];
            for (int i = 0; i < red.Length; i++)
                result[i] = new YrClippedPoint(red[i].Point, red[i].Id);
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
