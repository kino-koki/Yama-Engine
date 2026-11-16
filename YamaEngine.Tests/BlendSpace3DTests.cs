// BlendSpace3DTests.cs — BlendSpace3D 三维混合空间闭环验证（UE5.8 深度级 任务C）
// 覆盖：
//   * 查询点在样本点 → 该样本权重 1
//   * 正四面体质心查询 → 四面体重心插值（4 样本各 0.25）
//   * 各查询点（凸包内/外）权重和恒为 1 且非负
//   * 2 样本线段插值 → 姿势混合正确
//   * 凸包外查询 → 最近 4 样本反距离加权
//   * Delaunay 四面体化非空且顶点均为真实样本
// 全部 CPU 级确定性，不依赖 GPU/场景。
using System;
using System.Collections.Generic;
using System.Numerics;
using YamaEngine.Animation.BlendSpaces;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests;

/// <summary>BlendSpace3D 三维混合空间验证套件。</summary>
public sealed class BlendSpace3DTests
{
    /// <summary>查询点恰在某样本点 → 该样本权重 1。</summary>
    public static void TestWeightAtSamplePoint()
    {
        var skeleton = YamaEngine.Animation.Skeleton.CreateBiped("BS3D");
        var bs = new BlendSpace3D(skeleton);
        // 正四面体顶点
        var corners = new[]
        {
            new Vector3(1, 1, 1), new Vector3(1, -1, -1),
            new Vector3(-1, 1, -1), new Vector3(-1, -1, 1)
        };
        for (int i = 0; i < 4; i++) bs.AddSample(MakeClip("C" + i, i), corners[i]);

        var weights = bs.GetSampleWeights(corners[0]);
        AssertEqual(1, weights.Count, "查询点在样本点处应只有 1 个非零权重。");
        AssertTrue(MathF.Abs(weights[0].Weight - 1f) < 0.001f, "该样本权重应为 1。");
        AssertEqual(0, weights[0].Index, "应为样本 0。");
    }

    /// <summary>正四面体质心 → 4 样本各约 0.25（重心插值）。</summary>
    public static void TestTetrahedralBarycentric()
    {
        var skeleton = YamaEngine.Animation.Skeleton.CreateBiped("BS3D");
        var bs = new BlendSpace3D(skeleton);
        var corners = new[]
        {
            new Vector3(1, 1, 1), new Vector3(1, -1, -1),
            new Vector3(-1, 1, -1), new Vector3(-1, -1, 1)
        };
        for (int i = 0; i < 4; i++) bs.AddSample(MakeClip("C" + i, i), corners[i]);

        var weights = bs.GetSampleWeights(Vector3.Zero);
        AssertEqual(4, weights.Count, "质心应混合全部 4 个样本。");
        float sum = 0f;
        foreach (var w in weights)
        {
            AssertTrue(MathF.Abs(w.Weight - 0.25f) < 0.001f, "正四面体质心各权重应为 0.25。");
            sum += w.Weight;
        }
        AssertTrue(MathF.Abs(sum - 1f) < 0.001f, "权重和应为 1。");
    }

    /// <summary>各查询点权重和恒为 1 且非负（凸包内/外）。</summary>
    public static void TestWeightsSumToOneAndNonNegative()
    {
        var skeleton = YamaEngine.Animation.Skeleton.CreateBiped("BS3D");
        var bs = new BlendSpace3D(skeleton);
        var pts = new[]
        {
            new Vector3(2, 0, 0), new Vector3(0, 2, 0), new Vector3(0, 0, 2),
            new Vector3(-2, 0, 0), new Vector3(0, -2, 0), new Vector3(0, 0, -2),
            new Vector3(1, 1, 0), new Vector3(-1, 1, 1)
        };
        for (int i = 0; i < pts.Length; i++) bs.AddSample(MakeClip("C" + i, i), pts[i]);

        var queries = new[]
        {
            new Vector3(0.5f, 0.2f, 0.3f),   // 凸包内
            new Vector3(0, 0, 0),            // 质心
            new Vector3(10, 10, 10),         // 凸包外
            new Vector3(-5, 0, 3),           // 凸包外
            new Vector3(0.9f, -0.4f, 0.6f)   // 凸包内
        };
        foreach (var q in queries)
        {
            var weights = bs.GetSampleWeights(q);
            AssertTrue(weights.Count >= 1 && weights.Count <= 4, "每查询点应返回 1-4 个权重。");
            float sum = 0f;
            foreach (var w in weights)
            {
                AssertTrue(w.Weight >= -0.001f, "权重不应为负。");
                sum += w.Weight;
            }
            AssertTrue(MathF.Abs(sum - 1f) < 0.002f, "权重和应为 1。");
        }
    }

