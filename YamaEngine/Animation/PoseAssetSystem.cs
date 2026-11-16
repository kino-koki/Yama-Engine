using System;
using System.Collections.Generic;
using System.Numerics;

namespace YamaEngine.Animation
{
    // ============================================================================================
    //  PoseAssetSystem.cs
    //
    //  PoseAsset —— 姿态资产（对应 UE 的 UPoseAsset）+ 按权重混合节点（Blend Poses by weight）。
    //
    //  能力：
    //    - PoseEntry：命名姿态，每骨骼的旋转/平移/缩放变换。
    //    - PoseAsset ：骨架 + 姿态列表；AddPose / SetBone / CapturePose(从 SkeletonPose 捕获) /
    //                  Evaluate(单姿态 → AnimPoseOutput) / ApplyToPose(写回 SkeletonPose)。
    //    - BlendPosesNode：按权重混合多个姿态（旋转 Slerp + 平移/缩放 Lerp），
    //                      对应 UE 的 Blend Poses by weight 动画节点。
    //
    //  复用既有 SkeletonPose / AnimPoseOutput 数据类型。
    // ============================================================================================

    /// <summary>单个命名姿态：每骨骼的局部变换。</summary>
    public sealed class PoseEntry
    {
        public string Name = "";

        /// <summary>每骨骼局部平移。</summary>
        public Vector3[] Translations;

        /// <summary>每骨骼局部旋转。</summary>
        public Quaternion[] Rotations;

        /// <summary>每骨骼局部缩放。</summary>
        public Vector3[] Scales;

        public int BoneCount => Translations?.Length ?? 0;

        public PoseEntry(string name, int boneCount)
        {
            Name = name;
            Translations = new Vector3[boneCount];
            Rotations = new Quaternion[boneCount];
            Scales = new Vector3[boneCount];
            for (int i = 0; i < boneCount; i++)
            {
                Translations[i] = Vector3.Zero;
                Rotations[i] = Quaternion.Identity;
                Scales[i] = Vector3.One;
            }
        }
    }

    /// <summary>
    /// 姿态资产：骨架 + 命名姿态集合。
    /// 对应 UE 的 UPoseAsset（姿态资产）。
    /// </summary>
    public sealed class PoseAsset
    {
        public string Name = "PoseAsset";

        /// <summary>关联骨架。</summary>
        public Skeleton Skeleton { get; }

        public List<PoseEntry> Poses { get; } = new();

        public PoseAsset(Skeleton skeleton)
        {
            Skeleton = skeleton ?? throw new ArgumentNullException(nameof(skeleton));
        }

        /// <summary>添加命名姿态（全部骨骼初始化为绑定/单位变换）。</summary>
        public PoseEntry AddPose(string name)
        {
            var entry = new PoseEntry(name, Skeleton.Bones.Count);
            Poses.Add(entry);
            return entry;
        }

        /// <summary>设置某姿态中某骨骼的局部变换。</summary>
        public void SetBone(int poseIndex, int boneIndex, Vector3 translation, Quaternion rotation, Vector3 scale)
        {
            if (poseIndex < 0 || poseIndex >= Poses.Count) return;
            var entry = Poses[poseIndex];
            if (boneIndex < 0 || boneIndex >= entry.BoneCount) return;
            entry.Translations[boneIndex] = translation;
            entry.Rotations[boneIndex] = rotation;
            entry.Scales[boneIndex] = scale;
        }

        /// <summary>从 SkeletonPose 捕获某骨骼的姿态到指定姿态条目。</summary>
        public void CapturePose(int poseIndex, SkeletonPose pose)
        {
            if (pose == null || poseIndex < 0 || poseIndex >= Poses.Count) return;
            var entry = Poses[poseIndex];
            int n = Math.Min(entry.BoneCount, pose.BoneCount);
            for (int i = 0; i < n; i++)
            {
                entry.Translations[i] = pose.GetTranslationOffset(i);
                entry.Rotations[i] = pose.GetRotation(i);
                entry.Scales[i] = pose.GetScaleOffset(i);
            }
        }

