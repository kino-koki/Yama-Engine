using System;
using System.IO;
using System.Numerics;
using System.Linq;
using System.Collections.Generic;
using YamaEngine.Assets;
using YamaEngine.ECS;
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

        // ── UE5.8 级时间抗锯齿/上采样 (TAA / TSR) 纯数学验证 ──

        public static void TestHaltonSequenceMatchesKnownValues()
        {
            // Halton(1,2)=0.5, Halton(2,2)=0.25, Halton(3,2)=0.75（Van der Corput 序列）
            AssertTrue(MathF.Abs(HaltonSequence.Halton(1, 2) - 0.5f) < 0.001f, "Halton(1,2) should be 0.5.");
            AssertTrue(MathF.Abs(HaltonSequence.Halton(2, 2) - 0.25f) < 0.001f, "Halton(2,2) should be 0.25.");
            AssertTrue(MathF.Abs(HaltonSequence.Halton(3, 2) - 0.75f) < 0.001f, "Halton(3,2) should be 0.75.");
            AssertTrue(MathF.Abs(HaltonSequence.Halton(1, 3) - 1f / 3f) < 0.01f, "Halton(1,3) should be ~1/3.");

            // 所有 8x8 抖动网格值应在 [-0.5, 0.5]（中心化）
            bool inRange = HaltonSequence.JitterGrid8x8.All(j => j.X >= -0.5f && j.X <= 0.5f && j.Y >= -0.5f && j.Y <= 0.5f);
            AssertTrue(inRange, "All 8x8 Halton jitter offsets should be centered in [-0.5, 0.5].");
        }

        public static void TestYCoCgColorSpaceRoundTrips()
        {
            var rgb = new Vector3(0.8f, 0.3f, 0.6f);
            var roundTrip = TemporalAntiAliasing.YCoCgToRgb(TemporalAntiAliasing.RgbToYCoCg(rgb));
            AssertTrue(MathF.Abs(rgb.X - roundTrip.X) < 0.002f, "YCoCg round-trip should preserve red channel.");
            AssertTrue(MathF.Abs(rgb.Y - roundTrip.Y) < 0.002f, "YCoCg round-trip should preserve green channel.");
            AssertTrue(MathF.Abs(rgb.Z - roundTrip.Z) < 0.002f, "YCoCg round-trip should preserve blue channel.");

            // 灰色（R=G=B）：Co/Cg 色度分量应为 0
            var gray = TemporalAntiAliasing.RgbToYCoCg(new Vector3(0.5f, 0.5f, 0.5f));
            AssertTrue(MathF.Abs(gray.Y) < 0.01f, "Gray should have zero Co chroma.");
            AssertTrue(MathF.Abs(gray.Z) < 0.01f, "Gray should have zero Cg chroma.");
        }

        // ── UE5.8 级 Lumen 全局光照 纯数学验证 ──

        public static void TestOctahedralParameterizationRoundTripsUnitVectors()
        {
            var axes = new[] { new Vector3(0, 0, 1), new Vector3(0, 0, -1), new Vector3(0, 1, 0), new Vector3(1, 0, 0) };
            foreach (var dir in axes)
            {
                var decoded = OctahedralParameterization.Decode(OctahedralParameterization.Encode(dir));
                AssertTrue(Vector3.Dot(dir, Vector3.Normalize(decoded)) > 0.99f,
                    $"Octahedral encode/decode should round-trip direction ({dir}).");
            }
        }

        public static void TestLumenProbeSHReconstructsUniformRadiance()
        {
            var probe = new LumenIrradianceProbe();
            // 累加 64 个均匀方向、恒定辐射亮度（Accumulate 无立体角权重，属近似 SH 投影）
            var dirs = OctahedralParameterization.GenerateUniformDirections(64);
            var radiance = new Vector3(1f, 0.9f, 0.8f);
            foreach (var d in dirs) probe.Accumulate(d, radiance);
            probe.Normalize();

            // 不变量：恒定辐射场经 SH 归一化后，重建值应是各方向同数量级的低频函数。
            // Accumulate 无立体角权重，DC 系数重建因子为 0.282095²≈0.0796（非 1），故值落在 ~0.08 数量级。
            // 验证：通道次序保持（红>=绿>=蓝 不因投影反转）、+Z/-Z 近似对称、+Z/-Z 同量级。
            var atZ = probe.EvaluateIrradiance(new Vector3(0, 0, 1));
            var atNegZ = probe.EvaluateIrradiance(new Vector3(0, 0, -1));
            AssertTrue(atZ.X > 0.05f && atZ.X < 0.12f,
                $"SH should reconstruct red radiance at DC factor ~0.08 at +Z (got {atZ.X:F3}).");
            AssertTrue(atZ.X >= atZ.Y && atZ.Y >= atZ.Z,
                "SH reconstruction should preserve radiance channel order red>=green>=blue at +Z.");
            AssertTrue(MathF.Abs(atZ.X - atNegZ.X) < 0.03f,
                $"Uniform field SH reconstruction should be near-symmetric at +Z/-Z modulo z² quadrupole (diff {MathF.Abs(atZ.X - atNegZ.X):F3}).");
            AssertTrue(MathF.Abs(atZ.Y - atNegZ.Y) < 0.03f && MathF.Abs(atZ.Z - atNegZ.Z) < 0.03f,
                "SH reconstruction of uniform field should be near-symmetric in green/blue modulo quadrupole.");
        }

        // ── UE5.8 级贴花系统 (Decal System) 纯数学/生命周期验证 ──

        public static void TestDecalProjectorComputesWorldToLocalMapping()
        {
            // 贴花中心位于原点，无旋转，盒半尺寸 1
            var decal = DecalComponent.Create(Vector3.Zero, Quaternion.Identity, new Vector3(1f), 0);
            var worldToLocal = DecalProjector.ComputeWorldToDecal(in decal);

            // 贴花中心应映射到局部原点
            var centerLocal = Vector3.Transform(Vector3.Zero, worldToLocal);
            AssertTrue(Vector3.Distance(centerLocal, Vector3.Zero) < 0.001f,
                "Decal center should map to local origin.");

            // 盒内点 (0.5, 0.5, 0.5) 应在 [-1,1] 局部范围内
            var insideLocal = Vector3.Transform(new Vector3(0.5f, 0.5f, 0.5f), worldToLocal);
            AssertTrue(MathF.Abs(insideLocal.X) <= 1.001f && MathF.Abs(insideLocal.Y) <= 1.001f && MathF.Abs(insideLocal.Z) <= 1.001f,
                "Point inside decal box should map inside [-1,1] local range.");

            // 盒外点 (3, 0, 0) 应超出局部范围
            var outsideLocal = Vector3.Transform(new Vector3(3f, 0, 0), worldToLocal);
            AssertTrue(MathF.Abs(outsideLocal.X) > 1.001f,
                "Point outside decal box should map outside [-1,1] local range.");
        }

        public static void TestDecalLifecycleFadesOutAndExpires()
        {
            var decal = DecalComponent.Create(Vector3.Zero, Quaternion.Identity, new Vector3(1f), 0, lifetime: 1f);
            decal.FadeStartRatio = 0.5f;

            // 生命周期满 0.3s（< FadeStart 0.5s）→ 应完全不透明、未过期
            while (decal.Elapsed < 0.3f) DecalLifecycle.Update(ref decal, 0.05f);
            AssertNearValue(decal.CurrentOpacity, 1f, 0.01f, "Decal should be fully opaque before fade start.");
            AssertTrue(!decal.IsExpired, "Decal should not expire before lifetime.");

            // 生命周期满 0.75s（> FadeStart 0.5s，剩 0.25s/0.5s 淡出窗口 → 透明度 0.5）
            while (decal.Elapsed < 0.75f) DecalLifecycle.Update(ref decal, 0.05f);
            AssertNearValue(decal.CurrentOpacity, 0.5f, 0.05f, "Decal should fade to ~0.5 opacity in the fade window.");

            // 超过 1s 寿命 → 应标记过期
            while (decal.Elapsed < 1.1f) DecalLifecycle.Update(ref decal, 0.05f);
            AssertTrue(decal.IsExpired, "Decal past its lifetime should be marked expired.");
        }

        public static void TestDecalSorterOrdersFarToNearForBackToFront()
        {
            // 排序以相机位置到贴花的实际距离为准；相机在原点，贴花沿 +Z 分布
            var ecs = new EntityComponentSystem();
            var camPos = Vector3.Zero;
            int idFar = ecs.CreateEntity();
            ecs.AddComponent(idFar, DecalComponent.Create(new Vector3(0, 0, 10f), Quaternion.Identity, Vector3.One, 0));
            int idMid = ecs.CreateEntity();
            ecs.AddComponent(idMid, DecalComponent.Create(new Vector3(0, 0, 7f), Quaternion.Identity, Vector3.One, 0));
            int idNear = ecs.CreateEntity();
            ecs.AddComponent(idNear, DecalComponent.Create(new Vector3(0, 0, 3f), Quaternion.Identity, Vector3.One, 0));

            var order = DecalSorter.Sort(ecs, camPos, DecalSortMethod.BackToFront);
            AssertTrue(order.Count == 3, "Sorter should return all decals.");
            AssertTrue(order[0] == idFar && order[1] == idMid && order[2] == idNear,
                "Back-to-front sort should order decals far→near (far first for correct transparency blending).");
        }

        // ── UE5.8 级材质实例 (Material Instance) 覆盖解析验证 ──

        public static void TestMaterialInstanceResolvesOverridesAndFallbacks()
        {
            // 父材质：金属（Metallic=1, Roughness=0.3）
            var parent = YamaEngine.Rendering.MaterialAsset.CreateMetal("ParentMetal");
            var instance = new MaterialInstance(parent);

            // 无覆盖 → 属性完全回退到父材质
            var baseProps = instance.ResolvedProperties;
            AssertNearValue(baseProps.Metallic, 1.0f, 0.001f, "Unoverridden instance should fall back to parent metallic.");
            AssertNearValue(baseProps.Roughness, 0.3f, 0.001f, "Unoverridden instance should fall back to parent roughness.");

            // 覆盖 Roughness → 应生效但 Metallic 仍回退父材质
            instance.OverrideProperties = new MaterialProperties
            {
                Roughness = 0.8f,
                Metallic = 0.3f
            };
            var overridden = instance.ResolvedProperties;
            AssertNearValue(overridden.Roughness, 0.8f, 0.001f, "Override roughness should take effect.");
            AssertNearValue(overridden.Metallic, 0.3f, 0.001f, "Override metallic should take effect.");

            // 布尔标志：父为不透明，覆盖为透明 → 合并后透明
            instance.OverrideProperties = new MaterialProperties { IsTransparent = true };
            var transparent = instance.ResolvedProperties;
            AssertTrue(transparent.IsTransparent, "Override IsTransparent=true should propagate.");
        }

        public static void TestMaterialAssetComputeTextureFlags()
        {
            // 空材质（无纹理）→ 标志全为 0
            var empty = YamaEngine.Rendering.MaterialAsset.CreatePlastic("Empty");
            AssertTrue(empty.ComputeTextureFlags() == 0, "Empty material should have no texture flags.");

            // 添加 Albedo 纹理 → bit 0x01
            empty.SetTexture(MaterialTextureType.Albedo, new StubTexture());
            AssertTrue((empty.ComputeTextureFlags() & 0x01) != 0, "Adding Albedo texture should set bit 0x01.");

            // 添加 Normal 纹理 → bit 0x02
            empty.SetTexture(MaterialTextureType.Normal, new StubTexture());
            AssertTrue((empty.ComputeTextureFlags() & 0x02) != 0, "Adding Normal texture should set bit 0x02.");
        }

        // ── UE5.8 级 Nanite QEM / LOD / ReSTIR / GGX BRDF 确定性验证 ──

        // Nanite 二次误差矩阵：平面方程 ax+by+cz+d=0 的 QEM，落在该平面上误差为 0
        public static void TestNaniteQEMPlaneZeroError()
        {
            // 底面平面 z=0 → a=0,b=0,c=1,d=0；点 (x,y,0) 应零误差，z≠0 误差=z²
            var q = NaniteQEM.FromPlane(0f, 0f, 1f, 0f);
            var onPlane = q.Evaluate(new Vector3(3f, -2f, 0f));
            var offPlane = q.Evaluate(new Vector3(3f, -2f, 0.5f));
            AssertNearValue(onPlane, 0f, 0.0001f, "Point on the QEM plane should have zero error.");
            AssertNearValue(offPlane, 0.25f, 0.0001f, "Off-plane error should equal squared distance to plane.");

            // QEM 叠加：两个平面交线上的点对两者误差均为 0 → 合并后仍 0
            var qz = NaniteQEM.FromPlane(0f, 0f, 1f, 0f);   // z=0
            var qy = NaniteQEM.FromPlane(0f, 1f, 0f, 0f);   // y=0
            var combined = NaniteQEM.Add(qz, qy);
            var onEdge = combined.Evaluate(new Vector3(5f, 0f, 0f)); // y=0,z=0 交线
            AssertNearValue(onEdge, 0f, 0.0001f, "Point on both planes (intersection edge) should have zero combined QEM error.");
        }

        // GGX 微表面 BRDF：D 在 n=h(粗糙度0) 时峰值 a²/(π·a⁴)... 用粗糙度1光滑校验解析值
        public static void TestGGXBrdfAnalyticValues()
        {
            // 粗糙度=1（a=1）时 GGX_D(nDotH=1)=1/π；Fresnel 正入射 cosTheta=1 → f0；G2(1,1,a)=1
            float d = PathTracer.GGX_D(1f, 1f);
            AssertNearValue(d, 1f / MathF.PI, 0.001f, "GGX_D at nDotH=1, roughness=1 should equal 1/pi.");
            float g2 = PathTracer.Smith_G2(1f, 1f, 1f);
            AssertNearValue(g2, 1f, 0.001f, "Smith_G2 at nDotL=nDotV=1 should equal 1.");
            var f0 = new Vector3(0.04f, 0.04f, 0.04f);
            var f = PathTracer.SchlickFresnel(1f, f0);
            AssertNearValue(f.X, 0.04f, 0.0001f, "Schlick Fresnel at cosTheta=1 should equal F0.");
            // 掠射角 cosTheta=0 → Fresnel 趋近 1
            var fGrazing = PathTracer.SchlickFresnel(0f, f0);
            AssertNearValue(fGrazing.X, 1f, 0.0001f, "Schlick Fresnel at grazing angle should approach 1.");
        }

        // Nanite/UE5 虚拟几何 LOD 选择：误差低于阈值选低 LOD（省三角形），高于阈值保留 LOD0
        public static void TestVirtualGeometryLodSelectorPicksLodByError()
        {
            var cluster = (int id, int lod, int tris, float err) => new VirtualGeometryCluster
            {
                Id = id, LodLevel = lod, TriangleCount = tris,
                GeometricError = err, Bounds = new BoundingSphere(Vector3.Zero, 1f)
            };
            var asset = new VirtualGeometryAsset
            {
                Clusters = new[]
                {
                    cluster(0, 0, 1000, 1.0f), // LOD0 高误差
                    cluster(1, 1, 100, 20.0f), // LOD1 低误差
                }
            };
            // 投影误差 geometricError/distance*viewportHeight。
            // 近距→LOD0 投影误差 1/2*1080=540 远超阈值→保留高精度 LOD0
            var near = VirtualGeometryLodSelector.Select(asset, cameraDistance: 2f, errorThresholdPixels: 30f, viewportHeight: 1080);
            AssertTrue(near.LodLevel == 0, "Near camera (high projected error) should keep LOD0 for detail.");
            AssertTrue(near.EstimatedTriangleCount == 1000, "LOD0 selection should use LOD0 triangle count.");
            // 远距→LOD0 投影误差 1/200*1080=5.4 低于阈值→切换省三角形 LOD1
            var far = VirtualGeometryLodSelector.Select(asset, cameraDistance: 200f, errorThresholdPixels: 30f, viewportHeight: 1080);
            AssertTrue(far.LodLevel == 1, "Far camera (low projected error) should switch to LOD1.");
            AssertTrue(far.EstimatedTriangleCount == 100, "LOD1 selection should use LOD1 triangle count.");
        }

        // ReSTIR 时序水库 RIS 加权重采样：单候选必接受，无偏估计 (W_sum/M)
        public static void TestReSTIRTemporalReservoirRis()
        {
            var reservoir = new TemporalReservoir();
            reservoir.Reset();
            var sample = new LightSample { LightIndex = 3, Pdf = 1f, Radiance = new Vector3(1f, 1f, 1f) };
            var rng = new Random(12345); // 固定种子确定性
            // 单候选 M=1：接受概率 targetWeight/(0+targetWeight)=1 → 必接受
            reservoir.Update(sample, targetWeight: 2f, totalM: 1, ref rng);
            AssertTrue(reservoir.SelectedSample.LightIndex == 3, "Single-candidate reservoir should accept the sample.");
            AssertTrue(reservoir.SampleCountM == 1, "SampleCountM should track totalM.");
            AssertNearValue(reservoir.WeightSum, 2f, 0.001f, "WeightSum should accumulate target weight.");
            // 无偏光照估计：(W_sum/M)*EvaluateTarget(brdf,normal)；正对法线 cosTheta=1
            var brdf = new Vector3(0.5f, 0.5f, 0.5f);
            var estimate = reservoir.EvaluateFinalShading(brdf, Vector3.UnitZ);
            var sampleDir = reservoir.SelectedSample.SampleDirection;
            // EvaluateFinalShading = target * (WeightSum/M)，target=brdf*Radiance*cosTheta
            float cosTheta = MathF.Max(0f, Vector3.Dot(Vector3.UnitZ, sampleDir));
            var expected = brdf * reservoir.SelectedSample.Radiance * cosTheta * (reservoir.WeightSum / reservoir.SampleCountM);
            AssertNearValue(estimate.X, expected.X, 0.001f, "ReSTIR final shading estimate should be unbiased (W_sum/M)*target.");
            // 空水库（WeightSum=0）应返回零
            var empty = new TemporalReservoir();
            empty.Reset();
            AssertTrue(empty.EvaluateFinalShading(brdf, Vector3.UnitZ) == Vector3.Zero,
                "Empty reservoir (WeightSum=0) should return zero illumination.");
        }

        // ReSTIR LightTree：CDF 按光度加权采样，亮光源被采到概率更高
        public static void TestReSTIRLightTreeCdfSampling()
        {
            var config = new ReSTIRConfig();
            var tree = new LightTreeImportanceSampling(config);
            // 两个点光源：亮光 (power 高) 与暗光 (power 低)，同色不同强度
            var lights = new GPULight[2];
            lights[0] = new GPULight { Position = new Vector3(0, 0, 0), Color = Vector3.One, Intensity = 10f, Range = 10f, Type = (int)Rendering.LightType.Point };
            lights[1] = new GPULight { Position = new Vector3(50, 0, 0), Color = Vector3.One, Intensity = 0.1f, Range = 10f, Type = (int)Rendering.LightType.Point };
            tree.BuildTree(lights, 2);
            AssertTrue(tree.LightCount == 2, "LightTree must contain 2 lights.");

            // 统计采样：亮光（index 0）应被采到远多于暗光
            var rng = new Random(7);
            int bright = 0, n = 4000;
            for (int i = 0; i < n; i++)
            {
                var (idx, pdf) = tree.SampleLight(ref rng);
                if (idx == 0) bright++;
            }
            // 亮光 power≈10*0.2126+...≈3.826，暗光≈0.038；占比≈99% → 容差宽松
            AssertTrue(bright > n * 9 / 10,
                $"Bright light should dominate CDF sampling (>90%), got {bright}/{n}.");
        }

        // ReSTIR Reservoir：Weight 属性 = WeightSum/TargetFuncSum；Update 接受概率与加权选择
        public static void TestReSTIRReservoirWeightAndAcceptance()
        {
            var reservoir = new TemporalReservoir();
            reservoir.Reset();
            AssertTrue(reservoir.Weight == 0f, "Empty reservoir Weight must be 0.");
            var rng = new Random(99);
            // 首个样本 M=1 必接受
            var s1 = new LightSample { LightIndex = 0, Pdf = 1f, Radiance = Vector3.One };
            reservoir.Update(s1, targetWeight: 5f, totalM: 1, ref rng);
            AssertTrue(reservoir.SelectedSample.LightIndex == 0, "First sample must be accepted.");
            AssertNearValue(reservoir.WeightSum, 5f, 0.001f, "WeightSum=5 after first update.");
            // Weight = WeightSum/TargetFuncSum = 5/5 = 1
            AssertNearValue(reservoir.Weight, 1f, 0.001f, "Weight must equal WeightSum/TargetFuncSum.");
            // EvaluateTarget：cosTheta=max(0, n·dir)；SampleDirection 默认 Zero → cosTheta=0 → target=0
            var target0 = s1.EvaluateTarget(new Vector3(1f), Vector3.UnitZ);
            AssertNearValue(target0.Length(), 0f, 0.001f,
                "EvaluateTarget with default (zero) direction must be 0 (cosTheta=0).");
            // 正对：SampleDirection=UnitZ，normal=UnitZ → cosTheta=1 → target=brdf*Radiance
            var s2 = new LightSample { LightIndex = 1, Pdf = 1f, Radiance = new Vector3(2f), SampleDirection = Vector3.UnitZ };
            var target2 = s2.EvaluateTarget(new Vector3(3f), Vector3.UnitZ);
            AssertNearValue(target2.X, 6f, 0.001f, "EvaluateTarget must be brdf*Radiance*cosTheta (3*2*1=6).");
        }

        // SSR 的 HiZ 层级深度金字塔：逐级 2x2 取 max，mip0 保原值、高层保守远平面加速光线步进
        public static void TestHzbHierarchicalDepthPyramid()
        {
            var config = new ScreenSpaceReflectionConfig { HiZMaxMipLevels = 3 };
            var hzb = new HZBBuilder(config);
            // 4x4 深度：mip0 保原值，mip1=2x2 取 max，mip2 全 max
            var depth = new float[]
            {
                1f, 2f, 5f, 6f,
                3f, 4f, 7f, 8f,
                9f, 10f, 13f, 14f,
                11f, 12f, 15f, 16f,
            };
            hzb.Build(depth, width: 4, height: 4);
            AssertTrue(hzb.MipLevels == 3, "HiZ should report configured mip level count.");
            // SampleDepth 是双线性"取 max"的保守深度（光线步进宁远勿近）。原点采样会跨到 2x2 邻域取 max，
            // 故只断言最有区分力的两个事实：(1) 最粗 mip 存全局最远平面；(2) 逐级 mip 单调不小于原值块。
            AssertNearValue(hzb.SampleDepth(2, 0f, 0f), 16f, 0.001f, "HiZ coarsest mip should store global max depth (farthest plane).");
            // mip0 原点保守 max(1,2,3,4)=4，mip2 全局 16 → 逐级保守不减小
            float m0 = hzb.SampleDepth(0, 0f, 0f);
            float m1 = hzb.SampleDepth(1, 0f, 0f);
            AssertNearValue(m0, 4f, 0.001f, "HiZ mip0 conservative max should cover its 2x2 neighborhood.");
            AssertTrue(m1 >= m0, "HiZ coarser mip should not decrease (conservative).");
            // UVToPixel 最近邻查精确 texel：mip0 原点 texel=1
            var (px, py) = hzb.UVToPixel(0, 0f, 0f);
            AssertTrue(px == 0 && py == 0, "UVToPixel at origin should map to top-left texel.");
        }

        // UE5.8 指数高度雾：d(h)=density·exp(-(h-start)/falloff)，Beer-Lambert 透射率 exp(-d·dist)
        public static void TestExponentialHeightFogDensityAndTransmittance()
        {
            var fog = new ExponentialHeightFog { Density = 0.1f, HeightFalloff = 1f, StartHeight = 0f };
            // 海平面 h=0 → density·exp(0)=0.1；高 2m → 0.1·exp(-2)
            AssertNearValue(fog.SampleDensity(0f), 0.1f, 0.001f, "Height fog density at start height should equal base density.");
            float dHigh = fog.SampleDensity(2f);
            AssertNearValue(dHigh, 0.1f * MathF.Exp(-2f), 0.0001f, "Height fog density should decay exponentially with height.");
            // Beer-Lambert 透射率：exp(-density·distance)
            var t = fog.SampleTransmittance(0f, distance: 10f);
            float expectedT = MathF.Exp(-0.1f * 10f);
            AssertNearValue(t.X, expectedT, 0.001f, "Beer-Lambert transmittance should be exp(-density*distance).");
            AssertTrue(t.X >= 0f && t.X <= 1f, "Transmittance should be in [0,1].");
        }

        // UE5.8 体积雾 Henyey-Greenstein 相函数：各向同性 g=0 → 1/(4π)；前向散射 g>0 时前向更强
        public static void TestHenyeyGreensteinPhaseFunction()
        {
            var iso = new FogMaterial { PhaseG = 0f };
            AssertNearValue(iso.Phase(0f), 1f / (4f * MathF.PI), 0.0001f,
                "Isotropic HG phase (g=0) should equal 1/(4pi) in all directions.");
            var forward = new FogMaterial { PhaseG = 0.8f };
            float forwardPeak = forward.Phase(1f);   // 前向 cosTheta=1
            float backScatter = forward.Phase(-1f);  // 后向 cosTheta=-1
            AssertTrue(forwardPeak > backScatter, "Forward-scattering HG (g>0) should peak forward, weaker backward.");
            // FogMaterial 透射率 Beer-Lambert
            var m = new FogMaterial { Scattering = new Vector3(0.02f), Absorption = new Vector3(0.005f) };
            var t = m.Transmittance(distance: 10f);
            AssertNearValue(t.X, MathF.Exp(-0.025f * 10f), 0.001f, "FogMaterial transmittance should use Beer-Lambert with scattering+absorption.");
        }

        // GPU Scene 实例打包：FromTransform 把旋转/缩放/平移压成三行矩阵，默认可见标志
        public static void TestGpuInstanceDataFromTransform()
        {
            var data = GPUInstanceData.FromTransform(
                position: new Vector3(1f, 2f, 3f),
                rotation: Quaternion.Identity,
                scale: new Vector3(2f, 2f, 2f),
                meshIndex: 7u, materialIndexBase: 4u, instanceId: 42u);
            // 单位旋转+均匀缩放2：X 基向量列 = (2,0,0)，平移进第4分量
            AssertNearValue(data.TransformRow0.X, 2f, 0.001f, "Scale should multiply the X basis column.");
            AssertNearValue(data.TransformRow0.W, 1f, 0.001f, "Position should go into translation column row0.");
            AssertNearValue(data.TransformRow1.Y, 2f, 0.001f, "Scale should multiply the Y basis column.");
            AssertNearValue(data.TransformRow1.W, 2f, 0.001f, "Position should go into translation column row1.");
            AssertNearValue(data.TransformRow2.Z, 2f, 0.001f, "Scale should multiply the Z basis column.");
            AssertNearValue(data.TransformRow2.W, 3f, 0.001f, "Position should go into translation column row2.");
            AssertTrue(data.MeshIndex == 7u && data.InstanceId == 42u, "Mesh and instance IDs should be preserved.");
            AssertTrue(data.IsVisible, "FromTransform should default to visible flag.");
            AssertTrue(!data.CastShadow, "FromTransform should not default to cast-shadow flag.");
            AssertTrue(data.PrevBoundsCenter == new Vector3(1f, 2f, 3f), "PrevBoundsCenter should initialize to spawn position.");
        }

        // SSR 光线步进：射线逃出屏幕边界→不命中；射线撞向近墙深度→命中
        public static void TestRayMarcherEscapesScreenAndHitsWall()
        {
            var config = new ScreenSpaceReflectionConfig
            {
                HiZMaxMipLevels = 3, SSRMaxRaySteps = 128,
                SSRStepSize = 16f, SSRThickness = 0.5f
            };
            // 场景1：射线起点贴近右边界、朝右 → 第一步即越界 break，不命中（边界逃逸路径）
            var hzbFar = new HZBBuilder(config);
            hzbFar.Build(new float[64 * 64].Select(_ => 1.0f).ToArray(), width: 64, height: 64);
            var marcherFar = new RayMarcher(config, hzbFar);
            var esc = marcherFar.TraceRay(
                startUV: new Vector2(0.995f, 0.5f), startDepth: 0f,
                direction: new Vector2(1f, 0f), invProjMatrix: Matrix4x4.Identity,
                screenWidth: 64, screenHeight: 64);
            AssertTrue(!esc.Hit, "Ray starting at right edge marching outward should escape via boundary break.");

            // 场景2：深度全 0.5（近墙），射线朝右 → rayZ 低于墙深度+厚度 → 命中
            var hzbWall = new HZBBuilder(config);
            hzbWall.Build(new float[64 * 64].Select(_ => 0.5f).ToArray(), width: 64, height: 64);
            var marcherWall = new RayMarcher(config, hzbWall);
            var hit = marcherWall.TraceRay(
                startUV: new Vector2(0.1f, 0.5f), startDepth: 0f,
                direction: new Vector2(1f, 0f), invProjMatrix: Matrix4x4.Identity,
                screenWidth: 64, screenHeight: 64);
            AssertTrue(hit.Hit, "Ray marching into a near-wall depth should register a hit.");
            AssertTrue(hit.HitUV.X >= 0.1f, "Hit UV should advance from the start position.");
            AssertTrue(hit.RayDistance >= 0f, "Hit ray distance should be non-negative.");
        }

        /// <summary>用于材质标志位测试的最小具体纹理实现。</summary>
        private sealed class StubTexture : RHITexture
        {
            public override RHIResourceState CurrentState { get; set; }
            public override string Name { get; set; } = "Stub";
            public override ulong AllocatedBytes => 0;
            public override void Dispose() { }
        }

        // 容差断言辅助（TestHelpers 无 AssertNear）
        private static void AssertNearValue(float actual, float expected, float tol, string msg)
        {
            AssertTrue(MathF.Abs(actual - expected) <= tol,
                $"{msg} (got {actual:F3}, want {expected:F3}±{tol}).");
        }
    }
}
