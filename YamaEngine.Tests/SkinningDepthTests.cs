// SkinningDepthTests.cs — Unity5 蒙皮深度验证
// 覆盖：
//   * Skeleton.Validate 骨骼验证（空/父索引越界/前向引用/空名）
//   * ComputeWorldMatrices 世界矩阵层次（父子累积/根运动）
//   * SkinBinding.AutoBind 权重归一化/最大影响数
//   * CpuSkinning.Deform 边界（权重空/越界骨骼索引）
//   * SkeletonPose.CopyFrom/Clone/局部变换
// 全部纯 CPU 级确定性。
using System;
using System.Linq;
using System.Numerics;
using YamaEngine.Modeling;
using YamaEngine.Skinning;
using static YamaEngine.Tests.TestHelpers;

namespace YamaEngine.Tests;

public static class SkinningDepthTests
{
    private static void AssertNear(float got, float want, float tol, string msg)
    {
        if (MathF.Abs(got - want) > tol)
            throw new InvalidOperationException($"{msg}（实际 {got}，期望 {want}，容差 {tol}）");
    }
    /// <summary>Skeleton.Validate：空骨架报错。</summary>
    public static void TestValidateEmptySkeleton()
    {
        var s = new Skeleton();
        var errors = s.Validate();
        AssertTrue(errors.Count > 0, "空骨架应有验证错误");
        AssertTrue(errors[0].Contains("at least one"), "错误应提示至少一个骨骼");
    }

    /// <summary>Skeleton.Validate：父索引越界报错。</summary>
    public static void TestValidateParentIndexOutOfRange()
    {
        var s = new Skeleton();
        s.AddBone("A", -1, Vector3.Zero);
        s.Bones.Add(new YamaEngine.Skinning.Bone { Name = "B", ParentIndex = 5 }); // 越界
        var errors = s.Validate();
        AssertTrue(errors.Count > 0, "父索引越界应报错");
    }

    /// <summary>Skeleton.Validate：父索引前向引用报错（父不能在自己之后）。</summary>
    public static void TestValidateParentForwardRef()
    {
        var s = new Skeleton();
        s.Bones.Add(new YamaEngine.Skinning.Bone { Name = "A", ParentIndex = 1 }); // 指向后面的骨骼
        s.Bones.Add(new YamaEngine.Skinning.Bone { Name = "B", ParentIndex = -1 });
        var errors = s.Validate();
        AssertTrue(errors.Count > 0, "前向引用应报错");
        AssertTrue(errors.Any(e => e.Contains("precede")), "错误应提示父必须在前");
    }

    /// <summary>Skeleton.Validate：空名报错。</summary>
    public static void TestValidateEmptyName()
    {
        var s = new Skeleton();
        s.AddBone("", -1, Vector3.Zero);
        var errors = s.Validate();
        AssertTrue(errors.Count > 0, "空名应报错");
    }

    /// <summary>ComputeWorldMatrices：父子累积，子=局部×父世界。</summary>
    public static void TestWorldMatrixHierarchy()
    {
        var s = new Skeleton();
        int hip = s.AddBone("Hip", -1, new Vector3(0, 1, 0));
        int spine = s.AddBone("Spine", hip, new Vector3(0, 0.5f, 0));

        var world = s.ComputeWorldMatrices(null);
        // Hip 世界位置 = (0,1,0)
        AssertNear(world[hip].M41, 0f, 1e-4f, "Hip X 应=0");
        AssertNear(world[hip].M42, 1f, 1e-4f, "Hip Y 应=1");
        // Spine 世界位置 = 父位置 + 局部 = (0,1.5,0)
        AssertNear(world[spine].M41, 0f, 1e-4f, "Spine X 应=0");
        AssertNear(world[spine].M42, 1.5f, 1e-4f, "Spine Y 应=1.5");
    }

    /// <summary>ComputeWorldMatrices：旋转沿链累积。</summary>
    public static void TestWorldMatrixRotationAccumulation()
    {
        var s = new Skeleton();
        int hip = s.AddBone("Hip", -1, Vector3.Zero);
        int arm = s.AddBone("Arm", hip, new Vector3(1, 0, 0));

        // 父绕 Z 转 90°，子随父旋转
        var pose = new SkeletonPose(s);
        pose.SetRotation(hip, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2));

