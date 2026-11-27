using System;
using YamaEngine.Reality;

namespace YamaEngine.Tests
{
    /// <summary>
    /// 第十轮回归（14 项）：精确 CA、多面接触流形、接触缩减、管线开关。
    /// 全部进入现有 build.yml 的 Test step 真实执行。
    /// </summary>
    public static class RealityPhysicsTenthTests
    {
        private static void Require(bool ok, string msg)
        {
            if (!ok) throw new InvalidOperationException("REALITY_R10_FAIL: " + msg);
        }
        private static RVec3 H(float v) => new RVec3(v, v, v);
        private static YrObb Box(RVec3 c, float h) => YrObb.AxisAligned(c, H(h));

        private static RigidBody3D Make(RVec3 pos, float mass)
        {
            var b = new RigidBody3D { Position = pos, Mass = mass };
            b.SetInertia(RigidBody3D.SphereInertia(mass, 0.5f));
            return b;
        }

        public static void TestR10CaZeroSpinApproachesAnalyticToi()
        {
            var a = Box(new RVec3(-5f, 0f, 0f), 1f);
            var b = Box(new RVec3(5f, 0f, 0f), 1f);
            var va = new RVec3(20f, 0f, 0f);
            float dt = 0.65f;
            float analytic = YrSweptObb.ComputeToi(a, va, b, RVec3.Zero, dt);
            var ca = YrConservativeAdvancement.ComputeToi(
                a, va, RVec3.Zero, b, RVec3.Zero, RVec3.Zero, dt);
            Require(analytic >= 0f, "解析 TOI 应存在");
            Require(ca.Toi >= 0f, "CA 应报出 TOI");
            // CA 是保守下界推进：不得晚于解析（漏报），且应收敛到高精度
            Require(ca.Toi <= analytic + 1e-3f,
                    "CA 不得晚报（漏报）: ca=" + ca.Toi + " analytic=" + analytic);
            Require(Math.Abs(ca.Toi - analytic) < 2e-3f,
                    "零角速度下 CA 应逼近解析 TOI: diff=" + Math.Abs(ca.Toi - analytic));
            Require(ca.Converged, "常见场景 CA 应收敛");
        }

        public static void TestR10CaVersusInflationAcrossSpinRegimes()
        {
            var a = Box(new RVec3(-5f, 0f, 0f), 1f);
            var b = Box(new RVec3(5f, 0f, 0f), 1f);
            var va = new RVec3(20f, 0f, 0f);
            float dt = 0.65f;

            // 低角速：第七轮的 min(1, |ω|·dt·√2) 未饱和，整帧膨胀是有效上界，
            // CA 用局部 t 更精确 → 不应比它更早报。
            var waSlow = new RVec3(0f, 0f, 0.5f);
            float inflatedSlow = YrSweptObb.ComputeToiWithAngular(
                a, va, waSlow, b, RVec3.Zero, RVec3.Zero, dt);
            float caSlow = YrConservativeAdvancement.ComputeToi(
                a, va, waSlow, b, RVec3.Zero, RVec3.Zero, dt).Toi;
            Require(inflatedSlow >= 0f && caSlow >= 0f, "低角速两者都应报 TOI");
            Require(caSlow >= inflatedSlow - 1e-5f,
                    "未饱和区 CA 不应早报（更精确）: ca=" + caSlow + " inflated=" + inflatedSlow);

            // 高角速：第七轮的 min(1,...) 把膨胀**饱和**在 R，严重低估真实旋转上界
            // R·|ω|·dt（实测 1.732 vs 13.5），于是会漏报。CA 无饱和、用完整
            // spin·t 上界，因此更早报 —— 这是修复漏报，不是退化。
            var waFast = new RVec3(0f, 0f, 12f);
            float inflatedFast = YrSweptObb.ComputeToiWithAngular(
                a, va, waFast, b, RVec3.Zero, RVec3.Zero, dt);
            float caFast = YrConservativeAdvancement.ComputeToi(
                a, va, waFast, b, RVec3.Zero, RVec3.Zero, dt).Toi;
            Require(inflatedFast >= 0f && caFast >= 0f, "高角速两者都应报 TOI");
            Require(caFast <= inflatedFast + 1e-5f,
                    "饱和区 CA 应更早报（不漏报）: ca=" + caFast + " inflated=" + inflatedFast);
            // 数值证据：饱和确实把上界截断了
            float trueBound = a.HalfExtents.Length() * 12f * dt;
            float satur = YrSweptObb.AngularInflation(12f, a.HalfExtents.Length(), dt);
            Require(trueBound > satur * 4f,
                    "第七轮饱和应明显低估真实旋转上界: bound=" + trueBound + " satur=" + satur);
        }

