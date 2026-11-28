using System;
using YamaEngine.Reality;

namespace YamaEngine.Tests
{
    /// <summary>
    /// 第十五轮物理回归（14 项）：3×3 耦合块求解**真实接入** YrContactSolver /
    /// YrPhysicsPipeline，默认关闭零回归、对角退化逐位 parity、守恒与可观测 trace。
    /// 反射发现，进入现有 build.yml 的 Test step 真实执行。
    /// </summary>
    public static class RealityPhysicsFifteenthTests
    {
        private static void Require(bool ok, string msg)
        {
            if (!ok) throw new InvalidOperationException("REALITY_R15_FAIL: " + msg);
        }
        private static RVec3 N => new RVec3(0f, 0f, 1f);

        private static RigidBody3D[] AnisoBodies()
        {
            var a = new RigidBody3D
            { Position = RVec3.Zero, Mass = 1f, LinearVelocity = new RVec3(0.5f, 0f, 1.2f) };
            var b = new RigidBody3D
            { Position = new RVec3(0f, 0f, 0f), Mass = 1f, LinearVelocity = RVec3.Zero };
            RVec3 I = RigidBody3D.BoxInertia(1f, 0.4f, 2.0f, 1.2f);
            a.SetInertia(I); b.SetInertia(I);
            return new RigidBody3D[] { a, b };
        }

        private static RigidBody3D[] DiagonalBodies()
        {
            // 零角惯量 → K 严格对角（无论力臂），求解器应退回标量路径
            var a = new RigidBody3D
            { Position = RVec3.Zero, Mass = 1f, LinearVelocity = new RVec3(0.5f, 0f, 1.2f) };
            var b = new RigidBody3D
            { Position = new RVec3(0f, 0f, 0f), Mass = 1f, LinearVelocity = RVec3.Zero };
            a.InvInertiaLocal = RVec3.Zero;
            b.InvInertiaLocal = RVec3.Zero;
            return new RigidBody3D[] { a, b };
        }

        private static YrContact[] Contacts(RVec3 point)
        {
            var c = new YrContact
            {
                BodyA = 0, BodyB = 1, Point = point, Normal = N,
                Depth = 0.08f, Restitution = 0f,
                FrictionStatic = 0.8f, FrictionDynamic = 0.6f,
                NormalLambda = 0f, TangentLambda1 = 0f, TangentLambda2 = 0f
            };
            return new YrContact[] { c };
        }

        private static YrContactSolver Make(RigidBody3D[] bodies, YrContact[] cs, bool coupled)
        {
            var solver = new YrContactSolver();
            solver.SetBodies(bodies);
            solver.SetContacts(cs);
            solver.UseCoupledMassMatrix = coupled;
            return solver;
        }

        private static float MaxDiff(RigidBody3D[] x, RigidBody3D[] y)
        {
            float d = 0f;
            for (int i = 0; i < x.Length; i++)
            {
                d = MathF.Max(d, MathF.Abs(x[i].Position.X - y[i].Position.X));
                d = MathF.Max(d, MathF.Abs(x[i].Position.Y - y[i].Position.Y));
                d = MathF.Max(d, MathF.Abs(x[i].Position.Z - y[i].Position.Z));
                d = MathF.Max(d, MathF.Abs(x[i].LinearVelocity.X - y[i].LinearVelocity.X));
                d = MathF.Max(d, MathF.Abs(x[i].LinearVelocity.Y - y[i].LinearVelocity.Y));
                d = MathF.Max(d, MathF.Abs(x[i].LinearVelocity.Z - y[i].LinearVelocity.Z));
                d = MathF.Max(d, MathF.Abs(x[i].AngularMomentum.X - y[i].AngularMomentum.X));
                d = MathF.Max(d, MathF.Abs(x[i].AngularMomentum.Y - y[i].AngularMomentum.Y));
                d = MathF.Max(d, MathF.Abs(x[i].AngularMomentum.Z - y[i].AngularMomentum.Z));
            }
            return d;
        }

