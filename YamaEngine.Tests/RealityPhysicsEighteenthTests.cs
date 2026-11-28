using System;
using YamaEngine.Reality;

namespace YamaEngine.Tests
{
    /// <summary>
    /// 第十八轮数值回归（12 项）：块线性求解器谱系（Direct / 块 Jacobi /
    /// 块 Gauss-Seidel / 块预条件 CG）+ 诚实收敛诊断 + 双判据安全退回。
    ///
    /// 全部阈值来自推 CI 前的 float32 逐步累加预演（见
    /// docs/REALITY_PHYSICS_ROUND18.md），没有放宽、没有删断言。
    /// </summary>
    public static class RealityPhysicsEighteenthTests
    {
        private static void Require(bool ok, string msg)
        {
            if (!ok) throw new InvalidOperationException("REALITY_R18_FAIL: " + msg);
        }

        /// <summary>良条件对称正定 6×6（cond≈2.2），标准数值测试夹具。</summary>
        private static float[] WellConditionedSpd()
        {
            return new float[] {
                10f, 2f, 1f, 0f, 0f, 0f,
                2f, 12f, 3f, 0.5f, 0f, 0f,
                1f, 3f, 9f, 0f, 0.2f, 0f,
                0f, 0.5f, 0f, 11f, 1f, 0f,
                0f, 0f, 0.2f, 1f, 8f, 0.3f,
                0f, 0f, 0f, 0f, 0.3f, 7f
            };
        }

        private static float[] Rhs()
            => new float[] { 1f, -2f, 0.5f, 0.3f, 1.1f, -0.7f };

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
            { BodyA = 0, BodyB = 1, Point = new RVec3(1.3f, 0.7f, 0f), Normal = N,
              Depth = 0.08f, Restitution = 0f, FrictionStatic = 0.8f,
              FrictionDynamic = 0.6f };
            var c1 = new YrContact
            { BodyA = 0, BodyB = 1, Point = new RVec3(0.4f, -0.9f, 0f), Normal = N,
              Depth = 0.06f, Restitution = 0f, FrictionStatic = 0.8f,
              FrictionDynamic = 0.6f };
            return new YrContact[] { c0, c1 };
        }

        /// <summary>构造第十七轮的物理全局 K（病态：cond≈3.16e4）。</summary>
        private static float[] PhysicalK(out int n, out int m)
        {
            var b = Bodies(); var cs = Contacts();
            n = 6; m = 2;
            return YrContactBlockMatrix.BuildGlobalK(
                cs, new int[] { 0, 1 }, m, b, out _, out _, out _);
        }

        private static float[] DirectSetup(float[] k, int n, float damping,
                                           out float[] lower)
        {
            bool ok = YrContactBlockMatrix.Cholesky(k, n, damping, out lower);
            Require(ok, "前置：全局 K + 阻尼应可 Cholesky 分解");
            return YrContactBlockMatrix.Solve(lower, n, Rhs());
        }

        private static YrBlockLinearTrace Solve(float[] k, int n, int m,
                                                 float damping,
                                                 YrBlockSolveMode mode,
                                                 out float[] x)
        {
            bool ok = YrContactBlockMatrix.Cholesky(k, n, damping, out float[] lower);
            Require(ok, "前置：Cholesky 应成功");
            x = YrBlockLinearSolvers.TrySolve(mode, k, n, m, damping, lower,
                Rhs(), YrBlockLinearSolvers.DefaultMaxInnerIterations,
                YrBlockLinearSolvers.DefaultTolerance,
                YrBlockLinearSolvers.DefaultSolutionTolerance,
                out YrBlockLinearTrace t);
            return t;
        }

        private static bool BitwiseEqual(float[] u, float[] v)
        {
            if (u.Length != v.Length) return false;
            for (int i = 0; i < u.Length; i++) if (u[i] != v[i]) return false;
            return true;
        }

