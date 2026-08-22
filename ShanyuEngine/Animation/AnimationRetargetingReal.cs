// AnimationRetargetingReal.cs - Real animation retargeting for the ShanyuEngine.
//
// Implements skeleton-to-skeleton animation retargeting:
//   - AnimationRetargeter: retargets clips and poses between skeletons
//   - RetargetClip: retargets all bone tracks from source to target skeleton
//   - RetargetPose: retargets a single pose
//   - Root motion scaling: scales root motion by the overall height ratio
//   - IK target scaling: scales IK target positions by the target skeleton ratio
//   - Foot contact preservation: clamps foot Y to prevent foot sliding
//
// Retargeting algorithm:
//   1. Build a bone name mapping between source and target skeletons
//   2. For each bone, compute the bone-length ratio (target / source)
//   3. Local rotations are preserved (skeleton-independent)
//   4. Local translations are scaled by the parent bone's length ratio
//   5. Root motion is scaled by the overall skeleton scale ratio
//   6. Foot bones are adjusted to maintain ground contact (Y = 0)
//
// All algorithms use System.Numerics with mathematically correct implementations.

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace ShanyuEngine.Animation
{
    // ========================================================================
    // BoneMapping - maps source skeleton bones to target skeleton bones
    // ========================================================================

    /// <summary>
    /// Bone mapping between a source and target skeleton. Maps by bone name,
    /// storing the source bone index, target bone index, and the bone-length
    /// ratio (target length / source length).
    /// </summary>
    public class BoneMappingReal
    {
        /// <summary>Source bone index in the source skeleton.</summary>
        public int SourceBoneIndex { get; set; }

        /// <summary>Target bone index in the target skeleton.</summary>
        public int TargetBoneIndex { get; set; }

        /// <summary>Bone name (same in both skeletons).</summary>
        public string BoneName { get; set; }

        /// <summary>
        /// Length ratio: target bone length / source bone length.
        /// Used to scale translations.
        /// </summary>
        public float LengthRatio { get; set; } = 1f;

        /// <summary>
        /// The bone length in the source skeleton (distance from this bone to
        /// its parent in world rest space, or the local translation length).
        /// </summary>
        public float SourceLength { get; set; }

        /// <summary>The bone length in the target skeleton.</summary>
        public float TargetLength { get; set; }
    }

    // ========================================================================
    // RetargetingSettings - configuration for retargeting
    // ========================================================================

    /// <summary>
    /// Settings that control the retargeting behavior.
    /// </summary>
    public class RetargetingSettings
    {
        /// <summary>If true, scale root motion by the overall skeleton height ratio.</summary>
        public bool ScaleRootMotion { get; set; } = true;

        /// <summary>If true, preserve foot contact by clamping foot Y to 0.</summary>
        public bool PreserveFootContact { get; set; } = true;

        /// <summary>If true, scale IK target positions by the target skeleton ratio.</summary>
        public bool ScaleIKTargets { get; set; } = true;

        /// <summary>If true, preserve local rotations (recommended).</summary>
        public bool PreserveRotations { get; set; } = true;

        /// <summary>If true, scale local translations by bone-length ratios.</summary>
        public bool ScaleTranslations { get; set; } = true;

        /// <summary>If true, preserve scale values.</summary>
        public bool PreserveScale { get; set; } = true;

        /// <summary>Names of foot bones (for ground contact preservation).</summary>
        public List<string> FootBoneNames { get; set; } = new List<string>
        {
            "LeftFoot", "RightFoot", "LeftToe", "RightToe"
        };

        /// <summary>Names of IK target bones (for IK target scaling).</summary>
        public List<string> IKTargetBoneNames { get; set; } = new List<string>();

        /// <summary>Ground plane height (Y coordinate for foot contact).</summary>
        public float GroundHeight { get; set; } = 0f;
    }

    // ========================================================================
    // AnimationRetargeter - the main retargeting class
    // ========================================================================

    /// <summary>
    /// Animation retargeter. Transfers animation data from a source skeleton
    /// to a target skeleton by matching bone names and adjusting translations
    /// based on bone-length ratios.
    ///
    /// Key principles:
    ///   - Local rotations are preserved (they are skeleton-independent)
    ///   - Local translations are scaled by the parent bone's length ratio
    ///   - Root motion is scaled by the overall height ratio
    ///   - Foot bones are adjusted to maintain ground contact
    /// </summary>
    public class AnimationRetargeter
    {
        /// <summary>Source skeleton.</summary>
        public Skeleton SourceSkeleton { get; set; }

        /// <summary>Target skeleton.</summary>
        public Skeleton TargetSkeleton { get; set; }

        /// <summary>Retargeting settings.</summary>
        public RetargetingSettings Settings { get; set; } = new RetargetingSettings();

        /// <summary>Bone mappings (source index -> mapping).</summary>
        public Dictionary<int, BoneMappingReal> BoneMappings { get; } = new Dictionary<int, BoneMappingReal>();

        /// <summary>Overall height ratio (target / source).</summary>
        public float HeightRatio { get; private set; } = 1f;

        /// <summary>Source skeleton height (root to feet).</summary>
        public float SourceHeight { get; private set; } = 1f;

        /// <summary>Target skeleton height (root to feet).</summary>
        public float TargetHeight { get; private set; } = 1f;

        private const float Eps = 1e-6f;

        /// <summary>
        /// Initialize the retargeter by building the bone mapping between
        /// the source and target skeletons.
        /// </summary>
        public void Initialize()
        {
            BoneMappings.Clear();

            if (SourceSkeleton == null || TargetSkeleton == null) return;

            // Compute world rest transforms for both skeletons
            Matrix4x4[] sourceWorldRest = ComputeWorldRestMatrices(SourceSkeleton);
            Matrix4x4[] targetWorldRest = ComputeWorldRestMatrices(TargetSkeleton);

            // Build bone mappings by name
            foreach (var sourceBone in SourceSkeleton.Bones)
            {
                int targetIdx = TargetSkeleton.FindBone(sourceBone.Name);
                if (targetIdx < 0) continue;

                var targetBone = TargetSkeleton.Bones[targetIdx];

                // Compute bone lengths: the length of the local translation
                // (which represents the bone's offset from its parent)
                float sourceLen = sourceBone.RestTransform.Translation.Length();
                float targetLen = targetBone.RestTransform.Translation.Length();

                float ratio = sourceLen > Eps ? targetLen / sourceLen : 1f;

                BoneMappings[sourceBone.Index] = new BoneMappingReal
                {
                    SourceBoneIndex = sourceBone.Index,
                    TargetBoneIndex = targetIdx,
                    BoneName = sourceBone.Name,
                    LengthRatio = ratio,
                    SourceLength = sourceLen,
                    TargetLength = targetLen
                };
            }

            // Compute overall height ratio
            ComputeHeightRatio();
        }

        /// <summary>
        /// Compute the world-space rest matrices for a skeleton.
        /// worldRest[i] = localRest[i] * parentWorldRest[parentIndex]
        /// </summary>
        private static Matrix4x4[] ComputeWorldRestMatrices(Skeleton skeleton)
        {
            var result = new Matrix4x4[skeleton.BoneCount];
            for (int i = 0; i < skeleton.BoneCount; i++)
            {
                Matrix4x4 local = skeleton.Bones[i].RestTransform.ToMatrix();
                int parent = skeleton.Bones[i].ParentIndex;
                result[i] = parent >= 0 ? local * result[parent] : local;
            }
            return result;
        }

        /// <summary>
        /// Compute the overall height ratio by comparing the vertical distance
        /// from the root bone to the foot bones in both skeletons.
        /// </summary>
        private void ComputeHeightRatio()
        {
            SourceHeight = ComputeSkeletonHeight(SourceSkeleton);
            TargetHeight = ComputeSkeletonHeight(TargetSkeleton);
            HeightRatio = SourceHeight > Eps ? TargetHeight / SourceHeight : 1f;
        }

        /// <summary>
        /// Compute the height of a skeleton: the vertical distance from the
        /// root bone to the lowest foot bone in world rest space.
        /// </summary>
        private float ComputeSkeletonHeight(Skeleton skeleton)
        {
            if (skeleton == null || skeleton.BoneCount == 0) return 1f;

            Matrix4x4[] worldRest = ComputeWorldRestMatrices(skeleton);

            float rootY = worldRest[0].Translation.Y;
            float minFootY = rootY;

            // Find the lowest foot bone
            foreach (var bone in skeleton.Bones)
            {
                if (Settings.FootBoneNames.Contains(bone.Name))
                {
                    float y = worldRest[bone.Index].Translation.Y;
                    minFootY = Math.Min(minFootY, y);
                }
            }

            // If no foot bones found, use the lowest bone
            if (minFootY == rootY)
            {
                foreach (var mat in worldRest)
                    minFootY = Math.Min(minFootY, mat.Translation.Y);
            }

            return MathF.Abs(rootY - minFootY);
        }

        // ------------------------------------------------------------------
        // RetargetClip - retarget an entire animation clip
        // ------------------------------------------------------------------

        /// <summary>
        /// Retarget an animation clip from the source skeleton to the target
        /// skeleton. Returns a new clip with adjusted translations.
        ///
        /// For each bone track:
        ///   - Rotations are preserved (local rotations are skeleton-independent)
        ///   - Translations are scaled by the bone's length ratio
        ///   - Scale values are preserved
        ///   - Root motion translations are scaled by the overall height ratio
        /// </summary>
        public AnimationClipReal RetargetClip(
            AnimationClipReal sourceClip,
            Skeleton sourceSkeleton,
            Skeleton targetSkeleton)
        {
            SourceSkeleton = sourceSkeleton;
            TargetSkeleton = targetSkeleton;
            Initialize();

            if (sourceClip == null) return null;

            var retargetedClip = new AnimationClipReal
            {
                Name = sourceClip.Name + "_retargeted",
                Duration = sourceClip.Duration,
                FrameRate = sourceClip.FrameRate,
                Loop = sourceClip.Loop
            };

            // Retarget each bone track
            foreach (var sourceTrack in sourceClip.Tracks)
            {
                if (!BoneMappings.TryGetValue(sourceTrack.BoneIndex, out var mapping))
                {
                    // Bone not found in target skeleton; skip this track
                    continue;
                }

                // Create a new track for the target skeleton
                var retargetedTrack = new BoneTrack
                {
                    BoneIndex = mapping.TargetBoneIndex,
                    BoneName = mapping.BoneName,
                    PositionInterpolation = sourceTrack.PositionInterpolation,
                    RotationInterpolation = sourceTrack.RotationInterpolation,
                    ScaleInterpolation = sourceTrack.ScaleInterpolation
                };

                bool isRootBone = mapping.SourceBoneIndex == SourceSkeleton.RootBoneIndex;
                bool isFootBone = Settings.FootBoneNames.Contains(mapping.BoneName);
                bool isIKTarget = Settings.IKTargetBoneNames.Contains(mapping.BoneName);

                // Determine the translation scale factor
                float transScale;
                if (isRootBone && Settings.ScaleRootMotion)
                {
                    // Root motion: scale by overall height ratio
                    transScale = HeightRatio;
                }
                else if (isIKTarget && Settings.ScaleIKTargets)
                {
                    // IK targets: scale by height ratio
                    transScale = HeightRatio;
                }
                else if (Settings.ScaleTranslations)
                {
                    // Regular bones: scale by bone-length ratio
                    transScale = mapping.LengthRatio;
                }
                else
                {
                    transScale = 1f;
                }

                // Retarget position keyframes
                foreach (var posKey in sourceTrack.PositionKeys)
                {
                    Vector3 newPos = posKey.Value * transScale;

                    // Foot contact preservation: clamp foot Y to ground
                    if (isFootBone && Settings.PreserveFootContact)
                    {
                        newPos.Y = Settings.GroundHeight;
                    }

                    retargetedTrack.PositionKeys.Add(new Vector3Keyframe
                    {
                        Time = posKey.Time,
                        Value = newPos,
                        TangentIn = posKey.TangentIn * transScale,
                        TangentOut = posKey.TangentOut * transScale
                    });
                }

                // Retarget rotation keyframes (preserve as-is)
                if (Settings.PreserveRotations)
                {
                    foreach (var rotKey in sourceTrack.RotationKeys)
                    {
                        retargetedTrack.RotationKeys.Add(new QuaternionKeyframe
                        {
                            Time = rotKey.Time,
                            Value = rotKey.Value
                        });
                    }
                }
                else
                {
                    // If not preserving rotations, adjust by the rotation difference
                    // between source and target rest poses
                    Quaternion sourceRestRot = sourceSkeleton.Bones[mapping.SourceBoneIndex].RestTransform.Rotation;
                    Quaternion targetRestRot = targetSkeleton.Bones[mapping.TargetBoneIndex].RestTransform.Rotation;
                    Quaternion restDelta = targetRestRot * Quaternion.Inverse(sourceRestRot);

                    foreach (var rotKey in sourceTrack.RotationKeys)
                    {
                        // Apply the rest-pose rotation difference
                        Quaternion newRot = restDelta * rotKey.Value;
                        retargetedTrack.RotationKeys.Add(new QuaternionKeyframe
                        {
                            Time = rotKey.Time,
                            Value = newRot
                        });
                    }
                }

                // Retarget scale keyframes (preserve as-is)
                if (Settings.PreserveScale)
                {
                    foreach (var scaleKey in sourceTrack.ScaleKeys)
                    {
                        retargetedTrack.ScaleKeys.Add(new Vector3Keyframe
                        {
                            Time = scaleKey.Time,
                            Value = scaleKey.Value,
                            TangentIn = scaleKey.TangentIn,
                            TangentOut = scaleKey.TangentOut
                        });
                    }
                }
                else
                {
                    // Scale by the inverse of the length ratio to compensate
                    foreach (var scaleKey in sourceTrack.ScaleKeys)
                    {
                        Vector3 newScale = scaleKey.Value * mapping.LengthRatio;
                        retargetedTrack.ScaleKeys.Add(new Vector3Keyframe
                        {
                            Time = scaleKey.Time,
                            Value = newScale,
                            TangentIn = scaleKey.TangentIn * mapping.LengthRatio,
                            TangentOut = scaleKey.TangentOut * mapping.LengthRatio
                        });
                    }
                }

                retargetedClip.Tracks.Add(retargetedTrack);
            }

            // Retarget root motion track if present
            if (sourceClip.RootMotionTrack != null)
            {
                var sourceRootTrack = sourceClip.RootMotionTrack;
                if (BoneMappings.TryGetValue(sourceRootTrack.BoneIndex, out var rootMapping))
                {
                    var retargetedRootTrack = new BoneTrack
                    {
                        BoneIndex = rootMapping.TargetBoneIndex,
                        BoneName = rootMapping.BoneName,
                        PositionInterpolation = sourceRootTrack.PositionInterpolation,
                        RotationInterpolation = sourceRootTrack.RotationInterpolation,
                        ScaleInterpolation = sourceRootTrack.ScaleInterpolation
                    };

                    float rootScale = Settings.ScaleRootMotion ? HeightRatio : 1f;

                    foreach (var posKey in sourceRootTrack.PositionKeys)
                    {
                        retargetedRootTrack.PositionKeys.Add(new Vector3Keyframe
                        {
                            Time = posKey.Time,
                            Value = posKey.Value * rootScale,
                            TangentIn = posKey.TangentIn * rootScale,
                            TangentOut = posKey.TangentOut * rootScale
                        });
                    }

                    foreach (var rotKey in sourceRootTrack.RotationKeys)
                    {
                        retargetedRootTrack.RotationKeys.Add(new QuaternionKeyframe
                        {
                            Time = rotKey.Time,
                            Value = rotKey.Value
                        });
                    }

                    foreach (var scaleKey in sourceRootTrack.ScaleKeys)
                    {
                        retargetedRootTrack.ScaleKeys.Add(scaleKey);
                    }

                    retargetedClip.RootMotionTrack = retargetedRootTrack;
                }
            }

            return retargetedClip;
        }

        // ------------------------------------------------------------------
        // RetargetPose - retarget a single pose
        // ------------------------------------------------------------------

        /// <summary>
        /// Retarget a single SkeletonPose from the source skeleton to the
        /// target skeleton. Returns a new pose compatible with the target
        /// skeleton.
        ///
        /// For each bone:
        ///   - Rotation is preserved (local rotations are skeleton-independent)
        ///   - Translation is scaled by the bone's length ratio
        ///   - Scale is preserved
        ///   - Root bone translation is scaled by the overall height ratio
        ///   - Foot bones are clamped to ground height
        /// </summary>
        public SkeletonPose RetargetPose(
            SkeletonPose sourcePose,
            Skeleton sourceSkeleton,
            Skeleton targetSkeleton)
        {
            SourceSkeleton = sourceSkeleton;
            TargetSkeleton = targetSkeleton;
            Initialize();

            if (sourcePose == null || targetSkeleton == null)
                return targetSkeleton?.CreateBindPose();

            var result = new SkeletonPose(targetSkeleton);

            foreach (var kv in BoneMappings)
            {
                var mapping = kv.Value;
                if (mapping.SourceBoneIndex >= sourcePose.BoneCount) continue;
                if (mapping.TargetBoneIndex >= result.BoneCount) continue;

                BonePose sourceBP = sourcePose.BonePoses[mapping.SourceBoneIndex];

                bool isRootBone = mapping.SourceBoneIndex == SourceSkeleton.RootBoneIndex;
                bool isFootBone = Settings.FootBoneNames.Contains(mapping.BoneName);
                bool isIKTarget = Settings.IKTargetBoneNames.Contains(mapping.BoneName);

                // Determine translation scale
                float transScale;
                if (isRootBone && Settings.ScaleRootMotion)
                    transScale = HeightRatio;
                else if (isIKTarget && Settings.ScaleIKTargets)
                    transScale = HeightRatio;
                else if (Settings.ScaleTranslations)
                    transScale = mapping.LengthRatio;
                else
                    transScale = 1f;

                // Scale translation
                Vector3 newPos = sourceBP.Position * transScale;

                // Foot contact preservation
                if (isFootBone && Settings.PreserveFootContact)
                {
                    newPos.Y = Settings.GroundHeight;
                }

                // Rotation: preserve or adjust
                Quaternion newRot;
                if (Settings.PreserveRotations)
                {
                    newRot = sourceBP.Rotation;
                }
                else
                {
                    Quaternion sourceRestRot = sourceSkeleton.Bones[mapping.SourceBoneIndex].RestTransform.Rotation;
                    Quaternion targetRestRot = targetSkeleton.Bones[mapping.TargetBoneIndex].RestTransform.Rotation;
                    Quaternion restDelta = targetRestRot * Quaternion.Inverse(sourceRestRot);
                    newRot = restDelta * sourceBP.Rotation;
                }

                // Scale: preserve or adjust
                Vector3 newScale;
                if (Settings.PreserveScale)
                {
                    newScale = sourceBP.Scale;
                }
                else
                {
                    newScale = sourceBP.Scale * mapping.LengthRatio;
                }

                result.BonePoses[mapping.TargetBoneIndex] = new BonePose(newPos, newRot, newScale);
            }

            // Fill in unmapped bones with target skeleton's bind pose
            for (int i = 0; i < result.BoneCount; i++)
            {
                if (result.BonePoses[i].Equals(BonePose.Identity))
                {
                    result.BonePoses[i] = BonePose.FromTransform(
                        targetSkeleton.Bones[i].RestTransform);
                }
            }

            return result;
        }

        // ------------------------------------------------------------------
        // RetargetRootMotion - scale root motion delta
        // ------------------------------------------------------------------

        /// <summary>
        /// Retarget a root motion delta Transform from the source skeleton to
        /// the target skeleton. Scales the delta position by the height ratio
        /// and preserves the delta rotation.
        /// </summary>
        public Transform RetargetRootMotion(Transform sourceDelta)
        {
            float scale = Settings.ScaleRootMotion ? HeightRatio : 1f;
            return new Transform(
                sourceDelta.Translation * scale,
                sourceDelta.Rotation,
                sourceDelta.Scale);
        }

        // ------------------------------------------------------------------
        // RetargetIKTarget - scale an IK target position
        // ------------------------------------------------------------------

        /// <summary>
        /// Retarget an IK target position from source to target skeleton space.
        /// Scales the position by the height ratio.
        /// </summary>
        public Vector3 RetargetIKTarget(Vector3 sourceIKTarget)
        {
            float scale = Settings.ScaleIKTargets ? HeightRatio : 1f;
            return sourceIKTarget * scale;
        }

        // ------------------------------------------------------------------
        // RetargetPosition - scale a world-space position
        // ------------------------------------------------------------------

        /// <summary>
        /// Retarget a world-space position from source to target skeleton.
        /// Scales by the height ratio relative to the root position.
        /// </summary>
        public Vector3 RetargetPosition(Vector3 sourcePosition, Vector3 rootPosition)
        {
            Vector3 relative = sourcePosition - rootPosition;
            return rootPosition + relative * HeightRatio;
        }

        // ------------------------------------------------------------------
        // GetBoneMapping - look up a bone mapping
        // ------------------------------------------------------------------

        /// <summary>
        /// Get the bone mapping for a source bone index.
        /// Returns null if no mapping exists.
        /// </summary>
        public BoneMappingReal GetBoneMapping(int sourceBoneIndex)
        {
            return BoneMappings.TryGetValue(sourceBoneIndex, out var mapping) ? mapping : null;
        }

        /// <summary>
        /// Get the bone mapping for a bone name.
        /// Returns null if no mapping exists.
        /// </summary>
        public BoneMappingReal GetBoneMapping(string boneName)
        {
            foreach (var kv in BoneMappings)
            {
                if (kv.Value.BoneName == boneName)
                    return kv.Value;
            }
            return null;
        }

        // ------------------------------------------------------------------
        // GetUnmappedBones - find bones without mappings
        // ------------------------------------------------------------------

        /// <summary>
        /// Get a list of source bone names that have no mapping in the target
        /// skeleton. Useful for debugging retargeting issues.
        /// </summary>
        public List<string> GetUnmappedSourceBones()
        {
            var result = new List<string>();
            if (SourceSkeleton == null) return result;

            foreach (var bone in SourceSkeleton.Bones)
            {
                if (!BoneMappings.ContainsKey(bone.Index))
                    result.Add(bone.Name);
            }
            return result;
        }

        /// <summary>
        /// Get a list of target bone names that have no source mapping.
        /// </summary>
        public List<string> GetUnmappedTargetBones()
        {
            var result = new List<string>();
            if (TargetSkeleton == null) return result;

            var mappedTargetIndices = new HashSet<int>();
            foreach (var kv in BoneMappings)
                mappedTargetIndices.Add(kv.Value.TargetBoneIndex);

            foreach (var bone in TargetSkeleton.Bones)
            {
                if (!mappedTargetIndices.Contains(bone.Index))
                    result.Add(bone.Name);
            }
            return result;
        }

        // ------------------------------------------------------------------
        // Static convenience methods
        // ------------------------------------------------------------------

        /// <summary>
        /// Quick retarget: retarget a clip from source to target skeleton
        /// in a single call. Creates a retargeter, initializes it, and
        /// returns the retargeted clip.
        /// </summary>
        public static AnimationClipReal Retarget(
            AnimationClipReal clip,
            Skeleton sourceSkeleton,
            Skeleton targetSkeleton,
            RetargetingSettings settings = null)
        {
            var retargeter = new AnimationRetargeter
            {
                Settings = settings ?? new RetargetingSettings()
            };
            return retargeter.RetargetClip(clip, sourceSkeleton, targetSkeleton);
        }

        /// <summary>
        /// Quick retarget: retarget a single pose from source to target skeleton.
        /// </summary>
        public static SkeletonPose Retarget(
            SkeletonPose pose,
            Skeleton sourceSkeleton,
            Skeleton targetSkeleton,
            RetargetingSettings settings = null)
        {
            var retargeter = new AnimationRetargeter
            {
                Settings = settings ?? new RetargetingSettings()
            };
            return retargeter.RetargetPose(pose, sourceSkeleton, targetSkeleton);
        }

        /// <summary>
        /// Compute the bone-length ratio between two skeletons for a given
        /// bone name. Returns 1.0 if either bone is not found.
        /// </summary>
        public static float ComputeBoneLengthRatio(
            Skeleton sourceSkeleton, Skeleton targetSkeleton, string boneName)
        {
            if (sourceSkeleton == null || targetSkeleton == null) return 1f;

            int sourceIdx = sourceSkeleton.FindBone(boneName);
            int targetIdx = targetSkeleton.FindBone(boneName);
            if (sourceIdx < 0 || targetIdx < 0) return 1f;

            float sourceLen = sourceSkeleton.Bones[sourceIdx].RestTransform.Translation.Length();
            float targetLen = targetSkeleton.Bones[targetIdx].RestTransform.Translation.Length();

            if (sourceLen < Eps) return 1f;
            return targetLen / sourceLen;
        }

        /// <summary>
        /// Build a bone mapping table between two skeletons by matching bone names.
        /// Returns a dictionary: source bone index -> target bone index.
        /// </summary>
        public static Dictionary<int, int> BuildBoneMapping(
            Skeleton sourceSkeleton, Skeleton targetSkeleton)
        {
            var result = new Dictionary<int, int>();
            if (sourceSkeleton == null || targetSkeleton == null) return result;

            foreach (var sourceBone in sourceSkeleton.Bones)
            {
                int targetIdx = targetSkeleton.FindBone(sourceBone.Name);
                if (targetIdx >= 0)
                    result[sourceBone.Index] = targetIdx;
            }

            return result;
        }
    }
}
