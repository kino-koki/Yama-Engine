using System.Numerics;
using YamaEngine.Skinning;

namespace YamaEngine.Animation
{
    /// <summary>
    /// The dimensional layout and weighting strategy used by a <see cref="BlendTree"/>.
    /// </summary>
    public enum BlendTreeType
    {
        /// <summary>One-dimensional blend along a single threshold parameter (e.g. speed).</summary>
        Simple1D,
        /// <summary>
        /// Two-dimensional blend where child positions are directions radiating from the
        /// origin. The magnitude of the input vector scales the animation; a zero vector
        /// produces no motion contribution.
        /// </summary>
        SimpleDirectional2D,
        /// <summary>
        /// Two-dimensional blend where child positions are directions but may point in
        /// any direction and are not required to be evenly distributed.
        /// </summary>
        FreeformDirectional2D,
        /// <summary>
        /// Two-dimensional blend treating positions as free-form cartesian coordinates.
        /// </summary>
        FreeformCartesian2D
    }

    /// <summary>
    /// A single child motion inside a <see cref="BlendTree"/>. For 1D trees only
    /// <see cref="Threshold"/> is used; for 2D trees <see cref="Position2D"/> is used.
    /// </summary>
    public sealed class BlendTreeChild
    {
        public IMotion Motion { get; set; } = null!;
        public float Threshold { get; set; }
        public Vector2 Position2D { get; set; }
        public float Speed { get; set; } = 1f;

        /// <summary>Computed blend weight for the current frame (0..1). Updated by the parent tree.</summary>
        internal float Weight { get; set; }

        public BlendTreeChild() { }

        public BlendTreeChild(IMotion motion, float threshold)
        {
            Motion = motion;
            Threshold = threshold;
        }

        public BlendTreeChild(IMotion motion, Vector2 position2D)
        {
            Motion = motion;
            Position2D = position2D;
            Threshold = position2D.X;
        }
    }

    /// <summary>
    /// A blend tree that interpolates between several child motions (clips or nested
    /// blend trees) based on one or two parameters. Implements <see cref="IMotion"/> so it
    /// can be used anywhere an animation clip is accepted (e.g. as a state's motion).
    /// </summary>
    public sealed class BlendTree : IMotion
    {
        public string Name { get; set; } = "BlendTree";
        public BlendTreeType Type { get; set; } = BlendTreeType.Simple1D;

        /// <summary>Name of the parameter used for the X / primary axis.</summary>
        public string BlendParameter { get; set; } = string.Empty;
        /// <summary>Name of the parameter used for the Y axis (2D trees only).</summary>
        public string BlendParameterY { get; set; } = string.Empty;

        public List<BlendTreeChild> Children { get; set; } = new();

        /// <summary>
        /// Cached parameter values. The owning state machine writes the current parameter
        /// values here each frame before sampling. When used standalone, set these directly.
        /// </summary>
        public float ParameterValueX { get; set; }
        public float ParameterValueY { get; set; }

        string IMotion.Name => Name;

        float IMotion.Length
        {
            get
            {
                float max = 0f;
                foreach (var c in Children)
                    if (c.Motion != null) max = MathF.Max(max, c.Motion.Length);
                return max;
            }
        }

        bool IMotion.IsLooping
        {
            get
            {
                foreach (var c in Children)
                    if (c.Motion != null && c.Motion.IsLooping) return true;
                return false;
            }
        }

        public BlendTree() { }

        public BlendTree(string name, BlendTreeType type)
        {
            Name = name;
            Type = type;
        }

        // ---------------------------------------------------------------------
        // Child management
        // ---------------------------------------------------------------------

        public BlendTreeChild AddChild(IMotion motion, float threshold)
        {
            if (motion == null) throw new ArgumentNullException(nameof(motion));
            var child = new BlendTreeChild(motion, threshold);
            Children.Add(child);
            return child;
        }

        public BlendTreeChild AddChild(IMotion motion, Vector2 position2D)
        {
            if (motion == null) throw new ArgumentNullException(nameof(motion));
            var child = new BlendTreeChild(motion, position2D);
            Children.Add(child);
            return child;
        }

        // ---------------------------------------------------------------------
        // Weight computation
        // ---------------------------------------------------------------------

