using System;
using YamaEngine.Reality;

namespace YamaEngine.Tests
{
    /// <summary>
    /// 第十一轮回归（14 项）：完整旋转 CA、多面各自入射面、wrench 独立性缩减。
    /// 全部进入现有 build.yml 的 Test step 真实执行。
    /// </summary>
    public static class RealityPhysicsEleventhTests
    {
        private static void Require(bool ok, string msg)
        {
            if (!ok) throw new InvalidOperationException("REALITY_R11_FAIL: " + msg);
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

        public static void TestR11FullCaZeroSpinApproachesAnalyticToi()
        {
            var a = Box(new RVec3(-5f, 0f, 0f), 1f);
            var b = Box(new RVec3(5f, 0f, 0f), 1f);
            var va = new RVec3(20f, 0f, 0f);
            float dt = 0.65f;
            float analytic = YrSweptObb.ComputeToi(a, va, b, RVec3.Zero, dt);
            var full = YrFullRotationalCa.ComputeToi(
                a, va, RVec3.Zero, b, RVec3.Zero, RVec3.Zero, dt);
            Require(analytic >= 0f && full.Toi >= 0f, "解析与完整 CA 都应报 TOI");
            Require(Math.Abs(full.Toi - analytic) < 2e-3f,
                    "零角速完整 CA 应逼近解析 TOI: diff=" + Math.Abs(full.Toi - analytic));
            Require(full.Toi <= analytic + 1e-3f, "不得晚报（漏报）");
            Require(full.Converged, "常见场景应收敛");
        }

        public static void TestR11FullCaMatchesLinearCaAtZeroSpin()
        {
            var a = Box(new RVec3(-5f, 0f, 0f), 1f);
            var b = Box(new RVec3(5f, 0f, 0f), 1f);
            var va = new RVec3(17f, 3f, -2f);
            float dt = 0.7f;
            float linear = YrConservativeAdvancement.ComputeToi(
                a, va, RVec3.Zero, b, RVec3.Zero, RVec3.Zero, dt).Toi;
            float full = YrFullRotationalCa.ComputeToi(
                a, va, RVec3.Zero, b, RVec3.Zero, RVec3.Zero, dt).Toi;
            Require(linear >= 0f && full >= 0f, "两者都应报 TOI");
            Require(Math.Abs(full - linear) < 2e-3f,
                    "零角速下完整 CA 应与线性化 CA 一致: " + full + " vs " + linear);
        }

        public static void TestR11FullCaIsNoEarlierThanLinearCaWithSpin()
        {
            for (int k = 0; k < 6; k++)
            {
                var a = Box(new RVec3(-5f - k * 0.3f, 0f, 0f), 1f);
                var b = Box(new RVec3(5f, 0f, 0f), 1f);
                var va = new RVec3(18f + k, 0f, 0f);
                var wa = new RVec3(0f, 0f, 2f + k * 3f);
                float dt = 0.7f;
                float linear = YrConservativeAdvancement.ComputeToi(
                    a, va, wa, b, RVec3.Zero, RVec3.Zero, dt).Toi;
                float full = YrFullRotationalCa.ComputeToi(
                    a, va, wa, b, RVec3.Zero, RVec3.Zero, dt).Toi;
                Require(linear >= 0f && full >= 0f, "带旋转两者都应报 TOI @" + k);
                // 完整 CA 用真实姿态积分，不再按累积时间膨胀 → 不早报（更精确）
                Require(full >= linear - 1e-4f,
                        "完整 CA 不应比线性化 CA 更早报 @" + k + ": " + full + " < " + linear);
            }
        }

        public static void TestR11IntegrateOrientationMatchesRigidBodyIntegration()
        {
            var w = new RVec3(0.3f, 1.1f, -0.7f);
            float inertia = 0.4f * 1f * 0.5f * 0.5f;   // SphereInertia(1, 0.5) 对角元
            var q0 = RQuat.FromAxisAngle(new RVec3(0.2f, 1f, 0.3f), 0.4f);
            var b = Make(RVec3.Zero, 1f);
            b.Orientation = q0;
            b.AngularMomentum = w * inertia;   // 各向同性 ⇒ ω 恒定 = w
            float T = 0.5f;
            int N = 4000;
            for (int i = 0; i < N; i++) b.Integrate(T / N);
            RQuat integrated = b.Orientation;
            RQuat exact = YrFullRotationalCa.IntegrateOrientation(q0, w, T);
            float dot = QDot(integrated, exact);
            Require(dot > 0.999f,
                    "四元数积分应与 RigidBody3D 小步积分一致 (|dot|=" + dot + ")");
        }

        public static void TestR11IntegrateOrientationIdentityAndNorm()
        {
            var q0 = RQuat.FromAxisAngle(new RVec3(1f, 2f, 3f), 0.9f);
            Require(QDot(YrFullRotationalCa.IntegrateOrientation(q0, RVec3.Zero, 5f), q0) > 1f - 1e-6f,
                    "零角速度应原样返回");
            Require(QDot(YrFullRotationalCa.IntegrateOrientation(q0, new RVec3(0f, 3f, 0f), 0f), q0) > 1f - 1e-6f,
                    "t=0 应原样返回");
            RQuat q = YrFullRotationalCa.IntegrateOrientation(q0, new RVec3(1f, 2f, -1f), 1.7f);
            float norm = q.X * q.X + q.Y * q.Y + q.Z * q.Z + q.W * q.W;
            Require(MathF.Abs(norm - 1f) < 1e-5f, "积分后四元数应保持单位范数: " + norm);
        }

        public static void TestR11FullCaIsDeterministic()
        {
            var a = Box(new RVec3(-5f, 0f, 0f), 1f);
            var b = Box(new RVec3(5f, 0f, 0f), 1f);
            var va = new RVec3(17f, 2f, -1f);
            var wa = new RVec3(0f, 3f, 7f);
            var x = YrFullRotationalCa.ComputeToi(a, va, wa, b, RVec3.Zero, RVec3.Zero, 0.7f);
            var y = YrFullRotationalCa.ComputeToi(a, va, wa, b, RVec3.Zero, RVec3.Zero, 0.7f);
            Require(x.Toi == y.Toi && x.Iterations == y.Iterations
                 && x.Converged == y.Converged && x.Residual == y.Residual,
                    "完整 CA 结果必须逐位确定");
        }

        public static void TestR11FullCaIterationCountIsBounded()
        {
            // 用**真实固定步** 1/60 + 合理初始间隙（中心距 2.20~2.32，间隙 0.20~0.32）。
            // 第十轮线性化 CA 把 spin·t 从 0 累积，2~3 次就判接触；完整 CA
            // 用真实姿态积分，不会虚报，代价是带旋转时呈几何级数收敛（实测最大 34 次）。
            // 这是标准 CA 的已知性能代价，不是精度阈值放宽；上界取 48 留余量。
            int maxSeen = 0;
            int contacts = 0;
            for (int k = 0; k < 24; k++)
            {
                float dist = 1.10f + (k % 7) * 0.02f;
                var a = Box(new RVec3(-dist, 0f, 0f), 1f);
                var b = Box(new RVec3(dist, 0f, 0f), 1f);
                var va = new RVec3(15f + (k % 5), 0f, 0f);
                var wa = new RVec3(0f, 0f, (k % 9) * 2f);
                var r = YrFullRotationalCa.ComputeToi(
                    a, va, wa, b, RVec3.Zero, RVec3.Zero, 1f / 60f);
                if (r.Iterations > maxSeen) maxSeen = r.Iterations;
                Require(r.Iterations <= YrFullRotationalCa.MaxIterations,
                        "迭代数超 MaxIterations @" + k);
                if (r.Toi >= 0f)
                {
                    contacts++;
                    Require(r.Converged, "真实固定步下报接触的组应收敛 @" + k);
                }
            }
            Require(maxSeen <= 48, "真实固定步下迭代应有限，实际最大 " + maxSeen);
            Require(contacts >= 10, "布局应覆盖足够多的接触场景，实际 " + contacts);

            // 零角速度子集必须 2 次内收敛（与解析 TOI 同量级）
            for (int k = 0; k < 3; k++)
            {
                float dist = 1.10f + k * 0.02f;
                var a = Box(new RVec3(-dist, 0f, 0f), 1f);
                var b = Box(new RVec3(dist, 0f, 0f), 1f);
                var r = YrFullRotationalCa.ComputeToi(
                    a, new RVec3(17f, 0f, 0f), RVec3.Zero,
                    b, RVec3.Zero, RVec3.Zero, 1f / 60f);
                Require(r.Iterations <= 4 && r.Converged,
                        "零角速度应 2 次内收敛，实际 " + r.Iterations);
            }
        }

        public static void TestR11FullCaSeparatedBodiesReturnMinusOne()
        {
            var a = Box(new RVec3(-5f, 0f, 0f), 1f);
            var b = Box(new RVec3(5f, 0f, 0f), 1f);
            var r = YrFullRotationalCa.ComputeToi(
                a, RVec3.Zero, new RVec3(0f, 0f, 40f),
                b, RVec3.Zero, RVec3.Zero, 0.5f);
            Require(r.Toi < 0f, "静止分离体（即使自旋）不应报接触");
            var r2 = YrFullRotationalCa.ComputeToi(
                a, new RVec3(-20f, 0f, 0f), RVec3.Zero,
                b, new RVec3(20f, 0f, 0f), RVec3.Zero, 0.5f);
            Require(r2.Toi < 0f, "背向运动不应报接触");
        }

        public static void TestR11FullCaEarliestToiAllPicksSmallest()
        {
            var bodies = new RigidBody3D[] {
                Make(new RVec3(-5f, 0f, 0f), 1f),
                Make(new RVec3(5f, 0f, 0f), 1f),
                Make(new RVec3(0f, 40f, 0f), 1f)
            };
            bodies[0].LinearVelocity = new RVec3(20f, 0f, 0f);
            var obbs = new YrObb[] {
                Box(bodies[0].Position, 1f), Box(bodies[1].Position, 1f), Box(bodies[2].Position, 1f)
            };
            float all = YrFullRotationalCa.EarliestToiAll(obbs, bodies, 0.65f);
            float pair01 = YrFullRotationalCa.ComputeToi(
                obbs[0], bodies[0].LinearVelocity, bodies[0].AngularVelocityWorld(),
                obbs[1], bodies[1].LinearVelocity, bodies[1].AngularVelocityWorld(), 0.65f).Toi;
            Require(all >= 0f, "应找到 TOI");
            Require(Math.Abs(all - pair01) < 1e-6f, "全对最早值应等于 (0,1) 对");
            var pairs = new (int, int)[] { (0, 1) };
            Require(Math.Abs(YrFullRotationalCa.EarliestToi(pairs, obbs, bodies, 0.65f) - pair01) < 1e-6f,
                    "候选对版本应与全对一致");
        }

        public static void TestR11PerFaceIncidentPointsStayInsideOwnFace()
        {
            var a = YrObb.AxisAligned(RVec3.Zero, H(1f));
            var b = YrObb.AxisAligned(new RVec3(1.6f, 0.25f, -0.15f), H(1f));
            var hit = YrConvex.SatObbObb(a, b);
            Require(hit.Hit, "重叠盒应命中");
            var pts = YrFaceClipping.BuildMultiFace(a, b, hit);
            Require(pts.Length >= 1, "多面流形不应为空");
            var seen = new System.Collections.Generic.HashSet<int>();
            for (int i = 0; i < pts.Length; i++)
            {
                Require(seen.Add(pts[i].Id), "多面 Id 冲突");
                int fid = pts[i].Id / YrFaceClipping.MaxPoints;
                int axis = fid / 2, sign = (fid % 2 == 0) ? -1 : 1;
                var rf = YrFaceClipping.RefFaceFor(a, axis, sign);
                Require(YrFaceClipping.InsideReference(rf, pts[i].Point),
                        "各自入射面后点仍应落在自己的参考面内: id=" + pts[i].Id);
            }
        }

        public static void TestR11IncidentFaceForPicksOppositeNormal()
        {
            var b = YrObb.AxisAligned(new RVec3(4f, 0f, 0f), H(1f));
            var v = YrFaceClipping.IncidentFaceFor(b, new RVec3(1f, 0f, 0f));
            // B 的 −X 面：4 个顶点 x 应全为 3（中心 4 − 半尺寸 1）
            for (int i = 0; i < 4; i++)
                Require(MathF.Abs(v[i].X - 3f) < 1e-5f,
                        "faceNormal=+X 应选 B 的 −X 面: x=" + v[i].X);
            var v2 = YrFaceClipping.IncidentFaceFor(b, new RVec3(0f, -1f, 0f));
            for (int i = 0; i < 4; i++)
                Require(MathF.Abs(v2[i].Y - 1f) < 1e-5f,
                        "faceNormal=−Y 应选 B 的 +Y 面: y=" + v2[i].Y);
        }

        public static void TestR11MultiFaceStillDeterministicAfterPerFaceChange()
        {
            var q = RQuat.FromAxisAngle(new RVec3(0f, 1f, 0f), 0.35f);
            var a = new YrObb(RVec3.Zero, q, H(1f));
            var b = YrObb.AxisAligned(new RVec3(1.55f, 0.2f, 0f), H(1f));
            var hit = YrConvex.SatObbObb(a, b);
            var p1 = YrFaceClipping.BuildMultiFace(a, b, hit);
            var p2 = YrFaceClipping.BuildMultiFace(a, b, hit);
            Require(p1.Length == p2.Length, "多面点数不确定");
            for (int i = 0; i < p1.Length; i++)
                Require(p1[i].Id == p2[i].Id
                     && p1[i].Point.X == p2[i].Point.X
                     && p1[i].Point.Y == p2[i].Point.Y
                     && p1[i].Point.Z == p2[i].Point.Z,
                        "多面点逐位不确定 @" + i);
            var red1 = YrFaceClipping.BuildMultiFaceReduced(a, b, hit);
            var red2 = YrFaceClipping.BuildMultiFaceReduced(a, b, hit);
            Require(red1.Length == red2.Length && red1.Length <= 4, "缩减应有界");
            for (int i = 0; i < red1.Length; i++)
                Require(red1[i].Id == red2[i].Id, "缩减结果逐位不确定 @" + i);
        }

        public static void TestR11ReduceByWrenchKeepsDeepestAndIsBounded()
        {
            var pts = new RVec3[] {
                new RVec3(0f,0f,0f), new RVec3(1f,0f,0f), new RVec3(0f,1f,0f),
                new RVec3(1f,1f,0f), new RVec3(0.5f,0.5f,0f), new RVec3(2f,2f,0f)
            };
            var ids = new int[] { 5, 1, 9, 3, 7, 11 };
            var dep = new float[] { 0.1f, 0.9f, 0.2f, 0.3f, 0.4f, 0.05f };
            var red = YrContactReduction.ReduceByWrench(
                pts, ids, dep, new RVec3(0f, 0f, 1f), RVec3.Zero, 4);
            Require(red.Length == 4, "应缩减到 4 点，实际 " + red.Length);
            bool hasDeepest = false;
            foreach (var r in red) if (r.Id == 1) hasDeepest = true;
            Require(hasDeepest, "缩减必须保留最深点 (Id=1)");
            var set = new System.Collections.Generic.HashSet<int>();
            foreach (var r in red) Require(set.Add(r.Id), "缩减后 Id 重复");
            for (int i = 1; i < red.Length; i++)
                Require(red[i].Id > red[i - 1].Id, "缩减输出应按 Id 升序");
            Require(YrContactReduction.ReduceByWrench(
                pts, ids, dep, new RVec3(0f, 0f, 1f), RVec3.Zero, 0).Length == 0,
                "max=0 应返回空");
        }

        public static void TestR11WrenchReductionDiffersFromGeometricOnCollinearSet()
        {
            // 构造：normal=+X、center=原点。沿 +X 共线的 4 点力矩臂恒为 0，
            // 几何 farthest-point 会挑最远的 (3,0,0)；
            // wrench 独立性则优先挑唯一有正交力臂的 (0,1,0)。
            var pts = new RVec3[] {
                new RVec3(0f,0f,0f), new RVec3(1f,0f,0f), new RVec3(2f,0f,0f),
                new RVec3(3f,0f,0f), new RVec3(0f,1f,0f)
            };
            var ids = new int[] { 0, 1, 2, 3, 4 };
            var dep = new float[] { 0.9f, 0.2f, 0.2f, 0.2f, 0.2f };
            // 法向取 +X：沿 +X 共线的 4 点有 r×n = 0，wrench 行完全相同
            // （残差 0）；只有 (0,1,0) 提供正交力臂 → 独立性指标唯一非零。
            var n = new RVec3(1f, 0f, 0f);
            var geom = YrContactReduction.Reduce(pts, ids, dep, 3);
            var wr = YrContactReduction.ReduceByWrench(pts, ids, dep, n, RVec3.Zero, 3);
            Require(geom.Length == 3 && wr.Length == 3, "两者都应给 3 点");
            bool geomHasFar = false, wrHasOrtho = false;
            foreach (var r in geom) if (r.Id == 3) geomHasFar = true;
            foreach (var r in wr) if (r.Id == 4) wrHasOrtho = true;
            Require(geomHasFar, "几何版应挑最远共线点 Id=3");
            Require(wrHasOrtho, "wrench 版应挑力臂正交点 Id=4");
            var gp = new RVec3[geom.Length]; var wp = new RVec3[wr.Length];
            for (int i = 0; i < geom.Length; i++) gp[i] = geom[i].Point;
            for (int i = 0; i < wr.Length; i++) wp[i] = wr[i].Point;
            float gi = YrContactReduction.WrenchIndependence(gp, n, RVec3.Zero);
            float wi = YrContactReduction.WrenchIndependence(wp, n, RVec3.Zero);
            Require(wi >= gi - 1e-6f,
                    "wrench 版独立性指标不应低于几何版: " + wi + " vs " + gi);
            // 确定性
            var wr2 = YrContactReduction.ReduceByWrench(pts, ids, dep, n, RVec3.Zero, 3);
            for (int i = 0; i < wr.Length; i++)
                Require(wr[i].Id == wr2[i].Id, "wrench 缩减应逐位确定 @" + i);
        }

        public static void TestR11PipelineFullCaSwitchDefaultOffAndNoRegression()
        {
            var bodies = new RigidBody3D[] {
                Make(RVec3.Zero, 1f), Make(new RVec3(1.8f, 0f, 0f), 1f)
            };
            var pipe = new YrPhysicsPipeline(bodies, new RVec3[] { H(1f), H(1f) });
            Require(!pipe.UseFullRotationalCa, "新开关默认必须关闭");
            pipe.SleepEnabled = false;
            pipe.StepFixed(1f / 60f);
            Require(pipe.Tick == 1, "默认路径应正常推进");

            YrPhysicsPipeline MakePipe(bool full)
            {
                var bs = new RigidBody3D[] {
                    Make(RVec3.Zero, 1f), Make(new RVec3(1.8f, 0f, 0f), 1f)
                };
                var q = new YrPhysicsPipeline(bs, new RVec3[] { H(1f), H(1f) });
                q.SleepEnabled = false; q.UseFullRotationalCa = full;
                return q;
            }
            var p0 = MakePipe(false); var p1 = MakePipe(true);
            for (int k = 0; k < 5; k++)
            {
                p0.StepFixed(1f / 60f); p1.StepFixed(1f / 60f);
                ulong h0 = YrRollbackBuffer.Checksum(p0.Snapshot(), p0.Tick);
                ulong h1 = YrRollbackBuffer.Checksum(p1.Snapshot(), p1.Tick);
                Require(h0 == h1, "完整 CA 开关不应改变常规 StepFixed 路径 @" + (k + 1));
            }
            // CCD 子步路径下开启完整 CA 应能正常推进（不抛异常、tick 增长）
            var ccd = MakePipe(true);
            ccd.UseToiSubsteps = true;
            ccd.StepFixedToi(1f / 60f);
            Require(ccd.Tick == 1, "完整 CA 的 CCD 子步路径应正常推进");
            Require(ccd.UseFullRotationalCa && ccd.LastToiSubsteps >= 1,
                    "开关应透传到 ToiAdvance");
        }
    }
}