        public static void TestR10CaSeparatedBodiesReturnMinusOne()
        {
            var a = Box(new RVec3(-5f, 0f, 0f), 1f);
            var b = Box(new RVec3(5f, 0f, 0f), 1f);
            var ca = YrConservativeAdvancement.ComputeToi(
                a, RVec3.Zero, RVec3.Zero, b, RVec3.Zero, RVec3.Zero, 0.5f);
            Require(ca.Toi < 0f, "静止分离体不应报接触");
            // 背向运动也不应在 dt 内接触
            var ca2 = YrConservativeAdvancement.ComputeToi(
                a, new RVec3(-20f, 0f, 0f), RVec3.Zero,
                b, new RVec3(20f, 0f, 0f), RVec3.Zero, 0.5f);
            Require(ca2.Toi < 0f, "背向运动不应报接触");
        }

        public static void TestR10CaIsDeterministic()
        {
            var a = Box(new RVec3(-5f, 0f, 0f), 1f);
            var b = Box(new RVec3(5f, 0f, 0f), 1f);
            var va = new RVec3(17f, 2f, -1f);
            var wa = new RVec3(0f, 3f, 7f);
            var x = YrConservativeAdvancement.ComputeToi(a, va, wa, b, RVec3.Zero, RVec3.Zero, 0.7f);
            var y = YrConservativeAdvancement.ComputeToi(a, va, wa, b, RVec3.Zero, RVec3.Zero, 0.7f);
            Require(x.Toi == y.Toi && x.Iterations == y.Iterations
                 && x.Converged == y.Converged && x.Residual == y.Residual,
                    "CA 结果必须逐位确定");
        }

        public static void TestR10CaIterationCountIsBounded()
        {
            int maxSeen = 0;
            for (int k = 0; k < 24; k++)
            {
                float dist = 4f + (k % 7) * 0.7f;
                var a = Box(new RVec3(-dist, 0f, 0f), 1f);
                var b = Box(new RVec3(dist, 0f, 0f), 1f);
                var va = new RVec3(15f + (k % 5), 0f, 0f);
                var wa = new RVec3(0f, 0f, (k % 9) * 2f);
                var r = YrConservativeAdvancement.ComputeToi(
                    a, va, wa, b, RVec3.Zero, RVec3.Zero, 1.2f);
                if (r.Iterations > maxSeen) maxSeen = r.Iterations;
                Require(r.Iterations <= YrConservativeAdvancement.MaxIterations,
                        "迭代数超上界");
            }
            Require(maxSeen <= 12, "线性化 CA 应快速收敛，实际最大 " + maxSeen);
        }

        public static void TestR10CaMonotonicInAngularSpeed()
        {
            var a = Box(new RVec3(-5f, 0f, 0f), 1f);
            var b = Box(new RVec3(5f, 0f, 0f), 1f);
            var va = new RVec3(20f, 0f, 0f);
            float slow = YrConservativeAdvancement.ComputeToi(
                a, va, new RVec3(0f, 0f, 1f), b, RVec3.Zero, RVec3.Zero, 0.65f).Toi;
            float fast = YrConservativeAdvancement.ComputeToi(
                a, va, new RVec3(0f, 0f, 30f), b, RVec3.Zero, RVec3.Zero, 0.65f).Toi;
            Require(slow >= 0f && fast >= 0f, "两种角速度都应报 TOI");
            // 角速度上界越大 → 越早进入接触（保守单调），与第七轮同向
            Require(fast <= slow + 1e-5f, "角速度单调性被破坏");
        }

        public static void TestR10CaEarliestToiAllPicksSmallest()
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
            float all = YrConservativeAdvancement.EarliestToiAll(obbs, bodies, 0.65f);
            float pair01 = YrConservativeAdvancement.ComputeToi(
                obbs[0], bodies[0].LinearVelocity, bodies[0].AngularVelocityWorld(),
                obbs[1], bodies[1].LinearVelocity, bodies[1].AngularVelocityWorld(), 0.65f).Toi;
            Require(all >= 0f, "应找到 TOI");
            Require(Math.Abs(all - pair01) < 1e-6f, "全对最早值应等于 (0,1) 对");
            var pairs = new (int, int)[] { (0, 1) };
            Require(Math.Abs(YrConservativeAdvancement.EarliestToi(pairs, obbs, bodies, 0.65f) - pair01) < 1e-6f,
                    "候选对版本应与全对一致");
        }

