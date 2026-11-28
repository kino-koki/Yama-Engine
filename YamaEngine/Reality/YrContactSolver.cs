using System;
namespace YamaEngine.Reality
{
    /// <summary>一个接触点。Normal 从 A 指向 B；*Lambda 为 warm starting 累积冲量。</summary>
    public struct YrContact
    {
        public int BodyA;
        public int BodyB;
        public RVec3 Point;
        public RVec3 Normal;
        public float Depth;
        public float Restitution;
        public float FrictionStatic;
        public float FrictionDynamic;
        public float NormalLambda;
        public float TangentLambda1;
        public float TangentLambda2;
        public float InitialNormalVelocity;
        public float TargetNormalVelocity;
    }

    /// <summary>
    /// 序列冲量（Sequential Impulse）接触求解器：把前三轮分散的
    /// YrConvex(SAT 法向/深度) + ContactFriction(摩擦锥) + ContinuousCollision(TOI)
    /// 接成一条完整窄相链，并加上 warm starting 与恢复系数目标速度。
    ///
    /// 关键点：RigidBody3D 以世界系角动量 L 为状态量，所以角冲量就是
    /// `L += r × j`，无需再乘 I⁻¹ —— 结构上不会把内力矩算错。
    /// 位置修正（穿透恢复）与速度求解分离，避免 Baumgarte 注入虚假动能。
    /// </summary>
    /// <summary>
    /// 第十五轮耦合块求解的可观测轨迹（纯观测量，**不进快照**）。
    /// 用于把「3×3 K 的非对角耦合是否真的参与了逐接触求解」变成可判定事实。
    /// </summary>
    public readonly struct YrCoupledSolveTrace
    {
        public readonly int TotalSteps;
        public readonly int CoupledSteps;
        public readonly int FallbackSteps;
        public readonly float MaxOffDiagonal;
        public readonly YrMat3Sym LastK;
        public readonly bool LastUsedCoupling;
        public YrCoupledSolveTrace(int total, int coupled, int fallback,
                                   float maxOff, YrMat3Sym lastK, bool lastUsed)
        { TotalSteps = total; CoupledSteps = coupled; FallbackSteps = fallback;
          MaxOffDiagonal = maxOff; LastK = lastK; LastUsedCoupling = lastUsed; }
    }

    /// <summary>
    /// 第十七轮全局块求解的可观测轨迹（纯观测量，**不进快照**）。
    /// 把「跨接触点耦合是否真的参与了求解」变成可判定事实。
    /// </summary>
    public readonly struct YrGlobalBlockTrace
    {
        public readonly int Groups;
        public readonly int MaxPointsPerGroup;
        public readonly int TotalIterations;
        public readonly int SolvedGroups;
        public readonly int FallbackGroups;
        public readonly float MaxCrossBlock;
        public readonly bool LastUsedGlobal;
        public YrGlobalBlockTrace(int groups, int maxPts, int iters,
                                  int solved, int fallback, float cross, bool lastUsed)
        { Groups = groups; MaxPointsPerGroup = maxPts; TotalIterations = iters;
          SolvedGroups = solved; FallbackGroups = fallback;
          MaxCrossBlock = cross; LastUsedGlobal = lastUsed; }
    }

    public sealed class YrContactSolver
    {
        private RigidBody3D[] _bodies = Array.Empty<RigidBody3D>();
        private YrContact[] _contacts = Array.Empty<YrContact>();

        /// <summary>
        /// 启用跨接触点 3m×3m 全局块求解（默认 **false**）。
        /// 关闭时 Solve 与第十六轮逐字相同 → 222 项断言零回归。
        /// 优先级高于 UseCoupledMassMatrix（开启时后者被跳过，见文档）。
        /// </summary>
        public bool UseGlobalBlockSolve { get; set; } = false;

        /// <summary>Cholesky 主元阻尼；设 0 可让奇异上下文强制走安全退回路径。</summary>
        public float GlobalBlockDamping { get; set; } = YrContactBlockMatrix.DefaultDamping;

        private int _gGroups, _gMaxPts, _gIters, _gSolved, _gFallback;
        private float _gMaxCross;
        private bool _gLastUsed;

