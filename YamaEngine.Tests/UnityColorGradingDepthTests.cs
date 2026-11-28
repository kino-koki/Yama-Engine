// UnityColorGradingDepthTests.cs — Unity5 ColorGrading / Tonemapping / 后处理效果深度验证
// 覆盖：TonemapOperator/ColorSpace/WhiteBalanceMode/LutResolution 枚举、ColorGradingSettings.Default、
//       ColorGrading 各阶段（WhiteBalance/HueShift/Contrast/Saturation/Vibrance/LiftGammaGain/SMH）、
//       ApplyTonemapping 各算子、OperatorNames/OperatorEval、Bloom/DoF/LensFlare/SSAO/MotionBlur 效果辅助。
using System;
using System.Numerics;
using YamaEngine.Rendering;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests;

public static class UnityColorGradingDepthTests
{
    private static void Near(float want, float got, float tol, string msg)
        => AssertTrue(MathF.Abs(want - got) <= tol, $"{msg}: want {want}, got {got}");

    private static void NearV(Vector3 want, Vector3 got, float tol, string msg)
    {
        AssertTrue(MathF.Abs(want.X - got.X) <= tol &&
                   MathF.Abs(want.Y - got.Y) <= tol &&
                   MathF.Abs(want.Z - got.Z) <= tol,
            $"{msg}: want {want}, got {got}");
    }

    private static ColorGradingSettings Defaults() => ColorGradingSettings.Default;

    // ════════════════════════════════════════════════════════════════
    //  一、枚举
    // ════════════════════════════════════════════════════════════════

    /// <summary>TonemapOperator 11 个算子。</summary>
    public static void TestTonemapOperatorValues()
    {
        AssertEqual(11, Enum.GetValues<TonemapOperator>().Length, "11 tonemap operators");
        AssertEqual(TonemapOperator.Linear, (TonemapOperator)0, "Linear=0");
        AssertEqual(TonemapOperator.Reinhard, (TonemapOperator)1, "Reinhard=1");
        AssertEqual(TonemapOperator.ACES, (TonemapOperator)2, "ACES=2");
        AssertEqual(TonemapOperator.Custom, (TonemapOperator)10, "Custom=10");
    }

    /// <summary>ColorSpace 6 个色彩空间。</summary>
    public static void TestColorSpaceValues()
    {
        AssertEqual(6, Enum.GetValues<ColorSpace>().Length, "6 color spaces");
        AssertEqual(ColorSpace.sRGB, (ColorSpace)0, "sRGB=0");
        AssertEqual(ColorSpace.Linear, (ColorSpace)1, "Linear=1");
        AssertEqual(ColorSpace.Rec709, (ColorSpace)2, "Rec709=2");
        AssertEqual(ColorSpace.Rec2020, (ColorSpace)3, "Rec2020=3");
        AssertEqual(ColorSpace.DCI_P3, (ColorSpace)4, "DCI_P3=4");
        AssertEqual(ColorSpace.ACEScg, (ColorSpace)5, "ACEScg=5");
    }

    /// <summary>WhiteBalanceMode 9 个白平衡预设。</summary>
    public static void TestWhiteBalanceModeValues()
    {
        AssertEqual(9, Enum.GetValues<WhiteBalanceMode>().Length, "9 WB modes");
        AssertEqual(WhiteBalanceMode.Custom, (WhiteBalanceMode)0, "Custom=0");
        AssertEqual(WhiteBalanceMode.Kelvin, (WhiteBalanceMode)8, "Kelvin=8");
    }

    /// <summary>LutResolution 三档分辨率对应数值。</summary>
    public static void TestLutResolutionValues()
    {
        AssertEqual(3, Enum.GetValues<LutResolution>().Length, "3 LUT resolutions");
        AssertEqual(16, (int)LutResolution.Low16, "Low16=16");
        AssertEqual(32, (int)LutResolution.Medium32, "Medium32=32");
        AssertEqual(64, (int)LutResolution.High64, "High64=64");
    }

    // ════════════════════════════════════════════════════════════════
    //  二、ColorGradingSettings 默认值
    // ════════════════════════════════════════════════════════════════

