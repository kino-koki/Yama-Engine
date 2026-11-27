using System;
using YamaEngine.Reality;

namespace YamaEngine.Tests
{
    /// <summary>
    /// 第四轮真实物理回归（14 项）：序列冲量接触链、关节自由度、
    /// 四面体体积约束、燃烧端到端场景、确定性回滚。
    /// 全部进入现有 build.yml 的 Test step 真实执行。
    /// </summary>
    public static class RealityPhysicsFourthTests
    {
        private static void Require(bool ok, string msg)
        {
            if (!ok) throw new InvalidOperationException("REALITY_R4_FAIL: " + msg);
        }
        private static bool Near(float a, float b, float tol) => MathF.Abs(a - b) <= tol;

        private static RigidBody3D MakeBody(RVec3 position, float mass)
        {
            var b = new RigidBody3D { Position = position, Mass = mass };
            b.SetInertia(RigidBody3D.SphereInertia(mass, 0.5f));
            return b;
        }

        public static void TestR4ContactRestitutionBounces()
        {
            var a = MakeBody(new RVec3(-0.5f, 0f, 0f), 1f);
            var b = MakeBody(new RVec3(0.5f, 0f, 0f), 1f);
            a.LinearVelocity = new RVec3(2f, 0f, 0f);
            b.LinearVelocity = new RVec3(-2f, 0f, 0f);
            var bodies = new RigidBody3D[] { a, b };
            var c = new YrContact[1];
            c[0] = new YrContact {
                BodyA = 0, BodyB = 1, Point = RVec3.Zero,
                Normal = new RVec3(1f, 0f, 0f), Depth = 0.01f,
                Restitution = 1f, FrictionStatic = 0f, FrictionDynamic = 0f
            };
            var solver = new YrContactSolver();
            solver.SetBodies(bodies); solver.SetContacts(c);
            solver.Prepare(); solver.Solve(12);
            float vrel = (b.LinearVelocity - a.LinearVelocity).X;
            Require(vrel > 3.5f, "完全弹性碰撞应分离，vrel=" + vrel);
            Require(solver.Contacts[0].NormalLambda > 0f, "法向累积冲量应为正");
        }

        public static void TestR4ContactInelasticStopsApproach()
        {
            var a = MakeBody(new RVec3(-0.5f, 0f, 0f), 1f);
            var b = MakeBody(new RVec3(0.5f, 0f, 0f), 1f);
            a.LinearVelocity = new RVec3(2f, 0f, 0f);
            b.LinearVelocity = new RVec3(-2f, 0f, 0f);
            var c = new YrContact[1];
            c[0] = new YrContact {
                BodyA = 0, BodyB = 1, Point = RVec3.Zero,
                Normal = new RVec3(1f, 0f, 0f), Depth = 0.01f,
                Restitution = 0f, FrictionStatic = 0f, FrictionDynamic = 0f
            };
            var s = new YrContactSolver();
            s.SetBodies(new RigidBody3D[] { a, b }); s.SetContacts(c);
            s.Prepare(); s.Solve(12);
            float vrel = (b.LinearVelocity - a.LinearVelocity).X;
            Require(Near(vrel, 0f, 1e-4f), "完全非弹性应消除法向接近: " + vrel);
        }

        public static void TestR4ContactConservesTotalMomenta()
        {
            var a = MakeBody(new RVec3(-1f, 0.2f, 0f), 2f);
            var b = MakeBody(new RVec3(1f, -0.3f, 0.4f), 3f);
            a.LinearVelocity = new RVec3(1f, 0.4f, -0.2f);
            b.LinearVelocity = new RVec3(-0.7f, 0.1f, 0.5f);
            a.AngularMomentum = new RVec3(0.2f, -0.1f, 0.3f);
            b.AngularMomentum = new RVec3(-0.3f, 0.2f, 0.1f);
            var bodies = new RigidBody3D[] { a, b };
            RVec3 p0 = YrContactSolver.TotalLinearMomentum(bodies);
            RVec3 l0 = YrContactSolver.TotalAngularMomentum(bodies);
            var c = new YrContact[1];
            c[0] = new YrContact {
                BodyA = 0, BodyB = 1, Point = new RVec3(0f, 0.05f, 0.1f),
                Normal = new RVec3(0.8f, 0.5f, 0.3f), Depth = 0.02f,
                Restitution = 0.4f, FrictionStatic = 0.6f, FrictionDynamic = 0.4f
            };
            var s = new YrContactSolver();
            s.SetBodies(bodies); s.SetContacts(c);
            s.Prepare(); s.Solve(20);
            RVec3 p1 = YrContactSolver.TotalLinearMomentum(bodies);
            RVec3 l1 = YrContactSolver.TotalAngularMomentum(bodies);
            Require((p1 - p0).Length() < 1e-4f, "总线动量不守恒: " + (p1 - p0).Length());
            Require((l1 - l0).Length() < 1e-4f,
                    "总角动量(自旋+轨道)不守恒: " + (l1 - l0).Length());
        }

