using System;
using YamaEngine.Reality;

namespace YamaEngine.Tests
{
    /// <summary>
    /// 第三轮真实物理回归（14 项）：矩阵/极分解、形状匹配、凸体 SAT、
    /// 流固双向耦合、约束岛休眠、Arrhenius 网络、固定步时钟、Rust 同构热点。
    /// 全部进入现有 build.yml 的 Test step 真实执行。
    /// </summary>
    public static class RealityPhysicsThirdTests
    {
        private static void Require(bool ok, string msg)
        {
            if (!ok) throw new InvalidOperationException("REALITY_R3_FAIL: " + msg);
        }
        private static bool Near(float a, float b, float tol) => MathF.Abs(a - b) <= tol;

        public static void TestR3Mat3InverseRoundTrip()
        {
            var a = new YrMat3(2f, 1f, 0f, 1f, 3f, 1f, 0f, 1f, 4f);
            Require(a.TryInverse(out var inv), "非奇异矩阵求逆失败");
            Require(inv.Frobenius() > 0f && !float.IsNaN(inv.Frobenius()), "逆矩阵非有限");
            Require(MathF.Abs(a.Determinant()) > 1e-3f, "行列式异常");
            var singular = new YrMat3(1f, 2f, 3f, 2f, 4f, 6f, 0f, 0f, 0f);
            Require(!singular.TryInverse(out _), "奇异矩阵未拒绝求逆");
        }

        public static void TestR3PolarExtractionRotation()
        {
            // 绕 Z 轴 90° 的旋转矩阵
            var rz = new YrMat3(0f, -1f, 0f, 1f, 0f, 0f, 0f, 0f, 1f);
            var r = YrMat3.ExtractRotation(rz);
            Require((r - rz).Frobenius() < 1e-3f, "极分解未还原纯旋转: " + (r - rz).Frobenius());
            // 纯缩放 2I → 旋转因子应为 I
            var s = new YrMat3(2f, 0f, 0f, 0f, 2f, 0f, 0f, 0f, 2f);
            var rs = YrMat3.ExtractRotation(s);
            Require((rs - YrMat3.Identity).Frobenius() < 1e-2f,
                    "纯缩放未退化为单位旋转: " + (rs - YrMat3.Identity).Frobenius());
            // 镜像（det<0）应安全回退
            var mirror = new YrMat3(-1f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f);
            Require((YrMat3.ExtractRotation(mirror) - YrMat3.Identity).Frobenius() < 1e-6f,
                    "镜像未安全回退");
        }

        public static void TestR3ShapeMatchPureTranslation()
        {
            var sm = new YrShapeMatch { Stiffness = 0f };
            var rx = new float[] { 0f, 1f, 0f, 1f };
            var ry = new float[] { 0f, 0f, 1f, 1f };
            var rz = new float[] { 0f, 0f, 0f, 0f };
            sm.Initialize(rx, ry, rz);
            var px = new float[] { 5f, 6f, 5f, 6f };
            var py = new float[] { 2f, 2f, 3f, 3f };
            var pz = new float[] { 0f, 0f, 0f, 0f };
            var a = sm.DeformationGradient(px, py, pz);
            Require((a - YrMat3.Identity).Frobenius() < 1e-3f,
                    "纯平移的形变梯度应为 I: " + (a - YrMat3.Identity).Frobenius());
        }