    /// <summary>ColorGradingSettings.Default 中性参数。</summary>
    public static void TestSettingsDefaults()
    {
        var s = Defaults();
        AssertEqual(0f, s.Brightness, "Brightness=0");
        AssertEqual(1f, s.Contrast, "Contrast=1");
        AssertEqual(1f, s.Saturation, "Saturation=1");
        AssertEqual(0f, s.HueShift, "HueShift=0");
        AssertEqual(0f, s.Temperature, "Temperature=0");
        AssertEqual(0f, s.Tint, "Tint=0");
        AssertEqual(Vector3.Zero, s.Lift, "Lift=0");
        AssertEqual(new Vector3(1, 1, 1), s.Gamma, "Gamma=1");
        AssertEqual(new Vector3(1, 1, 1), s.Gain, "Gain=1");
        AssertEqual(TonemapOperator.ACES, s.Tonemap, "Tonemap=ACES");
        AssertEqual(1f, s.Exposure, "Exposure=1");
        AssertEqual(1f, s.GammaValue, "GammaValue=1");
        AssertEqual(0f, s.Vibrance, "Vibrance=0");
        AssertTrue(s.LutTexture is null, "LutTexture=null");
        AssertEqual(1f, s.LutIntensity, "LutIntensity=1");
        AssertTrue(s.Enabled, "Enabled=true");
    }

    // ════════════════════════════════════════════════════════════════
    //  三、ColorGrading 各阶段
    // ════════════════════════════════════════════════════════════════

    private static ColorGrading MakeGrader(ColorGradingSettings s) => new() { Settings = s };

    /// <summary>中性参数下各阶段应为恒等（不变换）。</summary>
    public static void TestNeutralIdentityStages()
    {
        var g = MakeGrader(Defaults());
        var c = new Vector3(0.25f, 0.5f, 0.75f);
        NearV(c, g.ApplyWhiteBalance(c), 1e-4f, "WhiteBalance 中性恒等");
        NearV(c, g.ApplyHueShift(c), 1e-4f, "HueShift 中性恒等");
        NearV(c, g.ApplyContrast(c), 1e-4f, "Contrast 中性恒等");
        NearV(c, g.ApplySaturation(c), 1e-4f, "Saturation 中性恒等");
        NearV(c, g.ApplyVibrance(c), 1e-4f, "Vibrance 中性恒等");
    }

    /// <summary>LiftGammaGain 中性参数（Gain=1/Gamma=1/Lift=0）恒等。</summary>
    public static void TestLiftGammaGainNeutral()
    {
        var g = MakeGrader(Defaults());
        var c = new Vector3(0.2f, 0.4f, 0.6f);
        NearV(c, g.ApplyLiftGammaGain(c), 1e-4f, "LiftGammaGain 中性恒等");
    }

    /// <summary>ApplyWhiteBalance 色温暖化：升 R 降 B。</summary>
    public static void TestWhiteBalanceWarm()
    {
        var s = Defaults(); s.Temperature = 1f;
        var g = MakeGrader(s);
        var c = new Vector3(0.5f, 0.5f, 0.5f);
        var outc = g.ApplyWhiteBalance(c);
        AssertTrue(outc.X > c.X, "暖色温升高 R");
        AssertTrue(outc.Z < c.Z, "暖色温降低 B");
        AssertTrue(outc.X >= 0 && outc.Z >= 0, "结果非负");
    }

    /// <summary>ApplySaturation 灰度饱和：饱和度 0 → 全通道等于亮度。</summary>
    public static void TestSaturationZeroGrayscale()
    {
        var s = Defaults(); s.Saturation = 0f;
        var g = MakeGrader(s);
        var c = new Vector3(0.2f, 0.4f, 0.6f);
        var outc = g.ApplySaturation(c);
        float lum = 0.2f * 0.2126f + 0.4f * 0.7152f + 0.6f * 0.0722f;
        Near(lum, outc.X, 1e-4f, "R=亮度");
        Near(lum, outc.Y, 1e-4f, "G=亮度");
        Near(lum, outc.Z, 1e-4f, "B=亮度");
    }

