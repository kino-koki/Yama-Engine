using System;
using YamaEngine.Reality;

namespace YamaEngine.Tests
{
    /// <summary>
    /// 第十二轮回归（14 项）：角动量版 CA、参考面候选修正、18D 接触 Jacobian 缩减。
    /// 全部进入现有 build.yml 的 Test step 真实执行。
    /// </summary>
    public static class RealityPhysicsTwelfthTests
    {
        private static void Require(bool ok, string msg)
        {
            if (!ok) throw new InvalidOperationException("REALITY_R12_FAIL: " + msg);
        }
        private static RVec3 H(float v) => new RVec3(v, v, v);
        private static YrObb Box(RVec3 c, float h) => YrObb.AxisAligned(c, H(h));
        private static RigidBody3D Make(RVec3 pos, float mass)
        {
            var b = new RigidBody3D { Position = pos, Mass = mass };
            b.SetInertia(RigidBody3D.SphereInertia(mass, 0.5f));
            return b;
        }
        private static float QDot(RQuat a, RQuat b)
            => MathF.Abs(a.X * b.X + a.Y * b.Y + a.Z * b.Z + a.W * b.W);

        private static RVec3 AnisoInvInertia()
        {
            // 明显各向异性的长条盒 → ω 会进动
            RVec3 I = RigidBody3D.BoxInertia(1f, 0.4f, 2.0f, 1.2f);
            return new RVec3(I.X > 1e-12f ? 1f / I.X : 0f,
                             I.Y > 1e-12f ? 1f / I.Y : 0f,
                             I.Z > 1e-12f ? 1f / I.Z : 0f);
        }

        public static void TestR12AmOrientationMatchesRigidBodyUnderAnisotropicInertia()
        {
            RVec3 invI = AnisoInvInertia();
            var q0 = RQuat.FromAxisAngle(new RVec3(0.2f, 1f, 0.3f), 0.4f);
            var L = new RVec3(0.7f, 1.3f, -0.5f);
            float T = 0.5f;
            var b = new RigidBody3D { Position = RVec3.Zero, Mass = 1f };
            b.Orientation = q0;
            b.InvInertiaLocal = invI;
            b.AngularMomentum = L;
            int N = 4000;
            for (int i = 0; i < N; i++) b.Integrate(T / N);
            RQuat refQ = b.Orientation;
            RQuat q = YrAngularMomentumCa.OrientationAt(q0, L, invI, T);
            float dot = QDot(refQ, q);
            Require(dot > 0.999f,
                    "L 版姿态积分应与 RigidBody3D 小步积分一致 (|dot|=" + dot + ")");
        }

        public static void TestR12AmOrientationPrecessesWhileConstantOmegaDoesNot()
        {
            RVec3 invI = AnisoInvInertia();
            var q0 = RQuat.FromAxisAngle(new RVec3(0.2f, 1f, 0.3f), 0.4f);
            var L = new RVec3(0.7f, 1.3f, -0.5f);
            RVec3 w0 = YrAngularMomentumCa.OmegaAt(q0, L, invI);
            RQuat q1 = YrAngularMomentumCa.OrientationAt(q0, L, invI, 0.6f);
            RVec3 w1 = YrAngularMomentumCa.OmegaAt(q1, L, invI);
            float dw = (w1 - w0).Length();
            Require(dw > 1e-3f, "各向异性惯量下 ω 应随姿态进动，实际变化 " + dw);
            // 常量 ω 外推的姿态与 L 版应有可判定差异
            RQuat constant = YrFullRotationalCa.IntegrateOrientation(q0, w0, 0.6f);
            Require(QDot(constant, q1) < 1f - 1e-4f,
                    "常量 ω 外推应明显偏离进动姿态");
        }

