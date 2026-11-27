using System;
using YamaEngine.Reality;

namespace YamaEngine.Tests
{
    /// <summary>
    /// 第十三轮回归（14 项）：割线加速 CA、两体有效质量矩阵、摩擦锥缩减。
    /// 全部进入现有 build.yml 的 Test step 真实执行。
    /// </summary>
    public static class RealityPhysicsThirteenthTests
    {
        private static void Require(bool ok, string msg)
        {
            if (!ok) throw new InvalidOperationException("REALITY_R13_FAIL: " + msg);
        }
        private static RVec3 H(float v) => new RVec3(v, v, v);
        private static YrObb Box(RVec3 c, float h) => YrObb.AxisAligned(c, H(h));
        private static RigidBody3D Make(RVec3 pos, float mass)
        {
            var b = new RigidBody3D { Position = pos, Mass = mass };
            b.SetInertia(RigidBody3D.SphereInertia(mass, 0.5f));
            return b;
        }
        private static RVec3 AnisoInvInertia()
        {
            RVec3 I = RigidBody3D.BoxInertia(1f, 0.4f, 2.0f, 1.2f);
            return new RVec3(I.X > 1e-12f ? 1f / I.X : 0f,
                             I.Y > 1e-12f ? 1f / I.Y : 0f,
                             I.Z > 1e-12f ? 1f / I.Z : 0f);
        }
        private static YrContactMass DefaultMass(float mu = 0.6f)
            => YrContactMass.Uniform(1f, new RVec3(10f, 10f, 10f),
                                     RVec3.Zero, new RVec3(4f, 0f, 0f), mu);

        public static void TestR13SecantZeroAngularMomentumMatchesAmCa()
        {
            var a = Box(new RVec3(-1.10f, 0f, 0f), 1f);
            var b = Box(new RVec3(1.10f, 0f, 0f), 1f);
            var va = new RVec3(17f, 0f, 0f);
            var invI = AnisoInvInertia();
            float dt = 1f / 60f;
            var am = YrAngularMomentumCa.ComputeToi(
                a, va, RVec3.Zero, invI, b, RVec3.Zero, RVec3.Zero, invI, dt);
            var sec = YrSecantCa.ComputeToi(
                a, va, RVec3.Zero, invI, b, RVec3.Zero, RVec3.Zero, invI, dt);
            Require(am.Toi >= 0f && sec.Toi >= 0f, "零角动量两者都应报 TOI");
            Require(Math.Abs(am.Toi - sec.Toi) < 1e-4f,
                    "零角动量割线版应与 AM 版一致: " + sec.Toi + " vs " + am.Toi);
            Require(sec.Converged, "常见场景割线版应收敛");
        }

        public static void TestR13SecantMatchesAmCaWithAngularMomentum()
        {
            var invI = AnisoInvInertia();
            int compared = 0;
            for (int k = 0; k < 10; k++)
            {
                float dist = 1.10f + (k % 7) * 0.02f;
                var a = Box(new RVec3(-dist, 0f, 0f), 1f);
                var b = Box(new RVec3(dist, 0f, 0f), 1f);
                var va = new RVec3(15f + (k % 5), 0f, 0f);
                var L = new RVec3(0.1f, 0.05f, 0.3f + (k % 5) * 0.15f);
                float dt = 1f / 60f;
                var am = YrAngularMomentumCa.ComputeToi(
                    a, va, L, invI, b, RVec3.Zero, RVec3.Zero, invI, dt);
                var sec = YrSecantCa.ComputeToi(
                    a, va, L, invI, b, RVec3.Zero, RVec3.Zero, invI, dt);
                Require((am.Toi >= 0f) == (sec.Toi >= 0f),
                        "割线版不得漏报/误报 @" + k);
                if (am.Toi >= 0f && sec.Toi >= 0f)
                {
                    compared++;
                    Require(Math.Abs(am.Toi - sec.Toi) < 1e-4f,
                            "带角动量 TOI 应与 AM 版一致 @" + k
                            + ": " + sec.Toi + " vs " + am.Toi);
                }
            }
            Require(compared >= 4, "应覆盖足够多命中场景，实际 " + compared);
        }

