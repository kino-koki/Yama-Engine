// BlendTree2DModeTests.cs —— Unity 5 Mecanim BlendTree 2D 混合模式深度验证
// 覆盖 SimpleDirectional2D（幅值缩放）/ FreeformDirectional2D（角度加权）/ FreeformCartesian2D（笛卡尔重心）
using System;
using System.Numerics;
using YamaEngine.Animation;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests;

/// <summary>BlendTree 2D 混合模式（Unity 5 Mecanim 语义）验证套件。</summary>
public sealed class BlendTree2DModeTests
{
    /// <summary>构造单骨骼 Hips 平移的测试剪辑，平移向量与 offset 成正比。</summary>
    private static AnimationClip MakeClip(string name, float offset)
    {
        var clip = new AnimationClip { Name = name, LengthSeconds = 1f, Looping = true };
        var v = new Vector3(0, 0, offset);
        clip.AddTranslationKey("Hips", 0f, v);
        clip.AddTranslationKey("Hips", 1f, v);
        return clip;
    }

    private static Skeleton MakeSkeleton() => Skeleton.CreateBiped("BT2D");

    // ── SimpleDirectional2D：零向量（幅值 0）→ 无运动贡献，权重全 0 ──
    public static void TestSimpleDirectionalZeroVectorNoMotion()
    {
        var skeleton = MakeSkeleton();
        var bt = new BlendTree("Move", BlendTreeType.SimpleDirectional2D)
        {
            BlendParameter = "x", BlendParameterY = "y",
        };
        bt.AddChild(MakeClip("F", 1f), new Vector2(0, 1));  // 前
        bt.AddChild(MakeClip("B", 1f), new Vector2(0, -1)); // 后
        bt.AddChild(MakeClip("L", 1f), new Vector2(-1, 0)); // 左
        bt.AddChild(MakeClip("R", 1f), new Vector2(1, 0));  // 右
        bt.ParameterValueX = 0f;
        bt.ParameterValueY = 0f;

        var weights = bt.GetWeights();
        float sum = 0f;
        foreach (var w in weights) sum += w;
        AssertTrue(MathF.Abs(sum) < 0.01f, $"零向量应无运动（权重和≈0），got {sum}。");
    }

    // ── SimpleDirectional2D：幅值缩放整体权重 ──
    public static void TestSimpleDirectionalMagnitudeScale()
    {
        var skeleton = MakeSkeleton();
        var bt = new BlendTree("Move", BlendTreeType.SimpleDirectional2D);
        bt.AddChild(MakeClip("F", 1f), new Vector2(0, 1));
        bt.AddChild(MakeClip("B", 1f), new Vector2(0, -1));
        bt.ParameterValueX = 0f;
        bt.ParameterValueY = 0.5f; // 幅值 0.5，朝 +Y

        var weights = bt.GetWeights();
        float sum = 0f;
        foreach (var w in weights) sum += w;
        AssertTrue(MathF.Abs(sum - 0.5f) < 0.01f, $"半幅值输入应权重和≈0.5，got {sum}。");
        AssertTrue(weights[0] > 0.4f, "朝 +Y 时 F（+Y 方向）权重应最大。");
    }

    // ── SimpleDirectional2D：满幅值时权重和≈1 ──
    public static void TestSimpleDirectionalFullMagnitude()
    {
        var skeleton = MakeSkeleton();
        var bt = new BlendTree("Move", BlendTreeType.SimpleDirectional2D);
        bt.AddChild(MakeClip("F", 1f), new Vector2(0, 1));
        bt.AddChild(MakeClip("B", 1f), new Vector2(0, -1));
        bt.ParameterValueX = 0f;
        bt.ParameterValueY = 1f; // 幅值 1，朝 +Y

        var weights = bt.GetWeights();
        float sum = 0f;
        foreach (var w in weights) sum += w;
        AssertTrue(MathF.Abs(sum - 1f) < 0.01f, $"满幅值应权重和≈1，got {sum}。");
    }

    // ── FreeformDirectional2D：与半径无关，只按角度加权 ──
    public static void TestFreeformDirectionalRadiusIndependent()
    {
        var skeleton = MakeSkeleton();
        var bt = new BlendTree("Dir", BlendTreeType.FreeformDirectional2D);
        bt.AddChild(MakeClip("F", 1f), new Vector2(0, 1));   // +Y = 90°
        bt.AddChild(MakeClip("R", 1f), new Vector2(1, 0));   // +X = 0°
        bt.ParameterValueX = 0f;
        bt.ParameterValueY = 0.3f; // 小半径，仍朝 +Y（90°）

        var weights = bt.GetWeights();
        AssertTrue(weights[0] > 0.9f, $"FreeformDirectional 只看角度，小半径朝 +Y 时 F 权重应≈1，got {weights[0]}。");
        float sum = 0f;
        foreach (var w in weights) sum += w;
        AssertTrue(MathF.Abs(sum - 1f) < 0.01f, "方向混合权重和应为 1（与半径无关）。");
    }

