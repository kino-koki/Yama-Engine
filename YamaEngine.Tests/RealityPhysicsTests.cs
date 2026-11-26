using System;
using YamaEngine.Reality;

namespace YamaEngine.Tests
{
    /// <summary>
    /// 真实物理内核的回归测试（纯 C#，无外部依赖，被 XunitAdapter 反射发现，
    /// 因此每次 CI 的 Test step 都会真实执行）。校验的是确定性与守恒，不是画面。
    /// </summary>
    public static class RealityPhysicsTests
    {
        private static void Require(bool ok, string msg)
        {
            if (!ok) throw new InvalidOperationException("REALITY_TEST_FAIL: " + msg);
        }
        private static bool Near(float a, float b, float tol) => MathF.Abs(a - b) <= tol;

        public static void TestRealityConservationLedgerClosed()
        {
            var l = new ConservationLedger();
            l.Supply(2.0, "Fe", 100.0, RVec3.Zero);
            l.Supply(1.0, "O", 50.0, RVec3.Zero);
            l.React("Fe", "FeO", 1.0, 0.0, RVec3.Zero);
            Require(l.IsBalanced(1e-9), "封闭系统四本账失衡 " + l.ClosedResidual());
            Require(Near((float)l.MassStored, 3f, 1e-6f), "质量账错误");
        }

        public static void TestRealityXpbdDistanceConverges()
        {
            var solver = new XpbdSolver();
            var p = new XpbdSolver.Particle[2];
            p[0] = new XpbdSolver.Particle { Position = new RVec3(0f, 2f, 0f), Velocity = RVec3.Zero, Previous = new RVec3(0f, 2f, 0f), InvMass = 1f };
            p[1] = new XpbdSolver.Particle { Position = new RVec3(2f, 2f, 0f), Velocity = RVec3.Zero, Previous = new RVec3(2f, 2f, 0f), InvMass = 1f };
            var c = new XpbdSolver.DistanceConstraint[1];
            c[0] = new XpbdSolver.DistanceConstraint { A = 0, B = 1, RestLength = 1f, Compliance = 0f, Lambda = 0f };
            solver.SetParticles(p); solver.SetConstraints(c);
            solver.Step(1f / 60f, 4, 12, 0f);
            float d = (solver.Particles[0].Position - solver.Particles[1].Position).Length();
            Require(Near(d, 1f, 0.02f), "XPBD 距离约束未收敛: " + d);
        }

        public static void TestRealityXpbdSubstepIndependence()
        {
            // XPBD 的核心性质：compliance 使刚度与子步数无关。
            float Run(int sub)
            {
                var s = new XpbdSolver();
                var p = new XpbdSolver.Particle[2];
                p[0] = new XpbdSolver.Particle { Position = new RVec3(0f, 3f, 0f), Velocity = RVec3.Zero, Previous = new RVec3(0f, 3f, 0f), InvMass = 1f };
                p[1] = new XpbdSolver.Particle { Position = new RVec3(1.5f, 3f, 0f), Velocity = RVec3.Zero, Previous = new RVec3(1.5f, 3f, 0f), InvMass = 1f };
                var c = new XpbdSolver.DistanceConstraint[1];
                c[0] = new XpbdSolver.DistanceConstraint { A = 0, B = 1, RestLength = 1f, Compliance = 1e-8f, Lambda = 0f };
                s.SetParticles(p); s.SetConstraints(c);
                s.Step(1f / 60f, sub, 10, 0f);
                return (s.Particles[0].Position - s.Particles[1].Position).Length();
            }
            float a = Run(2), b = Run(8);
            Require(MathF.Abs(a - b) < 0.05f, "XPBD 子步依赖过大: " + a + " vs " + b);
        }

        public static void TestRealitySpatialHashDeterministic()
        {
            var g = new SpatialHashGrid();
            var px = new float[] { 0.1f, 0.15f, 1.2f, 0.12f, 0.9f };
            var py = new float[] { 0.1f, 0.1f, 0.1f, 0.1f, 0.1f };
            var pz = new float[] { 0f, 0f, 0f, 0f, 0f };
            g.Build(px, py, pz, 5, 0.5f);
            var a = g.NeighborsOf(0);
            g.Build(px, py, pz, 5, 0.5f);
            var b = g.NeighborsOf(0);
            Require(a.Length == b.Length, "邻域数量不稳定");
            for (int i = 0; i < a.Length; i++) Require(a[i] == b[i], "邻域顺序不确定");
            for (int i = 1; i < a.Length; i++) Require(a[i] > a[i - 1], "邻域未按索引升序");
        }

