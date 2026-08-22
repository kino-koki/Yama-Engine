using System;
using System.Collections.Generic;
using System.Numerics;
using ShanyuEngine.Skinning;

namespace ShanyuEngine.Animation
{
    // ============================================================================================
    //  AnimStateMachineNode.cs
    //
    //  状态机动画节点（Animation State Machine Node）。
    //
    //  本系统提供类似 UE5 AnimGraph 中状态机节点的完整实现：
    //    - AnimStateMachineNode        ：状态机节点（AnimNodeBase 子类），在动画图表中作为
    //                                    一个节点存在，内部包含完整的子状态机。
    //    - AnimState                   ：单个状态，包含名称/动画引用/入口退出规则。
    //    - AnimTransition              ：状态转换，带条件委托和混合模式（Normal/Additive/Inertialization）。
    //    - AnimTransitionRuleDelegate  ：转换规则的函数签名（C# 委托）。
    //    - AnimStateHistory            ：状态历史记录，支持跨淡入淡出记忆。
    //    - InertialBlend               ：惯性化过渡实现，记录姿态导数、指数衰减混合。
    // ============================================================================================

    // ──────────────────────────────────────────────────────────────
    //  转换规则委托与混合模式
    // ──────────────────────────────────────────────────────────────

    /// <summary>转换条件的函数委托。接收 AnimInstance 上下文，返回 true 触发转换。</summary>
    public delegate bool AnimTransitionRuleDelegate(AnimInstance instance);

    /// <summary>状态转换的混合模式。</summary>
    public enum AnimTransitionBlendMode
    {
        /// <summary>普通交叉淡入淡出（Lerp/Slerp）。</summary>
        Normal,
        /// <summary>叠加混合（Additive）。</summary>
        Additive,
        /// <summary>惯性化过渡（指数衰减到目标姿态，消除脚部滑动）。</summary>
        Inertialization,
    }

    // ──────────────────────────────────────────────────────────────
    //  状态（AnimState）
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 状态机中的单个状态。每个状态可持有对 AnimNodeBase 子图的引用，
    /// 定义淡入/淡出时间、循环设置等。
    /// </summary>
    public sealed class AnimState
    {
        /// <summary>状态名称。</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>状态内的动画子图（可以是序列播放器、混合空间等）。</summary>
        public AnimNodeBase? Animation { get; set; }

        /// <summary>淡入时间（秒）。</summary>
        public float BlendInTime { get; set; } = 0.25f;

        /// <summary>淡出时间（秒）。</summary>
        public float BlendOutTime { get; set; } = 0.25f;

        /// <summary>是否循环播放。</summary>
        public bool bLoop { get; set; } = true;

        /// <summary>是否自动播放（状态激活时立即开始播放）。</summary>
        public bool bAutoplay { get; set; } = true;

        /// <summary>动画播放速率。</summary>
        public float PlayRate { get; set; } = 1f;

        /// <summary>可选的状态入口规则（额外条件判断）。</summary>
        public Func<AnimInstance, bool>? EntryRule { get; set; }

        /// <summary>可选的状态退出规则。</summary>
        public Func<AnimInstance, bool>? ExitRule { get; set; }

        // 运行时数据
        internal float _currentTime;
        internal float _previousTime;
        internal bool _isPlaying;

        /// <summary>标准化播放时间 [0, 1)。</summary>
        public float NormalizedTime
        {
            get
            {
                float len = CalculateLength();
                return len > 0f ? _currentTime / len : 0f;
            }
        }

        /// <summary>当前播放时间（秒）。</summary>
        public float CurrentTime => _currentTime;

        /// <summary>是否正在播放。</summary>
        public bool IsPlaying => _isPlaying;

        /// <summary>构造函数。</summary>
        public AnimState() { }

        public AnimState(string name, AnimNodeBase? animation = null)
        {
            Name = name;
            Animation = animation;
        }

        /// <summary>计算状态动画的总长度。</summary>
        public float CalculateLength()
        {
            if (Animation == null) return 0f;
            var output = Animation.OutputPose;
            // 通过子图的长度属性获取，这里简化处理
            return 1f; // 实际应从 Animation 类型中获取
        }