    // ── FreeformDirectional2D vs SimpleDirectional2D 对比：小半径下行为不同 ──
    public static void TestDirectionalModesDifferAtSmallRadius()
    {
        // 同样的 0.3 半径 +Y 输入
        var btSimple = new BlendTree("S", BlendTreeType.SimpleDirectional2D);
        btSimple.AddChild(MakeClip("F", 1f), new Vector2(0, 1));
        btSimple.AddChild(MakeClip("B", 1f), new Vector2(0, -1));
        btSimple.ParameterValueX = 0f; btSimple.ParameterValueY = 0.3f;
        var wSimple = btSimple.GetWeights();
        float sumSimple = 0f; foreach (var w in wSimple) sumSimple += w;

        var btFree = new BlendTree("F", BlendTreeType.FreeformDirectional2D);
        btFree.AddChild(MakeClip("F", 1f), new Vector2(0, 1));
        btFree.AddChild(MakeClip("B", 1f), new Vector2(0, -1));
        btFree.ParameterValueX = 0f; btFree.ParameterValueY = 0.3f;
        var wFree = btFree.GetWeights();
        float sumFree = 0f; foreach (var w in wFree) sumFree += w;

        // Simple 按幅值缩放（和≈0.3），Freeform 与半径无关（和≈1）
        AssertTrue(sumSimple < 0.4f, $"Simple 小半径权重和应≈0.3，got {sumSimple}。");
        AssertTrue(sumFree > 0.9f, $"Freeform 小半径权重和应≈1，got {sumFree}。");
    }

    // ── FreeformDirectional2D：45° 输入 → 两相邻方向各约一半 ──
    public static void TestFreeformDirectional45Degrees()
    {
        var skeleton = MakeSkeleton();
        var bt = new BlendTree("Dir", BlendTreeType.FreeformDirectional2D);
        bt.AddChild(MakeClip("F", 1f), new Vector2(0, 1));  // 90°
        bt.AddChild(MakeClip("R", 1f), new Vector2(1, 0));  // 0°
        // 输入 45° 方向（单位向量）
        bt.ParameterValueX = MathF.Sqrt(0.5f);
        bt.ParameterValueY = MathF.Sqrt(0.5f);

        var weights = bt.GetWeights();
        AssertTrue(MathF.Abs(weights[0] - 0.5f) < 0.05f, $"45° 应在 F/R 间各半，F={weights[0]}。");
        AssertTrue(MathF.Abs(weights[1] - 0.5f) < 0.05f, $"45° 应在 F/R 间各半，R={weights[1]}。");
    }

    // ── FreeformCartesian2D：重心仍正确（回归，确保未破坏）──
    public static void TestFreeformCartesianBarycentric()
    {
        var skeleton = MakeSkeleton();
        var bt = new BlendTree("Cart", BlendTreeType.FreeformCartesian2D);
        bt.AddChild(MakeClip("A", 1f), new Vector2(0, 0));
        bt.AddChild(MakeClip("B", 1f), new Vector2(2, 0));
        bt.AddChild(MakeClip("C", 1f), new Vector2(0, 2));
        bt.ParameterValueX = 0f;
        bt.ParameterValueY = 0f; // 三角形 (0,0)(2,0)(0,2) 的顶点 A

        var weights = bt.GetWeights();
        AssertTrue(weights[0] > 0.99f, "Cartesian 查询在顶点 A 应权重≈1。");
    }

    // ── Sample() 用 SimpleDirectional 零向量应产出无位移（接近 bind pose）──
    public static void TestSimpleDirectionalSampleNoTranslation()
    {
        var skeleton = MakeSkeleton();
        var bt = new BlendTree("Move", BlendTreeType.SimpleDirectional2D);
        bt.AddChild(MakeClip("F", 2f), new Vector2(0, 1));
        bt.AddChild(MakeClip("B", 2f), new Vector2(0, -1));
        bt.ParameterValueX = 0f;
        bt.ParameterValueY = 0f; // 零向量

        var pose = bt.Sample(skeleton, 0f);
        int hips = skeleton.FindBone("Hips");
        AssertTrue(MathF.Abs(pose.GetTranslationOffset(hips).Z) < 0.01f,
            "零向量 Sample 应无运动贡献（接近 bind pose）。");
    }
}
