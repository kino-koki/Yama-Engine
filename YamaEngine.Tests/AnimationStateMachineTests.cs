// AnimationStateMachineTests.cs — AnimatorController(多层状态机) / BlendTree 闭环验证
// 覆盖：
//   * AnimatorController 参数条件转移 (Idle→Run via Speed>0.5)
//   * 动画事件跨时间点触发
//   * BlendTree(1D) 作为状态 Motion 的权重混合输出
//   * Animator.Controller 委托路径 (Animator → AnimatorController)
// 全部 CPU 级确定性，不依赖 GPU/场景。
// 注意: 本测试使用 YamaEngine.Animation.Skeleton (动画运行时), 与 Skinning.Skeleton 不同。
using System;
using System.Numerics;
using System.Linq;
using YamaEngine.Animation;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests;

/// <summary>AnimatorController / 状态机 / BlendTree 验证套件。</summary>
public sealed class AnimationStateMachineTests
{
    private static AnimationClip MakeClip(string name, float hipsZ)
    {
        var clip = new AnimationClip { Name = name, LengthSeconds = 1.0f, Looping = true };
        clip.AddTranslationKey("Hips", 0f, Vector3.Zero);
        clip.AddTranslationKey("Hips", 1f, new Vector3(0, 0, hipsZ));
        return clip;
    }

    private static AnimatorController CreateController(
        YamaEngine.Animation.Skeleton skeleton,
        AnimationStateMachine sm,
        string floatParam,
        float floatValue)
    {
        var controller = new AnimatorController(skeleton);
        controller.AddLayer("Base", sm);
        controller.AddFloat(floatParam, floatValue);
        return controller;
    }

    /// <summary>参数条件应驱动 Idle→Run 转移。</summary>
    public static void TestAnimatorControllerTransitionsOnParameter()
    {
        var skeleton = YamaEngine.Animation.Skeleton.CreateBiped("AnimBiped");
        var idle = MakeClip("Idle", 0f);
        var run = MakeClip("Run", 2f);

        var sm = new AnimationStateMachine { Name = "Locomotion" };
        sm.AddState("Idle", idle);
        sm.AddState("Run", run);

        var controller = CreateController(skeleton, sm, "Speed", 0f);

        // Idle→Run 当 Speed > 0.5 (无退出时间, 条件立即生效)
        var trans = sm.AddTransition("Idle", "Run", hasExitTime: false, exitTime: 0f, duration: 0.1f);
        trans.Conditions.Add(new AnimatorCondition("Speed", AnimatorConditionMode.Greater, 0.5f));

        // 初始为 Idle
        controller.Update(1f / 60f);
        AssertEqual("Idle", controller.CurrentStateName, "初始状态应为 Idle。");

        // 触发条件并推进越过 0.1s 过渡
        controller.SetFloat("Speed", 1f);
        for (int i = 0; i < 30; i++) controller.Update(1f / 60f);

        AssertEqual("Run", controller.CurrentStateName, "条件满足后应切换到 Run。");
        var hips = skeleton.FindBone("Hips");
        AssertTrue(controller.CurrentPose.GetTranslationOffset(hips).Z > 0.5f,
            "Run 姿态应在 Hips 上产生位移。");
    }

    /// <summary>跨越剪辑事件时间点应触发事件。</summary>
    public static void TestAnimatorControllerFiresEvents()
    {
        var skeleton = YamaEngine.Animation.Skeleton.CreateBiped("AnimBiped");
        var clip = MakeClip("Walk", 1f);
        clip.AddEvent("Footstep", 0.5f);

        var sm = new AnimationStateMachine();
        sm.AddState("Walk", clip);
        var controller = CreateController(skeleton, sm, "Speed", 0f);
        controller.Update(0f); // 初始化播放

        bool fired = false;
        for (int i = 0; i < 40; i++) // 约 0.66s, 跨过 0.5s 事件点
        {
            controller.Update(1f / 60f);
            if (controller.FiredEventsThisFrame.Any(e => e.Name == "Footstep"))
            {
                fired = true;
                break;
            }
        }

        AssertTrue(fired, "跨越事件时间点应触发 Footstep 事件。");
    }

    /// <summary>BlendTree(1D) 作为状态 Motion 应按参数权重混合输出。</summary>
    public static void TestBlendTreeStateMotionInterpolates()
    {
        var skeleton = YamaEngine.Animation.Skeleton.CreateBiped("AnimBiped");
        var idle = MakeClip("Idle", 0f);
        var run = MakeClip("Run", 2f);

        var blend = new BlendTree("Locomotion", BlendTreeType.Simple1D);
        blend.BlendParameter = "Speed";
        blend.AddChild(idle, 0f);
        blend.AddChild(run, 1f);

        var sm = new AnimationStateMachine();
        sm.AddState("Locomotion", blend);

        var controller = CreateController(skeleton, sm, "Speed", 0.5f);

        // 推进到 t≈0.5s: idle 剪辑 Hips Z=0, run 剪辑 Hips Z=1.0(0.5s*2),
        // 1D 混合权重 0.5/0.5 => 期望 Z≈0.5。
        for (int i = 0; i < 30; i++) controller.Update(1f / 60f);

        var hips = skeleton.FindBone("Hips");
        float z = controller.CurrentPose.GetTranslationOffset(hips).Z;
        AssertTrue(z > 0.35f && z < 0.65f,
            $"Speed=0.5 时 1D 混合应在中点附近(≈0.5), 实际 Z={z}。");
    }

    /// <summary>Animator.Controller 应委托到 AnimatorController 管线。</summary>
    public static void TestAnimatorDelegatesToController()
    {
        var skeleton = YamaEngine.Animation.Skeleton.CreateBiped("AnimBiped");
        var idle = MakeClip("Idle", 0f);

        var sm = new AnimationStateMachine();
        sm.AddState("Idle", idle);
        var controller = new AnimatorController(skeleton);
        controller.AddLayer("Base", sm);

        var animator = new Animator(skeleton);
        animator.Controller = controller;
        animator.Update(1f / 60f);

        AssertEqual("Idle", animator.CurrentStateName, "Animator 应委托到控制器并暴露当前状态。");
        AssertTrue(animator.CurrentPose != null, "Animator 应获取控制器输出姿态。");
    }
}
