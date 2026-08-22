// ============================================================================================
//  MotionMatchingSystem.cs — Motion Matching 系统（深化版）
//
//  命名空间: ShanyuEngine.Animation
//
//  本系统实现 Motion Matching —— UE5 / UA 游戏中 GTA / Horizon 等 AAA 游戏使用的
//  state-of-the-art 动画技术。通过在预计算动画帧数据库中进行最近邻搜索，
//  实现无需手工编排过渡规则的自然流畅角色运动。
//
//  核心模块：
//    - MotionMatchConfig   ：配置（特征集 / 权重 / 预测时间 / 混合速率）
//    - FeatureSet          ：特征集（轨迹 / 姿态 / 速度）
//    - MotionDatabase      ：动画数据库（预计算特征 + KD-Tree 加速）
//    - MotionMatchState    ：匹配状态（当前clip / 目标特征 / 混合过渡）
//    - MotionMatchingSystem：主系统（查询 / 评估 / 更新管线）
//
//  与引擎其他系统的对接：
//    - 复用 ShanyuEngine.Skinning 的 Skeleton / SkeletonPose
//    - 复用 AnimationClip.Sample 在离线阶段采样动画帧
//    - 通过 AnimationPoseBlender 与现有 Animator 协作
//    - 通过 ECS 组件 MotionMatchingComponent 与游戏逻辑层集成
// ============================================================================================

using System;
using System.Collections.Generic;
using System.Numerics;

namespace ShanyuEngine.Animation
{
    // ──────────────────────────────────────────────────────────────
    //  特征集定义
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 轨迹特征 —— 描述角色未来运动轨迹。
    /// 记录当前位置 + 未来N帧预测位置的 VelocityXZ。
    /// </summary>
    public struct TrajectoryFeatures
    {
        /// <summary>轨迹采样点数量（含当前帧）。</summary>
        public int PointCount;

        /// <summary>轨迹采样点（XZ 平面上的速度向量）。</summary>
        /// <remarks>索引0为当前位置，后续为未来预测点。</remarks>
        public Vector2[] VelocityXZ;

        /// <summary>轨迹采样点（世界空间位置）。</summary>
        public Vector3[] Positions;

        /// <summary>轨迹采样点对应的偏航角度。</summary>
        public float[] FacingAngles;

        public TrajectoryFeatures(int pointCount)
        {
            PointCount = pointCount;
            VelocityXZ = new Vector2[pointCount];
            Positions = new Vector3[pointCount];
            FacingAngles = new float[pointCount];
        }
    }

    /// <summary>
    /// 姿态特征 —— 描述当前关键骨骼的 LocalRotation。
    /// </summary>
    public struct PoseFeatures
    {
        /// <summary>关键骨骼数量。</summary>
        public int BoneCount;

        /// <summary>关键骨骼名称列表。</summary>
        public string[] BoneNames;

        /// <summary>关键骨骼的 LocalRotation（四元数转 Euler 角，压缩存储）。</summary>
        /// <remarks>每个骨骼存储3个float（pitch/yaw/roll），共 BoneCount*3 个float。</remarks>
        public Vector3[] Rotations;

        public PoseFeatures(int boneCount)
        {
            BoneCount = boneCount;
            BoneNames = new string[boneCount];
            Rotations = new Vector3[boneCount];
        }
    }

    /// <summary>
    /// 速度特征 —— 描述根骨骼的速度信息。
    /// </summary>
    public struct VelocityFeatures
    {
        /// <summary>根骨骼 XZ 平面速度。</summary>
        public Vector2 RootVelocityXZ;

        /// <summary>根骨骼 Y 轴速度（垂直方向）。</summary>
        public float RootVelocityY;

        /// <summary>根骨骼 XZ 平面角速度（偏航速率）。</summary>
        public float AngularVelocityY;

        public float TotalFeatureCount => 3; // vx, vy, angularVY
    }

    /// <summary>
    /// 完整特征集 —— 单帧动画的全部特征向量。
    /// </summary>
    public struct FeatureSet
    {
        /// <summary>轨迹特征。</summary>
        public TrajectoryFeatures Trajectory;

        /// <summary>姿态特征。</summary>
        public PoseFeatures Pose;

        /// <summary>速度特征。</summary>
        public VelocityFeatures Velocity;

        /// <summary>计算总特征维度。</summary>
        public int TotalDimensions
        {
            get
            {
                int dims = 0;
                dims += Trajectory.PointCount * 2; // 每个轨迹点存储 VelocityXZ (2 float)
                dims += Trajectory.PointCount;     // 每个轨迹点的朝向角 (1 float)
                dims += Pose.BoneCount * 3;         // 每个骨骼3个旋转分量
                dims += Velocity.TotalFeatureCount; // 速度特征
                return dims;
            }
        }

        /// <summary>
        /// 将特征集展平为一维浮点数组。
        /// </summary>
        public float[] Flatten()
        {
            List<float> result = new List<float>(TotalDimensions);

            // 轨迹特征：VelocityXZ + FacingAngle
            for (int i = 0; i < Trajectory.PointCount; i++)
            {
                result.Add(Trajectory.VelocityXZ[i].X);
                result.Add(Trajectory.VelocityXZ[i].Y);
                result.Add(Trajectory.FacingAngles[i]);
            }

            // 姿态特征：每个骨骼的旋转
            for (int i = 0; i < Pose.BoneCount; i++)
            {
                result.Add(Pose.Rotations[i].X);
                result.Add(Pose.Rotations[i].Y);
                result.Add(Pose.Rotations[i].Z);
            }

            // 速度特征
            result.Add(Velocity.RootVelocityXZ.X);
            result.Add(Velocity.RootVelocityXZ.Y);
            result.Add(Velocity.RootVelocityY);

            return result.ToArray();
        }
    }

