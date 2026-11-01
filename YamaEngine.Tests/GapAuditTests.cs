// GapAuditTests.cs — 渲染缺口补齐 + 缺陷评估验证套件
// 覆盖：IBL/大气/附加效果 GLSL 有效性、新 pass 接入 GPU 管线计划、
//      缺陷审计工具 RenderGapAudit 的覆盖率/孤岛/桥接判定。
// 全部 CPU 确定性，无需真实设备。
using System;
using System.Linq;
using YamaEngine.Rendering;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests;

public static class GapAuditTests
{
    // ── 1. IBL shader 有效性 ───────────────────────────────────────

    public static void TestIblShadersValid()
    {
        AssertTrue(IBLShadersGLSL.IrradianceFragmentShader.Contains("#version 450")
            && IBLShadersGLSL.IrradianceFragmentShader.Contains("irradiance"),
            "IBL 辐照度卷积应有效。");
        AssertTrue(IBLShadersGLSL.PrefilterFragmentShader.Contains("ImportanceSampleGGX")
            && IBLShadersGLSL.PrefilterFragmentShader.Contains("textureLod"),
            "IBL 镜面预滤波应含 GGX 重要性采样 + mip Lod。");
        AssertTrue(IBLShadersGLSL.BrdfLutFragmentShader.Contains("IntegrateBRDF")
            && IBLShadersGLSL.BrdfLutFragmentShader.Contains("Fc"),
            "IBL BRDF LUT 应积分 scale/bias。");
        AssertTrue(IBLShadersGLSL.AmbientFragmentShader.Contains("uIrradianceMap")
            && IBLShadersGLSL.AmbientFragmentShader.Contains("uPrefilterMap")
            && IBLShadersGLSL.AmbientFragmentShader.Contains("uBrdfLUT"),
            "IBL 环境注入应绑定辐照度/预滤波/BRDF LUT。");
    }

    // ── 2. 大气散射 shader 有效性 ─────────────────────────────────

    public static void TestAtmosphereShadersValid()
    {
        var sky = AtmosphereShadersGLSL.SkyFragmentShader;
        AssertTrue(sky.Contains("#version 450") && sky.Contains("IntersectSphere")
            && sky.Contains("IntegrateScattering"),
            "大气天空应含球壳求交 + 散射积分。");
        AssertTrue(sky.Contains("BETA_R") && sky.Contains("BETA_M")
            && sky.Contains("HGP"), "大气应含瑞利/米氏系数 + Henyey-Greenstein。");
        AssertTrue(AtmosphereShadersGLSL.InscatterFragmentShader.Contains("inscatter"),
            "大气内散射注入应有效。");
    }

    // ── 3. 附加效果 shader 有效性 ─────────────────────────────────

    public static void TestAdditionalEffectShadersValid()
    {
        AssertTrue(AdditionalEffectShadersGLSL.SsgiFragmentShader.Contains("RayMarch")
            && AdditionalEffectShadersGLSL.SsgiFragmentShader.Contains("uScene"),
            "SSGI 应含屏幕空间 ray march。");
        AssertTrue(AdditionalEffectShadersGLSL.ContactShadowsFragmentShader.Contains("uLightDir"),
            "接触阴影应含光源方向。");
        AssertTrue(AdditionalEffectShadersGLSL.TaaFragmentShader.Contains("motion")
            && AdditionalEffectShadersGLSL.TaaFragmentShader.Contains("history"),
            "TAA 应含运动向量重投影 + 历史。");
        AssertTrue(AdditionalEffectShadersGLSL.LensFlareFragmentShader.Contains("radial")
            && AdditionalEffectShadersGLSL.LensFlareFragmentShader.Contains("halo"),
            "镜头光晕应含径向条纹 + 光环。");
        AssertTrue(AdditionalEffectShadersGLSL.VignetteFragmentShader.Contains("smoothstep"),
            "暗角应有效。");
        AssertTrue(AdditionalEffectShadersGLSL.ChromaticAberrationFragmentShader.Contains("offset"),
            "色差应含径向偏移。");
    }

