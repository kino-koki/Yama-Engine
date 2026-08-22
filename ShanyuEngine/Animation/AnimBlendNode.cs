using System;
using System.Collections.Generic;
using System.Numerics;
using ShanyuEngine.Skinning;

namespace ShanyuEngine.Animation
{
    // ============================================================================================
    //  AnimBlendNode.cs
    //
    //  混合动画节点（Animation Blend Nodes）。
    //
    //  本系统提供多种动画混合节点，可在 AnimGraph 中作为节点使用：
    //    - AnimBlendSpaceNode          ：混合空间节点（播放 BlendSpace 资源）
    //    - AnimBlendByBoolNode         ：布尔切换混合
    //    - AnimBlendByEnumNode         ：枚举多路混合
    //    - AnimBlendByIntNode          ：整数键值混合
    //    - AnimLayeredBoneBlendNode    ：分层骨骼混合（按骨骼名称过滤层）
    // ============================================================================================

    // ──────────────────────────────────────────────────────────────
    //  BlendSpace 播放器（在 AnimGraph 中使用的混合空间节点）
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 混合空间动画节点。直接播放一个 BlendSpace 资源，根据参数坐标采样。
    /// 相当于 UE5 的 BlendSpacePlayer 节点。
    /// </summary>
    public sealed class AnimBlendSpaceNode : AnimNodeBase
    {
        /// <summary>混合空间资源引用。</summary>
        public BlendSpace? BlendSpace { get; set; }

        /// <summary>混合参数 X。</summary>
        public float BlendParameterX { get; set; }

        /// <summary>混合参数 Y（2D 混合空间使用）。</summary>
        public float BlendParameterY { get; set; }

        /// <summary>播放速率。</summary>
        public float PlayRate { get; set; } = 1f;

        /// <summary>起始位置（标准化时间 [0,1]）。</summary>
        public float StartPosition { get; set; }

        /// <summary>是否循环。</summary>
        public bool bLoop { get; set; } = true;

        /// <summary>当前播放时间（秒）。</summary>
        public float CurrentTime { get; private set; }

        /// <summary>Skeleton 引用（外部设置）。</summary>
        private Skeleton? _skeleton;

        public AnimBlendSpaceNode()
        {
            NodeName = "BlendSpace";
        }

        public override void OnInitialize(Skeleton skeleton)
        {
            _skeleton = skeleton;
            CurrentTime = StartPosition * (BlendSpace?.LengthSeconds ?? 1f);
        }

        public override void OnUpdate(float deltaTime)
        {
            CurrentTime += deltaTime * PlayRate;
            float length = BlendSpace?.LengthSeconds ?? 1f;
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
            if (BlendSpace == null || _skeleton == null) return;

            // 设置混合参数到 BlendSpace
            BlendSpace.BlendParameterX = BlendParameterX;
            BlendSpace.BlendParameterY = BlendParameterY;

            // 采样混合空间
            var pose = BlendSpace.Sample(_skeleton, CurrentTime);

            // 复制到输出
            output.CopyFromSkeletonPose(_skeleton, pose);

            // 提取根运动（如果启用）
            if (BlendSpace.ApplyRootMotion)
            {
                // 根运动由外部 AnimInstance 统一提取
            }
        }

        public override void OnReset()
        {
            CurrentTime = StartPosition * (BlendSpace?.LengthSeconds ?? 1f);
        }
    }

    // ──────────────────────────────────────────────────────────────
    //  布尔切换混合节点（AnimBlendByBoolNode）
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 根据布尔值在两个输入姿势之间切换。
    /// 相当于 UE5 的 Blend Poses by Bool 节点。
    /// </summary>
    public sealed class AnimBlendByBoolNode : AnimNodeBase
    {
        /// <summary>布尔值输入。</summary>
        public bool BoolValue { get; set; }

        /// <summary>True 时的姿势。</summary>
        public AnimNodeBase? TruePose { get; set; }

        /// <summary>False 时的姿势。</summary>
        public AnimNodeBase? FalsePose { get; set; }

