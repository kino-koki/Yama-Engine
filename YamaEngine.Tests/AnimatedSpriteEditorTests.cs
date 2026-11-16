// AnimatedSpriteEditorTests.cs —— 追平 Godot 3 的 SpriteFrames 编辑器工作流 + AnimatedSprite2D 播放控制

using System;
using System.Numerics;
using YamaEngine.UI;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests
{
    public static class AnimatedSpriteEditorTests
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

        private static SpriteFrames MakeFrames()
        {
            var f = new SpriteFrames();
            f.AddAnimation("idle", fps: 4f, loop: true);
            f.AddFrame("idle", new Rect2(0f, 0f, 16f, 16f));
            f.AddFrame("idle", new Rect2(16f, 0f, 16f, 16f));
            f.AddFrame("idle", new Rect2(32f, 0f, 16f, 16f));
            return f;
        }

        // ── AddAnimation + GetAnimationNames ──
        public static void TestAddAnimationAndListNames()
        {
            var f = new SpriteFrames();
            f.AddAnimation("idle");
            f.AddAnimation("walk");
            f.AddAnimation("run");
            var names = f.GetAnimationNames();
            AssertEqual(3, names.Count, "Three animation names.");
            AssertTrue(names.Contains("idle"), "Contains 'idle'.");
            AssertTrue(names.Contains("walk"), "Contains 'walk'.");
            AssertTrue(names.Contains("run"), "Contains 'run'.");
        }

        // ── RemoveAnimation 成功/失败 ──
        public static void TestRemoveAnimation()
        {
            var f = new SpriteFrames();
            f.AddAnimation("idle");
            f.AddAnimation("walk");
            AssertTrue(f.RemoveAnimation("walk"), "Remove 'walk' succeeds.");
            AssertEqual(1, f.GetAnimationNames().Count, "One remaining.");
            AssertFalse(f.RemoveAnimation("walk"), "Remove non-existent returns false.");
        }

        // ── SetAnimationFps + GetAnimationFps ──
        public static void TestSetAndGetFps()
        {
            var f = MakeFrames();
            AssertNear(4f, f.GetAnimationFps("idle"), "Initial FPS=4.");
            f.SetAnimationFps("idle", 12f);
            AssertNear(12f, f.GetAnimationFps("idle"), "Updated FPS=12.");
        }

        // ── SetLoop + GetAnimationLoop ──
        public static void TestSetAndGetLoop()
        {
            var f = MakeFrames();
            AssertTrue(f.GetAnimationLoop("idle"), "Initial loop=true.");
            f.SetLoop("idle", false);
            AssertFalse(f.GetAnimationLoop("idle"), "Updated loop=false.");
        }

        // ── GetFrameCount ──
        public static void TestGetFrameCount()
        {
            var f = MakeFrames();
            AssertEqual(3, f.GetFrameCount("idle"), "Idle has 3 frames.");
            AssertEqual(0, f.GetFrameCount("nonexistent"), "Non-existent = 0.");
        }

        // ── InsertFrame 在指定位置插入 ──
        public static void TestInsertFrameAtPosition()
        {
            var f = MakeFrames();
            // 插到 index=1
            f.InsertFrame("idle", 1, new Rect2(48f, 0f, 16f, 16f));
            AssertEqual(4, f.GetFrameCount("idle"), "Now 4 frames.");
            AssertNear(48f, f.GetFrameRegion("idle", 1).X, "Inserted at index 1.");
            AssertNear(16f, f.GetFrameRegion("idle", 2).X, "Old index 1 pushed to 2.");
        }

        // ── InsertFrame 夹紧到边界 ──
        public static void TestInsertFrameClampIndex()
        {
            var f = MakeFrames();
            f.InsertFrame("idle", 999, new Rect2(100f, 0f, 16f, 16f)); // 夹到末尾
            AssertEqual(4, f.GetFrameCount("idle"), "Clamped to end.");
            AssertNear(100f, f.GetFrameRegion("idle", 3).X, "Appended at end.");
        }

        // ── RemoveFrame ──
        public static void TestRemoveFrame()
        {
            var f = MakeFrames();
            f.RemoveFrame("idle", 1);
            AssertEqual(2, f.GetFrameCount("idle"), "Now 2 frames.");
            AssertNear(0f, f.GetFrameRegion("idle", 0).X, "Frame 0 unchanged.");
            AssertNear(32f, f.GetFrameRegion("idle", 1).X, "Frame 2 (now index 1) unchanged.");
        }

        // ── SetFrameRegion + GetFrameRegion ──
        public static void TestSetGetFrameRegion()
        {
            var f = MakeFrames();
            f.SetFrameRegion("idle", 0, new Rect2(99f, 0f, 16f, 16f));
            var r = f.GetFrameRegion("idle", 0);
            AssertNear(99f, r.X, "Updated region X.");
        }

        // ── Clear 清空所有动画 ──
        public static void TestClearAllAnimations()
        {
            var f = MakeFrames();
            f.Clear();
            AssertEqual(0, f.GetAnimationNames().Count, "No animations after clear.");
            AssertFalse(f.HasAnimation("idle"), "Idle removed.");
        }

        // ── AnimatedSprite2D Play/Pause/Resume/Stop 状态机 ──
        public static void TestAnimSpriteStateMachine()
        {
            var sprite = new AnimatedSprite2D(MakeFrames());
            AssertEqual(SpriteAnimState.Stopped, sprite.State, "Initial stopped.");

            sprite.Play("idle");
            AssertEqual(SpriteAnimState.Playing, sprite.State, "After Play → Playing.");
            AssertEqual("idle", sprite.Animation, "Current animation = idle.");

            sprite.Pause();
            AssertEqual(SpriteAnimState.Paused, sprite.State, "After Pause → Paused.");

            sprite.Resume();
            AssertEqual(SpriteAnimState.Playing, sprite.State, "After Resume → Playing.");

            sprite.Stop();
            AssertEqual(SpriteAnimState.Stopped, sprite.State, "After Stop → Stopped.");
            AssertEqual(0, sprite.FrameIndex, "Stop resets frame index.");
        }

        // ── AnimatedSprite2D 帧推进（FPS=4 → 每帧 0.25s） ──
        public static void TestAnimSpriteFrameAdvancement()
        {
            var sprite = new AnimatedSprite2D(MakeFrames());
            sprite.Play("idle");

            // 0.1s < 0.25s → 仍在帧 0
            sprite.Update(0.1f);
            AssertEqual(0, sprite.FrameIndex, "At 0.1s still frame 0.");

            // 再 0.2s 累计 0.3s > 0.25s → 帧 1
            sprite.Update(0.2f);
            AssertEqual(1, sprite.FrameIndex, "At 0.3s total → frame 1.");

            // 再 0.3s 累计 0.6s → 帧 1(0.05s) → 累计过第二帧 0.25s → 帧 2
            sprite.Update(0.3f);
            AssertEqual(2, sprite.FrameIndex, "At 0.6s total → frame 2.");
        }

        // ── AnimatedSprite2D 循环回绕（3 帧，loop=true） ──
        public static void TestAnimSpriteLoopWrap()
        {
            var sprite = new AnimatedSprite2D(MakeFrames());
            sprite.Play("idle");
            // 走 4 帧时间（1.0s @ FPS=4）→ 3帧循环后回帧0（1完整轮回到0）
            sprite.Update(0.25f); // frame 1
            sprite.Update(0.25f); // frame 2
            sprite.Update(0.25f); // 回绕到 frame 0
            AssertEqual(0, sprite.FrameIndex, "Loop wraps back to frame 0.");
        }

        // ── 非循环动画停在末帧 + FinishedCount+1 ──
        public static void TestAnimSpriteNonLoopEnd()
        {
            var f = new SpriteFrames();
            f.AddAnimation("die", fps: 4f, loop: false);
            f.AddFrame("die", new Rect2(0f, 0f, 16f, 16f));
            f.AddFrame("die", new Rect2(16f, 0f, 16f, 16f));

            var sprite = new AnimatedSprite2D(f);
            sprite.Play("die");
            sprite.Update(0.25f); // 帧 1
            sprite.Update(0.25f); // 超末帧 → 停在帧1，FinishedCount=1
            AssertEqual(SpriteAnimState.Stopped, sprite.State, "Non-loop ends → Stopped.");
            AssertEqual(1, sprite.FrameIndex, "Stopped at last frame.");
            AssertEqual(1, sprite.FinishedCount, "FinishedCount incremented.");
        }

        // ── SpeedScale=2 双速 ──
        public static void TestAnimSpriteSpeedScale()
        {
            var sprite = new AnimatedSprite2D(MakeFrames());
            sprite.SpeedScale = 2f; // 2x速
            sprite.Play("idle");

            // 0.2s * 2 = 0.4s > 0.25s → 帧1
            sprite.Update(0.2f);
            AssertEqual(1, sprite.FrameIndex, "SpeedScale=2 → frame advance faster.");
        }

        // ── SpeedScale=0 暂停帧推进 ──
        public static void TestAnimSpriteSpeedScaleZero()
        {
            var sprite = new AnimatedSprite2D(MakeFrames());
            sprite.SpeedScale = 0f;
            sprite.Play("idle");
            sprite.Update(1.0f);
            AssertEqual(0, sprite.FrameIndex, "SpeedScale=0 → no advancement.");
        }

        // ── 切换动画：Play 重置到新动画帧0 ──
        public static void TestAnimSpriteSwitchAnimation()
        {
            var f = MakeFrames();
            f.AddAnimation("walk", fps: 8f, loop: true);
            f.AddFrame("walk", new Rect2(0f, 32f, 16f, 16f));
            f.AddFrame("walk", new Rect2(16f, 32f, 16f, 16f));

            var sprite = new AnimatedSprite2D(f);
            sprite.Play("idle");
            sprite.Update(0.25f); // 推到帧1
            AssertEqual(1, sprite.FrameIndex, "Idle frame 1.");

            sprite.Play("walk"); // 切换 → 重置帧0
            AssertEqual("walk", sprite.Animation, "Current = walk.");
            AssertEqual(0, sprite.FrameIndex, "Frame reset on switch.");
        }

        // ── Play 不存在的动画抛异常 ──
        public static void TestAnimSpritePlayUnknownThrows()
        {
            var f = MakeFrames();
            var sprite = new AnimatedSprite2D(f);
            AssertTrue(ThrowsException(() => sprite.Play("nonexistent")), "Play unknown throws.");
        }

        // ── Pause 状态下 Update 不推进帧 ──
        public static void TestAnimSpritePauseStopsFrameAdvance()
        {
            var sprite = new AnimatedSprite2D(MakeFrames());
            sprite.Play("idle");
            sprite.Update(0.25f);
            AssertEqual(1, sprite.FrameIndex, "Frame 1 before pause.");

            sprite.Pause();
            sprite.Update(1.0f); // 时间流失但暂停
            AssertEqual(1, sprite.FrameIndex, "Frame unchanged in pause.");
        }

        private static bool ThrowsException(Action action)
        {
            try { action(); return false; }
            catch { return true; }
        }
    }
}