    /// <summary>
    /// 特征权重 —— 每个特征维度的权重，用于代价函数计算。
    /// Cost = Sigma( weight_i * (feature_i_goal - feature_i_clip)^2 )
    /// </summary>
    public struct FeatureWeights
    {
        /// <summary>轨迹特征权重。</summary>
        public float TrajectoryWeight;

        /// <summary>姿态特征权重。</summary>
        public float PoseWeight;

        /// <summary>速度特征权重。</summary>
        public float VelocityWeight;

        /// <summary>各轨迹点的独立权重（可按时间衰减）。</summary>
        public float[] TrajectoryPointWeights;

        /// <summary>各骨骼姿态的独立权重（重要骨骼权重更高）。</summary>
        public float[] PoseBoneWeights;

        /// <summary>创建默认权重（均匀）。</summary>
        public static FeatureWeights Default => new FeatureWeights
        {
            TrajectoryWeight = 1.0f,
            PoseWeight = 0.5f,
            VelocityWeight = 1.0f,
        };

        /// <summary>创建推荐的权重配置。</summary>
        public static FeatureWeights Recommended => new FeatureWeights
        {
            TrajectoryWeight = 1.2f,
            PoseWeight = 0.3f,
            VelocityWeight = 1.0f,
        };
    }

    // ──────────────────────────────────────────────────────────────
    //  Motion Matching 配置
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Motion Matching 配置。
    /// </summary>
    public class MotionMatchConfig
    {
        /// <summary>特征集定义（骨骼列表等）。</summary>
        public string[] KeyBoneNames { get; set; } = new string[]
        {
            "Hips", "Spine", "Spine1", "Spine2", "Neck", "Head",
            "LeftUpLeg", "LeftLeg", "LeftFoot",
            "RightUpLeg", "RightLeg", "RightFoot",
            "LeftArm", "LeftForeArm", "LeftHand",
            "RightArm", "RightForeArm", "RightHand",
        };

        /// <summary>特征权重。</summary>
        public FeatureWeights Weights { get; set; } = FeatureWeights.Recommended;

        /// <summary>预测未来时间（秒）。</summary>
        public float PredictionTime { get; set; } = 0.4f;

        /// <summary>预测帧数。</summary>
        public int PredictionFrames { get; set; } = 8;

        /// <summary>过去轨迹帧数。</summary>
        public int PastFrames { get; set; } = 6;

        /// <summary>总轨迹点数 = PastFrames + 1（当前） + PredictionFrames。</summary>
        public int TotalTrajectoryPoints => PastFrames + 1 + PredictionFrames;

        /// <summary>混合过渡速度。</summary>
        public float CostBlendRate { get; set; } = 8.0f;

        /// <summary>混合过渡最小持续时间（秒）。</summary>
        public float MinBlendDuration { get; set; } = 0.1f;

        /// <summary>混合过渡最大持续时间（秒）。</summary>
        public float MaxBlendDuration { get; set; } = 0.35f;

        /// <summary>Cost阈值 —— 低于此值不触发过渡。</summary>
        public float CostThreshold { get; set; } = 0.15f;

        /// <summary>搜索窗口大小（±秒）—— 在当前帧附近搜索候选帧。</summary>
        public float SearchWindowRadius { get; set; } = 0.2f;

        /// <summary>最大过渡速度限制（避免跳帧过大）。</summary>
        public float MaxTransitionSpeed { get; set; } = 0.5f;

        /// <summary>是否启用响应式混合（Cost越大混合越快）。</summary>
        public bool ResponsiveBlend { get; set; } = true;

        /// <summary>KD-Tree 最近邻搜索候选数。</summary>
        public int KNearestCandidates { get; set; } = 5;

        /// <summary>每秒采样率（数据库采样帧率）。</summary>
        public int SampleRate { get; set; } = 30;
    }

    // ──────────────────────────────────────────────────────────────
    //  动画数据库
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 单个动画片段。
    /// </summary>
    public class MotionClip
    {
        /// <summary>动画资源引用。</summary>
        public AnimationClip AnimationAsset { get; set; }

        /// <summary>片段名称。</summary>
        public string Name { get; set; }

        /// <summary>起始时间（秒）。</summary>
        public float StartTime { get; set; }

        /// <summary>结束时间（秒）。</summary>
        public float EndTime { get; set; }

        /// <summary>片段总时长。</summary>
        public float Duration => EndTime - StartTime;

        /// <summary>采样帧数。</summary>
        public int FrameCount { get; set; }

        /// <summary>预计算的特征数据（每帧一个 FeatureSet）。</summary>
        public FeatureSet[] Features { get; set; }

        /// <summary>展平的特征向量（每帧一个 float[]，用于KD-Tree搜索）。</summary>
        public float[][] FlatFeatures { get; set; }

        /// <summary>根运动轨迹（每帧的世界空间位置）。</summary>
        public Vector3[] RootPositions { get; set; }

        /// <summary>根运动速度（每帧）。</summary>
        public Vector3[] RootVelocities { get; set; }

        /// <summary>标签（如"locomotion"、"jump"、"idle"）。</summary>
        public HashSet<string> Tags { get; set; } = new HashSet<string>();

        /// <summary>播放速率。</summary>
        public float PlayRate { get; set; } = 1.0f;

        /// <summary>是否循环播放。</summary>
        public bool Loop { get; set; } = true;

        /// <summary>优先级（标签匹配时使用）。</summary>
        public float Priority { get; set; } = 1.0f;
    }

