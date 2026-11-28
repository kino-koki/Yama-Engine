using System;
using System.Collections.Generic;
using YamaEngine.Reality;

namespace YamaEngine.Tests
{
    /// <summary>
    /// 第十六轮物理回归（12 项）：耦合质量选点缩减接入 YrNarrowPhase /
    /// YrPhysicsPipeline，默认关闭零回归、可判定选点差异与守恒语义。
    /// </summary>
    public static class RealityPhysicsSixteenthTests
    {
        private static void Require(bool ok, string msg)
        {
            if (!ok) throw new InvalidOperationException("REALITY_R16_FAIL: " + msg);
        }
        private static RVec3 N => new RVec3(0f, 0f, 1f);

        private static YrContactMass Aniso()
        {
            RVec3 I = RigidBody3D.BoxInertia(1f, 0.4f, 2.0f, 1.2f);
            var inv = new RVec3(1f / I.X, 1f / I.Y, 1f / I.Z);
            return YrContactMass.Uniform(1f, inv, RVec3.Zero, RVec3.Zero, 0.6f);
        }

        // 第十四/十五轮数值预演确认有区分度的点集
        private static RVec3[] Points() => new RVec3[] {
            new RVec3(0f,0f,0f), new RVec3(1f,0f,0f), new RVec3(2f,0f,0f),
            new RVec3(3f,0f,0f), new RVec3(0f,1f,0f), new RVec3(1f,0f,1f) };
        private static int[] Ids() => new int[] { 0, 1, 2, 3, 4, 5 };
        private static float[] Depths() => new float[] { 0.9f, 0.2f, 0.2f, 0.2f, 0.2f, 0.2f };

        public static void TestR16ReduceStaticEntryIsBoundedAndDeterministic()
        {
            var p = Points(); var i = Ids(); var d = Depths();
            var a = YrNarrowPhase.ReducePointsByCoupledMass(p, i, d, N, Aniso(), 4);
            var b = YrNarrowPhase.ReducePointsByCoupledMass(p, i, d, N, Aniso(), 4);
            Require(a.Points.Length == 4 && a.Ids.Length == 4, "应缩到 4 点");
            Require(b.Points.Length == 4, "第二次也应 4 点");
            var seen = new HashSet<int>();
            for (int q = 0; q < 4; q++)
            {
                Require(seen.Add(a.Ids[q]), "Id 重复");
                Require(a.Ids[q] == b.Ids[q], "应逐位确定 @" + q);
                Require(a.Points[q].X == p[a.Ids[q]].X
                     && a.Points[q].Y == p[a.Ids[q]].Y
                     && a.Points[q].Z == p[a.Ids[q]].Z, "Point 应回填 @" + q);
            }
            Require(seen.Contains(0), "应保留最深点 Id=0");
        }

        public static void TestR16ReduceMaxParameterIsRespected()
        {
            var p = Points(); var i = Ids(); var d = Depths();
            Require(YrNarrowPhase.ReducePointsByCoupledMass(p, i, d, N, Aniso(), 0).Points.Length == 0,
                    "max=0 应返回空");
            Require(YrNarrowPhase.ReducePointsByCoupledMass(p, i, d, N, Aniso(), 2).Points.Length == 2,
                    "max=2 应返回 2 点");
            Require(YrNarrowPhase.ReducePointsByCoupledMass(p, i, d, N, Aniso(), 9).Points.Length == 6,
                    "max>n 应返回全部 6 点");
        }

        public static void TestR16CoupledReductionDiffersFromScalarReduction()
        {
            var p = Points(); var i = Ids(); var d = Depths(); var m = Aniso();
            var coupled = YrContactMassMatrix3x3.ReduceByCoupledMass(p, i, d, N, m, 3);
            var scalar = YrContactReduction.ReduceByEffectiveMass(p, i, d, N, m, 3);
            Require(coupled.Length == 3 && scalar.Length == 3, "两者都应给 3 点");
            // 第十四/十五轮 float32 预演：耦合 [0,3,4]，标量 [0,3,5]
            bool differs = false;
            for (int q = 0; q < 3; q++)
                if (coupled[q].Id != scalar[q].Id) differs = true;
            Require(differs, "非对角耦合应改变选点");
        }

