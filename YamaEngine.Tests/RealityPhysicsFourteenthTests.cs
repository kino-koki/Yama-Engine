using System;
using YamaEngine.Reality;

namespace YamaEngine.Tests
{
    /// <summary>
    /// 第十四轮物理回归（14 项）：完整 3×3 接触有效质量矩阵的非对角耦合。
    /// 进入现有 build.yml 的 Test step 真实执行。
    /// </summary>
    public static class RealityPhysicsFourteenthTests
    {
        private static void Require(bool ok, string msg)
        {
            if (!ok) throw new InvalidOperationException("REALITY_R14_FAIL: " + msg);
        }
        private static RVec3 N => new RVec3(0f, 0f, 1f);
        private static YrContactMass Mass(float invI, float mu = 0.6f)
            => YrContactMass.Uniform(1f, new RVec3(invI, invI, invI),
                                     RVec3.Zero, RVec3.Zero, mu);
        private static YrContactMass Aniso()
        {
            RVec3 I = RigidBody3D.BoxInertia(1f, 0.4f, 2.0f, 1.2f);
            var inv = new RVec3(1f / I.X, 1f / I.Y, 1f / I.Z);
            return YrContactMass.Uniform(1f, inv, RVec3.Zero, RVec3.Zero, 0.6f);
        }
        private static float Diff(float[] a, float[] b)
        {
            if (a.Length != b.Length) return float.PositiveInfinity;
            float m = 0f;
            for (int i = 0; i < a.Length; i++) m = MathF.Max(m, MathF.Abs(a[i] - b[i]));
            return m;
        }

        public static void TestR14BuildKSymmetricAndMatchesScalarDiagonal()
        {
            var m = Aniso();
            var p = new RVec3(1.3f, 0.7f, 0f);
            var k = YrContactMassMatrix3x3.BuildK(m, p, N);
            Require(k.At(0, 1) == k.At(1, 0) && k.At(0, 2) == k.At(2, 0)
                 && k.At(1, 2) == k.At(2, 1), "K 应对称");
            YrContactReduction.TangentBasis(N, out RVec3 n, out RVec3 t1, out RVec3 t2);
            Require(MathF.Abs(k.At(0, 0) - YrContactEffectiveMass.EffectiveMassReciprocal(m, p, n)) < 1e-4f,
                    "K_nn 应与第十三轮标量一致");
            Require(MathF.Abs(k.At(1, 1) - YrContactEffectiveMass.EffectiveMassReciprocal(m, p, t1)) < 1e-4f,
                    "K_t1t1 应与标量一致");
            Require(MathF.Abs(k.At(2, 2) - YrContactEffectiveMass.EffectiveMassReciprocal(m, p, t2)) < 1e-4f,
                    "K_t2t2 应与标量一致");
        }

        public static void TestR14CholeskyReconstructsK()
        {
            var m = Aniso(); var p = new RVec3(1.3f, 0.7f, 0f);
            var k = YrContactMassMatrix3x3.BuildK(m, p, N);
            Require(YrContactMassMatrix3x3.Cholesky(k, out YrMat3Full l), "K 应正定可分解");
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                {
                    float s = 0f;
                    for (int t = 0; t < 3; t++) s += l.At(i, t) * l.At(j, t);
                    Require(MathF.Abs(s - k.At(i, j)) < 2e-3f,
                            "L·Lᵀ 重构 K 失败 @(" + i + "," + j + ")");
                }
            for (int i = 0; i < 3; i++)
                for (int j = i + 1; j < 3; j++)
                    Require(l.At(i, j) == 0f, "L 应为下三角");
        }

        public static void TestR14SolveKxEqualsB()
        {
            var m = Aniso(); var p = new RVec3(1.3f, 0.7f, 0f);
            var k = YrContactMassMatrix3x3.BuildK(m, p, N);
            var b = new RVec3(0.4f, -1.1f, 0.9f);
            var x = YrContactMassMatrix3x3.Solve(k, b);
            var kx = YrContactMassMatrix3x3.Mul(k, x);
            Require(MathF.Abs(kx.X - b.X) < 1e-3f && MathF.Abs(kx.Y - b.Y) < 1e-3f
                 && MathF.Abs(kx.Z - b.Z) < 1e-3f, "Kx 应等于 b");
        }