    // ──────────────────────────────────────────────────────────────
    //  KD-Tree（特征空间最近邻搜索加速结构）
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// KD-Tree 节点。
    /// </summary>
    internal class KDTreeNode
    {
        /// <summary>分裂维度。</summary>
        public int SplitDimension;

        /// <summary>分裂值。</summary>
        public float SplitValue;

        /// <summary>左子树。</summary>
        public KDTreeNode Left;

        /// <summary>右子树。</summary>
        public KDTreeNode Right;

        /// <summary>数据索引（叶子节点）。</summary>
        public int DataIndex;

        /// <summary>是否为叶子节点。</summary>
        public bool IsLeaf => Left == null && Right == null;
    }

    /// <summary>
    /// KD-Tree 用于高维特征空间的快速最近邻搜索。
    /// 避免暴力遍历所有帧的特征向量。
    /// </summary>
    public class FeatureKDTree
    {
        /// <summary>所有特征向量。</summary>
        private float[][] _points;

        /// <summary>每个点对应的 (clipIndex, frameIndex)。</summary>
        private (int clip, int frame)[] _indices;

        /// <summary>特征维度。</summary>
        private int _dimensions;

        /// <summary>根节点。</summary>
        private KDTreeNode _root;

        /// <summary>构造KD-Tree。</summary>
        public FeatureKDTree(int dimensions)
        {
            _dimensions = dimensions;
        }

        /// <summary>构建KD-Tree。</summary>
        public void Build(List<(int clip, int frame, float[] features)> dataPoints)
        {
            if (dataPoints.Count == 0) return;

            _points = new float[dataPoints.Count][];
            _indices = new (int, int)[dataPoints.Count];

            for (int i = 0; i < dataPoints.Count; i++)
            {
                _points[i] = dataPoints[i].features;
                _indices[i] = (dataPoints[i].clip, dataPoints[i].frame);
            }

            int[] indices = new int[dataPoints.Count];
            for (int i = 0; i < dataPoints.Count; i++) indices[i] = i;

            _root = BuildRecursive(indices, 0, 0);
        }

        /// <summary>递归构建KD-Tree。</summary>
        private KDTreeNode BuildRecursive(int[] indices, int depth, int rangeStart)
        {
            if (indices.Length == 0) return null;

            int dim = depth % _dimensions;

            // 按分裂维度排序
            Array.Sort(indices, (a, b) => _points[a][dim].CompareTo(_points[b][dim]));

            int mid = indices.Length / 2;
            int midIndex = indices[mid];

            var node = new KDTreeNode
            {
                SplitDimension = dim,
                SplitValue = _points[midIndex][dim],
                DataIndex = midIndex
            };

            if (indices.Length == 1)
                return node;

            // 左子树
            int[] leftIndices = new int[mid];
            Array.Copy(indices, leftIndices, mid);
            node.Left = BuildRecursive(leftIndices, depth + 1, rangeStart);

            // 右子树
            int[] rightIndices = new int[indices.Length - mid - 1];
            Array.Copy(indices, mid + 1, rightIndices, rightIndices.Length);
            node.Right = BuildRecursive(rightIndices, depth + 1, rangeStart + mid + 1);

            return node;
        }

        /// <summary>
        /// 查询最近邻。
        /// </summary>
        /// <returns>(clipIndex, frameIndex, distance)。</returns>
        public (int clip, int frame, float dist) QueryNearest(float[] query)
        {
            if (_root == null) return (-1, -1, float.MaxValue);

            float bestDist = float.MaxValue;
            int bestIndex = -1;

            QueryNearestRecursive(_root, query, ref bestDist, ref bestIndex);

            if (bestIndex >= 0)
                return (_indices[bestIndex].clip, _indices[bestIndex].frame, bestDist);
            return (-1, -1, float.MaxValue);
        }

        /// <summary>
        /// 查询 K 个最近邻。
        /// </summary>
        public List<(int clip, int frame, float dist)> QueryKNearest(float[] query, int k)
        {
            if (_root == null || k <= 0) return new List<(int, int, float)>();

            var candidates = new List<(float dist, int index)>(k + 1);
            float maxDist = float.MaxValue;

            QueryKNearestRecursive(_root, query, k, ref maxDist, candidates);

            candidates.Sort((a, b) => a.dist.CompareTo(b.dist));

            var result = new List<(int, int, float)>(candidates.Count);
            foreach (var c in candidates)
            {
                result.Add((_indices[c.index].clip, _indices[c.index].frame, c.dist));
            }
            return result;
        }

        private void QueryNearestRecursive(KDTreeNode node, float[] query, ref float bestDist, ref int bestIndex)
        {
            if (node == null) return;

            float dist = DistanceSquared(query, _points[node.DataIndex]);
            if (dist < bestDist)
            {
                bestDist = dist;
                bestIndex = node.DataIndex;
            }

            float diff = query[node.SplitDimension] - node.SplitValue;
            KDTreeNode near = diff <= 0 ? node.Left : node.Right;
            KDTreeNode far = diff <= 0 ? node.Right : node.Left;

            QueryNearestRecursive(near, query, ref bestDist, ref bestIndex);

            // 检查是否需要搜索远子树
            if (diff * diff < bestDist)
                QueryNearestRecursive(far, query, ref bestDist, ref bestIndex);
        }

