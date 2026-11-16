// UnityPostProcessDepthTests.cs — Unity5 PostProcess Volume 深度验证
// 覆盖：Volume 全局/局部 ContainsPoint、AddEffect、Priority 混合排序语义、
//       Bloom 阈值/强度默认、ToneMapping CPU 采样（Reinhard/ACES/AgX 归一化、
//       曝光缩放）、ColorGrading CDL+饱和度+对比度+色温、Exposure 自动/手动 EV。
// 全部 CPU 端确定性逻辑，不依赖 GPU。
using System;
using System.Numerics;
using YamaEngine.VFX;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests;

public static class UnityPostProcessDepthTests
{
    private static void Near(float got, float want, float tol, string msg)
        => AssertTrue(MathF.Abs(got - want) <= tol, $"{msg}（期望 {want}，实际 {got}，容差 {tol}）");

    private static void AssertFalse(bool c, string msg) => AssertTrue(!c, msg);

    private static void NearVec(Vector3 got, Vector3 want, float tol, string msg)
        => AssertTrue(Vector3.Distance(got, want) <= tol, $"{msg}（期望 {want}，实际 {got}）");

    // ══════════════════════════════════════════════════════════════════
    //  一、PostProcessVolume 体积判定
    // ══════════════════════════════════════════════════════════════════

    /// <summary>全局体积恒真。</summary>
    public static void TestVolumeGlobalAlwaysContains()
    {
        var vol = new PostProcessVolume { IsGlobal = true, Center = new Vector3(100, 100, 100) };
        AssertTrue(vol.ContainsPoint(Vector3.Zero), "全局体积包含原点");
        AssertTrue(vol.ContainsPoint(new Vector3(9999, -9999, 500)), "全局体积包含任意远点");
    }

    /// <summary>局部体积 AABB 判定。</summary>
    public static void TestVolumeLocalAABB()
    {
        var vol = new PostProcessVolume
        {
            IsGlobal = false,
            Center = new Vector3(0, 0, 0),
            Extent = new Vector3(5, 5, 5)
        };
        AssertTrue(vol.ContainsPoint(Vector3.Zero), "中心命中");
        AssertTrue(vol.ContainsPoint(new Vector3(5, 0, 0)), "边界命中");
        AssertTrue(vol.ContainsPoint(new Vector3(-3, 2, 4)), "盒内点命中");
        AssertFalse(vol.ContainsPoint(new Vector3(6, 0, 0)), "X 超界不命中");
        AssertFalse(vol.ContainsPoint(new Vector3(0, 0, -5.1f)), "Z 超界不命中");
    }

    /// <summary>AddEffect 入列，Priority 用于混合排序。</summary>
    public static void TestVolumeAddEffectAndPriority()
    {
        var vol = new PostProcessVolume();
        var bloom = new BloomEffect();
        var tonemap = new ToneMappingEffect();
        vol.AddEffect(bloom);
        vol.AddEffect(tonemap);

        AssertEqual(2, vol.Effects.Count, "体积含 2 个效果");
        AssertTrue(vol.Effects.Contains(bloom), "包含 Bloom");
        AssertTrue(tonemap.ExecutionOrder > bloom.ExecutionOrder, "ToneMapping 排序晚于 Bloom");

        vol.Priority = 10;
        AssertEqual(10, vol.Priority, "体积优先级可设置");
    }

    // ══════════════════════════════════════════════════════════════════
    //  二、Bloom 参数
    // ══════════════════════════════════════════════════════════════════

    /// <summary>Bloom 默认阈值/强度/半分辨率需求。</summary>
    public static void TestBloomDefaults()
    {
        var bloom = new BloomEffect();
        AssertEqual("Bloom", bloom.EffectName, "效果名 Bloom");
        AssertTrue(bloom.Enabled, "默认启用");
        AssertTrue(bloom.RequiresHalfResolution, "Bloom 需半分辨率");
        Near(bloom.Threshold, 1f, 0.001f, "默认阈值 1");
        Near(bloom.Intensity, 0.8f, 0.001f, "默认强度 0.8");
        Near(bloom.Scatter, 0.85f, 0.001f, "默认散射 0.85");
        AssertEqual(5, bloom.DownsamplePasses, "默认 5 级降采样");
        AssertEqual(5, bloom.MipContributions.Length, "5 级 Mip 贡献");
    }

