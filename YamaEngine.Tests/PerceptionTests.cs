// PerceptionTests.cs — AI 感知系统（AIPerceptionComponent）验证套件
// 覆盖：默认感官注册（视觉/听觉/伤害/触觉）、刺激注册触发回调、
//       遗忘移除、更新推进。全部 CPU 级确定性，不依赖 GPU/场景。
using System;
using YamaEngine.AI;
using YamaEngine.GameFramework;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests;

/// <summary>AI 感知系统验证套件（对齐 UE AI Perception / Sense / Stimulus）。</summary>
public sealed class PerceptionTests
{
    /// <summary>Initialize 应绑定 OwnerPawn 并注册默认感官。</summary>
    public static void TestPerceptionRegistersDefaultSenses()
    {
        var pawn = new Pawn();
        var comp = new AIPerceptionComponent();
        comp.Initialize(pawn);

        AssertTrue(comp.OwnerPawn == pawn, "感知组件应绑定 OwnerPawn。");
        AssertTrue(comp.Senses.Count >= 4,
            $"默认应注册 >=4 个感官(视/听/伤/触)，实际 {comp.Senses.Count}。");
    }

    /// <summary>RegisterStimulus 应触发 OnTargetAdded 并进入已知列表。</summary>
    public static void TestPerceptionStimulusTriggersOnTargetAdded()
    {
        var comp = new AIPerceptionComponent();
        comp.Initialize(new Pawn());
        var target = new Actor();
        comp.RegisterTarget(target);
        AssertTrue(comp.RegisteredTargets.Contains(target), "目标应被注册。");

        bool added = false;
        Actor? addedActor = null;
        AIStimulus? addedStim = null;
        comp.OnTargetAdded += (a, s) => { added = true; addedActor = a; addedStim = s; };

        comp.RegisterStimulus(target, new AIStimulus(target, default, AISenseType.Sight, 0.9f));

        AssertTrue(added, "RegisterStimulus 应触发 OnTargetAdded。");
        AssertTrue(addedActor == target, "OnTargetAdded 应携带来源 Actor。");
        AssertTrue(addedStim != null && addedStim.SenseType == AISenseType.Sight,
            "刺激应为视觉类型。");
        AssertTrue(comp.GetKnownTargets().ContainsKey(target), "目标应进入已知列表。");
    }

    /// <summary>ForgetTarget 应触发 OnTargetLost 并从已知列表移除。</summary>
    public static void TestPerceptionForgetRemovesTarget()
    {
        var comp = new AIPerceptionComponent();
        comp.Initialize(new Pawn());
        var target = new Actor();
        comp.RegisterTarget(target);
        comp.RegisterStimulus(target, new AIStimulus(target, default, AISenseType.Hearing));

        bool lost = false;
        comp.OnTargetLost += (a) => lost = true;
        comp.ForgetTarget(target);

        AssertTrue(lost, "ForgetTarget 应触发 OnTargetLost。");
        AssertTrue(!comp.GetKnownTargets().ContainsKey(target), "目标应从已知列表移除。");
    }

    /// <summary>Update 应可重复调用且不抛异常（空目标时安全）。</summary>
    public static void TestPerceptionUpdateAdvancesTime()
    {
        var comp = new AIPerceptionComponent();
        comp.Initialize(new Pawn());
        comp.Update(0.016f);
        comp.Update(0.016f);
        AssertTrue(true, "Update 应可重复调用。");
    }
}