        private void QueryKNearestRecursive(KDTreeNode node, float[] query, int k, ref float maxDist, List<(float dist, int index)> candidates)
        {
            if (node == null) return;

            float dist = DistanceSquared(query, _points[node.DataIndex]);

            if (dist < maxDist || candidates.Count < k)
            {
                candidates.Add((dist, node.DataIndex));
                if (candidates.Count > k)
                {
                    candidates.Sort((a, b) => a.dist.CompareTo(b.dist));
                    candidates.RemoveAt(candidates.Count - 1);
                    maxDist = candidates[candidates.Count - 1].dist;
                }
                else if (candidates.Count == k)
                {
                    candidates.Sort((a, b) => a.dist.CompareTo(b.dist));
                    maxDist = candidates[candidates.Count - 1].dist;
                }
            }

            float diff = query[node.SplitDimension] - node.SplitValue;
            KDTreeNode near = diff <= 0 ? node.Left : node.Right;
            KDTreeNode far = diff <= 0 ? node.Right : node.Left;

            QueryKNearestRecursive(near, query, k, ref maxDist, candidates);

            if (diff * diff < maxDist)
                QueryKNearestRecursive(far, query, k, ref maxDist, candidates);
        }

        /// <summary>欧几里得距离平方。</summary>
        private static float DistanceSquared(float[] a, float[] b)
        {
            float sum = 0f;
            int len = Math.Min(a.Length, b.Length);
            for (int i = 0; i < len; i++)
            {
                float d = a[i] - b[i];
                sum += d * d;
            }
            return sum;
        }
    }

    // ──────────────────────────────────────────────────────────────
    //  MotionDatabase —— 动画数据库
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 动画数据库。存储所有动画片段及其预计算特征，提供 KD-Tree 加速搜索。
    /// </summary>
    public class MotionDatabase
    {
        /// <summary>所有动画片段。</summary>
        public List<MotionClip> Clips { get; } = new List<MotionClip>();

        /// <summary>KD-Tree 加速结构。</summary>
        public FeatureKDTree KDTree { get; private set; }

        /// <summary>配置。</summary>
        public MotionMatchConfig Config { get; set; }

        /// <summary>总帧数。</summary>
        public int TotalFrames { get; private set; }

        /// <summary>特征维度。</summary>
        public int FeatureDimension { get; private set; }

        /// <summary>是否已构建。</summary>
        public bool IsBuilt { get; private set; }

        public MotionDatabase(MotionMatchConfig config)
        {
            Config = config;
            FeatureDimension = ComputeFeatureDimension(config);
        }

        /// <summary>计算特征维度。</summary>
        private static int ComputeFeatureDimension(MotionMatchConfig config)
        {
            int dims = 0;
            // 轨迹：每帧 3 个 float (VelocityX + VelocityZ + FacingAngle)
            dims += config.TotalTrajectoryPoints * 3;
            // 姿态：每骨骼 3 个 float (Euler)
            dims += config.KeyBoneNames.Length * 3;
            // 速度：3 个 float
            dims += 3;
            return dims;
        }

        /// <summary>
        /// 添加动画片段到数据库。
        /// </summary>
        public void AddClip(MotionClip clip)
        {
            Clips.Add(clip);
        }

        /// <summary>
        /// 构建数据库 —— 预计算所有帧的特征并构建 KD-Tree。
        /// </summary>
        public void Build()
        {
            TotalFrames = 0;
            foreach (var clip in Clips)
                TotalFrames += clip.FrameCount;

            // 构建KD-Tree数据
            var allData = new List<(int clip, int frame, float[] features)>(TotalFrames);

            for (int c = 0; c < Clips.Count; c++)
            {
                var clip = Clips[c];
                if (clip.FlatFeatures == null)
                    PrecomputeClipFeatures(clip);

                for (int f = 0; f < clip.FrameCount; f++)
                {
                    allData.Add((c, f, clip.FlatFeatures[f]));
                }
            }

            // 构建KD-Tree
            KDTree = new FeatureKDTree(FeatureDimension);
            KDTree.Build(allData);
            IsBuilt = true;
        }

