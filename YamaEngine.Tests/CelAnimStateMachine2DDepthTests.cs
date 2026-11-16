// CelAnimStateMachine2DDepthTests.cs — Unity5 2D 动画状态机深度验证
// 覆盖:
//   * CelAnimState2D: 默认值 + 命名构造
//   * CelAnimTransition2D: 字段默认值
//   * CelAnimationStateMachine2D: AddState/AddTransition/Enter/Update/Fire/IsIn/IsFinished/BindSprite

using System;
using System.Numerics;
using YamaEngine.Cel2D;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests
{
    public static class CelAnimStateMachine2DDepthTests
    {
        private const float Eps = 1e-4f;

        private static CelAnimationClip MakeClip(string name, int frames = 2, float frameDur = 0.1f)
        {
            var clip = new CelAnimationClip(name);
            for (int i = 0; i < frames; i++)
                clip.AddFrame(new Vector4(i, 0, i + 1, 1), frameDur);
            return clip;
        }

        // ── 1. CelAnimState2D 默认值 ──
        public static void TestStateDefaults()
        {
            var s = new CelAnimState2D();
            AssertEqual("State", s.Name, "默认名=State");
            AssertTrue(s.Clip != null, "默认 Clip 非空");
            AssertTrue(s.Loop, "默认 Loop=true");
            AssertTrue(MathF.Abs(s.Speed - 1.0f) < Eps, "默认 Speed=1");

            var named = new CelAnimState2D("Attack", MakeClip("atk"), loop: false);
            AssertEqual("Attack", named.Name, "命名构造 Name");
            AssertTrue(!named.Loop, "命名构造 Loop=false");
        }

        // ── 2. CelAnimTransition2D 字段默认值 ──
        public static void TestTransitionDefaults()
        {
            var t = new CelAnimTransition2D();
            AssertEqual(string.Empty, t.FromState, "默认 FromState");
            AssertEqual(string.Empty, t.ToState, "默认 ToState");
            AssertTrue(t.Condition == null, "默认 Condition=null");
            AssertTrue(t.RestartOnEnter, "默认 RestartOnEnter=true");
            AssertTrue(t.TriggerKey == null, "默认 TriggerKey=null");
        }

        // ── 3. Enter 加载状态 + 设置 CurrentState ──
        public static void TestEnterSetsCurrentState()
        {
            var sm = new CelAnimationStateMachine2D();
            sm.AddState(new CelAnimState2D("Idle", MakeClip("idle")));
            sm.AddState(new CelAnimState2D("Walk", MakeClip("walk")));

            AssertTrue(!sm.IsInitialized, "未 Enter 前 IsInitialized=false");
            AssertEqual(string.Empty, sm.CurrentState, "初始 CurrentState 空");

            bool ok = sm.Enter("Idle");
            AssertTrue(ok, "Enter 成功返回 true");
            AssertEqual("Idle", sm.CurrentState, "Enter 后 CurrentState=Idle");
            AssertEqual(0, sm.CurrentStateIndex, "Idle 索引=0");
            AssertTrue(sm.IsInitialized, "Enter 后 IsInitialized=true");
            AssertTrue(sm.IsIn("Idle"), "IsIn(Idle)=true");
            AssertTrue(!sm.IsIn("Walk"), "IsIn(Walk)=false");
        }

        // ── 4. Enter 未知状态返回 false ──
        public static void TestEnterUnknownStateReturnsFalse()
        {
            var sm = new CelAnimationStateMachine2D();
            bool ok = sm.Enter("Missing");
            AssertTrue(!ok, "未知状态 Enter 返回 false");
            AssertTrue(!sm.IsInitialized, "未知状态 Enter 不初始化");
        }

        // ── 5. Update 推进当前状态动画 ──
        public static void TestUpdateAdvancesClip()
        {
            var sm = new CelAnimationStateMachine2D();
            sm.AddState(new CelAnimState2D("Idle", MakeClip("idle", frames: 3, frameDur: 0.1f)));
            sm.Enter("Idle");

            // 未初始化时 Update 不推进
            var sm2 = new CelAnimationStateMachine2D();
            sm2.AddState(new CelAnimState2D("Idle", MakeClip("idle")));
            sm2.Update(0.5f);
            AssertEqual(0, sm2.Player.FrameIndex, "未初始化 Update 不推进");

            sm.Update(0.05f); // t=0.05 帧 0
            AssertEqual(0, sm.Player.FrameIndex, "t=0.05 帧 0");

            sm.Update(0.1f);  // 累计 0.15 帧 1
            AssertEqual(1, sm.Player.FrameIndex, "累计 0.15 帧 1");
        }

        // ── 6. Condition 触发转换 ──
        public static void TestConditionTransition()
        {
            var sm = new CelAnimationStateMachine2D();
            sm.AddState(new CelAnimState2D("Idle", MakeClip("idle")));
            sm.AddState(new CelAnimState2D("Walk", MakeClip("walk")));

            bool shouldWalk = false;
            sm.AddTransition(new CelAnimTransition2D
            {
                FromState = "Idle",
                ToState = "Walk",
                Condition = _ => shouldWalk,
            });
            sm.Enter("Idle");
            sm.Update(0.016f);
            AssertEqual("Idle", sm.CurrentState, "条件 false 不切换");

            shouldWalk = true;
            sm.Update(0.016f);
            AssertEqual("Walk", sm.CurrentState, "条件 true 切换到 Walk");
        }

        // ── 7. Fire TriggerKey 转换 ──
        public static void TestFireTrigger()
        {
            var sm = new CelAnimationStateMachine2D();
            sm.AddState(new CelAnimState2D("Idle", MakeClip("idle")));
            sm.AddState(new CelAnimState2D("Attack", MakeClip("atk")));

            sm.AddTransition(new CelAnimTransition2D
            {
                FromState = "Idle",
                ToState = "Attack",
                TriggerKey = "OnAttack",
            });

            sm.Enter("Idle");
            // 条件评估不会触发带 TriggerKey 的转换
            sm.Update(0.016f);
            AssertEqual("Idle", sm.CurrentState, "TriggerKey 不被 Update 触发");

            bool ok = sm.Fire("OnAttack");
            AssertTrue(ok, "Fire 命中返回 true");
            AssertEqual("Attack", sm.CurrentState, "Fire 后进入 Attack");

            bool miss = sm.Fire("Nonexistent");
            AssertTrue(!miss, "Fire 未命中返回 false");
        }

        // ── 8. TriggerKey 转换只在 FromState 匹配时生效 ──
        public static void TestFireRequiresFromState()
        {
            var sm = new CelAnimationStateMachine2D();
            sm.AddState(new CelAnimState2D("Idle", MakeClip("idle")));
            sm.AddState(new CelAnimState2D("Walk", MakeClip("walk")));
            sm.AddState(new CelAnimState2D("Attack", MakeClip("atk")));

            sm.AddTransition(new CelAnimTransition2D
            {
                FromState = "Idle",
                ToState = "Attack",
                TriggerKey = "OnAttack",
            });
            sm.Enter("Walk");

            bool ok = sm.Fire("OnAttack");
            AssertTrue(!ok, "非 Idle 状态下 Fire 不触发");
            AssertEqual("Walk", sm.CurrentState, "状态不变");
        }

        // ── 9. Enter 设置 PlaybackSpeed = state.Speed ──
        public static void TestEnterAppliesSpeed()
        {
            var clip = MakeClip("run");
            var sm = new CelAnimationStateMachine2D();
            sm.AddState(new CelAnimState2D("Run", clip) { Speed = 2.0f });
            sm.Enter("Run");

            AssertTrue(MathF.Abs(clip.PlaybackSpeed - 2.0f) < Eps,
                $"Enter 应用 state.Speed 到 clip.PlaybackSpeed，实际 {clip.PlaybackSpeed}");
        }

        // ── 10. BindSprite 同步精灵 Player + SourceRect ──
        public static void TestBindSpriteSyncsSourceRect()
        {
            var sm = new CelAnimationStateMachine2D();
            var clip = MakeClip("idle", frames: 2, frameDur: 0.1f);
            sm.AddState(new CelAnimState2D("Idle", clip));
            sm.Enter("Idle");

            var sprite = new CelSpriteData();
            sm.BindSprite(sprite);
            AssertTrue(sprite.Player == sm.Player, "BindSprite 把 player 注入 sprite");

            // 推进到帧 1
            sm.Update(0.15f);
            AssertEqual(1, sm.Player.FrameIndex, "推进到帧 1");
            Vector4 expected = clip.Frames[1].SourceRect;
            AssertTrue(MathF.Abs(sprite.SourceRect.X - expected.X) < Eps,
                $"精灵 SourceRect 同步帧 1，实际 X={sprite.SourceRect.X}");
        }

        // ── 11. IsFinished 非循环片段播完 ──
        public static void TestIsFinishedAfterNoLoopClip()
        {
            var sm = new CelAnimationStateMachine2D();
            var clip = MakeClip("hit", frames: 2, frameDur: 0.1f);
            clip.Loop = false;
            sm.AddState(new CelAnimState2D("Hit", clip) { Loop = false });
            sm.Enter("Hit");

            AssertTrue(!sm.IsFinished, "刚进入时未完成");
            sm.Update(0.5f); // 推进超过总时长
            AssertEqual(CelPlaybackState.Stopped, sm.Player.State, "非循环播完停止");
            AssertTrue(sm.IsFinished, "播完后 IsFinished=true");
        }

        // ── 12. 多次 Enter 同状态重置 ──
        public static void TestReEnterResetsFrame()
        {
            var sm = new CelAnimationStateMachine2D();
            sm.AddState(new CelAnimState2D("Idle", MakeClip("idle", frames: 3, frameDur: 0.1f)));
            sm.Enter("Idle");
            sm.Update(0.25f); // 推进到帧 2
            AssertEqual(2, sm.Player.FrameIndex, "推进到帧 2");

            sm.Enter("Idle"); // 重新进入
            AssertEqual(0, sm.Player.FrameIndex, "重新 Enter 重置帧");
        }

        // ── 13. CurrentStateIndex 跟踪 ──
        public static void TestCurrentStateIndexTracksOrder()
        {
            var sm = new CelAnimationStateMachine2D();
            sm.AddState(new CelAnimState2D("A", MakeClip("a")));
            sm.AddState(new CelAnimState2D("B", MakeClip("b")));
            sm.AddState(new CelAnimState2D("C", MakeClip("c")));

            AssertEqual(-1, sm.CurrentStateIndex, "未 Enter 索引=-1");
            sm.Enter("A"); AssertEqual(0, sm.CurrentStateIndex, "A 索引 0");
            sm.Enter("B"); AssertEqual(1, sm.CurrentStateIndex, "B 索引 1");
            sm.Enter("C"); AssertEqual(2, sm.CurrentStateIndex, "C 索引 2");
        }

        // ── 14. 转换后 RestartOnEnter=true 立即从新状态帧 0 起 ──
        public static void TestTransitionRestartsClip()
        {
            var sm = new CelAnimationStateMachine2D();
            sm.AddState(new CelAnimState2D("Idle", MakeClip("idle", frames: 3, frameDur: 0.1f)));
            sm.AddState(new CelAnimState2D("Walk", MakeClip("walk", frames: 3, frameDur: 0.1f)));
            sm.AddTransition(new CelAnimTransition2D
            {
                FromState = "Idle",
                ToState = "Walk",
                Condition = _ => true,
                RestartOnEnter = true,
            });
            sm.Enter("Idle");
            sm.Update(0.25f); // Idle 推进到帧 2 + 条件 true → 直接转换到 Walk
            AssertEqual("Walk", sm.CurrentState, "Update 评估转换后进入 Walk");
            AssertEqual(0, sm.Player.FrameIndex, "新状态从帧 0 开始");
        }
    }
}
