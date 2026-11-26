using System;
using YamaEngine.Reality;

namespace YamaEngine.Tests
{
    /// <summary>
    /// 第六轮回归（14 项）：15 轴扫掠 TOI、连续碰撞子步回退、
    /// 纯速度级求解下的严格动量/角动量守恒、确定性与回滚重放。
    /// 全部进入现有 build.yml 的 Test step 真实执行。
    /// </summary>
    public static class RealityPhysicsSixthTests
    {
        private static void Require(bool ok, string msg)
        {
            if (!ok) throw new InvalidOperationException("REALITY_R6_FAIL: " + msg);
        }
        private static bool Near(float a, float b, float tol) => MathF.Abs(a - b) <= tol;

        private static RigidBody3D Make(RVec3 pos, float mass)
        {
            var b = new RigidBody3D { Position = pos, Mass = mass };
            b.SetInertia(RigidBody3D.SphereInertia(mass, 0.5f));
            return b;
        }
        private static RVec3 H(float v) => new RVec3(v, v, v);

        private static YrObb Box(RigidBody3D b, float h)
            => new YrObb(b.Position, b.Orientation, H(h));

        public static void TestR6SweptToiHeadOnApproach()
        {
            var a = YrObb.AxisAligned(new RVec3(-5f, 0f, 0f), H(1f));
            var b = YrObb.AxisAligned(new RVec3(5f, 0f, 0f), H(1f));
            float t = YrSweptObb.ComputeToi(a, new RVec3(20f, 0f, 0f),
                                            b, RVec3.Zero, 0.65f);
            // x 投影半径和 = 2，初始间距 10，相对速度 20 → (10-2)/20 = 0.4
            Require(t >= 0f && Near(t, 0.4f, 1e-4f), "正对接近 TOI 应为 0.4，实际 " + t);
        }

        public static void TestR6SweptToiSeparatedReturnsMinusOne()
        {
            var a = YrObb.AxisAligned(new RVec3(-5f, 0f, 0f), H(1f));
            var b = YrObb.AxisAligned(new RVec3(5f, 0f, 0f), H(1f));
            float recede = YrSweptObb.ComputeToi(a, new RVec3(-20f, 0f, 0f),
                                                 b, RVec3.Zero, 1f);
            Require(recede < 0f, "远离运动不应报告 TOI");
            float tooLate = YrSweptObb.ComputeToi(a, new RVec3(5f, 0f, 0f),
                                                  b, RVec3.Zero, 1f);
            Require(tooLate < 0f, "接触发生在 dt 之后应返回 -1");
        }

        public static void TestR6SweptToiLateralMiss()
        {
            var a = YrObb.AxisAligned(new RVec3(-5f, 5f, 0f), H(1f));
            var b = YrObb.AxisAligned(new RVec3(5f, 0f, 0f), H(1f));
            float t = YrSweptObb.ComputeToi(a, new RVec3(20f, 0f, 0f),
                                            b, RVec3.Zero, 1f);
            Require(t < 0f, "横向错开 5 > 半径和 2 应永不接触，实际 " + t);
        }

        public static void TestR6SweptToiRotatedBoxUsesCrossAxes()
        {
            var q = RQuat.FromAxisAngle(new RVec3(0f, 0f, 1f), MathF.PI * 0.25f);
            var a = new YrObb(new RVec3(-5f, 0f, 0f), q, H(1f));
            var b = YrObb.AxisAligned(new RVec3(5f, 0f, 0f), H(1f));
            RVec3[] axes = YrSweptObb.EnumerateAxes(a, b);
            Require(axes.Length >= 6 && axes.Length <= 15, "轴数异常: " + axes.Length);
            float t = YrSweptObb.ComputeToi(a, new RVec3(20f, 0f, 0f),
                                            b, RVec3.Zero, 1f);
            Require(t >= 0f && t <= 1f, "旋转盒接近应报告 TOI: " + t);
        }

        public static void TestR6SweptToiIsDeterministic()
        {
            var a = YrObb.AxisAligned(new RVec3(-4f, 0.2f, 0f), H(1f));
            var b = YrObb.AxisAligned(new RVec3(4f, -0.1f, 0f), H(1f));
            var va = new RVec3(17f, 0.3f, 0f);
            float t1 = YrSweptObb.ComputeToi(a, va, b, RVec3.Zero, 1f);
            float t2 = YrSweptObb.ComputeToi(a, va, b, RVec3.Zero, 1f);
            Require(t1 == t2, "扫掠 TOI 非逐位确定");
            var obbs = new YrObb[] { a, Box(Make(b.Center, 1f), 1f) };
            var bodies = new RigidBody3D[] { Make(a.Center, 1f), Make(b.Center, 1f) };
            bodies[0].LinearVelocity = va;
            float e1 = YrSweptObb.EarliestToiAll(obbs, bodies, 1f);
            float e2 = YrSweptObb.EarliestToiAll(obbs, bodies, 1f);
            Require(e1 == e2, "全对最早 TOI 非逐位确定");
        }

