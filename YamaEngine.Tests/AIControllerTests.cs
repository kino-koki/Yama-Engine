// AIControllerTests.cs — AIController 感知→记忆→状态机→决策→行为树链路验证
// 覆盖：Possess 初始化感知、TickAI 驱动感知/记忆/状态机（闭环可运行）。
// 全部 CPU 级确定性，不依赖 GPU/场景/LLM。
using System;
using YamaEngine.AI;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests;

/// <summary>AIController TickAI 链路验证套件。</summary>
public sealed class AIControllerTests
{
    /// <summary>Possess 应绑定 OwnerPawn 并注册默认感官。</summary>
    public static void TestAIControllerPossessInitializesPerception()
    {
        var pawn = new YamaEngine.GameFramework.Pawn();
        var ctrl = new YamaEngine.AI.AIController();
        ctrl.Possess(pawn);

        AssertTrue(ctrl.PossessedPawn == pawn, "Possess 应绑定 Pawn。");
        AssertTrue(ctrl.Perception.OwnerPawn == pawn, "感知应绑定 OwnerPawn。");
        AssertTrue(ctrl.Perception.Senses.Count >= 4,
            $"默认应注册 >=4 感官(视/听/伤/触)，实际 {ctrl.Perception.Senses.Count}。");
    }

    /// <summary>TickAI 应驱动感知：已注入刺激保留在已知列表。</summary>
    public static void TestAIControllerTickAIDrivesPerception()
    {
        var ctrl = new YamaEngine.AI.AIController();
        ctrl.Possess(new YamaEngine.GameFramework.Pawn());
        var target = new YamaEngine.GameFramework.Actor();
        ctrl.Perception.RegisterTarget(target);
        ctrl.Perception.RegisterStimulus(target, new AIStimulus(target, default, AISenseType.Sight));

        ctrl.TickAI(0.016f);

        AssertTrue(ctrl.Perception.GetKnownTargets().ContainsKey(target),
            "TickAI 后目标应保留在感知已知列表。");
    }

    /// <summary>TickAI 应把感知目标写入记忆（感知→记忆闭环）。</summary>
    public static void TestAIControllerTickAIUpdatesMemory()
    {
        var ctrl = new YamaEngine.AI.AIController();
        ctrl.Possess(new YamaEngine.GameFramework.Pawn());
        var target = new YamaEngine.GameFramework.Actor();
        ctrl.Perception.RegisterTarget(target);
        ctrl.Perception.RegisterStimulus(target, new AIStimulus(target, default, AISenseType.Sight, 0.8f));

        ctrl.TickAI(0.016f);

        AssertTrue(ctrl.Memory.GetKnownTargets().Contains(target),
            "感知目标应同步到 AI 记忆。");
    }

    /// <summary>TickAI 应驱动状态机且可重复调用（不抛异常）。</summary>
    public static void TestAIControllerTickAIStateMachineRuns()
    {
        var ctrl = new YamaEngine.AI.AIController();
        ctrl.Possess(new YamaEngine.GameFramework.Pawn());
        var target = new YamaEngine.GameFramework.Actor();
        ctrl.Perception.RegisterTarget(target);
        ctrl.Perception.RegisterStimulus(target, new AIStimulus(target, default, AISenseType.Sight));

        ctrl.TickAI(0.016f);
        ctrl.TickAI(0.016f);
        ctrl.TickAI(0.016f);

        // 状态机已运行：感知目标>0 → Alert → Chase → (目标<5) → Attack 转移链应可达合法状态
        var type = ctrl.CurrentStateType;
        AssertTrue(type == AIStateType.Idle || type == AIStateType.Alert ||
                   type == AIStateType.Chase || type == AIStateType.Attack,
            $"状态机应进入合法状态(Idle/Alert/Chase/Attack)，实际 {type}。");
    }
}
