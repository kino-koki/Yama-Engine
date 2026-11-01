using YamaEngine.Testing;

namespace YamaEngine.Tests
{
    /// <summary>
    /// Gameplay 测试：Actor 生命周期、PlayerController、GameMode
    /// </summary>
    [TestClass]
    public class GameplayTests : EngineTestFixture
    {
        [TestMethod]
        public void GameplayActorLifecycleAndComponentTick()
        {
            var world = new YamaEngine.Gameplay.GameplayWorld();
            var actor = new TrackingActor();
            world.RegisterActor(actor);
            var counter = actor.AttachComponent(new TrackingComponent());

            Assert.AreEqual(0, actor.BeginPlayCount);

            world.Start();

            Assert.AreEqual(1, actor.BeginPlayCount);
            Assert.AreEqual(1, counter.BeginPlayCount);
            Assert.IsTrue(actor.LifecycleState == YamaEngine.Gameplay.ActorLifecycleState.Ticking);

            world.Tick(0.016f);
            world.Tick(0.016f);

            Assert.AreEqual(2, actor.TickCount);
            Assert.AreEqual(2, counter.TickCount);

            counter.TickEnabled = false;
            world.Tick(0.016f);
            Assert.AreEqual(3, actor.TickCount);
            Assert.AreEqual(2, counter.TickCount);

            world.Shutdown();
            Assert.AreEqual(1, actor.EndPlayCount);
            Assert.AreEqual(1, counter.EndPlayCount);
        }

        [TestMethod]
        public void PlayerControllerPossessesPawnAndDrivesMovement()
        {
            var world = new YamaEngine.Gameplay.GameplayWorld();
            var pc = world.RegisterActor(new YamaEngine.Gameplay.PlayerController { Name = "TestPC" });
            var pawn = world.RegisterActor(new YamaEngine.Gameplay.Pawn { Name = "TestPawn", MoveSpeed = 10f });

            pc.Possess(pawn);
            Assert.IsTrue(pc.PossessedPawn == pawn);
            Assert.IsTrue(pawn.CurrentController == pc);

            world.Start();
            pc.HandleInput(new YamaEngine.Gameplay.PlayerInputCommand(YamaEngine.Gameplay.PlayerInputKind.MoveRight, 1f));
            world.Tick(0.1f);

            Assert.IsTrue(MathF.Abs(pawn.Position.X - 1f) < 0.0001f);

            pc.UnPossess();
            Assert.IsTrue(pc.PossessedPawn == null);
            Assert.IsTrue(pawn.CurrentController == null);

            var beforeX = pawn.Position.X;
            pc.HandleInput(new YamaEngine.Gameplay.PlayerInputCommand(YamaEngine.Gameplay.PlayerInputKind.MoveRight, 1f));
            world.Tick(0.1f);
            Assert.IsTrue(MathF.Abs(pawn.Position.X - beforeX) < 0.0001f);
        }

        [TestMethod]
        public void GameplayWorldSpawnsDefaultPlayerAndScoresMatch()
        {
            var mode = new YamaEngine.Gameplay.GameMode
            {
                Name = "TestMode",
                DefaultPawnFactory = () => new YamaEngine.Gameplay.Pawn { Name = "HeroPawn", MoveSpeed = 4f }
            };
            var world = new YamaEngine.Gameplay.GameplayWorld(mode);

            world.Start();

            Assert.IsNotNull(world.LocalPlayerController);
            Assert.IsNotNull(world.LocalPlayerPawn);
            Assert.AreEqual("HeroPawn", world.LocalPlayerPawn!.Name);
            Assert.IsTrue(world.LocalPlayerController!.PossessedPawn == world.LocalPlayerPawn);

            mode.AddScore(10);
            mode.AddScore(5);
            Assert.AreEqual(15, mode.PlayerScore);

            mode.EndMatch("Time Up");
            Assert.IsTrue(mode.MatchHasEnded);
            Assert.AreEqual("Time Up", mode.EndReason);
        }

        [TestMethod]
        public void GameplayWorldDestroysActorAndStopsTicking()
        {
            var world = new YamaEngine.Gameplay.GameplayWorld();
            var actor = world.RegisterActor(new TrackingActor());
            world.Start();
            world.Tick(0.016f);
            Assert.AreEqual(1, actor.TickCount);

            world.DestroyActor(actor);
            world.Tick(0.016f);

            Assert.AreEqual(1, actor.TickCount);
            Assert.AreEqual(1, actor.EndPlayCount);
            Assert.IsFalse(world.Actors.Contains(actor));
        }
    }

    // ---- 辅助测试类型 ----

    sealed class TrackingActor : YamaEngine.Gameplay.Actor
    {
        public int BeginPlayCount { get; private set; }
        public int TickCount { get; private set; }
        public int EndPlayCount { get; private set; }

        protected override void OnBeginPlay() => BeginPlayCount++;
        protected override void OnTick(float deltaTime) => TickCount++;
        protected override void OnEndPlay() => EndPlayCount++;
    }

    sealed class TrackingComponent : YamaEngine.Gameplay.ActorComponent
    {
        public int BeginPlayCount { get; private set; }
        public int TickCount { get; private set; }
        public int EndPlayCount { get; private set; }
        public bool TickEnabled { get; set; } = true;

        protected override void OnBeginPlay() => BeginPlayCount++;
        protected override void OnTick(float deltaTime)
        {
            if (TickEnabled) TickCount++;
        }
        protected override void OnEndPlay() => EndPlayCount++;
    }
}
