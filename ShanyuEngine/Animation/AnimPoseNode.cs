using System;
using System.Collections.Generic;
using System.Numerics;
using ShanyuEngine.Skinning;

namespace ShanyuEngine.Animation
{
    // ============================================================================================
    //  AnimPoseNode.cs
    //
    //  基本姿势节点（Animation Pose Nodes）。
    //
    //  本系统提供多种基础姿势节点，用于构建动画图的最底层（叶子节点）：
    //    - AnimSequencePlayerNode    ：播放动画序列（≈ UE AnimSequencePlayer）
    //    - AnimPoseBlendNode         ：多姿势混合（≈ UE Blend Poses Together）
    //    - AnimSlotNode              ：插槽节点（≈ UE Slot Node，用于蒙太奇）
    //    - AnimApplyAdditiveNode     ：叠加动画（≈ UE Apply Additive）
    //    - AnimModifyBoneNode        ：骨骼修改（≈ UE Modify Bone）
    //    - AnimCopyBoneNode          ：复制骨骼变换（≈ UE Copy Bone）
    //    - AnimIKNode                ：IK 节点（≈ UE Two Bone IK / FABRIK）
    //    - AnimRootMotionNode        ：根骨骼运动提取（≈ UE Root Motion）
    // ============================================================================================

    // ──────────────────────────────────────────────────────────────
    //  动画序列播放器节点（AnimSequencePlayerNode）
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 播放一个动画序列。是动画图的叶子节点。
    /// 相当于 UE5 的 AnimSequencePlayer 节点。
    /// </summary>
    public sealed class AnimSequencePlayerNode : AnimNodeBase
    {
        /// <summary>动画剪辑引用。</summary>
        public AnimationClip? Animation { get; set; }

        /// <summary>播放速率。</summary>
        public float PlayRate { get; set; } = 1f;

        /// <summary>起始位置（标准化时间 [0,1]）。</summary>
        public float StartPosition { get; set; }

        /// <summary>是否循环。</summary>
        public bool bLoop { get; set; } = true;

        /// <summary>显式时间设置（若 >= 0 则覆盖内部时间）。</summary>
        public float ExplicitTime { get; set; } = -1f;

        /// <summary>时间重映射曲线。键为原时间，值为重映射后的时间。</summary>
        public List<Vector2> TimeRemapCurve { get; set; } = new();

        /// <summary>当前播放时间（秒）。</summary>
        public float CurrentTime { get; private set; }

        private Skeleton? _skeleton;

        public AnimSequencePlayerNode()
        {
            NodeName = "SequencePlayer";
        }

        public AnimSequencePlayerNode(AnimationClip clip) : this()
        {
            Animation = clip;
        }

        public override void OnInitialize(Skeleton skeleton)
        {
            _skeleton = skeleton;
            CurrentTime = StartPosition * (Animation?.LengthSeconds ?? 1f);
        }

        public override void OnUpdate(float deltaTime)
        {
            if (ExplicitTime >= 0f)
            {
                CurrentTime = ExplicitTime;
                return;
            }

            CurrentTime += deltaTime * PlayRate;
            float length = Animation?.LengthSeconds ?? 1f;
            if (bLoop && length > 0f)
            {
                CurrentTime %= length;
                if (CurrentTime < 0f) CurrentTime += length;
            }
            else if (CurrentTime > length)
            {
                CurrentTime = length;
            }
        }

        public override void OnEvaluate(AnimPoseOutput output)
        {
            if (Animation == null || _skeleton == null) return;

            // 应用时间重映射曲线
            float sampleTime = ApplyTimeRemap(CurrentTime);

            // 采样动画
            var pose = Animation.Sample(_skeleton, sampleTime);
            output.CopyFromSkeletonPose(_skeleton, pose);

            // 收集曲线
            // 注意：AnimationClip 中的 Events 可通过外部 Notifier 系统处理
        }

        public override void OnReset()
        {
            CurrentTime = StartPosition * (Animation?.LengthSeconds ?? 1f);
        }

        private float ApplyTimeRemap(float time)
        {
            if (TimeRemapCurve.Count < 2) return time;
            float length = Animation?.LengthSeconds ?? 1f;
            if (length <= 0f) return time;

            float normalizedTime = time / length;

            // 在重映射曲线中查找区间
            for (int i = 0; i < TimeRemapCurve.Count - 1; i++)
            {
                if (normalizedTime >= TimeRemapCurve[i].X && normalizedTime <= TimeRemapCurve[i + 1].X)
                {
                    float t1 = TimeRemapCurve[i].X;
                    float t2 = TimeRemapCurve[i + 1].X;
                    float v1 = TimeRemapCurve[i].Y;
                    float v2 = TimeRemapCurve[i + 1].Y;
                    float span = t2 - t1;
                    if (span <= 0f) return time;
                    float alpha = (normalizedTime - t1) / span;
                    return MathF.Max(0f, (v1 + (v2 - v1) * alpha) * length);
                }
            }

            return time;
        }
    }