        /// <summary>关键对照：常规单步先求解后积分，初始宽相分离 → 完全漏检穿透。</summary>
        public static void TestR6ConventionalStepMissesTunneling()
        {
            var bodies = new RigidBody3D[] {
                Make(new RVec3(-5f, 0f, 0f), 1f), Make(new RVec3(5f, 0f, 0f), 1f)
            };
            bodies[0].LinearVelocity = new RVec3(20f, 0f, 0f);
            var pipe = new YrPhysicsPipeline(bodies, new RVec3[] { H(1f), H(1f) });
            pipe.SleepEnabled = false;
            pipe.StepFixed(0.65f);
            Require(bodies[0].Position.X > 7f, "高速体应已越过障碍");
            Require(pipe.LastStats.ContactPoints == 0,
                    "常规单步本应漏检，却生成了接触（对照失效）");
        }

        public static void TestR6ToiStepCapturesTunneling()
        {
            var bodies = new RigidBody3D[] {
                Make(new RVec3(-5f, 0f, 0f), 1f), Make(new RVec3(5f, 0f, 0f), 1f)
            };
            bodies[0].LinearVelocity = new RVec3(20f, 0f, 0f);
            var pipe = new YrPhysicsPipeline(bodies, new RVec3[] { H(1f), H(1f) });
            pipe.SleepEnabled = false;
            pipe.UseToiSubsteps = true;
            pipe.StepFixedToi(0.65f);
            Require(pipe.LastToiHits >= 1, "TOI 子步应捕获至少一次接触");
            Require(pipe.LastToiContactPoints >= 1, "TOI 子步应生成接触点");
            Require(pipe.LastToiSubsteps >= 2, "应至少拆成 2 个子步，实际 " + pipe.LastToiSubsteps);
        }

        public static void TestR6ToiStepConservesLinearMomentum()
        {
            var bodies = new RigidBody3D[] {
                Make(new RVec3(-5f, 0.2f, 0f), 2f), Make(new RVec3(5f, -0.2f, 0f), 3f)
            };
            bodies[0].LinearVelocity = new RVec3(18f, 0.4f, 0f);
            bodies[1].LinearVelocity = new RVec3(-1f, 0.2f, 0f);
            RVec3 p0 = YrContactSolver.TotalLinearMomentum(bodies);
            var pipe = new YrPhysicsPipeline(bodies, new RVec3[] { H(1f), H(1f) });
            pipe.SleepEnabled = false;
            pipe.UseToiSubsteps = true;
            pipe.StepFixedToi(0.65f);
            RVec3 p1 = YrContactSolver.TotalLinearMomentum(bodies);
            Require((p1 - p0).Length() < 1e-4f,
                    "TOI 路径总线动量不守恒: " + (p1 - p0).Length());
        }

        public static void TestR6ToiStepConservesTotalAngularMomentum()
        {
            var bodies = new RigidBody3D[] {
                Make(new RVec3(-5f, 1.2f, 0f), 2f), Make(new RVec3(5f, -0.7f, 0.3f), 3f)
            };
            // 横向(y/z)速度两体取相同值 ⇒ 相对速度纯沿 x，确保确实会碰撞；
            // 同时 y 位置错开 + 非零横向速度 ⇒ 轨道项 r×(mv) 非零，守恒断言才有意义。
            // （上一版给了相反的横向相对速度，两体物理上会擦肩而过，
            //   「必须发生接触」本身就是错的断言。）
            bodies[0].LinearVelocity = new RVec3(18f, 0.5f, -0.2f);
            bodies[1].LinearVelocity = new RVec3(-1f, 0.5f, -0.2f);
            bodies[0].AngularMomentum = new RVec3(0.2f, -0.1f, 0.3f);
            bodies[1].AngularMomentum = new RVec3(-0.1f, 0.2f, 0.1f);
            RVec3 l0 = YrContactSolver.TotalAngularMomentum(bodies);
            var pipe = new YrPhysicsPipeline(bodies, new RVec3[] { H(1f), H(1f) });
            pipe.SleepEnabled = false;
            pipe.UseToiSubsteps = true;
            // 即使把 beta 设成 0.9，TOI 路径也完全不调用 CorrectPositions：
            // 这正是「不靠位置瞬移、严格守恒」的证明。
            pipe.PositionCorrectionBeta = 0.9f;
            pipe.StepFixedToi(0.65f);
            RVec3 l1 = YrContactSolver.TotalAngularMomentum(bodies);
            Require((l1 - l0).Length() < 1e-3f,
                    "TOI 路径总角动量(自旋+轨道)不守恒: " + (l1 - l0).Length());
            Require(pipe.LastToiHits >= 1, "本用例必须真的发生了接触");
        }

