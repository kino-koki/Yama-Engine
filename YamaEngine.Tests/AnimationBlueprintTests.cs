// AnimationBlueprintTests.cs — 动画蓝图运行时填充验证套件
// 覆盖:
//   * 资产 → 工厂 → 活的 AnimatorController → 挂到 Animator.Controller (Idle→Run 转移)
//   * 资产 1D 混合树状态 → 权重混合输出
//   * BlendTreeReal (BlendNode 图) → IMotion 桥 → 状态机 Motion (闭合断连缺口)
//   * EcsAnimationBridge 从蓝图资产填充 Animator.Controller (资源管线运行时填充)
// 全部 CPU 级确定性, 不依赖 GPU/场景。使用 YamaEngine.Animation.Skeleton (动画运行时)。
using System;
using System.Collections.Generic;
using System.Numerics;
using YamaEngine.Animation;
using YamaEngine.ECS;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests;

/// <summary>动画蓝图 (数据驱动 AnimatorController) 运行时填充验证套件。</summary>
public sealed class AnimationBlueprintTests
{
    private static AnimationClip MakeClip(string name, float hipsZ)
    {
        var clip = new AnimationClip { Name = name, LengthSeconds = 1.0f, Looping = true };
        clip.AddTranslationKey("Hips", 0f, Vector3.Zero);
        clip.AddTranslationKey("Hips", 1f, new Vector3(0, 0, hipsZ));
        return clip;
    }

    private static AnimationBlueprintAsset MakeLocomotionBlueprint(out Guid idleId, out Guid runId)
    {
        var asset = new AnimationBlueprintAsset { Name = "LocomotionBP" };
        asset.AddFloat("Speed", 0f);

        idleId = Guid.NewGuid();
        runId = Guid.NewGuid();
        asset.AddClipState("Idle", idleId);
        asset.AddClipState("Run", runId);
        asset.AddTransition("Idle", "Run", AnimatorConditionMode.Greater, "Speed", 0.5f, duration: 0.1f);
        return asset;
    }

    /// <summary>资产 → 工厂 → Animator.Controller 挂载 → Idle→Run 转移 (钩子已填充)。</summary>
    public static void TestAnimationBlueprintBuildsControllerAndFillsHook()
    {
        var skeleton = YamaEngine.Animation.Skeleton.CreateBiped("AnimBiped");
        var asset = MakeLocomotionBlueprint(out var idleId, out var runId);

        var clips = new Dictionary<Guid, AnimationClip>
        {
            [idleId] = MakeClip("Idle", 0f),
            [runId] = MakeClip("Run", 2f)
        };

        var animator = new Animator(skeleton);
        var controller = AnimationBlueprintRuntime.BuildAndApply(asset, animator, id => clips.GetValueOrDefault(id)!);

        AssertTrue(animator.Controller == controller, "Animator.Controller 应被蓝图工厂填充。");

        // 首次 Update 初始化状态机到默认状态 (Idle)
        controller.Update(1f / 60f);
        AssertEqual("Idle", controller.CurrentStateName, "初始状态应为 Idle。");

        // 触发条件并推进
        controller.SetFloat("Speed", 1f);
        for (int i = 0; i < 30; i++) animator.Update(1f / 60f);

        AssertEqual("Run", controller.CurrentStateName, "条件满足后应切换到 Run。");
        AssertEqual("Run", animator.CurrentStateName, "Animator 应镜像控制器当前状态。");

        var hips = skeleton.FindBone("Hips");
        AssertTrue(controller.CurrentPose.GetTranslationOffset(hips).Z > 0.5f,
            "Run 姿态应在 Hips 上产生位移 (蓝图驱动)。");
    }

    /// <summary>资产 1D 混合树状态应按 Speed 权重混合输出。</summary>
    public static void TestAnimationBlueprintBlendTreeStateInterpolates()
    {
        var skeleton = YamaEngine.Animation.Skeleton.CreateBiped("AnimBiped");
        var asset = new AnimationBlueprintAsset { Name = "BlendBP" };
        asset.AddFloat("Speed", 0f);

        var idleId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var state = asset.AddBlendTreeState1D("Locomotion", "Speed");
        state.BlendTree.AddChild(idleId.ToString(), 0f).ClipId = idleId;
        state.BlendTree.AddChild(runId.ToString(), 1f).ClipId = runId;

        var clips = new Dictionary<Guid, AnimationClip>
        {
            [idleId] = MakeClip("Idle", 0f),
            [runId] = MakeClip("Run", 2f)
        };

        var controller = AnimationBlueprintRuntime.Build(asset, skeleton, id => clips.GetValueOrDefault(id)!);
        controller.SetFloat("Speed", 0.5f);
        for (int i = 0; i < 30; i++) controller.Update(1f / 60f); // ≈0.5s

        var hips = skeleton.FindBone("Hips");
        float z = controller.CurrentPose.GetTranslationOffset(hips).Z;
        AssertTrue(z > 0.35f && z < 0.65f,
            $"Speed=0.5 时 1D 混合应在中点附近(≈0.5), 实际 Z={z}。");
    }

