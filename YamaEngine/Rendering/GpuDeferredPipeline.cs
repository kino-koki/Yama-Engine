// ════════════════════════════════════════════════════════════════════
// GpuDeferredPipeline.cs — GPU 渲染管线规划层（CPU 确定性，可严格验证）
//
// 把 Deferred 渲染 + 完整后处理栈的 GPU pass 定义为一棵可验证的管线图：
//   GBuffer(MRT 8) → DeferredLighting → SSAO → SSR → Bloom → PostStack → Composite → Present
// 每个 pass 携带真实 GLSL shader 源码、MRT 目标、深度/混合/光栅化状态。
//
// 关键能力：
//   - BuildFramePlan(...)      —— 规划渲染目标(GBuffer 8 MRT + Depth + SSAO + SSR + HDR + Bloom + Final)
//   - plan.BuildPipelineDescs()—— 生成真实 RHI RHIGraphicsPipelineDesc（VS/PS/MRT/深度/光栅化）
//   - 全部 CPU 确定性，无需真实设备即可验证 pass 顺序、MRT 布局、管线状态。
//
// 与 CPU 参考 DeferredRenderPipeline.PackGBuffer 打包约定对齐。
// ════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using YamaEngine.RHI;
using YamaEngine.Vulkan;

namespace YamaEngine.Rendering;

/// <summary>GPU pass 类型（与 RenderPipelineSettings 特性对应）。</summary>
public enum GpuPassKind
{
    GBuffer, DeferredLighting, SSAO, SSAOBlur, SSR,
    BloomExtract, BloomBlur, Tonemap, ColorGrading,
    DepthOfField, MotionBlur, Vignette, ChromaticAberration,
    Composite, Present
}

/// <summary>GPU 渲染目标描述。</summary>
public readonly struct GpuRenderTarget
{
    public readonly string Name;
    public readonly RHIFormat Format;
    public readonly float WidthScale;  // 相对主分辨率
    public readonly float HeightScale;
    public readonly bool IsDepth;

    public GpuRenderTarget(string name, RHIFormat format, float widthScale = 1f,
        float heightScale = 1f, bool isDepth = false)
    {
        Name = name; Format = format;
        WidthScale = widthScale; HeightScale = heightScale; IsDepth = isDepth;
    }
}

/// <summary>单个 GPU pass 记录（shader 源码 + 目标 + 状态）。</summary>
public sealed class GpuPassRecord
{
    public GpuPassKind Kind { get; init; }
    public string Name { get; init; } = "";
    public string VertexShader { get; init; } = "";
    public string FragmentShader { get; init; } = "";
    public IReadOnlyList<string> ColorTargets { get; init; } = Array.Empty<string>();
    public string? DepthTarget { get; init; }
    public bool IsFullScreen { get; init; }
    public RHIDepthDesc DepthState { get; init; } = RHIDepthDesc.Default;
    public RHIRasterizerDesc RasterizerState { get; init; } = RHIRasterizerDesc.Default;
    public RHIBlendDesc BlendState { get; init; } = RHIBlendDesc.Default;
}

/// <summary>GPU pass 管线描述（真实 RHI 描述 + 源码引用）。</summary>
public sealed class GpuPipelineDesc
{
    public GpuPassKind Kind { get; init; }
    public string Name { get; init; } = "";
    public string VSSource { get; init; } = "";
    public string FSSource { get; init; } = "";
    public bool IsFullScreen { get; init; }
    public RHIGraphicsPipelineDesc Desc { get; init; } = new();
}

/// <summary>GPU 管线统计（验证/诊断）。</summary>
public sealed class GpuPipelineStats
{
    public int GbufferTargets { get; set; }
    public int PassCount { get; set; }
    public int FullScreenPassCount { get; set; }
    public bool HasSSAO { get; set; }
    public bool HasSSR { get; set; }
    public bool HasBloom { get; set; }
    public bool HasColorGrading { get; set; }
    public bool HasDOF { get; set; }
    public bool HasMotionBlur { get; set; }
    public bool HasTonemap { get; set; }