        public YrGlobalBlockTrace LastGlobalBlockTrace => new YrGlobalBlockTrace(
            _gGroups, _gMaxPts, _gIters, _gSolved, _gFallback, _gMaxCross, _gLastUsed);

        public void ResetGlobalBlockTrace()
        { _gGroups = 0; _gMaxPts = 0; _gIters = 0; _gSolved = 0;
          _gFallback = 0; _gMaxCross = 0f; _gLastUsed = false; }

        /// <summary>
        /// 启用完整 3×3 接触有效质量矩阵的块求解（默认 **false**）。
        /// 关闭时 Solve 走与第十四轮逐字相同的标量路径 → 190 项断言零回归；
        /// 开启时每次接触一次性解出 (n,t1,t2) 三个方向的耦合冲量。
        /// </summary>
        public bool UseCoupledMassMatrix { get; set; } = false;

        private int _totalSteps, _coupledSteps, _fallbackSteps;
        private float _maxOff;
        private YrMat3Sym _lastK;
        private bool _lastUsed;

        public YrCoupledSolveTrace LastCoupledTrace => new YrCoupledSolveTrace(
            _totalSteps, _coupledSteps, _fallbackSteps, _maxOff, _lastK, _lastUsed);

        public void ResetCoupledTrace()
        { _totalSteps = 0; _coupledSteps = 0; _fallbackSteps = 0;
          _maxOff = 0f; _lastK = default; _lastUsed = false; }

        public void SetBodies(RigidBody3D[] bodies)
            => _bodies = bodies ?? Array.Empty<RigidBody3D>();
        public void SetContacts(YrContact[] contacts)
            => _contacts = contacts ?? Array.Empty<YrContact>();

        public YrContact[] Contacts => _contacts;
        public RigidBody3D[] Bodies => _bodies;

        private bool Valid(in YrContact c)
            => c.BodyA >= 0 && c.BodyB >= 0
            && c.BodyA < _bodies.Length && c.BodyB < _bodies.Length
            && _bodies[c.BodyA] != null && _bodies[c.BodyB] != null;

        public static (RVec3 T1, RVec3 T2) Basis(RVec3 normal)
        {
            RVec3 n = normal.Normalized();
            RVec3 seed = MathF.Abs(n.Y) < 0.9f
                ? new RVec3(0f, 1f, 0f) : new RVec3(1f, 0f, 0f);
            RVec3 t1 = seed.Cross(n).Normalized();
            RVec3 t2 = n.Cross(t1).Normalized();
            return (t1, t2);
        }

        public static (RVec3 T1, RVec3 T2) BasisFromAxis(RVec3 axis)
        {
            RVec3 a = axis.Normalized();
            RVec3 seed = MathF.Abs(a.Y) < 0.9f
                ? new RVec3(0f, 1f, 0f) : new RVec3(1f, 0f, 0f);
            RVec3 t1 = seed.Cross(a).Normalized();
            RVec3 t2 = a.Cross(t1).Normalized();
            return (t1, t2);
        }

        /// <summary>接触点相对速度（含两体角速度贡献），公开供回归门禁精确观测。</summary>
        public RVec3 RelativeVelocityAt(in YrContact c)
        {
            RigidBody3D a = _bodies[c.BodyA], b = _bodies[c.BodyB];
            RVec3 ra = c.Point - a.Position, rb = c.Point - b.Position;
            RVec3 va = a.LinearVelocity + a.AngularVelocityWorld().Cross(ra);
            RVec3 vb = b.LinearVelocity + b.AngularVelocityWorld().Cross(rb);
            return vb - va;
        }

        /// <summary>沿 dir 的广义有效质量：invM_A + invM_B + 两体转动贡献。</summary>
        public float EffectiveMass(in YrContact c, RVec3 dir)
        {
            RigidBody3D a = _bodies[c.BodyA], b = _bodies[c.BodyB];
            RVec3 ra = c.Point - a.Position, rb = c.Point - b.Position;
            float angular = a.WorldInvInertiaApply(ra.Cross(dir)).Cross(ra).Dot(dir)
                          + b.WorldInvInertiaApply(rb.Cross(dir)).Cross(rb).Dot(dir);
            return a.InvMass + b.InvMass + angular;
        }

