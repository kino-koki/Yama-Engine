using System.Numerics;
using ShanyuEngine.Skinning;

namespace ShanyuEngine.Animation
{
    /// <summary>Identifies the kind of inverse-kinematics solver.</summary>

    /// <summary>
    /// Common interface for runtime IK solvers. A solver reads the current pose, computes
    /// corrected bone transforms and writes them back into the same pose.
    /// </summary>

    /// <summary>
    /// Two-bone IK solver (e.g. arm or leg). Solves a chain of exactly three bones
    /// (root, mid, tip) so the tip reaches a target position, with an optional bend hint
    /// ("pole") to disambiguate the bend plane and an optional target rotation for the tip.
    /// </summary>

    /// <summary>
    /// Cyclic Coordinate Descent IK solver. Iteratively rotates each bone in a chain
    /// (from the bone just before the tip down to the root) so the tip approaches the
    /// target. Suitable for chains of arbitrary length such as tails, tentacles or spines.
    /// </summary>

    /// <summary>
    /// Forward And Backward Reaching Inverse Kinematics solver. Produces very natural
    /// results for long chains by alternately reaching from the tip to the target and
    /// from the root back to its origin. Rotations are derived from the solved joint
    /// positions so the skeleton deforms correctly.
    /// </summary>

    // -------------------------------------------------------------------------
    // Constraints
    // -------------------------------------------------------------------------

    /// <summary>Base class for runtime rig constraints applied after animation sampling.</summary>
    public abstract class RigConstraint
    {
        public string Name { get; set; } = "Constraint";
        public float Weight { get; set; } = 1f;
        public bool Enabled { get; set; } = true;
        public abstract void Apply(Skeleton skeleton, SkeletonPose pose);
    }

    /// <summary>
    /// Rotates a bone so a chosen local axis points toward a world-space target
    /// position. This is the workhorse behind head-tracking, weapon-aim and look-at rigs.
    /// </summary>
    public sealed class LookAtConstraint : RigConstraint
    {
        public int BoneIndex { get; set; } = -1;
        public Vector3 TargetPosition { get; set; }
        /// <summary>Local axis that should point at the target (default forward -Z).</summary>
        public Vector3 Axis { get; set; } = new(0, 0, -1);
        /// <summary>Optional world up vector used to stabilise roll.</summary>
        public Vector3 UpVector { get; set; } = new(0, 1, 0);
        public bool UseUpVector { get; set; }

        public LookAtConstraint() { Name = "LookAt"; }

        public LookAtConstraint(int boneIndex, Vector3 targetPosition) : this()
        {
            BoneIndex = boneIndex;
            TargetPosition = targetPosition;
        }

        public override void Apply(Skeleton skeleton, SkeletonPose pose)
        {
            if (skeleton == null || pose == null) return;
            if (!Enabled || Weight <= 0f) return;
            if (BoneIndex < 0 || BoneIndex >= skeleton.Bones.Count) return;

            var world = skeleton.ComputeWorldMatrices(pose);
            var bonePos = RigPoseMath.GetWorldPosition(world, BoneIndex);
            var boneWorldRot = RigPoseMath.GetWorldRotation(skeleton, pose, BoneIndex);

            var currentDir = Vector3.Normalize(Vector3.Transform(Axis, boneWorldRot));
            var toTarget = TargetPosition - bonePos;
            if (toTarget.LengthSquared() < 1e-8f) return;
            var desiredDir = Vector3.Normalize(toTarget);

            var delta = RigPoseMath.RotationFromTo(currentDir, desiredDir);
            delta = Quaternion.Slerp(Quaternion.Identity, delta, Weight);

            var newWorldRot = Quaternion.Normalize(delta * boneWorldRot);

            if (UseUpVector)
            {
                // Re-orthogonalise roll so the bone's up aligns with the requested up.
                var fwd = Vector3.Normalize(Vector3.Transform(Axis, newWorldRot));
                var right = Vector3.Cross(fwd, UpVector);
                if (right.LengthSquared() > 1e-8f)
                {
                    right = Vector3.Normalize(right);
                    var up = Vector3.Cross(right, fwd);
                    var m = Matrix4x4.CreateWorld(Vector3.Zero, fwd, up);
                    if (Matrix4x4.Decompose(m, out _, out var rot, out _))
                        newWorldRot = Quaternion.Slerp(newWorldRot, rot, Weight);
                }
            }

            RigPoseMath.SetWorldRotation(skeleton, pose, BoneIndex, newWorldRot);
        }
    }