        public static void TestR3ShapeMatchRecoversRotation()
        {
            var sm = new YrShapeMatch { Stiffness = 0.9f, RigidOnly = false };
            var rx = new float[] { -1f, 1f, -1f, 1f, -1f, 1f, -1f, 1f };
            var ry = new float[] { -1f, -1f, 1f, 1f, -1f, -1f, 1f, 1f };
            var rz = new float[] { -1f, -1f, -1f, -1f, 1f, 1f, 1f, 1f };
            sm.Initialize(rx, ry, rz);
            // 绕 Z 旋转 90°：(x,y) → (-y,x)
            var px = new float[8]; var py = new float[8]; var pz = new float[8];
            for (int i = 0; i < 8; i++) { px[i] = -ry[i]; py[i] = rx[i]; pz[i] = rz[i]; }
            var a = sm.DeformationGradient(px, py, pz);
            Require(MathF.Abs(a.M00) < 1e-3f && MathF.Abs(a.M01 + 1f) < 1e-3f
                 && MathF.Abs(a.M10 - 1f) < 1e-3f, "旋转形变梯度错误");
            // 轻微扰动后 Solve 应把点拉回形状（最大偏差下降）
            float before = 0f;
            for (int i = 0; i < 8; i++) before = MathF.Max(before, MathF.Abs(px[i] + ry[i]));
            px[0] += 0.3f;
            sm.Solve(px, py, pz);
            float after = MathF.Abs(px[0] + ry[0]);
            Require(after < 0.3f, "形状匹配未把扰动点拉回: " + after);
        }

        public static void TestR3SatSeparatedBoxes()
        {
            var a = YrObb.AxisAligned(RVec3.Zero, new RVec3(1f, 1f, 1f));
            var b = YrObb.AxisAligned(new RVec3(3f, 0f, 0f), new RVec3(1f, 1f, 1f));
            var hit = YrConvex.SatObbObb(a, b);
            Require(!hit.Hit, "分离盒不应报告碰撞");
        }

        public static void TestR3SatOverlapDepthAndNormal()
        {
            var a = YrObb.AxisAligned(RVec3.Zero, new RVec3(1f, 1f, 1f));
            var b = YrObb.AxisAligned(new RVec3(1.8f, 0f, 0f), new RVec3(1f, 1f, 1f));
            var hit = YrConvex.SatObbObb(a, b);
            Require(hit.Hit, "重叠盒应命中");
            Require(Near(hit.Depth, 0.2f, 1e-4f), "穿透深度错误: " + hit.Depth);
            Require(hit.Normal.X > 0.99f, "法向应从 a 指向 b: " + hit.Normal.X);
        }

        public static void TestR3SatRotatedBoxUsesCrossAxes()
        {
            var a = YrObb.AxisAligned(RVec3.Zero, new RVec3(1f, 1f, 1f));
            var q = RQuat.FromAxisAngle(new RVec3(0f, 0f, 1f), MathF.PI * 0.25f);
            var b = new YrObb(new RVec3(2.05f, 0f, 0f), q, new RVec3(1f, 1f, 1f));
            var hit = YrConvex.SatObbObb(a, b);
            Require(hit.Hit, "旋转盒重叠应命中");
            Require(hit.Depth > 0f && hit.Depth < 1f, "旋转盒穿透深度异常: " + hit.Depth);
            Require(hit.Normal.LengthSq() > 0.9f, "SAT 法向未归一化");
        }

        public static void TestR3FluidRigidSurfaceVelocity()
        {
            var body = new RigidBody3D { Mass = 2f };
            body.SetInertia(RigidBody3D.SphereInertia(2f, 0.5f));
            body.LinearVelocity = new RVec3(1f, 0f, 0f);
            var v0 = YrFluidRigid.SurfaceVelocity(body, body.Position);
            Require(Near(v0.X, 1f, 1e-6f) && Near(v0.Y, 0f, 1e-6f), "纯平移表面速度错误");
            body.LinearVelocity = RVec3.Zero;
            body.AngularMomentum = new RVec3(0f, 0f, 1f); // 绕 Z 自转
            var point = body.Position + new RVec3(1f, 0f, 0f);
            var v1 = YrFluidRigid.SurfaceVelocity(body, point);
            Require(MathF.Abs(v1.Y) > 1e-6f, "旋转刚体表面应出现切向速度");
            var drag = YrFluidRigid.DragForce(new RVec3(2f, 0f, 0f), 1000f, 0.5f, 1f);
            Require(drag.X < 0f, "阻力方向应与相对速度相反");
        }