        /// <summary>jOnB 为作用于 B 的冲量；A 受 -jOnB。同一点作用力与反作用力，合力矩为零。</summary>
        public void ApplyImpulsePair(in YrContact c, RVec3 jOnB)
        {
            RigidBody3D a = _bodies[c.BodyA], b = _bodies[c.BodyB];
            a.ApplyLinearImpulse(-jOnB);
            b.ApplyLinearImpulse(jOnB);
            RVec3 ra = c.Point - a.Position, rb = c.Point - b.Position;
            a.ApplyAngularImpulse(-(ra.Cross(jOnB)));
            b.ApplyAngularImpulse(rb.Cross(jOnB));
        }

        /// <summary>
        /// 记录恢复系数目标速度并施加 warm start 冲量。
        /// 目标速度只由初始法向速度决定（e 只作用一次），后续迭代目标为 0，
        /// 避免逐迭代重复注入弹性能量。
        /// </summary>
        public void Prepare()
        {
            for (int k = 0; k < _contacts.Length; k++)
            {
                ref var c = ref _contacts[k];
                if (!Valid(c)) continue;
                RVec3 n = c.Normal.Normalized();
                var basis = Basis(n);
                c.InitialNormalVelocity = RelativeVelocityAt(c).Dot(n);
                c.TargetNormalVelocity = c.InitialNormalVelocity < -1.0f
                    ? -c.Restitution * c.InitialNormalVelocity : 0f;
                RVec3 warm = n * c.NormalLambda
                           + basis.T1 * c.TangentLambda1
                           + basis.T2 * c.TangentLambda2;
                ApplyImpulsePair(c, warm);
            }
        }

        public void Solve(int iterations)
        {
            // 默认关闭时下面整个分支都不执行，循环体与第十六轮逐字相同 → 逐位零回归。
            if (UseGlobalBlockSolve) { SolveGlobalBlocks(iterations); return; }
            for (int it = 0; it < iterations; it++)
            {
                for (int k = 0; k < _contacts.Length; k++)
                {
                    ref var c = ref _contacts[k];
                    if (!Valid(c)) continue;
                    // 默认路径逐字保持原状：任何开关关闭时都是逐位零回归。
                    if (UseCoupledMassMatrix) SolveCoupled(ref c);
                    else { SolveNormal(ref c); SolveFriction(ref c); }
                }
            }
        }

        /// <summary>
        /// 按 (min(A,B), max(A,B), 原序) 定序分组；不同对无共享刚体，
        /// 全局 K 的跨对块为零，分组独立求解与整体求解等价。
        /// </summary>
        private void SolveGlobalBlocks(int iterations)
        {
            ResetGlobalBlockTrace();
            var valid = new System.Collections.Generic.List<int>();
            for (int k = 0; k < _contacts.Length; k++)
                if (Valid(_contacts[k])) valid.Add(k);
            valid.Sort((x, y) => {
                int ax = Math.Min(_contacts[x].BodyA, _contacts[x].BodyB);
                int bx = Math.Max(_contacts[x].BodyA, _contacts[x].BodyB);
                int ay = Math.Min(_contacts[y].BodyA, _contacts[y].BodyB);
                int by = Math.Max(_contacts[y].BodyA, _contacts[y].BodyB);
                if (ax != ay) return ax.CompareTo(ay);
                if (bx != by) return bx.CompareTo(by);
                return x.CompareTo(y);
            });

            int p = 0;
            while (p < valid.Count)
            {
                int ka = Math.Min(_contacts[valid[p]].BodyA, _contacts[valid[p]].BodyB);
                int kb = Math.Max(_contacts[valid[p]].BodyA, _contacts[valid[p]].BodyB);
                int q = p + 1;
                while (q < valid.Count)
                {
                    int qa = Math.Min(_contacts[valid[q]].BodyA, _contacts[valid[q]].BodyB);
                    int qb = Math.Max(_contacts[valid[q]].BodyA, _contacts[valid[q]].BodyB);
                    if (qa != ka || qb != kb) break;
                    q++;
                }
                var group = new int[q - p];
                for (int t = 0; t < q - p; t++) group[t] = valid[p + t];
                SolveOneGroup(group, iterations);
                p = q;
            }
        }