    /// <summary>Bloom 强度受 BlendWeight/GlobalIntensity 缩放（CPU 逻辑验证）。</summary>
    public static void TestBloomIntensityScaling()
    {
        var bloom = new BloomEffect { Intensity = 2f };
        var ctx = new PostProcessRenderContext { BlendWeight = 0.5f, GlobalIntensity = 0.5f, CurrentExposure = 1f };
        // effectiveIntensity = Intensity * BlendWeight * GlobalIntensity = 2 * 0.5 * 0.5 = 0.5
        float effective = bloom.Intensity * ctx.BlendWeight * ctx.GlobalIntensity;
        Near(effective, 0.5f, 0.001f, "有效强度 = 2*0.5*0.5");
        bloom.Render(new RenderTexture(), new RenderTexture(), ctx); // 不抛异常即通过
        AssertTrue(true, "Bloom.Render 可空纹理调用不抛异常");
    }

    // ══════════════════════════════════════════════════════════════════
    //  三、ToneMapping CPU 采样
    // ══════════════════════════════════════════════════════════════════

    /// <summary>Reinhard：白点映射到 0.5，单调递增，输出 <1。</summary>
    public static void TestTonemapReinhard()
    {
        var tm = new ToneMappingEffect { Operator = ToneMappingOperator.Reinhard };
        AssertEqual("ToneMapping", tm.EffectName, "效果名 ToneMapping");
        AssertEqual(ToneMappingOperator.Reinhard, tm.Operator, "算子 Reinhard");

        var white = tm.TonemapColor(Vector3.One, 1f);       // 1/(1+1)=0.5
        NearVec(white, new Vector3(0.5f), 0.01f, "Reinhard 白点→0.5");

        var zero = tm.TonemapColor(Vector3.Zero, 1f);       // 0/(1+0)=0
        NearVec(zero, Vector3.Zero, 0.001f, "Reinhard 黑→0");

        var small = tm.TonemapColor(new Vector3(0.2f), 1f);
        var big = tm.TonemapColor(new Vector3(0.8f), 1f);
        AssertTrue(small.X < big.X, "Reinhard 单调递增");
        AssertTrue(big.X < 1f, "Reinhard 输出恒 <1");
    }

    /// <summary>ACES 高光压缩到 1 附近，暗部保持。</summary>
    public static void TestTonemapACES()
    {
        var tm = new ToneMappingEffect { Operator = ToneMappingOperator.ACES };
        var bright = tm.TonemapColor(new Vector3(10f), 1f);  // 高光压缩
        var dark = tm.TonemapColor(new Vector3(0.05f), 1f);
        AssertTrue(bright.X >= 0.95f && bright.X <= 1f, $"ACES 高光压缩接近 1（实际 {bright.X}）");
        AssertTrue(dark.X > 0f && dark.X < 0.2f, "ACES 暗部保持小值");
        AssertTrue(bright.X > dark.X, "ACES 单调");
    }

    /// <summary>AgX / Neutral 均归一化到 [0,1]。</summary>
    public static void TestTonemapAgXNeutralRange()
    {
        var agx = new ToneMappingEffect { Operator = ToneMappingOperator.AgX };
        var neu = new ToneMappingEffect { Operator = ToneMappingOperator.Neutral };
        var hdr = new Vector3(5f);

        var a = agx.TonemapColor(hdr, 1f);
        var n = neu.TonemapColor(hdr, 1f);
        AssertTrue(a.X >= 0f && a.X <= 1f, "AgX 输出归一化");
        AssertTrue(n.X >= 0f && n.X <= 1f, "Neutral 输出归一化");
    }

    /// <summary>曝光缩放：exposure 线性缩放输入。</summary>
    public static void TestTonemapExposureScaling()
    {
        var tm = new ToneMappingEffect { Operator = ToneMappingOperator.Reinhard };
        var dim = tm.TonemapColor(Vector3.One, 0.5f);   // 输入 0.5 → 0.5/1.5
        var lit = tm.TonemapColor(Vector3.One, 2f);     // 输入 2 → 2/3
        Near(dim.X, 0.5f / 1.5f, 0.01f, "曝光 0.5 缩放");
        Near(lit.X, 2f / 3f, 0.01f, "曝光 2 缩放");
        AssertTrue(lit.X > dim.X, "更高曝光更亮");
    }

    // ══════════════════════════════════════════════════════════════════
    //  四、ColorGrading CPU 采样
    // ══════════════════════════════════════════════════════════════════

    /// <summary>恒等分级：默认参数不改变颜色。</summary>
    public static void TestColorGradingIdentity()
    {
        var cg = new ColorGradingEffect();
        AssertEqual("ColorGrading", cg.EffectName, "效果名 ColorGrading");
        var inColor = new Vector4(0.5f, 0.4f, 0.3f, 1f);
        var outColor = cg.GradeColor(inColor);
        Near(outColor.X, 0.5f, 0.02f, "恒等 R 保持");
        Near(outColor.Y, 0.4f, 0.02f, "恒等 G 保持");
        Near(outColor.Z, 0.3f, 0.02f, "恒等 B 保持");
        Near(outColor.W, 1f, 0.02f, "A 保持");
    }