        /// <summary>重置播放状态。</summary>
        public void ResetPlayback()
        {
            _currentTime = 0f;
            _previousTime = 0f;
            _isPlaying = bAutoplay;
        }
    }

    // ──────────────────────────────────────────────────────────────
    //  状态转换（AnimTransition）
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 定义从一个状态到另一个状态的转换。包含转换条件（C# 委托）、
    /// 淡入淡出时长和混合模式。
    /// </summary>
    public sealed class AnimTransition
    {
        /// <summary>源状态名称（为空表示 AnyState）。</summary>
        public string FromState { get; set; } = string.Empty;

        /// <summary>目标状态名称。</summary>
        public string ToState { get; set; } = string.Empty;

        /// <summary>转换条件委托。返回 true 时触发转换。</summary>
        public AnimTransitionRuleDelegate? TransitionRule { get; set; }

        /// <summary>交叉淡入淡出时长（秒）。</summary>
        public float CrossfadeDuration { get; set; } = 0.25f;

        /// <summary>混合模式。</summary>
        public AnimTransitionBlendMode BlendMode { get; set; } = AnimTransitionBlendMode.Normal;

        /// <summary>是否可中断（允许其他转换打断此转换）。</summary>
        public bool bInterruptionEnabled { get; set; }

        /// <summary>目标状态的起始偏移（标准化时间 [0,1]）。</summary>
        public float DestinationOffset { get; set; }

        public AnimTransition() { }

        public AnimTransition(string fromState, string toState, AnimTransitionRuleDelegate? rule = null)
        {
            FromState = fromState;
            ToState = toState;
            TransitionRule = rule;
        }
    }

    // ──────────────────────────────────────────────────────────────
    //  状态历史记录
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 状态历史记录。保存之前访问的状态信息，支持跨淡入淡出的状态记忆。
    /// 可用于实现"返回到上一个状态"等逻辑。
    /// </summary>
    public sealed class AnimStateHistory
    {
        /// <summary>历史条目。</summary>
        private readonly struct HistoryEntry
        {
            public readonly string StateName;
            public readonly float TimeSeconds;

            public HistoryEntry(string name, float time)
            {
                StateName = name;
                TimeSeconds = time;
            }
        }

        private readonly LinkedList<HistoryEntry> _entries = new();
        private int _maxEntries = 16;

        /// <summary>最大历史记录数。</summary>
        public int MaxEntries
        {
            get => _maxEntries;
            set => _maxEntries = Math.Max(1, value);
        }

        /// <summary>当前历史条目数。</summary>
        public int Count => _entries.Count;

        /// <summary>记录一个状态访问。</summary>
        public void Record(string stateName, float timeSeconds)
        {
            _entries.AddFirst(new HistoryEntry(stateName, timeSeconds));
            while (_entries.Count > _maxEntries)
                _entries.RemoveLast();
        }

        /// <summary>获取上一个状态名称。</summary>
        public string? GetPreviousState(int offset = 1)
        {
            int i = 0;
            foreach (var entry in _entries)
            {
                if (i == offset) return entry.StateName;
                i++;
            }
            return null;
        }

        /// <summary>清除历史。</summary>
        public void Clear() => _entries.Clear();
    }

    // ──────────────────────────────────────────────────────────────
    //  惯性化混合（InertialBlend）
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 惯性化过渡混合器。在状态转换时记录源姿态相对于目标姿态的偏移，
    /// 然后用弹簧-阻尼模型将偏移指数衰减到零，产生平滑的惯性过渡效果。
    /// 相比普通交叉淡入淡出，惯性化能更好地保留运动动量，消除脚部滑动。
    /// </summary>
    public sealed class InertialBlend
    {
        private readonly Vector3[] _translationOffsets;
        private readonly Quaternion[] _rotationOffsets;
        private readonly Vector3[] _translationVelocities;
        private readonly Vector3[] _rotationVelocities;

        /// <summary>过渡持续时间（秒）。</summary>
        public float Duration { get; set; } = 0.15f;

        /// <summary>阻尼比（1.0 = 临界阻尼）。</summary>
        public float DampingRatio { get; set; } = 1.0f;

        private bool _hasOffset;
        private float _elapsed;

