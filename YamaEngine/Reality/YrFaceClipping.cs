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

        /// <summary>
        /// 正确的入射面选择（第十一轮）：取与 faceNormal **最反向**的那个面，
        /// 即 |faceNormal·ax[k]| 最大的轴、sign = −sign(dot)。
        ///
        /// 既有 `IncidentFace(b, n)` 是「取 n·ax[k] 最小者」的启发式，对轴对齐
        /// 场景会落到侧面而非朝向 A 的面；单面路径保留它不动以维持零回归，
        /// 多面路径改用这个正确版本。
        /// </summary>
        public static RVec3[] IncidentFaceFor(in YrObb b, RVec3 faceNormal)
        {
            RVec3[] ax = AxesOf(b);
            int kinc = 0; float best = -1f; int sign = -1;
            for (int k = 0; k < 3; k++)
            {
                float d = faceNormal.Dot(ax[k]);
                if (MathF.Abs(d) > best) { best = MathF.Abs(d); kinc = k; sign = d >= 0f ? -1 : 1; }
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
        /// 第十一轮：入射面改为**每个参考面各自按该面法向重选**
        /// （`IncidentFaceFor`，修正旧启发式落到侧面的问题）；深度沿各自面法向测。
        /// 第十二轮：参考面候选改为「每轴只取与 n 同向的一侧 + 按 B 的
        /// 侵入粗筛」，不再让正反两面同时入候选；斜角多面接触仍是启发式。
        /// 输出经 YrContactReduction.ReduceByContactJacobian（含摩擦切向）
        /// 按 18D 接触 Jacobian 独立性缩到 4 点。
        /// </summary>
        public static YrClippedPoint[] BuildMultiFace(in YrObb a, in YrObb b,
                                                      in YrSatHit hit, int maxFaces = 3)
        {
            if (!hit.Hit) return Array.Empty<YrClippedPoint>();
            RVec3 n = hit.Normal.Normalized();
            RVec3[] ax = AxesOf(a);

            // 第十二轮：每轴**只取与 n 同向的那一侧**（hit.Normal 从 A 指向 B，
            // A 上朝向 B 的接触面法向应与 n 同向）。第十轮用 |dot| 让 ± 同轴
            // 的正反面同时入候选，会把背面也裁一遍；这里改成每轴至多 1 面。
            // 再加粗筛：B 中心沿该面法向到参考面的分离已大于 B 的投影半径时，
            // 该面不可能接触，直接剔除。
            var faces = new System.Collections.Generic.List<(float Align, int FaceId, int Axis, int Sign)>();
            for (int axis = 0; axis < 3; axis++)
            {
                float d = n.Dot(ax[axis]);
                float al = MathF.Abs(d);
                if (al < 0.4f) continue;
                int sign = d >= 0f ? 1 : -1;
                RVec3 fn = ax[axis] * (float)sign;
                RVec3 faceCenter = a.Center + fn * Half(a, axis);
                float sep = (b.Center - faceCenter).Dot(fn);
                float rb = YrContactManifold.ProjectedRadius(b, fn);
                if (sep > rb + Eps) continue;   // 该面完全在 B 之外
                faces.Add((al, FaceId(axis, sign), axis, sign));
            }
            // 保底：若粗筛把面全剔了，取 |dot| 最大轴的同向面（不再回退
            // BuildTagged —— 它的 Id 段 0..3 与多面 (faceId,pointId) 编码冲突，
            // 会让「点落在自己 Id 对应的参考面内」的断言失效）。
            if (faces.Count == 0)
            {
                int bestAxis = 0; float best = -1f;
                for (int axis = 0; axis < 3; axis++)
                {
                    float al = MathF.Abs(n.Dot(ax[axis]));
                    if (al > best) { best = al; bestAxis = axis; }
                }
                int sign = n.Dot(ax[bestAxis]) >= 0f ? 1 : -1;
                faces.Add((best, FaceId(bestAxis, sign), bestAxis, sign));
            }
            faces.Sort((x, y) => {
                if (x.Align > y.Align + 1e-9f) return -1;
                if (x.Align < y.Align - 1e-9f) return 1;
                return x.FaceId.CompareTo(y.FaceId);
            });
            int use = Math.Min(Math.Max(1, maxFaces), faces.Count);

            var all = new System.Collections.Generic.List<YrClippedPoint>();
            for (int f = 0; f < use; f++)
            {
                RefFace rf = RefFaceFor(a, faces[f].Axis, faces[f].Sign);
                int fid = faces[f].FaceId;
                // 第十一轮：每个参考面各自重选入射面（用该面法向），
                // 不再整对共用一次全局法向的启发式选择。
                RVec3[] incident = IncidentFaceFor(b, rf.Normal);

                var poly = new System.Collections.Generic.List<ClipPoint>();
                for (int i = 0; i < incident.Length; i++)
                {
                    RVec3 pt = incident[i];
                    RVec3 rel = pt - rf.Center;
                    poly.Add(new ClipPoint {
                        U = rel.Dot(rf.T1), V = rel.Dot(rf.T2),
                        D = (rf.Center - pt).Dot(rf.Normal), P = pt, Id = i
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
                // 第十一轮真修：Sutherland-Hodgman 的交点在 Lerp 里**继承起点 Id**，
                // 同一参考面内多个交点可能落到同一个 incident 顶点 Id。
                // 第十轮直接用 fid*4+cp.Id 会让面内出现重复 Id（CI 46006257966
                // 实测「多面 Id 冲突: 0」），逐点 warm start 会串台。
                // 修法：保留顶点原 Id 的跨帧稳定性；若该槽位已被占用（或越界），
                // 在该面内分配最小空闲槽位。面内 0..3 唯一，不同面段不重叠。
                var used = new bool[MaxPoints];
                for (int i = 0; i < cnt; i++)
                {
                    ClipPoint cp = cand[i].Point;
                    int local = cp.Id;
                    if (local < 0 || local >= MaxPoints || used[local])
                    {
                        local = 0;
                        while (local < MaxPoints && used[local]) local++;
                    }
                    if (local >= MaxPoints) continue;
                    used[local] = true;
                    all.Add(new YrClippedPoint(
                        rf.Center + rf.T1 * cp.U + rf.T2 * cp.V,
                        fid * MaxPoints + local));
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
            RVec3 nn = hit.Normal.Normalized();
            float ra = YrContactManifold.ProjectedRadius(a, nn);
            for (int i = 0; i < all.Length; i++)
            {
                pts[i] = all[i].Point; ids[i] = all[i].Id;
                // 物理侵入深度（沿碰撞法向），替代第十轮的「到中心距离」伪深度
                dep[i] = ra - (all[i].Point - a.Center).Dot(nn);
            }
            var red = YrContactReduction.ReduceByContactJacobian(
                pts, ids, dep, nn, a.Center, 4);
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