        private static YrContactSolver Make(RigidBody3D[] bodies, YrContact[] cs,
                                            YrBlockSolveMode mode)
        {
            var s = new YrContactSolver();
            s.SetBodies(bodies); s.SetContacts(cs);
            s.UseGlobalBlockSolve = true;
            s.GlobalBlockSolveMode = mode;
            return s;
        }

        public static void TestR18DirectDefaultIsBitwiseZeroRegression()
        {
            float[] k = PhysicalK(out int n, out int m);
            float[] direct = DirectSetup(k, n, 1e-3f, out float[] lower);
            // Direct 模式应逐字等于第十七轮 YrContactBlockMatrix.Solve
            float[] via = YrBlockLinearSolvers.TrySolve(
                YrBlockSolveMode.Direct, k, n, m, 1e-3f, lower, Rhs(),
                60, 1e-5f, 1e-3f, out YrBlockLinearTrace t);
            Require(BitwiseEqual(direct, via), "Direct 必须与第十七轮逐位一致");
            Require(t.Mode == YrBlockSolveMode.Direct
                 && t.InnerIterations == 0 && !t.UsedFallback,
                    "Direct 不应产生迭代 trace / 退回");

            var b0 = Bodies(); var b1 = Bodies();
            var s0 = Make(b0, Contacts(), YrBlockSolveMode.Direct);
            var s1 = Make(b1, Contacts(), YrBlockSolveMode.Direct);
            s0.Prepare(); s0.Solve(6);
            s1.Prepare(); s1.Solve(6);
            for (int i = 0; i < 2; i++)
                Require(b0[i].LinearVelocity.X == b1[i].LinearVelocity.X
                     && b0[i].LinearVelocity.Y == b1[i].LinearVelocity.Y
                     && b0[i].LinearVelocity.Z == b1[i].LinearVelocity.Z,
                        "Direct 物理应逐位确定");
            Require(s0.BlockLinearFallbackCount == 0, "Direct 不应有退回计数");
            Require(s0.LastBlockLinearTrace.InnerIterations == 0,
                    "Direct 不应写迭代 trace");
        }

        public static void TestR18WellConditionedBlockJacobiConverges()
        {
            float[] k = WellConditionedSpd();
            float[] direct = DirectSetup(k, 6, 0f, out _);
            var t = Solve(k, 6, 2, 0f, YrBlockSolveMode.BlockJacobi, out float[] x);
            Require(t.Converged, "良条件上块 Jacobi 残差应达标");
            Require(!t.UsedFallback, "良条件上不应退回");
            Require(YrBlockLinearSolvers.MaxAbsDiff(x, direct) < 1e-5f,
                    "块 Jacobi 解应逼近直接解，实际 "
                    + YrBlockLinearSolvers.MaxAbsDiff(x, direct));
            Require(t.ResidualMonotonic, "良条件上块 Jacobi 残差应单调下降");
            Require(t.InnerIterations > 0 && t.InnerIterations <= 60,
                    "内迭代数应有界");
        }

        public static void TestR18WellConditionedBlockGaussSeidelConverges()
        {
            float[] k = WellConditionedSpd();
            float[] direct = DirectSetup(k, 6, 0f, out _);
            var t = Solve(k, 6, 2, 0f, YrBlockSolveMode.BlockGaussSeidel, out float[] x);
            Require(t.Converged && !t.UsedFallback, "良条件上块 GS 应收敛且不退回");
            Require(YrBlockLinearSolvers.MaxAbsDiff(x, direct) < 1e-5f,
                    "块 GS 解应逼近直接解");
            Require(t.ResidualMonotonic, "良条件上块 GS 残差应单调下降");
        }

        public static void TestR18WellConditionedPreconditionedCGConverges()
        {
            float[] k = WellConditionedSpd();
            float[] direct = DirectSetup(k, 6, 0f, out _);
            var t = Solve(k, 6, 2, 0f, YrBlockSolveMode.PreconditionedCG, out float[] x);
            Require(t.Converged && !t.UsedFallback, "良条件上块 PCG 应收敛且不退回");
            Require(YrBlockLinearSolvers.MaxAbsDiff(x, direct) < 1e-5f,
                    "块 PCG 解应逼近直接解");
            Require(t.ResidualMonotonic, "良条件上块 PCG 残差应单调下降");
        }

