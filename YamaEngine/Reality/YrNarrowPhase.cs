using System;
namespace YamaEngine.Reality
{
    /// <summary>窄相材质参数（成对默认值，逐对可覆盖）。</summary>
    public readonly struct YrSurface
    {
        public readonly float Restitution;
        public readonly float FrictionStatic;
        public readonly float FrictionDynamic;
        public YrSurface(float restitution, float frictionStatic, float frictionDynamic)
        { Restitution = restitution; FrictionStatic = frictionStatic; FrictionDynamic = frictionDynamic; }
        public static YrSurface Default => new YrSurface(0.2f, 0.6f, 0.4f);
    }

    /// <summary>
    /// 窄相编排：宽相候选对 → CCD 包围球预筛/TOI 排序 → SAT → 接触流形 →
    /// YrContact[]（带跨帧 warm start 缓存）。
    ///
    /// 诚实边界：CCD 本轮只做「预筛 + 最早 TOI 观测 + 接触对优先级排序」，
    /// **尚未实现 TOI 子步回退**（把固定步拆成 [0,toi] 与 [toi,dt]）。
    /// 所以「高速物体恰好在本帧穿过薄板」仍可能漏检，这一环留给真机侧。
    /// </summary>
    public sealed class YrNarrowPhase
    {
        private readonly System.Collections.Generic.Dictionary<(int, int), float> _warmNormal
            = new System.Collections.Generic.Dictionary<(int, int), float>();
        private readonly System.Collections.Generic.Dictionary<(int, int, int), float> _warmPoints
            = new System.Collections.Generic.Dictionary<(int, int, int), float>();
        private int[] _lastPointIds = Array.Empty<int>();

        public float LastEarliestToi { get; private set; } = -1f;
        public int LastCandidatePairs { get; private set; }
        public int LastHitPairs { get; private set; }
        /// <summary>
        /// 默认 false = 第五轮的顶点侵入近似（保持既有回归语义）；
        /// true = 使用 Sutherland-Hodgman 面裁剪完整接触流形。
        /// </summary>
        public bool UseFaceClipping { get; set; } = false;
        /// <summary>
        /// 逐点持久 warm start：按 (A,B,稳定pointId) 缓存 λ_n，替代第五轮
        /// 「按 (A,B) 对取 max」的简化策略。默认 false = 零回归；
        /// 开启时自动启用面裁剪以获得稳定 Id。
        /// </summary>
        public bool UsePointWarmCache { get; set; } = false;
        /// <summary>
        /// 多面接触流形（(faceId,pointId) 稳定 Id + 几何缩减到 4 点）。
        /// 默认 false = 第八轮单面语义，零回归。
        /// </summary>
        public bool UseMultiFace { get; set; } = false;
        /// <summary>
        /// 第十六轮：用第十四轮的 3×3 耦合有效质量矩阵做接触流形选点缩减。
        /// 默认 **false** —— 关闭时 Build 与第十五轮逐字相同，逐位零回归。
        /// 开启后对每一对命中的 (pts, ids) 按逐点侵入深度 + K^{-1/2} 白化的
        /// 18D 耦合 Jacobian 行做严格 MGS 缩减，最多保留 4 点。
        /// LastReducedPoints 是纯观测量，不进 Snapshot。
        /// </summary>
        public bool UseCoupledMassReduction { get; set; } = false;
        public int LastReducedPoints { get; private set; }
        public int PointWarmCacheCount => _warmPoints.Count;

        /// <summary>
        /// 耦合质量缩减的公开静态入口（供 Build 与回归门禁共用）。
        /// 逐点深度由调用方给出；返回按 (depth desc, id asc) + MGS 选出的点。
        /// </summary>
        public static (RVec3[] Points, int[] Ids) ReducePointsByCoupledMass(
            RVec3[] points, int[] ids, float[] depths, RVec3 normal,
            in YrContactMass mass, int max = 4)
        {
            var red = YrContactMassMatrix3x3.ReduceByCoupledMass(
                points, ids, depths, normal, mass, max);
            var p = new RVec3[red.Length];
            var i = new int[red.Length];
            for (int q = 0; q < red.Length; q++)
            { p[q] = red[q].Point; i[q] = red[q].Id; }
            return (p, i);
        }

        public void ClearWarmCache()
        {
            _warmNormal.Clear();
            _warmPoints.Clear();
            _lastPointIds = Array.Empty<int>();
        }

        /// <summary>包围球半径（OBB 半尺寸范数），用于 CCD 粗预筛。</summary>
        public static float BoundingRadius(in YrObb o) => o.HalfExtents.Length();