        /// <summary>
        /// Recompute per-child weights from the current parameter values. After this call
        /// each <see cref="BlendTreeChild.Weight"/> holds a value in [0,1] and the weights
        /// sum to 1 (unless no children contribute, in which case all are zero).
        /// </summary>
        public void ComputeWeights()
        {
            foreach (var c in Children) c.Weight = 0f;
            if (Children.Count == 0) return;

            switch (Type)
            {
                case BlendTreeType.Simple1D:
                    ComputeWeights1D(ParameterValueX);
                    break;
                case BlendTreeType.SimpleDirectional2D:
                case BlendTreeType.FreeformDirectional2D:
                case BlendTreeType.FreeformCartesian2D:
                    ComputeWeights2D(new Vector2(ParameterValueX, ParameterValueY));
                    break;
            }
        }

        private void ComputeWeights1D(float x)
        {
            if (Children.Count == 1)
            {
                Children[0].Weight = 1f;
                return;
            }

            // Sort a copy of indices by threshold for neighbour lookup.
            var order = new int[Children.Count];
            for (var i = 0; i < order.Length; i++) order[i] = i;
            Array.Sort(order, (a, b) => Children[a].Threshold.CompareTo(Children[b].Threshold));

            var first = order[0];
            var last = order[^1];

            if (x <= Children[first].Threshold)
            {
                Children[first].Weight = 1f;
                return;
            }
            if (x >= Children[last].Threshold)
            {
                Children[last].Weight = 1f;
                return;
            }

            for (var i = 0; i < order.Length - 1; i++)
            {
                var a = order[i];
                var b = order[i + 1];
                var ta = Children[a].Threshold;
                var tb = Children[b].Threshold;
                if (x >= ta && x <= tb)
                {
                    var span = tb - ta;
                    var w = span <= 0f ? 0f : (x - ta) / span;
                    Children[a].Weight = 1f - w;
                    Children[b].Weight = w;
                    return;
                }
            }
        }

        private void ComputeWeights2D(Vector2 p)
        {
            // Build the list of child positions.
            var n = Children.Count;
            if (n == 1)
            {
                Children[0].Weight = 1f;
                return;
            }
            if (n == 2)
            {
                ProjectOntoSegment(0, 1, p);
                return;
            }

            // Try to find a triangle (i, j, k) that contains p via barycentric coordinates.
            for (var i = 0; i < n - 2; i++)
            for (var j = i + 1; j < n - 1; j++)
            for (var k = j + 1; k < n; k++)
            {
                if (Barycentric(Children[i].Position2D, Children[j].Position2D, Children[k].Position2D, p,
                        out var u, out var v, out var w)
                    && u >= -1e-5f && v >= -1e-5f && w >= -1e-5f)
                {
                    Children[i].Weight = u;
                    Children[j].Weight = v;
                    Children[k].Weight = w;
                    return;
                }
            }

            // Outside every triangle: project onto the nearest edge.
            int bestA = 0, bestB = 1;
            float bestDist = float.MaxValue;
            float bestU = 0f;
            for (var i = 0; i < n - 1; i++)
            for (var j = i + 1; j < n; j++)
            {
                var d = DistanceToSegment(Children[i].Position2D, Children[j].Position2D, p, out var u);
                if (d < bestDist)
                {
                    bestDist = d;
                    bestA = i;
                    bestB = j;
                    bestU = u;
                }
            }
            Children[bestA].Weight = 1f - bestU;
            Children[bestB].Weight = bestU;
        }

        private void ProjectOntoSegment(int a, int b, Vector2 p)
        {
            var d = DistanceToSegment(Children[a].Position2D, Children[b].Position2D, p, out var u);
            Children[a].Weight = 1f - u;
            Children[b].Weight = u;
        }

        private static float DistanceToSegment(Vector2 a, Vector2 b, Vector2 p, out float t)
        {
            var ab = b - a;
            var lenSq = ab.LengthSquared();
            if (lenSq <= 1e-8f)
            {
                t = 0f;
                return Vector2.Distance(p, a);
            }
            t = Math.Clamp(Vector2.Dot(p - a, ab) / lenSq, 0f, 1f);
            var proj = a + ab * t;
            return Vector2.Distance(p, proj);
        }