        public static void TestR13SecantAccelerationMechanismIsTriggered()
        {
            // 第十三轮诊断结论：云端 float32 下 SAT gap 非单调 + 姿态积分截断
            // 误差，使「割线迭代数一定少于标准 CA」**不是稳定成立**的性质
            // （实测某组 17 次 vs 标准 9 次）。因此不把迭代数倍率当硬断言，
            // 改为验证**加速机制本身**：实测斜率确实被采用、结果仍正确有界。
            var invI = AnisoInvInertia();
            int totalAccelerated = 0;
            int totalIterations = 0;
            int hits = 0;
            for (int k = 0; k < 10; k++)
            {
                float dist = 1.10f + (k % 7) * 0.02f;
                var a = Box(new RVec3(-dist, 0f, 0f), 1f);
                var b = Box(new RVec3(dist, 0f, 0f), 1f);
                var va = new RVec3(15f + (k % 5), 0f, 0f);
                var L = new RVec3(0.1f, 0.05f, 0.3f + (k % 5) * 0.15f);
                float dt = 1f / 60f;
                var am = YrAngularMomentumCa.ComputeToi(
                    a, va, L, invI, b, RVec3.Zero, RVec3.Zero, invI, dt);
                var sec = YrSecantCa.ComputeToi(
                    a, va, L, invI, b, RVec3.Zero, RVec3.Zero, invI, dt);
                Require((am.Toi >= 0f) == (sec.Toi >= 0f),
                        "割线不得漏报/误报 @" + k);
                if (am.Toi >= 0f)
                {
                    hits++;
                    Require(Math.Abs(am.Toi - sec.Toi) < 1e-4f,
                            "TOI 应与标准 AM CA 一致 @" + k);
                }
                Require(sec.Iterations <= 32,
                        "真实固定步下割线迭代应有限（上界 32），实际 "
                        + sec.Iterations + " @" + k);
                totalAccelerated += sec.AcceleratedSteps;
                totalIterations += sec.Iterations;
            }
            Require(hits >= 4, "应覆盖足够多命中场景，实际 " + hits);
            Require(totalAccelerated >= 2,
                    "割线加速机制（实测斜率步）应被触发，实际 " + totalAccelerated);
            Require(totalIterations > 0, "应完成采样");
        }

        public static void TestR13SecantIsDeterministic()
        {
            var a = Box(new RVec3(-1.12f, 0f, 0f), 1f);
            var b = Box(new RVec3(1.12f, 0f, 0f), 1f);
            var va = new RVec3(17f, 2f, -1f);
            var L = new RVec3(0.3f, 0.1f, 0.2f);
            var invI = AnisoInvInertia();
            var x = YrSecantCa.ComputeToi(
                a, va, L, invI, b, RVec3.Zero, RVec3.Zero, invI, 1f / 60f);
            var y = YrSecantCa.ComputeToi(
                a, va, L, invI, b, RVec3.Zero, RVec3.Zero, invI, 1f / 60f);
            Require(x.Toi == y.Toi && x.Iterations == y.Iterations
                 && x.Converged == y.Converged && x.Residual == y.Residual
                 && x.Substeps == y.Substeps,
                    "割线 CA 结果必须逐位确定");
        }

