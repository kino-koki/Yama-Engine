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

        public float LastEarliestToi { get; private set; } = -1f;
        public int LastCandidatePairs { get; private set; }
        public int LastHitPairs { get; private set; }

        public void ClearWarmCache() => _warmNormal.Clear();

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
            LastEarliestToi = EarliestToi(pairs, obbs, bodies, dt);

            var hits = new System.Collections.Generic.List<(int, int, float, YrSatHit, RVec3[])>();
            for (int k = 0; k < pairs.Length; k++)
            {
                int i = pairs[k].Item1, j = pairs[k].Item2;
                if (i < 0 || j < 0 || i >= obbs.Length || j >= obbs.Length) continue;
                YrSatHit hit = YrConvex.SatObbObb(obbs[i], obbs[j]);
                if (!hit.Hit) continue;
                float toi = ContinuousCollision.SweptSphereSphere(
                    obbs[i].Center, bodies[i].LinearVelocity, BoundingRadius(obbs[i]),
                    obbs[j].Center, bodies[j].LinearVelocity, BoundingRadius(obbs[j]), dt);
                RVec3[] pts = YrContactManifold.Build(obbs[i], obbs[j], hit);
                hits.Add((i, j, toi, hit, pts));
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
            int w = 0;
            for (int k = 0; k < hits.Count; k++)
            {
                var h = hits[k];
                var key = (h.Item1, h.Item2);
                float cached = 0f;
                if (_warmNormal.TryGetValue(key, out float v)) cached = v;
                for (int p = 0; p < h.Item5.Length; p++)
                {
                    contacts[w++] = new YrContact {
                        BodyA = h.Item1, BodyB = h.Item2,
                        Point = h.Item5[p], Normal = h.Item4.Normal,
                        Depth = h.Item4.Depth,
                        Restitution = surface.Restitution,
                        FrictionStatic = surface.FrictionStatic,
                        FrictionDynamic = surface.FrictionDynamic,
                        NormalLambda = p == 0 ? cached : 0f
                    };
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

        /// <summary>把本帧求解后的 λ_n 按 (A,B) 缓存，供下一帧 warm start。</summary>
        public void CacheWarm(YrContact[] contacts)
        {
            if (contacts == null) return;
            for (int k = 0; k < contacts.Length; k++)
            {
                var key = (contacts[k].BodyA, contacts[k].BodyB);
                float cur;
                if (!_warmNormal.TryGetValue(key, out cur)) cur = 0f;
                _warmNormal[key] = MathF.Max(cur, contacts[k].NormalLambda);
            }
        }
    }
}