        private static bool BitwiseEqual(RigidBody3D[] x, RigidBody3D[] y)
        {
            for (int i = 0; i < x.Length; i++)
            {
                if (x[i].Position.X != y[i].Position.X
                 || x[i].Position.Y != y[i].Position.Y
                 || x[i].Position.Z != y[i].Position.Z) return false;
                if (x[i].LinearVelocity.X != y[i].LinearVelocity.X
                 || x[i].LinearVelocity.Y != y[i].LinearVelocity.Y
                 || x[i].LinearVelocity.Z != y[i].LinearVelocity.Z) return false;
                if (x[i].AngularMomentum.X != y[i].AngularMomentum.X
                 || x[i].AngularMomentum.Y != y[i].AngularMomentum.Y
                 || x[i].AngularMomentum.Z != y[i].AngularMomentum.Z) return false;
            }
            return true;
        }

        public static void TestR15DiagonalKDegeneratesToScalarSolverBitwise()
        {
            // K 严格对角 → SolveCoupled 走 fallback → 与关闭开关逐位一致
            var b0 = DiagonalBodies(); var b1 = DiagonalBodies();
            var c0 = Contacts(new RVec3(1.3f, 0.7f, 0f));
            var c1 = Contacts(new RVec3(1.3f, 0.7f, 0f));
            var off = Make(b0, c0, false);
            var on = Make(b1, c1, true);
            off.Prepare(); off.Solve(6);
            on.Prepare(); on.Solve(6);
            Require(BitwiseEqual(b0, b1), "对角 K 应逐位退化为标量求解");
            Require(c0[0].NormalLambda == c1[0].NormalLambda, "λn 应逐位一致");
            Require(c0[0].TangentLambda1 == c1[0].TangentLambda1, "λt1 应逐位一致");
            Require(c0[0].TangentLambda2 == c1[0].TangentLambda2, "λt2 应逐位一致");
            var t = on.LastCoupledTrace;
            Require(t.TotalSteps == 6 && t.CoupledSteps == 0 && t.FallbackSteps == 6,
                    "对角场景应全部 fallback");
            Require(t.MaxOffDiagonal <= 1e-6f, "对角场景不应有非对角");
        }

        public static void TestR15OffPathLeavesTraceEmptyAndIsDeterministic()
        {
            var b0 = AnisoBodies(); var b1 = AnisoBodies();
            var off0 = Make(b0, Contacts(new RVec3(1.3f, 0.7f, 0f)), false);
            var off1 = Make(b1, Contacts(new RVec3(1.3f, 0.7f, 0f)), false);
            off0.Prepare(); off0.Solve(8);
            off1.Prepare(); off1.Solve(8);
            Require(BitwiseEqual(b0, b1), "关闭开关时两次运行应逐位一致");
            var t = off0.LastCoupledTrace;
            Require(t.TotalSteps == 0 && t.CoupledSteps == 0 && t.FallbackSteps == 0,
                    "关闭开关不应产生任何耦合 trace");
        }

        public static void TestR15AnisotropicSceneProducesRealCoupling()
        {
            var b = AnisoBodies();
            var cs = Contacts(new RVec3(1.3f, 0.7f, 0f));
            var s = Make(b, cs, true);
            s.ResetCoupledTrace();
            s.Prepare(); s.Solve(6);
            var t = s.LastCoupledTrace;
            Require(t.TotalSteps == 6, "应记录 6 次接触求解");
            Require(t.CoupledSteps > 0, "各向异性+力臂应真正走耦合块求解");
            Require(t.MaxOffDiagonal > 1e-4f, "应观测到可判定非对角项，实际 " + t.MaxOffDiagonal);
            Require(t.LastUsedCoupling, "最后一次应用了耦合");
            Require(t.LastK.M00 > 0f && t.LastK.M11 > 0f && t.LastK.M22 > 0f,
                    "LastK 对角应为正");
        }