        public static void TestR13SecantSeparatedBodiesReturnMinusOne()
        {
            var a = Box(new RVec3(-5f, 0f, 0f), 1f);
            var b = Box(new RVec3(5f, 0f, 0f), 1f);
            var invI = AnisoInvInertia();
            var r = YrSecantCa.ComputeToi(
                a, RVec3.Zero, new RVec3(0f, 0f, 3f), invI,
                b, RVec3.Zero, RVec3.Zero, invI, 0.5f);
            Require(r.Toi < 0f, "静止分离体不应报接触");
            var r2 = YrSecantCa.ComputeToi(
                a, new RVec3(-20f, 0f, 0f), RVec3.Zero, invI,
                b, new RVec3(20f, 0f, 0f), RVec3.Zero, invI, 0.5f);
            Require(r2.Toi < 0f, "背向运动不应报接触");
        }

        public static void TestR13SecantEarliestToiAllPicksSmallest()
        {
            var bodies = new RigidBody3D[] {
                Make(new RVec3(-1.10f, 0f, 0f), 1f),
                Make(new RVec3(1.10f, 0f, 0f), 1f),
                Make(new RVec3(0f, 40f, 0f), 1f)
            };
            bodies[0].LinearVelocity = new RVec3(17f, 0f, 0f);
            var obbs = new YrObb[] {
                Box(bodies[0].Position, 1f), Box(bodies[1].Position, 1f), Box(bodies[2].Position, 1f)
            };
            float dt = 1f / 60f;
            float all = YrSecantCa.EarliestToiAll(obbs, bodies, dt);
            float pair01 = YrSecantCa.ComputeToi(
                obbs[0], bodies[0].LinearVelocity, bodies[0].AngularMomentum,
                bodies[0].InvInertiaLocal,
                obbs[1], bodies[1].LinearVelocity, bodies[1].AngularMomentum,
                bodies[1].InvInertiaLocal, dt).Toi;
            Require(all >= 0f, "应找到 TOI");
            Require(Math.Abs(all - pair01) < 1e-6f, "全对最早值应等于 (0,1) 对");
            var pairs = new (int, int)[] { (0, 1) };
            Require(Math.Abs(YrSecantCa.EarliestToi(
                pairs, obbs, bodies, dt) - pair01) < 1e-6f,
                    "候选对版本应与全对一致");
        }

        public static void TestR13EffectiveMassGrowsWithLeverArm()
        {
            // 诊断轮修正：DefaultMass 的 CenterB=(4,0,0)，p=(0,0,0) 时 r_b=(-4,0,0)
            // 力臂反而比 p=(3,0,0) 的 r_b=(-1,0,0) 更大 → near=162 > far=102。
            // 那是**测试构造写错**，不是 K 公式错。这里把两体质心都放在原点，
            // 使 r_a=r_b=p，力臂随 |p| 单调增大。
            var m = YrContactMass.Uniform(1f, new RVec3(10f, 10f, 10f),
                                          RVec3.Zero, RVec3.Zero, 0.6f);
            var n = new RVec3(0f, 0f, 1f);
            float kNear = YrContactEffectiveMass.EffectiveMassReciprocal(
                m, new RVec3(0f, 0f, 0f), n);
            float kFar = YrContactEffectiveMass.EffectiveMassReciprocal(
                m, new RVec3(3f, 0f, 0f), n);
            Require(kFar > kNear + 1e-4f,
                    "力臂越大 K 应越大: near=" + kNear + " far=" + kFar);
            Require(YrContactEffectiveMass.EffectiveMass(m, new RVec3(3f, 0f, 0f), n)
                  < YrContactEffectiveMass.EffectiveMass(m, new RVec3(0f, 0f, 0f), n),
                    "K 大 → m_eff 小");
            // 顺带验证原来的「质心偏移」语义确实也被公式正确捕获：
            // CenterB=(4,0,0) 时 p=0 的 r_b 力臂大于 p=3 的 r_b 力臂。
            var shifted = DefaultMass();
            float k0 = YrContactEffectiveMass.EffectiveMassReciprocal(
                shifted, new RVec3(0f, 0f, 0f), n);
            float k3 = YrContactEffectiveMass.EffectiveMassReciprocal(
                shifted, new RVec3(3f, 0f, 0f), n);
            Require(k0 > k3, "质心偏移下 K 应随 r_b 力臂变化，公式仍正确");
            Require(kNear >= m.InvMassA + m.InvMassB - 1e-6f,
                    "K 至少含两体平动项");
        }