        public static void TestR18GaussSeidelFasterThanJacobiOnWellConditioned()
        {
            float[] k = WellConditionedSpd();
            var tj = Solve(k, 6, 2, 0f, YrBlockSolveMode.BlockJacobi, out _);
            var tg = Solve(k, 6, 2, 0f, YrBlockSolveMode.BlockGaussSeidel, out _);
            var tp = Solve(k, 6, 2, 0f, YrBlockSolveMode.PreconditionedCG, out _);
            Require(tj.Converged && tg.Converged && tp.Converged,
                    "三种模式在良条件上都应收敛");
            Require(tg.InnerIterations < tj.InnerIterations,
                    "块 GS 应比块 Jacobi 更快（GS=" + tg.InnerIterations
                    + " Jacobi=" + tj.InnerIterations + "）");
            Require(tp.InnerIterations < tj.InnerIterations,
                    "块 PCG 应比块 Jacobi 更快（PCG=" + tp.InnerIterations + "）");
        }

        public static void TestR18IllConditionedJacobiFallsBackToDirectBitwise()
        {
            float[] k = PhysicalK(out int n, out int m);
            float[] direct = DirectSetup(k, n, 1e-3f, out _);
            var t = Solve(k, n, m, 1e-3f, YrBlockSolveMode.BlockJacobi, out float[] x);
            Require(!t.Converged, "病态 K 上块 Jacobi 不应宣称收敛");
            Require(t.FinalResidual > 0.1f,
                    "病态 K 上块 Jacobi 60 步残差应仍 >0.1，实际 " + t.FinalResidual);
            Require(t.UsedFallback, "必须安全退回直接解");
            Require(BitwiseEqual(x, direct), "退回解必须与 Direct 逐位一致");
        }

        public static void TestR18IllConditionedGaussSeidelFallsBackToDirectBitwise()
        {
            float[] k = PhysicalK(out int n, out int m);
            float[] direct = DirectSetup(k, n, 1e-3f, out _);
            var t = Solve(k, n, m, 1e-3f,
                YrBlockSolveMode.BlockGaussSeidel, out float[] x);
            Require(!t.Converged && t.FinalResidual > 0.1f,
                    "病态 K 上块 GS 不应宣称收敛，实际残差 " + t.FinalResidual);
            Require(t.UsedFallback, "必须安全退回直接解");
            Require(BitwiseEqual(x, direct), "退回解必须与 Direct 逐位一致");
        }

        public static void TestR18IllConditionedPCGSolutionDriftIsCaughtByDualGate()
        {
            float[] k = PhysicalK(out int n, out int m);
            float[] direct = DirectSetup(k, n, 1e-3f, out _);
            var t = Solve(k, n, m, 1e-3f,
                YrBlockSolveMode.PreconditionedCG, out float[] x);
            // 诚实记录：病态系统上「残差变小」不等于「解变准」。
            Require(t.SolutionDiscrepancy > 1e-3f,
                    "病态 K 上 PCG 解漂移应可判定 >1e-3，实际 "
                    + t.SolutionDiscrepancy);
            Require(t.MaxResidual > t.InitialResidual,
                    "病态 K 上 PCG 残差应非单调（能量范数≠2-范数）");
            Require(t.UsedFallback, "双判据应拦下解漂移并退回");
            Require(BitwiseEqual(x, direct), "退回解必须与 Direct 逐位一致");
            Require(!float.IsNaN(t.FinalResidual) && !float.IsNaN(t.MaxResidual),
                    "诊断量不应出现 NaN");
        }