        public static void TestR14JacobiEigenDiagonalizesK()
        {
            var m = Aniso(); var p = new RVec3(1.3f, 0.7f, 0f);
            var k = YrContactMassMatrix3x3.BuildK(m, p, N);
            YrContactMassMatrix3x3.JacobiEigen(k, out RVec3 lam, out YrMat3Full q);
            Require(lam.X > 0f && lam.Y > 0f && lam.Z > 0f, "特征值应为正");
            // 每个特征向量列 e_i 满足 K·q_i = λ_i·q_i
            for (int col = 0; col < 3; col++)
            {
                var v = new RVec3(q.At(0, col), q.At(1, col), q.At(2, col));
                var kv = YrContactMassMatrix3x3.Mul(k, v);
                float lamI = col == 0 ? lam.X : (col == 1 ? lam.Y : lam.Z);
                Require(MathF.Abs(kv.X - lamI * v.X) < 3e-3f
                     && MathF.Abs(kv.Y - lamI * v.Y) < 3e-3f
                     && MathF.Abs(kv.Z - lamI * v.Z) < 3e-3f,
                        "特征方程不成立 @col" + col);
            }
        }

        public static void TestR14InverseSqrtWhitenessIdentity()
        {
            var m = Aniso(); var p = new RVec3(1.3f, 0.7f, 0f);
            var k = YrContactMassMatrix3x3.BuildK(m, p, N);
            var c = YrContactMassMatrix3x3.InverseSqrt(k);
            for (int d = 0; d < 3; d++)
            {
                var e = new RVec3(d == 0 ? 1f : 0f, d == 1 ? 1f : 0f, d == 2 ? 1f : 0f);
                var ckc = YrContactMassMatrix3x3.Mul(c,
                    YrContactMassMatrix3x3.Mul(k, YrContactMassMatrix3x3.Mul(c, e)));
                Require(MathF.Abs(ckc.X - e.X) < 2e-3f && MathF.Abs(ckc.Y - e.Y) < 2e-3f
                     && MathF.Abs(ckc.Z - e.Z) < 2e-3f,
                        "C·K·C 应为单位阵 @d" + d);
            }
        }

        public static void TestR14DiagonalKDegeneratesToScalarWeightedRow()
        {
            // 无角惯量 + p 在质心 → K 严格对角 ⇒ 耦合版逐位退化为第十三轮
            var m = new YrContactMass(1f, 1f, new RVec3(0f, 0f, 0f),
                                      new RVec3(0f, 0f, 0f), RQuat.Identity,
                                      RQuat.Identity, RVec3.Zero, RVec3.Zero, 0.6f);
            var p = RVec3.Zero;
            var k = YrContactMassMatrix3x3.BuildK(m, p, N);
            Require(MathF.Abs(k.At(0, 1)) < 1e-6f && MathF.Abs(k.At(0, 2)) < 1e-6f
                 && MathF.Abs(k.At(1, 2)) < 1e-6f, "构造应为对角 K");
            float[] coupled = YrContactMassMatrix3x3.CoupledContactRow(m, p, N);
            float[] scalar = YrContactEffectiveMass.WeightedContactRow(m, p, N);
            Require(Diff(coupled, scalar) < 2e-3f,
                    "对角 K 应退化为第十三轮加权行，实际差 " + Diff(coupled, scalar));
        }

        public static void TestR14OffDiagonalKProducesMeasurableDifference()
        {
            var m = Aniso(); var p = new RVec3(1.3f, 0.7f, 0f);
            var k = YrContactMassMatrix3x3.BuildK(m, p, N);
            Require(MathF.Abs(k.At(0, 1)) > 1e-4f || MathF.Abs(k.At(0, 2)) > 1e-4f
                 || MathF.Abs(k.At(1, 2)) > 1e-4f, "该场景应有非对角耦合");
            float[] coupled = YrContactMassMatrix3x3.CoupledContactRow(m, p, N);
            float[] scalar = YrContactEffectiveMass.WeightedContactRow(m, p, N);
            Require(Diff(coupled, scalar) > 1e-3f,
                    "非对角耦合应产生可判定差异，实际 " + Diff(coupled, scalar));
        }