        /// <summary>候选对中最早的包围球 TOI；无则 -1。仅作观测，不改接触生成。</summary>
        public static float EarliestToi((int, int)[] pairs, YrObb[] obbs,
                                        RigidBody3D[] bodies, float dt)
        {
            float best = -1f;
            if (pairs == null) return best;
            for (int k = 0; k < pairs.Length; k++)
            {
                int i = pairs[k].Item1, j = pairs[k].Item2;
                if (i < 0 || j < 0 || i >= obbs.Length || j >= obbs.Length) continue;
                if (bodies == null || i >= bodies.Length || j >= bodies.Length) continue;
                float toi = ContinuousCollision.SweptSphereSphere(
                    obbs[i].Center, bodies[i].LinearVelocity, BoundingRadius(obbs[i]),
                    obbs[j].Center, bodies[j].LinearVelocity, BoundingRadius(obbs[j]), dt);
                if (toi >= 0f && (best < 0f || toi < best)) best = toi;
            }
            return best;
        }

        public YrContact[] Build((int, int)[] pairs, YrObb[] obbs,
                                 RigidBody3D[] bodies, float dt, YrSurface surface)
        {
            if (pairs == null) throw new ArgumentNullException(nameof(pairs));
            if (obbs == null || bodies == null) throw new ArgumentNullException(nameof(obbs));
            LastCandidatePairs = pairs.Length;
            LastReducedPoints = 0;
            LastEarliestToi = EarliestToi(pairs, obbs, bodies, dt);

            var hits = new System.Collections.Generic.List<(int, int, float, YrSatHit, RVec3[], int[])>();
            for (int k = 0; k < pairs.Length; k++)
            {
                int i = pairs[k].Item1, j = pairs[k].Item2;
                if (i < 0 || j < 0 || i >= obbs.Length || j >= obbs.Length) continue;
                YrSatHit hit = YrConvex.SatObbObb(obbs[i], obbs[j]);
                if (!hit.Hit) continue;
                float toi = ContinuousCollision.SweptSphereSphere(
                    obbs[i].Center, bodies[i].LinearVelocity, BoundingRadius(obbs[i]),
                    obbs[j].Center, bodies[j].LinearVelocity, BoundingRadius(obbs[j]), dt);
                RVec3[] pts;
                int[] ids;
                if (UseMultiFace)
                {
                    var tagged = YrFaceClipping.BuildMultiFaceReduced(obbs[i], obbs[j], hit);
                    pts = new RVec3[tagged.Length];
                    ids = new int[tagged.Length];
                    for (int q = 0; q < tagged.Length; q++)
                    {
                        pts[q] = tagged[q].Point;
                        ids[q] = tagged[q].Id;
                    }
                }
                else if (UsePointWarmCache || UseFaceClipping)
                {
                    var tagged = YrFaceClipping.BuildTagged(obbs[i], obbs[j], hit);
                    pts = new RVec3[tagged.Length];
                    ids = new int[tagged.Length];
                    for (int q = 0; q < tagged.Length; q++)
                    {
                        pts[q] = tagged[q].Point;
                        ids[q] = tagged[q].Id;
                    }
                }
                else
                {
                    pts = YrContactManifold.Build(obbs[i], obbs[j], hit);
                    ids = new int[pts.Length];   // 全 0，兼容原路径
                }
                if (UseCoupledMassReduction && pts.Length > 0)
                {
                    // 逐点侵入深度与 YrContactManifold.Build 同口径：
                    // d = ra - (p - centerA)·n，ra 为 A 在 n 上的投影半径。
                    RVec3 nn = hit.Normal.Normalized();
                    float ra = YrContactManifold.ProjectedRadius(obbs[i], nn);
                    var dep = new float[pts.Length];
                    for (int q = 0; q < pts.Length; q++)
                        dep[q] = ra - (pts[q] - obbs[i].Center).Dot(nn);
                    var mass = new YrContactMass(
                        bodies[i].InvMass, bodies[j].InvMass,
                        bodies[i].InvInertiaLocal, bodies[j].InvInertiaLocal,
                        bodies[i].Orientation, bodies[j].Orientation,
                        bodies[i].Position, bodies[j].Position,
                        surface.FrictionDynamic);
                    var reduced = ReducePointsByCoupledMass(
                        pts, ids, dep, hit.Normal, mass, YrContactManifold.MaxPoints);
                    pts = reduced.Points;
                    ids = reduced.Ids;
                    LastReducedPoints = pts.Length;
                }
                hits.Add((i, j, toi, hit, pts, ids));
            }
            LastHitPairs = hits.Count;

            // TOI 升序 → 同值按 (A,B) 升序，确定性地排接触求解顺序
            hits.Sort((x, y) => {
                float a = x.Item3 < 0f ? float.MaxValue : x.Item3;
                float b = y.Item3 < 0f ? float.MaxValue : y.Item3;
                if (a < b - 1e-9f) return -1;
                if (a > b + 1e-9f) return 1;
                if (x.Item1 != y.Item1) return x.Item1.CompareTo(y.Item1);
                return x.Item2.CompareTo(y.Item2);
            });

            int total = 0;
            for (int k = 0; k < hits.Count; k++) total += hits[k].Item5.Length;
            var contacts = new YrContact[total];
            _lastPointIds = new int[total];
            int w = 0;
            for (int k = 0; k < hits.Count; k++)
            {
                var h = hits[k];
                var key = (h.Item1, h.Item2);
                float cached = 0f;
                if (_warmNormal.TryGetValue(key, out float v)) cached = v;
                for (int p = 0; p < h.Item5.Length; p++)
                {
                    float lambda = p == 0 ? cached : 0f;
                    int id = h.Item6[p];
                    if (UsePointWarmCache)
                    {
                        float perPoint;
                        if (_warmPoints.TryGetValue((h.Item1, h.Item2, id), out perPoint))
                            lambda = perPoint;
                    }
                    contacts[w] = new YrContact {
                        BodyA = h.Item1, BodyB = h.Item2,
                        Point = h.Item5[p], Normal = h.Item4.Normal,
                        Depth = h.Item4.Depth,
                        Restitution = surface.Restitution,
                        FrictionStatic = surface.FrictionStatic,
                        FrictionDynamic = surface.FrictionDynamic,
                        NormalLambda = lambda
                    };
                    _lastPointIds[w] = id;
                    w++;
                }
            }
            return contacts;
        }