        public static void TestR12AmCaZeroSpinMatchesFullRotationalCa()
        {
            var a = Box(new RVec3(-1.10f, 0f, 0f), 1f);
            var b = Box(new RVec3(1.10f, 0f, 0f), 1f);
            var va = new RVec3(17f, 0f, 0f);
            var invI = new RVec3(10f, 10f, 10f);
            float dt = 1f / 60f;
            float full = YrFullRotationalCa.ComputeToi(
                a, va, RVec3.Zero, b, RVec3.Zero, RVec3.Zero, dt).Toi;
            float am = YrAngularMomentumCa.ComputeToi(
                a, va, RVec3.Zero, invI, b, RVec3.Zero, RVec3.Zero, invI, dt).Toi;
            Require(full >= 0f && am >= 0f, "零角动量两者都应报 TOI");
            Require(Math.Abs(full - am) < 1e-3f,
                    "零角动量下 L 版应与完整旋转 CA 一致: " + am + " vs " + full);
        }

        public static void TestR12AmCaIsDeterministic()
        {
            var a = Box(new RVec3(-1.12f, 0f, 0f), 1f);
            var b = Box(new RVec3(1.12f, 0f, 0f), 1f);
            var va = new RVec3(17f, 2f, -1f);
            var L = new RVec3(0.3f, 0.1f, 0.2f);
            var invI = AnisoInvInertia();
            var x = YrAngularMomentumCa.ComputeToi(
                a, va, L, invI, b, RVec3.Zero, RVec3.Zero, invI, 1f / 60f);
            var y = YrAngularMomentumCa.ComputeToi(
                a, va, L, invI, b, RVec3.Zero, RVec3.Zero, invI, 1f / 60f);
            Require(x.Toi == y.Toi && x.Iterations == y.Iterations
                 && x.Converged == y.Converged && x.Residual == y.Residual
                 && x.Substeps == y.Substeps,
                    "L 版 CA 结果必须逐位确定");
        }

        public static void TestR12AmCaSeparatedBodiesReturnMinusOne()
        {
            var a = Box(new RVec3(-5f, 0f, 0f), 1f);
            var b = Box(new RVec3(5f, 0f, 0f), 1f);
            var invI = AnisoInvInertia();
            var r = YrAngularMomentumCa.ComputeToi(
                a, RVec3.Zero, new RVec3(0f, 0f, 3f), invI,
                b, RVec3.Zero, RVec3.Zero, invI, 0.5f);
            Require(r.Toi < 0f, "静止分离体（即使自旋）不应报接触");
            var r2 = YrAngularMomentumCa.ComputeToi(
                a, new RVec3(-20f, 0f, 0f), RVec3.Zero, invI,
                b, new RVec3(20f, 0f, 0f), RVec3.Zero, invI, 0.5f);
            Require(r2.Toi < 0f, "背向运动不应报接触");
        }

        public static void TestR12AmCaNeverMissesWhenConservativeSweepReportsHit()
        {
            // 带角动量场景：整帧角速度膨胀版若报接触，L 版 CA 也必须报（不漏报）。
            var invI = AnisoInvInertia();
            int checkedPairs = 0;
            for (int k = 0; k < 6; k++)
            {
                float dist = 1.10f + k * 0.02f;
                var a = Box(new RVec3(-dist, 0f, 0f), 1f);
                var b = Box(new RVec3(dist, 0f, 0f), 1f);
                var va = new RVec3(15f + k, 0f, 0f);
                var wa = new RVec3(0f, 0f, 2f + k * 2f);
                float dt = 1f / 60f;
                float inflated = YrSweptObb.ComputeToiWithAngular(
                    a, va, wa, b, RVec3.Zero, RVec3.Zero, dt);
                if (inflated < 0f) continue;
                checkedPairs++;
                // L 与 ω 的关系：ω = invI·L（局部对角），这里取 L = wa / invI 的近似
                // 让初始 ω 与 wa 同量级；CA 自己会按姿态重算 ω。
                var L = new RVec3(wa.X / MathF.Max(invI.X, 1e-6f),
                                  wa.Y / MathF.Max(invI.Y, 1e-6f),
                                  wa.Z / MathF.Max(invI.Z, 1e-6f));
                float am = YrAngularMomentumCa.ComputeToi(
                    a, va, L, invI, b, RVec3.Zero, RVec3.Zero, invI, dt).Toi;
                Require(am >= 0f, "保守膨胀报接触而 L 版漏报 @" + k);
            }
            Require(checkedPairs >= 3, "应覆盖足够多命中场景，实际 " + checkedPairs);
        }

