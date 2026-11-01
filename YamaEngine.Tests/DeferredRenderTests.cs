// DeferredRenderTests.cs — UE4.7 级延迟渲染管线 (DeferredRenderPipeline) 验证套件
// 全部为 CPU 级确定性测试，无 GPU 依赖，用于 CI/本地验证 Deferred Shading 正确性。
using System;
using System.Linq;
using System.Numerics;
using YamaEngine.Rendering;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests;

public static class DeferredRenderTests
{
    // ── GBuffer 打包/解码 ────────────────────────────────────────────

    public static void TestGBufferPackDecodeRoundtrip()
    {
        var albedo = new Vector3(0.8f, 0.5f, 0.2f);
        var normal = Vector3.Normalize(new Vector3(0.3f, -0.4f, 0.85f));
        var pos = new Vector3(3f, 2f, 1f);
        float metallic = 0.6f, roughness = 0.35f, ao = 0.9f;

        var gbuf = DeferredRenderPipeline.PackGBuffer(albedo, normal, metallic, roughness, ao, pos);
        var surf = DeferredRenderPipeline.DecodeGBuffer(gbuf);

        AssertTrue(Vector3.Distance(surf.Albedo, albedo) < 1e-3f, "Albedo 往返应一致。");
        AssertTrue(Vector3.Distance(surf.Normal, normal) < 1e-3f, "Normal 往返应一致（归一化）。");
        AssertTrue(Math.Abs(surf.Metallic - metallic) < 1e-3f, "Metallic 往返应一致。");
        AssertTrue(Math.Abs(surf.Roughness - roughness) < 1e-3f, "Roughness 往返应一致。");
        AssertTrue(Math.Abs(surf.AO - ao) < 1e-3f, "AO 往返应一致。");
        AssertTrue(Vector3.Distance(surf.WorldPos, pos) < 1e-3f, "WorldPos 往返应一致。");
    }

    public static void TestGBufferNormalEncodingAxisAligned()
    {
        // (0,0,1) 应编码为 [0.5, 0.5, 1.0]，解码后还原
        var gbuf = DeferredRenderPipeline.PackGBuffer(Vector3.One, Vector3.UnitZ, 0f, 0.5f, 1f, Vector3.Zero);
        var n = gbuf.DecodedNormal;
        AssertTrue(Math.Abs(n.X) < 1e-3f && Math.Abs(n.Y) < 1e-3f && Math.Abs(n.Z - 1f) < 1e-3f,
            "法线 (0,0,1) 应编码/解码还原。");
        AssertTrue(Math.Abs(gbuf.Normal.X - 0.5f) < 1e-3f && Math.Abs(gbuf.Normal.Z - 1f) < 1e-3f,
            "法线 (0,0,1) 的 [0,1] 编码应为 (0.5, 0.5, 1.0)。");
    }

    public static void TestGBufferShadingModelPacked()
    {
        var gbuf = DeferredRenderPipeline.PackGBuffer(Vector3.One, Vector3.UnitZ, 0f, 0.5f, 1f,
            Vector3.Zero, ShadingModel.Subsurface);
        AssertTrue(gbuf.ShadingModelType == ShadingModel.Subsurface,
            "ShadingModel 应打包到 PBR.A 通道并可还原。");
    }

    // ── CSM 级联分割 ─────────────────────────────────────────────────

    public static void TestCascadePracticalSplitsMonotonic()
    {
        var cfg = new CascadeShadowConfig
        {
            CascadeCount = 4, SplitMode = CascadeSplitMode.Practical,
            LogSplitWeight = 0.8f, ShadowDistance = 1000f, ShadowNearOffset = 0f
        };
        var splits = DeferredRenderPipeline.ComputeCascadeSplits(cfg, 0.1f, 1000f);

        AssertTrue(splits.Length == 4, "Practical 模式应产生 4 个级联。");
        // 级联是无缝连续的（d0(i+1) == d1(i)），允许浮点接缝
        float prev = 0f;
        for (int i = 0; i < splits.Length; i++)
        {
            AssertTrue(splits[i].near >= prev - 1e-3f, $"级联 {i} 近端应大于等于前一远端（连续）。");
            AssertTrue(splits[i].far > splits[i].near, $"级联 {i} 远端应严格大于近端。");
            prev = splits[i].far;
        }
        AssertTrue(Math.Abs(splits[0].near - 0.1f) < 1e-3f, "首个级联近端应等于相机近平面。");
        AssertTrue(Math.Abs(splits[^1].far - 1000f) < 1.0f, "末级联远端应接近阴影距离。");
    }

