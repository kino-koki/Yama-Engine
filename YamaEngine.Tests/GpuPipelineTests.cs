// GpuPipelineTests.cs — GPU 渲染管线（Deferred + 后处理栈）验证套件
// 覆盖：真实 GLSL shader 有效性、GBuffer MRT8 布局、GPU 帧计划、pass 顺序、
//      RHI 管线描述、shader 编译钩子（source-only 容错）、GPU 管线桥接。
// 全部 CPU 确定性，无需真实设备。
using System;
using System.Linq;
using YamaEngine.RHI;
using YamaEngine.Rendering;
using YamaEngine.Vulkan;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests;

public static class GpuPipelineTests
{
    // ── 1. Deferred shader 源码有效性 ──────────────────────────────

    public static void TestGbufferShadersValidAndMrt8()
    {
        AssertTrue(DeferredShadersGLSL.GBufferVertexShader.Contains("#version 450"), "GBuffer VS 应为 #version 450。");
        AssertTrue(DeferredShadersGLSL.GBufferFragmentShader.Contains("#version 450"), "GBuffer FS 应为 #version 450。");
        // MRT 8：应有 out location 0..7
        for (int i = 0; i < 8; i++)
            AssertTrue(DeferredShadersGLSL.GBufferFragmentShader.Contains($"layout(location = {i}) out vec4"),
                $"GBuffer 应输出 location {i}。");
        AssertTrue(DeferredShadersGLSL.GBufferFragmentShader.Contains("octahedralEncode"),
            "GBuffer 应含 Octahedral 法线编码。");
        AssertTrue(DeferredShadersGLSL.GBufferFragmentShader.Contains("metallicRoughnessTex"),
            "GBuffer 应读取 PBR 贴图。");
    }

    public static void TestDeferredLightingShaderValid()
    {
        var fs = DeferredShadersGLSL.DeferredLightingFragmentShader;
        AssertTrue(fs.Contains("gWorldPos") && fs.Contains("gAlbedo") && fs.Contains("gNormal")
            && fs.Contains("gPBR") && fs.Contains("gSSAO") && fs.Contains("gEmissive"),
            "延迟光照应绑定 GBuffer + SSAO 采样器。");
        AssertTrue(fs.Contains("D_GGX") && fs.Contains("G_SmithGGX") && fs.Contains("F_Schlick"),
            "延迟光照应实现 GGX Cook-Torrance BRDF。");
        AssertTrue(fs.Contains("octahedralDecode"), "延迟光照应解码法线。");
        AssertTrue(fs.Contains("direction") || fs.Contains("dirLight"), "延迟光照应评估方向光。");
    }

    public static void TestPostProcessStackShadersValid()
    {
        AssertTrue(PostProcessStackGLSL.BloomExtractFragmentShader.Contains("#version 450") && PostProcessStackGLSL.BloomExtractFragmentShader.Contains("threshold"),
            "Bloom 提取应有效。");
        AssertTrue(PostProcessStackGLSL.BloomBlurFragmentShader.Contains("weights"), "Bloom 模糊应含高斯权重。");
        AssertTrue(PostProcessStackGLSL.TonemapFragmentShader.Contains("acesTonemap"), "色调映射应含 ACES。");
        AssertTrue(PostProcessStackGLSL.ColorGradingFragmentShader.Contains("saturation"), "颜色分级应含饱和度。");
        AssertTrue(PostProcessStackGLSL.DepthOfFieldFragmentShader.Contains("coc"), "DOF 应含 CoC。");
        AssertTrue(PostProcessStackGLSL.MotionBlurFragmentShader.Contains("velocity"), "运动模糊应含速度采样。");
        AssertTrue(PostProcessStackGLSL.VignetteFragmentShader.Contains("smoothstep"), "暗角应有效。");
        AssertTrue(PostProcessStackGLSL.ChromaticAberrationFragmentShader.Contains("radial") || PostProcessStackGLSL.ChromaticAberrationFragmentShader.Contains("off"),
            "色差应含径向偏移。");
        AssertTrue(PostProcessStackGLSL.CompositeFragmentShader.Contains("acesTonemap") && PostProcessStackGLSL.CompositeFragmentShader.Contains("bloomTex"),
            "Composite 应合并 Bloom + 色调映射。");
    }

