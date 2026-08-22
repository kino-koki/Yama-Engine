// AnimGraphEvaluatorReal.cs - Real animation graph evaluator for the ShanyuEngine.
//
// Implements a node-based animation graph system:
//   - AnimGraphContext: evaluation context (delta time, current time, component
//     transform, LOD bone count, node cache, parameter table)
//   - AnimGraphNodeReal: abstract base node with input/output pins and Evaluate
//   - Node types: ClipNode, BlendSpace1DNode, BlendSpace2DNode, StateMachineNode,
//     LayeredBlendNode, AdditiveBlendNode, IKNode, TransformBoneNode, SplineNode,
//     RagdollNode, PoseSnapshotNode
//   - AnimGraphInstance: graph instance with node states, parameter values,
//     per-frame node evaluation caching, and Tick
//
// All algorithms use System.Numerics with mathematically correct implementations.

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace ShanyuEngine.Animation
{
    // ========================================================================
    // AnimGraphNodeRealType - types of graph nodes
    // ========================================================================

    /// <summary>Type of animation graph node.</summary>
    public enum AnimGraphNodeRealType
    {
        Clip,
        BlendSpace1D,
        BlendSpace2D,
        StateMachine,
        LayeredBlend,
        AdditiveBlend,
        IK,
        TransformBone,
        Spline,
        Ragdoll,
        PoseSnapshot,
        Output,
        Input
    }

    // ========================================================================
    // AnimGraphContext - evaluation context
    // ========================================================================

    /// <summary>
    /// Evaluation context passed through the animation graph during evaluation.
    /// Carries delta time, current time, component transform, LOD settings,
    /// a per-frame node cache, and the parameter table.
    /// </summary>
    public class AnimGraphContext
    {
        /// <summary>Delta time for this frame (seconds).</summary>
        public float DeltaTime { get; set; }

        /// <summary>Accumulated current time (seconds).</summary>
        public float CurrentTime { get; set; }

        /// <summary>Component (actor) world transform.</summary>
        public Matrix4x4 ComponentTransform { get; set; } = Matrix4x4.Identity;

        /// <summary>
        /// LOD bone count. Only the first N bones are evaluated when LOD is active.
        /// 0 or negative means all bones.
        /// </summary>
        public int LODBoneCount { get; set; } = -1;

        /// <summary>Skeleton reference.</summary>
        public Skeleton Skeleton { get; set; }

        /// <summary>Parameter values (shared with the graph instance).</summary>
        public Dictionary<string, float> Parameters { get; } = new Dictionary<string, float>();

        /// <summary>Bool parameter values.</summary>
        public Dictionary<string, bool> BoolParameters { get; } = new Dictionary<string, bool>();

        /// <summary>Per-frame node evaluation cache: node ID -> evaluated pose.</summary>
        public Dictionary<int, SkeletonPose> NodeCache { get; } = new Dictionary<int, SkeletonPose>();

        /// <summary>Frame counter for cache invalidation.</summary>
        public int FrameCounter { get; set; }

        /// <summary>Get a float parameter by name.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float GetParameter(string name)
        {
            return Parameters.TryGetValue(name, out float v) ? v : 0f;
        }

        /// <summary>Set a float parameter.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetParameter(string name, float value)
        {
            Parameters[name] = value;
        }

        /// <summary>Get a bool parameter by name.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool GetBoolParameter(string name)
        {
            return BoolParameters.TryGetValue(name, out bool v) && v;
        }

        /// <summary>Set a bool parameter.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetBoolParameter(string name, bool value)
        {
            BoolParameters[name] = value;
        }

        /// <summary>Clear the per-frame node cache.</summary>
        public void ClearCache()
        {
            NodeCache.Clear();
        }

        /// <summary>Check if a node has been cached this frame.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsCached(int nodeId)
        {
            return NodeCache.ContainsKey(nodeId);
        }

        /// <summary>Get a cached pose for a node.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public SkeletonPose GetCached(int nodeId)
        {
            return NodeCache.TryGetValue(nodeId, out var pose) ? pose : null;
        }

        /// <summary>Store a pose in the cache for a node.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void SetCached(int nodeId, SkeletonPose pose)
        {
            NodeCache[nodeId] = pose;
        }
    }

    // ========================================================================
    // AnimGraphPin - input/output pin for graph nodes
    // ========================================================================

    /// <summary>
    /// A pin on a graph node. Input pins receive poses from connected output pins.
    /// Output pins provide the node's evaluated pose to downstream nodes.
    /// </summary>
    public class AnimGraphPin
    {
        public string Name { get; set; }
        public int NodeId { get; set; }
        public int ConnectedNodeId { get; set; } = -1;

        public AnimGraphPin() { }

        public AnimGraphPin(string name, int nodeId)
        {
            Name = name;
            NodeId = nodeId;
        }
    }

    // ========================================================================
    // AnimGraphNodeReal - abstract base class for graph nodes
    // ========================================================================

    /// <summary>
    /// Abstract base class for all animation graph nodes. Each node has an ID,
    /// a type, input pins, an output pin, and an Evaluate method that produces
    /// a SkeletonPose given an AnimGraphContext.
    /// </summary>
    public abstract class AnimGraphNodeReal
    {
        /// <summary>Unique node ID within the graph.</summary>
        public int Id { get; set; }

        /// <summary>Node name (for editor display).</summary>
        public string Name { get; set; } = "Node";

        /// <summary>Node type.</summary>
        public AnimGraphNodeRealType NodeType { get; protected set; }

        /// <summary>Input pins (poses from upstream nodes).</summary>
        public List<AnimGraphPin> InputPins { get; } = new List<AnimGraphPin>();

        /// <summary>Output pin (pose for downstream nodes).</summary>
        public AnimGraphPin OutputPin { get; set; }

        protected AnimGraphNodeReal()
        {
            OutputPin = new AnimGraphPin("Output", -1);
        }

        /// <summary>
        /// Evaluate this node and return a SkeletonPose. The result is cached
        /// per frame in the context to avoid re-evaluation.
        /// </summary>
        public SkeletonPose Evaluate(AnimGraphContext context)
        {
            // Check cache first
            if (context.IsCached(Id))
                return context.GetCached(Id);

            // Evaluate this node
            SkeletonPose pose = EvaluateInternal(context);

            // Cache the result
            context.SetCached(Id, pose);
            return pose;
        }

        /// <summary>Internal evaluation logic (implemented by subclasses).</summary>
        protected abstract SkeletonPose EvaluateInternal(AnimGraphContext context);

        /// <summary>
        /// Get the pose from a connected input pin. If no connection, returns
        /// the skeleton's bind pose.
        /// </summary>
        protected SkeletonPose GetInputPose(int pinIndex, AnimGraphContext context)
        {
            if (pinIndex < 0 || pinIndex >= InputPins.Count) return null;
            int connectedId = InputPins[pinIndex].ConnectedNodeId;
            if (connectedId < 0) return null;
            return context.GetCached(connectedId);
        }

        /// <summary>Reset the node's internal state.</summary>
        public virtual void Reset() { }
    }

    // ========================================================================
    // ClipGraphNode - samples an animation clip
    // ========================================================================

    /// <summary>
    /// Clip graph node: samples an AnimationClipReal at the current playback time.
    /// Playback time advances by DeltaTime * PlaybackSpeed each frame.
    /// </summary>
    public class ClipGraphNode : AnimGraphNodeReal
    {
        public AnimationClipReal Clip { get; set; }
        public float PlaybackSpeed { get; set; } = 1f;
        public float CurrentTime { get; set; }
        public bool Loop { get; set; } = true;

        public ClipGraphNode()
        {
            NodeType = AnimGraphNodeRealType.Clip;
        }

        protected override SkeletonPose EvaluateInternal(AnimGraphContext context)
        {
            if (Clip == null || context.Skeleton == null)
                return context.Skeleton?.CreateBindPose();

            // Advance playback time
            CurrentTime += context.DeltaTime * PlaybackSpeed;

            // Handle looping / clamping
            float duration = Clip.Duration;
            if (duration > 0f)
            {
                if (Loop || Clip.Loop)
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
            return pose;
        }

        public override void Reset()
        {
            CurrentTime = 0f;
        }
    }

    // ========================================================================
    // BlendSpace1DGraphNode - 1D blend space graph node
    // ========================================================================

    /// <summary>
    /// 1D blend space graph node. Delegates to a BlendSpace1D blend node.
    /// Reads the axis value from a graph parameter.
    /// </summary>
    public class BlendSpace1DGraphNode : AnimGraphNodeReal
    {
        public BlendSpace1D BlendSpace { get; set; }
        public string ParameterName { get; set; } = "Speed";

        public BlendSpace1DGraphNode()
        {
            NodeType = AnimGraphNodeRealType.BlendSpace1D;
            BlendSpace = new BlendSpace1D();
            BlendSpace.ParameterName = ParameterName;
        }

        protected override SkeletonPose EvaluateInternal(AnimGraphContext context)
        {
            if (BlendSpace == null || context.Skeleton == null)
                return context.Skeleton?.CreateBindPose();

            // Build a BlendContext from the graph context
            var blendCtx = new BlendContext
            {
                DeltaTime = context.DeltaTime,
                CurrentTime = context.CurrentTime,
                Skeleton = context.Skeleton
            };
            blendCtx.SetParameter(ParameterName, context.GetParameter(ParameterName));

            return BlendSpace.Evaluate(blendCtx);
        }

        public override void Reset()
        {
            BlendSpace?.Reset();
        }
    }

    // ========================================================================
    // BlendSpace2DGraphNode - 2D blend space graph node
    // ========================================================================

    /// <summary>
    /// 2D blend space graph node. Delegates to a BlendSpace2D blend node.
    /// Reads X and Y axis values from graph parameters.
    /// </summary>
    public class BlendSpace2DGraphNode : AnimGraphNodeReal
    {
        public BlendSpace2D BlendSpace { get; set; }
        public string ParameterX { get; set; } = "MoveX";
        public string ParameterY { get; set; } = "MoveY";

        public BlendSpace2DGraphNode()
        {
            NodeType = AnimGraphNodeRealType.BlendSpace2D;
            BlendSpace = new BlendSpace2D();
            BlendSpace.ParameterX = ParameterX;
            BlendSpace.ParameterY = ParameterY;
        }

        protected override SkeletonPose EvaluateInternal(AnimGraphContext context)
        {
            if (BlendSpace == null || context.Skeleton == null)
                return context.Skeleton?.CreateBindPose();

            var blendCtx = new BlendContext
            {
                DeltaTime = context.DeltaTime,
                CurrentTime = context.CurrentTime,
                Skeleton = context.Skeleton
            };
            blendCtx.SetParameter(ParameterX, context.GetParameter(ParameterX));
            blendCtx.SetParameter(ParameterY, context.GetParameter(ParameterY));

            return BlendSpace.Evaluate(blendCtx);
        }

        public override void Reset()
        {
            BlendSpace?.Reset();
        }
    }

    // ========================================================================
    // StateMachineGraphNode - state machine graph node
    // ========================================================================

    /// <summary>
    /// State machine graph node. Wraps an AnimStateMachineReal. Ticks the
    /// state machine each frame and returns the current blended pose.
    /// </summary>
    public class StateMachineGraphNode : AnimGraphNodeReal
    {
        public AnimStateMachineReal StateMachine { get; set; }

        public StateMachineGraphNode()
        {
            NodeType = AnimGraphNodeRealType.StateMachine;
        }

        protected override SkeletonPose EvaluateInternal(AnimGraphContext context)
        {
            if (StateMachine == null || context.Skeleton == null)
                return context.Skeleton?.CreateBindPose();

            // Sync parameters from the graph context to the state machine
            SyncParameters(context);

            // Tick the state machine
            StateMachine.Tick(context.DeltaTime);

            // Get the current pose
            return StateMachine.GetCurrentPose(context.Skeleton);
        }

        /// <summary>Sync float parameters from the graph context to the state machine.</summary>
        private void SyncParameters(AnimGraphContext context)
        {
            foreach (var kv in context.Parameters)
            {
                var param = StateMachine.GetParameter(kv.Key);
                if (param != null && param.Type == AnimParameterType.Float)
                    param.FloatValue = kv.Value;
            }

            foreach (var kv in context.BoolParameters)
            {
                var param = StateMachine.GetParameter(kv.Key);
                if (param != null && param.Type == AnimParameterType.Bool)
                    param.BoolValue = kv.Value;
            }
        }

        public override void Reset()
        {
            StateMachine?.Reset();
        }
    }

    // ========================================================================
    // LayeredBlendGraphNode - layered blend graph node
    // ========================================================================

    /// <summary>
    /// Layered blend graph node. Delegates to a LayeredBlendNode. Input pins
    /// provide the base layer and overlay layer poses.
    /// </summary>
    public class LayeredBlendGraphNode : AnimGraphNodeReal
    {
        public LayeredBlendNode BlendNode { get; set; }

        public LayeredBlendGraphNode()
        {
            NodeType = AnimGraphNodeRealType.LayeredBlend;
            BlendNode = new LayeredBlendNode();
        }

        protected override SkeletonPose EvaluateInternal(AnimGraphContext context)
        {
            if (BlendNode == null || context.Skeleton == null)
                return context.Skeleton?.CreateBindPose();

            var blendCtx = new BlendContext
            {
                DeltaTime = context.DeltaTime,
                CurrentTime = context.CurrentTime,
                Skeleton = context.Skeleton
            };

            return BlendNode.Evaluate(blendCtx);
        }

        public override void Reset()
        {
            BlendNode?.Reset();
        }
    }

    // ========================================================================
    // AdditiveBlendGraphNode - additive blend graph node
    // ========================================================================

    /// <summary>
    /// Additive blend graph node. Delegates to an AdditiveBlendNode.
    /// Reads the blend weight from a graph parameter.
    /// </summary>
    public class AdditiveBlendGraphNode : AnimGraphNodeReal
    {
        public AdditiveBlendNode BlendNode { get; set; }
        public string WeightParameter { get; set; }

        public AdditiveBlendGraphNode()
        {
            NodeType = AnimGraphNodeRealType.AdditiveBlend;
            BlendNode = new AdditiveBlendNode();
        }

        protected override SkeletonPose EvaluateInternal(AnimGraphContext context)
        {
            if (BlendNode == null || context.Skeleton == null)
                return context.Skeleton?.CreateBindPose();

            // Read weight from parameter if specified
            if (!string.IsNullOrEmpty(WeightParameter))
                BlendNode.Weight = context.GetParameter(WeightParameter);

            var blendCtx = new BlendContext
            {
                DeltaTime = context.DeltaTime,
                CurrentTime = context.CurrentTime,
                Skeleton = context.Skeleton
            };

            return BlendNode.Evaluate(blendCtx);
        }

        public override void Reset()
        {
            BlendNode?.Reset();
        }
    }

    // ========================================================================
    // IKGraphNode - IK solver graph node
    // ========================================================================

    /// <summary>
    /// IK graph node. Applies an IK solver to the input pose.
    /// Reads the IK target position and weight from graph parameters.
    /// </summary>
    public class IKGraphNode : AnimGraphNodeReal
    {

        public IKSolverType SolverType { get; set; } = IKSolverType.TwoBone;
        public TwoBoneIKSolverReal TwoBoneSolver { get; set; } = new TwoBoneIKSolverReal();
        public LookAtIKSolver LookAtSolver { get; set; } = new LookAtIKSolver();
        public FABRIKSolverReal FABRIKSolver { get; set; } = new FABRIKSolverReal();
        public CCDIKSolverReal CCDSolver { get; set; } = new CCDIKSolverReal();
        public LimbIKSolver LimbSolver { get; set; } = new LimbIKSolver();
        public SpringIKSolver SpringSolver { get; set; } = new SpringIKSolver();

        /// <summary>Parameter name for the IK target position (X, Y, Z as separate params or combined).</summary>
        public string TargetXParam { get; set; }
        public string TargetYParam { get; set; }
        public string TargetZParam { get; set; }

        /// <summary>Parameter name for the IK weight.</summary>
        public string WeightParam { get; set; }

        public IKGraphNode()
        {
            NodeType = AnimGraphNodeRealType.IK;
        }

        protected override SkeletonPose EvaluateInternal(AnimGraphContext context)
        {
            // Get the input pose (from pin 0)
            SkeletonPose inputPose = GetInputPose(0, context);
            if (inputPose == null || context.Skeleton == null)
                return context.Skeleton?.CreateBindPose();

            // Clone the pose so we don't modify the cached input
            inputPose = inputPose.Clone();

            // Read target position from parameters
            Vector3 target = Vector3.Zero;
            if (!string.IsNullOrEmpty(TargetXParam))
                target.X = context.GetParameter(TargetXParam);
            if (!string.IsNullOrEmpty(TargetYParam))
                target.Y = context.GetParameter(TargetYParam);
            if (!string.IsNullOrEmpty(TargetZParam))
                target.Z = context.GetParameter(TargetZParam);

            // Read weight from parameter
            float weight = 1f;
            if (!string.IsNullOrEmpty(WeightParam))
                weight = context.GetParameter(WeightParam);

            // Apply the selected IK solver
            switch (SolverType)
            {
                case IKSolverType.TwoBone:
                    TwoBoneSolver.Weight = weight;
                    TwoBoneSolver.Solve(inputPose, target, context.ComponentTransform);
                    break;

                case IKSolverType.LookAt:
                    LookAtSolver.Target = target;
                    LookAtSolver.Weight = weight;
                    LookAtSolver.Solve(inputPose, context.ComponentTransform);
                    break;

                case IKSolverType.FABRIK:
                    FABRIKSolver.Target = target;
                    FABRIKSolver.Weight = weight;
                    FABRIKSolver.Solve(inputPose, context.ComponentTransform);
                    break;

                case IKSolverType.CCD:
                    CCDSolver.Target = target;
                    CCDSolver.Weight = weight;
                    CCDSolver.Solve(inputPose, context.ComponentTransform);
                    break;

                case IKSolverType.Limb:
                    LimbSolver.TargetPosition = target;
                    LimbSolver.PositionWeight = weight;
                    LimbSolver.Solve(inputPose, context.ComponentTransform);
                    break;

                case IKSolverType.Spring:
                    SpringSolver.Target = target;
                    SpringSolver.Weight = weight;
                    SpringSolver.Solve(inputPose, context.ComponentTransform);
                    break;
            }

            return inputPose;
        }

        public override void Reset()
        {
            SpringSolver?.Reset();
        }
    }

    // ========================================================================
    // TransformBoneGraphNode - modify specific bones
    // ========================================================================

    /// <summary>
    /// Transform bone graph node. Applies a transform (position/rotation/scale)
    /// to specific bones in the input pose. Useful for procedural bone adjustments
    /// like head tracking, weapon alignment, etc.
    /// </summary>
    public class TransformBoneGraphNode : AnimGraphNodeReal
    {
        /// <summary>A bone transform entry.</summary>
        public struct BoneTransformEntry
        {
            public int BoneIndex;
            public Vector3 PositionDelta;
            public Quaternion RotationDelta;
            public Vector3 ScaleDelta;
            public float Weight;
        }

        public List<BoneTransformEntry> BoneTransforms { get; } = new List<BoneTransformEntry>();

        /// <summary>Parameter name for the overall weight.</summary>
        public string WeightParam { get; set; }

        public TransformBoneGraphNode()
        {
            NodeType = AnimGraphNodeRealType.TransformBone;
        }

        protected override SkeletonPose EvaluateInternal(AnimGraphContext context)
        {
            SkeletonPose inputPose = GetInputPose(0, context);
            if (inputPose == null || context.Skeleton == null)
                return context.Skeleton?.CreateBindPose();

            inputPose = inputPose.Clone();

            float overallWeight = 1f;
            if (!string.IsNullOrEmpty(WeightParam))
                overallWeight = context.GetParameter(WeightParam);

            foreach (var entry in BoneTransforms)
            {
                if (entry.BoneIndex < 0 || entry.BoneIndex >= inputPose.BoneCount)
                    continue;

                float w = entry.Weight * overallWeight;
                if (w <= 0f) continue;

                ref BonePose bp = ref inputPose.BonePoses[entry.BoneIndex];

                // Apply position delta (blended by weight)
                bp.Position += entry.PositionDelta * w;

                // Apply rotation delta (SLerp by weight)
                Quaternion rotDelta = KeyframeInterpolation.InterpolateQuaternionSlerp(
                    Quaternion.Identity, entry.RotationDelta, w);
                bp.Rotation = Quaternion.Normalize(rotDelta * bp.Rotation);

                // Apply scale delta
                Vector3 scaleMult = Vector3.Lerp(Vector3.One, entry.ScaleDelta, w);
                bp.Scale *= scaleMult;

                inputPose.BonePoses[entry.BoneIndex] = bp;
            }

            return inputPose;
        }
    }

    // ========================================================================
    // SplineGraphNode - distribute bones along a spline
    // ========================================================================

    /// <summary>
    /// Spline graph node. Distributes a chain of bones along a Catmull-Rom spline.
    /// The spline is defined by control points read from parameters or set directly.
    /// </summary>
    public class SplineGraphNode : AnimGraphNodeReal
    {
        /// <summary>Control points defining the Catmull-Rom spline.</summary>
        public List<Vector3> ControlPoints { get; } = new List<Vector3>();

        /// <summary>Bone indices to distribute along the spline (root to tip).</summary>
        public List<int> BoneChain { get; } = new List<int>();

        /// <summary>Tension parameter for the spline (0 = uniform, higher = tighter).</summary>
        public float Tension { get; set; } = 0.5f;

        public SplineGraphNode()
        {
            NodeType = AnimGraphNodeRealType.Spline;
        }

        protected override SkeletonPose EvaluateInternal(AnimGraphContext context)
        {
            SkeletonPose inputPose = GetInputPose(0, context);
            if (inputPose == null || context.Skeleton == null)
                return context.Skeleton?.CreateBindPose();

            if (ControlPoints.Count < 2 || BoneChain.Count < 2)
                return inputPose;

            inputPose = inputPose.Clone();

            // Sample positions along the Catmull-Rom spline
            int n = BoneChain.Count;
            for (int i = 0; i < n; i++)
            {
                float t = n > 1 ? (float)i / (n - 1) : 0f;
                Vector3 pos = SampleCatmullRom(ControlPoints, t, Tension);

                int boneIdx = BoneChain[i];
                if (boneIdx < 0 || boneIdx >= inputPose.BoneCount) continue;

                ref BonePose bp = ref inputPose.BonePoses[boneIdx];
                bp.Position = pos;

                // Orient bone towards the next point on the spline
                if (i < n - 1)
                {
                    float tNext = n > 1 ? (float)(i + 1) / (n - 1) : 0f;
                    Vector3 nextPos = SampleCatmullRom(ControlPoints, tNext, Tension);
                    Vector3 dir = nextPos - pos;
                    float dirLen = dir.Length();
                    if (dirLen > 1e-6f)
                    {
                        dir /= dirLen;
                        // Create a rotation that points the bone's forward axis (+Z) towards dir
                        Vector3 forward = Vector3.UnitZ;
                        float dot = Vector3.Dot(forward, dir);
                        dot = Math.Clamp(dot, -1f, 1f);

                        if (dot < 0.99999f)
                        {
                            Vector3 axis = Vector3.Cross(forward, dir);
                            float axisLen = axis.Length();
                            if (axisLen > 1e-6f)
                            {
                                axis /= axisLen;
                                float angle = MathF.Acos(dot);
                                bp.Rotation = Quaternion.CreateFromAxisAngle(axis, angle);
                            }
                        }
                    }
                }

                inputPose.BonePoses[boneIdx] = bp;
            }

            return inputPose;
        }

        /// <summary>
        /// Sample a Catmull-Rom spline at parameter t (0..1).
        /// Uses the centripetal Catmull-Rom formulation for smooth curves.
        /// </summary>
        private static Vector3 SampleCatmullRom(List<Vector3> points, float t, float tension)
        {
            int n = points.Count;
            if (n == 0) return Vector3.Zero;
            if (n == 1) return points[0];

            // Map t to the spline segment
            float scaledT = t * (n - 1);
            int seg = Math.Clamp((int)scaledT, 0, n - 2);
            float localT = scaledT - seg;

            // Get the four control points for this segment
            Vector3 p0 = points[Math.Max(0, seg - 1)];
            Vector3 p1 = points[seg];
            Vector3 p2 = points[seg + 1];
            Vector3 p3 = points[Math.Min(n - 1, seg + 2)];

            // Catmull-Rom interpolation
            float t2 = localT * localT;
            float t3 = t2 * localT;

            float a = tension;
            float b = 1f - tension;

            Vector3 result = 0.5f * (
                2f * p1 +
                (-p0 + p2) * localT / a +
                (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 / a +
                (-p0 + 3f * p1 - 3f * p2 + p3) * t3 / a
            );

            return result;
        }
    }

    // ========================================================================
    // RagdollGraphNode - physics-based ragdoll
    // ========================================================================

    /// <summary>
    /// Ragdoll graph node. Simulates a simple physics ragdoll by applying
    /// gravity and velocity to bones, with distance constraints to maintain
    /// bone lengths. Blends between the input (animated) pose and the physics
    /// pose by a blend weight.
    /// </summary>
    public class RagdollGraphNode : AnimGraphNodeReal
    {
        /// <summary>Bone indices in the ragdoll chain (root to extremities).</summary>
        public List<int> BoneChain { get; } = new List<int>();

        /// <summary>Gravity acceleration.</summary>
        public Vector3 Gravity { get; set; } = new Vector3(0, -9.81f, 0);

        /// <summary>Damping coefficient.</summary>
        public float Damping { get; set; } = 0.5f;

        /// <summary>Blend weight between animated pose (0) and physics pose (1).</summary>
        public float BlendWeight { get; set; } = 1f;

        /// <summary>Parameter name for the blend weight.</summary>
        public string BlendWeightParam { get; set; }

        // Runtime state: positions and velocities for each bone in the chain
        private Vector3[] _positions;
        private Vector3[] _velocities;
        private float[] _restLengths;
        private bool _initialized;

        public RagdollGraphNode()
        {
            NodeType = AnimGraphNodeRealType.Ragdoll;
        }

        protected override SkeletonPose EvaluateInternal(AnimGraphContext context)
        {
            SkeletonPose inputPose = GetInputPose(0, context);
            if (inputPose == null || context.Skeleton == null || BoneChain.Count < 2)
                return inputPose ?? context.Skeleton?.CreateBindPose();

            inputPose = inputPose.Clone();

            float weight = BlendWeight;
            if (!string.IsNullOrEmpty(BlendWeightParam))
                weight = context.GetParameter(BlendWeightParam);

            // If weight is 0, just return the input pose
            if (weight <= 0f)
                return inputPose;

            // Get current world positions
            Matrix4x4[] worldMats = context.Skeleton.ComputeWorldMatrices(inputPose);
            int n = BoneChain.Count;

            if (!_initialized)
            {
                _positions = new Vector3[n];
                _velocities = new Vector3[n];
                _restLengths = new float[n - 1];
                for (int i = 0; i < n; i++)
                {
                    _positions[i] = worldMats[BoneChain[i]].Translation;
                    _velocities[i] = Vector3.Zero;
                }
                for (int i = 0; i < n - 1; i++)
                    _restLengths[i] = Vector3.Distance(_positions[i], _positions[i + 1]);
                _initialized = true;
            }

            // Physics simulation
            float dt = context.DeltaTime;
            if (dt > 0f)
            {
                // Apply gravity and integrate
                for (int i = 1; i < n; i++) // Skip root (pinned)
                {
                    _velocities[i] += Gravity * dt;
                    _velocities[i] *= (1f - Damping * dt);
                    _positions[i] += _velocities[i] * dt;
                }

                // Pin the root to the animated position
                _positions[0] = worldMats[BoneChain[0]].Translation;
                _velocities[0] = Vector3.Zero;

                // Satisfy distance constraints (position-based dynamics)
                for (int iter = 0; iter < 5; iter++)
                {
                    for (int i = 0; i < n - 1; i++)
                    {
                        Vector3 delta = _positions[i + 1] - _positions[i];
                        float dist = delta.Length();
                        if (dist < 1e-8f) continue;

                        float diff = (dist - _restLengths[i]) / dist;
                        float w0 = (i == 0) ? 0f : 0.5f;
                        float w1 = 0.5f;
                        float wSum = w0 + w1;
                        if (wSum < 1e-8f) continue;

                        _positions[i] += delta * diff * (w0 / wSum);
                        _positions[i + 1] -= delta * diff * (w1 / wSum);
                    }
                }
            }

            // Convert physics positions to bone rotations
            for (int i = 0; i < n - 1; i++)
            {
                int boneIdx = BoneChain[i];
                if (boneIdx < 0 || boneIdx >= worldMats.Length) continue;

                // Old direction (from animated pose)
                Vector3 oldDir = worldMats[BoneChain[i + 1]].Translation - worldMats[boneIdx].Translation;
                // New direction (from physics)
                Vector3 newDir = _positions[i + 1] - _positions[i];

                float oldLen = oldDir.Length();
                float newLen = newDir.Length();
                if (oldLen < 1e-6f || newLen < 1e-6f) continue;

                oldDir /= oldLen;
                newDir /= newLen;

                float dot = Math.Clamp(Vector3.Dot(oldDir, newDir), -1f, 1f);
                if (dot > 0.99999f) continue;

                Vector3 axis = Vector3.Cross(oldDir, newDir);
                float axisLen = axis.Length();
                if (axisLen < 1e-6f) continue;
                axis /= axisLen;

                float angle = MathF.Acos(dot) * weight;
                Quaternion deltaRot = Quaternion.CreateFromAxisAngle(axis, angle);

                Quaternion worldRot = Quaternion.CreateFromRotationMatrix(worldMats[boneIdx]);
                Quaternion newWorldRot = deltaRot * worldRot;

                int parentIdx = context.Skeleton.Bones[boneIdx].ParentIndex;
                Quaternion parentWorldRot = Quaternion.Identity;
                if (parentIdx >= 0 && parentIdx < worldMats.Length)
                    parentWorldRot = Quaternion.CreateFromRotationMatrix(worldMats[parentIdx]);

                Quaternion localRot = newWorldRot * Quaternion.Inverse(parentWorldRot);
                inputPose.SetRotation(boneIdx, localRot);
            }

            return inputPose;
        }

        public override void Reset()
        {
            _initialized = false;
            _positions = null;
            _velocities = null;
        }
    }

    // ========================================================================
    // PoseSnapshotGraphNode - store and replay a pose
    // ========================================================================

    /// <summary>
    /// Pose snapshot graph node. Captures the current input pose and can
    /// replay it later. Useful for freeze-frame effects, pose blending, or
    /// storing reference poses.
    /// </summary>
    public class PoseSnapshotGraphNode : AnimGraphNodeReal
    {
        /// <summary>The stored snapshot pose.</summary>
        public SkeletonPose Snapshot { get; set; }

        /// <summary>If true, capture a snapshot on the next evaluation.</summary>
        public bool CaptureRequested { get; set; }

        /// <summary>If true, output the snapshot instead of the live input.</summary>
        public bool UseSnapshot { get; set; }

        /// <summary>Blend weight between live input (0) and snapshot (1).</summary>
        public float BlendWeight { get; set; }

        /// <summary>Parameter name for the blend weight.</summary>
        public string BlendWeightParam { get; set; }

        public PoseSnapshotGraphNode()
        {
            NodeType = AnimGraphNodeRealType.PoseSnapshot;
        }

        protected override SkeletonPose EvaluateInternal(AnimGraphContext context)
        {
            SkeletonPose inputPose = GetInputPose(0, context);
            if (inputPose == null || context.Skeleton == null)
                return context.Skeleton?.CreateBindPose();

            // Capture if requested
            if (CaptureRequested)
            {
                Snapshot = inputPose.Clone();
                CaptureRequested = false;
            }

            float weight = BlendWeight;
            if (!string.IsNullOrEmpty(BlendWeightParam))
                weight = context.GetParameter(BlendWeightParam);

            // If using snapshot, return it (blended with input if weight < 1)
            if (UseSnapshot && Snapshot != null)
            {
                if (weight >= 1f) return Snapshot.Clone();
                return SkeletonPose.Blend(inputPose, Snapshot, weight);
            }

            return inputPose.Clone();
        }

        /// <summary>Request a snapshot capture on the next evaluation.</summary>
        public void Capture()
        {
            CaptureRequested = true;
        }

        /// <summary>Clear the stored snapshot.</summary>
        public void ClearSnapshot()
        {
            Snapshot = null;
            UseSnapshot = false;
        }

        public override void Reset()
        {
            Snapshot = null;
            CaptureRequested = false;
            UseSnapshot = false;
        }
    }

    // ========================================================================
    // OutputGraphNode - the final output node
    // ========================================================================

    /// <summary>
    /// Output graph node. This is the root of the evaluation: the graph
    /// instance evaluates this node, which simply returns its input pose.
    /// </summary>
    public class OutputGraphNode : AnimGraphNodeReal
    {
        public OutputGraphNode()
        {
            NodeType = AnimGraphNodeRealType.Output;
            InputPins.Add(new AnimGraphPin("Pose", -1));
        }

        protected override SkeletonPose EvaluateInternal(AnimGraphContext context)
        {
            return GetInputPose(0, context) ?? context.Skeleton?.CreateBindPose();
        }
    }

    // ========================================================================
    // AnimGraphInstance - graph instance with per-node state and Tick
    // ========================================================================

    /// <summary>
    /// Animation graph instance. Holds the graph's node collection, per-node
    /// runtime state, parameter values, and the Tick method that evaluates
    /// the graph from the output node.
    ///
    /// The Tick method:
    ///   1. Clears the per-frame node cache
    ///   2. Evaluates the output node (which recursively evaluates upstream nodes)
    ///   3. Each node is cached so it's only evaluated once per frame
    ///   4. Returns the final output pose
    /// </summary>
    public class AnimGraphInstance
    {
        /// <summary>All nodes in the graph, keyed by ID.</summary>
        public Dictionary<int, AnimGraphNodeReal> Nodes { get; } = new Dictionary<int, AnimGraphNodeReal>();

        /// <summary>The output node ID (root of evaluation).</summary>
        public int OutputNodeId { get; set; } = -1;

        /// <summary>The skeleton for this instance.</summary>
        public Skeleton Skeleton { get; set; }

        /// <summary>Float parameter values.</summary>
        public Dictionary<string, float> Parameters { get; } = new Dictionary<string, float>();

        /// <summary>Bool parameter values.</summary>
        public Dictionary<string, bool> BoolParameters { get; } = new Dictionary<string, bool>();

        /// <summary>The evaluation context.</summary>
        private readonly AnimGraphContext _context = new AnimGraphContext();

        /// <summary>The last evaluated output pose.</summary>
        public SkeletonPose CurrentPose { get; private set; }

        /// <summary>Component (actor) world transform.</summary>
        public Matrix4x4 ComponentTransform { get; set; } = Matrix4x4.Identity;

        /// <summary>LOD bone count (0 or negative = all bones).</summary>
        public int LODBoneCount { get; set; } = -1;

        /// <summary>Add a node to the graph.</summary>
        public void AddNode(AnimGraphNodeReal node)
        {
            if (node == null) return;
            Nodes[node.Id] = node;
            if (node.OutputPin != null)
                node.OutputPin.NodeId = node.Id;
        }

        /// <summary>
        /// Connect an output pin to an input pin.
        /// </summary>
        public void Connect(int sourceNodeId, int targetNodeId, int targetPinIndex = 0)
        {
            if (!Nodes.TryGetValue(targetNodeId, out var targetNode)) return;
            if (targetPinIndex < 0 || targetPinIndex >= targetNode.InputPins.Count) return;
            targetNode.InputPins[targetPinIndex].ConnectedNodeId = sourceNodeId;
        }

        /// <summary>Get a node by ID.</summary>
        public AnimGraphNodeReal GetNode(int nodeId)
        {
            return Nodes.TryGetValue(nodeId, out var node) ? node : null;
        }

        /// <summary>Get the output node.</summary>
        public AnimGraphNodeReal GetOutputNode()
        {
            return OutputNodeId >= 0 ? GetNode(OutputNodeId) : null;
        }

        /// <summary>Set a float parameter.</summary>
        public void SetParameter(string name, float value)
        {
            Parameters[name] = value;
        }

        /// <summary>Set a bool parameter.</summary>
        public void SetBoolParameter(string name, bool value)
        {
            BoolParameters[name] = value;
        }

        /// <summary>Get a float parameter.</summary>
        public float GetParameter(string name)
        {
            return Parameters.TryGetValue(name, out float v) ? v : 0f;
        }

        /// <summary>Get a bool parameter.</summary>
        public bool GetBoolParameter(string name)
        {
            return BoolParameters.TryGetValue(name, out bool v) && v;
        }

        /// <summary>Reset all nodes (clear playback state).</summary>
        public void Reset()
        {
            foreach (var kv in Nodes)
                kv.Value?.Reset();
            CurrentPose = null;
        }

        /// <summary>
        /// Tick the graph: evaluate from the output node, caching each node's
        /// result. Returns the final output pose.
        /// </summary>
        /// <param name="deltaTime">Delta time in seconds.</param>
        /// <returns>The evaluated output pose.</returns>
        public SkeletonPose Tick(float deltaTime)
        {
            if (Skeleton == null) return null;

            var outputNode = GetOutputNode();
            if (outputNode == null)
            {
                CurrentPose = Skeleton.CreateBindPose();
                return CurrentPose;
            }

            // Set up the evaluation context
            _context.DeltaTime = deltaTime;
            _context.CurrentTime += deltaTime;
            _context.ComponentTransform = ComponentTransform;
            _context.LODBoneCount = LODBoneCount;
            _context.Skeleton = Skeleton;
            _context.FrameCounter++;

            // Sync parameters to the context
            _context.Parameters.Clear();
            foreach (var kv in Parameters)
                _context.Parameters[kv.Key] = kv.Value;

            _context.BoolParameters.Clear();
            foreach (var kv in BoolParameters)
                _context.BoolParameters[kv.Key] = kv.Value;

            // Clear the per-frame cache
            _context.ClearCache();

            // Evaluate the output node (recursively evaluates upstream nodes)
            CurrentPose = outputNode.Evaluate(_context);

            // Apply LOD bone count if set
            if (LODBoneCount > 0 && CurrentPose != null && CurrentPose.BoneCount > LODBoneCount)
            {
                CurrentPose = ApplyLOD(CurrentPose, LODBoneCount);
            }

            return CurrentPose;
        }

        /// <summary>
        /// Apply LOD: only keep the first N bones, reset the rest to bind pose.
        /// </summary>
        private SkeletonPose ApplyLOD(SkeletonPose pose, int boneCount)
        {
            var result = pose.Clone();
            for (int i = boneCount; i < result.BoneCount; i++)
            {
                result.BonePoses[i] = BonePose.FromTransform(
                    Skeleton.Bones[i].RestTransform);
            }
            return result;
        }

        /// <summary>
        /// Get the current output pose without re-evaluating.
        /// </summary>
        public SkeletonPose GetCurrentPose()
        {
            return CurrentPose ?? Skeleton?.CreateBindPose();
        }
    }
}
