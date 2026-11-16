// PoseAssetTests.cs — PoseAsset 姿态资产 + BlendPosesNode 闭环验证（UE5.8 深度级 任务F）
// 覆盖：
//   * PoseAsset AddPose/SetBone/Evaluate 姿态变换读写
//   * PoseAsset CapturePose 从 SkeletonPose 捕获
//   * PoseAsset ApplyToPose 写回 SkeletonPose
//   * BlendPosesNode 等权混合（平移平均）
//   * BlendPosesNode 单姿态全权重
//   * BlendPosesNode 旋转混合（Slerp）
// 全部 CPU 级确定性，不依赖 GPU/场景。
using System;
using System.Numerics;
using YamaEngine.Animation;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests;

/// <summary>PoseAsset / BlendPosesNode 验证套件。</summary>
public sealed class PoseAssetTests
{
    /// <summary>姿态变换读写。</summary>
    public static void TestPoseAssetAddAndSetBone()
    {
        var skeleton = YamaEngine.Animation.Skeleton.CreateBiped("Pose");
        var asset = new PoseAsset(skeleton);
        var entry = asset.AddPose("Idle");

        int hips = skeleton.FindBone("Hips");
        var t = new Vector3(0, 1, 0);
        var r = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 4f);
        asset.SetBone(0, hips, t, r, new Vector3(1.2f, 1.2f, 1.2f));

        var output = asset.Evaluate(0);
        AssertTrue(Vector3.Distance(output.Translations[hips], t) < 0.001f, "平移应读回。");
        AssertTrue(MathF.Abs(Quaternion.Dot(output.Rotations[hips], r)) > 0.999f, "旋转应读回。");
        AssertTrue(Vector3.Distance(output.Scales[hips], new Vector3(1.2f)) < 0.001f, "缩放应读回。");
        AssertEqual(1, asset.Poses.Count, "应有 1 个姿态。");
        AssertEqual("Idle", entry.Name, "姿态名应正确。");
    }

    /// <summary>从 SkeletonPose 捕获姿态。</summary>
    public static void TestPoseAssetCapturePose()
    {
        var skeleton = YamaEngine.Animation.Skeleton.CreateBiped("Pose");
        var asset = new PoseAsset(skeleton);
        asset.AddPose("Captured");

        var pose = skeleton.CreateBindPose();
        int hips = skeleton.FindBone("Hips");
        pose.SetTranslationOffset(hips, new Vector3(0, 3, 0));
        pose.SetRotation(hips, Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2f));

        asset.CapturePose(0, pose);
        var output = asset.Evaluate(0);
        AssertTrue(Vector3.Distance(output.Translations[hips], new Vector3(0, 3, 0)) < 0.001f,
            "捕获的平移应等于源姿态。");
        AssertTrue(MathF.Abs(Quaternion.Dot(output.Rotations[hips],
            Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2f))) > 0.999f,
            "捕获的旋转应等于源姿态。");
    }

    /// <summary>写回 SkeletonPose。</summary>
    public static void TestPoseAssetApplyToPose()
    {
        var skeleton = YamaEngine.Animation.Skeleton.CreateBiped("Pose");
        var asset = new PoseAsset(skeleton);
        asset.AddPose("Target");

        int hips = skeleton.FindBone("Hips");
        asset.SetBone(0, hips, new Vector3(0, 0, 5), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 1f), Vector3.One);

        var pose = skeleton.CreateBindPose();
        asset.ApplyToPose(0, pose);
        AssertTrue(Vector3.Distance(pose.GetTranslationOffset(hips), new Vector3(0, 0, 5)) < 0.001f,
            "写回的平移应正确。");
        AssertTrue(MathF.Abs(Quaternion.Dot(pose.GetRotation(hips),
            Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 1f))) > 0.999f,
            "写回的旋转应正确。");
    }

    /// <summary>等权混合（平移平均）。</summary>
    public static void TestBlendPosesNodeWeighted()
    {
        var skeleton = YamaEngine.Animation.Skeleton.CreateBiped("Pose");
        var asset = new PoseAsset(skeleton);
        asset.AddPose("A");
        asset.AddPose("B");

        int hips = skeleton.FindBone("Hips");
        asset.SetBone(0, hips, Vector3.Zero, Quaternion.Identity, Vector3.One);
        asset.SetBone(1, hips, new Vector3(0, 0, 2), Quaternion.Identity, Vector3.One);

        var node = new BlendPosesNode { Asset = asset };
        node.AddInput(0, 1f);
        node.AddInput(1, 1f);
        var output = node.Blend();
        AssertTrue(MathF.Abs(output.Translations[hips].Z - 1f) < 0.01f,
            "等权混合的平移应为平均 (Z=1)。");
        AssertTrue(output.Translations[hips].X < 0.01f && output.Translations[hips].Y < 0.01f,
            "X/Y 分量应为 0。");
    }

    /// <summary>单姿态全权重。</summary>
    public static void TestBlendPosesNodeSingleFull()
    {
        var skeleton = YamaEngine.Animation.Skeleton.CreateBiped("Pose");
        var asset = new PoseAsset(skeleton);
        asset.AddPose("Only");

        int hips = skeleton.FindBone("Hips");
        asset.SetBone(0, hips, new Vector3(0, 0, 7), Quaternion.Identity, Vector3.One);

        var node = new BlendPosesNode { Asset = asset };
        node.AddInput(0, 1f);
        var output = node.Blend();
        AssertTrue(MathF.Abs(output.Translations[hips].Z - 7f) < 0.01f,
            "单姿态全权重应输出该姿态。");
    }

    /// <summary>旋转混合（Slerp 45°）。</summary>
    public static void TestBlendPosesNodeWeightedRotation()
    {
        var skeleton = YamaEngine.Animation.Skeleton.CreateBiped("Pose");
        var asset = new PoseAsset(skeleton);
        asset.AddPose("A");
        asset.AddPose("B");

        int hips = skeleton.FindBone("Hips");
        asset.SetBone(0, hips, Vector3.Zero, Quaternion.Identity, Vector3.One);
        asset.SetBone(1, hips, Vector3.Zero, Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f), Vector3.One);

        var node = new BlendPosesNode { Asset = asset };
        node.AddInput(0, 1f);
        node.AddInput(1, 1f);
        var output = node.Blend();
        // 等权 → 45°绕Y，sin(22.5°)≈0.383
        AssertTrue(output.Rotations[hips].Y > 0.38f && output.Rotations[hips].Y < 0.40f,
            "等权旋转混合应为 45°绕Y。");
    }
}
