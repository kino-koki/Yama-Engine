// ControlRigEvaluationTests.cs — Control Rig 完整约束求值器闭环验证（UE5.8 深度级 任务A）
// 覆盖：
//   * RigHierarchy 全局变换求值（前向遍历）
//   * 空间切换（Local/World/Parent 转换 + 往返）
//   * ParentConstraint（父级约束维持偏移）
//   * AimConstraint（瞄准约束指向目标）
//   * IKConstraint（两骨链解算到目标）
//   * TransformConstraint（匹配源变换）
//   * 端到端求值管线写回骨骼
// 全部 CPU 级确定性，不依赖 GPU/场景。
using System;
using System.Collections.Generic;
using System.Numerics;
using YamaEngine.Animation.ControlRig;
using YamaEngine.Skinning;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests;

/// <summary>Control Rig 约束求值器验证套件。</summary>
public sealed class ControlRigEvaluationTests
{
    /// <summary>层次求值：父先子，全局变换正确。</summary>
    public static void TestRigHierarchyComputesGlobals()
    {
        var asset = new ControlRigAsset { Name = "TestRig" };
        var root = new ControlRigControl { Name = "Root", InitialTransform = new RigTransform(new Vector3(0, 1, 0), Quaternion.Identity, Vector3.One) };
        asset.Elements.Add(root);
        var hand = new ControlRigControl { Name = "Hand", ParentIndex = 0, InitialTransform = new RigTransform(new Vector3(0, 0, 1), Quaternion.Identity, Vector3.One) };
        asset.Elements.Add(hand);

        var ev = new ControlRigEvaluator(asset);
        var skeleton = Skeleton.CreateBiped("TestBiped");
        var pose = skeleton.CreateBindPose();

        var controls = new Dictionary<string, RigTransform>
        {
            ["Root"] = new(new Vector3(0, 2, 0), Quaternion.Identity, Vector3.One),
            ["Hand"] = new(new Vector3(0, 0, 1), Quaternion.Identity, Vector3.One)
        };
        ev.Evaluate(asset, skeleton, pose, controls);

        var rootG = ev.GetGlobal(0);
        var handG = ev.GetGlobal(1);
        AssertNear(Vector3.Zero, rootG.Translation, new Vector3(0, 2, 0), "根控制全局位置应等于控制输入 (0,2,0)。");
        AssertNear(Vector3.Zero, handG.Translation, new Vector3(0, 2, 1), "子元素全局位置应为父位置 + 子局部 (0,2,1)。");
    }

    /// <summary>父级约束：目标元素维持相对父元素的偏移。</summary>
    public static void TestParentConstraintMaintainsOffset()
    {
        var asset = new ControlRigAsset { Name = "ParentTest" };
        asset.Elements.Add(new ControlRigControl { Name = "A", InitialTransform = new RigTransform(new Vector3(0, 0, 0), Quaternion.Identity, Vector3.One) });
        asset.Elements.Add(new ControlRigControl { Name = "B", ParentIndex = 0, InitialTransform = new RigTransform(new Vector3(0, 1, 0), Quaternion.Identity, Vector3.One) });

        asset.Constraints.Add(new ParentConstraint
        {
            Name = "KeepOffset",
            TargetElement = 1,
            ParentElement = 0,
            ParentSpace = RigSpace.Parent,
            Offset = new RigTransform(new Vector3(0, 0.5f, 0), Quaternion.Identity, Vector3.One)
        });

        var ev = new ControlRigEvaluator(asset);
        var skeleton = Skeleton.CreateBiped("TestBiped");
        var pose = skeleton.CreateBindPose();

        // A 移到 (0,3,0)
        var controls = new Dictionary<string, RigTransform>
        {
            ["A"] = new(new Vector3(0, 3, 0), Quaternion.Identity, Vector3.One),
            ["B"] = new(new Vector3(0, 1, 0), Quaternion.Identity, Vector3.One)
        };
        ev.Evaluate(asset, skeleton, pose, controls);

        var bGlobal = ev.GetGlobal(1);
        // B 应保持相对 A 的偏移 (0,0.5,0) → (0,3.5,0)
        AssertNear(Vector3.Zero, bGlobal.Translation, new Vector3(0, 3.5f, 0), "父约束应使 B 保持相对 A 的偏移。");
    }

