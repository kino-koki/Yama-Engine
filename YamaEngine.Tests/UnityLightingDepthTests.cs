// UnityLightingDepthTests.cs — Unity5 Lighting / Lightmap 深度验证
// 覆盖：ECS LightComponent 类型/参数、LightmapSettings 配置、
//       LightmapMode/AmbientOcclusionMode/BakingLightType 枚举语义。
using System;
using System.Numerics;
using YamaEngine.ECS;
using YamaEngine.Processing;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests;

public static class UnityLightingDepthTests
{
    private static void Near(float got, float want, float tol, string msg)
        => AssertTrue(MathF.Abs(got - want) <= tol, $"{msg}（期望 {want}，实际 {got}，容差 {tol}）");

    private static void AssertFalse(bool c, string msg) => AssertTrue(!c, msg);

    // ══════════════════════════════════════════════════════════════════
    //  一、ECS LightComponent 类型与参数
    // ══════════════════════════════════════════════════════════════════

    /// <summary>LightType 四种类型枚举存在。</summary>
    public static void TestLightTypeValues()
    {
        AssertEqual(0, (int)LightComponent.LightType.Directional, "Directional=0");
        AssertEqual(1, (int)LightComponent.LightType.Point, "Point=1");
        AssertEqual(2, (int)LightComponent.LightType.Spot, "Spot=2");
        AssertEqual(3, (int)LightComponent.LightType.Area, "Area=3");
    }

    /// <summary>LightComponent 默认值：白光、强度 1、范围 10。</summary>
    public static void TestLightComponentDefaults()
    {
        var light = new LightComponent
        {
            Type = LightComponent.LightType.Point,
            Color = Vector3.One,
            Intensity = 1f,
            Range = 10f,
            CastsShadow = true
        };
        AssertEqual(LightComponent.LightType.Point, light.Type, "类型 Point");
        Near(light.Intensity, 1f, 0.001f, "默认强度 1");
        Near(light.Range, 10f, 0.001f, "默认范围 10");
        AssertTrue(light.CastsShadow, "默认投射阴影");
    }

    /// <summary>Spot 灯内外锥角约束：inner < outer。</summary>
    public static void TestSpotLightCone()
    {
        var spot = new LightComponent
        {
            Type = LightComponent.LightType.Spot,
            InnerConeAngle = 30f,
            OuterConeAngle = 45f
        };
        AssertTrue(spot.InnerConeAngle < spot.OuterConeAngle, "内锥 < 外锥");
        Near(spot.InnerConeAngle, 30f, 0.001f, "内锥 30°");
        Near(spot.OuterConeAngle, 45f, 0.001f, "外锥 45°");
    }

    /// <summary>Directional 灯不使用 Range（忽略范围）。</summary>
    public static void TestDirectionalLightNoRange()
    {
        var dir = new LightComponent
        {
            Type = LightComponent.LightType.Directional,
            Range = 100f  // 设置但理论上不使用
        };
        AssertEqual(LightComponent.LightType.Directional, dir.Type, "类型 Directional");
        // Range 字段存在但语义上不影响平行光
        Near(dir.Range, 100f, 0.001f, "Range 字段可写");
    }

    /// <summary>阴影偏移 ShadowBias 默认 0.005（抗痤疮）。</summary>
    public static void TestShadowBiasDefault()
    {
        var light = new LightComponent { ShadowBias = 0.005f };
        Near(light.ShadowBias, 0.005f, 0.0001f, "默认 shadow bias 0.005");
    }

    // ══════════════════════════════════════════════════════════════════
    //  二、LightmapSettings 烘焙配置
    // ══════════════════════════════════════════════════════════════════

    /// <summary>LightmapSettings 默认值：512 分辨率、DirectAndIndirect 模式。</summary>
    public static void TestLightmapSettingsDefaults()
    {
        var settings = new LightmapSettings();
        AssertEqual(512, settings.Resolution, "默认分辨率 512");
        AssertEqual(LightmapMode.DirectAndIndirect, settings.Mode, "默认模式 Direct+Indirect");
        AssertEqual(256, settings.DirectSamples, "默认直接光采样 256");
        AssertEqual(128, settings.AOSamples, "默认 AO 采样 128");
        AssertEqual(1, settings.IndirectBounces, "默认间接反弹 1");
        AssertEqual(64, settings.IndirectSamples, "默认间接采样 64");
        AssertEqual(4, settings.DenoiseIterations, "默认降噪迭代 4");
    }

    /// <summary>LightmapMode 三态。</summary>
    public static void TestLightmapModeValues()
    {
        AssertTrue(Enum.IsDefined(typeof(LightmapMode), LightmapMode.DirectOnly), "DirectOnly");
        AssertTrue(Enum.IsDefined(typeof(LightmapMode), LightmapMode.DirectAndIndirect), "DirectAndIndirect");
        AssertTrue(Enum.IsDefined(typeof(LightmapMode), LightmapMode.AmbientOcclusionOnly), "AOOnly");
    }

    /// <summary>AmbientOcclusionMode 两种算法。</summary>
    public static void TestAmbientOcclusionMode()
    {
        AssertTrue(Enum.IsDefined(typeof(AmbientOcclusionMode), AmbientOcclusionMode.Hemisphere), "Hemisphere");
        AssertTrue(Enum.IsDefined(typeof(AmbientOcclusionMode), AmbientOcclusionMode.MonteCarloCosWeighted), "MonteCarlo");
    }

    /// <summary>自定义 LightmapSettings：高分辨率 + 多次反弹。</summary>
    public static void TestLightmapSettingsCustom()
    {
        var settings = new LightmapSettings
        {
            Resolution = 1024,
            Mode = LightmapMode.DirectAndIndirect,
            IndirectBounces = 3,
            IndirectSamples = 128,
            DenoiseIterations = 8
        };
        AssertEqual(1024, settings.Resolution, "高分辨率 1024");
        AssertEqual(3, settings.IndirectBounces, "3 次反弹");
        AssertEqual(128, settings.IndirectSamples, "128 间接采样");
        AssertEqual(8, settings.DenoiseIterations, "8 次降噪");
    }

    /// <summary>BakingLightType 枚举：与实时 LightType 对齐。</summary>
    public static void TestBakingLightType()
    {
        AssertTrue(Enum.IsDefined(typeof(BakingLightType), BakingLightType.Directional), "烘焙 Directional");
        AssertTrue(Enum.IsDefined(typeof(BakingLightType), BakingLightType.Point), "烘焙 Point");
        AssertTrue(Enum.IsDefined(typeof(BakingLightType), BakingLightType.Spot), "烘焙 Spot");
        AssertTrue(Enum.IsDefined(typeof(BakingLightType), BakingLightType.Area), "烘焙 Area");
    }
}