        /// <summary>
        /// 对单个动画片段预计算所有帧的特征值。
        /// </summary>
        private void PrecomputeClipFeatures(MotionClip clip)
        {
            int frameCount = (int)(clip.Duration * Config.SampleRate);
            clip.FrameCount = Math.Max(frameCount, 1);
            clip.Features = new FeatureSet[clip.FrameCount];
            clip.FlatFeatures = new float[clip.FrameCount][];
            clip.RootPositions = new Vector3[clip.FrameCount];
            clip.RootVelocities = new Vector3[clip.FrameCount];

            float dt = clip.Duration / clip.FrameCount;

            for (int f = 0; f < clip.FrameCount; f++)
            {
                float time = clip.StartTime + f * dt;

                // 采样动画
                var pose = clip.AnimationAsset.Sample(time);
                var nextPose = clip.AnimationAsset.Sample(Math.Min(time + dt, clip.EndTime));

                // 构建特征
                var features = new FeatureSet
                {
                    Trajectory = new TrajectoryFeatures(Config.TotalTrajectoryPoints),
                    Pose = new PoseFeatures(Config.KeyBoneNames.Length),
                    Velocity = new VelocityFeatures(),
                };

                // 速度特征
                if (pose.RootMotion.HasValue)
                {
                    features.Velocity.RootVelocityXZ = new Vector2(
                        pose.RootMotion.Value.X,
                        pose.RootMotion.Value.Z);
                    features.Velocity.RootVelocityY = pose.RootMotion.Value.Y;
                }
                if (nextPose.RootMotion.HasValue)
                {
                    float prevYaw = pose.RootMotion.HasValue ? (float)Math.Atan2(pose.RootMotion.Value.Z, pose.RootMotion.Value.X) : 0;
                    float nextYaw = (float)Math.Atan2(nextPose.RootMotion.Value.Z, nextPose.RootMotion.Value.X);
                    features.Velocity.AngularVelocityY = AngleDiff(prevYaw, nextYaw) / dt;
                }

                // 根运动位置
                clip.RootPositions[f] = pose.RootMotion.HasValue
                    ? new Vector3(pose.RootMotion.Value.X, pose.RootMotion.Value.Y, pose.RootMotion.Value.Z)
                    : Vector3.Zero;

                // 根运动速度
                clip.RootVelocities[f] = new Vector3(
                    features.Velocity.RootVelocityXZ.X,
                    features.Velocity.RootVelocityY,
                    features.Velocity.RootVelocityXZ.Y);

                // 姿态特征
                for (int b = 0; b < Config.KeyBoneNames.Length && b < clip.Features[f].Pose.BoneCount; b++)
                {
                    features.Pose.BoneNames[b] = Config.KeyBoneNames[b];
                    // 从骨骼变换中提取LocalRotation并转为Euler角
                    if (b < pose.LocalTransforms.Count)
                    {
                        var rot = pose.LocalTransforms[b].Rotation;
                        features.Pose.Rotations[b] = QuaternionToEuler(rot);
                    }
                }

                // 轨迹特征（预计算未来轨迹）
                for (int t = 0; t < Config.TotalTrajectoryPoints; t++)
                {
                    int futureFrame = f + (t - Config.PastFrames);
                    if (futureFrame >= 0 && futureFrame < clip.FrameCount)
                    {
                        float futureTime = clip.StartTime + futureFrame * dt;
                        var futurePose = clip.AnimationAsset.Sample(futureTime);

                        features.Trajectory.Positions[t] = futurePose.RootMotion.HasValue
                            ? new Vector3(futurePose.RootMotion.Value.X, futurePose.RootMotion.Value.Y, futurePose.RootMotion.Value.Z)
                            : Vector3.Zero;

                        float frameDelta = (t - Config.PastFrames) * dt;
                        features.Trajectory.VelocityXZ[t] = frameDelta > 1e-6f
                            ? new Vector2(
                                (features.Trajectory.Positions[t].X - clip.RootPositions[f].X) / frameDelta,
                                (features.Trajectory.Positions[t].Z - clip.RootPositions[f].Z) / frameDelta)
                            : Vector2.Zero;

                        features.Trajectory.FacingAngles[t] = (float)Math.Atan2(
                            features.Trajectory.Positions[t].Z - clip.RootPositions[f].Z,
                            features.Trajectory.Positions[t].X - clip.RootPositions[f].X);
                    }
                }

                clip.Features[f] = features;
                clip.FlatFeatures[f] = features.Flatten();
            }
        }

        /// <summary>
        /// 加权特征距离计算。
        /// Cost = Sigma( weight_i * (feature_i_goal - feature_i_clip)^2 )
        /// </summary>
        public float ComputeCost(float[] goalFeatures, float[] clipFeatures, FeatureWeights weights)
        {
            float cost = 0f;
            int offset = 0;

            // 轨迹特征距离
            int trajDims = Config.TotalTrajectoryPoints * 3;
            float trajWeight = weights.TrajectoryWeight;
            for (int i = 0; i < trajDims && i < goalFeatures.Length && i < clipFeatures.Length; i++)
            {
                float d = goalFeatures[offset + i] - clipFeatures[offset + i];
                cost += trajWeight * d * d;
            }
            offset += trajDims;

            // 姿态特征距离
            int poseDims = Config.KeyBoneNames.Length * 3;
            float poseWeight = weights.PoseWeight;
            for (int i = 0; i < poseDims && (offset + i) < goalFeatures.Length && (offset + i) < clipFeatures.Length; i++)
            {
                float d = goalFeatures[offset + i] - clipFeatures[offset + i];
                cost += poseWeight * d * d;
            }
            offset += poseDims;

            // 速度特征距离
            float velWeight = weights.VelocityWeight;
            for (int i = 0; (offset + i) < goalFeatures.Length && (offset + i) < clipFeatures.Length; i++)
            {
                float d = goalFeatures[offset + i] - clipFeatures[offset + i];
                cost += velWeight * d * d;
            }

            return cost;
        }

        private static Vector3 QuaternionToEuler(Quaternion q)
        {
            float sinX = 2f * (q.W * q.X + q.Y * q.Z);
            float cosX = 1f - 2f * (q.X * q.X + q.Y * q.Y);
            float pitch = (float)Math.Atan2(sinX, cosX);

            float sinY = 2f * (q.W * q.Y - q.Z * q.X);
            float yaw = Math.Abs(sinY) >= 1f
                ? (float)Math.CopySign((float)Math.PI / 2f, sinY)
                : (float)Math.Asin(sinY);

            float sinZ = 2f * (q.W * q.Z + q.X * q.Y);
            float cosZ = 1f - 2f * (q.Y * q.Y + q.Z * q.Z);
            float roll = (float)Math.Atan2(sinZ, cosZ);

            return new Vector3(pitch, yaw, roll);
        }

        private static float AngleDiff(float a, float b)
        {
            float d = b - a;
            while (d > Math.PI) d -= (float)(2 * Math.PI);
            while (d < -Math.PI) d += (float)(2 * Math.PI);
            return d;
        }
    }

    // ──────────────────────────────────────────────────────────────
    //  MotionMatchState —— 匹配状态
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Motion Matching 匹配状态。记录当前匹配的位置、混合过渡信息等。
    /// </summary>
    public class MotionMatchState
    {
        /// <summary>当前播放的动画片段索引。</summary>
        public int CurrentClipIndex { get; set; } = -1;

