// CPUParticles2DTests.cs —— 追平 Godot 3 的 CPUParticles2D（2D 粒子发射/重力/寿命/one-shot/颜色渐变）

using System;
using System.Collections.Generic;
using System.Numerics;
using YamaEngine.UI;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests
{
    public static class CPUParticles2DTests
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

        // 连续发射器：rate=amount/lifetime=2/1=2/s
        private static CPUParticles2D MakeContinuousEmitter()
        {
            return new CPUParticles2D("P")
            {
                Amount = 2,
                Lifetime = 1f,
                Direction = new Vector2(1f, 0f),
                Spread = 0f,
                InitialVelocityMin = 100f,
                InitialVelocityMax = 100f,
                Gravity = new Vector2(0f, 0f),
                Color = new Vector4(1f, 1f, 1f, 1f),
                Emitting = true,
            };
        }

        // ── 连续发射：0.5s 应发射约 1 个（rate=2/s × 0.5s = 1） ──
        public static void TestContinuousEmission()
        {
            var p = MakeContinuousEmitter();
            p.Update(0.5f);
            // 累积器 1.0 → 发 1 个，余 0
            AssertEqual(1, p.TotalEmitted, "Emitted 1 after 0.5s (rate=2/s).");
            AssertEqual(1, p.AliveCount, "1 alive.");
        }

        // ── 连续发射 1s：2 个 ──
        public static void TestContinuousEmissionOneSecond()
        {
            var p = MakeContinuousEmitter();
            p.Update(1.0f);
            AssertEqual(2, p.TotalEmitted, "Emitted 2 after 1s.");
        }

        // 单发发射器：OneShot + Amount=1，一次 Update 立即发 1 个
        private static CPUParticles2D MakeSingleShot()
        {
            return new CPUParticles2D("P")
            {
                Amount = 1,
                Lifetime = 10f,
                OneShot = true,
                Explosiveness = 1f,
                Direction = new Vector2(1f, 0f),
                Spread = 0f,
                InitialVelocityMin = 100f,
                InitialVelocityMax = 100f,
                Gravity = new Vector2(0f, 0f),
                Color = new Vector4(1f, 1f, 1f, 1f),
                Emitting = true,
            };
        }

        // ── 初速度：Min=Max=100 → 发射后速度=100×方向 ──
        public static void TestInitialVelocity()
        {
            var p = MakeSingleShot();
            p.Update(0.1f);
            var particles = p.GetParticles();
            AssertEqual(1, particles.Count, "One particle.");
            AssertNear(100f, particles[0].Velocity.X, "VX=100.");
            AssertNear(0f, particles[0].Velocity.Y, "VY=0.");
        }

        // ── 无重力时位置按 v*dt 前进 ──
        public static void TestPositionAdvanceNoGravity()
        {
            var p = MakeSingleShot();
            p.Update(0.1f);
            var particles = p.GetParticles();
            AssertNear(10f, particles[0].Position.X, "X advanced 100×0.1=10.");
        }

        // ── 重力积分：vy += g*dt（辛欧拉：位置用旧速度） ──
        public static void TestGravityIntegration()
        {
            var p = MakeSingleShot();
            p.Gravity = new Vector2(0f, 980f);
            p.InitialVelocityMin = p.InitialVelocityMax = 0f; // 静止发射，纯重力
            p.Update(0.1f); // 首步：vy=0+980*0.1=98，位置用新速度更新 → y=98*0.1=9.8

            var particles = p.GetParticles();
            AssertNear(98f, particles[0].Velocity.Y, "VY after first gravity step.", 0.5f);
            AssertNear(9.8f, particles[0].Position.Y, "Y advanced by new velocity (semi-implicit Euler).", 0.5f);

            // 第二步：vy=98+98=196，位置用新速度更新 → y=9.8+196*0.1=29.4
            p.Update(0.1f);
            particles = p.GetParticles();
            AssertNear(196f, particles[0].Velocity.Y, "VY after second step.", 0.5f);
            AssertNear(29.4f, particles[0].Position.Y, "Y advanced by new velocity.", 0.5f);
        }

        // ── 寿命回收：Lifetime=0.2，0.3s 后粒子死亡 ──
        public static void TestLifetimeRecycle()
        {
            var p = MakeSingleShot();
            p.Lifetime = 0.2f;
            p.Update(0.1f); // 发 1 个，存活
            AssertEqual(1, p.AliveCount, "Alive after emit.");

            p.Update(0.3f); // 粒子到期死亡
            AssertEqual(0, p.AliveCount, "Dead after lifetime.");
        }

        // ── Emitting=false 停止新发射，已有粒子继续走完 ──
        public static void TestEmittingFalseStopsNew()
        {
            var p = MakeSingleShot();
            p.Lifetime = 0.3f;

            // 先发 1 个（one-shot 触发）
            p.Update(0.1f);
            AssertEqual(1, p.TotalEmitted, "Emitted one.");

            // 关闭发射：已有粒子继续走完，不再新增
            p.Emitting = false;
            p.Restart(); // 清空（模拟"停止后清场"）
            AssertEqual(0, p.TotalEmitted, "After restart, count reset.");

            // Emitting=false 期间 Update 不发新（因为 one-shot 已在构造时未触发，Restart 复位了）
            // 用连续发射器验证：Emitting=false 时 Update 累积器仍走但被跳过
            var c = MakeContinuousEmitter();
            c.Emitting = false;
            c.Update(1.0f); // 1s 累积器=2，但 Emitting=false 跳过发射
            AssertEqual(0, c.TotalEmitted, "Emitting=false → no emission.");

            c.Emitting = true;
            c.Update(1.0f); // 现在发
            AssertEqual(2, c.TotalEmitted, "Emitting=true → emits.");
        }

        // ── OneShot + Explosiveness=1：一次性全发 ──
        public static void TestOneShotBurst()
        {
            var p = MakeContinuousEmitter();
            p.OneShot = true;
            p.Explosiveness = 1f;
            p.Amount = 8;

            p.Update(0.1f); // 一次性全发 8 个
            AssertEqual(8, p.TotalEmitted, "Burst emits all 8.");
            AssertEqual(8, p.AliveCount, "All 8 alive.");
            AssertTrue(p.OneShotEmitted, "OneShot flag set.");

            p.Update(0.1f); // 不再发
            AssertEqual(8, p.TotalEmitted, "No more after burst.");
        }

        // ── OneShot + Lifetime 结束后所有粒子死亡 ──
        public static void TestOneShotAllDie()
        {
            var p = MakeContinuousEmitter();
            p.OneShot = true;
            p.Explosiveness = 1f;
            p.Amount = 4;
            p.Lifetime = 0.3f;

            p.Update(0.1f); // 全发
            AssertEqual(4, p.AliveCount, "All alive after burst.");

            p.Update(0.5f); // 超寿命
            AssertEqual(0, p.AliveCount, "All dead after lifetime.");
        }

        // ── Explosiveness=0.5 半量爆发 ──
        public static void TestOneShotHalfExplosiveness()
        {
            var p = MakeContinuousEmitter();
            p.OneShot = true;
            p.Explosiveness = 0.5f;
            p.Amount = 8;

            p.Update(0.1f);
            AssertEqual(4, p.TotalEmitted, "Half amount (4 of 8).");
        }

        // ── 颜色渐变：ColorRamp 从红到蓝 ──
        public static void TestColorRampGradient()
        {
            var ramp = new ColorRamp();
            ramp.AddStop(0f, new Vector4(1f, 0f, 0f, 1f)); // 红
            ramp.AddStop(1f, new Vector4(0f, 0f, 1f, 1f)); // 蓝

            var p = new CPUParticles2D("P")
            {
                Amount = 1,
                Lifetime = 1f,
                OneShot = true,
                Explosiveness = 1f,
                Direction = new Vector2(1f, 0f),
                Spread = 0f,
                InitialVelocityMin = 0f,
                InitialVelocityMax = 0f,
                Gravity = new Vector2(0f, 0f),
                ColorRamp = ramp,
                Emitting = true,
            };

            p.Update(0.01f); // 发 1 个，t≈0
            AssertEqual(1, p.TotalEmitted, "First particle emitted.");
            var particles = p.GetParticles();
            AssertNear(1f, particles[0].Color.X, "Start red (R=1).", 0.01f);
            AssertNear(0f, particles[0].Color.Z, "Start blue=0 (B=0).", 0.01f);

            // 走到中段 t≈0.5 → 紫色
            p.Update(0.49f);
            particles = p.GetParticles();
            AssertEqual(1, p.TotalEmitted, "Still one particle (one-shot).");
            AssertNear(0.5f, particles[0].Color.X, "Mid R≈0.5.", 0.05f);
            AssertNear(0.5f, particles[0].Color.Z, "Mid B≈0.5.", 0.05f);
        }

        // ── 颜色固定（无 ramp）：始终是基色 ──
        public static void TestColorFixed()
        {
            var p = new CPUParticles2D("P")
            {
                Amount = 1,
                Lifetime = 1f,
                OneShot = true,
                Explosiveness = 1f,
                Direction = new Vector2(1f, 0f),
                Spread = 0f,
                InitialVelocityMin = 0f,
                InitialVelocityMax = 0f,
                Gravity = new Vector2(0f, 0f),
                Color = new Vector4(0.2f, 0.4f, 0.6f, 1f),
                Emitting = true,
            };

            p.Update(0.01f);
            AssertEqual(1, p.TotalEmitted, "First emitted.");
            p.Update(0.3f);
            var particles = p.GetParticles();
            AssertEqual(1, p.TotalEmitted, "Still one.");
            AssertNear(0.2f, particles[0].Color.X, "Fixed R.");
            AssertNear(0.4f, particles[0].Color.Y, "Fixed G.");
            AssertNear(0.6f, particles[0].Color.Z, "Fixed B.");
        }

        // ── Restart 清空所有粒子 + 状态复位 ──
        public static void TestRestart()
        {
            var p = MakeContinuousEmitter();
            p.OneShot = true;
            p.Explosiveness = 1f;
            p.Amount = 4;
            p.Update(0.1f);
            AssertEqual(4, p.TotalEmitted, "Emitted before restart.");

            p.Restart();
            AssertEqual(0, p.AliveCount, "Alive reset.");
            AssertEqual(0, p.TotalEmitted, "TotalEmitted reset.");
            AssertFalse(p.OneShotEmitted, "OneShot flag reset.");
        }

        // ── ColorRamp 单点返回固定色 ──
        public static void TestColorRampSingleStop()
        {
            var ramp = new ColorRamp();
            ramp.AddStop(0.5f, new Vector4(0f, 1f, 0f, 1f));
            AssertNear(0f, ramp.Sample(0f).X, "Sample 0 → R=0.");
            AssertNear(1f, ramp.Sample(0f).Y, "Sample 0 → G=1.");
            AssertNear(0f, ramp.Sample(1f).X, "Sample 1 → R=0.");
        }

        // ── ColorRamp 空返回白色 ──
        public static void TestColorRampEmpty()
        {
            var ramp = new ColorRamp();
            var c = ramp.Sample(0.5f);
            AssertNear(1f, c.X, "Empty ramp → R=1.");
            AssertNear(1f, c.Y, "Empty ramp → G=1.");
            AssertNear(1f, c.Z, "Empty ramp → B=1.");
        }

        // ── 连续发射 2s：rate=2/s → 4 个（Lifetime=1，但首批仍活着时重叠） ──
        public static void TestContinuousOverlap()
        {
            var p = MakeContinuousEmitter();
            p.Lifetime = 2f; // 长寿命，允许重叠
            p.Amount = 4;    // rate=4/2=2/s

            // 分 8 次 0.25s 更新：累积器每步 +0.5，每 2 步发 1 个（t=0.5,1.0,1.5,2.0）
            // 不同时间发射的粒子年龄不同，全部 < 2s 仍存活
            for (int i = 0; i < 8; i++) p.Update(0.25f);
            AssertEqual(4, p.TotalEmitted, "Emitted 4 in 2s.");
            AssertEqual(4, p.AliveCount, "All 4 still alive (staggered ages all <2s).");
        }
    }
}