        /// <summary>切换过渡时长（秒）。为 0 时瞬时切换。</summary>
        public float BlendTime { get; set; } = 0.25f;

        // 运行时混合状态
        private bool _previousBool;
        private float _blendTimer;
        private bool _isBlending;

        public AnimBlendByBoolNode()
        {
            NodeName = "BlendByBool";
        }

        public override void OnInitialize(Skeleton skeleton)
        {
            TruePose?.OnInitialize(skeleton);
            FalsePose?.OnInitialize(skeleton);
            _previousBool = BoolValue;
        }

        public override void OnUpdate(float deltaTime)
        {
            TruePose?.OnUpdate(deltaTime);
            FalsePose?.OnUpdate(deltaTime);

            // 检测布尔值变化，触发过渡
            if (BoolValue != _previousBool)
            {
                _isBlending = true;
                _blendTimer = 0f;
                _previousBool = BoolValue;
            }

            if (_isBlending)
            {
                _blendTimer += deltaTime;
                if (_blendTimer >= BlendTime)
                {
                    _isBlending = false;
                    _blendTimer = BlendTime;
                }
            }
        }

        public override void OnEvaluate(AnimPoseOutput output)
        {
            if (!_isBlending || BlendTime <= 0f)
            {
                // 非过渡状态：直接输出选择的姿势
                var selected = BoolValue ? TruePose : FalsePose;
                if (selected != null)
                {
                    selected.OnEvaluate(output);
                }
                return;
            }

            // 过渡中：混合两个姿势
            float weight = Math.Clamp(_blendTimer / BlendTime, 0f, 1f);
            // 如果从 false -> true，weight 从 0 到 1 混合到 TruePose
            // 如果从 true -> false，weight 从 0 到 1 混合到 FalsePose
            bool transitioningToTrue = BoolValue;

            var poseA = AnimPoseOutput.Create(output.BoneCount);
            var poseB = AnimPoseOutput.Create(output.BoneCount);

            if (transitioningToTrue)
            {
                FalsePose?.OnEvaluate(poseA);
                TruePose?.OnEvaluate(poseB);
            }
            else
            {
                TruePose?.OnEvaluate(poseA);
                FalsePose?.OnEvaluate(poseB);
            }

            BlendPoses(poseA, poseB, weight, output);
        }

        public override void OnReset()
        {
            TruePose?.OnReset();
            FalsePose?.OnReset();
            _isBlending = false;
            _blendTimer = 0f;
        }

        private static void BlendPoses(AnimPoseOutput a, AnimPoseOutput b, float weight, AnimPoseOutput output)
        {
            float w = Math.Clamp(weight, 0f, 1f);
            int count = Math.Min(a.BoneCount, Math.Min(b.BoneCount, output.BoneCount));
            for (int i = 0; i < count; i++)
            {
                output.Translations[i] = Vector3.Lerp(a.Translations[i], b.Translations[i], w);
                output.Scales[i] = Vector3.Lerp(a.Scales[i], b.Scales[i], w);
                var ra = a.Rotations[i];
                var rb = b.Rotations[i];
                if (Quaternion.Dot(ra, rb) < 0f) rb = -rb;
                output.Rotations[i] = Quaternion.Normalize(Quaternion.Slerp(ra, rb, w));
            }
        }
    }

    // ──────────────────────────────────────────────────────────────
    //  枚举混合节点（AnimBlendByEnumNode）
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 根据枚举值在多个输入姿势之间选择/混合。
    /// 每个枚举值对应一个通道姿势。
    /// 相当于 UE5 的 Blend Poses by Enum 节点。
    /// </summary>
    public sealed class AnimBlendByEnumNode : AnimNodeBase
    {
        /// <summary>枚举值到姿势的映射。</summary>
        public Dictionary<int, AnimNodeBase> PoseChannels { get; } = new();

        /// <summary>当前枚举值。</summary>
        public int EnumValue { get; set; }

        /// <summary>过渡时长（秒）。</summary>
        public float BlendTime { get; set; } = 0.25f;

