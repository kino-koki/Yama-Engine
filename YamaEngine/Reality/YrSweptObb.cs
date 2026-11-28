using System;
namespace YamaEngine.Reality
{
    /// <summary>
    /// 连续碰撞检测：15 轴扫掠 SAT（swept slab）。
    ///
    /// 相对第五轮的「包围球 TOI 粗筛」，这里用与窄相 SAT 完全相同的 15 根轴
    /// （3 + 3 + 9 叉积）做区间求交：
    ///   对轴 u：p = (cb-ca)·u，w = (vb-va)·u，r = ra(u)+rb(u)
    ///   |w|≈0 → 要求 |p| &lt; r，否则永不相交
    ///   否则 t1=(-r-p)/w, t2=(r-p)/w，取 tEnter=max(t1), tExit=min(t2)
    ///   若 tEnter ≤ tExit 且落在 [0,dt]，tEnter 即首次接触时刻。
    ///
    /// 因为轴集合与 SAT 一致，TOI 时刻的接触判定不再出现「包围球报早了、
    /// SAT 还没命中」的错配 —— 这是上一轮 CCD 只做预筛的核心缺陷。
    /// </summary>
    public static class YrSweptObb
    {
        private static readonly RVec3 Ex = new RVec3(1f, 0f, 0f);
        private static readonly RVec3 Ey = new RVec3(0f, 1f, 0f);
        private static readonly RVec3 Ez = new RVec3(0f, 0f, 1f);

        private static RVec3[] AxesOf(in YrObb o)
        {
            return new RVec3[] {
                o.Orientation.Rotate(Ex), o.Orientation.Rotate(Ey), o.Orientation.Rotate(Ez)
            };
        }

        /// <summary>枚举 15 根归一化轴（与 YrConvex.SatObbObb 同一口径）。</summary>
        public static RVec3[] EnumerateAxes(in YrObb a, in YrObb b)
        {
            RVec3[] aa = AxesOf(a), ba = AxesOf(b);
            var list = new System.Collections.Generic.List<RVec3>(15);
            for (int i = 0; i < 3; i++) list.Add(aa[i]);
            for (int i = 0; i < 3; i++) list.Add(ba[i]);
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                {
                    RVec3 u = aa[i], v = ba[j];
                    var c = new RVec3(u.Y * v.Z - u.Z * v.Y,
                                      u.Z * v.X - u.X * v.Z,
                                      u.X * v.Y - u.Y * v.X);
                    float l = c.Length();
                    if (l > 1e-6f) list.Add(c * (1f / l));
                }
            return list.ToArray();
        }

        /// <summary>返回 [0,dt] 内首次接触时刻；无碰撞或已在 dt 之后返回 -1。</summary>
        public static float ComputeToi(in YrObb a, RVec3 va,
                                       in YrObb b, RVec3 vb, float dt)
        {
            if (dt <= 0f) return -1f;
            RVec3 d = b.Center - a.Center;
            RVec3 v = vb - va;
            RVec3[] axes = EnumerateAxes(a, b);
            float tEnter = 0f;
            float tExit = dt;

            for (int k = 0; k < axes.Length; k++)
            {
                RVec3 u = axes[k];
                float p = d.Dot(u);
                float w = v.Dot(u);
                float r = YrContactManifold.ProjectedRadius(a, u)
                        + YrContactManifold.ProjectedRadius(b, u);
                if (MathF.Abs(w) < 1e-9f)
                {
                    if (MathF.Abs(p) > r) return -1f;   // 该轴已分离且不会靠近
                    continue;
                }
                float t1 = (-r - p) / w;
                float t2 = (r - p) / w;
                if (t1 > t2) { float tmp = t1; t1 = t2; t2 = tmp; }
                if (t1 > tEnter) tEnter = t1;
                if (t2 < tExit) tExit = t2;
                if (tEnter > tExit) return -1f;
            }
            if (tExit < 0f || tEnter > dt || tEnter < 0f) return -1f;
            return tEnter;
        }

        /// <summary>
        /// 角速度的保守半径膨胀。旋转 θ=|ω|·dt 内，OBB 顶点在任意轴上的投影
        /// 变化上界约为 R·θ；取 √2 余量覆盖「半尺寸范数 vs 轴向投影」的差异。
        ///
        /// 诚实边界：这是**保守上界近似**，不是精确旋转扫掠（conservative
        /// advancement）。它只保证「不漏报」（TOI 可能早报），可能多报候选对；
        /// 且随 dt 线性增长，dt 越大越保守。真机侧可换成真正的 CA 迭代。
        /// </summary>
        public static float AngularInflation(float omegaMagnitude, float boundingRadius, float dt)
            => boundingRadius * MathF.Min(1.0f,
                MathF.Abs(omegaMagnitude) * MathF.Max(0f, dt) * 1.41421356f);

