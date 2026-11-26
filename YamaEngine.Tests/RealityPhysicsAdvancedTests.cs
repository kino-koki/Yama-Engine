using System;
using YamaEngine.Reality;

namespace YamaEngine.Tests
{
    /// <summary>
    /// 第二批真实物理能力的回归测试：刚体角动量、摩擦锥、CCD、状态方程、
    /// 热浮力/对流/涡量、CSR 邻域加速一致性。全部进入 CI 的 Test step 真实执行。
    /// </summary>
    public static class RealityPhysicsAdvancedTests
    {
        private static void Require(bool ok, string msg)
        {
            if (!ok) throw new InvalidOperationException("REALITY_ADV_FAIL: " + msg);
        }
        private static bool Near(float a, float b, float tol) => MathF.Abs(a - b) <= tol;

        public static void TestAdvAngularMomentumConservedExactly()
        {
            var b = new RigidBody3D { Mass = 2f };
            b.SetInertia(RigidBody3D.BoxInertia(2f, 1f, 2f, 3f));
            b.Orientation = RQuat.FromAxisAngle(new RVec3(0f, 1f, 0f), 0.7f);
            b.AngularMomentum = new RVec3(0.3f, 0.8f, -0.2f);
            RVec3 l0 = b.AngularMomentum;
            for (int s = 0; s < 2000; s++) b.Integrate(1f / 1000f);
            Require(b.AngularMomentum.X == l0.X && b.AngularMomentum.Y == l0.Y
                 && b.AngularMomentum.Z == l0.Z, "无力矩时角动量未逐位守恒");
        }

        public static void TestAdvAnisotropicPrecession()
        {
            var b = new RigidBody3D { Mass = 1f };
            b.SetInertia(RigidBody3D.BoxInertia(1f, 1f, 3f, 5f)); // 强各向异性
            b.Orientation = RQuat.FromAxisAngle(new RVec3(0f, 0f, 1f), 0.9f);
            b.AngularMomentum = new RVec3(0.6f, 0.4f, 0.1f);
            RVec3 w0 = b.AngularVelocityWorld();
            for (int s = 0; s < 400; s++) b.Integrate(1f / 1000f);
            RVec3 w1 = b.AngularVelocityWorld();
            float dw = (w1 - w0).Length();
            Require(dw > 1e-3f, "各向异性刚体未出现自由进动（把 ω 当常量是错误简化）");
        }

        public static void TestAdvTorqueChangesAngularMomentum()
        {
            var b = new RigidBody3D();
            b.SetInertia(RigidBody3D.SphereInertia(1f, 0.5f));
            b.ApplyTorque(new RVec3(0f, 2f, 0f));
            b.Integrate(0.25f);
            Require(Near(b.AngularMomentum.Y, 0.5f, 1e-6f), "力矩未按 τ·dt 改变角动量");
        }

        public static void TestAdvQuaternionNormalization()
        {
            var b = new RigidBody3D();
            b.SetInertia(RigidBody3D.SphereInertia(1f, 0.5f));
            b.AngularMomentum = new RVec3(5f, 3f, 2f);
            for (int s = 0; s < 500; s++) b.Integrate(1f / 240f);
            var q = b.Orientation;
            float n2 = q.X * q.X + q.Y * q.Y + q.Z * q.Z + q.W * q.W;
            Require(Near(n2, 1f, 1e-4f), "四元数未归一化: " + n2);
        }

        public static void TestAdvCoulombStaticStopsSliding()
        {
            var n = new RVec3(0f, 1f, 0f);
            var vrel = new RVec3(0.2f, -1f, 0f); // 轻微切向 + 法向下落
            float jn = ContactFriction.NormalImpulse(vrel, n, 0f, 1f);
            Require(Near(jn, 1f, 1e-5f), "恢复系数 0 的法向冲量错误: " + jn);
            var jt = ContactFriction.TangentImpulse(vrel, n, jn, 1f, 0.8f, 0.5f);
            Require(Near(jt.Length(), 0.2f, 1e-4f), "静摩擦未完全止滑: " + jt.Length());
            Require(ContactFriction.InsideCone(jt, jn, 0.8f), "静摩擦冲量越出摩擦锥");
        }

