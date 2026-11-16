// Sprite2DCameraTests.cs —— 追平 Godot 3 的 Sprite2D / AnimatedSprite2D / Camera2D
// 覆盖：Sprite2D 变换/翻转/偏移、AnimatedSprite2D 帧推进/循环/非循环/暂停/SpeedScale、
//       Camera2D 世界↔屏幕投影/缩放/边界夹紧/当前相机。

using System;
using System.Collections.Generic;
using System.Numerics;
using YamaEngine.UI;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests
{
    public static class Sprite2DCameraTests
    {
        // ── Sprite2D 基础：变换继承 Node2D ──
        public static void TestSprite2DInheritsTransform()
        {
            var s = new Sprite2D("Hero") { Position = new Vector2(100, 50), Rotation = 0.5f, Scale = new Vector2(2, 2) };
            AssertNear(100f, s.GlobalPosition.X, 0.01f, "Sprite2D global X must follow Position.");
            AssertNear(50f, s.GlobalPosition.Y, 0.01f, "Sprite2D global Y must follow Position.");
            AssertNear(0.5f, s.Rotation, 0.001f, "Sprite2D inherits Rotation.");
        }

        // ── Sprite2D GetCenterGlobal 含 Offset ──
        public static void TestSprite2DCenterGlobalWithOffset()
        {
            var s = new Sprite2D("S") { Position = new Vector2(10, 20) };
            s.Offset = new Vector2(5, 0);
            var c = s.GetCenterGlobal();
            AssertNear(15f, c.X, 0.01f, "CenterGlobal X = Position.X(10) + Offset.X(5).");
            AssertNear(20f, c.Y, 0.01f, "CenterGlobal Y = Position.Y.");
        }

        // ── Sprite2D 翻转标志 ──
        public static void TestSprite2DFlipFlags()
        {
            var s = new Sprite2D("S");
            AssertTrue(!s.FlipH && !s.FlipV, "Default no flip.");
            s.FlipH = true;
            s.FlipV = true;
            AssertTrue(s.FlipH && s.FlipV, "Flip flags must be settable.");
        }

        // ── SpriteFrames 注册 + 取帧数 ──
        public static void TestSpriteFramesRegisterAndCount()
        {
            var sf = new SpriteFrames();
            sf.AddAnimation("idle", fps: 8f, loop: true)
              .AddFrame("idle", new Rect2(0, 0, 32, 32))
              .AddFrame("idle", new Rect2(32, 0, 32, 32))
              .AddFrame("idle", new Rect2(64, 0, 32, 32));
            AssertTrue(sf.HasAnimation("idle"), "idle must be registered.");
            AssertTrue(!sf.HasAnimation("missing"), "missing must not be registered.");
            var a = new AnimatedSprite2D(sf);
            a.Play("idle");
            AssertEqual(3, a.FrameCount, "idle must have 3 frames.");
        }

        // ── AnimatedSprite2D 帧推进（按 FPS） ──
        public static void TestAnimatedSprite2DFrameAdvances()
        {
            var sf = new SpriteFrames();
            sf.AddAnimation("walk", fps: 10f, loop: true)
              .AddFrame("walk", new Rect2(0, 0, 1, 1))
              .AddFrame("walk", new Rect2(1, 0, 1, 1))
              .AddFrame("walk", new Rect2(2, 0, 1, 1));
            var a = new AnimatedSprite2D(sf);
            a.Play("walk");
            AssertEqual(0, a.FrameIndex, "Start at frame 0.");

            a.Update(0.05f);  // 半帧 @10fps
            AssertEqual(0, a.FrameIndex, "0.05s at 10fps must stay frame 0.");

            a.Update(0.05f);  // 累计 0.1s = 1 帧
            AssertEqual(1, a.FrameIndex, "0.1s at 10fps must advance to frame 1.");
        }

        // ── AnimatedSprite2D 循环回绕 ──
        public static void TestAnimatedSprite2DLoopWraps()
        {
            var sf = new SpriteFrames();
            sf.AddAnimation("loop", fps: 10f, loop: true)
              .AddFrame("loop", new Rect2(0, 0, 1, 1))
              .AddFrame("loop", new Rect2(1, 0, 1, 1));
            var a = new AnimatedSprite2D(sf);
            a.Play("loop");

            a.Update(0.1f);   // 第 1 帧
            a.Update(0.1f);   // 回绕到 0
            AssertEqual(0, a.FrameIndex, "2-frame loop after 2 frames must wrap to 0.");
            AssertEqual(SpriteAnimState.Playing, a.State, "Loop anim must stay Playing.");
            AssertEqual(0, a.FinishedCount, "Loop anim never counts as finished.");
        }

        // ── AnimatedSprite2D 非循环停在末帧 ──
        public static void TestAnimatedSprite2DNonLoopStopsAtEnd()
        {
            var sf = new SpriteFrames();
            sf.AddAnimation("once", fps: 10f, loop: false)
              .AddFrame("once", new Rect2(0, 0, 1, 1))
              .AddFrame("once", new Rect2(1, 0, 1, 1));
            var a = new AnimatedSprite2D(sf);
            a.Play("once");

            a.Update(0.1f);  // frame 1 (last)
            AssertEqual(1, a.FrameIndex, "After 1 frame, at last frame index 1.");

            a.Update(0.1f);  // 尝试越界 → 停在末帧
            AssertEqual(1, a.FrameIndex, "Non-loop must clamp to last frame.");
            AssertEqual(SpriteAnimState.Stopped, a.State, "Non-loop must stop at end.");
            AssertEqual(1, a.FinishedCount, "Non-loop completion must count once.");

            a.Update(0.5f);  // 继续更新不重复计数
            AssertEqual(1, a.FinishedCount, "Stopped anim must not recount.");
        }

        // ── AnimatedSprite2D Pause/Resume ──
        public static void TestAnimatedSprite2DPauseResume()
        {
            var sf = new SpriteFrames();
            sf.AddAnimation("idle", fps: 10f, loop: true)
              .AddFrame("idle", new Rect2(0, 0, 1, 1))
              .AddFrame("idle", new Rect2(1, 0, 1, 1))
              .AddFrame("idle", new Rect2(2, 0, 1, 1));
            var a = new AnimatedSprite2D(sf);
            a.Play("idle");

            a.Update(0.1f);
            AssertEqual(1, a.FrameIndex, "At frame 1.");
            a.Pause();
            AssertEqual(SpriteAnimState.Paused, a.State, "Pause must set Paused.");
            a.Update(0.5f);
            AssertEqual(1, a.FrameIndex, "Paused anim must not advance.");
            a.Resume();
            a.Update(0.1f);
            AssertEqual(2, a.FrameIndex, "Resume must continue advancing.");
        }

        // ── AnimatedSprite2D SpeedScale ──
        public static void TestAnimatedSprite2DSpeedScale()
        {
            var sf = new SpriteFrames();
            sf.AddAnimation("run", fps: 10f, loop: false)
              .AddFrame("run", new Rect2(0, 0, 1, 1))
              .AddFrame("run", new Rect2(1, 0, 1, 1));
            var a = new AnimatedSprite2D(sf);
            a.SpeedScale = 2f;
            a.Play("run");

            a.Update(0.05f);  // 2x 速度 → 等效 0.1s = 1 帧 @10fps
            AssertEqual(1, a.FrameIndex, "SpeedScale=2 with dt=0.05 must advance 1 frame.");
        }

        // ── Camera2D 世界→屏幕（无旋转，居中） ──
        public static void TestCamera2DWorldToScreen()
        {
            var cam = new Camera2D("Cam")
            {
                Position = new Vector2(0, 0),
                Zoom = 1f,
                ViewportSize = new Vector2(1920, 1080),
            };
            cam.MakeCurrent();
            var screen = cam.WorldToScreen(new Vector2(0, 0));
            AssertNear(960f, screen.X, 0.01f, "World origin maps to viewport center X.");
            AssertNear(540f, screen.Y, 0.01f, "World origin maps to viewport center Y.");
        }

        // ── Camera2D 缩放投影：zoom=2 放大 ──
        public static void TestCamera2DZoomProjection()
        {
            var cam = new Camera2D("Cam")
            {
                Position = new Vector2(0, 0),
                Zoom = 2f,
                ViewportSize = new Vector2(1920, 1080),
            };
            var screen = cam.WorldToScreen(new Vector2(100, 0));
            AssertNear(960f + 200f, screen.X, 0.01f, "Zoom=2: world (100,0).X must be 960+200.");
            AssertNear(540f, screen.Y, 0.01f, "Zoom=2: world (100,0).Y must stay 540.");

            // 反投影
            var world = cam.ScreenToWorld(screen);
            AssertNear(100f, world.X, 0.01f, "ScreenToWorld must invert WorldToScreen X.");
            AssertNear(0f, world.Y, 0.01f, "ScreenToWorld must invert WorldToScreen Y.");
        }

        // ── Camera2D 边界夹紧 ──
        public static void TestCamera2DLimitBounds()
        {
            var cam = new Camera2D("Cam")
            {
                Position = new Vector2(5000, 0),  // 远超右边界
                Zoom = 1f,
                ViewportSize = new Vector2(1920, 1080),
                LimitEnabled = true,
                LimitLeft = 0f, LimitTop = 0f,
                LimitRight = 2000f, LimitBottom = 1000f,
            };
            // halfW=960, halfH=540。X 中心夹到 [960,1040] → 5000→1040。
            // Y 边界范围(1000) < 视野高(1080)，maxY<minY → 不夹紧，中心 Y=0。
            var (minX, minY, maxX, maxY) = cam.GetVisibleBounds();
            AssertNear(80f, minX, 0.01f, "minX = clampedCenterX(1040) - halfW(960) = 80.");
            AssertNear(2000f, maxX, 0.01f, "maxX = clampedCenterX(1040) + halfW(960) = 2000.");
            AssertNear(-540f, minY, 0.01f, "minY = center(0) - halfH(540) (Y not clamped).");
            AssertNear(540f, maxY, 0.01f, "maxY = center(0) + halfH(540) (Y not clamped).");
        }

        // ── Camera2D 当前相机切换 ──
        public static void TestCamera2DCurrentSwitch()
        {
            Camera2D.ClearCurrent();
            AssertTrue(Camera2D.Current == null, "No current camera after ClearCurrent.");
            var cam = new Camera2D("Main");
            cam.MakeCurrent();
            AssertTrue(Camera2D.Current == cam, "MakeCurrent must set current.");
        }

        private static void AssertNear(float got, float want, float tol, string msg)
        {
            if (MathF.Abs(got - want) > tol)
                throw new Exception($"{msg} (got {got:F4}, want {want:F4}).");
        }
    }
}
