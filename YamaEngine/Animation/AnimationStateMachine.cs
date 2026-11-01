using System.Numerics;
using YamaEngine.Skinning;

namespace YamaEngine.Animation
{
    /// <summary>The data type stored by an <see cref="AnimatorParameter"/>.</summary>
    public enum AnimatorParameterType
    {
        Bool,
        Float,
        Int,
        Trigger
    }

    /// <summary>
    /// A named, typed value that drives transition conditions. Parameters are shared
    /// across all layers of an <see cref="AnimatorController"/> and can be set from game
    /// code each frame.
    /// </summary>
    public sealed class AnimatorParameter
    {
        public string Name { get; }
        public AnimatorParameterType Type { get; }

        public bool BoolValue { get; set; }
        public float FloatValue { get; set; }
        public int IntValue { get; set; }
        /// <summary>Triggers are boolean-like but auto-reset once consumed by a transition.</summary>
        public bool TriggerValue { get; internal set; }

        public AnimatorParameter(string name, AnimatorParameterType type)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            Type = type;
        }

        public static AnimatorParameter Bool(string name, bool value = false) =>
            new(name, AnimatorParameterType.Bool) { BoolValue = value };

        public static AnimatorParameter Float(string name, float value = 0f) =>
            new(name, AnimatorParameterType.Float) { FloatValue = value };

        public static AnimatorParameter Int(string name, int value = 0) =>
            new(name, AnimatorParameterType.Int) { IntValue = value };

        public static AnimatorParameter Trigger(string name) =>
            new(name, AnimatorParameterType.Trigger);

        public void SetTrigger()
        {
            if (Type == AnimatorParameterType.Trigger) TriggerValue = true;
        }

        public void ResetTrigger()
        {
            if (Type == AnimatorParameterType.Trigger) TriggerValue = false;
        }

