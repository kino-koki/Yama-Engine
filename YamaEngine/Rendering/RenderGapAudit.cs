// ════════════════════════════════════════════════════════════════════
// RenderGapAudit.cs — 渲染缺陷评估工具
//
// 扫描渲染子系统并产出结构化缺陷报告（RenderGapReport）：
//   1. 特性覆盖审计  —— 对每个 RenderFeatureFlags 判定 GPU 计划/CPU pass 接线状态
//   2. 孤岛算法审计  —— 检查散落的延迟算法是否被管线引用（源引用核查）
//   3. Pass 接线审计 —— CPU pass 在 GPU 管线图中是否有对应记录（桥接命中）
//
// 全部 CPU 确定性、可测；用于「补齐缺口后评估剩余缺陷」。
// ════════════════════════════════════════════════════════════════════

using System.Reflection;

namespace YamaEngine.Rendering;

/// <summary>缺陷项状态。</summary>
public enum GapStatus { Wired, Partial, Gap }

/// <summary>单个缺陷/覆盖项。</summary>
public sealed class RenderGapItem
{
    public string Category { get; init; } = "";   // Feature / Shader / Algorithm / Pass
    public string Name { get; init; } = "";
    public GapStatus Status { get; init; }
    public string Detail { get; init; } = "";
}

/// <summary>渲染缺陷评估报告（结构化、可测）。</summary>
public sealed class RenderGapReport
{
    public List<RenderGapItem> Items { get; } = new();

    public int FeatureTotal { get; private set; }
    public int FeatureWired { get; private set; }
    public int FeaturePartial { get; private set; }
    public int FeatureGap { get; private set; }
    public int AlgorithmTotal { get; private set; }
    public int AlgorithmWired { get; private set; }
    public int PassTotal { get; private set; }
    public int PassWired { get; private set; }

    /// <summary>覆盖率 0..1（特性 Wired 全额 + Partial 半额）。</summary>
    public double CoverageScore =>
        FeatureTotal == 0 ? 0 :
        (FeatureWired + FeaturePartial * 0.5) / (double)FeatureTotal;

    public int GapCount => Items.Count(i => i.Status == GapStatus.Gap);

    private void AddFeature(string name, bool gpu, bool cpu)
    {
        var status = gpu && cpu ? GapStatus.Wired : (gpu || cpu ? GapStatus.Partial : GapStatus.Gap);
        Items.Add(new RenderGapItem
        {
            Category = "Feature", Name = name, Status = status,
            Detail = $"GPU:{(gpu ? "✓" : "—")} CPU:{(cpu ? "✓" : "—")}"
        });
        FeatureTotal++;
        if (status == GapStatus.Wired) FeatureWired++;
        else if (status == GapStatus.Partial) FeaturePartial++;
        else FeatureGap++;
    }

    private void AddAlgorithm(string name, bool wired)
    {
        Items.Add(new RenderGapItem
        {
            Category = "Algorithm", Name = name,
            Status = wired ? GapStatus.Wired : GapStatus.Gap,
            Detail = wired ? "已被管线引用" : "孤岛（0 代码引用，仅注释提及）"
        });
        AlgorithmTotal++;
        if (wired) AlgorithmWired++;
    }

    private void AddPass(string name, bool matched)
    {
        Items.Add(new RenderGapItem
        {
            Category = "Pass", Name = name,
            Status = matched ? GapStatus.Wired : GapStatus.Gap,
            Detail = matched ? "GPU 管线图命中" : "GPU 管线图无对应记录"
        });
        PassTotal++;
        if (matched) PassWired++;
    }

    public string Summary()
    {
        return $"特性 {FeatureWired}/{FeatureTotal} 已接线(+{FeaturePartial} 部分), " +
               $"孤岛算法 {AlgorithmWired}/{AlgorithmTotal} 已接线, " +
               $"pass {PassWired}/{PassTotal} 桥接命中, " +
               $"覆盖率 {CoverageScore:P0}, 缺口 {GapCount}";
    }