        public static void TestR4FrictionConeProjection()
        {
            var a = MakeBody(new RVec3(-0.5f, 0f, 0f), 1f);
            var b = MakeBody(new RVec3(0.5f, 0f, 0f), 1f);
            a.LinearVelocity = new RVec3(4f, 0f, 0f);
            b.LinearVelocity = new RVec3(-4f, 8f, 0f); // 大切向速度
            var c = new YrContact[1];
            c[0] = new YrContact {
                BodyA = 0, BodyB = 1, Point = RVec3.Zero,
                Normal = new RVec3(1f, 0f, 0f), Depth = 0f,
                Restitution = 0f, FrictionStatic = 0.8f, FrictionDynamic = 0.2f
            };
            var s = new YrContactSolver();
            s.SetBodies(new RigidBody3D[] { a, b }); s.SetContacts(c);
            s.Prepare(); s.Solve(20);
            float l1 = s.Contacts[0].TangentLambda1;
            float l2 = s.Contacts[0].TangentLambda2;
            float len = MathF.Sqrt(l1 * l1 + l2 * l2);
            float limit = s.Contacts[0].FrictionDynamic * s.Contacts[0].NormalLambda;
            Require(len <= limit + 1e-4f, "切向冲量越出二维库仑锥: " + len + " > " + limit);
            Require(s.Contacts[0].NormalLambda > 0f, "法向冲量为零则摩擦无意义");
        }

        public static void TestR4WarmStartAppliesCachedImpulse()
        {
            YrContact Make(float lambda) => new YrContact {
                BodyA = 0, BodyB = 1, Point = RVec3.Zero,
                Normal = new RVec3(1f, 0f, 0f), Depth = 0f,
                Restitution = 0f, FrictionStatic = 0f, FrictionDynamic = 0f,
                NormalLambda = lambda
            };
            float Run(float warm, out float afterPrepare, out float finalLambda,
                      out float finalRelativeVelocity)
            {
                var a = MakeBody(new RVec3(-0.5f, 0f, 0f), 1f);
                var b = MakeBody(new RVec3(0.5f, 0f, 0f), 1f);
                a.LinearVelocity = new RVec3(3f, 0f, 0f);
                b.LinearVelocity = new RVec3(-3f, 0f, 0f);
                var c = new YrContact[] { Make(warm) };
                var s = new YrContactSolver();
                s.SetBodies(new RigidBody3D[] { a, b }); s.SetContacts(c);
                s.Prepare();
                afterPrepare = (b.LinearVelocity - a.LinearVelocity).X;
                s.Solve(4);
                finalLambda = s.Contacts[0].NormalLambda;
                finalRelativeVelocity = (b.LinearVelocity - a.LinearVelocity).X;
                return finalRelativeVelocity;
            }
            float coldP, coldL, coldV;
            Run(0f, out coldP, out coldL, out coldV);
            float warmP, warmL, warmV;
            Run(2f, out warmP, out warmL, out warmV);

            // 线性单接触一次迭代必精确，"warm 减少残差" 其实是伪命题；
            // 真正该验的是：缓存冲量被施加、累积 λ 不被重置、两种初态都收敛。
            Require(MathF.Abs(warmP) < MathF.Abs(coldP),
                    "warm start 缓存冲量应被施加: " + warmP + " vs " + coldP);
            Require(warmL >= 2f - 1e-4f, "缓存 λ 应被保留而非重置: " + warmL);
            Require(MathF.Abs(coldV) < 1e-4f && MathF.Abs(warmV) < 1e-4f,
                    "两种初态都应收敛到零法向相对速度");
            Require(Near(coldL, warmL, 1e-4f), "最终 λ 应与初值无关（线性约束唯一解）");
        }

        public static void TestR4PositionCorrectionDoesNotInjectEnergy()
        {
            var a = MakeBody(new RVec3(-0.5f, 0f, 0f), 1f);
            var b = MakeBody(new RVec3(0.5f, 0f, 0f), 1f);
            float e0 = a.KineticEnergy() + b.KineticEnergy();
            var c = new YrContact[1];
            c[0] = new YrContact {
                BodyA = 0, BodyB = 1, Point = RVec3.Zero,
                Normal = new RVec3(1f, 0f, 0f), Depth = 0.2f
            };
            var s = new YrContactSolver();
            s.SetBodies(new RigidBody3D[] { a, b }); s.SetContacts(c);
            s.CorrectPositions(0.8f, 0f);
            float e1 = a.KineticEnergy() + b.KineticEnergy();
            Require(Near(e1, e0, 1e-6f), "位置投影不应注入动能");
            Require((b.Position - a.Position).X > 1f, "穿透未被分离修正");
        }