    /// <summary>ApplyContrast 提高：偏离中点的分量被放大。</summary>
    public static void TestContrastIncrease()
    {
        var s = Defaults(); s.Contrast = 2f; s.GammaValue = 1f; // midpoint=0.5
        var g = MakeGrader(s);
        var c = new Vector3(0.25f, 0.5f, 0.75f);
        var outc = g.ApplyContrast(c);
        // midpoint=0.5^1=0.5; out=(c-0.5)*2+0.5
        Near(0f, outc.X, 1e-4f, "R=(0.25-0.5)*2+0.5=0");
        Near(0.5f, outc.Y, 1e-4f, "G=0.5");
        Near(1f, outc.Z, 1e-4f, "B=1");
    }

    // ════════════════════════════════════════════════════════════════
    //  四、ApplyTonemapping 各算子
    // ════════════════════════════════════════════════════════════════

    /// <summary>Linear 算子：Exposure=1 时恒等。</summary>
    public static void TestTonemapLinearIdentity()
    {
        var s = Defaults(); s.Tonemap = TonemapOperator.Linear; s.Exposure = 1f;
        var g = MakeGrader(s);
        var c = new Vector3(0.3f, 0.5f, 0.7f);
        NearV(c, g.ApplyTonemapping(c), 1e-5f, "Linear Exposure=1 恒等");
    }

    /// <summary>Linear 算子乘 Exposure。</summary>
    public static void TestTonemapLinearExposure()
    {
        var s = Defaults(); s.Tonemap = TonemapOperator.Linear; s.Exposure = 2f;
        var g = MakeGrader(s);
        var c = new Vector3(0.3f, 0.5f, 0.7f);
        NearV(c * 2f, g.ApplyTonemapping(c), 1e-5f, "Linear Exposure=2 线性放大");
    }

    /// <summary>Reinhard 压缩高光：输出低于输入且 <1。</summary>
    public static void TestTonemapReinhard()
    {
        var s = Defaults(); s.Tonemap = TonemapOperator.Reinhard; s.Exposure = 1f;
        var g = MakeGrader(s);
        var c = new Vector3(2f, 2f, 2f);
        var outc = g.ApplyTonemapping(c);
        AssertTrue(outc.X > 0 && outc.X < 1, "Reinhard 输出压到 (0,1)");
        // Reinhard(2)=2/3≈0.667
        Near(2f / 3f, outc.X, 1e-4f, "Reinhard(2)=2/3");
    }

    /// <summary>ACES 压缩到 [0,1]。</summary>
    public static void TestTonemapACES()
    {
        var s = Defaults(); s.Tonemap = TonemapOperator.ACES; s.Exposure = 1f;
        var g = MakeGrader(s);
        var c = new Vector3(1.5f, 1.0f, 0.5f);
        var outc = g.ApplyTonemapping(c);
        AssertTrue(outc.X >= 0 && outc.X <= 1, "ACES R∈[0,1]");
        AssertTrue(outc.Z >= 0 && outc.Z <= 1, "ACES B∈[0,1]");
    }

    /// <summary>None 算子走 default 分支：等同 Linear（乘 Exposure）。</summary>
    public static void TestTonemapNone()
    {
        var s = Defaults(); s.Tonemap = TonemapOperator.None; s.Exposure = 1f;
        var g = MakeGrader(s);
        var c = new Vector3(0.4f, 0.4f, 0.4f);
        NearV(c, g.ApplyTonemapping(c), 1e-5f, "None 恒等（default 分支）");
    }

    /// <summary>OperatorNames 静态表 7 项。</summary>
    public static void TestOperatorNames()
    {
        AssertEqual(7, ColorGrading.OperatorNames.Length, "7 operator names");
        AssertEqual("Linear", ColorGrading.OperatorNames[0], "首个 Linear");
        AssertEqual("Hable", ColorGrading.OperatorNames[^1], "末位 Hable");
    }

    /// <summary>OperatorEval 返回 RGB 三分量。</summary>
    public static void TestOperatorEval()
    {
        var rgb = ColorGrading.OperatorEval(new Vector3(0.5f), TonemapOperator.Linear);
        AssertEqual(3, rgb.Length, "返回 3 分量");
        Near(0.5f, rgb[0], 1e-5f, "R=0.5");
        Near(0.5f, rgb[2], 1e-5f, "B=0.5");
    }