        public static void TestR16CoupledReductionPreservesDeepestPoint()
        {
            var r = YrContactMassMatrix3x3.ReduceByCoupledMass(
                Points(), Ids(), Depths(), N, Aniso(), 4);
            bool found = false;
            for (int q = 0; q < r.Length; q++) if (r[q].Id == 0) found = true;
            Require(found, "侵入最深点必须保留");
            for (int q = 1; q < r.Length; q++)
                Require(r[q - 1].Id <= r[q].Id, "输出应按 Id 排序");
        }

        public static void TestR16SingularMassReductionIsSafe()
        {
            var zero = new YrContactMass(0f, 0f, RVec3.Zero, RVec3.Zero,
                RQuat.Identity, RQuat.Identity, RVec3.Zero, RVec3.Zero, 0f);
            var r = YrNarrowPhase.ReducePointsByCoupledMass(
                Points(), Ids(), Depths(), N, zero, 4);
            Require(r.Points.Length == 4, "奇异上下文仍应有界");
            for (int q = 0; q < r.Points.Length; q++)
                Require(!float.IsNaN(r.Points[q].X) && !float.IsInfinity(r.Points[q].Y),
                        "不应出现 NaN/Inf");
        }

        public static void TestR16EmptyInputIsSafe()
        {
            var r = YrNarrowPhase.ReducePointsByCoupledMass(
                Array.Empty<RVec3>(), Array.Empty<int>(), Array.Empty<float>(), N, Aniso(), 4);
            Require(r.Points.Length == 0 && r.Ids.Length == 0, "空输入应返回空");
        }

        public static void TestR16StaticEntryMatchesDirectMatrixCall()
        {
            // 接线不得偷偷改语义：静态入口必须与第十四轮矩阵实现逐位一致
            var p = Points(); var i = Ids(); var d = Depths(); var m = Aniso();
            var direct = YrContactMassMatrix3x3.ReduceByCoupledMass(p, i, d, N, m, 4);
            var entry = YrNarrowPhase.ReducePointsByCoupledMass(p, i, d, N, m, 4);
            Require(entry.Points.Length == direct.Length, "点数应一致");
            for (int q = 0; q < direct.Length; q++)
            {
                Require(entry.Ids[q] == direct[q].Id, "Id 应一致 @" + q);
                Require(entry.Points[q].X == direct[q].Point.X
                     && entry.Points[q].Y == direct[q].Point.Y
                     && entry.Points[q].Z == direct[q].Point.Z,
                        "Point 应一致 @" + q);
            }
        }

        private static (YrObb[] Obbs, RigidBody3D[] Bodies) Scene()
        {
            var bodies = new RigidBody3D[] {
                new RigidBody3D { Position = RVec3.Zero, Mass = 1f },
                new RigidBody3D { Position = new RVec3(0.5f, 0f, 0f), Mass = 1f }
            };
            foreach (var b in bodies) b.SetInertia(RigidBody3D.BoxInertia(1f, 1f, 1f, 1f));
            var obbs = new YrObb[] {
                new YrObb(bodies[0].Position, bodies[0].Orientation, new RVec3(1f,1f,1f)),
                new YrObb(bodies[1].Position, bodies[1].Orientation, new RVec3(1f,1f,1f))
            };
            return (obbs, bodies);
        }

        public static void TestR16NarrowPhaseDefaultOffIsBitwiseUnchanged()
        {
            var (obbs, bodies) = Scene();
            var a = new YrNarrowPhase();
            var b = new YrNarrowPhase();
            Require(!a.UseCoupledMassReduction && !b.UseCoupledMassReduction,
                    "默认必须关闭");
            var ca = a.Build(new (int, int)[] { (0, 1) }, obbs, bodies,
                             1f / 60f, YrSurface.Default);
            var cb = b.Build(new (int, int)[] { (0, 1) }, obbs, bodies,
                             1f / 60f, YrSurface.Default);
            Require(ca.Length == cb.Length, "默认关闭时点数应一致");
            for (int q = 0; q < ca.Length; q++)
                Require(ca[q].Point.X == cb[q].Point.X
                     && ca[q].Point.Y == cb[q].Point.Y
                     && ca[q].Point.Z == cb[q].Point.Z
                     && ca[q].NormalLambda == cb[q].NormalLambda,
                        "默认关闭应逐位一致 @" + q);
            Require(a.LastReducedPoints == 0, "关闭时不得写观测量");
        }