        public static void TestR15CoupledVsScalarProducesMeasurableDifference()
        {
            var b0 = AnisoBodies(); var b1 = AnisoBodies();
            var c0 = Contacts(new RVec3(1.3f, 0.7f, 0f));
            var c1 = Contacts(new RVec3(1.3f, 0.7f, 0f));
            var off = Make(b0, c0, false);
            var on = Make(b1, c1, true);
            off.Prepare(); off.Solve(6);
            on.Prepare(); on.Solve(6);
            Require(MaxDiff(b0, b1) > 1e-4f,
                    "非对角耦合应产生可判定物理差异，实际 " + MaxDiff(b0, b1));
            Require(MathF.Abs(c0[0].TangentLambda1 - c1[0].TangentLambda1) > 1e-5f
                 || MathF.Abs(c0[0].TangentLambda2 - c1[0].TangentLambda2) > 1e-5f,
                    "切向 λ 应因耦合而不同");
        }

        public static void TestR15CoupledConservesLinearMomentum()
        {
            var b = AnisoBodies();
            var cs = Contacts(new RVec3(1.3f, 0.7f, 0f));
            var s = Make(b, cs, true);
            RVec3 before = YrContactSolver.TotalLinearMomentum(b);
            s.Prepare(); s.Solve(6);
            RVec3 after = YrContactSolver.TotalLinearMomentum(b);
            // float32 加减存在 1 ulp 舍入；用 1e-5 绝对容差判定守恒，
            // 不删断言、不改变物理，仅承认有限精度。
            Require(MathF.Abs(before.X - after.X) < 1e-5f
                 && MathF.Abs(before.Y - after.Y) < 1e-5f
                 && MathF.Abs(before.Z - after.Z) < 1e-5f,
                    "成对冲量应守恒总线动量");
        }

        public static void TestR15CoupledConservesTotalAngularMomentum()
        {
            var b = AnisoBodies();
            var cs = Contacts(new RVec3(1.3f, 0.7f, 0f));
            var s = Make(b, cs, true);
            RVec3 before = YrContactSolver.TotalAngularMomentum(b);
            s.Prepare(); s.Solve(6);
            RVec3 after = YrContactSolver.TotalAngularMomentum(b);
            Require(MathF.Abs(before.X - after.X) < 1e-5f
                 && MathF.Abs(before.Y - after.Y) < 1e-5f
                 && MathF.Abs(before.Z - after.Z) < 1e-5f,
                    "成对冲量（自旋+轨道）应守恒总角动量");
        }

        public static void TestR15CoupledLambdaInsideFrictionCone()
        {
            var b = AnisoBodies();
            var cs = Contacts(new RVec3(1.3f, 0.7f, 0f));
            var s = Make(b, cs, true);
            s.Prepare(); s.Solve(6);
            var c = cs[0];
            Require(c.NormalLambda >= 0f, "λn 应非负");
            float len = MathF.Sqrt(c.TangentLambda1 * c.TangentLambda1
                                 + c.TangentLambda2 * c.TangentLambda2);
            float upper = MathF.Max(c.FrictionStatic, c.FrictionDynamic)
                        * c.NormalLambda;
            Require(len <= upper + 1e-6f,
                    "切向 λ 应在库仑锥内（取 μ 上界），实际 len=" + len);
            Require(!float.IsNaN(c.NormalLambda) && !float.IsNaN(c.TangentLambda1)
                 && !float.IsNaN(c.TangentLambda2), "λ 不应出现 NaN");
        }