        public static void TestR14CoupledRowIs18DNormalizedAndDeterministic()
        {
            var m = Aniso(); var p = new RVec3(1.3f, 0.7f, 0f);
            float[] a = YrContactMassMatrix3x3.CoupledContactRow(m, p, N);
            float[] b = YrContactMassMatrix3x3.CoupledContactRow(m, p, N);
            Require(a.Length == 18, "行应为 18D");
            float norm = 0f;
            for (int i = 0; i < 18; i++)
            {
                norm += a[i] * a[i];
                Require(a[i] == b[i], "应逐位确定 @" + i);
            }
            Require(MathF.Abs(norm - 1f) < 1e-3f, "行应归一化，实际 " + norm);
            float tangent = 0f;
            for (int i = 6; i < 18; i++) tangent += MathF.Abs(a[i]);
            Require(tangent > 1e-5f, "切向段应非零");
        }

        public static void TestR14LeverArmGrowsKAndCoupling()
        {
            var m = Aniso();
            var kNear = YrContactMassMatrix3x3.BuildK(m, RVec3.Zero, N);
            var kFar = YrContactMassMatrix3x3.BuildK(m, new RVec3(3f, 1f, 0f), N);
            Require(kFar.At(0, 0) > kNear.At(0, 0) + 1e-3f, "力臂增大 K_nn 应增大");
            Require(MathF.Abs(kNear.At(0, 1)) < 1e-6f, "质心处非对角应为 0");
            // 本地数值预演：n=+Z、r 在 XY 平面时，(r×n) 也在 XY 平面，
            // 而 (r×t1)/(r×t2) 只有 Z 分量，故 K 的非对角只出现在 (t1,t2)
            // 即 At(1,2)；(0,1)/(0,2) 恒为 0。断言按真实耦合位置写。
            Require(MathF.Abs(kFar.At(0, 1)) > 1e-4f || MathF.Abs(kFar.At(0, 2)) > 1e-4f
                 || MathF.Abs(kFar.At(1, 2)) > 1e-4f,
                    "远离质心应产生非对角耦合（含 t1×t2 项）");
        }