        /// <summary>是否正在进行惯性化过渡。</summary>
        public bool IsActive => _hasOffset && _elapsed < Duration;

        /// <summary>当前进度 [0,1]。</summary>
        public float Progress => Duration > 0f ? Math.Clamp(_elapsed / Duration, 0f, 1f) : 1f;

        public InertialBlend(int boneCount)
        {
            _translationOffsets = new Vector3[boneCount];
            _rotationOffsets = new Quaternion[boneCount];
            _translationVelocities = new Vector3[boneCount];
            _rotationVelocities = new Vector3[boneCount];
            for (int i = 0; i < boneCount; i++)
            {
                _rotationOffsets[i] = Quaternion.Identity;
            }
        }

        /// <summary>在转换发生时调用，记录从源姿态到目标姿态的偏移。</summary>
        public void CaptureTransition(Skeleton skeleton, AnimPoseOutput fromPose, AnimPoseOutput toPose)
        {
            int count = Math.Min(skeleton.Bones.Count, _translationOffsets.Length);
            for (int i = 0; i < count; i++)
            {
                _translationOffsets[i] = fromPose.Translations[i] - toPose.Translations[i];
                _translationVelocities[i] = Vector3.Zero;

                var oldRot = fromPose.Rotations[i];
                var newRot = toPose.Rotations[i];
                var delta = Quaternion.Inverse(newRot) * oldRot;
                if (Quaternion.Dot(delta, Quaternion.Identity) < 0f) delta = -delta;
                _rotationOffsets[i] = delta;
                _rotationVelocities[i] = Vector3.Zero;
            }
            _hasOffset = true;
            _elapsed = 0f;
        }

        /// <summary>每帧更新，将惯性化偏移应用到目标姿态。</summary>
        public void Apply(Skeleton skeleton, AnimPoseOutput targetPose, float deltaTime)
        {
            if (!_hasOffset || deltaTime <= 0f) return;
            _elapsed += deltaTime;
            if (_elapsed >= Duration)
            {
                _hasOffset = false;
                return;
            }

            int count = Math.Min(skeleton.Bones.Count, _translationOffsets.Length);
            float omega = Duration > 0.0001f ? 1f / Duration * 4f : 40f;
            float alpha = omega * omega;
            float beta = 2f * DampingRatio * omega;

            for (int i = 0; i < count; i++)
            {
                // 平移：弹簧-阻尼积分
                var tOff = _translationOffsets[i];
                var tVel = _translationVelocities[i];
                tVel += (-alpha * tOff - beta * tVel) * deltaTime;
                tOff += tVel * deltaTime;
                _translationOffsets[i] = tOff;
                _translationVelocities[i] = tVel;
                targetPose.Translations[i] += tOff;

                // 旋转：转为旋转向量做弹簧-阻尼
                var rOff = _rotationOffsets[i];
                var (axis, angle) = QuaternionToAxisAngle(rOff);
                var rVec = axis * angle;
                var rVel = _rotationVelocities[i];
                rVel += (-alpha * rVec - beta * rVel) * deltaTime;
                rVec += rVel * deltaTime;
                _rotationVelocities[i] = rVel;

                float rLen = rVec.Length();
                if (rLen > 1e-6f)
                {
                    var applied = Quaternion.CreateFromAxisAngle(rVec / rLen, rLen);
                    targetPose.Rotations[i] = Quaternion.Normalize(applied * targetPose.Rotations[i]);
                    _rotationOffsets[i] = applied;
                }
                else
                {
                    _rotationOffsets[i] = Quaternion.Identity;
                }
            }
        }

        /// <summary>重置所有偏移。</summary>
        public void Reset()
        {
            for (int i = 0; i < _translationOffsets.Length; i++)
            {
                _translationOffsets[i] = Vector3.Zero;
                _rotationOffsets[i] = Quaternion.Identity;
                _translationVelocities[i] = Vector3.Zero;
                _rotationVelocities[i] = Vector3.Zero;
            }
            _hasOffset = false;
            _elapsed = 0f;
        }