        public static void TestR12AmCaEarliestToiAllPicksSmallest()
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
            float all = YrAngularMomentumCa.EarliestToiAll(obbs, bodies, dt);
            float pair01 = YrAngularMomentumCa.ComputeToi(
                obbs[0], bodies[0].LinearVelocity, bodies[0].AngularMomentum,
                bodies[0].InvInertiaLocal,
                obbs[1], bodies[1].LinearVelocity, bodies[1].AngularMomentum,
                bodies[1].InvInertiaLocal, dt).Toi;
            Require(all >= 0f, "应找到 TOI");
            Require(Math.Abs(all - pair01) < 1e-6f, "全对最早值应等于 (0,1) 对");
            var pairs = new (int, int)[] { (0, 1) };
            Require(Math.Abs(YrAngularMomentumCa.EarliestToi(
                pairs, obbs, bodies, dt) - pair01) < 1e-6f,
                    "候选对版本应与全对一致");
        }

        public static void TestR12FaceCandidatesNeverIncludeOppositeSide()
        {
            var a = YrObb.AxisAligned(RVec3.Zero, H(1f));
            var b = YrObb.AxisAligned(new RVec3(1.6f, 0.25f, -0.15f), H(1f));
            var hit = YrConvex.SatObbObb(a, b);
            Require(hit.Hit, "重叠盒应命中");
            var pts = YrFaceClipping.BuildMultiFace(a, b, hit);
            Require(pts.Length >= 1, "多面流形不应为空");
            // 每轴只取与 n 同向一侧：同一 axis 只能出现一个 sign
            var axisSign = new System.Collections.Generic.Dictionary<int, int>();
            for (int i = 0; i < pts.Length; i++)
            {
                int fid = pts[i].Id / YrFaceClipping.MaxPoints;
                int axis = fid / 2, sign = (fid % 2 == 0) ? -1 : 1;
                if (axisSign.TryGetValue(axis, out int prev))
                    Require(prev == sign, "同一轴的正反面不应同时入候选: axis=" + axis);
                else axisSign[axis] = sign;
            }
            Require(axisSign.Count <= 3, "最多 3 个轴各 1 面，实际 " + axisSign.Count);
        }

        public static void TestR12FaceCandidatesStillInsideOwnReferenceFace()
        {
            var q = RQuat.FromAxisAngle(new RVec3(0f, 1f, 0f), 0.35f);
            var a = new YrObb(RVec3.Zero, q, H(1f));
            var b = YrObb.AxisAligned(new RVec3(1.55f, 0.2f, 0f), H(1f));
            var hit = YrConvex.SatObbObb(a, b);
            var pts = YrFaceClipping.BuildMultiFace(a, b, hit);
            Require(pts.Length >= 1, "旋转 A 下多面流形不应为空");
            var seen = new System.Collections.Generic.HashSet<int>();
            for (int i = 0; i < pts.Length; i++)
            {
                Require(seen.Add(pts[i].Id), "Id 冲突");
                int fid = pts[i].Id / YrFaceClipping.MaxPoints;
                int axis = fid / 2, sign = (fid % 2 == 0) ? -1 : 1;
                var rf = YrFaceClipping.RefFaceFor(a, axis, sign);
                Require(YrFaceClipping.InsideReference(rf, pts[i].Point),
                        "候选修正后点仍应在自己的参考面内: id=" + pts[i].Id);
            }
            var red = YrFaceClipping.BuildMultiFaceReduced(a, b, hit);
            Require(red.Length >= 1 && red.Length <= 4, "缩减应有界，实际 " + red.Length);
            var seen2 = new System.Collections.Generic.HashSet<int>();
            for (int i = 0; i < red.Length; i++)
                Require(seen2.Add(red[i].Id), "缩减后 Id 重复");
        }