    /// <summary>
    /// Aims one or more source bones at a target and applies the resulting rotation to a
    /// constrained bone. Supports weighted multi-source averaging like Unity's aim constraint.
    /// </summary>
    public sealed class AimConstraint : RigConstraint
    {
        public int BoneIndex { get; set; } = -1;
        public Vector3 AimAxis { get; set; } = new(0, 0, -1);
        public Vector3 UpAxis { get; set; } = new(0, 1, 0);
        public List<Vector3> SourcePositions { get; set; } = new();
        public List<float> SourceWeights { get; set; } = new();

        public AimConstraint() { Name = "Aim"; }

        public override void Apply(Skeleton skeleton, SkeletonPose pose)
        {
            if (skeleton == null || pose == null) return;
            if (!Enabled || Weight <= 0f) return;
            if (BoneIndex < 0 || BoneIndex >= skeleton.Bones.Count) return;
            if (SourcePositions.Count == 0) return;

            var world = skeleton.ComputeWorldMatrices(pose);
            var bonePos = RigPoseMath.GetWorldPosition(world, BoneIndex);
            var boneWorldRot = RigPoseMath.GetWorldRotation(skeleton, pose, BoneIndex);

            // Average source direction weighted by per-source weights.
            var avgDir = Vector3.Zero;
            float wSum = 0f;
            for (var i = 0; i < SourcePositions.Count; i++)
            {
                var w = i < SourceWeights.Count ? SourceWeights[i] : 1f;
                var d = SourcePositions[i] - bonePos;
                if (d.LengthSquared() > 1e-10f)
                    avgDir += Vector3.Normalize(d) * w;
                wSum += w;
            }
            if (wSum <= 0f || avgDir.LengthSquared() < 1e-10f) return;
            avgDir = Vector3.Normalize(avgDir);

            var currentDir = Vector3.Normalize(Vector3.Transform(AimAxis, boneWorldRot));
            var delta = RigPoseMath.RotationFromTo(currentDir, avgDir);
            delta = Quaternion.Slerp(Quaternion.Identity, delta, Weight);
            var newWorldRot = Quaternion.Normalize(delta * boneWorldRot);

            // Stabilise roll using the configured up axis.
            var fwd = Vector3.Normalize(Vector3.Transform(AimAxis, newWorldRot));
            var right = Vector3.Cross(fwd, UpAxis);
            if (right.LengthSquared() > 1e-8f)
            {
                right = Vector3.Normalize(right);
                var up = Vector3.Cross(right, fwd);
                var m = Matrix4x4.CreateWorld(Vector3.Zero, fwd, up);
                if (Matrix4x4.Decompose(m, out _, out var rot, out _))
                    newWorldRot = Quaternion.Slerp(newWorldRot, rot, Weight);
            }

            RigPoseMath.SetWorldRotation(skeleton, pose, BoneIndex, newWorldRot);
        }
    }

    /// <summary>
    /// Limits a bone's local rotation using a twist-and-swing decomposition around a
    /// configurable axis. Twist is clamped to [TwistMin, TwistMax] degrees and swing is
    /// clamped to the SwingLimit cone half-angle. Useful for joints such as elbows and
    /// knees that should not hyperextend.
    /// </summary>
    public sealed class RotationLimitConstraint : RigConstraint
    {
        public int BoneIndex { get; set; } = -1;
        /// <summary>Local axis the twist rotates around (should be normalised).</summary>
        public Vector3 TwistAxis { get; set; } = new(0, 0, 1);
        /// <summary>Minimum twist angle in degrees.</summary>
        public float TwistMin { get; set; } = -180f;
        /// <summary>Maximum twist angle in degrees.</summary>
        public float TwistMax { get; set; } = 180f;
        /// <summary>Maximum swing (cone) half-angle in degrees.</summary>
        public float SwingLimit { get; set; } = 180f;