        private static bool Barycentric(Vector2 a, Vector2 b, Vector2 c, Vector2 p,
                                        out float u, out float v, out float w)
        {
            var v0 = b - a;
            var v1 = c - a;
            var v2 = p - a;
            var d00 = Vector2.Dot(v0, v0);
            var d01 = Vector2.Dot(v0, v1);
            var d11 = Vector2.Dot(v1, v1);
            var d20 = Vector2.Dot(v2, v0);
            var d21 = Vector2.Dot(v2, v1);
            var denom = d00 * d11 - d01 * d01;
            if (MathF.Abs(denom) <= 1e-8f)
            {
                u = v = w = 0f;
                return false;
            }
            v = (d11 * d20 - d01 * d21) / denom;
            w = (d00 * d21 - d01 * d20) / denom;
            u = 1f - v - w;
            return true;
        }

        // ---------------------------------------------------------------------
        // Sampling
        // ---------------------------------------------------------------------

        /// <summary>
        /// Sample the blend tree at an absolute time. Children are sampled at a normalised
        /// phase derived from the tree's effective length so that clips of differing
        /// durations stay in sync. The resulting poses are blended by the computed weights.
        /// </summary>
        public SkeletonPose Sample(Skeleton skeleton, float timeSeconds)
        {
            if (skeleton == null) throw new ArgumentNullException(nameof(skeleton));
            ComputeWeights();

            var boneCount = skeleton.Bones.Count;
            if (Children.Count == 0 || boneCount == 0)
                return skeleton.CreateBindPose();

            float length = 0f;
            foreach (var c in Children)
                if (c.Motion != null) length = MathF.Max(length, c.Motion.Length);
            if (length <= 0f) length = 1f;

            var looping = ((IMotion)this).IsLooping;
            var phase = timeSeconds / length;
            if (looping)
            {
                phase %= 1f;
                if (phase < 0f) phase += 1f;
            }
            else
            {
                phase = Math.Clamp(phase, 0f, 1f);
            }

            // Accumulators for weighted blend. Rotations are summed in quaternion space
            // (with shortest-path sign correction) and normalised at the end; this gives a
            // correct n-way blend for any number of contributing children.
            var transSum = new Vector3[boneCount];
            var scaleSum = new Vector3[boneCount];
            var rotSum = new Quaternion[boneCount];
            var reference = new Quaternion[boneCount];
            var hasReference = new bool[boneCount];
            float totalWeight = 0f;

            foreach (var child in Children)
            {
                if (child.Motion == null) continue;
                var w = Math.Clamp(child.Weight, 0f, 1f);
                if (w <= 0f) continue;

                var childTime = phase * child.Motion.Length;
                var childPose = child.Motion.Sample(skeleton, childTime);

                for (var i = 0; i < boneCount; i++)
                {
                    transSum[i] += childPose.GetTranslationOffset(i) * w;
                    scaleSum[i] += childPose.GetScaleOffset(i) * w;

                    var q = childPose.GetRotation(i);
                    if (!hasReference[i])
                    {
                        reference[i] = q;
                        hasReference[i] = true;
                    }
                    else if (Quaternion.Dot(reference[i], q) < 0f)
                    {
                        q = -q;
                    }
                    rotSum[i] += q * w;
                }
                totalWeight += w;
            }

            var result = skeleton.CreateBindPose();
            if (totalWeight <= 0f)
                return result;

            var inv = 1f / totalWeight;
            for (var i = 0; i < boneCount; i++)
            {
                result.SetTranslationOffset(i, transSum[i] * inv);
                result.SetScaleOffset(i, scaleSum[i] * inv);
                var r = rotSum[i];
                var lenSq = r.X * r.X + r.Y * r.Y + r.Z * r.Z + r.W * r.W;
                if (lenSq > 1e-10f)
                {
                    var invLen = 1f / MathF.Sqrt(lenSq);
                    r *= invLen;
                }
                else
                {
                    r = skeleton.Bones[i].LocalRotation;
                }
                result.SetRotation(i, r);
            }
            return result;
        }

        void IMotion.Reset()
        {
            foreach (var c in Children)
                c.Motion?.Reset();
        }

        /// <summary>Return the computed weights for diagnostic/preview purposes.</summary>
        public float[] GetWeights()
        {
            ComputeWeights();
            var w = new float[Children.Count];
            for (var i = 0; i < Children.Count; i++) w[i] = Children[i].Weight;
            return w;
        }
    }
}