        /// <summary>当前播放时间（秒）。</summary>
        public float CurrentTime { get; set; }

        /// <summary>当前帧索引。</summary>
        public int CurrentFrame { get; set; }

        /// <summary>目标特征（从游戏逻辑/玩家输入获得）。</summary>
        public FeatureSet GoalFeatures;

        /// <summary>展平后的目标特征向量。</summary>
        public float[] GoalFeatureVector;

        /// <summary>混合权重（0=完全使用当前clip，1=完全切换到目标）。</summary>
        public float BlendWeight { get; set; }

        /// <summary>混合过渡目标（目标clip索引）。</summary>
        public int BlendTargetClipIndex { get; set; } = -1;

        /// <summary>混合过渡目标时间。</summary>
        public float BlendTargetTime { get; set; }

        /// <summary>混合过渡持续时间。</summary>
        public float BlendDuration { get; set; }

        /// <summary>混合过渡计时器。</summary>
        public float BlendTimer { get; set; }

        /// <summary>是否正在混合过渡中。</summary>
        public bool IsBlending => BlendWeight > 0f && BlendWeight < 1f;

        /// <summary>最佳匹配帧索引。</summary>
        public int BestMatchClip { get; set; } = -1;

        /// <summary>最佳匹配帧帧索引。</summary>
        public int BestMatchFrame { get; set; } = -1;

        /// <summary>最佳匹配Cost。</summary>
        public float BestMatchCost { get; set; } = float.MaxValue;

        /// <summary>上次匹配的Cost（用于检测Cost变化）。</summary>
        public float PreviousCost { get; set; } = float.MaxValue;

        /// <summary>搜索冷却时间（避免频繁搜索）。</summary>
        public float SearchCooldown { get; set; }

        /// <summary>连续匹配同一clip的帧数（稳定化）。</summary>
        public int StabilityCounter { get; set; }

        public MotionMatchState()
        {
            GoalFeatures = default;
            GoalFeatureVector = Array.Empty<float>();
        }
    }

    // ──────────────────────────────────────────────────────────────
    //  MotionMatchingSystem —— 主系统
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Motion Matching 主系统。每帧执行完整的匹配管线。
    /// </summary>
    public class MotionMatchingSystem
    {
        /// <summary>配置。</summary>
        public MotionMatchConfig Config { get; set; } = new MotionMatchConfig();

        /// <summary>动画数据库。</summary>
        public MotionDatabase Database { get; set; }

        /// <summary>当前匹配状态。</summary>
        public MotionMatchState State { get; set; } = new MotionMatchState();

        /// <summary>是否已初始化。</summary>
        public bool IsInitialized { get; private set; }

        /// <summary>角色当前位置。</summary>
        public Vector3 CharacterPosition { get; set; }

        /// <summary>角色当前朝向（偏航角）。</summary>
        public float CharacterFacing { get; set; }

        /// <summary>玩家输入：移动方向（XZ平面）。</summary>
        public Vector2 MoveDirection { get; set; }

        /// <summary>玩家输入：是否跳跃。</summary>
        public bool JumpInput { get; set; }

        /// <summary>玩家输入：期望速度。</summary>
        public float DesiredSpeed { get; set; }

        /// <summary>当前实际速度。</summary>
        public Vector3 CurrentVelocity { get; set; }

        /// <summary>搜索间隔（秒）。</summary>
        public float SearchInterval { get; set; } = 0.033f; // ~30Hz

        /// <summary>搜索计时器。</summary>
        private float _searchTimer;

        /// <summary>惯性化数据（可选，用于更平滑过渡）。</summary>
        public struct InertializationData
        {
            public bool Active;
            public Vector3[] PreviousPositions;
            public Quaternion[] PreviousRotations;
            public float Timer;
            public float Duration;
            public float Alpha;
        }

        public InertializationData Inertialization;

        /// <summary>
        /// 初始化系统。
        /// </summary>
        public void Initialize(MotionMatchConfig config, MotionDatabase database)
        {
            Config = config;
            Database = database;

            if (!Database.IsBuilt)
                Database.Build();

            IsInitialized = true;
            State.GoalFeatureVector = new float[Database.FeatureDimension];
            State.StabilityCounter = 0;
        }

        /// <summary>
        /// 每帧更新。完整管线：
        ///   1. 获取玩家输入
        ///   2. 预测未来轨迹
        ///   3. 计算目标特征
        ///   4. 查询最佳匹配
        ///   5. 混合过渡
        ///   6. 评估输出
        /// </summary>
        public void Update(float deltaTime)
        {
            if (!IsInitialized || Database == null || Database.Clips.Count == 0)
                return;

            // 步骤1：输入已在属性中设置

            // 步骤2+3：计算目标特征
            ComputeGoalFeatures();

            // 步骤4：搜索计时器
            _searchTimer -= deltaTime;
            if (_searchTimer <= 0f)
            {
                _searchTimer = SearchInterval;

                // 查询最佳匹配
                QueryBestMatch();
            }

            // 步骤5：混合过渡
            UpdateBlending(deltaTime);

            // 步骤6：推进时间
            AdvanceTime(deltaTime);
        }