    // ──────────────────────────────────────────────────────────────
    //  多姿势混合节点（AnimPoseBlendNode）
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 将多个输入姿势按权重混合在一起。权重会归一化。
    /// 相当于 UE5 的 Blend Poses Together 节点。
    /// </summary>
    public sealed class AnimPoseBlendNode : AnimNodeBase
    {
        /// <summary>输入姿势列表。</summary>
        public List<AnimNodeBase> InputPoses { get; } = new();

        /// <summary>每个输入姿势的权重。</summary>
        public List<float> Weights { get; } = new();

        /// <summary>是否自动归一化权重。</summary>
        public bool bNormalizeWeights { get; set; } = true;

        public AnimPoseBlendNode()
        {
            NodeName = "PoseBlend";
        }

        /// <summary>添加一个输入姿势及权重。</summary>
        public void AddPose(AnimNodeBase pose, float weight = 1f)
        {
            if (pose != null)
            {
                InputPoses.Add(pose);
                Weights.Add(weight);
            }
        }

        public override void OnInitialize(Skeleton skeleton)
        {
            foreach (var pose in InputPoses)
                pose.OnInitialize(skeleton);
        }

        public override void OnUpdate(float deltaTime)
        {
            foreach (var pose in InputPoses)
                pose.OnUpdate(deltaTime);
        }

        public override void OnEvaluate(AnimPoseOutput output)
        {
            if (InputPoses.Count == 0) return;

            int count = InputPoses.Count;
            int boneCount = output.BoneCount;

            // 准备权重
            var weights = new float[count];
            float totalWeight = 0f;
            for (int i = 0; i < count; i++)
            {
                float w = i < Weights.Count ? Math.Max(0f, Weights[i]) : 1f;
                weights[i] = w;
                totalWeight += w;
            }

            if (totalWeight <= 0f)
            {
                InputPoses[0]?.OnEvaluate(output);
                return;
            }

            // 归一化
            if (bNormalizeWeights)
            {
                float invTotal = 1f / totalWeight;
                for (int i = 0; i < count; i++)
                    weights[i] *= invTotal;
            }

            // 累加混合
            var transSum = new Vector3[boneCount];
            var scaleSum = new Vector3[boneCount];
            var rotSum = new Quaternion[boneCount];
            var reference = new Quaternion[boneCount];
            var hasReference = new bool[boneCount];
            float finalWeightSum = 0f;

            for (int p = 0; p < count; p++)
            {
                float w = weights[p];
                if (w <= 0f) continue;

                var poseOutput = AnimPoseOutput.Create(boneCount);
                InputPoses[p].OnEvaluate(poseOutput);
                finalWeightSum += w;

                for (int i = 0; i < boneCount; i++)
                {
                    transSum[i] += poseOutput.Translations[i] * w;
                    scaleSum[i] += poseOutput.Scales[i] * w;

                    var q = poseOutput.Rotations[i];
                    if (!hasReference[i])
                    {
                        reference[i] = q;
                        hasReference[i] = true;
                    }
                    else if (Quaternion.Dot(reference[i], q) < 0f)
                    {
                        q = -q;
                    }
                    rotSum[i] += q * w;
                }
            }

            if (finalWeightSum <= 0f)
            {
                InputPoses[0]?.OnEvaluate(output);
                return;
            }

            float inv = 1f / finalWeightSum;
            for (int i = 0; i < boneCount; i++)
            {
                output.Translations[i] = transSum[i] * inv;
                output.Scales[i] = scaleSum[i] * inv;
                var r = rotSum[i];
                float lenSq = r.X * r.X + r.Y * r.Y + r.Z * r.Z + r.W * r.W;
                if (lenSq > 1e-10f)
                    r *= 1f / MathF.Sqrt(lenSq);
                else
                    r = Quaternion.Identity;
                output.Rotations[i] = r;
            }
        }

        public override void OnReset()
        {
            foreach (var pose in InputPoses)
                pose.OnReset();
        }
    }