    // ── 2. GBuffer MRT8 布局 ────────────────────────────────────────

    public static void TestFramePlanBuildsGbufferMrt8()
    {
        var plan = GpuDeferredPipeline.BuildFramePlan(1920, 1080, new RenderPipelineSettings { Path = RenderPath.Deferred });
        AssertTrue(plan.Stats.GbufferTargets == 8, "GBuffer 应为 MRT 8。");

        AssertTrue(plan.Target(GpuDeferredPipeline.GBufferWorldPos).Format == RHIFormat.R16G16B16A16_Float, "WorldPos 应为 RGBA16F。");
        AssertTrue(plan.Target(GpuDeferredPipeline.GBufferAlbedo).Format == RHIFormat.R8G8B8A8_UNorm, "Albedo 应为 RGBA8。");
        AssertTrue(plan.Target(GpuDeferredPipeline.GBufferNormal).Format == RHIFormat.R8G8B8A8_UNorm, "Normal 应为 RGBA8(Octahedral)。");
        AssertTrue(plan.Target(GpuDeferredPipeline.GBufferPBR).Format == RHIFormat.R8G8B8A8_UNorm, "PBR 应为 RGBA8。");
        AssertTrue(plan.Target(GpuDeferredPipeline.GBufferEmissive).Format == RHIFormat.R16G16B16A16_Float, "Emissive 应为 RGBA16F。");
        AssertTrue(plan.Target(GpuDeferredPipeline.DepthTarget).Format == RHIFormat.D32_Float, "深度应为 D32_Float。");
    }

    // ── 3. pass 顺序 ───────────────────────────────────────────────

    public static void TestFramePlanPassOrder()
    {
        var settings = new RenderPipelineSettings
        {
            Path = RenderPath.Deferred,
            FeatureFlags = RenderFeatureFlags.ShadowMaps | RenderFeatureFlags.ScreenSpaceAO |
                RenderFeatureFlags.ScreenSpaceRefl | RenderFeatureFlags.Bloom |
                RenderFeatureFlags.Tonemapping | RenderFeatureFlags.ColorGrading
        };
        var plan = GpuDeferredPipeline.BuildFramePlan(1280, 720, settings);
        int IndexOf(string name) => plan.Passes.ToList().FindIndex(p => p.Name == name);

        int gb = IndexOf("GeometryBuffer");
        int light = IndexOf("MainLighting");
        int ssao = IndexOf("SSAO");
        int ssr = IndexOf("SSR");
        int bloom = IndexOf("Bloom");
        int tone = IndexOf("Tonemapping");

        AssertTrue(gb == 0, "GBuffer 应为第一个 pass。");
        AssertTrue(gb < light, "GBuffer 应在光照前。");
        AssertTrue(light < ssao, "光照应在 SSAO 前。");
        AssertTrue(ssao < ssr, "SSAO 应在 SSR 前。");
        AssertTrue(ssr < bloom, "SSR 应在 Bloom 前。");
        AssertTrue(bloom < tone, "Bloom 应在色调映射前。");
        AssertTrue(tone == plan.Passes.Count - 1, "色调映射应为最后一个 pass。");
    }

    // ── 4. 特性开关 ─────────────────────────────────────────────────

    public static void TestFramePlanRespectsFeatureFlags()
    {
        var all = new RenderPipelineSettings
        {
            Path = RenderPath.Deferred,
            FeatureFlags = RenderFeatureFlags.ShadowMaps | RenderFeatureFlags.ScreenSpaceAO |
                RenderFeatureFlags.ScreenSpaceRefl | RenderFeatureFlags.Bloom |
                RenderFeatureFlags.ColorGrading | RenderFeatureFlags.DepthOfField |
                RenderFeatureFlags.MotionBlur | RenderFeatureFlags.Tonemapping
        };
        var plan = GpuDeferredPipeline.BuildFramePlan(800, 600, all);
        AssertTrue(plan.Stats.HasSSAO && plan.Stats.HasSSR && plan.Stats.HasBloom
            && plan.Stats.HasColorGrading && plan.Stats.HasDOF && plan.Stats.HasMotionBlur && plan.Stats.HasTonemap,
            "开启全部特性应全为真。");

        var minimal = new RenderPipelineSettings { Path = RenderPath.Forward, FeatureFlags = RenderFeatureFlags.None };
        var plan2 = GpuDeferredPipeline.BuildFramePlan(800, 600, minimal);
        AssertTrue(!plan2.Stats.HasSSAO && !plan2.Stats.HasSSR && !plan2.Stats.HasBloom,
            "关闭特性应无对应 pass。");
    }

