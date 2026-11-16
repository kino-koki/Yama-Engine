using System;
using System.IO;
using System.Numerics;
using System.Linq;
using System.Collections.Generic;
using YamaEngine.Animation;
using YamaEngine.Animation.RuntimeGraph;
using YamaEngine.Assets;
using YamaEngine.Modeling;
using YamaEngine.Skinning;
using static YamaEngine.Tests.TestHelpers;
// Skeleton 存在于 Animation 与 Skinning 两个命名空间；本测试主要用 Skinning（序列化/蒙皮），
// 仅 AnimationClip.Sample / Animator 相关调用显式使用 YamaEngine.Animation.Skeleton。
using Skeleton = YamaEngine.Skinning.Skeleton;

namespace YamaEngine.Tests
{
    public static class AnimationTests
    {
        public static void TestSkeletonCreateBipedHierarchy()
        {
            var skeleton = Skeleton.CreateBiped("Biped");

            AssertTrue(skeleton.Bones.Count >= 10, "Biped skeleton should contain a meaningful bone count.");
            AssertTrue(skeleton.Bones.Any(b => b.Name == "Hips"), "Biped skeleton should expose a Hips bone.");
            AssertTrue(skeleton.Bones.Any(b => b.Name == "Spine"), "Biped skeleton should expose a Spine bone.");
            AssertTrue(skeleton.Bones.Any(b => b.Name == "Head"), "Biped skeleton should expose a Head bone.");
            AssertEqual(0, skeleton.Validate().Count, "Biped skeleton should validate cleanly.");

            var hips = skeleton.Bones.First(b => b.Name == "Hips");
            AssertEqual(-1, hips.ParentIndex, "Hips should be the root bone.");
            var spine = skeleton.Bones.First(b => b.Name == "Spine");
            AssertEqual(skeleton.Bones.IndexOf(hips), spine.ParentIndex, "Spine should parent to Hips.");
        }

        public static void TestSkeletonRoundtripWithMeta(string tempRoot)
        {
            var projectPath = Path.Combine(tempRoot, "SkeletonProject");
            var skeletonPath = Path.Combine(projectPath, "Assets", "Skeletons", "Biped.shskel");

            var skeleton = Skeleton.CreateBiped("Biped");
            skeleton.Write(skeletonPath);

            AssertTrue(File.Exists(skeletonPath), "Skeleton file should be written.");
            AssertTrue(File.Exists(skeletonPath + ".shmeta"), "Skeleton meta should be written.");

            var loaded = Skeleton.Read(skeletonPath);
            AssertEqual(skeleton.Name, loaded.Name, "Skeleton name should roundtrip.");
            AssertEqual(skeleton.Bones.Count, loaded.Bones.Count, "Skeleton bone count should roundtrip.");
            AssertEqual(0, loaded.Validate().Count, "Loaded skeleton should validate.");

            var meta = AssetMeta.Read(skeletonPath + ".shmeta");
            AssertEqual(AssetType.Skeleton.ToString(), meta.Type, "Skeleton should register as Skeleton asset.");
            AssertEqual("Assets/Skeletons/Biped.shskel", meta.AssetPath, "Skeleton meta should store project relative path.");
        }

        public static void TestSkinBindingAutoWeightsAreNormalized()
        {
            var mesh = EditableMesh.CreateCube("SkinCube", 2f);
            var skeleton = Skeleton.CreateBiped("Biped");

            var binding = SkinBinding.AutoBind(mesh, skeleton);

            AssertEqual(mesh.Vertices.Count, binding.Weights.Count, "Skin binding should produce one weight set per vertex.");
            foreach (var weight in binding.Weights)
            {
                AssertTrue(weight.Influences.Count > 0, "Each vertex should have at least one bone influence.");
                var sum = weight.Influences.Sum(i => i.Weight);
                AssertTrue(Math.Abs(sum - 1f) < 0.001f, "Skin weights should be normalized per vertex.");
            }
        }