    // ──────────────────────────────────────────────────────────────
    //  插槽节点（AnimSlotNode）
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 插槽节点。蒙太奇通过插槽名称注入到此节点。
    /// 当有蒙太奇播放时，插槽节点输出蒙太奇姿态；
    /// 否则透传基础姿态。
    /// 相当于 UE5 的 Slot 节点。
    /// </summary>
    public sealed class AnimSlotNode : AnimNodeBase
    {
        /// <summary>插槽名称。</summary>
        public string SlotName { get; set; } = "DefaultSlot";

        /// <summary>基础姿势（下层动画图）。</summary>
        public AnimNodeBase? BasePose { get; set; }

        /// <summary>蒙太奇实例列表引用（由 AnimGraphEvaluator 设置）。</summary>
        internal List<MontageInstance>? MontageInstances;

        /// <summary>蒙太奇混合权重。</summary>
        public float MontageWeight { get; set; } = 1f;

        public AnimSlotNode()
        {
            NodeName = "Slot";
        }

        public override void OnInitialize(Skeleton skeleton)
        {
            BasePose?.OnInitialize(skeleton);
        }

        public override void OnUpdate(float deltaTime)
        {
            BasePose?.OnUpdate(deltaTime);
        }

        public override void OnEvaluate(AnimPoseOutput output)
        {
            // 1. 评估基础姿势
            BasePose?.OnEvaluate(output);

            // 2. 检查是否有蒙太奇使用此插槽
            if (MontageInstances == null || MontageWeight <= 0f) return;

            foreach (var montageInst in MontageInstances)
            {
                if (!montageInst.IsActive) continue;
                if (!montageInst.UsesSlot(SlotName)) continue;

                // 采样蒙太奇当前帧
                var montagePose = montageInst.SampleCurrentPose();
                if (montagePose == null) continue;

                // 混合蒙太奇姿态到输出
                float weight = montageInst.GetSlotWeight(SlotName) * MontageWeight;
                if (weight <= 0f) continue;

                // 将蒙太奇姿态叠加到输出
                int count = Math.Min(output.BoneCount, montagePose.BoneCount);
                for (int i = 0; i < count; i++)
                {
                    output.Translations[i] = Vector3.Lerp(output.Translations[i], montagePose.Translations[i], weight);
                    output.Scales[i] = Vector3.Lerp(output.Scales[i], montagePose.Scales[i], weight);
                    var ra = output.Rotations[i];
                    var rb = montagePose.Rotations[i];
                    if (Quaternion.Dot(ra, rb) < 0f) rb = -rb;
                    output.Rotations[i] = Quaternion.Normalize(Quaternion.Slerp(ra, rb, weight));
                }
            }
        }

        public override void OnReset()
        {
            BasePose?.OnReset();
        }
    }

    // ──────────────────────────────────────────────────────────────
    //  叠加动画节点（AnimApplyAdditiveNode）
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 将叠加姿态以加法方式应用到基础姿态上。
    /// 相当于 UE5 的 Apply Additive 节点。
    /// </summary>
    public sealed class AnimApplyAdditiveNode : AnimNodeBase
    {
        /// <summary>基础姿势。</summary>
        public AnimNodeBase? BasePose { get; set; }

        /// <summary>叠加姿势。</summary>
        public AnimNodeBase? AdditivePose { get; set; }

        /// <summary>叠加权重。</summary>
        public float AdditiveWeight { get; set; } = 1f;

        /// <summary>叠加骨骼过滤器（为空时影响全身）。</summary>
        public HashSet<string> BoneFilter { get; } = new(StringComparer.OrdinalIgnoreCase);

        private Skeleton? _skeleton;

        public AnimApplyAdditiveNode()
        {
            NodeName = "ApplyAdditive";
        }

        public override void OnInitialize(Skeleton skeleton)
        {
            _skeleton = skeleton;
            BasePose?.OnInitialize(skeleton);
            AdditivePose?.OnInitialize(skeleton);
        }

        public override void OnUpdate(float deltaTime)
        {
            BasePose?.OnUpdate(deltaTime);
            AdditivePose?.OnUpdate(deltaTime);
        }