        /// <summary>求值单姿态为 AnimPoseOutput。</summary>
        public AnimPoseOutput Evaluate(int poseIndex)
        {
            int boneCount = Skeleton.Bones.Count;
            var output = AnimPoseOutput.Create(boneCount);
            if (poseIndex < 0 || poseIndex >= Poses.Count) return output;
            var entry = Poses[poseIndex];
            int n = Math.Min(boneCount, entry.BoneCount);
            for (int i = 0; i < n; i++)
            {
                output.Translations[i] = entry.Translations[i];
                output.Rotations[i] = entry.Rotations[i];
                output.Scales[i] = entry.Scales[i];
            }
            return output;
        }

        /// <summary>把姿态写入 SkeletonPose。</summary>
        public void ApplyToPose(int poseIndex, SkeletonPose pose)
        {
            if (pose == null || poseIndex < 0 || poseIndex >= Poses.Count) return;
            var entry = Poses[poseIndex];
            int n = Math.Min(pose.BoneCount, entry.BoneCount);
            for (int i = 0; i < n; i++)
            {
                pose.SetTranslationOffset(i, entry.Translations[i]);
                pose.SetRotation(i, entry.Rotations[i]);
                pose.SetScaleOffset(i, entry.Scales[i]);
            }
        }
    }

    /// <summary>
    /// 按权重混合多个姿态（对应 UE 的 Blend Poses by weight 动画节点）。
    /// 旋转按权重顺序 Slerp，平移/缩放加权 Lerp。
    /// </summary>
    public sealed class BlendPosesNode
    {
        /// <summary>源姿态资产。</summary>
        public PoseAsset? Asset { get; set; }

        /// <summary>混合输入（姿态索引 + 权重）。</summary>
        public List<(int Index, float Weight)> Inputs { get; } = new();

        /// <summary>添加混合输入。</summary>
        public void AddInput(int poseIndex, float weight)
        {
            Inputs.Add((poseIndex, weight));
        }

        /// <summary>清空混合输入。</summary>
        public void ClearInputs() => Inputs.Clear();

        /// <summary>按权重混合所有输入姿态。</summary>
        public AnimPoseOutput Blend()
        {
            int boneCount = Asset?.Skeleton.Bones.Count ?? 0;
            var output = AnimPoseOutput.Create(boneCount);
            if (Asset == null || Inputs.Count == 0) return output;

            float wsum = 0f;
            foreach (var input in Inputs)
                wsum += MathF.Max(0f, input.Weight);
            if (wsum <= 1e-8f) return output;

            // 预采样所有输入姿态与归一化权重
            var poses = new AnimPoseOutput[Inputs.Count];
            var w = new float[Inputs.Count];
            for (int k = 0; k < Inputs.Count; k++)
            {
                poses[k] = Asset.Evaluate(Inputs[k].Index);
                w[k] = MathF.Max(0f, Inputs[k].Weight) / wsum;
            }

            for (int i = 0; i < boneCount; i++)
            {
                // 平移/缩放：加权平均
                Vector3 t = Vector3.Zero;
                Vector3 s = Vector3.Zero;
                for (int k = 0; k < Inputs.Count; k++)
                {
                    t += poses[k].Translations[i] * w[k];
                    s += poses[k].Scales[i] * w[k];
                }
                output.Translations[i] = t;
                output.Scales[i] = s;

                // 旋转：按累计权重顺序 Slerp（逐骨骼）
                Quaternion rot = poses[0].Rotations[i];
                float aw = w[0];
                for (int k = 1; k < Inputs.Count; k++)
                {
                    aw += w[k];
                    rot = Quaternion.Slerp(rot, poses[k].Rotations[i], aw > 0f ? w[k] / aw : 0f);
                }
                output.Rotations[i] = Quaternion.Normalize(rot);
            }
            return output;
        }
    }
}