        private static (Vector3 axis, float angle) QuaternionToAxisAngle(Quaternion q)
        {
            q = Quaternion.Normalize(q);
            float w = Math.Clamp(q.W, -1f, 1f);
            float angle = 2f * MathF.Acos(w);
            float s = MathF.Sqrt(MathF.Max(0f, 1f - w * w));
            if (s < 1e-6f) return (Vector3.UnitX, 0f);
            return (new Vector3(q.X, q.Y, q.Z) / s, angle);
        }
    }

    // ──────────────────────────────────────────────────────────────
    //  状态机动画节点（AnimStateMachineNode）
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 状态机动画节点。是 AnimNodeBase 的子类，可在动画图表中作为节点存在。
    /// 内部管理一组 AnimState 和 AnimTransition，运行独立的子状态机。
    /// 相当于 UE5 AnimGraph 中的状态机节点。
    ///
    /// 特性：
    ///   - 多状态 + 转换规则（C# 委托条件）
    ///   - AnyState 转换（从任意状态出发）
    ///   - 混合模式：Normal / Additive / Inertialization
    ///   - 状态历史记录
    ///   - LOD 阈值控制
    /// </summary>
    public sealed class AnimStateMachineNode : AnimNodeBase
    {
        /// <summary>状态列表。</summary>
        public List<AnimState> States { get; } = new();

        /// <summary>状态间转换列表。</summary>
        public List<AnimTransition> Transitions { get; } = new();

        /// <summary>AnyState 转换（从任意状态出发，但不包含当前状态）。</summary>
        public List<AnimTransition> AnyStateTransitions { get; } = new();

        /// <summary>默认状态名称（状态机激活时进入此状态）。</summary>
        public string DefaultState { get; set; } = string.Empty;

        /// <summary>状态历史记录。</summary>
        public AnimStateHistory History { get; } = new();

        // 运行时状态
        private int _currentStateIndex = -1;
        private int _previousStateIndex = -1;
        private int _targetStateIndex = -1;
        private bool _isTransitioning;
        private float _transitionElapsed;
        private float _transitionDuration;
        private AnimTransitionBlendMode _currentBlendMode;
        private AnimTransition? _activeTransition;
        private InertialBlend? _inertialBlend;
        private int _skeletonBoneCount;

        /// <summary>当前状态名称。</summary>
        public string CurrentStateName => GetStateName(_currentStateIndex);

        /// <summary>上一状态名称。</summary>
        public string PreviousStateName => GetStateName(_previousStateIndex);

        /// <summary>是否正在转换。</summary>
        public bool IsTransitioning => _isTransitioning;

        /// <summary>转换进度 [0, 1]。</summary>
        public float TransitionProgress => _isTransitioning && _transitionDuration > 0f
            ? Math.Clamp(_transitionElapsed / _transitionDuration, 0f, 1f)
            : 1f;

        /// <summary>活动转换。</summary>
        public AnimTransition? ActiveTransition => _activeTransition;

        /// <summary>当前状态对象。</summary>
        public AnimState? CurrentState => GetState(_currentStateIndex);

        /// <summary>上一状态对象。</summary>
        public AnimState? PreviousState => GetState(_previousStateIndex);

        public AnimStateMachineNode()
        {
            NodeName = "StateMachine";
        }

        // ──────────────────────────────────────────────
        //  状态/转换管理 API
        // ──────────────────────────────────────────────

        /// <summary>添加状态。</summary>
        public AnimState AddState(string name, AnimNodeBase? animation = null)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("状态名称不能为空。", nameof(name));
            var state = new AnimState(name, animation);
            States.Add(state);
            if (string.IsNullOrEmpty(DefaultState))
                DefaultState = name;
            return state;
        }

        /// <summary>添加状态转换。</summary>
        public AnimTransition AddTransition(string fromState, string toState, AnimTransitionRuleDelegate? rule = null, float crossfade = 0.25f)
        {
            var t = new AnimTransition(fromState, toState, rule)
            {
                CrossfadeDuration = crossfade,
            };
            Transitions.Add(t);
            return t;
        }

        /// <summary>添加 AnyState 转换。</summary>
        public AnimTransition AddAnyStateTransition(string toState, AnimTransitionRuleDelegate? rule = null, float crossfade = 0.25f)
        {
            var t = new AnimTransition(string.Empty, toState, rule)
            {
                CrossfadeDuration = crossfade,
            };
            AnyStateTransitions.Add(t);
            return t;
        }

