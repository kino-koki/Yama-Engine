// AnimStateMachineReal.cs - Real animation state machine with transitions,
// parameter system, nested state machines, parallel states, and conduits.
// All algorithms use System.Numerics with mathematically correct implementations.

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace ShanyuEngine.Animation
{
    // ========================================================================
    // Parameter system
    // ========================================================================

    /// <summary>Type of animation parameter.</summary>
    public enum AnimParameterType
    {
        Bool,
        Float,
        Int,
        Trigger
    }

    /// <summary>
    /// An animation parameter: Bool, Float, Int, or Trigger.
    /// Trigger parameters auto-reset to false after being consumed by a transition.
    /// </summary>
    public class AnimParameter
    {
        public string Name { get; set; }
        public AnimParameterType Type { get; set; }

        public bool BoolValue { get; set; }
        public float FloatValue { get; set; }
        public int IntValue { get; set; }

        /// <summary>Whether this trigger has been consumed (auto-resets).</summary>
        public bool TriggerConsumed { get; set; }

        public AnimParameter() { }

        public AnimParameter(string name, AnimParameterType type)
        {
            Name = name;
            Type = type;
        }

        public static AnimParameter Bool(string name, bool value = false)
            => new AnimParameter(name, AnimParameterType.Bool) { BoolValue = value };

        public static AnimParameter Float(string name, float value = 0f)
            => new AnimParameter(name, AnimParameterType.Float) { FloatValue = value };

        public static AnimParameter Int(string name, int value = 0)
            => new AnimParameter(name, AnimParameterType.Int) { IntValue = value };

        public static AnimParameter Trigger(string name)
            => new AnimParameter(name, AnimParameterType.Trigger);

        /// <summary>Set a trigger parameter (fires it; will be consumed by next transition).</summary>
        public void SetTrigger()
        {
            if (Type == AnimParameterType.Trigger)
            {
                BoolValue = true;
                TriggerConsumed = false;
            }
        }

        /// <summary>Consume a trigger (called by the state machine after a transition fires).</summary>
        public void ConsumeTrigger()
        {
            if (Type == AnimParameterType.Trigger)
            {
                BoolValue = false;
                TriggerConsumed = true;
            }
        }

        /// <summary>Check if a trigger is currently active (set but not consumed).</summary>
        public bool IsTriggerActive => Type == AnimParameterType.Trigger && BoolValue && !TriggerConsumed;

        public AnimParameter Clone()
        {
            return new AnimParameter
            {
                Name = Name,
                Type = Type,
                BoolValue = BoolValue,
                FloatValue = FloatValue,
                IntValue = IntValue,
                TriggerConsumed = TriggerConsumed
            };
        }
    }

    // ========================================================================
    // Transition conditions
    // ========================================================================

    /// <summary>Comparison operator for transition conditions.</summary>
    public enum ConditionOperator
    {
        Equals,
        NotEquals,
        Greater,
        GreaterEqual,
        Less,
        LessEqual,
        IsTrue,
        IsFalse
    }

    /// <summary>
    /// A single condition on a parameter. Compares the parameter's value
    /// against a threshold using the specified operator.
    /// </summary>
    public class TransitionCondition
    {
        public string ParameterName { get; set; }
        public ConditionOperator Operator { get; set; }
        public float ThresholdFloat { get; set; }
        public int ThresholdInt { get; set; }
        public bool ThresholdBool { get; set; }

        public TransitionCondition() { }

        public TransitionCondition(string paramName, ConditionOperator op, float threshold)
        {
            ParameterName = paramName;
            Operator = op;
            ThresholdFloat = threshold;
        }

        public TransitionCondition(string paramName, ConditionOperator op, int threshold)
        {
            ParameterName = paramName;
            Operator = op;
            ThresholdInt = threshold;
        }

        public TransitionCondition(string paramName, ConditionOperator op, bool threshold)
        {
            ParameterName = paramName;
            Operator = op;
            ThresholdBool = threshold;
        }

        /// <summary>
        /// Evaluate this condition against the given parameter.
        /// Returns true if the condition is satisfied.
        /// </summary>
        public bool Evaluate(AnimParameter parameter)
        {
            if (parameter == null) return false;

            switch (parameter.Type)
            {
                case AnimParameterType.Bool:
                    {
                        bool val = parameter.BoolValue;
                        return Operator switch
                        {
                            ConditionOperator.IsTrue => val,
                            ConditionOperator.IsFalse => !val,
                            ConditionOperator.Equals => val == ThresholdBool,
                            ConditionOperator.NotEquals => val != ThresholdBool,
                            _ => false
                        };
                    }

                case AnimParameterType.Float:
                    {
                        float val = parameter.FloatValue;
                        return Operator switch
                        {
                            ConditionOperator.Equals => MathF.Abs(val - ThresholdFloat) < 1e-4f,
                            ConditionOperator.NotEquals => MathF.Abs(val - ThresholdFloat) >= 1e-4f,
                            ConditionOperator.Greater => val > ThresholdFloat,
                            ConditionOperator.GreaterEqual => val >= ThresholdFloat,
                            ConditionOperator.Less => val < ThresholdFloat,
                            ConditionOperator.LessEqual => val <= ThresholdFloat,
                            _ => false
                        };
                    }

                case AnimParameterType.Int:
                    {
                        int val = parameter.IntValue;
                        return Operator switch
                        {
                            ConditionOperator.Equals => val == ThresholdInt,
                            ConditionOperator.NotEquals => val != ThresholdInt,
                            ConditionOperator.Greater => val > ThresholdInt,
                            ConditionOperator.GreaterEqual => val >= ThresholdInt,
                            ConditionOperator.Less => val < ThresholdInt,
                            ConditionOperator.LessEqual => val <= ThresholdInt,
                            _ => false
                        };
                    }

                case AnimParameterType.Trigger:
                    {
                        // Trigger is satisfied when it's active (set but not consumed)
                        return parameter.IsTriggerActive;
                    }

                default:
                    return false;
            }
        }
    }

    // ========================================================================
    // Transition types and blend curves
    // ========================================================================

    /// <summary>Type of state transition.</summary>
    public enum TransitionType
    {
        /// <summary>Instant switch (no blending).</summary>
        Instant,
        /// <summary>Crossfade over a fixed duration.</summary>
        Crossfade,
        /// <summary>Curve-controlled blend over a duration.</summary>
        Curve
    }

    /// <summary>Blend curve shape for transitions.</summary>
    public enum BlendCurveType
    {
        Linear,
        EaseIn,
        EaseOut,
        EaseInOut,
        Custom
    }

    // ========================================================================
    // AnimTransitionReal - a transition between two states
    // ========================================================================

    /// <summary>
    /// A transition from one animation state to another. Has a duration,
    /// transition type, blend curve, and a list of conditions that must all
    /// be true for the transition to fire.
    /// </summary>
    public class AnimTransitionReal
    {
        public string Name { get; set; } = "Transition";
        public int SourceStateIndex { get; set; } = -1;
        public int TargetStateIndex { get; set; } = -1;
        public float Duration { get; set; } = 0.2f;
        public TransitionType Type { get; set; } = TransitionType.Crossfade;
        public BlendCurveType BlendCurve { get; set; } = BlendCurveType.Linear;
        public List<TransitionCondition> Conditions { get; } = new List<TransitionCondition>();

        /// <summary>Whether this transition can be interrupted by other transitions.</summary>
        public bool CanInterrupt { get; set; } = true;

        /// <summary>If true, the source state keeps advancing during the transition.</summary>
        public bool SourceKeepsAdvancing { get; set; } = true;

        /// <summary>If true, the target state starts from time 0 when the transition begins.</summary>
        public bool TargetStartsFromZero { get; set; } = true;

        /// <summary>Custom keyframe curve for Curve transition type (time 0..1 → alpha 0..1).</summary>
        public List<FloatKeyframe> CustomCurve { get; set; }

        /// <summary>Exit time condition: transition fires when source state reaches this normalized time (0..1). -1 = no exit time.</summary>
        public float ExitTime { get; set; } = -1f;

        // Runtime state
        public float ElapsedTime { get; set; }
        public bool IsActive { get; set; }

        public AnimTransitionReal() { }

        public AnimTransitionReal(int source, int target, float duration = 0.2f)
        {
            SourceStateIndex = source;
            TargetStateIndex = target;
            Duration = duration;
        }

        /// <summary>Check if all conditions are satisfied for this transition.</summary>
        public bool CheckConditions(Dictionary<string, AnimParameter> parameters)
        {
            // Check exit time first (if set)
            if (ExitTime >= 0f)
            {
                // ExitTime is checked by the state machine using the source state's normalized time
                // The state machine sets _exitTimeMet before calling this
                if (!_exitTimeMet)
                    return false;
            }

            if (Conditions.Count == 0)
                return true;

            foreach (var cond in Conditions)
            {
                if (string.IsNullOrEmpty(cond.ParameterName))
                    continue;
                if (!parameters.TryGetValue(cond.ParameterName, out var param))
                    return false;
                if (!cond.Evaluate(param))
                    return false;
            }
            return true;
        }

        /// <summary>Internal: set by the state machine when exit time is met.</summary>
        internal bool _exitTimeMet = true;

        /// <summary>Evaluate the blend curve at normalized time t (0..1).</summary>
        public float EvaluateBlendCurve(float t)
        {
            t = Math.Clamp(t, 0f, 1f);

            switch (BlendCurve)
            {
                case BlendCurveType.Linear:
                    return t;

                case BlendCurveType.EaseIn:
                    return t * t;

                case BlendCurveType.EaseOut:
                    return 1f - (1f - t) * (1f - t);

                case BlendCurveType.EaseInOut:
                    // Smoothstep: t^2 * (3 - 2t)
                    return t * t * (3f - 2f * t);

                case BlendCurveType.Custom:
                    if (CustomCurve != null && CustomCurve.Count > 0)
                    {
                        return KeyframeInterpolation.EvaluateFloatCurve(
                            CustomCurve, t, InterpolationMode.Linear, ExtrapolationMode.Clamp);
                    }
                    return t;

                default:
                    return t;
            }
        }

        /// <summary>Consume trigger parameters used by this transition's conditions.</summary>
        public void ConsumeTriggers(Dictionary<string, AnimParameter> parameters)
        {
            foreach (var cond in Conditions)
            {
                if (string.IsNullOrEmpty(cond.ParameterName))
                    continue;
                if (parameters.TryGetValue(cond.ParameterName, out var param))
                {
                    if (param.Type == AnimParameterType.Trigger)
                        param.ConsumeTrigger();
                }
            }
        }

        public void Reset()
        {
            ElapsedTime = 0f;
            IsActive = false;
            _exitTimeMet = true;
        }
    }

    // ========================================================================
    // Conduit - guard condition between states
    // ========================================================================

    /// <summary>
    /// A conduit is a guard condition placed between states. It filters which
    /// transitions are allowed to fire. A conduit returns true to allow the
    /// transition, false to block it.
    /// </summary>
    public class AnimConduit
    {
        public string Name { get; set; } = "Conduit";
        public List<TransitionCondition> Conditions { get; } = new List<TransitionCondition>();

        /// <summary>
        /// Evaluate the conduit. Returns true if all conditions are satisfied
        /// (transition is allowed to proceed).
        /// </summary>
        public bool Evaluate(Dictionary<string, AnimParameter> parameters)
        {
            if (Conditions.Count == 0) return true;

            foreach (var cond in Conditions)
            {
                if (string.IsNullOrEmpty(cond.ParameterName))
                    continue;
                if (!parameters.TryGetValue(cond.ParameterName, out var param))
                    return false;
                if (!cond.Evaluate(param))
                    return false;
            }
            return true;
        }
    }

    // ========================================================================
    // AnimStateReal - a single state in the state machine
    // ========================================================================

    /// <summary>
    /// An animation state. Contains a blend node (which produces the pose),
    /// a list of outgoing transitions, and enter/exit/update event callbacks.
    /// </summary>
    public class AnimStateReal
    {
        public string Name { get; set; } = "State";
        public BlendNode Node { get; set; }
        public List<AnimTransitionReal> Transitions { get; } = new List<AnimTransitionReal>();
        public List<AnimConduit> Conduits { get; } = new List<AnimConduit>();

        /// <summary>If set, this state is a nested state machine (for hierarchical FSMs).</summary>
        public AnimStateMachineReal NestedStateMachine { get; set; }

        /// <summary>Whether this state is a nested state machine.</summary>
        public bool IsNestedStateMachine => NestedStateMachine != null;

        // Event callbacks
        public Action OnEnter { get; set; }
        public Action OnExit { get; set; }
        public Action<float> OnUpdate { get; set; }

        /// <summary>Playback time within this state.</summary>
        public float CurrentTime { get; set; }
        public float PlaybackSpeed { get; set; } = 1f;

        /// <summary>Whether this state has been entered (for one-shot enter events).</summary>
        public bool HasEntered { get; set; }

        public AnimStateReal() { }

        public AnimStateReal(string name, BlendNode node)
        {
            Name = name;
            Node = node;
        }

        /// <summary>Get the duration of this state's blend node.</summary>
        public float GetDuration()
        {
            if (IsNestedStateMachine)
                return NestedStateMachine?.GetTotalDuration() ?? 0f;
            return Node?.GetDuration() ?? 0f;
        }

        /// <summary>Get the normalized playback time (0..1).</summary>
        public float GetNormalizedTime()
        {
            float dur = GetDuration();
            if (dur <= 0f) return 0f;
            return CurrentTime / dur;
        }

        /// <summary>Evaluate the pose for this state.</summary>
        public SkeletonPose Evaluate(BlendContext context)
        {
            if (IsNestedStateMachine)
            {
                NestedStateMachine.Tick(context.DeltaTime);
                return NestedStateMachine.GetCurrentPose(context.Skeleton);
            }

            if (Node == null)
                return context.Skeleton?.CreateBindPose();

            var ctx = context.Clone();
            ctx.CurrentTime = CurrentTime;
            return Node.Evaluate(ctx);
        }

        /// <summary>Advance the state's playback time.</summary>
        public void Advance(float deltaTime)
        {
            CurrentTime += deltaTime * PlaybackSpeed;
            float dur = GetDuration();
            if (dur > 0f)
            {
                // Loop the time
                CurrentTime = ((CurrentTime % dur) + dur) % dur;
            }
            OnUpdate?.Invoke(deltaTime);
        }

        public void Enter()
        {
            HasEntered = true;
            CurrentTime = 0f;
            OnEnter?.Invoke();
        }

        public void Exit()
        {
            OnExit?.Invoke();
            HasEntered = false;
        }

        /// <summary>Add a transition to another state.</summary>
        public void AddTransition(AnimTransitionReal transition)
        {
            transition.SourceStateIndex = -1; // Will be set by the state machine
            Transitions.Add(transition);
        }

        /// <summary>Convenience: add a transition by target index and duration.</summary>
        public AnimTransitionReal AddTransition(int targetIndex, float duration = 0.2f)
        {
            var t = new AnimTransitionReal(-1, targetIndex, duration);
            Transitions.Add(t);
            return t;
        }
    }

    // ========================================================================
    // Parallel state - a state that runs concurrently with others
    // ========================================================================

    /// <summary>
    /// A parallel state entry: a state with a weight. Multiple parallel states
    /// are active simultaneously, and their results are blended by weight.
    /// </summary>
    public class ParallelStateEntry
    {
        public AnimStateReal State { get; set; }
        public float Weight { get; set; } = 1f;
        public BoneMask Mask { get; set; }
    }

    // ========================================================================
    // AnimStateMachineReal - the state machine
    // ========================================================================

    /// <summary>
    /// Real animation state machine. Manages a collection of states, transitions
    /// between them based on parameter conditions, and produces a blended pose
    /// during transitions. Supports nested state machines, parallel states,
    /// and conduits.
    /// </summary>
    public class AnimStateMachineReal
    {
        public List<AnimStateReal> States { get; } = new List<AnimStateReal>();
        public Dictionary<string, AnimParameter> Parameters { get; } = new Dictionary<string, AnimParameter>();

        public int InitialStateIndex { get; set; } = 0;
        public int CurrentStateIndex { get; set; } = -1;
        public int PreviousStateIndex { get; set; } = -1;

        /// <summary>The currently active transition (null if none).</summary>
        public AnimTransitionReal CurrentTransition { get; private set; }

        /// <summary>Parallel states that run concurrently with the main state.</summary>
        public List<ParallelStateEntry> ParallelStates { get; } = new List<ParallelStateEntry>();

        /// <summary>Whether to use parallel state blending.</summary>
        public bool UseParallelStates { get; set; }

        /// <summary>Conduits that apply globally to all transitions.</summary>
        public List<AnimConduit> GlobalConduits { get; } = new List<AnimConduit>();

        /// <summary>Cached blend context for evaluation.</summary>
        private BlendContext _context = new BlendContext();

        /// <summary>Cached poses for source and target during transition.</summary>
        private SkeletonPose _sourcePose;
        private SkeletonPose _targetPose;

        public AnimStateMachineReal() { }

        // ------------------------------------------------------------------
        // State management
        // ------------------------------------------------------------------

        /// <summary>Add a state to the state machine.</summary>
        public int AddState(AnimStateReal state)
        {
            int idx = States.Count;
            States.Add(state);
            return idx;
        }

        /// <summary>Convenience: add a state with a name and blend node.</summary>
        public int AddState(string name, BlendNode node)
        {
            return AddState(new AnimStateReal(name, node));
        }

        /// <summary>Get the current state, or null if not initialized.</summary>
        public AnimStateReal GetCurrentState()
        {
            return (CurrentStateIndex >= 0 && CurrentStateIndex < States.Count)
                ? States[CurrentStateIndex]
                : null;
        }

        /// <summary>Get the previous state, or null.</summary>
        public AnimStateReal GetPreviousState()
        {
            return (PreviousStateIndex >= 0 && PreviousStateIndex < States.Count)
                ? States[PreviousStateIndex]
                : null;
        }

        /// <summary>Get the current state's duration.</summary>
        public float GetCurrentStateDuration()
        {
            return GetCurrentState()?.GetDuration() ?? 0f;
        }

        /// <summary>Get the total duration of the state machine (max of all states).</summary>
        public float GetTotalDuration()
        {
            float max = 0f;
            foreach (var s in States)
                max = MathF.Max(max, s?.GetDuration() ?? 0f);
            return max;
        }

        // ------------------------------------------------------------------
        // Parameter management
        // ------------------------------------------------------------------

        public void AddParameter(AnimParameter param)
        {
            if (param != null && !string.IsNullOrEmpty(param.Name))
                Parameters[param.Name] = param;
        }

        public void AddBoolParameter(string name, bool value = false)
            => AddParameter(AnimParameter.Bool(name, value));

        public void AddFloatParameter(string name, float value = 0f)
            => AddParameter(AnimParameter.Float(name, value));

        public void AddIntParameter(string name, int value = 0)
            => AddParameter(AnimParameter.Int(name, value));

        public void AddTriggerParameter(string name)
            => AddParameter(AnimParameter.Trigger(name));

        /// <summary>Set a trigger parameter (fires it).</summary>
        public void SetTrigger(string name)
        {
            if (Parameters.TryGetValue(name, out var param))
                param.SetTrigger();
        }

        /// <summary>Set a float parameter value.</summary>
        public void SetFloat(string name, float value)
        {
            if (Parameters.TryGetValue(name, out var param) && param.Type == AnimParameterType.Float)
                param.FloatValue = value;
        }

        /// <summary>Set a bool parameter value.</summary>
        public void SetBool(string name, bool value)
        {
            if (Parameters.TryGetValue(name, out var param) && param.Type == AnimParameterType.Bool)
                param.BoolValue = value;
        }

        /// <summary>Set an int parameter value.</summary>
        public void SetInt(string name, int value)
        {
            if (Parameters.TryGetValue(name, out var param) && param.Type == AnimParameterType.Int)
                param.IntValue = value;
        }

        /// <summary>Get a parameter by name. Returns null if not found.</summary>
        public AnimParameter GetParameter(string name)
        {
            return Parameters.TryGetValue(name, out var p) ? p : null;
        }

        // ------------------------------------------------------------------
        // Initialization
        // ------------------------------------------------------------------

        /// <summary>Initialize the state machine: enter the initial state.</summary>
        public void Initialize()
        {
            if (States.Count == 0) return;
            CurrentStateIndex = Math.Clamp(InitialStateIndex, 0, States.Count - 1);
            PreviousStateIndex = -1;
            CurrentTransition = null;
            var state = GetCurrentState();
            if (state != null)
                state.Enter();
        }

        /// <summary>Reset the state machine to its initial state.</summary>
        public void Reset()
        {
            foreach (var state in States)
            {
                state?.Reset();
                state?.Exit();
                state.CurrentTime = 0f;
            }
            CurrentTransition = null;
            _sourcePose = null;
            _targetPose = null;
            Initialize();
        }

        // ------------------------------------------------------------------
        // Tick - the main update loop
        // ------------------------------------------------------------------

        /// <summary>
        /// Advance the state machine by dt seconds. This is the main update
        /// entry point. Steps:
        /// 1. Advance the current state's playback time
        /// 2. Check transitions from the current state
        /// 3. If a transition fires, start it
        /// 4. If in a transition, update progress and blend poses
        /// 5. When the transition completes, switch to the target state
        /// </summary>
        public void Tick(float deltaTime)
        {
            if (States.Count == 0) return;

            // Initialize if not yet done
            if (CurrentStateIndex < 0)
                Initialize();
            if (CurrentStateIndex < 0) return;

            // Update the blend context
            _context.DeltaTime = deltaTime;
            _context.CurrentTime += deltaTime;

            var currentState = GetCurrentState();
            if (currentState == null) return;

            // Step 1: Advance current state's time (if not in transition, or if source keeps advancing)
            bool inTransition = CurrentTransition != null;
            if (!inTransition || CurrentTransition.SourceKeepsAdvancing)
            {
                currentState.Advance(deltaTime);
            }

            // Step 2: Check for transition conditions (only if not already transitioning)
            if (!inTransition)
            {
                AnimTransitionReal triggered = CheckTransitions(currentState);
                if (triggered != null)
                {
                    StartTransition(triggered);
                    inTransition = CurrentTransition != null;
                }
            }

            // Step 3: If in transition, update progress and evaluate poses
            if (inTransition && CurrentTransition != null)
            {
                UpdateTransition(deltaTime);
            }

            // Step 4: Update parallel states
            if (UseParallelStates && ParallelStates.Count > 0)
            {
                foreach (var ps in ParallelStates)
                {
                    if (ps.State != null)
                        ps.State.Advance(deltaTime);
                }
            }

            // Step 5: Advance nested state machines
            if (currentState.IsNestedStateMachine)
            {
                currentState.NestedStateMachine.Tick(deltaTime);
            }
        }

        // ------------------------------------------------------------------
        // Transition logic
        // ------------------------------------------------------------------

        /// <summary>
        /// Check all transitions from the current state. Returns the first
        /// transition whose conditions are met, or null if none.
        /// </summary>
        private AnimTransitionReal CheckTransitions(AnimStateReal currentState)
        {
            // Check global conduits first
            foreach (var conduit in GlobalConduits)
            {
                if (!conduit.Evaluate(Parameters))
                    return null; // Global conduit blocks all transitions
            }

            // Check state-level conduits
            foreach (var conduit in currentState.Conduits)
            {
                if (!conduit.Evaluate(Parameters))
                    return null;
            }

            // Check each transition
            foreach (var transition in currentState.Transitions)
            {
                if (transition.TargetStateIndex < 0 || transition.TargetStateIndex >= States.Count)
                    continue;

                // Check exit time condition
                if (transition.ExitTime >= 0f)
                {
                    float normTime = currentState.GetNormalizedTime();
                    transition._exitTimeMet = normTime >= transition.ExitTime;
                }
                else
                {
                    transition._exitTimeMet = true;
                }

                if (transition.CheckConditions(Parameters))
                {
                    return transition;
                }
            }

            return null;
        }

        /// <summary>Start a transition.</summary>
        private void StartTransition(AnimTransitionReal transition)
        {
            transition.ElapsedTime = 0f;
            transition.IsActive = true;

            // Consume triggers used by this transition
            transition.ConsumeTriggers(Parameters);

            // For Instant transitions, immediately switch
            if (transition.Type == TransitionType.Instant || transition.Duration <= 0f)
            {
                SwitchToState(transition.TargetStateIndex);
                transition.IsActive = false;
                transition.ElapsedTime = transition.Duration;
                CurrentTransition = null;
                return;
            }

            // For Crossfade/Curve, set up the transition
            PreviousStateIndex = CurrentStateIndex;
            CurrentTransition = transition;

            // Initialize target state if needed
            var targetState = States[transition.TargetStateIndex];
            if (transition.TargetStartsFromZero)
            {
                targetState.CurrentTime = 0f;
            }
            targetState.HasEntered = false; // Will be entered when transition starts
        }

        /// <summary>Update an active transition.</summary>
        private void UpdateTransition(float deltaTime)
        {
            var transition = CurrentTransition;
            if (transition == null) return;

            transition.ElapsedTime += deltaTime;

            // Compute normalized progress
            float t = transition.Duration > 0f
                ? Math.Clamp(transition.ElapsedTime / transition.Duration, 0f, 1f)
                : 1f;

            // Apply blend curve
            float alpha = transition.EvaluateBlendCurve(t);

            // Evaluate source and target poses
            var sourceState = GetPreviousState();
            var targetState = States[transition.TargetStateIndex];

            if (sourceState != null && targetState != null)
            {
                var ctx = _context.Clone();

                // Evaluate source pose (at its current time)
                if (transition.SourceKeepsAdvancing)
                    sourceState.Advance(deltaTime);
                _sourcePose = sourceState.Evaluate(ctx);

                // Evaluate target pose (advancing from its start time)
                if (!targetState.HasEntered)
                {
                    targetState.Enter();
                }
                targetState.Advance(deltaTime);
                _targetPose = targetState.Evaluate(ctx);
            }

            // Check if transition is complete
            if (t >= 1f)
            {
                // Transition complete: switch to target state
                var oldState = GetCurrentState();
                if (oldState != null && oldState != targetState)
                    oldState.Exit();

                CurrentStateIndex = transition.TargetStateIndex;
                transition.IsActive = false;
                CurrentTransition = null;
                _sourcePose = null;
                _targetPose = null;
            }
        }

        /// <summary>Switch to a new state immediately (for Instant transitions).</summary>
        private void SwitchToState(int newStateIndex)
        {
            if (newStateIndex < 0 || newStateIndex >= States.Count) return;

            var oldState = GetCurrentState();
            if (oldState != null)
                oldState.Exit();

            PreviousStateIndex = CurrentStateIndex;
            CurrentStateIndex = newStateIndex;

            var newState = GetCurrentState();
            if (newState != null)
                newState.Enter();
        }

        // ------------------------------------------------------------------
        // Pose retrieval
        // ------------------------------------------------------------------

        /// <summary>
        /// Get the current pose of the state machine. If in a transition, this
        /// is the blended pose between source and target. Otherwise, it's the
        /// current state's pose.
        /// </summary>
        public SkeletonPose GetCurrentPose(Skeleton skeleton)
        {
            if (skeleton == null) return null;

            // Ensure context has the skeleton
            _context.Skeleton = skeleton;

            if (CurrentTransition != null && _sourcePose != null && _targetPose != null)
            {
                // Compute current alpha
                float t = CurrentTransition.Duration > 0f
                    ? Math.Clamp(CurrentTransition.ElapsedTime / CurrentTransition.Duration, 0f, 1f)
                    : 1f;
                float alpha = CurrentTransition.EvaluateBlendCurve(t);

                // Blend source and target: sourcePose * (1-alpha) + targetPose * alpha
                SkeletonPose blended = SkeletonPose.Blend(_sourcePose, _targetPose, alpha);

                // Apply parallel states if enabled
                if (UseParallelStates && ParallelStates.Count > 0)
                {
                    blended = ApplyParallelStates(blended, skeleton);
                }

                return blended;
            }

            // No transition: evaluate current state
            var state = GetCurrentState();
            if (state == null)
                return skeleton.CreateBindPose();

            var ctx = _context.Clone();
            ctx.Skeleton = skeleton;
            ctx.CurrentTime = state.CurrentTime;

            SkeletonPose pose = state.Evaluate(ctx);

            // Apply parallel states if enabled
            if (UseParallelStates && ParallelStates.Count > 0)
            {
                pose = ApplyParallelStates(pose, skeleton);
            }

            return pose;
        }

        /// <summary>Apply parallel states by blending their poses onto the base.</summary>
        private SkeletonPose ApplyParallelStates(SkeletonPose basePose, Skeleton skeleton)
        {
            if (basePose == null) return null;
            var result = basePose.Clone();

            foreach (var ps in ParallelStates)
            {
                if (ps.State == null || ps.Weight <= 0f) continue;

                var ctx = _context.Clone();
                ctx.Skeleton = skeleton;
                ctx.CurrentTime = ps.State.CurrentTime;
                ctx.DeltaTime = 0f; // Already advanced in Tick

                SkeletonPose psPose = ps.State.Evaluate(ctx);
                if (psPose == null) continue;

                float weight = Math.Clamp(ps.Weight, 0f, 1f);

                // Blend by weight, respecting bone mask
                int count = Math.Min(result.BoneCount, psPose.BoneCount);
                for (int i = 0; i < count; i++)
                {
                    if (ps.Mask != null && !ps.Mask.IsEnabled(i))
                        continue;

                    ref BonePose bp = ref result.BonePoses[i];
                    ref readonly BonePose op = ref psPose.BonePoses[i];

                    bp.Position = Vector3.Lerp(bp.Position, op.Position, weight);
                    bp.Rotation = KeyframeInterpolation.InterpolateQuaternionSlerp(
                        bp.Rotation, op.Rotation, weight);
                    bp.Scale = Vector3.Lerp(bp.Scale, op.Scale, weight);
                    result.BonePoses[i] = bp;
                }
            }

            return result;
        }

        // ------------------------------------------------------------------
        // Force transitions (for scripting)
        // ------------------------------------------------------------------

        /// <summary>Force a transition to the specified state index, ignoring conditions.</summary>
        public void ForceTransition(int targetStateIndex, float duration = 0.2f)
        {
            if (targetStateIndex < 0 || targetStateIndex >= States.Count) return;
            if (CurrentTransition != null) return; // Already transitioning

            var transition = new AnimTransitionReal(CurrentStateIndex, targetStateIndex, duration)
            {
                Type = duration <= 0f ? TransitionType.Instant : TransitionType.Crossfade,
                BlendCurve = BlendCurveType.Linear
            };

            StartTransition(transition);
        }

        /// <summary>Force a transition to the state with the given name.</summary>
        public void ForceTransition(string stateName, float duration = 0.2f)
        {
            for (int i = 0; i < States.Count; i++)
            {
                if (States[i].Name == stateName)
                {
                    ForceTransition(i, duration);
                    return;
                }
            }
        }

        // ------------------------------------------------------------------
        // Transition queries
        // ------------------------------------------------------------------

        /// <summary>Is the state machine currently transitioning between states?</summary>
        public bool IsTransitioning => CurrentTransition != null;

        /// <summary>Get the current transition progress (0..1), or 0 if not transitioning.</summary>
        public float GetTransitionProgress()
        {
            if (CurrentTransition == null || CurrentTransition.Duration <= 0f)
                return 0f;
            return Math.Clamp(CurrentTransition.ElapsedTime / CurrentTransition.Duration, 0f, 1f);
        }

        /// <summary>Get the current transition alpha (after blend curve evaluation).</summary>
        public float GetTransitionAlpha()
        {
            if (CurrentTransition == null) return 0f;
            return CurrentTransition.EvaluateBlendCurve(GetTransitionProgress());
        }
    }
}