    public override string ToString() =>
        $"passes={PassCount} fullscreen={FullScreenPassCount} gbufferTargets={GbufferTargets}";
}

/// <summary>GPU 帧计划：渲染目标 + 有序 pass 图。</summary>
public sealed class GpuFramePlan
{
    private readonly Dictionary<string, GpuRenderTarget> _targets = new(StringComparer.Ordinal);
    private readonly List<GpuPassRecord> _passes = new();

    public int Width { get; }
    public int Height { get; }
    public GpuPipelineStats Stats { get; } = new();
    public IReadOnlyDictionary<string, GpuRenderTarget> Targets => _targets;
    public IReadOnlyList<GpuPassRecord> Passes => _passes;

    public GpuFramePlan(int width, int height) { Width = width; Height = height; }

    public GpuFramePlan AddTarget(GpuRenderTarget t) { _targets[t.Name] = t; return this; }
    public GpuFramePlan AddPass(GpuPassRecord p) { _passes.Add(p); Stats.PassCount++; if (p.IsFullScreen) Stats.FullScreenPassCount++; return this; }
    public GpuRenderTarget Target(string name) => _targets.TryGetValue(name, out var t) ? t : default;

    /// <summary>生成每个 pass 的真实 RHI 管线描述（VS/PS bytecode 采用源码 UTF8；真实 GPU 用 GpuShaderCompiler 替换为 SPIR-V）。</summary>
    public List<GpuPipelineDesc> BuildPipelineDescs()
    {
        var list = new List<GpuPipelineDesc>(_passes.Count);
        foreach (var p in _passes)
        {
            var formats = p.ColorTargets.Count == 0
                ? new[] { RHIFormat.R8G8B8A8_UNorm }
                : p.ColorTargets.Select(t => _targets[t].Format).ToArray();
            var dsv = p.DepthTarget != null && _targets.TryGetValue(p.DepthTarget, out var dt)
                ? dt.Format : RHIFormat.Unknown;

            var desc = new RHIGraphicsPipelineDesc
            {
                Name = p.Name,
                VS = new RHIShaderBytecode(Encoding.UTF8.GetBytes(p.VertexShader), "main", RHIShaderStage.Vertex, p.Name + ".VS"),
                PS = new RHIShaderBytecode(Encoding.UTF8.GetBytes(p.FragmentShader), "main", RHIShaderStage.Pixel, p.Name + ".PS"),
                RTVFormats = formats,
                DSVFormat = dsv,
                DepthStencilState = p.DepthState,
                RasterizerState = p.RasterizerState,
                BlendState = p.BlendState,
                PrimitiveTopology = RHIPrimitiveTopology.TriangleList,
                SampleCount = 1
            };
            list.Add(new GpuPipelineDesc
            {
                Kind = p.Kind, Name = p.Name,
                VSSource = p.VertexShader, FSSource = p.FragmentShader,
                IsFullScreen = p.IsFullScreen, Desc = desc
            });
        }
        return list;
    }
}

/// <summary>GPU 管线构建器：规划 Deferred + 后处理 GPU 帧。</summary>
public static class GpuDeferredPipeline
{
    // 目标命名
    public const string GBufferWorldPos = "GBuffer.WorldPos";
    public const string GBufferAlbedo = "GBuffer.Albedo";
    public const string GBufferNormal = "GBuffer.Normal";
    public const string GBufferPBR = "GBuffer.PBR";
    public const string GBufferEmissive = "GBuffer.Emissive";
    public const string GBufferVelocity = "GBuffer.Velocity";
    public const string GBufferObjectId = "GBuffer.ObjectId";
    public const string GBufferSpare = "GBuffer.Spare";
    public const string DepthTarget = "Scene.Depth";
    public const string SSAOTarget = "Scene.SSAO";
    public const string HDRTarget = "Scene.HDR";
    public const string BloomATarget = "Bloom.A";
    public const string BloomBTarget = "Bloom.B";
    public const string FinalTarget = "Scene.Final";
    public const string SSRRTarget = "Scene.SSR";