        public override void OnEvaluate(AnimPoseOutput output)
        {
            if (_skeleton == null) return;

            // 1. 评估基础姿势
            BasePose?.OnEvaluate(output);

            // 2. 评估叠加姿势并叠加
            if (AdditivePose == null || AdditiveWeight <= 0f) return;

            var addOutput = AnimPoseOutput.Create(output.BoneCount);
            AdditivePose.OnEvaluate(addOutput);

            float w = Math.Clamp(AdditiveWeight, 0f, 1f);
            bool useFilter = BoneFilter.Count > 0;

            for (int i = 0; i < output.BoneCount; i++)
            {
                if (useFilter)
                {
                    string boneName = i < _skeleton.Bones.Count ? _skeleton.Bones[i].Name : string.Empty;
                    if (!BoneFilter.Contains(boneName, StringComparer.OrdinalIgnoreCase))
                        continue;
                }

                // 叠加平移
                output.Translations[i] += addOutput.Translations[i] * w;

                // 叠加缩放
                output.Scales[i] *= Vector3.Lerp(Vector3.One, addOutput.Scales[i], w);

                // 叠加旋转：additive 旋转作为 delta
                var addRot = Quaternion.Slerp(Quaternion.Identity, addOutput.Rotations[i], w);
                output.Rotations[i] = Quaternion.Normalize(output.Rotations[i] * addRot);
            }
        }

        public override void OnReset()
        {
            BasePose?.OnReset();
            AdditivePose?.OnReset();
        }
    }

    // ──────────────────────────────────────────────────────────────
    //  骨骼修改节点（AnimModifyBoneNode）
    // ──────────────────────────────────────────────────────────────

    /// <summary>变换空间选择。</summary>
    public enum AnimModifyBoneSpace
    {
        /// <summary>骨骼局部空间。</summary>
        Local,
        /// <summary>世界空间。</summary>
        World,
        /// <summary>另一个骨骼的空间。</summary>
        BoneSpace,
    }

    /// <summary>变换模式。</summary>
    public enum AnimModifyBoneMode
    {
        /// <summary>替换（覆盖原值）。</summary>
        Replace,
        /// <summary>累加到原值上。</summary>
        Add,
        /// <summary>缩放原值。</summary>
        Scale,
    }

    /// <summary>
    /// 修改指定骨骼的变换。支持平移/旋转/缩放，可选择变换空间和混合模式。
    /// 相当于 UE5 的 Modify Bone 节点。
    /// </summary>
    public sealed class AnimModifyBoneNode : AnimNodeBase
    {
        /// <summary>要修改的骨骼名称。</summary>
        public string BoneName { get; set; } = string.Empty;

        /// <summary>平移值。</summary>
        public Vector3 Translation { get; set; }

        /// <summary>旋转值。</summary>
        public Quaternion Rotation { get; set; } = Quaternion.Identity;

        /// <summary>缩放值。</summary>
        public Vector3 Scale { get; set; } = Vector3.One;

        /// <summary>变换空间。</summary>
        public AnimModifyBoneSpace TranslationSpace { get; set; } = AnimModifyBoneSpace.Local;
        public AnimModifyBoneSpace RotationSpace { get; set; } = AnimModifyBoneSpace.Local;
        public AnimModifyBoneSpace ScaleSpace { get; set; } = AnimModifyBoneSpace.Local;

        /// <summary>平移模式。</summary>
        public AnimModifyBoneMode TranslationMode { get; set; } = AnimModifyBoneMode.Replace;
        public AnimModifyBoneMode RotationMode { get; set; } = AnimModifyBoneMode.Replace;
        public AnimModifyBoneMode ScaleMode { get; set; } = AnimModifyBoneMode.Replace;

        /// <summary>输入姿势。</summary>
        public AnimNodeBase? InputPose { get; set; }

        private Skeleton? _skeleton;
        private int _boneIndex = -1;

        public AnimModifyBoneNode()
        {
            NodeName = "ModifyBone";
        }

        public override void OnInitialize(Skeleton skeleton)
        {
            _skeleton = skeleton;
            _boneIndex = skeleton.FindBone(BoneName);
            InputPose?.OnInitialize(skeleton);
        }

        public override void OnUpdate(float deltaTime)
        {
            InputPose?.OnUpdate(deltaTime);
        }

        public override void OnEvaluate(AnimPoseOutput output)
        {
            InputPose?.OnEvaluate(output);
            if (_boneIndex < 0 || _boneIndex >= output.BoneCount) return;

            // 应用平移
            switch (TranslationMode)
            {
                case AnimModifyBoneMode.Replace:
                    output.Translations[_boneIndex] = Translation;
                    break;
                case AnimModifyBoneMode.Add:
                    output.Translations[_boneIndex] += Translation;
                    break;
                case AnimModifyBoneMode.Scale:
                    output.Translations[_boneIndex] *= Translation;
                    break;
            }

            // 应用旋转
            switch (RotationMode)
            {
                case AnimModifyBoneMode.Replace:
                    output.Rotations[_boneIndex] = Rotation;
                    break;
                case AnimModifyBoneMode.Add:
                    output.Rotations[_boneIndex] = Quaternion.Normalize(Rotation * output.Rotations[_boneIndex]);
                    break;
                case AnimModifyBoneMode.Scale:
                    // 缩放旋转 = Slerp(Identity, Rotation, ...) 由外部控制
                    break;
            }

            // 应用缩放
            switch (ScaleMode)
            {
                case AnimModifyBoneMode.Replace:
                    output.Scales[_boneIndex] = Scale;
                    break;
                case AnimModifyBoneMode.Add:
                    output.Scales[_boneIndex] += Scale;
                    break;
                case AnimModifyBoneMode.Scale:
                    output.Scales[_boneIndex] *= Scale;
                    break;
            }
        }

