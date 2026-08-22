using System.Numerics;
using ShanyuEngine.Skinning;

namespace ShanyuEngine.Animation
{
    /// <summary>An explicit mapping between a single source bone and a single target bone.</summary>
    public sealed class BoneMapping
    {
        public string SourceBone { get; set; } = string.Empty;
        public string TargetBone { get; set; } = string.Empty;

        public BoneMapping() { }

        public BoneMapping(string sourceBone, string targetBone)
        {
            SourceBone = sourceBone;
            TargetBone = targetBone;
        }
    }

    /// <summary>
    /// Describes how bones in one skeleton correspond to bones in another. Built either
    /// automatically (by matching bone names) or from an explicit list of
    /// <see cref="BoneMapping"/> entries. Also stores per-bone length ratios used to scale
    /// translations so retargeted motion fits the target proportions.
    /// </summary>
    public sealed class SkeletonMapping
    {
        /// <summary>The skeleton the source animation was authored for.</summary>
        public Skeleton Source { get; }
        /// <summary>The skeleton the animation is being retargeted onto.</summary>
        public Skeleton Target { get; }

        /// <summary>Indexed by source bone: gives the matching target bone index, or -1.</summary>
        public int[] SourceToTarget { get; }
        /// <summary>Indexed by target bone: gives the matching source bone index, or -1.</summary>
        public int[] TargetToSource { get; }
        /// <summary>Indexed by target bone: target offset length / source offset length.</summary>
        public float[] LengthRatios { get; }

        private SkeletonMapping(Skeleton source, Skeleton target)
        {
            Source = source;
            Target = target;
            SourceToTarget = new int[source.Bones.Count];
            TargetToSource = new int[target.Bones.Count];
            LengthRatios = new float[target.Bones.Count];
            for (var i = 0; i < SourceToTarget.Length; i++) SourceToTarget[i] = -1;
            for (var i = 0; i < TargetToSource.Length; i++) TargetToSource[i] = -1;
            for (var i = 0; i < LengthRatios.Length; i++) LengthRatios[i] = 1f;
        }

        /// <summary>Build a mapping by matching bone names between the two skeletons.</summary>
        public static SkeletonMapping Build(Skeleton source, Skeleton target)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (target == null) throw new ArgumentNullException(nameof(target));

            var mapping = new SkeletonMapping(source, target);
            for (var s = 0; s < source.Bones.Count; s++)
            {
                var t = target.FindBone(source.Bones[s].Name);
                if (t >= 0)
                {
                    mapping.SourceToTarget[s] = t;
                    mapping.TargetToSource[t] = s;
                }
            }
            mapping.ComputeLengthRatios();
            return mapping;
        }

        /// <summary>Build a mapping from an explicit list, falling back to name matching for the rest.</summary>
        public static SkeletonMapping Build(Skeleton source, Skeleton target, IEnumerable<BoneMapping> explicitMappings)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (target == null) throw new ArgumentNullException(nameof(target));
            if (explicitMappings == null) throw new ArgumentNullException(nameof(explicitMappings));