    // ════════════════════════════════════════════════════════════════
    //  五、Bloom / DoF / LensFlare / SSAO / MotionBlur 效果辅助
    // ════════════════════════════════════════════════════════════════

    /// <summary>BloomEffect.GenerateKernel 归一化（和≈1）。</summary>
    public static void TestBloomKernel()
    {
        var b = new BloomEffect();
        var k = b.GenerateKernel(radius: 2, sigma: 1f);
        AssertEqual(5, k.Length, "kernel 长度 2r+1=5");
        float sum = 0; foreach (var v in k) sum += v;
        Near(1f, sum, 1e-3f, "kernel 和≈1");
        // 高斯对称：中心最大
        AssertTrue(k[2] >= k[0] && k[2] >= k[4], "中心权重最大");
    }

    /// <summary>BloomEffect.Prefilter 低于阈值返回 0。</summary>
    public static void TestBloomPrefilter()
    {
        var b = new BloomEffect { Threshold = 1f, SoftThreshold = 0.5f };
        AssertEqual(0f, b.Prefilter(0.3f), "低于 soft 阈值返回 0");
    }

    /// <summary>DepthOfFieldEffect.ComputeCoC 焦点处模糊=0。</summary>
    public static void TestDoFComputeCoC()
    {
        var dof = new DepthOfFieldEffect(); // FocalDistance=10, FocalRegion=3, MaxBlurSize=0.5
        Near(0f, dof.ComputeCoC(depth: 10f, focalDist: 10f, focalRegion: 3f), 1e-5f, "焦点处 CoC=0");
        Near(0.5f, dof.ComputeCoC(depth: 13f, focalDist: 10f, focalRegion: 3f), 1e-4f, "CoC clamp 到 MaxBlurSize");
        Near(dof.MaxBlurSize, dof.ComputeCoC(depth: 100f, focalDist: 10f, focalRegion: 3f), 1e-4f, "远景 clamp 到上限");
    }

    /// <summary>DoF Quality 枚举与默认档位。</summary>
    public static void TestDoFQualityEnum()
    {
        AssertEqual(4, Enum.GetValues<DepthOfFieldEffect.Quality>().Length, "4 quality tiers");
        AssertEqual(DepthOfFieldEffect.Quality.High16Taps, new DepthOfFieldEffect().BlurQuality, "默认 High16Taps");
    }

    /// <summary>LensFlareEffect.GenerateGhostPositions 长度=GhostCount*2。</summary>
    public static void TestLensFlareGhosts()
    {
        var lf = new LensFlareEffect { GhostCount = 5 };
        var pos = lf.GenerateGhostPositions(angle: 0.3f, brightness: 1f);
        AssertEqual(10, pos.Length, "ghost 数组长度 GhostCount*2=10");
    }

    /// <summary>SSAOEffect.GenerateSampleKernel 确定性输出（种子 42）。</summary>
    public static void TestSSAOKernel()
    {
        var s = new SSAOEffect { SampleCount = 16 };
        var k1 = s.GenerateSampleKernel();
        var k2 = s.GenerateSampleKernel();
        AssertEqual(48, k1.Length, "kernel 长度 SampleCount*3=48");
        for (int i = 0; i < k1.Length; i++)
            AssertTrue(MathF.Abs(k1[i] - k2[i]) < 1e-6f, $"kernel 确定性 idx{i}");
    }

    /// <summary>MotionBlurEffect 默认字段。</summary>
    public static void TestMotionBlurDefaults()
    {
        var mb = new MotionBlurEffect();
        AssertEqual(8, mb.SampleCount, "SampleCount=8");
        AssertEqual(1f, mb.Strength, "Strength=1");
        AssertEqual(0.5f, mb.MaxRadius, "MaxRadius=0.5");
        AssertEqual(2, mb.Downsample, "Downsample=2");
        AssertFalse(mb.FullResolution, "FullResolution=false");
    }

    private static void AssertFalse(bool c, string msg) => AssertTrue(!c, msg);
}