        public static void TestAdvCoulombDynamicClamp()
        {
            var n = new RVec3(0f, 1f, 0f);
            var vrel = new RVec3(10f, -1f, 0f);
            float jn = ContactFriction.NormalImpulse(vrel, n, 1f, 1f);
            Require(Near(jn, 2f, 1e-5f), "恢复系数 1 的法向冲量应为 2");
            var jt = ContactFriction.TangentImpulse(vrel, n, jn, 1f, 0.8f, 0.5f);
            Require(Near(jt.Length(), 1.0f, 1e-4f), "动摩擦未按 μ_d·|jn| 截断: " + jt.Length());
            Require(jt.X < 0f, "摩擦方向应与切向速度相反");
        }

        public static void TestAdvCcdSpherePlane()
        {
            var p0 = new RVec3(0f, 2f, 0f);
            var v = new RVec3(0f, -10f, 0f);
            var n = new RVec3(0f, 1f, 0f);
            float toi = ContinuousCollision.SweptSpherePlane(p0, v, 0.5f, n, 0f, 1f);
            Require(Near(toi, 0.15f, 1e-5f), "球-平面 TOI 错误: " + toi);
            float parallel = ContinuousCollision.SweptSpherePlane(
                p0, new RVec3(10f, 0f, 0f), 0.5f, n, 0f, 1f);
            Require(parallel < 0f, "平行掠过不应报告碰撞");
            float tooLate = ContinuousCollision.SweptSpherePlane(
                p0, new RVec3(0f, -1f, 0f), 0.5f, n, 0f, 0.5f);
            Require(tooLate < 0f, "超出 dt 的碰撞不应报告");
        }

        public static void TestAdvCcdSphereSphere()
        {
            float toi = ContinuousCollision.SweptSphereSphere(
                new RVec3(0f, 0f, 0f), new RVec3(4f, 0f, 0f), 0.5f,
                new RVec3(3f, 0f, 0f), RVec3.Zero, 0.5f, 1f);
            Require(Near(toi, 0.5f, 1e-5f), "球-球 TOI 错误: " + toi);
            float miss = ContinuousCollision.SweptSphereSphere(
                new RVec3(0f, 0f, 0f), new RVec3(4f, 0f, 0f), 0.5f,
                new RVec3(3f, 5f, 0f), RVec3.Zero, 0.5f, 1f);
            Require(miss < 0f, "错开的球不应报告碰撞");
        }

        public static void TestAdvTaitEquationOfState()
        {
            var eos = new EquationOfState(1000f, 100f, 7f);
            Require(Near(eos.Pressure(1000f), 0f, 1e-3f), "ρ=ρ0 时压力应为 0");
            Require(eos.Pressure(1100f) > 0f, "压缩时压力应为正");
            Require(eos.Pressure(900f) < 0f, "膨胀时压力应为负（Tait 允许拉应力）");
            Require(Near(eos.LocalSoundSpeed(1000f), 100f, 1e-2f), "ρ0 处声速应为 c0");
            Require(EquationOfState.CohesionFactor(0.2f, 0.2f, 1f) == 0f, "r>=h 时表面张力应为 0");
            Require(EquationOfState.CohesionFactor(1e-12f, 0.2f, 1f) == 0f, "r→0 不应有奇点");
            Require(EquationOfState.CohesionFactor(0.05f, 0.2f, 1f) > 0f, "邻域内应有内向凝聚力");
        }

        public static void TestAdvBoussinesqBuoyancy()
        {
            var g = ThermoFluidCoupler.DefaultGravity;
            var cold = ThermoFluidCoupler.BuoyancyAcceleration(293.15f, 293.15f, 1e-3f, g);
            Require(cold.Length() < 1e-9f, "参考温度不应产生浮力");
            var hot = ThermoFluidCoupler.BuoyancyAcceleration(393.15f, 293.15f, 1e-3f, g);
            Require(hot.Y > 0f, "热流体应向上浮");
            var cold2 = ThermoFluidCoupler.BuoyancyAcceleration(193.15f, 293.15f, 1e-3f, g);
            Require(cold2.Y < 0f, "冷流体应下沉");
        }