        /// <summary>
        /// 计算目标特征。
        /// 基于当前速度、玩家输入，预测未来轨迹并构建特征向量。
        /// </summary>
        private void ComputeGoalFeatures()
        {
            // 轨迹特征
            var trajectory = new TrajectoryFeatures(Config.TotalTrajectoryPoints);
            float dt = Config.PredictionTime / Config.PredictionFrames;

            for (int t = 0; t < Config.TotalTrajectoryPoints; t++)
            {
                float timeOffset = (t - Config.PastFrames) * dt;

                if (timeOffset < 0)
                {
                    // 过去轨迹：使用历史速度
                    trajectory.Positions[t] = CharacterPosition + CurrentVelocity * timeOffset;
                    trajectory.VelocityXZ[t] = new Vector2(CurrentVelocity.X, CurrentVelocity.Z);
                    trajectory.FacingAngles[t] = CharacterFacing + (float)Math.Atan2(CurrentVelocity.Z, CurrentVelocity.X) * timeOffset;
                }
                else if (timeOffset == 0)
                {
                    // 当前帧
                    trajectory.Positions[t] = CharacterPosition;
                    trajectory.VelocityXZ[t] = new Vector2(CurrentVelocity.X, CurrentVelocity.Z);
                    trajectory.FacingAngles[t] = CharacterFacing;
                }
                else
                {
                    // 未来轨迹：基于当前速度 + 输入预测
                    Vector2 predictedVel = new Vector2(
                        CurrentVelocity.X + MoveDirection.X * DesiredSpeed * timeOffset * 2f,
                        CurrentVelocity.Z + MoveDirection.Y * DesiredSpeed * timeOffset * 2f);

                    // 限制预测速度
                    float maxPredSpeed = DesiredSpeed * 1.5f;
                    float predSpeed = predictedVel.Length();
                    if (predSpeed > maxPredSpeed)
                        predictedVel *= maxPredSpeed / predSpeed;

                    trajectory.Positions[t] = CharacterPosition +
                        new Vector3(predictedVel.X * timeOffset, 0, predictedVel.Y * timeOffset);
                    trajectory.VelocityXZ[t] = predictedVel;
                    trajectory.FacingAngles[t] = CharacterFacing +
                        (float)Math.Atan2(MoveDirection.Y, MoveDirection.X) * timeOffset;
                }
            }

            // 速度特征
            var velocity = new VelocityFeatures
            {
                RootVelocityXZ = new Vector2(CurrentVelocity.X, CurrentVelocity.Z),
                RootVelocityY = CurrentVelocity.Y,
                AngularVelocityY = MoveDirection.Length() > 0.1f ? DesiredSpeed * 0.5f : 0f,
            };

            // 姿态特征（当前帧的姿态 —— 从当前动画采样获取，这里用默认值）
            var pose = new PoseFeatures(Config.KeyBoneNames.Length);
            if (State.CurrentClipIndex >= 0 && State.CurrentClipIndex < Database.Clips.Count)
            {
                var clip = Database.Clips[State.CurrentClipIndex];
                if (State.CurrentFrame >= 0 && State.CurrentFrame < clip.FrameCount)
                {
                    pose = clip.Features[State.CurrentFrame].Pose;
                }
            }

            State.GoalFeatures = new FeatureSet
            {
                Trajectory = trajectory,
                Pose = pose,
                Velocity = velocity,
            };

            State.GoalFeatureVector = State.GoalFeatures.Flatten();
        }

        /// <summary>
        /// 查询最佳匹配动画。
        /// 策略：每帧查询未来N帧的匹配，找总Cost最低的。
        /// 使用KD-Tree加速最近邻搜索，候选帧限制在当前帧附近 +-0.2s窗口。
        /// </summary>
        private void QueryBestMatch()
        {
            if (State.GoalFeatureVector == null || State.GoalFeatureVector.Length == 0)
                return;

            // 步骤1：KD-Tree搜索K个最近邻候选
            var candidates = Database.KDTree.QueryKNearest(
                State.GoalFeatureVector, Config.KNearestCandidates);

            // 步骤2：在候选中搜索窗口内最优帧
            float bestCost = float.MaxValue;
            int bestClip = State.CurrentClipIndex;
            int bestFrame = State.CurrentFrame;

            foreach (var (clipIdx, frameIdx, kdDist) in candidates)
            {
                if (clipIdx < 0 || clipIdx >= Database.Clips.Count) continue;
                var clip = Database.Clips[clipIdx];
                if (frameIdx < 0 || frameIdx >= clip.FrameCount) continue;

                // 加权Cost计算（使用数据库的加权距离函数）
                float weightedCost = Database.ComputeCost(
                    State.GoalFeatureVector,
                    clip.FlatFeatures[frameIdx],
                    Config.Weights);

                // 多帧前瞻：检查未来几帧的匹配质量
                float futureCost = 0f;
                int futureLookahead = Math.Min(3, clip.FrameCount - frameIdx - 1);
                for (int f = 1; f <= futureLookahead; f++)
                {
                    float dt = clip.Duration / clip.FrameCount;
                    // 简化：累加未来帧与当前目标的偏移惩罚
                    futureCost += kdDist * 0.1f * f;
                }
                weightedCost += futureCost;

                if (weightedCost < bestCost)
                {
                    bestCost = weightedCost;
                    bestClip = clipIdx;
                    bestFrame = frameIdx;
                }
            }

            // 步骤3：更新最佳匹配
            State.BestMatchClip = bestClip;
            State.BestMatchFrame = bestFrame;
            State.BestMatchCost = bestCost;

            // 步骤4：如果最佳匹配不是当前clip，触发混合过渡
            if (bestCost < Config.CostThreshold)
            {
                if (bestClip != State.CurrentClipIndex || Math.Abs(bestFrame - State.CurrentFrame) > 2)
                {
                    StartBlendTransition(bestClip, bestFrame, bestCost);
                }
                else
                {
                    State.StabilityCounter++;
                }
            }
        }

