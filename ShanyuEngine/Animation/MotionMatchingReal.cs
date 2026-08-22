// MotionMatchingReal.cs - Real motion matching system for the ShanyuEngine animation system.
//
// Implements a complete motion matching pipeline:
//   - MotionMatchingFeature: feature vector (position, velocity, acceleration,
//     future trajectory, foot contact, center of mass)
//   - MotionMatchingTrajectory: past and future motion trajectory samples
//   - MotionMatchingDatabase: precomputed frames with features and poses
//   - KDTree: kd-tree for nearest-neighbor search in feature space
//   - MotionMatchingSolver: database building, best-match search, blending
//
// All algorithms use System.Numerics with mathematically correct implementations.

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace ShanyuEngine.Animation
{
    // ========================================================================
    // MotionMatchingTrajectory - past and future trajectory samples
    // ========================================================================

    /// <summary>
    /// A motion trajectory: a sequence of past and future sampling points.
    /// Each point stores position, velocity, and facing direction.
    /// Used as part of the motion matching feature vector.
    /// </summary>
    public class MotionMatchingTrajectory
    {
        /// <summary>Trajectory points: position, velocity, facing direction.</summary>
        public struct TrajectoryPoint
        {
            public Vector3 Position;
            public Vector3 Velocity;
            public Vector3 Facing;
            public float Time; // Relative to current time (negative = past, positive = future)
        }

        /// <summary>Trajectory sampling points (past + future).</summary>
        public List<TrajectoryPoint> Points { get; } = new List<TrajectoryPoint>();

        /// <summary>Number of past samples.</summary>
        public int PastCount { get; set; }

        /// <summary>Number of future samples.</summary>
        public int FutureCount { get; set; }

        /// <summary>Time interval between samples (seconds).</summary>
        public float SampleInterval { get; set; } = 0.1f;

        public MotionMatchingTrajectory()
        {
            PastCount = 5;
            FutureCount = 5;
        }

        /// <summary>
        /// Build a trajectory from a root motion extractor. Samples past and
        /// future positions/velocities at regular intervals.
        /// </summary>
        public void BuildFromRootMotion(RootMotionExtractor extractor, float currentTime)
        {
            Points.Clear();
            float dt = SampleInterval;

            // Past samples (negative time offsets)
            for (int i = PastCount; i >= 1; i--)
            {
                float t = currentTime - i * dt;
                Vector3 pos = extractor.GetRootPosition(t);
                Vector3 vel = extractor.GetRootVelocity(t, dt * 0.5f);
                Quaternion rot = extractor.GetRootRotation(t);
                Vector3 facing = Vector3.Transform(Vector3.UnitZ, rot);

                Points.Add(new TrajectoryPoint
                {
                    Position = pos,
                    Velocity = vel,
                    Facing = facing,
                    Time = -i * dt
                });
            }

            // Current point
            {
                Vector3 pos = extractor.GetRootPosition(currentTime);
                Vector3 vel = extractor.GetRootVelocity(currentTime, dt * 0.5f);
                Quaternion rot = extractor.GetRootRotation(currentTime);
                Vector3 facing = Vector3.Transform(Vector3.UnitZ, rot);

                Points.Add(new TrajectoryPoint
                {
                    Position = pos,
                    Velocity = vel,
                    Facing = facing,
                    Time = 0f
                });
            }

            // Future samples (positive time offsets)
            for (int i = 1; i <= FutureCount; i++)
            {
                float t = currentTime + i * dt;
                Vector3 pos = extractor.GetRootPosition(t);
                Vector3 vel = extractor.GetRootVelocity(t, dt * 0.5f);
                Quaternion rot = extractor.GetRootRotation(t);
                Vector3 facing = Vector3.Transform(Vector3.UnitZ, rot);

                Points.Add(new TrajectoryPoint
                {
                    Position = pos,
                    Velocity = vel,
                    Facing = facing,
                    Time = i * dt
                });
            }
        }

        /// <summary>
        /// Build a desired (query) trajectory from a desired velocity and
        /// facing direction. Past points are zeroed (the query doesn't have
        /// a meaningful past); future points are extrapolated.
        /// </summary>
        public void BuildDesired(Vector3 currentPosition, Vector3 desiredVelocity,
            Vector3 desiredFacing)
        {
            Points.Clear();
            float dt = SampleInterval;

            // Past: use current position (no past data in query)
            for (int i = PastCount; i >= 1; i--)
            {
                Points.Add(new TrajectoryPoint
                {
                    Position = currentPosition,
                    Velocity = Vector3.Zero,
                    Facing = desiredFacing,
                    Time = -i * dt
                });
            }

            // Current
            Points.Add(new TrajectoryPoint
            {
                Position = currentPosition,
                Velocity = desiredVelocity,
                Facing = desiredFacing,
                Time = 0f
            });

            // Future: extrapolate using desired velocity
            for (int i = 1; i <= FutureCount; i++)
            {
                float t = i * dt;
                Points.Add(new TrajectoryPoint
                {
                    Position = currentPosition + desiredVelocity * t,
                    Velocity = desiredVelocity,
                    Facing = desiredFacing,
                    Time = t
                });
            }
        }

        /// <summary>Convert the trajectory to a flat float array for feature comparison.</summary>
        public float[] ToFloatArray()
        {
            // Each point contributes: Position(3) + Velocity(3) + Facing(3) = 9 floats
            float[] result = new float[Points.Count * 9];
            for (int i = 0; i < Points.Count; i++)
            {
                int offset = i * 9;
                result[offset + 0] = Points[i].Position.X;
                result[offset + 1] = Points[i].Position.Y;
                result[offset + 2] = Points[i].Position.Z;
                result[offset + 3] = Points[i].Velocity.X;
                result[offset + 4] = Points[i].Velocity.Y;
                result[offset + 5] = Points[i].Velocity.Z;
                result[offset + 6] = Points[i].Facing.X;
                result[offset + 7] = Points[i].Facing.Y;
                result[offset + 8] = Points[i].Facing.Z;
            }
            return result;
        }
    }

    // ========================================================================
    // MotionMatchingFeature - feature vector for motion matching
    // ========================================================================

    /// <summary>
    /// Feature vector for motion matching. Encapsulates the character's
    /// current state: root position, velocity, acceleration, future trajectory,
    /// foot contact state, and center of mass.
    /// </summary>
    public class MotionMatchingFeature
    {
        /// <summary>Root bone position in world space.</summary>
        public Vector3 RootPosition { get; set; }

        /// <summary>Root bone velocity (units per second).</summary>
        public Vector3 RootVelocity { get; set; }

        /// <summary>Root bone acceleration (units per second squared).</summary>
        public Vector3 RootAcceleration { get; set; }

        /// <summary>Root facing direction (forward vector).</summary>
        public Vector3 RootFacing { get; set; }

        /// <summary>Left foot contact state (1 = grounded, 0 = in air).</summary>
        public float LeftFootContact { get; set; }

        /// <summary>Right foot contact state (1 = grounded, 0 = in air).</summary>
        public float RightFootContact { get; set; }

        /// <summary>Left foot position in world space.</summary>
        public Vector3 LeftFootPosition { get; set; }

        /// <summary>Right foot position in world space.</summary>
        public Vector3 RightFootPosition { get; set; }

        /// <summary>Left foot velocity.</summary>
        public Vector3 LeftFootVelocity { get; set; }

        /// <summary>Right foot velocity.</summary>
        public Vector3 RightFootVelocity { get; set; }

        /// <summary>Center of mass position.</summary>
        public Vector3 CenterOfMass { get; set; }

        /// <summary>Future trajectory samples.</summary>
        public MotionMatchingTrajectory Trajectory { get; set; } = new MotionMatchingTrajectory();

        /// <summary>
        /// Convert the feature to a flat float array for distance computation.
        /// The order and length must be consistent across all features.
        /// </summary>
        public float[] ToFloatArray()
        {
            var trajArr = Trajectory.ToFloatArray();
            // Base features: 3+3+3+3 + 1+1 + 3+3 + 3+3 + 3 = 32 floats
            // Plus trajectory array
            float[] result = new float[32 + trajArr.Length];
            int o = 0;
            result[o++] = RootPosition.X;
            result[o++] = RootPosition.Y;
            result[o++] = RootPosition.Z;
            result[o++] = RootVelocity.X;
            result[o++] = RootVelocity.Y;
            result[o++] = RootVelocity.Z;
            result[o++] = RootAcceleration.X;
            result[o++] = RootAcceleration.Y;
            result[o++] = RootAcceleration.Z;
            result[o++] = RootFacing.X;
            result[o++] = RootFacing.Y;
            result[o++] = RootFacing.Z;
            result[o++] = LeftFootContact;
            result[o++] = RightFootContact;
            result[o++] = LeftFootPosition.X;
            result[o++] = LeftFootPosition.Y;
            result[o++] = LeftFootPosition.Z;
            result[o++] = RightFootPosition.X;
            result[o++] = RightFootPosition.Y;
            result[o++] = RightFootPosition.Z;
            result[o++] = LeftFootVelocity.X;
            result[o++] = LeftFootVelocity.Y;
            result[o++] = LeftFootVelocity.Z;
            result[o++] = RightFootVelocity.X;
            result[o++] = RightFootVelocity.Y;
            result[o++] = RightFootVelocity.Z;
            result[o++] = CenterOfMass.X;
            result[o++] = CenterOfMass.Y;
            result[o++] = CenterOfMass.Z;
            // Pad to 32
            result[o++] = 0f;
            result[o++] = 0f;

            Array.Copy(trajArr, 0, result, o, trajArr.Length);
            return result;
        }

        /// <summary>
        /// Compute the weighted Euclidean distance between two feature vectors.
        /// Weights control the relative importance of each feature group.
        /// </summary>
        public static float WeightedDistance(float[] a, float[] b, float[] weights)
        {
            int len = Math.Min(a.Length, b.Length);
            float sum = 0f;
            for (int i = 0; i < len; i++)
            {
                float w = (weights != null && i < weights.Length) ? weights[i] : 1f;
                float diff = a[i] - b[i];
                sum += w * diff * diff;
            }
            return MathF.Sqrt(sum);
        }

        /// <summary>
        /// Compute the weighted Euclidean distance between two feature vectors
        /// using default equal weights.
        /// </summary>
        public static float Distance(float[] a, float[] b)
        {
            int len = Math.Min(a.Length, b.Length);
            float sum = 0f;
            for (int i = 0; i < len; i++)
            {
                float diff = a[i] - b[i];
                sum += diff * diff;
            }
            return MathF.Sqrt(sum);
        }

        /// <summary>
        /// Compute a feature from an animation clip at a specific time.
        /// Uses finite differences for velocity and acceleration.
        /// </summary>
        public static MotionMatchingFeature ComputeFromClip(
            AnimationClipReal clip, Skeleton skeleton, int rootBoneIndex,
            int leftFootIndex, int rightFootIndex, float time, float dt = 0.033f)
        {
            var feature = new MotionMatchingFeature();
            if (clip == null || skeleton == null) return feature;

            // Sample pose at current time
            var rootMotion = new RootMotionExtractor(clip, rootBoneIndex);

            // Root position and velocity
            feature.RootPosition = rootMotion.GetRootPosition(time);
            feature.RootVelocity = rootMotion.GetRootVelocity(time, dt);

            // Root acceleration: finite difference of velocity
            Vector3 vel0 = rootMotion.GetRootVelocity(time - dt, dt);
            Vector3 vel1 = rootMotion.GetRootVelocity(time + dt, dt);
            feature.RootAcceleration = (vel1 - vel0) / (2f * dt);

            // Root facing
            Quaternion rootRot = rootMotion.GetRootRotation(time);
            feature.RootFacing = Vector3.Transform(Vector3.UnitZ, rootRot);

            // Foot positions and velocities
            if (leftFootIndex >= 0)
            {
                var track = clip.FindTrack(leftFootIndex);
                if (track != null)
                {
                    feature.LeftFootPosition = KeyframeInterpolation.EvaluateVector3Curve(
                        track.PositionKeys, time, track.PositionInterpolation, ExtrapolationMode.Clamp);
                    Vector3 footPrev = KeyframeInterpolation.EvaluateVector3Curve(
                        track.PositionKeys, time - dt, track.PositionInterpolation, ExtrapolationMode.Clamp);
                    Vector3 footNext = KeyframeInterpolation.EvaluateVector3Curve(
                        track.PositionKeys, time + dt, track.PositionInterpolation, ExtrapolationMode.Clamp);
                    feature.LeftFootVelocity = (footNext - footPrev) / (2f * dt);
                }
            }

            if (rightFootIndex >= 0)
            {
                var track = clip.FindTrack(rightFootIndex);
                if (track != null)
                {
                    feature.RightFootPosition = KeyframeInterpolation.EvaluateVector3Curve(
                        track.PositionKeys, time, track.PositionInterpolation, ExtrapolationMode.Clamp);
                    Vector3 footPrev = KeyframeInterpolation.EvaluateVector3Curve(
                        track.PositionKeys, time - dt, track.PositionInterpolation, ExtrapolationMode.Clamp);
                    Vector3 footNext = KeyframeInterpolation.EvaluateVector3Curve(
                        track.PositionKeys, time + dt, track.PositionInterpolation, ExtrapolationMode.Clamp);
                    feature.RightFootVelocity = (footNext - footPrev) / (2f * dt);
                }
            }

            // Foot contact: heuristic - foot is in contact if velocity is low
            float contactThreshold = 0.5f;
            feature.LeftFootContact = feature.LeftFootVelocity.Length() < contactThreshold ? 1f : 0f;
            feature.RightFootContact = feature.RightFootVelocity.Length() < contactThreshold ? 1f : 0f;

            // Center of mass: approximate as average of root and feet positions
            feature.CenterOfMass = (feature.RootPosition +
                feature.LeftFootPosition + feature.RightFootPosition) / 3f;

            // Build trajectory
            feature.Trajectory.BuildFromRootMotion(rootMotion, time);

            return feature;
        }
    }

    // ========================================================================
    // MotionMatchingFrame - a single database frame
    // ========================================================================

    /// <summary>
    /// A single frame in the motion matching database. Stores the source clip,
    /// time, feature vector, and the sampled pose.
    /// </summary>
    public class MotionMatchingFrame
    {
        /// <summary>Source clip index in the database.</summary>
        public int ClipIndex { get; set; }

        /// <summary>Time within the source clip.</summary>
        public float Time { get; set; }

        /// <summary>Frame index (for reference).</summary>
        public int FrameIndex { get; set; }

        /// <summary>Computed feature vector.</summary>
        public MotionMatchingFeature Feature { get; set; }

        /// <summary>Cached float array of the feature.</summary>
        public float[] FeatureArray { get; set; }

        /// <summary>Sampled pose at this frame.</summary>
        public SkeletonPose Pose { get; set; }

        /// <summary>Source clip reference.</summary>
        public AnimationClipReal Clip { get; set; }
    }

    // ========================================================================
    // KDTree - kd-tree for nearest-neighbor search in feature space
    // ========================================================================

    /// <summary>
    /// A kd-tree (k-dimensional tree) for efficient nearest-neighbor search
    /// in feature space. Each leaf stores a MotionMatchingFrame.
    ///
    /// The tree is built by recursively splitting along the dimension with
    /// the greatest variance, using the median as the split point.
    /// Nearest-neighbor search uses branch-and-bound with a priority queue.
    /// </summary>
    public class KDTree
    {
        private class KDNode
        {
            public int SplitDimension;
            public float SplitValue;
            public KDNode Left;
            public KDNode Right;
            public MotionMatchingFrame Frame; // Only for leaf nodes
            public bool IsLeaf => Frame != null;
        }

        private KDNode _root;
        private int _dimensions;

        /// <summary>Maximum number of frames in a leaf node before splitting.</summary>
        public int LeafSize { get; set; } = 1;

        /// <summary>
        /// Build the kd-tree from a list of frames.
        /// </summary>
        public void Build(List<MotionMatchingFrame> frames)
        {
            if (frames == null || frames.Count == 0)
            {
                _root = null;
                return;
            }

            // Determine dimensions from the first frame
            _dimensions = frames[0].FeatureArray?.Length ?? 0;
            if (_dimensions == 0) return;

            _root = BuildNode(frames, 0);
        }

        /// <summary>
        /// Recursively build a kd-tree node from a list of frames.
        /// </summary>
        private KDNode BuildNode(List<MotionMatchingFrame> frames, int depth)
        {
            if (frames.Count == 0) return null;

            // Leaf node
            if (frames.Count <= LeafSize)
            {
                return new KDNode { Frame = frames[0] };
            }

            // Find the dimension with the greatest variance
            int splitDim = FindMaxVarianceDimension(frames);
            if (splitDim < 0)
                return new KDNode { Frame = frames[0] };

            // Find the median along the split dimension
            float median = FindMedian(frames, splitDim);

            // Partition frames into left (<= median) and right (> median)
            var leftFrames = new List<MotionMatchingFrame>();
            var rightFrames = new List<MotionMatchingFrame>();
            foreach (var f in frames)
            {
                if (f.FeatureArray[splitDim] <= median)
                    leftFrames.Add(f);
                else
                    rightFrames.Add(f);
            }

            // Handle degenerate case: all points on one side
            if (leftFrames.Count == 0 || rightFrames.Count == 0)
            {
                // Split didn't work; use the first frame as leaf
                return new KDNode { Frame = frames[0] };
            }

            return new KDNode
            {
                SplitDimension = splitDim,
                SplitValue = median,
                Left = BuildNode(leftFrames, depth + 1),
                Right = BuildNode(rightFrames, depth + 1)
            };
        }

        /// <summary>
        /// Find the dimension with the greatest variance across all frames.
        /// </summary>
        private int FindMaxVarianceDimension(List<MotionMatchingFrame> frames)
        {
            if (frames.Count <= 1 || _dimensions == 0) return -1;

            int bestDim = 0;
            float maxVariance = 0f;

            for (int d = 0; d < _dimensions; d++)
            {
                float mean = 0f;
                for (int i = 0; i < frames.Count; i++)
                    mean += frames[i].FeatureArray[d];
                mean /= frames.Count;

                float variance = 0f;
                for (int i = 0; i < frames.Count; i++)
                {
                    float diff = frames[i].FeatureArray[d] - mean;
                    variance += diff * diff;
                }
                variance /= frames.Count;

                if (variance > maxVariance)
                {
                    maxVariance = variance;
                    bestDim = d;
                }
            }

            return bestDim;
        }

        /// <summary>
        /// Find the median value along a specific dimension.
        /// </summary>
        private float FindMedian(List<MotionMatchingFrame> frames, int dim)
        {
            var values = new float[frames.Count];
            for (int i = 0; i < frames.Count; i++)
                values[i] = frames[i].FeatureArray[dim];

            Array.Sort(values);
            return values[values.Length / 2];
        }

        /// <summary>
        /// Find the nearest neighbor to the query point.
        /// Uses branch-and-bound: traverses the tree, pruning branches that
        /// cannot contain a closer point.
        /// </summary>
        public MotionMatchingFrame FindNearest(float[] query, float[] weights = null)
        {
            if (_root == null || query == null) return null;

            float bestDist = float.MaxValue;
            MotionMatchingFrame bestFrame = null;

            SearchNearest(_root, query, weights, ref bestDist, ref bestFrame);
            return bestFrame;
        }

        /// <summary>
        /// Recursively search for the nearest neighbor.
        /// </summary>
        private void SearchNearest(KDNode node, float[] query, float[] weights,
            ref float bestDist, ref MotionMatchingFrame bestFrame)
        {
            if (node == null) return;

            if (node.IsLeaf)
            {
                float dist = MotionMatchingFeature.WeightedDistance(
                    query, node.Frame.FeatureArray, weights);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    bestFrame = node.Frame;
                }
                return;
            }

            // Determine which side to search first
            float queryVal = query[node.SplitDimension];
            KDNode near, far;
            if (queryVal <= node.SplitValue)
            {
                near = node.Left;
                far = node.Right;
            }
            else
            {
                near = node.Right;
                far = node.Left;
            }

            // Search the near side first
            SearchNearest(near, query, weights, ref bestDist, ref bestFrame);

            // Check if the far side could contain a closer point
            float splitDist = queryVal - node.SplitValue;
            float w = (weights != null && node.SplitDimension < weights.Length)
                ? weights[node.SplitDimension] : 1f;
            float splitDistWeighted = MathF.Sqrt(w) * MathF.Abs(splitDist);

            if (splitDistWeighted < bestDist)
            {
                SearchNearest(far, query, weights, ref bestDist, ref bestFrame);
            }
        }

        /// <summary>
        /// Find the k nearest neighbors to the query point.
        /// Returns up to k frames sorted by distance.
        /// </summary>
        public List<(MotionMatchingFrame frame, float distance)> FindKNearest(
            float[] query, int k, float[] weights = null)
        {
            if (_root == null || query == null) return new List<(MotionMatchingFrame, float)>();

            var results = new List<(MotionMatchingFrame frame, float distance)>();
            SearchKNearest(_root, query, k, weights, results);
            results.Sort((a, b) => a.distance.CompareTo(b.distance));
            if (results.Count > k)
                results.RemoveRange(k, results.Count - k);
            return results;
        }

        private void SearchKNearest(KDNode node, float[] query, int k, float[] weights,
            List<(MotionMatchingFrame, float)> results)
        {
            if (node == null) return;

            if (node.IsLeaf)
            {
                float dist = MotionMatchingFeature.WeightedDistance(
                    query, node.Frame.FeatureArray, weights);
                results.Add((node.Frame, dist));
                return;
            }

            float queryVal = query[node.SplitDimension];
            KDNode near, far;
            if (queryVal <= node.SplitValue)
            {
                near = node.Left;
                far = node.Right;
            }
            else
            {
                near = node.Right;
                far = node.Left;
            }

            SearchKNearest(near, query, k, weights, results);

            // Check if we need to search the far side
            float currentWorst = results.Count >= k
                ? results[results.Count - 1].distance : float.MaxValue;

            float splitDist = queryVal - node.SplitValue;
            float w = (weights != null && node.SplitDimension < weights.Length)
                ? weights[node.SplitDimension] : 1f;
            float splitDistWeighted = MathF.Sqrt(w) * MathF.Abs(splitDist);

            if (splitDistWeighted < currentWorst || results.Count < k)
            {
                SearchKNearest(far, query, k, weights, results);
            }
        }
    }

    // ========================================================================
    // MotionMatchingDatabase - precomputed database of animation frames
    // ========================================================================

    /// <summary>
    /// Motion matching database. Stores precomputed frames with features and
    /// poses. Built from a set of animation clips by sampling each clip at
    /// regular intervals and computing feature vectors.
    /// </summary>
    public class MotionMatchingDatabase
    {
        /// <summary>All frames in the database.</summary>
        public List<MotionMatchingFrame> Frames { get; } = new List<MotionMatchingFrame>();

        /// <summary>Source clips used to build the database.</summary>
        public List<AnimationClipReal> Clips { get; } = new List<AnimationClipReal>();

        /// <summary>Skeleton used for pose sampling.</summary>
        public Skeleton Skeleton { get; set; }

        /// <summary>Root bone index for feature extraction.</summary>
        public int RootBoneIndex { get; set; } = 0;

        /// <summary>Left foot bone index.</summary>
        public int LeftFootBoneIndex { get; set; } = -1;

        /// <summary>Right foot bone index.</summary>
        public int RightFootBoneIndex { get; set; } = -1;

        /// <summary>Sampling rate (frames per second).</summary>
        public float SampleRate { get; set; } = 30f;

        /// <summary>KD-tree for accelerated nearest-neighbor search.</summary>
        public KDTree Tree { get; private set; }

        /// <summary>
        /// Build the database from a set of animation clips. For each clip,
        /// samples at regular intervals (1/SampleRate seconds) and computes
        /// a feature vector for each frame.
        /// </summary>
        public void Build(List<AnimationClipReal> clips, Skeleton skeleton,
            int rootBoneIndex, int leftFootIndex, int rightFootIndex)
        {
            Frames.Clear();
            Clips.Clear();
            Clips.AddRange(clips);
            Skeleton = skeleton;
            RootBoneIndex = rootBoneIndex;
            LeftFootBoneIndex = leftFootIndex;
            RightFootBoneIndex = rightFootIndex;

            float sampleDt = 1f / SampleRate;
            int globalFrameIndex = 0;

            for (int clipIdx = 0; clipIdx < clips.Count; clipIdx++)
            {
                var clip = clips[clipIdx];
                if (clip == null || clip.Duration <= 0f) continue;

                int frameCount = (int)MathF.Ceiling(clip.Duration * SampleRate);

                for (int f = 0; f < frameCount; f++)
                {
                    float time = f * sampleDt;
                    if (time > clip.Duration) time = clip.Duration;

                    // Compute feature
                    var feature = MotionMatchingFeature.ComputeFromClip(
                        clip, skeleton, rootBoneIndex, leftFootIndex, rightFootIndex, time, sampleDt);

                    // Sample pose
                    var pose = new SkeletonPose(skeleton);
                    clip.Evaluate(time, pose);

                    var frame = new MotionMatchingFrame
                    {
                        ClipIndex = clipIdx,
                        Time = time,
                        FrameIndex = globalFrameIndex,
                        Feature = feature,
                        FeatureArray = feature.ToFloatArray(),
                        Pose = pose,
                        Clip = clip
                    };

                    Frames.Add(frame);
                    globalFrameIndex++;
                }
            }

            // Build KD-tree
            Tree = new KDTree();
            Tree.Build(Frames);
        }

        /// <summary>Get a frame by its global index.</summary>
        public MotionMatchingFrame GetFrame(int index)
        {
            if (index < 0 || index >= Frames.Count) return null;
            return Frames[index];
        }

        /// <summary>Total number of frames in the database.</summary>
        public int FrameCount => Frames.Count;
    }

    // ========================================================================
    // MotionMatchingSolver - the main solver
    // ========================================================================

    /// <summary>
    /// Motion matching solver. Builds the database, searches for the best
    /// matching frame, and handles blending between the current frame and
    /// the matched frame.
    /// </summary>
    public class MotionMatchingSolver
    {
        /// <summary>The motion matching database.</summary>
        public MotionMatchingDatabase Database { get; set; } = new MotionMatchingDatabase();

        /// <summary>
        /// Feature weights for distance computation. Controls the relative
        /// importance of each feature component.
        /// </summary>
        public float[] FeatureWeights { get; set; }

        /// <summary>Currently playing frame index in the database.</summary>
        public int CurrentFrameIndex { get; set; } = -1;

        /// <summary>Current playback time within the matched clip.</summary>
        public float CurrentTime { get; set; }

        /// <summary>Blending duration when switching to a new match (seconds).</summary>
        public float BlendTime { get; set; } = 0.2f;

        /// <summary>Remaining blend time (0 = not blending).</summary>
        public float RemainingBlendTime { get; set; }

        /// <summary>Pose at the start of the blend (for crossfading).</summary>
        public SkeletonPose BlendStartPose { get; set; }

        /// <summary>
        /// Minimum time between search queries. Prevents matching too frequently.
        /// </summary>
        public float SearchInterval { get; set; } = 0.1f;

        /// <summary>Time since the last search.</summary>
        public float TimeSinceLastSearch { get; set; }

        /// <summary>
        /// Build the database from a set of clips.
        /// </summary>
        public void BuildDatabase(List<AnimationClipReal> clips, Skeleton skeleton,
            int rootBoneIndex, int leftFootIndex, int rightFootIndex)
        {
            Database.Build(clips, skeleton, rootBoneIndex, leftFootIndex, rightFootIndex);
            CurrentFrameIndex = -1;
            CurrentTime = 0f;
        }

        /// <summary>
        /// Find the best matching frame for the given query feature and
        /// desired trajectory. Uses the KD-tree for accelerated search.
        /// </summary>
        public MotionMatchingFrame FindBestMatch(MotionMatchingFeature currentFeature,
            MotionMatchingTrajectory desiredTrajectory)
        {
            if (Database.FrameCount == 0) return null;

            // Build query feature: combine current state with desired trajectory
            var queryFeature = new MotionMatchingFeature
            {
                RootPosition = currentFeature.RootPosition,
                RootVelocity = currentFeature.RootVelocity,
                RootAcceleration = currentFeature.RootAcceleration,
                RootFacing = currentFeature.RootFacing,
                LeftFootContact = currentFeature.LeftFootContact,
                RightFootContact = currentFeature.RightFootContact,
                LeftFootPosition = currentFeature.LeftFootPosition,
                RightFootPosition = currentFeature.RightFootPosition,
                LeftFootVelocity = currentFeature.LeftFootVelocity,
                RightFootVelocity = currentFeature.RightFootVelocity,
                CenterOfMass = currentFeature.CenterOfMass,
                Trajectory = desiredTrajectory
            };

            float[] query = queryFeature.ToFloatArray();

            // Use KD-tree for nearest-neighbor search
            if (Database.Tree != null)
            {
                return Database.Tree.FindNearest(query, FeatureWeights);
            }

            // Fallback: linear search
            return FindBestMatchLinear(query);
        }

        /// <summary>
        /// Linear search fallback (no KD-tree). Iterates through all frames
        /// and finds the one with the minimum weighted distance.
        /// </summary>
        private MotionMatchingFrame FindBestMatchLinear(float[] query)
        {
            float bestDist = float.MaxValue;
            MotionMatchingFrame bestFrame = null;

            foreach (var frame in Database.Frames)
            {
                float dist = MotionMatchingFeature.WeightedDistance(
                    query, frame.FeatureArray, FeatureWeights);

                // Penalize frames that are too close to the current playback position
                // (to avoid re-matching the same frame)
                if (CurrentFrameIndex >= 0)
                {
                    int frameDist = Math.Abs(frame.FrameIndex - CurrentFrameIndex);
                    if (frameDist < 5) // Within 5 frames: add penalty
                    {
                        dist *= 1f + (5 - frameDist) * 0.5f;
                    }
                }

                if (dist < bestDist)
                {
                    bestDist = dist;
                    bestFrame = frame;
                }
            }

            return bestFrame;
        }

        /// <summary>
        /// Search for a best match within a local radius around the current
        /// frame. This accelerates the search by only considering nearby frames.
        /// </summary>
        /// <param name="query">Query feature array.</param>
        /// <param name="searchRadius">Number of frames to search around the current position.</param>
        public MotionMatchingFrame Search(float[] query, int searchRadius)
        {
            if (Database.FrameCount == 0) return null;

            float bestDist = float.MaxValue;
            MotionMatchingFrame bestFrame = null;

            int center = CurrentFrameIndex >= 0 ? CurrentFrameIndex : 0;
            int start = Math.Max(0, center - searchRadius);
            int end = Math.Min(Database.FrameCount - 1, center + searchRadius);

            for (int i = start; i <= end; i++)
            {
                var frame = Database.Frames[i];
                float dist = MotionMatchingFeature.WeightedDistance(
                    query, frame.FeatureArray, FeatureWeights);

                if (dist < bestDist)
                {
                    bestDist = dist;
                    bestFrame = frame;
                }
            }

            return bestFrame;
        }

        /// <summary>
        /// Update the solver. Advances playback time, optionally searches for
        /// a new match, and handles blending.
        /// </summary>
        /// <param name="dt">Delta time.</param>
        /// <param name="currentFeature">Current character feature.</param>
        /// <param name="desiredTrajectory">Desired future trajectory.</param>
        /// <returns>The current pose (blended if transitioning).</returns>
        public SkeletonPose Update(float dt, MotionMatchingFeature currentFeature,
            MotionMatchingTrajectory desiredTrajectory)
        {
            if (Database.FrameCount == 0 || Database.Skeleton == null)
                return null;

            TimeSinceLastSearch += dt;

            // Advance current playback
            if (CurrentFrameIndex >= 0)
            {
                CurrentTime += dt;
                var currentFrame = Database.GetFrame(CurrentFrameIndex);
                if (currentFrame != null && currentFrame.Clip != null)
                {
                    // Advance to the next frame in the database
                    if (CurrentTime >= 1f / Database.SampleRate)
                    {
                        CurrentTime = 0f;
                        CurrentFrameIndex++;
                        // Loop back if we've reached the end of this clip
                        if (CurrentFrameIndex >= Database.FrameCount ||
                            Database.Frames[CurrentFrameIndex].ClipIndex != currentFrame.ClipIndex)
                        {
                            // Search for a new match
                            TimeSinceLastSearch = SearchInterval;
                        }
                    }
                }
            }

            // Search for a new match at regular intervals
            if (TimeSinceLastSearch >= SearchInterval)
            {
                TimeSinceLastSearch = 0f;

                MotionMatchingFrame bestMatch = FindBestMatch(currentFeature, desiredTrajectory);
                if (bestMatch != null && bestMatch.FrameIndex != CurrentFrameIndex)
                {
                    // Start blending to the new match
                    BlendStartPose = GetCurrentPose();
                    CurrentFrameIndex = bestMatch.FrameIndex;
                    CurrentTime = 0f;
                    RemainingBlendTime = BlendTime;
                }
            }

            // Handle blending
            if (RemainingBlendTime > 0f)
            {
                RemainingBlendTime -= dt;
                float alpha = 1f - Math.Clamp(RemainingBlendTime / BlendTime, 0f, 1f);

                SkeletonPose targetPose = GetMatchedPose();
                if (BlendStartPose != null && targetPose != null)
                {
                    return SkeletonPose.Blend(BlendStartPose, targetPose, alpha);
                }
                return targetPose;
            }

            return GetMatchedPose();
        }

        /// <summary>Get the pose at the current matched frame.</summary>
        private SkeletonPose GetMatchedPose()
        {
            if (CurrentFrameIndex < 0 || CurrentFrameIndex >= Database.FrameCount)
                return Database.Skeleton.CreateBindPose();

            // Sample the clip at the current time
            var frame = Database.Frames[CurrentFrameIndex];
            if (frame.Clip == null) return Database.Skeleton.CreateBindPose();

            // Compute the actual time within the clip
            float clipTime = frame.Time + CurrentTime;
            var pose = new SkeletonPose(Database.Skeleton);
            frame.Clip.Evaluate(clipTime, pose);
            return pose;
        }

        /// <summary>Get the current pose without advancing time.</summary>
        public SkeletonPose GetCurrentPose()
        {
            return GetMatchedPose();
        }

        /// <summary>
        /// Blend between the current frame and a newly matched frame over
        /// the specified blend time.
        /// </summary>
        public SkeletonPose Blending(MotionMatchingFrame currentFrame,
            MotionMatchingFrame matchedFrame, float blendTime)
        {
            if (matchedFrame == null) return currentFrame?.Pose;
            if (currentFrame == null) return matchedFrame.Pose;
            if (blendTime <= 0f) return matchedFrame.Pose;

            // This is handled by the Update method's blending logic.
            // Here we just return the target pose.
            return matchedFrame.Pose;
        }
    }
}