    /// <summary>GBuffer MRT 8 目标（与 CPU DeferredRenderPipeline.PackGBuffer 对齐）。</summary>
    public static readonly GpuRenderTarget[] GbufferTargets =
    {
        new(GBufferWorldPos, RHIFormat.R16G16B16A16_Float),
        new(GBufferAlbedo, RHIFormat.R8G8B8A8_UNorm),
        new(GBufferNormal, RHIFormat.R8G8B8A8_UNorm),   // Octahedral 编码 [0,1]
        new(GBufferPBR, RHIFormat.R8G8B8A8_UNorm),      // R=Metal G=Rough B=AO
        new(GBufferEmissive, RHIFormat.R16G16B16A16_Float),
        new(GBufferVelocity, RHIFormat.R16G16_Float),
        new(GBufferObjectId, RHIFormat.R32_UInt),
        new(GBufferSpare, RHIFormat.R8G8B8A8_UNorm),
    };

    /// <summary>构建完整 GPU 帧计划（Deferred + 后处理栈）。</summary>
    public static GpuFramePlan BuildFramePlan(int width, int height, RenderPipelineSettings? settings = null)
    {
        var s = settings ?? new RenderPipelineSettings();
        var plan = new GpuFramePlan(Math.Max(width, 1), Math.Max(height, 1));

        // ── 渲染目标 ──
        foreach (var t in GbufferTargets) plan.AddTarget(t);
        plan.AddTarget(new GpuRenderTarget(DepthTarget, RHIFormat.D32_Float, isDepth: true));
        plan.AddTarget(new GpuRenderTarget(SSAOTarget, RHIFormat.R8_UNorm));
        plan.AddTarget(new GpuRenderTarget(SSRRTarget, RHIFormat.R16G16B16A16_Float));
        plan.AddTarget(new GpuRenderTarget(HDRTarget, RHIFormat.R16G16B16A16_Float));
        plan.AddTarget(new GpuRenderTarget(BloomATarget, RHIFormat.R16G16B16A16_Float, 0.5f, 0.5f));
        plan.AddTarget(new GpuRenderTarget(BloomBTarget, RHIFormat.R16G16B16A16_Float, 0.25f, 0.25f));
        plan.AddTarget(new GpuRenderTarget(FinalTarget, RHIFormat.B8G8R8A8_UNorm_SRGB));

        plan.Stats.GbufferTargets = GbufferTargets.Length;

        // ── Pass 图（有序）──
        // 1. GBuffer：MRT 8 + 深度写入
        plan.AddPass(new GpuPassRecord
        {
            Kind = GpuPassKind.GBuffer, Name = "GeometryBuffer",
            VertexShader = DeferredShadersGLSL.GBufferVertexShader,
            FragmentShader = DeferredShadersGLSL.GBufferFragmentShader,
            ColorTargets = GbufferTargets.Select(t => t.Name).ToArray(),
            DepthTarget = DepthTarget,
            DepthState = RHIDepthDesc.DepthWrite,
            RasterizerState = RHIRasterizerDesc.Default,
            BlendState = RHIBlendDesc.Default
        });

        // 2. Deferred Lighting：全屏，读 GBuffer + SSAO，写 HDR
        plan.AddPass(new GpuPassRecord
        {
            Kind = GpuPassKind.DeferredLighting, Name = "MainLighting",
            VertexShader = BuiltinShadersGLSL.FullScreenVertexShader,
            FragmentShader = DeferredShadersGLSL.DeferredLightingFragmentShader,
            ColorTargets = new[] { HDRTarget },
            IsFullScreen = true,
            DepthState = RHIDepthDesc.None,
            RasterizerState = RHIRasterizerDesc.NoCull,
            BlendState = RHIBlendDesc.Default
        });

        if ((s.FeatureFlags & RenderFeatureFlags.ScreenSpaceAO) != 0)
        {
            plan.Stats.HasSSAO = true;
            plan.AddPass(new GpuPassRecord
            {
                Kind = GpuPassKind.SSAO, Name = "SSAO",
                VertexShader = BuiltinShadersGLSL.FullScreenVertexShader,
                FragmentShader = DeferredShadersGLSL.SSAOFragmentShader,
                ColorTargets = new[] { SSAOTarget },
                IsFullScreen = true,
                DepthState = RHIDepthDesc.None,
                RasterizerState = RHIRasterizerDesc.NoCull
            });
            plan.AddPass(new GpuPassRecord
            {
                Kind = GpuPassKind.SSAOBlur, Name = "SSAOBlur",
                VertexShader = BuiltinShadersGLSL.FullScreenVertexShader,
                FragmentShader = DeferredShadersGLSL.SSAOBlurFragmentShader,
                ColorTargets = new[] { SSAOTarget },
                IsFullScreen = true,
                DepthState = RHIDepthDesc.None,
                RasterizerState = RHIRasterizerDesc.NoCull
            });
        }

        if ((s.FeatureFlags & RenderFeatureFlags.ScreenSpaceRefl) != 0)
        {
            plan.Stats.HasSSR = true;
            plan.AddPass(new GpuPassRecord
            {
                Kind = GpuPassKind.SSR, Name = "SSR",
                VertexShader = BuiltinShadersGLSL.FullScreenVertexShader,
                FragmentShader = DeferredShadersGLSL.SSRFragmentShader,
                ColorTargets = new[] { SSRRTarget },
                IsFullScreen = true,
                DepthState = RHIDepthDesc.None,
                RasterizerState = RHIRasterizerDesc.NoCull
            });
        }

        if ((s.FeatureFlags & RenderFeatureFlags.Bloom) != 0)
        {
            plan.Stats.HasBloom = true;
            plan.AddPass(new GpuPassRecord
            {
                Kind = GpuPassKind.BloomExtract, Name = "Bloom",
                VertexShader = BuiltinShadersGLSL.FullScreenVertexShader,
                FragmentShader = PostProcessStackGLSL.BloomExtractFragmentShader,
                ColorTargets = new[] { BloomATarget },
                IsFullScreen = true, DepthState = RHIDepthDesc.None,
                RasterizerState = RHIRasterizerDesc.NoCull
            });
            plan.AddPass(new GpuPassRecord
            {
                Kind = GpuPassKind.BloomBlur, Name = "BloomBlur",
                VertexShader = BuiltinShadersGLSL.FullScreenVertexShader,
                FragmentShader = PostProcessStackGLSL.BloomBlurFragmentShader,
                ColorTargets = new[] { BloomBTarget },
                IsFullScreen = true, DepthState = RHIDepthDesc.None,
                RasterizerState = RHIRasterizerDesc.NoCull
            });
        }

        if ((s.FeatureFlags & RenderFeatureFlags.ColorGrading) != 0)
        {
            plan.Stats.HasColorGrading = true;
            plan.AddPass(new GpuPassRecord
            {
                Kind = GpuPassKind.ColorGrading, Name = "ColorGrading",
                VertexShader = BuiltinShadersGLSL.FullScreenVertexShader,
                FragmentShader = PostProcessStackGLSL.ColorGradingFragmentShader,
                ColorTargets = new[] { HDRTarget },
                IsFullScreen = true, DepthState = RHIDepthDesc.None,
                RasterizerState = RHIRasterizerDesc.NoCull
            });
        }

        if ((s.FeatureFlags & RenderFeatureFlags.DepthOfField) != 0)
        {
            plan.Stats.HasDOF = true;
            plan.AddPass(new GpuPassRecord
            {
                Kind = GpuPassKind.DepthOfField, Name = "DepthOfField",
                VertexShader = BuiltinShadersGLSL.FullScreenVertexShader,
                FragmentShader = PostProcessStackGLSL.DepthOfFieldFragmentShader,
                ColorTargets = new[] { HDRTarget },
                IsFullScreen = true, DepthState = RHIDepthDesc.None,
                RasterizerState = RHIRasterizerDesc.NoCull
            });
        }

        if ((s.FeatureFlags & RenderFeatureFlags.MotionBlur) != 0)
        {
            plan.Stats.HasMotionBlur = true;
            plan.AddPass(new GpuPassRecord
            {
                Kind = GpuPassKind.MotionBlur, Name = "MotionBlur",
                VertexShader = BuiltinShadersGLSL.FullScreenVertexShader,
                FragmentShader = PostProcessStackGLSL.MotionBlurFragmentShader,
                ColorTargets = new[] { HDRTarget },
                IsFullScreen = true, DepthState = RHIDepthDesc.None,
                RasterizerState = RHIRasterizerDesc.NoCull
            });
        }

        if ((s.FeatureFlags & RenderFeatureFlags.Tonemapping) != 0)
        {
            plan.Stats.HasTonemap = true;
            plan.AddPass(new GpuPassRecord
            {
                Kind = GpuPassKind.Tonemap, Name = "Tonemapping",
                VertexShader = BuiltinShadersGLSL.FullScreenVertexShader,
                FragmentShader = PostProcessStackGLSL.CompositeFragmentShader,
                ColorTargets = new[] { FinalTarget },
                IsFullScreen = true, DepthState = RHIDepthDesc.None,
                RasterizerState = RHIRasterizerDesc.NoCull
            });
        }

        return plan;
    }
}