        private int _previousEnum;
        private float _blendTimer;
        private bool _isBlending;

        public AnimBlendByEnumNode()
        {
            NodeName = "BlendByEnum";
        }

        /// <summary>添加枚举通道。</summary>
        public void AddChannel(int enumValue, AnimNodeBase pose)
        {
            if (pose != null)
                PoseChannels[enumValue] = pose;
        }

        public override void OnInitialize(Skeleton skeleton)
        {
            foreach (var kv in PoseChannels)
                kv.Value.OnInitialize(skeleton);
            _previousEnum = EnumValue;
        }

        public override void OnUpdate(float deltaTime)
        {
            foreach (var kv in PoseChannels)
                kv.Value.OnUpdate(deltaTime);

            if (EnumValue != _previousEnum)
            {
                _isBlending = true;
                _blendTimer = 0f;
                _previousEnum = EnumValue;
            }

            if (_isBlending)
            {
                _blendTimer += deltaTime;
                if (_blendTimer >= BlendTime)
                {
                    _isBlending = false;
                }
            }
        }

        public override void OnEvaluate(AnimPoseOutput output)
        {
            if (!PoseChannels.TryGetValue(EnumValue, out var currentPose))
            {
                currentPose = null;
            }

            if (!_isBlending || BlendTime <= 0f || !PoseChannels.TryGetValue(_previousEnum, out var previousPose))
            {
                previousPose = null;
            }

            if (!_isBlending || previousPose == null)
            {
                // 不处于过渡或没有上一姿势：直接输出当前枚举的姿势
                currentPose?.OnEvaluate(output);
                return;
            }

            // 过渡中
            float weight = Math.Clamp(_blendTimer / BlendTime, 0f, 1f);
            var poseA = AnimPoseOutput.Create(output.BoneCount);
            var poseB = AnimPoseOutput.Create(output.BoneCount);
            previousPose.OnEvaluate(poseA);
            currentPose?.OnEvaluate(poseB);

            BlendPoses(poseA, poseB, weight, output);
        }

        public override void OnReset()
        {
            foreach (var kv in PoseChannels)
                kv.Value.OnReset();
            _isBlending = false;
            _blendTimer = 0f;
        }

        private static void BlendPoses(AnimPoseOutput a, AnimPoseOutput b, float weight, AnimPoseOutput output)
        {
            float w = Math.Clamp(weight, 0f, 1f);
            int count = Math.Min(a.BoneCount, Math.Min(b.BoneCount, output.BoneCount));
            for (int i = 0; i < count; i++)
            {
                output.Translations[i] = Vector3.Lerp(a.Translations[i], b.Translations[i], w);
                output.Scales[i] = Vector3.Lerp(a.Scales[i], b.Scales[i], w);
                var ra = a.Rotations[i];
                var rb = b.Rotations[i];
                if (Quaternion.Dot(ra, rb) < 0f) rb = -rb;
                output.Rotations[i] = Quaternion.Normalize(Quaternion.Slerp(ra, rb, w));
            }
        }
    }

    // ──────────────────────────────────────────────────────────────
    //  整数混合节点（AnimBlendByIntNode）
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 根据整数值在多个输入姿势之间选择。
    /// 相当于 UE5 的 Blend Poses by Int 节点。
    /// </summary>
    public sealed class AnimBlendByIntNode : AnimNodeBase
    {
        /// <summary>整数键值到姿势的映射。</summary>
        public Dictionary<int, AnimNodeBase> PoseChannels { get; } = new();

        /// <summary>当前整数键值。</summary>
        public int IntValue { get; set; }

        /// <summary>过渡时长（秒）。</summary>
        public float BlendTime { get; set; } = 0.25f;

        private int _previousInt;
        private float _blendTimer;
        private bool _isBlending;

        public AnimBlendByIntNode()
        {
            NodeName = "BlendByInt";
        }

        /// <summary>添加整数通道。</summary>
        public void AddChannel(int key, AnimNodeBase pose)
        {
            if (pose != null)
                PoseChannels[key] = pose;
        }

