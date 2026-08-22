// PoseEvaluationReal.cs - Real pose evaluation, keyframe interpolation, and clip sampling.
// Part of the ShanyuEngine animation system. Replaces stub implementations with
// mathematically correct algorithms using System.Numerics.

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace ShanyuEngine.Animation
{
    // ========================================================================
    // Enums
    // ========================================================================

    /// <summary>Interpolation method used between adjacent keyframes.</summary>
    public enum InterpolationMode
    {
        Linear,
        Cubic,
        Step,
        Slerp,
        Nlerp,
        Constant
    }

    /// <summary>How curve time is handled outside the keyframe range.</summary>
    public enum ExtrapolationMode
    {
        Clamp,
        Loop,
        PingPong
    }

    /// <summary>Mirror axis for pose mirroring.</summary>
    public enum Axis
    {
        None,
        X,
        Y,
        Z
    }

    // ========================================================================
    // Transform - decomposed local-space transform
    // ========================================================================

    /// <summary>
    /// Decomposed transform: Translation + Rotation (Quaternion) + Scale.
    /// Used for bone rest transforms and local-space bone transforms.
    /// </summary>
    public struct Transform
    {
        public Vector3 Translation;
        public Quaternion Rotation;
        public Vector3 Scale;

        public static readonly Transform Identity = new Transform(
            Vector3.Zero, Quaternion.Identity, Vector3.One);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Transform(Vector3 translation, Quaternion rotation, Vector3 scale)
        {
            Translation = translation;
            Rotation = rotation;
            Scale = scale;
        }

        /// <summary>Compose into a row-vector SRT matrix (Scale * Rotation * Translation).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Matrix4x4 ToMatrix()
        {
            return Matrix4x4.CreateScale(Scale) *
                   Matrix4x4.CreateFromQuaternion(Rotation) *
                   Matrix4x4.CreateTranslation(Translation);
        }

        /// <summary>Decompose a matrix into Translation, Rotation, Scale.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Transform FromMatrix(Matrix4x4 m)
        {
            Transform t;
            t.Translation = m.Translation;

            // Extract scale from column lengths
            t.Scale = new Vector3(
                MathF.Sqrt(m.M11 * m.M11 + m.M12 * m.M12 + m.M13 * m.M13),
                MathF.Sqrt(m.M21 * m.M21 + m.M22 * m.M22 + m.M23 * m.M23),
                MathF.Sqrt(m.M31 * m.M31 + m.M32 * m.M32 + m.M33 * m.M33));

            // Build rotation matrix by dividing out scale
            Matrix4x4 r = m;
            float sx = t.Scale.X > 1e-6f ? 1.0f / t.Scale.X : 0f;
            float sy = t.Scale.Y > 1e-6f ? 1.0f / t.Scale.Y : 0f;
            float sz = t.Scale.Z > 1e-6f ? 1.0f / t.Scale.Z : 0f;
            r.M11 *= sx; r.M12 *= sx; r.M13 *= sx;
            r.M21 *= sy; r.M22 *= sy; r.M23 *= sy;
            r.M31 *= sz; r.M32 *= sz; r.M33 *= sz;
            t.Rotation = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(r));
            return t;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Transform Clone() => new Transform(Translation, Rotation, Scale);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Transform Lerp(Transform a, Transform b, float t)
        {
            return new Transform(
                Vector3.Lerp(a.Translation, b.Translation, t),
                Quaternion.Slerp(a.Rotation, b.Rotation, t),
                Vector3.Lerp(a.Scale, b.Scale, t));
        }
    }

    // ========================================================================
    // BonePose - per-bone pose (position / rotation / scale)
    // ========================================================================

    /// <summary>
    /// Local-space pose of a single bone: Position, Rotation, Scale.
    /// Provides matrix composition/decomposition, accumulation, and interpolation.
    /// </summary>
    public struct BonePose
    {
        public Vector3 Position;
        public Quaternion Rotation;
        public Vector3 Scale;

        public static readonly BonePose Identity = new BonePose(
            Vector3.Zero, Quaternion.Identity, Vector3.One);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public BonePose(Vector3 position, Quaternion rotation, Vector3 scale)
        {
            Position = position;
            Rotation = rotation;
            Scale = scale;
        }

        /// <summary>Build a BonePose from a Transform.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static BonePose FromTransform(Transform t)
            => new BonePose(t.Translation, t.Rotation, t.Scale);

        /// <summary>Compose into SRT matrix.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Matrix4x4 ToMatrix()
        {
            return Matrix4x4.CreateScale(Scale) *
                   Matrix4x4.CreateFromQuaternion(Rotation) *
                   Matrix4x4.CreateTranslation(Position);
        }

        /// <summary>Decompose a matrix into a BonePose.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static BonePose FromMatrix(Matrix4x4 m)
        {
            Transform t = Transform.FromMatrix(m);
            return new BonePose(t.Translation, t.Rotation, t.Scale);
        }

        /// <summary>
        /// Accumulate (add) another pose onto this one.
        /// Position adds, Rotation composes, Scale multiplies.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public BonePose Accumulate(BonePose other)
        {
            return new BonePose(
                Position + other.Position,
                other.Rotation * Rotation,
                Scale * other.Scale);
        }

        /// <summary>
        /// Interpolate between two bone poses.
        /// Position and Scale use Vector3 Lerp; Rotation uses SLerp.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static BonePose Interpolate(BonePose a, BonePose b, float t)
        {
            return new BonePose(
                Vector3.Lerp(a.Position, b.Position, t),
                KeyframeInterpolation.InterpolateQuaternionSlerp(a.Rotation, b.Rotation, t),
                Vector3.Lerp(a.Scale, b.Scale, t));
        }

        /// <summary>
        /// Additive blend: result = base + (additive - additiveBase) * weight.
        /// Position adds deltas, Rotation composes deltas, Scale multiplies ratios.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static BonePose AdditiveBlend(
            BonePose basePose, BonePose additive, BonePose additiveBase, float weight)
        {
            Vector3 dPos = (additive.Position - additiveBase.Position) * weight;
            Quaternion dRot = KeyframeInterpolation.InterpolateQuaternionSlerp(
                Quaternion.Identity,
                additive.Rotation * Quaternion.Inverse(additiveBase.Rotation),
                weight);
            Vector3 dScale = Vector3.Lerp(Vector3.One, additive.Scale / additiveBase.Scale, weight);

            return new BonePose(
                basePose.Position + dPos,
                dRot * basePose.Rotation,
                basePose.Scale * dScale);
        }
    }

    // ========================================================================
    // BoneTransform - legacy compatibility struct
    // ========================================================================

    /// <summary>Local-space bone transform (legacy compatibility).</summary>
    public struct BoneTransform
    {
        public Vector3 Translation;
        public Quaternion Rotation;
        public Vector3 Scale;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public BoneTransform(Vector3 translation, Quaternion rotation, Vector3 scale)
        {
            Translation = translation;
            Rotation = rotation;
            Scale = scale;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static implicit operator BonePose(BoneTransform bt)
            => new BonePose(bt.Translation, bt.Rotation, bt.Scale);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static implicit operator BoneTransform(BonePose bp)
            => new BoneTransform(bp.Position, bp.Rotation, bp.Scale);
    }

    // ========================================================================
    // Keyframe structs
    // ========================================================================

    /// <summary>Keyframe for a scalar (float) channel.</summary>
    public struct FloatKeyframe
    {
        public float Time;
        public float Value;
        public float TangentIn;
        public float TangentOut;
    }

    /// <summary>Keyframe for a Vector3 channel.</summary>
    public struct Vector3Keyframe
    {
        public float Time;
        public Vector3 Value;
        public Vector3 TangentIn;
        public Vector3 TangentOut;
    }

    /// <summary>Keyframe for a Quaternion channel.</summary>
    public struct QuaternionKeyframe
    {
        public float Time;
        public Quaternion Value;
    }

    // ========================================================================
    // Bone - skeleton bone definition
    // ========================================================================

    /// <summary>
    /// A single bone in a skeleton. Stores name, parent index, rest transform,
    /// inverse bind matrix, and socket offset.
    /// </summary>
    public class Bone
    {
        public string Name { get; set; } = "Bone";
        public int ParentIndex { get; set; } = -1;
        public Transform RestTransform { get; set; } = Transform.Identity;
        public Matrix4x4 InverseBindMatrix { get; set; } = Matrix4x4.Identity;
        public Vector3 SocketOffset { get; set; } = Vector3.Zero;
        public int Index { get; set; } = -1;

        // --- Compatibility properties backed by RestTransform ---

        public Vector3 LocalPosition
        {
            get => RestTransform.Translation;
            set => RestTransform = new Transform(value, RestTransform.Rotation, RestTransform.Scale);
        }

        public Quaternion LocalRotation
        {
            get => RestTransform.Rotation;
            set => RestTransform = new Transform(RestTransform.Translation, value, RestTransform.Scale);
        }

        public Vector3 LocalScale
        {
            get => RestTransform.Scale;
            set => RestTransform = new Transform(RestTransform.Translation, RestTransform.Rotation, value);
        }

        public Bone() { }

        public Bone(string name, int parentIndex)
        {
            Name = name;
            ParentIndex = parentIndex;
        }

        public Bone(string name, int parentIndex, Transform restTransform)
        {
            Name = name;
            ParentIndex = parentIndex;
            RestTransform = restTransform;
        }

        public Bone DeepClone()
        {
            return new Bone
            {
                Name = Name,
                ParentIndex = ParentIndex,
                RestTransform = RestTransform.Clone(),
                InverseBindMatrix = InverseBindMatrix,
                SocketOffset = SocketOffset,
                Index = Index
            };
        }

        public override string ToString() => $"Bone[{Index}] \"{Name}\" parent={ParentIndex}";
    }

    // ========================================================================
    // Skeleton - bone hierarchy with name lookup and bind matrices
    // ========================================================================

    /// <summary>
    /// Skeleton definition: list of bones with parent-child relationships,
    /// name-to-index mapping, root bone identification, and global inverse
    /// bind matrices.
    /// </summary>
    public class Skeleton
    {
        public List<Bone> Bones { get; } = new List<Bone>();
        public Dictionary<string, int> BoneNameToIndex { get; } = new Dictionary<string, int>();
        public int RootBoneIndex { get; set; } = -1;

        public Bone RootBone
            => (RootBoneIndex >= 0 && RootBoneIndex < Bones.Count) ? Bones[RootBoneIndex] : null;

        public int BoneCount => Bones.Count;

        // ------------------------------------------------------------------
        // Bone management
        // ------------------------------------------------------------------

        /// <summary>Add a bone and register it in the name map.</summary>
        public int AddBone(Bone bone)
        {
            if (bone == null) throw new ArgumentNullException(nameof(bone));
            int idx = Bones.Count;
            bone.Index = idx;
            Bones.Add(bone);
            if (!string.IsNullOrEmpty(bone.Name))
                BoneNameToIndex[bone.Name] = idx;
            if (bone.ParentIndex < 0 && RootBoneIndex < 0)
                RootBoneIndex = idx;
            return idx;
        }

        /// <summary>Convenience: add a bone with decomposed local transform.</summary>
        public int AddBone(string name, int parentIndex, Vector3 localPosition)
        {
            return AddBone(new Bone(name, parentIndex,
                new Transform(localPosition, Quaternion.Identity, Vector3.One)));
        }

        /// <summary>Find a bone index by name. Returns -1 if not found.</summary>
        public int FindBone(string name)
        {
            if (string.IsNullOrEmpty(name)) return -1;
            return BoneNameToIndex.TryGetValue(name, out int idx) ? idx : -1;
        }

        /// <summary>Get direct children of a bone.</summary>
        public List<int> GetChildren(int boneIndex)
        {
            var children = new List<int>();
            for (int i = 0; i < Bones.Count; i++)
                if (Bones[i].ParentIndex == boneIndex)
                    children.Add(i);
            return children;
        }

        /// <summary>
        /// Build the chain from a descendant bone up to an ancestor (or root).
        /// Returns indices from ancestor to descendant.
        /// </summary>
        public List<int> GetChain(int fromBone, int toBone)
        {
            var chain = new List<int>();
            int current = toBone;
            while (current >= 0 && current < Bones.Count)
            {
                chain.Add(current);
                if (current == fromBone) break;
                current = Bones[current].ParentIndex;
            }
            chain.Reverse();
            return chain;
        }

        // ------------------------------------------------------------------
        // Bind pose and world matrices
        // ------------------------------------------------------------------

        /// <summary>Create a pose matching the skeleton's rest pose.</summary>
        public SkeletonPose CreateBindPose()
        {
            var pose = new SkeletonPose(this);
            for (int i = 0; i < Bones.Count; i++)
            {
                pose.BonePoses[i] = BonePose.FromTransform(Bones[i].RestTransform);
            }
            return pose;
        }

        /// <summary>
        /// Compute world (model-space) matrices for every bone given a local pose.
        /// Uses row-vector convention: world = local * parentWorld.
        /// </summary>
        public Matrix4x4[] ComputeWorldMatrices(SkeletonPose pose = null)
        {
            var result = new Matrix4x4[Bones.Count];
            for (int i = 0; i < Bones.Count; i++)
            {
                Bone bone = Bones[i];
                BonePose bp = (pose != null && i < pose.BonePoses.Length)
                    ? pose.BonePoses[i]
                    : BonePose.FromTransform(bone.RestTransform);

                Matrix4x4 local = bp.ToMatrix();
                result[i] = (bone.ParentIndex >= 0)
                    ? local * result[bone.ParentIndex]
                    : local;
            }
            return result;
        }

        /// <summary>Compute and cache global inverse bind matrices for all bones.</summary>
        public Matrix4x4[] GetInverseBindMatrices()
        {
            var worldRest = new Matrix4x4[Bones.Count];
            for (int i = 0; i < Bones.Count; i++)
            {
                Matrix4x4 local = Bones[i].RestTransform.ToMatrix();
                worldRest[i] = (Bones[i].ParentIndex >= 0)
                    ? local * worldRest[Bones[i].ParentIndex]
                    : local;
            }
            var inv = new Matrix4x4[Bones.Count];
            for (int i = 0; i < Bones.Count; i++)
            {
                if (!Matrix4x4.Invert(worldRest[i], out inv[i]))
                    inv[i] = Matrix4x4.Identity;
                Bones[i].InverseBindMatrix = inv[i];
            }
            return inv;
        }

        /// <summary>Deep-clone the skeleton (bones are cloned too).</summary>
        public Skeleton Clone()
        {
            var sk = new Skeleton();
            foreach (var b in Bones)
                sk.AddBone(b.DeepClone());
            sk.RootBoneIndex = RootBoneIndex;
            return sk;
        }
    }

    // ========================================================================
    // SkeletonPose - array of bone poses with space conversion and blending
    // ========================================================================

    /// <summary>
    /// Skeleton pose: an array of BonePose values plus a reference to the
    /// Skeleton. Supports local-to-world / world-to-local conversion and
    /// blending of two poses.
    /// </summary>
    public class SkeletonPose
    {
        public BonePose[] BonePoses { get; set; }
        public Skeleton Skeleton { get; set; }

        public int BoneCount => BonePoses?.Length ?? 0;

        public SkeletonPose(Skeleton skeleton)
        {
            Skeleton = skeleton ?? throw new ArgumentNullException(nameof(skeleton));
            BonePoses = new BonePose[skeleton.BoneCount];
            for (int i = 0; i < BonePoses.Length; i++)
                BonePoses[i] = BonePose.Identity;
        }

        public SkeletonPose(Skeleton skeleton, BonePose[] poses)
        {
            Skeleton = skeleton ?? throw new ArgumentNullException(nameof(skeleton));
            BonePoses = poses ?? throw new ArgumentNullException(nameof(poses));
        }

        // ------------------------------------------------------------------
        // Bone pose access
        // ------------------------------------------------------------------

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public BonePose GetBonePose(int boneIndex) => BonePoses[boneIndex];

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetBonePose(int boneIndex, BonePose pose) => BonePoses[boneIndex] = pose;

        // ------------------------------------------------------------------
        // Compatibility API (matches ShanyuEngine.Skinning.SkeletonPose)
        // ------------------------------------------------------------------

        /// <summary>Absolute local rotation of a bone.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Quaternion GetRotation(int boneIndex) => BonePoses[boneIndex].Rotation;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetRotation(int boneIndex, Quaternion rotation)
        {
            var bp = BonePoses[boneIndex];
            bp.Rotation = rotation;
            BonePoses[boneIndex] = bp;
        }

        /// <summary>Translation offset relative to the bone's rest position.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Vector3 GetTranslationOffset(int boneIndex)
        {
            Vector3 rest = Skeleton.Bones[boneIndex].RestTransform.Translation;
            return BonePoses[boneIndex].Position - rest;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetTranslationOffset(int boneIndex, Vector3 offset)
        {
            Vector3 rest = Skeleton.Bones[boneIndex].RestTransform.Translation;
            var bp = BonePoses[boneIndex];
            bp.Position = rest + offset;
            BonePoses[boneIndex] = bp;
        }

        /// <summary>Scale multiplier (1.0 = no change from rest scale).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Vector3 GetScaleOffset(int boneIndex)
        {
            Vector3 rest = Skeleton.Bones[boneIndex].RestTransform.Scale;
            Vector3 cur = BonePoses[boneIndex].Scale;
            return new Vector3(
                rest.X > 1e-6f ? cur.X / rest.X : 1f,
                rest.Y > 1e-6f ? cur.Y / rest.Y : 1f,
                rest.Z > 1e-6f ? cur.Z / rest.Z : 1f);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetScaleOffset(int boneIndex, Vector3 scaleMult)
        {
            Vector3 rest = Skeleton.Bones[boneIndex].RestTransform.Scale;
            var bp = BonePoses[boneIndex];
            bp.Scale = rest * scaleMult;
            BonePoses[boneIndex] = bp;
        }

        // ------------------------------------------------------------------
        // Space conversion
        // ------------------------------------------------------------------

        /// <summary>
        /// Convert local-space bone poses to world (component) space.
        /// worldPose[i] = localPose[i] concatenated with parent world pose.
        /// Bones must be ordered parent-before-child (typical skeleton order).
        /// </summary>
        public void LocalToWorld()
        {
            for (int i = 0; i < BonePoses.Length; i++)
            {
                int parent = Skeleton.Bones[i].ParentIndex;
                if (parent >= 0 && parent < i)
                {
                    Matrix4x4 localM = BonePoses[i].ToMatrix();
                    Matrix4x4 parentWorldM = BonePoses[parent].ToMatrix();
                    BonePoses[i] = BonePose.FromMatrix(localM * parentWorldM);
                }
                // Root bone: local = world, no change needed.
            }
        }

        /// <summary>
        /// Convert world (component) space poses back to local space.
        /// localPose[i] = worldPose[i] * inverse(parentWorldPose).
        /// Iterates in reverse so parent world pose is still available.
        /// </summary>
        public void WorldToLocal()
        {
            for (int i = BonePoses.Length - 1; i >= 0; i--)
            {
                int parent = Skeleton.Bones[i].ParentIndex;
                if (parent >= 0)
                {
                    Matrix4x4 worldM = BonePoses[i].ToMatrix();
                    Matrix4x4 parentWorldM = BonePoses[parent].ToMatrix();
                    if (Matrix4x4.Invert(parentWorldM, out Matrix4x4 invParent))
                        BonePoses[i] = BonePose.FromMatrix(worldM * invParent);
                }
            }
        }

        /// <summary>Get the world-space matrix for a single bone.</summary>
        public Matrix4x4 GetBoneWorldTransform(int boneIndex)
        {
            Matrix4x4 world = BonePoses[boneIndex].ToMatrix();
            int parent = Skeleton.Bones[boneIndex].ParentIndex;
            while (parent >= 0)
            {
                world = world * BonePoses[parent].ToMatrix() * Matrix4x4.Identity;
                // Actually we need parent's WORLD, not local. Recompute properly:
                break;
            }
            // Recompute chain properly
            return ComputeWorldMatrix(boneIndex);
        }

        private Matrix4x4 ComputeWorldMatrix(int boneIndex)
        {
            Matrix4x4 m = BonePoses[boneIndex].ToMatrix();
            int parent = Skeleton.Bones[boneIndex].ParentIndex;
            if (parent >= 0)
                m = m * ComputeWorldMatrix(parent);
            return m;
        }

        /// <summary>Compute all local-to-world matrices (cached array).</summary>
        public Matrix4x4[] GetBoneLocalToWorldMatrixArray()
        {
            return Skeleton.ComputeWorldMatrices(this);
        }

        // ------------------------------------------------------------------
        // Blending
        // ------------------------------------------------------------------

        /// <summary>
        /// Blend two poses. Position/Scale use Lerp, Rotation uses SLerp.
        /// </summary>
        public static SkeletonPose Blend(SkeletonPose a, SkeletonPose b, float alpha)
        {
            if (a == null) return b?.Clone();
            if (b == null) return a.Clone();
            alpha = Math.Clamp(alpha, 0f, 1f);
            int count = Math.Min(a.BoneCount, b.BoneCount);
            var result = new SkeletonPose(a.Skeleton);
            for (int i = 0; i < count; i++)
                result.BonePoses[i] = BonePose.Interpolate(a.BonePoses[i], b.BonePoses[i], alpha);
            return result;
        }

        /// <summary>Blend another pose into this one in-place.</summary>
        public void BlendWith(SkeletonPose other, float alpha)
        {
            if (other == null) return;
            alpha = Math.Clamp(alpha, 0f, 1f);
            int count = Math.Min(BoneCount, other.BoneCount);
            for (int i = 0; i < count; i++)
                BonePoses[i] = BonePose.Interpolate(BonePoses[i], other.BonePoses[i], alpha);
        }

        /// <summary>Additive blend: this = this + (additive - additiveBase) * weight.</summary>
        public void AdditiveBlend(SkeletonPose additive, SkeletonPose additiveBase, float weight)
        {
            if (additive == null) return;
            int count = Math.Min(BoneCount, additive.BoneCount);
            for (int i = 0; i < count; i++)
            {
                BonePose ab = (additiveBase != null && i < additiveBase.BoneCount)
                    ? additiveBase.BonePoses[i] : BonePose.Identity;
                BonePoses[i] = BonePose.AdditiveBlend(BonePoses[i], additive.BonePoses[i], ab, weight);
            }
        }

        // ------------------------------------------------------------------
        // Utility
        // ------------------------------------------------------------------

        public void CopyFrom(SkeletonPose other)
        {
            if (other == null) return;
            int count = Math.Min(BoneCount, other.BoneCount);
            for (int i = 0; i < count; i++)
                BonePoses[i] = other.BonePoses[i];
        }

        public SkeletonPose Clone()
        {
            var poses = new BonePose[BonePoses.Length];
            Array.Copy(BonePoses, poses, BonePoses.Length);
            return new SkeletonPose(Skeleton, poses);
        }

        /// <summary>Mirror the pose along an axis (negates one position component and conjugates rotation).</summary>
        public void Mirror(Axis mirrorAxis)
        {
            for (int i = 0; i < BonePoses.Length; i++)
            {
                var bp = BonePoses[i];
                switch (mirrorAxis)
                {
                    case Axis.X:
                        bp.Position = new Vector3(-bp.Position.X, bp.Position.Y, bp.Position.Z);
                        bp.Rotation = new Quaternion(-bp.Rotation.X, bp.Rotation.Y, bp.Rotation.Z, -bp.Rotation.W);
                        break;
                    case Axis.Y:
                        bp.Position = new Vector3(bp.Position.X, -bp.Position.Y, bp.Position.Z);
                        bp.Rotation = new Quaternion(bp.Rotation.X, -bp.Rotation.Y, bp.Rotation.Z, -bp.Rotation.W);
                        break;
                    case Axis.Z:
                        bp.Position = new Vector3(bp.Position.X, bp.Position.Y, -bp.Position.Z);
                        bp.Rotation = new Quaternion(bp.Rotation.X, bp.Rotation.Y, -bp.Rotation.Z, -bp.Rotation.W);
                        break;
                }
                BonePoses[i] = bp;
            }
        }
    }

    // ========================================================================
    // KeyframeInterpolation - real interpolation mathematics
    // ========================================================================

    /// <summary>
    /// Static utility providing linear, cubic Hermite, quaternion NLerp/Slerp,
    /// constant (step) interpolation, and generic curve evaluation with
    /// binary-search keyframe lookup and loop/clamp extrapolation.
    /// </summary>
    public static class KeyframeInterpolation
    {
        // ------------------------------------------------------------------
        // Linear interpolation
        // ------------------------------------------------------------------

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float InterpolateLinear(float a, float b, float t)
            => a + (b - a) * t;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector3 InterpolateLinear(Vector3 a, Vector3 b, float t)
            => Vector3.Lerp(a, b, t);

        // ------------------------------------------------------------------
        // Cubic Hermite interpolation
        // ------------------------------------------------------------------

        /// <summary>
        /// Cubic Hermite interpolation between a and b with tangent control.
        /// Uses the Hermite basis:
        ///   h00 = 2t^3 - 3t^2 + 1   (value at a)
        ///   h10 = t^3 - 2t^2 + t    (outgoing tangent at a)
        ///   h01 = -2t^3 + 3t^2      (value at b)
        ///   h11 = t^3 - t^2         (incoming tangent at b)
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float InterpolateCubic(
            float a, float b, float t, float tangentIn, float tangentOut)
        {
            float t2 = t * t;
            float t3 = t2 * t;
            float h00 = 2f * t3 - 3f * t2 + 1f;
            float h10 = t3 - 2f * t2 + t;
            float h01 = -2f * t3 + 3f * t2;
            float h11 = t3 - t2;
            return h00 * a + h10 * tangentOut + h01 * b + h11 * tangentIn;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector3 InterpolateCubic(
            Vector3 a, Vector3 b, float t, Vector3 tangentIn, Vector3 tangentOut)
        {
            float t2 = t * t;
            float t3 = t2 * t;
            float h00 = 2f * t3 - 3f * t2 + 1f;
            float h10 = t3 - 2f * t2 + t;
            float h01 = -2f * t3 + 3f * t2;
            float h11 = t3 - t2;
            return h00 * a + h10 * tangentOut + h01 * b + h11 * tangentIn;
        }

        // ------------------------------------------------------------------
        // Quaternion interpolation
        // ------------------------------------------------------------------

        /// <summary>
        /// Normalized Linear interpolation (NLerp) for quaternions.
        /// Fast approximation of SLerp; good for small angles.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Quaternion InterpolateQuaternionLinear(Quaternion a, Quaternion b, float t)
        {
            // Ensure shortest path
            float dot = a.X * b.X + a.Y * b.Y + a.Z * b.Z + a.W * b.W;
            if (dot < 0f)
                b = new Quaternion(-b.X, -b.Y, -b.Z, -b.W);

            Quaternion r = new Quaternion(
                a.X + (b.X - a.X) * t,
                a.Y + (b.Y - a.Y) * t,
                a.Z + (b.Z - a.Z) * t,
                a.W + (b.W - a.W) * t);
            return Quaternion.Normalize(r);
        }

        /// <summary>
        /// Spherical Linear interpolation (SLerp) for quaternions.
        /// Handles dot-product sign for shortest path, and degenerates to
        /// NLerp when the quaternions are nearly parallel (dot > 0.9995).
        /// </summary>
        public static Quaternion InterpolateQuaternionSlerp(Quaternion a, Quaternion b, float t)
        {
            // Dot product to determine the angle between quaternions
            float dot = a.X * b.X + a.Y * b.Y + a.Z * b.Z + a.W * b.W;

            // If dot < 0, negate b to take the shortest path
            if (dot < 0f)
            {
                b = new Quaternion(-b.X, -b.Y, -b.Z, -b.W);
                dot = -dot;
            }

            // If the quaternions are nearly parallel, fall back to NLerp
            const float threshold = 0.9995f;
            if (dot > threshold)
            {
                return Quaternion.Normalize(new Quaternion(
                    a.X + (b.X - a.X) * t,
                    a.Y + (b.Y - a.Y) * t,
                    a.Z + (b.Z - a.Z) * t,
                    a.W + (b.W - a.W) * t));
            }

            // Clamp dot to valid acos domain
            dot = Math.Clamp(dot, -1f, 1f);
            float theta0 = MathF.Acos(dot);     // angle between a and b
            float theta = theta0 * t;            // interpolated angle
            float sinTheta = MathF.Sin(theta);
            float sinTheta0 = MathF.Sin(theta0);

            float s0 = MathF.Cos(theta) - dot * sinTheta / sinTheta0;
            float s1 = sinTheta / sinTheta0;

            return new Quaternion(
                a.X * s0 + b.X * s1,
                a.Y * s0 + b.Y * s1,
                a.Z * s0 + b.Z * s1,
                a.W * s0 + b.W * s1);
        }

        // ------------------------------------------------------------------
        // Constant (step) interpolation
        // ------------------------------------------------------------------

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float InterpolateConstant(float a, float b, float t) => a;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector3 InterpolateConstant(Vector3 a, Vector3 b, float t) => a;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Quaternion InterpolateConstant(Quaternion a, Quaternion b, float t) => a;

        // ------------------------------------------------------------------
        // Curve evaluation (binary search + extrapolation)
        // ------------------------------------------------------------------

        /// <summary>
        /// Evaluate a float keyframe curve at the given time.
        /// Uses binary search to find the surrounding keyframes, then applies
        /// the specified interpolation mode. Handles loop, ping-pong, and clamp.
        /// </summary>
        public static float EvaluateFloatCurve(
            List<FloatKeyframe> keys, float time,
            InterpolationMode mode, ExtrapolationMode extrap = ExtrapolationMode.Clamp)
        {
            if (keys == null || keys.Count == 0) return 0f;
            if (keys.Count == 1) return keys[0].Value;

            time = WrapTime(time, keys[0].Time, keys[keys.Count - 1].Time, extrap);

            // Binary search for the keyframe pair
            int lo = 0, hi = keys.Count - 1;
            if (time <= keys[0].Time) return keys[0].Value;
            if (time >= keys[hi].Time) return keys[hi].Value;

            while (hi - lo > 1)
            {
                int mid = (lo + hi) >> 1;
                if (keys[mid].Time <= time) lo = mid; else hi = mid;
            }

            FloatKeyframe k0 = keys[lo], k1 = keys[hi];
            float range = k1.Time - k0.Time;
            float t = range > 1e-6f ? (time - k0.Time) / range : 0f;

            switch (mode)
            {
                case InterpolationMode.Linear:
                    return InterpolateLinear(k0.Value, k1.Value, t);
                case InterpolationMode.Cubic:
                    return InterpolateCubic(k0.Value, k1.Value, t, k0.TangentIn, k1.TangentOut);
                case InterpolationMode.Step:
                case InterpolationMode.Constant:
                    return InterpolateConstant(k0.Value, k1.Value, t);
                default:
                    return InterpolateLinear(k0.Value, k1.Value, t);
            }
        }

        /// <summary>Evaluate a Vector3 keyframe curve.</summary>
        public static Vector3 EvaluateVector3Curve(
            List<Vector3Keyframe> keys, float time,
            InterpolationMode mode, ExtrapolationMode extrap = ExtrapolationMode.Clamp)
        {
            if (keys == null || keys.Count == 0) return Vector3.Zero;
            if (keys.Count == 1) return keys[0].Value;

            time = WrapTime(time, keys[0].Time, keys[keys.Count - 1].Time, extrap);

            int lo = 0, hi = keys.Count - 1;
            if (time <= keys[0].Time) return keys[0].Value;
            if (time >= keys[hi].Time) return keys[hi].Value;

            while (hi - lo > 1)
            {
                int mid = (lo + hi) >> 1;
                if (keys[mid].Time <= time) lo = mid; else hi = mid;
            }

            Vector3Keyframe k0 = keys[lo], k1 = keys[hi];
            float range = k1.Time - k0.Time;
            float t = range > 1e-6f ? (time - k0.Time) / range : 0f;

            switch (mode)
            {
                case InterpolationMode.Linear:
                    return InterpolateLinear(k0.Value, k1.Value, t);
                case InterpolationMode.Cubic:
                    return InterpolateCubic(k0.Value, k1.Value, t, k0.TangentIn, k1.TangentOut);
                case InterpolationMode.Step:
                case InterpolationMode.Constant:
                    return InterpolateConstant(k0.Value, k1.Value, t);
                default:
                    return InterpolateLinear(k0.Value, k1.Value, t);
            }
        }

        /// <summary>Evaluate a Quaternion keyframe curve.</summary>
        public static Quaternion EvaluateQuaternionCurve(
            List<QuaternionKeyframe> keys, float time,
            InterpolationMode mode, ExtrapolationMode extrap = ExtrapolationMode.Clamp)
        {
            if (keys == null || keys.Count == 0) return Quaternion.Identity;
            if (keys.Count == 1) return keys[0].Value;

            time = WrapTime(time, keys[0].Time, keys[keys.Count - 1].Time, extrap);

            int lo = 0, hi = keys.Count - 1;
            if (time <= keys[0].Time) return keys[0].Value;
            if (time >= keys[hi].Time) return keys[hi].Value;

            while (hi - lo > 1)
            {
                int mid = (lo + hi) >> 1;
                if (keys[mid].Time <= time) lo = mid; else hi = mid;
            }

            QuaternionKeyframe k0 = keys[lo], k1 = keys[hi];
            float range = k1.Time - k0.Time;
            float t = range > 1e-6f ? (time - k0.Time) / range : 0f;

            switch (mode)
            {
                case InterpolationMode.Slerp:
                    return InterpolateQuaternionSlerp(k0.Value, k1.Value, t);
                case InterpolationMode.Nlerp:
                    return InterpolateQuaternionLinear(k0.Value, k1.Value, t);
                case InterpolationMode.Step:
                case InterpolationMode.Constant:
                    return InterpolateConstant(k0.Value, k1.Value, t);
                case InterpolationMode.Linear:
                    return InterpolateQuaternionLinear(k0.Value, k1.Value, t);
                default:
                    return InterpolateQuaternionSlerp(k0.Value, k1.Value, t);
            }
        }

        // ------------------------------------------------------------------
        // Time wrapping
        // ------------------------------------------------------------------

        /// <summary>
        /// Wrap or clamp time into the [startTime, endTime] range based on
        /// the extrapolation mode.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float WrapTime(float time, float startTime, float endTime, ExtrapolationMode mode)
        {
            float duration = endTime - startTime;
            if (duration <= 0f) return startTime;

            switch (mode)
            {
                case ExtrapolationMode.Loop:
                    float off = (time - startTime) % duration;
                    if (off < 0f) off += duration;
                    return startTime + off;

                case ExtrapolationMode.PingPong:
                    float p = (time - startTime) % (2f * duration);
                    if (p < 0f) p += 2f * duration;
                    if (p > duration) p = 2f * duration - p;
                    return startTime + p;

                default: // Clamp
                    return Math.Clamp(time, startTime, endTime);
            }
        }
    }

    // ========================================================================
    // BoneTrack - per-bone animation channel
    // ========================================================================

    /// <summary>
    /// Animation track for a single bone. Contains separate keyframe lists
    /// for position, rotation, and scale, each with its own interpolation mode.
    /// </summary>
    public class BoneTrack
    {
        public int BoneIndex { get; set; } = -1;
        public string BoneName { get; set; } = "";
        public List<Vector3Keyframe> PositionKeys { get; } = new List<Vector3Keyframe>();
        public List<QuaternionKeyframe> RotationKeys { get; } = new List<QuaternionKeyframe>();
        public List<Vector3Keyframe> ScaleKeys { get; } = new List<Vector3Keyframe>();

        public InterpolationMode PositionInterpolation { get; set; } = InterpolationMode.Linear;
        public InterpolationMode RotationInterpolation { get; set; } = InterpolationMode.Slerp;
        public InterpolationMode ScaleInterpolation { get; set; } = InterpolationMode.Linear;

        /// <summary>
        /// Evaluate this track at the given time and write the result into the
        /// bone pose. Only channels with keys are modified.
        /// </summary>
        public void Evaluate(float time, BonePose[] bonePoses)
        {
            if (BoneIndex < 0 || BoneIndex >= bonePoses.Length) return;

            var bp = bonePoses[BoneIndex];

            if (PositionKeys.Count > 0)
                bp.Position = KeyframeInterpolation.EvaluateVector3Curve(
                    PositionKeys, time, PositionInterpolation, ExtrapolationMode.Clamp);

            if (RotationKeys.Count > 0)
                bp.Rotation = KeyframeInterpolation.EvaluateQuaternionCurve(
                    RotationKeys, time, RotationInterpolation, ExtrapolationMode.Clamp);

            if (ScaleKeys.Count > 0)
                bp.Scale = KeyframeInterpolation.EvaluateVector3Curve(
                    ScaleKeys, time, ScaleInterpolation, ExtrapolationMode.Clamp);

            bonePoses[BoneIndex] = bp;
        }

        /// <summary>Get the time range covered by this track.</summary>
        public (float start, float end) GetTimeRange()
        {
            float start = float.MaxValue, end = float.MinValue;
            foreach (var k in PositionKeys)
            {
                start = Math.Min(start, k.Time);
                end = Math.Max(end, k.Time);
            }
            foreach (var k in RotationKeys)
            {
                start = Math.Min(start, k.Time);
                end = Math.Max(end, k.Time);
            }
            foreach (var k in ScaleKeys)
            {
                start = Math.Min(start, k.Time);
                end = Math.Max(end, k.Time);
            }
            if (start == float.MaxValue) return (0f, 0f);
            return (start, end);
        }
    }

    // ========================================================================
    // AnimationClipReal - animation clip with bone tracks and sampling
    // ========================================================================

    /// <summary>
    /// Animation clip: a collection of bone tracks sampled over a duration.
    /// Supports Evaluate (sample at arbitrary time) and Sample (sample a frame).
    /// </summary>
    public class AnimationClipReal
    {
        public string Name { get; set; } = "Clip";
        public List<BoneTrack> Tracks { get; } = new List<BoneTrack>();
        public float Duration { get; set; } = 0f;
        public float FrameRate { get; set; } = 30f;
        public bool Loop { get; set; }
        public BoneTrack RootMotionTrack { get; set; }

        /// <summary>Find a track by bone index.</summary>
        public BoneTrack FindTrack(int boneIndex)
        {
            foreach (var t in Tracks)
                if (t.BoneIndex == boneIndex) return t;
            return null;
        }

        /// <summary>Find a track by bone name.</summary>
        public BoneTrack FindTrack(string boneName)
        {
            foreach (var t in Tracks)
                if (t.BoneName == boneName) return t;
            return null;
        }

        /// <summary>Get the total frame count.</summary>
        public int FrameCount => Duration > 0 && FrameRate > 0
            ? (int)MathF.Ceiling(Duration * FrameRate) : 0;

        /// <summary>
        /// Evaluate the clip at an arbitrary time, writing the sampled
        /// bone transforms into the provided pose.
        /// </summary>
        public void Evaluate(float time, SkeletonPose pose)
        {
            if (pose == null) return;

            // Handle looping / clamping
            if (Duration > 0)
            {
                if (Loop)
                {
                    time = ((time % Duration) + Duration) % Duration;
                }
                else
                {
                    time = Math.Clamp(time, 0f, Duration);
                }
            }

            // Evaluate each track
            foreach (var track in Tracks)
                track.Evaluate(time, pose.BonePoses);
        }

        /// <summary>
        /// Sample a specific frame index. Converts frame to time via FrameRate.
        /// </summary>
        public void Sample(int frameIndex, SkeletonPose pose)
        {
            float time = FrameRate > 0 ? frameIndex / FrameRate : 0f;
            Evaluate(time, pose);
        }

        /// <summary>
        /// Evaluate the clip and return a new pose (does not modify the skeleton's bind pose).
        /// </summary>
        public SkeletonPose Evaluate(float time, Skeleton skeleton)
        {
            var pose = new SkeletonPose(skeleton);
            Evaluate(time, pose);
            return pose;
        }

        /// <summary>Compute the duration from track keyframes if not set explicitly.</summary>
        public void RecalculateDuration()
        {
            float maxTime = 0f;
            foreach (var track in Tracks)
            {
                var (start, end) = track.GetTimeRange();
                maxTime = Math.Max(maxTime, end);
            }
            Duration = maxTime;
        }
    }

    // ========================================================================
    // RootMotionExtractor - extract root motion delta from a clip
    // ========================================================================

    /// <summary>
    /// Extracts root motion (delta position and rotation) from an animation
    /// clip's root bone track. Provides delta-from-start and delta-between-times.
    /// </summary>
    public class RootMotionExtractor
    {
        public AnimationClipReal Clip { get; }
        public int RootBoneIndex { get; set; }

        public RootMotionExtractor(AnimationClipReal clip, int rootBoneIndex = 0)
        {
            Clip = clip ?? throw new ArgumentNullException(nameof(clip));
            RootBoneIndex = rootBoneIndex;
        }

        /// <summary>
        /// Extract the accumulated root motion delta from clip start (t=0) to
        /// the given time. Returns a Transform with delta position and rotation.
        /// </summary>
        public Transform ExtractRootMotion(float time)
        {
            Vector3 startPos = GetRootPosition(0f);
            Quaternion startRot = GetRootRotation(0f);
            Vector3 currPos = GetRootPosition(time);
            Quaternion currRot = GetRootRotation(time);

            Vector3 deltaPos = currPos - startPos;
            // Delta rotation = currRot * inverse(startRot)
            Quaternion deltaRot = currRot * Quaternion.Inverse(startRot);

            return new Transform(deltaPos, deltaRot, Vector3.One);
        }

        /// <summary>
        /// Extract the root motion delta between two time points.
        /// Useful for per-frame root motion extraction.
        /// </summary>
        public Transform ExtractDeltaMotion(float prevTime, float currTime)
        {
            Vector3 prevPos = GetRootPosition(prevTime);
            Quaternion prevRot = GetRootRotation(prevTime);
            Vector3 currPos = GetRootPosition(currTime);
            Quaternion currRot = GetRootRotation(currTime);

            Vector3 deltaPos = currPos - prevPos;
            Quaternion deltaRot = currRot * Quaternion.Inverse(prevRot);

            return new Transform(deltaPos, deltaRot, Vector3.One);
        }

        /// <summary>Sample the root bone's position at a given time.</summary>
        public Vector3 GetRootPosition(float time)
        {
            var track = Clip.FindTrack(RootBoneIndex);
            if (track == null || track.PositionKeys.Count == 0) return Vector3.Zero;
            return KeyframeInterpolation.EvaluateVector3Curve(
                track.PositionKeys, time, track.PositionInterpolation, ExtrapolationMode.Clamp);
        }

        /// <summary>Sample the root bone's rotation at a given time.</summary>
        public Quaternion GetRootRotation(float time)
        {
            var track = Clip.FindTrack(RootBoneIndex);
            if (track == null || track.RotationKeys.Count == 0) return Quaternion.Identity;
            return KeyframeInterpolation.EvaluateQuaternionCurve(
                track.RotationKeys, time, track.RotationInterpolation, ExtrapolationMode.Clamp);
        }

        /// <summary>Estimate the root velocity at a given time using finite differences.</summary>
        public Vector3 GetRootVelocity(float time, float dt = 0.0166f)
        {
            Vector3 p0 = GetRootPosition(time);
            Vector3 p1 = GetRootPosition(time + dt);
            return (p1 - p0) / dt;
        }

        /// <summary>Estimate the root angular velocity (axis-angle) at a given time.</summary>
        public Vector3 GetRootAngularVelocity(float time, float dt = 0.0166f)
        {
            Quaternion r0 = GetRootRotation(time);
            Quaternion r1 = GetRootRotation(time + dt);
            Quaternion delta = r1 * Quaternion.Inverse(r0);
            // Convert delta quaternion to axis-angle * (1/dt)
            float angle = 2f * MathF.Acos(Math.Clamp(delta.W, -1f, 1f));
            float sinHalfAngle = MathF.Sqrt(Math.Max(0f, 1f - delta.W * delta.W));
            if (sinHalfAngle < 1e-6f) return Vector3.Zero;
            Vector3 axis = new Vector3(delta.X, delta.Y, delta.Z) / sinHalfAngle;
            return axis * (angle / dt);
        }
    }
}