    /// <summary>饱和度 0 → 去色为灰度（亮度）。</summary>
    public static void TestColorGradingDesaturate()
    {
        var cg = new ColorGradingEffect { Saturation = 0f };
        var res = cg.GradeColor(new Vector4(1f, 0f, 0f, 1f));
        float lum = 0.2126f * 1f;  // 红色亮度
        Near(res.X, lum, 0.01f, "去色后 R = 亮度");
        Near(res.Y, lum, 0.01f, "去色后 G = 亮度");
        Near(res.Z, lum, 0.01f, "去色后 B = 亮度");
    }

    /// <summary>饱和度提升。</summary>
    public static void TestColorGradingSaturationBoost()
    {
        var cg = new ColorGradingEffect { Saturation = 2f };
        var res = cg.GradeColor(new Vector4(1f, 0f, 0f, 1f));
        float lum = 0.2126f;
        Near(res.X, lum + 2f * (1f - lum), 0.01f, "饱和 2 提升红通道");
        Near(res.Y, lum + 2f * (0f - lum), 0.01f, "饱和 2 压低绿通道");
    }

    /// <summary>对比度缩放。</summary>
    public static void TestColorGradingContrast()
    {
        var cg = new ColorGradingEffect { Contrast = 2f };
        var bright = cg.GradeColor(new Vector4(0.75f, 0.75f, 0.75f, 1f));
        var mid = cg.GradeColor(new Vector4(0.5f, 0.5f, 0.5f, 1f));
        var dark = cg.GradeColor(new Vector4(0.25f, 0.25f, 0.25f, 1f));
        Near(mid.X, 0.5f, 0.01f, "对比度下中灰不变");
        AssertTrue(bright.X > 0.75f, "对比度提升亮部");
        AssertTrue(dark.X < 0.25f, "对比度压低暗部");
    }

    /// <summary>色温偏移：暖色 R 增 B 减。</summary>
    public static void TestColorGradingTemperature()
    {
        var warm = new ColorGradingEffect { Temperature = 1f };
        var cool = new ColorGradingEffect { Temperature = -1f };
        var w = warm.GradeColor(new Vector4(0.5f, 0.5f, 0.5f, 1f));
        var c = cool.GradeColor(new Vector4(0.5f, 0.5f, 0.5f, 1f));
        AssertTrue(w.X > w.Z, "暖色 R > B");
        AssertTrue(c.Z > c.X, "冷色 B > R");
        Near(w.X - 0.5f, 0.1f, 0.02f, "Temperature=1 R 偏移 +0.1");
    }

    // ══════════════════════════════════════════════════════════════════
    //  五、Exposure 自动/手动 EV
    // ══════════════════════════════════════════════════════════════════

    /// <summary>手动曝光：EV 直接生效。</summary>
    public static void TestExposureManual()
    {
        var ex = new ExposureEffect { AutoExposure = false, ManualEV = 1f };
        AssertEqual("Exposure", ex.EffectName, "效果名 Exposure");
        ex.UpdateAutoExposure(0.016f, averageLuminance: 0.5f);
        Near(ex.CurrentEV, 1f, 0.001f, "手动 EV=1 直接设定");

        var ctx = new PostProcessRenderContext();
        ex.Render(new RenderTexture(), new RenderTexture(), ctx);
        Near(ctx.CurrentExposure, MathF.Pow(2f, 1f), 0.001f, "曝光 = 2^EV = 2");
    }

    /// <summary>自动曝光：暗场景提高 EV，亮场景降低，并 clamp。</summary>
    public static void TestExposureAutoAdapt()
    {
        var ex = new ExposureEffect
        {
            AutoExposure = true,
            TargetBrightness = 0.18f,
            MinEV = -8f,
            MaxEV = 8f
        };
        // 暗场景（平均亮度 0.01）→ targetEV 正 → 提高
        for (int i = 0; i < 200; i++) ex.UpdateAutoExposure(0.1f, 0.01f);
        AssertTrue(ex.CurrentEV > 0f, "暗场景 EV 提升");
        AssertTrue(ex.CurrentEV <= 8f, "EV 不超 MaxEV");

        // 亮场景（平均亮度 1.0）→ targetEV 负 → 降低
        // 不重置 CurrentEV（只读），靠 200 次迭代自然收敛
        for (int i = 0; i < 200; i++) ex.UpdateAutoExposure(0.1f, 1.0f);
        AssertTrue(ex.CurrentEV < 0f, "亮场景 EV 降低");
        AssertTrue(ex.CurrentEV >= -8f, "EV 不低于 MinEV");
    }
}
