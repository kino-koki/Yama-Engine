// Camera2DDragMarginTests.cs —— Camera2D 拖拽边距（drag_margin）深度验证

using System;
using System.Numerics;
using YamaEngine.UI;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests
{
    public static class Camera2DDragMarginTests
    {
        private static void AssertTrue(bool cond, string msg) { if (!cond) throw new Exception(msg); }
        private static void AssertFalse(bool cond, string msg) { if (cond) throw new Exception(msg); }
        private static void AssertNear(float got, float want, string msg, float tol = 1e-3f)
        { if (MathF.Abs(got - want) > tol) throw new Exception($"{msg} (got {got}, want {want})."); }
        private static void AssertEqual<T>(T got, T want, string msg)
        { if (!Equals(got, want)) throw new Exception($"{msg} (got {got}, want {want})."); }

        // ── 默认状态：关闭，margin_h/v = 0.2 ──
        public static void TestDefaults()
        {
            var cam = new Camera2D("C");
            AssertFalse(cam.DragMarginEnabled, "默认关闭.");
            AssertNear(0.2f, cam.DragMarginH, "默认 DragMarginH=0.2.");
            AssertNear(0.2f, cam.DragMarginV, "默认 DragMarginV=0.2.");
        }

        // ── 目标在 margin 内：相机不动 ──
        public static void TestTargetInsideMarginNoMove()
        {
            var cam = new Camera2D("C") { ViewportSize = new Vector2(1920, 1080) };
            cam.GlobalPosition = new Vector2(0, 0);
            cam.DragMarginEnabled = true;
            cam.DragMarginH = 0.2f; // 半宽 960 * 0.2 = 192
            cam.SetTargetPosition(new Vector2(100, 0)); // 100 < 192 → 在 margin 内
            var effective = cam.ComputeDragTarget();
            AssertNear(0f, effective.X, "目标在 margin 内，有效目标不动.");
        }

        // ── 目标超出 margin：夹到 target - margin（相机只追到边界）──
        public static void TestTargetOutsideMarginClamped()
        {
            var cam = new Camera2D("C") { ViewportSize = new Vector2(1920, 1080) };
            cam.GlobalPosition = new Vector2(0, 0);
            cam.DragMarginEnabled = true;
            cam.DragMarginH = 0.2f; // 960 * 0.2 = 192
            cam.SetTargetPosition(new Vector2(500, 0)); // 500 > 192 → 有效目标 = 500-192 = 308
            var effective = cam.ComputeDragTarget();
            AssertNear(308f, effective.X, "超出 margin，有效目标 = target-margin = 308.");
        }

        // ── drag_margin 关闭时：直接跟随目标（忽略 margin）──
        public static void TestDisabledFollowsTargetDirectly()
        {
            var cam = new Camera2D("C") { ViewportSize = new Vector2(1920, 1080) };
            cam.GlobalPosition = new Vector2(0, 0);
            cam.DragMarginEnabled = false;
            cam.SetTargetPosition(new Vector2(500, 0));
            var effective = cam.ComputeDragTarget();
            AssertNear(500f, effective.X, "关闭 drag_margin 时直接返回目标.");
        }

        // ── Y 轴同样截断 ──
        public static void TestVerticalClamp()
        {
            var cam = new Camera2D("C") { ViewportSize = new Vector2(1920, 1080) };
            cam.GlobalPosition = new Vector2(0, 0);
            cam.DragMarginEnabled = true;
            cam.DragMarginV = 0.2f; // 540 * 0.2 = 108
            cam.SetTargetPosition(new Vector2(0, 300)); // 300 > 108 → 300-108=192
            var effective = cam.ComputeDragTarget();
            AssertNear(192f, effective.Y, "Y 超出 margin，有效目标 = 300-108=192.");
        }

        // ── 负方向同样截断 ──
        public static void TestNegativeDirectionClamp()
        {
            var cam = new Camera2D("C") { ViewportSize = new Vector2(1920, 1080) };
            cam.GlobalPosition = new Vector2(0, 0);
            cam.DragMarginEnabled = true;
            cam.DragMarginH = 0.2f;
            cam.SetTargetPosition(new Vector2(-500, 0)); // -500 < -192 → -500+192=-308
            var effective = cam.ComputeDragTarget();
            AssertNear(-308f, effective.X, "负方向有效目标 = target+margin = -308.");
        }

        // ── drag_margin + smoothing：相机只在目标出 margin 时跟随 ──
        public static void TestDragMarginWithSmoothing()
        {
            var cam = new Camera2D("C") { ViewportSize = new Vector2(1920, 1080) };
            cam.GlobalPosition = new Vector2(0, 0);
            cam.PositionSmoothingEnabled = true;
            cam.PositionSmoothingSpeed = 10f;
            cam.DragMarginEnabled = true;
            cam.DragMarginH = 0.2f; // 192
            cam.SetTargetPosition(new Vector2(100, 0)); // 在 margin 内
            cam.Update(1f); // 长时间 dt
            AssertNear(0f, cam.GlobalPosition.X, "目标在 margin 内，平滑跟随不动.", 1f);
        }

        // ── 目标出 margin 后：相机朝 target-margin 收敛 ──
        public static void TestDragMarginFollowWhenOutside()
        {
            var cam = new Camera2D("C") { ViewportSize = new Vector2(1920, 1080) };
            cam.GlobalPosition = new Vector2(0, 0);
            cam.PositionSmoothingEnabled = true;
            cam.PositionSmoothingSpeed = 5f;
            cam.DragMarginEnabled = true;
            cam.DragMarginH = 0.2f; // 192
            cam.SetTargetPosition(new Vector2(600, 0)); // 超出 margin
            for (int i = 0; i < 200; i++) cam.Update(0.05f); // 10s 收敛
            // 相机最终收敛到 target-margin = 600-192 = 408（不是 target 600）
            AssertNear(408f, cam.GlobalPosition.X, "收敛到 target-margin = 408.", 1f);
        }

        // ── DragMarginH = 0：立即跟随（无死区）──
        public static void TestZeroMarginImmediateFollow()
        {
            var cam = new Camera2D("C") { ViewportSize = new Vector2(1920, 1080) };
            cam.GlobalPosition = new Vector2(0, 0);
            cam.DragMarginEnabled = true;
            cam.DragMarginH = 0f; // margin=0 → 任何偏移都触发跟随
            cam.SetTargetPosition(new Vector2(10, 0));
            var effective = cam.ComputeDragTarget();
            AssertNear(10f, effective.X, "margin=0 时任何偏移都触发跟随（10）.");
        }

        // ── ComputeDragTarget 不改变实际位置 ──
        public static void TestComputeDoesNotMutate()
        {
            var cam = new Camera2D("C") { ViewportSize = new Vector2(1920, 1080) };
            cam.GlobalPosition = new Vector2(0, 0);
            cam.DragMarginEnabled = true;
            cam.SetTargetPosition(new Vector2(1000, 0));
            _ = cam.ComputeDragTarget();
            AssertNear(0f, cam.GlobalPosition.X, "ComputeDragTarget 只读不改位置.");
        }

        // ── 对称性：左右等距目标 → 相机对称不动 ──
        public static void TestSymmetricNoMove()
        {
            var cam = new Camera2D("C") { ViewportSize = new Vector2(1920, 1080) };
            cam.GlobalPosition = new Vector2(0, 0);
            cam.DragMarginEnabled = true;
            cam.DragMarginH = 0.2f; // 192
            cam.SetTargetPosition(new Vector2(150, 0)); // 在 margin 内
            var e1 = cam.ComputeDragTarget();
            cam.SetTargetPosition(new Vector2(-150, 0)); // 对称负方向
            var e2 = cam.ComputeDragTarget();
            AssertNear(0f, e1.X, "左目标不动.");
            AssertNear(0f, e2.X, "右目标也不动（对称）.");
        }

        // ── DragMarginEnabled 切换：关闭后再开启，行为恢复 ──
        public static void TestToggleEnabled()
        {
            var cam = new Camera2D("C") { ViewportSize = new Vector2(1920, 1080) };
            cam.GlobalPosition = new Vector2(0, 0);
            cam.SetTargetPosition(new Vector2(500, 0));
            cam.DragMarginEnabled = false;
            AssertNear(500f, cam.ComputeDragTarget().X, "关闭时跟随目标.");
            cam.DragMarginEnabled = true;
            cam.DragMarginH = 0.2f;
            AssertNear(308f, cam.ComputeDragTarget().X, "开启后有效目标 = 500-192 = 308.");
        }
    }
}