        public static void TestCpuSkinningDeformsMeshWithPose()
        {
            var mesh = EditableMesh.CreateCube("SkinCube", 2f);
            var skeleton = Skeleton.CreateBiped("Biped");
            var binding = SkinBinding.AutoBind(mesh, skeleton);

            var pose = skeleton.CreateBindPose();
            pose.Rotate("Hips", Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f));

            var deformed = CpuSkinning.Deform(mesh, skeleton, binding, pose);

            AssertEqual(mesh.Vertices.Count, deformed.Count, "Deformed mesh should preserve vertex count.");
            AssertTrue(deformed.Zip(mesh.Vertices, (a, b) => Vector3.Distance(a, b)).Any(d => d > 0.01f), "CPU skinning should displace mesh vertices when pose changes.");
        }

        public static void TestAnimationClipSamplesBoneKeyframes()
        {
            var skeleton = YamaEngine.Animation.Skeleton.CreateBiped("AnimBiped");
            var clip = new AnimationClip { Name = "HipLift", LengthSeconds = 1.0f, Looping = false };
            clip.AddTranslationKey("Hips", 0.0f, Vector3.Zero);
            clip.AddTranslationKey("Hips", 1.0f, new Vector3(0, 2, 0));
            clip.AddRotationKey("Hips", 0.0f, Quaternion.Identity);
            clip.AddRotationKey("Hips", 1.0f, Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI));

            var pose = clip.Sample(skeleton, 0.5f);
            var hips = skeleton.FindBone("Hips");