        public static void TestR14ReduceByCoupledMassIsBoundedAndDeterministic()
        {
            var m = Aniso();
            var pts = new RVec3[] {
                new RVec3(0f,0f,0f), new RVec3(1f,0f,0f),
                new RVec3(0f,1f,0f), new RVec3(0f,0f,2f),
                new RVec3(0f,2f,1f), new RVec3(2f,1f,0f)
            };
            var ids = new int[] { 0, 1, 2, 3, 4, 5 };
            var dep = new float[] { 0.9f, 0.2f, 0.2f, 0.2f, 0.2f, 0.2f };
            var a = YrContactMassMatrix3x3.ReduceByCoupledMass(pts, ids, dep, N, m, 4);
            var b = YrContactMassMatrix3x3.ReduceByCoupledMass(pts, ids, dep, N, m, 4);
            Require(a.Length == 4 && b.Length == 4, "应缩到 4 点");
            bool deepest = false;
            var seen = new System.Collections.Generic.HashSet<int>();
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i].Id == 0) deepest = true;
                Require(seen.Add(a[i].Id), "Id 重复");
                Require(a[i].Id == b[i].Id, "逐位确定 @" + i);
                Require(a[i].Point.X == pts[a[i].Id].X
                     && a[i].Point.Y == pts[a[i].Id].Y
                     && a[i].Point.Z == pts[a[i].Id].Z, "Point 回填 @" + i);
            }
            Require(deepest, "应保留最深点 Id=0");
            Require(YrContactMassMatrix3x3.ReduceByCoupledMass(
                pts, ids, dep, N, m, 0).Length == 0, "max=0 应返回空");
        }

        public static void TestR14CouplingChangesSelectionVsScalarReduction()
        {
            // 第一版点集下耦合版与标量版选点完全相同（本地 float32 预演证实），
            // 断言会挂 —— 那是点集没有区分度，不是算法回退。换成数值预演确认
            // 有差异的点集：耦合 -> [0,3,4]，标量 -> [0,3,5]。
            var m = Aniso();
            var pts = new RVec3[] {
                new RVec3(0f,0f,0f), new RVec3(1f,0f,0f),
                new RVec3(2f,0f,0f), new RVec3(3f,0f,0f),
                new RVec3(0f,1f,0f), new RVec3(1f,0f,1f)
            };
            var ids = new int[] { 0, 1, 2, 3, 4, 5 };
            var dep = new float[] { 0.9f, 0.2f, 0.2f, 0.2f, 0.2f, 0.2f };
            var coupled = YrContactMassMatrix3x3.ReduceByCoupledMass(pts, ids, dep, N, m, 3);
            var scalar = YrContactReduction.ReduceByEffectiveMass(pts, ids, dep, N, m, 3);
            Require(coupled.Length == 3 && scalar.Length == 3, "两者都应给 3 点");
            bool differs = false;
            for (int i = 0; i < 3; i++)
                if (coupled[i].Id != scalar[i].Id) differs = true;
            Require(differs, "非对角耦合应改变选点");
        }

        public static void TestR14SingularContextDoesNotCrash()
        {
            var zero = new YrContactMass(0f, 0f, new RVec3(0f, 0f, 0f),
                                        new RVec3(0f, 0f, 0f), RQuat.Identity,
                                        RQuat.Identity, RVec3.Zero, RVec3.Zero, 0f);
            var k = YrContactMassMatrix3x3.BuildK(zero, RVec3.Zero, N);
            Require(!YrContactMassMatrix3x3.Cholesky(k, out _), "零矩阵应判非正定");
            var x = YrContactMassMatrix3x3.Solve(k, new RVec3(1f, 1f, 1f));
            Require(x.X == 0f && x.Y == 0f && x.Z == 0f, "奇异解应安全返回零");
            float[] row = YrContactMassMatrix3x3.CoupledContactRow(zero, RVec3.Zero, N);
            Require(row.Length == 18, "奇异上下文仍应产出 18D 行");
            for (int i = 0; i < 18; i++)
                Require(!float.IsNaN(row[i]) && !float.IsInfinity(row[i]),
                        "不应出现 NaN/Inf @" + i);
        }

        public static void TestR14JacobiIsDeterministic()
        {
            var m = Aniso(); var p = new RVec3(1.3f, 0.7f, 0f);
            var k = YrContactMassMatrix3x3.BuildK(m, p, N);
            YrContactMassMatrix3x3.JacobiEigen(k, out RVec3 l1, out YrMat3Full q1);
            YrContactMassMatrix3x3.JacobiEigen(k, out RVec3 l2, out YrMat3Full q2);
            Require(l1.X == l2.X && l1.Y == l2.Y && l1.Z == l2.Z, "特征值逐位确定");
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                    Require(q1.At(i, j) == q2.At(i, j), "Q 逐位确定");
        }

        public static void TestR14NoRegressionOnDefaultPipelinePath()
        {
            RVec3 H(float v) => new RVec3(v, v, v);
            var bodies = new RigidBody3D[] {
                new RigidBody3D { Position = RVec3.Zero, Mass = 1f },
                new RigidBody3D { Position = new RVec3(3f, 0f, 0f), Mass = 1f }
            };
            foreach (var b in bodies) b.SetInertia(RigidBody3D.SphereInertia(1f, 0.5f));
            var pipe = new YrPhysicsPipeline(bodies, new RVec3[] { H(1f), H(1f) });
            pipe.SleepEnabled = false;
            pipe.StepFixed(1f / 60f);
            Require(pipe.Tick == 1, "新增 3×3 矩阵不接入默认路径，应正常推进");
            ulong h1 = YrRollbackBuffer.Checksum(pipe.Snapshot(), pipe.Tick);
            pipe.StepFixed(1f / 60f);
            ulong h2 = YrRollbackBuffer.Checksum(pipe.Snapshot(), pipe.Tick);
            Require(h1 != h2, "第二 tick 快照应变化");
        }
    }
}
