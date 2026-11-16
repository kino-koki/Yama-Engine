// GIDepthTests.cs — Unity5 Global Illumination Probes 深度验证（球谐 / GIProbe / ProbeGrid 三线性采样 / 探针剔除 / 探针混合）
// 覆盖：
//   * SphericalHarmonics 基函数 Y00 常数、SH9 共 9 个系数、EvaluateSH4 共 4 个
//   * Project → EvaluateIrradiance → NormalizeSH 闭环：均匀环境光重建后各方向辐照度相等
//   * 方向性环境光投影后对应法线方向辐照度更高（SH 低频方向性）
//   * GIProbe.Accumulate/QueryIrradiance/NormalizeSH 访问器路径
//   * ProbeGrid 构造分辨率/ProbeCount、SampleIrradianceTrilinear 边界外回退
//   * ProbeGrid.CullEmbeddedProbes 剔除实体内部探针
//   * ProbeBlending 权重混合可见性（穿墙衰减）
// 全部 CPU 级确定性，不依赖 GPU/渲染设备。
using System;
using System.Numerics;
using YamaEngine.Rendering;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests;

public static class GIDepthTests
{
    private static void Near(float got, float want, float tol, string msg)
        => AssertTrue(MathF.Abs(got - want) <= tol, $"{msg}（期望 {want}，实际 {got}）");

    /// <summary>SH9 基函数：Y00 恒为 0.282095（归一化常数）；Z+ 方向 Y10 分量最大。</summary>
    public static void TestSH9BasisConstants()
    {
        var y = SphericalHarmonics.EvaluateSH9(new Vector3(0, 0, 1));
        AssertTrue(y.Length == 9, "SH9 应有 9 个基函数");
        Near(y[0], 0.282095f, 1e-4f, "Y00 归一化常数");
        // Y10 = 0.488603*z，z=1 → 最大
        Near(y[2], 0.488603f, 1e-4f, "Y10 在 Z+ 方向");
        Near(y[1], 0f, 1e-5f, "Y1-1 在 Z+ 方向应为 0（含 y 分量）");
    }

    /// <summary>SH4 基函数共 4 个。</summary>
    public static void TestSH4BasisCount()
    {
        var y = SphericalHarmonics.EvaluateSH4(Vector3.UnitY);
        AssertTrue(y.Length == 4, "SH4 应有 4 个基函数");
    }

    /// <summary>均匀环境光：全方向投影 → 归一化 → 任意法线重建辐照度均相等。</summary>
    public static void TestUniformIrradianceReconstruction()
    {
        var sh = new float[27];
        var dirs = new[] { Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ };
        const float rad = 1.0f;
        foreach (var d in dirs)
            SphericalHarmonics.Project(sh, d, new Vector3(rad), SHOrder.Order3);
        SphericalHarmonics.Normalize(sh, dirs.Length, SHOrder.Order3);

        // 均匀环境光 → 各方向辐照度应基本相等
        var up = SphericalHarmonics.EvaluateIrradiance(sh, Vector3.UnitY, SHOrder.Order3);
        var side = SphericalHarmonics.EvaluateIrradiance(sh, Vector3.UnitX, SHOrder.Order3);
        Near(up.X, side.X, 0.05f, "均匀环境光各方向辐照度应相等");
        AssertTrue(up.X > 0f, "均匀环境光辐照度应为正");
    }

    /// <summary>方向性环境光：Z+ 强光照 → Z+ 法线辐照度 > X+ 法线。</summary>
    public static void TestDirectionalIrradiance()
    {
        var sh = new float[27];
        // 在 Z+ 半球多点采样强光照
        for (int i = 0; i < 100; i++)
        {
            float phi = i * 0.618f * MathF.PI * 2;
            float cost = 0.2f + 0.8f * (i / 100f); // 偏向 Z+
            float sint = MathF.Sqrt(1 - cost * cost);
            var d = new Vector3(sint * MathF.Cos(phi), sint * MathF.Sin(phi), cost);
            SphericalHarmonics.Project(sh, d, Vector3.One * 5f, SHOrder.Order3);
        }
        SphericalHarmonics.Normalize(sh, 100, SHOrder.Order3);

        var front = SphericalHarmonics.EvaluateIrradiance(sh, Vector3.UnitZ, SHOrder.Order3);
        var right = SphericalHarmonics.EvaluateIrradiance(sh, Vector3.UnitX, SHOrder.Order3);
        AssertTrue(front.X > right.X, $"Z+ 光照方向 Z+ 法线辐照度({front.X})应 > X+ 法线({right.X})");
    }