        public override string ToString() => $"{Type} {Name} = {(Type == AnimatorParameterType.Bool ? BoolValue : Type == AnimatorParameterType.Int ? IntValue : Type == AnimatorParameterType.Trigger ? TriggerValue : FloatValue)}";
    }

    /// <summary>The comparison applied by an <see cref="AnimatorCondition"/>.</summary>
    public enum AnimatorConditionMode
    {
        /// <summary>True if a bool/trigger parameter is true.</summary>
        If,
        /// <summary>True if a bool/trigger parameter is false.</summary>
        IfNot,
        /// <summary>True if a numeric parameter is greater than the threshold.</summary>
        Greater,
        /// <summary>True if a numeric parameter is less than the threshold.</summary>
        Less,
        /// <summary>True if a numeric parameter equals the threshold.</summary>
        Equals,
        /// <summary>True if a numeric parameter does not equal the threshold.</summary>
        NotEqual
    }

    /// <summary>A single boolean test against a parameter. All conditions on a transition are AND-ed.</summary>
    public sealed class AnimatorCondition
    {
        public string Parameter { get; set; } = string.Empty;
        public AnimatorConditionMode Mode { get; set; }
        public float Threshold { get; set; }

        public AnimatorCondition() { }

        public AnimatorCondition(string parameter, AnimatorConditionMode mode, float threshold = 0f)
        {
            Parameter = parameter;
            Mode = mode;
            Threshold = threshold;
        }

        public bool Evaluate(IReadOnlyDictionary<string, AnimatorParameter> parameters)
        {
            if (string.IsNullOrEmpty(Parameter) || !parameters.TryGetValue(Parameter, out var p))
                return false;

            switch (Mode)
            {
                case AnimatorConditionMode.If:
                    return p.Type == AnimatorParameterType.Bool ? p.BoolValue : p.TriggerValue;
                case AnimatorConditionMode.IfNot:
                    return p.Type == AnimatorParameterType.Bool ? !p.BoolValue : !p.TriggerValue;
                case AnimatorConditionMode.Greater:
                    return p.Type == AnimatorParameterType.Int ? p.IntValue > (int)Threshold : p.FloatValue > Threshold;
                case AnimatorConditionMode.Less:
                    return p.Type == AnimatorParameterType.Int ? p.IntValue < (int)Threshold : p.FloatValue < Threshold;
                case AnimatorConditionMode.Equals:
                    return p.Type == AnimatorParameterType.Int
                        ? p.IntValue == (int)Threshold
                        : MathF.Abs(p.FloatValue - Threshold) < 1e-4f;
                case AnimatorConditionMode.NotEqual:
                    return p.Type == AnimatorParameterType.Int
                        ? p.IntValue != (int)Threshold
                        : MathF.Abs(p.FloatValue - Threshold) >= 1e-4f;
                default:
                    return false;
            }
        }
    }

    /// <summary>
    /// Determines which bones a layer affects. Bones not present in the mask fall back to
    /// <see cref="DefaultPass"/> (true by default, meaning "everything passes" / full body).
    /// </summary>
    public sealed class AvatarMask
    {
        public string Name { get; set; } = "AvatarMask";
        /// <summary>Bone names explicitly enabled. Absent names use <see cref="DefaultPass"/>.</summary>
        public HashSet<string> EnabledBones { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Bone names explicitly disabled. Takes precedence over EnabledBones.</summary>
        public HashSet<string> DisabledBones { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public bool DefaultPass { get; set; } = true;

        public AvatarMask() { }

        public AvatarMask(bool defaultPass)
        {
            DefaultPass = defaultPass;
        }

        public bool GetBonePass(string boneName)
        {
            if (DisabledBones.Contains(boneName)) return false;
            if (EnabledBones.Contains(boneName)) return true;
            return DefaultPass;
        }

        public bool GetBonePass(int boneIndex, Skeleton skeleton)
        {
            if (skeleton == null || boneIndex < 0 || boneIndex >= skeleton.Bones.Count)
                return DefaultPass;
            return GetBonePass(skeleton.Bones[boneIndex].Name);
        }

        public AvatarMask Enable(string boneName)
        {
            EnabledBones.Add(boneName);
            DisabledBones.Remove(boneName);
            return this;
        }

        public AvatarMask Disable(string boneName)
        {
            DisabledBones.Add(boneName);
            EnabledBones.Remove(boneName);
            return this;
        }

        /// <summary>A mask that passes every bone (full body).</summary>
        public static AvatarMask FullBody() => new(defaultPass: true);

        /// <summary>
        /// A heuristic upper-body mask that disables everything at or below the pelvis.
        /// Pass the skeleton and the name of the pelvis/hips bone to seed the disabled set.
        /// </summary>
        public static AvatarMask UpperBody(Skeleton skeleton, string pelvisBoneName)
        {
            var mask = new AvatarMask(defaultPass: true);
            if (skeleton == null || string.IsNullOrEmpty(pelvisBoneName)) return mask;
            var idx = skeleton.FindBone(pelvisBoneName);
            if (idx < 0) return mask;
            // Disable the pelvis and all of its descendants.
            var stack = new Stack<int>();
            stack.Push(idx);
            while (stack.Count > 0)
            {
                var i = stack.Pop();
                mask.Disable(skeleton.Bones[i].Name);
                for (var c = 0; c < skeleton.Bones.Count; c++)
                    if (skeleton.Bones[c].ParentIndex == i)
                        stack.Push(c);
            }
            return mask;
        }
    }

    /// <summary>How a layer combines with the layers beneath it.</summary>
    public enum AnimatorLayerBlendingMode
    {
        /// <summary>Overwrites/interpolates the bones it affects.</summary>
        Override,
        /// <summary>Adds its pose (relative to bind) on top of the base.</summary>
        Additive
    }

    /// <summary>
    /// A directed edge between two states. Fires when all <see cref="Conditions"/> are met
    /// and (if <see cref="HasExitTime"/>) the source state reaches <see cref="ExitTime"/>
    /// normalised time.
    /// </summary>
    public sealed class AnimationTransition
    {
        public string FromState { get; set; } = string.Empty;
        public string ToState { get; set; } = string.Empty;
        public List<AnimatorCondition> Conditions { get; set; } = new();
        public bool HasExitTime { get; set; } = true;
        /// <summary>Normalised time in [0,1] (or &gt;1 for looping clips) at which the transition may exit.</summary>
        public float ExitTime { get; set; } = 1f;
        /// <summary>Duration of the cross-fade. Seconds if <see cref="FixedDuration"/>, else normalised.</summary>
        public float TransitionDuration { get; set; } = 0.25f;
        /// <summary>If true, <see cref="TransitionDuration"/> is in seconds; otherwise it is a fraction of the source length.</summary>
        public bool FixedDuration { get; set; } = true;
        /// <summary>Normalised start offset applied to the destination state's playback time.</summary>
        public float TransitionOffset { get; set; } = 0f;
        /// <summary>Whether this transition can be interrupted by another transition firing.</summary>
        public bool InterruptionEnabled { get; set; }

        public AnimationTransition() { }

        public AnimationTransition(string fromState, string toState) : this()
        {
            FromState = fromState;
            ToState = toState;
        }

        public AnimationTransition AddCondition(string parameter, AnimatorConditionMode mode, float threshold = 0f)
        {
            Conditions.Add(new AnimatorCondition(parameter, mode, threshold));
            return this;
        }
    }

    /// <summary>A node in the state machine: a named motion plus its outgoing transitions.</summary>
    public sealed class AnimationState
    {
        public string Name { get; set; } = string.Empty;
        public IMotion? Motion { get; set; }
        public float Speed { get; set; } = 1f;
        public float CycleOffset { get; set; } = 0f;
        public string Tag { get; set; } = string.Empty;
        public List<AnimationTransition> Transitions { get; set; } = new();

        /// <summary>Playback time of the source motion, in seconds (advanced by the state machine).</summary>
        internal float CurrentTime { get; set; }
        internal float PreviousTime { get; set; }

        public float NormalizedTime =>
            Motion != null && Motion.Length > 0f ? CurrentTime / Motion.Length : 0f;

        public AnimationState() { }

        public AnimationState(string name, IMotion? motion = null) : this()
        {
            Name = name;
            Motion = motion;
        }

        public SkeletonPose Sample(Skeleton skeleton, IReadOnlyDictionary<string, AnimatorParameter>? parameters)
        {
            if (Motion == null) return skeleton.CreateBindPose();
            BlendTreeParameterSync.SyncBlendTreeParameters(Motion, parameters);
            return Motion.Sample(skeleton, CurrentTime);
        }

        public AnimationTransition AddTransition(string toState) =>
            AddTransition(toState, true);

        public AnimationTransition AddTransition(string toState, bool hasExitTime, float exitTime = 1f,
                                                  float duration = 0.25f, bool fixedDuration = true)
        {
            var t = new AnimationTransition(Name, toState)
            {
                HasExitTime = hasExitTime,
                ExitTime = exitTime,
                TransitionDuration = duration,
                FixedDuration = fixedDuration
            };
            Transitions.Add(t);
            return t;
        }

        internal void ResetPlayback()
        {
            CurrentTime = 0f;
            PreviousTime = 0f;
            Motion?.Reset();
        }
    }

    internal static class BlendTreeParameterSync
    {
        public static void SyncBlendTreeParameters(IMotion motion, IReadOnlyDictionary<string, AnimatorParameter>? parameters)
        {
            if (motion is not BlendTree bt || parameters == null) return;

            if (!string.IsNullOrEmpty(bt.BlendParameter) && parameters.TryGetValue(bt.BlendParameter, out var px))
                bt.ParameterValueX = px.FloatValue;

            if (!string.IsNullOrEmpty(bt.BlendParameterY) && parameters.TryGetValue(bt.BlendParameterY, out var py))
                bt.ParameterValueY = py.FloatValue;
        }
    }

    /// <summary>
    /// A single state machine: states, any-state transitions, parameters and the runtime
    /// that advances playback and resolves transitions. One state machine drives one
    /// <see cref="AnimationLayer"/>.
    /// </summary>
    public sealed class AnimationStateMachine
    {
        public string Name { get; set; } = "StateMachine";
        public List<AnimationState> States { get; set; } = new();
        public List<AnimationTransition> AnyStateTransitions { get; set; } = new();
        public Dictionary<string, AnimatorParameter> Parameters { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string DefaultState { get; set; } = string.Empty;

        public AnimationState? CurrentState => _currentIndex >= 0 && _currentIndex < States.Count ? States[_currentIndex] : null;
        public string CurrentStateName => CurrentState?.Name ?? string.Empty;
        public string NextStateName => _transitioning && _nextIndex >= 0 && _nextIndex < States.Count ? States[_nextIndex].Name : string.Empty;
        public bool IsTransitioning => _transitioning;
        public float TransitionProgress => _transitioning && _transitionDuration > 0f
            ? Math.Clamp(_transitionElapsed / _transitionDuration, 0f, 1f)
            : 0f;

        /// <summary>Animation events fired during the most recent <see cref="Update"/> call.</summary>
        public List<AnimationEvent> FiredEventsThisFrame { get; } = new();

        private int _currentIndex = -1;
        private int _nextIndex = -1;
        private bool _transitioning;
        private float _transitionElapsed;
        private float _transitionDuration;

        // ---------------------------------------------------------------------
        // Authoring API
        // ---------------------------------------------------------------------

        public AnimationState AddState(string name, IMotion? motion = null)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("State name cannot be empty.", nameof(name));
            var state = new AnimationState(name, motion);
            States.Add(state);
            if (string.IsNullOrEmpty(DefaultState))
                DefaultState = name;
            return state;
        }

        public AnimationState AddState(string name, AnimationClip clip) => AddState(name, (IMotion?)clip);

        public void AddParameter(AnimatorParameter parameter)
        {
            if (parameter == null) throw new ArgumentNullException(nameof(parameter));
            Parameters[parameter.Name] = parameter;
        }

        public AnimatorParameter AddBool(string name, bool value = false)
        {
            var p = AnimatorParameter.Bool(name, value);
            Parameters[name] = p;
            return p;
        }

        public AnimatorParameter AddFloat(string name, float value = 0f)
        {
            var p = AnimatorParameter.Float(name, value);
            Parameters[name] = p;
            return p;
        }

        public AnimatorParameter AddInt(string name, int value = 0)
        {
            var p = AnimatorParameter.Int(name, value);
            Parameters[name] = p;
            return p;
        }

        public AnimatorParameter AddTrigger(string name)
        {
            var p = AnimatorParameter.Trigger(name);
            Parameters[name] = p;
            return p;
        }

        public AnimationTransition AddTransition(string fromState, string toState,
            bool hasExitTime = true, float exitTime = 1f, float duration = 0.25f, bool fixedDuration = true)
        {
            var t = new AnimationTransition(fromState, toState)
            {
                HasExitTime = hasExitTime,
                ExitTime = exitTime,
                TransitionDuration = duration,
                FixedDuration = fixedDuration
            };
            var from = States.FirstOrDefault(s => s.Name == fromState);
            if (from != null)
                from.Transitions.Add(t);
            else
                throw new ArgumentException($"State '{fromState}' not found.", nameof(fromState));
            return t;
        }

        public AnimationTransition AddAnyStateTransition(string toState,
            bool hasExitTime = false, float exitTime = 0f, float duration = 0.25f, bool fixedDuration = true)
        {
            var t = new AnimationTransition(string.Empty, toState)
            {
                HasExitTime = hasExitTime,
                ExitTime = exitTime,
                TransitionDuration = duration,
                FixedDuration = fixedDuration
            };
            AnyStateTransitions.Add(t);
            return t;
        }

        // ---------------------------------------------------------------------
        // Parameter convenience setters (operate on this machine's own store)
        // ---------------------------------------------------------------------

        public void SetBool(string name, bool value)
        {
            if (Parameters.TryGetValue(name, out var p) && p.Type == AnimatorParameterType.Bool)
                p.BoolValue = value;
        }

        public void SetFloat(string name, float value)
        {
            if (Parameters.TryGetValue(name, out var p) && p.Type == AnimatorParameterType.Float)
                p.FloatValue = value;
        }

        public void SetInt(string name, int value)
        {
            if (Parameters.TryGetValue(name, out var p) && p.Type == AnimatorParameterType.Int)
                p.IntValue = value;
        }

        public void SetTrigger(string name)
        {
            if (Parameters.TryGetValue(name, out var p) && p.Type == AnimatorParameterType.Trigger)
                p.TriggerValue = true;
        }

        public void ResetTrigger(string name)
        {
            if (Parameters.TryGetValue(name, out var p) && p.Type == AnimatorParameterType.Trigger)
                p.TriggerValue = false;
        }

        public bool GetBool(string name) =>
            Parameters.TryGetValue(name, out var p) && p.Type == AnimatorParameterType.Bool && p.BoolValue;

        public float GetFloat(string name) =>
            Parameters.TryGetValue(name, out var p) && p.Type == AnimatorParameterType.Float ? p.FloatValue : 0f;

        public int GetInt(string name) =>
            Parameters.TryGetValue(name, out var p) && p.Type == AnimatorParameterType.Int ? p.IntValue : 0;

        // ---------------------------------------------------------------------
        // Playback control
        // ---------------------------------------------------------------------

        public void Play(string stateName, float time = 0f)
        {
            var idx = ResolveStateIndex(stateName);
            if (idx < 0) throw new ArgumentException($"State '{stateName}' not found.", nameof(stateName));
            _currentIndex = idx;
            _transitioning = false;
            _nextIndex = -1;
            States[idx].CurrentTime = time;
            States[idx].PreviousTime = time;
            States[idx].Motion?.Reset();
        }

        /// <summary>
        /// Advance the state machine by <paramref name="dt"/> seconds and return the
        /// sampled pose. <paramref name="parameters"/> overrides the internal parameter
        /// store (used by the controller to share parameters across layers).
        /// </summary>
        public SkeletonPose Update(Skeleton skeleton, float dt,
                                   IReadOnlyDictionary<string, AnimatorParameter>? parameters = null)
        {
            if (skeleton == null) throw new ArgumentNullException(nameof(skeleton));
            dt = MathF.Max(0f, dt);
            var p = parameters ?? Parameters;
            FiredEventsThisFrame.Clear();

            if (States.Count == 0)
                return skeleton.CreateBindPose();

            if (_currentIndex < 0)
            {
                var idx = ResolveStateIndex(DefaultState);
                _currentIndex = idx < 0 ? 0 : idx;
                States[_currentIndex].ResetPlayback();
            }

            var current = States[_currentIndex];
            current.PreviousTime = current.CurrentTime;
            current.CurrentTime += dt * current.Speed;

            if (_transitioning)
            {
                var next = States[_nextIndex];
                next.PreviousTime = next.CurrentTime;
                next.CurrentTime += dt * next.Speed;
                _transitionElapsed += dt;

                var t = _transitionDuration <= 0f ? 1f : Math.Clamp(_transitionElapsed / _transitionDuration, 0f, 1f);

                PollEvents(current);
                PollEvents(next);

                var poseA = current.Sample(skeleton, p);
                var poseB = next.Sample(skeleton, p);
                var blended = AnimationPoseBlender.Blend(skeleton, poseA, poseB, t);

                if (t >= 1f)
                {
                    _currentIndex = _nextIndex;
                    _transitioning = false;
                    _nextIndex = -1;
                }
                return blended;
            }

            PollEvents(current);
            var pose = current.Sample(skeleton, p);

            var trans = FindTransition(current, p);
            if (trans != null)
                StartTransition(trans, p);

            return pose;
        }

        private void PollEvents(AnimationState state)
        {
            if (state.Motion is not AnimationClip clip || clip.Events.Count == 0) return;
            var fired = clip.PollEvents(state.PreviousTime, state.CurrentTime);
            if (fired.Count > 0)
                FiredEventsThisFrame.AddRange(fired);
        }

        private AnimationTransition? FindTransition(AnimationState current,
                                                    IReadOnlyDictionary<string, AnimatorParameter> p)
        {
            var normTime = current.NormalizedTime;

            // Any-state transitions take priority but never target the current state.
            foreach (var tr in AnyStateTransitions)
            {
                if (tr.ToState == current.Name) continue;
                if (TransitionReady(tr, p, normTime)) return tr;
            }

            foreach (var tr in current.Transitions)
            {
                if (TransitionReady(tr, p, normTime)) return tr;
            }
            return null;
        }

        private static bool TransitionReady(AnimationTransition tr,
                                            IReadOnlyDictionary<string, AnimatorParameter> p,
                                            float normTime)
        {
            // A transition with no conditions and no exit time would fire every frame;
            // require at least one of the two.
            if (tr.Conditions.Count == 0 && !tr.HasExitTime)
                return false;

            if (tr.Conditions.Count > 0)
            {
                for (var i = 0; i < tr.Conditions.Count; i++)
                    if (!tr.Conditions[i].Evaluate(p))
                        return false;
            }

            if (tr.HasExitTime && normTime < tr.ExitTime)
                return false;

            return true;
        }

        private void StartTransition(AnimationTransition tr, IReadOnlyDictionary<string, AnimatorParameter> p)
        {
            var idx = ResolveStateIndex(tr.ToState);
            if (idx < 0) return;

            var source = States[_currentIndex];
            float duration;
            if (tr.FixedDuration)
            {
                duration = tr.TransitionDuration;
            }
            else
            {
                var len = source.Motion?.Length ?? 0f;
                duration = len > 0f ? tr.TransitionDuration * len : tr.TransitionDuration;
            }

            _nextIndex = idx;
            _transitioning = true;
            _transitionElapsed = 0f;
            _transitionDuration = MathF.Max(0.0001f, duration);

            var next = States[idx];
            var nextLen = next.Motion?.Length ?? 0f;
            next.CurrentTime = nextLen > 0f ? Math.Clamp(tr.TransitionOffset, 0f, 1f) * nextLen : 0f;
            next.PreviousTime = next.CurrentTime;
            next.Motion?.Reset();

            // Reset triggers that this transition consumed.
            foreach (var cond in tr.Conditions)
            {
                if (cond.Mode is AnimatorConditionMode.If or AnimatorConditionMode.IfNot
                    && p.TryGetValue(cond.Parameter, out var param)
                    && param.Type == AnimatorParameterType.Trigger)
                {
                    param.ResetTrigger();
                }
            }
        }

        private int ResolveStateIndex(string name)
        {
            for (var i = 0; i < States.Count; i++)
                if (States[i].Name == name) return i;
            return -1;
        }

        /// <summary>Current normalised playback time of the active state (0..1, may exceed 1 when looping).</summary>
        public float GetCurrentStateNormalizedTime() => CurrentState?.NormalizedTime ?? 0f;
    }

    /// <summary>
    /// A single layer of an <see cref="AnimatorController"/>. Each layer runs its own
    /// state machine, optionally masked to a subset of bones and blended additively or as
    /// an override.
    /// </summary>
    public sealed class AnimationLayer
    {
        public string Name { get; set; } = "Layer";
        public AnimationStateMachine StateMachine { get; set; }
        public AvatarMask? Mask { get; set; }
        public float Weight { get; set; } = 1f;
        public AnimatorLayerBlendingMode BlendingMode { get; set; } = AnimatorLayerBlendingMode.Override;
        public bool IkPass { get; set; }

        public AnimationLayer(AnimationStateMachine stateMachine)
        {
            StateMachine = stateMachine ?? throw new ArgumentNullException(nameof(stateMachine));
        }

        public AnimationLayer(string name, AnimationStateMachine stateMachine) : this(stateMachine)
        {
            Name = name;
        }
    }

    /// <summary>
    /// The top-level animation controller. Owns the shared parameter store and one or more
    /// <see cref="AnimationLayer"/>s, evaluates them each frame, blends the results and
    /// optionally applies an <see cref="AnimationRig"/>. This is the runtime equivalent of
    /// Unity's AnimatorController / Mecanim.
    /// </summary>
    public sealed class AnimatorController
    {
        public string Name { get; set; } = "AnimatorController";
        public Skeleton Skeleton { get; }
        public List<AnimationLayer> Layers { get; } = new();
        public Dictionary<string, AnimatorParameter> Parameters { get; } = new(StringComparer.OrdinalIgnoreCase);
        public AnimationRig? Rig { get; set; }
        public SkeletonPose CurrentPose { get; private set; }

        /// <summary>When enabled, the controller tracks per-frame root-bone motion deltas.</summary>
        public bool ApplyRootMotion { get; set; }
        public int RootBoneIndex { get; set; } = 0;
        public RootMotionData RootMotion { get; private set; }

        /// <summary>Animation events fired across all layers during the last update.</summary>
        public List<AnimationEvent> FiredEventsThisFrame { get; } = new();

        public string CurrentStateName => Layers.Count > 0 ? Layers[0].StateMachine.CurrentStateName : string.Empty;
        public string NextStateName => Layers.Count > 0 ? Layers[0].StateMachine.NextStateName : string.Empty;
        public bool IsTransitioning => Layers.Count > 0 && Layers[0].StateMachine.IsTransitioning;
        public float TransitionProgress => Layers.Count > 0 ? Layers[0].StateMachine.TransitionProgress : 0f;

        private Vector3 _lastRootPos;
        private Quaternion _lastRootRot = Quaternion.Identity;
        private bool _hasLastRoot;

        public AnimatorController(Skeleton skeleton)
        {
            Skeleton = skeleton ?? throw new ArgumentNullException(nameof(skeleton));
            CurrentPose = skeleton.CreateBindPose();
        }

        // ---------------------------------------------------------------------
        // Layer / parameter authoring
        // ---------------------------------------------------------------------

        public AnimationLayer AddLayer(AnimationStateMachine stateMachine)
        {
            var layer = new AnimationLayer(stateMachine);
            Layers.Add(layer);
            return layer;
        }

        public AnimationLayer AddLayer(string name, AnimationStateMachine stateMachine)
        {
            var layer = new AnimationLayer(name, stateMachine);
            Layers.Add(layer);
            return layer;
        }

        public AnimatorParameter AddParameter(AnimatorParameter parameter)
        {
            Parameters[parameter.Name] = parameter;
            return parameter;
        }

        public AnimatorParameter AddBool(string name, bool value = false) =>
            AddParameter(AnimatorParameter.Bool(name, value));

        public AnimatorParameter AddFloat(string name, float value = 0f) =>
            AddParameter(AnimatorParameter.Float(name, value));

        public AnimatorParameter AddInt(string name, int value = 0) =>
            AddParameter(AnimatorParameter.Int(name, value));

        public AnimatorParameter AddTrigger(string name) =>
            AddParameter(AnimatorParameter.Trigger(name));

        public void SetBool(string name, bool value)
        {
            if (Parameters.TryGetValue(name, out var p) && p.Type == AnimatorParameterType.Bool)
                p.BoolValue = value;
        }

        public void SetFloat(string name, float value)
        {
            if (Parameters.TryGetValue(name, out var p) && p.Type == AnimatorParameterType.Float)
                p.FloatValue = value;
        }

        public void SetInt(string name, int value)
        {
            if (Parameters.TryGetValue(name, out var p) && p.Type == AnimatorParameterType.Int)
                p.IntValue = value;
        }

        public void SetTrigger(string name)
        {
            if (Parameters.TryGetValue(name, out var p) && p.Type == AnimatorParameterType.Trigger)
                p.TriggerValue = true;
        }

        public void ResetTrigger(string name)
        {
            if (Parameters.TryGetValue(name, out var p) && p.Type == AnimatorParameterType.Trigger)
                p.TriggerValue = false;
        }

        public bool GetBool(string name) =>
            Parameters.TryGetValue(name, out var p) && p.Type == AnimatorParameterType.Bool && p.BoolValue;

        public float GetFloat(string name) =>
            Parameters.TryGetValue(name, out var p) && p.Type == AnimatorParameterType.Float ? p.FloatValue : 0f;

        public int GetInt(string name) =>
            Parameters.TryGetValue(name, out var p) && p.Type == AnimatorParameterType.Int ? p.IntValue : 0;

        public void Play(string stateName, int layerIndex = 0)
        {
            if (layerIndex < 0 || layerIndex >= Layers.Count) return;
            Layers[layerIndex].StateMachine.Play(stateName);
        }

        // ---------------------------------------------------------------------
        // Update
        // ---------------------------------------------------------------------

        public SkeletonPose Update(float dt)
        {
            dt = MathF.Max(0f, dt);
            FiredEventsThisFrame.Clear();

            if (Layers.Count == 0)
            {
                CurrentPose = Skeleton.CreateBindPose();
                return CurrentPose;
            }

            var skeleton = Skeleton;

            // Base layer provides the full-body pose.
            var basePose = Layers[0].StateMachine.Update(skeleton, dt, Parameters);
            CollectEvents(Layers[0]);

            // Subsequent layers blend on top.
            for (var i = 1; i < Layers.Count; i++)
            {
                var layer = Layers[i];
                if (layer.Weight <= 0f) continue;
                var layerPose = layer.StateMachine.Update(skeleton, dt, Parameters);
                CollectEvents(layer);
                AnimationPoseBlender.BlendLayer(skeleton, basePose, layerPose, layer.Weight,
                                                 layer.Mask, layer.BlendingMode);
            }

            CurrentPose = basePose;

            // Apply runtime rigging (IK + constraints) on the final pose.
            if (Rig != null)
                Rig.Apply(skeleton, CurrentPose);

            // Root motion extraction from the base pose.
            if (ApplyRootMotion)
                RootMotion = ExtractRootMotion();

            return CurrentPose;
        }

        private void CollectEvents(AnimationLayer layer)
        {
            if (layer.StateMachine.FiredEventsThisFrame.Count > 0)
                FiredEventsThisFrame.AddRange(layer.StateMachine.FiredEventsThisFrame);
        }

        private RootMotionData ExtractRootMotion()
        {
            if (RootBoneIndex < 0 || RootBoneIndex >= Skeleton.Bones.Count)
            {
                _hasLastRoot = false;
                return RootMotionData.Identity;
            }

            var pos = CurrentPose.GetTranslationOffset(RootBoneIndex);
            var rot = CurrentPose.GetRotation(RootBoneIndex);

            if (!_hasLastRoot)
            {
                _lastRootPos = pos;
                _lastRootRot = rot;
                _hasLastRoot = true;
                return RootMotionData.Identity;
            }

            var posDelta = pos - _lastRootPos;
            var rotDelta = Quaternion.Normalize(Quaternion.Inverse(_lastRootRot) * rot);
            if (Quaternion.Dot(rotDelta, Quaternion.Identity) < 0f)
                rotDelta = -rotDelta;

            _lastRootPos = pos;
            _lastRootRot = rot;

            return new RootMotionData { PositionDelta = posDelta, RotationDelta = rotDelta };
        }

        public void ResetRootMotion()
        {
            _hasLastRoot = false;
            _lastRootPos = Vector3.Zero;
            _lastRootRot = Quaternion.Identity;
            RootMotion = RootMotionData.Identity;
        }
    }
}
