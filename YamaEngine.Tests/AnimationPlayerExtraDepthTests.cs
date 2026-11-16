// AnimationPlayerExtraDepthTests.cs —— 补充 AnimationPlayer 深度验证（Stop/Slerp/Scale/重启/多动画管理）

using System;
using System.Numerics;
using YamaEngine.GameFramework;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests
{
    public static class AnimationPlayerExtraDepthTests
    {
        private static void AssertEqual<T>(T got, T want, string msg)
        {
            if (!Equals(got, want)) throw new Exception($"{msg} (got {got}, want {want}).");
        }

        private static void AssertTrue(bool cond, string msg)
        {
            if (!cond) throw new Exception($"{msg}");
        }

        private static void AssertFalse(bool cond, string msg)
        {
            if (cond) throw new Exception($"{msg}");
        }

        private static void AssertThrows<TEx>(Action a, string msg) where TEx : Exception
        {
            try { a(); }
            catch (TEx) { return; }
            catch (Exception ex) { throw new Exception($"{msg} (wrong exception {ex.GetType().Name})."); }
            throw new Exception($"{msg} (no exception).");
        }

        private static void AssertNear(float got, float want, string msg, float tol = 1e-3f)
        {
            if (MathF.Abs(got - want) > tol) throw new Exception($"{msg} (got {got}, want {want}).");
        }

        // ── 默认状态：无动画、未播放、空串 ──
        public static void TestDefaultState()
        {
            var go = new GameObjectReal("AnimDefault");
            var p = go.AddComponent<AnimationPlayerReal>();
            AssertEqual(0, p.AnimationCount, "No animations initially.");
            AssertEqual("", p.CurrentAnimation, "Empty current animation.");
            AssertFalse(p.IsPlaying, "Not playing.");
            AssertFalse(p.IsPaused, "Not paused.");
            AssertNear(1f, p.SpeedScale, "Default SpeedScale=1.");
            AssertNear(0f, p.CurrentLength, "CurrentLength=0 when no anim.");
        }

        // ── AddAnimation / HasAnimation / GetAnimation ──
        public static void TestAddHasGet()
        {
            var go = new GameObjectReal("AnimAdd");
            var p = go.AddComponent<AnimationPlayerReal>();
            var a = new AnimationResource("walk", 1.0f);
            p.AddAnimation(a);
            AssertEqual(1, p.AnimationCount, "One animation registered.");
            AssertTrue(p.HasAnimation("walk"), "Has 'walk'.");
            AssertFalse(p.HasAnimation("idle"), "Does not have 'idle'.");
            var got = p.GetAnimation("walk");
            AssertEqual("walk", got.Name, "GetAnimation returns registered.");
            AssertNear(1.0f, got.Length, "Length preserved.");
        }

        // ── AddAnimation 重名覆盖 ──
        public static void TestAddOverwrites()
        {
            var go = new GameObjectReal("AnimOverwrite");
            var p = go.AddComponent<AnimationPlayerReal>();
            p.AddAnimation(new AnimationResource("x", 1.0f));
            p.AddAnimation(new AnimationResource("x", 3.0f));
            AssertEqual(1, p.AnimationCount, "Count stays 1 after overwrite.");
            AssertNear(3.0f, p.GetAnimation("x").Length, "Overwritten Length=3.");
        }

        // ── Play unknown throws ──
        public static void TestPlayUnknownThrows()
        {
            var go = new GameObjectReal("AnimUnknown");
            var p = go.AddComponent<AnimationPlayerReal>();
            AssertThrows<KeyNotFoundException>(
                () => p.Play("missing"), "Play unregistered throws.");
        }

        // ── Play 启动 + CurrentAnimation 设置 ──
        public static void TestPlayStarts()
        {
            var go = new GameObjectReal("AnimPlay");
            var p = go.AddComponent<AnimationPlayerReal>();
            p.AddAnimation(new AnimationResource("run", 2.0f));
            p.Play("run");
            AssertTrue(p.IsPlaying, "IsPlaying after Play.");
            AssertEqual("run", p.CurrentAnimation, "CurrentAnimation set.");
            AssertNear(2.0f, p.CurrentLength, "CurrentLength=2.");
            AssertNear(0f, p.CurrentPosition, "Position starts at 0.");
        }

        // ── Play 推进 position（非循环）──
        public static void TestPlayAdvances()
        {
            var go = new GameObjectReal("AnimAdv");
            var p = go.AddComponent<AnimationPlayerReal>();
            p.AddAnimation(new AnimationResource("x", 2.0f));
            p.Play("x");
            p.Update(0.5f);
            AssertNear(0.5f, p.CurrentPosition, "Advanced to 0.5.");
            p.Update(0.5f);
            AssertNear(1.0f, p.CurrentPosition, "Advanced to 1.0.");
        }

        // ── Stop 复位到 0 并清除 current ──
        public static void TestStopResets()
        {
            var go = new GameObjectReal("AnimStop");
            var p = go.AddComponent<AnimationPlayerReal>();
            p.AddAnimation(new AnimationResource("x", 2.0f));
            p.Play("x");
            p.Update(1.0f);
            AssertTrue(p.IsPlaying, "Playing before stop.");
            p.Stop();
            AssertFalse(p.IsPlaying, "Not playing after stop.");
            AssertEqual("", p.CurrentAnimation, "Current cleared after stop.");
            AssertNear(0f, p.CurrentPosition, "Position reset to 0.");
        }

        // ── Play 同名重启动画：position 复位到 0 ──
        public static void TestPlayRestarts()
        {
            var go = new GameObjectReal("AnimRestart");
            var p = go.AddComponent<AnimationPlayerReal>();
            p.AddAnimation(new AnimationResource("x", 2.0f, loop: true));
            p.Play("x");
            p.Update(1.5f); // 推进到 1.5
            AssertNear(1.5f, p.CurrentPosition, "Advanced to 1.5.");
            p.Play("x"); // 重启
            AssertNear(0f, p.CurrentPosition, "Play same anim restarts position.");
            AssertTrue(p.IsPlaying, "Still playing after restart.");
        }

        // ── Rotation 轨道 Slerp 驱动 Transform ──
        public static void TestRotationTrackSlerp()
        {
            var go = new GameObjectReal("AnimRot");
            var p = go.AddComponent<AnimationPlayerReal>();
            var anim = new AnimationResource("spin", 2.0f);
            var rot = anim.AddRotationTrack();
            rot.AddKey(0f, Quaternion.Identity);
            rot.AddKey(2f, Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f)); // 90°
            p.AddAnimation(anim);
            p.Play("spin");
            p.Seek(1.0f); // 中点：应 slerp 到 ~45° 绕 Y
            var q = go.Transform.LocalRotation;
            // 从 Y 轴旋转角 ~45°，验证角度 ≈ π/4
            float angle = 2f * MathF.Acos(MathF.Min(1f, MathF.Abs(q.W)));
            AssertNear(MathF.PI / 4f, angle, "Rotation slerp midpoint ~45°.");
        }

        // ── Scale 轨道驱动 Transform.LocalScale ──
        public static void TestScaleTrackDrivesTransform()
        {
            var go = new GameObjectReal("AnimScale");
            var p = go.AddComponent<AnimationPlayerReal>();
            var anim = new AnimationResource("grow", 2.0f);
            var scaleTrack = anim.AddVectorTrack(AnimationTrackType.Scale);
            scaleTrack.AddKey(0f, new Vector3(1f));
            scaleTrack.AddKey(2f, new Vector3(3f, 3f, 3f));
            p.AddAnimation(anim);
            p.Play("grow");
            p.Seek(1.0f); // 中点：Scale 应为 2
            AssertNear(2f, go.Transform.LocalScale.X, "Scale lerps to 2 at midpoint.");
            AssertNear(2f, go.Transform.LocalScale.Y, "Scale.Y lerps to 2.");
        }

        // ── Translation 轨道驱动 Transform.LocalPosition ──
        public static void TestTranslationTrackDrivesTransform()
        {
            var go = new GameObjectReal("AnimTrans");
            var p = go.AddComponent<AnimationPlayerReal>();
            var anim = new AnimationResource("move", 2.0f);
            var transTrack = anim.AddVectorTrack(AnimationTrackType.Translation);
            transTrack.AddKey(0f, new Vector3(0f, 0f, 0f));
            transTrack.AddKey(2f, new Vector3(0f, 10f, 0f));
            p.AddAnimation(anim);
            p.Play("move");
            p.Seek(1.0f); // 中点：Y=5
            AssertNear(5f, go.Transform.LocalPosition.Y, "Translation lerps Y=5 at midpoint.");
        }

        // ── 队列：Stop 后队列清空，播完不接播 ──
        public static void TestStopClearsQueue()
        {
            var go = new GameObjectReal("AnimStopQueue");
            var p = go.AddComponent<AnimationPlayerReal>();
            p.AddAnimation(new AnimationResource("a", 0.5f));
            p.AddAnimation(new AnimationResource("b", 0.5f));
            p.Play("a");
            p.QueueAnimation("b");
            p.Stop(); // 清队列
            p.Play("a");
            p.Update(0.6f); // a 播完
            // b 不应被接播（Stop 已清队列）
            AssertFalse(p.IsPlaying, "After finish no auto-play (queue cleared).");
        }

        // ── SpeedScale 复位默认 ──
        public static void TestSpeedScaleReset()
        {
            var go = new GameObjectReal("AnimSpeedReset");
            var p = go.AddComponent<AnimationPlayerReal>();
            p.AddAnimation(new AnimationResource("x", 1.0f));
            p.Play("x");
            p.SpeedScale = 3.0f;
            p.SpeedScale = 1.0f; // 重置
            p.Update(0.5f);
            AssertNear(0.5f, p.CurrentPosition, "SpeedScale reset to 1: normal advance.");
        }

        // ── 非循环播完 animation_finished 后 IsPlaying=false ──
        public static void TestFinishStopsPlaying()
        {
            var go = new GameObjectReal("AnimFinish");
            var p = go.AddComponent<AnimationPlayerReal>();
            p.AddAnimation(new AnimationResource("die", 1.0f));
            int finished = 0;
            go.GetSignal("animation_finished").Connect(_ => finished++);
            p.Play("die");
            p.Update(1.5f);
            AssertFalse(p.IsPlaying, "IsPlaying=false after non-loop finish.");
            AssertEqual(1, finished, "animation_finished fired once.");
            AssertNear(1.0f, p.CurrentPosition, "Position at end.");
        }

        // ── 循环动画 IsPlaying 持续 true（永不停止）──
        public static void TestLoopStaysPlaying()
        {
            var go = new GameObjectReal("AnimLoopPlay");
            var p = go.AddComponent<AnimationPlayerReal>();
            p.AddAnimation(new AnimationResource("idle", 1.0f, loop: true));
            p.Play("idle");
            for (int i = 0; i < 50; i++) p.Update(0.1f);
            AssertTrue(p.IsPlaying, "Loop stays playing.");
            // 时间应回绕：50 * 0.1 = 5s，%1 = 0
            AssertNear(0f, p.CurrentPosition, "Loop position wrapped.", 1e-2f);
        }
    }
}