        public RotationLimitConstraint() { Name = "RotationLimit"; }

        public RotationLimitConstraint(int boneIndex) : this()
        {
            BoneIndex = boneIndex;
        }

        public override void Apply(Skeleton skeleton, SkeletonPose pose)
        {
            if (skeleton == null || pose == null) return;
            if (!Enabled || Weight <= 0f) return;
            if (BoneIndex < 0 || BoneIndex >= skeleton.Bones.Count) return;

            var axis = Vector3.Normalize(TwistAxis);
            var localRot = pose.GetRotation(BoneIndex);

            // Decompose into twist (around axis) and swing (the remainder).
            var twist = DecomposeTwist(localRot, axis);
            var swing = localRot * Quaternion.Inverse(twist);

            // Clamp twist angle.
            twist = ClampTwist(twist, axis, TwistMin, TwistMax);

            // Clamp swing to cone.
            swing = ClampSwing(swing, axis, SwingLimit);

            var clamped = Quaternion.Normalize(swing * twist);

            // Blend toward the clamped rotation by weight.
            var final = Quaternion.Slerp(localRot, clamped, Weight);
            pose.SetRotation(BoneIndex, final);
        }

        /// <summary>Extract the twist component of a rotation around the given axis.</summary>
        private static Quaternion DecomposeTwist(Quaternion q, Vector3 axis)
        {
            // Project the quaternion's vector part onto the axis.
            var v = new Vector3(q.X, q.Y, q.Z);
            var proj = axis * Vector3.Dot(v, axis);
            var twist = new Quaternion(proj.X, proj.Y, proj.Z, q.W);
            var lenSq = twist.X * twist.X + twist.Y * twist.Y + twist.Z * twist.Z + twist.W * twist.W;
            if (lenSq < 1e-10f)
                return Quaternion.Identity;
            return Quaternion.Normalize(twist);
        }

        private static Quaternion ClampTwist(Quaternion twist, Vector3 axis, float minDeg, float maxDeg)
        {
            // Twist angle around axis: project a reference perpendicular vector.
            var refPerp = MathF.Abs(axis.X) < 0.9f
                ? Vector3.Normalize(Vector3.Cross(axis, Vector3.UnitX))
                : Vector3.Normalize(Vector3.Cross(axis, Vector3.UnitY));

            var rotated = Vector3.Transform(refPerp, twist);
            // Angle between refPerp and rotated, signed around axis.
            var projected = rotated - axis * Vector3.Dot(rotated, axis);
            if (projected.LengthSquared() < 1e-10f) return twist;
            projected = Vector3.Normalize(projected);

            float angle = MathF.Atan2(
                Vector3.Dot(Vector3.Cross(refPerp, projected), axis),
                Vector3.Dot(refPerp, projected));
            float deg = angle * (180f / MathF.PI);

            if (deg < minDeg) deg = minDeg;
            if (deg > maxDeg) deg = maxDeg;
            return Quaternion.CreateFromAxisAngle(axis, deg * (MathF.PI / 180f));
        }

        private static Quaternion ClampSwing(Quaternion swing, Vector3 axis, float limitDeg)
        {
            if (limitDeg >= 180f) return swing;
            // Swing rotates the axis itself; measure the angle between axis and axis*swing.
            var swung = Vector3.Normalize(Vector3.Transform(axis, swing));
            float dot = Math.Clamp(Vector3.Dot(axis, swung), -1f, 1f);
            float angle = MathF.Acos(dot);
            float limit = limitDeg * (MathF.PI / 180f);
            if (angle <= limit) return swing;

            // Slerp between identity and swing to clamp within the cone.
            float t = limit / angle;
            return Quaternion.Slerp(Quaternion.Identity, swing, t);
        }
    }