        /// <summary>
        /// 单组全局块求解：一次组装 3m×3m K + 一次 Cholesky，之后每次迭代
        /// 基于**同一时刻**速度算全部 rhs，统一解出 Δλ 并统一施加冲量
        /// （投影 Jacobi，与 sequential impulse 的 Gauss-Seidel 语义不同）。
        /// λ_n ≥ 0 累加钳制与二维库仑圆锥投影逐点施加，成对合成冲量守恒。
        /// </summary>
        private void SolveOneGroup(int[] idx, int iterations)
        {
            int m = idx.Length;
            int n = 3 * m;
            _gGroups++;
            if (m > _gMaxPts) _gMaxPts = m;

            float[] k = YrContactBlockMatrix.BuildGlobalK(
                _contacts, idx, m, _bodies,
                out RVec3[] ns, out RVec3[] t1s, out RVec3[] t2s);
            float cross = YrContactBlockMatrix.MaxCrossBlockNorm(k, m);
            if (cross > _gMaxCross) _gMaxCross = cross;

            bool ok = YrContactBlockMatrix.Cholesky(
                k, n, GlobalBlockDamping, out float[] lower);
            if (!ok)
            {
                // 奇异上下文：退回原 SI 路径（与默认关闭逐字一致），绝不制造 NaN。
                _gFallback++;
                _gLastUsed = false;
                for (int it = 0; it < iterations; it++)
                {
                    for (int q = 0; q < m; q++)
                    {
                        ref var c = ref _contacts[idx[q]];
                        if (UseCoupledMassMatrix) SolveCoupled(ref c);
                        else { SolveNormal(ref c); SolveFriction(ref c); }
                    }
                }
                return;
            }
            _gSolved++;
            _gLastUsed = true;

            var rhs = new float[n];
            var mus = new float[m];
            var applied = new RVec3[m];
            for (int it = 0; it < iterations; it++)
            {
                _gIters++;
                // 先观测全部点的速度（此时尚未施加本轮任何冲量）
                for (int q = 0; q < m; q++)
                {
                    ref var c = ref _contacts[idx[q]];
                    RVec3 v = RelativeVelocityAt(c);
                    float v1 = v.Dot(t1s[q]), v2 = v.Dot(t2s[q]);
                    float slip = MathF.Sqrt(v1 * v1 + v2 * v2);
                    mus[q] = slip > 1e-4f ? c.FrictionDynamic : c.FrictionStatic;
                    rhs[3 * q + 0] = c.TargetNormalVelocity - v.Dot(ns[q]);
                    rhs[3 * q + 1] = -v1;
                    rhs[3 * q + 2] = -v2;
                }

                float[] dl = YrContactBlockMatrix.Solve(lower, n, rhs);
                // 先算好全部新 λ 与合成冲量，再统一施加 → Jacobi 语义
                for (int q = 0; q < m; q++)
                {
                    ref var c = ref _contacts[idx[q]];
                    float oldN = c.NormalLambda;
                    float old1 = c.TangentLambda1;
                    float old2 = c.TangentLambda2;
                    float nN = MathF.Max(0f, oldN + dl[3 * q + 0]);
                    float n1 = old1 + dl[3 * q + 1];
                    float n2 = old2 + dl[3 * q + 2];
                    float limit = MathF.Max(0f, mus[q] * nN);
                    float len = MathF.Sqrt(n1 * n1 + n2 * n2);
                    if (len > limit && len > 1e-12f)
                    {
                        float sc = limit / len;
                        n1 *= sc; n2 *= sc;
                    }
                    c.NormalLambda = nN;
                    c.TangentLambda1 = n1;
                    c.TangentLambda2 = n2;
                    applied[q] = ns[q] * (nN - oldN)
                               + t1s[q] * (n1 - old1)
                               + t2s[q] * (n2 - old2);
                }
                for (int q = 0; q < m; q++)
                    ApplyImpulsePair(_contacts[idx[q]], applied[q]);
            }
        }