        public override void OnReset()
        {
            InputPose?.OnReset();
        }
    }

    // ──────────────────────────────────────────────────────────────
    //  复制骨骼变换节点（AnimCopyBoneNode）
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 将源骨骼的变换复制到目标骨骼。
    /// 相当于 UE5 的 Copy Bone 节点。
    /// </summary>
    public sealed class AnimCopyBoneNode : AnimNodeBase
    {
        /// <summary>源骨骼名称。</summary>
        public string SourceBoneName { get; set; } = string.Empty;

        /// <summary>目标骨骼名称。</summary>
        public string TargetBoneName { get; set; } = string.Empty;

        /// <summary>是否复制平移。</summary>
        public bool bCopyTranslation { get; set; } = true;

        /// <summary>是否复制旋转。</summary>
        public bool bCopyRotation { get; set; } = true;

        /// <summary>是否复制缩放。</summary>
        public bool bCopyScale { get; set; }

        /// <summary>输入姿势。</summary>
        public AnimNodeBase? InputPose { get; set; }

        private int _sourceIndex = -1;
        private int _targetIndex = -1;

        public AnimCopyBoneNode()
        {
            NodeName = "CopyBone";
        }

        public override void OnInitialize(Skeleton skeleton)
        {
            _sourceIndex = skeleton.FindBone(SourceBoneName);
            _targetIndex = skeleton.FindBone(TargetBoneName);
            InputPose?.OnInitialize(skeleton);
        }

        public override void OnUpdate(float deltaTime)
        {
            InputPose?.OnUpdate(deltaTime);
        }

        public override void OnEvaluate(AnimPoseOutput output)
        {
            InputPose?.OnEvaluate(output);
            if (_sourceIndex < 0 || _targetIndex < 0) return;
            if (_sourceIndex >= output.BoneCount || _targetIndex >= output.BoneCount) return;

            if (bCopyTranslation)
                output.Translations[_targetIndex] = output.Translations[_sourceIndex];
            if (bCopyRotation)
                output.Rotations[_targetIndex] = output.Rotations[_sourceIndex];
            if (bCopyScale)
                output.Scales[_targetIndex] = output.Scales[_sourceIndex];
        }

        public override void OnReset()
        {
            InputPose?.OnReset();
        }
    }

    // ──────────────────────────────────────────────────────────────
    //  IK 节点（AnimIKNode）
    // ──────────────────────────────────────────────────────────────

    /// <summary>IK 求解器类型。</summary>
    public enum AnimIKType
    {
        TwoBoneIK,
        FABRIK,
    }

    /// <summary>
    /// IK 动画节点。支持 TwoBoneIK 和 FABRIK 求解。
    /// 相当于 UE5 的 Two Bone IK / FABRIK 节点。
    /// </summary>
    public sealed class AnimIKNode : AnimNodeBase
    {
        /// <summary>IK 求解器类型。</summary>
        public AnimIKType IKType { get; set; } = AnimIKType.TwoBoneIK;

        // TwoBoneIK 设置
        /// <summary>上臂骨骼名称。</summary>
        public string UpperBoneName { get; set; } = string.Empty;

        /// <summary>前臂骨骼名称。</summary>
        public string LowerBoneName { get; set; } = string.Empty;

        /// <summary>手部/末端骨骼名称。</summary>
        public string EffectorBoneName { get; set; } = string.Empty;

        /// <summary>目标位置（世界空间）。</summary>
        public Vector3 EffectorTargetPosition { get; set; }

        /// <summary>目标旋转。</summary>
        public Quaternion EffectorTargetRotation { get; set; } = Quaternion.Identity;

        /// <summary>影响权重 [0, 1]。</summary>
        public float Alpha { get; set; } = 1f;

        /// <summary>链式权重（影响子骨骼的程度）。</summary>
        public float AlphaChain { get; set; } = 0.5f;

        /// <summary>极向量位置（用于控制 TwoBoneIK 弯曲方向）。</summary>
        public Vector3 PoleVectorPosition { get; set; }