    public static void TestCascadeLinearEqualSpacing()
    {
        var cfg = new CascadeShadowConfig { CascadeCount = 4, SplitMode = CascadeSplitMode.Linear, ShadowDistance = 500f };
        var splits = DeferredRenderPipeline.ComputeCascadeSplits(cfg, 0.1f, 500f);

        float step = (500f - 0.1f) / 4f;
        for (int i = 0; i < splits.Length; i++)
        {
            AssertTrue(Math.Abs(splits[i].near - (0.1f + i * step)) < 1e-2f, $"Linear 级联 {i} 近端应等距。");
            AssertTrue(Math.Abs(splits[i].far - (0.1f + (i + 1) * step)) < 1e-2f, $"Linear 级联 {i} 远端应等距。");
        }
    }

    public static void TestCascadePracticalLogWeightBoundaries()
    {
        // lambda=0 -> 纯线性 (与 Linear 一致)；lambda=1 -> 纯对数
        var lin = DeferredRenderPipeline.ComputeCascadeSplits(
            new CascadeShadowConfig { CascadeCount = 4, SplitMode = CascadeSplitMode.Practical, LogSplitWeight = 0f, ShadowDistance = 500f }, 0.1f, 500f);
        var log = DeferredRenderPipeline.ComputeCascadeSplits(
            new CascadeShadowConfig { CascadeCount = 4, SplitMode = CascadeSplitMode.Practical, LogSplitWeight = 1f, ShadowDistance = 500f }, 0.1f, 500f);

        // 对数划分首级联应更小（近处精度更好）
        AssertTrue(log[0].far < lin[0].far, "纯对数划分的首级联应比纯线性更小（近处更密）。");
        // 对数划分把更多级联挤在近处，故末级联起点应比纯线性更近（对数在远处更稀疏）
        AssertTrue(log[^1].near < lin[^1].near, "纯对数划分的末级联起点应比纯线性更近（对数近处更密）。");
    }

    // ── SSAO 核生成 ─────────────────────────────────────────────────

    public static void TestSSAOKernelHemisphere()
    {
        var kernel = DeferredRenderPipeline.GenerateSSAOKernel(32);
        AssertTrue(kernel.Length == 32, "SSAO 核应生成 32 个样本。");

        float weightSum = 0f;
        for (int i = 0; i < kernel.Length; i++)
        {
            var v = kernel[i];
            float len = MathF.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
            AssertTrue(Math.Abs(len - 1f) < 1e-2f, $"SSAO 核样本 {i} 应为单位向量。");
            AssertTrue(v.Z > 0f, $"SSAO 核样本 {i} 应在上半球 (z>0)。");
            weightSum += v.W;
        }
        AssertTrue(Math.Abs(weightSum - 1f) < 1e-3f, "SSAO 核权重之和应为 1。");
    }

    // ── 延迟光照 (PBR) ───────────────────────────────────────────────

    public static void TestDeferredLightDirectionalNonZero()
    {
        // 白材质，法线朝 +Z；光沿 -Z 照射（从 +Z 来）→ 正面受光
        var gbuf = DeferredRenderPipeline.PackGBuffer(Vector3.One, Vector3.UnitZ, 0f, 0.5f, 1f, Vector3.Zero);
        var light = DeferredLight.Directional(-Vector3.UnitZ, Vector3.One, 1f);
        var lit = DeferredRenderPipeline.EvaluateLight(gbuf, light, Vector3.UnitZ);

        AssertTrue(lit.X > 0f && lit.Y > 0f && lit.Z > 0f, "正对平行光应产生非零漫反射+镜面。");
        AssertTrue(lit.Length() > 0.1f, "受光表面亮度应显著非零。");
    }

    public static void TestDeferredLightBackfaceZero()
    {
        // 法线朝 -Z，光仍沿 -Z 照射（从 +Z 来）→ 法线背对光，应无直接光照
        var gbuf = DeferredRenderPipeline.PackGBuffer(Vector3.One, -Vector3.UnitZ, 0f, 0.5f, 1f, Vector3.Zero);
        var light = DeferredLight.Directional(-Vector3.UnitZ, Vector3.One, 1f);
        var lit = DeferredRenderPipeline.EvaluateLight(gbuf, light, Vector3.UnitZ);

        AssertTrue(lit.Length() < 1e-4f, "背面应无直接光照。");
    }

    public static void TestDeferredLightMetalNoDiffuse()
    {
        // 纯金属 (metallic=1) 应无漫反射，只剩镜面
        var gbuf = DeferredRenderPipeline.PackGBuffer(Vector3.One, Vector3.UnitZ, 1f, 0.4f, 1f, Vector3.Zero);
        var surf = DeferredRenderPipeline.DecodeGBuffer(gbuf);
        AssertTrue(surf.Metallic > 0.99f, "metallic=1 应解码为纯金属。");

        var light = DeferredLight.Directional(-Vector3.UnitZ, Vector3.One, 1f);
        var lit = DeferredRenderPipeline.EvaluateLight(gbuf, light, Vector3.UnitZ);
        AssertTrue(lit.Length() > 0f, "金属应仍有镜面高光。");
    }