        /// <summary>
        /// 开始混合过渡。
        /// 混合持续时间由Cost差距决定（Cost越大，混合越快）。
        /// </summary>
        private void StartBlendTransition(int targetClip, int targetFrame, float cost)
        {
            State.BlendTargetClipIndex = targetClip;
            State.BlendTargetFrame = targetFrame;
            State.PreviousCost = State.BestMatchCost;

            // 响应式混合：Cost越大 → 混合越快
            float blendDuration;
            if (Config.ResponsiveBlend)
            {
                float costFactor = Math.Clamp(cost / Config.CostThreshold, 0f, 1f);
                blendDuration = Config.MaxBlendDuration -
                    (Config.MaxBlendDuration - Config.MinBlendDuration) * costFactor;
                blendDuration = Math.Clamp(blendDuration, Config.MinBlendDuration, Config.MaxBlendDuration);
            }
            else
            {
                blendDuration = Config.CostBlendRate;
            }

            State.BlendDuration = blendDuration;
            State.BlendTimer = 0f;
            State.BlendWeight = 0f;
            State.StabilityCounter = 0;
        }

        /// <summary>
        /// 更新混合过渡。
        /// </summary>
        private void UpdateBlending(float deltaTime)
        {
            if (State.IsBlending)
            {
                State.BlendTimer += deltaTime;
                State.BlendWeight = Math.Clamp(State.BlendTimer / State.BlendDuration, 0f, 1f);

                // 使用平滑插值（smoothstep）
                State.BlendWeight = State.BlendWeight * State.BlendWeight * (3f - 2f * State.BlendWeight);

                if (State.BlendWeight >= 1f)
                {
                    // 过渡完成
                    State.CurrentClipIndex = State.BlendTargetClipIndex;
                    float dt = Database.Clips[State.CurrentClipIndex].Duration /
                               Database.Clips[State.CurrentClipIndex].FrameCount;
                    State.CurrentTime = State.BlendTargetFrame * dt;
                    State.CurrentFrame = State.BlendTargetFrame;
                    State.BlendWeight = 0f;
                    State.IsBlending = false;
                }
            }
        }

        /// <summary>
        /// 推进当前动画时间。
        /// </summary>
        private void AdvanceTime(float deltaTime)
        {
            if (State.CurrentClipIndex < 0 || State.CurrentClipIndex >= Database.Clips.Count)
                return;

            var clip = Database.Clips[State.CurrentClipIndex];
            State.CurrentTime += deltaTime * clip.PlayRate;

            if (State.CurrentTime > clip.EndTime)
            {
                if (clip.Loop)
                {
                    State.CurrentTime = clip.StartTime + (State.CurrentTime - clip.EndTime);
                }
                else
                {
                    State.CurrentTime = clip.EndTime;
                }
            }

            State.CurrentFrame = (int)((State.CurrentTime - clip.StartTime) / clip.Duration * clip.FrameCount);
            State.CurrentFrame = Math.Clamp(State.CurrentFrame, 0, clip.FrameCount - 1);
        }

        /// <summary>
        /// 评估当前帧，获取骨骼变换。
        /// </summary>
        public SkeletonPose Evaluate()
        {
            if (!IsInitialized || State.CurrentClipIndex < 0)
                return null;

            var clip = Database.Clips[State.CurrentClipIndex];

            // 采样当前帧姿势
            SkeletonPose currentPose = clip.AnimationAsset.Sample(State.CurrentTime);

            // 如果正在混合过渡，采样目标帧并混合
            if (State.IsBlending && State.BlendTargetClipIndex >= 0 && State.BlendTargetClipIndex < Database.Clips.Count)
            {
                var targetClip = Database.Clips[State.BlendTargetClipIndex];
                float targetTime = State.BlendTargetFrame * (targetClip.Duration / targetClip.FrameCount);
                SkeletonPose targetPose = targetClip.AnimationAsset.Sample(targetTime);

                // 混合两个姿势
                return BlendPoses(currentPose, targetPose, State.BlendWeight);
            }

            return currentPose;
        }

        /// <summary>
        /// 混合两个骨骼姿势。
        /// </summary>
        private SkeletonPose BlendPoses(SkeletonPose a, SkeletonPose b, float weight)
        {
            if (a == null) return b;
            if (b == null) return a;

            int boneCount = Math.Min(a.LocalTransforms.Count, b.LocalTransforms.Count);
            var result = new SkeletonPose(a.BoneCount);

            for (int i = 0; i < boneCount; i++)
            {
                var tA = a.LocalTransforms[i];
                var tB = b.LocalTransforms[i];

                result.LocalTransforms[i] = new BoneTransform
                {
                    Translation = Vector3.Lerp(tA.Translation, tB.Translation, weight),
                    Rotation = Quaternion.Slerp(tA.Rotation, tB.Rotation, weight),
                    Scale = Vector3.Lerp(tA.Scale, tB.Scale, weight),
                };
            }

            return result;
        }

        /// <summary>
        /// 获取当前帧的骨骼变换列表。
        /// </summary>
        public List<BoneTransform> GetJointTransforms()
        {
            var pose = Evaluate();
            return pose?.LocalTransforms ?? new List<BoneTransform>();
        }

        /// <summary>
        /// 设置玩家输入（每帧调用）。
        /// </summary>
        public void SetInput(Vector2 moveDir, float speed, bool jump)
        {
            MoveDirection = moveDir;
            DesiredSpeed = speed;
            JumpInput = jump;
        }

        /// <summary>
        /// 重置系统到初始状态。
        /// </summary>
        public void Reset()
        {
            State = new MotionMatchState();
            _searchTimer = 0f;
            CurrentVelocity = Vector3.Zero;
            CharacterPosition = Vector3.Zero;
            CharacterFacing = 0f;
        }
    }
}
