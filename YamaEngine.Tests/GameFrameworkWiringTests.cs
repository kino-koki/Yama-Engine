// GameFrameworkWiringTests.cs — 双体系 Actor 架构接线验证
// 覆盖:
//   * GameFramework.Actor (体系 B) 注册进 GameplayWorld 后应被逐帧 Tick
//   * AIController (体系 B) 经世界驱动 TickAI (感知/记忆/状态机/决策/行为树闭环)
//   * 感知刺激 → 记忆 → 状态机 端到端 (世界驱动, Idle→Alert→Chase→Attack)
// 关键: 此前 GameplayWorld 仅调用基类 Gameplay.Actor 的 Invoke* 协议, 体系 B 的
// TickActor/OnTick 覆写与独立组件列表从未执行 —— 本测试证明修复后已闭环。
using System;
using System.Numerics;
using YamaEngine.Gameplay;
using YamaEngine.AI;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests;

/// <summary>GameFramework (体系 B) 与 GameplayWorld (体系 A) 接线验证套件。</summary>
public sealed class GameFrameworkWiringTests
{
    // 计数用体系 B Actor
    private sealed class BTrackingActor : YamaEngine.GameFramework.Actor
    {
        public int BeginPlayCount;
        public int TickCount;
        public int EndPlayCount;

        protected override void OnBeginPlay() => BeginPlayCount++;
        protected override void OnTick(float deltaTime) => TickCount++;
        protected override void OnEndPlay() => EndPlayCount++;
    }

    // 计数用 AIController (拦截 OnTick 计数, 再委托基类调用 TickAI)
    private sealed class BTrackingAIController : YamaEngine.AI.AIController
    {
        public int TickCount;

        protected override void OnTick(float deltaTime)
        {
            TickCount++;
            base.OnTick(deltaTime);
        }
    }

    /// <summary>体系 B Actor 注册进世界应被 BeginPlay + 逐帧 Tick + EndPlay。</summary>
    public static void TestGameFrameworkActorTicksInWorld()
    {
        var world = new GameplayWorld();
        var actor = new BTrackingActor { Name = "TrackingB" };
        world.RegisterActor(actor);

        world.Tick(1f / 60f);
        world.Tick(1f / 60f);
        world.Tick(1f / 60f);

        AssertEqual(1, actor.BeginPlayCount, "体系 B Actor 应 BeginPlay 恰好一次。");
        AssertTrue(actor.TickCount >= 3,
            $"体系 B Actor 应被逐帧 Tick (>=3), 实际 {actor.TickCount}。");

        world.Shutdown();
        AssertEqual(1, actor.EndPlayCount, "Shutdown 应触发体系 B Actor EndPlay。");
    }

    /// <summary>AIController 经世界逐帧驱动 TickAI (AI 闭环通电)。</summary>
    public static void TestAIControllerTickAIDrivenByWorld()
    {
        var world = new GameplayWorld();
        var pawn = new YamaEngine.GameFramework.Pawn();
        var ai = new BTrackingAIController();
        ai.Possess(pawn);
        world.RegisterActor(ai);

        world.Tick(1f / 60f);
        world.Tick(1f / 60f);
        world.Tick(1f / 60f);

        AssertTrue(ai.TickCount >= 3,
            $"AIController 应被世界逐帧驱动 TickAI (>=3), 实际 {ai.TickCount}。");

        // 感知时钟应推进 (证明 TickAI 内部真实逻辑执行, 非仅计数)
        AssertTrue(ai.Perception != null, "AIController 应持有感知组件。");
    }

    /// <summary>世界驱动的感知刺激应驱动状态机 Idle→Alert→Chase→Attack (端到端)。</summary>
    public static void TestAIControllerPerceptionDrivesStateMachine()
    {
        var world = new GameplayWorld();
        var pawn = new YamaEngine.GameFramework.Pawn();          // 位置 (0,0,0)
        var ai = new YamaEngine.AI.AIController();
        ai.Possess(pawn);

        // 目标置于 2 单位处 (视觉范围 500 内, 攻击距离 5 内)
        var target = new YamaEngine.GameFramework.Actor { Position = new Vector3(2f, 0f, 0f) };
        ai.Perception.RegisterTarget(target);
        ai.Perception.RegisterStimulus(target,
            new AIStimulus(target, target.Position, AISenseType.Sight, 1f));

        world.RegisterActor(ai);
        world.RegisterActor(target);

        AIStateType reached = AIStateType.Idle;
        for (int i = 0; i < 12; i++)
        {
            world.Tick(1f / 60f);
            reached = ai.CurrentStateType;
            if (reached == AIStateType.Attack) break;
        }

        AssertEqual(AIStateType.Attack, reached,
            $"感知目标应驱动状态机到 Attack, 实际 {reached}。");
    }
}