        public override void OnInitialize(Skeleton skeleton)
        {
            foreach (var kv in PoseChannels)
                kv.Value.OnInitialize(skeleton);
            _previousInt = IntValue;
        }

        public override void OnUpdate(float deltaTime)
        {
            foreach (var kv in PoseChannels)
                kv.Value.OnUpdate(deltaTime);

            if (IntValue != _previousInt)
            {
                _isBlending = true;
                _blendTimer = 0f;
                _previousInt = IntValue;
            }

            if (_isBlending)
            {
                _blendTimer += deltaTime;
                if (_blendTimer >= BlendTime)
                {
                    _isBlending = false;
                }
            }
        }

        public override void OnEvaluate(AnimPoseOutput output)
        {
            PoseChannels.TryGetValue(IntValue, out var currentPose);
            PoseChannels.TryGetValue(_previousInt, out var previousPose);

            if (!_isBlending || BlendTime <= 0f || previousPose == null || previousPose == currentPose)
            {
                currentPose?.OnEvaluate(output);
                return;
            }

            float weight = Math.Clamp(_blendTimer / BlendTime, 0f, 1f);
            var poseA = AnimPoseOutput.Create(output.BoneCount);
            var poseB = AnimPoseOutput.Create(output.BoneCount);
            previousPose.OnEvaluate(poseA);
            currentPose?.OnEvaluate(poseB);

            BlendPoses(poseA, poseB, weight, output);
        }

        public override void OnReset()
        {
            foreach (var kv in PoseChannels)
                kv.Value.OnReset();
            _isBlending = false;
            _blendTimer = 0f;
        }

        private static void BlendPoses(AnimPoseOutput a, AnimPoseOutput b, float weight, AnimPoseOutput output)
        {
            float w = Math.Clamp(weight, 0f, 1f);
            int count = Math.Min(a.BoneCount, Math.Min(b.BoneCount, output.BoneCount));
            for (int i = 0; i < count; i++)
            {
                output.Translations[i] = Vector3.Lerp(a.Translations[i], b.Translations[i], w);
                output.Scales[i] = Vector3.Lerp(a.Scales[i], b.Scales[i], w);
                var ra = a.Rotations[i];
                var rb = b.Rotations[i];
                if (Quaternion.Dot(ra, rb) < 0f) rb = -rb;
                output.Rotations[i] = Quaternion.Normalize(Quaternion.Slerp(ra, rb, w));
            }
        }
    }

    // ──────────────────────────────────────────────────────────────
    //  分层骨骼混合节点（AnimLayeredBoneBlendNode）
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 分层骨骼混合节点。将多个输入层按骨骼名称过滤后叠加到基础姿态上。
    /// 每层可指定权重、混合深度和骨骼过滤器。
    /// 相当于 UE5 的 LayeredBoneBlend 节点。
    /// </summary>
    public sealed class AnimLayeredBoneBlendNode : AnimNodeBase
    {
        /// <summary>基础姿势（通常来自下层状态机）。</summary>
        public AnimNodeBase? BasePose { get; set; }

        /// <summary>分层列表。</summary>
        public List<AnimLayerBlendInfo> Layers { get; } = new();

        /// <summary>骨架引用。</summary>
        private Skeleton? _skeleton;

        public AnimLayeredBoneBlendNode()
        {
            NodeName = "LayeredBoneBlend";
        }

        public override void OnInitialize(Skeleton skeleton)
        {
            _skeleton = skeleton;
            BasePose?.OnInitialize(skeleton);
            foreach (var layer in Layers)
                layer.Pose?.OnInitialize(skeleton);
        }

        public override void OnUpdate(float deltaTime)
        {
            BasePose?.OnUpdate(deltaTime);
            foreach (var layer in Layers)
                layer.Pose?.OnUpdate(deltaTime);
        }