        /// <summary>输入姿势。</summary>
        public AnimNodeBase? InputPose { get; set; }

        // FABRIK 设置
        /// <summary>FABRIK 链的骨骼名称列表。</summary>
        public List<string> FabrikChainBones { get; } = new();

        /// <summary>FABRIK 最大迭代次数。</summary>
        public int FabrikMaxIterations { get; set; } = 10;

        /// <summary>FABRIK 收敛容差。</summary>
        public float FabrikTolerance { get; set; } = 0.001f;

        private Skeleton? _skeleton;
        private int _upperIdx = -1, _lowerIdx = -1, _effectorIdx = -1;
        private int[] _fabrikIndices = Array.Empty<int>();

        public AnimIKNode()
        {
            NodeName = "IKNode";
        }

        public override void OnInitialize(Skeleton skeleton)
        {
            _skeleton = skeleton;
            _upperIdx = skeleton.FindBone(UpperBoneName);
            _lowerIdx = skeleton.FindBone(LowerBoneName);
            _effectorIdx = skeleton.FindBone(EffectorBoneName);

            // 解析 FABRIK 链
            _fabrikIndices = new int[FabrikChainBones.Count];
            for (int i = 0; i < FabrikChainBones.Count; i++)
                _fabrikIndices[i] = skeleton.FindBone(FabrikChainBones[i]);

            InputPose?.OnInitialize(skeleton);
        }

        public override void OnUpdate(float deltaTime)
        {
            InputPose?.OnUpdate(deltaTime);
        }

        public override void OnEvaluate(AnimPoseOutput output)
        {
            InputPose?.OnEvaluate(output);
            if (_skeleton == null || Alpha <= 0f) return;

            switch (IKType)
            {
                case AnimIKType.TwoBoneIK:
                    SolveTwoBoneIK(output);
                    break;
                case AnimIKType.FABRIK:
                    SolveFabrik(output);
                    break;
            }
        }