        public static void TestR12ContactJacobianRowHasTangentLeverArms()
        {
            var row = YrContactReduction.ContactJacobianRow(
                new RVec3(1f, 0f, 0f), RVec3.Zero, new RVec3(0f, 0f, 1f));
            Require(row.Length == 18, "行应为 18D，实际 " + row.Length);
            // n=+Z → t1=+X, t2=+Y；r=(1,0,0) 时 r×t2=(0,0,1) 非零
            float tangential = 0f;
            for (int k = 6; k < 18; k++) tangential += MathF.Abs(row[k]);
            Require(tangential > 1e-4f,
                    "切向段应有非零力臂，实际 " + tangential);
            // 正交基确定性
            YrContactReduction.TangentBasis(new RVec3(0f, 0f, 1f),
                out RVec3 n, out RVec3 t1, out RVec3 t2);
            Require(MathF.Abs(n.Dot(t1)) < 1e-6f && MathF.Abs(n.Dot(t2)) < 1e-6f
                 && MathF.Abs(t1.Dot(t2)) < 1e-6f, "切向基应两两正交");
            YrContactReduction.TangentBasis(new RVec3(0f, 0f, 1f),
                out RVec3 n2, out RVec3 t1b, out RVec3 t2b);
            Require(n.X == n2.X && t1.X == t1b.X && t2.X == t2b.X,
                    "正交基应逐位确定");
        }

        public static void TestR12ContactJacobianReductionDiffersFromWrench()
        {
            // n=+Z。数值预演（float64 复刻 MGS）确认：该点集下
            //   6D wrench → [0,1,4]，18D 接触 Jacobian → [0,3,4]，
            // 线性重 → [0,3,4]，角段重 → [0,1,4]。即 18D 会挑出
            // 含切向力臂的 Id=4，且与 6D 选点不同、权重确实改变选点。
            var pts = new RVec3[] {
                new RVec3(0f,0f,0f), new RVec3(1f,0f,0f),
                new RVec3(0f,1f,0f), new RVec3(0f,0f,2f),
                new RVec3(0f,2f,1f)
            };
            var ids = new int[] { 0, 1, 2, 3, 4 };
            var dep = new float[] { 0.9f, 0.2f, 0.2f, 0.2f, 0.2f };
            var n = new RVec3(0f, 0f, 1f);
            var wr = YrContactReduction.ReduceByWrench(pts, ids, dep, n, RVec3.Zero, 3);
            var cj = YrContactReduction.ReduceByContactJacobian(
                pts, ids, dep, n, RVec3.Zero, 3);
            Require(wr.Length == 3 && cj.Length == 3, "两者都应给 3 点");
            bool cjHasP4 = false;
            foreach (var r in cj) if (r.Id == 4) cjHasP4 = true;
            Require(cjHasP4, "18D 版应挑出含切向力臂的点 Id=4");
            bool differs = false;
            for (int i = 0; i < 3; i++)
                if (wr[i].Id != cj[i].Id) differs = true;
            Require(differs, "18D（含摩擦切向）与 6D 选点应有可判定差异");
            var set = new System.Collections.Generic.HashSet<int>();
            foreach (var r in cj) Require(set.Add(r.Id), "18D 缩减后 Id 重复");
            for (int i = 1; i < cj.Length; i++)
                Require(cj[i].Id > cj[i - 1].Id, "18D 输出应按 Id 升序");
            var cj2 = YrContactReduction.ReduceByContactJacobian(
                pts, ids, dep, n, RVec3.Zero, 3);
            for (int i = 0; i < cj.Length; i++)
                Require(cj[i].Id == cj2[i].Id, "18D 缩减应逐位确定 @" + i);
        }

