// Camera2DSmoothingDepthTests.cs —— Camera2D 平滑跟随（position_smoothing）深度验证

using System;
using System.Numerics;
using YamaEngine.UI;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests
{
    public static class Camera2DSmoothingDepthTests
    {
        private static void AssertTrue(bool cond, string msg)
        {
            if (!cond) throw new Exception($"{msg}");
        }

        private static void AssertFalse(bool cond, string msg)
        {
            if (cond) throw new Exception($"{msg}");
        }

        private static void AssertNear(float got, float want, string msg, float tol = 1e-3f)
        {
            if (MathF.Abs(got - want) > tol) throw new Exception($"{msg} (got {got}, want {want}).");
        }

        // ── 默认状态：smoothing 关闭、速度=5 ──
        public static void TestDefaults()
        {
            var cam = new Camera2D("C");
            AssertFalse(cam.PositionSmoothingEnabled, "Smoothing disabled by default.");
            AssertNear(5f, cam.PositionSmoothingSpeed, "Default speed=5.");
            AssertNear(0f, cam.GetTargetPosition().X, "Target starts at origin.");
            AssertNear(0f, cam.GetTargetPosition().Y, "Target starts at origin Y.");
        }

        // ── 平滑关闭时不移动 ──
        public static void TestDisabledNoMove()
        {
            var cam = new Camera2D("C");
            cam.Position = new Vector2(0, 0);
            cam.SetTargetPosition(new Vector2(100, 0));
            cam.Update(1f);
            AssertNear(0f, cam.GlobalPosition.X, "Camera stays at origin when disabled.");
        }

        // ── 平滑开启时朝目标移动 ──
        public static void TestEnabledMoves()
        {
            var cam = new Camera2D("C")
            {
                PositionSmoothingEnabled = true,
                PositionSmoothingSpeed = 10f,
            };
            cam.Position = new Vector2(0, 0);
            cam.SetTargetPosition(new Vector2(100, 0));
            cam.Update(0.5f);
            float x = cam.GlobalPosition.X;
            AssertTrue(x > 0f, "Camera moved toward target.");
            AssertTrue(x < 100f, "Camera hasn't overshot target.");
        }

        // ── 高速时快速收敛 ──
        public static void TestHighSpeedFastApproach()
        {
            var camSlow = new Camera2D("Slow") { PositionSmoothingEnabled = true, PositionSmoothingSpeed = 1f };
            camSlow.Position = new Vector2(0, 0);
            camSlow.SetTargetPosition(new Vector2(100, 0));

            var camFast = new Camera2D("Fast") { PositionSmoothingEnabled = true, PositionSmoothingSpeed = 20f };
            camFast.Position = new Vector2(0, 0);
            camFast.SetTargetPosition(new Vector2(100, 0));

            camSlow.Update(0.1f);
            camFast.Update(0.1f);
            AssertTrue(camFast.GlobalPosition.X > camSlow.GlobalPosition.X,
                "Higher speed moves camera further in same dt.");
        }

        // ── 帧率无关性：大 dt 多次 ≈ 小 dt 多次 ──
        public static void TestFrameRateIndependence()
        {
            // 模拟大帧率（小 dt 多次）vs 小帧率（大 dt 少次），总时间相同
            float totalDt = 0.5f;

            var camFine = new Camera2D("Fine") { PositionSmoothingEnabled = true, PositionSmoothingSpeed = 5f };
            camFine.Position = new Vector2(0, 0);
            camFine.SetTargetPosition(new Vector2(100, 0));
            int steps = 60;
            float dt = totalDt / steps;
            for (int i = 0; i < steps; i++) camFine.Update(dt);

            var camCoarse = new Camera2D("Coarse") { PositionSmoothingEnabled = true, PositionSmoothingSpeed = 5f };
            camCoarse.Position = new Vector2(0, 0);
            camCoarse.SetTargetPosition(new Vector2(100, 0));
            camCoarse.Update(totalDt);

            AssertNear(camFine.GlobalPosition.X, camCoarse.GlobalPosition.X, "Same total time → same result regardless of step count.", 0.1f);
        }

        // ── 足够长时间后收敛到目标 ──
        public static void TestConvergesToTarget()
        {
            var cam = new Camera2D("C") { PositionSmoothingEnabled = true, PositionSmoothingSpeed = 5f };
            cam.Position = new Vector2(0, 0);
            cam.SetTargetPosition(new Vector2(100, 50));
            for (int i = 0; i < 200; i++) cam.Update(0.05f); // 模拟 10s
            AssertNear(100f, cam.GlobalPosition.X, "Converges to target X.", 0.1f);
            AssertNear(50f, cam.GlobalPosition.Y, "Converges to target Y.", 0.1f);
        }

        // ── speed=0 完全不移动 ──
        public static void TestZeroSpeedNoMove()
        {
            var cam = new Camera2D("C") { PositionSmoothingEnabled = true, PositionSmoothingSpeed = 0f };
            cam.Position = new Vector2(0, 0);
            cam.SetTargetPosition(new Vector2(100, 0));
            cam.Update(1f);
            AssertNear(0f, cam.GlobalPosition.X, "Zero speed: camera doesn't move.");
        }

        // ── Y 轴同样平滑跟随 ──
        public static void TestYAxisFollow()
        {
            var cam = new Camera2D("C") { PositionSmoothingEnabled = true, PositionSmoothingSpeed = 5f };
            cam.Position = new Vector2(0, 0);
            cam.SetTargetPosition(new Vector2(0, 200));
            cam.Update(0.2f);
            AssertTrue(cam.GlobalPosition.Y > 0f, "Y axis moves toward target.");
            AssertTrue(cam.GlobalPosition.Y < 200f, "Y axis hasn't overshot.");
        }

        // ── 目标中途改变时跟随新目标 ──
        public static void TestTargetChange()
        {
            var cam = new Camera2D("C") { PositionSmoothingEnabled = true, PositionSmoothingSpeed = 10f };
            cam.Position = new Vector2(0, 0);
            cam.SetTargetPosition(new Vector2(100, 0));
            cam.Update(0.3f); // 部分跟随
            float x1 = cam.GlobalPosition.X;
            AssertTrue(x1 > 0f, "Moved toward first target.");

            cam.SetTargetPosition(new Vector2(-100, 0)); // 改目标
            cam.Update(0.3f);
            AssertTrue(cam.GlobalPosition.X < x1, "Camera reverses toward new target.");
        }

        // ── SetTargetPosition 与 GetTargetPosition 对称 ──
        public static void TestTargetRoundTrip()
        {
            var cam = new Camera2D("C");
            cam.SetTargetPosition(new Vector2(42, 17));
            AssertNear(42f, cam.GetTargetPosition().X, "Target X round-trip.");
            AssertNear(17f, cam.GetTargetPosition().Y, "Target Y round-trip.");
        }

        // ── 多步累积平滑：每步移动量递减（指数衰减）──
        public static void TestExponentialDecay()
        {
            var cam = new Camera2D("C") { PositionSmoothingEnabled = true, PositionSmoothingSpeed = 3f };
            cam.Position = new Vector2(0, 0);
            cam.SetTargetPosition(new Vector2(1000, 0));

            float prevStep = float.MaxValue;
            for (int i = 0; i < 10; i++)
            {
                float before = cam.GlobalPosition.X;
                cam.Update(0.1f);
                float step = cam.GlobalPosition.X - before;
                AssertTrue(step < prevStep, $"Step {i} smaller than previous (exponential decay).");
                prevStep = step;
            }
        }
    }
}
