using System;
using YamaEngine.Reality;

namespace YamaEngine.Tests
{
    /// <summary>
    /// 第十七轮物理回归（12 项）：跨接触点 3m×3m 全局块耦合求解接进
    /// YrContactSolver / YrPhysicsPipeline，默认关闭零回归、对角块 parity、
    /// 守恒、圆锥投影、收敛、奇异安全退回、三开关联动验证。
    /// 场景由推 CI 前的 float32 预演定标（见 docs/REALITY_PHYSICS_ROUND17.md）。
    /// </summary>
    public static class RealityPhysicsSeventeenthTests
    {
        private static void Require(bool ok, string msg)
        {
            if (!ok) throw new InvalidOperationException("REALITY_R17_FAIL: " + msg);
        }
        private static RVec3 N => new RVec3(0f, 0f, 1f);

        private static RigidBody3D[] Bodies(bool singular = false)
        {
            var a = new RigidBody3D
            { Position = RVec3.Zero, Mass = singular ? 0f : 1f,
              LinearVelocity = new RVec3(0.5f, 0f, 1.2f) };
            var b = new RigidBody3D
            { Position = RVec3.Zero, Mass = singular ? 0f : 1f,
              LinearVelocity = RVec3.Zero };
            if (singular)
            {
                a.InvInertiaLocal = RVec3.Zero;
                b.InvInertiaLocal = RVec3.Zero;
            }
            else
            {
                RVec3 I = RigidBody3D.BoxInertia(1f, 0.4f, 2.0f, 1.2f);
                a.SetInertia(I); b.SetInertia(I);
            }
            return new RigidBody3D[] { a, b };
        }

        private static YrContact[] Contacts()
        {
            var c0 = new YrContact
            {
                BodyA = 0, BodyB = 1, Point = new RVec3(1.3f, 0.7f, 0f), Normal = N,
                Depth = 0.08f, Restitution = 0f,
                FrictionStatic = 0.8f, FrictionDynamic = 0.6f,
                NormalLambda = 0f, TangentLambda1 = 0f, TangentLambda2 = 0f
            };
            var c1 = new YrContact
            {
                BodyA = 0, BodyB = 1, Point = new RVec3(0.4f, -0.9f, 0f), Normal = N,
                Depth = 0.06f, Restitution = 0f,
                FrictionStatic = 0.8f, FrictionDynamic = 0.6f,
                NormalLambda = 0f, TangentLambda1 = 0f, TangentLambda2 = 0f
            };
            return new YrContact[] { c0, c1 };
        }