    // ── 4. 全特性帧计划含全部新 pass ─────────────────────────────

    public static void TestAllFeaturePlanIncludesNewPasses()
    {
        var plan = GpuDeferredPipeline.BuildFramePlan(1920, 1080,
            new RenderPipelineSettings { Path = RenderPath.Deferred, FeatureFlags = (RenderFeatureFlags)ulong.MaxValue });
        var names = plan.Passes.Select(p => p.Name).ToHashSet();
        AssertTrue(names.Contains("IBL.Irradiance") && names.Contains("IBL.Prefilter")
            && names.Contains("IBL.BrdfLUT") && names.Contains("IBL.Ambient"),
            "全特性应含 IBL 四 pass。");
        AssertTrue(names.Contains("Atmosphere.Sky") && names.Contains("Atmosphere.Inscatter"),
            "全特性应含大气两 pass。");
        AssertTrue(names.Contains("VolumetricFog"), "全特性应含体积雾。");
        AssertTrue(names.Contains("SSGI") && names.Contains("ContactShadows"),
            "全特性应含 SSGI + 接触阴影。");
        AssertTrue(names.Contains("TAA"), "全特性应含 TAA。");
        AssertTrue(names.Contains("LensFlare") && names.Contains("Vignette")
            && names.Contains("ChromaticAberration"),
            "全特性应含镜头光晕/暗角/色差。");
    }

    // ── 5. 默认设置不含可选新 pass ───────────────────────────────

    public static void TestDefaultPlanExcludesOptionalPasses()
    {
        var plan = GpuDeferredPipeline.BuildFramePlan(1280, 720);
        var names = plan.Passes.Select(p => p.Name).ToHashSet();
        // 默认不含 IBL（GlobalIllumination 未开）
        AssertTrue(!names.Contains("IBL.Ambient"), "默认不应含 IBL。");
        AssertTrue(!names.Contains("SSGI") && !names.Contains("ContactShadows")
            && !names.Contains("TAA") && !names.Contains("LensFlare"),
            "默认不应含可选 SSGI/接触阴影/TAA/镜头光晕。");
        // 默认含 SSAO/Bloom/Tonemapping（FeatureFlags 默认含这些）
        AssertTrue(names.Contains("SSAO") && names.Contains("Bloom")
            && names.Contains("Tonemapping"), "默认应含核心 SSAO/Bloom/Tonemapping。");
    }

    // ── 6. 桥接统计识别新 pass ───────────────────────────────────

    public static void TestBridgeStatsDetectNewPasses()
    {
        GpuPipelineBridge.ResetFrame(1920, 1080,
            new RenderPipelineSettings { Path = RenderPath.Deferred, FeatureFlags = (RenderFeatureFlags)ulong.MaxValue });
        AssertTrue(GpuPipelineBridge.Execute("IBL.Ambient") != null, "IBL.Ambient 应可执行。");
        AssertTrue(GpuPipelineBridge.Stats.HasIBL, "桥接应识别 IBL。");
        AssertTrue(GpuPipelineBridge.Execute("Atmosphere.Sky") != null
            && GpuPipelineBridge.Stats.HasAtmosphere, "桥接应识别大气。");
        AssertTrue(GpuPipelineBridge.Execute("VolumetricFog") != null
            && GpuPipelineBridge.Stats.HasVolumetricFog, "桥接应识别体积雾。");
        AssertTrue(GpuPipelineBridge.Execute("SSGI") != null && GpuPipelineBridge.Stats.HasSSGI,
            "桥接应识别 SSGI。");
        AssertTrue(GpuPipelineBridge.Execute("ContactShadows") != null
            && GpuPipelineBridge.Stats.HasContactShadows, "桥接应识别接触阴影。");
        AssertTrue(GpuPipelineBridge.Execute("TAA") != null && GpuPipelineBridge.Stats.HasTAA,
            "桥接应识别 TAA。");
        AssertTrue(GpuPipelineBridge.Execute("LensFlare") != null
            && GpuPipelineBridge.Stats.HasLensFlare, "桥接应识别镜头光晕。");
        AssertTrue(GpuPipelineBridge.Execute("Vignette") != null
            && GpuPipelineBridge.Stats.HasVignette, "桥接应识别暗角。");
        AssertTrue(GpuPipelineBridge.Execute("ChromaticAberration") != null
            && GpuPipelineBridge.Stats.HasChromaticAberration, "桥接应识别色差。");
    }