        public static void TestAdvAdvectionIdentityAtZeroVelocity()
        {
            var field = new float[] { 1f, 2f, 3f, 4f, 5f, 6f, 7f, 8f };
            var zero = new float[8];
            var dst = ThermoFluidCoupler.AdvectScalar(field, zero, zero, zero, 2, 2, 2, 1f, 0.1f);
            for (int i = 0; i < 8; i++) Require(dst[i] == field[i], "零速度场对流应逐位不变 @" + i);
        }

        public static void TestAdvVorticityConfinementZeroOnUniformField()
        {
            const int n = 27;
            var vx = new float[n]; var vy = new float[n]; var vz = new float[n];
            for (int i = 0; i < n; i++) vx[i] = 1f; // 均匀平移场
            var fx = new float[n]; var fy = new float[n]; var fz = new float[n];
            ThermoFluidCoupler.VorticityConfinement(vx, vy, vz, 3, 3, 3, 1f, 0.5f, fx, fy, fz);
            for (int i = 0; i < n; i++)
                Require(MathF.Abs(fx[i]) + MathF.Abs(fy[i]) + MathF.Abs(fz[i]) < 1e-6f,
                        "均匀场不应产生涡量约束力 @" + i);
        }

        public static void TestAdvCsrDensityMatchesFullPairwise()
        {
            var rnd = new float[] {
                0.00f, 0.00f, 0.00f,
                0.10f, 0.05f, 0.00f,
                0.05f, 0.12f, 0.03f,
                0.40f, 0.00f, 0.00f,
                0.00f, 0.45f, 0.00f,
                0.42f, 0.42f, 0.00f
            };
            int n = 6;
            float h = 0.3f;
            float poly6 = RealityKernels.Poly6Coef(h);
            var a = new float[n]; var b = new float[n];
            NativeKernel.ScalarDensityPass(rnd, a, n, h, 10f, poly6);
            var csr = NativeKernel.BuildNeighborCsr(
                new float[] { rnd[0], rnd[3], rnd[6], rnd[9], rnd[12], rnd[15] },
                new float[] { rnd[1], rnd[4], rnd[7], rnd[10], rnd[13], rnd[16] },
                new float[] { rnd[2], rnd[5], rnd[8], rnd[11], rnd[14], rnd[17] },
                n, h);
            NativeKernel.DensityPassCsr(rnd, csr.offsets, csr.indices, b, n, h, 10f, poly6);
            for (int i = 0; i < n; i++)
                Require(a[i] == b[i], "CSR 邻域与全对密度不一致 @" + i + " : " + a[i] + " vs " + b[i]);
            Require(csr.offsets[n] >= 0, "CSR offsets 非法");
        }

        public static void TestAdvWorldBuoyancyStepIsFinite()
        {
            var w = new RealityPhysicsWorld();
            var p = new XpbdSolver.Particle[2];
            p[0] = new XpbdSolver.Particle { Position = new RVec3(0f, 1f, 0f), Velocity = RVec3.Zero, Previous = new RVec3(0f, 1f, 0f), InvMass = 1f };
            p[1] = new XpbdSolver.Particle { Position = new RVec3(0.8f, 1f, 0f), Velocity = RVec3.Zero, Previous = new RVec3(0.8f, 1f, 0f), InvMass = 1f };
            var c = new XpbdSolver.DistanceConstraint[1];
            c[0] = new XpbdSolver.DistanceConstraint { A = 0, B = 1, RestLength = 0.8f, Compliance = 0f, Lambda = 0f };
            w.Xpbd.SetParticles(p); w.Xpbd.SetConstraints(c);
            w.Thermal.Initialize(new float[] { 293.15f, 393.15f }, new float[] { 0f, 0f });
            w.Fracture.Initialize(2, 1e-6f);
            var px = new float[] { 0f, 0.1f }; var py = new float[] { 0.5f, 0.5f }; var pz = new float[] { 0f, 0f };
            var vx = new float[2]; var vy = new float[2]; var vz = new float[2];
            w.StepWithBuoyancy(px, py, pz, vx, vy, vz, 1f / 60f, 2, 293.15f, 1e-3f);
            for (int i = 0; i < 2; i++)
                Require(!float.IsNaN(px[i]) && !float.IsNaN(vy[i]) && MathF.Abs(px[i]) < 10f,
                        "热浮力闭环步输出非有限值 @" + i);
            Require(w.Tick == 1, "闭环步未推进 tick");
        }
    }
}
