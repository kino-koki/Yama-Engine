using System;
using System.IO;
using System.Numerics;
using System.Linq;
using System.Collections.Generic;
using YamaEngine.Assets;
using YamaEngine.Build;
using YamaEngine.Input;
using YamaEngine.Rendering;
using YamaEngine.Runtime;
using YamaEngine.Scenes;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests
{
    public static class RuntimeTests
    {
        public static void TestRuntimeSceneLoaderLoadsExportedDemoPackage(string tempRoot)
        {
            var projectPath = Path.Combine(tempRoot, "RuntimeLoadProject");
            Directory.CreateDirectory(projectPath);

            var sourcePath = Path.Combine(tempRoot, "runtime_cube.obj");
            File.WriteAllText(sourcePath, "o RuntimeCube\nv 0 0 0\nv 1 0 0\nv 0 1 0\nf 1 2 3\n");

            var db = new AssetDatabase(projectPath);
            var imported = db.ImportAsset(sourcePath);
            var cube = db.CreateObject("RuntimeCube", imported!.AssetPath, new Vector3(2, 0, 1));
            cube.MaterialAssetPath = "Assets/Materials/RuntimeCopper.shmat";
            cube.BaseColor = new Vector3(0.8f, 0.35f, 0.15f);
            db.CreateCamera(new Vector3(0, 2, -6), true);
            db.CreateLight("Directional", new Vector3(0, 4, 0), new Vector3(1, 0.95f, 0.9f));

            var outputRoot = Path.Combine(tempRoot, "RuntimeExports");
            var build = BuildPipeline.Build(new BuildOptions
            {
                ProjectName = "RuntimeLoadProject",
                OutputDirectory = outputRoot,
                SceneName = "Main",
                Assets = db
            });

            var load = RuntimeSceneLoader.LoadPackage(build.OutputDirectory);

            AssertTrue(load.Success, "Runtime loader should load a valid exported demo package.");
            AssertTrue(load.World != null, "Runtime loader should return a world.");
            AssertEqual("Main", load.World!.SceneName, "Runtime world should keep scene name.");
            AssertEqual(3, load.World.Objects.Count, "Runtime world should include mesh, camera and light objects.");
            AssertEqual("Content/MainScene.yama", load.Manifest.Scene, "Runtime loader should read manifest scene path.");
            AssertEqual(1, load.World.RenderScene.Meshes.Count, "Runtime render scene should include renderable mesh.");
            AssertEqual(1, load.World.RenderScene.Cameras.Count, "Runtime render scene should include camera.");
            AssertEqual(1, load.World.RenderScene.Lights.Count, "Runtime render scene should include light.");
            AssertTrue(load.World.RenderScene.Meshes[0].WorldMatrix.M41 == 2f, "Runtime render mesh should keep scene translation.");
        }

        public static void TestRuntimeSceneLoaderReportsMissingPackageFiles(string tempRoot)
        {
            var packageRoot = Path.Combine(tempRoot, "BrokenRuntimePackage");
            Directory.CreateDirectory(packageRoot);

            var load = RuntimeSceneLoader.LoadPackage(packageRoot);

            AssertTrue(!load.Success, "Runtime loader should fail for an incomplete package.");
            AssertTrue(load.World == null, "Runtime loader should not return a world for an incomplete package.");
            AssertTrue(load.Errors.Any(e => e.Contains(BuildPipeline.ManifestFileName)), "Runtime loader should report missing manifest.");
            AssertTrue(load.Errors.Any(e => e.Contains("MainScene.yama")), "Runtime loader should report missing main scene.");
        }

        public static void TestRuntimeGameLoopTicksComponentsAndRendersFrame()
        {
            var obj = new SceneObject
            {
                Name = "LoopCube",
                MeshAssetPath = "Assets/Meshes/LoopCube.obj",
                Position = new Vector3(1, 0, 0)
            };
            var mover = new RuntimeCounterComponent(new Vector3(4, 0, 0));
            obj.Components.Add(mover);

            var objects = new List<SceneObject> { obj };
            var world = new RuntimeWorld
            {
                SceneName = "LoopScene",
                Objects = objects,
                RenderScene = RenderSceneBuilder.FromObjects(objects, "LoopScene")
            };

            var loop = new RuntimeGameLoop(
                world,
                new NullRenderDevice("Loop Device"));

            var tick = loop.Tick(0.5f);

            AssertEqual(1, mover.UpdateCount, "Runtime loop should update scene object components once per variable tick.");
            AssertEqual(0.5f, mover.TotalDeltaSeconds, "Runtime loop should pass delta time to components.");
            AssertEqual(new Vector3(3, 0, 0), obj.Position, "Runtime loop component should be able to mutate object transform.");
            AssertEqual(1, tick.FrameIndex, "Runtime loop should increment frame index.");
            AssertEqual(1, tick.FrameStats.MeshCount, "Runtime loop should render one mesh.");
            AssertTrue(tick.FrameStats.RenderGraphPassCount >= 5, "Runtime loop should submit render graph when provided.");
            AssertTrue(world.RenderScene.Meshes[0].WorldMatrix.M41 == 3f, "Runtime loop should rebuild render snapshot after component updates.");
        }

        public static void TestRuntimeGameLoopRunsFixedStepsWithAccumulator()
        {
            var obj = new SceneObject
            {
                Name = "FixedStepCube",
                MeshAssetPath = "Assets/Meshes/FixedStepCube.obj",
                Position = Vector3.Zero
            };
            var mover = new RuntimeCounterComponent(new Vector3(10, 0, 0));
            obj.Components.Add(mover);

            var objects = new List<SceneObject> { obj };
            var world = new RuntimeWorld
            {
                SceneName = "FixedLoop",
                Objects = objects,
                RenderScene = RenderSceneBuilder.FromObjects(objects, "FixedLoop")
            };

            var loop = new RuntimeGameLoop(
                world,
                new NullRenderDevice(),
                new RuntimeLoopOptions
                {
                    UseFixedStep = true,
                    FixedDeltaTime = 0.02f,
                    MaxFixedStepsPerTick = 3
                });

            var tick = loop.Tick(0.07f);

            AssertEqual(3, tick.FixedStepCount, "Runtime loop should cap fixed steps per tick.");
            AssertEqual(3, mover.UpdateCount, "Runtime loop should update components once per fixed step.");
            AssertTrue(MathF.Abs(mover.TotalDeltaSeconds - 0.06f) < 0.0001f, "Runtime loop should accumulate fixed-step delta time.");
            AssertTrue(MathF.Abs(tick.AccumulatorSeconds - 0.01f) < 0.0001f, "Runtime loop should preserve leftover fixed-step accumulator.");
            AssertTrue(MathF.Abs(loop.TotalTimeSeconds - 0.06f) < 0.0001f, "Runtime loop total time should advance by consumed fixed steps.");
            AssertTrue(MathF.Abs(obj.Position.X - 0.6f) < 0.0001f, "Runtime fixed steps should mutate object transform deterministically.");
        }

        public static void TestRuntimeGameLoopSamplesInputBeforeComponents()
        {
            var input = new InputSystem();
            input.SetKeyDown(InputKey.W);

            var obj = new SceneObject
            {
                Name = "InputDrivenPlayer",
                MeshAssetPath = "Assets/Meshes/Player.obj",
                Position = Vector3.Zero
            };
            var mover = new RuntimeInputMoverComponent(2f);
            obj.Components.Add(mover);

            var objects = new List<SceneObject> { obj };
            var world = new RuntimeWorld
            {
                SceneName = "InputLoop",
                Objects = objects,
                RenderScene = RenderSceneBuilder.FromObjects(objects, "InputLoop")
            };

            var loop = new RuntimeGameLoop(
                world,
                new NullRenderDevice(),
                new RuntimeLoopOptions { Input = input });

            var tick = loop.Tick(0.5f);

            AssertEqual(1, mover.InputUpdateCount, "Runtime input component should receive input once per tick.");
            AssertEqual(1, mover.RegularUpdateCount, "Runtime component should still receive regular update once per tick.");
            AssertTrue(input.IsActionPressed("Gameplay", "MoveForward"), "Runtime loop should update input actions before input components.");
            AssertEqual(new Vector2(0, 1), mover.LastMovement, "Runtime input component should read gameplay movement vector.");
            AssertTrue(MathF.Abs(obj.Position.Z - 1f) < 0.0001f, "Runtime input component should move object from sampled input.");
            AssertEqual(1, tick.Input.ActiveActionCount, "Runtime tick result should count active input actions.");
        }

        public static void TestRuntimeGameLoopReportsInputFrameStats()
        {
            var input = new InputSystem();
            input.SetKeyDown(InputKey.W);
            input.SetKeyDown(InputKey.Space);
            input.SetAxis(InputAxis.GamepadRightStickX, 0.75f);

            var obj = new SceneObject
            {
                Name = "InputStatsObject",
                MeshAssetPath = "Assets/Meshes/InputStats.obj"
            };
            var objects = new List<SceneObject> { obj };
            var world = new RuntimeWorld
            {
                SceneName = "InputStats",
                Objects = objects,
                RenderScene = RenderSceneBuilder.FromObjects(objects, "InputStats")
            };

            var loop = new RuntimeGameLoop(
                world,
                new NullRenderDevice(),
                new RuntimeLoopOptions { Input = input });

            var tick = loop.Tick(1f / 60f);

            AssertEqual(2, tick.Input.PressedKeyCount, "Runtime input snapshot should count pressed keys.");
            AssertTrue(tick.Input.ActiveActionCount >= 3, "Runtime input snapshot should count key and axis actions.");
            AssertTrue(tick.Input.ActiveActions.Contains("Gameplay/MoveForward"), "Runtime input snapshot should include MoveForward.");
            AssertTrue(tick.Input.ActiveActions.Contains("Gameplay/Jump"), "Runtime input snapshot should include Jump.");
            AssertTrue(tick.Input.ActiveActions.Contains("Gameplay/LookRight"), "Runtime input snapshot should include axis-driven LookRight.");
        }

        public static void TestRuntimeLifecycleBridgeCreatesAndSyncsEcsEntity()
        {
            var ecs = new YamaEngine.ECS.EntityComponentSystem();
            var bridge = new RuntimeGameObjectEntityBridge(ecs);
            var obj = new SceneObject
            {
                Name = "BridgeActor",
                Position = new Vector3(2, 3, 4),
                Rotation = Quaternion.Identity,
                Scale = new Vector3(1, 2, 1),
                MeshAssetPath = "Assets/Meshes/BridgeActor.obj"
            };

            var binding = bridge.BindObject(obj);

            AssertTrue(ecs.HasEntity(binding.EntityId), "Bridge should create an ECS entity for a runtime object.");
            AssertTrue(ecs.HasComponent<YamaEngine.ECS.TransformComponent>(binding.EntityId), "Bridge should add a transform component.");
            AssertTrue(ecs.HasComponent<YamaEngine.ECS.MeshComponent>(binding.EntityId), "Bridge should add a mesh component for mesh objects.");
            AssertEqual("BridgeActor", ecs.GetEntityName(binding.EntityId), "Bridge should preserve object names.");

            var transform = ecs.GetComponent<YamaEngine.ECS.TransformComponent>(binding.EntityId);
            transform.Position = new Vector3(7, 8, 9);
            ecs.SetComponent(binding.EntityId, transform);
            bridge.SyncEntitiesToObjects();

            AssertEqual(new Vector3(7, 8, 9), obj.Position, "Bridge should sync ECS transform changes back to GameObject data.");
        }

        public static void TestRuntimeCoroutineAndStateMachineAdvanceDeterministically()
        {
            var clock = new RuntimeClock(new RuntimeClockOptions { FixedDeltaTime = 0.1f, MaxDeltaTime = 0.5f });
            var scheduler = new RuntimeCoroutineScheduler(clock);
            var stateMachine = new RuntimeStateMachine<string>("Idle");
            var visited = new List<string>();

            stateMachine.Configure("Idle")
                .OnEnter(_ => visited.Add("EnterIdle"))
                .Permit("Move", "Moving");
            stateMachine.Configure("Moving")
                .OnEnter(_ => visited.Add("EnterMoving"))
                .OnTick(ctx => ctx.Blackboard["distance"] = (float)ctx.Blackboard.GetValueOrDefault("distance", 0f) + ctx.DeltaTime)
                .PermitIf("Stop", "Idle", ctx => (float)ctx.Blackboard.GetValueOrDefault("distance", 0f) >= 0.2f);

            var steps = 0;
            scheduler.Start(Routine(), "DeterministicRoutine");
            stateMachine.Start();
            stateMachine.Fire("Move");

            for (int i = 0; i < 4; i++)
            {
                var frame = clock.Step(0.1f);
                scheduler.Tick(frame);
                stateMachine.Tick(frame.DeltaTime);
                stateMachine.Fire("Stop");
            }

            AssertEqual(2, steps, "Coroutine should resume after frame and seconds waits.");
            AssertEqual("Idle", stateMachine.CurrentState, "State machine should return to Idle after guarded transition.");
            AssertTrue(visited.Contains("EnterMoving"), "State machine should run enter callbacks.");

            IEnumerable<object?> Routine()
            {
                yield return RuntimeWait.ForFrames(2);
                steps++;
                yield return RuntimeWait.ForSeconds(0.2f);
                steps++;
            }
        }

        public static void TestRuntimeSaveGameStoreRoundtripsVariablesAndObjectTransforms()
        {
            var obj = new SceneObject
            {
                Name = "SavedActor",
                Position = new Vector3(3, 4, 5),
                Rotation = Quaternion.Identity,
                Scale = new Vector3(2, 2, 2),
                MeshAssetPath = "Assets/Meshes/SavedActor.obj",
                MaterialAssetPath = "Assets/Materials/SavedActor.mat"
            };
            var world = new RuntimeWorld
            {
                SceneName = "SaveScene",
                Objects = new[] { obj }
            };
            var store = new RuntimeSaveGameStore();
            var slot = new RuntimeSaveSlot("slot-1", "Quick Save");
            var snapshot = RuntimeSaveSnapshot.FromWorld(world, slot);
            snapshot.GameVariables["quest.main"] = "complete";

            var bytes = store.Write(snapshot);
            var loaded = store.Read(bytes);
            var restored = loaded.RestoreWorld();

            AssertEqual("slot-1", loaded.Slot.Id, "Save store should keep slot id.");
            AssertEqual("complete", loaded.GameVariables["quest.main"], "Save store should preserve gameplay variables.");
            AssertEqual("SaveScene", restored.SceneName, "Save store should restore scene name.");
            AssertEqual(new Vector3(3, 4, 5), restored.Objects[0].Position, "Save store should restore transforms.");
            AssertTrue(bytes.Length > 64, "Save payload should include header and compressed snapshot data.");
        }
    }
}
