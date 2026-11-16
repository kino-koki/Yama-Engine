// CelShading2DDepthTests.cs — Unity5 2D Toon Material + 逐帧动画播放器深度验证
// 覆盖:
//   * CelToonMaterial: 默认值不变式 + Clone 隔离 + BandCount/开关
//   * CelAnimationClip: 帧添加 + 总时长 + Loop + PlaybackSpeed
//   * CelAnimationPlayer: Play/Stop/Pause/Resume + Update 帧推进 + 循环 wrap + 非循环结束 + PlaybackSpeed

using System;
using System.Numerics;
using YamaEngine.Cel2D;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests
{
    public static class CelShading2DDepthTests
    {
        private const float Eps = 1e-4f;

        // ── 1. CelToonMaterial 默认值不变式 ──
        public static void TestMaterialDefaults()
        {
            var m = new CelToonMaterial();

            AssertTrue(MathF.Abs(m.BaseColor.X - 1.0f) < Eps, "BaseColor 默认=白");
            AssertEqual(3, m.BandCount, "BandCount 默认=3");
            AssertTrue(m.EnableBanding, "EnableBanding 默认=true");
            AssertTrue(m.EnableOutline, "EnableOutline 默认=true");
            AssertTrue(MathF.Abs(m.OutlineWidth - 2.0f) < Eps, "OutlineWidth 默认=2");
            AssertTrue(MathF.Abs(m.RimIntensity - 0.0f) < Eps, "RimIntensity 默认=0");
            AssertTrue(MathF.Abs(m.HalftoneDensity - 0.0f) < Eps, "HalftoneDensity 默认=0");

            // ShadowTint 默认冷色
            AssertTrue(m.ShadowTint.Z > m.ShadowTint.X, "ShadowTint 默认偏冷（B>R）");
        }

        // ── 2. CelToonMaterial Clone 隔离 ──
        public static void TestMaterialClone()
        {
            var orig = new CelToonMaterial
            {
                BandCount = 5,
                OutlineWidth = 3.5f,
                RimIntensity = 0.8f,
                EnableBanding = false,
                Emissive = new Vector4(1, 0.5f, 0, 1)
            };

            var clone = orig.Clone();
            AssertEqual(5, clone.BandCount, "Clone BandCount");
            AssertTrue(MathF.Abs(clone.OutlineWidth - 3.5f) < Eps, "Clone OutlineWidth");
            AssertTrue(MathF.Abs(clone.RimIntensity - 0.8f) < Eps, "Clone RimIntensity");
            AssertTrue(!clone.EnableBanding, "Clone EnableBanding=false");
            AssertTrue(MathF.Abs(clone.Emissive.Y - 0.5f) < Eps, "Clone Emissive.Y");

            // 修改克隆不影响原对象
            clone.BandCount = 8;
            clone.OutlineWidth = 0;
            AssertEqual(5, orig.BandCount, "修改克隆不影响原 BandCount");
            AssertTrue(MathF.Abs(orig.OutlineWidth - 3.5f) < Eps, "修改克隆不影响原 OutlineWidth");
        }

        // ── 3. CelAnimationClip 帧添加 + 总时长 ──
        public static void TestClipFramesAndDuration()
        {
            var clip = new CelAnimationClip("Idle");
            AssertEqual("Idle", clip.Name, "Clip 名");
            AssertEqual(0, clip.Frames.Count, "初始 0 帧");
            AssertTrue(MathF.Abs(clip.Duration - 0.0f) < Eps, "空 clip Duration=0");
            AssertTrue(clip.Loop, "默认 Loop=true");

            clip.AddFrame(new Vector4(0, 0, 0.25f, 1), 0.1f);
            clip.AddFrame(new Vector4(0.25f, 0, 0.5f, 1), 0.2f);
            clip.AddFrame(new Vector4(0.5f, 0, 0.75f, 1), 0.3f);

            AssertEqual(3, clip.Frames.Count, "添加后 3 帧");
            AssertTrue(MathF.Abs(clip.Duration - 0.6f) < Eps,
                $"总时长=0.1+0.2+0.3=0.6，实际={clip.Duration}");
        }

        // ── 4. Player Play/Stop/Pause/Resume 状态机 ──
        public static void TestPlayerStateTransitions()
        {
            var clip = new CelAnimationClip("Walk")
                .AddFrame(new Vector4(0, 0, 1, 1), 0.2f)
                .AddFrame(new Vector4(1, 0, 2, 1), 0.2f);

            var player = new CelAnimationPlayer();
            AssertEqual(CelPlaybackState.Stopped, player.State, "初始 Stopped");
            AssertTrue(player.Clip == null, "初始 Clip=null");

            player.Play(clip);
            AssertEqual(CelPlaybackState.Playing, player.State, "Play 后 Playing");
            AssertTrue(player.Clip == clip, "Clip 已设置");
            AssertEqual(0, player.FrameIndex, "Play 后 FrameIndex=0");

            player.Pause();
            AssertEqual(CelPlaybackState.Paused, player.State, "Pause 后 Paused");

            player.Resume();
            AssertEqual(CelPlaybackState.Playing, player.State, "Resume 后 Playing");

            player.Stop();
            AssertEqual(CelPlaybackState.Stopped, player.State, "Stop 后 Stopped");
            AssertEqual(0, player.FrameIndex, "Stop 后 FrameIndex=0");
        }

        // ── 5. Player Update 帧推进 ──
        public static void TestPlayerFrameAdvance()
        {
            var clip = new CelAnimationClip()
                .AddFrame(new Vector4(0, 0, 1, 1), 0.1f)    // 帧 0: 0-0.1
                .AddFrame(new Vector4(1, 0, 2, 1), 0.1f)    // 帧 1: 0.1-0.2
                .AddFrame(new Vector4(2, 0, 3, 1), 0.1f);   // 帧 2: 0.2-0.3

            var player = new CelAnimationPlayer();
            player.Play(clip);

            // t=0 → 帧 0
            Vector4 r0 = player.Update(0.0f);
            AssertEqual(0, player.FrameIndex, "t=0 帧 0");
            AssertTrue(MathF.Abs(r0.X - 0.0f) < Eps, "帧 0 SourceRect.x=0");

            // t=0.05 → 仍在帧 0
            player.Update(0.05f);
            AssertEqual(0, player.FrameIndex, "t=0.05 帧 0");

            // t=0.15 (累计 0.2) → 帧 1
            player.Update(0.1f);
            AssertEqual(1, player.FrameIndex, "累计 0.2 帧 1");

            // t=0.05 (累计 0.25) → 帧 2
            player.Update(0.05f);
            AssertEqual(2, player.FrameIndex, "累计 0.25 帧 2");
        }

        // ── 6. Player 循环 wrap ──
        public static void TestPlayerLoop()
        {
            var clip = new CelAnimationClip()
                .AddFrame(new Vector4(0, 0, 1, 1), 0.1f)
                .AddFrame(new Vector4(1, 0, 2, 1), 0.1f);
            AssertTrue(clip.Loop, "默认循环");

            var player = new CelAnimationPlayer();
            player.Play(clip);

            // 推进超过总时长 0.2 → 循环回帧 0
            player.Update(0.25f); // 0.25 % 0.2 = 0.05 → 帧 0
            AssertEqual(0, player.FrameIndex, "循环后回帧 0");

            // 再推进 0.1 → 累计 0.15 → 帧 1
            player.Update(0.1f);
            AssertEqual(1, player.FrameIndex, "循环后再推进到帧 1");
        }

        // ── 7. Player 非循环结束 ──
        public static void TestPlayerNoLoopEnd()
        {
            var clip = new CelAnimationClip()
                .AddFrame(new Vector4(0, 0, 1, 1), 0.1f)
                .AddFrame(new Vector4(1, 0, 2, 1), 0.1f);
            clip.Loop = false;

            var player = new CelAnimationPlayer();
            player.Play(clip);

            // 推进超过总时长 → 停在最后一帧，状态=Stopped
            player.Update(0.3f);
            AssertEqual(CelPlaybackState.Stopped, player.State, "非循环播完应 Stopped");
            AssertEqual(1, player.FrameIndex, "非循环停在最后一帧（帧 1）");
        }

        // ── 8. Player PlaybackSpeed 倍速 ──
        public static void TestPlayerPlaybackSpeed()
        {
            var clip = new CelAnimationClip()
                .AddFrame(new Vector4(0, 0, 1, 1), 0.1f)
                .AddFrame(new Vector4(1, 0, 2, 1), 0.1f)
                .AddFrame(new Vector4(2, 0, 3, 1), 0.1f);
            clip.PlaybackSpeed = 2.0f; // 2 倍速

            var player = new CelAnimationPlayer();
            player.Play(clip);

            // dt=0.1 → 实际推进 0.2 → 帧 2
            player.Update(0.1f);
            AssertEqual(2, player.FrameIndex, "2 倍速 dt=0.1 应到帧 2");

            // 0.5 倍速
            var clip2 = new CelAnimationClip()
                .AddFrame(new Vector4(0, 0, 1, 1), 0.1f)
                .AddFrame(new Vector4(1, 0, 2, 1), 0.1f);
            clip2.PlaybackSpeed = 0.5f;

            var player2 = new CelAnimationPlayer();
            player2.Play(clip2);
            player2.Update(0.1f); // 实际推进 0.05 → 帧 0
            AssertEqual(0, player2.FrameIndex, "0.5 倍速 dt=0.1 应仍在帧 0");
        }

        // ── 9. Player Pause 阻断推进 ──
        public static void TestPlayerPauseFreezesTime()
        {
            var clip = new CelAnimationClip()
                .AddFrame(new Vector4(0, 0, 1, 1), 0.1f)
                .AddFrame(new Vector4(1, 0, 2, 1), 0.1f);

            var player = new CelAnimationPlayer();
            player.Play(clip);
            player.Update(0.05f); // t=0.05 帧 0

            player.Pause();
            player.Update(0.5f); // 暂停期间 dt 不推进
            AssertEqual(0, player.FrameIndex, "暂停期间帧索引不变");
            AssertEqual(CelPlaybackState.Paused, player.State, "仍在 Paused");

            player.Resume();
            player.Update(0.1f); // t=0.15 → 帧 1
            AssertEqual(1, player.FrameIndex, "Resume 后继续推进到帧 1");
        }

        // ── 10. Player Stop 重置时间 ──
        public static void TestPlayerStopResets()
        {
            var clip = new CelAnimationClip()
                .AddFrame(new Vector4(0, 0, 1, 1), 0.1f)
                .AddFrame(new Vector4(1, 0, 2, 1), 0.1f);

            var player = new CelAnimationPlayer();
            player.Play(clip);
            player.Update(0.15f); // 帧 1
            AssertEqual(1, player.FrameIndex, "推进到帧 1");

            player.Stop();
            AssertEqual(0, player.FrameIndex, "Stop 后 FrameIndex=0");
            AssertEqual(CelPlaybackState.Stopped, player.State, "Stop 后 Stopped");

            // 重新 Play 从头开始
            player.Play(clip);
            AssertEqual(0, player.FrameIndex, "重新 Play 后 FrameIndex=0");
        }

        // ── 11. Player 无 Clip 时 Update 不崩溃 ──
        public static void TestPlayerNoClipSafe()
        {
            var player = new CelAnimationPlayer();
            Vector4 r = player.Update(0.1f);
            AssertTrue(MathF.Abs(r.X - 0.0f) < Eps, "无 Clip Update 返回默认 SourceRect");
            AssertEqual(CelPlaybackState.Stopped, player.State, "无 Clip 仍 Stopped");
        }

        // ── 12. Player NormalizedTime ──
        public static void TestPlayerNormalizedTime()
        {
            var clip = new CelAnimationClip()
                .AddFrame(new Vector4(0, 0, 1, 1), 0.1f)
                .AddFrame(new Vector4(1, 0, 2, 1), 0.1f);
            // 总时长 0.2

            var player = new CelAnimationPlayer();
            AssertTrue(MathF.Abs(player.NormalizedTime - 0.0f) < Eps, "无 Clip NormalizedTime=0");

            player.Play(clip);
            AssertTrue(MathF.Abs(player.NormalizedTime - 0.0f) < Eps, "Play 后初始 NormalizedTime=0");

            player.Update(0.1f); // t=0.1/0.2=0.5
            AssertTrue(MathF.Abs(player.NormalizedTime - 0.5f) < Eps,
                $"NormalizedTime 应=0.5，实际={player.NormalizedTime}");

            player.Update(0.05f); // t=0.15/0.2=0.75
            AssertTrue(MathF.Abs(player.NormalizedTime - 0.75f) < Eps,
                $"NormalizedTime 应=0.75，实际={player.NormalizedTime}");
        }

        // ── 13. CelSpriteData 纯数据默认值 ──
        public static void TestSpriteDataDefaults()
        {
            var s = new CelSpriteData();
            AssertEqual(string.Empty, s.TexturePath, "默认空路径");
            AssertTrue(MathF.Abs(s.Pivot.X - 0.5f) < Eps, "Pivot.X 默认=0.5");
            AssertTrue(MathF.Abs(s.Pivot.Y - 0.5f) < Eps, "Pivot.Y 默认=0.5");
            AssertTrue(!s.FlipX, "FlipX 默认=false");
            AssertTrue(!s.FlipY, "FlipY 默认=false");
            AssertTrue(s.Material != null, "默认 Material 非空");
            AssertTrue(s.Player == null, "Player 默认=null");
        }

        // ── 14. Clip AddFrames 批量添加 ──
        public static void TestClipAddFramesBatch()
        {
            var clip = new CelAnimationClip("Batch");
            var frames = new[]
            {
                new CelAnimationFrame(new Vector4(0, 0, 1, 1), 0.1f),
                new CelAnimationFrame(new Vector4(1, 0, 2, 1), 0.1f),
                new CelAnimationFrame(new Vector4(2, 0, 3, 1), 0.1f),
            };
            clip.AddFrames(frames);
            AssertEqual(3, clip.Frames.Count, "批量添加 3 帧");
            AssertTrue(MathF.Abs(clip.Duration - 0.3f) < Eps, "总时长=0.3");
        }
    }
}