        public static void TestR10MultiFaceIdsAreUniqueAndBounded()
        {
            var a = YrObb.AxisAligned(RVec3.Zero, H(1f));
            var b = YrObb.AxisAligned(new RVec3(1.7f, 0.3f, 0.2f), H(1f));
            var hit = YrConvex.SatObbObb(a, b);
            Require(hit.Hit, "重叠盒应命中");
            var pts = YrFaceClipping.BuildMultiFace(a, b, hit);
            Require(pts.Length >= 1, "多面流形不应为空");
            var seen = new System.Collections.Generic.HashSet<int>();
            for (int i = 0; i < pts.Length; i++)
            {
                Require(pts[i].Id >= 0 && pts[i].Id < 24,
                        "Id 应落在 faceId*4+localId (0..23): " + pts[i].Id);
                Require(seen.Add(pts[i].Id), "多面 Id 冲突: " + pts[i].Id);
            }
            for (int i = 1; i < pts.Length; i++)
                Require(pts[i].Id > pts[i - 1].Id, "多面输出应按 Id 升序");
        }

        public static void TestR10MultiFacePointsInsideOwnReferenceFace()
        {
            var a = YrObb.AxisAligned(RVec3.Zero, H(1f));
            var b = YrObb.AxisAligned(new RVec3(1.6f, 0.25f, -0.15f), H(1f));
            var hit = YrConvex.SatObbObb(a, b);
            var pts = YrFaceClipping.BuildMultiFace(a, b, hit);
            for (int i = 0; i < pts.Length; i++)
            {
                int fid = pts[i].Id / YrFaceClipping.MaxPoints;
                int axis = fid / 2, sign = (fid % 2 == 0) ? -1 : 1;
                var rf = YrFaceClipping.RefFaceFor(a, axis, sign);
                Require(YrFaceClipping.InsideReference(rf, pts[i].Point),
                        "点不在自己的参考面内: id=" + pts[i].Id);
            }
        }

