// RigidBody2DRotationTests.cs —— 追平 Godot 3 的 RigidBody2D 角动力学（惯性/角速度/扭矩/角冲量/偏心冲量/旋转积分）

using System;
using System.Numerics;
using YamaEngine.UI;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests
{
    public static class RigidBody2DRotationTests
    {
        private const float Tol = 1e-3f;

        private static void AssertEqual<T>(T got, T want, string msg)
        {
            if (!Equals(got, want)) throw new Exception($"{msg} (got {got}, want {want}).");
        }

        private static void AssertNear(float got, float want, string msg, float tol = Tol)
        {
            if (MathF.Abs(got - want) > tol) throw new Exception($"{msg} (got {got}, want {want}±{tol}).");
        }

        private static void AssertTrue(bool cond, string msg)
        {
            if (!cond) throw new Exception(msg);
        }

        private static void AssertFalse(bool cond, string msg)
        {
            if (cond) throw new Exception(msg);
        }

        // 圆盘刚体：m=2, r=10 → I = 0.5·2·10² = 100
        private static RigidBody2D MakeDisc(float mass = 2f, float radius = 10f)
        {
            var b = new RigidBody2D("Disc")
            {
                Mass = mass,
                Shape = new CircleShape2D(radius),
            };
            return b;
        }

        // 矩形刚体：m=3, w=6(半宽3), h=4(半高2) → I = 3·(6²+4²)/12 = 3·52/12 = 13
        private static RigidBody2D MakeRect(float mass = 3f)
        {
            var b = new RigidBody2D("Rect")
            {
                Mass = mass,
                Shape = new RectangleShape2D(3f, 2f),
            };
            return b;
        }

        // ── 圆盘转动惯量自动计算：I = 0.5·m·r² ──
        public static void TestDiscInertia()
        {
            var b = MakeDisc(mass: 2f, radius: 10f);
            AssertNear(100f, b.Inertia, "Disc inertia = 0.5·2·10²=100.");
        }

        // ── 矩形转动惯量自动计算：I = m(w²+h²)/12 ──
        public static void TestRectInertia()
        {
            var b = MakeRect(mass: 3f);
            // w=6, h=4: I = 3·(36+16)/12 = 3·52/12 = 13
            AssertNear(13f, b.Inertia, "Rect inertia = 3·(36+16)/12=13.");
        }

        // ── InertiaOverride 覆盖自动惯量 ──
        public static void TestInertiaOverride()
        {
            var b = MakeDisc(mass: 2f, radius: 10f);
            AssertNear(100f, b.Inertia, "Auto inertia=100.");

            b.InertiaOverride = 50f;
            AssertNear(50f, b.Inertia, "Override inertia=50.");
        }

        // ── ApplyAngularImpulse：Δω = L/I（L=50, I=100 → Δω=0.5） ──
        public static void TestApplyAngularImpulse()
        {
            var b = MakeDisc();
            AssertEqual(0f, b.AngularVelocity, "Initial ω=0.");

            b.ApplyAngularImpulse(50f);
            AssertNear(0.5f, b.AngularVelocity, "ω = 50/100 = 0.5 rad/s.");
        }

        // ── ApplyAngularImpulse 累积（两次冲量叠加） ──
        public static void TestAngularImpulseAccumulates()
        {
            var b = MakeDisc();
            b.ApplyAngularImpulse(50f); // ω=0.5
            b.ApplyAngularImpulse(25f); // ω=0.75
            AssertNear(0.75f, b.AngularVelocity, "ω accumulates to 0.75.");
        }

        // ── ApplyTorque：ω += (torque/I)·dt ──
        public static void TestApplyTorque()
        {
            var b = MakeDisc();
            b.ApplyTorque(200f, 0.1f); // Δω = (200/100)·0.1 = 0.2
            AssertNear(0.2f, b.AngularVelocity, "ω = (200/100)·0.1 = 0.2.");
        }

        // ── ApplyTorque 多次累积（持续扭矩） ──
        public static void TestTorqueAccumulates()
        {
            var b = MakeDisc();
            for (int i = 0; i < 10; i++) b.ApplyTorque(100f, 1f / 60f);
            // 总 Δω = 10 × (100/100) × (1/60) = 10/60 = 0.1667
            AssertNear(10f / 60f, b.AngularVelocity, "ω = 10·(100/100)·(1/60) ≈ 0.167.", 0.01f);
        }

        // ── IntegrateRotation：θ += ω·dt ──
        public static void TestIntegrateRotation()
        {
            var b = MakeDisc();
            b.AngularVelocity = 2f; // 2 rad/s
            b.IntegrateRotation(0.5f);
            AssertNear(1f, b.Rotation, "θ = 2×0.5 = 1 rad.");
        }

        // ── IntegrateRotation 连续两步 ──
        public static void TestRotationTwoSteps()
        {
            var b = MakeDisc();
            b.AngularVelocity = 10f;
            b.IntegrateRotation(0.1f); // θ=1
            b.IntegrateRotation(0.1f); // θ=2
            AssertNear(2f, b.Rotation, "θ = 10×0.1×2 = 2 rad.");
        }

        // ── ApplyImpulseAtOffset：线速度 + 角速度同时产生 ──
        // 冲量 (0, 10) 作用在 offset (5, 0)：角冲量 = 5×10 - 0×0 = 50
        // I=100, m=2 → Δv=(0,5), Δω=50/100=0.5
        public static void TestEccentricImpulse()
        {
            var b = MakeDisc(); // m=2, I=100
            b.ApplyImpulseAtOffset(new Vector2(0f, 10f), new Vector2(5f, 0f));

            AssertNear(0f, b.LinearVelocity.X, "VX=0.");
            AssertNear(5f, b.LinearVelocity.Y, "VY = 10/2 = 5.");
            AssertNear(0.5f, b.AngularVelocity, "ω = (5×10)/100 = 0.5.");
        }

        // ── 偏心冲量叉积方向：offset (0, 3), impulse (4, 0) → cross = 0·0 - 3·4 = -12 ──
        public static void TestEccentricImpulseDirection()
        {
            var b = MakeDisc(); // I=100
            b.ApplyImpulseAtOffset(new Vector2(4f, 0f), new Vector2(0f, 3f));
            // crossZ(offset, impulse) = 0×0 - 3×4 = -12 → ω = -12/100 = -0.12（顺时针）
            AssertNear(-0.12f, b.AngularVelocity, "ω = -12/100 = -0.12 (clockwise).");
        }

        // ── 偏心冲量为零：offset 平行于 impulse → 无旋转 ──
        public static void TestEccentricImpulseNoTorque()
        {
            var b = MakeDisc();
            // offset (2,0), impulse (4,0)：crossZ = 2·0 - 0·4 = 0
            b.ApplyImpulseAtOffset(new Vector2(4f, 0f), new Vector2(2f, 0f));
            AssertNear(0f, b.AngularVelocity, "Parallel offset+impulse → no rotation.");
            AssertNear(2f, b.LinearVelocity.X, "Linear still applies (4/2=2).");
        }

        // ── 无形状刚体：无穷惯量，ApplyAngularImpulse 不产生角速度 ──
        public static void TestNoShapeInfiniteInertia()
        {
            var b = new RigidBody2D("NoShape") { Mass = 1f };
            b.ApplyAngularImpulse(100f);
            // I=MaxValue → Δω = 100/MaxValue ≈ 2.9e-37（数值上不旋转）
            AssertNear(0f, b.AngularVelocity, "No shape → infinite inertia → no rotation.", 1e-20f);
        }

        // ── InertiaOverride=0 回退到自动惯量 ──
        public static void TestInertiaOverrideZeroFallsBack()
        {
            var b = MakeDisc();
            b.InertiaOverride = 0f; // 显式 0 = 不覆盖
            AssertNear(100f, b.Inertia, "Override=0 falls back to auto inertia.");
        }

        // ── 世界 Step 整合旋转：初始角速度持续旋转 ──
        public static void TestWorldStepRotates()
        {
            var world = new RigidBodyWorld { Gravity = new Vector2(0f, 0f) };
            var b = MakeDisc();
            b.AngularVelocity = 1f; // 1 rad/s
            world.AddBody(b);

            world.Step(1.0f);
            AssertNear(1f, b.Rotation, "After 1 step (dt=1): θ = 1×1 = 1 rad.");
        }

        // ── 世界 Step 中偏心冲量在 ApplyAngularImpulse 直接生效 ──
        public static void TestApplyAngularImpulseThenStep()
        {
            var world = new RigidBodyWorld { Gravity = new Vector2(0f, 0f) };
            var b = MakeDisc();
            b.ApplyAngularImpulse(100f); // ω = 100/100 = 1
            world.AddBody(b);

            world.Step(0.5f);
            AssertNear(0.5f, b.Rotation, "θ = 1×0.5 = 0.5 rad after step.");
        }

        // ── 角速度低于阈值 + 线速度低于阈值 → 休眠 ──
        public static void TestAngularSleep()
        {
            var b = MakeDisc();
            b.CanSleep = true;
            b.AngularVelocitySleepThreshold = 0.1f;
            b.AngularVelocity = 0.05f; // 低于阈值
            b.LinearVelocity = new Vector2(1f, 1f); // 也低于线性阈值 20
            bool slept = b.UpdateSleep();
            AssertTrue(slept, "Both velocities below threshold → sleep.");
            AssertEqual(0f, b.AngularVelocity, "Sleeping zeroes angular velocity.");
        }

        // ── 线速度低于阈值但角速度高 → 不休眠 ──
        public static void TestNoSleepWhenSpinning()
        {
            var b = MakeDisc();
            b.CanSleep = true;
            b.AngularVelocitySleepThreshold = 0.1f;
            b.AngularVelocity = 0.5f; // 高于角速度阈值
            b.LinearVelocity = Vector2.Zero;
            bool slept = b.UpdateSleep();
            AssertFalse(slept, "Still spinning → no sleep.");
            AssertFalse(b.Sleeping, "Not sleeping.");
        }

        // ── WakeUp 清除休眠 ──
        public static void TestWakeUpRestoresRotation()
        {
            var b = MakeDisc();
            b.Sleeping = true;
            b.ApplyAngularImpulse(50f);
            AssertFalse(b.Sleeping, "Angular impulse wakes up body.");
        }
    }
}