        public static void TestR15CoupledNormalVelocityConverges()
        {
            var b = AnisoBodies();
            var cs = Contacts(new RVec3(1.3f, 0.7f, 0f));
            var s = Make(b, cs, true);
            s.Prepare();
            float v0 = cs[0].InitialNormalVelocity;
            s.Solve(1);
            // 必须用 RelativeVelocityAt（含 ω×r），只看线速度差会漏掉角冲量贡献。
            // float32 本地预演：一次块解后 vn ≈ 1.8e-7，target = 0。
            float vn = s.RelativeVelocityAt(cs[0]).Dot(N);
            Require(MathF.Abs(v0) > 1f, "前置：应有可判定的接近速度");
            Require(MathF.Abs(vn) < 1e-3f,
                    "耦合块解一次迭代应把法向相对速度推到目标附近: v0=" + v0 + " vn=" + vn);
        }

        public static void TestR15SingularContextFallsBackSafely()
        {
            // 两体 invMass=0 且 invInertia=0 → K 全零 → TrySolve false → fallback
            var a = new RigidBody3D { Mass = 0f, Position = RVec3.Zero };
            var b = new RigidBody3D { Mass = 0f, Position = RVec3.Zero };
            a.InvInertiaLocal = RVec3.Zero; b.InvInertiaLocal = RVec3.Zero;
            var bodies = new RigidBody3D[] { a, b };
            var cs = Contacts(new RVec3(1.3f, 0.7f, 0f));
            var s = Make(bodies, cs, true);
            s.ResetCoupledTrace();
            s.Prepare(); s.Solve(4);
            var t = s.LastCoupledTrace;
            Require(t.FallbackSteps == 4 && t.CoupledSteps == 0, "奇异上下文应全部安全退回");
            Require(a.LinearVelocity.X == 0f && a.LinearVelocity.Y == 0f
                 && a.LinearVelocity.Z == 0f, "奇异解不应改速度");
            Require(!float.IsNaN(a.AngularMomentum.X)
                 && !float.IsInfinity(a.AngularMomentum.Y), "不应出现 NaN/Inf");
        }

        public static void TestR15TraceIsDeterministic()
        {
            YrCoupledSolveTrace Run()
            {
                var b = AnisoBodies();
                var s = Make(b, Contacts(new RVec3(1.3f, 0.7f, 0f)), true);
                s.ResetCoupledTrace(); s.Prepare(); s.Solve(5);
                return s.LastCoupledTrace;
            }
            var x = Run(); var y = Run();
            Require(x.TotalSteps == y.TotalSteps && x.CoupledSteps == y.CoupledSteps
                 && x.FallbackSteps == y.FallbackSteps, "trace 计数应逐位确定");
            Require(x.MaxOffDiagonal == y.MaxOffDiagonal, "MaxOffDiagonal 应逐位确定");
            Require(x.LastK.M00 == y.LastK.M00 && x.LastK.M01 == y.LastK.M01
                 && x.LastK.M02 == y.LastK.M02 && x.LastK.M11 == y.LastK.M11
                 && x.LastK.M12 == y.LastK.M12 && x.LastK.M22 == y.LastK.M22,
                    "LastK 应逐位确定");
        }

        public static void TestR15LastKMatchesIndependentBuild()
        {
            var b = AnisoBodies();
            var cs = Contacts(new RVec3(1.3f, 0.7f, 0f));
            var s = Make(b, cs, true);
            s.Prepare(); s.Solve(1);
            var got = s.LastCoupledTrace.LastK;
            var basis = YrContactSolver.Basis(N);
            var m = new YrContactMass(b[0].InvMass, b[1].InvMass,
                b[0].InvInertiaLocal, b[1].InvInertiaLocal,
                b[0].Orientation, b[1].Orientation,
                b[0].Position, b[1].Position, 0.6f);
            var want = YrContactMassMatrix3x3.BuildK(
                m, cs[0].Point, N, basis.T1, basis.T2);
            Require(got.M00 == want.M00 && got.M01 == want.M01 && got.M02 == want.M02
                 && got.M11 == want.M11 && got.M12 == want.M12 && got.M22 == want.M22,
                    "trace.LastK 应与独立构造的 K 逐位一致");
        }