        /// <summary>添加层。</summary>
        public void AddLayer(AnimNodeBase pose, float weight, string[]? boneFilter = null, float blendDepth = 1f)
        {
            var layer = new AnimLayerBlendInfo
            {
                Pose = pose,
                Weight = weight,
                BlendDepth = blendDepth,
            };
            if (boneFilter != null)
            {
                foreach (var bone in boneFilter)
                    layer.BoneFilter.Add(bone);
            }
            Layers.Add(layer);
        }

        public override void OnEvaluate(AnimPoseOutput output)
        {
            if (_skeleton == null) return;

            // 1. 评估基础姿势
            BasePose?.OnEvaluate(output);

            // 2. 叠加各层
            foreach (var layer in Layers)
            {
                if (layer.Pose == null || layer.Weight <= 0f) continue;

                var layerPose = AnimPoseOutput.Create(output.BoneCount);
                layer.Pose.OnEvaluate(layerPose);

                BlendLayerOntoBase(_skeleton, output, layerPose, layer);
            }
        }

        public override void OnReset()
        {
            BasePose?.OnReset();
            foreach (var layer in Layers)
                layer.Pose?.OnReset();
        }

        private static void BlendLayerOntoBase(Skeleton skeleton, AnimPoseOutput basePose, AnimPoseOutput layerPose, AnimLayerBlendInfo layer)
        {
            int count = Math.Min(basePose.BoneCount, layerPose.BoneCount);
            float weight = Math.Clamp(layer.Weight, 0f, 1f);

            // 如果 BoneFilter 为空，影响所有骨骼
            bool useFilter = layer.BoneFilter.Count > 0;

            for (int i = 0; i < count; i++)
            {
                bool affected;
                if (useFilter)
                {
                    string boneName = i < skeleton.Bones.Count ? skeleton.Bones[i].Name : string.Empty;
                    affected = layer.BoneFilter.Contains(boneName, StringComparer.OrdinalIgnoreCase);
                    // 检查子骨骼（混合深度）
                    if (!affected && layer.BlendDepth > 0f)
                    {
                        affected = IsDescendantOfFilteredBone(skeleton, i, layer.BoneFilter, (int)layer.BlendDepth);
                    }
                }
                else
                {
                    affected = true;
                }

                if (!affected) continue;

                // Override 模式：插值
                basePose.Translations[i] = Vector3.Lerp(basePose.Translations[i], layerPose.Translations[i], weight);
                basePose.Scales[i] = Vector3.Lerp(basePose.Scales[i], layerPose.Scales[i], weight);
                var ra = basePose.Rotations[i];
                var rb = layerPose.Rotations[i];
                if (Quaternion.Dot(ra, rb) < 0f) rb = -rb;
                basePose.Rotations[i] = Quaternion.Normalize(Quaternion.Slerp(ra, rb, weight));
            }
        }

        private static bool IsDescendantOfFilteredBone(Skeleton skeleton, int boneIndex, HashSet<string> filterBones, int maxDepth)
        {
            int current = boneIndex;
            int depth = 0;
            while (current >= 0 && depth <= maxDepth)
            {
                if (current < skeleton.Bones.Count)
                {
                    string name = skeleton.Bones[current].Name;
                    if (filterBones.Contains(name, StringComparer.OrdinalIgnoreCase))
                        return true;
                    current = skeleton.Bones[current].ParentIndex;
                }
                else
                {
                    break;
                }
                depth++;
            }
            return false;
        }
    }

    /// <summary>
    /// 分层骨骼混合中的单层信息。
    /// </summary>
    public sealed class AnimLayerBlendInfo
    {
        /// <summary>该层的姿势输入。</summary>
        public AnimNodeBase? Pose { get; set; }

        /// <summary>混合权重 [0, 1]。</summary>
        public float Weight { get; set; } = 1f;

        /// <summary>骨骼过滤器（为空时影响全身）。</summary>
        public HashSet<string> BoneFilter { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>混合深度。从过滤骨骼向下影响多少层子骨骼。0 表示仅影响过滤骨骼本身。</summary>
        public float BlendDepth { get; set; } = 1f;
    }
}
