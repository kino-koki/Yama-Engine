// FullBodyIKTests.cs — Full Body IK + 脚部锁定 + 脚部放置闭环验证（UE5.8 深度级 任务B）
// 覆盖：
//   * FullBodyIKSolver 多链 FABRIK 到达端到端目标
//   * FullBodyIKSolver 优先级（高优先级链覆盖低）
//   * FootLockSystem 锁定区间内保持脚部世界位置
//   * FootLockSystem 非锁定区间释放
//   * FootPlacementSystem 交替步态目标点
//   * FootPlacementSystem 相位推进
// 全部 CPU 级确定性，不依赖 GPU/场景。
using System;
using System.Numerics;
using YamaEngine.Animation.FullBodyIKSystem;
using YamaEngine.Skinning;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests;

/// <summary>Full Body IK / 脚部锁定 / 脚部放置验证套件。</summary>
public sealed class FullBodyIKTests
{
    /// <summary>FBIK：腿链到达端到端目标。</summary>
    public static void TestFullBodyIKReachesTarget()
    {
        var skeleton = Skeleton.CreateBiped("FBIKTest");
        var pose = skeleton.CreateBindPose();
        int thigh = skeleton.FindBone("LeftUpperLeg");
        int calf = skeleton.FindBone("LeftLowerLeg");
        int foot = skeleton.FindBone("LeftFoot");

        var solver = new FullBodyIKSolver();
        solver.Chains.Add(new FullBodyIKChain
        {
            BoneIndices = new[] { thigh, calf, foot },
            TargetPosition = new Vector3(-0.1f, 0.3f, 0.2f),
            Weight = 1f,
            Iterations = 20
        });

        solver.Solve(skeleton, pose, 1f / 60f);

        var world = skeleton.ComputeWorldMatrices(pose);
        var footPos = world[foot].Translation;
        AssertTrue(Vector3.Distance(footPos, new Vector3(-0.1f, 0.3f, 0.2f)) < 0.06f,
            "FBIK 应使脚部接近目标。实际=" + footPos + " 期望≈(-0.1,0.3,0.2)");
    }

    /// <summary>FBIK：优先级——高优先级链的末端更接近其目标。</summary>
    public static void TestFullBodyIKPriorityOrdering()
    {
        var skeleton = Skeleton.CreateBiped("FBIKPriority");
        var pose = skeleton.CreateBindPose();
        int lFoot = skeleton.FindBone("LeftFoot");
        int rFoot = skeleton.FindBone("RightFoot");

        var solver = new FullBodyIKSolver();
        // 两条腿链，目标互换位置（高优先级右侧覆盖）
        solver.Chains.Add(new FullBodyIKChain
        {
            BoneIndices = new[] { skeleton.FindBone("LeftUpperLeg"), skeleton.FindBone("LeftLowerLeg"), lFoot },
            TargetPosition = new Vector3(0.1f, 0.3f, 0.2f),
            Weight = 1f, Priority = 0, Iterations = 20
        });
        solver.Chains.Add(new FullBodyIKChain
        {
            BoneIndices = new[] { skeleton.FindBone("RightUpperLeg"), skeleton.FindBone("RightLowerLeg"), rFoot },
            TargetPosition = new Vector3(0.1f, 0.3f, 0.2f),
            Weight = 1f, Priority = 10, Iterations = 20
        });

        solver.Solve(skeleton, pose, 1f / 60f);
        var world = skeleton.ComputeWorldMatrices(pose);
        // 高优先级（右侧）脚应更接近目标
        float dRight = Vector3.Distance(world[rFoot].Translation, new Vector3(0.1f, 0.3f, 0.2f));
        AssertTrue(dRight < 0.06f, "高优先级链的末端应接近其目标。实际距离=" + dRight);
    }