        public static void TestR18IterativeTraceIsBitwiseDeterministic()
        {
            YrBlockLinearTrace Run(YrBlockSolveMode mode, float[] k)
            {
                Solve(k, 6, 2, 0f, mode, out _);
                YrContactBlockMatrix.Cholesky(k, 6, 0f, out float[] lower);
                YrBlockLinearSolvers.TrySolve(mode, k, 6, 2, 0f, lower, Rhs(),
                    60, 1e-5f, 1e-3f, out YrBlockLinearTrace t);
                return t;
            }
            float[] w = WellConditionedSpd();
            foreach (var mode in new[] { YrBlockSolveMode.BlockJacobi,
                                          YrBlockSolveMode.BlockGaussSeidel,
                                          YrBlockSolveMode.PreconditionedCG })
            {
                var a = Run(mode, w); var b = Run(mode, w);
                Require(a.Mode == b.Mode
                     && a.InnerIterations == b.InnerIterations
                     && a.InitialResidual == b.InitialResidual
                     && a.FinalResidual == b.FinalResidual
                     && a.MaxResidual == b.MaxResidual
                     && a.SolutionDiscrepancy == b.SolutionDiscrepancy
                     && a.Converged == b.Converged
                     && a.UsedFallback == b.UsedFallback
                     && a.ResidualMonotonic == b.ResidualMonotonic,
                        "trace 应逐位确定 @" + mode);
            }
        }

        public static void TestR18NonPositiveDefiniteBlocksAreRejectedAndNoNaNEver()
        {
            // ① 非正定 3×3 块必须被明确拒绝（不制造 NaN/Inf）
            bool blk = YrBlockLinearSolvers.Cholesky3(new float[9], out float[] l);
            Require(!blk && l.Length == 0, "全零 3×3 块 Cholesky 应失败且 L 为空");
            var zeroA = new float[36];
            bool bp = YrBlockLinearSolvers.BuildBlockPreconditioner(
                zeroA, 6, 2, out float[] lowers);
            Require(!bp && lowers.Length == 0, "非正定块预条件应整体拒绝");

            // ② 全零 K + 阻尼 → 纯对角系统：迭代应安全收敛且无 NaN
            var k = new float[36];
            bool ok = YrContactBlockMatrix.Cholesky(k, 6, 1e-3f, out float[] lower);
            Require(ok, "前置：阻尼后全零矩阵应可分解");
            float[] x = YrBlockLinearSolvers.TrySolve(
                YrBlockSolveMode.BlockJacobi, k, 6, 2, 1e-3f, lower, Rhs(),
                60, 1e-5f, 1e-3f, out YrBlockLinearTrace t);
            for (int i = 0; i < 6; i++)
                Require(!float.IsNaN(x[i]) && !float.IsInfinity(x[i]),
                        "对角系统迭代解不应出现 NaN/Inf @" + i);
            Require(t.Converged && !t.UsedFallback,
                    "纯对角系统块 Jacobi 应一步收敛且不退回");

            // ③ 奇异物理上下文（全零质量/惯量）下迭代模式同样不产生 NaN
            var sb = Bodies(true);
            var ss = Make(sb, Contacts(), YrBlockSolveMode.PreconditionedCG);
            ss.GlobalBlockDamping = 0f;
            ss.Prepare(); ss.Solve(3);
            for (int i = 0; i < 2; i++)
                Require(!float.IsNaN(sb[i].LinearVelocity.X)
                     && !float.IsInfinity(sb[i].AngularMomentum.Y),
                        "奇异上下文不应出现 NaN/Inf @" + i);
        }