    /// <summary>BlendTreeReal (BlendNode 图) 应可作为状态机 Motion (断连缺口闭合)。</summary>
    public static void TestBlendTreeRealMotionBridgesGraph()
    {
        var skeleton = YamaEngine.Animation.Skeleton.CreateBiped("AnimBiped");
        var idleClip = MakeClip("Idle", 0f);
        var runClip = MakeClip("Run", 2f);

        // 转换为 BlendTreeReal 使用的剪辑
        var idleReal = AnimationClipRealConverter.Convert(idleClip, skeleton);
        var runReal = AnimationClipRealConverter.Convert(runClip, skeleton);

        // 构建 1D 混合图: Idle@0, Run@1
        var space = new BlendSpace1D { ParameterName = "Speed" };
        space.AddEntry(new ClipNode(idleReal), 0f);
        space.AddEntry(new ClipNode(runReal), 1f);

        // 桥接为 IMotion
        var motion = new BlendTreeRealMotion(space, length: 1f);
        motion.SetParameter("Speed", 0.5f);

        // 作为状态机 Motion
        var sm = new AnimationStateMachine { Name = "RealGraph" };
        sm.AddState("Locomotion", motion);

        var controller = new AnimatorController(skeleton);
        controller.AddLayer("Base", sm);
        for (int i = 0; i < 30; i++) controller.Update(1f / 60f);

        var hips = skeleton.FindBone("Hips");
        float z = controller.CurrentPose.GetTranslationOffset(hips).Z;
        AssertTrue(z > 0.3f && z < 0.7f,
            $"BlendTreeReal 图经桥接混合应在中点附近, 实际 Z={z}。");
    }

    /// <summary>EcsAnimationBridge 应从蓝图资产填充 Animator.Controller (资源管线填充)。</summary>
    public static void TestEcsBridgeFillsControllerFromBlueprint()
    {
        var skeleton = YamaEngine.Animation.Skeleton.CreateBiped("AnimBiped");
        var asset = MakeLocomotionBlueprint(out var idleId, out var runId);
        var clips = new Dictionary<Guid, AnimationClip>
        {
            [idleId] = MakeClip("Idle", 0f),
            [runId] = MakeClip("Run", 2f)
        };

        var ecs = new EntityComponentSystem();
        var bridge = new EcsAnimationBridge(ecs);

        // 注册运行时资产 (资源管线填充)
        var skeletonId = Guid.NewGuid();
        bridge.RegisterSkeleton(skeletonId, skeleton);
        bridge.RegisterClip(idleId, clips[idleId]);
        bridge.RegisterBlueprint(asset);

        int entity = ecs.CreateEntity("AnimatedEntity");
        ecs.AddComponent(entity, new YamaEngine.Animation.AnimatorComponent
        {
            SkeletonId = skeletonId,
            AnimationClipId = idleId,
            AnimationBlueprintId = asset.Id,
            CurrentState = "Idle",
            IsPlaying = true
        });

        bridge.Update(1f / 60f);

        var anim = ecs.GetComponent<YamaEngine.Animation.AnimatorComponent>(entity);
        AssertTrue(anim.RuntimeController != null,
            "EcsAnimationBridge 应从蓝图资产构建控制器并填充。");

        var liveAnimator = bridge.GetAnimator(entity);
        AssertTrue(liveAnimator != null, "桥应持有实体的运行时 Animator。");
        AssertTrue(liveAnimator!.Controller == anim.RuntimeController,
            "Animator.Controller 应被桥填充为蓝图构建的控制器。");
        AssertEqual("Idle", anim.RuntimeController!.CurrentStateName,
            "控制器初始状态应为 Idle。");

        bridge.ClearRuntimeAssets();
    }
}