        private void SolveTwoBoneIK(AnimPoseOutput output)
        {
            if (_upperIdx < 0 || _lowerIdx < 0 || _effectorIdx < 0) return;
            if (_upperIdx >= output.BoneCount || _lowerIdx >= output.BoneCount || _effectorIdx >= output.BoneCount) return;

            // 获取世界空间位置
            var world = ComputeWorldMatrices(_skeleton, output);
            var rootPos = GetWorldPosition(world, _upperIdx);
            var midPos = GetWorldPosition(world, _lowerIdx);
            var tipPos = GetWorldPosition(world, _effectorIdx);

            float aLen = Vector3.Distance(rootPos, midPos);
            float bLen = Vector3.Distance(midPos, tipPos);
            if (aLen <= 1e-6f || bLen <= 1e-6f) return;

            var target = EffectorTargetPosition;
            float d = Vector3.Distance(rootPos, target);
            float dClamped = Math.Clamp(d, MathF.Abs(aLen - bLen) + 1e-4f, aLen + bLen - 1e-4f);
            if (d > dClamped)
                target = rootPos + Vector3.Normalize(target - rootPos) * dClamped;

            // 三角形的余弦定律
            var targetDir = Vector3.Normalize(target - rootPos);
            float x = (aLen * aLen - bLen * bLen + d * d) / (2f * d);
            float hSq = aLen * aLen - x * x;
            float h = hSq > 0f ? MathF.Sqrt(hSq) : 0f;

            // 弯曲方向
            Vector3 bendDir;
            var poleDir = PoleVectorPosition - rootPos;
            bendDir = poleDir - targetDir * Vector3.Dot(poleDir, targetDir);
            if (bendDir.LengthSquared() < 1e-8f)
            {
                var curDir = midPos - rootPos;
                bendDir = curDir - targetDir * Vector3.Dot(curDir, targetDir);
            }
            if (bendDir.LengthSquared() < 1e-8f)
                bendDir = Vector3.Cross(targetDir, MathF.Abs(targetDir.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX);
            bendDir = Vector3.Normalize(bendDir);

            var newMidPos = rootPos + targetDir * x + bendDir * h;

            // 旋转上臂
            var curRootSeg = midPos - rootPos;
            var desRootSeg = newMidPos - rootPos;
            var deltaRoot = RotationFromTo(curRootSeg, desRootSeg);
            deltaRoot = Quaternion.Slerp(Quaternion.Identity, deltaRoot, Alpha);
            var rootWorldRot = GetWorldRotation(_skeleton, output, _upperIdx);
            SetWorldRotation(_skeleton, output, _upperIdx, Quaternion.Normalize(deltaRoot * rootWorldRot));

            // 旋转前臂
            var midWorldRot = GetWorldRotation(_skeleton, output, _lowerIdx);
            var curMidSeg = tipPos - midPos;
            var desMidSeg = target - newMidPos;
            var deltaMid = RotationFromTo(curMidSeg, desMidSeg);
            deltaMid = Quaternion.Slerp(Quaternion.Identity, deltaMid, Alpha);
            SetWorldRotation(_skeleton, output, _lowerIdx, Quaternion.Normalize(deltaMid * midWorldRot));
        }

        private void SolveFabrik(AnimPoseOutput output)
        {
            if (_fabrikIndices.Length < 2) return;
            int n = _fabrikIndices.Length;

            // 获取世界空间位置
            var world = ComputeWorldMatrices(_skeleton, output);
            var positions = new Vector3[n];
            var origPositions = new Vector3[n];
            for (int i = 0; i < n; i++)
            {
                int idx = _fabrikIndices[i];
                positions[i] = GetWorldPosition(world, idx);
                origPositions[i] = positions[i];
            }

            // 计算段长度
            var segLengths = new float[n - 1];
            float totalLen = 0f;
            for (int i = 0; i < n - 1; i++)
            {
                segLengths[i] = Vector3.Distance(positions[i], positions[i + 1]);
                totalLen += segLengths[i];
            }

            var rootOrig = positions[0];
            var target = EffectorTargetPosition;

            if (Vector3.Distance(rootOrig, target) >= totalLen)
            {
                // 不可达：拉直链指向目标
                var dir = Vector3.Normalize(target - rootOrig);
                positions[0] = rootOrig;
                for (int i = 1; i < n; i++)
                    positions[i] = positions[i - 1] + dir * segLengths[i - 1];
            }
            else
            {
                for (int iter = 0; iter < FabrikMaxIterations; iter++)
                {
                    // Forward reaching
                    positions[n - 1] = target;
                    for (int i = n - 2; i >= 0; i--)
                    {
                        var dir = Vector3.Normalize(positions[i] - positions[i + 1]);
                        positions[i] = positions[i + 1] + dir * segLengths[i];
                    }

                    // Backward reaching
                    positions[0] = rootOrig;
                    for (int i = 1; i < n; i++)
                    {
                        var dir = Vector3.Normalize(positions[i] - positions[i - 1]);
                        positions[i] = positions[i - 1] + dir * segLengths[i - 1];
                    }

                    if (Vector3.Distance(positions[n - 1], target) <= FabrikTolerance)
                        break;
                }
            }

            // 按 Alpha 混合
            for (int i = 0; i < n; i++)
                positions[i] = Vector3.Lerp(origPositions[i], positions[i], Alpha);

            // 推导旋转
            for (int i = 0; i < n - 1; i++)
            {
                var curSeg = origPositions[i + 1] - origPositions[i];
                var desSeg = positions[i + 1] - positions[i];
                var delta = RotationFromTo(curSeg, desSeg);
                var worldRot = GetWorldRotation(_skeleton, output, _fabrikIndices[i]);
                SetWorldRotation(_skeleton, output, _fabrikIndices[i], Quaternion.Normalize(delta * worldRot));
            }
        }

        public override void OnReset()
        {
            InputPose?.OnReset();
        }

        // ────────────────── 辅助方法 ──────────────────

        private static Matrix4x4[] ComputeWorldMatrices(Skeleton skeleton, AnimPoseOutput pose)
        {
            int n = skeleton.Bones.Count;
            var world = new Matrix4x4[n];
            for (int i = 0; i < n; i++)
            {
                var bone = skeleton.Bones[i];
                var local = Matrix4x4.CreateFromQuaternion(pose.Rotations[i]) *
                            Matrix4x4.CreateScale(pose.Scales[i]);
                local.Translation = pose.Translations[i] + bone.LocalPosition;
                // 注意：此处使用简化版本，完整的应做矩阵连乘
                if (bone.ParentIndex >= 0)
                    world[i] = local * world[bone.ParentIndex];
                else
                    world[i] = local;
            }
            return world;
        }

        private static Vector3 GetWorldPosition(Matrix4x4[] world, int index)
            => new(world[index].M41, world[index].M42, world[index].M43);

        private static Quaternion GetWorldRotation(Skeleton skeleton, AnimPoseOutput pose, int index)
        {
            var rot = pose.Rotations[index];
            var p = skeleton.Bones[index].ParentIndex;
            while (p >= 0)
            {
                rot = pose.Rotations[p] * rot;
                p = skeleton.Bones[p].ParentIndex;
            }
            return rot;
        }

        private static void SetWorldRotation(Skeleton skeleton, AnimPoseOutput pose, int index, Quaternion worldRot)
        {
            var parent = skeleton.Bones[index].ParentIndex;
            var local = parent >= 0
                ? Quaternion.Inverse(GetWorldRotation(skeleton, pose, parent)) * worldRot
                : worldRot;
            pose.Rotations[index] = Quaternion.Normalize(local);
        }

        private static Quaternion RotationFromTo(Vector3 from, Vector3 to)
        {
            var f = from.LengthSquared() < 1e-12f ? Vector3.UnitZ : Vector3.Normalize(from);
            var t = to.LengthSquared() < 1e-12f ? Vector3.UnitZ : Vector3.Normalize(to);
            var dot = Math.Clamp(Vector3.Dot(f, t), -1f, 1f);
            if (dot >= 0.999999f) return Quaternion.Identity;
            if (dot <= -0.999999f)
            {
                var axis = MathF.Abs(f.X) < 0.9f
                    ? Vector3.Normalize(Vector3.Cross(f, Vector3.UnitX))
                    : Vector3.Normalize(Vector3.Cross(f, Vector3.UnitY));
                return Quaternion.CreateFromAxisAngle(axis, MathF.PI);
            }
            var c = Vector3.Cross(f, t);
            return Quaternion.Normalize(new Quaternion(c.X, c.Y, c.Z, 1f + dot));
        }
    }