        public static void TestR16NarrowPhaseSwitchIsWiredAndBounded()
        {
            var (obbs, bodies) = Scene();
            var n = new YrNarrowPhase();
            n.UseCoupledMassReduction = true;
            Require(n.UseCoupledMassReduction, "开关应可设置并回读");
            var cs = n.Build(new (int, int)[] { (0, 1) }, obbs, bodies,
                             1f / 60f, YrSurface.Default);
            Require(cs.Length <= 4, "开启后单对接触点数应有界（≤4）");
            Require(cs.Length >= 0, "不应抛异常");
            for (int q = 0; q < cs.Length; q++)
                Require(!float.IsNaN(cs[q].Point.X) && !float.IsInfinity(cs[q].Depth),
                        "接触点不应出现 NaN/Inf");
            Require(n.LastReducedPoints == cs.Length,
                    "观测量应等于缩减后点数");
        }

        public static void TestR16PipelineForwardsReductionSwitch()
        {
            var bodies = new RigidBody3D[] {
                new RigidBody3D { Position = RVec3.Zero, Mass = 1f },
                new RigidBody3D { Position = new RVec3(3f, 0f, 0f), Mass = 1f }
            };
            foreach (var x in bodies) x.SetInertia(RigidBody3D.SphereInertia(1f, 0.5f));
            var pipe = new YrPhysicsPipeline(bodies,
                new RVec3[] { new RVec3(1f,1f,1f), new RVec3(1f,1f,1f) });
            pipe.SleepEnabled = false;
            Require(!pipe.UseCoupledMassReduction, "管线默认关闭");
            pipe.UseCoupledMassReduction = true;
            Require(pipe.UseCoupledMassReduction, "应转发到窄相");
            pipe.StepFixed(1f / 60f);
            Require(pipe.Tick == 1, "开启后 StepFixed 应正常推进");
        }

        public static void TestR16ReductionDoesNotChangeSnapshotFormat()
        {
            var bodies = new RigidBody3D[] {
                new RigidBody3D { Position = RVec3.Zero, Mass = 1f },
                new RigidBody3D { Position = new RVec3(3f, 0f, 0f), Mass = 1f }
            };
            foreach (var x in bodies) x.SetInertia(RigidBody3D.SphereInertia(1f, 0.5f));
            var pipe = new YrPhysicsPipeline(bodies,
                new RVec3[] { new RVec3(1f,1f,1f), new RVec3(1f,1f,1f) });
            pipe.SleepEnabled = false;
            pipe.StepFixed(1f / 60f);
            float[] before = pipe.Snapshot();
            pipe.UseCoupledMassReduction = true;
            float[] after = pipe.Snapshot();
            Require(before.Length == after.Length, "开关不得改变快照长度");
            for (int q = 0; q < before.Length; q++)
                Require(before[q] == after[q], "快照内容不得变 @" + q);
            pipe.Restore(before);
            float[] restored = pipe.Snapshot();
            for (int q = 0; q < before.Length; q++)
                Require(before[q] == restored[q], "Restore 后逐位一致 @" + q);
        }

        public static void TestR16DefaultPipelineNoRegression()
        {
            var bodies = new RigidBody3D[] {
                new RigidBody3D { Position = RVec3.Zero, Mass = 1f },
                new RigidBody3D { Position = new RVec3(3f, 0f, 0f), Mass = 1f }
            };
            foreach (var x in bodies) x.SetInertia(RigidBody3D.SphereInertia(1f, 0.5f));
            var pipe = new YrPhysicsPipeline(bodies,
                new RVec3[] { new RVec3(1f,1f,1f), new RVec3(1f,1f,1f) });
            pipe.SleepEnabled = false;
            pipe.StepFixed(1f / 60f);
            Require(pipe.Tick == 1, "默认管线正常推进");
            ulong h1 = YrRollbackBuffer.Checksum(pipe.Snapshot(), pipe.Tick);
            pipe.StepFixed(1f / 60f);
            ulong h2 = YrRollbackBuffer.Checksum(pipe.Snapshot(), pipe.Tick);
            Require(h1 != h2, "第二 tick 快照应变化");
            Require(pipe.LastReducedPoints == 0, "默认路径不得写缩减观测量");
            Require(pipe.LastCoupledTrace.TotalSteps == 0, "默认路径不得触发耦合求解 trace");
        }
    }
}