    /// <summary>GIProbe 访问器：Accumulate → NormalizeSH → QueryIrradiance。</summary>
    public static void TestGIProbeAccumulateQuery()
    {
        var probe = new GIProbe();
        probe.Position = Vector3.Zero;
        probe.Range = 10f;
        probe.IsActive = 1;
        for (int i = 0; i < 8; i++)
        {
            var d = new Vector3(MathF.Cos(i * 0.785f), 0.5f, MathF.Sin(i * 0.785f));
            probe.Accumulate(d, Vector3.One, SHOrder.Order3);
        }
        AssertTrue(probe.AccumulatedSamples == 8, "累积采样数应为 8");
        probe.NormalizeSH(SHOrder.Order3);
        var irr = probe.QueryIrradiance(Vector3.UnitY, SHOrder.Order3);
        AssertTrue(irr.X > 0f, "探针重建辐照度应为正");
    }

    /// <summary>ProbeGrid 构造：分辨率/ProbeCount/Extent。</summary>
    public static void TestProbeGridConstruction()
    {
        using var grid = new ProbeGrid(new Vector3(2, 2, 2), new Vector3(10, 5, 10), SHOrder.Order3);
        AssertTrue(grid.ProbeCount == 8, "2x2x2 网格应有 8 个探针");
        Near(grid.Extent.X, 10f, 1e-5f, "Extent.X");
        AssertTrue(grid.Order == SHOrder.Order3, "SH 阶数应为 Order3");
    }

    /// <summary>SampleIrradianceTrilinear：网格外位置回退到边界探针，不崩溃。</summary>
    public static void TestProbeGridSampleClamped()
    {
        using var grid = new ProbeGrid(new Vector3(2, 2, 2), new Vector3(10, 5, 10));
        // 初始化探针为均匀环境光
        for (int i = 0; i < grid.ProbeCount; i++)
        {
            ref var p = ref grid.GetProbe(i);
            for (int s = 0; s < 6; s++)
                p.Accumulate(Vector3.Normalize(new Vector3(s - 2.5f, 1, 2 - s)), Vector3.One, SHOrder.Order3);
            p.NormalizeSH(SHOrder.Order3);
        }
        // 网格内
        var inner = grid.SampleIrradianceTrilinear(Vector3.Zero, Vector3.UnitY);
        AssertTrue(inner.X > 0f, "网格内采样应为正");
        // 远超边界
        var outer = grid.SampleIrradianceTrilinear(new Vector3(1000, 1000, 1000), Vector3.UnitY);
        AssertTrue(outer.X >= 0f, "网格外采样应 clamp 不崩溃且非负");
    }

    /// <summary>CullEmbeddedProbes：实体内部探针被剔除，外部保留。</summary>
    public static void TestProbeGridCullEmbedded()
    {
        using var grid = new ProbeGrid(new Vector3(3, 1, 3), new Vector3(10, 1, 10));
        // 中心探针位于原点，视为在实体内（半径 5）
        grid.CullEmbeddedProbes(pos => pos.Length() < 5f);
        int inactive = 0, active = 0;
        for (int i = 0; i < grid.ProbeCount; i++)
        {
            if (grid.GetProbe(i).IsActive == 0) inactive++;
            else active++;
        }
        AssertTrue(inactive > 0, "应有探针被剔除（位于原点实体内）");
        AssertTrue(active > 0, "应有探针保留（远离原点）");
        AssertTrue(grid.ActiveProbeIndices.Count == active, "活跃索引数应等于活跃探针数");
    }

    /// <summary>ProbeBlending 穿墙可见性：设置墙两侧不可见后影响混合。</summary>
    public static void TestProbeBlendingWallVisibility()
    {
        var blending = new ProbeBlending
        {
            Mode = ProbeBlendMode.Weighted,
            WallAttenuation = 0.1f
        };
        var idA = Guid.NewGuid();
        var idB = Guid.NewGuid();
        blending.SetProbeVisibility(idA, idB, 0.0f); // 墙体两侧不可见
        float vis = blending.GetProbeVisibility(idA, idB);
        Near(vis, 0.0f, 1e-4f, "墙两侧可见性应为 0");
    }
}