    // ── 5. RHI 管线描述 ────────────────────────────────────────────

    public static void TestBuildPipelineDescsProduceRhiDescs()
    {
        var plan = GpuDeferredPipeline.BuildFramePlan(1280, 720, new RenderPipelineSettings { Path = RenderPath.Deferred });
        var descs = plan.BuildPipelineDescs();

        AssertTrue(descs.Count == plan.Passes.Count, "每个 pass 应生成一个管线描述。");

        var gb = descs.First(d => d.Kind == GpuPassKind.GBuffer);
        AssertTrue(gb.Desc.RTVFormats.Length == 8, "GBuffer 管线应为 8 个 RTV。");
        AssertTrue(gb.Desc.DSVFormat == RHIFormat.D32_Float, "GBuffer 管线应绑定深度。");
        AssertTrue(gb.Desc.DepthStencilState.DepthWriteEnable, "GBuffer 应写深度。");
        AssertTrue(gb.Desc.VS.HasValue && gb.Desc.VS.Value.IsValid, "GBuffer VS 应有效。");
        AssertTrue(gb.Desc.PS.HasValue && gb.Desc.PS.Value.IsValid, "GBuffer PS 应有效。");

        var light = descs.First(d => d.Kind == GpuPassKind.DeferredLighting);
        AssertTrue(light.IsFullScreen, "光照应为全屏 pass。");
        AssertTrue(!light.Desc.DepthStencilState.DepthEnable, "光照应禁用深度。");
        AssertTrue(light.Desc.RasterizerState.CullMode == RHICullMode.None, "光照应不剔除。");

        var ssao = descs.First(d => d.Kind == GpuPassKind.SSAO);
        AssertTrue(ssao.Desc.RTVFormats.Length == 1 && ssao.Desc.RTVFormats[0] == RHIFormat.R8_UNorm,
            "SSAO 输出应为单通道 R8。");
    }

    // ── 6. shader 编译钩子 ─────────────────────────────────────────

    public static void TestGpuShaderCompilerDeterministic()
    {
        var r = GpuShaderCompiler.TryCompileToSpirv(DeferredShadersGLSL.GBufferFragmentShader, ShaderStageExt.Fragment);
        if (GpuShaderCompiler.IsShadercAvailable)
        {
            AssertTrue(r.Success && r.Spirv != null && r.Spirv.Length > 0,
                "shaderc 可用时应成功编译 SPIR-V。");
        }
        else
        {
            AssertTrue(!r.Success && r.IsSourceOnly && r.Error == GpuShaderCompiler.SourceOnlyReason,
                "无工具链时应进入 source-only 模式（不抛异常）。");
        }
    }

    public static void TestGpuShaderCompilerRejectsEmpty()
    {
        var r = GpuShaderCompiler.TryCompileToSpirv("", ShaderStageExt.Fragment);
        AssertTrue(!r.Success && !r.IsSourceOnly, "空源码应失败而非 source-only。");
    }

    // ── 7. GPU 管线桥接 ────────────────────────────────────────────

    public static void TestGpuPipelineBridgeExecutesPasses()
    {
        var plan = GpuPipelineBridge.ResetFrame(1280, 720, new RenderPipelineSettings { Path = RenderPath.Deferred });
        AssertTrue(plan != null && plan.Passes.Count > 0, "ResetFrame 应构建 GPU 帧计划。");

        var gb = GpuPipelineBridge.Execute("GeometryBuffer");
        AssertTrue(GpuPipelineBridge.LastMatched && gb != null, "GBuffer pass 应命中管线图。");
        AssertTrue(gb!.ColorTargets.Count == 8, "GBuffer 桥接应暴露 MRT8。");
        AssertTrue(GpuPipelineBridge.Stats.PassCount == 1, "应记录 1 次执行。");

        var shadow = GpuPipelineBridge.Execute("ShadowMap");
        AssertTrue(!GpuPipelineBridge.LastMatched && shadow == null, "ShadowMap 不在 Deferred 管线图中。");
    }
}