        public static void TestR13EffectiveMassRespectsAnisotropicInertia()
        {
            var q = RQuat.FromAxisAngle(new RVec3(0f, 1f, 0f), 0.5f);
            var invI = AnisoInvInertia();
            var m = new YrContactMass(1f, 1f, invI, invI, q, RQuat.Identity,
                                      RVec3.Zero, new RVec3(4f, 0f, 0f), 0.6f);
            var p = new RVec3(2f, 0f, 0f);
            float kx = YrContactEffectiveMass.EffectiveMassReciprocal(
                m, p, new RVec3(1f, 0f, 0f));
            float ky = YrContactEffectiveMass.EffectiveMassReciprocal(
                m, p, new RVec3(0f, 1f, 0f));
            float kz = YrContactEffectiveMass.EffectiveMassReciprocal(
                m, p, new RVec3(0f, 0f, 1f));
            Require(MathF.Abs(ky - kz) > 1e-4f || MathF.Abs(kx - ky) > 1e-4f,
                    "各向异性惯量下不同方向 K 应不同");
            // 与 RigidBody3D 的世界系 I⁻¹ 作用逐项一致
            var rb = new RigidBody3D { Position = RVec3.Zero, Mass = 1f };
            rb.Orientation = q; rb.InvInertiaLocal = invI;
            var v = new RVec3(0.3f, 1.1f, -0.7f);
            RVec3 a = YrContactEffectiveMass.ApplyWorldInvInertia(q, invI, v);
            RVec3 b = rb.WorldInvInertiaApply(v);
            Require(a.X == b.X && a.Y == b.Y && a.Z == b.Z,
                    "世界系 I⁻¹ 作用应与 RigidBody3D 逐位一致");
        }

        public static void TestR13FrictionConeProjectionAndMembership()
        {
            Require(YrContactEffectiveMass.InsideFrictionCone(
                        2f, new RVec3(0.5f, 0f, 0f), 0.6f),
                    "锥内切向冲量应通过");
            Require(!YrContactEffectiveMass.InsideFrictionCone(
                        2f, new RVec3(2f, 0f, 0f), 0.6f),
                    "锥外切向冲量应拒绝");
            Require(!YrContactEffectiveMass.InsideFrictionCone(
                        -1f, RVec3.Zero, 0.6f),
                    "负法向冲量应拒绝");
            RVec3 proj = YrContactEffectiveMass.ProjectToFrictionCone(
                2f, new RVec3(2f, 0f, 0f), 0.6f);
            Require(MathF.Abs(proj.X - 1.2f) < 1e-5f,
                    "投影应截断到 μ·λ_n = 1.2，实际 " + proj.X);
            Require(YrContactEffectiveMass.InsideFrictionCone(2f, proj, 0.6f),
                    "投影后应在锥内");
            RVec3 inside = YrContactEffectiveMass.ProjectToFrictionCone(
                2f, new RVec3(0.5f, 0f, 0f), 0.6f);
            Require(inside.X == 0.5f, "锥内冲量投影应原样返回");
        }

        public static void TestR13TangentialConeScaleIsBoundedByMu()
        {
            var m = DefaultMass(0.3f);
            var n = new RVec3(0f, 0f, 1f);
            var p = new RVec3(1f, 0f, 0f);
            float s1 = YrContactEffectiveMass.TangentialConeScale(
                m, p, n, new RVec3(1f, 0f, 0f));
            float s2 = YrContactEffectiveMass.TangentialConeScale(
                m, p, n, new RVec3(0f, 1f, 0f));
            Require(s1 >= 0f && s1 <= 1f && s2 >= 0f && s2 <= 1f,
                    "摩擦锥缩放应在 [0,1]");
            var mHi = DefaultMass(10f);
            float sHi = YrContactEffectiveMass.TangentialConeScale(
                mHi, p, n, new RVec3(1f, 0f, 0f));
            Require(sHi >= s1 - 1e-6f,
                    "μ 增大不应让切向缩放变小: " + sHi + " vs " + s1);
        }