        public static void TestRealityPbfDensityFiniteAndConverging()
        {
            var f = new PbfFluid { RestDensity = 1000f, ParticleMass = 10f, SmoothingLength = 0.3f, Epsilon = 100f };
            var px = new float[8]; var py = new float[8]; var pz = new float[8];
            var vx = new float[8]; var vy = new float[8]; var vz = new float[8];
            for (int i = 0; i < 8; i++)
            {
                px[i] = (i % 2) * 0.12f; py[i] = ((i / 2) % 2) * 0.12f; pz[i] = (i / 4) * 0.12f;
            }
            f.Step(px, py, pz, vx, vy, vz, 1f / 60f, 3, 0f);
            for (int i = 0; i < 8; i++)
                Require(!float.IsNaN(f.Density[i]) && !float.IsInfinity(f.Density[i]), "PBF 密度非有限值");
            Require(f.MaxDensityError() >= 0f, "密度误差为负");
            for (int i = 0; i < 8; i++)
                Require(!float.IsNaN(px[i]) && MathF.Abs(px[i]) < 10f, "PBF 位置发散");
        }

        public static void TestRealityThermalSofteningAndStress()
        {
            var l = new ConservationLedger();
            var t = new ThermoStructuralCoupler(RealityMaterial.Steel(), l);
            var temp = new float[] { 293.15f, 293.15f, 293.15f };
            var fuel = new float[] { 0f, 0f, 0f };
            t.Initialize(temp, fuel);
            float cold = t.StrengthAt(1);
            temp[1] = 1200f;
            t.Initialize(temp, fuel);
            float hot = t.StrengthAt(1);
            Require(hot < cold, "高温未导致强度退化: " + hot + " >= " + cold);
            Require(t.StressAt(1) > t.StressAt(0), "热应力未随温差上升");
        }

        public static void TestRealityFractureEnergyNonNegative()
        {
            var l = new ConservationLedger();
            var m = new FractureModel(1f, 3f, l);
            m.Initialize(2, 1e-5f);
            float e0 = m.Evaluate(0, 1e6f, 2.0e11f, 1e-4f);
            Require(e0 >= 0f, "断裂释放能为负");
            Require(!m.IsBroken(0), "低应力下不应断裂");
            float e1 = m.Evaluate(0, 1e9f, 2.0e11f, 1e-4f);
            Require(e1 >= 0f, "断裂释放能为负");
            Require(m.IsBroken(0), "高应力下应断裂");
            float e2 = m.Evaluate(0, 1e9f, 2.0e11f, 1e-4f);
            Require(e2 == 0f, "已断裂键仍重复释放能量");
        }

        public static void TestRealityNativeFallbackParity()
        {
            var pos = new float[] { 0f, 0f, 0f, 0.1f, 0f, 0f, 0f, 0.1f, 0f };
            float h = 0.3f;
            float poly6 = RealityKernels.Poly6Coef(h);
            var a = new float[3]; var b = new float[3];
            NativeKernel.ScalarDensityPass(pos, a, 3, h, 10f, poly6);
            NativeKernel.DensityPass(pos, b, 3, h, 10f, poly6);
            for (int i = 0; i < 3; i++) Require(a[i] == b[i], "原生/回退结果不一致 @" + i);
            Require(a[0] > 0f, "密度自贡献为零");
        }

        public static void TestRealityWorldDeterministicReplay()
        {
            float[] Run()
            {
                var w = new RealityPhysicsWorld();
                var p = new XpbdSolver.Particle[2];
                p[0] = new XpbdSolver.Particle { Position = new RVec3(0f, 1f, 0f), Velocity = RVec3.Zero, Previous = new RVec3(0f, 1f, 0f), InvMass = 1f };
                p[1] = new XpbdSolver.Particle { Position = new RVec3(0.8f, 1f, 0f), Velocity = RVec3.Zero, Previous = new RVec3(0.8f, 1f, 0f), InvMass = 1f };
                var c = new XpbdSolver.DistanceConstraint[1];
                c[0] = new XpbdSolver.DistanceConstraint { A = 0, B = 1, RestLength = 0.8f, Compliance = 0f, Lambda = 0f };
                w.Xpbd.SetParticles(p); w.Xpbd.SetConstraints(c);
                w.Thermal.Initialize(new float[] { 293.15f, 293.15f }, new float[] { 0f, 0f });
                w.Fracture.Initialize(2, 1e-6f);
                w.Step(1f / 60f); w.Step(1f / 60f);
                return w.Snapshot();
            }
            var a = Run(); var b = Run();
            Require(a.Length == b.Length, "快照长度不一致");
            for (int i = 0; i < a.Length; i++) Require(a[i] == b[i], "快照逐位不一致 @" + i);
        }
    }
}