            var mapping = Build(source, target);
            foreach (var m in explicitMappings)
            {
                if (m == null || string.IsNullOrEmpty(m.SourceBone) || string.IsNullOrEmpty(m.TargetBone))
                    continue;
                var s = source.FindBone(m.SourceBone);
                var t = target.FindBone(m.TargetBone);
                if (s < 0 || t < 0) continue;
                // Clear any previous auto-mapping for these indices.
                var prevT = mapping.SourceToTarget[s];
                var prevS = mapping.TargetToSource[t];
                if (prevT >= 0) mapping.TargetToSource[prevT] = -1;
                if (prevS >= 0) mapping.SourceToTarget[prevS] = -1;
                mapping.SourceToTarget[s] = t;
                mapping.TargetToSource[t] = s;
            }
            mapping.ComputeLengthRatios();
            return mapping;
        }

        public bool HasMapping(int sourceBoneIndex) =>
            sourceBoneIndex >= 0 && sourceBoneIndex < SourceToTarget.Length && SourceToTarget[sourceBoneIndex] >= 0;

        public int MapToTarget(int sourceBoneIndex) =>
            sourceBoneIndex >= 0 && sourceBoneIndex < SourceToTarget.Length ? SourceToTarget[sourceBoneIndex] : -1;

        public int MapToSource(int targetBoneIndex) =>
            targetBoneIndex >= 0 && targetBoneIndex < TargetToSource.Length ? TargetToSource[targetBoneIndex] : -1;

        /// <summary>
        /// Compute the ratio of each target bone's local offset length to the corresponding
        /// source bone's local offset length. Used to scale translation offsets during
        /// retargeting so that, for example, a tall character's walk covers more ground than
        /// a short character's.
        /// </summary>
        private void ComputeLengthRatios()
        {
            for (var t = 0; t < Target.Bones.Count; t++)
            {
                var s = TargetToSource[t];
                if (s < 0)
                {
                    LengthRatios[t] = 1f;
                    continue;
                }

                var srcLen = Target.Bones[t].LocalPosition.Length();
                var tgtLen = Source.Bones[s].LocalPosition.Length();
                LengthRatios[t] = srcLen <= 1e-6f ? 1f : tgtLen / srcLen;
            }
        }

        /// <summary>Overall height ratio (target root height / source root height) for root motion scaling.</summary>
        public float RootScaleRatio
        {
            get
            {
                if (Source.Bones.Count == 0 || Target.Bones.Count == 0) return 1f;
                var s = Source.Bones[0].LocalPosition.Length();
                var t = Target.Bones[0].LocalPosition.Length();
                if (s <= 1e-6f) return 1f;
                return t / s;
            }
        }
    }

    /// <summary>
    /// A frame-to-frame delta of the root bone's transform, used to drive character
    /// movement (root motion) independently of the retargeted pose.
    /// </summary>
    public struct RootMotionData
    {
        public Vector3 PositionDelta;
        public Quaternion RotationDelta;

        public static RootMotionData Identity => new()
        {
            PositionDelta = Vector3.Zero,
            RotationDelta = Quaternion.Identity
        };
    }

    /// <summary>
    /// Retargets animation from a source skeleton onto a target skeleton. The core idea is
    /// to treat each bone's rotation as a delta relative to its bind pose and apply that
    /// same delta to the target bone's bind pose; translations are scaled by bone-length
    /// ratios so motion fits the target's proportions.
    /// </summary>
    public sealed class Retargeter
    {
        public SkeletonMapping Mapping { get; }
        /// <summary>When true, root translation deltas are extracted and scaled for the target.</summary>
        public bool TransferRootMotion { get; set; } = true;
        /// <summary>Name of the root bone (defaults to each skeleton's first bone).</summary>
        public string? RootBoneName { get; set; }

        private int _sourceRootIndex = -1;
        private int _targetRootIndex = -1;
        private Vector3 _lastRootPosition;
        private Quaternion _lastRootRotation = Quaternion.Identity;
        private bool _hasLastRoot;

        public Retargeter(SkeletonMapping mapping)
        {
            Mapping = mapping ?? throw new ArgumentNullException(nameof(mapping));
            ResolveRootIndices();
        }

        private void ResolveRootIndices()
        {
            var src = Mapping.Source;
            var tgt = Mapping.Target;
            _sourceRootIndex = string.IsNullOrEmpty(RootBoneName)
                ? (src.Bones.Count > 0 ? 0 : -1)
                : src.FindBone(RootBoneName);
            _targetRootIndex = string.IsNullOrEmpty(RootBoneName)
                ? (tgt.Bones.Count > 0 ? 0 : -1)
                : tgt.FindBone(RootBoneName);
        }

        /// <summary>
        /// Retarget a source pose onto the target skeleton, returning a new target pose.
        /// Bones without a mapping keep the target's bind pose.
        /// </summary>
        public SkeletonPose RetargetPose(SkeletonPose sourcePose)
        {
            if (sourcePose == null) throw new ArgumentNullException(nameof(sourcePose));
            var target = Mapping.Target;
            var pose = target.CreateBindPose();

            for (var s = 0; s < Mapping.Source.Bones.Count; s++)
            {
                var t = Mapping.SourceToTarget[s];
                if (t < 0) continue;

                var sourceBindRot = Mapping.Source.Bones[s].LocalRotation;
                var sourceLocalRot = sourcePose.GetRotation(s);

                // Rotation delta relative to the source bind pose.
                var delta = Quaternion.Normalize(Quaternion.Inverse(sourceBindRot) * sourceLocalRot);

                // Apply the delta to the target bind rotation.
                var targetBindRot = target.Bones[t].LocalRotation;
                var targetLocalRot = Quaternion.Normalize(targetBindRot * delta);
                pose.SetRotation(t, targetLocalRot);

                // Scale translation offsets by the bone-length ratio.
                var offset = sourcePose.GetTranslationOffset(s);
                if (offset != Vector3.Zero)
                    pose.SetTranslationOffset(t, offset * Mapping.LengthRatios[t]);

                // Copy scale offsets directly (uniform scaling is rarely pose-dependent).
                var scale = sourcePose.GetScaleOffset(s);
                if (scale != Vector3.One)
                    pose.SetScaleOffset(t, scale);
            }

            return pose;
        }

        /// <summary>
        /// Extract the root motion delta between the previous frame and the current source
        /// pose. The translation delta is scaled by <see cref="SkeletonMapping.RootScaleRatio"/>
        /// so it is appropriate for the target character. Call this once per frame with the
        /// freshly-sampled source pose.
        /// </summary>
        public RootMotionData ExtractRootMotion(SkeletonPose sourcePose)
        {
            if (!TransferRootMotion || _sourceRootIndex < 0)
                return RootMotionData.Identity;
            if (sourcePose == null)
                return RootMotionData.Identity;

            var rootPos = sourcePose.GetTranslationOffset(_sourceRootIndex);
            var rootRot = sourcePose.GetRotation(_sourceRootIndex);

            if (!_hasLastRoot)
            {
                _lastRootPosition = rootPos;
                _lastRootRotation = rootRot;
                _hasLastRoot = true;
                return RootMotionData.Identity;
            }

            var posDelta = (rootPos - _lastRootPosition) * Mapping.RootScaleRatio;

            // Shortest-arc rotation delta.
            var rotDelta = Quaternion.Normalize(Quaternion.Inverse(_lastRootRotation) * rootRot);
            if (Quaternion.Dot(rotDelta, Quaternion.Identity) < 0f)
                rotDelta = -rotDelta;

            _lastRootPosition = rootPos;
            _lastRootRotation = rootRot;

            return new RootMotionData { PositionDelta = posDelta, RotationDelta = rotDelta };
        }

        /// <summary>Reset the root motion tracker (e.g. when switching animations).</summary>
        public void ResetRootMotion()
        {
            _hasLastRoot = false;
            _lastRootPosition = Vector3.Zero;
            _lastRootRotation = Quaternion.Identity;
        }

        /// <summary>
        /// Produce a normalised reference pose (T-pose / bind pose) for the target skeleton.
        /// This is the pose retargeted deltas are applied on top of, and is useful for
        /// authoring or verifying mappings.
        /// </summary>
        public SkeletonPose NormalizeToTPose()
        {
            return Mapping.Target.CreateBindPose();
        }

        /// <summary>
        /// Retarget a single <see cref="AnimationClip"/> from the source skeleton to the
        /// target skeleton by sampling it at many resolutions and rebuilding per-bone
        /// curves scaled to the target. The returned clip preserves the source timing and
        /// events but uses target bone names and scaled translations.
        /// </summary>
        public AnimationClip RetargetClip(AnimationClip sourceClip, int samplesPerSecond = 30)
        {
            if (sourceClip == null) throw new ArgumentNullException(nameof(sourceClip));
            var source = Mapping.Source;
            var target = Mapping.Target;

            var result = new AnimationClip(sourceClip.Name + "_retargeted", sourceClip.LengthSeconds, sourceClip.Looping)
            {
                WrapMode = sourceClip.WrapMode,
                Speed = sourceClip.Speed
            };

            // Copy events verbatim (they are time-based, not skeleton-based).
            foreach (var e in sourceClip.Events)
                result.Events.Add(e.Clone());

            var step = 1f / Math.Max(1, samplesPerSecond);
            var duration = sourceClip.LengthSeconds;

            // Determine, for each mapped target bone, which channels exist on the source.
            foreach (var sourceTrack in sourceClip.Tracks)
            {
                var s = source.FindBone(sourceTrack.BoneName);
                if (s < 0) continue;
                var t = Mapping.SourceToTarget[s];
                if (t < 0) continue;
                var targetBoneName = target.Bones[t].Name;
                var ratio = Mapping.LengthRatios[t];

                var hasTrans = sourceTrack.TranslationKeys.Count > 0;
                var hasRot = sourceTrack.RotationKeys.Count > 0;
                var hasScale = sourceTrack.ScaleKeys.Count > 0;
                if (!hasTrans && !hasRot && !hasScale) continue;

                for (float time = 0f; time <= duration + 1e-5f; time += step)
                {
                    var sampled = sourceClip.Sample(source, Math.Min(time, duration));

                    if (hasTrans)
                        result.AddTranslationKey(targetBoneName, Math.Min(time, duration),
                            sampled.GetTranslationOffset(s) * ratio);
                    if (hasRot)
                        result.AddRotationKey(targetBoneName, Math.Min(time, duration),
                            sampled.GetRotation(s));
                    if (hasScale)
                        result.AddScaleKey(targetBoneName, Math.Min(time, duration),
                            sampled.GetScaleOffset(s));
                }
            }

            result.RecalculateLength();
            return result;
        }
    }
}