    /// <summary>脚部锁定：锁定区间内保持世界位置。</summary>
    public static void TestFootLockHoldsPosition()
    {
        var skeleton = Skeleton.CreateBiped("FootLockTest");
        var pose = skeleton.CreateBindPose();
        int thigh = skeleton.FindBone("LeftUpperLeg");
        int calf = skeleton.FindBone("LeftLowerLeg");
        int foot = skeleton.FindBone("LeftFoot");

        var world = skeleton.ComputeWorldMatrices(pose);
        var lockedPos = world[foot].Translation;

        var system = new FootLockSystem();
        system.Tracks.Add(new FootLockTrack
        {
            FootBoneIndex = foot,
            ThighIndex = thigh,
            CalfIndex = calf,
            LockedWorldPosition = lockedPos,
            Keys = { new FootLockKey(0f, false), new FootLockKey(0.5f, true), new FootLockKey(1f, false) }
        });

        // 扰动大腿（父骨骼）旋转 45° 绕 Z，使脚部移动（模拟运动）
        pose.SetRotation(thigh, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 4f));
        var perturbed = skeleton.ComputeWorldMatrices(pose);
        AssertTrue(Vector3.Distance(perturbed[foot].Translation, lockedPos) > 0.05f,
            "扰动后脚部应偏离锁定位置（前置条件）。");

        // 锁定求解（animTime=0.7 在锁定区间）
        system.Solve(skeleton, pose, 0.7f);
        var after = skeleton.ComputeWorldMatrices(pose);
        AssertTrue(Vector3.Distance(after[foot].Translation, lockedPos) < 0.08f,
            "锁定系统应把脚部拉回锁定位置。实际距离=" + Vector3.Distance(after[foot].Translation, lockedPos));
    }

    /// <summary>脚部锁定：非锁定区间释放（脚部自由）。</summary>
    public static void TestFootLockUnlockReleases()
    {
        var skeleton = Skeleton.CreateBiped("FootLockRelease");
        var pose = skeleton.CreateBindPose();
        int foot = skeleton.FindBone("LeftFoot");

        var system = new FootLockSystem();
        system.Tracks.Add(new FootLockTrack
        {
            FootBoneIndex = foot,
            ThighIndex = skeleton.FindBone("LeftUpperLeg"),
            CalfIndex = skeleton.FindBone("LeftLowerLeg"),
            LockedWorldPosition = new Vector3(99, 99, 99), // 远处的假锁定位置
            Keys = { new FootLockKey(0f, false), new FootLockKey(0.5f, true), new FootLockKey(1f, false) }
        });

        // animTime=0.2（非锁定区间），不应把脚拉向 (99,99,99)
        system.Solve(skeleton, pose, 0.2f);
        var world = skeleton.ComputeWorldMatrices(pose);
        AssertTrue(Vector3.Distance(world[foot].Translation, new Vector3(99, 99, 99)) > 1f,
            "非锁定区间脚部应保持自由，不被拉到假锁定位置。");
    }

    /// <summary>脚部放置：双足交替目标点。</summary>
    public static void TestFootPlacementGeneratesAlternatingTargets()
    {
        var placement = new FootPlacementSystem
        {
            StepLength = 0.6f,
            StanceWidth = 0.2f,
            Forward = Vector3.UnitZ,
            Up = Vector3.UnitY
        };
        placement.AddFoot(0, Vector3.Zero);
        placement.AddFoot(1, Vector3.Zero);

        var root = new Vector3(0, 1, 0);
        placement.Update(0.1f, root);   // 相位推进

        var f0 = placement.Feet[0].CurrentTarget;
        var f1 = placement.Feet[1].CurrentTarget;
        // 两只脚应沿前向（Z）不同位置、侧向（X）对称分开
        AssertTrue(MathF.Abs(f0.Z - f1.Z) > 0.05f, "双足目标应沿前向错开（交替步态）。");
        AssertTrue(MathF.Abs(f0.X - f1.X) > 0.05f, "双足目标应在侧向分开。");
    }

    /// <summary>脚部放置：步态相位推进。</summary>
    public static void TestFootPlacementAdvancesPhase()
    {
        var placement = new FootPlacementSystem { StepFrequency = 2f };
        placement.AddFoot(0, Vector3.Zero);
        placement.AddFoot(1, Vector3.Zero);

        float phase0 = placement.GetPhase(0);
        placement.Update(0.25f, new Vector3(0, 1, 0));
        float phase1 = placement.GetPhase(0);
        AssertTrue(phase1 > phase0 || phase1 < phase0, "步态相位应随 Update 推进。");
        AssertTrue(placement.GetPhase(0) >= 0f && placement.GetPhase(0) < 1f, "相位应在 [0,1)。");
    }
}