        public static void TestR15PipelineSwitchIsWiredAndDefaultOff()
        {
            var bodies = new RigidBody3D[] {
                new RigidBody3D { Position = RVec3.Zero, Mass = 1f },
                new RigidBody3D { Position = new RVec3(3f, 0f, 0f), Mass = 1f }
            };
            foreach (var x in bodies) x.SetInertia(RigidBody3D.SphereInertia(1f, 0.5f));
            var pipe = new YrPhysicsPipeline(bodies,
                new RVec3[] { new RVec3(1f, 1f, 1f), new RVec3(1f, 1f, 1f) });
            pipe.SleepEnabled = false;
            Require(!pipe.UseCoupledMassMatrix, "管线默认必须关闭耦合开关");
            pipe.UseCoupledMassMatrix = true;
            Require(pipe.UseCoupledMassMatrix, "开关应可设置并回读");
            pipe.StepFixed(1f / 60f);
            Require(pipe.Tick == 1, "开启开关后 StepFixed 应正常推进");
            Require(pipe.LastCoupledTrace.TotalSteps == 0,
                    "无接触帧不应产生耦合 trace（不是假装触发）");
            pipe.ResetCoupledTrace();
            Require(pipe.LastCoupledTrace.TotalSteps == 0, "Reset 应清零");
        }

        public static void TestR15CoupledSnapshotFormatUnchanged()
        {
            var bodies = new RigidBody3D[] {
                new RigidBody3D { Position = RVec3.Zero, Mass = 1f },
                new RigidBody3D { Position = new RVec3(3f, 0f, 0f), Mass = 1f }
            };
            foreach (var x in bodies) x.SetInertia(RigidBody3D.SphereInertia(1f, 0.5f));
            var pipe = new YrPhysicsPipeline(bodies,
                new RVec3[] { new RVec3(1f, 1f, 1f), new RVec3(1f, 1f, 1f) });
            pipe.SleepEnabled = false;
            pipe.StepFixed(1f / 60f);
            float[] before = pipe.Snapshot();
            pipe.UseCoupledMassMatrix = true;
            float[] after = pipe.Snapshot();
            Require(before.Length == after.Length, "开关不得改变快照长度");
            for (int i = 0; i < before.Length; i++)
                Require(before[i] == after[i], "开关不得改变快照内容 @" + i);
            pipe.Restore(before);
            float[] restored = pipe.Snapshot();
            for (int i = 0; i < before.Length; i++)
                Require(before[i] == restored[i], "Restore 后应逐位一致 @" + i);
        }

        public static void TestR15NoRegressionOnDefaultPipelinePath()
        {
            var bodies = new RigidBody3D[] {
                new RigidBody3D { Position = RVec3.Zero, Mass = 1f },
                new RigidBody3D { Position = new RVec3(3f, 0f, 0f), Mass = 1f }
            };
            foreach (var x in bodies) x.SetInertia(RigidBody3D.SphereInertia(1f, 0.5f));
            var pipe = new YrPhysicsPipeline(bodies,
                new RVec3[] { new RVec3(1f, 1f, 1f), new RVec3(1f, 1f, 1f) });
            pipe.SleepEnabled = false;
            pipe.StepFixed(1f / 60f);
            Require(pipe.Tick == 1, "默认管线应正常推进");
            ulong h1 = YrRollbackBuffer.Checksum(pipe.Snapshot(), pipe.Tick);
            pipe.StepFixed(1f / 60f);
            ulong h2 = YrRollbackBuffer.Checksum(pipe.Snapshot(), pipe.Tick);
            Require(h1 != h2, "第二 tick 快照应变化");
            Require(pipe.LastCoupledTrace.TotalSteps == 0, "默认路径不得误触发耦合 trace");
        }
    }
}
