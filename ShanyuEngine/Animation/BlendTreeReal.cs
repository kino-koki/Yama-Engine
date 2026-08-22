// BlendTreeReal.cs - Real blend tree evaluation with 1D/2D blend spaces,
// layered blending, additive blending, sync groups, and state machine bridge.
// All algorithms use System.Numerics with mathematically correct implementations.

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace ShanyuEngine.Animation
{
    // ========================================================================
    // BlendContext - evaluation context passed through the blend tree
    // ========================================================================

    /// <summary>
    /// Context passed to every blend node during evaluation. Carries delta time,
    /// current playback time, skeleton reference, and a parameter map for
    /// data-driven blend parameter lookup.
    /// </summary>
    public class BlendContext
    {
        public float DeltaTime { get; set; }
        public float CurrentTime { get; set; }
        public Skeleton Skeleton { get; set; }
        public Dictionary<string, float> Parameters { get; } = new Dictionary<string, float>();

        /// <summary>Get a blend parameter by name, returning 0 if not found.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float GetParameter(string name)
        {
            return Parameters.TryGetValue(name, out float v) ? v : 0f;
        }

        /// <summary>Set a blend parameter.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetParameter(string name, float value)
        {
            Parameters[name] = value;
        }

        public BlendContext Clone()
        {
            var ctx = new BlendContext
            {
                DeltaTime = DeltaTime,
                CurrentTime = CurrentTime,
                Skeleton = Skeleton
            };
            foreach (var kv in Parameters)
                ctx.Parameters[kv.Key] = kv.Value;
            return ctx;
        }
    }

    // ========================================================================
    // BlendNode - abstract base for all blend tree nodes
    // ========================================================================

    /// <summary>
    /// Base class for all blend tree nodes. Each node evaluates to a SkeletonPose
    /// given a BlendContext. Nodes can have child nodes forming a tree.
    /// </summary>
    public abstract class BlendNode
    {
        public string Name { get; set; } = "BlendNode";
        public BlendNodeType NodeType { get; protected set; }
        public List<BlendNode> Children { get; } = new List<BlendNode>();

        /// <summary>Cache for the last evaluated pose (avoids re-evaluation within a frame).</summary>
        protected SkeletonPose _cachedPose;
        protected float _cachedFrame = -1f;

        /// <summary>
        /// Evaluate this node and return a SkeletonPose. Implementations must
        /// be deterministic given the same context.
        /// </summary>
        public abstract SkeletonPose Evaluate(BlendContext context);

        /// <summary>Reset playback state (time, events) for this node and children.</summary>
        public virtual void Reset()
        {
            _cachedPose = null;
            _cachedFrame = -1f;
            foreach (var child in Children)
                child?.Reset();
        }

        /// <summary>Get the effective duration of this node (for sync groups).</summary>
        public virtual float GetDuration() => 0f;

        /// <summary>Return cached pose if still valid for the given frame, else null.</summary>
        protected SkeletonPose GetCached(float frame)
        {
            if (_cachedPose != null && MathF.Abs(_cachedFrame - frame) < 1e-6f)
                return _cachedPose;
            return null;
        }

        protected void SetCache(SkeletonPose pose, float frame)
        {
            _cachedPose = pose;
            _cachedFrame = frame;
        }
    }

    /// <summary>Type of blend node, for editor/runtime introspection.</summary>
    public enum BlendNodeType
    {
        Clip,
        BlendSpace1D,
        BlendSpace2D,
        LayeredBlend,
        AdditiveBlend,
        SyncGroup,
        StateMachine,
        Pose
    }

    // ========================================================================
    // ClipNode - leaf node that samples an animation clip
    // ========================================================================

    /// <summary>
    /// Leaf blend node holding an AnimationClipReal. Evaluates by sampling
    /// the clip at the current playback time, advancing time by DeltaTime.
    /// </summary>
    public class ClipNode : BlendNode
    {
        public AnimationClipReal Clip { get; set; }
        public float PlaybackSpeed { get; set; } = 1f;
        public float CurrentTime { get; set; }
        public bool AutoLoop { get; set; } = true;

        public ClipNode()
        {
            NodeType = BlendNodeType.Clip;
        }

        public ClipNode(AnimationClipReal clip) : this()
        {
            Clip = clip;
            if (clip != null)
                AutoLoop = clip.Loop;
        }

        public override SkeletonPose Evaluate(BlendContext context)
        {
            if (Clip == null || context.Skeleton == null)
                return context.Skeleton?.CreateBindPose();

            // Advance playback time
            CurrentTime += context.DeltaTime * PlaybackSpeed;

            // Handle looping / clamping
            float duration = Clip.Duration;
            if (duration > 0f)
            {
                if (AutoLoop || Clip.Loop)
                {
                    CurrentTime = ((CurrentTime % duration) + duration) % duration;
                }
                else
                {
                    CurrentTime = Math.Clamp(CurrentTime, 0f, duration);
                }
            }

            var pose = new SkeletonPose(context.Skeleton);
            Clip.Evaluate(CurrentTime, pose);
            SetCache(pose, context.CurrentTime);
            return pose;
        }

        public override float GetDuration()
        {
            return Clip?.Duration ?? 0f;
        }

        public override void Reset()
        {
            base.Reset();
            CurrentTime = 0f;
        }
    }

    // ========================================================================
    // BlendSpace1D - one-dimensional blend space
    // ========================================================================

    /// <summary>
    /// One-dimensional blend space. Child clip nodes are placed along a single
    /// axis. Given an input value, the two adjacent clips are found and their
    /// poses are blended using SLerp for rotation and Lerp for position/scale.
    /// </summary>
    public class BlendSpace1D : BlendNode
    {
        /// <summary>Entry in the 1D blend space: a child node with an axis position.</summary>
        public struct BlendEntry
        {
            public BlendNode Node;
            public float Position;
        }

        public List<BlendEntry> Entries { get; } = new List<BlendEntry>();
        public string ParameterName { get; set; } = "Speed";
        public float CurrentValue { get; set; }
        public float PlaybackTime { get; set; }

        private bool _sorted;

        public BlendSpace1D()
        {
            NodeType = BlendNodeType.BlendSpace1D;
        }

        /// <summary>Add a child node at a given axis position.</summary>
        public void AddEntry(BlendNode node, float position)
        {
            if (node == null) return;
            Entries.Add(new BlendEntry { Node = node, Position = position });
            Children.Add(node);
            _sorted = false;
        }

        private void EnsureSorted()
        {
            if (!_sorted)
            {
                Entries.Sort((a, b) => a.Position.CompareTo(b.Position));
                _sorted = true;
            }
        }

        public override SkeletonPose Evaluate(BlendContext context)
        {
            EnsureSorted();
            if (context.Skeleton == null) return null;
            if (Entries.Count == 0)
                return context.Skeleton.CreateBindPose();

            float inputValue = context.GetParameter(ParameterName);
            CurrentValue = inputValue;

            // Advance playback time
            PlaybackTime += context.DeltaTime;

            // Single entry: just evaluate it
            if (Entries.Count == 1)
            {
                var childCtx = context.Clone();
                childCtx.CurrentTime = PlaybackTime;
                return Entries[0].Node.Evaluate(childCtx);
            }

            // Clamp below range
            if (inputValue <= Entries[0].Position)
            {
                var childCtx = context.Clone();
                childCtx.CurrentTime = PlaybackTime;
                return Entries[0].Node.Evaluate(childCtx);
            }

            // Clamp above range
            if (inputValue >= Entries[Entries.Count - 1].Position)
            {
                var childCtx = context.Clone();
                childCtx.CurrentTime = PlaybackTime;
                return Entries[Entries.Count - 1].Node.Evaluate(childCtx);
            }

            // Find adjacent pair
            int idx = 0;
            for (int i = 0; i < Entries.Count - 1; i++)
            {
                if (inputValue >= Entries[i].Position && inputValue <= Entries[i + 1].Position)
                {
                    idx = i;
                    break;
                }
            }

            float span = Entries[idx + 1].Position - Entries[idx].Position;
            float alpha = span > 1e-8f
                ? (inputValue - Entries[idx].Position) / span
                : 0f;
            alpha = Math.Clamp(alpha, 0f, 1f);

            // Evaluate both children at the same playback time
            var ctxA = context.Clone();
            ctxA.CurrentTime = PlaybackTime;
            var ctxB = context.Clone();
            ctxB.CurrentTime = PlaybackTime;

            SkeletonPose poseA = Entries[idx].Node.Evaluate(ctxA);
            SkeletonPose poseB = Entries[idx + 1].Node.Evaluate(ctxB);

            // Blend the two poses
            return SkeletonPose.Blend(poseA, poseB, alpha);
        }

        public override float GetDuration()
        {
            float max = 0f;
            foreach (var e in Entries)
                max = MathF.Max(max, e.Node?.GetDuration() ?? 0f);
            return max;
        }

        public override void Reset()
        {
            base.Reset();
            PlaybackTime = 0f;
        }
    }

    // ========================================================================
    // BlendSpace2D - two-dimensional blend space with Delaunay triangulation
    // ========================================================================

    /// <summary>
    /// Two-dimensional blend space. Child clip nodes are placed on a 2D plane.
    /// A Delaunay triangulation connects the points into triangles. Given an
    /// input (x, y), the containing triangle is found and barycentric coordinates
    /// are used to blend three poses.
    /// </summary>
    public class BlendSpace2D : BlendNode
    {
        /// <summary>Entry in the 2D blend space.</summary>
        public struct BlendEntry2D
        {
            public BlendNode Node;
            public Vector2 Position;
        }

        public List<BlendEntry2D> Entries { get; } = new List<BlendEntry2D>();
        public string ParameterX { get; set; } = "MoveX";
        public string ParameterY { get; set; } = "MoveY";
        public Vector2 CurrentValue { get; set; }
        public float PlaybackTime { get; set; }

        private List<(int A, int B, int C)> _triangles;
        private bool _triangulated;

        public BlendSpace2D()
        {
            NodeType = BlendNodeType.BlendSpace2D;
        }

        public void AddEntry(BlendNode node, Vector2 position)
        {
            if (node == null) return;
            Entries.Add(new BlendEntry2D { Node = node, Position = position });
            Children.Add(node);
            _triangulated = false;
        }

        public void AddEntry(BlendNode node, float x, float y)
            => AddEntry(node, new Vector2(x, y));

        // ------------------------------------------------------------------
        // Delaunay triangulation (Bowyer-Watson incremental algorithm)
        // ------------------------------------------------------------------

        /// <summary>
        /// Build the Delaunay triangulation of the entry positions using the
        /// Bowyer-Watson incremental insertion algorithm.
        /// </summary>
        public void Triangulate()
        {
            _triangles = new List<(int, int, int)>();
            int n = Entries.Count;
            if (n < 3)
            {
                _triangulated = true;
                return;
            }

            // Compute bounding box of all points
            float minX = float.MaxValue, minY = float.MaxValue;
            float maxX = float.MinValue, maxY = float.MinValue;
            for (int i = 0; i < n; i++)
            {
                minX = MathF.Min(minX, Entries[i].Position.X);
                minY = MathF.Min(minY, Entries[i].Position.Y);
                maxX = MathF.Max(maxX, Entries[i].Position.X);
                maxY = MathF.Max(maxY, Entries[i].Position.Y);
            }

            float dx = maxX - minX;
            float dy = maxY - minY;
            float deltaMax = MathF.Max(dx, dy) * 10f + 1f;
            float midX = (minX + maxX) * 0.5f;
            float midY = (minY + maxY) * 0.5f;

            // Super-triangle vertices (appended as virtual indices n, n+1, n+2)
            var points = new List<Vector2>(n + 3);
            for (int i = 0; i < n; i++)
                points.Add(Entries[i].Position);
            points.Add(new Vector2(midX - 20f * deltaMax, midY - deltaMax)); // n
            points.Add(new Vector2(midX, midY + 20f * deltaMax));             // n+1
            points.Add(new Vector2(midX + 20f * deltaMax, midY - deltaMax));  // n+2

            var tris = new List<(int A, int B, int C)>();
            tris.Add((n, n + 1, n + 2)); // super-triangle

            // Insert each real point
            for (int i = 0; i < n; i++)
            {
                var badTris = new List<(int A, int B, int C)>();
                var edges = new List<(int P, int Q)>();

                // Find triangles whose circumcircle contains point i
                foreach (var tri in tris)
                {
                    if (InCircumcircle(points[tri.A], points[tri.B], points[tri.C], points[i]))
                    {
                        badTris.Add(tri);
                        edges.Add((tri.A, tri.B));
                        edges.Add((tri.B, tri.C));
                        edges.Add((tri.C, tri.A));
                    }
                }

                // Remove bad triangles from the list
                foreach (var bad in badTris)
                    tris.Remove(bad);

                // Find unique edges (edges that appear only once)
                var uniqueEdges = new List<(int P, int Q)>();
                for (int j = 0; j < edges.Count; j++)
                {
                    bool shared = false;
                    for (int k = 0; k < edges.Count; k++)
                    {
                        if (j == k) continue;
                        if (EdgesEqual(edges[j], edges[k]))
                        {
                            shared = true;
                            break;
                        }
                    }
                    if (!shared)
                        uniqueEdges.Add(edges[j]);
                }

                // Create new triangles from unique edges to the inserted point
                foreach (var edge in uniqueEdges)
                    tris.Add((edge.P, edge.Q, i));
            }

            // Remove triangles that use super-triangle vertices
            foreach (var tri in tris)
            {
                if (tri.A < n && tri.B < n && tri.C < n)
                    _triangles.Add(tri);
            }

            _triangulated = true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool EdgesEqual((int P, int Q) a, (int P, int Q) b)
        {
            return (a.P == b.P && a.Q == b.Q) || (a.P == b.Q && a.Q == b.P);
        }

        /// <summary>
        /// Test if point d is inside the circumcircle of triangle (a, b, c).
        /// Uses the determinant test.
        /// </summary>
        private static bool InCircumcircle(Vector2 a, Vector2 b, Vector2 c, Vector2 d)
        {
            float ax = a.X - d.X, ay = a.Y - d.Y;
            float bx = b.X - d.X, by = b.Y - d.Y;
            float cx = c.X - d.X, cy = c.Y - d.Y;
            float ap = ax * ax + ay * ay;
            float bp = bx * bx + by * by;
            float cp = cx * cx + cy * cy;
            float det = ax * (by * cp - bp * cy)
                      - ay * (bx * cp - bp * cx)
                      + ap * (bx * cy - by * cx);

            // Orientation of (a, b, c)
            float orient = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
            if (orient > 0f) return det > 0f;
            if (orient < 0f) return det < 0f;
            return false;
        }

        // ------------------------------------------------------------------
        // Barycentric coordinates
        // ------------------------------------------------------------------

        /// <summary>
        /// Compute barycentric coordinates (u, v, w) of point p relative to
        /// triangle (a, b, c). u corresponds to a, v to b, w to c.
        /// </summary>
        private static void Barycentric(Vector2 a, Vector2 b, Vector2 c, Vector2 p,
            out float u, out float v, out float w)
        {
            Vector2 v0 = b - a;
            Vector2 v1 = c - a;
            Vector2 v2 = p - a;
            float d00 = Vector2.Dot(v0, v0);
            float d01 = Vector2.Dot(v0, v1);
            float d11 = Vector2.Dot(v1, v1);
            float d20 = Vector2.Dot(v2, v0);
            float d21 = Vector2.Dot(v2, v1);
            float denom = d00 * d11 - d01 * d01;
            if (MathF.Abs(denom) < 1e-12f)
            {
                u = v = w = 0f;
                return;
            }
            v = (d11 * d20 - d01 * d21) / denom;
            w = (d00 * d21 - d01 * d20) / denom;
            u = 1f - v - w;
        }

        /// <summary>
        /// Check if point p is inside triangle (a, b, c) using barycentric
        /// coordinates. All coordinates must be non-negative.
        /// </summary>
        private static bool PointInTriangle(Vector2 a, Vector2 b, Vector2 c, Vector2 p)
        {
            Barycentric(a, b, c, p, out float u, out float v, out float w);
            return u >= -1e-6f && v >= -1e-6f && w >= -1e-6f;
        }

        // ------------------------------------------------------------------
        // Evaluation
        // ------------------------------------------------------------------

        public override SkeletonPose Evaluate(BlendContext context)
        {
            if (!_triangulated)
                Triangulate();

            if (context.Skeleton == null) return null;
            if (Entries.Count == 0)
                return context.Skeleton.CreateBindPose();

            float inputX = context.GetParameter(ParameterX);
            float inputY = context.GetParameter(ParameterY);
            Vector2 input = new Vector2(inputX, inputY);
            CurrentValue = input;

            PlaybackTime += context.DeltaTime;

            // Single entry
            if (Entries.Count == 1)
            {
                var ctx = context.Clone();
                ctx.CurrentTime = PlaybackTime;
                return Entries[0].Node.Evaluate(ctx);
            }

            // Two entries: blend along the line segment
            if (Entries.Count == 2)
            {
                Vector2 a = Entries[0].Position;
                Vector2 b = Entries[1].Position;
                Vector2 ab = b - a;
                float lenSq = Vector2.Dot(ab, ab);
                float t = lenSq > 1e-8f
                    ? Math.Clamp(Vector2.Dot(input - a, ab) / lenSq, 0f, 1f)
                    : 0f;

                var ctxA = context.Clone();
                ctxA.CurrentTime = PlaybackTime;
                var ctxB = context.Clone();
                ctxB.CurrentTime = PlaybackTime;
                var poseA = Entries[0].Node.Evaluate(ctxA);
                var poseB = Entries[1].Node.Evaluate(ctxB);
                return SkeletonPose.Blend(poseA, poseB, t);
            }

            // Three or more: find containing triangle
            if (_triangles != null && _triangles.Count > 0)
            {
                foreach (var tri in _triangles)
                {
                    Vector2 a = Entries[tri.A].Position;
                    Vector2 b = Entries[tri.B].Position;
                    Vector2 c = Entries[tri.C].Position;

                    if (PointInTriangle(a, b, c, input))
                    {
                        Barycentric(a, b, c, input, out float u, out float v, out float w);
                        return BlendThree(context, tri.A, tri.B, tri.C, u, v, w);
                    }
                }

                // Point is outside the convex hull: find nearest triangle and
                // clamp by projecting onto the nearest edge
                int bestTri = 0;
                float bestDist = float.MaxValue;
                for (int i = 0; i < _triangles.Count; i++)
                {
                    var tri = _triangles[i];
                    float d = DistanceToTriangle(
                        Entries[tri.A].Position,
                        Entries[tri.B].Position,
                        Entries[tri.C].Position,
                        input);
                    if (d < bestDist)
                    {
                        bestDist = d;
                        bestTri = i;
                    }
                }

                var nearestTri = _triangles[bestTri];
                Vector2 pa = Entries[nearestTri.A].Position;
                Vector2 pb = Entries[nearestTri.B].Position;
                Vector2 pc = Entries[nearestTri.C].Position;

                // Clamp: compute barycentric and clamp negatives to zero, then renormalize
                Barycentric(pa, pb, pc, input, out float cu, out float cv, out float cw);
                cu = Math.Max(0f, cu);
                cv = Math.Max(0f, cv);
                cw = Math.Max(0f, cw);
                float sum = cu + cv + cw;
                if (sum > 1e-8f)
                {
                    cu /= sum;
                    cv /= sum;
                    cw /= sum;
                }
                else
                {
                    cu = cv = cw = 1f / 3f;
                }

                return BlendThree(context, nearestTri.A, nearestTri.B, nearestTri.C, cu, cv, cw);
            }

            // Fallback: just evaluate first entry
            var fallbackCtx = context.Clone();
            fallbackCtx.CurrentTime = PlaybackTime;
            return Entries[0].Node.Evaluate(fallbackCtx);
        }

        /// <summary>Blend three child poses by barycentric weights.</summary>
        private SkeletonPose BlendThree(BlendContext context, int ia, int ib, int ic,
            float wa, float wb, float wc)
        {
            var ctxA = context.Clone();
            ctxA.CurrentTime = PlaybackTime;
            var ctxB = context.Clone();
            ctxB.CurrentTime = PlaybackTime;
            var ctxC = context.Clone();
            ctxC.CurrentTime = PlaybackTime;

            SkeletonPose poseA = Entries[ia].Node.Evaluate(ctxA);
            SkeletonPose poseB = Entries[ib].Node.Evaluate(ctxB);
            SkeletonPose poseC = Entries[ic].Node.Evaluate(ctxC);

            if (poseA == null) return poseB ?? poseC;
            if (poseB == null) return poseA;
            if (poseC == null) return poseA;

            int count = Math.Min(poseA.BoneCount, Math.Min(poseB.BoneCount, poseC.BoneCount));
            var result = new SkeletonPose(context.Skeleton);

            for (int i = 0; i < count; i++)
            {
                ref readonly BonePose bpA = ref poseA.BonePoses[i];
                ref readonly BonePose bpB = ref poseB.BonePoses[i];
                ref readonly BonePose bpC = ref poseC.BonePoses[i];

                // Position and scale: weighted average
                Vector3 pos = bpA.Position * wa + bpB.Position * wb + bpC.Position * wc;
                Vector3 scl = bpA.Scale * wa + bpB.Scale * wb + bpC.Scale * wc;

                // Rotation: sequential SLerp by cumulative weight
                // Start with poseA, slerp towards poseB by wb/(wa+wb), then towards poseC
                float wab = wa + wb;
                Quaternion rot;
                if (wab > 1e-8f)
                {
                    rot = KeyframeInterpolation.InterpolateQuaternionSlerp(
                        bpA.Rotation, bpB.Rotation, wb / wab);
                }
                else
                {
                    rot = bpC.Rotation;
                }
                float total = wab + wc;
                if (total > 1e-8f)
                {
                    rot = KeyframeInterpolation.InterpolateQuaternionSlerp(
                        rot, bpC.Rotation, cw / total);
                }
                rot = Quaternion.Normalize(rot);

                result.BonePoses[i] = new BonePose(pos, rot, scl);
            }

            return result;
        }

        /// <summary>Compute minimum distance from point p to triangle (a, b, c).</summary>
        private static float DistanceToTriangle(Vector2 a, Vector2 b, Vector2 c, Vector2 p)
        {
            float d1 = DistanceToSegment(a, b, p);
            float d2 = DistanceToSegment(b, c, p);
            float d3 = DistanceToSegment(c, a, p);
            return MathF.Min(d1, MathF.Min(d2, d3));
        }

        /// <summary>Distance from point p to line segment (segA, segB).</summary>
        private static float DistanceToSegment(Vector2 segA, Vector2 segB, Vector2 p)
        {
            Vector2 ab = segB - segA;
            float lenSq = Vector2.Dot(ab, ab);
            if (lenSq < 1e-12f) return Vector2.Distance(p, segA);
            float t = Math.Clamp(Vector2.Dot(p - segA, ab) / lenSq, 0f, 1f);
            Vector2 proj = segA + ab * t;
            return Vector2.Distance(p, proj);
        }

        public override float GetDuration()
        {
            float max = 0f;
            foreach (var e in Entries)
                max = MathF.Max(max, e.Node?.GetDuration() ?? 0f);
            return max;
        }

        public override void Reset()
        {
            base.Reset();
            PlaybackTime = 0f;
        }
    }

    // ========================================================================
    // BoneMask - per-bone mask for layered blending
    // ========================================================================

    /// <summary>
    /// A bone mask: a boolean array indexed by bone index. Used by
    /// LayeredBlendNode to control which bones each overlay layer affects.
    /// </summary>
    public class BoneMask
    {
        public bool[] Enabled { get; }
        public int BoneCount => Enabled?.Length ?? 0;

        public BoneMask(int boneCount)
        {
            Enabled = new bool[boneCount];
            for (int i = 0; i < boneCount; i++)
                Enabled[i] = true;
        }

        public BoneMask(Skeleton skeleton) : this(skeleton?.BoneCount ?? 0) { }

        /// <summary>Enable or disable a specific bone.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetBone(int boneIndex, bool enabled)
        {
            if (boneIndex >= 0 && boneIndex < Enabled.Length)
                Enabled[boneIndex] = enabled;
        }

        /// <summary>Check if a bone is enabled.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsEnabled(int boneIndex)
        {
            return boneIndex >= 0 && boneIndex < Enabled.Length && Enabled[boneIndex];
        }

        /// <summary>Enable all bones.</summary>
        public void EnableAll()
        {
            for (int i = 0; i < Enabled.Length; i++)
                Enabled[i] = true;
        }

        /// <summary>Disable all bones.</summary>
        public void DisableAll()
        {
            for (int i = 0; i < Enabled.Length; i++)
                Enabled[i] = false;
        }

        /// <summary>Enable a bone and all of its descendants.</summary>
        public void EnableBoneAndChildren(Skeleton skeleton, int boneIndex)
        {
            if (skeleton == null || boneIndex < 0) return;
            SetBone(boneIndex, true);
            foreach (int child in skeleton.GetChildren(boneIndex))
                EnableBoneAndChildren(skeleton, child);
        }

        /// <summary>Create a mask from a list of bone names.</summary>
        public static BoneMask FromBoneNames(Skeleton skeleton, IEnumerable<string> boneNames, bool enableListed = true)
        {
            var mask = new BoneMask(skeleton);
            if (!enableListed)
                mask.DisableAll();
            foreach (string name in boneNames)
            {
                int idx = skeleton.FindBone(name);
                if (idx >= 0)
                    mask.SetBone(idx, enableListed);
            }
            return mask;
        }

        public BoneMask Clone()
        {
            var clone = new BoneMask(Enabled.Length);
            Array.Copy(Enabled, clone.Enabled, Enabled.Length);
            return clone;
        }
    }

    // ========================================================================
    // LayeredBlendNode - per-bone layered blending
    // ========================================================================

    /// <summary>
    /// An overlay layer in a layered blend: a child node, a weight, and a bone mask.
    /// </summary>
    public class BlendLayer
    {
        public BlendNode Node { get; set; }
        public float Weight { get; set; } = 1f;
        public BoneMask Mask { get; set; }

        /// <summary>If true, this layer is blended additively rather than overriding.</summary>
        public bool IsAdditive { get; set; }
    }

    /// <summary>
    /// Layered blend node: evaluates a base layer, then applies overlay layers
    /// on top. Each overlay layer has a weight and an optional bone mask that
    /// controls which bones it affects.
    /// </summary>
    public class LayeredBlendNode : BlendNode
    {
        public BlendNode BaseLayer { get; set; }
        public List<BlendLayer> Layers { get; } = new List<BlendLayer>();

        public LayeredBlendNode()
        {
            NodeType = BlendNodeType.LayeredBlend;
        }

        public void AddLayer(BlendNode node, float weight, BoneMask mask = null, bool additive = false)
        {
            var layer = new BlendLayer
            {
                Node = node,
                Weight = weight,
                Mask = mask,
                IsAdditive = additive
            };
            Layers.Add(layer);
            if (node != null)
                Children.Add(node);
        }

        public override SkeletonPose Evaluate(BlendContext context)
        {
            if (context.Skeleton == null) return null;

            // Evaluate base layer
            SkeletonPose result = BaseLayer?.Evaluate(context);
            if (result == null)
                result = context.Skeleton.CreateBindPose();
            else
                result = result.Clone();

            // Apply each overlay layer
            foreach (var layer in Layers)
            {
                if (layer.Node == null) continue;
                float weight = Math.Clamp(layer.Weight, 0f, 1f);
                if (weight <= 0f) continue;

                SkeletonPose overlay = layer.Node.Evaluate(context);
                if (overlay == null) continue;

                if (layer.IsAdditive)
                {
                    // Additive: result = result + overlay_delta * weight * mask
                    ApplyAdditiveLayer(result, overlay, weight, layer.Mask);
                }
                else
                {
                    // Override: result = lerp(result, overlay, weight * mask)
                    ApplyOverrideLayer(result, overlay, weight, layer.Mask);
                }
            }

            return result;
        }

        /// <summary>
        /// Apply an override layer: for each masked bone, blend from base to overlay
        /// by (weight * maskWeight). Position/Scale use Lerp, Rotation uses SLerp.
        /// </summary>
        private static void ApplyOverrideLayer(SkeletonPose basePose, SkeletonPose overlay,
            float weight, BoneMask mask)
        {
            int count = Math.Min(basePose.BoneCount, overlay.BoneCount);
            for (int i = 0; i < count; i++)
            {
                float boneWeight = weight;
                if (mask != null && !mask.IsEnabled(i))
                    continue;

                ref BonePose bp = ref basePose.BonePoses[i];
                ref readonly BonePose op = ref overlay.BonePoses[i];

                bp.Position = Vector3.Lerp(bp.Position, op.Position, boneWeight);
                bp.Rotation = KeyframeInterpolation.InterpolateQuaternionSlerp(
                    bp.Rotation, op.Rotation, boneWeight);
                bp.Scale = Vector3.Lerp(bp.Scale, op.Scale, boneWeight);
                basePose.BonePoses[i] = bp;
            }
        }

        /// <summary>
        /// Apply an additive layer: for each masked bone, add the overlay's
        /// delta (relative to identity) scaled by weight.
        /// </summary>
        private static void ApplyAdditiveLayer(SkeletonPose basePose, SkeletonPose overlay,
            float weight, BoneMask mask)
        {
            int count = Math.Min(basePose.BoneCount, overlay.BoneCount);
            for (int i = 0; i < count; i++)
            {
                if (mask != null && !mask.IsEnabled(i))
                    continue;

                ref BonePose bp = ref basePose.BonePoses[i];
                ref readonly BonePose op = ref overlay.BonePoses[i];

                // Additive: position adds, rotation composes, scale multiplies
                Vector3 addPos = op.Position * weight;
                Quaternion addRot = KeyframeInterpolation.InterpolateQuaternionSlerp(
                    Quaternion.Identity, op.Rotation, weight);
                Vector3 addScale = Vector3.Lerp(Vector3.One, op.Scale, weight);

                bp.Position += addPos;
                bp.Rotation = Quaternion.Normalize(addRot * bp.Rotation);
                bp.Scale *= addScale;
                basePose.BonePoses[i] = bp;
            }
        }

        public override float GetDuration()
        {
            float max = BaseLayer?.GetDuration() ?? 0f;
            foreach (var layer in Layers)
                max = MathF.Max(max, layer.Node?.GetDuration() ?? 0f);
            return max;
        }

        public override void Reset()
        {
            base.Reset();
            BaseLayer?.Reset();
        }
    }

    // ========================================================================
    // AdditiveBlendNode - additive blending of base + delta clip
    // ========================================================================

    /// <summary>Type of additive blending.</summary>
    public enum AdditiveType
    {
        /// <summary>Blend translation, rotation, and scale.</summary>
        TranslationRotationScale,
        /// <summary>Only blend translation.</summary>
        Translation,
        /// <summary>Only blend rotation.</summary>
        Rotation,
        /// <summary>Only blend scale.</summary>
        Scale
    }

    /// <summary>
    /// Additive blend node: result = base + (additive - additiveBase) * weight.
    /// The additiveBase is typically the first frame of the additive clip (rest pose).
    /// </summary>
    public class AdditiveBlendNode : BlendNode
    {
        public BlendNode BaseNode { get; set; }
        public BlendNode AdditiveNode { get; set; }
        public SkeletonPose AdditiveBasePose { get; set; }
        public float Weight { get; set; } = 1f;
        public AdditiveType AdditiveMode { get; set; } = AdditiveType.TranslationRotationScale;
        public string WeightParameter { get; set; }

        public AdditiveBlendNode()
        {
            NodeType = BlendNodeType.AdditiveBlend;
        }

        public AdditiveBlendNode(BlendNode baseNode, BlendNode additiveNode, SkeletonPose additiveBase = null)
            : this()
        {
            BaseNode = baseNode;
            AdditiveNode = additiveNode;
            AdditiveBasePose = additiveBase;
            if (baseNode != null) Children.Add(baseNode);
            if (additiveNode != null) Children.Add(additiveNode);
        }

        public override SkeletonPose Evaluate(BlendContext context)
        {
            if (context.Skeleton == null) return null;

            SkeletonPose basePose = BaseNode?.Evaluate(context);
            if (basePose == null)
                basePose = context.Skeleton.CreateBindPose();
            else
                basePose = basePose.Clone();

            if (AdditiveNode == null)
                return basePose;

            SkeletonPose additivePose = AdditiveNode.Evaluate(context);
            if (additivePose == null)
                return basePose;

            float weight = Weight;
            if (!string.IsNullOrEmpty(WeightParameter))
                weight = context.GetParameter(WeightParameter);
            weight = Math.Clamp(weight, 0f, 1f);

            if (weight <= 0f)
                return basePose;

            int count = Math.Min(basePose.BoneCount, additivePose.BoneCount);

            for (int i = 0; i < count; i++)
            {
                ref BonePose bp = ref basePose.BonePoses[i];
                ref readonly BonePose ap = ref additivePose.BonePoses[i];

                BonePose additiveBase = (AdditiveBasePose != null && i < AdditiveBasePose.BoneCount)
                    ? AdditiveBasePose.BonePoses[i]
                    : BonePose.Identity;

                switch (AdditiveMode)
                {
                    case AdditiveType.Translation:
                        {
                            Vector3 dPos = (ap.Position - additiveBase.Position) * weight;
                            bp.Position += dPos;
                            break;
                        }
                    case AdditiveType.Rotation:
                        {
                            Quaternion deltaRot = KeyframeInterpolation.InterpolateQuaternionSlerp(
                                Quaternion.Identity,
                                ap.Rotation * Quaternion.Inverse(additiveBase.Rotation),
                                weight);
                            bp.Rotation = Quaternion.Normalize(deltaRot * bp.Rotation);
                            break;
                        }
                    case AdditiveType.Scale:
                        {
                            Vector3 dScale = Vector3.Lerp(Vector3.One,
                                ap.Scale / additiveBase.Scale, weight);
                            bp.Scale *= dScale;
                            break;
                        }
                    default: // TranslationRotationScale
                        {
                            bp = BonePose.AdditiveBlend(bp, ap, additiveBase, weight);
                            break;
                        }
                }
                basePose.BonePoses[i] = bp;
            }

            return basePose;
        }

        public override float GetDuration()
        {
            return Math.Max(
                BaseNode?.GetDuration() ?? 0f,
                AdditiveNode?.GetDuration() ?? 0f);
        }

        public override void Reset()
        {
            base.Reset();
            BaseNode?.Reset();
            AdditiveNode?.Reset();
        }
    }

    // ========================================================================
    // SyncGroupNode - synchronized playback of multiple clips
    // ========================================================================

    /// <summary>
    /// How the sync group determines its playback duration.
    /// </summary>
    public enum SyncMode
    {
        /// <summary>Use the shortest clip's duration.</summary>
        Shortest,
        /// <summary>Use the longest clip's duration.</summary>
        Longest,
        /// <summary>Use the specified clip's duration.</summary>
        Specified
    }

    /// <summary>
    /// Synchronized group node: plays multiple child nodes in sync. All children
    /// share the same normalized playback time (0..1 mapped to each clip's duration).
    /// </summary>
    public class SyncGroupNode : BlendNode
    {
        public List<BlendNode> SyncedNodes { get; } = new List<BlendNode>();
        public SyncMode Mode { get; set; } = SyncMode.Shortest;
        public int SpecifiedNodeIndex { get; set; } = 0;
        public float NormalizedTime { get; set; }
        public float PlaybackSpeed { get; set; } = 1f;

        public SyncGroupNode()
        {
            NodeType = BlendNodeType.SyncGroup;
        }

        public void AddNode(BlendNode node)
        {
            if (node == null) return;
            SyncedNodes.Add(node);
            Children.Add(node);
        }

        /// <summary>Compute the effective duration based on the sync mode.</summary>
        public override float GetDuration()
        {
            if (SyncedNodes.Count == 0) return 0f;

            switch (Mode)
            {
                case SyncMode.Longest:
                    {
                        float max = 0f;
                        foreach (var n in SyncedNodes)
                            max = MathF.Max(max, n?.GetDuration() ?? 0f);
                        return max;
                    }
                case SyncMode.Specified:
                    {
                        if (SpecifiedNodeIndex >= 0 && SpecifiedNodeIndex < SyncedNodes.Count)
                            return SyncedNodes[SpecifiedNodeIndex]?.GetDuration() ?? 0f;
                        return 0f;
                    }
                default: // Shortest
                    {
                        float min = float.MaxValue;
                        foreach (var n in SyncedNodes)
                        {
                            float d = n?.GetDuration() ?? 0f;
                            if (d > 0f) min = MathF.Min(min, d);
                        }
                        return min == float.MaxValue ? 0f : min;
                    }
            }
        }

        public override SkeletonPose Evaluate(BlendContext context)
        {
            if (context.Skeleton == null || SyncedNodes.Count == 0)
                return context.Skeleton?.CreateBindPose();

            // Advance normalized time
            float duration = GetDuration();
            if (duration > 0f)
            {
                NormalizedTime += (context.DeltaTime * PlaybackSpeed) / duration;
                NormalizedTime = ((NormalizedTime % 1f) + 1f) % 1f;
            }

            // Evaluate all nodes at the same normalized time
            var poses = new List<SkeletonPose>();
            foreach (var node in SyncedNodes)
            {
                if (node == null) continue;
                float nodeDuration = node.GetDuration();
                float nodeTime = nodeDuration > 0f ? NormalizedTime * nodeDuration : 0f;

                var childCtx = context.Clone();
                childCtx.CurrentTime = nodeTime;
                childCtx.DeltaTime = context.DeltaTime * PlaybackSpeed;
                var pose = node.Evaluate(childCtx);
                if (pose != null)
                    poses.Add(pose);
            }

            if (poses.Count == 0)
                return context.Skeleton.CreateBindPose();
            if (poses.Count == 1)
                return poses[0];

            // Blend all poses with equal weight
            return BlendMultiple(context.Skeleton, poses);
        }

        /// <summary>Blend multiple poses with equal weight (1/N each).</summary>
        private static SkeletonPose BlendMultiple(Skeleton skeleton, List<SkeletonPose> poses)
        {
            int count = poses[0].BoneCount;
            var result = new SkeletonPose(skeleton);

            for (int i = 0; i < count; i++)
            {
                Vector3 posSum = Vector3.Zero;
                Vector3 sclSum = Vector3.Zero;

                for (int j = 0; j < poses.Count; j++)
                {
                    posSum += poses[j].BonePoses[i].Position;
                    sclSum += poses[j].BonePoses[i].Scale;
                }

                float invN = 1f / poses.Count;
                Vector3 avgPos = posSum * invN;
                Vector3 avgScl = sclSum * invN;

                // Rotation: sequential slerp
                Quaternion rot = poses[0].BonePoses[i].Rotation;
                for (int j = 1; j < poses.Count; j++)
                {
                    float t = 1f / (j + 1);
                    rot = KeyframeInterpolation.InterpolateQuaternionSlerp(
                        rot, poses[j].BonePoses[i].Rotation, t);
                }

                result.BonePoses[i] = new BonePose(avgPos, Quaternion.Normalize(rot), avgScl);
            }

            return result;
        }

        public override void Reset()
        {
            base.Reset();
            NormalizedTime = 0f;
        }
    }

    // ========================================================================
    // StateMachineBlendNode - bridge to AnimStateMachineReal
    // ========================================================================

    /// <summary>
    /// Blend node that wraps an AnimStateMachineReal. Evaluation delegates to
    /// the state machine's Tick + GetCurrentPose.
    /// </summary>
    public class StateMachineBlendNode : BlendNode
    {
        public AnimStateMachineReal StateMachine { get; set; }

        public StateMachineBlendNode()
        {
            NodeType = BlendNodeType.StateMachine;
        }

        public StateMachineBlendNode(AnimStateMachineReal stateMachine) : this()
        {
            StateMachine = stateMachine;
        }

        public override SkeletonPose Evaluate(BlendContext context)
        {
            if (StateMachine == null || context.Skeleton == null)
                return context.Skeleton?.CreateBindPose();

            // Tick the state machine with delta time
            StateMachine.Tick(context.DeltaTime);

            // Return the current blended pose
            return StateMachine.GetCurrentPose(context.Skeleton);
        }

        public override float GetDuration()
        {
            return StateMachine?.GetCurrentStateDuration() ?? 0f;
        }

        public override void Reset()
        {
            base.Reset();
            StateMachine?.Reset();
        }
    }
}