        public static void TestR4BallJointPositionProjection()
        {
            var a = MakeBody(RVec3.Zero, 1f);
            var b = MakeBody(new RVec3(0f, 1.5f, 0f), 1f);
            var j = new YrJoint[1];
            j[0] = new YrJoint {
                A = 0, B = 1, LocalAnchorA = new RVec3(0f, 0.5f, 0f),
                LocalAnchorB = new RVec3(0f, -0.5f, 0f),
                Axis = new RVec3(0f, 1f, 0f), Kind = YrJointKind.Ball, Beta = 0f
            };
            var s = new YrJointSolver();
            s.SetBodies(new RigidBody3D[] { a, b }); s.SetJoints(j);
            float before = s.AnchorError(j[0]);
            for (int k = 0; k < 12; k++) s.CorrectPositions(0.5f);
            float after = s.AnchorError(j[0]);
            Require(after < before * 0.05f, "球窝锚点误差未收敛: " + before + " -> " + after);
        }

        public static void TestR4SliderKeepsAxialFreedom()
        {
            var a = MakeBody(RVec3.Zero, 1f);
            var b = MakeBody(new RVec3(0f, 1f, 0f), 1f);
            b.LinearVelocity = new RVec3(2f, 3f, 0f); // x 横向, y 轴向
            var j = new YrJoint[1];
            j[0] = new YrJoint {
                A = 0, B = 1, LocalAnchorA = RVec3.Zero, LocalAnchorB = RVec3.Zero,
                Axis = new RVec3(0f, 1f, 0f), Kind = YrJointKind.Slider, Beta = 0f
            };
            var s = new YrJointSolver();
            s.SetBodies(new RigidBody3D[] { a, b }); s.SetJoints(j);
            s.Solve(1f / 60f, 12);
            // 成对冲量消的是「相对速度」，不是让某个刚体静止：
            // 等质量时两者各分一半冲量，最终同速滑行。断言绝对速度是错的。
            RVec3 vrel = b.LinearVelocity - a.LinearVelocity;
            Require(MathF.Abs(vrel.X) < 0.1f,
                    "滑块应压住横向相对速度: " + vrel.X);
            Require(vrel.Y > 2.5f,
                    "滑块不应吃掉轴向相对速度: " + vrel.Y);
        }

        public static void TestR4HingeLocksTransverseAngularVelocity()
        {
            var a = MakeBody(RVec3.Zero, 1f);
            var b = MakeBody(new RVec3(0f, 1f, 0f), 1f);
            // 必须给轴向(Y)分量，否则「保留绕轴自转」无从验证；
            // 横向 x/z 分量才是铰链要锁掉的部分。
            b.AngularMomentum = new RVec3(1f, 0.8f, 0.5f);
            var j = new YrJoint[1];
            j[0] = new YrJoint {
                A = 0, B = 1, LocalAnchorA = RVec3.Zero, LocalAnchorB = RVec3.Zero,
                Axis = new RVec3(0f, 1f, 0f), Kind = YrJointKind.Hinge, Beta = 0f
            };
            var s = new YrJointSolver();
            s.SetBodies(new RigidBody3D[] { a, b }); s.SetJoints(j);
            s.Solve(1f / 60f, 16);
            RVec3 w = b.AngularVelocityWorld() - a.AngularVelocityWorld();
            var basis = YrContactSolver.BasisFromAxis(new RVec3(0f, 1f, 0f));
            Require(MathF.Abs(w.Dot(basis.T1)) < 1e-3f
                 && MathF.Abs(w.Dot(basis.T2)) < 1e-3f,
                    "铰链应锁死垂直于轴的相对角速度");
            Require(MathF.Abs(w.Y) > 1e-4f, "铰链应保留绕轴自转");
        }

        public static void TestR4VolumeGradientsSumToZero()
        {
            var vol = new YrVolumeConstraint();
            var px = new float[] { 0f, 1f, 0f, 0f };
            var py = new float[] { 0f, 0f, 1f, 0f };
            var pz = new float[] { 0f, 0f, 0f, 1f };
            vol.Initialize(new int[] { 0, 1, 2, 3 }, px, py, pz, 0f);
            var g = vol.Gradients(0, px, py, pz);
            RVec3 sum = RVec3.Zero;
            for (int i = 0; i < 4; i++) sum = sum + g[i];
            Require(sum.Length() < 1e-6f, "体积梯度未平移不变: " + sum.Length());
        }