    // ── 7. 缺陷审计：全特性覆盖率高 ───────────────────────────────

    public static void TestGapAuditAllFeaturesHighCoverage()
    {
        var report = RenderGapReport.Audit();
        // 全特性基线：核心特性应全部接线；仅高级未实现特性（Decals/Skinned/Instanced/VirtualTexturing）可为 Gap
        AssertTrue(report.FeatureGap <= 6,
            $"全特性下 Gap 应仅为高级未实现特性（<=6），实际 {report.FeatureGap} 个。");
        // 覆盖率应显著（大部分接线或部分接线）
        AssertTrue(report.CoverageScore >= 0.6,
            $"覆盖率应 >=60%，实际 {report.CoverageScore:P0}。");
        // 核心延迟特性应 Wired
        AssertTrue(report.Items.Any(i => i.Category == "Feature" && i.Name == "ScreenSpaceAO" && i.Status == GapStatus.Wired),
            "SSAO 应 Wired。");
        AssertTrue(report.Items.Any(i => i.Category == "Feature" && i.Name == "Bloom" && i.Status == GapStatus.Wired),
            "Bloom 应 Wired。");
    }

    // ── 8. 缺陷审计：孤岛算法判定 ─────────────────────────────────

    public static void TestGapAuditIslandDetection()
    {
        var report = RenderGapReport.Audit();
        // BRDFUtility 等核心算法应接线
        AssertTrue(report.Items.Any(i => i.Category == "Algorithm" && i.Name == "BRDFUtility" && i.Status == GapStatus.Wired),
            "BRDFUtility 应已接线。");
        // 仍孤岛的算法应识别为 Gap
        AssertTrue(report.Items.Any(i => i.Category == "Algorithm" && i.Name == "GeometryBufferGenerator" && i.Status == GapStatus.Gap),
            "GeometryBufferGenerator 应识别为孤岛。");
        AssertTrue(report.Items.Any(i => i.Category == "Algorithm" && i.Name == "LightResolveProcessor" && i.Status == GapStatus.Gap),
            "LightResolveProcessor 应识别为孤岛。");
    }

    // ── 9. 缺陷审计：pass 桥接命中 ────────────────────────────────

    public static void TestGapAuditPassBridgeMatch()
    {
        var report = RenderGapReport.Audit();
        // GeometryBuffer/MainLighting/Bloom/Tonemapping 应桥接命中
        AssertTrue(report.Items.Any(i => i.Category == "Pass" && i.Name == "GeometryBuffer" && i.Status == GapStatus.Wired),
            "GeometryBuffer pass 应桥接命中。");
        AssertTrue(report.Items.Any(i => i.Category == "Pass" && i.Name == "MainLighting" && i.Status == GapStatus.Wired),
            "MainLighting pass 应桥接命中。");
        // 默认全特性下 Present pass 不在 GPU 图中（Present 由 RuntimeGameLoop 处理）——允许任意
        AssertTrue(report.PassTotal > 0, "应有 pass 被审计。");
    }

    // ── 10. 缺陷审计：报告摘要含关键指标 ─────────────────────────

    public static void TestGapAuditSummaryContainsMetrics()
    {
        var report = RenderGapReport.Audit();
        var s = report.Summary();
        AssertTrue(s.Contains("覆盖率"), "摘要应含覆盖率。");
        AssertTrue(s.Contains("缺口"), "摘要应含缺口数。");
        AssertTrue(report.FeatureTotal > 0 && report.AlgorithmTotal > 0 && report.PassTotal > 0,
            "报告应统计特性/算法/pass。");
    }
}
