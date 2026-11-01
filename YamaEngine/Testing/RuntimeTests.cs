using YamaEngine.Testing;

namespace YamaEngine.Tests
{
    /// <summary>
    /// 运行时测试：GameLoop、SceneLoader
    /// </summary>
    [TestClass]
    public class RuntimeTests : EngineTestFixture
    {
        [TestMethod]
        public void RuntimeSceneLoaderLoadsExportedDemoPackage()
        {
            var projectPath = Path.Combine(TempDirectory, "RuntimeLoadProject");
            Directory.CreateDirectory(projectPath);

            var sourcePath = Path.Combine(TempDirectory, "runtime_cube.obj");
            File.WriteAllText(sourcePath, "o RuntimeCube\nv 0 0 0\nv 1 0 0\nv 0 1 0\nf 1 2 3\n");

            var db = new YamaEngine.Assets.AssetDatabase(projectPath);
            var imported = db.ImportAsset(sourcePath);
            var cube = db.CreateObject("RuntimeCube", imported!.AssetPath, new System.Numerics.Vector3(2, 0, 1));
            cube.MaterialAssetPath = "Assets/Materials/RuntimeCopper.shmat";
            cube.BaseColor = new System.Numerics.Vector3(0.8f, 0.35f, 0.15f);
            db.CreateCamera(new System.Numerics.Vector3(0, 2, -6), true);
            db.CreateLight("Directional", new System.Numerics.Vector3(0, 4, 0), new System.Numerics.Vector3(1, 0.95f, 0.9f));

            var outputRoot = Path.Combine(TempDirectory, "RuntimeExports");
            var build = YamaEngine.Build.BuildPipeline.Build(new YamaEngine.Build.BuildOptions
            {
                ProjectName = "RuntimeLoadProject",
                OutputDirectory = outputRoot,
                SceneName = "Main",
                Assets = db
            });

            var load = YamaEngine.Runtime.RuntimeSceneLoader.LoadPackage(build.OutputDirectory);

            Assert.IsTrue(load.Success);
            Assert.IsNotNull(load.World);
            Assert.AreEqual("Main", load.World!.SceneName);
            Assert.AreEqual(3, load.World.Objects.Count);
            Assert.AreEqual("Content/MainScene.yama", load.Manifest.Scene);
        }

        [TestMethod]
        public void RuntimeSceneLoaderReportsMissingPackageFiles()
        {
            var packageRoot = Path.Combine(TempDirectory, "BrokenRuntimePackage");
            Directory.CreateDirectory(packageRoot);

            var load = YamaEngine.Runtime.RuntimeSceneLoader.LoadPackage(packageRoot);

            Assert.IsFalse(load.Success);
            Assert.IsNull(load.World);
            Assert.IsTrue(load.Errors.Any(e => e.Contains(YamaEngine.Build.BuildPipeline.ManifestFileName)));
            Assert.IsTrue(load.Errors.Any(e => e.Contains("MainScene.yama")));
        }

        [TestMethod]
        public void RuntimeGameLoopTicksComponentsAndRendersFrame()
        {
            var obj = new YamaEngine.Assets.SceneObject
            {
                Name = "LoopCube",
                MeshAssetPath = "Assets/Meshes/LoopCube.obj",
                Position = new System.Numerics.Vector3(1, 0, 0)
            };
            var mover = new RuntimeCounterComponent(new System.Numerics.Vector3(4, 0, 0));
            obj.Components.Add(mover);

            var objects = new List<YamaEngine.Assets.SceneObject> { obj };
            var world = new YamaEngine.Runtime.RuntimeWorld
            {
                SceneName = "LoopScene",
                Objects = objects,
                RenderScene = YamaEngine.Rendering.RenderSceneBuilder.FromObjects(objects, "LoopScene")
            };

            var loop = new YamaEngine.Runtime.RuntimeGameLoop(
                world,
                new YamaEngine.Rendering.NullRenderDevice("Loop Device"),
                renderGraph: new YamaEngine.Rendering.RenderGraph(null, "RuntimeFrame"));

            var tick = loop.Tick(0.5f);

            Assert.AreEqual(1, mover.UpdateCount);
            Assert.AreEqual(0.5f, mover.TotalDeltaSeconds);
            Assert.AreEqual(new System.Numerics.Vector3(3, 0, 0), obj.Position);
            Assert.AreEqual(1, tick.FrameIndex);
        }

        [TestMethod]
        public void RuntimeGameLoopRunsFixedStepsWithAccumulator()
        {
            var obj = new YamaEngine.Assets.SceneObject
            {
                Name = "FixedStepCube",
                MeshAssetPath = "Assets/Meshes/FixedStepCube.obj",
                Position = System.Numerics.Vector3.Zero
            };
            var mover = new RuntimeCounterComponent(new System.Numerics.Vector3(10, 0, 0));
            obj.Components.Add(mover);

            var objects = new List<YamaEngine.Assets.SceneObject> { obj };
            var world = new YamaEngine.Runtime.RuntimeWorld
            {
                SceneName = "FixedLoop",
                Objects = objects,
                RenderScene = YamaEngine.Rendering.RenderSceneBuilder.FromObjects(objects, "FixedLoop")
            };

            var loop = new YamaEngine.Runtime.RuntimeGameLoop(
                world,
                new YamaEngine.Rendering.NullRenderDevice(),
                new YamaEngine.Runtime.RuntimeLoopOptions
                {
                    UseFixedStep = true,
                    FixedDeltaTime = 0.02f,
                    MaxFixedStepsPerTick = 3
                });

            var tick = loop.Tick(0.07f);

            Assert.AreEqual(3, tick.FixedStepCount);
            Assert.AreEqual(3, mover.UpdateCount);
            Assert.IsTrue(MathF.Abs(mover.TotalDeltaSeconds - 0.06f) < 0.0001f);
        }

        // 辅助组件
        private sealed class RuntimeCounterComponent : YamaEngine.Runtime.IComponent
        {
            private readonly System.Numerics.Vector3 _velocity;
            public string Name => "RuntimeCounter";
            public int UpdateCount { get; private set; }
            public float TotalDeltaSeconds { get; private set; }

            public RuntimeCounterComponent(System.Numerics.Vector3 velocity) => _velocity = velocity;

            public void Update(float deltaTime, YamaEngine.Assets.SceneObject owner)
            {
                UpdateCount++;
                TotalDeltaSeconds += deltaTime;
                owner.Position += _velocity * deltaTime;
            }
        }
    }
}
