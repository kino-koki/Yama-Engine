// CelSkeletalAnimation2DDepthTests.cs — Unity5 2D 骨骼动画（Spine 等价物）深度验证
// 覆盖:
//   * CelBone2D: 默认值 + LocalMatrix
//   * CelPose2D: 构造 + SetTransform 边界
//   * CelSkeleton2D: AddBone/GetBone/ParentIndex 越界异常/SetPose/GetBoneWorldPosition/TransformPoint
//   * CelSkinning2D: 线性蒙皮加权混合
//   * CelBoneTrack2D: Add/Sample 关键帧插值 + 边界
//   * CelSkeletalClip2D: AddTrack/Duration/Sample
//   * CelSkeletalAnimator2D: Play/Update/Pause/Resume/Stop + Loop + 非循环停止 + PlaybackSpeed

using System;
using System.Numerics;
using YamaEngine.Cel2D;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests
{
    public static class CelSkeletalAnimation2DDepthTests
    {
        private const float Eps = 1e-3f;

        private static bool Near(float a, float b, float e = Eps) => MathF.Abs(a - b) < e;

        // ── 1. CelBone2D 默认值 ──
        public static void TestBoneDefaults()
        {
            var b = new CelBone2D();
            AssertEqual("Bone", b.Name, "默认名=Bone");
            AssertEqual(-1, b.ParentIndex, "默认无父");
            AssertEqual(Vector2.Zero, b.LocalPosition, "默认 LocalPosition=Zero");
            AssertEqual(Vector2.One, b.LocalScale, "默认 LocalScale=One");
            AssertEqual(0f, b.LocalRotation, "默认 Rotation=0");
        }

        // ── 2. CelPose2D 构造 + SetTransform 边界 ──
        public static void TestPoseDefaults()
        {
            var pose = new CelPose2D(3);
            AssertEqual(3, pose.Count, "Count=3");
            pose.SetTransform(1, new Vector2(5, 6), 0.5f, new Vector2(2, 2));
            AssertEqual(new Vector2(5, 6), pose.Transforms[1].Position, "SetTransform 位置");
            AssertEqual(0.5f, pose.Transforms[1].Rotation, "SetTransform 旋转");

            pose.SetTransform(-1, Vector2.One, 0, Vector2.One); // 负索引不崩
            pose.SetTransform(99, Vector2.One, 0, Vector2.One); // 越界不崩
        }

        // ── 3. Skeleton AddBone 顺序约束 ──
        public static void TestSkeletonAddBoneOrder()
        {
            var skel = new CelSkeleton2D();
            int root = skel.AddBone("root", -1, Vector2.Zero);
            AssertEqual(0, root, "根骨骼索引=0");

            int child = skel.AddBone("child", 0, new Vector2(10, 0));
            AssertEqual(1, child, "子骨骼索引=1");

            bool threw = false;
            try { skel.AddBone("bad", 5, Vector2.Zero); }
            catch (ArgumentOutOfRangeException) { threw = true; }
            AssertTrue(threw, "父索引越界抛 ArgumentOutOfRangeException");
        }

        // ── 4. SetPose 世界矩阵传递（父→子）──
        public static void TestSetPoseChain()
        {
            var skel = new CelSkeleton2D();
            skel.AddBone("root", -1, Vector2.Zero);
            skel.AddBone("child", 0, Vector2.Zero);

            var pose = new CelPose2D(2);
            pose.SetTransform(0, new Vector2(100, 50), 0f, Vector2.One);
            pose.SetTransform(1, new Vector2(10, 0), 0f, Vector2.One);
            skel.SetPose(pose);

            AssertTrue(Near(skel.GetBoneWorldPosition(0).X, 100f), "根世界 X=100");
            AssertTrue(Near(skel.GetBoneWorldPosition(0).Y, 50f), "根世界 Y=50");
            AssertTrue(Near(skel.GetBoneWorldPosition(1).X, 110f), "子世界 X=110");
            AssertTrue(Near(skel.GetBoneWorldPosition(1).Y, 50f), "子世界 Y=50");
        }

        // ── 5. SetPose 旋转累积 ──
        public static void TestSetPoseRotationChain()
        {
            var skel = new CelSkeleton2D();
            skel.AddBone("root", -1, Vector2.Zero);
            skel.AddBone("arm", 0, Vector2.Zero);

            var pose = new CelPose2D(2);
            pose.SetTransform(0, Vector2.Zero, MathF.PI / 2f, Vector2.One); // 根转 90°
            pose.SetTransform(1, new Vector2(10, 0), 0f, Vector2.One);      // arm 沿 +X 延伸 10
            skel.SetPose(pose);

            // arm 世界位置 = 根旋转 90° 后 (10,0) → (0,10)
            var armPos = skel.GetBoneWorldPosition(1);
            AssertTrue(Near(armPos.X, 0f, 0.01f) && Near(armPos.Y, 10f, 0.01f),
                $"arm 世界位置≈(0,10)，实际 {armPos}");
        }

        // ── 6. TransformPoint ──
        public static void TestTransformPoint()
        {
            var skel = new CelSkeleton2D();
            skel.AddBone("root", -1, Vector2.Zero);

            var pose = new CelPose2D(1);
            pose.SetTransform(0, new Vector2(100, 0), 0f, Vector2.One);
            skel.SetPose(pose);

            var world = skel.TransformPoint(0, new Vector2(5, 0));
            AssertTrue(Near(world.X, 105f), $"局部 (5,0) + 根 (100,0) = (105,0)，实际 {world}");
        }

        // ── 7. 蒙皮无权重返回原位置 ──
        public static void TestSkinningNoWeights()
        {
            var skel = new CelSkeleton2D();
            skel.AddBone("root", -1, Vector2.Zero);
            skel.SetPose(new CelPose2D(1));

            var v = new CelSkinVertex2D { Position = new Vector2(3, 4) };
            var result = CelSkinning2D.Apply(skel, v);
            AssertEqual(new Vector2(3, 4), result, "无权重返回原位置");
        }

        // ── 8. 蒙皮加权混合 ──
        public static void TestSkinningBlend()
        {
            var skel = new CelSkeleton2D();
            skel.AddBone("A", -1, Vector2.Zero);
            skel.AddBone("B", -1, Vector2.Zero);

            var pose = new CelPose2D(2);
            pose.SetTransform(0, Vector2.Zero, 0f, Vector2.One);
            pose.SetTransform(1, new Vector2(100, 0), 0f, Vector2.One);
            skel.SetPose(pose);

            var v = new CelSkinVertex2D { Position = new Vector2(0, 0) };
            v.AddWeight(0, 0.5f).AddWeight(1, 0.5f); // 两骨骼各占 50%

            var result = CelSkinning2D.Apply(skel, v);
            // A 世界原点 (0,0)，B 世界原点 (100,0)，各 50% → (50,0)
            AssertTrue(Near(result.X, 50f), $"蒙皮混合 X=50，实际 {result.X}");
        }

        // ── 9. Track 空关键帧采样 ──
        public static void TestTrackEmpty()
        {
            var track = new CelBoneTrack2D(0);
            var t = track.Sample(0.5f);
            AssertEqual(Vector2.Zero, t.Position, "空轨道采样=Zero");
            AssertEqual(Vector2.One, t.Scale, "空轨道采样 Scale=One");
        }

        // ── 10. Track 单关键帧采样 ──
        public static void TestTrackSingleKey()
        {
            var track = new CelBoneTrack2D(0);
            track.Add(0f, new Vector2(5, 5), 0.5f, new Vector2(2, 2));
            var t = track.Sample(10f); // 任意时间都返回该帧
            AssertEqual(new Vector2(5, 5), t.Position, "单帧采样位置");
        }

        // ── 11. Track 两关键帧线性插值 ──
        public static void TestTrackLinearInterp()
        {
            var track = new CelBoneTrack2D(0);
            track.Add(0f, Vector2.Zero, 0f, Vector2.One);
            track.Add(1f, new Vector2(10, 0), 0f, Vector2.One);

            var t0 = track.Sample(0f);
            var tHalf = track.Sample(0.5f);
            var tEnd = track.Sample(1f);

            AssertEqual(Vector2.Zero, t0.Position, "t=0 起点");
            AssertTrue(Near(tHalf.Position.X, 5f), $"t=0.5 X=5，实际 {tHalf.Position.X}");
            AssertEqual(new Vector2(10, 0), tEnd.Position, "t=1 终点");
        }

        // ── 12. Track 采样超范围钳制 ──
        public static void TestTrackClampRange()
        {
            var track = new CelBoneTrack2D(0);
            track.Add(0f, Vector2.Zero, 0f, Vector2.One);
            track.Add(1f, new Vector2(10, 0), 0f, Vector2.One);

            AssertEqual(Vector2.Zero, track.Sample(-1f).Position, "负时间钳到首帧");
            AssertEqual(new Vector2(10, 0), track.Sample(5f).Position, "超时钳到末帧");
        }

        // ── 13. Clip Duration 取轨道最大时间 ──
        public static void TestClipDuration()
        {
            var clip = new CelSkeletalClip2D("test");
            AssertEqual(0f, clip.Duration, "空剪辑 Duration=0");

            var t1 = new CelBoneTrack2D(0);
            t1.Add(0f, Vector2.Zero, 0, Vector2.One).Add(0.5f, new Vector2(5, 0), 0, Vector2.One);
            var t2 = new CelBoneTrack2D(1);
            t2.Add(0f, Vector2.Zero, 0, Vector2.One).Add(1.5f, new Vector2(10, 0), 0, Vector2.One);

            clip.AddTrack(t1).AddTrack(t2);
            AssertEqual(1.5f, clip.Duration, "Duration=最长轨道末帧时间");
        }

        // ── 14. Clip Sample 跨轨道 ──
        public static void TestClipSampleMultiTrack()
        {
            var clip = new CelSkeletalClip2D("walk");
            var t0 = new CelBoneTrack2D(0);
            t0.Add(0f, new Vector2(0, 0), 0, Vector2.One).Add(1f, new Vector2(10, 0), 0, Vector2.One);
            var t1 = new CelBoneTrack2D(1);
            t1.Add(0f, new Vector2(0, 5), 0, Vector2.One).Add(1f, new Vector2(0, 15), 0, Vector2.One);
            clip.AddTrack(t0).AddTrack(t1);

            var pose = clip.Sample(0.5f, 2);
            AssertTrue(Near(pose.Transforms[0].Position.X, 5f), "骨骼 0 t=0.5 X=5");
            AssertTrue(Near(pose.Transforms[1].Position.Y, 10f), "骨骼 1 t=0.5 Y=10");
        }

        // ── 15. Animator Play/Update 推进 ──
        public static void TestAnimatorPlayUpdate()
        {
            var clip = new CelSkeletalClip2D("idle");
            clip.Loop = false; // 避免在 t=1.0 wrap 回 0
            var track = new CelBoneTrack2D(0);
            track.Add(0f, Vector2.Zero, 0, Vector2.One).Add(1f, new Vector2(10, 0), 0, Vector2.One);
            clip.AddTrack(track);

            var anim = new CelSkeletalAnimator2D();
            AssertTrue(!anim.IsPlaying, "初始未播放");

            anim.Play(clip);
            AssertTrue(anim.IsPlaying, "Play 后播放中");

            anim.Update(0.5f, 1);
            AssertEqual(0.5f, anim.Time, "0.5s 后 Time=0.5");

            var pose = anim.Update(0.5f, 1);
            AssertTrue(pose != null, "Update 返回姿势");
            AssertTrue(Near(pose!.Transforms[0].Position.X, 10f), "t=1s X=10");
        }

        // ── 16. Animator Loop 循环 wrap ──
        public static void TestAnimatorLoop()
        {
            var clip = new CelSkeletalClip2D("loop");
            clip.Loop = true;
            var track = new CelBoneTrack2D(0);
            track.Add(0f, Vector2.Zero, 0, Vector2.One).Add(1f, new Vector2(10, 0), 0, Vector2.One);
            clip.AddTrack(track);

            var anim = new CelSkeletalAnimator2D();
            anim.Play(clip);
            anim.Update(1.5f, 1); // 超过 Duration=1，wrap 到 0.5
            AssertEqual(0.5f, anim.Time, "Loop 1.5s wrap 到 0.5");
            AssertTrue(anim.IsPlaying, "Loop 播放不停止");
        }

        // ── 17. Animator 非循环播放完停止 ──
        public static void TestAnimatorNoLoopStop()
        {
            var clip = new CelSkeletalClip2D("oneshot");
            clip.Loop = false;
            var track = new CelBoneTrack2D(0);
            track.Add(0f, Vector2.Zero, 0, Vector2.One).Add(1f, new Vector2(10, 0), 0, Vector2.One);
            clip.AddTrack(track);

            var anim = new CelSkeletalAnimator2D();
            anim.Play(clip);
            anim.Update(2f, 1); // 超时
            AssertEqual(1f, anim.Time, "非循环钳到 Duration");
            AssertTrue(!anim.IsPlaying, "非循环播完停止");
        }

        // ── 18. Animator PlaybackSpeed 加速 ──
        public static void TestAnimatorPlaybackSpeed()
        {
            var clip = new CelSkeletalClip2D("fast");
            clip.PlaybackSpeed = 2f;
            var track = new CelBoneTrack2D(0);
            track.Add(0f, Vector2.Zero, 0, Vector2.One).Add(1f, new Vector2(10, 0), 0, Vector2.One);
            clip.AddTrack(track);

            var anim = new CelSkeletalAnimator2D();
            anim.Play(clip);
            anim.Update(0.25f, 1); // 实际推进 0.5s
            AssertEqual(0.5f, anim.Time, "PlaybackSpeed=2 下 0.25s 推进到 0.5s");
        }

        // ── 19. Animator Pause/Resume/Stop ──
        public static void TestAnimatorPauseResumeStop()
        {
            var clip = new CelSkeletalClip2D("idle");
            clip.Loop = false;  // 非循环：Duration=1.0 处不 wrap，便于断言 Time
            var track = new CelBoneTrack2D(0);
            track.Add(0f, Vector2.Zero, 0, Vector2.One).Add(1f, new Vector2(10, 0), 0, Vector2.One);
            clip.AddTrack(track);

            var anim = new CelSkeletalAnimator2D();
            anim.Play(clip);
            anim.Update(0.5f, 1);

            anim.Pause();
            AssertTrue(!anim.IsPlaying, "Pause 后未播放");
            anim.Update(1f, 1);
            AssertEqual(0.5f, anim.Time, "Pause 时 Time 不推进");

            anim.Resume();
            AssertTrue(anim.IsPlaying, "Resume 后播放");
            anim.Update(0.5f, 1);
            AssertEqual(1f, anim.Time, "Resume 后 Time 推进");

            anim.Stop();
            AssertTrue(!anim.IsPlaying, "Stop 后未播放");
            AssertEqual(0f, anim.Time, "Stop 重置 Time");
            AssertTrue(anim.Clip != null, "Stop 后 Clip 仍保留");
        }

        // ── 20. LerpAngle 最短弧 ──
        public static void TestLerpAngleShortestArc()
        {
            // 350° 到 10°，最短弧走 +20°（经过 0°）
            float result = CelMath2D.LerpAngle(
                350f * MathF.PI / 180f,
                10f * MathF.PI / 180f,
                0.5f);
            // 期望 = 0° (即 360°)
            AssertTrue(Near(result, 0f, 0.01f) || Near(result, MathF.Tau, 0.01f),
                $"LerpAngle(350°,10°,0.5)≈0°，实际 {result * 180f / MathF.PI}°");
        }
    }
}