    /// <summary>2 样本线段插值 → 中点姿势混合为两剪辑平均。</summary>
    public static void TestSegmentInterpolationPose()
    {
        var skeleton = YamaEngine.Animation.Skeleton.CreateBiped("BS3D");
        var clip0 = new YamaEngine.Animation.AnimationClip { Name = "A", LengthSeconds = 1f, Looping = true };
        clip0.AddTranslationKey("Hips", 0f, Vector3.Zero);
        clip0.AddTranslationKey("Hips", 1f, Vector3.Zero);
        var clip1 = new YamaEngine.Animation.AnimationClip { Name = "B", LengthSeconds = 1f, Looping = true };
        clip1.AddTranslationKey("Hips", 0f, new Vector3(0, 0, 2));
        clip1.AddTranslationKey("Hips", 1f, new Vector3(0, 0, 2));

        var bs = new BlendSpace3D(skeleton);
        bs.AddSample(clip0, new Vector3(0, 0, 0));
        bs.AddSample(clip1, new Vector3(0, 0, 1));

        var output = bs.Sample(new Vector3(0, 0, 0.5f), 0f);
        int hips = skeleton.FindBone("Hips");
        AssertTrue(MathF.Abs(output.Translations[hips].Z - 1f) < 0.01f,
            "中点采样 Hips 平移应为两剪辑平均（Z=1）。");
        AssertTrue(output.Translations[hips].X < 0.01f && output.Translations[hips].Y < 0.01f,
            "X/Y 分量应保持为 0。");
    }

    /// <summary>凸包外查询 → 最近 4 样本反距离加权（和=1）。</summary>
    public static void TestConvexHullFallback()
    {
        var skeleton = YamaEngine.Animation.Skeleton.CreateBiped("BS3D");
        var bs = new BlendSpace3D(skeleton);
        var pts = new[]
        {
            new Vector3(2, 0, 0), new Vector3(0, 2, 0), new Vector3(0, 0, 2),
            new Vector3(-2, 0, 0), new Vector3(0, -2, 0), new Vector3(0, 0, -2),
            new Vector3(1, 1, 0), new Vector3(-1, 1, 1)
        };
        for (int i = 0; i < pts.Length; i++) bs.AddSample(MakeClip("C" + i, i), pts[i]);

        // 远离所有样本的点
        var weights = bs.GetSampleWeights(new Vector3(50, 50, 50));
        AssertEqual(4, weights.Count, "凸包外应取最近 4 个样本。");
        float sum = 0f;
        foreach (var w in weights) sum += w.Weight;
        AssertTrue(MathF.Abs(sum - 1f) < 0.001f, "反距离权重和应为 1。");
        // 最近的点 (2,0,0) 权重应最大
        AssertTrue(weights[0].Weight >= weights[3].Weight - 1e-4f, "最近样本权重应不小于最远样本。");
    }

    /// <summary>Delaunay 四面体化：非空且顶点均为真实样本。</summary>
    public static void TestTetrahedralizeValid()
    {
        var skeleton = YamaEngine.Animation.Skeleton.CreateBiped("BS3D");
        var bs = new BlendSpace3D(skeleton);
        int n = 6;
        var rng = new Random(1234);
        for (int i = 0; i < n; i++)
        {
            var pos = new Vector3((float)rng.NextDouble() * 4f - 2f,
                                  (float)rng.NextDouble() * 4f - 2f,
                                  (float)rng.NextDouble() * 4f - 2f);
            bs.AddSample(MakeClip("C" + i, i), pos);
        }
        var tets = bs.Tetrahedralize();
        AssertTrue(tets.Count >= 1, "6 个非共面点应至少剖分出 1 个四面体。");
        foreach (var tet in tets)
        {
            AssertTrue(tet.A >= 0 && tet.A < n && tet.B >= 0 && tet.B < n &&
                       tet.C >= 0 && tet.C < n && tet.D >= 0 && tet.D < n,
                "四面体顶点必须全部为真实样本索引。");
        }
    }

    /// <summary>构造单骨骼 Hips 平移的测试剪辑。</summary>
    private static YamaEngine.Animation.AnimationClip MakeClip(string name, int offset)
    {
        var clip = new YamaEngine.Animation.AnimationClip { Name = name, LengthSeconds = 1f, Looping = true };
        var v = new Vector3(0, 0, offset);
        clip.AddTranslationKey("Hips", 0f, v);
        clip.AddTranslationKey("Hips", 1f, v);
        return clip;
    }
}