    // ──────────────────────────────────────────────────────────────
    //  根骨骼运动节点（AnimRootMotionNode）
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 根骨骼运动提取和使能节点。控制是否启用根运动，
    /// 并将根运动增量输出以供移动组件使用。
    /// 相当于 UE5 的 Root Motion 相关节点。
    /// </summary>
    public sealed class AnimRootMotionNode : AnimNodeBase
    {
        /// <summary>是否启用根运动提取。</summary>
        public bool bEnableRootMotion { get; set; } = true;

        /// <summary>根骨骼名称。</summary>
        public string RootBoneName { get; set; } = "root";

        /// <summary>输入姿势。</summary>
        public AnimNodeBase? InputPose { get; set; }

        /// <summary>提取到的根运动平移增量（每帧）。</summary>
        public Vector3 DeltaTranslation { get; private set; }

        /// <summary>提取到的根运动旋转增量（每帧）。</summary>
        public Quaternion DeltaRotation { get; private set; } = Quaternion.Identity;

        private Skeleton? _skeleton;
        private int _rootBoneIndex;
        private Vector3 _lastTranslation;
        private Quaternion _lastRotation = Quaternion.Identity;
        private bool _hasLast;

        public AnimRootMotionNode()
        {
            NodeName = "RootMotion";
        }

        public override void OnInitialize(Skeleton skeleton)
        {
            _skeleton = skeleton;
            _rootBoneIndex = skeleton.FindBone(RootBoneName);
            if (_rootBoneIndex < 0) _rootBoneIndex = 0;
            InputPose?.OnInitialize(skeleton);
        }

        public override void OnUpdate(float deltaTime)
        {
            InputPose?.OnUpdate(deltaTime);
        }

        public override void OnEvaluate(AnimPoseOutput output)
        {
            InputPose?.OnEvaluate(output);

            if (!bEnableRootMotion || _skeleton == null) return;

            // 提取根骨骼变换
            if (_rootBoneIndex >= 0 && _rootBoneIndex < output.BoneCount)
            {
                var currentTrans = output.Translations[_rootBoneIndex];
                var currentRot = output.Rotations[_rootBoneIndex];

                if (!_hasLast)
                {
                    _lastTranslation = currentTrans;
                    _lastRotation = currentRot;
                    _hasLast = true;
                    DeltaTranslation = Vector3.Zero;
                    DeltaRotation = Quaternion.Identity;
                }
                else
                {
                    DeltaTranslation = currentTrans - _lastTranslation;

                    var delta = Quaternion.Normalize(Quaternion.Inverse(_lastRotation) * currentRot);
                    if (Quaternion.Dot(delta, Quaternion.Identity) < 0f) delta = -delta;
                    DeltaRotation = delta;

                    _lastTranslation = currentTrans;
                    _lastRotation = currentRot;
                }

                output.RootMotionDeltaTranslation = DeltaTranslation;
                output.RootMotionDeltaRotation = DeltaRotation;
            }
        }

        public override void OnReset()
        {
            InputPose?.OnReset();
            _hasLast = false;
            DeltaTranslation = Vector3.Zero;
            DeltaRotation = Quaternion.Identity;
        }
    }
}
