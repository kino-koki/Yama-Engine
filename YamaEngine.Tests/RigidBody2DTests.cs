// RigidBody2DTests.cs —— 追平 Godot 3 的 RigidBody2D（重力/质量/冲量/反弹/摩擦/休眠）

using System;
using System.Numerics;
using YamaEngine.UI;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests
{
    public static class RigidBody2DTests
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

        // ── 重力积分：velocity += gravity * gravity_scale * dt ──
        public static void TestGravityIntegration()
        {
            var body = new RigidBody2D("R") { GravityScale = 1f };
            var gravity = new Vector2(0f, 980f);
            body.IntegrateGravity(gravity, 1f / 60f);

            AssertNear(0f, body.LinearVelocity.X, "Gravity X zero.");
            AssertNear(980f / 60f, body.LinearVelocity.Y, "Gravity Y = g*dt.", 0.01f);
        }

        // ── GravityScale=0 关闭重力 ──
        public static void TestGravityScaleZero()
        {
            var body = new RigidBody2D("R") { GravityScale = 0f };
            body.IntegrateGravity(new Vector2(0f, 980f), 1f / 60f);
            AssertNear(0f, body.LinearVelocity.Y, "GravityScale=0 → no gravity.");
        }

        // ── ApplyImpulse：Δv = impulse / mass ──
        public static void TestApplyImpulse()
        {
            var body = new RigidBody2D("R") { Mass = 2f };
            body.ApplyImpulse(new Vector2(100f, 0f));
            AssertNear(50f, body.LinearVelocity.X, "Impulse 100 on mass 2 → Δv=50.");
            AssertFalse(body.Sleeping, "Impulse wakes body.");
        }

        // ── ApplyForce：velocity += (force/mass)*dt ──
        public static void TestApplyForce()
        {
            var body = new RigidBody2D("R") { Mass = 1f };
            body.ApplyForce(new Vector2(60f, 0f), 1f / 60f);
            AssertNear(1f, body.LinearVelocity.X, "Force 60 for 1/60s on mass 1 → Δv=1.");
        }

        // ── GetWeight = mass * |gravity| ──
        public static void TestWeight()
        {
            var body = new RigidBody2D("R") { Mass = 2f };
            float w = body.GetWeight(new Vector2(0f, 980f));
            AssertNear(1960f, w, "Weight = mass * gravity.", 0.5f);
        }

        // ── 完全非弹性落地（bounce=0）：法向速度归零 ──
        public static void TestLandInelastic()
        {
            var body = new RigidBody2D("R") { Bounce = 0f, Friction = 0f };
            body.LinearVelocity = new Vector2(0f, 500f); // 向下落
            body.ResolveBounce(new Vector2(0f, -1f)); // 地板法线朝上

            AssertNear(0f, body.LinearVelocity.Y, "Bounce=0 → normal velocity zeroed.", 0.01f);
            AssertTrue(body.ContactNormals.Count > 0, "Contact normal recorded.");
        }

        // ── 完全弹性反弹（bounce=1）：v' = -v ──
        public static void TestBounceElastic()
        {
            var body = new RigidBody2D("R") { Bounce = 1f, Friction = 0f };
            body.LinearVelocity = new Vector2(0f, 300f); // 向下落（+Y），撞地板法线朝上
            body.ResolveBounce(new Vector2(0f, -1f));

            AssertNear(-300f, body.LinearVelocity.Y, "Bounce=1 → perfect reflection upward.", 0.01f);
        }

        // ── 摩擦衰减：切向速度乘 (1-friction) ──
        public static void TestFrictionTangent()
        {
            var body = new RigidBody2D("R") { Bounce = 0f, Friction = 0.5f };
            body.LinearVelocity = new Vector2(200f, 100f); // 撞地板，法线朝上，切向=X
            body.ResolveBounce(new Vector2(0f, -1f));

            AssertNear(100f, body.LinearVelocity.X, "Friction=0.5 → tangent halved.", 0.01f);
            AssertNear(0f, body.LinearVelocity.Y, "Normal zeroed (bounce=0).", 0.01f);
        }

        // ── 休眠：低速进入休眠，速度归零 ──
        public static void TestSleepOnLowVelocity()
        {
            var body = new RigidBody2D("R") { CanSleep = true, LinearVelocitySleepThreshold = 10f };
            body.LinearVelocity = new Vector2(5f, 0f); // 低于阈值
            bool slept = body.UpdateSleep();
            AssertTrue(slept, "Low velocity triggers sleep.");
            AssertTrue(body.Sleeping, "Body is sleeping.");
            AssertNear(0f, body.LinearVelocity.Length(), "Sleeping zeros velocity.", 0.01f);
        }

        // ── CanSleep=false 永不休眠 ──
        public static void TestNoSleepWhenDisabled()
        {
            var body = new RigidBody2D("R") { CanSleep = false, LinearVelocitySleepThreshold = 100f };
            body.LinearVelocity = new Vector2(1f, 0f);
            bool slept = body.UpdateSleep();
            AssertFalse(slept, "CanSleep=false → never sleeps.");
            AssertFalse(body.Sleeping, "Not sleeping.");
        }

        // ── 世界 Step：自由落体（无碰撞） ──
        public static void TestWorldFreeFall()
        {
            var world = new RigidBodyWorld { Gravity = new Vector2(0f, 980f), TimeStep = 1f / 60f };
            var body = new RigidBody2D("R")
            {
                Shape = new CircleShape2D(10f),
                Position = new Vector2(0f, 0f),
                CanSleep = false,
            };
            world.AddBody(body);

            world.Step(1f / 60f);

            // 一步后 v = g*dt = 16.33，位移 = v*dt ≈ 0.27
            AssertNear(980f / 60f, body.LinearVelocity.Y, "Velocity after 1 step.", 0.1f);
            AssertNear((980f / 60f) * (1f / 60f), body.Position.Y, "Position advanced.", 0.01f);
        }

        // ── 世界 Step：落到地板反弹（bounce=1） ──
        public static void TestWorldBounce()
        {
            var world = new RigidBodyWorld { Gravity = new Vector2(0f, 0f), TimeStep = 1f / 60f };
            var ball = new RigidBody2D("Ball")
            {
                Shape = new CircleShape2D(10f),
                Position = new Vector2(0f, 0f),
                LinearVelocity = new Vector2(0f, 600f), // 向下落
                Bounce = 1f,
                Friction = 0f,
                CanSleep = false,
            };
            var floor = new StaticBody2D("Floor")
            {
                Shape = new RectangleShape2D(100f, 10f),
                Position = new Vector2(0f, 25f), // 上表面 y=15，球下沿 y=10，距离=5
            };
            world.AddBody(ball);
            world.AddStatic(floor);

            world.Step(1f / 60f); // dy=10 → 球到 y=10，与地板（上表面15，球下沿0）距离=15>0 不碰
            // 第二步：球到 y=20，球下沿 y=10+10=20，地板上表面=15 → 球下沿 20+10=30 > 15 → 深度穿透
            // 实际：球心 (0,20)，地板中心 (0,25)，法线朝上 (0,-1)，穿透发生 → 反弹

            // 简化断言：至少发生了一次接触（法线被记录）
            bool bounced = ball.LinearVelocity.Y < 0f;
            AssertTrue(bounced, "Bounce flips velocity upward.");
        }

        // ── 重力 + 地板平衡：物体停在地上速度归零 ──
        public static void TestWorldRestsOnFloor()
        {
            var world = new RigidBodyWorld { Gravity = new Vector2(0f, 980f), TimeStep = 1f / 60f };
            var ball = new RigidBody2D("Ball")
            {
                Shape = new CircleShape2D(10f),
                Position = new Vector2(0f, 0f),
                Bounce = 0f,
                Friction = 0.3f,
                CanSleep = true,
                LinearVelocitySleepThreshold = 5f,
            };
            var floor = new StaticBody2D("Floor")
            {
                Shape = new RectangleShape2D(100f, 10f),
                Position = new Vector2(0f, 20f), // 上表面 y=10，球下沿 y=10，刚好接触
            };
            world.AddBody(ball);
            world.AddStatic(floor);

            // 跑多步，物体应停在地板上（速度接近 0）
            for (int i = 0; i < 120; i++)
                world.Step(1f / 60f);

            AssertTrue(ball.Position.Y < 15f, "Ball rests near floor (no deep penetration).");
            AssertNear(0f, ball.LinearVelocity.Y, "Vertical velocity settles near 0.", 5f);
        }

        // ── 层掩码过滤：mask 不匹配则穿过 ──
        public static void TestCollisionMaskFilter()
        {
            var world = new RigidBodyWorld { Gravity = new Vector2(0f, 0f), TimeStep = 1f / 60f };
            var ball = new RigidBody2D("Ball")
            {
                Shape = new CircleShape2D(10f),
                Position = new Vector2(0f, 0f),
                LinearVelocity = new Vector2(0f, 600f),
                Bounce = 1f,
                CollisionMask = 0b10, // 只检测层 2
            };
            var floor = new StaticBody2D("Floor")
            {
                Shape = new RectangleShape2D(100f, 10f),
                Position = new Vector2(0f, 25f),
                CollisionLayer = 0b01, // 在层 1
            };
            world.AddBody(ball);
            world.AddStatic(floor);

            world.Step(1f / 60f);
            world.Step(1f / 60f);
            world.Step(1f / 60f);

            AssertTrue(ball.LinearVelocity.Y > 0f, "Passes through floor (mask mismatch).");
            AssertEqual(0, ball.ContactNormals.Count, "No contact recorded.");
        }

        // ── 休眠的刚体跳过 Step（速度不变） ──
        public static void TestSleepingSkipsStep()
        {
            var world = new RigidBodyWorld { Gravity = new Vector2(0f, 980f), TimeStep = 1f / 60f };
            var body = new RigidBody2D("R")
            {
                Position = new Vector2(0f, 0f),
                CanSleep = true,
                LinearVelocitySleepThreshold = 50f, // 高阈值，低速即休眠
            };
            body.LinearVelocity = new Vector2(1f, 0f); // 低于阈值
            body.UpdateSleep();
            AssertTrue(body.Sleeping, "Precondition: sleeping.");

            world.AddBody(body);
            var posBefore = body.Position;
            world.Step(1f / 60f); // 休眠中：跳过重力+位移
            AssertNear(posBefore.X, body.Position.X, "Sleeping body X unchanged.");
            AssertNear(posBefore.Y, body.Position.Y, "Sleeping body Y unchanged (no gravity applied).");
        }
    }
}