        public static void TestR18IterativeModesConserveMomentumAndStayInsideCone()
        {
            foreach (var mode in new[] { YrBlockSolveMode.BlockJacobi,
                                          YrBlockSolveMode.BlockGaussSeidel,
                                          YrBlockSolveMode.PreconditionedCG,
                                          YrBlockSolveMode.Direct })
            {
                var b = Bodies(); var cs = Contacts();
                var s = Make(b, cs, mode);
                RVec3 p0 = YrContactSolver.TotalLinearMomentum(b);
                RVec3 l0 = YrContactSolver.TotalAngularMomentum(b);
                s.Prepare(); s.Solve(6);
                RVec3 p1 = YrContactSolver.TotalLinearMomentum(b);
                RVec3 l1 = YrContactSolver.TotalAngularMomentum(b);
                Require(MathF.Abs(p0.X - p1.X) < 1e-5f
                     && MathF.Abs(p0.Z - p1.Z) < 1e-5f,
                        "成对冲量应守恒线动量 @" + mode);
                Require(MathF.Abs(l0.X - l1.X) < 1e-5f
                     && MathF.Abs(l0.Z - l1.Z) < 1e-5f,
                        "成对冲量应守恒总角动量 @" + mode);
                for (int q = 0; q < 2; q++)
                {
                    var c = cs[q];
                    Require(c.NormalLambda >= 0f && !float.IsNaN(c.NormalLambda),
                            "λn 应非负且非 NaN @" + mode + "/" + q);
                    float len = MathF.Sqrt(c.TangentLambda1 * c.TangentLambda1
                                         + c.TangentLambda2 * c.TangentLambda2);
                    float up = MathF.Max(c.FrictionStatic, c.FrictionDynamic)
                             * c.NormalLambda;
                    Require(len <= up + 1e-6f, "切向 λ 应在库仑锥内 @"
                            + mode + "/" + q);
                }
            }
        }

        public static void TestR18PipelineFourSwitchLinkageKeepsSnapshotIntact()
        {
            var bodies = new RigidBody3D[] {
                new RigidBody3D { Position = RVec3.Zero, Mass = 1f },
                new RigidBody3D { Position = new RVec3(0.5f, 0f, 0f), Mass = 1f }
            };
            foreach (var x in bodies) x.SetInertia(RigidBody3D.BoxInertia(1f, 1f, 1f, 1f));
            var pipe = new YrPhysicsPipeline(bodies,
                new RVec3[] { new RVec3(1f,1f,1f), new RVec3(1f,1f,1f) });
            pipe.SleepEnabled = false;
            Require(pipe.GlobalBlockSolveMode == YrBlockSolveMode.Direct,
                    "管线默认必须为 Direct");
            Require(pipe.GlobalBlockInnerIterations == 60
                 && MathF.Abs(pipe.GlobalBlockInnerTolerance - 1e-5f) < 1e-9f
                 && MathF.Abs(pipe.GlobalBlockSolutionTolerance - 1e-3f) < 1e-9f,
                    "内迭代参数应正确转发");

            pipe.StepFixed(1f / 60f);
            float[] before = pipe.Snapshot();
            Require(pipe.LastBlockLinearTrace.InnerIterations == 0
                 && pipe.BlockLinearFallbackCount == 0,
                    "默认路径不得写迭代 trace / 退回计数");

            // 四开关同帧开启：全局块 + 迭代模式 + 选点缩减 + 单点块解
            pipe.UseGlobalBlockSolve = true;
            pipe.GlobalBlockSolveMode = YrBlockSolveMode.PreconditionedCG;
            pipe.UseCoupledMassReduction = true;
            pipe.UseCoupledMassMatrix = true;
            Require(pipe.UseGlobalBlockSolve
                 && pipe.GlobalBlockSolveMode == YrBlockSolveMode.PreconditionedCG
                 && pipe.UseCoupledMassReduction && pipe.UseCoupledMassMatrix,
                    "四开关应都可回读");
            float[] after = pipe.Snapshot();
            Require(before.Length == after.Length, "开关不得改变快照长度");
            for (int i = 0; i < before.Length; i++)
                Require(before[i] == after[i], "开关不得改变快照内容 @" + i);
            pipe.StepFixed(1f / 60f);
            Require(pipe.Tick == 2, "四开关同时开启后 StepFixed 应正常推进");
            pipe.Restore(before);
            float[] restored = pipe.Snapshot();
            for (int i = 0; i < before.Length; i++)
                Require(before[i] == restored[i], "Restore 后逐位一致 @" + i);
            pipe.ResetBlockLinearTrace();
            Require(pipe.LastBlockLinearTrace.InnerIterations == 0
                 && pipe.BlockLinearFallbackCount == 0, "Reset 应清零");
        }
    }
}