        private static YrContactSolver Make(RigidBody3D[] bodies, YrContact[] cs,
                                            bool global, float damping = 1e-3f)
        {
            var s = new YrContactSolver();
            s.SetBodies(bodies);
            s.SetContacts(cs);
            s.UseGlobalBlockSolve = global;
            s.GlobalBlockDamping = damping;
            return s;
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

        private static float MaxDiff(RigidBody3D[] x, RigidBody3D[] y)
        {
            float d = 0f;
            for (int i = 0; i < x.Length; i++)
            {
                d = MathF.Max(d, MathF.Abs(x[i].LinearVelocity.X - y[i].LinearVelocity.X));
                d = MathF.Max(d, MathF.Abs(x[i].LinearVelocity.Y - y[i].LinearVelocity.Y));
                d = MathF.Max(d, MathF.Abs(x[i].LinearVelocity.Z - y[i].LinearVelocity.Z));
                d = MathF.Max(d, MathF.Abs(x[i].AngularMomentum.X - y[i].AngularMomentum.X));
                d = MathF.Max(d, MathF.Abs(x[i].AngularMomentum.Y - y[i].AngularMomentum.Y));
                d = MathF.Max(d, MathF.Abs(x[i].AngularMomentum.Z - y[i].AngularMomentum.Z));
            }
            return d;
        }

        public static void TestR17DefaultOffLeavesEmptyTraceAndIsBitwiseDeterministic()
        {
            var b0 = Bodies(); var b1 = Bodies();
            var off0 = Make(b0, Contacts(), false);
            var off1 = Make(b1, Contacts(), false);
            Require(!off0.UseGlobalBlockSolve && !off1.UseGlobalBlockSolve,
                    "默认必须关闭");
            off0.Prepare(); off0.Solve(6);
            off1.Prepare(); off1.Solve(6);
            Require(BitwiseEqual(b0, b1), "关闭开关时两次运行应逐位一致");
            var t = off0.LastGlobalBlockTrace;
            Require(t.Groups == 0 && t.TotalIterations == 0 && t.SolvedGroups == 0
                 && t.FallbackGroups == 0 && t.MaxCrossBlock == 0f && !t.LastUsedGlobal,
                    "关闭开关不应产生任何全局 trace");
        }

        public static void TestR17BuildGlobalKIsSymmetricAndHasCrossCoupling()
        {
            var b = Bodies(); var cs = Contacts();
            float[] k = YrContactBlockMatrix.BuildGlobalK(
                cs, new int[] { 0, 1 }, 2, b,
                out RVec3[] ns, out RVec3[] t1s, out RVec3[] t2s);
            Require(k.Length == 36, "2 点应为 6×6");
            float asym = 0f;
            for (int i = 0; i < 6; i++)
                for (int j = 0; j < 6; j++)
                    asym = MathF.Max(asym, MathF.Abs(k[i * 6 + j] - k[j * 6 + i]));
            Require(asym <= 1e-5f, "全局 K 应严格对称，残差 " + asym);
            float cross = YrContactBlockMatrix.MaxCrossBlockNorm(k, 2);
            Require(cross > 1e-3f, "应组装出可判定的跨点耦合，实际 " + cross);
            Require(ns.Length == 2 && t1s.Length == 2 && t2s.Length == 2,
                    "应回填每点基");
            Require(ns[0].Z == 1f && ns[1].Z == 1f, "法向应归一化");
        }

        public static void TestR17DiagonalBlocksMatchSinglePointBuildKBitwise()
        {
            var b = Bodies(); var cs = Contacts();
            float[] k = YrContactBlockMatrix.BuildGlobalK(
                cs, new int[] { 0, 1 }, 2, b,
                out RVec3[] ns, out RVec3[] t1s, out RVec3[] t2s);
            var basis = YrContactSolver.Basis(N);
            for (int q = 0; q < 2; q++)
            {
                var m = new YrContactMass(
                    b[0].InvMass, b[1].InvMass,
                    b[0].InvInertiaLocal, b[1].InvInertiaLocal,
                    b[0].Orientation, b[1].Orientation,
                    b[0].Position, b[1].Position, 0.6f);
                YrMat3Sym want = YrContactMassMatrix3x3.BuildK(
                    m, cs[q].Point, ns[q], t1s[q], t2s[q]);
                for (int x = 0; x < 3; x++)
                    for (int y = 0; y < 3; y++)
                    {
                        float got = k[(3 * q + x) * 6 + (3 * q + y)];
                        float exp = want.At(x, y);
                        Require(got == exp,
                            "对角块 q=" + q + " (" + x + "," + y + ") 应逐位等于单点 BuildK: "
                            + got + " vs " + exp);
                    }
            }
        }

        public static void TestR17CholeskyRoundTripSolvesKxEqualsB()
        {
            var b = Bodies(); var cs = Contacts();
            float[] k = YrContactBlockMatrix.BuildGlobalK(
                cs, new int[] { 0, 1 }, 2, b,
                out _, out _, out _);
            float damp = 1e-3f;
            bool ok = YrContactBlockMatrix.Cholesky(k, 6, damp, out float[] lower);
            Require(ok, "带阻尼的全局 K 应正定可分解");
            Require(lower.Length == 36, "L 应为 6×6");
            float[] rhs = new float[] { 1f, -2f, 0.5f, 0.3f, 1.1f, -0.7f };
            float[] x = YrContactBlockMatrix.Solve(lower, 6, rhs);
            float maxRes = 0f;
            for (int i = 0; i < 6; i++)
            {
                float s = damp * x[i];
                for (int j = 0; j < 6; j++) s += k[i * 6 + j] * x[j];
                maxRes = MathF.Max(maxRes, MathF.Abs(s - rhs[i]));
            }
            Require(maxRes < 1e-3f, "Cholesky 往返 (K+damp·I)x=b 残差应 <1e-3，实际 " + maxRes);
        }

        public static void TestR17CholeskySingularMatrixFallsBack()
        {
            var zero = new float[36];
            bool ok = YrContactBlockMatrix.Cholesky(zero, 6, 0f, out float[] lower);
            Require(!ok, "全零矩阵加零阻尼应判定非正定");
            Require(lower.Length == 0, "失败时 L 应为空，调用方据此退回");
            var neg = new float[36];
            neg[0] = -1f;
            bool ok2 = YrContactBlockMatrix.Cholesky(neg, 6, 1e-6f, out float[] lower2);
            Require(!ok2, "负主元即使加小阻尼也应失败");
            Require(lower2.Length == 0, "L 应为空");
            bool ok3 = YrContactBlockMatrix.Cholesky(zero, 6, 1e-3f, out float[] lower3);
            Require(ok3 && lower3.Length == 36, "阻尼应能把全零矩阵正则化为正定");
        }

        public static void TestR17TwoPointGlobalVsSequentialProducesMeasurableDifference()
        {
            var b0 = Bodies(); var b1 = Bodies();
            var off = Make(b0, Contacts(), false);
            var on = Make(b1, Contacts(), true);
            off.Prepare(); off.Solve(6);
            on.Prepare(); on.Solve(6);
            float d = MaxDiff(b0, b1);
            Require(d > 1e-4f, "跨点耦合应产生可判定物理差异，实际 " + d);
            var t = on.LastGlobalBlockTrace;
            Require(t.Groups == 1 && t.MaxPointsPerGroup == 2,
                    "2 个同对接触点应成 1 组");
            Require(t.SolvedGroups == 1 && t.FallbackGroups == 0,
                    "该场景应成功走全局块求解");
            Require(t.TotalIterations == 6, "6 次迭代应记录 6 次");
            Require(t.MaxCrossBlock > 1e-3f, "trace 应观测到跨块耦合");
            Require(t.LastUsedGlobal, "最后一次应标记使用全局求解");
            Require(MathF.Abs(off.Contacts[0].TangentLambda2
                            - on.Contacts[0].TangentLambda2) > 1e-4f
                 || MathF.Abs(off.Contacts[1].TangentLambda2
                            - on.Contacts[1].TangentLambda2) > 1e-4f,
                    "切向 λ 应因跨点耦合而不同");
        }

        public static void TestR17GlobalConservesLinearAndAngularMomentum()
        {
            var b = Bodies(); var cs = Contacts();
            var s = Make(b, cs, true);
            RVec3 p0 = YrContactSolver.TotalLinearMomentum(b);
            RVec3 l0 = YrContactSolver.TotalAngularMomentum(b);
            s.Prepare(); s.Solve(6);
            RVec3 p1 = YrContactSolver.TotalLinearMomentum(b);
            RVec3 l1 = YrContactSolver.TotalAngularMomentum(b);
            // float32 加减存在 1 ulp 舍入；承认有限精度，不改变物理、不删断言。
            Require(MathF.Abs(p0.X - p1.X) < 1e-5f
                 && MathF.Abs(p0.Y - p1.Y) < 1e-5f
                 && MathF.Abs(p0.Z - p1.Z) < 1e-5f, "成对冲量应守恒总线动量");
            Require(MathF.Abs(l0.X - l1.X) < 1e-5f
                 && MathF.Abs(l0.Y - l1.Y) < 1e-5f
                 && MathF.Abs(l0.Z - l1.Z) < 1e-5f,
                    "成对冲量（自旋+轨道）应守恒总角动量");
        }

        public static void TestR17GlobalLambdaInsideFrictionCone()
        {
            var b = Bodies(); var cs = Contacts();
            var s = Make(b, cs, true);
            s.Prepare(); s.Solve(6);
            for (int q = 0; q < 2; q++)
            {
                var c = cs[q];
                Require(c.NormalLambda >= 0f, "λn 应非负 @" + q);
                float len = MathF.Sqrt(c.TangentLambda1 * c.TangentLambda1
                                     + c.TangentLambda2 * c.TangentLambda2);
                float upper = MathF.Max(c.FrictionStatic, c.FrictionDynamic)
                            * c.NormalLambda;
                Require(len <= upper + 1e-6f,
                        "切向 λ 应在库仑锥内（μ 上界）@" + q + " 实际 " + len);
                Require(!float.IsNaN(c.NormalLambda) && !float.IsNaN(c.TangentLambda1)
                     && !float.IsNaN(c.TangentLambda2), "λ 不应出现 NaN @" + q);
            }
        }

        public static void TestR17GlobalNormalVelocityConverges()
        {
            var b = Bodies(); var cs = Contacts();
            var s = Make(b, cs, true);
            s.Prepare();
            for (int q = 0; q < 2; q++)
                Require(MathF.Abs(cs[q].InitialNormalVelocity) > 1f,
                        "前置：应有可判定的接近速度 @" + q);
            s.Solve(6);
            for (int q = 0; q < 2; q++)
            {
                float vn = s.RelativeVelocityAt(cs[q]).Dot(N);
                Require(MathF.Abs(vn) < 1e-3f,
                        "全局块解 6 次迭代后两点法向速度应收敛到目标附近 @"
                        + q + " 实际 " + vn);
            }
        }

        public static void TestR17GlobalTraceIsDeterministic()
        {
            YrGlobalBlockTrace Run()
            {
                var b = Bodies();
                var s = Make(b, Contacts(), true);
                s.Prepare(); s.Solve(5);
                return s.LastGlobalBlockTrace;
            }
            var x = Run(); var y = Run();
            Require(x.Groups == y.Groups
                 && x.MaxPointsPerGroup == y.MaxPointsPerGroup
                 && x.TotalIterations == y.TotalIterations
                 && x.SolvedGroups == y.SolvedGroups
                 && x.FallbackGroups == y.FallbackGroups,
                    "trace 计数应逐位确定");
            Require(x.MaxCrossBlock == y.MaxCrossBlock, "MaxCrossBlock 应逐位确定");
            Require(x.LastUsedGlobal == y.LastUsedGlobal, "LastUsedGlobal 应确定");
        }

        public static void TestR17SingularContextFallsBackSafely()
        {
            var b = Bodies(true);
            var cs = Contacts();
            var s = Make(b, cs, true, 0f);   // 零阻尼 → 全零 K → Cholesky 失败
            s.Prepare(); s.Solve(4);
            var t = s.LastGlobalBlockTrace;
            Require(t.Groups == 1 && t.FallbackGroups == 1 && t.SolvedGroups == 0,
                    "奇异上下文应整组安全退回");
            Require(!t.LastUsedGlobal, "不应标记使用全局求解");
            Require(b[0].LinearVelocity.X == 0.5f
                 && b[0].LinearVelocity.Y == 0f
                 && b[0].LinearVelocity.Z == 1.2f,
                    "退回路径不得改变速度");
            Require(!float.IsNaN(b[0].AngularMomentum.X)
                 && !float.IsInfinity(b[0].AngularMomentum.Y),
                    "不应出现 NaN/Inf");
        }

        public static void TestR17PipelineSwitchAndThreeWayLinkage()
        {
            var bodies = new RigidBody3D[] {
                new RigidBody3D { Position = RVec3.Zero, Mass = 1f },
                new RigidBody3D { Position = new RVec3(0.5f, 0f, 0f), Mass = 1f }
            };
            foreach (var x in bodies) x.SetInertia(RigidBody3D.BoxInertia(1f, 1f, 1f, 1f));
            var pipe = new YrPhysicsPipeline(bodies,
                new RVec3[] { new RVec3(1f,1f,1f), new RVec3(1f,1f,1f) });
            pipe.SleepEnabled = false;
            Require(!pipe.UseGlobalBlockSolve, "管线默认必须关闭");
            Require(MathF.Abs(pipe.GlobalBlockDamping - 1e-3f) < 1e-9f,
                    "默认阻尼应转发");

            pipe.StepFixed(1f / 60f);
            float[] before = pipe.Snapshot();
            Require(pipe.LastGlobalBlockTrace.Groups == 0,
                    "默认路径不得写全局 trace");
            Require(pipe.LastCoupledTrace.TotalSteps == 0,
                    "默认路径不得写耦合 trace");
            Require(pipe.LastReducedPoints == 0, "默认路径不得写缩减观测量");

            // 三开关同时开启：验证优先级明确、不冲突、不崩
            pipe.UseGlobalBlockSolve = true;
            pipe.UseCoupledMassMatrix = true;
            pipe.UseCoupledMassReduction = true;
            Require(pipe.UseGlobalBlockSolve && pipe.UseCoupledMassMatrix
                 && pipe.UseCoupledMassReduction, "三开关应都可回读");
            float[] after = pipe.Snapshot();
            Require(before.Length == after.Length, "开关不得改变快照长度");
            for (int i = 0; i < before.Length; i++)
                Require(before[i] == after[i], "开关不得改变快照内容 @" + i);
            pipe.StepFixed(1f / 60f);
            Require(pipe.Tick == 2, "三开关同时开启后 StepFixed 应正常推进");
            pipe.Restore(before);
            float[] restored = pipe.Snapshot();
            for (int i = 0; i < before.Length; i++)
                Require(before[i] == restored[i], "Restore 后逐位一致 @" + i);
            pipe.ResetGlobalBlockTrace();
            Require(pipe.LastGlobalBlockTrace.Groups == 0, "Reset 应清零");
        }
    }
}