        /// <summary>获取指定名称的状态索引。</summary>
        public int ResolveStateIndex(string name)
        {
            for (int i = 0; i < States.Count; i++)
                if (States[i].Name == name) return i;
            return -1;
        }

        /// <summary>跳转到指定状态。</summary>
        public void Play(string stateName, float time = 0f)
        {
            int idx = ResolveStateIndex(stateName);
            if (idx < 0) return;

            // 记录历史
            if (_currentStateIndex >= 0)
                History.Record(States[_currentStateIndex].Name, States[_currentStateIndex]._currentTime);

            _previousStateIndex = _currentStateIndex;
            _currentStateIndex = idx;
            _isTransitioning = false;
            _targetStateIndex = -1;
            _activeTransition = null;

            var state = States[idx];
            state._currentTime = time;
            state._previousTime = time;
            state._isPlaying = state.bAutoplay;
        }

        // ──────────────────────────────────────────────
        //  节点生命周期
        // ──────────────────────────────────────────────

        public override void OnInitialize(Skeleton skeleton)
        {
            _skeletonBoneCount = skeleton.Bones.Count;
            foreach (var state in States)
                state.Animation?.OnInitialize(skeleton);

            // 初始化默认状态
            if (_currentStateIndex < 0 && !string.IsNullOrEmpty(DefaultState))
            {
                int idx = ResolveStateIndex(DefaultState);
                if (idx >= 0)
                {
                    _currentStateIndex = idx;
                    States[idx]._isPlaying = States[idx].bAutoplay;
                }
                else if (States.Count > 0)
                {
                    _currentStateIndex = 0;
                    States[0]._isPlaying = States[0].bAutoplay;
                }
            }

            // 预分配惯性化混合器
            _inertialBlend = new InertialBlend(_skeletonBoneCount);
        }

        public override void OnUpdate(float deltaTime)
        {
            if (!LODPass || States.Count == 0) return;

            // 如果不在转换中，检查是否需要开始转换
            if (!_isTransitioning && _currentStateIndex >= 0)
            {
                var currentState = States[_currentStateIndex];
                var transition = FindTransition(currentState);
                if (transition != null)
                {
                    StartTransition(transition);
                }
            }

            // 推进当前状态
            if (_currentStateIndex >= 0)
            {
                var current = States[_currentStateIndex];
                current._previousTime = current._currentTime;
                current._currentTime += deltaTime * current.PlayRate;
            }

            // 推进目标状态
            if (_isTransitioning && _targetStateIndex >= 0)
            {
                var target = States[_targetStateIndex];
                target._previousTime = target._currentTime;
                target._currentTime += deltaTime * target.PlayRate;
                _transitionElapsed += deltaTime;

                // 如果转换完成，切换到目标状态
                if (_transitionElapsed >= _transitionDuration)
                {
                    CompleteTransition();
                }
            }
        }

