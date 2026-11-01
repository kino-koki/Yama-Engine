using System;
using System.IO;
using System.Numerics;
using System.Linq;
using System.Collections.Generic;
using YamaEngine.Assets;
using YamaEngine.Modeling;
using YamaEngine.RHI;
using YamaEngine.Rendering;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests
{
    public static class RenderTests
    {
        public static void TestRenderSceneBuilderCreatesStableSnapshot(string tempRoot)
        {
            var projectPath = Path.Combine(tempRoot, "RenderSnapshotProject");
            Directory.CreateDirectory(projectPath);

            var db = new AssetDatabase(projectPath);
            var cube = db.CreateObject("RenderableCube", "Assets/Meshes/Cube.obj", new Vector3(3, 2, 1));
            cube.MaterialAssetPath = "Assets/Materials/Copper.shmat";
            cube.BaseColor = new Vector3(0.9f, 0.4f, 0.2f);
            cube.Metallic = 1f;
            cube.Roughness = 0.25f;
            cube.Scale = new Vector3(2, 2, 2);
            cube.CastShadow = true;
            cube.ReceiveShadow = false;

            var hidden = db.CreateObject("HiddenCube", "Assets/Meshes/Hidden.obj", new Vector3(0, 0, 0));
            hidden.IsVisible = false;

            var camera = db.CreateCamera(new Vector3(0, 2, -6), true);
            var light = db.CreateLight("Directional", new Vector3(0, 5, 0), new Vector3(1, 0.95f, 0.9f));

            var scene = RenderSceneBuilder.FromAssetDatabase(db, "Main");

            AssertEqual("Main", scene.Name, "Render scene snapshot should keep scene name.");
            AssertEqual(1, scene.Meshes.Count, "Render scene should include only visible renderable meshes.");
            AssertEqual(1, scene.Cameras.Count, "Render scene should include camera components.");
            AssertEqual(1, scene.Lights.Count, "Render scene should include light components.");

            var mesh = scene.Meshes[0];
            AssertEqual("RenderableCube", mesh.Name, "Render mesh should keep source object name.");
            AssertEqual("Assets/Meshes/Cube.obj", mesh.MeshAssetPath, "Render mesh should keep mesh asset path.");
            AssertEqual("Assets/Materials/Copper.shmat", mesh.Material.AssetPath, "Render material should keep material asset path.");
            AssertEqual(new Vector3(0.9f, 0.4f, 0.2f), mesh.Material.BaseColor, "Render material should keep base color.");
            AssertEqual(1f, mesh.Material.Metallic, "Render material should keep metallic.");
            AssertEqual(0.25f, mesh.Material.Roughness, "Render material should keep roughness.");
            AssertTrue(mesh.WorldMatrix.M41 == 3f && mesh.WorldMatrix.M42 == 2f && mesh.WorldMatrix.M43 == 1f, "Render mesh world matrix should contain object translation.");
            AssertTrue(mesh.CastShadow, "Render mesh should keep CastShadow flag.");
            AssertTrue(!mesh.ReceiveShadow, "Render mesh should keep ReceiveShadow flag.");

            AssertEqual(1, scene.Stats.MeshCount, "Snapshot stats should count visible meshes.");
            AssertEqual(1, scene.Stats.VisibleMeshCount, "Snapshot stats should count visible meshes.");
            AssertEqual(1, scene.Stats.CameraCount, "Snapshot stats should count cameras.");
            AssertEqual(1, scene.Stats.LightCount, "Snapshot stats should count lights.");
        }

        public static void TestNullRenderDeviceReturnsFrameStats(string tempRoot)
        {
            var projectPath = Path.Combine(tempRoot, "NullRenderProject");
            Directory.CreateDirectory(projectPath);

            var db = new AssetDatabase(projectPath);
            db.CreateObject("CubeA", "Assets/Meshes/A.obj", new Vector3(0, 0, 0));
            db.CreateObject("CubeB", "Assets/Meshes/B.obj", new Vector3(1, 0, 0));
            db.CreateCamera(new Vector3(0, 2, -5), true);

            var scene = RenderSceneBuilder.FromAssetDatabase(db, "Main");
            IRenderDevice device = new NullRenderDevice("Test Null Device");
            var stats = device.Render(scene, 1f / 60f);

            AssertEqual("Test Null Device", device.Name, "Render device name should be exposed.");
            AssertTrue(device.Capabilities.SupportsPbrMaterials, "Null render device should advertise PBR material support for pipeline validation.");
            AssertEqual(2, stats.MeshCount, "Null render stats should count meshes.");
            AssertEqual(2, stats.VisibleMeshCount, "Null render stats should count visible meshes.");
            AssertEqual(2, stats.DrawCallCount, "Null renderer should use one draw call per visible mesh.");
            AssertEqual(1, stats.CameraCount, "Null render stats should count cameras.");
            AssertTrue(stats.FrameTimeMilliseconds >= 0f, "Null render stats should expose non-negative frame time.");
        }

        public static void TestRenderGraphCompilesPassDependencies()
        {
            var graph = new RenderGraph("DeferredFrame");
            var depth = graph.CreateResource(RenderGraphResourceDesc.Texture2D("SceneDepth", RHIFormat.D32_Float, 1920, 1080,
                flags: RenderGraphResourceFlags.DepthStencil | RenderGraphResourceFlags.ShaderResource));
            var geometrybuffer = graph.CreateResource(RenderGraphResourceDesc.Texture2D("GeometryBufferA", RHIFormat.R16G16B16A16_Float, 1920, 1080));
            var lit = graph.CreateResource(RenderGraphResourceDesc.Texture2D("LitScene", RHIFormat.R16G16B16A16_Float, 1920, 1080));
            var swapchain = graph.CreateResource(RenderGraphResourceDesc.Texture2D("BackBuffer", RHIFormat.R8G8B8A8_UNorm, 1920, 1080,
                flags: RenderGraphResourceFlags.Imported | RenderGraphResourceFlags.RenderTarget | RenderGraphResourceFlags.ShaderResource));

            graph.AddPass("Lighting", RenderGraphPassType.Compute)
                .Read(geometrybuffer)
                .Read(depth)
                .Write(lit);
            graph.AddPass("Geometry", RenderGraphPassType.Render)
                .Write(depth)
                .Write(geometrybuffer);
            graph.AddPass("Tonemap", RenderGraphPassType.Compute)
                .Read(lit)
                .Write(swapchain)
                .SetHasSideEffects();

            var plan = graph.Compile();

            AssertEqual(3, plan.Passes.Count, "RenderGraph should compile three passes.");
            AssertEqual("Geometry", plan.Passes[0].Pass.Name, "Geometry must run before passes that read the GeometryBuffer.");
            AssertEqual("Lighting", plan.Passes[1].Pass.Name, "Lighting must run after Geometry.");
            AssertEqual("Tonemap", plan.Passes[2].Pass.Name, "Tonemap must run after Lighting.");
            AssertTrue(plan.IsValid, "A legal acyclic render graph should compile as valid.");
        }

        public static void TestRenderGraphDetectsCyclicResourceDependencies()
        {
            var graph = new RenderGraph("BrokenFrame");
            var a = graph.CreateResource(RenderGraphResourceDesc.Texture2D("A", RHIFormat.R8G8B8A8_UNorm, 1, 1));
            var b = graph.CreateResource(RenderGraphResourceDesc.Texture2D("B", RHIFormat.R8G8B8A8_UNorm, 1, 1));

            graph.AddPass("PassA", RenderGraphPassType.Compute).Read(b).Write(a);
            graph.AddPass("PassB", RenderGraphPassType.Compute).Read(a).Write(b);

            var plan = graph.Compile();

            AssertTrue(!plan.IsValid, "RenderGraph should reject cyclic pass dependencies.");
            AssertTrue(plan.Errors.Any(e => e.Contains("循环", StringComparison.OrdinalIgnoreCase)), "RenderGraph should report a cycle error.");
        }

        public static void TestRenderGraphReportsResourceLifetimesAndStats()
        {
            var graph = RenderGraph.CreateDefaultDeferredGraph(1920, 1080);
            var plan = graph.Compile();

            AssertTrue(plan.IsValid, "Default deferred graph should compile.");
            AssertTrue(plan.Passes.Count >= 4, "Default deferred graph should include shadow, geometry, lighting and post passes.");
            AssertTrue(plan.Lifetimes.Any(l => l.FirstPass >= 0 && l.LastPass >= l.FirstPass), "Resources should expose a valid lifetime.");
            AssertTrue(plan.Stats.TransientResourceCount > 0, "Frame graph should count transient resources.");
            AssertTrue(plan.Stats.EstimatedMemoryBytes > 0, "Frame graph should estimate memory.");
        }

        public static void TestNullRenderDeviceExecutesCompiledRenderGraph()
        {
            var scene = new RenderSceneSnapshot
            {
                Name = "GraphScene",
                Meshes = new[]
                {
                    new RenderMesh { Name = "MeshA", MeshAssetPath = "Assets/Meshes/A.shmesh", IsVisible = true },
                    new RenderMesh { Name = "MeshB", MeshAssetPath = "Assets/Meshes/B.shmesh", IsVisible = true }
                },
                Cameras = new[] { new RenderCamera { Name = "MainCamera", IsMain = true } },
                Lights = new[] { new RenderLight { Name = "Sun", LightType = "Directional", CastsShadows = true } }
            };
            var graph = RenderGraph.CreateDefaultDeferredGraph(1920, 1080);
            var device = new NullRenderDevice();

            var stats = device.Render(scene, graph, 1f / 60f);

            AssertEqual(2, stats.MeshCount, "Graph render stats should still count scene meshes.");
            AssertTrue(stats.RenderGraphPassCount >= 4, "Graph render stats should include executed pass count.");
            AssertTrue(stats.TransientResourceCount > 0, "Graph render stats should expose transient resource count.");
            AssertTrue(stats.EstimatedRenderGraphMemoryBytes > 0, "Graph render stats should expose graph memory estimate.");
            AssertEqual(0, stats.RenderGraphErrorCount, "Valid default graph should have no graph errors.");
        }

        public static void TestHzbOcclusionBufferRejectsHiddenBounds()
        {
            var hzb = new HzbOcclusionBuffer(4, 4);
            hzb.Clear(1.0f);
            hzb.SetDepth(1, 1, 0.20f);
            hzb.SetDepth(2, 1, 0.20f);
            hzb.SetDepth(1, 2, 0.20f);
            hzb.SetDepth(2, 2, 0.20f);
            hzb.BuildMips();

            var hidden = hzb.IsOccluded(new Vector2(0.25f, 0.25f), new Vector2(0.75f, 0.75f), 0.80f);
            var visible = hzb.IsOccluded(new Vector2(0.25f, 0.25f), new Vector2(0.75f, 0.75f), 0.10f);

            AssertTrue(hidden, "HZB should reject bounds behind existing depth.");
            AssertTrue(!visible, "HZB should keep bounds in front of existing depth.");
            AssertTrue(hzb.MipCount >= 3, "HZB should build a mip pyramid.");
        }

        public static void TestGpuDrivenDrawListBuildsVisibleCommands()
        {
            var hzb = new HzbOcclusionBuffer(4, 4);
            hzb.Clear(1.0f);
            hzb.SetDepth(1, 1, 0.20f);
            hzb.SetDepth(2, 1, 0.20f);
            hzb.SetDepth(1, 2, 0.20f);
            hzb.SetDepth(2, 2, 0.20f);
            hzb.BuildMips();

            var candidates = new[]
            {
                new GpuDrawCandidate("VisibleNear", "Assets/Meshes/Near.shmesh", "Assets/Materials/M.shmat")
                {
                    ScreenMin = new Vector2(0.25f, 0.25f),
                    ScreenMax = new Vector2(0.75f, 0.75f),
                    Depth = 0.10f,
                    SortKey = 10
                },
                new GpuDrawCandidate("HiddenFar", "Assets/Meshes/Far.shmesh", "Assets/Materials/M.shmat")
                {
                    ScreenMin = new Vector2(0.25f, 0.25f),
                    ScreenMax = new Vector2(0.75f, 0.75f),
                    Depth = 0.80f,
                    SortKey = 20
                }
            };

            var list = GpuDrivenDrawListBuilder.Build(candidates, hzb);

            AssertEqual(1, list.Commands.Count, "GPU driven draw list should only contain visible commands.");
            AssertEqual("VisibleNear", list.Commands[0].Name, "Visible command should be preserved.");
            AssertEqual(1, list.OccludedCount, "Draw list should report occluded candidate count.");
        }

        public static void TestSparseShadowAtlasSchedulerAllocatesPagesByLightPriority()
        {
            var scheduler = new SparseShadowAtlasScheduler(maxPhysicalPages: 4);
            var requests = new[]
            {
                new SparseShadowAtlasRequest("Sun", 3, 10.0f),
                new SparseShadowAtlasRequest("SmallLamp", 2, 1.0f),
                new SparseShadowAtlasRequest("HeroSpot", 2, 5.0f)
            };

            var plan = scheduler.Schedule(requests);

            AssertEqual(4, plan.AllocatedPages.Count, "Scheduler should respect physical page budget.");
            AssertTrue(plan.AllocatedPages.Count(p => p.LightName == "Sun") == 3, "Highest priority light should receive requested pages first.");
            AssertTrue(plan.AllocatedPages.Any(p => p.LightName == "HeroSpot"), "Second priority light should receive remaining page.");
            AssertEqual(3, plan.EvictedPageCount, "Scheduler should report pages that could not be allocated.");
        }

        public static void TestAdvancedVisibilityWritesFrameStats()
        {
            var drawList = new GpuDrivenDrawList
            {
                InputCount = 8,
                OccludedCount = 3,
                Commands = new[]
                {
                    new GpuDrawCommand { Name = "A", InstanceCount = 1 },
                    new GpuDrawCommand { Name = "B", InstanceCount = 4 }
                }
            };
            var shadowPlan = new SparseShadowAtlasPlan
            {
                PhysicalPageBudget = 8,
                RequestedPageCount = 5,
                AllocatedPages = new[]
                {
                    new VSMAllocatedPage("Sun", 0, 10),
                    new VSMAllocatedPage("Sun", 1, 10),
                    new VSMAllocatedPage("Spot", 2, 4)
                }
            };
            var stats = new FrameStats();

            AdvancedVisibilityStats.WriteTo(stats, drawList, shadowPlan);

            AssertEqual(8, stats.VisibilityCandidateCount, "FrameStats should track visibility input count.");
            AssertEqual(2, stats.GpuVisibleDrawCount, "FrameStats should track visible GPU draw commands.");
            AssertEqual(3, stats.HzbOccludedCount, "FrameStats should track HZB occluded candidates.");
            AssertEqual(3, stats.SparseShadowAtlasAllocatedPages, "FrameStats should track VSM allocated pages.");
            AssertEqual(2, stats.SparseShadowAtlasEvictedPages, "FrameStats should track VSM evicted pages.");
        }

        public static void TestVirtualGeometryBuilderCreatesMeshClusters()
        {
            var mesh = EditableMesh.CreateCube("ClusterCube", 2f);

            var asset = VirtualGeometryBuilder.Build(mesh, "Assets/Meshes/ClusterCube.shmesh", maxTrianglesPerCluster: 4);

            AssertEqual("Assets/Meshes/ClusterCube.shmesh", asset.MeshAssetPath, "Virtual geometry asset should keep source mesh path.");
            AssertEqual(3, asset.Clusters.Count(c => c.LodLevel == 0), "Cube with 12 triangles and 4 triangles per cluster should create three LOD0 clusters.");
            AssertTrue(asset.Clusters.All(c => c.TriangleCount <= 4 || c.LodLevel > 0), "LOD0 clusters should respect max triangle count.");
            AssertTrue(asset.Clusters.All(c => c.Bounds.Radius > 0f), "Every cluster should expose a positive bounding sphere.");
            AssertTrue(asset.Clusters.Any(c => c.LodLevel == 1), "Builder should create at least one lower-detail parent cluster.");
        }

        public static void TestVirtualGeometryLodSelectorChoosesLowerDetailAtDistance()
        {
            var asset = VirtualGeometryBuilder.Build(EditableMesh.CreateCube("LodCube", 2f), "Assets/Meshes/LodCube.shmesh", maxTrianglesPerCluster: 4);

            var near = VirtualGeometryLodSelector.Select(asset, cameraDistance: 2f, errorThresholdPixels: 0.75f, viewportHeight: 1080);
            var far = VirtualGeometryLodSelector.Select(asset, cameraDistance: 5000f, errorThresholdPixels: 0.75f, viewportHeight: 1080);

            AssertTrue(near.SelectedClusters.All(c => c.LodLevel == 0), "Near virtual geometry should keep fine clusters.");
            AssertTrue(far.SelectedClusters.Any(c => c.LodLevel == 1), "Far virtual geometry should choose lower-detail cluster.");
            AssertTrue(far.EstimatedTriangleCount < near.EstimatedTriangleCount, "Far LOD should reduce triangle count.");
        }

        public static void TestVirtualGeometryDrawListUsesHzbOcclusion()
        {
            var asset = VirtualGeometryBuilder.Build(EditableMesh.CreateCube("DrawCube", 2f), "Assets/Meshes/DrawCube.shmesh", maxTrianglesPerCluster: 4);
            var selection = VirtualGeometryLodSelector.Select(asset, cameraDistance: 2f, errorThresholdPixels: 0.75f, viewportHeight: 1080);
            var hzb = new HzbOcclusionBuffer(4, 4);
            hzb.Clear(1.0f);
            hzb.SetDepth(1, 1, 0.20f);
            hzb.SetDepth(2, 1, 0.20f);
            hzb.SetDepth(1, 2, 0.20f);
            hzb.SetDepth(2, 2, 0.20f);
            hzb.BuildMips();

            var drawList = VirtualGeometryDrawListBuilder.Build(selection, hzb, "Assets/Materials/Default.shmat");

            AssertTrue(drawList.InputClusterCount >= 3, "Cluster draw list should inspect selected clusters.");
            AssertTrue(drawList.VisibleClusterCount < drawList.InputClusterCount, "Cluster draw list should cull at least one occluded cluster.");
            AssertTrue(drawList.Commands.All(c => c.Name.StartsWith("Cluster:", StringComparison.Ordinal)), "Cluster draw commands should identify cluster source.");
        }
    }
}
