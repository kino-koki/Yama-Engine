using ShanyuEngine.Testing;

namespace ShanyuEngine.Tests
{
    /// <summary>
    /// 渲染测试：RenderVertex 格式、WorldConstants 布局、MeshGpuResource 创建、RenderGraph、HZB、GPU Draw
    /// </summary>
    [TestClass]
    public class RenderTests : EngineTestFixture
    {
        [TestMethod]
        public void MeshCreation()
        {
            var mesh = new MockEngine.Mesh
            {
                Name = "Cube",
                Vertices = new[] { MockEngine.Vector3.Zero, MockEngine.Vector3.One },
                Indices = new[] { 0, 1 }
            };
            Assert.AreEqual("Cube", mesh.Name);
            Assert.AreEqual(2, mesh.Vertices.Length);
        }

        [TestMethod]
        public void LODGeneration()
        {
            var mesh = new MockEngine.Mesh
            {
                Name = "HighPoly",
                Vertices = Enumerable.Range(0, 100).Select(i => new MockEngine.Vector3(i, 0, 0)).ToArray(),
                Indices = Enumerable.Range(0, 300).ToArray()
            };
            var lod = MockEngine.MeshOperations.ComputeLOD(mesh, 0.5f);
            Assert.IsNotNull(lod);
            Assert.AreEqual("HighPoly_LOD", lod.Name);
            Assert.IsTrue(lod.Vertices.Length <= 50);
        }

        [TestMethod]
        public void MeshBooleanUnion()
        {
            var a = new MockEngine.Mesh
            {
                Name = "A",
                Vertices = new[] { MockEngine.Vector3.Zero },
                Indices = new[] { 0 }
            };
            var b = new MockEngine.Mesh
            {
                Name = "B",
                Vertices = new[] { MockEngine.Vector3.One },
                Indices = new[] { 0 }
            };
            var u = MockEngine.MeshOperations.BooleanUnion(a, b);
            Assert.AreEqual(2, u.Vertices.Length);
            Assert.AreEqual(2, u.Indices.Length);
        }

        [TestMethod]
        public void MeshLODLevels()
        {
            var mesh = new MockEngine.Mesh
            {
                Name = "Tree",
                Vertices = new MockEngine.Vector3[1000],
                Indices = new int[3000],
                LODLevel = 0
            };
            var lod1 = MockEngine.MeshOperations.ComputeLOD(mesh, 0.5f);
            var lod2 = MockEngine.MeshOperations.ComputeLOD(lod1, 0.5f);
            Assert.AreEqual(1, lod1.LODLevel);
            Assert.AreEqual(2, lod2.LODLevel);
        }

        [TestMethod]
        public void RenderSceneBuilderCreatesStableSnapshot()
        {
            var projectPath = Path.Combine(TempDirectory, "RenderSnapshotProject");
            Directory.CreateDirectory(projectPath);

            var db = new ShanyuEngine.Assets.AssetDatabase(projectPath);
            var cube = db.CreateObject("RenderableCube", "Assets/Meshes/Cube.obj", new System.Numerics.Vector3(3, 2, 1));
            cube.MaterialAssetPath = "Assets/Materials/Copper.shmat";
            cube.BaseColor = new System.Numerics.Vector3(0.9f, 0.4f, 0.2f);
            cube.Metallic = 1f;
            cube.Roughness = 0.25f;
            cube.Scale = new System.Numerics.Vector3(2, 2, 2);
            cube.CastShadow = true;
            cube.ReceiveShadow = false;

            var hidden = db.CreateObject("HiddenCube", "Assets/Meshes/Hidden.obj", new System.Numerics.Vector3(0, 0, 0));
            hidden.IsVisible = false;

            db.CreateCamera(new System.Numerics.Vector3(0, 2, -6), true);
            db.CreateLight("Directional", new System.Numerics.Vector3(0, 5, 0), new System.Numerics.Vector3(1, 0.95f, 0.9f));

            var scene = ShanyuEngine.Rendering.RenderSceneBuilder.FromAssetDatabase(db, "Main");

            Assert.AreEqual("Main", scene.Name);
            Assert.AreEqual(1, scene.Meshes.Count);
            Assert.AreEqual(1, scene.Cameras.Count);
            Assert.AreEqual(1, scene.Lights.Count);

            var mesh = scene.Meshes[0];
            Assert.AreEqual("RenderableCube", mesh.Name);
            Assert.AreEqual("Assets/Meshes/Cube.obj", mesh.MeshAssetPath);
            Assert.AreEqual(1f, mesh.Material.Metallic);
            Assert.AreEqual(0.25f, mesh.Material.Roughness);
            Assert.AreEqual(1, scene.Stats.MeshCount);
        }