        public static void TestR4VolumeConstraintRecoversVolume()
        {
            var vol = new YrVolumeConstraint();
            var px = new float[] { 0f, 1f, 0f, 0f };
            var py = new float[] { 0f, 0f, 1f, 0f };
            var pz = new float[] { 0f, 0f, 0f, 1f };
            vol.Initialize(new int[] { 0, 1, 2, 3 }, px, py, pz, 0f);
            float rest = vol.TotalVolume(px, py, pz);
            Require(rest > 0f, "初始体积应为正");
            // 整体放大 2 倍 → 体积 ×8
            for (int i = 0; i < 4; i++) { px[i] *= 2f; py[i] *= 2f; pz[i] *= 2f; }
            float swollen = vol.TotalVolume(px, py, pz);
            Require(Near(swollen, rest * 8f, 1e-5f), "缩放体积应为 8 倍: " + swollen);
            vol.Solve(px, py, pz, null, 1f / 60f, 30);
            float after = vol.TotalVolume(px, py, pz);
            Require(MathF.Abs(after - rest) < 1e-3f,
                    "体积约束未恢复: " + after + " vs " + rest);
        }

        public static void TestR4CombustionScenarioIsConservativeAndDeterministic()
        {
            float[] Run(int steps, out YrScenarioFrame last)
            {
                var sc = new YrCombustionScenario(6, 293.15f, 2f,
                    new float[] { 1f, 2f, 3f, 2f, 1f, 1f }, 5e5f, 1e5f);
                YrScenarioFrame f = default;
                for (int k = 0; k < steps; k++) f = sc.Step(1f / 60f);
                last = f;
                return sc.Snapshot();
            }
            YrScenarioFrame f1;
            var a = Run(40, out f1);
            var b = Run(40, out _);
            Require(a.Length == b.Length, "场景快照长度不一致");
            for (int i = 0; i < a.Length; i++)
                Require(a[i] == b[i], "场景快照逐位不一致 @" + i);
            Require(f1.BurnedFuel > 0f, "燃烧未推进");
            Require(f1.PeakTemperature > 293.15f, "温度未因反应热上升");
            Require(f1.LedgerResidual < 1e-6, "场景账本失衡: " + f1.LedgerResidual);
            Require(f1.BrokenCount >= 0, "断裂计数非法");
        }

        public static void TestR4NoFuelScenarioDoesNotHeat()
        {
            var sc = new YrCombustionScenario(4, 300f, 0f, null, 5e5f, 1e5f);
            YrScenarioFrame f = default;
            for (int k = 0; k < 30; k++) f = sc.Step(1f / 60f);
            Require(Near(f.PeakTemperature, 300f, 1e-3f),
                    "无燃料时温度不应自发上升（杜绝隐式能量）: " + f.PeakTemperature);
            Require(f.BurnedFuel == 0f, "无燃料不应有燃烧进度");
        }

        public static void TestR4RollbackChecksumAndRewind()
        {
            var state = new float[] { 1f, 2f, 3f };
            ulong h0 = YrRollbackBuffer.Checksum(state, 0);
            var altered = new float[] { 1f, 2f, 3.000001f };
            ulong h1 = YrRollbackBuffer.Checksum(altered, 0);
            Require(h0 != h1, "位级校验和未捕捉微小差异");
            var negZero = new float[] { -0f, 0f, 0f };
            Require(YrRollbackBuffer.Checksum(negZero, 0)
                    != YrRollbackBuffer.Checksum(new float[] { 0f, 0f, 0f }, 0),
                    "-0 与 +0 应被视为不同位模式");

            var buf = new YrRollbackBuffer();
            Func<float[], int, float[]> step = (st, k) => {
                var next = new float[st.Length];
                for (int i = 0; i < st.Length; i++)
                    next[i] = st[i] * 0.999f + i * 0.001f + k * 1e-6f;
                return next;
            };
            var hashes = new ulong[13];
            var cur = (float[])state.Clone();
            for (int k = 0; k <= 12; k++)
            {
                buf.Push(k, cur);
                hashes[k] = YrRollbackBuffer.Checksum(cur, k);
                cur = step(cur, k);
            }
            Require(buf.Count == 13, "帧数错误");
            int removed = buf.Rewind(7);
            Require(removed == 5 && buf.Count == 8, "Rewind 未正确丢弃未来帧");
            Require(buf.TryGet(7, out float[] s7), "未找到 tick 7 快照");

            // 从 tick7 回滚重放，逐 tick 校验和必须与原始序列 8..12 完全一致
            var replay = (float[])s7.Clone();
            for (int k = 7; k < 12; k++)
            {
                replay = step(replay, k);
                ulong rh = YrRollbackBuffer.Checksum(replay, k + 1);
                Require(rh == hashes[k + 1], "回滚重放校验和分叉 @" + (k + 1));
            }
            Require(YrRollbackBuffer.VerifyDeterministic(step, state, 12),
                    "step 函数非逐位确定");
        }
    }
}