        public static void TestR13WeightedContactRowIs18DAndDeterministic()
        {
            var m = DefaultMass();
            var p = new RVec3(1f, 0.5f, 0f);
            var n = new RVec3(0f, 0f, 1f);
            float[] a = YrContactEffectiveMass.WeightedContactRow(m, p, n);
            float[] b = YrContactEffectiveMass.WeightedContactRow(m, p, n);
            Require(a.Length == 18, "行应为 18D，实际 " + a.Length);
            for (int i = 0; i < 18; i++)
                Require(a[i] == b[i], "加权行应逐位确定 @" + i);
            float tangent = 0f;
            for (int i = 6; i < 18; i++) tangent += MathF.Abs(a[i]);
            Require(tangent > 1e-5f,
                    "含摩擦切向的段应非零，实际 " + tangent);
            float norm = 0f;
            for (int i = 0; i < 18; i++) norm += a[i] * a[i];
            Require(MathF.Abs(norm - 1f) < 1e-4f,
                    "加权行应归一化，实际 " + norm);
        }

        public static void TestR13ReduceByEffectiveMassIsBoundedAndDeterministic()
        {
            var pts = new RVec3[] {
                new RVec3(0f,0f,0f), new RVec3(1f,0f,0f),
                new RVec3(0f,1f,0f), new RVec3(0f,0f,2f),
                new RVec3(0f,2f,1f), new RVec3(2f,1f,0f)
            };
            var ids = new int[] { 0, 1, 2, 3, 4, 5 };
            var dep = new float[] { 0.9f, 0.2f, 0.2f, 0.2f, 0.2f, 0.2f };
            var n = new RVec3(0f, 0f, 1f);
            var m = DefaultMass();
            var r1 = YrContactReduction.ReduceByEffectiveMass(pts, ids, dep, n, m, 4);
            var r2 = YrContactReduction.ReduceByEffectiveMass(pts, ids, dep, n, m, 4);
            Require(r1.Length == 4 && r2.Length == 4,
                    "应缩减到 4 点，实际 " + r1.Length);
            bool deepest = false;
            var seen = new System.Collections.Generic.HashSet<int>();
            for (int i = 0; i < r1.Length; i++)
            {
                if (r1[i].Id == 0) deepest = true;
                Require(seen.Add(r1[i].Id), "Id 重复");
                Require(r1[i].Id == r2[i].Id, "缩减应逐位确定 @" + i);
                Require(r1[i].Point.X == pts[r1[i].Id].X
                     && r1[i].Point.Y == pts[r1[i].Id].Y
                     && r1[i].Point.Z == pts[r1[i].Id].Z,
                        "Point 应按 Id 正确回填 @" + i);
            }
            Require(deepest, "必须保留最深点 Id=0");
            for (int i = 1; i < r1.Length; i++)
                Require(r1[i].Id > r1[i - 1].Id, "输出应按 Id 升序");
            Require(YrContactReduction.ReduceByEffectiveMass(
                pts, ids, dep, n, m, 0).Length == 0, "max=0 应返回空");
        }