        /// <summary>按 (A,B) 字典序导出缓存，供快照保存（避免 Dictionary 遍历顺序不定）。</summary>
        public (int, int, float)[] ExportWarm()
        {
            var keys = new System.Collections.Generic.List<(int, int)>(_warmNormal.Keys);
            keys.Sort((x, y) => x.Item1 != y.Item1 ? x.Item1.CompareTo(y.Item1)
                                                   : x.Item2.CompareTo(y.Item2));
            var result = new (int, int, float)[keys.Count];
            for (int i = 0; i < keys.Count; i++)
                result[i] = (keys[i].Item1, keys[i].Item2, _warmNormal[keys[i]]);
            return result;
        }

        /// <summary>从快照恢复缓存；Restore 必须调用它，否则回滚重放会分叉。</summary>
        public void ImportWarm((int, int, float)[] entries)
        {
            _warmNormal.Clear();
            if (entries == null) return;
            for (int i = 0; i < entries.Length; i++)
                _warmNormal[(entries[i].Item1, entries[i].Item2)] = entries[i].Item3;
        }

        /// <summary>按 (A,B,Id) 字典序导出逐点缓存，供位级快照保存。</summary>
        public (int, int, int, float)[] ExportWarmPoints()
        {
            var keys = new System.Collections.Generic.List<(int, int, int)>(_warmPoints.Keys);
            keys.Sort((x, y) => {
                if (x.Item1 != y.Item1) return x.Item1.CompareTo(y.Item1);
                if (x.Item2 != y.Item2) return x.Item2.CompareTo(y.Item2);
                return x.Item3.CompareTo(y.Item3);
            });
            var result = new (int, int, int, float)[keys.Count];
            for (int i = 0; i < keys.Count; i++)
                result[i] = (keys[i].Item1, keys[i].Item2, keys[i].Item3,
                             _warmPoints[keys[i]]);
            return result;
        }

        /// <summary>从快照恢复逐点缓存（Restore 必须调用，否则回滚重放会分叉）。</summary>
        public void ImportWarmPoints((int, int, int, float)[] entries)
        {
            _warmPoints.Clear();
            if (entries == null) return;
            for (int i = 0; i < entries.Length; i++)
                _warmPoints[(entries[i].Item1, entries[i].Item2, entries[i].Item3)]
                    = entries[i].Item4;
        }

        /// <summary>把本帧求解后的 λ_n 缓存，供下一帧 warm start。</summary>
        public void CacheWarm(YrContact[] contacts)
        {
            if (contacts == null) return;
            for (int k = 0; k < contacts.Length; k++)
            {
                var key = (contacts[k].BodyA, contacts[k].BodyB);
                float cur;
                if (!_warmNormal.TryGetValue(key, out cur)) cur = 0f;
                _warmNormal[key] = MathF.Max(cur, contacts[k].NormalLambda);
                if (UsePointWarmCache && k < _lastPointIds.Length)
                {
                    _warmPoints[(contacts[k].BodyA, contacts[k].BodyB,
                                 _lastPointIds[k])] = contacts[k].NormalLambda;
                }
            }
        }
    }
}