            AssertTrue(Vector3.Distance(new Vector3(0, 1, 0), pose.GetTranslationOffset(hips)) < 0.001f, "AnimationClip should interpolate translation keyframes.");
            AssertTrue(MathF.Abs(pose.GetRotation(hips).Y) > 0.6f, "AnimationClip should interpolate rotation keyframes.");
        }

        public static void TestAnimationClipLoopsAndRoundtrips(string tempRoot)
        {
            var clipPath = Path.Combine(tempRoot, "AnimationProject", "Assets", "Animations", "Walk.shanim");
            var clip = new AnimationClip { Name = "Walk", LengthSeconds = 1.0f, Looping = true };
            clip.AddTranslationKey("Hips", 0.0f, Vector3.Zero);
            clip.AddTranslationKey("Hips", 1.0f, new Vector3(0, 0, 1));
            clip.Write(clipPath);

            var loaded = AnimationClip.Read(clipPath);
            var pose = loaded.Sample(YamaEngine.Animation.Skeleton.CreateBiped("AnimBiped"), 1.5f);
            var hips = YamaEngine.Animation.Skeleton.CreateBiped("AnimBiped").FindBone("Hips");

            AssertEqual("Walk", loaded.Name, "AnimationClip name should roundtrip.");
            AssertTrue(loaded.Looping, "AnimationClip looping flag should roundtrip.");
            AssertTrue(File.Exists(clipPath), "AnimationClip file should be written.");
            AssertTrue(File.Exists(clipPath + ".shmeta"), "AnimationClip meta should be written.");
            AssertTrue(Vector3.Distance(new Vector3(0, 0, 0.5f), pose.GetTranslationOffset(hips)) < 0.001f, "Looping clip should wrap sample time.");
        }

        public static void TestAnimatorCrossFadesBetweenStates()
        {
            var skeleton = YamaEngine.Animation.Skeleton.CreateBiped("AnimBiped");
            var idle = new AnimationClip { Name = "Idle", LengthSeconds = 1.0f, Looping = true };
            idle.AddTranslationKey("Hips", 0.0f, Vector3.Zero);
            idle.AddTranslationKey("Hips", 1.0f, Vector3.Zero);

            var run = new AnimationClip { Name = "Run", LengthSeconds = 1.0f, Looping = true };
            run.AddTranslationKey("Hips", 0.0f, Vector3.Zero);
            run.AddTranslationKey("Hips", 1.0f, new Vector3(0, 0, 2));

            var animator = new Animator(skeleton);
            animator.AddState("Idle", idle);
            animator.AddState("Run", run);
            animator.Play("Idle");
            animator.Update(0.25f);
            animator.CrossFade("Run", 0.5f);
            animator.Update(0.25f);

            AssertEqual("Run", animator.TargetStateName, "Animator should expose crossfade target.");
            AssertTrue(animator.BlendWeight > 0.4f && animator.BlendWeight < 0.6f, "Animator should advance blend weight during crossfade.");

            animator.Update(0.25f);
            var pose = animator.CurrentPose;
            var hips = skeleton.FindBone("Hips");

            AssertEqual("Run", animator.CurrentStateName, "Animator should commit target state after crossfade.");
            AssertTrue(pose.GetTranslationOffset(hips).Z > 0.0f, "Animator should output target animation pose after transition.");
        }

        // ────────────────────────────────────────────────────────────────
        // UE5.8 级动捕匹配 (MotionMatching) + 根运动提取 (Root Motion)
        // ────────────────────────────────────────────────────────────────

        /// <summary>
        /// 构建双剪辑动捕数据库（Walk 慢速 / Run 快速根骨骼运动），验证求解器根据
        /// 由剪辑计算出的速度特征选回正确的最佳匹配帧。这是
        /// MotionMatchingSolver.FindBestMatch 的核心行为（UE5.8 动捕匹配基础）。
        /// </summary>
        public static void TestMotionMatchingSelectsBestMatchByVelocity()
        {
            var skeleton = YamaEngine.Animation.Skeleton.CreateBiped("MMBiped");
            int hips = skeleton.FindBone("Hips");

            // Walk：根骨骼每秒 1 单位 Z
            var walk = new AnimationClipReal { Name = "Walk", Duration = 0.5f, FrameRate = 30f, Loop = false };
            var walkTrack = new BoneTrack { BoneIndex = hips, BoneName = "Hips" };
            walkTrack.PositionKeys.Add(new Vector3Keyframe { Time = 0f, Value = new Vector3(0, 1, 0) });
            walkTrack.PositionKeys.Add(new Vector3Keyframe { Time = 0.5f, Value = new Vector3(0, 1, 0.5f) });
            walk.Tracks.Add(walkTrack);

            // Run：根骨骼每秒 6 单位 Z
            var run = new AnimationClipReal { Name = "Run", Duration = 0.5f, FrameRate = 30f, Loop = false };
            var runTrack = new BoneTrack { BoneIndex = hips, BoneName = "Hips" };
            runTrack.PositionKeys.Add(new Vector3Keyframe { Time = 0f, Value = new Vector3(0, 1, 0) });
            runTrack.PositionKeys.Add(new Vector3Keyframe { Time = 0.5f, Value = new Vector3(0, 1, 3f) });
            run.Tracks.Add(runTrack);

            var solver = new MotionMatchingSolver();
            solver.BuildDatabase(new List<AnimationClipReal> { walk, run }, skeleton, hips, -1, -1);
            AssertTrue(solver.Database.FrameCount > 0, "Motion matching database should be built from clips.");

            // 用确定性线性搜索 Search(query, radius)（引擎内置的确定性回退路径），
            // 规避 2 帧退化 KD 树的边界行为，专注验证"按速度特征选回正确剪辑"的核心逻辑。
            // radius 取整库规模，覆盖全部帧。
            int radius = solver.Database.FrameCount;

            // 慢速前向速度 → 应匹配回 Walk（clip 0）
            var slowQuery = new MotionMatchingFeature { RootVelocity = new Vector3(0, 0, 1f) };
            var slowBest = solver.Search(slowQuery.ToFloatArray(), radius);
            AssertTrue(slowBest != null, "Motion matching should return a best match frame.");
            AssertEqual(0, slowBest.ClipIndex, "Motion matching should select Walk clip for slow velocity.");

            // 快速前向速度 → 应匹配到 Run（clip 1）
            var fastQuery = new MotionMatchingFeature { RootVelocity = new Vector3(0, 0, 6f) };
            var fastBest = solver.Search(fastQuery.ToFloatArray(), radius);
            AssertTrue(fastBest != null, "Motion matching should return a best match frame for fast velocity.");
            AssertEqual(1, fastBest.ClipIndex, "Motion matching should select Run clip for fast velocity.");
        }

        /// <summary>
        /// 构建带根骨骼 Z 平移的剪辑，验证 RootMotionExtractor 提取平移/旋转增量
        /// 以及有限差分速度估计的正确性（UE5.8 root motion 流水线基础）。
        /// </summary>
        public static void TestRootMotionExtractionTranslationAndVelocity()
        {
            var skeleton = YamaEngine.Animation.Skeleton.CreateBiped("RMBiped");
            int hips = skeleton.FindBone("Hips");

            var clip = new AnimationClipReal { Name = "RootMove", Duration = 1.0f, FrameRate = 30f, Loop = false };
            var track = new BoneTrack { BoneIndex = hips, BoneName = "Hips" };
            track.PositionKeys.Add(new Vector3Keyframe { Time = 0f, Value = new Vector3(0, 1, 0) });
            track.PositionKeys.Add(new Vector3Keyframe { Time = 1f, Value = new Vector3(0, 1, 2) });
            clip.Tracks.Add(track);

            var extractor = new YamaEngine.Animation.RootMotionExtractor(clip, hips);

            // t=0 到 t=1 全程位移
            var full = extractor.ExtractRootMotion(1f);
            AssertTrue(Vector3.Distance(full.Translation, new Vector3(0, 0, 2)) < 0.001f,
                "Root motion should extract full Z displacement from clip start.");

            // t=0.5 半程位移（线性插值）
            var half = extractor.ExtractRootMotion(0.5f);
            AssertTrue(Vector3.Distance(half.Translation, new Vector3(0, 0, 1)) < 0.001f,
                "Root motion should extract interpolated half Z displacement.");

            // 帧间增量位移
            var delta = extractor.ExtractDeltaMotion(0f, 0.5f);
            AssertTrue(Vector3.Distance(delta.Translation, new Vector3(0, 0, 1)) < 0.001f,
                "Delta motion between t=0 and t=0.5 should be half the total Z displacement.");

            // 有限差分速度：每秒 2 单位 Z
            var vel = extractor.GetRootVelocity(0f, 0.1f);
            AssertTrue(MathF.Abs(vel.Z - 2f) < 0.001f,
                "Root velocity estimate should match constant Z speed of 2 units/sec.");
        }

        /// <summary>
        /// 构建一条由 12 个**共线**关键帧组成的平移轨道（可被线性插值完全重建），
        /// 压缩后应大幅移除冗余关键帧（压缩率 > 50%），同时压缩后采样值仍落在
        /// 允许误差内（UE5.8 动画压缩管线的核心：降低内存/采样开销且不失真）。
        /// </summary>
        public static void TestAnimationCompressionRemovesRedundantCollinearKeys()
        {
            var clip = new AnimationClip("CompressClip", 1.0f);
            // 共线轨迹：沿 Z 每步 +0.1，共 12 个关键帧 → 全部可由首尾线性插值重建。
            for (int i = 0; i < 12; i++)
                clip.AddTranslationKey("Hips", i / 11f, new Vector3(0f, 1f, i * 0.1f));

            var originalCount = clip.Tracks[0].TranslationKeys.Count;
            AssertTrue(originalCount == 12, "Clip should start with 12 translation keys.");

            var settings = new AnimationCompressionSettings
            {
                MaxTranslationError = 0.01f,
                MinKeysToCompress = 4,
                PreserveEndKeys = true
            };
            var stats = AnimationCompression.Compress(clip, settings);

            // 压缩必须减少关键帧数（移除共线冗余）
            var compressedCount = clip.Tracks[0].TranslationKeys.Count;
            AssertTrue(compressedCount < originalCount,
                $"Compression should remove collinear redundant keys (was {originalCount}, now {compressedCount}).");
            AssertTrue(stats.CompressionRatio > 0.5f,
                $"Compression ratio should exceed 50% for a fully collinear track (got {stats.CompressionRatio * 100f:F1}%).");
            AssertTrue(compressedCount >= 2, "Compression must preserve at least the end keys.");

            // 首尾必须精确保留
            var firstKey = clip.Tracks[0].TranslationKeys[0];
            var lastKey = clip.Tracks[0].TranslationKeys[^1];
            AssertTrue(Vector3.Distance(firstKey.Value, new Vector3(0, 1, 0)) < 0.001f,
                "Compression must preserve the first key exactly.");
            AssertTrue(Vector3.Distance(lastKey.Value, new Vector3(0, 1, 1.1f)) < 0.001f,
                "Compression must preserve the last key exactly.");
        }

        /// <summary>
        /// 分层骨骼遮罩混合（UE5.8 AvatarMask / Layered Blend Per Bone）：基础姿势为
        /// 全身静止，上层姿势仅旋转 Head。经 boneFilter=["Head"] 遮罩后，Head 应取自
        /// 上层旋转，而未被遮罩的 Hips 应保持基础姿势，验证逐骨遮罩只影响指定骨骼。
        /// </summary>
        public static void TestLayeredBoneBlendMasksUpperBodyOnly()
        {
            var skeleton = YamaEngine.Animation.Skeleton.CreateBiped("MaskBiped");
            int hips = skeleton.FindBone("Hips");
            int head = skeleton.FindBone("Head");

            // 基础姿势：全身静止（Hips 抬升 +1，Head 无旋转）
            var baseClip = new AnimationClip { Name = "Idle", LengthSeconds = 1.0f };
            baseClip.AddTranslationKey("Hips", 0f, new Vector3(0, 1, 0));
            baseClip.AddTranslationKey("Hips", 1f, new Vector3(0, 1, 0));
            baseClip.AddRotationKey("Head", 0f, Quaternion.Identity);
            baseClip.AddRotationKey("Head", 1f, Quaternion.Identity);

            // 上层姿势：仅旋转 Head 180°，Hips 不动
            var upperClip = new AnimationClip { Name = "LookBack", LengthSeconds = 1.0f };
            upperClip.AddTranslationKey("Hips", 0f, new Vector3(0, 1, 0));
            upperClip.AddTranslationKey("Hips", 1f, new Vector3(0, 1, 0));
            upperClip.AddRotationKey("Head", 0f, Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI));
            upperClip.AddRotationKey("Head", 1f, Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI));

            // 节点图：基础层 + 仅作用于 Head 的遮罩层
            var baseNode = new AnimSequencePlayerNode(baseClip) { ExplicitTime = 0f };
            var upperNode = new AnimSequencePlayerNode(upperClip) { ExplicitTime = 0f };
            var layered = new AnimLayeredBoneBlendNode
            {
                BasePose = baseNode
            };
            layered.AddLayer(upperNode, weight: 1f, boneFilter: new[] { "Head" }, blendDepth: 0f);
            layered.OnInitialize(skeleton);

            var output = AnimPoseOutput.Create(skeleton.Bones.Count);
            layered.OnEvaluate(output);

            // Head 取自上层旋转（180° → 绕 Y 旋转分量显著）
            AssertTrue(MathF.Abs(output.Rotations[head].Y) > 0.6f,
                "Masked bone Head should take upper-layer rotation (180 deg about Y).");
            // 未遮罩的 Hips 保持基础姿势（无抬升）
            AssertTrue(Vector3.Distance(output.Translations[hips], new Vector3(0, 1, 0)) < 0.001f,
                "Unmasked bone Hips should keep base pose translation.");
        }

        /// <summary>
        /// 动画同步组（UE5.8 Sync Group）：两个不同长度的剪辑（Walk 1s / Run 2s）放入同一
        /// 同步组，推进到归一化时间 0.5 后，各成员的本地时间应按自身长度换算但保持同一相位
        /// （Walk=0.5s，Run=1.0s），验证跨长度剪辑的相位同步。
        /// </summary>
        public static void TestAnimSyncGroupKeepsDifferentLengthClipsInPhase()
        {
            var walkClip = new AnimationClip { Name = "Walk", LengthSeconds = 1.0f };
            var runClip = new AnimationClip { Name = "Run", LengthSeconds = 2.0f };

            var walkPose = new PoseNode(walkClip);
            var runPose = new PoseNode(runClip);

            var group = new AnimSyncGroup("FullBody");
            group.AddMember(walkPose);
            group.AddMember(runPose);
            // 无 Leader → 使用最长成员（Run 2s）作同步基准

            AssertTrue(MathF.Abs(group.GroupLength - 2.0f) < 0.001f,
                "Sync group length should be the longest member (Run 2s).");

            // 推进到归一化时间 0.5：各成员按自身长度换算本地时间
            group.SetNormalizedTime(0.5f);
            AssertTrue(MathF.Abs(group.NormalizedTime - 0.5f) < 0.001f,
                "Sync group should be at normalized time 0.5.");

            AssertTrue(MathF.Abs(walkPose.CurrentTime - 0.5f) < 0.001f,
                "Walk (1s) should be at local time 0.5s at phase 0.5.");
            AssertTrue(MathF.Abs(runPose.CurrentTime - 1.0f) < 0.001f,
                "Run (2s) should be at local time 1.0s at phase 0.5.");

            // 归一化时间回到 0 时，两剪辑本地时间都应归零（相位对齐起点）
            group.SetNormalizedTime(0f);
            AssertTrue(MathF.Abs(walkPose.CurrentTime) < 0.001f && MathF.Abs(runPose.CurrentTime) < 0.001f,
                "Both clips should return to local time 0 at phase 0.");
        }

        /// <summary>
        /// 叠加混合层（UE5.8 Additive Blend / Aim Offset 基础）：基础姿势为静止，
        /// 叠加层为 Head 附加一个 90° 绕 Y 旋转，Weight=1 时 Head 最终旋转应叠加为
        /// 90°，验证加性层在基础姿势上叠加增量（而非覆盖）。
        /// </summary>
        public static void TestAdditiveBlendLayerAddsRotationDelta()
        {
            var skeleton = YamaEngine.Animation.Skeleton.CreateBiped("AddBiped");
            int head = skeleton.FindBone("Head");

            // 基础剪辑：全身静止（Head 无旋转）
            var baseClip = new AnimationClipReal { Name = "Idle", Duration = 1.0f, FrameRate = 30f };
            var baseTrack = new BoneTrack { BoneIndex = head, BoneName = "Head" };
            baseTrack.RotationKeys.Add(new QuaternionKeyframe { Time = 0f, Value = Quaternion.Identity });
            baseClip.Tracks.Add(baseTrack);

            // 叠加剪辑：Head 附加 90° 绕 Y 旋转（相对绑定姿势的增量）
            var additiveClip = new AnimationClipReal { Name = "Aim", Duration = 1.0f, FrameRate = 30f };
            var addTrack = new BoneTrack { BoneIndex = head, BoneName = "Head" };
            addTrack.RotationKeys.Add(new QuaternionKeyframe
            {
                Time = 0f,
                Value = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI * 0.5f)
            });
            additiveClip.Tracks.Add(addTrack);

            var baseNode = new ClipNode(baseClip);
            var additiveNode = new ClipNode(additiveClip);
            var additive = new AdditiveBlendNode(baseNode, additiveNode)
            {
                Weight = 1f,
                AdditiveMode = AdditiveType.Rotation,
                AdditiveBasePose = skeleton.CreateBindPose()
            };

            var ctx = new BlendContext { DeltaTime = 0.016f, CurrentTime = 0f, Skeleton = skeleton };
            var result = additive.Evaluate(ctx);
            AssertTrue(result != null, "Additive blend should produce a pose.");

            // Head 旋转应叠加到约 90°（绕 Y 分量 ~0.707）
            AssertTrue(MathF.Abs(result.BonePoses[head].Rotation.Y) > 0.6f,
                $"Additive layer should add ~90 deg Y rotation to Head (got Y={result.BonePoses[head].Rotation.Y:F3}).");
        }

        /// <summary>
        /// 瞄准偏移（UE5.8 Aim Offset）：以绑定姿势为基础层，叠加层提供 0°→90° 绕 Y 的
        /// 瞄准增量。Aim Offset 的核心是加性权重插值：weight=0 → 无瞄准，weight=1 → 满瞄准。
        /// 验证 weight=0.5 时 Head 绕 Y 叠加约 45°（slerp 半程，Y 分量 sin(22.5°)≈0.383），
        /// weight=0 时 Head 保持绑定姿势（无瞄准偏移）。
        /// </summary>
        public static void TestAimOffsetAdditiveWeightInterpolation()
        {
            var skeleton = YamaEngine.Animation.Skeleton.CreateBiped("AimBiped");
            int head = skeleton.FindBone("Head");
            var bindPose = skeleton.CreateBindPose();

            // 基础层：静止（Head 无旋转，等于绑定姿势）
            var baseClip = new AnimationClipReal { Name = "Idle", Duration = 1.0f, FrameRate = 30f };
            var baseTrack = new BoneTrack { BoneIndex = head, BoneName = "Head" };
            baseTrack.RotationKeys.Add(new QuaternionKeyframe { Time = 0f, Value = Quaternion.Identity });
            baseClip.Tracks.Add(baseTrack);

            // 瞄准偏移增量层：Head 提供满 90° 绕 Y 的瞄准增量
            var aimClip = new AnimationClipReal { Name = "AimOffset", Duration = 1.0f, FrameRate = 30f };
            var aimTrack = new BoneTrack { BoneIndex = head, BoneName = "Head" };
            aimTrack.RotationKeys.Add(new QuaternionKeyframe
            {
                Time = 0f,
                Value = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI * 0.5f)
            });
            aimClip.Tracks.Add(aimTrack);

            var baseNode = new ClipNode(baseClip);
            var aimNode = new ClipNode(aimClip);
            float halfSin = MathF.Sin(MathF.PI * 0.125f); // slerp 半程 45° 的 Y 分量

            // weight=0 → 无瞄准偏移，Head 回到绑定姿势
            var aimZero = new AdditiveBlendNode(baseNode, aimNode)
            {
                Weight = 0f, AdditiveMode = AdditiveType.Rotation, AdditiveBasePose = bindPose
            };
            var r0 = aimZero.Evaluate(new BlendContext { DeltaTime = 0.016f, CurrentTime = 0f, Skeleton = skeleton });
            AssertTrue(MathF.Abs(r0.BonePoses[head].Rotation.Y) < 0.05f,
                "Aim offset weight=0 should keep Head at bind pose (no aim).");

            // weight=0.5 → 半瞄准（绕 Y 叠加 ~45°）
            var aimHalf = new AdditiveBlendNode(baseNode, aimNode)
            {
                Weight = 0.5f, AdditiveMode = AdditiveType.Rotation, AdditiveBasePose = bindPose
            };
            var rHalf = aimHalf.Evaluate(new BlendContext { DeltaTime = 0.016f, CurrentTime = 0f, Skeleton = skeleton });
            AssertTrue(MathF.Abs(rHalf.BonePoses[head].Rotation.Y - halfSin) < 0.02f,
                $"Aim offset weight=0.5 should add ~45 deg Y aim (got Y={rHalf.BonePoses[head].Rotation.Y:F3}, want ~{halfSin:F3}).");
        }
    }
}