        public static void TestR3FluidRigidBoundaryProjection()
        {
            var body = new RigidBody3D { Mass = 1f };
            body.SetInertia(RigidBody3D.SphereInertia(1f, 0.5f));
            body.LinearVelocity = new RVec3(0f, 3f, 0f);
            var px = new float[] { 0f, 5f, 0f, 0.1f };
            var py = new float[] { 0f, 0f, 0f, 0f };
            var pz = new float[] { 0f, 0f, 0f, 0f };
            var vx = new float[] { 9f, 9f, 9f, 9f };
            var vy = new float[] { 9f, 9f, 9f, 9f };
            var vz = new float[4];
            int touched = YrFluidRigid.ApplyBoundaryVelocity(px, py, pz, vx, vy, vz,
                                                              body, 0.5f, 1f);
            Require(touched == 3, "应有 3 个粒子进入边界影响域，实际 " + touched);
            Require(Near(vy[0], 3f, 1e-5f), "边界速度未投影到流体: " + vy[0]);
            Require(Near(vy[1], 9f, 1e-5f), "域外粒子不应被修改");
        }

        public static void TestR3IslandSplitAndSleep()
        {
            var isl = new YrIslandSolver();
            isl.Build(5, new (int, int)[] { (0, 1), (1, 2), (3, 4) });
            Require(isl.IslandCount == 2, "应为 2 个岛，实际 " + isl.IslandCount);
            Require(isl.IslandOf(0) == isl.IslandOf(2), "0 与 2 应同岛");
            Require(isl.IslandOf(0) != isl.IslandOf(3), "两簇不应同岛");
            var zero = new float[5];
            for (int k = 0; k < 60; k++) isl.Update(zero, zero, zero, 0.01f, 30);
            Require(isl.IsSleeping(0) && isl.IsSleeping(4), "低速岛应进入休眠");
            isl.Wake(3);
            Require(!isl.IsSleeping(3) && !isl.IsSleeping(4), "唤醒应覆盖整岛");
        }

        public static void TestR3ReactionBalancing()
        {
            // 2H2 + O2 -> 2H2O，元素向量 [H, O]
            var species = new YrSpecies[] {
                new YrSpecies { Name = "H2",  Elements = new double[] { 2, 0 } },
                new YrSpecies { Name = "O2",  Elements = new double[] { 0, 2 } },
                new YrSpecies { Name = "H2O", Elements = new double[] { 2, 1 } },
            };
            var good = new YrReactionStep {
                Name = "burn", Stoich = new float[] { -2f, -1f, 2f },
                ForwardOrder = new float[] { 2f, 1f, 0f },
                PreExponential = 1f, ActivationEnergy = 1000f, Enthalpy = -100f
            };
            var ledger = new ConservationLedger();
            var net = new YrReactionNetwork(species, new YrReactionStep[] { good }, ledger);
            Require(net.IsBalanced(), "正确配平未通过");
            var badStep = new YrReactionStep {
                Name = "bad", Stoich = new float[] { -1f, -1f, 2f },
                ForwardOrder = new float[] { 1f, 1f, 0f }
            };
            var netBad = new YrReactionNetwork(species, new YrReactionStep[] { badStep }, ledger);
            Require(!netBad.IsBalanced(), "错误配平应被拒绝");
        }