        public static void TestR10MultiFaceIsDeterministic()
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
            Require(YrFaceClipping.BuildMultiFace(a, b, YrSatHit.Miss).Length == 0,
                    "无命中不应生成流形点");
        }

        public static void TestR10MultiFaceReducedIsBounded()
        {
            var a = YrObb.AxisAligned(RVec3.Zero, H(1f));
            var b = YrObb.AxisAligned(new RVec3(1.5f, 0.4f, 0.3f), H(1f));
            var hit = YrConvex.SatObbObb(a, b);
            var red = YrFaceClipping.BuildMultiFaceReduced(a, b, hit);
            Require(red.Length >= 1 && red.Length <= 4,
                    "缩减后点数应在 1..4: " + red.Length);
            var seen = new System.Collections.Generic.HashSet<int>();
            for (int i = 0; i < red.Length; i++)
                Require(seen.Add(red[i].Id), "缩减后 Id 不应重复");
        }

        public static void TestR10ContactReductionKeepsDeepestAndIsBounded()
        {
            var pts = new RVec3[] {
                new RVec3(0f,0f,0f), new RVec3(1f,0f,0f), new RVec3(0f,1f,0f),
                new RVec3(1f,1f,0f), new RVec3(0.5f,0.5f,0f), new RVec3(2f,2f,0f)
            };
            var ids = new int[] { 5, 1, 9, 3, 7, 11 };
            var dep = new float[] { 0.1f, 0.9f, 0.2f, 0.3f, 0.4f, 0.05f };
            var red = YrContactReduction.Reduce(pts, ids, dep, 4);
            Require(red.Length == 4, "应缩减到 4 点，实际 " + red.Length);
            bool hasDeepest = false;
            foreach (var r in red) if (r.Id == 1) hasDeepest = true;
            Require(hasDeepest, "缩减必须保留最深点 (Id=1, depth=0.9)");
            var set = new System.Collections.Generic.HashSet<int>();
            foreach (var r in red) Require(set.Add(r.Id), "缩减后 Id 重复");
            Require(YrContactReduction.Reduce(pts, ids, dep, 0).Length == 0,
                    "max=0 应返回空");
        }

        public static void TestR10ContactReductionDeterministicAndCovers()
        {
            var pts = new RVec3[] {
                new RVec3(0f,0f,0f), new RVec3(2f,0f,0f), new RVec3(0f,2f,0f),
                new RVec3(2f,2f,0f), new RVec3(1f,1f,0f)
            };
            var ids = new int[] { 0, 1, 2, 3, 4 };
            var dep = new float[] { 0.5f, 0.5f, 0.5f, 0.5f, 0.9f };
            var a = YrContactReduction.Reduce(pts, ids, dep, 4);
            var b = YrContactReduction.Reduce(pts, ids, dep, 4);
            Require(a.Length == b.Length, "缩减点数不确定");
            for (int i = 0; i < a.Length; i++)
                Require(a[i].Id == b[i].Id && a[i].Depth == b[i].Depth,
                        "缩减结果逐位不确定 @" + i);
            var pa = new RVec3[a.Length];
            for (int i = 0; i < a.Length; i++) pa[i] = a[i].Point;
            Require(YrContactReduction.Spread(pa) > 0.5f,
                    "farthest-point 采样应保留几何覆盖");
            // 输出按 Id 排序
            for (int i = 1; i < a.Length; i++)
                Require(a[i].Id > a[i - 1].Id, "缩减输出应按 Id 升序");
        }

        public static void TestR10PipelineNewSwitchesDefaultOffAndZeroRegression()
        {
            var bodies = new RigidBody3D[] {
                Make(RVec3.Zero, 1f), Make(new RVec3(1.8f, 0f, 0f), 1f)
            };
            var pipe = new YrPhysicsPipeline(bodies, new RVec3[] { H(1f), H(1f) });
            Require(!pipe.UseExactCa && !pipe.UseMultiFace,
                    "两个新开关默认必须关闭");
            pipe.SleepEnabled = false;
            pipe.StepFixed(1f / 60f);
            Require(pipe.Tick == 1, "默认路径应正常推进");
            Require(pipe.PointWarmCacheCount == 0, "默认路径不应写逐点缓存");

            // UseExactCa 只作用于 StepFixedToi（CCD 子步路径），常规 StepFixed
            // 不受它影响 → 开启后必须与默认路径逐位一致。
            YrPhysicsPipeline MakePipe(bool ca, bool multi)
            {
                var bs = new RigidBody3D[] {
                    Make(RVec3.Zero, 1f), Make(new RVec3(1.8f, 0f, 0f), 1f)
                };
                var q = new YrPhysicsPipeline(bs, new RVec3[] { H(1f), H(1f) });
                q.SleepEnabled = false; q.UseExactCa = ca; q.UseMultiFace = multi;
                return q;
            }
            var p0 = MakePipe(false, false); var pCa = MakePipe(true, false);
            for (int k = 0; k < 5; k++)
            {
                p0.StepFixed(1f / 60f); pCa.StepFixed(1f / 60f);
                ulong h0 = YrRollbackBuffer.Checksum(p0.Snapshot(), p0.Tick);
                ulong h1 = YrRollbackBuffer.Checksum(pCa.Snapshot(), pCa.Tick);
                Require(h0 == h1, "UseExactCa 不应改变常规路径 @" + (k + 1));
            }
            Require(pCa.UseExactCa && !pCa.UseMultiFace, "CA 开关应透传且不与多面耦合");

            // UseMultiFace 会改变常规窄相的接触点集（多面 vs 顶点近似），
            // 因此**预期**与默认路径分叉；该验的是它自身仍逐位确定、
            // 且快照回滚重放一致，而不是「与默认相同」。
            var m1 = MakePipe(false, true); var m2 = MakePipe(false, true);
            var mh = new ulong[6];
            mh[0] = YrRollbackBuffer.Checksum(m1.Snapshot(), 0);
            m1.SaveFrame();
            for (int k = 0; k < 5; k++)
            {
                m1.StepFixed(1f / 60f); m2.StepFixed(1f / 60f);
                m1.SaveFrame();
                mh[k + 1] = YrRollbackBuffer.Checksum(m1.Snapshot(), m1.Tick);
                ulong h2 = YrRollbackBuffer.Checksum(m2.Snapshot(), m2.Tick);
                Require(mh[k + 1] == h2, "多面路径逐位不确定 @" + (k + 1));
            }
            Require(m1.RewindTo(2), "多面路径应能回滚");
            for (int k = 2; k < 5; k++)
            {
                m1.StepFixed(1f / 60f);
                ulong h = YrRollbackBuffer.Checksum(m1.Snapshot(), m1.Tick);
                Require(h == mh[k + 1], "多面路径回滚重放分叉 @" + (k + 1));
            }
            Require(m1.UseMultiFace && !m1.UseExactCa, "多面开关应透传且不与 CA 耦合");
        }
    }
}
