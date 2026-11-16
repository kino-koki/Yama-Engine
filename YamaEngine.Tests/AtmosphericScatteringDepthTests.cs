// AtmosphericScatteringDepthTests.cs — Unity5 大气散射深度验证
// 覆盖：
//   * RayleighScattering 截面 λ⁻⁴、相位函数对称、光学深度
//   * MieScattering Henyey-Greenstein 前向峰值、双 lobe 平衡
//   * AtmosphericOptics Transmittance Beer-Lambert、SkyColor 地平线增强
//   * TransmittanceLUTGenerator 生成
// 全部纯 CPU 级确定性。
using System;
using System.Numerics;
using YamaEngine.Rendering;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests;

public static class AtmosphericScatteringDepthTests
{
    /// <summary>Rayleigh 截面基准值：550nm 处 RGB=(5.8,13.5,33.1)e-6，蓝光>红光。</summary>
    public static void TestRayleighBlueScattersMore()
    {
        // 550nm 基准：ratio=(550/550)^4=1，返回预计算的 Rayleigh 散射系数
        var cross = RayleighScattering.ScatteringCrossSection(new Vector3(550f));
        float expectedRatio = MathF.Pow(680f / 440f, 4);
        float actualRatio = cross.Z / cross.X; // 蓝/红
        AssertTrue(actualRatio > expectedRatio * 0.8f,
            $"蓝/红散射比({actualRatio:F2})应≈λ⁻⁴({expectedRatio:F2})");
        AssertTrue(cross.Z > cross.X * 5f, $"蓝光({cross.Z})应远>红光({cross.X})");
    }

    /// <summary>Rayleigh 相位函数对称：phase(1)≈phase(-1)，均大于 phase(0)。</summary>
    public static void TestRayleighPhaseSymmetric()
    {
        float p0 = RayleighScattering.Phase(0f);
        float pFwd = RayleighScattering.Phase(1f);
        float pBack = RayleighScattering.Phase(-1f);
        AssertTrue(MathF.Abs(pFwd - pBack) < 1e-6f, "Rayleigh 相位应前后对称");
        AssertTrue(pFwd > p0, $"前向({pFwd})应>垂直({p0})");
        // 理论值：phase(1) = (3/16π)(1+1) = 3/(8π) ≈ 0.1194
        AssertTrue(MathF.Abs(pFwd - 3f / (8f * MathF.PI)) < 1e-5f, "phase(1) 应≈3/(8π)");
    }

    /// <summary>Rayleigh 光学深度：height=0, baseDensity=1, pathLength=1 → OD=1；高海拔密度衰减。</summary>
    public static void TestRayleighOpticalDepthZeroHeight()
    {
        float od = RayleighScattering.OpticalDepth(0f, 8500f, 1f, 1f);
        AssertTrue(MathF.Abs(od - 1f) < 1e-5f, $"OD 应=1（实际 {od}）");
        // 8500m 高度处 OD≈e⁻¹≈0.368
        float odHigh = RayleighScattering.OpticalDepth(8500f, 8500f, 1f, 1f);
        AssertTrue(odHigh < od * 0.4f, $"8500m 处 OD 应≈e⁻¹≈0.368（实际 {odHigh}）");
    }

    /// <summary>Mie Henyey-Greenstein 前向峰值：phase(1) >> phase(-1) for g=0.76。</summary>
    public static void TestMiePhaseForwardPeak()
    {
        float g = 0.76f;
        float pFwd = MieScattering.Phase(1f, g);
        float pBack = MieScattering.Phase(-1f, g);
        AssertTrue(pFwd > pBack * 5f, $"Mie 前向({pFwd})应远>后向({pBack})");
    }

    /// <summary>Mie 双 lobe：等权重时 = 简单 HG 的算术平均。</summary>
    public static void TestMieDualLobeEqualWeight()
    {
        float cosTheta = 0.5f;
        float gF = 0.8f, gB = -0.5f;
        float dual = MieScattering.DualLobePhase(cosTheta, gF, gB, 0.5f);
        float expected = 0.5f * MieScattering.Phase(cosTheta, gF) + 0.5f * MieScattering.Phase(cosTheta, gB);
        AssertTrue(MathF.Abs(dual - expected) < 1e-5f, $"双 lobe 等权应=平均（实际 {dual} vs {expected}）");
    }

    /// <summary>Transmittance Beer-Lambert：OD=0→T=1，OD=1→T≈e⁻¹≈0.368。</summary>
    public static void TestTransmittanceBeerLambert()
    {
        var t0 = AtmosphericOptics.Transmittance(Vector3.Zero);
        AssertTrue(MathF.Abs(t0.X - 1f) < 1e-5f, "OD=0 透射率应为 1");

        var t1 = AtmosphericOptics.Transmittance(Vector3.One);
        float expected = MathF.Exp(-1f);
        AssertTrue(MathF.Abs(t1.X - expected) < 1e-5f, $"OD=1 透射率应≈e⁻¹≈{expected:F4}（实际 {t1.X}）");
    }

    /// <summary>SkyColor 地平线增强：地平线方向亮度 > 天顶方向。</summary>
    public static void TestSkyColorHorizonBrighter()
    {
        var cfg = AtmosphericConfig.Default;
        var sunDir = cfg.GetSunDirection();
        var sunColor = Vector3.One * cfg.SunIntensity;

        // 天顶方向（正上方）
        var zenith = AtmosphericOptics.ComputeSkyColor(
            new Vector3(0, 1, 0), sunDir, sunColor,
            cfg.RayleighScatteringColor, cfg.MieScatteringColor, cfg.MieG,
            cfg.RayleighHeightScale, cfg.MieHeightScale);
        // 地平线方向（正前方，微抬）
        var horizon = AtmosphericOptics.ComputeSkyColor(
            new Vector3(0, 0.01f, 1f), sunDir, sunColor,
            cfg.RayleighScatteringColor, cfg.MieScatteringColor, cfg.MieG,
            cfg.RayleighHeightScale, cfg.MieHeightScale);

        AssertTrue(horizon.Length() > zenith.Length(),
            $"地平线亮度({horizon.Length()})应>天顶亮度({zenith.Length()})");
    }

    /// <summary>TransmittanceLUT 生成：尺寸正确，透射率值∈(0,1]。</summary>
    public static void TestTransmittanceLUTGenerate()
    {
        int size = 8;
        var lut = TransmittanceLUTGenerator.Generate(size, AtmosphericConfig.Default);
        AssertTrue(lut.Length == size * size, $"LUT 长度应={size * size}（实际 {lut.Length}）");
        for (int i = 0; i < lut.Length; i++)
        {
            AssertTrue(lut[i].X > 0f && lut[i].X <= 1f,
                $"LUT[{i}].X 透射率应在(0,1]（实际 {lut[i].X}）");
        }
    }
}