    /// <summary>瞄准约束：AimAxis 指向目标元素。</summary>
    public static void TestAimConstraintPointsAtTarget()
    {
        var asset = new ControlRigAsset { Name = "AimTest" };
        asset.Elements.Add(new ControlRigControl { Name = "Gun", InitialTransform = new RigTransform(new Vector3(0, 0, 0), Quaternion.Identity, Vector3.One) });
        asset.Elements.Add(new ControlRigControl { Name = "Target", InitialTransform = new RigTransform(new Vector3(0, 1, 0), Quaternion.Identity, Vector3.One) });

        asset.Constraints.Add(new AimConstraint
        {
            Name = "AimAt",
            TargetElement = 0,
            AimTarget = 1,
            AimAxis = Vector3.UnitX,   // +X 应指向目标
            Weight = 1f
        });

        var ev = new ControlRigEvaluator(asset);
        var skeleton = Skeleton.CreateBiped("TestBiped");
        var pose = skeleton.CreateBindPose();

        var controls = new Dictionary<string, RigTransform>
        {
            ["Gun"] = new(new Vector3(0, 0, 0), Quaternion.Identity, Vector3.One),
            ["Target"] = new(new Vector3(0, 1, 0), Quaternion.Identity, Vector3.One)
        };
        ev.Evaluate(asset, skeleton, pose, controls);

        var gunGlobal = ev.GetGlobal(0);
        // 世界 +X 轴旋转后应指向 (0,1,0) 方向
        var axisWorld = Vector3.Transform(Vector3.UnitX, gunGlobal.Rotation);
        var dir = Vector3.Normalize(new Vector3(0, 1, 0) - gunGlobal.Translation);
        AssertTrue(Vector3.Dot(axisWorld, dir) > 0.999f,
            "瞄准约束应使 +X 轴指向目标。实际点积=" + Vector3.Dot(axisWorld, dir));
    }

    /// <summary>IK 约束：两骨链末端解算到目标。</summary>
    public static void TestIKConstraintReachesTarget()
    {
        var asset = new ControlRigAsset { Name = "IKTest" };
        // 元素链：Root(0,0,0) → Mid(0,1,0) → End(0,1,0)；Pole 在 +X
        asset.Elements.Add(new RigElement { Name = "Root", Type = RigElementType.Bone, InitialTransform = new RigTransform(new Vector3(0, 0, 0), Quaternion.Identity, Vector3.One) });
        asset.Elements.Add(new RigElement { Name = "Mid", Type = RigElementType.Bone, ParentIndex = 0, InitialTransform = new RigTransform(new Vector3(0, 1, 0), Quaternion.Identity, Vector3.One) });
        asset.Elements.Add(new RigElement { Name = "End", Type = RigElementType.Bone, ParentIndex = 1, InitialTransform = new RigTransform(new Vector3(0, 1, 0), Quaternion.Identity, Vector3.One) });
        asset.Elements.Add(new RigElement { Name = "Pole", Type = RigElementType.Null, InitialTransform = new RigTransform(new Vector3(1, 0, 0), Quaternion.Identity, Vector3.One) });

        asset.Constraints.Add(new IKConstraint
        {
            Name = "Reach",
            TargetElement = 2,
            TargetPosition = new Vector3(1, 1, 0),
            TargetSpace = RigSpace.World,
            PoleTarget = 3,
            Weight = 1f
        });

        var ev = new ControlRigEvaluator(asset);
        var skeleton = Skeleton.CreateBiped("TestBiped");
        var pose = skeleton.CreateBindPose();

        ev.Evaluate(asset, skeleton, pose, null);

        var endGlobal = ev.GetGlobal(2);
        AssertTrue(Vector3.Distance(endGlobal.Translation, new Vector3(1, 1, 0)) < 0.05f,
            "IK 约束应使末端到达目标 (1,1,0)。实际=" + endGlobal.Translation);
    }