        var world = s.ComputeWorldMatrices(pose);
        // 子局部 (1,0,0) 绕 Z 转 90° → (0,1,0)
        AssertNear(world[arm].M41, 0f, 1e-3f, "Arm X 应=0（旋转后）");
        AssertNear(world[arm].M42, 1f, 1e-3f, "Arm Y 应=1（旋转后）");
    }

    /// <summary>SkeletonPose.RootMotion：GetTranslationOffset 可设根位移。</summary>
    public static void TestPoseRootMotion()
    {
        var s = Skeleton.CreateBiped();
        var pose = new SkeletonPose(s);
        pose.RootMotion = new Vector3(1, 0, 2);

        AssertTrue(pose.RootMotion.HasValue, "RootMotion 应有值");
        AssertNear(pose.RootMotion.Value.Z, 2f, 1e-5f, "RootMotion Z 应=2");
    }

    /// <summary>SkeletonPose.Translate：叠加根位移并传播到世界矩阵。</summary>
    public static void TestPoseTranslateAccumulates()
    {
        var s = Skeleton.CreateBiped();
        var pose = new SkeletonPose(s);
        int hips = s.FindBone("Hips");

        // 累积平移 0.5 + 0.3 = 0.8
        pose.Translate("Hips", new Vector3(0.5f, 0, 0));
        pose.Translate("Hips", new Vector3(0.3f, 0, 0));

        var world = s.ComputeWorldMatrices(pose);
        AssertNear(world[hips].M41, 0.8f, 1e-4f, "Hips X 应=0.8");
    }

    /// <summary>SkeletonPose.CopyFrom：复制后旋转一致。</summary>
    public static void TestPoseCopyFrom()
    {
        var s = Skeleton.CreateBiped();
        var src = new SkeletonPose(s);
        src.SetRotation("Chest", Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 4));
        src.Translate("Hips", new Vector3(1, 0, 0));

        var dst = new SkeletonPose(s);
        dst.CopyFrom(src);

        int chest = s.FindBone("Chest");
        var q1 = src.GetRotation(chest);
        var q2 = dst.GetRotation(chest);
        AssertNear(q1.X, q2.X, 1e-5f, "Chest 旋转应一致");
    }

    /// <summary>SkeletonPose.Clone：克隆后独立，修改原不影响克隆。</summary>
    public static void TestPoseCloneIndependence()
    {
        var s = Skeleton.CreateBiped();
        var pose = new SkeletonPose(s);
        pose.Translate("Hips", new Vector3(1, 0, 0));
        var clone = pose.Clone();

        // 修改原
        pose.Translate("Hips", new Vector3(10, 0, 0));

        int hips = s.FindBone("Hips");
        AssertNear(clone.GetTranslationOffset(hips).X, 1f, 1e-5f, "克隆不受原修改影响");
    }

    /// <summary>SkinBinding.AutoBind：权重归一化到 1.0。</summary>
    public static void TestAutoBindWeightsNormalized()
    {
        var skel = Skeleton.CreateBiped();
        var mesh = EditableMesh.CreateCube("AutoBindCube", 2f);
        var binding = SkinBinding.AutoBind(mesh, skel);

        AssertTrue(binding.Weights.Count == mesh.Vertices.Count, "每个顶点应有权重");
        foreach (var vw in binding.Weights)
        {
            float sum = vw.Influences.Sum(i => i.Weight);
            AssertNear(sum, 1f, 1e-3f, $"顶点权重应归一化到 1（实际 {sum}）");
        }
    }

    /// <summary>SkinBinding.AutoBind：maxInfluences=1 → 单骨骼绑定。</summary>
    public static void TestAutoBindMaxInfluencesOne()
    {
        var skel = Skeleton.CreateBiped();
        var mesh = EditableMesh.CreateCube("SingleBindCube", 2f);
        var binding = SkinBinding.AutoBind(mesh, skel, maxInfluences: 1);

        foreach (var vw in binding.Weights)
        {
            AssertTrue(vw.Influences.Count <= 1, "maxInfluences=1 应只有 1 个影响");
            if (vw.Influences.Count == 1)
            {
                AssertNear(vw.Influences[0].Weight, 1f, 1e-5f, "单影响权重应=1");
            }
        }
    }

    /// <summary>SkinBinding.AutoBind：InverseBindPoses 数量=骨骼数。</summary>
    public static void TestAutoBindInverseBindCount()
    {
        var skel = Skeleton.CreateBiped();
        var mesh = EditableMesh.CreateCube("IBCountCube", 2f);
        var binding = SkinBinding.AutoBind(mesh, skel);

        AssertTrue(binding.InverseBindPoses.Count == skel.Bones.Count,
            $"InverseBindPoses 数量应=骨骼数（实际 {binding.InverseBindPoses.Count} vs {skel.Bones.Count}）");
    }

    /// <summary>CpuSkinning.Deform：绑定姿态下变形≈0（返回原位置）。</summary>
    public static void TestCpuSkinningBindPoseIdentity()
    {
        var skel = Skeleton.CreateBiped();
        var mesh = EditableMesh.CreateCube("BindPoseCube", 2f);
        var binding = SkinBinding.AutoBind(mesh, skel);
        var pose = skel.CreateBindPose(); // 默认绑定姿态

        var deformed = CpuSkinning.Deform(mesh, skel, binding, pose);
        for (int i = 0; i < mesh.Vertices.Count; i++)
        {
            var src = mesh.Vertices[i];
            var dst = deformed[i];
            AssertNear(src.X, dst.X, 1e-3f, $"顶点 {i} X 绑定姿态下应≈原位置");
            AssertNear(src.Y, dst.Y, 1e-3f, $"顶点 {i} Y 绑定姿态下应≈原位置");
        }
    }

    /// <summary>CpuSkinning.Deform：位移骨骼后顶点跟随移动。</summary>
    public static void TestCpuSkinningDeformMovesVertices()
    {
        var skel = Skeleton.CreateBiped();
        var mesh = EditableMesh.CreateCube("DeformCube", 2f);
        var binding = SkinBinding.AutoBind(mesh, skel);

        // 整个骨架平移 5
        var pose = new SkeletonPose(skel);
        pose.Translate("Hips", new Vector3(5, 0, 0));

        var deformed = CpuSkinning.Deform(mesh, skel, binding, pose);
        // 所有顶点应整体平移（近似）
        var src0 = mesh.Vertices[0];
        var dst0 = deformed[0];
        AssertNear(dst0.X - src0.X, 5f, 0.5f, "顶点应大致平移 5");
    }

    /// <summary>CpuSkinning.Deform：旋转骨骼后顶点跟随旋转。</summary>
    public static void TestCpuSkinningDeformRotatesVertices()
    {
        var skel = Skeleton.CreateBiped();
        var mesh = EditableMesh.CreateCube("RotCube", 1f);
        var binding = SkinBinding.AutoBind(mesh, skel);

        // 根绕 Z 转 90°，所有顶点应跟着转
        var pose = new SkeletonPose(skel);
        pose.SetRotation(0, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2));

        var deformed = CpuSkinning.Deform(mesh, skel, binding, pose);
        // 顶点 (0.5, 0.5, 0.5) 绕 Z 转 90° → (-0.5, 0.5, 0.5)
        var src0 = mesh.Vertices[6]; // (0.5, 0.5, 0.5)
        var dst0 = deformed[6];
        AssertTrue(MathF.Abs(dst0.X - src0.X) > 0.1f || MathF.Abs(dst0.Y - src0.Y) > 0.1f,
            "顶点旋转后位置应有显著变化");
    }

    /// <summary>Skeleton.FindBone：找不到返回 -1。</summary>
    public static void TestFindBoneNotFound()
    {
        var s = Skeleton.CreateBiped();
        int idx = s.FindBone("NonExistent");
        AssertTrue(idx == -1, "不存在的骨骼应返回 -1");
    }

    /// <summary>Skeleton.AddBone：返回新骨骼索引。</summary>
    public static void TestAddBoneReturnsIndex()
    {
        var s = new Skeleton();
        int a = s.AddBone("A", -1, Vector3.Zero);
        int b = s.AddBone("B", a, Vector3.Zero);
        AssertTrue(a == 0, "第一个骨骼索引应为 0");
        AssertTrue(b == 1, "第二个骨骼索引应为 1");
    }
}
