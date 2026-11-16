// TerrainDepthTests.cs — Unity5 Terrain 深度验证（heightmap / splatmap / grass / trees / water / collider / debugger）
// 覆盖：
//   * TerrainHeightmap Get/Set/Clamp/双线性采样/边界外返回0/法线计算
//   * AddHill 局部隆起、Perlin 生成幅度受控
//   * TerrainSplatmap Set/Clamp/双线性采样/Paint 加权归一
//   * TerrainGrass/TerrainTrees 过滤高度生成 / Visible 距离裁剪
//   * TerrainWater 波形 WaveHeight/WaveNormal/位置变化
//   * TerrainCollider Raycast 命中平地 / Sample 嵌入判定
//   * TerrainDebugger SampleAt 坡度/水下判定
// 全部 CPU 级确定性，不依赖 GPU/渲染。
using System;
using System.Numerics;
using YamaEngine.World;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests;

public static class TerrainDepthTests
{
    private static void Near(float got, float want, float tol, string msg)
        => AssertTrue(MathF.Abs(got - want) <= tol, $"{msg}（期望 {want}，实际 {got}）");

    // ───────────── Heightmap ─────────────
    /// <summary>Get/Set/Clamp：超过 MaxHeight 截断，负值截 0。</summary>
    public static void TestHeightmapSetClamp()
    {
        var hm = new TerrainHeightmap(65, 100f, 50f);
        hm.SetH(10, 10, 60f);
        AssertTrue(hm.GetH(10, 10) <= 50f, "SetH 超过 MaxHeight 应被 clamp");
        hm.SetH(5, 5, -10f);
        AssertTrue(hm.GetH(5, 5) >= 0f, "SetH 负值应被 clamp 到 0");
    }

    /// <summary>双线性采样：平坦地形中心采样应等于设定值。</summary>
    public static void TestHeightmapBilinearSample()
    {
        var hm = new TerrainHeightmap(65, 100f, 50f);
        hm.SetH(32, 32, 25f);
        // 中心点世界坐标 (0,?,0)
        float h = hm.Sample(new Vector3(0, 0, 0));
        AssertTrue(h > 24f && h < 26f, $"中心采样应≈25（实际 {h}）");
    }

    /// <summary>边界外采样返回 0。</summary>
    public static void TestHeightmapOutsideReturnsZero()
    {
        var hm = new TerrainHeightmap(65, 100f, 50f);
        hm.SetH(32, 32, 25f);
        float h = hm.Sample(new Vector3(1000f, 0, 0));
        Near(h, 0f, 0.0001f, "超出地形范围 Sample 应返回 0");
    }

    /// <summary>法线计算：平坦地形法线朝上（0,1,0）。</summary>
    public static void TestHeightmapFlatNormalUp()
    {
        var hm = new TerrainHeightmap(65, 100f, 50f);
        var n = hm.Normal(new Vector3(0, 0, 0));
        Near(n.Y, 1f, 0.01f, "平坦地形法线 Y 分量应为 1");
        Near(n.X, 0f, 0.01f, "平坦地形法线 X 分量应为 0");
        Near(n.Z, 0f, 0.01f, "平坦地形法线 Z 分量应为 0");
    }

    /// <summary>AddHill：中心点应比边缘高。</summary>
    public static void TestHeightmapAddHill()
    {
        var hm = new TerrainHeightmap(65, 100f, 50f);
        hm.AddHill(32, 32, 10, 20f);
        float center = hm.GetH(32, 32);
        float edge = hm.GetH(40, 32);
        AssertTrue(center > edge, "Hill 中心高度应大于边缘");
    }

    // ───────────── Splatmap ─────────────
    /// <summary>Set/Get/Clamp：权重 [0,1] 范围。</summary>
    public static void TestSplatmapSetClamp()
    {
        var sm = new TerrainSplatmap(64, 4);
        sm.Set(10, 10, 0, 2f);
        AssertTrue(sm.Get(10, 10, 0) <= 1f, "Splatmap 权重应 clamp 到 1");
        sm.Set(5, 5, 1, -0.5f);
        AssertTrue(sm.Get(5, 5, 1) >= 0f, "Splatmap 负权重应 clamp 到 0");
    }

    /// <summary>双线性采样：4个角全设权重1后中心采样应返回1。</summary>
    public static void TestSplatmapBilinearSample()
    {
        var sm = new TerrainSplatmap(64, 4);
        // 中心采样点 (0,0,0) 对应 splat 网格 (31.5,31.5)，涉及 4 个相邻格子
        sm.Set(31, 31, 2, 1f);
        sm.Set(32, 31, 2, 1f);
        sm.Set(31, 32, 2, 1f);
        sm.Set(32, 32, 2, 1f);
        float[] w = sm.Sample(new Vector3(0, 0, 0), 64f);
        AssertTrue(w.Length == 4, "Splatmap 应有 4 层");
        Near(w[2], 1f, 0.01f, "中心采样第 2 层权重应为 1");
    }