        public override void OnEvaluate(AnimPoseOutput output)
        {
            if (!LODPass || States.Count == 0 || _currentStateIndex < 0)
            {
                // 没有状态或 LOD 未通过：保持绑定姿态
                for (int i = 0; i < output.BoneCount; i++)
                {
                    output.Translations[i] = Vector3.Zero;
                    output.Rotations[i] = Quaternion.Identity;
                    output.Scales[i] = Vector3.One;
                }
                return;
            }

            var currentState = States[_currentStateIndex];

            if (!_isTransitioning || _targetStateIndex < 0)
            {
                // 没有转换：直接评估当前状态
                if (currentState.Animation != null)
                {
                    currentState.Animation.OnEvaluate(output);
                }
                else
                {
                    // 没有动画：输出绑定姿态
                    for (int i = 0; i < output.BoneCount; i++)
                    {
                        output.Translations[i] = Vector3.Zero;
                        output.Rotations[i] = Quaternion.Identity;
                        output.Scales[i] = Vector3.One;
                    }
                }
                return;
            }

            // 转换中：混合当前状态和目标状态
            var targetState = States[_targetStateIndex];
            float blendWeight = _transitionDuration > 0f
                ? Math.Clamp(_transitionElapsed / _transitionDuration, 0f, 1f)
                : 1f;

            // 评估当前状态姿态
            var poseA = AnimPoseOutput.Create(output.BoneCount);
            if (currentState.Animation != null)
            {
                currentState.Animation.OnEvaluate(poseA);
            }

            // 评估目标状态姿态
            var poseB = AnimPoseOutput.Create(output.BoneCount);
            if (targetState.Animation != null)
            {
                targetState.Animation.OnEvaluate(poseB);
            }

            switch (_currentBlendMode)
            {
                case AnimTransitionBlendMode.Normal:
                    BlendNormal(poseA, poseB, blendWeight, output);
                    break;

                case AnimTransitionBlendMode.Additive:
                    BlendAdditive(poseA, poseB, blendWeight, output);
                    break;

                case AnimTransitionBlendMode.Inertialization:
                    // 第一个更新帧捕获偏移，之后应用惯性化
                    if (_inertialBlend != null)
                    {
                        if (_inertialBlend.Progress <= 0f && blendWeight < 0.5f)
                        {
                            // 初始帧：捕获从源到目标的偏移
                            _inertialBlend.CaptureTransition(null, poseA, poseB);
                        }
                        // 输出 = 目标姿态 + 惯性化偏移
                        output.CopyFrom(poseB);
                        _inertialBlend.Apply(null, output, 0.016f);
                    }
                    else
                    {
                        BlendNormal(poseA, poseB, blendWeight, output);
                    }
                    break;
            }
        }

        public override void OnReset()
        {
            _currentStateIndex = -1;
            _previousStateIndex = -1;
            _targetStateIndex = -1;
            _isTransitioning = false;
            _transitionElapsed = 0f;
            _activeTransition = null;
            _inertialBlend?.Reset();

            foreach (var state in States)
            {
                state.ResetPlayback();
                state.Animation?.OnReset();
            }

            // 重置到默认状态
            if (!string.IsNullOrEmpty(DefaultState))
            {
                int idx = ResolveStateIndex(DefaultState);
                if (idx >= 0)
                {
                    _currentStateIndex = idx;
                    States[idx]._isPlaying = States[idx].bAutoplay;
                }
            }
        }

        // ──────────────────────────────────────────────
        //  内部方法
        // ──────────────────────────────────────────────

        private AnimTransition? FindTransition(AnimState state)
        {
            float normTime = state.NormalizedTime;

            // 检查 AnyState 转换（优先级最高）
            foreach (var t in AnyStateTransitions)
            {
                if (string.IsNullOrEmpty(t.ToState)) continue;
                // 不转换到自身
                int toIdx = ResolveStateIndex(t.ToState);
                if (toIdx < 0 || toIdx == _currentStateIndex) continue;
                if (t.TransitionRule != null && t.TransitionRule.Invoke(null))
                    return t;
                // 如果没有规则但设置了自动转换
                if (t.TransitionRule == null)
                    return t;
            }

            // 检查当前状态的出转换
            foreach (var t in Transitions)
            {
                if (t.FromState != state.Name) continue;
                if (string.IsNullOrEmpty(t.ToState)) continue;
                int toIdx = ResolveStateIndex(t.ToState);
                if (toIdx < 0 || toIdx == _currentStateIndex) continue;
                if (t.TransitionRule != null && t.TransitionRule.Invoke(null))
                    return t;
                // 如果没有规则，需要退出时间条件
                if (t.TransitionRule == null)
                {
                    // 默认在结束时触发（标准化时间 >= 1.0 且循环状态）
                    if (state.bLoop && normTime >= 1f)
                        return t;
                }
            }

            return null;
        }