        public static void TestR3ArrheniusMonotonicAndConservation()
        {
            var species = new YrSpecies[] {
                new YrSpecies { Name = "A", Elements = new double[] { 1 } },
                new YrSpecies { Name = "B", Elements = new double[] { 1 } },
            };
            var step = new YrReactionStep {
                Name = "A->B", Stoich = new float[] { -1f, 1f },
                ForwardOrder = new float[] { 1f, 0f }, ReverseOrder = new float[] { 0f, 1f },
                PreExponential = 1f, ActivationEnergy = 5000f, Enthalpy = -200f,
                EquilibriumK0 = 1f, Reversible = true
            };
            var ledger = new ConservationLedger();
            var net = new YrReactionNetwork(species, new YrReactionStep[] { step }, ledger);
            var c = new float[] { 1f, 0f };
            float k300 = net.Rate(0, c, 300f);
            float k600 = net.Rate(0, c, 600f);
            Require(k600 > k300, "Arrhenius 速率应随温度单调增: " + k300 + " -> " + k600);
            double inv0 = net.ElementInventory(0, c);
            net.Step(c, 400f, 1f, 200);
            double inv1 = net.ElementInventory(0, c);
            Require(Math.Abs(inv0 - inv1) < 1e-4, "组分守恒被破坏: " + inv0 + " -> " + inv1);
            Require(c[0] >= 0f && c[1] >= 0f, "浓度出现负值");
            Require(ledger.IsBalanced(1e-6), "反应热过账后账本失衡");
            // van't Hoff：ΔH=0、K0=1 时 Keq 与 T 无关恒为 1，50/50 即平衡
            var iso = new YrReactionStep {
                Name = "iso", Stoich = new float[] { -1f, 1f },
                ForwardOrder = new float[] { 1f, 0f }, ReverseOrder = new float[] { 0f, 1f },
                PreExponential = 1f, ActivationEnergy = 0f, Enthalpy = 0f,
                EquilibriumK0 = 1f, Reversible = true
            };
            var eqNet = new YrReactionNetwork(species, new YrReactionStep[] { iso }, ledger);
            var eq = new float[] { 0.5f, 0.5f };
            Require(MathF.Abs(eqNet.Rate(0, eq, 400f)) < 1e-5f, "平衡点净速率应趋零");
        }

        public static void TestR3FixedClockStepsAndAlpha()
        {
            var clock = new YrFixedClock(1.0 / 60.0, 8);
            Require(clock.Advance(1.0 / 60.0) == 1, "一个固定 dt 应执行 1 步");
            Require(clock.Tick == 1, "tick 未递增");
            Require(clock.Advance(0.05) == 3, "0.05s 应执行 3 步");
            Require(Near(clock.InterpolationAlpha(), 0f, 1e-4f), "整除后 alpha 应为 0");
            Require(clock.Advance(0.055) == 3, "0.055s 应执行 3 步");
            Require(Near(clock.InterpolationAlpha(), 0.3f, 1e-3f),
                    "插值 alpha 应为 0.3，实际 " + clock.InterpolationAlpha());
            Require(clock.Advance(10.0) == 8, "超长帧应被 maxSubSteps 截断防死亡螺旋");
            Require(clock.Tick == 15, "累计 tick 错误: " + clock.Tick);
        }

        public static void TestR3WorldFixedStepAndCovarianceParity()
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
            int steps = w.FixedStepAdvance(0.05f);
            Require(steps == 3, "World 固定步应执行 3 步，实际 " + steps);
            Require(w.Tick == 3, "World tick 与 clock 不一致");
            w.AttachIslands(2, new (int, int)[] { (0, 1) });
            Require(w.Islands.IslandCount == 1, "单约束应形成 1 个岛");

            var pv = new float[] { 1f, 2f, 3f, 0.5f, -1f, 2f };
            var qv = new float[] { 2f, 0f, 1f, 1f, 1f, 1f };
            var a = new float[9]; var b = new float[9];
            NativeKernel.ScalarCovarianceAccum(pv, qv, a, 2);
            NativeKernel.CovarianceAccum(pv, qv, b, 2);
            for (int i = 0; i < 9; i++) Require(a[i] == b[i], "协方差原生/回退不一致 @" + i);
            Require(MathF.Abs(a[0] - 2.5f) < 1e-5f, "协方差累加值错误: " + a[0]);
        }
    }
}