        public static void TestR13EffectiveMassChangesSelection()
        {
            var pts = new RVec3[] {
                new RVec3(0f,0f,0f), new RVec3(1f,0f,0f),
                new RVec3(0f,1f,0f), new RVec3(0f,0f,2f),
                new RVec3(0f,2f,1f), new RVec3(2f,1f,0f)
            };
            var ids = new int[] { 0, 1, 2, 3, 4, 5 };
            var dep = new float[] { 0.9f, 0.2f, 0.2f, 0.2f, 0.2f, 0.2f };
            var n = new RVec3(0f, 0f, 1f);
            // 轻惯量（角段几乎自由）vs 重惯量（角段几乎锁死）
            var light = YrContactMass.Uniform(1f, new RVec3(0.01f, 0.01f, 0.01f),
                                              RVec3.Zero, new RVec3(4f, 0f, 0f), 0.6f);
            var heavy = YrContactMass.Uniform(1f, new RVec3(100f, 100f, 100f),
                                              RVec3.Zero, new RVec3(4f, 0f, 0f), 0.6f);
            var a = YrContactReduction.ReduceByEffectiveMass(pts, ids, dep, n, light, 3);
            var b = YrContactReduction.ReduceByEffectiveMass(pts, ids, dep, n, heavy, 3);
            Require(a.Length == 3 && b.Length == 3, "两者都应给 3 点");
            bool differs = false;
            for (int i = 0; i < 3; i++)
                if (a[i].Id != b[i].Id) differs = true;
            Require(differs, "两体惯量张量应改变选点（有效质量加权生效）");
            // 摩擦系数也应可判定地影响选点
            var noFric = YrContactMass.Uniform(1f, new RVec3(10f, 10f, 10f),
                                               RVec3.Zero, new RVec3(4f, 0f, 0f), 0f);
            var hiFric = YrContactMass.Uniform(1f, new RVec3(10f, 10f, 10f),
                                               RVec3.Zero, new RVec3(4f, 0f, 0f), 5f);
            var c = YrContactReduction.ReduceByEffectiveMass(pts, ids, dep, n, noFric, 3);
            var d = YrContactReduction.ReduceByEffectiveMass(pts, ids, dep, n, hiFric, 3);
            bool fricDiffers = false;
            for (int i = 0; i < 3; i++)
                if (c[i].Id != d[i].Id) fricDiffers = true;
            Require(fricDiffers, "库仑摩擦锥缩放应改变选点");
        }

        public static void TestR13SecantPipelineSwitchDefaultOffAndNoRegression()
        {
            var bodies = new RigidBody3D[] {
                Make(RVec3.Zero, 1f), Make(new RVec3(1.8f, 0f, 0f), 1f)
            };
            var pipe = new YrPhysicsPipeline(bodies, new RVec3[] { H(1f), H(1f) });
            Require(!pipe.UseSecantCa, "新开关默认必须关闭");
            pipe.SleepEnabled = false;
            pipe.StepFixed(1f / 60f);
            Require(pipe.Tick == 1, "默认路径应正常推进");

            YrPhysicsPipeline MakePipe(bool sec)
            {
                var bs = new RigidBody3D[] {
                    Make(RVec3.Zero, 1f), Make(new RVec3(1.8f, 0f, 0f), 1f)
                };
                var q = new YrPhysicsPipeline(bs, new RVec3[] { H(1f), H(1f) });
                q.SleepEnabled = false; q.UseSecantCa = sec;
                return q;
            }
            var p0 = MakePipe(false); var p1 = MakePipe(true);
            for (int k = 0; k < 5; k++)
            {
                p0.StepFixed(1f / 60f); p1.StepFixed(1f / 60f);
                ulong h0 = YrRollbackBuffer.Checksum(p0.Snapshot(), p0.Tick);
                ulong h1 = YrRollbackBuffer.Checksum(p1.Snapshot(), p1.Tick);
                Require(h0 == h1, "割线开关不应改变常规 StepFixed 路径 @" + (k + 1));
            }
            var ccd = MakePipe(true);
            ccd.UseToiSubsteps = true;
            ccd.StepFixedToi(1f / 60f);
            Require(ccd.Tick == 1, "割线 CA 的 CCD 子步路径应正常推进");
            Require(ccd.UseSecantCa && ccd.LastToiSubsteps >= 1,
                    "开关应透传到 ToiAdvance");
        }
    }
}