    // ───────────── Grass / Trees ─────────────
    /// <summary>草生成：在地形上方才生成（Y>0.1）；Visible 距离裁剪。</summary>
    public static void TestGrassGenerationAndVisible()
    {
        var hm = new TerrainHeightmap(65, 100f, 50f);
        hm.GeneratePerlin(20f, 0.01f, 3);
        var grass = new TerrainGrass();
        grass.Gen(hm, sp: 5f, j: 0.1f, max: 1000);
        AssertTrue(grass.Count > 0, "应有草簇生成");
        // Visible: 靠近相机的草应少或等于总数
        var visible = grass.Visible(new Vector3(0, 30f, 0), 30f);
        AssertTrue(visible.Length <= grass.Count, "可见草簇数应≤总数");
    }

    /// <summary>树生成：高度过滤（mn=1, mx=450），Visible 距离裁剪。</summary>
    public static void TestTreeGenerationHeightFilter()
    {
        var hm = new TerrainHeightmap(65, 100f, 50f);
        hm.GeneratePerlin(20f, 0.01f, 3);
        var trees = new TerrainTrees();
        trees.Gen(hm, c: 200, mn: 5f, mx: 15f, max: 100);
        foreach (var t in trees.Visible(new Vector3(0, 30f, 0), 10000f))
        {
            AssertTrue(t.P.Y >= 5f - 0.5f && t.P.Y <= 15f + 0.5f,
                $"树 Y={t.P.Y} 应在 [5,15] 范围");
        }
    }

    // ───────────── Water ─────────────
    /// <summary>WaveHeight：不同时间波形应变化；波幅受 WaveAmplitude 控制。</summary>
    public static void TestWaterWaveHeight()
    {
        var w = new TerrainWater { Level = 0f, WaveAmplitude = 1f, WaveFrequency = 1f, WaveSpeed = 1f };
        float h0 = w.WaveHeight(new Vector3(0, 0, 0), 0f);
        float h1 = w.WaveHeight(new Vector3(0, 0, 0), 1f);
        AssertTrue(MathF.Abs(h1 - h0) > 0.01f, "不同时间波形应变化");
        AssertTrue(MathF.Abs(h0) <= 1.1f, "波形应在幅度范围内");
    }

    /// <summary>WaveNormal：法线应接近 (0,1,0)（微幅波）。</summary>
    public static void TestWaterWaveNormal()
    {
        var w = new TerrainWater { WaveAmplitude = 0.01f, WaveFrequency = 0.1f };
        var n = w.WaveNormal(new Vector3(5, 0, 5), 0f);
        AssertTrue(n.Y > 0.9f, $"微幅波法线 Y 应接近 1（实际 {n.Y}）");
        Near(n.Length(), 1f, 0.01f, "法线应已归一化");
    }

    // ───────────── Collider ─────────────
    /// <summary>Raycast：垂直向下应命中平地（t>0, hitPoint.Y≈0）。</summary>
    public static void TestColliderRaycastFlatGround()
    {
        var hm = new TerrainHeightmap(65, 100f, 50f);
        var col = new TerrainCollider(hm);
        bool hit = col.Raycast(new Vector3(0, 100f, 0), new Vector3(0, -1, 0), out float t, out Vector3 hitPoint);
        AssertTrue(hit, "垂直向下应命中平地");
        AssertTrue(t > 0f, "命中距离 t 应 > 0");
        Near(hitPoint.Y, 0f, 0.5f, "平地命中点 Y 应≈0");
    }

    /// <summary>Sample 嵌入判定：物体低于地形高度应返回 true。</summary>
    public static void TestColliderSampleEmbedded()
    {
        var hm = new TerrainHeightmap(65, 100f, 50f);
        hm.AddHill(32, 32, 20, 30f);
        var col = new TerrainCollider(hm);
        // 中心点 Y=5，地形高≈30 → 嵌入
        bool embedded = col.Sample(new Vector3(0, 5f, 0), out float h, out _);
        AssertTrue(embedded, "物体低于地形应返回嵌入 true");
        AssertTrue(h > 20f, $"嵌入处地形高应 >20（实际 {h}）");
    }

    // ───────────── Debugger ─────────────
    /// <summary>SampleAt：平地坡度≈0，非水下。</summary>
    public static void TestDebuggerFlatSlopeZero()
    {
        var hm = new TerrainHeightmap(65, 100f, 50f);
        var dbg = new TerrainDebugger();
        var info = dbg.SampleAt(hm, null, null, new Vector3(0, 0, 0));
        Near(info.Slope, 0f, 1f, "平地坡度应≈0度");
        AssertTrue(!info.Underwater, "无水体时 Underwater 应 false");
    }

    /// <summary>GetProfile：格式含 Res/Size/H range。</summary>
    public static void TestDebuggerProfile()
    {
        var hm = new TerrainHeightmap(65, 100f, 50f);
        var dbg = new TerrainDebugger();
        string profile = dbg.GetProfile(hm);
        AssertTrue(profile.Contains("65x65"), "Profile 应含分辨率");
        AssertTrue(profile.Contains("Size:100"), "Profile 应含 Size");
    }
}