        public static void TestR12ContactJacobianWeightsChangeSelection()
        {
            var pts = new RVec3[] {
                new RVec3(0f,0f,0f), new RVec3(1f,0f,0f),
                new RVec3(0f,1f,0f), new RVec3(0f,0f,2f),
                new RVec3(0f,2f,1f)
            };
            var ids = new int[] { 0, 1, 2, 3, 4 };
            var dep = new float[] { 0.9f, 0.2f, 0.2f, 0.2f, 0.2f };
            var n = new RVec3(0f, 0f, 1f);
            var linearHeavy = YrContactReduction.ReduceByContactJacobian(
                pts, ids, dep, n, RVec3.Zero, 3, 4f, 0.25f);
            var angularHeavy = YrContactReduction.ReduceByContactJacobian(
                pts, ids, dep, n, RVec3.Zero, 3, 0.25f, 4f);
            Require(linearHeavy.Length == 3 && angularHeavy.Length == 3,
                    "两种权重都应给 3 点");
            bool differs = false;
            for (int i = 0; i < 3; i++)
                if (linearHeavy[i].Id != angularHeavy[i].Id) differs = true;
            Require(differs, "线性/角段权重应改变选点（有效质量一阶近似生效）");
            Require(YrContactReduction.ReduceByContactJacobian(
                pts, ids, dep, n, RVec3.Zero, 0).Length == 0, "max=0 应返回空");
        }

        public static void TestR12MultiFaceBuildIsDeterministicAfterCandidateFix()
        {
            var a = YrObb.AxisAligned(RVec3.Zero, H(1f));
            var b = YrObb.AxisAligned(new RVec3(1.7f, 0.3f, 0.2f), H(1f));
            var hit = YrConvex.SatObbObb(a, b);
            var p1 = YrFaceClipping.BuildMultiFace(a, b, hit);
            var p2 = YrFaceClipping.BuildMultiFace(a, b, hit);
            Require(p1.Length == p2.Length && p1.Length >= 1, "点数应确定且非空");
            for (int i = 0; i < p1.Length; i++)
                Require(p1[i].Id == p2[i].Id
                     && p1[i].Point.X == p2[i].Point.X
                     && p1[i].Point.Y == p2[i].Point.Y
                     && p1[i].Point.Z == p2[i].Point.Z,
                        "候选修正后多面点逐位不确定 @" + i);
            var r1 = YrFaceClipping.BuildMultiFaceReduced(a, b, hit);
            var r2 = YrFaceClipping.BuildMultiFaceReduced(a, b, hit);
            Require(r1.Length == r2.Length, "缩减点数不确定");
            for (int i = 0; i < r1.Length; i++)
                Require(r1[i].Id == r2[i].Id, "缩减结果逐位不确定 @" + i);
        }

        public static void TestR12PipelineAmCaSwitchDefaultOffAndNoRegression()
        {
            var bodies = new RigidBody3D[] {
                Make(RVec3.Zero, 1f), Make(new RVec3(1.8f, 0f, 0f), 1f)
            };
            var pipe = new YrPhysicsPipeline(bodies, new RVec3[] { H(1f), H(1f) });
            Require(!pipe.UseAngularMomentumCa, "新开关默认必须关闭");
            pipe.SleepEnabled = false;
            pipe.StepFixed(1f / 60f);
            Require(pipe.Tick == 1, "默认路径应正常推进");

            YrPhysicsPipeline MakePipe(bool am)
            {
                var bs = new RigidBody3D[] {
                    Make(RVec3.Zero, 1f), Make(new RVec3(1.8f, 0f, 0f), 1f)
                };
                var q = new YrPhysicsPipeline(bs, new RVec3[] { H(1f), H(1f) });
                q.SleepEnabled = false; q.UseAngularMomentumCa = am;
                return q;
            }
            var p0 = MakePipe(false); var p1 = MakePipe(true);
            for (int k = 0; k < 5; k++)
            {
                p0.StepFixed(1f / 60f); p1.StepFixed(1f / 60f);
                ulong h0 = YrRollbackBuffer.Checksum(p0.Snapshot(), p0.Tick);
                ulong h1 = YrRollbackBuffer.Checksum(p1.Snapshot(), p1.Tick);
                Require(h0 == h1, "L 版 CA 开关不应改变常规 StepFixed 路径 @" + (k + 1));
            }
            var ccd = MakePipe(true);
            ccd.UseToiSubsteps = true;
            ccd.StepFixedToi(1f / 60f);
            Require(ccd.Tick == 1, "L 版 CA 的 CCD 子步路径应正常推进");
            Require(ccd.UseAngularMomentumCa && ccd.LastToiSubsteps >= 1,
                    "开关应透传到 ToiAdvance");
        }
    }
}