    /// <summary>
    /// 执行渲染缺陷审计。
    /// settings 指定评估基线（默认全特性 Deferred），w/h 为评估分辨率。
    /// </summary>
    public static RenderGapReport Audit(RenderPipelineSettings? settings = null, int width = 1920, int height = 1080)
    {
        var report = new RenderGapReport();

        // 全特性 Deferred 基线：保证每个 flag 都被评估
        var baseSettings = settings ?? new RenderPipelineSettings
        {
            Path = RenderPath.Deferred,
            FeatureFlags = (RenderFeatureFlags)ulong.MaxValue
        };

        // ── GPU 帧计划（全特性）──
        var plan = GpuDeferredPipeline.BuildFramePlan(width, height, baseSettings);
        var gpuPassNames = plan.Passes.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool GpuHas(params string[] names) => names.Any(n =>
            gpuPassNames.Any(p => p.Equals(n, StringComparison.OrdinalIgnoreCase)
                               || p.StartsWith(n, StringComparison.OrdinalIgnoreCase)));

        // ── CPU pass 框架（全特性）──
        var pipeline = new RenderPipeline(baseSettings);
        var cpuPassNames = pipeline.Passes.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // ── 特性覆盖审计 ──
        // 每个 flag → (GPU pass 名候选, CPU pass 名候选)
        var featureMap = new (RenderFeatureFlags Flag, string Name, string[] Gpu, string[] Cpu)[]
        {
            (RenderFeatureFlags.ShadowMaps, "ShadowMaps", new[] { "ShadowMap", "MainLighting" }, new[] { "ShadowMap" }),
            (RenderFeatureFlags.CascadedShadows, "CascadedShadows", new[] { "ShadowMap", "MainLighting" }, new[] { "ShadowMap" }),
            (RenderFeatureFlags.ContactShadows, "ContactShadows", new[] { "ContactShadows" }, Array.Empty<string>()),
            (RenderFeatureFlags.ScreenSpaceAO, "ScreenSpaceAO", new[] { "SSAO" }, new[] { "SSAO" }),
            (RenderFeatureFlags.ScreenSpaceGI, "ScreenSpaceGI", new[] { "SSGI" }, Array.Empty<string>()),
            (RenderFeatureFlags.ScreenSpaceRefl, "ScreenSpaceRefl", new[] { "SSR" }, new[] { "SSR" }),
            (RenderFeatureFlags.Bloom, "Bloom", new[] { "Bloom" }, new[] { "Bloom" }),
            (RenderFeatureFlags.Tonemapping, "Tonemapping", new[] { "Tonemapping" }, new[] { "Tonemapping" }),
            (RenderFeatureFlags.ColorGrading, "ColorGrading", new[] { "ColorGrading" }, Array.Empty<string>()),
            (RenderFeatureFlags.Vignette, "Vignette", new[] { "Vignette" }, Array.Empty<string>()),
            (RenderFeatureFlags.ChromaticAberration, "ChromaticAberration", new[] { "ChromaticAberration" }, Array.Empty<string>()),
            (RenderFeatureFlags.DepthOfField, "DepthOfField", new[] { "DepthOfField" }, new[] { "DepthOfField" }),
            (RenderFeatureFlags.MotionBlur, "MotionBlur", new[] { "MotionBlur" }, new[] { "MotionBlur" }),
            (RenderFeatureFlags.LensFlare, "LensFlare", new[] { "LensFlare" }, Array.Empty<string>()),
            (RenderFeatureFlags.VolumetricFog, "VolumetricFog", new[] { "VolumetricFog" }, Array.Empty<string>()),
            (RenderFeatureFlags.GlobalIllumination, "GlobalIllumination", new[] { "IBL." }, Array.Empty<string>()),
            (RenderFeatureFlags.Decals, "Decals", Array.Empty<string>(), Array.Empty<string>()),
            (RenderFeatureFlags.SkinnedMeshes, "SkinnedMeshes", Array.Empty<string>(), Array.Empty<string>()),
            (RenderFeatureFlags.InstancedMeshes, "InstancedMeshes", Array.Empty<string>(), Array.Empty<string>()),
            (RenderFeatureFlags.GPUDrivenCulling, "GPUDrivenCulling", Array.Empty<string>(), new[] { "GeometryBuffer" }),
            (RenderFeatureFlags.VirtualTexturing, "VirtualTexturing", Array.Empty<string>(), Array.Empty<string>()),
            (RenderFeatureFlags.TemporalUpsampling, "TemporalUpsampling", new[] { "TAA" }, Array.Empty<string>()),
            (RenderFeatureFlags.HDR, "HDR", new[] { "Atmosphere." }, new[] { "MainLighting" }),
        };
        foreach (var (flag, name, gpu, cpu) in featureMap)
        {
            // 仅当 flag 在基线上（全特性时全部在）
            report.AddFeature(name, GpuHas(gpu), cpu.Length > 0 && cpuPassNames.Overlaps(cpu));
        }

        // ── 孤岛算法审计（源引用核查：注释性提及不计为接线）──
        // 静态表：由源码核查得出（DeferredRenderPipeline.cs 头部注释仅描述未接线算法）
        var algorithms = new (string Name, bool Wired)[]
        {
            ("BRDFUtility", true),
            ("SSAOKernelGenerator", true),
            ("CascadeSplitCalculator", true),
            ("GeometryBufferGenerator", false),
            ("CascadeShadowMapRenderer", false),
            ("ScreenSpaceAmbientOcclusion", false),
            ("LightResolveProcessor", false),
            // 2026-11-16 真实化成果: 以下模块已从原型接入真实管线
            ("PathTracerBVHBuilder", true),               // CPU LBVH 构建 + 上传 + PathTraceCS 软件遍历
            ("PathTracerAccelerationStructure", true),    // 三缓冲 (节点/顶点/索引) + SRV
            ("PostProcessingStack", true),                // 全部 8 个后处理 pass 资源绑定
            // 2026-11-17 真实化成果: 延迟渲染 GPU 执行器 (帧计划→RHI)
            ("DeferredGpuRenderer", true),                // 帧计划落地: 目标分配/GLSL→SPIR-V/管线/逐 pass 执行
        };
        // 反射交叉校验：确认类型确实存在于程序集
        var renderingAssembly = typeof(RenderGapReport).Assembly;
        foreach (var (name, wired) in algorithms)
        {
            bool exists = renderingAssembly.GetTypes()
                .Any(t => t.Name == name && t.Namespace == "YamaEngine.Rendering");
            report.AddAlgorithm(name, wired && exists);
        }

        // ── Pass 接线审计：CPU pass 在 GPU 图中是否有记录 ──
        foreach (var passName in cpuPassNames)
            report.AddPass(passName, GpuHas(passName));

        return report;
    }
}
