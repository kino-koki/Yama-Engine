// TweenTests.cs —— 追平 Godot 3 的 Tween 属性补间（create_tween + tween_property）
// 覆盖：四种过渡曲线、顺序/并行、循环、SpeedScale、驱动 Node2D 位置/旋转/缩放/透明度。

using System;
using System.Numerics;
using YamaEngine.UI;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests
{
    public static class TweenTests
    {
        private const float Tol = 1e-3f;

        // ── 线性过渡：进度线性推进位置 ──
        public static void TestTweenLinearMovesPosition()
        {
            var node = new Node2D("N") { Position = new Vector2(0, 0) };
            var tween = new Tween();
            tween.TweenPropertyStep(node, TweenProperty.PositionX, 100f, 1f).SetTrans(TweenTransition.Linear, TweenEase.InOut);
            tween.Start();

            tween.Update(0.25f);
            AssertNear(25f, node.Position.X, "Linear 25% X=25.", Tol);
            AssertTrue(tween.IsRunning, "Still running at quarter.");

            tween.Update(0.25f);
            AssertNear(50f, node.Position.X, "Linear halfway X=50.", Tol);
            AssertNear(0f, node.Position.Y, "Y unchanged.", Tol);
            AssertTrue(tween.IsRunning, "Still running at halfway.");

            tween.Update(0.5f); // 完成本帧
            AssertNear(100f, node.Position.X, "Linear end X=100.", Tol);
            AssertEqual(TweenState.Done, tween.State, "Tween done after full duration.");
        }
        // ── Quad In 起步慢：前 1/4 进度远小于线性 ──
        public static void TestTweenQuadInSlowStart()
        {
            float lin = Tween.EaseValue(0.25f, TweenTransition.Linear, TweenEase.InOut);
            float quadIn = Tween.EaseValue(0.25f, TweenTransition.Quad, TweenEase.In);
            AssertTrue(quadIn < lin, $"QuadIn at 25% ({quadIn}) < Linear ({lin}).");
            AssertNear(0.0625f, quadIn, "QuadIn(0.25)=0.25^2.", 1e-4f);
        }

        // ── Sine Out 起步快：前 1/4 进度大于线性 ──
        public static void TestTweenSineOutFastStart()
        {
            float lin = Tween.EaseValue(0.25f, TweenTransition.Linear, TweenEase.InOut);
            float sineOut = Tween.EaseValue(0.25f, TweenTransition.Sine, TweenEase.Out);
            AssertTrue(sineOut > lin, $"SineOut at 25% ({sineOut}) > Linear ({lin}).");
        }

        // ── Cubic InOut 中点恰为 0.5（对称） ──
        public static void TestTweenCubicInOutMidpointHalf()
        {
            float mid = Tween.EaseValue(0.5f, TweenTransition.Cubic, TweenEase.InOut);
            AssertNear(0.5f, mid, "CubicInOut(0.5)=0.5.", 1e-4f);
        }

        // ── 顺序步骤：第一步完成后第二步才开始（不重叠） ──
        public static void TestTweenSequentialSteps()
        {
            var node = new Node2D("N") { Position = new Vector2(0, 0) };
            var tween = new Tween();
            tween.TweenPropertyStep(node, TweenProperty.PositionX, 100f, 1f);
            tween.TweenPropertyStep(node, TweenProperty.PositionY, 50f, 1f);
            tween.Start();

            tween.Update(0.5f); // 第一步半程
            AssertNear(50f, node.Position.X, "Step1 X halfway.", Tol);
            AssertNear(0f, node.Position.Y, "Step2 Y not started while step1 runs.", Tol);

            tween.Update(0.5f); // 第一步完成；同帧第二步启动并推进剩余 0.5s
            AssertNear(100f, node.Position.X, "Step1 X done.", Tol);
            AssertNear(25f, node.Position.Y, "Step2 starts same frame step1 ends, halfway Y=25.", Tol);
        }

        // ── 并行步骤：两属性同时推进 ──
        public static void TestTweenParallelSteps()
        {
            var node = new Node2D("N") { Position = new Vector2(0, 0), Scale = new Vector2(1, 1) };
            var tween = new Tween();
            tween.TweenPropertyStep(node, TweenProperty.PositionX, 100f, 1f);
            tween.TweenPropertyStep(node, TweenProperty.ScaleX, 3f, 1f).Parallel();
            tween.Start();

            tween.Update(0.5f); // 两步都半程
            AssertNear(50f, node.Position.X, "Parallel X halfway.", Tol);
            AssertNear(2f, node.Scale.X, "Parallel ScaleX halfway (1→3 midpoint=2).", Tol);
        }

        // ── 循环：set_loops(2) 播两轮 ──
        public static void TestTweenLoopsTwice()
        {
            var node = new Node2D("N") { Position = new Vector2(0, 0) };
            var tween = new Tween();
            tween.TweenPropertyStep(node, TweenProperty.PositionX, 100f, 1f);
            tween.SetLoops(2);
            tween.Start();

            tween.Update(1f); // 第一轮完成
            AssertEqual(1, tween.FinishedCount, "One round finished.");
            AssertTrue(tween.IsRunning, "Still running for second loop.");
            AssertEqual(1, tween.LoopsLeft, "One loop left.");

            tween.Update(1f); // 第二轮完成
            AssertEqual(2, tween.FinishedCount, "Two rounds finished.");
            AssertEqual(TweenState.Done, tween.State, "Done after loops exhausted.");
            AssertNear(100f, node.Position.X, "Position at target after final loop.", Tol);
        }

        // ── SpeedScale=2 双速推进 ──
        public static void TestTweenSpeedScaleDoubles()
        {
            var node = new Node2D("N") { Position = new Vector2(0, 0) };
            var tween = new Tween();
            tween.TweenPropertyStep(node, TweenProperty.PositionX, 100f, 1f);
            tween.SpeedScale = 2f;
            tween.Start();

            tween.Update(0.5f); // 0.5s × 2 = 1s 有效进度 → 完成
            AssertNear(100f, node.Position.X, "SpeedScale=2 finishes in half real time.", Tol);
            AssertEqual(TweenState.Done, tween.State, "Done at 0.5s real with 2x speed.");
        }

        // ── 驱动旋转（度→弧度） ──
        public static void TestTweenDrivesRotation()
        {
            var node = new Node2D("N") { Rotation = 0f };
            var tween = new Tween();
            tween.TweenPropertyStep(node, TweenProperty.RotationDegrees, 180f, 1f);
            tween.Start();
            tween.Update(1f);
            AssertNear(MathF.PI, node.Rotation, "180° = π radians.", 1e-3f);
        }

        // ── 驱动透明度 Modulate.W ──
        public static void TestTweenDrivesOpacity()
        {
            var node = new Node2D("N") { Modulate = new Vector4(1, 1, 1, 0f) };
            var tween = new Tween();
            tween.TweenPropertyStep(node, TweenProperty.Opacity, 1f, 1f);
            tween.Start();
            tween.Update(0.5f);
            AssertNear(0.5f, node.Modulate.W, "Opacity halfway 0.5.", Tol);
        }

        // ── TweenFactory 统一 Update 并清理已完成 Tween ──
        public static void TestTweenFactoryUpdatesAndCleans()
        {
            var factory = new TweenFactory();
            var node = new Node2D("N") { Position = new Vector2(0, 0) };
            var t1 = factory.CreateTween();
            t1.TweenPropertyStep(node, TweenProperty.PositionX, 10f, 1f).SetTrans(TweenTransition.Linear, TweenEase.InOut);
            t1.Start();
            var t2 = factory.CreateTween();
            t2.TweenPropertyStep(node, TweenProperty.PositionY, 20f, 1f).SetTrans(TweenTransition.Linear, TweenEase.InOut);
            t2.Start();

            AssertEqual(2, factory.ActiveCount, "Two active tweens.");
            factory.Update(1f); // 两者都完成
            AssertEqual(0, factory.ActiveCount, "Completed tweens removed from factory.");
        }

        private static void AssertEqual<T>(T got, T want, string msg)
        {
            if (!Equals(got, want)) throw new Exception($"{msg} (got {got}, want {want}).");
        }

        private static void AssertNear(float got, float want, string msg, float tol)
        {
            if (MathF.Abs(got - want) > tol) throw new Exception($"{msg} (got {got}, want {want}±{tol}).");
        }

        private static void AssertTrue(bool cond, string msg)
        {
            if (!cond) throw new Exception(msg);
        }
    }
}