    /// <summary>
    /// A collection of IK solvers and constraints. Solvers run first (in order), then
    /// constraints. The rig is applied to a pose after animation evaluation.
    /// </summary>
    public sealed class AnimationRig
    {
        public string Name { get; set; } = "Rig";
        public List<IKSolver> Solvers { get; set; } = new();
        public List<RigConstraint> Constraints { get; set; } = new();
        public bool Enabled { get; set; } = true;

        public void AddSolver(IKSolver solver) => Solvers.Add(solver ?? throw new ArgumentNullException(nameof(solver)));
        public void AddConstraint(RigConstraint constraint) => Constraints.Add(constraint ?? throw new ArgumentNullException(nameof(constraint)));

        public void Apply(Skeleton skeleton, SkeletonPose pose)
        {
            if (!Enabled || skeleton == null || pose == null) return;
            foreach (var solver in Solvers)
                solver?.Solve(skeleton, pose);
            foreach (var constraint in Constraints)
                constraint?.Apply(skeleton, pose);
        }

        public bool Validate(Skeleton skeleton, out List<string> errors)
        {
            errors = new List<string>();
            foreach (var s in Solvers)
                if (s != null && !s.IsValid(skeleton))
                    errors.Add($"Solver '{s.Name}' ({s.Type}) is invalid for the skeleton.");
            return errors.Count == 0;
        }
    }

    /// <summary>Low-level helpers for converting between local and world bone transforms.</summary>
    internal static class RigPoseMath
    {
        public static Vector3 GetWorldPosition(Matrix4x4[] world, int index)
            => new(world[index].M41, world[index].M42, world[index].M43);

        /// <summary>
        /// World rotation of a bone, computed by walking the parent chain and composing
        /// local rotations. This avoids matrix decomposition issues caused by scale.
        /// </summary>
        public static Quaternion GetWorldRotation(Skeleton skeleton, SkeletonPose pose, int index)
        {
            var rot = pose.GetRotation(index);
            var p = skeleton.Bones[index].ParentIndex;
            while (p >= 0)
            {
                rot = pose.GetRotation(p) * rot;
                p = skeleton.Bones[p].ParentIndex;
            }
            return rot;
        }

        /// <summary>Set a bone's local rotation so its world rotation matches <paramref name="worldRot"/>.</summary>
        public static void SetWorldRotation(Skeleton skeleton, SkeletonPose pose, int index, Quaternion worldRot)
        {
            var parent = skeleton.Bones[index].ParentIndex;
            var local = parent >= 0
                ? Quaternion.Inverse(GetWorldRotation(skeleton, pose, parent)) * worldRot
                : worldRot;
            pose.SetRotation(index, Quaternion.Normalize(local));
        }

        /// <summary>Shortest-arc rotation that maps direction <paramref name="from"/> to <paramref name="to"/>.</summary>
        public static Quaternion RotationFromTo(Vector3 from, Vector3 to)
        {
            var f = from.LengthSquared() < 1e-12f ? Vector3.UnitZ : Vector3.Normalize(from);
            var t = to.LengthSquared() < 1e-12f ? Vector3.UnitZ : Vector3.Normalize(to);

            var dot = Math.Clamp(Vector3.Dot(f, t), -1f, 1f);
            if (dot >= 0.999999f)
                return Quaternion.Identity;
            if (dot <= -0.999999f)
            {
                // 180-degree turn: pick any perpendicular axis.
                var axis = MathF.Abs(f.X) < 0.9f
                    ? Vector3.Normalize(Vector3.Cross(f, Vector3.UnitX))
                    : Vector3.Normalize(Vector3.Cross(f, Vector3.UnitY));
                return Quaternion.CreateFromAxisAngle(axis, MathF.PI);
            }

            var c = Vector3.Cross(f, t);
            var q = new Quaternion(c.X, c.Y, c.Z, 1f + dot);
            return Quaternion.Normalize(q);
        }
    }
}