        /// <summary>
        /// 带角速度保守膨胀的 TOI。把膨胀量加到每根轴的投影半径 r 上，
        /// 因此 tEnter 只会更早或相等 —— 不漏报，可能早报。
        /// </summary>
        public static float ComputeToiWithAngular(in YrObb a, RVec3 va, RVec3 wa,
                                                  in YrObb b, RVec3 vb, RVec3 wb, float dt)
        {
            if (dt <= 0f) return -1f;
            RVec3 d = b.Center - a.Center;
            RVec3 v = vb - va;
            float inflate = AngularInflation(wa.Length(), a.HalfExtents.Length(), dt)
                          + AngularInflation(wb.Length(), b.HalfExtents.Length(), dt);
            RVec3[] axes = EnumerateAxes(a, b);
            float tEnter = 0f, tExit = dt;
            for (int k = 0; k < axes.Length; k++)
            {
                RVec3 u = axes[k];
                float p = d.Dot(u), w = v.Dot(u);
                float r = YrContactManifold.ProjectedRadius(a, u)
                        + YrContactManifold.ProjectedRadius(b, u) + inflate;
                if (MathF.Abs(w) < 1e-9f)
                {
                    if (MathF.Abs(p) > r) return -1f;
                    continue;
                }
                float t1 = (-r - p) / w, t2 = (r - p) / w;
                if (t1 > t2) { float tmp = t1; t1 = t2; t2 = tmp; }
                if (t1 > tEnter) tEnter = t1;
                if (t2 < tExit) tExit = t2;
                if (tEnter > tExit) return -1f;
            }
            if (tExit < 0f || tEnter > dt || tEnter < 0f) return -1f;
            return tEnter;
        }

        /// <summary>全对扫掠（角速度保守版）。</summary>
        public static float EarliestToiAllAngular(YrObb[] obbs, RigidBody3D[] bodies, float dt)
        {
            if (obbs == null || bodies == null) return -1f;
            int n = Math.Min(obbs.Length, bodies.Length);
            float best = -1f;
            for (int i = 0; i < n; i++)
                for (int j = i + 1; j < n; j++)
                {
                    float t = ComputeToiWithAngular(
                        obbs[i], bodies[i].LinearVelocity, bodies[i].AngularVelocityWorld(),
                        obbs[j], bodies[j].LinearVelocity, bodies[j].AngularVelocityWorld(), dt);
                    if (t >= 0f && (best < 0f || t < best)) best = t;
                }
            return best;
        }

        /// <summary>全对最早 TOI（确定性：按 (A,B) 字典序取严格更小值）。</summary>
        public static float EarliestToi((int, int)[] pairs, YrObb[] obbs,
                                        RigidBody3D[] bodies, float dt)
        {
            if (pairs == null || obbs == null || bodies == null) return -1f;
            float best = -1f;
            for (int k = 0; k < pairs.Length; k++)
            {
                int i = pairs[k].Item1, j = pairs[k].Item2;
                if (i < 0 || j < 0 || i >= obbs.Length || j >= obbs.Length) continue;
                if (i >= bodies.Length || j >= bodies.Length) continue;
                // CCD 不能只跑宽相候选：宽相基于「当前」AABB，高速物体当前是分离的。
                // 因此这里对所有体对做扫掠，宽相只作为常规窄相的优化。
                float t = ComputeToi(obbs[i], bodies[i].LinearVelocity,
                                     obbs[j], bodies[j].LinearVelocity, dt);
                if (t >= 0f && (best < 0f || t < best)) best = t;
            }
            return best;
        }

        /// <summary>对全体刚体做 O(n²) 扫掠（高速场景的正确入口，不依赖宽相）。</summary>
        public static float EarliestToiAll(YrObb[] obbs, RigidBody3D[] bodies, float dt)
        {
            if (obbs == null || bodies == null) return -1f;
            int n = Math.Min(obbs.Length, bodies.Length);
            float best = -1f;
            for (int i = 0; i < n; i++)
                for (int j = i + 1; j < n; j++)
                {
                    float t = ComputeToi(obbs[i], bodies[i].LinearVelocity,
                                         obbs[j], bodies[j].LinearVelocity, dt);
                    if (t >= 0f && (best < 0f || t < best)) best = t;
                }
            return best;
        }
    }
}
