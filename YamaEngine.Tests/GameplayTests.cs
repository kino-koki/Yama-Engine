using System;
using System.IO;
using System.Numerics;
using System.Linq;
using System.Collections.Generic;
using YamaEngine.Gameplay;
using YamaEngine.Input;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests
{
    public static class GameplayTests
    {
        public static void TestGameplayActorLifecycleAndComponentTick()
        {
            var world = new GameplayWorld();
            var actor = new TrackingActor();
            world.RegisterActor(actor);
            var counter = actor.AttachComponent(new TrackingComponent());

            AssertEqual(0, actor.BeginPlayCount, "Actor should not begin play before world starts.");

            world.Start();

            AssertEqual(1, actor.BeginPlayCount, "GameplayWorld.Start should trigger actor BeginPlay once.");
            AssertEqual(1, counter.BeginPlayCount, "Component BeginPlay should be invoked when world starts.");
            AssertTrue(actor.LifecycleState == ActorLifecycleState.Ticking, "Actor should be in Ticking state after BeginPlay.");

            world.Tick(0.016f);
            world.Tick(0.016f);

            AssertEqual(2, actor.TickCount, "Actor.OnTick should be invoked every world tick.");
            AssertEqual(2, counter.TickCount, "Component.OnTick should be invoked every world tick.");

            counter.TickEnabled = false;
            world.Tick(0.016f);
            AssertEqual(3, actor.TickCount, "Actor should keep ticking even if a component is disabled.");
            AssertEqual(2, counter.TickCount, "Disabled component must not receive OnTick.");

            world.Shutdown();
            AssertEqual(1, actor.EndPlayCount, "EndPlay should be raised on shutdown.");
            AssertEqual(1, counter.EndPlayCount, "Component EndPlay should be raised on shutdown.");
        }

        public static void TestPlayerControllerPossessesPawnAndDrivesMovement()
        {
            var world = new GameplayWorld();
            var pc = world.RegisterActor(new PlayerController { Name = "TestPC" });
            var pawn = world.RegisterActor(new Pawn { Name = "TestPawn", MoveSpeed = 10f });

            pc.Possess(pawn);
            AssertTrue(pc.PossessedPawn == pawn, "PlayerController should possess the requested pawn.");
            AssertTrue(pawn.CurrentController == pc, "Pawn should reference its possessing controller.");

            world.Start();
            pc.HandleInput(new PlayerInputCommand(PlayerInputKind.MoveRight, 1f));
            world.Tick(0.1f);

            AssertTrue(MathF.Abs(pawn.Position.X - 1f) < 0.0001f, "Movement input should move pawn along X by moveSpeed * dt.");
            AssertTrue(MathF.Abs(pawn.Position.Z) < 0.0001f, "Pawn should not drift along Z without input.");

            pc.UnPossess();
            AssertTrue(pc.PossessedPawn == null, "UnPossess should clear PossessedPawn.");
            AssertTrue(pawn.CurrentController == null, "UnPossessed pawn should clear its controller back-reference.");

            var beforeX = pawn.Position.X;
            pc.HandleInput(new PlayerInputCommand(PlayerInputKind.MoveRight, 1f));
            world.Tick(0.1f);
            AssertTrue(MathF.Abs(pawn.Position.X - beforeX) < 0.0001f, "Unpossessed pawn must not receive controller input.");
        }

        public static void TestGameplayWorldSpawnsDefaultPlayerAndScoresMatch()
        {
            var mode = new GameMode
            {
                Name = "TestMode",
                DefaultPawnFactory = () => new Pawn { Name = "HeroPawn", MoveSpeed = 4f }
            };
            var world = new GameplayWorld(mode);

            world.Start();

            AssertTrue(world.LocalPlayerController != null, "GameplayWorld.Start should spawn a PlayerController via GameMode.");
            AssertTrue(world.LocalPlayerPawn != null, "GameplayWorld.Start should spawn a default Pawn via GameMode.");
            AssertEqual("HeroPawn", world.LocalPlayerPawn!.Name, "GameMode factory should drive the default pawn class.");
            AssertTrue(world.LocalPlayerController!.PossessedPawn == world.LocalPlayerPawn, "Spawned controller should possess spawned pawn.");

            mode.AddScore(10);
            mode.AddScore(5);
            AssertEqual(15, mode.PlayerScore, "GameMode should accumulate score across AddScore calls.");

            mode.EndMatch("Time Up");
            AssertTrue(mode.MatchHasEnded, "GameMode.EndMatch should set MatchHasEnded.");
            AssertEqual("Time Up", mode.EndReason, "GameMode.EndMatch should record the reason.");

            world.DispatchInput(new PlayerInputCommand(PlayerInputKind.MoveForward, 1f));
            world.Tick(0.25f);
            AssertTrue(MathF.Abs(world.LocalPlayerPawn.Position.Z - 1f) < 0.0001f, "DispatchInput should route through LocalPlayerController to Pawn.");
        }

        public static void TestGameplayWorldDestroysActorAndStopsTicking()
        {
            var world = new GameplayWorld();
            var actor = world.RegisterActor(new TrackingActor());
            world.Start();
            world.Tick(0.016f);
            AssertEqual(1, actor.TickCount, "Actor should tick at least once before destroy.");

            world.DestroyActor(actor);
            world.Tick(0.016f);

            AssertEqual(1, actor.TickCount, "Destroyed actor must not receive further ticks.");
            AssertEqual(1, actor.EndPlayCount, "Destroyed actor should receive EndPlay exactly once.");
            AssertTrue(!world.Actors.Contains(actor), "Destroyed actor should be removed from world.Actors.");
        }
    }
}