        /// <summary>
        /// 第十五轮：把第十四轮的 3×3 K 接进逐接触求解。
        ///
        /// 在接触正交基 (n,t1,t2) 下，速度误差 rhs = (targetNV - vn, -v_t1, -v_t2)，
        /// 冲量增量 Δλ = K⁻¹·rhs —— **一次性解出三个方向**，保留 K 的非对角耦合
        /// （标量版等价于把 K 的非对角强行置零后分三次独立求解）。
        ///
        /// 之后仍按 sequential impulse 语义做 λ_n ≥ 0 累加钳制与二维库仑圆锥投影，
        /// 再以合成冲量 n·Δλ_n + t1·Δλ_t1 + t2·Δλ_t2 成对施加。
        ///
        /// 两个安全退化（保证可判定 parity、绝不制造 NaN）：
        /// ① K 严格对角（|非对角| ≤ 1e-6）→ 直接走原 SolveNormal/SolveFriction，
        ///    与关闭开关时**逐位一致**；
        /// ② K 非正定（如两体 invMass/invInertia 全零）→ TrySolve 返回 false，
        ///    同样退回原路径。
        ///
        /// 诚实边界：块求解只覆盖单个接触点内部的 3 方向耦合，未做跨接触点的
        /// 全局 LCP/SOCP；摩擦锥仍是二维圆锥投影；K 由当前帧姿态构造，迭代中
        /// 不随姿态更新。
        /// </summary>
        private void SolveCoupled(ref YrContact c)
        {
            RigidBody3D a = _bodies[c.BodyA], b = _bodies[c.BodyB];
            RVec3 n = c.Normal.Normalized();
            var basis = Basis(n);
            RVec3 t1 = basis.T1, t2 = basis.T2;

            RVec3 vrel = RelativeVelocityAt(c);
            float slip = MathF.Sqrt(
                vrel.Dot(t1) * vrel.Dot(t1) + vrel.Dot(t2) * vrel.Dot(t2));
            float mu = slip > 1e-4f ? c.FrictionDynamic : c.FrictionStatic;

            var mass = new YrContactMass(
                a.InvMass, b.InvMass,
                a.InvInertiaLocal, b.InvInertiaLocal,
                a.Orientation, b.Orientation,
                a.Position, b.Position, mu);
            YrMat3Sym k = YrContactMassMatrix3x3.BuildK(
                mass, c.Point, n, t1, t2);

            float off = MathF.Max(MathF.Abs(k.M01),
                        MathF.Max(MathF.Abs(k.M02), MathF.Abs(k.M12)));
            _totalSteps++;
            if (off > _maxOff) _maxOff = off;
            _lastK = k;

            // 不能用 && 短路接 out 参数：C# 的确定性赋值分析看不到
            // 「coupled==true ⇒ dlambda 已赋值」这个逻辑关联，会把
            // dlambda.X/Y/Z 判为 possibly unassigned field（云端 -warnaserror 红灯）。
            RVec3 rhs = new RVec3(c.TargetNormalVelocity - vrel.Dot(n),
                                  -vrel.Dot(t1), -vrel.Dot(t2));
            RVec3 dlambda = RVec3.Zero;
            bool coupled = false;
            if (off > 1e-6f)
                coupled = YrContactMassMatrix3x3.TrySolve(k, rhs, out dlambda);
            _lastUsed = coupled;

            if (!coupled)
            {
                _fallbackSteps++;
                SolveNormal(ref c);
                SolveFriction(ref c);
                return;
            }
            _coupledSteps++;

            float oldN = c.NormalLambda, old1 = c.TangentLambda1, old2 = c.TangentLambda2;
            float nN = MathF.Max(0f, oldN + dlambda.X);
            float n1 = old1 + dlambda.Y, n2 = old2 + dlambda.Z;

            // 与标量版同一套库仑圆锥投影（不是盒式近似）
            float limit = MathF.Max(0f, mu * nN);
            float len = MathF.Sqrt(n1 * n1 + n2 * n2);
            if (len > limit && len > 1e-12f)
            {
                float s = limit / len;
                n1 *= s; n2 *= s;
            }
            c.NormalLambda = nN;
            c.TangentLambda1 = n1;
            c.TangentLambda2 = n2;
            RVec3 applied = n * (nN - oldN)
                          + t1 * (n1 - old1)
                          + t2 * (n2 - old2);
            ApplyImpulsePair(c, applied);
        }