/// <summary>
/// GPU 管线桥接层：把 CPU 渲染 pass 框架接到 GPU 管线图（每帧环境状态）。
/// RenderPipeline.Render 先 ResetFrame(宽,高,设置)，随后各 pass 的 Execute 调用
/// Execute(passName) 记录并返回对应 GPU pass 记录（含真实 shader 源码/MRT 布局）。
/// 全部 CPU 确定性，可测；真实 GPU 端由 VulkanRenderDevice/PostProcessSystem 消费。
/// </summary>
public static class GpuPipelineBridge
{
    /// <summary>当前帧 GPU 帧计划（ResetFrame 后有效）。</summary>
    public static GpuFramePlan? Plan { get; private set; }

    /// <summary>当前帧已执行的 pass 统计。</summary>
    public static GpuPipelineStats Stats { get; private set; } = new();

    /// <summary>最近一次 Execute 命中的 GPU pass 记录。</summary>
    public static GpuPassRecord? LastRecord { get; private set; }

    /// <summary>最近一次 Execute 是否命中（pass 存在于管线图）。</summary>
    public static bool LastMatched { get; private set; }

    /// <summary>开启一帧：构建 GPU 帧计划并重置统计。</summary>
    public static GpuFramePlan ResetFrame(int width, int height, RenderPipelineSettings? settings = null)
    {
        Plan = GpuDeferredPipeline.BuildFramePlan(width, height, settings);
        Stats = new GpuPipelineStats { PassCount = 0, GbufferTargets = Plan.Stats.GbufferTargets };
        LastRecord = null; LastMatched = false;
        return Plan;
    }

    /// <summary>按 pass 名在管线图中执行对应 GPU pass（记录活动，返回记录或 null）。</summary>
    public static GpuPassRecord? Execute(string passName)
    {
        if (Plan == null) return null;
        var rec = Plan.Passes.FirstOrDefault(p => p.Name.Equals(passName, StringComparison.OrdinalIgnoreCase));
        LastMatched = rec != null;
        if (rec == null) { LastRecord = null; return null; }
        LastRecord = rec;
        Stats.PassCount++;
        switch (rec.Kind)
        {
            case GpuPassKind.SSAO: Stats.HasSSAO = true; break;
            case GpuPassKind.SSR: Stats.HasSSR = true; break;
            case GpuPassKind.BloomExtract:
            case GpuPassKind.BloomBlur: Stats.HasBloom = true; break;
            case GpuPassKind.ColorGrading: Stats.HasColorGrading = true; break;
            case GpuPassKind.DepthOfField: Stats.HasDOF = true; break;
            case GpuPassKind.MotionBlur: Stats.HasMotionBlur = true; break;
            case GpuPassKind.Tonemap:
            case GpuPassKind.Composite: Stats.HasTonemap = true; break;
        }
        return rec;
    }
}