        public static void TestR6ToiSubstepCountIsBounded()
        {
            var bodies = new RigidBody3D[] {
                Make(new RVec3(-5f, 0f, 0f), 1f), Make(new RVec3(5f, 0f, 0f), 1f),
                Make(new RVec3(0f, 8f, 0f), 1f)
            };
            bodies[0].LinearVelocity = new RVec3(20f, 0f, 0f);
            var pipe = new YrPhysicsPipeline(bodies, new RVec3[] { H(1f), H(1f), H(1f) });
            pipe.SleepEnabled = false;
            pipe.UseToiSubsteps = true;
            pipe.ToiMaxSubsteps = 4;
            pipe.StepFixedToi(0.65f);
            Require(pipe.LastToiSubsteps >= 1 && pipe.LastToiSubsteps <= 4,
                    "子步数未受上界约束: " + pipe.LastToiSubsteps);
        }

        public static void TestR6ToiStepJointsStillSolved()
        {
            var bodies = new RigidBody3D[] {
                Make(new RVec3(-5f, 0f, 0f), 1f), Make(new RVec3(5f, 0f, 0f), 1f)
            };
            bodies[0].LinearVelocity = new RVec3(20f, 0f, 0f);
            var pipe = new YrPhysicsPipeline(bodies, new RVec3[] { H(1f), H(1f) });
            pipe.SleepEnabled = false;
            pipe.UseToiSubsteps = true;
            var j = new YrJoint[1];
            j[0] = new YrJoint {
                A = 0, B = 1, LocalAnchorA = RVec3.Zero, LocalAnchorB = RVec3.Zero,
                Axis = new RVec3(0f, 1f, 0f), Kind = YrJointKind.Ball, Beta = 0f
            };
            pipe.SetJoints(j);
            pipe.StepFixedToi(0.65f);
            Require(pipe.LastStats.IslandCount == 1, "关节应把两体并成 1 个岛");
            Require(pipe.LastToiSubsteps >= 1, "关节路径仍应推进子步");
        }

        public static void TestR6ToiStepDeterministicSnapshot()
        {
            YrPhysicsPipeline MakePipe()
            {
                var b = new RigidBody3D[] {
                    Make(new RVec3(-5f, 0.3f, 0f), 1f), Make(new RVec3(5f, -0.2f, 0f), 1f)
                };
                b[0].LinearVelocity = new RVec3(19f, 0.2f, 0f);
                var p = new YrPhysicsPipeline(b, new RVec3[] { H(1f), H(1f) });
                p.SleepEnabled = false; p.UseToiSubsteps = true;
                return p;
            }
            var a = MakePipe(); var b = MakePipe();
            for (int k = 0; k < 8; k++)
            {
                a.StepFixedToi(1f / 60f); b.StepFixedToi(1f / 60f);
                ulong ha = YrRollbackBuffer.Checksum(a.Snapshot(), a.Tick);
                ulong hb = YrRollbackBuffer.Checksum(b.Snapshot(), b.Tick);
                Require(ha == hb, "两条 TOI 管线逐位分叉 @" + (k + 1));
            }
        }

        public static void TestR6ToiRollbackReplay()
        {
            var bodies = new RigidBody3D[] {
                Make(new RVec3(-5f, 0f, 0f), 1f), Make(new RVec3(5f, 0f, 0f), 1f)
            };
            bodies[0].LinearVelocity = new RVec3(20f, 0f, 0f);
            var pipe = new YrPhysicsPipeline(bodies, new RVec3[] { H(1f), H(1f) });
            pipe.SleepEnabled = false; pipe.UseToiSubsteps = true;
            var hashes = new ulong[11];
            hashes[0] = YrRollbackBuffer.Checksum(pipe.Snapshot(), 0);
            pipe.SaveFrame();
            for (int k = 0; k < 10; k++)
            {
                pipe.StepFixedToi(0.05f);
                pipe.SaveFrame();
                hashes[k + 1] = YrRollbackBuffer.Checksum(pipe.Snapshot(), pipe.Tick);
            }
            Require(pipe.RewindTo(5), "未找到 tick 5 快照");
            Require(pipe.Tick == 5, "回滚后 tick 错误");
            for (int k = 5; k < 10; k++)
            {
                pipe.StepFixedToi(0.05f);
                ulong h = YrRollbackBuffer.Checksum(pipe.Snapshot(), pipe.Tick);
                Require(h == hashes[k + 1], "TOI 回滚重放分叉 @" + (k + 1));
            }
        }

        public static void TestR6StepDispatchesToToiWhenEnabled()
        {
            var bodies = new RigidBody3D[] {
                Make(new RVec3(-5f, 0f, 0f), 1f), Make(new RVec3(5f, 0f, 0f), 1f)
            };
            bodies[0].LinearVelocity = new RVec3(20f, 0f, 0f);
            var pipe = new YrPhysicsPipeline(bodies, new RVec3[] { H(1f), H(1f) });
            pipe.SleepEnabled = false; pipe.UseToiSubsteps = true;
            int steps = pipe.Step(0.05);
            Require(steps == 3, "Step 应执行 3 个固定步，实际 " + steps);
            Require(pipe.Tick == 3, "Step tick 未同步");
            Require(pipe.LastToiSubsteps >= 1, "Step 未分派到 TOI 路径");
        }
    }
}
