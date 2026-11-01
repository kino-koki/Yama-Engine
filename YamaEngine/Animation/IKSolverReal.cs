// IKSolverReal.cs - Real inverse kinematics solvers for the YamaEngine animation system.
//
// Implements mathematically correct IK algorithms using System.Numerics:
//   - TwoBoneIKSolver: analytic 2-bone IK using the law of cosines and pole-vector control
//   - LookAtIKSolver: bone-chain rotation toward a target with per-bone angle limits
//   - FABRIKSolver: Forward And Backward Reaching Inverse Kinematics
//   - CCDIKSolver: Cyclic Coordinate Descent
//   - LimbIKSolver: two-bone IK plus foot-rotation correction
//   - SpringIKSolver: spring-damper chain with length constraints

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace YamaEngine.Animation
{
    // ========================================================================
    // IKResult - result returned by all IK solvers
    // ========================================================================

    /// <summary>
    /// Result of an IK solve: the solved chain positions, whether the target
    /// was reached within tolerance, and the number of iterations used.
    /// </summary>
    public struct IKResult
    {
        public Vector3[] Positions;
        public bool Converged;
        public int Iterations;
        public float FinalError;

        public IKResult(Vector3[] positions, bool converged, int iterations, float error)
        {
            Positions = positions;
            Converged = converged;
            Iterations = iterations;
            FinalError = error;
        }
    }

    // ========================================================================
    // TwoBoneIKSolver - analytic two-bone IK
    // ========================================================================

    /// <summary>
    /// Analytic two-bone IK solver. Given a root joint (e.g. shoulder), a mid
    /// joint (e.g. elbow), and an end effector (e.g. hand), computes new
    /// positions for the mid joint and end effector so that the end effector
    /// reaches the target while the mid joint bends towards the pole vector.
    ///
    /// Uses the law of cosines:
    ///   cos(A) = (lenA^2 + dist^2 - lenB^2) / (2 * lenA * dist)
    ///   cos(B) = (lenA^2 + lenB^2 - dist^2) / (2 * lenA * lenB)
    /// where A is the angle at the root, B is the angle at the mid joint,
    /// lenA/lenB are the upper/lower bone lengths, and dist is the clamped
    /// root-to-target distance.
    /// </summary>
    public class TwoBoneIKSolverReal
    {
        /// <summary>Index of the root bone (e.g. upper arm).</summary>
        public int RootBoneIndex { get; set; }
        /// <summary>Index of the mid bone (e.g. forearm).</summary>
        public int MidBoneIndex { get; set; }
        /// <summary>Index of the end-effector bone (e.g. hand).</summary>
        public int EndBoneIndex { get; set; }

        /// <summary>Pole vector target (bend direction). The mid joint bends towards this.</summary>
        public Vector3 PoleTarget { get; set; }

        /// <summary>Blend weight [0..1] of the IK correction.</summary>
        public float Weight { get; set; } = 1f;

        /// <summary>Small epsilon to avoid division by zero.</summary>
        private const float Eps = 1e-6f;

        /// <summary>
        /// Solve two-bone IK analytically.
        /// </summary>
        /// <param name="rootPos">Current world position of the root joint.</param>
        /// <param name="midPos">Current world position of the mid joint.</param>
        /// <param name="endPos">Current world position of the end effector.</param>
        /// <param name="target">Desired end-effector position.</param>
        /// <param name="poleTarget">Pole vector target (bend direction).</param>
        /// <returns>Solved positions: [0]=root (unchanged), [1]=mid, [2]=end.</returns>
        public static Vector3[] Solve(
            Vector3 rootPos, Vector3 midPos, Vector3 endPos,
            Vector3 target, Vector3 poleTarget)
        {
            // Bone lengths
            float lenA = Vector3.Distance(rootPos, midPos);
            float lenB = Vector3.Distance(midPos, endPos);

            // Desired distance from root to target
            float targetDist = Vector3.Distance(rootPos, target);

            // Clamp target distance to reachable range [|lenA - lenB|, lenA + lenB]
            float minDist = MathF.Abs(lenA - lenB) + Eps;
            float maxDist = lenA + lenB - Eps;
            float clampedDist = Math.Clamp(targetDist, minDist, maxDist);

            // Clamp the target position to the reachable sphere
            Vector3 clampedTarget = target;
            if (targetDist > Eps)
            {
                Vector3 dirToTarget = (target - rootPos) / targetDist;
                clampedTarget = rootPos + dirToTarget * clampedDist;
            }

            // --- Law of cosines ---
            // Angle at root (between root->target and root->mid)
            float cosA = (lenA * lenA + clampedDist * clampedDist - lenB * lenB)
                         / (2f * lenA * clampedDist);
            cosA = Math.Clamp(cosA, -1f, 1f);
            float angleA = MathF.Acos(cosA);

            // --- Compute mid joint position ---
            // Direction from root to (clamped) target
            Vector3 rootToTarget = clampedTarget - rootPos;
            float rttLen = rootToTarget.Length();
            if (rttLen < Eps)
            {
                // Target is at root; keep mid joint where it is
                return new[] { rootPos, midPos, clampedTarget };
            }
            Vector3 dir = rootToTarget / rttLen;

            // Project the pole vector onto the plane perpendicular to dir
            Vector3 rootToPole = poleTarget - rootPos;
            Vector3 poleProjected = rootToPole - dir * Vector3.Dot(rootToPole, dir);

            // If the pole is nearly parallel to the root-target line, use the
            // original mid joint direction as a fallback
            Vector3 bendDir;
            float poleLen = poleProjected.Length();
            if (poleLen > Eps)
            {
                bendDir = poleProjected / poleLen;
            }
            else
            {
                Vector3 origMidDir = midPos - rootPos;
                Vector3 origMidProj = origMidDir - dir * Vector3.Dot(origMidDir, dir);
                float origLen = origMidProj.Length();
                bendDir = origLen > Eps ? origMidProj / origLen : Vector3.UnitY;
            }

            // The mid joint is at distance lenA from root, at angleA from the
            // root-target direction, towards the bend direction.
            // Distance along the root-target line:
            float alongDist = lenA * MathF.Cos(angleA);
            // Perpendicular distance from the root-target line:
            float perpDist = lenA * MathF.Sin(angleA);

            Vector3 newMidPos = rootPos + dir * alongDist + bendDir * perpDist;

            // The end effector is at the clamped target
            Vector3 newEndPos = clampedTarget;

            return new[] { rootPos, newMidPos, newEndPos };
        }

        /// <summary>
        /// Solve and apply the result to a SkeletonPose in world space.
        /// Writes the solved mid-joint rotation and end-effector rotation
        /// back into the pose's local-space bone poses.
        /// </summary>
        public void Solve(SkeletonPose pose, Vector3 target, Matrix4x4 componentTransform)
        {
            if (pose == null || pose.Skeleton == null) return;
            if (Weight <= 0f) return;

            Skeleton skel = pose.Skeleton;

            // Get current world positions of the three bones
            Matrix4x4[] worldMats = skel.ComputeWorldMatrices(pose);
            if (RootBoneIndex >= worldMats.Length || MidBoneIndex >= worldMats.Length ||
                EndBoneIndex >= worldMats.Length)
                return;

            Vector3 rootPos = worldMats[RootBoneIndex].Translation;
            Vector3 midPos = worldMats[MidBoneIndex].Translation;
            Vector3 endPos = worldMats[EndBoneIndex].Translation;

            // Transform target to local space if needed (target is in world/component space)
            Vector3 localTarget = target;
            Vector3 localPole = PoleTarget;

            // Solve
            Vector3[] solved = Solve(rootPos, midPos, endPos, localTarget, localPole);

            // Blend by weight
            if (Weight < 1f)
            {
                solved[1] = Vector3.Lerp(midPos, solved[1], Weight);
                solved[2] = Vector3.Lerp(endPos, solved[2], Weight);
            }

            // Compute rotation for the mid bone: align (midPos->endPos) to (solvedMid->solvedEnd)
            Vector3 oldMidToEnd = endPos - midPos;
            Vector3 newMidToEnd = solved[2] - solved[1];
            float oldLen = oldMidToEnd.Length();
            if (oldLen > Eps)
            {
                oldMidToEnd /= oldLen;
                newMidToEnd = newMidToEnd.Length() > Eps
                    ? Vector3.Normalize(newMidToEnd) : oldMidToEnd;
            }

            // Compute rotation for the root bone: align (rootPos->midPos) to (solvedRoot->solvedMid)
            Vector3 oldRootToMid = midPos - rootPos;
            Vector3 newRootToMid = solved[1] - rootPos;
            float rtmLen = oldRootToMid.Length();
            if (rtmLen > Eps)
            {
                oldRootToMid /= rtmLen;
                newRootToMid = newRootToMid.Length() > Eps
                    ? Vector3.Normalize(newRootToMid) : oldRootToMid;
            }

            // Apply rotations to world transforms, then convert back to local
            ApplySolvedRotation(pose, skel, worldMats, RootBoneIndex, oldRootToMid, newRootToMid);
            // Recompute world matrices after root rotation
            worldMats = skel.ComputeWorldMatrices(pose);
            ApplySolvedRotation(pose, skel, worldMats, MidBoneIndex, oldMidToEnd, newMidToEnd);
        }

        /// <summary>
        /// Rotate a bone so that its world-space direction changes from
        /// oldDir to newDir, then convert the world rotation back to local
        /// space and write it into the pose.
        /// </summary>
        private static void ApplySolvedRotation(
            SkeletonPose pose, Skeleton skel, Matrix4x4[] worldMats,
            int boneIndex, Vector3 oldDir, Vector3 newDir)
        {
            float dot = Vector3.Dot(oldDir, newDir);
            dot = Math.Clamp(dot, -1f, 1f);

            if (dot > 0.99999f) return; // Already aligned

            Vector3 axis = Vector3.Cross(oldDir, newDir);
            float axisLen = axis.Length();
            if (axisLen < Eps) return;

            axis /= axisLen;
            float angle = MathF.Acos(dot);
            Quaternion worldDelta = Quaternion.CreateFromAxisAngle(axis, angle);

            // Get the bone's current world rotation
            Matrix4x4 worldMat = worldMats[boneIndex];
            Quaternion worldRot = Quaternion.CreateFromRotationMatrix(worldMat);

            // Apply delta in world space
            Quaternion newWorldRot = worldDelta * worldRot;

            // Convert to local: localRot = newWorldRot * inverse(parentWorldRot)
            int parentIdx = skel.Bones[boneIndex].ParentIndex;
            Quaternion parentWorldRot = Quaternion.Identity;
            if (parentIdx >= 0)
            {
                parentWorldRot = Quaternion.CreateFromRotationMatrix(worldMats[parentIdx]);
            }

            Quaternion localRot = newWorldRot * Quaternion.Inverse(parentWorldRot);
            pose.SetRotation(boneIndex, localRot);
        }
    }

    // ========================================================================
    // LookAtIKSolver - aim a bone chain at a target
    // ========================================================================

    /// <summary>
    /// Look-at IK solver. Rotates a chain of bones so that the final bone's
    /// forward direction points toward the target. Each bone's rotation is
    /// limited by a maximum angle, and the total correction is blended by weight.
    /// </summary>
    public class LookAtIKSolver
    {
        /// <summary>Chain of bone indices from root to tip (e.g. spine bones).</summary>
        public List<int> Chain { get; set; } = new List<int>();

        /// <summary>Target position in world space.</summary>
        public Vector3 Target { get; set; }

        /// <summary>Forward axis of the end bone (local space). Default is +Z.</summary>
        public Vector3 ForwardAxis { get; set; } = Vector3.UnitZ;

        /// <summary>Maximum rotation angle per bone (radians).</summary>
        public float MaxAnglePerBone { get; set; } = 0.5f;

        /// <summary>Blend weight [0..1].</summary>
        public float Weight { get; set; } = 1f;

        /// <summary>Chain space: World or Component.</summary>
        public bool UseComponentSpace { get; set; } = true;

        private const float Eps = 1e-6f;

        /// <summary>
        /// Solve the look-at IK. Rotates each bone in the chain towards the
        /// target, distributing the total rotation across the chain with
        /// per-bone angle limits.
        /// </summary>
        public void Solve(SkeletonPose pose, Matrix4x4 componentTransform)
        {
            if (pose == null || pose.Skeleton == null || Chain.Count == 0 || Weight <= 0f)
                return;

            Skeleton skel = pose.Skeleton;
            Matrix4x4[] worldMats = skel.ComputeWorldMatrices(pose);

            // Get the tip bone's world position and current forward direction
            int tipIndex = Chain[Chain.Count - 1];
            if (tipIndex >= worldMats.Length) return;

            Vector3 tipPos = worldMats[tipIndex].Translation;
            Quaternion tipWorldRot = Quaternion.CreateFromRotationMatrix(worldMats[tipIndex]);

            // Current forward direction in world space
            Vector3 currentForward = Vector3.Transform(ForwardAxis, tipWorldRot);

            // Desired direction: from tip to target
            Vector3 toTarget = Target - tipPos;
            float targetDist = toTarget.Length();
            if (targetDist < Eps) return;
            Vector3 desiredForward = toTarget / targetDist;

            // Total angle between current and desired forward
            float dot = Vector3.Dot(currentForward, desiredForward);
            dot = Math.Clamp(dot, -1f, 1f);
            float totalAngle = MathF.Acos(dot);

            if (totalAngle < Eps) return;

            // Rotation axis
            Vector3 axis = Vector3.Cross(currentForward, desiredForward);
            float axisLen = axis.Length();
            if (axisLen < Eps) return;
            axis /= axisLen;

            // Apply weight
            totalAngle *= Weight;

            // Distribute the rotation across the chain
            // Each bone gets an equal share, limited by MaxAnglePerBone
            float anglePerBone = totalAngle / Chain.Count;
            anglePerBone = Math.Min(anglePerBone, MaxAnglePerBone);

            // Apply rotation from root to tip
            // We rotate each bone by anglePerBone around the same axis
            for (int i = 0; i < Chain.Count; i++)
            {
                int boneIdx = Chain[i];
                if (boneIdx < 0 || boneIdx >= worldMats.Length) continue;

                // Create the delta rotation
                Quaternion deltaRot = Quaternion.CreateFromAxisAngle(axis, anglePerBone);

                // Get the bone's current world rotation
                Quaternion worldRot = Quaternion.CreateFromRotationMatrix(worldMats[boneIdx]);

                // Apply delta in world space (pre-multiply for world-space rotation)
                Quaternion newWorldRot = deltaRot * worldRot;

                // Convert to local space
                int parentIdx = skel.Bones[boneIdx].ParentIndex;
                Quaternion parentWorldRot = Quaternion.Identity;
                if (parentIdx >= 0 && parentIdx < worldMats.Length)
                {
                    parentWorldRot = Quaternion.CreateFromRotationMatrix(worldMats[parentIdx]);
                }

                Quaternion localRot = newWorldRot * Quaternion.Inverse(parentWorldRot);
                pose.SetRotation(boneIdx, localRot);

                // Recompute world matrices for subsequent bones
                worldMats = skel.ComputeWorldMatrices(pose);
            }
        }
    }

    // ========================================================================
    // FABRIKSolver - Forward And Backward Reaching Inverse Kinematics
    // ========================================================================

    /// <summary>
    /// FABRIK (Forward And Backward Reaching Inverse Kinematics) solver.
    ///
    /// Algorithm:
    ///   Backward pass: Set the end effector to the target, then iterate
    ///     backwards from the end to the root, adjusting each joint position
    ///     to maintain the original bone length.
    ///   Forward pass: Set the root to its original position, then iterate
    ///     forwards from the root to the end, adjusting each joint position
    ///     to maintain the original bone length.
    ///   Repeat until the end effector is within tolerance of the target
    ///   or the maximum iteration count is reached.
    /// </summary>
    public class FABRIKSolverReal
    {
        /// <summary>Chain of bone indices from root to end effector.</summary>
        public List<int> Chain { get; set; } = new List<int>();

        /// <summary>Target position for the end effector.</summary>
        public Vector3 Target { get; set; }

        /// <summary>Pole targets for each chain segment (optional, can be null).</summary>
        public List<Vector3> PoleTargets { get; set; } = new List<Vector3>();

        /// <summary>Maximum number of iterations.</summary>
        public int MaxIterations { get; set; } = 10;

        /// <summary>Convergence tolerance (distance from target).</summary>
        public float Tolerance { get; set; } = 0.001f;

        /// <summary>Blend weight [0..1].</summary>
        public float Weight { get; set; } = 1f;

        private const float Eps = 1e-8f;

        /// <summary>
        /// Solve FABRIK for the given chain positions and target.
        /// Returns the solved positions and convergence info.
        /// </summary>
        /// <param name="positions">Initial chain positions [root, ..., end].</param>
        /// <param name="target">Target for the end effector.</param>
        /// <param name="maxIterations">Maximum iterations.</param>
        /// <param name="tolerance">Convergence tolerance.</param>
        public static IKResult Solve(
            Vector3[] positions, Vector3 target,
            int maxIterations, float tolerance)
        {
            if (positions == null || positions.Length < 2)
                return new IKResult(positions, true, 0, 0f);

            int n = positions.Length;

            // Copy initial positions
            var chain = new Vector3[n];
            Array.Copy(positions, chain, n);

            // Store original bone lengths
            var lengths = new float[n - 1];
            float totalLength = 0f;
            for (int i = 0; i < n - 1; i++)
            {
                lengths[i] = Vector3.Distance(chain[i], chain[i + 1]);
                totalLength += lengths[i];
            }

            Vector3 root = chain[0];
            float rootToTargetDist = Vector3.Distance(root, target);

            // If target is unreachable, stretch the chain towards it
            if (rootToTargetDist > totalLength)
            {
                Vector3 dir = (target - root) / rootToTargetDist;
                for (int i = 0; i < n - 1; i++)
                {
                    chain[i + 1] = chain[i] + dir * lengths[i];
                }
                float directError = Vector3.Distance(chain[n - 1], target);
                return new IKResult(chain, false, 1, directError);
            }

            // Iterative FABRIK
            int iter;
            float error = float.MaxValue;
            for (iter = 0; iter < maxIterations; iter++)
            {
                // --- Backward pass: end to root ---
                chain[n - 1] = target;
                for (int i = n - 2; i >= 0; i--)
                {
                    Vector3 dir = chain[i] - chain[i + 1];
                    float len = dir.Length();
                    if (len > Eps)
                    {
                        dir /= len;
                        chain[i] = chain[i + 1] + dir * lengths[i];
                    }
                    else
                    {
                        chain[i] = chain[i + 1];
                    }
                }

                // --- Forward pass: root to end ---
                chain[0] = root;
                for (int i = 0; i < n - 1; i++)
                {
                    Vector3 dir = chain[i + 1] - chain[i];
                    float len = dir.Length();
                    if (len > Eps)
                    {
                        dir /= len;
                        chain[i + 1] = chain[i] + dir * lengths[i];
                    }
                    else
                    {
                        chain[i + 1] = chain[i];
                    }
                }

                // Check convergence
                error = Vector3.Distance(chain[n - 1], target);
                if (error < tolerance)
                    break;
            }

            bool converged = error < tolerance;
            return new IKResult(chain, converged, iter + 1, error);
        }

        /// <summary>
        /// Solve FABRIK and apply the result to a SkeletonPose. Converts the
        /// solved world-space positions back to local-space bone rotations.
        /// </summary>
        public void Solve(SkeletonPose pose, Matrix4x4 componentTransform)
        {
            if (pose == null || pose.Skeleton == null || Chain.Count < 2 || Weight <= 0f)
                return;

            Skeleton skel = pose.Skeleton;
            Matrix4x4[] worldMats = skel.ComputeWorldMatrices(pose);

            // Extract current chain positions
            var positions = new Vector3[Chain.Count];
            for (int i = 0; i < Chain.Count; i++)
            {
                if (Chain[i] >= worldMats.Length) return;
                positions[i] = worldMats[Chain[i]].Translation;
            }

            // Blend target by weight
            Vector3 effectiveTarget = Vector3.Lerp(positions[positions.Length - 1], Target, Weight);

            // Solve
            IKResult result = Solve(positions, effectiveTarget, MaxIterations, Tolerance);
            Vector3[] solved = result.Positions;

            // Apply pole targets if specified
            if (PoleTargets != null && PoleTargets.Count > 0)
            {
                ApplyPoleTargets(solved, positions, lengths =>
                {
                    var lens = new float[positions.Length - 1];
                    for (int i = 0; i < lens.Length; i++)
                        lens[i] = Vector3.Distance(positions[i], positions[i + 1]);
                    return lens;
                });
            }

            // Convert solved positions to local-space rotations
            // For each bone in the chain, compute the rotation that aligns
            // the old direction to the new direction
            for (int i = 0; i < Chain.Count - 1; i++)
            {
                int boneIdx = Chain[i];
                if (boneIdx < 0 || boneIdx >= worldMats.Length) continue;

                // Old direction: from this bone to the next
                Vector3 oldDir = positions[i + 1] - positions[i];
                // New direction: from solved position to next solved position
                Vector3 newDir = solved[i + 1] - solved[i];

                float oldLen = oldDir.Length();
                if (oldLen < Eps) continue;
                oldDir /= oldLen;

                float newLen = newDir.Length();
                if (newLen < Eps) continue;
                newDir /= newLen;

                // Compute rotation delta
                float dot = Math.Clamp(Vector3.Dot(oldDir, newDir), -1f, 1f);
                if (dot > 0.99999f) continue;

                Vector3 axis = Vector3.Cross(oldDir, newDir);
                float axisLen = axis.Length();
                if (axisLen < Eps) continue;
                axis /= axisLen;

                float angle = MathF.Acos(dot);
                Quaternion deltaRot = Quaternion.CreateFromAxisAngle(axis, angle);

                // Apply to world rotation
                Quaternion worldRot = Quaternion.CreateFromRotationMatrix(worldMats[boneIdx]);
                Quaternion newWorldRot = deltaRot * worldRot;

                // Convert to local
                int parentIdx = skel.Bones[boneIdx].ParentIndex;
                Quaternion parentWorldRot = Quaternion.Identity;
                if (parentIdx >= 0 && parentIdx < worldMats.Length)
                {
                    parentWorldRot = Quaternion.CreateFromRotationMatrix(worldMats[parentIdx]);
                }

                Quaternion localRot = newWorldRot * Quaternion.Inverse(parentWorldRot);
                pose.SetRotation(boneIdx, localRot);

                // Recompute world matrices for subsequent bones
                worldMats = skel.ComputeWorldMatrices(pose);
            }
        }

        /// <summary>
        /// Apply pole vector constraints to the solved chain. For each pole
        /// target, project the corresponding joint towards the pole vector
        /// while maintaining bone lengths.
        /// </summary>
        private void ApplyPoleTargets(Vector3[] solved, Vector3[] original,
            Func<float[], float[]> getLengths)
        {
            if (PoleTargets == null) return;
            int poleCount = Math.Min(PoleTargets.Count, solved.Length - 2);
            for (int p = 0; p < poleCount; p++)
            {
                Vector3 poleTarget = PoleTargets[p];
                int jointIdx = p + 1; // The joint to adjust
                if (jointIdx >= solved.Length - 1) break;

                Vector3 root = solved[jointIdx - 1];
                Vector3 end = solved[jointIdx + 1];

                // Direction from root to end
                Vector3 rootToEnd = end - root;
                float rteLen = rootToEnd.Length();
                if (rteLen < Eps) continue;

                Vector3 rootToEndDir = rootToEnd / rteLen;

                // Project pole target onto the plane perpendicular to rootToEnd
                Vector3 rootToPole = poleTarget - root;
                Vector3 poleProjected = rootToPole - rootToEndDir * Vector3.Dot(rootToPole, rootToEndDir);
                float poleLen = poleProjected.Length();
                if (poleLen < Eps) continue;
                Vector3 bendDir = poleProjected / poleLen;

                // Current joint position projected
                Vector3 rootToJoint = solved[jointIdx] - root;
                float alongDist = Vector3.Dot(rootToJoint, rootToEndDir);
                // Perpendicular component magnitude
                Vector3 perp = rootToJoint - rootToEndDir * alongDist;
                float perpMag = perp.Length();

                // Reposition joint: same along-distance, but perpendicular towards pole
                solved[jointIdx] = root + rootToEndDir * alongDist + bendDir * perpMag;
            }
        }
    }

    // ========================================================================
    // CCDIKSolver - Cyclic Coordinate Descent IK
    // ========================================================================

    /// <summary>
    /// CCD (Cyclic Coordinate Descent) IK solver.
    ///
    /// Algorithm:
    ///   For each iteration:
    ///     For each joint from the end-effector's parent back to the root:
    ///       1. Compute the vector from the joint to the end effector (v_ee)
    ///       2. Compute the vector from the joint to the target (v_target)
    ///       3. Compute the rotation that aligns v_ee to v_target
    ///       4. Apply this rotation to the joint and all downstream bones
    ///   Repeat until the end effector is within tolerance or max iterations.
    /// </summary>
    public class CCDIKSolverReal
    {
        /// <summary>Chain of bone indices from root to end effector.</summary>
        public List<int> Chain { get; set; } = new List<int>();

        /// <summary>Target position.</summary>
        public Vector3 Target { get; set; }

        /// <summary>Maximum iterations.</summary>
        public int MaxIterations { get; set; } = 15;

        /// <summary>Convergence tolerance.</summary>
        public float Tolerance { get; set; } = 0.001f;

        /// <summary>Maximum angle change per joint per iteration (radians). 0 = unlimited.</summary>
        public float MaxAngleChange { get; set; } = 0f;

        /// <summary>Blend weight.</summary>
        public float Weight { get; set; } = 1f;

        private const float Eps = 1e-8f;

        /// <summary>
        /// Solve CCD for the given chain positions and target.
        /// </summary>
        public static IKResult Solve(
            Vector3[] positions, Vector3 target,
            int maxIterations, float tolerance,
            float maxAngleChange = 0f)
        {
            if (positions == null || positions.Length < 2)
                return new IKResult(positions, true, 0, 0f);

            int n = positions.Length;
            var chain = new Vector3[n];
            Array.Copy(positions, chain, n);

            // Store bone lengths
            var lengths = new float[n - 1];
            for (int i = 0; i < n - 1; i++)
                lengths[i] = Vector3.Distance(chain[i], chain[i + 1]);

            float error = float.MaxValue;
            int iter;

            for (iter = 0; iter < maxIterations; iter++)
            {
                // Iterate from the second-to-last joint back to the root
                for (int i = n - 2; i >= 0; i--)
                {
                    // Vector from joint i to end effector
                    Vector3 vEE = chain[n - 1] - chain[i];
                    float eeLen = vEE.Length();
                    if (eeLen < Eps) continue;
                    vEE /= eeLen;

                    // Vector from joint i to target
                    Vector3 vTarget = target - chain[i];
                    float targetLen = vTarget.Length();
                    if (targetLen < Eps) continue;
                    vTarget /= targetLen;

                    // Compute rotation
                    float dot = Math.Clamp(Vector3.Dot(vEE, vTarget), -1f, 1f);
                    float angle = MathF.Acos(dot);

                    // Apply max angle change limit
                    if (maxAngleChange > 0f && angle > maxAngleChange)
                        angle = maxAngleChange;

                    if (angle < Eps) continue;

                    Vector3 axis = Vector3.Cross(vEE, vTarget);
                    float axisLen = axis.Length();
                    if (axisLen < Eps) continue;
                    axis /= axisLen;

                    // Rotate all downstream joints around joint i
                    Quaternion rot = Quaternion.CreateFromAxisAngle(axis, angle);
                    for (int j = i + 1; j < n; j++)
                    {
                        Vector3 relative = chain[j] - chain[i];
                        chain[j] = chain[i] + Vector3.Transform(relative, rot);
                    }
                }

                // Check convergence
                error = Vector3.Distance(chain[n - 1], target);
                if (error < tolerance)
                    break;
            }

            bool converged = error < tolerance;
            return new IKResult(chain, converged, iter + 1, error);
        }

        /// <summary>Solve CCD and apply to a SkeletonPose.</summary>
        public void Solve(SkeletonPose pose, Matrix4x4 componentTransform)
        {
            if (pose == null || pose.Skeleton == null || Chain.Count < 2 || Weight <= 0f)
                return;

            Skeleton skel = pose.Skeleton;
            Matrix4x4[] worldMats = skel.ComputeWorldMatrices(pose);

            var positions = new Vector3[Chain.Count];
            for (int i = 0; i < Chain.Count; i++)
            {
                if (Chain[i] >= worldMats.Length) return;
                positions[i] = worldMats[Chain[i]].Translation;
            }

            // Blend target by weight
            Vector3 effectiveTarget = Vector3.Lerp(positions[positions.Length - 1], Target, Weight);

            IKResult result = Solve(positions, effectiveTarget, MaxIterations, Tolerance, MaxAngleChange);
            Vector3[] solved = result.Positions;

            // Convert to local rotations
            for (int i = 0; i < Chain.Count - 1; i++)
            {
                int boneIdx = Chain[i];
                if (boneIdx < 0 || boneIdx >= worldMats.Length) continue;

                Vector3 oldDir = positions[i + 1] - positions[i];
                Vector3 newDir = solved[i + 1] - solved[i];

                float oldLen = oldDir.Length();
                float newLen = newDir.Length();
                if (oldLen < Eps || newLen < Eps) continue;

                oldDir /= oldLen;
                newDir /= newLen;

                float dot = Math.Clamp(Vector3.Dot(oldDir, newDir), -1f, 1f);
                if (dot > 0.99999f) continue;

                Vector3 axis = Vector3.Cross(oldDir, newDir);
                float axisLen = axis.Length();
                if (axisLen < Eps) continue;
                axis /= axisLen;

                float angle = MathF.Acos(dot);
                Quaternion deltaRot = Quaternion.CreateFromAxisAngle(axis, angle);

                Quaternion worldRot = Quaternion.CreateFromRotationMatrix(worldMats[boneIdx]);
                Quaternion newWorldRot = deltaRot * worldRot;

                int parentIdx = skel.Bones[boneIdx].ParentIndex;
                Quaternion parentWorldRot = Quaternion.Identity;
                if (parentIdx >= 0 && parentIdx < worldMats.Length)
                    parentWorldRot = Quaternion.CreateFromRotationMatrix(worldMats[parentIdx]);

                Quaternion localRot = newWorldRot * Quaternion.Inverse(parentWorldRot);
                pose.SetRotation(boneIdx, localRot);

                worldMats = skel.ComputeWorldMatrices(pose);
            }
        }
    }

    // ========================================================================
    // LimbIKSolver - two-bone IK with foot rotation correction
    // ========================================================================

    /// <summary>
    /// Limb IK solver: combines two-bone IK for the knee/elbow with a foot/hand
    /// rotation correction that aligns the end effector to a desired orientation.
    /// Typically used for legs (hip-knee-ankle + foot rotation).
    /// </summary>
    public class LimbIKSolver
    {
        /// <summary>Root bone (hip or shoulder).</summary>
        public int RootBoneIndex { get; set; }
        /// <summary>Mid bone (knee or elbow).</summary>
        public int MidBoneIndex { get; set; }
        /// <summary>End bone (ankle or wrist).</summary>
        public int EndBoneIndex { get; set; }

        /// <summary>IK target position for the end effector.</summary>
        public Vector3 TargetPosition { get; set; }
        /// <summary>Desired rotation for the end effector (foot/hand orientation).</summary>
        public Quaternion TargetRotation { get; set; } = Quaternion.Identity;

        /// <summary>Pole vector target for bend direction.</summary>
        public Vector3 PoleTarget { get; set; }

        /// <summary>Blend weight for position IK.</summary>
        public float PositionWeight { get; set; } = 1f;
        /// <summary>Blend weight for rotation correction.</summary>
        public float RotationWeight { get; set; } = 1f;

        private readonly TwoBoneIKSolverReal _twoBone = new TwoBoneIKSolverReal();

        /// <summary>Solve limb IK and apply to the pose.</summary>
        public void Solve(SkeletonPose pose, Matrix4x4 componentTransform)
        {
            if (pose == null || pose.Skeleton == null) return;
            if (PositionWeight <= 0f && RotationWeight <= 0f) return;

            Skeleton skel = pose.Skeleton;

            // Step 1: Two-bone IK for position
            if (PositionWeight > 0f)
            {
                _twoBone.RootBoneIndex = RootBoneIndex;
                _twoBone.MidBoneIndex = MidBoneIndex;
                _twoBone.EndBoneIndex = EndBoneIndex;
                _twoBone.PoleTarget = PoleTarget;
                _twoBone.Weight = PositionWeight;

                // We need to temporarily set Weight to 1 for the internal solve,
                // then blend manually. But the TwoBoneIKSolver already handles weight.
                _twoBone.Solve(pose, TargetPosition, componentTransform);
            }

            // Step 2: Foot/hand rotation correction
            if (RotationWeight > 0f)
            {
                Matrix4x4[] worldMats = skel.ComputeWorldMatrices(pose);
                if (EndBoneIndex >= worldMats.Length) return;

                // Current end bone world rotation
                Quaternion currentWorldRot = Quaternion.CreateFromRotationMatrix(worldMats[EndBoneIndex]);

                // Blend towards target rotation
                Quaternion blendedRot = KeyframeInterpolation.InterpolateQuaternionSlerp(
                    currentWorldRot, TargetRotation, RotationWeight);

                // Convert to local space
                int parentIdx = skel.Bones[EndBoneIndex].ParentIndex;
                Quaternion parentWorldRot = Quaternion.Identity;
                if (parentIdx >= 0 && parentIdx < worldMats.Length)
                {
                    parentWorldRot = Quaternion.CreateFromRotationMatrix(worldMats[parentIdx]);
                }

                Quaternion localRot = blendedRot * Quaternion.Inverse(parentWorldRot);
                pose.SetRotation(EndBoneIndex, localRot);
            }
        }
    }

    // ========================================================================
    // SpringIKSolver - spring-damper chain with length constraints
    // ========================================================================

    /// <summary>
    /// Spring IK solver. Models each bone segment as a spring-damper system.
    /// The end effector is pulled towards the target by a spring force, and
    /// the forces propagate through the chain. After integration, distance
    /// constraints are enforced to maintain bone lengths.
    ///
    /// This produces smoother, more organic motion than FABRIK or CCD,
    /// at the cost of slower convergence.
    /// </summary>
    public class SpringIKSolver
    {
        /// <summary>Chain of bone indices from root to end effector.</summary>
        public List<int> Chain { get; set; } = new List<int>();

        /// <summary>Target position for the end effector.</summary>
        public Vector3 Target { get; set; }

        /// <summary>Spring stiffness (force per unit distance).</summary>
        public float Stiffness { get; set; } = 500f;

        /// <summary>Damping coefficient (resistance to motion).</summary>
        public float Damping { get; set; } = 20f;

        /// <summary>Number of simulation substeps per solve.</summary>
        public int Substeps { get; set; } = 5;

        /// <summary>Simulation timestep.</summary>
        public float Dt { get; set; } = 0.016f;

        /// <summary>Maximum iterations for constraint satisfaction.</summary>
        public int MaxConstraintIterations { get; set; } = 5;

        /// <summary>Blend weight.</summary>
        public float Weight { get; set; } = 1f;

        // Runtime state: velocities for each joint
        private Vector3[] _velocities;

        private const float Eps = 1e-8f;

        /// <summary>
        /// Solve spring IK for the given chain and target.
        /// Uses Verlet-style integration with spring forces and distance constraints.
        /// </summary>
        public static IKResult Solve(
            Vector3[] positions, Vector3 target,
            float stiffness, float damping,
            int substeps, float dt,
            int maxConstraintIters)
        {
            if (positions == null || positions.Length < 2)
                return new IKResult(positions, true, 0, 0f);

            int n = positions.Length;
            var chain = new Vector3[n];
            Array.Copy(positions, chain, n);

            // Store rest lengths
            var restLengths = new float[n - 1];
            for (int i = 0; i < n - 1; i++)
                restLengths[i] = Vector3.Distance(chain[i], chain[i + 1]);

            // Velocities
            var velocities = new Vector3[n];
            Vector3 rootPos = chain[0];

            float subDt = dt / substeps;

            for (int step = 0; step < substeps; step++)
            {
                // --- Apply forces ---
                for (int i = 1; i < n; i++)
                {
                    Vector3 force = Vector3.Zero;

                    // Spring force towards the next joint (or target for the last)
                    if (i < n - 1)
                    {
                        Vector3 toNext = chain[i + 1] - chain[i];
                        float dist = toNext.Length();
                        if (dist > Eps)
                        {
                            float stretch = dist - restLengths[i];
                            force += toNext / dist * stretch * stiffness;
                        }
                    }
                    else
                    {
                        // End effector: spring force towards target
                        Vector3 toTarget = target - chain[i];
                        force += toTarget * stiffness;
                    }

                    // Spring force towards the previous joint
                    {
                        Vector3 toPrev = chain[i - 1] - chain[i];
                        float dist = toPrev.Length();
                        if (dist > Eps)
                        {
                            float stretch = dist - restLengths[i - 1];
                            force += toPrev / dist * stretch * stiffness;
                        }
                    }

                    // Damping
                    force -= velocities[i] * damping;

                    // Integrate (semi-implicit Euler)
                    velocities[i] += force * subDt;
                    chain[i] += velocities[i] * subDt;
                }

                // Pin the root
                chain[0] = rootPos;
                velocities[0] = Vector3.Zero;

                // --- Satisfy distance constraints (position-based dynamics) ---
                for (int iter = 0; iter < maxConstraintIters; iter++)
                {
                    for (int i = 0; i < n - 1; i++)
                    {
                        Vector3 delta = chain[i + 1] - chain[i];
                        float dist = delta.Length();
                        if (dist < Eps) continue;

                        float diff = (dist - restLengths[i]) / dist;
                        float w0 = (i == 0) ? 0f : 0.5f;  // Root is pinned
                        float w1 = 0.5f;
                        float wSum = w0 + w1;
                        if (wSum < Eps) continue;

                        chain[i] += delta * diff * (w0 / wSum);
                        chain[i + 1] -= delta * diff * (w1 / wSum);
                    }
                }
            }

            float error = Vector3.Distance(chain[n - 1], target);
            return new IKResult(chain, error < 0.01f, substeps, error);
        }

        /// <summary>Solve spring IK and apply to a SkeletonPose.</summary>
        public void Solve(SkeletonPose pose, Matrix4x4 componentTransform)
        {
            if (pose == null || pose.Skeleton == null || Chain.Count < 2 || Weight <= 0f)
                return;

            Skeleton skel = pose.Skeleton;
            Matrix4x4[] worldMats = skel.ComputeWorldMatrices(pose);

            int n = Chain.Count;
            var positions = new Vector3[n];
            for (int i = 0; i < n; i++)
            {
                if (Chain[i] >= worldMats.Length) return;
                positions[i] = worldMats[Chain[i]].Translation;
            }

            // Initialize velocities if needed
            if (_velocities == null || _velocities.Length != n)
                _velocities = new Vector3[n];

            // Blend target by weight
            Vector3 effectiveTarget = Vector3.Lerp(positions[n - 1], Target, Weight);

            IKResult result = Solve(positions, effectiveTarget,
                Stiffness, Damping, Substeps, Dt, MaxConstraintIterations);

            Vector3[] solved = result.Positions;

            // Convert to local rotations (same approach as FABRIK/CCD)
            for (int i = 0; i < n - 1; i++)
            {
                int boneIdx = Chain[i];
                if (boneIdx < 0 || boneIdx >= worldMats.Length) continue;

                Vector3 oldDir = positions[i + 1] - positions[i];
                Vector3 newDir = solved[i + 1] - solved[i];

                float oldLen = oldDir.Length();
                float newLen = newDir.Length();
                if (oldLen < Eps || newLen < Eps) continue;

                oldDir /= oldLen;
                newDir /= newLen;

                float dot = Math.Clamp(Vector3.Dot(oldDir, newDir), -1f, 1f);
                if (dot > 0.99999f) continue;

                Vector3 axis = Vector3.Cross(oldDir, newDir);
                float axisLen = axis.Length();
                if (axisLen < Eps) continue;
                axis /= axisLen;

                float angle = MathF.Acos(dot) * Weight;
                Quaternion deltaRot = Quaternion.CreateFromAxisAngle(axis, angle);

                Quaternion worldRot = Quaternion.CreateFromRotationMatrix(worldMats[boneIdx]);
                Quaternion newWorldRot = deltaRot * worldRot;

                int parentIdx = skel.Bones[boneIdx].ParentIndex;
                Quaternion parentWorldRot = Quaternion.Identity;
                if (parentIdx >= 0 && parentIdx < worldMats.Length)
                    parentWorldRot = Quaternion.CreateFromRotationMatrix(worldMats[parentIdx]);

                Quaternion localRot = newWorldRot * Quaternion.Inverse(parentWorldRot);
                pose.SetRotation(boneIdx, localRot);

                worldMats = skel.ComputeWorldMatrices(pose);
            }
        }

        /// <summary>Reset the solver's internal velocity state.</summary>
        public void Reset()
        {
            if (_velocities != null)
                Array.Clear(_velocities, 0, _velocities.Length);
        }
    }

    // ========================================================================
    // IKSolverUtilities - shared helper functions
    // ========================================================================

    /// <summary>
    /// Utility functions shared across IK solvers.
    /// </summary>
    public static class IKSolverUtilities
    {
        /// <summary>
        /// Compute the rotation that aligns direction 'from' to direction 'to'.
        /// Returns Quaternion.Identity if either direction is near zero or
        /// they are already aligned.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Quaternion AlignDirections(Vector3 from, Vector3 to)
        {
            float fromLen = from.Length();
            float toLen = to.Length();
            if (fromLen < 1e-8f || toLen < 1e-8f)
                return Quaternion.Identity;

            Vector3 f = from / fromLen;
            Vector3 t = to / toLen;

            float dot = Math.Clamp(Vector3.Dot(f, t), -1f, 1f);
            if (dot > 0.99999f)
                return Quaternion.Identity;
            if (dot < -0.99999f)
            {
                // 180-degree rotation: find any perpendicular axis
                Vector3 axis = MathF.Abs(f.X) < 0.9f
                    ? Vector3.Cross(f, Vector3.UnitX)
                    : Vector3.Cross(f, Vector3.UnitY);
                float axisLen = axis.Length();
                if (axisLen > 1e-8f)
                {
                    axis /= axisLen;
                    return Quaternion.CreateFromAxisAngle(axis, MathF.PI);
                }
                return Quaternion.Identity;
            }

            Vector3 cross = Vector3.Cross(f, t);
            float crossLen = cross.Length();
            if (crossLen < 1e-8f)
                return Quaternion.Identity;

            cross /= crossLen;
            float angle = MathF.Acos(dot);
            return Quaternion.CreateFromAxisAngle(cross, angle);
        }

        /// <summary>
        /// Clamp a target position to the reachable sphere of a two-bone chain.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector3 ClampToReachable(
            Vector3 root, Vector3 target, float lenA, float lenB, float eps = 1e-6f)
        {
            float maxReach = lenA + lenB - eps;
            float minReach = MathF.Abs(lenA - lenB) + eps;

            Vector3 toTarget = target - root;
            float dist = toTarget.Length();

            if (dist < Eps)
                return root + toTarget; // at root, return as-is

            if (dist > maxReach)
                return root + toTarget / dist * maxReach;

            if (dist < minReach)
                return root + toTarget / dist * minReach;

            return target;
        }

        private const float Eps = 1e-8f;

        /// <summary>
        /// Convert a world-space rotation to a local-space rotation given
        /// the parent's world rotation.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Quaternion WorldToLocal(Quaternion worldRot, Quaternion parentWorldRot)
        {
            return worldRot * Quaternion.Inverse(parentWorldRot);
        }

        /// <summary>
        /// Convert a local-space rotation to a world-space rotation given
        /// the parent's world rotation.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Quaternion LocalToWorld(Quaternion localRot, Quaternion parentWorldRot)
        {
            return localRot * parentWorldRot;
        }
    }
}