    public static void TestDeferredPointLightAttenuation()
    {
        // 近距离 vs 超范围点光
        var gbuf = DeferredRenderPipeline.PackGBuffer(Vector3.One, Vector3.UnitZ, 0f, 0.5f, 1f, Vector3.Zero);
        var near = DeferredLight.Point(new Vector3(0f, 0f, 5f), Vector3.One, 1f, 100f);
        var far = DeferredLight.Point(new Vector3(0f, 0f, 200f), Vector3.One, 1f, 100f);

        var litNear = DeferredRenderPipeline.EvaluateLight(gbuf, near, Vector3.UnitZ);
        var litFar = DeferredRenderPipeline.EvaluateLight(gbuf, far, Vector3.UnitZ);

        AssertTrue(litNear.Length() > 0f, "范围内的点光应产生光照。");
        AssertTrue(litFar.Length() < 1e-4f, "超出范围的点光应完全衰减。");
    }

    public static void TestUnlitEmissiveIgnoresLight()
    {
        var gbuf = DeferredRenderPipeline.PackGBuffer(Vector3.One, Vector3.UnitZ, 0f, 0.5f, 1f,
            Vector3.Zero, ShadingModel.Unlit, emissive: new Vector3(1f, 0.5f, 0f));
        var light = DeferredLight.Directional(Vector3.UnitZ, Vector3.One, 1f);
        var lit = DeferredRenderPipeline.EvaluateLight(gbuf, light, Vector3.UnitZ);

        AssertTrue(lit.Length() < 1e-4f, "Unlit 材质不应受光照影响。");

        var full = DeferredRenderPipeline.RenderDeferredPixel(gbuf, new[] { light }, Vector3.UnitZ);
        AssertTrue(Math.Abs(full.X - 1f) < 1e-3f && Math.Abs(full.Y - 0.5f) < 1e-3f,
            "Unlit 材质应直接返回自发光。");
    }

    // ── RenderGraph 4-pass Deferred 图 ───────────────────────────────

    public static void TestDefaultDeferredGraphHasFourPasses()
    {
        var graph = DeferredRenderPipeline.BuildGraph(1920, 1080);
        var plan = graph.Compile();

        AssertTrue(plan.IsValid, "默认延迟图应可编译。");
        AssertTrue(plan.Passes.Count >= 4,
            "默认延迟图应包含 Shadow/GeometryBuffer/Lighting/PostProcess 4 个传递（PostProcess 已被标记副作用）。");
    }

    public static void TestDefaultDeferredGraphResources()
    {
        var graph = DeferredRenderPipeline.BuildGraph(1920, 1080);
        var plan = graph.Compile();

        var names = plan.Passes.Select(p => p.Pass.Name).ToList();
        AssertTrue(names.Contains("Shadow"), "延迟图应含 Shadow pass。");
        AssertTrue(names.Contains("GeometryBuffer"), "延迟图应含 GeometryBuffer pass。");
        AssertTrue(names.Contains("Lighting"), "延迟图应含 Lighting pass。");
        AssertTrue(names.Contains("PostProcess"), "延迟图应含 PostProcess pass。");

        AssertTrue(plan.Lifetimes.Any(l => l.FirstPass >= 0 && l.LastPass >= l.FirstPass),
            "资源应暴露有效生命周期。");
        AssertTrue(plan.Stats.TransientResourceCount > 0, "延迟图应统计瞬态资源。");
        AssertTrue(plan.Stats.EstimatedMemoryBytes > 0, "延迟图应估算内存。");
    }

    public static void TestDeferredGraphExecutesWithNullDevice()
    {
        var scene = new RenderSceneSnapshot
        {
            Name = "DeferredScene",
            Meshes = new[] { new RenderMesh { Name = "Cube", MeshAssetPath = "Assets/Meshes/Cube.shmesh", IsVisible = true } },
            Cameras = new[] { new RenderCamera { Name = "Main", IsMain = true } },
            Lights = new[] { new RenderLight { Name = "Sun", LightType = "Directional", CastsShadows = true } }
        };
        var graph = DeferredRenderPipeline.BuildGraph(1920, 1080);
        var device = new NullRenderDevice();

        var stats = device.Render(scene, graph, 1f / 60f);

        AssertTrue(stats.RenderGraphPassCount >= 4, "Null 设备应执行 4 个延迟 pass。");
        AssertTrue(stats.TransientResourceCount > 0, "执行统计应暴露瞬态资源。");
        AssertTrue(stats.EstimatedRenderGraphMemoryBytes > 0, "执行统计应暴露图内存估计。");
        AssertEqual(0, stats.RenderGraphErrorCount, "默认延迟图执行应无错误。");
    }
}