        private void StartTransition(AnimTransition transition)
        {
            int toIdx = ResolveStateIndex(transition.ToState);
            if (toIdx < 0) return;

            // 如果已经在转换中且不可中断，不处理
            if (_isTransitioning && (_activeTransition == null || !_activeTransition.bInterruptionEnabled))
                return;

            // 记录当前状态到历史
            if (_currentStateIndex >= 0)
                History.Record(States[_currentStateIndex].Name, States[_currentStateIndex]._currentTime);

            _previousStateIndex = _currentStateIndex;
            _targetStateIndex = toIdx;
            _isTransitioning = true;
            _transitionElapsed = 0f;
            _transitionDuration = Math.Max(0.0001f, transition.CrossfadeDuration);
            _currentBlendMode = transition.BlendMode;
            _activeTransition = transition;

            // 初始化目标状态
            var targetState = States[toIdx];
            targetState.ResetPlayback();
            if (transition.DestinationOffset > 0f)
            {
                float len = targetState.CalculateLength();
                targetState._currentTime = transition.DestinationOffset * len;
                targetState._previousTime = targetState._currentTime;
            }
        }

        private void CompleteTransition()
        {
            if (_targetStateIndex >= 0)
            {
                _previousStateIndex = _currentStateIndex;
                _currentStateIndex = _targetStateIndex;
                _targetStateIndex = -1;
            }
            _isTransitioning = false;
            _activeTransition = null;
        }

        private string GetStateName(int index) => index >= 0 && index < States.Count ? States[index].Name : string.Empty;

        private AnimState? GetState(int index) => index >= 0 && index < States.Count ? States[index] : null;

        // ──────────────────────────────────────────────
        //  混合方法
        // ──────────────────────────────────────────────

        private static void BlendNormal(AnimPoseOutput a, AnimPoseOutput b, float weight, AnimPoseOutput output)
        {
            float w = Math.Clamp(weight, 0f, 1f);
            int count = Math.Min(a.BoneCount, Math.Min(b.BoneCount, output.BoneCount));
            for (int i = 0; i < count; i++)
            {
                output.Translations[i] = Vector3.Lerp(a.Translations[i], b.Translations[i], w);
                output.Scales[i] = Vector3.Lerp(a.Scales[i], b.Scales[i], w);
                var ra = a.Rotations[i];
                var rb = b.Rotations[i];
                if (Quaternion.Dot(ra, rb) < 0f) rb = -rb;
                output.Rotations[i] = Quaternion.Normalize(Quaternion.Slerp(ra, rb, w));
            }

            // 混合曲线
            foreach (var kv in a.Curves)
                output.Curves[kv.Key] = kv.Value * (1f - w);
            foreach (var kv in b.Curves)
            {
                if (output.Curves.TryGetValue(kv.Key, out float existing))
                    output.Curves[kv.Key] = existing + kv.Value * w;
                else
                    output.Curves[kv.Key] = kv.Value * w;
            }
        }

        private static void BlendAdditive(AnimPoseOutput basePose, AnimPoseOutput additive, float weight, AnimPoseOutput output)
        {
            float w = Math.Clamp(weight, 0f, 1f);
            int count = Math.Min(basePose.BoneCount, Math.Min(additive.BoneCount, output.BoneCount));
            for (int i = 0; i < count; i++)
            {
                output.Translations[i] = basePose.Translations[i] + additive.Translations[i] * w;
                output.Scales[i] = basePose.Scales[i] * Vector3.Lerp(Vector3.One, additive.Scales[i], w);
                var baseRot = basePose.Rotations[i];
                // 叠加旋转：将 additive 旋转视为 delta
                var addRot = Quaternion.Slerp(Quaternion.Identity, additive.Rotations[i], w);
                output.Rotations[i] = Quaternion.Normalize(baseRot * addRot);
            }
        }
    }

    // ──────────────────────────────────────────────────────────────
    //  AnimPoseOutput 扩展
    // ──────────────────────────────────────────────────────────────

    internal static class AnimPoseOutputExtensions
    {
        public static void CopyFrom(this AnimPoseOutput target, AnimPoseOutput source)
        {
            int count = Math.Min(target.BoneCount, source.BoneCount);
            for (int i = 0; i < count; i++)
            {
                target.Translations[i] = source.Translations[i];
                target.Rotations[i] = source.Rotations[i];
                target.Scales[i] = source.Scales[i];
            }
            target.RootMotionDeltaTranslation = source.RootMotionDeltaTranslation;
            target.RootMotionDeltaRotation = source.RootMotionDeltaRotation;
            foreach (var kv in source.Curves)
                target.Curves[kv.Key] = kv.Value;
        }
    }
}