        [TestMethod]
        public void NullRenderDeviceReturnsFrameStats()
        {
            var projectPath = Path.Combine(TempDirectory, "NullRenderProject");
            Directory.CreateDirectory(projectPath);

            var db = new ShanyuEngine.Assets.AssetDatabase(projectPath);
            db.CreateObject("CubeA", "Assets/Meshes/A.obj", new System.Numerics.Vector3(0, 0, 0));
            db.CreateObject("CubeB", "Assets/Meshes/B.obj", new System.Numerics.Vector3(1, 0, 0));
            db.CreateCamera(new System.Numerics.Vector3(0, 2, -5), true);

            var scene = ShanyuEngine.Rendering.RenderSceneBuilder.FromAssetDatabase(db, "Main");
            ShanyuEngine.Rendering.IRenderDevice device = new ShanyuEngine.Rendering.NullRenderDevice("Test Null Device");
            var stats = device.Render(scene, 1f / 60f);

            Assert.AreEqual("Test Null Device", device.Name);
            Assert.AreEqual(2, stats.MeshCount);
            Assert.AreEqual(2, stats.DrawCallCount);
            Assert.AreEqual(1, stats.CameraCount);
            Assert.IsTrue(stats.FrameTimeMilliseconds >= 0f);
        }

        [TestMethod]
        public void RenderGraphCompilesPassDependencies()
        {
            var graph = new ShanyuEngine.Rendering.RenderGraph("DeferredFrame");
            var depth = graph.CreateTexture("SceneDepth", ShanyuEngine.Rendering.RenderResourceFormat.Depth24);
            var geometrybuffer = graph.CreateTexture("GeometryBufferA", ShanyuEngine.Rendering.RenderResourceFormat.Rgba16Float);
            var lit = graph.CreateTexture("LitScene", ShanyuEngine.Rendering.RenderResourceFormat.Rgba16Float);
            var swapchain = graph.ImportTexture("BackBuffer", ShanyuEngine.Rendering.RenderResourceFormat.Rgba8Unorm);

            graph.AddPass("Lighting", ShanyuEngine.Rendering.RenderPassKind.Lighting).Reads(geometrybuffer).Reads(depth).Writes(lit);
            graph.AddPass("Geometry", ShanyuEngine.Rendering.RenderPassKind.Geometry).Writes(depth).Writes(geometrybuffer);
            graph.AddPass("Tonemap", ShanyuEngine.Rendering.RenderPassKind.PostProcess).Reads(lit).Writes(swapchain);

            var plan = graph.Compile();

            Assert.AreEqual(3, plan.Passes.Count);
            Assert.AreEqual("Geometry", plan.Passes[0].Name);
            Assert.AreEqual("Lighting", plan.Passes[1].Name);
            Assert.AreEqual("Tonemap", plan.Passes[2].Name);
            Assert.IsTrue(plan.IsValid);
        }

        [TestMethod]
        public void RenderGraphDetectsCyclicResourceDependencies()
        {
            var graph = new ShanyuEngine.Rendering.RenderGraph("BrokenFrame");
            var a = graph.CreateTexture("A", ShanyuEngine.Rendering.RenderResourceFormat.Rgba8Unorm);
            var b = graph.CreateTexture("B", ShanyuEngine.Rendering.RenderResourceFormat.Rgba8Unorm);

            graph.AddPass("PassA", ShanyuEngine.Rendering.RenderPassKind.Compute).Reads(b).Writes(a);
            graph.AddPass("PassB", ShanyuEngine.Rendering.RenderPassKind.Compute).Reads(a).Writes(b);

            var plan = graph.Compile();

            Assert.IsFalse(plan.IsValid);
            Assert.IsTrue(plan.Errors.Any(e => e.Contains("cycle", StringComparison.OrdinalIgnoreCase)));
        }

        [TestMethod]
        public void NullRenderDeviceExecutesCompiledRenderGraph()
        {
            var scene = new ShanyuEngine.Rendering.RenderSceneSnapshot
            {
                Name = "GraphScene",
                Meshes = new[]
                {
                    new ShanyuEngine.Rendering.RenderMesh { Name = "MeshA", MeshAssetPath = "Assets/Meshes/A.shmesh", IsVisible = true },
                    new ShanyuEngine.Rendering.RenderMesh { Name = "MeshB", MeshAssetPath = "Assets/Meshes/B.shmesh", IsVisible = true }
                },
                Cameras = new[] { new ShanyuEngine.Rendering.RenderCamera { Name = "MainCamera", IsMain = true } },
                Lights = new[] { new ShanyuEngine.Rendering.RenderLight { Name = "Sun", LightType = "Directional", CastsShadows = true } }
            };
            var graph = ShanyuEngine.Rendering.RenderGraph.CreateDefaultDeferred("RuntimeFrame");
            var device = new ShanyuEngine.Rendering.NullRenderDevice();

            var stats = device.Render(scene, graph, 1f / 60f);

            Assert.AreEqual(2, stats.MeshCount);
            Assert.IsTrue(stats.RenderGraphPassCount >= 5);
            Assert.AreEqual(0, stats.RenderGraphErrorCount);
        }
    }
}