        private void SolveNormal(ref YrContact c)
        {
            RVec3 n = c.Normal.Normalized();
            float vn = RelativeVelocityAt(c).Dot(n);
            float eff = EffectiveMass(c, n);
            if (eff <= 1e-12f) return;
            float delta = (c.TargetNormalVelocity - vn) / eff;
            float old = c.NormalLambda;
            float next = MathF.Max(0f, old + delta);
            c.NormalLambda = next;
            ApplyImpulsePair(c, n * (next - old));
        }

        private void SolveFriction(ref YrContact c)
        {
            RVec3 n = c.Normal.Normalized();
            var basis = Basis(n);
            RVec3 vrel = RelativeVelocityAt(c);
            float v1 = vrel.Dot(basis.T1), v2 = vrel.Dot(basis.T2);
            float slip = MathF.Sqrt(v1 * v1 + v2 * v2);
            float mu = slip > 1e-4f ? c.FrictionDynamic : c.FrictionStatic;
            float e1 = EffectiveMass(c, basis.T1), e2 = EffectiveMass(c, basis.T2);
            float d1 = e1 > 1e-12f ? -v1 / e1 : 0f;
            float d2 = e2 > 1e-12f ? -v2 / e2 : 0f;
            float l1 = c.TangentLambda1 + d1;
            float l2 = c.TangentLambda2 + d2;
            // 二维库仑圆锥投影（不是盒式近似）：切向合冲量模长 <= mu * |lambda_n|
            float limit = MathF.Max(0f, mu * c.NormalLambda);
            float len = MathF.Sqrt(l1 * l1 + l2 * l2);
            if (len > limit && len > 1e-12f)
            {
                float s = limit / len;
                l1 *= s; l2 *= s;
            }
            RVec3 applied = basis.T1 * (l1 - c.TangentLambda1)
                          + basis.T2 * (l2 - c.TangentLambda2);
            c.TangentLambda1 = l1;
            c.TangentLambda2 = l2;
            ApplyImpulsePair(c, applied);
        }

        /// <summary>
        /// 纯位置投影消除穿透：不改线速度/角动量，因此不注入动能。
        /// 与速度级冲量求解分离，是「恢复系数不被污染」的关键。
        /// </summary>
        public void CorrectPositions(float beta, float slop)
        {
            for (int k = 0; k < _contacts.Length; k++)
            {
                ref var c = ref _contacts[k];
                if (!Valid(c)) continue;
                RigidBody3D a = _bodies[c.BodyA], b = _bodies[c.BodyB];
                float w = a.InvMass + b.InvMass;
                if (w <= 0f) continue;
                float pen = MathF.Max(0f, c.Depth - slop);
                if (pen <= 0f) continue;
                RVec3 n = c.Normal.Normalized();
                float corr = beta * pen / w;
                a.Position = a.Position - n * (corr * a.InvMass);
                b.Position = b.Position + n * (corr * b.InvMass);
            }
        }

        /// <summary>
        /// 总角动量（关于世界原点）：自旋 L + 轨道 r×(mv)。
        /// 单独看 L_A+L_B 并不守恒——这是写守恒断言时最常见的错误。
        /// </summary>
        public static RVec3 TotalAngularMomentum(RigidBody3D[] bodies)
        {
            RVec3 total = RVec3.Zero;
            for (int i = 0; i < bodies.Length; i++)
            {
                RigidBody3D b = bodies[i];
                if (b == null) continue;
                float mass = b.Mass > 1e-9f ? b.Mass : 0f;
                total = total + b.AngularMomentum
                      + b.Position.Cross(b.LinearVelocity * mass);
            }
            return total;
        }

        public static RVec3 TotalLinearMomentum(RigidBody3D[] bodies)
        {
            RVec3 total = RVec3.Zero;
            for (int i = 0; i < bodies.Length; i++)
            {
                RigidBody3D b = bodies[i];
                if (b == null) continue;
                float mass = b.Mass > 1e-9f ? b.Mass : 0f;
                total = total + b.LinearVelocity * mass;
            }
            return total;
        }
    }
}