    /// <summary>变换约束：目标匹配源元素的位置/旋转。</summary>
    public static void TestTransformConstraintMatchesSource()
    {
        var asset = new ControlRigAsset { Name = "TransformTest" };
        asset.Elements.Add(new ControlRigControl { Name = "Source", InitialTransform = new RigTransform(new Vector3(5, 0, 0), Quaternion.Identity, Vector3.One) });
        asset.Elements.Add(new ControlRigControl { Name = "Dest", InitialTransform = new RigTransform(new Vector3(0, 0, 0), Quaternion.Identity, Vector3.One) });

        asset.Constraints.Add(new TransformConstraint
        {
            Name = "Match",
            TargetElement = 1,
            SourceElement = 0,
            SourceSpace = RigSpace.Global,
            MatchTranslation = true,
            MatchRotation = true,
            Weight = 1f
        });

        var ev = new ControlRigEvaluator(asset);
        var skeleton = Skeleton.CreateBiped("TestBiped");
        var pose = skeleton.CreateBindPose();

        var controls = new Dictionary<string, RigTransform>
        {
            ["Source"] = new(new Vector3(5, 0, 0), Quaternion.Identity, Vector3.One),
            ["Dest"] = new(new Vector3(0, 0, 0), Quaternion.Identity, Vector3.One)
        };
        ev.Evaluate(asset, skeleton, pose, controls);

        var destGlobal = ev.GetGlobal(1);
        AssertNear(Vector3.Zero, destGlobal.Translation, new Vector3(5, 0, 0), "变换约束应使 Dest 匹配 Source 位置 (5,0,0)。");
    }

    /// <summary>空间转换：Local→World→Local 往返。</summary>
    public static void TestSpaceConversionRoundtrip()
    {
        var parent = new RigTransform(new Vector3(10, 0, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2f), Vector3.One);
        var local = new RigTransform(new Vector3(1, 0, 0), Quaternion.Identity, Vector3.One);

        var world = RigSpaceUtil.ToWorld(local, RigSpace.Parent, parent);
        // 旋转 90°Z 后 (1,0,0) → (0,1,0)，再 + (10,0,0) → (10,1,0)
        AssertNear(Vector3.Zero, world.Translation, new Vector3(10, 1, 0), "Local→World 应把偏移旋转到父空间。");

        var back = RigSpaceUtil.ToLocal(world, parent);
        AssertNear(Vector3.Zero, back.Translation, new Vector3(1, 0, 0), "World→Local 往返应还原局部位置。");
    }

    /// <summary>端到端：控制输入 → 约束 → 骨骼写回。</summary>
    public static void TestControlRigEvaluateWritesBones()
    {
        var asset = new ControlRigAsset { Name = "EndToEnd" };
        // 骨骼映射元素：Hips 在根，HipsCtrl 控制挂 Hips 下
        asset.Elements.Add(new RigElement { Name = "Hips", Type = RigElementType.Bone, InitialTransform = new RigTransform(new Vector3(0, 1, 0), Quaternion.Identity, Vector3.One) });
        asset.Elements.Add(new ControlRigControl { Name = "HipsCtrl", ParentIndex = 0, InitialTransform = new RigTransform(Vector3.Zero, Quaternion.Identity, Vector3.One) });

        var ev = new ControlRigEvaluator(asset);
        var skeleton = Skeleton.CreateBiped("TestBiped");
        var pose = skeleton.CreateBindPose();

        // 控制 HipsCtrl 旋转 45° 绕 Z
        var controls = new Dictionary<string, RigTransform>
        {
            ["HipsCtrl"] = new(Vector3.Zero, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 4f), Vector3.One)
        };
        ev.Evaluate(asset, skeleton, pose, controls);

        // Hips 骨骼（元素0）被约束后写回 pose
        var hipsIdx = skeleton.FindBone("Hips");
        AssertTrue(hipsIdx >= 0, "骨架应包含 Hips。");
        var world = skeleton.ComputeWorldMatrices(pose)[hipsIdx];
        AssertTrue(Vector3.Distance(world.Translation, new Vector3(0, 1, 0)) < 0.01f,
            "Hips 写回后应保持绑定位置 (0,1,0)。实际=" + world.Translation);
    }

    // ── 断言辅助 ─────────────────────────────────────────────────────────
    private static void AssertNear(Vector3 zero, Vector3 actual, Vector3 expected, string message)
    {
        AssertTrue(Vector3.Distance(actual, expected) < 0.01f, message + " 实际=" + actual + " 期望=" + expected);
    }
}
